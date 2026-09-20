// ============================================================================
//  PMR4CollisionAdapterTest —— 受控 Unity 查询双件下重跑真实适配器与真实模型
// ============================================================================
//
//  为什么有这个工程（它对应哪次真实缺陷）：
//    2026-09-20 的真实 Unity Play 运行（隔离 runtime-local-physics-scene）出现 6 项失败：
//      [FAIL] sweepBlockedByWall        :: fraction=0 normal=(1,0,0)（期望 ≈0.70）
//      [FAIL] startOverlapPathExercised :: StartOverlapResolutions=0（期望 ≥1）
//      [FAIL] allowlistIsolatesOtherColliders :: 白名单/无白名单都 fraction=0
//      [FAIL] defaultWorldIsolation     :: 隔离世界无白名单扫掠 fraction=0 normal=(0,0,-1)
//      [FAIL] walkStopsAtWall           :: minX=3 finalX=3.124991（期望 minX≈0.9）
//      [FAIL] jumpLandsBackOnFloor      :: maxY=1（期望 >1.5）
//    六项是**同一个根因**：PhysX 的 CapsuleCast 对"起点已接触/重叠"的 Collider 返回
//    `distance=0` 且 `normal = -dir`（贴地 capsule 的足底正好在 floor 顶面 y=0 时，
//    任意方向的扫掠都会拿到 floor 的这条 0 距离命中）。适配器当时只做"法线与运动方向相反"的
//    过滤，而 `-dir` 恒满足该判据 ⇒ 贴地时水平/竖直向上扫掠全被判 fraction=0：
//      · 水平 ⇒ 每帧被"脚下地面"挡住，角色原地不动（walkStopsAtWall）；
//      · 起跳 ⇒ 竖直向上的位移被同一条假命中抵消（jumpLandsBackOnFloor）；
//      · 墙前 ⇒ 真正的墙 TOI（2.1/3=0.7）被 0 距离假命中抢走（sweepBlockedByWall / allowlist）；
//      · 起点嵌墙 ⇒ Cast 分支先返回，Overlap 分支永不执行（startOverlapPathExercised=0）。
//
//  本工程做什么：
//    · 用**受控 Unity 查询双件**（Program.cs 尾部的 UnityEngine 命名空间）复现上述已观测语义；
//    · 编**真实源码** PMUnityMoverCollisionQuery.cs（适配器）与 PMMoverModel.cs（模型），
//      不复制、不快照、不打桩替身替代被验证对象；
//    · 逐条钉住：起点正穿透 / 仅接触 / 有效前向 TOI 的三分，以及
//      「贴地水平可走、向下仍被地面阻挡、墙前 0.7、触墙向内阻挡/朝外与沿墙可走、
//        真嵌墙给有效推出法线并计数、不许只返回首命中、不许缩小尺寸漏墙」。
//
//  **它不证明什么**（边界，禁止被读成"假验物理"）：
//    · 双件是我自己实现的轴对齐 Box 语义模型，**不是 PhysX 实机**，也不是 Unity；
//      它复现的是"已观测到的 distance=0 / normal=-dir 语义 + 一阶 TOI"，用来把适配器逻辑钉死；
//    · 真实 PhysX 上的最终结论仍需用户在 Unity 内跑
//      Client/Assets/Editor/PMR4UnityValidation.cs（菜单 Tools/PMR4/验证 Unity 碰撞适配（真实 PhysX））；
//    · 旋转 Box / Mesh / Terrain 等非轴对齐 Box：双件用 AABB(bounds) 近似（**有意简化**），
//      适配器侧对应的"保守分类边界"在 D5 单独断言（宁可早挡、绝不漏墙），不声称通用形状精确。
//
//  运行：dotnet build Tools/PMR4CollisionAdapterTest -c Release
//        dotnet Tools/PMR4CollisionAdapterTest/bin/Release/net8.0/PMR4CollisionAdapterTest.dll
//  退出码：0 = 全部通过；1 = 存在失败
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using PMNet;
using PMNet.Mover;
using PMNet.Prediction;
using PMNet.Unity;
using UnityEngine;

namespace PMR4CollisionAdapterTest
{
    internal static class Program
    {
        // ------------------------------------------------------------------
        //  冻结布局（与 PMR3TestScene / PMR4UnityValidation 同值，避免"两套场景数值"）
        // ------------------------------------------------------------------

        private static readonly Vector3 FloorCenter = new Vector3(0f, -0.5f, 0f);
        private static readonly Vector3 FloorSize = new Vector3(40f, 1f, 40f);
        private static readonly Vector3 WallCenter = new Vector3(0f, 1f, 0f);
        private static readonly Vector3 WallSize = new Vector3(1f, 2f, 8f);

        private const float Radius = PMMoverDefaults.CapsuleRadiusMeters;          // 0.4
        private const float HalfHeight = PMMoverDefaults.CapsuleHalfHeightMeters;  // 1.0
        private const float CapsuleOffset = HalfHeight - Radius;                   // 0.6（两端球心偏移）

        private const int WorldVersion = 0x52334202;
        private const int AllLayers = ~0;

        /// <summary>墙内推到墙外的正确最小轴（墙 x∈[-0.5,0.5] 比 z∈[-4,4] 近），用于断言"有效推出法线"。</summary>
        private const float WallEscapeAxisDot = 0.9f;

        private static int _checks;
        private static int _failed;

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

        // ==================================================================
        //  入口
        // ==================================================================

        private static int Main()
        {
            Console.WriteLine("===== PMR4CollisionAdapterTest：PhysX 零距离命中语义下的适配器回归（受控双件） =====");
            Console.WriteLine("边界：双件是**受控语义模型**（复现已观测的 distance=0 / normal=-dir），不是 PhysX 实机；");
            Console.WriteLine("      真实 PhysX 结论仍需 Unity 菜单 Tools/PMR4/验证 Unity 碰撞适配（真实 PhysX）。");

            SectionA();
            SectionB();
            SectionC();
            SectionD();
            SectionE();
            SectionF();
            SectionG();
            SectionH();
            SectionI();

            Console.WriteLine();
            Console.WriteLine("结果：" + (_failed == 0 ? "全部通过" : "存在失败")
                              + "（checks=" + _checks + " failed=" + _failed + "）");
            Console.WriteLine("边界：本工程不代替真实 PhysX / Unity 运行；"
                              + "隔离世界与真实物理世界的对照证据由 Editor 菜单提供。");

            return _failed == 0 ? 0 : 1;
        }

        // ==================================================================
        //  A. 受控双件自检：复现已观测的 PhysX 零距离语义
        // ==================================================================

