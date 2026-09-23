// ============================================================================
//  PMProjectile 生命周期 / 假弹镜像接管 / 激活账本 / 停止墓碑（R5-A1 / M09）
// ============================================================================
//  契约来源（只读）：
//    · Docs/plans/net-r5-projectile-contract.md（R5 冻结契约）
//    · Docs/plans/_r5_semantics_survey.md §1.1–1.4（UE 侧语义整理）
//    · Docs/plans/net-architecture-migration.md §3.9.4「投射物与权威裁决」+ D-R5-01
//    · Client/Assets/Scripts/PMProjectile/PMProjectileContracts.cs（主 Agent 冻结共享类型）
//
//  硬边界：
//    · 纯 C#：netstandard2.0 + C#7.3，零 UnityEngine / 零 UE / 零业务类型（M09 纪律）。
//    · **不修改**共享契约类型：本文件只新增自己的 result/outcome/limits 类型。
//    · 不依赖尚未实现的 PMProjectileValidator（A2 组产物）：本文件不做几何/历史/L0–L4 判定，
//      只做身份、生命周期、接管、墓碑与「谁在等激活」的记账。
//    · **无外部回调**：所有会消费状态的 API 走「独占返回列表」（调用方拥有、可自由改写），
//      因此不存在「回调抛异常导致重复结算」的窗口。出队/去重提交都发生在返回之前。
//
//  时间归属（G2 决策，禁止跨基相减）：
//    · TTL / 墓碑 / 挂起时长 / 墙钟单调性 → 一律用 **墙钟** wallNowMs（double 毫秒）。
//      用 double 而不是 int：宿主可直接喂 Stopwatch 毫秒，不需要先取整；
//      「非 finite / 倒退显式拒绝」由 PMProjectileWallClock.Validate 统一裁决。
//    · 帧锚新鲜度 = TotalSimTimeMs（A2 负责），本文件不碰。
//
//  关键语义（逐条对应契约）：
//    1) 身份：PMProjectileKey = (Epoch, OwnerNetId, ProjectileId, Origin)。
//       同 epoch/owner/origin 的 ProjectileId **单调递增且永不复用**（水位拒绝，含已销毁 key）。
//       跨 owner 同号合法（身份含 owner）；跨 epoch 请求一律 StaleEpoch。
//    2) server-direct 可信入口：只有传 PMProjectileTrust.AuthorityServerDirect 才能登记权威弹；
//       客户端路径调用该入口被拒（UntrustedAuthority）。客户端预测登记的 ActivationId 必须非 0
//       （0 只允许可信 ServerDirect），非 0 时由激活账本裁决 Confirmed / Rejected / Pending。
//    3) 注册先于追赶：TryBeginCatchUp 只接受**已成功登记**的 key，且每 key 只能开始一次
//       （AlreadyStarted），在 API 上封住「未注册就追赶」与「重入重复注册/重复追赶」。
//    4) 假弹镜像接管：一次消费（TryTakeoverPredicted 第二次起 AlreadyTakenOver）；
//       假弹存活时镜像位置不写（GateFakeAlive 位置闸门）；假弹早结束后的迟到镜像只隐藏、
//       不复活、不二次停止通知（HiddenNoRevive + HideWithoutStopEvent）；
//       停止（本地/镜像/假弹结束）后镜像不得再写运动（StoppedNotMoved）。
//    5) 拒绝即撤销：Rejected 会**实际移除**预测登记，并产出 RemovedInstance=true 的释放项；
//       但只对「真存在的本地假弹」为 true —— 已接管的弹与从未落地的登记不会误删。
//    6) 停止与墓碑：停止保留权威记录（注册不解除），墓碑窗口内允许迟到 Verify
//       （AllowedInTombstone），超窗明确过期（TombstoneExpired）并清理。
//       镜像带来的 Stopped 也会建立墓碑窗口（否则迟到的合法 Verify 会被误判过期）。
//    7) 终态不倒退：已 Retired 的 key 不会因迟到镜像/Verify 复活；同一 activation 的多颗弹
//       可同时解挂（一次 SetActivationResult 产出多条释放项）。
//       「ID 永不复用」由每 (owner, origin) 高水位保证，与终态标记环是否被淘汰无关。
//       激活账本满时会淘汰最旧终态项（只 512）；被淘汰的**终态判决**另有一份有界记忆
//       （_terminalMemory，≤ 512），只被**写路径**与**登记裁决**查询，
//       使得淘汰后迟到/重放的 Confirmed/Rejected 与同 activation 的新登记仍不能“复活”。
//    7.1) 预留假弹→权威原地升级（R5-A3 返工新增）：TryPromoteReservedToAuthority 把
//       「收请求时 TryRegisterPredicted 预留的 ID」原地切成权威本体（不重分配 ID、不改 Origin、
//       权威 NetId 真实非 0、只有 Confirmed 可升级、幂等）；这是玩家弹能开始权威追赶的唯一路径。
//    8) 有界：登记表 ≤ 1024/类别、owner ≤ 64、(owner,origin) 流/水位 ≤ 128、激活账本 ≤ 512、
//       被淘汰终态记忆 ≤ 512、retired 标记环 ≤ 2048、待取释放队列 ≤ 1024（溢出丢最旧并计数，可观测）。
//       注意 TryRecordStop 会把墓碑时刻同步写进冻结 State.TombstoneUntilMs（与账本同一份真值），
//       否则按 State 判墓碑的消费方在「本地停止」路径上会静默失效。
//    9) finite 与身份：登记/镜像/推进的 position/velocity/yaw/moveTime 与 spec 数值必须有限；
//       state 自带 key 时必须与本次身份完全相等，否则 Invalid/InvalidPayload。
//   10) 运动推进：TryAdvanceMotion 是**唯一**可写运动状态的入口（冻结副本写不回去），
//       它只写运动面，不动身份/目标集合/停止/寿命账本。
// ============================================================================

using System;
using System.Collections.Generic;
using PMNet.Mover;

namespace PMNet.Projectile
{
    // ------------------------------------------------------------------ 墙钟

    /// <summary>墙钟校验结果。契约要求「非法/倒退明确拒绝」，不是一个静默 if。</summary>
    public enum PMProjectileClockCheck : byte
    {
        Ok = 0,
        NonFinite = 1,
        Regressed = 2,
    }

    /// <summary>
    /// finite 判据的唯一入口（与 <see cref="PMProjectileWallClock"/> 并列）。
    ///
    /// 契约「上行点 finite」在纯核心侧的落点：登记/镜像/推进/挂起载荷里的位置、速度、朝向、
    /// 时间与 spec 数值一律先过这里。**禁止**在别处再散写 `IsNaN / IsInfinity` 组合，
    /// 否则少量入口漏检就会让 NaN 进入冻结状态（NaN 会在下游几何里静默传播成「永不命中」）。
    /// </summary>
    internal static class PMProjectileFinite
    {
        public static bool F(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }

        public static bool D(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }

        public static bool V(PMVector3 v) { return F(v.X) && F(v.Y) && F(v.Z); }

        /// <summary>冻结状态里的「数值面」是否有限（null 视为空状态，合法）。</summary>
        public static bool State(PMProjectileState s)
        {
            if (s == null) { return true; }
            return V(s.SpawnPosition) && V(s.PreviousPosition) && V(s.Position) && V(s.Velocity)
                && F(s.Yaw) && D(s.MoveTimeMs)
                && D(s.StopWallTimeMs) && D(s.TimeAfterStoppedMs) && D(s.TombstoneUntilMs);
        }

        /// <summary>冻结规格里的「数值面」是否有限（null 视为默认规格，合法）。</summary>
        public static bool Spec(PMProjectileSpec s)
        {
            if (s == null) { return true; }
            return F(s.SpeedMps) && F(s.RadiusM);
        }
    }

    /// <summary>墙钟唯一判据（TTL / 墓碑 / 挂起时长共用）。**禁止**在别处再写一遍。</summary>
    public static class PMProjectileWallClock
    {
        /// <summary>非 finite 或小于上一次观测值时拒绝。相等视为合法（同一毫秒内多次调用是正常的）。</summary>
        public static PMProjectileClockCheck Validate(double wallNowMs, double lastWallMs)
        {
            if (double.IsNaN(wallNowMs) || double.IsInfinity(wallNowMs))
            {
                return PMProjectileClockCheck.NonFinite;
            }

            if (wallNowMs < lastWallMs)
            {
                return PMProjectileClockCheck.Regressed;
            }

            return PMProjectileClockCheck.Ok;
        }
    }

    // ------------------------------------------------------------ 容量与墓碑

    /// <summary>
    /// 生命周期层的自有上限。共享契约的 <see cref="PMProjectileLimits"/> 不包含
    /// 「激活账本 / retired 标记 / 待取释放队列」这三项（它们是本项目新增的记账面），
    /// 因此在这里显式给定，避免任何一处出现无界容器。
    /// </summary>
    public static class PMProjectileLifecycleLimits
    {
        /// <summary>激活账本容量（对应 UE 侧 512 环形缓存）。满时按插入序淘汰终态项。</summary>
        public const int MaxActivationLedger = 512;

        /// <summary>已回收 key 的终态标记环容量。让迟到镜像/Verify 判为 Retired 而不是「未知」。</summary>
        public const int MaxRetiredMarkers = 2048;

        /// <summary>待取释放队列容量（TTL/断连驱动的释放项）。溢出丢最旧并计数。</summary>
        public const int MaxPendingReleases = 1024;

        /// <summary>每颗弹的去重目标集合上限（共享契约 MaxTargets=100 的另一处引用点）。</summary>
        public const int MaxTargetsPerProjectile = PMProjectileLimits.MaxTargets;
    }

    /// <summary>墓碑窗口计算。契约公式：max(150, delayDestroyMs, clamp(2*predictionMs+100, 0, 1000))。</summary>
    public static class PMProjectileTombstone
    {
        public const int MinTombstoneMs = 150;
        public const int MaxGraveDelayMs = 1000;

        public static int GraveDelayMs(int predictionMs)
        {
            int v = 2 * predictionMs + PMProjectileLimits.TickBufferMs;
            if (v < 0) { v = 0; }
            if (v > MaxGraveDelayMs) { v = MaxGraveDelayMs; }
            return v;
        }

        public static int ComputeTombstoneMs(int delayDestroyMs, int predictionMs)
        {
            int t = MinTombstoneMs;
            if (delayDestroyMs > t) { t = delayDestroyMs; }
            int grave = GraveDelayMs(predictionMs);
            if (grave > t) { t = grave; }
            return t;
        }
    }

    // ------------------------------------------------------------------ 枚举

    /// <summary>登记入口的可信度。权威弹只能从 AuthorityServerDirect 入口产生。</summary>
    public enum PMProjectileTrust : byte
    {
        ClientRequest = 0,
        AuthorityServerDirect = 1,
    }

    public enum PMProjectileRegisterResult : byte
    {
        Registered = 0,
        DuplicateId = 1,
        NonMonotonicId = 2,
        StaleEpoch = 3,
        OwnerCapacity = 4,
        RegistryCapacity = 5,
        InvalidKey = 6,
        UntrustedAuthority = 7,
        InvalidActivationId = 8,
        RevokedRejected = 9,
        InvalidClock = 10,
        InvalidPayload = 11,
    }

    public enum PMProjectileTakeoverResult : byte
    {
        TakenOver = 0,
        AlreadyTakenOver = 1,
        NoPredicted = 2,
        PredictedNotAlive = 3,
        UnknownKey = 4,
        StaleEpoch = 5,
        Retired = 6,
        Invalid = 7,
    }

