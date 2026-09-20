// ============================================================================
//  PMMoverPredictionTest —— R4-A / P4A4：真实预测核心 + 真实 PMMoverModel 集成门禁
// ============================================================================
//
//  事实来源：
//    · Docs/plans/net-r4-prediction-contract.md（冻结契约：输入帧 n→边界 n+1 / 深 clone /
//      重放必须用原始输入与原 dt / 确认事件只随 ConfirmedFrame 一次 / DS 预算 8 步 100ms +
//      墙钟信用 50..200 / SP 首批只插值不外推 / Mover 冻结字段与机制）
//    · Docs/plans/net-architecture-migration.md 文末 R4-A 行 P4A4
//      「真实预测核心+Mover模型 | PMMoverPredictionTest | 强制全状态差异后收敛」
//    · Docs/plans/_r4a_prediction_report.md（P4A1/P4A2 的实际公开 API）
//    · Docs/plans/_r4a_mover_report.md（P4A3 的实际公开 API）
//
//  做法（不写任何"第二套 reconcile oracle"，不复制 timeline 实现）：
//    · 直接编真实源码：PMPrediction/**（契约 + timeline + authority input buffer +
//      interpolation buffer）+ PMMover/**（model/state/collision/test world）+ PMNet 帧与角色原语；
//    · AP：真实 PMMoverModel 装进真实 PMPredictionTimeline（AutonomousProxy 才允许回滚）；
//    · DS：真实 PMAuthorityInputBuffer 驱动"同一模型的第二个实例"，按服务器墙钟预算出步；
//    · 碰撞：真实 PMMoverTestWorld 的 floor + wall（不是空世界），
//      且校正点选在"墙前 / 落地 / 模式切换"三类真实帧上；
//    · 预期值：用真实模型从一个**修正后**的边界逐输入 Simulate 得到 reference
//      （reference 只驱动序列与状态链，不复制 timeline 的恢复/重放/事件逻辑）。
//
//  诚实边界（报告"未实现/未声称"逐条对应）：
//    · 不验证 Unity PhysX 等价性（PMMoverTestWorld 是确定性 AABB 替身）；
//    · 不实现/不声称：行为类 Modifier 实例级补偿、效果拒绝裁决、真实网络承载（R4-B）、
//      斜坡台阶、移动平台 BaseCarry、空中操控、子步循环。
//
//  权威事件证据（本轮修复）：
//    · ApplyAuthority 在 frozen / NeedsResync 期间一律返回 PMAuthorityRejectReason.Stalled，
//      预测状态 / 确认边界 / 已确认事件零改动；恢复只走显式可信 Resync（第 K 节回归）。
//    · 不可逆事件只广播「携带该输出边界权威事件证据」的边界
//      （PMPredictionSnapshot.ConfirmedEvents / ConfirmedEventFrames）；缺证据一律不广播，
//      绝不用预测事件冒充权威（第 F/L 节）。
//
//  运行：dotnet Tools/PMMoverPredictionTest/bin/Release/net8.0/PMMoverPredictionTest.dll
//  退出码：0 = 全部通过；1 = 存在失败
// ============================================================================

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using PMNet;
using PMNet.Mover;
using PMNet.Prediction;

namespace PMMoverPredictionTest
{
    internal static class Program
    {
        private static int _passed;
        private static readonly List<string> _failures = new List<string>();

        // 会话绑定：非 0（契约要求每实例绑定 SessionEpoch + InstanceId）
        private const uint Epoch = 0x51A1u;
        private const uint Instance = 0x0002u;
        private const int HistoryCapacity = 128;
        private const long DisconnectTimeoutMs = 2000L;
        private const long FutureWindow = 128L;

        private static Scenario _scenario;

        private static int Main()
        {
            Console.WriteLine("=== R4-A / P4A4：真实预测核心 + 真实 PMMoverModel 集成门禁 ===");
            Console.WriteLine();

            Section("A. 装配面：真实类型/双世界/角色门/无 Finalize/无回滚 API/无静态可变状态", TestAssemblySurface);
            Section("B. 双环境一致性：AP 预测 == DS 权威（逐位）+ 真墙/落地/模式切换帧存在性", TestPredictionMatchesAuthority);
            Section("C. 校正起点（边界 0）重放：无确认事件、无外部回调、全区间 reference 匹配", TestReplayAtBoundaryZero);
            Section("D. 墙前校正：六类强制差异 + 重放 == 独立 reference + 原输入原 dt", TestWallCorrection);
            Section("E. 落地帧收敛 / 模式切换帧零回滚", TestLandingAndModeSwitch);
            Section("F. 晚帧强制校正 + 确认事件只发一次 + 同帧事件集合被回滚替换", TestLateCorrectionAndEvents);
            Section("G. 权威/重同步拒绝矩阵 + 历史满 resync + 断线墙钟", TestRejectMatrixAndHistoryFull);
            Section("H. DS 输入预算：重复/乱序/缺帧/信用/步数与 ms 上限/容量/占位", TestAuthorityInputBudget);
            Section("I. SP 时间插值：yaw 最短弧/离散取 To/不外推/无回滚 API", TestSpInterpolation);
            Section("J. clone 别名隔离 / 原输入与 dt 保真 / 计时与计数不变式", TestCloneAndFidelityInvariants);
            Section("K. Stall 门回归（冻结/待重同步期间 ApplyAuthority 必须被拒绝）", TestUpstreamDeviationRegression);
            Section("L. 权威事件证据（真实 Mover 模型）：无证据不广播/深克隆/跳确认不伪造/有界校验", TestAuthorityEventEvidence);

            if (_scenario != null)
            {
                Console.WriteLine("  [集成事实] 确认边界=" + _scenario.Timeline.ConfirmedFrame.Value
                    + "；累计重放步数=" + _scenario.Timeline.ReplayedSteps
                    + "；确认事件=" + _scenario.EmittedKeys.Count
                    + "；DS 出步=" + _scenario.DsSyncAt.Count + " 个边界"
                    + "；真实碰撞查询 Sweep/QueryGround=" + _scenario.ApWorld.Sweeps + "/"
                    + _scenario.ApWorld.Grounds);
            }

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

            Console.WriteLine();
            Console.WriteLine("  说明：本轮已把 K 节从「上游契约偏离复现（预期 FAIL）」改成严格正确断言，"
                              + "并新增 L 节（权威事件证据）；任何失败都是回归而非已知缺口。");
            return 1;
        }

        // =================================================================================
        //  A. 装配面
        // =================================================================================

        private static void TestAssemblySurface()
        {
            Check(typeof(IPMPredictionModel<PMMoverInput, PMMoverSyncState, PMMoverAuxState>)
                      .IsAssignableFrom(typeof(PMMoverModel)),
                "A1 真实 PMMoverModel 实现共享契约 IPMPredictionModel<MoverInput,MoverSync,MoverAux>");

            Type timelineType = typeof(PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState>);
            Check(timelineType.IsSealed, "A2 PMPredictionTimeline 是 sealed（契约形状）");
            Check(timelineType.GetGenericArguments().Length == 3, "A3 Timeline 三个类型参数（TInput/TSync/TAux）");

            // 契约：「Finalize 由宿主在一批校正/模拟后调用一次，不在 resim 循环内部」。
            // 实现里根本不存在 Finalize 入口 → resim 循环内不可能回调宿主。
            Check(!HasMethodNamed(timelineType, "Finalize"),
                "A4 PMPredictionTimeline 没有任何 Finalize 外部回调入口（resim 内不可能回调宿主）");
            Check(!HasMethodNamed(timelineType, "OnFinalize") && !HasMethodNamed(timelineType, "FinalizeStep"),
                "A5 也没有 OnFinalize/FinalizeStep 变体入口");

            Type spType = typeof(PMInterpolationBuffer<PMMoverSyncState, PMMoverAuxState>);
            Check(spType.GetGenericArguments().Length == 2,
                "A6 PMInterpolationBuffer 只有 <TSync,TAux> 两个类型参数（不依赖 TInput / 不引 AP）");
            string[] banned = new string[] { "Reconcile", "Restore", "Rollback", "ApplyAuthority", "Resimulate" };
            for (int i = 0; i < banned.Length; i++)
            {
                Check(!HasMethodNamed(spType, banned[i]),
                    "A7 SP 无 " + banned[i] + " 类 API（首批只插值、不回滚）");
            }

            // 纯模型：不允许静态可变状态（跨实例串扰 / 墙钟 / 缓存都会让重放发散）
            Check(!HasStaticMutableField(typeof(PMMoverModel)), "A8 PMMoverModel 无静态可变字段");
            Check(!HasStaticMutableField(typeof(PMMoverTestWorld)), "A9 PMMoverTestWorld 无静态可变字段");
            Check(!HasStaticMutableField(timelineType), "A10 PMPredictionTimeline 无静态可变字段");
            Check(!HasStaticMutableField(spType), "A11 PMInterpolationBuffer 无静态可变字段");
            Check(!HasStaticMutableField(typeof(PMAuthorityInputBuffer<PMMoverInput>)), "A12 PMAuthorityInputBuffer 无静态可变字段");

            // 跨模块的冻结常量一致性（真实落点检查，不是自比）
            Check(PMMoverDefaults.MinStepMs == PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState>.MinStepMs
                  && PMMoverDefaults.MaxStepMs == PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState>.MaxStepMs,
                "A13 Mover 与 Timeline 的整毫秒 dt 范围一致（1..50）");
            Check(PMAuthorityInputBuffer<PMMoverInput>.MinStepMs == PMMoverDefaults.MinStepMs
                  && PMAuthorityInputBuffer<PMMoverInput>.MaxStepMs == PMMoverDefaults.MaxStepMs,
                "A14 DS 输入缓冲与 Mover 的 dt 范围一致（1..50，0=缺帧占位）");
            Check(PMInterpolationBuffer<PMMoverSyncState, PMMoverAuxState>.DefaultMaxExtrapolateMs == 0L,
                "A15 SP 外推上限为 0（首批只插值，不外推）");
        }

        // =================================================================================
        //  B. 双环境一致性 + 场景真实性
        // =================================================================================

        private static void TestPredictionMatchesAuthority()
        {
            Scenario s = GetScenario();

            // --- 两个**独立**碰撞世界实例，同 version（契约：两模型环境独立实例同 version）
            Check(!object.ReferenceEquals(s.WorldA, s.WorldB), "B1 AP/DS 使用两个独立世界实例");
            Check(s.WorldA.WorldVersion == s.WorldB.WorldVersion,
                "B2 两世界 WorldVersion 相同（" + s.WorldA.WorldVersion + "）");
            Check(s.WorldA.WorldVersion == (int)PMMoverTestWorld.FrozenCollisionWorldVersion,
                "B3 WorldVersion == 冻结测试布局 0x52334201");
            Check(s.ApModel.CollisionQuery == s.ApWorld && s.ApWorld.Inner == s.WorldA,
                "B4 AP 模型持有的正是注入的计数世界（注入点唯一）");
            Check(s.DsModel.CollisionQuery == s.WorldB, "B5 DS 模型持有自己的世界实例");
            Check(s.WorldA.BoxCount == 2 && s.WorldB.BoxCount == 2, "B6 两世界都是 floor + wall 两个盒（非空世界）");

            PMMoverTestBox fa = s.WorldA.GetBox(PMMoverTestWorld.FloorBoxIndex);
            PMMoverTestBox fb = s.WorldB.GetBox(PMMoverTestWorld.FloorBoxIndex);
            PMMoverTestBox wa = s.WorldA.GetBox(PMMoverTestWorld.WallBoxIndex);
            PMMoverTestBox wb = s.WorldB.GetBox(PMMoverTestWorld.WallBoxIndex);
            Check(SameBox(fa, fb) && SameBox(wa, wb), "B7 两世界几何逐位相同（同 floor/同 wall）");

            // --- dt 变序列（契约：测试 AP 保存输入 dt 变序列）
            List<int> used = new List<int>();
            for (int i = 0; i < s.Script.Count; i++)
            {
                if (!used.Contains(s.Script[i].Dt)) { used.Add(s.Script[i].Dt); }
            }

            used.Sort();
            Check(used.Contains(0), "B8 序列含 0（缺帧占位）");
            Check(used.Contains(1) && used.Contains(50), "B9 序列含 dt 下界 1 与上界 50");
            Check(used.Count >= 4, "B10 序列是多频 dt（实际取值 " + JoinInts(used) + "）");

            // --- AP 预测 == DS 权威（逐位）：两个独立世界/两个模型实例的确定性证据
            int mismatches = 0;
            string firstMismatch = null;
            for (long b = 0; b <= s.BoundaryCount; b++)
            {
                string diff = SyncDifference(s.PreCorrectionAp[(int)b].Sync, s.DsSyncAt[(int)b]);
                if (diff != null)
                {
                    mismatches++;
                    if (firstMismatch == null) { firstMismatch = "边界 " + b + "：" + diff; }
                }
            }

            Check(mismatches == 0,
                "B11 AP 预测状态 == DS 权威状态（全部 " + (s.BoundaryCount + 1) + " 个边界逐位相同"
                + (firstMismatch == null ? "）" : "；首个不一致 " + firstMismatch + "）"));

            double apTotal = s.PreCorrectionAp[(int)s.BoundaryCount].TotalSimTimeMs;
            Check(Math.Abs(apTotal - s.DsTotalAt[(int)s.BoundaryCount]) < 1e-9,
                "B12 AP 累计仿真时间 == DS 累计（" + apTotal + " ms）");

            // --- DS 预算没有合并/放大 dt：注入的 dt 就是模拟用的 dt
            int expectedSim = 0;
            int expectedPlaceholders = 0;
            int expectedMs = 0;
            for (int i = 0; i < s.Script.Count; i++)
            {
                if (s.Script[i].Dt != 0) { expectedSim++; } else { expectedPlaceholders++; }
                expectedMs += s.Script[i].Dt;
            }

            Check(s.DsStepsPumped == s.Script.Count,
                "B13 DS 出步数 == 序列帧数（含占位：" + s.DsStepsPumped + "/" + s.Script.Count + "）");
            Check(s.DsPumpedMs == expectedMs,
                "B14 DS 累计输入时间 == 序列原 dt 之和（" + s.DsPumpedMs + "/" + expectedMs + " ms，无合并无放大）");
            Check(s.DsBuffer.SimulatedStepsPumped == expectedSim
                  && s.DsBuffer.PlaceholdersPumped == expectedPlaceholders,
                "B14b DS 侧非占位/占位出步数分别正确（" + s.DsBuffer.SimulatedStepsPumped + "/"
                + s.DsBuffer.PlaceholdersPumped + "）");
            Check(s.DsBuffer.NextInputFrame == s.BoundaryCount && s.DsBuffer.QueuedCount == 0,
                "B15 DS 追平序列（全部输入被消费，队列空）");

            // --- 真实墙：接触平面是手算几何，不是"随便停住"
            Check(s.WallContactFrame >= 0, "B16 场景确实撞到墙（检测到接触帧）");
            Check(s.WallApproachFrame >= 0 && s.WallApproachFrame < s.WallContactFrame,
                "B17 存在墙前逼近帧（frame " + s.WallApproachFrame + "）早于接触帧（" + s.WallContactFrame + "）");

            const float wallFaceX = 0.5f;                                  // WallCenter.X 0 + WallSize.X/2 0.5
            float contactPlane = wallFaceX + PMMoverDefaults.CapsuleRadiusMeters
                                 - PMMoverDefaults.CollisionSkinMeters;   // 0.5 + 0.4 - 0.001 = 0.899
            float minX = float.MaxValue;
            for (long b = 0; b <= s.BoundaryCount; b++)
            {
                float x = s.PreCorrectionAp[(int)b].Sync.Position.X;
                if (x < minX) { minX = x; }
            }

            Check(minX >= contactPlane - 0.002f,
                "B18 全程最小 X = " + minX.ToString("F6") + " >= 接触平面 " + contactPlane.ToString("F4")
                + "（膨胀盒面 - skin）：50ms 满速步也不穿透");
            PMMoverSyncState contact = s.PreCorrectionAp[(int)(s.WallContactFrame + 1)].Sync;
            Check(contact.Position.X >= contactPlane - 0.002f && contact.Position.X <= wallFaceX + 0.401f,
                "B19 接触帧 X = " + contact.Position.X.ToString("F6") + " 落在手算接触带 [0.897,0.901]");
            Check(contact.Velocity.X <= -3.5f,
                "B20 接触帧仍保留朝墙速度 X = " + contact.Velocity.X.ToString("F3") + "（是几何阻挡，不是制动）");
            Check(contact.Grounded && contact.Mode == PMMoverMode.Walking, "B21 贴墙站立时仍判定着地且 Walking");

            // --- 真实落地：中心 Y 精确回到 halfHeight
            Check(s.LandingFrame >= 0, "B22 场景确实发生 Falling->Walking 落地（frame " + s.LandingFrame + "）");
            PMMoverSyncState land = s.PreCorrectionAp[(int)(s.LandingFrame + 1)].Sync;
            Check(land.Mode == PMMoverMode.Walking && land.Grounded, "B23 落地帧模式切回 Walking 且 Grounded");
            Check(land.Position.Y == PMMoverDefaults.CapsuleHalfHeightMeters,
                "B24 落地后中心 Y == halfHeight（" + land.Position.Y.ToString("F6") + "），足底精确贴地");
            Check(s.PreCorrectionAp[(int)s.LandingFrame].Sync.Mode == PMMoverMode.Falling,
                "B25 落地前一帧是 Falling（确实是模式切换帧）");

            // --- 真实模式切换帧（SetMode(Flying)）
            Check(s.ModeSwitchFrame >= 0, "B26 场景确实发生 SetMode(Flying) 模式切换（frame " + s.ModeSwitchFrame + "）");
            Check(s.PreCorrectionAp[(int)(s.ModeSwitchFrame + 1)].Sync.Mode == PMMoverMode.Flying,
                "B27 模式切换帧后 Mode == Flying");
            Check(s.WallContactFrame < s.LandingFrame && s.LandingFrame < s.ModeSwitchFrame,
                "B28 三类校正帧次序合理：墙(" + s.WallContactFrame + ") < 落地(" + s.LandingFrame
                + ") < 模式切换(" + s.ModeSwitchFrame + ")");

            // --- 层真的生效过（ActiveLayers 非空，供后续层差异校正使用）
            bool sawLayers = false;
            for (long b = 0; b <= s.BoundaryCount; b++)
            {
                PMMoverLayer[] layers = s.PreCorrectionAp[(int)b].Sync.ActiveLayers;
                if (layers != null && layers.Length > 0) { sawLayers = true; break; }
            }

            Check(sawLayers, "B29 序列中活跃叠加层真的出现过（ActiveLayers 非空）");

            Console.WriteLine("      [场景事实] 帧数=" + s.BoundaryCount
                + "；首次接触墙 frame=" + s.WallContactFrame
                + "（X=" + s.PreCorrectionAp[(int)(s.WallContactFrame + 1)].Sync.Position.X.ToString("F6") + "）"
                + "；墙前校正 frame=" + s.WallApproachFrame
                + "；落地 frame=" + s.LandingFrame
                + "；模式切换 frame=" + s.ModeSwitchFrame
                + "；dt 取值={" + JoinInts(used) + "}"
                + "；接触平面手算=" + contactPlane.ToString("F4"));

            // --- 碰撞查询真的被调用（不是被桩住）
            Check(s.ApWorld.Sweeps > 0 && s.ApWorld.Grounds > 0,
                "B30 真实碰撞查询被调用：Sweep " + s.ApWorld.Sweeps + " 次 / QueryGround " + s.ApWorld.Grounds + " 次");
        }

