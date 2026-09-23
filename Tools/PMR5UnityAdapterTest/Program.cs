// ============================================================================
//  PMR5UnityAdapterTest —— R5-C1/C2 适配器的受控语义回归（**不是** PhysX 验证）
// ============================================================================
//
//  本工程做什么：
//    · 编**真实源码** PMUnityProjectileMotion.cs（C1）与 PMUnityProjectilePresentation.cs（C2），
//      不复制、不快照、不用替身替换被验证对象；
//    · 用**受控 UnityEngine 替身**（UnityStubs.cs）提供 PhysicsScene 查询族与对象生命周期；
//      该替身是"球 vs 轴对齐 AABB"的解析模型，并在零距离命中上复现一次观测到的语义
//      （R4-B 曾观察到"起点接触 ⇒ distance=0 且该条 normal=-dir"）。**那是一次观测，不是契约**：
//      不得表述为"所有接触法线恒为 -dir"；被验证的适配器也**不读 normal**，本替身写不写它都不影响断言；
//    · 通过 **IPMProjectileHostMotion 接口**（而不是具体类型）驱动适配器，逐条钉住 C1 契约：
//      无阻挡显式回填直线 / 受阻给接触点 + 零速度 / 起点正重叠不推进 / 沿线最早可信阻挡 /
//      零 TOI 不被当成无障碍 / 饱和显式失败 / trigger 与非白名单被忽略 / 非法输入拒收 /
//      per-key 停止标记不串弹且有界 / Clear 与 Dispose；再钉住 C2：DS 禁止创建 / 只写
//      pos-yaw-尺寸-hidden / 停止无二次副作用 / 位置唯一来源 / 材料只克隆一次 / Dispose 清理 /
//      不碰其它对象与摄像机。
//
//  **它不证明什么**（边界，禁止被读成"假验物理"）：
//    · 替身是"球 vs 轴对齐 AABB"的一阶解析模型，**不是** PhysX、也不是 Unity；
//      它只复现已观测语义，用来把适配器的**判定逻辑**钉死。
//    · 真实 PhysX 上的结论仍是 **PENDING_USER**：必须在 Unity 内跑（本批不启动 Unity、
//      不点菜单、不改场景）。真机验证项见 Docs/plans/_r5_unity_adapter_report.md。
//    · 本工程不接整合器/网络 driver/宿主：整合器侧的 fail-closed 行为由
//      Tools/PMProjectileIntegrationTest 的 P 段独立覆盖，本工程只证明适配器**会返回 false**。
//
//  运行：dotnet build Tools/PMR5UnityAdapterTest -c Release
//        dotnet Tools/PMR5UnityAdapterTest/bin/Release/net8.0/PMR5UnityAdapterTest.dll
//  退出码：0 = 全部通过；1 = 存在失败
// ============================================================================

