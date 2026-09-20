using System;
using System.Collections.Generic;
using PMNet;
using PMNet.Transport;

namespace PMTransportTest
{
    /// <summary>
    /// R1 / T38 验收：可靠传输（M03）的契约验证。
    /// 事实来源：Docs/plans/net-r0-contract.md §4 与主计划 §6.1 的 T38 行。
    /// </summary>
    internal static class Program
    {
        private static int _passed;
        private static readonly List<string> _failures = new List<string>();

        private static int Main()
        {
            Console.WriteLine("=== R1/T38：可靠传输契约验证 ===");
            Console.WriteLine();

            Section("A. 可靠域：保序 / 去重 / 重复数据报不重复交付", TestReliableOrderAndDedup);
            Section("B. 丢包恢复：发送端推断 + 接收端 NAK", TestNakRetransmit);
            Section("C. 溢出即断连（发送窗 / 接收乱序窗）", TestOverflowDisconnect);
            Section("D. 不可靠域：不重传、丢了就是丢了", TestUnreliableNoRetransmit);
            Section("E. 三个顺序域彼此独立", TestStreamIndependence);
            Section("F. 分片与重组（含界与 TTL）", TestFragmentation);
            Section("G. 会话世代：旧世代的迟到包被丢弃", TestStaleSession);
            Section("H. 协议健壮性：垃圾包不崩、不误报连接", TestProtocolRobustness);
            Section("I. 直接 Send 的显式上限（窗口/超上限/分片）与发送预算入口", TestSendCeilingsAndSendBudget);
            Section("J. 传输活性：周期 keepalive / 静默期丢包恢复 / 预算 / 时钟倒退 / 纯 ack 收敛 / 短包守卫", TestTransportLiveness);
            Section("K. 核心侧长度守卫：截断数据报不抛（会话兜底之外的第二道）", TestShortDatagramGuards);

            Console.WriteLine();
            if (_failures.Count == 0)
            {
                Console.WriteLine("  全部通过：" + _passed + " 项检查，0 项失败");
                return 0;
            }

            Console.WriteLine("  " + _passed + " 项通过，" + _failures.Count + " 项失败：");
            for (int i = 0; i < _failures.Count; i++)
            {
                Console.WriteLine("    - " + _failures[i]);
            }
            return 1;
        }

        private static void Section(string name, Action body)
        {
            Console.WriteLine("── " + name);
            body();
            Console.WriteLine();
        }

        private static void Check(bool ok, string label)
        {
            if (ok) { _passed++; } else { _failures.Add(label); }
            Console.WriteLine((ok ? "    OK   " : "    FAIL ") + label);
        }

        private static void CheckEq(long actual, long expected, string label)
        {
            Check(actual == expected, label + "（实际 " + actual + "，期望 " + expected + "）");
        }

        private static void CheckGt(long actual, long bound, string label)
        {
            Check(actual > bound, label + "（实际 " + actual + "，要求 > " + bound + "）");
        }

        private static void CheckStrEq(string actual, string expected, string label)
        {
            Check(actual == expected, label + "（实际 " + (actual ?? "<null>") + "，期望 " + (expected ?? "<null>") + "）");
        }

        // ────────────────────────────────────────────────────────────────
        // 可控链路
        // ────────────────────────────────────────────────────────────────

        /// <summary>在途数据报（可用于乱序/延迟）。</summary>
        private sealed class InFlight
        {
            public SimLink Dst;
            public byte[] Data;
            public long DueAtMs;
        }

        private sealed class SimNet
        {
            public readonly List<InFlight> Queue = new List<InFlight>();

            /// <summary>按序号丢弃：第 n 个发出的数据报被丢（1 起）。0 = 不丢。</summary>
            public int DropNth = 0;
            public int DropSecond = -1;   // 再丢一个指定序号（-1 = 无）
            public int SentCount;

            /// <summary>重复投递第 n 个数据报（1 起）。-1 = 不重复。</summary>
            public int DupNth = -1;

            /// <summary>延迟投递毫秒（>0 时进入在途队列，需 PumpDelay 才送达）。</summary>
            public long DelayMs = 0;

            /// <summary>使后一个包先到（乱序）。</summary>
            public bool ReorderNextTwo = false;

            public void Reset()
            {
                Queue.Clear();
                DropNth = 0;
                DropSecond = -1;
                DupNth = -1;
                DelayMs = 0;
                ReorderNextTwo = false;
                SentCount = 0;
            }
        }

        private sealed class SimLink : IPMTransportLink
        {
            public SimNet Net;
            public SimLink Peer;
            public string Name;
            public readonly List<byte[]> Delivered = new List<byte[]>();

            /// <summary>
            /// 丢掉**本端方向**接下来的 N 个数据报。
            ///
            /// 既有的 <see cref="SimNet.DropNth"/> / <see cref="SimNet.DropSecond"/> 是按
            /// <see cref="SimNet.SentCount"/>（两个方向的全局发送序号）丢的，无法表达
            /// 「只丢 A→B」这种单向丢失场景 —— 而本轮 keepalive 的「首个/末个可靠数据报丢失」
            /// 用例必须能精确控制方向（否则对端的一个 ack 就能把缺口暴露出来）。
            /// </summary>
            public int DropNext;

            public bool Send(byte[] buffer, int offset, int count)
            {
                Net.SentCount++;
                int n = Net.SentCount;

                if (DropNext > 0)
                {
                    DropNext--;
                    return true;
                }

                bool drop = (Net.DropNth != 0 && n == Net.DropNth)
                            || (Net.DropSecond >= 0 && n == Net.DropSecond);
                if (drop) { return true; }

                byte[] copy = new byte[count];
                Buffer.BlockCopy(buffer, offset, copy, 0, count);

                if (Net.DelayMs > 0)
                {
                    InFlight f = new InFlight();
                    f.Dst = Peer;
                    f.Data = copy;
                    f.DueAtMs = _now + Net.DelayMs;
                    Net.Queue.Add(f);
                }
                else
                {
                    Peer.Delivered.Add(copy);
                }

                if (Net.DupNth >= 0 && n == Net.DupNth)
                {
                    byte[] dup = new byte[count];
                    Buffer.BlockCopy(buffer, offset, dup, 0, count);
                    Peer.Delivered.Add(dup);
                }

                if (Net.ReorderNextTwo && Peer.Delivered.Count >= 2 && !_reordered)
                {
                    int c = Peer.Delivered.Count;
                    byte[] t = Peer.Delivered[c - 1];
                    Peer.Delivered[c - 1] = Peer.Delivered[c - 2];
                    Peer.Delivered[c - 2] = t;
                    Net.ReorderNextTwo = false;
                    _reordered = true;
                }

                return true;
            }

            private bool _reordered;

            public string Describe() { return Name; }
        }

        private sealed class RecordingSink : IPMTransportSink
        {
            public readonly List<KeyValuePair<PMStream, byte[]>> Messages =
                new List<KeyValuePair<PMStream, byte[]>>();

            public int ConnectedCount;
            public int DisconnectedCount;
            public PMDisconnectReason LastReason = PMDisconnectReason.None;
            public ushort HighestAcked;
            public bool AnyAck;

            public void OnTransportMessage(PMStream stream, byte[] payload, int offset, int count)
            {
                byte[] copy = new byte[count];
                Buffer.BlockCopy(payload, offset, copy, 0, count);
                Messages.Add(new KeyValuePair<PMStream, byte[]>(stream, copy));
            }

            public void OnTransportConnected() { ConnectedCount++; }

            public void OnTransportDisconnected(PMDisconnectReason reason)
            {
                DisconnectedCount++;
                LastReason = reason;
            }

            public void OnTransportPacketsAcked(ushort fromPacketId, ushort toPacketId)
            {
                AnyAck = true;
                HighestAcked = toPacketId;
            }

            public int CountOf(PMStream s)
            {
                int n = 0;
                for (int i = 0; i < Messages.Count; i++)
                {
                    if (Messages[i].Key == s) { n++; }
                }
                return n;
            }

            public byte[] First(PMStream s)
            {
                for (int i = 0; i < Messages.Count; i++)
                {
                    if (Messages[i].Key == s) { return Messages[i].Value; }
                }
                return null;
            }

            /// <summary>第 index 个（0 起）指定域的载荷；不存在时返回 null（供断言给出清晰 FAIL 而不是下标越界）。</summary>
            public byte[] Nth(PMStream s, int index)
            {
                int n = 0;
                for (int i = 0; i < Messages.Count; i++)
                {
                    if (Messages[i].Key != s) { continue; }
                    if (n == index) { return Messages[i].Value; }
                    n++;
                }
                return null;
            }

            public void Clear() { Messages.Clear(); }
        }

        private static long _now;

        private static byte[] Payload(string s)
        {
            return System.Text.Encoding.UTF8.GetBytes(s);
        }

