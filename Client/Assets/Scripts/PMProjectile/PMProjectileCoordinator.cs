// ============================================================================
//  PMProjectileCoordinator —— R5-A3：真实核心整合器（M09 纯核心）
// ============================================================================
//  契约来源（只读）：
//    · Docs/plans/net-r5-projectile-contract.md「A3/B1集成冻结」（最新）+ 语义/资源/命中验证三节
//    · Client/Assets/Scripts/PMProjectile/PMProjectileContracts.cs（主 Agent 冻结共享类型）
//    · Docs/plans/_r5_lifecycle_review.md §3（A1 公开 API 口径）
//    · Docs/plans/_r5_validation_report.md §3（A2 公开 API 口径）
//
//  本文件是**整合器**，不重写第二套核心：
//    · 生命周期 / 假弹接管 / 激活账本 / 停止墓碑 → PMProjectileLifecycle（A1，只调用）
//    · 三类 Pending 队列                          → PMProjectilePendingSpawns / Verifies / Hits（A1，只调用）
//    · 目标历史 / L0–L4 命中验证                   → PMProjectileHistory / PMProjectileValidator（A2，只调用）
//    本文件只做「联动」：可信入参消毒、队列生命周期串联、去重提交、结算出口、
//    自有记账（verify 配额 / 每 key 出生记录）、有界输出队列与显式溢出计数。
//
//  硬边界（逐条对应冻结契约）：
//    1) 纯 C#：netstandard2.0 + C#7.3，零 UnityEngine / 零 UE / 零 business 类型。
//    2) 不引用 B1 的 PMProjectileCodec（B1 并行中，禁止依赖或等待）。
//    3) **不直接扣血**：本文件没有任何 HP/伤害出口；DrainSettlements 是 R6 的**唯一**接收点。
//    4) **无外部回调回入**：所有会消费状态的 API 都是「先提交、后独占返回」；
//       宿主运动 hook 只允许是纯函数（见 IPMProjectileHostMotion），不得回调整合器。
//    5) 全部对外队列有界。**溢出即显式失败**（R5-A3 返工修订；原实现「丢最旧 + 计数」被否决）：
//       四个出口队列（Decisions / SpawnReady / Stopped / Settlements）一旦在调用入口已达容量、
//       或推送时写入会越界，立即把整合器置为**永久 Faulted**（本 epoch 停止接纳任何变更型调用，
//       宿主必须断连并升 epoch 重建），并抛出 InvalidOperationException。
//       既有排队项目**不被覆盖**（不丢已接受出口 ⇒ 不丢真实伤害；已提交的去重也不会被静默吞掉），
//       数据不完整时绝不继续假装成功。所有变更型调用（含 PurgeExpired / ResolveActivation /
//       ClearOwner / ResetState）都会看到 Faulted；Drain* 仍可取出既有项目（便于宿主处置），
//       AdvanceEpoch 是唯一的恢复路径。
//    6) **预留假弹可原地升级为权威**（R5-A3 返工修订）：admission 先用 TryRegisterPredicted 预留
//       ProjectileId（否则同一 activation 的多弹乱序解挂会被 ID 高水位误拒），activation 变为
//       Confirmed 时调用 PMProjectileLifecycle.TryPromoteReservedToAuthority 原地升级
//       （ID / Origin / Key 不变、权威 NetId 真实非 0、只有 Confirmed 可升级、幂等）。
//       只有这样 CatchUpMotion 才能真的推进玩家弹（升级前它是 NotAuthority）。
//    7) **命中上报必须绑定认证 owner**：ReportHits 要求 authenticatedOwnerNetId 与 batch.Key.OwnerNetId
//       一致（契约「OwnerNetId 必须取认证会话绑定网络玩家而非客户端自报」）；尚未登记的 key（B 队列）
//       只接受 Origin=ClientPredicted，否则客户端能用自报的 ServerDirect key 预先“占位”未来的权威弹。
//    8) **宿主运动 hook 失败必须 fail closed**：TryStep 返回 false / 抛异常 / 输出非 finite
//       （以及 TryStop 抛异常 / 非 finite 停止点）一律**不推进**并按当前位置停止
//       （显式 HostMotionFaulted + 停止出口），绝不回退成直线穿墙；
//       传给 hook 的 snapshot 是深 clone（保持真实位置，hook 改不回账本）。
//
//  客户端预测 Spawn admission（RequestSpawn）：
//    · 可信入参：authenticatedOwnerNetId / authorityNetId / ownerPosition / trustedSpec /
//      trustedAllowedTargets —— 全部由可信宿主给出，**不读 request 里的权威面**。
//    · epoch 一致：key.Epoch 必须等于本会话 epoch，否则 StaleEpoch。
//    · 10m 枪口：|SpawnPosition - ownerPosition| <= PMProjectileLimits.MuzzleToleranceM(10m)。
//    · 无客户端权威字段写入：request.State 的 AuthorityNetId / ActivationId / HitTargets /
//      AllowedTargets / Stopped / Hidden / TakenOver / StopWallTimeMs / TombstoneUntilMs / MoveTimeMs
//      一律丢弃并重建；request.Spec **整份丢弃**（client 请求 spec 不得被当权威配置）。
//    · 只保留三样客户端意图：位置（SpawnPosition，已过 10m 门）、velocity 的**方向**（归一化后
//      乘 trustedSpec.SpeedMps）、yaw。
//    · ID 在**收请求时预留**：立即 TryRegisterPredicted（含 pending activation），
//      因此后续「乱序解挂」只消费释放项、不再二次注册，不会被 ID 高水位误拒。
//
//  可信 ServerDirect：ServerDirectSpawn 是**独立入口**，且是唯一允许 ActivationId=0 的入口；
//    ServerDirect 镜像不收集候选（L0 按构造 Confirmed，永不进 C 队列）、不自行结算。
//
//  三类 Pending 的联动口径：
//    · A PMProjectilePendingSpawns：activation 未定 → 挂起 payload（128/owner、总 1024、TTL2000ms）。
//      确认时才发 SpawnReady（=「不生成幽灵弹」）；拒绝时释放项 RemovedInstance 指示实际撤销。
//    · B PMProjectilePendingVerifies：**该 key 连登记都还没有**（命中上报先到 / 生成请求丢失重传）→
//      原样暂存原始上行（未消毒，交给回放后的 L0–L4）；生成后按 FIFO 回放。
//    · C PMProjectilePendingHits：弹已登记但 activation 仍 Pending（L0 判 Pending）→
//      只收**已消毒结论** PMProjectileValidatedHitSet；确认时**直接结算这批候选，不重跑历史**。
//
//  结算唯一性：命中目标去重提交（TryAddHitTarget）发生在入队**之前**；Reject/TTL/capacity 不结算；
//    最终结算前用权威历史复核目标 epoch/stream/alive 仍有效（不重跑 L0–L4 几何）。
//
//  运动：整毫秒、单步 <= 50ms、总预算 1000ms（恶意巨大 loop 被钳并计数）；
//    写回一律走 PMProjectileLifecycle.TryAdvanceMotion（保护字段不被覆盖）；
//    本组只做直线核心运动，**不声称不穿墙**（场景/Unity 碰撞查询留 C 阶段）。
// ============================================================================

using System;
using System.Collections.Generic;
using PMNet.Mover;

namespace PMNet.Projectile
{
    /// <summary>整合器自有上限。共享契约没有覆盖的记账面在这里显式给出（不留无界容器）。</summary>
    public static class PMProjectileCoordinatorLimits
    {
        /// <summary>单步运动上限（毫秒）。契约「整毫秒 &lt;= 50」。</summary>
        public const int MaxCatchUpStepMs = 50;

        /// <summary>单次追赶的总预算（毫秒）。恶意巨大的 totalMs 被钳到它并计数。</summary>
        public const int MaxCatchUpBudgetMs = 1000;

        public const int MaxDecisions = 512;
        public const int MaxSpawnEvents = 512;
        public const int MaxStopEvents = 512;
        public const int MaxSettlements = 512;

        /// <summary>
        /// 整合器自有记账（verify 配额 / 出生记录 / 暂存去重预留）的 key 上限。
        /// 取值为「权威 1024 + 预测 1024 + 挂起 1024 + B key 128」之和的量级；
        /// 正常链路（受契约容量约束）不会触发。**触发即 Faulted**（不静默忘掉记账）。
        /// </summary>
        public const int MaxKeyRecords = 4096;

        /// <summary>追赶循环的硬步数上限（即使预算被改小也不会失控）。</summary>
        public const int MaxCatchUpSteps = 64;

        /// <summary>
        /// 「权威注册后的首次追赶」里预测窗口的折算分母（R5-B2c 返工）：
        /// 预算里的预测项 = predictionMs / 本值。
        ///
        /// 为什么取 1/2：客户端声明的 <c>predictionMs</c> 是它自己的**预测提前量**（一个来回），
        /// 而权威侧要补的是「客户端已经领先、但权威还没跑」的那一段，量级是**单程**，
        /// 即 predictionMs 的一半。挂起时长另算（<see cref="PendingElapsedMs"/> 明说它必须叠加进追赶窗口）。
        /// </summary>
        public const int PredictionCatchUpDivisor = 2;

        /// <summary>首次追赶里挂起项的防御上限（挂起 TTL 的 4 倍，避免异常墙钟把预算顶到 int 溢出）。</summary>
        public const int MaxPendingCatchUpMs = 8000;
    }

    /// <summary>
    /// 整合器永久故障原因（Faulted）。
    ///
    /// 语义：**一次故障 = 本 epoch 报销**。故障后本对象拒绝一切变更型调用
    /// （抛出 <see cref="System.InvalidOperationException"/>），宿主必须记录、断连、升 epoch 重建；
    /// 这样「数据不完整」永远不会被当成成功继续跑（原实现「丢最旧 + 计数」会静默丢确认/结算）。
    /// </summary>
    public enum PMProjectileFaultReason : byte
    {
        None = 0,

        /// <summary>激活决策出口队列将越界。</summary>
        DecisionQueueOverflow = 1,

        /// <summary>权威生成出口队列将越界。</summary>
        SpawnEventQueueOverflow = 2,

        /// <summary>停止通知出口队列将越界。</summary>
        StopEventQueueOverflow = 3,

        /// <summary>结算出口队列将越界（**绝不丢已接受出口 ⇒ 不丢真实伤害**）。</summary>
        SettlementQueueOverflow = 4,

        /// <summary>整合器自有记账表将越界。</summary>
        KeyRecordTableOverflow = 5,

        /// <summary>内部不变式被破坏（如 Confirmed 却无法原地升级）：宁可故障，不假装成功。</summary>
        InternalInvariant = 6,

        /// <summary>
        /// 生命周期侧的「待取释放队列」发生了溢出丢弃。
        /// 该队列是 A1 内部的**中转**队列（容量 = 预测活对象上限 1024，且整合器每次调用都抽干），
        /// 正常链路不可达；一旦发生就是「需要调用方撤销的假弹通知」被静默丢掉，
        /// 后果是永久假弹，因此把它提升为显式故障。
        /// </summary>
        LifecycleReleaseDrop = 7,
    }

    // ------------------------------------------------------------------ 登记

    /// <summary>admission 结果。每个失败原因都是明确枚举，不是布尔。</summary>
    public enum PMProjectileAdmissionResult : byte
    {
        Admitted = 0,

        /// <summary>同一 key 已经登记过（id 复用 / 重复请求）。</summary>
        AlreadyAdmitted = 1,

        /// <summary>可信 owner 与请求自称的 owner 不一致，或 owner 为 0。</summary>
        UntrustedOwner = 2,

        InvalidAuthority = 3,
        StaleEpoch = 4,
        InvalidRequest = 5,

        /// <summary>velocity 方向非 finite 或长度为 0（方向意图不可用）。</summary>
        InvalidDirection = 6,

        /// <summary>位置非 finite。</summary>
        InvalidPosition = 7,

        /// <summary>枪口离 owner 超过 10m。</summary>
        MuzzleTooFar = 8,

        /// <summary>客户端预测必须携带非 0 activationId（0 只允许可信 ServerDirect）。</summary>
        InvalidActivationId = 9,

        /// <summary>trustedSpec 缺失或数值非法。</summary>
        InvalidSpec = 10,

        /// <summary>该 activation 已被权威裁决为 Rejected（终态不倒退）。</summary>
        RevokedActivation = 11,

        /// <summary>生命周期拒收 ID（非单调 / 重复 / owner 或 registry 容量）。</summary>
        IdNotReserved = 12,

        /// <summary>挂起队列容量（128/owner 或总 1024）。</summary>
        Capacity = 13,

        InvalidClock = 14,
    }

    public struct PMProjectileAdmissionOutcome
    {
        public PMProjectileAdmissionResult Result;
        public PMProjectileKey Key;
        public uint ActivationId;
        public PMActivationResult Activation;

        /// <summary>true = 该 spawn 正在等 activation（进了 A 队列，尚未发 SpawnReady）。</summary>
        public bool SpawnPending;

        /// <summary>true = 本次已发出 SpawnReady（可直接落地实例）。</summary>
        public bool Spawned;

        /// <summary>A1 生命周期登记结果（诊断 / 精确原因）。</summary>
        public PMProjectileRegisterResult Register;

        /// <summary>A1 挂起队列结果（诊断 / 精确原因）。</summary>
        public PMProjectilePendingSpawnResult PendingSpawnResult;

        public PMProjectileClockCheck Clock;

        public bool IsAdmitted { get { return Result == PMProjectileAdmissionResult.Admitted; } }
    }

    // -------------------------------------------------------------- 命中上报

    public enum PMProjectileHitReportResult : byte
    {
        /// <summary>Confirmed 且有命中：已产出结算（去重已提交）。</summary>
        Settled = 0,

        /// <summary>L0 Pending：已消毒结论进了 C 队列，**不结算**。</summary>
        Pending = 1,

        /// <summary>该 key 尚未生成：原始上报进了 B 队列，等生成后按序回放。</summary>
        StashedForSpawn = 2,

