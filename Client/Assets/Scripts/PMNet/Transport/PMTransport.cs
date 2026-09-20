using System;
using System.Collections.Generic;

namespace PMNet.Transport
{
    /// <summary>
    /// 一条连接的传输层：把「可靠/不可靠/复制」三个顺序域复用到同一个数据报通道上。
    ///
    /// 与 UE 的对应关系（见 Docs/plans/net-r0-contract.md §4）：
    /// - 数据报 ↔ UE 的 packet（带 PacketId）；
    /// - 一条消息 ↔ UE 的一个 bunch（各自带可靠性）；
    /// - 可靠序号 ↔ `FOutBunch::ChSequence`；
    /// - ack/NAK ↔ `FNetPacketNotify` 的 ack 位图与 NAK；
    /// - 分片 ↔ partial bunch。
    ///
    /// 有意差异（已在契约 §2.3 记录）：
    /// - **不做 per-actor channel**，改成三个固定域 ⇒ 同域内跨对象也保序，代价是队头阻塞；
    /// - **可靠重传不做独立 RTO 定时器**：重传由对端 NAK 与发送端 ack 推断驱动；
    ///   静默期靠**周期性 keepalive**（既有 `ControlPing` 帧）把两端的 ack/NAK 水位推起来，
    ///   而不是新造一套超时重传。见 <see cref="PMTransportConfig.KeepAliveIntervalMs"/>；
    /// - 可靠缓冲溢出 ⇒ 断连（不静默丢弃）。
    ///
    /// 线程约束：<see cref="OnDatagram"/> 可从接收线程调用（只入队），
    /// 其余一切（解析、分发、组包、发包）都在 <see cref="Update"/> 内、即主线程完成。
    /// </summary>
    public sealed class PMTransport
    {
        // ── 线格式常量 ──────────────────────────────────────────────────
        private const byte ProtocolVersion = 1;

        /// <summary>
        /// 单条逻辑消息在**线格式**上可表达的最大字节数。
        ///
        /// 分片头用 `ushort` 写「整条消息总长」（<see cref="PMReliableSendBuffer.Entry.TotalLen"/>），
        /// 因此超过 65535 的载荷**无法原样表达**。历史实现直接 `(ushort)count`，
        /// 超限载荷被**静默截断**成一个错的总长：对端要么永远重组不出来、要么拼出长度对不上的
        /// 脏数据，而发送侧毫无感知（可靠消息也没有重传的机会）。
        /// 现在在 <see cref="Send"/> 入口显式拒绝，绝不截断。
        ///
        /// 注意：这里**没有**把总长字段扩成 `uint` —— 那会改变与既有对端的字节契约
        /// （线格式的一次静默变更比一条被拒绝的消息贵得多）。
        /// </summary>
        public const int MaxTransportableMessageBytes = ushort.MaxValue;

        /// <summary>数据报头固定长度：ver(1)+epoch(4)+pktId(2)+ackId(2)+ackBits(8)+msgCount(1) = 18</summary>
        private const int PacketHeaderBytes = 18;

        /// <summary>ack 位图宽度（与 <c>AckBits</c> 的 ulong 对齐）。</summary>
        private const int AckBitsWidth = 64;

        private const byte FlagReliable = 1 << 0;
        private const byte FlagFragment = 1 << 1;
        private const byte FlagControl = 1 << 2;

        private const byte ControlNak = 0;
        private const byte ControlPing = 1;
        private const byte ControlDisconnect = 2;

        // ── 状态 ────────────────────────────────────────────────────────
        private readonly PMTransportConfig _config;
        private readonly IPMTransportLink _link;
        private readonly IPMTransportSink _sink;
        private readonly uint _sessionEpoch;

        private readonly PMReliableSendBuffer _sendBuffer;
        private readonly PMReliableRecvBuffer _recvBuffer;
        private readonly PMFragmentReassembler _reassembler;

        private ushort _nextPacketId = 1;
        private ushort _recvWindowMax;
        private ulong _recvMask;
        private bool _seenAnyPacket;
        private ushort _highestAckedPacketId;
        private bool _highestAckedValid;

        /// <summary>
        /// 收到的 ack 信息尚未告知对端。
        ///
        /// 为什么需要“纯 ack 包”：ack 是搭车在数据报上的，若本端没有数据要发，
        /// 就永远不会把 ack 送出去——发送端于是无法退休可靠消息、也无法推断丢包，
        /// 最终可靠窗溢出断连。UE 同样靠周期性 keepalive 携带 ack。
        /// </summary>
        private bool _ackDirty;

        private readonly List<ushort> _nakOutbox = new List<ushort>();
        private readonly byte[] _scratch;

        /// <summary>待发消息（不可靠/复制域）。它们不重传，出队即弃。</summary>
        private readonly Queue<PMReliableSendBuffer.Entry> _immediateOutbox =
            new Queue<PMReliableSendBuffer.Entry>();

        private ushort _nextFragId = 1;

        private PMTransportStats _stats;
        private bool _disconnected;
        private PMDisconnectReason _disconnectReason = PMDisconnectReason.None;
        private long _lastRecvMs = -1L;

        /// <summary>
        /// 首次被 <see cref="Update(long, int)"/> 驱动时的时钟（毫秒）。
        ///
        /// 为什么需要它：<see cref="PMTransportConfig.IdleTimeoutMs"/> 原本只在
        /// `_lastRecvMs >= 0`（即至少收到过一个数据报）时才判超时，于是「对端建了链却一个包都不发」
        /// 的连接**永远不会超时**（永生：占着 uid/槽位，对端上行丢失也无从察觉）。
        /// 用一个显式起点把「从未收到过入站流量」也纳入看门狗。
        /// </summary>
        private long _firstUpdateMs = long.MinValue;

