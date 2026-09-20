// ============================================================================
//  PMR4UnityValidation —— R4-B / B2 的**真实 PhysX** 物理验证入口（Editor 菜单）
// ============================================================================
//
//  契约来源：Docs/plans/net-r4-network-contract.md 的 B2 一节：
//    · 「PhysX 运行测试要在 Unity 内另有可执行入口/步骤，不能宣称 net8 运行真实 Physics」；
//    · 「可新增 Client/Assets/Editor/PMR4UnityValidation.cs 菜单测试（**不自动执行**、
//      不触碰用户场景，使用**临时本地 PhysicsScene** / 创建后清理），
//      允许对纯构造世界进行墙/地面/贴面扫掠验证」。
//
//  三条纪律（本文件的设计落点）：
//    1) **不自动执行**：只有用户点菜单（或显式 -executeMethod）才会跑；本文件没有
//       [InitializeOnLoad]、没有构造期副作用。
//    2) **不触碰用户场景**：所有测试对象都进“临时本地物理场景”（LocalPhysicsMode.Physics3D），
//       查询全部走该场景的 PhysicsScene，并**显式校验它不等于 Physics.defaultPhysicsScene**
//       （2019.4 文档：场景没有本地物理场景时 GetPhysicsScene() 会返回默认物理世界，
//        它 IsValid()==true，所以“只看 IsValid”会得出假隔离结论）。
//       **不**再用 EditorSceneManager 的 preview scene：预览场景不保证带本地物理世界。
//       创建/销毁都在 try/finally 里，任何异常路径都会清理；临时切活动场景也会在 finally 还原。
//       编辑模式：SceneManager.CreateScene 是**运行时** API，本验证会照常调用并**用真实 API 结果判定**；
//       若创建失败或回退到默认物理世界，则明确报告“仅 Play 模式可执行”并提前返回（不做假隔离）。
//    3) **诚实口径**：本验证证明"真实 Unity 2019.4 PhysX 上，适配器与纯 Mover 模型的行为
//       符合契约"。它**不**证明 UE PhysX 等价、**不**证明真实网络/DS 接线、**不**证明
//       AP/SP 多端回滚——那三项分别属 R4-B3/B4 与 P4B6（报告里逐条列明）。
//
//  用法：Unity 菜单 Tools / PMR4 / 验证 Unity 碰撞适配（真实 PhysX）。
//        结果同时打印到 Console（完整报告）并弹出摘要对话框。
//        CLI: -executeMethod PMNet.UnityEditor.PMR4UnityValidation.RunValidation
//  退出：编辑模式下不改变用户场景、不保存任何资产。
// ============================================================================

