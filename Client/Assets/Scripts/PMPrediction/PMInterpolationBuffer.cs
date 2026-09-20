using System;

namespace PMNet.Prediction
{
    public enum PMInterpolationRejectReason
    {
        None = 0,
        /// <summary>字段非法（epoch/instance 为 0、Sync/Aux 为空、帧命名空间不对）。</summary>
        Malformed = 1,
        EpochMismatch = 2,
        InstanceMismatch = 3,
        /// <summary>仿真时间早于已有基准：迟到/乱序包，丢弃且不刷新基准。</summary>
        StaleOrDuplicate = 4,
    }

    /// <summary>SP 采样结果。Sync/Aux 都是深克隆，宿主可随意改写。</summary>
    public struct PMInterpolatedState<TSync, TAux>
    {
        /// <summary>是否已有权威快照可采样。</summary>
        public bool HasValue;

        public TSync Sync;

        /// <summary>离散辅助状态：一律取 To（最新权威），不做插值。</summary>
        public TAux Aux;

        /// <summary>最新权威快照的边界（Input 命名空间，仅作元数据）。</summary>
        public PMFrameId OutputFrame;

        /// <summary>最新权威快照的权威帧（AuthorityServer 命名空间，仅作元数据）。</summary>
        public PMFrameId ServerFrame;

        /// <summary>最新权威快照的累计仿真时间（SP 的唯一对齐量）。</summary>
        public double TotalSimTimeMs;

        /// <summary>插值系数（0=From，1=To）。</summary>
        public float Alpha;

        /// <summary>采样时间已到/超过最新快照（外推上限 0：停在最新权威值）。</summary>
        public bool ClampedToLatest;
    }

    /// <summary>
    /// SP（SimulatedProxy）时间插值缓冲（M07 / SP 侧）。纯 C#，不依赖 TInput，也不引用 AP 时间轴。
    ///
    /// 冻结事实（Docs/plans/net-r4-prediction-contract.md）：
    /// 1. SP 首批**只插值**，不调用 AP 回滚；对齐量是 TotalSimTimeMs（SP 没有等价帧号）。
    /// 2. 超出最新快照时停在最新权威值：外推上限 0，明确暂不实现项目额外表现外推。
    /// 3. 离散量（mode/grounded 等）取 To —— 由模型的 Interpolate 负责 Snap；本类对 Aux 一律取 To。
    /// 4. 收到新包时外推量归零并刷新权威基准；静止时也照样推进已接收的权威元数据。
    ///
    /// 用委托而非泛型接口，是为了让本类只有 &lt;TSync, TAux&gt; 两个类型参数；
    /// 真实模型可经 <see cref="For{TInput}"/> 从共享接口适配。
    /// </summary>
    public sealed class PMInterpolationBuffer<TSync, TAux>
    {
        /// <summary>首批的外推上限：0（只插值，不外推）。</summary>
        public const long DefaultMaxExtrapolateMs = 0L;

        private sealed class Frame
        {
            public PMFrameId OutputFrame;
            public PMFrameId ServerFrame;
            public double TotalSimTimeMs;
            public TSync Sync;
            public TAux Aux;
        }

        private readonly Func<TSync, TSync, float, TSync> _interpolate;
        private readonly Func<TSync, TSync> _cloneSync;
        private readonly Func<TAux, TAux> _cloneAux;
        private readonly long _maxExtrapolateMs;

        private Frame _from;
        private Frame _to;
        private uint _epoch;
        private uint _instanceId;
        private double _sampleTimeMs;

        private long _authorityAccepted;
        private long _staleRejected;
        private long _epochRejected;
        private long _instanceRejected;
        private long _malformedRejected;
        private long _bindings;
        private long _duplicateRefreshes;
        private long _samples;
        private long _clampedSamples;
        private long _alignments;
        private long _rewindsIgnored;

        public PMInterpolationBuffer(
            Func<TSync, TSync, float, TSync> interpolate,
            Func<TSync, TSync> cloneSync,
            Func<TAux, TAux> cloneAux,
            long maxExtrapolateMs = DefaultMaxExtrapolateMs)
        {
            if (interpolate == null) { throw new ArgumentNullException("interpolate"); }
            if (cloneSync == null) { throw new ArgumentNullException("cloneSync"); }
            if (cloneAux == null) { throw new ArgumentNullException("cloneAux"); }
            if (maxExtrapolateMs < 0L) { throw new ArgumentOutOfRangeException("maxExtrapolateMs"); }

            _interpolate = interpolate;
            _cloneSync = cloneSync;
            _cloneAux = cloneAux;
            _maxExtrapolateMs = maxExtrapolateMs;
        }

