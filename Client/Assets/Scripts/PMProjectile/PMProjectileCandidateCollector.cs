// ============================================================================
//  PMProjectileCandidateCollector —— R5-C 客户端候选收集器（纯 C#，零 Unity / 零 PMR3）
// ============================================================================
//
//  契约来源：
//    · Docs/plans/net-r5-network-contract.md 末段「C宿主首个可执行入口」的候选收集一节；
//    · 同一契约的「B2b/C适配可并行边界」；
//    · Tools 侧 PMProjectileValidator（R5-A2）的 L3 几何口径 —— 客户端候选必须与
//      DS 权威判定**同一套几何**，否则上报的候选会被 DS 以 GeometryMiss 全量跳过。
//
//  职责（一句话）：把「本地 owner 自己预测出来的（ClientPredicted）一颗弹的**本子步线段**」
//  对「本帧真实呈现的目标 Y 轴胶囊」做纯数学最近距离判定，产出**候选**（不结算、不扣血），
//  并按 (弹, 目标) 做有界去重；去重只在**发送成功后提交**，发送失败可回滚。
//
//  硬边界（逐条对应契约原文）：
//    1) **只上报，不结算**：本类没有任何伤害 / HP / 资源 / 胜负出口，也不产生结算对象。
//    2) **不接受别人的弹**：`owner` 必须是本地 owner、`origin` 必须是 `ClientPredicted`；
//       `ServerDirect`（DS 直创）与非本地 owner（SP 视角）一律拒绝。
//    3) **目标必须自证**：`ServerFrame` 必须是 `AuthorityServer` 命名空间（**不许**拿本地
//       input 帧冒充 DS 权威帧）、`StreamVersion` 必须非 0、`Epoch` 必须与会话一致、
//       目标必须有可命中尺寸。缺任一项即拒（fail closed），不做"猜测性补齐"。
//    4) **几何与 DS 一致**：阈值 = 弹半径 + 目标半径 + `HitToleranceM`(0.30m)；
//       目标中心线 = 竖直胶囊的轴线段（半长 = `HalfHeightM - RadiusM`，Y-up）；
//       线段–线段最短距离用 Ericson 稳健算法（双精度，覆盖退化/平行/共线）。
//    5) **impact = 候选中心线附近点**：取弹线段上离中心线最近的那个点（必落在阈值内），
//       最终值由 DS 的 L3 消毒钳制决定；`VisualOffset` 恒为 0（首批不支持额外视觉偏移）。
//    6) **有界**：每弹最多记住 100 个目标（已提交 + 在途），key 数最多 1024；
//       满时**拒新**并给出明确原因，绝不无界增长。
//    7) **失败不预占去重**：`TryCollect` 只写入**在途（pending）**集合；只有 `Commit`
//       才把它并入去重账。发送失败时宿主**必须** `Rollback`，否则该 (弹, 目标) 会被
//       永久占位，真实命中再也发不出去。
//    8) **停止只允许最后一段**：`segment.Stopped` 为真时，本次是这颗弹的收官段；该段一旦
//       被提交，后续（迟到/重复）调用一律 `StopSegmentAlreadyReported`。
//
//  为什么不是「只上报 `LocalFake == true` 的弹」：镜像接管后 `LocalFake` 会变成 false，
//  但「这颗弹是不是我自己预测出来的」并没有变。判定只看 owner + origin + 终态，
//  不看接管状态；否则接管瞬间自己的预测来源就再也报不出命中。
//
//  依赖面（硬）：只允许 `System` / `System.Collections.Generic` / `PMNet.Mover`（PMVector3）
//  与 `PMNet.Projectile` 自己的契约类型。**禁止** UnityEngine（PMR5UnityCheck 之类的
//  真 Unity API 编译面）与 PMR3（driver/World）——否则其它测试工程对
//  `PMProjectile/**` 的通配 include 会被本文件拖入循环依赖。
//  Unity 2019.4 = C# 7.3 + netstandard2.0：不使用 Math.Clamp / Span / 元组 / ??=。
// ============================================================================

using System;
using System.Collections.Generic;
using PMNet.Mover;

