// ============================================================================
//  PMUnityBattlePresentation —— R4-C / C2 正式角色的表现层（唯一 Transform 写者）
// ============================================================================
//
//  契约来源：Docs/plans/net-r4c-content-contract.md 的「C2 运行内容适配」一节。逐条落点：
//    · 「PMUnityBattlePresentation IDisposable：(label,role,manifest)」
//    · 「加载清理后的 PlayerVisualV1」—— 只实例化 **C1 生成的干净 prefab**
//      （Resources 键来自 manifest.playerResource），**绝不**在运行期 Instantiate
//      旧 Resources/Remake/Player.prefab 再禁脚本（那会把 missing script 与旧驱动带进场景）。
//    · 「拒绝 DS/Authority 创建」—— <see cref="PMNetRuntime.IsDedicatedServer"/> 或
//      <see cref="PMNetRole.Authority"/> 一律显式抛异常（不是"悄悄不建"）。
//    · 「验证无旧脚本/Collider/Rigidbody」
//    · 「ApplyPredicted/ApplyInterpolated(PMMoverSyncState) 唯一 Transform 写入口，
//       yaw/position/scale」—— 一次写根节点的 position/rotation/localScale；
//       子层级（骨骼/网格）一律不写（骨骼交给 Animator）。
//    · 「Animator 按确实存在的 Speed 参数驱动、root motion 关闭」—— 参数表里没有
//       Float 类型的 "Speed" 就不设（绝不猜参数名、绝不 AddParameter）。
//    · 「平滑不是第二仿真，不查询物理」—— 本类**不做**任何插值/平滑/物理查询：
//       SP 的插值由驱动侧（PMMoverSyncState 输出）完成，本类只负责"把状态搬到 Transform"。
//    · 「可选 CreateTestCamera 独立相机只跟新根、不读旧静态，不修改旧相机，Dispose 清理」
//      —— **修订（本次视觉修复）**：相机**不再挂到角色根节点下**。挂根节点会继承根节点的
//      Yaw 旋转，而宿主每帧按 Mover predicted Yaw 旋转根 ⇒ 左右方向键会把镜头一起急转
//      （90/180 度瞬转）。现在相机是独立顶层对象，世界位姿由宿主每帧通过
//      <see cref="ApplyTestCameraPose"/> 写入；几何决策（固定方位角 + 遮挡回退）在纯几何
//      模块 PMBattleCameraGeometry 里，本类只写 Transform，不做任何几何/物理决策。
//      这条修订同时消除了"相机挂在会旋转的父节点上"这一结构性隐患（而不是打补丁）。
//    · 「提供 Transform Root 供未来宿主绑定 UI，相机不强制创建」
//
//  为什么"唯一 Transform 写者"是硬边界：
//    旧链上 Player.prefab 挂着 HYLDPlayerController（Update 里写 position/LookAt、
//    FixedUpdate 里写 Animator Speed）与 PlayerLogic（写 body position / 开关 BoxCollider）。
//    在新链里它们没有任何位置：运动真值只有 PMR4MovementDriver + PMNet 复制/RPC。
//    因此表现层必须：(a) 只接受 SyncState；(b) 只写根节点；(c) 绝不查询物理、绝不推进仿真。
//
//  允许/禁止的组件（写在这里作为给 C1 的接口约束，与 C1 契约的"保留清单"一致）：
//    允许：Transform / MeshFilter / Renderer（含 MeshRenderer、SkinnedMeshRenderer 等子类）/
//          Animator / LODGroup；
//    另外（复核新增）：MeshFilter 的网格引用必须真的在（"角色是不是可见"不能只看类型白名单）；
//    骨骼/材质可用性在 C1 的烘焙回读自检里把关 —— 本文件不引用 Material/Renderer.sharedMaterials/
//    SkinnedMeshRenderer.bones，以免给 Tools/PMUnityGlueCheck 的桩件增加另一组需同步维护的成员。
//    致命：missing script（GetComponents 的 null 项）、任何 MonoBehaviour、Collider、
//          Rigidbody、Camera、AudioListener、以及任何其它类型（报类型全名，便于 C1 精确删除）。
//
//  Unity 依赖面：GameObject / Transform / Animator / Camera / Resources / Object / Application，
//  以及 manifest 类（后者只依赖 JsonUtility）。不引用任何旧业务类型。
// ============================================================================

using System;
using PMNet.Mover;
using UnityEngine;

namespace PMNet.Unity
{
    /// <summary>
    /// 一个角色副本的正式可见表现（C1 烘焙的 PlayerVisualV1）。
    ///
    /// 宿主按网络角色（AP/SP）决定把"预测输出"还是"插值输出"喂给
    /// <see cref="ApplyPredicted"/> / <see cref="ApplyInterpolated"/>，本类不替宿主做选择，
    /// 也不自己采样网络状态。
    /// </summary>
    public sealed class PMUnityBattlePresentation : IDisposable
    {
        /// <summary>
        /// 测试相机占位后撤距离（米）。
        ///
        /// **只用于建立相机时的占位位姿**（见 <see cref="PlaceholderCameraLookHeightMeters"/>）；
        /// 正式模式下每帧的权威位姿由宿主用 <c>PMBattleCameraGeometry</c> 求解后经
        /// <see cref="ApplyTestCameraPose"/> 写入。取值与旧预制体选型一致，
        /// 使“第一次 ApplyTestCameraPose 之前的那一帧”看上去不跳。
        /// </summary>
        public const float DefaultCameraDistanceMeters = PlaceholderCameraDistanceMeters;

