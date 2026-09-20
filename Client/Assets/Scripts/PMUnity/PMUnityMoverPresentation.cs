// ============================================================================
//  PMUnityMoverPresentation —— 每角色可见简单胶囊的表现层（契约 B2）
// ============================================================================
//
//  契约来源：Docs/plans/net-r4-network-contract.md 的 B2 一节：
//    · 「每角色可见简单胶囊；无碰撞（创建 primitive 自带 Collider 必须禁用），
//       DS 绝不创建该对象」；
//    · 「构造角色标识/Role；Apply(PMMoverSyncState state) 一次写 position/yaw/scale，
//       运动仿真不在里面」；
//    · 「Dispose 幂等；可选简单 Camera 测试显示，不改旧 camera、不强制自动创建，
//       最终宿主自行取舍」；
//    · 「AP 呈现预测输出，SP 呈现插值输出」。
//
//  三条硬边界（本文件的设计落点）：
//    1) **DS 绝不创建**：构造函数在 <see cref="PMNetRuntime.IsDedicatedServer"/> 为真时
//       直接抛异常（显式失败，而不是"悄悄不建"——悄悄不建会让宿主以为表现层在工作）。
//       判定走 PMNetRuntime，符合计划 §3.8「禁止用编辑器/场景名之类间接条件代替它」。
//    2) **无碰撞**：primitive 自带的 CapsuleCollider 立即 disable **并**销毁。
//       只 disable 不销毁会让"禁用碰撞体"依赖后续代码不改回来；只销毁不 disable 则会
//       留下"一帧内仍可被 PhysX 查询命中"的窗口（Destroy 是延迟到帧末的）。
//    3) **不改旧相机**：本类从不读写 Camera.main / 已有相机；测试相机只在显式调用
//       <see cref="CreateTestCamera"/> 时创建，且挂在本角色根节点下。
//
//  Apply 的写入面（契约"一次写"）：位置 + 绕 Y 朝向写在根节点，胶囊尺寸写在子节点
//  （Unity 的父级缩放会乘到子级，把尺寸写在叶子节点上语义最干净）。
//  除此之外 Apply **不做任何事**：不查询物理、不推进时间、不写业务状态、不建/删对象。
//
//  本文件不引用 PMMover 以外的业务类型（只有 PMNet.Mover 的 SyncState + PMNet 的 Role/Runtime）。
// ============================================================================

using System;
using PMNet.Mover;
using UnityEngine;

namespace PMNet.Unity
{
    /// <summary>
    /// 一个角色副本的可见表现。宿主按网络角色（AP/SP）决定把"预测输出"还是"插值输出"
    /// 喂给 <see cref="Apply"/>，本类不替宿主做这个选择。
    /// </summary>
    public sealed class PMUnityMoverPresentation : IDisposable
    {
        /// <summary>Unity 内置胶囊 primitive 的半径（世界单位、未缩放时）。尺寸换算用。</summary>
        public const float PrimitiveCapsuleRadius = 0.5f;

        /// <summary>Unity 内置胶囊 primitive 的半高（世界单位、未缩放时）。尺寸换算用。</summary>
        public const float PrimitiveCapsuleHalfHeight = 1f;

        private readonly string _label;
        private readonly PMNetRole _role;

        private GameObject _root;
        private Transform _body;
        private Camera _testCamera;
        private bool _disposed;

        private int _applyCount;
        private PMMoverSyncState _lastApplied;
        private string _lastSource = "none";

        /// <summary>
        /// 建出一个角色的可见胶囊。
        /// </summary>
        /// <param name="label">角色标识（进 GameObject 名字与诊断，便于在 Hierarchy 里对账）。</param>
        /// <param name="role">该副本的网络角色（Authority / AutonomousProxy / SimulatedProxy）。</param>
        public PMUnityMoverPresentation(string label, PMNetRole role)
            : this(label, role, false)
        {
        }

        /// <summary>
        /// 建出一个角色的可见胶囊，可选同时建一个测试用相机。
        /// </summary>
        /// <param name="createTestCamera">
        /// true 时在角色根节点下建一个测试相机（只用于"看得见在动"的验证）。
        /// 默认 false：宿主自行取舍，本类绝不自动创建。
        /// </param>
        public PMUnityMoverPresentation(string label, PMNetRole role, bool createTestCamera)
        {
            if (PMNetRuntime.IsDedicatedServer)
            {
                throw new InvalidOperationException(
                    "PMUnityMoverPresentation: 专用服务器上禁止创建表现对象（契约 B2：DS 绝不创建）。"
                    + "宿主必须先按 PMNetRuntime.IsDedicatedServer / PMNetRole 判定，DS 路径不要实例化本类。");
            }

            _label = label ?? string.Empty;
            _role = role;

            _root = new GameObject(BuildRootName(_label, _role));
            _root.transform.position = new Vector3(
                PMMoverSyncState.CreateDefault().Position.X,
                PMMoverSyncState.CreateDefault().Position.Y,
                PMMoverSyncState.CreateDefault().Position.Z);

            GameObject capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            capsule.name = "Body";
            capsule.transform.parent = _root.transform;
            capsule.transform.localPosition = Vector3.zero;
            capsule.transform.localRotation = Quaternion.identity;

            DisablePrimitiveCollider(capsule);

            _body = capsule.transform;
            _lastApplied = PMMoverSyncState.CreateDefault();
            ApplyCapsuleSize(_lastApplied.Scale);

            if (createTestCamera)
            {
                CreateTestCamera(DefaultCameraDistanceMeters);
            }
        }

