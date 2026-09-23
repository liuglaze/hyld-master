// ============================================================================
//  PMR6CombatDriver —— R6-B：战斗的**会话级网络驱动**
//  （把 PMCombat 权威核心 / R5 投射物驱动 / R6-A2 声明接缝接成一条每个会话一份的驱动）
// ============================================================================
//
//  契约来源
//  --------
//  · Docs/plans/net-r6-combat-contract.md「B 整合与宿主」+「B 驱动冻结补充」；
//  · Docs/plans/_r6_core_report.md（PMCombatWeaponPlanner / PMCombatSession 的精确 API）；
//  · Docs/plans/_r6_declaration_report.md（A2 的 9 条复制属性 / 4 条 RPC / IPMCombatNetworkDriver）；
//  · Docs/plans/_r5_network_review.md（PMR5ProjectileDriver 的公开 host API：TryFire /
//    ResolveActivation / DrainSettlements / BindPlayer / UnbindPlayer / 会话 fault）。
//
//  本文件做什么（一句话）
//  --------------------
//  「玩家按了攻击键」到「DS 上权威地扣资源、授权弹、结算伤害、复制 HP/资源、最后给出胜负并
//   逐 owner 可靠送达结果」这条链路上，**所有会话级网络适配**都在这里：
//
//    角色 Authority（DS）：
//      · 可信名册 AddPlayer → StartMatch（全名册接入后才开门）；
//      · 收到上行 ServerCombatAttackV1 → **单次** core.RequestAttack → R5.ResolveActivation(权威裁决)
//        → 可靠下行 ClientCombatAttackResultV1；
//      · 生产授权策略（CreateAuthorityPolicy）只认 core 已批准的 slot，trustedSpec/hostPosition 全来自
//        core + 宿主位置提供者，**不使用客户端自报的 spec/damage/位置**；
//      · R5 唯一 Settlement 出口 → core.ApplySettlement（唯一消费点）→ 状态脏 → FlushState 复制；
//      · 终局：冻结 outcome + 摘要（PMNetWriter 原语）→ 逐在线 owner 可靠下发 ClientCombatMatchResultV1
//        → 全 ACK 或 5000ms 宽限 → ResultReadyForLobby。
//
//    角色 AutonomousProxy（AP，本地 owner 副本）：
//      · TryAttack：planner 本地建计划 → **先**发 ServerCombatAttackV1 → **再**按计划 N 方向逐颗 R5.TryFire
//        （同一 activation / 同一枪口；不等批准）；任一步失败 = 会话 fault + 撤掉该 activation 的假弹；
//      · 收到裁决：accepted=false 走 R5 真撤销（CancelPredictedActivation），accepted=true 只记录
//        （接管时机由 R5 的镜像/高水位逻辑决定，本驱动**不抢**）；
//      · 收到比赛结果：幂等保存 + 在 FlushState 回 ServerCombatResultAckV1（**不在回调里发送**）。
//
//  为什么必须是「会话级 + 有界队列」而不是直接回调业务
//  ------------------------------------------------
//  声明层（PMR3Player）的复制回调与 RPC 接收都跑在**入站 drain 栈**上；在那里直接发送会与本次帧的
//  发送时机纠缠，直接做业务会让一次重发/一次乱序就改变权威状态。因此本驱动的所有接缝
//  一律**只入有界队列**，真正的处理发生在主线程的 `Pump` / `FlushState`。
//
//  引擎无关（硬约束）
//  ----------------
//  本文件零 `UnityEngine`、零 R3、零 Google.Protobuf：它同时被
//  `Tools/PMR6NetworkCheck`（netstandard2.0 + C# 7.3，零 Unity）与 `Tools/PMR6NetworkTest`（net8.0）
//  直接编译。位置/存活/Freeze 一律由宿主以 delegate 注入（见 `PMCombatTryGetOwnerPosition`）。
//
//  线程模型
//  --------
//  与 PMProjectile / PMR5ProjectileDriver 一致：**单线程宿主所有**。构造时记录线程，之后跨线程调用 fail-fast。
//  墙钟**只有一个来源**：`Pump` / `FlushState` / `UpdateClock` 共用同一个单调墙钟；
//  同一帧应把**同一个** now 传给 R6.Pump → R5.Pump → R6.FlushState（契约「所有入口共用同一个单调墙钟」）。
//
//  权威时钟次序（不可交换，实测修正）
//  --------------------------------
//  · 回蓝只发生在 `PMCombatSession.Tick` 里，而攻击合法性（普通攻击的蓝耗）按**请求时刻的权威资源**判定。
//    因此 DS 的 `Pump` 必须**先 Tick(now)、再处理上行攻击**。反序会让「这一段回蓝刚好到达」的合法攻击
//    被判 `InsufficientMana`，而且该拒绝会作为终态写进 core 的攻击账本（同一 activationId 的幂等回显
//    永远是拒绝）—— 一次 16ms 的次序错误等于永久吃掉一次攻击。
//  · 回蓝是本驱动里唯一**不经过任何显式入口**的权威状态变化，所以「需要复制」不能只看显式脏标记：
//    Tick 真的推进过墙钟时，`FlushState` 必须重新比对一次权威快照（见 `PublishStateIfDirty`）。
//  · 授权策略里**位置先于 core 的槽位授权**：core 的 `TryAuthorizeProjectile` 会消耗一个方向槽 +
//    一条已授权 key 记录（终态、等 TTL）。位置不可信时若先问 core，一次「枪口暂时不可用」就白扣一个槽，
//    同一笔攻击的多颗弹会把 N 个槽全部吃掉。因此策略先向宿主确认真实枪口，再进 core。
//
//  ⚠ 诚实边界：本批**未**接线真实 Unity 宿主（`PMClientSessionHost` / `PMDsSessionHost` 尚未创建本驱动；
//    且 `PMDsSessionHost` 目前仍以**诊断**方式订阅 `_projectileDriver.Settlement`，宿主接线时必须换掉，
//    否则会出现「诊断消费者 + 本驱动」双订阅）。因此**不能声称完整玩法已完成**（T46/T47 继续 PENDING_USER）。
// ============================================================================

using System;
using System.Collections.Generic;
using PMNet.Combat;
using PMNet.Mover;
using PMNet.Projectile;
using PMNet.Session;
using PMNet.Shared;

namespace PMNet.R3
{
    /// <summary>
    /// DS 侧「owner 真实位置」的**有效性查询**（R6 授权策略的真实位置来源）。
    ///
    /// 为什么是 delegate 而不是接口：R6 驱动必须引擎无关（位置来自 Unity 侧的运动/Mover/Physics），
    /// 但策略对象又必须在**创建 R5 driver 之前**就存在（R5 构造时就要 policy）。
    /// 用 delegate 让宿主只提供「给我这个玩家的可信位置」这一件事，并且返回 false 表示
    /// 「位置不可用/未冻结完成」——策略会据此**拒绝授权**（fail closed），而不是拿 0 向量去开一枪。
    /// </summary>
    /// <param name="player">DS 权威副本（其 NetId 就是认证 owner）。</param>
    /// <param name="position">可信位置（米，Y-up）。返回 false 时内容无意义。</param>
    /// <returns>位置是否可信可用。</returns>
    public delegate bool PMCombatTryGetOwnerPosition(PMR3Player player, out PMVector3 position);

    /// <summary>
    /// R6 战斗驱动的会话级故障原因。
    ///
    /// 语义与 R5 一致：**一次故障 = 本 session 报销**。故障后驱动拒绝一切推进与发送
    /// （`Pump` / `FlushState` 早退；`TryAttack` 返回 false + error），宿主必须记录并终止会话。
    /// 这样「发送失败 / 队列溢出 / 结果冲突 / 字段越界」永远不会被当成成功继续跑。
    /// </summary>
    public enum PMR6CombatFaultReason : byte
    {
        None = 0,

        /// <summary>四条战斗 RPC 之一发送失败（未接线 / 未就绪 / 桥拒绝）。</summary>
        SendFailed = 1,

        /// <summary>入站队列或本地跟踪表溢出（有界资源被打满，绝不静默丢弃）。</summary>
        QueueOverflow = 2,

        /// <summary>收到的字段越界/自相矛盾（未知拒绝码、未知胜方、0 outcomeId 等）—— fail closed。</summary>
        InvalidField = 3,

        /// <summary>DS 侧缺少 PMCombatSession（契约：DS 必有）。</summary>
        ModelMissing = 4,

        /// <summary>R5 投射物驱动已 fault（本会话无法继续）。</summary>
        R5Faulted = 5,

        /// <summary>驱动已释放后仍被使用。</summary>
        Disposed = 6,

        /// <summary>跨线程调用。</summary>
        ThreadViolation = 7,

        /// <summary>墙钟倒退（Pump/FlushState 的 wallNowMs 必须单调）。</summary>
        NonMonotonicWallClock = 8,

        /// <summary>同一 outcome 收到**冲突**的胜方/结果（不得改写已冻结的结论）。</summary>
        ConflictingOutcome = 9,

        /// <summary>结果摘要编码失败（PMNetWriter 异常）。</summary>
        ResultEncodeFailed = 10,

        /// <summary>TryAttack 的副本不是本地 owner（AP）。</summary>
        NotLocalOwner = 11,

        /// <summary>activationId 空间回绕（0 是非法哨兵，绝不回绕复用）。</summary>
        InvalidActivation = 12,

        /// <summary>
        /// 结算应用路径抛异常（已消化，不向 R5 的订阅分发栈外泄）。
        ///
        /// 为什么必须自己消化并 fault：R5 对「订阅者抛异常」的兜底是把同一条结算**退回到有界待取缓冲**，
        /// 于是它会在下一次 `Pump` 的 Drain 里被取回来再应用一遍。core 对每 key-target 是幂等的
        /// （血量/能量不会二次扣），但「已应用」记账与死亡事件会被重复触发，而且这条真实伤害的
        /// 归属变得不可判定。所以：消化后 fail closed，绝不让它流回 R5。
        /// </summary>
        SettlementFailed = 13,
    }

    /// <summary>
    /// R6 战斗的**会话级网络驱动**：一个会话（world + bridge + epoch + R5 driver）一份。
    ///
    /// 通过 <see cref="BindPlayer"/> 把**每个真实 <see cref="PMR3Player"/>** 的声明接缝接到本实例上
    /// （与 R5 完全同一套路：回调携带 player 身份，因此上行源认证和下行归属都不依赖 payload 自述）。
    /// </summary>
    public sealed class PMR6CombatDriver : IDisposable
    {
        // ================================================================ 冻结上限