        // =================================================================================
        //  C. 校正起点（边界 0）：整段重放，但不得有任何确认事件/外部回调
        // =================================================================================

        private static void TestReplayAtBoundaryZero()
        {
            Scenario s = GetScenario();
            long pendingBefore = s.Timeline.PendingFrame.Value;
            long advBefore = s.ConfirmedAdvances.Count;
            int eventsBefore = s.EmittedKeys.Count;
            long simulatedBefore = s.Timeline.SimulatedSteps;
            long placeholderBefore = s.Timeline.PlaceholderSteps;
            long tickedBefore = s.Timeline.TickedSteps;

            // 强制六类同帧差异都出现在边界 0（位置/速度/mode/参数/ActiveLayers/aux 版本）
            PMMoverSyncState baseState = s.PreCorrectionAp[0].Sync;
            PMMoverSyncState perturbed = Perturb(s.ReferenceModel, baseState, PMMoverMode.Walking);
            PMMoverAuxState pertAux = PerturbAux(s.PreCorrectionAp[0].Aux);
            const double totalShift = 3.0;   // 权威累计时间也必须被采纳（契约：恢复含 TotalSimTimeMs）
            PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> authority =
                MakeAuthority(0L, perturbed, pertAux, s.PreCorrectionAp[0].TotalSimTimeMs + totalShift,
                              new PMFrameId(PMFrameDomain.AuthorityServer, 9000L));
            WithEvidence(s, authority);   // 边界 0 无可确认区间 -> 空证据批

            PMAuthorityApplyResult res = s.Timeline.ApplyAuthority(authority);
            s.BoundaryZeroResult = res;

            Check(res.Reject == PMAuthorityRejectReason.None && res.Applied,
                "C1 边界 0 的权威快照被接纳（校正起点）");
            Check(res.Reconciled, "C2 六类差异 -> 触发完整恢复 + 重放");
            Check(res.ResimulatedFrames == (int)pendingBefore,
                "C3 重放步数 == PendingFrame - 权威边界（" + res.ResimulatedFrames + "/" + pendingBefore + "）");
            Check(!res.AdvancedConfirmed && res.EventsConfirmed == 0,
                "C4 边界 0 是校正起点：不推进确认边界、不发任何确认事件");
            Check(s.ConfirmedAdvances.Count == advBefore && s.EmittedKeys.Count == eventsBefore,
                "C5 连 ConfirmedFrameAdvanced/EventConfirmed 回调都没有触发（重放期间无外部回调）");
            Check(s.Timeline.SimulatedSteps == simulatedBefore && s.Timeline.PlaceholderSteps == placeholderBefore
                  && s.Timeline.TickedSteps == tickedBefore,
                "C6 重放不改动 Ticked/Simulated/Placeholder 计数（重放是独立计数）");
            Check(s.Timeline.ReplayedSteps == res.ResimulatedFrames,
                "C7 ReplayedSteps == 本次重放步数（" + s.Timeline.ReplayedSteps + "）");

            // 累计时间被权威值接管（手算：权威 0+3ms，再逐帧加原 dt）
            double expectedBoundary1 = s.PreCorrectionAp[0].TotalSimTimeMs + totalShift + s.Script[0].Dt;
            Check(Math.Abs(s.Timeline.TryGetBoundarySnapshotTotal(1L) - expectedBoundary1) < 1e-9,
                "C8 重放后的累计时间从权威值起算（边界 1 == " + expectedBoundary1 + " ms）");

            // 全区间重放结果 == 独立 reference（从修正边界逐输入真实模型 Simulate）
            CompareReplayToReference(s, 0L, pendingBefore, authority, "C9");
        }

        // =================================================================================
        //  D. 墙前校正：六类差异逐项 + 组合 + 重放保真 + 原输入原 dt
        // =================================================================================

        private static void TestWallCorrection()
        {
            Scenario s = GetScenario();
            long boundary = s.WallApproachFrame + 1;      // 墙前校正：重放区间覆盖真实撞墙帧

            PMMoverSyncState apAtBoundary = CurrentBoundarySync(s, boundary);
            PMMoverSyncState dsAtBoundary = s.DsSyncAt[(int)boundary];

            // 契约「P4A4：强制位置/速度/Mode/参数/ActiveLayers/aux 版本差异」
            PMMoverSyncState p = s.ReferenceModel.CloneSync(dsAtBoundary);
            p.Position = p.Position + new PMVector3(0.30f, 0f, 0f);
            Check(s.ReferenceModel.ShouldReconcile(apAtBoundary, p, s.PreCorrectionAp[(int)boundary].Aux,
                      s.PreCorrectionAp[(int)boundary].Aux), "D1 位置差异 -> reconcile");

            p = s.ReferenceModel.CloneSync(dsAtBoundary);
            p.Velocity = p.Velocity + new PMVector3(0.75f, 0f, 0f);
            Check(s.ReferenceModel.ShouldReconcile(apAtBoundary, p, s.PreCorrectionAp[(int)boundary].Aux,
                      s.PreCorrectionAp[(int)boundary].Aux), "D2 速度差异 -> reconcile");

            p = s.ReferenceModel.CloneSync(dsAtBoundary);
            p.Mode = dsAtBoundary.Mode == PMMoverMode.Falling ? PMMoverMode.Flying : PMMoverMode.Falling;
            Check(s.ReferenceModel.ShouldReconcile(apAtBoundary, p, s.PreCorrectionAp[(int)boundary].Aux,
                      s.PreCorrectionAp[(int)boundary].Aux), "D3 Mode 差异 -> reconcile");

            p = s.ReferenceModel.CloneSync(dsAtBoundary);
            p.MaxSpeed = dsAtBoundary.MaxSpeed + 0.5f;
            Check(s.ReferenceModel.ShouldReconcile(apAtBoundary, p, s.PreCorrectionAp[(int)boundary].Aux,
                      s.PreCorrectionAp[(int)boundary].Aux), "D4 有效参数差异 -> reconcile");

            p = s.ReferenceModel.CloneSync(dsAtBoundary);
            p.ActiveLayers = new PMMoverLayer[] { MakeLayer(404u, PMMoverLayerKind.AdditiveVelocity, 0,
                new PMVector3(1.25f, 0f, 0f), 0, 0) };
            Check(s.ReferenceModel.ShouldReconcile(apAtBoundary, p, s.PreCorrectionAp[(int)boundary].Aux,
                      s.PreCorrectionAp[(int)boundary].Aux), "D5 ActiveLayers 差异 -> reconcile");

            p = s.ReferenceModel.CloneSync(dsAtBoundary);
            Check(s.ReferenceModel.ShouldReconcile(apAtBoundary, p, s.PreCorrectionAp[(int)boundary].Aux,
                      PerturbAux(s.PreCorrectionAp[(int)boundary].Aux)), "D6 aux 版本/重力差异 -> reconcile");

            // 组合：六类差异同时出现，并把它送进真实 timeline
            PMMoverSyncState combined = Perturb(s.ReferenceModel, dsAtBoundary, PMMoverMode.Falling);
            PMMoverAuxState combinedAux = PerturbAux(s.PreCorrectionAp[(int)boundary].Aux);
            PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> authority =
                MakeAuthority(boundary, combined, combinedAux, s.PreCorrectionAp[(int)boundary].TotalSimTimeMs,
                              new PMFrameId(PMFrameDomain.AuthorityServer, 9100L + boundary));
            WithEvidence(s, authority);

            long pendingBefore = s.Timeline.PendingFrame.Value;
            long confirmedBefore = s.Timeline.ConfirmedFrame.Value;
            s.ReplayCallsBeforeD = CountResimulatingCalls(s.ApModel.Calls);
            s.ApModel.Calls.Clear();

            PMAuthorityApplyResult res = s.Timeline.ApplyAuthority(authority);

            Check(res.Applied && res.Reconciled && res.Reject == PMAuthorityRejectReason.None,
                "D7 墙前组合校正被接纳并触发恢复重放");
            Check(res.ResimulatedFrames == (int)(pendingBefore - boundary),
                "D8 重放步数 == PendingFrame - 权威边界（" + res.ResimulatedFrames + "）");
            Check(res.AdvancedConfirmed && s.Timeline.ConfirmedFrame.Value == boundary
                  && confirmedBefore < boundary,
                "D9 确认边界推进到权威边界（" + confirmedBefore + " -> " + boundary + "）");
            Check(res.EventsConfirmed > 0 && s.EmittedKeys.Count == res.EventsConfirmed,
                "D10 确认推进时事件按帧升序各发一次（本次 " + res.EventsConfirmed + " 条）");

            // 重放保真：与独立 reference 逐边界比对（reference 用第三个独立世界实例）
            CompareReplayToReference(s, boundary, pendingBefore, authority, "D11");

            // 原输入与 dt 一致：重放调用的 (frame, dt, 输入摘要) 必须等于序列
            int replayCalls = 0;
            int badDt = 0;
            int badFrame = 0;
            int badInput = 0;
            int notResim = 0;
            int placeholdersInRange = 0;
            for (int i = 0; i < s.ApModel.Calls.Count; i++)
            {
                CallRecord c = s.ApModel.Calls[i];
                if (!c.Resimulating) { notResim++; continue; }
                replayCalls++;
                int index = (int)c.InputFrame;
                if (index < 0 || index >= s.Script.Count) { badFrame++; continue; }
                if (c.StepMs != s.Script[index].Dt) { badDt++; }
                if (c.InputFrame != index) { badFrame++; }
                if (c.InputDigest != InputDigest(s.Script[index].Input)) { badInput++; }
            }

            for (long f = boundary; f < pendingBefore; f++)
            {
                if (s.Script[(int)f].Dt == 0) { placeholdersInRange++; }
            }

            Check(notResim == 0, "D12 校正期间 CPU 侧只有重放调用（无旁路 Simulate）");
            Check(replayCalls == res.ResimulatedFrames - placeholdersInRange,
                "D13 重放调用数 == 重放步数 - 占位步数（" + replayCalls + " == " + res.ResimulatedFrames
                + " - " + placeholdersInRange + "）：0 占位不调模型");
            Check(badDt == 0 && badFrame == 0 && badInput == 0,
                "D14 重放用的就是**原始输入与原始 dt**（错配 dt=" + badDt + " frame=" + badFrame
                + " input=" + badInput + "）");
            Check(s.ApModel.Calls[0].BaseSimTimeMs == (long)authority.TotalSimTimeMs,
                "D15 重放首步 BaseSimTimeMs == 权威累计时间（恢复边界状态整体取权威值）");
            Check(s.ApModel.Calls[0].InputFrame == boundary,
                "D16 重放从修正边界那一条输入开始（frame " + s.ApModel.Calls[0].InputFrame + "）");

            // 权威快照不得被 timeline 保留别名：事后改权威对象不影响 timeline 状态
            PMMoverSyncState authorityCopy = s.ReferenceModel.CloneSync(combined);
            string before = SyncDifference(CurrentBoundarySync(s, boundary), authorityCopy);
            if (combined.ActiveLayers != null && combined.ActiveLayers.Length > 0)
            {
                combined.ActiveLayers[0].InstanceId = 0xFFFFFFu;
            }

            combined.Position = new PMVector3(-12345f, 0f, 0f);
            Check(before == null && SyncDifference(CurrentBoundarySync(s, boundary), authorityCopy) == null,
                "D17 事后改写权威快照对象/其层数组不会污染 timeline 状态（无别名）");
        }

        // =================================================================================
        //  E. 落地帧收敛 / 模式切换帧零回滚
        // =================================================================================

        private static void TestLandingAndModeSwitch()
        {
            Scenario s = GetScenario();

            // --- 落地帧：送入未扰动的真实 DS 权威 → 必须精确收敛到 DS 边界状态
            long landingBoundary = s.LandingFrame + 1;
            PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> landAuth =
                MakeAuthority(landingBoundary, s.DsSyncAt[(int)landingBoundary], s.PreCorrectionAp[(int)landingBoundary].Aux,
                              s.DsTotalAt[(int)landingBoundary], new PMFrameId(PMFrameDomain.AuthorityServer, 9200L + landingBoundary));
            WithEvidence(s, landAuth);
            PMAuthorityApplyResult landRes = s.Timeline.ApplyAuthority(landAuth);

            Check(landRes.Applied && landRes.Reject == PMAuthorityRejectReason.None,
                "E1 落地帧权威快照被接纳");
            Check(landRes.Reconciled, "E2 落地帧校正触发了恢复重放（此前被起点扰动引入分歧）");
            string diff = SyncDifference(CurrentBoundarySync(s, landingBoundary), s.DsSyncAt[(int)landingBoundary]);
            Check(diff == null, "E3 落地帧校正后 AP 状态 == DS 权威状态（逐位收敛"
                  + (diff == null ? "）" : "；" + diff + "）"));
            CompareReplayToReference(s, landingBoundary, s.Timeline.PendingFrame.Value, landAuth, "E4");

            // --- 模式切换帧：AP 已与 DS 一致 → 必须"零回滚"（ShouldReconcile 为假）
            long switchBoundary = s.ModeSwitchFrame + 1;
            Check(s.ReferenceModel.ShouldReconcile(
                      CurrentBoundarySync(s, switchBoundary), s.DsSyncAt[(int)switchBoundary],
                      s.PreCorrectionAp[(int)switchBoundary].Aux, s.PreCorrectionAp[(int)switchBoundary].Aux) == false,
                "E5 模式切换帧：本地预测与权威一致 -> ShouldReconcile == false");

            PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> switchAuth =
                MakeAuthority(switchBoundary, s.DsSyncAt[(int)switchBoundary], s.PreCorrectionAp[(int)switchBoundary].Aux,
                              s.DsTotalAt[(int)switchBoundary], new PMFrameId(PMFrameDomain.AuthorityServer, 9300L + switchBoundary));
            long replayedBefore = s.Timeline.ReplayedSteps;
            WithEvidence(s, switchAuth);
            PMAuthorityApplyResult switchRes = s.Timeline.ApplyAuthority(switchAuth);

            Check(switchRes.Applied && !switchRes.Reconciled && switchRes.ResimulatedFrames == 0,
                "E6 模式切换帧：接受但不回滚、重放 0 步（不制造无谓 rollback）");
            Check(s.Timeline.ReplayedSteps == replayedBefore, "E7 零回滚：ReplayedSteps 未增长");
            Check(SyncDifference(CurrentBoundarySync(s, switchBoundary), s.DsSyncAt[(int)switchBoundary]) == null,
                "E8 模式切换帧状态仍与权威逐位相同（收敛后不再漂移）");
            Check(switchRes.AdvancedConfirmed && switchRes.EventsConfirmed > 0,
                "E9 确认边界照常推进且事件发一次（不回滚不等于不确认）");
        }

        // =================================================================================
        //  F. 晚帧强制校正 + 事件只发一次 + 同帧事件集合被回滚替换
        // =================================================================================

