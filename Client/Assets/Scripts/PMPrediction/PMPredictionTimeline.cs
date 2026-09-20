using System;
using System.Collections.Generic;

namespace PMNet.Prediction
{
    public enum PMTickRejectReason
    {
        None = 0,
        /// <summary>断线冻结：帧号停止推进，等待宿主墙钟与显式 Resync。</summary>
        Frozen = 1,
        /// <summary>已进入 NeedsResync，必须先显式 Resync。</summary>
        NeedsResync = 2,
        /// <summary>历史容量满且最旧记录仍未被确认，不能静默覆盖。</summary>
        HistoryExhausted = 3,
    }

    /// <summary>一次本地 Tick 的接纳结果。</summary>
    public struct PMTickResult
    {
        /// <summary>该输入槽是否被接纳（接纳即代表输出边界 n+1 已存在）。</summary>
        public bool Accepted;

        /// <summary>是否调用了模型 Simulate（占位步不会调用）。</summary>
        public bool Simulated;

        /// <summary>是否为 dt==0 的缺帧占位（只消耗输入槽与输出边界，不推进仿真时间）。</summary>
        public bool Placeholder;

        /// <summary>因断线冻结被拒绝。</summary>
        public bool Frozen;

        /// <summary>因历史满/需重同步被拒绝。</summary>
        public bool NeedsResync;

        /// <summary>本步实际采用的整毫秒 dt（0 表示占位）。</summary>
        public int StepMs;

        public PMTickRejectReason Reject;
    }

    public enum PMAuthorityRejectReason
    {
        None = 0,
        /// <summary>字段非法（epoch/instance 为 0、Sync/Aux 为空、帧命名空间不对）。</summary>
        Malformed = 1,
        EpochMismatch = 2,
        InstanceMismatch = 3,
        /// <summary>本角色不参与预测回滚（D-R0-22：只有 AutonomousProxy 回滚）。</summary>
        RoleNotPredictive = 4,
        /// <summary>权威边界超过本端 PendingFrame（未来边界）。</summary>
        Future = 5,
        /// <summary>边界 &lt;= ConfirmedFrame：迟到/重复，丢弃且确认边界不回退。</summary>
        StaleOrDuplicate = 6,
        /// <summary>边界早于历史窗口，无法恢复，必须重同步（禁止用当前值冒充）。</summary>
        HistoryMissing = 7,
        /// <summary>
        /// 已冻结（断线）或已进入 NeedsResync：一律拒绝 ApplyAuthority，
        /// 状态 / 确认边界 / 事件全部保持原样；恢复只走显式可信 <see cref="PMPredictionTimeline{TSync,TAux}.Resync"/>。
        /// </summary>
        Stalled = 8,
    }

    public struct PMAuthorityApplyResult
    {
        /// <summary>权威快照是否被接纳（含 ShouldReconcile==false 的“接受但不回滚”）。</summary>
        public bool Applied;

        /// <summary>是否执行了 Restore + Replay。</summary>
        public bool Reconciled;

        /// <summary>本次是否推进了确认边界。</summary>
        public bool AdvancedConfirmed;

        /// <summary>重放步数 == PendingFrame - 权威边界。</summary>
        public int ResimulatedFrames;

        /// <summary>本次新增广播的不可逆事件数。</summary>
        public int EventsConfirmed;

        /// <summary>宿主应向权威请求完整快照（缺历史/历史耗尽）。</summary>
        public bool ShouldRequestResync;

        public PMFrameId PreviousConfirmedFrame;
        public PMFrameId ConfirmedFrame;
        public PMAuthorityRejectReason Reject;
    }

    public struct PMResyncResult
    {
        public bool Applied;

        /// <summary>是否因 epoch/instance 变化整体重绑。</summary>
        public bool EpochRebound;

        /// <summary>被丢弃的未确认输入条数（宿主据此清理自己的暂存）。</summary>
        public int ResetUnconfirmedInputs;

        public PMAuthorityRejectReason Reject;
    }

    /// <summary>
    /// 每实例预测时间轴（M07 / AP 侧）。纯 C#，只依赖 <see cref="PMNet"/> 的帧与角色原语。
    ///
    /// 冻结事实（Docs/plans/net-r4-prediction-contract.md）：
    /// 1. 输入帧 n 在边界 n 起模拟，产出边界 n+1。<see cref="PendingFrame"/> 是下一条输入编号，
    ///    <see cref="ConfirmedFrame"/> 是最近接受的输出边界，两者都在 <see cref="PMFrameDomain.Input"/>；
    ///    DS 自己的 AuthorityServer 帧只作为快照元数据，绝不与 AP 帧做算术。
    /// 2. 历史默认 128 条输入 / 129 个边界状态；满且未确认时**不覆盖**，冻结进 NeedsResync。
    /// 3. 只有 AutonomousProxy 回滚（D-R0-22）；SP/DS 调 ApplyAuthority 一律 fail-closed。
    /// 4. 不可逆事件只随确认边界推进广播一次；同帧同 key 去重；回滚替换同帧事件集合且不重复广播。
    ///    并且：**只有携带该输出边界的权威事件证据（快照 ConfirmedEvents / ConfirmedEventFrames）
    ///    才能广播该边界的不可逆事件**；缺证据一律不广播，绝不用预测事件冒充权威。
    /// </summary>
    public sealed class PMPredictionTimeline<TInput, TSync, TAux>
    {
        /// <summary>默认历史深度：128 条输入（+ 129 个边界状态）。</summary>
        public const int DefaultHistoryCapacity = 128;

        /// <summary>本项目首批时间步下限（整毫秒）。</summary>
        public const int MinStepMs = 1;

        /// <summary>本项目首批时间步上限（整毫秒）。</summary>
        public const int MaxStepMs = 50;

        /// <summary>断线后触发一次 ResyncRequested 的墙钟阈值。</summary>
        public const long DefaultDisconnectTimeoutMs = 2000L;

        /// <summary>可接纳的未来窗口：边界最多领先确认边界多少帧。</summary>
        public const int DefaultFutureWindow = 128;

        /// <summary>单帧事件条数上限（模型输出与权威证据共用）：超出即契约违例/畸形证据。</summary>
        public const int MaxEventsPerFrame = 256;

        /// <summary>一次权威快照可携带的「边界 + 事件」证据条目上限。</summary>
        public const int MaxConfirmedEventFrames = 256;

        /// <summary>一次权威快照可携带的权威事件总条数上限（跨全部证据条目）。</summary>
        public const int MaxConfirmedEventsTotal = 4096;

        private static readonly PMPredictionEvent[] EmptyEvents = new PMPredictionEvent[0];

        /// <summary>历史槽位：一条输入 + 它产出的边界状态。槽位对象复用，避免环形缓冲反复分配。</summary>
        private sealed class Step
        {
            public long InputFrame;
            public TInput Input;
            public int StepMs;
            public bool Placeholder;
            public PMFrameId ServerFrame;
            public TSync Sync;
            public TAux Aux;
            public double TotalSimTimeMs;
            public PMPredictionEvent[] Events;

            /// <summary>该边界的 Events 是否来自权威证据（false = 本地预测，绝不作权威广播）。</summary>
            public bool EventsAuthoritative;
        }

        private readonly IPMPredictionModel<TInput, TSync, TAux> _model;
        private readonly PMNetRole _role;
        private readonly int _capacity;
        private readonly long _disconnectTimeoutMs;
        private readonly long _futureWindow;
        private readonly Step[] _slots;

