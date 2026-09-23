// R4-B / B1：每对象运动驱动（契约 Docs/plans/net-r4-network-contract.md §B1）。
//
// 职责（一句话）：把**同一个** PMMoverModel 按网络角色接到真实的
// AP 预测时间轴 / DS 输入预算 / SP 插值上，并只经声明承载收发。
//
//   角色 Authority        → DS：PMAuthorityInputBuffer 接纳上行输入 + 预算 Pump + 权威快照/事件下发
//   角色 AutonomousProxy  → AP：PMPredictionTimeline 预测 + 同帧校正重放 + 有界输入重发窗口
//   角色 SimulatedProxy   → SP：PMInterpolationBuffer 只插值（不预测、不回滚）
//
// 契约要点（实现里逐条落地）：
//   1) 身份 = (会话 Epoch, player.NetId.Value) 绑定；StreamVersion 是**输入流重置代次**，
//      既不是对象 ID 也不是会话 Epoch（三者在 Describe() 里分开呈现）。
//   2) 重同步只由 DS 递增 StreamVersion；旧流的输入一律丢弃；快照/事件按「当前流 +
//      上一流（重绑后尚未闭合的迟到批）」接受 —— 后者是**可靠通道保序**推出的事实：
//      上一流的事件批一定先于新流的批到达，因此「收到第一条新流事件批」就是安全的闭合点。
//      新流不得回退输出边界与累计仿真时间。
//   3) AP 只在「可靠初始 / 显式 resync / 首次生命周期初值」重建时间轴；
//      **普通属性更新（快照）绝不重建、更不解冻**。
//   4) 输入重发走**不可靠**通道：一包最多 8 条，取「最旧未确认窗口」+「最新」，
//      既不饿死旧缺口也不丢新鲜度；0ms 上行一律拒绝。
//   5) 事件独立于快照确认游标：DS 经**可靠 RPC**下发「边界 + 顺序序号 + 事件集合」，
//      AP 在状态确认到该边界之后才派发；事件迟于快照也能派发；缓存有界，重复不广播，
//      溢出**显式失败**（绝不淘汰未交付事件）。
//      并且：本 Driver 下发的快照**从不携带事件证据**，因此预测时间轴一条事件都不广播
//      —— 事件只有一个派发者（本文件的事件日志），不存在双重派发。
//   6) 运动 Effect/Layer 只能由 DS 在帧边界提交可信命令；上行不开放该权限。
//
// 为什么事件由 Driver 派生（写清楚，避免被当成"发明"）：
//   R4-A 的 PMMoverModel.Simulate 目前自产 0 条事件（只输出纯数据状态），而 B1 要求事件通道
//   是**活的**、能被真实输入驱动出证据。因此 Driver 在 DS 侧把两次权威状态之间的
//   **离散、不可逆跃迁**记成事件：Mode 变化（kind=1）与落地（Grounded false→true，kind=2）。
//   它们的 Key 只由 (边界, kind) 决定，因此稳定、可去重、可跨重发识别。
//   AP 侧**从不**本地派生事件。
//
// 语言面：纯 C#（C# 7.3 / netstandard2.0）。不得引用 UnityEngine；
//        不自行 socket、不读墙钟（墙钟由宿主以参数传入）。
//
// 线程纪律：与 PMNetIdAllocator 同口径 —— 构造时记录线程，之后跨线程调用 fail-fast，
//          因为复制应用、RPC 收发与仿真都必须在主线程（D13）。

using System;
using System.Collections.Generic;
using PMNet.Mover;
using PMNet.Prediction;

namespace PMNet.R3
{
    /// <summary>
    /// 有界的权威事件日志（AP 侧）。
    ///
    /// 它存在的唯一理由：预测时间轴的 `ApplyAuthority` **不能**为已经退役的旧边界补事件
    /// （契约明确「Snapshot 事件缺证据不广播」），因此事件必须独立携带自己的
    /// 「边界 + 顺序序号」，并在状态确认到该边界之后才派发。
    ///
    /// 语义：
    ///   · 只接受当前流、以及**重绑后尚未闭合的上一流**（可靠通道保序 ⇒ 上一流的批必先到达）；
    ///   · 每个流的 Sequence 必须**恰好**是上一批 + 1，缺口 ⇒ 显式失败（不可跳过）；
    ///   · 已派发过的边界 / 重复 Key 一律不广播（去重）；
    ///   · 容量满 ⇒ 显式失败（<see cref="Failed"/> = true），**绝不**淘汰未交付事件。
    /// </summary>
    public sealed class PMR4MovementEventJournal
    {
        /// <summary>未派发边界上限。</summary>
        public const int MaxPendingBoundaries = 256;

        /// <summary>未派发事件上限（与预测时间轴的 4096 同口径）。</summary>
        public const int MaxPendingEvents = 4096;

        private sealed class Entry
        {
            public long Boundary;
            public List<PMPredictionEvent> Events = new List<PMPredictionEvent>(4);
            public HashSet<ulong> Keys = new HashSet<ulong>();
        }

        private readonly List<Entry> _entries = new List<Entry>(8);

        private uint _streamVersion;
        private int _lastSequence;

        /// <summary>重绑前的流代次（0 = 已闭合/无）。只用于接纳"换绑定后仍在途"的上一流事件批。</summary>
        private uint _previousStream;
        private int _previousSequence;

        private long _dispatchedThrough;
        private int _pendingEvents;
        private bool _failed;
        private string _failureReason;

        public long AcceptedBatches { get; private set; }
        public long DuplicateBatches { get; private set; }
        public long RejectedBatches { get; private set; }
        public long AcceptedPreviousStreamBatches { get; private set; }
        public long ClosedPreviousStream { get; private set; }
        public long DuplicateEvents { get; private set; }
        public long DispatchedEvents { get; private set; }
        public long LateDispatches { get; private set; }
        public long SequenceGaps { get; private set; }
        public long Overflows { get; private set; }
        public long DroppedAboveRebase { get; private set; }

        /// <summary>已显式失败的日志（缺口/溢出）。失败后必须由宿主发起显式重同步。</summary>
        public bool Failed { get { return _failed; } }

        /// <summary>失败原因（诊断）。</summary>
        public string FailureReason { get { return _failureReason; } }

        /// <summary>当前流代次。</summary>
        public uint StreamVersion { get { return _streamVersion; } }

        /// <summary>上一流代次（0 = 已闭合）。</summary>
        public uint PreviousStream { get { return _previousStream; } }

        /// <summary>当前流已连续接受的最后一个序号。</summary>
        public int LastSequence { get { return _lastSequence; } }

        /// <summary>最近一次派发过的边界。</summary>
        public long DispatchedThrough { get { return _dispatchedThrough; } }

        /// <summary>尚未派发的事件条数。</summary>
        public int PendingEvents { get { return _pendingEvents; } }

        /// <summary>尚未派发的边界数。</summary>
        public int PendingBoundaries { get { return _entries.Count; } }

        /// <summary>该日志是否接纳这个流代次的事件批。</summary>
        public bool AcceptsStream(uint streamVersion)
        {
            return streamVersion == _streamVersion
                   || (_previousStream != 0u && streamVersion == _previousStream);
        }

        /// <summary>
        /// 换绑定（DS 升流）：
        ///   · 保留边界 ≤ <paramref name="dispatchedThrough"/>（重绑边界）的未派发事件
        ///     —— 它们在旧流里已经发生，必须继续派发，否则会"漏事件"；
        ///   · 丢弃边界高于重绑边界的条目（旧流不可能有，纯防御）；
        ///   · 上一流保持"可接纳"直到收到第一条新流的批（可靠通道保序 ⇒ 到那时上一流已发完）。
        /// </summary>
        public void Rebind(uint streamVersion, long dispatchedThrough)
        {
            if (streamVersion == 0u) { throw new ArgumentOutOfRangeException("streamVersion"); }

            _previousStream = _streamVersion;
            _previousSequence = _lastSequence;
            _streamVersion = streamVersion;
            _lastSequence = 0;

            DropAbove(dispatchedThrough);
            _failed = false;
            _failureReason = null;
        }

        /// <summary>
        /// 同流显式重同步：保留序号水位（DS 未重置序号）与全部未派发事件，
        /// 只清掉"已失败"标记（重同步已经把状态拉回可信）。
        /// </summary>
        public void RecoverAfterResync(long dispatchedThrough)
        {
            DropAbove(dispatchedThrough);
            _failed = false;
            _failureReason = null;
        }

        /// <summary>清空未派发事件（断线/释放）。保留流代次与已派发水位。</summary>
        public void Clear()
        {
            _entries.Clear();
            _pendingEvents = 0;
            _failed = false;
            _failureReason = null;
        }

        /// <summary>记录一次显式失败（调用方已知原因，例如快照缺历史）。</summary>
        public void Fail(string reason)
        {
            _failed = true;
            _failureReason = reason;
        }

