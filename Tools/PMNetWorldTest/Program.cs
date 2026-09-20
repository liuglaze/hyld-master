using System;
using System.Collections.Generic;
using PMNet;

namespace PMNetWorldTest
{
    /// <summary>
    /// R1 / T39+T40 验收：对象运行时（M04）的契约验证。
    ///
    /// 事实来源：
    ///   - `Docs/plans/net-r0-contract.md` §2.1（D-R0-01..04）、§2.3、§5（生命周期契约行）
    ///   - UE 源码逐字（文件:行号见 `PMNetOwnerChain.cs` 与 `PMNetObject.GetNetConnection` 注释）
    ///
    /// 运行：dotnet Tools/PMNetWorldTest/bin/Release/net8.0/PMNetWorldTest.dll
    /// </summary>
    internal static class Program
    {
        private static int _passed;
        private static readonly List<string> _failures = new List<string>();

        private static int Main()
        {
            Console.WriteLine("=== R1/T39+T40：对象运行时（M04）契约验证 ===");
            Console.WriteLine();

            Section("A. 生命周期消息的字节级契约", TestCodec);
            Section("B. 身份分配：单调 + 永不复用 + 有界", TestIdentityAllocation);
            Section("C. Owner 链：逐条对齐 UE 的三处覆写", TestOwnerChain);
            Section("D. 服务端出站：每连接待发与保序", TestServerOutbound);
            Section("E. 客户端入站：创建/销毁/拒绝路径", TestClientInbound);
            Section("F. 端到端：服务端生成 → 字节 → 客户端应用", TestEndToEnd);
            Section("G. 原子性与边界：初始状态回滚、批量不部分采纳", TestAtomicity);
            Section("H. 登记表有界（RV3）：大量 spawn/destroy、回滚与容量", TestObjectRegistryBounded);
            Section("I. 声明式 Create 初值（RV5）：描述符初值、条件过滤、失败不发布", TestDeclarativeInitialState);

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

        // ================================================================
        // 测试替身
        // ================================================================

        /// <summary>可观察的连接替身。</summary>
        private sealed class TestConnection : PMNetConnection
        {
            public readonly string Name;
            public readonly List<byte[]> Sent = new List<byte[]>();
            public bool Ready = true;

            public TestConnection(int id, bool isServerSide, string name)
                : base(id, isServerSide)
            {
                Name = name;
            }

            public override bool IsReady { get { return Ready; } }

            public bool HasViewer;
            public float VX, VY, VZ;

            public override bool TryGetViewerLocation(out float x, out float y, out float z)
            {
                x = VX; y = VY; z = VZ;
                return HasViewer;
            }

            public override void Send(byte[] payload, PMRpcReliability reliability)
            {
                Sent.Add(payload);
            }

            public override string ToString() { return Name; }
        }

        /// <summary>带可观察初始状态的对象。</summary>
        private class TestObject : PMNetObject
        {
            public int Hp;
            public int Mana;
            public PMNetObject Ref;          // 初始状态里引用的另一个对象
            public int CreateCalls;
            public int DestroyCalls;
            public PMObjectDestroyReason LastReason;

            protected internal override void OnSerializeInitialState(PMNetWriter writer)
            {
                writer.WriteVarint((ulong)Hp);
                writer.WriteVarint((ulong)Mana);
                // 引用按 NetId 值写；0 表示空引用
                writer.WriteVarint(Ref != null ? (ulong)Ref.NetId.Value : 0UL);
            }

            protected internal override void OnDeserializeInitialState(PMNetReader reader)
            {
                Hp = checked((int)reader.ReadVarint());
                Mana = checked((int)reader.ReadVarint());
                uint refId = checked((uint)reader.ReadVarint());
                if (refId != 0u)
                {
                    // 引用解析失败不是异常：合法情况（对端还没创建它 / 已被销毁）
                    PMNetObject found;
                    if (World != null && World.TryFind(refId, out found))
                    {
                        Ref = found;
                    }
                }
            }

            protected internal override void OnReplicatedCreate() { CreateCalls++; }

            protected internal override void OnReplicatedDestroy(PMObjectDestroyReason reason)
            {
                DestroyCalls++;
                LastReason = reason;
            }
        }

        /// <summary>带世界坐标的对象，用于走通「相关性裁剪」分支。</summary>
        private class PositionedObject : TestObject
        {
            public float X, Y, Z;

            protected override bool TryGetReplicationLocation(out float x, out float y, out float z)
            {
                x = X; y = Y; z = Z;
                return true;
            }
        }

        /// <summary>初始状态反序列化必定抛异常的对象（用于回滚测试）。</summary>
        private sealed class BadStateObject : PMNetObject
        {
            protected internal override void OnDeserializeInitialState(PMNetReader reader)
            {
                throw new InvalidOperationException("初始状态损坏（测试注入）");
            }
        }

        private const uint ClassTest = 100u;
        private const uint ClassBad = 101u;
        private const uint ClassPositioned = 102u;

        private static PMNetWorld NewServerWorld(int maxObjects = 1024)
        {
            return new PMNetWorld(new PMSession(1u, true), maxObjects);
        }

        private static PMNetWorld NewClientWorld(uint epoch = 1u, int maxObjects = 1024)
        {
            PMNetWorld w = new PMNetWorld(new PMSession(epoch, false), maxObjects);
            w.RegisterClass(ClassTest, delegate { return new TestObject(); });
            w.RegisterClass(ClassBad, delegate { return new BadStateObject(); });
            w.RegisterClass(ClassPositioned, delegate { return new PositionedObject(); });
            return w;
        }

        // ================================================================
        // A. 编解码字节级契约
        // ================================================================

        private static void TestCodec()
        {
            // A1 往返：创建（带初始状态）+ 销毁，且保持全局顺序
            {
                List<PMNetLifecycleRecord> input = new List<PMNetLifecycleRecord>();
                PMNetLifecycleRecord c1 = new PMNetLifecycleRecord();
                c1.Kind = PMObjectEventKind.Create;
                c1.NetId = new PMNetId(7u, false);
                c1.ClassId = 100u;
                c1.ArchetypeId = 3u;
                c1.IsOwner = true;
                c1.InitialState = new byte[] { 1, 2, 3, 4, 5 };
                c1.RepInitialState = new byte[] { 9, 8, 7 };
                input.Add(c1);

                PMNetLifecycleRecord d1 = new PMNetLifecycleRecord();
                d1.Kind = PMObjectEventKind.Destroy;
                d1.NetId = new PMNetId(9u, false);
                d1.Reason = PMObjectDestroyReason.OutOfRange;
                input.Add(d1);

                PMNetLifecycleRecord c2 = new PMNetLifecycleRecord();
                c2.Kind = PMObjectEventKind.Create;
                c2.NetId = new PMNetId(11u, true);
                c2.ClassId = 200u;
                c2.ArchetypeId = 0u;
                c2.IsOwner = false;
                c2.InitialState = null;
                c2.RepInitialState = null;
                input.Add(c2);

                PMNetWriter w = new PMNetWriter();
                PMLifecycleCodec.Write(w, input);

                List<PMNetLifecycleRecord> output = new List<PMNetLifecycleRecord>();
                string err;
                bool ok = PMLifecycleCodec.TryRead(w.GetBuffer(), 0, w.Length, output, out err);

                Check(ok, "A1 往返解析成功" + (ok ? "" : "（err=" + err + "）"));
                Check(output.Count == 3, "A1 记录数一致（3）");
                if (output.Count == 3)
                {
                    Check(output[0].Kind == PMObjectEventKind.Create
                          && output[0].NetId.Value == 7u
                          && output[0].NetId.IsStatic == false
                          && output[0].ClassId == 100u
                          && output[0].ArchetypeId == 3u
                          && output[0].IsOwner
                          && output[0].InitialState != null
                          && output[0].InitialState.Length == 5
                          && output[0].InitialState[4] == 5
                          && output[0].RepInitialState != null
                          && output[0].RepInitialState.Length == 3
                          && output[0].RepInitialState[2] == 7,
                        "A1 第 1 条创建字段逐项一致（含声明式初值）");

                    Check(output[1].Kind == PMObjectEventKind.Destroy
                          && output[1].NetId.Value == 9u
                          && output[1].Reason == PMObjectDestroyReason.OutOfRange,
                        "A1 第 2 条销毁字段逐项一致");

                    Check(output[2].Kind == PMObjectEventKind.Create
                          && output[2].NetId.Value == 11u
                          && output[2].NetId.IsStatic == true
                          && output[2].ClassId == 200u
                          && output[2].InitialState == null
                          && output[2].RepInitialState == null,
                        "A1 第 3 条（静态 + 无初始状态）字段一致");

                    Check(output[0].Kind == PMObjectEventKind.Create
                          && output[1].Kind == PMObjectEventKind.Destroy
                          && output[2].Kind == PMObjectEventKind.Create,
                        "A1 全局相对顺序保持（创建→销毁→创建，未被分组重排）");
                }
            }

            // A2 版本不符 ⇒ 拒绝
            {
                PMNetWriter w = new PMNetWriter();
                w.WriteVarint(99UL);   // 错误的 formatVersion
                w.WriteVarint(1UL);
                List<PMNetLifecycleRecord> outRec = new List<PMNetLifecycleRecord>();
                string err;
                bool ok = PMLifecycleCodec.TryRead(w.GetBuffer(), 0, w.Length, outRec, out err);
                Check(!ok, "A2 线格式版本不符 ⇒ 拒绝");
                Check(!ok && outRec.Count == 0, "A2 版本不符时不产出任何记录");
                Check(!ok && err != null && err.Contains("版本"), "A2 版本不符的告警文本可诊断");
            }

            // A3 记录数越界 ⇒ 拒绝（不得按长度预分配）
            {
                PMNetWriter w = new PMNetWriter();
                w.WriteVarint((ulong)PMLifecycleCodec.FormatVersion);
                w.WriteVarint(1000000UL);
                List<PMNetLifecycleRecord> outRec = new List<PMNetLifecycleRecord>();
                string err;
                bool ok = PMLifecycleCodec.TryRead(w.GetBuffer(), 0, w.Length, outRec, out err);
                Check(!ok, "A3 记录数超上限 ⇒ 拒绝");
            }

            // A4 记录数为 0 ⇒ 拒绝（空批无意义，且有界检查必须覆盖 <=0）
            {
                PMNetWriter w = new PMNetWriter();
                w.WriteVarint((ulong)PMLifecycleCodec.FormatVersion);
                w.WriteVarint(0UL);
                List<PMNetLifecycleRecord> outRec = new List<PMNetLifecycleRecord>();
                string err;
                bool ok = PMLifecycleCodec.TryRead(w.GetBuffer(), 0, w.Length, outRec, out err);
                Check(!ok, "A4 记录数为 0 ⇒ 拒绝");
            }

            // A5 初始状态长度越界 ⇒ 拒绝
            {
                PMNetWriter w = new PMNetWriter();
                w.WriteVarint((ulong)PMLifecycleCodec.FormatVersion);
                w.WriteVarint(1UL);
                w.WriteVarint(1UL);      // kind = Create
                w.WriteVarint(5UL);      // netId
                w.WriteVarint(0UL);      // 非静态
                w.WriteVarint(100UL);    // classId
                w.WriteVarint(0UL);      // archetypeId
                w.WriteVarint(0UL);      // isOwner
                w.WriteVarint(999999UL); // stateLen 越界
                List<PMNetLifecycleRecord> outRec = new List<PMNetLifecycleRecord>();
                string err;
                bool ok = PMLifecycleCodec.TryRead(w.GetBuffer(), 0, w.Length, outRec, out err);
                Check(!ok, "A5 初始状态长度越界 ⇒ 拒绝");
            }

            // A6 未知事件类型 ⇒ 拒绝
            {
                PMNetWriter w = new PMNetWriter();
                w.WriteVarint((ulong)PMLifecycleCodec.FormatVersion);
                w.WriteVarint(1UL);
                w.WriteVarint(77UL);     // 非法 kind
                w.WriteVarint(5UL);
                w.WriteVarint(0UL);
                List<PMNetLifecycleRecord> outRec = new List<PMNetLifecycleRecord>();
                string err;
                bool ok = PMLifecycleCodec.TryRead(w.GetBuffer(), 0, w.Length, outRec, out err);
                Check(!ok, "A6 未知事件类型 ⇒ 拒绝");
            }

            // A7 NetId 为 0 ⇒ 拒绝（身份无效）
            {
                PMNetWriter w = new PMNetWriter();
                w.WriteVarint((ulong)PMLifecycleCodec.FormatVersion);
                w.WriteVarint(1UL);
                w.WriteVarint(1UL);
                w.WriteVarint(0UL);      // 非法 NetId
                w.WriteVarint(0UL);
                w.WriteVarint(100UL);
                w.WriteVarint(0UL);
                w.WriteVarint(0UL);
                w.WriteVarint(0UL);
                List<PMNetLifecycleRecord> outRec = new List<PMNetLifecycleRecord>();
                string err;
                bool ok = PMLifecycleCodec.TryRead(w.GetBuffer(), 0, w.Length, outRec, out err);
                Check(!ok, "A7 NetId 为 0 ⇒ 拒绝");
            }

            // A8 尾部多余字节 ⇒ 拒绝（防止「截断/拼接」被静默接受）
            {
                PMNetWriter w = new PMNetWriter();
                w.WriteVarint((ulong)PMLifecycleCodec.FormatVersion);
                w.WriteVarint(1UL);
                w.WriteVarint(2UL);      // Destroy
                w.WriteVarint(5UL);
                w.WriteVarint(0UL);
                w.WriteVarint(1UL);
                w.WriteVarint(42UL);     // 多余尾巴
                List<PMNetLifecycleRecord> outRec = new List<PMNetLifecycleRecord>();
                string err;
                bool ok = PMLifecycleCodec.TryRead(w.GetBuffer(), 0, w.Length, outRec, out err);
                Check(!ok, "A8 尾部多余字节 ⇒ 拒绝");
            }

            // A9 截断字节流 ⇒ 拒绝且不抛异常
            {
                List<PMNetLifecycleRecord> input = new List<PMNetLifecycleRecord>();
                PMNetLifecycleRecord c = new PMNetLifecycleRecord();
                c.Kind = PMObjectEventKind.Create;
                c.NetId = new PMNetId(3u, false);
                c.ClassId = 1u;
                c.InitialState = new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 };
                input.Add(c);

                PMNetWriter w = new PMNetWriter();
                PMLifecycleCodec.Write(w, input);

                bool anyThrew = false;
                bool anyAccepted = false;
                for (int cut = 1; cut < w.Length; cut++)
                {
                    List<PMNetLifecycleRecord> outRec = new List<PMNetLifecycleRecord>();
                    string err;
                    try
                    {
                        if (PMLifecycleCodec.TryRead(w.GetBuffer(), 0, cut, outRec, out err))
                        {
                            anyAccepted = true;
                        }
                    }
                    catch (Exception)
                    {
                        anyThrew = true;
                    }
                }

                Check(!anyThrew, "A9 任意长度截断都不抛异常（TryRead 契约）");
                Check(!anyAccepted, "A9 任意长度截断都被拒绝（不留半截数据）");
            }

            // A10 写入侧拒绝超上限的批
            {
                List<PMNetLifecycleRecord> many = new List<PMNetLifecycleRecord>();
                for (int i = 0; i < PMLifecycleCodec.MaxRecordsPerBatch + 1; i++)
                {
                    PMNetLifecycleRecord r = new PMNetLifecycleRecord();
                    r.Kind = PMObjectEventKind.Destroy;
                    r.NetId = new PMNetId((uint)(i + 1), false);
                    many.Add(r);
                }

                PMNetWriter w = new PMNetWriter();
                bool threw = false;
                try { PMLifecycleCodec.Write(w, many); }
                catch (ArgumentOutOfRangeException) { threw = true; }
                Check(threw, "A10 写入侧拒绝超过上限的批（不产生无界的包）");
            }

            // A11 恰好等于上限必须通过（边界不是 off-by-one）
            {
                List<PMNetLifecycleRecord> exact = new List<PMNetLifecycleRecord>();
                for (int i = 0; i < PMLifecycleCodec.MaxRecordsPerBatch; i++)
                {
                    PMNetLifecycleRecord r = new PMNetLifecycleRecord();
                    r.Kind = PMObjectEventKind.Destroy;
                    r.NetId = new PMNetId((uint)(i + 1), false);
                    exact.Add(r);
                }

                PMNetWriter w = new PMNetWriter();
                PMLifecycleCodec.Write(w, exact);
                List<PMNetLifecycleRecord> outRec = new List<PMNetLifecycleRecord>();
                string err;
                bool ok = PMLifecycleCodec.TryRead(w.GetBuffer(), 0, w.Length, outRec, out err);
                Check(ok && outRec.Count == PMLifecycleCodec.MaxRecordsPerBatch,
                      "A11 恰好等于上限的批可以往返（边界无 off-by-one）");
            }

            // A12 写入侧拒绝超过上限的初始状态（两侧对称，不得静默写一个对端必拒的包）
            {
                List<PMNetLifecycleRecord> big = new List<PMNetLifecycleRecord>();
                PMNetLifecycleRecord r = new PMNetLifecycleRecord();
                r.Kind = PMObjectEventKind.Create;
                r.NetId = new PMNetId(3u, false);
                r.ClassId = 100u;
                r.RepInitialState = new byte[PMLifecycleCodec.MaxInitialStateBytes + 1];
                big.Add(r);

                PMNetWriter w = new PMNetWriter();
                bool threw = false;
                try { PMLifecycleCodec.Write(w, big); }
                catch (ArgumentOutOfRangeException) { threw = true; }
                Check(threw, "A12 写入侧拒绝超上限的声明式初值（" + PMLifecycleCodec.MaxInitialStateBytes + " 字节）");

                List<PMNetLifecycleRecord> big2 = new List<PMNetLifecycleRecord>();
                PMNetLifecycleRecord r2 = new PMNetLifecycleRecord();
                r2.Kind = PMObjectEventKind.Create;
                r2.NetId = new PMNetId(4u, false);
                r2.ClassId = 100u;
                r2.InitialState = new byte[PMLifecycleCodec.MaxInitialStateBytes + 1];
                big2.Add(r2);

                PMNetWriter w2 = new PMNetWriter();
                threw = false;
                try { PMLifecycleCodec.Write(w2, big2); }
                catch (ArgumentOutOfRangeException) { threw = true; }
                Check(threw, "A12b 写入侧同样拒绝超上限的手写初始状态");
            }
        }