namespace PMNet.Projectile
{
    /// <summary>
    /// 一条候选被拒的明确原因。**None 只出现在成功收集时**。
    ///
    /// 分段前缀：`Segment*` = 弹线段本身不合格（整次调用拒绝）；`Target*` = 目标样本不合格；
    /// `Already*/Stop*` = 去重账判定；`Geometry*/Numeric*` = 几何结论。
    /// </summary>
    public enum PMProjectileCandidateRejectReason : byte
    {
        None = 0,

        /// <summary>弹的 key 非法（epoch/owner/projectileId 为 0 或 origin 非枚举值）。</summary>
        SegmentKeyInvalid = 1,

        /// <summary>弹的 epoch 与本收集器会话 epoch 不一致（跨世代不得混用）。</summary>
        SegmentEpochMismatch = 2,

        /// <summary>弹的 owner 不是本地 owner（SP 视角看到别人的弹：不上报）。</summary>
        SegmentOwnerNotLocal = 3,

        /// <summary>弹的 origin 不是 ClientPredicted（ServerDirect 由 DS 自己判定，不由客户端上报）。</summary>
        SegmentOriginNotPredicted = 4,

        /// <summary>线段端点非有限值（NaN / ±Infinity），或退化到无法构成线段。</summary>
        SegmentNotFinite = 5,

        /// <summary>弹半径非有限或为负。</summary>
        SegmentRadiusInvalid = 6,

        /// <summary>线段长度超过 DS 的 MaxSegmentM（整包会被 DS 拒，故本地先拒）。</summary>
        SegmentTooLong = 7,

        /// <summary>key 数已达 <see cref="PMProjectileCandidateCollector.MaxKeys"/>：拒绝新 key。</summary>
        KeyCapacityExceeded = 8,

        /// <summary>目标 NetId 为 0。</summary>
        TargetIdInvalid = 9,

        /// <summary>目标是本地 owner 自己（自己不能命中自己）。</summary>
        TargetIsOwner = 10,

        /// <summary>目标 epoch 与会话 epoch 不一致。</summary>
        TargetEpochMismatch = 11,

        /// <summary>目标本帧不可命中（已死亡等）。</summary>
        TargetNotAlive = 12,

        /// <summary>目标尺寸不可信（radius &lt;= 0、非有限，或 halfHeight &lt; radius）。</summary>
        TargetSizeInvalid = 13,

        /// <summary>目标位置非有限值。</summary>
        TargetPositionNotFinite = 14,

        /// <summary>目标 StreamVersion 为 0（未建立输入流）。</summary>
        TargetStreamInvalid = 15,

        /// <summary>
        /// 目标的 ServerFrame 不是 `AuthorityServer` 命名空间的合法帧。
        /// **这是「不拿本地 input 帧冒充 DS 权威帧」的落地判据。**
        /// </summary>
        TargetServerFrameInvalid = 16,

        /// <summary>该弹已记住 <see cref="PMProjectileCandidateCollector.MaxTargetsPerProjectile"/> 个目标：拒绝新目标。</summary>
        TargetCapacityExceeded = 17,

        /// <summary>该 (弹, 目标) 已经**提交**过去重账（不重复上报）。</summary>
        AlreadyCommitted = 18,

        /// <summary>该 (弹, 目标) 正在**在途**（已收集、尚未 Commit/Rollback）。</summary>
        AlreadyPending = 19,

        /// <summary>该弹的停止段已经被提交过：不再接受任何候选。</summary>
        StopSegmentAlreadyReported = 20,

        /// <summary>线段到目标中心线的最近距离超出阈值（真实未命中，不是错误）。</summary>
        GeometryMiss = 21,

        /// <summary>中间量非有限（数值溢出）：fail closed，不当成命中。</summary>
        NumericOverflow = 22,
    }

    /// <summary>
    /// 一次候选收集的输入：本地 owner 的一颗 `ClientPredicted` 弹的**本子步线段**。
    ///
    /// 单位与坐标：Y-up、米（与 <see cref="PMVector3"/> 一致）。
    /// `PreviousPosition` → `Position` 就是这一子步弹真正走过的线段（**不是**整帧外推，
    /// 也不是本地 input 帧推导出来的位置）。
    /// </summary>
    public struct PMProjectileCandidateSegment
    {
        public PMProjectileKey Key;