        private int _head;
        private int _count;
        private uint _sessionEpoch;
        private uint _instanceId;

        /// <summary>最旧保留边界（其状态存在 _base*）。</summary>
        private long _boundaryStartFrame;

        private TSync _baseSync;
        private TAux _baseAux;
        private double _baseTotalSimTimeMs;
        private PMFrameId _baseServerFrame;

        private long _pendingFrame;
        private long _confirmedFrame;

        /// <summary>最近一次被接纳的权威边界；-1 表示本会话尚未接纳过任何权威快照。
        /// 与 <see cref="_confirmedFrame"/> 的区别：初值边界 0 属于“尚未确认”状态，
        /// 因此允许首份权威快照落在边界 0 上（用于校正起点状态），但同一边界不得重复接纳。</summary>
        private long _acceptedBoundary = -1L;

        private bool _frozen;
        private bool _needsResync;
        private double _frozenElapsedMs;
        private bool _resyncNotifiedWhileFrozen;

        private long _tickedSteps;
        private long _simulatedSteps;
        private long _placeholderSteps;
        private long _replayedSteps;
        private long _confirmedEventsEmitted;
        private long _rejectedAuthorities;
        private long _duplicateAuthorities;
        private long _futureAuthorities;
        private long _roleRejections;
        private long _resyncRequests;
        private long _resyncApplications;
        private long _epochRebinds;
        private long _unconfirmedInputsReset;
        private long _prunedConfirmedSteps;
        private long _historyExhaustions;
        private long _wallClockRewindsIgnored;
        private long _historyMisses;
        private long _stalledAuthorities;
        private long _confirmedEventFramesApplied;
        private long _confirmedEventFramesSkipped;

        public PMPredictionTimeline(
            IPMPredictionModel<TInput, TSync, TAux> model,
            uint sessionEpoch,
            uint instanceId,
            TSync initialSync,
            TAux initialAux,
            PMNetRole role = PMNetRole.AutonomousProxy,
            int historyCapacity = DefaultHistoryCapacity,
            long disconnectTimeoutMs = DefaultDisconnectTimeoutMs,
            long futureWindow = DefaultFutureWindow)
        {
            if (model == null) { throw new ArgumentNullException("model"); }
            if (object.ReferenceEquals(initialSync, null)) { throw new ArgumentNullException("initialSync"); }
            if (object.ReferenceEquals(initialAux, null)) { throw new ArgumentNullException("initialAux"); }
            if (historyCapacity < 1) { throw new ArgumentOutOfRangeException("historyCapacity"); }
            if (futureWindow < 1) { throw new ArgumentOutOfRangeException("futureWindow"); }

            _model = model;
            _role = role;
            _capacity = historyCapacity;
            _disconnectTimeoutMs = disconnectTimeoutMs;
            _futureWindow = futureWindow;
            _slots = new Step[historyCapacity];

            _sessionEpoch = sessionEpoch;
            _instanceId = instanceId;
            _baseSync = model.CloneSync(initialSync);
            _baseAux = model.CloneAux(initialAux);
            _baseTotalSimTimeMs = 0.0;
            _baseServerFrame = PMFrameId.None;
            _pendingFrame = 0L;
            _confirmedFrame = 0L;
        }

        /// <summary>用可信完整快照构造（含 epoch/instance/边界/累计仿真时间/权威帧元数据）。</summary>
        public PMPredictionTimeline(
            IPMPredictionModel<TInput, TSync, TAux> model,
            PMPredictionSnapshot<TSync, TAux> initialSnapshot,
            PMNetRole role = PMNetRole.AutonomousProxy,
            int historyCapacity = DefaultHistoryCapacity,
            long disconnectTimeoutMs = DefaultDisconnectTimeoutMs,
            long futureWindow = DefaultFutureWindow)
            : this(
                model,
                sessionEpoch: initialSnapshot == null ? 1u : initialSnapshot.Epoch,
                instanceId: initialSnapshot == null ? 1u : initialSnapshot.InstanceId,
                initialSync: initialSnapshot == null ? default(TSync) : initialSnapshot.Sync,
                initialAux: initialSnapshot == null ? default(TAux) : initialSnapshot.Aux,
                role: role,
                historyCapacity: historyCapacity,
                disconnectTimeoutMs: disconnectTimeoutMs,
                futureWindow: futureWindow)
        {
            if (initialSnapshot == null) { throw new ArgumentNullException("initialSnapshot"); }
            if (initialSnapshot.Epoch == 0u || initialSnapshot.InstanceId == 0u)
            {
                throw new InvalidOperationException("[PMPredictionTimeline] 初始快照的 epoch/instance 必须非 0。");
            }

            if (!initialSnapshot.OutputFrame.IsValid || initialSnapshot.OutputFrame.Domain != PMFrameDomain.Input)
            {
                throw new InvalidOperationException("[PMPredictionTimeline] 初始快照的 OutputFrame 必须是 Input 命名空间。");
            }

            if (initialSnapshot.ServerFrame.IsValid && initialSnapshot.ServerFrame.Domain != PMFrameDomain.AuthorityServer)
            {
                throw new InvalidOperationException(
                    "[PMPredictionTimeline] 初始快照的 ServerFrame 必须是 AuthorityServer 命名空间。");
            }

            if (!IsFiniteDouble(initialSnapshot.TotalSimTimeMs) || initialSnapshot.TotalSimTimeMs < 0.0)
            {
                throw new InvalidOperationException(
                    "[PMPredictionTimeline] 初始快照的 TotalSimTimeMs 必须有限且非负，收到 "
                    + initialSnapshot.TotalSimTimeMs + "。");
            }

            long boundary = initialSnapshot.OutputFrame.Value;
            if (boundary < 0L)
            {
                throw new InvalidOperationException(
                    "[PMPredictionTimeline] 初始快照的 OutputFrame 不得为负，收到 " + boundary + "。");
            }

            // _boundaryStartFrame 是「最旧保留边界（其状态存于 _base*）」；读取走
            // boundary - 1 - _boundaryStartFrame，因此它必须与快照边界**同时**写齐。
            // 旧缺陷：只写 _pendingFrame/_confirmedFrame，基态停在 0 ⇒ OutputFrame > 0 时
            // 首次读取（GetBoundarySyncRef / ResolveBoundaryIndex）立刻抛出「读取边界越界」。
            _boundaryStartFrame = boundary;
            _pendingFrame = boundary;
            _confirmedFrame = boundary;
            _baseTotalSimTimeMs = initialSnapshot.TotalSimTimeMs;
            _baseServerFrame = initialSnapshot.ServerFrame;

            if (_boundaryStartFrame != _pendingFrame || _boundaryStartFrame != _confirmedFrame)
            {
                // 防御性断言：构造函数只应产生「基态边界 == 输出边界」的一致状态。
                throw new InvalidOperationException(
                    "[PMPredictionTimeline] 初始快照构造后边界不自洽（内部错误）。");
            }
        }

