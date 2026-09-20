using System;
using System.Collections.Generic;

namespace PMNet.Prediction
{
    /// <summary>
    /// DS 侧一条待接纳的客户端输入。帧号在 <see cref="PMFrameDomain.Input"/> 命名空间，
    /// 必须从 <c>NextInputFrame</c> 起逐帧连续。
    /// </summary>
    public struct PMAuthorityInput<TInput>
    {
        public uint Epoch;
        public uint InstanceId;
        public long InputFrame;
        public TInput Input;

        /// <summary>整毫秒时间步：0 = 缺帧占位（不调用模型），1..50 = 正常输入。</summary>
        public int StepMs;

        public bool IsPlaceholder { get { return StepMs == 0; } }
    }

    public enum PMInputRejectReason
    {
        None = 0,
        /// <summary>epoch/instance 为 0。</summary>
        MalformedEpochOrInstance = 1,
        EpochMismatch = 2,
        InstanceMismatch = 3,
        /// <summary>已接纳过或早于下一待接纳帧。</summary>
        DuplicateOrStale = 4,
        /// <summary>超出未来窗口。</summary>
        BeyondWindow = 5,
        QueueFull = 6,
        /// <summary>dt 非法（只允许 0 或 1..50 的整毫秒）。</summary>
        MalformedStepMs = 7,
        /// <summary>队列已进入 NeedsResync，必须先显式 Resync。</summary>
        NeedsResync = 8,
    }

    public struct PMInputAdmissionResult
    {
        public bool Accepted;
        public bool Queued;
        public int QueuedCount;
        public PMInputRejectReason Reject;
    }

    public enum PMPumpDeferReason
    {
        None = 0,
        /// <summary>队列没有可连续消费的输入。</summary>
        Idle = 1,
        /// <summary>达到每 Pump 步数上限（8）。</summary>
        StepLimit = 2,
        /// <summary>达到每 Pump 输入时间上限（100ms）。</summary>
        InputTimeLimit = 3,
        /// <summary>信用不足，延后（不拆分、不放大小 dt）。</summary>
        InsufficientCredit = 4,
        /// <summary>缺帧：先等待，不跨越。</summary>
        MissingFrameGap = 5,
        /// <summary>已进入 NeedsResync。</summary>
        NeedsResync = 6,
    }

    public struct PMPumpResult
    {
        public int Steps;
        public int SimulatedSteps;
        public int Placeholders;
        public int ConsumedMs;
        public double CreditMsBefore;
        public double CreditMsAfter;
        public double WallElapsedMs;
        /// <summary>本次是否发出了 ResyncRequested 通知。</summary>
        public bool ResyncRequested;
        public PMPumpDeferReason Defer;
        public long NextInputFrame;
    }

    /// <summary>
    /// DS 输入接纳与预算（M07 / DS 侧）。纯 C#，只处理「准入 + 信用 + 节拍」，不依赖玩法模型。
    ///
    /// 冻结事实（Docs/plans/net-r4-prediction-contract.md）：
    /// 1. 每 Pump 最多 8 步 / 100ms 输入时间；信用按服务器墙钟 1:1 补充，上限 200ms，初值 50ms。
    /// 2. 正常输入保留原始 dt；信用不足时**延后**，绝不拆分或放大 dt。
    /// 3. 输入必须逐帧连续：重复/迟到拒绝，乱序等待（缺帧不跨越），超时则请求 Resync 而不是无界卡住。
    /// 4. 0 占位不调用模型，但仍消耗步数与一个输入槽（边界照常推进）。
    ///
    /// 这些都是本项目首批保守可配置默认，不是 UE 默认值。
    /// </summary>
    public sealed class PMAuthorityInputBuffer<TInput>
    {
        /// <summary>可接纳的未来窗口（未被消费帧最多领先下一待消费帧多少）。</summary>
        public const int DefaultFutureWindow = 128;

        /// <summary>等待队列容量上限（独立于未来窗口，用于“队列容量失败且无副作用”）。</summary>
        public const int DefaultQueueCapacity = 128;

        /// <summary>每 Pump 最多步数。</summary>
        public const int DefaultMaxStepsPerPump = 8;

        /// <summary>每 Pump 最多输入时间（毫秒）。</summary>
        public const int DefaultMaxInputMsPerPump = 100;

        /// <summary>信用上限（毫秒）。</summary>
        public const double DefaultCreditCapMs = 200.0;

        /// <summary>初始信用（毫秒）。</summary>
        public const double DefaultInitialCreditMs = 50.0;

        /// <summary>缺帧等待超过该墙钟时长即请求 Resync（本项目首批值）。</summary>
        public const long DefaultMissingFrameTimeoutMs = 500L;