        /// <summary>Animator 里用于驱动移动表现的速度参数名（与旧链一致）。</summary>
        public const string SpeedParameterName = "Speed";

        /// <summary>Animator Speed 参数的取值范围上界（归一化到 0..1，与旧链的摇杆量级一致）。</summary>
        public const float MaxSpeedParameterValue = 1f;

        /// <summary>
        /// 旧链的**可见转身节点**名（直系子节点）。
        ///
        /// 证据：Resources/Remake/Player.prefab 的 `HYLDPlayerController.selfTransform` 指向的
        /// 就是这个子节点（旧代码 LookAt 移动方向的就是它），C1 烘焙保留同名层级
        /// （Resources/PMNet/PlayerVisualV1.prefab 根下唯一一个 Capsule）。
        ///
        /// 缺它的正式内容一律**显式失败**（见构造函数）：把权威 Yaw 写在根上会与 Capsule
        /// 烘焙的本地 Yaw 270° 叠加 ⇒ 可见朝向错 270°。
        /// </summary>
        public const string FacingNodeName = "Capsule";

        /// <summary>
        /// 旧 Capsule 节点在 prefab 里**烘焙**的本地 Yaw（度）。
        /// 它不得参与可见朝向：权威 Yaw 直接写在 Capsule 节点上，烘焙偏转在结构上被覆盖。
        /// 同值常量也在纯几何模块（<c>PMBattleCameraGeometry.LegacyCapsuleBakedLocalYawDegrees</c>，
        /// 那里带 net8 断言；本文件不引用那个类型，因为 Tools/PMBattleContentRuntimeCheck 不编译它）。
        /// </summary>
        public const float BakedFacingNodeLocalYawDegrees = 270f;

        // ---- 相机占位取景（仅“宿主写入第一帧位姿之前的那一瞬间”有效）----
        //
        //  值与 PMBattleCameraGeometry 的旧预制体冻结选型一致（Main Camera.prefab：
        //  FOV 60 / near 0.3 / far 1000 / eulerHint (68.191, −90)；旧 HYLDCameraManger 偏移 (6,12,0)）。
        //  这里**不引用** PMBattleCameraGeometry（Tools/PMBattleContentRuntimeCheck 不编译它，
        //  加引用会直接打断那个编译门）；**生产路径**是宿主用显式重载把同一组常量传进来
        //  （见 PMClientSessionHost.EnsureMovementRig），因此权威值只有一处。
        private const float PlaceholderCameraDistanceMeters = 13.416408f;
        private const float PlaceholderCameraYawDegrees = -90f;
        private const float PlaceholderCameraPitchDegrees = 68.191f;
        private const float PlaceholderCameraLookHeightMeters = 1.5f;
        private const float PlaceholderCameraFieldOfViewDegrees = 60f;
        private const float PlaceholderCameraNearClipMeters = 0.3f;
        private const float PlaceholderCameraFarClipMeters = 1000f;

        private readonly string _label;
        private readonly PMNetRole _role;
        private readonly PMBattleContentManifest _manifest;
        private readonly GameObject _root;

        /// <summary>
        /// 可见转身节点（旧链 HYLDPlayerController.selfTransform 指向的同一个子节点）。
        /// 权威 Yaw 只写在这里；角色根只写位置/scale。
        /// </summary>
        private readonly Transform _facingNode;

        private readonly Animator _animator;
        private readonly int _animatorCount;
        private readonly bool _animatorHasSpeedParameter;

        private Camera _testCamera;

        /// <summary>
        /// 测试相机自己的 GameObject（**独立顶层对象，不是角色根的子节点**）。
        /// 独立持有它才能保证 Dispose 时相机一定被销毁（相机不随角色根节点一起销毁）。
        /// </summary>
        private GameObject _testCameraObject;

        private int _testCameraPoseCount;
        private bool _disposed;

        private int _applyCount;
        private PMMoverSyncState _lastApplied;
        private string _lastSource = "none";
        private float _lastSpeedParameterValue;
        private float _lastAppliedFacingYawDegrees;

        /// <summary>
        /// 建出一个角色的正式表现（不建相机）。
        /// </summary>
        /// <param name="label">角色标识（进 GameObject 名字与诊断）。</param>
        /// <param name="role">该副本的网络角色：只接受 AutonomousProxy / SimulatedProxy。</param>
        /// <param name="manifest">已通过强校验的 manifest（提供 playerResource 键）。为 null 或校验失败一律抛。</param>
        public PMUnityBattlePresentation(string label, PMNetRole role, PMBattleContentManifest manifest)
            : this(label, role, manifest, false)
        {
        }