        // ================================================================
        // B. 身份分配
        // ================================================================

        private static void TestIdentityAllocation()
        {
            // B1 单调分配、不复用
            {
                PMNetWorld w = NewServerWorld();
                TestObject a = new TestObject();
                TestObject b = new TestObject();
                TestObject c = new TestObject();

                Check(w.Spawn(a, ClassTest), "B1 第一次 Spawn 成功");
                Check(w.Spawn(b, ClassTest), "B1 第二次 Spawn 成功");
                Check(a.NetId.Value > 0u && b.NetId.Value > a.NetId.Value, "B1 NetId 单调递增");

                Check(w.DestroyObject(b), "B1 销毁成功");
                Check(w.Spawn(c, ClassTest), "B1 销毁后再 Spawn 成功");
                Check(c.NetId.Value > b.NetId.Value, "B1 已销毁对象的 NetId 不被复用（严格大于）");
                Check(c.NetId.Value != b.NetId.Value, "B1 新对象 NetId ≠ 已销毁对象 NetId");
            }

            // B2 销毁即刻摘除索引（D-R0-04）
            {
                PMNetWorld w = NewServerWorld();
                TestObject a = new TestObject();
                w.Spawn(a, ClassTest);

                PMNetObject found;
                Check(w.TryFind(a.NetId.Value, out found) && ReferenceEquals(found, a),
                      "B2 存活时可按 NetId 查到");

                w.DestroyObject(a);
                found = null;
                Check(!w.TryFind(a.NetId.Value, out found), "B2 销毁后立刻查不到（不等 ack）");
                Check(a.State == PMNetObjectState.Destroyed, "B2 对象状态 = Destroyed");
                Check(w.ObjectCount == 0, "B2 存活计数归零");
                Check(w.TotalObjectCount == 0, "B2 登记表只保留存活对象（TotalObjectCount 同步归零）");
                Check(w.Stats.ObjectsSpawned == 1, "B2 历史累计创建数由 Stats.ObjectsSpawned 保留（诊断用）");
            }

            // B3 上限拒绝且不浪费 NetId
            {
                PMNetWorld w = NewServerWorld(2);
                TestObject a = new TestObject();
                TestObject b = new TestObject();
                TestObject c = new TestObject();

                Check(w.Spawn(a, ClassTest), "B3 上限内第 1 个成功");
                Check(w.Spawn(b, ClassTest), "B3 上限内第 2 个成功");
                Check(!w.Spawn(c, ClassTest), "B3 超出上限被拒绝");

                uint before = w.Allocator.LastAllocated;
                TestObject d = new TestObject();
                Check(!w.Spawn(d, ClassTest), "B3 再次超出上限仍被拒绝");
                Check(w.Allocator.LastAllocated == before, "B3 被拒绝的 Spawn 不消耗 NetId（编号不可回收）");
                Check(c.NetId.Value == 0u && d.NetId.Value == 0u, "B3 被拒绝的对象保持无效身份");
                Check(w.Stats.RejectedOverCapacity == 2, "B3 超限计数 = 2");
            }

            // B4 客户端侧禁止 Spawn / Destroy（权威只能在服务端）
            {
                PMNetWorld w = NewClientWorld();
                TestObject a = new TestObject();
                Check(!w.Spawn(a, ClassTest), "B4 客户端侧 Spawn 被拒绝");
                Check(w.ObjectCount == 0, "B4 客户端侧 Spawn 未登记任何对象");
            }

            // B5 重复 Spawn 同一对象被拒绝
            {
                PMNetWorld w = NewServerWorld();
                TestObject a = new TestObject();
                Check(w.Spawn(a, ClassTest), "B5 首次 Spawn 成功");
                Check(!w.Spawn(a, ClassTest), "B5 对同一对象再次 Spawn 被拒绝");
                Check(w.ObjectCount == 1, "B5 未产生重复登记");
            }

            // B6 销毁未登记对象 / 重复销毁安全
            {
                PMNetWorld w = NewServerWorld();
                TestObject a = new TestObject();
                Check(!w.DestroyObject(a), "B6 销毁未登记对象返回 false（不崩）");
                w.Spawn(a, ClassTest);
                Check(w.DestroyObject(a), "B6 首次销毁成功");
                Check(!w.DestroyObject(a), "B6 重复销毁返回 false（幂等，不重复计数）");
                Check(w.Stats.ObjectsDestroyed == 1, "B6 销毁计数只加一次");
            }

            // B7 静态与动态共用编号空间（D-R0-02 前提）
            {
                PMNetWorld w = NewServerWorld();
                TestObject dyn = new TestObject();
                TestObject st = new TestObject();
                w.Spawn(dyn, ClassTest, 0u, false);
                w.Spawn(st, ClassTest, 0u, true);

                Check(dyn.NetId.IsStatic == false && st.NetId.IsStatic == true, "B7 静态位正确记录");
                Check(st.NetId.Value > dyn.NetId.Value, "B7 静态与动态共用同一单调编号空间");
            }
        }