        public const int MinStepMs = 1;
        public const int MaxStepMs = 50;

        private readonly Func<TInput, TInput> _inputCloner;
        private readonly List<PMAuthorityInput<TInput>> _queue = new List<PMAuthorityInput<TInput>>();
        private readonly int _queueCapacity;
        private readonly int _maxStepsPerPump;
        private readonly int _maxInputMsPerPump;
        private readonly int _futureWindow;
        private readonly double _creditCapMs;
        private readonly long _missingFrameTimeoutMs;

        private uint _epoch;
        private uint _instanceId;
        private long _nextInputFrame;
        private double _creditMs;
        private bool _needsResync;
        private double _missingFrameElapsedMs;

        private long _admittedCount;
        private long _duplicateRejections;
        private long _epochRejections;
        private long _instanceRejections;
        private long _beyondWindowRejections;
        private long _queueFullRejections;
        private long _malformedRejections;
        private long _needsResyncRejections;
        private long _placeholderAdmitted;
        private long _stepsPumped;
        private long _simulatedStepsPumped;
        private long _placeholdersPumped;
        private long _pumpedMs;
        private long _deferredByCredit;
        private long _deferredByStepLimit;
        private long _deferredByTimeLimit;
        private long _missingFrameTimeouts;
        private long _resyncRequests;
        private long _wallClockRewindsIgnored;
        private double _wallClockTotalMs;

        public PMAuthorityInputBuffer(
            Func<TInput, TInput> inputCloner,
            uint epoch,
            uint instanceId,
            int queueCapacity = DefaultQueueCapacity,
            int maxStepsPerPump = DefaultMaxStepsPerPump,
            int maxInputMsPerPump = DefaultMaxInputMsPerPump,
            double initialCreditMs = DefaultInitialCreditMs,
            double creditCapMs = DefaultCreditCapMs,
            int futureWindow = DefaultFutureWindow,
            long missingFrameTimeoutMs = DefaultMissingFrameTimeoutMs)
        {
            if (inputCloner == null) { throw new ArgumentNullException("inputCloner"); }
            if (epoch == 0u) { throw new ArgumentOutOfRangeException("epoch"); }
            if (instanceId == 0u) { throw new ArgumentOutOfRangeException("instanceId"); }
            if (queueCapacity < 1) { throw new ArgumentOutOfRangeException("queueCapacity"); }
            if (maxStepsPerPump < 1) { throw new ArgumentOutOfRangeException("maxStepsPerPump"); }
            if (maxInputMsPerPump < 1) { throw new ArgumentOutOfRangeException("maxInputMsPerPump"); }
            if (initialCreditMs < 0.0) { throw new ArgumentOutOfRangeException("initialCreditMs"); }
            if (creditCapMs < initialCreditMs) { throw new ArgumentOutOfRangeException("creditCapMs"); }
            if (futureWindow < 1) { throw new ArgumentOutOfRangeException("futureWindow"); }

            _inputCloner = inputCloner;
            _epoch = epoch;
            _instanceId = instanceId;
            _queueCapacity = queueCapacity;
            _maxStepsPerPump = maxStepsPerPump;
            _maxInputMsPerPump = maxInputMsPerPump;
            _futureWindow = futureWindow;
            _creditCapMs = creditCapMs;
            _missingFrameTimeoutMs = missingFrameTimeoutMs;
            _creditMs = initialCreditMs;
            _nextInputFrame = 0L;
        }

        // ------------------------------------------------------------------ 只读视图与计数

        public uint Epoch { get { return _epoch; } }
        public uint InstanceId { get { return _instanceId; } }

        /// <summary>下一待消费的输入帧号（Input 命名空间）。</summary>
        public long NextInputFrame { get { return _nextInputFrame; } }

        public int QueuedCount { get { return _queue.Count; } }
        public int QueueCapacity { get { return _queueCapacity; } }
        public double CreditMs { get { return _creditMs; } }
        public double CreditCapMs { get { return _creditCapMs; } }
        public double WallClockTotalMs { get { return _wallClockTotalMs; } }
        public bool NeedsResync { get { return _needsResync; } }

        /// <summary>队列的最小帧号；空队列返回 -1。</summary>
        public long QueuedHeadFrame { get { return _queue.Count == 0 ? -1L : _queue[0].InputFrame; } }

        /// <summary>队列里是否存在缺口（最小帧号大于下一待消费帧）。</summary>
        public bool HasGap { get { return _queue.Count > 0 && _queue[0].InputFrame > _nextInputFrame; } }

        public double MissingFrameElapsedMs { get { return _missingFrameElapsedMs; } }