        /// <summary>
        /// 建出一个角色的正式表现，可选同时建一个测试用相机。
        /// </summary>
        /// <param name="createTestCamera">
        /// true 时在角色根节点下建一个测试相机。默认 false：宿主自行取舍，本类绝不自动创建。
        /// </param>
        public PMUnityBattlePresentation(string label, PMNetRole role, PMBattleContentManifest manifest,
                                         bool createTestCamera)
        {
            if (PMNetRuntime.IsDedicatedServer)
            {
                throw new InvalidOperationException(
                    "PMUnityBattlePresentation: 专用服务器上禁止创建表现对象（契约：DS 拒绝创建）。"
                    + "宿主必须先按 PMNetRuntime.IsDedicatedServer / PMNetRole 判定，DS 路径不要实例化本类。");
            }

            if (PMNetRoles.IsAuthority(role))
            {
                throw new InvalidOperationException(
                    "PMUnityBattlePresentation: 权威副本不得创建表现对象（契约：拒绝 DS/Authority 创建）。"
                    + "权威侧只有 PMR4MovementDriver 的仿真真值，没有任何可见体。");
            }

            if (PMNetRoles.IsNone(role))
            {
                throw new InvalidOperationException(
                    "PMUnityBattlePresentation: 角色为 None（未参与复制）不得创建表现对象；"
                    + "客户端副本只能是 AutonomousProxy 或 SimulatedProxy。");
            }

            string manifestError;
            if (!PMBattleContentManifest.Validate(manifest, out manifestError))
            {
                throw new ArgumentException(
                    "PMUnityBattlePresentation: manifest 未通过强校验，拒绝用它的资源键加载角色表现。"
                    + "原因：" + manifestError, "manifest");
            }

            _label = label ?? string.Empty;
            _role = role;
            _manifest = manifest.Clone();   // 自己持有一份，避免外部改写影响资源键

            GameObject prefab;
            try
            {
                prefab = Resources.Load<GameObject>(_manifest.playerResource);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "PMUnityBattlePresentation: 读取角色表现 prefab 失败（键 \"" + _manifest.playerResource + "\"）："
                    + ex.GetType().Name + " " + ex.Message);
            }

            if (prefab == null)
            {
                throw new InvalidOperationException(
                    "PMUnityBattlePresentation: Resources 缺少角色表现 prefab（键 \"" + _manifest.playerResource
                    + "\"）：正式内容尚未生成。先执行 Editor 菜单 Build/Prepare PMNet Battle Content（C1）。"
                    + "本版**不**回退旧 Resources/Remake/Player.prefab。");
            }

            GameObject instance = UnityEngine.Object.Instantiate(prefab);
            instance.name = BuildRootName(_label, _role);

            if (!instance.activeInHierarchy)
            {
                DestroyObject(instance);
                throw new InvalidOperationException(
                    "PMUnityBattlePresentation: 角色表现 prefab 根节点未激活（activeInHierarchy=false）："
                    + "C1 必须保存一个激活的干净 prefab，否则角色在场景里不可见。");
            }

            Animator animator;
            int animatorCount;
            string componentError;

            if (!ValidateComponents(instance, out animator, out animatorCount, out componentError))
            {
                DestroyObject(instance);
                throw new InvalidOperationException("PMUnityBattlePresentation: " + componentError);
            }

            // 可见转身节点：严格拒绝缺 Capsule 的正式内容（见 FacingNodeName 的说明）。
            Transform facingNode;
            string facingError;
            if (!TryFindFacingNode(instance, instance.transform, out facingNode, out facingError))
            {
                DestroyObject(instance);
                throw new InvalidOperationException("PMUnityBattlePresentation: " + facingError);
            }

            _facingNode = facingNode;
            _root = instance;
            _animator = animator;
            _animatorCount = animatorCount;

            if (_animator != null)
            {
                // 契约：root motion 关闭。即便 C1 已烘焙为 false，这里也显式再设一次——
                // root motion 一旦为真，Animator 会自己写 Transform，与"唯一 Transform 写者"直接冲突。
                _animator.applyRootMotion = false;
                _animatorHasSpeedParameter = HasFloatParameter(_animator, SpeedParameterName);
            }

            PMMoverSyncState initial = PMMoverSyncState.CreateDefault();
            _lastApplied = initial;
            _root.transform.position = new Vector3(initial.Position.X, initial.Position.Y, initial.Position.Z);
            _root.transform.rotation = Quaternion.identity;
            _root.transform.localScale = Vector3.one * initial.Scale;
            _facingNode.rotation = Quaternion.Euler(0f, initial.YawDegrees, 0f);
            _lastAppliedFacingYawDegrees = initial.YawDegrees;

            if (createTestCamera)
            {
                CreateTestCamera(DefaultCameraDistanceMeters);
            }
        }

        // ---------------------------------------------------------------- 属性

        /// <summary>角色标识（构造时传入）。</summary>
        public string Label { get { return _label; } }

        /// <summary>该副本的网络角色（AutonomousProxy / SimulatedProxy）。</summary>
        public PMNetRole Role { get { return _role; } }

        /// <summary>本表现使用的 manifest 副本（宿主只读）。</summary>
        public PMBattleContentManifest Manifest { get { return _manifest; } }

        /// <summary>角色表现 prefab 的 Resources 键（诊断/对账用）。</summary>
        public string PlayerResourceKey { get { return _manifest.playerResource; } }

        /// <summary>
        /// 角色根节点 Transform（供宿主绑定 UI / 跟随目标）。已释放时为 null。
        ///
        /// 本类只写**两个** Transform：
        ///   · 根：位置 + localScale（**不写旋转**，恒为单位阵）；
        ///   · 可见转身节点（<see cref="FacingNode"/>）：世界 Yaw。
        /// 若未来 UI 需要“朝向”，请读 <see cref="FacingNode"/> 的朝向而不是根。
        /// </summary>
        public Transform Root { get { return _disposed ? null : _root.transform; } }

