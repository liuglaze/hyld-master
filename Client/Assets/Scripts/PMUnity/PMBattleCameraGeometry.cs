// ============================================================================
//  PMBattleCameraGeometry —— 正式模式本地相机跟随的**纯几何**（无 UnityEngine 依赖）
// ============================================================================
//
//  为什么要有这个文件（根因，不是装饰）：
//    R4-C/C3 的 PMUnityBattlePresentation.CreateTestCamera 把测试相机**挂在角色根节点下**
//    （localPosition=(0,1.5,-6)），而宿主每帧按 Mover 的 predicted Yaw 旋转角色根：
//      · PMUnityMoverInput 的左右键（A/D、Left/Right）会由世界坐标推导 Yaw（DeriveYawDegrees），
//        左键 → -90、右键 → +90、后退 → 180；
//      · 于是"我想让角色转向"会**顺带把镜头一起急转**（子节点继承父节点旋转），
//        表现就是方向键一按镜头 90/180 度瞬转；
//      · 相机贴在角色身后 6m，遇到墙会直接穿进几何体内，出现黑遮挡。
//    本文件把"相机应该在哪、朝哪、近裁面多大、被墙挡住时后退距离多少"从
//    Unity/PhysX 里**抽出来变成纯粹的浮点运算**，这样：
//      · 相机方位角是**固定常量**（与角色 Yaw 无关，结构上不可能再继承按键转向）；
//      · 遮挡回退是纯函数，可用 net8 逐条断言（Tools/PMR4UnityAdapterTest）；
//      · 真实 PhysicsScene 只负责"给我一个障碍距离"，不参与任何几何决策
//        （查询只读表现，绝不改动权威碰撞，也不需要改 DS）。
//
//  与 Unity 的边界（刻意写清楚，避免被误当成"在替身里假验物理"）：
//    · 本文件**不引用** UnityEngine：只用 System.Math 与 PMNet.Mover.PMVector3；
//    · 因此它能在 net8（Tools/PMR4UnityAdapterTest，无 Unity 桩件）里独立执行；
//    · 真实射线/球体投射由宿主（PMClientSessionHost）在**同一隔离 PhysicsScene**里做，
//      本文件只接收它返回的"障碍距离"（float），不自己碰 PhysX。
//
//  坐标与朝向约定（与 Unity Y-up / 项目 Y-up 一致）：
//    · YawDegrees：绕 Y 的世界方位角，0 = 世界 +Z，90 = 世界 +X（正弦/余弦展开同 PMUnityMoverInput）；
//    · PitchDegrees：正值 = 相机在观察目标**上方**并向下俯视；
//    · “相机后向” BackDirection = 从观察目标指向相机位置的单位向量：
//          (-sin(yaw)·cos(pitch), +sin(pitch), -cos(yaw)·cos(pitch))
//      于是相机在 lookTarget + BackDirection × distance 处，且朝向 lookTarget。
//
//  T-PLAY1（本轮）：取景从“诊断选择（yaw 0 / 俯角 12 / 距离 6）”改成**用户选型的旧预制体**：
//    · yaw −90、俯角 68.191、FOV 60、近裁面 0.3（见 LegacyPrefab* 常量）；
//    · 后距 = 旧 HYLDCameraManger 相对偏移 (6,12,0) 的模长 ≈ 13.416（见 LegacyFollow* 常量）；
//    · 位置用 SmoothFollowPosition 做 0.08s 临界阻尼跟随（旧链 SmoothTime），
//      但**不写 rotation**（旧链镜头姿态从不随角色/输入变化）——固定世界 yaw 依旧由
//      <see cref="FixedCameraYawDegrees"/> 从结构上保证。
//
//  T-PLAY2（本轮）：可见角色转身由 <see cref="VisibleFacingYawDegrees"/> 规定：
//    权威 Mover Yaw 直接写在可见节点上，**不带烘焙偏转入参**（旧 Capsule 自带的 270° 不得叠加）。
//
//  旧行为对照（回归反例，只用于证明"新方案不再瞬转"）：
//    · LegacyHierarchyChildCameraWorldYaw(ownerYaw, localYaw) = ownerYaw + localYaw
//      —— 这是**挂根节点**时的世界方位角：owner 转 90/180，相机跟着转 90/180；
//    · FixedCameraYawDegrees(configuredYaw) = configuredYaw（**没有 ownerYaw 入参**）
//      —— 新方案里角色朝向在结构上无法影响相机方位角。
//
//  遮挡回退的**硬不变式**（T-VIS2b 修正，可逐条断言）：
//    · 命中距离 > 0 时，相机到观察目标的距离**严格小于**它 ⇒ 相机永远在障碍物之前。
//      旧实现把结果硬钳到 MinDistanceMeters(1.6)：命中 1.5/0.5/0.1m 时相机被放到 1.6m，
//      也就是**在墙的后面**（"贴近黑遮挡"的成因之一）。安全现已优先于构图；
//    · 遮挡探测的原始输出必须先经 ResolveProbeOcclusion 归一化：**只要有一次命中就绝不
//      返回"无遮挡"**。否则"起点重叠"（PhysX 对球心已在障碍内的命中报 distance ≈ 0）
//      会被当成 0 = 无可用信息，静默退回想要距离（= 穿墙）。
// ============================================================================