        /// <summary>本子步起点（上一子步的终点/出生点）。</summary>
        public PMVector3 PreviousPosition;

        /// <summary>本子步终点（DS 的 L2 预算锚点也用它）。</summary>
        public PMVector3 Position;

        /// <summary>弹半径（米）。来自可信 spec；由宿主注入，本类不再校验其来源。</summary>
        public float RadiusM;

        /// <summary>
        /// 该弹是否已进入终态（停止 / 本地预测结束）。为真时本次是该弹的**收官段**：
        /// 提交后本 key 不再接受任何候选。
        /// </summary>
        public bool Stopped;
    }

    /// <summary>
    /// 客户端候选收集器。**纯 C#、无状态机副作用、非线程安全**（只在客户端主线程使用）。
    ///
    /// 生命周期：会话级（与 PMR5ProjectileDriver 同寿命）。构造时冻结 `epoch` 与
    /// `localOwnerNetId`（本地 owner 的 NetId 只有在其副本复制下来之后才可知，因此宿主
    /// 必须**在拿到本地 owner 之后**再构造本类）；会话退出必须 `Clear()`。
    /// </summary>
    public sealed class PMProjectileCandidateCollector
    {
        /// <summary>每弹去重上限（契约：每弹每 target dedup 有界 100）。</summary>
        public const int MaxTargetsPerProjectile = 100;

        /// <summary>key 数上限（契约：key 数有界 1024）。</summary>
        public const int MaxKeys = 1024;

        /// <summary>命中容差（米）。与 DS 的 `PMProjectileValidator.HitToleranceM` 同值。</summary>
        public const float HitToleranceM = PMProjectileLimits.HitToleranceM;

        /// <summary>线段长度上限（米）。与 DS 的 `PMProjectileValidator.MaxSegmentM` 同值。</summary>
        public const float MaxSegmentM = PMProjectileLimits.MaxSegmentM;

        /// <summary>单个 key 的去重账：已提交集合 + 在途候选（含停止段标记）。</summary>
        private sealed class Ledger
        {
            public readonly HashSet<uint> Committed = new HashSet<uint>();
            public readonly List<PMProjectileHitCandidate> Pending = new List<PMProjectileHitCandidate>(4);
            public bool StopCommitted;
            public bool StopPending;
        }

        private readonly Dictionary<PMProjectileKey, Ledger> _ledgers =
            new Dictionary<PMProjectileKey, Ledger>();

        private readonly uint _epoch;
        private readonly uint _localOwnerNetId;

        // ---- 可观测计数（只增；供诊断与测试断言，不参与判定）----
        public long Collected { get { return _collected; } }
        public long Rejected { get { return _rejected; } }
        public long Commits { get { return _commits; } }
        public long Rollbacks { get { return _rollbacks; } }
        public long ForgottenKeys { get { return _forgottenKeys; } }

        private long _collected;
        private long _rejected;
        private long _commits;
        private long _rollbacks;
        private long _forgottenKeys;

        /// <param name="epoch">会话 epoch（非 0）。跨 epoch 的弹/目标一律拒绝。</param>
        /// <param name="localOwnerNetId">本地 owner 副本的 NetId（非 0）。构造后不可变。</param>
        public PMProjectileCandidateCollector(uint epoch, uint localOwnerNetId)
        {
            if (epoch == 0u)
            {
                throw new ArgumentOutOfRangeException("epoch",
                    "[PMProjectileCandidateCollector] epoch 不得为 0（0 表示未建立会话）。");
            }

            if (localOwnerNetId == 0u)
            {
                throw new ArgumentOutOfRangeException("localOwnerNetId",
                    "[PMProjectileCandidateCollector] 本地 owner NetId 不得为 0（等待副本复制后再构造本类）。");
            }

            _epoch = epoch;
            _localOwnerNetId = localOwnerNetId;
        }