        /// <summary>角色根节点 GameObject（诊断用）。已释放时为 null。</summary>
        public GameObject RootObject { get { return _disposed ? null : _root; } }

        /// <summary>表现用的 Animator；prefab 里没有则为 null（没有也允许，只是不驱动动画）。</summary>
        public Animator Animator { get { return _disposed ? null : _animator; } }

        /// <summary>
        /// 可见转身节点（名字恒为 <see cref="FacingNodeName"/>）。已释放时为 null。
        /// 权威 Yaw 只写在这个节点上（旧链的转身语义）。
        /// </summary>
        public Transform FacingNode { get { return _disposed ? null : _facingNode; } }

        /// <summary>最近一次写到可见节点上的世界 Yaw（度；诊断/断言用）。</summary>
        public float LastAppliedFacingYawDegrees { get { return _lastAppliedFacingYawDegrees; } }

        /// <summary>prefab 里的 Animator 数量（&gt;1 时只使用第一个；数量本身作为资源异常信号暴露）。</summary>
        public int AnimatorCount { get { return _animatorCount; } }

        /// <summary>Animator 参数表里是否**确实**存在 Float 类型的 "Speed"（不存在就不设）。</summary>
        public bool AnimatorHasSpeedParameter { get { return _animatorHasSpeedParameter; } }

        /// <summary>测试相机；未创建或已释放时为 null。</summary>
        public Camera TestCamera { get { return _disposed ? null : _testCamera; } }

        /// <summary>测试相机的 GameObject（独立顶层对象）；未创建或已释放时为 null。</summary>
        public GameObject TestCameraObject { get { return _disposed ? null : _testCameraObject; } }

        /// <summary>
        /// <see cref="ApplyTestCameraPose"/> 被成功执行的次数（诊断用）。
        /// 宿主在正式模式下应当每帧 +1；死亡/冻结静止时不再增长（相机停住）。
        /// </summary>
        public int TestCameraPoseCount { get { return _testCameraPoseCount; } }

        /// <summary><see cref="Apply"/> 调用次数（诊断用）。</summary>
        public int ApplyCount { get { return _applyCount; } }

        /// <summary>最近一次应用的状态（诊断/断言用）。</summary>
        public PMMoverSyncState LastApplied { get { return _lastApplied; } }

        /// <summary>最近一次驱动来源："predicted" / "interpolated" / "none"。</summary>
        public string LastSource { get { return _lastSource; } }

        /// <summary>最近一次写给 Animator 的 Speed 值（没有 Speed 参数时恒为 0）。</summary>
        public float LastSpeedParameterValue { get { return _lastSpeedParameterValue; } }

        /// <summary>是否已释放。</summary>
        public bool Disposed { get { return _disposed; } }

        // ---------------------------------------------------------------- 应用（唯一写入口）

        /// <summary>
        /// 应用一个运动状态：**一次**写可见转身节点的绕 Y 朝向、角色根的位置与整体缩放，
        /// 并按需写 Animator Speed。
        ///
        /// 为什么 Yaw 写在**可见节点**而不是角色根（本轮修复）：
        ///   · 旧链 HYLDPlayerController 的 `selfTransform` 指向的正是 Capsule 可见子节点，
        ///     LookAt 移动方向的就是它 —— “跟移动方向转身”就是把这颗节点的世界朝向写成权威 Yaw；
        ///   · 烘焙产物里 Capsule 自带 270° 本地 Yaw（<see cref="BakedFacingNodeLocalYawDegrees"/>）。
        ///     若把 Yaw 写在根上，它就会沿层级叠加，可见朝向 = 权威 Yaw + 270 ⇒ 实机上看就是
        ///     “角色转身和画面看到的朝向对不上”。直接写节点世界朝向则在结构上覆盖烘焙偏转。
        ///   · 角色根因此只做位置/scale（root.rotation 恒为单位阵）。
        ///
        /// 本方法**不**改权威 Mover.Yaw、不参与网络/协议，也**不**触碰攻击/瞄准方向：
        /// 攻击朝向仍由宿主按 Mover predicted yaw 决定（下一 UI 阶段才迁到独立瞄准输入）。
        ///
        /// 不做任何仿真/插值/物理查询/建删对象。非法输入（NaN/Inf、Scale ≤ 0）显式抛异常
        /// —— 绝不把坏值悄悄写进 Transform。
        /// </summary>
        public void Apply(PMMoverSyncState state)
        {
            RequireAlive();
            RequireFinite(state);

            // 根：位置 + scale（不写 yaw —— 见上面的层级说明）。
            _root.transform.position = new Vector3(state.Position.X, state.Position.Y, state.Position.Z);
            _root.transform.rotation = Quaternion.identity;
            _root.transform.localScale = Vector3.one * state.Scale;

            // 可见节点：权威 Mover Yaw → 世界 Yaw（烘焙的本地 270° 不参与）。
            _facingNode.rotation = Quaternion.Euler(0f, state.YawDegrees, 0f);
            _lastAppliedFacingYawDegrees = state.YawDegrees;

            if (_animatorHasSpeedParameter && _animator != null && _animator.isActiveAndEnabled)
            {
                float speed = ComputeSpeedParameterValue(state);
                _animator.SetFloat(SpeedParameterName, speed);
                _lastSpeedParameterValue = speed;
            }

            _lastApplied = state;
            _applyCount++;
        }