using System;
using PMNet.Mover;

namespace PMNet.Unity
{
    /// <summary>
    /// 一次相机求解的结果（纯数据，无 Unity 类型）。
    ///
    /// 宿主拿到它以后只做两件事：把 <see cref="CameraPosition"/> 转成 <c>Vector3</c> 写进相机，
    /// 以及把 <see cref="YawDegrees"/>/<see cref="PitchDegrees"/> 转成 <c>Quaternion.Euler</c>。
    /// </summary>
    public struct PMBattleCameraPose
    {
        /// <summary>相机世界坐标（米）。</summary>
        public PMVector3 CameraPosition;

        /// <summary>固定观察目标（角色世界位置 + 观察高度；**不是**角色朝向的前方点）。</summary>
        public PMVector3 LookTarget;

        /// <summary>固定相机方位角（度）。与角色 Yaw 无关。</summary>
        public float YawDegrees;

        /// <summary>相机俯仰角（度，正值向下俯视）。</summary>
        public float PitchDegrees;

        /// <summary>本次实际使用的后退距离（米，已被遮挡/最小距离钳制）。</summary>
        public float DistanceMeters;

        /// <summary>本次实际使用的近裁面（米，已按实际距离钳制）。</summary>
        public float NearClipMeters;

        /// <summary>本帧是否因可见障碍而把相机拉近（诊断/断言用）。</summary>
        public bool Occluded;
    }

    /// <summary>
    /// 正式模式本地相机的纯几何：固定方位角跟随 + 遮挡回退。
    /// </summary>
    public static class PMBattleCameraGeometry
    {
        /// <summary>
        /// “无遮挡”哨兵值：可见障碍投射没有命中时就返回它（正无穷比 0 更不容易与
        /// “命中了 0 米处的几何”混淆）。
        /// </summary>
        public const float NoOcclusionDistance = float.PositiveInfinity;

        /// <summary>观察目标高度（角色世界 Y 之上的偏移，米）——相机看向角色的上半身。</summary>
        public const float DefaultLookHeightMeters = 1.5f;

        // ---------------------------------------------------------------- T-PLAY1 冻结选型（旧预制体）
        //
        //  来源：用户二次实机反馈后的选型冻结（Docs/plans/net-architecture-migration.md
        //  「实机视觉/操控还原：用户选型冻结」+「T-PLAY 第一阶段接口与验收细化」）。
        //  这些值**不是**本轮自选：它们是旧预制体/旧脚本里读出来的确定值，
        //  下面的常量就是它们的唯一落点（宿主与表现层都从这里取，不再各处硬编码）：
        //    · Client/Assets/HYLD1.0/Resources/Main Camera.prefab：
        //        field of view 60、near clip plane 0.3、far clip plane 1000、
        //        m_LocalEulerAnglesHint = (68.191, −90.00001, 0)；
        //    · 旧 HYLDCameraManger（已退役，见 net-legacy-retirement-contract.md）：
        //        相对角色位置取 temp x = min(6, …)、temp y = min(12, …)，z 保持相机自身 z，
        //        位置用 SmoothDamp(SmoothTime = 0.08) 跟随，**rotation 从不写**（固定为预制体姿态）。
        //  正式源场景 HYLDGame.unity 里的相机是正交尺寸 17，**不是**用户选型，不采用。

        /// <summary>旧预制体 Main Camera.prefab 的透视 FOV（用户选型：透视，非正交）。</summary>
        public const float LegacyPrefabFieldOfViewDegrees = 60f;

        /// <summary>旧预制体 Main Camera.prefab 的近裁面（米）。</summary>
        public const float LegacyPrefabNearClipMeters = 0.3f;

        /// <summary>旧预制体 Main Camera.prefab 的远裁面（米）。</summary>
        public const float LegacyPrefabFarClipMeters = 1000f;

        /// <summary>旧预制体 Main Camera.prefab 的俯仰角（度，正值俯视；m_LocalEulerAnglesHint.x）。</summary>
        public const float LegacyPrefabPitchDegrees = 68.191f;

        /// <summary>旧预制体 Main Camera.prefab 的世界方位角（度；m_LocalEulerAnglesHint.y）。</summary>
        public const float LegacyPrefabYawDegrees = -90f;

        /// <summary>
        /// 旧 HYLDCameraManger 的相机相对角色**水平**偏移（世界轴，米）。
        /// yaw = −90 时它正是相机的水平后撤方向（世界 +X）。
        /// </summary>
        public const float LegacyFollowHorizontalMeters = 6f;

        /// <summary>
        /// 旧 HYLDCameraManger 的相机相对角色**竖直**抬升（世界轴，米）。
        /// z 旧链保持相机自身 z（不自适应）；本模块用固定后向 × 距离表达同一取景，
        /// 因此 z 偏移为 0（见 <see cref="DefaultDistanceMeters"/> 的说明）。
        /// </summary>
        public const float LegacyFollowHeightMeters = 12f;