        /// <summary>
        /// 最近一次**成功发出**数据报的时刻（毫秒）。
        ///
        /// 同时承担两件事：① keepalive 的到期判据；② 「当帧有业务包就不补多余 ping」。
        /// 首次 Update 时初始化为当时的时钟 ⇒ 不会在第一次 Update 就突然发一个 ping。
        /// </summary>
        private long _lastSendMs = long.MinValue;

        private bool _connectedNotified;
        private PMSendRejectReason _lastSendRejectReason = PMSendRejectReason.None;

        // 入站队列（接收线程 → 主线程）
        private readonly object _inboundLock = new object();
        private readonly Queue<byte[]> _inbound = new Queue<byte[]>();
        private const int InboundQueueLimit = 4096;
        private long _droppedInbound;

        // 交付缓冲（避免每帧分配）
        private readonly List<uint> _deliverSeq = new List<uint>();
        private readonly List<byte[]> _deliverPayload = new List<byte[]>();

        public PMTransport(uint sessionEpoch, PMTransportConfig config,
                           IPMTransportLink link, IPMTransportSink sink)
        {
            if (config == null) { throw new ArgumentNullException("config"); }
            if (link == null) { throw new ArgumentNullException("link"); }
            if (sink == null) { throw new ArgumentNullException("sink"); }

            _sessionEpoch = sessionEpoch;
            _config = config;
            _link = link;
            _sink = sink;
            _sendBuffer = new PMReliableSendBuffer(config);
            _recvBuffer = new PMReliableRecvBuffer(config);
            _reassembler = new PMFragmentReassembler(config);
            _scratch = new byte[config.MaxDatagramBytes];
        }

        public bool IsConnected { get { return !_disconnected; } }
        public PMDisconnectReason DisconnectReason { get { return _disconnectReason; } }
        public uint SessionEpoch { get { return _sessionEpoch; } }
        public PMTransportStats Stats { get { return _stats; } }
        public long DroppedInbound { get { return _droppedInbound; } }
        public int ReliableOutstanding { get { return _sendBuffer.OutstandingCount; } }
        public int ReliableReorderBuffered { get { return _recvBuffer.ReorderCount; } }
        public int ReassemblyPending { get { return _reassembler.PendingCount; } }
        public ushort HighestAckedPacketId { get { return _highestAckedPacketId; } }

        /// <summary>单次 <see cref="Update(long)"/> 最多发出多少数据报（配置值的只读视图）。</summary>
        public int MaxDatagramsPerUpdate { get { return _config.MaxDatagramsPerUpdate; } }

        /// <summary>
        /// 最近一次 <see cref="Send"/> 被拒绝的原因（成功时为 <see cref="PMSendRejectReason.None"/>）。
        ///
        /// 调用方据此区分处置方式：只有 <see cref="PMSendRejectReason.ReliableBufferOverflow"/>
        /// 对应「可靠窗溢出 ⇒ 必须断连」，其余是调用方的错（参数窗口/超上限/分片超限），
        /// 断连理由也应当不同。
        /// </summary>
        public PMSendRejectReason LastSendRejectReason { get { return _lastSendRejectReason; } }

        // ────────────────────────────────────────────────────────────────
        // 发送
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 发送一条逻辑消息。必要时自动分片。
        ///
        /// 可靠时：分配序号、进发送缓冲（由 ack 驱动退休、由 NAK 驱动重传）。
        /// 不可靠/复制域：只入队一次，丢包不补（复制状态由后续更新收敛）。
        /// </summary>
        /// <returns>false 表示发送被拒绝（参数非法/超上限/分片超限/缓冲溢出/已断开）。
        /// 被拒绝的**具体原因**见 <see cref="LastSendRejectReason"/>。</returns>
        public bool Send(PMStream stream, bool reliable, byte[] payload, int offset, int count)
        {
            _lastSendRejectReason = PMSendRejectReason.None;

            if (_disconnected)
            {
                _lastSendRejectReason = PMSendRejectReason.Disconnected;
                return false;
            }

            // 窗口校验必须**溢出安全**：`offset + count > payload.Length` 在 offset/count
            // 都接近 int.MaxValue 时会回绕成负数，从而放过一个越界窗口（随后 BlockCopy 抛）。
            if (payload == null || offset < 0 || count < 0
                || offset > payload.Length || count > payload.Length - offset)
            {
                _stats.SendRejectedWindow++;
                _lastSendRejectReason = PMSendRejectReason.InvalidWindow;
                return false;
            }

            // ① 线格式上限：显式拒绝，**绝不** `(ushort)count` 截断（见常量注释）。
            if (count > MaxTransportableMessageBytes)
            {
                _stats.SendRejectedOversize++;
                _lastSendRejectReason = PMSendRejectReason.MessageTooLarge;
                return false;
            }

            int maxPayload = MaxPayloadPerMessage();
            if (count <= maxPayload)
            {
                return EnqueueOne(stream, reliable, payload, offset, count, 0, 0, 0, 0);
            }

            // 分片：固定片长，接收端可按 fragIndex * 片长 精确还原偏移。
            //
            // ★ 关键：发送端的切片长度必须与接收端的还原公式**完全同源**，
            //   即都用 `FragmentSize(count, fragCount) = CeilDiv(count, fragCount)`。
            //   曾经发送端按 `maxPayload` 切、接收端按 `CeilDiv(totalLen, fragCount)` 拼，
            //   两者在多数规模下并不相等（例：5000/266 → 19 片，而 5000/19 → 264），
            //   结果是“长度对、字节错”的静默损坏。
            int fragCount = (count + maxPayload - 1) / maxPayload;
            if (fragCount > _config.MaxFragmentsPerMessage)
            {
                // ② 分片数上限：显式拒绝，**不无界切分**，也**不**与可靠窗溢出混为一谈
                //    （历史实现让上层把它误标成 ReliableBufferOverflow，真实原因被掩盖）。
                _stats.SendRejectedFragmentLimit++;
                _lastSendRejectReason = PMSendRejectReason.FragmentLimitExceeded;
                return false;
            }

            int sliceSize = PMFragmentReassembler.FragmentSize(count, fragCount);
            if (sliceSize <= 0) { return false; }

            ushort fragId = _nextFragId++;
            if (_nextFragId == 0) { _nextFragId = 1; }

            bool any = false;
            for (int i = 0; i < fragCount; i++)
            {
                int off = i * sliceSize;
                int len = Math.Min(sliceSize, count - off);
                if (len <= 0) { break; }
                if (EnqueueOne(stream, reliable, payload, offset + off, len,
                               fragId, (ushort)count, (ushort)i, (ushort)fragCount))
                {
                    any = true;
                }
            }
            return any;
        }