        /// <summary>测试相机的默认后撤距离（米）。</summary>
        public const float DefaultCameraDistanceMeters = 6f;

        /// <summary>角色标识（构造时传入）。</summary>
        public string Label { get { return _label; } }

        /// <summary>该副本的网络角色。</summary>
        public PMNetRole Role { get { return _role; } }

        /// <summary>是否已释放。</summary>
        public bool Disposed { get { return _disposed; } }

        /// <summary>根节点；已释放时为 null。</summary>
        public GameObject Root { get { return _root; } }

        /// <summary>胶囊子节点；已释放时为 null。</summary>
        public Transform Body { get { return _body; } }

        /// <summary>测试相机；未创建或已释放时为 null。</summary>
        public Camera TestCamera { get { return _testCamera; } }

        /// <summary><see cref="Apply"/> 调用次数（诊断用）。</summary>
        public int ApplyCount { get { return _applyCount; } }

        /// <summary>最近一次应用的状态（诊断/断言用）。</summary>
        public PMMoverSyncState LastApplied { get { return _lastApplied; } }

        /// <summary>最近一次驱动来源："predicted" / "interpolated" / "none"。</summary>
        public string LastSource { get { return _lastSource; } }

        /// <summary>
        /// 应用一个运动状态：**一次**写位置、绕 Y 朝向与胶囊尺寸。不做任何仿真与查询。
        ///
        /// 非法输入显式失败（NaN/Inf、Scale ≤ 0）——绝不把坏值悄悄写进 Transform。
        /// </summary>
        public void Apply(PMMoverSyncState state)
        {
            RequireAlive();
            RequireFinite(state);

            _root.transform.position = new Vector3(state.Position.X, state.Position.Y, state.Position.Z);
            _root.transform.rotation = Quaternion.Euler(0f, state.YawDegrees, 0f);
            ApplyCapsuleSize(state.Scale);

            _lastApplied = state;
            _applyCount++;
        }

        /// <summary>AP（拥有者客户端）路径：喂**预测输出**。写入面与 <see cref="Apply"/> 完全相同。</summary>
        public void ApplyPredicted(PMMoverSyncState state)
        {
            _lastSource = "predicted";
            Apply(state);
        }

        /// <summary>SP（模拟端）路径：喂**插值输出**。写入面与 <see cref="Apply"/> 完全相同。</summary>
        public void ApplyInterpolated(PMMoverSyncState state)
        {
            _lastSource = "interpolated";
            Apply(state);
        }

        /// <summary>
        /// 显式创建一个只属于本角色的测试相机（挂在根节点下、朝 -Z 略微俯视）。
        ///
        /// **不碰**旧相机与 Camera.main；重复调用只返回已存在的那一个。
        /// 这是"可选"能力：宿主不调它就不会有相机。
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

        /// <summary>幂等释放：销毁根节点（连带子节点与相机）并清空引用。重复调用是空操作。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _testCamera = null;
            _body = null;

            GameObject root = _root;
            _root = null;

            if (root != null)
            {
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(root);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }
            }
        }

        // ---------------------------------------------------------------- 内部

        private void ApplyCapsuleSize(float scale)
        {
            if (_body == null)
            {
                return;
            }

            float radiusWorld = PMMoverDefaults.CapsuleRadiusMeters * scale;
            float halfHeightWorld = PMMoverDefaults.CapsuleHalfHeightMeters * scale;

            _body.localScale = new Vector3(
                radiusWorld / PrimitiveCapsuleRadius,
                halfHeightWorld / PrimitiveCapsuleHalfHeight,
                radiusWorld / PrimitiveCapsuleRadius);
        }

        private static void DisablePrimitiveCollider(GameObject go)
        {
            Collider collider = go.GetComponent<Collider>();
            if (collider == null)
            {
                return;
            }

            // 先 disable（立刻退出 PhysX 查询面），再销毁（清理 Hierarchy/序列化面）。
            collider.enabled = false;

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(collider);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(collider);
            }
        }

        private void RequireAlive()
        {
            if (_disposed || _root == null)
            {
                throw new ObjectDisposedException(
                    "PMUnityMoverPresentation(" + _label + ")",
                    "表现对象已释放：释放后不得再 Apply（宿主必须在断线/离场时先停止驱动再 Dispose）。");
            }
        }

        private static void RequireFinite(PMMoverSyncState state)
        {
            if (!state.Position.IsFinite || !state.Velocity.IsFinite)
            {
                throw new ArgumentException(
                    "PMUnityMoverPresentation.Apply: 状态位置/速度含非有限值，拒绝写入 Transform。");
            }

            if (float.IsNaN(state.YawDegrees) || float.IsInfinity(state.YawDegrees))
            {
                throw new ArgumentException(
                    "PMUnityMoverPresentation.Apply: YawDegrees 非有限值，拒绝写入 Transform。");
            }

            if (float.IsNaN(state.Scale) || float.IsInfinity(state.Scale) || state.Scale <= 0f)
            {
                throw new ArgumentException(
                    "PMUnityMoverPresentation.Apply: Scale 必须是有限正值，实际 " + state.Scale.ToString("R"));
            }
        }

        private static string BuildRootName(string label, PMNetRole role)
        {
            return "PMUnityMoverPresentation[" + label + "|" + role + "]";
        }
    }
}