        /// <summary>旧链相机跟随平滑时间（秒；HYLDCameraManger.SmoothTime）。</summary>
        public const float FollowSmoothTimeSeconds = 0.08f;

        /// <summary>
        /// 跟随目标一次跳变超过它（米）就立即贴合、不平滑：出生、瞬移（服务端重同步/强制差异）、
        /// 换局首帧都不应该看见镜头“慢慢滑过去”。旧链远端角色用的是同一量级的“超过就传送”阈值。
        /// </summary>
        public const float FollowSnapDistanceMeters = 8f;

        /// <summary>
        /// 旧 Capsule 可见子节点在角色 prefab 里**烘焙**的本地 Yaw（度）。
        ///
        /// 为什么要把这个数写进纯模块：它是“可见朝向偏 270°”这条缺陷的量值基准，
        /// 必须能在 net8 门禁里被逐条断言（见 <see cref="VisibleFacingYawDegrees"/>）。
        /// 证据：Resources/PMNet/PlayerVisualV1.prefab 与 Resources/Remake/Player.prefab 的
        /// 直系子节点 Capsule 的 m_LocalEulerAnglesHint 都是 (0, 270, 0)。
        /// </summary>
        public const float LegacyCapsuleBakedLocalYawDegrees = 270f;

        /// <summary>
        /// 旧相对偏移 (6, 12, 0) 的模长（米）—— 相机想要的后撤距离。
        ///
        /// 取模长而不是分别取 6/12：本模块的相机位姿是“观察目标 + 固定后向 × 距离”的球面表达，
        /// 距离取旧偏移长度时，相机到角色的**距离**与旧链一致（取景尺度不变），
        /// 方向由旧预制体姿态（yaw −90 / 俯角 68.191）决定。
        /// 与旧轴对齐偏移的差异（约 −1m 水平 / +0.5m 竖直）已在报告里显式登记：
        /// 用户选型是“不一定像素逐位，优先源 prefab 确定值”。
        /// </summary>
        public static readonly float LegacyFollowOffsetDistanceMeters =
            OffsetLength(LegacyFollowHorizontalMeters, LegacyFollowHeightMeters);

        /// <summary>默认后撤距离（米）= 旧相对偏移 (6,12,0) 的模长 ≈ 13.416。</summary>
        public static readonly float DefaultDistanceMeters = LegacyFollowOffsetDistanceMeters;

        /// <summary>默认俯仰角（度，正值俯视）= 旧预制体 68.191（诊断时期的 12 不再是默认）。</summary>
        public const float DefaultPitchDegrees = LegacyPrefabPitchDegrees;

        /// <summary>
        /// 默认相机方位角（度）——**固定常量**，这就是“不继承按键 Yaw”的落点：
        /// 角色转向 90/180 都不会改变它。取值 = 旧预制体 −90（诊断时期的 0 不再是默认）。
        /// </summary>
        public const float DefaultYawDegrees = LegacyPrefabYawDegrees;

        /// <summary>默认近裁面（米）= 旧预制体 0.3。</summary>
        public const float DefaultNearClipMeters = LegacyPrefabNearClipMeters;

        /// <summary>近裁面下限（米）：再小会放大深度精度问题。</summary>
        public const float MinNearClipMeters = 0.02f;

        /// <summary>
        /// 遮挡回退后的最小后撤距离（米）：**构图期望**，不是硬下限。
        ///
        /// 语义（T-VIS2b 起）：
        ///   · 障碍允许时（命中距离 − 安全边距 &gt;= 它），它是"希望至少有这么远"的下限；
        ///   · 障碍挤压到比它还近时，**安全优先**：结果按命中距离的比例下限退到墙前，
        ///     绝不为了满足它而把相机放到障碍物之后（这正是 T-VIS2b 之前的缺陷）；
        ///   · 因此本常量在任何遮挡下都不会变成"穿墙"的来源，但也**不能**反过来保证
        ///     "相机一定不离角色更近"——那条由观察高度（
        ///     <see cref="DefaultLookHeightMeters"/> 1.5m）高于角色权威胶囊半高
        ///     （<c>PMMoverDefaults.CapsuleHalfHeightMeters</c> 1.0m）在结构上保证。
        /// </summary>
        public const float MinDistanceMeters = 1.6f;

        /// <summary>
        /// 遮挡安全边距（米）：命中点再往外留一段，避免相机贴着墙皮导致近裁面穿透。
        /// 这是"保守相机遮挡"的量化落点。
        /// </summary>
        public const float OcclusionMarginMeters = 0.4f;

        /// <summary>
        /// 相机遮挡探测的球体半径（米）。
        ///
        /// 为什么不用“一条无体积的射线”：相机是有体积的（近裁面 + 视锥宽度），单条射线会从
        /// 墙角/棱边擦过去，导致相机贴墙时仍然穿模。用一个小球体扫掠是**保守近似**：
        /// 命中距离是球心可行距离，宿主再减 <see cref="OcclusionMarginMeters"/> 做安全边距。
        /// 这个值**只影响表现层相机**，不参与任何权威碰撞/Mover 仿真。
        /// </summary>
        public const float ProbeRadiusMeters = 0.25f;

