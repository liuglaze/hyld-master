// ============================================================================
//  PMUnityProjectilePresentation —— R5-C2 独立适配：投射物的薄表现层（诊断占位）
// ============================================================================
//
//  契约来源（只读）：
//    · Docs/plans/net-r5-network-contract.md 末段「B2b/C适配可并行边界」的 C2 一节；
//    · Docs/plans/net-r5-projectile-contract.md（单位：Y-up 米、Yaw 度）。
//
//  C2 逐条落点：
//    · 「非 MonoBehaviour 可 Dispose 薄表现」 → 普通 sealed class + IDisposable，
//       不是组件、不挂到任何角色上，宿主按需 new/Dispose。
//    · 「构造或工厂创建不带 Collider/Rigidbody/旧脚本的简单球形可视占位
//       （不盗用原 shell 玩法 Prefab）」 → 只用 Unity 内建 primitive（Sphere）现建一个占位；
//       primitive 自带的 Collider **先禁用再销毁**，Rigidbody 若存在同样先运动学再销毁；
//       全程不引用、不实例化任何旧子弹 Prefab/脚本/材质资产。
//       **构造中途失败**（例如 primitive 创建失败 / 内建材质克隆失败）时，
//       构造函数的 catch 会把已自建的根节点/子节点/已克隆材质全部销毁后再把异常抛出 ——
//       构造抛异常时调用方拿不到实例，不给清理入口就只能永久泄漏。
//    · 「Apply(PMProjectileState, PMProjectileSpec) 只更新位置/yaw/尺寸/hidden」 → 见 Apply
//       的写入面（位置 + 绕 Y 朝向 + 球半径 + 可见性），此外**什么都不做**：
//       不查询物理、不推进时间、不读全局时钟、不写业务状态、不建/删对象、不发声。
//    · 「Stopped 不二次触发副作用」 → 停止只是 hidden 计算的一个输入；重复 Apply 同一个
//      已停止状态与第一次写入**逐字段相同**（幂等），不产生一次性事件/音效/回调。
//       本类**没有任何**音效/事件出口（"二次音效"在本实现里不可表达）。
//    · 「DS 禁止创建」 → 构造函数在 <see cref="PMNetRuntime.IsDedicatedServer"/> 为真时直接抛异常
//       （显式失败，而不是"悄悄不建"——悄悄不建会让宿主以为表现层在工作）。
//       判定走 PMNetRuntime；**不**用 Application.isEditor / 场景名之类的间接条件代替它。
//       Application.isPlaying 只用于选 Destroy 还是 DestroyImmediate（生命周期 API，不用于身份判定）。
//    · 「运行时对象归一个自建根，Dispose 清理；材料不反复实例化，不能动原资产」 →
//       一个自建根节点 + 一个 Body 子节点；材质在构造期**只克隆一次**并复用
//       （绝不每帧 new），Dispose 时连同自建根一起销毁；原资产（primitive 内建材质、
//       任何 Prefab/材质资源）只被**读**，从不被写。
//       注意"延迟销毁 + 已禁用碰撞体"：运行时 `Object.Destroy` 是**帧末**生效的，
//       因此占位球自带的 Collider/Rigidbody 必须在构造期就被**禁用**（再销毁），
//       Dispose 前也会幂等再确保一次 —— 否则在那一帧窗口里它仍会被 PhysX 查询命中。
//    · 「当前只是诊断表现，不称全部英雄子弹外观已移植」 → 根节点名字里带
//       `diagnostic-sphere-not-hero-art`，并且本文件不引用任何英雄外观配置。
//
//  四条硬边界（本文件的设计落点）：
//    1) **不碰别人的对象**：本类只拥有自己 new 出来的根节点/子节点/材质。
//       从不读写真·摄像机（Camera.main）、已有角色、地图、UI，也不尝试接管任何已有 Renderer。
//    2) **写入面最小**：Apply 只写 4 样东西；"位置"的唯一来源是 `state.Position`
//       （不读 SpawnPosition/PreviousPosition/Velocity 去猜）。
//    3) **fail loud**：非法输入（null、NaN/Inf 位置/朝向/半径、非正半径）显式抛异常，
//       绝不把坏值悄悄写进 Transform —— 表现层的静默坏值是最难归因的一类问题。
//       校验**先于任何写入**，因此失败不会半写（不产生"既非旧状态也非新状态"的幽灵位置）。
//    4) **构造要么完整要么不留痕**：构造中途抛异常时先销毁已自建的对象/材质再抛，
//       调用方永远不需要（也无法）为一个构造失败的实例调 Dispose。
//
//  明确不做（诚实边界）：
//    · 不做插值/平滑/拖尾/音效/命中特效：这些属后续 C 宿主表现批次，本批只交付
//      "能被宿主接线的、可验证的占位"。**不声称**英雄子弹外观已迁移完成。
//    · 不做任何物理/伤害判定（那是整合器 + R6 的事）。
// ============================================================================