        /// <summary>本实例固定的会话 epoch。</summary>
        public uint Epoch { get { return _epoch; } }

        /// <summary>本实例认定的本地 owner NetId。</summary>
        public uint LocalOwnerNetId { get { return _localOwnerNetId; } }

        /// <summary>仍被记住的弹 key 数（≤ <see cref="MaxKeys"/>）。</summary>
        public int KeyCount { get { return _ledgers.Count; } }

        /// <summary>仍有在途候选（未 Commit/Rollback）的 key 数。</summary>
        public int PendingKeyCount
        {
            get
            {
                int n = 0;
                foreach (KeyValuePair<PMProjectileKey, Ledger> kv in _ledgers)
                {
                    if (kv.Value.Pending.Count > 0 || kv.Value.StopPending) { n++; }
                }

                return n;
            }
        }

        // ==================================================================
        //  收集
        // ==================================================================

        /// <summary>
        /// 收集一条候选（弹线段 × 单个目标样本）。
        ///
        /// 成功时把候选写入该 key 的**在途**集合，并返回 true；
        /// 失败时返回 false 并给出 <paramref name="reason"/>（**不修改任何账**）。
        /// </summary>
        public bool TryCollect(PMProjectileCandidateSegment segment, PMProjectileTargetSample target,
            out PMProjectileHitCandidate candidate, out PMProjectileCandidateRejectReason reason)
        {
            candidate = default(PMProjectileHitCandidate);
            reason = PMProjectileCandidateRejectReason.None;

            // ---- 弹线段自证 ----
            if (!segment.Key.IsValid)
            {
                reason = PMProjectileCandidateRejectReason.SegmentKeyInvalid;
                _rejected++;
                return false;
            }

            if (segment.Key.Epoch != _epoch)
            {
                reason = PMProjectileCandidateRejectReason.SegmentEpochMismatch;
                _rejected++;
                return false;
            }

            if (segment.Key.OwnerNetId != _localOwnerNetId)
            {
                reason = PMProjectileCandidateRejectReason.SegmentOwnerNotLocal;
                _rejected++;
                return false;
            }

            if (segment.Key.Origin != PMProjectileOrigin.ClientPredicted)
            {
                reason = PMProjectileCandidateRejectReason.SegmentOriginNotPredicted;
                _rejected++;
                return false;
            }

            if (!segment.PreviousPosition.IsFinite || !segment.Position.IsFinite)
            {
                reason = PMProjectileCandidateRejectReason.SegmentNotFinite;
                _rejected++;
                return false;
            }

            if (IsNonFinite(segment.RadiusM) || segment.RadiusM < 0f)
            {
                reason = PMProjectileCandidateRejectReason.SegmentRadiusInvalid;
                _rejected++;
                return false;
            }

            double segmentM = Distance(segment.PreviousPosition, segment.Position);
            if (IsNonFinite(segmentM))
            {
                reason = PMProjectileCandidateRejectReason.NumericOverflow;
                _rejected++;
                return false;
            }

            if (segmentM > (double)MaxSegmentM)
            {
                // 超长线段会让 DS 的整包 Reject（SegmentTooLong）：本地先拒，不去浪费一次上行配额。
                reason = PMProjectileCandidateRejectReason.SegmentTooLong;
                _rejected++;
                return false;
            }

            // ---- key 容量（只有新 key 才受限；已有 key 允许继续写）----
            Ledger ledger;
            bool exists = _ledgers.TryGetValue(segment.Key, out ledger);
            if (!exists && _ledgers.Count >= MaxKeys)
            {
                reason = PMProjectileCandidateRejectReason.KeyCapacityExceeded;
                _rejected++;
                return false;
            }

            // ---- 目标自证 ----
            if (target.NetId == 0u)
            {
                reason = PMProjectileCandidateRejectReason.TargetIdInvalid;
                _rejected++;
                return false;
            }

            if (target.NetId == _localOwnerNetId)
            {
                reason = PMProjectileCandidateRejectReason.TargetIsOwner;
                _rejected++;
                return false;
            }

            if (target.Epoch != _epoch)
            {
                reason = PMProjectileCandidateRejectReason.TargetEpochMismatch;
                _rejected++;
                return false;
            }

            if (target.StreamVersion == 0u)
            {
                reason = PMProjectileCandidateRejectReason.TargetStreamInvalid;
                _rejected++;
                return false;
            }

            if (!target.ServerFrame.IsValid || target.ServerFrame.Domain != PMFrameDomain.AuthorityServer)
            {
                // 「不拿本地 input frame 冒充 DS 权威帧」的唯一判据：锚必须真的是权威帧。
                reason = PMProjectileCandidateRejectReason.TargetServerFrameInvalid;
                _rejected++;
                return false;
            }

            if (!target.Alive)
            {
                reason = PMProjectileCandidateRejectReason.TargetNotAlive;
                _rejected++;
                return false;
            }

            if (!target.Position.IsFinite)
            {
                reason = PMProjectileCandidateRejectReason.TargetPositionNotFinite;
                _rejected++;
                return false;
            }

            // 尺寸：radius>0 且 halfHeight>=radius（与 DS 的 TargetSizeInvalid 同判据）。
            if (IsNonFinite(target.RadiusM) || target.RadiusM <= 0f)
            {
                reason = PMProjectileCandidateRejectReason.TargetSizeInvalid;
                _rejected++;
                return false;
            }

            if (IsNonFinite(target.HalfHeightM) || target.HalfHeightM < target.RadiusM)
            {
                reason = PMProjectileCandidateRejectReason.TargetSizeInvalid;
                _rejected++;
                return false;
            }

            // ---- 去重账（在途 + 已提交；停止段的收官约束）----
            if (exists)
            {
                if (ledger.StopCommitted)
                {
                    reason = PMProjectileCandidateRejectReason.StopSegmentAlreadyReported;
                    _rejected++;
                    return false;
                }

                if (ledger.Committed.Contains(target.NetId))
                {
                    reason = PMProjectileCandidateRejectReason.AlreadyCommitted;
                    _rejected++;
                    return false;
                }

                if (ContainsPending(ledger, target.NetId))
                {
                    reason = PMProjectileCandidateRejectReason.AlreadyPending;
                    _rejected++;
                    return false;
                }
            }

            // ---- 几何（与 DS 的 L3 完全一致的阈值与中心线）----
            float halfLength = target.HalfHeightM - target.RadiusM;
            PMVector3 bottom = new PMVector3(target.Position.X, target.Position.Y - halfLength, target.Position.Z);
            PMVector3 top = new PMVector3(target.Position.X, target.Position.Y + halfLength, target.Position.Z);

            double threshold = (double)segment.RadiusM + (double)target.RadiusM + (double)HitToleranceM;
            if (IsNonFinite(threshold) || threshold < 0.0)
            {
                reason = PMProjectileCandidateRejectReason.NumericOverflow;
                _rejected++;
                return false;
            }

            double s;
            double t;
            double distance = ClosestSegmentPoints(segment.PreviousPosition, segment.Position, bottom, top, out s, out t);
            if (IsNonFinite(distance))
            {
                reason = PMProjectileCandidateRejectReason.NumericOverflow;
                _rejected++;
                return false;
            }

            if (distance > threshold)
            {
                reason = PMProjectileCandidateRejectReason.GeometryMiss;
                _rejected++;
                return false;
            }

            // ---- 容量（目标级）----
            if (exists && ledger.Committed.Count + ledger.Pending.Count >= MaxTargetsPerProjectile)
            {
                reason = PMProjectileCandidateRejectReason.TargetCapacityExceeded;
                _rejected++;
                return false;
            }

            // ---- 产出候选（**只写到在途**，未提交前不参与去重）----
            PMVector3 impact = PointOnSegment(segment.PreviousPosition, segment.Position, s);
            if (!impact.IsFinite)
            {
                reason = PMProjectileCandidateRejectReason.NumericOverflow;
                _rejected++;
                return false;
            }

            if (!exists)
            {
                ledger = new Ledger();
                _ledgers.Add(segment.Key, ledger);
            }

            candidate.TargetNetId = target.NetId;
            candidate.TargetStreamVersion = target.StreamVersion;
            candidate.TargetServerFrame = target.ServerFrame;
            candidate.ImpactPoint = impact;
            candidate.VisualOffset = PMVector3.Zero;   // 首批不支持额外视觉偏移。

            ledger.Pending.Add(candidate);
            if (segment.Stopped) { ledger.StopPending = true; }

            _collected++;
            return true;
        }