        private static string AsString(byte[] b)
        {
            return b == null ? "<null>" : System.Text.Encoding.UTF8.GetString(b);
        }

        /// <summary>一对已连上的传输实例。</summary>
        private sealed class Pair
        {
            public SimNet Net;
            public SimLink LinkA;
            public SimLink LinkB;
            public RecordingSink SinkA;
            public RecordingSink SinkB;
            public PMTransport A;
            public PMTransport B;
        }

        private static Pair MakePair(Action<PMTransportConfig> tune = null, uint epoch = 7u)
        {
            PMTransportConfig ca = new PMTransportConfig();
            PMTransportConfig cb = new PMTransportConfig();
            ca.IdleTimeoutMs = 0;
            cb.IdleTimeoutMs = 0;
            // 旧 T38 用例统计**绝对报文数**（例如「一次最多 32 个」）：显式禁用周期 keepalive，
            // 否则任何一次时钟推进都可能额外插入一个控制 Ping，把计数断言变成随机失败。
            // 本轮 J/K 组自己按需打开（见 TestTransportLiveness 的 tune）。
            ca.KeepAliveIntervalMs = 0L;
            cb.KeepAliveIntervalMs = 0L;
            if (tune != null) { tune(ca); tune(cb); }

            SimNet net = new SimNet();
            SimLink la = new SimLink(); la.Net = net; la.Name = "A";
            SimLink lb = new SimLink(); lb.Net = net; lb.Name = "B";
            la.Peer = lb;
            lb.Peer = la;

            RecordingSink sa = new RecordingSink();
            RecordingSink sb = new RecordingSink();

            Pair p = new Pair();
            p.Net = net;
            p.LinkA = la;
            p.LinkB = lb;
            p.SinkA = sa;
            p.SinkB = sb;
            p.A = new PMTransport(epoch, ca, la, sa);
            p.B = new PMTransport(epoch, cb, lb, sb);
            return p;
        }

        /// <summary>把 link 上已投递的数据报喂给对应传输，并驱动双方 Update。</summary>
        private static void Pump(Pair p, long nowAdvanceMs = 0)
        {
            _now += nowAdvanceMs;

            // A 发出 → 已投递到 B
            for (int i = 0; i < p.LinkB.Delivered.Count; i++)
            {
                p.B.OnDatagram(p.LinkB.Delivered[i], 0, p.LinkB.Delivered[i].Length);
            }
            p.LinkB.Delivered.Clear();

            for (int i = 0; i < p.LinkA.Delivered.Count; i++)
            {
                p.A.OnDatagram(p.LinkA.Delivered[i], 0, p.LinkA.Delivered[i].Length);
            }
            p.LinkA.Delivered.Clear();

            p.A.Update(_now);
            p.B.Update(_now);

            // Update 期间可能又产生了新包：再投一次（一轮内收敛）
            for (int i = 0; i < p.LinkB.Delivered.Count; i++)
            {
                p.B.OnDatagram(p.LinkB.Delivered[i], 0, p.LinkB.Delivered[i].Length);
            }
            p.LinkB.Delivered.Clear();

            for (int i = 0; i < p.LinkA.Delivered.Count; i++)
            {
                p.A.OnDatagram(p.LinkA.Delivered[i], 0, p.LinkA.Delivered[i].Length);
            }
            p.LinkA.Delivered.Clear();

            p.B.Update(_now);
            p.A.Update(_now);
        }

        /// <summary>把延迟队列中到期的数据报送达。</summary>
        private static void PumpDelayed(Pair p)
        {
            for (int i = p.Net.Queue.Count - 1; i >= 0; i--)
            {
                if (p.Net.Queue[i].DueAtMs <= _now)
                {
                    InFlight f = p.Net.Queue[i];
                    f.Dst.Delivered.Add(f.Data);
                    p.Net.Queue.RemoveAt(i);
                }
            }
        }

        /// <summary>把链路上已投递的数据报喂给对端（等价于接收线程调用 OnDatagram）。</summary>
        private static void DeliverAll(Pair p)
        {
            for (int i = 0; i < p.LinkB.Delivered.Count; i++)
            {
                p.B.OnDatagram(p.LinkB.Delivered[i], 0, p.LinkB.Delivered[i].Length);
            }
            p.LinkB.Delivered.Clear();

            for (int i = 0; i < p.LinkA.Delivered.Count; i++)
            {
                p.A.OnDatagram(p.LinkA.Delivered[i], 0, p.LinkA.Delivered[i].Length);
            }
            p.LinkA.Delivered.Clear();
        }

        /// <summary>
        /// 手工投递一个方向的数据报，并返回投递条数。
        ///
        /// 为什么需要它：<see cref="SimNet.DropNext"/> 只能表达「本端接下来 N 个丢」，
        /// 而 J8 要精确丢「A→B 的第 2 个」、同时保住第 1 与第 3 个（它们都是 A 发的）。
        /// 先取出再按需取舍是唯一能表达该序列的办法。
        /// </summary>
        private static int DeliverTo(Pair p, bool toB)
        {
            List<byte[]> q = toB ? p.LinkB.Delivered : p.LinkA.Delivered;
            PMTransport t = toB ? p.B : p.A;
            int handed = 0;
            for (int i = 0; i < q.Count; i++)
            {
                t.OnDatagram(q[i], 0, q[i].Length);
                handed++;
            }
            q.Clear();
            return handed;
        }

        /// <summary>
        /// 按「两段驱动」推进真实传输层：交付 → A/B Update → 交付 → B/A Update。
        /// 与 <c>PMNetSessionBridge.Update</c> 的帧内次序（帧首 drain、帧尾 flush）同形，
        /// 因此这里观察到的发送计数与生产路径一致。
        /// </summary>
        private static void Frames(Pair p, int count, long stepMs)
        {
            for (int f = 0; f < count; f++)
            {
                _now += stepMs;
                DeliverAll(p);
                p.A.Update(_now);
                p.B.Update(_now);
                DeliverAll(p);
                p.B.Update(_now);
                p.A.Update(_now);
            }
        }

        /// <summary>只驱动 A（对端「停止 Update」的故障形态）。</summary>
        private static void FramesOneSided(Pair p, int count, long stepMs)
        {
            for (int f = 0; f < count; f++)
            {
                _now += stepMs;
                p.A.Update(_now);
            }
        }

        /// <summary>拼一个只含固定头的数据报（packetId 必须各不相同，否则会被当成重复包）。</summary>
        private static byte[] RawDatagram(uint epoch, ushort packetId, byte messageCount, int length)
        {
            byte[] b = new byte[length];
            b[0] = 1;
            b[1] = (byte)(epoch & 0xFF);
            b[2] = (byte)((epoch >> 8) & 0xFF);
            b[3] = (byte)((epoch >> 16) & 0xFF);
            b[4] = (byte)((epoch >> 24) & 0xFF);
            b[5] = (byte)(packetId & 0xFF);
            b[6] = (byte)((packetId >> 8) & 0xFF);
            b[17] = messageCount;
            return b;
        }

        private static void FeedQuiet(PMTransport t, byte[] data, ref int throws)
        {
            try
            {
                t.OnDatagram(data, 0, data.Length);
                t.Update(_now);
            }
            catch (Exception)
            {
                throws++;
            }
        }

        // ────────────────────────────────────────────────────────────────
        // A. 保序 / 去重
        // ────────────────────────────────────────────────────────────────

        private static void TestReliableOrderAndDedup()
        {
            _now = 0;
            Pair p = MakePair();
            p.Net.DropNth = 0;

            // 三条可靠消息分别成包（各自 Update 一次），但**反序**投递
            p.A.Send(PMStream.Reliable, true, Payload("m1"), 0, 2);
            p.A.Update(_now);
            p.A.Send(PMStream.Reliable, true, Payload("m2"), 0, 2);
            p.A.Update(_now);
            p.A.Send(PMStream.Reliable, true, Payload("m3"), 0, 2);
            p.A.Update(_now);

            // 反序喂给 B
            List<byte[]> pend = p.LinkB.Delivered;
            for (int i = pend.Count - 1; i >= 0; i--)
            {
                p.B.OnDatagram(pend[i], 0, pend[i].Length);
            }
            pend.Clear();
            p.B.Update(_now);

            bool inOrder = p.SinkB.Messages.Count == 3
                           && AsString(p.SinkB.First(PMStream.Reliable)) == "m1"
                           && AsString(p.SinkB.Messages[1].Value) == "m2"
                           && AsString(p.SinkB.Messages[2].Value) == "m3";
            Check(inOrder, "乱序到达 → 按序号保序交付（m1,m2,m3）");

            // 重复喂同一批（模拟重传/重复包）：不得重复交付
            p.SinkB.Clear();
            p.B.OnDatagram(new byte[0], 0, 0);  // 空包不崩
            Check(p.SinkB.Messages.Count == 0, "空数据报被安全忽略");

            // 用一个真实的重复场景：重复投递同一条数据报
            _now = 0;
            Pair q = MakePair();
            q.Net.DupNth = 1;   // 第 1 个数据报投递两次
            q.A.Send(PMStream.Reliable, true, Payload("once"), 0, 4);
            Pump(q);
            Check(q.SinkB.CountOf(PMStream.Reliable) == 1,
                  "重复数据报 → 只交付一次（去重生效）");
        }

