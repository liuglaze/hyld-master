// ============================================================================
//  PMProjectileHistory —— R5-A2 目标历史（有界环形，帧锚 + 时间回溯）
// ============================================================================
//
//  契约来源：
//    · Docs/plans/net-r5-projectile-contract.md（「单位和时钟」「语义与有限资源」「命中验证」）
//    · Docs/plans/_r5_semantics_survey.md §1.5 / §1.6（L3 位置还原优先级与换算表）
//    · Docs/plans/_r5_host_survey.md §A（七元组真实来源）
//    · 共享只读类型 PMProjectile/PMProjectileContracts.cs（**本文件不得修改它**）
//
//  本文件实现 IPMProjectileTargetHistory：把 DS 每宿主帧记录下来的权威目标状态，
//  在「同 stream 的权威帧锚」与「WorldTimeMs 时间回溯」两种口径下还原成样本。
//
//  ---- 两条时钟口径（禁止混用）----
//    · TotalSimTimeMs（double，毫秒）：**只**用于「同一目标同 stream 的帧锚新鲜度」。
//      语义＝该实例累计仿真时间。锚新鲜度 = 最新样本.TotalSimTimeMs - 锚样本.TotalSimTimeMs ∈ [0, 600 + extraDefer]。
//    · WorldTimeMs（double，毫秒）：DS 会话单调经过时间，用于**各对象一致**的回溯时刻。
//      回溯目标时刻 = worldNowMs - clamp(rewindMs, 0, 500)，在样本的 WorldTimeMs 上做插值。
//
//  ---- 明令禁止 ----
//    · **不得**用「帧号差 × 16ms」代替上面的时间口径（可变 dt 下必然错误；见
//      net-r0-contract.md D-R0-19 与 _r5_semantics_survey.md G2）。
//    · 不得跨 epoch / 跨 stream 退化到另一世代的数据（旧流请求直接拒绝，不猜）。
//    · 不得跨 Teleport 插值、不得外推（超出最新样本一律退化 Current）。
//
//  ---- 有界性（硬）----
//    · 每目标 stream 512 样本环形（PMProjectileLimits.HistoryCapacity），超出丢最旧并计数。
//    · 目标总数 64（本文件 MaxTargetStreams）：满时**拒新**并计数，绝不无界增长。
//    · 保留窗口 1000ms（PMProjectileLimits.HistoryAgeMs）：按 WorldTimeMs 惰性裁剪。
//    · epoch 固定于实例；显式 Reset(newEpoch) 之后旧 epoch 的记录/请求一律被拒。
//
//  依赖面（硬）：只允许 PMNet（PMFrameId / PMFrameDomain）、PMNet.Mover（PMVector3）、
//  以及 PMNet.Projectile 自己的契约类型。禁止 UnityEngine / 业务类型。
//  Unity 2019.4 = C# 7.3 + netstandard2.0：不使用 Math.Clamp / Span / 元组 / ??=。
// ============================================================================

using System;
using System.Collections.Generic;
using PMNet.Mover;

namespace PMNet.Projectile
{
    /// <summary>
    /// 目标历史：每 epoch、每 (NetId, StreamVersion) 一条有界环形样本序列。
    /// 纯数据结构，无外部依赖，可被 netstandard2.0 + C#7.3 零 Unity 编译。
    /// **非线程安全**：只在 DS 主线程使用（D13「局内权威在主线程」）；内部 Dictionary 与
    /// 环形缓冲均无锁，跨线程调用会产生数据竞争。
    /// </summary>
    public sealed class PMProjectileHistory : IPMProjectileTargetHistory
    {
        /// <summary>可跟踪的目标数量上限（契约：「目标数最多64」）。满时拒新，不淘汰既有目标。</summary>
        public const int MaxTargetStreams = 64;

        /// <summary>每目标样本环形容量（契约：512 样本/目标）。</summary>
        public const int RingCapacity = PMProjectileLimits.HistoryCapacity;

        /// <summary>样本保留窗口（毫秒）。超出窗口的样本在查询/记录时被裁剪。</summary>
        public const int RetainMs = PMProjectileLimits.HistoryAgeMs;

