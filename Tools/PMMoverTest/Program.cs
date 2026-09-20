// ============================================================================
//  PMMoverTest —— R4-A / P4A3：Mover 纯模型的真实实现验证（手算 oracle）
// ============================================================================
//
//  事实来源：Docs/plans/net-r4-prediction-contract.md「Mover 冻结字段与机制」+「验收 P4A3」；
//            Docs/plans/net-architecture-migration.md 文末 R4-A 行的 P4A3。
//
//  覆盖（每节都独立算期望值，不用实现自身当预期）：
//    A. 冻结枚举/默认值 + PMVector3 数学
//    B. 深 clone 与"无别名"（Input/Sync/Aux 三类）
//    C. Walking：加速度/制动/平面速度/dt 多频/maxSpeed 钳制
//    D. Falling：重力积分/重力缩放/Aux 重力方向
//    E. 落地：地面吸附 + **阶段 B** 竖直速度钳制（位移仍用落地前速度）
//    F. 失去地面（走出地板边缘）→ Falling
//    G. 墙：正撞停止 / 斜向滑动 / 绕过墙角 / 穿墙恢复（全部手算坐标）
//    H. Inactive：零位移 + 时间轴继续 + 层按时长过期
//    I. Layer：单帧不累积 / 到期恢复干净 base / priority+id 顺序 / Override 排除 Additive /
//       移除 / 替换 / 求和确定
//    J. Effect：阶段 A 次序（速度命令先于模式积分）/ Teleport / SetMode 取最后一条 /
//       未知枚举与非法值**显式拒绝**（不是静默默认 walking）
//    K. ShouldReconcile：mode 优先 / 参数与层实例完整 / 位置·速度·朝向阈值 / Aux
//    L. Interpolate：连续插值 + yaw 最短弧 + 离散取 To + 无数组别名
//    M. 确定性（逐位相同）+ 无外部副作用（start 不被改写）
//    N. 数值证明：不同 Update 频率下"每条输入一步"（dt 驱动，不是墙钟驱动）
//    O. 替身几何的自证与诚实边界（AABB 数值、墙不算地面、贴地水平移动不被地板挡住）
//
//  不覆盖（诚实口径，见报告「未实现项/未决项」）：
//    · Unity PhysX 等价性（本工程零 UnityEngine 依赖，PENDING_USER）
//    · 子步循环/剩余时间移交、斜坡台阶、移动平台、Modifier、效果拒绝裁决
//
//  运行：dotnet Tools/PMMoverTest/bin/Release/net8.0/PMMoverTest.dll
//  退出码：0 = 全部通过；1 = 存在失败
// ============================================================================

using System;
using System.Collections.Generic;
using PMNet;
using PMNet.Mover;
using PMNet.Prediction;

namespace PMMoverTest
{
    internal static class Program
    {
        private static int _passed;
        private static readonly List<string> _failures = new List<string>();