        private static bool IsFiniteDouble(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        // ------------------------------------------------------------------ 只读视图与计数

        public IPMPredictionModel<TInput, TSync, TAux> Model { get { return _model; } }
        public PMNetRole Role { get { return _role; } }
        public uint SessionEpoch { get { return _sessionEpoch; } }
        public uint InstanceId { get { return _instanceId; } }

        /// <summary>下一条要 tick 的输入编号（Input 命名空间）。</summary>
        public PMFrameId PendingFrame { get { return new PMFrameId(PMFrameDomain.Input, _pendingFrame); } }

        /// <summary>
        /// 当前 <see cref="PendingFrame"/> 边界对应的权威服务器帧元数据（<see cref="PMFrameDomain.AuthorityServer"/> 命名空间；
        /// 未建立时为 <see cref="PMFrameId.None"/>）。
        ///
        /// **只读元数据入口**：既不深克隆 Sync/Aux，也不合成/推断任何帧号——
        /// 返回值就是该边界已记录的 ServerFrame（构造快照初值、Tick 的 <c>step.ServerFrame</c>、
        /// 权威 apply/resync 写入的值），与 <see cref="GetSnapshot"/> 的 <c>ServerFrame</c> 字段逐次一致。
        /// 宿主在每子步取样阶段只需要帧号时用它，避免为一个字段付出 CloneSync + CloneAux 的分配尖峰。
        /// </summary>
        public PMFrameId PendingServerFrame { get { return GetBoundaryServerFrame(_pendingFrame); } }

        /// <summary>最近接受的输出边界（Input 命名空间）；初值为边界 0。</summary>
        public PMFrameId ConfirmedFrame { get { return new PMFrameId(PMFrameDomain.Input, _confirmedFrame); } }

        /// <summary>是否已接纳过任何权威快照（边界 0 的初值不算）。</summary>
        public bool HasAcceptedAuthority { get { return _acceptedBoundary >= 0L; } }

        /// <summary>最近接纳的权威边界（Input 命名空间）；未接纳过时为 None。</summary>
        public PMFrameId AcceptedBoundaryFrame
        {
            get { return _acceptedBoundary < 0L ? PMFrameId.None : new PMFrameId(PMFrameDomain.Input, _acceptedBoundary); }
        }

        /// <summary>最旧仍可恢复的边界编号。</summary>
        public PMFrameId OldestRetainedBoundaryFrame { get { return new PMFrameId(PMFrameDomain.Input, _boundaryStartFrame); } }

        /// <summary>历史中保留的输入条数。</summary>
        public int RetainedInputCount { get { return _count; } }
        public int HistoryCapacity { get { return _capacity; } }

        /// <summary>断线冻结（帧号停止推进；恢复只接受显式 Resync）。</summary>
        public bool IsFrozen { get { return _frozen; } }

        /// <summary>历史耗尽/缺历史后必须重同步。</summary>
        public bool NeedsResync { get { return _needsResync; } }

        /// <summary>冻结或待重同步（帧号不再推进）。</summary>
        public bool IsStalled { get { return _frozen || _needsResync; } }

        /// <summary>冻结后累计的墙钟时长（毫秒）。</summary>
        public double FrozenElapsedMs { get { return _frozenElapsedMs; } }

        /// <summary>当前边界（PendingFrame）的累计仿真时间（毫秒）。</summary>
        public double CurrentTotalSimTimeMs { get { return GetBoundaryTotalMs(_pendingFrame); } }

        public long TickedSteps { get { return _tickedSteps; } }
        public long SimulatedSteps { get { return _simulatedSteps; } }
        public long PlaceholderSteps { get { return _placeholderSteps; } }
        public long ReplayedSteps { get { return _replayedSteps; } }
        public long ConfirmedEventsEmitted { get { return _confirmedEventsEmitted; } }
        public long RejectedAuthorities { get { return _rejectedAuthorities; } }
        public long DuplicateAuthorities { get { return _duplicateAuthorities; } }
        public long FutureAuthorities { get { return _futureAuthorities; } }
        public long RoleRejections { get { return _roleRejections; } }
        public long ResyncRequests { get { return _resyncRequests; } }
        public long ResyncApplications { get { return _resyncApplications; } }
        public long EpochRebinds { get { return _epochRebinds; } }
        public long UnconfirmedInputsReset { get { return _unconfirmedInputsReset; } }
        public long PrunedConfirmedSteps { get { return _prunedConfirmedSteps; } }
        public long HistoryExhaustions { get { return _historyExhaustions; } }
        public long HistoryMisses { get { return _historyMisses; } }
        public long WallClockRewindsIgnored { get { return _wallClockRewindsIgnored; } }

        /// <summary>冻结/待重同步期间被拒绝的权威快照次数（状态零改动）。</summary>
        public long StalledAuthorities { get { return _stalledAuthorities; } }

        /// <summary>确认时“已提供权威事件证据”的边界数（含证据为空数组的边界）。</summary>
        public long ConfirmedEventFramesApplied { get { return _confirmedEventFramesApplied; } }

        /// <summary>确认时“未提供权威事件证据”而被跳过广播的边界数。</summary>
        public long ConfirmedEventFramesSkipped { get { return _confirmedEventFramesSkipped; } }

        // ------------------------------------------------------------------ 通知

        /// <summary>确认边界推进的唯一通知点（老边界, 新边界），均在 Input 命名空间。</summary>
        public event Action<PMFrameId, PMFrameId> ConfirmedFrameAdvanced;

        /// <summary>不可逆事件唯一广播点：每条事件只在此处出现一次。</summary>
        public event Action<PMPredictionEvent> EventConfirmed;

        /// <summary>需要宿主向权威请求完整快照（断线超时 / 历史耗尽 / 缺历史）。</summary>
        public event Action ResyncRequested;

        /// <summary>Resync 丢弃了未确认输入（宿主据此清理暂存与表现）。</summary>
        public event Action<int> UnconfirmedInputsResetNotified;

        // ------------------------------------------------------------------ Tick

        /// <summary>
        /// 以「输入帧 n → 输出边界 n+1」推进一帧。
        /// 宿主契约违例（dt 非整毫秒/超范围、帧命名空间错误）fail-fast 抛异常；
        /// 权威/容量/冻结这类运行期条件返回拒绝码且不改状态。
        /// </summary>
        public PMTickResult Tick(in PMTimeStep step, TInput input)
        {
            if (!step.IsWellFormed())
            {
                throw new InvalidOperationException("[PMPredictionTimeline] PMTimeStep 非法（NaN/负 dt/负 BaseSimTimeMs）。");
            }

            int deltaMs = NormalizeStepMsFromFloat(step.StepMs);
            EnsureClientInputFrameMatchesPending(step.ClientInputFrame);
            EnsureServerFrameDomain(step.ServerFrame);
            return TickCore(deltaMs, input, step.ServerFrame);
        }

        /// <summary>本项目首批便捷入口：直接给出整毫秒 dt（0 表示缺帧占位）。</summary>
        public PMTickResult Tick(int deltaMs, TInput input, PMFrameId serverFrame)
        {
            EnsureServerFrameDomain(serverFrame);
            return TickCore(NormalizeStepMs(deltaMs), input, serverFrame);
        }

        private PMTickResult TickCore(int deltaMs, TInput input, PMFrameId serverFrame)
        {
            PMTickResult result = new PMTickResult();
            result.StepMs = deltaMs;
            result.Reject = PMTickRejectReason.None;

            if (_frozen)
            {
                result.Frozen = true;
                result.Reject = PMTickRejectReason.Frozen;
                return result;
            }

            if (_needsResync)
            {
                result.NeedsResync = true;
                result.Reject = PMTickRejectReason.NeedsResync;
                return result;
            }

            if (!TryPruneConfirmedForCapacity())
            {
                _historyExhaustions++;
                EnterNeedsResync();
                result.NeedsResync = true;
                result.Reject = PMTickRejectReason.HistoryExhausted;
                return result;
            }

            Step slot = AcquireSlot();
            long inputFrame = _pendingFrame;
            double boundaryBaseMs = GetBoundaryTotalMs(inputFrame);
            TSync startSync = CloneSyncOrThrow(GetBoundarySyncRef(inputFrame));
            TAux startAux = CloneAuxOrThrow(GetBoundaryAuxRef(inputFrame));

            slot.InputFrame = inputFrame;
            slot.Input = _model.CloneInput(input);
            slot.StepMs = deltaMs;
            slot.Placeholder = (deltaMs == 0);
            slot.ServerFrame = serverFrame;

            if (deltaMs == 0)
            {
                // 0 占位：不调用模型、不累计时间，但显式消耗输入槽并推进输出边界。
                slot.Sync = startSync;
                slot.Aux = startAux;
                slot.TotalSimTimeMs = boundaryBaseMs;
                slot.Events = EmptyEvents;
                _placeholderSteps++;
            }
            else
            {
                PMTimeStep normalized = new PMTimeStep();
                normalized.BaseSimTimeMs = (long)boundaryBaseMs;
                normalized.StepMs = deltaMs;
                normalized.ServerFrame = serverFrame;
                normalized.ClientInputFrame = new PMFrameId(PMFrameDomain.Input, inputFrame);
                normalized.IsResimulating = false;

                PMSimulationResult<TSync, TAux> sim = RunSimulate(
                    normalized, _model.CloneInput(slot.Input), startSync, startAux);
                slot.Sync = CloneSyncOrThrow(sim.Sync);
                slot.Aux = CloneAuxOrThrow(sim.Aux);
                slot.TotalSimTimeMs = boundaryBaseMs + deltaMs;
                slot.Events = CopyAndDeduplicateEvents(sim.Events);
                _simulatedSteps++;
            }

            _count++;
            _pendingFrame++;
            _tickedSteps++;

            result.Accepted = true;
            result.Simulated = (deltaMs != 0);
            result.Placeholder = (deltaMs == 0);
            return result;
        }

        // ------------------------------------------------------------------ 权威快照

        /// <summary>
        /// 应用一份权威快照。顺序固定：校验 → 同帧比较 → （需要时）完整恢复 + 按原 dt 重放 → 推进确认边界。
        /// 迟到/重复边界丢弃且确认边界不回退；缺历史/未来/错 epoch/错 instance 一律 fail-closed（不改状态）。
        /// </summary>
        public PMAuthorityApplyResult ApplyAuthority(in PMPredictionSnapshot<TSync, TAux> authority)
        {
            PMAuthorityApplyResult result = new PMAuthorityApplyResult();
            result.PreviousConfirmedFrame = ConfirmedFrame;
            result.ConfirmedFrame = ConfirmedFrame;
            result.Reject = PMAuthorityRejectReason.None;

            if (authority == null)
            {
                throw new ArgumentNullException("authority");
            }

            // 冻结（断线）与 NeedsResync 期间一切权威快照一律拒绝：预测状态 / 确认边界 /
            // 已确认事件全部保持原样，也不做任何重放。恢复只接受显式可信 Resync
            // （契约「Disconnected 只 Freeze … 状态恢复只接受显式可信 Resync」）。
            if (_frozen || _needsResync)
            {
                _stalledAuthorities++;
                result.Reject = PMAuthorityRejectReason.Stalled;
                return result;
            }

            PMAuthorityRejectReason structural = ValidateSnapshotStructural(authority);
            if (structural != PMAuthorityRejectReason.None)
            {
                _rejectedAuthorities++;
                result.Reject = structural;
                return result;
            }

            if (authority.Epoch != _sessionEpoch)
            {
                _rejectedAuthorities++;
                result.Reject = PMAuthorityRejectReason.EpochMismatch;
                return result;
            }

            if (authority.InstanceId != _instanceId)
            {
                _rejectedAuthorities++;
                result.Reject = PMAuthorityRejectReason.InstanceMismatch;
                return result;
            }

            if (_role != PMNetRole.AutonomousProxy)
            {
                // D-R0-22：只有拥有者客户端回滚。SP/DS 不得在此路径上“顺手”回滚。
                _roleRejections++;
                _rejectedAuthorities++;
                result.Reject = PMAuthorityRejectReason.RoleNotPredictive;
                return result;
            }

            long boundary = authority.OutputFrame.Value;

            if (boundary > _pendingFrame || (boundary - _confirmedFrame) > _futureWindow)
            {
                _futureAuthorities++;
                _rejectedAuthorities++;
                result.Reject = PMAuthorityRejectReason.Future;
                return result;
            }

            if (boundary <= _acceptedBoundary)
            {
                _duplicateAuthorities++;
                _rejectedAuthorities++;
                result.Reject = PMAuthorityRejectReason.StaleOrDuplicate;
                return result;
            }

            if (boundary < _boundaryStartFrame)
            {
                // 历史窗口已经不含该边界：禁止用当前值冒充，必须重同步。
                _historyMisses++;
                _rejectedAuthorities++;
                EnterNeedsResync();
                result.ShouldRequestResync = true;
                result.Reject = PMAuthorityRejectReason.HistoryMissing;
                return result;
            }

            // 权威事件证据：只做只读校验与规范化（深克隆 + 帧内去重），不改任何状态。
            // 「确认事件不能先广播再 reconcile」——广播统一放在恢复/重放完成之后。
            List<EvidenceEntry> evidence;
            if (!TryCollectEvidence(authority, _confirmedFrame, boundary, out evidence))
            {
                _rejectedAuthorities++;
                result.Reject = PMAuthorityRejectReason.Malformed;
                return result;
            }

            bool reconcile = _model.ShouldReconcile(
                GetBoundarySyncRef(boundary), authority.Sync,
                GetBoundaryAuxRef(boundary), authority.Aux);

            if (reconcile)
            {
                result.ResimulatedFrames = RestoreAndReplay(boundary, authority);
                result.Reconciled = true;
                _replayedSteps += result.ResimulatedFrames;
            }

            result.Applied = true;
            _acceptedBoundary = boundary;

            long previousConfirmed = _confirmedFrame;
            if (boundary > previousConfirmed)
            {
                int emitted = BroadcastConfirmedRange(previousConfirmed, boundary, evidence);
                _confirmedFrame = boundary;
                result.AdvancedConfirmed = true;
                result.EventsConfirmed = emitted;
                _confirmedEventsEmitted += emitted;

                Action<PMFrameId, PMFrameId> handler = ConfirmedFrameAdvanced;
                if (handler != null)
                {
                    handler(
                        new PMFrameId(PMFrameDomain.Input, previousConfirmed),
                        new PMFrameId(PMFrameDomain.Input, boundary));
                }
            }

            result.ConfirmedFrame = ConfirmedFrame;
            return result;
        }

        /// <summary>
        /// 显式可信恢复（重同步唯一入口）。
        /// epoch/instance 变化 = 整体重绑（帧与历史全部重建）；
        /// 相同 epoch/instance 时**不得回退确认边界**，只丢弃未确认输入并通知宿主。
        /// </summary>
        public PMResyncResult Resync(in PMPredictionSnapshot<TSync, TAux> snapshot)
        {
            PMResyncResult result = new PMResyncResult();
            result.Reject = PMAuthorityRejectReason.None;

            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }

            PMAuthorityRejectReason structural = ValidateSnapshotStructural(snapshot);
            if (structural != PMAuthorityRejectReason.None)
            {
                result.Reject = structural;
                return result;
            }

            long boundary = snapshot.OutputFrame.Value;

            if (snapshot.Epoch != _sessionEpoch || snapshot.InstanceId != _instanceId)
            {
                if (snapshot.ConfirmedEvents != null
                    || (snapshot.ConfirmedEventFrames != null && snapshot.ConfirmedEventFrames.Length > 0))
                {
                    // 重绑是「换绑定整体重建」：旧绑定的事件证据无法归属到新绑定，
                    // 与其静默丢掉不可逆事件，不如 fail-closed 拒绝（宿主在重绑快照上不带事件证据）。
                    result.Reject = PMAuthorityRejectReason.Malformed;
                    return result;
                }

                int dropped = _count;
                ClearAllSteps();
                _sessionEpoch = snapshot.Epoch;
                _instanceId = snapshot.InstanceId;
                _boundaryStartFrame = boundary;
                _pendingFrame = boundary;
                _confirmedFrame = boundary;
                _baseSync = CloneSyncOrThrow(snapshot.Sync);
                _baseAux = CloneAuxOrThrow(snapshot.Aux);
                _baseTotalSimTimeMs = snapshot.TotalSimTimeMs;
                _baseServerFrame = snapshot.ServerFrame;
                ClearStallState();
                _epochRebinds++;
                _acceptedBoundary = boundary;

                if (dropped > 0)
                {
                    _unconfirmedInputsReset += dropped;
                    NotifyUnconfirmedInputsReset(dropped);
                }

                result.Applied = true;
                result.EpochRebound = true;
                result.ResetUnconfirmedInputs = dropped;
                return result;
            }

            if (boundary < _confirmedFrame)
            {
                // 不得回退确认边界（否则已广播的不可逆事件语义被破坏）。
                result.Reject = PMAuthorityRejectReason.StaleOrDuplicate;
                return result;
            }

            if (boundary > _pendingFrame || (boundary - _confirmedFrame) > _futureWindow)
            {
                result.Reject = PMAuthorityRejectReason.Future;
                return result;
            }

            List<EvidenceEntry> evidence;
            if (!TryCollectEvidence(snapshot, _confirmedFrame, boundary, out evidence))
            {
                result.Reject = PMAuthorityRejectReason.Malformed;
                return result;
            }

            int resetCount = (int)(_pendingFrame - boundary);
            if (resetCount > 0)
            {
                for (int i = _count - resetCount; i < _count; i++)
                {
                    ClearStep(IndexToStep(i));
                }

                _count -= resetCount;
            }

            SetBoundaryState(boundary, snapshot.Sync, snapshot.Aux, snapshot.TotalSimTimeMs, snapshot.ServerFrame);
            _pendingFrame = boundary;

            // 同绑定 Resync 也可能把确认边界推到边界号 boundary：依旧只广播「有权威证据」的边界，
            // 缺证据的边界一律不广播（不得用预测事件冒充权威）。
            if (boundary > _confirmedFrame)
            {
                BroadcastConfirmedRange(_confirmedFrame, boundary, evidence);
            }

            _confirmedFrame = boundary;
            if (boundary > _acceptedBoundary) { _acceptedBoundary = boundary; }
            ClearStallState();

            _resyncApplications++;
            if (resetCount > 0)
            {
                _unconfirmedInputsReset += resetCount;
                NotifyUnconfirmedInputsReset(resetCount);
            }

            result.Applied = true;
            result.ResetUnconfirmedInputs = resetCount;
            return result;
        }