        private int MaxPayloadPerMessage()
        {
            // 最坏情况的消息头：flags(1)+stream(1)+seq(4)+frag(8)+len(2) = 16
            int avail = _config.MaxDatagramBytes - PacketHeaderBytes - 16;
            return avail > 0 ? avail : 1;
        }

        private bool EnqueueOne(PMStream stream, bool reliable, byte[] payload, int offset, int count,
                                ushort fragId, ushort totalLen, ushort fragIndex, ushort fragCount)
        {
            if (reliable)
            {
                if (_sendBuffer.IsOverflowed)
                {
                    // D-R0-07：可靠缓冲溢出必须断连，不得静默丢弃。
                    _lastSendRejectReason = PMSendRejectReason.ReliableBufferOverflow;
                    Disconnect(PMDisconnectReason.ReliableBufferOverflow);
                    return false;
                }

                _sendBuffer.Enqueue(stream, payload, offset, count, fragId, totalLen, fragIndex, fragCount);
                _stats.ReliableSent++;
                if (fragCount > 1) { _stats.FragmentsSent++; }
                return true;
            }

            PMReliableSendBuffer.Entry e = new PMReliableSendBuffer.Entry();
            e.Seq = 0u;
            e.Data = payload;
            e.Offset = offset;
            e.Length = count;
            e.Stream = stream;
            e.FragId = fragId;
            e.TotalLen = totalLen;
            e.FragIndex = fragIndex;
            e.FragCount = fragCount;

            if (stream == PMStream.Unreliable) { _stats.UnreliableSent++; }
            if (fragCount > 1) { _stats.FragmentsSent++; }

            // 不可靠消息必须立即复制到自有缓冲：调用方的 payload 生命周期不受我们控制。
            _immediateOutbox.Enqueue(e);
            return true;
        }

        // ────────────────────────────────────────────────────────────────
        // 入站（可从接收线程调用）
        // ────────────────────────────────────────────────────────────────

        /// <summary>接收线程调用：只做拷贝+入队，不做任何解析与业务分发。</summary>
        public void OnDatagram(byte[] data, int offset, int count)
        {
            if (_disconnected || data == null || count <= 0) { return; }

            lock (_inboundLock)
            {
                if (_inbound.Count >= InboundQueueLimit)
                {
                    _droppedInbound++;
                    return;
                }

                byte[] copy = new byte[count];
                Buffer.BlockCopy(data, offset, copy, 0, count);
                _inbound.Enqueue(copy);
            }
        }

        // ────────────────────────────────────────────────────────────────
        // 主线程驱动
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 主线程驱动一次：处理入站，并按**传输层自己的**上限
        /// （<see cref="PMTransportConfig.MaxDatagramsPerUpdate"/>）冲刷出站。
        ///
        /// 语义与 R1/T38 时期完全一致（既有调用方与门禁不受影响）。
        /// </summary>
        public void Update(long nowMs)
        {
            Update(nowMs, _config.MaxDatagramsPerUpdate);
        }