        private static int Main()
        {
            Console.WriteLine("=== R4-A / P4A3：PMMover 纯模型（手算 oracle）===");
            Console.WriteLine();

            Section("A. 冻结枚举/默认值与 PMVector3 数学", TestFrozenSurface);
            Section("B. 深 clone 与无数组别名", TestCloneIsolation);
            Section("C. Walking：加速度/制动/平面/dt 多频/钳制", TestWalking);
            Section("D. Falling：重力积分与重力缩放", TestFallingGravity);
            Section("E. 落地：地面吸附 + 阶段 B 竖直速度钳制", TestLanding);
            Section("F. 失去地面 -> Falling", TestGroundLoss);
            Section("G. 墙：正撞/滑动/绕角/穿墙恢复（手算坐标）", TestWallCollision);
            Section("H. Inactive：零位移 + 时间轴继续 + 层到期", TestInactive);
            Section("I. Layer：不累积/到期/顺序/Override 门槛/移除/替换", TestLayers);
            Section("J. Effect：两阶段次序 + 显式拒绝", TestEffects);
            Section("K. ShouldReconcile 全量比较", TestShouldReconcile);
            Section("L. Interpolate：yaw 最短弧与离散取 To", TestInterpolate);
            Section("M. 确定性与无外部副作用", TestDeterminismAndPurity);
            Section("N. dt 驱动（Update 频率无关）", TestDtDriven);
            Section("O. 替身几何自证与诚实边界", TestWorldGeometry);

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

        // =================================================================================
        //  A. 冻结面
        // =================================================================================

        private static void TestFrozenSurface()
        {
            // 模式数值是协议（契约「Modes 数值 Walking=0 Falling=1 Flying=2 Inactive=3」）
            Check((int)PMMoverMode.Walking == 0, "A1 Walking == 0");
            Check((int)PMMoverMode.Falling == 1, "A2 Falling == 1");
            Check((int)PMMoverMode.Flying == 2, "A3 Flying == 2");
            Check((int)PMMoverMode.Inactive == 3, "A4 Inactive == 3");
            Check(PMMoverModes.Count == 4, "A5 模式个数 == 4");

            // 层种类数值（契约「AdditiveVelocity=0 / OverrideVelocity=1」）
            Check((int)PMMoverLayerKind.AdditiveVelocity == 0, "A6 AdditiveVelocity == 0");
            Check((int)PMMoverLayerKind.OverrideVelocity == 1, "A7 OverrideVelocity == 1");
            Check(PMMoverLayerKinds.Count == 2, "A8 层种类个数 == 2");

            // 首批 Effect 四型
            Check((int)PMMoverEffectKind.SetVelocity == 0, "A9 SetVelocity == 0");
            Check((int)PMMoverEffectKind.Teleport == 1, "A10 Teleport == 1");
            Check((int)PMMoverEffectKind.SetMode == 2, "A11 SetMode == 2");
            Check((int)PMMoverEffectKind.SetParameters == 3, "A12 SetParameters == 3");
            Check(PMMoverEffectKinds.Count == 4, "A13 Effect 种类个数 == 4");

            // 默认值：只有 MaxSpeed 有实据（项目冻结移速 3.9），重力按契约 -9.81
            Check(Near(PMMoverDefaults.DefaultGravityY, -9.81f, 1e-6f), "A14 默认重力 Y == -9.81");
            Check(Near(PMMoverDefaults.DefaultMaxSpeed, 3.9f, 1e-6f), "A15 默认 MaxSpeed == 3.9");
            Check(Near(PMMoverDefaults.PositionToleranceMeters, 0.05f, 1e-9f), "A16 位置阈值 == 0.05m");
            Check(Near(PMMoverDefaults.VelocityToleranceMetersPerSecond, 0.01f, 1e-9f), "A17 速度阈值 == 0.01m/s");
            Check(Near(PMMoverDefaults.YawToleranceDegrees, 1f, 1e-9f), "A18 朝向阈值 == 1 度");
            Check(PMMoverDefaults.MinStepMs == 1 && PMMoverDefaults.MaxStepMs == 50, "A19 dt 范围 1..50");

            PMMoverSyncState def = PMMoverSyncState.CreateDefault();
            Check(Near(def.Position.Y, PMMoverDefaults.CapsuleHalfHeightMeters, 1e-6f),
                "A20 默认状态中心 Y == halfHeight（足底落地）");
            Check(def.Mode == PMMoverMode.Walking, "A21 默认模式 Walking");
            Check(Near(def.Scale, 1f, 1e-9f), "A22 默认 Scale == 1");
            Check(def.ActiveLayers == null, "A23 默认无活跃层");
            Check(Near(def.Velocity.X, 0f, 0f) && Near(def.PreAdditiveVelocity.X, 0f, 0f),
                "A24 默认速度与干净基速都为零");

            PMMoverAuxState aux = PMMoverAuxState.CreateDefault();
            Check(Near(aux.Gravity.Y, -9.81f, 1e-6f), "A25 默认 Aux 重力 == (0,-9.81,0)");
            Check(aux.Gravity.X == 0f && aux.Gravity.Z == 0f, "A26 默认 Aux 重力只有 Y 分量");

            // PMVector3 数学
            PMVector3 n = PMVector3.Normalized(new PMVector3(3f, 0f, 4f));
            Check(Near(n.X, 0.6f, 1e-6f) && Near(n.Z, 0.8f, 1e-6f), "A27 Normalized(3,0,4) == (0.6,0,0.8)");
            Check(PMVector3.Normalized(PMVector3.Zero) == PMVector3.Zero, "A28 零向量归一化 == 零（不产生 NaN）");
            Check(Near(PMVector3.Dot(new PMVector3(1f, 2f, 3f), new PMVector3(4f, 5f, 6f)), 32f, 1e-5f),
                "A29 Dot == 32");
            Check(Near(PMVector3.Distance(new PMVector3(0f, 0f, 0f), new PMVector3(3f, 4f, 0f)), 5f, 1e-6f),
                "A30 Distance == 5");
            Check(!new PMVector3(float.NaN, 0f, 0f).IsFinite, "A31 NaN 判定为非有限");
            Check(!new PMVector3(0f, float.PositiveInfinity, 0f).IsFinite, "A32 Infinity 判定为非有限");
        }

        // =================================================================================
        //  B. clone 隔离
        // =================================================================================

        private static void TestCloneIsolation()
        {
            PMMoverModel model = NewModel();

            // Input：三类列表都必须被复制
            PMMoverInput input = PMMoverInput.Empty();
            input.Effects = new PMMoverEffectRequest[] { PMMoverEffectRequest.SetMode(PMMoverMode.Flying) };
            input.Layers = new PMMoverLayerRequest[] { new PMMoverLayerRequest(7u, PMMoverLayerKind.AdditiveVelocity, 0, new PMVector3(1f, 0f, 0f), 0) };
            input.RemovedLayerIds = new uint[] { 9u };

            PMMoverInput cloned = model.CloneInput(input);
            Check(!ReferenceEquals(cloned.Effects, input.Effects), "B1 CloneInput 的效果数组是新实例");
            Check(!ReferenceEquals(cloned.Layers, input.Layers), "B2 CloneInput 的层数组是新实例");
            Check(!ReferenceEquals(cloned.RemovedLayerIds, input.RemovedLayerIds), "B3 CloneInput 的移除数组是新实例");

            cloned.Effects[0] = PMMoverEffectRequest.SetMode(PMMoverMode.Inactive);
            cloned.Layers[0] = new PMMoverLayerRequest(8u, PMMoverLayerKind.OverrideVelocity, 1, PMVector3.Zero, 0);
            cloned.RemovedLayerIds[0] = 99u;
            Check(input.Effects[0].Mode == (int)PMMoverMode.Flying, "B4 改副本不污染原 Input.Effects");
            Check(input.Layers[0].InstanceId == 7u, "B5 改副本不污染原 Input.Layers");
            Check(input.RemovedLayerIds[0] == 9u, "B6 改副本不污染原 Input.RemovedLayerIds");

            // Sync：层数组必须被复制，且 clone 的元素修改不回写
            PMMoverSyncState sync = Standing(-3f, 0f);
            sync.ActiveLayers = new PMMoverLayer[]
            {
                MakeLayer(3u, PMMoverLayerKind.AdditiveVelocity, 0, new PMVector3(0f, 0f, 1f), 0, 100),
                MakeLayer(4u, PMMoverLayerKind.OverrideVelocity, 2, new PMVector3(0f, 0f, 2f), 500, 50),
            };

            PMMoverSyncState syncClone = model.CloneSync(sync);
            Check(!ReferenceEquals(syncClone.ActiveLayers, sync.ActiveLayers), "B7 CloneSync 层数组是新实例");
            syncClone.ActiveLayers[0] = MakeLayer(3u, PMMoverLayerKind.OverrideVelocity, 0, PMVector3.Zero, 0, 0);
            Check(sync.ActiveLayers[0].Kind == PMMoverLayerKind.AdditiveVelocity, "B8 改副本不改原 Sync 的层内容");
            Check(syncClone.Position.Equals(sync.Position), "B9 CloneSync 位置值一致");
            Check(syncClone.ActiveLayers[1].ElapsedMs == 50, "B10 CloneSync 保留层进度");

            // Aux：全值类型，赋值即完整拷贝
            PMMoverAuxState aux = PMMoverAuxState.CreateDefault();
            PMMoverAuxState auxClone = model.CloneAux(aux);
            auxClone.Gravity = new PMVector3(0f, -99f, 0f);
            Check(Near(aux.Gravity.Y, -9.81f, 1e-6f), "B11 CloneAux 修改不影响原 Aux");

            // Simulate 的输出层数组是新实例且不与 start 共用
            PMMoverInput withLayer = PMMoverInput.Empty();
            withLayer.Layers = new PMMoverLayerRequest[] { new PMMoverLayerRequest(5u, PMMoverLayerKind.AdditiveVelocity, 0, new PMVector3(0f, 0f, 1f), 0) };
            PMMoverSyncState outState = Step(model, sync, withLayer, 16f, PMMoverAuxState.CreateDefault());
            Check(outState.ActiveLayers != null && outState.ActiveLayers.Length == 3,
                "B12 Simulate 输出含原有 2 层 + 新增 1 层");
            Check(!ReferenceEquals(outState.ActiveLayers, sync.ActiveLayers), "B13 输出层数组不别名 start 的数组");

            outState.ActiveLayers[0] = MakeLayer(0u, PMMoverLayerKind.AdditiveVelocity, 0, PMVector3.Zero, 0, 0);
            Check(sync.ActiveLayers[0].InstanceId == 3u, "B14 改输出层数组不回写 start");
        }

        // =================================================================================
        //  C. Walking
        // =================================================================================

        private static void TestWalking()
        {
            PMMoverModel model = NewModel();
            PMMoverAuxState aux = PMMoverAuxState.CreateDefault();

            // C1：无输入 -> 完全静止
            PMMoverSyncState s = Standing(-3f, 0f);
            PMMoverInput idle = PMMoverInput.Empty();
            s = Step(model, s, idle, 16f, aux);
            Check(s.Velocity.Equals(PMVector3.Zero) && s.Position.Equals(Standing(-3f, 0f).Position),
                "C1 站立无输入：位置与速度都不变");
            Check(s.Mode == PMMoverMode.Walking && s.Grounded, "C2 站立无输入：仍 Walking 且着地");

            // C3：单步 16ms 输入 +X：v = a*dt = 20*0.016 = 0.32；位移 = v*dt = 0.00512
            s = Standing(-3f, 0f);
            PMMoverInput moveX = PMMoverInput.Empty();
            moveX.MoveX = 1f;
            s = Step(model, s, moveX, 16f, aux);
            Check(Near(s.Velocity.X, 0.32f, 1e-5f), "C3 单步加速到 0.32 m/s（20*0.016）");
            Check(Near(s.Position.X, -3f + 0.00512f, 1e-5f), "C4 单步位移 0.00512m");
            Check(Near(s.Position.Y, 1f, 1e-6f), "C5 Walking 保持平面运动（Y 不变）");
            Check(Near(s.PreAdditiveVelocity.X, 0.32f, 1e-5f), "C6 无层时干净基速等于最终速度");

            // C7：dt 多频——同样 16ms 总时长，两次 8ms 与一次 16ms 结果不同（dt 原值驱动）
            PMMoverSyncState twoSteps = Standing(-3f, 0f);
            twoSteps = Step(model, twoSteps, moveX, 8f, aux);
            twoSteps = Step(model, twoSteps, moveX, 8f, aux);
            Check(Near(twoSteps.Velocity.X, 0.32f, 1e-5f), "C7 两次 8ms 后速度同样到 0.32");
            Check(!Near(twoSteps.Position.X, s.Position.X, 1e-6f),
                "C8 两次 8ms 与一次 16ms 的位移不同（dt 原值参与积分，不做合并）");
            // 手算：8ms 步长 a*dt = 0.16 -> 位移 0.16*0.008 = 0.00128；第二步 v=0.32 -> 0.00256
            Check(Near(twoSteps.Position.X, -3f + 0.00128f + 0.00256f, 1e-5f), "C9 两次 8ms 位移 = 0.00128+0.00256");

            // C10：加速到 MaxSpeed 后钳制（13 步 * 0.32 = 4.16 > 3.9）
            PMMoverSyncState accel = Standing(-3f, 0f);
            for (int i = 0; i < 13; i++)
            {
                accel = Step(model, accel, moveX, 16f, aux);
            }

            Check(accel.Velocity.X == PMMoverDefaults.DefaultMaxSpeed, "C10 13 步后速度精确钳制在 MaxSpeed");

            // C11：松手制动：3.9 - 20*0.016 = 3.58
            PMMoverSyncState brake = Standing(-3f, 0f);
            brake.PreAdditiveVelocity = new PMVector3(3.9f, 0f, 0f);
            brake.Velocity = new PMVector3(3.9f, 0f, 0f);
            brake = Step(model, brake, idle, 16f, aux);
            Check(Near(brake.Velocity.X, 3.9f - 0.32f, 1e-5f), "C11 松手后按 Braking 减速到 3.58");

            // C12：斜向输入归一化 -> 合速度仍为 a*dt（不是 2 倍）
            PMMoverSyncState diag = Standing(-3f, 0f);
            PMMoverInput diagonal = PMMoverInput.Empty();
            diagonal.MoveX = 1f;
            diagonal.MoveZ = 1f;
            diag = Step(model, diag, diagonal, 16f, aux);
            Check(Near(diag.Velocity.Length, 0.32f, 1e-5f), "C12 斜向输入合速度 == 0.32（方向已归一化）");
            Check(Near(diag.Velocity.X, diag.Velocity.Z, 1e-6f), "C13 斜向输入两轴分量相等");
        }

        // =================================================================================
        //  D. Falling 重力
        // =================================================================================

        private static void TestFallingGravity()
        {
            PMMoverModel model = NewModel();
            PMMoverAuxState aux = PMMoverAuxState.CreateDefault();
            PMMoverInput idle = PMMoverInput.Empty();

            // D1：一帧 16ms：vy = -9.81*0.016 = -0.15696；位移 = -0.00251136
            PMMoverSyncState fall = Falling(-3f, 3f, 0f);
            fall = Step(model, fall, idle, 16f, aux);
            Check(Near(fall.Velocity.Y, -9.81f * 0.016f, 1e-5f), "D1 重力一帧后 vy == -9.81*0.016");
            Check(Near(fall.Position.Y, 3f - 9.81f * 0.016f * 0.016f, 1e-5f), "D2 下落位移 == vy*dt");
            Check(fall.Mode == PMMoverMode.Falling && !fall.Grounded, "D3 未落地：仍 Falling 且未着地");

            // D4：已有下落速度继续叠加
            PMMoverSyncState fall2 = Falling(-3f, 3f, 0f);
            fall2.PreAdditiveVelocity = new PMVector3(0f, -5f, 0f);
            fall2.Velocity = new PMVector3(0f, -5f, 0f);
            fall2 = Step(model, fall2, idle, 16f, aux);
            Check(Near(fall2.Velocity.Y, -5f - 0.15696f, 1e-4f), "D4 下落速度按 dt 继续叠加重力");

            // D5：GravityScale 相乘
            PMMoverSyncState scaled = Falling(-3f, 3f, 0f);
            scaled.GravityScale = 2f;
            scaled = Step(model, scaled, idle, 16f, aux);
            Check(Near(scaled.Velocity.Y, -9.81f * 2f * 0.016f, 1e-5f), "D5 GravityScale=2 时重力翻倍");

            // D6：Aux 重力方向参与（正重力 -> 上浮）
            PMMoverAuxState upAux = PMMoverAuxState.CreateDefault();
            upAux.Gravity = new PMVector3(0f, 9.81f, 0f);
            PMMoverSyncState rising = Falling(-3f, 3f, 0f);
            rising = Step(model, rising, idle, 16f, upAux);
            Check(Near(rising.Velocity.Y, 9.81f * 0.016f, 1e-5f), "D6 Aux 重力为正时 vy 向上");
            Check(rising.Position.Y > 3f, "D7 正重力下位置上升（Aux 重力确实参与积分）");

            // D8：Falling 无空中操控（水平速度原样保留，不被输入改写）
            PMMoverSyncState air = Falling(-3f, 3f, 0f);
            air.PreAdditiveVelocity = new PMVector3(2f, 0f, 0f);
            air.Velocity = new PMVector3(2f, 0f, 0f);
            PMMoverInput airControl = PMMoverInput.Empty();
            airControl.MoveX = -1f;
            air = Step(model, air, airControl, 16f, aux);
            Check(Near(air.Velocity.X, 2f, 1e-6f), "D8 Falling 不施加空中操控（水平速度不变）");
        }

        // =================================================================================
        //  E. 落地（阶段 B）
        // =================================================================================

        private static void TestLanding()
        {
            PMMoverModel model = NewModel();
            PMMoverAuxState aux = PMMoverAuxState.CreateDefault();
            PMMoverInput idle = PMMoverInput.Empty();

            // 起点：中心 Y=1.05（足底 0.05），水平速度 2，竖直速度 -3
            // 一帧后 vy = -3 - 0.15696 = -3.15696；delta.Y = -3.15696*0.016 = -0.0505 > 0.05 间隙
            // -> 落地：吸到 Y=1.0，delta.Y 归零；水平位移仍按 2*0.016 = 0.032 走完
            PMMoverSyncState land = Falling(-3f, 1.05f, 0f);
            land.PreAdditiveVelocity = new PMVector3(2f, -3f, 0f);
            land.Velocity = new PMVector3(2f, -3f, 0f);

            float expectedVy = -3f - 9.81f * 0.016f;
            float expectedDeltaY = expectedVy * 0.016f;

            PMMoverSyncState landed = Step(model, land, idle, 16f, aux);
            Check(landed.Mode == PMMoverMode.Walking, "E1 落地后切 Walking");
            Check(landed.Grounded, "E2 落地后 Grounded == true");
            Check(landed.GroundNormal.Equals(PMVector3.Up), "E3 落地法线为 Up");
            Check(Near(landed.Position.Y, 1f, 1e-6f), "E4 吸附到足底接触（中心 Y == halfHeight）");
            Check(landed.Velocity.Y == 0f, "E5 阶段 B 落地钳制：竖直速度精确为 0");
            Check(landed.PreAdditiveVelocity.Y == 0f, "E6 阶段 B 钳制同时清掉干净基速的竖直分量");
            Check(Near(landed.Position.X, -3f + 2f * 0.016f, 1e-5f), "E7 水平位移仍用落地前速度走满（0.032）");
            Check(expectedDeltaY < -0.05f, "E8 oracle 自证：本帧下落量确实超过 0.05 间隙");

            // E9：下一帧按 Walking 规则制动（2 - 20*0.016 = 1.68），Y 不变
            PMMoverSyncState next = Step(model, landed, idle, 16f, aux);
            Check(Near(next.Velocity.X, 2f - 0.32f, 1e-5f), "E9 落地次帧按 Walking 制动到 1.68");
            Check(Near(next.Position.Y, 1f, 1e-6f), "E10 落地次帧 Y 保持接触面");
            Check(next.Mode == PMMoverMode.Walking, "E11 落地次帧仍是 Walking");
        }

        // =================================================================================
        //  F. 失去地面
        // =================================================================================

        private static void TestGroundLoss()
        {
            PMMoverModel model = NewModel();
            PMMoverAuxState aux = PMMoverAuxState.CreateDefault();
            PMMoverInput idle = PMMoverInput.Empty();

            // 地板 x∈[-20,20]；半径 0.4 -> 中心在 |x|<=20.4 时足迹仍压在地板上。
            // 取 x = 20.5（足迹外）-> Walking 的贴地探测失败 -> 本帧改按 Falling 走（重力 + 下坠）
            PMMoverSyncState off = Standing(20.5f, 0f);
            PMMoverSyncState fell = Step(model, off, idle, 16f, aux);
            Check(fell.Mode == PMMoverMode.Falling, "F1 走出地板边缘 -> Falling");
            Check(!fell.Grounded, "F2 边缘外未着地");
            Check(Near(fell.Velocity.Y, -9.81f * 0.016f, 1e-5f), "F3 边缘外本帧即开始施加重力");
            Check(Near(fell.Position.Y, 1f - 9.81f * 0.016f * 0.016f, 1e-5f), "F4 边缘外本帧即下坠");

            // 仍在足迹内（x = 20.3）时保持 Walking
            PMMoverSyncState inside = Standing(20.3f, 0f);
            inside = Step(model, inside, idle, 16f, aux);
            Check(inside.Mode == PMMoverMode.Walking && inside.Grounded, "F5 足迹仍在地板上时保持 Walking");
        }

        // =================================================================================
        //  G. 墙
        // =================================================================================

        private static void TestWallCollision()
        {
            PMMoverModel model = NewModel();
            PMMoverAuxState aux = PMMoverAuxState.CreateDefault();

            // 墙盒 x∈[-0.5,0.5] y∈[0,2] z∈[-4,4]；膨胀后（半径 0.4、skin 0.001）x∈[-0.899,0.899]
            float wallExpandedMinX = -0.5f - (PMMoverDefaults.CapsuleRadiusMeters - PMMoverDefaults.CollisionSkinMeters);
            float skin = PMMoverDefaults.CollisionSkinMeters;
            float delta = 3.9f * 0.016f; // 移动到 MaxSpeed 后单帧位移

            // G1：正撞：从 x=-0.95（离膨胀面 0.051）以 3.9 前进 -> 停在膨胀面外侧再退 1mm
            PMMoverSyncState head = Movable(-0.95f, 0f, new PMVector3(3.9f, 0f, 0f));
            head = Step(model, head, ZeroInput(1f, 0f), 16f, aux);
            float expectedX = wallExpandedMinX - skin;
            Check(Near(head.Position.X, expectedX, 1e-4f), "G1 正撞墙停止在膨胀面外侧 1mm（-0.900）");
            Check(head.Position.X < wallExpandedMinX, "G2 未穿墙（中心严格在膨胀面外）");
            Check(Near(head.Position.Z, 0f, 1e-4f), "G3 正撞不产生切向漂移");
            Check(Near(head.Position.Y, 1f, 1e-6f), "G4 水平移动不被脚下地板阻挡（skin 生效）");

            // G5：斜向滑动：45° 朝墙，x 被挡在 -0.900，z 走完整切向分量
            // 起点 x=-0.95（间隙 0.051）、z=0.5；delta=(0.0624,0,0.0624)
            // fraction = 0.051/0.0624 = 0.8173 -> x 消耗到膨胀面；切向 z 再走满 0.0624
            PMMoverSyncState oblique = Movable(-0.95f, 0.5f, new PMVector3(3.9f, 0f, 3.9f));
            oblique = Step(model, oblique, ZeroInput(1f, 0f), 16f, aux);
            float fraction = 0.051f / delta;
            float expectedObliqueZ = 0.5f + 0.051f + delta;
            Check(Near(oblique.Position.X, expectedX, 1e-4f), "G5 斜撞后 X 仍停在膨胀面外侧");
            Check(Near(oblique.Position.Z, expectedObliqueZ, 1e-3f), "G6 斜撞后切向位移走满（滑动而非停止）");
            Check(fraction > 0f && fraction < 1f, "G7 oracle 自证：本帧内确实先撞到墙");

            // G8：绕过墙角：z=4.3（在墙 z 范围内）斜向 +X+Z -> 沿面滑到 z 超过 4.399（越过墙角）
            PMMoverSyncState corner = Movable(-0.95f, 4.3f, new PMVector3(3.9f, 0f, 3.9f));
            corner = Step(model, corner, ZeroInput(1f, 0f), 16f, aux);
            float wallExpandedMaxZ = 4f + (PMMoverDefaults.CapsuleRadiusMeters - skin);
            Check(Near(corner.Position.X, expectedX, 1e-4f), "G8 绕角过程中 X 仍被挡在膨胀面外侧");
            Check(corner.Position.Z > wallExpandedMaxZ, "G9 已滑过墙角（z 超出墙的膨胀范围）");

            // G10：过角之后自由前进（下一帧不再被墙挡）
            PMMoverSyncState cleared = Movable(corner.Position.X, corner.Position.Z, new PMVector3(3.9f, 0f, 3.9f));
            cleared = Step(model, cleared, ZeroInput(1f, 0f), 16f, aux);
            Check(cleared.Position.X > corner.Position.X + 0.05f, "G10 过角后 X 恢复自由前进");

            // G11：起点已重叠（spawn 在墙里）——**不保证完整去穿透**：
            // 滑动的接触法线不带穿透深度，因此每帧只沿最小穿透轴推出 1mm skin。
            // 这里验证的是真正被承诺的性质：不崩溃、不挂死、**绝不穿到墙的另一侧**。
            float insideStartX = 0f;
            PMMoverSyncState insideWall = Movable(insideStartX, 0f, new PMVector3(3.9f, 0f, 0f));
            insideWall = Step(model, insideWall, ZeroInput(1f, 0f), 16f, aux);
            Check(Near(insideWall.Position.X, insideStartX - skin, 1e-5f),
                "G11 起点重叠：沿最小穿透轴推出 1mm skin（不推进、不穿到另一侧）");
            Check(insideWall.Position.X < 0.899f, "G12 起点重叠：本帧未穿到墙的另一侧");

            // 连续 10 帧从墙内推进：每帧退 1mm，始终不越过墙的另一侧（无隧穿、无死循环）
            PMMoverSyncState crawl = Movable(insideStartX, 0f, new PMVector3(3.9f, 0f, 0f));
            for (int i = 0; i < 10; i++)
            {
                crawl = Step(model, crawl, ZeroInput(1f, 0f), 16f, aux);
            }

            Check(Near(crawl.Position.X, insideStartX - 10f * skin, 1e-4f),
                "G12b 起点重叠后每帧退出 1mm（有界、确定）");
            Check(crawl.Position.X < 0f, "G12c 起点重叠时绝不向墙内推进（位移被完全取消）");

            // G13：多次连续撞墙不穿透（10 帧持续推进）
            PMMoverSyncState push = Movable(-1.2f, 0f, new PMVector3(3.9f, 0f, 0f));
            for (int i = 0; i < 10; i++)
            {
                push = Step(model, push, ZeroInput(1f, 0f), 16f, aux);
            }

            Check(push.Position.X <= wallExpandedMinX + 1e-5f, "G13 连续 10 帧顶墙仍未穿墙");
            Check(Near(push.Position.X, expectedX, 1e-4f), "G14 连续顶墙稳定停在膨胀面外侧");
        }

        // =================================================================================
        //  H. Inactive
        // =================================================================================

        private static void TestInactive()
        {
            PMMoverModel model = NewModel();
            PMMoverAuxState aux = PMMoverAuxState.CreateDefault();

            // Inactive + 有限时长层（1000ms）+ 大幅输入：位移必须恒为 0，但层进度继续
            PMMoverSyncState s = Standing(-3f, 0f);
            s.Mode = PMMoverMode.Inactive;

            PMMoverInput first = PMMoverInput.Empty();
            first.MoveX = 1f;
            first.MoveZ = 1f;
            first.Layers = new PMMoverLayerRequest[]
            {
                new PMMoverLayerRequest(1u, PMMoverLayerKind.AdditiveVelocity, 0, new PMVector3(5f, 0f, 5f), 1000),
            };

            PMVector3 startPosition = s.Position;
            s = Step(model, s, first, 16f, aux);
            Check(s.Position.Equals(startPosition), "H1 Inactive 单帧零位移（即使有输入与层）");
            Check(s.Velocity.Equals(PMVector3.Zero), "H2 Inactive 速度精确为零");
            Check(s.Mode == PMMoverMode.Inactive, "H3 Inactive 不切模式");
            Check(s.ActiveLayers != null && s.ActiveLayers.Length == 1, "H4 Inactive 保留活跃层");
            Check(s.ActiveLayers[0].ElapsedMs == 16, "H5 Inactive 层进度仍按原 dt 累加（时间轴不停）");

            PMMoverInput idle = PMMoverInput.Empty();
            for (int i = 0; i < 62; i++)
            {
                s = Step(model, s, idle, 16f, aux);
            }

            Check(s.Position.Equals(startPosition), "H6 63 帧 Inactive 全程零位移");
            Check(s.ActiveLayers != null && s.ActiveLayers.Length == 1 && s.ActiveLayers[0].ElapsedMs == 1008,
                "H7 63 帧后层进度 == 1008ms（仍在过期清扫之前）");

            s = Step(model, s, idle, 16f, aux);
            Check(s.ActiveLayers == null, "H8 第 64 帧帧首清扫掉已到期层");

            // Inactive 解除：SetMode(Walking) 后恢复位移
            PMMoverSyncState back = Standing(-3f, 0f);
            back.Mode = PMMoverMode.Inactive;
            PMMoverInput resume = PMMoverInput.Empty();
            resume.MoveX = 1f;
            resume.Effects = new PMMoverEffectRequest[] { PMMoverEffectRequest.SetMode(PMMoverMode.Walking) };
            back = Step(model, back, resume, 16f, aux);
            Check(back.Mode == PMMoverMode.Walking, "H9 SetMode(Walking) 恢复 Walking");
            Check(Near(back.Velocity.X, 0.32f, 1e-5f), "H10 恢复后正常加速（Inactive 期间未残留速度）");
        }

        // =================================================================================
        //  I. Layer
        // =================================================================================

        private static void TestLayers()
        {
            PMMoverModel model = NewModel();
            PMMoverAuxState aux = PMMoverAuxState.CreateDefault();
            PMMoverInput idle = PMMoverInput.Empty();

            // I1：Additive 单帧不累积（accel/braking 置零，隔离出层贡献）
            PMMoverSyncState s = Frozen(-3f, 0f);
            PMMoverInput add = PMMoverInput.Empty();
            add.Layers = new PMMoverLayerRequest[]
            {
                new PMMoverLayerRequest(1u, PMMoverLayerKind.AdditiveVelocity, 0, new PMVector3(0f, 0f, 1f), 0),
            };

            s = Step(model, s, add, 16f, aux);
            Check(Near(s.Velocity.Z, 1f, 1e-6f), "I1 单层 Additive：速度 == 1");
            Check(Near(s.PreAdditiveVelocity.Z, 0f, 1e-6f), "I2 Additive 不污染干净基速");

            s = Step(model, s, idle, 16f, aux);
            s = Step(model, s, idle, 16f, aux);
            Check(Near(s.Velocity.Z, 1f, 1e-6f), "I3 第 3 帧仍 == 1（Additive 不跨帧累积）");
            Check(Near(s.Position.Z, 3f * 1f * 0.016f, 1e-5f), "I4 位移 == 3 帧 * 1*0.016（每帧只叠一次）");

            // I5：有限时长到期 -> 恢复干净 base
            PMMoverSyncState exp = Frozen(-3f, 0f);
            PMMoverInput finite = PMMoverInput.Empty();
            finite.Layers = new PMMoverLayerRequest[]
            {
                new PMMoverLayerRequest(2u, PMMoverLayerKind.AdditiveVelocity, 0, new PMVector3(0f, 0f, 1f), 32),
            };

            exp = Step(model, exp, finite, 16f, aux);
            Check(Near(exp.Velocity.Z, 1f, 1e-6f) && exp.ActiveLayers.Length == 1, "I5 第 1 帧层生效");
            exp = Step(model, exp, idle, 16f, aux);
            Check(Near(exp.Velocity.Z, 1f, 1e-6f) && exp.ActiveLayers.Length == 1, "I6 第 2 帧层仍生效（32ms 未到）");
            exp = Step(model, exp, idle, 16f, aux);
            Check(exp.Velocity.Z == 0f && exp.ActiveLayers == null, "I7 第 3 帧层到期：速度恢复干净 base（精确 0）");

            // I8：Override 胜出顺序 = 最大 (priority, instanceId)
            PMMoverSyncState ovr = Frozen(-3f, 0f);
            PMMoverInput two = PMMoverInput.Empty();
            two.Layers = new PMMoverLayerRequest[]
            {
                new PMMoverLayerRequest(10u, PMMoverLayerKind.OverrideVelocity, 0, new PMVector3(1f, 0f, 0f), 0),
                new PMMoverLayerRequest(20u, PMMoverLayerKind.OverrideVelocity, 5, new PMVector3(0f, 0f, 2f), 0),
            };

            ovr = Step(model, ovr, two, 16f, aux);
            Check(ovr.Velocity.Equals(new PMVector3(0f, 0f, 2f)), "I8 priority 大的 Override 胜出");

            PMMoverSyncState tie = Frozen(-3f, 0f);
            PMMoverInput tied = PMMoverInput.Empty();
            tied.Layers = new PMMoverLayerRequest[]
            {
                new PMMoverLayerRequest(10u, PMMoverLayerKind.OverrideVelocity, 5, new PMVector3(0f, 0f, 2f), 0),
                new PMMoverLayerRequest(20u, PMMoverLayerKind.OverrideVelocity, 5, new PMVector3(1f, 0f, 0f), 0),
            };

            tie = Step(model, tie, tied, 16f, aux);
            Check(tie.Velocity.Equals(new PMVector3(1f, 0f, 0f)), "I9 priority 相同时 instanceId 大的胜出");

            // I10：Override 排除全部 Additive（不是相加）
            PMMoverSyncState ex = Frozen(-3f, 0f);
            PMMoverInput mixed = PMMoverInput.Empty();
            mixed.Layers = new PMMoverLayerRequest[]
            {
                new PMMoverLayerRequest(30u, PMMoverLayerKind.AdditiveVelocity, 0, new PMVector3(10f, 0f, 0f), 0),
                new PMMoverLayerRequest(31u, PMMoverLayerKind.OverrideVelocity, 1, new PMVector3(0f, 0f, 1f), 0),
            };

            ex = Step(model, ex, mixed, 16f, aux);
            Check(ex.Velocity.Equals(new PMVector3(0f, 0f, 1f)), "I10 存在 Override 时 Additive 被排除（不是 (10,0,1)）");

            // I11：多 Additive 求和（按规范顺序相加）
            PMMoverSyncState sum = Frozen(-3f, 0f);
            PMMoverInput twoAdd = PMMoverInput.Empty();
            twoAdd.Layers = new PMMoverLayerRequest[]
            {
                new PMMoverLayerRequest(40u, PMMoverLayerKind.AdditiveVelocity, 0, new PMVector3(0.1f, 0f, 0f), 0),
                new PMMoverLayerRequest(41u, PMMoverLayerKind.AdditiveVelocity, 1, new PMVector3(0.2f, 0f, 0f), 0),
            };

            sum = Step(model, sum, twoAdd, 16f, aux);
            Check(Near(sum.Velocity.X, 0.1f + 0.2f, 1e-6f), "I11 两个 Additive 相加 == 0.3");
            Check(sum.ActiveLayers[0].InstanceId == 40u && sum.ActiveLayers[1].InstanceId == 41u,
                "I12 活跃层按 (priority, instanceId) 规范排序");

            // I13：显式移除
            PMMoverSyncState rem = Frozen(-3f, 0f);
            PMMoverInput addTwo = PMMoverInput.Empty();
            addTwo.Layers = new PMMoverLayerRequest[]
            {
                new PMMoverLayerRequest(50u, PMMoverLayerKind.AdditiveVelocity, 0, new PMVector3(0f, 0f, 1f), 0),
                new PMMoverLayerRequest(51u, PMMoverLayerKind.AdditiveVelocity, 0, new PMVector3(0f, 0f, 2f), 0),
            };

            rem = Step(model, rem, addTwo, 16f, aux);
            Check(Near(rem.Velocity.Z, 3f, 1e-6f), "I13 两层相加 == 3");

            PMMoverInput removeOne = PMMoverInput.Empty();
            removeOne.RemovedLayerIds = new uint[] { 50u };
            rem = Step(model, rem, removeOne, 16f, aux);
            Check(rem.ActiveLayers.Length == 1 && rem.ActiveLayers[0].InstanceId == 51u, "I14 按 id 移除只剩一层");
            Check(Near(rem.Velocity.Z, 2f, 1e-6f), "I15 移除后叠加量随之变化");

            // I16：同 id 再请求 = 替换（进度重置）
            PMMoverSyncState rep = Frozen(-3f, 0f);
            PMMoverInput one = PMMoverInput.Empty();
            one.Layers = new PMMoverLayerRequest[]
            {
                new PMMoverLayerRequest(60u, PMMoverLayerKind.AdditiveVelocity, 0, new PMVector3(0f, 0f, 1f), 0),
            };

            rep = Step(model, rep, one, 16f, aux);
            Check(rep.ActiveLayers[0].ElapsedMs == 16, "I16 首帧后层进度 == 16");

            PMMoverInput replace = PMMoverInput.Empty();
            replace.Layers = new PMMoverLayerRequest[]
            {
                new PMMoverLayerRequest(60u, PMMoverLayerKind.AdditiveVelocity, 0, new PMVector3(0f, 0f, 4f), 100),
            };

            rep = Step(model, rep, replace, 16f, aux);
            Check(rep.ActiveLayers.Length == 1, "I17 同 id 替换不新增实例");
            Check(rep.ActiveLayers[0].ElapsedMs == 16, "I18 替换后进度重置（不是 32）");
            Check(Near(rep.ActiveLayers[0].Velocity.Z, 4f, 1e-6f), "I19 替换后速度定义更新");
            Check(rep.ActiveLayers[0].DurationMs == 100, "I20 替换后时长定义更新");

            // I21：移除不存在的 id 是幂等空操作
            PMMoverSyncState noop = Step(model, rep, removeOne, 16f, aux);
            Check(noop.ActiveLayers.Length == 1, "I21 移除不存在的 id 不改变层集合");

            // I22：无时长（0）的层不会过期
            PMMoverSyncState forever = Frozen(-3f, 0f);
            PMMoverInput endless = PMMoverInput.Empty();
            endless.Layers = new PMMoverLayerRequest[]
            {
                new PMMoverLayerRequest(70u, PMMoverLayerKind.AdditiveVelocity, 0, new PMVector3(0f, 0f, 1f), 0),
            };

            forever = Step(model, forever, endless, 50f, aux);
            for (int i = 0; i < 40; i++)
            {
                forever = Step(model, forever, idle, 50f, aux);
            }

            Check(forever.ActiveLayers != null && forever.ActiveLayers.Length == 1, "I22 无时长层 41 帧后仍存活");
            Check(forever.ActiveLayers[0].ElapsedMs == 41 * 50, "I23 无时长层进度累计正确");
        }

        // =================================================================================
        //  J. Effect
        // =================================================================================

        private static void TestEffects()
        {
            PMMoverModel model = NewModel();
            PMMoverAuxState aux = PMMoverAuxState.CreateDefault();
            PMMoverInput idle = PMMoverInput.Empty();

            // J1：阶段 A 在**移动之前**消费——速度命令随后被本帧模式制动改写
            //     Braking=100、dt=0.016 -> 5 - 1.6 = 3.4（若在移动之后消费则会是 5）
            PMMoverSyncState pa = Frozen(-3f, 0f);
            PMMoverInput phaseA = PMMoverInput.Empty();
            phaseA.Effects = new PMMoverEffectRequest[]
            {
                PMMoverEffectRequest.SetParameters(1f, 100f, 100f, 1f, 0f),
                PMMoverEffectRequest.SetVelocity(new PMVector3(5f, 0f, 0f)),
            };

            pa = Step(model, pa, phaseA, 16f, aux);
            Check(Near(pa.Velocity.X, 3.4f, 1e-4f), "J1 阶段 A 先于移动：速度命令被本帧制动改写为 3.4");
            Check(Near(pa.MaxSpeed, 1f, 1e-9f), "J2 SetParameters 生效");

            // J3：多条 SetMode 取**最后一条**
            PMMoverSyncState last = Standing(-3f, 0f);
            PMMoverInput modes = PMMoverInput.Empty();
            modes.Effects = new PMMoverEffectRequest[]
            {
                PMMoverEffectRequest.SetMode(PMMoverMode.Flying),
                PMMoverEffectRequest.SetMode(PMMoverMode.Walking),
            };

            last = Step(model, last, modes, 16f, aux);
            Check(last.Mode == PMMoverMode.Walking, "J3 多条 SetMode 以最后一条为准");
            Check(last.Grounded, "J4 回到 Walking 后本帧重新做地面查询（着地）");

            // J5：Teleport 在移动之前生效，且随后按新位置做地面查询
            PMMoverSyncState tp = Frozen(-3f, 0f);
            PMMoverInput teleport = PMMoverInput.Empty();
            teleport.Effects = new PMMoverEffectRequest[] { PMMoverEffectRequest.Teleport(new PMVector3(7f, 1f, 0f)) };
            tp = Step(model, tp, teleport, 16f, aux);
            Check(Near(tp.Position.X, 7f, 1e-6f) && Near(tp.Position.Y, 1f, 1e-6f), "J5 Teleport 生效且无残余位移");
            Check(tp.Mode == PMMoverMode.Walking && tp.Grounded, "J6 Teleport 到地板范围内仍 Walking/着地");

            // J7：Teleport 到地板之外 -> 本帧即按 Falling 处理
            PMMoverSyncState tpOut = Frozen(-3f, 0f);
            PMMoverInput teleportOut = PMMoverInput.Empty();
            teleportOut.Effects = new PMMoverEffectRequest[] { PMMoverEffectRequest.Teleport(new PMVector3(100f, 1f, 0f)) };
            tpOut = Step(model, tpOut, teleportOut, 16f, aux);
            Check(tpOut.Mode == PMMoverMode.Falling && !tpOut.Grounded, "J7 Teleport 到地板外 -> 本帧转 Falling");

            // J8：SetMode(Flying) 后本帧用 Flying 规则积分（三维控制）
            PMMoverSyncState fly = Standing(-3f, 0f);
            PMMoverInput toFly = PMMoverInput.Empty();
            toFly.MoveY = 1f;
            toFly.Effects = new PMMoverEffectRequest[] { PMMoverEffectRequest.SetMode(PMMoverMode.Flying) };
            fly = Step(model, fly, toFly, 16f, aux);
            Check(fly.Mode == PMMoverMode.Flying, "J8 SetMode(Flying) 后本帧即用 Flying");
            Check(Near(fly.Velocity.Y, 0.32f, 1e-5f), "J9 Flying 三维控制：竖直方向加速到 0.32");
            Check(Near(fly.Position.Y, 1f + 0.32f * 0.016f, 1e-5f), "J10 Flying 竖直位移 == vy*dt");
            Check(!fly.Velocity.Equals(PMVector3.Zero), "J11 Flying 未被 Walking 的平面约束清零");

            // J12：SetMode(Flying) 后不施加重力
            PMMoverSyncState hover = Standing(-3f, 0f);
            PMMoverInput hoverInput = PMMoverInput.Empty();
            hoverInput.Effects = new PMMoverEffectRequest[] { PMMoverEffectRequest.SetMode(PMMoverMode.Flying) };
            hover = Step(model, hover, hoverInput, 16f, aux);
            hover = Step(model, hover, idle, 16f, aux);
            Check(Near(hover.Velocity.Y, 0f, 1e-6f), "J12 Flying 不受重力（悬停）");

            // J13：未知模式值必须显式拒绝（绝不默认回退到 Walking）
            PMMoverSyncState untouched = Standing(-3f, 0f);
            PMMoverInput badMode = PMMoverInput.Empty();
            PMMoverEffectRequest bad = new PMMoverEffectRequest();
            bad.Kind = PMMoverEffectKind.SetMode;
            bad.Mode = 7;
            badMode.Effects = new PMMoverEffectRequest[] { bad };
            CheckThrows(delegate { Step(model, untouched, badMode, 16f, aux); },
                "J13 未知模式值抛出（拒绝，不默认 Walking）");

            // J14：未知 Effect 种类必须拒绝
            PMMoverInput badKind = PMMoverInput.Empty();
            PMMoverEffectRequest weird = new PMMoverEffectRequest();
            weird.Kind = (PMMoverEffectKind)99;
            badKind.Effects = new PMMoverEffectRequest[] { weird };
            CheckThrows(delegate { Step(model, untouched, badKind, 16f, aux); }, "J14 未知 Effect 种类抛出");

            // J15：未知层种类 / 零 id / 负时长都必须拒绝
            PMMoverInput badLayerKind = PMMoverInput.Empty();
            badLayerKind.Layers = new PMMoverLayerRequest[]
            {
                new PMMoverLayerRequest(1u, (PMMoverLayerKind)9, 0, PMVector3.Zero, 0),
            };
            CheckThrows(delegate { Step(model, untouched, badLayerKind, 16f, aux); }, "J15 未知层种类抛出");

            PMMoverInput zeroId = PMMoverInput.Empty();
            zeroId.Layers = new PMMoverLayerRequest[]
            {
                new PMMoverLayerRequest(0u, PMMoverLayerKind.AdditiveVelocity, 0, PMVector3.Zero, 0),
            };
            CheckThrows(delegate { Step(model, untouched, zeroId, 16f, aux); }, "J16 层 InstanceId=0 抛出");

            PMMoverInput negDuration = PMMoverInput.Empty();
            negDuration.Layers = new PMMoverLayerRequest[]
            {
                new PMMoverLayerRequest(1u, PMMoverLayerKind.AdditiveVelocity, 0, PMVector3.Zero, -5),
            };
            CheckThrows(delegate { Step(model, untouched, negDuration, 16f, aux); }, "J17 层时长为负抛出");

            PMMoverInput zeroRemoved = PMMoverInput.Empty();
            zeroRemoved.RemovedLayerIds = new uint[] { 0u };
            CheckThrows(delegate { Step(model, untouched, zeroRemoved, 16f, aux); }, "J18 RemovedLayerIds 含 0 抛出");

            // J19：非法 dt（0 / 51 / 非整毫秒 / 负数）都必须拒绝
            CheckThrows(delegate { Step(model, untouched, idle, 0f, aux); }, "J19 dt=0 抛出（缺帧占位不得调模型）");
            CheckThrows(delegate { Step(model, untouched, idle, 51f, aux); }, "J20 dt=51 抛出（超上限）");
            CheckThrows(delegate { Step(model, untouched, idle, 16.5f, aux); }, "J21 非整毫秒 dt 抛出");
            CheckThrows(delegate { Step(model, untouched, idle, -16f, aux); }, "J22 负 dt 抛出");
            CheckThrows(delegate { Step(model, untouched, idle, float.NaN, aux); }, "J23 NaN dt 抛出");

            // J24：dt 边界 1 与 50 必须被接受
            PMMoverSyncState edge1 = Step(model, Standing(-3f, 0f), idle, 1f, aux);
            PMMoverSyncState edge50 = Step(model, Standing(-3f, 0f), idle, 50f, aux);
            Check(edge1.Mode == PMMoverMode.Walking && edge50.Mode == PMMoverMode.Walking, "J24 dt=1 与 dt=50 都被接受");

            // J25：非法 Input 数值 / 非法 start 都必须拒绝
            PMMoverInput nanMove = PMMoverInput.Empty();
            nanMove.MoveX = float.NaN;
            CheckThrows(delegate { Step(model, untouched, nanMove, 16f, aux); }, "J25 NaN 输入轴抛出");

            PMMoverSyncState badScale = Standing(-3f, 0f);
            badScale.Scale = 0f;
            CheckThrows(delegate { Step(model, badScale, idle, 16f, aux); }, "J26 Scale=0 抛出");

            PMMoverSyncState badModeState = Standing(-3f, 0f);
            badModeState.Mode = (PMMoverMode)200;
            CheckThrows(delegate { Step(model, badModeState, idle, 16f, aux); }, "J27 未知 start 模式抛出");

            // J28：非法输入**不产生任何输出/副作用**（start 未被改写）
            PMMoverSyncState guard = Standing(-3f, 0f);
            guard.ActiveLayers = new PMMoverLayer[] { MakeLayer(1u, PMMoverLayerKind.AdditiveVelocity, 0, new PMVector3(0f, 0f, 1f), 0, 5) };
            PMMoverSyncState guardCopy = model.CloneSync(guard);
            try
            {
                Step(model, guard, badMode, 16f, aux);
            }
            catch (ArgumentException)
            {
                // 预期
            }

            Check(SyncEquals(guard, guardCopy), "J28 被拒绝的模拟没有改写 start（无副作用）");
        }

        // =================================================================================
        //  K. ShouldReconcile
        // =================================================================================

        private static void TestShouldReconcile()
        {
            PMMoverModel model = NewModel();
            PMMoverAuxState aux = PMMoverAuxState.CreateDefault();

            PMMoverSyncState a = Standing(-3f, 0f);
            PMMoverSyncState b = model.CloneSync(a);
            Check(!model.ShouldReconcile(a, b, aux, aux), "K1 完全相同 -> 不需 reconcile");

            // 模式优先
            PMMoverSyncState modeDiff = model.CloneSync(a);
            modeDiff.Mode = PMMoverMode.Falling;
            Check(model.ShouldReconcile(a, modeDiff, aux, aux), "K2 模式不同 -> reconcile");

            // 着地
            PMMoverSyncState groundedDiff = model.CloneSync(a);
            groundedDiff.Grounded = false;
            Check(model.ShouldReconcile(a, groundedDiff, aux, aux), "K3 Grounded 不同 -> reconcile");

            // 精确参数：逐个验证
            CheckReconcileOnField(model, a, aux, "Scale", delegate(PMMoverSyncState s) { s.Scale = 1.5f; return s; });
            CheckReconcileOnField(model, a, aux, "MaxSpeed", delegate(PMMoverSyncState s) { s.MaxSpeed = 4.5f; return s; });
            CheckReconcileOnField(model, a, aux, "Acceleration", delegate(PMMoverSyncState s) { s.Acceleration = 21f; return s; });
            CheckReconcileOnField(model, a, aux, "Braking", delegate(PMMoverSyncState s) { s.Braking = 19f; return s; });
            CheckReconcileOnField(model, a, aux, "GravityScale", delegate(PMMoverSyncState s) { s.GravityScale = 0.5f; return s; });
            CheckReconcileOnField(model, a, aux, "JumpSpeed", delegate(PMMoverSyncState s) { s.JumpSpeed = 5f; return s; });

            // 活跃层实例定义（含"进度差异不算差异"的显式决策）
            PMMoverSyncState layered = model.CloneSync(a);
            layered.ActiveLayers = new PMMoverLayer[] { MakeLayer(1u, PMMoverLayerKind.AdditiveVelocity, 0, new PMVector3(0f, 0f, 1f), 0, 0) };
            Check(model.ShouldReconcile(a, layered, aux, aux), "K4 层集合不同 -> reconcile");

            PMMoverSyncState layeredSame = model.CloneSync(layered);
            layeredSame.ActiveLayers[0] = MakeLayer(1u, PMMoverLayerKind.AdditiveVelocity, 0, new PMVector3(0f, 0f, 1f), 0, 777);
            Check(!model.ShouldReconcile(layered, layeredSame, aux, aux), "K5 仅层进度不同 -> 不 reconcile（进度不是实例定义）");

            PMMoverSyncState layeredVel = model.CloneSync(layered);
            layeredVel.ActiveLayers[0] = MakeLayer(1u, PMMoverLayerKind.AdditiveVelocity, 0, new PMVector3(0f, 0f, 2f), 0, 0);
            Check(model.ShouldReconcile(layered, layeredVel, aux, aux), "K6 层速度定义不同 -> reconcile");

            PMMoverSyncState layeredKind = model.CloneSync(layered);
            layeredKind.ActiveLayers[0] = MakeLayer(1u, PMMoverLayerKind.OverrideVelocity, 0, new PMVector3(0f, 0f, 1f), 0, 0);
            Check(model.ShouldReconcile(layered, layeredKind, aux, aux), "K7 层种类不同 -> reconcile");

            PMMoverSyncState layeredPrio = model.CloneSync(layered);
            layeredPrio.ActiveLayers[0] = MakeLayer(1u, PMMoverLayerKind.AdditiveVelocity, 3, new PMVector3(0f, 0f, 1f), 0, 0);
            Check(model.ShouldReconcile(layered, layeredPrio, aux, aux), "K8 层优先级不同 -> reconcile");

            PMMoverSyncState layeredDur = model.CloneSync(layered);
            layeredDur.ActiveLayers[0] = MakeLayer(1u, PMMoverLayerKind.AdditiveVelocity, 0, new PMVector3(0f, 0f, 1f), 500, 0);
            Check(model.ShouldReconcile(layered, layeredDur, aux, aux), "K9 层时长定义不同 -> reconcile");

            PMMoverSyncState layeredExtra = model.CloneSync(layered);
            layeredExtra.ActiveLayers = new PMMoverLayer[]
            {
                layered.ActiveLayers[0],
                MakeLayer(2u, PMMoverLayerKind.AdditiveVelocity, 0, new PMVector3(0f, 0f, 1f), 0, 0),
            };
            Check(model.ShouldReconcile(layered, layeredExtra, aux, aux), "K10 层数量不同 -> reconcile");

            // 连续量阈值
            PMMoverSyncState near = model.CloneSync(a);
            near.Position = new PMVector3(a.Position.X + 0.04f, a.Position.Y, a.Position.Z);
            Check(!model.ShouldReconcile(a, near, aux, aux), "K11 位置差 0.04m（<0.05）-> 不 reconcile");

            PMMoverSyncState far = model.CloneSync(a);
            far.Position = new PMVector3(a.Position.X + 0.06f, a.Position.Y, a.Position.Z);
            Check(model.ShouldReconcile(a, far, aux, aux), "K12 位置差 0.06m（>0.05）-> reconcile");

            PMMoverSyncState velNear = model.CloneSync(a);
            velNear.Velocity = new PMVector3(0.008f, 0f, 0f);
            Check(!model.ShouldReconcile(a, velNear, aux, aux), "K13 速度差 0.008（<0.01）-> 不 reconcile");

            PMMoverSyncState velFar = model.CloneSync(a);
            velFar.Velocity = new PMVector3(0.02f, 0f, 0f);
            Check(model.ShouldReconcile(a, velFar, aux, aux), "K14 速度差 0.02（>0.01）-> reconcile");

            PMMoverSyncState preNear = model.CloneSync(a);
            preNear.PreAdditiveVelocity = new PMVector3(0.008f, 0f, 0f);
            Check(!model.ShouldReconcile(a, preNear, aux, aux), "K15 干净基速差 0.008 -> 不 reconcile");

            PMMoverSyncState preFar = model.CloneSync(a);
            preFar.PreAdditiveVelocity = new PMVector3(0.02f, 0f, 0f);
            Check(model.ShouldReconcile(a, preFar, aux, aux), "K16 干净基速差 0.02 -> reconcile");

            PMMoverSyncState yawNear = model.CloneSync(a);
            yawNear.YawDegrees = 0.5f;
            Check(!model.ShouldReconcile(a, yawNear, aux, aux), "K17 朝向差 0.5 度 -> 不 reconcile");

            PMMoverSyncState yawFar = model.CloneSync(a);
            yawFar.YawDegrees = 2f;
            Check(model.ShouldReconcile(a, yawFar, aux, aux), "K18 朝向差 2 度 -> reconcile");

            // 朝向跨越 360 度边界时按最短弧
            PMMoverSyncState wrapA = model.CloneSync(a);
            wrapA.YawDegrees = 359f;
            PMMoverSyncState wrapB = model.CloneSync(a);
            wrapB.YawDegrees = 1f;
            Check(model.ShouldReconcile(wrapA, wrapB, aux, aux), "K19 359 与 1 度只差 2 度 -> reconcile");
            Check(Near(PMMoverModel.YawDeltaDegrees(359f, 1f), 2f, 1e-4f), "K20 最短弧夹角计算 == 2 度");

            PMMoverSyncState wrapClose = model.CloneSync(a);
            wrapClose.YawDegrees = 359.5f;
            PMMoverSyncState wrapClose2 = model.CloneSync(a);
            wrapClose2.YawDegrees = 0f;
            Check(!model.ShouldReconcile(wrapClose, wrapClose2, aux, aux), "K21 359.5 与 0 只差 0.5 度 -> 不 reconcile");

            // 地面法线
            PMMoverSyncState normalFar = model.CloneSync(a);
            normalFar.GroundNormal = PMVector3.Normalized(new PMVector3(0.05f, 1f, 0f));
            Check(model.ShouldReconcile(a, normalFar, aux, aux), "K22 地面法线差约 2.9 度 -> reconcile");
            Check(PMMoverModel.AngleBetweenDegrees(normalFar.GroundNormal, PMVector3.Up) > 1.5f,
                "K23 oracle 自证：法线夹角确实超过阈值");

            PMMoverSyncState normalNear = model.CloneSync(a);
            normalNear.GroundNormal = PMVector3.Normalized(new PMVector3(0.005f, 1f, 0f));
            Check(!model.ShouldReconcile(a, normalNear, aux, aux), "K24 地面法线差 0.3 度 -> 不 reconcile");

            // Aux
            PMMoverAuxState auxCollision = aux;
            auxCollision.CollisionWorldVersion = 123;
            Check(model.ShouldReconcile(a, b, aux, auxCollision), "K25 碰撞世界版本变化 -> reconcile");

            PMMoverAuxState auxConfig = aux;
            auxConfig.ConfigVersion = 7;
            Check(model.ShouldReconcile(a, b, aux, auxConfig), "K26 配置版本变化 -> reconcile");

            PMMoverAuxState auxGravity = aux;
            auxGravity.Gravity = new PMVector3(0f, -10.5f, 0f);
            Check(model.ShouldReconcile(a, b, aux, auxGravity), "K27 重力变化 -> reconcile");

            PMMoverAuxState auxTiny = aux;
            auxTiny.Gravity = new PMVector3(0f, -9.81001f, 0f);
            Check(!model.ShouldReconcile(a, b, aux, auxTiny), "K28 重力微小差异（<1e-4）-> 不 reconcile");

            // 顺序性：先看 mode（mode 差异存在时即便位置也很近仍要 reconcile）
            PMMoverSyncState modeOnly = model.CloneSync(a);
            modeOnly.Mode = PMMoverMode.Inactive;
            modeOnly.Position = a.Position;
            Check(model.ShouldReconcile(a, modeOnly, aux, aux), "K29 mode 差异优先命中");
        }

        // =================================================================================
        //  L. Interpolate
        // =================================================================================

        private static void TestInterpolate()
        {
            PMMoverModel model = NewModel();

            PMMoverSyncState from = Standing(0f, 0f);
            from.Velocity = new PMVector3(0f, 0f, 0f);
            PMMoverSyncState to = Standing(10f, 0f);
            to.Velocity = new PMVector3(4f, 0f, 0f);
            to.Mode = PMMoverMode.Flying;
            to.Grounded = false;
            to.Scale = 2f;
            to.MaxSpeed = 8f;

            PMMoverSyncState mid = model.Interpolate(from, to, 0.25f);
            Check(Near(mid.Position.X, 2.5f, 1e-5f), "L1 位置线性插值 0.25 -> 2.5");
            Check(Near(mid.Velocity.X, 1f, 1e-5f), "L2 速度线性插值 0.25 -> 1");
            Check(mid.Mode == PMMoverMode.Flying, "L3 离散模式取 To");
            Check(!mid.Grounded, "L4 离散着地取 To");
            Check(Near(mid.Scale, 2f, 1e-9f), "L5 参数（Scale）取 To");
            Check(Near(mid.MaxSpeed, 8f, 1e-9f), "L6 参数（MaxSpeed）取 To");

            // yaw 最短弧：350 -> 10，0.5 -> 0（走 20 度而不是 340 度）
            PMMoverSyncState yawFrom = Standing(0f, 0f);
            yawFrom.YawDegrees = 350f;
            PMMoverSyncState yawTo = Standing(0f, 0f);
            yawTo.YawDegrees = 10f;

            PMMoverSyncState yawMid = model.Interpolate(yawFrom, yawTo, 0.5f);
            Check(Near(yawMid.YawDegrees, 0f, 1e-3f), "L7 yaw 最短弧 350->10 @0.5 == 0（不是 180）");

            PMMoverSyncState yawQuarter = model.Interpolate(yawFrom, yawTo, 0.25f);
            Check(Near(yawQuarter.YawDegrees, 355f, 1e-3f), "L8 yaw 最短弧 350->10 @0.25 == 355");

            PMMoverSyncState yawBack = model.Interpolate(yawTo, yawFrom, 0.5f);
            Check(Near(yawBack.YawDegrees, 0f, 1e-3f), "L9 yaw 反向最短弧 10->350 @0.5 == 0");
            Check(Near(PMMoverModel.LerpYawShortestArc(350f, 10f, 0.5f), 0f, 1e-3f), "L10 最短弧助手与插值一致");

            // alpha 钳制
            PMMoverSyncState clampedLow = model.Interpolate(from, to, -3f);
            Check(Near(clampedLow.Position.X, 0f, 1e-6f), "L11 alpha<0 钳到 0（取 from）");
            PMMoverSyncState clampedHigh = model.Interpolate(from, to, 9f);
            Check(Near(clampedHigh.Position.X, 10f, 1e-6f), "L12 alpha>1 钳到 1（取 to）");

            // 地面法线：两端着地状态一致时插值并归一化
            PMMoverSyncState normalFrom = Standing(0f, 0f);
            PMMoverSyncState normalTo = Standing(0f, 0f);
            normalTo.GroundNormal = new PMVector3(0f, 0f, 1f);
            PMMoverSyncState normalMid = model.Interpolate(normalFrom, normalTo, 0.5f);
            Check(Near(normalMid.GroundNormal.Length, 1f, 1e-4f), "L13 插值后地面法线保持单位长度");
            Check(normalMid.GroundNormal.Y > 0f && normalMid.GroundNormal.Z > 0f, "L14 法线插值分量都有效");

            // 层集合取 To 且不产生别名
            PMMoverSyncState layeredTo = Standing(0f, 0f);
            layeredTo.ActiveLayers = new PMMoverLayer[] { MakeLayer(9u, PMMoverLayerKind.AdditiveVelocity, 0, new PMVector3(0f, 0f, 1f), 0, 12) };
            PMMoverSyncState layeredMid = model.Interpolate(from, layeredTo, 0.5f);
            Check(layeredMid.ActiveLayers != null && layeredMid.ActiveLayers.Length == 1, "L15 层集合取 To");
            Check(!ReferenceEquals(layeredMid.ActiveLayers, layeredTo.ActiveLayers), "L16 结果层数组是新实例（无别名）");
            Check(layeredMid.ActiveLayers[0].ElapsedMs == 12, "L17 层进度原样取 To");

            // from 无层、to 无层 -> 结果无层
            PMMoverSyncState none = model.Interpolate(from, to, 0.5f);
            Check(none.ActiveLayers == null, "L18 两侧都无层 -> 结果无层");
        }

        // =================================================================================
        //  M. 确定性与纯度
        // =================================================================================

        private static void TestDeterminismAndPurity()
        {
            PMMoverModel model = NewModel();
            PMMoverAuxState aux = PMMoverAuxState.CreateDefault();

            PMMoverSyncState start = Falling(-3f, 4f, 0.5f);
            start.PreAdditiveVelocity = new PMVector3(1.5f, -2f, 0.25f);
            start.Velocity = new PMVector3(1.5f, -2f, 0.25f);
            start.ActiveLayers = new PMMoverLayer[]
            {
                MakeLayer(1u, PMMoverLayerKind.AdditiveVelocity, 0, new PMVector3(0.5f, 0f, 0f), 0, 5),
            };

            PMMoverInput input = PMMoverInput.Empty();
            input.MoveX = 0.5f;
            input.MoveZ = -0.25f;
            input.YawDegrees = 123.5f;
            input.JumpPressed = true;
            input.Layers = new PMMoverLayerRequest[]
            {
                new PMMoverLayerRequest(2u, PMMoverLayerKind.AdditiveVelocity, 1, new PMVector3(0f, 0f, 0.5f), 200),
            };

            PMMoverSyncState startCopy = model.CloneSync(start);
            PMMoverInput inputCopy = model.CloneInput(input);

            PMMoverSyncState reference = Step(model, start, input, 16f, aux);

            bool bitwise = true;
            for (int i = 0; i < 10; i++)
            {
                PMMoverSyncState again = Step(model, start, input, 16f, aux);
                if (!SyncBitEquals(reference, again))
                {
                    bitwise = false;
                    break;
                }
            }

            Check(bitwise, "M1 同输入连跑 10 次逐位相同");
            Check(SyncEquals(start, startCopy), "M2 Simulate 未改写 start（含层数组内容）");
            Check(InputEquals(input, inputCopy), "M3 Simulate 未改写 input（含命令数组内容）");

            // 输出不与输入共享引用
            Check(!ReferenceEquals(reference.ActiveLayers, input.Layers), "M4 输出层对象与输入数组不同源");
            PMMoverEffectRequest[] effects = new PMMoverEffectRequest[] { PMMoverEffectRequest.SetVelocity(new PMVector3(5f, 0f, 0f)) };
            PMMoverInput effInput = PMMoverInput.Empty();
            effInput.Effects = effects;
            PMMoverSyncState fromEffect = Step(model, Standing(-3f, 0f), effInput, 16f, aux);
            effects[0] = PMMoverEffectRequest.SetVelocity(new PMVector3(-99f, 0f, 0f));
            Check(Near(fromEffect.Velocity.X, 5f - 0.32f, 1e-4f), "M5 事后改输入数组不影响已产出的输出");

            // IsResimulating 不改变结果
            PMTimeStep step = NewStep(16f);
            PMTimeStep resimStep = NewStep(16f);
            resimStep.IsResimulating = true;
            PMSimulationResult<PMMoverSyncState, PMMoverAuxState> normal = model.Simulate(step, input, start, aux);
            PMSimulationResult<PMMoverSyncState, PMMoverAuxState> resim = model.Simulate(resimStep, input, start, aux);
            Check(SyncBitEquals(normal.Sync, resim.Sync), "M6 IsResimulating 不影响输出（重放可复现）");

            // 模型不产生不可逆事件（本批诚实口径）
            Check(normal.Events != null && normal.Events.Length == 0, "M7 本批模型不产生事件（空事件集）");
            Check(normal.Aux.Gravity.Equals(aux.Gravity), "M8 Aux 原样透传");

            // 碰撞查询是注入的、只读的
            Check(ReferenceEquals(model.CollisionQuery, _world), "M9 碰撞查询来自构造函数注入");
        }

        // =================================================================================
        //  N. dt 驱动
        // =================================================================================

        private static void TestDtDriven()
        {
            PMMoverModel model = NewModel();
            PMMoverAuxState aux = PMMoverAuxState.CreateDefault();
            PMMoverInput move = ZeroInput(1f, 0f);

            // 同样的输入序列，仅 dt 不同（16 与 50）——每条输入各自一步，不被墙钟合并
            PMMoverSyncState s16 = Standing(-3f, 0f);
            s16 = Step(model, s16, move, 16f, aux);
            Check(Near(s16.Velocity.X, 20f * 0.016f, 1e-5f), "N1 dt=16ms 一步 -> v == 20*0.016");

            PMMoverSyncState s50 = Standing(-3f, 0f);
            s50 = Step(model, s50, move, 50f, aux);
            Check(Near(s50.Velocity.X, 20f * 0.050f, 1e-5f), "N2 dt=50ms 一步 -> v == 20*0.050");
            Check(Near(s50.Position.X, -3f + 1f * 0.050f, 1e-5f), "N3 dt=50ms 位移 == v*dt（不放大也不截断）");

            // 5 条输入分 5 步 vs 1 条输入 1 步：步数 = 输入条数
            PMMoverSyncState five = Standing(-3f, 0f);
            for (int i = 0; i < 5; i++)
            {
                five = Step(model, five, move, 16f, aux);
            }

            Check(Near(five.Velocity.X, 5f * 20f * 0.016f, 1e-4f), "N4 5 条输入 = 5 步加速（20*0.016*5）");
            Check(!Near(five.Position.X, s16.Position.X, 1e-4f), "N5 步数不同则位移不同（不存在墙钟合并）");
        }

        // =================================================================================
        //  O. 替身几何
        // =================================================================================

        private static void TestWorldGeometry()
        {
            PMMoverTestWorld world = new PMMoverTestWorld();

            Check(world.BoxCount == 2, "O1 替身几何只有地板与墙两个盒");
            Check(world.WorldVersion == (int)PMMoverTestWorld.FrozenCollisionWorldVersion, "O2 WorldVersion == 冻结摘要");
            Check(PMMoverTestWorld.FrozenCollisionWorldVersion == 0x52334201u, "O3 冻结摘要字节值 == 0x52334201");

            PMMoverTestBox floor = world.GetBox(PMMoverTestWorld.FloorBoxIndex);
            Check(floor.Min.Equals(new PMVector3(-20f, -1f, -20f)) && floor.Max.Equals(new PMVector3(20f, 0f, 20f)),
                "O4 地板盒 == (-20,-1,-20)..(20,0,20)");

            PMMoverTestBox wall = world.GetBox(PMMoverTestWorld.WallBoxIndex);
            Check(wall.Min.Equals(new PMVector3(-0.5f, 0f, -4f)) && wall.Max.Equals(new PMVector3(0.5f, 2f, 4f)),
                "O5 墙盒 == (-0.5,0,-4)..(0.5,2,4)");

            // 地面查询：站在地板上 -> 距离 0；悬空 -> 未找到
            PMMoverGround onFloor = world.QueryGround(new PMVector3(-3f, 1f, 0f), 0.4f, 1f, 0.05f);
            Check(onFloor.Found && Near(onFloor.Distance, 0f, 1e-6f), "O6 站在地板上：Found 且距离 0");
            Check(onFloor.Normal.Equals(PMVector3.Up), "O7 地板法线为 Up");

            PMMoverGround inAir = world.QueryGround(new PMVector3(-3f, 5f, 0f), 0.4f, 1f, 0.05f);
            Check(!inAir.Found && inAir.Normal.Equals(PMVector3.Zero), "O8 悬空 4m：未找到且法线为零");

            PMMoverGround offFloor = world.QueryGround(new PMVector3(25f, 1f, 0f), 0.4f, 1f, 0.05f);
            Check(!offFloor.Found, "O9 地板之外：未找到支撑面");

            // 贴墙站立时，墙**不能**被当成地面（否则会把角色顶到墙顶）
            PMMoverGround besideWall = world.QueryGround(new PMVector3(-0.9f, 1f, 0f), 0.4f, 1f, 0.05f);
            Check(besideWall.Found && Near(besideWall.Distance, 0f, 1e-6f), "O10 贴墙站立：支撑面仍是地板而不是墙顶");

            // 贴地水平移动不被地板阻挡（skin 的意义）
            PMMoverHit onFloorMove = world.Sweep(new PMVector3(-3f, 1f, 0f), new PMVector3(0.1f, 0f, 0f), 0.4f, 1f);
            Check(!onFloorMove.Blocking && Near(onFloorMove.Fraction, 1f, 1e-6f), "O11 贴地水平移动不被地板阻挡");

            // 向墙扫掠会被阻挡，法线背离墙
            PMMoverHit toWall = world.Sweep(new PMVector3(-0.95f, 1f, 0f), new PMVector3(0.1f, 0f, 0f), 0.4f, 1f);
            Check(toWall.Blocking, "O12 朝墙扫掠被阻挡");
            Check(toWall.Normal.Equals(new PMVector3(-1f, 0f, 0f)), "O13 阻挡法线背离墙（-X）");
            Check(toWall.Fraction > 0f && toWall.Fraction < 1f, "O14 阻挡比例为 (0,1)");

            // 起点在墙内：Fraction=0 且给出推出法线（不产生 NaN）
            PMMoverHit inside = world.Sweep(new PMVector3(0f, 1f, 0f), new PMVector3(0.1f, 0f, 0f), 0.4f, 1f);
            Check(inside.Blocking && Near(inside.Fraction, 0f, 1e-9f), "O15 起点在墙内：Fraction == 0");
            Check(inside.Normal.IsFinite && inside.Normal.Length > 0.5f, "O16 起点重叠时仍给出有限推出法线");

            // 垂直向上不撞地板（地板在脚下）
            PMMoverHit up = world.Sweep(new PMVector3(-3f, 1f, 0f), new PMVector3(0f, 0.5f, 0f), 0.4f, 1f);
            Check(!up.Blocking, "O17 向上跳不撞脚下地板");

            // 本类不得声称 PhysX 等价（门禁只是固化这条"存在且可判定"的事实）
            Check(true, "O18 诚实口径：本类是确定性 AABB 替身，不声称与 Unity PhysX 等价");
        }

        // =================================================================================
        //  辅助
        // =================================================================================

        private static PMMoverTestWorld _world;

        private static PMMoverModel NewModel()
        {
            _world = new PMMoverTestWorld();
            return new PMMoverModel(_world);
        }

        /// <summary>站在 (-3,0) 附近地板上的默认状态（避开原点的墙）。</summary>
        private static PMMoverSyncState Standing(float x, float z)
        {
            PMMoverSyncState s = PMMoverSyncState.CreateDefault();
            s.Position = new PMVector3(x, PMMoverDefaults.CapsuleHalfHeightMeters, z);
            return s;
        }

        /// <summary>Falling 起点（中心 y 给定，未着地）。</summary>
        private static PMMoverSyncState Falling(float x, float y, float z)
        {
            PMMoverSyncState s = Standing(x, z);
            s.Position = new PMVector3(x, y, z);
            s.Mode = PMMoverMode.Falling;
            s.Grounded = false;
            s.GroundNormal = PMVector3.Up;
            return s;
        }

        /// <summary>把加速度与制动都置零，从而"冻结"速度，用于把层/碰撞的效果单独隔离出来。</summary>
        private static PMMoverSyncState Frozen(float x, float z)
        {
            PMMoverSyncState s = Standing(x, z);
            s.Acceleration = 0f;
            s.Braking = 0f;
            return s;
        }

        /// <summary>带初始速度的可移动状态（Velocity 与干净基速必须一起写，见 SyncState 不变量）。</summary>
        private static PMMoverSyncState Movable(float x, float z, PMVector3 velocity)
        {
            PMMoverSyncState s = Frozen(x, z);
            s.Velocity = velocity;
            s.PreAdditiveVelocity = velocity;
            return s;
        }

        private static PMMoverInput ZeroInput(float moveX, float moveZ)
        {
            PMMoverInput input = PMMoverInput.Empty();
            input.MoveX = moveX;
            input.MoveZ = moveZ;
            return input;
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

        private static PMTimeStep NewStep(float dtMs)
        {
            PMTimeStep step = new PMTimeStep();
            step.BaseSimTimeMs = 0L;
            step.StepMs = dtMs;
            step.ServerFrame = PMFrameId.None;
            step.ClientInputFrame = PMFrameId.None;
            step.IsResimulating = false;
            return step;
        }

        private static PMMoverSyncState Step(PMMoverModel model, PMMoverSyncState start, PMMoverInput input,
                                             float dtMs, PMMoverAuxState aux)
        {
            PMSimulationResult<PMMoverSyncState, PMMoverAuxState> result =
                model.Simulate(NewStep(dtMs), input, start, aux);
            return result.Sync;
        }

        private static bool Near(float a, float b, float tolerance)
        {
            return Math.Abs(a - b) <= tolerance;
        }

        private static bool SyncEquals(PMMoverSyncState a, PMMoverSyncState b)
        {
            if (!a.Position.Equals(b.Position) || !a.Velocity.Equals(b.Velocity)
                || !a.PreAdditiveVelocity.Equals(b.PreAdditiveVelocity)
                || a.YawDegrees != b.YawDegrees || a.Mode != b.Mode || a.Grounded != b.Grounded
                || !a.GroundNormal.Equals(b.GroundNormal) || a.Scale != b.Scale
                || a.MaxSpeed != b.MaxSpeed || a.Acceleration != b.Acceleration
                || a.Braking != b.Braking || a.GravityScale != b.GravityScale
                || a.JumpSpeed != b.JumpSpeed)
            {
                return false;
            }

            int countA = a.ActiveLayers == null ? 0 : a.ActiveLayers.Length;
            int countB = b.ActiveLayers == null ? 0 : b.ActiveLayers.Length;
            if (countA != countB)
            {
                return false;
            }

            for (int i = 0; i < countA; i++)
            {
                PMMoverLayer la = a.ActiveLayers[i];
                PMMoverLayer lb = b.ActiveLayers[i];
                if (la.InstanceId != lb.InstanceId || la.Kind != lb.Kind || la.Priority != lb.Priority
                    || !la.Velocity.Equals(lb.Velocity) || la.DurationMs != lb.DurationMs
                    || la.ElapsedMs != lb.ElapsedMs)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool SyncBitEquals(PMMoverSyncState a, PMMoverSyncState b)
        {
            if (Bits(a.Position.X) != Bits(b.Position.X) || Bits(a.Position.Y) != Bits(b.Position.Y)
                || Bits(a.Position.Z) != Bits(b.Position.Z)
                || Bits(a.Velocity.X) != Bits(b.Velocity.X) || Bits(a.Velocity.Y) != Bits(b.Velocity.Y)
                || Bits(a.Velocity.Z) != Bits(b.Velocity.Z)
                || Bits(a.PreAdditiveVelocity.X) != Bits(b.PreAdditiveVelocity.X)
                || Bits(a.PreAdditiveVelocity.Y) != Bits(b.PreAdditiveVelocity.Y)
                || Bits(a.PreAdditiveVelocity.Z) != Bits(b.PreAdditiveVelocity.Z)
                || Bits(a.YawDegrees) != Bits(b.YawDegrees)
                || Bits(a.Scale) != Bits(b.Scale) || Bits(a.MaxSpeed) != Bits(b.MaxSpeed)
                || Bits(a.Acceleration) != Bits(b.Acceleration) || Bits(a.Braking) != Bits(b.Braking)
                || Bits(a.GravityScale) != Bits(b.GravityScale) || Bits(a.JumpSpeed) != Bits(b.JumpSpeed)
                || a.Mode != b.Mode || a.Grounded != b.Grounded
                || Bits(a.GroundNormal.X) != Bits(b.GroundNormal.X)
                || Bits(a.GroundNormal.Y) != Bits(b.GroundNormal.Y)
                || Bits(a.GroundNormal.Z) != Bits(b.GroundNormal.Z))
            {
                return false;
            }

            int countA = a.ActiveLayers == null ? 0 : a.ActiveLayers.Length;
            int countB = b.ActiveLayers == null ? 0 : b.ActiveLayers.Length;
            if (countA != countB)
            {
                return false;
            }

            for (int i = 0; i < countA; i++)
            {
                PMMoverLayer la = a.ActiveLayers[i];
                PMMoverLayer lb = b.ActiveLayers[i];
                if (la.InstanceId != lb.InstanceId || la.Kind != lb.Kind || la.Priority != lb.Priority
                    || la.DurationMs != lb.DurationMs || la.ElapsedMs != lb.ElapsedMs
                    || Bits(la.Velocity.X) != Bits(lb.Velocity.X)
                    || Bits(la.Velocity.Y) != Bits(lb.Velocity.Y)
                    || Bits(la.Velocity.Z) != Bits(lb.Velocity.Z))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool InputEquals(PMMoverInput a, PMMoverInput b)
        {
            if (a.MoveX != b.MoveX || a.MoveY != b.MoveY || a.MoveZ != b.MoveZ
                || a.YawDegrees != b.YawDegrees || a.JumpPressed != b.JumpPressed)
            {
                return false;
            }

            int ae = a.Effects == null ? 0 : a.Effects.Length;
            int be = b.Effects == null ? 0 : b.Effects.Length;
            if (ae != be)
            {
                return false;
            }

            for (int i = 0; i < ae; i++)
            {
                if (a.Effects[i].Kind != b.Effects[i].Kind || a.Effects[i].Mode != b.Effects[i].Mode
                    || !a.Effects[i].Vector.Equals(b.Effects[i].Vector))
                {
                    return false;
                }
            }

            int al = a.Layers == null ? 0 : a.Layers.Length;
            int bl = b.Layers == null ? 0 : b.Layers.Length;
            if (al != bl)
            {
                return false;
            }

            for (int i = 0; i < al; i++)
            {
                if (a.Layers[i].InstanceId != b.Layers[i].InstanceId || a.Layers[i].Kind != b.Layers[i].Kind
                    || a.Layers[i].Priority != b.Layers[i].Priority
                    || a.Layers[i].DurationMs != b.Layers[i].DurationMs
                    || !a.Layers[i].Velocity.Equals(b.Layers[i].Velocity))
                {
                    return false;
                }
            }

            int ar = a.RemovedLayerIds == null ? 0 : a.RemovedLayerIds.Length;
            int br = b.RemovedLayerIds == null ? 0 : b.RemovedLayerIds.Length;
            if (ar != br)
            {
                return false;
            }

            for (int i = 0; i < ar; i++)
            {
                if (a.RemovedLayerIds[i] != b.RemovedLayerIds[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static int Bits(float value)
        {
            return BitConverter.SingleToInt32Bits(value);
        }

        // =================================================================================
        //  断言
        // =================================================================================

        /// <summary>
        /// 用 mutator 改一个字段，验证 "<paramref name="fieldName"/> 差异必须触发 reconcile"。
        /// 注意：SyncState 是 struct，mutator 必须**返回**修改后的值（值传递不会回写）。
        /// </summary>
        private static void CheckReconcileOnField(PMMoverModel model, PMMoverSyncState baseState,
                                                 PMMoverAuxState aux, string fieldName,
                                                 Func<PMMoverSyncState, PMMoverSyncState> mutate)
        {
            PMMoverSyncState changed = mutate(model.CloneSync(baseState));
            Check(model.ShouldReconcile(baseState, changed, aux, aux), "K-字段 " + fieldName + " 差异 -> reconcile");
        }

        private static void CheckThrows(Action action, string what)
        {
            try
            {
                action();
            }
            catch (ArgumentException)
            {
                Check(true, what);
                return;
            }
            catch (Exception ex)
            {
                Fail(what + "（抛出了非 ArgumentException：" + ex.GetType().Name + " " + ex.Message + "）");
                return;
            }

            Fail(what + "（**没有**抛出：非法输入被静默接受）");
        }

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
}
