// ============================================================================
//  PMProjectile 三类 Pending 暂存（R5-A1 / M09）
// ============================================================================
//  契约来源（只读）：
//    · Docs/plans/net-r5-projectile-contract.md「语义与有限资源（R0 适配修订）」
//    · Docs/plans/_r5_semantics_survey.md §1.3（三类 Pending 的 UE 语义）
//    · Docs/plans/net-architecture-migration.md §3.9.4「投射物与权威裁决」
//    · Client/Assets/Scripts/PMProjectile/PMProjectileContracts.cs（冻结共享类型）
//
//  三类等待（**用途不同，不能合并成一个队列**）：
//    A PMProjectilePendingSpawns   生成源激活了吗        键 = 完整 PMProjectileKey
//                                  128/owner、总 1024、TTL 2000ms、容量满拒新
//    B PMProjectilePendingVerifies Verify 先到但弹还没生成 键 = PMProjectileKey
//                                  单 key 5 / 总 128、FIFO、TTL 随 Spawn(2000ms)
//    C PMProjectilePendingHits     命中已成立但激活还 Pending 键 = PMProjectileKey
//                                  128 组、同 key 合并不刷新原始时刻、满丢最旧并统计、TTL 2000ms
//
//  「原始上报」与「可信结论」的严格分界（这是本文件最容易搞混的地方）：
//    · B 存的是**原始上行**（PMProjectileVerifyRequest：含未消毒的 ImpactPoint / VisualOffset /
//      候选目标 / RewindMs）——因为弹还没生成，必须原样留着，等生成后再走 L0–L4 校验。
//      所以 B **刻意不做几何消毒**（NaN / 超长线段 / 越界视觉偏移由 A2 的 L0 判 Rejected，
//      如果 A1 先静默拒掉，调用方就再也看不到「整包 Rejected」这个结果了）。
//    · C 存的是**已消毒结论**（PMProjectileValidatedHitSet -> PMProjectileValidatedHit[]）——
//      契约 L0–L4 的产物。C 只接受这个类型，原始 PMProjectileHitBatch 不许进来；
//      否则「结算数据」里可能混进未过滤的坐标，Pending 的“不结算”保证就只剩命名。
//
//  硬语义（逐条对应契约，测试逐条钉住）：
//    1) Pending 期间**绝不结算伤害**：三个类都是纯数据，没有伤害出口、没有外部回调。
//    2) 所有会「消费」状态的 API 走**独占返回列表**（调用方拥有、可自由改写）；
//       去重提交/出队/标记清除都发生在返回之前，因此不存在「回调里重入导致重复结算」的窗口。
//    3) B 的回放：**先解除 pending 标记**，再按 FIFO 返回条目（MarkerCleared 体现顺序语义）；
//       且要求调用方传入已登记的 PMProjectileLifecycle（对应「回放必须在注册之后」）。
//    4) C 的合并**不刷新原始时刻**；Confirm 只兑现一次，Reject 也是终态（同样打已兑现标记）；
//       Confirm 之后的重复暂存/重复确认都拿不到第二份结算数据；TTL 不结算。
//    5) TTL / 挂起时长一律用墙钟（double 毫秒），非法/倒退显式拒绝（同 PMProjectileWallClock）。
//    6) 所有容器有界：溢出都产生可观测计数，不用 TTL 代替容量。
//    7) A 的键是完整 PMProjectileKey：同一个 activation / 同一个生成源必须能产出**多颗**散弹，
//       只有「同一个 key 重复入队」才算重复（原来的 (owner, source) 键会把散弹挡成
//       只剩一颗 —— 与本项目「一次激活多颗弹」的语义直接冲突）。
// ============================================================================

using System;
using System.Collections.Generic;
using PMNet.Mover;

namespace PMNet.Projectile
{
    /// <summary>本项目自有的 Pending 相关上限（共享契约未覆盖的部分）。</summary>
    public static class PMProjectilePendingLimits
    {
        /// <summary>
        /// 挂起 Spawn 的**全局**上限 = 1024。
        ///
        /// 口径说明（主侧明确要求，不允许无说明地放大）：契约「权威/预测活对象各上限 1024」
        /// 是这一族的容量口径；挂起项属于「已受理但尚未落地的弹」，与活对象共享同一份 1024，
        /// 而不是 owner 上限 × 每 owner 上限 = 64×128 = 8192（8192 在契约里没有任何依据）。
        /// 每 owner 的 128 仍然独立生效（契约明示），两者取小。
        /// </summary>
        public const int MaxPendingSpawnsTotal = PMProjectileLimits.MaxProjectiles;

        /// <summary>同一 key 的命中结论文档条数上限（无界追加会让「128 组」失去意义）。</summary>
        public const int MaxHitSetsPerKey = 64;

        /// <summary>已兑现 key 的标记环容量（用于 Confirm/Reject 只兑现一次的判定）。</summary>
        public const int MaxResolvedHitMarkers = 2048;
    }

    // ==================================================================== A
    //  挂起 Spawn：生成源尚未激活，不生成幽灵弹
    // ====================================================================

    /// <summary>一条挂起 Spawn 的冻结副本。</summary>
    public sealed class PMProjectilePendingSpawn
    {
        /// <summary>完整身份 (Epoch, OwnerNetId, ProjectileId, Origin)。**同一 key 只能挂一条**。</summary>
        public PMProjectileKey Key;

        /// <summary>
        /// 生成源实例 ID。**不参与唯一性**：一次激活/一个源可以产出多颗散弹，
        /// 它只用于诊断与「同一源产出的弹」的分组观察。
        /// </summary>
        public uint SourceBehaviorInstanceId;

        /// <summary>解挂依据：同一 (owner, activationId) 一次解挂该源的**全部**弹。</summary>
        public uint ActivationId;

        public uint AuthorityNetId;
        public int PredictionMs;
        public double EnqueuedWallTimeMs;
        public PMProjectileSpawnRequest Request = new PMProjectileSpawnRequest();

        public PMProjectilePendingSpawn Clone()
        {
            PMProjectilePendingSpawn copy = (PMProjectilePendingSpawn)MemberwiseClone();
            copy.Request = Request == null ? new PMProjectileSpawnRequest() : Request.Clone();
            return copy;
        }
    }

