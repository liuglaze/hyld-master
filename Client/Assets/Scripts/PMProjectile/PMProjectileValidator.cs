// ============================================================================
//  PMProjectileValidator —— R5-A2 命中验证纯核心（L0–L3，无副作用）
// ============================================================================
//
//  契约来源：
//    · Docs/plans/net-r5-projectile-contract.md（「命中验证（纯验证不直接扣血）」L0–L4）
//    · Docs/plans/_r5_semantics_survey.md §1.5（L0–L4 算法）/ §1.6（米 + 整毫秒换算）
//    · 共享只读类型 PMProjectile/PMProjectileContracts.cs（**本文件不得修改它**）
//
//  ---- 本文件提供的公开 API（交付主侧）----
//    PMProjectileValidateRequest / PMProjectileValidateResult
//    PMProjectileValidator.Validate(...)   ← 唯一入口，纯函数、可重入、无状态
//    PMProjectileValidateStatus / PMProjectileRejectReason / PMProjectileSkipReason
//
//  ---- 硬语义 ----
//    · 纯校验：不扣血、不创建 Unity 对象、不调用任何伤害出口。Pending 绝不产生可结算结果
//      （<see cref="PMProjectileValidateResult.IsSettleable"/> 为 false）。
//    · 被拒整个包不部分输出可结算结果：Rejected 时 Hits 为空数组。
//    · 不修改输入：State / Spec / Batch / Target 数组在调用前后逐字节不变；输出为深拷贝值。
//    · max 5 次 Verify 配额由**主调用者**持有：本验证器无字典、无跨调用状态；
//      调用者传入 UsedVerifyCalls，返回 VerifyConsumed 表示本次是否消耗了一次配额。
//    · 数值溢出 fail closed：任何中间量非有限即拒绝（包级 Reject / 目标级 skip），绝不“当作通过”。
//
//  ---- L0 激活裁决（本项目修订，D-R5-01）----
//    ActivationId == 0：**仅** ServerDirect 可信 → Confirmed；ClientPredicted 携带 0 → 整包 Reject。
//    ActivationId != 0：以调用方传入的权威账本结论（PMActivationResult）为准，**不**按号段自行放行。
//
//  ---- L1 生命周期/三道闸门（任一失败整包 Reject）----
//    0) 时钟必须有限（WorldNowMs 非有限 → Reject）：否则墓碑窗口比较恒为 false、窗口静默失效；
//    ① 目标数组非空；② 墓碑截止（Stopped 且 worldNow > TombstoneUntilMs）；③ Verify 次数 ≤ 5；
//    ④ 每批目标 ≤ 100。配额在通过 0)①② 之后消耗（即「进入验证」的包才消耗，最多 5 次）；
//    结构性非法（入参/元素/identity/activation/墓碑）在配额之前拒绝，因此**不消耗**。
//
//  ---- L2 飞行预算三分支（整包 Reject）----
//    ① 飞行中（!Stopped 且未 skip）：锚＝权威当前位置；budget = speed*(2*clamp(rewind,0,500)+100+extraDefer)/1000 米。
//    ②a 停止 + 回放（Stopped 且 extraDefer>0）：锚＝权威出生点 SpawnPosition；同 ① 预算。
//    ②b 停止 + 常规（Stopped 且 extraDefer==0）：锚＝停止位置 Position；budget = 弹半径 + 5.00 + 0.30 米。
//    SkipFlyingTrajectoryValidation **只**跳过 ①；②a/②b 永不跳过。
//
//  ---- L3 逐目标（失败只 skip 该目标，不连坐整包）----
//    白名单 → 已命中/批内重复去重 → 历史还原（帧锚/回溯/当前）→ 尺寸合法性 → Alive →
//    视觉偏移 → ImpactPoint 钳制（只钳不拒）→ 权威 Filter（收到**消毒后**的点）→
//    线段–线段几何（双精度、覆盖平行/退化）。骨骼细节后置，不发明伤害倍率。
//
//  依赖面（硬）：PMNet / PMNet.Mover / PMNet.Projectile 共享契约。禁止 UnityEngine。
//  Unity 2019.4 = C# 7.3 + netstandard2.0：不使用 Math.Clamp / Span / 元组 / ??=。
// ============================================================================

