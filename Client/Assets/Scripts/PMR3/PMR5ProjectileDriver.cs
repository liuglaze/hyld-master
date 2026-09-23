// R5-B2b：投射物的**会话级网络驱动**（契约 Docs/plans/net-r5-network-contract.md「B2b网络driver」）。
//
// 职责（一句话）：把 World 预留 / 声明层三条投射物 RPC / PMProjectile 核心 codec 与
// Coordinator 接成一条**每个会话一份**的驱动，并按 PMR3Player 逐个挂上声明回调接缝。
//
//   角色 Authority        → DS：可信 policy 授权的生成预留 → 确认后 SpawnReserved 上线 →
//                               权威运动 + snapshot 复制 → 命中上报走 codec 验证 → 裁决下行
//   角色 AutonomousProxy  → AP：TryFire 本地假弹 + 生成 RPC 上行；裁决下行 → 接管/撤销假弹；
//                               权威镜像经声明复制收敛（假弹存活时位置闸门）
//   角色 SimulatedProxy   → SP：纯镜像消费（不预测、不收上行命中）
//
// 铁律（逐条对应契约原文，实现里可直接找到落点）：
//   1) **身份只从认证源取**：DS 侧 owner 取 `player.NetId.Value`，并要求 owner 连接 ready；
//      codec 里自报的 owner/origin 只作为「必须与认证身份一致」的断言，**绝不被采信**。
//      可信 spec / 玩家位置 / 激活裁决一律来自注入的 `IPMR5ProjectileAuthorityPolicy`。
//   2) **先预留、后上线**：DS 先 `PMNetWorld.TryReserveNetId` 拿真实 NetId，
//      `RequestSpawn` 用它当 authorityNetId；拒绝 / TTL / owner 离开 / Dispose 一律 `CancelReservedNetId`。
//      只有 SpawnReady 才「写好初始 snapshot → SpawnReserved → RegisterReplicatedObject」。
//   3) **权威注册先于追赶**（R5-B2c 返工）：SpawnReady 时先 `CatchUpRegisteredMotion`
//      （预算 = prediction/2 + 挂起时长，由整合器自记账），**再**编码初始 snapshot；
//      否则线上初值就是未追赶的枪口位置，客户端接管瞬间就会回退。
//   4) **接管只在镜像真实存在时一次完成**（R5-B2c 返工）：Confirmed 先到不得提前接管；
//      接管沿用假弹自己的运动时基，在途旧 snapshot 被**高水位**挡下（位置不回退），
//      但停止/隐藏是状态转移、不受该闸门限制。权威对象被 Destroy 后本地 key **退休**（不复活）。
//   5) **持续状态只走生成复制**：状态变化一律 `PublishProjectileSnapshot`（生成 setter 标脏），
//      驱动**不新造 socket、不手写 MainPack、不手工广播**。
//   6) **出口唯一且显式失败**：三条生成 RPC 的发送失败在本批被放宽为抛异常（PMR3Runtime.SendRemoteRpc，
//      按 **(ClassId, RpcId)** 判定），驱动捕获后设置 `IsFaulted` / `FaultReason` / `FaultError`；
//      输出队列溢出 / Coordinator faulted 同样是会话级 fault，绝不「只 log 仍继续」。
//   7) **网络回调只入有界队列**：声明回调（RPC / RepNotify / 生命周期事件）只入队，
//      真正解码与应用在主线程 `Pump` 里；每次 Pump 的入站、运动、状态发布、Drain 全部有界。
//      本地账（`_fakes` / `_mirrors` / 上行 Verify 账 / 视图）随 key 退休回落，不留到对局结束。
//   8) **不扣血**：`DrainSettlements` 是未来 R6 的**唯一**消费点；没有订阅者时结算进**有界待取缓冲**
//      （缓冲满则会话 fault），绝不静默丢掉真实伤害。
//   9) **已受理 key 无副作用幂等**（返工收口）：可靠上行重发同一颗弹时，在 codec + 身份断言之后、
//      policy/预留/RequestSpawn **之前**用**自己的登记事实**（预留 / 权威对象 / 生命周期登记记录 / 终态环）
//      吸收重复与迟到终态请求：不重复扣授权额度、不重复预留 NetId、不重复登记、**不补发 Rejected**
//      （那会反向撤销已 Confirmed 的合法假弹）。冲突重复只计数（`RejectedDuplicateConflict`），
//      不改原对象/原决策；不新增无界 fingerprint 缓存（见 `HandleServerSpawn` 的「已受理 key」段）。
//
// 语言面：纯 C#（C# 7.3 / netstandard2.0），零 UnityEngine 依赖；墙钟由宿主以参数传入。
//
// 线程纪律：构造时记录线程，之后跨线程调用 fail-fast（复制应用 / RPC 收发 / 运动都在主线程）。
//
// ⚠ 本批**未**接线真实 Unity 宿主（PMClientSessionHost / PMDsSessionHost 未创建本驱动），
//    T45 实机仍 PENDING_USER。

using System;
using System.Collections.Generic;
using PMNet.Mover;
using PMNet.Projectile;
using PMNet.Session;

namespace PMNet.R3
{
    /// <summary>
    /// 投射物驱动的会话级故障原因。
    ///
    /// 语义：**一次故障 = 本 session 报销**。故障后驱动拒绝一切推进与发送
    /// （`Pump` 早退、`TryFire`/`SubmitPredictedHits` 返回 false + error），宿主必须记录并终止。
    /// 这样「发送失败 / 队列溢出 / Coordinator faulted / 身份被拒」永远不会被当成成功继续跑。
    /// </summary>
    public enum PMR5ProjectileFaultReason : byte
    {
        None = 0,

        /// <summary>三条投射物生成 RPC 的发送失败（未接线 / 未就绪 / 桥拒绝 / 编码超限）。</summary>
        SendFailed = 1,

        /// <summary>世界未接线（`PMR3Runtime.Attach` 未做），发送必然失败。</summary>
        WorldNotAttached = 2,

        /// <summary>owner 连接不存在或 !IsReady。</summary>
        OwnerNotReady = 3,

        /// <summary>PMProjectileCoordinator 进入永久 Faulted（出口溢出 / 内部不变式破坏）。</summary>
        CoordinatorFaulted = 4,

        /// <summary>驱动自有输出/入站队列溢出。</summary>
        OutputOverflow = 5,

        /// <summary>内部不变式被破坏（例如 SpawnReady 找不到对应预留令牌）。</summary>
        InternalInvariant = 6,

        /// <summary>墙钟倒退（Pump 的 wallNowMs 必须单调）。</summary>
        NonMonotonicWallClock = 7,

        /// <summary>DS 侧没有注入 `IPMR5ProjectileAuthorityPolicy`（契约：DS 必须有）。</summary>
        PolicyMissing = 8,

        /// <summary>跨线程调用。</summary>
        ThreadViolation = 9,

        /// <summary>驱动已释放后仍被使用。</summary>
        Disposed = 10,

        /// <summary>
        /// 结算出口**没有消费者**且驱动侧的有界待取缓冲也满了：
        /// 再不 fault 就等于静默丢掉真实伤害（D-R0 的「绝不丢已接受出口」）。
        /// </summary>
        SettlementUnconsumed = 11,
    }

    /// <summary>
    /// DS 侧**可信**投射物授权策略（契约：定义在本文件，DS 必须有）。
    ///
    /// 它是上行 intent 与权威世界之间**唯一**的可信桥：
    ///   · `trustedSpec`   —— 权威配置（速度 / 半径 / 寿命 / StopOnHit / HideOnStop / DelayDestroy）。
    ///                        上行 payload **绝不能**携带 spec，因此这里必须现给出。
    ///   · `ownerPosition` —— 权威侧认为的 owner 当前位置（用于 10m 枪口判定）。
    ///   · `verdict`       —— 本次激活的权威裁决（Pending / Confirmed / Rejected）。
    ///                        返回 Pending 时驱动只做预留 + 挂起，后续由宿主显式调
    ///                        `ResolveActivation` 兑现（契约：Pending 按权威 ResolveActivation 显式后续入口）。
    ///
    /// 返回 false 表示「本次请求不予授权」：驱动会取消预留并向 owner 下行一条 Rejected 裁决
    /// （让客户端真正撤掉假弹），**不进任何队列**。
    ///
    /// 线程：与驱动同线程（主线程）；实现不得回调驱动。
    /// </summary>
    public interface IPMR5ProjectileAuthorityPolicy
    {
        /// <summary>
        /// 授权一次投射物生成。
        /// </summary>
        /// <param name="player">上行来源副本（其 `NetId` 就是认证 owner，**不是** payload 自报值）。</param>
        /// <param name="intent">已过 codec 解码与身份断言的上行请求 DTO（只含非权威字段）。</param>
        /// <param name="trustedSpec">权威配置（返回 true 时必须非 null 且数值合法）。</param>
        /// <param name="ownerPosition">权威侧 owner 当前位置（米）。</param>
        /// <param name="verdict">权威激活裁决：Pending / Confirmed / Rejected。</param>
        /// <param name="error">失败原因（返回 false 时给出，供日志/诊断）。</param>
        bool TryAuthorizeSpawn(
            PMR3Player player,
            PMProjectileSpawnIntent intent,
            out PMProjectileSpec trustedSpec,
            out PMVector3 ownerPosition,
            out PMActivationResult verdict,
            out string error);
    }

    /// <summary>
    /// 给 C（Unity 表现层）消费的**只读视图**。核心只经由本结构暴露状态，**不引用 UnityEngine**。
    ///
    /// `LocalFake` = 该视图当前由本地预测假弹驱动（AP 自己的弹，权威镜像尚未接管）；
    /// `TakenOver` = 已由权威接管（此后的位置来自权威镜像）；
    /// `Hidden`    = 表现应隐藏（Rejected 撤销 / 假弹已结束的迟到镜像 / 权威 HideOnStop）。
    /// </summary>
    public struct PMR5ProjectileView
    {
        public PMProjectileKey Key;
        public uint OwnerNetId;
        public uint AuthorityNetId;
        public uint MirrorObjectNetId;
        public PMVector3 Position;
        public PMVector3 PreviousPosition;
        public PMVector3 Velocity;
        public float Yaw;
        public float RadiusM;
        public double MoveTimeMs;
        public bool Hidden;
        public bool Stopped;
        public bool LocalFake;
        public bool TakenOver;
    }

    /// <summary>
    /// 会话级投射物网络驱动。
    ///
    /// 一个会话（world + bridge + epoch）一份；通过 <see cref="BindPlayer"/> 把
    /// **每个真实的 `PMR3Player`** 的声明回调接到本实例上（回调携带 player 身份）。
    /// </summary>
    public sealed class PMR5ProjectileDriver : IDisposable
    {
        // ================================================================ 冻结上限

        /// <summary>命中候选分包上限（契约：上行命中分 ≤48 目标一条 RPC）。</summary>
        public const int MaxTargetsPerHitRpc = 48;

        /// <summary>每 key 的上行 Verify 总配额（契约：每条 RPC 占 1 次，不重置、不超过）。</summary>
        public const int MaxVerifyPerKey = PMProjectileLimits.MaxVerifyCalls;

        /// <summary>入站上行生成队列上限。</summary>
        public const int MaxInboundSpawns = 256;

        /// <summary>入站上行命中队列上限。</summary>
        public const int MaxInboundHits = 256;

        /// <summary>入站下行裁决队列上限。</summary>
        public const int MaxInboundDecisions = 512;

        /// <summary>入站镜像载荷队列上限（当前状态语义：满了丢最旧保最新）。</summary>
        public const int MaxInboundMirrors = 128;

        /// <summary>未绑定 player 的镜像暂存上限（契约：不能永久丢掉初始状态）。</summary>
        public const int MaxUnboundMirrors = 128;

        /// <summary>未绑定 player 的镜像暂存 TTL（毫秒）。</summary>
        public const int UnboundMirrorTtlMs = 5000;

        /// <summary>
        /// 无消费者时结算的**有界待取缓冲**上限（R5-B2c 返工）。
        ///
        /// 为什么不是「无订阅就直接丢」：结算就是伤害，丢一条就是少扣一次血。
        /// 没有订阅者时进入本缓冲等待宿主 Drain（R6 接入前的唯一合规出口）；
        /// 缓冲也满 ⇒ 会话级 fault（<see cref="PMR5ProjectileFaultReason.SettlementUnconsumed"/>），
        /// 绝不静默丢弃。
        /// </summary>
        public const int MaxPendingSettlements = 512;

        /// <summary>视图变更（脏）集合上限。</summary>
        public const int MaxDirtyViews = 4096;

        /// <summary>每次 Pump 单队列最多处理的条数（所有批次有界）。</summary>
        public const int MaxApplyPerPump = 64;

        /// <summary>
        /// 同 key 重复上行的**枪口位置漂移**判定容差（米）：超过即视为「冲突重复」。
        /// 只用于计数与告警，不改变任何已建对象/已作决策。
        /// </summary>
        private const float DuplicateMuzzleToleranceM = 0.05f;

        /// <summary>冲突重复上行的限流告警条数上限（防被重复上行刷日志）。</summary>
        private const long MaxDuplicateConflictWarnings = 8L;

        // ================================================================ 依赖

        private readonly PMNetWorld _world;
        private readonly PMNetSessionBridge _bridge;
        private readonly uint _epoch;
        private readonly PMProjectileHistory _history;
        private readonly IPMR5ProjectileAuthorityPolicy _policy;
        private readonly IPMProjectileHostMotion _hostMotion;
        private readonly PMProjectileCoordinator _coordinator;
        private readonly int _ownerThreadId;
        private readonly bool _isServer;

        // ================================================================ 玩家接线

        private sealed class PlayerAdapter : IPMProjectileNetworkDriver
        {
            private readonly PMR5ProjectileDriver _driver;
            private readonly PMR3Player _player;

            public PlayerAdapter(PMR5ProjectileDriver driver, PMR3Player player)
            {
                _driver = driver;
                _player = player;
            }

            public PMR3Player Player { get { return _player; } }

            public void OnServerSpawnPayload(byte[] payload) { _driver.EnqueueServerSpawn(_player, payload); }
            public void OnServerHitPayload(byte[] payload) { _driver.EnqueueServerHit(_player, payload); }
            public void OnClientDecisionPayload(byte[] payload) { _driver.EnqueueClientDecision(_player, payload); }
        }

        private readonly Dictionary<PMR3Player, PlayerAdapter> _adapters =
            new Dictionary<PMR3Player, PlayerAdapter>();

        private readonly Dictionary<uint, PMR3Player> _playersByOwnerNetId = new Dictionary<uint, PMR3Player>();

        // ================================================================ 入站队列（只入队，Pump 应用）

        private struct InboundSpawn
        {
            public PMR3Player Player;
            public byte[] Payload;
        }

        private struct InboundHit
        {
            public PMR3Player Player;
            public byte[] Payload;
        }

        private struct InboundDecision
        {
            public PMR3Player Player;
            public byte[] Payload;
        }

        private readonly List<InboundSpawn> _inboundSpawns = new List<InboundSpawn>(8);
        private readonly List<InboundHit> _inboundHits = new List<InboundHit>(8);
        private readonly List<InboundDecision> _inboundDecisions = new List<InboundDecision>(8);

        private struct MirrorWake
        {
            public PMR5Projectile Object;
            public bool Destroyed;
            public PMObjectDestroyReason Reason;
        }