        private static void TestLateCorrectionAndEvents()
        {
            Scenario s = GetScenario();

            // --- 晚帧（Walking 段）强制差异：Mode/参数/层/aux 同时不同
            long boundary = s.BoundaryCount - 6;
            PMMoverSyncState combined = Perturb(s.ReferenceModel, s.DsSyncAt[(int)boundary], PMMoverMode.Inactive);
            PMMoverAuxState combinedAux = PerturbAux(s.PreCorrectionAp[(int)boundary].Aux);
            PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> authority =
                MakeAuthority(boundary, combined, combinedAux, s.PreCorrectionAp[(int)boundary].TotalSimTimeMs,
                              new PMFrameId(PMFrameDomain.AuthorityServer, 9400L + boundary));
            WithEvidence(s, authority);

            PMAuthorityApplyResult res = s.Timeline.ApplyAuthority(authority);
            Check(res.Applied && res.Reconciled && res.ResimulatedFrames == (int)(s.BoundaryCount - boundary),
                "F1 晚帧强制差异 -> 恢复重放 " + res.ResimulatedFrames + " 步");
            CompareReplayToReference(s, boundary, s.BoundaryCount, authority, "F2");

            long confirmed = s.Timeline.ConfirmedFrame.Value;
            Check(confirmed == boundary, "F3 确认边界追到权威边界 " + boundary);

            // --- 事件只随确认边界发一次（逐 key 唯一）
            List<ulong> expectedKeys = new List<ulong>();
            for (long f = 0; f < confirmed; f++)
            {
                if (s.Script[(int)f].Dt != 0)
                {
                    expectedKeys.Add(KeyOf(f));
                }
            }

            Check(s.EmittedKeys.Count == expectedKeys.Count,
                "F4 事件条数 == 确认区间内非占位帧数（" + s.EmittedKeys.Count + "/" + expectedKeys.Count + "）");

            HashSet<ulong> seen = new HashSet<ulong>();
            int duplicates = 0;
            for (int i = 0; i < s.EmittedKeys.Count; i++)
            {
                if (!seen.Add(s.EmittedKeys[i])) { duplicates++; }
            }

            Check(duplicates == 0, "F5 同一个事件 key 从不重复广播（重复数 " + duplicates + "）：回滚不会重发");

            int missing = 0;
            for (int i = 0; i < expectedKeys.Count; i++)
            {
                if (!seen.Contains(expectedKeys[i])) { missing++; }
            }

            Check(missing == 0, "F6 确认区间内每条事件都已广播（缺失 " + missing + "）");

            // --- 确认广播的 key 区间严格等于 (previousConfirmed, newConfirmed]
            long advanceSum = 0;
            for (int i = 0; i < s.ConfirmedAdvances.Count; i++) { advanceSum += s.ConfirmedAdvances[i]; }
            Check(advanceSum == confirmed, "F7 全部 ConfirmedFrameAdvanced 增量之和 == 最终确认边界（" + advanceSum + "）");

            // --- 同帧事件集合被回滚替换：发出的是**重放后**的值，不是被替换掉的旧值
            long replacedFrame = -1;
            for (long f = 0; f < confirmed; f++)
            {
                if (s.Script[(int)f].Dt == 0) { continue; }
                int pre = s.PreCorrectionAp[(int)(f + 1)].Sync.ValueOfPositionXMilli();
                int now = CurrentBoundarySync(s, f + 1).ValueOfPositionXMilli();
                if (pre != now) { replacedFrame = f; break; }
            }

            Check(replacedFrame >= 0,
                "F8 存在「被重放替换了事件值」的帧（证明同帧事件集合确实被替换）");

            if (replacedFrame >= 0)
            {
                int expected = CurrentBoundarySync(s, replacedFrame + 1).ValueOfPositionXMilli();
                int emitted = EmittedValueForKey(s, KeyOf(replacedFrame));
                int pre = s.PreCorrectionAp[(int)(replacedFrame + 1)].Sync.ValueOfPositionXMilli();
                Check(emitted == expected,
                    "F9 帧 " + replacedFrame + " 广播的事件值 == 重放后最终集合的值（" + emitted + "，旧值 "
                    + pre + " 未被广播）");
            }

            // --- 全部确认帧的事件值都等于各边界最终状态导出的值。
            //     校正边界前一帧（b-1）不在重放区间内、快照也不可能"重放"它，
            //     因此它的确认事件必须来自权威事件证据（ConfirmedEventFrames）；
            //     原先 3/3 帧不一致的"上游载荷缺口"本轮已闭合为 0 不一致。
            int valueMismatch = 0;
            long firstMismatch = -1L;
            for (long f = 0; f < confirmed; f++)
            {
                if (s.Script[(int)f].Dt == 0) { continue; }
                int expectedValue = CurrentBoundarySync(s, f + 1).ValueOfPositionXMilli();
                int emittedValue = EmittedValueForKey(s, KeyOf(f));
                if (emittedValue != expectedValue)
                {
                    valueMismatch++;
                    if (firstMismatch < 0L) { firstMismatch = f; }
                }
            }

            Check(valueMismatch == 0,
                "F10 每条已广播事件值都来自各边界最终状态（不一致 " + valueMismatch
                + (firstMismatch < 0L ? " 条）" : " 条；首个不一致帧 " + firstMismatch + "）"));

            // F11（严格化）：三个原“载荷缺口”帧（墙前校正 / 落地校正 / 晚帧校正 的校正边界前一帧）
            //     不再依赖"校正前的预测事件集合"：其确认事件必须来自该次校正携带的权威事件证据。
            //     · 三个缺口帧的广播事件都必须等于边界最终状态导出的值；
            //     · 三个缺口帧的广播事件都必须逐字段等于该次校正携带的证据（kind + value）；
            //     · 其中两次校正的权威被**故意扰动**（墙前 frame21、晚帧 frame81）：其广播事件必须
            //       与校正前的预测事件不同 —— 证明"被权威纠正掉的事件没有发"；
            //     · 落地校正（frame54）的权威就是真实 DS 真值，因此与预测一致属于正确行为，
            //       单独作为反例断言（防止把"任何情况都必须不同"这种错误口径写死）。
            long[] formerGapFrames = new long[] { s.WallApproachFrame, s.LandingFrame, s.BoundaryCount - 7L };
            int gapClosed = 0;
            int evidenceDerived = 0;
            int perturbedDiffers = 0;
            int truthCoincides = 0;
            for (int i = 0; i < formerGapFrames.Length; i++)
            {
                long f = formerGapFrames[i];
                long gapBoundary = f + 1L;
                ulong key = KeyOf(f);
                int expected = CurrentBoundarySync(s, gapBoundary).ValueOfPositionXMilli();
                int emittedKind = EmittedKindForKey(s, key);
                int emittedValue = EmittedValueForKey(s, key);
                if (emittedValue == expected) { gapClosed++; }

                PMPredictionEvent evidence;
                bool hasEvidence = s.EvidenceEvents.TryGetValue(gapBoundary, out evidence);
                bool fromEvidence = hasEvidence && emittedKind == evidence.Kind && emittedValue == evidence.Value;
                if (fromEvidence) { evidenceDerived++; }

                // 校正前的预测事件：由校正前的边界状态按同一确定性公式导出（InstrumentedModel 的口径）。
                PMMoverSyncState predictedState = s.PreCorrectionAp[(int)gapBoundary].Sync;
                bool sameAsPrediction = emittedKind == (int)predictedState.Mode
                                        && emittedValue == predictedState.ValueOfPositionXMilli();

                if (f == s.LandingFrame)
                {
                    // 落地校正的权威 = 真实 DS 真值，与本地预测一致；此处相等是正确结果。
                    if (sameAsPrediction) { truthCoincides++; }
                }
                else if (!sameAsPrediction)
                {
                    perturbedDiffers++;
                }
            }

            Check(gapClosed == 3,
                "F11a 三个原载荷缺口帧全部闭合：广播值 == 边界最终状态（" + gapClosed + "/3）");
            Check(evidenceDerived == 3,
                "F11b 三个缺口帧的广播事件逐字段等于该次校正携带的权威证据（" + evidenceDerived + "/3）");
            Check(perturbedDiffers == 2,
                "F11c 两次被扰动的权威（墙前/晚帧）其广播事件与校正前预测事件不同（" + perturbedDiffers
                + "/2）：被权威纠正掉的事件没有被冒充广播");
            Check(truthCoincides == 1,
                "F11d 落地校正的权威就是真实真值，广播事件与本地预测一致（" + truthCoincides
                + "/1）：证据机制不是无条件改写，而是逐边界如实采用");

            Check(s.Timeline.ConfirmedEventFramesSkipped == 0L && s.Timeline.ConfirmedEventFramesApplied == 82L,
                "F12 本场景 82 个确认边界全部携带权威证据（applied=" + s.Timeline.ConfirmedEventFramesApplied
                + " skipped=" + s.Timeline.ConfirmedEventFramesSkipped + "）：没有任何边界被伪造或无证据广播");
        }

        // =================================================================================
        //  G. 拒绝矩阵 + 历史满 resync + 断线墙钟
        // =================================================================================

        private static void TestRejectMatrixAndHistoryFull()
        {
            PMMoverModel model = new PMMoverModel(new PMMoverTestWorld());
            PMMoverSyncState start = PMMoverSyncState.CreateDefault();
            PMMoverAuxState aux = PMMoverAuxState.CreateDefault();
            aux.CollisionWorldVersion = (int)PMMoverTestWorld.FrozenCollisionWorldVersion;

            // --- 角色门：SP/Authority 不得走回滚路径
            PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState> sp =
                new PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState>(
                    model, Epoch, Instance, start, aux, PMNetRole.SimulatedProxy, HistoryCapacity, DisconnectTimeoutMs, FutureWindow);
            sp.Tick(16, MoveInput(1f, 0f, 0f), new PMFrameId(PMFrameDomain.AuthorityServer, 0L));
            PMAuthorityApplyResult spRes = sp.ApplyAuthority(MakeAuthority(1L, start, aux, 16.0, new PMFrameId(PMFrameDomain.AuthorityServer, 1L)));
            Check(!spRes.Applied && spRes.Reject == PMAuthorityRejectReason.RoleNotPredictive && sp.RoleRejections == 1,
                "G1 SP 角色调用 ApplyAuthority -> RoleNotPredictive（D-R0-22）");

            PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState> ds =
                new PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState>(
                    model, Epoch, Instance, start, aux, PMNetRole.Authority, HistoryCapacity, DisconnectTimeoutMs, FutureWindow);
            ds.Tick(16, MoveInput(1f, 0f, 0f), new PMFrameId(PMFrameDomain.AuthorityServer, 0L));
            Check(ds.ApplyAuthority(MakeAuthority(1L, start, aux, 16.0, new PMFrameId(PMFrameDomain.AuthorityServer, 1L)))
                      .Reject == PMAuthorityRejectReason.RoleNotPredictive,
                "G2 Authority 角色调用 ApplyAuthority -> RoleNotPredictive");

            // --- 拒绝矩阵
            PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState> tl =
                new PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState>(
                    model, Epoch, Instance, start, aux, PMNetRole.AutonomousProxy, HistoryCapacity, DisconnectTimeoutMs, 2L);
            for (int i = 0; i < 4; i++)
            {
                tl.Tick(16, MoveInput(1f, 0f, 0f), new PMFrameId(PMFrameDomain.AuthorityServer, i));
            }

            Check(tl.ApplyAuthority(MakeAuthorityWithEpoch(0u, 1L, start, aux, 0.0, PMFrameId.None))
                      .RejectedAs(PMAuthorityRejectReason.Malformed),
                "G3 epoch=0 -> Malformed（fail-closed）");
            Check(tl.ApplyAuthority(MakeAuthorityWithFrame(1L, start, aux, 0.0, PMFrameId.None,
                      new PMFrameId(PMFrameDomain.AuthorityServer, 1L))).RejectedAs(PMAuthorityRejectReason.Malformed),
                "G4 OutputFrame 用 AuthorityServer 命名空间 -> Malformed（帧命名空间隔离）");
            Check(tl.ApplyAuthority(MakeAuthorityWithInstance(0u, 1L, start, aux, 0.0, PMFrameId.None))
                      .RejectedAs(PMAuthorityRejectReason.Malformed),
                "G5 instance=0 -> Malformed");
            Check(tl.ApplyAuthority(MakeAuthorityWithEpoch(Epoch + 1u, 1L, start, aux, 0.0, PMFrameId.None))
                      .RejectedAs(PMAuthorityRejectReason.EpochMismatch),
                "G6 错 epoch -> EpochMismatch（不改状态）");
            Check(tl.ApplyAuthority(MakeAuthorityWithInstance(Instance + 1u, 1L, start, aux, 0.0, PMFrameId.None))
                      .RejectedAs(PMAuthorityRejectReason.InstanceMismatch),
                "G7 错 instance -> InstanceMismatch（不改状态）");
            Check(tl.ApplyAuthority(MakeAuthority(5L, start, aux, 0.0, PMFrameId.None))
                      .RejectedAs(PMAuthorityRejectReason.Future),
                "G8 未来边界（> PendingFrame）-> Future");
            Check(tl.ApplyAuthority(MakeAuthority(3L, start, aux, 0.0, PMFrameId.None))
                      .RejectedAs(PMAuthorityRejectReason.Future),
                "G9 超出未来窗口 2 的边界 3 -> Future");

            PMAuthorityApplyResult first = tl.ApplyAuthority(MakeAuthority(0L, start, aux, 0.0, PMFrameId.None));
            Check(first.Applied && first.Reject == PMAuthorityRejectReason.None,
                "G10 边界 0 的首份权威被接纳（校正起点，初值边界视为未确认）");
            Check(tl.ApplyAuthority(MakeAuthority(0L, start, aux, 0.0, PMFrameId.None))
                      .RejectedAs(PMAuthorityRejectReason.StaleOrDuplicate),
                "G11 同一边界的第二份权威 -> StaleOrDuplicate（确认边界不回退）");
            Check(tl.ConfirmedFrame.Value == 0L, "G12 迟到/重复权威不改动确认边界");
            Check(tl.RejectedAuthorities == 8 && tl.DuplicateAuthorities == 1 && tl.FutureAuthorities == 2
                  && tl.EpochRebinds == 0,
                "G13 拒绝计数可观测（rejected=8 dup=1 future=2 epochRebinds=0）");

            // --- 历史满：不静默覆盖，冻结进 NeedsResync 并要求宿主重同步
            PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState> small =
                new PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState>(
                    model, Epoch, Instance, start, aux, PMNetRole.AutonomousProxy, 4, DisconnectTimeoutMs, FutureWindow);
            int resyncCallbacks = 0;
            int resetNotices = 0;
            int lastResetCount = -1;
            small.ResyncRequested += delegate { resyncCallbacks++; };
            small.UnconfirmedInputsResetNotified += delegate(int n) { resetNotices++; lastResetCount = n; };

            for (int i = 0; i < 4; i++)
            {
                Check(small.Tick(16, MoveInput(1f, 0f, 0f), new PMFrameId(PMFrameDomain.AuthorityServer, i)).Accepted,
                    "G14 容量 4 的前四帧被接纳 @" + i);
            }

            PMTickResult full = small.Tick(16, MoveInput(1f, 0f, 0f), new PMFrameId(PMFrameDomain.AuthorityServer, 4L));
            Check(full.Reject == PMTickRejectReason.HistoryExhausted && full.NeedsResync,
                "G15 历史满且未确认 -> HistoryExhausted + NeedsResync（不静默覆盖）");
            Check(small.TickedSteps == 4 && small.RetainedInputCount == 4 && small.HistoryExhaustions == 1,
                "G16 冻结时历史与计数零改动（retained=4, exhaustions=1）");
            Check(resyncCallbacks == 1, "G17 历史耗尽只请求一次 Resync（回调 " + resyncCallbacks + " 次）");
            Check(small.Tick(16, MoveInput(1f, 0f, 0f), new PMFrameId(PMFrameDomain.AuthorityServer, 5L))
                      .Reject == PMTickRejectReason.NeedsResync,
                "G18 NeedsResync 期间 Tick 一律被拒（必须先显式 Resync）");

            PMResyncResult rr = small.Resync(MakeAuthority(2L, small.TryGetBoundarySnapshotTyped(2L).Sync, aux,
                                              2.0 * 16.0, PMFrameId.None));
            Check(rr.Applied && !rr.EpochRebound && rr.ResetUnconfirmedInputs == 2,
                "G19 同绑定 Resync：丢弃 2 条未确认输入（" + rr.ResetUnconfirmedInputs + "）");
            Check(resetNotices == 1 && lastResetCount == 2, "G20 未确认输入被重置时通知宿主一次");
            Check(!small.NeedsResync && small.ConfirmedFrame.Value == 2L && small.PendingFrame.Value == 2L,
                "G21 Resync 后解冻，边界落到可信快照");
            Check(small.Tick(16, MoveInput(0f, 0f, 0f), new PMFrameId(PMFrameDomain.AuthorityServer, 6L)).Accepted,
                "G22 Resync 后可继续推进");

            Check(small.Resync(MakeAuthority(1L, start, aux, 0.0, PMFrameId.None))
                      .Reject == PMAuthorityRejectReason.StaleOrDuplicate,
                "G23 Resync 不得回退确认边界 -> StaleOrDuplicate");
            Check(small.Resync(MakeAuthority(99L, start, aux, 0.0, PMFrameId.None))
                      .Reject == PMAuthorityRejectReason.Future, "G24 Resync 未来边界 -> Future");

            PMResyncResult rebind = small.Resync(MakeAuthorityWithEpoch(Epoch + 7u, 3L, start, aux, 0.0, PMFrameId.None));
            Check(rebind.Applied && rebind.EpochRebound && small.EpochRebinds == 1,
                "G25 换 epoch -> 整体重绑（EpochRebound）");

            // --- 断线墙钟：只在冻结期累计，跨 2000ms 只通知一次
            PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState> frozen =
                new PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState>(
                    model, Epoch, Instance, start, aux, PMNetRole.AutonomousProxy, HistoryCapacity, DisconnectTimeoutMs, FutureWindow);
            int frozenResync = 0;
            int frozenInputsReset = 0;
            frozen.ResyncRequested += delegate { frozenResync++; };
            frozen.UnconfirmedInputsResetNotified += delegate(int n) { frozenInputsReset += n; };
            frozen.Tick(16, MoveInput(1f, 0f, 0f), new PMFrameId(PMFrameDomain.AuthorityServer, 0L));
            frozen.Freeze();
            Check(frozen.Tick(16, MoveInput(1f, 0f, 0f), new PMFrameId(PMFrameDomain.AuthorityServer, 1L))
                      .Reject == PMTickRejectReason.Frozen,
                "G26 冻结后 Tick 被拒（帧号停止推进）");
            Check(!frozen.NotifyWallClock(1900.0) && frozenResync == 0, "G27 冻结 1900ms 不触发 Resync 请求");
            Check(frozen.NotifyWallClock(-5.0) == false && frozen.WallClockRewindsIgnored == 1,
                "G28 墙钟倒退：不累计、不提前触发");
            Check(frozen.NotifyWallClock(100.0) && frozenResync == 1,
                "G29 跨过 2000ms 阈值：只通知一次（累计 2000ms）");
            Check(!frozen.NotifyWallClock(1000.0) && frozenResync == 1, "G30 之后每帧不再重复通知");
            PMResyncResult unfreeze = frozen.Resync(MakeAuthority(0L, start, aux, 0.0, PMFrameId.None));
            Check(unfreeze.Applied && !frozen.IsFrozen && frozen.Tick(16, MoveInput(1f, 0f, 0f),
                      new PMFrameId(PMFrameDomain.AuthorityServer, 2L)).Accepted,
                "G31 显式 Resync 后解冻并可继续推进（恢复只走 Resync）");
            Check(frozenInputsReset == 1, "G32 Resync 丢弃 1 条未确认输入并通知宿主");
        }

        // =================================================================================
        //  H. DS 输入预算
        // =================================================================================