        private static void SectionA()
        {
            Section("A. 受控双件自检：复现真实 Play 观测到的零距离语义（不是 PhysX 实机）");

            PhysicsScene scene = CreateStandardScene(out Collider floor, out Collider wall);
            RaycastHit[] buffer = new RaycastHit[PMUnityMoverCollisionQuery.HitBufferCapacity];

            Vector3 top = new Vector3(3f, HalfHeight + CapsuleOffset, 0f);
            Vector3 bottom = new Vector3(3f, HalfHeight - CapsuleOffset, 0f);

            // 贴地（足底 == floor 顶面 y=0）时的水平扫掠 —— 真实 Play 里正是这里出的问题。
            int count = scene.CapsuleCast(top, bottom, Radius, new Vector3(0f, 0f, 1f),
                                          buffer, 2f, AllLayers, QueryTriggerInteraction.Ignore);

            Check("A1 贴地水平扫掠返回 floor 的 0 距离命中（已观测语义）",
                count == 1 && buffer[0].distance == 0f && ReferenceEquals(buffer[0].collider, floor),
                "count=" + count + " distance=" + buffer[0].distance
                + " collider=" + (buffer[0].collider == null ? "null" : buffer[0].collider.name));

            // 原始法线 = -dir：恰好通过"法线与运动方向相反"的朴素过滤 —— 这就是根因。
            float dot = buffer[0].normal.z * 1f;
            Check("A2 该命中原始法线 = -dir（原始法线不可信，朴素反向过滤必然误接纳）",
                Near(buffer[0].normal.x, 0f, 1e-6f) && Near(buffer[0].normal.y, 0f, 1e-6f)
                && Near(buffer[0].normal.z, -1f, 1e-6f) && dot <= -0.999f,
                "normal=" + buffer[0].normal + " dot(normal,dir)=" + dot.ToString("R"));

            // 悬空 0.3：正距离 TOI（真实 PhysX 在"有间隙"时才给可信 TOI）。
            Vector3 floating = new Vector3(3f, HalfHeight + 0.3f + CapsuleOffset, 0f);
            int floatingCount = scene.CapsuleCast(floating, floating - new Vector3(0f, 2f * CapsuleOffset, 0f),
                                                  Radius, new Vector3(0f, -1f, 0f), buffer, 0.5f,
                                                  AllLayers, QueryTriggerInteraction.Ignore);
            Check("A3 悬空 0.3m 向下扫掠返回正距离 ≈0.3 的 TOI",
                floatingCount == 1 && Near(buffer[0].distance, 0.3f, 1e-4f)
                && Near(buffer[0].normal.y, 1f, 1e-6f),
                "count=" + floatingCount + " distance=" + buffer[0].distance.ToString("R")
                + " normal=" + buffer[0].normal);

            // 起点嵌墙：同样是 distance=0 + normal=-dir（穿透与"仅接触"在原始命中上**无法区分**）。
            int embeddedCount = scene.CapsuleCast(new Vector3(0f, HalfHeight + CapsuleOffset, 0f),
                                                  new Vector3(0f, HalfHeight - CapsuleOffset, 0f),
                                                  Radius, new Vector3(0f, 0f, 1f), buffer, 3f,
                                                  AllLayers, QueryTriggerInteraction.Ignore);
            Check("A4 起点嵌墙时原始命中也是 distance=0（穿透/接触无法由原始命中区分）",
                embeddedCount >= 1 && buffer[0].distance == 0f && Near(buffer[0].normal.z, -1f, 1e-6f),
                "count=" + embeddedCount + " distance=" + buffer[0].distance
                + " normal=" + buffer[0].normal);

            // 饱和：候选数 > 容量时返回恰好容量（Editor 的 saturationFailsExplicitly 依赖此语义）。
            PhysicsScene saturated = new PhysicsScene();
            for (int i = 0; i < PMUnityMoverCollisionQuery.HitBufferCapacity + 8; i++)
            {
                saturated.AddBox("Slab" + i, new Vector3(10f, 1f, 1f + i * 0.05f), new Vector3(0.4f, 2f, 0.2f));
            }

            int saturatedCount = saturated.CapsuleCast(new Vector3(10f, HalfHeight + CapsuleOffset, 0f),
                                                       new Vector3(10f, HalfHeight - CapsuleOffset, 0f),
                                                       Radius, new Vector3(0f, 0f, 1f), buffer, 6f,
                                                       AllLayers, QueryTriggerInteraction.Ignore);
            Check("A5 候选数超容量时 double 返回恰好容量（饱和语义）",
                saturatedCount == PMUnityMoverCollisionQuery.HitBufferCapacity,
                "count=" + saturatedCount + " capacity=" + PMUnityMoverCollisionQuery.HitBufferCapacity);

            Info("双件统计：capsuleCast=" + scene.CapsuleCastCalls + " overlapCapsule=" + scene.OverlapCapsuleCalls);
            Info("（双件语义模型：gap<=1e-5 ⇒ distance=0 + normal=-dir；有正间隙且接近时才给 TOI）");
        }

        // ==================================================================
        //  B. 贴地可移动（核心回归：修复前 fraction=0 走不动）
        // ==================================================================

        private static void SectionB()
        {
            Section("B. 贴地（足底正好在 floor 顶面）必须仍然可移动");

            PhysicsScene scene = CreateStandardScene(out Collider floor, out Collider wall);
            PMUnityMoverCollisionQuery query =
                new PMUnityMoverCollisionQuery(scene, AllLayers, WorldVersion, new Collider[] { floor, wall });

            PMMoverHit forward = query.Sweep(new PMVector3(3f, HalfHeight, 0f), new PMVector3(0f, 0f, 2f),
                                             Radius, HalfHeight);
            Check("B1 贴地沿 +Z 扫掠不阻挡（核心回归）", !forward.Blocking,
                forward.ToString() + "（修复前 fraction=0 normal=(0,0,-1)）");

            PMMoverHit thirdDimension = query.Sweep(new PMVector3(10f, HalfHeight, 0f), new PMVector3(0f, 0f, 5f),
                                                    Radius, HalfHeight);
            Check("B2 远离墙处贴地扫掠不阻挡", !thirdDimension.Blocking, thirdDimension.ToString());

            PMMoverHit tiny = query.Sweep(new PMVector3(3f, HalfHeight, 0f), new PMVector3(0f, 0f, 0.02f),
                                          Radius, HalfHeight);
            Check("B3 贴地小位移扫掠不阻挡（小位移不得被地面吞掉）", !tiny.Blocking, tiny.ToString());

            PMMoverHit zero = query.Sweep(new PMVector3(3f, HalfHeight, 0f), PMVector3.Zero, Radius, HalfHeight);
            Check("B4 零 delta 不阻挡且 fraction=1 / normal=0",
                !zero.Blocking && zero.Fraction == 1f && zero.Normal.LengthSquared == 0f, zero.ToString());

            // 贴地 + 向上（起跳）也不得被地面挡住。
            PMMoverHit up = query.Sweep(new PMVector3(3f, HalfHeight, 0f), new PMVector3(0f, 0.06f, 0f),
                                        Radius, HalfHeight);
            Check("B5 贴地沿 +Y 扫掠（起跳）不阻挡", !up.Blocking,
                up.ToString() + "（修复前起跳位移被地板假命中抵消）");

            Info("扫掠后计数：" + query.GetStats());
        }

        // ==================================================================
        //  C. 墙：前向 0.7 / 向内阻挡 / 朝外与沿墙可走 / 高速不漏 / 贴墙小位移
        // ==================================================================

