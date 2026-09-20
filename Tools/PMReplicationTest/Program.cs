using System;
using System.Collections.Generic;
using PMNet;

namespace PMReplicationTest
{
    /// <summary>
    /// R2 / T41 验收：运行时属性复制层（M06 最小）的契约验证。
    ///
    /// 事实来源：
    ///   - `Docs/plans/net-r0-contract.md` §2.4（D-R0-12..18）、§5（复制与生命周期契约）、§3.5
    ///   - `Docs/plans/net-r2-codegen-contract.md` §1/§3/§7（B 块交付物）
    ///   - `D:/hyld-refactor-survey/R0_3_replication.md` C-1..C-20、C-29..C-38
    ///
    /// 结构：
    ///   [1] 干净实现跑全套断言 —— 期望 0 失败；
    ///   [2] 对若干处决策点各注入一个缺陷，重跑全套并断言「期望的断言确实失败了」。
    ///       这一步是门禁的元验证：如果注入缺陷后仍然全绿，说明断言是空的。
    /// </summary>
    internal static class Program
    {
        // ------------------------------------------------------------------ 断言框架

        private static int _passed;
        private static readonly List<string> _failures = new List<string>();
        private static Func<PMRepOptions, PMReplicationChannel> _senderFactory;
        private static readonly List<string> _onRepLog = new List<string>();

        private struct SuiteResult
        {
            public int Passed;
            public List<string> Failures;
        }

        private static int Main()
        {
            Console.WriteLine("=== R2 / T41：运行时属性复制层（M06 最小）契约验证 ===");
            Console.WriteLine("覆盖：每连接基线 / ACK 只能前进 / 变更掩码比较 / 8 项复制条件 /");
            Console.WriteLine("      条件跃迁全掩码 / 初始状态全量 / OnRep 分发 / 有界 / 端到端收敛");
            Console.WriteLine();

            SuiteResult clean = RunSuite(null);
            PrintSuite("[1] 干净实现（无缺陷注入）", clean);

            Console.WriteLine();
            Console.WriteLine("[2] 负向验证：注入缺陷，确认门禁真的抓得到");
            Console.WriteLine();

            FaultCase[] faults = new FaultCase[]
            {
                new FaultCase(
                    "F1 旧 ACK 不清新脏位 / 不回退版本",
                    "IsAckStale 恒 false（旧 ACK 被接受）+ ShouldAdvanceWithoutInflight 恒 true（无在途记录也推进水位，RV2 之前的行为）+ TryClearSettledDirty 无条件清脏",
                    delegate (PMRepOptions o) { return new FaultOldAckChannel(o); },
                    new string[] { "B5", "B6", "B8", "B9" }),
                new FaultCase(
                    "F2 条件跃迁全掩码（D-R0-15）",
                    "ShouldForceIncludeOnTransition 恒 false（跃迁不补发）",
                    delegate (PMRepOptions o) { return new FaultNoTransitionChannel(o); },
                    new string[] { "E6", "E7", "E8" }),
                new FaultCase(
                    "F3 值未变不发（D-R0-13）",
                    "HasValueChangedSinceBaseline 恒 true（标脏即发，不比较）",
                    delegate (PMRepOptions o) { return new FaultSendWithoutCompareChannel(o); },
                    new string[] { "C2", "C3" }),
                new FaultCase(
                    "F4 接收侧每写一个属性就回调（RV2 返工前）",
                    "DispatchOnRepPerProperty 恒 true（写一个属性立刻派发它的 OnRep）",
                    delegate (PMRepOptions o) { return new FaultPerPropertyDispatchChannel(o); },
                    new string[] { "O4" }),
                new FaultCase(
                    "F5 OnRep 回调异常直接穿透（RV2 返工前）",
                    "IsolateOnRepExceptions 恒 false（回调异常不隔离、穿透到网络层）",
                    delegate (PMRepOptions o) { return new FaultUnisolatedOnRepChannel(o); },
                    new string[] { "[P." }),
            };

            int caughtCount = 0;
            for (int i = 0; i < faults.Length; i++)
            {
                FaultCase fault = faults[i];
                SuiteResult r = RunSuite(fault.Factory);

                Console.WriteLine("── " + fault.Name);
                Console.WriteLine("   缺陷：" + fault.Injection);
                Console.WriteLine("   注入前失败数：" + clean.Failures.Count + "（干净实现）");
                Console.WriteLine("   注入后失败数：" + r.Failures.Count + "（通过 " + r.Passed + " 项）");

                bool allCaught = true;
                for (int k = 0; k < fault.ExpectedPrefixes.Length; k++)
                {
                    string prefix = fault.ExpectedPrefixes[k];
                    string hit = FindFailureWithPrefix(r.Failures, prefix);
                    bool caught = hit != null;
                    if (!caught) { allCaught = false; }
                    Console.WriteLine("     期望被抓住的断言 " + prefix + " → " + (caught ? "命中：" + hit : "**未命中**"));
                }

                if (!allCaught)
                {
                    Console.WriteLine("     结论：**未命中** —— 该缺陷没有被门禁抓住，断言需要加强");
                }
                else
                {
                    caughtCount++;
                    Console.WriteLine("     结论：命中，门禁抓到了该缺陷");
                }

                Console.WriteLine("     注入后失败清单（最多列 12 条）：");
                for (int f = 0; f < r.Failures.Count && f < 12; f++)
                {
                    Console.WriteLine("       - " + r.Failures[f]);
                }

                Console.WriteLine();
            }

            Console.WriteLine("=== 汇总 ===");
            Console.WriteLine("干净实现：通过 " + clean.Passed + " 项，失败 " + clean.Failures.Count + " 项");
            for (int i = 0; i < clean.Failures.Count; i++)
            {
                Console.WriteLine("  - " + clean.Failures[i]);
            }

            Console.WriteLine("缺陷注入：" + faults.Length + " 个缺陷中命中 " + caughtCount + " 个");

            if (clean.Failures.Count == 0 && caughtCount == faults.Length)
            {
                Console.WriteLine("结果：PASS");
                return 0;
            }

            Console.WriteLine("结果：FAIL");
            return 1;
        }

        private static string FindFailureWithPrefix(List<string> failures, string prefix)
        {
            for (int i = 0; i < failures.Count; i++)
            {
                if (failures[i] != null && failures[i].StartsWith(prefix, StringComparison.Ordinal))
                {
                    return failures[i];
                }
            }

            return null;
        }

        private struct FaultCase
        {
            public string Name;
            public string Injection;
            public Func<PMRepOptions, PMReplicationChannel> Factory;
            public string[] ExpectedPrefixes;

            public FaultCase(string name, string injection, Func<PMRepOptions, PMReplicationChannel> factory, string[] expectedPrefixes)
            {
                Name = name;
                Injection = injection;
                Factory = factory;
                ExpectedPrefixes = expectedPrefixes;
            }
        }

        private static SuiteResult RunSuite(Func<PMRepOptions, PMReplicationChannel> senderFactory)
        {
            _passed = 0;
            _failures.Clear();
            _senderFactory = senderFactory;
            _onRepLog.Clear();

            Section("A. 线格式字节级契约（编解码 + 协议不兼容判定）", TestCodec);
            Section("B. 每连接基线：ACK 只能前进 / 旧 ACK 不清新脏位", TestBaselineAndAck);
            Section("C. 变更掩码：值未变不发（D-R0-13）", TestUnchangedSuppression);
            Section("D. 8 项复制条件 × 角色组合（D-R0-14，legacy 口径）", TestConditionTable);
            Section("E. 条件跃迁强制补发（D-R0-15）", TestConditionTransition);
            Section("F. 初始状态全量 / 新连接补齐（D-R0-12 / D-R0-16）", TestInitialState);
            Section("G. OnRep 分发", TestOnRep);
            Section("H. 有界：单包属性数 / 每对象在途 / 每帧对象预算（D-R0-18）", TestBounds);
            Section("I. 端到端：两条不同基线的连接最终一致（T41）", TestConvergence);
            Section("J. 掩码位序与边界位宽（RV1）：7/8/31/32/63/64/255 与 256 位掩码", TestMaskAndWideSlots);
            Section("K. 写/读侧形状拒绝（RV1）：乱序/重复/越界/尾部/非最短掩码", TestShapeRejection);
            Section("L. 接收应用原子性（RV2）：不部分应用、不回 ACK", TestApplyAtomicity);
            Section("M. 无在途记录的 ACK（RV2）：不盲目推进确认水位", TestAckWithoutInflight);
            Section("N. 脏位位宽与收敛（RV4）：MarkAll/休眠唤醒后不空扫", TestDirtyTrackerRange);
            Section("O. 接收侧回调看到整条新值（RV2 返工）", TestOnRepSeesWholeRecord);
            Section("P. 回调异常与协议失败分离（RV2 返工）", TestOnRepExceptionIsolation);
            Section("Q. 暂存工厂必须产出新对象（RV2 返工）", TestStagingFactoryRejection);
            Section("R. 非纯 Reader 的剩余边界（诚实记录）", TestNonPureReaderBoundary);

            SuiteResult result = new SuiteResult();
            result.Passed = _passed;
            result.Failures = new List<string>(_failures);
            return result;
        }

        private static void PrintSuite(string title, SuiteResult r)
        {
            Console.WriteLine(title + "：通过 " + r.Passed + " 项，失败 " + r.Failures.Count + " 项");
            for (int i = 0; i < r.Failures.Count; i++)
            {
                Console.WriteLine("  - " + r.Failures[i]);
            }
        }

        private static void Section(string name, Action body)
        {
            Console.WriteLine("── " + name);
            try
            {
                body();
            }
            catch (Exception ex)
            {
                _failures.Add("[" + name + "] 测试体抛异常：" + ex.GetType().Name + " " + ex.Message);
                Console.WriteLine("    FAIL 测试体抛异常：" + ex.GetType().Name + " " + ex.Message);
            }

            Console.WriteLine();
        }

        private static void Check(bool ok, string label)
        {
            if (ok)
            {
                _passed++;
            }
            else
            {
                _failures.Add(label);
            }

            Console.WriteLine((ok ? "    OK   " : "    FAIL ") + label);
        }

        // ------------------------------------------------------------------ 被测类型的替身

        /// <summary>
        /// 测试用复制对象：4 个属性槽位（int/int/float/string），
        /// 槽位下标 0..3 与描述符的 `MaskOffset` 一一对应。
        /// </summary>
        internal sealed class RepObj : PMNetObject
        {
            public int A;
            public int B;
            public float C;
            public string S = string.Empty;
        }

        /// <summary>只用于与冻结的 `ShouldReplicateProperty` 做交叉校验的条件载体。</summary>
        internal sealed class CondObj : PMNetObject
        {
            public PMCond[] Conds = new PMCond[] { PMCond.None, PMCond.None, PMCond.None, PMCond.None };

            protected override void CollectLifetimeReplicatedProps(PMRepList outProps)
            {
                for (int i = 0; i < Conds.Length; i++)
                {
                    outProps.Add(i, Conds[i], true);
                }
            }
        }

        /// <summary>简易连接替身：把发出的载荷收集起来，便于"手动投递"。</summary>
        private sealed class TestConn : PMNetConnection
        {
            public readonly List<byte[]> Sent = new List<byte[]>();

            public TestConn(int id, bool serverSide)
                : base(id, serverSide)
            {
            }

            public override bool IsReady { get { return true; } }

            public override void Send(byte[] payload, PMRpcReliability reliability)
            {
                Sent.Add(payload);
            }
        }

        // ---- 缺陷子类（负向验证用；生产代码不会覆写这些决策点）----

        private sealed class FaultOldAckChannel : PMReplicationChannel
        {
            public FaultOldAckChannel(PMRepOptions options)
                : base(options)
            {
            }

            protected override bool IsAckStale(long ackedVersion, long incomingVersion)
            {
                // 缺陷：不做单调判定，旧 ACK 被当成有效确认。
                return false;
            }

            protected override bool ShouldAdvanceWithoutInflight(long version)
            {
                // 缺陷：RV2 之前的实现会"盲目推进水位" —— 即使找不到对应在途记录，
                // 也把 AckedVersion 推上去、丢掉在途链、清揉脏位。
                // 两个注入是成对的：只注入 IsAckStale 已不足以造成伤害
                // （新防护会在"无在途记录"处拦住），而旧实现两者都没拦。
                return true;
            }

            protected override void TryClearSettledDirty(PMNetObject obj, PMRepConnectionState state)
            {
                // 缺陷：无条件清掉整对象的脏位（"这个 ACK 到了 ⇒ 都同步好了"）。
                if (obj != null)
                {
                    obj.Dirty.Clear();
                }
            }
        }

        private sealed class FaultNoTransitionChannel : PMReplicationChannel
        {
            public FaultNoTransitionChannel(PMRepOptions options)
                : base(options)
            {
            }

            protected override bool ShouldForceIncludeOnTransition(int slot)
            {
                // 缺陷：条件由不满足变满足时不补发（D-R0-15 缺实现）。
                return false;
            }
        }

        private sealed class FaultSendWithoutCompareChannel : PMReplicationChannel
        {
            public FaultSendWithoutCompareChannel(PMRepOptions options)
                : base(options)
            {
            }

            protected override bool HasValueChangedSinceBaseline(PMNetObject obj, PMReplicationDescriptor descriptor, int slot, byte[] current, byte[] baseline)
            {
                // 缺陷：标脏即发，不与基线比较（D-R0-13 缺实现）。
                return true;
            }
        }