        /// <summary>通过验证但没有可结算命中（无命中 / 全被去重或过滤）。</summary>
        NoSettleableHit = 3,

        Rejected = 4,
        Retired = 5,
        TombstoneExpired = 6,
        QuotaExhausted = 7,
        Capacity = 8,
        UnknownKey = 9,
        StaleEpoch = 10,
        Invalid = 11,
        InvalidClock = 12,

        /// <summary>
        /// 上报自称的 OwnerNetId 与认证会话绑定的网络玩家不一致（或为 0）。
        /// 契约「OwnerNetId 必须取认证会话绑定网络玩家而非客户端自报」的落点。
        /// </summary>
        UntrustedOwner = 13,
    }

    public struct PMProjectileHitReportOutcome
    {
        public PMProjectileHitReportResult Result;
        public PMProjectileKey Key;
        public PMProjectileValidateStatus Status;
        public PMProjectileRejectReason Reason;

        /// <summary>A2 验证器报告本次是否消耗一次 Verify 配额（整合器据此更新真实配额）。</summary>
        public bool VerifyConsumed;

        /// <summary>本 key 累计已消耗的 Verify 次数（写入后值）。</summary>
        public int VerifyUsed;

        public int SettledHitCount;
        public int StashedHitCount;
        public bool StopRecorded;
        public PMProjectileClockCheck Clock;
    }

    // ---------------------------------------------------------- 激活裁决

    public enum PMProjectileActivationApplyResult : byte
    {
        Applied = 0,
        RecordedPending = 1,

        /// <summary>该 activation 已有终态判决：幂等拒绝，**不重复释放 / 不重复结算**。</summary>
        AlreadyResolved = 2,

        Invalid = 3,
        InvalidClock = 4,
        Capacity = 5,
    }

    public struct PMProjectileActivationApplyOutcome
    {
        public PMProjectileActivationApplyResult Result;
        public uint OwnerNetId;
        public uint ActivationId;
        public PMActivationResult Outcome;

        public int ReleasedKeys;
        public int SpawnedKeys;
        public int ReplayedVerifies;
        public int SettledHitCount;

        public PMActivationLedgerResult Ledger;
        public PMProjectileClockCheck Clock;
    }

    // ---------------------------------------------------------------- 运动

    public enum PMProjectileAdvanceResult : byte
    {
        Advanced = 0,

        /// <summary>本步触发了停止（Lifetime 到期或宿主 stop hook）并建立了墓碑。</summary>
        Stopped = 1,

        /// <summary>运动已是终态（已停止 / 假弹已结束）。</summary>
        NotMovable = 2,

        NotRegistered = 3,
        NotAuthority = 4,
        UnknownKey = 5,
        StaleEpoch = 6,
        Retired = 7,
        Invalid = 8,
        InvalidClock = 9,

        /// <summary>
        /// 宿主运动/停止 hook 失败（返回 false / 抛异常 / 输出非 finite）。
        /// **fail closed**：本步不推进，弹停在当前位置并建立墓碑（不把接口失败当无障碍穿墙）。
        /// </summary>
        HostMotionFaulted = 10,
    }

    public struct PMProjectileAdvanceOutcome
    {
        public PMProjectileAdvanceResult Result;
        public PMProjectileKey Key;

        /// <summary>A1 运动写回结果（诊断）。</summary>
        public PMProjectileMotionResult Motion;

        /// <summary>A1 追赶开始结果（仅 CatchUp 填充；诊断）。</summary>
        public PMProjectileCatchUpResult CatchUp;

        public int RequestedMs;
        public int AppliedMs;
        public int Steps;

        /// <summary>true = 请求的 totalMs 超过总预算，被钳到 MaxCatchUpBudgetMs。</summary>
        public bool Clamped;

        public bool Stopped;
        public PMVector3 Position;
        public PMVector3 Velocity;
        public double MoveTimeMs;
        public PMProjectileClockCheck Clock;
    }

    // ------------------------------------------------------------ 清理结果

    public struct PMProjectilePurgeOutcome
    {
        public int PendingSpawnsExpired;
        public int PendingVerifiesExpired;
        public int PendingHitGroupsExpired;
        public int LifecycleRetired;
        public int RecordsForgotten;
        public PMProjectileClockCheck Clock;
    }

    public struct PMProjectileOwnerClearOutcome
    {
        public uint OwnerNetId;
        public int RetiredProjects;
        public int DiscardedPendingSpawns;
        public int DiscardedVerifyStashes;
        public int DiscardedHitGroups;
        public int RecordsForgotten;
        public PMProjectileClockCheck Clock;
    }

    // ------------------------------------------------------------ 对外出口

    /// <summary>可复制到网络层的权威 spawn（Origin 标明来源；调用方据此决定复制对象）。</summary>
    public struct PMProjectileSpawnEvent
    {
        public PMProjectileKey Key;
        public uint OwnerNetId;
        public uint ActivationId;
        public uint AuthorityNetId;
        public PMProjectileOrigin Origin;
        public double WallTimeMs;

        /// <summary>深 clone 的冻结配置/状态（调用方拥有）。</summary>
        public PMProjectileSpec Spec;
        public PMProjectileState State;
    }

    public struct PMProjectileStopEvent
    {
        public PMProjectileKey Key;
        public uint OwnerNetId;
        public PMVector3 StopPosition;
        public double TombstoneUntilMs;
        public double WallTimeMs;
    }

    /// <summary>
    /// **R6 唯一接收点**的结算数据。只含已消毒命中（PMProjectileValidatedHit），
    /// 本文件不做任何扣血：谁拥有生命值谁消费它。
    /// </summary>
    public struct PMProjectileSettlement
    {
        public PMProjectileKey Key;
        public uint OwnerNetId;
        public uint ActivationId;
        public uint AuthorityNetId;
        public PMProjectileOrigin Origin;
        public double WallTimeMs;
        public bool StopOnHit;
        public int HitCount;
        public PMProjectileValidatedHit[] Hits;
    }

    // ------------------------------------------------------------ 宿主 hook

    /// <summary>
    /// 宿主注入的**运动 / 停止** hook（可选）。
    ///
    /// 硬约束（整合器不会代宿主兜底这些约束）：
    ///   · 必须是**纯函数**：只能根据入参算出结果，**不得**回调整合器 / 生命周期 / 队列
    ///     （否则会破坏「先提交再返回独占结果」的不变式）；
    ///   · **不得决定伤害**：hook 只影响运动与停止；结算只能由 Authoritative 验证器 + DrainSettlements 产生。
    ///
    /// **失败必须 fail closed（R5-A3 返工修订）**：一旦提供本 hook，它就承担了「场景碰撞/阻挡」的
    /// 全部责任。因此以下情况一律视为**失败**，**不推进**并按当前位置停止（HostMotionFaulted + 停止出口）：
    ///   · <see cref="TryStep"/> 返回 false；
    ///   · <see cref="TryStep"/> 抛异常，或输出的 position / velocity / yaw 非 finite；
    ///   · <see cref="TryStop"/> 抛异常，或返回 true 但 stopPosition 非 finite。
    /// 为什么不能回退直线：直线内核完全不知道墙，回退就等于「接口失败 ⇒ 穿墙」。反过来，
    /// 想表达「本步无阻挡」必须**显式返回 true 并回填直线结果**（入参已经给了直线位置/速度/朝向）。
    /// <see cref="TryStop"/> 返回 false 是正常语义（= 本步不停止），不是失败。
    ///
    /// 入参 <c>snapshot</c> 是账本的**深 clone**：hook 可以自由改写它，但写不回真实位置；
    /// 它总是表示弹的当前位置（真值），供 hook 做碰撞查询用。
    /// </summary>
    public interface IPMProjectileHostMotion
    {
        /// <summary>
        /// 计算一步运动。true = 采用 position/velocity/yaw；false = **失败**（fail closed：本步不推进，停在当前位置）。
        /// </summary>
        bool TryStep(PMProjectileKey key, PMProjectileSpec spec, PMProjectileState snapshot,
            int deltaMs, PMVector3 straightLinePosition, PMVector3 straightLineVelocity, float straightLineYaw,
            out PMVector3 position, out PMVector3 velocity, out float yaw);

        /// <summary>
        /// true = 本步结束后应停止在 stopPosition（权威侧决定，与伤害无关）；false = 不停止。
        /// 抛异常/输出非 finite 视为 hook 失败（fail closed：停止在当前位置）。
        /// </summary>
        bool TryStop(PMProjectileKey key, PMProjectileSpec spec, PMProjectileState snapshot,
            double wallNowMs, out PMVector3 stopPosition);
    }

    // ==================================================================== 整合器

    public sealed class PMProjectileCoordinator
    {
        /// <summary>整合器自有的每 key 记账：verify 真实配额 + 出生信息（清理时按 owner 扫）
        /// + 暂存命中结论的**去重预留**（避免同一目标反复入暂存而挤掉其它目标）。</summary>
        private sealed class KeyRecord
        {
            public PMProjectileKey Key;
            public uint OwnerNetId;
            public uint ActivationId;
            public uint AuthorityNetId;
            public PMProjectileOrigin Origin;
            public int PredictionMs;
            public int VerifyUsed;
            public int PendingDeferMs;
            public bool Spawned;

            /// <summary>
            /// 已进 C 队列（activation Pending）的目标去重预留。
            ///
            /// 为什么在暂存时就预留：同一目标若在 Pending 期间被反复上报（UDP 重传 / 客户端重报），
            /// 每次都会往 C 队列追加一份已消毒结论。不预留就会把单 key 追加上限（64）耗尽，
            /// 甚至把后来的**其它目标**挤掉（真实伤害丢失）。预留语义只活在「暂存期」：
            /// 一旦该组被兑现/被拒/被 TTL 或容量丢弃，预留必须一并释放，否则那个目标就再也结算不了。
            /// 注意：这与生命周期里的 HitTargets（**结算期**提交的去重）是两回事——后者只在结算时写。
            /// </summary>
            public readonly HashSet<uint> PendingHitTargets = new HashSet<uint>();
        }

        private uint _epoch;
        private readonly PMProjectileLifecycle _lifecycle;
        private readonly PMProjectilePendingSpawns _pendingSpawns;
        private readonly PMProjectilePendingVerifies _pendingVerifies;
        private readonly PMProjectilePendingHits _pendingHits;
        private readonly IPMProjectileTargetHistory _history;
        private readonly IPMProjectileHitFilter _filter;
        private readonly IPMProjectileHostMotion _hostMotion;

        private readonly Dictionary<PMProjectileKey, KeyRecord> _records =
            new Dictionary<PMProjectileKey, KeyRecord>();
        private readonly List<PMProjectileKey> _recordOrder = new List<PMProjectileKey>();

        private readonly List<PMProjectileActivationRelease> _decisions =
            new List<PMProjectileActivationRelease>();
        private readonly List<PMProjectileSpawnEvent> _spawnEvents = new List<PMProjectileSpawnEvent>();
        private readonly List<PMProjectileStopEvent> _stopEvents = new List<PMProjectileStopEvent>();
        private readonly List<PMProjectileSettlement> _settlements = new List<PMProjectileSettlement>();

        private double _wallMs;
        private int _clampedCatchUps;
        private int _rejectedAdmissions;
        private int _stashedVerifies;
        private int _stashCapacityRejections;
        private int _rejectedStaleTargets;
        private int _quotaRejections;

        // ---- R5-A3 返工新增的可观测量 ----
        private bool _faulted;
        private PMProjectileFaultReason _faultReason;
        private string _faultMessage;
        private int _hostMotionFailures;
        private int _dedupPendingHitSkips;
        private int _releasedPendingHitReservations;
        private int _observedDroppedHitGroups;
        private int _observedLifecycleReleaseDrops;
        private int _promotions;

        public PMProjectileCoordinator(uint epoch, IPMProjectileTargetHistory history,
            IPMProjectileHitFilter filter, IPMProjectileHostMotion hostMotion)
        {
            if (epoch == 0u)
            {
                throw new ArgumentOutOfRangeException("epoch",
                    "[PMProjectileCoordinator] epoch 0 无效（PMProjectileKey.IsValid 要求非 0）。");
            }

            _epoch = epoch;
            _history = history;
            _filter = filter;
            _hostMotion = hostMotion;

            _lifecycle = new PMProjectileLifecycle(epoch);
            _pendingSpawns = new PMProjectilePendingSpawns(epoch);
            _pendingVerifies = new PMProjectilePendingVerifies(epoch);
            _pendingHits = new PMProjectilePendingHits(epoch);
        }

        // ------------------------------------------------------------ 观察面

        public uint Epoch { get { return _epoch; } }

        /// <summary>A1 真实生命周期账本（只读观察；写入口都在整合器上）。</summary>
        public PMProjectileLifecycle Lifecycle { get { return _lifecycle; } }

        public PMProjectilePendingSpawns PendingSpawns { get { return _pendingSpawns; } }

        public PMProjectilePendingVerifies PendingVerifies { get { return _pendingVerifies; } }

        public PMProjectilePendingHits PendingHits { get { return _pendingHits; } }

        public double LastWallTimeMs { get { return _wallMs; } }

        public int DecisionCount { get { return _decisions.Count; } }

        public int SpawnEventCount { get { return _spawnEvents.Count; } }

        public int StopEventCount { get { return _stopEvents.Count; } }

        public int SettlementCount { get { return _settlements.Count; } }

        /// <summary>true = 已永久故障（本 epoch 报销）：一切变更型调用都会抛 InvalidOperationException。</summary>
        public bool IsFaulted { get { return _faulted; } }

        public PMProjectileFaultReason FaultReason { get { return _faultReason; } }

        /// <summary>故障详情（写入时的可读说明，空串 = 未故障）。</summary>
        public string FaultMessage { get { return _faultMessage == null ? string.Empty : _faultMessage; } }

        /// <summary>宿主运动/停止 hook 失败（fail closed）次数。</summary>
        public int HostMotionFailureCount { get { return _hostMotionFailures; } }