    public enum PMProjectileMirrorResult : byte
    {
        Applied = 0,
        GateFakeAlive = 1,
        HiddenNoRevive = 2,
        UnknownKey = 3,
        StaleEpoch = 4,
        Retired = 5,
        Invalid = 6,
        InvalidClock = 7,

        /// <summary>
        /// 已停止（本地停止 / 镜像带来的停止 / 假弹已结束）后的镜像：停止对**运动**是终态，
        /// 镜像不得再写 Position/PreviousPosition/Velocity/Yaw/MoveTimeMs，也不产生二次停止事件。
        /// 调用方必须视 <c>PositionApplied=false</c>（视觉上保持停止位置）。
        /// </summary>
        StoppedNotMoved = 8,
    }

    public enum PMProjectileStopResult : byte
    {
        Recorded = 0,
        AlreadyStopped = 1,
        UnknownKey = 2,
        StaleEpoch = 3,
        Retired = 4,
        InvalidClock = 5,
        Invalid = 6,
    }

    public enum PMProjectileCatchUpResult : byte
    {
        Started = 0,
        NotRegistered = 1,
        AlreadyStarted = 2,
        NotAuthority = 3,
        StaleEpoch = 4,
        Retired = 5,
        InvalidClock = 6,
        Invalid = 7,
    }

    /// <summary>
    /// 「请求时预留的预测登记」**原地**升级为权威本体的结果（R5-A3 返工新增）。
    ///
    /// 为什么需要它：客户端预测弹在收请求时就必须预留 ProjectileId（否则同一 activation 的多弹
    /// 乱序解挂会被 ID 高水位误拒），但「预留的假弹」在 `TryBeginCatchUp` 眼里是 NotAuthority
    /// ⇒ 权威确认后**永远无法开始权威追赶**（玩家弹不能追赶 = 完全没有延迟补偿）。
    /// 升级必须是**原地**的：不重新分配 ID、不改 Origin（ID 空间按 (owner, origin) 分域）、
    /// 不改 Key，只把这条登记从「预测」切到「权威」。
    /// </summary>
    public enum PMProjectilePromoteResult : byte
    {
        /// <summary>本次完成升级（Predicted → 权威，权威 NetId 已盖章）。</summary>
        Promoted = 0,

        /// <summary>同一 key 已经升级过（幂等：不重复计数、不重复盖章）。</summary>
        AlreadyPromoted = 1,

        /// <summary>权威原生登记（TryRegisterAuthority），本来就不是预留假弹。</summary>
        NotPredicted = 2,

        /// <summary>activation 仍 Pending / Rejected：**只有 Confirmed 可升级**。</summary>
        NotConfirmed = 3,

        UnknownKey = 4,
        StaleEpoch = 5,
        Retired = 6,

        /// <summary>可信权威 NetId 为空（0）。</summary>
        InvalidAuthority = 7,
        InvalidClock = 8,

        /// <summary>权威类别容量已满（1024/类别），升级被拒。</summary>
        Capacity = 9,
    }

    public struct PMProjectilePromoteOutcome
    {
        public PMProjectilePromoteResult Result;
        public PMProjectileKey Key;

        /// <summary>升级后登记项上的权威 NetId（Promoted / AlreadyPromoted 时为真实非 0 值）。</summary>
        public uint AuthorityNetId;

        public PMProjectileClockCheck Clock;

        public bool IsPromoted { get { return Result == PMProjectilePromoteResult.Promoted; } }
    }

    /// <summary>假弹提前结束的记账结果。</summary>
    public enum PMProjectilePredictedEndResult : byte
    {
        Recorded = 0,
        AlreadyEnded = 1,
        TakenOver = 2,
        NotPredicted = 3,
        UnknownKey = 4,
        StaleEpoch = 5,
        InvalidClock = 6,
        Invalid = 7,
    }

    public enum PMProjectileVerifyAdmission : byte
    {
        Allowed = 0,
        AllowedInTombstone = 1,
        TombstoneExpired = 2,
        UnknownKey = 3,
        StaleEpoch = 4,
        Retired = 5,
        InvalidClock = 6,
        Invalid = 7,
    }

    /// <summary>激活账本写入结果。终态不倒退 ⇒ AlreadyTerminal 是幂等成功语义（不重复释放）。</summary>
    public enum PMActivationLedgerResult : byte
    {
        Applied = 0,
        RecordedPending = 1,
        AlreadyTerminal = 2,
        LedgerFull = 3,
        InvalidActivationId = 4,
        InvalidOutcome = 5,
        InvalidClock = 6,
    }

    /// <summary>释放项产生的原因。调用方据此决定撤销/接管/仅记录。</summary>
    public enum PMProjectileReleaseReason : byte
    {
        ActivationConfirmed = 0,
        ActivationRejected = 1,
        TombstoneExpired = 2,
        OwnerCleared = 3,
    }

    /// <summary>已登记投弹的运动推进结果（A3 宿主 / 共享轨迹模型专用）。</summary>
    public enum PMProjectileMotionResult : byte
    {
        Advanced = 0,

        /// <summary>运动已是终态（已停止 / 假弹已结束）：推进被拒，位置保持不动。</summary>
        NotMovable = 1,

        UnknownKey = 2,
        StaleEpoch = 3,
        Retired = 4,

        /// <summary>入参非 finite。</summary>
        Invalid = 5,
    }

    /// <summary>
    /// 运动推进结果。**不引回调**：写入发生在返回之前，返回值只是回显本次生效的运动量。
    /// </summary>
    public struct PMProjectileMotionOutcome
    {
        public PMProjectileMotionResult Result;
        public PMProjectileKey Key;

        /// <summary>生效后的位置（NotMovable 时为当前冻结位置，便于调用方拉回表现层）。</summary>
        public PMVector3 Position;

        public PMVector3 Velocity;
        public double MoveTimeMs;

        public bool IsAdvanced { get { return Result == PMProjectileMotionResult.Advanced; } }
    }

    // --------------------------------------------------------------- 释放项

    /// <summary>
    /// 一条**独占可消费**的释放项。返回后调用方拥有它，可自由改写（全部字段是值类型/拷贝）。
    /// </summary>
    public struct PMProjectileActivationRelease
    {
        public PMProjectileKey Key;
        public uint OwnerNetId;
        public uint ActivationId;
        public PMProjectileOrigin Origin;
        public uint AuthorityNetId;
        public PMActivationResult Outcome;
        public PMProjectileReleaseReason Reason;

        /// <summary>true = 调用方必须**实际撤销**本地的预测实例（Rejected 与清理路径）。</summary>
        public bool RemovedInstance;

        /// <summary>挂起时长（毫秒）。挂起期不得生成幽灵弹；该值须叠加进追赶/校验窗口。</summary>
        public double PendingElapsedMs;

        public double WallTimeMs;
    }

    // ------------------------------------------------------------ 对外快照

    /// <summary>
    /// 登记项的**冻结副本**。所有对外读取都返回它的深 clone，调用方改写不会影响内部状态。
    /// </summary>
    public sealed class PMProjectileRegistration
    {
        public PMProjectileKey Key;
        public uint AuthorityNetId;
        public uint ActivationId;
        public PMActivationResult Activation;
        public PMProjectileSpec Spec = new PMProjectileSpec();
        public PMProjectileState State = new PMProjectileState();

        public bool Predicted;
        public bool PredictedAlive;
        public bool PredictedEnded;
        public bool TakenOver;
        public bool Stopped;
        public bool Hidden;
        public bool CatchUpStarted;

        /// <summary>true = 由预留假弹原地升级为权威（见 <see cref="PMProjectilePromoteResult"/>）。</summary>
        public bool Promoted;

        public double RegisteredWallTimeMs;
        public double StopWallTimeMs;
        public double TimeAfterStoppedMs;
        public double TombstoneUntilMs;
        public double LastMirrorWallTimeMs;

        public uint[] AllowedTargets = new uint[0];
        public uint[] HitTargets = new uint[0];

        public PMProjectileRegistration Clone()
        {
            PMProjectileRegistration copy = (PMProjectileRegistration)MemberwiseClone();
            copy.Spec = Spec == null ? new PMProjectileSpec() : Spec.Clone();
            copy.State = State == null ? new PMProjectileState() : State.Clone();
            copy.AllowedTargets = AllowedTargets == null ? new uint[0] : (uint[])AllowedTargets.Clone();
            copy.HitTargets = HitTargets == null ? new uint[0] : (uint[])HitTargets.Clone();
            return copy;
        }
    }

    public struct PMProjectileRegisterOutcome
    {
        public PMProjectileRegisterResult Result;
        public PMProjectileClockCheck Clock;
        public PMProjectileKey Key;
        public uint AuthorityNetId;
        public uint ActivationId;
        public PMActivationResult Activation;

        /// <summary>本次调用**独占**产出的释放项（可能为空数组，永不为 null）。</summary>
        public PMProjectileActivationRelease[] Releases;

        public bool IsRegistered { get { return Result == PMProjectileRegisterResult.Registered; } }
    }

    public struct PMProjectileTakeoverOutcome
    {
        public PMProjectileTakeoverResult Result;
        public PMProjectileKey Key;
        public uint AuthorityNetId;

        /// <summary>被消费的假弹冻结副本（一次消费；第二次为 null）。</summary>
        public PMProjectileSpec Spec;
        public PMProjectileState State;
    }

    public struct PMProjectileMirrorOutcome
    {
        public PMProjectileMirrorResult Result;
        public PMProjectileKey Key;

        /// <summary>镜像位置/速度是否写入了登记项（位置闸门为假时才为 true）。</summary>
        public bool PositionApplied;

        /// <summary>true = 调用方必须隐藏但**不得**发出停止通知（假弹已结束的迟到镜像）。</summary>
        public bool HideWithoutStopEvent;

        public PMProjectileClockCheck Clock;
        public PMProjectileRegistration Snapshot;
    }

    public struct PMProjectileStopOutcome
    {
        public PMProjectileStopResult Result;
        public PMProjectileKey Key;
        public double TombstoneUntilMs;

        /// <summary>true = 本次调用真正产生了一次停止（重复停止不产生事件）。</summary>
        public bool StopEventEmitted;

        public PMProjectileClockCheck Clock;
    }

    public struct PMProjectileCatchUpOutcome
    {
        public PMProjectileCatchUpResult Result;
        public PMProjectileKey Key;
        public uint AuthorityNetId;
        public PMProjectileSpec Spec;
        public double RegistrationWallTimeMs;
    }

    public struct PMProjectileVerifyAdmissionOutcome
    {
        public PMProjectileVerifyAdmission Result;
        public PMProjectileKey Key;
        public double TombstoneRemainingMs;
        public PMProjectileClockCheck Clock;
    }

    public struct PMProjectilePredictedEndOutcome
    {
        public PMProjectilePredictedEndResult Result;
        public PMProjectileKey Key;
        public int TombstoneMs;
        public PMProjectileClockCheck Clock;
    }

    // ------------------------------------------------------- 内部字典键

    internal struct PMProjectileStreamKey : IEquatable<PMProjectileStreamKey>
    {
        public readonly uint OwnerNetId;
        public readonly PMProjectileOrigin Origin;

        public PMProjectileStreamKey(uint ownerNetId, PMProjectileOrigin origin)
        {
            OwnerNetId = ownerNetId;
            Origin = origin;
        }