        private sealed class FaultPerPropertyDispatchChannel : PMReplicationChannel
        {
            public FaultPerPropertyDispatchChannel(PMRepOptions options)
                : base(options)
            {
            }

            protected override bool DispatchOnRepPerProperty()
            {
                // 缺陷：RV2 返工前的实现是"写入一个属性就立刻派发它的 OnRep"。
                // 结果是同一条记录里后面的属性还没写，回调就先看到了旧值。
                return true;
            }
        }

        private sealed class FaultUnisolatedOnRepChannel : PMReplicationChannel
        {
            public FaultUnisolatedOnRepChannel(PMRepOptions options)
                : base(options)
            {
            }

            protected override bool IsolateOnRepExceptions()
            {
                // 缺陷：RV2 返工前的实现不隔离表现回调的异常：异常会从 OnMessage 一路
                // 穿透到调用方（网络层），而已完成提交的那条记录拿不到 ACK
                // ⇒ 发送侧重发 ⇒ 已执行过的回调重复执行。
                return false;
            }
        }

        // ------------------------------------------------------------------ 描述符与测试装置

        private static readonly string[] SlotNames = new string[] { "A", "B", "C", "S" };

        private static readonly PMPropertyWriter[] SlotWriters = new PMPropertyWriter[]
        {
            delegate (PMNetObject t, PMNetWriter w) { w.WriteInt32(((RepObj)t).A); },
            delegate (PMNetObject t, PMNetWriter w) { w.WriteInt32(((RepObj)t).B); },
            delegate (PMNetObject t, PMNetWriter w) { w.WriteFloat(((RepObj)t).C); },
            delegate (PMNetObject t, PMNetWriter w) { w.WriteStringValue(((RepObj)t).S ?? string.Empty); },
        };

        private static readonly PMPropertyReader[] SlotReaders = new PMPropertyReader[]
        {
            delegate (PMNetObject t, PMNetReader r) { ((RepObj)t).A = r.ReadInt32(); },
            delegate (PMNetObject t, PMNetReader r) { ((RepObj)t).B = r.ReadInt32(); },
            delegate (PMNetObject t, PMNetReader r) { ((RepObj)t).C = r.ReadFloat(); },
            delegate (PMNetObject t, PMNetReader r) { ((RepObj)t).S = r.ReadStringValue(); },
        };

        /// <summary>手写一份复制描述符（刻意不经生成器：B 与 A 必须能各自独立验收）。</summary>
        private static PMReplicationDescriptor MakeDescriptor(uint classId, PMCond[] conditions, ushort[] onRepIds)
        {
            int n = SlotNames.Length;
            PMPropertyDescriptor[] props = new PMPropertyDescriptor[n];
            bool hasConditional = false;

            for (int i = 0; i < n; i++)
            {
                PMCond condition = conditions == null ? PMCond.None : conditions[(i < conditions.Length) ? i : 0];
                if (condition != PMCond.None)
                {
                    hasConditional = true;
                }

                props[i].PropertyId = (ushort)(100 + i);
                props[i].Condition = condition;
                props[i].MaskOffset = (ushort)i;
                props[i].MaskBitCount = 1;
                props[i].QuantizerId = 0;
                props[i].OnRepMethodId = onRepIds == null ? (ushort)0 : onRepIds[i];
                props[i].Writer = SlotWriters[i];
                props[i].Reader = SlotReaders[i];
                props[i].PushBased = true;
                props[i].MemberName = SlotNames[i];
                props[i].SetterName = "PMNet_Set" + SlotNames[i];
            }

            PMReplicationDescriptor desc = new PMReplicationDescriptor();
            desc.ClassId = classId;
            desc.Properties = props;
            desc.ChangeMaskBitCount = n;
            desc.HasConditionalMask = hasConditional;
            desc.ProtocolHash = PMStableHash.ClassProtocolHash(classId, props);
            desc.TypeName = "RepObj";
            return desc;
        }

        /// <summary>一套"服务端权威对象 + N 个客户端副本"的联调装置。</summary>
        private sealed class Rig
        {
            public uint ClassId;
            public PMNetWorld ServerWorld;
            public PMReplicationChannel Sender;
            public RepObj ServerObj;

            public readonly List<Client> Clients = new List<Client>();

            public sealed class Client
            {
                public PMNetWorld World;
                public TestConn Conn;
                public PMReplicationChannel Receiver;
                public RepObj Obj;
            }

            public static Rig Create(uint classId, int clientCount, PMCond[] conditions, ushort[] onRepIds, PMRepOptions options)
            {
                PMNetRegistry.Reset();

                PMReplicationDescriptor desc = MakeDescriptor(classId, conditions, onRepIds);
                PMNetClassEntry entry = new PMNetClassEntry();
                entry.ClassId = classId;
                entry.TypeName = "RepObj";
                entry.Rep = desc;
                entry.Factory = delegate () { return new RepObj(); };
                PMNetRegistry.RegisterClass(entry);

                Rig rig = new Rig();
                rig.ClassId = classId;
                rig.ServerWorld = new PMNetWorld(new PMSession(7u, true));
                rig.ServerWorld.RegisterClass(classId, delegate () { return new RepObj(); });

                rig.ServerObj = new RepObj();
                rig.ServerWorld.Spawn(rig.ServerObj, classId);

                rig.Sender = NewSender(options);
                rig.Sender.RegisterObject(rig.ServerObj);
                rig.Sender.RegisterOnRepDispatcher(classId, OnRepHandler);

                for (int i = 0; i < clientCount; i++)
                {
                    rig.AddClient();
                }

                return rig;
            }

            public Client AddClient()
            {
                Client c = new Client();
                c.World = new PMNetWorld(new PMSession(7u, false));
                c.World.RegisterClass(ClassId, delegate () { return new RepObj(); });
                c.Conn = new TestConn(1000 + Clients.Count, true);

                ServerWorld.AddConnection(c.Conn);
                Sender.AddConnection(c.Conn);

                byte[] lifecycle = ServerWorld.BuildLifecycleBatch(c.Conn);
                if (lifecycle != null)
                {
                    c.World.OnLifecycleMessage(lifecycle, 0, lifecycle.Length);
                }

                PMNetObject obj;
                if (!c.World.TryFind(ServerObj.NetId, out obj) || obj == null)
                {
                    throw new InvalidOperationException("客户端世界没有创建出副本对象（生命周期路径有问题）");
                }

                c.Obj = (RepObj)obj;
                // 接收侧也走 `_senderFactory`（见 NewChannel 的注释）：RV2 返工的两处决策点
                // 只发生在接收路径上，不走工厂就注入不到、相应断言就是没牙的。
                c.Receiver = NewChannel();
                c.Receiver.World = c.World;
                c.Receiver.RegisterOnRepDispatcher(ClassId, OnRepHandler);

                Clients.Add(c);
                return c;
            }

            /// <summary>标记全部 4 个槽位为脏（而不是 MarkAllPropertiesDirty：那会置满 64 位）。</summary>
            public void MarkAllDirty()
            {
                for (int i = 0; i < SlotNames.Length; i++)
                {
                    ServerObj.MarkPropertyDirty(i);
                }
            }
        }

        private static void OnRepHandler(PMNetObject obj, ushort methodId)
        {
            _onRepLog.Add(((RepObj)obj).A + "/" + ((RepObj)obj).B + ":" + methodId);
        }

        private static PMReplicationChannel NewSender(PMRepOptions options)
        {
            PMRepOptions o = options ?? new PMRepOptions();
            return _senderFactory == null ? new PMReplicationChannel(o) : _senderFactory(o);
        }

        /// <summary>
        /// 造接收侧通道。
        ///
        /// 刻意也走 `_senderFactory`：缺陷注入子类必须能作用于**接收路径**，
        /// 否则 `DispatchOnRepPerProperty` / `IsolateOnRepExceptions` 这两处
        /// （只在 OnMessage 里生效）的断言就无法被负向验证 —— 一组没被注入验证过的断言，
        /// 与没写断言没有区别。干净实现的 `_senderFactory == null` ⇒ 与
        /// `new PMReplicationChannel()` 完全等价（`PMRepOptions.Default()` 就是 `new PMRepOptions()`）。
        /// </summary>
        private static PMReplicationChannel NewChannel()
        {
            return NewSender(null);
        }

        /// <summary>
        /// 把某条连接"待发"的载荷投递给它的客户端副本，并把客户端产生的确认回灌给发送侧。
        ///
        /// 注意：投递时传的连接对象是**服务端视角的那一条**（`Client.Conn`）——
        /// 在真实形态下，客户端的上行确认同样是落在服务端那条连接上被处理的。
        /// </summary>
        private static int Deliver(Rig rig, Rig.Client client)
        {
            int applied = 0;
            List<byte[]> sent = client.Conn.Sent;

            for (int i = 0; i < sent.Count; i++)
            {
                applied += client.Receiver.OnMessage(client.Conn, sent[i], 0, sent[i].Length);
            }

            sent.Clear();

            byte[] ack = client.Receiver.BuildAckMessage(client.Conn);
            if (ack != null)
            {
                rig.Sender.OnMessage(client.Conn, ack, 0, ack.Length);
            }

            return applied;
        }

        private static int DeliverAll(Rig rig)
        {
            int applied = 0;
            for (int i = 0; i < rig.Clients.Count; i++)
            {
                applied += Deliver(rig, rig.Clients[i]);
            }

            return applied;
        }

        private static void TickAndDeliver(Rig rig, Rig.Client client)
        {
            rig.Sender.Tick();
            Deliver(rig, client);
        }

        private static void TickAndDeliverAll(Rig rig)
        {
            rig.Sender.Tick();
            DeliverAll(rig);
        }

        private static byte[] MakeAckMessage(uint netId, long version)
        {
            List<PMRepAck> acks = new List<PMRepAck>(1);
            acks.Add(new PMRepAck(netId, version));

            PMNetWriter w = new PMNetWriter(32);
            PMReplicationWriter.WriteAck(w, acks);
            return w.ToArray();
        }

        private static PMRepMessage Decode(byte[] payload, string context)
        {
            PMRepMessage msg = new PMRepMessage();
            string error;
            if (!PMReplicationReader.TryRead(new PMNetReader(payload), msg, out error))
            {
                _failures.Add(context + "：解码载荷失败 " + error);
                return null;
            }

            return msg;
        }

        /// <summary>某连接已发出的载荷里出现过的全部槽位（去重、升序）。</summary>
        private static List<int> SentSlots(TestConn conn, string context)
        {
            List<int> all = new List<int>();
            for (int i = 0; i < conn.Sent.Count; i++)
            {
                PMRepMessage msg = Decode(conn.Sent[i], context);
                if (msg == null)
                {
                    continue;
                }

                for (int r = 0; r < msg.Updates.Count; r++)
                {
                    for (int k = 0; k < msg.Updates[r].Slots.Length; k++)
                    {
                        int slot = msg.Updates[r].Slots[k];
                        if (!all.Contains(slot))
                        {
                            all.Add(slot);
                        }
                    }
                }
            }

            all.Sort();
            return all;
        }

        private static string SlotsToString(List<int> slots)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < slots.Count; i++)
            {
                if (i > 0) { sb.Append(','); }
                sb.Append(slots[i]);
            }

