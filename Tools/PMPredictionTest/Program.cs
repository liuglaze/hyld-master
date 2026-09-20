using System;
using System.Collections.Generic;
using System.Reflection;
using PMNet;
using PMNet.Prediction;

namespace PMPredictionTest
{
    // =====================================================================================
    // 测试用最小模型：Input / Sync / Aux 三层，含数组字段（用于深克隆证据）。
    // 公式刻意极简，便于独立手算 oracle。
    // =====================================================================================

    internal sealed class TestInput
    {
        public float Dx;
        public float Dz;
        public float YawRequest;
        public int Mode;              // 0 = 保持
        public int Grounded;          // -1 = 保持
        public int AuxGravity;        // 0 = 保持
        public int AuxWorldVersion;   // 0 = 保持
        public int EventKey;          // 0 = 不发事件（非 0 时故意发两次同一 key）
        public int EventKey2;         // 0 = 不发
        public int EventKind;
        public int[] Tokens;
    }

    internal sealed class TestSync
    {
        public float X;
        public float Z;
        public float Yaw;
        public float Vx;
        public float Vz;
        public int Mode;
        public int Grounded;
        public int[] Layers;
    }

    internal sealed class TestAux
    {
        public int GravityCm;
        public int WorldVersion;
        public int[] Versions;
    }

    internal class TestModel : IPMPredictionModel<TestInput, TestSync, TestAux>
    {
        public const float PositionTolerance = 0.05f;   // 项目首批：5cm
        public const float VelocityTolerance = 0.01f;   // 项目首批：1cm/s
        public const float YawToleranceDegrees = 1f;    // 项目首批：1 度

        public int SimulateCount;
        public bool MutateArguments;
        public bool EmitModeEvents;
        public readonly List<int> StepMsLog = new List<int>();
        public readonly List<bool> ResimLog = new List<bool>();
        public readonly List<long> InputFrameLog = new List<long>();
        public readonly List<int> SeenToken0 = new List<int>();

        public virtual TestInput CloneInput(TestInput input)
        {
            TestInput copy = new TestInput();
            copy.Dx = input.Dx;
            copy.Dz = input.Dz;
            copy.YawRequest = input.YawRequest;
            copy.Mode = input.Mode;
            copy.Grounded = input.Grounded;
            copy.AuxGravity = input.AuxGravity;
            copy.AuxWorldVersion = input.AuxWorldVersion;
            copy.EventKey = input.EventKey;
            copy.EventKey2 = input.EventKey2;
            copy.EventKind = input.EventKind;
            copy.Tokens = CopyInts(input.Tokens);
            return copy;
        }

        public virtual TestSync CloneSync(TestSync sync)
        {
            TestSync copy = new TestSync();
            copy.X = sync.X;
            copy.Z = sync.Z;
            copy.Yaw = sync.Yaw;
            copy.Vx = sync.Vx;
            copy.Vz = sync.Vz;
            copy.Mode = sync.Mode;
            copy.Grounded = sync.Grounded;
            copy.Layers = CopyInts(sync.Layers);
            return copy;
        }

        public virtual TestAux CloneAux(TestAux aux)
        {
            TestAux copy = new TestAux();
            copy.GravityCm = aux.GravityCm;
            copy.WorldVersion = aux.WorldVersion;
            copy.Versions = CopyInts(aux.Versions);
            return copy;
        }

        public virtual PMSimulationResult<TestSync, TestAux> Simulate(PMTimeStep step, TestInput input, TestSync start, TestAux aux)
        {
            SimulateCount++;
            StepMsLog.Add((int)step.StepMs);
            ResimLog.Add(step.IsResimulating);
            InputFrameLog.Add(step.ClientInputFrame.IsValid ? step.ClientInputFrame.Value : -1L);
            SeenToken0.Add(input.Tokens == null ? -1 : input.Tokens[0]);

            if (MutateArguments)
            {
                // 故意违反契约：模型不得改写入参。时间轴必须让历史不受影响。
                if (input.Tokens != null && input.Tokens.Length > 0) { input.Tokens[0] = 999; }
                if (start.Layers != null && start.Layers.Length > 0) { start.Layers[0] = 999; }
            }

            TestSync sync = CloneSync(start);
            sync.X = start.X + input.Dx * step.StepMs;
            sync.Z = start.Z + input.Dz * step.StepMs;
            sync.Vx = input.Dx;
            sync.Vz = input.Dz;
            if (input.Mode != 0)
            {
                sync.Mode = input.Mode;
                sync.Yaw = input.YawRequest;
                sync.Layers = new int[] { input.Mode };
            }

            if (input.Grounded >= 0)
            {
                sync.Grounded = input.Grounded;
            }

            TestAux auxOut = CloneAux(aux);
            if (input.AuxGravity != 0) { auxOut.GravityCm = input.AuxGravity; }
            if (input.AuxWorldVersion != 0) { auxOut.WorldVersion = input.AuxWorldVersion; }

            PMSimulationResult<TestSync, TestAux> result = new PMSimulationResult<TestSync, TestAux>();
            result.Sync = sync;
            result.Aux = auxOut;

            List<PMPredictionEvent> events = new List<PMPredictionEvent>();
            if (input.EventKey != 0)
            {
                // 同一帧同一 key 故意发两次：门禁必须去重成一次。
                events.Add(new PMPredictionEvent((ulong)input.EventKey, input.EventKind, 1));
                events.Add(new PMPredictionEvent((ulong)input.EventKey, input.EventKind, 1));
            }

            if (input.EventKey2 != 0)
            {
                events.Add(new PMPredictionEvent((ulong)input.EventKey2, input.EventKind, 2));
            }

            if (EmitModeEvents && sync.Mode != 0)
            {
                // 依赖“起点状态”的事件：回滚替换同帧事件集合时它的值会变。
                events.Add(new PMPredictionEvent((ulong)(4000 + sync.Mode), 2, sync.Mode));
            }

            result.Events = events.ToArray();
            return result;
        }

        public virtual bool ShouldReconcile(TestSync predicted, TestSync authority, TestAux predictedAux, TestAux authorityAux)
        {
            if (predicted.Mode != authority.Mode) { return true; }
            if (predicted.Grounded != authority.Grounded) { return true; }
            if (Math.Abs(predicted.X - authority.X) > PositionTolerance) { return true; }
            if (Math.Abs(predicted.Z - authority.Z) > PositionTolerance) { return true; }
            if (Math.Abs(predicted.Vx - authority.Vx) > VelocityTolerance) { return true; }
            if (Math.Abs(predicted.Vz - authority.Vz) > VelocityTolerance) { return true; }
            if (Math.Abs(ShortestYawDelta(predicted.Yaw, authority.Yaw)) > YawToleranceDegrees) { return true; }
            if (!SameInts(predicted.Layers, authority.Layers)) { return true; }
            if (predictedAux.GravityCm != authorityAux.GravityCm) { return true; }
            if (predictedAux.WorldVersion != authorityAux.WorldVersion) { return true; }
            if (!SameInts(predictedAux.Versions, authorityAux.Versions)) { return true; }
            return false;
        }

        public virtual TestSync Interpolate(TestSync from, TestSync to, float alpha)
        {
            TestSync outSync = new TestSync();
            // 离散量：Snap 取 To。
            outSync.Mode = to.Mode;
            outSync.Grounded = to.Grounded;
            outSync.Layers = CopyInts(to.Layers);
            // 连续量：lerp；yaw 走最短弧。
            outSync.X = from.X + (to.X - from.X) * alpha;
            outSync.Z = from.Z + (to.Z - from.Z) * alpha;
            outSync.Vx = from.Vx + (to.Vx - from.Vx) * alpha;
            outSync.Vz = from.Vz + (to.Vz - from.Vz) * alpha;
            outSync.Yaw = NormalizeYaw(from.Yaw + ShortestYawDelta(from.Yaw, to.Yaw) * alpha);
            return outSync;
        }

        public static float ShortestYawDelta(float from, float to)
        {
            float delta = to - from;
            delta = (float)(((delta + 180.0) % 360.0 + 360.0) % 360.0 - 180.0);
            return delta;
        }

        public static float NormalizeYaw(float yaw)
        {
            return (float)(((yaw + 180.0) % 360.0 + 360.0) % 360.0 - 180.0);
        }

        public static int[] CopyInts(int[] source)
        {
            if (source == null) { return null; }
            int[] copy = new int[source.Length];
            Array.Copy(source, copy, source.Length);
            return copy;
        }

        public static bool SameInts(int[] a, int[] b)
        {
            if (ReferenceEquals(a, b)) { return true; }
            if (a == null || b == null) { return false; }
            if (a.Length != b.Length) { return false; }
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) { return false; }
            }