        /// <summary>因「同 key 同目标已在暂存中」而被跳过的暂存命中数（去重预留生效的观测量）。</summary>
        public int DedupPendingHitSkipCount { get { return _dedupPendingHitSkips; } }

        /// <summary>因暂存组被丢弃（TTL / 容量）而释放的去重预留数。</summary>
        public int ReleasedPendingHitReservationCount { get { return _releasedPendingHitReservations; } }

        /// <summary>成功执行「预留假弹 → 权威本体」原地升级的弹数。</summary>
        public int PromotedProjectileCount { get { return _promotions; } }

        public int ClampedCatchUpCount { get { return _clampedCatchUps; } }

        public int RejectedAdmissionCount { get { return _rejectedAdmissions; } }

        public int StashedVerifyCount { get { return _stashedVerifies; } }

        public int StashCapacityRejectionCount { get { return _stashCapacityRejections; } }

        /// <summary>最终确认时因「目标 stream 已变 / 目标已死亡 / 历史不可用」被拒的命中数。</summary>
        public int RejectedStaleTargetCount { get { return _rejectedStaleTargets; } }

        public int QuotaRejectionCount { get { return _quotaRejections; } }

        /// <summary>权威 spec/state 的深 clone 观察（改返回值不影响账本）。</summary>
        public bool TryObserveFrozen(PMProjectileKey key, out PMProjectileSpec spec, out PMProjectileState state)
        {
            return _lifecycle.TryGetFrozen(key, out spec, out state);
        }

        public bool TryObserveRegistration(PMProjectileKey key, out PMProjectileRegistration snapshot)
        {
            return _lifecycle.TryGetRegistration(key, out snapshot);
        }

        /// <summary>该 key 已消耗的 Verify 次数（真实配额，由 A2 的 VerifyConsumed 驱动）。</summary>
        public int VerifyUsedCount(PMProjectileKey key)
        {
            KeyRecord rec;
            return _records.TryGetValue(key, out rec) ? rec.VerifyUsed : 0;
        }

        public bool IsSpawnPending(PMProjectileKey key)
        {
            return _pendingSpawns.Contains(key);
        }

        public bool IsSpawned(PMProjectileKey key)
        {
            KeyRecord rec;
            return _records.TryGetValue(key, out rec) && rec.Spawned;
        }

        // ================================================================ 登记

        /// <summary>
        /// 客户端预测 Spawn admission。参见文件头「客户端预测 Spawn admission」。
        /// </summary>
        public PMProjectileAdmissionOutcome RequestSpawn(
            PMProjectileSpawnRequest request,
            uint authenticatedOwnerNetId,
            uint authorityNetId,
            uint sourceBehaviorInstanceId,
            PMVector3 ownerPosition,
            PMProjectileSpec trustedSpec,
            uint[] trustedAllowedTargets,
            double wallNowMs)
        {
            ThrowIfFaulted();
            EnsureOutputHeadroom();

            PMProjectileAdmissionOutcome o = new PMProjectileAdmissionOutcome();
            o.Clock = CheckClock(wallNowMs);
            if (o.Clock != PMProjectileClockCheck.Ok) { return FailAdmission(o, PMProjectileAdmissionResult.InvalidClock); }

            if (request == null || request.State == null)
            {
                return FailAdmission(o, PMProjectileAdmissionResult.InvalidRequest);
            }

            PMProjectileState rawState = request.State;
            o.Key = rawState.Key;

            if (authenticatedOwnerNetId == 0u || rawState.Key.OwnerNetId != authenticatedOwnerNetId)
            {
                // 客户端不能自称 owner（跨 owner 注入在这里被拒，而不是「打上可信 owner 继续跑」）。
                return FailAdmission(o, PMProjectileAdmissionResult.UntrustedOwner);
            }

            if (authorityNetId == 0u) { return FailAdmission(o, PMProjectileAdmissionResult.InvalidAuthority); }
            if (sourceBehaviorInstanceId == 0u || request.PredictionMs < 0)
            {
                return FailAdmission(o, PMProjectileAdmissionResult.InvalidRequest);
            }

            if (!rawState.Key.IsValid) { return FailAdmission(o, PMProjectileAdmissionResult.InvalidRequest); }
            if (rawState.Key.Epoch != _epoch) { return FailAdmission(o, PMProjectileAdmissionResult.StaleEpoch); }
            if (rawState.Key.Origin != PMProjectileOrigin.ClientPredicted)
            {
                // 客户端不得冒充 ServerDirect。
                return FailAdmission(o, PMProjectileAdmissionResult.InvalidRequest);
            }

            o.ActivationId = rawState.ActivationId;
            if (rawState.ActivationId == 0u)
            {
                return FailAdmission(o, PMProjectileAdmissionResult.InvalidActivationId);
            }

            if (!IsTrustedSpec(trustedSpec)) { return FailAdmission(o, PMProjectileAdmissionResult.InvalidSpec); }

            if (!PMProjectileFinite.V(rawState.SpawnPosition) || !PMProjectileFinite.V(rawState.Position)
                || !PMProjectileFinite.F(rawState.Yaw))
            {
                return FailAdmission(o, PMProjectileAdmissionResult.InvalidPosition);
            }

            // 方向意图来自客户端 velocity（未归一化），强度由 trustedSpec 决定。
            PMVector3 direction = rawState.Velocity;
            if (!PMProjectileFinite.V(direction) || direction.LengthSquared <= PMVector3.Epsilon)
            {
                return FailAdmission(o, PMProjectileAdmissionResult.InvalidDirection);
            }

            // 10m 枪口：枪口位置必须在 owner 周围 10m 内（位置是唯一被采信的客户端权威面之一）。
            PMVector3 muzzle = rawState.SpawnPosition;
            if (PMVector3.Distance(muzzle, ownerPosition) > PMProjectileLimits.MuzzleToleranceM)
            {
                return FailAdmission(o, PMProjectileAdmissionResult.MuzzleTooFar);
            }

            PMVector3 velocity = PMVector3.Normalized(direction) * trustedSpec.SpeedMps;
            PMProjectileState sanitizedState = new PMProjectileState();
            SanitizeState(rawState, sanitizedState, rawState.Key, authorityNetId, rawState.ActivationId, muzzle, velocity);

            PMProjectileSpec trusted = trustedSpec.Clone();
            PMProjectileSpawnRequest sanitizedRequest = new PMProjectileSpawnRequest();
            sanitizedRequest.State = sanitizedState;
            sanitizedRequest.Spec = trusted;
            sanitizedRequest.PredictionMs = request.PredictionMs;

            // 1) 先登记（= 预留 ID）。失败时**没有**任何东西需要回滚。
            PMProjectileRegisterOutcome reg = _lifecycle.TryRegisterPredicted(
                authenticatedOwnerNetId, rawState.Key.ProjectileId, rawState.ActivationId,
                sanitizedState, trusted, wallNowMs);
            o.Register = reg.Result;
            o.Activation = reg.Activation;

            if (reg.Result == PMProjectileRegisterResult.RevokedRejected)
            {
                // activation 已被权威拒绝：终态不倒退 ⇒ 不入队、不生成，但把拒绝释放项交出去。
                PushReleases(reg.Releases);
                return FailAdmission(o, PMProjectileAdmissionResult.RevokedActivation);
            }

            if (reg.Result != PMProjectileRegisterResult.Registered)
            {
                return FailAdmission(o, MapRegisterFailure(reg.Result));
            }

            EnsureRecord(reg.Key, authenticatedOwnerNetId, rawState.ActivationId, authorityNetId,
                PMProjectileOrigin.ClientPredicted, request.PredictionMs);
            _lifecycle.TrySetAllowedTargets(reg.Key, trustedAllowedTargets);

            if (reg.Activation == PMActivationResult.Pending)
            {
                // 2) 挂起 payload（A 队列）。容量失败必须回滚已登记的 ID 预留。
                PMProjectilePendingSpawnOutcome enq = _pendingSpawns.TryEnqueue(
                    reg.Key, sourceBehaviorInstanceId, rawState.ActivationId, authorityNetId,
                    request.PredictionMs, sanitizedRequest, wallNowMs);
                o.PendingSpawnResult = enq.Result;
                if (enq.Result != PMProjectilePendingSpawnResult.Enqueued)
                {
                    _lifecycle.Retire(reg.Key, wallNowMs);
                    ForgetRecord(reg.Key);
                    if (enq.Result == PMProjectilePendingSpawnResult.InvalidClock)
                    {
                        return FailAdmission(o, PMProjectileAdmissionResult.InvalidClock);
                    }

                    if (enq.Result == PMProjectilePendingSpawnResult.Invalid)
                    {
                        return FailAdmission(o, PMProjectileAdmissionResult.InvalidRequest);
                    }

                    return FailAdmission(o, enq.Result == PMProjectilePendingSpawnResult.StaleEpoch
                        ? PMProjectileAdmissionResult.StaleEpoch
                        : PMProjectileAdmissionResult.Capacity);
                }

                o.SpawnPending = true;
                o.Result = PMProjectileAdmissionResult.Admitted;
                AcceptWallClock(wallNowMs);
                return o;
            }

            // activation 已 Confirmed：立即生成，并**原地升级为权威本体**（否则这颗弹永远不能追赶）。
            PromoteReservedOrFail(reg.Key, authorityNetId, wallNowMs);
            MarkSpawned(reg.Key);
            EmitSpawnReady(reg.Key, wallNowMs);
            o.Spawned = true;

            KeyRecord fresh;
            int defer = _records.TryGetValue(reg.Key, out fresh) ? fresh.PendingDeferMs : 0;
            ReplayStashed(reg.Key, defer, wallNowMs);

            o.Result = PMProjectileAdmissionResult.Admitted;
            AcceptWallClock(wallNowMs);
            DrainLifecycleReleasesIntoDecisions();
            return o;
        }

        /// <summary>
        /// 可信 ServerDirect 独立入口。唯一允许 ActivationId=0 的入口；
        /// 位置/方向/朝向由权威侧给出，速度仍由 trustedSpec 决定（与预测路径同一口径）。
        /// </summary>
        public PMProjectileAdmissionOutcome ServerDirectSpawn(
            PMProjectileState state,
            uint authorityNetId,
            PMProjectileSpec trustedSpec,
            uint[] trustedAllowedTargets,
            int predictionMs,
            double wallNowMs)
        {
            ThrowIfFaulted();
            EnsureOutputHeadroom();

            PMProjectileAdmissionOutcome o = new PMProjectileAdmissionOutcome();
            o.Clock = CheckClock(wallNowMs);
            if (o.Clock != PMProjectileClockCheck.Ok) { return FailAdmission(o, PMProjectileAdmissionResult.InvalidClock); }

            if (state == null) { return FailAdmission(o, PMProjectileAdmissionResult.InvalidRequest); }
            o.Key = state.Key;

            if (!state.Key.IsValid) { return FailAdmission(o, PMProjectileAdmissionResult.InvalidRequest); }
            if (state.Key.Epoch != _epoch) { return FailAdmission(o, PMProjectileAdmissionResult.StaleEpoch); }
            if (state.Key.Origin != PMProjectileOrigin.ServerDirect)
            {
                return FailAdmission(o, PMProjectileAdmissionResult.InvalidRequest);
            }

            if (authorityNetId == 0u) { return FailAdmission(o, PMProjectileAdmissionResult.InvalidAuthority); }
            if (!IsTrustedSpec(trustedSpec)) { return FailAdmission(o, PMProjectileAdmissionResult.InvalidSpec); }
            if (predictionMs < 0) { return FailAdmission(o, PMProjectileAdmissionResult.InvalidRequest); }

            if (!PMProjectileFinite.V(state.SpawnPosition) || !PMProjectileFinite.V(state.Position)
                || !PMProjectileFinite.F(state.Yaw))
            {
                return FailAdmission(o, PMProjectileAdmissionResult.InvalidPosition);
            }

            PMVector3 direction = state.Velocity;
            if (!PMProjectileFinite.V(direction) || direction.LengthSquared <= PMVector3.Epsilon)
            {
                return FailAdmission(o, PMProjectileAdmissionResult.InvalidDirection);
            }

            PMVector3 velocity = PMVector3.Normalized(direction) * trustedSpec.SpeedMps;
            PMProjectileState sanitizedState = new PMProjectileState();
            SanitizeState(state, sanitizedState, state.Key, authorityNetId, state.ActivationId,
                state.SpawnPosition, velocity);

            PMProjectileSpec trusted = trustedSpec.Clone();
            o.ActivationId = state.ActivationId;

            PMProjectileRegisterOutcome reg = _lifecycle.TryRegisterAuthority(
                PMProjectileTrust.AuthorityServerDirect, state.Key.OwnerNetId, state.Key.ProjectileId,
                authorityNetId, state.ActivationId, sanitizedState, trusted, wallNowMs);
            o.Register = reg.Result;
            o.Activation = reg.Activation;

            if (reg.Result == PMProjectileRegisterResult.RevokedRejected)
            {
                PushReleases(reg.Releases);
                return FailAdmission(o, PMProjectileAdmissionResult.RevokedActivation);
            }

            if (reg.Result != PMProjectileRegisterResult.Registered)
            {
                return FailAdmission(o, MapRegisterFailure(reg.Result));
            }

            EnsureRecord(reg.Key, state.Key.OwnerNetId, state.ActivationId, authorityNetId,
                PMProjectileOrigin.ServerDirect, predictionMs);
            _lifecycle.TrySetAllowedTargets(reg.Key, trustedAllowedTargets);
            MarkSpawned(reg.Key);
            EmitSpawnReady(reg.Key, wallNowMs);
            o.Spawned = true;

            ReplayStashed(reg.Key, 0, wallNowMs);

            o.Result = PMProjectileAdmissionResult.Admitted;
            AcceptWallClock(wallNowMs);
            DrainLifecycleReleasesIntoDecisions();
            return o;
        }

        // ============================================================== 激活裁决