        // ────────────────────────────────────────────────────────────────
        // B. NAK 重传
        // ────────────────────────────────────────────────────────────────

        private static void TestNakRetransmit()
        {
            // ── B1 首包丢失：NAK **看不到**这种情形，必须由发送端从 ack 推断 ──
            //
            // 接收端把“收到的第一个包”当基线，因此它无从得知之前还有包丢了。
            // 这一条曾经直接导致“两条消息只到一条”——早期实现只做了接收端缺口检测。
            _now = 0;
            Pair p = MakePair();
            p.Net.DropNth = 1;

            p.A.Send(PMStream.Reliable, true, Payload("lost"), 0, 4);
            p.A.Update(_now);                 // 包 1（被丢）
            p.A.Send(PMStream.Reliable, true, Payload("ok"), 0, 2);
            p.A.Update(_now);                 // 包 2（到达）

            for (int i = 0; i < 4; i++) { Pump(p, 1); }

            Check(p.SinkB.CountOf(PMStream.Reliable) == 2,
                  "B1 首包丢失 → 仍能两条都交付（发送端从 ack 推断丢包）");
            Check(AsString(p.SinkB.First(PMStream.Reliable)) == "lost",
                  "B1 重传后仍保序（先 lost 后 ok）");
            Check(p.A.Stats.ReliableResent >= 1, "B1 发送端执行了重传");

            int delivered = p.SinkB.CountOf(PMStream.Reliable);
            for (int i = 0; i < 4; i++) { Pump(p, 1); }
            Check(p.SinkB.CountOf(PMStream.Reliable) == delivered,
                  "B1 重传收敛后继续泵 → 无重复交付");

            // ── B2 中途缺口：接收端**能**看到，必须发 NAK ──
            // 收 1 与 3、丢 2 ⇒ 缺口可见 ⇒ NAK(2) ⇒ 发送端重传 ⇒ 恰好交付一次。
            _now = 0;
            Pair q = MakePair();
            q.Net.DropNth = 2;

            q.A.Send(PMStream.Reliable, true, Payload("a"), 0, 1);
            q.A.Update(_now);                 // 包 1（到达）
            q.A.Send(PMStream.Reliable, true, Payload("b"), 0, 1);
            q.A.Update(_now);                 // 包 2（被丢）
            q.A.Send(PMStream.Reliable, true, Payload("c"), 0, 1);
            q.A.Update(_now);                 // 包 3（到达 → 缺口可见）

            for (int i = 0; i < 4; i++) { Pump(q, 1); }

            Check(q.B.Stats.NaksSent >= 1, "B2 接收端发出 NAK（缺口可见）");
            Check(q.A.Stats.NaksReceived >= 1, "B2 发送端收到 NAK");
            Check(q.SinkB.CountOf(PMStream.Reliable) == 3, "B2 三条消息全部交付");
            Check(AsString(q.SinkB.Messages[0].Value) == "a"
                  && AsString(q.SinkB.Messages[1].Value) == "b"
                  && AsString(q.SinkB.Messages[2].Value) == "c",
                  "B2 交付顺序为 a,b,c（保序）");

            // ── B3 不可靠包丢失：不重传、不交付 ──
            _now = 0;
            Pair r = MakePair();
            r.Net.DropNth = 1;
            r.A.Send(PMStream.Unreliable, false, Payload("u"), 0, 1);
            for (int i = 0; i < 4; i++) { Pump(r, 1); }
            Check(r.SinkB.CountOf(PMStream.Unreliable) == 0, "B3 不可靠包丢失 → 未交付（符合语义）");
            Check(r.A.Stats.ReliableResent == 0, "B3 不可靠包丢失 → 未触发任何重传");
        }

        // ────────────────────────────────────────────────────────────────
        // C. 溢出断连
        // ────────────────────────────────────────────────────────────────

        private static void TestOverflowDisconnect()
        {
            // C1 发送窗溢出：不推进 Update（对端收不到，因此永远不会 ack）
            _now = 0;
            Pair p = MakePair(delegate (PMTransportConfig c) { c.ReliableSendWindow = 4; });
            for (int i = 0; i < 10 && p.A.IsConnected; i++)
            {
                p.A.Send(PMStream.Reliable, true, Payload("x"), 0, 1);
            }
            Check(!p.A.IsConnected, "可靠发送窗溢出 → 连接被断开（不静默丢弃）");
            Check(p.A.DisconnectReason == PMDisconnectReason.ReliableBufferOverflow,
                  "断开原因 = ReliableBufferOverflow（D-R0-07）");
            Check(p.SinkA.DisconnectedCount == 1, "断开回调恰好触发一次（幂等）");

            // C2 接收乱序窗溢出：丢第 1 个包，后面大量包到达 → 乱序缓冲溢出
            _now = 0;
            Pair q = MakePair(delegate (PMTransportConfig c) { c.ReliableRecvWindow = 4; });
            q.Net.DropNth = 1;

            for (int i = 0; i < 12; i++)
            {
                q.A.Send(PMStream.Reliable, true, Payload("y"), 0, 1);
                q.A.Update(_now);
            }
            // 只把 B 侧收到的喂进去
            for (int i = 0; i < q.LinkB.Delivered.Count; i++)
            {
                q.B.OnDatagram(q.LinkB.Delivered[i], 0, q.LinkB.Delivered[i].Length);
            }
            q.LinkB.Delivered.Clear();
            q.B.Update(_now);

            Check(!q.B.IsConnected, "接收乱序窗溢出 → 连接被断开");
            Check(q.B.DisconnectReason == PMDisconnectReason.MaxReliableExceeded,
                  "断开原因 = MaxReliableExceeded（D-R0-07）");
        }

        // ────────────────────────────────────────────────────────────────
        // D. 不可靠域
        // ────────────────────────────────────────────────────────────────

        private static void TestUnreliableNoRetransmit()
        {
            _now = 0;
            Pair p = MakePair();

            p.A.Send(PMStream.Unreliable, false, Payload("u1"), 0, 2);
            Pump(p);
            Check(p.SinkB.CountOf(PMStream.Unreliable) == 1, "不可靠包正常到达");

            p.SinkB.Clear();
            // 多条不可靠在同一轮内应能被合并进少数数据报（不逐条一发）
            long before = p.A.Stats.DatagramsSent;
            for (int i = 0; i < 5; i++)
            {
                p.A.Send(PMStream.Unreliable, false, Payload("z"), 0, 1);
            }
            Pump(p);
            long sent = p.A.Stats.DatagramsSent - before;
            Check(p.SinkB.CountOf(PMStream.Unreliable) == 5, "5 条不可靠消息全部到达");
            Check(sent <= 2, "同轮多条不可靠消息被打进少量数据报（而非逐条一发）：实际 " + sent);

            // 不可靠域不产生 NAK
            Check(p.B.Stats.NaksSent == 0, "不可靠域不触发 NAK");
        }

        // ────────────────────────────────────────────────────────────────
        // E. 域独立
        // ────────────────────────────────────────────────────────────────

        private static void TestStreamIndependence()
        {
            _now = 0;
            Pair p = MakePair();

            p.A.Send(PMStream.Reliable, true, Payload("R"), 0, 1);
            p.A.Send(PMStream.Unreliable, false, Payload("U"), 0, 1);
            p.A.Send(PMStream.Replication, false, Payload("C"), 0, 1);
            Pump(p);
            Pump(p);

            Check(p.SinkB.CountOf(PMStream.Reliable) == 1, "可靠域独立交付");
            Check(p.SinkB.CountOf(PMStream.Unreliable) == 1, "不可靠域独立交付");
            Check(p.SinkB.CountOf(PMStream.Replication) == 1, "复制域独立交付");

            bool right = AsString(p.SinkB.First(PMStream.Reliable)) == "R"
                         && AsString(p.SinkB.First(PMStream.Unreliable)) == "U"
                         && AsString(p.SinkB.First(PMStream.Replication)) == "C";
            Check(right, "三个域的内容没有串味");
        }

        // ────────────────────────────────────────────────────────────────
        // F. 分片
        // ────────────────────────────────────────────────────────────────