        /// <summary>入站上行攻击队列上限（声明回调只入队，这里是它的有界容量）。</summary>
        public const int MaxInboundAttacks = 256;

        /// <summary>入站攻击裁决队列上限（AP）。</summary>
        public const int MaxInboundAttackResults = 256;

        /// <summary>入站比赛结果队列上限（AP）。</summary>
        public const int MaxInboundMatchResults = 64;

        /// <summary>入站结果确认队列上限（DS）。</summary>
        public const int MaxInboundResultAcks = 256;

        /// <summary>
        /// 客户端「已发出但未终态」的攻击跟踪上限。终态（Confirmed/Revoked）或 TTL 到期即退休，
        /// 因此这是一个真正有界、会回落的表（不是无限指纹缓存）。
        /// </summary>
        public const int MaxPendingAttacks = 512;

        /// <summary>未及时消费的结算缓冲上限（Drain 兜底路径的有界容量）。</summary>
        public const int MaxBufferedSettlements = 256;

        /// <summary>单次 Pump 最多处理的队列条数（每类批次都有界）。</summary>
        public const int MaxApplyPerPump = 64;

        /// <summary>结果重发周期（毫秒）。同一 outcome、同一胜方，内容永不改变。</summary>
        public const int ResultResendMs = PMCombatLimits.ResultResendMs;

        /// <summary>结果确认宽限期（毫秒）：超时即 ResultReadyForLobby=true（**不**宣称客户端都看到了）。</summary>
        public const int ResultGraceMs = PMCombatLimits.ResultGraceMs;

        /// <summary>客户端 pending attack 的 TTL（毫秒）：到期即退休（debug 计数可见）。</summary>
        public const int PendingAttackTtlMs = PMCombatLimits.AttackRecordTtlMs;

        // ================================================================ 依赖

        private readonly PMNetWorld _world;
        private readonly PMNetSessionBridge _bridge;
        private readonly uint _epoch;
        private readonly PMR5ProjectileDriver _projectiles;
        private readonly PMCombatSession _model;
        private readonly int _ownerThreadId;
        private readonly bool _isServer;

        // ================================================================ 玩家接线

        private sealed class PlayerAdapter : IPMCombatNetworkDriver
        {
            private readonly PMR6CombatDriver _driver;
            private readonly PMR3Player _player;

            public PlayerAdapter(PMR6CombatDriver driver, PMR3Player player)
            {
                _driver = driver;
                _player = player;
            }

            public PMR3Player Player { get { return _player; } }

            public void OnCombatStateReplicated() { _driver.EnqueueStateReplicated(_player); }

            public void OnServerAttack(uint activationId, bool isSuper, float aimX, float aimZ)
            {
                _driver.EnqueueServerAttack(_player, activationId, isSuper, aimX, aimZ);
            }

            public void OnClientAttackResult(uint activationId, bool accepted, int reason)
            {
                _driver.EnqueueAttackResult(_player, activationId, accepted, reason);
            }

            public void OnClientMatchResult(uint outcomeId, int winnerTeamId)
            {
                _driver.EnqueueMatchResult(_player, outcomeId, winnerTeamId);
            }

            public void OnServerResultAck(uint outcomeId)
            {
                _driver.EnqueueResultAck(_player, outcomeId);
            }
        }

        private readonly Dictionary<PMR3Player, PlayerAdapter> _adapters =
            new Dictionary<PMR3Player, PlayerAdapter>();

        private readonly Dictionary<uint, PMR3Player> _playersByNetId = new Dictionary<uint, PMR3Player>();

        // ================================================================ 入站有界队列（声明回调只入队）

        private struct InboundAttack
        {
            public PMR3Player Player;
            public uint ActivationId;
            public bool IsSuper;
            public float AimX;
            public float AimZ;
        }

        private struct InboundAttackResult
        {
            public PMR3Player Player;
            public uint ActivationId;
            public bool Accepted;
            public int Reason;
        }

        private struct InboundMatchResult
        {
            public PMR3Player Player;
            public uint OutcomeId;
            public int WinnerTeamId;
        }

        private struct InboundResultAck
        {
            public PMR3Player Player;
            public uint OutcomeId;
        }

        private readonly List<InboundAttack> _inboundAttacks = new List<InboundAttack>(8);
        private readonly List<InboundAttackResult> _inboundAttackResults = new List<InboundAttackResult>(8);
        private readonly List<InboundMatchResult> _inboundMatchResults = new List<InboundMatchResult>(4);
        private readonly List<InboundResultAck> _inboundResultAcks = new List<InboundResultAck>(8);

        // ================================================================ 客户端：pending attack 跟踪

        private sealed class PendingAttack
        {
            public uint OwnerNetId;
            public uint ActivationId;
            public double WallTimeMs;
            public int ProjectileCount;

            /// <summary>收到攻击级 Accepted（终态，不再被后来的 Rejected 翻回）。</summary>
            public bool Confirmed;

            /// <summary>收到攻击级 Rejected 并**真实撤销**了该 activation 的假弹（终态）。</summary>
            public bool Revoked;
        }

        private readonly Dictionary<ulong, PendingAttack> _pendingAttacks =
            new Dictionary<ulong, PendingAttack>();

        private readonly Dictionary<uint, uint> _nextActivationByOwner = new Dictionary<uint, uint>();

        private readonly List<ulong> _pruneBuffer = new List<ulong>();

        // ================================================================ DS：死亡上报真值（本地，不依赖复制字段）

        /// <summary>
        /// 本会话内**已上报过死亡**的玩家 NetId（让 <see cref="PlayerDied"/> 每个玩家只触发一次）。
        ///
        /// 为什么不复用复制字段 `player.CombatDead`：那要等 `FlushState` 才更新，而结算可能在
        /// 同一帧被应用两次（R5 对「订阅者抛异常」的兜底会把同一条结算退回待取缓冲，再由 `Pump`
        /// 的 Drain 取回）。用复制字段会让同一名玩家的 `PlayerDied` 触发两次 —— 宿主据此
        /// freeze 对应 Mover / 写 `history.Alive` 时就会出现重复副作用。
        /// </summary>
        private readonly HashSet<uint> _deathsReported = new HashSet<uint>();

        // ================================================================ DS：终局结果冻结

        private bool _outcomeFrozen;
        private bool _resultSentOnce;
        private uint _frozenOutcomeId;
        private int _frozenWinnerTeamId;
        private PMCombatEndReason _frozenReason;
        private byte[] _resultSummary;
        private double _frozenWallMs;
        private double _lastResultSendMs = double.NegativeInfinity;
        private bool _resultReadyForLobby;

        /// <summary>已确认该 outcome 的 owner NetId 集合（认证 Ack，幂等）。</summary>
        private readonly HashSet<uint> _resultAcked = new HashSet<uint>();

        // ================================================================ AP：结果幂等保存

        private bool _clientResultStored;
        private uint _clientOutcomeId;
        private int _clientWinnerTeamId;
        private bool _clientResultAckPending;

        // ================================================================ 状态 / 墙钟

        private double _wallMs;
        private bool _wallStarted;
        private bool _disposed;
        private bool _faulted;
        private PMR6CombatFaultReason _faultReason;
        private string _faultError;
        private bool _stateDirty = true;

        /// <summary>上一次喂给 core 的墙钟（判断「这一帧权威状态是否可能因 Tick 变化」）。</summary>
        private double _lastModelTickMs = double.NegativeInfinity;

        /// <summary>
        /// `Tick` 真的推进过墙钟后置位：回蓝这类**持续变化**不经过任何显式入口，
        /// 必须让下一次 `FlushState` 重新比对一次权威快照。
        /// </summary>
        private bool _tickAdvancedSincePublish;

        /// <summary>
        /// 每个 roster 成员最近一次**真正写入**过的九条复制值。
        ///
        /// 两个作用：
        ///   ① 「无变化的重复发布」不再调用 `PublishCombatState`（避免每次 Flush 都触发九条 RepNotify）；
        ///   ② 让「回蓝」这种没有显式脏标记的变化也能被识别出来（比对即可）。
        /// </summary>
        private readonly Dictionary<uint, PublishedCombatState> _publishedByNetId =
            new Dictionary<uint, PublishedCombatState>();

        /// <summary>九条战斗复制属性的值语义（只用于「有没有变化」的比较与写入）。</summary>
        private struct PublishedCombatState
        {
            public int HeroId;
            public int TeamId;
            public int Hp;
            public int MaxHp;
            public bool Dead;
            public int Mana;
            public int SuperEnergy;
            public bool MatchEnded;
            public int WinnerTeamId;

            public static PublishedCombatState From(PMCombatPlayerSnapshot player, PMCombatMatchOutcome outcome)
            {
                PublishedCombatState state;
                state.HeroId = player.HeroId;
                state.TeamId = player.TeamId;
                state.Hp = player.Hp;
                state.MaxHp = player.MaxHp;
                state.Dead = player.Dead;
                state.Mana = player.Mana;
                state.SuperEnergy = player.SuperEnergy;
                state.MatchEnded = outcome.Ended;
                state.WinnerTeamId = outcome.WinnerTeamId;
                return state;
            }

            public bool SameAs(PublishedCombatState other)
            {
                return HeroId == other.HeroId
                       && TeamId == other.TeamId
                       && Hp == other.Hp
                       && MaxHp == other.MaxHp
                       && Dead == other.Dead
                       && Mana == other.Mana
                       && SuperEnergy == other.SuperEnergy
                       && MatchEnded == other.MatchEnded
                       && WinnerTeamId == other.WinnerTeamId;
            }

            public override bool Equals(object obj)
            {
                return obj is PublishedCombatState && SameAs((PublishedCombatState)obj);
            }

            public override int GetHashCode()
            {
                int hash = HeroId;
                hash = (hash * 397) ^ TeamId;
                hash = (hash * 397) ^ Hp;
                hash = (hash * 397) ^ MaxHp;
                hash = (hash * 397) ^ Mana;
                hash = (hash * 397) ^ SuperEnergy;
                hash = (hash * 397) ^ WinnerTeamId;
                hash = (hash * 397) ^ (Dead ? 1 : 0);
                hash = (hash * 397) ^ (MatchEnded ? 1 : 0);
                return hash;
            }
        }

        // ================================================================ 观测计数（本地记账，非复制）