        /// <summary>回溯上限（毫秒）：rewind 超过它只按它算（契约：rewind 上限 500ms）。</summary>
        public const int MaxRewindMs = PMProjectileLimits.MaxRewindMs;

        /// <summary>帧锚新鲜度基底（毫秒），实际窗口 = 它 + extraDeferMs（契约：600 + extraDefer）。</summary>
        public const int AnchorAgeMs = PMProjectileLimits.AnchorAgeMs;

        private readonly Dictionary<uint, TargetStream> _streams = new Dictionary<uint, TargetStream>();
        private uint _epoch;

        // ---- 可观测计数（全部有界、只增，供诊断与测试断言，不参与判定）----
        public int RejectedRecords { get { return _rejectedRecords; } }
        public int RejectedNewTargets { get { return _rejectedNewTargets; } }
        public int StaleStreamRecords { get { return _staleStreamRecords; } }
        public int StaleEpochRequests { get { return _staleEpochRequests; } }
        public int StaleStreamRequests { get { return _staleStreamRequests; } }
        public int PrunedByAge { get { return _prunedByAge; } }
        public int OverwrittenSamples { get { return _overwrittenSamples; } }
        public int IgnoredStaleDuplicates { get { return _ignoredStaleDuplicates; } }
        public int ClampedRewinds { get { return _clampedRewinds; } }
        public int TeleportRefusals { get { return _teleportRefusals; } }

        private int _rejectedRecords;
        private int _rejectedNewTargets;
        private int _staleStreamRecords;
        private int _staleEpochRequests;
        private int _staleStreamRequests;
        private int _prunedByAge;
        private int _overwrittenSamples;
        private int _ignoredStaleDuplicates;
        private int _clampedRewinds;
        private int _teleportRefusals;

        /// <summary>构造时固定 epoch（非 0）。epoch 不匹配的记录与请求一律被拒。</summary>
        public PMProjectileHistory(uint epoch)
        {
            if (epoch == 0u)
            {
                throw new ArgumentOutOfRangeException("epoch", "[PMProjectileHistory] epoch 不得为 0（0 表示未建立会话）。");
            }

            _epoch = epoch;
        }

        /// <summary>本实例固定的 epoch。跨 epoch 的记录/请求不会被退化到本实例。</summary>
        public uint Epoch { get { return _epoch; } }

        /// <summary>当前跟踪的目标数量（≤ <see cref="MaxTargetStreams"/>）。</summary>
        public int TargetCount { get { return _streams.Count; } }

        /// <summary>
        /// 清空全部样本并切换 epoch：此后旧 epoch 的记录/请求全部被拒（显式 Reset 拒旧）。
        /// **epoch 只允许单调前进**；更大的值 = 升代清旧，相同的值等价于 <see cref="Clear"/>，
        /// 更小的值抛 <see cref="ArgumentOutOfRangeException"/>（绝不静默倒退）。
        /// </summary>
        public void Reset(uint newEpoch)
        {
            if (newEpoch == 0u)
            {
                throw new ArgumentOutOfRangeException("newEpoch", "[PMProjectileHistory] epoch 不得为 0。");
            }

            // epoch 只能单调前进：倒退会把**已经关闭的旧 epoch 重新打开**——该 epoch 此前的
            // 记录/请求从此前一律被拒变成重新被接受（违反「重置需新 epoch」与「跨 epoch 不串扰」）。
            // 传入与当前相同的 epoch 等价于 Clear()（不做世代推进）。
            if (newEpoch < _epoch)
            {
                throw new ArgumentOutOfRangeException("newEpoch",
                    "[PMProjectileHistory] epoch 只能单调前进：当前 " + _epoch + "，收到 " + newEpoch
                    + "（倒退会重新打开已关闭的旧 epoch）。");
            }

            _streams.Clear();
            _epoch = newEpoch;
        }