        private static void TestAuthorityInputBudget()
        {
            PMMoverModel model = new PMMoverModel(new PMMoverTestWorld());
            List<PMAuthorityInput<PMMoverInput>> outSteps = new List<PMAuthorityInput<PMMoverInput>>();

            // --- 重复/迟到
            PMAuthorityInputBuffer<PMMoverInput> dup = NewBuffer(model);
            Check(dup.Admit(BufInput(0L, 16, Epoch, Instance)).Accepted, "H1 首帧被接纳");
            Check(dup.Admit(BufInput(0L, 16, Epoch, Instance)).Reject == PMInputRejectReason.DuplicateOrStale,
                "H2 重复帧 -> DuplicateOrStale");
            Check(dup.QueuedCount == 1 && dup.AdmittedCount == 1 && dup.DuplicateRejections == 1,
                "H3 重复帧零副作用（队列仍 1 条）");
            dup.Pump(0.0, outSteps);
            Check(dup.NextInputFrame == 1L, "H4 消费后水位推进到 1");
            Check(dup.Admit(BufInput(0L, 16, Epoch, Instance)).Reject == PMInputRejectReason.DuplicateOrStale,
                "H5 迟到（早于水位）-> DuplicateOrStale，水位不倒退");
            Check(dup.NextInputFrame == 1L, "H6 迟到帧不回退水位");

            // --- epoch / instance / 畸形 dt
            Check(dup.Admit(BufInput(1L, 16, Epoch + 1u, Instance)).Reject == PMInputRejectReason.EpochMismatch,
                "H7 错 epoch -> EpochMismatch");
            Check(dup.Admit(BufInput(1L, 16, Epoch, Instance + 1u)).Reject == PMInputRejectReason.InstanceMismatch,
                "H8 错 instance -> InstanceMismatch");
            Check(dup.Admit(BufInput(1L, 16, 0u, Instance)).Reject == PMInputRejectReason.MalformedEpochOrInstance,
                "H9 epoch=0 -> MalformedEpochOrInstance");
            Check(dup.Admit(BufInput(1L, 51, Epoch, Instance)).Reject == PMInputRejectReason.MalformedStepMs,
                "H10 dt=51 -> MalformedStepMs（超上限）");
            Check(dup.Admit(BufInput(1L, -1, Epoch, Instance)).Reject == PMInputRejectReason.MalformedStepMs,
                "H11 dt=-1 -> MalformedStepMs（负 dt 显式拒绝）");
            Check(dup.Admit(BufInput(1L + 129L, 16, Epoch, Instance)).Reject == PMInputRejectReason.BeyondWindow,
                "H12 超出未来窗口 128 -> BeyondWindow");
            Check(dup.MalformedRejections == 3 && dup.EpochRejections == 1 && dup.InstanceRejections == 1
                  && dup.BeyondWindowRejections == 1,
                "H13 拒绝计数可观测（malformed=3 epoch=1 instance=1 beyond=1）");

            // --- 队列容量失败且无副作用
            PMAuthorityInputBuffer<PMMoverInput> cap = new PMAuthorityInputBuffer<PMMoverInput>(
                model.CloneInput, Epoch, Instance, queueCapacity: 2);
            Check(cap.Admit(BufInput(0L, 16, Epoch, Instance)).Accepted
                  && cap.Admit(BufInput(1L, 16, Epoch, Instance)).Accepted, "H14 容量 2 前两条被接纳");
            Check(cap.Admit(BufInput(2L, 16, Epoch, Instance)).Reject == PMInputRejectReason.QueueFull,
                "H15 队列满 -> QueueFull");
            Check(cap.QueuedCount == 2 && cap.AdmittedCount == 2 && cap.NextInputFrame == 0L
                  && cap.QueueFullRejections == 1,
                "H16 队列满时零副作用（水位不动、队列不变）");

            // --- 缺帧：先等待，不跨越；超时才请求 Resync
            PMAuthorityInputBuffer<PMMoverInput> gap = NewBuffer(model);
            int gapResync = 0;
            gap.ResyncRequested += delegate { gapResync++; };
            gap.Admit(BufInput(0L, 16, Epoch, Instance));
            gap.Admit(BufInput(2L, 16, Epoch, Instance));
            outSteps.Clear();
            PMPumpResult prefixPump = gap.Pump(16.0, outSteps);
            Check(prefixPump.Steps == 1 && prefixPump.ConsumedMs == 16 && gap.NextInputFrame == 1L && gap.HasGap
                  && prefixPump.Defer == PMPumpDeferReason.Idle,
                "H17 连续前缀先被消费（帧 0 出步、水位推进到 1，随后暴露缺口）");
            outSteps.Clear();
            PMPumpResult gapPump = gap.Pump(16.0, outSteps);
            Check(gapPump.Defer == PMPumpDeferReason.MissingFrameGap && gapPump.Steps == 0 && outSteps.Count == 0,
                "H18 缺帧（有 0/2、缺 1）-> 先等待：不出步");
            Check(gap.HasGap && gap.NextInputFrame == 1L && gap.CreditMs == 66.0 && gap.MissingFrameElapsedMs == 16.0,
                "H19 缺帧等待不推水位（next=1）、不消耗信用（50+16=66）");
            Check(gap.Pump(24.0, outSteps).Defer == PMPumpDeferReason.MissingFrameGap && gapResync == 0,
                "H20 累计 40ms 仍未超时（阈值 500ms）");
            outSteps.Clear();
            PMPumpResult gapTimeout = gap.Pump(500.0, outSteps);
            Check(gapTimeout.Defer == PMPumpDeferReason.NeedsResync && gapTimeout.ResyncRequested && gapResync == 1,
                "H21 缺帧超时 -> 请求 Resync（一次）");
            Check(gap.NeedsResync && gap.QueuedCount == 0 && gap.NextInputFrame == 1L && gap.MissingFrameTimeouts == 1,
                "H22 超时清队列但**从不跨越**水位（next 仍为 1，绝不跳到 2）");
            Check(gap.Admit(BufInput(1L, 16, Epoch, Instance)).Reject == PMInputRejectReason.NeedsResync,
                "H23 NeedsResync 期间拒绝新输入");
            int dropped = gap.Resync(Epoch, Instance, 1L);
            Check(dropped == 0 && !gap.NeedsResync && gap.NextInputFrame == 1L, "H24 显式 Resync 复位队列与水位");

            // --- 信用：初始 50ms，严格按墙钟 1:1 补充、上限 200
            PMAuthorityInputBuffer<PMMoverInput> credit = NewBuffer(model);
            for (int i = 0; i < 6; i++) { credit.Admit(BufInput(i, 16, Epoch, Instance)); }
            outSteps.Clear();
            PMPumpResult creditPump = credit.Pump(0.0, outSteps);
            Check(creditPump.Steps == 3 && creditPump.ConsumedMs == 48 && creditPump.Defer == PMPumpDeferReason.InsufficientCredit,
                "H25 信用 50ms 只够 3 步（48ms），第 4 步延后：Defer=InsufficientCredit（实测 "
                + creditPump.Steps + " 步/" + creditPump.ConsumedMs + "ms）");
            Check(credit.CreditMs == 2.0 && credit.DeferredByCredit == 1, "H26 信用精确扣减为 2ms，延后只记一次");
            outSteps.Clear();
            Check(credit.Pump(14.0, outSteps).Steps == 1 && credit.CreditMs == 0.0,
                "H27 墙钟补 14ms 后恰好再走 1 步（14+2-16=0）");
            PMAuthorityInputBuffer<PMMoverInput> capOnly = NewBuffer(model);
            outSteps.Clear();
            PMPumpResult capPump = capOnly.Pump(1000.0, outSteps);
            Check(capPump.CreditMsAfter == 200.0 && capOnly.CreditMs == 200.0 && capPump.Defer == PMPumpDeferReason.Idle,
                "H28 信用上限 200ms（一次补 1000ms 也只到 200）");
            Check(credit.NextInputFrame == 4L, "H29 水位 == 已消费帧数（4）");

            // --- 每 Pump 步数上限 8
            PMAuthorityInputBuffer<PMMoverInput> stepCap = new PMAuthorityInputBuffer<PMMoverInput>(
                model.CloneInput, Epoch, Instance, initialCreditMs: 200.0);
            for (int i = 0; i < 10; i++) { stepCap.Admit(BufInput(i, 12, Epoch, Instance)); }
            outSteps.Clear();
            PMPumpResult stepPump = stepCap.Pump(0.0, outSteps);
            Check(stepPump.Steps == 8 && stepPump.ConsumedMs == 96 && stepPump.Defer == PMPumpDeferReason.StepLimit,
                "H30 每 Pump 最多 8 步（实测 " + stepPump.Steps + " 步 / " + stepPump.ConsumedMs
                + "ms，Defer=" + stepPump.Defer + "）");
            Check(stepCap.DeferredByStepLimit == 1, "H31 步数上限延后可观测");

            // --- 每 Pump 输入时间上限 100ms
            PMAuthorityInputBuffer<PMMoverInput> msCap = new PMAuthorityInputBuffer<PMMoverInput>(
                model.CloneInput, Epoch, Instance, initialCreditMs: 200.0);
            for (int i = 0; i < 10; i++) { msCap.Admit(BufInput(i, 17, Epoch, Instance)); }
            outSteps.Clear();
            PMPumpResult msPump = msCap.Pump(0.0, outSteps);
            Check(msPump.Steps == 5 && msPump.ConsumedMs == 85 && msPump.Defer == PMPumpDeferReason.InputTimeLimit,
                "H32 每 Pump 最多 100ms 输入时间（实测 " + msPump.Steps + " 步 / " + msPump.ConsumedMs
                + "ms，Defer=" + msPump.Defer + "）：不拆分不放大");
            Check(msCap.DeferredByTimeLimit == 1, "H33 输入时间上限延后可观测");

            // --- 0 占位：消耗步数与输入槽，但不花信用、不调模型
            PMAuthorityInputBuffer<PMMoverInput> ph = new PMAuthorityInputBuffer<PMMoverInput>(
                model.CloneInput, Epoch, Instance, initialCreditMs: 0.0, maxStepsPerPump: 2);
            ph.Admit(BufInput(0L, 0, Epoch, Instance));
            ph.Admit(BufInput(1L, 16, Epoch, Instance));
            outSteps.Clear();
            PMPumpResult phPump = ph.Pump(0.0, outSteps);
            Check(phPump.Steps == 1 && phPump.Placeholders == 1 && phPump.ConsumedMs == 0
                  && phPump.Defer == PMPumpDeferReason.InsufficientCredit,
                "H34 0 占位走一步、不花信用（信用 0 时占位仍能通过，下一步 dt=16 被信用拦下）");
            Check(ph.CreditMs == 0.0 && ph.PlaceholdersPumped == 1 && ph.SimulatedStepsPumped == 0,
                "H35 占位不消耗信用、计入占位计数");

            PMAuthorityInputBuffer<PMMoverInput> ph2 = new PMAuthorityInputBuffer<PMMoverInput>(
                model.CloneInput, Epoch, Instance, maxStepsPerPump: 1);
            ph2.Admit(BufInput(0L, 16, Epoch, Instance));
            ph2.Admit(BufInput(1L, 16, Epoch, Instance));
            outSteps.Clear();
            PMPumpResult ph2Pump = ph2.Pump(0.0, outSteps);
            Check(ph2Pump.Steps == 1 && ph2Pump.Defer == PMPumpDeferReason.StepLimit
                  && ph2.CreditMs == 34.0,
                "H36 占位与正常帧共同占用每 Pump 步数预算（信用 50-16=34）");

            // --- 墙钟倒退不产生信用
            PMAuthorityInputBuffer<PMMoverInput> rewind = NewBuffer(model);
            rewind.Pump(-100.0, outSteps);
            Check(rewind.CreditMs == 50.0 && rewind.WallClockRewindsIgnored == 1,
                "H37 墙钟倒退既不补信用也不扣信用");

            // --- 显式 Resync 返回值与语义
            PMAuthorityInputBuffer<PMMoverInput> rs = NewBuffer(model);
            rs.Admit(BufInput(0L, 16, Epoch, Instance));
            rs.Admit(BufInput(1L, 16, Epoch, Instance));
            int rsDropped = rs.Resync(Epoch + 3u, Instance, 42L);
            Check(rsDropped == 2 && rs.NextInputFrame == 42L && rs.Epoch == Epoch + 3u,
                "H38 Resync 返回被丢弃条数、换绑定并把水位落到给定帧（" + rsDropped + "）");

            // --- 无副作用：被拒绝的输入绝不会进入队列（用真实输入克隆器验证）
            PMAuthorityInputBuffer<PMMoverInput> pure = NewBuffer(model);
            PMMoverInput sneaky = MoveInput(1f, 0f, 0f);
            sneaky.Layers = new PMMoverLayerRequest[] { LayerRequest(9u, PMMoverLayerKind.AdditiveVelocity, 0,
                new PMVector3(1f, 0f, 0f), 0) };
            PMAuthorityInput<PMMoverInput> bad = BufInput(0L, 51, Epoch, Instance);
            bad.Input = sneaky;
            Check(pure.Admit(bad).Reject == PMInputRejectReason.MalformedStepMs && pure.QueuedCount == 0,
                "H39 畸形 dt 被拒时连输入克隆都不入队（零副作用）");
        }

        // =================================================================================
        //  I. SP 时间插值
        // =================================================================================

        private static void TestSpInterpolation()
        {
            PMMoverModel model = new PMMoverModel(new PMMoverTestWorld());
            PMInterpolationBuffer<PMMoverSyncState, PMMoverAuxState> sp =
                PMInterpolationBuffer<PMMoverSyncState, PMMoverAuxState>.For<PMMoverInput>(model);

            PMMoverSyncState a = PMMoverSyncState.CreateDefault();
            a.Position = new PMVector3(0f, 1f, 0f);
            a.Velocity = new PMVector3(2f, 0f, 0f);
            a.PreAdditiveVelocity = new PMVector3(2f, 0f, 0f);
            a.YawDegrees = 350f;
            a.Mode = PMMoverMode.Walking;
            a.Grounded = true;
            a.ActiveLayers = new PMMoverLayer[] { MakeLayer(71u, PMMoverLayerKind.AdditiveVelocity, 0,
                new PMVector3(0.5f, 0f, 0f), 0, 0) };

            PMMoverSyncState b = model.CloneSync(a);
            b.Position = new PMVector3(4f, 1.5f, -2f);
            b.Velocity = new PMVector3(6f, 1f, 2f);
            b.PreAdditiveVelocity = new PMVector3(6f, 1f, 2f);
            b.YawDegrees = 10f;
            b.Mode = PMMoverMode.Falling;
            b.Grounded = false;
            b.MaxSpeed = 7.5f;
            b.ActiveLayers = new PMMoverLayer[] { MakeLayer(72u, PMMoverLayerKind.OverrideVelocity, 3,
                new PMVector3(0f, 0f, 1f), 32, 0) };

            PMMoverAuxState aux = PMMoverAuxState.CreateDefault();
            aux.CollisionWorldVersion = 1234;
            aux.ConfigVersion = 5;

            Check(sp.OnAuthority(MakeAuthority(0L, a, aux, 100.0, new PMFrameId(PMFrameDomain.AuthorityServer, 1L)))
                  == PMInterpolationRejectReason.None, "I1 首包被接纳");
            Check(sp.HasAuthority && sp.LastTotalSimTimeMs == 100.0, "I2 首包对齐到最新权威时间（外推归零）");

            Check(sp.OnAuthority(MakeAuthority(1L, b, aux, 116.0, new PMFrameId(PMFrameDomain.AuthorityServer, 2L)))
                  == PMInterpolationRejectReason.None, "I3 第二包被接纳（窗口 [100,116]）");
            sp.Advance(8.0);
            PMInterpolatedState<PMMoverSyncState, PMMoverAuxState> mid = sp.Sample();

            Check(mid.HasValue && mid.Alpha == 0.5f, "I4 alpha == 0.5（8/(116-100)）");
            Check(mid.Sync.Position.X == 2f && mid.Sync.Position.Y == 1.25f && mid.Sync.Position.Z == -1f,
                "I5 位置线性插值手算 == (2, 1.25, -1)，实测 " + mid.Sync.Position);
            Check(mid.Sync.YawDegrees == 0f,
                "I6 yaw 350°->10° 走最短弧（20°），alpha 0.5 -> 0°，实测 " + mid.Sync.YawDegrees);
            Check(mid.Sync.Mode == PMMoverMode.Falling && !mid.Sync.Grounded,
                "I7 离散量（Mode/Grounded）取 To");
            Check(mid.Sync.MaxSpeed == 7.5f, "I8 有效参数取 To（7.5）");
            Check(mid.Sync.ActiveLayers != null && mid.Sync.ActiveLayers.Length == 1
                  && mid.Sync.ActiveLayers[0].InstanceId == 72u,
                "I9 活跃层集合取 To（#72）");
            Check(mid.Aux.CollisionWorldVersion == 1234 && mid.Aux.ConfigVersion == 5,
                "I10 Aux 一律取 To（离散环境量）");
            Check(!sp.HasRollbackApi(), "I11 SP 没有任何回滚/恢复/应用权威 API（反射钉死）");

            // --- 外推上限 0：超出最新快照就停在最新权威值
            sp.Advance(1000.0);
            PMInterpolatedState<PMMoverSyncState, PMMoverAuxState> clamped = sp.Sample();
            Check(clamped.Alpha == 1f && clamped.ClampedToLatest, "I12 超出 To -> alpha 1 且 ClampedToLatest");
            Check(SyncDifference(clamped.Sync, b) == null, "I13 外推上限 0：停在最新权威值（不做表现外推）");
            Check(sp.ExtrapolationMs > 0.0 && sp.ClampedSamples > 0, "I14 超前量可观测但不参与采样");

            // --- 墙钟倒退忽略
            double sampleBefore = sp.SampleTimeMs;
            sp.Advance(-50.0);
            Check(sp.SampleTimeMs == sampleBefore && sp.RewindsIgnored == 1, "I15 表现时钟倒退被忽略");

            // --- 迟到 / 错绑定 fail-closed
            Check(sp.OnAuthority(MakeAuthority(2L, b, aux, 90.0, PMFrameId.None))
                  == PMInterpolationRejectReason.StaleOrDuplicate, "I16 仿真时间倒退 -> StaleOrDuplicate（不刷新基准）");
            Check(sp.LastTotalSimTimeMs == 116.0, "I17 迟到包未改动基准");
            Check(sp.OnAuthority(MakeAuthorityWithEpoch(Epoch + 1u, 2L, b, aux, 130.0, PMFrameId.None))
                  == PMInterpolationRejectReason.EpochMismatch, "I18 错 epoch -> EpochMismatch");
            Check(sp.OnAuthority(MakeAuthorityWithInstance(Instance + 1u, 2L, b, aux, 130.0, PMFrameId.None))
                  == PMInterpolationRejectReason.InstanceMismatch, "I19 错 instance -> InstanceMismatch");
            Check(sp.OnAuthority(MakeAuthorityWithEpoch(0u, 2L, b, aux, 130.0, PMFrameId.None))
                  == PMInterpolationRejectReason.Malformed, "I20 epoch=0 -> Malformed");
            Check(sp.OnAuthority(MakeAuthority(2L, b, aux, 130.0, new PMFrameId(PMFrameDomain.Input, 3L)))
                  == PMInterpolationRejectReason.Malformed, "I21 ServerFrame 用 Input 命名空间 -> Malformed");

            // --- 别名隔离：采样结果可被宿主自由改写
            sp.AlignToLatest();
            sp.Advance(8.0);
            PMInterpolatedState<PMMoverSyncState, PMMoverAuxState> s1 = sp.Sample();
            PMInterpolatedState<PMMoverSyncState, PMMoverAuxState> s2 = sp.Sample();
            if (s1.Sync.ActiveLayers != null && s2.Sync.ActiveLayers != null)
            {
                Check(!object.ReferenceEquals(s1.Sync.ActiveLayers, s2.Sync.ActiveLayers),
                    "I22 两次采样不共享层数组（无别名）");
                s1.Sync.ActiveLayers[0].InstanceId = 0xDEADu;
                Check(sp.Sample().Sync.ActiveLayers[0].InstanceId == 72u,
                    "I23 改写采样结果不影响 SP 内部基准");
            }
            else
            {
                Fail("I22/I23 采样结果缺少层数组，无法验证别名隔离");
            }

            // --- SP 不调用 AP：喂包+采样期间 timeline 计数零变化
            Scenario scenario = GetScenario();
            long ticked = scenario.Timeline.TickedSteps;
            long replayed = scenario.Timeline.ReplayedSteps;
            long confirmed = scenario.Timeline.ConfirmedFrame.Value;
            PMInterpolationBuffer<PMMoverSyncState, PMMoverAuxState> sp2 =
                PMInterpolationBuffer<PMMoverSyncState, PMMoverAuxState>.For<PMMoverInput>(scenario.ReferenceModel);
            for (long f = 0; f <= 4; f++)
            {
                sp2.OnAuthority(MakeAuthority(f, scenario.DsSyncAt[(int)f], scenario.StartAux,
                    scenario.DsTotalAt[(int)f], new PMFrameId(PMFrameDomain.AuthorityServer, f)));
                sp2.Advance(8.0);
                sp2.Sample();
            }

            Check(scenario.Timeline.TickedSteps == ticked && scenario.Timeline.ReplayedSteps == replayed
                  && scenario.Timeline.ConfirmedFrame.Value == confirmed,
                "I24 真实 AP timeline 在 SP 喂包/采样期间零变化（SP 不调用 AP 回滚）");
        }