        /// <summary>AP（拥有者客户端）路径：喂**预测输出**。写入面与 <see cref="Apply"/> 完全相同。</summary>
        public void ApplyPredicted(PMMoverSyncState state)
        {
            _lastSource = "predicted";
            Apply(state);
        }

        /// <summary>SP（模拟端）路径：喂**插值输出**（插值在驱动侧完成）。写入面与 <see cref="Apply"/> 完全相同。</summary>
        public void ApplyInterpolated(PMMoverSyncState state)
        {
            _lastSource = "interpolated";
            Apply(state);
        }

        /// <summary>
        /// 把同步状态映射成 Animator 的 Speed 参数（0..1，水平速率 / 最大速率，钳制到
        /// [0, <see cref="MaxSpeedParameterValue"/>]）。
        ///
        /// 为什么归一化：旧链写入的是摇杆量级 0..1（HYLDPlayerController.FixedUpdate 写
        /// playerMoveMagnitude），Animator 的混合树是按这个量级建的；新链的速度是米/秒，
        /// 不做归一化会让混合树直接饱和在奔跑状态。
        /// 这是 C2 的表现映射选择（不是协议），已登记在报告里供 C3/美术确认。
        /// </summary>
        public static float ComputeSpeedParameterValue(PMMoverSyncState state)
        {
            float horizontal = (float)Math.Sqrt((double)(state.Velocity.X * state.Velocity.X
                                                         + state.Velocity.Z * state.Velocity.Z));
            float maxSpeed = state.MaxSpeed;

            float normalized;
            if (maxSpeed > 0f && !float.IsNaN(maxSpeed) && !float.IsInfinity(maxSpeed))
            {
                normalized = horizontal / maxSpeed;
            }
            else
            {
                normalized = horizontal > 0f ? 1f : 0f;
            }

            if (float.IsNaN(normalized)) { return 0f; }
            if (normalized <= 0f) { return 0f; }
            if (normalized >= MaxSpeedParameterValue) { return MaxSpeedParameterValue; }
            return normalized;
        }

        // ---------------------------------------------------------------- 可选测试相机

        /// <summary>
        /// 显式创建一个只属于本角色的测试相机（占位取景 = <see cref="DefaultCameraDistanceMeters"/> 等常量）。
        ///
        /// 生产路径请用带取景参数的重载（宿主把 <c>PMBattleCameraGeometry</c> 的旧预制体常量传进来），
        /// 这里保留单参形态是为了不改变 C2 既有公开面。
        /// </summary>
        public Camera CreateTestCamera(float distanceMeters)
        {
            return CreateTestCamera(distanceMeters, PlaceholderCameraYawDegrees, PlaceholderCameraPitchDegrees,
                                    PlaceholderCameraLookHeightMeters, PlaceholderCameraFieldOfViewDegrees,
                                    PlaceholderCameraNearClipMeters, PlaceholderCameraFarClipMeters);
        }

        /// <summary>
        /// 显式创建一个只属于本角色的测试相机，并给定完整取景（后距/方位角/俯角/观察高度/FOV/近远裁面）。
        ///
        /// **关键约束**：相机建在**独立的顶层 GameObject** 上，**不**挂在角色根节点下。
        /// 挂根节点会继承根节点的旋转，方向键会让镜头跟着急转；独立对象使“镜头不继承按键转向”
        /// 成为结构保证（而方位角本身由宿主的固定常量为准，本类不做任何几何决策）。
        /// 相机的世界位姿由宿主每帧调用 <see cref="ApplyTestCameraPose"/> 写入；
        /// 这里只给一个与权威求解同形的占位位姿，且**不读**任何旧静态数据
        /// （旧链的 HYLDCameraManger 读 HYLDStaticValue.Players[...]；本类全程不接触它）。
        /// **不碰** Camera.main 与任何既有相机；重复调用只返回已存在的那一个。
        /// </summary>
        public Camera CreateTestCamera(float distanceMeters, float yawDegrees, float pitchDegrees,
                                       float lookHeightMeters, float fieldOfViewDegrees,
                                       float nearClipMeters, float farClipMeters)
        {
            RequireAlive();

            if (_testCamera != null)
            {
                return _testCamera;
            }

            if (float.IsNaN(distanceMeters) || float.IsInfinity(distanceMeters) || distanceMeters <= 0f)
            {
                throw new ArgumentOutOfRangeException("distanceMeters",
                    "CreateTestCamera: 距离必须是有限正值，实际 " + distanceMeters.ToString("R"));
            }

            if (float.IsNaN(yawDegrees) || float.IsInfinity(yawDegrees)
                || float.IsNaN(pitchDegrees) || float.IsInfinity(pitchDegrees)
                || float.IsNaN(lookHeightMeters) || float.IsInfinity(lookHeightMeters))
            {
                throw new ArgumentException(
                    "CreateTestCamera: 取景角度/观察高度含非有限值，拒绝建立相机。");
            }

            if (float.IsNaN(fieldOfViewDegrees) || float.IsInfinity(fieldOfViewDegrees)
                || fieldOfViewDegrees <= 0f || fieldOfViewDegrees >= 180f)
            {
                throw new ArgumentOutOfRangeException("fieldOfViewDegrees",
                    "CreateTestCamera: FOV 必须是 (0,180) 内的有限值，实际 " + fieldOfViewDegrees.ToString("R"));
            }

            if (float.IsNaN(nearClipMeters) || float.IsInfinity(nearClipMeters) || nearClipMeters <= 0f
                || float.IsNaN(farClipMeters) || float.IsInfinity(farClipMeters) || farClipMeters <= nearClipMeters)
            {
                throw new ArgumentOutOfRangeException("nearClipMeters",
                    "CreateTestCamera: 近/远裁面必须是有限值且 0 < near < far，实际 near="
                    + nearClipMeters.ToString("R") + " far=" + farClipMeters.ToString("R"));
            }

            // 占位位姿：与权威求解同形（观察目标 + 后向 × 距离，朝向即看向观察目标）。
            Vector3 lookTarget = _root.transform.position + new Vector3(0f, lookHeightMeters, 0f);
            Vector3 back = BackDirectionPlaceholder(yawDegrees, pitchDegrees);

            GameObject cameraObject = new GameObject(BuildCameraName(_label, _role));
            // 独立顶层对象：不给 parent，因此永远不会继承角色根的朝向。
            cameraObject.transform.position = lookTarget + back * distanceMeters;
            cameraObject.transform.rotation = Quaternion.Euler(pitchDegrees, yawDegrees, 0f);

            Camera camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.08f, 0.09f, 0.11f, 1f);
            camera.fieldOfView = fieldOfViewDegrees;
            camera.nearClipPlane = nearClipMeters;
            camera.farClipPlane = farClipMeters;