        public long Pumps;
        public long Flushes;
        public long StatePublishCount;
        public long StatePublishRejected;
        public long StateReplicatedNotices;
        public long AttacksReceived;
        public long AttacksProcessed;
        public long AttackResultsApplied;
        public long AttackRejections { get; private set; }
        public uint LastRejectedActivationId { get; private set; }
        public PMCombatRejectReason LastAttackRejectionReason { get; private set; }
        public long AttackResultsUnknown;
        public long MatchResultsReceived;
        public long MatchResultDuplicates;
        public long ResultAcksReceived;
        public long ResultAckMismatches;
        public long ResultAckDuplicates;
        public long ResultSends;
        public long ResultResends;
        public long ResultReadyByAckCount;
        public long ResultReadyByGraceCount;
        public long OutcomeFrozenCount;
        public long OutcomeConflictCount;
        public long SettlementsApplied;
        public long SettlementsBuffered;
        public long SettlementsRejected;
        public long SettlementsDrainedFromR5;
        public long PendingAttackTimeouts;
        public long PendingAttacksPruned;
        public long ActivationWraps;

        /// <summary>结算应用路径抛异常的次数（已在订阅边界消化；一旦发生即会话 fault）。</summary>
        public long SettlementApplyFailures;

        /// <summary>
        /// 「终态不倒退」被命中的次数：已 Confirmed 的攻击收到迟到 Rejected、或已 Revoked 的攻击
        /// 收到迟到 Accepted。已接受的结论**只观测不改写**（不撤销假弹、不翻状态）。
        /// </summary>
        public long AttackResultsLateAfterTerminal;

        /// <summary>宿主订阅回调（PlayerDied / OutcomeFrozen）抛异常并被逐订阅者隔离的次数。</summary>
        public long CallbackExceptions;

        /// <summary>AP：由**攻击级拒绝**（本驱动）真实撤销的假弹颗数（与 R5 逐颗裁决路径可区分）。</summary>
        public long PredictionsCancelled;
        public long ThreadViolations;
        public long PlayerDeathsReported;

        /// <summary>玩家在本会话内**首次**由存活变为死亡（宿主据此 freeze 对应 Mover / 对齐 history.Alive）。</summary>
        public event Action<PMR3Player> PlayerDied;

        /// <summary>DS 终局冻结（宿主据此记录/上报考场结论；只触发一次）。</summary>
        public event Action<uint, int> OutcomeFrozen;

        // ================================================================ 构造

        /// <summary>
        /// 构造会话级战斗驱动。
        /// </summary>
        /// <param name="world">本会话世界。</param>
        /// <param name="bridge">本会话桥（必须属于同一个 world）。</param>
        /// <param name="epoch">本局身份纪元（**必须非 0**，且必须与 R5 driver 一致；epoch 不复用）。</param>
        /// <param name="projectiles">R5 投射物驱动（本驱动唯一的下行投射物出口）。</param>
        /// <param name="model">
        /// PMCombat 权威核心。**DS 必须提供**（缺失即编程错误，直接抛）；AP 传 null
        /// （客户端不持有权威会话，只用 planner + 复制字段）。
        /// </param>
        public PMR6CombatDriver(
            PMNetWorld world,
            PMNetSessionBridge bridge,
            uint epoch,
            PMR5ProjectileDriver projectiles,
            PMCombatSession model = null)
        {
            if (world == null) { throw new ArgumentNullException("world"); }
            if (bridge == null) { throw new ArgumentNullException("bridge"); }
            if (projectiles == null) { throw new ArgumentNullException("projectiles"); }
            if (epoch == 0u) { throw new ArgumentOutOfRangeException("epoch", "epoch 必须非 0"); }
            if (!ReferenceEquals(bridge.World, world))
            {
                throw new ArgumentException("桥不属于该世界", "bridge");
            }

            if (projectiles.Epoch != epoch)
            {
                throw new ArgumentException("R5 投射物驱动的 epoch 与本驱动不一致", "projectiles");
            }

            _isServer = world.IsServer;

            // 契约：DS 必有权威核心；AP 传 null。两者都错（DS 缺 model / AP 有 model）都是宿主 bug。
            if (_isServer && model == null)
            {
                throw new ArgumentNullException("model", "DS 侧必须提供 PMCombatSession（契约：DS 必有）");
            }

            if (model != null && model.Epoch != epoch)
            {
                throw new ArgumentException("PMCombatSession 的 epoch 与本驱动不一致", "model");
            }

            _world = world;
            _bridge = bridge;
            _epoch = epoch;
            _projectiles = projectiles;
            _model = model;
            _ownerThreadId = Environment.CurrentManagedThreadId;

            // R5 的唯一 Settlement 消费点就是本驱动（订阅 + Drain 兜底）。
            // 只在权威侧订阅：客户端不产生权威结算，订阅只会掩盖错误。
            if (_isServer)
            {
                _projectiles.Settlement += OnProjectileSettlement;
            }
        }

        // ================================================================ 只读视图

        public PMNetWorld World { get { return _world; } }
        public PMNetSessionBridge Bridge { get { return _bridge; } }
        public uint Epoch { get { return _epoch; } }
        public bool IsServer { get { return _isServer; } }
        public PMR5ProjectileDriver Projectiles { get { return _projectiles; } }
        public PMCombatSession Model { get { return _model; } }
        public int BoundPlayerCount { get { return _adapters.Count; } }
        public bool IsFaulted { get { return _faulted; } }
        public PMR6CombatFaultReason FaultReason { get { return _faultReason; } }
        public string FaultError { get { return _faultError == null ? string.Empty : _faultError; } }
        public bool IsDisposed { get { return _disposed; } }

        /// <summary>本驱动当前墙钟水位（毫秒，单调）。</summary>
        public double WallTimeMs { get { return _wallMs; } }

        /// <summary>客户端本地 owner 副本（找不到返回 null）。</summary>
        public PMR3Player LocalOwner
        {
            get
            {
                if (_isServer) { return null; }

                int uid = _projectiles.LocalUid;
                if (uid == 0) { return null; }

                foreach (KeyValuePair<PMR3Player, PlayerAdapter> kv in _adapters)
                {
                    if (kv.Key.Uid == uid) { return kv.Key; }
                }

                return null;
            }
        }

        /// <summary>客户端已发出的 pending attack 条数（观测/有界性断言）。</summary>
        public int PendingAttackCount { get { return _pendingAttacks.Count; } }

        /// <summary>
        /// DS 待消费的结算条数（**观测 R5 的真实待取缓冲**，不是本地副本）。
        ///
        /// 正常路径下恒为 0：本驱动的订阅者从不抛异常，因此 R5 的每条结算都当场交付；
        /// 只有「订阅者异常 / 晚期接管」才会在 R5 侧落缓冲，由 `Pump` 的 Drain 兜底取回。
        /// </summary>
        public int BufferedSettlementCount { get { return _projectiles.PendingSettlementCount; } }

        /// <summary>DS：结果是否已可交给 Lobby（全 ACK 或宽限到期）。AP 恒为 false。</summary>
        public bool ResultReadyForLobby { get { return _isServer && _resultReadyForLobby; } }

        /// <summary>DS：冻结的胜方队伍号（未冻结为 0）。</summary>
        public int WinnerTeamId { get { return _outcomeFrozen ? _frozenWinnerTeamId : 0; } }

        /// <summary>DS：冻结的 outcomeId（未冻结为 0）。</summary>
        public uint FrozenOutcomeId { get { return _outcomeFrozen ? _frozenOutcomeId : 0u; } }

        /// <summary>终止原因（未冻结时为 <see cref="PMCombatEndReason.None"/>）。</summary>
        public PMCombatEndReason FrozenEndReason
        {
            get { return _outcomeFrozen ? _frozenReason : PMCombatEndReason.None; }
        }

        /// <summary>是否已经冻结结果（一旦冻结不因断线/重复结算改变）。</summary>
        public bool OutcomeIsFrozen { get { return _outcomeFrozen; } }

        /// <summary>DS：已确认该 outcome 的 owner 数。</summary>
        public int ResultAckCount { get { return _resultAcked.Count; } }

        /// <summary>DS：冻结的结果摘要（**深拷贝**；未冻结返回空数组）。</summary>
        public byte[] ResultSummary
        {
            get
            {
                byte[] summary = _resultSummary;
                if (summary == null) { return new byte[0]; }
                return (byte[])summary.Clone();
            }
        }

        /// <summary>AP：已幂等保存的比赛结果（未收到返回 false）。</summary>
        public bool TryGetStoredMatchResult(out uint outcomeId, out int winnerTeamId)
        {
            outcomeId = _clientOutcomeId;
            winnerTeamId = _clientWinnerTeamId;
            return _clientResultStored;
        }

        /// <summary>AP：某个 activation 是否仍在 pending 跟踪表里（观测/有界性断言）。</summary>
        public bool HasPendingAttack(uint ownerNetId, uint activationId)
        {
            return _pendingAttacks.ContainsKey(PendingKey(ownerNetId, activationId));
        }

        /// <summary>AP：某个 activation 是否已被判定为**拒绝**（假弹已真实撤销）。</summary>
        public bool WasActivationRevoked(uint ownerNetId, uint activationId)
        {
            PendingAttack pending;
            if (!_pendingAttacks.TryGetValue(PendingKey(ownerNetId, activationId), out pending)) { return false; }
            return pending.Revoked;
        }

        /// <summary>AP：某个 activation 是否已被判定为**确认**（仅记录，接管仍由 R5 决定）。</summary>
        public bool WasActivationConfirmed(uint ownerNetId, uint activationId)
        {
            PendingAttack pending;
            if (!_pendingAttacks.TryGetValue(PendingKey(ownerNetId, activationId), out pending)) { return false; }
            return pending.Confirmed;
        }

        // ================================================================ 玩家绑定

        /// <summary>
        /// 把一个真实 <see cref="PMR3Player"/> 的战斗声明接缝接到本驱动上，并**同时**把它接到 R5 驱动
        /// （同一套路：一个 player 的两条接缝都由宿主一次性接好，避免漏接一侧）。
        ///
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

            uint netId = player.NetId.Value;
            if (!_playersByNetId.ContainsKey(netId))
            {
                _playersByNetId.Add(netId, player);
            }

            player.CombatDriver = adapter;

            // R5 侧同一玩家（幂等；失败不致命——上行投射物会走 R5 自己的「无 driver 丢弃」可见路径）。
            _projectiles.BindPlayer(player);

            // 新绑定的 player 需要一份初值（若它是名册成员）。
            _stateDirty = true;
            return true;
        }