#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.Text;
using PMNet;
using PMNet.Mover;
using PMNet.Prediction;
using PMNet.Unity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PMNet.UnityEditor
{
    /// <summary>
    /// 真实 PhysX 下的 R4-B Unity 适配验证。见文件头注释的三条纪律。
    /// </summary>
    public static class PMR4UnityValidation
    {
        // ------------------------------------------------------------------------------
        //  固定测试布局（契约 net-r3-control-contract §7.4 / PMR3TestScene 同值）
        //
        //  这里**重复**了 PSMR3TestScene 的四个常量，而不是引用它：
        //  PMDsSessionHost.cs 里带着整个会话宿主（Control/Session/Logging 依赖），
        //  把它拖进 Tools/PMR4UnityCheck 的编译门禁会让门禁依赖面爆炸，从而没人敢跑。
        //  代价只是一个极小的分歧风险，故下面每个用例都显式断言几何自洽（"地板顶 y=0"、
        //  "墙右面 x=0.5"），一旦有人改了场景数值，这里的断言会先失败。
        // ------------------------------------------------------------------------------

        /// <summary>地板中心（世界坐标）。与 PMR3TestScene.FloorCenter 同值。</summary>
        private static readonly Vector3 FloorCenter = new Vector3(0f, -0.5f, 0f);

        /// <summary>地板尺寸。与 PMR3TestScene.FloorSize 同值。</summary>
        private static readonly Vector3 FloorSize = new Vector3(40f, 1f, 40f);

        /// <summary>墙中心（世界坐标）。与 PMR3TestScene.WallCenter 同值。</summary>
        private static readonly Vector3 WallCenter = new Vector3(0f, 1f, 0f);

        /// <summary>墙尺寸。与 PMR3TestScene.WallSize 同值。</summary>
        private static readonly Vector3 WallSize = new Vector3(1f, 2f, 8f);

        /// <summary>胶囊半径（米）。取自纯模型默认值，保证与模型使用的尺寸一致。</summary>
        private const float Radius = PMMoverDefaults.CapsuleRadiusMeters;

        /// <summary>胶囊半高（米）。</summary>
        private const float HalfHeight = PMMoverDefaults.CapsuleHalfHeightMeters;

        /// <summary>两端球心相对胶囊中心的偏移（halfHeight − radius）；供原始命中观测用例复用。</summary>
        private const float CapsuleSphereOffset = HalfHeight - Radius;

        /// <summary>验证用碰撞世界版本（非 0，便于在快照/Aux 比较里看出它被带上了）。</summary>
        private const int WorldVersion = 0x52334202;

        /// <summary>所有图层的掩码（用于默认场景查询）。</summary>
        private const int AllLayers = ~0;

        /// <summary>
        /// 默认物理世界隔离对照用的干扰体数量：40 &gt; <see cref="PMUnityMoverCollisionQuery.HitBufferCapacity"/>(32)，
        /// 因此默认世界查询要么被阻挡、要么顶到缓冲上限显式失败——两者都是“默认世界看得见干扰体”的证据。
        /// </summary>
        private const int InterferenceBodyCount = 40;

        /// <summary>
        /// 干扰体的 X 位置：地板顶面 y=0 覆盖 x∈[-20,20]，而墙只在 x∈[-0.5,0.5]，
        /// 所以 x=10 处除了地板什么都没有——隔离世界在该处必须“畅通无阻”。
        /// </summary>
        private const float InterferenceCenterX = 10f;

        /// <summary>Walk 用例的步长（毫秒）。整毫秒且落在契约 1..50 内。</summary>
        private const int ValidationStepMs = 16;

        /// <summary>菜单入口。用户主动点击才会执行。</summary>
        [MenuItem("Tools/PMR4/验证 Unity 碰撞适配（真实 PhysX）")]
        public static void ValidateFromMenu()
        {
            string report = RunValidation();

            Debug.Log(report);

            if (!Application.isBatchMode)
            {
                EditorUtility.DisplayDialog(
                    "PMR4 Unity 碰撞适配验证",
                    Summarize(report),
                    "关闭");
            }
        }

        /// <summary>
        /// 跑完整验证并返回可读报告（不弹窗、不改资产）。异常路径也会完成清理。
        /// 这是可被 <c>-executeMethod</c> 调用的入口。
        /// </summary>
        public static string RunValidation()
        {
            List<GameObject> created = new List<GameObject>();
            Scene tempScene = default(Scene);
            bool tempSceneIsPreview = false;
            string isolation = "(未建立)";
            string setupError = null;
            string cleanupNote = string.Empty;

            Report report = new Report();

            PhysicsScene physicsScene = default(PhysicsScene);
            PMUnityMoverCollisionQuery query = null;
            PMUnityMoverCollisionQuery unscopedQuery = null;

            // 活动场景必须记下来：为了让测试对象落进临时场景，下面会临时切换活动场景，
            // 并在 finally 里还原（契约：活动场景语义属于宿主/用户）。
            Scene activeBefore = SceneManager.GetActiveScene();
            bool activeSwitched = false;
            bool activeDirtyBefore = activeBefore.IsValid() && activeBefore.isDirty;

            try
            {
                // ============================================================ 建隔离世界
                if (!TryCreateIsolatedPhysicsScene(out tempScene, out tempSceneIsPreview, out isolation, out setupError))
                {
                    report.Check("isolatedScene", false, setupError);
                    tempScene = default(Scene);
                    return report.Build("PMR4 Unity 碰撞适配验证（真实 PhysX）", isolation, cleanupNote);
                }

                physicsScene = tempScene.GetPhysicsScene();

                // 隔离判定必须同时满足：场景有效 + 物理场景有效 + **不是** Physics.defaultPhysicsScene。
                // 只看 IsValid 不够：2019.4 文档明确“场景没有本地物理场景时就返回默认物理场景”，
                // 而默认物理场景 IsValid()==true（这正是 _r4b_physics_final_review §2.1 的假隔离点）。
                bool isDefaultWorld = physicsScene.Equals(Physics.defaultPhysicsScene);
                bool physicsEmpty = !isDefaultWorld && physicsScene.IsEmpty();

                report.Check("isolatedScene",
                    tempScene.IsValid() && physicsScene.IsValid() && !isDefaultWorld,
                    "mode=" + isolation + " scene=" + tempScene.name
                    + " handle=" + tempScene.handle
                    + " sceneValid=" + tempScene.IsValid()
                    + " physicsValid=" + physicsScene.IsValid()
                    + " isDefaultWorld=" + isDefaultWorld
                    + " physicsEmpty=" + physicsEmpty
                    + " activeScene=" + activeBefore.name);

                if (!physicsScene.IsValid() || isDefaultWorld)
                {
                    report.Check("physicsSceneUsable", false,
                        "隔离场景没有可用的**非默认** PhysicsScene，无法继续（绝不退回用户场景的物理世界）。");
                    return report.Build("PMR4 Unity 碰撞适配验证（真实 PhysX）", isolation, cleanupNote);
                }

                // 测试对象必须进临时场景：`new GameObject`/`CreatePrimitive` 落在活动场景，
                // 所以临时把活动场景切到隔离场景（Collider 一创建就注册在隔离世界里），
                // 并在 finally 里还原到 activeBefore。
                activeSwitched = SwitchActiveScene(tempScene, out activeBefore);

                Collider floor = null;
                Collider wall = null;

                GameObject floorObject = NewTestObject("PMR4Floor", tempScene, created);
                floor = AddWorldBox(floorObject, FloorCenter, FloorSize);
                GameObject wallObject = NewTestObject("PMR4Wall", tempScene, created);
                wall = AddWorldBox(wallObject, WallCenter, WallSize);

                report.Check("sceneCollidersReady",
                    floor != null && wall != null && floor.enabled && wall.enabled,
                    "floorBounds=" + DescribeBounds(floor) + " wallBounds=" + DescribeBounds(wall));

                // 几何自洽：契约布局的直接推论，后面所有期望值都建立在这两条上。
                report.Check("floorTopIsZero",
                    floor != null && Math.Abs(floor.bounds.max.y) <= 1e-4f,
                    "floor.bounds.max.y=" + (floor == null ? "null" : floor.bounds.max.y.ToString("R")) + "（期望 0）");
                report.Check("wallRightFaceIsHalf",
                    wall != null && Math.Abs(wall.bounds.max.x - 0.5f) <= 1e-4f,
                    "wall.bounds.max.x=" + (wall == null ? "null" : wall.bounds.max.x.ToString("R")) + "（期望 0.5）");

                query = new PMUnityMoverCollisionQuery(physicsScene, AllLayers, WorldVersion,
                                                        new Collider[] { floor, wall });
                unscopedQuery = new PMUnityMoverCollisionQuery(physicsScene, AllLayers, WorldVersion, null);

                // ============================================================ 1) 地面：贴地 / 悬空 / 穿透
                PMMoverGround touching = query.QueryGround(
                    new PMVector3(3f, HalfHeight, 0f), Radius, HalfHeight, PMMoverDefaults.GroundProbeMeters);
                report.Check("groundTouching",
                    touching.Found && Math.Abs(touching.Distance) <= 0.005f
                    && IsUp(touching.Normal, 2f),
                    touching.ToString() + "（期望 found=true / |distance|≤0.005 / normal≈Up）");

                PMMoverGround floating = query.QueryGround(
                    new PMVector3(3f, HalfHeight + 0.3f, 0f), Radius, HalfHeight, PMMoverDefaults.GroundProbeMeters);
                report.Check("groundFloatingNotFound",
                    !floating.Found && floating.Distance == 0f && floating.Normal.LengthSquared == 0f,
                    floating.ToString() + "（期望 found=false / distance=0 / normal=0：0.3m 悬空超出 0.05m 探测）");

                PMMoverGround penetrating = query.QueryGround(
                    new PMVector3(3f, HalfHeight - 0.15f, 0f), Radius, HalfHeight, PMMoverDefaults.GroundProbeMeters);
                report.Check("groundPenetratingSignedDistance",
                    penetrating.Found && penetrating.Distance < -0.10f && penetrating.Distance > -0.20f,
                    penetrating.ToString() + "（期望 found=true / distance≈-0.15：足底穿入 0.15m）");

                // ============================================================ 2) 扫掠：零 delta / 墙 / 起点重叠
                PMMoverHit zeroDelta = query.Sweep(
                    new PMVector3(3f, HalfHeight, 0f), PMVector3.Zero, Radius, HalfHeight);
                report.Check("sweepZeroDeltaUnblocked",
                    !zeroDelta.Blocking && zeroDelta.Fraction == 1f && zeroDelta.Normal.LengthSquared == 0f,
                    zeroDelta.ToString() + "（期望 blocking=false / fraction=1 / normal=0）");

                PMMoverHit wallHit = query.Sweep(
                    new PMVector3(3f, HalfHeight, 0f), new PMVector3(-3f, 0f, 0f), Radius, HalfHeight);
                // 期望 fraction ≈ (2.6-0.5)/3 = 0.7（胶囊左沿 2.6，墙右面 0.5）；
                // contactOffset 会让接触点略早，因此给 [0.55,0.75] 的区间而不钉死单点。
                report.Check("sweepBlockedByWall",
                    wallHit.Blocking && wallHit.Fraction > 0.55f && wallHit.Fraction < 0.75f
                    && wallHit.Normal.X > 0.9f && Math.Abs(wallHit.Normal.Y) < 0.1f,
                    wallHit.ToString() + "（期望 blocking=true / fraction≈0.70 / normal≈(+1,0,0)）");

                PMMoverHit overlapHit = query.Sweep(
                    new PMVector3(0f, HalfHeight, 0f), new PMVector3(0f, 0f, 1f), Radius, HalfHeight);
                // 起点 (0,1,0) 在墙体内：墙 x∈[-0.5,0.5] 比 z∈[-4,4] 近 ⇒ 最小平移轴必须是 ±X。
                // 修复前这条断言的 |normal|=1 是**假通过**：cast 返回 -dir 假法线 (0,0,-1)，
                // 它指向墙内 4m、不是有效推出方向；故追加「法线必须沿 ±X」这一条实质判据。
                report.Check("sweepResolvesStartOverlap",
                    overlapHit.Blocking && overlapHit.Fraction == 0f
                    && Math.Abs(overlapHit.Normal.Length - 1f) <= 1e-3f
                    && Math.Abs(overlapHit.Normal.X) > 0.9f,
                    overlapHit.ToString() + "（起点 (0,1,0) 在墙体内：期望 blocking=true / fraction=0 / |normal|=1 / 法线沿 ±X 有效推出轴）");

                PMUnityCollisionQueryStats afterSweeps = query.GetStats();
                report.Info("扫掠后计数：" + afterSweeps);
                report.Check("startOverlapPathExercised",
                    afterSweeps.StartOverlapResolutions >= 1,
                    "StartOverlapResolutions=" + afterSweeps.StartOverlapResolutions + "（期望 ≥1）");

                // ============================================================ 2b) 零距离命中语义
                // 真实 PhysX 的 CapsuleCast 对「起点已接触/重叠」的 Collider 返回 distance=0 且 normal=-dir。
                // 贴地（足底正好在 floor 顶面 y=0）时它就以「脚下的地面」报出这条假命中，于是把
                // 水平移动 / 起跳 / 真正的墙 TOI(2.1/3=0.7) 全部抢走，并让「起点嵌墙」分支永不执行
                // （2026-09-20 真实 Play 的 6 项失败全部由它引起）。
                // 这里先把**原始命中**（collider.name / distance / normal）打出来，让该语义在验证工具内
                // 可观测（不进每帧日志），再逐条断言适配器修复后的语义（原有门槛不降低）。
                {
                    RaycastHit[] rawHits = new RaycastHit[PMUnityMoverCollisionQuery.HitBufferCapacity];
                    Vector3 rawPoint1 = new Vector3(3f, HalfHeight + CapsuleSphereOffset, 0f);
                    Vector3 rawPoint2 = new Vector3(3f, HalfHeight - CapsuleSphereOffset, 0f);
                    int rawCount = physicsScene.CapsuleCast(rawPoint1, rawPoint2, Radius,
                        new Vector3(-1f, 0f, 0f), rawHits, 3f, AllLayers, QueryTriggerInteraction.Ignore);

                    StringBuilder rawText = new StringBuilder();
                    for (int i = 0; i < rawCount; i++)
                    {
                        rawText.Append(rawHits[i].collider == null ? "(null)" : rawHits[i].collider.name)
                               .Append(":distance=").Append(rawHits[i].distance.ToString("R"))
                               .Append(",normal=").Append(rawHits[i].normal).Append(" | ");
                    }

                    report.Info("原始 CapsuleCast(3,1,0)→(-1,0,0) 命中数=" + rawCount + "：" + rawText
                                + "（distance=0 且 normal 与扫掠方向相反，是「起点已接触/重叠」的原始报告；"
                                + "其法线与真实接触面无关，因此适配器不再采信它的方向）");

                    PMMoverHit groundTouchHorizontal = query.Sweep(
                        new PMVector3(3f, HalfHeight, 0f), new PMVector3(0f, 0f, 2f), Radius, HalfHeight);
                    report.Check("sweepGroundTouchHorizontalUnblocked",
                        !groundTouchHorizontal.Blocking,
                        groundTouchHorizontal.ToString()
                        + "（期望 blocking=false：贴地不得被脚下的地面判成阻挡，否则角色永远走不动）");

                    PMMoverHit groundTouchUp = query.Sweep(
                        new PMVector3(3f, HalfHeight, 0f), new PMVector3(0f, 0.06f, 0f), Radius, HalfHeight);
                    report.Check("sweepGroundTouchVerticalUpUnblocked",
                        !groundTouchUp.Blocking,
                        groundTouchUp.ToString() + "（期望 blocking=false：贴地起跳的竖直位移不得被地面假命中抵消）");

                    PMMoverHit downFromAbove = query.Sweep(
                        new PMVector3(3f, HalfHeight + 0.3f, 0f), new PMVector3(0f, -0.5f, 0f), Radius, HalfHeight);
                    report.Check("sweepVerticalDownBlockedByFloor",
                        downFromAbove.Blocking && downFromAbove.Fraction > 0.5f
                        && downFromAbove.Fraction < 0.7f && downFromAbove.Normal.Y > 0.9f,
                        downFromAbove.ToString() + "（悬空 0.3m 向下 0.5m：期望 fraction≈0.6 / normal≈Up）");

                    PMMoverHit intoWallAtSkin = query.Sweep(
                        new PMVector3(0.9f, HalfHeight, 0f), new PMVector3(-0.5f, 0f, 0f), Radius, HalfHeight);
                    report.Check("sweepIntoWallBlockedNearZero",
                        intoWallAtSkin.Blocking && intoWallAtSkin.Fraction <= 0.02f
                        && intoWallAtSkin.Normal.X > 0.9f,
                        intoWallAtSkin.ToString() + "（触墙向内：期望 fraction≈0 / normal=(+1,0,0)，绝不漏墙）");

                    PMMoverHit awayFromWall = query.Sweep(
                        new PMVector3(0.9f, HalfHeight, 0f), new PMVector3(0.5f, 0f, 0f), Radius, HalfHeight);
                    report.Check("sweepAwayFromWallUnblocked",
                        !awayFromWall.Blocking,
                        awayFromWall.ToString() + "（触墙朝外：法线与运动同向 ⇒ 不阻挡）");

                    PMMoverHit alongWall = query.Sweep(
                        new PMVector3(0.9f, HalfHeight, 0f), new PMVector3(0f, 0f, 0.5f), Radius, HalfHeight);
                    report.Check("sweepAlongWallUnblocked",
                        !alongWall.Blocking,
                        alongWall.ToString() + "（触墙沿墙：法线与运动垂直 ⇒ 不阻挡）");

                    PMMoverHit highSpeed = query.Sweep(
                        new PMVector3(4f, HalfHeight, 0f), new PMVector3(-6f, 0f, 0f), Radius, HalfHeight);
                    report.Check("sweepHighSpeedDoesNotTunnel",
                        highSpeed.Blocking && highSpeed.Fraction > 0.45f
                        && highSpeed.Fraction < 0.6f && highSpeed.Normal.X > 0.9f,
                        highSpeed.ToString() + "（一帧 6m 也不许穿过墙：期望 fraction≈0.5167）");

                    PMUnityCollisionQueryStats zeroStats = query.GetStats();
                    report.Check("zeroDistanceSemanticsObserved",
                        zeroStats.ZeroDistanceCandidates >= 1 && zeroStats.ZeroDistanceContactsIgnored >= 1,
                        "ZeroDistanceCandidates=" + zeroStats.ZeroDistanceCandidates
                        + " ZeroDistanceContactsIgnored=" + zeroStats.ZeroDistanceContactsIgnored
                        + "（期望都 ≥1：贴地时地面确实以 distance=0 报出、并被几何法线判为不相对而忽略）");
                    report.Info("零距离语义后计数：" + zeroStats);
                }

                // ============================================================ 3) 白名单隔离
                // 造一个**不在白名单**的诱饵盒挡在通往墙的路上：白名单查询必须无视它，
                // 无白名单查询必须被它挡住。这是契约"隔离旧场景"要求的直接证据。
                GameObject decoy = NewTestObject("PMR4Decoy", tempScene, created);
                AddWorldBox(decoy, new Vector3(2f, 1f, 0f), new Vector3(0.4f, 2f, 2f));

                PMMoverHit withAllowlist = query.Sweep(
                    new PMVector3(3f, HalfHeight, 0f), new PMVector3(-3f, 0f, 0f), Radius, HalfHeight);
                PMMoverHit withoutAllowlist = unscopedQuery.Sweep(
                    new PMVector3(3f, HalfHeight, 0f), new PMVector3(-3f, 0f, 0f), Radius, HalfHeight);

                report.Check("allowlistIsolatesOtherColliders",
                    withAllowlist.Blocking && withAllowlist.Fraction > 0.55f
                    && withoutAllowlist.Blocking && withoutAllowlist.Fraction < 0.25f,
                    "白名单=" + withAllowlist + " 无白名单=" + withoutAllowlist
                    + "（期望白名单≈0.70 无视诱饵；无白名单≈0.133 被诱饵挡住）");

                RemoveObject(decoy, created);

                // ============================================================ 3b) 默认物理世界隔离对照（40 个干扰体）
                report.Check("defaultWorldIsolation",
                    CheckDefaultWorldIsolation(physicsScene, created, out string isolationDetail), isolationDetail);

                // ============================================================ 4) 纯模型 + 真实 PhysX：走墙 / 跳落
                PMMoverModel model = new PMMoverModel(query);
                PMMoverAuxState aux = PMMoverAuxState.CreateDefault();
                aux.CollisionWorldVersion = WorldVersion;

                report.Check("walkStopsAtWall",
                    RunWalkIntoWall(model, aux, out string walkDetail), walkDetail);
                report.Check("jumpLandsBackOnFloor",
                    RunJumpAndLand(model, aux, out string jumpDetail), jumpDetail);

                // ============================================================ 5) 饱和必须显式失败
                report.Check("saturationFailsExplicitly",
                    RunSaturationProbe(tempScene, created, out string saturationDetail), saturationDetail);

                // ============================================================ 6) 输入边沿状态机（不读硬件）
                report.Check("jumpEdgeSurvivesZeroStep", CheckJumpEdgeSurvivesZeroStep(), "见用例断言");
                report.Check("inputPureConversions", CheckPureConversions(out string inputDetail), inputDetail);

                // ============================================================ 7) 表现层（仅 Play 模式）
                if (Application.isPlaying)
                {
                    CheckPresentation(report, created, tempScene);
                }
                else
                {
                    report.Info("表现层运行时验证已跳过：编辑模式下 `new GameObject` 会落到用户当前场景并把它标脏"
                                + "（违反契约「不触碰用户场景」）。请在 Play 模式下再点一次本菜单以取得该项证据。");
                }
            }
            catch (Exception ex)
            {
                report.Check("unhandledException", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                // ---- 清理：先摘掉所有测试对象，再关掉临时场景。任何路径都会走到这里。 ----
                for (int i = created.Count - 1; i >= 0; i--)
                {
                    RemoveObject(created[i], null);
                }

                created.Clear();

                try
                {
                    if (tempScene.IsValid() && tempScene.isLoaded)
                    {
                        if (tempSceneIsPreview)
                        {
                            EditorSceneManager.ClosePreviewScene(tempScene);
                        }
                        else if (Application.isPlaying)
                        {
                            SceneManager.UnloadSceneAsync(tempScene);
                        }
                        else
                        {
                            EditorSceneManager.CloseScene(tempScene, true);
                        }
                    }
                }
                catch (Exception cleanupEx)
                {
                    cleanupNote = "清理临时场景失败：" + cleanupEx.GetType().Name + " " + cleanupEx.Message;
                }

                // 活动场景还原：临时切 active 只是为了让测试对象落进临时场景，绝不能留在测试场景。
                if (activeSwitched)
                {
                    try { SceneManager.SetActiveScene(activeBefore); }
                    catch (Exception restoreEx)
                    {
                        cleanupNote = AppendNote(cleanupNote, "还原活动场景失败：" + restoreEx.GetType().Name);
                    }
                }

                // 诚实口径：编辑模式下 `new GameObject` 就算立刻被搬走，也可能把原活动场景标脏。
                // 把“跑前/跑后 isDirty”写进报告，不假装没发生（公开 API 无法回滚脏标记）。
                Scene activeAfter = SceneManager.GetActiveScene();
                bool activeDirtyAfter = activeAfter.IsValid() && activeAfter.isDirty;
                if (activeDirtyBefore != activeDirtyAfter)
                {
                    cleanupNote = AppendNote(cleanupNote,
                        "用户活动场景 isDirty: " + activeDirtyBefore + " -> " + activeDirtyAfter
                        + "（临时对象已全部销毁；脏标记来自编辑期建/搬 GameObject，无法用公开 API 回滚）");
                }
            }

            return report.Build("PMR4 Unity 碰撞适配验证（真实 PhysX）", isolation, cleanupNote);
        }

        // ================================================================================
        //  用例实现
        // ================================================================================

        /// <summary>Walk 用例：从 (3,1,0) 朝 -X 走 2 秒，必须被墙挡住且**永不进入墙内**。</summary>
        private static bool RunWalkIntoWall(PMMoverModel model, PMMoverAuxState aux, out string detail)
        {
            PMMoverSyncState state = PMMoverSyncState.CreateDefault();
            state.Position = new PMVector3(3f, HalfHeight, 0f);

            float minX = state.Position.X;
            long simMs = 0L;
            long frame = 0L;

            const int steps = 125; // 125 × 16ms = 2.0s，足够从 x=3 撞上墙并贴住

            for (int i = 0; i < steps; i++)
            {
                PMMoverInput input = PMUnityMoverInput.Convert(-1f, 0f, 0f, 270f, false);
                PMSimulationResult<PMMoverSyncState, PMMoverAuxState> result =
                    model.Simulate(MakeStep(simMs, frame), input, state, aux);

                state = result.Sync;
                aux = result.Aux;

                if (state.Position.X < minX) { minX = state.Position.X; }

                simMs += ValidationStepMs;
                frame++;
            }

            // 胶囊左沿不得越过墙右面：minX - Radius ≥ 0.5 ⇒ minX ≥ 0.9。
            // 门槛说明：0.02m 覆盖 Physics.defaultContactOffset(0.01) 可能带来的「接触点略早」，
            // 同时把原先的 0.05m（= 半径的 12.5%）收紧到 0.02m；修复后受控基准实测停在 x≈0.901。
            float expectedStop = 0.5f + Radius;
            bool neverEnteredWall = minX >= expectedStop - 0.02f;
            bool actuallyAdvancedAndStopped = minX <= expectedStop + 0.15f;

            detail = "minX=" + minX.ToString("R") + " finalX=" + state.Position.X.ToString("R")
                     + " mode=" + PMMoverModes.Name(state.Mode)
                     + "（期望 minX≈" + expectedStop.ToString("R") + "：既走过去了、也没有穿墙）";
            return neverEnteredWall && actuallyAdvancedAndStopped;
        }

        /// <summary>Jump 用例：原地起跳后必须落回地面并回到 Walking（贴地平移的另一半证据）。</summary>
        private static bool RunJumpAndLand(PMMoverModel model, PMMoverAuxState aux, out string detail)
        {
            PMMoverSyncState state = PMMoverSyncState.CreateDefault();
            state.Position = new PMVector3(6f, HalfHeight, 0f);

            float maxY = state.Position.Y;
            long simMs = 0L;
            long frame = 0L;

            const int steps = 125;

            for (int i = 0; i < steps; i++)
            {
                // 只有第一步带跳跃边沿（模型自己不做去抖，边沿由调用方保证一次）。
                bool jump = i == 0;
                PMMoverInput input = PMUnityMoverInput.Convert(0f, 0f, 0f, 0f, jump);
                PMSimulationResult<PMMoverSyncState, PMMoverAuxState> result =
                    model.Simulate(MakeStep(simMs, frame), input, state, aux);

                state = result.Sync;
                aux = result.Aux;

                if (state.Position.Y > maxY) { maxY = state.Position.Y; }

                simMs += ValidationStepMs;
                frame++;
            }

            bool jumped = maxY > HalfHeight + 0.5f; // 起跳速度 4 m/s、重力 9.81 ⇒ 顶点约 +0.82m
            bool landed = state.Mode == PMMoverMode.Walking && state.Grounded
                          && Math.Abs(state.Position.Y - HalfHeight) <= 0.02f;

            detail = "maxY=" + maxY.ToString("R") + " finalY=" + state.Position.Y.ToString("R")
                     + " mode=" + PMMoverModes.Name(state.Mode) + " grounded=" + state.Grounded
                     + "（期望 maxY>1.5，落回 y≈1 且 mode=Walking）";
            return jumped && landed;
        }

        /// <summary>
        /// 饱和用例：在临时场景里堆一批彼此重叠的薄盒，让一次扫掠的命中数超过适配器缓冲容量，
        /// 必须抛出显式异常并且计数 +1（契约："饱和显式失败而非静默忽略墙"）。
        /// </summary>
        private static bool RunSaturationProbe(Scene tempScene, List<GameObject> created, out string detail)
        {
            for (int i = 0; i < PMUnityMoverCollisionQuery.HitBufferCapacity + 8; i++)
            {
                GameObject slab = NewTestObject("PMR4Slab" + i, tempScene, created);
                AddWorldBox(slab, new Vector3(10f, 1f, 1f + i * 0.05f), new Vector3(0.4f, 2f, 0.2f));
            }

            PMUnityMoverCollisionQuery satQuery =
                new PMUnityMoverCollisionQuery(tempScene.GetPhysicsScene(), AllLayers, WorldVersion, null);

            bool threw = false;
            string message = string.Empty;

            try
            {
                satQuery.Sweep(new PMVector3(10f, HalfHeight, 0f), new PMVector3(0f, 0f, 6f), Radius, HalfHeight);
            }
            catch (InvalidOperationException ex)
            {
                threw = true;
                message = ex.Message;
            }

            PMUnityCollisionQueryStats stats = satQuery.GetStats();
            int expected = PMUnityMoverCollisionQuery.HitBufferCapacity + 8;

            detail = "placedSlabs=" + expected + " threw=" + threw
                     + " saturationFailures=" + stats.SaturationFailures
                     + (message.Length == 0 ? string.Empty : (" message=" + Truncate(message, 120)));

            return threw && stats.SaturationFailures == 1;
        }

        /// <summary>
        /// 跳跃边沿状态机：零 ms 步不能丢边沿；真实步消费后必须清空（"补子步不能重复跳跃"）。
        /// 这条用例**不读硬件**（用 NotifyJumpEdge 注入），因此编辑模式下也能跑。
        /// </summary>
        private static bool CheckJumpEdgeSurvivesZeroStep()
        {
            PMUnityMoverInput input = new PMUnityMoverInput();

            input.NotifyJumpEdge();
            bool buffered = input.HasBufferedJumpEdge;

            input.Consume(0); // 零 ms 占位步
            bool survivedZeroStep = input.HasBufferedJumpEdge;

            input.Consume(PMUnityMoverInput.MaxRealStepMs); // 真实步
            bool consumedByRealStep = !input.HasBufferedJumpEdge;

            input.Consume(PMUnityMoverInput.MaxRealStepMs + 1); // 越界值不是真实步
            bool outOfRangeKeptClear = !input.HasBufferedJumpEdge;

            return buffered && survivedZeroStep && consumedByRealStep && outOfRangeKeptClear;
        }

        /// <summary>纯转换入口的可控断言（不读硬件、不依赖场景）。</summary>
        private static bool CheckPureConversions(out string detail)
        {
            bool clamped = true;
            PMMoverInput high = PMUnityMoverInput.Convert(3f, -2f, 0.5f, 400f, true);
            clamped &= Math.Abs(high.MoveX - 1f) < 1e-6f;
            clamped &= Math.Abs(high.MoveZ + 1f) < 1e-6f;
            clamped &= Math.Abs(high.MoveY - 0.5f) < 1e-6f;
            clamped &= high.JumpPressed;
            clamped &= high.Effects == null && high.Layers == null && high.RemovedLayerIds == null;

            bool yawOk = true;
            yawOk &= Math.Abs(PMUnityMoverInput.DeriveYawDegrees(1f, 0f) - 90f) < 1e-3f;
            yawOk &= Math.Abs(PMUnityMoverInput.DeriveYawDegrees(0f, 1f) - 0f) < 1e-3f;
            yawOk &= Math.Abs(PMUnityMoverInput.DeriveYawDegrees(-1f, 0f) + 90f) < 1e-3f;
            yawOk &= Math.Abs(PMUnityMoverInput.DeriveYawDegrees(-1f, -1f) + 135f) < 1e-3f;
            yawOk &= PMUnityMoverInput.DeriveYawDegrees(0f, 0f) == 0f;

            bool stepOk = PMUnityMoverInput.IsRealStepMs(1) && PMUnityMoverInput.IsRealStepMs(50)
                          && !PMUnityMoverInput.IsRealStepMs(0) && !PMUnityMoverInput.IsRealStepMs(51)
                          && !PMUnityMoverInput.IsRealStepMs(-1);

            bool deadZoneOk = PMUnityMoverInput.ApplyDeadZone(0.1f, 0.2f) == 0f
                              && Math.Abs(PMUnityMoverInput.ApplyDeadZone(0.5f, 0.2f) - 0.5f) < 1e-6f
                              && Math.Abs(PMUnityMoverInput.ApplyDeadZone(-0.5f, 0.2f) + 0.5f) < 1e-6f;

            bool rejectsNaN = false;
            try
            {
                PMUnityMoverInput.Convert(float.NaN, 0f, 0f, 0f, false);
            }
            catch (ArgumentException)
            {
                rejectsNaN = true;
            }

            detail = "clamp=" + clamped + " yaw=" + yawOk + " step=" + stepOk
                     + " deadZone=" + deadZoneOk + " rejectsNaN=" + rejectsNaN;
            return clamped && yawOk && stepOk && deadZoneOk && rejectsNaN;
        }

        /// <summary>
        /// 表现层用例（**仅 Play 模式**）：可见胶囊、无碰撞、Apply 一次写位置/朝向/尺寸、Dispose 幂等。
        /// 编辑模式不跑，因为 `new GameObject` 会落到用户当前场景并把它标脏。
        /// </summary>
        private static void CheckPresentation(Report report, List<GameObject> created, Scene tempScene)
        {
            PMUnityMoverPresentation presentation = null;

            try
            {
                presentation = new PMUnityMoverPresentation("validation", PMNetRole.AutonomousProxy);

                if (presentation.Root != null)
                {
                    SceneManager.MoveGameObjectToScene(presentation.Root, tempScene);
                }

                Collider bodyCollider = presentation.Body == null ? null : presentation.Body.GetComponent<Collider>();
                report.Check("presentationHasNoCollider",
                    bodyCollider == null || !bodyCollider.enabled,
                    "Body collider=" + (bodyCollider == null ? "null（已销毁）" : ("enabled=" + bodyCollider.enabled)));

                report.Check("presentationRootInIsolatedScene",
                    presentation.Root != null && presentation.Root.scene == tempScene,
                    "rootScene=" + (presentation.Root == null ? "null" : presentation.Root.scene.name));

                PMMoverSyncState state = PMMoverSyncState.CreateDefault();
                state.Position = new PMVector3(1f, 2f, 3f);
                state.YawDegrees = 90f;
                state.Scale = 1f;
                presentation.ApplyPredicted(state);

                Transform rootTransform = presentation.Root.transform;
                bool positionOk = (rootTransform.position - new Vector3(1f, 2f, 3f)).magnitude <= 1e-4f;
                float yaw = rootTransform.rotation.eulerAngles.y;
                float yawDelta = Math.Abs(Mathf.DeltaAngle(yaw, 90f));
                bool yawOk = yawDelta <= 0.05f;

                Vector3 bodyScale = presentation.Body.localScale;
                float expectedScaleX = PMMoverDefaults.CapsuleRadiusMeters
                                       / PMUnityMoverPresentation.PrimitiveCapsuleRadius;
                float expectedScaleY = PMMoverDefaults.CapsuleHalfHeightMeters
                                       / PMUnityMoverPresentation.PrimitiveCapsuleHalfHeight;
                bool scaleOk = Math.Abs(bodyScale.x - expectedScaleX) <= 1e-4f
                               && Math.Abs(bodyScale.y - expectedScaleY) <= 1e-4f;

                report.Check("presentationAppliesTransform",
                    positionOk && yawOk && scaleOk,
                    "position=" + rootTransform.position + " yaw=" + yaw.ToString("R")
                    + " bodyScale=" + bodyScale + " source=" + presentation.LastSource);

                presentation.Dispose();
                presentation.Dispose(); // 幂等

                bool disposedThrows = false;
                try
                {
                    presentation.Apply(state);
                }
                catch (ObjectDisposedException)
                {
                    disposedThrows = true;
                }

                report.Check("presentationDisposeIdempotent", disposedThrows,
                    "Dispose 两次不抛；Dispose 后再 Apply 抛 ObjectDisposedException=" + disposedThrows);
            }
            catch (Exception ex)
            {
                report.Check("presentationRuntime", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (presentation != null && !presentation.Disposed)
                {
                    presentation.Dispose();
                }
            }
        }

        // ================================================================================
        //  场景与工具
        // ================================================================================

        /// <summary>
        /// 建一个**隔离**的本地物理场景（`LocalPhysicsMode.Physics3D`），并**用真实 API 结果**确认隔离生效。
        ///
        /// 为什么不再用 preview scene：`EditorSceneManager.NewPreviewScene` 不保证带本地物理场景，
        /// 而 2019.4 文档明确“场景没有本地物理场景时 <c>GetPhysicsScene()</c> 返回
        /// <c>Physics.defaultPhysicsScene</c>”——预览场景正是这种情况，于是“已隔离”的报告会与实际不符。
        ///
        /// 编辑模式：<c>SceneManager.CreateScene</c> 是**运行时** API（文档原文 "at runtime"），
        /// 本方法不假设它在编辑模式可用：照常调用并校验结果；失败或回退到默认物理世界就返回 false，
        /// 由调用方报告“仅 Play 模式可执行”并提前返回（**绝不**把默认物理世界当隔离世界）。
        /// </summary>
        private static bool TryCreateIsolatedPhysicsScene(out Scene scene, out bool isPreview,
                                                          out string mode, out string error)
        {
            scene = default(Scene);
            isPreview = false;
            mode = "(none)";
            error = null;

            string name = "PMR4UnityValidation_" + DateTime.Now.ToString("HHmmssfff");

            try
            {
                scene = SceneManager.CreateScene(name, new CreateSceneParameters(LocalPhysicsMode.Physics3D));
                mode = Application.isPlaying ? "runtime-local-physics-scene" : "editor-runtime-local-physics-scene";
            }
            catch (Exception ex)
            {
                scene = default(Scene);
                error = "创建本地物理场景失败：" + ex.GetType().Name + " " + ex.Message
                        + "。SceneManager.CreateScene 是**运行时** API：本验证只在 Play 模式下执行"
                        + "（不会退回默认物理世界，也没有使用用户场景的 Collider）。";
                return false;
            }

            if (!scene.IsValid() || !scene.isLoaded)
            {
                error = "本地物理场景无效（sceneValid=" + scene.IsValid() + " isLoaded=" + scene.isLoaded + "）";
                PlayerSafeClose(scene, false);
                scene = default(Scene);
                return false;
            }

            PhysicsScene physicsScene = scene.GetPhysicsScene();
            if (!physicsScene.IsValid())
            {
                error = "本地物理场景没有可用的 PhysicsScene（IsValid=false）";
                PlayerSafeClose(scene, false);
                scene = default(Scene);
                return false;
            }

            if (physicsScene.Equals(Physics.defaultPhysicsScene))
            {
                error = "本地物理场景回退到了 Physics.defaultPhysicsScene（GetPhysicsScene 返回默认世界）："
                        + "本模式不支持隔离物理世界，本验证只在 Play 模式下执行；"
                        + "拒绝把默认物理世界当隔离世界（也不会改用普通场景 Collider 假装隔离）。";
                PlayerSafeClose(scene, false);
                scene = default(Scene);
                return false;
            }

            return true;
        }

        /// <summary>
        /// 临时把活动场景切到 <paramref name="target"/>，并回填原活动场景。返回是否真的切了。
        ///
        /// 为什么要切：`new GameObject` / `GameObject.CreatePrimitive` 都落在**活动场景**。
        /// 测试对象必须先出生在临时场景里，才能保证 Collider 一创建就注册在隔离物理世界
        /// （而不是先落到用户场景的默认世界、再被搬走）。调用方**必须**在 finally 里还原。
        /// </summary>
        private static bool SwitchActiveScene(Scene target, out Scene previous)
        {
            previous = SceneManager.GetActiveScene();

            if (!target.IsValid())
            {
                return false;
            }

            if (previous.IsValid() && previous.handle == target.handle)
            {
                return false;
            }

            return SceneManager.SetActiveScene(target);
        }

        /// <summary>
        /// 隔离对照用例（“测试世界真的隔离了默认物理世界”的直接证据）：
        ///
        /// 1) 建**另一个临时场景**并用 `LocalPhysicsMode.None`：它不自带物理场景，
        ///    因此放进去的 Collider 注册在 <see cref="Physics.defaultPhysicsScene"/>（这就是“干扰体”）。
        /// 2) 在隔离世界里用**无白名单**查询沿 +Z 扫过干扰体所在位置：必须畅通无阻
        ///    （无白名单是关键：不能靠 allowlist 后置过滤来伪装隔离）。
        /// 3) 对同一段扫掠在**默认物理世界**上查询（白名单=干扰体本身）：必须被阻挡或顶到缓冲上限
        ///    显式失败，证明干扰体确实在默认世界里。
        ///
        /// 干扰体与其临时场景在本方法内创建、在本方法内清理（不污染用户场景）。
        /// </summary>
        private static bool CheckDefaultWorldIsolation(PhysicsScene isolatedPhysicsScene,
                                                       List<GameObject> created, out string detail)
        {
            detail = string.Empty;

            Scene interferenceScene = default(Scene);
            Scene activeBefore = default(Scene);
            bool activeSwitched = false;
            List<GameObject> slabs = new List<GameObject>();
            List<Collider> interferenceColliders = new List<Collider>();

            try
            {
                string name = "PMR4DefaultWorldInterference_" + DateTime.Now.ToString("HHmmssfff");
                interferenceScene = SceneManager.CreateScene(name, new CreateSceneParameters(LocalPhysicsMode.None));

                if (!interferenceScene.IsValid() || !interferenceScene.isLoaded)
                {
                    detail = "干扰场景无效（LocalPhysicsMode.None 场景创建失败）";
                    return false;
                }

                bool interferenceIsDefaultWorld =
                    interferenceScene.GetPhysicsScene().Equals(Physics.defaultPhysicsScene);

                activeSwitched = SwitchActiveScene(interferenceScene, out activeBefore);

                for (int i = 0; i < InterferenceBodyCount; i++)
                {
                    GameObject slab = new GameObject("PMR4Interference" + i);
                    SceneManager.MoveGameObjectToScene(slab, interferenceScene);

                    Collider collider = AddWorldBox(slab, new Vector3(InterferenceCenterX, 1f, 1f + i * 0.05f),
                                                    new Vector3(0.4f, 2f, 0.2f));

                    slabs.Add(slab);
                    created.Add(slab);
                    interferenceColliders.Add(collider);
                }

                if (activeSwitched)
                {
                    SceneManager.SetActiveScene(activeBefore);
                    activeSwitched = false;
                }

                PMVector3 from = new PMVector3(InterferenceCenterX, HalfHeight, 0f);
                PMVector3 sweep = new PMVector3(0f, 0f, 6f);

                // 隔离世界：**无白名单**（AllLayers + allowlist=null）也应看不到默认世界的干扰体。
                PMUnityMoverCollisionQuery isolatedUnscoped =
                    new PMUnityMoverCollisionQuery(isolatedPhysicsScene, AllLayers, WorldVersion, null);
                PMMoverHit isolatedHit = isolatedUnscoped.Sweep(from, sweep, Radius, HalfHeight);

                // 默认世界：白名单=干扰体本身，必须被挡（若命中数顶到容量则适配器按契约显式失败）。
                PMUnityMoverCollisionQuery defaultQuery = new PMUnityMoverCollisionQuery(
                    Physics.defaultPhysicsScene, AllLayers, WorldVersion, interferenceColliders.ToArray());

                bool defaultThrew = false;
                PMMoverHit defaultHit = default(PMMoverHit);
                try
                {
                    defaultHit = defaultQuery.Sweep(from, sweep, Radius, HalfHeight);
                }
                catch (InvalidOperationException)
                {
                    defaultThrew = true;
                }

                bool isolatedUnaffected = !isolatedHit.Blocking;
                bool defaultSeesInterference = defaultThrew || defaultHit.Blocking;

                detail = "干扰体=" + InterferenceBodyCount
                         + "（另一临时场景 LocalPhysicsMode.None，其物理世界=默认世界：" + interferenceIsDefaultWorld + "）"
                         + " 隔离世界无白名单扫掠=" + isolatedHit
                         + " 默认世界扫掠=" + (defaultThrew ? "命中顶到缓冲上限→显式失败" : defaultHit.ToString())
                         + "（期望：隔离世界不被阻挡，默认世界被阻挡/饱和）";

                return isolatedUnaffected && defaultSeesInterference && interferenceIsDefaultWorld;
            }
            catch (Exception ex)
            {
                detail = "隔离对照用例异常：" + ex.GetType().Name + " " + ex.Message;
                return false;
            }
            finally
            {
                if (activeSwitched)
                {
                    try { SceneManager.SetActiveScene(activeBefore); }
                    catch (Exception) { }
                }

                for (int i = slabs.Count - 1; i >= 0; i--)
                {
                    RemoveObject(slabs[i], created);
                }

                PlayerSafeClose(interferenceScene, false);
            }
        }

        private static string AppendNote(string existing, string note)
        {
            if (string.IsNullOrEmpty(existing))
            {
                return note;
            }

            return existing + " " + note;
        }

        private static void PlayerSafeClose(Scene scene, bool wasPreview)
        {
            try
            {
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    return;
                }

                if (wasPreview)
                {
                    EditorSceneManager.ClosePreviewScene(scene);
                }
                else if (Application.isPlaying)
                {
                    SceneManager.UnloadSceneAsync(scene);
                }
                else
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
            catch (Exception)
            {
                // 清理路径不容许再抛：报告里已经会体现后续用例的失败。
            }
        }

        /// <summary>建一个对象并立刻移进临时场景（`new GameObject` 默认落在活动场景 = 用户场景）。</summary>
        private static GameObject NewTestObject(string name, Scene tempScene, List<GameObject> created)
        {
            GameObject go = new GameObject(name);
            SceneManager.MoveGameObjectToScene(go, tempScene);
            created.Add(go);
            return go;
        }

        /// <summary>
        /// 加一个**世界坐标** BoxCollider（与 PMR3TestScene 的无头路径同手法：
        /// transform 保持 identity，几何全部由 BoxCollider.center/size 决定，
        /// 因此完全不依赖 Transform→PhysX 的同步时机）。
        /// </summary>
        private static Collider AddWorldBox(GameObject go, Vector3 worldCenter, Vector3 size)
        {
            BoxCollider box = go.AddComponent<BoxCollider>();
            box.center = worldCenter;
            box.size = size;
            box.enabled = true;
            return box;
        }

        /// <summary>先禁用碰撞体（立刻退出查询面），再立刻销毁对象。created 非 null 时同时出列。</summary>
        private static void RemoveObject(GameObject go, List<GameObject> created)
        {
            if (go == null)
            {
                return;
            }

            Collider[] colliders = go.GetComponentsInChildren<Collider>();
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null) { colliders[i].enabled = false; }
            }

            UnityEngine.Object.DestroyImmediate(go);

            if (created != null)
            {
                created.Remove(go);
            }
        }

        private static PMTimeStep MakeStep(long simMs, long frame)
        {
            PMTimeStep step = new PMTimeStep();
            step.BaseSimTimeMs = simMs;
            step.StepMs = ValidationStepMs;
            step.ServerFrame = new PMFrameId(PMFrameDomain.AuthorityServer, frame);
            step.ClientInputFrame = new PMFrameId(PMFrameDomain.Input, frame);
            step.IsResimulating = false;
            return step;
        }

        private static bool IsUp(PMVector3 v, float toleranceDegrees)
        {
            if (!v.IsFinite || v.LengthSquared <= PMVector3.Epsilon)
            {
                return false;
            }

            PMVector3 unit = PMVector3.Normalized(v);
            float dot = PMVector3.Dot(unit, PMVector3.Up);
            if (dot > 1f) { dot = 1f; }
            if (dot < -1f) { dot = -1f; }

            float degrees = (float)(Math.Acos(dot) * (180.0 / Math.PI));
            return degrees <= toleranceDegrees;
        }

        private static string DescribeBounds(Collider collider)
        {
            if (collider == null)
            {
                return "null";
            }

            Bounds b = collider.bounds;
            return "min=" + b.min + " max=" + b.max;
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max)
            {
                return text;
            }

            return text.Substring(0, max) + "…";
        }

        private static string Summarize(string report)
        {
            string[] lines = report.Split('\n');
            StringBuilder builder = new StringBuilder();
            int shown = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (line.Contains("[FAIL]") || line.StartsWith("结果：") || line.StartsWith("隔离："))
                {
                    builder.AppendLine(line.Trim());
                    shown++;
                }

                if (shown >= 14)
                {
                    builder.AppendLine("…（完整报告见 Console）");
                    break;
                }
            }

            return builder.ToString();
        }

        /// <summary>验证报告的累加器（PASS/FAIL 计数 + 缩进行）。</summary>
        private sealed class Report
        {
            private readonly List<string> _lines = new List<string>();

            public int Pass;
            public int Fail;

            public void Info(string message)
            {
                _lines.Add("  [info] " + message);
            }

            public void Check(string name, bool ok, string detail)
            {
                if (ok) { Pass++; } else { Fail++; }
                _lines.Add("  [" + (ok ? "PASS" : "FAIL") + "] " + name + " :: " + detail);
            }

            public string Build(string title, string isolation, string tail)
            {
                StringBuilder builder = new StringBuilder();
                builder.AppendLine("===== " + title + " =====");
                builder.AppendLine("隔离：" + isolation);
                builder.AppendLine("结果：" + (Fail == 0 ? "全部通过" : "存在失败") + "（PASS=" + Pass + " FAIL=" + Fail + "）");

                for (int i = 0; i < _lines.Count; i++)
                {
                    builder.AppendLine(_lines[i]);
                }

                if (!string.IsNullOrEmpty(tail))
                {
                    builder.AppendLine("  [info] " + tail.Trim());
                }

                builder.AppendLine("限制（本验证**不**证明）：① 与 UE PhysX 等价；② 真实网络/DS 接线；"
                                   + "③ AP/SP 多端回滚与重同步（属 R4-B3/B4 与 P4B6）。");
                builder.AppendLine("===== end =====");
                return builder.ToString();
            }
        }
    }
}

#endif