        // ------------------------------------------------------------------ 断线 / 墙钟

        /// <summary>断线：只冻结（帧号停止推进），不丢历史、不改状态。</summary>
        public void Freeze()
        {
            _frozen = true;
            _frozenElapsedMs = 0.0;
            _resyncNotifiedWhileFrozen = false;
        }

        /// <summary>
        /// 宿主墙钟推进。只在冻结期间累计；负增量忽略（时钟倒退不提前触发也不产生信用）。
        /// 累计跨过 2s 阈值时只通知一次，避免每帧重复请求。
        /// </summary>
        public bool NotifyWallClock(double elapsedMs)
        {
            if (elapsedMs <= 0.0)
            {
                if (elapsedMs < 0.0)
                {
                    _wallClockRewindsIgnored++;
                }

                return false;
            }

            if (!_frozen)
            {
                return false;
            }

            _frozenElapsedMs += elapsedMs;
            if (_frozenElapsedMs >= _disconnectTimeoutMs && !_resyncNotifiedWhileFrozen)
            {
                _resyncNotifiedWhileFrozen = true;
                _resyncRequests++;
                Action handler = ResyncRequested;
                if (handler != null)
                {
                    handler();
                }

                return true;
            }

            return false;
        }

        // ------------------------------------------------------------------ 快照读取（一律深克隆）