        /// <summary>
        /// 可信激活裁决写入（Pending / Confirmed / Rejected）并联动三队列：
        ///   · Confirmed → 发 SpawnReady + FIFO 回放 B 队列 + 结算 C 队列候选；
        ///   · Rejected  → 实际撤销预测实例（释放项 RemovedInstance）、丢弃 B/C 暂存、**不结算**；
        ///   · Pending   → 只记账。
        /// 终态不倒退：重复（含反向）写入返回 AlreadyResolved 且不重复释放/结算。
        /// </summary>
        public PMProjectileActivationApplyOutcome ResolveActivation(
            uint ownerNetId, uint activationId, PMActivationResult outcome, double wallNowMs)
        {
            ThrowIfFaulted();
            EnsureOutputHeadroom();

            PMProjectileActivationApplyOutcome o = new PMProjectileActivationApplyOutcome();
            o.OwnerNetId = ownerNetId;
            o.ActivationId = activationId;
            o.Outcome = outcome;

            o.Clock = CheckClock(wallNowMs);
            if (o.Clock != PMProjectileClockCheck.Ok)
            {
                o.Result = PMProjectileActivationApplyResult.InvalidClock;
                return o;
            }

            if (ownerNetId == 0u || activationId == 0u)
            {
                o.Result = PMProjectileActivationApplyResult.Invalid;
                return o;
            }

            PMProjectileActivationRelease[] releases;
            PMActivationLedgerResult ledger =
                _lifecycle.TrySetActivationResult(ownerNetId, activationId, outcome, wallNowMs, out releases);
            o.Ledger = ledger;

            if (ledger == PMActivationLedgerResult.AlreadyTerminal)
            {
                // 重复确认 / 重复拒绝 / 反向写入：终态不倒退，且绝不重复结算。
                o.Result = PMProjectileActivationApplyResult.AlreadyResolved;
                o.ReleasedKeys = 0;
                AcceptWallClock(wallNowMs);
                DrainLifecycleReleasesIntoDecisions();
                return o;
            }

            if (ledger == PMActivationLedgerResult.InvalidActivationId
                || ledger == PMActivationLedgerResult.InvalidOutcome)
            {
                o.Result = PMProjectileActivationApplyResult.Invalid;
                return o;
            }

            if (ledger == PMActivationLedgerResult.InvalidClock)
            {
                o.Result = PMProjectileActivationApplyResult.InvalidClock;
                return o;
            }

            if (ledger == PMActivationLedgerResult.LedgerFull)
            {
                o.Result = PMProjectileActivationApplyResult.Capacity;
                return o;
            }

            AcceptWallClock(wallNowMs);

            if (ledger == PMActivationLedgerResult.RecordedPending)
            {
                o.Result = PMProjectileActivationApplyResult.RecordedPending;
                return o;
            }

            // 决策出口（网络层据此告知客户端激活结论）。
            for (int i = 0; i < releases.Length; i++)
            {
                PushDecision(releases[i]);
            }

            o.ReleasedKeys = releases.Length;

            PMProjectilePendingSpawnRelease[] spawnReleases;
            int spawnCount = _pendingSpawns.ResolveByActivation(ownerNetId, activationId, outcome,
                wallNowMs, out spawnReleases);

            if (outcome == PMActivationResult.Confirmed)
            {
                for (int i = 0; i < spawnCount; i++)
                {
                    PMProjectileKey key = spawnReleases[i].Key;

                    // 1) **先原地升级为权威本体**（ID/Origin/Key 不变、权威 NetId 真实非 0），
                    //    再发 SpawnReady：生成出口里的冻结状态必须已经带着真实权威 ID（下行 Snapshot 要求
                    //    「权威ID 非 0」），而且升级后才可能 CatchUpMotion。
                    uint trustedAuthority = spawnReleases[i].Spawn != null
                        ? spawnReleases[i].Spawn.AuthorityNetId
                        : 0u;
                    KeyRecord existingRecord;
                    if (trustedAuthority == 0u && _records.TryGetValue(key, out existingRecord))
                    {
                        trustedAuthority = existingRecord.AuthorityNetId;
                    }

                    PromoteReservedOrFail(key, trustedAuthority, wallNowMs);

                    KeyRecord rec = EnsureRecord(key, key.OwnerNetId, activationId, trustedAuthority, key.Origin, 0);
                    rec.PendingDeferMs = spawnReleases[i].ExtraDeferMs;
                    rec.ActivationId = activationId;
                    if (rec.AuthorityNetId == 0u && trustedAuthority != 0u) { rec.AuthorityNetId = trustedAuthority; }
                    MarkSpawned(key);
                    if (EmitSpawnReady(key, wallNowMs)) { o.SpawnedKeys++; }
                }

                for (int i = 0; i < releases.Length; i++)
                {
                    PMProjectileKey key = releases[i].Key;
                    KeyRecord rec;
                    int defer = _records.TryGetValue(key, out rec) ? rec.PendingDeferMs : 0;
                    int replayedHits;
                    o.ReplayedVerifies += ReplayStashed(key, defer, wallNowMs, out replayedHits);
                    o.SettledHitCount += replayedHits;


                    // C 队列：确认时**直接结算候选，不重跑 Validate / 不重跑历史几何**。
                    PMProjectileHitResolveOutcome res =
                        _pendingHits.TryResolve(key, PMActivationResult.Confirmed, wallNowMs);
                    if (res.Settled)
                    {
                        bool stop;
                        o.SettledHitCount += SettleHitSets(key, res.Sets, wallNowMs, out stop);
                    }

                    // 组已被兑现/移除 ⇒ 暂存期的去重预留一并释放（否则该目标永远结不了）。
                    ClearPendingHitTargets(key);
                }

                o.Result = PMProjectileActivationApplyResult.Applied;
            }
            else
            {
                // Rejected：拒绝也是终态 ⇒ 丢弃暂存，绝不结算。
                for (int i = 0; i < spawnCount; i++)
                {
                    DiscardPendingVerifies(spawnReleases[i].Key);
                }

                for (int i = 0; i < releases.Length; i++)
                {
                    PMProjectileKey key = releases[i].Key;
                    DiscardPendingVerifies(key);
                    _pendingHits.TryResolve(key, PMActivationResult.Rejected, wallNowMs);
                    ClearPendingHitTargets(key);
                    ForgetRecord(key);
                }

                o.Result = PMProjectileActivationApplyResult.Applied;
            }

            DrainLifecycleReleasesIntoDecisions();
            return o;
        }

        // ============================================================== 命中上报

        /// <summary>
        /// 接收一次命中上报（原始上行 PMProjectileHitBatch）并按 key 状态分流：
        ///   · 身份：<paramref name="authenticatedOwnerNetId"/>（认证会话绑定的网络玩家）必须与
        ///     batch.Key.OwnerNetId 一致 —— 契约「OwnerNetId 必须取认证会话绑定网络玩家而非客户端自报」，
        ///     否则跨 owner 伪造就能替别人提交命中；
        ///   · key 还没登记 → B 队列（原始上行，生成后按 FIFO 回放）；**只接受 Origin=ClientPredicted**
        ///     （未登记的 ServerDirect key 不可能已经存在，“先占位、等权威弹出现后回放”就是伪造通道）；
        ///   · 已登记 → 墓碑准入 + L0–L4 验证 → Confirmed 结算 / Pending 进 C / Rejected 丢弃。
        /// 命中目标去重提交（TryAddHitTarget）发生在结算**入队之前**。
        /// </summary>
        public PMProjectileHitReportOutcome ReportHits(PMProjectileHitBatch batch,
            uint authenticatedOwnerNetId, double wallNowMs)
        {
            ThrowIfFaulted();
            EnsureOutputHeadroom();

            PMProjectileHitReportOutcome o = new PMProjectileHitReportOutcome();
            o.Clock = CheckClock(wallNowMs);
            o.Status = PMProjectileValidateStatus.Rejected;

            if (o.Clock != PMProjectileClockCheck.Ok)
            {
                o.Result = PMProjectileHitReportResult.InvalidClock;
                return o;
            }

            if (batch == null || !batch.Key.IsValid)
            {
                o.Result = PMProjectileHitReportResult.Invalid;
                return o;
            }

            o.Key = batch.Key;

            // 身份绑定：不接受「自称的 owner」。跨 owner 的上报一律不进入任何路径（含 B 队列）。
            if (authenticatedOwnerNetId == 0u || batch.Key.OwnerNetId != authenticatedOwnerNetId)
            {
                o.Result = PMProjectileHitReportResult.UntrustedOwner;
                return o;
            }

            if (batch.Key.Epoch != _epoch)
            {
                o.Result = PMProjectileHitReportResult.StaleEpoch;
                return o;
            }

            if (_lifecycle.IsRetired(batch.Key))
            {
                o.Result = PMProjectileHitReportResult.Retired;
                return o;
            }

            if (!_lifecycle.IsRegistered(batch.Key))
            {
                // Verify-before-Spawn：连登记都还没有（上报先到 / 生成请求丢失）。
                // 上行只能声称 ClientPredicted：ServerDirect 弹只能由权威入口产生，
                // 未登记就声称 ServerDirect 的 key 是「占位未来权威弹」的伪造入口。
                if (batch.Key.Origin != PMProjectileOrigin.ClientPredicted)
                {
                    o.Result = PMProjectileHitReportResult.Invalid;
                    return o;
                }

                PMProjectileVerifyStashOutcome st =
                    _pendingVerifies.TryStash(ToVerifyRequest(batch), wallNowMs);
                if (st.Result == PMProjectileVerifyStashResult.Stashed)
                {
                    _stashedVerifies++;
                    EnsureRecord(batch.Key, batch.Key.OwnerNetId, 0u, 0u, batch.Key.Origin, 0);
                    AcceptWallClock(wallNowMs);
                    o.Result = PMProjectileHitReportResult.StashedForSpawn;
                    return o;
                }

                if (st.Result == PMProjectileVerifyStashResult.StaleEpoch)
                {
                    o.Result = PMProjectileHitReportResult.StaleEpoch;
                    return o;
                }

                if (st.Result == PMProjectileVerifyStashResult.InvalidClock)
                {
                    o.Result = PMProjectileHitReportResult.InvalidClock;
                    return o;
                }

                if (st.Result == PMProjectileVerifyStashResult.Invalid)
                {
                    o.Result = PMProjectileHitReportResult.Invalid;
                    return o;
                }

                _stashCapacityRejections++;
                o.Result = PMProjectileHitReportResult.Capacity;
                return o;
            }

            return ProcessVerify(batch.Key, batch, 0, wallNowMs);
        }

        // ================================================================ 运动

        /// <summary>
        /// 已登记弹的单步推进：整毫秒、0 &lt;= deltaMs &lt;= 50；写回走 A1 TryAdvanceMotion（保护字段）。
        /// Lifetime 到期或宿主 stop hook 触发时记录停止 + 墓碑并发出 Stopped 出口。
        /// 本组只有直线核心运动，**不声称不穿墙**（Unity 碰撞查询留 C）。
        /// </summary>
        public PMProjectileAdvanceOutcome AdvanceMotion(PMProjectileKey key, int deltaMs, double wallNowMs)
        {
            ThrowIfFaulted();
            EnsureOutputHeadroom();

            PMProjectileAdvanceOutcome o = NewAdvanceOutcome(key, deltaMs);
            o.Clock = CheckClock(wallNowMs);
            if (o.Clock != PMProjectileClockCheck.Ok)
            {
                o.Result = PMProjectileAdvanceResult.InvalidClock;
                return o;
            }

            if (deltaMs < 0 || deltaMs > PMProjectileCoordinatorLimits.MaxCatchUpStepMs)
            {
                o.Result = PMProjectileAdvanceResult.Invalid;
                return o;
            }

            if (deltaMs == 0)
            {
                // 零步：只做注册检查，不改状态（也不消耗生命周期墙钟）。
                return CheckOnly(key, o);
            }

            AcceptWallClock(wallNowMs);
            return StepMotion(key, deltaMs, wallNowMs, o);
        }

        /// <summary>
        /// 注册先于追赶：先 TryBeginCatchUp（只能对已登记权威 key 开始一次），
        /// 再以 &lt;= 50ms 步推进，总预算 MaxCatchUpBudgetMs —— 恶意巨大的 totalMs 被钳并计数，
        /// 不会形成无界循环。
        /// </summary>
        public PMProjectileAdvanceOutcome CatchUpMotion(PMProjectileKey key, int totalMs, double wallNowMs)
        {
            ThrowIfFaulted();
            EnsureOutputHeadroom();

            PMProjectileAdvanceOutcome o = NewAdvanceOutcome(key, totalMs);
            o.Clock = CheckClock(wallNowMs);
            if (o.Clock != PMProjectileClockCheck.Ok)
            {
                o.Result = PMProjectileAdvanceResult.InvalidClock;
                return o;
            }

            if (totalMs < 0)
            {
                o.Result = PMProjectileAdvanceResult.Invalid;
                return o;
            }

            int budget = totalMs;
            if (budget > PMProjectileCoordinatorLimits.MaxCatchUpBudgetMs)
            {
                budget = PMProjectileCoordinatorLimits.MaxCatchUpBudgetMs;
                o.Clamped = true;
                _clampedCatchUps++;
            }

            PMProjectileCatchUpOutcome cu = _lifecycle.TryBeginCatchUp(key, wallNowMs);
            o.CatchUp = cu.Result;
            if (cu.Result == PMProjectileCatchUpResult.NotRegistered)
            {
                o.Result = PMProjectileAdvanceResult.NotRegistered;
                return o;
            }

            if (cu.Result == PMProjectileCatchUpResult.NotAuthority)
            {
                o.Result = PMProjectileAdvanceResult.NotAuthority;
                return o;
            }

            if (cu.Result == PMProjectileCatchUpResult.Retired)
            {
                o.Result = PMProjectileAdvanceResult.Retired;
                return o;
            }

            if (cu.Result == PMProjectileCatchUpResult.StaleEpoch)
            {
                o.Result = PMProjectileAdvanceResult.StaleEpoch;
                return o;
            }

            if (cu.Result == PMProjectileCatchUpResult.InvalidClock)
            {
                o.Result = PMProjectileAdvanceResult.InvalidClock;
                return o;
            }

            if (cu.Result == PMProjectileCatchUpResult.Invalid)
            {
                o.Result = PMProjectileAdvanceResult.Invalid;
                return o;
            }

            AcceptWallClock(wallNowMs);

            int remaining = budget;
            int steps = 0;
            PMProjectileAdvanceResult last = PMProjectileAdvanceResult.Advanced;
            while (remaining > 0 && steps < PMProjectileCoordinatorLimits.MaxCatchUpSteps)
            {
                int step = remaining > PMProjectileCoordinatorLimits.MaxCatchUpStepMs
                    ? PMProjectileCoordinatorLimits.MaxCatchUpStepMs
                    : remaining;

                PMProjectileAdvanceOutcome stepOutcome = NewAdvanceOutcome(key, step);
                PMProjectileAdvanceOutcome s = StepMotion(key, step, wallNowMs, stepOutcome);
                steps++;
                o.AppliedMs += step;
                remaining -= step;
                last = s.Result;
                o.Motion = s.Motion;
                o.Position = s.Position;
                o.Velocity = s.Velocity;
                o.MoveTimeMs = s.MoveTimeMs;
                if (s.Stopped) { o.Stopped = true; }

                if (s.Result != PMProjectileAdvanceResult.Advanced
                    && s.Result != PMProjectileAdvanceResult.Stopped)
                {
                    if (s.Result == PMProjectileAdvanceResult.HostMotionFaulted)
                    {
                        o.Result = PMProjectileAdvanceResult.HostMotionFaulted;
                    }

                    break;
                }

                if (o.Stopped) { break; }
            }

            o.Steps = steps;
            if (o.Result != PMProjectileAdvanceResult.HostMotionFaulted)
            {
                o.Result = o.Stopped ? PMProjectileAdvanceResult.Stopped : last;
            }

            return o;
        }