        /// <summary>从共享模型契约适配（<c>Interpolate/CloneSync/CloneAux</c> 三项即够）。</summary>
        public static PMInterpolationBuffer<TSync, TAux> For<TInput>(
            IPMPredictionModel<TInput, TSync, TAux> model,
            long maxExtrapolateMs = DefaultMaxExtrapolateMs)
        {
            if (model == null) { throw new ArgumentNullException("model"); }
            return new PMInterpolationBuffer<TSync, TAux>(
                model.Interpolate, model.CloneSync, model.CloneAux, maxExtrapolateMs);
        }

        // ------------------------------------------------------------------ 只读视图与计数

        public bool HasAuthority { get { return _to != null; } }
        public uint Epoch { get { return _epoch; } }
        public uint InstanceId { get { return _instanceId; } }

        /// <summary>最新权威快照的累计仿真时间（静止时也会随新包推进）。</summary>
        public double LastTotalSimTimeMs { get { return _to == null ? 0.0 : _to.TotalSimTimeMs; } }

        /// <summary>次新权威快照的累计仿真时间；只有一帧时为 0。</summary>
        public double PreviousTotalSimTimeMs { get { return _from == null ? 0.0 : _from.TotalSimTimeMs; } }

        public PMFrameId LastOutputFrame { get { return _to == null ? PMFrameId.None : _to.OutputFrame; } }
        public PMFrameId LastServerFrame { get { return _to == null ? PMFrameId.None : _to.ServerFrame; } }

        public double SampleTimeMs { get { return _sampleTimeMs; } }
        public long MaxExtrapolateMs { get { return _maxExtrapolateMs; } }

        public long AuthorityAccepted { get { return _authorityAccepted; } }
        public long StaleRejected { get { return _staleRejected; } }
        public long EpochRejected { get { return _epochRejected; } }
        public long InstanceRejected { get { return _instanceRejected; } }
        public long MalformedRejected { get { return _malformedRejected; } }
        public long Bindings { get { return _bindings; } }
        public long DuplicateRefreshes { get { return _duplicateRefreshes; } }
        public long Samples { get { return _samples; } }
        public long ClampedSamples { get { return _clampedSamples; } }
        /// <summary>表现时钟落后于新窗口起点而直接对齐到最新权威的次数。</summary>
        public long Alignments { get { return _alignments; } }
        public long RewindsIgnored { get { return _rewindsIgnored; } }

        /// <summary>表现时钟相对最新权威时间的超前量（信息量；渲染永远钳到 0 外推）。</summary>
        public double ExtrapolationMs
        {
            get
            {
                if (_to == null) { return 0.0; }
                double lead = _sampleTimeMs - _to.TotalSimTimeMs;
                return lead > 0.0 ? lead : 0.0;
            }
        }

        // ------------------------------------------------------------------ 输入

        /// <summary>
        /// 接收一份权威快照。迟到（仿真时间倒退）/错 epoch/错 instance/畸形一律 fail-closed（不改基准）。
        /// 接受时：From ← To，To ← 新包（深克隆）。
        ///
        /// 表现时钟规则（首批，无额外阈值）：
        /// - 首包，或表现时钟落在新窗口起点之前（例如停发期间时钟没被推进）→ 直接对齐到最新权威时间
        ///   （不逐帧追赶、无衰减回弹，外推量归零）。
        /// - 时钟落在 [From, To] 内 → 保持，由 <see cref="Sample"/> 正常插值。
        /// - 时钟超过 To → 保持（<see cref="Sample"/> 会钳到 To：外推上限 0）。
        /// </summary>
        public PMInterpolationRejectReason OnAuthority(in PMPredictionSnapshot<TSync, TAux> snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }

            if (snapshot.Epoch == 0u || snapshot.InstanceId == 0u ||
                object.ReferenceEquals(snapshot.Sync, null) || object.ReferenceEquals(snapshot.Aux, null))
            {
                _malformedRejected++;
                return PMInterpolationRejectReason.Malformed;
            }

            if (snapshot.OutputFrame.IsValid && snapshot.OutputFrame.Domain != PMFrameDomain.Input)
            {
                _malformedRejected++;
                return PMInterpolationRejectReason.Malformed;
            }

            if (snapshot.ServerFrame.IsValid && snapshot.ServerFrame.Domain != PMFrameDomain.AuthorityServer)
            {
                _malformedRejected++;
                return PMInterpolationRejectReason.Malformed;
            }

            if (_epoch != 0u)
            {
                if (snapshot.Epoch != _epoch)
                {
                    _epochRejected++;
                    return PMInterpolationRejectReason.EpochMismatch;
                }

                if (snapshot.InstanceId != _instanceId)
                {
                    _instanceRejected++;
                    return PMInterpolationRejectReason.InstanceMismatch;
                }
            }