        // =================================================================================
        //  J. clone 别名隔离 / 输入与 dt 保真 / 计数不变式
        // =================================================================================

        private static void TestCloneAndFidelityInvariants()
        {
            Scenario s = GetScenario();
            PMMoverModel model = s.ReferenceModel;

            // --- 深 clone：三类都不产生数组别名
            PMMoverInput input = MoveInput(1f, -0.5f, 0.25f);
            input.Effects = new PMMoverEffectRequest[] { PMMoverEffectRequest.SetVelocity(new PMVector3(1f, 2f, 3f)) };
            input.Layers = new PMMoverLayerRequest[] { LayerRequest(5u, PMMoverLayerKind.AdditiveVelocity, 1,
                new PMVector3(0.5f, 0f, 0f), 0) };
            input.RemovedLayerIds = new uint[] { 8u };
            PMMoverInput inputCopy = model.CloneInput(input);
            Check(!object.ReferenceEquals(inputCopy.Effects, input.Effects)
                  && !object.ReferenceEquals(inputCopy.Layers, input.Layers)
                  && !object.ReferenceEquals(inputCopy.RemovedLayerIds, input.RemovedLayerIds),
                "J1 CloneInput 三个列表都是新数组（无别名）");
            inputCopy.Layers[0].InstanceId = 999u;
            Check(input.Layers[0].InstanceId == 5u, "J2 改写克隆不污染原输入");

            PMMoverSyncState withLayers = PMMoverSyncState.CreateDefault();
            withLayers.ActiveLayers = new PMMoverLayer[] { MakeLayer(61u, PMMoverLayerKind.AdditiveVelocity, 0,
                new PMVector3(1f, 0f, 0f), 0, 0) };
            PMMoverSyncState syncCopy = model.CloneSync(withLayers);
            Check(!object.ReferenceEquals(syncCopy.ActiveLayers, withLayers.ActiveLayers),
                "J3 CloneSync 的 ActiveLayers 是新数组");
            syncCopy.ActiveLayers[0].InstanceId = 777u;
            Check(withLayers.ActiveLayers[0].InstanceId == 61u, "J4 改写克隆不污染原状态");

            // --- Simulate 不回写入参
            PMMoverInput simInput = MoveInput(1f, 0f, 0f);
            simInput.Layers = new PMMoverLayerRequest[] { LayerRequest(5u, PMMoverLayerKind.AdditiveVelocity, 0,
                new PMVector3(0.5f, 0f, 0f), 0) };
            string inputDigest = InputDigest(simInput);
            PMMoverSyncState simStart = model.CloneSync(withLayers);
            string startDigest = SyncDigest(simStart);
            PMTimeStep step = new PMTimeStep();
            step.BaseSimTimeMs = 0L;
            step.StepMs = 16f;
            step.ServerFrame = new PMFrameId(PMFrameDomain.AuthorityServer, 1L);
            step.ClientInputFrame = new PMFrameId(PMFrameDomain.Input, 0L);
            step.IsResimulating = false;
            PMSimulationResult<PMMoverSyncState, PMMoverAuxState> sim =
                model.Simulate(step, model.CloneInput(simInput), model.CloneSync(simStart), model.CloneAux(s.PreCorrectionAp[0].Aux));
            Check(InputDigest(simInput) == inputDigest, "J5 Simulate 不改写入参 Input");
            Check(SyncDigest(simStart) == startDigest, "J6 Simulate 不改写入参 start");
            Check(sim.Events != null && sim.Events.Length == 0, "J7 真实 Mover 模型本批不产生任何预测事件（空事件集）");
            Check(sim.Sync.ActiveLayers == null || !object.ReferenceEquals(sim.Sync.ActiveLayers, simStart.ActiveLayers),
                "J8 Simulate 输出的层数组不别名入参");

            // --- timeline 快照别名隔离
            long probeBoundary = s.WallApproachFrame + 1;
            PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> snap1 = s.Timeline.TryGetBoundarySnapshotTyped(probeBoundary);
            string digest1 = SyncDigest(snap1.Sync);
            if (snap1.Sync.ActiveLayers != null && snap1.Sync.ActiveLayers.Length > 0)
            {
                snap1.Sync.ActiveLayers[0].InstanceId = 0xBADu;
            }

            snap1.Sync.Position = new PMVector3(-999f, -999f, -999f);
            PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> snap2 = s.Timeline.TryGetBoundarySnapshotTyped(probeBoundary);
            Check(SyncDigest(snap2.Sync) == digest1, "J9 改写历史快照不污染 timeline 内部状态（深克隆）");

            PMMoverSyncState g1 = s.Timeline.GetSyncSnapshot();
            PMMoverSyncState g2 = s.Timeline.GetSyncSnapshot();
            Check(SyncDigest(g1) == SyncDigest(g2), "J10 GetSyncSnapshot 两次内容一致");

            // --- 输入与 dt 保真：整条序列在全部校正之后仍与原始脚本逐位相同
            int scriptInputDrift = 0;
            for (int i = 0; i < s.Script.Count; i++)
            {
                if (InputDigest(s.Script[i].Input) != s.ScriptDigests[i]) { scriptInputDrift++; }
            }

            Check(scriptInputDrift == 0,
                "J11 全部校正完成后调用方输入序列逐位未变（" + scriptInputDrift + " 处漂移）");
            Check(SyncDigest(s.StartSync) == s.StartSyncDigest, "J12 初始 Sync 未被 Simulate 改写");
            Check(SyncDigest(s.StartAux) == s.StartAuxDigest, "J13 初始 Aux 未被改写");

            // --- 计数不变式
            Check(s.Timeline.TickedSteps == s.BoundaryCount,
                "J14 TickedSteps == 序列帧数（" + s.Timeline.TickedSteps + "）：重放不计入");
            int placeholders = 0;
            for (int i = 0; i < s.Script.Count; i++)
            {
                if (s.Script[i].Dt == 0) { placeholders++; }
            }

            Check(s.Timeline.PlaceholderSteps == placeholders,
                "J15 PlaceholderSteps == 占位帧数（" + s.Timeline.PlaceholderSteps + "/" + placeholders + "）");
            Check(s.Timeline.SimulatedSteps == s.BoundaryCount - placeholders,
                "J16 SimulatedSteps == 非占位帧数（" + s.Timeline.SimulatedSteps + "）：重放不改写它");

            long expectedReplay = 0;
            int placeholdersInReplay = 0;
            for (int i = 0; i < s.ReplayFrom.Length; i++)
            {
                if (!s.ReplayHappens[i] || s.ReplayFrom[i] > s.BoundaryCount) { continue; }
                long from = s.ReplayFrom[i];
                expectedReplay += s.BoundaryCount - from;
                for (long f = from; f < s.BoundaryCount; f++)
                {
                    if (s.Script[(int)f].Dt == 0) { placeholdersInReplay++; }
                }
            }

            Check(s.Timeline.ReplayedSteps == expectedReplay,
                "J17 ReplayedSteps == 各次校正实际重放步数之和（" + s.Timeline.ReplayedSteps + "/" + expectedReplay + "）");
            Check(s.CallsAfterBuild == (int)s.Timeline.SimulatedSteps,
                "J18 预测期模型调用数 == SimulatedSteps（" + s.CallsAfterBuild + "/" + s.Timeline.SimulatedSteps
                + "）：AP 每步只调一次、占位不调");

            int replayCalls = CountResimulatingCalls(s.ApModel.Calls);
            int tickCalls = s.ApModel.Calls.Count - replayCalls;
            Check(tickCalls == 0, "J19 D 节清空记录器后不应再出现非重放调用（实测 " + tickCalls + "）");
            Check(replayCalls == (int)s.Timeline.ReplayedSteps - placeholdersInReplay - s.ReplayCallsBeforeD,
                "J19b 重放调用数 == 重放步数 - 重放区间占位 - 已清空的历史重放调用（" + replayCalls + "/"
                + ((int)s.Timeline.ReplayedSteps - placeholdersInReplay - s.ReplayCallsBeforeD) + "）");

            // --- 确定性：整段场景用两个全新实例各跑一次，最终摘要逐位相同
            string digest = ScenarioDigest(BuildFreshScenario());
            string digest2 = ScenarioDigest(BuildFreshScenario());
            Check(digest == digest2,
                "J20 集成场景整体确定性：两个全新实例重跑最终摘要逐位相同");
            Check(digest.Length > 1000, "J21 摘要覆盖全部边界状态（长度 " + digest.Length + " 字符）");
        }

        // =================================================================================
        //  K. Stall 门回归（原为上游契约偏离，本轮由本任务修复）
        // =================================================================================

        private static void TestUpstreamDeviationRegression()
        {
            // 契约原文（net-r4-prediction-contract.md「时间、参数与安全决策」）：
            //   「Disconnected 只 Freeze，墙钟 2s 触发一次 ResyncRequested 通知(非每帧重复)；
            //     状态恢复只接受显式可信 Resync。」
            // 本轮修复落点（Client/Assets/Scripts/PMPrediction/PMPredictionTimeline.cs）：
            //   ApplyAuthority 在结构/epoch/instance/角色/未来/重复等校验**之前**先判 _frozen/_needsResync，
            //   命中即返回 PMAuthorityRejectReason.Stalled 并直接返回 —— 预测状态 / 确认边界 /
            //   已确认事件全部零改动；恢复只走显式 Resync（Resync 内部 ClearStallState）。
            PMMoverModel model = new PMMoverModel(new PMMoverTestWorld());
            PMMoverSyncState start = PMMoverSyncState.CreateDefault();
            PMMoverAuxState aux = PMMoverAuxState.CreateDefault();
            aux.CollisionWorldVersion = (int)PMMoverTestWorld.FrozenCollisionWorldVersion;

            PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState> tl =
                new PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState>(
                    model, Epoch, Instance, start, aux, PMNetRole.AutonomousProxy, HistoryCapacity, DisconnectTimeoutMs, FutureWindow);
            int frameAdvances = 0;
            int confirmedEvents = 0;
            List<ulong> confirmedKeys = new List<ulong>();
            tl.ConfirmedFrameAdvanced += delegate(PMFrameId a, PMFrameId b) { frameAdvances++; };
            tl.EventConfirmed += delegate(PMPredictionEvent e) { confirmedEvents++; confirmedKeys.Add(e.Key); };

            for (int i = 0; i < 4; i++)
            {
                Check(tl.Tick(16, MoveInput(1f, 0f, 0f), new PMFrameId(PMFrameDomain.AuthorityServer, i)).Accepted,
                    "K1 前置：Tick 接纳 @" + i);
            }

            Check(tl.PendingFrame.Value == 4L && tl.ConfirmedFrame.Value == 0L, "K2 前置：pending=4 confirmed=0");
            tl.Freeze();
            Check(tl.Tick(16, MoveInput(1f, 0f, 0f), new PMFrameId(PMFrameDomain.AuthorityServer, 4L))
                      .Reject == PMTickRejectReason.Frozen,
                "K3 前置对照：Freeze 后 Tick 已被拒（门只在 TickCore）");

            // 冻结期间送入一份"当前状态 + 合法事件证据"的权威快照：仍必须被拒绝。
            PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> authority =
                MakeAuthority(4L, tl.GetSyncSnapshot(), aux, tl.CurrentTotalSimTimeMs,
                              new PMFrameId(PMFrameDomain.AuthorityServer, 77L));
            authority.ConfirmedEventFrames = new PMPredictionEventFrame[]
            {
                new PMPredictionEventFrame(new PMFrameId(PMFrameDomain.Input, 4L),
                    new PMPredictionEvent[] { new PMPredictionEvent(7001UL, 1, 7) }),
            };

            long pendingBeforeStall = tl.PendingFrame.Value;
            PMAuthorityApplyResult res = tl.ApplyAuthority(authority);

            Check(!res.Applied && res.Reject == PMAuthorityRejectReason.Stalled,
                "K4 冻结期间 ApplyAuthority 必须被拒绝（Stalled：恢复只接受显式可信 Resync）；实测 Applied="
                + res.Applied + " Reject=" + res.Reject + " Reconciled=" + res.Reconciled);
            Check(!res.AdvancedConfirmed && frameAdvances == 0 && confirmedEvents == 0
                  && tl.ConfirmedFrame.Value == 0L && tl.PendingFrame.Value == pendingBeforeStall,
                "K5 冻结期间确认边界/已确认事件/待处理帧零改动；实测 AdvancedConfirmed="
                + res.AdvancedConfirmed + " 帧推进回调=" + frameAdvances + " 事件=" + confirmedEvents
                + " confirmed=" + tl.ConfirmedFrame.Value + " pending=" + tl.PendingFrame.Value);
            Check(tl.IsFrozen && tl.StalledAuthorities == 1,
                "K5b 冻结标记保持 true（不存在半冻结态），Stalled 拒绝计数可观测=1");

            // 同一处缺失门的第二个形态：HistoryExhausted -> NeedsResync 期间同样必须被拒绝。
            PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState> stuck =
                new PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState>(
                    model, Epoch, Instance, start, aux, PMNetRole.AutonomousProxy, 2, DisconnectTimeoutMs, FutureWindow);
            for (int i = 0; i < 2; i++)
            {
                stuck.Tick(16, MoveInput(1f, 0f, 0f), new PMFrameId(PMFrameDomain.AuthorityServer, i));
            }

            PMTickResult exhausted = stuck.Tick(16, MoveInput(1f, 0f, 0f),
                new PMFrameId(PMFrameDomain.AuthorityServer, 2L));
            Check(exhausted.Reject == PMTickRejectReason.HistoryExhausted && stuck.NeedsResync,
                "K6 前置：容量 2 的第三次 Tick -> HistoryExhausted + NeedsResync");

            PMAuthorityApplyResult stuckRes = stuck.ApplyAuthority(
                MakeAuthority(2L, stuck.TryGetBoundarySnapshotTyped(2L).Sync, aux, 32.0,
                              new PMFrameId(PMFrameDomain.AuthorityServer, 88L)));
            Check(!stuckRes.Applied && stuckRes.Reject == PMAuthorityRejectReason.Stalled
                  && !stuckRes.AdvancedConfirmed && stuck.ConfirmedFrame.Value == 0L && stuck.NeedsResync,
                "K7 NeedsResync 期间 ApplyAuthority 必须被拒绝（Stalled：恢复只走显式 Resync）；实测 Applied="
                + stuckRes.Applied + " Reject=" + stuckRes.Reject + " AdvancedConfirmed="
                + stuckRes.AdvancedConfirmed + " NeedsResync=" + stuck.NeedsResync);

            // K8：显式 Resync 是唯一恢复入口；恢复时确认区间内只广播有权威证据的边界。
            PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> recovery =
                MakeAuthority(2L, tl.TryGetBoundarySnapshotTyped(2L).Sync, aux, 32.0, PMFrameId.None);
            recovery.ConfirmedEventFrames = new PMPredictionEventFrame[]
            {
                new PMPredictionEventFrame(new PMFrameId(PMFrameDomain.Input, 1L),
                    new PMPredictionEvent[] { new PMPredictionEvent(7101UL, 1, 11) }),
                new PMPredictionEventFrame(new PMFrameId(PMFrameDomain.Input, 2L),
                    new PMPredictionEvent[] { new PMPredictionEvent(7102UL, 1, 12) }),
            };

            PMResyncResult recovered = tl.Resync(recovery);
            Check(recovered.Applied && !tl.IsFrozen && tl.ConfirmedFrame.Value == 2L,
                "K8 显式 Resync 解冻并把确认边界落到可信快照（confirmed=" + tl.ConfirmedFrame.Value + "）");
            Check(confirmedEvents == 2 && confirmedKeys.Count == 2 && confirmedKeys[0] == 7101UL
                  && confirmedKeys[1] == 7102UL,
                "K8b Resync 只广播携带证据的两个边界（实测 " + confirmedEvents + " 条，升序）");
            Check(tl.Tick(16, MoveInput(1f, 0f, 0f), new PMFrameId(PMFrameDomain.AuthorityServer, 5L)).Accepted,
                "K8c Resync 后 Tick 恢复推进");
            Check(tl.ApplyAuthority(MakeAuthority(3L, tl.TryGetBoundarySnapshotTyped(3L).Sync, aux, 48.0,
                      new PMFrameId(PMFrameDomain.AuthorityServer, 89L)))
                      .Reject == PMAuthorityRejectReason.None,
                "K8d Resync 后 ApplyAuthority 恢复可用（不再是 Stalled）");
        }

        // =================================================================================
        //  L. 权威事件证据（真实 PMMoverModel + 真实 timeline）：契约级断言
        // =================================================================================