        /// <summary>
        /// 摘掉一个玩家的接线（幂等）。
        ///
        /// DS 侧同时把该玩家标为断线（core.Disconnect：保留水位与死亡真值，必要时按**剩余唯一队伍**判 Forfeit）。
        /// 结果等待集也会去掉这个 owner（契约：Unbind 允许少一个等待 Ack）。
        /// **注意**：`Dispose` 不会走本方法（会话整体收场不应触发「断线判胜」）。
        /// </summary>
        public bool UnbindPlayer(PMR3Player player)
        {
            EnsureThread();
            if (player == null) { return false; }

            PlayerAdapter adapter;
            if (!_adapters.TryGetValue(player, out adapter)) { return false; }

            _adapters.Remove(player);
            if (ReferenceEquals(player.CombatDriver, adapter))
            {
                player.CombatDriver = null;
            }

            PMR3Player existing;
            if (player.NetId.IsValid
                && _playersByNetId.TryGetValue(player.NetId.Value, out existing)
                && ReferenceEquals(existing, player))
            {
                _playersByNetId.Remove(player.NetId.Value);
            }

            _projectiles.UnbindPlayer(player);

            if (player.NetId.IsValid)
            {
                if (_model != null)
                {
                    // core 负责「保留水位/死亡真值 + 只在剩余唯一队伍时判 Forfeit」。
                    _model.Disconnect(player.NetId.Value);
                    _stateDirty = true;
                }

                _resultAcked.Remove(player.NetId.Value);
            }

            return true;
        }

        /// <summary>该 player 是否已绑定。</summary>
        public bool IsPlayerBound(PMR3Player player)
        {
            return player != null && _adapters.ContainsKey(player);
        }

        // ================================================================ DS：可信名册

        /// <summary>
        /// DS：把一名**可信名册**成员登记进权威核心并绑定接缝。
        ///
        /// uid/team/hero 只能来自 DS 的名册（票据验签结果），**绝不**取客户端自报值。
        /// 成功时会写脏状态，下一次 `FlushState` 即把 hero/team/HP/资源复制出去。
        /// </summary>
        public bool AddPlayer(PMR3Player player, int uid, int teamId, int heroId)
        {
            EnsureThread();
            if (_disposed || _faulted) { return false; }
            if (!_isServer) { return false; }
            if (_model == null) { return false; }
            if (player == null || !player.NetId.IsValid) { return false; }

            if (!_model.AddPlayer(player.NetId.Value, uid, teamId, heroId))
            {
                return false;
            }

            _stateDirty = true;
            BindPlayer(player);
            return true;
        }

        /// <summary>DS：全名册接入后的开战门（人数必须与名册完全一致）。</summary>
        public bool StartMatch(int expectedPlayerCount)
        {
            EnsureThread();
            if (_disposed || _faulted) { return false; }
            if (!_isServer || _model == null) { return false; }

            bool started = _model.StartMatch(expectedPlayerCount);
            if (started) { _stateDirty = true; }
            return started;
        }

        /// <summary>DS：读某位玩家的权威快照（深拷贝；未知 netId 返回 false）。</summary>
        public bool TryGetPlayerSnapshot(uint netId, out PMCombatPlayerSnapshot snapshot)
        {
            snapshot = null;
            if (_model == null) { return false; }
            snapshot = _model.GetPlayer(netId);
            return snapshot != null;
        }

        // ================================================================ AP：攻击

        /// <summary>
        /// AP：发起一次攻击（普通或大招）。
        ///
        /// 次序（契约原文）：**先共用 planner 建立本地计划 → 先把 ServerCombatAttackV1 发出去 →
        /// 再按计划逐颗 R5.TryFire**。客户端**不等**服务端批准才开始预测；DS 的拒绝会通过
        /// ClientCombatAttackResultV1 回来，届时**真实撤销**该 activation 的所有假弹。
        ///
        /// 本方法**不写任何复制字段**（mana/energy 的权威值只来自 DS 复制，客户端只读）。
        /// </summary>
        /// <param name="player">本地 owner 副本（必须是本驱动的本地 owner）。</param>
        /// <param name="isSuper">是否大招。</param>
        /// <param name="aimX">瞄准世界方向 X（XZ 平面）。</param>
        /// <param name="aimZ">瞄准世界方向 Z。</param>
        /// <param name="position">枪口/出生点世界坐标（本次攻击 N 颗弹共用同一枪口）。</param>
        /// <param name="predictionMs">预测提前量（毫秒，R5 允许 0..500）。</param>
        /// <param name="wallNow">单调墙钟（毫秒）。</param>
        /// <param name="activationId">成功时为本次激活 ID（单调非 0、不回绕）；**失败时恒为 0**
        ///（已消耗的 ID 不外泄，避免宿主把一次失败调用当成一次已激活的攻击）。</param>
        /// <param name="error">失败原因。</param>
        public bool TryAttack(PMR3Player player, bool isSuper, float aimX, float aimZ,
            PMVector3 position, int predictionMs, double wallNow,
            out uint activationId, out string error)
        {
            activationId = 0u;
            error = null;

            EnsureThread();
            if (_disposed) { error = "driver disposed"; return false; }
            if (_faulted) { error = "driver faulted: " + FaultError; return false; }
            if (_isServer) { error = "TryAttack 只适用于客户端 AP"; return false; }
            if (player == null || !player.NetId.IsValid) { error = "player/NetId 无效"; return false; }

            if (_projectiles.IsFaulted)
            {
                Fault(PMR6CombatFaultReason.R5Faulted, "R5 投射物驱动已 fault：" + _projectiles.FaultError);
                error = "R5 投射物驱动已 fault";
                return false;
            }

            PMR3Player localOwner = LocalOwner;
            if (localOwner == null || !ReferenceEquals(localOwner, player))
            {
                error = "本副本不是本地 owner（AP）";
                return false;
            }

            if (!ReferenceEquals(player.World, _world))
            {
                error = "副本不属于本驱动世界";
                return false;
            }

            if (!IsAliveForAttack(player, out error)) { return false; }

            double now = EffectiveWall(wallNow);

            // 双方同源：客户端用**同一份** planner 建立本地计划（DS 侧 core 也会用同一份口径复核）。
            PMCombatAttackPlan plan;
            PMCombatRejectReason reason;
            if (!PMCombatWeaponPlanner.TryBuild(player.CombatHeroId, isSuper, aimX, aimZ, out plan, out reason)
                || plan == null)
            {
                error = "planner 拒绝：" + reason;
                return false;
            }

            uint next;
            if (!NextActivation(player.NetId.Value, out next))
            {
                ActivationWraps++;
                Fault(PMR6CombatFaultReason.InvalidActivation, "activationId 空间回绕，拒绝复用");
                error = "activationId 空间耗尽/回绕";
                return false;
            }

            // ---------------------------------------------------------------- 0) 先确认有界表装得下
            //
            // 必须在**任何不可逆动作之前**（发包 / 逐颗假弹）：否则一次「表满」会先发出上行攻击声明、
            // 先生成 N 颗假弹、再去撤它们（并且那些假弹对应的上行 spawn 已经在路上，DS 会为一批
            // 客户端已经撤掉的弹建权威镜像）。有界资源打满应是「什么都没做的拒绝」。
            PrunePendingAttacks(now);
            if (_pendingAttacks.Count >= MaxPendingAttacks)
            {
                Fault(PMR6CombatFaultReason.QueueOverflow,
                    "pending attack 跟踪表已满（" + MaxPendingAttacks + "），拒绝新攻击（未发包、未生成假弹）");
                error = "pending attack 跟踪表已满";
                return false;
            }

            activationId = next;

            // ---------------------------------------------------------------- 1) 先发包（不可逆事件）
            try
            {
                player.ServerCombatAttackV1(activationId, isSuper, aimX, aimZ);
            }
            catch (Exception ex)
            {
                // 失败时**不外泄**已消耗的 ID：否则宿主会把一次失败调用当成「已激活」去等结果。
                activationId = 0u;
                Fault(PMR6CombatFaultReason.SendFailed,
                    "ServerCombatAttackV1 发送失败：" + ex.GetType().Name + " " + ex.Message);
                error = "上行攻击声明发送失败：" + ex.Message;
                return false;
            }

            // ---------------------------------------------------------------- 2) 再按计划逐颗预测
            PMVector3[] directions = plan.Directions;
            if (directions == null || directions.Length == 0)
            {
                activationId = 0u;
                Fault(PMR6CombatFaultReason.InvalidField, "计划方向为空（planner 不变式被破坏）");
                error = "计划方向为空";
                return false;
            }

            for (int i = 0; i < directions.Length; i++)
            {
                PMVector3 direction = directions[i];
                float yaw = YawFromDirection(direction);

                PMProjectileKey key;
                string fireError;
                bool fired = _projectiles.TryFire(
                    player, activationId, position, direction, yaw, plan.Spec.Clone(), predictionMs,
                    now, out key, out fireError);

                if (!fired)
                {
                    // 「部分 TryFire 失败不能假成功」：撤掉该 activation 的全部假弹 + 会话 fault。
                    int revoked = _projectiles.CancelPredictedActivation(player, activationId, now);
                    activationId = 0u;
                    Fault(PMR6CombatFaultReason.SendFailed,
                        "TryFire 第 " + i + "/" + directions.Length + " 颗失败（已撤 " + revoked + " 颗假弹）："
                        + fireError);
                    error = "部分投射物预测失败：" + fireError;
                    return false;
                }
            }

            // ---------------------------------------------------------------- 3) 有界跟踪（按终态/TTL 退休）
            //   容量已在步骤 0 验过，此处只登记。
            PendingAttack pending = new PendingAttack();
            pending.OwnerNetId = player.NetId.Value;
            pending.ActivationId = activationId;
            pending.WallTimeMs = now;
            pending.ProjectileCount = directions.Length;
            _pendingAttacks[PendingKey(player.NetId.Value, activationId)] = pending;

            if (now > _wallMs) { _wallMs = now; }
            return true;
        }

        private bool IsAliveForAttack(PMR3Player player, out string error)
        {
            error = null;

            // 已知 hero / team：复制字段（DS 名册真值）没有就位时不允许开火。
            if (player.CombatHeroId < 0 || player.CombatHeroId >= PMHeroId.Count)
            {
                error = "未知英雄（heroId=" + player.CombatHeroId + "）";
                return false;
            }

            if (player.CombatTeamId <= 0)
            {
                error = "未知队伍（teamId=" + player.CombatTeamId + "）";
                return false;
            }

            if (player.CombatMaxHp <= 0)
            {
                error = "未收到 HP 复制（maxHp=" + player.CombatMaxHp + "）";
                return false;
            }

            if (player.CombatDead)
            {
                error = "已死亡，禁止新攻击";
                return false;
            }

            if (player.CombatMatchEnded)
            {
                error = "比赛已结束，禁止新攻击";
                return false;
            }

            return true;
        }

        private bool NextActivation(uint ownerNetId, out uint activationId)
        {
            activationId = 0u;

            uint current;
            if (!_nextActivationByOwner.TryGetValue(ownerNetId, out current)) { current = 0u; }
            if (current == uint.MaxValue) { return false; }

            current = current + 1u;
            if (current == 0u) { return false; }

            _nextActivationByOwner[ownerNetId] = current;
            activationId = current;
            return true;
        }