        public TSync GetSyncSnapshot()
        {
            return CloneSyncOrThrow(GetBoundarySyncRef(_pendingFrame));
        }

        public TAux GetAuxSnapshot()
        {
            return CloneAuxOrThrow(GetBoundaryAuxRef(_pendingFrame));
        }

        public PMPredictionSnapshot<TSync, TAux> GetSnapshot()
        {
            return BuildSnapshot(PendingFrame);
        }

        /// <summary>按边界读取历史状态（深克隆）；边界不在窗口内返回 false。</summary>
        public bool TryGetBoundarySnapshot(PMFrameId boundary, out PMPredictionSnapshot<TSync, TAux> snapshot)
        {
            snapshot = null;
            if (!boundary.IsValid || boundary.Domain != PMFrameDomain.Input)
            {
                throw new InvalidOperationException("[PMPredictionTimeline] 查询边界必须是 Input 命名空间。");
            }

            long b = boundary.Value;
            if (b < _boundaryStartFrame || b > _pendingFrame)
            {
                return false;
            }

            snapshot = BuildSnapshot(boundary);
            return true;
        }

        /// <summary>
        /// 按边界读取该输出边界的事件集合（深克隆）。<paramref name="authoritative"/> 为 true 表示
        /// 该集合已被确认时的权威证据替换；为 false 表示仍是本地预测集合（不得当权威用）。
        /// </summary>
        public bool TryGetBoundaryEvents(PMFrameId boundary, out PMPredictionEvent[] events, out bool authoritative)
        {
            events = null;
            authoritative = false;

            if (!boundary.IsValid || boundary.Domain != PMFrameDomain.Input)
            {
                throw new InvalidOperationException("[PMPredictionTimeline] 查询边界必须是 Input 命名空间。");
            }

            long b = boundary.Value;
            if (b < _boundaryStartFrame || b > _pendingFrame)
            {
                return false;
            }

            if (b == _boundaryStartFrame)
            {
                // 起点边界没有产出它的输入帧，因此没有事件。
                events = EmptyEvents;
                return true;
            }

            Step step = IndexToStep((int)(b - 1 - _boundaryStartFrame));
            events = CopyEvents(step.Events);
            authoritative = step.EventsAuthoritative;
            return true;
        }

        /// <summary>历史中是否仍保留「输入边界 n 产出的输出边界 n+1」的配对。</summary>
        public bool HasInput(long inputFrame)
        {
            long index = inputFrame - _boundaryStartFrame;
            return index >= 0 && index < _count;
        }