            _testCameraObject = cameraObject;
            _testCamera = camera;
            return _testCamera;
        }

        /// <summary>
        /// 把一个**世界坐标位姿**写进测试相机（宿主每帧调用）。
        ///
        /// 只做三件事：写世界位置、写世界旋转、按实际距离写入近裁面。
        /// **不做**几何决策（那在宿主的 PMBattleCameraGeometry）、**不查询物理**、
        /// **不写**角色根 Transform —— 相机与角色根彻底解耦，因此角色怎么转都不会带着镜头转。
        /// </summary>
        /// <param name="cameraPosition">相机世界坐标（米）。</param>
        /// <param name="yawDegrees">固定的相机方位角（度）；**不得**传入角色 Yaw。</param>
        /// <param name="pitchDegrees">俯仰角（度，正值俯视）。</param>
        /// <param name="nearClipMeters">近裁面（米，有限正值）。</param>
        public void ApplyTestCameraPose(PMVector3 cameraPosition, float yawDegrees, float pitchDegrees,
                                        float nearClipMeters)
        {
            RequireAlive();

            if (_testCamera == null || _testCameraObject == null)
            {
                throw new InvalidOperationException(
                    "PMUnityBattlePresentation.ApplyTestCameraPose: 尚未创建测试相机，宿主必须先调用 CreateTestCamera。");
            }

            if (!cameraPosition.IsFinite)
            {
                throw new ArgumentException(
                    "PMUnityBattlePresentation.ApplyTestCameraPose: 相机位置含非有限值，拒绝写入 Transform。");
            }

            if (float.IsNaN(yawDegrees) || float.IsInfinity(yawDegrees)
                || float.IsNaN(pitchDegrees) || float.IsInfinity(pitchDegrees))
            {
                throw new ArgumentException(
                    "PMUnityBattlePresentation.ApplyTestCameraPose: 相机朝向含非有限值，拒绝写入 Transform。");
            }

            if (float.IsNaN(nearClipMeters) || float.IsInfinity(nearClipMeters) || nearClipMeters <= 0f)
            {
                throw new ArgumentException(
                    "PMUnityBattlePresentation.ApplyTestCameraPose: 近裁面必须是有限正值，实际 "
                    + nearClipMeters.ToString("R"));
            }

            _testCameraObject.transform.position =
                new Vector3(cameraPosition.X, cameraPosition.Y, cameraPosition.Z);
            _testCameraObject.transform.rotation = Quaternion.Euler(pitchDegrees, yawDegrees, 0f);
            _testCamera.nearClipPlane = nearClipMeters;
            _testCameraPoseCount++;
        }

        // ---------------------------------------------------------------- 释放

        /// <summary>
        /// 幂等释放：先销毁**独立**的测试相机对象（它不随角色根节点销毁），再销毁角色根节点
        /// （连带骨骼/网格）并清空引用。重复调用是空操作。
        /// 释放后 <see cref="Root"/>/<see cref="Animator"/>/<see cref="TestCamera"/> 为 null，
        /// 再 Apply 抛 <see cref="ObjectDisposedException"/>（不静默写坏 Transform）。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // 相机在独立顶层对象上，**必须**单独销毁：漏掉它就等于在退局/换局后留下一个活跃相机。
            GameObject cameraObject = _testCameraObject;
            _testCameraObject = null;
            _testCamera = null;

            if (cameraObject != null)
            {
                DestroyObject(cameraObject);
            }

            GameObject root = _root;
            if (root != null)
            {
                DestroyObject(root);
            }
        }

        // ---------------------------------------------------------------- 内部

        private void RequireAlive()
        {
            if (_disposed || _root == null)
            {
                throw new ObjectDisposedException(
                    "PMUnityBattlePresentation(" + _label + ")",
                    "表现对象已释放：释放后不得再 Apply（宿主必须在断线/离场时先停止驱动再 Dispose）。");
            }
        }

        private static void RequireFinite(PMMoverSyncState state)
        {
            if (!state.Position.IsFinite || !state.Velocity.IsFinite)
            {
                throw new ArgumentException(
                    "PMUnityBattlePresentation.Apply: 状态位置/速度含非有限值，拒绝写入 Transform。");
            }

            if (float.IsNaN(state.YawDegrees) || float.IsInfinity(state.YawDegrees))
            {
                throw new ArgumentException(
                    "PMUnityBattlePresentation.Apply: YawDegrees 非有限值，拒绝写入 Transform。");
            }

            if (float.IsNaN(state.Scale) || float.IsInfinity(state.Scale) || state.Scale <= 0f)
            {
                throw new ArgumentException(
                    "PMUnityBattlePresentation.Apply: Scale 必须是有限正值，实际 " + state.Scale.ToString("R"));
            }
        }

        /// <summary>
        /// 严格查找可见转身节点：名字恰为 <see cref="FacingNodeName"/> 的**直系子节点**，
        /// 必须**恰好一个**。找到 0 个或多个一律显式失败（不猜、不退化到根节点）。
        ///
        /// 为什么不用 Transform.GetChild/childCount：本文件也要在 Tools/PMUnityGlueCheck 的
        /// UnityEngine 桩件下编译，而那个桩件只提供了 name/parent/GetComponentsInChildren 这一组
        /// 成员（不得为一条查找去改其它工具的文件）。用 GetComponentsInChildren + parent 过滤
        /// 与 GetChild 同义，且**只看直系子节点**（递归匹配会让“层级变了”这类真问题被静默掩盖）。
        /// </summary>
        private static bool TryFindFacingNode(GameObject instance, Transform root,
                                              out Transform facingNode, out string error)
        {
            facingNode = null;
            error = null;

            if (instance == null || root == null)
            {
                error = "角色表现根节点为空，无法定位可见转身节点。";
                return false;
            }

            Transform found = null;
            int count = 0;
            Transform[] all = instance.GetComponentsInChildren<Transform>(true);

            for (int i = 0; i < all.Length; i++)
            {
                Transform candidate = all[i];
                if (candidate == null || candidate == root) { continue; }
                if (!string.Equals(candidate.name, FacingNodeName, StringComparison.Ordinal)) { continue; }
                if (candidate.parent != root) { continue; }

                if (found == null) { found = candidate; }
                count++;
            }

            if (count != 1)
            {
                error = "角色表现 prefab 的可见转身节点不唯一（期望直系子节点名恰为 \"" + FacingNodeName
                        + "\" 的那个，实际 " + count.ToString() + " 个）。"
                        + "旧链的转身就是把权威 Yaw 写在该节点上（HYLDPlayerController.selfTransform）；"
                        + "缺它就只能写在根上，而那会与烘焙的本地 Yaw "
                        + BakedFacingNodeLocalYawDegrees.ToString("R") + "° 叠加 ⇒ 可见朝向错 270°。"
                        + "实际直系子节点：" + DescribeChildren(instance, root);
                return false;
            }

            facingNode = found;
            return true;
        }

        /// <summary>列出直系子节点名（失败信息用；只读 name/parent，不引用其它类型）。</summary>
        private static string DescribeChildren(GameObject instance, Transform root)
        {
            if (instance == null || root == null) { return "<null>"; }

            Transform[] all = instance.GetComponentsInChildren<Transform>(true);
            string names = string.Empty;
            int count = 0;

            for (int i = 0; i < all.Length; i++)
            {
                Transform candidate = all[i];
                if (candidate == null || candidate == root) { continue; }
                if (candidate.parent != root) { continue; }

                if (count > 0) { names += ", "; }
                names += candidate.name;
                count++;
            }

            return count == 0 ? "<无>" : names;
        }

        /// <summary>
        /// 占位取景用的后向向量（与 PMBattleCameraGeometry.BackDirection 同一算式）。
        ///
        /// 为什么在这里重写一遍：Tools/PMBattleContentRuntimeCheck 只编译本文件，不含
        /// PMBattleCameraGeometry.cs，加引用会直接打断那个编译门。权威位姿仍由宿主每帧写入，
        /// 这里的值只影响“第一帧位姿被写入之前”的占位画面。
        /// </summary>
        private static Vector3 BackDirectionPlaceholder(float yawDegrees, float pitchDegrees)
        {
            double yawRadians = (double)yawDegrees * (Math.PI / 180.0);
            double pitchRadians = (double)pitchDegrees * (Math.PI / 180.0);
            double cosPitch = Math.Cos(pitchRadians);

            return new Vector3((float)(-Math.Sin(yawRadians) * cosPitch),
                               (float)Math.Sin(pitchRadians),
                               (float)(-Math.Cos(yawRadians) * cosPitch));
        }

        /// <summary>组件校验：只允许"干净表现"的组件集；抓到旧脚本/碰撞/刚体/相机就显式失败。</summary>
        private static bool ValidateComponents(GameObject instance, out Animator animator, out int animatorCount,
                                               out string error)
        {
            animator = null;
            animatorCount = 0;
            error = null;

            Component[] components = instance.GetComponentsInChildren<Component>(true);

            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];

                if (component == null)
                {
                    error = "角色表现 prefab 含 **missing script**（组件数组中存在 null 项）："
                            + "C1 必须把旧脚本彻底删除（不是禁用）；本版本拒绝加载带缺失脚本的资源。";
                    return false;
                }

                if (component is Transform) { continue; }        // Transform / RectTransform

                if (component is MeshFilter)
                {
                    // 白名单只保证**类型**合法；网格引用是否存在决定角色可不可见，必须显式检查。
                    MeshFilter meshFilter = (MeshFilter)component;
                    if (meshFilter.sharedMesh == null)
                    {
                        error = "角色表现 prefab 的 MeshFilter 没有网格（对象=\"" + PathOf(meshFilter.gameObject)
                                + "\"）：角色表现不可见（C1 的 mesh 引用没有落地？）。";
                        return false;
                    }

                    continue;
                }

                if (component is Renderer)
                {
                    // 这里**不**查骨骼/材质：`Material`、`Renderer.sharedMaterials`、
                    // `SkinnedMeshRenderer.sharedMesh/bones` 未在 Tools/PMUnityGlueCheck 的桩件里
                    // （那是另一组的文件，本文件不改它，也不为它增加必须同步维护的成员）。
                    // 角色"骨骼/材质/Renderer 仍可用"由 C1 的烘焙回读自检
                    // （VerifyPlayerPrefabLoaded 的 SkinnedMeshRenderer 网格/骨骼与"无材质渲染器"判定）
                    // 在**产出侧**把关。
                    continue;                                     // MeshRenderer / SkinnedMeshRenderer / ...
                }

                if (component is LODGroup) { continue; }

                Animator found = component as Animator;
                if (found != null)
                {
                    if (animator == null) { animator = found; }
                    animatorCount++;
                    continue;
                }

                Collider collider = component as Collider;
                if (collider != null)
                {
                    error = "角色表现 prefab 含 Collider（类型=" + collider.GetType().Name
                            + "，对象=\"" + PathOf(collider.gameObject)
                            + "\"）：角色的碰撞属于运动模型（PMR4MovementDriver + 胶囊查询），"
                            + "表现体必须 0 Collider。";
                    return false;
                }

                Rigidbody rigidbody = component as Rigidbody;
                if (rigidbody != null)
                {
                    error = "角色表现 prefab 含 Rigidbody（对象=\"" + PathOf(rigidbody.gameObject)
                            + "\"）：表现体不得参与物理，必须 0 Rigidbody。";
                    return false;
                }

                if (component is MonoBehaviour)
                {
                    error = "角色表现 prefab 含 MonoBehaviour（类型=" + component.GetType().FullName
                            + "，对象=\"" + PathOf(component.gameObject)
                            + "\"）：禁止旧 PlayerLogic/HYLDPlayerController/攻击脚本等一切旧脚本。";
                    return false;
                }

                if (component is Camera)
                {
                    error = "角色表现 prefab 含 Camera（对象=\"" + PathOf(component.gameObject)
                            + "\"）：相机由宿主显式创建（CreateTestCamera），不得烘焙进角色资源。";
                    return false;
                }

                if (component is AudioListener)
                {
                    error = "角色表现 prefab 含 AudioListener（对象=\"" + PathOf(component.gameObject)
                            + "\"）：C1 必须移除它。";
                    return false;
                }

                error = "角色表现 prefab 含未允许的组件类型 " + component.GetType().FullName
                        + "（对象=\"" + PathOf(component.gameObject)
                        + "\"）：允许集 = Transform/MeshFilter/Renderer/Animator/LODGroup；"
                        + "其余一律显式失败而不是静默残留。";
                return false;
            }

            return true;
        }

        /// <summary>Animator 参数表里是否确实有该名字的 Float 参数（不存在就绝不设）。</summary>
        private static bool HasFloatParameter(Animator animator, string parameterName)
        {
            if (animator == null || animator.runtimeAnimatorController == null)
            {
                return false;
            }

            AnimatorControllerParameter[] parameters;
            try
            {
                parameters = animator.parameters;
            }
            catch (Exception)
            {
                return false;
            }

            if (parameters == null)
            {
                return false;
            }

            for (int i = 0; i < parameters.Length; i++)
            {
                AnimatorControllerParameter parameter = parameters[i];
                if (parameter == null) { continue; }
                if (parameter.type != AnimatorControllerParameterType.Float) { continue; }
                if (string.Equals(parameter.name, parameterName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static void DestroyObject(UnityEngine.Object target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(target);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private static string PathOf(GameObject go)
        {
            if (go == null) { return "<null>"; }

            Transform current = go.transform;
            string path = go.name;

            while (current.parent != null)
            {
                current = current.parent;
                path = current.name + "/" + path;
            }

            return path;
        }

        private static string BuildRootName(string label, PMNetRole role)
        {
            return "PMUnityBattlePresentation[" + label + "|" + role + "]";
        }

        private static string BuildCameraName(string label, PMNetRole role)
        {
            return "PMBattleTestCamera[" + label + "|" + role + "]";
        }
    }
}