        /// <summary>
        /// **权威注册先于追赶**里「注册完成后那一次有界追赶」的唯一入口（DS 生成出口专用）。
        ///
        /// 预算 = `predictionMs / PredictionCatchUpDivisor` + **挂起时长**；两项都取自本整合器自己的记账
        /// （`KeyRecord.PredictionMs` 与生命周期登记的 `RegisteredWallTimeMs`），
        /// 调用方**不需要也不应该自报**预算 —— 自报的预算就是可被伪造的输入。
        ///
        /// 为什么必须在**发布初始 snapshot 之前**调用：`PMProjectileSpawnEvent.State` 是发那一刻的旧值，
        /// 发布方拿它当线上初值就会把一颗已经该飞到半空的弹钉在枪口上；客户端接管时就会看到回退。
        /// 调用方应该在本方法返回后**重新**读一次冻结状态再编码。
        ///
        /// 有界性完全由 <see cref="CatchUpMotion"/> 保证（预算钳制 + 每步 ≤50ms + 步数上限 + ClampedCatchUps 计数）。
        /// 对未登记/非权威的 key，本方法转发 <see cref="CatchUpMotion"/> 的 NotRegistered / NotAuthority 结果，
        /// **不**偷偷改成直线运动（那就是「接口失败回退穿墙」）。
        /// </summary>
        public PMProjectileAdvanceOutcome CatchUpRegisteredMotion(PMProjectileKey key, double wallNowMs)
        {
            int predictionMs = 0;
            KeyRecord rec;
            if (_records.TryGetValue(key, out rec) && rec.PredictionMs > 0) { predictionMs = rec.PredictionMs; }

            double pendingElapsedMs = 0.0;
            PMProjectileRegistration reg;
            if (_lifecycle.TryGetRegistration(key, out reg) && reg != null)
            {
                pendingElapsedMs = wallNowMs - reg.RegisteredWallTimeMs;
            }

            if (pendingElapsedMs < 0.0) { pendingElapsedMs = 0.0; }
            if (pendingElapsedMs > PMProjectileCoordinatorLimits.MaxPendingCatchUpMs)
            {
                pendingElapsedMs = PMProjectileCoordinatorLimits.MaxPendingCatchUpMs;
            }

            int totalMs = predictionMs / PMProjectileCoordinatorLimits.PredictionCatchUpDivisor
                          + (int)pendingElapsedMs;

            return CatchUpMotion(key, totalMs, wallNowMs);
        }

        // ============================================================== 超时清理

        /// <summary>
        /// 墙钟驱动的联动清理：三队列 TTL + 生命周期墓碑 + 待取释放项 → 决策出口。
        /// TTL 到期**不结算**；挂起 Spawn 超时会把对应 ID 预留一并退回（不让它变成永久假弹）。
        /// </summary>
        public PMProjectilePurgeOutcome PurgeExpired(double wallNowMs)
        {
            ThrowIfFaulted();
            EnsureOutputHeadroom();

            PMProjectilePurgeOutcome o = new PMProjectilePurgeOutcome();
            o.Clock = CheckClock(wallNowMs);
            if (o.Clock != PMProjectileClockCheck.Ok) { return o; }

            AcceptWallClock(wallNowMs);

            PMProjectilePendingSpawnRelease[] droppedSpawns;
            int n = _pendingSpawns.PurgeExpired(wallNowMs, out droppedSpawns);
            o.PendingSpawnsExpired = n;
            for (int i = 0; i < n; i++)
            {
                PMProjectileKey key = droppedSpawns[i].Key;
                DiscardPendingVerifies(key);
                _pendingHits.TryResolve(key, PMActivationResult.Rejected, wallNowMs);
                ClearPendingHitTargets(key);
                RetireKey(key, wallNowMs);
            }

            o.PendingVerifiesExpired = _pendingVerifies.PurgeExpired(wallNowMs);

            PMProjectilePendingHitExpiry[] droppedHits;
            o.PendingHitGroupsExpired = _pendingHits.PurgeExpired(wallNowMs, out droppedHits);
            for (int i = 0; i < droppedHits.Length; i++)
            {
                // TTL 丢弃一组未结算结论：只释放它的**暂存期去重预留**（让后续合法上报能重新入队并结算），
                // 不整条忘掉记账（否则该 key 的 verify 配额会被重置）。
                ClearPendingHitTargets(droppedHits[i].Key);
            }

            _releasedPendingHitReservations += ReleaseOrphanPendingHitReservations();

            o.LifecycleRetired = _lifecycle.PurgeExpired(wallNowMs);
            DrainLifecycleReleasesIntoDecisions();

            o.RecordsForgotten = PruneRecords();
            return o;
        }

        /// <summary>
        /// 断连清理：丢弃该 owner 的全部暂存（A/B/C）与登记，**保留 ID 高水位** ⇒ 旧 ID 不会复活。
        /// </summary>
        public PMProjectileOwnerClearOutcome ClearOwner(uint ownerNetId, double wallNowMs)
        {
            ThrowIfFaulted();
            EnsureOutputHeadroom();

            PMProjectileOwnerClearOutcome o = new PMProjectileOwnerClearOutcome();
            o.OwnerNetId = ownerNetId;
            o.Clock = CheckClock(wallNowMs);
            if (o.Clock != PMProjectileClockCheck.Ok) { return o; }
            if (ownerNetId == 0u) { return o; }

            AcceptWallClock(wallNowMs);

            PMProjectilePendingSpawnRelease[] droppedSpawns;
            o.DiscardedPendingSpawns = _pendingSpawns.DiscardOwner(ownerNetId, wallNowMs, out droppedSpawns);

            // 先按自有记录摘掉该 owner 的 B/C 暂存（A1 的三个队列没有「按 owner 枚举」接口）。
            PMProjectileKey[] owned = _recordOrder.ToArray();
            for (int i = 0; i < owned.Length; i++)
            {
                PMProjectileKey key = owned[i];
                if (key.OwnerNetId != ownerNetId) { continue; }
                int discarded = _pendingVerifies.Discard(key);
                if (discarded > 0) { o.DiscardedVerifyStashes++; }
                PMProjectileHitResolveOutcome res =
                    _pendingHits.TryResolve(key, PMActivationResult.Rejected, wallNowMs);
                if (res.Result == PMProjectileHitResolveResult.RejectedNoSettlement)
                {
                    o.DiscardedHitGroups++;
                }

                ClearPendingHitTargets(key);
            }

            o.RetiredProjects = _lifecycle.ClearOwner(ownerNetId, wallNowMs);
            DrainLifecycleReleasesIntoDecisions();

            for (int i = _recordOrder.Count - 1; i >= 0; i--)
            {
                if (_recordOrder[i].OwnerNetId == ownerNetId)
                {
                    _records.Remove(_recordOrder[i]);
                    _recordOrder.RemoveAt(i);
                    o.RecordsForgotten++;
                }
            }

            return o;
        }

        /// <summary>
        /// 会话级重置（同 epoch）：清空三队列暂存与全部登记，**保留每个 (owner,origin) 的 ID 高水位**
        /// ⇒ 已用过的 ID 不会复活（重复使用会被 NonMonotonicId 拒）。
        /// 注意：这是拆除路径，终态出口队列在重置前应先被取走（本方法不清空出口队列）。
        /// </summary>
        public int ResetState(double wallNowMs)
        {
            ThrowIfFaulted();
            EnsureOutputHeadroom();

            if (CheckClock(wallNowMs) != PMProjectileClockCheck.Ok) { return 0; }
            AcceptWallClock(wallNowMs);

            int retired = 0;
            PMProjectileKey[] keys = _recordOrder.ToArray();
            for (int i = 0; i < keys.Length; i++)
            {
                if (RetireKey(keys[i], wallNowMs, PMProjectileReleaseReason.OwnerCleared)) { retired++; }
            }

            _pendingSpawns.Clear();
            _pendingVerifies.Clear();
            _pendingHits.Clear();
            _records.Clear();
            _recordOrder.Clear();
            return retired;
        }

        /// <summary>
        /// 升 epoch：整体清理，旧 epoch 的 key 此后一律 StaleEpoch（旧 ID 不复活）。
        /// **这也是 Faulted 的唯一恢复路径**：故障意味着本 epoch 的数据不完整，
        /// 宿主必须断连 → 升 epoch 重建（本方法清除故障标记）。
        /// </summary>
        public bool AdvanceEpoch(uint newEpoch)
        {
            if (newEpoch <= _epoch) { return false; }
            _lifecycle.AdvanceEpoch(newEpoch);
            _pendingSpawns.AdvanceEpoch(newEpoch);
            _pendingVerifies.AdvanceEpoch(newEpoch);
            _pendingHits.AdvanceEpoch(newEpoch);
            _records.Clear();
            _recordOrder.Clear();
            _wallMs = 0.0;
            _epoch = newEpoch;
            _faulted = false;
            _faultReason = PMProjectileFaultReason.None;
            _faultMessage = null;
            _observedDroppedHitGroups = 0;
            return true;
        }

        // ============================================================== 对外出口

        /// <summary>取出至多 max 条激活决策（FIFO，独占数组）。</summary>
        public int DrainDecisions(int max, out PMProjectileActivationRelease[] items)
        {
            return DrainList(_decisions, max, out items);
        }

        /// <summary>取出至多 max 条可复制的权威 spawn（FIFO，独占数组）。</summary>
        public int DrainSpawnReady(int max, out PMProjectileSpawnEvent[] items)
        {
            return DrainList(_spawnEvents, max, out items);
        }

        /// <summary>取出至多 max 条停止通知（FIFO，独占数组）。</summary>
        public int DrainStopped(int max, out PMProjectileStopEvent[] items)
        {
            return DrainList(_stopEvents, max, out items);
        }

        /// <summary>取出至多 max 条结算（FIFO，独占数组）。R6 的**唯一**接收点。</summary>
        public int DrainSettlements(int max, out PMProjectileSettlement[] items)
        {
            return DrainList(_settlements, max, out items);
        }

        // ================================================================== 内部

        private PMProjectileAdmissionOutcome FailAdmission(PMProjectileAdmissionOutcome o,
            PMProjectileAdmissionResult result)
        {
            _rejectedAdmissions++;
            o.Result = result;
            o.SpawnPending = false;
            o.Spawned = false;
            return o;
        }

        private static PMProjectileAdmissionResult MapRegisterFailure(PMProjectileRegisterResult result)
        {
            if (result == PMProjectileRegisterResult.DuplicateId) { return PMProjectileAdmissionResult.AlreadyAdmitted; }
            if (result == PMProjectileRegisterResult.StaleEpoch) { return PMProjectileAdmissionResult.StaleEpoch; }
            if (result == PMProjectileRegisterResult.InvalidClock) { return PMProjectileAdmissionResult.InvalidClock; }
            if (result == PMProjectileRegisterResult.InvalidKey
                || result == PMProjectileRegisterResult.InvalidActivationId
                || result == PMProjectileRegisterResult.InvalidPayload
                || result == PMProjectileRegisterResult.UntrustedAuthority)
            {
                return PMProjectileAdmissionResult.InvalidRequest;
            }

            if (result == PMProjectileRegisterResult.OwnerCapacity
                || result == PMProjectileRegisterResult.RegistryCapacity)
            {
                return PMProjectileAdmissionResult.Capacity;
            }

            return PMProjectileAdmissionResult.IdNotReserved;
        }

        private static bool IsTrustedSpec(PMProjectileSpec spec)
        {
            if (spec == null) { return false; }
            if (!PMProjectileFinite.F(spec.SpeedMps) || spec.SpeedMps <= 0f) { return false; }
            if (!PMProjectileFinite.F(spec.RadiusM) || spec.RadiusM < 0f) { return false; }
            if (spec.LifetimeMs < 0) { return false; }
            return true;
        }