using System;
using System.Collections.Generic;
using PMNet.Mover;

namespace PMNet.Projectile
{
    /// <summary>验证结论：Rejected 无任何可结算输出；Pending 只有候选；Confirmed 才有可结算 hits。</summary>
    public enum PMProjectileValidateStatus : byte
    {
        Rejected = 0,
        Pending = 1,
        Confirmed = 2,
    }

    /// <summary>整包被拒的明确原因。None 仅出现在未拒绝的结果里。</summary>
    public enum PMProjectileRejectReason : byte
    {
        None = 0,
        InvalidArgument = 1,
        StateKeyInvalid = 2,
        BatchKeyMismatch = 3,
        TargetIdInvalid = 4,
        NoTargets = 5,
        TargetCountExceeded = 6,
        HitPositionNotFinite = 7,
        PreviousPositionNotFinite = 8,
        SegmentNotFinite = 9,
        SegmentTooLong = 10,
        RewindNegative = 11,
        ImpactPointNotFinite = 12,
        VisualOffsetNotFinite = 13,
        VisualOffsetTooLarge = 14,
        SpecInvalid = 15,
        ActivationIdZeroNotServerDirect = 16,
        ActivationRejected = 17,
        TombstoneExpired = 18,
        VerifyQuotaExhausted = 19,
        TrajectoryBudgetExceeded = 20,
        BudgetOverflow = 21,
    }

    /// <summary>单个目标被跳过的原因（L3 目标级，不影响整包结论）。</summary>
    public enum PMProjectileSkipReason : byte
    {
        None = 0,
        DuplicateAlreadyHit = 1,
        DuplicateInBatch = 2,
        NotInWhitelist = 3,
        HistoryUnavailable = 4,
        TargetPositionNotFinite = 5,
        TargetSizeInvalid = 6,
        TargetNotAlive = 7,
        StreamMismatch = 8,
        FilterUnavailable = 9,
        FilterRejected = 10,
        GeometryMiss = 11,
        NumericOverflow = 12,
    }

    /// <summary>被跳过的目标及其原因（诊断用，不参与结算）。</summary>
    public struct PMProjectileSkippedTarget
    {
        public uint TargetNetId;
        public PMProjectileSkipReason Reason;

        public PMProjectileSkippedTarget(uint targetNetId, PMProjectileSkipReason reason)
        {
            TargetNetId = targetNetId;
            Reason = reason;
        }
    }

    /// <summary>一次验证的完整输入。调用方负责填充；本类被 Validate 只读访问，不被修改。</summary>
    public sealed class PMProjectileValidateRequest
    {
        public PMProjectileState State;
        public PMProjectileSpec Spec;

        /// <summary>权威账本对该激活的裁决（仅当 ActivationId != 0 时被采信）。</summary>
        public PMActivationResult Activation = PMActivationResult.Pending;

        public PMProjectileHitBatch Batch;

        /// <summary>目标历史（DS 权威提供）。为 null 时所有目标按 HistoryUnavailable skip。</summary>
        public IPMProjectileTargetHistory History;

        /// <summary>权威命中过滤器（收到消毒后的命中点）。为 null 时目标按 FilterUnavailable skip。</summary>
        public IPMProjectileHitFilter Filter;

        /// <summary>DS 会话世界时间（毫秒），用于 TTL / 墓碑 / 回溯基准。</summary>
        public double WorldNowMs;

        /// <summary>额外延迟（毫秒，来自挂起 Spawn 的解挂等待）。负值按 0 计并记 Report。</summary>
        public int ExtraDeferMs;

        /// <summary>该弹已消耗的 Verify 次数（主调用者持有）。&gt;= 5 时本包被拒且不再消耗。</summary>
        public int UsedVerifyCalls;
    }

    /// <summary>验证结果（深拷贝值；与输入数组无别名）。</summary>
    public sealed class PMProjectileValidateResult
    {
        public PMProjectileValidateStatus Status;

        /// <summary>整包被拒原因；Status != Rejected 时为 None。</summary>
        public PMProjectileRejectReason Reason;
        public PMProjectileValidatedHit[] Hits = new PMProjectileValidatedHit[0];
        public PMProjectileSkippedTarget[] SkippedTargets = new PMProjectileSkippedTarget[0];

