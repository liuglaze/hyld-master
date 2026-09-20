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
        /// <summary>测试相机默认后撤距离（米）。</summary>
        public const float DefaultCameraDistanceMeters = 6f;

        /// <summary>Animator 里用于驱动移动表现的速度参数名（与旧链一致）。</summary>
        public const string SpeedParameterName = "Speed";

        /// <summary>Animator Speed 参数的取值范围上界（归一化到 0..1，与旧链的摇杆量级一致）。</summary>
        public const float MaxSpeedParameterValue = 1f;

        private readonly string _label;
        private readonly PMNetRole _role;
        private readonly PMBattleContentManifest _manifest;
        private readonly GameObject _root;
        private readonly Animator _animator;
        private readonly int _animatorCount;
        private readonly bool _animatorHasSpeedParameter;

        private Camera _testCamera;
        private bool _disposed;

        private int _applyCount;
        private PMMoverSyncState _lastApplied;
        private string _lastSource = "none";
        private float _lastSpeedParameterValue;

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
            _root.transform.rotation = Quaternion.Euler(0f, initial.YawDegrees, 0f);
            _root.transform.localScale = Vector3.one * initial.Scale;

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
        /// 角色根节点 Transform（供宿主绑定 UI / 相机）。已释放时为 null。
        /// 这是**唯一**被本类写入的 Transform。
        /// </summary>
        public Transform Root { get { return _disposed ? null : _root.transform; } }

        /// <summary>角色根节点 GameObject（诊断用）。已释放时为 null。</summary>
        public GameObject RootObject { get { return _disposed ? null : _root; } }

        /// <summary>表现用的 Animator；prefab 里没有则为 null（没有也允许，只是不驱动动画）。</summary>
        public Animator Animator { get { return _disposed ? null : _animator; } }

        /// <summary>prefab 里的 Animator 数量（&gt;1 时只使用第一个；数量本身作为资源异常信号暴露）。</summary>
        public int AnimatorCount { get { return _animatorCount; } }

        /// <summary>Animator 参数表里是否**确实**存在 Float 类型的 "Speed"（不存在就不设）。</summary>
        public bool AnimatorHasSpeedParameter { get { return _animatorHasSpeedParameter; } }

        /// <summary>测试相机；未创建或已释放时为 null。</summary>
        public Camera TestCamera { get { return _disposed ? null : _testCamera; } }

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
        /// 应用一个运动状态：**一次**写根节点的位置、绕 Y 朝向与整体缩放，并按需写 Animator Speed。
        ///
        /// 不做任何仿真/插值/物理查询/建删对象。非法输入（NaN/Inf、Scale ≤ 0）显式抛异常
        /// —— 绝不把坏值悄悄写进 Transform。
        /// </summary>
        public void Apply(PMMoverSyncState state)
        {
            RequireAlive();
            RequireFinite(state);

            _root.transform.position = new Vector3(state.Position.X, state.Position.Y, state.Position.Z);
            _root.transform.rotation = Quaternion.Euler(0f, state.YawDegrees, 0f);
            _root.transform.localScale = Vector3.one * state.Scale;

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
        /// 显式创建一个只属于本角色的测试相机（挂在角色根节点下、朝 -Z 略微俯视）。
        ///
        /// 因为它挂在根节点下，所以"跟随"是层级关系带来的，**不读**任何旧静态数据
        /// （旧链的 HYLDCameraManger 读 HYLDStaticValue.Players[...]；本类全程不接触它）。
        /// **不碰** Camera.main 与任何既有相机；重复调用只返回已存在的那一个。
        /// </summary>
        public Camera CreateTestCamera(float distanceMeters)
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

            GameObject cameraObject = new GameObject("TestCamera");
            cameraObject.transform.parent = _root.transform;
            cameraObject.transform.localPosition = new Vector3(0f, 1.5f, -distanceMeters);
            cameraObject.transform.localRotation = Quaternion.Euler(12f, 0f, 0f);

            Camera camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.08f, 0.09f, 0.11f, 1f);
            camera.fieldOfView = 60f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 500f;

            _testCamera = camera;
            return _testCamera;
        }

        // ---------------------------------------------------------------- 释放

        /// <summary>
        /// 幂等释放：销毁角色根节点（连带骨骼/网格/测试相机）并清空引用。重复调用是空操作。
        /// 释放后 <see cref="Root"/>/<see cref="Animator"/> 为 null，再 Apply 抛
        /// <see cref="ObjectDisposedException"/>（不静默写坏 Transform）。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _testCamera = null;

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
    }
}