        private static void SectionC()
        {
            Section("C. 墙语义：有效前向 TOI、向内阻挡、朝外/沿墙可走、高速不漏墙");

            PhysicsScene scene = CreateStandardScene(out Collider floor, out Collider wall);
            PMUnityMoverCollisionQuery query =
                new PMUnityMoverCollisionQuery(scene, AllLayers, WorldVersion, new Collider[] { floor, wall });

            // 期望 fraction = ((3-0.4)-0.5)/3 = 2.1/3 = 0.7
            PMMoverHit wallHit = query.Sweep(new PMVector3(3f, HalfHeight, 0f), new PMVector3(-3f, 0f, 0f),
                                             Radius, HalfHeight);
            Check("C1 墙前 fraction≈0.70 且 normal≈(+1,0,0)（有效前向 cast 不被地面假命中抢走）",
                wallHit.Blocking && Near(wallHit.Fraction, 0.7f, 0.02f) && wallHit.Normal.X > 0.9f
                && Math.Abs(wallHit.Normal.Y) < 0.1f,
                wallHit.ToString() + "（期望 0.70 / (+1,0,0)）");

            // 贴墙（胶囊左沿正好在墙右面 x=0.5）向内：接触 ⇒ fraction=0 且法线朝外。
            PMMoverHit inward = query.Sweep(new PMVector3(0.9f, HalfHeight, 0f), new PMVector3(-0.5f, 0f, 0f),
                                            Radius, HalfHeight);
            Check("C2 触墙向内阻挡（fraction≈0，normal=(+1,0,0)）",
                inward.Blocking && inward.Fraction <= 0.01f && inward.Normal.X > 0.9f, inward.ToString());

            PMMoverHit outward = query.Sweep(new PMVector3(0.9f, HalfHeight, 0f), new PMVector3(0.5f, 0f, 0f),
                                             Radius, HalfHeight);
            Check("C3 触墙朝外可走（法线与运动同向，不算阻挡）", !outward.Blocking, outward.ToString());

            PMMoverHit along = query.Sweep(new PMVector3(0.9f, HalfHeight, 0f), new PMVector3(0f, 0f, 0.5f),
                                           Radius, HalfHeight);
            Check("C4 触墙沿墙可走（法线与运动垂直，不算阻挡）", !along.Blocking, along.ToString());

            // 高速 + 薄墙：一次性 6m 位移，墙 1m 厚（真实场景墙 x∈[-0.5,0.5]），必须仍被挡住。
            // 期望 fraction = ((4-0.4)-0.5)/6 = 3.1/6 ≈ 0.5167
            PMMoverHit fast = query.Sweep(new PMVector3(4f, HalfHeight, 0f), new PMVector3(-6f, 0f, 0f),
                                          Radius, HalfHeight);
            Check("C5 高速大位移不漏墙（fraction≈0.5167，绝不一帧穿过去）",
                fast.Blocking && Near(fast.Fraction, 3.1f / 6f, 0.02f) && fast.Normal.X > 0.9f,
                fast.ToString() + "（期望 ≈0.5167）");

            // 贴墙 1mm（模型滑动时的 skin 间隙）向内的小位移：仍必须被挡住（不能因为"看不到墙"而推进）。
            PMMoverHit skin = query.Sweep(new PMVector3(0.901f, HalfHeight, 0f), new PMVector3(-0.1f, 0f, 0f),
                                          Radius, HalfHeight);
            Check("C6 贴墙 1mm 处小位移向内仍阻挡（不能漏墙，也不能返回 0 假穿透）",
                skin.Blocking && skin.Fraction <= 0.05f && skin.Normal.X > 0.9f, skin.ToString());

            // 退化输入仍要显式拒绝（保持既有契约）。
            bool threwBadRadius = false;
            try
            {
                query.Sweep(new PMVector3(3f, HalfHeight, 0f), new PMVector3(1f, 0f, 0f), 0f, HalfHeight);
            }
            catch (ArgumentOutOfRangeException) { threwBadRadius = true; }

            bool threwNaN = false;
            try
            {
                query.Sweep(new PMVector3(3f, HalfHeight, 0f), new PMVector3(float.NaN, 0f, 0f), Radius, HalfHeight);
            }
            catch (ArgumentException) { threwNaN = true; }

            Check("C7 非法半径/NaN 位移仍显式抛出", threwBadRadius && threwNaN,
                "radius=0 抛出=" + threwBadRadius + " NaN 抛出=" + threwNaN);
        }

        // ==================================================================
        //  D. 起点正穿透：有效推出法线 + 计数 + 与扫掠方向无关
        // ==================================================================

        private static void SectionD()
        {
            Section("D. 起点正穿透（真正嵌墙/嵌地）必须给有效推出法线并计数");

            PhysicsScene scene = CreateStandardScene(out Collider floor, out Collider wall);
            PMUnityMoverCollisionQuery query =
                new PMUnityMoverCollisionQuery(scene, AllLayers, WorldVersion, new Collider[] { floor, wall });

            // (0,1,0) 完全在墙体内：最小平移轴是 X（±0.8 比 Z 的 ±0.8/±4 更近，取 X），法线必须沿 ±X。
            PMMoverHit embedded = query.Sweep(new PMVector3(0f, HalfHeight, 0f), new PMVector3(0f, 0f, 1f),
                                              Radius, HalfHeight);
            Check("D1 起点嵌墙给 fraction=0 + 有效推出法线（沿最小重叠轴 ±X）",
                embedded.Blocking && embedded.Fraction == 0f && Near(embedded.Normal.Length, 1f, 1e-3f)
                && Math.Abs(embedded.Normal.X) > WallEscapeAxisDot,
                embedded.ToString() + "（修复前是 cast 的假法线 (0,0,-1)：方向朝向墙内 4m，不是有效推出方向）");

            PMMoverHit embeddedBackward = query.Sweep(new PMVector3(0f, HalfHeight, 0f), new PMVector3(0f, 0f, -1f),
                                                      Radius, HalfHeight);
            Check("D2 起点嵌墙的判定与扫掠方向无关（仍 fraction=0 + 沿 ±X 有效推出）",
                embeddedBackward.Blocking && embeddedBackward.Fraction == 0f
                && Math.Abs(embeddedBackward.Normal.X) > WallEscapeAxisDot, embeddedBackward.ToString());

            PMUnityCollisionQueryStats stats = query.GetStats();
            Check("D3 StartOverlapResolutions ≥ 1（起点重叠路径真的被执行，修复前恒为 0）",
                stats.StartOverlapResolutions >= 1, stats.ToString());

            // 足底穿入地板 0.1m（水平扫掠）：仍属"起点正穿透"，必须给朝上的有效推出法线。
            PMMoverHit sunkFloor = query.Sweep(new PMVector3(3f, HalfHeight - 0.1f, 0f), new PMVector3(0f, 0f, 1f),
                                               Radius, HalfHeight);
            Check("D4 足底穿入地板 0.1m 时给 +Y 有效推出法线（不会被判成水平阻挡方向）",
                sunkFloor.Blocking && sunkFloor.Fraction == 0f && sunkFloor.Normal.Y > 0.9f, sunkFloor.ToString());

            // 非 BoxCollider 形状（Mesh / Terrain / 旋转盒…）：bounds 只是**超集**近似。
            // 适配器在这里给出的是**保守**结论：判"不穿透"可靠（形状 ⊆ bounds ⇒ 绝不漏墙），
            // 判"穿透"可能偏早（宁可早挡），推出方向把胶囊推出 bounds 就一定推离形状（方向有效）。
            // 这里用"只给 AABB 的 MeshCollider 双件"钉住该边界：必须保守阻挡 + 给有效推出方向 + 计数。
            PhysicsScene meshScene = new PhysicsScene();
            Collider mesh = meshScene.AddMeshBox("MeshBox", new Vector3(0f, 1f, 0f), new Vector3(2f, 2f, 2f));
            PMUnityMoverCollisionQuery meshQuery =
                new PMUnityMoverCollisionQuery(meshScene, AllLayers, WorldVersion, new Collider[] { mesh });

            PMMoverHit meshHit = meshQuery.Sweep(new PMVector3(0f, HalfHeight, 0f), new PMVector3(0f, 0f, 1f),
                                                Radius, HalfHeight);
            PMUnityCollisionQueryStats meshStats = meshQuery.GetStats();
            Check("D5 非 BoxCollider 形状走 bounds 保守分类（不假装精确 MTD，绝不漏墙，边界被计数）",
                meshHit.Blocking && meshHit.Fraction == 0f && Near(meshHit.Normal.Length, 1f, 1e-3f)
                && Math.Abs(meshHit.Normal.X) > WallEscapeAxisDot
                && meshStats.NonBoxBoundsClassifications >= 1,
                meshHit.ToString() + " nonBoxBoundsClassifications=" + meshStats.NonBoxBoundsClassifications
                + "（边界：非 Box 形状的 bounds 是超集 ⇒ 保守分类；轴对齐 Box 上 bounds 精确）");

            // 旋转 Box（仍按 AABB 参与双件查询）与上一条同属"bounds 超集"边界，仅作观测记录。
            PhysicsScene rotatedScene = new PhysicsScene();
            Collider rotated = rotatedScene.AddRotatedBox("RotatedBox", new Vector3(0f, 1f, 0f),
                                                          new Vector3(2f, 2f, 2f), 30f);
            PMUnityMoverCollisionQuery rotatedQuery =
                new PMUnityMoverCollisionQuery(rotatedScene, AllLayers, WorldVersion, new Collider[] { rotated });

            PMMoverHit rotatedHit = rotatedQuery.Sweep(new PMVector3(0f, HalfHeight, 0f), new PMVector3(0f, 0f, 1f),
                                                       Radius, HalfHeight);
            Check("D6 旋转 Box（bounds 超集）同样保守阻挡且给出有效推出方向",
                rotatedHit.Blocking && rotatedHit.Fraction == 0f
                && Math.Abs(rotatedHit.Normal.X) > WallEscapeAxisDot,
                rotatedHit.ToString() + "（bounds 保守分类；若为轴对齐 Box 则该结论精确）");
        }