        private static void TestAuthorityEventEvidence()
        {
            PMMoverTestWorld world = new PMMoverTestWorld();
            InstrumentedModel model = new InstrumentedModel(new PMMoverModel(world));
            model.InjectEvents = true;   // 模型每步都产出确定性预测事件 key=KeyOf(inputFrame)

            PMMoverSyncState start = PMMoverSyncState.CreateDefault();
            start.Position = new PMVector3(2f, PMMoverDefaults.CapsuleHalfHeightMeters, 0f);
            PMMoverAuxState aux = PMMoverAuxState.CreateDefault();
            aux.CollisionWorldVersion = world.WorldVersion;

            PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState> tl =
                new PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState>(
                    model, Epoch, Instance, start, aux, PMNetRole.AutonomousProxy,
                    HistoryCapacity, DisconnectTimeoutMs, FutureWindow);

            List<ulong> keys = new List<ulong>();
            List<int> values = new List<int>();
            tl.EventConfirmed += delegate(PMPredictionEvent e) { keys.Add(e.Key); values.Add(e.Value); };

            for (int i = 0; i < 4; i++)
            {
                PMTickResult tick = tl.Tick(16, MoveInput(1f, 0f, 0f),
                    new PMFrameId(PMFrameDomain.AuthorityServer, i));
                if (!tick.Accepted) { throw new InvalidOperationException("L 节 Tick 被拒 @" + i + "：" + tick.Reject); }
            }

            Check(keys.Count == 0, "L1 未确认前零广播（预测事件不是权威）");

            // ---- L2：无事件证据 -> 只确认状态、零广播，绝不拿预测事件顶替
            PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> noEvidence =
                MakeAuthority(4L, tl.GetSyncSnapshot(), aux, tl.CurrentTotalSimTimeMs,
                              new PMFrameId(PMFrameDomain.AuthorityServer, 900L));
            PMAuthorityApplyResult noEvidenceRes = tl.ApplyAuthority(noEvidence);
            Check(noEvidenceRes.Applied && noEvidenceRes.AdvancedConfirmed && noEvidenceRes.EventsConfirmed == 0
                  && tl.ConfirmedFrame.Value == 4L,
                "L2 无证据 -> 确认边界照常推进到 4、零广播（实测 " + noEvidenceRes.EventsConfirmed + " 条）");
            Check(keys.Count == 0 && tl.ConfirmedEventFramesApplied == 0 && tl.ConfirmedEventFramesSkipped == 4,
                "L2 4 个边界全部计入 skip、applied=0、预测事件一条都没被冒充广播");

            // ---- L3：带证据继续推进；证据深克隆；重复包不重播
            long boundary6 = 6L;
            for (int i = 4; i < 6; i++)
            {
                tl.Tick(16, MoveInput(1f, 0f, 0f), new PMFrameId(PMFrameDomain.AuthorityServer, i));
            }

            PMPredictionEvent[] ev5 = new PMPredictionEvent[] { new PMPredictionEvent(KeyOf(4L), 4, 404) };
            PMPredictionEvent[] ev6 = new PMPredictionEvent[] { new PMPredictionEvent(KeyOf(5L), 5, 505) };
            PMPredictionEventFrame[] frames5And6 = new PMPredictionEventFrame[]
            {
                new PMPredictionEventFrame(new PMFrameId(PMFrameDomain.Input, 5L), ev5),
                new PMPredictionEventFrame(new PMFrameId(PMFrameDomain.Input, 6L), ev6),
            };

            PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> withEvidence =
                MakeAuthority(boundary6, tl.GetSyncSnapshot(), aux, tl.CurrentTotalSimTimeMs,
                              new PMFrameId(PMFrameDomain.AuthorityServer, 901L));
            withEvidence.ConfirmedEventFrames = frames5And6;

            PMAuthorityApplyResult evidenceRes = tl.ApplyAuthority(withEvidence);
            Check(evidenceRes.Applied && evidenceRes.EventsConfirmed == 2 && keys.Count == 2,
                "L3 携带证据确认 4->6 -> 广播 2 条（实测 " + evidenceRes.EventsConfirmed + "）");
            Check(keys[0] == KeyOf(4L) && keys[1] == KeyOf(5L) && values[0] == 404 && values[1] == 505,
                "L3 按边界升序广播，且值来自证据（404/505，不是模型预测值）");
            Check(tl.ConfirmedEventFramesApplied == 2 && tl.ConfirmedEventFramesSkipped == 4,
                "L3 applied=2 / skipped 仍为 4");

            // 事后改写调用方持有的证据数组：历史必须不受污染
            ev5[0] = new PMPredictionEvent(999999UL, 9, 999);
            ev6[0] = new PMPredictionEvent(999998UL, 9, 998);
            frames5And6[0] = new PMPredictionEventFrame(new PMFrameId(PMFrameDomain.Input, 5L),
                new PMPredictionEvent[] { new PMPredictionEvent(999997UL, 9, 997) });

            PMPredictionEvent[] history5;
            bool history5Auth;
            Check(tl.TryGetBoundaryEvents(new PMFrameId(PMFrameDomain.Input, 5L), out history5, out history5Auth),
                "L3 可读取历史证据集合");
            Check(history5.Length == 1 && history5[0].Key == KeyOf(4L) && history5[0].Value == 404,
                "L3 事后改写证据数组不污染历史（key/value 未变）");
            Check(history5Auth, "L3 该边界事件被标记为权威");

            // 重复包（同一边界、不同内容、不同证据）仍旧包拒绝且不重播
            PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> duplicate =
                MakeAuthority(boundary6, tl.GetSyncSnapshot(), aux, 12345.0,
                              new PMFrameId(PMFrameDomain.AuthorityServer, 902L));
            duplicate.ConfirmedEventFrames = new PMPredictionEventFrame[]
            {
                new PMPredictionEventFrame(new PMFrameId(PMFrameDomain.Input, 6L),
                    new PMPredictionEvent[] { new PMPredictionEvent(424242UL, 4, 24) }),
            };
            PMAuthorityApplyResult duplicateRes = tl.ApplyAuthority(duplicate);
            Check(duplicateRes.Reject == PMAuthorityRejectReason.StaleOrDuplicate && duplicateRes.EventsConfirmed == 0
                  && keys.Count == 2,
                "L3 已确认边界的重复包（含新证据）-> StaleOrDuplicate 且不重播（实测 "
                + duplicateRes.Reject + " / " + keys.Count + " 条）");

            // ---- L4：跨边界跳确认只广播有证据的边界，不伪造中间边界
            PMMoverTestWorld jumpWorld = new PMMoverTestWorld();
            InstrumentedModel jumpModel = new InstrumentedModel(new PMMoverModel(jumpWorld));
            jumpModel.InjectEvents = true;
            PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState> jump =
                new PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState>(
                    jumpModel, Epoch, Instance, start, aux, PMNetRole.AutonomousProxy,
                    HistoryCapacity, DisconnectTimeoutMs, FutureWindow);
            List<ulong> jumpKeys = new List<ulong>();
            jump.EventConfirmed += delegate(PMPredictionEvent e) { jumpKeys.Add(e.Key); };

            for (int i = 0; i < 5; i++)
            {
                jump.Tick(16, MoveInput(1f, 0f, 0f), new PMFrameId(PMFrameDomain.AuthorityServer, i));
            }

            PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> jumpAuth =
                MakeAuthority(5L, jump.GetSyncSnapshot(), aux, jump.CurrentTotalSimTimeMs,
                              new PMFrameId(PMFrameDomain.AuthorityServer, 910L));
            jumpAuth.ConfirmedEvents = new PMPredictionEvent[]
            {
                new PMPredictionEvent(KeyOf(4L), 3, 333),
            };

            PMAuthorityApplyResult jumpRes = jump.ApplyAuthority(jumpAuth);
            Check(jumpRes.Applied && jumpRes.EventsConfirmed == 1 && jumpKeys.Count == 1
                  && jumpKeys[0] == KeyOf(4L),
                "L4 跳确认 0->5 只广播边界5 的证据（实测 " + jumpRes.EventsConfirmed + " 条）");
            Check(jump.ConfirmedEventFramesApplied == 1 && jump.ConfirmedEventFramesSkipped == 4,
                "L4 中间 4 个边界被跳过（不伪造中间边界事件）");
            Check(jump.ConfirmedFrame.Value == 5L, "L4 确认边界仍照常推进到 5");

            // ---- L5：证据有界校验（畸形证据 fail-closed，确认边界与事件零改动）
            PMPredictionEventFrame[] tooManyFrames = new PMPredictionEventFrame[
                PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState>.MaxConfirmedEventFrames + 1];
            for (int i = 0; i < tooManyFrames.Length; i++)
            {
                tooManyFrames[i] = new PMPredictionEventFrame(new PMFrameId(PMFrameDomain.Input, 1L),
                    new PMPredictionEvent[0]);
            }

            // 再推进一帧：确认边界 6 之后本次要确认的区间是 (6, 7]，边界 7 必须存在且不是未来边界。
            PMTickResult extraTick = tl.Tick(16, MoveInput(1f, 0f, 0f),
                new PMFrameId(PMFrameDomain.AuthorityServer, 6L));
            Check(extraTick.Accepted && tl.PendingFrame.Value == 7L, "L5 前置：推进到边界 7");

            PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> malformed =
                MakeAuthority(7L, tl.GetSyncSnapshot(), aux, tl.CurrentTotalSimTimeMs,
                              new PMFrameId(PMFrameDomain.AuthorityServer, 903L));
            malformed.ConfirmedEventFrames = tooManyFrames;

            long keysBeforeMalformed = keys.Count;
            PMAuthorityApplyResult malformedRes = tl.ApplyAuthority(malformed);
            Check(malformedRes.Reject == PMAuthorityRejectReason.Malformed && !malformedRes.Applied,
                "L5 证据条目超上限 -> Malformed（实测 " + malformedRes.Reject + "）");
            Check(tl.ConfirmedFrame.Value == boundary6 && keys.Count == keysBeforeMalformed,
                "L5 畸形证据后确认边界与已广播事件零改动");

            // 未来边界 + 证据：不得广播
            PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> future =
                MakeAuthority(FutureWindow + 50L, tl.GetSyncSnapshot(), aux, 0.0, PMFrameId.None);
            future.ConfirmedEvents = new PMPredictionEvent[] { new PMPredictionEvent(9911UL, 1, 1) };
            PMAuthorityApplyResult futureRes = tl.ApplyAuthority(future);
            Check(futureRes.Reject == PMAuthorityRejectReason.Future && keys.Count == keysBeforeMalformed,
                "L5 未来边界 + 证据 -> Future 且不广播（实测 " + futureRes.Reject + "）");
        }

        // =================================================================================
        //  场景（真实模型 + 真实 timeline + 真实 DS 预算）
        // =================================================================================

        private sealed class FrameSpec
        {
            public int Dt;
            public PMMoverInput Input;
            public string Tag;

            public FrameSpec(int dt, PMMoverInput input, string tag)
            {
                Dt = dt;
                Input = input;
                Tag = tag;
            }
        }

        private sealed class CallRecord
        {
            public bool Resimulating;
            public int StepMs;
            public long InputFrame;
            public long BaseSimTimeMs;
            public string InputDigest;
        }

        /// <summary>计数 + 纯查询自证的碰撞世界包装（真几何仍是 PMMoverTestWorld）。</summary>
        private sealed class CountingWorld : IPMMoverCollisionQuery
        {
            private readonly PMMoverTestWorld _inner;

            public long Sweeps;
            public long Grounds;

            public CountingWorld(PMMoverTestWorld inner) { _inner = inner; }

            public PMMoverTestWorld Inner { get { return _inner; } }

            public int WorldVersion { get { return _inner.WorldVersion; } }

            public PMMoverHit Sweep(PMVector3 position, PMVector3 delta, float radius, float halfHeight)
            {
                Sweeps++;
                PMMoverHit first = _inner.Sweep(position, delta, radius, halfHeight);
                PMMoverHit again = _inner.Sweep(position, delta, radius, halfHeight);
                if (!SameHit(first, again))
                {
                    throw new InvalidOperationException("[门禁] Sweep 不是纯查询：同参数两次结果不同。");
                }

                return first;
            }

            public PMMoverGround QueryGround(PMVector3 position, float radius, float halfHeight, float distance)
            {
                Grounds++;
                PMMoverGround first = _inner.QueryGround(position, radius, halfHeight, distance);
                PMMoverGround again = _inner.QueryGround(position, radius, halfHeight, distance);
                if (!SameGround(first, again))
                {
                    throw new InvalidOperationException("[门禁] QueryGround 不是纯查询：同参数两次结果不同。");
                }

                return first;
            }
        }

        /// <summary>
        /// 真实模型的**装饰器**：只做两件事——记录 Simulate 调用参数、按确定性公式注入预测事件。
        /// 状态数学 100% 委托给真实 PMMoverModel（本类没有任何位置/速度/层计算），
        /// 因此不构成"第二套 reconcile oracle"。
        /// </summary>
        private sealed class InstrumentedModel : IPMPredictionModel<PMMoverInput, PMMoverSyncState, PMMoverAuxState>
        {
            private readonly PMMoverModel _inner;

            public bool InjectEvents;
            public readonly List<CallRecord> Calls = new List<CallRecord>();

            public InstrumentedModel(PMMoverModel inner) { _inner = inner; }

            public PMMoverModel Inner { get { return _inner; } }

            public IPMMoverCollisionQuery CollisionQuery { get { return _inner.CollisionQuery; } }

            public PMMoverInput CloneInput(PMMoverInput input) { return _inner.CloneInput(input); }

            public PMMoverSyncState CloneSync(PMMoverSyncState sync) { return _inner.CloneSync(sync); }

            public PMMoverAuxState CloneAux(PMMoverAuxState aux) { return _inner.CloneAux(aux); }

            public bool ShouldReconcile(PMMoverSyncState predicted, PMMoverSyncState authority,
                                        PMMoverAuxState predictedAux, PMMoverAuxState authorityAux)
            {
                return _inner.ShouldReconcile(predicted, authority, predictedAux, authorityAux);
            }

            public PMMoverSyncState Interpolate(PMMoverSyncState from, PMMoverSyncState to, float alpha)
            {
                return _inner.Interpolate(from, to, alpha);
            }

            public PMSimulationResult<PMMoverSyncState, PMMoverAuxState> Simulate(
                PMTimeStep step, PMMoverInput input, PMMoverSyncState start, PMMoverAuxState aux)
            {
                PMSimulationResult<PMMoverSyncState, PMMoverAuxState> result =
                    _inner.Simulate(step, input, start, aux);

                CallRecord record = new CallRecord();
                record.Resimulating = step.IsResimulating;
                record.StepMs = (int)step.StepMs;
                record.InputFrame = step.ClientInputFrame.IsValid ? step.ClientInputFrame.Value : -1L;
                record.BaseSimTimeMs = step.BaseSimTimeMs;
                record.InputDigest = InputDigest(input);
                Calls.Add(record);

                if (InjectEvents && step.ClientInputFrame.IsValid)
                {
                    // 确定性事件：key 由输入帧决定（同一帧重放得到同一 key），
                    // value 由该步输出状态决定（因此回滚会替换同帧事件值）。
                    ulong key = KeyOf(step.ClientInputFrame.Value);
                    result.Events = new PMPredictionEvent[]
                    {
                        new PMPredictionEvent(key, (int)result.Sync.Mode, result.Sync.ValueOfPositionXMilli()),
                    };
                }

                return result;
            }
        }

        private sealed class Scenario
        {
            public List<FrameSpec> Script;
            public List<string> ScriptDigests;
            public PMMoverSyncState StartSync;
            public string StartSyncDigest;
            public PMMoverAuxState StartAux;
            public string StartAuxDigest;
            public PMMoverTestWorld WorldA;
            public CountingWorld ApWorld;
            public PMMoverTestWorld WorldB;
            public InstrumentedModel ApModel;
            public PMMoverModel DsModel;
            public PMMoverModel ReferenceModel;
            public PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState> Timeline;
            public PMAuthorityInputBuffer<PMMoverInput> DsBuffer;
            public long DsStepsPumped;
            public int DsPumpedMs;
            public List<PMMoverSyncState> DsSyncAt = new List<PMMoverSyncState>();
            public List<double> DsTotalAt = new List<double>();
            public List<PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState>> PreCorrectionAp =
                new List<PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState>>();
            public long BoundaryCount;
            public long WallContactFrame = -1L;
            public long WallApproachFrame = -1L;
            public long LandingFrame = -1L;
            public long ModeSwitchFrame = -1L;
            public List<long> ConfirmedAdvances = new List<long>();
            public List<ulong> EmittedKeys = new List<ulong>();
            public List<int> EmittedValues = new List<int>();
            public List<int> EmittedKinds = new List<int>();
            public long ResyncRequestCallbacks;
            public PMAuthorityApplyResult BoundaryZeroResult;

            /// <summary>各边界实际送进去的权威事件证据（供 F11 核对「广播的就是证据」）。</summary>
            public readonly Dictionary<long, PMPredictionEvent> EvidenceEvents =
                new Dictionary<long, PMPredictionEvent>();

            /// <summary>各次校正的起点边界（与 ReplayHappens 对齐）；供重放步数/调用数核对。</summary>
            public long[] ReplayFrom;

            /// <summary>该次校正是否真的发生了重放（模式切换帧那次是零回滚，不重放）。</summary>
            public bool[] ReplayHappens;

            /// <summary>首次校正之前记录到的模型调用数（== 预测期每步一次）。</summary>
            public int CallsAfterBuild;

            /// <summary>D 节清空记录器之前已累计的重放调用数（C0 那一次）。</summary>
            public int ReplayCallsBeforeD;
        }

        private static Scenario GetScenario()
        {
            if (_scenario == null)
            {
                _scenario = BuildFreshScenario();
            }

            return _scenario;
        }