        /// <summary>诊断信号（例如 rewind &gt; 1000ms、历史退化为 Current、extraDefer 负值被钳）。</summary>
        public string[] Reports = new string[0];

        /// <summary>本次是否消耗了一次 Verify 配额（主调用者据此更新自己的计数）。</summary>
        public bool VerifyConsumed;

        /// <summary>
        /// 是否可交给唯一结算者。**只有** Confirmed 且有命中才为 true：
        /// Pending（候选）与 Rejected 一律 false，杜绝“Pending 直接结算”。
        ///
        /// 调用方约定（B/A1 集成，本类无法强制）：
        ///   · <see cref="Status"/> == Confirmed 只表示**激活可信**，并不表示有命中：结算前必须同时看
        ///     <see cref="IsSettleable"/>（或 Hits.Length &gt; 0）。
        ///   · Pending 的 <see cref="Hits"/> 是**候选**：账本确认后应直接结算这批候选，**不得重跑 Validate**
        ///     （本类无状态，重跑会再消耗一次 Verify 配额，且可能因时间/墓碑推进而改判）。
        ///   · 结算后调用方必须把命中目标并入 `state.HitTargets`（本类纯验证、不写输入），
        ///     否则同一弹对同一目标可以重复结算。
        /// </summary>
        public bool IsSettleable
        {
            get { return Status == PMProjectileValidateStatus.Confirmed && Hits.Length > 0; }
        }
    }

    /// <summary>
    /// 命中验证纯核心。全部方法为静态纯函数；本类不持有任何跨调用状态。
    /// </summary>
    public static class PMProjectileValidator
    {
        public const int MaxVerifyCalls = PMProjectileLimits.MaxVerifyCalls;       // 5
        public const int MaxTargetsPerBatch = PMProjectileLimits.MaxTargets;       // 100
        public const float MaxSegmentM = PMProjectileLimits.MaxSegmentM;           // 20m
        public const float MaxVisualOffsetM = PMProjectileLimits.MaxVisualOffsetM; // 20m
        public const float HitToleranceM = PMProjectileLimits.HitToleranceM;       // 0.30m
        public const int MaxRewindMs = PMProjectileLimits.MaxRewindMs;             // 500ms
        public const int TickBufferMs = PMProjectileLimits.TickBufferMs;           // 100ms

        /// <summary>rewind 超过它只 Report、不 Reject（契约：RewindTime Report 阈值 1000ms）。</summary>
        public const int RewindReportThresholdMs = 1000;

        /// <summary>L2 ②b 的目标体型余量（契约：MaxTargetBodyRadiusCm = 500cm = 5.00m）。</summary>
        public const float StoppedBodyMarginM = 5f;

        private static readonly PMProjectileHitCandidate[] EmptyTargets = new PMProjectileHitCandidate[0];