using System;
using System.Threading;
using PMNet;
using PMNet.Mover;
using PMNet.Projectile;
using PMNet.Unity;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PMR5UnityAdapterTest
{
    internal static class Program
    {
        // ---------------------------------------------------------------- 冻结布局/常量

        /// <summary>会话 epoch（测试内的冻结值）。</summary>
        private const uint TestEpoch = 7u;

        /// <summary>投射物半径（与 PMProjectileSpec 默认值一致）。</summary>
        private const float Radius = 0.1f;

        /// <summary>全层掩码（受控世界的 Collider 都在 layer 0）。</summary>
        private const int AllLayers = ~0;

        /// <summary>命中回退（与适配器常量同源，避免"测试里另写一个数"）。</summary>
        private const float Skin = PMUnityProjectileMotion.ContactSkinMeters;

        /// <summary>地板：顶面 y = 0（与 R4 受控场景同值）。</summary>
        private static readonly Vector3 FloorCenter = new Vector3(0f, -0.5f, 0f);
        private static readonly Vector3 FloorSize = new Vector3(40f, 1f, 40f);

        /// <summary>墙：x ∈ [4.5, 5.5]、y ∈ [0, 2]、z ∈ [-4, 4]。</summary>
        private static readonly Vector3 WallCenter = new Vector3(5f, 1f, 0f);
        private static readonly Vector3 WallSize = new Vector3(1f, 2f, 8f);

        /// <summary>Z 向墙：z ∈ [4.5, 5.5]、x ∈ [-4, 4]（多弹交错用例用）。</summary>
        private static readonly Vector3 WallZCenter = new Vector3(0f, 1f, 5f);
        private static readonly Vector3 WallZSize = new Vector3(8f, 2f, 1f);

        /// <summary>球心到墙面的接触距离 = 4.5 − radius。</summary>
        private const float WallContactX = 4.5f - Radius;

        private static int _checks;
        private static int _failed;

        // ================================================================== 运行框架

        private static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("---- " + title + " ----");
        }

        private static void Check(string name, bool ok, string detail)
        {
            _checks++;
            if (!ok) { _failed++; }
            Console.WriteLine("  [" + (ok ? "PASS" : "FAIL") + "] " + name + " :: " + detail);
        }

        private static void Info(string message)
        {
            Console.WriteLine("  [info] " + message);
        }

        private static bool Near(float a, float b, float tolerance)
        {
            return Math.Abs(a - b) <= tolerance;
        }

        private static bool SameVec(Vector3 a, Vector3 b)
        {
            return a.x == b.x && a.y == b.y && a.z == b.z;
        }

        private static bool SamePM(PMVector3 a, PMVector3 b)
        {
            return a.X == b.X && a.Y == b.Y && a.Z == b.Z;
        }

        private static string V(PMVector3 v)
        {
            return "(" + v.X.ToString("R") + ", " + v.Y.ToString("R") + ", " + v.Z.ToString("R") + ")";
        }

        // ================================================================== 构造助手

        private static PMVector3 P(float x, float y, float z)
        {
            return new PMVector3(x, y, z);
        }

        private static PMProjectileKey PredKey(uint owner, uint id)
        {
            return new PMProjectileKey(TestEpoch, owner, id, PMProjectileOrigin.ClientPredicted);
        }

        private static PMProjectileSpec Spec(float radius, bool stopOnHit, bool hideOnStop)
        {
            PMProjectileSpec spec = new PMProjectileSpec();
            spec.RadiusM = radius;
            spec.SpeedMps = 10f;
            spec.StopOnHit = stopOnHit;
            spec.HideOnStop = hideOnStop;
            spec.LifetimeMs = 3000;
            return spec;
        }

        private static PMProjectileSpec DefaultSpec()
        {
            return Spec(Radius, true, true);
        }

        private static PMProjectileState State(PMVector3 position)
        {
            PMProjectileState state = new PMProjectileState();
            state.Key = PredKey(1u, 1u);
            state.Position = position;
            state.PreviousPosition = position;
            state.SpawnPosition = position;
            state.Velocity = PMVector3.Zero;
            state.Yaw = 0f;
            return state;
        }

        /// <summary>建一个受控物理世界 + 绑定的场景（白名单"同场景"校验需要它）。</summary>
        private static PhysicsScene NewWorld(string name, int handle, out Scene scene)
        {
            PhysicsScene physics = new PhysicsScene(name);
            scene = new Scene(handle, physics);
            physics.BindOwnerScene(scene);
            return physics;
        }

        // ================================================================== 入口

        private static int Main()
        {
            Console.WriteLine("===== PMR5UnityAdapterTest：R5-C1/C2 适配器受控语义回归（替身，不是 PhysX） =====");
            Console.WriteLine("边界：受控替身是『球 vs 轴对齐 AABB』的一阶模型，只复现已观测语义；");
            Console.WriteLine("      真实 PhysX 结论 = PENDING_USER（需在 Unity 内跑），本工程不代替它。");

            SectionA();
            SectionB();
            SectionC();
            SectionD();
            SectionE();
            SectionF();
            SectionG();
            SectionH();
            SectionI();
            SectionJ();
            SectionK();
            SectionL();
            SectionM();

            Console.WriteLine();
            Console.WriteLine("结果：" + (_failed == 0 ? "全部通过" : "存在失败")
                              + "（checks=" + _checks + " failed=" + _failed + "）");
            Console.WriteLine("边界：本工程不启动 Unity、不跑 PhysX、不接整合器与网络 driver；"
                              + "真机验证项见 Docs/plans/_r5_unity_adapter_report.md 的 PENDING_USER 段。");

            return _failed == 0 ? 0 : 1;
        }

        // ================================================================== A. 替身自检

        private static void SectionA()
        {
            Section("A. 受控替身自检：复现真实 Play 观测到的零距离语义（不是 PhysX 实机）");

            Scene scene;
            PhysicsScene world = NewWorld("A", 101, out scene);
            Collider floor = world.AddBox("Floor", FloorCenter, FloorSize, 0);
            Collider wall = world.AddBox("Wall", WallCenter, WallSize, 0);

            RaycastHit[] buffer = new RaycastHit[PMUnityProjectileMotion.HitBufferCapacity];

            // A1：球正好贴地（球心 y = radius、地板顶面 y = 0）时的水平扫掠。
            int count = world.SphereCast(new Vector3(0f, Radius, 0f), Radius, new Vector3(1f, 0f, 0f),
                                         buffer, 2f, AllLayers, QueryTriggerInteraction.Ignore);
            bool zero = count >= 1 && buffer[0].distance == 0f
                        && ReferenceEquals(buffer[0].collider, floor)
                        && Near(buffer[0].normal.x, -1f, 1e-6f)
                        && Near(buffer[0].normal.y, 0f, 1e-6f)
                        && Near(buffer[0].normal.z, 0f, 1e-6f);
            Check("A1 贴地水平扫掠在本替身模型里得到 distance=0（normal 仅为复现那次观测，适配器不读）", zero,
                "count=" + count + " distance=" + buffer[0].distance + " normal=" + buffer[0].normal);

            // A2：悬空 0.3 时向下扫掠给正距离 TOI。
            int downCount = world.SphereCast(new Vector3(0f, Radius + 0.3f, 0f), Radius,
                                             new Vector3(0f, -1f, 0f), buffer, 0.5f,
                                             AllLayers, QueryTriggerInteraction.Ignore);
            Check("A2 悬空 0.3 向下扫掠得到正距离 TOI ≈ 0.3",
                downCount >= 1 && Near(buffer[0].distance, 0.3f, 1e-4f),
                "count=" + downCount + " distance=" + buffer[0].distance.ToString("R"));

            // A3：OverlapSphere 的语义是 "touching or inside"。
            Collider[] overlaps = new Collider[PMUnityProjectileMotion.OverlapBufferCapacity];
            int overlapCount = world.OverlapSphere(new Vector3(0f, Radius, 0f), Radius, overlaps,
                                                  AllLayers, QueryTriggerInteraction.Ignore);
            Check("A3 起点正好接触时 OverlapSphere 报出该 Collider（touching or inside）",
                overlapCount == 1 && ReferenceEquals(overlaps[0], floor),
                "count=" + overlapCount);

            // A4：白名单外的实体在替身里同样可被查询看见（证明"隔离"只能靠白名单，不能靠场景）。
            Check("A4 同一世界同时含地板与墙（供后面断言『最早命中』不是『首个命中』）",
                world.Colliders.Count == 2 && ReferenceEquals(world.Colliders[1], wall),
                "colliders=" + world.Colliders.Count);
        }

        // ================================================================== B. 无阻挡

        private static void SectionB()
        {
            Section("B. 无阻挡：TryStep 返回 true + 原直线，TryStop 返回 false");

            Scene scene;
            PhysicsScene world = NewWorld("B", 102, out scene);
            Collider far = world.AddBox("FarBox", new Vector3(100f, 1f, 0f), new Vector3(1f, 1f, 1f), 0);

            IPMProjectileHostMotion motion =
                new PMUnityProjectileMotion(world, new Collider[] { far }, AllLayers);

            PMProjectileKey key = PredKey(1u, 1u);
            PMProjectileSpec spec = DefaultSpec();
            PMProjectileState snapshot = State(P(0f, 1f, 0f));

            PMVector3 straight = P(1f, 1f, 0f);
            PMVector3 straightVelocity = P(10f, 0f, 0f);

            PMVector3 position;
            PMVector3 velocity;
            float yaw;
            bool used = motion.TryStep(key, spec, snapshot, 100, straight, straightVelocity, 33f,
                                      out position, out velocity, out yaw);

            Check("B1 无阻挡：TryStep=true 且原样回填直线位置/速度/朝向",
                used && SamePM(position, straight) && SamePM(velocity, straightVelocity) && yaw == 33f,
                "used=" + used + " position=" + V(position) + " velocity=" + V(velocity) + " yaw=" + yaw);

            PMVector3 stopPosition;
            bool stop = motion.TryStop(key, spec, snapshot, 0.0, out stopPosition);
            Check("B2 无阻挡：TryStop=false（正常语义：本步不停止）", !stop, "stop=" + stop);

            Check("B3 查询透传：layerMask 一致 + trigger=Ignore + maxDistance=本步位移长度",
                world.LastLayerMask == AllLayers
                && world.LastQueryTriggerInteraction == QueryTriggerInteraction.Ignore
                && Near(world.LastMaxDistance, 1f, 1e-6f),
                "mask=" + world.LastLayerMask + " trigger=" + world.LastQueryTriggerInteraction
                + " maxDistance=" + world.LastMaxDistance.ToString("R"));

            Check("B4 适配器只走 _scene.*，从不调用全局 Physics（不操作默认物理世界）",
                Physics.StaticQueryCalls == 0, "Physics.StaticQueryCalls=" + Physics.StaticQueryCalls);

            PMUnityProjectileMotionStats stats = ((PMUnityProjectileMotion)motion).GetStats();
            Check("B5 统计：Steps=1 / BlockedSteps=0 / LiveStopMarks=0",
                stats.Steps == 1 && stats.BlockedSteps == 0 && stats.LiveStopMarks == 0,
                "steps=" + stats.Steps + " blocked=" + stats.BlockedSteps + " live=" + stats.LiveStopMarks);
        }

        // ================================================================== C. 沿线最早阻挡

        private static void SectionC()
        {
            Section("C. 沿线最早可信阻挡：取最近接触点（不是 PhysX 返回顺序里的第一条）");

            Scene scene;
            PhysicsScene world = NewWorld("C", 103, out scene);
            Collider floor = world.AddBox("Floor", FloorCenter, FloorSize, 0);
            Collider nearWall = world.AddBox("NearWall", WallCenter, WallSize, 0);
            Collider farWall = world.AddBox("FarWall", new Vector3(9f, 1f, 0f), WallSize, 0);

            IPMProjectileHostMotion motion =
                new PMUnityProjectileMotion(world, new Collider[] { floor, nearWall, farWall }, AllLayers);

            PMProjectileKey key = PredKey(1u, 1u);
            PMProjectileSpec spec = DefaultSpec();
            PMProjectileState snapshot = State(P(0f, 1f, 0f));

            PMVector3 straight = P(10f, 1f, 0f);

            PMVector3 position;
            PMVector3 velocity;
            float yaw;
            bool used = motion.TryStep(key, spec, snapshot, 1000, straight, P(10f, 0f, 0f), 0f,
                                      out position, out velocity, out yaw);

            bool blocked = used && velocity == PMVector3.Zero
                           && Near(position.X, WallContactX - Skin, 2e-3f)
                           && Near(position.Y, 1f, 1e-6f) && Near(position.Z, 0f, 1e-6f);

            Check("C1 受阻：TryStep=true + 接触点（回退 skin）+ 零 velocity + 原朝向",
                blocked && yaw == 0f,
                "used=" + used + " position=" + V(position) + " velocity=" + V(velocity) + " yaw=" + yaw);

            Check("C2 取的是**最近**的墙（4.4 而不是更远的 8.4 / 不是返回顺序的第一条）",
                position.X > 2f && position.X < 5f,
                "x=" + position.X.ToString("R") + "（若取到远端墙会接近 8.4）");

            PMVector3 stopPosition;
            bool stop1 = motion.TryStop(key, spec, snapshot, 1.0, out stopPosition);
            Check("C3 TryStop 消费本步标记：true + 与 TryStep 接触点逐字段相同",
                stop1 && SamePM(stopPosition, position),
                "stop=" + stop1 + " stopPosition=" + V(stopPosition));

            PMVector3 stopPosition2;
            bool stop2 = motion.TryStop(key, spec, snapshot, 1.0, out stopPosition2);
            Check("C4 标记已被消费：第二次 TryStop=false（不重复停止）", !stop2, "stop2=" + stop2);

            PMUnityProjectileMotionStats stats = ((PMUnityProjectileMotion)motion).GetStats();
            Check("C5 统计：BlockedSteps=1 / ConsumedStopMarks=1 / LiveStopMarks=0",
                stats.BlockedSteps == 1 && stats.ConsumedStopMarks == 1 && stats.LiveStopMarks == 0,
                "blocked=" + stats.BlockedSteps + " consumed=" + stats.ConsumedStopMarks
                + " live=" + stats.LiveStopMarks);
        }

        // ================================================================== D. 多弹交错

        private static void SectionD()
        {
            Section("D. 多弹交错：停止标记按 key 记账，互不串（同帧多颗弹查询）");

            Scene scene;
            PhysicsScene world = NewWorld("D", 104, out scene);
            Collider floor = world.AddBox("Floor", FloorCenter, FloorSize, 0);
            Collider wallX = world.AddBox("WallX", WallCenter, WallSize, 0);
            Collider wallZ = world.AddBox("WallZ", WallZCenter, WallZSize, 0);

            IPMProjectileHostMotion motion =
                new PMUnityProjectileMotion(world, new Collider[] { floor, wallX, wallZ }, AllLayers);

            PMProjectileSpec spec = DefaultSpec();

            PMProjectileKey keyX = PredKey(1u, 11u);
            PMProjectileKey keyZ = PredKey(1u, 12u);
            PMProjectileKey keyFree = PredKey(1u, 13u);

            PMProjectileState snapX = State(P(0f, 1f, 0f));
            PMProjectileState snapZ = State(P(0f, 1f, 1f));
            PMProjectileState snapFree = State(P(0f, 6f, 0f));

            PMVector3 position;
            PMVector3 velocity;
            float yaw;

            // 交错顺序：X 步、Z 步、自由步；然后按相反顺序取停止标记。
            bool usedX = motion.TryStep(keyX, spec, snapX, 1000, P(10f, 1f, 0f), P(10f, 0f, 0f), 0f,
                                        out position, out velocity, out yaw);
            bool okX = usedX && velocity == PMVector3.Zero && Near(position.X, WallContactX - Skin, 2e-3f);

            bool usedZ = motion.TryStep(keyZ, spec, snapZ, 1000, P(0f, 1f, 11f), P(0f, 0f, 10f), 90f,
                                        out position, out velocity, out yaw);
            bool okZ = usedZ && velocity == PMVector3.Zero && Near(position.Z, WallContactX - Skin, 2e-3f)
                       && Near(position.X, 0f, 1e-6f);

            bool usedFree = motion.TryStep(keyFree, spec, snapFree, 1000, P(10f, 6f, 0f), P(10f, 0f, 0f), 0f,
                                           out position, out velocity, out yaw);
            bool okFree = usedFree && SamePM(position, P(10f, 6f, 0f)) && velocity == P(10f, 0f, 0f);

            Check("D1 三颗弹各自判定正确（X 撞墙 / Z 撞墙 / 高处无阻挡）", okX && okZ && okFree,
                "okX=" + okX + " okZ=" + okZ + " okFree=" + okFree);

            PMUnityProjectileMotionStats staged = ((PMUnityProjectileMotion)motion).GetStats();
            Check("D2 未消费前：3 条标记各占一条（LiveStopMarks=2，无阻挡的弹不记标记）",
                staged.LiveStopMarks == 2,
                "live=" + staged.LiveStopMarks);

            PMVector3 stopFree;
            bool stopFreeFlag = motion.TryStop(keyFree, spec, snapFree, 1.0, out stopFree);

            PMVector3 stopZ;
            bool stopZFlag = motion.TryStop(keyZ, spec, snapZ, 1.0, out stopZ);

            PMVector3 stopX;
            bool stopXFlag = motion.TryStop(keyX, spec, snapX, 1.0, out stopX);

            Check("D3 交错取标记：无阻挡弹 false，Z 弹拿到 Z 的位置，X 弹拿到 X 的位置（不串）",
                !stopFreeFlag && stopZFlag && stopXFlag
                && Near(stopZ.Z, WallContactX - Skin, 2e-3f) && Near(stopZ.X, 0f, 1e-6f)
                && Near(stopX.X, WallContactX - Skin, 2e-3f) && Near(stopX.Z, 0f, 1e-6f),
                "stopZ=" + V(stopZ) + " stopX=" + V(stopX));

            PMUnityProjectileMotionStats done = ((PMUnityProjectileMotion)motion).GetStats();
            Check("D4 全部消费后 LiveStopMarks=0 / ConsumedStopMarks=2",
                done.LiveStopMarks == 0 && done.ConsumedStopMarks == 2,
                "live=" + done.LiveStopMarks + " consumed=" + done.ConsumedStopMarks);
        }

        // ================================================================== E. 起点正重叠

        private static void SectionE()
        {
            Section("E. 起点正重叠：OverlapSphere 权威判定 ⇒ 阻止推进（停在当前真实位置）");

            Scene scene;
            PhysicsScene world = NewWorld("E", 105, out scene);
            Collider floor = world.AddBox("Floor", FloorCenter, FloorSize, 0);
            Collider wall = world.AddBox("Wall", WallCenter, WallSize, 0);

            IPMProjectileHostMotion motion =
                new PMUnityProjectileMotion(world, new Collider[] { floor, wall }, AllLayers);

            PMProjectileSpec spec = DefaultSpec();

            // E1：球心在墙体内（穿透）。
            PMProjectileKey key = PredKey(1u, 21u);
            PMProjectileState embedded = State(P(5f, 1f, 0f));

            PMVector3 position;
            PMVector3 velocity;
            float yaw;
            int castsBefore = world.SphereCastCalls;

            bool used = motion.TryStep(key, spec, embedded, 1000, P(15f, 1f, 0f), P(10f, 0f, 0f), 0f,
                                       out position, out velocity, out yaw);

            Check("E1 起点嵌墙：TryStep=true 但**停在起点**（不推进）+ 零 velocity",
                used && SamePM(position, embedded.Position) && velocity == PMVector3.Zero,
                "used=" + used + " position=" + V(position) + " start=" + V(embedded.Position));

            Check("E2 起点重叠由 OverlapSphere 权威判定，因此**不需要**再扫掠（短路，不浪费查询）",
                world.SphereCastCalls == castsBefore,
                "castsBefore=" + castsBefore + " castsAfter=" + world.SphereCastCalls);

            PMVector3 stopPosition;
            bool stop = motion.TryStop(key, spec, embedded, 1.0, out stopPosition);
            Check("E3 起点重叠同样产生一次性停止标记（TryStop=true，位置=起点）",
                stop && SamePM(stopPosition, embedded.Position),
                "stop=" + stop + " stopPosition=" + V(stopPosition));

            // E4：球心在地板内部（半嵌入）。
            PMProjectileKey keyFloor = PredKey(1u, 22u);
            PMProjectileState halfIn = State(P(0f, 0.05f, 0f));

            bool usedFloor = motion.TryStep(keyFloor, spec, halfIn, 1000, P(10f, 0.05f, 0f),
                                            P(10f, 0f, 0f), 0f, out position, out velocity, out yaw);
            Check("E4 半嵌地板（球心 y=0.05 < radius）同样判为起点重叠并停止在原地",
                usedFloor && SamePM(position, halfIn.Position) && velocity == PMVector3.Zero,
                "used=" + usedFloor + " position=" + V(position));

            PMUnityProjectileMotionStats stats = ((PMUnityProjectileMotion)motion).GetStats();
            Check("E5 统计：StartOverlapBlocks=2 / BlockedSteps=2 / SaturationFailures=0",
                stats.StartOverlapBlocks == 2 && stats.BlockedSteps == 2 && stats.SaturationFailures == 0,
                "startOverlap=" + stats.StartOverlapBlocks + " blocked=" + stats.BlockedSteps
                + " saturation=" + stats.SaturationFailures);
        }

        // ================================================================== F. 零 TOI 与真墙

        private static void SectionF()
        {
            Section("F. 零 TOI 不被当成无障碍：贴地不让弹跳过真正墙，也不让弹穿地");

            Scene scene;
            PhysicsScene world = NewWorld("F", 106, out scene);
            Collider floor = world.AddBox("Floor", FloorCenter, FloorSize, 0);
            Collider wall = world.AddBox("Wall", WallCenter, WallSize, 0);

            PMProjectileSpec spec = DefaultSpec();

            // F1：贴地（起点与地板正好接触）+ 前方有墙 ⇒ 必须停在起点，而不是飞到墙的接触点。
            IPMProjectileHostMotion withFloor =
                new PMUnityProjectileMotion(world, new Collider[] { floor, wall }, AllLayers);

            PMProjectileKey key1 = PredKey(1u, 31u);
            PMProjectileState touching = State(P(0f, Radius, 0f));

            PMVector3 position;
            PMVector3 velocity;
            float yaw;
            bool used1 = withFloor.TryStep(key1, spec, touching, 1000, P(10f, Radius, 0f),
                                           P(10f, 0f, 0f), 0f, out position, out velocity, out yaw);

            Check("F1 贴地 + 前方有墙：停在起点（零接触没有被跳过而飞到墙）",
                used1 && SamePM(position, touching.Position) && velocity == PMVector3.Zero,
                "position=" + V(position) + "（若跳过零接触会接近 x="
                + (WallContactX - Skin).ToString("R") + "）");

            // F2：对照 —— 把地板从白名单里去掉，同一姿态就会飞到墙的接触点。
            IPMProjectileHostMotion wallOnly =
                new PMUnityProjectileMotion(world, new Collider[] { wall }, AllLayers);

            PMProjectileKey key2 = PredKey(1u, 32u);
            bool used2 = wallOnly.TryStep(key2, spec, State(P(0f, Radius, 0f)), 1000,
                                          P(10f, Radius, 0f), P(10f, 0f, 0f), 0f,
                                          out position, out velocity, out yaw);

            Check("F2 对照（地板不在白名单）：同一姿态飞抵墙的接触点 ⇒ 证明 F1 的差异来自『零接触』",
                used2 && velocity == PMVector3.Zero && Near(position.X, WallContactX - Skin, 2e-3f),
                "position=" + V(position));

            // F3：贴地 + 无墙 ⇒ 零接触仍然是"阻挡"，不是"无障碍"。
            IPMProjectileHostMotion floorOnly =
                new PMUnityProjectileMotion(world, new Collider[] { floor, wall }, AllLayers);

            PMProjectileKey key3 = PredKey(1u, 33u);
            bool used3 = floorOnly.TryStep(key3, spec, State(P(0f, Radius, 0f)), 1000,
                                           P(0f, Radius, 20f), P(0f, 0f, 10f), 0f,
                                           out position, out velocity, out yaw);

            Check("F3 贴地且水平方向无障碍：仍然受阻（零接触是真实阻挡，不是无障碍）",
                used3 && SamePM(position, P(0f, Radius, 0f)) && velocity == PMVector3.Zero,
                "position=" + V(position));

            // F4：只让**扫掠**报出零距离（替身开关模拟"overlap 与 sweep 对恰好接触不承诺同一结论"），
            //     验证适配器在"只有 cast 的 distance=0"这条路径上同样不跳过、不穿墙。
            world.ReportTouchingInOverlap = false;

            IPMProjectileHostMotion castOnly =
                new PMUnityProjectileMotion(world, new Collider[] { floor, wall }, AllLayers);

            PMProjectileKey key4 = PredKey(1u, 34u);
            bool used4 = castOnly.TryStep(key4, spec, State(P(0f, Radius, 0f)), 1000,
                                          P(10f, Radius, 0f), P(10f, 0f, 0f), 0f,
                                          out position, out velocity, out yaw);

            PMUnityProjectileMotionStats castStats = ((PMUnityProjectileMotion)castOnly).GetStats();

            Check("F4 仅扫掠报出零距离（OverlapSphere 未报）：仍停在起点，且不是失败路径",
                used4 && SamePM(position, P(0f, Radius, 0f)) && velocity == PMVector3.Zero
                && castStats.StartOverlapBlocks == 0 && castStats.BlockedSteps == 1
                && castStats.SaturationFailures == 0,
                "position=" + V(position) + " startOverlapBlocks=" + castStats.StartOverlapBlocks
                + " blocked=" + castStats.BlockedSteps + " saturation=" + castStats.SaturationFailures);

            world.ReportTouchingInOverlap = true;

            PMUnityProjectileMotionStats stats = ((PMUnityProjectileMotion)withFloor).GetStats();
            Check("F5 全程 SaturationFailures=0（受阻不是靠失败路径实现的）",
                stats.SaturationFailures == 0,
                "saturation=" + stats.SaturationFailures);
        }

        // ================================================================== G. 饱和

        private static void SectionG()
        {
            Section("G. 缓冲饱和 ⇒ 显式失败（false），绝不当作『没有障碍』");

            PMProjectileSpec spec = DefaultSpec();

            // G1：扫掠饱和（40 个候选在弹道上，容量 32）。
            Scene scene1;
            PhysicsScene world1 = NewWorld("G1", 107, out scene1);
            Collider[] many = new Collider[40];
            for (int i = 0; i < many.Length; i++)
            {
                many[i] = world1.AddBox("Block" + i, new Vector3(2f + 0.1f * i, 1f, 0f),
                                        new Vector3(0.05f, 2f, 2f), 0);
            }

            IPMProjectileHostMotion motion1 = new PMUnityProjectileMotion(world1, many, AllLayers);

            PMProjectileKey key1 = PredKey(1u, 41u);
            PMVector3 position;
            PMVector3 velocity;
            float yaw;
            bool used1 = motion1.TryStep(key1, spec, State(P(0f, 1f, 0f)), 1000,
                                         P(10f, 1f, 0f), P(10f, 0f, 0f), 0f,
                                         out position, out velocity, out yaw);

            PMUnityProjectileMotionStats stats1 = ((PMUnityProjectileMotion)motion1).GetStats();
            Check("G1 扫掠命中数 == 容量 ⇒ TryStep=false（不是 true+直线，也不是某个碰巧的命中）",
                !used1 && stats1.SaturationFailures == 1 && stats1.LiveStopMarks == 0,
                "used=" + used1 + " saturation=" + stats1.SaturationFailures
                + " live=" + stats1.LiveStopMarks);

            // G2：起点重叠饱和（40 个 Collider 全压在起点上）。
            Scene scene2;
            PhysicsScene world2 = NewWorld("G2", 108, out scene2);
            Collider[] packed = new Collider[40];
            for (int i = 0; i < packed.Length; i++)
            {
                packed[i] = world2.AddBox("Packed" + i, new Vector3(0f, 1f, 0f), new Vector3(2f, 2f, 2f), 0);
            }

            IPMProjectileHostMotion motion2 = new PMUnityProjectileMotion(world2, packed, AllLayers);

            PMProjectileKey key2 = PredKey(1u, 42u);
            bool used2 = motion2.TryStep(key2, spec, State(P(0f, 1f, 0f)), 1000,
                                         P(10f, 1f, 0f), P(10f, 0f, 0f), 0f,
                                         out position, out velocity, out yaw);

            PMUnityProjectileMotionStats stats2 = ((PMUnityProjectileMotion)motion2).GetStats();
            Check("G2 起点重叠查询饱和 ⇒ TryStep=false（无法证明起点干净时必须 fail closed）",
                !used2 && stats2.SaturationFailures == 1 && world2.SphereCastCalls == 0,
                "used=" + used2 + " saturation=" + stats2.SaturationFailures
                + " casts=" + world2.SphereCastCalls);
        }

        // ================================================================== H. trigger / 白名单

        private static void SectionH()
        {
            Section("H. 只看白名单、忽略 trigger：非白名单实体与 trigger 都不阻挡");

            PMProjectileSpec spec = DefaultSpec();

            Scene scene;
            PhysicsScene world = NewWorld("H", 109, out scene);
            Collider far = world.AddBox("Far", new Vector3(100f, 1f, 0f), new Vector3(1f, 1f, 1f), 0);
            Collider trigger = world.AddTriggerBox("Trigger", WallCenter, WallSize, 0);
            Collider notWhitelisted = world.AddBox("NotWhitelisted", WallZCenter, WallZSize, 0);

            IPMProjectileHostMotion motion =
                new PMUnityProjectileMotion(world, new Collider[] { far, trigger }, AllLayers);

            PMProjectileKey key = PredKey(1u, 51u);
            PMVector3 position;
            PMVector3 velocity;
            float yaw;

            bool used = motion.TryStep(key, spec, State(P(0f, 1f, 0f)), 1000,
                                       P(10f, 1f, 0f), P(10f, 0f, 0f), 0f,
                                       out position, out velocity, out yaw);

            Check("H1 白名单内的 trigger 不阻挡：TryStep=true + 原直线",
                used && SamePM(position, P(10f, 1f, 0f)) && velocity == P(10f, 0f, 0f),
                "used=" + used + " position=" + V(position));

            Check("H2 场景内但**不在**白名单的实体不阻挡（隔离只能靠白名单实现）",
                unblockedProbe(motion, world) && world.LastQueryTriggerInteraction == QueryTriggerInteraction.Ignore,
                "trigger=" + world.LastQueryTriggerInteraction);

            // H3：白名单里真的放一面墙 ⇒ 仍然阻挡（证明 H1/H2 不是"查询坏了"）。
            PhysicsScene world2 = NewWorld("H2", 110, out scene);
            Collider wall = world2.AddBox("Wall", WallCenter, WallSize, 0);
            IPMProjectileHostMotion motion2 =
                new PMUnityProjectileMotion(world2, new Collider[] { wall }, AllLayers);

            bool used2 = motion2.TryStep(PredKey(1u, 52u), spec, State(P(0f, 1f, 0f)), 1000,
                                         P(10f, 1f, 0f), P(10f, 0f, 0f), 0f,
                                         out position, out velocity, out yaw);

            Check("H3 白名单里的墙照常阻挡（对照组：说明 H1/H2 的『通过』是白名单判定的结果）",
                used2 && velocity == PMVector3.Zero && Near(position.X, WallContactX - Skin, 2e-3f),
                "position=" + V(position));
        }

        private static bool unblockedProbe(IPMProjectileHostMotion motion, PhysicsScene world)
        {
            PMProjectileSpec spec = DefaultSpec();
            PMVector3 position;
            PMVector3 velocity;
            float yaw;
            bool used = motion.TryStep(PredKey(2u, 51u), spec, State(P(0f, 1f, 1f)), 1000,
                                       P(0f, 1f, 11f), P(0f, 0f, 10f), 0f,
                                       out position, out velocity, out yaw);
            return used && SamePM(position, P(0f, 1f, 11f));
        }

        // ================================================================== I. 非法构造/输入/线程

        private static void SectionI()
        {
            Section("I. 错误 scene / 非法输入 / 跨线程：显式拒绝（构造抛异常，查询返回 false）");

            Scene sceneA;
            PhysicsScene worldA = NewWorld("I-A", 111, out sceneA);
            Collider colliderA = worldA.AddBox("A", new Vector3(100f, 1f, 0f), new Vector3(1f, 1f, 1f), 0);

            Scene sceneB;
            PhysicsScene worldB = NewWorld("I-B", 112, out sceneB);
            Collider colliderB = worldB.AddBox("B", new Vector3(100f, 1f, 0f), new Vector3(1f, 1f, 1f), 0);

            Check("I1 拒绝默认物理世界（Physics.defaultPhysicsScene）",
                ThrowsArgument(() => new PMUnityProjectileMotion(Physics.defaultPhysicsScene,
                                                                 new Collider[] { colliderA }, AllLayers)),
                "期望 ArgumentException");

            PhysicsScene invalid = new PhysicsScene("invalid");
            invalid.Valid = false;
            Check("I2 拒绝无效 PhysicsScene（IsValid()==false）",
                ThrowsArgument(() => new PMUnityProjectileMotion(invalid, new Collider[] { colliderA }, AllLayers)),
                "期望 ArgumentException");

            Check("I3 拒绝 null / 空白名单（两种默认解释都不安全）",
                ThrowsArgument(() => new PMUnityProjectileMotion(worldA, null, AllLayers))
                && ThrowsArgument(() => new PMUnityProjectileMotion(worldA, new Collider[0], AllLayers)),
                "期望 ArgumentException");

            Check("I4 拒绝白名单里的 null 项",
                ThrowsArgument(() => new PMUnityProjectileMotion(worldA, new Collider[] { null }, AllLayers)),
                "期望 ArgumentException");

            Check("I5 拒绝**跨场景**白名单（旧大厅 Collider 不得进入投射物查询）",
                ThrowsArgument(() => new PMUnityProjectileMotion(worldA, new Collider[] { colliderB }, AllLayers)),
                "期望 ArgumentException（colliderB 属于 sceneB）");

            IPMProjectileHostMotion motion = null;
            bool ctorOk = true;
            try
            {
                motion = new PMUnityProjectileMotion(worldA, new Collider[] { colliderA }, AllLayers);
            }
            catch (Exception ex)
            {
                ctorOk = false;
                Info("同场景白名单构造异常：" + ex.GetType().Name + " " + ex.Message);
            }

            Check("I6 同场景白名单构造成功（对照：I5 的失败确实来自场景不一致）", ctorOk, "ctorOk=" + ctorOk);

            PMProjectileState snapshot = State(P(0f, 1f, 0f));
            PMVector3 position;
            PMVector3 velocity;
            float yaw;

            bool nanRadius = motion.TryStep(PredKey(1u, 61u), Spec(float.NaN, true, true), snapshot, 100,
                                            P(1f, 1f, 0f), P(10f, 0f, 0f), 0f,
                                            out position, out velocity, out yaw);
            bool negRadius = motion.TryStep(PredKey(1u, 62u), Spec(-1f, true, true), snapshot, 100,
                                            P(1f, 1f, 0f), P(10f, 0f, 0f), 0f,
                                            out position, out velocity, out yaw);
            bool zeroRadius = motion.TryStep(PredKey(1u, 63u), Spec(0f, true, true), snapshot, 100,
                                             P(1f, 1f, 0f), P(10f, 0f, 0f), 0f,
                                             out position, out velocity, out yaw);

            Check("I7 拒绝 NaN / 负 / 零半径（返回 false ⇒ 整合器停在原位）",
                !nanRadius && !negRadius && !zeroRadius,
                "nan=" + nanRadius + " neg=" + negRadius + " zero=" + zeroRadius);

            bool nanStraight = motion.TryStep(PredKey(1u, 64u), DefaultSpec(), snapshot, 100,
                                              P(float.NaN, 1f, 0f), P(10f, 0f, 0f), 0f,
                                              out position, out velocity, out yaw);
            bool nanVelocity = motion.TryStep(PredKey(1u, 65u), DefaultSpec(), snapshot, 100,
                                              P(1f, 1f, 0f), P(float.PositiveInfinity, 0f, 0f), 0f,
                                              out position, out velocity, out yaw);
            bool nanYaw = motion.TryStep(PredKey(1u, 66u), DefaultSpec(), snapshot, 100,
                                         P(1f, 1f, 0f), P(10f, 0f, 0f), float.NaN,
                                         out position, out velocity, out yaw);
            bool nanSnapshot = motion.TryStep(PredKey(1u, 67u), DefaultSpec(), State(P(float.NaN, 1f, 0f)), 100,
                                              P(1f, 1f, 0f), P(10f, 0f, 0f), 0f,
                                              out position, out velocity, out yaw);

            Check("I8 拒绝非 finite 的直线位置/速度/朝向与快照位置",
                !nanStraight && !nanVelocity && !nanYaw && !nanSnapshot,
                "straight=" + nanStraight + " velocity=" + nanVelocity + " yaw=" + nanYaw
                + " snapshot=" + nanSnapshot);

            bool nullSpec = motion.TryStep(PredKey(1u, 68u), null, snapshot, 100,
                                           P(1f, 1f, 0f), P(10f, 0f, 0f), 0f,
                                           out position, out velocity, out yaw);
            bool nullSnapshot = motion.TryStep(PredKey(1u, 69u), DefaultSpec(), null, 100,
                                               P(1f, 1f, 0f), P(10f, 0f, 0f), 0f,
                                               out position, out velocity, out yaw);

            Check("I9 拒绝 null spec / null snapshot（空引用不得变成『无障碍』）",
                !nullSpec && !nullSnapshot, "nullSpec=" + nullSpec + " nullSnapshot=" + nullSnapshot);

            PMUnityProjectileMotionStats rejected = ((PMUnityProjectileMotion)motion).GetStats();
            Check("I10 被拒收的调用不留下停止标记（RejectedInputs=9 / LiveStopMarks=0）",
                rejected.RejectedInputs == 9 && rejected.LiveStopMarks == 0,
                "rejected=" + rejected.RejectedInputs + " live=" + rejected.LiveStopMarks);

            // I11：跨线程查询必须显式失败（适配器只允许主线程）。
            bool crossThreadThrew = false;
            bool crossThreadReturnedTrue = false;
            Exception crossThreadError = null;
            Thread worker = new Thread(() =>
            {
                try
                {
                    PMVector3 p;
                    PMVector3 v;
                    float y;
                    bool ok = motion.TryStep(PredKey(1u, 70u), DefaultSpec(), State(P(0f, 1f, 0f)), 100,
                                             P(1f, 1f, 0f), P(10f, 0f, 0f), 0f, out p, out v, out y);
                    crossThreadReturnedTrue = ok;
                }
                catch (InvalidOperationException ex)
                {
                    crossThreadThrew = true;
                    crossThreadError = ex;
                }
                catch (Exception ex)
                {
                    crossThreadError = ex;
                }
            });
            worker.Start();
            worker.Join(5000);

            Check("I11 跨线程查询显式抛出（主线程约束；整合器会 fail closed）",
                crossThreadThrew && !crossThreadReturnedTrue
                && (crossThreadError == null || crossThreadError is InvalidOperationException),
                "threw=" + crossThreadThrew + " returned=" + crossThreadReturnedTrue);
        }

        private static bool ThrowsArgument(Action action)
        {
            try
            {
                action();
                return false;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        // ================================================================== J. 有界记账

        private static void SectionJ()
        {
            Section("J. 停止标记有界：表满时**显式失败且不淘汰已接受标记**；Clear/Dispose 是唯一清空入口");

            Scene scene;
            PhysicsScene world = NewWorld("J", 113, out scene);
            Collider wall = world.AddBox("Wall", WallCenter, WallSize, 0);

            PMUnityProjectileMotion motion = new PMUnityProjectileMotion(world, new Collider[] { wall },
                                                                        AllLayers);
            PMProjectileSpec spec = DefaultSpec();

            int cap = PMUnityProjectileMotion.MaxStopMarkRecords;
            int overflow = cap + 44;

            PMVector3 position;
            PMVector3 velocity;
            float yaw;

            int accepted = 0;
            int refused = 0;
            bool lastRefusedOk = true;

            for (int i = 0; i < overflow; i++)
            {
                bool used = motion.TryStep(PredKey(1u, (uint)(1000 + i)), spec, State(P(0f, 1f, 0f)), 1000,
                                           P(10f, 1f, 0f), P(10f, 0f, 0f), 0f,
                                           out position, out velocity, out yaw);

                if (used) { accepted++; }
                else
                {
                    refused++;

                    // 反例的语义必须干净：不是 true+直线，也不是"回填了一个向前飞的位置"。
                    if (!SamePM(position, P(0f, 1f, 0f)) || velocity != PMVector3.Zero)
                    {
                        lastRefusedOk = false;
                    }
                }
            }

            PMUnityProjectileMotionStats full = motion.GetStats();

            Check("J1 正：前 256 个受阻步全部被接受（accepted==live），表长封顶 == 上限，且无一条被淘汰",
                accepted == cap && motion.StopMarkCount == cap && full.ForfeitedStopMarks == 0,
                "accepted=" + accepted + " live=" + motion.StopMarkCount + " cap=" + cap
                + " forfeited=" + full.ForfeitedStopMarks);

            Check("J2 反：表满后需要记账的步**显式返回 false**（fail closed），且输出回填的就是安全值（停在原位、零速度）",
                refused == 44 && lastRefusedOk,
                "refused=" + refused + " lastRefusedClean=" + lastRefusedOk);

            Check("J3 表满后 TryStep=false 被计数（StopMarkCapacityFailures=44）且 BlockedSteps 如实记到每步",
                full.StopMarkCapacityFailures == 44 && full.BlockedSteps == overflow,
                "capacityFailures=" + full.StopMarkCapacityFailures + " blocked=" + full.BlockedSteps);

            // 关键正向断言：表满**没有**把任何已接受的标记挤掉（最旧的那条仍可逐字段消费）。
            PMVector3 oldestStop;
            bool oldestStillThere = motion.TryStop(PredKey(1u, 1000u), spec, State(P(0f, 1f, 0f)), 1.0,
                                                   out oldestStop);

            PMVector3 newestStop;
            bool newestStillThere = motion.TryStop(PredKey(1u, (uint)(1000 + cap - 1)), spec,
                                                  State(P(0f, 1f, 0f)), 1.0, out newestStop);

            Check("J4 已接受的标记一条都没丢：最旧那条可被 TryStop 逐字段消费（不是淘汰最旧）",
                oldestStillThere && Near(oldestStop.X, WallContactX - Skin, 2e-3f),
                "oldestStillThere=" + oldestStillThere + " stop=" + V(oldestStop));

            Check("J5 最新的那条同样可消费（保住的不是只有尾巴）",
                newestStillThere && Near(newestStop.X, WallContactX - Skin, 2e-3f),
                "newestStillThere=" + newestStillThere + " stop=" + V(newestStop));

            Check("J6 被拒的那一步没有留下标记（TryStop=false）⇒ 不会被误读成『撞墙了』",
                !motion.TryStop(PredKey(1u, (uint)(1000 + cap)), spec, State(P(0f, 1f, 0f)), 1.0,
                                out newestStop),
                "refusedKeyHasMark=False");

            // 表满时**无阻挡**的步不受影响：不需要记账就不该失败。
            int liveBeforeFree = motion.StopMarkCount;

            bool freeWhileFull = motion.TryStep(PredKey(1u, 9000u), spec, State(P(0f, 6f, 0f)), 1000,
                                                P(10f, 6f, 0f), P(10f, 0f, 0f), 0f,
                                                out position, out velocity, out yaw);

            Check("J7 表满时无阻挡的步仍返回 true（fail closed 只发生在『确实需要记账』的步上），且不改变表长",
                freeWhileFull && SamePM(position, P(10f, 6f, 0f)) && velocity == P(10f, 0f, 0f)
                && motion.StopMarkCount == liveBeforeFree,
                "used=" + freeWhileFull + " live=" + motion.StopMarkCount + " before=" + liveBeforeFree);

            // Clear() 是显式的清空通道，也是从饱和中恢复的唯一手段。
            motion.Clear();

            bool afterClearStep = motion.TryStep(PredKey(1u, 9100u), spec, State(P(0f, 1f, 0f)), 1000,
                                                 P(10f, 1f, 0f), P(10f, 0f, 0f), 0f,
                                                 out position, out velocity, out yaw);
            PMVector3 afterClearStop;
            bool afterClearStopFlag = motion.TryStop(PredKey(1u, 9100u), spec, State(P(0f, 1f, 0f)), 1.0,
                                                     out afterClearStop);

            Check("J8 Clear() 清空未消费标记并**恢复记录能力**（清空后新 key 的 TryStep=true 且可消费）",
                motion.StopMarkCount == 0 && afterClearStep && afterClearStopFlag
                && Near(afterClearStop.X, WallContactX - Skin, 2e-3f),
                "live=" + motion.StopMarkCount + " step=" + afterClearStep + " stop=" + afterClearStopFlag);

            // Dispose 同样清空，且之后一律 fail closed（返回 false，不抛异常打断宿主循环）。
            motion.Dispose();
            bool disposedStep = motion.TryStep(PredKey(1u, 9200u), spec, State(P(0f, 1f, 0f)), 100,
                                               P(1f, 1f, 0f), P(10f, 0f, 0f), 0f,
                                               out position, out velocity, out yaw);
            PMVector3 disposedStop;
            bool disposedTryStop = motion.TryStop(PredKey(1u, 9200u), spec, State(P(0f, 1f, 0f)), 1.0,
                                                  out disposedStop);
            motion.Dispose();
            PMUnityProjectileMotionStats disposed = motion.GetStats();

            Check("J9 Dispose 幂等（清空标记）且释放后 TryStep/TryStop 一律 false（不抛异常）",
                motion.Disposed && motion.StopMarkCount == 0 && !disposedStep && !disposedTryStop
                && disposed.DisposedCalls == 2,
                "disposed=" + motion.Disposed + " live=" + motion.StopMarkCount
                + " step=" + disposedStep + " stop=" + disposedTryStop
                + " disposedCalls=" + disposed.DisposedCalls);
        }

        // ================================================================== K. 表现层

        private static void SectionK()
        {
            Section("K. 表现层（C2）：DS 禁止 / 写入面最小 / 停止无副作用 / Dispose 清理");

            // ---- K1：DS 禁止创建（判定走 PMNetRuntime.IsDedicatedServer）----
            PMNetRuntime.Initialize(PMNetLaunchOptions.Parse(new[] { "-server", "-port", "7101" }));
            int createdBefore = GameObject.CreatedTotal;

            bool dsThrew = false;
            try
            {
                new PMUnityProjectilePresentation("ds");
            }
            catch (InvalidOperationException)
            {
                dsThrew = true;
            }

            bool factoryThrew = false;
            try
            {
                PMUnityProjectilePresentation.Create("ds2");
            }
            catch (InvalidOperationException)
            {
                factoryThrew = true;
            }

            Check("K1 DS 模式：构造与工厂都抛 InvalidOperationException，且**一个对象都没建**",
                PMNetRuntime.IsDedicatedServer && dsThrew && factoryThrew
                && GameObject.CreatedTotal == createdBefore,
                "isDs=" + PMNetRuntime.IsDedicatedServer + " ctor=" + dsThrew + " factory=" + factoryThrew
                + " created=" + (GameObject.CreatedTotal - createdBefore));

            // ---- 切回客户端模式 ----
            PMNetRuntime.Initialize(PMNetLaunchOptions.Parse(new string[0]));
            Check("K2 客户端模式：IsDedicatedServer=false（唯一身份来源是 PMNetRuntime）",
                !PMNetRuntime.IsDedicatedServer, "isDs=" + PMNetRuntime.IsDedicatedServer);

            // ---- 不碰摄像机：先放一台"用户相机" ----
            Camera userCamera = new Camera();
            userCamera.name = "UserMainCamera";
            Camera.main = userCamera;

            int aliveBefore = GameObject.AliveCount;
            int materialsBefore = Material.CreatedTotal;

            PMUnityProjectilePresentation view = new PMUnityProjectilePresentation("p1");
            GameObject root = view.Root;
            Transform body = view.Body;

            Check("K3 自建层级：根节点名字带诊断标记 + Body 子节点存在",
                root != null && body != null && root.name.Contains("p1")
                && root.name.Contains("diagnostic-sphere-not-hero-art")
                && root.name.Contains("PMUnityProjectilePresentation")
                && body.gameObject.name == "Body"
                && ReferenceEquals(body.parent, root.transform),
                "root=" + (root == null ? "<null>" : root.name));

            bool noCollider = body.GetComponent<Collider>() == null;
            bool noRigidbody = body.GetComponent<Rigidbody>() == null;
            bool noScript = true;
            for (int i = 0; i < body.gameObject.Components.Count; i++)
            {
                Component component = body.gameObject.Components[i];
                if (component is MonoBehaviour) { noScript = false; }
                if (component is Collider) { noCollider = false; }
                if (component is Rigidbody) { noRigidbody = false; }
            }

            Check("K4 占位球不带 Collider / Rigidbody / 旧脚本（primitive 自带 Collider 已禁用并销毁）",
                noCollider && noRigidbody && noScript && body.GetComponent<Renderer>() != null,
                "collider=" + noCollider + " rigidbody=" + noRigidbody + " script=" + noScript);

            // ---- Apply 的写入面 ----
            PMProjectileState state = new PMProjectileState();
            state.Position = P(1f, 2f, 3f);
            state.PreviousPosition = P(9f, 9f, 9f);
            state.SpawnPosition = P(8f, 8f, 8f);
            state.Velocity = P(7f, 7f, 7f);
            state.Yaw = 90f;

            PMProjectileSpec spec = Spec(0.25f, true, true);
            view.Apply(state, spec);

            Check("K5 位置唯一来源 = state.Position（SpawnPosition/PreviousPosition/Velocity 不被采信）",
                SameVec(root.transform.position, new Vector3(1f, 2f, 3f)),
                "position=" + root.transform.position);

            Check("K6 绕 Y 朝向与尺寸：yaw=90 写入旋转；radius=0.25 ⇒ 球缩放 = 0.25/0.5",
                Near(root.transform.rotation.eulerAngles.y, 90f, 1e-6f)
                && Near(body.localScale.x, 0.5f, 1e-6f)
                && Near(body.localScale.y, 0.5f, 1e-6f)
                && Near(body.localScale.z, 0.5f, 1e-6f),
                "yaw=" + root.transform.rotation.eulerAngles.y.ToString("R")
                + " scale=" + body.localScale);

            // ---- hidden / stopped ----
            PMProjectileState hiddenState = new PMProjectileState();
            hiddenState.Position = P(1f, 2f, 3f);
            hiddenState.Hidden = true;
            view.Apply(hiddenState, spec);
            bool hiddenWorks = !root.activeSelf && view.Hidden;

            PMProjectileState shownState = new PMProjectileState();
            shownState.Position = P(1f, 2f, 3f);
            view.Apply(shownState, spec);
            bool shownWorks = root.activeSelf && !view.Hidden;

            PMProjectileState stoppedState = new PMProjectileState();
            stoppedState.Position = P(4f, 5f, 6f);
            stoppedState.Stopped = true;
            view.Apply(stoppedState, Spec(0.25f, true, true));
            bool stopHides = !root.activeSelf && view.Hidden;

            view.Apply(stoppedState, Spec(0.25f, true, false));
            bool stopKeepsVisible = root.activeSelf && !view.Hidden;

            Check("K7 Hidden 生效；Stopped + HideOnStop 时隐藏，HideOnStop=false 时保持可见",
                hiddenWorks && shownWorks && stopHides && stopKeepsVisible,
                "hidden=" + hiddenWorks + " shown=" + shownWorks + " stopHides=" + stopHides
                + " stopKeeps=" + stopKeepsVisible);

            // ---- 停止无二次副作用 ----
            int objectsBeforeRepeat = GameObject.CreatedTotal;
            int componentsBeforeRepeat = body.gameObject.Components.Count;
            Vector3 posBeforeRepeat = root.transform.position;
            bool activeBeforeRepeat = root.activeSelf;

            view.Apply(stoppedState, Spec(0.25f, true, false));
            view.Apply(stoppedState, Spec(0.25f, true, false));

            Check("K8 停止态重复 Apply 无二次副作用：不建对象/不加组件/位置与可见性不变",
                GameObject.CreatedTotal == objectsBeforeRepeat
                && body.gameObject.Components.Count == componentsBeforeRepeat
                && SameVec(root.transform.position, posBeforeRepeat)
                && root.activeSelf == activeBeforeRepeat
                && view.ApplyCount == 7,
                "created=" + GameObject.CreatedTotal + " components=" + body.gameObject.Components.Count
                + " applyCount=" + view.ApplyCount);

            // ---- 材料只克隆一次 ----
            Renderer renderer = body.GetComponent<Renderer>();
            Check("K9 材料只克隆一次（构造期），Apply 不重建；renderer 共享的就是那一个实例",
                Material.CreatedTotal - materialsBefore == 1
                && view.Material != null
                && renderer != null && ReferenceEquals(renderer.sharedMaterial, view.Material),
                "materialsCreated=" + (Material.CreatedTotal - materialsBefore));

            Check("K10 不触碰摄像机（Camera.main 仍是用户那台，且未被销毁）",
                ReferenceEquals(Camera.main, userCamera) && !userCamera.Destroyed,
                "cameraDestroyed=" + userCamera.Destroyed);

            // ---- Dispose ----
            Material viewMaterial = view.Material;
            view.Dispose();

            bool cleaned = root.Destroyed && viewMaterial.Destroyed && view.Root == null
                           && view.Body == null && view.Material == null && view.Disposed;

            bool applyThrew = false;
            try
            {
                view.Apply(stoppedState, spec);
            }
            catch (ObjectDisposedException)
            {
                applyThrew = true;
            }

            view.Dispose();

            Check("K11 Dispose 清理自建物体 + 自建材质；释放后 Apply 抛 ObjectDisposedException；重复 Dispose 无副作用",
                cleaned && applyThrew, "cleaned=" + cleaned + " applyThrew=" + applyThrew);

            Check("K12 Dispose 后不残留自建对象（AliveCount 回到创建前）",
                GameObject.AliveCount == aliveBefore,
                "aliveBefore=" + aliveBefore + " aliveAfter=" + GameObject.AliveCount);
        }

        // ================================================================== L. 表现层参数校验

        private static void SectionL()
        {
            Section("L. 表现层参数校验：非法输入显式失败，绝不把坏值写进 Transform");

            PMUnityProjectilePresentation view = new PMUnityProjectilePresentation("l1");
            GameObject root = view.Root;
            int createdBefore = GameObject.CreatedTotal;

            PMProjectileState good = new PMProjectileState();
            good.Position = P(1f, 1f, 1f);
            good.Yaw = 0f;

            PMProjectileSpec goodSpec = DefaultSpec();

            bool nullState = false;
            try { view.Apply(null, goodSpec); }
            catch (ArgumentNullException) { nullState = true; }

            bool nullSpec = false;
            try { view.Apply(good, null); }
            catch (ArgumentNullException) { nullSpec = true; }

            PMProjectileState nanPos = new PMProjectileState();
            nanPos.Position = P(float.NaN, 1f, 1f);
            bool nanPosition = false;
            try { view.Apply(nanPos, goodSpec); }
            catch (ArgumentException) { nanPosition = true; }

            PMProjectileState nanYaw = new PMProjectileState();
            nanYaw.Position = P(1f, 1f, 1f);
            nanYaw.Yaw = float.NaN;
            bool badYaw = false;
            try { view.Apply(nanYaw, goodSpec); }
            catch (ArgumentException) { badYaw = true; }

            bool badRadius = false;
            try { view.Apply(good, Spec(0f, true, true)); }
            catch (ArgumentException) { badRadius = true; }

            bool negRadius = false;
            try { view.Apply(good, Spec(-0.5f, true, true)); }
            catch (ArgumentException) { negRadius = true; }

            Check("L1 null state/spec 抛 ArgumentNullException；NaN 位置/朝向、非正半径抛 ArgumentException",
                nullState && nullSpec && nanPosition && badYaw && badRadius && negRadius,
                "nullState=" + nullState + " nullSpec=" + nullSpec + " nanPos=" + nanPosition
                + " badYaw=" + badYaw + " badRadius=" + badRadius + " negRadius=" + negRadius);

            Check("L2 全部失败调用都没有建对象、也没有污染根节点位置",
                GameObject.CreatedTotal == createdBefore
                && SameVec(root.transform.position, new Vector3(0f, 0f, 0f)),
                "created=" + (GameObject.CreatedTotal - createdBefore) + " position=" + root.transform.position);

            view.Apply(good, goodSpec);
            Check("L3 失败之后合法输入仍然正常工作（对象未被破坏）",
                SameVec(root.transform.position, new Vector3(1f, 1f, 1f)) && view.ApplyCount == 1,
                "position=" + root.transform.position + " applyCount=" + view.ApplyCount);

            // ---- L4：非法输入**不半写**（独立复核项）----
            // 最具破坏性的半写形态：位置合法但朝向/半径非法。若实现是"先写位置、后校验朝向"，
            // 就会把根节点停在 (5,5,5)/(6,6,6) 这种"新位置的幽灵"上（既非旧状态也非新状态）。
            view.Apply(good, goodSpec);

            Vector3 posBefore = root.transform.position;
            Quaternion rotBefore = root.transform.rotation;
            Vector3 scaleBefore = view.Body.localScale;
            bool hiddenBefore = view.Hidden;
            int applyCountBefore = view.ApplyCount;
            PMVector3 lastPosBefore = view.LastPosition;

            PMProjectileState halfWriteYaw = new PMProjectileState();
            halfWriteYaw.Position = P(5f, 5f, 5f);
            halfWriteYaw.Yaw = float.NaN;

            PMProjectileState halfWriteRadius = new PMProjectileState();
            halfWriteRadius.Position = P(6f, 6f, 6f);

            PMProjectileState halfWriteHidden = new PMProjectileState();
            halfWriteHidden.Position = P(7f, 7f, 7f);
            halfWriteHidden.Hidden = true;

            bool threwYaw = false;
            try { view.Apply(halfWriteYaw, goodSpec); }
            catch (ArgumentException) { threwYaw = true; }

            bool threwRadius = false;
            try { view.Apply(halfWriteRadius, Spec(0f, true, true)); }
            catch (ArgumentException) { threwRadius = true; }

            bool threwHidden = false;
            try { view.Apply(halfWriteHidden, Spec(float.PositiveInfinity, true, true)); }
            catch (ArgumentException) { threwHidden = true; }

            Check("L4 非法输入**不半写**：失败调用后位置/旋转/尺寸/可见性/ApplyCount/LastPosition 逐字段未变",
                threwYaw && threwRadius && threwHidden
                && SameVec(root.transform.position, posBefore)
                && Near(root.transform.rotation.eulerAngles.y, rotBefore.eulerAngles.y, 1e-6f)
                && SameVec(view.Body.localScale, scaleBefore)
                && view.Hidden == hiddenBefore && view.ApplyCount == applyCountBefore
                && SamePM(view.LastPosition, lastPosBefore),
                "threwYaw=" + threwYaw + " threwRadius=" + threwRadius + " threwHidden=" + threwHidden
                + " position=" + root.transform.position + " applyCount=" + view.ApplyCount);

            view.Dispose();
        }

        // ================================================================== M. 构造失败清理

        private static void SectionM()
        {
            Section("M. 表现层：**构造中途失败要清掉已自建的对象与材质** + 不改源材质（独立复核新增）");

            // 表现层只在客户端存在：先确保切回客户端模式。
            PMNetRuntime.Initialize(PMNetLaunchOptions.Parse(new string[0]));

            // ---- M1：primitive 创建失败 ⇒ 已建的根节点也必须被清掉 ----
            Material.ResetHooks();
            Renderer.ThrowOnSharedMaterialSet = false;

            int aliveBefore = GameObject.AliveCount;
            int materialsBefore = Material.CreatedTotal;

            GameObject.FailNextCreatePrimitive = true;
            bool primitiveThrew = false;
            try
            {
                new PMUnityProjectilePresentation("ctor-fail-primitive");
            }
            catch (InvalidOperationException)
            {
                primitiveThrew = true;
            }
            finally
            {
                GameObject.FailNextCreatePrimitive = false;
            }

            Check("M1 primitive 创建失败：构造函数抛异常且**不留任何自建对象/材质**（AliveCount 回到创建前）",
                primitiveThrew && GameObject.AliveCount == aliveBefore
                && Material.CreatedTotal == materialsBefore,
                "threw=" + primitiveThrew + " alive=" + GameObject.AliveCount + " before=" + aliveBefore
                + " materialsAlive=" + (Material.CreatedTotal - materialsBefore));

            // ---- M2：克隆材质自身构造失败（在创建任何东西之前抛）⇒ 根/子节点也得清 ----
            Material.ResetHooks();
            Material.ThrowOnClone = true;

            aliveBefore = GameObject.AliveCount;
            materialsBefore = Material.CreatedTotal;

            bool cloneThrew = false;
            try
            {
                new PMUnityProjectilePresentation("ctor-fail-clone");
            }
            catch (InvalidOperationException)
            {
                cloneThrew = true;
            }
            finally
            {
                Material.ThrowOnClone = false;
            }

            Check("M2 克隆材质构造失败：根节点与 primitive 子节点都被清掉，且一个多余材质都没留下",
                cloneThrew && GameObject.AliveCount == aliveBefore
                && Material.CreatedTotal == materialsBefore && Material.LastCreated == null,
                "threw=" + cloneThrew + " alive=" + GameObject.AliveCount + " before=" + aliveBefore
                + " materialsCreated=" + (Material.CreatedTotal - materialsBefore));

            // ---- M3：克隆成功、但挂到 renderer 时失败 ⇒ 已创建的克隆材质也必须被销毁 ----
            Material.ResetHooks();
            Renderer.ThrowOnSharedMaterialSet = true;

            aliveBefore = GameObject.AliveCount;
            materialsBefore = Material.CreatedTotal;

            bool attachThrew = false;
            try
            {
                new PMUnityProjectilePresentation("ctor-fail-attach");
            }
            catch (InvalidOperationException)
            {
                attachThrew = true;
            }
            finally
            {
                Renderer.ThrowOnSharedMaterialSet = false;
            }

            Material orphanClone = Material.LastCreated;

            Check("M3 克隆成功但挂载失败：对象与**已创建的克隆材质**都被清理（无半成品泄漏）",
                attachThrew && GameObject.AliveCount == aliveBefore
                && Material.CreatedTotal == materialsBefore + 1
                && orphanClone != null && orphanClone.IsClone && orphanClone.Destroyed,
                "threw=" + attachThrew + " alive=" + GameObject.AliveCount + " before=" + aliveBefore
                + " cloneDestroyed=" + (orphanClone != null && orphanClone.Destroyed));

            // ---- M4：正常构造与 Apply **不改源材质**（primitive 内建共享材质只被读）----
            Material.ResetHooks();

            Color sourceColorBefore = Material.BuiltinDefault.color;
            int aliveBeforeOk = GameObject.AliveCount;
            int materialsBeforeOk = Material.CreatedTotal;

            PMUnityProjectilePresentation view = new PMUnityProjectilePresentation("m4");
            PMProjectileState state = new PMProjectileState();
            state.Position = P(1f, 1f, 1f);
            view.Apply(state, DefaultSpec());

            Color sourceColorAfter = Material.BuiltinDefault.color;
            Material ownMaterial = view.Material;

            bool placeholderColorOk = ownMaterial != null
                && Near(ownMaterial.color.r, PMUnityProjectilePresentation.PlaceholderColor.r, 1e-6f)
                && Near(ownMaterial.color.g, PMUnityProjectilePresentation.PlaceholderColor.g, 1e-6f)
                && Near(ownMaterial.color.b, PMUnityProjectilePresentation.PlaceholderColor.b, 1e-6f);

            bool sourceUntouched = Near(sourceColorBefore.r, sourceColorAfter.r, 1e-6f)
                && Near(sourceColorBefore.g, sourceColorAfter.g, 1e-6f)
                && Near(sourceColorBefore.b, sourceColorAfter.b, 1e-6f)
                && Near(sourceColorBefore.a, sourceColorAfter.a, 1e-6f)
                && sourceColorAfter.r == Color.white.r && sourceColorAfter.g == Color.white.g
                && sourceColorAfter.b == Color.white.b;

            Check("M4 占位材质是克隆体（改了占位色），**源材质（primitive 内建）未被修改**",
                ownMaterial != null && ownMaterial.IsClone && placeholderColorOk && sourceUntouched
                && !ReferenceEquals(ownMaterial, Material.BuiltinDefault)
                && Material.CreatedTotal - materialsBeforeOk == 1,
                "isClone=" + (ownMaterial != null && ownMaterial.IsClone) + " cloned="
                + (Material.CreatedTotal - materialsBeforeOk) + " source=" + sourceColorAfter);

            view.Dispose();

            Check("M5 正常路径 Dispose 后不残留自建对象（AliveCount 回到 M4 构造前）",
                GameObject.AliveCount == aliveBeforeOk && ownMaterial.Destroyed,
                "alive=" + GameObject.AliveCount + " before=" + aliveBeforeOk
                + " materialDestroyed=" + ownMaterial.Destroyed);

            Material.ResetHooks();
            Renderer.ThrowOnSharedMaterialSet = false;
            GameObject.FailNextCreatePrimitive = false;
        }
    }
}