        private static Scenario BuildFreshScenario()
        {
            Scenario s = new Scenario();

            // 两个**独立**的确定性碰撞世界实例（同 version），第三个实例只给 reference 用
            s.WorldA = new PMMoverTestWorld();
            s.ApWorld = new CountingWorld(s.WorldA);
            s.ApModel = new InstrumentedModel(new PMMoverModel(s.ApWorld));
            s.ApModel.InjectEvents = true;
            s.WorldB = new PMMoverTestWorld();
            s.DsModel = new PMMoverModel(s.WorldB);
            s.ReferenceModel = new PMMoverModel(new PMMoverTestWorld());

            PMMoverSyncState start = PMMoverSyncState.CreateDefault();
            start.Position = new PMVector3(2f, PMMoverDefaults.CapsuleHalfHeightMeters, 0f);
            start.JumpSpeed = 1.2f;   // 缩短滞空，让落地帧落在序列内
            s.StartSync = start;
            s.StartSyncDigest = SyncDigest(start);

            PMMoverAuxState aux = PMMoverAuxState.CreateDefault();
            aux.CollisionWorldVersion = s.WorldA.WorldVersion;
            aux.ConfigVersion = 7;
            s.StartAux = aux;
            s.StartAuxDigest = SyncDigest(aux);

            s.Script = BuildScript();
            s.ScriptDigests = new List<string>();
            for (int i = 0; i < s.Script.Count; i++)
            {
                s.ScriptDigests.Add(InputDigest(s.Script[i].Input));
            }

            s.BoundaryCount = s.Script.Count;

            // ---- AP：真实 timeline + 真实模型
            s.Timeline = new PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState>(
                s.ApModel, Epoch, Instance, s.StartSync, s.StartAux, PMNetRole.AutonomousProxy,
                HistoryCapacity, DisconnectTimeoutMs, FutureWindow);
            s.Timeline.ConfirmedFrameAdvanced += delegate(PMFrameId a, PMFrameId b)
            {
                s.ConfirmedAdvances.Add(b.Value - a.Value);
            };
            s.Timeline.EventConfirmed += delegate(PMPredictionEvent e)
            {
                s.EmittedKeys.Add(e.Key);
                s.EmittedValues.Add(e.Value);
                s.EmittedKinds.Add(e.Kind);
            };
            s.Timeline.ResyncRequested += delegate { s.ResyncRequestCallbacks++; };

            for (int i = 0; i < s.Script.Count; i++)
            {
                PMTickResult r = s.Timeline.Tick(s.Script[i].Dt, s.Script[i].Input,
                    new PMFrameId(PMFrameDomain.AuthorityServer, i));
                if (!r.Accepted)
                {
                    throw new InvalidOperationException("AP Tick 被拒绝 @" + i + "：" + r.Reject);
                }
            }

            for (long b = 0; b <= s.BoundaryCount; b++)
            {
                s.PreCorrectionAp.Add(s.Timeline.TryGetBoundarySnapshotTyped(b));
            }

            // ---- DS：真实输入缓冲按预算出步，驱动同一模型的第二个实例
            s.DsBuffer = new PMAuthorityInputBuffer<PMMoverInput>(s.DsModel.CloneInput, Epoch, Instance);
            PMMoverSyncState dsSync = s.DsModel.CloneSync(s.StartSync);
            PMMoverAuxState dsAux = s.DsModel.CloneAux(s.StartAux);
            double dsTotal = 0.0;
            s.DsSyncAt.Add(s.DsModel.CloneSync(dsSync));
            s.DsTotalAt.Add(dsTotal);

            List<PMAuthorityInput<PMMoverInput>> pumped = new List<PMAuthorityInput<PMMoverInput>>();
            for (int i = 0; i < s.Script.Count; i++)
            {
                PMAuthorityInput<PMMoverInput> item = new PMAuthorityInput<PMMoverInput>();
                item.Epoch = Epoch;
                item.InstanceId = Instance;
                item.InputFrame = i;
                item.Input = s.Script[i].Input;
                item.StepMs = s.Script[i].Dt;

                PMInputAdmissionResult admitted = s.DsBuffer.Admit(item);
                if (!admitted.Accepted)
                {
                    throw new InvalidOperationException("DS Admit 被拒绝 @" + i + "：" + admitted.Reject);
                }

                PumpAndApply(s, pumped, 16.0, ref dsSync, ref dsAux, ref dsTotal);
            }

            // 预算是真的会延后的（例如 dt=50 的帧要先攒够信用），
            // 因此继续按服务器墙钟推进直到队列排空 —— 这正是"预算驱动"的语义，
            // 不能假定"喂一条就立刻走一条"。
            int guard = 0;
            while ((s.DsBuffer.QueuedCount > 0 || s.DsBuffer.NextInputFrame < s.Script.Count) && guard < 100000)
            {
                PumpAndApply(s, pumped, 16.0, ref dsSync, ref dsAux, ref dsTotal);
                guard++;
            }

            if (s.DsSyncAt.Count != s.Script.Count + 1)
            {
                throw new InvalidOperationException(
                    "DS 未追平序列：边界状态数 " + s.DsSyncAt.Count + " != " + (s.Script.Count + 1)
                    + "（guard=" + guard + "，defer=" + s.DsBuffer.NeedsResync + "）");
            }

            DetectScenarioFrames(s);
            s.ReplayFrom = new long[]
            {
                0L,
                s.WallApproachFrame + 1L,
                s.LandingFrame + 1L,
                s.ModeSwitchFrame + 1L,
                s.BoundaryCount - 6L,
            };
            s.ReplayHappens = new bool[] { true, true, true, false, true };
            s.CallsAfterBuild = s.ApModel.Calls.Count;
            return s;
        }

        /// <summary>推进一次 DS 预算并把取出的输入逐步喂给权威模型（纯转发，无额外语义）。</summary>
        private static void PumpAndApply(Scenario s, List<PMAuthorityInput<PMMoverInput>> pumped, double wallElapsedMs,
                                         ref PMMoverSyncState dsSync, ref PMMoverAuxState dsAux, ref double dsTotal)
        {
            pumped.Clear();
            s.DsBuffer.Pump(wallElapsedMs, pumped);
            for (int k = 0; k < pumped.Count; k++)
            {
                PMAuthorityInput<PMMoverInput> item = pumped[k];
                s.DsStepsPumped++;
                s.DsPumpedMs += item.StepMs;

                if (item.StepMs != 0)
                {
                    PMTimeStep step = new PMTimeStep();
                    step.BaseSimTimeMs = (long)dsTotal;
                    step.StepMs = item.StepMs;
                    step.ServerFrame = new PMFrameId(PMFrameDomain.AuthorityServer, item.InputFrame);
                    step.ClientInputFrame = new PMFrameId(PMFrameDomain.Input, item.InputFrame);
                    step.IsResimulating = false;
                    PMSimulationResult<PMMoverSyncState, PMMoverAuxState> sim =
                        s.DsModel.Simulate(step, s.DsModel.CloneInput(item.Input), dsSync, dsAux);
                    dsSync = s.DsModel.CloneSync(sim.Sync);
                    dsAux = s.DsModel.CloneAux(sim.Aux);
                    dsTotal += item.StepMs;
                }

                s.DsSyncAt.Add(s.DsModel.CloneSync(dsSync));
                s.DsTotalAt.Add(dsTotal);
            }
        }

        private static void DetectScenarioFrames(Scenario s)
        {
            float contactPlane = PMMoverTestWorld.WallCenter.X + PMMoverTestWorld.WallSize.X * 0.5f
                                 + PMMoverDefaults.CapsuleRadiusMeters - PMMoverDefaults.CollisionSkinMeters;

            for (long b = 1; b <= s.BoundaryCount; b++)
            {
                PMMoverSyncState now = s.DsSyncAt[(int)b];
                PMMoverSyncState prev = s.DsSyncAt[(int)(b - 1)];

                if (s.WallContactFrame < 0L && now.Position.X <= contactPlane + 0.0015f)
                {
                    s.WallContactFrame = b - 1;
                }

                if (now.Position.X > contactPlane + 0.0015f && now.Position.X < 1.6f)
                {
                    s.WallApproachFrame = b - 1;
                }

                if (s.LandingFrame < 0L && prev.Mode == PMMoverMode.Falling && now.Mode == PMMoverMode.Walking)
                {
                    s.LandingFrame = b - 1;
                }

                if (s.ModeSwitchFrame < 0L && now.Mode == PMMoverMode.Flying)
                {
                    s.ModeSwitchFrame = b - 1;
                }
            }
        }

        private static List<FrameSpec> BuildScript()
        {
            List<FrameSpec> script = new List<FrameSpec>();
            int[] pattern = new int[] { 16, 16, 17, 15, 16, 16, 16, 16 };
            int cursor = 0;

            // 阶段 A：朝墙走（-X）。第 22..25 帧用上限 dt=50：既是"撞墙"，也是"不许穿透"的证明。
            for (int k = 0; k < 40; k++)
            {
                PMMoverInput input = MoveInput(-1f, 0f, 0f);
                int dt = pattern[cursor % pattern.Length];
                cursor++;
                string tag = "walk-wall";

                if (k == 5) { dt = 0; tag = "placeholder"; }
                if (k >= 22 && k <= 25) { dt = 50; tag = "walk-wall-dt50"; }
                if (k == 20)
                {
                    input.Layers = new PMMoverLayerRequest[]
                    {
                        LayerRequest(3u, PMMoverLayerKind.AdditiveVelocity, 0, new PMVector3(0.25f, 0f, 0f), 48),
                    };
                    tag = "layer-add";
                }

                if (k == 25)
                {
                    input.RemovedLayerIds = new uint[] { 3u };
                    tag = "layer-remove-dt50";
                }

                if (k == 33) { dt = 1; tag = "walk-wall-dt1"; }

                script.Add(new FrameSpec(dt, input, tag));
            }

            // 阶段 B：起跳 -> Falling（无空中操控，水平速度保留）
            for (int k = 0; k < 20; k++)
            {
                PMMoverInput input = MoveInput(-1f, 0f, 0f);
                int dt = pattern[cursor % pattern.Length];
                cursor++;
                string tag = "air";
                if (k == 0) { input.JumpPressed = true; tag = "jump"; }
                script.Add(new FrameSpec(dt, input, tag));
            }

            // 阶段 C：SetMode(Flying) + 加层/覆盖层/移除/到期
            for (int k = 0; k < 16; k++)
            {
                PMMoverInput input = MoveInput(0f, 1f, 0f);
                int dt = pattern[cursor % pattern.Length];
                cursor++;
                string tag = "fly";

                if (k == 0)
                {
                    input.Effects = new PMMoverEffectRequest[] { PMMoverEffectRequest.SetMode(PMMoverMode.Flying) };
                    input.Layers = new PMMoverLayerRequest[]
                    {
                        LayerRequest(11u, PMMoverLayerKind.AdditiveVelocity, 0, new PMVector3(1f, 0f, 0f), 0),
                    };
                    tag = "set-mode-flying";
                }

                if (k == 3)
                {
                    input.Layers = new PMMoverLayerRequest[]
                    {
                        LayerRequest(12u, PMMoverLayerKind.OverrideVelocity, 5, new PMVector3(0f, 0f, 2f), 64),
                    };
                    tag = "layer-override";
                }

                if (k == 6) { input.RemovedLayerIds = new uint[] { 11u }; tag = "layer-remove"; }

                script.Add(new FrameSpec(dt, input, tag));
            }

            // 阶段 D：SetMode(Walking) 后继续走（保持有限位移）
            for (int k = 0; k < 12; k++)
            {
                PMMoverInput input = MoveInput(0f, 1f, 0f);
                int dt = pattern[cursor % pattern.Length];
                cursor++;
                string tag = "walk-after";
                if (k == 0)
                {
                    input.Effects = new PMMoverEffectRequest[] { PMMoverEffectRequest.SetMode(PMMoverMode.Walking) };
                    tag = "set-mode-walking";
                }

                script.Add(new FrameSpec(dt, input, tag));
            }

            return script;
        }

        // =================================================================================
        //  reference：独立地用真实模型从修正边界逐输入 Simulate
        // =================================================================================

        private static void CompareReplayToReference(Scenario s, long boundary, long pendingBefore,
                                                    PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> authority,
                                                    string label)
        {
            PMMoverSyncState cursorSync = s.ReferenceModel.CloneSync(authority.Sync);
            PMMoverAuxState cursorAux = s.ReferenceModel.CloneAux(authority.Aux);
            double cursorTotal = authority.TotalSimTimeMs;

            int compared = 0;
            int syncMismatch = 0;
            int totalMismatch = 0;
            int serverFrameMismatch = 0;
            int simCalls = 0;
            string firstSync = null;
            string firstTotal = null;

            for (long f = boundary; f < pendingBefore; f++)
            {
                FrameSpec spec = s.Script[(int)f];
                if (spec.Dt != 0)
                {
                    PMTimeStep step = new PMTimeStep();
                    step.BaseSimTimeMs = (long)cursorTotal;
                    step.StepMs = spec.Dt;
                    step.ServerFrame = authority.ServerFrame;
                    step.ClientInputFrame = new PMFrameId(PMFrameDomain.Input, f);
                    step.IsResimulating = true;
                    PMSimulationResult<PMMoverSyncState, PMMoverAuxState> sim = s.ReferenceModel.Simulate(
                        step, s.ReferenceModel.CloneInput(spec.Input), cursorSync, cursorAux);
                    cursorSync = s.ReferenceModel.CloneSync(sim.Sync);
                    cursorAux = s.ReferenceModel.CloneAux(sim.Aux);
                    cursorTotal += spec.Dt;
                    simCalls++;
                }

                PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> got =
                    s.Timeline.TryGetBoundarySnapshotTyped(f + 1L);
                compared++;

                string diff = SyncDifference(got.Sync, cursorSync);
                if (diff != null)
                {
                    syncMismatch++;
                    if (firstSync == null) { firstSync = "边界 " + (f + 1) + "：" + diff; }
                }

                if (Math.Abs(got.TotalSimTimeMs - cursorTotal) > 1e-9)
                {
                    totalMismatch++;
                    if (firstTotal == null)
                    {
                        firstTotal = "边界 " + (f + 1) + "：" + got.TotalSimTimeMs + " vs " + cursorTotal;
                    }
                }

                // 契约决策：重放步的 ServerFrame 元数据取本次新学到的权威帧
                if (!(got.ServerFrame == authority.ServerFrame))
                {
                    serverFrameMismatch++;
                }
            }

            Check(syncMismatch == 0,
                label + "a 重放后每个边界状态 == 独立 reference 的真实模型 Simulate 结果（比对 " + compared
                + " 个边界，不一致 " + syncMismatch + (firstSync == null ? "）" : "；首个 " + firstSync + "）"));
            Check(totalMismatch == 0,
                label + "b 重放的累计仿真时间 == 权威起点 + 原 dt 累计（不一致 " + totalMismatch
                + (firstTotal == null ? "）" : "；首个 " + firstTotal + "）"));
            Check(serverFrameMismatch == 0,
                label + "c 重放步的 ServerFrame 元数据取本次新到的权威帧（不一致 " + serverFrameMismatch + "）");
            Check(simCalls > 0, label + "d reference 确实独立跑了 " + simCalls + " 次真实模型 Simulate");
        }

        // =================================================================================
        //  工具：快照构造 / 扰动 / 输入与摘要
        // =================================================================================

        private static PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> MakeAuthority(
            long boundary, PMMoverSyncState sync, PMMoverAuxState aux, double totalMs, PMFrameId serverFrame)
        {
            return MakeAuthorityWithFrame(Epoch, boundary, sync, aux, totalMs, serverFrame);
        }

        private static PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> MakeAuthorityWithEpoch(
            uint epoch, long boundary, PMMoverSyncState sync, PMMoverAuxState aux, double totalMs, PMFrameId serverFrame)
        {
            return MakeAuthorityWithFrame(epoch, boundary, sync, aux, totalMs, serverFrame);
        }

        private static PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> MakeAuthorityWithInstance(
            uint instance, long boundary, PMMoverSyncState sync, PMMoverAuxState aux, double totalMs, PMFrameId serverFrame)
        {
            PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> snapshot = MakeAuthorityWithFrame(
                Epoch, boundary, sync, aux, totalMs, serverFrame);
            snapshot.InstanceId = instance;
            return snapshot;
        }

        private static PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> MakeAuthorityWithFrame(
            uint epoch, long boundary, PMMoverSyncState sync, PMMoverAuxState aux, double totalMs, PMFrameId serverFrame)
        {
            PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> snapshot =
                new PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState>();
            snapshot.Epoch = epoch;
            snapshot.InstanceId = Instance;
            snapshot.OutputFrame = new PMFrameId(PMFrameDomain.Input, boundary);
            snapshot.ServerFrame = serverFrame;
            snapshot.TotalSimTimeMs = totalMs;
            snapshot.Sync = sync;
            snapshot.Aux = aux;
            return snapshot;
        }

        /// <summary>只为测试构造的"畸形帧域"快照：OutputFrame 用 AuthorityServer 命名空间。</summary>
        private static PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> MakeAuthorityWithFrame(
            long boundary, PMMoverSyncState sync, PMMoverAuxState aux, double totalMs, PMFrameId ignored,
            PMFrameId outputFrame)
        {
            PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> snapshot =
                MakeAuthority(boundary, sync, aux, totalMs, PMFrameId.None);
            snapshot.OutputFrame = outputFrame;
            return snapshot;
        }

        /// <summary>把六类状态一次改到位（位置/速度/mode/参数/层），用于"强制同帧差异"。</summary>
        private static PMMoverSyncState Perturb(PMMoverModel model, PMMoverSyncState basis, PMMoverMode mode)
        {
            PMMoverSyncState p = model.CloneSync(basis);
            p.Position = p.Position + new PMVector3(0.25f, 0.10f, -0.20f);
            p.Velocity = new PMVector3(1.5f, -0.5f, 2.5f);
            p.PreAdditiveVelocity = new PMVector3(1.25f, 0f, 2.0f);
            p.YawDegrees = 123.5f;
            p.Mode = mode;
            p.Grounded = mode == PMMoverMode.Walking;
            p.GroundNormal = PMVector3.Up;
            p.Scale = 0.8f;
            p.MaxSpeed = 4.4f;
            p.Acceleration = 22f;
            p.Braking = 18f;
            p.GravityScale = 1.25f;
            p.JumpSpeed = 1.6f;
            p.ActiveLayers = new PMMoverLayer[]
            {
                MakeLayer(101u, PMMoverLayerKind.AdditiveVelocity, 0, new PMVector3(0.7f, 0f, 0f), 0, 0),
                MakeLayer(102u, PMMoverLayerKind.OverrideVelocity, 2, new PMVector3(0f, 0.5f, 1.5f), 96, 10),
            };
            return p;
        }

        /// <summary>aux 版本与重力同时不同（契约：AuxGravity/版本变化参与 reconcile）。</summary>
        private static PMMoverAuxState PerturbAux(PMMoverAuxState basis)
        {
            PMMoverAuxState aux = basis;
            aux.Gravity = new PMVector3(0f, -5f, 0f);
            aux.CollisionWorldVersion = basis.CollisionWorldVersion + 1;
            aux.ConfigVersion = basis.ConfigVersion + 41;
            return aux;
        }

        private static PMMoverLayer MakeLayer(uint id, PMMoverLayerKind kind, int priority, PMVector3 velocity,
                                             int durationMs, int elapsedMs)
        {
            PMMoverLayer layer = new PMMoverLayer();
            layer.InstanceId = id;
            layer.Kind = kind;
            layer.Priority = priority;
            layer.Velocity = velocity;
            layer.DurationMs = durationMs;
            layer.ElapsedMs = elapsedMs;
            return layer;
        }