        /// <summary>允许的俯仰角绝对值上界（度），避免非法/极端姿态。</summary>
        public const float MaxAbsPitchDegrees = 85f;

        /// <summary>
        /// 极近遮挡的比例下限：命中距离 × 该系数。
        ///
        /// 为什么需要它：安全边距（<see cref="OcclusionMarginMeters"/>）是固定米数，当命中距离
        /// 比边距还近时（"贴脸"）"命中距离 − 边距"会变成非正数，无法再作为上界。
        /// 此时退化为"命中距离的一半"——对任意 &gt; 0 的命中距离都**严格小于**它，
        /// 因此"相机永远在障碍物之前"这条不变式在任何输入下都成立（
        /// <see cref="ResolveDistance"/>）。
        /// </summary>
        public const float TightOcclusionFraction = 0.5f;

        /// <summary>
        /// 起点重叠/起点接触（PhysX 对"球心已碰到或在障碍内"的命中报 distance ≈ 0）
        /// 归一化后的"贴脸"遮挡距离（米）。
        ///
        /// 必须是**有限正值**：0 在本模块里是"没有可用的遮挡信息"的哨兵语义，
        /// 若把起点重叠原样当作 0 传下去就会静默退回"无遮挡"（相机退到想要距离 = 穿墙）。
        /// 见 <see cref="ResolveProbeOcclusion"/>。
        /// </summary>
        public const float StartOverlapOcclusionMeters = 0.001f;

        /// <summary>浮点比较用的极小量。</summary>
        public const float Epsilon = 1e-5f;

        // ---------------------------------------------------------------- 纯判定

        /// <summary>有限值判定（NaN/±Inf 都不是有限值）。</summary>
        public static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>纯角度归一化到 (-180, 180]（与 PMUnityMoverInput.NormalizeDegrees 同口径）。</summary>
        public static float NormalizeDegrees(float degrees)
        {
            RequireFinite(degrees, "degrees");

            float value = degrees % 360f;
            if (value <= -180f) { value += 360f; }
            if (value > 180f) { value -= 360f; }
            return value;
        }

        /// <summary>
        /// 固定相机方位角：**故意不接受角色 Yaw 入参**。
        ///
        /// 这不是"少写一个参数"，而是把"镜头不继承按键转向"做成结构约束：
        /// 只要调用方用的是本函数，就不可能把 ownerYaw 混进来。对照
        /// <see cref="LegacyHierarchyChildCameraWorldYaw"/>（旧挂根节点的错误行为）。
        /// </summary>
        public static float FixedCameraYawDegrees(float configuredYawDegrees)
        {
            return NormalizeDegrees(configuredYawDegrees);
        }

        /// <summary>
        /// **旧行为**（挂角色根节点）的世界方位角 = 父节点 Yaw + 本地 Yaw。
        ///
        /// 只用于回归反例/文档：本函数在正式相机路径上**没有任何调用点**。
        /// 它的存在是为了让"方向键导致镜头 90/180 瞬转"这一现象在测试里可被逐条断言，
        /// 而不是只写在注释里。
        /// </summary>
        public static float LegacyHierarchyChildCameraWorldYaw(float ownerYawDegrees, float localYawDegrees)
        {
            RequireFinite(ownerYawDegrees, "ownerYawDegrees");
            RequireFinite(localYawDegrees, "localYawDegrees");
            return NormalizeDegrees(ownerYawDegrees + localYawDegrees);
        }

        // ---------------------------------------------------------------- T-PLAY2 可见朝向

        /// <summary>
        /// 可见角色的世界朝向 Yaw（度）：**故意不接受“烘焙偏转”入参**。
        ///
        /// 为什么这是正确的一侧（代码事实）：
        ///   · 旧链的 HYLDPlayerController 把 `selfTransform`（Inspector 里实际指向的正是
        ///     角色 prefab 的 **Capsule 子节点**，见 Resources/Remake/Player.prefab 的
        ///     `selfTransform: {fileID: <Capsule Transform>}`）LookAt 到移动方向 —— 也就是说
        ///     旧游戏的“转身”本来就是把权威朝向写在**可见节点**上；
        ///   · 烘焙产物 Resources/PMNet/PlayerVisualV1.prefab 里 Capsule 自带了
        ///     <see cref="LegacyCapsuleBakedLocalYawDegrees"/>（270°）的本地旋转；
        ///   · 如果把权威 Yaw 写在**角色根**上，那个 270 就会沿层级叠加，
        ///     可见朝向 = 权威 Yaw + 270 —— 正是实机上“角色转身和画面看到的朝向对不上”的形态
        ///     （对照 <see cref="LegacyHierarchyChildCameraWorldYaw"/>，回归反例）。
        ///
        /// 本函数把“把 Yaw 写在可见节点上”做成**结构约束**：没有烘焙偏转入参，
        /// 调用方就不可能再把它混进来（与 <see cref="FixedCameraYawDegrees"/> 同一手法）。
        /// 本函数**只做表现层映射**：不修改权威 Mover.Yaw、不参与任何网络/协议/攻击朝向。
        /// </summary>
        public static float VisibleFacingYawDegrees(float authoritativeYawDegrees)
        {
            return NormalizeDegrees(authoritativeYawDegrees);
        }