        // ================================================================
        // C. Owner 链
        // ================================================================

        private static void TestOwnerChain()
        {
            TestConnection conn = new TestConnection(1, true, "C1");

            // C1 纯 Owner 链：leaf.Owner=mid, mid.Owner=controller(有 Player)
            {
                PMNetControllerObject pc = new PMNetControllerObject();
                pc.NetConnection = conn;

                TestObject mid = new TestObject();
                mid.Owner = pc;

                TestObject leaf = new TestObject();
                leaf.Owner = mid;

                Check(ReferenceEquals(pc.GetNetConnection(), conn), "C1 控制器直接返回其连接");
                Check(ReferenceEquals(mid.GetNetConnection(), conn), "C1 中间对象沿 Owner 链推到控制器");
                Check(ReferenceEquals(leaf.GetNetConnection(), conn), "C1 叶子对象沿两级 Owner 链推到控制器");
            }

            // C2 控制器无 Player ⇒ null，**不回退 Owner 链**（UE PlayerController.cpp:276）
            {
                PMNetControllerObject grandparent = new PMNetControllerObject();
                TestConnection other = new TestConnection(2, true, "C2-other");
                grandparent.NetConnection = other;

                PMNetControllerObject pc = new PMNetControllerObject();
                pc.NetConnection = null;      // 无 Player
                pc.Owner = grandparent;       // 但它自己还有 Owner

                Check(pc.GetNetConnection() == null,
                      "C2 无 Player 的控制器返回 null（即使自己还有 Owner，也不回退）");
                Check(grandparent.GetNetConnection() != null,
                      "C2 对照：有 Player 的控制器正常返回连接");
            }

            // C3 Pawn 有控制器 ⇒ 走控制器，且不再回退 Owner 链
            {
                PMNetControllerObject pc = new PMNetControllerObject();
                pc.NetConnection = conn;

                PMNetPawnObject pawn = new PMNetPawnObject();
                pawn.Controller = pc;

                // 故意再挂一个指向别的连接的 Owner，验证"有控制器时不会用到它"
                PMNetControllerObject decoy = new PMNetControllerObject();
                decoy.NetConnection = new TestConnection(3, true, "C3-decoy");
                pawn.Owner = decoy;

                Check(ReferenceEquals(pawn.GetNetConnection(), conn),
                      "C3 Pawn 有控制器时走控制器（Owner 链被短路，不混用）");
            }

            // C4 Pawn 无控制器 ⇒ 回退 Owner 链（UE Pawn.cpp:736 的 Super:: 分支）
            {
                PMNetControllerObject pc = new PMNetControllerObject();
                pc.NetConnection = conn;

                PMNetPawnObject pawn = new PMNetPawnObject();
                pawn.Controller = null;
                pawn.Owner = pc;

                Check(ReferenceEquals(pawn.GetNetConnection(), conn),
                      "C4 Pawn 无控制器时回退 Owner 链");
            }

            // C5 控制器链走到头（无连接无 Owner）⇒ null，不崩
            {
                PMNetControllerObject pc = new PMNetControllerObject();
                Check(pc.GetNetConnection() == null, "C5 裸控制器返回 null");
                TestObject orphan = new TestObject();
                orphan.Owner = pc;
                Check(orphan.GetNetConnection() == null, "C5 指向裸控制器的对象返回 null");
            }

            // C6 GetNetOwningPlayer：权威角色是前置条件（UE Actor.cpp:1979）
            {
                PMNetControllerObject pc = new PMNetControllerObject();
                pc.NetConnection = conn;
                pc.Role = PMNetRole.Authority;

                TestObject child = new TestObject();
                child.Owner = pc;

                child.Role = PMNetRole.Authority;
                Check(ReferenceEquals(child.GetNetOwningPlayer(), conn),
                      "C6 权威角色 + Owner 链 ⇒ 推出拥有者玩家");

                child.Role = PMNetRole.AutonomousProxy;
                Check(child.GetNetOwningPlayer() == null,
                      "C6 非权威角色 ⇒ 推不出拥有者玩家（权威是前置条件）");
            }

            // C7 Pawn 的 GetNetOwningPlayer：控制器必须是控制器类才取它的 Player
            {
                PMNetControllerObject pc = new PMNetControllerObject();
                pc.NetConnection = conn;

                PMNetPawnObject pawn = new PMNetPawnObject();
                pawn.Role = PMNetRole.Authority;
                pawn.Controller = pc;
                Check(ReferenceEquals(pawn.GetNetOwningPlayer(), conn),
                      "C7 权威 Pawn + 控制器类控制器 ⇒ 取到 Player");

                // 控制器不是控制器类对象 ⇒ 回退基类（基类要求 Authority 且走 Owner，Owner 为空 ⇒ null）
                TestObject weirdController = new TestObject();
                weirdController.Owner = pc;
                pawn.Controller = weirdController;
                Check(pawn.GetNetOwningPlayer() == null,
                      "C7 控制器不是控制器类对象 ⇒ 不回退到它的连接（对齐 UE 的 Cast 失败分支）");
            }
        }

        // ================================================================
        // D. 服务端出站
        // ================================================================