        private readonly List<MirrorWake> _inboundMirrors = new List<MirrorWake>(8);
        private PMR5ProjectileEvents.Subscription _mirrorSubscription;

        // ================================================================ DS：权威对象与预留

        private sealed class AuthorityEntry
        {
            public PMProjectileKey Key;
            public PMR5Projectile Obj;
            public PMProjectileSpec Spec;
            public byte[] LastPublished;
            public bool Destroyed;

            /// <summary>
            /// 已观察到的停止墓碑截止墙钟（0 = 尚未停止）。
            ///
            /// 为什么自己记一份：Coordinator 的 `PurgeExpired` 会在墓碑到期后**退役**该 key，
            /// 之后 `TryObserveFrozen` 就查不到了；若销毁只依赖生命周期查询，
            /// 权威对象会永远留在世界上（既不复制也不销毁）。在墓碑期记下截止时刻，
            /// 销毁就与「生命周期是否还记得这个 key」解耦。
            /// </summary>
            public double TombstoneUntilMs;

            /// <summary>上线时实际应用的有界追赶量（毫秒；0 = 无需追赶）。</summary>
            public int CatchUpMs;
        }

        private readonly Dictionary<PMProjectileKey, AuthorityEntry> _authorityObjects =
            new Dictionary<PMProjectileKey, AuthorityEntry>();

        private readonly Dictionary<PMProjectileKey, PMNetSpawnReservation> _reservations =
            new Dictionary<PMProjectileKey, PMNetSpawnReservation>();

        // ================================================================ 客户端：假弹 / 镜像

        private sealed class FakeEntry
        {
            public PMProjectileKey Key;
            public PMProjectileSpec Spec;
            public int PredictionMs;
            public bool TakenOver;
            public bool Revoked;

            /// <summary>
            /// 本颗假弹所属的激活 ID（R6-B 新增）。
            ///
            /// 用途：R6 收到攻击级拒绝（`ClientCombatAttackResultV1` accepted=false）时必须
            /// **一次性真实撤销该 activation 的全部假弹** —— 没有这个字段就只能去反查生命周期登记，
            /// 既多一次依赖、也会在登记已被退休时漏撤。
            /// </summary>
            public uint ActivationId;

            /// <summary>权威已下行 Confirmed（仅观测；接管条件仍是「镜像真实存在」）。</summary>
            public bool Confirmed;

            /// <summary>
            /// 本地预测最后的运动时基（毫秒），由每帧 `AdvanceMotion` 的返回值直接记下。
            ///
            /// 用途：接管时把它当作**高水位起点**（「延续假弹自己的时基」），
            /// 这样在途的旧 mirror 不会把位置拽回。直接记返回值而不是每帧克隆登记快照，
            /// 是为了不在复制回调路径上制造无谓分配。
            /// </summary>
            public double LastMoveTimeMs;
        }

        private readonly Dictionary<PMProjectileKey, FakeEntry> _fakes =
            new Dictionary<PMProjectileKey, FakeEntry>();

        private sealed class MirrorEntry
        {
            public PMProjectileKey Key;
            public PMR5Projectile Obj;
            public PMProjectileSpec Spec;
            public PMProjectileState State;

            /// <summary>
            /// 已应用的镜像**运动时基高水位**（毫秒）。
            ///
            /// 为什么需要它：同一 key 的镜像载荷会在同一帧里批量到达（创建 + 若干次 OnRep），
            /// 也可能因为复制/队列时序比当前展示的状态更旧。
            /// 旧载荷绝不允许把位置/运动时基往回拉（那就是可见的回退），而
            /// **停止/隐藏是状态转移、不受本闸门限制**（否则真实停止会被旧时基永远挡住）。
            /// </summary>
            public double HighWaterMoveTimeMs;

            /// <summary>至少成功 apply 过一次（用于判断「权威本体是否真的驱动过表现」）。</summary>
            public bool Applied;

            /// <summary>最新快照里的权威 NetId（**身份**信息，总是跟随最新快照；与运动闸门无关）。</summary>
            public uint AuthorityNetId;
        }

        private readonly Dictionary<PMProjectileKey, MirrorEntry> _mirrors =
            new Dictionary<PMProjectileKey, MirrorEntry>();

        private struct UnboundMirror
        {
            public PMProjectileKey Key;
            public uint AuthorityNetId;
            public PMProjectileSpec Spec;
            public PMProjectileState State;
            public double QueuedWallMs;
        }

        private readonly List<UnboundMirror> _unboundMirrors = new List<UnboundMirror>(8);

        /// <summary>每 (owner) 单调递增的预测 projectileId（契约：同 epoch 同 owner 单调、永不复用）。</summary>
        private readonly Dictionary<uint, uint> _nextProjectileId = new Dictionary<uint, uint>();

        /// <summary>客户端上行 Verify 配额账（契约：每条 RPC 占 1 次，不重置、不超过）。</summary>
        private readonly Dictionary<PMProjectileKey, int> _uplinkVerifyUsed = new Dictionary<PMProjectileKey, int>();

        // ================================================================ 视图

        private readonly Dictionary<PMProjectileKey, PMR5ProjectileView> _views =
            new Dictionary<PMProjectileKey, PMR5ProjectileView>();

        private readonly List<PMProjectileKey> _dirtyViewOrder = new List<PMProjectileKey>(16);
        private readonly HashSet<PMProjectileKey> _dirtyViewSet = new HashSet<PMProjectileKey>();

        // ================================================================ 时钟 / 故障

        private double _lastWallMs;
        private bool _wallStarted;
        private bool _disposed;

        private bool _faulted;
        private PMR5ProjectileFaultReason _faultReason;
        private string _faultError;

        // ================================================================ 可观测计数

        public long Pumps;
        public long ServerSpawnsReceived;
        public long ServerSpawnsAuthorized;
        public long ServerSpawnsRejected;
        public long ServerSpawnsSpawned;
        public long ServerDirectSpawned;
        public long HitsReceived;
        public long HitsReported;
        public long HitRpcsSent;
        public long HitReportRejected;
        public long DecisionPayloadsQueued;
        public long DecisionsSent;
        public long DecisionsReceived;
        public long FakesCreated;
        public long FakesTakenOver;
        public long FakesRevoked;
        public long MirrorPayloadsQueued;
        public long MirrorPayloadsApplied;
        public long MirrorIdentityRejected;
        public long MirrorPositionGated;
        public long MirrorHiddenNoRevive;
        public long MirrorPayloadQueueDrops;
        public long UnboundMirrorsStaged;
        public long UnboundMirrorsExpired;
        public long UnboundMirrorsDropped;
        public long InboundQueueOverflows;
        public long ReservationsCancelled;
        public long AuthorityObjectsDestroyed;
        public long ViewDirtyOverflows;
        public long SettlementsHandedToR6;
        public long SettlementsWithNoConsumer;
        public long SettlementsQueued;
        public long ThreadViolations;
        public long FakesRetired;
        public long MirrorStaleDropped;
        public long CatchUpsApplied;
        public long OwnerCleanups;
        public long ClientReleasesDrained;

        /// <summary>R6-B：被 <see cref="CancelPredictedActivation"/> 真实撤销的假弹颗数（观测）。</summary>
        public long CancelledPredictions;

        /// <summary>
        /// 已受理 key 的**重复上行**被无副作用幂等吸收的次数
        /// （不重复 policy 授权 / 不预留 NetId / 不 RequestSpawn / 不下发裁决）。
        /// </summary>
        public long ServerSpawnsDuplicateIgnored;

        /// <summary>
        /// 已受理 key 的**冲突**重复上行（同 key 但 activationId 或枪口位置与登记不符）次数。
        /// 只计数 + 限流告警，绝不修改原对象/原决策，也不据此下发 Rejected。
        /// </summary>
        public long RejectedDuplicateConflict;

        /// <summary>已回收（retired）key 的迟到上行被按终态吸收的次数（无副作用、不补 Rejected）。</summary>
        public long RetiredSpawnsDropped;

        /// <summary>结算出口（R6 的**唯一**消费点）。未订阅时进有界待取缓冲（<see cref="DrainSettlements"/>）。</summary>
        public event Action<PMProjectileSettlement> Settlement;

        /// <summary>
        /// 无消费者期间累计的待取结算（有界，<see cref="MaxPendingSettlements"/>）。
        /// 宿主（R6 接入前的过渡宿主）必须周期性 <see cref="DrainSettlements"/>。
        /// </summary>
        private readonly List<PMProjectileSettlement> _pendingSettlements = new List<PMProjectileSettlement>(8);

        // ================================================================ 生命周期

        /// <summary>
        /// 构造会话级驱动。
        ///
        /// `history` 是**宿主注入**的目标历史（由宿主从真实玩家状态填入，本驱动只读不写，
        /// 契约：history 由宿主注入填充、不伪造）。
        /// `policy` 是 DS 的可信授权策略（DS 必须提供；客户端可为 null）。
        /// `hostMotion` 是可选的宿主运动/停止 hook（fail closed 语义见 PMProjectileCoordinator）。
        /// </summary>
        public PMR5ProjectileDriver(
            PMNetWorld world,
            PMNetSessionBridge bridge,
            uint epoch,
            PMProjectileHistory history,
            IPMR5ProjectileAuthorityPolicy policy = null,
            IPMProjectileHostMotion hostMotion = null,
            IPMProjectileHitFilter hitFilter = null)
        {
            if (world == null) { throw new ArgumentNullException("world"); }
            if (bridge == null) { throw new ArgumentNullException("bridge"); }
            if (history == null) { throw new ArgumentNullException("history"); }
            if (epoch == 0u) { throw new ArgumentOutOfRangeException("epoch", "epoch 必须非 0"); }
            if (!ReferenceEquals(bridge.World, world))
            {
                throw new ArgumentException("桥不属于该世界", "bridge");
            }

            _world = world;
            _bridge = bridge;
            _epoch = epoch;
            _history = history;
            _policy = policy;
            _hostMotion = hostMotion;
            _isServer = world.IsServer;
            _ownerThreadId = Environment.CurrentManagedThreadId;

            // 权威命中过滤器：本批不接队伍/友伤规则（R6 才有伤害），因此未注入时用
            // 「权威 accept-all」过滤器 —— 它是**权威侧**拥有的过滤器，不是跳过校验。
            IPMProjectileHitFilter effectiveFilter = hitFilter != null ? hitFilter : AcceptAllFilter.Instance;

            _coordinator = new PMProjectileCoordinator(epoch, history, effectiveFilter, hostMotion);

            // 镜像入口：订阅**本 world** 的投射物副本事件（契约：按 obj.World 筛选 +
            // 各宿主 Dispose 取消自己的订阅）。回调只入队。
            _mirrorSubscription = PMR5ProjectileEvents.Subscribe(
                world, OnMirrorCreated, OnMirrorUpdated, OnMirrorDestroyed);
        }

        private sealed class AcceptAllFilter : IPMProjectileHitFilter
        {
            public static readonly AcceptAllFilter Instance = new AcceptAllFilter();

            public bool Accept(PMProjectileKey projectile, PMProjectileTargetSample target, PMVector3 sanitizedImpact)
            {
                return true;
            }
        }

        // ================================================================ 只读视图

        public PMNetWorld World { get { return _world; } }
        public PMNetSessionBridge Bridge { get { return _bridge; } }
        public uint Epoch { get { return _epoch; } }
        public bool IsServer { get { return _isServer; } }

        /// <summary>宿主注入的目标历史（本驱动只读；由宿主从真实玩家状态填充，不伪造）。</summary>
        public PMProjectileHistory History { get { return _history; } }

        /// <summary>宿主注入的运动 hook（可为 null；fail closed 语义见 Coordinator）。</summary>
        public IPMProjectileHostMotion HostMotion { get { return _hostMotion; } }

        /// <summary>DS 注入的可信授权策略（客户端为 null）。</summary>
        public IPMR5ProjectileAuthorityPolicy Policy { get { return _policy; } }

        public bool IsFaulted { get { return _faulted; } }
        public PMR5ProjectileFaultReason FaultReason { get { return _faultReason; } }
        public string FaultError { get { return _faultError == null ? string.Empty : _faultError; } }
        public bool IsDisposed { get { return _disposed; } }
        public double LastWallTimeMs { get { return _lastWallMs; } }

        /// <summary>底层 Coordinator（只读观察；写入口都在本驱动上）。</summary>
        public PMProjectileCoordinator Coordinator { get { return _coordinator; } }

        /// <summary>DS：仍持有未消费 NetId 预留的 key 数。</summary>
        public int ReservationCount { get { return _reservations.Count; } }

        /// <summary>当前视图数。</summary>
        public int ViewCount { get { return _views.Count; } }

        /// <summary>本地假弹登记数（观测/有界性断言；飞完的 key 会退休）。</summary>
        public int FakeCount { get { return _fakes.Count; } }

        /// <summary>镜像登记数（观测/有界性断言；镜像对象被销毁后立即退休）。</summary>
        public int MirrorCount { get { return _mirrors.Count; } }

        /// <summary>DS 仍在本会话存活的权威对象数。</summary>
        public int AuthorityObjectCount { get { return _authorityObjects.Count; } }

        /// <summary>上行 Verify 账里仍被记住的 key 数（key 退休后必须回落）。</summary>
        public int UplinkVerifyKeyCount { get { return _uplinkVerifyUsed.Count; } }

        /// <summary>无消费者期间待取的结算条数。</summary>
        public int PendingSettlementCount { get { return _pendingSettlements.Count; } }

        /// <summary>已绑定 player 数。</summary>
        public int BoundPlayerCount { get { return _adapters.Count; } }

        // ================================================================ 玩家绑定

        /// <summary>
        /// 把一个真实 <see cref="PMR3Player"/> 的声明回调接到本驱动上。
        ///
        /// 回调携带 player 身份，因此**上行源认证**（owner = player.NetId）与
        /// **下行归属**（是否本地 owner）都能在回调入口直接确定，不依赖 payload 自述。
        /// 幂等：同一 player 重复绑定返回 false。
        /// </summary>
        public bool BindPlayer(PMR3Player player)
        {
            EnsureThread();
            if (_disposed || _faulted) { return false; }
            if (player == null || !player.NetId.IsValid) { return false; }
            if (_adapters.ContainsKey(player)) { return false; }

            PlayerAdapter adapter = new PlayerAdapter(this, player);
            _adapters.Add(player, adapter);

            if (!_playersByOwnerNetId.ContainsKey(player.NetId.Value))
            {
                _playersByOwnerNetId.Add(player.NetId.Value, player);
            }

            player.ProjectileDriver = adapter;

            // 契约：收包前未绑 player 的镜像必须暂存（上限 + TTL），绑定时立即兑现。
            ReplayUnboundMirrorsFor(player.NetId.Value);
            return true;
        }

        /// <summary>摘掉一个 player 的接线（幂等）。</summary>
        public bool UnbindPlayer(PMR3Player player)
        {
            EnsureThread();
            if (player == null) { return false; }

            PlayerAdapter adapter;
            if (!_adapters.TryGetValue(player, out adapter)) { return false; }

            _adapters.Remove(player);
            if (ReferenceEquals(player.ProjectileDriver, adapter))
            {
                player.ProjectileDriver = null;
            }

            PMR3Player existing;
            if (_playersByOwnerNetId.TryGetValue(player.NetId.Value, out existing)
                && ReferenceEquals(existing, player))
            {
                _playersByOwnerNetId.Remove(player.NetId.Value);
            }

            // 契约（R5-B2c 返工）：**移除 owner 的未消费预留/队列要取消**。
            // 摘掉接缝却不收拾它留下的预留与待处理队列，就等于把「本会话的 NetId 预留」
            // 挂到 Dispose（编号永不回收）且让已断开玩家的载荷继续被消费。
            CleanupOwner(player);
            return true;
        }