        /// <summary>
        /// 清空全部样本，保留当前 epoch（用于同一 epoch 内的显式重置）。
        ///
        /// **已知限制（本批登记，不在本文件修复）**：本方法同时丢弃每目标的 stream 版本高水位，
        /// 因此清空后若再收到**低于此前版本**的 stream 记录，会被当作新流接受（同 epoch 内的
        /// 「旧流回归」）。按 D-R0-02，netId 在单会话内永不复用，正常链路不会产生这种记录；
        /// 若需要硬防旧流，请改用 <see cref="Reset"/>(更新 epoch) 升代，并把 key/stream 高水位
        /// 交给 A1 注册表（`PMProjectileLifecycle`）负责。
        /// </summary>
        public void Clear()
        {
            _streams.Clear();
        }

        /// <summary>某目标的当前样本数（0 = 未跟踪）。</summary>
        public int SampleCount(uint netId)
        {
            TargetStream stream;
            if (!_streams.TryGetValue(netId, out stream))
            {
                return 0;
            }

            return stream.Count;
        }

        /// <summary>某目标当前的 stream 版本（0 = 未跟踪）。</summary>
        public uint StreamVersionOf(uint netId)
        {
            TargetStream stream;
            if (!_streams.TryGetValue(netId, out stream))
            {
                return 0u;
            }

            return stream.Version;
        }

        // ------------------------------------------------------------------
        //  记录（DS 侧：每宿主帧按真实运动结果调用）
        // ------------------------------------------------------------------

        /// <summary>
        /// 记录一条权威目标样本。返回 false 时 <paramref name="rejectReason"/> 给出明确原因；
        /// 返回 true 表示已接受（含「同宿主帧的更新输出覆盖」与「无变化的旧重复被忽略」）。
        ///
        /// 校验顺序（任一失败即拒，不修改内部状态）：
        ///   1) epoch 匹配；2) NetId/StreamVersion 非 0；3) 帧域合法（ServerFrame=AuthorityServer，
        ///   OutputFrame 为空或 Input）；4) 位置/尺寸/时间有限；5) 尺寸合法（radius&gt;0 且 halfHeight≥radius）；
        ///   6) stream 关系（升代清旧 / 旧流拒收）；7) 时间与帧号单调；8) 目标容量。
        /// </summary>
        public bool Record(PMProjectileTargetSample sample, out string rejectReason)
        {
            rejectReason = null;

            if (sample.Epoch != _epoch)
            {
                _rejectedRecords++;
                rejectReason = "epoch-mismatch";
                return false;
            }

            if (sample.NetId == 0u)
            {
                _rejectedRecords++;
                rejectReason = "netid-invalid";
                return false;
            }

            if (sample.StreamVersion == 0u)
            {
                _rejectedRecords++;
                rejectReason = "stream-invalid";
                return false;
            }

            if (!sample.ServerFrame.IsValid || sample.ServerFrame.Domain != PMFrameDomain.AuthorityServer)
            {
                _rejectedRecords++;
                rejectReason = "serverframe-domain";
                return false;
            }

            if (sample.OutputFrame.IsValid && sample.OutputFrame.Domain != PMFrameDomain.Input)
            {
                _rejectedRecords++;
                rejectReason = "outputframe-domain";
                return false;
            }

            if (!sample.Position.IsFinite)
            {
                _rejectedRecords++;
                rejectReason = "position-not-finite";
                return false;
            }

            if (IsNonFinite(sample.RadiusM))
            {
                _rejectedRecords++;
                rejectReason = "radius-not-finite";
                return false;
            }

            if (IsNonFinite(sample.HalfHeightM))
            {
                _rejectedRecords++;
                rejectReason = "halfheight-not-finite";
                return false;
            }

            if (sample.RadiusM <= 0f)
            {
                _rejectedRecords++;
                rejectReason = "radius-not-positive";
                return false;
            }

            if (sample.HalfHeightM < sample.RadiusM)
            {
                _rejectedRecords++;
                rejectReason = "halfheight-below-radius";
                return false;
            }

            if (IsNonFinite(sample.WorldTimeMs) || IsNonFinite(sample.TotalSimTimeMs))
            {
                _rejectedRecords++;
                rejectReason = "time-not-finite";
                return false;
            }

            TargetStream stream;
            if (!_streams.TryGetValue(sample.NetId, out stream))
            {
                if (_streams.Count >= MaxTargetStreams)
                {
                    _rejectedNewTargets++;
                    rejectReason = "target-capacity";
                    return false;
                }

                stream = new TargetStream(sample.NetId, sample.StreamVersion);
                _streams.Add(sample.NetId, stream);
                if (stream.Append(sample))
                {
                    _overwrittenSamples++;
                }

                return true;
            }

            if (sample.StreamVersion < stream.Version)
            {
                _staleStreamRecords++;
                _rejectedRecords++;
                rejectReason = "stale-stream";
                return false;
            }

            if (sample.StreamVersion > stream.Version)
            {
                // 升代清旧：旧世代样本不得与新世代混用。
                stream.Clear();
                stream.Version = sample.StreamVersion;
                if (stream.Append(sample))
                {
                    _overwrittenSamples++;
                }

                return true;
            }

            if (stream.Count == 0)
            {
                if (stream.Append(sample))
                {
                    _overwrittenSamples++;
                }

                return true;
            }

            PMProjectileTargetSample newest = stream.Newest();

            // 时间单调（同 stream 内 WorldTimeMs / TotalSimTimeMs 不得回退）。
            if (sample.TotalSimTimeMs < newest.TotalSimTimeMs)
            {
                _rejectedRecords++;
                rejectReason = "totaltime-regression";
                return false;
            }

            if (sample.WorldTimeMs < newest.WorldTimeMs)
            {
                _rejectedRecords++;
                rejectReason = "worldtime-regression";
                return false;
            }

            if (sample.ServerFrame.Value == newest.ServerFrame.Value)
            {
                // 同一 AuthorityServer 帧可以有多个 Input 边界：取「最后输出」。
                // 若新记录的输出边界反而更旧，说明是迟到重复 → 忽略，保留较新者。
                if (sample.OutputFrame.IsValid && newest.OutputFrame.IsValid
                    && sample.OutputFrame.Value < newest.OutputFrame.Value)
                {
                    _ignoredStaleDuplicates++;
                    return true;
                }

                stream.ReplaceNewest(sample);
                return true;
            }

            if (sample.ServerFrame.Value < newest.ServerFrame.Value)
            {
                _rejectedRecords++;
                rejectReason = "serverframe-regression";
                return false;
            }

            if (stream.Append(sample))
            {
                _overwrittenSamples++;
            }

            return true;
        }

