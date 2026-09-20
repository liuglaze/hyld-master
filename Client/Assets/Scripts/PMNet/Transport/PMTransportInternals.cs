using System;
using System.Collections.Generic;

namespace PMNet.Transport
{
    /// <summary>
    /// 可靠**发送**侧缓冲。
    ///
    /// 语义（对应 UE 的 `FOutBunch` + `OutRec` + NAK 重传，见 net-r0-contract §4 / D-R0-06）：
    /// - 每条待发消息分配一个单调递增的序号；
    /// - 记录"这条消息被放进过哪些数据报"；
    /// - 收到某数据报的 ack ⇒ 该数据报携带的消息已送达，从缓冲移除；
    /// - 收到某数据报的 NAK ⇒ 该数据报携带的、**尚未送达**的消息重新入队发送。
    ///
    /// **不做定时重传**：重传完全由对端 NAK 驱动。
    /// </summary>
    internal sealed class PMReliableSendBuffer
    {
        internal sealed class Entry
        {
            public uint Seq;
            public byte[] Data;
            public int Offset;
            public int Length;
            public PMStream Stream;
            public ushort FragId;
            public ushort TotalLen;
            public ushort FragIndex;
            public ushort FragCount;
            public readonly List<ushort> SentInPackets = new List<ushort>(2);

            /// <summary>
            /// 是否已在待发队列中。
            ///
            /// 两条丢包恢复路径（接收端 NAK 与发送端 ack 推断）可能同时决定重传同一条消息，
            /// 没有这个标记就会把同一条消息**两份塞进同一个数据报**。
            /// 正确性不受影响（接收端会去重），但白占带宽。
            /// </summary>
            public bool Queued;

            /// <summary>组包时的可复算字段：是否为分片、以及各头字段是否有效。</summary>
            public bool IsFragment { get { return FragCount > 1; } }
        }

        private readonly PMTransportConfig _config;
        private uint _nextSeq = 1u;
        private readonly List<Entry> _entries = new List<Entry>();

        /// <summary>
        /// 待发队列。用 LinkedList 而不是 Queue：组包时若一条消息装不下当前数据报，
        /// 需要把它**放回队首**而不是丢弃——丢弃就是静默丢可靠消息。
        /// </summary>
        private readonly LinkedList<Entry> _pending = new LinkedList<Entry>();

        public PMReliableSendBuffer(PMTransportConfig config)
        {
            _config = config;
        }

        public int OutstandingCount { get { return _entries.Count; } }

        /// <summary>发送窗口是否已满。满了必须由调用方断连（D-R0-07），不得静默丢弃。</summary>
        public bool IsOverflowed { get { return _entries.Count > _config.ReliableSendWindow; } }

        public Entry Enqueue(PMStream stream, byte[] data, int offset, int length,
                             ushort fragId, ushort totalLen, ushort fragIndex, ushort fragCount)
        {
            Entry e = new Entry();
            e.Seq = _nextSeq++;
            e.Data = data;
            e.Offset = offset;
            e.Length = length;
            e.Stream = stream;
            e.FragId = fragId;
            e.TotalLen = totalLen;
            e.FragIndex = fragIndex;
            e.FragCount = fragCount;
            _entries.Add(e);
            e.Queued = true;
            _pending.AddLast(e);
            return e;
        }

        public Entry DequeuePending()
        {
            if (_pending.Count == 0) { return null; }
            LinkedListNode<Entry> node = _pending.First;
            _pending.RemoveFirst();
            node.Value.Queued = false;
            return node.Value;
        }

        /// <summary>把装不下当前数据报的消息放回队首（保持发送顺序单调）。</summary>
        public void PushFront(Entry e)
        {
            e.Queued = true;
            _pending.AddFirst(e);
        }

        public void MarkSent(Entry e, ushort packetId)
        {
            if (!e.SentInPackets.Contains(packetId))
            {
                e.SentInPackets.Add(packetId);
            }
        }