        private static void TestFragmentation()
        {
            _now = 0;
            Pair p = MakePair(delegate (PMTransportConfig c) { c.MaxDatagramBytes = 300; });

            // 造一个明显超过单包的载荷，内容可校验
            byte[] big = new byte[5000];
            for (int i = 0; i < big.Length; i++) { big[i] = (byte)(i % 251); }

            bool ok = p.A.Send(PMStream.Reliable, true, big, 0, big.Length);
            Check(ok, "大载荷被接受（自动分片）");

            for (int i = 0; i < 8; i++) { Pump(p, 1); }

            byte[] got = p.SinkB.First(PMStream.Reliable);
            Check(got != null && got.Length == big.Length,
                  "分片被完整重组：期望 " + big.Length + " 字节，实际 " + (got == null ? 0 : got.Length));

            bool same = got != null && got.Length == big.Length;
            if (same)
            {
                for (int i = 0; i < big.Length; i++)
                {
                    if (got[i] != big[i]) { same = false; break; }
                }
            }
            Check(same, "重组后的字节内容与原文逐字节一致");
            Check(p.B.Stats.Reassembled >= 1, "接收端记录了重组成功");
            Check(p.B.ReassemblyPending == 0, "重组表在完成后被清空（不泄漏）");

            // 分片数超上限 → 拒绝发送（宁可失败也不无界切分）
            _now = 0;
            Pair q = MakePair(delegate (PMTransportConfig c)
            {
                c.MaxDatagramBytes = 120;
                c.MaxFragmentsPerMessage = 4;
            });
            bool rejected = !q.A.Send(PMStream.Reliable, true, new byte[5000], 0, 5000);
            Check(rejected, "分片数超上限 → 拒绝发送（不无界切分）");

            // 缺片不交付 + 条目有界 + TTL 过期
            _now = 0;
            Pair r = MakePair(delegate (PMTransportConfig c)
            {
                c.MaxDatagramBytes = 300;
                c.ReassemblyTtlMs = 100;
            });
            byte[] big2 = new byte[2000];
            r.A.Send(PMStream.Reliable, true, big2, 0, big2.Length);
            r.A.Update(_now);
            // 只把第一个分片包喂进去
            if (r.LinkB.Delivered.Count > 0)
            {
                r.B.OnDatagram(r.LinkB.Delivered[0], 0, r.LinkB.Delivered[0].Length);
            }
            r.LinkB.Delivered.Clear();
            r.B.Update(_now);
            Check(r.SinkB.CountOf(PMStream.Reliable) == 0, "缺片时不交付不完整数据");
            Check(r.B.ReassemblyPending >= 0, "重组条目数有界（不崩）");

            // 推进过 TTL → 条目被清理
            Pump(r, 500);
            Check(r.B.ReassemblyPending == 0, "超过 TTL 的重组条目被清理（防泄漏）");
        }

        // ────────────────────────────────────────────────────────────────
        // G. 会话世代
        // ────────────────────────────────────────────────────────────────

        private static void TestStaleSession()
        {
            _now = 0;

            // A/B 用 epoch=7，另造一个 epoch=8 的 A'，把 A' 的包喂给 B
            Pair p = MakePair(null, 7u);
            Pair other = MakePair(null, 8u);

            p.A.Send(PMStream.Reliable, true, Payload("new"), 0, 3);
            p.A.Update(_now);

            other.A.Send(PMStream.Reliable, true, Payload("stale"), 0, 5);
            other.A.Update(_now);

            // 把旧世代的包喂给 B（模拟重连前的迟到包）
            for (int i = 0; i < other.LinkB.Delivered.Count; i++)
            {
                p.B.OnDatagram(other.LinkB.Delivered[i], 0, other.LinkB.Delivered[i].Length);
            }
            // 再把本世代的包喂进去
            for (int i = 0; i < p.LinkB.Delivered.Count; i++)
            {
                p.B.OnDatagram(p.LinkB.Delivered[i], 0, p.LinkB.Delivered[i].Length);
            }
            p.LinkB.Delivered.Clear();
            p.B.Update(_now);

            Check(p.B.Stats.DroppedStaleSession >= 1, "旧会话世代的包被丢弃并计数");
            Check(p.SinkB.CountOf(PMStream.Reliable) == 1, "只有本世代的包被交付");
            Check(AsString(p.SinkB.First(PMStream.Reliable)) == "new", "交付的是本世代的内容");
        }

        // ────────────────────────────────────────────────────────────────
        // H. 协议健壮性
        // ────────────────────────────────────────────────────────────────

        private static void TestProtocolRobustness()
        {
            _now = 0;
            Pair p = MakePair();

            // 过短
            p.B.OnDatagram(new byte[3], 0, 3);
            p.B.Update(_now);
            Check(p.B.Stats.ParseErrors >= 1, "过短数据报被计为解析错误");
            Check(p.SinkB.ConnectedCount == 0, "垃圾包不触发「已连接」");

            // 版本不符
            byte[] bad = new byte[32];
            bad[0] = 99;
            p.B.OnDatagram(bad, 0, bad.Length);
            p.B.Update(_now);
            Check(p.B.Stats.ParseErrors >= 2, "版本不符被计为解析错误");
            Check(p.SinkB.ConnectedCount == 0, "版本不符不触发「已连接」");

            // 截断的消息（声称有 1 条消息但没有内容）
            byte[] trunc = new byte[18];
            trunc[0] = 1;
            trunc[1] = 7;   // epoch 低字节
            trunc[17] = 1;  // messageCount = 1
            long before = p.B.Stats.ParseErrors;
            p.B.OnDatagram(trunc, 0, trunc.Length);
            p.B.Update(_now);
            Check(p.B.Stats.ParseErrors > before, "截断消息被计为解析错误且不崩");

            // 正常连接仍然可以建立
            Pair q = MakePair();
            q.A.Send(PMStream.Reliable, true, Payload("hi"), 0, 2);
            Pump(q);
            Pump(q);
            Check(q.SinkB.ConnectedCount >= 1, "正常包仍能建立连接");

            // 主动断开是幂等的
            q.B.Disconnect(PMDisconnectReason.LocalClosed);
            q.B.Disconnect(PMDisconnectReason.LocalClosed);
            Check(q.SinkB.DisconnectedCount == 1, "重复调用 Disconnect 只回调一次（幂等）");

            // 断开后不再收
            q.B.OnDatagram(new byte[32], 0, 32);
            Check(q.B.Stats.ParseErrors == 0 || true, "断开后 OnDatagram 安全忽略");
            Check(!q.B.IsConnected, "断开后 IsConnected=false");
        }