        // ==================================================================
        //  E. allowlist 隔离（白名单外的诱饵必须被无视）
        // ==================================================================

        private static void SectionE()
        {
            Section("E. allowlist 隔离语义（白名单外的 Collider 不参与判定）");

            PhysicsScene scene = CreateStandardScene(out Collider floor, out Collider wall);
            Collider decoy = scene.AddBox("PMR4Decoy", new Vector3(2f, 1f, 0f), new Vector3(0.4f, 2f, 2f));

            PMUnityMoverCollisionQuery scoped =
                new PMUnityMoverCollisionQuery(scene, AllLayers, WorldVersion, new Collider[] { floor, wall });
            PMUnityMoverCollisionQuery unscoped =
                new PMUnityMoverCollisionQuery(scene, AllLayers, WorldVersion, null);

            PMMoverHit withAllowlist = scoped.Sweep(new PMVector3(3f, HalfHeight, 0f), new PMVector3(-3f, 0f, 0f),
                                                    Radius, HalfHeight);
            PMMoverHit withoutAllowlist = unscoped.Sweep(new PMVector3(3f, HalfHeight, 0f), new PMVector3(-3f, 0f, 0f),
                                                         Radius, HalfHeight);

            // 期望：白名单 ≈0.70（无视诱饵）；无白名单 ≈(2.6-2.2)/3 = 0.1333（被诱饵先挡住）。
            Check("E1 白名单无视诱饵（≈0.70），无白名单被诱饵挡住（≈0.1333）",
                withAllowlist.Blocking && Near(withAllowlist.Fraction, 0.7f, 0.02f)
                && withoutAllowlist.Blocking && Near(withoutAllowlist.Fraction, 0.1333f, 0.02f),
                "白名单=" + withAllowlist + " 无白名单=" + withoutAllowlist);

            Check("E2 白名单计数与掩码可审计", scoped.AllowlistCount == 2 && scoped.HasAllowlist
                  && !unscoped.HasAllowlist && scoped.LayerMask == AllLayers,
                "allowlist=" + scoped.AllowlistCount + " hasAllowlist=" + scoped.HasAllowlist
                + " mask=" + scoped.LayerMask);
        }

        // ==================================================================
        //  F. 地面查询（贴地/悬空/穿透）与竖直向下阻挡
        // ==================================================================

        private static void SectionF()
        {
            Section("F. 地面查询与『竖直向下』扫掠");

            PhysicsScene scene = CreateStandardScene(out Collider floor, out Collider wall);
            PMUnityMoverCollisionQuery query =
                new PMUnityMoverCollisionQuery(scene, AllLayers, WorldVersion, new Collider[] { floor, wall });

            PMMoverGround touching = query.QueryGround(new PMVector3(3f, HalfHeight, 0f), Radius, HalfHeight,
                                                      PMMoverDefaults.GroundProbeMeters);
            Check("F1 贴地：Found + |distance|≈0 + normal=Up",
                touching.Found && Math.Abs(touching.Distance) <= 1e-5f && touching.Normal.Y > 0.99f,
                touching.ToString());

            PMMoverGround floating = query.QueryGround(new PMVector3(3f, HalfHeight + 0.3f, 0f), Radius, HalfHeight,
                                                      PMMoverDefaults.GroundProbeMeters);
            Check("F2 悬空 0.3m 且探测 0.05m：Found=false（不虚报支撑）",
                !floating.Found && floating.Distance == 0f, floating.ToString());

            PMMoverGround penetrating = query.QueryGround(new PMVector3(3f, HalfHeight - 0.15f, 0f), Radius, HalfHeight,
                                                          PMMoverDefaults.GroundProbeMeters);
            Check("F3 足底穿入 0.15m：signed ≈ -0.15（负=穿透）",
                penetrating.Found && Near(penetrating.Distance, -0.15f, 0.01f), penetrating.ToString());

            // 竖直向下扫掠：悬空 0.3m、位移 0.5m ⇒ fraction = 0.3/0.5 = 0.6。
            PMMoverHit downward = query.Sweep(new PMVector3(3f, HalfHeight + 0.3f, 0f), new PMVector3(0f, -0.5f, 0f),
                                              Radius, HalfHeight);
            Check("F4 竖直向下扫掠仍被地面阻挡（fraction≈0.6，normal=Up）",
                downward.Blocking && Near(downward.Fraction, 0.6f, 0.02f) && downward.Normal.Y > 0.9f,
                downward.ToString());

            PMMoverHit downwardTouch = query.Sweep(new PMVector3(3f, HalfHeight, 0f), new PMVector3(0f, -0.2f, 0f),
                                                   Radius, HalfHeight);
            Check("F5 贴地竖直向下：阻挡且 fraction≈0（下压不得穿过地面）",
                downwardTouch.Blocking && downwardTouch.Fraction <= 0.01f && downwardTouch.Normal.Y > 0.9f,
                downwardTouch.ToString());

            Info("地面查询后计数：" + query.GetStats());
        }