        // ---------------------------------------------------------------- 平滑跟随（旧镜头手感）

        /// <summary>
        /// 临界阻尼平滑跟随（与 UnityEngine.Vector3.SmoothDamp / Mathf.SmoothDamp 同算法，
        /// maxSpeed = +∞ 即不额外限速），但**实现是纯浮点**。
        ///
        /// 为什么要纯实现而不在宿主里直接调 Mathf.SmoothDamp：
        ///   · 旧链镜头手感就是这个 SmoothTime = <see cref="FollowSmoothTimeSeconds"/> 的跟随；
        ///   · 把平滑抽到这里就能用 net8 逐条断言“收敛、不过冲、大跳变直接贴合”，
        ///     而不用把表现层搬进替身（那等于假验 Unity）；
        ///   · 不在宿主里每轴写一遍，避免四处漏改。
        ///
        /// 语义：
        ///   · 一次跳变 |target − current| ≥ <paramref name="snapDistanceMeters"/> ⇒ 立即贴合
        ///     （速度归零）：出生/瞬移/重同步不等镜头滑过去；
        ///   · 否则逐轴临界阻尼追随，**不过冲**（越过目标就停在目标上）。
        /// </summary>
        /// <param name="current">上一帧的跟随位置。</param>
        /// <param name="target">本帧想要的位置。</param>
        /// <param name="velocity">平滑速度状态（逐帧带回）。</param>
        /// <param name="smoothTimeSeconds">平滑时间（秒，有限正值）。</param>
        /// <param name="deltaSeconds">本帧时长（秒，非负有限；0 表示本帧不平滑）。</param>
        /// <param name="snapDistanceMeters">贴合阈值（米，有限正值）。</param>
        public static PMVector3 SmoothFollowPosition(PMVector3 current, PMVector3 target,
                                                     ref PMVector3 velocity,
                                                     float smoothTimeSeconds, float deltaSeconds,
                                                     float snapDistanceMeters)
        {
            RequireFinite(current, "current");
            RequireFinite(target, "target");
            RequireFinite(smoothTimeSeconds, "smoothTimeSeconds");
            RequireFinite(deltaSeconds, "deltaSeconds");
            RequireFinite(snapDistanceMeters, "snapDistanceMeters");

            if (smoothTimeSeconds <= 0f)
            {
                throw new ArgumentOutOfRangeException("smoothTimeSeconds",
                    "SmoothFollowPosition: 平滑时间必须是有限正值，实际 " + smoothTimeSeconds.ToString("R"));
            }

            if (deltaSeconds < 0f)
            {
                throw new ArgumentOutOfRangeException("deltaSeconds",
                    "SmoothFollowPosition: 帧时长不得为负，实际 " + deltaSeconds.ToString("R"));
            }

            if (snapDistanceMeters <= 0f)
            {
                throw new ArgumentOutOfRangeException("snapDistanceMeters",
                    "SmoothFollowPosition: 贴合阈值必须是有限正值，实际 " + snapDistanceMeters.ToString("R"));
            }

            double dx = (double)target.X - current.X;
            double dy = (double)target.Y - current.Y;
            double dz = (double)target.Z - current.Z;

            if (Math.Sqrt(dx * dx + dy * dy + dz * dz) >= snapDistanceMeters)
            {
                velocity = new PMVector3(0f, 0f, 0f);
                return target;
            }

            float omega = 2f / smoothTimeSeconds;

            float vx = velocity.X;
            float vy = velocity.Y;
            float vz = velocity.Z;

            float ox = SmoothDampAxis(current.X, target.X, ref vx, omega, deltaSeconds);
            float oy = SmoothDampAxis(current.Y, target.Y, ref vy, omega, deltaSeconds);
            float oz = SmoothDampAxis(current.Z, target.Z, ref vz, omega, deltaSeconds);

            velocity = new PMVector3(vx, vy, vz);
            return new PMVector3(ox, oy, oz);
        }

        // ---------------------------------------------------------------- 几何

        /// <summary>
        /// 观察目标：角色世界位置 + 观察高度（纯加法，不含任何朝向信息）。
        /// </summary>
        public static PMVector3 ComputeLookTarget(PMVector3 ownerPosition, float lookHeightMeters)
        {
            RequireFinite(ownerPosition, "ownerPosition");
            RequireFinite(lookHeightMeters, "lookHeightMeters");

            return new PMVector3(ownerPosition.X, ownerPosition.Y + lookHeightMeters, ownerPosition.Z);
        }