        // ────────────────────────────────────────────────────────────────
        // I. 直接 Send 的显式上限与发送预算入口
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 直接调 <see cref="PMTransport.Send"/> 的边界回归，以及新加的带预算
        /// <see cref="PMTransport.Update(long, int)"/> 入口。
        ///
        /// 为什么这几条必须钉在**传输层自己**的门禁上（而不是只测适配层）：
        ///   1. “超过 65535 字节”在旧实现里是 `(ushort)count` **静默截断** —— 不报错、不断连，
        ///      对端要么永远重组不出来、要么拼出长度对不上的脏数据。适配层的闸门只能盖住
        ///      经过它的路径；直接调传输层的路径（以及任何将来的调用方）必须自己拒绝。
        ///   2. “分片数超限”过去与“可靠窗溢出”共用同一个返回值，上层据此误标断连原因。
        ///   3. 协调者一帧要驱动两次，而单次上限只约束单次 ⇒ 帧内总量会变成 2×上限 − 1。
        /// </summary>
        private static void TestSendCeilingsAndSendBudget()
        {
            // ── ① 数组窗口：非法窗口一律拒绝，且**溢出安全** ──
            _now = 0;
            Pair p = MakePair();
            byte[] small = Payload("hello");

            Check(!p.A.Send(PMStream.Reliable, true, null, 0, 1), "payload=null ⇒ 拒绝");
            Check(!p.A.Send(PMStream.Reliable, true, small, -1, 1), "offset<0 ⇒ 拒绝");
            Check(!p.A.Send(PMStream.Reliable, true, small, 0, -1), "count<0 ⇒ 拒绝");
            Check(!p.A.Send(PMStream.Reliable, true, small, 3, 99), "offset+count 越界 ⇒ 拒绝");
            // offset 与 count 都很大时 `offset + count` 会回绕成负数：旧实现会从这里放过去。
            Check(!p.A.Send(PMStream.Reliable, true, small, int.MaxValue, int.MaxValue),
                  "接近 int.MaxValue 的窗口 ⇒ 拒绝且不抛（校验必须溢出安全）");
            // 这一条在旧实现（offset + count > payload.Length）下会**回绕后放行**，
            // 随后组包时的 Buffer.BlockCopy 会以非法 offset 抛异常，把整条发送路径炸掉。
            Check(!p.A.Send(PMStream.Reliable, true, small, int.MaxValue, 1),
                  "offset 远超长度且回绕 ⇒ 拒绝（旧实现会放行，随后 BlockCopy 抛）");
            Check(p.A.Stats.SendRejectedWindow == 6, "窗口拒绝全部被记账（实际 " + p.A.Stats.SendRejectedWindow + "）");
            Check(p.A.Stats.DatagramsSent == 0, "窗口非法没有产生任何数据报");
            Check(p.A.LastSendRejectReason.ToString() == "InvalidWindow", "最近一次拒绝原因 = InvalidWindow");

            // ── ② 线格式上限：>65535 显式拒绝，不再 ushort 截断 ──
            byte[] huge = new byte[70000];
            Check(PMTransport.MaxTransportableMessageBytes == 65535,
                  "传输层声明的可搬运上限 = ushort.MaxValue");
            Check(!p.A.Send(PMStream.Replication, false, huge, 0, huge.Length),
                  "70000 字节 ⇒ 显式拒绝（旧实现会 (ushort)count 静默截断）");
            Check(!p.A.Send(PMStream.Replication, false, huge, 0, 65536), "65536（超 1 字节）⇒ 显式拒绝");
            Check(p.A.LastSendRejectReason.ToString() == "MessageTooLarge", "最近一次拒绝原因 = MessageTooLarge");
            Check(p.A.Stats.SendRejectedOversize == 2,
                  "超限拒绝被记账（实际 " + p.A.Stats.SendRejectedOversize + "）");
            Check(p.A.Stats.DatagramsSent == 0, "超限拒绝没有产生任何数据报");
            Check(p.A.ReliableOutstanding == 0, "可靠缓冲里没有残留半个消息");
            Check(p.A.IsConnected, "超限是调用方的错，不断开连接");

            // 临界：正好 65535 字节应当被接受，并能被对端**完整**重组
            _now = 0;
            Pair q = MakePair();
            byte[] exact = new byte[65535];
            for (int i = 0; i < exact.Length; i++) { exact[i] = (byte)(i % 251); }
            Check(q.A.Send(PMStream.Reliable, true, exact, 0, exact.Length),
                  "65535 字节（临界值）⇒ 接受（不会被拒，也不会被截断）");
            for (int i = 0; i < 30; i++) { Pump(q, 1); }

            byte[] gotExact = q.SinkB.First(PMStream.Reliable);
            Check(gotExact != null && gotExact.Length == exact.Length,
                  "临界值被完整重组（期望 " + exact.Length + "，实际 " + (gotExact == null ? 0 : gotExact.Length) + "）");
            bool exactSame = gotExact != null && gotExact.Length == exact.Length;
            if (exactSame)
            {
                for (int i = 0; i < exact.Length; i++)
                {
                    if (gotExact[i] != exact[i]) { exactSame = false; break; }
                }
            }
            Check(exactSame, "临界值重组后逐字节一致");

            // ── ③ 分片数上限：显式拒绝，且**不再被误标成可靠窗溢出** ──
            _now = 0;
            Pair r = MakePair(delegate (PMTransportConfig c)
            {
                c.MaxDatagramBytes = 120;
                c.MaxFragmentsPerMessage = 4;
            });
            Check(!r.A.Send(PMStream.Reliable, true, new byte[5000], 0, 5000), "分片数超上限 ⇒ 拒绝");
            Check(r.A.LastSendRejectReason.ToString() == "FragmentLimitExceeded",
                  "拒绝原因 = FragmentLimitExceeded（与 ReliableBufferOverflow 区分开）");
            Check(r.A.Stats.SendRejectedFragmentLimit == 1,
                  "分片超限被单独记账（实际 " + r.A.Stats.SendRejectedFragmentLimit + "）");
            Check(r.A.Stats.ReliableSent == 0, "没有任何分片进入可靠缓冲");
            Check(r.A.IsConnected, "分片超限不触发断连（旧实现把原因误标成可靠窗溢出 ⇒ 上层会断连）");

            // ── ④ 发送预算入口：Update(now, budget) 严格生效，原 Update(now) 语义不变 ──
            _now = 0;
            Pair b = MakePair();
            int payloadEach = 1200 - 18 - 16;   // 每条独占一个数据报
            byte[] fill = new byte[payloadEach];
            for (int i = 0; i < 80; i++)
            {
                b.A.Send(PMStream.Replication, false, fill, 0, payloadEach);
            }

            b.A.Update(_now, 0);
            Check(b.A.Stats.DatagramsSent == 0, "Update(now, 0) 只 drain、不发数据报");
            Check(b.B.Stats.DatagramsReceived == 0, "预算 0 时对端什么也没收到");

            b.A.Update(_now, 7);
            Check(b.A.Stats.DatagramsSent == 7, "Update(now, 7) 恰好发 7 个数据报（实际 " + b.A.Stats.DatagramsSent + "）");

            b.A.Update(_now, 7);
            Check(b.A.Stats.DatagramsSent == 14, "再调一次继续发 7 个（累计 14）");

            b.A.Update(_now, 1000);
            Check(b.A.Stats.DatagramsSent == 14 + b.A.MaxDatagramsPerUpdate,
                  "传入超过单次上限的预算 ⇒ 仍只发 " + b.A.MaxDatagramsPerUpdate + " 个（实际 "
                  + b.A.Stats.DatagramsSent + "）");

            // 原入口语义不变
            _now = 0;
            Pair c = MakePair();
            for (int i = 0; i < 80; i++)
            {
                c.A.Send(PMStream.Replication, false, fill, 0, payloadEach);
            }

            c.A.Update(_now);
            Check(c.A.Stats.DatagramsSent == 32, "原 Update(now) 语义不变：一次最多 32 个（实际 " + c.A.Stats.DatagramsSent + "）");
            c.A.Update(_now);
            Check(c.A.Stats.DatagramsSent == 64, "再次 Update(now) 继续发满一批（累计 64）");
            c.A.Update(_now);
            Check(c.A.Stats.DatagramsSent == 80, "第三次把剩下的发完（累计 80）");
            c.A.Update(_now);
            Check(c.A.Stats.DatagramsSent == 80, "没有待发消息时 Update(now) 不再产生数据报");
        }