        // ==================================================================
        //  G. 饱和必须显式失败（保持既有门禁）
        // ==================================================================

        private static void SectionG()
        {
            Section("G. 命中缓冲饱和必须显式失败（绝不静默忽略墙）");

            PhysicsScene scene = new PhysicsScene();
            for (int i = 0; i < PMUnityMoverCollisionQuery.HitBufferCapacity + 8; i++)
            {
                scene.AddBox("Slab" + i, new Vector3(10f, 1f, 1f + i * 0.05f), new Vector3(0.4f, 2f, 0.2f));
            }

            PMUnityMoverCollisionQuery query = new PMUnityMoverCollisionQuery(scene, AllLayers, WorldVersion, null);

            bool threw = false;
            string message = string.Empty;
            try
            {
                query.Sweep(new PMVector3(10f, HalfHeight, 0f), new PMVector3(0f, 0f, 6f), Radius, HalfHeight);
            }
            catch (InvalidOperationException ex)
            {
                threw = true;
                message = ex.Message;
            }

            PMUnityCollisionQueryStats stats = query.GetStats();
            Check("G1 饱和抛出 InvalidOperationException 且 SaturationFailures=1",
                threw && stats.SaturationFailures == 1,
                "threw=" + threw + " saturationFailures=" + stats.SaturationFailures
                + " message=" + (message.Length > 100 ? message.Substring(0, 100) + "…" : message));
        }

        // ==================================================================
        //  H. 真实模型（PMMoverModel + 真实适配器 + 双件世界）
        // ==================================================================

        private static void SectionH()
        {
            Section("H. 真实 PMMoverModel 在受控世界上的行走/起跳（复现真实 Play 的两项模型级失败）");

            PhysicsScene scene = CreateStandardScene(out Collider floor, out Collider wall);
            PMUnityMoverCollisionQuery query =
                new PMUnityMoverCollisionQuery(scene, AllLayers, WorldVersion, new Collider[] { floor, wall });
            PMMoverModel model = new PMMoverModel(query);

            string walkDetail;
            bool walkOk = RunWalkIntoWall(model, out walkDetail);
            Check("H1 贴地朝墙走 2 秒：既走到墙前(≈0.9)又不穿墙", walkOk, walkDetail);

            string jumpDetail;
            bool jumpOk = RunJumpAndLand(model, out jumpDetail);
            Check("H2 起跳后落回地面并回到 Walking", jumpOk, jumpDetail);

            Info("模型用例后适配器计数：" + query.GetStats());
        }

        /// <summary>
        /// 走墙：起点 (3,1,0) 朝 -X 输入 125 × 16ms。期望 minX ≈ 0.5+radius = 0.9（永远不小于 0.9-1e-3）。
        /// 与 Editor 的 RunWalkIntoWall 同参数、同布局；这里把门槛收紧到 1mm（Editor 保留 5cm 只因其容差是为
        /// contactOffset 留的，受控世界无该不确定性）。
        /// </summary>
        private static bool RunWalkIntoWall(PMMoverModel model, out string detail)
        {
            PMMoverSyncState state = PMMoverSyncState.CreateDefault();
            state.Position = new PMVector3(3f, HalfHeight, 0f);

            PMMoverAuxState aux = PMMoverAuxState.CreateDefault();
            aux.CollisionWorldVersion = WorldVersion;

            float minX = state.Position.X;
            long simMs = 0L;
            long frame = 0L;

            const int steps = 125; // 125 × 16ms = 2.0s

            for (int i = 0; i < steps; i++)
            {
                PMMoverInput input = PMMoverInput.Empty();
                input.MoveX = -1f;
                input.YawDegrees = 270f;

                PMSimulationResult<PMMoverSyncState, PMMoverAuxState> result =
                    model.Simulate(MakeStep(simMs, frame), input, state, aux);

                state = result.Sync;
                aux = result.Aux;

                if (state.Position.X < minX) { minX = state.Position.X; }

                simMs += 16L;
                frame++;
            }

            float expectedStop = 0.5f + Radius; // 0.9
            bool neverEnteredWall = minX >= expectedStop - 1e-3f;
            bool actuallyAdvanced = minX <= expectedStop + 0.05f;
            bool movedAtAll = minX < 2.9f;

            detail = "minX=" + minX.ToString("R") + " finalX=" + state.Position.X.ToString("R")
                     + " mode=" + PMMoverModes.Name(state.Mode)
                     + "（期望 minX≈0.9；修复前 minX=3 finalX≈3.125 ⇒ 贴地走不动的真实症状）";

            return neverEnteredWall && actuallyAdvanced && movedAtAll;
        }

        /// <summary>起跳：原地跳一次，期望顶点 &gt;1.5、落回 y≈1 且 mode=Walking（贴地平移的另一半证据）。</summary>
        private static bool RunJumpAndLand(PMMoverModel model, out string detail)
        {
            PMMoverSyncState state = PMMoverSyncState.CreateDefault();
            state.Position = new PMVector3(6f, HalfHeight, 0f);

            PMMoverAuxState aux = PMMoverAuxState.CreateDefault();
            aux.CollisionWorldVersion = WorldVersion;

            float maxY = state.Position.Y;
            long simMs = 0L;
            long frame = 0L;

            const int steps = 125;

            for (int i = 0; i < steps; i++)
            {
                PMMoverInput input = PMMoverInput.Empty();
                input.YawDegrees = 0f;
                input.JumpPressed = i == 0; // 边沿只给一次（模型不做去抖）

                PMSimulationResult<PMMoverSyncState, PMMoverAuxState> result =
                    model.Simulate(MakeStep(simMs, frame), input, state, aux);

                state = result.Sync;
                aux = result.Aux;

                if (state.Position.Y > maxY) { maxY = state.Position.Y; }

                simMs += 16L;
                frame++;
            }

            bool jumped = maxY > HalfHeight + 0.5f;
            bool landed = state.Mode == PMMoverMode.Walking && state.Grounded
                          && Math.Abs(state.Position.Y - HalfHeight) <= 0.02f;

            detail = "maxY=" + maxY.ToString("R") + " finalY=" + state.Position.Y.ToString("R")
                     + " mode=" + PMMoverModes.Name(state.Mode) + " grounded=" + state.Grounded
                     + "（期望 maxY>1.5；修复前 maxY=1 ⇒ 竖直位移被地板假命中抵消）";

            return jumped && landed;
        }

        private static PMTimeStep MakeStep(long simMs, long frame)
        {
            PMTimeStep step = new PMTimeStep();
            step.BaseSimTimeMs = simMs;
            step.StepMs = 16f;
            step.ServerFrame = new PMFrameId(PMFrameDomain.AuthorityServer, frame);
            step.ClientInputFrame = new PMFrameId(PMFrameDomain.Input, frame);
            step.IsResimulating = false;
            return step;
        }

        // ==================================================================
        //  I. 查询不得有副作用
        // ==================================================================