        private static PMMoverLayerRequest LayerRequest(uint id, PMMoverLayerKind kind, int priority,
                                                       PMVector3 velocity, int durationMs)
        {
            return new PMMoverLayerRequest(id, kind, priority, velocity, durationMs);
        }

        private static PMMoverInput MoveInput(float x, float z, float y)
        {
            PMMoverInput input = new PMMoverInput();
            input.MoveX = x;
            input.MoveZ = z;
            input.MoveY = y;
            input.YawDegrees = 0f;
            input.JumpPressed = false;
            input.Effects = null;
            input.Layers = null;
            input.RemovedLayerIds = null;
            return input;
        }

        private static PMAuthorityInputBuffer<PMMoverInput> NewBuffer(PMMoverModel model)
        {
            return new PMAuthorityInputBuffer<PMMoverInput>(model.CloneInput, Epoch, Instance);
        }

        private static PMAuthorityInput<PMMoverInput> BufInput(long frame, int dt, uint epoch, uint instance)
        {
            PMAuthorityInput<PMMoverInput> item = new PMAuthorityInput<PMMoverInput>();
            item.Epoch = epoch;
            item.InstanceId = instance;
            item.InputFrame = frame;
            item.Input = MoveInput(1f, 0f, 0f);
            item.StepMs = dt;
            return item;
        }

        private static ulong KeyOf(long inputFrame)
        {
            return (ulong)(inputFrame + 1L) * 1000UL + 1UL;
        }

        private static int CountResimulatingCalls(List<CallRecord> calls)
        {
            int count = 0;
            for (int i = 0; i < calls.Count; i++)
            {
                if (calls[i].Resimulating) { count++; }
            }

            return count;
        }

        private static PMMoverSyncState CurrentBoundarySync(Scenario s, long boundary)
        {
            return s.Timeline.TryGetBoundarySnapshotTyped(boundary).Sync;
        }

        // ------------------------------------------------------------------ 权威事件证据（本轮新增）

        /// <summary>
        /// 独立权威事件证据：事件 key 由产出该边界的输入帧决定（与 InstrumentedModel 的确定性公式同源），
        /// value 由该边界的**权威状态**决定 —— 不是读时间轴的预测事件集合。
        /// 占位帧（dt==0）确定无事件；边界 0 没有产出它的输入帧。
        /// </summary>
        private static PMPredictionEvent[] AuthorityEventsOf(Scenario s, long boundary, PMMoverSyncState state)
        {
            if (boundary <= 0L || s.Script[(int)(boundary - 1L)].Dt == 0)
            {
                return new PMPredictionEvent[0];
            }

            return new PMPredictionEvent[]
            {
                new PMPredictionEvent(KeyOf(boundary - 1L), (int)state.Mode, state.ValueOfPositionXMilli()),
            };
        }

        /// <summary>
        /// 为本次 ApplyAuthority 构造 (ConfirmedFrame, authorityBoundary] 的按边界证据批。
        /// · 权威边界自身：证据取自权威快照状态（校正后该边界就是这个状态）；
        /// · 中间边界：本次调用不改动它们，证据取自调用前时间轴持有的、已由前次校正固定的边界状态
        ///   （本集成门禁的宿主没有逐边界服务器事件通道，只能回放自己已确认的链；报告里明说）。
        /// 一次确认跨多个边界时不会伪造没有证据的中间边界 —— 见 L 节。
        /// </summary>
        private static PMPredictionEventFrame[] EvidenceForRange(Scenario s, long previousConfirmed, long boundary,
                                                                PMMoverSyncState authoritySync)
        {
            List<PMPredictionEventFrame> frames = new List<PMPredictionEventFrame>();
            for (long b = previousConfirmed + 1L; b <= boundary; b++)
            {
                PMMoverSyncState state = b == boundary
                    ? authoritySync
                    : s.Timeline.TryGetBoundarySnapshotTyped(b).Sync;
                PMPredictionEvent[] events = AuthorityEventsOf(s, b, state);
                frames.Add(new PMPredictionEventFrame(new PMFrameId(PMFrameDomain.Input, b), events));
                if (events.Length > 0)
                {
                    s.EvidenceEvents[b] = events[0];
                }
            }

            return frames.ToArray();
        }

        /// <summary>给权威快照挂上本次要确认区间的按边界证据批（必须在 ApplyAuthority 之前调用）。</summary>
        private static PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> WithEvidence(
            Scenario s, PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> authority)
        {
            long boundary = authority.OutputFrame.Value;
            authority.ConfirmedEventFrames =
                EvidenceForRange(s, s.Timeline.ConfirmedFrame.Value, boundary, authority.Sync);
            return authority;
        }

        private static int EmittedValueForKey(Scenario s, ulong key)
        {
            for (int i = 0; i < s.EmittedKeys.Count; i++)
            {
                if (s.EmittedKeys[i] == key) { return s.EmittedValues[i]; }
            }

            return int.MinValue;
        }

        private static int EmittedKindForKey(Scenario s, ulong key)
        {
            for (int i = 0; i < s.EmittedKinds.Count; i++)
            {
                if (s.EmittedKeys[i] == key) { return s.EmittedKinds[i]; }
            }

            return int.MinValue;
        }

        // =================================================================================
        //  工具：比较 / 摘要
        // =================================================================================

        private static string SyncDifference(PMMoverSyncState a, PMMoverSyncState b)
        {
            if (Bits(a.Position.X) != Bits(b.Position.X) || Bits(a.Position.Y) != Bits(b.Position.Y)
                || Bits(a.Position.Z) != Bits(b.Position.Z))
            {
                return "Position " + a.Position + " vs " + b.Position;
            }

            if (Bits(a.Velocity.X) != Bits(b.Velocity.X) || Bits(a.Velocity.Y) != Bits(b.Velocity.Y)
                || Bits(a.Velocity.Z) != Bits(b.Velocity.Z))
            {
                return "Velocity " + a.Velocity + " vs " + b.Velocity;
            }

            if (Bits(a.PreAdditiveVelocity.X) != Bits(b.PreAdditiveVelocity.X)
                || Bits(a.PreAdditiveVelocity.Y) != Bits(b.PreAdditiveVelocity.Y)
                || Bits(a.PreAdditiveVelocity.Z) != Bits(b.PreAdditiveVelocity.Z))
            {
                return "PreAdditiveVelocity " + a.PreAdditiveVelocity + " vs " + b.PreAdditiveVelocity;
            }

            if (Bits(a.YawDegrees) != Bits(b.YawDegrees)) { return "Yaw " + a.YawDegrees + " vs " + b.YawDegrees; }
            if (a.Mode != b.Mode) { return "Mode " + a.Mode + " vs " + b.Mode; }
            if (a.Grounded != b.Grounded) { return "Grounded " + a.Grounded + " vs " + b.Grounded; }
            if (Bits(a.GroundNormal.X) != Bits(b.GroundNormal.X) || Bits(a.GroundNormal.Y) != Bits(b.GroundNormal.Y)
                || Bits(a.GroundNormal.Z) != Bits(b.GroundNormal.Z))
            {
                return "GroundNormal " + a.GroundNormal + " vs " + b.GroundNormal;
            }

            if (Bits(a.Scale) != Bits(b.Scale)) { return "Scale " + a.Scale + " vs " + b.Scale; }
            if (Bits(a.MaxSpeed) != Bits(b.MaxSpeed)) { return "MaxSpeed " + a.MaxSpeed + " vs " + b.MaxSpeed; }
            if (Bits(a.Acceleration) != Bits(b.Acceleration)) { return "Acceleration"; }
            if (Bits(a.Braking) != Bits(b.Braking)) { return "Braking"; }
            if (Bits(a.GravityScale) != Bits(b.GravityScale)) { return "GravityScale"; }
            if (Bits(a.JumpSpeed) != Bits(b.JumpSpeed)) { return "JumpSpeed"; }

            PMMoverLayer[] la = a.ActiveLayers;
            PMMoverLayer[] lb = b.ActiveLayers;
            int ca = la == null ? 0 : la.Length;
            int cb = lb == null ? 0 : lb.Length;
            if (ca != cb) { return "ActiveLayers 数量 " + ca + " vs " + cb; }

            for (int i = 0; i < ca; i++)
            {
                if (la[i].InstanceId != lb[i].InstanceId || la[i].Kind != lb[i].Kind
                    || la[i].Priority != lb[i].Priority || la[i].DurationMs != lb[i].DurationMs
                    || la[i].ElapsedMs != lb[i].ElapsedMs
                    || Bits(la[i].Velocity.X) != Bits(lb[i].Velocity.X)
                    || Bits(la[i].Velocity.Y) != Bits(lb[i].Velocity.Y)
                    || Bits(la[i].Velocity.Z) != Bits(lb[i].Velocity.Z))
                {
                    return "ActiveLayers[" + i + "] " + la[i] + " vs " + lb[i];
                }
            }

            return null;
        }

        private static string SyncDigest(PMMoverSyncState s)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Bits(s.Position.X)).Append('|').Append(Bits(s.Position.Y)).Append('|').Append(Bits(s.Position.Z));
            sb.Append('|').Append(Bits(s.Velocity.X)).Append('|').Append(Bits(s.Velocity.Y)).Append('|').Append(Bits(s.Velocity.Z));
            sb.Append('|').Append(Bits(s.PreAdditiveVelocity.X)).Append('|').Append(Bits(s.PreAdditiveVelocity.Y));
            sb.Append('|').Append(Bits(s.PreAdditiveVelocity.Z)).Append('|').Append(Bits(s.YawDegrees));
            sb.Append('|').Append((int)s.Mode).Append('|').Append(s.Grounded ? '1' : '0');
            sb.Append('|').Append(Bits(s.GroundNormal.X)).Append('|').Append(Bits(s.GroundNormal.Y));
            sb.Append('|').Append(Bits(s.GroundNormal.Z)).Append('|').Append(Bits(s.Scale));
            sb.Append('|').Append(Bits(s.MaxSpeed)).Append('|').Append(Bits(s.Acceleration)).Append('|')
              .Append(Bits(s.Braking)).Append('|').Append(Bits(s.GravityScale)).Append('|').Append(Bits(s.JumpSpeed));
            sb.Append('|').Append(s.ActiveLayers == null ? 0 : s.ActiveLayers.Length);
            if (s.ActiveLayers != null)
            {
                for (int i = 0; i < s.ActiveLayers.Length; i++)
                {
                    sb.Append('|').Append(s.ActiveLayers[i].InstanceId).Append(':').Append((int)s.ActiveLayers[i].Kind)
                      .Append(':').Append(s.ActiveLayers[i].Priority).Append(':').Append(s.ActiveLayers[i].DurationMs)
                      .Append(':').Append(s.ActiveLayers[i].ElapsedMs)
                      .Append(':').Append(Bits(s.ActiveLayers[i].Velocity.X)).Append(':')
                      .Append(Bits(s.ActiveLayers[i].Velocity.Y)).Append(':').Append(Bits(s.ActiveLayers[i].Velocity.Z));
                }
            }

            return sb.ToString();
        }

        private static string SyncDigest(PMMoverAuxState aux)
        {
            return Bits(aux.Gravity.X) + "|" + Bits(aux.Gravity.Y) + "|" + Bits(aux.Gravity.Z) + "|"
                   + aux.CollisionWorldVersion + "|" + aux.ConfigVersion;
        }

        private static string InputDigest(PMMoverInput input)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Bits(input.MoveX)).Append(',').Append(Bits(input.MoveZ)).Append(',').Append(Bits(input.MoveY));
            sb.Append(',').Append(Bits(input.YawDegrees)).Append(',').Append(input.JumpPressed ? '1' : '0');
            sb.Append('|').Append(input.Effects == null ? 0 : input.Effects.Length).Append(',');
            if (input.Effects != null)
            {
                for (int i = 0; i < input.Effects.Length; i++)
                {
                    sb.Append((int)input.Effects[i].Kind).Append(':').Append(Bits(input.Effects[i].Vector.X)).Append(':')
                      .Append(Bits(input.Effects[i].Vector.Y)).Append(':').Append(Bits(input.Effects[i].Vector.Z))
                      .Append(':').Append(input.Effects[i].Mode).Append(':').Append(Bits(input.Effects[i].MaxSpeed))
                      .Append(':').Append(Bits(input.Effects[i].Acceleration)).Append(':')
                      .Append(Bits(input.Effects[i].Braking)).Append(':').Append(Bits(input.Effects[i].GravityScale))
                      .Append(':').Append(Bits(input.Effects[i].JumpSpeed)).Append(';');
                }
            }

            sb.Append('|').Append(input.Layers == null ? 0 : input.Layers.Length).Append(',');
            if (input.Layers != null)
            {
                for (int i = 0; i < input.Layers.Length; i++)
                {
                    sb.Append(input.Layers[i].InstanceId).Append(':').Append((int)input.Layers[i].Kind).Append(':')
                      .Append(input.Layers[i].Priority).Append(':').Append(Bits(input.Layers[i].Velocity.X)).Append(':')
                      .Append(Bits(input.Layers[i].Velocity.Y)).Append(':').Append(Bits(input.Layers[i].Velocity.Z))
                      .Append(':').Append(input.Layers[i].DurationMs).Append(';');
                }
            }

            sb.Append('|').Append(input.RemovedLayerIds == null ? 0 : input.RemovedLayerIds.Length).Append(',');
            if (input.RemovedLayerIds != null)
            {
                for (int i = 0; i < input.RemovedLayerIds.Length; i++)
                {
                    sb.Append(input.RemovedLayerIds[i]).Append(';');
                }
            }

            return sb.ToString();
        }

        private static string ScenarioDigest(Scenario s)
        {
            StringBuilder sb = new StringBuilder();
            for (long b = 0; b <= s.BoundaryCount; b++)
            {
                sb.Append(b).Append('=').Append(SyncDigest(s.Timeline.TryGetBoundarySnapshotTyped(b).Sync))
                  .Append(';');
            }

            sb.Append('|').Append(s.Timeline.ConfirmedFrame.Value).Append('|').Append(s.Timeline.ReplayedSteps)
              .Append('|').Append(s.EmittedKeys.Count);
            return sb.ToString();
        }

        private static string JoinInts(List<int> values)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) { sb.Append(','); }
                sb.Append(values[i]);
            }

            return sb.ToString();
        }

        private static int Bits(float value)
        {
            return BitConverter.SingleToInt32Bits(value);
        }

        private static bool SameHit(PMMoverHit a, PMMoverHit b)
        {
            return a.Blocking == b.Blocking && Bits(a.Fraction) == Bits(b.Fraction)
                   && Bits(a.Normal.X) == Bits(b.Normal.X) && Bits(a.Normal.Y) == Bits(b.Normal.Y)
                   && Bits(a.Normal.Z) == Bits(b.Normal.Z);
        }

        private static bool SameGround(PMMoverGround a, PMMoverGround b)
        {
            return a.Found == b.Found && Bits(a.Distance) == Bits(b.Distance)
                   && Bits(a.Normal.X) == Bits(b.Normal.X) && Bits(a.Normal.Y) == Bits(b.Normal.Y)
                   && Bits(a.Normal.Z) == Bits(b.Normal.Z);
        }

        private static bool SameBox(PMMoverTestBox a, PMMoverTestBox b)
        {
            return Bits(a.Min.X) == Bits(b.Min.X) && Bits(a.Min.Y) == Bits(b.Min.Y) && Bits(a.Min.Z) == Bits(b.Min.Z)
                   && Bits(a.Max.X) == Bits(b.Max.X) && Bits(a.Max.Y) == Bits(b.Max.Y) && Bits(a.Max.Z) == Bits(b.Max.Z);
        }

        private static bool HasMethodNamed(Type type, string fragment)
        {
            MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                  | BindingFlags.Instance | BindingFlags.Static
                                                  | BindingFlags.DeclaredOnly);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasStaticMutableField(Type type)
        {
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            for (int i = 0; i < fields.Length; i++)
            {
                if (!fields[i].IsLiteral && !fields[i].IsInitOnly)
                {
                    return true;
                }
            }

            return false;
        }

        // =================================================================================
        //  断言
        // =================================================================================

        private static void Check(bool condition, string what)
        {
            if (condition)
            {
                _passed++;
                return;
            }

            Fail(what);
        }

        private static void Fail(string what)
        {
            _failures.Add(what);
            Console.WriteLine("    [FAIL] " + what);
        }

        private static void Section(string name, Action test)
        {
            Console.WriteLine("  " + name);
            try
            {
                test();
            }
            catch (Exception ex)
            {
                Fail(name + " 抛出未捕获异常：" + ex.GetType().Name + " " + ex.Message);
            }

            Console.WriteLine();
        }
    }

    // =====================================================================================
    //  扩展：只为门禁可读性服务的辅助方法（不改任何生产代码）
    // =====================================================================================

    internal static class TestExtensions
    {
        public static bool RejectedAs(this PMAuthorityApplyResult result, PMAuthorityRejectReason reason)
        {
            return !result.Applied && result.Reject == reason;
        }

        public static bool HasRollbackApi(this PMInterpolationBuffer<PMMoverSyncState, PMMoverAuxState> buffer)
        {
            Type type = typeof(PMInterpolationBuffer<PMMoverSyncState, PMMoverAuxState>);
            string[] banned = new string[] { "Reconcile", "Restore", "Rollback", "ApplyAuthority", "Resimulate" };
            for (int i = 0; i < banned.Length; i++)
            {
                MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                       | BindingFlags.Instance | BindingFlags.Static);
                for (int k = 0; k < methods.Length; k++)
                {
                    if (methods[k].Name.IndexOf(banned[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static int ValueOfPositionXMilli(this PMMoverSyncState sync)
        {
            return (int)Math.Round((double)sync.Position.X * 1000.0);
        }

        public static PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> TryGetBoundarySnapshotTyped(
            this PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState> timeline, long boundary)
        {
            PMPredictionSnapshot<PMMoverSyncState, PMMoverAuxState> snapshot;
            if (!timeline.TryGetBoundarySnapshot(new PMFrameId(PMFrameDomain.Input, boundary), out snapshot))
            {
                throw new InvalidOperationException("[门禁] 边界 " + boundary + " 不在历史窗口内。");
            }

            return snapshot;
        }

        public static double TryGetBoundarySnapshotTotal(
            this PMPredictionTimeline<PMMoverInput, PMMoverSyncState, PMMoverAuxState> timeline, long boundary)
        {
            return timeline.TryGetBoundarySnapshotTyped(boundary).TotalSimTimeMs;
        }
    }
}