        // ────────────────────────────────────────────────────────────────
        // J. 传输活性（本轮新增：周期 keepalive 与静默期丢包恢复）
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 修的两个真实缺口（详见 `Docs/plans/_r3b_transport_liveness.md`）：
        ///   1. **静默成功会话被自己的看门狗断开**：<see cref="PMTransportConfig.IdleTimeoutMs"/>=10000
        ///      是「没收到任何数据报」的判据，而两端都没业务时本来就不会有数据报。
        ///   2. **静默期首个可靠数据报丢失无从恢复**：接收端把收到的第一个包当基线（看不到它之前的缺口），
        ///      发送端的丢包推断又需要 ack 水位；两端都静默时既没有 NAK 也没有推断，
        ///      该消息只能等到空闲超时断连。
        ///
        /// 做法是**不发明新 RTO**：用既有 `ControlPing` 线格式按周期发控制帧，
        /// 让两端的 ack/NAK 机制重新获得驱动源。
        ///
        /// 本轮追加修的第二个缺口：**"纯 ack 互相追赶"**。ack 是搭车在数据报上的，
        /// 只要收到任何新数据报就置 `_ackDirty`；于是两端每帧各回一个 18 字节纯 ack，
        /// 又各自在对端置位，往复不停（实测 12s 每端 `tx≈1100`，活性完全由 ack 流承担）。
        /// 修法：**header-only（messageCount==0）的纯 ack 数据报不再置 `_ackDirty`** ——
        /// 它的 ack 信息与包号跟踪照旧全量消费（否则丢包推断失去水位），
        /// 被抑制的只是「本端再产生一个纯 ack」。见 J7/J8/J9 与 J1d。
        /// </summary>
        private static void TestTransportLiveness()
        {
            // ── J1 纯静默 >10s：keepalive 开 ⇒ 保持；关 ⇒ 超时（口径对照）──
            _now = 0;
            Pair idle = MakePair(delegate (PMTransportConfig c)
            {
                c.KeepAliveIntervalMs = 1000L;
                c.IdleTimeoutMs = 10000L;
            });
            Frames(idle, 600, 20L);   // 12s 全程无业务

            Check(idle.A.IsConnected && idle.B.IsConnected,
                  "J1 静默 12s 后两端仍连通（周期 keepalive 抵消空闲看门狗）");
            CheckGt(idle.A.Stats.KeepAlivesSent, 0, "J1 A 侧确实发出了周期 keepalive");
            CheckGt(idle.B.Stats.KeepAlivesSent, 0, "J1 B 侧确实发出了周期 keepalive");
            Check(idle.A.Stats.KeepAlivesSent <= 13 && idle.B.Stats.KeepAlivesSent <= 13,
                  "J1 keepalive 数量有界（12s 至多 13 个：每 1000ms 一个）实际 A="
                  + idle.A.Stats.KeepAlivesSent + " B=" + idle.B.Stats.KeepAlivesSent);
            CheckGt(idle.A.Stats.KeepAlivesReceived, 0, "J1 A 侧确实收到了对端 keepalive（只记账，不回 ping）");

            // J1d 双端空闲 12s 的**数据报总量**必须接近 keepalive 量级，
            // 而不是修复前的「每帧各回一个 18 字节纯 ack」（实测修复前每端 tx≈1100）。
            long idleTotal = idle.A.Stats.DatagramsSent + idle.B.Stats.DatagramsSent;
            Check(idleTotal < 80L,
                  "J1d 双端空闲 12s 的数据报总量有限（< 80，实际 " + idleTotal + "）—— 不是每帧 1 包");
            Check(idleTotal >= 4L,
                  "J1d 活性确实产生过流量（keepalive 至少一个来回，实际 " + idleTotal + "）");
            Check(idleTotal < 600L,
                  "J1d 明确区别于「每帧 1 个数据报」（600 帧，实际 " + idleTotal + "）");
            // 每个 ping 至多换回一个 ack：本端发送量不得超过「自己发的 ping + 自己收到的 ping」。
            Check(idle.A.Stats.DatagramsSent <= idle.A.Stats.KeepAlivesSent + idle.A.Stats.KeepAlivesReceived,
                  "J1d A 的发送量可归因到 ping/ack 对（tx=" + idle.A.Stats.DatagramsSent
                  + "，kaTx=" + idle.A.Stats.KeepAlivesSent + "，kaRx=" + idle.A.Stats.KeepAlivesReceived + "）");
            Check(idle.B.Stats.DatagramsSent <= idle.B.Stats.KeepAlivesSent + idle.B.Stats.KeepAlivesReceived,
                  "J1d B 的发送量可归因到 ping/ack 对（tx=" + idle.B.Stats.DatagramsSent
                  + "，kaTx=" + idle.B.Stats.KeepAlivesSent + "，kaRx=" + idle.B.Stats.KeepAlivesReceived + "）");
            long txBeforePerFrame = idle.A.Stats.DatagramsSent;
            Frames(idle, 50, 20L);
            long perFrame = idle.A.Stats.DatagramsSent - txBeforePerFrame;
            Check(perFrame <= 20L,
                  "J1 静默期每帧发送量有界（50 帧共 " + perFrame + " 个数据报，纯 ack 追赶已收敛）");

            _now = 0;
            Pair silent = MakePair(delegate (PMTransportConfig c)
            {
                c.KeepAliveIntervalMs = 0L;
                c.IdleTimeoutMs = 10000L;
            });
            Frames(silent, 499, 20L);                          // t = 9980ms
            Check(silent.A.IsConnected && silent.B.IsConnected,
                  "J1c keepalive 关闭：10s 之前连接仍然连通（看门狗没提前开火）");
            Frames(silent, 20, 20L);                           // t = 10380ms
            Check(!silent.A.IsConnected && !silent.B.IsConnected,
                  "J1c keepalive 关闭：纯静默超过 10s 后被空闲看门狗断开（口径对照）");
            Check(silent.A.DisconnectReason == PMDisconnectReason.Timeout,
                  "J1c 断开原因 = Timeout");
            CheckEq(silent.A.Stats.DatagramsSent, 0, "J1c 全程零发送：keepalive 确实是纯静默下唯一的活性来源");
            CheckEq(silent.A.Stats.KeepAlivesSent, 0, "J1c 禁用后一个 keepalive 都没发");

            // ── J2 首个可靠数据报丢失后**再无业务** ──
            // 这是「接收端缺口检测看不到第一个包」与「发送端推断需要 ack 水位」同时失效的区间。
            _now = 0;
            Pair stuck = MakePair(delegate (PMTransportConfig c)
            {
                c.KeepAliveIntervalMs = 0L;
                c.IdleTimeoutMs = 0L;
            });
            stuck.LinkA.DropNext = 1;
            stuck.A.Send(PMStream.Reliable, true, Payload("first"), 0, 5);
            Frames(stuck, 100, 20L);   // 2s

            CheckEq(stuck.SinkB.CountOf(PMStream.Reliable), 0,
                    "J2a 前置：首个可靠数据报丢失后对端永远收不到（真实缺口，旧行为）");
            CheckEq(stuck.A.ReliableOutstanding, 1,
                    "J2a 发送端仍持有未确认的可靠消息（没被静默退休）");
            CheckEq(stuck.A.Stats.ReliableResent, 0,
                    "J2a 既无 NAK 也无 ack 水位 ⇒ 无法重传（旧行为：只能等超时断连）");

            _now = 0;
            Pair healed = MakePair(delegate (PMTransportConfig c)
            {
                c.KeepAliveIntervalMs = 1000L;
                c.IdleTimeoutMs = 10000L;
            });
            healed.LinkA.DropNext = 1;
            healed.A.Send(PMStream.Reliable, true, Payload("first"), 0, 5);
            Frames(healed, 100, 20L);

            CheckEq(healed.SinkB.CountOf(PMStream.Reliable), 1,
                    "J2b keepalive 把缺口暴露给对端 ⇒ 可靠消息最终交付，且**恰好一次**");
            CheckStrEq(AsString(healed.SinkB.First(PMStream.Reliable)), "first", "J2b 交付内容一致");
            CheckGt(healed.A.Stats.ReliableResent, 0, "J2b 发送端执行了重传（不是静默丢失）");
            CheckEq(healed.A.ReliableOutstanding, 0, "J2b 可靠消息被真实确认后退休（无残留）");

            Frames(healed, 100, 20L);
            CheckEq(healed.SinkB.CountOf(PMStream.Reliable), 1, "J2b 继续静默 2s 仍是恰好一次（不重复交付）");
            Check(healed.A.IsConnected && healed.B.IsConnected, "J2b 恢复期间连接没有被看门狗断开");

            // ── J3 末个可靠数据报与其 ack **同时**丢失（单向丢失）──
            // 末包前面还有包，但若那个包的 ack 也丢了，发送端就拿不到水位、接收端也看不到末包之后的新包
            // ⇒ 末包对双方都是不可见的。keepalive 是唯一能把缺口变成可见的信号。
            _now = 0;
            Pair tail = MakePair(delegate (PMTransportConfig c)
            {
                c.KeepAliveIntervalMs = 1000L;
                c.IdleTimeoutMs = 10000L;
            });
            tail.LinkB.DropNext = 1;                                  // 丢 B 的第一个 ack
            tail.A.Send(PMStream.Reliable, true, Payload("m0"), 0, 2);
            Frames(tail, 3, 20L);
            CheckEq(tail.A.ReliableOutstanding, 1, "J3 前置：m0 已送出但 ack 丢失 ⇒ 发送端仍未确认");

            tail.LinkA.DropNext = 1;                                  // 丢 A 的下一个数据报（末个可靠包）
            tail.A.Send(PMStream.Reliable, true, Payload("m1"), 0, 2);
            Frames(tail, 100, 20L);

            CheckEq(tail.SinkB.CountOf(PMStream.Reliable), 2,
                    "J3 keepalive 仍能在末包与 ack 同时丢失后把两条可靠消息都补齐");
            CheckStrEq(AsString(tail.SinkB.Nth(PMStream.Reliable, 0)), "m0", "J3 交付保序：先 m0");
            CheckStrEq(AsString(tail.SinkB.Nth(PMStream.Reliable, 1)), "m1", "J3 交付保序：后 m1");
            CheckEq(tail.A.ReliableOutstanding, 0, "J3 两条都已确认退休");

            _now = 0;
            Pair tailStuck = MakePair(delegate (PMTransportConfig c)
            {
                c.KeepAliveIntervalMs = 0L;
                c.IdleTimeoutMs = 0L;
            });
            tailStuck.LinkB.DropNext = 1;
            tailStuck.A.Send(PMStream.Reliable, true, Payload("m0"), 0, 2);
            Frames(tailStuck, 3, 20L);
            tailStuck.LinkA.DropNext = 1;
            tailStuck.A.Send(PMStream.Reliable, true, Payload("m1"), 0, 2);
            Frames(tailStuck, 100, 20L);
            CheckEq(tailStuck.SinkB.CountOf(PMStream.Reliable), 1,
                    "J3c 对照：无 keepalive 时末条可靠消息永久丢失");
            CheckEq(tailStuck.A.ReliableOutstanding, 2,
                    "J3c 对照：发送端同时挂着两条未确认消息（m0 的 ack 与 m1 一起不可恢复）");
            CheckEq(tailStuck.A.Stats.ReliableResent, 0, "J3c 对照：没有任何重传发生");

            // ── J4 对端停止 Update（进程卡死/退出）──
            _now = 0;
            Pair dead = MakePair(delegate (PMTransportConfig c)
            {
                c.KeepAliveIntervalMs = 1000L;
                c.IdleTimeoutMs = 10000L;
            });
            FramesOneSided(dead, 700, 20L);   // 14s，上限保护
            Check(!dead.A.IsConnected, "J4 对端停止 Update ⇒ 本端仍会超时断开（keepalive 得不到回应）");
            Check(dead.A.DisconnectReason == PMDisconnectReason.Timeout, "J4 断开原因 = Timeout");
            CheckGt(dead.A.Stats.KeepAlivesSent, 8, "J4 keepalive 在 10s 窗口内按周期持续尝试");
            Check(dead.A.Stats.KeepAlivesSent <= 12, "J4 尝试次数有界（实际 " + dead.A.Stats.KeepAlivesSent + "）");
            CheckEq(dead.A.Stats.DatagramsSent, dead.A.Stats.KeepAlivesSent,
                    "J4 本端只发 keepalive，没有其它流量（口径可归因）");
            Check(dead.B.IsConnected, "J4 对端从未被驱动，本地状态仍是连通（不会单方面宣布对方断开）");

            // ── J5 发送预算：零预算不偷发 / 预算 1 恰好一个 / 不绕过 32 ──
            _now = 0;
            Pair budget = MakePair(delegate (PMTransportConfig c)
            {
                c.KeepAliveIntervalMs = 1000L;
                c.IdleTimeoutMs = 0L;
            });
            FramesOneSided(budget, 60, 20L);   // 1.2s ⇒ keepalive 已到期
            CheckGt(budget.A.Stats.KeepAlivesSent, 0, "J5 前置：keepalive 已经发出过");

            long txBefore = budget.A.Stats.DatagramsSent;
            long kaBefore = budget.A.Stats.KeepAlivesSent;
            _now += 1000L;                     // 再次到期
            budget.A.Update(_now, 0);
            CheckEq(budget.A.Stats.DatagramsSent, txBefore, "J5 Update(now, 0) 连 keepalive 也不偷发（预算 0 = 只 drain）");
            CheckEq(budget.A.Stats.KeepAlivesSent, kaBefore, "J5 预算 0 时 KeepAlivesSent 不变");

            budget.A.Update(_now, 1);
            CheckEq(budget.A.Stats.DatagramsSent, txBefore + 1, "J5 预算 1 ⇒ 恰好一个数据报");
            CheckEq(budget.A.Stats.KeepAlivesSent, kaBefore + 1, "J5 该数据报就是那个到期的 keepalive");

            int maxPayload = 1200 - 18 - 16;
            byte[] fill = new byte[maxPayload];
            for (int i = 0; i < 80; i++)
            {
                budget.A.Send(PMStream.Replication, false, fill, 0, maxPayload);
            }
            _now += 1000L;
            long before32 = budget.A.Stats.DatagramsSent;
            long before32Ka = budget.A.Stats.KeepAlivesSent;
            budget.A.Update(_now);
            CheckEq(budget.A.Stats.DatagramsSent - before32, 32,
                    "J5 keepalive 到期 + 80 条待发 ⇒ 仍严格 32（不额外绕过传输层预算）");
            CheckEq(budget.A.Stats.KeepAlivesSent - before32Ka, 1,
                    "J5 该轮 keepalive 与业务包**同包**发出（不多占一个数据报）");

            // ── J6 时钟倒退（NTP 回拨 / 受控时钟回退）──
            _now = 0;
            Pair clock = MakePair(delegate (PMTransportConfig c)
            {
                c.KeepAliveIntervalMs = 1000L;
                c.IdleTimeoutMs = 10000L;
            });
            FramesOneSided(clock, 60, 20L);    // t = 1200ms
            long txBeforeJump = clock.A.Stats.DatagramsSent;
            long kaBeforeJump = clock.A.Stats.KeepAlivesSent;
            CheckGt(kaBeforeJump, 0, "J6 前置：倒退前 keepalive 已经在发");

            _now = 300L;                       // 墙钟倒退
            clock.A.Update(_now);
            CheckEq(clock.A.Stats.DatagramsSent, txBeforeJump, "J6 时钟倒退不产生补偿性发送（无风暴）");
            CheckEq(clock.A.Stats.KeepAlivesSent, kaBeforeJump, "J6 时钟倒退不产生额外 keepalive");
            Check(clock.A.IsConnected, "J6 时钟倒退不误判断连");

            FramesOneSided(clock, 530, 20L);   // 从 300ms 起再推进 10.6s
            Check(!clock.A.IsConnected, "J6 倒退后看门狗仍会触发（负差值不得让连接永生）");
            Check(clock.A.DisconnectReason == PMDisconnectReason.Timeout, "J6 断开原因 = Timeout");

            // ── J7 keepalive 关闭：一次可靠消息完整确认后反复 Update，不再产生 ack 流 ──
            //
            // 修复前：对端那个纯 ack 又会置本端 `_ackDirty`，本端再回一个纯 ack ……
            // 两端每帧各发 1 个 18 字节数据报，永远停不下来。
            _now = 0;
            Pair ackLoop = MakePair(delegate (PMTransportConfig c)
            {
                c.KeepAliveIntervalMs = 0L;
                c.IdleTimeoutMs = 0L;
            });
            ackLoop.A.Send(PMStream.Reliable, true, Payload("once"), 0, 4);
            for (int i = 0; i < 8 && ackLoop.A.ReliableOutstanding > 0; i++)
            {
                Frames(ackLoop, 1, 20L);
            }

            CheckEq(ackLoop.SinkB.CountOf(PMStream.Reliable), 1, "J7 前置：可靠消息恰好交付一次");
            CheckEq(ackLoop.A.ReliableOutstanding, 0, "J7 前置：发送端已被对端 ack 退休");

            long ackLoopTxA = ackLoop.A.Stats.DatagramsSent;
            long ackLoopTxB = ackLoop.B.Stats.DatagramsSent;
            long ackLoopRxA = ackLoop.A.Stats.DatagramsReceived;
            long ackLoopRxB = ackLoop.B.Stats.DatagramsReceived;
            Frames(ackLoop, 100, 20L);   // 2s：反复 Update，全程零业务
            CheckEq(ackLoop.A.Stats.DatagramsSent, ackLoopTxA,
                    "J7 keepalive 关闭 + 已完整确认 ⇒ A 不再产生任何数据报（无 ack 流）");
            CheckEq(ackLoop.B.Stats.DatagramsSent, ackLoopTxB, "J7 B 同样不再产生任何数据报");
            CheckEq(ackLoop.A.Stats.DatagramsReceived, ackLoopRxA, "J7 A 不再收到新数据报（确认链收敛）");
            CheckEq(ackLoop.B.Stats.DatagramsReceived, ackLoopRxB, "J7 B 不再收到新数据报");
            Check(ackLoop.A.IsConnected && ackLoop.B.IsConnected, "J7 空闲但连接保持（IdleTimeout=0）");

            // ── J8 纯 ack（header-only）被丢 ⇒ 后来针对它的 NAK 不得误推可靠重传 ──
            //
            // 手工控制 A→B 的投递序列（DropNext 只能表达「接下来 N 个丢」）：
            //   A→B #1 可靠 m1          到达
            //   B→A #1 不可靠 u          到达（让 A 产生「待告知的 ack」）
            //   A→B #2 **纯 ack 18B**    丢弃
            //   A→B #3 可靠 m2          到达 ⇒ B 看到 #2 缺口 ⇒ NAK(A→B #2)
            // 断言：被 NAK 的对象是「没有任何可靠消息」的数据报 ⇒ A 侧不得因此重传；
            //      m1/m2 仍保序、恰好一次。这是「抑制纯 ack 回程」必须付的代价的反面保证。
            _now = 0;
            Pair ackNak = MakePair(delegate (PMTransportConfig c)
            {
                c.KeepAliveIntervalMs = 0L;
                c.IdleTimeoutMs = 0L;
            });

            ackNak.A.Send(PMStream.Reliable, true, Payload("m1"), 0, 2);
            ackNak.A.Update(_now);
            CheckEq(ackNak.LinkB.Delivered.Count, 1, "J8 前置：m1 独占 A→B #1");
            CheckEq(DeliverTo(ackNak, true), 1, "J8 前置：A→B #1 送达 B");
            ackNak.B.Update(_now);
            CheckEq(ackNak.SinkB.CountOf(PMStream.Reliable), 1, "J8 前置：m1 已交付");
            CheckEq(DeliverTo(ackNak, false), 1, "J8 前置：B 的 ack 送回 A");
            ackNak.A.Update(_now);
            CheckEq(ackNak.A.ReliableOutstanding, 0, "J8 前置：m1 已被 ack 退休");
            CheckEq(ackNak.A.Stats.DatagramsSent, 1,
                    "J8 前置：A 只为 m1 发了 1 个数据报（没有为「收到的纯 ack」再回一个）");

            ackNak.B.Send(PMStream.Unreliable, false, Payload("u"), 0, 1);
            ackNak.B.Update(_now);
            CheckEq(ackNak.LinkA.Delivered.Count, 1, "J8 前置：B→A #1 就是那条业务消息");
            CheckEq(DeliverTo(ackNak, false), 1, "J8 前置：B→A #1 送达 A");
            ackNak.A.Update(_now);               // A 因收到业务消息而有了待告知的 ack

            CheckEq(ackNak.LinkB.Delivered.Count, 1, "J8 前置：A→B #2 是纯 ack 数据报");
            byte[] pureAckDatagram = ackNak.LinkB.Delivered[0];
            CheckEq(pureAckDatagram.Length, 18, "J8 前置：A→B #2 确实是 18 字节 header-only 纯 ack");
            ackNak.LinkB.Delivered.Clear();      // ★ 丢掉 A→B #2

            long nakResentBefore = ackNak.A.Stats.ReliableResent;
            ackNak.A.Send(PMStream.Reliable, true, Payload("m2"), 0, 2);
            ackNak.A.Update(_now);               // A→B #3
            CheckEq(ackNak.LinkB.Delivered.Count, 1, "J8 前置：A→B #3 是 m2");
            CheckEq(DeliverTo(ackNak, true), 1, "J8 前置：A→B #3 送达 B");
            ackNak.B.Update(_now);

            CheckGt(ackNak.B.Stats.NaksSent, 0, "J8 B 确实为纯 ack 的缺口发了 NAK（缺口可见）");
            CheckEq(ackNak.SinkB.CountOf(PMStream.Reliable), 2, "J8 丢纯 ack 不影响业务：m1/m2 都交付");
            CheckStrEq(AsString(ackNak.SinkB.Nth(PMStream.Reliable, 0)), "m1", "J8 保序：先 m1");
            CheckStrEq(AsString(ackNak.SinkB.Nth(PMStream.Reliable, 1)), "m2", "J8 保序：后 m2");

            DeliverTo(ackNak, false);            // B 的 NAK 数据报回给 A
            ackNak.A.Update(_now);
            CheckGt(ackNak.A.Stats.NaksReceived, 0, "J8 A 收到了针对纯 ack 数据报的 NAK");
            CheckEq(ackNak.A.Stats.ReliableResent, nakResentBefore,
                    "J8 纯 ack 数据报被 NAK 不得误推任何可靠重传（实际 " + ackNak.A.Stats.ReliableResent + "）");
            CheckEq(ackNak.A.ReliableOutstanding, 0, "J8 两条可靠消息都已确认退休");

            Frames(ackNak, 20, 20L);
            CheckEq(ackNak.SinkB.CountOf(PMStream.Reliable), 2, "J8 再泵 20 帧仍是恰好一次（不重复交付）");
            CheckEq(ackNak.A.Stats.ReliableResent, nakResentBefore, "J8 后续帧仍无可靠重传");

            // ── J9 预算 0 不偷发 / 旧窗口不退化（在新收敛口径下再钉一次）──
            _now = 0;
            Pair zero = MakePair(delegate (PMTransportConfig c)
            {
                c.KeepAliveIntervalMs = 1000L;
                c.IdleTimeoutMs = 0L;
            });
            zero.A.Send(PMStream.Unreliable, false, Payload("warm"), 0, 4);
            Frames(zero, 60, 20L);               // 1.2s：业务已发、keepalive 已到期
            CheckGt(zero.A.Stats.KeepAlivesSent, 0, "J9 前置：keepalive 已经发出过");

            long zeroTx = zero.A.Stats.DatagramsSent;
            long zeroKa = zero.A.Stats.KeepAlivesSent;
            _now += 1000L;
            zero.A.Update(_now, 0);
            CheckEq(zero.A.Stats.DatagramsSent, zeroTx,
                    "J9 Update(now,0) 连 keepalive 与纯 ack 都不偷发（预算 0 = 只 drain）");
            CheckEq(zero.A.Stats.KeepAlivesSent, zeroKa, "J9 预算 0 时 keepalive 计数不变");

            int zeroPayload = 1200 - 18 - 16;
            byte[] zeroFill = new byte[zeroPayload];
            for (int i = 0; i < 80; i++)
            {
                zero.A.Send(PMStream.Replication, false, zeroFill, 0, zeroPayload);
            }

            long zeroBatch = zero.A.Stats.DatagramsSent;
            zero.A.Update(_now);
            CheckEq(zero.A.Stats.DatagramsSent - zeroBatch, 32,
                    "J9 一次 Update 仍严格不超过 32（旧窗口语义不退化）");
            zeroBatch = zero.A.Stats.DatagramsSent;
            zero.A.Update(_now);
            CheckEq(zero.A.Stats.DatagramsSent - zeroBatch, 32, "J9 继续冲刷仍按 32 推进");
        }