        /// <summary>
        /// 带**本次发送预算**的 Update（兼容重载：不改 <see cref="Update(long)"/> 的语义）。
        ///
        /// 为什么需要它：一个协调者可能在一帧内驱动同一条连接两次（帧首 drain、帧尾 flush，
        /// 见 `PMNetSessionBridge.Update`），而 <see cref="PMTransportConfig.MaxDatagramsPerUpdate"/>
        /// 只约束**单次调用**。两次各自按自己的上限发，帧内总发送量就是 2×上限 − 1
        /// （曾经真的发到 63 个数据报）。有了这个重载，协调者把「本帧剩余预算」传进来，
        /// 两次调用**共享**同一个余量，帧内总量才能真正守住配置值。
        ///
        /// 语义：
        ///   - <paramref name="maxDatagramsThisFlush"/> &lt;= 0 ⇒ 只 drain 不发（本次发送预算为 0，
        ///     **keepalive 也不得偻发**）；
        ///   - 传入值会被**夹到** <see cref="PMTransportConfig.MaxDatagramsPerUpdate"/> 以内
        ///     —— 传输层自己的上限不会因为调用方传大数而被突破；
        ///   - 入站解析、重组 TTL、溢出判定、空闲超时等与无参版本完全一致。
        ///
        /// 本轮新增（T38 意图不变，只补活性）：
        ///   - <see cref="PMTransportConfig.KeepAliveIntervalMs"/> 到期时发一个控制 Ping
        ///     （与当轮业务包同包），它携带本端 ack 水位、并在对端制造缺口可见性，
        ///     	因此静默期的首个/末个丢包不需要新的 RTO 就能恢复；
        ///   - 入站活性看门狗覆盖「从未收到过任何数据报」的初始静默期。
        /// </summary>
        public void Update(long nowMs, int maxDatagramsThisFlush)
        {
            if (_disconnected) { return; }

            NormalizeClock(nowMs);

            DrainInbound(nowMs);

            if (_disconnected) { return; }

            if (_reassembler.Expire(nowMs) > 0)
            {
                _stats.ReassemblyDropped++;
            }

            if (_recvBuffer.IsOverflowed)
            {
                Disconnect(PMDisconnectReason.MaxReliableExceeded);
                return;
            }

            if (_sendBuffer.IsOverflowed)
            {
                Disconnect(PMDisconnectReason.ReliableBufferOverflow);
                return;
            }

            if (_config.IdleTimeoutMs > 0L)
            {
                // 基准：收到过包就用最后一个包的到达时刻，否则用本连接第一次被驱动的时刻。
                // 后者是「初始静默期不得永生」的落地点（原先 `_lastRecvMs >= 0` 的前置会让它永不触发）。
                long reference = _lastRecvMs >= 0L ? _lastRecvMs : _firstUpdateMs;
                if (nowMs - reference > _config.IdleTimeoutMs)
                {
                    Disconnect(PMDisconnectReason.Timeout);
                    return;
                }
            }

            int budget = maxDatagramsThisFlush;
            if (budget > _config.MaxDatagramsPerUpdate) { budget = _config.MaxDatagramsPerUpdate; }
            if (budget <= 0) { return; }

            FlushOutbound(budget, nowMs);
        }

        /// <summary>
        /// 时钟归一（首次登记起点 + 墙钟倒退重基准）。
        ///
        /// 为什么必须处理倒退：keepalive 与空闲超时都是 `now - base >= interval` 形式，
        /// 一旦 `now &lt; base`（NTP 回拨、受控测试时钟回退、宿主换时钟源），差值变负 ⇒
        /// 两个看门狗**同时永久失效**（表现为连接永生、ping 不发）。
        /// 这里把基准重设到当前时刻：既不发一个倒退补偿包，也不会让看门狗卡死。
        /// </summary>
        private void NormalizeClock(long nowMs)
        {
            if (_firstUpdateMs == long.MinValue)
            {
                _firstUpdateMs = nowMs;
                _lastSendMs = nowMs;
                return;
            }

            if (nowMs < _firstUpdateMs) { _firstUpdateMs = nowMs; }
            if (_lastSendMs == long.MinValue || nowMs < _lastSendMs) { _lastSendMs = nowMs; }
            if (_lastRecvMs >= 0L && nowMs < _lastRecvMs) { _lastRecvMs = nowMs; }
        }

        private void DrainInbound(long nowMs)
        {
            int budget = _config.MaxDatagramsPerUpdate * 4;
            while (budget-- > 0)
            {
                byte[] pkt;
                lock (_inboundLock)
                {
                    if (_inbound.Count == 0) { break; }
                    pkt = _inbound.Dequeue();
                }

                if (!_disconnected)
                {
                    ProcessDatagram(pkt, nowMs);
                }
            }
        }

        // ────────────────────────────────────────────────────────────────
        // 数据报解析
        // ────────────────────────────────────────────────────────────────