        private PMPredictionSnapshot<TSync, TAux> BuildSnapshot(PMFrameId boundary)
        {
            long b = boundary.Value;
            PMPredictionSnapshot<TSync, TAux> snapshot = new PMPredictionSnapshot<TSync, TAux>();
            snapshot.Epoch = _sessionEpoch;
            snapshot.InstanceId = _instanceId;
            snapshot.OutputFrame = boundary;
            snapshot.ServerFrame = GetBoundaryServerFrame(b);
            snapshot.TotalSimTimeMs = GetBoundaryTotalMs(b);
            snapshot.Sync = CloneSyncOrThrow(GetBoundarySyncRef(b));
            snapshot.Aux = CloneAuxOrThrow(GetBoundaryAuxRef(b));
            return snapshot;
        }

        // ------------------------------------------------------------------ 校验

        private static int NormalizeStepMsFromFloat(float stepMs)
        {
            if (float.IsNaN(stepMs) || float.IsInfinity(stepMs))
            {
                throw new InvalidOperationException("[PMPredictionTimeline] dt 为 NaN/Infinity，拒绝推进。");
            }

            int rounded = (int)Math.Round((double)stepMs);
            if (Math.Abs((double)stepMs - rounded) > 0.0001)
            {
                throw new InvalidOperationException(
                    "[PMPredictionTimeline] 首批只接纳整毫秒 dt，收到 " + stepMs + "；不得拆分或放大 dt。");
            }

            return NormalizeStepMs(rounded);
        }

        private static int NormalizeStepMs(int deltaMs)
        {
            if (deltaMs == 0)
            {
                return 0;
            }

            if (deltaMs < MinStepMs || deltaMs > MaxStepMs)
            {
                throw new InvalidOperationException(
                    "[PMPredictionTimeline] dt 超出首批范围 1.." + MaxStepMs + "（0 仅作缺帧占位），收到 " + deltaMs + "。");
            }

            return deltaMs;
        }

        private void EnsureClientInputFrameMatchesPending(PMFrameId clientInputFrame)
        {
            if (!clientInputFrame.IsValid)
            {
                return;
            }

            if (clientInputFrame.Domain != PMFrameDomain.Input)
            {
                throw new InvalidOperationException(
                    "[PMPredictionTimeline] ClientInputFrame 必须是 Input 命名空间，收到 " + clientInputFrame + "。");
            }

            if (clientInputFrame.Value != _pendingFrame)
            {
                throw new InvalidOperationException(
                    "[PMPredictionTimeline] ClientInputFrame 必须等于 PendingFrame（" + _pendingFrame +
                    "），收到 " + clientInputFrame.Value + "。");
            }
        }

        private static void EnsureServerFrameDomain(PMFrameId serverFrame)
        {
            if (serverFrame.IsValid && serverFrame.Domain != PMFrameDomain.AuthorityServer)
            {
                throw new InvalidOperationException(
                    "[PMPredictionTimeline] ServerFrame 必须是 AuthorityServer 命名空间，收到 " + serverFrame + "。");
            }
        }

        /// <summary>与本次绑定无关的结构校验；epoch/instance 是否匹配由调用方按语义处理。</summary>
        private static PMAuthorityRejectReason ValidateSnapshotStructural(PMPredictionSnapshot<TSync, TAux> snapshot)
        {
            if (snapshot.Epoch == 0u || snapshot.InstanceId == 0u)
            {
                return PMAuthorityRejectReason.Malformed;
            }

            if (object.ReferenceEquals(snapshot.Sync, null) || object.ReferenceEquals(snapshot.Aux, null))
            {
                return PMAuthorityRejectReason.Malformed;
            }

            if (!snapshot.OutputFrame.IsValid || snapshot.OutputFrame.Domain != PMFrameDomain.Input)
            {
                return PMAuthorityRejectReason.Malformed;
            }

            if (snapshot.ServerFrame.IsValid && snapshot.ServerFrame.Domain != PMFrameDomain.AuthorityServer)
            {
                return PMAuthorityRejectReason.Malformed;
            }

            return PMAuthorityRejectReason.None;
        }

        // ------------------------------------------------------------------ 模型调用（入参出参一律脱离历史别名）

        private PMSimulationResult<TSync, TAux> RunSimulate(PMTimeStep step, TInput input, TSync start, TAux aux)
        {
            PMSimulationResult<TSync, TAux> result = _model.Simulate(step, input, start, aux);
            if (result == null)
            {
                throw new InvalidOperationException("[PMPredictionTimeline] 模型 Simulate 返回 null。");
            }

            if (object.ReferenceEquals(result.Sync, null))
            {
                throw new InvalidOperationException("[PMPredictionTimeline] 模型 Simulate 返回的 Sync 为 null。");
            }

            if (object.ReferenceEquals(result.Aux, null))
            {
                throw new InvalidOperationException("[PMPredictionTimeline] 模型 Simulate 返回的 Aux 为 null。");
            }

            if (result.Events != null && result.Events.Length > MaxEventsPerFrame)
            {
                // 有界：去重集合的容量由事件条数上限封死，不存在“无限去重 HashSet”。
                throw new InvalidOperationException(
                    "[PMPredictionTimeline] 模型单帧事件条数 " + result.Events.Length + " 超过上限 "
                    + MaxEventsPerFrame + "（模型违反契约）。");
            }

            return result;
        }

        private TSync CloneSyncOrThrow(TSync value)
        {
            if (object.ReferenceEquals(value, null))
            {
                throw new InvalidOperationException("[PMPredictionTimeline] Sync 为 null（模型违反契约）。");
            }

            return _model.CloneSync(value);
        }

        private TAux CloneAuxOrThrow(TAux value)
        {
            if (object.ReferenceEquals(value, null))
            {
                throw new InvalidOperationException("[PMPredictionTimeline] Aux 为 null（模型违反契约）。");
            }

            return _model.CloneAux(value);
        }

        private static PMPredictionEvent[] CopyAndDeduplicateEvents(PMPredictionEvent[] events)
        {
            if (events == null || events.Length == 0)
            {
                return EmptyEvents;
            }

            // 永远复制：模型可以复用同一数组，历史不得与模型内部存储共享引用。
            if (events.Length == 1)
            {
                PMPredictionEvent[] single = new PMPredictionEvent[1];
                single[0] = events[0];
                return single;
            }

            // 有界去重：作用域仅限本帧事件集合，不保留跨帧的全局 HashSet。
            HashSet<ulong> seen = new HashSet<ulong>();
            List<PMPredictionEvent> unique = new List<PMPredictionEvent>(events.Length);
            for (int i = 0; i < events.Length; i++)
            {
                if (seen.Add(events[i].Key))
                {
                    unique.Add(events[i]);
                }
            }

            return unique.ToArray();
        }

        // ------------------------------------------------------------------ 确认与重放

        /// <summary>
        /// 推进确认边界时广播不可逆事件。**只有携带该边界权威事件证据的边界才广播**：
        /// 该边界的预测事件集合被替换为证据集合、标记为权威，并按帧内顺序（已去重）逐条广播一次。
        /// 缺证据的边界一律跳过（不得用预测事件冒充权威，宿主可经可靠事件通道补），
        /// 因此一次确认跨多个边界时不会伪造中间边界的事件。
        /// </summary>
        private int BroadcastConfirmedRange(long previousConfirmed, long newConfirmed, List<EvidenceEntry> evidence)
        {
            int emitted = 0;
            for (long boundary = previousConfirmed + 1; boundary <= newConfirmed; boundary++)
            {
                long inputFrame = boundary - 1;
                long index = inputFrame - _boundaryStartFrame;
                if (index < 0 || index >= _count)
                {
                    // 已确认区间必然仍在窗口内（裁剪只丢弃确认过的记录）；出现即表示不变量被破坏。
                    throw new InvalidOperationException(
                        "[PMPredictionTimeline] 确认区间内的输入不在历史窗口内：input=" + inputFrame + "。");
                }

                PMPredictionEvent[] events = FindEvidence(evidence, boundary);
                Step step = IndexToStep((int)index);

                if (events == null)
                {
                    // 该边界没有权威事件证据：保持预测事件集合原样（非权威），一条都不广播。
                    _confirmedEventFramesSkipped++;
                    continue;
                }

                step.Events = events;
                step.EventsAuthoritative = true;
                _confirmedEventFramesApplied++;

                for (int i = 0; i < events.Length; i++)
                {
                    emitted++;
                    Action<PMPredictionEvent> handler = EventConfirmed;
                    if (handler != null)
                    {
                        handler(events[i]);
                    }
                }
            }

            return emitted;
        }