            return true;
        }
    }

    /// <summary>负面模型：返回 null Sync（违反契约）。</summary>
    internal sealed class NullSyncModel : TestModel
    {
        public override PMSimulationResult<TestSync, TestAux> Simulate(PMTimeStep step, TestInput input, TestSync start, TestAux aux)
        {
            PMSimulationResult<TestSync, TestAux> result = base.Simulate(step, input, start, aux);
            result.Sync = null;
            return result;
        }
    }

    /// <summary>负面模型：返回 null 结果（违反契约）。</summary>
    internal sealed class NullResultModel : TestModel
    {
        public override PMSimulationResult<TestSync, TestAux> Simulate(PMTimeStep step, TestInput input, TestSync start, TestAux aux)
        {
            return null;
        }
    }

    // =====================================================================================

    internal static class Program
    {
        private const uint Epoch = 7u;
        private const uint Instance = 11u;

        private static int _passed;
        private static readonly List<string> _failures = new List<string>();

        private static int Main()
        {
            Console.WriteLine("=== R4-A P4A1/P4A2：预测核心契约矩阵（PMPredictionTimeline / PMAuthorityInputBuffer / PMInterpolationBuffer）===");
            Console.WriteLine("预期值全部由独立手算给出；不引用实现自身的常量作为期望。");
            Console.WriteLine();

            SectionA_FrameCorrespondence();
            SectionB_CloneIsolation();
            SectionC_AuthorityReconcile();
            SectionD_ConfirmedEvents();
            SectionL_AuthorityEventEvidence();
            SectionE_HistoryBoundsAndResync();
            SectionF_DisconnectFreezeWallClock();
            SectionG_RoleAndFrameNamespace();
            SectionH_SpInterpolation();
            SectionI_AuthorityInputBuffer();
            SectionJ_BufferTimelineIntegration();
            SectionK_ContractEnforcement();
            SectionM_InitialSnapshotConstruction();
            SectionN_PendingServerFrameMetadata();

            Console.WriteLine();
            Console.WriteLine("================ 结果 ================");
            Console.WriteLine("通过: " + _passed);
            Console.WriteLine("失败: " + _failures.Count);
            for (int i = 0; i < _failures.Count; i++)
            {
                Console.WriteLine("  [FAIL] " + _failures[i]);
            }

            return _failures.Count == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ 框架

        private static void Section(string title)
        {
            Console.WriteLine("---- " + title);
        }

        private static void Check(string name, bool ok)
        {
            if (ok) { _passed++; }
            else { _failures.Add(name); }
        }

        private static void CheckEq(string name, long expected, long actual)
        {
            Check(name + "（期望 " + expected + "，实际 " + actual + "）", expected == actual);
        }

        /// <summary>
        /// 断言一段代码**不抛异常**。
        ///
        /// 用于「缺陷注入时必须失败，而不是让门禁自己崩掉」：若被测路径直接抛异常，
        /// 这里会把它记成一条普通 FAIL，保证结果汇总与退出码仍然可读。
        /// </summary>
        private static void CheckNoThrow(string name, Action body)
        {
            try
            {
                body();
                _passed++;
            }
            catch (Exception ex)
            {
                _failures.Add(name + "（抛出了 " + ex.GetType().Name + "：" + ex.Message + "）");
            }
        }

        private static void CheckEqF(string name, double expected, double actual, double tolerance)
        {
            Check(name + "（期望 " + expected.ToString("0.######") + "，实际 " + actual.ToString("0.######") + "）",
                Math.Abs(expected - actual) <= tolerance);
        }

        private static void CheckEqEnum<T>(string name, T expected, T actual) where T : struct
        {
            Check(name + "（期望 " + expected + "，实际 " + actual + "）", expected.Equals(actual));
        }

        private static void CheckThrows<T>(string name, Action action) where T : Exception
        {
            try
            {
                action();
                _failures.Add(name + "（未抛出 " + typeof(T).Name + "）");
            }
            catch (T)
            {
                _passed++;
            }
            catch (Exception ex)
            {
                _failures.Add(name + "（抛出了 " + ex.GetType().Name + "，期望 " + typeof(T).Name + "）");
            }
        }

        // ------------------------------------------------------------------ 构造助手

        private static TestSync MakeSync()
        {
            TestSync sync = new TestSync();
            sync.Layers = new int[0];
            return sync;
        }

        private static TestAux MakeAux()
        {
            TestAux aux = new TestAux();
            aux.GravityCm = -981;
            aux.WorldVersion = 1;
            aux.Versions = new int[0];
            return aux;
        }

        private static TestInput InputOf(float dx, float dz)
        {
            TestInput input = new TestInput();
            input.Dx = dx;
            input.Dz = dz;
            input.Grounded = -1;
            input.Tokens = new int[] { 7 };
            return input;
        }

        private static PMPredictionTimeline<TestInput, TestSync, TestAux> NewTimeline(
            TestModel model,
            int capacity = 128,
            PMNetRole role = PMNetRole.AutonomousProxy,
            long disconnectTimeoutMs = 2000L,
            long futureWindow = 128L)
        {
            return new PMPredictionTimeline<TestInput, TestSync, TestAux>(
                model, Epoch, Instance, MakeSync(), MakeAux(), role, capacity, disconnectTimeoutMs, futureWindow);
        }

        private static PMFrameId InputFrame(long value)
        {
            return new PMFrameId(PMFrameDomain.Input, value);
        }

        private static PMFrameId ServerFrame(long value)
        {
            return new PMFrameId(PMFrameDomain.AuthorityServer, value);
        }

        /// <summary>以时间轴在 boundary 的预测状态为底，构造权威快照（可再改字段）。</summary>
        private static PMPredictionSnapshot<TestSync, TestAux> AuthFrom(
            PMPredictionTimeline<TestInput, TestSync, TestAux> timeline,
            long boundary,
            double totalSimTimeMs,
            Action<TestSync> editSync,
            Action<TestAux> editAux,
            PMPredictionEventFrame[] evidenceFrames = null,
            PMPredictionEvent[] evidence = null)
        {
            PMPredictionSnapshot<TestSync, TestAux> snapshot;
            if (!timeline.TryGetBoundarySnapshot(InputFrame(boundary), out snapshot))
            {
                throw new InvalidOperationException("测试构造失败：边界 " + boundary + " 不在窗口内。");
            }

            snapshot.ServerFrame = ServerFrame(boundary * 10);
            snapshot.TotalSimTimeMs = totalSimTimeMs;
            if (editSync != null) { editSync(snapshot.Sync); }
            if (editAux != null) { editAux(snapshot.Aux); }
            snapshot.ConfirmedEventFrames = evidenceFrames;
            snapshot.ConfirmedEvents = evidence;
            return snapshot;
        }

        private static TestSync BoundarySync(PMPredictionTimeline<TestInput, TestSync, TestAux> timeline, long boundary)
        {
            PMPredictionSnapshot<TestSync, TestAux> snapshot;
            if (!timeline.TryGetBoundarySnapshot(InputFrame(boundary), out snapshot))
            {
                throw new InvalidOperationException("测试读取失败：边界 " + boundary + " 不在窗口内。");
            }

            return snapshot.Sync;
        }

        // 安全读取助手：缺陷注入时断言应当失败，而不是让门禁自己抛异常
        private static long StepMsAt(TestModel model, int index)
        {
            return index >= 0 && index < model.StepMsLog.Count ? model.StepMsLog[index] : -1L;
        }

        private static bool ResimAt(TestModel model, int index)
        {
            return index >= 0 && index < model.ResimLog.Count && model.ResimLog[index];
        }

        private static long InputFrameAt(TestModel model, int index)
        {
            return index >= 0 && index < model.InputFrameLog.Count ? model.InputFrameLog[index] : -1L;
        }

        private static int TokenAt(TestModel model, int index)
        {
            return index >= 0 && index < model.SeenToken0.Count ? model.SeenToken0[index] : -1;
        }

        private static int LayerAt(TestSync sync, int index)
        {
            if (sync == null || sync.Layers == null || index < 0 || index >= sync.Layers.Length) { return int.MinValue; }
            return sync.Layers[index];
        }

        private static void SetLayer(TestSync sync, int index, int value)
        {
            if (sync != null && sync.Layers != null && index >= 0 && index < sync.Layers.Length) { sync.Layers[index] = value; }
        }

        private static ulong EventKeyAt(List<PMPredictionEvent> events, int index)
        {
            return index >= 0 && index < events.Count ? events[index].Key : 0UL;
        }

        private static long PendingInputAt(List<PMAuthorityInput<TestInput>> steps, int index)
        {
            return index >= 0 && index < steps.Count ? steps[index].InputFrame : -1L;
        }

        private static string AdvanceAt(List<string> advances, int index)
        {
            return index >= 0 && index < advances.Count ? advances[index] : "<缺失>";
        }

        // ==================================================================================
        // M. 可信完整快照构造（OutputFrame > 0）：正规初始化与非法快照拒绝
        // ==================================================================================

        private static PMPredictionSnapshot<TestSync, TestAux> MakeInitialSnapshot(long outputFrame)
        {
            PMPredictionSnapshot<TestSync, TestAux> snapshot = new PMPredictionSnapshot<TestSync, TestAux>();
            snapshot.Epoch = Epoch;
            snapshot.InstanceId = Instance;
            snapshot.OutputFrame = InputFrame(outputFrame);
            snapshot.ServerFrame = ServerFrame(outputFrame * 10L);
            snapshot.TotalSimTimeMs = outputFrame * 16.0;
            snapshot.Sync = MakeSync();
            snapshot.Aux = MakeAux();
            return snapshot;
        }

        /// <summary>
        /// M. 快照构造的非零边界：基态边界必须与输出边界一次写齐（构造后立即可读 + 可 Tick）；
        ///    非法初始快照必须在构造期 fail-fast（不允许留下半初始化状态）。
        ///
        /// 旧缺陷（本轮修复）：快照构造函数只写 _pendingFrame/_confirmedFrame，不写 _boundaryStartFrame。
        /// 基态停在 0，而读取走 boundary - 1 - _boundaryStartFrame ⇒ OutputFrame > 0 时**首次读取**
        /// 即抛「读取边界越界（boundary=7）」。边界 0 的快照恰好看不出问题，因此需要非零用例。
        /// </summary>
        private static void SectionM_InitialSnapshotConstruction()
        {
            Section("M. 快照构造：非 0 输出边界的基态写齐（构造后立即可读 + 可 Tick）；非法快照构造期 fail-fast");

            // ---- M1：OutputFrame = 7 的可信快照 ----
            PMPredictionSnapshot<TestSync, TestAux> initial = MakeInitialSnapshot(7L);
            initial.Sync.X = 4.5f;
            initial.Sync.Z = -1.25f;
            initial.Sync.Yaw = 30f;
            initial.Aux.WorldVersion = 3;
            initial.TotalSimTimeMs = 112.0;

            TestModel model = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> timeline = null;

            // 旧缺陷（本轮修复）在这里直接抛「读取边界越界（boundary=7）」，因此先把它包成一条可读的 FAIL。
            CheckNoThrow("非 0 边界快照构造后立即可读（旧缺陷在此抛读取边界越界）", delegate
            {
                timeline = new PMPredictionTimeline<TestInput, TestSync, TestAux>(
                    model, initial, PMNetRole.AutonomousProxy);
                timeline.GetSyncSnapshot();
            });

            if (timeline != null)
            {
                // 整个读取/推进块都包在 no-throw 断言里：上述缺陷注入时它必须记成 FAIL，
                // 而不是让门禁自己抛异常、丢掉结果汇总。
                CheckNoThrow("非 0 边界快照的全部读取与推进都不抛异常", delegate
                {
                CheckEq("快照构造后 PendingFrame = 快照边界", 7L, timeline.PendingFrame.Value);
                CheckEq("快照构造后 ConfirmedFrame = 快照边界", 7L, timeline.ConfirmedFrame.Value);
                CheckEq("快照构造后最旧保留边界 = 快照边界（基态与读取边界一致）", 7L,
                    timeline.OldestRetainedBoundaryFrame.Value);
                CheckEq("快照构造后保留输入条数 = 0", 0L, timeline.RetainedInputCount);
                Check("快照构造不算「已接纳权威快照」（首份权威快照仍可校正该边界）", !timeline.HasAcceptedAuthority);
                CheckEqEnum("快照构造后角色仍为 AutonomousProxy", PMNetRole.AutonomousProxy, timeline.Role);

                // 基态读取必须直接可用（旧缺陷在这里抛越界）。
                TestSync baseSync = timeline.GetSyncSnapshot();
                CheckEqF("快照构造后读到快照 Sync.X", 4.5, baseSync.X, 1e-6);
                CheckEqF("快照构造后读到快照 Sync.Z", -1.25, baseSync.Z, 1e-6);
                CheckEqF("快照构造后读到快照 Sync.Yaw", 30.0, baseSync.Yaw, 1e-6);
                CheckEq("快照构造后读到快照 Aux.WorldVersion", 3L, timeline.GetAuxSnapshot().WorldVersion);
                CheckEqF("快照构造后累计仿真时间取快照值", 112.0, timeline.CurrentTotalSimTimeMs, 1e-6);

                PMPredictionSnapshot<TestSync, TestAux> built = timeline.GetSnapshot();
                CheckEq("快照构造后 GetSnapshot 的 OutputFrame 保持 7", 7L, built.OutputFrame.Value);
                CheckEq("快照构造后 GetSnapshot 的 ServerFrame 取快照值", 70L, built.ServerFrame.Value);

                PMPredictionSnapshot<TestSync, TestAux> baseRead;
                bool readBase = timeline.TryGetBoundarySnapshot(InputFrame(7L), out baseRead);
                Check("快照构造后最旧边界可读（不越界）", readBase);
                CheckEqF("快照构造后最旧边界读回的是快照值", 4.5, baseRead == null ? 0.0 : baseRead.Sync.X, 1e-6);

                // 构造后立即 Tick：输入帧 7 → 输出边界 8，且以快照基态为起点继续积分。
                PMTimeStep step = new PMTimeStep();
                step.BaseSimTimeMs = 0L;
                step.StepMs = 16f;
                step.ServerFrame = ServerFrame(70L);
                step.ClientInputFrame = InputFrame(7L);
                step.IsResimulating = false;

                PMTickResult tick = timeline.Tick(step, InputOf(0.5f, 0f));
                Check("快照构造后立即 Tick 被接纳", tick.Accepted && tick.Simulated && !tick.Placeholder);
                CheckEq("快照构造后 Tick 推进到边界 8", 8L, timeline.PendingFrame.Value);
                CheckEqF("Tick 从快照基态继续积分（X = 4.5 + 0.5 * 16）", 12.5, timeline.GetSyncSnapshot().X, 1e-4);
                CheckEqF("Tick 从快照基态累计仿真时间（112 + 16）", 128.0, timeline.CurrentTotalSimTimeMs, 1e-4);
                CheckEq("快照构造后 Tick 调用了模型（模拟步数 1）", 1L, timeline.SimulatedSteps);
                });
            }

            // 边界 0 的快照仍然照常工作（不能因为修非零路径而破坏零路径）。
            PMPredictionSnapshot<TestSync, TestAux> zero = MakeInitialSnapshot(0L);
            TestModel zeroModel = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> zeroTimeline =
                new PMPredictionTimeline<TestInput, TestSync, TestAux>(zeroModel, zero, PMNetRole.AutonomousProxy);
            CheckEq("边界 0 快照构造后 PendingFrame = 0", 0L, zeroTimeline.PendingFrame.Value);
            CheckEq("边界 0 快照构造后最旧保留边界 = 0", 0L, zeroTimeline.OldestRetainedBoundaryFrame.Value);
            PMTimeStep zeroStep = new PMTimeStep();
            zeroStep.StepMs = 16f;
            zeroStep.ClientInputFrame = InputFrame(0L);
            zeroStep.ServerFrame = ServerFrame(0L);
            Check("边界 0 快照构造后立即 Tick 被接纳", zeroTimeline.Tick(zeroStep, InputOf(1f, 0f)).Accepted);

            // ---- M2：非法初始快照必须构造期 fail-fast ----
            CheckThrows<ArgumentNullException>("空快照必须抛 ArgumentNullException", delegate
            {
                new PMPredictionTimeline<TestInput, TestSync, TestAux>(
                    new TestModel(), (PMPredictionSnapshot<TestSync, TestAux>)null, PMNetRole.AutonomousProxy);
            });

            CheckThrows<InvalidOperationException>("epoch = 0 的快照必须被拒", delegate
            {
                PMPredictionSnapshot<TestSync, TestAux> bad = MakeInitialSnapshot(3L);
                bad.Epoch = 0u;
                new PMPredictionTimeline<TestInput, TestSync, TestAux>(new TestModel(), bad, PMNetRole.AutonomousProxy);
            });

            CheckThrows<InvalidOperationException>("instance = 0 的快照必须被拒", delegate
            {
                PMPredictionSnapshot<TestSync, TestAux> bad = MakeInitialSnapshot(3L);
                bad.InstanceId = 0u;
                new PMPredictionTimeline<TestInput, TestSync, TestAux>(new TestModel(), bad, PMNetRole.AutonomousProxy);
            });

            CheckThrows<InvalidOperationException>("OutputFrame 不是 Input 命名空间的快照必须被拒", delegate
            {
                PMPredictionSnapshot<TestSync, TestAux> bad = MakeInitialSnapshot(3L);
                bad.OutputFrame = ServerFrame(30L);
                new PMPredictionTimeline<TestInput, TestSync, TestAux>(new TestModel(), bad, PMNetRole.AutonomousProxy);
            });

            CheckThrows<InvalidOperationException>("OutputFrame 未建立的快照必须被拒", delegate
            {
                PMPredictionSnapshot<TestSync, TestAux> bad = MakeInitialSnapshot(3L);
                bad.OutputFrame = PMFrameId.None;
                new PMPredictionTimeline<TestInput, TestSync, TestAux>(new TestModel(), bad, PMNetRole.AutonomousProxy);
            });

            CheckThrows<InvalidOperationException>("OutputFrame 为负的快照必须被拒（边界不变量）", delegate
            {
                PMPredictionSnapshot<TestSync, TestAux> bad = MakeInitialSnapshot(3L);
                bad.OutputFrame = InputFrame(-1L);
                new PMPredictionTimeline<TestInput, TestSync, TestAux>(new TestModel(), bad, PMNetRole.AutonomousProxy);
            });

            CheckThrows<InvalidOperationException>("ServerFrame 命名空间错误的快照必须被拒", delegate
            {
                PMPredictionSnapshot<TestSync, TestAux> bad = MakeInitialSnapshot(3L);
                bad.ServerFrame = InputFrame(30L);
                new PMPredictionTimeline<TestInput, TestSync, TestAux>(new TestModel(), bad, PMNetRole.AutonomousProxy);
            });

            CheckThrows<InvalidOperationException>("TotalSimTimeMs = NaN 的快照必须被拒", delegate
            {
                PMPredictionSnapshot<TestSync, TestAux> bad = MakeInitialSnapshot(3L);
                bad.TotalSimTimeMs = double.NaN;
                new PMPredictionTimeline<TestInput, TestSync, TestAux>(new TestModel(), bad, PMNetRole.AutonomousProxy);
            });

            CheckThrows<InvalidOperationException>("TotalSimTimeMs 为负的快照必须被拒", delegate
            {
                PMPredictionSnapshot<TestSync, TestAux> bad = MakeInitialSnapshot(3L);
                bad.TotalSimTimeMs = -1.0;
                new PMPredictionTimeline<TestInput, TestSync, TestAux>(new TestModel(), bad, PMNetRole.AutonomousProxy);
            });

            CheckThrows<ArgumentNullException>("Sync 为 null 的快照必须被拒", delegate
            {
                PMPredictionSnapshot<TestSync, TestAux> bad = MakeInitialSnapshot(3L);
                bad.Sync = null;
                new PMPredictionTimeline<TestInput, TestSync, TestAux>(new TestModel(), bad, PMNetRole.AutonomousProxy);
            });

            CheckThrows<ArgumentNullException>("Aux 为 null 的快照必须被拒", delegate
            {
                PMPredictionSnapshot<TestSync, TestAux> bad = MakeInitialSnapshot(3L);
                bad.Aux = null;
                new PMPredictionTimeline<TestInput, TestSync, TestAux>(new TestModel(), bad, PMNetRole.AutonomousProxy);
            });
        }

        // ==================================================================================
        // N. PendingServerFrame 只读元数据（R4-B D4 最小修法）
        // ==================================================================================

        /// <summary>
        /// 克隆计数模型：证明「读取只读元数据不触发 Sync/Aux 深克隆」。
        /// 时间轴的深克隆全部经 <see cref="IPMPredictionModel{TInput,TSync,TAux}.CloneSync"/>
        /// / <c>CloneAux</c>，因此计数即可作为「有没有克隆」的判据（不依赖内存采样）。
        /// </summary>
        private sealed class CloneCountingModel : TestModel
        {
            public int CloneSyncCount;
            public int CloneAuxCount;

            public override TestSync CloneSync(TestSync sync)
            {
                CloneSyncCount++;
                return base.CloneSync(sync);
            }

            public override TestAux CloneAux(TestAux aux)
            {
                CloneAuxCount++;
                return base.CloneAux(aux);
            }
        }

        /// <summary>
        /// N. <see cref="PMPredictionTimeline{TInput,TSync,TAux}.PendingServerFrame"/>：
        ///
        /// D4 缺陷：AP 每子步用 <c>GetSnapshot()</c> 只为取一个 ServerFrame，
        /// 却连带深克隆 Sync/Aux（含数组字段）⇒ 每渲染帧数百次堆分配尖峰。
        /// 最小修法是给时间轴加只读元数据入口，本 Section 把它钉成可执行契约：
        ///   1) 初始未建立（<see cref="PMFrameId.None"/>，DS 首权威帧到达前 blob.ServerFrame=0）保持 None，
        ///      **不合成假 AuthorityServer 帧**；
        ///   2) 非 0 快照 / Tick / 权威校正重放 / Resync（同绑定与重绑）之后，
        ///      都与 <c>GetSnapshot().ServerFrame</c> **逐次一致**；
        ///   3) 读取本身不深克隆 Sync/Aux（对照：<c>GetSnapshot</c> 各克隆一次）。
        /// </summary>
        private static void SectionN_PendingServerFrameMetadata()
        {
            Section("N. PendingServerFrame 只读元数据：None/非 0 快照/Tick/校正重放/Resync 与 GetSnapshot.ServerFrame 一致且不克隆");

            // ---- N1：未建立（None）初值 —— DS 首权威帧到达前的合法状态（blob.ServerFrame = 0） ----
            PMPredictionSnapshot<TestSync, TestAux> noneInitial = MakeInitialSnapshot(0L);
            noneInitial.ServerFrame = PMFrameId.None;
            CloneCountingModel noneModel = new CloneCountingModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> noneTimeline =
                new PMPredictionTimeline<TestInput, TestSync, TestAux>(noneModel, noneInitial, PMNetRole.AutonomousProxy);

            Check("N1 初始 PendingServerFrame 未建立（None）", !noneTimeline.PendingServerFrame.IsValid);
            CheckEqEnum("N1 初始 PendingServerFrame 域为 None（不合成假 AuthorityServer）",
                PMFrameDomain.None, noneTimeline.PendingServerFrame.Domain);
            CheckEq("N1 初始 PendingServerFrame.Value = 0", 0L, noneTimeline.PendingServerFrame.Value);
            Check("N1 初始 PendingServerFrame == GetSnapshot().ServerFrame",
                noneTimeline.PendingServerFrame == noneTimeline.GetSnapshot().ServerFrame);

            // 读取只读元数据不得深克隆 Sync/Aux；对照 GetSnapshot 各克隆一次。
            noneModel.CloneSyncCount = 0;
            noneModel.CloneAuxCount = 0;
            PMFrameId noneFirstRead = noneTimeline.PendingServerFrame;
            PMFrameId noneSecondRead = noneTimeline.PendingServerFrame;
            CheckEq("N1 读取 PendingServerFrame 不触发 Sync 深克隆", 0L, noneModel.CloneSyncCount);
            CheckEq("N1 读取 PendingServerFrame 不触发 Aux 深克隆", 0L, noneModel.CloneAuxCount);
            CheckEq("N1 重复读取不推进 PendingFrame（只读视图）", 0L, noneTimeline.PendingFrame.Value);

            PMPredictionSnapshot<TestSync, TestAux> noneSnapshot = noneTimeline.GetSnapshot();
            CheckEq("N1 对照：GetSnapshot 深克隆 Sync 恰一次", 1L, noneModel.CloneSyncCount);
            CheckEq("N1 对照：GetSnapshot 深克隆 Aux 恰一次", 1L, noneModel.CloneAuxCount);
            Check("N1 两次读取都与 GetSnapshot().ServerFrame 一致",
                noneFirstRead == noneSnapshot.ServerFrame && noneSecondRead == noneSnapshot.ServerFrame);

            // ---- N2：非 0 边界 + 真实 AuthorityServer 元数据 ----
            PMPredictionSnapshot<TestSync, TestAux> nonzeroInitial = MakeInitialSnapshot(7L);
            nonzeroInitial.ServerFrame = ServerFrame(70L);
            PMPredictionTimeline<TestInput, TestSync, TestAux> nonzeroTimeline =
                new PMPredictionTimeline<TestInput, TestSync, TestAux>(new TestModel(), nonzeroInitial, PMNetRole.AutonomousProxy);

            CheckEqEnum("N2 非 0 快照 PendingServerFrame 域为 AuthorityServer",
                PMFrameDomain.AuthorityServer, nonzeroTimeline.PendingServerFrame.Domain);
            CheckEq("N2 非 0 快照 PendingServerFrame = 快照 ServerFrame(70)", 70L, nonzeroTimeline.PendingServerFrame.Value);
            Check("N2 非 0 快照 PendingServerFrame == GetSnapshot().ServerFrame",
                nonzeroTimeline.PendingServerFrame == nonzeroTimeline.GetSnapshot().ServerFrame);

            // 非 0 边界 + None 元数据（DS 首次 PumpMovement 之前）同样保持 None。
            PMPredictionSnapshot<TestSync, TestAux> noneAtSeven = MakeInitialSnapshot(7L);
            noneAtSeven.ServerFrame = PMFrameId.None;
            PMPredictionTimeline<TestInput, TestSync, TestAux> noneAtSevenTimeline =
                new PMPredictionTimeline<TestInput, TestSync, TestAux>(new TestModel(), noneAtSeven, PMNetRole.AutonomousProxy);
            Check("N2 非 0 边界 + None 元数据仍为 None（不合成帧号）", !noneAtSevenTimeline.PendingServerFrame.IsValid);
            Check("N2 非 0 边界 + None 与 GetSnapshot().ServerFrame 一致",
                noneAtSevenTimeline.PendingServerFrame == noneAtSevenTimeline.GetSnapshot().ServerFrame);

            // ---- N3：Tick 后 PendingServerFrame 跟随本次 step 的 ServerFrame ----
            PMTimeStep tickStep = new PMTimeStep();
            tickStep.StepMs = 16f;
            tickStep.ServerFrame = ServerFrame(80L);
            tickStep.ClientInputFrame = InputFrame(7L);
            PMTickResult tickResult = nonzeroTimeline.Tick(tickStep, InputOf(0.5f, 0f));
            Check("N3 Tick 被接纳", tickResult.Accepted);
            CheckEq("N3 Tick 后 PendingFrame 推进到 8", 8L, nonzeroTimeline.PendingFrame.Value);
            CheckEq("N3 Tick 后 PendingServerFrame = step.ServerFrame(80)", 80L, nonzeroTimeline.PendingServerFrame.Value);
            Check("N3 Tick 后 PendingServerFrame == GetSnapshot().ServerFrame",
                nonzeroTimeline.PendingServerFrame == nonzeroTimeline.GetSnapshot().ServerFrame);

            // None 元数据的 Tick（宿主首批 AP 步进带 None）也不得合成帧号。
            PMTimeStep noneTickStep = new PMTimeStep();
            noneTickStep.StepMs = 16f;
            noneTickStep.ServerFrame = PMFrameId.None;
            noneTickStep.ClientInputFrame = InputFrame(0L);
            noneTimeline.Tick(noneTickStep, InputOf(1f, 0f));
            Check("N3 None 元数据 Tick 后仍为 None（不合成假 AuthorityServer）", !noneTimeline.PendingServerFrame.IsValid);
            Check("N3 None 元数据 Tick 后与 GetSnapshot().ServerFrame 一致",
                noneTimeline.PendingServerFrame == noneTimeline.GetSnapshot().ServerFrame);

            // ---- N4：权威校正 + 重放后 PendingServerFrame 取权威 ServerFrame ----
            TestModel reconcileModel = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> reconcileTimeline = NewTimeline(reconcileModel);
            for (int i = 0; i < 3; i++)
            {
                reconcileTimeline.Tick(16, InputOf(0.5f, 0f), ServerFrame(100L + i));
            }

            CheckEq("N4 前置：Tick 3 帧后 PendingFrame = 3", 3L, reconcileTimeline.PendingFrame.Value);
            CheckEq("N4 前置：PendingServerFrame = 最近一次 step 的 ServerFrame(102)", 102L,
                reconcileTimeline.PendingServerFrame.Value);

            PMPredictionSnapshot<TestSync, TestAux> authority = AuthFrom(reconcileTimeline, 2L, 33.0,
                delegate(TestSync s) { s.X = 99f; }, null);
            authority.ServerFrame = ServerFrame(777L);
            PMAuthorityApplyResult reconcileApply = reconcileTimeline.ApplyAuthority(authority);
            Check("N4 权威快照被接纳并触发校正重放", reconcileApply.Applied && reconcileApply.Reconciled);
            CheckEq("N4 重放 1 步（边界 2 → PendingFrame 3）", 1L, reconcileApply.ResimulatedFrames);
            CheckEq("N4 校正重放后 PendingServerFrame = 权威 ServerFrame(777)", 777L,
                reconcileTimeline.PendingServerFrame.Value);
            Check("N4 校正重放后 PendingServerFrame == GetSnapshot().ServerFrame",
                reconcileTimeline.PendingServerFrame == reconcileTimeline.GetSnapshot().ServerFrame);

            // ---- N5：Resync（同绑定 / 跨 epoch 重绑）后 PendingServerFrame 取快照 ServerFrame ----
            TestModel resyncModel = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> resyncTimeline = NewTimeline(resyncModel);

            PMPredictionSnapshot<TestSync, TestAux> sameBinding = MakeInitialSnapshot(0L);
            sameBinding.ServerFrame = ServerFrame(555L);
            PMResyncResult sameResult = resyncTimeline.Resync(sameBinding);
            Check("N5 同绑定 Resync 被接受且不重绑", sameResult.Applied && !sameResult.EpochRebound);
            CheckEq("N5 同绑定 Resync 后 PendingServerFrame = 快照 ServerFrame(555)", 555L,
                resyncTimeline.PendingServerFrame.Value);
            Check("N5 同绑定 Resync 后 PendingServerFrame == GetSnapshot().ServerFrame",
                resyncTimeline.PendingServerFrame == resyncTimeline.GetSnapshot().ServerFrame);

            PMPredictionSnapshot<TestSync, TestAux> rebound = MakeInitialSnapshot(4L);
            rebound.Epoch = Epoch + 1u;
            rebound.ServerFrame = ServerFrame(888L);
            PMResyncResult reboundResult = resyncTimeline.Resync(rebound);
            Check("N5 跨 epoch Resync 触发重绑", reboundResult.Applied && reboundResult.EpochRebound);
            CheckEq("N5 重绑后 PendingFrame = 新快照边界 4", 4L, resyncTimeline.PendingFrame.Value);
            CheckEq("N5 重绑后 PendingServerFrame = 新快照 ServerFrame(888)", 888L,
                resyncTimeline.PendingServerFrame.Value);
            Check("N5 重绑后 PendingServerFrame == GetSnapshot().ServerFrame",
                resyncTimeline.PendingServerFrame == resyncTimeline.GetSnapshot().ServerFrame);

            // ---- N6：冻结态只读读取不参与冻结判定（宿主冻结期间仍可取元数据） ----
            TestModel frozenModel = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> frozenTimeline = NewTimeline(frozenModel);
            frozenTimeline.Tick(16, InputOf(0.5f, 0f), ServerFrame(300L));
            frozenTimeline.Freeze();
            Check("N6 前置：冻结生效", frozenTimeline.IsFrozen);
            CheckEq("N6 冻结后仍可读且保持 step.ServerFrame(300)", 300L, frozenTimeline.PendingServerFrame.Value);
            Check("N6 冻结后 PendingServerFrame == GetSnapshot().ServerFrame",
                frozenTimeline.PendingServerFrame == frozenTimeline.GetSnapshot().ServerFrame);
        }

        // ==================================================================================
        // A. 帧对应与时间步
        // ==================================================================================

        private static void SectionA_FrameCorrespondence()
        {
            Section("A. 帧对应 n -> n+1、整毫秒 dt、0 占位、非法 dt fail-fast");

            TestModel model = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> timeline = NewTimeline(model);

            Check("A1 ConfirmedFrame 初值为 Input:0", timeline.ConfirmedFrame.Domain == PMFrameDomain.Input && timeline.ConfirmedFrame.Value == 0L);
            CheckEq("A1 PendingFrame 初值 0", 0L, timeline.PendingFrame.Value);
            CheckEq("A1 初始保留输入 0", 0L, timeline.RetainedInputCount);

            timeline.Tick(16, InputOf(0.5f, 0.25f), PMFrameId.None);
            timeline.Tick(17, InputOf(0.5f, 0.25f), PMFrameId.None);
            timeline.Tick(16, InputOf(0.5f, 0.25f), PMFrameId.None);

            CheckEq("A1 三次输入后 PendingFrame=3", 3L, timeline.PendingFrame.Value);
            CheckEqF("A1 累计仿真时间 = 16+17+16 = 49", 49.0, timeline.CurrentTotalSimTimeMs, 1e-9);
            CheckEqF("A1 边界1 X = 0.5*16 = 8", 8.0, BoundarySync(timeline, 1L).X, 1e-5);
            CheckEqF("A1 边界2 X = 8 + 0.5*17 = 16.5", 16.5, BoundarySync(timeline, 2L).X, 1e-5);
            CheckEqF("A1 边界3 X = 16.5 + 0.5*16 = 24.5", 24.5, BoundarySync(timeline, 3L).X, 1e-5);
            CheckEqF("A1 边界0 保持初值 X=0", 0.0, BoundarySync(timeline, 0L).X, 1e-9);
            CheckEq("A1 历史条数 3", 3L, timeline.RetainedInputCount);
            CheckEq("A1 模型被调用 3 次", 3L, model.SimulateCount);
            Check("A1 模型收到 input 帧 0/1/2", InputFrameAt(model, 0) == 0L && InputFrameAt(model, 1) == 1L && InputFrameAt(model, 2) == 2L);
            Check("A1 模型收到原始 dt 16/17/16", StepMsAt(model, 0) == 16L && StepMsAt(model, 1) == 17L && StepMsAt(model, 2) == 16L);

            // A2：0 占位
            TestModel placeholderModel = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> placeholderTimeline = NewTimeline(placeholderModel);
            PMTickResult zero = placeholderTimeline.Tick(0, InputOf(0.5f, 0.25f), PMFrameId.None);
            Check("A2 0 占位被接纳", zero.Accepted);
            Check("A2 0 占位不调用模型", !zero.Simulated);
            Check("A2 0 占位标记 Placeholder", zero.Placeholder);
            CheckEq("A2 0 占位后 PendingFrame=1（边界照常推进）", 1L, placeholderTimeline.PendingFrame.Value);
            CheckEqF("A2 0 占位不推进仿真时间", 0.0, placeholderTimeline.CurrentTotalSimTimeMs, 1e-9);
            CheckEq("A2 0 占位不调用模型（计数 0）", 0L, placeholderModel.SimulateCount);
            CheckEqF("A2 边界1 与边界0 同状态", 0.0, BoundarySync(placeholderTimeline, 1L).X, 1e-9);
            CheckEq("A2 PlaceholderSteps 计数 1", 1L, placeholderTimeline.PlaceholderSteps);

            placeholderTimeline.Tick(16, InputOf(0.5f, 0.25f), PMFrameId.None);
            CheckEqF("A2 占位后下一帧照常模拟 X = 0.5*16 = 8", 8.0, placeholderTimeline.GetSyncSnapshot().X, 1e-5);
            CheckEqF("A2 占位后累计时间只算 16", 16.0, placeholderTimeline.CurrentTotalSimTimeMs, 1e-9);

            // A3：非法 dt / 帧命名空间 fail-fast，且状态零改动
            TestModel strictModel = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> strictTimeline = NewTimeline(strictModel);
            strictTimeline.Tick(16, InputOf(0.5f, 0f), PMFrameId.None);
            long pendingBefore = strictTimeline.PendingFrame.Value;
            int retainedBefore = strictTimeline.RetainedInputCount;
            double totalBefore = strictTimeline.CurrentTotalSimTimeMs;
            int simulateBefore = strictModel.SimulateCount;

            CheckThrows<InvalidOperationException>("A3 dt=51 被拒", delegate { strictTimeline.Tick(51, InputOf(0f, 0f), PMFrameId.None); });
            CheckThrows<InvalidOperationException>("A3 dt=-1 被拒", delegate { strictTimeline.Tick(-1, InputOf(0f, 0f), PMFrameId.None); });
            CheckThrows<InvalidOperationException>("A3 dt=16.5 非整毫秒被拒", delegate
            {
                PMTimeStep step = new PMTimeStep();
                step.StepMs = 16.5f;
                strictTimeline.Tick(step, InputOf(0f, 0f));
            });
            CheckThrows<InvalidOperationException>("A3 dt=NaN 被拒", delegate
            {
                PMTimeStep step = new PMTimeStep();
                step.StepMs = float.NaN;
                strictTimeline.Tick(step, InputOf(0f, 0f));
            });
            CheckThrows<InvalidOperationException>("A3 ClientInputFrame 与 PendingFrame 不符被拒", delegate
            {
                PMTimeStep step = new PMTimeStep();
                step.StepMs = 16f;
                step.ClientInputFrame = InputFrame(5L);
                strictTimeline.Tick(step, InputOf(0f, 0f));
            });
            CheckThrows<InvalidOperationException>("A3 ClientInputFrame 跨命名空间被拒", delegate
            {
                PMTimeStep step = new PMTimeStep();
                step.StepMs = 16f;
                step.ClientInputFrame = ServerFrame(0L);
                strictTimeline.Tick(step, InputOf(0f, 0f));
            });
            CheckThrows<InvalidOperationException>("A3 ServerFrame 跨命名空间被拒", delegate
            {
                PMTimeStep step = new PMTimeStep();
                step.StepMs = 16f;
                step.ServerFrame = InputFrame(0L);
                strictTimeline.Tick(step, InputOf(0f, 0f));
            });

            CheckEq("A3 非法 dt 后 PendingFrame 不变", pendingBefore, strictTimeline.PendingFrame.Value);
            CheckEq("A3 非法 dt 后历史条数不变", retainedBefore, strictTimeline.RetainedInputCount);
            CheckEqF("A3 非法 dt 后仿真时间不变", totalBefore, strictTimeline.CurrentTotalSimTimeMs, 1e-9);
            CheckEq("A3 非法 dt 后模型未被调用", simulateBefore, strictModel.SimulateCount);
        }

        // ==================================================================================
        // B. 深克隆隔离
        // ==================================================================================

        private static void SectionB_CloneIsolation()
        {
            Section("B. 深克隆：输入数组 / Sync / Aux 历史与调用方、模型之间互不共享可变引用");

            TestModel model = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> timeline = NewTimeline(model);

            TestInput input = InputOf(0.5f, 0f);
            input.Tokens = new int[] { 1, 2, 3 };
            input.Mode = 5;
            input.YawRequest = 30f;
            timeline.Tick(16, input, PMFrameId.None);

            // 调用方改写自己的数组：历史里的输入快照必须不受影响。
            input.Tokens[0] = 99;

            Check("B1 模型第一次看到 Tokens[0]=1", model.SeenToken0.Count == 1 && TokenAt(model, 0) == 1);

            PMAuthorityApplyResult reconcile = timeline.ApplyAuthority(
                AuthFrom(timeline, 0L, 0.0, delegate (TestSync s) { s.Mode = 9; }, null));
            Check("B1 边界0 的权威快照被接纳（初值边界不算已确认）", reconcile.Applied);
            Check("B1 强制回滚已发生", reconcile.Reconciled);
            CheckEq("B1 回滚重放 1 步", 1L, reconcile.ResimulatedFrames);
            Check("B1 重放读到的是历史深克隆（Tokens[0]=1 而非 99）", model.SeenToken0.Count == 2 && TokenAt(model, 1) == 1);

            PMAuthorityApplyResult repeatZero = timeline.ApplyAuthority(
                AuthFrom(timeline, 0L, 0.0, delegate (TestSync s) { s.Mode = 9; }, null));
            CheckEqEnum("B1 同一边界 0 第二次被拒", PMAuthorityRejectReason.StaleOrDuplicate, repeatZero.Reject);
            CheckEq("B1 边界0 接纳后确认边界仍为 0", 0L, timeline.ConfirmedFrame.Value);
            Check("B1 已接纳记录可观测", timeline.HasAcceptedAuthority && timeline.AcceptedBoundaryFrame.Value == 0L);

            // 返回的快照必须是克隆：改写它不能影响内部状态。
            TestSync leaked = timeline.GetSyncSnapshot();
            CheckEq("B2 GetSyncSnapshot 层数 1", 1L, leaked.Layers == null ? -1L : leaked.Layers.Length);
            SetLayer(leaked, 0, 777);
            leaked.X = -1f;
            TestSync again = timeline.GetSyncSnapshot();
            CheckEq("B2 改写返回值不影响内部 Layers（重放后为输入指定的 5）", 5L, (long)LayerAt(again, 0));
            CheckEqF("B2 改写返回值不影响内部 X", 8.0, again.X, 1e-5);

            // 相邻历史条目之间不得共享数组引用。
            TestModel model2 = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> timeline2 = NewTimeline(model2);
            TestInput in1 = InputOf(0f, 0f);
            in1.Mode = 3;
            in1.YawRequest = 10f;
            TestInput in2 = InputOf(0f, 0f);
            in2.Mode = 4;
            in2.YawRequest = 20f;
            timeline2.Tick(16, in1, PMFrameId.None);
            timeline2.Tick(16, in2, PMFrameId.None);
            TestSync b1 = BoundarySync(timeline2, 1L);
            TestSync b2 = BoundarySync(timeline2, 2L);
            Check("B3 相邻边界数组不是同一引用", !ReferenceEquals(b1.Layers, b2.Layers));
            SetLayer(b1, 0, 500);
            CheckEq("B3 改写旧边界不影响新边界", 4L, (long)LayerAt(BoundarySync(timeline2, 2L), 0));

            // 模型违规改写入参：历史必须不受影响。
            TestModel mutateModel = new TestModel();
            mutateModel.MutateArguments = true;
            PMPredictionTimeline<TestInput, TestSync, TestAux> mutateTimeline = NewTimeline(mutateModel);
            TestInput mutateInput = InputOf(0.5f, 0f);
            mutateInput.Tokens = new int[] { 1, 2, 3 };
            mutateInput.Mode = 6;
            mutateInput.YawRequest = 45f;
            mutateTimeline.Tick(16, mutateInput, PMFrameId.None);
            CheckEq("B4 模型改写 start.Layers 后历史 Mode 仍为 6", 6L, (long)LayerAt(mutateTimeline.GetSyncSnapshot(), 0));
            Check("B4 模型改写 input.Tokens 后历史输入仍是 [1,...]", TokenAt(mutateModel, 0) == 1);
            PMAuthorityApplyResult mutateReconcile = mutateTimeline.ApplyAuthority(
                AuthFrom(mutateTimeline, 0L, 0.0, delegate (TestSync s) { s.Mode = 8; }, null));
            Check("B4 模型改写后历史输入仍可用于重放",
                mutateReconcile.Reconciled && mutateModel.SeenToken0.Count == 2 && TokenAt(mutateModel, 1) == 1);
            CheckEq("B4 重放使用历史输入 dt=16", 16L, StepMsAt(mutateModel, 1));
        }

        // ==================================================================================
        // C. 权威快照：同帧比较、完整恢复、原 dt 重放
        // ==================================================================================

        private static void SectionC_AuthorityReconcile()
        {
            Section("C. 权威边界：迟到/重复/未来/错 epoch/错 instance fail-closed，同帧比较后完整恢复 + 原 dt 重放");

            // C1：迟到/重复
            TestModel model = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> timeline = NewTimeline(model);
            for (int i = 0; i < 3; i++) { timeline.Tick(16, InputOf(0.5f, 0f), PMFrameId.None); }

            PMAuthorityApplyResult first = timeline.ApplyAuthority(AuthFrom(timeline, 2L, 33.0, null, null));
            Check("C1 首次权威被接纳", first.Applied);
            Check("C1 同帧状态一致 → 不回滚", !first.Reconciled);
            CheckEq("C1 确认边界 0 -> 2", 2L, timeline.ConfirmedFrame.Value);
            Check("C1 报告刚才确认 0 -> 2", first.AdvancedConfirmed && first.PreviousConfirmedFrame.Value == 0L);

            PMAuthorityApplyResult duplicate = timeline.ApplyAuthority(AuthFrom(timeline, 2L, 33.0, null, null));
            CheckEqEnum("C1 重复边界被拒（StaleOrDuplicate）", PMAuthorityRejectReason.StaleOrDuplicate, duplicate.Reject);
            Check("C1 重复边界不改变状态", !duplicate.Applied);
            CheckEq("C1 确认边界不回退（仍 2）", 2L, timeline.ConfirmedFrame.Value);
            CheckEq("C1 重复计数 1", 1L, timeline.DuplicateAuthorities);

            PMAuthorityApplyResult older = timeline.ApplyAuthority(AuthFrom(timeline, 1L, 16.0, null, null));
            CheckEqEnum("C1 更旧边界被拒", PMAuthorityRejectReason.StaleOrDuplicate, older.Reject);
            CheckEq("C1 确认边界仍为 2", 2L, timeline.ConfirmedFrame.Value);
            CheckEq("C1 被拒权威计数 2", 2L, timeline.RejectedAuthorities);

            // C2：未来边界
            TestModel futureModel = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> futureTimeline = NewTimeline(futureModel);
            futureTimeline.Tick(16, InputOf(0f, 0f), PMFrameId.None);
            PMPredictionSnapshot<TestSync, TestAux> futureSnapshot = AuthFrom(futureTimeline, 1L, 16.0, null, null);
            futureSnapshot.OutputFrame = InputFrame(3L);
            PMAuthorityApplyResult future = futureTimeline.ApplyAuthority(futureSnapshot);
            CheckEqEnum("C2 未来边界被拒", PMAuthorityRejectReason.Future, future.Reject);
            CheckEq("C2 未来边界计数 1", 1L, futureTimeline.FutureAuthorities);
            CheckEq("C2 确认边界不变", 0L, futureTimeline.ConfirmedFrame.Value);

            TestModel windowModel = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> windowTimeline = NewTimeline(windowModel, 128, PMNetRole.AutonomousProxy, 2000L, 4L);
            for (int i = 0; i < 10; i++) { windowTimeline.Tick(16, InputOf(0f, 0f), PMFrameId.None); }
            PMAuthorityApplyResult beyondWindow = windowTimeline.ApplyAuthority(AuthFrom(windowTimeline, 10L, 160.0, null, null));
            CheckEqEnum("C2 超出未来窗口被拒", PMAuthorityRejectReason.Future, beyondWindow.Reject);
            CheckEq("C2 超出未来窗口后确认边界不变", 0L, windowTimeline.ConfirmedFrame.Value);

            // C3：错 epoch / 错 instance / 畸形
            TestModel bindModel = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> bindTimeline = NewTimeline(bindModel);
            bindTimeline.Tick(16, InputOf(0f, 0f), PMFrameId.None);

            PMPredictionSnapshot<TestSync, TestAux> wrongEpoch = AuthFrom(bindTimeline, 1L, 16.0, null, null);
            wrongEpoch.Epoch = Epoch + 1u;
            CheckEqEnum("C3 错 epoch fail-closed", PMAuthorityRejectReason.EpochMismatch, bindTimeline.ApplyAuthority(wrongEpoch).Reject);

            PMPredictionSnapshot<TestSync, TestAux> wrongInstance = AuthFrom(bindTimeline, 1L, 16.0, null, null);
            wrongInstance.InstanceId = Instance + 1u;
            CheckEqEnum("C3 错 instance fail-closed", PMAuthorityRejectReason.InstanceMismatch, bindTimeline.ApplyAuthority(wrongInstance).Reject);

            PMPredictionSnapshot<TestSync, TestAux> zeroEpoch = AuthFrom(bindTimeline, 1L, 16.0, null, null);
            zeroEpoch.Epoch = 0u;
            CheckEqEnum("C3 epoch=0 视为畸形", PMAuthorityRejectReason.Malformed, bindTimeline.ApplyAuthority(zeroEpoch).Reject);

            PMPredictionSnapshot<TestSync, TestAux> nullSync = AuthFrom(bindTimeline, 1L, 16.0, null, null);
            nullSync.Sync = null;
            CheckEqEnum("C3 Sync=null 视为畸形", PMAuthorityRejectReason.Malformed, bindTimeline.ApplyAuthority(nullSync).Reject);

            PMPredictionSnapshot<TestSync, TestAux> badFrame = AuthFrom(bindTimeline, 1L, 16.0, null, null);
            badFrame.OutputFrame = ServerFrame(1L);
            CheckEqEnum("C3 输出边界命名空间错误视为畸形", PMAuthorityRejectReason.Malformed, bindTimeline.ApplyAuthority(badFrame).Reject);

            CheckEq("C3 失败权威计数累计 5", 5L, bindTimeline.RejectedAuthorities);
            CheckEq("C3 确认边界始终未推进", 0L, bindTimeline.ConfirmedFrame.Value);
            CheckEq("C3 PendingFrame 未受影响", 1L, bindTimeline.PendingFrame.Value);

            // C4：Mode 差异 → 完整恢复 + 原 dt 重放
            TestModel modeModel = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> modeTimeline = NewTimeline(modeModel);
            TestInput modeInput = InputOf(0.5f, 0f);
            modeInput.Mode = 1;
            modeInput.YawRequest = 0f;
            modeTimeline.Tick(16, modeInput, PMFrameId.None);          // 边界1：X=8, Mode=1
            modeTimeline.Tick(17, InputOf(0.5f, 0f), PMFrameId.None);  // 边界2：X=16.5
            modeTimeline.Tick(16, InputOf(0.5f, 0f), PMFrameId.None);  // 边界3：X=24.5

            PMAuthorityApplyResult modeFix = modeTimeline.ApplyAuthority(
                AuthFrom(modeTimeline, 2L, 33.0, delegate (TestSync s)
                {
                    s.Mode = 2;
                    s.Layers = new int[] { 2 };
                }, null));

            Check("C4 Mode 差异触发完整恢复", modeFix.Reconciled);
            CheckEq("C4 重放步数 = PendingFrame - 权威边界 = 1", 1L, modeFix.ResimulatedFrames);
            CheckEq("C4 重放后 Mode 为权威的 2", 2L, (long)modeTimeline.GetSyncSnapshot().Mode);
            CheckEqF("C4 重放后 X = 16.5 + 0.5*16 = 24.5", 24.5, modeTimeline.GetSyncSnapshot().X, 1e-5);
            CheckEq("C4 权威 Layers 被继续 carry", 2L, (long)LayerAt(modeTimeline.GetSyncSnapshot(), 0));
            CheckEq("C4 模型第 4 次调用是重放", 4L, modeModel.SimulateCount);
            Check("C4 重放标记 IsResimulating", ResimAt(modeModel, 3));
            CheckEq("C4 重放使用原始 dt=16", 16L, StepMsAt(modeModel, 3));
            CheckEq("C4 重放使用原始输入帧 2", 2L, InputFrameAt(modeModel, 3));
            CheckEqF("C4 重放后累计时间保持 33+16=49", 49.0, modeTimeline.CurrentTotalSimTimeMs, 1e-9);
            CheckEq("C4 ReplayedSteps 计数 1", 1L, modeTimeline.ReplayedSteps);

            // C5：Aux 差异 → 恢复后从权威 Aux 继续 carry（不是旧 Aux 盲覆盖）
            TestModel auxModel = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> auxTimeline = NewTimeline(auxModel);
            auxTimeline.Tick(16, InputOf(0f, 0f), PMFrameId.None);
            auxTimeline.Tick(16, InputOf(0f, 0f), PMFrameId.None);
            auxTimeline.Tick(16, InputOf(0f, 0f), PMFrameId.None);
            CheckEq("C5 回滚前 Aux.Gravity = -981", -981L, (long)auxTimeline.GetAuxSnapshot().GravityCm);

            PMAuthorityApplyResult auxFix = auxTimeline.ApplyAuthority(
                AuthFrom(auxTimeline, 2L, 1000.0, null, delegate (TestAux a) { a.GravityCm = -500; }));

            Check("C5 Aux 差异触发恢复", auxFix.Reconciled);
            CheckEq("C5 重放后 Aux 沿权威版本 carry（-500，而非 -981）", -500L, (long)auxTimeline.GetAuxSnapshot().GravityCm);
            CheckEqF("C5 恢复采用权威累计时间 1000+16=1016", 1016.0, auxTimeline.CurrentTotalSimTimeMs, 1e-9);
            CheckEq("C5 恢复采用权威 ServerFrame 元数据", 20L, auxTimeline.GetSnapshot().ServerFrame.Value);

            // C6：ShouldReconcile == false → 不恢复、不重放、本地状态零改动
            TestModel stillModel = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> stillTimeline = NewTimeline(stillModel);
            for (int i = 0; i < 3; i++) { stillTimeline.Tick(16, InputOf(0.5f, 0f), PMFrameId.None); }
            int stillSimulateBefore = stillModel.SimulateCount;
            PMAuthorityApplyResult still = stillTimeline.ApplyAuthority(AuthFrom(stillTimeline, 2L, 33.0, null, null));

            Check("C6 一致状态被接纳", still.Applied);
            Check("C6 不触发恢复", !still.Reconciled);
            CheckEq("C6 重放步数 0", 0L, still.ResimulatedFrames);
            CheckEq("C6 模型未被再次调用", stillSimulateBefore, stillModel.SimulateCount);
            CheckEqF("C6 本地 X 不变（0.5*16*3 = 24）", 24.0, stillTimeline.GetSyncSnapshot().X, 1e-5);
            CheckEqF("C6 本地累计时间不变（16*3 = 48）", 48.0, stillTimeline.CurrentTotalSimTimeMs, 1e-9);
            CheckEq("C6 确认边界仍推进到 2", 2L, stillTimeline.ConfirmedFrame.Value);
            CheckEq("C6 ReplayedSteps 仍为 0", 0L, stillTimeline.ReplayedSteps);
        }

        // ==================================================================================
        // D. 确认事件
        // ==================================================================================

        private static void SectionD_ConfirmedEvents()
        {
            Section("D. 权威事件证据：只随确认区间广播一次、帧内去重、按边界升序、回滚替换同帧集合、null/空语义");

            // ---- D1：区间严格 + 帧内去重 + 按边界升序 + 广播的必须是证据而不是预测事件
            TestModel model = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> timeline = NewTimeline(model);
            List<PMPredictionEvent> events = new List<PMPredictionEvent>();
            List<string> advances = new List<string>();
            timeline.EventConfirmed += delegate (PMPredictionEvent e) { events.Add(e); };
            timeline.ConfirmedFrameAdvanced += delegate (PMFrameId from, PMFrameId to)
            {
                advances.Add(from.Value + "->" + to.Value);
            };

            TestInput withKey100 = InputOf(0f, 0f);
            withKey100.EventKey = 100;
            withKey100.EventKind = 7;
            TestInput withKey200 = InputOf(0f, 0f);
            withKey200.EventKey = 200;
            withKey200.EventKind = 7;

            timeline.Tick(16, withKey100, PMFrameId.None);   // 输入0 -> 边界1：预测事件 key100（模型故意发两次）
            timeline.Tick(16, withKey200, PMFrameId.None);   // 输入1 -> 边界2：预测事件 key200
            timeline.Tick(16, InputOf(0f, 0f), PMFrameId.None);
            timeline.Tick(16, InputOf(0f, 0f), PMFrameId.None);

            CheckEq("D1 未确认前不广播任何事件（预测事件不是权威）", 0L, events.Count);

            // 独立权威证据：value 由测试独立指定（11/22），与模型预测的 value=1 刻意不同 ——
            // 广播出 11/22 才能证明用的是证据，而不是时间轴里的预测事件集合。
            PMPredictionEvent[] evidenceBoundary1 = new PMPredictionEvent[]
            {
                new PMPredictionEvent(100UL, 7, 11),
                new PMPredictionEvent(100UL, 7, 11),
            };
            PMPredictionEvent[] evidenceBoundary2 = new PMPredictionEvent[]
            {
                new PMPredictionEvent(200UL, 7, 22),
            };

            PMAuthorityApplyResult toTwo = timeline.ApplyAuthority(AuthFrom(timeline, 2L, 32.0, null, null,
                new PMPredictionEventFrame[]
                {
                    new PMPredictionEventFrame(InputFrame(1L), evidenceBoundary1),
                    new PMPredictionEventFrame(InputFrame(2L), evidenceBoundary2),
                }));
            CheckEq("D1 确认 0->2 广播 2 条（key100 帧内去重 + key200）", 2L, events.Count);
            CheckEq("D1 第一条 key=100", 100L, (long)EventKeyAt(events, 0));
            CheckEq("D1 第一条 kind=7", 7L, events.Count > 0 ? (long)events[0].Kind : -1L);
            CheckEq("D1 第一条 value=11（独立证据，不是模型预测的 1）", 11L,
                events.Count > 0 ? (long)events[0].Value : -1L);
            CheckEq("D1 第二条 key=200", 200L, (long)EventKeyAt(events, 1));
            CheckEq("D1 第二条 value=22（独立证据）", 22L, events.Count > 1 ? (long)events[1].Value : -1L);
            CheckEq("D1 本次广播计数 2", 2L, toTwo.EventsConfirmed);
            CheckEq("D1 累计广播 2", 2L, timeline.ConfirmedEventsEmitted);
            CheckEq("D1 证据被采用的边界数 2", 2L, timeline.ConfirmedEventFramesApplied);
            CheckEq("D1 没有被跳过广播的边界", 0L, timeline.ConfirmedEventFramesSkipped);
            CheckEq("D1 区间记录 1 条", 1L, advances.Count);
            Check("D1 区间为 0->2", AdvanceAt(advances, 0) == "0->2");

            // 无证据：即使这两帧有预测事件，也不得广播（缺失不得用预测事件冒充权威）
            PMAuthorityApplyResult toFour = timeline.ApplyAuthority(AuthFrom(timeline, 4L, 64.0, null, null));
            CheckEq("D1 确认 2->4 无证据 -> 不广播", 0L, toFour.EventsConfirmed);
            CheckEq("D1 累计仍为 2", 2L, events.Count);
            CheckEq("D1 无证据边界被跳过 2 个", 2L, timeline.ConfirmedEventFramesSkipped);
            CheckEq("D1 证据采用数不变", 2L, timeline.ConfirmedEventFramesApplied);
            CheckEq("D1 区间记录 2 条", 2L, advances.Count);
            Check("D1 区间为 2->4", AdvanceAt(advances, 1) == "2->4");

            // 已确认边界再送「不同内容 + 不同证据」：仍旧包拒绝、不重播
            PMAuthorityApplyResult repeat = timeline.ApplyAuthority(
                AuthFrom(timeline, 4L, 999.0, null, null, null,
                         new PMPredictionEvent[] { new PMPredictionEvent(400UL, 7, 44) }));
            CheckEqEnum("D1 重复确认（即便带新证据）不触发广播", PMAuthorityRejectReason.StaleOrDuplicate, repeat.Reject);
            CheckEq("D1 重复后累计仍为 2", 2L, events.Count);
            CheckEq("D1 重复后区间记录仍 2 条", 2L, advances.Count);
            CheckEq("D1 重复证据被采用的边界数不变", 2L, timeline.ConfirmedEventFramesApplied);

            // ---- D2：回滚替换同帧事件集合（时间轴内部），确认时广播的是权威证据
            TestModel replaceModel = new TestModel();
            replaceModel.EmitModeEvents = true;
            PMPredictionTimeline<TestInput, TestSync, TestAux> replaceTimeline = NewTimeline(replaceModel);
            List<PMPredictionEvent> replaceEvents = new List<PMPredictionEvent>();
            replaceTimeline.EventConfirmed += delegate (PMPredictionEvent e) { replaceEvents.Add(e); };

            TestInput key300 = InputOf(0.5f, 0f);
            key300.EventKey = 300;
            key300.EventKind = 5;

            replaceTimeline.Tick(16, InputOf(0.5f, 0f), PMFrameId.None);  // 输入0 -> 边界1：无事件
            replaceTimeline.Tick(16, key300, PMFrameId.None);             // 输入1 -> 边界2：预测事件 300
            replaceTimeline.Tick(16, InputOf(0.5f, 0f), PMFrameId.None);  // 输入2 -> 边界3：Mode=0 无模式事件

            PMPredictionEvent[] boundary3Before;
            bool boundary3AuthBefore;
            Check("D2 校正前可读到边界3 的预测事件集合",
                replaceTimeline.TryGetBoundaryEvents(InputFrame(3L), out boundary3Before, out boundary3AuthBefore));
            CheckEq("D2 校正前边界3 预测事件为空", 0L, (long)boundary3Before.Length);
            Check("D2 校正前边界3 事件非权威", !boundary3AuthBefore);

            // 权威把边界2 的 Mode 改掉：重放输入2 时起点 Mode=2 → 产生新事件 4002（替换旧事件集合）
            PMAuthorityApplyResult replace = replaceTimeline.ApplyAuthority(
                AuthFrom(replaceTimeline, 2L, 32.0, delegate (TestSync s)
                {
                    s.Mode = 2;
                    s.Layers = new int[] { 2 };
                }, null,
                new PMPredictionEventFrame[]
                {
                    new PMPredictionEventFrame(InputFrame(1L), new PMPredictionEvent[0]),
                    new PMPredictionEventFrame(InputFrame(2L), new PMPredictionEvent[] { new PMPredictionEvent(300UL, 5, 55) }),
                }));

            Check("D2 权威差异触发重放", replace.Reconciled);
            CheckEq("D2 确认 0->2 只广播边界1/2 的证据（300）", 1L, replaceEvents.Count);
            CheckEq("D2 事件 key=300", 300L, (long)EventKeyAt(replaceEvents, 0));
            CheckEq("D2 事件 value=55（证据值，不是模型预测的 1）", 55L,
                replaceEvents.Count > 0 ? (long)replaceEvents[0].Value : -1L);

            // 回滚把边界3 的同帧事件集合从「校正前的空集合」替换成「重放后的新集合」
            PMPredictionEvent[] boundary3After;
            bool boundary3AuthAfter;
            Check("D2 可读到重放后的边界3 事件集合",
                replaceTimeline.TryGetBoundaryEvents(InputFrame(3L), out boundary3After, out boundary3AuthAfter));
            CheckEq("D2 回滚替换同帧事件集合（新集合 1 条）", 1L, (long)boundary3After.Length);
            CheckEq("D2 新集合是重放产生的事件 4002", 4002L,
                boundary3After.Length > 0 ? (long)boundary3After[0].Key : -1L);
            CheckEq("D2 新集合 value=2（重放起点 Mode=2）", 2L,
                boundary3After.Length > 0 ? (long)boundary3After[0].Value : -1L);
            Check("D2 重放集合仍非权威（该边界尚未确认）", !boundary3AuthAfter);
            CheckEq("D2 4002 尚未广播（确认边界还停在 2）", 0L, (long)CountKey(replaceEvents, 4002L));

            // 确认边界3：广播该边界的权威证据；300 不重播、4002 只发一次
            PMAuthorityApplyResult replaceToThree = replaceTimeline.ApplyAuthority(
                AuthFrom(replaceTimeline, 3L, 48.0, null, null,
                new PMPredictionEventFrame[]
                {
                    new PMPredictionEventFrame(InputFrame(3L), new PMPredictionEvent[] { new PMPredictionEvent(4002UL, 2, 2) }),
                }));
            CheckEq("D2 确认 3 时广播该边界的权威证据", 1L, replaceToThree.EventsConfirmed);
            CheckEq("D2 累计 2 条", 2L, replaceEvents.Count);
            CheckEq("D2 第二条是重放产生的新事件 4002", 4002L, (long)EventKeyAt(replaceEvents, 1));
            CheckEq("D2 300 没有被重复广播", 1L, (long)CountKey(replaceEvents, 300L));
            CheckEq("D2 4002 只出现一次", 1L, (long)CountKey(replaceEvents, 4002L));

            PMPredictionEvent[] boundary3Final;
            bool boundary3AuthFinal;
            Check("D2 确认后边界3 事件集合被权威证据替换",
                replaceTimeline.TryGetBoundaryEvents(InputFrame(3L), out boundary3Final, out boundary3AuthFinal));
            Check("D2 边界3 事件被标记为权威", boundary3AuthFinal);
            CheckEq("D2 权威证据只有 1 条", 1L, (long)boundary3Final.Length);
        }

        private static int CountKey(List<PMPredictionEvent> events, ulong key)
        {
            int count = 0;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].Key == key) { count++; }
            }

            return count;
        }

        // ==================================================================================
        // L. 权威事件证据契约（兼容新增：Stalled 门 + ConfirmedEvents/ConfirmedEventFrames）
        // ==================================================================================

        private static void SectionL_AuthorityEventEvidence()
        {
            Section("L. 权威事件证据：null/空语义、有界校验、深克隆隔离、跳确认不伪造、冻结与待重同步必须拒绝");

            // ---- L1：null = 未提供证据（不广播，也不得拿预测事件顶替）
            TestModel nullModel = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> nullTimeline = NewTimeline(nullModel);
            int nullEvents = 0;
            nullTimeline.EventConfirmed += delegate (PMPredictionEvent e) { nullEvents++; };
            TestInput key500 = InputOf(0f, 0f);
            key500.EventKey = 500;
            key500.EventKind = 3;
            nullTimeline.Tick(16, key500, PMFrameId.None);   // 输入0 -> 边界1：预测事件 500

            PMAuthorityApplyResult nullEvidence = nullTimeline.ApplyAuthority(AuthFrom(nullTimeline, 1L, 16.0, null, null));
            CheckEq("L1 证据为 null -> 不广播", 0L, nullEvidence.EventsConfirmed);
            CheckEq("L1 证据为 null -> 该边界计入 skip", 1L, nullTimeline.ConfirmedEventFramesSkipped);
            CheckEq("L1 证据为 null -> 不计入 applied", 0L, nullTimeline.ConfirmedEventFramesApplied);
            CheckEq("L1 该边界的预测事件没有被冒充广播", 0L, (long)nullEvents);

            PMPredictionEvent[] nullBoundaryEvents;
            bool nullBoundaryAuth;
            Check("L1 可读取该边界事件集合",
                nullTimeline.TryGetBoundaryEvents(InputFrame(1L), out nullBoundaryEvents, out nullBoundaryAuth));
            Check("L1 证据为 null 后该边界事件仍非权威", !nullBoundaryAuth);

            // ---- L2：空数组 = 确定无事件（证据成立），与 null 语义可区分
            TestModel emptyModel = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> emptyTimeline = NewTimeline(emptyModel);
            int emptyEvents = 0;
            emptyTimeline.EventConfirmed += delegate (PMPredictionEvent e) { emptyEvents++; };
            emptyTimeline.Tick(16, key500, PMFrameId.None);

            PMAuthorityApplyResult emptyEvidence = emptyTimeline.ApplyAuthority(
                AuthFrom(emptyTimeline, 1L, 16.0, null, null, null, new PMPredictionEvent[0]));
            CheckEq("L2 证据为空数组 -> 不广播", 0L, emptyEvidence.EventsConfirmed);
            CheckEq("L2 证据为空数组 -> 计入 applied（不是 skip）", 1L, emptyTimeline.ConfirmedEventFramesApplied);
            CheckEq("L2 证据为空数组 -> skip 为 0", 0L, emptyTimeline.ConfirmedEventFramesSkipped);
            CheckEq("L2 预测事件没有被冒充广播", 0L, emptyEvents);

            PMPredictionEvent[] emptyBoundaryEvents;
            bool emptyBoundaryAuth;
            Check("L2 可读取该边界事件集合",
                emptyTimeline.TryGetBoundaryEvents(InputFrame(1L), out emptyBoundaryEvents, out emptyBoundaryAuth));
            CheckEq("L2 该边界事件集合被替换为空（确定无事件）", 0L, (long)emptyBoundaryEvents.Length);
            Check("L2 该边界事件被标记为权威", emptyBoundaryAuth);

            // ---- L3：跨边界跳确认只广播有证据的边界，不伪造中间边界
            TestModel jumpModel = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> jump = NewTimeline(jumpModel);
            List<ulong> jumpKeys = new List<ulong>();
            jump.EventConfirmed += delegate (PMPredictionEvent e) { jumpKeys.Add(e.Key); };
            for (int i = 0; i < 4; i++)
            {
                TestInput input = InputOf(0f, 0f);
                input.EventKey = 700 + i;    // 每帧都有预测事件，但只给边界 4 证据
                jump.Tick(16, input, PMFrameId.None);
            }

            PMAuthorityApplyResult jumpRes = jump.ApplyAuthority(
                AuthFrom(jump, 4L, 64.0, null, null, null,
                         new PMPredictionEvent[] { new PMPredictionEvent(999UL, 1, 9) }));
            CheckEq("L3 跳确认 0->4 只广播边界4 的证据", 1L, jumpRes.EventsConfirmed);
            CheckEq("L3 广播的就是边界4 的证据 key", 999L, (long)(jumpKeys.Count > 0 ? jumpKeys[0] : 0UL));
            CheckEq("L3 中间 3 个边界被跳过（不伪造中间事件）", 3L, jump.ConfirmedEventFramesSkipped);
            CheckEq("L3 证据采用 1 个边界", 1L, jump.ConfirmedEventFramesApplied);
            Check("L3 中间边界的预测事件 key 一个都没广播",
                jumpKeys.IndexOf(700UL) < 0 && jumpKeys.IndexOf(701UL) < 0 && jumpKeys.IndexOf(702UL) < 0);
            CheckEq("L3 确认边界照常推进到 4", 4L, jump.ConfirmedFrame.Value);

            // ---- L4：证据深克隆 —— 事后改写调用方数组不污染历史
            TestModel cloneModel = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> cloneTl = NewTimeline(cloneModel);
            cloneTl.Tick(16, InputOf(0f, 0f), PMFrameId.None);
            PMPredictionEvent[] mutableEvents = new PMPredictionEvent[] { new PMPredictionEvent(800UL, 1, 8) };
            PMPredictionEventFrame[] mutableFrames = new PMPredictionEventFrame[]
            {
                new PMPredictionEventFrame(InputFrame(1L), mutableEvents),
            };
            cloneTl.ApplyAuthority(AuthFrom(cloneTl, 1L, 16.0, null, null, mutableFrames));
            CheckEq("L4 证据已被采用", 1L, cloneTl.ConfirmedEventFramesApplied);

            mutableEvents[0] = new PMPredictionEvent(801UL, 1, 81);
            mutableFrames[0] = new PMPredictionEventFrame(InputFrame(1L),
                new PMPredictionEvent[] { new PMPredictionEvent(802UL, 1, 82) });

            PMPredictionEvent[] afterMutate;
            bool afterMutateAuth;
            Check("L4 事后可读取历史事件集合",
                cloneTl.TryGetBoundaryEvents(InputFrame(1L), out afterMutate, out afterMutateAuth));
            CheckEq("L4 事后改写证据数组不污染历史（key 仍为 800）", 800L, (long)afterMutate[0].Key);
            CheckEq("L4 事后改写证据数组不污染历史（value 仍为 8）", 8L, (long)afterMutate[0].Value);

            // ---- L5：证据有界校验 —— 畸形证据 fail-closed 且确认边界/事件零改动
            TestModel bigModel = new TestModel();
            // 未来窗口放宽到 4096：本节要构造 300 条边界跨度上的证据批，默认窗口 128 会先以 Future 拒绝。
            PMPredictionTimeline<TestInput, TestSync, TestAux> big =
                NewTimeline(bigModel, 512, PMNetRole.AutonomousProxy, 2000L, 4096L);
            for (int i = 0; i < 300; i++) { big.Tick(16, InputOf(0f, 0f), PMFrameId.None); }

            long eventsBeforeL5 = big.ConfirmedEventsEmitted;

            PMPredictionEventFrame[] tooManyFrames = new PMPredictionEventFrame[
                PMPredictionTimeline<TestInput, TestSync, TestAux>.MaxConfirmedEventFrames + 1];
            for (int i = 0; i < tooManyFrames.Length; i++)
            {
                tooManyFrames[i] = new PMPredictionEventFrame(InputFrame(1L), new PMPredictionEvent[0]);
            }

            CheckEqEnum("L5 证据条目超过 MaxConfirmedEventFrames -> Malformed",
                PMAuthorityRejectReason.Malformed,
                big.ApplyAuthority(AuthFrom(big, 300L, 4800.0, null, null, tooManyFrames)).Reject);

            PMPredictionEvent[] tooManyEvents = new PMPredictionEvent[
                PMPredictionTimeline<TestInput, TestSync, TestAux>.MaxEventsPerFrame + 1];
            for (int i = 0; i < tooManyEvents.Length; i++)
            {
                tooManyEvents[i] = new PMPredictionEvent((ulong)(i + 1), 1, i);
            }

            CheckEqEnum("L5 单帧事件超过 MaxEventsPerFrame -> Malformed",
                PMAuthorityRejectReason.Malformed,
                big.ApplyAuthority(AuthFrom(big, 300L, 4800.0, null, null, null, tooManyEvents)).Reject);

            // 总事件数超上限：256 个不同边界 × 17 条 = 4352 > 4096
            PMPredictionEventFrame[] tooManyTotal = new PMPredictionEventFrame[
                PMPredictionTimeline<TestInput, TestSync, TestAux>.MaxConfirmedEventFrames];
            for (int i = 0; i < tooManyTotal.Length; i++)
            {
                PMPredictionEvent[] chunk = new PMPredictionEvent[17];
                for (int k = 0; k < chunk.Length; k++)
                {
                    // key 唯一，避免帧内去重把总量降下来
                    chunk[k] = new PMPredictionEvent((ulong)(100000 + i * 17 + k), 1, k);
                }

                tooManyTotal[i] = new PMPredictionEventFrame(InputFrame(44L + i), chunk);
            }

            CheckEqEnum("L5 事件总量超过 MaxConfirmedEventsTotal -> Malformed",
                PMAuthorityRejectReason.Malformed,
                big.ApplyAuthority(AuthFrom(big, 300L, 4800.0, null, null, tooManyTotal)).Reject);

            PMPredictionEvent zeroKey = default(PMPredictionEvent);   // 绕过构造函数，直接构造 key=0
            CheckEqEnum("L5 证据含 key=0 -> Malformed",
                PMAuthorityRejectReason.Malformed,
                big.ApplyAuthority(AuthFrom(big, 300L, 4800.0, null, null, null,
                    new PMPredictionEvent[] { zeroKey })).Reject);

            CheckEqEnum("L5 证据边界用 AuthorityServer 命名空间 -> Malformed",
                PMAuthorityRejectReason.Malformed,
                big.ApplyAuthority(AuthFrom(big, 300L, 4800.0, null, null,
                    new PMPredictionEventFrame[]
                    {
                        new PMPredictionEventFrame(ServerFrame(7L), new PMPredictionEvent[0]),
                    })).Reject);

            CheckEqEnum("L5 证据边界 <= 已确认边界 -> Malformed",
                PMAuthorityRejectReason.Malformed,
                big.ApplyAuthority(AuthFrom(big, 300L, 4800.0, null, null,
                    new PMPredictionEventFrame[]
                    {
                        new PMPredictionEventFrame(InputFrame(0L), new PMPredictionEvent[0]),
                    })).Reject);

            CheckEqEnum("L5 证据边界 > 本次权威边界 -> Malformed",
                PMAuthorityRejectReason.Malformed,
                big.ApplyAuthority(AuthFrom(big, 300L, 4800.0, null, null,
                    new PMPredictionEventFrame[]
                    {
                        new PMPredictionEventFrame(InputFrame(301L), new PMPredictionEvent[0]),
                    })).Reject);

            CheckEqEnum("L5 同一边界重复给证据 -> Malformed",
                PMAuthorityRejectReason.Malformed,
                big.ApplyAuthority(AuthFrom(big, 300L, 4800.0, null, null,
                    new PMPredictionEventFrame[]
                    {
                        new PMPredictionEventFrame(InputFrame(299L), new PMPredictionEvent[0]),
                        new PMPredictionEventFrame(InputFrame(299L), new PMPredictionEvent[0]),
                    })).Reject);

            CheckEq("L5 畸形证据后确认边界零改动", 0L, big.ConfirmedFrame.Value);
            CheckEq("L5 畸形证据后未广播任何事件", eventsBeforeL5, big.ConfirmedEventsEmitted);
            CheckEq("L5 畸形证据后 applied/skipped 均未增长", 0L, big.ConfirmedEventFramesApplied);
            CheckEq("L5 畸形证据后 skipped 未增长", 0L, big.ConfirmedEventFramesSkipped);

            // 畸形证据即便和其他拒绝原因同时出现，也不会顺手推进确认
            CheckEq("L5 畸形证据未改动确认边界（PendingFrame 也未变）", 300L, big.PendingFrame.Value);

            // frame.Events == null 的条目等于「本边界未提供证据」：跳过而不是畸形
            PMPredictionEventFrame[] nullFrameEvents = new PMPredictionEventFrame[]
            {
                new PMPredictionEventFrame(InputFrame(299L), null),
            };
            int nullFrameBroadcast = 0;
            big.EventConfirmed += delegate (PMPredictionEvent e) { nullFrameBroadcast++; };
            long skippedBeforeNullFrame = big.ConfirmedEventFramesSkipped;
            PMAuthorityApplyResult nullFrameRes = big.ApplyAuthority(
                AuthFrom(big, 300L, 4800.0, null, null, nullFrameEvents));
            Check("L5 frame.Events=null 视为未提供证据（仍被接纳）", nullFrameRes.Applied);
            CheckEq("L5 frame.Events=null 不广播", 0L, (long)nullFrameBroadcast);
            // 一次确认跨 0->300，300 个边界里只有 299 有证据条目、且该条目 Events=null，
            // 因此全部 300 个边界都计入 skip（不伪造任何中间边界事件）。
            CheckEq("L5 frame.Events=null 计入 skip", 300L, big.ConfirmedEventFramesSkipped - skippedBeforeNullFrame);

            // ---- L6：错误绑定/未来边界即便带证据也不广播
            TestModel bindModel = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> bind = NewTimeline(bindModel);
            int bindEvents = 0;
            bind.EventConfirmed += delegate (PMPredictionEvent e) { bindEvents++; };
            bind.Tick(16, InputOf(0f, 0f), PMFrameId.None);
            bind.Tick(16, InputOf(0f, 0f), PMFrameId.None);

            TestInput future = InputOf(0f, 0f);
            future.EventKey = 1;
            future.EventKind = 1;

            CheckEqEnum("L6 错 epoch + 证据 -> EpochMismatch 且不广播",
                PMAuthorityRejectReason.EpochMismatch,
                bind.ApplyAuthority(new PMPredictionSnapshot<TestSync, TestAux>
                {
                    Epoch = Epoch + 1u,
                    InstanceId = Instance,
                    OutputFrame = InputFrame(1L),
                    ServerFrame = ServerFrame(10L),
                    TotalSimTimeMs = 16.0,
                    Sync = MakeSync(),
                    Aux = MakeAux(),
                    ConfirmedEvents = new PMPredictionEvent[] { new PMPredictionEvent(1UL, 1, 1) },
                }).Reject);

            PMPredictionSnapshot<TestSync, TestAux> futureSnapshot = AuthFrom(bind, 2L, 32.0, null, null, null,
                new PMPredictionEvent[] { new PMPredictionEvent(2UL, 1, 2) });
            futureSnapshot.OutputFrame = InputFrame(9L);
            CheckEqEnum("L6 未来边界 + 证据 -> Future 且不广播",
                PMAuthorityRejectReason.Future, bind.ApplyAuthority(futureSnapshot).Reject);

            CheckEq("L6 两次被拒后一个事件都没广播", 0L, (long)bindEvents);
            CheckEq("L6 被拒后确认边界仍为 0", 0L, bind.ConfirmedFrame.Value);
            CheckEq("L6 被拒后 applied 未增长", 0L, bind.ConfirmedEventFramesApplied);

            // ---- L7：冻结 / 待重同步期间 ApplyAuthority 必须被拒绝，状态/确认/事件零改动
            TestModel stallModel = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> stall = NewTimeline(stallModel);
            int stallEvents = 0;
            int stallAdvances = 0;
            List<ulong> stallKeys = new List<ulong>();
            stall.EventConfirmed += delegate (PMPredictionEvent e) { stallEvents++; stallKeys.Add(e.Key); };
            stall.ConfirmedFrameAdvanced += delegate (PMFrameId a, PMFrameId b) { stallAdvances++; };
            for (int i = 0; i < 4; i++) { stall.Tick(16, InputOf(0f, 0f), PMFrameId.None); }

            stall.Freeze();
            PMAuthorityApplyResult stalledApply = stall.ApplyAuthority(AuthFrom(stall, 4L, 64.0, null, null,
                new PMPredictionEventFrame[]
                {
                    new PMPredictionEventFrame(InputFrame(4L), new PMPredictionEvent[] { new PMPredictionEvent(31UL, 1, 3) }),
                }));
            Check("L7 冻结期间 ApplyAuthority 被拒绝", !stalledApply.Applied);
            CheckEqEnum("L7 冻结期间拒绝码为 Stalled", PMAuthorityRejectReason.Stalled, stalledApply.Reject);
            Check("L7 冻结期间不重放", !stalledApply.Reconciled);
            Check("L7 冻结期间不推进确认边界", !stalledApply.AdvancedConfirmed);
            CheckEq("L7 冻结期间确认边界不变", 0L, stall.ConfirmedFrame.Value);
            CheckEq("L7 冻结期间待处理帧不变", 4L, stall.PendingFrame.Value);
            CheckEq("L7 冻结期间零事件广播", 0L, (long)stallEvents);
            CheckEq("L7 冻结期间零确认回调", 0L, (long)stallAdvances);
            Check("L7 冻结期间仍然冻结（HalfFrozen 不存在）", stall.IsFrozen);
            CheckEq("L7 Stalled 拒绝计数可观测", 1L, stall.StalledAuthorities);
            CheckEq("L7 冻结期间 applied/skipped 未增长", 0L, stall.ConfirmedEventFramesApplied);
            CheckEq("L7 冻结期间 skipped 未增长", 0L, stall.ConfirmedEventFramesSkipped);

            // 连畸形快照也先被判 Stalled（门在结构校验之前），且不计数到普通拒绝
            long rejectedBefore = stall.RejectedAuthorities;
            CheckEqEnum("L7 冻结期间畸形快照同样 Stalled",
                PMAuthorityRejectReason.Stalled,
                stall.ApplyAuthority(new PMPredictionSnapshot<TestSync, TestAux>
                {
                    Epoch = 0u,
                    InstanceId = 0u,
                    OutputFrame = PMFrameId.None,
                    ServerFrame = PMFrameId.None,
                    TotalSimTimeMs = 0.0,
                    Sync = null,
                    Aux = null,
                }).Reject);
            CheckEq("L7 Stalled 不混入普通拒绝计数", rejectedBefore, stall.RejectedAuthorities);

            // 显式 Resync 是唯一恢复入口；恢复时同样只广播有证据的边界
            PMAuthorityApplyResult beforeResync = stall.ApplyAuthority(AuthFrom(stall, 4L, 64.0, null, null));
            CheckEqEnum("L7 未 Resync 前反复 ApplyAuthority 一律 Stalled",
                PMAuthorityRejectReason.Stalled, beforeResync.Reject);
            CheckEq("L7 Stalled 累计 3 次", 3L, stall.StalledAuthorities);

            PMResyncResult stallResync = stall.Resync(AuthFrom(stall, 4L, 64.0, null, null,
                new PMPredictionEventFrame[]
                {
                    new PMPredictionEventFrame(InputFrame(4L), new PMPredictionEvent[] { new PMPredictionEvent(32UL, 1, 4) }),
                }));
            Check("L7 Resync 被接纳", stallResync.Applied);
            Check("L7 Resync 后解除冻结", !stall.IsFrozen);
            CheckEq("L7 Resync 推进确认边界到 4", 4L, stall.ConfirmedFrame.Value);
            CheckEq("L7 Resync 只广播有证据的边界（1 条）", 1L, (long)stallEvents);
            CheckEq("L7 Resync 广播的就是该边界的证据 key", 32L, (long)(stallKeys.Count > 0 ? stallKeys[0] : 0UL));
            Check("L7 Resync 后可继续推进", stall.Tick(16, InputOf(0f, 0f), PMFrameId.None).Accepted);

            // ---- L8：重绑快照携带事件证据必须 fail-closed（不可静默丢不可逆事件）
            TestModel rebindModel = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> rebind = NewTimeline(rebindModel);
            rebind.Tick(16, InputOf(0f, 0f), PMFrameId.None);
            uint epochBefore = rebind.SessionEpoch;
            PMResyncResult rebindRejected = rebind.Resync(new PMPredictionSnapshot<TestSync, TestAux>
            {
                Epoch = Epoch + 5u,
                InstanceId = Instance,
                OutputFrame = InputFrame(1L),
                ServerFrame = ServerFrame(50L),
                TotalSimTimeMs = 16.0,
                Sync = MakeSync(),
                Aux = MakeAux(),
                ConfirmedEvents = new PMPredictionEvent[] { new PMPredictionEvent(41UL, 1, 4) },
            });
            CheckEqEnum("L8 重绑快照带事件证据 -> Malformed", PMAuthorityRejectReason.Malformed, rebindRejected.Reject);
            Check("L8 被拒后未重绑", rebind.SessionEpoch == epochBefore && rebind.EpochRebinds == 0);
            CheckEq("L8 被拒后确认边界不变", 0L, rebind.ConfirmedFrame.Value);

            // 不带证据的重绑照常成立
            PMResyncResult rebindOk = rebind.Resync(new PMPredictionSnapshot<TestSync, TestAux>
            {
                Epoch = Epoch + 5u,
                InstanceId = Instance,
                OutputFrame = InputFrame(1L),
                ServerFrame = ServerFrame(50L),
                TotalSimTimeMs = 16.0,
                Sync = MakeSync(),
                Aux = MakeAux(),
            });
            Check("L8 不带证据的重绑正常成立", rebindOk.Applied && rebindOk.EpochRebound);
        }

        // ==================================================================================
        // E. 历史边界与重同步
        // ==================================================================================

        private static void SectionE_HistoryBoundsAndResync()
        {
            Section("E. 历史有界：满且未确认就冻结进 NeedsResync；确认后可裁剪；不静默覆盖、不用当前值冒充");

            // E1：小容量满 → 冻结
            TestModel smallModel = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> small = NewTimeline(smallModel, 4);
            for (int i = 0; i < 4; i++) { small.Tick(16, InputOf(0f, 0f), PMFrameId.None); }
            CheckEq("E1 历史保留 4", 4L, small.RetainedInputCount);

            int resyncRequests = 0;
            small.ResyncRequested += delegate { resyncRequests++; };

            PMTickResult overflow = small.Tick(16, InputOf(0f, 0f), PMFrameId.None);
            CheckEqEnum("E1 满且未确认 → HistoryExhausted", PMTickRejectReason.HistoryExhausted, overflow.Reject);
            Check("E1 进入 NeedsResync", small.NeedsResync);
            CheckEq("E1 输入未被吞掉（PendingFrame 仍 4）", 4L, small.PendingFrame.Value);
            CheckEq("E1 历史未被静默覆盖", 4L, small.RetainedInputCount);
            CheckEq("E1 HistoryExhaustions=1", 1L, small.HistoryExhaustions);
            CheckEq("E1 请求重同步 1 次", 1L, (long)resyncRequests);

            PMTickResult stillFrozen = small.Tick(16, InputOf(0f, 0f), PMFrameId.None);
            CheckEqEnum("E1 未重同步前一切 Tick 被拒", PMTickRejectReason.NeedsResync, stillFrozen.Reject);

            PMResyncResult resync = small.Resync(AuthFrom(small, 2L, 32.0, null, null));
            Check("E1 Resync 被接受", resync.Applied);
            Check("E1 Resync 不是 epoch 重绑", !resync.EpochRebound);
            CheckEq("E1 Resync 丢弃 2 条未确认输入", 2L, resync.ResetUnconfirmedInputs);
            CheckEq("E1 Resync 后 PendingFrame=2", 2L, small.PendingFrame.Value);
            CheckEq("E1 Resync 后历史 2 条", 2L, small.RetainedInputCount);
            Check("E1 Resync 后解除冻结", !small.NeedsResync);

            PMTickResult afterResync = small.Tick(16, InputOf(0f, 0f), PMFrameId.None);
            Check("E1 Resync 后可继续推进", afterResync.Accepted);

            // E2：默认 128 满 + 确认后裁剪
            TestModel capModel = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> cap = NewTimeline(capModel, 128);
            for (int i = 0; i < 128; i++) { cap.Tick(16, InputOf(0f, 0f), PMFrameId.None); }
            CheckEq("E2 128 步全部接纳", 128L, cap.RetainedInputCount);
            CheckEq("E2 128 步 PendingFrame=128", 128L, cap.PendingFrame.Value);

            PMTickResult capOverflow = cap.Tick(16, InputOf(0f, 0f), PMFrameId.None);
            CheckEqEnum("E2 128 满未确认 → 冻结", PMTickRejectReason.HistoryExhausted, capOverflow.Reject);

            cap.Resync(AuthFrom(cap, 128L, 2048.0, null, null));
            Check("E2 Resync 解除 NeedsResync", !cap.NeedsResync);
            CheckEq("E2 Resync 后确认边界=128", 128L, cap.ConfirmedFrame.Value);

            PMTickResult afterCap = cap.Tick(16, InputOf(0f, 0f), PMFrameId.None);
            Check("E2 确认后裁剪并继续推进", afterCap.Accepted);
            CheckEq("E2 裁剪 1 条确认记录", 1L, cap.PrunedConfirmedSteps);
            CheckEq("E2 最旧保留边界推进到 1", 1L, cap.OldestRetainedBoundaryFrame.Value);
            CheckEq("E2 历史深度仍为 128", 128L, cap.RetainedInputCount);

            // E3：不变式 —— 未确认区间永远留在窗口内，因此“权威边界缺历史”不可达
            TestModel invModel = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> inv = NewTimeline(invModel, 4);
            bool invariantHolds = true;
            for (int step = 0; step < 200; step++)
            {
                inv.Tick(16, InputOf(0f, 0f), PMFrameId.None);
                if (step % 3 == 2)
                {
                    double total = (step + 1) * 16.0;
                    PMAuthorityApplyResult applied = inv.ApplyAuthority(AuthFrom(inv, inv.PendingFrame.Value, total, null, null));
                    if (applied.Reject != PMAuthorityRejectReason.None) { invariantHolds = false; }
                }

                long confirmed = inv.ConfirmedFrame.Value;
                long pending = inv.PendingFrame.Value;
                if (inv.OldestRetainedBoundaryFrame.Value > confirmed) { invariantHolds = false; }
                for (long b = confirmed + 1; b <= pending; b++)
                {
                    if (!inv.HasInput(b - 1)) { invariantHolds = false; }
                }
            }

            Check("E3 未确认区间永不被裁剪（200 步不变式）", invariantHolds);
            CheckEq("E3 从未发生缺历史回滚", 0L, inv.HistoryMisses);
            Check("E3 已被裁剪的旧边界可被宿主识别为不可恢复", !inv.HasInput(0L));
            PMPredictionSnapshot<TestSync, TestAux> lost;
            Check("E3 查询已裁剪边界返回 false（宿主据此请求重同步）",
                !inv.TryGetBoundarySnapshot(InputFrame(0L), out lost));
        }

        // ==================================================================================
        // F. 断线冻结与墙钟
        // ==================================================================================

        private static void SectionF_DisconnectFreezeWallClock()
        {
            Section("F. 断线只 Freeze：帧号停止推进；墙钟 2s 只通知一次；时钟倒退不提前触发");

            TestModel model = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> timeline = NewTimeline(model);
            timeline.Tick(16, InputOf(0f, 0f), PMFrameId.None);
            timeline.Tick(16, InputOf(0f, 0f), PMFrameId.None);
            timeline.ApplyAuthority(AuthFrom(timeline, 2L, 32.0, null, null));
            CheckEq("F1 确认边界 2", 2L, timeline.ConfirmedFrame.Value);

            int notifications = 0;
            timeline.ResyncRequested += delegate { notifications++; };
            int simulateBeforeFreeze = model.SimulateCount;

            timeline.Freeze();
            Check("F1 冻结标记", timeline.IsFrozen);

            PMTickResult frozenTick = timeline.Tick(16, InputOf(0f, 0f), PMFrameId.None);
            CheckEqEnum("F1 冻结时 Tick 被拒", PMTickRejectReason.Frozen, frozenTick.Reject);
            CheckEq("F1 冻结时帧号不推进", 2L, timeline.PendingFrame.Value);
            CheckEq("F1 冻结时模型不再被调用", (long)simulateBeforeFreeze, model.SimulateCount);

            Check("F1 1900ms 未到阈值不通知", !timeline.NotifyWallClock(1900.0));
            CheckEqF("F1 冻结累计 1900", 1900.0, timeline.FrozenElapsedMs, 1e-9);
            Check("F1 时钟倒退不通知", !timeline.NotifyWallClock(-5000.0));
            CheckEqF("F1 时钟倒退不减少累计", 1900.0, timeline.FrozenElapsedMs, 1e-9);
            CheckEq("F1 时钟倒退被计数", 1L, timeline.WallClockRewindsIgnored);
            Check("F1 跨过 2000ms 通知一次", timeline.NotifyWallClock(200.0));
            CheckEqF("F1 累计 2100", 2100.0, timeline.FrozenElapsedMs, 1e-9);
            Check("F1 阈值后不再重复通知", !timeline.NotifyWallClock(5000.0));
            CheckEq("F1 通知只发生一次", 1L, (long)notifications);
            CheckEq("F1 ResyncRequests=1", 1L, timeline.ResyncRequests);

            int resetNotified = -1;
            timeline.UnconfirmedInputsResetNotified += delegate (int n) { resetNotified = n; };
            PMResyncResult recovery = timeline.Resync(AuthFrom(timeline, 2L, 32.0, null, null));
            Check("F1 显式 Resync 恢复", recovery.Applied);
            Check("F1 恢复后不再冻结", !timeline.IsFrozen);
            CheckEqF("F1 恢复后冻结计时清零", 0.0, timeline.FrozenElapsedMs, 1e-9);
            CheckEq("F1 恢复未丢弃未确认输入", 0L, recovery.ResetUnconfirmedInputs);
            CheckEq("F1 未触发未确认输入重置通知", -1L, (long)resetNotified);

            Check("F1 恢复后可继续推进", timeline.Tick(16, InputOf(0f, 0f), PMFrameId.None).Accepted);
        }

        // ==================================================================================
        // G. 角色与帧命名空间
        // ==================================================================================

        private static void SectionG_RoleAndFrameNamespace()
        {
            Section("G. 只有 AutonomousProxy 回滚（D-R0-22）；帧命名空间禁止混算");

            TestModel spModel = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> sp = NewTimeline(spModel, 128, PMNetRole.SimulatedProxy);
            sp.Tick(16, InputOf(0f, 0f), PMFrameId.None);
            CheckEqEnum("G1 SimulatedProxy 调 ApplyAuthority 被拒",
                PMAuthorityRejectReason.RoleNotPredictive, sp.ApplyAuthority(AuthFrom(sp, 1L, 16.0, null, null)).Reject);
            CheckEq("G1 角色拒绝计数 1", 1L, sp.RoleRejections);
            CheckEq("G1 SP 上确认边界不推进", 0L, sp.ConfirmedFrame.Value);
            CheckEq("G1 SP 上模型未被重放", 1L, (long)spModel.SimulateCount);

            TestModel dsModel = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> ds = NewTimeline(dsModel, 128, PMNetRole.Authority);
            ds.Tick(16, InputOf(0f, 0f), PMFrameId.None);
            CheckEqEnum("G1 Authority 调 ApplyAuthority 被拒",
                PMAuthorityRejectReason.RoleNotPredictive, ds.ApplyAuthority(AuthFrom(ds, 1L, 16.0, null, null)).Reject);

            CheckThrows<InvalidOperationException>("G2 跨命名空间差值抛异常", delegate
            {
                long unused = InputFrame(1L) - ServerFrame(1L);
                GC.KeepAlive(unused);
            });
            CheckThrows<InvalidOperationException>("G2 跨命名空间顺序比较抛异常", delegate
            {
                bool unused = InputFrame(1L) < ServerFrame(1L);
                GC.KeepAlive(unused);
            });
            CheckThrows<InvalidOperationException>("G2 None 参与运算抛异常", delegate
            {
                long unused = PMFrameId.None - InputFrame(1L);
                GC.KeepAlive(unused);
            });
        }

        // ==================================================================================
        // H. SP 插值
        // ==================================================================================

        private static void SectionH_SpInterpolation()
        {
            Section("H. SP 只插值：离散取 To、连续最短弧、外推上限 0、静止也推进权威元数据");

            TestModel model = new TestModel();
            PMInterpolationBuffer<TestSync, TestAux> sp =
                PMInterpolationBuffer<TestSync, TestAux>.For<TestInput>(model);

            Check("H1 未收到权威前无值", !sp.HasAuthority);
            Check("H1 无快照时 Sample 返回 HasValue=false", !sp.Sample().HasValue);

            TestSync aSync = MakeSync();
            aSync.X = 0f;
            aSync.Yaw = 350f;
            aSync.Mode = 1;
            aSync.Grounded = 1;
            aSync.Layers = new int[] { 1 };
            TestAux aAux = MakeAux();

            PMPredictionSnapshot<TestSync, TestAux> a = new PMPredictionSnapshot<TestSync, TestAux>();
            a.Epoch = Epoch;
            a.InstanceId = Instance;
            a.OutputFrame = InputFrame(1L);
            a.ServerFrame = ServerFrame(10L);
            a.TotalSimTimeMs = 100.0;
            a.Sync = aSync;
            a.Aux = aAux;

            CheckEqEnum("H1 首包被接纳", PMInterpolationRejectReason.None, sp.OnAuthority(a));
            CheckEq("H1 绑定计数 1", 1L, sp.Bindings);

            PMInterpolatedState<TestSync, TestAux> first = sp.Sample();
            Check("H1 首包后可采样", first.HasValue);
            CheckEqF("H1 首包 Alpha=1（停在最新权威）", 1.0, first.Alpha, 1e-6);
            CheckEq("H1 首包元数据 OutputFrame=1", 1L, first.OutputFrame.Value);
            CheckEq("H1 首包元数据 ServerFrame=10", 10L, first.ServerFrame.Value);
            Check("H1 首包 ClampedToLatest", first.ClampedToLatest);

            // 推进表现时钟到窗口中间，再收第二包 → 正常插值
            sp.Advance(50.0);
            CheckEqF("H1 时钟推进到 150", 150.0, sp.SampleTimeMs, 1e-9);

            TestSync bSync = MakeSync();
            bSync.X = 10f;
            bSync.Yaw = 10f;
            bSync.Mode = 2;
            bSync.Grounded = 0;
            bSync.Layers = new int[] { 2 };
            TestAux bAux = MakeAux();
            bAux.GravityCm = -500;
            bAux.WorldVersion = 2;

            PMPredictionSnapshot<TestSync, TestAux> b = new PMPredictionSnapshot<TestSync, TestAux>();
            b.Epoch = Epoch;
            b.InstanceId = Instance;
            b.OutputFrame = InputFrame(2L);
            b.ServerFrame = ServerFrame(11L);
            b.TotalSimTimeMs = 200.0;
            b.Sync = bSync;
            b.Aux = bAux;

            CheckEqEnum("H2 第二包被接纳", PMInterpolationRejectReason.None, sp.OnAuthority(b));
            CheckEqF("H2 时钟在窗口内保持 150", 150.0, sp.SampleTimeMs, 1e-9);

            PMInterpolatedState<TestSync, TestAux> mid = sp.Sample();
            CheckEqF("H2 Alpha = (150-100)/(200-100) = 0.5", 0.5, mid.Alpha, 1e-6);
            CheckEqF("H2 连续量 X = 0 + (10-0)*0.5 = 5", 5.0, mid.Sync.X, 1e-5);
            CheckEqF("H2 连续量 yaw 最短弧：350 -> 10 走 +20 度的一半 = 0", 0.0, mid.Sync.Yaw, 1e-4);
            CheckEq("H2 离散 Mode Snap 到 To = 2", 2L, (long)mid.Sync.Mode);
            CheckEq("H2 离散 Grounded Snap 到 To = 0", 0L, (long)mid.Sync.Grounded);
            CheckEq("H2 离散 Layers Snap 到 To", 2L, (long)LayerAt(mid.Sync, 0));
            CheckEq("H2 Aux 一律取 To：WorldVersion=2", 2L, (long)mid.Aux.WorldVersion);
            CheckEq("H2 Aux 一律取 To：Gravity=-500", -500L, (long)mid.Aux.GravityCm);
            Check("H2 窗口内不算钳制", !mid.ClampedToLatest);

            // 超出最新快照：外推上限 0 → 停在最新权威值
            sp.Advance(10000.0);
            PMInterpolatedState<TestSync, TestAux> clamped = sp.Sample();
            CheckEqF("H3 超限后停在最新权威值 X=10", 10.0, clamped.Sync.X, 1e-6);
            CheckEqF("H3 超限后 Alpha=1", 1.0, clamped.Alpha, 1e-6);
            Check("H3 超限标记 ClampedToLatest", clamped.ClampedToLatest);
            Check("H3 渲染外推为 0（时钟超前但只报信息量）", sp.ExtrapolationMs > 0.0);

            // 静止（Sync 不变）也要推进已接收的权威元数据
            TestSync cSync = model.CloneSync(bSync);
            PMPredictionSnapshot<TestSync, TestAux> c = new PMPredictionSnapshot<TestSync, TestAux>();
            c.Epoch = Epoch;
            c.InstanceId = Instance;
            c.OutputFrame = InputFrame(3L);
            c.ServerFrame = ServerFrame(12L);
            c.TotalSimTimeMs = 300.0;
            c.Sync = cSync;
            c.Aux = model.CloneAux(bAux);

            CheckEqEnum("H4 静止包被接纳", PMInterpolationRejectReason.None, sp.OnAuthority(c));
            CheckEq("H4 静止也推进 OutputFrame=3", 3L, sp.LastOutputFrame.Value);
            CheckEq("H4 静止也推进 ServerFrame=12", 12L, sp.LastServerFrame.Value);
            CheckEqF("H4 静止也推进 TotalSimTimeMs=300", 300.0, sp.LastTotalSimTimeMs, 1e-9);
            PMInterpolatedState<TestSync, TestAux> staticSample = sp.Sample();
            CheckEqF("H4 静止采样仍是 To 的 X=10", 10.0, staticSample.Sync.X, 1e-6);
            CheckEqF("H4 静止采样元数据 TotalSimTimeMs=300", 300.0, staticSample.TotalSimTimeMs, 1e-9);

            // 迟到 / 错绑定 / 畸形 fail-closed
            PMPredictionSnapshot<TestSync, TestAux> stale = new PMPredictionSnapshot<TestSync, TestAux>();
            stale.Epoch = Epoch;
            stale.InstanceId = Instance;
            stale.OutputFrame = InputFrame(2L);
            stale.ServerFrame = ServerFrame(11L);
            stale.TotalSimTimeMs = 250.0;
            stale.Sync = model.CloneSync(bSync);
            stale.Aux = model.CloneAux(bAux);
            CheckEqEnum("H5 迟到包被拒", PMInterpolationRejectReason.StaleOrDuplicate, sp.OnAuthority(stale));
            CheckEqF("H5 被拒后基准不被刷新", 300.0, sp.LastTotalSimTimeMs, 1e-9);

            PMPredictionSnapshot<TestSync, TestAux> wrongEpoch = new PMPredictionSnapshot<TestSync, TestAux>();
            wrongEpoch.Epoch = Epoch + 1u;
            wrongEpoch.InstanceId = Instance;
            wrongEpoch.OutputFrame = InputFrame(4L);
            wrongEpoch.ServerFrame = ServerFrame(13L);
            wrongEpoch.TotalSimTimeMs = 400.0;
            wrongEpoch.Sync = model.CloneSync(bSync);
            wrongEpoch.Aux = model.CloneAux(bAux);
            CheckEqEnum("H5 错 epoch fail-closed", PMInterpolationRejectReason.EpochMismatch, sp.OnAuthority(wrongEpoch));

            wrongEpoch.Epoch = Epoch;
            wrongEpoch.InstanceId = Instance + 1u;
            CheckEqEnum("H5 错 instance fail-closed", PMInterpolationRejectReason.InstanceMismatch, sp.OnAuthority(wrongEpoch));

            wrongEpoch.InstanceId = Instance;
            wrongEpoch.Epoch = 0u;
            CheckEqEnum("H5 epoch=0 视为畸形", PMInterpolationRejectReason.Malformed, sp.OnAuthority(wrongEpoch));

            int rewindsBefore = (int)sp.RewindsIgnored;
            sp.Advance(-25.0);
            CheckEq("H6 表现时钟倒退被忽略", (long)rewindsBefore + 1L, sp.RewindsIgnored);

            // 停发恢复：宿主显式对齐（本类不做阈值启发式）
            TestModel recoveryModel = new TestModel();
            PMInterpolationBuffer<TestSync, TestAux> recovery =
                PMInterpolationBuffer<TestSync, TestAux>.For<TestInput>(recoveryModel);
            recovery.OnAuthority(a);
            recovery.Advance(10.0);
            PMPredictionSnapshot<TestSync, TestAux> resumed = new PMPredictionSnapshot<TestSync, TestAux>();
            resumed.Epoch = Epoch;
            resumed.InstanceId = Instance;
            resumed.OutputFrame = InputFrame(9L);
            resumed.ServerFrame = ServerFrame(99L);
            resumed.TotalSimTimeMs = 6000.0;
            resumed.Sync = model.CloneSync(bSync);
            resumed.Aux = model.CloneAux(bAux);
            recovery.OnAuthority(resumed);
            recovery.AlignToLatest();
            PMInterpolatedState<TestSync, TestAux> aligned = recovery.Sample();
            CheckEqF("H7 恢复时显式对齐到最新权威（无衰减回弹）", 10.0, aligned.Sync.X, 1e-6);
            CheckEqF("H7 对齐后 Alpha=1", 1.0, aligned.Alpha, 1e-6);

            // 结构性约束：SP 缓冲没有回滚 API，也不是按 TInput 参数化的
            MethodInfo[] methods = typeof(PMInterpolationBuffer<TestSync, TestAux>).GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            bool hasRollbackApi = false;
            for (int i = 0; i < methods.Length; i++)
            {
                string name = methods[i].Name;
                if (name.IndexOf("Reconcile", StringComparison.Ordinal) >= 0 ||
                    name.IndexOf("Restore", StringComparison.Ordinal) >= 0 ||
                    name.IndexOf("Rollback", StringComparison.Ordinal) >= 0 ||
                    name.IndexOf("Resimulate", StringComparison.Ordinal) >= 0 ||
                    name.IndexOf("ApplyAuthority", StringComparison.Ordinal) >= 0)
                {
                    hasRollbackApi = true;
                }
            }

            Check("H8 SP 缓冲不暴露任何 AP 回滚 API", !hasRollbackApi);
            CheckEq("H8 SP 缓冲只按 <TSync,TAux> 参数化", 2L, typeof(PMInterpolationBuffer<,>).GetGenericArguments().Length);
        }

        // ==================================================================================
        // I. DS 输入接纳与预算
        // ==================================================================================

        private static void SectionI_AuthorityInputBuffer()
        {
            Section("I. DS 输入接纳：连续帧、重复拒绝、乱序等待、信用 1:1 墙钟（初 50/上限 200）、每 Pump 8 步 100ms");

            // I1：基本准入 + 重复
            PMAuthorityInputBuffer<TestInput> buffer = NewBuffer();
            CheckEq("I1 初始水位 0", 0L, buffer.NextInputFrame);
            CheckEq("I1 初始信用 50", 50L, (long)buffer.CreditMs);

            Check("I1 帧0 被接纳", buffer.Admit(AuthorityInput(0L, 16)).Accepted);
            Check("I1 帧1 被接纳", buffer.Admit(AuthorityInput(1L, 17)).Accepted);
            Check("I1 帧2 被接纳", buffer.Admit(AuthorityInput(2L, 16)).Accepted);
            CheckEq("I1 队列 3 条", 3L, buffer.QueuedCount);

            PMInputAdmissionResult duplicate = buffer.Admit(AuthorityInput(0L, 16));
            CheckEqEnum("I1 重复帧被拒", PMInputRejectReason.DuplicateOrStale, duplicate.Reject);
            CheckEq("I1 重复计数 1", 1L, buffer.DuplicateRejections);
            CheckEq("I1 重复不改变队列", 3L, buffer.QueuedCount);
            CheckEq("I1 重复不改变水位", 0L, buffer.NextInputFrame);

            List<PMAuthorityInput<TestInput>> ready = new List<PMAuthorityInput<TestInput>>();
            PMPumpResult pump0 = buffer.Pump(0.0, ready);
            CheckEq("I1 信用 50 只够 3 步（16+17+16=49ms）", 3L, pump0.Steps);
            CheckEq("I1 首次 Pump 消耗 49ms", 49L, pump0.ConsumedMs);
            CheckEqF("I1 剩余信用 1", 1.0, pump0.CreditMsAfter, 1e-9);
            CheckEqEnum("I1 队列已空", PMPumpDeferReason.Idle, pump0.Defer);
            Check("I1 取出的输入按帧号升序", PendingInputAt(ready, 0) == 0L && PendingInputAt(ready, 1) == 1L && PendingInputAt(ready, 2) == 2L);
            Check("I1 取出的是深克隆（与队列脱钩）",
                ready.Count > 1 && !ReferenceEquals(ready[0].Input, ready[1].Input));

            PMInputAdmissionResult stale = buffer.Admit(AuthorityInput(0L, 16));
            CheckEqEnum("I1 已消费帧再上报视为迟到", PMInputRejectReason.DuplicateOrStale, stale.Reject);
            CheckEq("I1 重复计数 2", 2L, buffer.DuplicateRejections);

            // I2：乱序等待（缺帧不跨越）
            PMAuthorityInputBuffer<TestInput> gapBuffer = NewBuffer();
            Check("I2 帧2 先到被接纳（未来窗口内）", gapBuffer.Admit(AuthorityInput(2L, 16)).Accepted);
            List<PMAuthorityInput<TestInput>> gapReady = new List<PMAuthorityInput<TestInput>>();
            PMPumpResult gapPump = gapBuffer.Pump(0.0, gapReady);
            CheckEqEnum("I2 缺帧时只等待", PMPumpDeferReason.MissingFrameGap, gapPump.Defer);
            CheckEq("I2 缺帧时不消耗任何步", 0L, gapPump.Steps);
            CheckEq("I2 缺帧时水位不动", 0L, gapBuffer.NextInputFrame);

            Check("I2 补上帧0", gapBuffer.Admit(AuthorityInput(0L, 16)).Accepted);
            gapReady.Clear();
            PMPumpResult gapPump2 = gapBuffer.Pump(0.0, gapReady);
            CheckEq("I2 只消费连续帧0", 1L, gapPump2.Steps);
            CheckEq("I2 水位推进到 1", 1L, gapBuffer.NextInputFrame);
            Check("I2 绝不跨越（消费的是帧0）", PendingInputAt(gapReady, 0) == 0L);

            Check("I2 补上帧1", gapBuffer.Admit(AuthorityInput(1L, 16)).Accepted);
            gapReady.Clear();
            PMPumpResult gapPump3 = gapBuffer.Pump(0.0, gapReady);
            CheckEq("I2 连续后一次消费 2 步", 2L, gapPump3.Steps);
            CheckEq("I2 水位推进到 3", 3L, gapBuffer.NextInputFrame);

            // I3：拒绝的种类与无副作用
            PMAuthorityInputBuffer<TestInput> rejectBuffer = NewBuffer();
            CheckEqEnum("I3 错 epoch 被拒", PMInputRejectReason.EpochMismatch, rejectBuffer.Admit(AuthorityInput(0L, 16, Epoch + 1u, Instance)).Reject);
            CheckEqEnum("I3 错 instance 被拒", PMInputRejectReason.InstanceMismatch, rejectBuffer.Admit(AuthorityInput(0L, 16, Epoch, Instance + 1u)).Reject);
            CheckEqEnum("I3 epoch=0 被拒", PMInputRejectReason.MalformedEpochOrInstance, rejectBuffer.Admit(AuthorityInput(0L, 16, 0u, Instance)).Reject);
            CheckEqEnum("I3 dt=51 被拒", PMInputRejectReason.MalformedStepMs, rejectBuffer.Admit(AuthorityInput(0L, 51)).Reject);
            CheckEqEnum("I3 dt=-1 被拒", PMInputRejectReason.MalformedStepMs, rejectBuffer.Admit(AuthorityInput(0L, -1)).Reject);
            CheckEqEnum("I3 超未来窗口被拒", PMInputRejectReason.BeyondWindow, rejectBuffer.Admit(AuthorityInput(129L, 16)).Reject);
            CheckEq("I3 epoch 拒绝 1", 1L, rejectBuffer.EpochRejections);
            CheckEq("I3 instance 拒绝 1", 1L, rejectBuffer.InstanceRejections);
            CheckEq("I3 畸形拒绝 3（epoch=0 / dt=51 / dt=-1）", 3L, rejectBuffer.MalformedRejections);
            CheckEq("I3 超窗口拒绝 1", 1L, rejectBuffer.BeyondWindowRejections);
            CheckEq("I3 全部拒绝后队列为空", 0L, rejectBuffer.QueuedCount);
            CheckEq("I3 全部拒绝后水位仍 0", 0L, rejectBuffer.NextInputFrame);
            CheckEq("I3 全部拒绝后信用仍是 50", 50L, (long)rejectBuffer.CreditMs);

            PMAuthorityInputBuffer<TestInput> fullBuffer = NewBuffer(queueCapacity: 2);
            Check("I3 帧0 入队", fullBuffer.Admit(AuthorityInput(0L, 16)).Accepted);
            Check("I3 帧1 入队", fullBuffer.Admit(AuthorityInput(1L, 16)).Accepted);
            CheckEqEnum("I3 队列容量失败", PMInputRejectReason.QueueFull, fullBuffer.Admit(AuthorityInput(2L, 16)).Reject);
            CheckEq("I3 队列容量失败无副作用", 2L, fullBuffer.QueuedCount);

            // I4：信用逐 Pump 累积与输入时间上限
            PMAuthorityInputBuffer<TestInput> creditBuffer = NewBuffer();
            for (int i = 0; i < 10; i++) { creditBuffer.Admit(AuthorityInput((long)i, 16)); }

            List<PMAuthorityInput<TestInput>> creditReady = new List<PMAuthorityInput<TestInput>>();
            PMPumpResult creditPump1 = creditBuffer.Pump(10.0, creditReady);
            CheckEq("I4 信用 60 先取 3 步（48ms）", 3L, creditPump1.Steps);
            CheckEqF("I4 余 12 信用不足以再取 16ms 一步", 12.0, creditPump1.CreditMsAfter, 1e-9);
            CheckEqEnum("I4 信用不足延后", PMPumpDeferReason.InsufficientCredit, creditPump1.Defer);
            CheckEq("I4 延后不推水位过界（已取 3 步）", 3L, creditBuffer.NextInputFrame);

            creditReady.Clear();
            PMPumpResult creditPump2 = creditBuffer.Pump(100.0, creditReady);
            CheckEq("I4 每 Pump 输入时间上限 100ms：只取 6 步", 6L, creditPump2.Steps);
            CheckEq("I4 6 步 = 96ms", 96L, creditPump2.ConsumedMs);
            CheckEqEnum("I4 触发输入时间上限", PMPumpDeferReason.InputTimeLimit, creditPump2.Defer);
            CheckEqF("I4 信用 112-96=16", 16.0, creditPump2.CreditMsAfter, 1e-9);

            creditReady.Clear();
            PMPumpResult creditPump3 = creditBuffer.Pump(50.0, creditReady);
            CheckEq("I4 剩余 1 步后队列空", 1L, creditPump3.Steps);
            CheckEqEnum("I4 空闲", PMPumpDeferReason.Idle, creditPump3.Defer);
            CheckEq("I4 水位推进到 10", 10L, creditBuffer.NextInputFrame);
            CheckEq("I4 累计 10 步", 10L, creditBuffer.StepsPumped);

            // I5：步数上限与信用上限
            PMAuthorityInputBuffer<TestInput> stepBuffer = NewBuffer();
            for (int i = 0; i < 10; i++) { stepBuffer.Admit(AuthorityInput((long)i, 1)); }
            List<PMAuthorityInput<TestInput>> stepReady = new List<PMAuthorityInput<TestInput>>();
            PMPumpResult stepPump = stepBuffer.Pump(1000.0, stepReady);
            CheckEq("I5 每 Pump 最多 8 步", 8L, stepPump.Steps);
            CheckEqEnum("I5 触发步数上限", PMPumpDeferReason.StepLimit, stepPump.Defer);
            CheckEqF("I5 Pump 前信用为初始 50", 50.0, stepPump.CreditMsBefore, 1e-9);
            CheckEqF("I5 8 步后信用 = min(200, 50+1000) - 8 = 192", 192.0, stepPump.CreditMsAfter, 1e-9);
            stepReady.Clear();
            PMPumpResult stepPump2 = stepBuffer.Pump(0.0, stepReady);
            CheckEq("I5 下一 Pump 取完剩余 2 步", 2L, stepPump2.Steps);
            CheckEq("I5 水位推进到 10", 10L, stepBuffer.NextInputFrame);

            PMAuthorityInputBuffer<TestInput> capBuffer = NewBuffer();
            List<PMAuthorityInput<TestInput>> capReady = new List<PMAuthorityInput<TestInput>>();
            capBuffer.Pump(10000.0, capReady);
            CheckEqF("I5 信用永不超过 200", 200.0, capBuffer.CreditMs, 1e-9);

            // I6：时钟倒退不产生信用（空队列，只看信用与步数）
            PMAuthorityInputBuffer<TestInput> rewindBuffer = NewBuffer();
            double creditBefore = rewindBuffer.CreditMs;
            PMPumpResult rewindPump = rewindBuffer.Pump(-500.0, new List<PMAuthorityInput<TestInput>>());
            CheckEqF("I6 时钟倒退不增加信用", creditBefore, rewindPump.CreditMsAfter, 1e-9);
            CheckEq("I6 时钟倒退不消耗步", 0L, rewindPump.Steps);
            CheckEq("I6 时钟倒退被计数", 1L, rewindBuffer.WallClockRewindsIgnored);

            // I7：0 占位消耗步但不花信用
            PMAuthorityInputBuffer<TestInput> placeholderBuffer = NewBuffer();
            Check("I7 0 占位被接纳", placeholderBuffer.Admit(AuthorityInput(0L, 0)).Accepted);
            List<PMAuthorityInput<TestInput>> placeholderReady = new List<PMAuthorityInput<TestInput>>();
            PMPumpResult placeholderPump = placeholderBuffer.Pump(0.0, placeholderReady);
            CheckEq("I7 0 占位消耗 1 步", 1L, placeholderPump.Steps);
            CheckEq("I7 0 占位计为 Placeholder", 1L, placeholderPump.Placeholders);
            CheckEq("I7 0 占位不计输入时间", 0L, placeholderPump.ConsumedMs);
            CheckEqF("I7 0 占位不花信用", 50.0, placeholderPump.CreditMsAfter, 1e-9);
            CheckEq("I7 0 占位仍推进水位", 1L, placeholderBuffer.NextInputFrame);

            // I8：缺帧超时 → 请求 Resync，且绝不跨越
            PMAuthorityInputBuffer<TestInput> timeoutBuffer = NewBuffer();
            timeoutBuffer.Admit(AuthorityInput(1L, 16));
            int resyncNotified = 0;
            timeoutBuffer.ResyncRequested += delegate { resyncNotified++; };
            List<PMAuthorityInput<TestInput>> timeoutReady = new List<PMAuthorityInput<TestInput>>();
            timeoutBuffer.Pump(100.0, timeoutReady);
            timeoutBuffer.Pump(100.0, timeoutReady);
            CheckEqF("I8 缺帧等待累计 200", 200.0, timeoutBuffer.MissingFrameElapsedMs, 1e-9);
            CheckEq("I8 200ms 尚未超时", 0L, (long)resyncNotified);
            PMPumpResult timeoutPump = timeoutBuffer.Pump(400.0, timeoutReady);
            Check("I8 600ms 超过 500ms 阈值 → 请求重同步", timeoutPump.ResyncRequested);
            CheckEq("I8 通知 1 次", 1L, (long)resyncNotified);
            CheckEq("I8 超时计数 1", 1L, timeoutBuffer.MissingFrameTimeouts);
            Check("I8 进入 NeedsResync", timeoutBuffer.NeedsResync);
            CheckEq("I8 队列被清空", 0L, timeoutBuffer.QueuedCount);
            CheckEq("I8 水位从未被跨越", 0L, timeoutBuffer.NextInputFrame);
            CheckEqEnum("I8 超时后准入被拒", PMInputRejectReason.NeedsResync, timeoutBuffer.Admit(AuthorityInput(1L, 16)).Reject);

            int dropped = timeoutBuffer.Resync(Epoch, Instance, 1L);
            CheckEq("I8 Resync 丢弃 0 条", 0L, (long)dropped);
            Check("I8 Resync 后解除 NeedsResync", !timeoutBuffer.NeedsResync);
            CheckEq("I8 Resync 后水位落到权威位置 1", 1L, timeoutBuffer.NextInputFrame);
            Check("I8 Resync 后可继续接纳", timeoutBuffer.Admit(AuthorityInput(1L, 16)).Accepted);
        }

        private static PMAuthorityInputBuffer<TestInput> NewBuffer(int queueCapacity = 128)
        {
            TestModel cloner = new TestModel();
            return new PMAuthorityInputBuffer<TestInput>(
                cloner.CloneInput, Epoch, Instance, queueCapacity);
        }

        private static PMAuthorityInput<TestInput> AuthorityInput(long frame, int stepMs)
        {
            return AuthorityInput(frame, stepMs, Epoch, Instance);
        }

        private static PMAuthorityInput<TestInput> AuthorityInput(long frame, int stepMs, uint epoch, uint instance)
        {
            PMAuthorityInput<TestInput> input = new PMAuthorityInput<TestInput>();
            input.Epoch = epoch;
            input.InstanceId = instance;
            input.InputFrame = frame;
            input.StepMs = stepMs;
            input.Input = InputOf(0.5f, 0f);
            return input;
        }

        // ==================================================================================
        // J. 缓冲 + 时间轴集成
        // ==================================================================================

        private static void SectionJ_BufferTimelineIntegration()
        {
            Section("J. DS 缓冲 → AP 时间轴：0 占位不调模型但仍消耗边界、原始 dt 不被拆分/放大");

            TestModel model = new TestModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> timeline = NewTimeline(model);
            PMAuthorityInputBuffer<TestInput> buffer = new PMAuthorityInputBuffer<TestInput>(
                model.CloneInput, Epoch, Instance);

            buffer.Admit(AuthorityInput(0L, 16));
            buffer.Admit(AuthorityInput(1L, 0));
            buffer.Admit(AuthorityInput(2L, 17));
            buffer.Admit(AuthorityInput(3L, 16));

            List<PMAuthorityInput<TestInput>> steps = new List<PMAuthorityInput<TestInput>>();
            PMPumpResult pump = buffer.Pump(1000.0, steps);
            CheckEq("J1 一次 Pump 取 4 步", 4L, pump.Steps);
            CheckEq("J1 输入时间合计 16+0+17+16 = 49ms", 49L, pump.ConsumedMs);
            CheckEq("J1 其中 1 个占位", 1L, pump.Placeholders);

            for (int i = 0; i < steps.Count; i++)
            {
                PMAuthorityInput<TestInput> step = steps[i];
                timeline.Tick(step.StepMs, step.Input, ServerFrame(step.InputFrame + 1L));
            }

            CheckEq("J1 边界推进 4（0 占位也消耗边界）", 4L, timeline.PendingFrame.Value);
            CheckEq("J1 占位步 1 次", 1L, timeline.PlaceholderSteps);
            CheckEq("J1 模型只被调用 3 次", 3L, model.SimulateCount);
            CheckEqF("J1 累计仿真时间 49ms（占位不计时）", 49.0, timeline.CurrentTotalSimTimeMs, 1e-9);
            CheckEqF("J1 X = 0.5*(16+17+16) = 24.5", 24.5, timeline.GetSyncSnapshot().X, 1e-5);
            Check("J1 模型看到的 dt 序列是 16/17/16", StepMsAt(model, 0) == 16L && StepMsAt(model, 1) == 17L && StepMsAt(model, 2) == 16L);
            Check("J1 不存在被拆分/放大的 dt", StepMsAt(model, 0) + StepMsAt(model, 1) + StepMsAt(model, 2) == 49L);
        }

        // ==================================================================================
        // K. 契约强制
        // ==================================================================================

        private static void SectionK_ContractEnforcement()
        {
            Section("K. 模型违约时必须显式失败，不得静默继续");

            TestModel nullSyncModel = new NullSyncModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> nullSyncTimeline = NewTimeline(nullSyncModel);
            CheckThrows<InvalidOperationException>("K1 模型返回 null Sync 时抛异常", delegate
            {
                nullSyncTimeline.Tick(16, InputOf(0f, 0f), PMFrameId.None);
            });
            CheckEq("K1 失败后未推进边界", 0L, nullSyncTimeline.PendingFrame.Value);

            TestModel nullResultModel = new NullResultModel();
            PMPredictionTimeline<TestInput, TestSync, TestAux> nullResultTimeline = NewTimeline(nullResultModel);
            CheckThrows<InvalidOperationException>("K2 模型返回 null 结果时抛异常", delegate
            {
                nullResultTimeline.Tick(16, InputOf(0f, 0f), PMFrameId.None);
            });
            CheckEq("K2 失败后未推进边界", 0L, nullResultTimeline.PendingFrame.Value);

            CheckThrows<ArgumentNullException>("K3 构造时拒绝 null 模型", delegate
            {
                PMPredictionTimeline<TestInput, TestSync, TestAux> unused =
                    new PMPredictionTimeline<TestInput, TestSync, TestAux>(null, Epoch, Instance, MakeSync(), MakeAux());
                GC.KeepAlive(unused);
            });
            CheckThrows<ArgumentNullException>("K3 构造时拒绝 null 初始 Sync", delegate
            {
                PMPredictionTimeline<TestInput, TestSync, TestAux> unused =
                    new PMPredictionTimeline<TestInput, TestSync, TestAux>(new TestModel(), Epoch, Instance, null, MakeAux());
                GC.KeepAlive(unused);
            });
            CheckThrows<ArgumentNullException>("K4 输入缓冲拒绝 null cloner", delegate
            {
                PMAuthorityInputBuffer<TestInput> unused =
                    new PMAuthorityInputBuffer<TestInput>(null, Epoch, Instance);
                GC.KeepAlive(unused);
            });
            CheckThrows<ArgumentNullException>("K4 SP 缓冲拒绝 null interpolate", delegate
            {
                PMInterpolationBuffer<TestSync, TestAux> unused =
                    new PMInterpolationBuffer<TestSync, TestAux>(null, new TestModel().CloneSync, new TestModel().CloneAux);
                GC.KeepAlive(unused);
            });

            CheckThrows<ArgumentOutOfRangeException>("K5 事件 key=0 被拒", delegate
            {
                PMPredictionEvent unused = new PMPredictionEvent(0UL, 1, 1);
                GC.KeepAlive(unused);
            });
        }
    }
}