        /// <summary>
        /// 一个 owner 离开会话时的有界收拾：
        ///
        ///   1) **取消它的未消费 NetId 预留**（编号是 World 的有限资源，不能悬空到 Dispose）；
        ///   2) 丢弃还没被 Pump 消费的入站载荷（断开后不得再推进）；
        ///   3) 退休它的假弹/镜像/上行 Verify 账/未绑定镜像暂存；
        ///   4) `Coordinator.ClearOwner` 清掉它的暂存与登记（**保留 ID 高水位** ⇒ 旧 ID 不复活）。
        ///
        /// 刻意**不**在此销毁已有的权威对象：它们是已复制的活对象，
        /// 直接抹掉而不发停止快照就会在客户端留下幽灵；
        /// 它们的生命周期仍由寿命/墓碑/PurgeExpired 驱动。
        /// </summary>
        private void CleanupOwner(PMR3Player player)
        {
            if (player == null) { return; }

            uint ownerNetId = player.NetId.IsValid ? player.NetId.Value : 0u;
            if (ownerNetId == 0u) { return; }

            // 1) 未消费预留。
            List<PMProjectileKey> reserved = null;
            foreach (KeyValuePair<PMProjectileKey, PMNetSpawnReservation> kv in _reservations)
            {
                if (kv.Key.OwnerNetId != ownerNetId) { continue; }
                if (reserved == null) { reserved = new List<PMProjectileKey>(4); }
                reserved.Add(kv.Key);
            }

            if (reserved != null)
            {
                for (int i = 0; i < reserved.Count; i++)
                {
                    PMNetSpawnReservation token;
                    if (_reservations.TryGetValue(reserved[i], out token))
                    {
                        _reservations.Remove(reserved[i]);
                        _world.CancelReservedNetId(token);
                        ReservationsCancelled++;
                    }
                }
            }

            // 2) 入站队列：丢掉属于这个 player 的载荷。
            for (int i = _inboundSpawns.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_inboundSpawns[i].Player, player)) { _inboundSpawns.RemoveAt(i); }
            }