    public struct PMProjectilePendingSpawnRelease
    {
        /// <summary>深 clone 的挂起项（丢弃路径为 null）。</summary>
        public PMProjectilePendingSpawn Spawn;

        public PMProjectileKey Key;
        public PMActivationResult Outcome;
        public PMProjectileReleaseReason Reason;

        /// <summary>挂起时长（毫秒），须叠加进追赶与校验窗口。</summary>
        public int ExtraDeferMs;
        public double PendingElapsedMs;
        public double WallTimeMs;
    }

    public enum PMProjectilePendingSpawnResult : byte
    {
        Enqueued = 0,

        /// <summary>**同一个完整 key** 重复入队（不是「同一个生成源」）。</summary>
        DuplicateKey = 1,

        OwnerCapacity = 2,
        TotalCapacity = 3,
        StaleEpoch = 4,
        Invalid = 5,
        InvalidClock = 6,
    }

    public struct PMProjectilePendingSpawnOutcome
    {
        public PMProjectilePendingSpawnResult Result;
        public PMProjectileKey Key;
        public int OwnerCount;
        public int TotalCount;
        public PMProjectileClockCheck Clock;
    }

    /// <summary>
    /// 等待激活的挂起 Spawn（键 = 完整 PMProjectileKey；128/owner、总 1024、TTL 2000ms、
    /// 容量满**拒新**并产生明确结果）。
    ///
    /// 与 UE 的「追加处无 cap」有意偏离（D-R5-01）：本项目选择有界 + 拒新 + 可观测结果。
    /// 与初版实现的关键差别：唯一性按**完整 key**而不是 (owner, source) —— 同一生成源
    /// 发出一波散弹时，每颗弹是一个独立挂起项，解挂时按 activationId 一起解挂。
    /// </summary>
    public sealed class PMProjectilePendingSpawns
    {
        private readonly Dictionary<PMProjectileKey, PMProjectilePendingSpawn> _entries =
            new Dictionary<PMProjectileKey, PMProjectilePendingSpawn>();
        private readonly Dictionary<uint, List<PMProjectileKey>> _byOwner =
            new Dictionary<uint, List<PMProjectileKey>>();

        private uint _epoch;
        private double _lastWallMs;
        private int _rejectedByCapacity;

        public PMProjectilePendingSpawns(uint epoch)
        {
            if (epoch == 0u)
            {
                throw new ArgumentOutOfRangeException("epoch", "[PMProjectilePendingSpawns] epoch 0 无效。");
            }

            _epoch = epoch;
        }

        public uint Epoch { get { return _epoch; } }

        public int Count { get { return _entries.Count; } }

        public int OwnerCount { get { return _byOwner.Count; } }

        public int RejectedByCapacityCount { get { return _rejectedByCapacity; } }

        public int CountForOwner(uint ownerNetId)
        {
            List<PMProjectileKey> list;
            if (!_byOwner.TryGetValue(ownerNetId, out list)) { return 0; }
            return list.Count;
        }

        /// <summary>同一生成源当前挂起的弹数（散弹诊断用；不参与容量判定）。</summary>
        public int CountForSource(uint ownerNetId, uint sourceBehaviorInstanceId)
        {
            List<PMProjectileKey> list;
            if (!_byOwner.TryGetValue(ownerNetId, out list)) { return 0; }
            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                PMProjectilePendingSpawn e;
                if (_entries.TryGetValue(list[i], out e) && e.SourceBehaviorInstanceId == sourceBehaviorInstanceId)
                {
                    n++;
                }
            }

            return n;
        }

        public bool Contains(PMProjectileKey key)
        {
            return _entries.ContainsKey(key);
        }

        public bool TryGet(PMProjectileKey key, out PMProjectilePendingSpawn clone)
        {
            PMProjectilePendingSpawn e;
            if (!_entries.TryGetValue(key, out e))
            {
                clone = null;
                return false;
            }

            clone = e.Clone();
            return true;
        }