        /// <summary>
        /// 客户端「位置 / velocity 方向 / yaw」三样意图 + 可信身份/配置，重建一份干净的权威状态。
        /// 其余一切（权威 ID、命中集合、白名单、停止/隐藏/接管、墓碑时刻）都不来自请求。
        /// </summary>
        private static void SanitizeState(PMProjectileState source, PMProjectileState target,
            PMProjectileKey key, uint authorityNetId, uint activationId, PMVector3 muzzle, PMVector3 velocity)
        {
            target.Key = key;
            target.AuthorityNetId = authorityNetId;
            target.ActivationId = activationId;
            target.SpawnPosition = muzzle;
            target.PreviousPosition = muzzle;
            target.Position = muzzle;
            target.Velocity = velocity;
            target.Yaw = source.Yaw;
            target.MoveTimeMs = 0.0;
            target.Stopped = false;
            target.Hidden = false;
            target.TakenOver = false;
            target.StopWallTimeMs = 0.0;
            target.TimeAfterStoppedMs = 0.0;
            target.TombstoneUntilMs = 0.0;
            target.HitTargets = new uint[0];
            target.AllowedTargets = new uint[0];
        }

        private static PMProjectileVerifyRequest ToVerifyRequest(PMProjectileHitBatch batch)
        {
            PMProjectileVerifyRequest req = new PMProjectileVerifyRequest();
            req.Key = batch.Key;
            req.PreviousPosition = batch.PreviousPosition;
            req.HitPosition = batch.HitPosition;
            req.RewindMs = batch.RewindMs;
            req.TotalSimTimeMs = 0.0;
            req.Targets = batch.Targets == null
                ? new PMProjectileHitCandidate[0]
                : (PMProjectileHitCandidate[])batch.Targets.Clone();
            return req;
        }

        private static PMProjectileHitBatch ToHitBatch(PMProjectileVerifyRequest req)
        {
            PMProjectileHitBatch batch = new PMProjectileHitBatch();
            batch.Key = req.Key;
            batch.PreviousPosition = req.PreviousPosition;
            batch.HitPosition = req.HitPosition;
            batch.RewindMs = req.RewindMs;
            batch.Targets = req.Targets == null
                ? new PMProjectileHitCandidate[0]
                : (PMProjectileHitCandidate[])req.Targets.Clone();
            return batch;
        }

        // ------------------------------------------------------------ 验证核心

        private PMProjectileHitReportOutcome ProcessVerify(PMProjectileKey key, PMProjectileHitBatch batch,
            int extraDeferMs, double wallNowMs)
        {
            PMProjectileHitReportOutcome o = new PMProjectileHitReportOutcome();
            o.Key = key;
            o.Clock = PMProjectileClockCheck.Ok;
            o.Status = PMProjectileValidateStatus.Rejected;

            // 墓碑准入：这是生命周期闸门，**不是**配额消耗点（避免与 Validate 双计数）。
            PMProjectileVerifyAdmissionOutcome adm = _lifecycle.AdmitVerify(key, wallNowMs);
            if (adm.Result == PMProjectileVerifyAdmission.TombstoneExpired)
            {
                DiscardPendingVerifies(key);
                ForgetRecord(key);
                DrainLifecycleReleasesIntoDecisions();
                o.Result = PMProjectileHitReportResult.TombstoneExpired;
                return o;
            }

            if (adm.Result == PMProjectileVerifyAdmission.Retired)
            {
                o.Result = PMProjectileHitReportResult.Retired;
                return o;
            }

            if (adm.Result == PMProjectileVerifyAdmission.StaleEpoch)
            {
                o.Result = PMProjectileHitReportResult.StaleEpoch;
                return o;
            }

            if (adm.Result == PMProjectileVerifyAdmission.InvalidClock)
            {
                o.Result = PMProjectileHitReportResult.InvalidClock;
                return o;
            }

            if (adm.Result == PMProjectileVerifyAdmission.Invalid
                || adm.Result == PMProjectileVerifyAdmission.UnknownKey)
            {
                o.Result = PMProjectileHitReportResult.Invalid;
                return o;
            }

            PMProjectileSpec spec;
            PMProjectileState state;
            if (!_lifecycle.TryGetFrozen(key, out spec, out state))
            {
                o.Result = PMProjectileHitReportResult.Invalid;
                return o;
            }

            PMProjectileRegistration reg;
            if (!_lifecycle.TryGetRegistration(key, out reg))
            {
                o.Result = PMProjectileHitReportResult.Invalid;
                return o;
            }

            // A1 的 TryRecordStop 不把墓碑时刻同步进冻结 State（见报告 §A1 缺口 G1）：
            // 这里用账本上的权威值补齐只读入参，不修改 A1 文件。
            state.TombstoneUntilMs = reg.TombstoneUntilMs;

            KeyRecord rec = EnsureRecord(key, key.OwnerNetId, reg.ActivationId, reg.AuthorityNetId,
                key.Origin, 0);
            if (rec.VerifyUsed >= PMProjectileLimits.MaxVerifyCalls)
            {
                _quotaRejections++;
                o.Result = PMProjectileHitReportResult.QuotaExhausted;
                o.Status = PMProjectileValidateStatus.Rejected;
                o.Reason = PMProjectileRejectReason.VerifyQuotaExhausted;
                o.VerifyUsed = rec.VerifyUsed;
                return o;
            }

            int defer = extraDeferMs > 0 ? extraDeferMs : rec.PendingDeferMs;
            PMProjectileValidateResult vr = PMProjectileValidator.Validate(
                state, spec, reg.Activation, batch, _history, _filter, wallNowMs, defer, rec.VerifyUsed);

            o.Status = vr.Status;
            o.Reason = vr.Reason;

            // 真实配额：只由验证器的 VerifyConsumed 驱动（唯一计数点）。
            if (vr.VerifyConsumed)
            {
                rec.VerifyUsed = rec.VerifyUsed + 1;
                o.VerifyConsumed = true;
            }

            o.VerifyUsed = rec.VerifyUsed;

            if (vr.Status == PMProjectileValidateStatus.Rejected)
            {
                o.Result = vr.Reason == PMProjectileRejectReason.VerifyQuotaExhausted
                    ? PMProjectileHitReportResult.QuotaExhausted
                    : PMProjectileHitReportResult.Rejected;
                AcceptWallClock(wallNowMs);
                DrainLifecycleReleasesIntoDecisions();
                return o;
            }

            if (vr.Status == PMProjectileValidateStatus.Pending)
            {
                if (vr.Hits == null || vr.Hits.Length == 0)
                {
                    o.Result = PMProjectileHitReportResult.NoSettleableHit;
                    AcceptWallClock(wallNowMs);
                    return o;
                }

                // 暂存期去重预留：同一目标在 Pending 期间被反复上报时只入队一次。
                // 不预留的后果是真实的：C 队列单 key 追加上限会被重传耗尽，后来的**其它目标**
                // 会被 KeyAppendCapacity 丢掉（那是真实伤害丢失），而且重复结论毫无信息量。
                KeyRecord reserve = EnsureRecord(key, key.OwnerNetId, reg.ActivationId, reg.AuthorityNetId,
                    key.Origin, 0);

                List<PMProjectileValidatedHit> fresh = new List<PMProjectileValidatedHit>(vr.Hits.Length);
                for (int i = 0; i < vr.Hits.Length; i++)
                {
                    PMProjectileValidatedHit h = vr.Hits[i];
                    if (h.TargetNetId == 0u) { continue; }
                    if (reserve.PendingHitTargets.Contains(h.TargetNetId))
                    {
                        _dedupPendingHitSkips++;
                        continue;
                    }

                    fresh.Add(h);
                }

                o.StashedHitCount = 0;
                if (fresh.Count > 0)
                {
                    // C 队列只收**已消毒结论**（原始 batch 不许进来）。
                    PMProjectileValidatedHitSet set = new PMProjectileValidatedHitSet();
                    set.Key = key;
                    set.Hits = fresh.ToArray();
                    PMProjectileHitStashOutcome stash = _pendingHits.TryStash(key, set, wallNowMs);
                    if (stash.Result == PMProjectileHitStashResult.StaleEpoch)
                    {
                        o.Result = PMProjectileHitReportResult.StaleEpoch;
                        return o;
                    }

                    if (stash.Result == PMProjectileHitStashResult.Invalid)
                    {
                        o.Result = PMProjectileHitReportResult.Invalid;
                        return o;
                    }

                    if (stash.Result != PMProjectileHitStashResult.KeyAppendCapacity)
                    {
                        // 只有真的进了组才提交去重预留（否则那批目标以后再也不能结算）。
                        for (int i = 0; i < fresh.Count; i++) { reserve.PendingHitTargets.Add(fresh[i].TargetNetId); }
                        o.StashedHitCount = fresh.Count;
                    }

                    // 容量淘汰（丢最旧未结算组）时，被淘汰组的去重预留必须一并释放，
                    // 否则那个目标被预留永久挡住 = 后续真实命中无法结算。
                    ReconcileDroppedPendingHitGroups();
                }

                // StopOnHit 必须在 **Pending 期间就停**（不是等到 Confirm）：命中已经成立，
                // 不停的话弹会继续往前飞（幽灵轨迹 + 越飞越远的错误后续命中）。
                if (spec != null && spec.StopOnHit && !reg.Stopped)
                {
                    if (RecordStop(key, state == null ? PMVector3.Zero : state.Position, wallNowMs))
                    {
                        o.StopRecorded = true;
                    }
                }

                AcceptWallClock(wallNowMs);
                o.Result = PMProjectileHitReportResult.Pending;
                return o;
            }

            // Confirmed
            if (vr.Hits == null || vr.Hits.Length == 0)
            {
                o.Result = PMProjectileHitReportResult.NoSettleableHit;
                AcceptWallClock(wallNowMs);
                DrainLifecycleReleasesIntoDecisions();
                return o;
            }

            bool stopped;
            int settled = SettleHits(key, spec, reg, vr.Hits, wallNowMs, out stopped);
            o.SettledHitCount = settled;
            o.StopRecorded = stopped;
            o.Result = settled > 0 ? PMProjectileHitReportResult.Settled : PMProjectileHitReportResult.NoSettleableHit;

            AcceptWallClock(wallNowMs);
            DrainLifecycleReleasesIntoDecisions();
            return o;
        }

        /// <summary>
        /// 结算一批已消毒命中：逐目标（1）最终有效性复核（epoch/stream/alive）、（2）去重提交，
        /// 两者都通过才进入结算出口。去重提交发生在入队之前 —— 契约「先提交再排队」。
        /// </summary>
        private int SettleHits(PMProjectileKey key, PMProjectileSpec spec, PMProjectileRegistration reg,
            PMProjectileValidatedHit[] hits, double wallNowMs, out bool stopRecorded)
        {
            stopRecorded = false;

            List<PMProjectileValidatedHit> accepted = new List<PMProjectileValidatedHit>(hits.Length);
            for (int i = 0; i < hits.Length; i++)
            {
                PMProjectileValidatedHit hit = hits[i];
                if (hit.TargetNetId == 0u) { continue; }
                if (!IsTargetStillSettleable(key, hit, wallNowMs))
                {
                    _rejectedStaleTargets++;
                    continue;
                }

                if (!_lifecycle.TryAddHitTarget(key, hit.TargetNetId)) { continue; }
                accepted.Add(hit);
            }

            if (accepted.Count == 0) { return 0; }

            PMProjectileSettlement s = new PMProjectileSettlement();
            s.Key = key;
            s.OwnerNetId = key.OwnerNetId;
            s.ActivationId = reg.ActivationId;
            uint authorityNetId = reg.AuthorityNetId;
            KeyRecord rec;
            if (_records.TryGetValue(key, out rec) && rec.AuthorityNetId != 0u) { authorityNetId = rec.AuthorityNetId; }
            s.AuthorityNetId = authorityNetId;
            s.Origin = key.Origin;
            s.WallTimeMs = wallNowMs;
            s.StopOnHit = spec != null && spec.StopOnHit;
            s.HitCount = accepted.Count;
            s.Hits = accepted.ToArray();
            PushSettlement(s);

            if (s.StopOnHit && !reg.Stopped)
            {
                if (RecordStop(key, reg.State == null ? PMVector3.Zero : reg.State.Position, wallNowMs))
                {
                    stopRecorded = true;
                }
            }

            return accepted.Count;
        }

        private int SettleHitSets(PMProjectileKey key, PMProjectileValidatedHitSet[] sets, double wallNowMs,
            out bool stopRecorded)
        {
            stopRecorded = false;
            if (sets == null || sets.Length == 0) { return 0; }

            List<PMProjectileValidatedHit> all = new List<PMProjectileValidatedHit>();
            for (int i = 0; i < sets.Length; i++)
            {
                PMProjectileValidatedHitSet set = sets[i];
                if (set == null || set.Hits == null) { continue; }
                for (int h = 0; h < set.Hits.Length; h++) { all.Add(set.Hits[h]); }
            }

            if (all.Count == 0) { return 0; }

            PMProjectileSpec spec;
            PMProjectileState state;
            _lifecycle.TryGetFrozen(key, out spec, out state);

            PMProjectileRegistration reg;
            if (!_lifecycle.TryGetRegistration(key, out reg))
            {
                return 0;
            }

            return SettleHits(key, spec, reg, all.ToArray(), wallNowMs, out stopRecorded);
        }

        /// <summary>
        /// 最终结算前的轻量复核：只确认目标 epoch/stream/alive 仍有效（**不重跑 L0–L4 几何**，
        /// 因此不会二次消耗 Verify 配额，也不会因时间推进改判命中）。
        /// </summary>
        private bool IsTargetStillSettleable(PMProjectileKey key, PMProjectileValidatedHit hit, double wallNowMs)
        {
            if (_history == null) { return false; }

            PMProjectileHitCandidate candidate = default(PMProjectileHitCandidate);
            candidate.TargetNetId = hit.TargetNetId;
            candidate.TargetStreamVersion = hit.TargetStreamVersion;

            PMProjectileTargetSample sample;
            PMProjectileHistoryResolution resolution;
            if (!_history.TryResolve(key.Epoch, candidate, wallNowMs, 0, 0, out sample, out resolution))
            {
                return false;
            }

            if (sample.StreamVersion != hit.TargetStreamVersion) { return false; }
            if (!sample.Alive) { return false; }
            return true;
        }

        // ------------------------------------------------------------ 运动核心