        private static ulong PendingKey(uint ownerNetId, uint activationId)
        {
            return ((ulong)ownerNetId << 32) | (ulong)activationId;
        }

        private static float YawFromDirection(PMVector3 direction)
        {
            double yaw = Math.Atan2((double)direction.X, (double)direction.Z) * 180.0 / Math.PI;
            if (yaw < 0.0) { yaw += 360.0; }
            if (yaw >= 360.0) { yaw -= 360.0; }
            return (float)yaw;
        }

        private void PrunePendingAttacks(double wallNow)
        {
            if (_pendingAttacks.Count == 0) { return; }

            _pruneBuffer.Clear();
            foreach (KeyValuePair<ulong, PendingAttack> kv in _pendingAttacks)
            {
                if (wallNow - kv.Value.WallTimeMs < (double)PendingAttackTtlMs) { continue; }
                _pruneBuffer.Add(kv.Key);
            }

            for (int i = 0; i < _pruneBuffer.Count; i++)
            {
                PendingAttack pending;
                if (!_pendingAttacks.TryGetValue(_pruneBuffer[i], out pending)) { continue; }

                // 终态（Confirmed/Revoked）到期是正常退休；未终态到期说明裁决一直没回来。
                if (!pending.Confirmed && !pending.Revoked) { PendingAttackTimeouts++; }

                _pendingAttacks.Remove(_pruneBuffer[i]);
                PendingAttacksPruned++;
            }

            _pruneBuffer.Clear();
        }

        // ================================================================ 入站队列（只入队，不处理）

        private void EnqueueStateReplicated(PMR3Player player)
        {
            // 复制层通知只用于观测：本驱动不做任何状态机（客户端读的是复制字段本身）。
            StateReplicatedNotices++;
        }

        private void EnqueueServerAttack(PMR3Player player, uint activationId, bool isSuper,
            float aimX, float aimZ)
        {
            AttacksReceived++;

            if (_inboundAttacks.Count >= MaxInboundAttacks)
            {
                Fault(PMR6CombatFaultReason.QueueOverflow,
                    "入站上行攻击队列溢出（" + MaxInboundAttacks + "）");
                return;
            }

            InboundAttack item = new InboundAttack();
            item.Player = player;
            item.ActivationId = activationId;
            item.IsSuper = isSuper;
            item.AimX = aimX;
            item.AimZ = aimZ;
            _inboundAttacks.Add(item);
        }

        private void EnqueueAttackResult(PMR3Player player, uint activationId, bool accepted, int reason)
        {
            if (_inboundAttackResults.Count >= MaxInboundAttackResults)
            {
                Fault(PMR6CombatFaultReason.QueueOverflow,
                    "入站攻击裁决队列溢出（" + MaxInboundAttackResults + "）");
                return;
            }

            InboundAttackResult item = new InboundAttackResult();
            item.Player = player;
            item.ActivationId = activationId;
            item.Accepted = accepted;
            item.Reason = reason;
            _inboundAttackResults.Add(item);
        }

        private void EnqueueMatchResult(PMR3Player player, uint outcomeId, int winnerTeamId)
        {
            MatchResultsReceived++;

            if (_inboundMatchResults.Count >= MaxInboundMatchResults)
            {
                Fault(PMR6CombatFaultReason.QueueOverflow,
                    "入站比赛结果队列溢出（" + MaxInboundMatchResults + "）");
                return;
            }

            InboundMatchResult item = new InboundMatchResult();
            item.Player = player;
            item.OutcomeId = outcomeId;
            item.WinnerTeamId = winnerTeamId;
            _inboundMatchResults.Add(item);
        }

        private void EnqueueResultAck(PMR3Player player, uint outcomeId)
        {
            ResultAcksReceived++;

            if (_inboundResultAcks.Count >= MaxInboundResultAcks)
            {
                Fault(PMR6CombatFaultReason.QueueOverflow,
                    "入站结果确认队列溢出（" + MaxInboundResultAcks + "）");
                return;
            }

            InboundResultAck item = new InboundResultAck();
            item.Player = player;
            item.OutcomeId = outcomeId;
            _inboundResultAcks.Add(item);
        }

        // ================================================================ R5 结算（唯一消费点）

        /// <summary>
        /// R5 的**唯一**结算出口。本驱动是它在这个会话里的唯一消费者：
        /// 收到的每一条都交给 `core.ApplySettlement`（不是「记个数」）。订阅在 Dispose 时取消自己的那一份。
        ///
        /// 异常必须在**本方法内部**消化：R5 对「订阅者抛异常」的兜底是把同一条结算退回到有界待取缓冲，
        /// 下一次 `Pump` 的 Drain 会取回它再应用一遍（core 对每 key-target 幂等 ⇒ 血量不会二次扣，
        /// 但「已应用」记账与 `PlayerDied` 会重复触发，伤害归属也无法判定）。因此：
        /// 消化失败 ⇒ 会话 fault（fail closed），绝不让异常流回 R5。
        /// </summary>
        private void OnProjectileSettlement(PMProjectileSettlement settlement)
        {
            if (_disposed || !_isServer || _model == null) { return; }

            try
            {
                ApplySettlementNow(settlement);
            }
            catch (Exception ex)
            {
                ApplySettlementFailure(ex);
            }
        }

        /// <summary>结算应用链爆掉的统一处置：计数 + 会话 fault（不向调用者抛）。</summary>
        private void ApplySettlementFailure(Exception ex)
        {
            SettlementApplyFailures++;
            Fault(PMR6CombatFaultReason.SettlementFailed,
                "结算应用抛异常（已消化，不退回 R5 待取缓冲）：" + ex.GetType().Name + " " + ex.Message);
        }

        private void ApplySettlementNow(PMProjectileSettlement settlement)
        {
            // 墙钟：R6 自己拥有喂给 core 的时钟，因此取「驱动水位」与「结算自带墙钟」的较大者，
            // 既保证单调（绝不 InvalidClock 丢伤害），也不会让 core 时钟莫名超前一帧。
            double applyNow = _wallMs;
            if (settlement.WallTimeMs > applyNow) { applyNow = settlement.WallTimeMs; }

            if (!_model.Started)
            {
                SettlementsRejected++;
                return;
            }

            PMCombatRejectReason reason;
            bool applied = _model.ApplySettlement(settlement, applyNow, out reason);
            if (!applied)
            {
                SettlementsRejected++;
                return;
            }

            SettlementsApplied++;
            _stateDirty = true;

            // 死亡是权威转移：宿主据此 freeze 对应 Mover / 把 history.Alive 对齐（本驱动不做引擎操作）。
            // 用本驱动自己的「已上报」集合，而不是复制字段 `player.CombatDead`（后者要等 FlushState，
            // 而结算可能在同一帧被应用两次）。
            PMCombatPlayerSnapshot[] players = _model.CapturePlayers();
            for (int i = 0; i < players.Length; i++)
            {
                if (!players[i].Dead) { continue; }
                if (!_deathsReported.Add(players[i].NetId)) { continue; }

                PMR3Player player;
                if (!_playersByNetId.TryGetValue(players[i].NetId, out player) || player == null) { continue; }

                PlayerDeathsReported++;
                NotifyPlayerDied(player);
            }
        }