        /// <summary>
        /// 唯一入口。纯函数：不修改 <paramref name="request"/> 引用的任何对象，
        /// 返回的结果持有新建数组，不与输入共享可变状态。
        /// </summary>
        public static PMProjectileValidateResult Validate(PMProjectileValidateRequest request)
        {
            List<string> reports = new List<string>();

            if (request == null)
            {
                return Reject(PMProjectileRejectReason.InvalidArgument, reports, false);
            }

            PMProjectileState state = request.State;
            PMProjectileSpec spec = request.Spec;
            PMProjectileHitBatch batch = request.Batch;
            if (state == null || spec == null || batch == null)
            {
                return Reject(PMProjectileRejectReason.InvalidArgument, reports, false);
            }

            // ---------------- 结构前置 ----------------
            if (IsNonFinite(request.WorldNowMs))
            {
                // 非有限时钟会让墓碑/过期比较恒为 false（窗口静默失效）且让历史裁剪失效：
                // 宁可整包拒，也不放行一个「迟到窗口本身已经失效」的验证。
                return Reject(PMProjectileRejectReason.InvalidArgument, reports, false);
            }

            if (!state.Key.IsValid)
            {
                return Reject(PMProjectileRejectReason.StateKeyInvalid, reports, false);
            }

            if (batch.Key != state.Key)
            {
                return Reject(PMProjectileRejectReason.BatchKeyMismatch, reports, false);
            }

            // ---------------- 入口硬门（全批先检查）----------------
            if (!batch.HitPosition.IsFinite)
            {
                return Reject(PMProjectileRejectReason.HitPositionNotFinite, reports, false);
            }

            if (!batch.PreviousPosition.IsFinite)
            {
                return Reject(PMProjectileRejectReason.PreviousPositionNotFinite, reports, false);
            }

            double segmentM = Distance(batch.PreviousPosition, batch.HitPosition);
            if (IsNonFinite(segmentM))
            {
                return Reject(PMProjectileRejectReason.SegmentNotFinite, reports, false);
            }

            if (segmentM > (double)MaxSegmentM)
            {
                return Reject(PMProjectileRejectReason.SegmentTooLong, reports, false);
            }

            if (batch.RewindMs < 0)
            {
                return Reject(PMProjectileRejectReason.RewindNegative, reports, false);
            }

            if (batch.RewindMs > RewindReportThresholdMs)
            {
                // 只 Report，不 Reject：仍必须继续检查所有候选（不得提前绕过 Reject）。
                reports.Add("rewind-exceeds-1000ms:" + batch.RewindMs);
            }

            PMProjectileHitCandidate[] targets = batch.Targets;
            if (targets == null)
            {
                targets = EmptyTargets;
            }

            if (targets.Length == 0)
            {
                return Reject(PMProjectileRejectReason.NoTargets, reports, false);
            }

            if (targets.Length > MaxTargetsPerBatch)
            {
                return Reject(PMProjectileRejectReason.TargetCountExceeded, reports, false);
            }

            for (int i = 0; i < targets.Length; i++)
            {
                PMProjectileHitCandidate target = targets[i];
                if (target.TargetNetId == 0u)
                {
                    return Reject(PMProjectileRejectReason.TargetIdInvalid, reports, false);
                }

                if (!target.ImpactPoint.IsFinite)
                {
                    return Reject(PMProjectileRejectReason.ImpactPointNotFinite, reports, false);
                }

                if (!target.VisualOffset.IsFinite)
                {
                    return Reject(PMProjectileRejectReason.VisualOffsetNotFinite, reports, false);
                }

                if (target.VisualOffset.Length > MaxVisualOffsetM)
                {
                    return Reject(PMProjectileRejectReason.VisualOffsetTooLarge, reports, false);
                }
            }

            // ---------------- spec 硬门 ----------------
            if (IsNonFinite(spec.SpeedMps) || spec.SpeedMps < 0f)
            {
                return Reject(PMProjectileRejectReason.SpecInvalid, reports, false);
            }

            if (IsNonFinite(spec.RadiusM) || spec.RadiusM < 0f)
            {
                return Reject(PMProjectileRejectReason.SpecInvalid, reports, false);
            }

            int deferMs = request.ExtraDeferMs;
            if (deferMs < 0)
            {
                deferMs = 0;
                reports.Add("extra-defer-negative-clamped");
            }
            else if (deferMs > PMProjectileLimits.PendingTtlMs)
            {
                // 只 Report 不 Reject：extraDefer 同时放大 L2 预算与帧锚新鲜度窗口，
                // 正常值由 DS 按挂起 Spawn 实测（必然 ≤ PendingTtlMs）。超过 TTL 属异常输入，
                // 必须可观测（否则「预算被放大到等于无约束」这件事会完全无迹可查）。
                reports.Add("extra-defer-exceeds-pending-ttl:" + deferMs);
            }

            // ---------------- L0 激活裁决 ----------------
            bool l0Confirmed;
            if (state.ActivationId == 0u)
            {
                if (state.Key.Origin != PMProjectileOrigin.ServerDirect)
                {
                    // 激活 ID 0 只对服务端直创可信；客户端预测携带 0 → 拒绝（不照搬 UE 号段免校验）。
                    return Reject(PMProjectileRejectReason.ActivationIdZeroNotServerDirect, reports, false);
                }

                l0Confirmed = true;
            }
            else
            {
                if (request.Activation == PMActivationResult.Rejected)
                {
                    return Reject(PMProjectileRejectReason.ActivationRejected, reports, false);
                }

                l0Confirmed = request.Activation == PMActivationResult.Confirmed;
            }

            // ---------------- L1 生命周期 / 墓碑截止 ----------------
            if (state.Stopped)
            {
                if (IsNonFinite(state.TombstoneUntilMs))
                {
                    reports.Add("tombstone-not-finite");
                }
                else if (state.TombstoneUntilMs > 0.0 && request.WorldNowMs > state.TombstoneUntilMs)
                {
                    return Reject(PMProjectileRejectReason.TombstoneExpired, reports, false);
                }
            }

            // L1 配额：主调用者持有计数，本验证器只判、只报是否消耗。
            int usedVerifies = request.UsedVerifyCalls;
            if (usedVerifies < 0)
            {
                usedVerifies = 0;
            }

            if (usedVerifies >= MaxVerifyCalls)
            {
                return Reject(PMProjectileRejectReason.VerifyQuotaExhausted, reports, false);
            }

            bool verifyConsumed = true;

            // ---------------- L2 飞行预算三分支（整包）----------------
            if (!PassesL2Budget(state, spec, batch, deferMs, reports))
            {
                return Reject(PMProjectileRejectReason.TrajectoryBudgetExceeded, reports, verifyConsumed);
            }

            // ---------------- L3 逐目标 ----------------
            List<PMProjectileValidatedHit> hits = new List<PMProjectileValidatedHit>();
            List<PMProjectileSkippedTarget> skipped = new List<PMProjectileSkippedTarget>();
            HashSet<uint> batchSeen = new HashSet<uint>();

            int safeRewindMs = batch.RewindMs;
            if (safeRewindMs < 0)
            {
                safeRewindMs = 0;
            }
            else if (safeRewindMs > MaxRewindMs)
            {
                safeRewindMs = MaxRewindMs;
            }

            uint epoch = state.Key.Epoch;

            for (int i = 0; i < targets.Length; i++)
            {
                PMProjectileHitCandidate target = targets[i];
                uint netId = target.TargetNetId;

                if (Contains(state.HitTargets, netId))
                {
                    Skip(skipped, netId, PMProjectileSkipReason.DuplicateAlreadyHit);
                    continue;
                }

                if (!batchSeen.Add(netId))
                {
                    Skip(skipped, netId, PMProjectileSkipReason.DuplicateInBatch);
                    continue;
                }

                if (state.AllowedTargets != null && state.AllowedTargets.Length > 0
                    && !Contains(state.AllowedTargets, netId))
                {
                    Skip(skipped, netId, PMProjectileSkipReason.NotInWhitelist);
                    continue;
                }

                if (request.History == null)
                {
                    Skip(skipped, netId, PMProjectileSkipReason.HistoryUnavailable);
                    continue;
                }

                PMProjectileTargetSample sample;
                PMProjectileHistoryResolution resolution;
                if (!request.History.TryResolve(epoch, target, request.WorldNowMs, safeRewindMs, deferMs,
                        out sample, out resolution))
                {
                    Skip(skipped, netId, PMProjectileSkipReason.HistoryUnavailable);
                    continue;
                }

                if (sample.Epoch != epoch || sample.NetId != netId
                    || sample.StreamVersion != target.TargetStreamVersion)
                {
                    Skip(skipped, netId, PMProjectileSkipReason.StreamMismatch);
                    continue;
                }

                if (!sample.Position.IsFinite)
                {
                    Skip(skipped, netId, PMProjectileSkipReason.TargetPositionNotFinite);
                    continue;
                }

                // 目标尺寸：radius>0 且 halfHeight>=radius 才可信（否则明确 skip，不静默当小目标）。
                if (IsNonFinite(sample.RadiusM) || sample.RadiusM <= 0f)
                {
                    Skip(skipped, netId, PMProjectileSkipReason.TargetSizeInvalid);
                    continue;
                }

                if (IsNonFinite(sample.HalfHeightM) || sample.HalfHeightM < sample.RadiusM)
                {
                    Skip(skipped, netId, PMProjectileSkipReason.TargetSizeInvalid);
                    continue;
                }

                if (!sample.Alive)
                {
                    Skip(skipped, netId, PMProjectileSkipReason.TargetNotAlive);
                    continue;
                }

                if (resolution == PMProjectileHistoryResolution.Current)
                {
                    reports.Add("history-degraded-to-current:target=" + netId);
                }

                // 位置还原统一 += 视觉偏移。
                PMVector3 center = sample.Position + target.VisualOffset;
                if (!center.IsFinite)
                {
                    Skip(skipped, netId, PMProjectileSkipReason.TargetPositionNotFinite);
                    continue;
                }

                // 竖直胶囊中心线：半长 = halfHeight - radius（Y-up）。
                float halfLength = sample.HalfHeightM - sample.RadiusM;
                PMVector3 bottom = new PMVector3(center.X, center.Y - halfLength, center.Z);
                PMVector3 top = new PMVector3(center.X, center.Y + halfLength, center.Z);

                double threshold = (double)spec.RadiusM + (double)sample.RadiusM + (double)HitToleranceM;
                if (IsNonFinite(threshold) || threshold < 0.0)
                {
                    Skip(skipped, netId, PMProjectileSkipReason.NumericOverflow);
                    continue;
                }

                // ImpactPoint 消毒：只钳不拒（不连坐其他目标）。
                PMVector3 sanitized = ClampImpactToThreshold(target.ImpactPoint, bottom, top, threshold);
                if (!sanitized.IsFinite)
                {
                    Skip(skipped, netId, PMProjectileSkipReason.NumericOverflow);
                    continue;
                }

                // 权威 Filter：必须收到**消毒后**的点。
                if (request.Filter == null)
                {
                    Skip(skipped, netId, PMProjectileSkipReason.FilterUnavailable);
                    continue;
                }

                if (!request.Filter.Accept(state.Key, sample, sanitized))
                {
                    Skip(skipped, netId, PMProjectileSkipReason.FilterRejected);
                    continue;
                }

                // 几何：子弹线段 vs 目标竖直中心线（双精度、覆盖平行/退化）。
                double distance = SegmentSegmentDistance(batch.PreviousPosition, batch.HitPosition, bottom, top);
                if (IsNonFinite(distance))
                {
                    Skip(skipped, netId, PMProjectileSkipReason.NumericOverflow);
                    continue;
                }

                if (distance > threshold)
                {
                    Skip(skipped, netId, PMProjectileSkipReason.GeometryMiss);
                    continue;
                }

                PMProjectileValidatedHit hit = default(PMProjectileValidatedHit);
                hit.TargetNetId = netId;
                hit.TargetStreamVersion = sample.StreamVersion;
                hit.ImpactPoint = sanitized;
                hit.Resolution = resolution;
                hits.Add(hit);
            }

            PMProjectileValidateResult result = new PMProjectileValidateResult();
            result.Status = l0Confirmed ? PMProjectileValidateStatus.Confirmed : PMProjectileValidateStatus.Pending;
            result.Reason = PMProjectileRejectReason.None;
            result.Hits = hits.ToArray();
            result.SkippedTargets = skipped.ToArray();
            result.Reports = reports.ToArray();
            result.VerifyConsumed = verifyConsumed;
            return result;
        }