        private static void TestServerOutbound()
        {
            // D1 Spawn 后每条连接各有待发创建
            {
                PMNetWorld w = NewServerWorld();
                TestConnection a = new TestConnection(1, true, "A");
                TestConnection b = new TestConnection(2, true, "B");
                w.AddConnection(a);
                w.AddConnection(b);

                TestObject obj = new TestObject();
                obj.Hp = 77;
                w.Spawn(obj, ClassTest);

                Check(w.PendingEventCount(a) == 1, "D1 连接 A 有 1 条待发事件");
                Check(w.PendingEventCount(b) == 1, "D1 连接 B 有 1 条待发事件");

                byte[] bytesA = w.BuildLifecycleBatch(a);
                Check(bytesA != null && bytesA.Length > 0, "D1 A 的批次构造成功");
                Check(w.PendingEventCount(a) == 0, "D1 A 的待发被排空");

                byte[] again = w.BuildLifecycleBatch(a);
                Check(again == null, "D1 A 第二次构造返回 null（无待发）");
                Check(w.PendingEventCount(b) == 1, "D1 B 的待发不受 A 排空影响（每连接独立）");

                byte[] bytesB = w.BuildLifecycleBatch(b);
                Check(bytesB != null && bytesB.Length > 0, "D1 B 的批次构造成功");
            }

            // D2 新连接加入 ⇒ 已有对象补发全量（D-R0-12）
            {
                PMNetWorld w = NewServerWorld();
                TestConnection a = new TestConnection(1, true, "A");
                w.AddConnection(a);

                TestObject o1 = new TestObject();
                TestObject o2 = new TestObject();
                w.Spawn(o1, ClassTest);
                w.Spawn(o2, ClassTest);

                Check(w.BuildLifecycleBatch(a) != null, "D2 老连接收到两个创建");
                Check(w.PendingEventCount(a) == 0, "D2 老连接待发排空");

                TestConnection late = new TestConnection(9, true, "Late");
                w.AddConnection(late);

                Check(w.PendingEventCount(late) == 2, "D2 新加入的连接收到全部既有对象（全量初始状态）");

                byte[] bytes = w.BuildLifecycleBatch(late);
                PMNetWorld client = NewClientWorld();
                client.OnLifecycleMessage(bytes, 0, bytes.Length);
                Check(client.ObjectCount == 2, "D2 新连接应用后看到 2 个对象");
            }

            // D3 isOwner 按连接区分（bNetOwner）
            {
                PMNetWorld w = NewServerWorld();
                TestConnection ownerConn = new TestConnection(1, true, "Owner");
                TestConnection otherConn = new TestConnection(2, true, "Other");
                w.AddConnection(ownerConn);
                w.AddConnection(otherConn);

                PMNetControllerObject pc = new PMNetControllerObject();
                pc.NetConnection = ownerConn;

                TestObject owned = new TestObject();
                owned.Owner = pc;
                w.Spawn(owned, ClassTest);

                byte[] toOwner = w.BuildLifecycleBatch(ownerConn);
                byte[] toOther = w.BuildLifecycleBatch(otherConn);

                PMNetWorld wOwner = NewClientWorld();
                wOwner.OnLifecycleMessage(toOwner, 0, toOwner.Length);
                PMNetObject gotOwner;
                wOwner.TryFind(owned.NetId, out gotOwner);

                PMNetWorld wOther = NewClientWorld();
                wOther.OnLifecycleMessage(toOther, 0, toOther.Length);
                PMNetObject gotOther;
                wOther.TryFind(owned.NetId, out gotOther);

                Check(gotOwner != null && gotOwner.Role == PMNetRole.AutonomousProxy,
                      "D3 拥有者连接上的副本 = AutonomousProxy");
                Check(gotOther != null && gotOther.Role == PMNetRole.SimulatedProxy,
                      "D3 其他连接上的副本 = SimulatedProxy");
            }

            // D4 销毁：创建已发出 ⇒ 排一条销毁
            {
                PMNetWorld w = NewServerWorld();
                TestConnection a = new TestConnection(1, true, "A");
                w.AddConnection(a);

                TestObject obj = new TestObject();
                w.Spawn(obj, ClassTest);
                w.BuildLifecycleBatch(a);          // 把创建发出去

                Check(w.DestroyObject(obj, PMObjectDestroyReason.Destroyed), "D4 销毁成功");
                Check(w.PendingEventCount(a) == 1, "D4 排入 1 条销毁事件");

                byte[] bytes = w.BuildLifecycleBatch(a);
                Check(bytes != null, "D4 销毁批次构造成功");

                PMNetWorld client = NewClientWorld();
                client.OnLifecycleMessage(bytes, 0, bytes.Length);
                Check(client.ObjectCount == 0, "D4 客户端对象被移除");
            }

            // D5 销毁：创建尚未发出 ⇒ 连同创建一起吞掉（对端从没见过它）
            {
                PMNetWorld w = NewServerWorld();
                TestConnection a = new TestConnection(1, true, "A");
                w.AddConnection(a);

                TestObject obj = new TestObject();
                w.Spawn(obj, ClassTest);
                Check(w.PendingEventCount(a) == 1, "D5 创建已待发");

                w.DestroyObject(obj);
                Check(w.PendingEventCount(a) == 0, "D5 创建与销毁双双被吞掉（不产生任何记录）");
                Check(w.BuildLifecycleBatch(a) == null, "D5 无可发批次");
                Check(w.Stats.SwallowedUnsentCreate == 1, "D5 吞掉计数 = 1");

                PMNetWorld client = NewClientWorld();
                Check(client.ObjectCount == 0, "D5 客户端从未看到该对象（不产生幽灵再消失）");
            }

            // D6 无连接时 Spawn 不崩，后续加连接能补齐
            {
                PMNetWorld w = NewServerWorld();
                TestObject obj = new TestObject();
                Check(w.Spawn(obj, ClassTest), "D6 无连接时 Spawn 成功");

                TestConnection a = new TestConnection(1, true, "A");
                w.AddConnection(a);
                Check(w.PendingEventCount(a) == 1, "D6 后加入的连接能补齐该对象");
            }

            // D8 不相关对象：**不发但保持待发**（曾被静默丢弃 —— 见负向验证记录）
            {
                PMNetWorld w = NewServerWorld();
                TestConnection a = new TestConnection(1, true, "A");
                a.HasViewer = true;             // 观察者在原点
                w.AddConnection(a);

                PositionedObject far = new PositionedObject();
                far.CullDistanceSquared = 10000f;   // 100 单位
                far.X = 1000f;                       // 远超裁剪半径
                w.Spawn(far, ClassPositioned);

                TestObject viewer = new TestObject();   // 非 null 才会走裁剪分支
                byte[] none = w.BuildLifecycleBatch(a, viewer);

                Check(none == null, "D8 不相关对象不产生字节");
                Check(w.PendingEventCount(a) == 1,
                      "D8 不相关对象**保持待发**（不得因筛选而静默丢弃）");

                // 第二次取批次仍然不发，且仍然保留
                Check(w.BuildLifecycleBatch(a, viewer) == null, "D8 持续不相关持续不发");
                Check(w.PendingEventCount(a) == 1, "D8 持续不相关持续保留");
            }

            // D9 由不相关转为相关 ⇒ 补发全量初始状态（D-R0-12「首次进入范围 ⇒ 全量」）
            {
                PMNetWorld w = NewServerWorld();
                TestConnection a = new TestConnection(1, true, "A");
                a.HasViewer = true;
                w.AddConnection(a);

                PositionedObject obj = new PositionedObject();
                obj.CullDistanceSquared = 10000f;
                obj.X = 1000f;
                obj.Hp = 55;
                w.Spawn(obj, ClassPositioned);

                TestObject viewer = new TestObject();
                Check(w.BuildLifecycleBatch(a, viewer) == null, "D9 初始不相关，不发");
                Check(w.PendingEventCount(a) == 1, "D9 事件被保留");

                obj.X = 0f;                                  // 进入范围
                byte[] bytes = w.BuildLifecycleBatch(a, viewer);
                Check(bytes != null, "D9 进入范围后补发");

                PMNetWorld client = NewClientWorld();
                client.OnLifecycleMessage(bytes, 0, bytes.Length);

                PMNetObject found;
                bool ok = client.TryFind(obj.NetId, out found);
                Check(ok, "D9 客户端此时才看到该对象");
                PositionedObject c = found as PositionedObject;
                Check(c != null && c.Hp == 55, "D9 补发携带的是**发那一刻**的全量初始状态");
                Check(w.PendingEventCount(a) == 0, "D9 补发后待发排空");

                // 一个「不相关」与一个「相关」混合：相关的必须发出去，不相关的必须留下
                PMNetWorld w2 = NewServerWorld();
                TestConnection b = new TestConnection(2, true, "B");
                b.HasViewer = true;
                w2.AddConnection(b);

                PositionedObject near = new PositionedObject();
                near.CullDistanceSquared = 10000f;
                near.X = 0f;
                PositionedObject far = new PositionedObject();
                far.CullDistanceSquared = 10000f;
                far.X = 5000f;

                w2.Spawn(far, ClassPositioned);     // 先创建不相关的
                w2.Spawn(near, ClassPositioned);    // 再创建相关的

                byte[] mixed = w2.BuildLifecycleBatch(b, viewer);
                Check(mixed != null, "D9 混合批次能产出（相关的那条）");
                Check(w2.PendingEventCount(b) == 1, "D9 混合批次后不相关的仍留在队列（精确 1 条）");

                PMNetWorld c2 = NewClientWorld();
                c2.OnLifecycleMessage(mixed, 0, mixed.Length);
                PMNetObject gotNear, gotFar;
                Check(c2.TryFind(near.NetId, out gotNear) && !c2.TryFind(far.NetId, out gotFar),
                      "D9 混合批次只送达相关对象，不误送不相关对象");
            }

            // D7 注销连接会丢弃其待发（不为已断开的连接排事件）
            {
                PMNetWorld w = NewServerWorld();
                TestConnection a = new TestConnection(1, true, "A");
                w.AddConnection(a);

                TestObject obj = new TestObject();
                w.Spawn(obj, ClassTest);

                w.RemoveConnection(a);
                Check(w.ConnectionCount == 0, "D7 连接已注销");
                Check(w.BuildLifecycleBatch(a) == null, "D7 已注销连接取不到批次");
            }
        }

        // ================================================================
        // E. 客户端入站
        // ================================================================