        private PMProjectileAdvanceOutcome NewAdvanceOutcome(PMProjectileKey key, int requestedMs)
        {
            PMProjectileAdvanceOutcome o = new PMProjectileAdvanceOutcome();
            o.Key = key;
            o.Result = PMProjectileAdvanceResult.Invalid;
            o.Motion = PMProjectileMotionResult.Invalid;
            o.CatchUp = PMProjectileCatchUpResult.Invalid;
            o.RequestedMs = requestedMs;
            o.Position = PMVector3.Zero;
            o.Velocity = PMVector3.Zero;
            return o;
        }

        private PMProjectileAdvanceOutcome CheckOnly(PMProjectileKey key, PMProjectileAdvanceOutcome o)
        {
            if (_lifecycle.IsRetired(key)) { o.Result = PMProjectileAdvanceResult.Retired; return o; }
            if (!_lifecycle.IsRegistered(key))
            {
                o.Result = PMProjectileAdvanceResult.UnknownKey;
                return o;
            }

            PMProjectileRegistration reg;
            _lifecycle.TryGetRegistration(key, out reg);
            o.Position = reg.State.Position;
            o.Velocity = reg.State.Velocity;
            o.MoveTimeMs = reg.State.MoveTimeMs;
            o.Motion = PMProjectileMotionResult.NotMovable;
            o.Result = reg.Stopped ? PMProjectileAdvanceResult.NotMovable : PMProjectileAdvanceResult.Advanced;
            return o;
        }

        private PMProjectileAdvanceOutcome StepMotion(PMProjectileKey key, int deltaMs, double wallNowMs,
            PMProjectileAdvanceOutcome o)
        {
            o.Key = key;
            o.Result = PMProjectileAdvanceResult.Invalid;
            o.Motion = PMProjectileMotionResult.Invalid;
            o.Position = PMVector3.Zero;
            o.Velocity = PMVector3.Zero;
            o.MoveTimeMs = 0.0;
            o.Stopped = false;

            if (key.Epoch != _epoch)
            {
                o.Result = PMProjectileAdvanceResult.StaleEpoch;
                return o;
            }

            if (_lifecycle.IsRetired(key))
            {
                o.Result = PMProjectileAdvanceResult.Retired;
                return o;
            }

            if (!_lifecycle.IsRegistered(key))
            {
                o.Result = PMProjectileAdvanceResult.UnknownKey;
                return o;
            }

            PMProjectileSpec spec;
            PMProjectileState state;
            if (!_lifecycle.TryGetFrozen(key, out spec, out state))
            {
                o.Result = PMProjectileAdvanceResult.UnknownKey;
                return o;
            }

            // 运动已是终态：不叫 hook、也不写账本（stop 后继续问 hook 只会把“已停止”误报成 hook 失败）。
            // 注意：假弹提前结束（NotifyPredictedEnded）也会把冻结状态的 Stopped 置真，因此只判 Stopped 就够。
            if (state.Stopped)
            {
                o.Motion = PMProjectileMotionResult.NotMovable;
                o.Position = state.Position;
                o.Velocity = state.Velocity;
                o.MoveTimeMs = state.MoveTimeMs;
                o.Result = PMProjectileAdvanceResult.NotMovable;
                return o;
            }

            float dt = deltaMs / 1000f;
            PMVector3 straight = state.Position + state.Velocity * dt;
            PMVector3 straightVelocity = state.Velocity;
            float straightYaw = state.Yaw;

            PMVector3 position = straight;
            PMVector3 velocity = straightVelocity;
            float yaw = straightYaw;

            if (_hostMotion != null)
            {
                PMVector3 hookPosition;
                PMVector3 hookVelocity;
                float hookYaw;
                bool used;
                try
                {
                    // state 是 TryGetFrozen 给出的**深 clone**：hook 可以随意改它，但写不回账本，
                    // 传入的总是弹的真实当前位置（供其做碰撞查询）。
                    used = _hostMotion.TryStep(key, spec, state, deltaMs, straight, straightVelocity,
                        straightYaw, out hookPosition, out hookVelocity, out hookYaw);
                }
                catch (Exception)
                {
                    used = false;
                    hookPosition = state.Position;
                    hookVelocity = PMVector3.Zero;
                    hookYaw = straightYaw;
                }

                if (!used || !PMProjectileFinite.V(hookPosition) || !PMProjectileFinite.V(hookVelocity)
                    || !PMProjectileFinite.F(hookYaw))
                {
                    // **fail closed**：hook 是场景阻挡的唯一来源。它失败/非 finite/抛异常时，
                    // 回退直线就等于「接口失败 ⇒ 穿墙」。这里本步不推进，就地停止（墓碑 + 停止出口）。
                    return FailClosedHostMotion(key, state, wallNowMs, o);
                }

                position = hookPosition;
                velocity = hookVelocity;
                yaw = hookYaw;
            }

            double nextMoveTime = state.MoveTimeMs + (double)deltaMs;

            PMProjectileMotionOutcome motion =
                _lifecycle.TryAdvanceMotion(key, position, state.Position, velocity, yaw, nextMoveTime);
            o.Motion = motion.Result;
            o.Position = motion.Position;
            o.Velocity = motion.Velocity;
            o.MoveTimeMs = motion.MoveTimeMs;

            if (motion.Result == PMProjectileMotionResult.NotMovable)
            {
                o.Result = PMProjectileAdvanceResult.NotMovable;
                return o;
            }

            if (motion.Result != PMProjectileMotionResult.Advanced)
            {
                o.Result = MapMotionFailure(motion.Result);
                return o;
            }

            o.Result = PMProjectileAdvanceResult.Advanced;

            // Lifetime 到期 → 停弹 + 墓碑。
            if (spec != null && spec.LifetimeMs > 0 && motion.MoveTimeMs >= (double)spec.LifetimeMs)
            {
                if (RecordStop(key, motion.Position, wallNowMs))
                {
                    o.Stopped = true;
                    o.Result = PMProjectileAdvanceResult.Stopped;
                    return o;
                }
            }

            if (_hostMotion != null)
            {
                bool shouldStop;
                PMVector3 stopPosition;
                try
                {
                    shouldStop = _hostMotion.TryStop(key, spec, state, wallNowMs, out stopPosition);
                }
                catch (Exception)
                {
                    // 停止 hook 抛异常：不能当成「无障碍继续飞」（那会把一次失败变成穿墙）。
                    return FailClosedHostStop(key, motion, wallNowMs, o);
                }

                if (shouldStop)
                {
                    if (!PMProjectileFinite.V(stopPosition))
                    {
                        // 非 finite 停止点：不得写进账本；fail closed 地停在当前位置。
                        return FailClosedHostStop(key, motion, wallNowMs, o);
                    }

                    if (RecordStop(key, stopPosition, wallNowMs))
                    {
                        o.Stopped = true;
                        o.Result = PMProjectileAdvanceResult.Stopped;
                    }
                }
            }

            return o;
        }

        /// <summary>
        /// 宿主运动 hook 失败的 fail-closed 落点（本步**不推进**）：弹停在当前位置，
        /// 建墓碑 + 停止出口，结果标为 <see cref="PMProjectileAdvanceResult.HostMotionFaulted"/>。
        /// 注意 o.Position 仍是账本真实位置（不采用 hook 给出的任何值）。
        /// </summary>
        private PMProjectileAdvanceOutcome FailClosedHostMotion(PMProjectileKey key, PMProjectileState state,
            double wallNowMs, PMProjectileAdvanceOutcome o)
        {
            _hostMotionFailures++;

            PMVector3 here = state == null ? PMVector3.Zero : state.Position;
            o.Motion = PMProjectileMotionResult.NotMovable;
            o.Position = here;
            o.Velocity = PMVector3.Zero;
            o.MoveTimeMs = state == null ? 0.0 : state.MoveTimeMs;
            o.Result = PMProjectileAdvanceResult.HostMotionFaulted;
            o.Stopped = true;
            RecordStop(key, here, wallNowMs);
            return o;
        }

        /// <summary>宿主**停止** hook 失败的 fail-closed 落点：本步运动已生效，但立即就地停止。</summary>
        private PMProjectileAdvanceOutcome FailClosedHostStop(PMProjectileKey key, PMProjectileMotionOutcome motion,
            double wallNowMs, PMProjectileAdvanceOutcome o)
        {
            _hostMotionFailures++;

            o.Motion = motion.Result;
            o.Position = motion.Position;
            o.Velocity = motion.Velocity;
            o.MoveTimeMs = motion.MoveTimeMs;
            o.Result = PMProjectileAdvanceResult.HostMotionFaulted;
            o.Stopped = true;
            RecordStop(key, motion.Position, wallNowMs);
            return o;
        }

        private static PMProjectileAdvanceResult MapMotionFailure(PMProjectileMotionResult result)
        {
            if (result == PMProjectileMotionResult.StaleEpoch) { return PMProjectileAdvanceResult.StaleEpoch; }
            if (result == PMProjectileMotionResult.Retired) { return PMProjectileAdvanceResult.Retired; }
            if (result == PMProjectileMotionResult.UnknownKey) { return PMProjectileAdvanceResult.UnknownKey; }
            if (result == PMProjectileMotionResult.NotMovable) { return PMProjectileAdvanceResult.NotMovable; }
            return PMProjectileAdvanceResult.Invalid;
        }

        /// <summary>记录停止（墓碑）并发出 Stopped 出口。返回 true = 本次真的产生了一次停止。</summary>
        private bool RecordStop(PMProjectileKey key, PMVector3 stopPosition, double wallNowMs)
        {
            PMProjectileSpec spec;
            PMProjectileState state;
            if (!_lifecycle.TryGetFrozen(key, out spec, out state)) { return false; }

            KeyRecord rec = EnsureRecord(key, key.OwnerNetId, 0u, 0u, key.Origin, 0);
            int delayDestroyMs = spec == null ? 0 : spec.DelayDestroyMs;

            PMProjectileStopOutcome stop =
                _lifecycle.TryRecordStop(key, stopPosition, wallNowMs, rec.PredictionMs, delayDestroyMs);
            if (!stop.StopEventEmitted) { return false; }

            PMProjectileStopEvent ev = new PMProjectileStopEvent();
            ev.Key = key;
            ev.OwnerNetId = key.OwnerNetId;
            ev.StopPosition = stopPosition;
            ev.TombstoneUntilMs = stop.TombstoneUntilMs;
            ev.WallTimeMs = wallNowMs;
            PushStopEvent(ev);
            return true;
        }

        // ------------------------------------------------------------ 回放 / 清理

        /// <summary>
        /// B 队列按 FIFO 回放（注册先于回放：TryBeginReplay 自带该检查）。
        /// 每条回放都走同一条 ProcessVerify，因此配额与去重口径与直达路径完全一致。
        /// </summary>
        private int ReplayStashed(PMProjectileKey key, int extraDeferMs, double wallNowMs)
        {
            int settledHits;
            return ReplayStashed(key, extraDeferMs, wallNowMs, out settledHits);
        }

        private int ReplayStashed(PMProjectileKey key, int extraDeferMs, double wallNowMs, out int settledHits)
        {
            settledHits = 0;
            PMProjectileVerifyReplayOutcome replay =
                _pendingVerifies.TryBeginReplay(_lifecycle, key, extraDeferMs, wallNowMs);
            if (replay.RegistrationMissing || replay.Items == null || replay.Items.Length == 0) { return 0; }

            for (int i = 0; i < replay.Items.Length; i++)
            {
                PMProjectileHitReportOutcome o =
                    ProcessVerify(key, ToHitBatch(replay.Items[i]), extraDeferMs, wallNowMs);
                settledHits += o.SettledHitCount;
            }

            return replay.Items.Length;
        }

        private void DiscardPendingVerifies(PMProjectileKey key)
        {
            _pendingVerifies.Discard(key);
        }

        /// <summary>退回一个 key：登记 + 记录 + 出口决策（终态不静默丢失）。</summary>
        private bool RetireKey(PMProjectileKey key, double wallNowMs)
        {
            return RetireKey(key, wallNowMs, PMProjectileReleaseReason.TombstoneExpired);
        }

        private bool RetireKey(PMProjectileKey key, double wallNowMs, PMProjectileReleaseReason reason)
        {
            PMProjectileRegistration snapshot;
            bool known = _lifecycle.TryGetRegistration(key, out snapshot);
            if (!known)
            {
                ForgetRecord(key);
                return false;
            }

            bool retired = _lifecycle.Retire(key, wallNowMs);
            if (retired)
            {
                PMProjectileActivationRelease release = new PMProjectileActivationRelease();
                release.Key = key;
                release.OwnerNetId = key.OwnerNetId;
                release.ActivationId = snapshot.ActivationId;
                release.Origin = key.Origin;
                release.AuthorityNetId = snapshot.AuthorityNetId;
                release.Outcome = snapshot.Activation;
                release.Reason = reason;
                release.RemovedInstance = snapshot.Predicted && !snapshot.TakenOver;
                release.PendingElapsedMs = wallNowMs - snapshot.RegisteredWallTimeMs;
                if (release.PendingElapsedMs < 0.0) { release.PendingElapsedMs = 0.0; }
                release.WallTimeMs = wallNowMs;
                PushDecision(release);
            }

            ForgetRecord(key);
            return retired;
        }

        private int PruneRecords()
        {
            int forgotten = 0;
            for (int i = _recordOrder.Count - 1; i >= 0; i--)
            {
                PMProjectileKey key = _recordOrder[i];
                if (_lifecycle.IsRegistered(key) || _pendingVerifies.IsPendingMarkerSet(key)
                    || _pendingHits.Contains(key) || _pendingSpawns.Contains(key))
                {
                    continue;
                }
                _records.Remove(key);
                _recordOrder.RemoveAt(i);
                forgotten++;
            }

            return forgotten;
        }