        /// <summary>
        /// 批量收集：对同一弹线段依次尝试 <paramref name="targets"/> 里的每个目标样本，
        /// 把成功的候选**追加**到 <paramref name="results"/>，返回本次追加的条数。
        ///
        /// 单个目标的失败（含 GeometryMiss）只影响该目标，不影响同批其它目标。
        /// </summary>
        public int Collect(PMProjectileCandidateSegment segment, IList<PMProjectileTargetSample> targets,
            List<PMProjectileHitCandidate> results)
        {
            if (targets == null || results == null) { return 0; }

            int before = results.Count;
            for (int i = 0; i < targets.Count; i++)
            {
                PMProjectileHitCandidate candidate;
                PMProjectileCandidateRejectReason reason;
                if (TryCollect(segment, targets[i], out candidate, out reason))
                {
                    results.Add(candidate);
                }
            }

            return results.Count - before;
        }

        // ==================================================================
        //  提交 / 回滚 / 退休
        // ==================================================================

        /// <summary>
        /// 把该 key 的**在途**候选并入去重账（**只允许在上行发送成功之后调用**）。
        ///
        /// 返回 true 表示确有在途内容被提交（含仅提交「停止段」标记）；false 表示没有在途内容。
        /// </summary>
        public bool Commit(PMProjectileKey key)
        {
            Ledger ledger;
            if (!_ledgers.TryGetValue(key, out ledger)) { return false; }

            bool had = ledger.Pending.Count > 0 || ledger.StopPending;

            for (int i = 0; i < ledger.Pending.Count; i++)
            {
                ledger.Committed.Add(ledger.Pending[i].TargetNetId);
            }

            ledger.Pending.Clear();

            if (ledger.StopPending)
            {
                ledger.StopPending = false;
                ledger.StopCommitted = true;
            }

            if (had) { _commits++; }
            return had;
        }