        private static void SectionI()
        {
            Section("I. 查询期无副作用（不动 Transform / 不 SyncTransforms / 不动世界 / 忽略 trigger）");

            PhysicsScene scene = CreateStandardScene(out Collider floor, out Collider wall);
            PMUnityMoverCollisionQuery query =
                new PMUnityMoverCollisionQuery(scene, AllLayers, WorldVersion, new Collider[] { floor, wall });

            string before = DescribeWorld(scene);
            int beforeCount = scene.Colliders.Count;

            query.Sweep(new PMVector3(3f, HalfHeight, 0f), new PMVector3(-3f, 0f, 0f), Radius, HalfHeight);
            query.Sweep(new PMVector3(0f, HalfHeight, 0f), new PMVector3(0f, 0f, 1f), Radius, HalfHeight);
            query.QueryGround(new PMVector3(3f, HalfHeight, 0f), Radius, HalfHeight,
                              PMMoverDefaults.GroundProbeMeters);

            string after = DescribeWorld(scene);

            Check("I1 世界几何/启用状态/Transform 均未被改动（逐 Collider 与数量比对）",
                before == after && scene.Colliders.Count == beforeCount,
                "colliders=" + scene.Colliders.Count + "（内容一致=" + (before == after) + "）");

            Check("I2 适配器从不调用 Physics.SyncTransforms（宿主每帧单次负责）",
                Physics.SyncTransformsCalls == 0, "SyncTransformsCalls=" + Physics.SyncTransformsCalls);

            Check("I3 查询一律 QueryTriggerInteraction.Ignore（不读也不写全局 queriesHitTriggers）",
                scene.LastQueryTriggerInteraction == QueryTriggerInteraction.Ignore
                && scene.OverlapLastQueryTriggerInteraction == QueryTriggerInteraction.Ignore,
                "capsuleCast=" + scene.LastQueryTriggerInteraction
                + " overlapCapsule=" + scene.OverlapLastQueryTriggerInteraction);

            Check("I4 查询确实打在该场景上（隔离语义：calls>0 且掩码按参数生效）",
                scene.CapsuleCastCalls > 0 && scene.OverlapCapsuleCalls > 0 && scene.LastLayerMask == AllLayers,
                "capsuleCast=" + scene.CapsuleCastCalls + " overlapCapsule=" + scene.OverlapCapsuleCalls
                + " mask=" + scene.LastLayerMask);

            // 主线程约束：构造线程之外的查询必须显式失败（而不是给出不可信结果）。
            PMUnityMoverCollisionQuery shared = query;
            bool crossThreadThrew = false;
            Exception crossThreadError = null;
            Thread worker = new Thread(delegate ()
            {
                try
                {
                    shared.Sweep(new PMVector3(3f, HalfHeight, 0f), new PMVector3(1f, 0f, 0f), Radius, HalfHeight);
                }
                catch (Exception ex)
                {
                    crossThreadError = ex;
                }
            });

            worker.Start();
            worker.Join();

            crossThreadThrew = crossThreadError is InvalidOperationException;
            Check("I5 跨线程查询显式失败（主线程约束）", crossThreadThrew,
                "exception=" + (crossThreadError == null ? "none" : crossThreadError.GetType().Name));
        }

        private static PhysicsScene CreateStandardScene(out Collider floor, out Collider wall)
        {
            PhysicsScene scene = new PhysicsScene();
            floor = scene.AddBox("PMR4Floor", FloorCenter, FloorSize);
            wall = scene.AddBox("PMR4Wall", WallCenter, WallSize);
            return scene;
        }

        private static string DescribeWorld(PhysicsScene scene)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < scene.Colliders.Count; i++)
            {
                Collider c = scene.Colliders[i];
                builder.Append(c.name).Append('|')
                       .Append(c.AabbMin).Append('|').Append(c.AabbMax).Append('|')
                       .Append(c.enabled).Append('|')
                       .Append(c.transform.position).Append('|')
                       .Append(c.transform.right).Append('|').Append(c.transform.up).Append('|')
                       .Append(c.transform.forward).Append(';');
            }

            return builder.ToString();
        }
    }
}

// ============================================================================
//  以下为**受控 Unity 查询双件**（测试专用）
// ============================================================================
//
//  这不是 Unity，也不是 PhysX。它只实现适配器真正用到的那一层 API 语义，并且刻意复现
//  2026-09-20 真实 Play 里观测到的两条行为：
//    1) 起点已接触/重叠（几何间隙 gap <= InitialContactTolerance）⇒ distance = 0
//       且 normal = -dir（所以"法线与运动方向相反"的过滤必然误接纳它）；
//    2) 有正间隙且相对接近时 ⇒ 一阶 TOI = gap / |dot(dir, n)|，法线取"Box → 胶囊段"的特征法线。
//  几何模型：轴对齐 Box（世界 AABB）vs 竖直胶囊（段 [p1,p2] + radius）。
//  **有意简化**：旋转 Box 只按 AABB(bounds) 近似 —— 适配器的"不支持形状"回退另有断言（D5）。
// ============================================================================

namespace UnityEngine
{
    /// <summary>与 Unity 数值一致的 Y-up 三维向量（只实现适配器/测试用到的成员）。</summary>
    public struct Vector3
    {
        public float x;
        public float y;
        public float z;

        public Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public static Vector3 zero { get { return new Vector3(0f, 0f, 0f); } }
        public static Vector3 up { get { return new Vector3(0f, 1f, 0f); } }
        public static Vector3 down { get { return new Vector3(0f, -1f, 0f); } }
        public static Vector3 right { get { return new Vector3(1f, 0f, 0f); } }
        public static Vector3 forward { get { return new Vector3(0f, 0f, 1f); } }

        public float magnitude
        {
            get { return (float)Math.Sqrt((double)(x * x + y * y + z * z)); }
        }

        public static Vector3 operator +(Vector3 a, Vector3 b)
        {
            return new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        }

        public static Vector3 operator -(Vector3 a, Vector3 b)
        {
            return new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        }

        public static Vector3 operator *(Vector3 a, float s)
        {
            return new Vector3(a.x * s, a.y * s, a.z * s);
        }

        public static Vector3 operator *(float s, Vector3 a)
        {
            return new Vector3(a.x * s, a.y * s, a.z * s);
        }

        public override string ToString()
        {
            return "(" + x.ToString("R") + ", " + y.ToString("R") + ", " + z.ToString("R") + ")";
        }
    }

    /// <summary>世界轴对齐包围盒（min/max/center/size 与 Unity 同义）。</summary>
    public struct Bounds
    {
        private Vector3 _center;
        private Vector3 _size;

        public Bounds(Vector3 center, Vector3 size)
        {
            _center = center;
            _size = size;
        }

        public Vector3 center { get { return _center; } }
        public Vector3 size { get { return _size; } }

        public Vector3 min
        {
            get
            {
                return new Vector3(_center.x - _size.x * 0.5f,
                                   _center.y - _size.y * 0.5f,
                                   _center.z - _size.z * 0.5f);
            }
        }

        public Vector3 max
        {
            get
            {
                return new Vector3(_center.x + _size.x * 0.5f,
                                   _center.y + _size.y * 0.5f,
                                   _center.z + _size.z * 0.5f);
            }
        }
    }