        private void DrainLifecycleReleasesIntoDecisions()
        {
            // A1 的中转队列若发生溢出丢弃，就意味着「需要实际撤销的假弹通知」被静默吞掉 ⇒ 永久假弹。
            // 正常链路不可达（容量 = 预测活对象上限 1024，且本方法每次调用都抽干），
            // 但一旦发生必须显式故障，不能继续假装成功。
            if (_lifecycle.DroppedReleaseCount > _observedLifecycleReleaseDrops)
            {
                _observedLifecycleReleaseDrops = _lifecycle.DroppedReleaseCount;
                Fault(PMProjectileFaultReason.LifecycleReleaseDrop,
                    "生命周期待取释放队列发生溢出丢弃（累计 " + _lifecycle.DroppedReleaseCount
                    + " 条）：释放项就是“撤销假弹”的指令，丢了就是永久假弹");
            }

            // **先做容量预检**：生命周期侧的待取释放队列一旦被取出就离开了源头，
            // 若推入决策队列时才发现越界，那批释放项就真丢了（激活结论丢失 ⇒ 永久假弹）。
            int pending = _lifecycle.PendingReleaseCount;
            if (pending > 0 && _decisions.Count + pending > PMProjectileCoordinatorLimits.MaxDecisions)
            {
                Fault(PMProjectileFaultReason.DecisionQueueOverflow,
                    "生命周期待取释放项（" + pending + "）无法全部放入决策出口队列（当前 " + _decisions.Count
                    + "/" + PMProjectileCoordinatorLimits.MaxDecisions + "）：提前故障，避免丢弃激活结论");
            }

            PMProjectileActivationRelease[] releases;
            int n = _lifecycle.DrainPendingReleases(PMProjectileCoordinatorLimits.MaxDecisions, out releases);
            for (int i = 0; i < n; i++) { PushDecision(releases[i]); }
        }

        // ------------------------------------------------------ 故障（Faulted）

        /// <summary>
        /// 进入**永久故障**并抛出。任何一次调用都不允许在「数据不完整」时假装成功：
        /// 本方法先把故障状态提交（IsFaulted=true + 原因），再抛 InvalidOperationException；
        /// 既有排队项目不被覆盖（本方法不碰任何出口队列），宿主可以 Drain 出来处置，
        /// 然后必须断连并升 epoch（唯一的恢复路径）。
        /// </summary>
        private void Fault(PMProjectileFaultReason reason, string message)
        {
            if (!_faulted)
            {
                _faulted = true;
                _faultReason = reason;
                _faultMessage = message;
            }

            throw new InvalidOperationException(
                "[PMProjectileCoordinator] 永久故障(" + reason + ")：" + message
                + "。本 epoch 已停止接纳变更型调用；既有出口项目保留原样（可 Drain），"
                + "宿主必须断连并升 epoch 重建（AdvanceEpoch 是唯一恢复路径）。");
        }

        private void ThrowIfFaulted()
        {
            if (_faulted)
            {
                throw new InvalidOperationException(
                    "[PMProjectileCoordinator] 已处于永久故障(" + _faultReason + ")：" + FaultMessage
                    + "。拒绝一切变更型调用；请断连并升 epoch 重建。");
            }
        }

        /// <summary>
        /// 调用入口的容量预检：任一出口队列已达容量时**立即故障**（而不是等写到一半才抛）。
        /// 为什么要预检：结算/决策的提交发生在状态变更（去重提交、激活解挂）之前，
        /// 入口即发现出口已满时最好直接拒绝，别让调用方以为“本次已生效”。
        /// </summary>
        private void EnsureOutputHeadroom()
        {
            if (_decisions.Count >= PMProjectileCoordinatorLimits.MaxDecisions)
            {
                Fault(PMProjectileFaultReason.DecisionQueueOverflow,
                    "激活决策出口队列已满（" + _decisions.Count + "/" + PMProjectileCoordinatorLimits.MaxDecisions
                    + "），下一个决策无处存放：拒绝继续运行，避免静默丢弃已受理结论");
            }

            if (_spawnEvents.Count >= PMProjectileCoordinatorLimits.MaxSpawnEvents)
            {
                Fault(PMProjectileFaultReason.SpawnEventQueueOverflow,
                    "权威生成出口队列已满（" + _spawnEvents.Count + "/" + PMProjectileCoordinatorLimits.MaxSpawnEvents
                    + "）：继续运行会丢生成事件（永久假弹），拒绝");
            }

            if (_stopEvents.Count >= PMProjectileCoordinatorLimits.MaxStopEvents)
            {
                Fault(PMProjectileFaultReason.StopEventQueueOverflow,
                    "停止通知出口队列已满（" + _stopEvents.Count + "/" + PMProjectileCoordinatorLimits.MaxStopEvents
                    + "）：继续运行会丢停止通知，拒绝");
            }

            if (_settlements.Count >= PMProjectileCoordinatorLimits.MaxSettlements)
            {
                Fault(PMProjectileFaultReason.SettlementQueueOverflow,
                    "结算出口队列已满（" + _settlements.Count + "/" + PMProjectileCoordinatorLimits.MaxSettlements
                    + "）：**绝不丢已接受出口**（那就是丢真实伤害）——去重可能已提交，拒绝继续运行");
            }
        }

        // ------------------------------------------------------ 暂存期去重预留

        /// <summary>释放某 key 的暂存期去重预留（组已兑现/被拒/被丢弃时的必经收尾）。</summary>
        private void ClearPendingHitTargets(PMProjectileKey key)
        {
            KeyRecord rec;
            if (!_records.TryGetValue(key, out rec)) { return; }
            if (rec.PendingHitTargets.Count == 0) { return; }
            _releasedPendingHitReservations += rec.PendingHitTargets.Count;
            rec.PendingHitTargets.Clear();
        }

        /// <summary>
        /// 释放「本地还记着暂存去重预留，但 C 队列里已经没有该组」的孤儿预留。
        /// 为什么需要：C 队列容量淘汰（丢最旧未结算组）是 A1 队列内部行为，只报计数不报 key；
        /// 若不同步释放预留，被淘汰组里的目标会被永久挡住（后续真实命中再也结不了）。
        /// </summary>
        private int ReleaseOrphanPendingHitReservations()
        {
            int released = 0;
            for (int i = _recordOrder.Count - 1; i >= 0; i--)
            {
                KeyRecord rec;
                if (!_records.TryGetValue(_recordOrder[i], out rec)) { continue; }
                if (rec.PendingHitTargets.Count == 0) { continue; }
                if (_pendingHits.Contains(rec.Key)) { continue; }
                released += rec.PendingHitTargets.Count;
                rec.PendingHitTargets.Clear();
            }

            return released;
        }

        private void ReconcileDroppedPendingHitGroups()
        {
            if (_pendingHits.DroppedGroupCount <= _observedDroppedHitGroups) { return; }
            _observedDroppedHitGroups = _pendingHits.DroppedGroupCount;
            _releasedPendingHitReservations += ReleaseOrphanPendingHitReservations();
        }

        // ------------------------------------------------------ 预留 → 权威升级

        /// <summary>
        /// 把 admission 时预留的预测登记**原地升级**为权威本体。
        /// 升级失败（除幂等 AlreadyPromoted 外）说明内部不变式被破坏（例如 ledger 说 Confirmed
        /// 但登记项不是 Confirmed）：这种情况**不得**假装成功（那会让玩家弹永远追不上、
        /// 而且生成出口里的冻结状态权威 ID 恒为 0），因此直接故障。
        /// </summary>
        private void PromoteReservedOrFail(PMProjectileKey key, uint authorityNetId, double wallNowMs)
        {
            PMProjectilePromoteOutcome p =
                _lifecycle.TryPromoteReservedToAuthority(key, authorityNetId, wallNowMs);
            if (p.Result == PMProjectilePromoteResult.Promoted)
            {
                _promotions++;
                return;
            }

            if (p.Result == PMProjectilePromoteResult.AlreadyPromoted) { return; }

            Fault(PMProjectileFaultReason.InternalInvariant,
                "Confirmed 的预留弹无法原地升级为权威：key=" + key.ProjectileId + " result=" + p.Result
                + "（authorityNetId=" + authorityNetId + "）；升级缺失会导致该弹永远不能追赶且权威 ID 为 0");
        }

        // ------------------------------------------------------------ 记账辅助

        private void EnsureRecordCapacity()
        {
            if (_records.Count >= PMProjectileCoordinatorLimits.MaxKeyRecords)
            {
                // 原实现是「汰最旧 + 计数」：那会静默丢掉 verify 配额与暂存去重预留。
                // 这个上限远超契约允许的活对象/挂起总量（1024+1024+1024+128），正常链路不可达；
                // 真到了就是记账退化，宁可故障。
                Fault(PMProjectileFaultReason.KeyRecordTableOverflow,
                    "整合器记账表已满（" + _records.Count + "/" + PMProjectileCoordinatorLimits.MaxKeyRecords
                    + "）：继续会静默丢掉 verify 配额/去重预留，拒绝");
            }
        }

        private KeyRecord EnsureRecord(PMProjectileKey key, uint ownerNetId, uint activationId,
            uint authorityNetId, PMProjectileOrigin origin, int predictionMs)
        {
            KeyRecord rec;
            if (_records.TryGetValue(key, out rec)) { return rec; }

            EnsureRecordCapacity();
            rec = new KeyRecord();
            rec.Key = key;
            rec.OwnerNetId = ownerNetId;
            rec.ActivationId = activationId;
            rec.AuthorityNetId = authorityNetId;
            rec.Origin = origin;
            rec.PredictionMs = predictionMs;
            _records.Add(key, rec);
            _recordOrder.Add(key);
            return rec;
        }

        private void MarkSpawned(PMProjectileKey key)
        {
            KeyRecord rec = EnsureRecord(key, key.OwnerNetId, 0u, 0u, key.Origin, 0);
            rec.Spawned = true;
        }

        private void ForgetRecord(PMProjectileKey key)
        {
            if (_records.Remove(key)) { _recordOrder.Remove(key); }
        }
        private bool EmitSpawnReady(PMProjectileKey key, double wallNowMs)
        {
            PMProjectileSpec spec;
            PMProjectileState state;
            if (!_lifecycle.TryGetFrozen(key, out spec, out state)) { return false; }

            PMProjectileRegistration reg;
            if (!_lifecycle.TryGetRegistration(key, out reg)) { return false; }

            KeyRecord rec;
            _records.TryGetValue(key, out rec);

            PMProjectileSpawnEvent ev = new PMProjectileSpawnEvent();
            ev.Key = key;
            ev.OwnerNetId = key.OwnerNetId;
            ev.ActivationId = reg.ActivationId;
            // 权威 ID：A1 对**预测**登记一律归 0（预测侧无权威 ID），因此这里优先用整合器
            // 自有的可信入参记录，保证复制出口仍带得动 authorityNetId。
            ev.AuthorityNetId = rec != null && rec.AuthorityNetId != 0u ? rec.AuthorityNetId : reg.AuthorityNetId;
            ev.Origin = key.Origin;
            ev.WallTimeMs = wallNowMs;
            ev.Spec = spec;
            ev.State = state;
            PushSpawnEvent(ev);
            return true;
        }

        // ------------------------------------------------------------ 有界出口

        private void PushDecision(PMProjectileActivationRelease release)
        {
            if (_decisions.Count >= PMProjectileCoordinatorLimits.MaxDecisions)
            {
                Fault(PMProjectileFaultReason.DecisionQueueOverflow,
                    "激活决策出口队列写入越界（" + _decisions.Count + "/" + PMProjectileCoordinatorLimits.MaxDecisions
                    + "）：不得覆盖已排队项目（那就是静默丢激活结论）");
            }

            _decisions.Add(release);
        }

        private void PushReleases(PMProjectileActivationRelease[] releases)
        {
            if (releases == null) { return; }
            for (int i = 0; i < releases.Length; i++) { PushDecision(releases[i]); }
        }

        private void PushSpawnEvent(PMProjectileSpawnEvent ev)
        {
            if (_spawnEvents.Count >= PMProjectileCoordinatorLimits.MaxSpawnEvents)
            {
                Fault(PMProjectileFaultReason.SpawnEventQueueOverflow,
                    "权威生成出口队列写入越界（" + _spawnEvents.Count + "/" + PMProjectileCoordinatorLimits.MaxSpawnEvents
                    + "）：不得覆盖已排队项目（那就是丢生成 = 永久假弹）");
            }

            _spawnEvents.Add(ev);
        }

        private void PushStopEvent(PMProjectileStopEvent ev)
        {
            if (_stopEvents.Count >= PMProjectileCoordinatorLimits.MaxStopEvents)
            {
                Fault(PMProjectileFaultReason.StopEventQueueOverflow,
                    "停止通知出口队列写入越界（" + _stopEvents.Count + "/" + PMProjectileCoordinatorLimits.MaxStopEvents
                    + "）：不得覆盖已排队项目");
            }

            _stopEvents.Add(ev);
        }

        private void PushSettlement(PMProjectileSettlement s)
        {
            if (_settlements.Count >= PMProjectileCoordinatorLimits.MaxSettlements)
            {
                Fault(PMProjectileFaultReason.SettlementQueueOverflow,
                    "结算出口队列写入越界（" + _settlements.Count + "/" + PMProjectileCoordinatorLimits.MaxSettlements
                    + "）：结算就是真实伤害，不得丢最旧/覆盖已排队项目");
            }

            _settlements.Add(s);
        }

        private static int DrainList<T>(List<T> list, int max, out T[] items)
        {
            if (max <= 0 || list.Count == 0)
            {
                items = new T[0];
                return 0;
            }

            int n = list.Count < max ? list.Count : max;
            items = new T[n];
            for (int i = 0; i < n; i++) { items[i] = list[i]; }
            list.RemoveRange(0, n);
            return n;
        }

        // ------------------------------------------------------------ 墙钟

        private PMProjectileClockCheck CheckClock(double wallNowMs)
        {
            return PMProjectileWallClock.Validate(wallNowMs, _wallMs);
        }

        private void AcceptWallClock(double wallNowMs)
        {
            if (wallNowMs > _wallMs) { _wallMs = wallNowMs; }
        }
    }
}