        /// <summary>
        /// 丢弃该 key 的**在途**候选（上行发送失败 / 未发送时调用）。
        ///
        /// 这是「失败发送不能永久预占去重」的落点：回滚后同一 (弹, 目标) 可以被重新收集。
        /// 返回 true 表示确有在途内容被丢弃。
        /// </summary>
        public bool Rollback(PMProjectileKey key)
        {
            Ledger ledger;
            if (!_ledgers.TryGetValue(key, out ledger)) { return false; }

            bool had = ledger.Pending.Count > 0 || ledger.StopPending;
            ledger.Pending.Clear();
            ledger.StopPending = false;

            if (had) { _rollbacks++; }
            return had;
        }

        /// <summary>
        /// 彻底忘掉该 key（视图被移除 / 镜像销毁 / owner 退出时调用）。
        ///
        /// 忘掉之后该 key 的去重账不再存在：若同一 key 之后又被重新收集（例如迟到的镜像
        /// 让视图复活），去重从零开始 —— 重复的上行由 DS 的 `state.HitTargets` 兜底去重。
        /// </summary>
        public bool Forget(PMProjectileKey key)
        {
            if (!_ledgers.Remove(key)) { return false; }
            _forgottenKeys++;
            return true;
        }

        /// <summary>清空全部账（会话退出）。计数保留（诊断对账用）。</summary>
        public void Clear()
        {
            _ledgers.Clear();
        }

        // ==================================================================
        //  观测
        // ==================================================================

        /// <summary>该 (弹) 已提交去重的目标数（0 = 未记录）。</summary>
        public int CommittedTargetCount(PMProjectileKey key)
        {
            Ledger ledger;
            return _ledgers.TryGetValue(key, out ledger) ? ledger.Committed.Count : 0;
        }