        /// <summary>
        /// 逐订阅者隔离的死亡通知（宿主的 freeze / history 写入不能因为一个订阅者的 bug 而消失）。
        /// 与 `PMR3Runtime.NotifyPlayerReplicated` 同一手法：捕获 + 显式计数 + 告警，不改变权威状态、
        /// 也不因此让会话 fault。
        /// </summary>
        private void NotifyPlayerDied(PMR3Player player)
        {
            Action<PMR3Player> handler = PlayerDied;
            if (handler == null) { return; }

            Delegate[] list = handler.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                try
                {
                    ((Action<PMR3Player>)list[i])(player);
                }
                catch (Exception ex)
                {
                    CallbackExceptions++;
                    Warn("[PMR6CombatDriver] PlayerDied 订阅者异常（已隔离，不影响权威状态）："
                         + ex.GetType().Name + " " + ex.Message);
                }
            }
        }

        /// <summary>逐订阅者隔离的终局通知（同上）。</summary>
        private void NotifyOutcomeFrozen(uint outcomeId, int winnerTeamId)
        {
            Action<uint, int> handler = OutcomeFrozen;
            if (handler == null) { return; }

            Delegate[] list = handler.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                try
                {
                    ((Action<uint, int>)list[i])(outcomeId, winnerTeamId);
                }
                catch (Exception ex)
                {
                    CallbackExceptions++;
                    Warn("[PMR6CombatDriver] OutcomeFrozen 订阅者异常（已隔离，不影响权威状态）："
                         + ex.GetType().Name + " " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Drain 兜底：R5 的待取缓冲只能由「无订阅者」或「订阅者抛异常」写入。
        /// 本驱动的订阅者不抛异常 ⇒ 正常路径下这里什么都不做；一旦真的取到条目，
        /// 计数可观测（`SettlementsBuffered`）。
        /// </summary>
        private void DrainBufferedSettlements()
        {
            if (_projectiles.PendingSettlementCount <= 0) { return; }

            PMProjectileSettlement[] items;
            int count = _projectiles.DrainSettlements(MaxApplyPerPump, out items);
            for (int i = 0; i < count; i++)
            {
                SettlementsBuffered++;
                SettlementsDrainedFromR5++;

                try
                {
                    ApplySettlementNow(items[i]);
                }
                catch (Exception ex)
                {
                    ApplySettlementFailure(ex);
                    return;
                }
            }
        }

        // ================================================================ 主线程 Pump（命令 + Tick）

        /// <summary>
        /// 主线程每帧入口（**必须早于 R5.Pump**）：处理上行攻击（DS）/ 裁决与结果（AP），再推进 core 时钟。
        ///
        /// 契约固定次序（宿主每帧）：`R6.Pump(now)` → `R5.Pump(now, step)` → `R6.FlushState(now)`。
        /// 本方法**不**调用 R5.Pump（投射物泵由宿主驱动，避免两处各推一次）。
        /// </summary>
        public void Pump(double wallNowMs)
        {
            EnsureThread();
            if (_disposed || _faulted) { return; }

            if (_wallStarted && wallNowMs < _wallMs)
            {
                Fault(PMR6CombatFaultReason.NonMonotonicWallClock,
                    "Pump 墙钟倒退：now=" + wallNowMs + " last=" + _wallMs);
                return;
            }

            _wallMs = wallNowMs;
            _wallStarted = true;
            Pumps++;

            if (_projectiles.IsFaulted)
            {
                Fault(PMR6CombatFaultReason.R5Faulted,
                    "R5 投射物驱动已 fault：" + _projectiles.FaultReason + " " + _projectiles.FaultError);
                return;
            }

            // ---------------------------------------------------------------- 1) 先推进权威时钟（回蓝）
            //
            // 次序不可交换：攻击合法性（普通攻击的蓝耗）按**请求时刻的权威资源**判定，
            // 而资源只在 core.Tick 里随墙钟推进。若先 RequestAttack 后 Tick，恰好卡在
            // 「这一段回蓝刚好到达」边界上的合法攻击会被判 InsufficientMana，而且该拒绝会作为
            // 终态写进 core 的攻击账本（同一 activationId 的幂等回显永远是拒绝）。
            if (_isServer && _model != null)
            {
                TickModel(wallNowMs);
            }

            ApplyInboundAttacks();
            if (_faulted) { return; }

            ApplyInboundAttackResults();
            if (_faulted) { return; }

            ApplyInboundMatchResults();
            if (_faulted) { return; }

            ApplyInboundResultAcks();
            if (_faulted) { return; }

            if (_isServer && _model != null)
            {
                // Drain 兜底：正常路径结算已在订阅回调里消费（订阅者不抛异常 ⇒ R5 不会落缓冲）。
                DrainBufferedSettlements();
                if (_faulted) { return; }
            }
            else
            {
                PrunePendingAttacks(wallNowMs);
            }
        }

        /// <summary>
        /// 推进 core 墙钟（回蓝），并记下「这一帧权威状态可能变化」：回蓝不经过任何显式入口，
        /// `FlushState` 必须重新比对一次权威快照才能把它复制出去。
        /// </summary>
        private void TickModel(double wallNowMs)
        {
            if (wallNowMs > _lastModelTickMs)
            {
                _tickAdvancedSincePublish = true;
            }

            // 只在 core 真的接受这根时钟时推进本地 Tick 水位：
            // 若 core 因某种原因拒了（本驱动已保证单调，正常不可达），下一帧仍会重试。
            if (_model.Tick(wallNowMs))
            {
                _lastModelTickMs = wallNowMs;
            }
        }

        private void ApplyInboundAttacks()
        {
            int limit = _inboundAttacks.Count < MaxApplyPerPump ? _inboundAttacks.Count : MaxApplyPerPump;
            for (int i = 0; i < limit; i++)
            {
                InboundAttack item = _inboundAttacks[0];
                _inboundAttacks.RemoveAt(0);
                HandleServerAttack(item);
                if (_faulted) { return; }
            }
        }

        private void ApplyInboundAttackResults()
        {
            int limit = _inboundAttackResults.Count < MaxApplyPerPump
                ? _inboundAttackResults.Count
                : MaxApplyPerPump;

            for (int i = 0; i < limit; i++)
            {
                InboundAttackResult item = _inboundAttackResults[0];
                _inboundAttackResults.RemoveAt(0);
                HandleAttackResult(item);
                if (_faulted) { return; }
            }
        }

        private void ApplyInboundMatchResults()
        {
            int limit = _inboundMatchResults.Count < MaxApplyPerPump
                ? _inboundMatchResults.Count
                : MaxApplyPerPump;

            for (int i = 0; i < limit; i++)
            {
                InboundMatchResult item = _inboundMatchResults[0];
                _inboundMatchResults.RemoveAt(0);
                HandleMatchResult(item);
                if (_faulted) { return; }
            }
        }

        private void ApplyInboundResultAcks()
        {
            int limit = _inboundResultAcks.Count < MaxApplyPerPump
                ? _inboundResultAcks.Count
                : MaxApplyPerPump;

            for (int i = 0; i < limit; i++)
            {
                InboundResultAck item = _inboundResultAcks[0];
                _inboundResultAcks.RemoveAt(0);
                HandleResultAck(item);
                if (_faulted) { return; }
            }
        }

        // ================================================================ DS：处理一条上行攻击

        private void HandleServerAttack(InboundAttack item)
        {
            PMR3Player player = item.Player;
            if (player == null || !player.NetId.IsValid) { return; }

            if (_model == null)
            {
                Fault(PMR6CombatFaultReason.ModelMissing, "DS 收到上行攻击但没有 PMCombatSession");
                return;
            }

            // 契约：**单次** core.RequestAttack。不允许「先试一次、失败再试一次」这类重复扣费写法。
            PMCombatAttackDecision decision = _model.RequestAttack(
                player.NetId.Value, item.ActivationId, item.IsSuper, item.AimX, item.AimZ, _wallMs);

            AttacksProcessed++;
            _stateDirty = true;

            // R5 权威裁决：用 core 的 decision（duplicate 返回的是**原始**决策，因此绝不会
            // 「把一条已接受的攻击反向拒掉」—— R5 账本自身也是终态不倒退）。
            //
            // 关于「后来 StaleId 会不会撤掉老的 Confirmed 弹」：不会，而且有两道拦截：
            //   · R5 侧：`TrySetActivationResult` 对已终态（含 Confirmed）的 activation 一律返回
            //     `AlreadyTerminal`，被容量淘汰的终态判决还留在有界 `_terminalMemory` 里，
            //     因此任何后来（含反向）的写入都不会释放/撤掉已 Confirmed 的弹；
            //   · AP 侧：本驱动的 pending attack 表是终态先到先胜（见 `HandleAttackResult`），
            //     已 Accepted 的攻击不会被一条迟到的 Rejected 反向撤销。
            // 因此这里对 core 的每一种拒绝都照实写入 R5（包括 StaleId：它会清掉那笔 activation
            // 残留的等待弹，而不可能倒退任何已终态结论）。
            PMActivationResult verdict = decision.Accepted
                ? PMActivationResult.Confirmed
                : PMActivationResult.Rejected;

            PMProjectileActivationApplyResult ignored;
            _projectiles.ResolveActivation(player.NetId.Value, item.ActivationId, verdict, _wallMs, out ignored);

            int reason = decision.Accepted ? 0 : (int)decision.Reason;
            try
            {
                player.ClientCombatAttackResultV1(item.ActivationId, decision.Accepted, reason);
            }
            catch (Exception ex)
            {
                Fault(PMR6CombatFaultReason.SendFailed,
                    "ClientCombatAttackResultV1 发送失败：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        // ================================================================ AP：处理一条攻击裁决

        private void HandleAttackResult(InboundAttackResult item)
        {
            PMR3Player player = item.Player;
            if (player == null || !player.NetId.IsValid) { return; }
            if (item.ActivationId == 0u) { return; }

            if (!_isServer && (LocalOwner == null || !ReferenceEquals(LocalOwner, player)))
            {
                // 不是本地 owner 的裁决（SP 不会收到 owner-only 可靠下行）：不处理也不 fault。
                return;
            }

            if (!item.Accepted && !IsKnownRejectReason(item.Reason))
            {
                // 未知字段 fail closed：拒绝码越界说明对面/链路不是本版本语义，不能瞎猜。
                Fault(PMR6CombatFaultReason.InvalidField,
                    "收到未知拒绝码 " + item.Reason + "（activation " + item.ActivationId + "）");
                return;
            }

            PendingAttack pending;
            if (!_pendingAttacks.TryGetValue(PendingKey(player.NetId.Value, item.ActivationId), out pending))
            {
                // 已被 TTL 退休的重发/迟到裁决：幂等吸收（不 fault、不反向改状态）。
                AttackResultsUnknown++;
                return;
            }

            AttackResultsApplied++;

            // ---- 终态不倒退（pending 表是一笔 attack 的"终态记忆"）----
            // 权威语义上一条 attack 的结论只能先写一次：
            //   · Accepted 后 core 不可能再把它翻成 Rejected（RequestAttack 的 duplicate 回显原始 decision），
            //     所以"已 Confirmed 又来一条 Rejected"只可能是链路/对面 bug；此时若照着撤假弹，
            //     会把已经合法出膛（甚至已被权威镜像接管）的弹反向撤销 —— 典型的"活镜像幽灵"。
            //   · Rejected 已经真实撤过弹了，迟到的 Accepted 也不得把它翻回去。
            if (pending.Revoked)
            {
                AttackResultsLateAfterTerminal++;
                return;
            }

            if (item.Accepted)
            {
                // 「Confirmed 仅记录不抢 R5 镜像接管」：接管时机与高水位由 R5 决定。
                pending.Confirmed = true;
                return;
            }

            if (pending.Confirmed)
            {
                AttackResultsLateAfterTerminal++;
                return;
            }

            AttackRejections++;
            LastRejectedActivationId = item.ActivationId;
            LastAttackRejectionReason = (PMCombatRejectReason)item.Reason;

            // Rejected：**真实撤销**该 activation 的全部假弹（不是只改 UI / 只改计数）。
            int revoked = _projectiles.CancelPredictedActivation(player, item.ActivationId, _wallMs);
            if (revoked > 0) { PredictionsCancelled += revoked; }
            pending.Revoked = true;
            if (revoked == 0 && pending.ProjectileCount > 0)
            {
                // 一颗都没撤到：说明 R5 侧已经先一步处理（例如 R5 自己的 Rejected 裁决），属正常。
                AttackResultsUnknown++;
            }
        }

        private static bool IsKnownRejectReason(int reason)
        {
            if (reason < 0) { return false; }
            return reason <= (int)PMCombatRejectReason.InvalidClock;
        }

        // ================================================================ AP：处理一条比赛结果

        private void HandleMatchResult(InboundMatchResult item)
        {
            if (_isServer) { return; }
            if (item.Player == null || !item.Player.NetId.IsValid) { return; }

            PMR3Player localOwner = LocalOwner;
            if (localOwner == null || !ReferenceEquals(localOwner, item.Player)) { return; }

            if (item.OutcomeId == 0u)
            {
                Fault(PMR6CombatFaultReason.InvalidField, "比赛结果的 outcomeId 为 0（无效哨兵）");
                return;
            }

            if (!IsKnownWinner(item.WinnerTeamId))
            {
                Fault(PMR6CombatFaultReason.InvalidField,
                    "比赛结果的胜方队伍 " + item.WinnerTeamId + " 不在本端已知名册内");
                return;
            }

            if (_clientResultStored)
            {
                if (item.OutcomeId == _clientOutcomeId && item.WinnerTeamId == _clientWinnerTeamId)
                {
                    // 同一 outcome、同一胜方：幂等（重发是正常路径），仍补一次 Ack。
                    MatchResultDuplicates++;
                    _clientResultAckPending = true;
                    return;
                }

                // 冲突（不同 outcome 或同 outcome 不同胜方）：fail closed，绝不改写已保存结论。
                Fault(PMR6CombatFaultReason.ConflictingOutcome,
                    "比赛结果冲突：已保存 outcome=" + _clientOutcomeId + " winner=" + _clientWinnerTeamId
                    + "，收到 outcome=" + item.OutcomeId + " winner=" + item.WinnerTeamId);
                return;
            }

            _clientResultStored = true;
            _clientOutcomeId = item.OutcomeId;
            _clientWinnerTeamId = item.WinnerTeamId;
            _clientResultAckPending = true;
        }

        /// <summary>胜方是否可验证：0 = 无唯一胜者；否则必须命中本端已复制的队伍号。</summary>
        private bool IsKnownWinner(int winnerTeamId)
        {
            if (winnerTeamId == 0) { return true; }
            if (winnerTeamId < 0) { return false; }

            foreach (KeyValuePair<PMR3Player, PlayerAdapter> kv in _adapters)
            {
                if (kv.Key.CombatTeamId == winnerTeamId) { return true; }
            }

            return false;
        }

        // ================================================================ DS：处理一条结果确认

        private void HandleResultAck(InboundResultAck item)
        {
            if (!_isServer) { return; }
            if (item.Player == null || !item.Player.NetId.IsValid) { return; }
            if (item.OutcomeId == 0u) { return; }

            if (!_outcomeFrozen || item.OutcomeId != _frozenOutcomeId)
            {
                // 与本会话冻结的 outcome 不符的确认不能推进闭环（不 fault：可能是重发/迟到）。
                ResultAckMismatches++;
                return;
            }

            // 归属：认证由 RPC 层（Owner 判定）保证；这里再要求它确实是本会话**名册**成员，
            // 否则一个非名册对象（将来某条新复制路径）的 ACK 会虚增可观测的 `ResultAckCount`。
            if (_model.GetPlayer(item.Player.NetId.Value) == null)
            {
                ResultAckMismatches++;
                return;
            }

            if (!_resultAcked.Add(item.Player.NetId.Value))
            {
                ResultAckDuplicates++;
            }
        }

        // ================================================================ 主线程 FlushState（复制 + 结果）

        /// <summary>
        /// 主线程每帧出口（**必须晚于 R5.Pump**）：把权威状态复制出去，并推进终局结果闭环。
        ///
        /// DS：脏时把 9 个战斗字段经 `PublishCombatState` 提交；终局时冻结 outcome + 摘要，
        /// 逐在线 owner 可靠下发 `ClientCombatMatchResultV1`，按「全 ACK 或 5000ms 宽限」置
        /// <see cref="ResultReadyForLobby"/>。
        /// AP：若已幂等保存结果，则在这里回 `ServerCombatResultAckV1`（**不在回调里发送**）。
        /// </summary>
        public void FlushState(double wallNowMs)
        {
            EnsureThread();
            if (_disposed || _faulted) { return; }

            if (_wallStarted && wallNowMs < _wallMs)
            {
                Fault(PMR6CombatFaultReason.NonMonotonicWallClock,
                    "FlushState 墙钟倒退：now=" + wallNowMs + " last=" + _wallMs);
                return;
            }

            _wallMs = wallNowMs;
            _wallStarted = true;
            Flushes++;

            if (_projectiles.IsFaulted)
            {
                Fault(PMR6CombatFaultReason.R5Faulted,
                    "R5 投射物驱动已 fault：" + _projectiles.FaultReason + " " + _projectiles.FaultError);
                return;
            }

            if (_isServer)
            {
                PublishStateIfDirty();
                if (_faulted) { return; }
                UpdateOutcome(wallNowMs);
            }
            else
            {
                FlushClientResultAck();
            }
        }

        private void PublishStateIfDirty()
        {
            if (_model == null) { return; }

            // 「需要发布」两种来源：
            //   · 显式状态转移（攻击/结算/名册/终局）⇒ `_stateDirty`；
            //   · 随墙钟持续变化（回蓝，发生在 Tick 里）⇒ `_tickAdvancedSincePublish`。
            // 后者不经过任何显式入口，所以不能只看 `_stateDirty`，否则回蓝永远复制不出去
            //（对客户端表现为「蓝量卡在扣费后的值，直到下一次命中才跳到正确值」）。
            if (!_stateDirty && !_tickAdvancedSincePublish) { return; }
            _tickAdvancedSincePublish = false;

            PMCombatMatchOutcome outcome = _model.Outcome;
            PMCombatPlayerSnapshot[] players = _model.CapturePlayers();

            bool allPublished = true;
            for (int i = 0; i < players.Length; i++)
            {
                PMR3Player player;
                if (!_playersByNetId.TryGetValue(players[i].NetId, out player) || player == null)
                {
                    if (!players[i].Connected)
                    {
                        // 已断线的名册成员（`UnbindPlayer` 之后）已经没有 owner 可复制：
                        // 这不是「还没绑定」，不能让它把状态永远保持在脏（否则每帧都白比一遍）。
                        continue;
                    }

                    // 在线但还没绑定：状态保持脏，下一帧重试（不静默丢初值）。
                    allPublished = false;
                    continue;
                }

                PublishedCombatState next = PublishedCombatState.From(players[i], outcome);

                PublishedCombatState previous;
                if (_publishedByNetId.TryGetValue(players[i].NetId, out previous) && previous.SameAs(next))
                {
                    // 无变化就不重复写：赋值会触发 9 条 RepNotify（客户端侧是 9 次通知），
                    // 而「没变」的情况下它不可能携带任何新信息。
                    continue;
                }

                bool written = player.PublishCombatState(
                    next.HeroId, next.TeamId, next.Hp, next.MaxHp, next.Dead,
                    next.Mana, next.SuperEnergy, next.MatchEnded, next.WinnerTeamId);

                if (written)
                {
                    _publishedByNetId[players[i].NetId] = next;
                    StatePublishCount++;
                }
                else
                {
                    // 被权限拒绝（例如该副本不是权威世界）：保持脏，下帧重试且可观测。
                    StatePublishRejected++;
                    allPublished = false;
                }
            }

            _stateDirty = !allPublished;
        }

        private void UpdateOutcome(double wallNowMs)
        {
            if (_model == null) { return; }

            if (!_outcomeFrozen)
            {
                if (!_model.Ended) { return; }

                PMCombatMatchOutcome outcome = _model.Outcome;
                _outcomeFrozen = true;
                _frozenOutcomeId = outcome.OutcomeId;
                _frozenWinnerTeamId = outcome.WinnerTeamId;
                _frozenReason = outcome.Reason;
                _frozenWallMs = wallNowMs;
                _resultReadyForLobby = false;
                _resultAcked.Clear();
                _resultSentOnce = false;
                _lastResultSendMs = double.NegativeInfinity;

                byte[] summary;
                if (!TryBuildResultSummary(outcome, out summary))
                {
                    Fault(PMR6CombatFaultReason.ResultEncodeFailed, "结果摘要编码失败");
                    return;
                }

                _resultSummary = summary;
                OutcomeFrozenCount++;

                // 逐订阅者隔离：宿主回调（记分/上报）抛异常不得打断下面的「冻结后重发 + 就绪判定」，
                // 否则整包结果会在那一帧静默停摆（客户永远收不到胜负）。
                NotifyOutcomeFrozen(_frozenOutcomeId, _frozenWinnerTeamId);
            }
            else
            {
                // 冻结后只观测：core 自身也只锁一次；任何后续断线/迟到结算都不得改写本结论。
                PMCombatMatchOutcome current = _model.Outcome;
                if (current.OutcomeId != _frozenOutcomeId || current.WinnerTeamId != _frozenWinnerTeamId)
                {
                    OutcomeConflictCount++;
                }
            }

            // 重发：内容永不改变（同一 outcome、同一胜方），每 ResultResendMs 一次。
            if (_lastResultSendMs == double.NegativeInfinity
                || wallNowMs - _lastResultSendMs >= (double)ResultResendMs)
            {
                SendMatchResult(wallNowMs);
                if (_faulted) { return; }
            }

            if (_resultReadyForLobby) { return; }

            if (AllConnectedOwnersAcked())
            {
                _resultReadyForLobby = true;
                ResultReadyByAckCount++;
                return;
            }

            if (wallNowMs - _frozenWallMs >= (double)ResultGraceMs)
            {
                // 超时是**有界退场**，不是「所有客户端都已观察到」的证明（统计可见）。
                _resultReadyForLobby = true;
                ResultReadyByGraceCount++;
            }
        }

        private bool AllConnectedOwnersAcked()
        {
            if (_model == null) { return false; }

            PMCombatPlayerSnapshot[] players = _model.CapturePlayers();
            for (int i = 0; i < players.Length; i++)
            {
                if (!players[i].Connected) { continue; }
                if (!_resultAcked.Contains(players[i].NetId)) { return false; }
            }

            // 走到这里说明「所有在线 owner 都已确认」；没有在线 owner 时也没有人可等。
            return true;
        }

        private void SendMatchResult(double wallNowMs)
        {
            _lastResultSendMs = wallNowMs;
            if (_resultSentOnce) { ResultResends++; } else { ResultSends++; _resultSentOnce = true; }

            foreach (KeyValuePair<PMR3Player, PlayerAdapter> kv in _adapters)
            {
                PMR3Player player = kv.Key;
                if (player == null || !player.NetId.IsValid) { continue; }

                if (_model != null)
                {
                    PMCombatPlayerSnapshot snapshot = _model.GetPlayer(player.NetId.Value);
                    if (snapshot == null || !snapshot.Connected) { continue; }
                }

                try
                {
                    player.ClientCombatMatchResultV1(_frozenOutcomeId, _frozenWinnerTeamId);
                }
                catch (Exception ex)
                {
                    Fault(PMR6CombatFaultReason.SendFailed,
                        "ClientCombatMatchResultV1 发送失败：" + ex.GetType().Name + " " + ex.Message);
                    return;
                }
            }
        }

        private bool TryBuildResultSummary(PMCombatMatchOutcome outcome, out byte[] summary)
        {
            summary = null;

            try
            {
                PMCombatPlayerSnapshot[] players = _model.CapturePlayers();

                // 只用 PMNetWriter 的原语 + 有限数值字段（无字符串 ⇒ 没有 UTF8 长度/编码歧义）。
                PMNetWriter writer = new PMNetWriter(256);
                writer.WriteTag(1, PMWireType.Varint);
                writer.WriteUInt32(outcome.OutcomeId);
                writer.WriteTag(2, PMWireType.Varint);
                writer.WriteInt32(outcome.WinnerTeamId);
                writer.WriteTag(3, PMWireType.Varint);
                writer.WriteEnum((int)outcome.Reason);
                writer.WriteTag(4, PMWireType.Varint);
                writer.WriteUInt32((uint)players.Length);

                for (int i = 0; i < players.Length; i++)
                {
                    PMCombatPlayerSnapshot p = players[i];
                    writer.WriteTag(5, PMWireType.Varint);
                    writer.WriteUInt32(p.NetId);
                    writer.WriteTag(6, PMWireType.Varint);
                    writer.WriteInt32(p.Uid);
                    writer.WriteTag(7, PMWireType.Varint);
                    writer.WriteInt32(p.TeamId);
                    writer.WriteTag(8, PMWireType.Varint);
                    writer.WriteInt32(p.HeroId);
                    writer.WriteTag(9, PMWireType.Varint);
                    writer.WriteInt32(p.Hp);
                    writer.WriteTag(10, PMWireType.Varint);
                    writer.WriteInt32(p.MaxHp);
                    writer.WriteTag(11, PMWireType.Varint);
                    writer.WriteBool(p.Dead);
                }

                summary = writer.ToArray();
                return summary != null && summary.Length > 0;
            }
            catch (Exception)
            {
                summary = null;
                return false;
            }
        }

        private void FlushClientResultAck()
        {
            if (!_clientResultAckPending || !_clientResultStored) { return; }

            PMR3Player owner = LocalOwner;
            if (owner == null || !owner.NetId.IsValid) { return; }

            try
            {
                owner.ServerCombatResultAckV1(_clientOutcomeId);
                _clientResultAckPending = false;
            }
            catch (Exception ex)
            {
                Fault(PMR6CombatFaultReason.SendFailed,
                    "ServerCombatResultAckV1 发送失败：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        // ================================================================ 墙钟

        /// <summary>
        /// 显式推高本驱动的墙钟水位（单调；倒退返回 false 且**不改任何状态**）。
        ///
        /// 用途：宿主在同一帧里先驱动 R5（投射物泵）再驱动 R6 时，把同一个 now 提前告知本驱动，
        /// 保证结算应用用的墙钟不会落后于 core 时钟。
        /// </summary>
        public bool UpdateClock(double wallNowMs)
        {
            EnsureThread();
            if (_disposed) { return false; }
            if (!IsFiniteD(wallNowMs) || wallNowMs < 0.0) { return false; }
            if (_wallStarted && wallNowMs < _wallMs) { return false; }

            _wallMs = wallNowMs;
            _wallStarted = true;
            return true;
        }

        private double EffectiveWall(double wallNowMs)
        {
            if (!IsFiniteD(wallNowMs) || wallNowMs < 0.0) { return _wallMs; }
            return wallNowMs > _wallMs ? wallNowMs : _wallMs;
        }

        // ================================================================ DS：可信授权策略工厂

        /// <summary>
        /// 生成 DS 侧可信授权策略（**独立 public 工厂**：宿主在创建 R5 driver **之前**就能拿到它）。
        ///
        /// 策略的每一条信息都来自「认证身份 + core + 宿主位置」，没有一条来自客户端自报：
        ///   · **slot**：`core.TryAuthorizeProjectile` 只认已被批准且方向匹配的未消费槽；
        ///   · **trustedSpec**：只来自 core 冻结计划（客户端 payload 里根本没有 spec/damage 字段）；
        ///   · **ownerPosition**：只来自宿主注入的 <paramref name="tryGetOwnerPosition"/>；
        ///   · **verdict**：core 批准即 Confirmed，其余一律拒绝（fail closed，不降级）。
        /// </summary>
        /// <param name="model">权威核心（必须非 null，且与后续 R6 driver 用的是同一个对象）。</param>
        /// <param name="tryGetOwnerPosition">真实位置/冻结有效性查询（返回 false ⇒ 拒绝授权）。</param>
        /// <param name="wallClockNow">单调墙钟（毫秒）。**不得**读 UnityEngine.Time（引擎无关）。</param>
        public static IPMR5ProjectileAuthorityPolicy CreateAuthorityPolicy(
            PMCombatSession model,
            PMCombatTryGetOwnerPosition tryGetOwnerPosition,
            Func<double> wallClockNow)
        {
            if (model == null) { throw new ArgumentNullException("model"); }
            if (tryGetOwnerPosition == null) { throw new ArgumentNullException("tryGetOwnerPosition"); }
            if (wallClockNow == null) { throw new ArgumentNullException("wallClockNow"); }

            return new AuthorityPolicy(model, tryGetOwnerPosition, wallClockNow);
        }

        /// <summary>
        /// DS 可信授权策略：上行 intent 与权威世界之间**唯一**的可信桥。
        /// </summary>
        private sealed class AuthorityPolicy : IPMR5ProjectileAuthorityPolicy
        {
            private readonly PMCombatSession _model;
            private readonly PMCombatTryGetOwnerPosition _tryGetOwnerPosition;
            private readonly Func<double> _wallClockNow;

            public AuthorityPolicy(PMCombatSession model, PMCombatTryGetOwnerPosition tryGetOwnerPosition,
                Func<double> wallClockNow)
            {
                _model = model;
                _tryGetOwnerPosition = tryGetOwnerPosition;
                _wallClockNow = wallClockNow;
            }

            public bool TryAuthorizeSpawn(
                PMR3Player player,
                PMProjectileSpawnIntent intent,
                out PMProjectileSpec trustedSpec,
                out PMVector3 ownerPosition,
                out PMActivationResult verdict,
                out string error)
            {
                trustedSpec = null;
                ownerPosition = PMVector3.Zero;
                verdict = PMActivationResult.Pending;
                error = null;

                if (player == null || !player.NetId.IsValid)
                {
                    error = "player/NetId 无效";
                    return false;
                }

                if (intent == null)
                {
                    error = "intent 为空";
                    return false;
                }

                double now = _wallClockNow();
                if (!IsFiniteD(now) || now < 0.0)
                {
                    error = "墙钟非法";
                    return false;
                }

                // ① **先向宿主确认真实枪口**，再问 core 要槽位。
                //
                // 为什么次序不能反：core 的 `TryAuthorizeProjectile` 一旦成功就会消耗一个方向槽 +
                // 一条已授权 key 记录（终态、等 TTL 过期）。若先问 core 再验位置，一次
                // 「宿主位置暂时不可用」（Mover 未就绪 / 刚断线 / freeze 未完成）就会白扣一个槽，
                // 而同一笔攻击的 N 颗弹全是同一个枪口 ⇒ N 个槽被一次性吃掉，等位置恢复后
                // 这批弹再也无法被授权。位置是宿主的真相，优先于 core 的额度。
                PMVector3 position;
                if (!_tryGetOwnerPosition(player, out position))
                {
                    // 位置不可信 ⇒ 不允许生成（绝不拿 0 向量去开一枪）。
                    error = "owner 位置不可用/不可信";
                    return false;
                }

                if (!IsFiniteV(position))
                {
                    error = "owner 位置非法";
                    return false;
                }

                // ② 核心是**唯一**的批准来源：slot / epoch / owner / origin / 方向容差 / TTL 全由它判。
                PMProjectileSpec spec;
                PMCombatRejectReason reason;
                if (!_model.TryAuthorizeProjectile(player.NetId.Value, intent, now, out spec, out reason)
                    || spec == null)
                {
                    error = "core 拒绝授权：" + reason;
                    return false;
                }

                trustedSpec = spec;
                ownerPosition = position;
                verdict = PMActivationResult.Confirmed;
                return true;
            }
        }

        // ================================================================ 故障 / 工具

        /// <summary>是否处于故障态（宿主必须终止会话）。</summary>
        private void Fault(PMR6CombatFaultReason reason, string message)
        {
            if (_faulted) { return; }
            _faulted = true;
            _faultReason = reason;
            _faultError = message;
            Warn("[PMR6CombatDriver] 会话失败（" + reason + "）：" + message);
        }

        private static void Warn(string message)
        {
            Action<string> handler = PMR3Player.CombatWarn;
            if (handler != null)
            {
                handler(message);
            }
        }

        private void EnsureThread()
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                ThreadViolations++;
                throw new InvalidOperationException(
                    "[PMR6CombatDriver] 跨线程调用：驱动只允许在构造它的线程（主线程）上使用"
                    + "（复制应用 / RPC 收发 / 业务处理都在主线程）。");
            }
        }

        private static bool IsFiniteF(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFiniteD(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool IsFiniteV(PMVector3 v)
        {
            return IsFiniteF(v.X) && IsFiniteF(v.Y) && IsFiniteF(v.Z);
        }

        // ================================================================ 释放

        /// <summary>
        /// 幂等释放：**只撤自己的那一份**（结算订阅 + 本地账 + 接缝引用），
        /// **不** Dispose R5 驱动、**不**触碰别的会话（不调全局 ClearAll）、**不**触发断线判胜。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;

            if (_isServer)
            {
                try { _projectiles.Settlement -= OnProjectileSettlement; }
                catch (Exception) { }
            }

            foreach (KeyValuePair<PMR3Player, PlayerAdapter> kv in _adapters)
            {
                if (ReferenceEquals(kv.Key.CombatDriver, kv.Value))
                {
                    kv.Key.CombatDriver = null;
                }
            }

            _adapters.Clear();
            _playersByNetId.Clear();
            _inboundAttacks.Clear();
            _inboundAttackResults.Clear();
            _inboundMatchResults.Clear();
            _inboundResultAcks.Clear();
            _pendingAttacks.Clear();
            _deathsReported.Clear();
            _publishedByNetId.Clear();
            _nextActivationByOwner.Clear();
            _resultAcked.Clear();
            _resultSummary = null;

            PlayerDied = null;
            OutcomeFrozen = null;
        }

        /// <summary>单行诊断摘要。</summary>
        public string Describe()
        {
            return "PMR6CombatDriver server=" + _isServer
                   + " epoch=" + _epoch
                   + " players=" + _adapters.Count
                   + " pendingAttacks=" + _pendingAttacks.Count
                   + " applied=" + SettlementsApplied
                   + " rejected=" + SettlementsRejected
                   + " buffered=" + _projectiles.PendingSettlementCount
                   + " frozen=" + _outcomeFrozen
                   + (_outcomeFrozen ? "(outcome=" + _frozenOutcomeId + " winner=" + _frozenWinnerTeamId
                       + " acks=" + _resultAcked.Count + " ready=" + _resultReadyForLobby + ")" : string.Empty)
                   + " faulted=" + _faulted
                   + (_faulted ? "(" + _faultReason + ")" : string.Empty);
        }
    }
}