        /// <summary>
        /// 从观察目标指向相机位置的单位后向向量。
        /// yaw/pitch 都被归一化/钳制后使用，因此返回的一定是有限单位向量。
        /// </summary>
        public static PMVector3 BackDirection(float yawDegrees, float pitchDegrees)
        {
            float yaw = NormalizeDegrees(yawDegrees);
            float pitch = ClampPitch(pitchDegrees);

            double yawRadians = yaw * (Math.PI / 180.0);
            double pitchRadians = pitch * (Math.PI / 180.0);

            double cosPitch = Math.Cos(pitchRadians);
            double sinPitch = Math.Sin(pitchRadians);

            // 与 Unity Quaternion.Euler(pitch, yaw, 0) 的前向 (-sin(yaw)cos(pitch), sin(pitch), -cos(yaw)cos(pitch)) 相反。
            float x = (float)(-Math.Sin(yawRadians) * cosPitch);
            float y = (float)sinPitch;
            float z = (float)(-Math.Cos(yawRadians) * cosPitch);

            return new PMVector3(x, y, z);
        }

        /// <summary>
        /// 遮挡回退：把"想要的后撤距离"按"可见障碍命中距离"钳制。
        ///
        /// **不变式（T-VIS2b 起，可断言）**：<paramref name="occlusionDistanceMeters"/> &gt; 0 时
        /// 返回值**严格小于**它 —— 相机永远在障碍物之前。
        ///
        /// 为什么之前不是这样（缺陷事实）：旧实现把结果硬钳到
        /// <see cref="MinDistanceMeters"/>（1.6m），于是命中 1.5/0.5/0.1m 时相机被放到 1.6m，
        /// 也就是**在墙的后面**（"贴墙黑遮挡/穿模"的成因之一）。
        ///
        /// 规则（单条、可断言）：
        ///   · 非有限或 ≤ 0 → 视为"没有可用的遮挡信息"，用想要的距离；
        ///   · 命中距离 − 安全边距 ≥ 想要距离 → 用想要的距离（远墙不缩短）；
        ///   · 否则上界 = 命中距离 − 安全边距（边距 &gt; 0 时它严格小于命中距离）；
        ///     边距为 0（退化配置）或上界 ≤ 0（贴脸）时，上界 = 命中距离 ×
        ///     <see cref="TightOcclusionFraction"/>，仍然严格小于命中距离；
        ///   · 下限 = min(最小距离, 想要距离, 命中距离 × TightOcclusionFraction)
        ///     —— 构图期望，但**自身也被严格限制在命中距离之内**；
        ///   · 结果 = clamp(上界, 下限, 想要距离)。
        ///
        /// 因此"安全（严格在障碍物之前）"永远优先于"构图（不贴进角色）"；
        /// 远墙、命中 3m、命中点比想要距离更远、无命中哨兵等既有行为逐字不变。
        /// </summary>
        public static float ResolveDistance(float desiredDistanceMeters,
                                            float occlusionDistanceMeters,
                                            float minDistanceMeters,
                                            float marginMeters)
        {
            RequireFinite(desiredDistanceMeters, "desiredDistanceMeters");
            RequireFinite(minDistanceMeters, "minDistanceMeters");
            RequireFinite(marginMeters, "marginMeters");

            if (desiredDistanceMeters <= 0f)
            {
                throw new ArgumentOutOfRangeException("desiredDistanceMeters",
                    "ResolveDistance: 想要的距离必须是有限正值，实际 " + desiredDistanceMeters.ToString("R"));
            }

            if (minDistanceMeters < 0f)
            {
                throw new ArgumentOutOfRangeException("minDistanceMeters",
                    "ResolveDistance: 最小距离不得为负，实际 " + minDistanceMeters.ToString("R"));
            }

            if (marginMeters < 0f)
            {
                throw new ArgumentOutOfRangeException("marginMeters",
                    "ResolveDistance: 安全边距不得为负，实际 " + marginMeters.ToString("R"));
            }

            // 无可用遮挡信息：非有限或 ≤ 0 —— 唯一允许"当无遮挡"的路径。
            // （真实命中的 distance ≈ 0 由 ResolveProbeOcclusion 先归一成有限正值，不走这里。）
            if (!IsFinite(occlusionDistanceMeters) || occlusionDistanceMeters <= 0f)
            {
                return desiredDistanceMeters;
            }

            // 远墙：留出安全边距后仍不比想要的距离近 ⇒ 不缩短（不误判）。
            if (occlusionDistanceMeters - marginMeters >= desiredDistanceMeters)
            {
                return desiredDistanceMeters;
            }

            float proportional = occlusionDistanceMeters * TightOcclusionFraction;

            // 上界：必须落在 (0, 命中距离) 内 —— 相机在与"障碍平面"的最近方向上仍然在墙前。
            float upper = occlusionDistanceMeters - marginMeters;
            if (!(upper < occlusionDistanceMeters) || upper <= 0f)
            {
                upper = proportional;
            }

            if (upper > desiredDistanceMeters)
            {
                upper = desiredDistanceMeters;
            }

            // 下限：构图期望，但自身也被 TIGHT 比例压到命中距离之内（所以绝不会把相机推过墙）。
            float floor = minDistanceMeters < desiredDistanceMeters ? minDistanceMeters : desiredDistanceMeters;
            if (proportional < floor) { floor = proportional; }

            return floor > upper ? floor : upper;
        }