using System;
using PMNet.Mover;
using PMNet.Projectile;
using UnityEngine;

namespace PMNet.Unity
{
    /// <summary>
    /// 单颗投射物的诊断用可视化占位。宿主按网络状态（权威快照或预测）决定把哪个
    /// <see cref="PMProjectileState"/> 喂给 <see cref="Apply"/>，本类不替宿主做这个选择。
    /// </summary>
    public sealed class PMUnityProjectilePresentation : IDisposable
    {
        /// <summary>Unity 内建 Sphere primitive 的半径（世界单位、未缩放时）。尺寸换算用。</summary>
        public const float PrimitiveSphereRadius = 0.5f;

        /// <summary>
        /// 占位种类标记（进 GameObject 名字）。**明确写清这不是最终英雄子弹外观**：
        /// 本批只交付诊断占位，外观迁移属后续批次。
        /// </summary>
        public const string PlaceholderKind = "diagnostic-sphere-not-hero-art";

        /// <summary>占位球颜色（诊断用；能一眼与真实子弹美术区分开）。</summary>
        public static readonly Color PlaceholderColor = new Color(0.35f, 0.85f, 1f, 1f);

        private readonly string _label;

        private GameObject _root;
        private Transform _body;
        private Material _material;
        private bool _disposed;

        private int _applyCount;
        private bool _hidden;
        private PMVector3 _lastPosition;
        private float _lastYaw;
        private float _lastRadius;

        /// <summary>
        /// 用给定标识建一个投射物占位（根节点挂在场景根下）。
        /// </summary>
        /// <param name="label">投射物标识（进 GameObject 名字与诊断，便于在 Hierarchy 里对账）。</param>
        public PMUnityProjectilePresentation(string label)
            : this(label, null)
        {
        }

        /// <summary>
        /// 用给定标识建一个投射物占位，可选挂到宿主给的根节点下。
        ///
        /// 注意：本类会写自己根节点的世界位置/朝向，因此 <paramref name="attachRoot"/>
        /// 应当是**单位缩放**的纯层级挂点（不要挂在会被缩放的骨点上，否则球半径会被父级缩放放大）。
        /// </summary>
        /// <param name="label">投射物标识（进 GameObject 名字与诊断）。</param>
        /// <param name="attachRoot">可选父节点；null = 挂在场景根下。本类不修改该节点。</param>
        public PMUnityProjectilePresentation(string label, Transform attachRoot)
        {
            if (PMNetRuntime.IsDedicatedServer)
            {
                throw new InvalidOperationException(
                    "PMUnityProjectilePresentation: 专用服务器上禁止创建表现对象（契约 C2：DS 禁止创建）。"
                    + "宿主必须先按 PMNetRuntime.IsDedicatedServer 判定，DS 路径不要实例化本类。");
            }

            _label = label == null ? string.Empty : label;
            _lastPosition = PMVector3.Zero;
            _lastRadius = 0f;

            // 自建对象/材质的局部持杆：构造中途失败时用它做清理（见 catch）。
            GameObject sphere = null;

            try
            {
                _root = new GameObject(BuildRootName(_label));
                if (attachRoot != null)
                {
                    _root.transform.parent = attachRoot;
                }

                sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = "Body";
                sphere.transform.parent = _root.transform;
                sphere.transform.localPosition = Vector3.zero;
                sphere.transform.localRotation = Quaternion.identity;

                StripPhysicsComponents(sphere);

                _body = sphere.transform;

                CreatePlaceholderMaterial(sphere);
            }
            catch
            {
                // 构造失败：**清掉本类已自建的对象与材质**，然后把异常原样抛出。
                // 为什么必须做：构造抛异常时调用方拿不到 this，没有任何入口能再调 Dispose，
                // 半成品（根节点 / primitive 子节点 / 已克隆材质）会永久泄在场景里。
                // 顺序：先把字段清空再销毁，避免销毁回调/异常路径又走到已失效的引用上。
                GameObject root = _root;
                Material material = _material;
                _root = null;
                _body = null;
                _material = null;
                _disposed = true;

                DestroyObject(sphere);
                DestroyObject(root);
                DestroyObject(material);

                throw;
            }
        }