        /// <summary>该 (弹) 在途（未提交/未回滚）的候选数（0 = 无）。</summary>
        public int PendingTargetCount(PMProjectileKey key)
        {
            Ledger ledger;
            return _ledgers.TryGetValue(key, out ledger) ? ledger.Pending.Count : 0;
        }

        /// <summary>该 (弹) 是否有在途内容（候选或停止段标记）。</summary>
        public bool HasPending(PMProjectileKey key)
        {
            Ledger ledger;
            return _ledgers.TryGetValue(key, out ledger) && (ledger.Pending.Count > 0 || ledger.StopPending);
        }

        /// <summary>把该 (弹) 的在途候选按收集顺序拷进 <paramref name="buffer"/>，返回拷贝条数。</summary>
        public int CopyPending(PMProjectileKey key, PMProjectileHitCandidate[] buffer)
        {
            if (buffer == null) { return 0; }

            Ledger ledger;
            if (!_ledgers.TryGetValue(key, out ledger)) { return 0; }

            int n = ledger.Pending.Count < buffer.Length ? ledger.Pending.Count : buffer.Length;
            for (int i = 0; i < n; i++) { buffer[i] = ledger.Pending[i]; }
            return n;
        }

        /// <summary>单行诊断摘要。</summary>
        public string Describe()
        {
            return "PMProjectileCandidateCollector epoch=" + _epoch
                   + " localOwner=" + _localOwnerNetId
                   + " keys=" + _ledgers.Count
                   + " pendingKeys=" + PendingKeyCount
                   + " collected=" + _collected
                   + " rejected=" + _rejected
                   + " commits=" + _commits
                   + " rollbacks=" + _rollbacks
                   + " forgotten=" + _forgottenKeys;
        }

        // ==================================================================
        //  几何工具（双精度、fail-closed；与 DS 的 L3 口径逐式对应）
        // ==================================================================

        private static bool ContainsPending(Ledger ledger, uint netId)
        {
            for (int i = 0; i < ledger.Pending.Count; i++)
            {
                if (ledger.Pending[i].TargetNetId == netId) { return true; }
            }

            return false;
        }

        private static PMVector3 PointOnSegment(PMVector3 a, PMVector3 b, double s)
        {
            return new PMVector3(
                (float)((double)a.X + ((double)b.X - (double)a.X) * s),
                (float)((double)a.Y + ((double)b.Y - (double)a.Y) * s),
                (float)((double)a.Z + ((double)b.Z - (double)a.Z) * s));
        }

        /// <summary>
        /// 线段–线段最短距离（Ericson 稳健算法，双精度），并回传两条线段上的参数
        /// <paramref name="s"/> / <paramref name="t"/>（均已被钳到 [0,1]）。
        ///
        /// 与 DS 的 `PMProjectileValidator.SegmentSegmentDistance` **同一算法**（只是多回传参数），
        /// 因此「客户端认为命中」与「DS 认为命中」在同一输入上必然一致。
        /// 覆盖：两段皆退化点、单段退化、平行/共线（denom≈0 → 固定 s=0 再解 t）与端点钳制。
        /// </summary>
        private static double ClosestSegmentPoints(PMVector3 p1, PMVector3 p2, PMVector3 q1, PMVector3 q2,
            out double s, out double t)
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

            s = 0.0;
            t = 0.0;

            if (a <= Eps && e <= Eps)
            {
                // 两段都退化为点。
                return Math.Sqrt(rx * rx + ry * ry + rz * rz);
            }

            if (a <= Eps)
            {
                // 第一段退化为点：点到第二段的参数。
                s = 0.0;
                t = Clamp01(f / e);
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
            if (value < 0.0) { return 0.0; }
            if (value > 1.0) { return 1.0; }
            return value;
        }

        private static double Distance(PMVector3 a, PMVector3 b)
        {
            double dx = (double)a.X - (double)b.X;
            double dy = (double)a.Y - (double)b.Y;
            double dz = (double)a.Z - (double)b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static bool IsNonFinite(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value);
        }

        private static bool IsNonFinite(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value);
        }
    }
}