        private void ProcessDatagram(byte[] pkt, long nowMs)
        {
            _lastRecvMs = nowMs;
            _stats.DatagramsReceived++;
            _stats.BytesReceived += pkt.Length;

            if (pkt.Length < PacketHeaderBytes) { _stats.ParseErrors++; return; }

            int p = 0;
            byte version = pkt[p]; p += 1;
            if (version != ProtocolVersion) { _stats.ParseErrors++; return; }

            uint epoch = ReadU32(pkt, ref p);
            if (epoch != _sessionEpoch)
            {
                // 旧连接/旧世代的迟到包：丢弃，不得作用到本会话。
                _stats.DroppedStaleSession++;
                return;
            }

            // 通过版本与 epoch 校验后才认为“连上了”：
            // 否则一个垃圾包也会触发上层 OverConnected。
            if (!_connectedNotified)
            {
                _connectedNotified = true;
                _sink.OnTransportConnected();
            }

            ushort packetId = ReadU16(pkt, ref p);
            ushort ackPacketId = ReadU16(pkt, ref p);
            ulong ackBits = ReadU64(pkt, ref p);
            byte messageCount = pkt[p]; p += 1;

            // 先处理对端对我们发包的确认（推进可靠发送缓冲）
            ProcessIncomingAcks(ackPacketId, ackBits);

            // 再看这个包本身是否是新包（重复包不重复交付）。
            //
            // ★ 是否「需要再回一个 ack」由本数据报**是否携带消息**决定：
            //   纯 ack 数据报（18 字节 header-only，messageCount == 0）不回 ack。
            //   见 <see cref="TrackIncomingPacketId"/> 的注释（本轮修的“纯 ack 互相追赶”）。
            bool isNew = TrackIncomingPacketId(packetId, messageCount > 0);
            if (!isNew)
            {
                // 重复数据报：仍可携带新的 ack 信息（上面已处理），但消息不重复交付。
                return;
            }

            for (int i = 0; i < messageCount; i++)
            {
                if (p >= pkt.Length) { _stats.ParseErrors++; break; }

                byte flags = pkt[p]; p += 1;

                // ★ 最小长度守卫（T38 健壮性，本轮补核心侧）：读完 flags 后必须还有 stream 字节。
                //   旧实现直接 `pkt[p]` 读 streamRaw：一条 19 字节、messageCount=1 的数据报
                //   （PacketHeaderBytes=18 + flags=1）会在 `pkt[19]` 抛 IndexOutOfRangeException，
                //   而 DrainInbound 不捕异常 ⇒ 沿 Transport.Update 一路冒泡，中断宿主同一帧里
                //   所有端点的收包/握手/flush 与复制 Tick。会话层已有纵深兜底（把异常收敛成本连接
                //   协议错误），但核心自己也不应该抛。
                if (p >= pkt.Length) { _stats.ParseErrors++; break; }

                byte streamRaw = pkt[p]; p += 1;

                bool reliable = (flags & FlagReliable) != 0;
                bool fragment = (flags & FlagFragment) != 0;
                bool control = (flags & FlagControl) != 0;

                if (control)
                {
                    if (!ReadControl(pkt, ref p)) { _stats.ParseErrors++; break; }
                    continue;
                }

                // 下面每一段定宽读取都有同样的越界风险（截断消息不得抛），统一补守卫。
                uint seq = 0u;
                if (reliable)
                {
                    if (p + 4 > pkt.Length) { _stats.ParseErrors++; break; }
                    seq = ReadU32(pkt, ref p);
                }

                ushort fragId = 0, totalLen = 0, fragIndex = 0, fragCount = 0;
                if (fragment)
                {
                    if (p + 8 > pkt.Length) { _stats.ParseErrors++; break; }
                    fragId = ReadU16(pkt, ref p);
                    totalLen = ReadU16(pkt, ref p);
                    fragIndex = ReadU16(pkt, ref p);
                    fragCount = ReadU16(pkt, ref p);
                }

                if (p + 2 > pkt.Length) { _stats.ParseErrors++; break; }
                ushort payloadLen = ReadU16(pkt, ref p);
                if (p + payloadLen > pkt.Length) { _stats.ParseErrors++; break; }

                if (streamRaw >= (byte)PMStream.Count)
                {
                    _stats.DroppedUnknownStream++;
                    p += payloadLen;
                    continue;
                }

                PMStream stream = (PMStream)streamRaw;

                if (reliable)
                {
                    byte[] payload = new byte[payloadLen];
                    Buffer.BlockCopy(pkt, p, payload, 0, payloadLen);
                    p += payloadLen;

                    bool accepted = _recvBuffer.Buffer(seq, payload);
                    if (!accepted)
                    {
                        _stats.ReliableDuplicates++;
                        continue;
                    }

                    _recvBuffer.DrainContiguous(_deliverSeq, _deliverPayload);
                    for (int k = 0; k < _deliverPayload.Count; k++)
                    {
                        DeliverMessage(stream, fragment, fragId, totalLen, fragIndex, fragCount,
                                       _deliverPayload[k], 0, _deliverPayload[k].Length, nowMs);
                        _stats.ReliableDelivered++;
                    }
                }
                else
                {
                    if (stream == PMStream.Unreliable) { _stats.UnreliableReceived++; }
                    DeliverMessage(stream, fragment, fragId, totalLen, fragIndex, fragCount,
                                   pkt, p, payloadLen, nowMs);
                    p += payloadLen;
                }
            }
        }

        private void DeliverMessage(PMStream stream, bool fragment, ushort fragId, ushort totalLen,
                                    ushort fragIndex, ushort fragCount,
                                    byte[] buf, int offset, int length, long nowMs)
        {
            if (!fragment)
            {
                _sink.OnTransportMessage(stream, buf, offset, length);
                return;
            }

            _stats.FragmentsReceived++;
            byte[] whole = _reassembler.Add(stream, fragId, totalLen, fragIndex, fragCount,
                                            buf, offset, length, nowMs);
            if (whole == null) { return; }

            _stats.Reassembled++;
            _sink.OnTransportMessage(stream, whole, 0, whole.Length);
        }

        private bool ReadControl(byte[] pkt, ref int p)
        {
            if (p >= pkt.Length) { return false; }

            byte type = pkt[p]; p += 1;
            if (type == ControlNak)
            {
                if (p + 2 > pkt.Length) { return false; }
                ushort n = ReadU16(pkt, ref p);
                for (int i = 0; i < n; i++)
                {
                    if (p + 2 > pkt.Length) { return false; }
                    ushort missing = ReadU16(pkt, ref p);
                    int resent = _sendBuffer.OnNak(missing);
                    _stats.NaksReceived++;
                    _stats.ReliableResent += resent;
                }
                return true;
            }

            if (type == ControlPing)
            {
                // keepalive：只记账。**故意不回 ping**（回 ping 会让两个端点互相触发永动风暴）；
                // ack 反馈走既有的 `_ackDirty`（ProcessDatagram 已因这个新包置位），
                // 因此对端最多收到一个纯 ack 数据报，数量由它的 Update 节拍约束。
                _stats.KeepAlivesReceived++;
                return true;
            }

            if (type == ControlDisconnect)
            {
                byte reason = (p < pkt.Length) ? pkt[p] : (byte)PMDisconnectReason.PeerClosed;
                p += 1;
                Disconnect((PMDisconnectReason)reason);
                return true;
            }

            return false;
        }