        /// <summary>记录一条样本（失败时丢弃原因）。测试/便利入口。</summary>
        public bool Record(PMProjectileTargetSample sample)
        {
            string ignored;
            return Record(sample, out ignored);
        }

        // ------------------------------------------------------------------
        //  查询（客户端 Verify / 命中验证使用）
        // ------------------------------------------------------------------

        /// <summary>
        /// 在「同 stream 权威帧锚 → WorldTimeMs 时间回溯 → 当前」三档退化中还原目标样本。
        ///
        /// 命中规则：
        ///   1) epoch / 目标 / stream 版本必须完全匹配；否则返回 false（**不退到另一世代**）。
        ///   2) **FrameAnchor**：candidate.TargetServerFrame 在本 stream 内存在，且
        ///      age = 最新.TotalSimTimeMs - 锚.TotalSimTimeMs ∈ [0, 600 + extraDeferMs]。
        ///      未来锚（帧号大于最新）或新鲜度不合格 → 退时间回溯，而不是硬拒。
        ///   3) **Rewind**：targetWorldMs = worldNowMs - clamp(rewindMs,0,500)，在样本 WorldTimeMs 上插值；
        ///      不跨 Teleport 插值、不外推、同宿主帧多输入取最后输出；早于保留窗口则钳到最旧保留样本。
        ///   4) **Current**：无可用回溯（rewind=0、请求时刻不早于最新、或历史不足）时退回最新权威样本，
        ///      并显式以 <see cref="PMProjectileHistoryResolution.Current"/> 暴露退化。
        /// </summary>
        public bool TryResolve(uint epoch, PMProjectileHitCandidate candidate, double worldNowMs,
            int rewindMs, int extraDeferMs, out PMProjectileTargetSample sample,
            out PMProjectileHistoryResolution resolution)
        {
            sample = default(PMProjectileTargetSample);
            resolution = PMProjectileHistoryResolution.Current;

            if (epoch != _epoch)
            {
                _staleEpochRequests++;
                return false;
            }

            if (candidate.TargetNetId == 0u)
            {
                return false;
            }

            TargetStream stream;
            if (!_streams.TryGetValue(candidate.TargetNetId, out stream))
            {
                return false;
            }

            if (candidate.TargetStreamVersion != stream.Version)
            {
                // 旧流/未知 stream：拒收，绝不用另一世代的样本顶替。
                _staleStreamRequests++;
                return false;
            }

            Prune(stream, worldNowMs);

            if (stream.Count == 0)
            {
                return false;
            }

            // ---- 第 1 档：同 stream 权威帧锚 ----
            if (candidate.TargetServerFrame.IsValid
                && candidate.TargetServerFrame.Domain == PMFrameDomain.AuthorityServer)
            {
                PMProjectileTargetSample newest = stream.Newest();
                if (candidate.TargetServerFrame.Value <= newest.ServerFrame.Value)
                {
                    for (int i = stream.Count - 1; i >= 0; i--)
                    {
                        PMProjectileTargetSample at = stream.At(i);
                        if (at.ServerFrame.Value == candidate.TargetServerFrame.Value)
                        {
                            double age = newest.TotalSimTimeMs - at.TotalSimTimeMs;
                            double maxAge = (double)AnchorAgeMs + (extraDeferMs > 0 ? (double)extraDeferMs : 0.0);
                            if (age >= 0.0 && age <= maxAge && !IsNonFinite(age))
                            {
                                sample = at;
                                resolution = PMProjectileHistoryResolution.FrameAnchor;
                                return true;
                            }

                            break; // 帧存在但新鲜度不合格 → 退时间回溯（不硬拒）
                        }

                        if (at.ServerFrame.Value < candidate.TargetServerFrame.Value)
                        {
                            break; // 该锚不在历史里 → 退时间回溯
                        }
                    }
                }
            }

            // ---- 第 2 档：WorldTimeMs 时间回溯 ----
            int rw = rewindMs;
            if (rw < 0)
            {
                rw = 0;
            }
            else if (rw > MaxRewindMs)
            {
                rw = MaxRewindMs;
            }

            double targetWorldMs = worldNowMs - (double)rw;
            PMProjectileTargetSample newestSample = stream.Newest();

            if (IsNonFinite(targetWorldMs) || targetWorldMs >= newestSample.WorldTimeMs)
            {
                // 不外推：请求时刻不早于最新样本 → 退回当前权威位置。
                sample = newestSample;
                resolution = PMProjectileHistoryResolution.Current;
                return true;
            }

            int upper = -1;
            for (int i = stream.Count - 1; i >= 0; i--)
            {
                if (stream.At(i).WorldTimeMs >= targetWorldMs)
                {
                    upper = i;
                    continue;
                }

                break;
            }

            if (upper < 0)
            {
                sample = newestSample;
                resolution = PMProjectileHistoryResolution.Current;
                return true;
            }

            if (upper == 0)
            {
                // 请求时刻早于（或等于）最旧保留样本：钳到最旧保留样本（now-clamped rewind）。
                sample = stream.At(0);
                resolution = PMProjectileHistoryResolution.Rewind;
                _clampedRewinds++;
                return true;
            }

            PMProjectileTargetSample s1 = stream.At(upper);
            PMProjectileTargetSample s0 = stream.At(upper - 1);

            if (s1.WorldTimeMs <= s0.WorldTimeMs)
            {
                sample = s1;
                resolution = PMProjectileHistoryResolution.Rewind;
                return true;
            }

            if (targetWorldMs >= s1.WorldTimeMs)
            {
                sample = s1;
                resolution = PMProjectileHistoryResolution.Rewind;
                return true;
            }

            if (s1.Teleported)
            {
                // 不跨 Teleport 插值：取近侧（不采用回溯区间之后的位置，避免事实上的外推）。
                sample = s0;
                resolution = PMProjectileHistoryResolution.Rewind;
                _teleportRefusals++;
                return true;
            }

            if (s0.ServerFrame.Value == s1.ServerFrame.Value)
            {
                // 同宿主帧多输入：不宣称每子步精确回溯，取最后输出。
                sample = s1;
                resolution = PMProjectileHistoryResolution.Rewind;
                return true;
            }

            double alpha = (targetWorldMs - s0.WorldTimeMs) / (s1.WorldTimeMs - s0.WorldTimeMs);
            sample = Interpolate(s0, s1, alpha);
            resolution = PMProjectileHistoryResolution.Rewind;
            return true;
        }