        /// <summary>标量入参便利重载（等价于填充 <see cref="PMProjectileValidateRequest"/> 后调用）。</summary>
        public static PMProjectileValidateResult Validate(PMProjectileState state, PMProjectileSpec spec,
            PMActivationResult activation, PMProjectileHitBatch batch, IPMProjectileTargetHistory history,
            IPMProjectileHitFilter filter, double worldNowMs, int extraDeferMs, int usedVerifyCalls)
        {
            PMProjectileValidateRequest request = new PMProjectileValidateRequest();
            request.State = state;
            request.Spec = spec;
            request.Activation = activation;
            request.Batch = batch;
            request.History = history;
            request.Filter = filter;
            request.WorldNowMs = worldNowMs;
            request.ExtraDeferMs = extraDeferMs;
            request.UsedVerifyCalls = usedVerifyCalls;
            return Validate(request);
        }

        // ==================================================================
        //  L2 预算
        // ==================================================================

        private static bool PassesL2Budget(PMProjectileState state, PMProjectileSpec spec,
            PMProjectileHitBatch batch, int deferMs, List<string> reports)
        {
            bool skipFlying = spec.SkipFlyingTrajectoryValidation;
            double safeRewindMs = (double)ClampRewind(batch.RewindMs);

            PMVector3 anchor;
            double budgetM;

            if (!state.Stopped)
            {
                if (skipFlying)
                {
                    // skipTrajectoryValidation **只**跳过飞行分支；未停止时无 ② 分支可施加。
                    return true;
                }

                anchor = state.Position;
                budgetM = (double)spec.SpeedMps
                          * (2.0 * safeRewindMs + (double)TickBufferMs + (double)deferMs) / 1000.0;
            }
            else if (deferMs > 0)
            {
                // ②a 停止 + 回放：锚＝权威出生点。
                anchor = state.SpawnPosition;
                budgetM = (double)spec.SpeedMps
                          * (2.0 * safeRewindMs + (double)TickBufferMs + (double)deferMs) / 1000.0;
            }
            else
            {
                // ②b 停止 + 常规：锚＝停止位置。
                anchor = state.Position;
                budgetM = (double)spec.RadiusM + (double)StoppedBodyMarginM + (double)HitToleranceM;
            }

            if (IsNonFinite(budgetM) || budgetM < 0.0)
            {
                reports.Add("l2-budget-overflow");
                return false;
            }

            if (!anchor.IsFinite)
            {
                reports.Add("l2-anchor-not-finite");
                return false;
            }

            double anchorDistance = Distance(anchor, batch.HitPosition);
            if (IsNonFinite(anchorDistance))
            {
                reports.Add("l2-distance-overflow");
                return false;
            }

            if (anchorDistance > budgetM)
            {
                reports.Add("l2-budget-exceeded:anchor=" + anchorDistance.ToString("0.###")
                            + ",budget=" + budgetM.ToString("0.###"));
                return false;
            }

            return true;
        }