        // ────────────────────────────────────────────────────────────────
        // ack / NAK 记账
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 记录收到的数据报编号，返回它是否为"新"包。
        /// 同时检测缺口并发起 NAK（这是唯一的重传触发源）。
        ///
        /// <paramref name="carriesMessages"/> 只控制一件事：本次入站是否要**再回一个 ack**
        /// （即是否置 <see cref="_ackDirty"/>）。它与「是否消费本次入站信息」完全无关：
        ///   - 对端 ack 位图的消费在 <see cref="ProcessIncomingAcks"/> 里，**无条件**执行；
        ///   - 本方法的包号跟踪（`_recvWindowMax` / `_recvMask` / 缺口 NAK）**无条件**执行。
        /// 被抑制的只有「本端再产生一个纯 ack 数据报」这个动作。
        ///
        /// 为什么必须区分（本轮修的真实缺陷："纯 ack 互相追赶"）：
        /// ack 是搭车在数据报上的，两端都被驱动时任意一次入站都会置 `_ackDirty`，
        /// 于是每端**每帧**产生一个 18 字节纯 ack 数据报；这个纯 ack 又在对端置位，往复不停。
        /// 实测（修复前）：静默 12s 探针每端 `tx≈1100`（≈每帧 1 包），活性完全由 ack 流承担，
        /// `KeepAlivesSent` 只剩 1。抑制「ack 一个只含 ack 的数据报」后，一个 ping 至多换来
        /// 一个 ack，链路重新收敛（详见 `Docs/plans/_r3b_transport_liveness.md`）。
        ///
        /// 为什么不能改成「纯 ack 数据报干脆不跟踪包号」：那样对端 ack 水位不再前进，
        /// 发送端的 <see cref="PMReliableSendBuffer.RetransmitOlderThan"/> 推断会失效 ——
        /// 静默期首个可靠数据报的丢失就再也补不回来（J2b/J3/M3 正是钉住这一点的用例）。
        /// </summary>
        private bool TrackIncomingPacketId(ushort packetId, bool carriesMessages)
        {
            // 只有携带消息（业务消息 / 控制 Ping / NAK）的数据报才需要本端回 ack。
            bool wantAck = carriesMessages;

            if (!_seenAnyPacket)
            {
                _seenAnyPacket = true;
                _recvWindowMax = packetId;
                _recvMask = 1UL;
                if (wantAck) { _ackDirty = true; }
                return true;
            }

            if (packetId == _recvWindowMax)
            {
                bool wasNew = (_recvMask & 1UL) == 0UL;
                _recvMask |= 1UL;
                if (wasNew && wantAck) { _ackDirty = true; }
                return wasNew;
            }

            int diff = unchecked((short)(packetId - _recvWindowMax));
            if (diff > 0)
            {
                // 更靠后的包：移位并把中间的空位当作缺口 → NAK
                if (diff >= AckBitsWidth)
                {
                    _recvMask = 1UL;
                }
                else
                {
                    _recvMask = (_recvMask << diff) | 1UL;

                    // 新移入的位 i∈[1,diff] 若为 0，说明 (packetId - i) 没收到。
                    // 只对窗口内（< 64）的缺口发 NAK，避免风暴且保持有界。
                    for (int i = 1; i <= diff; i++)
                    {
                        if (((_recvMask >> i) & 1UL) == 0UL)
                        {
                            _nakOutbox.Add(unchecked((ushort)(packetId - i)));
                        }
                    }
                }

                _recvWindowMax = packetId;
                if (wantAck) { _ackDirty = true; }
                return true;
            }

            // 更早的包：落在窗口内则补位（重复交付由可靠层去重；不可靠域按语义允许重复）
            int back = -diff;
            if (back < AckBitsWidth)
            {
                ulong bit = 1UL << back;
                bool wasNew = (_recvMask & bit) == 0UL;
                _recvMask |= bit;
                if (wasNew && wantAck) { _ackDirty = true; }
                return wasNew;
            }

            // 太旧：视为重复
            return false;
        }

        private void ProcessIncomingAcks(ushort ackPacketId, ulong ackBits)
        {
            if (ackPacketId == 0 && ackBits == 0UL) { return; }

            int removed = _sendBuffer.OnAck(ackPacketId);

            // ★ 位图语义：ackBits 的 bit i 表示包 `ackPacketId - 1 - i` 已收到。
            //   （发送侧写的是 `_recvMask >> 1`，而 mask 的 bit0 是 ackPacketId 自身。）
            //
            //   这里曾经写成 `ackPacketId - i` 并跳过 bit0，整体偏一位 ——
            //   后果极危险：对端报「包 1 收到」时，本端会把**包 2** 上的消息退休，
            //   即静默丢掉一条从未送达的可靠消息。任何位图编解码都必须成对核对。
            for (int i = 0; i < AckBitsWidth; i++)
            {
                if ((ackBits & (1UL << i)) != 0UL)
                {
                    removed += _sendBuffer.OnAck(unchecked((ushort)(ackPacketId - 1 - i)));
                }
            }

            if (removed > 0)
            {
                _stats.ReliableDelivered += removed;
            }

            if (!_highestAckedValid || unchecked((short)(ackPacketId - _highestAckedPacketId)) > 0)
            {
                _highestAckedPacketId = ackPacketId;
                _highestAckedValid = true;

                // ★ 发送端从 ack 推断丢包：
                //   对端已经确认到 ackPacketId，而某条消息只被放进过更早的数据报、
                //   至今仍未退休 ⇒ 那些数据报丢了 ⇒ 重传。
                //
                //   为什么必须有这条：接收端的缺口检测（NAK）看不到“第一个包就丢”的情况
                //   —— 它把收到的第一个包当基线，之前的丢失无从得知。
                //   只有发送端掌握完整的发送历史，才能补上这一段。
                int inferred = _sendBuffer.RetransmitOlderThan(_highestAckedPacketId);
                _stats.ReliableResent += inferred;

                _sink.OnTransportPacketsAcked(ackPacketId, ackPacketId);
            }
        }