        /// <summary>
        /// 近裁面：不超过**实际距离的一半**（避免近裁面越过观察目标），且不低于
        /// <see cref="MinNearClipMeters"/> —— 但当实际距离本身小于两倍下限时（T-VIS2b 的极近遮挡），
        /// 下限自动降到距离的一半，保证"近裁面 &lt; 实际距离"在**任何**距离下都成立。
        /// 非法输入（非有限或 ≤ 0）显式抛异常，不把坏近裁面写进相机。
        /// </summary>
        public static float ResolveNearClip(float desiredNearClipMeters, float distanceMeters)
        {
            RequireFinite(desiredNearClipMeters, "desiredNearClipMeters");
            RequireFinite(distanceMeters, "distanceMeters");

            if (desiredNearClipMeters <= 0f)
            {
                throw new ArgumentOutOfRangeException("desiredNearClipMeters",
                    "ResolveNearClip: 近裁面必须是有限正值，实际 " + desiredNearClipMeters.ToString("R"));
            }

            if (distanceMeters <= 0f)
            {
                throw new ArgumentOutOfRangeException("distanceMeters",
                    "ResolveNearClip: 实际距离必须是有限正值，实际 " + distanceMeters.ToString("R"));
            }

            float half = distanceMeters * 0.5f;
            float floor = MinNearClipMeters < half ? MinNearClipMeters : half;

            float result = desiredNearClipMeters;
            if (result > half) { result = half; }
            if (result < floor) { result = floor; }
            return result;
        }

        // ---------------------------------------------------------------- 遮挡命中归一化

        /// <summary>
        /// 命中缓冲是否**饱和**：批量重载不会为我们排序，被丢掉的命中里理论上
        /// 可能有更近的一面墙。饱和不是"无遮挡"，但因信息不完备**无法强保证**
        /// 拿到的一定是最近命中 —— 调用方应把它计数/上日志，不要静默忽略（见报告残留风险）。
        /// </summary>
        public static bool IsProbeSaturated(int rawHitCount, int bufferCapacity)
        {
            return rawHitCount >= bufferCapacity;
        }

        /// <summary>
        /// 把"一次球体扫掠的原始输出"归一成 <see cref="ResolveDistance"/> 的遮挡距离入参。
        ///
        /// 不变式：**只要报告了至少一次命中（rawHitCount &gt; 0），就绝不返回
        /// <see cref="NoOcclusionDistance"/>**。这堵住了两类静默失败：
        ///   · 起点重叠/起点接触：PhysX 对"球心已在障碍内"的命中报 distance ≈ 0，
        ///     而 0 在本模块里是"无可用遮挡信息"的哨兵 ⇒ 归一为
        ///     <see cref="StartOverlapOcclusionMeters"/>（正数，导致相机收到最短，而不是退回想要距离）；
        ///   · 命中距离不可用（NaN/±Inf/负）：同样保守归一到贴脸距离，不退回"无遮挡"。
        ///
        /// 唯一允许"当无遮挡"的路径是 rawHitCount ≤ 0（一次都没命中）。
        /// </summary>
        public static float ResolveProbeOcclusion(int rawHitCount, float nearestHitDistanceMeters)
        {
            if (rawHitCount <= 0)
            {
                return NoOcclusionDistance;
            }

            if (!IsFinite(nearestHitDistanceMeters) || nearestHitDistanceMeters <= 0f)
            {
                return StartOverlapOcclusionMeters;
            }

            return nearestHitDistanceMeters;
        }

        // ---------------------------------------------------------------- 求解

        /// <summary>
        /// 求解一帧相机位姿。**没有任何角色 Yaw 入参**：观察方位角只由
        /// <paramref name="configuredYawDegrees"/> 决定（见 <see cref="FixedCameraYawDegrees"/>）。
        /// </summary>
        /// <param name="ownerPosition">角色世界位置（米，Y-up）。</param>
        /// <param name="lookHeightMeters">观察目标高度偏移（米）。</param>
        /// <param name="desiredDistanceMeters">想要的后撤距离（米）。</param>
        /// <param name="configuredYawDegrees">固定相机方位角（度）。</param>
        /// <param name="pitchDegrees">俯仰角（度，正值俯视）。</param>
        /// <param name="desiredNearClipMeters">想要的近裁面（米）。</param>
        /// <param name="occlusionDistanceMeters">可见障碍命中距离（米）；无命中用 <see cref="NoOcclusionDistance"/>。</param>
        /// <param name="minDistanceMeters">遮挡回退的最小距离（米）。</param>
        /// <param name="marginMeters">遮挡安全边距（米）。</param>
        public static PMBattleCameraPose Compute(PMVector3 ownerPosition,
                                                 float lookHeightMeters,
                                                 float desiredDistanceMeters,
                                                 float configuredYawDegrees,
                                                 float pitchDegrees,
                                                 float desiredNearClipMeters,
                                                 float occlusionDistanceMeters,
                                                 float minDistanceMeters,
                                                 float marginMeters)
        {
            RequireFinite(lookHeightMeters, "lookHeightMeters");

            PMVector3 lookTarget = ComputeLookTarget(ownerPosition, lookHeightMeters);

            return ComputeAtLookTarget(lookTarget, desiredDistanceMeters, configuredYawDegrees, pitchDegrees,
                                       desiredNearClipMeters, occlusionDistanceMeters, minDistanceMeters,
                                       marginMeters);
        }