        /// <summary>工厂：与构造函数同义（保持一个显式的创建入口，便于宿主日后替换实现）。</summary>
        public static PMUnityProjectilePresentation Create(string label)
        {
            return new PMUnityProjectilePresentation(label);
        }

        /// <summary>工厂：带父节点的版本。</summary>
        public static PMUnityProjectilePresentation Create(string label, Transform attachRoot)
        {
            return new PMUnityProjectilePresentation(label, attachRoot);
        }

        // ---------------------------------------------------------------- 只读视图

        /// <summary>投射物标识（构造时传入）。</summary>
        public string Label { get { return _label; } }

        /// <summary>是否已释放。</summary>
        public bool Disposed { get { return _disposed; } }

        /// <summary>自建根节点；已释放时为 null。</summary>
        public GameObject Root { get { return _root; } }

        /// <summary>占位球子节点；已释放时为 null。</summary>
        public Transform Body { get { return _body; } }

        /// <summary>占位材质（构造期克隆一次；未克隆成功时为 null）。已释放时为 null。</summary>
        public Material Material { get { return _material; } }

        /// <summary><see cref="Apply"/> 调用次数（诊断用）。</summary>
        public int ApplyCount { get { return _applyCount; } }

        /// <summary>最近一次 Apply 之后本对象是否处于隐藏状态。</summary>
        public bool Hidden { get { return _hidden; } }

        /// <summary>最近一次 Apply 写入的位置（诊断/断言用）。</summary>
        public PMVector3 LastPosition { get { return _lastPosition; } }

        /// <summary>最近一次 Apply 写入的绕 Y 朝向（度）。</summary>
        public float LastYawDegrees { get { return _lastYaw; } }

        /// <summary>最近一次 Apply 写入的投射物半径（米）。</summary>
        public float LastRadiusM { get { return _lastRadius; } }

        // ---------------------------------------------------------------- Apply

        /// <summary>
        /// 应用一个投射物状态 + 权威配置：**一次**写位置、绕 Y 朝向、球半径与可见性。
        ///
        /// **全部校验都发生在任何写入之前**（RequireAlive → null 检查 → RequireFinite），
        /// 因此非法输入（null、NaN/Inf、非正半径）**不会半写**：位置/朝向/尺寸/可见性
        /// 逐字段保持上一次成功 Apply 的结果（"半写"会让宿主看到一个既非旧状态也非新状态的
        /// 幽灵位置，比直接抛异常难归因得多）。
        ///
        /// 写入面之外什么都不做；重复调用是幂等的（包括 `Stopped == true` 的重复调用：
        /// 不产生任何一次性副作用，本类也没有音效/事件出口）。
        /// </summary>
        public void Apply(PMProjectileState state, PMProjectileSpec spec)
        {
            RequireAlive();

            if (state == null)
            {
                throw new ArgumentNullException("state",
                    "PMUnityProjectilePresentation.Apply: state 为 null。");
            }

            if (spec == null)
            {
                throw new ArgumentNullException("spec",
                    "PMUnityProjectilePresentation.Apply: spec 为 null（半径来自权威 spec，不能缺省）。");
            }

            RequireFinite(state, spec);

            // 1) 位置：唯一来源 = state.Position（不读 SpawnPosition/PreviousPosition/Velocity 去猜）。
            _root.transform.position = new Vector3(state.Position.X, state.Position.Y, state.Position.Z);

            // 2) 绕 Y 朝向。
            _root.transform.rotation = Quaternion.Euler(0f, state.Yaw, 0f);

            // 3) 尺寸（球半径换算成 primitive 的局部缩放）。
            ApplySphereSize(spec.RadiusM);

            // 4) 可见性：显式 Hidden 或（已停止且 spec 要求停止即隐藏）。
            bool hidden = state.Hidden || (state.Stopped && spec.HideOnStop);
            ApplyHidden(hidden);

            _applyCount++;
            _lastPosition = state.Position;
            _lastYaw = state.Yaw;
            _lastRadius = spec.RadiusM;
        }

        /// <summary>
        /// 幂等释放：销毁自建材质与自建根节点（连带子节点与所有自建组件），清空引用。
        /// 重复调用是空操作。**不触碰**任何非自建对象（摄像机、角色、地图、资产）。
        ///
        /// 关于"延迟销毁 + 被禁用的碰撞体"：本类先确保占位球的 Collider/Rigidbody **已禁用**
        /// 再进入销毁（构造期已做，这里再幂等确保一次），因为 `Object.Destroy` 在运行时是
        /// **帧末**生效的——若那一个窗口里还存在可查询的 COLLIDER，它就会在该帧被 PhysX 命中。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            GameObject root = _root;
            Transform body = _body;
            Material material = _material;
            _root = null;
            _body = null;
            _material = null;