    /// <summary>Transform 双件：只需要 position 与三个主轴（适配器用它判定"轴对齐 Box"）。</summary>
    public sealed class Transform
    {
        public string name;
        public Vector3 position;
        public Vector3 right = new Vector3(1f, 0f, 0f);
        public Vector3 up = new Vector3(0f, 1f, 0f);
        public Vector3 forward = new Vector3(0f, 0f, 1f);

        public Transform(string name)
        {
            this.name = name;
        }
    }

    /// <summary>Collider 双件：世界 AABB + 启用位 + 图层（供 layerMask 过滤）。</summary>
    public class Collider
    {
        public string name;
        public int layer;
        public bool enabled = true;
        public Transform transform;

        /// <summary>受控世界里的世界 AABB。轴对齐 Box 是精确值；旋转 Box 是 bounds 近似值。</summary>
        public Vector3 AabbMin;
        public Vector3 AabbMax;

        /// <summary>false 表示"旋转 Box"（双件几何用 AABB 近似；适配器会走不支持形状回退）。</summary>
        public bool AxisAligned = true;

        public Bounds bounds
        {
            get
            {
                return new Bounds((AabbMin + AabbMax) * 0.5f, AabbMax - AabbMin);
            }
        }

        public string GetNameSafe()
        {
            return name == null ? "(null)" : name;
        }
    }

    /// <summary>BoxCollider 双件。</summary>
    public sealed class BoxCollider : Collider
    {
    }

    /// <summary>
    /// MeshCollider 双件（只用于 D5：非 BoxCollider 形状在适配器里走 bounds 保守分类）。
    /// 双件不模拟真实网格几何，只用 AABB 参与查询（**有意简化**，已在用例里写明）。
    /// </summary>
    public sealed class MeshCollider : Collider
    {
    }

    /// <summary>与 Unity 同名的射线命中结构（只实现 distance/normal/collider）。</summary>
    public struct RaycastHit
    {
        public float distance;
        public Vector3 normal;
        public Collider collider;
    }

    /// <summary>与 Unity 数值一致的枚举（适配器只使用 Ignore）。</summary>
    public enum QueryTriggerInteraction
    {
        UseGlobal = 0,
        Ignore = 1,
        Collide = 2,
    }

    /// <summary>
    /// 受控物理场景：持有一批 Collider，并按"已观测语义"回答 CapsuleCast / OverlapCapsule。
    /// 同时记录查询次数与最后一次的 trigger/mask 参数，供"无副作用/参数透传"断言使用。
    /// </summary>
    public sealed class PhysicsScene
    {
        /// <summary>gap &lt;= 该值视为"起点已接触/重叠"（真实 PhysX 在几何正好接触时也报 distance=0）。</summary>
        internal const float InitialContactTolerance = 1e-5f;

        internal readonly List<Collider> Colliders = new List<Collider>();

        public int CapsuleCastCalls;
        public int OverlapCapsuleCalls;
        public float LastMaxDistance;
        public int LastLayerMask;
        public QueryTriggerInteraction LastQueryTriggerInteraction = QueryTriggerInteraction.UseGlobal;
        public QueryTriggerInteraction OverlapLastQueryTriggerInteraction = QueryTriggerInteraction.UseGlobal;

        public bool IsValid() { return true; }

        public bool IsEmpty() { return Colliders.Count == 0; }

        /// <summary>测试专用：加一个轴对齐 Box（世界 AABB = center ± size/2）。</summary>
        public BoxCollider AddBox(string name, Vector3 center, Vector3 size, int layer)
        {
            BoxCollider box = new BoxCollider();
            box.name = name;
            box.layer = layer;
            box.enabled = true;
            box.AxisAligned = true;
            box.AabbMin = center - size * 0.5f;
            box.AabbMax = center + size * 0.5f;
            box.transform = new Transform(name);
            box.transform.position = center;
            Colliders.Add(box);
            return box;
        }

        public BoxCollider AddBox(string name, Vector3 center, Vector3 size)
        {
            return AddBox(name, center, size, 0);
        }

        /// <summary>测试专用：加一个**非 BoxCollider** 形状（双件只保存 AABB，用于 D5 的保守边界断言）。</summary>
        public MeshCollider AddMeshBox(string name, Vector3 center, Vector3 size, int layer)
        {
            MeshCollider mesh = new MeshCollider();
            mesh.name = name;
            mesh.layer = layer;
            mesh.enabled = true;
            mesh.AxisAligned = false;
            mesh.AabbMin = center - size * 0.5f;
            mesh.AabbMax = center + size * 0.5f;
            mesh.transform = new Transform(name);
            mesh.transform.position = center;
            Colliders.Add(mesh);
            return mesh;
        }

        public MeshCollider AddMeshBox(string name, Vector3 center, Vector3 size)
        {
            return AddMeshBox(name, center, size, 0);
        }

        /// <summary>
        /// 测试专用：加一个**旋转** Box（绕 Y 轴 yaw 度）。几何按旋转后的 AABB 近似（有意简化）：
        /// transform 的基给成旋转后的方向，于是适配器的"轴对齐"判定必然失败 ⇒ 走不支持形状回退。
        /// </summary>
        public BoxCollider AddRotatedBox(string name, Vector3 center, Vector3 size, float yawDegrees)
        {
            double yaw = yawDegrees * Math.PI / 180.0;
            float cos = (float)Math.Cos(yaw);
            float sin = (float)Math.Sin(yaw);

            float halfX = size.x * 0.5f;
            float halfY = size.y * 0.5f;
            float halfZ = size.z * 0.5f;

            float extentsX = Math.Abs(cos) * halfX + Math.Abs(sin) * halfZ;
            float extentsZ = Math.Abs(sin) * halfX + Math.Abs(cos) * halfZ;

            BoxCollider box = new BoxCollider();
            box.name = name;
            box.layer = 0;
            box.enabled = true;
            box.AxisAligned = false;
            box.AabbMin = new Vector3(center.x - extentsX, center.y - halfY, center.z - extentsZ);
            box.AabbMax = new Vector3(center.x + extentsX, center.y + halfY, center.z + extentsZ);
            box.transform = new Transform(name);
            box.transform.position = center;
            box.transform.right = new Vector3(cos, 0f, -sin);
            box.transform.up = new Vector3(0f, 1f, 0f);
            box.transform.forward = new Vector3(sin, 0f, cos);
            Colliders.Add(box);
            return box;
        }

        public int CapsuleCast(Vector3 point1, Vector3 point2, float radius, Vector3 direction,
                               RaycastHit[] results, float maxDistance, int layerMask,
                               QueryTriggerInteraction queryTriggerInteraction)
        {
            CapsuleCastCalls++;
            LastMaxDistance = maxDistance;
            LastLayerMask = layerMask;
            LastQueryTriggerInteraction = queryTriggerInteraction;

            if (results == null) { throw new ArgumentNullException("results"); }

            Vector3 dir = Normalize(direction);
            int total = 0;
            int written = 0;

            for (int i = 0; i < Colliders.Count; i++)
            {
                Collider collider = Colliders[i];
                if (!IsInLayerMask(collider, layerMask)) { continue; }

                float gap;
                Vector3 normal;
                DoubleBoxQuery.Measure(point1, point2, radius, collider, out gap, out normal);

                float distance;
                Vector3 hitNormal;

                if (gap <= InitialContactTolerance)
                {
                    // 已观测语义：起点已接触/重叠 ⇒ distance=0 且 normal = -dir（原始法线不可信）。
                    distance = 0f;
                    hitNormal = dir * -1f;
                }
                else
                {
                    float rate = Dot(dir, normal);
                    if (rate >= -InitialContactTolerance) { continue; } // 不接近该面（含平行掠过）
                    distance = gap / (-rate);
                    if (distance > maxDistance) { continue; }
                    hitNormal = normal;
                }

                total++;
                if (written < results.Length)
                {
                    results[written].distance = distance;
                    results[written].normal = hitNormal;
                    results[written].collider = collider;
                    written++;
                }
            }

            // 真实 PhysX 的批量查询只返回容量内的项，count 取 min(总命中, 容量) —— 饱和断言依赖此语义。
            return total < results.Length ? total : results.Length;
        }