        private static int ClampRewind(int rewindMs)
        {
            if (rewindMs < 0)
            {
                return 0;
            }

            if (rewindMs > MaxRewindMs)
            {
                return MaxRewindMs;
            }

            return rewindMs;
        }

        // ==================================================================
        //  L3 几何工具（双精度、fail-closed）
        // ==================================================================

        /// <summary>
        /// 把命中点钳到「目标中心线 + threshold 半径」的球面外沿：阈值内保持原值，阈值外沿
        /// 最近点方向拉到球面。只钳不拒。
        /// </summary>
        private static PMVector3 ClampImpactToThreshold(PMVector3 point, PMVector3 a, PMVector3 b, double threshold)
        {
            PMVector3 closest = ClosestPointOnSegment(point, a, b);
            double dx = (double)point.X - (double)closest.X;
            double dy = (double)point.Y - (double)closest.Y;
            double dz = (double)point.Z - (double)closest.Z;
            double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);

            if (length <= threshold || length <= 1e-12)
            {
                return point;
            }

            double scale = threshold / length;
            return new PMVector3(
                (float)((double)closest.X + dx * scale),
                (float)((double)closest.Y + dy * scale),
                (float)((double)closest.Z + dz * scale));
        }

        private static PMVector3 ClosestPointOnSegment(PMVector3 point, PMVector3 a, PMVector3 b)
        {
            double abx = (double)b.X - (double)a.X;
            double aby = (double)b.Y - (double)a.Y;
            double abz = (double)b.Z - (double)a.Z;
            double lengthSq = abx * abx + aby * aby + abz * abz;

            if (lengthSq <= 1e-18)
            {
                return a;
            }

            double apx = (double)point.X - (double)a.X;
            double apy = (double)point.Y - (double)a.Y;
            double apz = (double)point.Z - (double)a.Z;
            double t = (apx * abx + apy * aby + apz * abz) / lengthSq;
            if (t < 0.0)
            {
                t = 0.0;
            }
            else if (t > 1.0)
            {
                t = 1.0;
            }

            return new PMVector3(
                (float)((double)a.X + abx * t),
                (float)((double)a.Y + aby * t),
                (float)((double)a.Z + abz * t));
        }