        // ------------------------------------------------------------------ 权威事件证据

        /// <summary>规范化的权威事件证据条目（深克隆、帧内去重后的集合）。</summary>
        private struct EvidenceEntry
        {
            public long Boundary;
            public PMPredictionEvent[] Events;
        }

        /// <summary>
        /// 只读校验并规范化权威事件证据（<see cref="PMPredictionSnapshot{TSync,TAux}.ConfirmedEvents"/>
        /// 与 <see cref="PMPredictionSnapshot{TSync,TAux}.ConfirmedEventFrames"/>）。
        ///
        /// 通过时 <paramref name="evidence"/> 可能为空列表（本次未提供任何证据）。
        /// 返回 false = 证据畸形：调用方 fail-closed，零状态改动。
        ///
        /// 有界性：证据条目数 ≤ <see cref="MaxConfirmedEventFrames"/>，单帧事件数 ≤
        /// <see cref="MaxEventsPerFrame"/>，总事件数 ≤ <see cref="MaxConfirmedEventsTotal"/>；
        /// 去重只在单个帧的证据集合内进行（HashSet 容量被该帧长度封死），
        /// 不保留跨帧 / 跨调用的全局 HashSet。
        /// </summary>
        private static bool TryCollectEvidence(
            PMPredictionSnapshot<TSync, TAux> authority,
            long previousConfirmed,
            long boundary,
            out List<EvidenceEntry> evidence)
        {
            evidence = null;

            bool hasFrames = authority.ConfirmedEventFrames != null && authority.ConfirmedEventFrames.Length > 0;
            bool hasBoundaryEvents = authority.ConfirmedEvents != null;

            if (!hasFrames && !hasBoundaryEvents)
            {
                evidence = new List<EvidenceEntry>();
                return true;
            }

            // 证据只描述本次调用真正要确认的边界区间；没有可确认的区间时携带证据视为畸形。
            if (boundary <= previousConfirmed)
            {
                return false;
            }

            if (hasFrames && authority.ConfirmedEventFrames.Length > MaxConfirmedEventFrames)
            {
                return false;
            }

            List<EvidenceEntry> collected = new List<EvidenceEntry>();
            int totalEvents = 0;

            if (hasFrames)
            {
                for (int i = 0; i < authority.ConfirmedEventFrames.Length; i++)
                {
                    PMPredictionEventFrame frame = authority.ConfirmedEventFrames[i];
                    if (frame.Events == null)
                    {
                        // 该条目等于“本边界未提供证据”，不参与广播。
                        continue;
                    }

                    long frameBoundary;
                    PMPredictionEvent[] events;
                    if (!TryNormalizeEvidenceFrame(frame, previousConfirmed, boundary,
                                                   out frameBoundary, out events))
                    {
                        return false;
                    }

                    for (int k = 0; k < collected.Count; k++)
                    {
                        if (collected[k].Boundary == frameBoundary)
                        {
                            // 同一边界重复给证据：语义有歧义，拒绝而不是任选一条。
                            return false;
                        }
                    }

                    totalEvents += events.Length;
                    if (totalEvents > MaxConfirmedEventsTotal)
                    {
                        return false;
                    }

                    EvidenceEntry entry = new EvidenceEntry();
                    entry.Boundary = frameBoundary;
                    entry.Events = events;
                    collected.Add(entry);
                }
            }

            if (hasBoundaryEvents)
            {
                // ConfirmedEvents 等价于「一条 OutputFrame = 本次权威边界 的证据」。
                // 若 ConfirmedEventFrames 已给出同一边界的证据，以成对形式为准，不重复广播。
                bool already = false;
                for (int k = 0; k < collected.Count; k++)
                {
                    if (collected[k].Boundary == boundary)
                    {
                        already = true;
                        break;
                    }
                }

                if (!already)
                {
                    PMPredictionEvent[] events;
                    if (!TryNormalizeEvents(authority.ConfirmedEvents, out events))
                    {
                        return false;
                    }

                    totalEvents += events.Length;
                    if (totalEvents > MaxConfirmedEventsTotal)
                    {
                        return false;
                    }

                    EvidenceEntry entry = new EvidenceEntry();
                    entry.Boundary = boundary;
                    entry.Events = events;
                    collected.Add(entry);
                }
            }

            collected.Sort(CompareEvidenceByBoundary);
            evidence = collected;
            return true;
        }

        private static int CompareEvidenceByBoundary(EvidenceEntry a, EvidenceEntry b)
        {
            return a.Boundary.CompareTo(b.Boundary);
        }

        private static bool TryNormalizeEvidenceFrame(PMPredictionEventFrame frame, long previousConfirmed, long boundary,
                                                     out long frameBoundary, out PMPredictionEvent[] events)
        {
            frameBoundary = 0L;
            events = null;

            if (!frame.OutputFrame.IsValid || frame.OutputFrame.Domain != PMFrameDomain.Input)
            {
                return false;
            }

            frameBoundary = frame.OutputFrame.Value;
            if (frameBoundary <= previousConfirmed || frameBoundary > boundary)
            {
                return false;
            }

            return TryNormalizeEvents(frame.Events, out events);
        }

        /// <summary>深克隆 + 帧内去重（有界，见 <see cref="TryCollectEvidence"/>）。</summary>
        private static bool TryNormalizeEvents(PMPredictionEvent[] source, out PMPredictionEvent[] events)
        {
            events = null;

            if (source == null)
            {
                return false;
            }

            if (source.Length > MaxEventsPerFrame)
            {
                return false;
            }

            if (source.Length == 0)
            {
                events = EmptyEvents;
                return true;
            }

            for (int i = 0; i < source.Length; i++)
            {
                if (source[i].Key == 0UL)
                {
                    return false;
                }
            }

            if (source.Length == 1)
            {
                PMPredictionEvent[] single = new PMPredictionEvent[1];
                single[0] = source[0];
                events = single;
                return true;
            }

            HashSet<ulong> seen = new HashSet<ulong>();
            List<PMPredictionEvent> unique = new List<PMPredictionEvent>(source.Length);
            for (int i = 0; i < source.Length; i++)
            {
                if (seen.Add(source[i].Key))
                {
                    unique.Add(source[i]);
                }
            }

            events = unique.ToArray();
            return true;
        }

        private static PMPredictionEvent[] FindEvidence(List<EvidenceEntry> evidence, long boundary)
        {
            if (evidence == null)
            {
                return null;
            }

            for (int i = 0; i < evidence.Count; i++)
            {
                if (evidence[i].Boundary == boundary)
                {
                    return evidence[i].Events;
                }
            }

            return null;
        }