        /// <summary>处理 ack：该数据报携带的、仍在缓冲中的消息视为已送达并移除。返回条数。</summary>
        public int OnAck(ushort packetId)
        {
            int removed = 0;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].SentInPackets.Contains(packetId))
                {
                    _entries.RemoveAt(i);
                    removed++;
                }
            }
            return removed;
        }

        /// <summary>处理 NAK：该数据报携带的、仍在缓冲中的消息重新入队（重传）。返回条数。</summary>
        public int OnNak(ushort packetId)
        {
            int resent = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                Entry e = _entries[i];
                if (e.SentInPackets.Contains(packetId) && !e.Queued)
                {
                    e.Queued = true;
                    _pending.AddLast(e);
                    resent++;
                }
            }
            return resent;
        }

        /// <summary>
        /// 发送端丢包推断：对端已确认到 <paramref name="ackedUpTo"/>，
        /// 若某条消息最近一次发送仍在该水位之前、且至今未退休，则那次发送已丢 → 重传。
        ///
        /// 为什么需要它：接收端的缺口检测（NAK）把收到的第一个包当基线，
        /// **看不到“第一个包就丢”**的情况；只有发送端掌握完整发送历史才能补上这一段。
        /// </summary>
        public int RetransmitOlderThan(ushort ackedUpTo)
        {
            int n = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                Entry e = _entries[i];
                ushort maxSent = 0;
                for (int k = 0; k < e.SentInPackets.Count; k++)
                {
                    ushort pid = e.SentInPackets[k];
                    if (unchecked((short)(pid - maxSent)) > 0) { maxSent = pid; }
                }

                if (unchecked((short)(ackedUpTo - maxSent)) > 0 && !e.Queued)
                {
                    e.Queued = true;
                    _pending.AddLast(e);
                    n++;
                }
            }
            return n;
        }

        public void Clear()
        {
            _entries.Clear();
            _pending.Clear();
        }
    }

    /// <summary>
    /// 可靠**接收**侧缓冲：去重 + 保序 + 乱序等待。
    ///
    /// 语义（D-R0-06）：
    /// - 序号小于期望值 ⇒ 重复包，丢弃（去重；重传依赖它工作）；
    /// - 等于期望值 ⇒ 交付，并连续交付后续已到达的；
    /// - 大于期望值 ⇒ 进乱序缓冲等待；缓冲超过上限 ⇒ 必须断连（不得无限增长）。
    /// </summary>
    internal sealed class PMReliableRecvBuffer
    {
        private readonly PMTransportConfig _config;
        private uint _expectedSeq = 1u;
        private readonly Dictionary<uint, byte[]> _reorder = new Dictionary<uint, byte[]>();
        private bool _lastWasDuplicate;

        public PMReliableRecvBuffer(PMTransportConfig config)
        {
            _config = config;
        }

        public int ReorderCount { get { return _reorder.Count; } }
        public bool IsOverflowed { get { return _reorder.Count > _config.ReliableRecvWindow; } }
        public bool LastWasDuplicate { get { return _lastWasDuplicate; } }

        /// <summary>暂存一个收到的可靠载荷。返回 false 表示重复（已被丢弃）。</summary>
        public bool Buffer(uint seq, byte[] payload)
        {
            // 带符号差值比较，天然处理序号回绕（窗口远小于 2^31）。
            int diff = unchecked((int)(seq - _expectedSeq));
            if (diff < 0)
            {
                _lastWasDuplicate = true;
                return false;
            }

            _lastWasDuplicate = false;
            _reorder[seq] = payload;
            return true;
        }

        /// <summary>按序交付所有连续可交付的消息。</summary>
        public void DrainContiguous(List<uint> outSeq, List<byte[]> outPayload)
        {
            outSeq.Clear();
            outPayload.Clear();

            byte[] next;
            while (_reorder.TryGetValue(_expectedSeq, out next))
            {
                _reorder.Remove(_expectedSeq);
                outSeq.Add(_expectedSeq);
                outPayload.Add(next);
                _expectedSeq++;
            }
        }

        public void Clear()
        {
            _reorder.Clear();
        }
    }

    /// <summary>
    /// 分片重组（R0 契约 §4 / D-R0-10）。
    ///
    /// 偏移口径：发送端按**固定片长**切片，`片长 = CeilDiv(totalLen, fragCount)`，
    /// 因此接收端可用 `fragIndex * 片长` 精确还原偏移，**不需要**在头里额外携带偏移，
    /// 也不依赖"用本片长度反推"（末片更短时会算错）。
    ///
    /// 有界性是这个类的主要职责：条目数、总字节、存活时间都必须有上限，
    /// 否则对端只要发一个"永远不发剩余片"的包就能让内存无界增长。
    /// 片数上限 64 与这里的 `ulong` 位图正好对齐（见 <see cref="PMTransportConfig.MaxFragmentsPerMessage"/>）。
    /// </summary>
    internal sealed class PMFragmentReassembler
    {
        private sealed class Pending
        {
            public ushort TotalLen;
            public ushort FragCount;
            public ulong ReceivedMask;
            public int ReceivedCount;
            public byte[] Buffer;
            public long LastTouchMs;
        }

        private readonly PMTransportConfig _config;
        private readonly Dictionary<int, Pending> _pending = new Dictionary<int, Pending>();

        public PMFragmentReassembler(PMTransportConfig config)
        {
            _config = config;
        }

        public int PendingCount { get { return _pending.Count; } }

        /// <summary>片长：与发送端同一口径。</summary>
        public static int FragmentSize(int totalLen, int fragCount)
        {
            if (fragCount <= 1) { return totalLen; }
            return (totalLen + fragCount - 1) / fragCount;
        }

        /// <summary>丢弃超过 TTL 的条目，返回丢弃条数。</summary>
        public int Expire(long nowMs)
        {
            if (_config.ReassemblyTtlMs <= 0L) { return 0; }

            List<int> dead = null;
            foreach (KeyValuePair<int, Pending> kv in _pending)
            {
                if (nowMs - kv.Value.LastTouchMs > _config.ReassemblyTtlMs)
                {
                    if (dead == null) { dead = new List<int>(); }
                    dead.Add(kv.Key);
                }
            }

            if (dead == null) { return 0; }
            for (int i = 0; i < dead.Count; i++) { _pending.Remove(dead[i]); }
            return dead.Count;
        }

        /// <summary>喂入一个分片；重组完成时返回完整载荷，否则返回 null。</summary>
        public byte[] Add(PMStream stream, ushort fragId, ushort totalLen, ushort fragIndex, ushort fragCount,
                          byte[] payload, int payloadOffset, int payloadLength, long nowMs)
        {
            if (fragCount == 0 || fragIndex >= fragCount || totalLen == 0) { return null; }
            if (fragCount > _config.MaxFragmentsPerMessage) { return null; }

            int key = ((int)stream * 65536) + fragId;
            Pending p;
            if (!_pending.TryGetValue(key, out p))
            {
                if (_pending.Count >= _config.ReassemblyCapacity)
                {
                    DropOldest();
                }

                p = new Pending();
                p.TotalLen = totalLen;
                p.FragCount = fragCount;
                p.Buffer = new byte[totalLen];
                p.ReceivedCount = 0;
                p.ReceivedMask = 0UL;
                _pending[key] = p;
            }

            p.LastTouchMs = nowMs;

            int fragSize = FragmentSize(totalLen, fragCount);
            int offset = fragIndex * fragSize;
            if (offset < 0 || offset + payloadLength > p.Buffer.Length) { return null; }

            ulong bit = 1UL << fragIndex;
            if ((p.ReceivedMask & bit) == 0UL)
            {
                Buffer.BlockCopy(payload, payloadOffset, p.Buffer, offset, payloadLength);
                p.ReceivedMask |= bit;
                p.ReceivedCount++;
            }

            if (p.ReceivedCount >= p.FragCount)
            {
                _pending.Remove(key);
                return p.Buffer;
            }

            return null;
        }

        private void DropOldest()
        {
            int oldestKey = 0;
            long oldest = long.MaxValue;
            bool found = false;
            foreach (KeyValuePair<int, Pending> kv in _pending)
            {
                if (kv.Value.LastTouchMs < oldest)
                {
                    oldest = kv.Value.LastTouchMs;
                    oldestKey = kv.Key;
                    found = true;
                }
            }
            if (found) { _pending.Remove(oldestKey); }
        }
    }
}