        private static void TestClientInbound()
        {
            // E1 未知 ClassId ⇒ 丢弃 + 计数（D-R0-11）
            {
                PMNetWorld server = NewServerWorld();
                TestConnection a = new TestConnection(1, true, "A");
                server.AddConnection(a);

                TestObject obj = new TestObject();
                server.Spawn(obj, 12345u);   // 客户端没注册这个类型
                byte[] bytes = server.BuildLifecycleBatch(a);

                PMNetWorld client = NewClientWorld();
                client.OnLifecycleMessage(bytes, 0, bytes.Length);

                Check(client.ObjectCount == 0, "E1 未知类型不被创建");
                Check(client.Stats.DroppedUnknownClass == 1, "E1 未知类型计数 = 1");
            }

            // E2 重复 NetId ⇒ 丢弃 + 计数（NetId 不复用 ⇒ 属协议违规）
            {
                PMNetWorld client = NewClientWorld();
                List<PMNetLifecycleRecord> recs = new List<PMNetLifecycleRecord>();
                PMNetLifecycleRecord r = new PMNetLifecycleRecord();
                r.Kind = PMObjectEventKind.Create;
                r.NetId = new PMNetId(42u, false);
                r.ClassId = ClassTest;
                recs.Add(r);

                PMNetWriter w = new PMNetWriter();
                PMLifecycleCodec.Write(w, recs);
                byte[] bytes = w.ToArray();

                client.OnLifecycleMessage(bytes, 0, bytes.Length);
                client.OnLifecycleMessage(bytes, 0, bytes.Length);   // 同一个 NetId 再来一次

                Check(client.ObjectCount == 1, "E2 只创建了一个对象");
                Check(client.Stats.CreatesApplied == 1, "E2 创建只应用一次");
                Check(client.Stats.DroppedDuplicateId == 1, "E2 重复 NetId 计数 = 1");
            }

            // E3 客户端超上限 ⇒ 拒绝（D-R0-18 有界）
            {
                PMNetWorld client = NewClientWorld(1u, 2);
                List<PMNetLifecycleRecord> recs = new List<PMNetLifecycleRecord>();
                for (uint i = 1; i <= 3; i++)
                {
                    PMNetLifecycleRecord r = new PMNetLifecycleRecord();
                    r.Kind = PMObjectEventKind.Create;
                    r.NetId = new PMNetId(i, false);
                    r.ClassId = ClassTest;
                    recs.Add(r);
                }

                PMNetWriter w = new PMNetWriter();
                PMLifecycleCodec.Write(w, recs);
                byte[] bytes = w.ToArray();
                client.OnLifecycleMessage(bytes, 0, bytes.Length);

                Check(client.ObjectCount == 2, "E3 只创建到上限为止");
                Check(client.Stats.RejectedOverCapacity == 1, "E3 超限计数 = 1");
            }

            // E4 销毁未知对象 ⇒ 计数不崩
            {
                PMNetWorld client = NewClientWorld();
                List<PMNetLifecycleRecord> recs = new List<PMNetLifecycleRecord>();
                PMNetLifecycleRecord r = new PMNetLifecycleRecord();
                r.Kind = PMObjectEventKind.Destroy;
                r.NetId = new PMNetId(999u, false);
                r.Reason = PMObjectDestroyReason.Destroyed;
                recs.Add(r);

                PMNetWriter w = new PMNetWriter();
                PMLifecycleCodec.Write(w, recs);
                byte[] bytes = w.ToArray();
                client.OnLifecycleMessage(bytes, 0, bytes.Length);

                Check(client.Stats.DroppedUnknownObject == 1, "E4 销毁未知对象计数 = 1");
                Check(client.ObjectCount == 0, "E4 未产生对象");
            }

            // E5 销毁生效 + 回调收到原因
            {
                PMNetWorld client = NewClientWorld();
                List<PMNetLifecycleRecord> cre = new List<PMNetLifecycleRecord>();
                PMNetLifecycleRecord c = new PMNetLifecycleRecord();
                c.Kind = PMObjectEventKind.Create;
                c.NetId = new PMNetId(5u, false);
                c.ClassId = ClassTest;
                cre.Add(c);

                PMNetWriter w1 = new PMNetWriter();
                PMLifecycleCodec.Write(w1, cre);
                byte[] b1 = w1.ToArray();
                client.OnLifecycleMessage(b1, 0, b1.Length);

                PMNetObject obj;
                client.TryFind(5u, out obj);
                TestObject t = obj as TestObject;
                Check(t != null && t.CreateCalls == 1, "E5 创建回调触发一次");

                List<PMNetLifecycleRecord> des = new List<PMNetLifecycleRecord>();
                PMNetLifecycleRecord d = new PMNetLifecycleRecord();
                d.Kind = PMObjectEventKind.Destroy;
                d.NetId = new PMNetId(5u, false);
                d.Reason = PMObjectDestroyReason.OutOfRange;
                des.Add(d);

                PMNetWriter w2 = new PMNetWriter();
                PMLifecycleCodec.Write(w2, des);
                byte[] b2 = w2.ToArray();
                client.OnLifecycleMessage(b2, 0, b2.Length);

                Check(t != null && t.DestroyCalls == 1, "E5 销毁回调触发一次");
                Check(t != null && t.LastReason == PMObjectDestroyReason.OutOfRange, "E5 销毁原因正确传递");
                Check(client.ObjectCount == 0, "E5 对象已移除");
            }

            // E6 协议错误 ⇒ 整条丢弃，不做部分采纳
            {
                PMNetWorld client = NewClientWorld();
                PMNetWriter w = new PMNetWriter();
                w.WriteVarint((ulong)PMLifecycleCodec.FormatVersion);   // 版本正确
                w.WriteVarint(2UL);          // 声明 2 条
                w.WriteVarint(1UL);          // 第 1 条：创建（合法）
                w.WriteVarint(1UL);
                w.WriteVarint(0UL);
                w.WriteVarint(ClassTest);
                w.WriteVarint(0UL);
                w.WriteVarint(0UL);
                w.WriteVarint(0UL);          // stateLen = 0
                w.WriteVarint(0UL);          // repStateLen = 0（RV5）
                // 第 2 条缺失 ⇒ 截断
                byte[] bytes = w.ToArray();
                client.OnLifecycleMessage(bytes, 0, bytes.Length);

                Check(client.ObjectCount == 0, "E6 截断的批不产生任何对象（不部分采纳）");
                Check(client.Stats.ProtocolErrors == 1, "E6 协议错误计数 = 1");
            }

            // E7 单条业务失败不拖垮同批其他记录
            {
                PMNetWorld client = NewClientWorld();
                List<PMNetLifecycleRecord> recs = new List<PMNetLifecycleRecord>();

                PMNetLifecycleRecord bad = new PMNetLifecycleRecord();
                bad.Kind = PMObjectEventKind.Create;
                bad.NetId = new PMNetId(1u, false);
                bad.ClassId = 99999u;        // 未知类型
                recs.Add(bad);

                PMNetLifecycleRecord good = new PMNetLifecycleRecord();
                good.Kind = PMObjectEventKind.Create;
                good.NetId = new PMNetId(2u, false);
                good.ClassId = ClassTest;
                recs.Add(good);

                PMNetWriter w = new PMNetWriter();
                PMLifecycleCodec.Write(w, recs);
                byte[] bytes = w.ToArray();
                client.OnLifecycleMessage(bytes, 0, bytes.Length);

                Check(client.ObjectCount == 1, "E7 同批中的合法记录仍然被应用");
                Check(client.Stats.DroppedUnknownClass == 1, "E7 非法记录单独计数");
            }
        }

        // ================================================================
        // F. 端到端
        // ================================================================

        private static void TestEndToEnd()
        {
            // F1 生成 → 字节 → 应用，初始状态随创建原子到达（D-R0-16）
            {
                PMNetWorld server = NewServerWorld();
                TestConnection a = new TestConnection(1, true, "A");
                server.AddConnection(a);

                TestObject obj = new TestObject();
                obj.Hp = 321;
                obj.Mana = 654;
                server.Spawn(obj, ClassTest);

                byte[] bytes = server.BuildLifecycleBatch(a);
                Check(bytes != null, "F1 批次构造成功");

                PMNetWorld client = NewClientWorld();
                client.OnLifecycleMessage(bytes, 0, bytes.Length);

                PMNetObject found;
                bool ok = client.TryFind(obj.NetId, out found);
                Check(ok, "F1 客户端按同一个 NetId 找到对象");

                TestObject t = found as TestObject;
                Check(t != null, "F1 类型还原正确");
                Check(t != null && t.Hp == 321 && t.Mana == 654,
                      "F1 初始状态与创建原子到达（不存在「已创建但值未到」）");
                Check(t != null && t.NetId == obj.NetId && t.ClassId == ClassTest,
                      "F1 身份与类型 ID 一致");
            }

            // F2 初始状态里的引用：同批中先创建的被引用方可以解析到
            {
                PMNetWorld server = NewServerWorld();
                TestConnection a = new TestConnection(1, true, "A");
                server.AddConnection(a);

                TestObject target = new TestObject();
                server.Spawn(target, ClassTest);          // 先创建
                TestObject holder = new TestObject();
                holder.Ref = target;                       // 后创建的引用它
                server.Spawn(holder, ClassTest);

                byte[] bytes = server.BuildLifecycleBatch(a);
                PMNetWorld client = NewClientWorld();
                client.OnLifecycleMessage(bytes, 0, bytes.Length);

                PMNetObject found;
                client.TryFind(holder.NetId, out found);
                TestObject cHolder = found as TestObject;
                Check(cHolder != null && cHolder.Ref != null, "F2 同批中先创建的引用可被解析");
                Check(cHolder != null && cHolder.Ref != null && cHolder.Ref.NetId == target.NetId,
                      "F2 解析到的是正确对象");
            }

            // F3 同一批内「销毁 A → 创建引用 A 的 B」：顺序保序 ⇒ B 解析不到 A
            {
                PMNetWorld server = NewServerWorld();
                TestConnection a = new TestConnection(1, true, "A");
                server.AddConnection(a);

                TestObject target = new TestObject();
                server.Spawn(target, ClassTest);
                server.BuildLifecycleBatch(a);              // 先把 target 的创建发出去

                // 在服务端：销毁 target，然后创建引用它的 holder
                server.DestroyObject(target);
                TestObject holder = new TestObject();
                holder.Ref = target;                        // 引用一个已销毁对象（业务应避免，但协议必须确定行为）
                server.Spawn(holder, ClassTest);

                byte[] bytes = server.BuildLifecycleBatch(a);
                PMNetWorld client = NewClientWorld();

                // 客户端先看到 target 的创建
                PMNetWriter wFirst = new PMNetWriter();
                List<PMNetLifecycleRecord> tmp = new List<PMNetLifecycleRecord>();
                PMNetLifecycleRecord tr = new PMNetLifecycleRecord();
                tr.Kind = PMObjectEventKind.Create;
                tr.NetId = target.NetId;
                tr.ClassId = ClassTest;
                tmp.Add(tr);
                PMLifecycleCodec.Write(wFirst, tmp);
                byte[] bFirst = wFirst.ToArray();
                client.OnLifecycleMessage(bFirst, 0, bFirst.Length);

                client.OnLifecycleMessage(bytes, 0, bytes.Length);

                PMNetObject found;
                client.TryFind(holder.NetId, out found);
                TestObject cHolder = found as TestObject;
                Check(cHolder != null, "F3 引用已销毁对象的创建本身仍然成功");
                Check(cHolder != null && cHolder.Ref == null,
                      "F3 引用解析不到已销毁对象（销毁在创建之前生效，顺序未被重排）");

                client.TryFind(target.NetId, out found);
                Check(found == null, "F3 被引用方确实已销毁");
            }

            // F4 端到端多轮：创建→更新→销毁，全程 NetId 稳定
            {
                PMNetWorld server = NewServerWorld();
                TestConnection a = new TestConnection(1, true, "A");
                server.AddConnection(a);
                PMNetWorld client = NewClientWorld();

                TestObject obj = new TestObject();
                obj.Hp = 10;
                server.Spawn(obj, ClassTest);
                byte[] firstBatch = server.BuildLifecycleBatch(a);
                client.OnLifecycleMessage(firstBatch, 0, firstBatch.Length);

                PMNetObject found;
                bool ok1 = client.TryFind(obj.NetId, out found);
                TestObject t = found as TestObject;
                Check(ok1 && t != null && t.Hp == 10, "F4 第一轮：创建 + 初始血量");

                // 再创建两个，验证 NetId 在客户端与服务端一致
                TestObject o2 = new TestObject();
                TestObject o3 = new TestObject();
                server.Spawn(o2, ClassTest);
                server.Spawn(o3, ClassTest);
                byte[] batch = server.BuildLifecycleBatch(a);
                client.OnLifecycleMessage(batch, 0, batch.Length);

                PMNetObject f2, f3;
                Check(client.TryFind(o2.NetId, out f2) && client.TryFind(o3.NetId, out f3),
                      "F4 多个对象的 NetId 两端一致");
                Check(client.ObjectCount == 3, "F4 客户端共 3 个对象");

                server.DestroyObject(o2);
                byte[] batch2 = server.BuildLifecycleBatch(a);
                client.OnLifecycleMessage(batch2, 0, batch2.Length);

                PMNetObject gone;
                Check(!client.TryFind(o2.NetId, out gone), "F4 销毁后客户端不再持有");
                Check(client.ObjectCount == 2, "F4 客户端剩 2 个对象");
            }
        }