            return "[" + sb + "]";
        }

        // ==================================================================================
        //  A. 线格式
        // ==================================================================================

        private static void TestCodec()
        {
            uint netId = 42u;
            int[] slots = new int[] { 0, 2, 3 };
            ushort[] ids = new ushort[] { 100, 102, 103 };
            byte[][] values = new byte[3][];

            PMNetWriter vw = new PMNetWriter(32);
            vw.Reset();
            vw.WriteInt32(-7);
            values[0] = vw.ToArray();
            vw.Reset();
            vw.WriteFloat(1.5f);
            values[1] = vw.ToArray();
            vw.Reset();
            vw.WriteStringValue("abc");
            values[2] = vw.ToArray();

            List<PMRepUpdateRecord> records = new List<PMRepUpdateRecord>(1);
            records.Add(new PMRepUpdateRecord(netId, 9L, slots, ids, values));

            PMNetWriter w = new PMNetWriter(256);
            PMReplicationWriter.WriteUpdate(w, records);
            byte[] payload = w.ToArray();

            PMRepMessage msg = new PMRepMessage();
            string error;
            bool ok = PMReplicationReader.TryRead(new PMNetReader(payload), msg, out error);
            Check(ok, "A1 更新消息可往返解析（" + (error ?? "ok") + "）");
            if (!ok)
            {
                return;
            }

            Check(msg.Kind == PMRepMessageKind.Update, "A2 消息类型 = Update");
            Check(msg.Updates.Count == 1, "A3 记录数 = 1");
            if (msg.Updates.Count != 1)
            {
                return;
            }

            PMRepUpdateRecord r = msg.Updates[0];
            Check(r.NetId == netId, "A4 NetId 保真");
            Check(r.Version == 9L, "A5 版本保真");
            Check(r.Slots.Length == 3 && r.Slots[0] == 0 && r.Slots[1] == 2 && r.Slots[2] == 3,
                "A6 掩码槽位保真 " + SlotsToString(new List<int>(r.Slots)));
            Check(r.PropertyIds[0] == 100 && r.PropertyIds[2] == 103, "A7 属性 ID 保真");
            Check(new PMNetReader(r.Values[0]).ReadInt32() == -7, "A8 int32 值往返");
            Check(Math.Abs(new PMNetReader(r.Values[1]).ReadFloat() - 1.5f) < 1e-6f, "A9 float 值往返");
            Check(new PMNetReader(r.Values[2]).ReadStringValue() == "abc", "A10 string 值往返");

            // 协议不兼容 ⇒ 整条丢弃（D-R0-46 的运行期兜底）
            byte[] badMagic = (byte[])payload.Clone();
            badMagic[0] = 0x00;
            Check(!PMReplicationReader.TryRead(new PMNetReader(badMagic), msg, out error), "A11 魔数不符被拒绝");

            PMNetWriter badVersion = new PMNetWriter(32);
            badVersion.WriteVarint(PMRepProtocol.Magic);
            badVersion.WriteVarint(PMRepProtocol.Version + 1);
            badVersion.WriteVarint((ulong)PMRepMessageKind.Update);
            badVersion.WriteVarint(0UL);
            Check(!PMReplicationReader.TryRead(new PMNetReader(badVersion.ToArray()), msg, out error), "A12 协议版本不符被拒绝");

            bool truncated = PMReplicationReader.TryRead(new PMNetReader(payload, 0, payload.Length - 1), msg, out error);
            Check(!truncated, "A13 截断载荷被拒绝（" + (error ?? "") + "）");

            PMNetWriter huge = new PMNetWriter(32);
            huge.WriteVarint(PMRepProtocol.Magic);
            huge.WriteVarint(PMRepProtocol.Version);
            huge.WriteVarint((ulong)PMRepMessageKind.Update);
            huge.WriteVarint((ulong)(PMRepProtocol.MaxUpdatesPerMessage + 1));
            Check(!PMReplicationReader.TryRead(new PMNetReader(huge.ToArray()), msg, out error), "A14 记录数超上限被拒绝（有界）");

            List<PMRepUpdateRecord> empty = new List<PMRepUpdateRecord>(1);
            empty.Add(new PMRepUpdateRecord(netId, 1L, new int[0], new ushort[0], new byte[0][]));
            PMNetWriter ew = new PMNetWriter(64);
            PMReplicationWriter.WriteUpdate(ew, empty);
            Check(!PMReplicationReader.TryRead(new PMNetReader(ew.ToArray()), msg, out error), "A15 空掩码记录被拒绝（D-R0-13：空更新不应上线）");

            // Ack 往返
            byte[] ackPayload = MakeAckMessage(netId, 5L);
            ok = PMReplicationReader.TryRead(new PMNetReader(ackPayload), msg, out error);
            Check(ok && msg.Kind == PMRepMessageKind.Ack && msg.Acks.Count == 1
                  && msg.Acks[0].NetId == netId && msg.Acks[0].Version == 5L, "A16 Ack 消息往返");
        }

        // ==================================================================================
        //  B. 每连接基线 / ACK 只能前进
        // ==================================================================================

        private static void TestBaselineAndAck()
        {
            Rig rig = Rig.Create(0x1001u, 1, null, null, null);
            Rig.Client c = rig.Clients[0];
            uint netId = rig.ServerObj.NetId.Value;
            long version;

            rig.ServerObj.A = 10;
            rig.ServerObj.MarkPropertyDirty(0);
            TickAndDeliver(rig, c);
            Check(c.Obj.A == 10, "B1 首次投递（基线缺失 ⇒ 全量）送达，客户端 A=10");

            bool got = rig.Sender.TryGetAckedVersion(c.Conn, netId, out version);
            Check(got && version == 1L, "B2 确认版本 = 1（got=" + got + " version=" + version + "）");
            Check(rig.Sender.GetInflightCount(c.Conn, netId) == 0, "B3 确认后无在途记录");

            rig.ServerObj.A = 20;
            rig.ServerObj.MarkPropertyDirty(0);
            TickAndDeliver(rig, c);
            Check(c.Obj.A == 20, "B4 第二次投递送达，客户端 A=20");
            got = rig.Sender.TryGetAckedVersion(c.Conn, netId, out version);
            Check(got && version == 2L, "B5a 确认版本推进到 2");

            // 旧 ACK（v1）不得回退已确认版本
            byte[] oldAck = MakeAckMessage(netId, 1L);
            rig.Sender.OnMessage(c.Conn, oldAck, 0, oldAck.Length);
            got = rig.Sender.TryGetAckedVersion(c.Conn, netId, out version);
            Check(got && version == 2L, "B5 旧 ACK 不回退已确认版本（仍为 2，实际 " + version + "）");
            Check(rig.Sender.Stats.StaleAckIgnored >= 1, "B6 旧 ACK 被计入 stale（" + rig.Sender.Stats.StaleAckIgnored + "）");

            // 旧 ACK 不得清除比它更新的脏位
            rig.ServerObj.A = 30;
            rig.ServerObj.MarkPropertyDirty(0);
            Check(rig.ServerObj.Dirty.IsDirty(0), "B7 修改后脏位已置");
            rig.Sender.OnMessage(c.Conn, oldAck, 0, oldAck.Length);
            Check(rig.ServerObj.Dirty.IsDirty(0), "B8 旧 ACK 不清除新脏位");

            TickAndDeliver(rig, c);
            Check(c.Obj.A == 30, "B9 未确认的修改仍然被送达（客户端 A=30）");
        }

        // ==================================================================================
        //  C. 值未变不发
        // ==================================================================================

        private static void TestUnchangedSuppression()
        {
            Rig rig = Rig.Create(0x1002u, 1, null, null, null);
            Rig.Client c = rig.Clients[0];

            // 先建立完整基线：4 个槽位全部确认一次
            rig.ServerObj.A = 10;
            rig.ServerObj.B = 0;
            rig.ServerObj.C = 0f;
            rig.ServerObj.S = string.Empty;
            rig.MarkAllDirty();
            TickAndDeliverAll(rig);

            Check(c.Obj.A == 10, "C0 建立完整基线后客户端 A=10");
            int sentAfterBaseline = c.Conn.Sent.Count;
            Check(sentAfterBaseline == 0, "C1 基线建立后待发载荷为空（实际 " + sentAfterBaseline + "）");

            // 只标脏、不改值
            rig.ServerObj.MarkPropertyDirty(0);
            rig.Sender.Tick();
            Check(c.Conn.Sent.Count == 0, "C2 标脏但值未变 ⇒ 不发数据（实际待发 " + c.Conn.Sent.Count + "）");
            Check(rig.Sender.Stats.SuppressedUnchanged >= 1, "C3 值未变的抑制被计数（" + rig.Sender.Stats.SuppressedUnchanged + "）");
            Check(!rig.ServerObj.Dirty.IsDirty(0), "C4 追平后脏位被清（对象不再每轮空扫）");

            // 正常修改仍然工作
            rig.ServerObj.A = 11;
            rig.ServerObj.MarkPropertyDirty(0);
            TickAndDeliver(rig, c);
            Check(c.Obj.A == 11, "C5 真实变化仍会送达（客户端 A=11）");
        }

        // ==================================================================================
        //  D. 8 项复制条件 × 角色组合
        // ==================================================================================

        private struct ConditionRow
        {
            public PMCond Cond;
            public PMCond DynamicResolved;
            public bool CustomActive;
            public bool ExpectAuthority;
            public bool ExpectAutonomous;
            public bool ExpectSimulated;

            public ConditionRow(PMCond cond, PMCond dynamicResolved, bool customActive, bool auth, bool auto, bool sim)
            {
                Cond = cond;
                DynamicResolved = dynamicResolved;
                CustomActive = customActive;
                ExpectAuthority = auth;
                ExpectAutonomous = auto;
                ExpectSimulated = sim;
            }
        }

        private static readonly ConditionRow[] ConditionTable = new ConditionRow[]
        {
            // cond, dynResolved, custom, Authority, Autonomous, Simulated
            new ConditionRow(PMCond.None, PMCond.Dynamic, true, true, true, true),
            new ConditionRow(PMCond.OwnerOnly, PMCond.Dynamic, true, false, true, false),
            new ConditionRow(PMCond.SkipOwner, PMCond.Dynamic, true, true, false, true),
            new ConditionRow(PMCond.SimulatedOnly, PMCond.Dynamic, true, false, false, true),
            new ConditionRow(PMCond.AutonomousOnly, PMCond.Dynamic, true, true, true, false),
            new ConditionRow(PMCond.Custom, PMCond.Dynamic, true, true, true, true),
            new ConditionRow(PMCond.Custom, PMCond.Dynamic, false, false, false, false),
            new ConditionRow(PMCond.Dynamic, PMCond.None, true, true, true, true),
            new ConditionRow(PMCond.Dynamic, PMCond.Never, true, false, false, false),
            new ConditionRow(PMCond.Dynamic, PMCond.OwnerOnly, true, false, true, false),
            new ConditionRow(PMCond.Never, PMCond.Dynamic, true, false, false, false),
        };

        private static void TestConditionTable()
        {
            // D1..D33：表驱动（8 项 × 3 角色，外加 Custom 关闭与 Dynamic 改写）
            int row = 0;
            for (int i = 0; i < ConditionTable.Length; i++)
            {
                ConditionRow r = ConditionTable[i];
                row++;

                bool auth = PMRepConditions.Evaluate(r.Cond, false, false, r.CustomActive, r.DynamicResolved);
                bool auto = PMRepConditions.Evaluate(r.Cond, true, false, r.CustomActive, r.DynamicResolved);
                bool sim = PMRepConditions.Evaluate(r.Cond, false, true, r.CustomActive, r.DynamicResolved);

                string name = "D" + row + " " + r.Cond + "（dyn=" + r.DynamicResolved + ", custom=" + r.CustomActive + "）";
                Check(auth == r.ExpectAuthority, name + " × 权威 = " + r.ExpectAuthority);
                Check(auto == r.ExpectAutonomous, name + " × 自治 = " + r.ExpectAutonomous);
                Check(sim == r.ExpectSimulated, name + " × 模拟 = " + r.ExpectSimulated);
            }

            // D-cross：与冻结的 PMNetObject.ShouldReplicateProperty 交叉校验
            // （自治/模拟两行必须与冻结实现一致；权威行是冻结实现未覆盖的语义）
            PMCond[] supported = new PMCond[]
            {
                PMCond.None, PMCond.OwnerOnly, PMCond.SkipOwner, PMCond.SimulatedOnly,
                PMCond.AutonomousOnly, PMCond.Custom, PMCond.Dynamic, PMCond.Never,
            };

            TestConn ownerConn = new TestConn(9001, true);
            TestConn otherConn = new TestConn(9002, true);

            for (int i = 0; i < supported.Length; i++)
            {
                CondObj obj = new CondObj();
                obj.Conds[0] = supported[i];
                obj.OwnerConnection = ownerConn;

                bool frozenOwner = obj.ShouldReplicateProperty(0, ownerConn);
                bool frozenOther = obj.ShouldReplicateProperty(0, otherConn);

                bool expectOwner = PMRepConditions.Evaluate(supported[i], true, false, true, PMCond.Dynamic);
                bool expectOther = PMRepConditions.Evaluate(supported[i], false, true, true, PMCond.Dynamic);

                Check(frozenOwner == expectOwner,
                    "D-cross " + supported[i] + " 自治行与冻结 ShouldReplicateProperty 一致（冻结=" + frozenOwner + "）");
                Check(frozenOther == expectOther,
                    "D-cross " + supported[i] + " 模拟行与冻结 ShouldReplicateProperty 一致（冻结=" + frozenOther + "）");
            }

            // D-int：OwnerOnly 真的只发给拥有者（端到端）
            PMCond[] conds = new PMCond[] { PMCond.None, PMCond.OwnerOnly, PMCond.None, PMCond.None };
            Rig rig = Rig.Create(0x1003u, 2, conds, null, null);
            rig.Sender.ViewRoleResolver = delegate (PMNetObject o, PMNetConnection conn)
            {
                return ReferenceEquals(conn, rig.Clients[0].Conn) ? PMRepViewRole.Autonomous : PMRepViewRole.Simulated;
            };

            rig.ServerObj.A = 77;
            rig.ServerObj.B = 88;
            rig.MarkAllDirty();
            TickAndDeliverAll(rig);

            Check(rig.Clients[0].Obj.A == 77 && rig.Clients[1].Obj.A == 77, "D-int 无条件属性对两条连接都可见");
            Check(rig.Clients[0].Obj.B == 88, "D-int OwnerOnly 属性发给了拥有者连接");
            Check(rig.Clients[1].Obj.B == 0, "D-int OwnerOnly 属性没有发给非拥有者连接");
            Check(rig.Sender.Stats.ConditionFiltered >= 1, "D-int 条件过滤被计数（" + rig.Sender.Stats.ConditionFiltered + "）");
        }

        // ==================================================================================
        //  E. 条件跃迁
        // ==================================================================================

        private static void TestConditionTransition()
        {
            PMCond[] conds = new PMCond[] { PMCond.None, PMCond.OwnerOnly, PMCond.None, PMCond.None };
            Rig rig = Rig.Create(0x1004u, 1, conds, null, null);
            Rig.Client c = rig.Clients[0];
            uint netId = rig.ServerObj.NetId.Value;

            PMRepViewRole role = PMRepViewRole.Autonomous;
            rig.Sender.ViewRoleResolver = delegate (PMNetObject o, PMNetConnection conn) { return role; };

            // 1) 自治视角：两个属性都可见，建立基线
            rig.ServerObj.A = 1;
            rig.ServerObj.B = 5;
            rig.MarkAllDirty();
            TickAndDeliver(rig, c);

            Check(c.Obj.A == 1 && c.Obj.B == 5, "E1 自治视角下两个属性都送达");
            bool active;
            Check(rig.Sender.TryGetConditionActive(c.Conn, netId, 1, out active) && active, "E2 条件状态记录为满足");

            // 2) 切到模拟视角：OwnerOnly 不满足。改另一个属性触发一次扫描，
            //    让复制层把"槽位 1 条件不满足"记下来。
            //    同时把槽位 1 也标脏（但**不改值**）—— 这一步刻意不改 B：
            //    如果改了值，后续"基线比较"自己就能发现差异，跃迁补偿就不可观测了。
            role = PMRepViewRole.Simulated;
            rig.ServerObj.A = 2;
            rig.ServerObj.MarkPropertyDirty(0);
            rig.ServerObj.MarkPropertyDirty(1);
            TickAndDeliver(rig, c);

            Check(c.Obj.A == 2, "E3 模拟视角下无条件属性仍可见");
            Check(rig.Sender.TryGetConditionActive(c.Conn, netId, 1, out active) && !active, "E4 条件状态记录为不满足");
            Check(rig.Sender.Stats.ConditionFiltered >= 1, "E5a 条件不满足的属性被过滤（" + rig.Sender.Stats.ConditionFiltered + "）");
            Check(!rig.ServerObj.Dirty.IsDirty(1), "E5 条件不满足的连接不阻塞脏位清理（值不丢，靠跃迁补发）");

            // 3) 条件由不满足 → 满足：必须把该属性全部掩码位置脏（D-R0-15）。
            //    **即使值相对基线没有变化**也要补发一次当前值 —— 这正是 D-R0-15 规定的行为：
            //    "新获得可见性的连接要拿到当前值"，而不是依赖"值变了才发"。
            role = PMRepViewRole.Autonomous;
            rig.Sender.Tick();

            Check(c.Conn.Sent.Count >= 1, "E6 条件跃迁后必须有载荷发出（实际 " + c.Conn.Sent.Count + "）");

            List<int> slots = SentSlots(c.Conn, "E6");
            Check(slots.Contains(1), "E7 跃迁补偿的掩码包含该属性（实际 " + SlotsToString(slots) + "）");
            Check(rig.Sender.Stats.TransitionForceSends >= 1, "E8 跃迁补发被计数（" + rig.Sender.Stats.TransitionForceSends + "）");

            Deliver(rig, c);
            Check(c.Obj.B == 5, "E9 跃迁后客户端持有当前值（B=5）");
        }

        // ==================================================================================
        //  F. 初始状态
        // ==================================================================================

        private static void TestInitialState()
        {
            Rig rig = Rig.Create(0x1005u, 1, null, null, null);
            Rig.Client c0 = rig.Clients[0];

            // 注意：**一个脏位都没标**。首次进入范围/基线缺失本身就要求全量（D-R0-12）。
            rig.ServerObj.A = 1;
            rig.ServerObj.B = 2;
            rig.ServerObj.C = 3.5f;
            rig.ServerObj.S = "hi";
            rig.Sender.Tick();

            List<int> slots = SentSlots(c0.Conn, "F1");
            Check(slots.Count == 4, "F1 基线缺失 ⇒ 全量发送全部 4 个属性（实际 " + SlotsToString(slots) + "）");
            Check(rig.Sender.Stats.InitialFullSends >= 1, "F2 初始全量被计数（" + rig.Sender.Stats.InitialFullSends + "）");

            Deliver(rig, c0);
            Check(c0.Obj.A == 1 && c0.Obj.B == 2 && Math.Abs(c0.Obj.C - 3.5f) < 1e-6f && c0.Obj.S == "hi",
                "F3 客户端拿到完整初始状态");

            // 新连接中途加入 ⇒ 同样必须全量补齐（D-R0-16 的"新进入者能补齐"）
            Rig.Client c1 = rig.AddClient();
            rig.Sender.Tick();

            Check(c0.Conn.Sent.Count == 0, "F4 已追平的连接不再收到任何数据（实际 " + c0.Conn.Sent.Count + "）");
            List<int> newSlots = SentSlots(c1.Conn, "F5");
            Check(newSlots.Count == 4, "F5 新连接首次进入范围 ⇒ 全量（实际 " + SlotsToString(newSlots) + "）");

            Deliver(rig, c1);
            Check(c1.Obj.A == 1 && c1.Obj.B == 2 && Math.Abs(c1.Obj.C - 3.5f) < 1e-6f && c1.Obj.S == "hi",
                "F6 新连接补到完整当前值");
        }

        // ==================================================================================
        //  G. OnRep
        // ==================================================================================

        private static void TestOnRep()
        {
            ushort[] onRepIds = new ushort[] { 1, 2, 0, 0 };
            Rig rig = Rig.Create(0x1006u, 1, null, onRepIds, null);
            Rig.Client c = rig.Clients[0];

            _onRepLog.Clear();

            // 先建立完整基线（初始全量也会触发 OnRep，这是对的：收到值就该通知）
            rig.ServerObj.A = 1;
            rig.ServerObj.B = 1;
            rig.MarkAllDirty();
            TickAndDeliverAll(rig);

            _onRepLog.Clear();
            rig.ServerObj.A = 10;
            rig.ServerObj.MarkPropertyDirty(0);
            TickAndDeliver(rig, c);
            Check(_onRepLog.Count == 1, "G1 属性 A 变化触发 1 次 OnRep（实际 " + _onRepLog.Count + "）");
            Check(_onRepLog.Count > 0 && _onRepLog[0].EndsWith(":1"), "G2 OnRep 携带声明的方法 ID=1");

            _onRepLog.Clear();
            rig.ServerObj.C = 4.25f;
            rig.ServerObj.MarkPropertyDirty(2);
            TickAndDeliver(rig, c);
            Check(_onRepLog.Count == 0, "G3 OnRepMethodId=0 的属性不触发分发（实际 " + _onRepLog.Count + "）");

            Check(c.Receiver.Stats.OnRepDispatched >= 1, "G4 接收侧统计到 OnRep 分发（" + c.Receiver.Stats.OnRepDispatched + "）");
            Check(rig.Sender.Stats.OnRepDispatched == 0, "G5 发送侧不分发 OnRep");
            Check(c.Receiver.Stats.OnRepUnhandled == 0, "G6 没有未接线的 OnRep（" + c.Receiver.Stats.OnRepUnhandled + "）");
        }

        // ==================================================================================
        //  H. 有界
        // ==================================================================================

        private static void TestBounds()
        {
            // H1 单包属性数上限
            PMRepOptions h1 = new PMRepOptions();
            h1.MaxPropertiesPerUpdate = 2;
            Rig rig = Rig.Create(0x1007u, 1, null, null, h1);
            Rig.Client c = rig.Clients[0];

            rig.ServerObj.A = 1;
            rig.ServerObj.B = 2;
            rig.ServerObj.C = 3f;
            rig.ServerObj.S = "s";
            rig.MarkAllDirty();
            rig.Sender.Tick();

            Check(c.Conn.Sent.Count >= 2, "H1a 属性数超上限被拆成多个载荷（实际 " + c.Conn.Sent.Count + "）");
            int worst = 0;
            List<int> union = new List<int>();
            for (int i = 0; i < c.Conn.Sent.Count; i++)
            {
                PMRepMessage msg = Decode(c.Conn.Sent[i], "H1");
                if (msg == null) { continue; }
                int props = 0;
                for (int r = 0; r < msg.Updates.Count; r++)
                {
                    props += msg.Updates[r].Slots.Length;
                    for (int k = 0; k < msg.Updates[r].Slots.Length; k++)
                    {
                        if (!union.Contains(msg.Updates[r].Slots[k])) { union.Add(msg.Updates[r].Slots[k]); }
                    }
                }

                if (props > worst) { worst = props; }
            }

            Check(worst <= 2, "H1b 单个载荷的属性数不超过上限（实际最大 " + worst + "）");
            Check(union.Count == 4, "H1c 拆分不丢属性（实际 " + SlotsToString(union) + "）");

            // H2 每对象在途上限（顺延而不是丢弃）
            PMRepOptions h2 = new PMRepOptions();
            h2.MaxInflightPerObject = 2;
            Rig rig2 = Rig.Create(0x1008u, 1, null, null, h2);
            Rig.Client c2 = rig2.Clients[0];
            uint netId = rig2.ServerObj.NetId.Value;

            rig2.ServerObj.A = 1;
            rig2.ServerObj.MarkPropertyDirty(0);
            rig2.Sender.Tick();
            rig2.ServerObj.A = 2;
            rig2.ServerObj.MarkPropertyDirty(0);
            rig2.Sender.Tick();

            Check(rig2.Sender.GetInflightCount(c2.Conn, netId) == 2, "H2a 在途记录达到上限 2");

            rig2.ServerObj.A = 3;
            rig2.ServerObj.MarkPropertyDirty(0);
            rig2.Sender.Tick();
            Check(rig2.Sender.GetInflightCount(c2.Conn, netId) <= 2, "H2b 在途记录不超过上限（实际 "
                  + rig2.Sender.GetInflightCount(c2.Conn, netId) + "）");
            Check(rig2.Sender.Stats.DeferredByInflight >= 1, "H2c 超限被顺延并计数（" + rig2.Sender.Stats.DeferredByInflight + "）");

            // H2e..H2g 丢包通知：去掉一条在途记录 ⇒ 容量归还，未确认的值会重发
            long[] versions = rig2.Sender.GetInflightVersions(c2.Conn, netId);
            Check(versions.Length == 2, "H2e 在途版本可查询（" + versions.Length + " 条）");

            if (versions.Length >= 2)
            {
                long before = rig2.Sender.Stats.LossReports;
                bool removed = rig2.Sender.OnLoss(c2.Conn, netId, versions[0]);
                Check(removed && rig2.Sender.Stats.LossReports == before + 1, "H2f 丢包通知被处理并计数");
                Check(rig2.Sender.GetInflightCount(c2.Conn, netId) == versions.Length - 1, "H2g 丢包记录被移除（容量归还）");

                rig2.Sender.Tick();
                Check(rig2.Sender.GetInflightCount(c2.Conn, netId) == versions.Length, "H2h 腾出的容量被重新使用（未确认的值重发）");
            }

            Deliver(rig2, c2);
            rig2.Sender.Tick();
            Deliver(rig2, c2);
            Check(c2.Obj.A == 3, "H2d 被顺延的修改最终仍然送达（不丢），客户端 A=" + c2.Obj.A);

            // H3 每帧每连接对象预算（顺延而不是丢弃）
            PMRepOptions h3 = new PMRepOptions();
            h3.MaxObjectsPerConnectionPerTick = 1;
            Rig rig3 = Rig.Create(0x1009u, 1, null, null, h3);
            Rig.Client c3 = rig3.Clients[0];

            RepObj second = new RepObj();
            rig3.ServerWorld.Spawn(second, rig3.ClassId);
            rig3.Sender.RegisterObject(second);

            // 第二个对象的创建记录也要投递过去（M04 的生命周期路径）
            byte[] secondLifecycle = rig3.ServerWorld.BuildLifecycleBatch(c3.Conn);
            if (secondLifecycle != null)
            {
                c3.World.OnLifecycleMessage(secondLifecycle, 0, secondLifecycle.Length);
            }

            rig3.ServerObj.A = 1;
            rig3.ServerObj.MarkPropertyDirty(0);
            second.A = 2;
            second.MarkPropertyDirty(0);

            rig3.Sender.Tick();
            Check(rig3.Sender.Stats.DeferredByBudget >= 1, "H3a 超出每帧对象预算被顺延并计数（"
                  + rig3.Sender.Stats.DeferredByBudget + "）");

            Deliver(rig3, c3);

            // 每个对象各有 4 个槽位要补齐，预算为 1 ⇒ 需要多轮才能排空（顺延不丢）
            for (int round = 0; round < 8; round++)
            {
                rig3.Sender.Tick();
                Deliver(rig3, c3);
            }

            PMNetObject found;
            bool foundSecond = c3.World.TryFind(second.NetId, out found);
            RepObj secondCopy = foundSecond ? (RepObj)found : null;

            Check(c3.Obj.A == 1, "H3b 第一个对象送达（A=" + c3.Obj.A + "）");
            Check(secondCopy != null && secondCopy.A == 2, "H3c 被顺延的第二个对象在后续帧送达（不丢）");
        }

        // ==================================================================================
        //  I. 端到端收敛
        // ==================================================================================

        private static void TestConvergence()
        {
            Rig rig = Rig.Create(0x100Au, 2, null, null, null);
            Rig.Client fast = rig.Clients[0];
            Rig.Client slow = rig.Clients[1];

            rig.ServerObj.A = 1;
            rig.ServerObj.B = 1;
            rig.ServerObj.C = 1f;
            rig.ServerObj.S = "v1";
            rig.MarkAllDirty();
            TickAndDeliverAll(rig);

            Check(fast.Obj.A == 1 && slow.Obj.A == 1, "I1 两条连接初始收敛");

            // 只服务 fast：slow 的基线落后多版
            for (int i = 2; i <= 6; i++)
            {
                rig.ServerObj.A = i * 10;
                rig.ServerObj.B = i;
                rig.ServerObj.C = i * 1.5f;
                rig.ServerObj.S = "v" + i;
                rig.ServerObj.MarkPropertyDirty(0);
                rig.ServerObj.MarkPropertyDirty(1);
                rig.ServerObj.MarkPropertyDirty(2);
                rig.ServerObj.MarkPropertyDirty(3);
                rig.Sender.Tick();
                Deliver(rig, fast);
            }

            Check(fast.Obj.A == 60 && slow.Obj.A == 1, "I2 fast 已追到最新，slow 仍停在旧基线（"
                  + fast.Obj.A + " / " + slow.Obj.A + "）");
            Check(slow.Conn.Sent.Count >= 5, "I3 slow 的落后更新仍在队列里（" + slow.Conn.Sent.Count + " 个载荷）");

            Deliver(rig, slow);

            Check(slow.Obj.A == 60, "I4 slow 最终收敛到 A=" + slow.Obj.A);
            Check(slow.Obj.A == fast.Obj.A && slow.Obj.B == fast.Obj.B, "I5 两条连接最终一致（int 属性）");
            Check(Math.Abs(slow.Obj.C - fast.Obj.C) < 1e-6f && slow.Obj.S == fast.Obj.S, "I6 两条连接最终一致（float/string 属性）");
            Check(slow.Obj.A == rig.ServerObj.A && slow.Obj.S == rig.ServerObj.S, "I7 与权威值一致");
            Check(rig.Sender.Stats.DeferredByInflight == 0, "I8 落后连接没有触发在途上限顺延（"
                  + rig.Sender.Stats.DeferredByInflight + "）");
        }

        // ==================================================================================
        //  J..N：RV1 / RV2 / RV4 的边界覆盖
        //
        //  这一组测试针对的是"负向验证盖不到的地方"：编解码层、掩码层、世界记账层。
        //  既有负向验证只注入四个 protected virtual 决策点，结构上不可能覆盖到
        //  `PMRepMask` 的字节/位映射与 `ApplyRecord` 的写入顺序。
        // ==================================================================================

        /// <summary>宽对象：N 个 int 槽位（覆盖槽位 ≥ 8 / ≥ 64 的掩码映射）。</summary>
        internal sealed class WideObj : PMNetObject
        {
            public int[] Values = new int[0];
        }

        private static PMReplicationDescriptor MakeWideDescriptor(uint classId, int slotCount)
        {
            PMPropertyDescriptor[] props = new PMPropertyDescriptor[slotCount];
            for (int i = 0; i < slotCount; i++)
            {
                int captured = i;
                props[i].PropertyId = (ushort)(1000 + i);
                props[i].Condition = PMCond.None;
                props[i].MaskOffset = (ushort)i;
                props[i].MaskBitCount = 1;
                props[i].OnRepMethodId = 0;
                props[i].PushBased = true;
                props[i].MemberName = "V" + i;
                props[i].SetterName = "PMNet_SetV" + i;
                props[i].Writer = delegate (PMNetObject t, PMNetWriter w) { w.WriteInt32(((WideObj)t).Values[captured]); };
                props[i].Reader = delegate (PMNetObject t, PMNetReader r) { ((WideObj)t).Values[captured] = r.ReadInt32(); };
            }

            PMReplicationDescriptor desc = new PMReplicationDescriptor();
            desc.ClassId = classId;
            desc.Properties = props;
            desc.ChangeMaskBitCount = slotCount;
            desc.HasConditionalMask = false;
            desc.ProtocolHash = PMStableHash.ClassProtocolHash(classId, props);
            desc.TypeName = "WideObj";
            return desc;
        }

        private static Func<PMNetObject> MakeWideFactory(int slotCount)
        {
            return delegate ()
            {
                WideObj o = new WideObj();
                o.Values = new int[slotCount];
                return o;
            };
        }

        private sealed class WideRig
        {
            public uint ClassId;
            public int SlotCount;
            public PMNetWorld ServerWorld;
            public PMReplicationChannel Sender;
            public WideObj ServerObj;
            public PMNetWorld ClientWorld;
            public TestConn Conn;
            public PMReplicationChannel Receiver;
            public WideObj ClientObj;
        }

        /// <summary>造一条"服务端权威 + 一个客户端副本"的宽对象链路（副本由生命周期消息创建）。</summary>
        private static WideRig CreateWideRig(uint classId, int slotCount)
        {
            PMNetRegistry.Reset();

            PMReplicationDescriptor desc = MakeWideDescriptor(classId, slotCount);
            PMNetClassEntry entry = new PMNetClassEntry();
            entry.ClassId = classId;
            entry.TypeName = "WideObj";
            entry.Rep = desc;
            entry.Factory = MakeWideFactory(slotCount);
            PMNetRegistry.RegisterClass(entry);

            WideRig rig = new WideRig();
            rig.ClassId = classId;
            rig.SlotCount = slotCount;

            rig.ServerWorld = new PMNetWorld(new PMSession(9u, true));
            rig.ServerWorld.RegisterClass(classId, MakeWideFactory(slotCount));

            rig.ServerObj = new WideObj();
            rig.ServerObj.Values = new int[slotCount];
            if (!rig.ServerWorld.Spawn(rig.ServerObj, classId))
            {
                throw new InvalidOperationException("宽对象的 Spawn 失败");
            }

            rig.Sender = NewSender(null);
            rig.Sender.World = rig.ServerWorld;
            rig.Sender.RegisterObject(rig.ServerObj);

            rig.ClientWorld = new PMNetWorld(new PMSession(9u, false));
            rig.ClientWorld.RegisterClass(classId, MakeWideFactory(slotCount));
            rig.Conn = new TestConn(2000, true);

            rig.ServerWorld.AddConnection(rig.Conn);
            rig.Sender.AddConnection(rig.Conn);

            byte[] lifecycle = rig.ServerWorld.BuildLifecycleBatch(rig.Conn);
            if (lifecycle == null)
            {
                throw new InvalidOperationException("宽对象没有产生 Create 记录");
            }

            rig.ClientWorld.OnLifecycleMessage(lifecycle, 0, lifecycle.Length);

            PMNetObject found;
            if (!rig.ClientWorld.TryFind(rig.ServerObj.NetId, out found) || found == null)
            {
                throw new InvalidOperationException("客户端世界没有创建出宽对象副本（生命周期路径有问题）");
            }

            rig.ClientObj = (WideObj)found;
            rig.Receiver = NewChannel();
            rig.Receiver.World = rig.ClientWorld;
            return rig;
        }

        private static void WideTickAndDeliver(WideRig rig)
        {
            rig.Sender.Tick();

            for (int i = 0; i < rig.Conn.Sent.Count; i++)
            {
                rig.Receiver.OnMessage(rig.Conn, rig.Conn.Sent[i], 0, rig.Conn.Sent[i].Length);
            }

            rig.Conn.Sent.Clear();

            byte[] ack = rig.Receiver.BuildAckMessage(rig.Conn);
            if (ack != null)
            {
                rig.Sender.OnMessage(rig.Conn, ack, 0, ack.Length);
            }
        }

        private static byte[] EncodeUpdate(List<PMRepUpdateRecord> records)
        {
            PMNetWriter w = new PMNetWriter(512);
            PMReplicationWriter.WriteUpdate(w, records);
            return w.ToArray();
        }

        private static byte[] EncodeInt32Value(int value)
        {
            PMNetWriter w = new PMNetWriter(16);
            w.WriteInt32(value);
            return w.ToArray();
        }

        private static PMRepUpdateRecord OneRecord(uint netId, long version, int slot, ushort propertyId, byte[] value)
        {
            return new PMRepUpdateRecord(netId, version,
                new int[] { slot }, new ushort[] { propertyId }, new byte[][] { value });
        }

        /// <summary>造一条 Create 生命周期记录（RV2 返工的三个新用例都用它创建副本对象）。</summary>
        private static PMNetLifecycleRecord MakeCreateRecord(uint netId, uint classId)
        {
            PMNetLifecycleRecord rec = new PMNetLifecycleRecord();
            rec.Kind = PMObjectEventKind.Create;
            rec.NetId = new PMNetId(netId, false);
            rec.ClassId = classId;
            return rec;
        }

        /// <summary>把生命周期消息直接投给某个世界（少写几行，语义与既有用例一致）。</summary>
        private static void DeliverLifecycle(PMNetWorld world, params PMNetLifecycleRecord[] records)
        {
            List<PMNetLifecycleRecord> list = new List<PMNetLifecycleRecord>(records);
            PMNetWriter lw = new PMNetWriter(128);
            PMLifecycleCodec.Write(lw, list);
            byte[] payload = lw.ToArray();
            world.OnLifecycleMessage(payload, 0, payload.Length);
        }

        // ------------------------------------------------------------------ J

        private static void TestMaskAndWideSlots()
        {
            // J1..J4 位序：线上第 b 字节承载槽位 8b..8b+7，位 j 落在该字节第 j 位。
            bool mappingOk = true;
            bool isolationOk = true;
            for (int bit = 0; bit < PMRepMask.MaxBits; bit++)
            {
                PMRepMask mask = default(PMRepMask);
                mask.Set(bit);

                int byteIndex = bit >> 3;
                byte expected = (byte)(1 << (bit & 7));

                if (mask.GetByte(byteIndex) != expected || !mask.IsSet(bit) || mask.CountBits() != 1)
                {
                    mappingOk = false;
                }

                for (int b = 0; b < PMRepMask.MaxBytes; b++)
                {
                    if (b != byteIndex && mask.GetByte(b) != 0)
                    {
                        isolationOk = false;
                    }
                }
            }

            Check(mappingOk, "J1 全部 256 个位的 Set/GetByte/IsSet/CountBits 一致（字节下标语义）");
            Check(isolationOk, "J2 置位不污染其他字节（无跨字节错位）");

            // J3 SetByte 是覆盖（重复写入结果与顺序无关）
            {
                PMRepMask mask = default(PMRepMask);
                mask.SetByte(5, 0xFF);
                mask.SetByte(5, 0x0F);
                Check(mask.GetByte(5) == 0x0F && mask.CountBits() == 4,
                    "J3 SetByte 覆盖而非累加（重复/乱序写入不会多出属性位）");

                mask.SetByte(31, 0x80);
                Check(mask.IsSet(255) && mask.GetByte(31) == 0x80, "J4 第 32 字节（槽位 248..255）可寻址");
                Check(!mask.IsSet(256) && mask.GetByte(32) == 0, "J4b 越界字节/位不静默置位");
            }

            // J5 TryLoadBytes 往返 + 越界拒绝
            {
                byte[] raw = new byte[PMRepMask.MaxBytes];
                for (int i = 0; i < raw.Length; i++)
                {
                    raw[i] = (byte)(i * 7 + 1);
                }

                PMRepMask mask = default(PMRepMask);
                Check(mask.TryLoadBytes(raw, 0, raw.Length), "J5 TryLoadBytes 接受 32 字节");

                bool roundTrip = true;
                for (int i = 0; i < raw.Length; i++)
                {
                    if (mask.GetByte(i) != raw[i]) { roundTrip = false; }
                }

                Check(roundTrip, "J5b 32 字节原样往返");
                Check(!default(PMRepMask).TryLoadBytes(raw, 0, PMRepMask.MaxBytes + 1), "J5c 超过 32 字节被拒绝");
            }

            // J6 编解码边界：掩码位数 1/7/8/9/31/32/33/63/64/65/255/256
            int[] widths = new int[] { 1, 7, 8, 9, 31, 32, 33, 63, 64, 65, 255, 256 };
            for (int w = 0; w < widths.Length; w++)
            {
                int highest = widths[w] - 1;
                List<int> slots = new List<int>();
                slots.Add(0);
                if (highest != 0) { slots.Add(highest); }

                int[] slotArray = slots.ToArray();
                ushort[] ids = new ushort[slotArray.Length];
                byte[][] values = new byte[slotArray.Length][];
                for (int k = 0; k < slotArray.Length; k++)
                {
                    ids[k] = (ushort)(100 + slotArray[k]);
                    values[k] = EncodeInt32Value(slotArray[k] * 3 + 1);
                }

                List<PMRepUpdateRecord> records = new List<PMRepUpdateRecord>();
                records.Add(new PMRepUpdateRecord(7u, 3L, slotArray, ids, values));

                byte[] payload = EncodeUpdate(records);
                PMRepMessage msg = new PMRepMessage();
                string error;
                bool ok = PMReplicationReader.TryRead(new PMNetReader(payload), msg, out error);

                bool shapeOk = ok && msg.Updates.Count == 1
                               && msg.Updates[0].Slots.Length == slotArray.Length;
                if (shapeOk)
                {
                    for (int k = 0; k < slotArray.Length; k++)
                    {
                        if (msg.Updates[0].Slots[k] != slotArray[k]
                            || msg.Updates[0].PropertyIds[k] != ids[k]
                            || new PMNetReader(msg.Updates[0].Values[k]).ReadInt32() != slotArray[k] * 3 + 1)
                        {
                            shapeOk = false;
                        }
                    }
                }

                Check(shapeOk, "J6 掩码位宽 " + widths[w] + "（最高槽位 " + highest + "）编解码往返一致"
                      + (shapeOk ? "" : "（err=" + (error ?? "结构不符") + "）"));
            }

            // J7 通道路径：9 属性类（审阅里的最小复现场景）槽位 8 必须真的到达并被 ACK
            {
                WideRig rig = CreateWideRig(0x3001u, 9);
                rig.ServerObj.Values[8] = 4242;
                rig.ServerObj.MarkPropertyDirty(8);
                WideTickAndDeliver(rig);

                Check(rig.ClientObj.Values[8] == 4242,
                    "J7 9 属性类：槽位 8 的值真的到达客户端（实际 " + rig.ClientObj.Values[8] + "）");
                Check(rig.ClientObj.Values[0] == 0, "J7b 没有把槽位 8 错写到其他槽位");

                long acked;
                bool hasAcked = rig.Sender.TryGetAckedVersion(rig.Conn, rig.ServerObj.NetId.Value, out acked);
                Check(hasAcked && acked > 0L, "J7c 槽位 8 的记录被 ACK（水位已推进 " + acked + "）");
                Check(rig.Sender.GetInflightCount(rig.Conn, rig.ServerObj.NetId.Value) == 0,
                    "J7d ACK 后该对象的在途记录已排空");
            }

            // J8 通道路径：256 属性类，逐个覆盖跨字节/跨字边界的槽位
            {
                WideRig rig = CreateWideRig(0x3002u, 256);
                int[] probed = new int[] { 7, 8, 31, 32, 63, 64, 255 };

                for (int i = 0; i < probed.Length; i++)
                {
                    int slot = probed[i];
                    rig.ServerObj.Values[slot] = 1000 + slot;
                    rig.ServerObj.MarkPropertyDirty(slot);
                    WideTickAndDeliver(rig);

                    Check(rig.ClientObj.Values[slot] == 1000 + slot,
                        "J8 256 属性类：槽位 " + slot + " 到达（实际 " + rig.ClientObj.Values[slot] + "）");

                    rig.ServerObj.Values[slot] = 0;
                    rig.ServerObj.MarkPropertyDirty(slot);
                    WideTickAndDeliver(rig);
                    Check(rig.ClientObj.Values[slot] == 0, "J8b 槽位 " + slot + " 的回写也到达（无残留）");
                }

                Check(rig.ClientObj.Values[9] == 0 && rig.ClientObj.Values[254] == 0,
                    "J8c 探测到的槽位之外没有被误写");
            }
        }

        // ------------------------------------------------------------------ K

        private static void TestShapeRejection()
        {
            uint netId = 5u;

            // K1 写侧：槽位乱序
            {
                bool threw = false;
                List<PMRepUpdateRecord> bad = new List<PMRepUpdateRecord>();
                bad.Add(new PMRepUpdateRecord(netId, 1L, new int[] { 2, 1 }, new ushort[] { 102, 101 },
                    new byte[][] { EncodeInt32Value(2), EncodeInt32Value(1) }));
                try { EncodeUpdate(bad); }
                catch (ArgumentException) { threw = true; }
                Check(threw, "K1 写侧拒绝乱序槽位（否则掩码位序与值流不同源）");
            }

            // K2 写侧：槽位重复
            {
                bool threw = false;
                List<PMRepUpdateRecord> bad = new List<PMRepUpdateRecord>();
                bad.Add(new PMRepUpdateRecord(netId, 1L, new int[] { 1, 1 }, new ushort[] { 101, 101 },
                    new byte[][] { EncodeInt32Value(1), EncodeInt32Value(2) }));
                try { EncodeUpdate(bad); }
                catch (ArgumentException) { threw = true; }
                Check(threw, "K2 写侧拒绝重复槽位");
            }

            // K3 写侧：槽位越界（不再静默跳过）
            {
                bool threw = false;
                List<PMRepUpdateRecord> bad = new List<PMRepUpdateRecord>();
                bad.Add(OneRecord(netId, 1L, PMRepMask.MaxBits, 200, EncodeInt32Value(1)));
                try { EncodeUpdate(bad); }
                catch (ArgumentOutOfRangeException) { threw = true; }
                Check(threw, "K3 写侧拒绝越界槽位 " + PMRepMask.MaxBits + "（不静默截断）");
            }

            // K4 写侧：三个平行数组长度不一致
            {
                bool threw = false;
                List<PMRepUpdateRecord> bad = new List<PMRepUpdateRecord>();
                bad.Add(new PMRepUpdateRecord(netId, 1L, new int[] { 0, 1 }, new ushort[] { 100 },
                    new byte[][] { EncodeInt32Value(1) }));
                try { EncodeUpdate(bad); }
                catch (ArgumentException) { threw = true; }
                Check(threw, "K4 写侧拒绝平行数组长度不一致（写出去必然错位）");
            }

            // K5 读侧：合法消息后拼尾巴
            {
                List<PMRepUpdateRecord> records = new List<PMRepUpdateRecord>();
                records.Add(OneRecord(netId, 1L, 0, 100, EncodeInt32Value(1)));
                byte[] good = EncodeUpdate(records);
                byte[] padded = new byte[good.Length + 1];
                Buffer.BlockCopy(good, 0, padded, 0, good.Length);

                PMRepMessage msg = new PMRepMessage();
                string error;
                Check(!PMReplicationReader.TryRead(new PMNetReader(padded), msg, out error),
                    "K5 读侧拒绝尾部多余字节（" + (error ?? "") + "）");
            }

            // K6 读侧：非最短形态掩码（尾字节为 0）
            {
                PMNetWriter w = new PMNetWriter(64);
                w.WriteVarint(PMRepProtocol.Magic);
                w.WriteVarint(PMRepProtocol.Version);
                w.WriteVarint((ulong)PMRepMessageKind.Update);
                w.WriteVarint(1UL);
                w.WriteVarint(netId);
                w.WriteVarint(1UL);
                w.WriteVarint(2UL);          // 掩码声明 2 字节
                w.WriteRawBytes(new byte[] { 0x01, 0x00 }, 0, 2);   // 尾字节为 0 = 非最短
                w.WriteVarint(100UL);
                byte[] value = EncodeInt32Value(1);
                w.WriteVarint((ulong)value.Length);
                w.WriteRawBytes(value, 0, value.Length);

                PMRepMessage msg = new PMRepMessage();
                string error;
                Check(!PMReplicationReader.TryRead(new PMNetReader(w.ToArray()), msg, out error),
                    "K6 读侧拒绝非最短形态掩码（" + (error ?? "") + "）");
            }

            // K7 读侧：掩码字节数超上限
            {
                PMNetWriter w = new PMNetWriter(64);
                w.WriteVarint(PMRepProtocol.Magic);
                w.WriteVarint(PMRepProtocol.Version);
                w.WriteVarint((ulong)PMRepMessageKind.Update);
                w.WriteVarint(1UL);
                w.WriteVarint(netId);
                w.WriteVarint(1UL);
                w.WriteVarint((ulong)(PMRepMask.MaxBytes + 1));

                PMRepMessage msg = new PMRepMessage();
                string error;
                Check(!PMReplicationReader.TryRead(new PMNetReader(w.ToArray()), msg, out error),
                    "K7 读侧拒绝掩码字节数超上限（" + (error ?? "") + "）");
            }
        }

        // ------------------------------------------------------------------ L

        private static void TestApplyAtomicity()
        {
            ushort[] onRepIds = new ushort[] { 1, 2, 0, 0 };
            Rig rig = Rig.Create(0x3100u, 1, null, onRepIds, null);
            Rig.Client c = rig.Clients[0];
            uint netId = rig.ServerObj.NetId.Value;

            rig.ServerObj.A = 10;
            rig.ServerObj.B = 20;
            rig.ServerObj.S = "base";
            rig.MarkAllDirty();
            TickAndDeliver(rig, c);

            Check(c.Obj.A == 10 && c.Obj.B == 20 && c.Obj.S == "base", "L0 建立基线（A=10/B=20/S=base）");

            // L1..L5：第二条（后项）非法 ⇒ 前项也不能被写入，且不回 ACK
            {
                long onRepBefore = c.Receiver.Stats.OnRepDispatched;
                long rejectedBefore = c.Receiver.Stats.RecordsRejected;

                List<PMRepUpdateRecord> records = new List<PMRepUpdateRecord>();
                records.Add(new PMRepUpdateRecord(netId, 9001L,
                    new int[] { 0, 1 }, new ushort[] { 100, 999 },
                    new byte[][] { EncodeInt32Value(999), EncodeInt32Value(888) }));

                byte[] payload = EncodeUpdate(records);
                int applied = c.Receiver.OnMessage(c.Conn, payload, 0, payload.Length);

                Check(applied == 0, "L1 属性 ID 不一致 ⇒ 整条记录不应用（applied=" + applied + "）");
                Check(c.Obj.A == 10, "L2 前一个合法槽位也没有被写入（无部分应用，实际 A=" + c.Obj.A + "）");
                Check(c.Obj.B == 20, "L2b 非法槽位未写入（实际 B=" + c.Obj.B + "）");
                Check(c.Receiver.PendingAckCount(c.Conn) == 0, "L3 失败记录不回 ACK（防止发送侧以为已同步）");
                Check(c.Receiver.Stats.RecordsRejected > rejectedBefore, "L4 被拒绝记录计数增长");
                Check(c.Receiver.Stats.OnRepDispatched == onRepBefore, "L5 失败记录不触发 OnRep");
            }

            // L5b..L5e：**后项值解码失败、前项值合法** —— 这是暂存对象解码唯一能盖住的形态
            //        （前一个用例的非法之处在结构校验阶段就被拦下，根本到不了解码）。
            {
                long onRepBefore = c.Receiver.Stats.OnRepDispatched;

                List<PMRepUpdateRecord> records = new List<PMRepUpdateRecord>();
                records.Add(new PMRepUpdateRecord(netId, 9005L,
                    new int[] { 0, 3 }, new ushort[] { 100, 103 },
                    new byte[][] { EncodeInt32Value(555), new byte[] { 0x7F } }));

                byte[] payload = EncodeUpdate(records);
                int applied = c.Receiver.OnMessage(c.Conn, payload, 0, payload.Length);

                Check(applied == 0, "L5b 后项值解码失败 ⇒ 整条记录不应用");
                Check(c.Obj.A == 10, "L5c 前项（合法）也没有被写入（暂存解码后提交，实际 A=" + c.Obj.A + "）");
                Check(c.Obj.S == "base", "L5d 失败槽位未写入（实际 \"" + c.Obj.S + "\"）");
                Check(c.Receiver.PendingAckCount(c.Conn) == 0, "L5e 失败记录不回 ACK");
                Check(c.Receiver.Stats.OnRepDispatched == onRepBefore, "L5f 失败记录不触发 OnRep");
            }

            // L6..L8：值编码截断（字符串声明长度超过实际字节）
            {
                long onRepBefore = c.Receiver.Stats.OnRepDispatched;
                List<PMRepUpdateRecord> records = new List<PMRepUpdateRecord>();
                records.Add(OneRecord(netId, 9002L, 3, 103, new byte[] { 0x7F }));

                byte[] payload = EncodeUpdate(records);
                int applied = c.Receiver.OnMessage(c.Conn, payload, 0, payload.Length);

                Check(applied == 0, "L6 值字节截断 ⇒ 整条记录不应用");
                Check(c.Obj.S == "base", "L7 截断值未写入活对象（实际 \"" + c.Obj.S + "\"）");
                Check(c.Receiver.PendingAckCount(c.Conn) == 0, "L8 截断值不回 ACK");
                Check(c.Receiver.Stats.OnRepDispatched == onRepBefore, "L8b 截断值不触发 OnRep");
            }

            // L9..L10：值有尾部多余字节（长度与内容不符）
            {
                byte[] withTail = new byte[] { 0x05, 0x00 };
                List<PMRepUpdateRecord> records = new List<PMRepUpdateRecord>();
                records.Add(OneRecord(netId, 9003L, 0, 100, withTail));

                byte[] payload = EncodeUpdate(records);
                int applied = c.Receiver.OnMessage(c.Conn, payload, 0, payload.Length);

                Check(applied == 0, "L9 值尾部有多余字节 ⇒ 整条记录不应用");
                Check(c.Obj.A == 10, "L10 尾部异常的值未写入活对象（实际 A=" + c.Obj.A + "）");
                Check(c.Receiver.PendingAckCount(c.Conn) == 0, "L10b 尾部异常的值不回 ACK");
            }

            // L11 对照：合法记录照常应用并回 ACK
            {
                List<PMRepUpdateRecord> records = new List<PMRepUpdateRecord>();
                records.Add(OneRecord(netId, 9004L, 0, 100, EncodeInt32Value(77)));

                byte[] payload = EncodeUpdate(records);
                int applied = c.Receiver.OnMessage(c.Conn, payload, 0, payload.Length);

                Check(applied == 1, "L11 合法记录仍然被应用（对照组）");
                Check(c.Obj.A == 77, "L12 合法值已生效（实际 A=" + c.Obj.A + "）");
                Check(c.Receiver.PendingAckCount(c.Conn) == 1, "L13 合法记录回了 1 条 ACK");
            }

            // L14..L17：没有构造工厂 ⇒ 明确拒绝（不静默半应用）
            {
                PMNetRegistry.Reset();

                uint classId = 0x3101u;
                PMReplicationDescriptor desc = MakeWideDescriptor(classId, 3);
                PMNetClassEntry entry = new PMNetClassEntry();
                entry.ClassId = classId;
                entry.TypeName = "WideObjNoFactory";
                entry.Rep = desc;
                entry.Factory = null;
                PMNetRegistry.RegisterClass(entry);

                PMNetWorld clientWorld = new PMNetWorld(new PMSession(11u, false));
                clientWorld.RegisterClass(classId, MakeWideFactory(3));

                List<PMNetLifecycleRecord> lifecycleRecords = new List<PMNetLifecycleRecord>();
                PMNetLifecycleRecord createRecord = new PMNetLifecycleRecord();
                createRecord.Kind = PMObjectEventKind.Create;
                createRecord.NetId = new PMNetId(77u, false);
                createRecord.ClassId = classId;
                lifecycleRecords.Add(createRecord);

                PMNetWriter lw = new PMNetWriter(64);
                PMLifecycleCodec.Write(lw, lifecycleRecords);
                byte[] lb = lw.ToArray();
                clientWorld.OnLifecycleMessage(lb, 0, lb.Length);

                PMNetObject found;
                WideObj obj = null;
                if (clientWorld.TryFind(77u, out found))
                {
                    obj = found as WideObj;
                }

                Check(obj != null, "L14 副本对象已由生命周期消息创建");

                PMReplicationChannel receiver = new PMReplicationChannel();
                receiver.World = clientWorld;
                TestConn conn = new TestConn(2100, true);

                List<PMRepUpdateRecord> records = new List<PMRepUpdateRecord>();
                records.Add(OneRecord(77u, 1L, 0, 1000, EncodeInt32Value(1234)));

                byte[] payload = EncodeUpdate(records);
                int applied = receiver.OnMessage(conn, payload, 0, payload.Length);

                Check(applied == 0, "L15 无工厂 ⇒ 整条记录被拒绝（不静默半应用）");
                Check(obj != null && obj.Values[0] == 0, "L16 活对象未被写入");
                Check(receiver.PendingAckCount(conn) == 0, "L17 无工厂的记录不回 ACK");
                Check(receiver.Stats.RecordsRejected >= 1, "L17b 拒绝计数增长");
            }
        }

        // ------------------------------------------------------------------ M

        private static void TestAckWithoutInflight()
        {
            Rig rig = Rig.Create(0x3200u, 1, null, null, null);
            Rig.Client c = rig.Clients[0];
            uint netId = rig.ServerObj.NetId.Value;

            rig.ServerObj.A = 1;
            rig.MarkAllDirty();
            TickAndDeliver(rig, c);

            long ackedBefore;
            bool has = rig.Sender.TryGetAckedVersion(c.Conn, netId, out ackedBefore);
            Check(has && ackedBefore > 0L, "M0 基线已确认（版本 " + ackedBefore + "）");

            long rejectedBefore = rig.Sender.Stats.AckWithoutInflight;

            // 伪造/重放：一个远远超前、且不存在在途记录的版本
            rig.Sender.OnAck(c.Conn, netId, ackedBefore + 100L);

            long ackedAfter;
            rig.Sender.TryGetAckedVersion(c.Conn, netId, out ackedAfter);
            Check(ackedAfter == ackedBefore,
                "M1 无在途记录的 ACK 不推进确认水位（" + ackedBefore + " → " + ackedAfter + "）");
            Check(rig.Sender.Stats.AckWithoutInflight > rejectedBefore,
                "M2 该情形被单独计数（" + rig.Sender.Stats.AckWithoutInflight + "）");

            // 关键：水位没有被污染 ⇒ 后续**真实** ACK 仍然有效（否则会被判过期，基线永远追不上）
            rig.ServerObj.A = 2;
            rig.ServerObj.MarkPropertyDirty(0);
            rig.Sender.Tick();

            long[] inflight = rig.Sender.GetInflightVersions(c.Conn, netId);
            Check(inflight.Length >= 1, "M3 新一轮产生了在途记录（" + inflight.Length + "）");

            if (inflight.Length >= 1)
            {
                long real = inflight[inflight.Length - 1];
                long staleBefore = rig.Sender.Stats.StaleAckIgnored;
                rig.Sender.OnAck(c.Conn, netId, real);

                long ackedReal;
                rig.Sender.TryGetAckedVersion(c.Conn, netId, out ackedReal);
                Check(ackedReal == real, "M4 真实 ACK 把水位推进到 " + real + "（实际 " + ackedReal + "）");
                Check(rig.Sender.Stats.StaleAckIgnored == staleBefore, "M5 真实 ACK 没有被误判为过期");
            }
        }

        // ------------------------------------------------------------------ N

        private static void TestDirtyTrackerRange()
        {
            // N0..N5 单元：位宽 256、越界明确失败
            {
                PMDirtyTracker t = default(PMDirtyTracker);
                t.Mark(200);
                Check(t.IsDirty(200) && !t.IsDirty(199) && t.HasAny,
                    "N0 Mark(200) 可寻址（>64 位不再退化为整对象置满）");
                Check(!t.HasAnyBelow(200) && t.HasAnyBelow(201), "N1 HasAnyBelow 边界正确");

                t.ClearBit(200);
                Check(!t.HasAny, "N2 ClearBit(200) 按位清除");

                bool threw = false;
                try { t.Mark(PMDirtyTracker.MaxBits); }
                catch (ArgumentOutOfRangeException) { threw = true; }
                Check(threw, "N3 Mark(" + PMDirtyTracker.MaxBits + ") 明确失败（不支持的范围不静默退化）");

                threw = false;
                try { t.IsDirty(-1); }
                catch (ArgumentOutOfRangeException) { threw = true; }
                Check(threw, "N4 IsDirty(-1) 明确失败");

                PMDirtyTracker t2 = default(PMDirtyTracker);
                t2.MarkAll();
                Check(t2.HasAnyBelow(4) && t2.HasAnyBelow(PMDirtyTracker.MaxBits), "N5 MarkAll 置满全部 256 位");
                t2.ClearBitsAbove(4);
                Check(t2.IsDirty(0) && t2.IsDirty(3) && !t2.IsDirty(4) && !t2.IsDirty(255),
                    "N5b ClearBitsAbove(4) 只清下界之上的位（下界之下原样保留）");
            }

            // N6..N8 通道路径：MarkAllPropertiesDirty（休眠唤醒同源）后必须收敛且不再空扫
            {
                Rig rig = Rig.Create(0x3300u, 1, null, null, null);
                Rig.Client c = rig.Clients[0];

                rig.ServerObj.A = 5;
                rig.ServerObj.B = 6;
                rig.ServerObj.C = 7f;
                rig.ServerObj.S = "s";
                rig.ServerObj.MarkAllPropertiesDirty();      // 而不是逐槽位 MarkPropertyDirty
                TickAndDeliverAll(rig);

                Check(c.Obj.A == 5 && c.Obj.B == 6 && c.Obj.S == "s",
                    "N6 MarkAllPropertiesDirty 后全量送达（A/B/C/S）");
                Check(!rig.ServerObj.Dirty.HasAny,
                    "N7 收敛后脏位（含 [SlotCount,256) 的非属性伪位）全部清掉，HasAny 为假");

                c.Conn.Sent.Clear();
                for (int i = 0; i < 5; i++)
                {
                    rig.Sender.Tick();
                    Deliver(rig, c);
                }

                Check(c.Conn.Sent.Count == 0, "N8 稳态不再产生空扫载荷（实际待发 " + c.Conn.Sent.Count + "）");
            }

            // N9..N11 宽类（70 槽位）：槽位 >= 64 的脏位也能按位清除
            {
                WideRig rig = CreateWideRig(0x3301u, 70);
                rig.ServerObj.Values[69] = 4242;
                rig.ServerObj.MarkPropertyDirty(69);
                WideTickAndDeliver(rig);

                Check(rig.ClientObj.Values[69] == 4242,
                    "N9 70 属性类：槽位 69 的值到达（实际 " + rig.ClientObj.Values[69] + "）");
                Check(!rig.ServerObj.Dirty.IsDirty(69), "N10 槽位 69 的脏位被清除（>=64 不再清不掉）");
                Check(!rig.ServerObj.Dirty.HasAny, "N10b HasAny 为假，对象不再每 Tick 空扫");

                rig.ServerObj.MarkAllPropertiesDirty();
                WideTickAndDeliver(rig);
                Check(!rig.ServerObj.Dirty.HasAny, "N11 MarkAll 后同样收敛（HasAny 为假）");
            }

            // N12 休眠唤醒路径（FlushNetDormancy 内部就是 MarkAll）
            {
                Rig rig = Rig.Create(0x3302u, 1, null, null, null);
                Rig.Client c = rig.Clients[0];

                rig.ServerObj.A = 9;
                rig.MarkAllDirty();
                TickAndDeliverAll(rig);

                rig.ServerObj.SetNetDormancy(PMNetDormancy.Partial);
                rig.ServerObj.A = 10;
                rig.ServerObj.MarkPropertyDirty(0);      // 休眠中修改 ⇒ 隐含 FlushNetDormancy
                TickAndDeliverAll(rig);

                Check(!rig.ServerObj.IsDormant, "N12 休眠中的修改唤醒了对象");
                Check(c.Obj.A == 10, "N12b 唤醒后的值已送达（实际 " + c.Obj.A + "）");
                Check(!rig.ServerObj.Dirty.HasAny, "N13 唤醒路径（MarkAll）收敛后 HasAny 为假");

                c.Conn.Sent.Clear();
                rig.Sender.Tick();
                Deliver(rig, c);
                Check(c.Conn.Sent.Count == 0, "N14 唤醒收敛后不再空扫");
            }
        }

        // ------------------------------------------------------------------ O

        /// <summary>
        /// RV2 返工：OnRep 必须在**整条记录提交完成之后**派发。
        ///
        /// 真实形态：一条更新记录里同时带 HP 与 MaxHP，业务在 HP 的 OnRep 里算
        /// `HP / MaxHP`。旧实现"写一个属性就回调一次"会让 HP 的回调读到**旧的 MaxHP**
        /// —— 这就是"回调里读同记录另一个字段"的缺陷形态。
        /// </summary>
        private static void TestOnRepSeesWholeRecord()
        {
            ushort[] onRepIds = new ushort[] { 1, 2, 0, 0 };
            Rig rig = Rig.Create(0x4100u, 1, null, onRepIds, null);
            Rig.Client c = rig.Clients[0];
            uint netId = rig.ServerObj.NetId.Value;

            rig.ServerObj.A = 1;
            rig.ServerObj.B = 2;
            rig.ServerObj.S = "base";
            rig.MarkAllDirty();
            TickAndDeliver(rig, c);

            Check(c.Obj.A == 1 && c.Obj.B == 2 && c.Obj.S == "base", "O0 基线建立（A=1/B=2/S=base）");

            // 换一个"把回调时刻看到的状态记下来"的分发器（rig 默认只记 A/B）
            List<string> seen = new List<string>();
            c.Receiver.RegisterOnRepDispatcher(rig.ClassId, delegate (PMNetObject o, ushort mid)
            {
                RepObj r = (RepObj)o;
                seen.Add(mid + ":" + r.A + "/" + r.B + "/" + r.S);
            });

            // 一条记录同时改槽位 0（A）与槽位 1（B），两者都挂了 OnRep
            List<PMRepUpdateRecord> records = new List<PMRepUpdateRecord>();
            records.Add(new PMRepUpdateRecord(netId, 5001L,
                new int[] { 0, 1 }, new ushort[] { 100, 101 },
                new byte[][] { EncodeInt32Value(11), EncodeInt32Value(22) }));

            byte[] payload = EncodeUpdate(records);
            int applied = c.Receiver.OnMessage(c.Conn, payload, 0, payload.Length);

            Check(applied == 1, "O1 记录已应用（applied=" + applied + "）");
            Check(c.Obj.A == 11 && c.Obj.B == 22,
                "O2 同记录两个属性都已提交（A=" + c.Obj.A + " B=" + c.Obj.B + "）");
            Check(seen.Count == 2, "O3 两个属性的 OnRep 各派发一次（实际 " + seen.Count + "）");
            Check(seen.Count > 0 && seen[0] == "1:11/22/base",
                "O4 A 的 OnRep 看到了同记录里 B 的新值（期望 1:11/22/base，实际 "
                + (seen.Count > 0 ? seen[0] : "<无>") + "）");
            Check(seen.Count > 1 && seen[1] == "2:11/22/base",
                "O5 B 的 OnRep 也看到了整条新值（期望 2:11/22/base，实际 "
                + (seen.Count > 1 ? seen[1] : "<无>") + "）");
            Check(!(seen.Count > 0 && seen[0] == "1:11/2/base"),
                "O6 回调没有拿到新旧混合的状态（旧行为会拿到 B=2）");
        }

        // ------------------------------------------------------------------ P

        /// <summary>
        /// RV2 返工：OnRep 回调抛异常**与协议失败分离**。
        ///
        /// 三件事必须同时成立：
        ///   1) 记录已完整提交（同记录其他属性都在，后面的 OnRep 也照常派发）；
        ///   2) 仍然回 ACK ⇒ 发送侧不会重发 ⇒ 已执行过的回调不会重复执行（无重复副作用）；
        ///   3) 不被伪装成协议错误（RecordsRejected / ProtocolErrors 不增长），另有独立计数。
        /// </summary>
        private static void TestOnRepExceptionIsolation()
        {
            ushort[] onRepIds = new ushort[] { 1, 2, 0, 0 };
            Rig rig = Rig.Create(0x4200u, 1, null, onRepIds, null);
            Rig.Client c = rig.Clients[0];
            uint netId = rig.ServerObj.NetId.Value;

            rig.ServerObj.A = 1;
            rig.ServerObj.B = 2;
            rig.ServerObj.S = "base";
            rig.MarkAllDirty();
            TickAndDeliver(rig, c);

            List<string> seen = new List<string>();
            c.Receiver.RegisterOnRepDispatcher(rig.ClassId, delegate (PMNetObject o, ushort mid)
            {
                RepObj r = (RepObj)o;
                seen.Add(mid + ":" + r.A + "/" + r.B);
                if (mid == 1)
                {
                    // 表现层自己炸了（例如动画/UI 抛异常）—— 与"包坏了"是两回事
                    throw new InvalidOperationException("OnRep 故意抛出（表现层失败）");
                }
            });

            long rejectedBefore = c.Receiver.Stats.RecordsRejected;
            long protoErrBefore = c.Receiver.Stats.ProtocolErrors;
            long exceptionsBefore = c.Receiver.OnRepExceptions;

            rig.ServerObj.A = 11;
            rig.ServerObj.B = 22;
            rig.MarkAllDirty();

            // 手工投递（而不是 TickAndDeliver）：需要在 ACK 被消费之前断言它真的排进了队列
            rig.Sender.Tick();
            List<byte[]> payloads = new List<byte[]>(c.Conn.Sent);
            c.Conn.Sent.Clear();

            int applied = 0;
            for (int i = 0; i < payloads.Count; i++)
            {
                applied += c.Receiver.OnMessage(c.Conn, payloads[i], 0, payloads[i].Length);
            }

            Check(applied == 1, "P1 记录仍被正常应用（回调异常不改变提交结论，applied=" + applied + "）");
            Check(c.Obj.A == 11 && c.Obj.B == 22,
                "P2 整条记录都已提交（A=" + c.Obj.A + " B=" + c.Obj.B + "）");
            Check(seen.Count == 2,
                "P3 抛异常的属性之后，后续属性的 OnRep 仍然派发（实际 " + seen.Count + " 次）");
            Check(seen.Count > 1 && seen[1] == "2:11/22",
                "P4 后续回调看到了完整新值（实际 " + (seen.Count > 1 ? seen[1] : "<无>") + "）");
            Check(c.Receiver.OnRepExceptions == exceptionsBefore + 1,
                "P5 回调异常被单独计数（" + c.Receiver.OnRepExceptions + "）");
            Check(c.Receiver.Stats.RecordsRejected == rejectedBefore,
                "P6 回调异常不算协议失败（RecordsRejected 不变，实际 " + c.Receiver.Stats.RecordsRejected + "）");
            Check(c.Receiver.Stats.ProtocolErrors == protoErrBefore,
                "P7 回调异常不算协议错误（ProtocolErrors 不变，实际 " + c.Receiver.Stats.ProtocolErrors + "）");
            Check(c.Receiver.PendingAckCount(c.Conn) == 1,
                "P8 已完整提交的记录照常回 ACK（实际 " + c.Receiver.PendingAckCount(c.Conn) + " 条）");

            // ACK 回给发送侧 ⇒ 基线推进 ⇒ 下一轮无载荷 ⇒ 那次回调不会被重放
            byte[] ack = c.Receiver.BuildAckMessage(c.Conn);
            Check(ack != null, "P9 ACK 真的可以取出");
            if (ack != null)
            {
                rig.Sender.OnMessage(c.Conn, ack, 0, ack.Length);
            }

            long onRepBefore = c.Receiver.Stats.OnRepDispatched;
            c.Conn.Sent.Clear();
            rig.Sender.Tick();
            Check(c.Conn.Sent.Count == 0, "P10 确认后不再重发（实际待发 " + c.Conn.Sent.Count + "）");

            for (int i = 0; i < c.Conn.Sent.Count; i++)
            {
                c.Receiver.OnMessage(c.Conn, c.Conn.Sent[i], 0, c.Conn.Sent[i].Length);
            }

            Check(c.Receiver.Stats.OnRepDispatched == onRepBefore,
                "P11 回调没有被重放（OnRepDispatched 不变）");
        }

        // ------------------------------------------------------------------ Q

        /// <summary>
        /// RV2 返工：暂存工厂必须产出**全新、未登记**的对象。
        ///
        /// 反例：工厂返回活对象（或任何已登记对象）⇒ "暂存验证"写的就是活对象，
        /// 原子性荡然无存（验证失败时活对象已被污染）。这类工厂必须被当场拒绝。
        /// </summary>
        private static void TestStagingFactoryRejection()
        {
            PMNetRegistry.Reset();

            uint classId = 0x4300u;
            PMReplicationDescriptor desc = MakeDescriptor(classId, null, null);
            PMNetClassEntry entry = new PMNetClassEntry();
            entry.ClassId = classId;
            entry.TypeName = "RepObj";
            entry.Rep = desc;
            entry.Factory = null;                       // 逐个用例替换
            PMNetRegistry.RegisterClass(entry);

            PMNetWorld clientWorld = new PMNetWorld(new PMSession(13u, false));
            clientWorld.RegisterClass(classId, delegate () { return new RepObj(); });

            DeliverLifecycle(clientWorld, MakeCreateRecord(88u, classId), MakeCreateRecord(89u, classId));

            PMNetObject f1;
            PMNetObject f2;
            RepObj live = clientWorld.TryFind(88u, out f1) ? (RepObj)f1 : null;
            RepObj other = clientWorld.TryFind(89u, out f2) ? (RepObj)f2 : null;

            Check(live != null && other != null && live.State == PMNetObjectState.Active,
                "Q0 两个副本对象都已登记（State=" + (live == null ? "?" : live.State.ToString()) + "）");

            PMReplicationChannel receiver = new PMReplicationChannel();
            receiver.World = clientWorld;
            TestConn conn = new TestConn(2200, true);

            List<PMRepUpdateRecord> recs = new List<PMRepUpdateRecord>();
            recs.Add(OneRecord(88u, 1L, 0, 100, EncodeInt32Value(777)));
            byte[] payload = EncodeUpdate(recs);

            // Q1..Q4：工厂返回**活对象本身**
            entry.Factory = delegate () { return live; };
            int applied = receiver.OnMessage(conn, payload, 0, payload.Length);

            Check(applied == 0, "Q1 工厂返回活对象 ⇒ 整条记录被拒绝（applied=" + applied + "）");
            Check(live != null && live.A == 0, "Q2 活对象一个字节都没被写（实际 A=" + (live == null ? -1 : live.A) + "）");
            Check(receiver.PendingAckCount(conn) == 0, "Q3 不回 ACK");
            Check(receiver.StagingRejected > 0, "Q4 该情形被单独计数（" + receiver.StagingRejected + "）");

            // Q5..Q7：工厂返回**另一个已登记的对象**（同样必须拒绝）
            entry.Factory = delegate () { return other; };
            int applied2 = receiver.OnMessage(conn, payload, 0, payload.Length);

            Check(applied2 == 0, "Q5 工厂返回已登记的另一个对象 ⇒ 整条记录被拒绝");
            Check(other != null && other.A == 0, "Q6 那个已登记对象也没被写（实际 A=" + (other == null ? -1 : other.A) + "）");
            Check(receiver.StagingRejected >= 2, "Q7 拒绝计数继续增长（" + receiver.StagingRejected + "）");

            // Q8..Q10：对照组 —— 返回全新对象的工厂才能通过。
            // 注意要换一个**新的接收通道**：`PMRepObjectEntry` 是按对象缓存的，缓存时会
            // 把当时的工厂一起记住（`ResolveEntry` 里 `entry.Factory = cls.Factory`），
            // 所以同一个通道上改注册表里的工厂不会生效。
            entry.Factory = delegate () { return new RepObj(); };
            PMReplicationChannel receiver2 = new PMReplicationChannel();
            receiver2.World = clientWorld;
            int applied3 = receiver2.OnMessage(conn, payload, 0, payload.Length);

            Check(applied3 == 1, "Q8 返回全新对象的工厂照常工作（对照组，applied=" + applied3 + "）");
            Check(live != null && live.A == 777, "Q9 值已应用到活对象（实际 A=" + (live == null ? -1 : live.A) + "）");
            Check(receiver2.PendingAckCount(conn) == 1, "Q10 对照组回了 1 条 ACK");
        }

        // ------------------------------------------------------------------ R

        /// <summary>
        /// RV2 返工的**剩余边界**（诚实记录，不是"已解决"）：
        /// 若描述符的 Reader 不是纯字段写入（有副作用 / 只在活对象上抛），
        /// "暂存解码成功"并不能推出"提交一定成功"。此时本层的选择是：
        ///   - 不派发任何 OnRep（不让业务看到混合态）；
        ///   - 不回 ACK（让发送侧重发整条记录）；
        ///   - 计数 + 告警如实暴露，而不是假装成功，也不是"用 Writer 回滚"
        ///     （Writer 自身也可能抛，回滚失败将不可见 —— 那种保证造不出来）。
        /// 对纯字段写入的 Reader，重发幂等 ⇒ 最终收敛；本用例就是把这条链走完。
        /// </summary>
        private static void TestNonPureReaderBoundary()
        {
            PMNetRegistry.Reset();

            uint classId = 0x4400u;
            RepObj[] liveBox = new RepObj[1];
            int[] throwBudget = new int[] { 1 };
            List<ushort> onReps = new List<ushort>();

            PMReplicationDescriptor desc = MakeDescriptor(classId, null, null);
            desc.Properties[0].OnRepMethodId = 1;

            // 非纯 Reader：在**活对象**上抛一次（暂存对象上正常）。
            // 这正是"双解码盖不住"的形态：解到暂存对象成功，提交到活对象却失败。
            // 挂在**第二个槽位**上，这样"前一个属性已提交"与"后面那个提交失败"
            // 能同时被观察到（若挂在第一个槽位上，整条记录其实是零提交）。
            desc.Properties[3].Reader = delegate (PMNetObject t, PMNetReader r)
            {
                string s = r.ReadStringValue();
                if (ReferenceEquals(t, liveBox[0]) && throwBudget[0] > 0)
                {
                    throwBudget[0]--;
                    throw new InvalidOperationException("Reader 只在活对象上抛（非纯字段写入）");
                }

                ((RepObj)t).S = s;
            };

            PMNetClassEntry entry = new PMNetClassEntry();
            entry.ClassId = classId;
            entry.TypeName = "RepObj";
            entry.Rep = desc;
            entry.Factory = delegate () { return new RepObj(); };
            PMNetRegistry.RegisterClass(entry);

            PMNetWorld clientWorld = new PMNetWorld(new PMSession(17u, false));
            clientWorld.RegisterClass(classId, delegate () { return new RepObj(); });

            DeliverLifecycle(clientWorld, MakeCreateRecord(90u, classId));

            PMNetObject found;
            liveBox[0] = clientWorld.TryFind(90u, out found) ? (RepObj)found : null;
            Check(liveBox[0] != null, "R0 副本对象已创建");

            PMReplicationChannel receiver = new PMReplicationChannel();
            receiver.World = clientWorld;
            receiver.RegisterOnRepDispatcher(classId, delegate (PMNetObject o, ushort mid) { onReps.Add(mid); });
            TestConn conn = new TestConn(2300, true);

            // 一条记录：槽位 0（合法） + 槽位 3（Reader 在活对象上抛）
            PMNetWriter vw0 = new PMNetWriter(16);
            vw0.WriteInt32(55);
            PMNetWriter vw3 = new PMNetWriter(16);
            vw3.WriteStringValue("new");

            List<PMRepUpdateRecord> recs = new List<PMRepUpdateRecord>();
            recs.Add(new PMRepUpdateRecord(90u, 1L,
                new int[] { 0, 3 }, new ushort[] { 100, 103 },
                new byte[][] { vw0.ToArray(), vw3.ToArray() }));
            byte[] payload = EncodeUpdate(recs);

            int applied = receiver.OnMessage(conn, payload, 0, payload.Length);

            Check(applied == 0, "R1 提交阶段失败 ⇒ 整条记录不生效");
            Check(receiver.CommitFailures == 1, "R2 提交失败被单独计数（" + receiver.CommitFailures + "）");
            Check(receiver.PendingAckCount(conn) == 0, "R3 不回 ACK（发送侧会重发整条记录）");
            Check(onReps.Count == 0, "R4 部分提交不派发任何 OnRep（业务看不到混合态）");
            Check(liveBox[0] != null && liveBox[0].A == 55 && liveBox[0].S == string.Empty,
                "R5 如实暴露：已提交的属性留在活对象上（A=" + (liveBox[0] == null ? -1 : liveBox[0].A)
                + "，S=\"" + (liveBox[0] == null ? "?" : liveBox[0].S) + "\"）");

            // 发送侧没收到 ACK ⇒ 重发同一条记录；本例的 Reader 只抛一次 ⇒ 这次能完整提交
            int applied2 = receiver.OnMessage(conn, payload, 0, payload.Length);

            Check(applied2 == 1, "R6 重发后完整提交（applied=" + applied2 + "）");
            Check(liveBox[0] != null && liveBox[0].S == "new",
                "R7 重发后活对象收敛（S=\"" + (liveBox[0] == null ? "?" : liveBox[0].S) + "\"）");
            Check(onReps.Count == 1 && onReps[0] == 1,
                "R8 只在完整提交后才派发 OnRep（实际 " + onReps.Count + " 次）");
            Check(receiver.PendingAckCount(conn) == 1, "R9 完整提交后回 ACK");
        }
    }
}