        public long AdmittedCount { get { return _admittedCount; } }
        public long DuplicateRejections { get { return _duplicateRejections; } }
        public long EpochRejections { get { return _epochRejections; } }
        public long InstanceRejections { get { return _instanceRejections; } }
        public long BeyondWindowRejections { get { return _beyondWindowRejections; } }
        public long QueueFullRejections { get { return _queueFullRejections; } }
        public long MalformedRejections { get { return _malformedRejections; } }
        public long NeedsResyncRejections { get { return _needsResyncRejections; } }
        public long PlaceholderAdmitted { get { return _placeholderAdmitted; } }
        public long StepsPumped { get { return _stepsPumped; } }
        public long SimulatedStepsPumped { get { return _simulatedStepsPumped; } }
        public long PlaceholdersPumped { get { return _placeholdersPumped; } }
        public long PumpedMs { get { return _pumpedMs; } }
        public long DeferredByCredit { get { return _deferredByCredit; } }
        public long DeferredByStepLimit { get { return _deferredByStepLimit; } }
        public long DeferredByTimeLimit { get { return _deferredByTimeLimit; } }
        public long MissingFrameTimeouts { get { return _missingFrameTimeouts; } }
        public long ResyncRequests { get { return _resyncRequests; } }
        public long WallClockRewindsIgnored { get { return _wallClockRewindsIgnored; } }

        /// <summary>缺帧超时 / 需要重建时通知宿主去请求可信快照。</summary>
        public event Action ResyncRequested;

        // ------------------------------------------------------------------ 接纳

        /// <summary>
        /// 准入一条输入。拒绝时状态零改动（不占队列、不推水位、不动信用）。
        /// 复制用注入的 cloner，避免与上行缓冲共享可变数组。
        /// </summary>
        public PMInputAdmissionResult Admit(in PMAuthorityInput<TInput> input)
        {
            PMInputAdmissionResult result = new PMInputAdmissionResult();
            result.Reject = PMInputRejectReason.None;
            result.QueuedCount = _queue.Count;

            if (_needsResync)
            {
                _needsResyncRejections++;
                result.Reject = PMInputRejectReason.NeedsResync;
                return result;
            }

            if (input.Epoch == 0u || input.InstanceId == 0u)
            {
                _malformedRejections++;
                result.Reject = PMInputRejectReason.MalformedEpochOrInstance;
                return result;
            }

            if (input.Epoch != _epoch)
            {
                _epochRejections++;
                result.Reject = PMInputRejectReason.EpochMismatch;
                return result;
            }

            if (input.InstanceId != _instanceId)
            {
                _instanceRejections++;
                result.Reject = PMInputRejectReason.InstanceMismatch;
                return result;
            }

            if (input.StepMs < 0 || input.StepMs > MaxStepMs)
            {
                _malformedRejections++;
                result.Reject = PMInputRejectReason.MalformedStepMs;
                return result;
            }

            if (input.InputFrame < _nextInputFrame)
            {
                _duplicateRejections++;
                result.Reject = PMInputRejectReason.DuplicateOrStale;
                return result;
            }

            if (input.InputFrame - _nextInputFrame > _futureWindow)
            {
                _beyondWindowRejections++;
                result.Reject = PMInputRejectReason.BeyondWindow;
                return result;
            }

            if (_queue.Count >= _queueCapacity)
            {
                _queueFullRejections++;
                result.Reject = PMInputRejectReason.QueueFull;
                return result;
            }

            int insertAt = FindInsertIndex(input.InputFrame);
            if (insertAt < _queue.Count && _queue[insertAt].InputFrame == input.InputFrame)
            {
                _duplicateRejections++;
                result.Reject = PMInputRejectReason.DuplicateOrStale;
                return result;
            }

            PMAuthorityInput<TInput> stored = new PMAuthorityInput<TInput>();
            stored.Epoch = input.Epoch;
            stored.InstanceId = input.InstanceId;
            stored.InputFrame = input.InputFrame;
            stored.Input = _inputCloner(input.Input);
            stored.StepMs = input.StepMs;

            _queue.Insert(insertAt, stored);
            _admittedCount++;
            if (stored.StepMs == 0)
            {
                _placeholderAdmitted++;
            }

            result.Accepted = true;
            result.Queued = true;
            result.QueuedCount = _queue.Count;
            return result;
        }