        // ================================================================
        // G. 原子性与边界
        // ================================================================

        private static void TestAtomicity()
        {
            // G1 初始状态反序列化失败 ⇒ 回滚整个创建（不留半初始化对象）
            {
                PMNetWorld client = NewClientWorld();
                List<PMNetLifecycleRecord> recs = new List<PMNetLifecycleRecord>();
                PMNetLifecycleRecord r = new PMNetLifecycleRecord();
                r.Kind = PMObjectEventKind.Create;
                r.NetId = new PMNetId(8u, false);
                r.ClassId = ClassBad;
                r.InitialState = new byte[] { 1, 2, 3 };
                recs.Add(r);

                PMNetWriter w = new PMNetWriter();
                PMLifecycleCodec.Write(w, recs);
                byte[] bytes = w.ToArray();
                client.OnLifecycleMessage(bytes, 0, bytes.Length);

                PMNetObject found;
                Check(!client.TryFind(8u, out found), "G1 初始状态损坏 ⇒ 对象被回滚，查询不到");
                Check(client.ObjectCount == 0, "G1 存活计数为 0");
                Check(client.TotalObjectCount == 0, "G1 回滚后登记表不留强引用");
                Check(client.Stats.RolledBackCreate == 1, "G1 回滚计数 = 1");
                Check(client.Stats.CreatesApplied == 0, "G1 未计入成功创建");
            }

            // G2 回滚后同一个 NetId 可以再次创建（因为已从索引摘除，不是"重复 ID"）
            {
                PMNetWorld client = NewClientWorld();
                List<PMNetLifecycleRecord> bad = new List<PMNetLifecycleRecord>();
                PMNetLifecycleRecord r = new PMNetLifecycleRecord();
                r.Kind = PMObjectEventKind.Create;
                r.NetId = new PMNetId(8u, false);
                r.ClassId = ClassBad;
                r.InitialState = new byte[] { 1 };
                bad.Add(r);

                PMNetWriter w = new PMNetWriter();
                PMLifecycleCodec.Write(w, bad);
                byte[] b = w.ToArray();
                client.OnLifecycleMessage(b, 0, b.Length);

                List<PMNetLifecycleRecord> good = new List<PMNetLifecycleRecord>();
                PMNetLifecycleRecord g = new PMNetLifecycleRecord();
                g.Kind = PMObjectEventKind.Create;
                g.NetId = new PMNetId(8u, false);
                g.ClassId = ClassTest;
                good.Add(g);

                PMNetWriter w2 = new PMNetWriter();
                PMLifecycleCodec.Write(w2, good);
                byte[] b2 = w2.ToArray();
                client.OnLifecycleMessage(b2, 0, b2.Length);

                PMNetObject found;
                Check(client.TryFind(8u, out found), "G2 回滚后的 NetId 可以重新创建（未留下脏索引）");
                Check(client.Stats.DroppedDuplicateId == 0, "G2 回滚不被误判为重复 NetId");
            }

            // G3 反复往返不泄漏：大量创建/销毁后计数一致
            {
                PMNetWorld server = NewServerWorld(4096);
                TestConnection a = new TestConnection(1, true, "A");
                server.AddConnection(a);
                PMNetWorld client = NewClientWorld(1u, 4096);

                for (int round = 0; round < 100; round++)
                {
                    TestObject obj = new TestObject();
                    obj.Hp = round;
                    server.Spawn(obj, ClassTest);
                    byte[] b = server.BuildLifecycleBatch(a);
                    client.OnLifecycleMessage(b, 0, b.Length);

                    server.DestroyObject(obj);
                    byte[] b2 = server.BuildLifecycleBatch(a);
                    client.OnLifecycleMessage(b2, 0, b2.Length);
                }

                Check(server.ObjectCount == 0, "G3 服务端存活对象归零");
                Check(client.ObjectCount == 0, "G3 客户端存活对象归零");
                Check(server.Stats.ObjectsSpawned == 100, "G3 生成计数 = 100");
                Check(server.Stats.ObjectsDestroyed == 100, "G3 销毁计数 = 100");
                Check(client.Stats.CreatesApplied == 100, "G3 客户端应用创建 100 次");
                Check(client.Stats.DestroysApplied == 100, "G3 客户端应用销毁 100 次");
                Check(server.PendingEventCount(a) == 0, "G3 服务端待发队列排空（无泄漏）");
            }

            // G4 重复注册同一 ClassId ⇒ 抛异常（D-R0-49 稳定 ID 不得冲突）
            {
                PMNetWorld w = NewClientWorld();
                bool threw = false;
                try { w.RegisterClass(ClassTest, delegate { return new TestObject(); }); }
                catch (InvalidOperationException) { threw = true; }
                Check(threw, "G4 重复注册 ClassId 抛异常（拒绝静默覆盖）");
            }

            // G5 ClassId 0 保留 ⇒ 注册被拒
            {
                PMNetWorld w = NewClientWorld();
                bool threw = false;
                try { w.RegisterClass(0u, delegate { return new TestObject(); }); }
                catch (ArgumentOutOfRangeException) { threw = true; }
                Check(threw, "G5 ClassId 0 不允许注册（保留为无效值）");
            }

            // G6 未初始化对象被工厂返回 ⇒ 拒绝（工厂契约）
            {
                PMNetWorld w = new PMNetWorld(new PMSession(1u, false));
                TestObject reused = new TestObject();
                reused.State = PMNetObjectState.Destroyed;   // 工厂错误地复用了旧对象
                w.RegisterClass(ClassTest, delegate { return reused; });

                List<PMNetLifecycleRecord> recs = new List<PMNetLifecycleRecord>();
                PMNetLifecycleRecord r = new PMNetLifecycleRecord();
                r.Kind = PMObjectEventKind.Create;
                r.NetId = new PMNetId(1u, false);
                r.ClassId = ClassTest;
                recs.Add(r);

                PMNetWriter ww = new PMNetWriter();
                PMLifecycleCodec.Write(ww, recs);
                byte[] b = ww.ToArray();
                w.OnLifecycleMessage(b, 0, b.Length);

                Check(w.ObjectCount == 0, "G6 工厂返回已使用对象 ⇒ 拒绝登记");
                Check(w.Stats.ProtocolErrors == 1, "G6 计入协议错误");
            }

            // G7 Reset 清空世界但不复位 Id 分配器（D-R0-02：同一世界内不复用编号）
            {
                PMNetWorld w = NewServerWorld();
                TestObject a = new TestObject();
                w.Spawn(a, ClassTest);
                uint last = w.Allocator.LastAllocated;

                w.Reset();
                Check(w.ObjectCount == 0, "G7 Reset 后对象清空");

                TestObject b = new TestObject();
                w.Spawn(b, ClassTest);
                Check(b.NetId.Value > last, "G7 Reset 不复位分配器：新对象拿到更大的 NetId");
            }
        }

        // ================================================================
        // H. 登记表有界（RV3）
        // ================================================================

        /// <summary>无初始状态的普通节点（用于"无描述符就不产声明式初值"）。</summary>
        private class PlainNode : PMNetObject
        {
            public int Value;
        }