        private void DropAbove(long boundary)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Boundary > boundary)
                {
                    _pendingEvents -= _entries[i].Events.Count;
                    _entries.RemoveAt(i);
                    DroppedAboveRebase++;
                }
            }
        }

        /// <summary>
        /// 接受一批权威事件。任何拒绝都**不改动已缓存的未交付事件**。
        /// 返回 false 时 <paramref name="error"/> 给出原因；调用方应据此决定是否重同步。
        /// </summary>
        public bool TryAccept(PMR4MovementEventBatch batch, out string error)
        {
            error = null;

            if (batch == null)
            {
                error = "事件批为 null";
                RejectedBatches++;
                return false;
            }

            bool current = batch.StreamVersion == _streamVersion;
            bool previous = !current && _previousStream != 0u && batch.StreamVersion == _previousStream;

            if (!current && !previous)
            {
                error = "事件批的流代次不被接纳（批 " + batch.StreamVersion + "，当前 " + _streamVersion
                        + "，上一流 " + _previousStream + "）";
                RejectedBatches++;
                return false;
            }

            int expectedSequence = (current ? _lastSequence : _previousSequence) + 1;

            if (batch.Sequence < expectedSequence)
            {
                // 可靠通道的重复交付：不重复广播，也不算失败。
                DuplicateBatches++;
                error = "事件批序号 " + batch.Sequence + " 已接受过（去重）";
                return false;
            }

            if (batch.Sequence != expectedSequence)
            {
                // 序号跳号 = 可靠流被破坏（或对端 bug）。显式失败，绝不"跳过继续"。
                SequenceGaps++;
                _failed = true;
                _failureReason = "事件批序号缺口：期望 " + expectedSequence + "，收到 " + batch.Sequence;
                error = _failureReason;
                RejectedBatches++;
                return false;
            }

            // 先算容量，**再**做任何改动（失败时不产生半应用）。
            int newEvents = 0;
            int newBoundaries = 0;
            for (int i = 0; i < batch.Events.Length; i++)
            {
                PMR4MovementEventRecord record = batch.Events[i];
                if (record.Boundary <= _dispatchedThrough)
                {
                    continue;
                }

                Entry existing = FindEntry(record.Boundary);
                if (existing != null && existing.Keys.Contains(record.Key))
                {
                    continue;
                }

                if (existing == null)
                {
                    newBoundaries++;
                }

                newEvents++;
            }

            if (_pendingEvents + newEvents > MaxPendingEvents || _entries.Count + newBoundaries > MaxPendingBoundaries)
            {
                Overflows++;
                _failed = true;
                _failureReason = "事件日志容量溢出（事件 " + (_pendingEvents + newEvents) + "/" + MaxPendingEvents
                                 + "，边界 " + (_entries.Count + newBoundaries) + "/" + MaxPendingBoundaries
                                 + "）：按契约显式失败，不淘汰未交付事件";
                error = _failureReason;
                RejectedBatches++;
                return false;
            }

            for (int i = 0; i < batch.Events.Length; i++)
            {
                PMR4MovementEventRecord record = batch.Events[i];
                if (record.Boundary <= _dispatchedThrough)
                {
                    DuplicateEvents++;
                    continue;
                }

                Entry entry = FindEntry(record.Boundary);
                if (entry == null)
                {
                    entry = new Entry();
                    entry.Boundary = record.Boundary;
                    InsertEntry(entry);
                }

                if (!entry.Keys.Add(record.Key))
                {
                    DuplicateEvents++;
                    continue;
                }

                entry.Events.Add(new PMPredictionEvent(record.Key, record.Kind, record.Value));
                _pendingEvents++;
            }

            if (current)
            {
                _lastSequence = batch.Sequence;
                if (_previousStream != 0u)
                {
                    // 第一条新流的批已经到达 ⇒ 可靠通道保序保证上一流已发完 ⇒ 安全闭合。
                    _previousStream = 0u;
                    _previousSequence = 0;
                    ClosedPreviousStream++;
                }
            }
            else
            {
                _previousSequence = batch.Sequence;
                AcceptedPreviousStreamBatches++;
            }

            AcceptedBatches++;
            return true;
        }

        /// <summary>
        /// 把「边界 ≤ <paramref name="confirmedBoundary"/>」的事件按（边界升序，边界内到达序）派发。
        /// 返回派发条数。**不依赖**任何快照证据，因此事件迟于快照到达时仍能派发。
        /// </summary>
        public int DispatchTo(long confirmedBoundary, Action<PMPredictionEvent> sink)
        {
            if (sink == null) { throw new ArgumentNullException("sink"); }

            int dispatched = 0;
            bool late = false;

            while (_entries.Count > 0)
            {
                Entry head = _entries[0];
                if (head.Boundary > confirmedBoundary)
                {
                    break;
                }

                if (head.Boundary < confirmedBoundary)
                {
                    late = true;
                }

                // 先推进「已派发」水位、再回调宿主：回调抛异常时状态已经前移，
                // 同一条不可逆事件**不会被再派发一次**（否则宿主每帧重试就会重复播音效/生成子弹）。
                _entries.RemoveAt(0);
                _pendingEvents -= head.Events.Count;
                if (head.Boundary > _dispatchedThrough)
                {
                    _dispatchedThrough = head.Boundary;
                }

                for (int i = 0; i < head.Events.Count; i++)
                {
                    dispatched++;
                    sink(head.Events[i]);
                }
            }

            DispatchedEvents += dispatched;
            if (dispatched > 0 && late)
            {
                LateDispatches++;
            }

            return dispatched;
        }

        private Entry FindEntry(long boundary)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Boundary == boundary)
                {
                    return _entries[i];
                }
            }

            return null;
        }

        private void InsertEntry(Entry entry)
        {
            int at = 0;
            while (at < _entries.Count && _entries[at].Boundary < entry.Boundary)
            {
                at++;
            }

            _entries.Insert(at, entry);
        }
    }

    /// <summary>
    /// 每对象运动驱动。一个网络副本一个实例（DS 侧是权威副本，客户端侧是 AP 或 SP 副本）。
    ///
    /// 实现 <see cref="IPMMovementNetworkDriver"/>（声明层定义的接缝）：
    /// 五个入口全部由 PMR3Player 的声明承载（RPC 业务实现 / RepNotify）转发进来。
    /// </summary>
    public sealed class PMR4MovementDriver : IPMMovementNetworkDriver, IDisposable
    {
        // ================================================================ 冻结常量 / 事件种类

        /// <summary>DS 派生事件：运动模式发生离散变化（不可逆、只通知一次）。</summary>
        public const int EventKindModeChanged = 1;

        /// <summary>DS 派生事件：由「未着地」变为「着地」。</summary>
        public const int EventKindLanded = 2;

        /// <summary>未派发快照载荷上限（快照是"当前状态"语义，满了丢最旧、保最新）。</summary>
        public const int MaxPendingSnapshotPayloads = 64;

        /// <summary>未处理入站输入载荷上限（DS：满了丢弃并计数，输入本身可重发）。</summary>
        public const int MaxPendingInputPayloads = 256;

        /// <summary>未处理入站事件载荷上限（AP：满了显式失败，随后由重同步闭合）。</summary>
        public const int MaxPendingEventPayloads = 256;

        /// <summary>未处理入站重同步载荷上限（AP）。</summary>
        public const int MaxPendingResyncPayloads = 4;

        /// <summary>未处理入站重同步请求上限（DS）。</summary>
        public const int MaxPendingResyncRequests = 8;

        // ================================================================ 身份与角色

        private readonly PMR3Player _player;
        private readonly IPMMoverCollisionQuery _query;
        private readonly PMNetRole _role;
        private readonly uint _epoch;
        private readonly uint _instanceId;
        private readonly PMMoverModel _model;
        private readonly int _ownerThreadId;

        private uint _streamVersion;
        private int _configVersion;
        private bool _frozen;
        private bool _disposed;
        private bool _initialSnapshotPublished;

        /// <summary>
        /// 最近一次**已确认的权威**累计仿真时间（毫秒）。
        ///
        /// 只用来判定重同步是否“倒退”：不能拿 AP 自己的预测累计时间比（AP 领先权威是正常的，
        /// 而且正是重同步要丢弃的部分），否则会陷入“永远拒绝重同步”。
        /// </summary>
        private double _confirmedTotalSimTimeMs;

        /// <summary>是否已有一个重同步请求在飞（防止未确认窗口满时每帧都发一遍）。</summary>
        private bool _resyncRequestInFlight;

        // ---- AP ----
        private PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState> _timeline;
        private readonly List<PMR4MovementInputEntry> _unacked = new List<PMR4MovementInputEntry>(32);
        private PMR4MovementEventJournal _journal;

        // ---- DS ----
        private PMAuthorityInputBuffer<PMMoverInput> _authority;
        private readonly List<PMAuthorityInput<PMMoverInput>> _pumpSteps = new List<PMAuthorityInput<PMMoverInput>>(16);
        private readonly List<PMMoverEffectRequest> _trustedEffects = new List<PMMoverEffectRequest>(4);
        private readonly List<PMMoverLayerRequest> _trustedLayers = new List<PMMoverLayerRequest>(4);
        private readonly List<uint> _trustedRemovedLayers = new List<uint>(4);
        private PMMoverSyncState _authoritySync;
        private PMMoverAuxState _authorityAux;
        private long _authorityBoundary;
        private double _authorityTotalSimTimeMs;
        private PMFrameId _authorityServerFrame = PMFrameId.None;
        private int _authorityEventSequence;
        private readonly List<PMR4MovementEventRecord> _authorityEventBuffer = new List<PMR4MovementEventRecord>(32);
        private long _autoHostTickId;
        private long _lastHostTickId = long.MinValue;

        // ---- SP ----
        private PMInterpolationBuffer<PMMoverSyncState, PMMoverAuxState> _interp;

        // ---- 入站队列（OnRep / RPC 只入队，真正应用在 ApplyPending）----
        private readonly List<byte[]> _pendingSnapshots = new List<byte[]>(4);
        private readonly List<byte[]> _pendingInputs = new List<byte[]>(4);

        /// <summary>
        /// AP 侧**可靠域**入站队列：<c>ClientMovementEventsV1</c> 与 <c>ClientMovementResyncV1</c>
        /// 共用一个 FIFO，因此「事件 → Resync → 事件 → Resync → 事件」的**到达顺序在消费时被原样保留**。
        ///
        /// 为什么必须同队：两者在传输层共享同一个可靠顺序域，DS 侧也是按序发送
        /// （先 flush 事件批、再发升流重同步）。若象旧实现那样分成两个 List 再
        /// 「先跑完所有 Resync、再跑所有 Events」，同一批里的旧流事件会在重绑到**更晚**代次之后
        /// 才被喂进去 ⇒ 被当成过期流丢弃（漏事件，且只表现为「偶发丢一条不可逆通知」）。
        /// </summary>
        private readonly List<PMR4ReliableInbound> _pendingReliable = new List<PMR4ReliableInbound>(6);

        private int _pendingReliableEvents;
        private int _pendingReliableResyncs;
        private readonly List<uint> _pendingResyncRequests = new List<uint>(2);
        private bool _resyncRequestPending;

        /// <summary>可靠域入站条目的种类（事件 / 重同步）。</summary>
        private enum PMR4ReliableKind
        {
            Events = 1,
            Resync = 2,
        }

        /// <summary>可靠域入站条目（载荷在入队时已与对端缓冲区脱离：RPC 参数是独立数组）。</summary>
        private struct PMR4ReliableInbound
        {
            public PMR4ReliableKind Kind;
            public byte[] Payload;
        }

        // ================================================================ 诊断计数

        public long SnapshotPayloadsQueued;
        public long SnapshotPayloadsApplied;
        public long SnapshotDecodeFailed;
        public long SnapshotRejectedIdentity;
        public long SnapshotRejectedStreamNewer;
        public long SnapshotRejectedWorldVersion;
        public long SnapshotQueueDrops;
        public long SnapshotPayloadsPublished;
        public long InputPayloadsReceived;
        public long InputPayloadsQueued;
        public long InputPayloadQueueDrops;
        public long InputDecodeFailed;
        public long InputRejectedIdentity;
        public long InputRejectedStream;
        public long InputsAdmitted;
        public long InputsRejectedByBuffer;
        public long InputPayloadsSent;
        public long InputEntriesSent;
        public long UnackedRetired;
        public long UnackedWindowExhausted;
        public long EventPayloadsReceived;
        public long EventDecodeFailed;
        public long EventBatchesAccepted;
        public long EventBatchesRejected;
        public long EventPayloadsSent;
        public long ResyncRequestsSent;
        public long ResyncRequestsReceived;
        public long ResyncServed;
        public long ResyncPayloadsReceived;
        public long ResyncApplied;
        public long ResyncRejectedRegressing;
        public long ResyncRejectedOldStream;
        public long ResyncInlineRejected;
        public long DuplicateHostTickRejections;
        public long TrustedCommandsQueued;
        public long ThreadViolations;
        public long FrozenWallClockRequests;
        public long Ticks;
        public long TicksRejected;
        public long Placeholders;

        /// <summary>
        /// 旧实现「占位 epoch 构造 + Resync 显式重绑」绕行的触发次数。
        /// R4-A 核心缺陷（快照构造漏写 <c>_boundaryStartFrame</c>）修复后，重绑退化为普通快照构造，
        /// 本计数恒为 0；字段保留是为了不破坏并行集成方已消费的 API 面。
        /// </summary>
        public long RebindFallbacks;

        /// <summary>SP 经**复制快照**绑定到更新流代次的次数（SP 收不到 owner-only 的重同步 RPC）。</summary>
        public long SnapshotStreamRebinds;

        /// <summary>SP 因「新流但输出边界/累计仿真时间倒退」而被拒的复制快照数。</summary>
        public long SnapshotRebindRejectedRegressing;

        /// <summary>冻结的 SP 拒绝借复制快照升流的次数（断线冻结保持）。</summary>
        public long SnapshotRebindRejectedFrozen;

        /// <summary>不可逆事件派发回调抛异常的次数（状态已前移，不会重复派发同一条事件）。</summary>
        public long EventDispatchFailed;

        /// <summary>最近一次事件回调异常信息（诊断）。</summary>
        public string EventDispatchLastError;

        // ================================================================ 生命周期

        /// <summary>
        /// 构造。
        ///
        /// <paramref name="initialSync"/>/<paramref name="initialAux"/>/<paramref name="initialOutputFrame"/>/
        /// <paramref name="initialTotalSimTimeMs"/> 是**可信初始状态**：
        ///   · DS 侧 = 宿主选的权威出生状态（并且必须在首次生命周期 Flush 前
        ///     调 <see cref="PublishInitialSnapshot"/>，否则客户端拿不到初值）；
        ///   · AP/SP 侧一般不用这个构造，而用
        ///     <see cref="CreateFromPlayerInitialSnapshot"/>（从 Create 记录里的初值建立）。
        ///
        /// 构造成功即把 <c>player.MovementDriver</c> 指向本实例：声明承载收到的
        /// 运动 RPC / OnRep 需要它才能转发进来（协议层不扫反射找实现）。
        /// </summary>
        public PMR4MovementDriver(
            PMR3Player player,
            IPMMoverCollisionQuery query,
            PMNetRole role,
            uint epoch,
            PMMoverSyncState initialSync,
            PMMoverAuxState initialAux,
            long initialOutputFrame = 0L,
            PMFrameId initialServerFrame = default(PMFrameId),
            double initialTotalSimTimeMs = 0.0,
            int historyCapacity = PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState>.DefaultHistoryCapacity,
            uint streamVersion = 1u)
        {
            if (player == null) { throw new ArgumentNullException("player"); }
            if (query == null) { throw new ArgumentNullException("query"); }

            if (role != PMNetRole.Authority && role != PMNetRole.AutonomousProxy && role != PMNetRole.SimulatedProxy)
            {
                throw new ArgumentException(
                    "PMR4MovementDriver 需要明确角色（Authority/AutonomousProxy/SimulatedProxy），收到 " + role, "role");
            }

            if (epoch == 0u) { throw new ArgumentOutOfRangeException("epoch", "epoch 必须非 0"); }
            if (streamVersion == 0u) { throw new ArgumentOutOfRangeException("streamVersion", "streamVersion 必须非 0"); }
            if (!player.NetId.IsValid)
            {
                throw new ArgumentException("player 必须已有有效 NetId（先 world.Spawn / 生命周期创建）", "player");
            }

            if (initialOutputFrame < 0L) { throw new ArgumentOutOfRangeException("initialOutputFrame"); }
            if (!PMR4MovementCodec.IsFinite(initialTotalSimTimeMs) || initialTotalSimTimeMs < 0.0)
            {
                throw new ArgumentOutOfRangeException("initialTotalSimTimeMs");
            }

            if (initialServerFrame.IsValid && initialServerFrame.Domain != PMFrameDomain.AuthorityServer)
            {
                throw new ArgumentException("initialServerFrame 必须是 AuthorityServer 命名空间", "initialServerFrame");
            }

            _player = player;
            _query = query;
            _role = role;
            _epoch = epoch;
            _instanceId = player.NetId.Value;
            _streamVersion = streamVersion;
            _ownerThreadId = Environment.CurrentManagedThreadId;
            _model = new PMMoverModel(query);

            _authoritySync = _model.CloneSync(initialSync);
            _authorityAux = _model.CloneAux(initialAux);
            _authorityAux.CollisionWorldVersion = query.WorldVersion;
            _authorityBoundary = initialOutputFrame;
            _authorityTotalSimTimeMs = initialTotalSimTimeMs;
            _authorityServerFrame = initialServerFrame;
            _confirmedTotalSimTimeMs = initialTotalSimTimeMs;

            player.MovementDriver = this;

            if (role == PMNetRole.Authority)
            {
                _authority = new PMAuthorityInputBuffer<PMMoverInput>(_model.CloneInput, epoch, _instanceId);
                _authority.ResyncRequested += OnAuthorityBufferResyncRequested;

                // DS 自己决定水位，**不采纳客户端自报帧号**。
                _authority.Resync(epoch, _instanceId, initialOutputFrame);
            }
            else if (role == PMNetRole.AutonomousProxy)
            {
                PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> snapshot = BuildSnapshot(
                    initialOutputFrame, initialServerFrame, initialTotalSimTimeMs, _authoritySync, _authorityAux);

                _timeline = new PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState>(
                    _model, snapshot, PMNetRole.AutonomousProxy, historyCapacity);

                _journal = new PMR4MovementEventJournal();
                _journal.Rebind(_streamVersion, initialOutputFrame);
            }
            else
            {
                _interp = PMInterpolationBuffer<PMMoverSyncState, PMMoverAuxState>.For(_model);
                EnqueueSnapshotFromPlayer();
            }
        }

        /// <summary>
        /// 客户端用：按 <paramref name="player"/> 的角色与 **Create 记录里的初值** 建立 Driver。
        /// AP（owner）建预测时间轴；SP 建插值缓冲。失败返回 null 并给出原因（宿主决定重试/告警）。
        /// </summary>
        public static PMR4MovementDriver CreateFromPlayerInitialSnapshot(
            PMR3Player player, IPMMoverCollisionQuery query, uint epoch, out string error)
        {
            error = null;

            if (player == null) { error = "player 为 null"; return null; }
            if (query == null) { error = "query 为 null"; return null; }
            if (epoch == 0u) { error = "epoch 必须非 0"; return null; }

            PMR4MovementSnapshotBlob initial;
            if (!TryDecodePlayerSnapshot(player, out initial, out error))
            {
                return null;
            }

            if (initial.InstanceId != player.NetId.Value)
            {
                error = "初值快照的 instanceId(" + initial.InstanceId + ") 与副本 NetId(" + player.NetId.Value + ") 不符";
                return null;
            }

            if (initial.Epoch != epoch)
            {
                error = "初值快照的 epoch(" + initial.Epoch + ") 与当前会话(" + epoch + ") 不符";
                return null;
            }

            if (initial.Aux.CollisionWorldVersion != query.WorldVersion)
            {
                error = "初值快照的碰撞世界版本(" + initial.Aux.CollisionWorldVersion + ") 与本端("
                        + query.WorldVersion + ") 不符";
                return null;
            }

            PMNetRole role = player.Role;
            if (role != PMNetRole.AutonomousProxy && role != PMNetRole.SimulatedProxy)
            {
                error = "副本角色 " + role + " 不需要客户端 Driver";
                return null;
            }

            PMFrameId serverFrame = initial.ServerFrame > 0L
                ? new PMFrameId(PMFrameDomain.AuthorityServer, initial.ServerFrame)
                : PMFrameId.None;

            return new PMR4MovementDriver(
                player, query, role, epoch, initial.Sync, initial.Aux,
                initial.OutputFrame, serverFrame, initial.TotalSimTimeMs, streamVersion: initial.StreamVersion);
        }

        private static bool TryDecodePlayerSnapshot(PMR3Player player, out PMR4MovementSnapshotBlob blob, out string error)
        {
            blob = null;
            error = null;

            byte[] payload = player.MovementSnapshotPayload;
            if (payload == null || payload.Length == 0)
            {
                error = "副本上没有运动初值快照载荷（DS 是否在首次 Flush 前调了 PublishInitialSnapshot？）";
                return false;
            }

            return PMR4MovementCodec.TryDecodeSnapshot(payload, 0, payload.Length,
                PMR4PayloadKind.Snapshot, out blob, out error);
        }

        private void EnqueueSnapshotFromPlayer()
        {
            byte[] payload = _player.MovementSnapshotPayload;
            if (payload == null || payload.Length == 0)
            {
                return;
            }

            byte[] copy = new byte[payload.Length];
            Buffer.BlockCopy(payload, 0, copy, 0, payload.Length);
            _pendingSnapshots.Add(copy);
            SnapshotPayloadsQueued++;
        }

        // ================================================================ 只读视图

        public PMR3Player Player { get { return _player; } }
        public PMNetRole Role { get { return _role; } }
        public uint Epoch { get { return _epoch; } }

        /// <summary>对象身份（= player.NetId.Value）。</summary>
        public uint InstanceId { get { return _instanceId; } }

        /// <summary>输入流重置代次（非 0）。既不是对象 ID 也不是会话 Epoch。</summary>
        public uint StreamVersion { get { return _streamVersion; } }

        /// <summary>运动参数配置版本（进 Aux 并参与 reconcile）。</summary>
        public int ConfigVersion
        {
            get { return _configVersion; }
            set
            {
                EnsureThread();
                _configVersion = value;
                _authorityAux.ConfigVersion = value;
            }
        }

        /// <summary>输出边界（AP：PendingFrame；DS：权威输出边界；SP：最近权威输出边界）。</summary>
        public PMFrameId OutputBoundary
        {
            get
            {
                if (_role == PMNetRole.Authority) { return new PMFrameId(PMFrameDomain.Input, _authorityBoundary); }
                if (_role == PMNetRole.AutonomousProxy && _timeline != null) { return _timeline.PendingFrame; }
                if (_interp != null) { return _interp.LastOutputFrame; }
                return PMFrameId.None;
            }
        }

        /// <summary>已确认边界（仅 AP 有意义；其他角色返回 None）。</summary>
        public PMFrameId ConfirmedBoundary
        {
            get { return _timeline != null ? _timeline.ConfirmedFrame : PMFrameId.None; }
        }

        /// <summary>AP 未确认输入条数（重发窗口占用）。</summary>
        public int UnackedInputCount { get { return _unacked.Count; } }

        /// <summary>DS 当前累计仿真时间（毫秒）。</summary>
        public double AuthorityTotalSimTimeMs { get { return _authorityTotalSimTimeMs; } }

        /// <summary>DS 输入队列的下一待消费帧号。</summary>
        public long AuthorityNextInputFrame { get { return _authority != null ? _authority.NextInputFrame : -1L; } }

        /// <summary>AP 预测时间轴（仅供诊断/门禁读取；不得用于绕过 Driver 的写入路径）。</summary>
        public PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState> Timeline { get { return _timeline; } }

        /// <summary>AP 事件日志。</summary>
        public PMR4MovementEventJournal Journal { get { return _journal; } }

        /// <summary>DS 输入预算缓冲（诊断）。</summary>
        public PMAuthorityInputBuffer<PMMoverInput> AuthorityBuffer { get { return _authority; } }

        /// <summary>SP 插值缓冲（诊断）。</summary>
        public PMInterpolationBuffer<PMMoverSyncState, PMMoverAuxState> Interpolation { get { return _interp; } }

        /// <summary>是否已冻结（断线）。</summary>
        public bool IsFrozen { get { return _frozen; } }

        /// <summary>是否已释放。</summary>
        public bool IsDisposed { get { return _disposed; } }

        /// <summary>初始快照是否已发布（宿主用于拒绝"还没发初值就 Flush"）。</summary>
        public bool InitialSnapshotPublished { get { return _initialSnapshotPublished; } }

        /// <summary>不可逆权威事件派发出口（AP）。</summary>
        public event Action<PMPredictionEvent> EventDispatched;

        // ================================================================ DS：初始快照与发布

        /// <summary>
        /// 把当前权威状态写成快照并推到复制属性上（DS 侧唯一快照出口）。
        /// 返回是否真的发布了（角色不对/编码失败时不发布）。
        ///
        /// **必须在首次生命周期 Flush 之前调用一次**（契约 §B1）：Create 记录会带上
        /// 声明式初值，客户端据此建立 AP 时间轴/SP 插值缓冲。宿主典型顺序：
        /// <c>world.Spawn → 构造 Driver → PublishInitialSnapshot → bridge.Update(...)</c>。
        /// </summary>
        public bool PublishInitialSnapshot()
        {
            EnsureRole(PMNetRole.Authority, "PublishInitialSnapshot");
            EnsureThread();
            ThrowIfDisposed();

            if (!PublishAuthoritySnapshot(false))
            {
                return false;
            }

            _initialSnapshotPublished = true;
            return true;
        }

        private bool PublishAuthoritySnapshot(bool resync)
        {
            PMR4MovementSnapshotBlob blob = new PMR4MovementSnapshotBlob();
            blob.Epoch = _epoch;
            blob.InstanceId = _instanceId;
            blob.StreamVersion = _streamVersion;
            blob.OutputFrame = _authorityBoundary;
            blob.ServerFrame = _authorityServerFrame.IsValid ? _authorityServerFrame.Value : 0L;
            blob.TotalSimTimeMs = _authorityTotalSimTimeMs;
            blob.Sync = _model.CloneSync(_authoritySync);
            blob.Aux = _model.CloneAux(_authorityAux);

            byte[] payload;
            try
            {
                payload = PMR4MovementCodec.EncodeSnapshot(false, blob);
            }
            catch (FormatException ex)
            {
                _player.WarnMovement("[PMR4MovementDriver] 权威快照编码失败：" + ex.Message);
                return false;
            }

            if (resync)
            {
                // 重同步走**可靠 RPC**（owner-only），与普通快照区分开：
                // 接收侧据此判断"该整体重绑"。普通快照始终同时推复制属性，保持"当前状态"收敛。
                byte[] resyncPayload;
                try
                {
                    resyncPayload = PMR4MovementCodec.EncodeSnapshot(true, blob);
                }
                catch (FormatException ex)
                {
                    _player.WarnMovement("[PMR4MovementDriver] 重同步快照编码失败：" + ex.Message);
                    return false;
                }

                _player.ClientMovementResyncV1(resyncPayload);
            }

            _player.PublishMovementSnapshot(payload);
            SnapshotPayloadsPublished++;
            return true;
        }

        // ================================================================ AP：采样与步进

        /// <summary>
        /// AP 预测一步：输入帧 <c>PendingFrame</c> → 输出边界 +1。
        /// 非法 dt/非法输入是**本地编程错误**，fail-fast 抛异常（不静默丢弃）。
        /// </summary>
        public PMTickResult Tick(int stepMs, PMMoverInput input, PMFrameId serverFrame)
        {
            EnsureRole(PMNetRole.AutonomousProxy, "Tick");
            EnsureThread();
            ThrowIfDisposed();
            ApplyPending();

            string why;
            if (!PMR4MovementCodec.IsValidUplinkInput(input, stepMs, out why))
            {
                throw new FormatException("[PMR4MovementDriver] Tick 的输入违反上行契约：" + why);
            }

            if (_unacked.Count >= PMR4MovementCodec.MaxUnackedInputs)
            {
                // 未确认窗口满 = 服务器没跟上。契约要求"未确认输入最多 128 条"，
                // 因此这里**不**再产出新的预测输入（不静默扩窗、不丢最旧）。
                UnackedWindowExhausted++;
                TicksRejected++;
                RequestResync("unacked-window-exhausted");

                PMTickResult blocked = new PMTickResult();
                blocked.Reject = PMTickRejectReason.HistoryExhausted;
                blocked.NeedsResync = true;
                return blocked;
            }

            PMTimeStep step = new PMTimeStep();
            step.BaseSimTimeMs = 0L;
            step.StepMs = (float)stepMs;
            step.ServerFrame = serverFrame;
            step.ClientInputFrame = _timeline.PendingFrame;
            step.IsResimulating = false;

            long inputFrame = _timeline.PendingFrame.Value;
            PMTickResult result = _timeline.Tick(step, input);
            Ticks++;

            if (!result.Accepted)
            {
                TicksRejected++;
                if (result.NeedsResync)
                {
                    RequestResync("timeline-needs-resync");
                }

                return result;
            }

            if (result.Placeholder)
            {
                Placeholders++;
            }

            PMR4MovementInputEntry entry = new PMR4MovementInputEntry();
            entry.InputFrame = inputFrame;
            entry.StepMs = stepMs;
            entry.Input = _model.CloneInput(input);
            _unacked.Add(entry);

            return result;
        }

        /// <summary>AP 当前预测状态（深克隆）。</summary>
        public PMMoverSyncState GetPredictedSync()
        {
            EnsureRole(PMNetRole.AutonomousProxy, "GetPredictedSync");
            EnsureThread();
            ThrowIfDisposed();
            ApplyPending();
            return _timeline.GetSyncSnapshot();
        }

        /// <summary>AP 当前预测环境状态（深克隆）。</summary>
        public PMMoverAuxState GetPredictedAux()
        {
            EnsureRole(PMNetRole.AutonomousProxy, "GetPredictedAux");
            EnsureThread();
            ThrowIfDisposed();
            ApplyPending();
            return _timeline.GetAuxSnapshot();
        }

        /// <summary>
        /// 构造一包上行输入（**不可靠**通道）。返回 false 表示当前没有未确认输入（不需要发）。
        ///
        /// 选择规则（契约「按最旧未确认窗口持续重发防缺帧饥饿；可兼带最新但不能饿死旧缺口」）：
        ///   · 未确认 ≤ 8 条 ⇒ 全部发出（这时"最新"天然在包内）；
        ///   · 未确认 &gt; 8 条 ⇒ 最旧 4 条 + 最新 4 条（严格升序、不重复）。
        /// 包内条目**不必**连续（DS 侧按帧号排序接纳），因此既能补旧缺口也能带新鲜输入。
        /// </summary>
        public bool TryBuildInputPayload(out byte[] payload)
        {
            payload = null;

            EnsureRole(PMNetRole.AutonomousProxy, "TryBuildInputPayload");
            EnsureThread();
            ThrowIfDisposed();
            ApplyPending();

            RetireUnacked();

            if (_unacked.Count == 0)
            {
                return false;
            }

            PMR4MovementInputEntry[] entries;
            if (_unacked.Count <= PMR4MovementCodec.MaxInputsPerPacket)
            {
                entries = _unacked.ToArray();
            }
            else
            {
                const int half = PMR4MovementCodec.MaxInputsPerPacket / 2;
                entries = new PMR4MovementInputEntry[PMR4MovementCodec.MaxInputsPerPacket];
                for (int i = 0; i < half; i++)
                {
                    entries[i] = _unacked[i];
                    entries[half + i] = _unacked[_unacked.Count - half + i];
                }
            }

            payload = PMR4MovementCodec.EncodeInputs(_epoch, _instanceId, _streamVersion, entries);
            InputPayloadsSent++;
            InputEntriesSent += entries.Length;
            return true;
        }

        /// <summary>一步到位：构造并按生成桩发出上行输入（不可靠）。</summary>
        public bool SendInputPayload()
        {
            byte[] payload;
            if (!TryBuildInputPayload(out payload))
            {
                return false;
            }

            _player.ServerMovementInputV1(payload);
            return true;
        }

        private void RetireUnacked()
        {
            if (_timeline == null || _unacked.Count == 0)
            {
                return;
            }

            long confirmed = _timeline.ConfirmedFrame.Value;
            int keep = 0;
            while (keep < _unacked.Count && _unacked[keep].InputFrame < confirmed)
            {
                keep++;
            }

            if (keep == 0)
            {
                return;
            }

            _unacked.RemoveRange(0, keep);
            UnackedRetired += keep;
        }

        // ================================================================ DS：输入 / Pump

        /// <summary>
        /// DS：处理一条上行输入载荷（由 <c>ServerMovementInputV1</c> 的生成桩 → 业务实现转发）。
        /// 只入队；真正接纳在 <see cref="ApplyPending"/>（本类所有步进入口都会先调它）。
        /// </summary>
        public void OnServerInputPayload(byte[] payload)
        {
            EnsureThread();
            if (_disposed) { return; }
            InputPayloadsReceived++;

            if (_role != PMNetRole.Authority)
            {
                InputRejectedIdentity++;
                return;
            }

            if (_pendingInputs.Count >= MaxPendingInputPayloads)
            {
                InputPayloadQueueDrops++;
                return;
            }

            _pendingInputs.Add(payload);
            InputPayloadsQueued++;
        }

        /// <summary>
        /// DS：处理一条重同步请求（由 <c>ServerMovementResyncV1</c> 的生成桩 → 业务实现转发）。
        /// 只入队。
        /// </summary>
        public void OnServerResyncRequest(uint clientStreamVersion)
        {
            EnsureThread();
            if (_disposed) { return; }
            ResyncRequestsReceived++;

            if (_role != PMNetRole.Authority)
            {
                ResyncInlineRejected++;
                return;
            }

            if (_pendingResyncRequests.Count >= MaxPendingResyncRequests)
            {
                ResyncInlineRejected++;
                return;
            }

            _pendingResyncRequests.Add(clientStreamVersion);
        }

        /// <summary>
        /// DS：在帧边界提交一条**可信**运动 Effect（只允许 DS 调用；上行不开放该权限）。
        /// 它在下一次真正执行的模拟步上生效（随该步的 Input 进入模型），因此
        /// 可在同一帧内做「强制差异」验证。
        /// </summary>
        public bool ServerSubmitTrustedEffect(PMMoverEffectRequest effect)
        {
            if (_role != PMNetRole.Authority)
            {
                return false;
            }

            EnsureThread();
            ThrowIfDisposed();

            _trustedEffects.Add(effect);
            TrustedCommandsQueued++;
            return true;
        }

        /// <summary>DS：在帧边界提交一条**可信**叠加层请求（只允许 DS 调用）。</summary>
        public bool ServerSubmitTrustedLayer(PMMoverLayerRequest layer)
        {
            if (_role != PMNetRole.Authority)
            {
                return false;
            }

            EnsureThread();
            ThrowIfDisposed();

            _trustedLayers.Add(layer);
            TrustedCommandsQueued++;
            return true;
        }

        /// <summary>DS：在帧边界请求移除一条叠加层（只允许 DS 调用）。</summary>
        public bool ServerSubmitTrustedLayerRemoval(uint layerInstanceId)
        {
            if (_role != PMNetRole.Authority)
            {
                return false;
            }

            EnsureThread();
            ThrowIfDisposed();

            if (layerInstanceId == 0u)
            {
                return false;
            }

            _trustedRemovedLayers.Add(layerInstanceId);
            TrustedCommandsQueued++;
            return true;
        }

        /// <summary>DS：按墙钟流逝推进仿真预算（自动分配宿主 tick 序号，每次调用都补充信用）。</summary>
        public PMPumpResult Pump(double elapsedMs, PMFrameId serverFrame)
        {
            return Pump(elapsedMs, serverFrame, unchecked(++_autoHostTickId));
        }

        /// <summary>
        /// DS：按墙钟流逝推进仿真预算（带宿主 tick 序号）。
        /// **同一宿主 tick 重复调用不会重复补充信用**（契约「同一宿主tick不得重复充值」）：
        /// 重复的 tick 序号返回零步结果且不动预算。
        /// </summary>
        public PMPumpResult Pump(double elapsedMs, PMFrameId serverFrame, long hostTickId)
        {
            EnsureRole(PMNetRole.Authority, "Pump");
            EnsureThread();
            ThrowIfDisposed();

            if (serverFrame.IsValid && serverFrame.Domain != PMFrameDomain.AuthorityServer)
            {
                throw new InvalidOperationException(
                    "[PMR4MovementDriver] serverFrame 必须是 AuthorityServer 命名空间，收到 " + serverFrame);
            }

            ApplyPending();

            PMPumpResult result = new PMPumpResult();
            result.Defer = PMPumpDeferReason.None;
            result.NextInputFrame = _authority.NextInputFrame;
            result.CreditMsBefore = _authority.CreditMs;
            result.CreditMsAfter = _authority.CreditMs;

            if (hostTickId == _lastHostTickId)
            {
                DuplicateHostTickRejections++;
                return result;
            }

            _lastHostTickId = hostTickId;

            _pumpSteps.Clear();
            PMPumpResult pumped = _authority.Pump(elapsedMs, _pumpSteps);

            result.Defer = pumped.Defer;
            result.WallElapsedMs = pumped.WallElapsedMs;
            result.Steps = pumped.Steps;
            result.SimulatedSteps = pumped.SimulatedSteps;
            result.Placeholders = pumped.Placeholders;
            result.ConsumedMs = pumped.ConsumedMs;
            result.CreditMsBefore = pumped.CreditMsBefore;
            result.CreditMsAfter = pumped.CreditMsAfter;
            result.ResyncRequested = pumped.ResyncRequested;
            result.NextInputFrame = pumped.NextInputFrame;

            if (serverFrame.IsValid)
            {
                _authorityServerFrame = serverFrame;
            }

            if (pumped.Steps > 0)
            {
                SimulatePumpSteps(serverFrame);
                PublishAuthoritySnapshot(false);
                FlushAuthorityEvents();
            }

            if (pumped.ResyncRequested || _authority.NeedsResync)
            {
                // 缺帧超时 / 队列进入 NeedsResync：由 DS 主动升流并下发完整可信快照
                // （**不**采纳客户端自报帧号决定新水位）。
                BeginServerResync("ds-missing-frame");
                result.ResyncRequested = true;
            }

            return result;
        }

        private void SimulatePumpSteps(PMFrameId serverFrame)
        {
            for (int i = 0; i < _pumpSteps.Count; i++)
            {
                PMAuthorityInput<PMMoverInput> stepInput = _pumpSteps[i];
                long inputFrame = stepInput.InputFrame;

                if (stepInput.StepMs == 0)
                {
                    // 缺帧占位：不调用模型、不累计时间，但推进输出边界（与预测时间轴同口径）。
                    _authorityBoundary = inputFrame + 1L;
                    Placeholders++;
                    continue;
                }

                PMMoverSyncState previous = _model.CloneSync(_authoritySync);
                PMMoverInput input = _model.CloneInput(stepInput.Input);
                ApplyTrustedCommands(ref input);

                PMTimeStep step = new PMTimeStep();
                step.BaseSimTimeMs = (long)_authorityTotalSimTimeMs;
                step.StepMs = (float)stepInput.StepMs;
                step.ServerFrame = serverFrame;
                step.ClientInputFrame = new PMFrameId(PMFrameDomain.Input, inputFrame);
                step.IsResimulating = false;

                PMSimulationResult<PMMoverSyncState, PMMoverAuxState> sim = _model.Simulate(
                    step, input, _authoritySync, _authorityAux);

                if (sim == null)
                {
                    throw new InvalidOperationException("[PMR4MovementDriver] PMMoverModel.Simulate 返回 null");
                }

                _authoritySync = _model.CloneSync(sim.Sync);
                _authorityAux = _model.CloneAux(sim.Aux);
                _authorityAux.CollisionWorldVersion = _query.WorldVersion;
                _authorityTotalSimTimeMs += stepInput.StepMs;
                _authorityBoundary = inputFrame + 1L;

                DeriveAuthoritativeEvents(previous, _authoritySync, inputFrame + 1L);
            }
        }

        private void ApplyTrustedCommands(ref PMMoverInput input)
        {
            if (_trustedEffects.Count == 0 && _trustedLayers.Count == 0 && _trustedRemovedLayers.Count == 0)
            {
                return;
            }

            if (_trustedEffects.Count > 0)
            {
                input.Effects = _trustedEffects.ToArray();
                _trustedEffects.Clear();
            }

            if (_trustedLayers.Count > 0)
            {
                input.Layers = _trustedLayers.ToArray();
                _trustedLayers.Clear();
            }

            if (_trustedRemovedLayers.Count > 0)
            {
                input.RemovedLayerIds = _trustedRemovedLayers.ToArray();
                _trustedRemovedLayers.Clear();
            }
        }

        /// <summary>
        /// DS 派生不可逆的离散跃迁事件（见文件头说明）：Mode 变化 / 落地。
        /// Key 只由 (边界, kind) 决定 —— 稳定、可去重、可跨重发识别。
        /// </summary>
        private void DeriveAuthoritativeEvents(in PMMoverSyncState previous, in PMMoverSyncState current, long boundary)
        {
            if (previous.Mode != current.Mode)
            {
                AddAuthorityEvent(boundary, EventKindModeChanged, (int)current.Mode);
            }

            if (!previous.Grounded && current.Grounded)
            {
                AddAuthorityEvent(boundary, EventKindLanded, (int)current.Mode);
            }
        }

        private void AddAuthorityEvent(long boundary, int kind, int value)
        {
            PMR4MovementEventRecord record = new PMR4MovementEventRecord();
            record.Boundary = boundary;
            record.Kind = kind;
            record.Value = value;
            record.Key = BuildEventKey(boundary, kind);
            _authorityEventBuffer.Add(record);
        }

        /// <summary>事件稳定 Key：只由 (边界, 种类) 决定（非 0）。</summary>
        public static ulong BuildEventKey(long boundary, int kind)
        {
            if (boundary <= 0L) { throw new ArgumentOutOfRangeException("boundary"); }
            if (kind <= 0) { throw new ArgumentOutOfRangeException("kind"); }
            return unchecked((ulong)boundary * 8UL + (ulong)kind);
        }

        private void FlushAuthorityEvents()
        {
            if (_authorityEventBuffer.Count == 0)
            {
                return;
            }

            int offset = 0;
            while (offset < _authorityEventBuffer.Count)
            {
                int take = _authorityEventBuffer.Count - offset;
                if (take > PMR4MovementCodec.MaxEventsPerBatch)
                {
                    take = PMR4MovementCodec.MaxEventsPerBatch;
                }

                PMR4MovementEventRecord[] slice = new PMR4MovementEventRecord[take];
                _authorityEventBuffer.CopyTo(offset, slice, 0, take);

                _authorityEventSequence++;
                byte[] payload = PMR4MovementCodec.EncodeEvents(
                    _epoch, _instanceId, _streamVersion, _authorityEventSequence, slice);

                // 可靠 RPC（owner-only）：事件必须独立于快照确认游标到达。
                _player.ClientMovementEventsV1(payload);
                EventPayloadsSent++;

                offset += take;
            }

            _authorityEventBuffer.Clear();
        }

        /// <summary>DS：当前权威状态（深克隆）。</summary>
        public PMMoverSyncState GetAuthoritativeSync()
        {
            EnsureRole(PMNetRole.Authority, "GetAuthoritativeSync");
            EnsureThread();
            ThrowIfDisposed();
            ApplyPending();
            return _model.CloneSync(_authoritySync);
        }

        /// <summary>DS：当前权威环境状态（深克隆）。</summary>
        public PMMoverAuxState GetAuthoritativeAux()
        {
            EnsureRole(PMNetRole.Authority, "GetAuthoritativeAux");
            EnsureThread();
            ThrowIfDisposed();
            ApplyPending();
            return _model.CloneAux(_authorityAux);
        }

        // ================================================================ DS：重同步

        private void OnAuthorityBufferResyncRequested()
        {
            // 事件在 Pump 内触发：这里只记一个待办（不在缓冲区内部改它的状态）。
            _resyncRequestPending = true;
        }

        /// <summary>
        /// DS 主动开启一个新输入流：递增 StreamVersion、清空旧流队列（水位由 DS 自己决定）、
        /// 下发完整可信快照给 owner。旧流的输入在两侧都按流代次丢弃；
        /// 旧流事件仍可被接纳（可靠通道保序 ⇒ 它们必先到达）以免"漏事件"。
        /// </summary>
        public bool BeginServerResync(string reason)
        {
            EnsureRole(PMNetRole.Authority, "BeginServerResync");
            EnsureThread();
            ThrowIfDisposed();

            if (!_initialSnapshotPublished)
            {
                // 还没发初值就要求重同步：先补初值，避免宿主顺序错误导致客户端永远建不起来。
                PublishInitialSnapshot();
            }

            uint next = _streamVersion + 1u;
            if (next == 0u)
            {
                next = 1u;
            }

            _streamVersion = next;
            _authorityEventSequence = 0;
            _authorityEventBuffer.Clear();
            _trustedEffects.Clear();
            _trustedLayers.Clear();
            _trustedRemovedLayers.Clear();
            _resyncRequestPending = false;

            // 水位落在 DS 自己已经达到的边界上（不采纳客户端自报帧号）。
            _authority.Resync(_epoch, _instanceId, _authorityBoundary);

            PublishAuthoritySnapshot(true);
            ResyncServed++;
            _player.WarnMovement("[PMR4MovementDriver] DS 重同步：" + (reason == null ? "(no reason)" : reason)
                                 + " → streamVersion=" + _streamVersion);
            return true;
        }

        // ================================================================ SP：表现

        /// <summary>SP：推进表现时钟（不预测、不回滚；冻结时不推进）。</summary>
        public void Advance(double deltaMs)
        {
            EnsureRole(PMNetRole.SimulatedProxy, "Advance");
            EnsureThread();
            ThrowIfDisposed();
            ApplyPending();

            if (_frozen)
            {
                return;
            }

            _interp.Advance(deltaMs);
        }

        /// <summary>SP：采样当前表现状态（只插值，超出最新权威值不预测）。</summary>
        public PMInterpolatedState<PMMoverSyncState, PMMoverAuxState> SamplePresentation()
        {
            EnsureRole(PMNetRole.SimulatedProxy, "SamplePresentation");
            EnsureThread();
            ApplyPending();
            return _interp.Sample();
        }

        // ================================================================ 入站转发（复制 / RPC）

        /// <summary>
        /// 复制层通知「运动快照属性更新了」。**只入队**（契约：OnRep 只通知/入队），
        /// 真正解码与应用发生在 <see cref="ApplyPending"/>（本类所有步进入口都会先调它）。
        /// </summary>
        public void OnSnapshotReplicated()
        {
            EnsureThread();
            if (_disposed) { return; }
            SnapshotPayloadsQueued++;

            if (_role == PMNetRole.Authority)
            {
                // 权威端不会被自己的复制属性回调（本地 MarkPropertyDirty 不触发 OnRep）。
                SnapshotRejectedIdentity++;
                return;
            }

            byte[] payload = _player.MovementSnapshotPayload;
            if (payload == null || payload.Length == 0)
            {
                SnapshotDecodeFailed++;
                return;
            }

            if (_pendingSnapshots.Count >= MaxPendingSnapshotPayloads)
            {
                // 快照是「当前状态」语义：满了丢最旧、保最新（与事件的"不得淘汰"相反）。
                _pendingSnapshots.RemoveAt(0);
                SnapshotQueueDrops++;
            }

            byte[] copy = new byte[payload.Length];
            Buffer.BlockCopy(payload, 0, copy, 0, payload.Length);
            _pendingSnapshots.Add(copy);
        }

        /// <summary>AP：处理一条权威事件载荷（由 <c>ClientMovementEventsV1</c> 转发）。只入队。</summary>
        public void OnClientEventsPayload(byte[] payload)
        {
            EnsureThread();
            if (_disposed) { return; }
            EventPayloadsReceived++;

            if (_role != PMNetRole.AutonomousProxy || payload == null || payload.Length == 0)
            {
                EventDecodeFailed++;
                return;
            }

            if (_pendingReliableEvents >= MaxPendingEventPayloads)
            {
                // 事件不允许静默淘汰：队列满 ⇒ 显式失败（随后由宿主重同步闭合）。
                EventDecodeFailed++;
                if (_journal != null)
                {
                    _journal.Fail("事件载荷入站队列溢出（上限 " + MaxPendingEventPayloads + "）");
                }

                RequestResync("event-inbound-queue-full");
                return;
            }

            PMR4ReliableInbound item = new PMR4ReliableInbound();
            item.Kind = PMR4ReliableKind.Events;
            item.Payload = payload;
            _pendingReliable.Add(item);
            _pendingReliableEvents++;
        }

        /// <summary>AP：处理一条重同步快照载荷（由 <c>ClientMovementResyncV1</c> 转发）。只入队。</summary>
        public void OnClientResyncPayload(byte[] payload)
        {
            EnsureThread();
            if (_disposed) { return; }
            ResyncPayloadsReceived++;

            if (_role != PMNetRole.AutonomousProxy || payload == null || payload.Length == 0)
            {
                ResyncInlineRejected++;
                return;
            }

            if (_pendingReliableResyncs >= MaxPendingResyncPayloads)
            {
                ResyncInlineRejected++;
                return;
            }

            PMR4ReliableInbound item = new PMR4ReliableInbound();
            item.Kind = PMR4ReliableKind.Resync;
            item.Payload = payload;
            _pendingReliable.Add(item);
            _pendingReliableResyncs++;
        }

        // ================================================================ 每帧统一入口

        /// <summary>
        /// 消费入站队列并推进断线墙钟（宿主每帧调一次）。
        /// 本类所有"步进/取状态"入口都会先调内部的 ApplyPending，因此宿主即使用别的方式驱动
        /// （例如只调 Pump/Advance/Tick）也不会漏掉入站数据。
        /// </summary>
        public void Update(double wallElapsedMs)
        {
            EnsureThread();
            if (_disposed) { return; }

            ApplyPending();

            if (wallElapsedMs > 0.0 && _timeline != null && _timeline.IsFrozen)
            {
                if (_timeline.NotifyWallClock(wallElapsedMs))
                {
                    FrozenWallClockRequests++;
                    _resyncRequestPending = true;
                }
            }

            FlushPendingResyncRequest();
        }

        private void ApplyPending()
        {
            if (_disposed)
            {
                return;
            }

            ApplyPendingResyncRequests();
            ApplyPendingInputs();
            ApplyPendingSnapshots();
            ApplyPendingReliable();
            FlushPendingResyncRequest();
        }

        private void ApplyPendingInputs()
        {
            if (_role != PMNetRole.Authority)
            {
                _pendingInputs.Clear();
                return;
            }

            if (_pendingInputs.Count == 0)
            {
                return;
            }

            for (int i = 0; i < _pendingInputs.Count; i++)
            {
                byte[] payload = _pendingInputs[i];
                PMR4MovementInputBatch batch;
                string error;
                if (!PMR4MovementCodec.TryDecodeInputs(payload, 0, payload.Length, out batch, out error))
                {
                    InputDecodeFailed++;
                    _player.WarnMovement("[PMR4MovementDriver] 输入载荷解码失败，已丢弃：" + error);
                    continue;
                }

                if (batch.Epoch != _epoch || batch.InstanceId != _instanceId)
                {
                    InputRejectedIdentity++;
                    continue;
                }

                if (batch.StreamVersion != _streamVersion)
                {
                    // 旧流（或伪造的新流）输入一律丢弃 —— 流代次由 DS 递增，客户端不得自升。
                    InputRejectedStream++;
                    continue;
                }

                for (int k = 0; k < batch.Entries.Length; k++)
                {
                    PMR4MovementInputEntry entry = batch.Entries[k];
                    PMAuthorityInput<PMMoverInput> admitted = new PMAuthorityInput<PMMoverInput>();
                    admitted.Epoch = _epoch;
                    admitted.InstanceId = _instanceId;
                    admitted.InputFrame = entry.InputFrame;
                    admitted.Input = _model.CloneInput(entry.Input);
                    admitted.StepMs = entry.StepMs;

                    PMInputAdmissionResult admission = _authority.Admit(admitted);
                    if (admission.Accepted)
                    {
                        InputsAdmitted++;
                    }
                    else
                    {
                        InputsRejectedByBuffer++;
                    }
                }
            }

            _pendingInputs.Clear();
        }

        private void ApplyPendingResyncRequests()
        {
            if (_role != PMNetRole.Authority)
            {
                _pendingResyncRequests.Clear();
                return;
            }

            if (_pendingResyncRequests.Count == 0)
            {
                return;
            }

            for (int i = 0; i < _pendingResyncRequests.Count; i++)
            {
                uint clientStream = _pendingResyncRequests[i];

                if (clientStream == _streamVersion)
                {
                    // 客户端就在当前流上失步：升流 + 下发完整可信快照。
                    BeginServerResync("client-request");
                }
                else
                {
                    // 客户端还在旧流（它错过了我们的升流通知）：只补一次当前流的完整快照，
                    // **不**再升流（否则会形成无界递增）。
                    PublishAuthoritySnapshot(true);
                    ResyncServed++;
                }
            }

            _pendingResyncRequests.Clear();
        }

        private void ApplyPendingSnapshots()
        {
            if (_role == PMNetRole.Authority || _pendingSnapshots.Count == 0)
            {
                _pendingSnapshots.Clear();
                return;
            }

            for (int i = 0; i < _pendingSnapshots.Count; i++)
            {
                byte[] payload = _pendingSnapshots[i];
                PMR4MovementSnapshotBlob blob;
                string error;
                if (!PMR4MovementCodec.TryDecodeSnapshot(payload, 0, payload.Length,
                        PMR4PayloadKind.Snapshot, out blob, out error))
                {
                    SnapshotDecodeFailed++;
                    _player.WarnMovement("[PMR4MovementDriver] 快照载荷解码失败，已丢弃：" + error);
                    continue;
                }

                if (blob.Epoch != _epoch || blob.InstanceId != _instanceId)
                {
                    SnapshotRejectedIdentity++;
                    continue;
                }

                if (blob.StreamVersion != _streamVersion)
                {
                    if (_role == PMNetRole.SimulatedProxy)
                    {
                        // SP 收不到 owner-only 的 ClientMovementResyncV1，因此**较新代次的合法
                        // 复制快照本身就是它唯一的「重绑」来源**（契约主侧复核修订）。四个前置：
                        //   ① 身份（epoch/instance，已在上面校验）与世界版本必须匹配；
                        //   ② 冻结的 SP 保持冻结（断线冻结保持，不借快照解冻/升流）；
                        //   ③ 旧流快照绝不回写（新流已建立）；
                        //   ④ 输出边界与累计仿真时间不得倒退。
                        // 注意：本分支**只**放宽 SP；AP 的「普通快照不得解冻/重建」门槛不变。
                        if (_frozen)
                        {
                            SnapshotRebindRejectedFrozen++;
                            continue;
                        }

                        if (blob.StreamVersion < _streamVersion)
                        {
                            // 旧流延迟到达：新流已经建立，旧流状态不得回写。
                            SnapshotRejectedIdentity++;
                            continue;
                        }

                        if (_interp != null && _interp.HasAuthority
                            && (blob.OutputFrame < _interp.LastOutputFrame.Value
                                || blob.TotalSimTimeMs < _interp.LastTotalSimTimeMs))
                        {
                            SnapshotRebindRejectedRegressing++;
                            continue;
                        }

                        // 绑定到新流：此后旧流载荷一律丢弃（旧流不回写）。
                        _streamVersion = blob.StreamVersion;
                        SnapshotStreamRebinds++;
                    }
                    else if (blob.StreamVersion > _streamVersion)
                    {
                        // AP：DS 已经升流但我们还没拿到重同步载荷：先丢弃，等 Resync 载荷整体重绑。
                        SnapshotRejectedStreamNewer++;
                        continue;
                    }
                    else
                    {
                        // AP 的旧流快照：新流已经建立，旧流状态不得回写。
                        SnapshotRejectedIdentity++;
                        continue;
                    }
                }

                if (blob.Aux.CollisionWorldVersion != _query.WorldVersion)
                {
                    // 碰撞世界不一致 ⇒ 快照不可比（契约「碰撞WorldVersion不符拒绝」）。
                    SnapshotRejectedWorldVersion++;
                    RequestResync("world-version-mismatch");
                    continue;
                }

                if (ApplyPlainSnapshot(blob))
                {
                    SnapshotPayloadsApplied++;
                }
            }

            _pendingSnapshots.Clear();
        }

        private bool ApplyPlainSnapshot(PMR4MovementSnapshotBlob blob)
        {
            PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> snapshot = BuildSnapshot(
                blob.OutputFrame, ToAuthorityFrame(blob.ServerFrame), blob.TotalSimTimeMs, blob.Sync, blob.Aux);

            if (_role == PMNetRole.AutonomousProxy)
            {
                if (_timeline == null)
                {
                    return false;
                }

                // 普通属性更新**从不**携带事件证据 ⇒ 预测时间轴一条事件都不广播，
                // 事件唯一的派发者是 Driver 的独立日志（避免双重派发）。
                snapshot.ConfirmedEvents = null;
                snapshot.ConfirmedEventFrames = null;

                PMAuthorityApplyResult applied = _timeline.ApplyAuthority(snapshot);

                if (applied.Reject == PMAuthorityRejectReason.HistoryMissing || applied.ShouldRequestResync)
                {
                    if (_journal != null)
                    {
                        _journal.Fail("快照缺历史（前边界 " + applied.PreviousConfirmedFrame + "）");
                    }

                    RequestResync("history-missing");
                }

                RetireUnacked();
                DispatchConfirmedEvents();

                if (applied.Applied)
                {
                    _confirmedTotalSimTimeMs = blob.TotalSimTimeMs;
                }

                return applied.Applied;
            }

            if (_role == PMNetRole.SimulatedProxy)
            {
                if (_interp == null)
                {
                    return false;
                }

                PMInterpolationRejectReason reason = _interp.OnAuthority(snapshot);
                return reason == PMInterpolationRejectReason.None;
            }

            return false;
        }

        /// <summary>
        /// AP：按**到达顺序**消费可靠域入站（场景 A：ClientMovementEventsV1；场景 B：ClientMovementResyncV1）。
        ///
        /// 为什么必须同一趟：两者在传输层共享同一个可靠顺序域，DS 侧也是按序发送
        /// （先 flush 事件批，再发升流重同步载荷）。若先跑完所有 Resync 再跑所有 Events，
        /// 「旧流事件 → Resync2 → 流2事件 → Resync3 → 流3事件」会变成
        /// 「先重绑到流3、再喂旧流事件与流2事件」；重绑会把更早代次的事件当成过期流丢弃。
        /// 本方法保证同一批内**不漏不重**。
        /// </summary>
        private void ApplyPendingReliable()
        {
            if (_role != PMNetRole.AutonomousProxy)
            {
                _pendingReliable.Clear();
                _pendingReliableEvents = 0;
                _pendingReliableResyncs = 0;
                return;
            }

            if (_pendingReliable.Count == 0)
            {
                return;
            }

            for (int i = 0; i < _pendingReliable.Count; i++)
            {
                PMR4ReliableInbound item = _pendingReliable[i];
                if (item.Kind == PMR4ReliableKind.Events)
                {
                    ApplyEventPayload(item.Payload);
                }
                else
                {
                    ApplyResyncPayload(item.Payload);
                }
            }

            _pendingReliable.Clear();
            _pendingReliableEvents = 0;
            _pendingReliableResyncs = 0;
            DispatchConfirmedEvents();
        }

        /// <summary>AP：处理**一条**权威事件载荷（解码 → 身份/流校验 → 有界日志接纳）。</summary>
        private void ApplyEventPayload(byte[] payload)
        {
            PMR4MovementEventBatch batch;
            string error;
            if (!PMR4MovementCodec.TryDecodeEvents(payload, 0, payload.Length, out batch, out error))
            {
                EventDecodeFailed++;
                _player.WarnMovement("[PMR4MovementDriver] 事件载荷解码失败，已丢弃：" + error);
                return;
            }

            if (batch.Epoch != _epoch || batch.InstanceId != _instanceId)
            {
                EventBatchesRejected++;
                return;
            }

            if (_journal == null || !_journal.AcceptsStream(batch.StreamVersion))
            {
                // 既不是当前流、也不是重绑后尚未闭合的上一流 ⇒ 真正过期的旧流事件，丢弃。
                EventBatchesRejected++;
                return;
            }

            if (_journal.TryAccept(batch, out error))
            {
                EventBatchesAccepted++;
            }
            else
            {
                EventBatchesRejected++;
                if (_journal.Failed)
                {
                    RequestResync("event-journal-failed");
                }
            }
        }

        /// <summary>AP：处理**一条**重同步完整快照（解码 → 身份/世界版本校验 → 整体重绑）。</summary>
        private void ApplyResyncPayload(byte[] payload)
        {
            PMR4MovementSnapshotBlob blob;
            string error;
            if (!PMR4MovementCodec.TryDecodeSnapshot(payload, 0, payload.Length,
                    PMR4PayloadKind.Resync, out blob, out error))
            {
                SnapshotDecodeFailed++;
                _player.WarnMovement("[PMR4MovementDriver] 重同步载荷解码失败，已丢弃：" + error);
                return;
            }

            if (blob.Epoch != _epoch || blob.InstanceId != _instanceId)
            {
                SnapshotRejectedIdentity++;
                return;
            }

            if (blob.Aux.CollisionWorldVersion != _query.WorldVersion)
            {
                SnapshotRejectedWorldVersion++;
                return;
            }

            if (ApplyResync(blob))
            {
                ResyncApplied++;
            }
        }

        /// <summary>
        /// AP：应用显式可信重同步。
        ///
        ///   · 流代次更旧 ⇒ 丢弃（旧流延迟到达，绝不覆盖新流）；
        ///   · 输出边界或累计仿真时间**倒退** ⇒ 拒绝（契约「新流保持输出边界与累计仿真时间不倒退」）；
        ///   · 流代次相同 ⇒ 时间轴同绑定 Resync（丢未确认输入、不回退确认边界），
        ///     事件日志只清失败标记与越界条目（DS 没有重置序号）；
        ///   · 流代次更新 ⇒ **整体重绑**（契约允许的重建点之一），
        ///     事件日志换绑定但保留 ≤ 重绑边界的未派发事件（否则会漏事件）。
        /// </summary>
        private bool ApplyResync(PMR4MovementSnapshotBlob blob)
        {
            if (_timeline == null)
            {
                return false;
            }

            if (blob.StreamVersion < _streamVersion)
            {
                ResyncRejectedOldStream++;
                return false;
            }

            if (blob.OutputFrame < _timeline.ConfirmedFrame.Value
                || blob.TotalSimTimeMs < _confirmedTotalSimTimeMs)
            {
                ResyncRejectedRegressing++;
                _player.WarnMovement("[PMR4MovementDriver] 重同步会倒退**已确认**边界/权威时间，已拒绝：边界 "
                                     + blob.OutputFrame + " vs 已确认 " + _timeline.ConfirmedFrame.Value
                                     + "，权威时间 " + blob.TotalSimTimeMs + " vs 已确认 " + _confirmedTotalSimTimeMs);
                return false;
            }

            PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> snapshot = BuildSnapshot(
                blob.OutputFrame, ToAuthorityFrame(blob.ServerFrame), blob.TotalSimTimeMs, blob.Sync, blob.Aux);
            snapshot.ConfirmedEvents = null;
            snapshot.ConfirmedEventFrames = null;

            if (blob.StreamVersion != _streamVersion)
            {
                // 整体重绑：新时间轴从可信快照建立，输出边界/累计时间取快照值（已校验不倒退）。
                _streamVersion = blob.StreamVersion;
                _timeline = CreateReboundTimeline(
                    blob.OutputFrame, ToAuthorityFrame(blob.ServerFrame), blob.TotalSimTimeMs,
                    blob.Sync, blob.Aux, _timeline.HistoryCapacity);
                _unacked.Clear();
                _frozen = false;

                if (_journal == null)
                {
                    _journal = new PMR4MovementEventJournal();
                }

                _journal.Rebind(_streamVersion, blob.OutputFrame);
                _confirmedTotalSimTimeMs = blob.TotalSimTimeMs;
                _resyncRequestInFlight = false;
                DispatchConfirmedEvents();
                return true;
            }

            PMResyncResult applied = _timeline.Resync(snapshot);
            if (!applied.Applied)
            {
                ResyncRejectedRegressing++;
                return false;
            }

            if (applied.ResetUnconfirmedInputs > 0)
            {
                _unacked.Clear();
            }

            if (_journal != null)
            {
                _journal.RecoverAfterResync(blob.OutputFrame);
            }

            _frozen = false;
            _confirmedTotalSimTimeMs = blob.TotalSimTimeMs;
            _resyncRequestInFlight = false;
            RetireUnacked();
            DispatchConfirmedEvents();
            return true;
        }

        /// <summary>
        /// 在指定输出边界上新建一个预测时间轴（重同步重绑专用）。
        ///
        /// 历史（供回溯，已不再需要）：R4-A 的**快照构造函数**曾漏写 <c>_boundaryStartFrame</c>，
        /// 当快照的 OutputFrame &gt; 0 时基态边界（0）与读取边界不一致，首次读取即抛「读取边界越界」。
        /// 当时用「占位 epoch 构造 + Resync 显式重绑」绕行（并把每次绕行记入 RebindFallbacks）。
        /// 该核心缺陷已修复（构造时一次写齐 _boundaryStartFrame/_pendingFrame/_confirmedFrame），
        /// 因此这里回到**普通快照构造**：不再伪造绑定、不再依赖 Resync 分支，也不改变任何语义。
        /// </summary>
        private PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState> CreateReboundTimeline(
            long outputFrame, PMFrameId serverFrame, double totalSimTimeMs,
            PMMoverSyncState sync, PMMoverAuxState aux, int historyCapacity)
        {
            PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> snapshot = BuildSnapshot(
                outputFrame, serverFrame, totalSimTimeMs, sync, aux);

            // 普通快照通道**从不**携带事件证据（事件唯一派发者是事件日志），保持一致。
            snapshot.ConfirmedEvents = null;
            snapshot.ConfirmedEventFrames = null;

            return new PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState>(
                _model, snapshot, PMNetRole.AutonomousProxy, historyCapacity);
        }

        private void DispatchConfirmedEvents()
        {
            if (_journal == null || _timeline == null)
            {
                return;
            }

            Action<PMPredictionEvent> sink = EventDispatched;
            long confirmed = _timeline.ConfirmedFrame.Value;

            _journal.DispatchTo(confirmed, delegate(PMPredictionEvent evt)
            {
                if (sink == null)
                {
                    return;
                }

                try
                {
                    sink(evt);
                }
                catch (Exception ex)
                {
                    // 表现回调异常单独记录并继续其它通知：事件已经计入已派发水位，不会被重放。
                    EventDispatchFailed++;
                    EventDispatchLastError = ex.Message;
                }
            });
        }

        private void RequestResync(string reason)
        {
            if (_role != PMNetRole.AutonomousProxy || _player == null)
            {
                return;
            }

            if (_resyncRequestInFlight)
            {
                // 已经有一个请求在飞（可靠通道会送达）：不再每帧重复发，避免把可靠流刷爆。
                return;
            }

            _resyncRequestInFlight = true;
            ResyncRequestsSent++;
            _player.ServerMovementResyncV1(_streamVersion);
            _player.WarnMovement("[PMR4MovementDriver] AP 请求重同步："
                                 + (reason == null ? "(no reason)" : reason));
        }

        private void FlushPendingResyncRequest()
        {
            if (!_resyncRequestPending)
            {
                return;
            }

            _resyncRequestPending = false;

            if (_role == PMNetRole.AutonomousProxy)
            {
                RequestResync("driver-notified");
            }
            else if (_role == PMNetRole.Authority)
            {
                BeginServerResync("ds-buffer");
            }
        }

        private static PMFrameId ToAuthorityFrame(long serverFrame)
        {
            return serverFrame > 0L ? new PMFrameId(PMFrameDomain.AuthorityServer, serverFrame) : PMFrameId.None;
        }

        private PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> BuildSnapshot(
            long outputFrame, PMFrameId serverFrame, double totalSimTimeMs,
            PMMoverSyncState sync, PMMoverAuxState aux)
        {
            PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> snapshot =
                new PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState>();

            // 身份必须与**本对象绑定**一致：预测时间轴 / 插值缓冲靠它拒绝错对象/错会话的快照。
            snapshot.Epoch = _epoch;
            snapshot.InstanceId = _instanceId;
            snapshot.OutputFrame = new PMFrameId(PMFrameDomain.Input, outputFrame);
            snapshot.ServerFrame = serverFrame;
            snapshot.TotalSimTimeMs = totalSimTimeMs;
            snapshot.Sync = sync;
            snapshot.Aux = aux;
            return snapshot;
        }

        // ================================================================ 冻结 / 释放

        /// <summary>
        /// 断线冻结：AP 停止推进并进入"只等显式 Resync"的状态；SP 停在最近权威值。
        /// **不**释放任何东西（释放由宿主在确认退出后调 <see cref="Dispose"/>）。
        /// 入站队列清空；事件日志保留（重同步时按边界保留/丢弃，不会重复广播）。
        /// </summary>
        public void Freeze()
        {
            EnsureThread();
            if (_disposed) { return; }

            _frozen = true;

            if (_timeline != null)
            {
                _timeline.Freeze();
            }

            _pendingSnapshots.Clear();
            _pendingReliable.Clear();
            _pendingReliableEvents = 0;
            _pendingReliableResyncs = 0;
            _pendingInputs.Clear();
            _pendingResyncRequests.Clear();
        }

        /// <summary>幂等释放：摘掉事件订阅、清队列、清 player 上的 Driver 引用。不清服务端对象。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_authority != null)
            {
                try
                {
                    _authority.ResyncRequested -= OnAuthorityBufferResyncRequested;
                }
                catch (Exception)
                {
                }
            }

            _pendingSnapshots.Clear();
            _pendingInputs.Clear();
            _pendingReliable.Clear();
            _pendingReliableEvents = 0;
            _pendingReliableResyncs = 0;
            _pendingResyncRequests.Clear();
            _unacked.Clear();
            _trustedEffects.Clear();
            _trustedLayers.Clear();
            _trustedRemovedLayers.Clear();
            _authorityEventBuffer.Clear();

            if (_player != null && ReferenceEquals(_player.MovementDriver, this))
            {
                _player.MovementDriver = null;
            }

            EventDispatched = null;
        }

        // ================================================================ 诊断

        /// <summary>单行诊断摘要（宿主启动/心跳日志用）。</summary>
        public string Describe()
        {
            string extra;
            if (_role == PMNetRole.Authority)
            {
                extra = " boundary=" + _authorityBoundary
                        + " nextInput=" + (_authority != null ? _authority.NextInputFrame : -1L)
                        + " creditMs=" + (_authority != null ? _authority.CreditMs : 0.0)
                        + " queued=" + (_authority != null ? _authority.QueuedCount : 0)
                        + " totalMs=" + _authorityTotalSimTimeMs
                        + " eventSeq=" + _authorityEventSequence
                        + " trustedQueued=" + (_trustedEffects.Count + _trustedLayers.Count);
            }
            else if (_role == PMNetRole.AutonomousProxy)
            {
                extra = " pending=" + (_timeline != null ? _timeline.PendingFrame.Value : -1L)
                        + " confirmed=" + (_timeline != null ? _timeline.ConfirmedFrame.Value : -1L)
                        + " unacked=" + _unacked.Count
                        + " journalPending=" + (_journal != null ? _journal.PendingEvents : 0)
                        + " journalSeq=" + (_journal != null ? _journal.LastSequence : 0)
                        + " journalFailed=" + (_journal != null && _journal.Failed)
                        + " frozen=" + _frozen;
            }
            else
            {
                extra = " lastOutput=" + (_interp != null ? _interp.LastOutputFrame.Value : -1L)
                        + " simTime=" + (_interp != null ? _interp.LastTotalSimTimeMs : 0.0)
                        + " sampleTimeMs=" + (_interp != null ? _interp.SampleTimeMs : 0.0)
                        + " frozen=" + _frozen;
            }

            return "PMR4MovementDriver role=" + _role
                   + " epoch=" + _epoch
                   + " instance=" + _instanceId
                   + " stream=" + _streamVersion
                   + extra;
        }

        // ================================================================ 前置条件

        private void EnsureThread()
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                ThreadViolations++;
                throw new InvalidOperationException(
                    "[PMR4MovementDriver] 跨线程调用：Driver 只允许在构造它的线程（主线程）上使用"
                    + "（复制应用 / RPC 收发 / 仿真都在主线程，D13）。");
            }
        }

        private void EnsureRole(PMNetRole expected, string what)
        {
            if (_role != expected)
            {
                throw new InvalidOperationException(
                    "[PMR4MovementDriver] " + what + " 只适用于角色 " + expected + "，当前角色是 " + _role + "。");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    "PMR4MovementDriver", "Driver 已释放（角色 " + _role + "，instance " + _instanceId + "）");
            }
        }
    }
}