            // 破坏性删除前再幂等确保一次"占位不参与物理"（防止外部代码把它启回来）。
            if (body != null && body.gameObject != null)
            {
                StripPhysicsComponents(body.gameObject);
            }

            DestroyObject(material);
            DestroyObject(root);
        }

        // ---------------------------------------------------------------- 内部

        /// <summary>
        /// 摘掉 primitive 自带的物理组件（契约：占位物**不带** Collider / Rigidbody）。
        ///
        /// 为什么"先禁用、再销毁"两步都要：
        ///   · 只销毁不先禁用 —— Unity 的 Destroy 延迟到帧末，中间会留下"仍可被 PhysX 查询命中"的窗口；
        ///   · 只禁用不销毁 —— 组件仍留在 Hierarchy/序列化面上，后续代码可能又把它启回来。
        /// Rigidbody 同理：非运动学刚体会在那一帧开始自由落体（表现层凭空多出一个物理体）。
        /// </summary>
        private static void StripPhysicsComponents(GameObject sphere)
        {
            Collider collider = sphere.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
                DestroyObject(collider);
            }

            Rigidbody rigidbody = sphere.GetComponent<Rigidbody>();
            if (rigidbody != null)
            {
                rigidbody.isKinematic = true;
                DestroyObject(rigidbody);
            }
        }

        /// <summary>
        /// 构造期**只克隆一次**占位材质（契约：材料不反复实例化，不能动原资产）。
        ///
        /// 来源是 primitive 自带的内建共享材质（保证非空、且与任何英雄资产无关），
        /// 用 `new Material(source)` 克隆后改色，再把克隆体赋给自己的 renderer；
        /// `source` 只被**读**，从不被写。取不到源材质时保持 primitive 原样（仍不产生每帧分配）。
        /// </summary>
        private void CreatePlaceholderMaterial(GameObject sphere)
        {
            Renderer renderer = sphere.GetComponent<Renderer>();
            if (renderer == null)
            {
                return;
            }

            Material source = renderer.sharedMaterial;
            if (source == null)
            {
                // 没有源材质（例如内建资源被剥离）：不创建材质，也不每帧补救。
                return;
            }

            Material material = new Material(source);

            // 先记账再挂上去：若 renderer 赋值失败抛出，构造期的 catch 也能把这个已创建的
            // 克隆材质销掉（否则就是"半成品泄漏"）。
            _material = material;

            material.color = PlaceholderColor;
            renderer.sharedMaterial = material;
        }

        private void ApplySphereSize(float radiusMeters)
        {
            if (_body == null)
            {
                return;
            }

            float scale = radiusMeters / PrimitiveSphereRadius;
            _body.localScale = new Vector3(scale, scale, scale);
        }

        private void ApplyHidden(bool hidden)
        {
            if (_root == null)
            {
                return;
            }

            _hidden = hidden;

            // 只在状态真的变化时才写引擎（避免每帧无意义的 SetActive 广播）。
            bool currentlyHidden = !_root.activeSelf;
            if (currentlyHidden != hidden)
            {
                _root.SetActive(!hidden);
            }
        }

        private void RequireAlive()
        {
            if (_disposed || _root == null)
            {
                throw new ObjectDisposedException(
                    "PMUnityProjectilePresentation(" + _label + ")",
                    "表现对象已释放：释放后不得再 Apply（宿主必须先停止驱动再 Dispose）。");
            }
        }

        private static void RequireFinite(PMProjectileState state, PMProjectileSpec spec)
        {
            if (!state.Position.IsFinite)
            {
                throw new ArgumentException(
                    "PMUnityProjectilePresentation.Apply: Position 含非有限值，拒绝写入 Transform。");
            }

            if (float.IsNaN(state.Yaw) || float.IsInfinity(state.Yaw))
            {
                throw new ArgumentException(
                    "PMUnityProjectilePresentation.Apply: Yaw 非有限值，拒绝写入 Transform。");
            }

            float radius = spec.RadiusM;
            if (float.IsNaN(radius) || float.IsInfinity(radius) || radius <= 0f)
            {
                throw new ArgumentException(
                    "PMUnityProjectilePresentation.Apply: RadiusM 必须是有限正值，实际 "
                    + radius.ToString("R"));
            }
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

        private static string BuildRootName(string label)
        {
            return "PMUnityProjectilePresentation[" + label + "|" + PlaceholderKind + "]";
        }
    }
}