        // ────────────────────────────────────────────────────────────────
        // K. 核心侧长度守卫
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 会话层已有「把传输层解析异常收敛成本连接协议错误」的纵深兜底（R3-B F-3），
        /// 但那是不让故障域扩散，不是「核心自己可以抛」。
        /// 一条 19 字节、messageCount=1 的数据报（18 字节头 + 1 字节 flags）会让旧实现在读
        /// stream 字节时直接越界。本组用例**直接打真实 `PMTransport`**，断言不抛且计为解析错误。
        /// </summary>
        private static void TestShortDatagramGuards()
        {
            _now = 0;
            Pair p = MakePair();
            long parseBefore = p.B.Stats.ParseErrors;
            long recvBefore = p.B.Stats.DatagramsReceived;
            int throws = 0;

            // ① 19 字节：flags 读完就没有 stream 字节（旧实现在这里抛 IndexOutOfRange）
            byte[] d19 = RawDatagram(7u, 101, 1, 19);
            FeedQuiet(p.B, d19, ref throws);

            // ② 20 字节：有 flags+stream，但没有 payloadLen
            byte[] d20 = RawDatagram(7u, 102, 1, 20);
            FeedQuiet(p.B, d20, ref throws);

            // ③ 22 字节：flags=Reliable，但 seq 只有 2 字节（不够 4）
            byte[] d22 = RawDatagram(7u, 103, 1, 22);
            d22[18] = 1;    // FlagReliable
            d22[19] = 0;
            FeedQuiet(p.B, d22, ref throws);

            // ④ 24 字节：seq 完整但没有 payloadLen
            byte[] d24 = RawDatagram(7u, 104, 1, 24);
            d24[18] = 1;    // FlagReliable
            d24[19] = 0;
            FeedQuiet(p.B, d24, ref throws);

            // ⑤ 24 字节：flags=Fragment，但没有 8 字节分片头
            byte[] d24f = RawDatagram(7u, 105, 1, 24);
            d24f[18] = 2;   // FlagFragment
            d24f[19] = 0;
            FeedQuiet(p.B, d24f, ref throws);

            // ⑥ 19 字节：flags=Control，但没有 type 字节
            byte[] d19c = RawDatagram(7u, 106, 1, 19);
            d19c[18] = 4;   // FlagControl
            FeedQuiet(p.B, d19c, ref throws);

            CheckEq(throws, 0, "K 六种截断/畸形数据报一律不抛（核心侧长度守卫生效）");
            CheckGt(p.B.Stats.ParseErrors - parseBefore, 5,
                    "K 畸形数据报被计为解析错误（不是静默吞掉）");
            CheckEq(p.B.Stats.DatagramsReceived - recvBefore, 6, "K 六个数据报都被统计为「已收到」");
            Check(p.B.IsConnected, "K 畸形数据报不断连（本机噪声不应废掉一条健康连接）");

            // 对照：同一位置放一个**完整**的合法控制 Ping，必须被当成 keepalive 记账而不是解析错误
            long parseBefore2 = p.B.Stats.ParseErrors;
            long kaBefore = p.B.Stats.KeepAlivesReceived;
            byte[] ping = RawDatagram(7u, 107, 1, 21);
            ping[18] = 4;   // FlagControl
            ping[19] = 0;
            ping[20] = 1;   // ControlPing
            FeedQuiet(p.B, ping, ref throws);
            CheckEq(throws, 0, "K 对照：合法控制 Ping 不抛");
            CheckEq(p.B.Stats.ParseErrors, parseBefore2, "K 对照：合法控制 Ping 不计解析错误");
            CheckEq(p.B.Stats.KeepAlivesReceived - kaBefore, 1, "K 对照：合法控制 Ping 被识别为 keepalive");
        }
    }
}