            for (int i = _inboundHits.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_inboundHits[i].Player, player)) { _inboundHits.RemoveAt(i); }
            }

            for (int i = _inboundDecisions.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_inboundDecisions[i].Player, player)) { _inboundDecisions.RemoveAt(i); }
            }

            // 3) 本地账/表现。
            List<PMProjectileKey> owned = null;
            foreach (KeyValuePair<PMProjectileKey, FakeEntry> kv in _fakes)
            {
                if (kv.Key.OwnerNetId != ownerNetId) { continue; }
                if (owned == null) { owned = new List<PMProjectileKey>(4); }
                owned.Add(kv.Key);
            }

            foreach (KeyValuePair<PMProjectileKey, MirrorEntry> kv in _mirrors)
            {
                if (kv.Key.OwnerNetId != ownerNetId) { continue; }
                if (owned == null) { owned = new List<PMProjectileKey>(4); }
                owned.Add(kv.Key);
            }

            if (owned != null)
            {
                for (int i = 0; i < owned.Count; i++)
                {
                    _fakes.Remove(owned[i]);
                    _mirrors.Remove(owned[i]);
                    _uplinkVerifyUsed.Remove(owned[i]);
                }
            }

            for (int i = _unboundMirrors.Count - 1; i >= 0; i--)
            {
                if (_unboundMirrors[i].Key.OwnerNetId == ownerNetId) { _unboundMirrors.RemoveAt(i); }
            }

            // 4) 整合器侧（全量清理该 owner，保留高水位）。
            try
            {
                _coordinator.ClearOwner(ownerNetId, _lastWallMs);
            }
            catch (InvalidOperationException)
            {
                // 账本已 faulted：不把异常冒到 UnbindPlayer 的调用方。
            }

            // 5) DS：该 owner 的权威对象已无账本支撑（ClearOwner 会退役它们的登记），
            //    若不销毁就会成为「既不复制也不销毁」的悬空对象。销毁是可见的（客户端收到 Destroy），
            //    客户端侧的镜像登记与假弹会随销毁同步退休（RemoveMirrorByObject → RetireLocalKey）。
            if (_isServer && _authorityObjects.Count > 0)
            {
                List<PMProjectileKey> orphaned = null;
                foreach (KeyValuePair<PMProjectileKey, AuthorityEntry> kv in _authorityObjects)
                {
                    if (kv.Key.OwnerNetId != ownerNetId) { continue; }
                    if (orphaned == null) { orphaned = new List<PMProjectileKey>(4); }
                    orphaned.Add(kv.Key);
                }

                if (orphaned != null)
                {
                    for (int i = 0; i < orphaned.Count; i++)
                    {
                        AuthorityEntry entry = _authorityObjects[orphaned[i]];
                        _authorityObjects.Remove(orphaned[i]);
                        if (entry.Obj != null && !entry.Destroyed)
                        {
                            entry.Destroyed = true;
                            _bridge.DestroyObject(entry.Obj);
                            AuthorityObjectsDestroyed++;
                        }
                    }
                }
            }

            // ClearOwner 会产出释放项；客户端必须排空，否则出口可能因容量而 fault。
            if (!_isServer)
            {
                PMProjectileActivationRelease[] releases;
                int drained = _coordinator.DrainDecisions(MaxApplyPerPump, out releases);
                if (drained > 0) { ClientReleasesDrained += drained; }
            }

            OwnerCleanups++;
        }

        /// <summary>该 player 是否已绑定。</summary>
        public bool IsPlayerBound(PMR3Player player)
        {
            return player != null && _adapters.ContainsKey(player);
        }

        /// <summary>本地 owner 副本（客户端侧：其 uid 等于本地连接 uid 的那个副本）；找不到返回 null。</summary>
        public PMR3Player LocalOwner
        {
            get
            {
                if (_isServer) { return null; }

                int uid = LocalUid;
                if (uid == 0) { return null; }

                foreach (KeyValuePair<PMR3Player, PlayerAdapter> kv in _adapters)
                {
                    if (kv.Key.Uid == uid) { return kv.Key; }
                }

                return null;
            }
        }

        /// <summary>本端连接的 uid（客户端取服务端连接的 identity；DS 无「本地 owner」概念，返回 0）。</summary>
        public int LocalUid
        {
            get
            {
                if (_isServer) { return 0; }
                PMTransportConnection server = _bridge.ServerConnection;
                return server != null ? server.Identity.Uid : 0;
            }
        }

        // ================================================================ 入站（回调只入队）

        private void EnqueueServerSpawn(PMR3Player player, byte[] payload)
        {
            EnsureThread();
            if (_disposed || _faulted) { return; }
            if (!_isServer) { ServerSpawnsRejected++; return; }

            if (_inboundSpawns.Count >= MaxInboundSpawns)
            {
                // 上行生成是可被客户端滥用的流：满了**拒绝并计数**（不 fault 整个会话），
                // 但要给 owner 一条 Rejected 裁决，避免它的假弹永久驻留。
                InboundQueueOverflows++;
                ServerSpawnsRejected++;
                RejectOwnerUplinkSpawn(player, payload, "inbound-queue-full");
                return;
            }

            InboundSpawn item = new InboundSpawn();
            item.Player = player;
            item.Payload = payload;
            _inboundSpawns.Add(item);
        }

        private void EnqueueServerHit(PMR3Player player, byte[] payload)
        {
            EnsureThread();
            if (_disposed || _faulted) { return; }
            if (!_isServer) { return; }

            if (_inboundHits.Count >= MaxInboundHits)
            {
                InboundQueueOverflows++;
                return;
            }

            InboundHit item = new InboundHit();
            item.Player = player;
            item.Payload = payload;
            _inboundHits.Add(item);
        }

        private void EnqueueClientDecision(PMR3Player player, byte[] payload)
        {
            EnsureThread();
            if (_disposed || _faulted) { return; }
            if (_isServer) { return; }

            if (_inboundDecisions.Count >= MaxInboundDecisions)
            {
                // 裁决是权威下行、可靠域：丢它就等于永久假弹 ⇒ 显式会话失败。
                InboundQueueOverflows++;
                Fault(PMR5ProjectileFaultReason.OutputOverflow,
                    "下行裁决入站队列溢出（上限 " + MaxInboundDecisions + "）");
                return;
            }

            InboundDecision item = new InboundDecision();
            item.Player = player;
            item.Payload = payload;
            _inboundDecisions.Add(item);
            DecisionPayloadsQueued++;
        }

        private void OnMirrorCreated(PMR5Projectile obj)
        {
            EnqueueMirror(obj, false, PMObjectDestroyReason.Destroyed);
        }

        private void OnMirrorUpdated(PMR5Projectile obj)
        {
            EnqueueMirror(obj, false, PMObjectDestroyReason.Destroyed);
        }

        private void OnMirrorDestroyed(PMR5Projectile obj, PMObjectDestroyReason reason)
        {
            EnqueueMirror(obj, true, reason);
        }

        private void EnqueueMirror(PMR5Projectile obj, bool destroyed, PMObjectDestroyReason reason)
        {
            EnsureThread();
            if (_disposed || _faulted || obj == null) { return; }

            if (_inboundMirrors.Count >= MaxInboundMirrors)
            {
                // 镜子是「当前状态」语义（不是事件）：满了丢最旧保最新。
                _inboundMirrors.RemoveAt(0);
                MirrorPayloadQueueDrops++;
            }

            MirrorWake wake = new MirrorWake();
            wake.Object = obj;
            wake.Destroyed = destroyed;
            wake.Reason = reason;
            _inboundMirrors.Add(wake);
            MirrorPayloadsQueued++;
        }

        // ================================================================ 主线程 Pump

        /// <summary>
        /// 主线程每帧入口：`wallNowMs` 是**单调墙钟**（毫秒），`stepMs` 是本帧推进量。
        ///
        /// 次序（契约）：先入站处理 → 再运动 / 状态发布 → 最后 Drain 出口。
        /// 每类批次都有上限（<see cref="MaxApplyPerPump"/>），因此单帧工作量有界。
        /// </summary>
        public void Pump(double wallNowMs, int stepMs)
        {
            EnsureThread();
            if (_disposed || _faulted) { return; }

            if (_wallStarted && wallNowMs < _lastWallMs)
            {
                Fault(PMR5ProjectileFaultReason.NonMonotonicWallClock,
                    "Pump 墙钟倒退：now=" + wallNowMs + " last=" + _lastWallMs);
                return;
            }

            _lastWallMs = wallNowMs;
            _wallStarted = true;
            Pumps++;

            if (stepMs < 0) { stepMs = 0; }
            if (stepMs > PMProjectileCoordinatorLimits.MaxCatchUpStepMs)
            {
                stepMs = PMProjectileCoordinatorLimits.MaxCatchUpStepMs;
            }

            ApplyInbound();
            if (_faulted) { return; }

            if (_isServer)
            {
                AdvanceAuthorityMotion(stepMs, wallNowMs);
                DrainSpawnReady(wallNowMs);
                DrainDecisions(wallNowMs);
                DestroyExpiredTombstones(wallNowMs);
                SweepStaleReservations();
            }
            else
            {
                AdvanceLocalFakes(stepMs, wallNowMs);
                SweepLocalState(wallNowMs);
            }

            ApplyMirrors(wallNowMs);
            ApplyUnboundMirrorTtl(wallNowMs);
            DrainCoordinatorOutlets();
            RebuildViews();
        }

        // ---------------------------------------------------------------- 入站应用

        private void ApplyInbound()
        {
            ApplyInboundSpawns();
            ApplyInboundHits();
            ApplyInboundDecisions();
        }

        private void ApplyInboundSpawns()
        {
            int limit = _inboundSpawns.Count < MaxApplyPerPump ? _inboundSpawns.Count : MaxApplyPerPump;
            for (int i = 0; i < limit; i++)
            {
                InboundSpawn item = _inboundSpawns[0];
                _inboundSpawns.RemoveAt(0);
                HandleServerSpawn(item.Player, item.Payload);
                if (_faulted) { return; }
            }
        }

        private void ApplyInboundHits()
        {
            int limit = _inboundHits.Count < MaxApplyPerPump ? _inboundHits.Count : MaxApplyPerPump;
            for (int i = 0; i < limit; i++)
            {
                InboundHit item = _inboundHits[0];
                _inboundHits.RemoveAt(0);
                HandleServerHit(item.Player, item.Payload);
                if (_faulted) { return; }
            }
        }

        private void ApplyInboundDecisions()
        {
            int limit = _inboundDecisions.Count < MaxApplyPerPump ? _inboundDecisions.Count : MaxApplyPerPump;
            for (int i = 0; i < limit; i++)
            {
                InboundDecision item = _inboundDecisions[0];
                _inboundDecisions.RemoveAt(0);
                HandleClientDecision(item.Player, item.Payload);
                if (_faulted) { return; }
            }
        }

        // ---------------------------------------------------------------- DS：上行生成

        private void HandleServerSpawn(PMR3Player player, byte[] payload)
        {
            ServerSpawnsReceived++;

            if (player == null)
            {
                ServerSpawnsRejected++;
                return;
            }

            // 认证 owner：只取副本的 NetId（客户端自报的 owner 只用于「必须一致」断言）。
            uint authenticatedOwnerNetId = player.NetId.Value;

            // owner 连接必须就绪（契约：从该 player 与 ready owner connection 取得身份）。
            PMTransportConnection ownerConnection = player.OwnerConnection as PMTransportConnection;
            if (ownerConnection == null || !ownerConnection.IsReady)
            {
                ServerSpawnsRejected++;
                return;
            }

            if (_policy == null)
            {
                Fault(PMR5ProjectileFaultReason.PolicyMissing,
                    "DS 收到上行生成但没有注入 IPMR5ProjectileAuthorityPolicy");
                return;
            }

            PMProjectileSpawnIntent intent;
            string error;
            if (!PMProjectileCodec.TryDecodeSpawnIntent(payload, out intent, out error) || intent == null)
            {
                ServerSpawnsRejected++;
                return;
            }

            // 身份断言：epoch / origin / owner 必须与认证事实一致；activationId 必须非 0。
            if (intent.Key.Epoch != _epoch
                || intent.Key.Origin != PMProjectileOrigin.ClientPredicted
                || intent.Key.OwnerNetId != authenticatedOwnerNetId
                || intent.ActivationId == 0u
                || !intent.Key.IsValid)
            {
                ServerSpawnsRejected++;
                SendDecisionFor(authenticatedOwnerNetId, intent.Key, intent.ActivationId,
                    PMActivationResult.Rejected, "identity");
                return;
            }

            // ---------------------------------------------------------------- 已受理 key：无副作用幂等门
            //
            // 背景（真实根因，不是宿主旁路）：可靠上行可能在「客户端重连 / 传输重发 / 换流」时把
            // **同一颗弹**的生成意图再投一次。若照原路径重跑，会：
            //   ① 再扣一次 policy 授权额度（单调 activation 水位 / 开火间隔水位被无谓推进）；
            //   ② 再预留一个真实 NetId（世界容量被白占，最终还得取消）；
            //   ③ 再 RequestSpawn → 生命周期判 DuplicateId → 准入 AlreadyAdmitted；
            //   ④ 最后**无条件**下行一条 Rejected —— 对一条已经 Confirmed、镜像已在客户端接管的良弹，
            //      这条 Rejected 会让客户端反向 RevokeFake，把**合法假弹撤掉**（终态被倒退）。
            //
            // 收口位置就在这里（而不是在宿主加装饰器）：完成 codec 解码 + 认证身份断言之后、
            // policy / 预留 / RequestSpawn **之前**。已受理 = 驱动自己的登记事实：
            //   `_reservations`（已预留未上线）/ `_authorityObjects`（已 SpawnReserved 上线）/ 
            //   `_coordinator.TryObserveRegistration`（协调器-生命周期登记记录，含待生成与已生成）。
            //
            // **真实性边界（安全口径）**：本门只信服务端自己的登记事实，不读 payload 的任何自报标志；
            // 且调用点位于身份断言之后，因此能走到这里的 key 必然已被**同一认证 owner** 的 policy
            // 授权并成功登记过。生命周期对每 (owner, origin) 的 projectileId 维护**单调高水位、永不复用**
            // （CreateEntry 的 NonMonotonicId），所以同 key 不可能是「另一颗伪装成新弹的伪造」——
            // 它只能是同一颗弹的重复投递。这正是「仅凭已存在 key 忽略重复」成立的前提。
            // 反过来说：登记事实被清（Purge/Retire）且超出有界终态环时本门不命中，会退回原路径
            // （预留 + policy + 生命周期水位 NonMonotonicId 拒绝）：**有界且显式**，不无限缓存指纹。
            PMProjectileRegistration admitted;
            string admittedBy;
            if (TryFindAdmittedKey(intent.Key, out admitted, out admittedBy))
            {
                HandleDuplicateUplinkSpawn(intent, admitted, admittedBy);
                return;
            }

            // 终态（已回收）key 的迟到上行：按终态拒、**无副作用**。
            // 不重新预留/授权/登记，也**不**补发 Rejected —— 迟到请求对应的假弹要么早已接管，
            // 要么由客户端自己的本地 TTL/墓碑清理收束；补 Rejected 只会把一条已 Confirmed 的弹反向撤销。
            if (_coordinator.Lifecycle.IsRetired(intent.Key))
            {
                RetiredSpawnsDropped++;
                return;
            }

            // 先预留真实 NetId（契约：DS 先 TryReserveNetId，RequestSpawn 用 token.NetId.Value）。
            PMNetSpawnReservation token;
            if (!_world.TryReserveNetId(out token) || token == null)
            {
                ServerSpawnsRejected++;
                SendDecisionFor(authenticatedOwnerNetId, intent.Key, intent.ActivationId,
                    PMActivationResult.Rejected, "reserve");
                return;
            }

            PMProjectileSpec trustedSpec;
            PMVector3 ownerPosition;
            PMActivationResult verdict;
            string policyError;
            bool authorized;
            try
            {
                authorized = _policy.TryAuthorizeSpawn(player, intent, out trustedSpec, out ownerPosition,
                    out verdict, out policyError);
            }
            catch (Exception ex)
            {
                _world.CancelReservedNetId(token);
                ReservationsCancelled++;
                Fault(PMR5ProjectileFaultReason.InternalInvariant,
                    "IPMR5ProjectileAuthorityPolicy 抛异常：" + ex.GetType().Name + " " + ex.Message);
                return;
            }

            if (!authorized || trustedSpec == null)
            {
                _world.CancelReservedNetId(token);
                ReservationsCancelled++;
                ServerSpawnsRejected++;
                // 策略拒绝 = 权威终态拒绝：记入账本（终态不倒退）并下行 Rejected 让客户端撤掉假弹。
                ResolveActivationInternal(authenticatedOwnerNetId, intent.ActivationId,
                    PMActivationResult.Rejected, _lastWallMs);
                SendDecisionFor(authenticatedOwnerNetId, intent.Key, intent.ActivationId,
                    PMActivationResult.Rejected, "policy");
                return;
            }

            if (verdict == PMActivationResult.Rejected)
            {
                _world.CancelReservedNetId(token);
                ReservationsCancelled++;
                ServerSpawnsRejected++;
                ResolveActivationInternal(authenticatedOwnerNetId, intent.ActivationId,
                    PMActivationResult.Rejected, _lastWallMs);
                SendDecisionFor(authenticatedOwnerNetId, intent.Key, intent.ActivationId,
                    PMActivationResult.Rejected, "policy-rejected");
                return;
            }

            // 先把权威裁决写进账本（Pending 也写），RequestSpawn 再据此走挂起或立即生成。
            ResolveActivationInternal(authenticatedOwnerNetId, intent.ActivationId, verdict, _lastWallMs);

            PMProjectileSpawnRequest request = new PMProjectileSpawnRequest();
            PMProjectileState state = new PMProjectileState();
            state.Key = intent.Key;
            state.ActivationId = intent.ActivationId;
            state.SpawnPosition = intent.Position;
            state.PreviousPosition = intent.Position;
            state.Position = intent.Position;
            state.Velocity = intent.Direction;
            state.Yaw = intent.Yaw;
            state.MoveTimeMs = 0.0;
            request.State = state;
            request.Spec = trustedSpec;
            request.PredictionMs = intent.PredictionMs;

            PMProjectileAdmissionOutcome admission;
            try
            {
                admission = _coordinator.RequestSpawn(
                    request,
                    authenticatedOwnerNetId,
                    token.NetId.Value,
                    authenticatedOwnerNetId,
                    ownerPosition,
                    trustedSpec,
                    new uint[0],
                    _lastWallMs);
            }
            catch (InvalidOperationException ex)
            {
                _world.CancelReservedNetId(token);
                ReservationsCancelled++;
                Fault(PMR5ProjectileFaultReason.CoordinatorFaulted, "RequestSpawn：" + ex.Message);
                return;
            }

            if (!admission.IsAdmitted)
            {
                _world.CancelReservedNetId(token);
                ReservationsCancelled++;
                ServerSpawnsRejected++;
                SendDecisionFor(authenticatedOwnerNetId, intent.Key, intent.ActivationId,
                    PMActivationResult.Rejected, "admission-" + admission.Result);
                return;
            }

            // 预留令牌在「生成出口被兑现」或「TTL/拒绝」之前必须一直被记住。
            _reservations[admission.Key] = token;
            ServerSpawnsAuthorized++;
        }

        /// <summary>
        /// 「该 key 已被本会话受理」的判据（只读登记事实，**零副作用**）：
        ///   1) `_reservations`：已预留 NetId、尚未上线（Pending 或等 SpawnReady）；
        ///   2) `_authorityObjects`：已 `SpawnReserved` 上线；
        ///   3) `_coordinator.TryObserveRegistration`：协调器-生命周期登记记录（最贴近「同一颗弹」的事实）；
        ///   4) `IsSpawnPending` / `IsSpawned`：协调器 KeyRecord（可能在 ForgetRecord 后为 false，故排在最后）。
        ///
        /// 命中即意味着「这颗弹已经走完 policy + RequestSpawn」，重复上行必须幂等吸收。
        /// `admitted` 仅在 (3) 命中时非 null（用于冲突检测）；`evidence` 是命中来源，供诊断与测试可读。
        ///
        /// 真实性前提：调用方已过 codec + 认证身份断言，且生命周期对每 (owner, origin) 的 projectileId
        /// 单调且永不复用 ⇒ 同 key 就是同一颗弹，不是伪造的新弹。
        /// </summary>
        private bool TryFindAdmittedKey(PMProjectileKey key, out PMProjectileRegistration admitted, out string evidence)
        {
            admitted = null;
            evidence = null;

            bool admittedByOwned = false;
            if (_reservations.ContainsKey(key)) { evidence = "reservation"; admittedByOwned = true; }
            else if (_authorityObjects.ContainsKey(key)) { evidence = "authority-object"; admittedByOwned = true; }

            // 冲突检测需要 activationId / 枪口（登记快照）；未登记时 TryObserveRegistration 不分配直接返回 false。
            PMProjectileRegistration snapshot;
            if (_coordinator.TryObserveRegistration(key, out snapshot) && snapshot != null)
            {
                admitted = snapshot;
                if (!admittedByOwned) { evidence = "registration"; }
                return true;
            }

            if (admittedByOwned) { return true; }

            if (_coordinator.IsSpawnPending(key)) { evidence = "spawn-pending"; return true; }
            if (_coordinator.IsSpawned(key)) { evidence = "spawned"; return true; }

            return false;
        }

        /// <summary>
        /// 已受理 key 的重复上行：**零副作用**幂等吸收。
        ///
        /// 保证（逐条对应收口口径）：
        ///   · **不再** `_policy.TryAuthorizeSpawn`（不扣授权额度、不推进单调 activation/间隔水位）；
        ///   · **不再** `_world.TryReserveNetId`（不占世界容量、不产生待取消令牌）；
        ///   · **不再** `_coordinator.RequestSpawn`（不重复登记、不刷新 Pending TTL、不产出第二份 SpawnReady）；
        ///   · **不**下发任何 Rejected —— 原裁决在可靠域上会照常送达，重复下行只会反向撤销合法假弹。
        ///
        /// 冲突（同 key 但 activationId 或枪口位置与登记不符）：只**计数**并限流告警，
        /// 绝不修改原对象/原决策（不得覆写已建对象、不得改写激活账本）。
        /// </summary>
        private void HandleDuplicateUplinkSpawn(PMProjectileSpawnIntent intent,
            PMProjectileRegistration admitted, string evidence)
        {
            ServerSpawnsDuplicateIgnored++;

            bool activationChanged = admitted != null && admitted.ActivationId != intent.ActivationId;
            bool muzzleDrifted = admitted != null && admitted.State != null
                && PMVector3.Distance(admitted.State.SpawnPosition, intent.Position) > DuplicateMuzzleToleranceM;

            if (!activationChanged && !muzzleDrifted) { return; }

            RejectedDuplicateConflict++;
            if (RejectedDuplicateConflict <= MaxDuplicateConflictWarnings)
            {
                Warn("[PMR5ProjectileDriver] 已受理 key 的冲突重复上行（" + DescribeKey(intent.Key)
                     + "，来源=" + evidence
                     + (activationChanged
                         ? "，activationId " + admitted.ActivationId + "→" + intent.ActivationId
                         : string.Empty)
                     + (muzzleDrifted ? "，枪口位置漂移" : string.Empty)
                     + "）：忽略，不改原对象/原决策、不发 Rejected。");
            }
        }

        /// <summary>
        /// 入站生成队列溢出时给 owner 补一条 Rejected 裁决（best-effort，解码失败就不发）。
        ///
        /// 与 <see cref="HandleServerSpawn"/> 同一铁律：对**已被本会话受理**的 key 绝不补发 Rejected
        /// （那会反向撤销一条已 Confirmed 的合法假弹）。队列溢出只应淘汰「尚未受理」的载荷。
        /// </summary>
        private void RejectOwnerUplinkSpawn(PMR3Player player, byte[] payload, string reason)
        {
            if (player == null || payload == null) { return; }

            PMProjectileSpawnIntent intent;
            string error;
            if (!PMProjectileCodec.TryDecodeSpawnIntent(payload, out intent, out error) || intent == null)
            {
                return;
            }

            PMProjectileRegistration admitted;
            string evidence;
            if (TryFindAdmittedKey(intent.Key, out admitted, out evidence)
                || _coordinator.Lifecycle.IsRetired(intent.Key))
            {
                ServerSpawnsDuplicateIgnored++;
                return;
            }

            SendDecisionFor(player.NetId.Value, intent.Key, intent.ActivationId,
                PMActivationResult.Rejected, reason);
        }

        // ---------------------------------------------------------------- DS：上行命中

        private void HandleServerHit(PMR3Player player, byte[] payload)
        {
            HitsReceived++;

            if (player == null) { return; }

            uint authenticatedOwnerNetId = player.NetId.Value;
            PMTransportConnection ownerConnection = player.OwnerConnection as PMTransportConnection;
            if (ownerConnection == null || !ownerConnection.IsReady) { return; }

            PMProjectileHitBatch batch;
            string error;
            if (!PMProjectileCodec.TryDecodeHitBatch(payload, out batch, out error) || batch == null)
            {
                return;
            }

            // codec 已保证上行 origin 只能是 ClientPredicted；这里再断言 owner 一致性
            // （Coordinator 也会做，但驱动入口先挡一次，避免无谓的记账）。
            if (batch.Key.OwnerNetId != authenticatedOwnerNetId || batch.Key.Epoch != _epoch)
            {
                return;
            }

            PMProjectileHitReportOutcome outcome;
            try
            {
                // 命中结论**只**经真实 history 验证（history 由宿主注入填充）。
                outcome = _coordinator.ReportHits(batch, authenticatedOwnerNetId, _lastWallMs);
            }
            catch (InvalidOperationException ex)
            {
                Fault(PMR5ProjectileFaultReason.CoordinatorFaulted, "ReportHits：" + ex.Message);
                return;
            }

            if (outcome.Result == PMProjectileHitReportResult.Settled
                || outcome.Result == PMProjectileHitReportResult.Pending
                || outcome.Result == PMProjectileHitReportResult.StashedForSpawn)
            {
                HitsReported++;
            }
            else
            {
                HitReportRejected++;
            }
        }

        // ---------------------------------------------------------------- 客户端：下行裁决

        private void HandleClientDecision(PMR3Player player, byte[] payload)
        {
            DecisionsReceived++;

            if (player == null) { return; }

            // 只处理「本地 owner 副本」收到的裁决（SP 不会收到 owner-only 可靠下行）。
            PMR3Player localOwner = LocalOwner;
            if (localOwner == null || !ReferenceEquals(localOwner, player)) { return; }

            PMProjectileDecision decision;
            string error;
            if (!PMProjectileCodec.TryDecodeDecision(payload, out decision, out error) || decision == null)
            {
                return;
            }

            if (decision.Key.Epoch != _epoch || decision.Key.OwnerNetId != player.NetId.Value)
            {
                MirrorIdentityRejected++;
                return;
            }

            FakeEntry fake;
            if (!_fakes.TryGetValue(decision.Key, out fake))
            {
                // 没有本地假弹（可能已撤销/已被镜像接管）：幂等吸收，不 fault。
                return;
            }

            if (decision.Result == PMActivationResult.Rejected)
            {
                RevokeFake(fake, decision.Key);
                return;
            }

            if (decision.Result == PMActivationResult.Confirmed)
            {
                fake.Confirmed = true;

                // 契约：**接管一次性消费完整运动/停止/命中集合**，且接管的前提是「权威镜像真实存在」。
                // 裁决先到（镜像还在路上）时**不得**提前接管：一旦提前接管，本地那条预测记录的
                // 「假弹存活」位置闸门就没了，在途的旧 snapshot 会直接覆写本地预测位置
                //（可见回退 / 丢状态）。因此这里只在镜像已在本端存在时才接管。
                if (MirrorObjectExists(decision.Key))
                {
                    TakeOverFake(fake, decision.Key);
                }
            }
        }

        /// <summary>该 key 是否已经有真实的权威镜像副本（对象在手，而不只是有一条快照）。</summary>
        private bool MirrorObjectExists(PMProjectileKey key)
        {
            MirrorEntry entry;
            return _mirrors.TryGetValue(key, out entry) && entry.Obj != null;
        }

        private void RevokeFake(FakeEntry fake, PMProjectileKey key)
        {
            if (fake.Revoked) { return; }

            fake.Revoked = true;
            _fakes.Remove(key);
            fake.TakenOver = false;

            // 该 key 已死：上行 Verify 账一并退休（否则它会留到该 owner 离开会话）。
            _uplinkVerifyUsed.Remove(key);

            try
            {
                // Rejected 必须**真正撤销**预测实例（契约：Rejected 实际撤销预测实例）。
                _coordinator.Lifecycle.Retire(key, _lastWallMs);
            }
            catch (InvalidOperationException)
            {
                // 生命周期已 retired：幂等。
            }

            FakesRevoked++;
        }

        private void TakeOverFake(FakeEntry fake, PMProjectileKey key)
        {
            if (fake.TakenOver) { return; }

            try
            {
                PMProjectileTakeoverOutcome to = _coordinator.Lifecycle.TryTakeoverPredicted(key);
                if (to.Result == PMProjectileTakeoverResult.TakenOver
                    || to.Result == PMProjectileTakeoverResult.AlreadyTakenOver)
                {
                    fake.TakenOver = true;
                    FakesTakenOver++;
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        /// <summary>
        /// **客户端（AP）**：真实撤销某个 activation 的全部本地假弹（R6-B 新增，owner 受限）。
        ///
        /// 契约（net-r6-combat-contract.md「B 整合与宿主」）：客户端收到攻击级拒绝时，
        /// **必须撤销该 activation 对应的假弹**（不能只改 UI / 只改计数 / 只等 R5 自己的逐颗裁决）。
        ///
        /// 语义：
        ///   · 只作用于 `owner` 自己的 key（`Key.OwnerNetId == owner.NetId.Value`），跨 owner 一律不动；
        ///   · 只作用于**尚未被权威接管**的假弹：已接管（`TakenOver`）的那颗由权威镜像驱动，
        ///     它的生命周期归 R5 的镜像/墓碑逻辑，这里不越权销毁；
        ///   · 撤销走 <see cref="RevokeFake"/>（= 生命周期 `Retire` + 退休上行 Verify 账），
        ///     因此迟到的镜像/裁决不会把已撤的假弹复活（终态不倒退）；
        ///   · 幂等：同一 activation 重复调用只会在第一次真的撤到东西。
        ///
        /// 刻意**不**是「只按计数改状态」的空操作：它必须真的把本地账与视图收掉。
        /// </summary>
        /// <returns>本次真实撤销的假弹颗数。</returns>
        public int CancelPredictedActivation(PMR3Player owner, uint activationId, double wallNowMs)
        {
            EnsureThread();
            if (_disposed || _isServer) { return 0; }
            if (owner == null || !owner.NetId.IsValid) { return 0; }
            if (activationId == 0u) { return 0; }

            uint ownerNetId = owner.NetId.Value;

            List<PMProjectileKey> victims = null;
            foreach (KeyValuePair<PMProjectileKey, FakeEntry> kv in _fakes)
            {
                if (kv.Key.OwnerNetId != ownerNetId) { continue; }
                if (kv.Value.ActivationId != activationId) { continue; }
                if (kv.Value.TakenOver) { continue; }

                if (victims == null) { victims = new List<PMProjectileKey>(4); }
                victims.Add(kv.Key);
            }

            if (victims == null) { return 0; }

            int revoked = 0;
            for (int i = 0; i < victims.Count; i++)
            {
                FakeEntry fake;
                if (!_fakes.TryGetValue(victims[i], out fake)) { continue; }

                RevokeFake(fake, victims[i]);
                revoked++;
            }

            if (revoked > 0) { CancelledPredictions += revoked; }
            if (wallNowMs > _lastWallMs) { _lastWallMs = wallNowMs; }
            return revoked;
        }

        // ---------------------------------------------------------------- 客户端：本地假弹运动

        private void AdvanceLocalFakes(int stepMs, double wallNowMs)
        {
            if (stepMs <= 0 || _fakes.Count == 0) { return; }

            // 用 Coordinator 的单步入口推进（同一套 fail-closed hook / 生命周期 / 墓碑语义）；
            // 它不要求权威身份，因此对 ClientPredicted 假弹同样成立。
            List<PMProjectileKey> keys = new List<PMProjectileKey>(_fakes.Count);
            foreach (KeyValuePair<PMProjectileKey, FakeEntry> kv in _fakes)
            {
                keys.Add(kv.Key);
            }

            for (int i = 0; i < keys.Count; i++)
            {
                PMProjectileKey key = keys[i];
                FakeEntry fake;
                if (!_fakes.TryGetValue(key, out fake) || fake.Revoked) { continue; }

                // 已接管的弹由**权威镜像**驱动：本地再推一次就是「双驱动」
                //（表现会同时受到两套运动的影响，最典型的就是本地直线把权威的停止推回去）。
                if (fake.TakenOver) { continue; }

                PMProjectileAdvanceOutcome adv;
                try
                {
                    adv = _coordinator.AdvanceMotion(key, stepMs, wallNowMs);
                }
                catch (InvalidOperationException ex)
                {
                    Fault(PMR5ProjectileFaultReason.CoordinatorFaulted, "AdvanceMotion(AP)：" + ex.Message);
                    return;
                }

                fake.LastMoveTimeMs = adv.MoveTimeMs;

                if (adv.Result == PMProjectileAdvanceResult.Stopped
                    || adv.Result == PMProjectileAdvanceResult.HostMotionFaulted
                    || adv.Result == PMProjectileAdvanceResult.NotMovable)
                {
                    // 假弹运动已到终态：保留 key 作为迟到镜像的墓碑（不复活、不二次停止通知）。
                    _coordinator.Lifecycle.NotifyPredictedEnded(key, wallNowMs, fake.PredictionMs, 0);
                }
                else if (adv.Result != PMProjectileAdvanceResult.Advanced)
                {
                    // UnknownKey / Retired / StaleEpoch：本地假弹已不在账本里 ⇒ 移除视图。
                    _fakes.Remove(key);
                }
            }
        }

        // ---------------------------------------------------------------- 客户端：本地退休与有界清理

        /// <summary>
        /// 客户端每帧的**有界收拾**（R5-B2c 返工）：
        ///
        ///   1) 驱动一次生命周期 TTL/墓碑清理 —— 客户端过去从不调 `PurgeExpired`，
        ///      于是「已经飞完的 key」永远留在账本与视图里（幽灵弹留到对局结束）；
        ///   2) 排空本地账本产生的释放项（客户端不向下行发裁决，但队列必须有界，
        ///      否则 `EnsureOutputHeadroom` 会因出口满而 fault 整个会话）；
        ///   3) 退休已经彻底结束的 key（生命周期不再登记 + 没有活镜像对象）。
        /// </summary>
        private void SweepLocalState(double wallNowMs)
        {
            try
            {
                _coordinator.PurgeExpired(wallNowMs);
            }
            catch (InvalidOperationException ex)
            {
                Fault(PMR5ProjectileFaultReason.CoordinatorFaulted, "PurgeExpired(AP)：" + ex.Message);
                return;
            }

            // 客户端本地的释放项只用于「撤销假弹」（已由裁决路径处理），这里只需有界排空。
            PMProjectileActivationRelease[] releases;
            int drained = _coordinator.DrainDecisions(MaxApplyPerPump, out releases);
            if (drained > 0) { ClientReleasesDrained += drained; }

            SweepRetiredLocalKeys();
        }

        /// <summary>
        /// 退休「已经彻底结束」的本地 key：生命周期里已不再登记，且没有活着的权威镜像对象。
        /// 两者都成立才清理，是为了不把「权威本体还在、只是本地登记到期」的弹错误地摘掉。
        /// </summary>
        private void SweepRetiredLocalKeys()
        {
            if (_fakes.Count > 0)
            {
                List<PMProjectileKey> done = null;
                foreach (KeyValuePair<PMProjectileKey, FakeEntry> kv in _fakes)
                {
                    PMProjectileRegistration reg;
                    if (_coordinator.TryObserveRegistration(kv.Key, out reg)) { continue; }

                    MirrorEntry mirror;
                    if (_mirrors.TryGetValue(kv.Key, out mirror) && MirrorObjectAlive(mirror)) { continue; }

                    if (done == null) { done = new List<PMProjectileKey>(4); }
                    done.Add(kv.Key);
                }

                if (done != null)
                {
                    for (int i = 0; i < done.Count; i++)
                    {
                        if (_fakes.Remove(done[i])) { FakesRetired++; }
                        _uplinkVerifyUsed.Remove(done[i]);
                    }
                }
            }

            if (_mirrors.Count > 0)
            {
                List<PMProjectileKey> gone = null;
                foreach (KeyValuePair<PMProjectileKey, MirrorEntry> kv in _mirrors)
                {
                    if (MirrorObjectAlive(kv.Value)) { continue; }
                    if (gone == null) { gone = new List<PMProjectileKey>(4); }
                    gone.Add(kv.Key);
                }

                if (gone != null)
                {
                    for (int i = 0; i < gone.Count; i++)
                    {
                        MirrorEntry entry = _mirrors[gone[i]];
                        bool wasDriving = entry.Applied;
                        _mirrors.Remove(gone[i]);

                        // 权威本体已经不存在：本地那条预测记录与展示必须同步退休，
                        // 绝不让「Destroy 之后假弹复活」。
                        if (wasDriving) { RetireLocalKey(gone[i]); }
                    }
                }
            }
        }

        private bool MirrorObjectAlive(MirrorEntry entry)
        {
            if (entry == null || entry.Obj == null) { return false; }
            PMNetObject live;
            return _world.TryFind(entry.Obj.NetId.Value, out live) && live != null;
        }

        // ---------------------------------------------------------------- DS：权威运动与快照

        private void AdvanceAuthorityMotion(int stepMs, double wallNowMs)
        {
            if (_authorityObjects.Count == 0) { return; }

            List<PMProjectileKey> keys = new List<PMProjectileKey>(_authorityObjects.Count);
            foreach (KeyValuePair<PMProjectileKey, AuthorityEntry> kv in _authorityObjects)
            {
                keys.Add(kv.Key);
            }

            for (int i = 0; i < keys.Count; i++)
            {
                PMProjectileKey key = keys[i];
                AuthorityEntry entry;
                if (!_authorityObjects.TryGetValue(key, out entry) || entry.Destroyed) { continue; }

                if (stepMs > 0)
                {
                    try
                    {
                        _coordinator.AdvanceMotion(key, stepMs, wallNowMs);
                    }
                    catch (InvalidOperationException ex)
                    {
                        Fault(PMR5ProjectileFaultReason.CoordinatorFaulted, "AdvanceMotion(DS)：" + ex.Message);
                        return;
                    }
                }

                PublishAuthoritySnapshot(entry);
            }
        }

        private void PublishAuthoritySnapshot(AuthorityEntry entry)
        {
            PMProjectileSpec spec;
            PMProjectileState state;
            if (!_coordinator.TryObserveFrozen(entry.Key, out spec, out state) || state == null) { return; }

            if (state.Stopped && state.TombstoneUntilMs > entry.TombstoneUntilMs)
            {
                entry.TombstoneUntilMs = state.TombstoneUntilMs;
            }

            PMProjectileSnapshot snapshot = new PMProjectileSnapshot();
            snapshot.State = state;
            snapshot.Spec = spec;

            byte[] payload;
            string error;
            if (!PMProjectileCodec.TryEncodeSnapshot(snapshot, out payload, out error) || payload == null)
            {
                return;
            }

            if (entry.LastPublished != null && ByteEquals(entry.LastPublished, payload)) { return; }

            entry.LastPublished = payload;
            entry.Spec = spec;
            entry.Obj.PublishProjectileSnapshot(payload);
        }

        // ---------------------------------------------------------------- DS：生成出口

        private void DrainSpawnReady(double wallNowMs)
        {
            PMProjectileSpawnEvent[] events;
            int count = _coordinator.DrainSpawnReady(MaxApplyPerPump, out events);
            for (int i = 0; i < count; i++)
            {
                PMProjectileSpawnEvent e = events[i];

                PMNetSpawnReservation token;
                if (!_reservations.TryGetValue(e.Key, out token) || token == null)
                {
                    Fault(PMR5ProjectileFaultReason.InternalInvariant,
                        "SpawnReady 找不到该 key 的 NetId 预留令牌（key=" + DescribeKey(e.Key) + "）");
                    return;
                }

                PMR3Player owner;
                _playersByOwnerNetId.TryGetValue(e.OwnerNetId, out owner);

                // 契约：**权威注册先于追赶**，而且追赶必须在「发布初始 snapshot」之前完成。
                // `PMProjectileSpawnEvent.State` 是发那一刻的旧值：拿它当线上初值，
                // 一颗本应已经飞过半空的弹会被钉在枪口上，客户端接管瞬间就会回退。
                int catchUpMs = 0;
                try
                {
                    PMProjectileAdvanceOutcome catchUp =
                        _coordinator.CatchUpRegisteredMotion(e.Key, wallNowMs);
                    catchUpMs = catchUp.AppliedMs;
                }
                catch (InvalidOperationException ex)
                {
                    _world.CancelReservedNetId(token);
                    _reservations.Remove(e.Key);
                    ReservationsCancelled++;
                    Fault(PMR5ProjectileFaultReason.CoordinatorFaulted, "CatchUpRegisteredMotion：" + ex.Message);
                    return;
                }

                CatchUpsApplied++;

                // 追赶后**重新**读冻结状态：这才是要上线的初值。
                PMProjectileSpec frozenSpec;
                PMProjectileState frozenState;
                if (!_coordinator.TryObserveFrozen(e.Key, out frozenSpec, out frozenState) || frozenState == null)
                {
                    _world.CancelReservedNetId(token);
                    _reservations.Remove(e.Key);
                    ReservationsCancelled++;
                    Fault(PMR5ProjectileFaultReason.InternalInvariant,
                        "SpawnReady 后取不到冻结状态（key=" + DescribeKey(e.Key) + "）：拒绝发布假初值");
                    return;
                }

                PMProjectileSnapshot snapshot = new PMProjectileSnapshot();
                snapshot.State = frozenState;
                snapshot.Spec = frozenSpec != null ? frozenSpec : e.Spec;

                byte[] payload;
                string error;
                if (!PMProjectileCodec.TryEncodeSnapshot(snapshot, out payload, out error) || payload == null)
                {
                    _world.CancelReservedNetId(token);
                    _reservations.Remove(e.Key);
                    ReservationsCancelled++;
                    continue;
                }

                PMR5Projectile obj = new PMR5Projectile();

                // 权威对象的 owner 绑定真实玩家：复制层的 IsOwner 判定走
                // `obj.GetNetConnection()`（PMR5Projectile 未覆写 ⇒ 沿 Owner 链到 player 的 OwnerConnection），
                // 因此这里必须把 Owner 指向真实副本；上行 RPC 的归属校验也据此一致。
                if (owner != null)
                {
                    obj.Owner = owner;
                    obj.OwnerConnection = owner.OwnerConnection;
                }

                // 初始 snapshot **先写好再上线**（契约：Pending 期间无客户端幽灵对象，
                // Create 记录里的初值是发那一刻现取的）。
                obj.PublishProjectileSnapshot(payload);

                if (!_world.SpawnReserved(obj, token, PMR5Projectile.PMGeneratedClassId))
                {
                    _world.CancelReservedNetId(token);
                    _reservations.Remove(e.Key);
                    ReservationsCancelled++;
                    continue;
                }

                _reservations.Remove(e.Key);
                _bridge.RegisterReplicatedObject(obj);

                AuthorityEntry entry = new AuthorityEntry();
                entry.Key = e.Key;
                entry.Obj = obj;
                entry.Spec = snapshot.Spec;
                entry.LastPublished = payload;
                entry.CatchUpMs = catchUpMs;
                _authorityObjects[e.Key] = entry;
                ServerSpawnsSpawned++;
            }
        }

        private void DestroyExpiredTombstones(double wallNowMs)
        {
            if (_authorityObjects.Count == 0) { return; }

            List<PMProjectileKey> expired = null;

            foreach (KeyValuePair<PMProjectileKey, AuthorityEntry> kv in _authorityObjects)
            {
                AuthorityEntry entry = kv.Value;
                if (entry.Destroyed) { continue; }
                if (entry.TombstoneUntilMs <= 0.0 || wallNowMs < entry.TombstoneUntilMs) { continue; }

                // 尽量补一次最终 stopped 快照（生命周期可能已退役，那就跳过）。
                PublishAuthoritySnapshot(entry);

                if (expired == null) { expired = new List<PMProjectileKey>(4); }
                expired.Add(kv.Key);
            }

            if (expired == null) { return; }

            for (int i = 0; i < expired.Count; i++)
            {
                AuthorityEntry entry = _authorityObjects[expired[i]];
                entry.Destroyed = true;
                _authorityObjects.Remove(expired[i]);

                // 墓碑到期：世界销毁 + 复制注销（真实 API，不假造 ID、不手工广播）。
                _bridge.DestroyObject(entry.Obj);
                AuthorityObjectsDestroyed++;
            }
        }

        /// <summary>
        /// 取消「已不再挂起、也从未上线」的预留（TTL 到期 / 被裁决拒绝 / 队列清理）。
        /// 契约：拒绝/TTL/dispose 一律 Cancel，绝不留下悬空编号。
        /// </summary>
        private void SweepStaleReservations()
        {
            if (_reservations.Count == 0) { return; }

            // 先让 Coordinator 的 TTL（挂起 Spawn 2000ms）有机会触发。
            try
            {
                _coordinator.PurgeExpired(_lastWallMs);
            }
            catch (InvalidOperationException ex)
            {
                Fault(PMR5ProjectileFaultReason.CoordinatorFaulted, "PurgeExpired：" + ex.Message);
                return;
            }

            List<PMProjectileKey> stale = null;
            foreach (KeyValuePair<PMProjectileKey, PMNetSpawnReservation> kv in _reservations)
            {
                if (_coordinator.IsSpawnPending(kv.Key) || _coordinator.IsSpawned(kv.Key)) { continue; }
                if (stale == null) { stale = new List<PMProjectileKey>(4); }
                stale.Add(kv.Key);
            }

            if (stale == null) { return; }

            for (int i = 0; i < stale.Count; i++)
            {
                PMNetSpawnReservation token = _reservations[stale[i]];
                _reservations.Remove(stale[i]);
                _world.CancelReservedNetId(token);
                ReservationsCancelled++;
            }
        }

        // ---------------------------------------------------------------- 出口

        private void DrainDecisions(double wallNowMs)
        {
            if (!_isServer) { return; }

            PMProjectileActivationRelease[] releases;
            int count = _coordinator.DrainDecisions(MaxApplyPerPump, out releases);
            for (int i = 0; i < count; i++)
            {
                PMProjectileActivationRelease release = releases[i];
                if (release.Reason == PMProjectileReleaseReason.ActivationConfirmed)
                {
                    SendDecisionFor(release.OwnerNetId, release.Key, release.ActivationId,
                        PMActivationResult.Confirmed, "confirmed");
                }
                else if (release.Reason == PMProjectileReleaseReason.ActivationRejected)
                {
                    SendDecisionFor(release.OwnerNetId, release.Key, release.ActivationId,
                        PMActivationResult.Rejected, "rejected");
                }
                // TombstoneExpired / OwnerCleared：不向客户端发终态裁决（客户端本地清理即可）。
            }
        }

        private void DrainCoordinatorOutlets()
        {
            // 停止出口：DS 用它确认「停止 snapshot 已发布」；客户端用它刷新视图（无网络语义）。
            PMProjectileStopEvent[] stops;
            int stopCount = _coordinator.DrainStopped(MaxApplyPerPump, out stops);
            for (int i = 0; i < stopCount; i++)
            {
                PMProjectileStopEvent stop = stops[i];
                AuthorityEntry entry;
                if (_authorityObjects.TryGetValue(stop.Key, out entry))
                {
                    PublishAuthoritySnapshot(entry);
                }
            }

            // 结算出口：**R6 的唯一消费点**。本驱动不扣血、不解释伤害。
            PMProjectileSettlement[] settlements;
            int settlementCount = _coordinator.DrainSettlements(MaxApplyPerPump, out settlements);
            for (int i = 0; i < settlementCount; i++)
            {
                Action<PMProjectileSettlement> sink = Settlement;
                if (sink != null)
                {
                    bool delivered = false;
                    try
                    {
                        sink(settlements[i]);
                        delivered = true;
                    }
                    catch (Exception)
                    {
                        delivered = false;
                    }

                    if (delivered)
                    {
                        SettlementsHandedToR6++;
                        continue;
                    }
                }

                // 没有消费者（或消费者抛异常）：**绝不能静默丢真实伤害**。
                // 进有界待取缓冲等宿主 Drain；缓冲也满 ⇒ 会话级 fault。
                if (_pendingSettlements.Count >= MaxPendingSettlements)
                {
                    Fault(PMR5ProjectileFaultReason.SettlementUnconsumed,
                        "结算出口没有消费者且待取缓冲已满（" + _pendingSettlements.Count + "/"
                        + MaxPendingSettlements + "）：继续就是丢掉真实伤害，拒绝继续运行");
                    return;
                }

                _pendingSettlements.Add(settlements[i]);
                SettlementsQueued++;
                SettlementsWithNoConsumer++;
            }
        }

        private void SendDecisionFor(uint ownerNetId, PMProjectileKey key, uint activationId,
            PMActivationResult result, string reason)
        {
            if (!key.IsValid || activationId == 0u) { return; }

            PMR3Player player;
            if (!_playersByOwnerNetId.TryGetValue(ownerNetId, out player) || player == null) { return; }

            PMProjectileDecision decision = new PMProjectileDecision();
            decision.Key = key;
            decision.ActivationId = activationId;
            decision.Result = result;
            decision.Reason = TruncateReason(reason);

            byte[] payload;
            string error;
            if (!PMProjectileCodec.TryEncodeDecision(decision, out payload, out error) || payload == null)
            {
                return;
            }

            try
            {
                // 唯一发送入口 = 声明层入口（普通名 ClientProjectileDecisionV1，下行裁决）。失败会抛，驱动转成会话 fault。
                SendClientProjectileDecisionV1(player, payload);
                DecisionsSent++;
            }
            catch (Exception ex)
            {
                Fault(PMR5ProjectileFaultReason.SendFailed,
                    "ClientProjectileDecisionV1 发送失败：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        private static string TruncateReason(string reason)
        {
            if (string.IsNullOrEmpty(reason)) { return string.Empty; }
            return reason.Length <= 64 ? reason : reason.Substring(0, 64);
        }

        // ---------------------------------------------------------------- 客户端：镜像

        private void ApplyMirrors(double wallNowMs)
        {
            if (_isServer) { return; }
            if (_inboundMirrors.Count == 0) { return; }

            int limit = _inboundMirrors.Count < MaxApplyPerPump ? _inboundMirrors.Count : MaxApplyPerPump;
            for (int i = 0; i < limit; i++)
            {
                MirrorWake wake = _inboundMirrors[0];
                _inboundMirrors.RemoveAt(0);

                if (wake.Destroyed)
                {
                    RemoveMirrorByObject(wake.Object);
                    continue;
                }

                ApplyMirrorObject(wake.Object, wallNowMs);
            }
        }

        private void RemoveMirrorByObject(PMR5Projectile obj)
        {
            if (obj == null) { return; }

            uint netId = obj.NetId.Value;
            PMProjectileKey found = default(PMProjectileKey);
            bool any = false;

            foreach (KeyValuePair<PMProjectileKey, MirrorEntry> kv in _mirrors)
            {
                if (kv.Value.Obj != null && kv.Value.Obj.NetId.Value == netId)
                {
                    found = kv.Key;
                    any = true;
                    break;
                }
            }

            if (!any) { return; }

            MirrorEntry entry = _mirrors[found];
            bool wasDriving = entry.Applied;
            _mirrors.Remove(found);

            // 权威本体被销毁：本地的预测记录与展示同步退休。
            // 这一步就是「Destroy 会不会把假弹复活」的答案：不会复活，而且必须消失。
            if (wasDriving) { RetireLocalKey(found); }
        }

        /// <summary>
        /// 退休一个本地 key：移除假弹登记 + 忘掉上行 Verify 账 + 把生命周期登记置为终态。
        /// 终态置位后，迟到的镜像/裁决都不会把它复活（终态不倒退）。
        /// </summary>
        private void RetireLocalKey(PMProjectileKey key)
        {
            if (_fakes.Remove(key)) { FakesRetired++; }
            _uplinkVerifyUsed.Remove(key);

            try
            {
                _coordinator.Lifecycle.Retire(key, _lastWallMs);
            }
            catch (InvalidOperationException)
            {
                // 账本已 faulted：退休本身不再有意义，但绝不能把异常冒到 Pump。
            }
        }

        private void ApplyMirrorObject(PMR5Projectile obj, double wallNowMs)
        {
            if (obj == null) { return; }

            byte[] payload = obj.ProjectileSnapshotPayload;
            if (payload == null || payload.Length == 0) { return; }

            PMProjectileSnapshot snapshot;
            string error;
            if (!PMProjectileCodec.TryDecodeSnapshot(payload, out snapshot, out error) || snapshot == null)
            {
                MirrorIdentityRejected++;
                return;
            }

            PMProjectileState state = snapshot.State;
            if (state == null || !state.Key.IsValid)
            {
                MirrorIdentityRejected++;
                return;
            }

            // 契约：Create + OnRep 读取 codec 快照后必须验证 Epoch + AuthorityNetId == obj.NetId + owner。
            if (state.Key.Epoch != _epoch
                || state.AuthorityNetId == 0u
                || state.AuthorityNetId != obj.NetId.Value
                || state.Key.OwnerNetId == 0u)
            {
                MirrorIdentityRejected++;
                return;
            }

            PMR3Player owner;
            if (!_playersByOwnerNetId.TryGetValue(state.Key.OwnerNetId, out owner) || owner == null)
            {
                // 契约：收包前未绑 player 的镜像暂存（上限 + TTL），不能永久丢掉初始状态。
                StageUnboundMirror(state.Key, state, snapshot.Spec, wallNowMs);
                return;
            }

            ApplyMirrorPayload(state.Key, obj, state, snapshot.Spec, wallNowMs);
        }

        private void ApplyMirrorPayload(PMProjectileKey key, PMR5Projectile obj, PMProjectileState state,
            PMProjectileSpec spec, double wallNowMs)
        {
            MirrorEntry entry;
            if (!_mirrors.TryGetValue(key, out entry))
            {
                entry = new MirrorEntry();
                entry.Key = key;
                _mirrors.Add(key, entry);
            }

            entry.Obj = obj;
            entry.Spec = spec;
            if (state.AuthorityNetId != 0u) { entry.AuthorityNetId = state.AuthorityNetId; }

            FakeEntry fake;
            bool liveFake = _fakes.TryGetValue(key, out fake) && !fake.Revoked && !fake.TakenOver;

            // 接管 = **延续假弹自己的运动时基**，而不是被在途的旧 snapshot 拽回去。
            // 所以高水位先采纳假弹当前时基，再判定本快照是否旧。
            if (liveFake)
            {
                if (fake.LastMoveTimeMs > entry.HighWaterMoveTimeMs)
                {
                    entry.HighWaterMoveTimeMs = fake.LastMoveTimeMs;
                }
            }

            bool stalePosition = state.MoveTimeMs < entry.HighWaterMoveTimeMs;
            if (stalePosition && !state.Stopped && !state.Hidden)
            {
                // 在途的旧 mirror：位置/运动时基**不写**（高水位），也不产生任何状态倒退。
                // 注意：停止/隐藏是状态转移，不受本闸门限制 —— 否则真实停止会被旧时基永远挡住。
                MirrorStaleDropped++;
            }
            else
            {
                bool wroteLedger = false;
                try
                {
                    PMProjectileMirrorOutcome mirror = _coordinator.Lifecycle.TryApplyMirror(key, state, wallNowMs);

                    if (mirror.Result == PMProjectileMirrorResult.GateFakeAlive)
                    {
                        MirrorPositionGated++;
                    }
                    else if (mirror.Result == PMProjectileMirrorResult.HiddenNoRevive)
                    {
                        MirrorHiddenNoRevive++;
                        wroteLedger = true;
                    }
                    else if (mirror.Result == PMProjectileMirrorResult.Applied
                             || mirror.Result == PMProjectileMirrorResult.StoppedNotMoved
                             || mirror.Result == PMProjectileMirrorResult.UnknownKey)
                    {
                        // UnknownKey 是 **SP 的正常路径**：该 key 的预测登记在 owner 客户端，
                        // SP 的本地账本里根本没有它，权威镜像就是它唯一的状态来源（不是失败）。
                        MirrorPayloadsApplied++;
                        wroteLedger = true;
                    }
                }
                catch (InvalidOperationException)
                {
                }

                if (wroteLedger)
                {
                    if (!stalePosition && state.MoveTimeMs > entry.HighWaterMoveTimeMs)
                    {
                        entry.HighWaterMoveTimeMs = state.MoveTimeMs;
                    }

                    entry.State = state;
                    entry.Applied = true;
                }
            }

            // 权威副本已经真实存在 ⇒ 完成**一次性**接管（无双弹）。
            // 接管本身就把「后续表现由权威驱动」定下了，因此它也算「权威已驱动本 key」。
            if (liveFake)
            {
                TakeOverFake(fake, key);
                if (fake.TakenOver) { entry.Applied = true; }
            }
        }

        private void StageUnboundMirror(PMProjectileKey key, PMProjectileState state, PMProjectileSpec spec,
            double wallNowMs)
        {
            if (_unboundMirrors.Count >= MaxUnboundMirrors)
            {
                _unboundMirrors.RemoveAt(0);
                UnboundMirrorsDropped++;
            }

            UnboundMirror staged = new UnboundMirror();
            staged.Key = key;
            staged.AuthorityNetId = state.AuthorityNetId;
            staged.Spec = spec;
            staged.State = state;
            staged.QueuedWallMs = wallNowMs;
            _unboundMirrors.Add(staged);
            UnboundMirrorsStaged++;
        }

        private void ReplayUnboundMirrorsFor(uint ownerNetId)
        {
            if (_unboundMirrors.Count == 0) { return; }

            for (int i = _unboundMirrors.Count - 1; i >= 0; i--)
            {
                UnboundMirror staged = _unboundMirrors[i];
                if (staged.Key.OwnerNetId != ownerNetId) { continue; }

                _unboundMirrors.RemoveAt(i);

                PMR5Projectile obj = FindMirrorObject(staged.AuthorityNetId);
                if (obj == null)
                {
                    UnboundMirrorsDropped++;
                    continue;
                }

                ApplyMirrorPayload(staged.Key, obj, staged.State, staged.Spec, _lastWallMs);
            }
        }

        private PMR5Projectile FindMirrorObject(uint authorityNetId)
        {
            PMNetObject obj;
            if (!_world.TryFind(authorityNetId, out obj) || obj == null)
            {
                return null;
            }

            return obj as PMR5Projectile;
        }

        private void ApplyUnboundMirrorTtl(double wallNowMs)
        {
            if (_unboundMirrors.Count == 0) { return; }

            for (int i = _unboundMirrors.Count - 1; i >= 0; i--)
            {
                if (wallNowMs - _unboundMirrors[i].QueuedWallMs < UnboundMirrorTtlMs) { continue; }
                _unboundMirrors.RemoveAt(i);
                UnboundMirrorsExpired++;
            }
        }

        // ================================================================ 客户端：TryFire

        /// <summary>
        /// AP 本地开火：分配每 owner 单 epoch 单调 ID → 真实 Lifecycle 预测登记 →
        /// 本地直线运动（由 Pump 按 ≤50ms 步推进）→ 唯一发送入口发出上行生成 RPC。
        ///
        /// 说明：`activationId` 必须非 0（0 只允许可信 ServerDirect）；本地 `spec` 是**本机配置**，
        /// 不是从 payload 采信（上行 payload 根本不携带 spec）。
        /// </summary>
        public bool TryFire(
            PMR3Player player,
            uint activationId,
            PMVector3 position,
            PMVector3 direction,
            float yaw,
            PMProjectileSpec localSpec,
            int predictionMs,
            double wallNowMs,
            out PMProjectileKey key,
            out string error)
        {
            key = default(PMProjectileKey);
            error = null;

            EnsureThread();
            if (_disposed) { error = "driver disposed"; return false; }
            if (_faulted) { error = "driver faulted: " + FaultError; return false; }
            if (_isServer) { error = "TryFire 只适用于客户端 AP"; return false; }
            if (player == null || !player.NetId.IsValid) { error = "player/NetId 无效"; return false; }

            PMR3Player localOwner = LocalOwner;
            if (localOwner == null || !ReferenceEquals(localOwner, player))
            {
                error = "本副本不是本地 owner（AP）";
                return false;
            }

            if (activationId == 0u) { error = "activationId 0 只允许可信 ServerDirect"; return false; }
            if (localSpec == null || !IsFiniteSpec(localSpec))
            {
                error = "本地 spec 无效";
                return false;
            }

            if (!IsFiniteV(position) || !IsFiniteV(direction)
                || direction.LengthSquared <= PMVector3.Epsilon || !IsFiniteF(yaw))
            {
                error = "位置/方向/朝向非法";
                return false;
            }

            float canonicalYaw;
            if (!PMProjectileCodec.TryNormalizeYaw(yaw, out canonicalYaw)) { canonicalYaw = 0f; }

            if (predictionMs < PMProjectileCodec.MinPredictionMs || predictionMs > PMProjectileCodec.MaxPredictionMs)
            {
                error = "predictionMs 超出 [0,500]";
                return false;
            }

            uint ownerNetId = player.NetId.Value;

            // 有界性：每 owner 一个单调 ID 游标。游标**刻意不重置**（重置就等于允许 ID 复用，
            // 服务端的高水位会直接拒掉后续全部请求），因此这里只能限制 owner 数量：
            // 超过契约的每会话 owner 上限时明确拒新，而不是静默丢弃或让表无界增长。
            if (!_nextProjectileId.ContainsKey(ownerNetId)
                && _nextProjectileId.Count >= PMProjectileLimits.MaxOwners)
            {
                error = "owner 流容量已满（" + PMProjectileLimits.MaxOwners + "）：拒绝新的预测量";
                return false;
            }

            uint next = NextProjectileId(ownerNetId);
            if (next == 0u) { error = "projectileId 空间耗尽"; return false; }

            PMProjectileKey candidate = new PMProjectileKey(_epoch, ownerNetId, next,
                PMProjectileOrigin.ClientPredicted);

            PMProjectileSpec spec = localSpec.Clone();

            PMProjectileState state = new PMProjectileState();
            state.Key = candidate;
            state.ActivationId = activationId;
            state.SpawnPosition = position;
            state.PreviousPosition = position;
            state.Position = position;
            state.Velocity = PMVector3.Normalized(direction) * spec.SpeedMps;
            state.Yaw = canonicalYaw;
            state.MoveTimeMs = 0.0;

            PMProjectileRegisterOutcome reg;
            try
            {
                // 契约：**先真实 Lifecycle 预测登记**，再发上行生成 RPC。
                reg = _coordinator.Lifecycle.TryRegisterPredicted(ownerNetId, next, activationId,
                    state, spec, wallNowMs);
            }
            catch (InvalidOperationException ex)
            {
                Fault(PMR5ProjectileFaultReason.CoordinatorFaulted, "TryRegisterPredicted：" + ex.Message);
                error = ex.Message;
                return false;
            }

            if (reg.Result != PMProjectileRegisterResult.Registered)
            {
                error = "预测登记被拒：" + reg.Result;
                return false;
            }

            // 上行 payload：**只含非权威字段**（codec 的 SpawnIntent DTO 根本表达不了 spec / 权威 ID）。
            PMProjectileSpawnIntent intent = new PMProjectileSpawnIntent();
            intent.Key = candidate;
            intent.ActivationId = activationId;
            intent.Position = position;
            intent.Direction = direction;
            intent.Yaw = canonicalYaw;
            intent.PredictionMs = predictionMs;

            byte[] payload;
            string encodeError;
            if (!PMProjectileCodec.TryEncodeSpawnIntent(intent, out payload, out encodeError) || payload == null)
            {
                _coordinator.Lifecycle.Retire(candidate, wallNowMs);
                error = "上行生成编码失败：" + encodeError;
                return false;
            }

            FakeEntry fake = new FakeEntry();
            fake.Key = candidate;
            fake.Spec = spec;
            fake.PredictionMs = predictionMs;
            fake.ActivationId = activationId;
            _fakes[candidate] = fake;
            FakesCreated++;

            try
            {
                SendServerProjectileSpawnV1(player, payload);
            }
            catch (Exception ex)
            {
                RevokeFake(fake, candidate);
                Fault(PMR5ProjectileFaultReason.SendFailed,
                    "ServerProjectileSpawnV1 发送失败：" + ex.GetType().Name + " " + ex.Message);
                error = "上行生成发送失败：" + ex.Message;
                key = candidate;
                return false;
            }

            key = candidate;
            return true;
        }

        private uint NextProjectileId(uint ownerNetId)
        {
            uint current;
            if (!_nextProjectileId.TryGetValue(ownerNetId, out current)) { current = 0u; }
            if (current == uint.MaxValue) { return 0u; }

            current = current + 1u;
            _nextProjectileId[ownerNetId] = current;
            return current;
        }

        // ================================================================ 客户端：上行命中

        /// <summary>
        /// AP 上报预测命中：codec 分 ≤48 目标一条 RPC；发送前检查「需要几条 RPC」与
        /// **该 key 剩余 Verify 总配额（每 key 5，每条 RPC 仍占 1 次，不重置、不超过）**；
        /// 任一编码结果 &gt; 4096 字节则整次发送**明确拒绝**；发送中任何失败 = 会话 fault。
        /// </summary>
        public bool SubmitPredictedHits(
            PMR3Player player,
            PMProjectileHitBatch batch,
            double wallNowMs,
            out int rpcCount,
            out string error)
        {
            rpcCount = 0;
            error = null;

            EnsureThread();
            if (_disposed) { error = "driver disposed"; return false; }
            if (_faulted) { error = "driver faulted: " + FaultError; return false; }
            if (_isServer) { error = "SubmitPredictedHits 只适用于客户端 AP"; return false; }
            if (player == null || batch == null || !batch.Key.IsValid) { error = "player/batch 无效"; return false; }

            PMR3Player localOwner = LocalOwner;
            if (localOwner == null || !ReferenceEquals(localOwner, player))
            {
                error = "本副本不是本地 owner（AP）";
                return false;
            }

            if (batch.Key.Epoch != _epoch || batch.Key.OwnerNetId != player.NetId.Value)
            {
                error = "batch 身份与本端不一致";
                return false;
            }

            int targets = batch.Targets == null ? 0 : batch.Targets.Length;
            if (targets == 0) { error = "空命中批"; return false; }
            if (targets > PMProjectileLimits.MaxTargets)
            {
                error = "目标数超过 " + PMProjectileLimits.MaxTargets;
                return false;
            }

            int needed = (targets + MaxTargetsPerHitRpc - 1) / MaxTargetsPerHitRpc;

            int used = UplinkVerifyUsed(batch.Key);
            if (used + needed > MaxVerifyPerKey)
            {
                error = "Verify 配额不足：已用 " + used + "，本次需要 " + needed + "，上限 " + MaxVerifyPerKey;
                return false;
            }

            // 先全部编码（>4096 明确拒，且不做半发送），再统一发出。
            byte[][] payloads = new byte[needed][];
            for (int i = 0; i < needed; i++)
            {
                int offset = i * MaxTargetsPerHitRpc;
                int take = targets - offset;
                if (take > MaxTargetsPerHitRpc) { take = MaxTargetsPerHitRpc; }

                PMProjectileHitCandidate[] slice = new PMProjectileHitCandidate[take];
                Array.Copy(batch.Targets, offset, slice, 0, take);

                PMProjectileHitBatch chunk = new PMProjectileHitBatch();
                chunk.Key = batch.Key;
                chunk.PreviousPosition = batch.PreviousPosition;
                chunk.HitPosition = batch.HitPosition;
                chunk.RewindMs = batch.RewindMs;
                chunk.Targets = slice;

                byte[] payload;
                string encodeError;
                if (!PMProjectileCodec.TryEncodeHitBatch(chunk, out payload, out encodeError) || payload == null)
                {
                    error = "命中批编码失败：" + encodeError;
                    return false;
                }

                if (payload.Length > PMR3Player.ProjectileMaxPayloadBytes)
                {
                    error = "命中批分包 " + payload.Length + " 字节超过生成桩上限 "
                            + PMR3Player.ProjectileMaxPayloadBytes;
                    return false;
                }

                payloads[i] = payload;
            }

            for (int i = 0; i < payloads.Length; i++)
            {
                try
                {
                    SendServerProjectileHitV1(player, payloads[i]);
                }
                catch (Exception ex)
                {
                    // 契约：发送中任何失败都不能吞 —— 直接会话 fault。
                    Fault(PMR5ProjectileFaultReason.SendFailed,
                        "ServerProjectileHitV1 发送失败（第 " + (i + 1) + "/" + payloads.Length + " 包）："
                        + ex.GetType().Name + " " + ex.Message);
                    error = "上行命中发送失败：" + ex.Message;
                    return false;
                }
            }

            _uplinkVerifyUsed[batch.Key] = used + needed;
            rpcCount = needed;
            HitRpcsSent += needed;

            if (wallNowMs > _lastWallMs) { _lastWallMs = wallNowMs; }
            return true;
        }

        /// <summary>该 key 已消耗的上行 Verify 次数（每条 RPC 记 1 次）。</summary>
        public int UplinkVerifyUsed(PMProjectileKey key)
        {
            int used;
            return _uplinkVerifyUsed.TryGetValue(key, out used) ? used : 0;
        }

        // ================================================================ 显式权威入口（DS 宿主）

        /// <summary>
        /// 权威激活裁决的**显式后续入口**（契约：Pending 不在生成时兑现，而由宿主显式裁决）。
        /// 返回 true 表示账本接受了本次写入（幂等/终态不倒退时也返回 true 并回报结果）。
        /// </summary>
        public bool ResolveActivation(uint ownerNetId, uint activationId, PMActivationResult outcome,
            double wallNowMs, out PMProjectileActivationApplyResult result)
        {
            result = PMProjectileActivationApplyResult.Invalid;

            EnsureThread();
            if (_disposed || _faulted || !_isServer) { return false; }

            try
            {
                PMProjectileActivationApplyOutcome o =
                    _coordinator.ResolveActivation(ownerNetId, activationId, outcome, wallNowMs);
                result = o.Result;
                return true;
            }
            catch (InvalidOperationException ex)
            {
                Fault(PMR5ProjectileFaultReason.CoordinatorFaulted, "ResolveActivation：" + ex.Message);
                return false;
            }
        }

        private void ResolveActivationInternal(uint ownerNetId, uint activationId, PMActivationResult outcome,
            double wallNowMs)
        {
            try
            {
                _coordinator.ResolveActivation(ownerNetId, activationId, outcome, wallNowMs);
            }
            catch (InvalidOperationException ex)
            {
                Fault(PMR5ProjectileFaultReason.CoordinatorFaulted, "ResolveActivation：" + ex.Message);
            }
        }

        /// <summary>
        /// 可信 ServerDirect 生成（权威侧独立入口；只有它能用 ActivationId=0）。
        /// 预留真实 NetId、走 Coordinator 的权威登记入口，再经生成出口上线。
        /// </summary>
        public bool TryServerDirectSpawn(
            uint ownerNetId,
            uint projectileId,
            uint activationId,
            PMVector3 position,
            PMVector3 direction,
            float yaw,
            PMProjectileSpec trustedSpec,
            uint[] trustedAllowedTargets,
            int predictionMs,
            double wallNowMs,
            out PMProjectileKey key,
            out string error)
        {
            key = default(PMProjectileKey);
            error = null;

            EnsureThread();
            if (_disposed) { error = "driver disposed"; return false; }
            if (_faulted) { error = "driver faulted: " + FaultError; return false; }
            if (!_isServer) { error = "TryServerDirectSpawn 只适用于 DS"; return false; }
            if (ownerNetId == 0u || projectileId == 0u) { error = "owner/projectileId 非 0"; return false; }
            if (trustedSpec == null || !IsFiniteSpec(trustedSpec)) { error = "spec 无效"; return false; }

            PMNetSpawnReservation token;
            if (!_world.TryReserveNetId(out token) || token == null)
            {
                error = "NetId 预留失败（容量/非权威）";
                return false;
            }

            PMProjectileState state = new PMProjectileState();
            state.Key = new PMProjectileKey(_epoch, ownerNetId, projectileId, PMProjectileOrigin.ServerDirect);
            state.ActivationId = activationId;
            state.SpawnPosition = position;
            state.PreviousPosition = position;
            state.Position = position;
            state.Velocity = direction;
            state.Yaw = yaw;
            state.MoveTimeMs = 0.0;

            PMProjectileAdmissionOutcome admission;
            try
            {
                admission = _coordinator.ServerDirectSpawn(
                    state, token.NetId.Value, trustedSpec, trustedAllowedTargets, predictionMs, wallNowMs);
            }
            catch (InvalidOperationException ex)
            {
                _world.CancelReservedNetId(token);
                ReservationsCancelled++;
                Fault(PMR5ProjectileFaultReason.CoordinatorFaulted, "ServerDirectSpawn：" + ex.Message);
                error = ex.Message;
                return false;
            }

            if (!admission.IsAdmitted)
            {
                _world.CancelReservedNetId(token);
                ReservationsCancelled++;
                error = "权威登记被拒：" + admission.Result;
                return false;
            }

            _reservations[admission.Key] = token;
            key = admission.Key;
            ServerDirectSpawned++;
            return true;
        }

        // ================================================================ 视图

        /// <summary>把当前视图拷进缓冲区，返回拷贝条数（缓冲区不足时只拷前 N 条）。</summary>
        public int CopyViews(PMR5ProjectileView[] buffer)
        {
            EnsureThread();
            if (buffer == null) { return 0; }

            int n = 0;
            foreach (KeyValuePair<PMProjectileKey, PMR5ProjectileView> kv in _views)
            {
                if (n >= buffer.Length) { break; }
                buffer[n] = kv.Value;
                n++;
            }

            return n;
        }

        /// <summary>
        /// 取出至多 max 条「无消费者期间累积」的结算（FIFO，独占数组）。
        ///
        /// 与 <see cref="Settlement"/> 事件的关系：有订阅者时结算走事件（并计
        /// <see cref="SettlementsHandedToR6"/>）；没有订阅者时**不丢**，进本缓冲等宿主取。
        /// 两者不会重复投递同一条结算。
        /// </summary>
        public int DrainSettlements(int max, out PMProjectileSettlement[] items)
        {
            EnsureThread();
            if (max < 0) { max = 0; }
            if (max > _pendingSettlements.Count) { max = _pendingSettlements.Count; }

            items = new PMProjectileSettlement[max];
            for (int i = 0; i < max; i++) { items[i] = _pendingSettlements[i]; }

            _pendingSettlements.RemoveRange(0, max);
            return max;
        }

        /// <summary>取出至多 max 条「自上次 Drain 之后发生变化的」视图（C 的增量消费口）。</summary>
        public int DrainViewChanges(int max, out PMR5ProjectileView[] changes)
        {            EnsureThread();

            if (max < 0) { max = 0; }
            int take = _dirtyViewOrder.Count < max ? _dirtyViewOrder.Count : max;
            changes = new PMR5ProjectileView[take];

            for (int i = 0; i < take; i++)
            {
                PMProjectileKey key = _dirtyViewOrder[i];
                PMR5ProjectileView view;
                if (_views.TryGetValue(key, out view))
                {
                    changes[i] = view;
                }
                else
                {
                    // 视图已消失：给出「已移除」信号（Hidden + Stopped），C 据此回收表现。
                    view = new PMR5ProjectileView();
                    view.Key = key;
                    view.Hidden = true;
                    view.Stopped = true;
                    changes[i] = view;
                }

                _dirtyViewSet.Remove(key);
            }

            _dirtyViewOrder.RemoveRange(0, take);
            return take;
        }

        /// <summary>
        /// 重建本帧视图集合并标脏（C 用 <see cref="DrainViewChanges"/> 消费增量）。
        ///
        /// 必须**整体重建**而不是就地 upsert：权威对象在墓碑到期后被 DestroyObject 摘除，
        /// 若只做 upsert，C 永远收不到「该投射物已移除」，表现层会残留幽灵弹。
        /// </summary>
        private void RebuildViews()
        {
            Dictionary<PMProjectileKey, PMR5ProjectileView> next =
                new Dictionary<PMProjectileKey, PMR5ProjectileView>();

            foreach (KeyValuePair<PMProjectileKey, AuthorityEntry> kv in _authorityObjects)
            {
                PMProjectileSpec spec;
                PMProjectileState state;
                if (!_coordinator.TryObserveFrozen(kv.Key, out spec, out state) || state == null) { continue; }

                PMR5ProjectileView view = new PMR5ProjectileView();
                view.Key = kv.Key;
                view.OwnerNetId = kv.Key.OwnerNetId;
                view.AuthorityNetId = state.AuthorityNetId;
                view.MirrorObjectNetId = kv.Value.Obj != null ? kv.Value.Obj.NetId.Value : 0u;
                view.Position = state.Position;
                view.PreviousPosition = state.PreviousPosition;
                view.Velocity = state.Velocity;
                view.Yaw = state.Yaw;
                view.RadiusM = spec != null ? spec.RadiusM : 0f;
                view.MoveTimeMs = state.MoveTimeMs;
                view.Hidden = state.Hidden;
                view.Stopped = state.Stopped;
                view.LocalFake = false;
                view.TakenOver = true;
                next[view.Key] = view;
            }

            if (!_isServer)
            {
                foreach (KeyValuePair<PMProjectileKey, FakeEntry> kv in _fakes)
                {
                    PMProjectileSpec spec;
                    PMProjectileState state;
                    if (!_coordinator.TryObserveFrozen(kv.Key, out spec, out state) || state == null) { continue; }

                    PMR5ProjectileView view = new PMR5ProjectileView();
                    view.Key = kv.Key;
                    view.OwnerNetId = kv.Key.OwnerNetId;
                    // 假弹仍在时视图以假弹位置为准，但**必须**把权威镜像对象与权威 NetId 暴露给 C：
                    // 表现层要据此知道「这颗弹已经有权威副本」，并在接管时切换到它。
                    // 权威 NetId 只能来自镜像快照（客户端本地预测登记时并不知道它，见 A1：
                    // AuthorityNetId 不是预测面字段）。
                    MirrorEntry mirrorForFake;
                    bool hasMirror = _mirrors.TryGetValue(kv.Key, out mirrorForFake)
                                     && mirrorForFake.Obj != null;
                    if (hasMirror)
                    {
                        view.MirrorObjectNetId = mirrorForFake.Obj.NetId.Value;
                        if (mirrorForFake.AuthorityNetId != 0u)
                        {
                            view.AuthorityNetId = mirrorForFake.AuthorityNetId;
                        }
                        else
                        {
                            view.AuthorityNetId = state.AuthorityNetId;
                        }
                    }
                    else
                    {
                        view.AuthorityNetId = state.AuthorityNetId;
                        view.MirrorObjectNetId = 0u;
                    }

                    view.Position = state.Position;
                    view.PreviousPosition = state.PreviousPosition;
                    view.Velocity = state.Velocity;
                    view.Yaw = state.Yaw;
                    view.RadiusM = spec != null ? spec.RadiusM : 0f;
                    view.MoveTimeMs = state.MoveTimeMs;
                    view.Hidden = state.Hidden || FakeEnded(kv.Key);
                    view.Stopped = state.Stopped;
                    view.LocalFake = !kv.Value.TakenOver;
                    view.TakenOver = kv.Value.TakenOver;
                    next[view.Key] = view;
                }

                foreach (KeyValuePair<PMProjectileKey, MirrorEntry> kv in _mirrors)
                {
                    if (_fakes.ContainsKey(kv.Key)) { continue; }

                    PMR5ProjectileView view = new PMR5ProjectileView();
                    view.Key = kv.Key;
                    view.OwnerNetId = kv.Key.OwnerNetId;
                    view.AuthorityNetId = kv.Value.AuthorityNetId != 0u
                        ? kv.Value.AuthorityNetId
                        : (kv.Value.State != null ? kv.Value.State.AuthorityNetId : 0u);
                    view.MirrorObjectNetId = kv.Value.Obj != null ? kv.Value.Obj.NetId.Value : 0u;
                    if (kv.Value.State != null)
                    {
                        view.Position = kv.Value.State.Position;
                        view.PreviousPosition = kv.Value.State.PreviousPosition;
                        view.Velocity = kv.Value.State.Velocity;
                        view.Yaw = kv.Value.State.Yaw;
                        view.MoveTimeMs = kv.Value.State.MoveTimeMs;
                        view.Hidden = kv.Value.State.Hidden;
                        view.Stopped = kv.Value.State.Stopped;
                    }

                    view.RadiusM = kv.Value.Spec != null ? kv.Value.Spec.RadiusM : 0f;
                    view.LocalFake = false;
                    view.TakenOver = true;
                    next[view.Key] = view;
                }
            }

            // 新增 / 变化 → 脏。
            foreach (KeyValuePair<PMProjectileKey, PMR5ProjectileView> kv in next)
            {
                PMR5ProjectileView previous;
                if (_views.TryGetValue(kv.Key, out previous) && ViewEquals(previous, kv.Value)) { continue; }
                MarkViewDirty(kv.Key);
            }

            // 移除 → 也要脏（C 据此回收表现）。
            foreach (KeyValuePair<PMProjectileKey, PMR5ProjectileView> kv in _views)
            {
                if (!next.ContainsKey(kv.Key)) { MarkViewDirty(kv.Key); }
            }

            _views.Clear();
            foreach (KeyValuePair<PMProjectileKey, PMR5ProjectileView> kv in next) { _views[kv.Key] = kv.Value; }
        }

        private void MarkViewDirty(PMProjectileKey key)
        {
            if (_dirtyViewSet.Add(key))
            {
                if (_dirtyViewOrder.Count >= MaxDirtyViews)
                {
                    // 脏集合是有界诊断通道：满了就整体作废重来（不静默丢失语义，只丢增量粒度）。
                    _dirtyViewSet.Clear();
                    _dirtyViewOrder.Clear();
                    ViewDirtyOverflows++;
                    _dirtyViewSet.Add(key);
                }

                _dirtyViewOrder.Add(key);
            }
        }

        private static bool ViewEquals(PMR5ProjectileView a, PMR5ProjectileView b)
        {
            return a.Key.Equals(b.Key)
                   && a.OwnerNetId == b.OwnerNetId
                   && a.AuthorityNetId == b.AuthorityNetId
                   && a.MirrorObjectNetId == b.MirrorObjectNetId
                   && a.Position == b.Position
                   && a.PreviousPosition == b.PreviousPosition
                   && a.Velocity == b.Velocity
                   && a.Yaw == b.Yaw
                   && a.RadiusM == b.RadiusM
                   && a.MoveTimeMs == b.MoveTimeMs
                   && a.Hidden == b.Hidden
                   && a.Stopped == b.Stopped
                   && a.LocalFake == b.LocalFake
                   && a.TakenOver == b.TakenOver;
        }

        // ================================================================ 释放

        /// <summary>
        /// 幂等释放：取消全部未消费预留 → 销毁本会话的权威对象（世界 + 复制注销）→
        /// 取消本 world 的事件订阅 → 摘掉每个 player 的 Driver 引用 → 清队列与记账。
        /// **不**触碰别的 world（不调 `PMR5ProjectileEvents.ClearAll`）。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;

            // 1) 预留全部取消（编号不返还，但绝不留下悬空令牌）。
            foreach (KeyValuePair<PMProjectileKey, PMNetSpawnReservation> kv in _reservations)
            {
                _world.CancelReservedNetId(kv.Value);
                ReservationsCancelled++;
            }

            _reservations.Clear();

            // 2) 权威对象：世界销毁 + 复制注销。
            foreach (KeyValuePair<PMProjectileKey, AuthorityEntry> kv in _authorityObjects)
            {
                if (kv.Value.Obj != null && !kv.Value.Destroyed)
                {
                    _bridge.DestroyObject(kv.Value.Obj);
                    AuthorityObjectsDestroyed++;
                }
            }

            _authorityObjects.Clear();

            // 3) 事件订阅：只摘自己这一张。
            if (_mirrorSubscription != null)
            {
                _mirrorSubscription.Dispose();
                _mirrorSubscription = null;
            }

            // 4) 摘掉 player 上的接缝引用。
            foreach (KeyValuePair<PMR3Player, PlayerAdapter> kv in _adapters)
            {
                if (ReferenceEquals(kv.Key.ProjectileDriver, kv.Value))
                {
                    kv.Key.ProjectileDriver = null;
                }
            }

            _adapters.Clear();
            _playersByOwnerNetId.Clear();

            _inboundSpawns.Clear();
            _inboundHits.Clear();
            _inboundDecisions.Clear();
            _inboundMirrors.Clear();
            _unboundMirrors.Clear();
            _fakes.Clear();
            _mirrors.Clear();
            _uplinkVerifyUsed.Clear();
            _pendingSettlements.Clear();
            _views.Clear();
            _dirtyViewOrder.Clear();
            _dirtyViewSet.Clear();
            Settlement = null;
        }

        // ================================================================ 发送接缝（唯一入口）

        /// <summary>
        /// 上行投射物生成的唯一发送入口（声明层入口）。该入口内部走 `PMNetGeneratedRegistry.EnqueueRemote`
        /// → `PMR3Runtime.SendRemoteRpc`；本批把三条投射物 RPC 的失败路径放宽为**抛异常**，
        /// 由本驱动捕获后转成会话 fault。
        /// </summary>
        private static void SendServerProjectileSpawnV1(PMR3Player player, byte[] payload)
        {
            player.ServerProjectileSpawnV1(payload);
        }

        private static void SendServerProjectileHitV1(PMR3Player player, byte[] payload)
        {
            player.ServerProjectileHitV1(payload);
        }

        private static void SendClientProjectileDecisionV1(PMR3Player player, byte[] payload)
        {
            player.ClientProjectileDecisionV1(payload);
        }

        // ================================================================ 故障 / 工具

        private void Fault(PMR5ProjectileFaultReason reason, string message)
        {
            if (_faulted) { return; }
            _faulted = true;
            _faultReason = reason;
            _faultError = message;

            Warn("[PMR5ProjectileDriver] 会话失败（" + reason + "）：" + message);
        }

        private static void Warn(string message)
        {
            Action<string> handler = PMR3Player.ProjectileWarn;
            if (handler != null)
            {
                handler(message);
            }
        }

        private static bool ByteEquals(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b)) { return true; }
            if (a == null || b == null || a.Length != b.Length) { return false; }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) { return false; }
            }

            return true;
        }

        /// <summary>假弹是否已在本地结束（冻结状态没有该标记，只存在于 A1 账本）。</summary>
        private bool FakeEnded(PMProjectileKey key)
        {
            PMProjectileRegistration reg;
            if (!_coordinator.TryObserveRegistration(key, out reg) || reg == null) { return false; }
            return reg.PredictedEnded;
        }

        private static bool IsFiniteF(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }

        private static bool IsFiniteD(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }

        private static bool IsFiniteV(PMVector3 v)
        {
            return IsFiniteF(v.X) && IsFiniteF(v.Y) && IsFiniteF(v.Z);
        }

        private static bool IsFiniteSpec(PMProjectileSpec s)
        {
            if (s == null) { return false; }
            if (!IsFiniteF(s.SpeedMps) || s.SpeedMps <= 0f) { return false; }
            if (!IsFiniteF(s.RadiusM) || s.RadiusM < 0f) { return false; }
            if (s.LifetimeMs < 0) { return false; }
            return true;
        }

        private static string DescribeKey(PMProjectileKey key)
        {
            return "e" + key.Epoch + "/o" + key.OwnerNetId + "/p" + key.ProjectileId + "/" + key.Origin;
        }

        private void EnsureThread()
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                ThreadViolations++;
                throw new InvalidOperationException(
                    "[PMR5ProjectileDriver] 跨线程调用：驱动只允许在构造它的线程（主线程）上使用"
                    + "（复制应用 / RPC 收发 / 运动都在主线程）。");
            }
        }

        /// <summary>单行诊断摘要。</summary>
        public string Describe()
        {
            return "PMR5ProjectileDriver server=" + _isServer
                   + " epoch=" + _epoch
                   + " players=" + _adapters.Count
                   + " fakes=" + _fakes.Count
                   + " mirrors=" + _mirrors.Count
                   + " authority=" + _authorityObjects.Count
                   + " reserved=" + _reservations.Count
                   + " dupIgnored=" + ServerSpawnsDuplicateIgnored
                   + " dupConflict=" + RejectedDuplicateConflict
                   + " retiredDropped=" + RetiredSpawnsDropped
                   + " views=" + _views.Count
                   + " pendingSettlements=" + _pendingSettlements.Count
                   + " historyTargets=" + (_history != null ? _history.TargetCount : 0)
                   + " hostMotion=" + (_hostMotion != null)
                   + " faulted=" + _faulted
                   + (_faulted ? "(" + _faultReason + ")" : string.Empty);
        }
    }
}