        public bool Equals(PMProjectileStreamKey other)
        {
            return OwnerNetId == other.OwnerNetId && Origin == other.Origin;
        }

        public override bool Equals(object obj)
        {
            return obj is PMProjectileStreamKey && Equals((PMProjectileStreamKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked { return ((int)OwnerNetId * 397) ^ (int)Origin; }
        }
    }

    internal struct PMActivationKey : IEquatable<PMActivationKey>
    {
        public readonly uint OwnerNetId;
        public readonly uint ActivationId;

        public PMActivationKey(uint ownerNetId, uint activationId)
        {
            OwnerNetId = ownerNetId;
            ActivationId = activationId;
        }

        public bool Equals(PMActivationKey other)
        {
            return OwnerNetId == other.OwnerNetId && ActivationId == other.ActivationId;
        }

        public override bool Equals(object obj)
        {
            return obj is PMActivationKey && Equals((PMActivationKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked { return ((int)OwnerNetId * 397) ^ (int)ActivationId; }
        }
    }

    // ------------------------------------------------------------ 主体

    /// <summary>
    /// 投射物生命周期账本（每个 DS 会话/每个 epoch 一份）。
    ///
    /// 本类**不做**任何几何、历史或命中判定；它回答四个问题：
    ///   · 这个 key 现在是哪一类对象（预测假弹 / 权威镜像），它的冻结状态是什么；
    ///   · 这个 key 归谁、ID 有没有复用、还要不要等激活；
    ///   · 镜像到了能不能写位置、要不要接管、要不要「隐藏但不二次停止」；
    ///   · 停止之后墓碑窗口还开不开、过期清理有没有把白名单/命中集合一起清掉。
    /// </summary>
    public sealed class PMProjectileLifecycle
    {
        private sealed class Entry
        {
            public PMProjectileKey Key;
            public uint AuthorityNetId;
            public uint ActivationId;
            public PMActivationResult Activation;
            public PMProjectileSpec Spec;
            public PMProjectileState State;
            public bool Predicted;
            public bool PredictedAlive;
            public bool PredictedEnded;
            public bool TakenOver;
            public bool Stopped;
            public bool Hidden;
            public bool CatchUpStarted;

            /// <summary>
            /// 这条登记是否由「请求时预留的预测弹」原地升级为权威（TryPromoteReservedToAuthority）。
            /// Predicted=false 有两种来路（权威原生登记 / 预留升级），没有这个标记就无法做到升级幂等。
            /// </summary>
            public bool Promoted;

            public double RegisteredWallTimeMs;
            public double StopWallTimeMs;
            public double TimeAfterStoppedMs;
            public double TombstoneUntilMs;
            public double LastMirrorWallTimeMs;
            public List<uint> AllowedTargets = new List<uint>();
            public List<uint> HitTargets = new List<uint>();

            /// <summary>
            /// 冻结状态的**唯一读取口径**：HitTargets / AllowedTargets 一律由两份 List 派生，
            /// 不再从 State 里的同名数组读。否则「List 一份真值 + State 数组另一份真值」会在
            /// TryAddHitTarget / TrySetAllowedTargets 之后立刻不一致（数组那份会静默变陈旧）。
            /// </summary>
            public PMProjectileState StateCopy()
            {
                PMProjectileState c = State == null ? new PMProjectileState() : State.Clone();
                c.HitTargets = HitTargets.Count == 0 ? new uint[0] : HitTargets.ToArray();
                c.AllowedTargets = AllowedTargets.Count == 0 ? new uint[0] : AllowedTargets.ToArray();
                return c;
            }

            public PMProjectileRegistration Snapshot()
            {
                PMProjectileRegistration s = new PMProjectileRegistration();
                s.Key = Key;
                s.AuthorityNetId = AuthorityNetId;
                s.ActivationId = ActivationId;
                s.Activation = Activation;
                s.Spec = Spec == null ? new PMProjectileSpec() : Spec.Clone();
                s.State = StateCopy();
                s.Predicted = Predicted;
                s.PredictedAlive = PredictedAlive;
                s.PredictedEnded = PredictedEnded;
                s.TakenOver = TakenOver;
                s.Stopped = Stopped;
                s.Hidden = Hidden;
                s.CatchUpStarted = CatchUpStarted;
                s.Promoted = Promoted;
                s.RegisteredWallTimeMs = RegisteredWallTimeMs;
                s.StopWallTimeMs = StopWallTimeMs;
                s.TimeAfterStoppedMs = TimeAfterStoppedMs;
                s.TombstoneUntilMs = TombstoneUntilMs;
                s.LastMirrorWallTimeMs = LastMirrorWallTimeMs;
                s.AllowedTargets = AllowedTargets.Count == 0 ? new uint[0] : AllowedTargets.ToArray();
                s.HitTargets = HitTargets.Count == 0 ? new uint[0] : HitTargets.ToArray();
                return s;
            }
        }

        private sealed class LedgerEntry
        {
            public PMActivationKey Key;
            public PMActivationResult Outcome;
            public double WallTimeMs;
        }

        private readonly Dictionary<PMProjectileKey, Entry> _entries = new Dictionary<PMProjectileKey, Entry>();
        private readonly Dictionary<PMProjectileStreamKey, uint> _watermarks = new Dictionary<PMProjectileStreamKey, uint>();
        private readonly Dictionary<uint, int> _ownerCounts = new Dictionary<uint, int>();
        private readonly Dictionary<PMActivationKey, List<PMProjectileKey>> _activationWaiters =
            new Dictionary<PMActivationKey, List<PMProjectileKey>>();
        private readonly List<LedgerEntry> _ledger = new List<LedgerEntry>();
        private readonly Dictionary<PMActivationKey, LedgerEntry> _ledgerIndex =
            new Dictionary<PMActivationKey, LedgerEntry>();
        private readonly Queue<PMProjectileKey> _retiredRing = new Queue<PMProjectileKey>();
        private readonly HashSet<PMProjectileKey> _retiredSet = new HashSet<PMProjectileKey>();

        /// <summary>
        /// 被容量淘汰的**终态激活判决**的有界记忆（插入序环）。
        ///
        /// 为什么必须有：激活账本只 512，满时按插入序淘汰终态项。淘汰后若只靠 _ledgerIndex，
        /// 一条迟到（或重放）的 Confirmed/Rejected 会在同一 (owner, activationId) 上**新建**一条
        /// 账本项 —— 已 Rejected 的 activation 就此“复活”（之后的登记会被当成 Confirmed 直接生成），
        /// 已 Confirmed 的也可能被反向覆盖。这里把被淘汰的终态判决留在环里，且只被**写路径**与
        /// **登记裁决**查询（不改变 TryGetActivationResult 的「账本可查性」语义），使「终态不倒退」
        /// 在淘汰后仍然成立。有界是必然的：超出环容量后只能遗忘，由 EvictedTerminalLedgerCount 与
        /// TerminalVerdictMemoryCount 可观测，不假装「永远记得」。
        /// </summary>
        private readonly Queue<PMActivationKey> _terminalRing = new Queue<PMActivationKey>();
        private readonly Dictionary<PMActivationKey, PMActivationResult> _terminalMemory =
            new Dictionary<PMActivationKey, PMActivationResult>();

        private readonly List<PMProjectileActivationRelease> _pendingReleases =
            new List<PMProjectileActivationRelease>();

        private uint _epoch;
        private int _authorityCount;
        private int _predictedCount;
        private double _lastWallMs;
        private int _droppedReleaseCount;
        private int _evictedTerminalLedgerCount;

        public PMProjectileLifecycle(uint epoch)
        {
            if (epoch == 0u)
            {
                throw new ArgumentOutOfRangeException("epoch", "[PMProjectileLifecycle] epoch 0 无效（PMProjectileKey.IsValid 要求非 0）。");
            }

            _epoch = epoch;
        }

        // -------------------------------------------------------------- 只读

        public uint Epoch { get { return _epoch; } }

        public int RegistrationCount { get { return _entries.Count; } }

        public int AuthorityCount { get { return _authorityCount; } }

        public int PredictedCount { get { return _predictedCount; } }

        public int OwnerCount { get { return _ownerCounts.Count; } }

        public int ActivationLedgerCount { get { return _ledger.Count; } }

        public int PendingReleaseCount { get { return _pendingReleases.Count; } }

        public int RetiredMarkerCount { get { return _retiredSet.Count; } }

        public int DroppedReleaseCount { get { return _droppedReleaseCount; } }

        /// <summary>
        /// 因账本满而淘汰掉的**终态**条目数（累计、可观测）。
        ///
        /// 这是「有界记忆」的诚实处：账本只 512，淘汰后该 activation 的终态判决不再可查。
        /// 它**不会**造成 key 复活（复活由每 (owner, origin) 高水位拦住，与账本无关），
        /// 但会让此后同一 activation 上登记的**新**弹被当成 Pending 而不是 Confirmed/Rejected。
        /// 这个计数就是让该退化可被监控，而不是静默。
        /// </summary>
        public int EvictedTerminalLedgerCount { get { return _evictedTerminalLedgerCount; } }

        /// <summary>被淘汰但仍被记住的终态激活判决数（≤ MaxActivationLedger，可观测的「有界记忆」占用）。</summary>
        public int TerminalVerdictMemoryCount { get { return _terminalMemory.Count; } }

        public double LastWallTimeMs { get { return _lastWallMs; } }

        /// <summary>位置闸门（**只读、不消费**）。为真时镜像不得写位置/速度/运动时间。</summary>
        public bool IsPredictedAlive(PMProjectileKey key)
        {
            Entry e;
            if (!_entries.TryGetValue(key, out e)) { return false; }
            return e.Predicted && e.PredictedAlive && !e.TakenOver && !e.Stopped;
        }

        public bool IsRegistered(PMProjectileKey key)
        {
            return _entries.ContainsKey(key);
        }

        /// <summary>迟到的 key 是否落在「已回收」终态标记环内（终态不倒退的判据）。</summary>
        public bool IsRetired(PMProjectileKey key)
        {
            return _retiredSet.Contains(key);
        }

        public bool TryGetRegistration(PMProjectileKey key, out PMProjectileRegistration snapshot)
        {
            Entry e;
            if (!_entries.TryGetValue(key, out e))
            {
                snapshot = null;
                return false;
            }

            snapshot = e.Snapshot();
            return true;
        }

        /// <summary>深 clone 出冻结的 Spec/State。</summary>
        public bool TryGetFrozen(PMProjectileKey key, out PMProjectileSpec spec, out PMProjectileState state)
        {
            Entry e;
            if (!_entries.TryGetValue(key, out e))
            {
                spec = null;
                state = null;
                return false;
            }

            spec = e.Spec == null ? new PMProjectileSpec() : e.Spec.Clone();
            state = e.StateCopy();
            return true;
        }

        public bool TryGetActivationResult(uint ownerNetId, uint activationId, out PMActivationResult outcome)
        {
            LedgerEntry le;
            if (_ledgerIndex.TryGetValue(new PMActivationKey(ownerNetId, activationId), out le))
            {
                outcome = le.Outcome;
                return true;
            }

            outcome = PMActivationResult.Pending;
            return false;
        }

        /// <summary>
        /// 登记裁决 / 写账本用的激活判决查询：先查**在册账本**，再查**被淘汰终态的环**。
        /// 与 <see cref="TryGetActivationResult"/> 的区别是后者只反映「账本里还查得到吗」；
        /// 这里回答的是「这个 activation 的终态判决是什么」——用于拦住被淘汰后的倒退/复活写入。
        /// </summary>
        private bool TryGetActivationVerdict(uint ownerNetId, uint activationId, out PMActivationResult outcome)
        {
            LedgerEntry le;
            if (_ledgerIndex.TryGetValue(new PMActivationKey(ownerNetId, activationId), out le))
            {
                outcome = le.Outcome;
                return true;
            }

            PMActivationResult remembered;
            if (_terminalMemory.TryGetValue(new PMActivationKey(ownerNetId, activationId), out remembered))
            {
                outcome = remembered;
                return true;
            }

            outcome = PMActivationResult.Pending;
            return false;
        }

        public int HitTargetCount(PMProjectileKey key)
        {
            Entry e;
            if (!_entries.TryGetValue(key, out e)) { return 0; }
            return e.HitTargets.Count;
        }

        public int AllowedTargetCount(PMProjectileKey key)
        {
            Entry e;
            if (!_entries.TryGetValue(key, out e)) { return 0; }
            return e.AllowedTargets.Count;
        }

        public bool ContainsHitTarget(PMProjectileKey key, uint targetNetId)
        {
            Entry e;
            if (!_entries.TryGetValue(key, out e)) { return false; }
            return e.HitTargets.Contains(targetNetId);
        }

        public bool IsTargetAllowed(PMProjectileKey key, uint targetNetId)
        {
            Entry e;
            if (!_entries.TryGetValue(key, out e)) { return false; }

            // 白名单为空 = 不限制（与 UE 的「无白名单即全放行」同口径）。
            if (e.AllowedTargets.Count == 0) { return true; }
            return e.AllowedTargets.Contains(targetNetId);
        }

        // ------------------------------------------------------------ 写入

        /// <summary>每条命中记录一次（去重、上限 100）。返回 false = 已存在、未知 key 或已满。</summary>
        public bool TryAddHitTarget(PMProjectileKey key, uint targetNetId)
        {
            Entry e;
            if (!_entries.TryGetValue(key, out e)) { return false; }
            if (targetNetId == 0u) { return false; }
            if (e.HitTargets.Contains(targetNetId)) { return false; }
            if (e.HitTargets.Count >= PMProjectileLifecycleLimits.MaxTargetsPerProjectile) { return false; }
            e.HitTargets.Add(targetNetId);
            return true;
        }

        /// <summary>设置运行时白名单（深拷贝、去重、上限 100）。空数组 = 清空 = 不限制。</summary>
        public bool TrySetAllowedTargets(PMProjectileKey key, uint[] allowedTargets)
        {
            Entry e;
            if (!_entries.TryGetValue(key, out e)) { return false; }

            e.AllowedTargets.Clear();
            if (allowedTargets != null)
            {
                for (int i = 0; i < allowedTargets.Length; i++)
                {
                    uint t = allowedTargets[i];
                    if (t == 0u || e.AllowedTargets.Contains(t)) { continue; }
                    if (e.AllowedTargets.Count >= PMProjectileLifecycleLimits.MaxTargetsPerProjectile) { break; }
                    e.AllowedTargets.Add(t);
                }
            }

            return true;
        }

        // -------------------------------------------------------- 登记入口

        /// <summary>
        /// 权威登记入口（ServerDirect 或权威镜像）。**唯一**能产生 Origin=ServerDirect 的入口。
        ///
        /// 顺序（对应契约「注册必须先于追赶」）：登记 → TryBeginCatchUp → 追赶。
        /// 重复登记同一 key 一律 DuplicateId（不可重入），因此外部无法「重复注册再追赶」。
        /// </summary>
        public PMProjectileRegisterOutcome TryRegisterAuthority(
            PMProjectileTrust trust,
            uint ownerNetId,
            uint projectileId,
            uint authorityNetId,
            uint activationId,
            PMProjectileState state,
            PMProjectileSpec spec,
            double wallNowMs)
        {
            PMProjectileRegisterOutcome outcome = NewRegisterOutcome();
            PMProjectileClockCheck clock = PMProjectileWallClock.Validate(wallNowMs, _lastWallMs);
            outcome.Clock = clock;
            if (clock != PMProjectileClockCheck.Ok)
            {
                outcome.Result = PMProjectileRegisterResult.InvalidClock;
                return outcome;
            }

            if (trust != PMProjectileTrust.AuthorityServerDirect)
            {
                outcome.Result = PMProjectileRegisterResult.UntrustedAuthority;
                return outcome;
            }

            PMProjectileKey key = new PMProjectileKey(_epoch, ownerNetId, projectileId, PMProjectileOrigin.ServerDirect);
            outcome.Key = key;
            outcome.AuthorityNetId = authorityNetId;

            if (authorityNetId == 0u || !key.IsValid)
            {
                outcome.Result = PMProjectileRegisterResult.InvalidKey;
                return outcome;
            }

            // 权威弹按构造即 Confirmed；但若该 activation 已被权威裁决为 Rejected，
            // 则「终态不倒退」优先：拒绝登记（而不是把它变成 Confirmed）。
            PMActivationResult activation = PMActivationResult.Confirmed;
            if (activationId != 0u)
            {
                PMActivationResult ledgerOutcome;
                if (TryGetActivationVerdict(ownerNetId, activationId, out ledgerOutcome)
                    && ledgerOutcome == PMActivationResult.Rejected)
                {
                    outcome.Result = PMProjectileRegisterResult.RevokedRejected;
                    outcome.Activation = PMActivationResult.Rejected;
                    outcome.ActivationId = activationId;
                    // RemovedInstance=false：本路径**没有**登进任何实例（登记被拒），
                    // 所以不存在「要撤销的假弹」。报 true 会让调用方去销毁一个从未创建的实例
                    // （或误伤同 key 的其他对象）。
                    outcome.Releases = MakeReleases(key, ownerNetId, activationId,
                        PMProjectileReleaseReason.ActivationRejected, false, 0.0, wallNowMs);
                    return outcome;
                }

                if (ledgerOutcome == PMActivationResult.Confirmed)
                {
                    activation = PMActivationResult.Confirmed;
                }
            }

            outcome.Activation = activation;
            outcome.ActivationId = activationId;
            PMProjectileRegisterResult failure;
            Entry entry = CreateEntry(key, authorityNetId, activationId, activation, state, spec, false, wallNowMs, out failure);
            if (entry == null)
            {
                outcome.Result = failure;
                return outcome;
            }

            outcome.Result = PMProjectileRegisterResult.Registered;
            AcceptWallClock(wallNowMs);
            return outcome;
        }

        /// <summary>
        /// 客户端预测登记入口（假弹）。ActivationId 必须非 0（0 只允许可信 ServerDirect）；
        /// 非 0 时由激活账本裁决：Confirmed 立即登记并解挂、Rejected 拒绝并撤销、否则挂起等待。
        /// </summary>
        public PMProjectileRegisterOutcome TryRegisterPredicted(
            uint ownerNetId,
            uint projectileId,
            uint activationId,
            PMProjectileState state,
            PMProjectileSpec spec,
            double wallNowMs)
        {
            PMProjectileRegisterOutcome outcome = NewRegisterOutcome();
            PMProjectileClockCheck clock = PMProjectileWallClock.Validate(wallNowMs, _lastWallMs);
            outcome.Clock = clock;
            if (clock != PMProjectileClockCheck.Ok)
            {
                outcome.Result = PMProjectileRegisterResult.InvalidClock;
                return outcome;
            }

            if (activationId == 0u)
            {
                outcome.Result = PMProjectileRegisterResult.InvalidActivationId;
                return outcome;
            }

            PMProjectileKey key = new PMProjectileKey(_epoch, ownerNetId, projectileId, PMProjectileOrigin.ClientPredicted);
            outcome.Key = key;
            if (!key.IsValid)
            {
                outcome.Result = PMProjectileRegisterResult.InvalidKey;
                return outcome;
            }

            PMActivationResult activation = PMActivationResult.Pending;
            PMActivationResult ledgerOutcome;
            if (TryGetActivationVerdict(ownerNetId, activationId, out ledgerOutcome))
            {
                activation = ledgerOutcome;
            }

            outcome.ActivationId = activationId;
            if (activation == PMActivationResult.Rejected)
            {
                outcome.Result = PMProjectileRegisterResult.RevokedRejected;
                outcome.Activation = PMActivationResult.Rejected;
                // 同 TryRegisterAuthority：登记根本没落地，没有可撤销的实例。
                outcome.Releases = MakeReleases(key, ownerNetId, activationId,
                    PMProjectileReleaseReason.ActivationRejected, false, 0.0, wallNowMs);
                return outcome;
            }

            outcome.Activation = activation;
            PMProjectileRegisterResult failure;
            Entry entry = CreateEntry(key, 0u, activationId, activation, state, spec, true, wallNowMs, out failure);
            if (entry == null)
            {
                outcome.Result = failure;
                return outcome;
            }

            if (activation == PMActivationResult.Pending)
            {
                // 挂起激活：记入等待表，**不生成任何幽灵子弹**（生成由调用方在解挂后做）。
                PMActivationKey ak = new PMActivationKey(ownerNetId, activationId);
                List<PMProjectileKey> waiters;
                if (!_activationWaiters.TryGetValue(ak, out waiters))
                {
                    waiters = new List<PMProjectileKey>();
                    _activationWaiters.Add(ak, waiters);
                }

                waiters.Add(key);
            }

            outcome.Result = PMProjectileRegisterResult.Registered;
            AcceptWallClock(wallNowMs);
            return outcome;
        }

        private PMProjectileRegisterOutcome NewRegisterOutcome()
        {
            PMProjectileRegisterOutcome o = new PMProjectileRegisterOutcome();
            o.Releases = new PMProjectileActivationRelease[0];
            o.Clock = PMProjectileClockCheck.Ok;
            return o;
        }

        private Entry CreateEntry(PMProjectileKey key, uint authorityNetId, uint activationId,
            PMActivationResult activation, PMProjectileState state, PMProjectileSpec spec,
            bool predicted, double wallNowMs, out PMProjectileRegisterResult failure)
        {
            if (_entries.ContainsKey(key))
            {
                failure = PMProjectileRegisterResult.DuplicateId;
                return null;
            }

            PMProjectileStreamKey stream = new PMProjectileStreamKey(key.OwnerNetId, key.Origin);
            uint watermark;
            bool knownStream = _watermarks.TryGetValue(stream, out watermark);
            if (knownStream && key.ProjectileId <= watermark)
            {
                failure = PMProjectileRegisterResult.NonMonotonicId;
                return null;
            }

            // 每 (owner, origin) 高水位是「ID 一旦用过永不复用」的**唯一**记忆：Retire /
            // ClearOwner / 终态标记淘汰都不能把它丢掉（否则旧 key 可复活）。
            // 这份「永不复用」记忆自身也必须有界：(owner, origin) 流数上限 = MaxOwners × 2。
            // 超过后拒新 owner 的流（ID 不再可能被复用，因为该流从未登记过）。
            if (!knownStream && _watermarks.Count >= PMProjectileLimits.MaxOwners * 2)
            {
                failure = PMProjectileRegisterResult.OwnerCapacity;
                return null;
            }

            if (!_ownerCounts.ContainsKey(key.OwnerNetId) && _ownerCounts.Count >= PMProjectileLimits.MaxOwners)
            {
                failure = PMProjectileRegisterResult.OwnerCapacity;
                return null;
            }

            int categoryCount = predicted ? _predictedCount : _authorityCount;
            if (categoryCount >= PMProjectileLimits.MaxProjectiles)
            {
                failure = PMProjectileRegisterResult.RegistryCapacity;
                return null;
            }

            // 身份一致性：state 若自带 key，必须与本次登记身份完全相等；否则拒绝。
            // （否则 A 弹的冻结状态能写进 B 弹的登记项 —— 几何/命中都会错对象。）
            if (state != null && state.Key.IsValid && !state.Key.Equals(key))
            {
                failure = PMProjectileRegisterResult.InvalidPayload;
                return null;
            }

            // finite：位置/速度/朝向/时间与 spec 数值必须有限。
            if (!PMProjectileFinite.State(state) || !PMProjectileFinite.Spec(spec))
            {
                failure = PMProjectileRegisterResult.InvalidPayload;
                return null;
            }

            Entry e = new Entry();
            e.Key = key;
            e.AuthorityNetId = authorityNetId;
            e.ActivationId = activationId;
            e.Activation = activation;
            e.Predicted = predicted;
            e.PredictedAlive = predicted;
            e.Spec = spec == null ? new PMProjectileSpec() : spec.Clone();
            e.State = state == null ? new PMProjectileState() : state.Clone();

            // 登记 = 新实例。身份字段由登记参数盖章（client/server 域），
            // 终态/寿命字段一律归零：否则入参会把冻结状态钉成与账本标志矛盾的组合
            // （例如 State.Stopped=true 但 e.Stopped=false，会让「停止保护」读到两个真值）。
            e.State.Key = key;
            e.State.AuthorityNetId = authorityNetId;
            e.State.ActivationId = activationId;
            e.State.Stopped = false;
            e.State.Hidden = false;
            e.State.TakenOver = false;
            e.State.StopWallTimeMs = 0.0;
            e.State.TimeAfterStoppedMs = 0.0;
            e.State.TombstoneUntilMs = 0.0;

            // 目标集合的两个真值就是 Entry 上的两份 List；入参若已带（迟到登记）则先播种。
            SeedTargets(e, state == null ? null : state.HitTargets, state == null ? null : state.AllowedTargets);
            e.RegisteredWallTimeMs = wallNowMs;

            _entries.Add(key, e);
            _watermarks[stream] = key.ProjectileId;
            int ownerLive;
            _ownerCounts.TryGetValue(key.OwnerNetId, out ownerLive);
            _ownerCounts[key.OwnerNetId] = ownerLive + 1;
            if (predicted) { _predictedCount++; } else { _authorityCount++; }

            failure = PMProjectileRegisterResult.Registered;
            return e;
        }

        private static void SeedTargets(Entry e, uint[] hitTargets, uint[] allowedTargets)
        {
            if (hitTargets != null)
            {
                for (int i = 0; i < hitTargets.Length; i++)
                {
                    uint t = hitTargets[i];
                    if (t == 0u || e.HitTargets.Contains(t)) { continue; }
                    if (e.HitTargets.Count >= PMProjectileLifecycleLimits.MaxTargetsPerProjectile) { break; }
                    e.HitTargets.Add(t);
                }
            }

            if (allowedTargets != null)
            {
                for (int i = 0; i < allowedTargets.Length; i++)
                {
                    uint t = allowedTargets[i];
                    if (t == 0u || e.AllowedTargets.Contains(t)) { continue; }
                    if (e.AllowedTargets.Count >= PMProjectileLifecycleLimits.MaxTargetsPerProjectile) { break; }
                    e.AllowedTargets.Add(t);
                }
            }
        }

        // -------------------------------------------------------- 注册先于追赶

        /// <summary>
        /// 开始服务器追赶。**只能**对已成功登记的权威 key 调用，且每 key 一次。
        /// 这就是「注册必须先于追赶」在 API 上的体现：没有登记的 key 无法开始追赶，
        /// 重复/重入调用被 AlreadyStarted 拒绝。
        /// </summary>
        public PMProjectileCatchUpOutcome TryBeginCatchUp(PMProjectileKey key, double wallNowMs)
        {
            PMProjectileCatchUpOutcome o = new PMProjectileCatchUpOutcome();
            o.Key = key;
            o.Result = PMProjectileCatchUpResult.Invalid;
            o.Spec = null;
            o.RegistrationWallTimeMs = 0.0;

            if (key.Epoch != _epoch)
            {
                o.Result = PMProjectileCatchUpResult.StaleEpoch;
                return o;
            }

            PMProjectileClockCheck clock = PMProjectileWallClock.Validate(wallNowMs, _lastWallMs);
            if (clock != PMProjectileClockCheck.Ok)
            {
                o.Result = PMProjectileCatchUpResult.InvalidClock;
                return o;
            }

            if (_retiredSet.Contains(key))
            {
                o.Result = PMProjectileCatchUpResult.Retired;
                return o;
            }

            Entry e;
            if (!_entries.TryGetValue(key, out e))
            {
                o.Result = PMProjectileCatchUpResult.NotRegistered;
                return o;
            }

            if (e.Predicted)
            {
                o.Result = PMProjectileCatchUpResult.NotAuthority;
                return o;
            }

            if (e.CatchUpStarted)
            {
                o.Result = PMProjectileCatchUpResult.AlreadyStarted;
                return o;
            }

            e.CatchUpStarted = true;
            AcceptWallClock(wallNowMs);
            o.Result = PMProjectileCatchUpResult.Started;
            o.AuthorityNetId = e.AuthorityNetId;
            o.Spec = e.Spec == null ? new PMProjectileSpec() : e.Spec.Clone();
            o.RegistrationWallTimeMs = e.RegisteredWallTimeMs;
            return o;
        }

        // -------------------------------------------------- 预留假弹 → 权威本体

        /// <summary>
        /// 【R5-A3 返工】把「收请求时预留的预测登记」**原地**升级为权威本体。
        ///
        /// 为什么需要它（原实现的真缺陷）：admission 阶段必须用 TryRegisterPredicted 预留 ProjectileId
        /// （否则同 activation 的多弹乱序解挂会被 ID 高水位误拒），但预留项在 TryBeginCatchUp 眼里是
        /// NotAuthority ⇒ 一旦 activation 被权威确认为 Confirmed，这颗弹**永远无法开始权威追赶**
        /// （玩家弹拿不到任何延迟补偿，等同「确认后只能静止不动」）。
        ///
        /// 升级的不变式（逐条）：
        ///   · **不重新分配 ID**：不新建 Entry、不动 Key.ProjectileId；
        ///   · **不改 Origin**：ID 空间按 (owner, origin) 分域，改 Origin 就等于换身份；
        ///   · **权威 NetId 真实非 0**：同时写 Entry.AuthorityNetId 与冻结状态的 State.AuthorityNetId；
        ///   · **只有 Confirmed 可升级**（Pending 等待、Rejected 已撤销都不允许）；
        ///   · 幂等：重复升级返回 AlreadyPromoted（不重复计数、不重复盖上不同的权威 ID）；
        ///   · 升级后可开始追赶（TryBeginCatchUp 不再返回 NotAuthority）——这就是“确认后可追赶”的落点；
        ///   · 已停止/假弹已结束的预留项**仍可升级**：停止是运动终态，但身份必须切成权威，
        ///     否则确认时那颗弹会卡在「假弹身份」上（C 队列候选无法结算）。
        /// </summary>
        public PMProjectilePromoteOutcome TryPromoteReservedToAuthority(
            PMProjectileKey key, uint authorityNetId, double wallNowMs)
        {
            PMProjectilePromoteOutcome o = new PMProjectilePromoteOutcome();
            o.Key = key;
            o.Result = PMProjectilePromoteResult.UnknownKey;

            PMProjectileClockCheck clock = PMProjectileWallClock.Validate(wallNowMs, _lastWallMs);
            o.Clock = clock;
            if (clock != PMProjectileClockCheck.Ok)
            {
                o.Result = PMProjectilePromoteResult.InvalidClock;
                return o;
            }

            if (authorityNetId == 0u)
            {
                o.Result = PMProjectilePromoteResult.InvalidAuthority;
                return o;
            }

            if (key.Epoch != _epoch)
            {
                o.Result = PMProjectilePromoteResult.StaleEpoch;
                return o;
            }

            if (_retiredSet.Contains(key))
            {
                o.Result = PMProjectilePromoteResult.Retired;
                return o;
            }

            Entry e;
            if (!_entries.TryGetValue(key, out e))
            {
                o.Result = PMProjectilePromoteResult.UnknownKey;
                return o;
            }

            if (!e.Predicted)
            {
                // 权威原生登记 与 「已升级」都是 Predicted=false；用 Promoted 标记区分（幂等）。
                o.Result = e.Promoted
                    ? PMProjectilePromoteResult.AlreadyPromoted
                    : PMProjectilePromoteResult.NotPredicted;
                o.AuthorityNetId = e.AuthorityNetId;
                return o;
            }

            if (e.Activation != PMActivationResult.Confirmed)
            {
                // Pending（等待裁决）与 Rejected（已撤销、已从登记表移除）都不得升级。
                o.Result = PMProjectilePromoteResult.NotConfirmed;
                return o;
            }

            if (_authorityCount >= PMProjectileLimits.MaxProjectiles)
            {
                // 权威类别容量满：不升级、也不消耗预测名额（调用方必须显式处置）。
                o.Result = PMProjectilePromoteResult.Capacity;
                return o;
            }

            e.Predicted = false;
            e.PredictedAlive = false;
            e.Promoted = true;
            e.AuthorityNetId = authorityNetId;
            e.State.AuthorityNetId = authorityNetId;
            _predictedCount--;
            _authorityCount++;
            AcceptWallClock(wallNowMs);

            o.Result = PMProjectilePromoteResult.Promoted;
            o.AuthorityNetId = authorityNetId;
            return o;
        }

        // ------------------------------------------------------------ 接管

        /// <summary>
        /// 一次消费接管：权威镜像到达时把本地假弹「消费」掉。
        /// 第二次调用返回 AlreadyTakenOver（不允许二次消费造成的双发）。
        /// </summary>
        public PMProjectileTakeoverOutcome TryTakeoverPredicted(PMProjectileKey key)
        {
            PMProjectileTakeoverOutcome o = new PMProjectileTakeoverOutcome();
            o.Key = key;
            o.Result = PMProjectileTakeoverResult.Invalid;

            if (key.Epoch != _epoch)
            {
                o.Result = PMProjectileTakeoverResult.StaleEpoch;
                return o;
            }

            if (_retiredSet.Contains(key))
            {
                o.Result = PMProjectileTakeoverResult.Retired;
                return o;
            }

            Entry e;
            if (!_entries.TryGetValue(key, out e))
            {
                o.Result = PMProjectileTakeoverResult.UnknownKey;
                return o;
            }

            if (!e.Predicted)
            {
                o.Result = PMProjectileTakeoverResult.NoPredicted;
                return o;
            }

            if (e.TakenOver)
            {
                o.Result = PMProjectileTakeoverResult.AlreadyTakenOver;
                return o;
            }

            if (!e.PredictedAlive)
            {
                // 假弹已结束：接管不成立，调用方应走「隐藏但不复活」分支。
                o.Result = PMProjectileTakeoverResult.PredictedNotAlive;
                return o;
            }

            e.TakenOver = true;
            e.PredictedAlive = false;
            e.State.TakenOver = true;
            o.Result = PMProjectileTakeoverResult.TakenOver;
            o.AuthorityNetId = e.AuthorityNetId;
            o.Spec = e.Spec == null ? new PMProjectileSpec() : e.Spec.Clone();
            o.State = e.State == null ? new PMProjectileState() : e.State.Clone();
            return o;
        }

        /// <summary>假弹提前结束（本地销毁/超时）。保留 key 作为迟到镜像的墓碑。</summary>
        public PMProjectilePredictedEndOutcome NotifyPredictedEnded(
            PMProjectileKey key, double wallNowMs, int predictionMs, int delayDestroyMs)
        {
            PMProjectilePredictedEndOutcome o = new PMProjectilePredictedEndOutcome();
            o.Key = key;
            o.Result = PMProjectilePredictedEndResult.Invalid;

            if (key.Epoch != _epoch)
            {
                o.Result = PMProjectilePredictedEndResult.StaleEpoch;
                return o;
            }

            PMProjectileClockCheck clock = PMProjectileWallClock.Validate(wallNowMs, _lastWallMs);
            o.Clock = clock;
            if (clock != PMProjectileClockCheck.Ok)
            {
                o.Result = PMProjectilePredictedEndResult.InvalidClock;
                return o;
            }

            Entry e;
            if (!_entries.TryGetValue(key, out e))
            {
                o.Result = PMProjectilePredictedEndResult.UnknownKey;
                return o;
            }

            if (!e.Predicted)
            {
                o.Result = PMProjectilePredictedEndResult.NotPredicted;
                return o;
            }

            if (e.TakenOver)
            {
                // 接管后的权威实例仍活着：这里不是「假弹结束」，不改状态。
                o.Result = PMProjectilePredictedEndResult.TakenOver;
                return o;
            }

            AcceptWallClock(wallNowMs);
            if (e.PredictedEnded)
            {
                // 幂等：重复结束不延长墓碑（避免用重复事件无限续期）。
                o.Result = PMProjectilePredictedEndResult.AlreadyEnded;
                o.TombstoneMs = 0;
                return o;
            }

            e.PredictedAlive = false;
            e.PredictedEnded = true;
            // 假弹结束 = 运动终态：同时置 Stopped，使之后的本地停止/镜像停止都只能拿到
            // AlreadyStopped / StoppedNotMoved，不会产生**第二次停止通知**（契约 D9）。
            e.Stopped = true;
            int tombstoneMs = PMProjectileTombstone.ComputeTombstoneMs(delayDestroyMs, predictionMs);
            e.StopWallTimeMs = wallNowMs;
            e.TimeAfterStoppedMs = 0.0;
            e.TombstoneUntilMs = wallNowMs + tombstoneMs;
            e.State.Stopped = true;
            e.State.StopWallTimeMs = wallNowMs;
            e.State.TimeAfterStoppedMs = 0.0;
            e.State.TombstoneUntilMs = e.TombstoneUntilMs;
            o.Result = PMProjectilePredictedEndResult.Recorded;
            o.TombstoneMs = tombstoneMs;
            return o;
        }

        /// <summary>
        /// 镜像到达（位置/速度/运动时间）。四种分支：
        ///   · 假弹仍存活 → GateFakeAlive：**不写位置**（位置闸门）；
        ///   · 假弹已结束 → HiddenNoRevive：隐藏、不复活、不发二次停止通知；
        ///   · 已停止（本地停止 / 镜像带来的停止 / 假弹结束）→ StoppedNotMoved：不写运动（停止是终态）；
        ///   · 已接管 / 权威镜像 → Applied：写入镜像状态。
        ///
        /// 先校验后写入：身份不一致（key 不同）、非 finite 的镜像一律 Invalid，且**不**产生半写。
        /// </summary>
        public PMProjectileMirrorOutcome TryApplyMirror(
            PMProjectileKey key, PMProjectileState mirrorState, double wallNowMs)
        {
            PMProjectileMirrorOutcome o = new PMProjectileMirrorOutcome();
            o.Key = key;
            o.Result = PMProjectileMirrorResult.Invalid;
            o.Clock = PMProjectileClockCheck.Ok;

            if (mirrorState == null)
            {
                o.Result = PMProjectileMirrorResult.Invalid;
                return o;
            }

            // 身份一致性：镜像自带的 key 若有效，必须就是目标 key。
            if (mirrorState.Key.IsValid && !mirrorState.Key.Equals(key))
            {
                o.Result = PMProjectileMirrorResult.Invalid;
                return o;
            }

            // finite：非有限的位置/速度/朝向/时间不得写入冻结状态。
            if (!PMProjectileFinite.V(mirrorState.Position)
                || !PMProjectileFinite.V(mirrorState.PreviousPosition)
                || !PMProjectileFinite.V(mirrorState.Velocity)
                || !PMProjectileFinite.F(mirrorState.Yaw)
                || !PMProjectileFinite.D(mirrorState.MoveTimeMs))
            {
                o.Result = PMProjectileMirrorResult.Invalid;
                return o;
            }

            if (key.Epoch != _epoch)
            {
                o.Result = PMProjectileMirrorResult.StaleEpoch;
                return o;
            }

            PMProjectileClockCheck clock = PMProjectileWallClock.Validate(wallNowMs, _lastWallMs);
            o.Clock = clock;
            if (clock != PMProjectileClockCheck.Ok)
            {
                o.Result = PMProjectileMirrorResult.InvalidClock;
                return o;
            }

            if (_retiredSet.Contains(key))
            {
                o.Result = PMProjectileMirrorResult.Retired;
                return o;
            }

            Entry e;
            if (!_entries.TryGetValue(key, out e))
            {
                o.Result = PMProjectileMirrorResult.UnknownKey;
                return o;
            }

            AcceptWallClock(wallNowMs);
            e.LastMirrorWallTimeMs = wallNowMs;

            if (e.Predicted && e.PredictedAlive && !e.TakenOver)
            {
                // 位置闸门：假弹还在本地活着，权威镜像不得覆盖本地预测位置。
                o.Result = PMProjectileMirrorResult.GateFakeAlive;
                o.PositionApplied = false;
                o.Snapshot = e.Snapshot();
                return o;
            }

            if (e.Predicted && !e.TakenOver && e.PredictedEnded)
            {
                // 迟到的镜像：假弹已结束 → 隐藏，但不复活、不产生第二次停止通知。
                e.Hidden = true;
                e.Stopped = true;
                e.State.Stopped = true;
                e.State.Hidden = true;
                e.State.Position = mirrorState.Position;
                e.State.PreviousPosition = mirrorState.PreviousPosition;
                e.State.Velocity = PMVector3.Zero;
                int tombstoneMs = PMProjectileTombstone.ComputeTombstoneMs(e.Spec == null ? 0 : e.Spec.DelayDestroyMs, 0);
                double until = wallNowMs + tombstoneMs;
                if (until > e.TombstoneUntilMs) { e.TombstoneUntilMs = until; }
                e.State.TombstoneUntilMs = e.TombstoneUntilMs;
                o.Result = PMProjectileMirrorResult.HiddenNoRevive;
                o.PositionApplied = true;
                o.HideWithoutStopEvent = true;
                o.Snapshot = e.Snapshot();
                return o;
            }

            if (e.Stopped)
            {
                // 停止对运动是终态：镜像不得再把弹向前挪（否则会看到停止后继续漂移的幽灵轨迹），
                // 也不得把 Stopped/Hidden 翻回去。这里只记 LastMirrorWallTime。
                //
                // 为什么连「权威镜像」也挡：
                //   · 对预测假弹：已停止 = 已结束（TryTakeoverPredicted 也会因 PredictedNotAlive
                //     拒绝接管），契约要求「不复活、不二次停止通知」；
                //   · 对权威实例：停止本身已经是从权威侧观察到的结果，之后的镜像只是重复告知，
                //     而「未带 Stopped 的迟到镜像」若真能推翻权威停止，应该用新的接管/停止语义
                //     显式表达，而不是靠位置写入隐式改状态。
                o.Result = PMProjectileMirrorResult.StoppedNotMoved;
                o.PositionApplied = false;
                o.HideWithoutStopEvent = false;
                o.Snapshot = e.Snapshot();
                return o;
            }

            // 已接管（或权威镜像）：镜像状态生效。
            e.State.Position = mirrorState.Position;
            e.State.PreviousPosition = mirrorState.PreviousPosition;
            e.State.Velocity = mirrorState.Velocity;
            e.State.Yaw = mirrorState.Yaw;
            e.State.MoveTimeMs = mirrorState.MoveTimeMs;
            e.State.TakenOver = e.TakenOver;
            e.State.Stopped = false;
            e.Hidden = mirrorState.Hidden;
            e.State.Hidden = mirrorState.Hidden;

            if (mirrorState.Stopped)
            {
                // 镜像自带的停止只写状态，**不算**停止事件（事件只能从 TryRecordStop 产生）；
                // 但必须同时建立墓碑窗口 —— 否则紧随其后的合法迟到 Verify 会被
                // 「TombstoneUntilMs 仍为 0」误判为已过期，直接清掉条目。
                e.Stopped = true;
                e.State.Stopped = true;
                e.StopWallTimeMs = wallNowMs;
                e.TimeAfterStoppedMs = 0.0;
                int tombstoneMs = PMProjectileTombstone.ComputeTombstoneMs(
                    e.Spec == null ? 0 : e.Spec.DelayDestroyMs, 0);
                e.TombstoneUntilMs = wallNowMs + tombstoneMs;
                e.State.StopWallTimeMs = wallNowMs;
                e.State.TimeAfterStoppedMs = 0.0;
                e.State.TombstoneUntilMs = e.TombstoneUntilMs;
                if (e.Spec != null && e.Spec.HideOnStop)
                {
                    e.Hidden = true;
                    e.State.Hidden = true;
                }
            }

            o.Result = PMProjectileMirrorResult.Applied;
            o.PositionApplied = true;
            o.Snapshot = e.Snapshot();
            return o;
        }

        // ------------------------------------------------------ 运动推进（A3）

        /// <summary>
        /// 【A3 宿主 / 共享轨迹模型专用】推进已登记投弹的运动状态。
        ///
        /// 为什么必须有这个入口：<see cref="TryGetFrozen"/> / <see cref="TryGetRegistration"/> 返回的是
        /// **冻结副本**，调用方改写它写不回内部；
        /// 没有推进入口，账本里的弹就永远停在登记那一刻，共享轨迹模型/宿主无法真实跑动。
        ///
        /// 只写运动面：PreviousPosition / Position / Velocity / Yaw / MoveTimeMs。
        /// **不得被来路覆盖**的字段（本方法一律不动）：Key / ActivationId / Activation /
        /// AuthorityNetId / HitTargets / AllowedTargets / Stopped / Hidden / Predicted /
        /// PredictedAlive / PredictedEnded / TakenOver / CatchUpStarted /
        /// RegisteredWallTimeMs / StopWallTimeMs / TimeAfterStoppedMs / TombstoneUntilMs。
        ///
        /// 停止（本地停止 / 镜像停止 / 假弹结束）对运动是终态：之后返回 NotMovable，
        /// 位置保持不动（避免停止后继续漂移的幽灵轨迹）。
        ///
        /// MoveTimeMs 不要求单调：共享轨迹模型重放早于当前的时刻是合法用法，
        /// 新鲜度判定由调用方结合 TotalSimTimeMs 帧锚自行完成。
        /// </summary>
        public PMProjectileMotionOutcome TryAdvanceMotion(
            PMProjectileKey key,
            PMVector3 position,
            PMVector3 previousPosition,
            PMVector3 velocity,
            float yaw,
            double moveTimeMs)
        {
            PMProjectileMotionOutcome o = new PMProjectileMotionOutcome();
            o.Key = key;
            o.Result = PMProjectileMotionResult.Invalid;

            // 先校验再定位：非 finite 运动量不得进入冻结状态。
            if (!PMProjectileFinite.V(position) || !PMProjectileFinite.V(previousPosition)
                || !PMProjectileFinite.V(velocity) || !PMProjectileFinite.F(yaw)
                || !PMProjectileFinite.D(moveTimeMs))
            {
                o.Result = PMProjectileMotionResult.Invalid;
                return o;
            }

            if (key.Epoch != _epoch)
            {
                o.Result = PMProjectileMotionResult.StaleEpoch;
                return o;
            }

            if (_retiredSet.Contains(key))
            {
                o.Result = PMProjectileMotionResult.Retired;
                return o;
            }

            Entry e;
            if (!_entries.TryGetValue(key, out e))
            {
                o.Result = PMProjectileMotionResult.UnknownKey;
                return o;
            }

            if (e.Stopped || e.PredictedEnded)
            {
                o.Result = PMProjectileMotionResult.NotMovable;
                o.Position = e.State.Position;
                o.Velocity = e.State.Velocity;
                o.MoveTimeMs = e.State.MoveTimeMs;
                return o;
            }

            e.State.PreviousPosition = previousPosition;
            e.State.Position = position;
            e.State.Velocity = velocity;
            e.State.Yaw = yaw;
            e.State.MoveTimeMs = moveTimeMs;

            o.Result = PMProjectileMotionResult.Advanced;
            o.Position = position;
            o.Velocity = velocity;
            o.MoveTimeMs = moveTimeMs;
            return o;
        }

        // ------------------------------------------------------------ 停止

        /// <summary>
        /// 记录停止（墓碑开始）。停止**不**解除注册，墓碑期迟到 Verify 仍可完成校验。
        /// 重复停止返回 AlreadyStopped 且不发事件、不延长墓碑。
        /// </summary>
        public PMProjectileStopOutcome TryRecordStop(
            PMProjectileKey key,
            PMVector3 stopPosition,
            double wallNowMs,
            int predictionMs,
            int delayDestroyMs)
        {
            PMProjectileStopOutcome o = new PMProjectileStopOutcome();
            o.Key = key;
            o.Result = PMProjectileStopResult.Invalid;
            o.Clock = PMProjectileClockCheck.Ok;
            o.TombstoneUntilMs = 0.0;
            o.StopEventEmitted = false;

            if (key.Epoch != _epoch)
            {
                o.Result = PMProjectileStopResult.StaleEpoch;
                return o;
            }

            PMProjectileClockCheck clock = PMProjectileWallClock.Validate(wallNowMs, _lastWallMs);
            o.Clock = clock;
            if (clock != PMProjectileClockCheck.Ok)
            {
                o.Result = PMProjectileStopResult.InvalidClock;
                return o;
            }

            if (!PMProjectileFinite.V(stopPosition))
            {
                o.Result = PMProjectileStopResult.Invalid;
                return o;
            }

            if (_retiredSet.Contains(key))
            {
                o.Result = PMProjectileStopResult.Retired;
                return o;
            }

            Entry e;
            if (!_entries.TryGetValue(key, out e))
            {
                o.Result = PMProjectileStopResult.UnknownKey;
                return o;
            }

            AcceptWallClock(wallNowMs);
            if (e.Stopped)
            {
                o.Result = PMProjectileStopResult.AlreadyStopped;
                o.TombstoneUntilMs = e.TombstoneUntilMs;
                return o;
            }

            e.Stopped = true;
            e.StopWallTimeMs = wallNowMs;
            e.TimeAfterStoppedMs = 0.0;
            e.State.Stopped = true;
            e.State.StopWallTimeMs = wallNowMs;
            e.State.TimeAfterStoppedMs = 0.0;
            e.State.Position = stopPosition;
            e.State.Velocity = PMVector3.Zero;
            // 注意：停止**不清空**白名单/命中集合。它们必须活到墓碑结束（停止后的迟到命中
            // 仍要用它们做去重），只在 Retire/Purge 的回收路径清空（对应 D-R0-39 的修正）。
            if (e.Spec != null && e.Spec.HideOnStop)
            {
                e.Hidden = true;
                e.State.Hidden = true;
            }

            int tombstoneMs = PMProjectileTombstone.ComputeTombstoneMs(delayDestroyMs, predictionMs);
            e.TombstoneUntilMs = wallNowMs + tombstoneMs;
            // 冻结状态必须与账本同一份真值：否则 TryGetFrozen 出来的 State.TombstoneUntilMs 恒为 0，
            // 任何按 State 判墓碑的消费方（含 A2 验证器的 L1 截止）在「本地停止」路径上都会静默失效。
            e.State.TombstoneUntilMs = e.TombstoneUntilMs;

            o.Result = PMProjectileStopResult.Recorded;
            o.TombstoneUntilMs = e.TombstoneUntilMs;
            o.StopEventEmitted = true;
            return o;
        }

        /// <summary>
        /// 迟到 Verify 的准入。墓碑窗口内允许（AllowedInTombstone），超窗明确过期
        /// （TombstoneExpired）并把该 key 清理到终态标记环。
        /// </summary>
        public PMProjectileVerifyAdmissionOutcome AdmitVerify(PMProjectileKey key, double wallNowMs)
        {
            PMProjectileVerifyAdmissionOutcome o = new PMProjectileVerifyAdmissionOutcome();
            o.Key = key;
            o.Result = PMProjectileVerifyAdmission.Invalid;

            if (key.Epoch != _epoch)
            {
                o.Result = PMProjectileVerifyAdmission.StaleEpoch;
                return o;
            }

            PMProjectileClockCheck clock = PMProjectileWallClock.Validate(wallNowMs, _lastWallMs);
            o.Clock = clock;
            if (clock != PMProjectileClockCheck.Ok)
            {
                o.Result = PMProjectileVerifyAdmission.InvalidClock;
                return o;
            }

            if (_retiredSet.Contains(key))
            {
                o.Result = PMProjectileVerifyAdmission.Retired;
                return o;
            }

            Entry e;
            if (!_entries.TryGetValue(key, out e))
            {
                o.Result = PMProjectileVerifyAdmission.UnknownKey;
                return o;
            }

            AcceptWallClock(wallNowMs);
            bool tombstoned = e.Stopped || e.PredictedEnded;
            if (!tombstoned)
            {
                o.Result = PMProjectileVerifyAdmission.Allowed;
                o.TombstoneRemainingMs = 0.0;
                return o;
            }

            if (wallNowMs > e.TombstoneUntilMs)
            {
                o.Result = PMProjectileVerifyAdmission.TombstoneExpired;
                o.TombstoneRemainingMs = 0.0;
                PMProjectileActivationRelease release;
                Retire(key, PMProjectileReleaseReason.TombstoneExpired, wallNowMs, out release);
                if (release.RemovedInstance)
                {
                    AddPendingRelease(release);
                }

                return o;
            }

            o.Result = PMProjectileVerifyAdmission.AllowedInTombstone;
            o.TombstoneRemainingMs = e.TombstoneUntilMs - wallNowMs;
            return o;
        }

        // ------------------------------------------------------ 激活账本

        /// <summary>
        /// 可信激活裁决写入（唯一写入口）。返回**独占**释放项列表：
        ///   · Confirmed → 同一 activation 的所有等待弹一次性解挂（多弹同时解挂）；
        ///   · Rejected → 等待弹**实际移除**（RemovedInstance=true）；
        ///   · 终态不倒退 → 已终态再写（含反向）返回 AlreadyTerminal，不重复释放。
        /// </summary>
        public PMActivationLedgerResult TrySetActivationResult(
            uint ownerNetId,
            uint activationId,
            PMActivationResult outcome,
            double wallNowMs,
            out PMProjectileActivationRelease[] releases)
        {
            releases = new PMProjectileActivationRelease[0];
            if (ownerNetId == 0u || activationId == 0u)
            {
                return PMActivationLedgerResult.InvalidActivationId;
            }

            if (outcome != PMActivationResult.Pending
                && outcome != PMActivationResult.Confirmed
                && outcome != PMActivationResult.Rejected)
            {
                return PMActivationLedgerResult.InvalidOutcome;
            }

            if (PMProjectileWallClock.Validate(wallNowMs, _lastWallMs) != PMProjectileClockCheck.Ok)
            {
                return PMActivationLedgerResult.InvalidClock;
            }

            PMActivationKey ak = new PMActivationKey(ownerNetId, activationId);
            LedgerEntry existing;
            if (_ledgerIndex.TryGetValue(ak, out existing))
            {
                bool existingTerminal = existing.Outcome != PMActivationResult.Pending;
                bool incomingTerminal = outcome != PMActivationResult.Pending;
                if (existingTerminal)
                {
                    // 终态不倒退：重复 Confirmed / Rejected 与反向写入一律幂等拒绝。
                    return PMActivationLedgerResult.AlreadyTerminal;
                }

                if (!incomingTerminal)
                {
                    existing.WallTimeMs = wallNowMs;
                    AcceptWallClock(wallNowMs);
                    return PMActivationLedgerResult.RecordedPending;
                }

                existing.Outcome = outcome;
                existing.WallTimeMs = wallNowMs;
                AcceptWallClock(wallNowMs);
                releases = ReleaseWaiters(ak, outcome, wallNowMs);
                return PMActivationLedgerResult.Applied;
            }

            // 账本里已经没有这条（可能被容量淘汰）：被淘汰的终态判决仍在有界记忆里，
            // 必须继续拦住一切写入（含同向重复、反向覆盖、以及把终态降回 Pending），
            // 否则一条迟到/重放的裁决就能在同一 activation 上“复活”。
            PMActivationResult remembered;
            if (_terminalMemory.TryGetValue(ak, out remembered))
            {
                return PMActivationLedgerResult.AlreadyTerminal;
            }

            if (_ledger.Count >= PMProjectileLifecycleLimits.MaxActivationLedger
                && !EvictOldestTerminalLedgerEntry())
            {
                return PMActivationLedgerResult.LedgerFull;
            }

            LedgerEntry le = new LedgerEntry();
            le.Key = ak;
            le.Outcome = outcome;
            le.WallTimeMs = wallNowMs;
            _ledger.Add(le);
            _ledgerIndex.Add(ak, le);
            AcceptWallClock(wallNowMs);

            if (outcome == PMActivationResult.Pending)
            {
                return PMActivationLedgerResult.RecordedPending;
            }

            releases = ReleaseWaiters(ak, outcome, wallNowMs);
            return PMActivationLedgerResult.Applied;
        }

        private bool EvictOldestTerminalLedgerEntry()
        {
            for (int i = 0; i < _ledger.Count; i++)
            {
                if (_ledger[i].Outcome == PMActivationResult.Pending) { continue; }
                // 终态仍有等待者意味着还有弹挂着未解挂：这种条目不得淘汰。
                if (_activationWaiters.ContainsKey(_ledger[i].Key)) { continue; }
                RememberTerminalVerdict(_ledger[i].Key, _ledger[i].Outcome);
                _ledgerIndex.Remove(_ledger[i].Key);
                _ledger.RemoveAt(i);
                _evictedTerminalLedgerCount++;
                return true;
            }

            return false;
        }

        /// <summary>把被淘汰的终态判决记入有界环（插入序，超出容量则遗忘最旧）。</summary>
        private void RememberTerminalVerdict(PMActivationKey key, PMActivationResult outcome)
        {
            if (_terminalMemory.ContainsKey(key)) { return; }

            if (_terminalRing.Count >= PMProjectileLifecycleLimits.MaxActivationLedger)
            {
                PMActivationKey oldest = _terminalRing.Dequeue();
                _terminalMemory.Remove(oldest);
            }

            _terminalRing.Enqueue(key);
            _terminalMemory.Add(key, outcome);
        }

        private PMProjectileActivationRelease[] ReleaseWaiters(
            PMActivationKey ak, PMActivationResult outcome, double wallNowMs)
        {
            List<PMProjectileKey> waiters;
            if (!_activationWaiters.TryGetValue(ak, out waiters))
            {
                return new PMProjectileActivationRelease[0];
            }

            // 必须先快照、并把映射从字典摘掉：Retire 内部会经 RemoveWaiter 修改这个列表，
            // 边遍历边删会让「同一 activation 的后续弹」被静默漏掉（只撤銈一半）。
            // 摘掉映射后 RemoveWaiter 自然变成 no-op，无需在删除路径上加特例。
            PMProjectileKey[] snapshot = waiters.ToArray();
            _activationWaiters.Remove(ak);

            // 用 List + Add 而不是预分配数组按下标写：预分配时若某 key 已不在登记表，
            // 那个下标会留下 default 释放项（全 0 Key / RemovedInstance=false），
            // 调用方无法区分「真的没事」与「这条是洞」。
            List<PMProjectileActivationRelease> releases =
                new List<PMProjectileActivationRelease>(snapshot.Length);
            for (int i = 0; i < snapshot.Length; i++)
            {
                PMProjectileKey key = snapshot[i];
                Entry e;
                if (!_entries.TryGetValue(key, out e)) { continue; }

                e.Activation = outcome;
                PMProjectileActivationRelease release = new PMProjectileActivationRelease();
                release.Key = key;
                release.OwnerNetId = key.OwnerNetId;
                release.ActivationId = ak.ActivationId;
                release.Origin = key.Origin;
                release.AuthorityNetId = e.AuthorityNetId;
                release.Outcome = outcome;
                release.PendingElapsedMs = wallNowMs - e.RegisteredWallTimeMs;
                if (release.PendingElapsedMs < 0.0) { release.PendingElapsedMs = 0.0; }
                release.WallTimeMs = wallNowMs;

                if (outcome == PMActivationResult.Rejected)
                {
                    // 拒绝 ⇒ 实际撤销预测实例（不是只打标记）。
                    // RemovedInstance 由 Retire 判定：只有「真存在的本地假弹」才是 true；
                    // 已接管的弹本地实例已被权威镜像取代，不得被误删。
                    PMProjectileActivationRelease retired;
                    Retire(key, PMProjectileReleaseReason.ActivationRejected, wallNowMs, out retired);
                    release.RemovedInstance = retired.RemovedInstance;
                    release.Reason = PMProjectileReleaseReason.ActivationRejected;
                }
                else
                {
                    release.RemovedInstance = false;
                    release.Reason = PMProjectileReleaseReason.ActivationConfirmed;
                }

                releases.Add(release);
            }

            return releases.ToArray();
        }

        // ------------------------------------------------------ 清理与回收

        /// <summary>墓碑到期清理（墙钟驱动）。返回被清理的条数；撤销项进待取释放队列。</summary>
        public int PurgeExpired(double wallNowMs)
        {
            if (PMProjectileWallClock.Validate(wallNowMs, _lastWallMs) != PMProjectileClockCheck.Ok)
            {
                return 0;
            }

            AcceptWallClock(wallNowMs);
            List<PMProjectileKey> expired = null;
            foreach (KeyValuePair<PMProjectileKey, Entry> kv in _entries)
            {
                Entry e = kv.Value;
                if (!(e.Stopped || e.PredictedEnded)) { continue; }
                if (e.TakenOver && !e.Stopped) { continue; }
                if (wallNowMs <= e.TombstoneUntilMs) { continue; }
                if (expired == null) { expired = new List<PMProjectileKey>(); }
                expired.Add(kv.Key);
            }

            if (expired == null) { return 0; }

            for (int i = 0; i < expired.Count; i++)
            {
                PMProjectileActivationRelease release;
                Retire(expired[i], PMProjectileReleaseReason.TombstoneExpired, wallNowMs, out release);
                if (release.RemovedInstance)
                {
                    AddPendingRelease(release);
                }
            }

            return expired.Count;
        }

        /// <summary>退回池/彻底销毁：终态不可逆，白名单与命中集合一并清空。</summary>
        public bool Retire(PMProjectileKey key, double wallNowMs)
        {
            PMProjectileActivationRelease release;
            return Retire(key, PMProjectileReleaseReason.TombstoneExpired, wallNowMs, out release);
        }

        private bool Retire(PMProjectileKey key, PMProjectileReleaseReason reason, double wallNowMs,
            out PMProjectileActivationRelease release)
        {
            release = new PMProjectileActivationRelease();
            Entry e;
            if (!_entries.TryGetValue(key, out e))
            {
                return false;
            }

            release.Key = key;
            release.OwnerNetId = key.OwnerNetId;
            release.ActivationId = e.ActivationId;
            release.Origin = key.Origin;
            release.AuthorityNetId = e.AuthorityNetId;
            release.Outcome = e.Activation;
            release.Reason = reason;
            release.RemovedInstance = e.Predicted && !e.TakenOver;
            release.PendingElapsedMs = wallNowMs - e.RegisteredWallTimeMs;
            if (release.PendingElapsedMs < 0.0) { release.PendingElapsedMs = 0.0; }
            release.WallTimeMs = wallNowMs;

            // 回收：白名单/命中集合/激活等待全部清空（契约「回池清单」的纯核心部分）。
            // 注意：内部 State 的 HitTargets/AllowedTargets 数组不再是真值（唯一真值是这里的
            // 两份 List，且 StateCopy 会从它们派生），所以这里只需清 List。
            e.AllowedTargets.Clear();
            e.HitTargets.Clear();
            e.Hidden = true;
            e.State.Hidden = true;

            RemoveWaiter(key, e);
            _entries.Remove(key);

            int ownerLive;
            if (_ownerCounts.TryGetValue(key.OwnerNetId, out ownerLive))
            {
                if (ownerLive <= 1) { _ownerCounts.Remove(key.OwnerNetId); }
                else { _ownerCounts[key.OwnerNetId] = ownerLive - 1; }
            }

            if (e.Predicted) { if (_predictedCount > 0) { _predictedCount--; } }
            else { if (_authorityCount > 0) { _authorityCount--; } }

            PushRetiredMarker(key);
            return true;
        }

        private void RemoveWaiter(PMProjectileKey key, Entry e)
        {
            if (e.ActivationId == 0u) { return; }
            PMActivationKey ak = new PMActivationKey(key.OwnerNetId, e.ActivationId);
            List<PMProjectileKey> waiters;
            if (!_activationWaiters.TryGetValue(ak, out waiters)) { return; }
            waiters.Remove(key);
            if (waiters.Count == 0) { _activationWaiters.Remove(ak); }
        }

        private void PushRetiredMarker(PMProjectileKey key)
        {
            if (_retiredSet.Contains(key)) { return; }
            if (_retiredRing.Count >= PMProjectileLifecycleLimits.MaxRetiredMarkers)
            {
                PMProjectileKey oldest = _retiredRing.Dequeue();
                _retiredSet.Remove(oldest);
            }

            _retiredRing.Enqueue(key);
            _retiredSet.Add(key);
        }

        /// <summary>断连清理：移除该 owner 的全部登记与等待（水位保留 ⇒ ID 永不复用）。</summary>
        public int ClearOwner(uint ownerNetId, double wallNowMs)
        {
            if (ownerNetId == 0u) { return 0; }
            if (PMProjectileWallClock.Validate(wallNowMs, _lastWallMs) != PMProjectileClockCheck.Ok)
            {
                return 0;
            }

            AcceptWallClock(wallNowMs);
            List<PMProjectileKey> owned = null;
            foreach (KeyValuePair<PMProjectileKey, Entry> kv in _entries)
            {
                if (kv.Key.OwnerNetId != ownerNetId) { continue; }
                if (owned == null) { owned = new List<PMProjectileKey>(); }
                owned.Add(kv.Key);
            }

            if (owned == null) { return 0; }

            for (int i = 0; i < owned.Count; i++)
            {
                PMProjectileActivationRelease release;
                Retire(owned[i], PMProjectileReleaseReason.OwnerCleared, wallNowMs, out release);
                if (release.RemovedInstance)
                {
                    AddPendingRelease(release);
                }
            }

            return owned.Count;
        }

        /// <summary>
        /// 升 epoch：整体清理，**不允许旧 epoch 复活**（旧 epoch 的 key 此后一律 StaleEpoch）。
        /// 只接受严格递增；否则返回 false（不改变状态）。
        /// </summary>
        public bool AdvanceEpoch(uint newEpoch)
        {
            if (newEpoch <= _epoch) { return false; }
            _epoch = newEpoch;
            _entries.Clear();
            _watermarks.Clear();
            _ownerCounts.Clear();
            _activationWaiters.Clear();
            _ledger.Clear();
            _ledgerIndex.Clear();
            _terminalRing.Clear();
            _terminalMemory.Clear();
            _retiredRing.Clear();
            _retiredSet.Clear();
            _pendingReleases.Clear();
            _authorityCount = 0;
            _predictedCount = 0;
            _droppedReleaseCount = 0;
            _evictedTerminalLedgerCount = 0;
            _lastWallMs = 0.0;
            return true;
        }

        // -------------------------------------------------- 待取释放队列

        /// <summary>取出至多 max 条（FIFO）待取释放项。返回**独占**数组。</summary>
        public int DrainPendingReleases(int max, out PMProjectileActivationRelease[] releases)
        {
            if (max <= 0)
            {
                releases = new PMProjectileActivationRelease[0];
                return 0;
            }

            int n = _pendingReleases.Count < max ? _pendingReleases.Count : max;
            releases = new PMProjectileActivationRelease[n];
            for (int i = 0; i < n; i++)
            {
                releases[i] = _pendingReleases[i];
            }

            _pendingReleases.RemoveRange(0, n);
            return n;
        }

        private void AddPendingRelease(PMProjectileActivationRelease release)
        {
            if (_pendingReleases.Count >= PMProjectileLifecycleLimits.MaxPendingReleases)
            {
                _pendingReleases.RemoveAt(0);
                _droppedReleaseCount++;
            }

            _pendingReleases.Add(release);
        }

        private static PMProjectileActivationRelease[] MakeReleases(
            PMProjectileKey key, uint ownerNetId, uint activationId, PMProjectileReleaseReason reason,
            bool removed, double pendingElapsedMs, double wallNowMs)
        {
            PMProjectileActivationRelease[] releases = new PMProjectileActivationRelease[1];
            releases[0].Key = key;
            releases[0].OwnerNetId = ownerNetId;
            releases[0].ActivationId = activationId;
            releases[0].Origin = key.Origin;
            releases[0].AuthorityNetId = 0u;
            releases[0].Outcome = PMActivationResult.Rejected;
            releases[0].Reason = reason;
            releases[0].RemovedInstance = removed;
            releases[0].PendingElapsedMs = pendingElapsedMs;
            releases[0].WallTimeMs = wallNowMs;
            return releases;
        }

        private void AcceptWallClock(double wallNowMs)
        {
            if (wallNowMs > _lastWallMs) { _lastWallMs = wallNowMs; }
        }
    }
}