        private int FindInsertIndex(long inputFrame)
        {
            int low = 0;
            int high = _queue.Count;
            while (low < high)
            {
                int mid = (low + high) / 2;
                if (_queue[mid].InputFrame < inputFrame)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            return low;
        }

        // ------------------------------------------------------------------ 预算与节拍

        /// <summary>
        /// 推进服务器墙钟并按预算取出可消费的连续输入。取出的输入按帧号升序写入 <paramref name="stepsOut"/>。
        /// 信用不足只延后；缺帧只等待（超时则请求 Resync），不用最大帧直接跨过。
        /// </summary>
        public PMPumpResult Pump(double wallElapsedMs, IList<PMAuthorityInput<TInput>> stepsOut)
        {
            if (stepsOut == null) { throw new ArgumentNullException("stepsOut"); }

            PMPumpResult result = new PMPumpResult();
            result.CreditMsBefore = _creditMs;
            result.WallElapsedMs = wallElapsedMs;
            result.Defer = PMPumpDeferReason.None;

            if (wallElapsedMs > 0.0)
            {
                _creditMs = Math.Min(_creditCapMs, _creditMs + wallElapsedMs);
                _wallClockTotalMs += wallElapsedMs;
            }
            else if (wallElapsedMs < 0.0)
            {
                // 时钟倒退既不补充信用，也不扣减既有信用。
                _wallClockRewindsIgnored++;
            }

            if (_needsResync)
            {
                result.Defer = PMPumpDeferReason.NeedsResync;
                result.CreditMsAfter = _creditMs;
                result.NextInputFrame = _nextInputFrame;
                return result;
            }

            if (_queue.Count > 0 && _queue[0].InputFrame > _nextInputFrame)
            {
                // 缺帧先等待：不跨越、不消耗信用、不推水位。
                if (wallElapsedMs > 0.0)
                {
                    _missingFrameElapsedMs += wallElapsedMs;
                }

                if (_missingFrameElapsedMs >= _missingFrameTimeoutMs)
                {
                    _missingFrameTimeouts++;
                    _needsResync = true;
                    _queue.Clear();
                    RequestResync();
                    result.ResyncRequested = true;
                    result.Defer = PMPumpDeferReason.NeedsResync;
                    result.CreditMsAfter = _creditMs;
                    result.NextInputFrame = _nextInputFrame;
                    return result;
                }

                result.Defer = PMPumpDeferReason.MissingFrameGap;
                result.CreditMsAfter = _creditMs;
                result.NextInputFrame = _nextInputFrame;
                return result;
            }

            _missingFrameElapsedMs = 0.0;

            int consumedMs = 0;
            while (true)
            {
                if (result.Steps >= _maxStepsPerPump)
                {
                    _deferredByStepLimit++;
                    result.Defer = PMPumpDeferReason.StepLimit;
                    break;
                }

                if (_queue.Count == 0 || _queue[0].InputFrame != _nextInputFrame)
                {
                    result.Defer = PMPumpDeferReason.Idle;
                    break;
                }

                PMAuthorityInput<TInput> next = _queue[0];

                if (next.StepMs > 0 && consumedMs + next.StepMs > _maxInputMsPerPump)
                {
                    _deferredByTimeLimit++;
                    result.Defer = PMPumpDeferReason.InputTimeLimit;
                    break;
                }

                if (next.StepMs > 0 && _creditMs < next.StepMs)
                {
                    _deferredByCredit++;
                    result.Defer = PMPumpDeferReason.InsufficientCredit;
                    break;
                }

                _queue.RemoveAt(0);
                _nextInputFrame++;
                _creditMs -= next.StepMs;
                consumedMs += next.StepMs;

                stepsOut.Add(next);

                result.Steps++;
                result.ConsumedMs += next.StepMs;
                if (next.StepMs == 0)
                {
                    result.Placeholders++;
                }
                else
                {
                    result.SimulatedSteps++;
                }

                _stepsPumped++;
                _pumpedMs += next.StepMs;
                if (next.StepMs == 0)
                {
                    _placeholdersPumped++;
                }
                else
                {
                    _simulatedStepsPumped++;
                }
            }

            result.CreditMsAfter = _creditMs;
            result.NextInputFrame = _nextInputFrame;
            return result;
        }

        /// <summary>
        /// 显式重同步：换绑定、清空队列、把水位直接落到 <paramref name="nextInputFrame"/>。
        /// 不补信用（时间倒退不产生信用），只清缺帧计时器。
        /// </summary>
        public int Resync(uint epoch, uint instanceId, long nextInputFrame)
        {
            if (epoch == 0u) { throw new ArgumentOutOfRangeException("epoch"); }
            if (instanceId == 0u) { throw new ArgumentOutOfRangeException("instanceId"); }

            int dropped = _queue.Count;
            _queue.Clear();
            _epoch = epoch;
            _instanceId = instanceId;
            _nextInputFrame = nextInputFrame;
            _needsResync = false;
            _missingFrameElapsedMs = 0.0;
            return dropped;
        }

        private void RequestResync()
        {
            _resyncRequests++;
            Action handler = ResyncRequested;
            if (handler != null)
            {
                handler();
            }
        }
    }
}