        /// <summary>
        /// 线段–线段最短距离（Ericson 稳健算法，双精度）。覆盖：两段皆点、单段退化、
        /// 平行（denom≈0 → 固定 s=0 再解 t）、以及端点钳制。返回非有限表示数值失败。
        /// </summary>
        private static double SegmentSegmentDistance(PMVector3 p1, PMVector3 p2, PMVector3 q1, PMVector3 q2)
        {
            const double Eps = 1e-12;

            double d1x = (double)p2.X - (double)p1.X;
            double d1y = (double)p2.Y - (double)p1.Y;
            double d1z = (double)p2.Z - (double)p1.Z;
            double d2x = (double)q2.X - (double)q1.X;
            double d2y = (double)q2.Y - (double)q1.Y;
            double d2z = (double)q2.Z - (double)q1.Z;
            double rx = (double)p1.X - (double)q1.X;
            double ry = (double)p1.Y - (double)q1.Y;
            double rz = (double)p1.Z - (double)q1.Z;

            double a = d1x * d1x + d1y * d1y + d1z * d1z;
            double e = d2x * d2x + d2y * d2y + d2z * d2z;
            double f = d2x * rx + d2y * ry + d2z * rz;

            double s;
            double t;

            if (a <= Eps && e <= Eps)
            {
                // 两段都退化为点。
                return Math.Sqrt(rx * rx + ry * ry + rz * rz);
            }

            if (a <= Eps)
            {
                // 第一段退化为点：点到第二段的参数。
                s = 0.0;
                t = f / e;
                t = Clamp01(t);
            }
            else
            {
                double c = d1x * rx + d1y * ry + d1z * rz;
                if (e <= Eps)
                {
                    // 第二段退化为点。
                    t = 0.0;
                    s = Clamp01(-c / a);
                }
                else
                {
                    double b = d1x * d2x + d1y * d2y + d1z * d2z;
                    double denom = a * e - b * b;
                    if (denom > Eps)
                    {
                        s = Clamp01((b * f - c * e) / denom);
                    }
                    else
                    {
                        // 平行（含共线）：固定 s=0，交由 t 求解与后续钳制。
                        s = 0.0;
                    }

                    t = (b * s + f) / e;
                    if (t < 0.0)
                    {
                        t = 0.0;
                        s = Clamp01(-c / a);
                    }
                    else if (t > 1.0)
                    {
                        t = 1.0;
                        s = Clamp01((b - c) / a);
                    }
                }
            }

            double cx = ((double)p1.X + d1x * s) - ((double)q1.X + d2x * t);
            double cy = ((double)p1.Y + d1y * s) - ((double)q1.Y + d2y * t);
            double cz = ((double)p1.Z + d1z * s) - ((double)q1.Z + d2z * t);
            return Math.Sqrt(cx * cx + cy * cy + cz * cz);
        }