        private static void TestObjectRegistryBounded()
        {
            // H1..H4 大量 spawn/destroy：登记表不无界增长
            {
                PMNetWorld w = NewServerWorld(64);
                TestConnection a = new TestConnection(1, true, "A");
                w.AddConnection(a);

                bool ok = true;
                for (int round = 0; round < 4000; round++)
                {
                    TestObject obj = new TestObject();
                    if (!w.Spawn(obj, ClassTest) || !w.DestroyObject(obj))
                    {
                        ok = false;
                        break;
                    }

                    if ((round & 63) == 0)
                    {
                        w.BuildLifecycleBatch(a);
                    }
                }

                Check(ok, "H1 4000 轮 spawn/destroy 全部成功");
                Check(w.ObjectCount == 0, "H2 存活对象归零");
                Check(w.TotalObjectCount == 0, "H3 登记表只保留存活对象（无历史墓碑 / 无幽灵强引用）");
                Check(w.AllObjectSlotCount <= 2 * w.MaxObjects,
                    "H4 登记表槽位数有界（" + w.AllObjectSlotCount + " <= 2 * " + w.MaxObjects + "）");
                Check(w.Stats.ObjectsSpawned == 4000, "H4b 历史累计创建数由统计保留（" + w.Stats.ObjectsSpawned + "）");
            }

            // H5..H6 容量判据按存活数（销毁后就腾出容量）
            {
                PMNetWorld w = NewServerWorld(2);
                TestObject a = new TestObject();
                TestObject b = new TestObject();
                TestObject c = new TestObject();
                Check(w.Spawn(a, ClassTest) && w.Spawn(b, ClassTest), "H5 上限内两个成功");
                Check(!w.Spawn(c, ClassTest), "H5b 存活数达上限被拒绝");

                w.DestroyObject(a);
                Check(w.Spawn(c, ClassTest), "H6 销毁后腾出容量（判据是存活数）");
                Check(w.ObjectCount == 2 && w.TotalObjectCount == 2,
                    "H6b 两个计数一致（都只算存活：" + w.ObjectCount + "/" + w.TotalObjectCount + "）");
            }

            // H7 GetObjectAt 只遍历存活对象，顺序仍按创建
            {
                PMNetWorld w = NewServerWorld();
                TestObject a = new TestObject();
                TestObject b = new TestObject();
                TestObject c = new TestObject();
                w.Spawn(a, ClassTest);
                w.Spawn(b, ClassTest);
                w.Spawn(c, ClassTest);
                w.DestroyObject(b);

                Check(ReferenceEquals(w.GetObjectAt(0), a) && ReferenceEquals(w.GetObjectAt(1), c),
                    "H7 GetObjectAt 跳过已销毁槽位（顺序按创建）");
                Check(w.GetObjectAt(2) == null, "H7b 越界返回 null");
            }

            // H8 创建回滚也解除强引用与槽位
            {
                PMNetWorld client = NewClientWorld();
                List<PMNetLifecycleRecord> recs = new List<PMNetLifecycleRecord>();
                PMNetLifecycleRecord r = new PMNetLifecycleRecord();
                r.Kind = PMObjectEventKind.Create;
                r.NetId = new PMNetId(8u, false);
                r.ClassId = ClassBad;
                r.InitialState = new byte[] { 1, 2, 3 };
                recs.Add(r);

                PMNetWriter w = new PMNetWriter();
                PMLifecycleCodec.Write(w, recs);
                byte[] bytes = w.ToArray();
                client.OnLifecycleMessage(bytes, 0, bytes.Length);

                Check(client.TotalObjectCount == 0, "H8 回滚后登记表不留强引用");
                Check(client.AllObjectSlotCount == 0, "H8b 槽位也被释放（" + client.AllObjectSlotCount + "）");
            }

            // H9 新连接补齐时只列存活对象
            {
                PMNetWorld w = NewServerWorld();
                TestConnection a = new TestConnection(1, true, "A");
                w.AddConnection(a);

                TestObject x = new TestObject();
                TestObject y = new TestObject();
                w.Spawn(x, ClassTest);
                w.Spawn(y, ClassTest);
                w.DestroyObject(y);

                TestConnection b = new TestConnection(2, true, "B");
                w.AddConnection(b);

                Check(w.PendingEventCount(b) == 1,
                    "H9 新连接只收到存活对象的创建（实际 " + w.PendingEventCount(b) + "）");
            }
        }

        // ================================================================
        // I. 声明式 Create 初值（RV5）
        // ================================================================

        private const uint ClassRepNode = 200u;

        /// <summary>RV5 夹具：带条件属性的可复制节点（描述符手工注册，不用生成器）。</summary>
        private class RepNode : PMNetObject
        {
            public int Health;    // 槽位 0，None
            public int Ammo;      // 槽位 1，OwnerOnly
            public int Secret;    // 槽位 2，Never
            public int Score;     // 槽位 3，Custom

            public int CreateCalls;
            public int HealthAtCreate = int.MinValue;
            public int AmmoAtCreate = int.MinValue;
            public int SecretAtCreate = int.MinValue;
            public int ScoreAtCreate = int.MinValue;

            protected internal override void OnReplicatedCreate()
            {
                CreateCalls++;
                HealthAtCreate = Health;
                AmmoAtCreate = Ammo;
                SecretAtCreate = Secret;
                ScoreAtCreate = Score;
            }
        }

        private static PMReplicationDescriptor MakeRepNodeDescriptor(uint classId)
        {
            PMPropertyDescriptor[] props = new PMPropertyDescriptor[4];

            props[0].PropertyId = 200; props[0].Condition = PMCond.None; props[0].MemberName = "Health";
            props[1].PropertyId = 201; props[1].Condition = PMCond.OwnerOnly; props[1].MemberName = "Ammo";
            props[2].PropertyId = 202; props[2].Condition = PMCond.Never; props[2].MemberName = "Secret";
            props[3].PropertyId = 203; props[3].Condition = PMCond.Custom; props[3].MemberName = "Score";

            for (int i = 0; i < props.Length; i++)
            {
                props[i].MaskOffset = (ushort)i;
                props[i].MaskBitCount = 1;
                props[i].PushBased = true;
                props[i].SetterName = "PMNet_Set" + props[i].MemberName;
            }

            props[0].Writer = delegate (PMNetObject t, PMNetWriter w) { w.WriteVarint((ulong)((RepNode)t).Health); };
            props[0].Reader = delegate (PMNetObject t, PMNetReader r) { ((RepNode)t).Health = (int)r.ReadVarint(); };
            props[1].Writer = delegate (PMNetObject t, PMNetWriter w) { w.WriteVarint((ulong)((RepNode)t).Ammo); };
            props[1].Reader = delegate (PMNetObject t, PMNetReader r) { ((RepNode)t).Ammo = (int)r.ReadVarint(); };
            props[2].Writer = delegate (PMNetObject t, PMNetWriter w) { w.WriteVarint((ulong)((RepNode)t).Secret); };
            props[2].Reader = delegate (PMNetObject t, PMNetReader r) { ((RepNode)t).Secret = (int)r.ReadVarint(); };
            props[3].Writer = delegate (PMNetObject t, PMNetWriter w) { w.WriteVarint((ulong)((RepNode)t).Score); };
            props[3].Reader = delegate (PMNetObject t, PMNetReader r) { ((RepNode)t).Score = (int)r.ReadVarint(); };

            PMReplicationDescriptor desc = new PMReplicationDescriptor();
            desc.ClassId = classId;
            desc.Properties = props;
            desc.ChangeMaskBitCount = props.Length;
            desc.HasConditionalMask = true;
            desc.ProtocolHash = PMStableHash.ClassProtocolHash(classId, props);
            desc.TypeName = "RepNode";
            return desc;
        }

        /// <summary>给 `TestObject`（手写钩子）注册一份描述符（两个 None 槽位）。</summary>
        private static PMReplicationDescriptor MakeHookObjectDescriptor(uint classId)
        {
            PMPropertyDescriptor[] props = new PMPropertyDescriptor[2];

            props[0].PropertyId = 300; props[0].Condition = PMCond.None; props[0].MemberName = "Hp";
            props[1].PropertyId = 301; props[1].Condition = PMCond.None; props[1].MemberName = "Mana";

            for (int i = 0; i < props.Length; i++)
            {
                props[i].MaskOffset = (ushort)i;
                props[i].MaskBitCount = 1;
                props[i].PushBased = true;
                props[i].SetterName = "PMNet_Set" + props[i].MemberName;
            }

            props[0].Writer = delegate (PMNetObject t, PMNetWriter w) { w.WriteVarint((ulong)((TestObject)t).Hp); };
            props[0].Reader = delegate (PMNetObject t, PMNetReader r) { ((TestObject)t).Hp = (int)r.ReadVarint(); };
            props[1].Writer = delegate (PMNetObject t, PMNetWriter w) { w.WriteVarint((ulong)((TestObject)t).Mana); };
            props[1].Reader = delegate (PMNetObject t, PMNetReader r) { ((TestObject)t).Mana = (int)r.ReadVarint(); };

            PMReplicationDescriptor desc = new PMReplicationDescriptor();
            desc.ClassId = classId;
            desc.Properties = props;
            desc.ChangeMaskBitCount = props.Length;
            desc.HasConditionalMask = false;
            desc.ProtocolHash = PMStableHash.ClassProtocolHash(classId, props);
            desc.TypeName = "TestObject";
            return desc;
        }

        private static void RegisterDescriptor(uint classId, PMReplicationDescriptor desc, Func<PMNetObject> factory)
        {
            PMNetClassEntry entry = new PMNetClassEntry();
            entry.ClassId = classId;
            entry.TypeName = desc.TypeName;
            entry.Rep = desc;
            entry.Factory = factory;
            PMNetRegistry.RegisterClass(entry);
        }

        private static List<PMNetLifecycleRecord> DecodeLifecycle(byte[] bytes)
        {
            List<PMNetLifecycleRecord> recs = new List<PMNetLifecycleRecord>();
            string err;
            if (!PMLifecycleCodec.TryRead(bytes, 0, bytes.Length, recs, out err))
            {
                _failures.Add("生命周期批次解码失败：" + err);
                return new List<PMNetLifecycleRecord>();
            }

            return recs;
        }

        /// <summary>从声明式初值字节里取出槽位列表（用于断言过滤结果）。</summary>
        private static List<int> RepStateSlots(byte[] repState)
        {
            List<int> slots = new List<int>();
            if (repState == null)
            {
                return slots;
            }

            PMNetReader r = new PMNetReader(repState);
            while (!r.IsAtEnd)
            {
                int slot = (int)r.ReadVarint();
                r.ReadVarint();                        // propertyId
                int len = (int)r.ReadVarint();
                r.ReadRawBytesCopy(len);
                slots.Add(slot);
            }

            slots.Sort();
            return slots;
        }

        private static string SlotsToText(List<int> slots)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < slots.Count; i++)
            {
                if (i > 0) { sb.Append(','); }
                sb.Append(slots[i]);
            }