            if (_to != null && snapshot.TotalSimTimeMs < _to.TotalSimTimeMs)
            {
                _staleRejected++;
                return PMInterpolationRejectReason.StaleOrDuplicate;
            }

            if (_epoch == 0u)
            {
                _epoch = snapshot.Epoch;
                _instanceId = snapshot.InstanceId;
                _bindings++;
            }

            if (_to != null && snapshot.TotalSimTimeMs == _to.TotalSimTimeMs &&
                snapshot.OutputFrame == _to.OutputFrame)
            {
                _duplicateRefreshes++;
            }

            Frame next = new Frame();
            next.OutputFrame = snapshot.OutputFrame;
            next.ServerFrame = snapshot.ServerFrame;
            next.TotalSimTimeMs = snapshot.TotalSimTimeMs;
            next.Sync = _cloneSync(snapshot.Sync);
            next.Aux = _cloneAux(snapshot.Aux);

            _from = _to;
            _to = next;

            // 外推量归零：时钟落后于新窗口起点（含首包）时直接对齐到最新权威。
            if (_from == null || _sampleTimeMs < _from.TotalSimTimeMs)
            {
                _sampleTimeMs = next.TotalSimTimeMs;
                _alignments++;
            }

            _authorityAccepted++;
            return PMInterpolationRejectReason.None;
        }

        /// <summary>推进表现层时钟。负数忽略（时钟倒退不产生表现位移）。</summary>
        public void Advance(double deltaMs)
        {
            if (deltaMs <= 0.0)
            {
                if (deltaMs < 0.0)
                {
                    _rewindsIgnored++;
                }

                return;
            }

            if (_to == null)
            {
                return;
            }

            _sampleTimeMs += deltaMs;
        }

        /// <summary>
        /// 宿主显式把表现时钟对齐到最新权威时间（停发恢复时使用）：无衰减回弹、外推量归零。
        /// 本类刻意不做“差距超过阈值就自动跳”的启发式，避免把未实测的阈值写进首批契约。
        /// </summary>
        public void AlignToLatest()
        {
            if (_to == null) { return; }
            _sampleTimeMs = _to.TotalSimTimeMs;
            _alignments++;
        }

        /// <summary>
        /// 采样当前表现状态。渲染时间钳在 [From, To]：外推上限 0 —— 超过最新快照就停在最新权威值（Alpha=1）。
        /// Aux 一律取 To；Sync 的连续量交给模型 Interpolate，离散量由模型 Snap 到 To。
        /// </summary>
        public PMInterpolatedState<TSync, TAux> Sample()
        {
            PMInterpolatedState<TSync, TAux> state = new PMInterpolatedState<TSync, TAux>();

            if (_to == null)
            {
                state.HasValue = false;
                return state;
            }

            _samples++;
            state.HasValue = true;
            state.OutputFrame = _to.OutputFrame;
            state.ServerFrame = _to.ServerFrame;
            state.TotalSimTimeMs = _to.TotalSimTimeMs;
            state.Aux = _cloneAux(_to.Aux);

            if (_from == null)
            {
                state.Sync = _cloneSync(_to.Sync);
                state.Alpha = 1f;
                state.ClampedToLatest = true;
                _clampedSamples++;
                return state;
            }

            double fromTime = _from.TotalSimTimeMs;
            double toTime = _to.TotalSimTimeMs;
            double limit = toTime + _maxExtrapolateMs;
            double sampleTime = _sampleTimeMs;

            if (sampleTime > limit) { sampleTime = limit; }
            if (sampleTime < fromTime) { sampleTime = fromTime; }

            float alpha;
            if (toTime <= fromTime)
            {
                alpha = 1f;
            }
            else
            {
                alpha = (float)((sampleTime - fromTime) / (toTime - fromTime));
                if (alpha < 0f) { alpha = 0f; }
                if (alpha > 1f) { alpha = 1f; }
            }

            state.Alpha = alpha;
            state.ClampedToLatest = (_sampleTimeMs >= toTime);
            if (state.ClampedToLatest)
            {
                _clampedSamples++;
            }

            if (alpha >= 1f)
            {
                // 停在最新权威值，绝不做表现外推。
                state.Sync = _cloneSync(_to.Sync);
            }
            else if (alpha <= 0f)
            {
                state.Sync = _cloneSync(_from.Sync);
            }
            else
            {
                state.Sync = _cloneSync(_interpolate(_from.Sync, _to.Sync, alpha));
            }

            return state;
        }
    }
}