        private static double Clamp01(double value)
        {
            if (value < 0.0)
            {
                return 0.0;
            }

            if (value > 1.0)
            {
                return 1.0;
            }

            return value;
        }

        private static double Distance(PMVector3 a, PMVector3 b)
        {
            double dx = (double)a.X - (double)b.X;
            double dy = (double)a.Y - (double)b.Y;
            double dz = (double)a.Z - (double)b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // ==================================================================
        //  小工具
        // ==================================================================

        private static PMProjectileValidateResult Reject(PMProjectileRejectReason reason, List<string> reports,
            bool verifyConsumed)
        {
            PMProjectileValidateResult result = new PMProjectileValidateResult();
            result.Status = PMProjectileValidateStatus.Rejected;
            result.Reason = reason;
            // 被拒整个包不得部分输出可以结算的结果。
            result.Hits = new PMProjectileValidatedHit[0];
            result.SkippedTargets = new PMProjectileSkippedTarget[0];
            result.Reports = reports == null ? new string[0] : reports.ToArray();
            result.VerifyConsumed = verifyConsumed;
            return result;
        }

        private static void Skip(List<PMProjectileSkippedTarget> skipped, uint netId, PMProjectileSkipReason reason)
        {
            skipped.Add(new PMProjectileSkippedTarget(netId, reason));
        }

        private static bool Contains(uint[] array, uint value)
        {
            if (array == null)
            {
                return false;
            }

            for (int i = 0; i < array.Length; i++)
            {
                if (array[i] == value)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsNonFinite(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value);
        }

        private static bool IsNonFinite(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value);
        }
    }
}