        // ------------------------------------------------------------------

        private static PMProjectileTargetSample Interpolate(PMProjectileTargetSample a, PMProjectileTargetSample b, double t)
        {
            PMProjectileTargetSample s = default(PMProjectileTargetSample);
            s.Epoch = a.Epoch;
            s.NetId = a.NetId;
            s.StreamVersion = a.StreamVersion;
            s.ServerFrame = b.ServerFrame;
            s.OutputFrame = b.OutputFrame;
            s.TotalSimTimeMs = a.TotalSimTimeMs + (b.TotalSimTimeMs - a.TotalSimTimeMs) * t;
            s.WorldTimeMs = a.WorldTimeMs + (b.WorldTimeMs - a.WorldTimeMs) * t;
            s.Position = a.Position + (b.Position - a.Position) * (float)t;
            s.RadiusM = a.RadiusM + (b.RadiusM - a.RadiusM) * (float)t;
            s.HalfHeightM = a.HalfHeightM + (b.HalfHeightM - a.HalfHeightM) * (float)t;
            s.Teleported = false;
            s.Alive = a.Alive && b.Alive; // 区间任一端已死 → 区间内一律视为不可命中
            return s;
        }

        private void Prune(TargetStream stream, double worldNowMs)
        {
            if (IsNonFinite(worldNowMs))
            {
                return;
            }

            double floorMs = worldNowMs - (double)RetainMs;
            while (stream.Count > 0 && stream.At(0).WorldTimeMs < floorMs)
            {
                stream.DropOldest();
                _prunedByAge++;
            }
        }