        // ────────────────────────────────────────────────────────────────
        // 组包与发送
        // ────────────────────────────────────────────────────────────────

        private void FlushOutbound(int maxDatagrams, long nowMs)
        {
            int sent = 0;
            while (sent < maxDatagrams)
            {
                // keepalive 只在**本轮第一个数据报**上施加：
                //   - 若本轮还有业务待发，ping 会被并进同一个数据报（messageCount 里多一条控制帧），
                //     不多占一个数据报；
                //   - 只要本轮真的发出过任何包，后面的循环就不会再补 ping
                //     （`_lastSendMs` 已被刷新，`IsKeepAliveDue` 为假）。
                bool keepAlive = sent == 0 && IsKeepAliveDue(nowMs);

                bool keepAliveWritten;
                int len = BuildDatagram(keepAlive, out keepAliveWritten);
                if (len <= 0) { break; }

                if (!_link.Send(_scratch, 0, len))
                {
                    // 发送失败不抛出、不作废连接（P3'-1 已确立：发送异常曾静默废掉整局）。
                    break;
                }

                _stats.DatagramsSent++;
                _stats.BytesSent += len;
                if (keepAliveWritten) { _stats.KeepAlivesSent++; }

                // 任何一次成功发送（业务或 keepalive）都推迟下一个 keepalive：
                // 这就是「与当帧业务包共存、不额外产生多余 ping」的落地点。
                _lastSendMs = nowMs;
                sent++;

                if (_disconnected) { break; }
            }
        }

        /// <summary>keepalive 是否到期。间隔 ≤ 0 表示禁用；未驱动过则不判到期。</summary>
        private bool IsKeepAliveDue(long nowMs)
        {
            if (_config.KeepAliveIntervalMs <= 0L) { return false; }
            if (_lastSendMs == long.MinValue) { return false; }
            return nowMs - _lastSendMs >= _config.KeepAliveIntervalMs;
        }

        /// <summary>
        /// 组装一个数据报。返回长度；0 表示没有待发消息。
        ///
        /// <paramref name="keepAlive"/> 为真时先塞一条控制 Ping（与业务包同包，不额外占包）。
        /// </summary>
        private int BuildDatagram(bool keepAlive, out bool keepAliveWritten)
        {
            keepAliveWritten = false;

            // 先看有没有可发的消息（可靠待发 / 不可靠队列 / NAK 控制帧）
            bool hasImmediate = _immediateOutbox.Count > 0;
            bool hasNak = _nakOutbox.Count > 0;

            int p = PacketHeaderBytes;

            // NAK 控制帧优先（它是重传的触发路径，不该被数据饿死）
            bool nakWritten = false;
            if (hasNak)
            {
                int maxNak = (_config.MaxDatagramBytes - p - 4) / 2;
                if (maxNak > 0)
                {
                    int n = Math.Min(maxNak, _nakOutbox.Count);
                    _scratch[p++] = FlagControl;    // ★ 必须是 Control 标志，否则解析端会当成普通消息
                    _scratch[p++] = 0;              // stream 占位（控制帧不属于任何域）
                    _scratch[p++] = ControlNak;
                    WriteU16(_scratch, ref p, (ushort)n);
                    for (int i = 0; i < n; i++)
                    {
                        WriteU16(_scratch, ref p, _nakOutbox[i]);
                    }
                    _nakOutbox.RemoveRange(0, n);
                    _stats.NaksSent += n;
                    nakWritten = true;
                }
            }

            // keepalive 控制帧：与 NAK/业务包同一个数据报（messageCount 各自计数）。
            // 线格式未变：flags=Control、stream 占位、type=ControlPing，与既有解析分支同源。
            if (keepAlive && p + 3 <= _config.MaxDatagramBytes)
            {
                _scratch[p++] = FlagControl;
                _scratch[p++] = 0;
                _scratch[p++] = ControlPing;
                keepAliveWritten = true;
            }

            int messageCount = 0;
            if (nakWritten) { messageCount++; }
            if (keepAliveWritten) { messageCount++; }

            // 可靠待发（首次 + 重传），一条一条塞到装不下为止
            while (true)
            {
                PMReliableSendBuffer.Entry e = _sendBuffer.DequeuePending();
                if (e == null) { break; }

                int need = 1 + 1 + (e.IsFragment ? 8 : 0) + 4 + 2 + e.Length;
                if (p + need > _config.MaxDatagramBytes)
                {
                    // 装不下：放回队首，下一轮先发它（不得丢弃——这是未发送过的可靠消息）。
                    _sendBuffer.PushFront(e);
                    break;
                }

                _scratch[p++] = (byte)(FlagReliable | (e.IsFragment ? FlagFragment : 0));
                _scratch[p++] = (byte)e.Stream;
                WriteU32(_scratch, ref p, e.Seq);
                if (e.IsFragment)
                {
                    WriteU16(_scratch, ref p, e.FragId);
                    WriteU16(_scratch, ref p, e.TotalLen);
                    WriteU16(_scratch, ref p, e.FragIndex);
                    WriteU16(_scratch, ref p, e.FragCount);
                }
                WriteU16(_scratch, ref p, (ushort)e.Length);
                Buffer.BlockCopy(e.Data, e.Offset, _scratch, p, e.Length);
                p += e.Length;

                messageCount++;

                // 记录它进了哪个包（用"将要发的包号"）
                _pendingSentEntries.Add(e);
            }

            // 不可靠 / 复制域消息
            while (_immediateOutbox.Count > 0)
            {
                PMReliableSendBuffer.Entry e = _immediateOutbox.Peek();
                int need = 1 + 1 + (e.IsFragment ? 8 : 0) + 2 + e.Length;
                if (p + need > _config.MaxDatagramBytes) { break; }

                _immediateOutbox.Dequeue();
                _scratch[p++] = (byte)(e.IsFragment ? FlagFragment : 0);
                _scratch[p++] = (byte)e.Stream;
                if (e.IsFragment)
                {
                    WriteU16(_scratch, ref p, e.FragId);
                    WriteU16(_scratch, ref p, e.TotalLen);
                    WriteU16(_scratch, ref p, e.FragIndex);
                    WriteU16(_scratch, ref p, e.FragCount);
                }
                WriteU16(_scratch, ref p, (ushort)e.Length);
                Buffer.BlockCopy(e.Data, e.Offset, _scratch, p, e.Length);
                p += e.Length;
                messageCount++;
            }

            if (messageCount == 0 && !_ackDirty)
            {
                // 既没有要发的消息，也没有新的 ack 信息 —— 真的没东西要发。
                return 0;
            }

            ushort packetId = _nextPacketId++;
            if (_nextPacketId == 0) { _nextPacketId = 1; }

            // 回填头
            int h = 0;
            _scratch[h++] = ProtocolVersion;
            WriteU32(_scratch, ref h, _sessionEpoch);
            WriteU16(_scratch, ref h, packetId);
            WriteU16(_scratch, ref h, _seenAnyPacket ? _recvWindowMax : (ushort)0);
            WriteU64(_scratch, ref h, _seenAnyPacket ? (_recvMask >> 1) : 0UL);
            _scratch[h] = (byte)messageCount;

            // 登记本轮装入的可靠消息 → 它们属于这个 packetId
            for (int i = 0; i < _pendingSentEntries.Count; i++)
            {
                _sendBuffer.MarkSent(_pendingSentEntries[i], packetId);
            }
            _pendingSentEntries.Clear();

            _ackDirty = false;
            return p;
        }