        private static PMPredictionEvent[] CopyEvents(PMPredictionEvent[] source)
        {
            if (source == null || source.Length == 0)
            {
                return EmptyEvents;
            }

            PMPredictionEvent[] copy = new PMPredictionEvent[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                copy[i] = source[i];
            }

            return copy;
        }

        private int RestoreAndReplay(long boundary, PMPredictionSnapshot<TSync, TAux> authority)
        {
            long keepInputs = boundary - _boundaryStartFrame;

            // 1) 边界 boundary 的状态整体替换为权威值（含累计仿真时间与 ServerFrame 元数据）。
            SetBoundaryState(boundary, authority.Sync, authority.Aux, authority.TotalSimTimeMs, authority.ServerFrame);

            // 2) 从 boundary 起用**原始输入与原始 dt**重放；Aux 沿恢复后的权威版本继续 carry，
            //    绝不把旧的历史 Aux 盲覆盖回去。重放只改“产出”，输入槽与 dt 保持原样。
            int replayed = 0;
            for (long index = keepInputs; index < _count; index++)
            {
                Step step = IndexToStep((int)index);
                long inputFrame = step.InputFrame;
                double baseMs = GetBoundaryTotalMs(inputFrame);
                TSync startSync = CloneSyncOrThrow(GetBoundarySyncRef(inputFrame));
                TAux startAux = CloneAuxOrThrow(GetBoundaryAuxRef(inputFrame));

                if (step.Placeholder)
                {
                    step.Sync = startSync;
                    step.Aux = startAux;
                    step.TotalSimTimeMs = baseMs;
                    step.Events = EmptyEvents;
                }
                else
                {
                    PMTimeStep normalized = new PMTimeStep();
                    normalized.BaseSimTimeMs = (long)baseMs;
                    normalized.StepMs = step.StepMs;
                    normalized.ServerFrame = authority.ServerFrame;
                    normalized.ClientInputFrame = new PMFrameId(PMFrameDomain.Input, inputFrame);
                    normalized.IsResimulating = true;

                    TInput replayedInput = _model.CloneInput(step.Input);
                    PMSimulationResult<TSync, TAux> sim = RunSimulate(normalized, replayedInput, startSync, startAux);
                    step.Sync = CloneSyncOrThrow(sim.Sync);
                    step.Aux = CloneAuxOrThrow(sim.Aux);
                    step.TotalSimTimeMs = baseMs + step.StepMs;
                    step.Events = CopyAndDeduplicateEvents(sim.Events);
                }

                step.ServerFrame = authority.ServerFrame;
                replayed++;
            }

            return replayed;
        }

        private void SetBoundaryState(long boundary, TSync sync, TAux aux, double totalSimTimeMs, PMFrameId serverFrame)
        {
            if (boundary == _boundaryStartFrame)
            {
                _baseSync = CloneSyncOrThrow(sync);
                _baseAux = CloneAuxOrThrow(aux);
                _baseTotalSimTimeMs = totalSimTimeMs;
                _baseServerFrame = serverFrame;
                return;
            }

            long index = boundary - 1 - _boundaryStartFrame;
            if (index < 0 || index >= _count)
            {
                throw new InvalidOperationException("[PMPredictionTimeline] SetBoundaryState 越界：boundary=" + boundary + "。");
            }

            Step step = IndexToStep((int)index);
            step.Sync = CloneSyncOrThrow(sync);
            step.Aux = CloneAuxOrThrow(aux);
            step.TotalSimTimeMs = totalSimTimeMs;
            step.ServerFrame = serverFrame;
        }

        // ------------------------------------------------------------------ 边界状态读取

        private TSync GetBoundarySyncRef(long boundary)
        {
            int index = ResolveBoundaryIndex(boundary);
            return index < 0 ? _baseSync : IndexToStep(index).Sync;
        }

        private TAux GetBoundaryAuxRef(long boundary)
        {
            int index = ResolveBoundaryIndex(boundary);
            return index < 0 ? _baseAux : IndexToStep(index).Aux;
        }

        private double GetBoundaryTotalMs(long boundary)
        {
            int index = ResolveBoundaryIndex(boundary);
            return index < 0 ? _baseTotalSimTimeMs : IndexToStep(index).TotalSimTimeMs;
        }

        private PMFrameId GetBoundaryServerFrame(long boundary)
        {
            int index = ResolveBoundaryIndex(boundary);
            return index < 0 ? _baseServerFrame : IndexToStep(index).ServerFrame;
        }

        /// <summary>把边界号映射到槽位下标；边界等于 _boundaryStartFrame 时返回 -1（表示 _base*）。</summary>
        private int ResolveBoundaryIndex(long boundary)
        {
            if (boundary == _boundaryStartFrame)
            {
                return -1;
            }

            long index = boundary - 1 - _boundaryStartFrame;
            if (index < 0 || index >= _count)
            {
                throw new InvalidOperationException("[PMPredictionTimeline] 读取边界越界：boundary=" + boundary + "。");
            }

            return (int)index;
        }

        // ------------------------------------------------------------------ 环形缓冲

        private Step IndexToStep(int index)
        {
            if (index < 0)
            {
                return null;
            }

            return _slots[(_head + index) % _capacity];
        }

        private Step AcquireSlot()
        {
            int slotIndex = (_head + _count) % _capacity;
            Step slot = _slots[slotIndex];
            if (slot == null)
            {
                slot = new Step();
                _slots[slotIndex] = slot;
            }
            else
            {
                ClearStep(slot);
            }

            return slot;
        }

        private bool TryPruneConfirmedForCapacity()
        {
            while (_count == _capacity)
            {
                long oldestOutputBoundary = _boundaryStartFrame + 1;
                if (oldestOutputBoundary > _confirmedFrame)
                {
                    // 满且未确认：禁止静默覆盖。
                    return false;
                }

                Step oldest = _slots[_head];

                // 所有权转移：该克隆成为新基准，槽位随即清空，不存在共享引用。
                _baseSync = oldest.Sync;
                _baseAux = oldest.Aux;
                _baseTotalSimTimeMs = oldest.TotalSimTimeMs;
                _baseServerFrame = oldest.ServerFrame;
                _boundaryStartFrame = oldestOutputBoundary;

                ClearStep(oldest);
                _head = (_head + 1) % _capacity;
                _count--;
                _prunedConfirmedSteps++;
            }

            return true;
        }

        private void ClearAllSteps()
        {
            for (int i = 0; i < _capacity; i++)
            {
                if (_slots[i] != null)
                {
                    ClearStep(_slots[i]);
                }
            }

            _head = 0;
            _count = 0;
        }

        private static void ClearStep(Step step)
        {
            step.InputFrame = 0L;
            step.Input = default(TInput);
            step.StepMs = 0;
            step.Placeholder = false;
            step.ServerFrame = PMFrameId.None;
            step.Sync = default(TSync);
            step.Aux = default(TAux);
            step.TotalSimTimeMs = 0.0;
            step.Events = null;
            step.EventsAuthoritative = false;
        }

        private void EnterNeedsResync()
        {
            _needsResync = true;
            _resyncRequests++;
            Action handler = ResyncRequested;
            if (handler != null)
            {
                handler();
            }
        }

        private void ClearStallState()
        {
            _frozen = false;
            _needsResync = false;
            _frozenElapsedMs = 0.0;
            _resyncNotifiedWhileFrozen = false;
        }

        private void NotifyUnconfirmedInputsReset(int count)
        {
            Action<int> handler = UnconfirmedInputsResetNotified;
            if (handler != null)
            {
                handler(count);
            }
        }
    }
}