        /// <summary>
        /// 入队。唯一性 = 完整 <paramref name="key"/>：**同一 (owner, source) 的不同 projectileId
        /// 必须全部通过**（散弹），只有同一 key 重复入队才返回 DuplicateKey 并保留**原始**挂起时刻。
        /// </summary>
        public PMProjectilePendingSpawnOutcome TryEnqueue(
            PMProjectileKey key,
            uint sourceBehaviorInstanceId,
            uint activationId,
            uint authorityNetId,
            int predictionMs,
            PMProjectileSpawnRequest request,
            double wallNowMs)
        {
            PMProjectilePendingSpawnOutcome o = new PMProjectilePendingSpawnOutcome();
            o.Key = key;
            o.Clock = PMProjectileClockCheck.Ok;

            PMProjectileClockCheck clock = PMProjectileWallClock.Validate(wallNowMs, _lastWallMs);
            o.Clock = clock;
            if (clock != PMProjectileClockCheck.Ok)
            {
                o.Result = PMProjectilePendingSpawnResult.InvalidClock;
                return o;
            }

            if (key.Epoch != _epoch)
            {
                o.Result = PMProjectilePendingSpawnResult.StaleEpoch;
                return o;
            }

            // activationId=0 只对「可信 ServerDirect 且按构造即 Confirmed」成立；
            // 一条等待激活的挂起项配 0 永远解不开（TrySetActivationResult 拒绝 0），是无效输入。
            // source=0 同理：挂起 Spawn 的语义就是「等某个生成源激活」，0 号源不存在。
            if (!key.IsValid || activationId == 0u || sourceBehaviorInstanceId == 0u || predictionMs < 0)
            {
                o.Result = PMProjectilePendingSpawnResult.Invalid;
                return o;
            }

            // finite + 身份一致：载荷不能把别的弹的状态带进来。
            if (request != null
                && (!PMProjectileFinite.State(request.State) || !PMProjectileFinite.Spec(request.Spec)))
            {
                o.Result = PMProjectilePendingSpawnResult.Invalid;
                return o;
            }

            if (request != null && request.State != null && request.State.Key.IsValid
                && !request.State.Key.Equals(key))
            {
                o.Result = PMProjectilePendingSpawnResult.Invalid;
                return o;
            }

            if (_entries.ContainsKey(key))
            {
                o.Result = PMProjectilePendingSpawnResult.DuplicateKey;
                o.OwnerCount = CountForOwner(key.OwnerNetId);
                o.TotalCount = _entries.Count;
                return o;
            }

            if (CountForOwner(key.OwnerNetId) >= PMProjectileLimits.MaxPendingSpawnsPerOwner)
            {
                _rejectedByCapacity++;
                o.Result = PMProjectilePendingSpawnResult.OwnerCapacity;
                o.OwnerCount = CountForOwner(key.OwnerNetId);
                o.TotalCount = _entries.Count;
                return o;
            }

            if (_entries.Count >= PMProjectilePendingLimits.MaxPendingSpawnsTotal)
            {
                _rejectedByCapacity++;
                o.Result = PMProjectilePendingSpawnResult.TotalCapacity;
                o.OwnerCount = CountForOwner(key.OwnerNetId);
                o.TotalCount = _entries.Count;
                return o;
            }

            PMProjectilePendingSpawn entry = new PMProjectilePendingSpawn();
            entry.Key = key;
            entry.SourceBehaviorInstanceId = sourceBehaviorInstanceId;
            entry.ActivationId = activationId;
            entry.AuthorityNetId = authorityNetId;
            entry.PredictionMs = predictionMs;
            entry.EnqueuedWallTimeMs = wallNowMs;
            entry.Request = request == null ? new PMProjectileSpawnRequest() : request.Clone();

            _entries.Add(key, entry);
            List<PMProjectileKey> list;
            if (!_byOwner.TryGetValue(key.OwnerNetId, out list))
            {
                list = new List<PMProjectileKey>();
                _byOwner.Add(key.OwnerNetId, list);
            }

            list.Add(key);
            AcceptWallClock(wallNowMs);
            o.Result = PMProjectilePendingSpawnResult.Enqueued;
            o.OwnerCount = list.Count;
            o.TotalCount = _entries.Count;
            return o;
        }

        /// <summary>TTL 到期清理（墙钟）。清除的条目**不生成任何东西**。返回条数。</summary>
        public int PurgeExpired(double wallNowMs, out PMProjectilePendingSpawnRelease[] dropped)
        {
            dropped = new PMProjectilePendingSpawnRelease[0];
            if (PMProjectileWallClock.Validate(wallNowMs, _lastWallMs) != PMProjectileClockCheck.Ok)
            {
                return 0;
            }

            AcceptWallClock(wallNowMs);
            List<PMProjectileKey> expired = null;
            foreach (KeyValuePair<PMProjectileKey, PMProjectilePendingSpawn> kv in _entries)
            {
                if (wallNowMs - kv.Value.EnqueuedWallTimeMs < PMProjectileLimits.PendingTtlMs) { continue; }
                if (expired == null) { expired = new List<PMProjectileKey>(); }
                expired.Add(kv.Key);
            }

            if (expired == null) { return 0; }

            dropped = new PMProjectilePendingSpawnRelease[expired.Count];
            for (int i = 0; i < expired.Count; i++)
            {
                dropped[i] = Remove(expired[i], PMActivationResult.Pending,
                    PMProjectileReleaseReason.TombstoneExpired, wallNowMs, false);
            }

            return expired.Count;
        }

        /// <summary>
        /// 按激活裁决解挂。Confirmed → 返回**该 activation 的全部**挂起项（含 ExtraDeferMs，
        /// 由调用方生成后回放 Verify）；Rejected → 丢弃（不携带可生成载荷）。
        /// </summary>
        public int ResolveByActivation(
            uint ownerNetId,
            uint activationId,
            PMActivationResult outcome,
            double wallNowMs,
            out PMProjectilePendingSpawnRelease[] releases)
        {
            releases = new PMProjectilePendingSpawnRelease[0];
            if (outcome == PMActivationResult.Pending)
            {
                return 0;
            }

            if (PMProjectileWallClock.Validate(wallNowMs, _lastWallMs) != PMProjectileClockCheck.Ok)
            {
                return 0;
            }

            AcceptWallClock(wallNowMs);
            List<PMProjectileKey> hit = null;
            List<PMProjectileKey> ownerList;
            if (_byOwner.TryGetValue(ownerNetId, out ownerList))
            {
                for (int i = 0; i < ownerList.Count; i++)
                {
                    PMProjectilePendingSpawn e;
                    if (!_entries.TryGetValue(ownerList[i], out e)) { continue; }
                    if (e.ActivationId != activationId) { continue; }
                    if (hit == null) { hit = new List<PMProjectileKey>(); }
                    hit.Add(ownerList[i]);
                }
            }

            if (hit == null) { return 0; }

            PMProjectileReleaseReason reason = outcome == PMActivationResult.Confirmed
                ? PMProjectileReleaseReason.ActivationConfirmed
                : PMProjectileReleaseReason.ActivationRejected;
            releases = new PMProjectilePendingSpawnRelease[hit.Count];
            for (int i = 0; i < hit.Count; i++)
            {
                releases[i] = Remove(hit[i], outcome, reason, wallNowMs,
                    outcome == PMActivationResult.Confirmed);
            }

            return hit.Count;
        }

        /// <summary>断连/owner 失效清理：一次性丢弃该 owner 的全部挂起项。</summary>
        public int DiscardOwner(uint ownerNetId, double wallNowMs, out PMProjectilePendingSpawnRelease[] dropped)
        {
            dropped = new PMProjectilePendingSpawnRelease[0];
            if (ownerNetId == 0u) { return 0; }
            if (PMProjectileWallClock.Validate(wallNowMs, _lastWallMs) != PMProjectileClockCheck.Ok)
            {
                return 0;
            }

            AcceptWallClock(wallNowMs);
            List<PMProjectileKey> ownerList;
            if (!_byOwner.TryGetValue(ownerNetId, out ownerList) || ownerList.Count == 0)
            {
                return 0;
            }

            PMProjectileKey[] snapshot = ownerList.ToArray();
            dropped = new PMProjectilePendingSpawnRelease[snapshot.Length];
            for (int i = 0; i < snapshot.Length; i++)
            {
                dropped[i] = Remove(snapshot[i], PMActivationResult.Pending,
                    PMProjectileReleaseReason.OwnerCleared, wallNowMs, false);
            }

            return snapshot.Length;
        }