        public int OverlapCapsule(Vector3 point0, Vector3 point1, float radius, Collider[] results,
                                  int layerMask, QueryTriggerInteraction queryTriggerInteraction)
        {
            OverlapCapsuleCalls++;
            LastLayerMask = layerMask;
            OverlapLastQueryTriggerInteraction = queryTriggerInteraction;

            if (results == null) { throw new ArgumentNullException("results"); }

            int total = 0;
            int written = 0;

            for (int i = 0; i < Colliders.Count; i++)
            {
                Collider collider = Colliders[i];
                if (!IsInLayerMask(collider, layerMask)) { continue; }
                if (!collider.enabled) { continue; }

                float gap;
                Vector3 normal;
                DoubleBoxQuery.Measure(point0, point1, radius, collider, out gap, out normal);
                if (gap > InitialContactTolerance) { continue; }

                total++;
                if (written < results.Length)
                {
                    results[written] = collider;
                    written++;
                }
            }

            return total < results.Length ? total : results.Length;
        }

        private static bool IsInLayerMask(Collider collider, int layerMask)
        {
            if (collider == null || !collider.enabled) { return false; }
            return (layerMask & (1 << collider.layer)) != 0;
        }

        internal static Vector3 Normalize(Vector3 v)
        {
            float length = v.magnitude;
            if (length <= 1e-12f) { return Vector3.zero; }
            return v * (1f / length);
        }

        internal static float Dot(Vector3 a, Vector3 b)
        {
            return a.x * b.x + a.y * b.y + a.z * b.z;
        }
    }

    /// <summary>Physics 双件：只提供 defaultPhysicsScene 与一个计数的 SyncTransforms（用于"零副作用"断言）。</summary>
    public static class Physics
    {
        public static readonly PhysicsScene defaultPhysicsScene = new PhysicsScene();

        /// <summary>被调用次数。适配器必须恒为 0（宿主每帧单次负责 SyncTransforms）。</summary>
        public static int SyncTransformsCalls;

        public static void SyncTransforms()
        {
            SyncTransformsCalls++;
        }
    }

    /// <summary>
    /// 受控几何：**竖直胶囊**（段 [segA,segB] + radius）与 Collider 的轴对齐 AABB 的有符号分离。
    ///
    /// 推导（轴对齐 Box 上是**精确**的）：
    ///   段是 x=segX, z=segZ, y∈[segMinY,segMaxY] 的竖直线段；Box 是三个区间的笛卡尔积，
    ///   所以"点到 Box 的距离"在三个轴上可分离 ⇒
    ///     separation = sqrt(dx² + dy² + dz²)，dx/dz 是 segX/segZ 到 Box 区间的间隙，
    ///     dy 是段 y 区间到 Box y 区间的间隙。
    ///   gap = separation − radius（&gt;0 间隙、=0 接触、&lt;0 穿透）。
    ///   separation &gt; 0 时特征法线 = 各轴间隙正交合成的单位向量（面/棱/角）；
    ///   separation == 0（段已进入 Box）时退化为"胶囊 AABB ∩ Box AABB 的最小重叠轴"（保守有效）。
    /// </summary>
    internal static class DoubleBoxQuery
    {
        private const float SeparationEpsilon = 1e-6f;

        internal static void Measure(Vector3 segA, Vector3 segB, float radius, Collider collider,
                                     out float gap, out Vector3 normal)
        {
            gap = 0f;
            normal = Vector3.zero;

            Vector3 min = collider.AabbMin;
            Vector3 max = collider.AabbMax;

            float segX = segA.x;
            float segZ = segA.z;
            float segMinY = segA.y < segB.y ? segA.y : segB.y;
            float segMaxY = segA.y > segB.y ? segA.y : segB.y;

            float dx = AxisGap(segX, min.x, max.x);
            float dy = IntervalGap(segMinY, segMaxY, min.y, max.y);
            float dz = AxisGap(segZ, min.z, max.z);

            float separation = (float)Math.Sqrt((double)(dx * dx + dy * dy + dz * dz));
            gap = separation - radius;

            if (separation > SeparationEpsilon)
            {
                float sx = segX < min.x ? -1f : (segX > max.x ? 1f : 0f);
                float sy = segMaxY < min.y ? -1f : (segMinY > max.y ? 1f : 0f);
                float sz = segZ < min.z ? -1f : (segZ > max.z ? 1f : 0f);

                normal = new Vector3(sx * dx, sy * dy, sz * dz) * (1f / separation);
                return;
            }

            // 段已进入 Box：用胶囊 AABB 与 Box AABB 的最小重叠轴（保守、确定）。
            float capMinX = segX - radius;
            float capMaxX = segX + radius;
            float capMinY = segMinY - radius;
            float capMaxY = segMaxY + radius;
            float capMinZ = segZ - radius;
            float capMaxZ = segZ + radius;

            float ox = Overlap1D(capMinX, capMaxX, min.x, max.x);
            float oy = Overlap1D(capMinY, capMaxY, min.y, max.y);
            float oz = Overlap1D(capMinZ, capMaxZ, min.z, max.z);

            int axis = 0;
            float best = ox;
            if (oy < best) { axis = 1; best = oy; }
            if (oz < best) { axis = 2; best = oz; }

            float centerX = (min.x + max.x) * 0.5f;
            float centerY = (min.y + max.y) * 0.5f;
            float centerZ = (min.z + max.z) * 0.5f;

            switch (axis)
            {
                case 0:
                    normal = new Vector3(segX < centerX ? -1f : 1f, 0f, 0f);
                    return;

                case 1:
                    normal = new Vector3(0f, (segMinY + segMaxY) * 0.5f < centerY ? -1f : 1f, 0f);
                    return;

                default:
                    normal = new Vector3(0f, 0f, segZ < centerZ ? -1f : 1f);
                    return;
            }
        }

        private static float AxisGap(float value, float lo, float hi)
        {
            if (value < lo) { return lo - value; }
            if (value > hi) { return value - hi; }
            return 0f;
        }

        private static float IntervalGap(float aMin, float aMax, float bMin, float bMax)
        {
            if (aMax < bMin) { return bMin - aMax; }
            if (bMax < aMin) { return aMin - bMax; }
            return 0f;
        }

        private static float Overlap1D(float aMin, float aMax, float bMin, float bMax)
        {
            float lo = aMin > bMin ? aMin : bMin;
            float hi = aMax < bMax ? aMax : bMax;
            return hi - lo;
        }
    }
}