        private readonly List<PMReliableSendBuffer.Entry> _pendingSentEntries =
            new List<PMReliableSendBuffer.Entry>();

        // ────────────────────────────────────────────────────────────────
        // 断开
        // ────────────────────────────────────────────────────────────────

        /// <summary>主动断开：尽力通知对端一次，然后本地清场（幂等）。</summary>
        public void Disconnect(PMDisconnectReason reason)
        {
            if (_disconnected) { return; }
            _disconnected = true;
            _disconnectReason = reason;

            if (reason == PMDisconnectReason.LocalClosed || reason == PMDisconnectReason.PeerClosed)
            {
                TrySendDisconnect(reason);
            }

            _sendBuffer.Clear();
            _recvBuffer.Clear();
            _immediateOutbox.Clear();
            _nakOutbox.Clear();
            _pendingSentEntries.Clear();

            lock (_inboundLock) { _inbound.Clear(); }

            _sink.OnTransportDisconnected(reason);
        }

        private void TrySendDisconnect(PMDisconnectReason reason)
        {
            int p = 0;
            _scratch[p++] = ProtocolVersion;
            WriteU32(_scratch, ref p, _sessionEpoch);
            WriteU16(_scratch, ref p, _nextPacketId++);
            WriteU16(_scratch, ref p, 0);
            WriteU64(_scratch, ref p, 0UL);
            _scratch[p++] = 1;
            _scratch[p++] = FlagControl;   // 控制帧标志
            _scratch[p++] = 0;             // stream 占位
            _scratch[p++] = ControlDisconnect;
            _scratch[p++] = (byte)reason;
            _link.Send(_scratch, 0, p);
        }

        // ────────────────────────────────────────────────────────────────
        // 固定宽度小端编解码（帧头用固定宽度，不用 protobuf varint）
        // ────────────────────────────────────────────────────────────────

        private static void WriteU16(byte[] b, ref int p, ushort v)
        {
            b[p] = (byte)(v & 0xFF); b[p + 1] = (byte)((v >> 8) & 0xFF); p += 2;
        }

        private static void WriteU32(byte[] b, ref int p, uint v)
        {
            b[p] = (byte)(v & 0xFF);
            b[p + 1] = (byte)((v >> 8) & 0xFF);
            b[p + 2] = (byte)((v >> 16) & 0xFF);
            b[p + 3] = (byte)((v >> 24) & 0xFF);
            p += 4;
        }

        private static void WriteU64(byte[] b, ref int p, ulong v)
        {
            for (int i = 0; i < 8; i++)
            {
                b[p + i] = (byte)((v >> (8 * i)) & 0xFF);
            }
            p += 8;
        }

        private static ushort ReadU16(byte[] b, ref int p)
        {
            ushort v = (ushort)(b[p] | (b[p + 1] << 8)); p += 2; return v;
        }

        private static uint ReadU32(byte[] b, ref int p)
        {
            uint v = (uint)(b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24));
            p += 4;
            return v;
        }

        private static ulong ReadU64(byte[] b, ref int p)
        {
            ulong v = 0UL;
            for (int i = 0; i < 8; i++)
            {
                v |= ((ulong)b[p + i]) << (8 * i);
            }
            p += 8;
            return v;
        }
    }
}
