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
                new FaultCase(
                    "F6 轮询属性（PushBased=false）必须参与调度",
                    "ShouldPollProperty 恒 false（旧实现：NeedsWork 的触发源里没有 `PushBased=false`）",
                    delegate (PMRepOptions o) { return new FaultNoPollChannel(o); },
                    new string[] { "SA4 ", "SB3 " }),
                new FaultCase(
                    "F7 可见性跃迁必须记录两个方向",
                    "IsVisibilityTransition 只认 met && !wasActive（旧实现：失去可见性不被记录）",
                    delegate (PMRepOptions o) { return new FaultGainOnlyTransitionChannel(o); },
                    new string[] { "SG3 ", "SG4 " }),
                new FaultCase(
                    "F8 不可见槽位的未知基线不算工作",
                    "ShouldWorkOnUnknownBaseline 恒 true（旧实现：不可见槽位永久占用每连接预算）",
                    delegate (PMRepOptions o) { return new FaultBaselineAlwaysWorkChannel(o); },
                    new string[] { "SE2 " }),
                new FaultCase(
                    "F9 每连接对象预算必须轮转",
                    "UseFairBudgetRotation 恒 false（旧实现：每轮从表头分配预算，前缀常驻轮询饿死后续对象）",
                    delegate (PMRepOptions o) { return new FaultPrefixBudgetChannel(o); },
                    new string[] { "SF3 " }),
                new FaultCase(
                    "F10 不可见槽位不执行 Writer",
                    "IsSlotFullyConfirmed 覆写为'先采样再判可见性'（旧实现：确认前不判可见性就执行 Writer）",
                    delegate (PMRepOptions o) { return new FaultSampleInvisibleSlotChannel(o); },
                    new string[] { "SD3 " }),
                new FaultCase(
                    "F11 旧调度语义整块还原（轮询补丁之前的实现）",
                    "五个决策点同时取旧形态：不采样 PushBased=false / 只认单向可见性跃迁 / 任何未知基线都算工作 / 表头优先 / 确认前先采样",
                    delegate (PMRepOptions o) { return new FaultOldPollingSemanticsChannel(o); },
                    new string[] { "SA4 ", "SB3 ", "SD3 ", "SE2 ", "SF3 ", "SG3 ", "SG4 " }),
                new FaultCase(
                    "F12 对象级触发源（ForceInclude / 脏位）必须按可见性过滤",
                    "ShouldFilterForceAndDirtyByVisibility 恒 false（旧实现：ForceIncludeCount>0 与 Dirty.HasAny 不分可见性，不可见槽位上的标记终身吃预算）",
                    delegate (PMRepOptions o) { return new FaultUnfilteredWorkChannel(o); },
                    new string[] { "T-a5 ", "T-a6 ", "T-b4 " }),
                new FaultCase(
                    "F13 失去可见性必须当场记住（预算顺延不能丢）",
                    "ShouldCommitVisibilityLossUpfront 恒 false（旧实现：只在进入 ScanAndAppend 时才记录，顺延的轮次把这次丢失丢掉）",
                    delegate (PMRepOptions o) { return new FaultLossOnlyOnScanChannel(o); },
                    new string[] { "T-c1-8", "T-c1-9", "T-c2-8" }),
                new FaultCase(
                    "F14 旧调度语义整块还原（本轮修复之前的实现）",
                    "两个决策点同时取旧形态：ForceInclude/脏位不分可见性 + 失去可见性只在扫描时记录",
                    delegate (PMRepOptions o) { return new FaultOldForceDirtySemanticsChannel(o); },
                    new string[] { "T-a5 ", "T-a6 ", "T-b4 ", "T-c1-8", "T-c1-9", "T-c2-8" }),
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
            Section("S. 轮询（PushBased=false）调度：可见非 Push 每轮采样 / 无变化不发 / "
                + "补不齐的不可见槽位不耗预算 / 预算轮转", TestPollScheduling);
            Section("T. 独立对抗审查：**对象级**触发源（ForceInclude/脏位）也必须按可见性过滤 / "
                + "失去可见性必须当场记住（预算顺延不能丢）/ 对象表增删下的游标 / ACK 落后与视图切换",
                TestAdversarialReview);

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

        /// <summary>
        /// 子用例隔离：一个子用例抛异常不影响同一 Section 里的其它子用例。
        /// 缺陷注入时这一点尤其重要 —— 否则"前面的子用例先炸了"会让后面那些本该抓住缺陷的
        /// 断言根本没机会执行，负向验证就会变成假阴性（看起来没抓住，其实是没跑）。
        /// </summary>
        private static void SubSection(string name, Action body)
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                _failures.Add("[" + name + "] 测试体抛异常：" + ex.GetType().Name + " " + ex.Message);
                Console.WriteLine("    FAIL " + name + " 测试体抛异常：" + ex.GetType().Name + " " + ex.Message);
            }
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

        private sealed class FaultNoPollChannel : PMReplicationChannel
        {
            public FaultNoPollChannel(PMRepOptions options)
                : base(options)
            {
            }

            protected override bool ShouldPollProperty(int slot)
            {
                // 缺陷（旧实现）：`NeedsWork` 的触发源里没有"可见且 PushBased=false"。
                // 后果：业务直接写字段（不调用 MarkPropertyDirty）的修改**永不参与比较**
                // ⇒ 该属性静默不同步，且只在"没走 PMNet_Set / MarkPropertyDirty"时才复现。
                return false;
            }
        }

        private sealed class FaultGainOnlyTransitionChannel : PMReplicationChannel
        {
            public FaultGainOnlyTransitionChannel(PMRepOptions options)
                : base(options)
            {
            }

            protected override bool IsVisibilityTransition(int slot, bool met, bool wasActive)
            {
                // 缺陷（旧实现）：只把"不满足 → 满足"当作跃迁，"满足 → 不满足"不进入扫描，
                // 于是 ConditionActive 停在 true；条件再变回满足时判不出跃迁 ⇒ 不补发。
                return met && !wasActive;
            }
        }

        private sealed class FaultBaselineAlwaysWorkChannel : PMReplicationChannel
        {
            public FaultBaselineAlwaysWorkChannel(PMRepOptions options)
                : base(options)
            {
            }

            protected override bool ShouldWorkOnUnknownBaseline(bool slotVisible)
            {
                // 缺陷（旧实现）：任何未知基线都算工作。对某条连接**永远不可见**的槽位
                // （OwnerOnly 之于非拥有者 / Never 等）永远拿不到基线 ⇒ 该对象终身每轮
                // 占用这条连接的调度预算，后面的对象被无限顺延（等价于丢弃）。
                return true;
            }
        }

        private sealed class FaultPrefixBudgetChannel : PMReplicationChannel
        {
            public FaultPrefixBudgetChannel(PMRepOptions options)
                : base(options)
            {
            }

            protected override bool UseFairBudgetRotation()
            {
                // 缺陷（旧实现）：每轮都从对象表表头分配预算。常驻轮询对象会把预算吃光，
                // 后面的对象即使有真实修改也永久顺延。
                return false;
            }
        }

        /// <summary>
        /// 把轮询调度补丁之前的**整块**调度语义一次性还原（五个决策点全部取旧形态）。
        ///
        /// 单个缺陷子类只证明"某一条断言抓得住某一个轴"；把五个轴同时还原，才能证明
        /// "旧实现在这一节里是**成片**失败的"，而不是靠某一条断言独扛
        /// —— 也就是"这些新断言确实在测旧实现没有的行为"。
        /// </summary>
        private sealed class FaultOldPollingSemanticsChannel : PMReplicationChannel
        {
            public FaultOldPollingSemanticsChannel(PMRepOptions options)
                : base(options)
            {
            }

            protected override bool ShouldPollProperty(int slot)
            {
                return false;                        // 旧：调度判据里没有 PushBased=false
            }

            protected override bool IsVisibilityTransition(int slot, bool met, bool wasActive)
            {
                return met && !wasActive;            // 旧：只认"不满足 → 满足"
            }

            protected override bool ShouldWorkOnUnknownBaseline(bool slotVisible)
            {
                return true;                         // 旧：任何未知基线都算工作
            }

            protected override bool UseFairBudgetRotation()
            {
                return false;                        // 旧：每轮从对象表表头分配预算
            }

            protected override bool IsSlotFullyConfirmed(PMRepObjectEntry entry, int slot)
            {
                SerializeSlot(entry.Object, slot);   // 旧：确认前先采样（不判可见性）
                return base.IsSlotFullyConfirmed(entry, slot);
            }
        }

        private sealed class FaultSampleInvisibleSlotChannel : PMReplicationChannel
        {
            public FaultSampleInvisibleSlotChannel(PMRepOptions options)
                : base(options)
            {
            }

            protected override bool IsSlotFullyConfirmed(PMRepObjectEntry entry, int slot)
            {
                // 缺陷（旧实现）：先取当前值（执行 Writer）再逐连接判可见性 ——
                // 于是对一个对本连接完全不可见的槽位也会采样一次它的值。
                // （`SerializeSlot` 走的就是描述符 Writer，语义与旧实现的 `SerializeProperty` 相同。）
                SerializeSlot(entry.Object, slot);
                return base.IsSlotFullyConfirmed(entry, slot);
            }
        }

        private sealed class FaultUnfilteredWorkChannel : PMReplicationChannel
        {
            public FaultUnfilteredWorkChannel(PMRepOptions options)
                : base(options)
            {
            }

            protected override bool ShouldFilterForceAndDirtyByVisibility()
            {
                // 缺陷（旧实现）：`NeedsWork` 只看 `state.ForceIncludeCount > 0` 与 `obj.Dirty.HasAny`
                // —— 二者都是**对象级**的（不区分连接），于是"对这条连接永远不可见"的槽位上挂着的
                // `ForceInclude`（典型：`Dynamic` 改写成 `Never` 后 SetDynamicCondition 留在所有
                // 连接上的标记永远清不掉）或脏位（典型：owner-only 字段服务端频繁变脏）会让该对象
                // **终身每轮**占用这条连接的调度预算。
                return false;
            }
        }

        private sealed class FaultLossOnlyOnScanChannel : PMReplicationChannel
        {
            public FaultLossOnlyOnScanChannel(PMRepOptions options)
                : base(options)
            {
            }

            protected override bool ShouldCommitVisibilityLossUpfront()
            {
                // 缺陷（旧实现）："满足 → 不满足"只在真正进入 `ScanAndAppend` 时才写进
                // `ConditionActive`；而调度预算耗尽的轮次会顺延（直接 continue），根本不进扫描
                // ⇒ 这次丢失只活在本轮的 `_condScratch` 里，下一轮被覆盖。条件在顺延期间变回
                // 满足时"不满足 → 满足"无从判定 ⇒ **漏强制补发**（值相同、无脏位时尤其明显）。
                return false;
            }
        }

        /// <summary>
        /// 把本轮修复之前的**整块**调度语义一次性还原（两个决策点同时取旧形态）。
        ///
        /// 单个缺陷子类只证明"某一条断言抓得住某一个轴"；把两个轴同时还原，才能证明
        /// "旧实现在 T 节里是**成片**失败的"，而不是靠某一条断言独扛。
        /// </summary>
        private sealed class FaultOldForceDirtySemanticsChannel : PMReplicationChannel
        {
            public FaultOldForceDirtySemanticsChannel(PMRepOptions options)
                : base(options)
            {
            }

            protected override bool ShouldFilterForceAndDirtyByVisibility()
            {
                return false;                        // 旧：ForceIncludeCount>0 / Dirty.HasAny 不分可见性
            }

            protected override bool ShouldCommitVisibilityLossUpfront()
            {
                return false;                        // 旧：失去可见性只在扫描时记录
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
            return MakeDescriptor(classId, conditions, onRepIds, null);
        }

        /// <summary>
        /// 同上，但可逐槽位指定 `PushBased`（null = 全部 Push）。
        ///
        /// 为什么必须能表达"混合"：只有让同一张描述符里同时存在 Push 与轮询槽位，
        /// 才能把"轮询真的生效"与"整层不再需要标脏"区分开 —— 后者是意图之外的退化。
        /// </summary>
        private static PMReplicationDescriptor MakeDescriptor(uint classId, PMCond[] conditions, ushort[] onRepIds, bool[] pushBased)
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
                props[i].PushBased = pushBased == null ? true : pushBased[(i < pushBased.Length) ? i : 0];
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

        /// <summary>
        /// 与 <see cref="MakeDescriptor"/> 同形，但每个槽位的 Writer 会先给 `writerCalls[slot]`
        /// 计数再写值。
        ///
        /// 用途：断言"对某条连接**不可见**的槽位不该被采样"（Writer 是业务提供的委托，
        /// 不该为一条看不到它的连接白跑一次）。用计数而不是抛异常，是为了让"多跑了一次"
        /// 与"整个链路断了"可以区分。
        /// </summary>
        private static PMReplicationDescriptor MakeCountingDescriptor(uint classId, PMCond[] conditions, int[] writerCalls)
        {
            int n = SlotNames.Length;
            PMPropertyDescriptor[] props = new PMPropertyDescriptor[n];
            bool hasConditional = false;

            for (int i = 0; i < n; i++)
            {
                int captured = i;
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
                props[i].OnRepMethodId = 0;
                props[i].PushBased = true;
                props[i].MemberName = SlotNames[i];
                props[i].SetterName = "PMNet_Set" + SlotNames[i];
                props[i].Writer = delegate (PMNetObject t, PMNetWriter w)
                {
                    writerCalls[captured]++;
                    SlotWriters[captured](t, w);
                };
                props[i].Reader = SlotReaders[i];
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
                return CreateWithDescriptor(classId, clientCount, MakeDescriptor(classId, conditions, onRepIds), options);
            }

            /// <summary>同上，但可逐槽位指定 `PushBased`（轮询调度用例需要）。</summary>
            public static Rig Create(uint classId, int clientCount, PMCond[] conditions, ushort[] onRepIds, PMRepOptions options, bool[] pushBased)
            {
                return CreateWithDescriptor(classId, clientCount, MakeDescriptor(classId, conditions, onRepIds, pushBased), options);
            }

            /// <summary>
            /// 用一张**自定义**描述符建装置（计数 Writer / 自定义 PushBased 的用例需要）。
            /// 描述符仍然经 `PMNetRegistry` 注册 —— 与生产路径一致（运行期不扫反射，只读静态表）。
            /// </summary>
            public static Rig CreateWithDescriptor(uint classId, int clientCount, PMReplicationDescriptor desc, PMRepOptions options)
            {
                PMNetRegistry.Reset();

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

        /// <summary>往已有装置里再挂一个服务端权威对象（并登记进复制层）。</summary>
        private static RepObj AddServerObject(Rig rig)
        {
            RepObj obj = new RepObj();
            if (!rig.ServerWorld.Spawn(obj, rig.ClassId))
            {
                throw new InvalidOperationException("追加对象 Spawn 失败");
            }

            rig.Sender.RegisterObject(obj);
            return obj;
        }

        /// <summary>把服务端世界里"尚未发出"的 Create 记录投给某条连接的客户端世界。</summary>
        private static void DeliverLifecycleToClient(Rig rig, Rig.Client client)
        {
            byte[] batch = rig.ServerWorld.BuildLifecycleBatch(client.Conn);
            if (batch != null && batch.Length > 0)
            {
                client.World.OnLifecycleMessage(batch, 0, batch.Length);
            }
        }

        /// <summary>在客户端世界里找某个服务端对象的副本；找不到返回 null（调用方必须判空）。</summary>
        private static RepObj FindClientCopy(Rig rig, Rig.Client client, RepObj serverObj)
        {
            if (serverObj == null || !serverObj.NetId.IsValid)
            {
                return null;
            }

            PMNetObject found;
            if (!client.World.TryFind(serverObj.NetId, out found))
            {
                return null;
            }

            return found as RepObj;
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

        /// <summary>
        /// 某连接已发出的载荷里，**指定 NetId** 那个对象出现过的全部槽位（去重、升序）。
        ///
        /// 为何必须按对象过滤：同一个测试装置里多个对象共用同一张描述符（也就共用同一批
        /// 条件属性）。断言"槽位 1 被发出"而不看 NetId，会被**另一个对象**的同类槽位满足
        /// —— 那是假阳性（尤其做缺陷注入时，会把该抓的失败掩盖掉）。
        /// </summary>
        private static List<int> SentSlotsFor(TestConn conn, uint netId, string context)
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
                    if (msg.Updates[r].NetId != netId)
                    {
                        continue;
                    }

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

        // ==================================================================================
        //  S. 轮询（PushBased=false）调度
        //
        //  `PushBased=false` 的契约是"业务直接写字段、不标脏，由复制层每轮取当前值与基线比较"。
        //  旧实现的调度判据只有 未知基线 / ForceInclude / 脏位 / 条件跃迁，**不含"可见且非 Push"**，
        //  于是这类属性永不参与比较（静默不同步）；同一处缺失还连带三个缺陷：
        //    * 永远拿不到基线的不可见槽位让对象终身占用每连接预算；
        //    * "满足 → 不满足"没被记录，重新可见时不补发（值相同、无脏位时尤其明显）；
        //    * 轮询对象常驻候选后，每轮从表头分配预算会让后面的对象永久饥饿。
        //  以下每个子用例都先用**缺陷子类**证明它抓得住对应的旧行为（见 [2] 的 F6..F10）。
        // ==================================================================================

        /// <summary>槽位 0 为轮询式（PushBased=false），其余为 Push。</summary>
        private static readonly bool[] PollOnSlot0 = new bool[] { false, true, true, true };

        private static void TestPollScheduling()
        {
            SubSection("S-a 轮询直接字段写与无变化抑制", TestPollDirectWriteAndSuppression);
            SubSection("S-b 轮询与 Push 混合", TestPollMixedWithPush);
            SubSection("S-c 全 Push 对象仍然只认脏位", TestPollPushOnlyStillNeedsDirty);
            SubSection("S-d 不可见槽位不被采样", TestPollInvisibleSlotNotSampled);
            SubSection("S-e 不可见槽位不消耗每连接预算", TestPollInvisibleSlotDoesNotConsumeBudget);
            SubSection("S-f 每连接对象预算轮转（无前缀饥饿）", TestPollBudgetRotation);
            SubSection("S-g 失去可见性被记录 / 重新可见强制补发", TestPollVisibilityLossAndRegain);
            SubSection("S-h 两条连线的轮询收敛 / 旧 ACK / 丢包", TestPollTwoConnections);
        }

        // ------------------------------------------------------------------ S-a

        private static void TestPollDirectWriteAndSuppression()
        {
            Rig rig = Rig.Create(0x5001u, 1, null, null, null, PollOnSlot0);
            Rig.Client c = rig.Clients[0];

            // 首轮：基线缺失 ⇒ 仍然全量（轮询不改变"初始状态全量"）
            rig.ServerObj.A = 1;
            rig.ServerObj.B = 2;
            rig.ServerObj.C = 0f;
            rig.ServerObj.S = string.Empty;
            long sampledBefore = rig.Sender.Stats.PollSampledSlots;
            rig.Sender.Tick();

            List<int> initial = SentSlots(c.Conn, "SA1");
            Check(initial.Count == 4, "SA1 轮询属性首轮仍然全量补齐（实际 " + SlotsToString(initial) + "）");
            Check(rig.Sender.Stats.PollSampledSlots > sampledBefore, "SA2 轮询槽位在首轮被采样（"
                  + sampledBefore + " → " + rig.Sender.Stats.PollSampledSlots + "）");

            Deliver(rig, c);
            Check(c.Obj.A == 1 && c.Obj.B == 2, "SA3 初始值到达客户端（A=1/B=2）");
            Check(!rig.ServerObj.Dirty.HasAny, "SA3b 首轮 ACK 后对象没有残留脏位（轮询不依赖脏位）");

            // ★ 核心：全部追平之后，直接写字段（**不调用 MarkPropertyDirty**）仍必须同步
            c.Conn.Sent.Clear();
            rig.ServerObj.A = 42;
            rig.Sender.Tick();
            Check(c.Conn.Sent.Count >= 1, "SA4 全部基线 ACK 且脏位清空后，轮询属性直接字段写仍被采样发送（实际 "
                  + c.Conn.Sent.Count + " 个载荷）");

            List<int> changed = SentSlots(c.Conn, "SA5");
            Check(changed.Count == 1 && changed[0] == 0, "SA5 载荷里只有被改动的轮询槽位（实际 "
                  + SlotsToString(changed) + "）");
            Deliver(rig, c);
            Check(c.Obj.A == 42, "SA6 客户端收到轮询直接改的值（实际 " + c.Obj.A + "）");

            // 值没变 ⇒ 采样了，但不发（"无变化不发"必须与"压根没调度"可区分）
            c.Conn.Sent.Clear();
            long suppressedBefore = rig.Sender.Stats.SuppressedUnchanged;
            long sampledBefore2 = rig.Sender.Stats.PollSampledSlots;
            for (int i = 0; i < 3; i++)
            {
                rig.Sender.Tick();
            }

            Check(c.Conn.Sent.Count == 0, "SA7 值没变 ⇒ 轮询采样后不发任何载荷（实际 " + c.Conn.Sent.Count + "）");
            Check(rig.Sender.Stats.PollSampledSlots > sampledBefore2, "SA8 采样确实发生了（计数增长，不是没调度；"
                  + sampledBefore2 + " → " + rig.Sender.Stats.PollSampledSlots + "）");
            Check(rig.Sender.Stats.SuppressedUnchanged > suppressedBefore, "SA9 值未变的抑制被计数（D-R0-13）");
        }

        // ------------------------------------------------------------------ S-b

        private static void TestPollMixedWithPush()
        {
            Rig rig = Rig.Create(0x5002u, 1, null, null, null, PollOnSlot0);
            Rig.Client c = rig.Clients[0];

            rig.ServerObj.A = 1;
            rig.ServerObj.B = 1;
            rig.Sender.Tick();
            Deliver(rig, c);
            Check(c.Obj.A == 1 && c.Obj.B == 1, "SB1 混合对象基线建立（A=1/B=1）");

            // Push 槽位标脏 + 改值 ⇒ 照常走 Push 路径（轮询的存在不破坏它）
            c.Conn.Sent.Clear();
            rig.ServerObj.B = 2;
            rig.ServerObj.MarkPropertyDirty(1);
            rig.Sender.Tick();
            List<int> pushSlots = SentSlots(c.Conn, "SB2");
            Check(pushSlots.Contains(1), "SB2 Push 槽位标脏 + 改值仍照常发（实际 " + SlotsToString(pushSlots) + "）");
            Deliver(rig, c);
            Check(c.Obj.B == 2, "SB2b Push 槽位送达（B=" + c.Obj.B + "）");

            // 轮询槽位改值（不标脏）⇒ 一定发
            c.Conn.Sent.Clear();
            rig.ServerObj.A = 7;
            rig.Sender.Tick();
            List<int> pollSlots = SentSlots(c.Conn, "SB3");
            Check(pollSlots.Contains(0), "SB3 轮询槽位的修改一定被带上（实际 " + SlotsToString(pollSlots) + "）");
            Check(!pollSlots.Contains(2) && !pollSlots.Contains(3),
                "SB3b 未改动且未标脏的其它槽位不被带上（实际 " + SlotsToString(pollSlots) + "）");
            Deliver(rig, c);
            Check(c.Obj.A == 7, "SB4 客户端拿到轮询槽位的新值（实际 A=" + c.Obj.A + "）");

            // Push 槽位改值但不标脏：轮询候选会让该对象**整对象扫描**，于是它可能被顺便发现。
            // 契约明确允许这一点（"不承诺数组元素绝不会因其它候选顺便被发现"），
            // 因此这里断言**实际行为**，不把"允许"写成"禁止"。
            c.Conn.Sent.Clear();
            rig.ServerObj.B = 3;
            rig.Sender.Tick();
            List<int> incidental = SentSlots(c.Conn, "SB5");
            Check(incidental.Contains(1), "SB5 轮询候选触发的整对象比较会顺便发现未标脏的 Push 变化（契约允许，实际 "
                  + SlotsToString(incidental) + "）");
            Deliver(rig, c);
            Check(c.Obj.B == 3, "SB5b 顺便发现的值同样收敛（B=" + c.Obj.B + "）");
        }

        // ------------------------------------------------------------------ S-c

        private static void TestPollPushOnlyStillNeedsDirty()
        {
            Rig rig = Rig.Create(0x5003u, 1, null, null, null);
            Rig.Client c = rig.Clients[0];

            rig.ServerObj.A = 1;
            rig.ServerObj.B = 1;
            rig.Sender.Tick();
            Deliver(rig, c);
            Check(c.Obj.B == 1, "SC1 全 Push 对象基线建立（B=1）");

            c.Conn.Sent.Clear();
            rig.ServerObj.B = 99;                    // 直接写字段，不标脏
            rig.Sender.Tick();
            Check(c.Conn.Sent.Count == 0, "SC2 全 Push 对象：直接写字段不标脏 ⇒ 不调度、不发（与轮询对照，实际 "
                  + c.Conn.Sent.Count + "）");
            Check(rig.Sender.Stats.PollSampledSlots == 0, "SC3 全 Push 对象不产生轮询采样（实际 "
                  + rig.Sender.Stats.PollSampledSlots + "）");
            Deliver(rig, c);
            Check(c.Obj.B == 1, "SC4 客户端没有拿到未标脏的 Push 值（实际 B=" + c.Obj.B + "）");
        }

        // ------------------------------------------------------------------ S-d

        private static void TestPollInvisibleSlotNotSampled()
        {
            int[] writerCalls = new int[4];
            PMReplicationDescriptor desc = MakeCountingDescriptor(0x5400u,
                new PMCond[] { PMCond.None, PMCond.OwnerOnly, PMCond.None, PMCond.None }, writerCalls);

            Rig rig = Rig.CreateWithDescriptor(0x5400u, 1, desc, null);
            Rig.Client c = rig.Clients[0];
            rig.Sender.ViewRoleResolver = delegate (PMNetObject o, PMNetConnection conn)
            {
                return PMRepViewRole.Simulated;      // 槽位 1（OwnerOnly）对这条连接不可见
            };

            rig.ServerObj.A = 1;
            rig.ServerObj.B = 5;
            rig.MarkAllDirty();
            TickAndDeliver(rig, c);

            Check(c.Obj.A == 1, "SD1 可见槽位送达（A=1）");
            Check(c.Obj.B == 0, "SD2 不可见槽位（OwnerOnly × 非拥有者）没有送到客户端（实际 B=" + c.Obj.B + "）");

            for (int i = 0; i < writerCalls.Length; i++)
            {
                writerCalls[i] = 0;
            }

            rig.ServerObj.A = 2;
            rig.ServerObj.MarkPropertyDirty(0);
            rig.ServerObj.MarkPropertyDirty(1);      // 不可见槽位也标脏（业务可能标记它）
            TickAndDeliver(rig, c);

            Check(writerCalls[1] == 0, "SD3 不可见槽位的 Writer 没有被执行（实际 " + writerCalls[1] + " 次）");
            Check(writerCalls[0] > 0, "SD3b 可见槽位的 Writer 正常执行（对照，" + writerCalls[0] + " 次）");
            Check(!rig.ServerObj.Dirty.IsDirty(1), "SD4 不可见槽位的脏位不把对象永久卡在待比较集合（已清）");
            Check(c.Obj.A == 2, "SD4b 可见槽位仍正常推进（A=" + c.Obj.A + "）");
        }

        // ------------------------------------------------------------------ S-e

        private static void TestPollInvisibleSlotDoesNotConsumeBudget()
        {
            PMRepOptions opt = new PMRepOptions();
            opt.MaxObjectsPerConnectionPerTick = 1;

            PMReplicationDescriptor desc = MakeDescriptor(0x5800u,
                new PMCond[] { PMCond.None, PMCond.OwnerOnly, PMCond.None, PMCond.None }, null);

            Rig rig = Rig.CreateWithDescriptor(0x5800u, 1, desc, opt);
            Rig.Client c = rig.Clients[0];
            rig.Sender.ViewRoleResolver = delegate (PMNetObject o, PMNetConnection conn)
            {
                return PMRepViewRole.Simulated;      // 槽位 1 的基线对这条连接永远补不齐
            };

            rig.ServerObj.A = 1;
            rig.MarkAllDirty();
            TickAndDeliver(rig, c);
            Check(!rig.ServerObj.Dirty.HasAny,
                "SE1 第一个对象已无脏位（不可见槽位不把对象卡在待比较集合，只剩补不齐的基线）");

            RepObj second = AddServerObject(rig);
            DeliverLifecycleToClient(rig, c);
            RepObj secondCopy = FindClientCopy(rig, c, second);
            Check(secondCopy != null, "SE1b 第二个对象的客户端副本已创建");

            second.A = 7;
            second.MarkPropertyDirty(0);
            for (int round = 0; round < 4; round++)
            {
                rig.Sender.Tick();
                Deliver(rig, c);
            }

            Check(secondCopy != null && secondCopy.A == 7,
                "SE1c 第二个对象的基线已建立（A=" + (secondCopy == null ? -1 : secondCopy.A) + "）");

            long deferredBefore = rig.Sender.Stats.DeferredByBudget;

            // 预算 = 1。若"永远补不齐的不可见基线"被当成工作，第一个对象会**终身**占用唯一名额，
            // 第二个对象每轮的真实修改都会被顺延（顺延无限次 = 丢弃）。这里第二个对象每轮都改，
            // 因此只要第一个对象真的在抢预算，顺延计数必然每轮 +1。
            for (int round = 0; round < 4; round++)
            {
                second.A = 100 + round;
                second.MarkPropertyDirty(0);
                rig.Sender.Tick();
                Deliver(rig, c);
            }

            Check(rig.Sender.Stats.DeferredByBudget == deferredBefore,
                "SE2 不可见槽位的未知基线不占用每连接预算（预算 1 下 4 轮 0 次顺延，实际 +"
                + (rig.Sender.Stats.DeferredByBudget - deferredBefore) + "）");
            Check(secondCopy != null && secondCopy.A == 103,
                "SE3 第二个对象每轮都被处理（A=" + (secondCopy == null ? -1 : secondCopy.A) + "）");
        }

        // ------------------------------------------------------------------ S-f

        private static void TestPollBudgetRotation()
        {
            PMRepOptions opt = new PMRepOptions();
            opt.MaxObjectsPerConnectionPerTick = 1;

            Rig rig = Rig.Create(0x5500u, 1, null, null, opt, PollOnSlot0);
            Rig.Client c = rig.Clients[0];

            // 三个"常驻轮询"对象：每个都含可见的非 Push 槽位 ⇒ 每轮都是候选。
            // 预算 = 1 时若每轮都从对象表表头开始（旧行为），表头对象会把唯一名额吃光，
            // 后面的对象即使有真实修改也永远排不上。
            RepObj[] objs = new RepObj[3];
            objs[0] = rig.ServerObj;
            for (int i = 1; i < objs.Length; i++)
            {
                objs[i] = AddServerObject(rig);
            }

            DeliverLifecycleToClient(rig, c);

            RepObj[] copies = new RepObj[objs.Length];
            bool allCopies = true;
            for (int i = 0; i < objs.Length; i++)
            {
                copies[i] = FindClientCopy(rig, c, objs[i]);
                if (copies[i] == null)
                {
                    allCopies = false;
                }
            }

            Check(allCopies, "SF1 三个对象的客户端副本都已创建");

            for (int i = 0; i < objs.Length; i++)
            {
                objs[i].A = i + 1;
            }

            for (int round = 0; round < objs.Length + 2; round++)
            {
                rig.Sender.Tick();
                Deliver(rig, c);
            }

            bool baselineOk = allCopies;
            for (int i = 0; i < objs.Length && baselineOk; i++)
            {
                if (copies[i].A != i + 1)
                {
                    baselineOk = false;
                }
            }

            Check(baselineOk, "SF2 三个对象的基线都已建立（预算 1 下每轮推进一个）");

            for (int i = 0; i < objs.Length; i++)
            {
                objs[i].A = 100 + i;                 // 轮询：不标脏
            }

            for (int round = 0; round < objs.Length + 1; round++)
            {
                rig.Sender.Tick();
                Deliver(rig, c);
            }

            bool advanced = allCopies;
            for (int i = 0; i < objs.Length && advanced; i++)
            {
                if (copies[i].A != 100 + i)
                {
                    advanced = false;
                }
            }

            string actual = "<副本缺失>";
            if (allCopies)
            {
                actual = copies[0].A + "," + copies[1].A + "," + copies[2].A;
            }

            Check(advanced, "SF3 预算 1 下三个常驻轮询对象都能在有限轮次内推进（无前缀饥饿），实际 ["
                  + actual + "]");
        }

        // ------------------------------------------------------------------ S-g

        private static void TestPollVisibilityLossAndRegain()
        {
            PMReplicationDescriptor desc = MakeDescriptor(0x5600u,
                new PMCond[] { PMCond.None, PMCond.OwnerOnly, PMCond.None, PMCond.None }, null);

            Rig rig = Rig.CreateWithDescriptor(0x5600u, 1, desc, null);
            Rig.Client c = rig.Clients[0];
            uint netId = rig.ServerObj.NetId.Value;

            PMRepViewRole role = PMRepViewRole.Autonomous;
            rig.Sender.ViewRoleResolver = delegate (PMNetObject o, PMNetConnection conn) { return role; };

            rig.ServerObj.A = 1;
            rig.ServerObj.B = 5;
            rig.MarkAllDirty();
            TickAndDeliver(rig, c);
            Check(c.Obj.B == 5, "SG1 拥有者视角下 OwnerOnly 槽位已送达（B=" + c.Obj.B + "）");

            // 失去可见性：不改值、不标脏 —— 只 Tick 一轮，让复制层"记录"这次丢失
            role = PMRepViewRole.Simulated;
            c.Conn.Sent.Clear();
            rig.Sender.Tick();
            Check(c.Conn.Sent.Count == 0, "SG2 失去可见性本身不产生载荷（实际 " + c.Conn.Sent.Count + "）");

            bool active;
            Check(rig.Sender.TryGetConditionActive(c.Conn, netId, 1, out active) && !active,
                "SG3 失去可见性被记录下来（ConditionActive=false，实际 " + active + "）");

            // 重新可见：值相同（5）、无脏位 ⇒ 契约仍要求强制补发一次当前值
            role = PMRepViewRole.Autonomous;
            c.Conn.Sent.Clear();
            long forceBefore = rig.Sender.Stats.TransitionForceSends;
            rig.Sender.Tick();

            List<int> slots = SentSlots(c.Conn, "SG4");
            Check(slots.Contains(1), "SG4 重新可见 ⇒ 强制补发（值未变、无脏位也必须补发，实际 "
                  + SlotsToString(slots) + "）");
            Check(rig.Sender.Stats.TransitionForceSends > forceBefore, "SG5 跃迁补发计数增长（"
                  + forceBefore + " → " + rig.Sender.Stats.TransitionForceSends + "）");
            Deliver(rig, c);
            Check(c.Obj.B == 5, "SG6 客户端持有当前值（B=" + c.Obj.B + "）");
        }

        // ------------------------------------------------------------------ S-h

        private static void TestPollTwoConnections()
        {
            Rig rig = Rig.Create(0x5700u, 2, null, null, null, PollOnSlot0);
            Rig.Client fast = rig.Clients[0];
            Rig.Client slow = rig.Clients[1];
            uint netId = rig.ServerObj.NetId.Value;

            rig.ServerObj.A = 1;
            rig.ServerObj.B = 1;
            rig.Sender.Tick();
            DeliverAll(rig);
            Check(fast.Obj.A == 1 && slow.Obj.A == 1, "SH1 两条连接的基线都已建立");

            // 只服务 fast：slow 的基线落后多版（轮询式，全程不标脏）
            for (int i = 2; i <= 6; i++)
            {
                rig.ServerObj.A = i * 10;
                rig.Sender.Tick();
                Deliver(rig, fast);
            }

            Check(fast.Obj.A == 60, "SH2 fast 连接已追到最新（A=" + fast.Obj.A + "）");
            Check(slow.Obj.A == 1, "SH3 slow 连接仍停在旧基线（A=" + slow.Obj.A + "）");
            Check(slow.Conn.Sent.Count > 0, "SH4 slow 的落后更新仍在待投递队列里（"
                  + slow.Conn.Sent.Count + " 个载荷）");

            // 旧 ACK：不得把"尚未追平"的连接状态当成已同步
            byte[] oldAck = MakeAckMessage(netId, 1L);
            long staleBefore = rig.Sender.Stats.StaleAckIgnored;
            rig.Sender.OnMessage(slow.Conn, oldAck, 0, oldAck.Length);
            Check(rig.Sender.Stats.StaleAckIgnored > staleBefore, "SH5 旧 ACK 被判为过期（"
                  + staleBefore + " → " + rig.Sender.Stats.StaleAckIgnored + "）");
            Check(slow.Obj.A == 1, "SH5b 旧 ACK 没有让 slow 的连接状态被当成已同步（A=" + slow.Obj.A + "）");

            // 丢包通知：去掉一条在途记录（容量归还），未确认的值仍会重发
            long[] inflight = rig.Sender.GetInflightVersions(slow.Conn, netId);
            Check(inflight.Length >= 1, "SH6 slow 侧存在在途记录（" + inflight.Length + " 条）");

            bool removed = inflight.Length >= 1 && rig.Sender.OnLoss(slow.Conn, netId, inflight[0]);
            Check(removed, "SH7 丢包通知被处理（容量归还，未确认的值仍会重发）");

            rig.Sender.Tick();
            Deliver(rig, slow);
            Check(slow.Obj.A == 60, "SH8 slow 最终收敛到最新值（A=" + slow.Obj.A + "）");
            Check(fast.Obj.A == 60, "SH9 fast 未因 slow 的旧 ACK/丢包回退（A=" + fast.Obj.A + "）");
        }
        // ==================================================================================
        //  T. 独立对抗审查（第二轮）
        //
        //  上一轮修好了"未知基线按可见性过滤"，但同一处**剩下两个缺口**：
        //    * `state.ForceIncludeCount > 0` 与 `obj.Dirty.HasAny` 仍然不分可见性 —— 二者是
        //      **对象级**的（`ForceInclude[slot]` 由 Set*Condition* 一次写给所有连接；脏位更是
        //      整对象共享）。owner-only 字段服务端频繁变脏、或 `Dynamic` 改写成 `Never` 后留下的
        //      `ForceInclude` 永远清不掉，都会让非拥有者连接**终身每轮**占预算。
        //    * "失去可见性"只在进入 `ScanAndAppend` 时才写进 `ConditionActive`；预算耗尽的轮次
        //      直接顺延、不进扫描，于是这次丢失只留在本轮的 `_condScratch` 里、下一轮被覆盖，
        //      条件在顺延期间变回满足时判不出跃迁 ⇒ **漏强制补发**。
        //  本节的每个子用例都对应一个可复现反例；F12/F13 两个缺陷子类把这两个缺口还原成旧行为，
        //  用来证明这些断言不是空断言。
        // ==================================================================================

        private static void TestAdversarialReview()
        {
            SubSection("T-a 不可见槽位上的 ForceInclude 不占预算（Dynamic→Never）", TestInvisibleForceIncludeDoesNotConsumeBudget);
            SubSection("T-b 不可见槽位的脏位不占预算（owner-only 频繁变脏）", TestInvisibleDirtyDoesNotConsumeBudget);
            SubSection("T-c1 预算顺延期间失去可见性必须被记住（同值无脏位也要补发）", TestVisibilityLossRecordedDespiteBudgetDeferral);
            SubSection("T-c2 顺延 + ACK 路径清掉脏位 ⇒ 重新可见仍必须补发（否则永久停在旧值）", TestDeferredLossWithForeignDirtyClear);
            SubSection("T-d 对象表增删（下标位移）下游标不产生永久饥饿", TestBudgetCursorUnderObjectChurn);
            SubSection("T-e ACK 落后 + 可见性切换：旧 ACK 不推进水位、重新可见收敛", TestAckLagWithVisibilitySwitch);
        }

        // ------------------------------------------------------------------ T-a

        private static void TestInvisibleForceIncludeDoesNotConsumeBudget()
        {
            PMRepOptions opt = new PMRepOptions();
            opt.MaxObjectsPerConnectionPerTick = 1;

            PMReplicationDescriptor desc = MakeDescriptor(0x6001u,
                new PMCond[] { PMCond.None, PMCond.Dynamic, PMCond.None, PMCond.None }, null);

            Rig rig = Rig.CreateWithDescriptor(0x6001u, 1, desc, opt);
            Rig.Client c = rig.Clients[0];

            rig.ServerObj.A = 1;
            rig.ServerObj.B = 5;
            rig.MarkAllDirty();
            TickAndDeliver(rig, c);
            Check(c.Obj.B == 5, "T-a1 基线建立（Dynamic 未覆盖 ⇒ 可见，B=" + c.Obj.B + "）");

            Check(rig.Sender.SetDynamicCondition(rig.ServerObj, 1, PMCond.Never),
                "T-a2 Dynamic 条件改写为 Never 被接受（对所有连接 RequireSend ⇒ ForceInclude 挂上）");

            RepObj second = AddServerObject(rig);
            DeliverLifecycleToClient(rig, c);
            RepObj secondCopy = FindClientCopy(rig, c, second);
            Check(secondCopy != null, "T-a3 第二个对象的客户端副本已创建");

            second.A = 7;
            second.MarkPropertyDirty(0);
            for (int round = 0; round < 4; round++)
            {
                rig.Sender.Tick();
                Deliver(rig, c);
            }

            Check(secondCopy != null && secondCopy.A == 7,
                "T-a4 第二个对象基线建立（A=" + (secondCopy == null ? -1 : secondCopy.A) + "）");

            long deferredBefore = rig.Sender.Stats.DeferredByBudget;
            long filteredBefore = rig.Sender.Stats.ConditionFiltered;

            for (int round = 0; round < 4; round++)
            {
                second.A = 100 + round;
                second.MarkPropertyDirty(0);
                rig.Sender.Tick();
                Deliver(rig, c);
            }

            Check(rig.Sender.Stats.DeferredByBudget == deferredBefore,
                "T-a5 挂在不可见槽位上的 ForceInclude 不占用每连接预算（预算 1 下 0 次顺延，实际 +"
                + (rig.Sender.Stats.DeferredByBudget - deferredBefore) + "）");
            Check(rig.Sender.Stats.ConditionFiltered == filteredBefore,
                "T-a6 该对象不再因不可见槽位进入扫描（ConditionFiltered 不再增长，实际 +"
                + (rig.Sender.Stats.ConditionFiltered - filteredBefore) + "）");
            Check(secondCopy != null && secondCopy.A == 103,
                "T-a7 第二个对象每轮都被处理（A=" + (secondCopy == null ? -1 : secondCopy.A) + "）");

            // 改回可见：当前值必须补发（条件跃迁 / 仍挂着的 ForceInclude 两条独立机制都不该漏）
            Check(rig.Sender.SetDynamicCondition(rig.ServerObj, 1, PMCond.None),
                "T-a8 Dynamic 改回 None（槽位重新可见）");

            c.Conn.Sent.Clear();
            long forceBefore = rig.Sender.Stats.TransitionForceSends;
            rig.Sender.Tick();
            List<int> slots = SentSlotsFor(c.Conn, rig.ServerObj.NetId.Value, "T-a9");
            Check(slots.Contains(1),
                "T-a9 槽位重新可见后当前值被补发（X 实际 " + SlotsToString(slots) + "）");
            Check(rig.Sender.Stats.TransitionForceSends > forceBefore,
                "T-a10 跃迁补发计数增长（" + forceBefore + " → " + rig.Sender.Stats.TransitionForceSends + "）");
            Deliver(rig, c);
            Check(c.Obj.B == 5, "T-a11 客户端持有当前值（B=" + c.Obj.B + "）");
        }

        // ------------------------------------------------------------------ T-b

        private static void TestInvisibleDirtyDoesNotConsumeBudget()
        {
            PMRepOptions opt = new PMRepOptions();
            opt.MaxObjectsPerConnectionPerTick = 1;

            PMReplicationDescriptor desc = MakeDescriptor(0x6002u,
                new PMCond[] { PMCond.None, PMCond.OwnerOnly, PMCond.None, PMCond.None }, null);

            Rig rig = Rig.CreateWithDescriptor(0x6002u, 1, desc, opt);
            Rig.Client c = rig.Clients[0];
            rig.Sender.ViewRoleResolver = delegate (PMNetObject o, PMNetConnection conn)
            {
                return PMRepViewRole.Simulated;      // 槽位 1（OwnerOnly）对这条连接永远不可见
            };

            rig.ServerObj.A = 1;
            rig.ServerObj.B = 5;
            rig.MarkAllDirty();
            TickAndDeliver(rig, c);
            Check(c.Obj.B == 0, "T-b1 不可见槽位从未送达（B=" + c.Obj.B + "）");

            RepObj second = AddServerObject(rig);
            DeliverLifecycleToClient(rig, c);
            RepObj secondCopy = FindClientCopy(rig, c, second);
            Check(secondCopy != null, "T-b2 第二个对象的客户端副本已创建");

            second.A = 7;
            second.MarkPropertyDirty(0);
            for (int round = 0; round < 4; round++)
            {
                rig.Sender.Tick();
                Deliver(rig, c);
            }

            Check(secondCopy != null && secondCopy.A == 7,
                "T-b3 第二个对象基线建立（A=" + (secondCopy == null ? -1 : secondCopy.A) + "）");

            long deferredBefore = rig.Sender.Stats.DeferredByBudget;

            for (int round = 0; round < 4; round++)
            {
                // 服务端权威侧频繁写 owner-only 字段并标脏（业务正常行为）
                rig.ServerObj.B = 100 + round;
                rig.ServerObj.MarkPropertyDirty(1);

                second.A = 200 + round;
                second.MarkPropertyDirty(0);
                rig.Sender.Tick();
                Deliver(rig, c);
            }

            Check(rig.Sender.Stats.DeferredByBudget == deferredBefore,
                "T-b4 不可见槽位的脏位不占用每连接预算（预算 1 下 0 次顺延，实际 +"
                + (rig.Sender.Stats.DeferredByBudget - deferredBefore) + "）");
            Check(secondCopy != null && secondCopy.A == 203,
                "T-b5 第二个对象每轮都被处理（A=" + (secondCopy == null ? -1 : secondCopy.A) + "）");
        }

        // ------------------------------------------------------------------ T-c1

        private static void TestVisibilityLossRecordedDespiteBudgetDeferral()
        {
            PMRepOptions opt = new PMRepOptions();
            opt.MaxObjectsPerConnectionPerTick = 1;

            PMReplicationDescriptor desc = MakeDescriptor(0x6003u,
                new PMCond[] { PMCond.None, PMCond.OwnerOnly, PMCond.None, PMCond.None }, null);

            Rig rig = Rig.CreateWithDescriptor(0x6003u, 1, desc, opt);
            Rig.Client c = rig.Clients[0];
            uint netId = rig.ServerObj.NetId.Value;

            PMRepViewRole role = PMRepViewRole.Autonomous;
            rig.Sender.ViewRoleResolver = delegate (PMNetObject o, PMNetConnection conn) { return role; };

            rig.ServerObj.A = 1;
            rig.ServerObj.B = 5;
            rig.MarkAllDirty();
            TickAndDeliver(rig, c);
            Check(c.Obj.B == 5, "T-c1-1 拥有者视角下基线建立（B=" + c.Obj.B + "）");

            RepObj second = AddServerObject(rig);
            DeliverLifecycleToClient(rig, c);
            RepObj secondCopy = FindClientCopy(rig, c, second);
            Check(secondCopy != null, "T-c1-2 第二个对象的客户端副本已创建");

            second.A = 1;
            second.MarkPropertyDirty(0);
            for (int round = 0; round < 4; round++)
            {
                rig.Sender.Tick();
                Deliver(rig, c);
            }

            Check(secondCopy != null && secondCopy.A == 1,
                "T-c1-3 第二个对象基线建立（A=" + (secondCopy == null ? -1 : secondCopy.A) + "）");

            // 把游标推到"下一个该处理第二个对象"：本轮只有 X 需要工作 ⇒ X 必被处理，
            // 轮转游标落到"X 之后"（= 第二个对象）。
            rig.ServerObj.A = 2;
            rig.ServerObj.MarkPropertyDirty(0);
            TickAndDeliver(rig, c);
            Check(c.Obj.A == 2, "T-c1-4 游标就位轮：X 被处理（A=" + c.Obj.A + "）");

            // 失去可见性：本轮 X 被预算顺延（起点在第二个对象）—— 这正是"丢失只留在
            // `_condScratch` 里"的触发条件。
            role = PMRepViewRole.Simulated;
            second.A = 3;
            second.MarkPropertyDirty(0);
            long deferredBefore = rig.Sender.Stats.DeferredByBudget;
            rig.Sender.Tick();

            Check(rig.Sender.Stats.DeferredByBudget > deferredBefore,
                "T-c1-5 本轮 X 被预算顺延（实际 +" + (rig.Sender.Stats.DeferredByBudget - deferredBefore) + "）");
            Check(c.Conn.Sent.Count > 0, "T-c1-6 同一轮第二个对象的更新照常发出（" + c.Conn.Sent.Count + " 个载荷）");

            Deliver(rig, c);
            Check(secondCopy != null && secondCopy.A == 3,
                "T-c1-7 第二个对象送达（A=" + (secondCopy == null ? -1 : secondCopy.A) + "）");

            bool active;
            Check(rig.Sender.TryGetConditionActive(c.Conn, netId, 1, out active) && !active,
                "T-c1-8 预算顺延没有丢掉\"失去可见性\"这个事实（ConditionActive=false，实际 " + active + "）");

            // 重新可见：值相同（5）、无脏位 ⇒ 契约仍要求强制补发一次当前值
            role = PMRepViewRole.Autonomous;
            c.Conn.Sent.Clear();
            long forceBefore = rig.Sender.Stats.TransitionForceSends;
            rig.Sender.Tick();

            List<int> slots = SentSlotsFor(c.Conn, netId, "T-c1-9");
            Check(slots.Contains(1),
                "T-c1-9 重新可见 ⇒ 强制补发（值未变、无脏位也必须补发，X 实际 " + SlotsToString(slots) + "）");
            Check(rig.Sender.Stats.TransitionForceSends > forceBefore,
                "T-c1-10 跃迁补发计数增长（" + forceBefore + " → " + rig.Sender.Stats.TransitionForceSends + "）");
            Deliver(rig, c);
            Check(c.Obj.B == 5, "T-c1-11 客户端持有当前值（B=" + c.Obj.B + "）");
        }

        // ------------------------------------------------------------------ T-c2

        private static void TestDeferredLossWithForeignDirtyClear()
        {
            PMRepOptions opt = new PMRepOptions();
            opt.MaxObjectsPerConnectionPerTick = 1;

            PMReplicationDescriptor desc = MakeDescriptor(0x6004u,
                new PMCond[] { PMCond.None, PMCond.OwnerOnly, PMCond.None, PMCond.None }, null);

            Rig rig = Rig.CreateWithDescriptor(0x6004u, 1, desc, opt);
            Rig.Client c = rig.Clients[0];

            PMRepViewRole role = PMRepViewRole.Autonomous;
            rig.Sender.ViewRoleResolver = delegate (PMNetObject o, PMNetConnection conn) { return role; };

            rig.ServerObj.A = 1;
            rig.ServerObj.B = 5;
            rig.MarkAllDirty();
            TickAndDeliver(rig, c);
            Check(c.Obj.B == 5, "T-c2-1 拥有者视角下基线建立（B=" + c.Obj.B + "）");

            RepObj second = AddServerObject(rig);
            RepObj third = AddServerObject(rig);
            DeliverLifecycleToClient(rig, c);
            RepObj secondCopy = FindClientCopy(rig, c, second);
            RepObj thirdCopy = FindClientCopy(rig, c, third);
            Check(secondCopy != null && thirdCopy != null, "T-c2-2 两个附加对象的客户端副本已创建");

            second.A = 1;
            second.MarkPropertyDirty(0);
            third.A = 1;
            third.MarkPropertyDirty(0);
            for (int round = 0; round < 6; round++)
            {
                rig.Sender.Tick();
                Deliver(rig, c);
            }

            Check(secondCopy != null && thirdCopy != null && secondCopy.A == 1 && thirdCopy.A == 1,
                "T-c2-3 三个对象基线都建立");

            // 造一条"尚未确认"的 X 记录：本轮只有 X 需要工作 ⇒ X 必被处理，游标落到 X 之后；
            // 我们**故意不投递**它的 ACK（模拟落后的确认）。
            rig.ServerObj.A = 2;
            rig.ServerObj.MarkPropertyDirty(0);
            rig.Sender.Tick();
            Check(c.Conn.Sent.Count > 0, "T-c2-4 X 的更新已发出（留作在途，稍后才投递）");

            // 失去可见性（游标起点在 second 上）⇒ X 连两轮被顺延。
            role = PMRepViewRole.Simulated;
            second.A = 2;
            second.MarkPropertyDirty(0);
            long deferredBefore = rig.Sender.Stats.DeferredByBudget;
            rig.Sender.Tick();
            Check(rig.Sender.Stats.DeferredByBudget > deferredBefore,
                "T-c2-5 X 在失去可见性这一轮被预算顺延（实际 +"
                + (rig.Sender.Stats.DeferredByBudget - deferredBefore) + "）");

            rig.ServerObj.B = 9;
            rig.ServerObj.MarkPropertyDirty(1);
            third.A = 3;
            third.MarkPropertyDirty(0);
            long deferredBefore2 = rig.Sender.Stats.DeferredByBudget;
            rig.Sender.Tick();
            Check(rig.Sender.Stats.DeferredByBudget > deferredBefore2,
                "T-c2-6 第二个顺延轮（期间 owner-only 字段被改写并标脏，实际 +"
                + (rig.Sender.Stats.DeferredByBudget - deferredBefore2) + "）");

            // 投递所有在途载荷（含 X 的 slot 0 记录）：ACK 到达后 `TryClearSettledDirty` 会把
            // "对本连接不可见"的 slot 1 脏位当成已结算而清掉 —— 这就是最狠的前提：脏位没了，
            // 只剩跃迁补偿能把这个值补上。
            Deliver(rig, c);
            Check(!rig.ServerObj.Dirty.IsDirty(1),
                "T-c2-7 对外不可见的 slot 1 脏位被 ACK 路径当作已结算清掉（构造前提）");

            // 重新可见：当前值 9 ≠ 客户端基线 5，但脏位已清、旧实现里这次丢失从未被记录
            // ⇒ 该对象对本连接再无任何触发源，客户端**永久**停在旧值。
            role = PMRepViewRole.Autonomous;
            c.Conn.Sent.Clear();
            for (int round = 0; round < 3; round++)
            {
                rig.Sender.Tick();
                Deliver(rig, c);
            }

            Check(c.Obj.B == 9,
                "T-c2-8 重新可见后 owner-only 字段收敛到最新值（实际 B=" + c.Obj.B + "）");
        }

        // ------------------------------------------------------------------ T-d

        private static void TestBudgetCursorUnderObjectChurn()
        {
            PMRepOptions opt = new PMRepOptions();
            opt.MaxObjectsPerConnectionPerTick = 1;

            Rig rig = Rig.Create(0x6005u, 1, null, null, opt, PollOnSlot0);
            Rig.Client c = rig.Clients[0];

            const int trackedCount = 3;
            RepObj[] tracked = new RepObj[trackedCount];
            tracked[0] = rig.ServerObj;
            for (int i = 1; i < trackedCount; i++)
            {
                tracked[i] = AddServerObject(rig);
            }

            DeliverLifecycleToClient(rig, c);

            RepObj[] copies = new RepObj[trackedCount];
            bool allCopies = true;
            for (int i = 0; i < trackedCount; i++)
            {
                copies[i] = FindClientCopy(rig, c, tracked[i]);
                if (copies[i] == null) { allCopies = false; }
            }

            Check(allCopies, "T-d1 三个被跟踪对象的客户端副本都已创建");

            for (int i = 0; i < trackedCount; i++) { tracked[i].A = i + 1; }
            for (int round = 0; round < trackedCount + 2; round++)
            {
                rig.Sender.Tick();
                Deliver(rig, c);
            }

            bool baselineOk = allCopies;
            for (int i = 0; i < trackedCount && baselineOk; i++)
            {
                if (copies[i].A != i + 1) { baselineOk = false; }
            }

            Check(baselineOk, "T-d2 预算 1 下三个对象基线都建立（轮转覆盖）");

            // 表扰动：每轮注销一个陪跑对象（`List.Remove` ⇒ 其后所有对象下标整体前移）、
            // 再登记一个新对象（追加到尾部）。游标是按 `_objects` 下标存的，下标位移后若规约规则
            // 出错（重置为 0 / 停在同一下标），被跟踪对象就会出现永久饥饿。
            RepObj churn = AddServerObject(rig);
            DeliverLifecycleToClient(rig, c);

            int[] lag = new int[trackedCount];
            int[] worstLag = new int[trackedCount];

            for (int round = 0; round < 10; round++)
            {
                for (int i = 0; i < trackedCount; i++)
                {
                    tracked[i].A = 1000 + round;      // 轮询式：不标脏
                }

                rig.Sender.UnregisterObject(churn);
                churn = AddServerObject(rig);
                DeliverLifecycleToClient(rig, c);

                rig.Sender.Tick();
                Deliver(rig, c);

                for (int i = 0; i < trackedCount; i++)
                {
                    if (copies[i].A == 1000 + round)
                    {
                        lag[i] = 0;
                    }
                    else
                    {
                        lag[i]++;
                        if (lag[i] > worstLag[i]) { worstLag[i] = lag[i]; }
                    }
                }
            }

            int worst = 0;
            for (int i = 0; i < trackedCount; i++)
            {
                if (worstLag[i] > worst) { worst = worstLag[i]; }
            }

            Check(worst <= 6,
                "T-d3 对象表增删（下标整体位移）后无永久饥饿：被跟踪对象最长连续顺延 " + worst
                + " 轮（预算 1、表长 4..5，实际 " + worstLag[0] + "/" + worstLag[1] + "/" + worstLag[2] + "）");

            for (int round = 0; round < 10; round++)
            {
                for (int i = 0; i < trackedCount; i++) { tracked[i].A = 4242; }
                rig.Sender.Tick();
                Deliver(rig, c);
            }

            bool converged = allCopies;
            for (int i = 0; i < trackedCount && converged; i++)
            {
                if (copies[i].A != 4242) { converged = false; }
            }

            Check(converged, "T-d4 扰动结束后全部被跟踪对象最终收敛（A=4242）");
        }

        // ------------------------------------------------------------------ T-e

        private static void TestAckLagWithVisibilitySwitch()
        {
            PMReplicationDescriptor desc = MakeDescriptor(0x6006u,
                new PMCond[] { PMCond.None, PMCond.OwnerOnly, PMCond.None, PMCond.None }, null);

            Rig rig = Rig.CreateWithDescriptor(0x6006u, 2, desc, null);
            Rig.Client fast = rig.Clients[0];
            Rig.Client slow = rig.Clients[1];
            uint netId = rig.ServerObj.NetId.Value;

            PMRepViewRole slowRole = PMRepViewRole.Autonomous;
            rig.Sender.ViewRoleResolver = delegate (PMNetObject o, PMNetConnection conn)
            {
                return ReferenceEquals(conn, slow.Conn) ? slowRole : PMRepViewRole.Autonomous;
            };

            rig.ServerObj.A = 1;
            rig.ServerObj.B = 5;
            rig.MarkAllDirty();
            rig.Sender.Tick();
            DeliverAll(rig);
            Check(fast.Obj.B == 5 && slow.Obj.B == 5,
                "T-e1 两条连接的 owner-only 基线都建立（fast=" + fast.Obj.B + " slow=" + slow.Obj.B + "）");

            // slow 失去可见性；它的 ACK 全程落后（不投递）。
            slowRole = PMRepViewRole.Simulated;
            rig.Sender.Tick();
            Deliver(rig, fast);

            rig.ServerObj.B = 9;
            rig.ServerObj.MarkPropertyDirty(1);
            rig.Sender.Tick();
            Deliver(rig, fast);
            Check(fast.Obj.B == 9, "T-e2 fast 连接拿到新值（B=" + fast.Obj.B + "）");
            Check(slow.Obj.B == 5, "T-e3 slow 连接（不可见）没拿到新值（B=" + slow.Obj.B + "）");

            byte[] oldAck = MakeAckMessage(netId, 1L);
            long staleBefore = rig.Sender.Stats.StaleAckIgnored;
            rig.Sender.OnMessage(slow.Conn, oldAck, 0, oldAck.Length);
            Check(rig.Sender.Stats.StaleAckIgnored > staleBefore,
                "T-e4 落后连接上的旧 ACK 被判过期（不推进确认水位）");

            // 重新可见：slow 的基线仍是 5、值已改成 9、脏位已被 fast 的 ACK 结清
            // ⇒ 只能靠跃迁补偿把值补上。
            slowRole = PMRepViewRole.Autonomous;
            slow.Conn.Sent.Clear();
            long forceBefore = rig.Sender.Stats.TransitionForceSends;
            rig.Sender.Tick();

            List<int> slots = SentSlotsFor(slow.Conn, netId, "T-e5");
            Check(slots.Contains(1),
                "T-e5 slow 重新可见 ⇒ 强制补发 owner-only 当前值（实际 " + SlotsToString(slots) + "）");
            Check(rig.Sender.Stats.TransitionForceSends > forceBefore,
                "T-e6 跃迁补发计数增长（" + forceBefore + " → " + rig.Sender.Stats.TransitionForceSends + "）");

            Deliver(rig, slow);
            Check(slow.Obj.B == 9, "T-e7 slow 收敛到最新值（B=" + slow.Obj.B + "）");
            Check(fast.Obj.B == 9, "T-e8 fast 未因 slow 的旧 ACK/落后回退（B=" + fast.Obj.B + "）");
        }
    }
}