        /// <summary>
        /// 求解一帧相机位姿，但观察目标**已给定**（宿主先用 <see cref="SmoothFollowPosition"/> 把
        /// 跟随目标平滑到当前帧，再求位姿 —— 这样镜头手感可被 net8 逐条断言，而不是把
        /// 平滑散在 Unity 宿主里）。
        ///
        /// 与 <see cref="Compute"/> 完全同构，同样**没有任何角色 Yaw 入参**。
        /// </summary>
        public static PMBattleCameraPose ComputeAtLookTarget(PMVector3 lookTarget,
                                                            float desiredDistanceMeters,
                                                            float configuredYawDegrees,
                                                            float pitchDegrees,
                                                            float desiredNearClipMeters,
                                                            float occlusionDistanceMeters,
                                                            float minDistanceMeters,
                                                            float marginMeters)
        {
            RequireFinite(lookTarget, "lookTarget");

            float distance = ResolveDistance(desiredDistanceMeters, occlusionDistanceMeters,
                                             minDistanceMeters, marginMeters);
            float nearClip = ResolveNearClip(desiredNearClipMeters, distance);

            float yaw = FixedCameraYawDegrees(configuredYawDegrees);
            float pitch = ClampPitch(pitchDegrees);

            PMVector3 back = BackDirection(yaw, pitch);
            PMVector3 cameraPosition = new PMVector3(lookTarget.X + back.X * distance,
                                                     lookTarget.Y + back.Y * distance,
                                                     lookTarget.Z + back.Z * distance);

            PMBattleCameraPose pose = new PMBattleCameraPose();
            pose.CameraPosition = cameraPosition;
            pose.LookTarget = lookTarget;
            pose.YawDegrees = yaw;
            pose.PitchDegrees = pitch;
            pose.DistanceMeters = distance;
            pose.NearClipMeters = nearClip;
            pose.Occluded = distance < desiredDistanceMeters - Epsilon;
            return pose;
        }

        /// <summary>俯仰角钳制（同时拒绝 NaN/±Inf）。</summary>
        public static float ClampPitch(float pitchDegrees)
        {
            RequireFinite(pitchDegrees, "pitchDegrees");

            if (pitchDegrees > MaxAbsPitchDegrees) { return MaxAbsPitchDegrees; }
            if (pitchDegrees < -MaxAbsPitchDegrees) { return -MaxAbsPitchDegrees; }
            return pitchDegrees;
        }

        // ---------------------------------------------------------------- 内部

        /// <summary>旧相对偏移 (水平, 抬升, 0) 的模长（米）。</summary>
        private static float OffsetLength(float horizontalMeters, float heightMeters)
        {
            return (float)Math.Sqrt((double)(horizontalMeters * horizontalMeters
                                             + heightMeters * heightMeters));
        }

        /// <summary>
        /// 单轴临界阻尼平滑（UnityEngine.Mathf.SmoothDamp 在 maxSpeed = +∞ 下的同一算式）。
        /// 越过目标就停在目标上（与 Unity 同款收尾），因此不会振荡/过冲。
        /// </summary>
        private static float SmoothDampAxis(float current, float target, ref float velocity,
                                            float omega, float deltaSeconds)
        {
            float x = omega * deltaSeconds;
            float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);

            float change = current - target;
            float originalTo = target;

            float temp = (velocity + omega * change) * deltaSeconds;
            velocity = (velocity - omega * temp) * exp;

            float output = target + (change + temp) * exp;

            if ((originalTo - current > 0f) == (output > originalTo))
            {
                output = originalTo;
                velocity = 0f;
            }

            return output;
        }

        private static void RequireFinite(PMVector3 value, string name)
        {
            if (!value.IsFinite)
            {
                throw new ArgumentException(
                    "PMBattleCameraGeometry: " + name + " 含非有限值（NaN/Inf），拒绝据此求解相机位姿。"
                    + "实际 (" + value.X.ToString("R") + ", " + value.Y.ToString("R") + ", "
                    + value.Z.ToString("R") + ")", name);
            }
        }

        private static void RequireFinite(float value, string name)
        {
            if (!IsFinite(value))
            {
                throw new ArgumentException(
                    "PMBattleCameraGeometry: " + name + " 必须是有限值，实际 " + value.ToString("R"), name);
            }
        }
    }
}