        /// <summary>升 epoch 整体清理；旧 epoch 请求随后一律 StaleEpoch。</summary>
        public bool AdvanceEpoch(uint newEpoch)
        {
            if (newEpoch <= _epoch) { return false; }
            _epoch = newEpoch;
            Clear();
            return true;
        }

        public void Clear()
        {
            _entries.Clear();
            _byOwner.Clear();
            _rejectedByCapacity = 0;
            _lastWallMs = 0.0;
        }

        private PMProjectilePendingSpawnRelease Remove(
            PMProjectileKey key,
            PMActivationResult outcome,
            PMProjectileReleaseReason reason,
            double wallNowMs,
            bool keepPayload)
        {
            PMProjectilePendingSpawnRelease release = new PMProjectilePendingSpawnRelease();
            PMProjectilePendingSpawn e;
            if (_entries.TryGetValue(key, out e))
            {
                release.Spawn = keepPayload ? e.Clone() : null;
                release.Key = key;
                release.Outcome = outcome;
                release.Reason = reason;
                double elapsed = wallNowMs - e.EnqueuedWallTimeMs;
                if (elapsed < 0.0) { elapsed = 0.0; }
                release.PendingElapsedMs = elapsed;
                release.ExtraDeferMs = elapsed > int.MaxValue ? int.MaxValue : (int)elapsed;
                release.WallTimeMs = wallNowMs;

                _entries.Remove(key);
                List<PMProjectileKey> list;
                if (_byOwner.TryGetValue(key.OwnerNetId, out list))
                {
                    list.Remove(key);
                    if (list.Count == 0) { _byOwner.Remove(key.OwnerNetId); }
                }
            }

            return release;
        }