        private static bool IsNonFinite(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value);
        }

        private static bool IsNonFinite(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value);
        }

        /// <summary>单目标样本环形：固定容量，零 GC 追加，超出丢最旧。</summary>
        private sealed class TargetStream
        {
            public readonly uint NetId;
            public uint Version;
            public readonly PMProjectileTargetSample[] Ring = new PMProjectileTargetSample[RingCapacity];
            public int Head;
            public int Count;

            public TargetStream(uint netId, uint version)
            {
                NetId = netId;
                Version = version;
            }

            public PMProjectileTargetSample At(int index)
            {
                return Ring[(Head + index) % RingCapacity];
            }

            public PMProjectileTargetSample Newest()
            {
                return At(Count - 1);
            }

            /// <summary>追加一条；返回 true 表示覆盖了最旧样本（容量已满）。</summary>
            public bool Append(PMProjectileTargetSample sample)
            {
                if (Count < RingCapacity)
                {
                    Ring[(Head + Count) % RingCapacity] = sample;
                    Count++;
                    return false;
                }

                Ring[Head] = sample;
                Head = (Head + 1) % RingCapacity;
                return true;
            }

            public void ReplaceNewest(PMProjectileTargetSample sample)
            {
                Ring[(Head + Count - 1) % RingCapacity] = sample;
            }

            public void DropOldest()
            {
                Head = (Head + 1) % RingCapacity;
                Count--;
            }

            public void Clear()
            {
                Head = 0;
                Count = 0;
            }
        }
    }
}