            return "[" + sb + "]";
        }

        private static void TestDeclarativeInitialState()
        {
            PMNetRegistry.Reset();

            // I0 无复制描述符的类 ⇒ 不产声明式初值（也不产钩子初值）
            {
                PMNetWorld server = NewServerWorld();
                TestConnection a = new TestConnection(1, true, "A");
                server.AddConnection(a);

                PlainNode plain = new PlainNode();
                plain.Value = 7;
                Check(server.Spawn(plain, ClassPositioned + 50u), "I0 Spawn 一个无描述符的类");

                byte[] bytes = server.BuildLifecycleBatch(a);
                List<PMNetLifecycleRecord> recs = DecodeLifecycle(bytes);
                Check(recs.Count == 1 && recs[0].InitialState == null && recs[0].RepInitialState == null,
                    "I0b 无描述符的类不产任何初值（不凭空造属性）");
            }

            RegisterDescriptor(ClassRepNode, MakeRepNodeDescriptor(ClassRepNode),
                delegate () { return new RepNode(); });

            // I1..I6 无 channel 策略 ⇒ 只发无条件属性；初值在 OnReplicatedCreate 之前到位
            {
                PMNetWorld server = NewServerWorld();
                TestConnection a = new TestConnection(1, true, "A");
                server.AddConnection(a);

                RepNode node = new RepNode();
                node.Health = 111;
                node.Ammo = 222;
                node.Secret = 333;
                node.Score = 444;
                Check(server.Spawn(node, ClassRepNode), "I1 Spawn 成功");

                byte[] bytes = server.BuildLifecycleBatch(a);
                List<PMNetLifecycleRecord> recs = DecodeLifecycle(bytes);
                Check(recs.Count == 1 && recs[0].RepInitialState != null, "I2 Create 记录携带声明式初值");

                List<int> slots = RepStateSlots(recs.Count > 0 ? recs[0].RepInitialState : null);
                Check(slots.Count == 1 && slots[0] == 0,
                    "I3 无 channel 策略时只发无条件属性（实际 " + SlotsToText(slots) + "）");

                PMNetWorld client = NewClientWorld();
                client.RegisterClass(ClassRepNode, delegate () { return new RepNode(); });
                client.OnLifecycleMessage(bytes, 0, bytes.Length);

                PMNetObject found;
                RepNode copy = null;
                if (client.TryFind(node.NetId, out found))
                {
                    copy = found as RepNode;
                }

                Check(copy != null && copy.CreateCalls == 1, "I4 副本已创建");
                Check(copy != null && copy.HealthAtCreate == 111,
                    "I5 初值在 OnReplicatedCreate 之前到位（回调里看到 Health="
                    + (copy == null ? "<null>" : copy.HealthAtCreate.ToString()) + "）");
                Check(copy != null && copy.Ammo == 0 && copy.Secret == 0 && copy.Score == 0,
                    "I6 条件属性未被默认全量泄露（OwnerOnly/Never/Custom 均未下发）");
            }

            // I7..I9 接入条件过滤器（与 channel 同源）⇒ 按连接过滤，且 Never 永远不发
            {
                PMNetWorld server = NewServerWorld();
                TestConnection a = new TestConnection(2, true, "A");
                server.AddConnection(a);

                // 故意“全放行”，用来验证 Never 不会被过滤器绕过
                server.ReplicatedPropertyFilter = delegate (PMNetObject o, PMNetConnection c, int slot) { return true; };

                RepNode node = new RepNode();
                node.Health = 11;
                node.Ammo = 22;
                node.Secret = 33;
                node.Score = 44;
                server.Spawn(node, ClassRepNode);

                byte[] bytes = server.BuildLifecycleBatch(a);
                List<PMNetLifecycleRecord> recs = DecodeLifecycle(bytes);
                List<int> slots = RepStateSlots(recs.Count > 0 ? recs[0].RepInitialState : null);

                Check(!slots.Contains(2),
                    "I7 即使过滤器全放行，Never 属性也绝不进入 Create 初值（实际 " + SlotsToText(slots) + "）");
                Check(slots.Contains(0) && slots.Contains(1) && slots.Contains(3),
                    "I8 过滤器放行的条件属性被包含（实际 " + SlotsToText(slots) + "）");

                PMNetWorld client = NewClientWorld();
                client.RegisterClass(ClassRepNode, delegate () { return new RepNode(); });
                client.OnLifecycleMessage(bytes, 0, bytes.Length);

                PMNetObject found;
                RepNode copy = null;
                if (client.TryFind(node.NetId, out found)) { copy = found as RepNode; }

                Check(copy != null && copy.Health == 11 && copy.Ammo == 22 && copy.Score == 44,
                    "I9 过滤器放行的初值都在回调前到位");
                Check(copy != null && copy.Secret == 0, "I9b Never 属性的值确实没有下发");
            }

            // I10..I11 条件不满足的槽位不下发（OwnerOnly 对非拥有者连接）
            {
                PMNetWorld server = NewServerWorld();
                TestConnection a = new TestConnection(3, true, "A");
                server.AddConnection(a);

                server.ReplicatedPropertyFilter = delegate (PMNetObject o, PMNetConnection c, int slot) { return slot != 1; };

                RepNode node = new RepNode();
                node.Health = 5;
                node.Ammo = 6;
                server.Spawn(node, ClassRepNode);

                byte[] bytes = server.BuildLifecycleBatch(a);
                List<PMNetLifecycleRecord> recs = DecodeLifecycle(bytes);
                List<int> slots = RepStateSlots(recs.Count > 0 ? recs[0].RepInitialState : null);

                Check(!slots.Contains(1), "I10 条件不满足（OwnerOnly 对非拥有者）的槽位不进初值（实际 " + SlotsToText(slots) + "）");
                Check(slots.Contains(0), "I10b 无条件属性仍然进初值");
            }

            // I12..I14 手写钩子优先：有钩子内容就不补声明式初值（不重复/不覆盖）
            {
                PMNetRegistry.Reset();
                RegisterDescriptor(ClassTest, MakeHookObjectDescriptor(ClassTest),
                    delegate () { return new TestObject(); });

                PMNetWorld server = NewServerWorld();
                TestConnection a = new TestConnection(4, true, "A");
                server.AddConnection(a);

                TestObject hooked = new TestObject();
                hooked.Hp = 321;
                hooked.Mana = 654;
                server.Spawn(hooked, ClassTest);

                byte[] bytes = server.BuildLifecycleBatch(a);
                List<PMNetLifecycleRecord> recs = DecodeLifecycle(bytes);

                bool hookOk = recs.Count == 1 && recs[0].InitialState != null;
                if (hookOk)
                {
                    PMNetReader hr = new PMNetReader(recs[0].InitialState);
                    hookOk = (int)hr.ReadVarint() == 321 && (int)hr.ReadVarint() == 654 && hr.ReadVarint() == 0UL;
                }

                Check(hookOk, "I12 手写钩子的初始状态仍然完整写进 Create 记录（逐字段核对）");
                Check(recs.Count == 1 && recs[0].RepInitialState == null,
                    "I13 有手写钩子时不补声明式初值（不重复/不覆盖）");

                PMNetWorld client = NewClientWorld();
                client.OnLifecycleMessage(bytes, 0, bytes.Length);

                PMNetObject found;
                TestObject copy = null;
                if (client.TryFind(hooked.NetId, out found)) { copy = found as TestObject; }

                Check(copy != null && copy.Hp == 321 && copy.Mana == 654,
                    "I14 手写钩子的初始状态仍按原语义生效");
                Check(copy != null && copy.CreateCalls == 1, "I14b 创建回调仍然触发一次");
            }

            // I15..I18 声明式初值非法 ⇒ 整个创建不发布（不留半初始化对象）
            {
                PMNetRegistry.Reset();
                RegisterDescriptor(ClassRepNode, MakeRepNodeDescriptor(ClassRepNode),
                    delegate () { return new RepNode(); });

                PMNetWriter bad = new PMNetWriter(32);
                bad.WriteVarint(0UL);            // 槽位 0
                bad.WriteVarint(9999UL);         // 故意写错的 propertyId
                bad.WriteVarint(1UL);            // 1 字节值
                bad.WriteRawBytes(new byte[] { 0x05 }, 0, 1);

                List<PMNetLifecycleRecord> recs = new List<PMNetLifecycleRecord>();
                PMNetLifecycleRecord cr = new PMNetLifecycleRecord();
                cr.Kind = PMObjectEventKind.Create;
                cr.NetId = new PMNetId(900u, false);
                cr.ClassId = ClassRepNode;
                cr.RepInitialState = bad.ToArray();
                recs.Add(cr);

                PMNetWriter w = new PMNetWriter();
                PMLifecycleCodec.Write(w, recs);
                byte[] bytes = w.ToArray();

                PMNetWorld client = NewClientWorld();
                client.RegisterClass(ClassRepNode, delegate () { return new RepNode(); });
                client.OnLifecycleMessage(bytes, 0, bytes.Length);

                PMNetObject found;
                Check(!client.TryFind(900u, out found), "I15 初值非法 ⇒ 创建不发布（查询不到）");
                Check(client.ObjectCount == 0, "I15b 存活计数为 0");
                Check(client.TotalObjectCount == 0, "I15c 回滚后登记表不留强引用");
                Check(client.AllObjectSlotCount == 0, "I15d 槽位被释放（" + client.AllObjectSlotCount + "）");
                Check(client.Stats.RolledBackCreate == 1, "I16 回滚计数 = 1");
                Check(client.Stats.CreatesApplied == 0, "I16b 未计入成功创建");

                // 回滚后同一 NetId 可以重新创建（未留下脏索引）
                PMNetWriter good = new PMNetWriter(32);
                good.WriteVarint(0UL);
                good.WriteVarint(200UL);
                good.WriteVarint(1UL);
                good.WriteRawBytes(new byte[] { 0x2A }, 0, 1);

                recs.Clear();
                PMNetLifecycleRecord cr2 = new PMNetLifecycleRecord();
                cr2.Kind = PMObjectEventKind.Create;
                cr2.NetId = new PMNetId(900u, false);
                cr2.ClassId = ClassRepNode;
                cr2.RepInitialState = good.ToArray();
                recs.Add(cr2);

                PMNetWriter w2 = new PMNetWriter();
                PMLifecycleCodec.Write(w2, recs);
                byte[] bytes2 = w2.ToArray();
                client.OnLifecycleMessage(bytes2, 0, bytes2.Length);

                RepNode okNode = null;
                if (client.TryFind(900u, out found)) { okNode = found as RepNode; }

                Check(okNode != null && okNode.Health == 42, "I17 回滚后合法初值可重新创建并生效");
                Check(okNode != null && okNode.CreateCalls == 1, "I17b 创建回调这次被触发");
                Check(client.Stats.DroppedDuplicateId == 0, "I18 回滚不被误判为重复 NetId");
            }

            PMNetRegistry.Reset();
        }
    }
}