        private void AcceptWallClock(double wallNowMs)
        {
            if (wallNowMs > _lastWallMs) { _lastWallMs = wallNowMs; }
        }
    }

    // ==================================================================== B
    //  Verify 先于 Spawn：弹还没生成，先把命中校验请求原样暂存
    // ====================================================================

    /// <summary>
    /// 一条暂存的 Verify 请求。**这是原始上行，不是结论**：位置/候选目标/视觉偏移都还没消毒，
    /// 保留原因见文件头「原始上报 vs 可信结论」。回放时必须整体送进 L0–L4 校验。
    /// </summary>
    public sealed class PMProjectileVerifyRequest
    {
        public PMProjectileKey Key;
        public PMVector3 PreviousPosition;
        public PMVector3 HitPosition;
        public int RewindMs;
        public double TotalSimTimeMs;
        public PMProjectileHitCandidate[] Targets = new PMProjectileHitCandidate[0];

        public PMProjectileVerifyRequest Clone()
        {
            PMProjectileVerifyRequest copy = (PMProjectileVerifyRequest)MemberwiseClone();
            copy.Targets = Targets == null ? new PMProjectileHitCandidate[0] : (PMProjectileHitCandidate[])Targets.Clone();
            return copy;
        }
    }

    public enum PMProjectileVerifyStashResult : byte
    {
        Stashed = 0,
        KeyCapacity = 1,
        TotalCapacity = 2,
        StaleEpoch = 3,
        Invalid = 4,
        InvalidClock = 5,
    }

    public struct PMProjectileVerifyStashOutcome
    {
        public PMProjectileVerifyStashResult Result;
        public PMProjectileKey Key;
        public int KeyCount;
        public int TotalCount;
        public PMProjectileClockCheck Clock;
    }

    public struct PMProjectileVerifyReplayOutcome
    {
        public PMProjectileKey Key;

        /// <summary>true = 本次调用真正解除了 pending 标记（条目已出队）。</summary>
        public bool MarkerCleared;

        /// <summary>true = 目标 key 尚未登记（违反「回放必须在注册之后」）。</summary>
        public bool RegistrationMissing;

        /// <summary>本次回放携带的额外延迟（来自挂起 Spawn 的实测时长）。</summary>
        public int ExtraDeferMs;

        public int DrainedCount;

        /// <summary>**独占**的 FIFO 回放条目（深 clone；调用方可自由改写）。</summary>
        public PMProjectileVerifyRequest[] Items;
    }

    /// <summary>
    /// Verify-before-Spawn 暂存（单 key ≤ 5、总量 ≤ 128、FIFO、TTL 随 Spawn = 2000ms）。
    /// 回放语义：**无条件先解除 pending 标记**，再按 FIFO 返回条目；且必须已登记（注册先于追赶/回放）。
    ///
    /// 本类**不做几何消毒**（见文件头）：非 finite 的上行点也照样暂存，由回放后的 L0 判 Rejected。
    /// 暂存时刻不因同 key 追加而刷新（与 C 的「合并不刷新原始时刻」同口径）。
    /// </summary>
    public sealed class PMProjectilePendingVerifies
    {
        private readonly List<PMProjectileKey> _order = new List<PMProjectileKey>();
        private readonly Dictionary<PMProjectileKey, List<PMProjectileVerifyRequest>> _entries =
            new Dictionary<PMProjectileKey, List<PMProjectileVerifyRequest>>();
        private readonly Dictionary<PMProjectileKey, double> _stashWallMs =
            new Dictionary<PMProjectileKey, double>();
        private readonly Dictionary<PMProjectileKey, int> _stashCounts =
            new Dictionary<PMProjectileKey, int>();
        private readonly HashSet<PMProjectileKey> _markers = new HashSet<PMProjectileKey>();

        private uint _epoch;
        private double _lastWallMs;
        private int _total;
        private int _rejectedByCapacity;

        public PMProjectilePendingVerifies(uint epoch)
        {
            if (epoch == 0u)
            {
                throw new ArgumentOutOfRangeException("epoch", "[PMProjectilePendingVerifies] epoch 0 无效。");
            }

            _epoch = epoch;
        }

        public uint Epoch { get { return _epoch; } }

        /// <summary>暂存条目总数（FIFO 数组元素数）。</summary>
        public int Count { get { return _total; } }

        public int KeyCount { get { return _entries.Count; } }

        public int PendingMarkerCount { get { return _markers.Count; } }

        public int RejectedByCapacityCount { get { return _rejectedByCapacity; } }

        public int CountForKey(PMProjectileKey key)
        {
            int n;
            return _stashCounts.TryGetValue(key, out n) ? n : 0;
        }

        /// <summary>pending 标记（验证先到时置位；回放/丢弃/超时清除）。</summary>
        public bool IsPendingMarkerSet(PMProjectileKey key)
        {
            return _markers.Contains(key);
        }

        /// <summary>单次入队会先清掉 TTL 过期项（惰性清理，与 UE 的「组件不 Tick」一致）。</summary>
        public PMProjectileVerifyStashOutcome TryStash(PMProjectileVerifyRequest request, double wallNowMs)
        {
            PMProjectileVerifyStashOutcome o = new PMProjectileVerifyStashOutcome();
            o.Clock = PMProjectileClockCheck.Ok;

            PMProjectileClockCheck clock = PMProjectileWallClock.Validate(wallNowMs, _lastWallMs);
            o.Clock = clock;
            if (clock != PMProjectileClockCheck.Ok)
            {
                o.Result = PMProjectileVerifyStashResult.InvalidClock;
                return o;
            }

            PurgeExpiredInternal(wallNowMs);

            if (request == null)
            {
                o.Result = PMProjectileVerifyStashResult.Invalid;
                return o;
            }

            o.Key = request.Key;
            if (request.Key.Epoch != _epoch)
            {
                o.Result = PMProjectileVerifyStashResult.StaleEpoch;
                return o;
            }

            if (!request.Key.IsValid || request.RewindMs < 0)
            {
                o.Result = PMProjectileVerifyStashResult.Invalid;
                return o;
            }

            if (CountForKey(request.Key) >= PMProjectileLimits.MaxPendingVerifiesPerKey)
            {
                _rejectedByCapacity++;
                o.Result = PMProjectileVerifyStashResult.KeyCapacity;
                o.KeyCount = CountForKey(request.Key);
                o.TotalCount = _total;
                return o;
            }

            if (_total >= PMProjectileLimits.MaxPendingVerifies)
            {
                _rejectedByCapacity++;
                o.Result = PMProjectileVerifyStashResult.TotalCapacity;
                o.KeyCount = CountForKey(request.Key);
                o.TotalCount = _total;
                return o;
            }

            List<PMProjectileVerifyRequest> list;
            if (!_entries.TryGetValue(request.Key, out list))
            {
                list = new List<PMProjectileVerifyRequest>();
                _entries.Add(request.Key, list);
                _order.Add(request.Key);
                _stashWallMs[request.Key] = wallNowMs;
            }

            list.Add(request.Clone());
            _stashCounts[request.Key] = list.Count;
            _markers.Add(request.Key);
            _total++;
            AcceptWallClock(wallNowMs);

            o.Result = PMProjectileVerifyStashResult.Stashed;
            o.KeyCount = list.Count;
            o.TotalCount = _total;
            return o;
        }

        /// <summary>
        /// 开始回放：**先解除 pending 标记**，再按 FIFO 返回条目。
        /// 第二个调用点（或标记已被清除时）返回 MarkerCleared=false 且 Items 为空（不可重入消费）。
        /// 需要 <paramref name="lifecycle"/> 中该 key 已登记：否则 RegistrationMissing（注册必须先于回放）。
        /// </summary>
        public PMProjectileVerifyReplayOutcome TryBeginReplay(
            PMProjectileLifecycle lifecycle, PMProjectileKey key, int extraDeferMs, double wallNowMs)
        {
            PMProjectileVerifyReplayOutcome o = new PMProjectileVerifyReplayOutcome();
            o.Key = key;
            o.Items = new PMProjectileVerifyRequest[0];
            o.ExtraDeferMs = extraDeferMs;

            if (lifecycle == null || !lifecycle.IsRegistered(key))
            {
                // 注册必须先于回放：不消耗标记，让调用方在生成完成后再来。
                o.RegistrationMissing = true;
                o.MarkerCleared = false;
                o.DrainedCount = 0;
                return o;
            }

            if (PMProjectileWallClock.Validate(wallNowMs, _lastWallMs) != PMProjectileClockCheck.Ok)
            {
                o.MarkerCleared = false;
                o.DrainedCount = 0;
                return o;
            }

            AcceptWallClock(wallNowMs);

            bool hadMarker = _markers.Remove(key);
            List<PMProjectileVerifyRequest> list;
            if (!_entries.TryGetValue(key, out list) || list.Count == 0)
            {
                o.MarkerCleared = hadMarker;
                o.DrainedCount = 0;
                return o;
            }

            PMProjectileVerifyRequest[] items = new PMProjectileVerifyRequest[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                items[i] = list[i].Clone();
            }

            // 出队/去重提交发生在**返回之前**（契约：先提交再对外可见）。
            _total -= list.Count;
            _entries.Remove(key);
            _stashCounts.Remove(key);
            _stashWallMs.Remove(key);
            _order.Remove(key);

            o.MarkerCleared = true;
            o.DrainedCount = items.Length;
            o.Items = items;
            return o;
        }

        /// <summary>丢弃某 key 的全部暂存（含 pending 标记）。返回丢弃条数。</summary>
        public int Discard(PMProjectileKey key)
        {
            _markers.Remove(key);
            List<PMProjectileVerifyRequest> list;
            if (!_entries.TryGetValue(key, out list))
            {
                return 0;
            }

            int n = list.Count;
            _total -= n;
            _entries.Remove(key);
            _stashCounts.Remove(key);
            _stashWallMs.Remove(key);
            _order.Remove(key);
            return n;
        }

        /// <summary>TTL 到期清理（随 Spawn 的 2000ms）。返回条数。</summary>
        public int PurgeExpired(double wallNowMs)
        {
            if (PMProjectileWallClock.Validate(wallNowMs, _lastWallMs) != PMProjectileClockCheck.Ok)
            {
                return 0;
            }

            AcceptWallClock(wallNowMs);
            return PurgeExpiredInternal(wallNowMs);
        }

        private int PurgeExpiredInternal(double wallNowMs)
        {
            List<PMProjectileKey> expired = null;
            for (int i = 0; i < _order.Count; i++)
            {
                PMProjectileKey key = _order[i];
                double stashMs;
                if (!_stashWallMs.TryGetValue(key, out stashMs)) { continue; }
                if (wallNowMs - stashMs < PMProjectileLimits.PendingTtlMs) { continue; }
                if (expired == null) { expired = new List<PMProjectileKey>(); }
                expired.Add(key);
            }

            if (expired == null) { return 0; }

            int dropped = 0;
            for (int i = 0; i < expired.Count; i++)
            {
                dropped += Discard(expired[i]);
            }

            return dropped;
        }

        public bool AdvanceEpoch(uint newEpoch)
        {
            if (newEpoch <= _epoch) { return false; }
            _epoch = newEpoch;
            Clear();
            return true;
        }

        public void Clear()
        {
            _order.Clear();
            _entries.Clear();
            _stashWallMs.Clear();
            _stashCounts.Clear();
            _markers.Clear();
            _total = 0;
            _rejectedByCapacity = 0;
            _lastWallMs = 0.0;
        }

        private void AcceptWallClock(double wallNowMs)
        {
            if (wallNowMs > _lastWallMs) { _lastWallMs = wallNowMs; }
        }
    }

    // ==================================================================== C
    //  命中结论暂存：命中已成立，但外层激活裁决仍 Pending
    // ====================================================================

    /// <summary>
    /// 一台**已消毒**的命中结论（= L0–L4 之后的产物）。
    ///
    /// 为什么单独有这一层：把「客户端原始上报」与「权威结论」在类型上分开。
    /// <see cref="PMProjectileHitBatch"/> / <see cref="PMProjectileHitCandidate"/> 是原始上行，
    /// 里面还带着未过滤的 ImpactPoint 与 VisualOffset；把它直接当结算数据发出去，
    /// 就等于把「Pending 不结算」变成了命名约定。这里只收 4 个字段的结论。
    /// </summary>
    public sealed class PMProjectileValidatedHitSet
    {
        public PMProjectileKey Key;
        public PMProjectileValidatedHit[] Hits = new PMProjectileValidatedHit[0];

        public PMProjectileValidatedHitSet Clone()
        {
            return new PMProjectileValidatedHitSet
            {
                Key = Key,
                Hits = Hits == null ? new PMProjectileValidatedHit[0] : (PMProjectileValidatedHit[])Hits.Clone(),
            };
        }
    }

    public struct PMProjectilePendingHitGroup
    {
        public PMProjectileKey Key;
        public double EnqueuedWallTimeMs;
        public int SetCount;
        public int HitCount;
    }

    public struct PMProjectilePendingHitExpiry
    {
        public PMProjectileKey Key;
        public double EnqueuedWallTimeMs;
        public int SetCount;
    }

    public enum PMProjectileHitStashResult : byte
    {
        Created = 0,
        Appended = 1,
        EvictedOldest = 2,
        KeyAppendCapacity = 3,
        StaleEpoch = 4,
        Invalid = 5,
        InvalidClock = 6,

        /// <summary>
        /// 该 key 的命中结论已终结（Confirm 已兑现或已被 Reject）：**不允许再暂存**，
        /// 否则迟到的一批命中会在终态之后重新攒出「第二份可结算数据」。
        /// </summary>
        AlreadyResolved = 7,
    }

    public struct PMProjectileHitStashOutcome
    {
        public PMProjectileHitStashResult Result;
        public PMProjectileKey Key;
        public int GroupCount;

        /// <summary>因满而丢弃的最旧组数（累计，可观测）。</summary>
        public int DroppedGroups;

        /// <summary>因单 key 追加上限而丢弃的条数（累计，可观测）。</summary>
        public int DroppedAppends;

        public double OriginalWallTimeMs;
        public PMProjectileClockCheck Clock;
    }

    public enum PMProjectileHitResolveResult : byte
    {
        ConfirmedOnce = 0,
        RejectedNoSettlement = 1,
        AlreadyResolved = 2,
        PendingNoSettlement = 3,
        NotFound = 4,
        InvalidClock = 5,
        Invalid = 6,
    }

    public struct PMProjectileHitResolveOutcome
    {
        public PMProjectileHitResolveResult Result;
        public PMProjectileKey Key;

        /// <summary>**只有** ConfirmedOnce 为 true（Pending/Reject/TTL/重复确认都不结算）。</summary>
        public bool Settled;

        public int SettledSetCount;
        public int SettledHitCount;

        /// <summary>**独占**的已消毒结论（深 clone；只有 Settled=true 时非空）。</summary>
        public PMProjectileValidatedHitSet[] Sets;

        public double OriginalWallTimeMs;
        public double WallTimeMs;
    }

    /// <summary>
    /// 命中结论暂存（128 组上限；同 key 合并追加但**不刷新原始时刻**；满则丢最旧并计数；
    /// Confirm 只兑现一次；Reject / TTL 都不结算；Reject 同样是终态）。
    /// Pending 期间**绝不产生任何结算数据**，并且只接受已消毒结论类型。
    /// </summary>
    public sealed class PMProjectilePendingHits
    {
        private sealed class Group
        {
            public PMProjectileKey Key;
            public double EnqueuedWallTimeMs;
            public List<PMProjectileValidatedHitSet> Sets = new List<PMProjectileValidatedHitSet>();
            public int HitCount;
        }

        private readonly List<Group> _order = new List<Group>();
        private readonly Dictionary<PMProjectileKey, Group> _index = new Dictionary<PMProjectileKey, Group>();
        private readonly Queue<PMProjectileKey> _resolvedRing = new Queue<PMProjectileKey>();
        private readonly HashSet<PMProjectileKey> _resolvedSet = new HashSet<PMProjectileKey>();

        private uint _epoch;
        private double _lastWallMs;
        private int _droppedGroups;
        private int _droppedAppends;

        public PMProjectilePendingHits(uint epoch)
        {
            if (epoch == 0u)
            {
                throw new ArgumentOutOfRangeException("epoch", "[PMProjectilePendingHits] epoch 0 无效。");
            }

            _epoch = epoch;
        }

        public uint Epoch { get { return _epoch; } }

        public int GroupCount { get { return _order.Count; } }

        /// <summary>因容量丢弃的最旧组数（累计）。</summary>
        public int DroppedGroupCount { get { return _droppedGroups; } }

        /// <summary>因单 key 追加上限丢弃的条数（累计）。</summary>
        public int DroppedAppendCount { get { return _droppedAppends; } }

        public int PendingHitCount()
        {
            int n = 0;
            for (int i = 0; i < _order.Count; i++) { n += _order[i].HitCount; }
            return n;
        }

        public bool Contains(PMProjectileKey key)
        {
            return _index.ContainsKey(key);
        }

        public bool IsResolvedMarkerSet(PMProjectileKey key)
        {
            return _resolvedSet.Contains(key);
        }

        public bool TryGetGroup(PMProjectileKey key, out PMProjectilePendingHitGroup group)
        {
            Group g;
            if (!_index.TryGetValue(key, out g))
            {
                group = new PMProjectilePendingHitGroup();
                return false;
            }

            group = new PMProjectilePendingHitGroup();
            group.Key = g.Key;
            group.EnqueuedWallTimeMs = g.EnqueuedWallTimeMs;
            group.SetCount = g.Sets.Count;
            group.HitCount = g.HitCount;
            return true;
        }

        /// <summary>
        /// 暂存一批**已消毒**命中结论。同 key 合并追加且**不刷新原始时刻**；满 128 组则丢最旧并计数。
        /// 已终态的 key（Confirm 已兑现 / 已 Reject）返回 AlreadyResolved。
        /// 入队前惰性清理 TTL 过期组。
        /// </summary>
        public PMProjectileHitStashOutcome TryStash(
            PMProjectileKey key, PMProjectileValidatedHitSet set, double wallNowMs)
        {
            PMProjectileHitStashOutcome o = new PMProjectileHitStashOutcome();
            o.Key = key;
            o.Clock = PMProjectileClockCheck.Ok;

            PMProjectileClockCheck clock = PMProjectileWallClock.Validate(wallNowMs, _lastWallMs);
            o.Clock = clock;
            if (clock != PMProjectileClockCheck.Ok)
            {
                o.Result = PMProjectileHitStashResult.InvalidClock;
                return o;
            }

            PurgeExpiredInternal(wallNowMs);

            if (set == null)
            {
                o.Result = PMProjectileHitStashResult.Invalid;
                return o;
            }

            if (key.Epoch != _epoch)
            {
                o.Result = PMProjectileHitStashResult.StaleEpoch;
                return o;
            }

            if (!key.IsValid)
            {
                o.Result = PMProjectileHitStashResult.Invalid;
                return o;
            }

            if (!IsTrustedSet(key, set))
            {
                o.Result = PMProjectileHitStashResult.Invalid;
                return o;
            }

            if (_resolvedSet.Contains(key))
            {
                o.Result = PMProjectileHitStashResult.AlreadyResolved;
                o.GroupCount = _order.Count;
                o.DroppedGroups = _droppedGroups;
                o.DroppedAppends = _droppedAppends;
                return o;
            }

            AcceptWallClock(wallNowMs);
            Group g;
            if (_index.TryGetValue(key, out g))
            {
                if (g.Sets.Count >= PMProjectilePendingLimits.MaxHitSetsPerKey)
                {
                    _droppedAppends++;
                    o.Result = PMProjectileHitStashResult.KeyAppendCapacity;
                    o.GroupCount = _order.Count;
                    o.DroppedGroups = _droppedGroups;
                    o.DroppedAppends = _droppedAppends;
                    o.OriginalWallTimeMs = g.EnqueuedWallTimeMs;
                    return o;
                }

                g.Sets.Add(set.Clone());
                g.HitCount += set.Hits.Length;
                o.Result = PMProjectileHitStashResult.Appended;
                o.GroupCount = _order.Count;
                o.DroppedGroups = _droppedGroups;
                o.DroppedAppends = _droppedAppends;
                o.OriginalWallTimeMs = g.EnqueuedWallTimeMs;
                return o;
            }

            bool evicted = false;
            if (_order.Count >= PMProjectileLimits.MaxPendingHitGroups)
            {
                EvictOldestGroup();
                evicted = true;
            }

            g = new Group();
            g.Key = key;
            g.EnqueuedWallTimeMs = wallNowMs;
            g.Sets.Add(set.Clone());
            g.HitCount = set.Hits.Length;
            _order.Add(g);
            _index.Add(key, g);

            o.Result = evicted ? PMProjectileHitStashResult.EvictedOldest : PMProjectileHitStashResult.Created;
            o.GroupCount = _order.Count;
            o.DroppedGroups = _droppedGroups;
            o.DroppedAppends = _droppedAppends;
            o.OriginalWallTimeMs = g.EnqueuedWallTimeMs;
            return o;
        }

        /// <summary>
        /// 按激活裁决兑现。Confirmed → 返回**独占**结算数据且只兑现一次；
        /// Rejected → 丢弃、打终态标记、不结算；Pending → 保留且不结算。
        /// </summary>
        public PMProjectileHitResolveOutcome TryResolve(
            PMProjectileKey key, PMActivationResult outcome, double wallNowMs)
        {
            PMProjectileHitResolveOutcome o = new PMProjectileHitResolveOutcome();
            o.Key = key;
            o.Sets = new PMProjectileValidatedHitSet[0];
            o.WallTimeMs = wallNowMs;

            if (PMProjectileWallClock.Validate(wallNowMs, _lastWallMs) != PMProjectileClockCheck.Ok)
            {
                o.Result = PMProjectileHitResolveResult.InvalidClock;
                return o;
            }

            AcceptWallClock(wallNowMs);

            if (outcome == PMActivationResult.Pending)
            {
                Group pendingGroup;
                if (!_index.TryGetValue(key, out pendingGroup))
                {
                    o.Result = _resolvedSet.Contains(key)
                        ? PMProjectileHitResolveResult.AlreadyResolved
                        : PMProjectileHitResolveResult.NotFound;
                    return o;
                }

                o.Result = PMProjectileHitResolveResult.PendingNoSettlement;
                o.Settled = false;
                o.OriginalWallTimeMs = pendingGroup.EnqueuedWallTimeMs;
                return o;
            }

            Group g;
            if (!_index.TryGetValue(key, out g))
            {
                o.Result = _resolvedSet.Contains(key)
                    ? PMProjectileHitResolveResult.AlreadyResolved
                    : PMProjectileHitResolveResult.NotFound;
                o.Settled = false;
                return o;
            }

            o.OriginalWallTimeMs = g.EnqueuedWallTimeMs;

            if (outcome == PMActivationResult.Rejected)
            {
                // 拒绝也是终态：移除 + 打标记，之后的迟到暂存/迟到 Confirm 都拿不到结算数据。
                RemoveGroup(g);
                PushResolvedMarker(key);
                o.Result = PMProjectileHitResolveResult.RejectedNoSettlement;
                o.Settled = false;
                return o;
            }

            // Confirmed：先提交移除（含去重标记），再构造返回数据 ⇒ 只兑现一次。
            int setCount = g.Sets.Count;
            int hits = g.HitCount;
            PMProjectileValidatedHitSet[] sets = new PMProjectileValidatedHitSet[setCount];
            for (int i = 0; i < setCount; i++)
            {
                sets[i] = g.Sets[i].Clone();
            }

            RemoveGroup(g);
            PushResolvedMarker(key);

            o.Result = PMProjectileHitResolveResult.ConfirmedOnce;
            o.Settled = true;
            o.SettledSetCount = setCount;
            o.SettledHitCount = hits;
            o.Sets = sets;
            return o;
        }

        /// <summary>TTL 到期清理（**不结算**）。返回被丢弃的组信息（独占数组）。</summary>
        public int PurgeExpired(double wallNowMs, out PMProjectilePendingHitExpiry[] dropped)
        {
            dropped = new PMProjectilePendingHitExpiry[0];
            if (PMProjectileWallClock.Validate(wallNowMs, _lastWallMs) != PMProjectileClockCheck.Ok)
            {
                return 0;
            }

            AcceptWallClock(wallNowMs);
            List<Group> expired = CollectExpired(wallNowMs);
            if (expired == null) { return 0; }

            dropped = new PMProjectilePendingHitExpiry[expired.Count];
            for (int i = 0; i < expired.Count; i++)
            {
                dropped[i].Key = expired[i].Key;
                dropped[i].EnqueuedWallTimeMs = expired[i].EnqueuedWallTimeMs;
                dropped[i].SetCount = expired[i].Sets.Count;
                RemoveGroup(expired[i]);
            }

            return expired.Count;
        }

        public bool AdvanceEpoch(uint newEpoch)
        {
            if (newEpoch <= _epoch) { return false; }
            _epoch = newEpoch;
            Clear();
            return true;
        }

        public void Clear()
        {
            _order.Clear();
            _index.Clear();
            _resolvedRing.Clear();
            _resolvedSet.Clear();
            _droppedGroups = 0;
            _droppedAppends = 0;
            _lastWallMs = 0.0;
        }

        /// <summary>
        /// 「可信结论」的门：身份一致 + 条数上限 + 每个目标 NetId 有效且 ImpactPoint 有限。
        /// 这是**边界上的廉价 sanity**，不是 L0–L4（几何/白名单/历史在 A2）。
        /// </summary>
        private static bool IsTrustedSet(PMProjectileKey key, PMProjectileValidatedHitSet set)
        {
            if (set.Key.IsValid && !set.Key.Equals(key)) { return false; }
            if (set.Hits == null) { return false; }
            if (set.Hits.Length > PMProjectileLimits.MaxTargets) { return false; }
            for (int i = 0; i < set.Hits.Length; i++)
            {
                if (set.Hits[i].TargetNetId == 0u) { return false; }
                if (!PMProjectileFinite.V(set.Hits[i].ImpactPoint)) { return false; }
            }

            return true;
        }

        private void AcceptWallClock(double wallNowMs)
        {
            if (wallNowMs > _lastWallMs) { _lastWallMs = wallNowMs; }
        }

        private int PurgeExpiredInternal(double wallNowMs)
        {
            List<Group> expired = CollectExpired(wallNowMs);
            if (expired == null) { return 0; }
            for (int i = 0; i < expired.Count; i++)
            {
                RemoveGroup(expired[i]);
            }

            return expired.Count;
        }

        private List<Group> CollectExpired(double wallNowMs)
        {
            List<Group> expired = null;
            for (int i = 0; i < _order.Count; i++)
            {
                if (wallNowMs - _order[i].EnqueuedWallTimeMs < PMProjectileLimits.PendingTtlMs) { continue; }
                if (expired == null) { expired = new List<Group>(); }
                expired.Add(_order[i]);
            }

            return expired;
        }

        private void EvictOldestGroup()
        {
            if (_order.Count == 0) { return; }
            Group oldest = _order[0];
            _order.RemoveAt(0);
            _index.Remove(oldest.Key);
            _droppedGroups++;
        }

        private void RemoveGroup(Group g)
        {
            _index.Remove(g.Key);
            _order.Remove(g);
        }

        private void PushResolvedMarker(PMProjectileKey key)
        {
            if (_resolvedSet.Contains(key)) { return; }
            if (_resolvedRing.Count >= PMProjectilePendingLimits.MaxResolvedHitMarkers)
            {
                PMProjectileKey oldest = _resolvedRing.Dequeue();
                _resolvedSet.Remove(oldest);
            }

            _resolvedRing.Enqueue(key);
            _resolvedSet.Add(key);
        }
    }
}
