// ============================================================================
//  PMR5UnityAdapterTest / UnityStubs.cs —— 受控 UnityEngine 替身（**不是** Unity，也不是 PhysX）
// ============================================================================
//
//  边界声明（本文件最重要的一段，禁止被读成"假验物理"）：
//    · 这里是用**我自己实现的语义模型**代替 UnityEngine：它只复现 R5-C1/C2 的适配器
//      真正用到的成员（PhysicsScene 查询族、GameObject/Transform/Component/Renderer/Material、
//      Object.Destroy、Application.isPlaying、Physics.defaultPhysicsScene、
//      PhysicsSceneExtensions.GetPhysicsScene 等）。
//    · 几何判定是"**球 vs 轴对齐 AABB**"的一阶解析模型。
//    · 关于 R4-B 的那次实测（Docs/plans/_r4b_physx_zero_hit_fix.md）：真实 Play 里观察到
//      "起点已接触/重叠时，扫掠返回的那一条命中 `distance == 0` 且其 `normal` 为 `-dir`"。
//      那是**一次观测**，**不是** PhysX 的接口契约：不得表述为"所有接触法线恒为 `-dir`"，
//      也不得据此推断任何命中的法线都不可信。被验证的适配器**完全不读 `RaycastHit.normal`**
//      （它只按距离取最早阻挡），因此本替身写不写 normal 都不影响任何断言。
//    · 它**不能**证明真实 PhysX 上的行为：真机结论仍是 PENDING_USER。
//      真实物理的最终验证必须在 Unity 内跑（本批不启动 Unity、不点菜单）。
//    · 因此本工程断言的是"适配器的判定逻辑"，而不是"引擎的几何结论"。
//
//  与 Tools/PMR4CollisionAdapterTest/Program.cs 的关系：
//    两者是同一套做法（受控替身 + 真实适配器源码），区别只是被验证对象从"胶囊 vs 盒"的
//    角色查询换成"球 vs 盒"的**投射物**查询，并补上表现层的对象生命周期替身。
//    这里刻意**不**引入任何独立物理引擎/积分器：只做"点到 AABB 的距离 + 一阶 TOI"。
//
//  替身的两处**有意简化**（已在用例里标注）：
//    · Quaternion 只保存欧拉角（不做四元数乘法）；用例只比较 `Quaternion.Euler` 的往返值。
//    · Transform.position/rotation 只做"父级位置平移"的父子合成（没有旋转/缩放合成）。
//      表现层只写自己的根节点、只读子节点的 localScale，因此该简化不影响断言。
// ============================================================================

using System;
using System.Collections.Generic;

namespace UnityEngine
{
    /// <summary>与 Unity 数值一致的 Y-up 三维向量（只实现适配器/表现层/用例用到的成员）。</summary>
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
        public static Vector3 one { get { return new Vector3(1f, 1f, 1f); } }
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

        public static Vector3 operator -(Vector3 a)
        {
            return new Vector3(-a.x, -a.y, -a.z);
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

    /// <summary>RGBA 颜色（表现层占位色用）。</summary>
    public struct Color
    {
        public float r;
        public float g;
        public float b;
        public float a;

        public Color(float r, float g, float b, float a)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }

        public static Color white { get { return new Color(1f, 1f, 1f, 1f); } }

        public override string ToString()
        {
            return "RGBA(" + r.ToString("R") + ", " + g.ToString("R") + ", " + b.ToString("R")
                   + ", " + a.ToString("R") + ")";
        }
    }

    /// <summary>
    /// 四元数替身：**只保存欧拉角**（有意简化）。表现层只写 `Quaternion.Euler(0, yaw, 0)`，
    /// 用例只比较往返值，因此不需要真正的四元数代数。
    /// </summary>
    public struct Quaternion
    {
        public float x;
        public float y;
        public float z;
        public float w;

        private Quaternion(float x, float y, float z, float w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public static Quaternion identity { get { return new Quaternion(0f, 0f, 0f, 1f); } }

        /// <summary>替身语义：把 (x,y,z) 当作欧拉角原样保存（不做乘法）。</summary>
        public static Quaternion Euler(float x, float y, float z)
        {
            return new Quaternion(x, y, z, 1f);
        }

        public Vector3 eulerAngles { get { return new Vector3(x, y, z); } }

        public override string ToString()
        {
            return "Euler(" + x.ToString("R") + ", " + y.ToString("R") + ", " + z.ToString("R") + ")";
        }
    }

    /// <summary>
    /// <see cref="UnityEngine.Object"/> 替身：实现 Destroy / DestroyImmediate 的**级联销毁**与计数。
    ///
    /// 与真实 Unity 的差异（有意）：被销毁对象仍然是一个非 null 的托管引用，
    /// 因此本替身不支持"已销毁对象 == null"的 Unity 语义；适配器与表现层都不依赖该语义。
    /// </summary>
    public class Object
    {
        public string name;
        public bool Destroyed;

        /// <summary>Destroy 系列被调用的总次数（含级联）。</summary>
        public static int DestroyCalls;

        public static void Destroy(Object target)
        {
            DestroyCore(target);
        }

        public static void DestroyImmediate(Object target)
        {
            DestroyCore(target);
        }

        private static void DestroyCore(Object target)
        {
            if (target == null || target.Destroyed)
            {
                return;
            }

            DestroyCalls++;

            GameObject go = target as GameObject;
            if (go != null)
            {
                go.DestroyCascade();
                return;
            }

            Component component = target as Component;
            if (component != null)
            {
                component.Destroyed = true;
                if (component.gameObject != null)
                {
                    component.gameObject.Components.Remove(component);
                }

                return;
            }

            target.Destroyed = true;
        }
    }

    /// <summary>组件替身：每个组件知道自己挂在哪个 GameObject 上。</summary>
    public class Component : Object
    {
        public GameObject gameObject;

        /// <summary>便捷属性（真实 Unity 同义）。</summary>
        public Transform transform { get { return gameObject == null ? null : gameObject.transform; } }

        /// <summary>便捷查询（真实 Unity 的 Component.GetComponent<T>() 同义）。</summary>
        public T GetComponent<T>() where T : Component
        {
            return gameObject == null ? null : gameObject.GetComponent<T>();
        }
    }

    /// <summary>行为替身（只保留 enabled）。</summary>
    public class Behaviour : Component
    {
        public bool enabled = true;
    }

    /// <summary>MonoBehaviour 替身：只用于断言"没有旧脚本挂在自建对象上"。</summary>
    public class MonoBehaviour : Behaviour
    {
    }

    /// <summary>
    /// Transform 替身：位置/朝向/缩放 + 父子关系。
    /// **有意简化**：世界位置只做父级平移合成（无旋转/缩放合成），够表现层用例使用。
    /// </summary>
    public class Transform : Component
    {
        private Transform _parent;

        public readonly List<Transform> Children = new List<Transform>();

        public Vector3 localPosition;
        public Quaternion localRotation = Quaternion.identity;
        public Vector3 localScale = Vector3.one;

        public Transform parent
        {
            get { return _parent; }
            set { SetParent(value); }
        }

        public void SetParent(Transform newParent)
        {
            if (ReferenceEquals(_parent, newParent))
            {
                return;
            }

            if (_parent != null)
            {
                _parent.Children.Remove(this);
            }

            _parent = newParent;

            if (_parent != null)
            {
                _parent.Children.Add(this);
            }
        }

        public Vector3 position
        {
            get { return _parent == null ? localPosition : _parent.position + localPosition; }
            set { localPosition = _parent == null ? value : value - _parent.position; }
        }

        public Quaternion rotation
        {
            get { return localRotation; }
            set { localRotation = value; }
        }
    }

    /// <summary>
    /// GameObject 替身：组件表 + 层级 + 场景归属 + 激活状态。
    /// 静态计数用于断言"每帧没有新建对象"以及"Dispose 之后没有残留"。
    /// </summary>
    public class GameObject : Object
    {
        public readonly List<Component> Components = new List<Component>();

        public SceneManagement.Scene scene;
        public int layer;
        public bool activeSelf = true;

        public static int CreatedTotal;
        public static int DestroyedTotal;

        /// <summary>当前存活（已创建且未被销毁）的 GameObject 数。</summary>
        public static int AliveCount { get { return CreatedTotal - DestroyedTotal; } }

        public GameObject()
            : this("GameObject")
        {
        }

        public GameObject(string name)
        {
            this.name = name;
            CreatedTotal++;
            transform = AddComponent<Transform>();
        }

        public Transform transform { get; private set; }

        /// <summary>真实 Unity 同义：自身与所有祖先都处于激活状态。</summary>
        public bool activeInHierarchy
        {
            get
            {
                if (Destroyed || !activeSelf) { return false; }

                Transform cursor = transform == null ? null : transform.parent;
                while (cursor != null)
                {
                    if (cursor.gameObject != null && !cursor.gameObject.activeSelf) { return false; }
                    cursor = cursor.parent;
                }

                return true;
            }
        }

        public T AddComponent<T>() where T : Component, new()
        {
            T component = new T();
            component.gameObject = this;
            Components.Add(component);
            return component;
        }

        public T GetComponent<T>() where T : Component
        {
            for (int i = 0; i < Components.Count; i++)
            {
                T typed = Components[i] as T;
                if (typed != null)
                {
                    return typed;
                }
            }

            return null;
        }

        public void SetActive(bool value)
        {
            activeSelf = value;
        }

        /// <summary>
        /// 测试钩子（只影响替身）：置 true 时下一次 <see cref="CreatePrimitive"/> 抛异常（随后自动复位）。
        /// 用来验证"primitive 创建失败"时表现层构造函数把已建的根节点清掉。
        /// </summary>
        public static bool FailNextCreatePrimitive;

        /// <summary>替身语义：primitive 自带一个 Collider（Sphere/Capsule/Cube…）与一个内建材质，没有 Rigidbody。</summary>
        public static GameObject CreatePrimitive(PrimitiveType type)
        {
            if (FailNextCreatePrimitive)
            {
                FailNextCreatePrimitive = false;
                throw new InvalidOperationException(
                    "GameObject 替身：FailNextCreatePrimitive 已置位（测试钩子：模拟 primitive 创建失败）。");
            }

            GameObject go = new GameObject(type.ToString());
            go.AddComponent<MeshFilter>();
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = Material.BuiltinDefault;

            Collider collider;
            switch (type)
            {
                case PrimitiveType.Sphere:
                    collider = go.AddComponent<SphereCollider>();
                    break;

                case PrimitiveType.Capsule:
                    collider = go.AddComponent<CapsuleCollider>();
                    break;

                default:
                    collider = go.AddComponent<BoxCollider>();
                    break;
            }

            collider.name = type + " Collider";
            return go;
        }

        internal void DestroyCascade()
        {
            if (Destroyed) { return; }

            // 子节点先级联（真实 Unity 的 Destroy 同样会带走整个子树）。
            for (int i = transform.Children.Count - 1; i >= 0; i--)
            {
                GameObject child = transform.Children[i].gameObject;
                if (child != null)
                {
                    child.DestroyCascade();
                }
            }

            Destroyed = true;
            DestroyedTotal++;

            for (int i = 0; i < Components.Count; i++)
            {
                Components[i].Destroyed = true;
            }

            Components.Clear();
        }
    }

    /// <summary>Collider 替身：世界 AABB + enabled/isTrigger/layer。查询只看这些字段。</summary>
    public class Collider : Component
    {
        public bool enabled = true;
        public bool isTrigger;
        public int layer;

        public Vector3 AabbMin;
        public Vector3 AabbMax;

        public string Describe()
        {
            return (name == null ? "<unnamed>" : name) + " AABB[" + AabbMin + " .. " + AabbMax + "]";
        }
    }

    public class SphereCollider : Collider
    {
    }

    public class BoxCollider : Collider
    {
    }

    public class CapsuleCollider : Collider
    {
    }

    public class MeshCollider : Collider
    {
    }

    /// <summary>Rigidbody 替身：只保留 isKinematic（用于断言"自建对象上没有动态刚体"）。</summary>
    public class Rigidbody : Component
    {
        public bool isKinematic;
    }

    /// <summary>Renderer 替身：只保留 sharedMaterial / enabled。</summary>
    public class Renderer : Component
    {
        /// <summary>
        /// 测试钩子（只影响替身）：置 true 时，给 sharedMaterial 赋一个**克隆材质**
        /// （<see cref="Material.IsClone"/>）会抛异常。用于验证"表现层构造中途失败"时
        /// 已创建的克隆材质也被清理（primitive 内建材质的赋值不受影响）。
        /// </summary>
        public static bool ThrowOnSharedMaterialSet;

        private Material _sharedMaterial;

        public bool enabled = true;

        public Material sharedMaterial
        {
            get { return _sharedMaterial; }
            set
            {
                if (ThrowOnSharedMaterialSet && value != null && value.IsClone)
                {
                    throw new InvalidOperationException(
                        "Renderer 替身：ThrowOnSharedMaterialSet 已置位（测试钩子：模拟挂克隆材质失败）。");
                }

                _sharedMaterial = value;
            }
        }

        /// <summary>替身语义：material 与 sharedMaterial 是同一个引用（不做实例化语义）。</summary>
        public Material material
        {
            get { return _sharedMaterial; }
            set { sharedMaterial = value; }
        }
    }

    public class MeshRenderer : Renderer
    {
    }

    public class MeshFilter : Component
    {
    }

    /// <summary>
    /// Material 替身：创建计数用于断言"材料只克隆一次，不每帧 new"。
    /// 内建共享材质（给 primitive 的 renderer 当默认材质）**不计入** CreatedTotal，
    /// 否则用例无法区分"引擎内建"与"代码里 new 的"。
    /// </summary>
    public class Material : Object
    {
        public static int CreatedTotal;

        /// <summary>
        /// 测试钩子（**只影响替身，不涉及任何真实引擎语义**）：置 true 时 <c>Material(Material)</c>
        /// 在**创建任何东西之前**就抛异常 —— 模拟"原生克隆构造失败"，用来验证表现层构造
        /// 失败时不留痕（既不计数、也不留实例）。
        /// </summary>
        public static bool ThrowOnClone;

        /// <summary>最近一个**被计数**的材质实例（内建默认材质不计入）。供用例断言"已创建的克隆被销毁"。</summary>
        public static Material LastCreated;

        /// <summary>本实例是否由 <c>Material(Material)</c> 克隆而来（供 Renderer 钩子区分内建与克隆）。</summary>
        internal bool IsClone;

        /// <summary>内建默认材质（只创建一次；不计入 CreatedTotal，也不是 LastCreated）。</summary>
        internal static readonly Material BuiltinDefault = new Material(false);

        public Color color = Color.white;

        public Material()
            : this(true)
        {
        }

        public Material(Material source)
            // 钩子置位时走"不计数"的构造：先抛，再什么都不留（与原生构造失败同形）。
            : this(!ThrowOnClone)
        {
            if (ThrowOnClone)
            {
                throw new InvalidOperationException(
                    "Material 替身：ThrowOnClone 已置位（测试钩子：模拟克隆构造失败）。");
            }

            IsClone = true;

            if (source != null)
            {
                color = source.color;
            }
        }

        internal Material(bool countCreation)
        {
            if (countCreation)
            {
                CreatedTotal++;
                LastCreated = this;
            }
        }

        /// <summary>重置用例间的钩子与"最近创建"标记（只由用例在每段开头调用）。</summary>
        internal static void ResetHooks()
        {
            ThrowOnClone = false;
            LastCreated = null;
        }
    }

    /// <summary>
    /// Camera 替身。`Camera.main` 在这里是**显式设置的静态引用**（真实 Unity 是"找第一个
    /// MainCamera 标签的启用相机"）。它存在的唯一目的：让用例能断言表现层**没有碰过**它。
    /// </summary>
    public class Camera : Component
    {
        public static Camera main;
    }

    /// <summary>Application 替身：只保留 isPlaying（表现层用它选 Destroy / DestroyImmediate）。</summary>
    public static class Application
    {
        public static bool isPlaying;
    }

    /// <summary>与 Unity 同名的 primitive 类型枚举（替身只区分 Sphere/Capsule/其它）。</summary>
    public enum PrimitiveType
    {
        Sphere = 0,
        Capsule = 1,
        Cylinder = 2,
        Cube = 3,
        Plane = 4,
        Quad = 5,
    }

    /// <summary>与 Unity 同名的枚举（适配器只使用 Ignore）。</summary>
    public enum QueryTriggerInteraction
    {
        UseGlobal = 0,
        Ignore = 1,
        Collide = 2,
    }

    /// <summary>与 Unity 同名的射线命中结构（只实现 distance/normal/collider）。</summary>
    public struct RaycastHit
    {
        public float distance;
        public Vector3 normal;
        public Collider collider;
    }

    /// <summary>
    /// 受控物理场景：持有一批 Collider，并按"本替身的解析模型"回答 SphereCast / OverlapSphere。
    ///
    /// 替身模型语义：
    ///   · `gap <= 接触容差` ⇒ SphereCast 返回 `distance == 0`（替身顺便把该条 normal 记成 `-dir`，
    ///     这只是为了复现 R4-B 的**一次**观测，不是断言 PhysX 会永远如此；适配器不读 normal）；
    ///   · `gap > 0` 且朝向该面 ⇒ 射线 vs "按半径外扩的 AABB" 的 slab 距离，
    ///     normal = "AABB → 球心"的特征法线（仅诊断用，适配器不读）；
    ///   · 批量查询返回 `min(命中总数, 缓冲长度)`（饱和断言依赖此语义）。
    /// </summary>
    public class PhysicsScene
    {
        /// <summary>gap &lt;= 该值视为"起点已接触/重叠"（真实 PhysX 在几何正好接触时也报 distance=0）。</summary>
        internal const float InitialContactTolerance = 1e-5f;

        internal readonly List<Collider> Colliders = new List<Collider>();

        /// <summary>替身语义：false 用来构造"无效 PhysicsScene"（真实 Unity 里由 IsValid() 判定）。</summary>
        public bool Valid = true;

        /// <summary>
        /// 替身开关（**测试专用，不是对 PhysX 的断言**）：OverlapSphere 是否报出"恰好接触"
        /// （0 &lt;= gap &lt;= 容差）的 Collider。默认 true（与 R4-B 的观测一致）。
        ///
        /// 为什么需要它：真实 PhysX 的 overlap 与 sweep 各自受 contactOffset 影响，二者
        /// **不承诺**在"恰好接触"这一边界上给出同一结论。把开关置 false（只报真穿透 gap &lt; 0）
        /// 就能让被测适配器走到"只有扫掠报 distance == 0"这条路径，从而单独验证
        /// "零 TOI 不被当成无障碍"。它不改变任何真实引擎语义。
        /// </summary>
        public bool ReportTouchingInOverlap = true;

        public string Name;

        public int SphereCastCalls;
        public int OverlapSphereCalls;
        public float LastMaxDistance;
        public int LastLayerMask;
        public QueryTriggerInteraction LastQueryTriggerInteraction = QueryTriggerInteraction.UseGlobal;
        public QueryTriggerInteraction OverlapLastQueryTriggerInteraction = QueryTriggerInteraction.UseGlobal;

        private SceneManagement.Scene _ownerScene;

        public PhysicsScene()
            : this("<unnamed>")
        {
        }

        public PhysicsScene(string name)
        {
            Name = name;
        }

        public bool IsValid()
        {
            return Valid;
        }

        public bool IsEmpty()
        {
            return Colliders.Count == 0;
        }

        /// <summary>替身专用：把本物理世界绑定到一个场景，使白名单校验（同场景）可被验证。</summary>
        public void BindOwnerScene(SceneManagement.Scene scene)
        {
            _ownerScene = scene;
        }

        /// <summary>替身专用：加一个轴对齐盒（世界 AABB = center ± size/2）。</summary>
        public BoxCollider AddBox(string name, Vector3 center, Vector3 size, int layer)
        {
            GameObject go = NewOwner(name, layer);
            BoxCollider box = go.AddComponent<BoxCollider>();
            box.name = name;
            box.layer = layer;
            box.AabbMin = center - size * 0.5f;
            box.AabbMax = center + size * 0.5f;
            Colliders.Add(box);
            return box;
        }

        /// <summary>替身专用：加一个 **trigger** 盒（isTrigger=true）。</summary>
        public BoxCollider AddTriggerBox(string name, Vector3 center, Vector3 size, int layer)
        {
            BoxCollider box = AddBox(name, center, size, layer);
            box.isTrigger = true;
            return box;
        }

        /// <summary>替身专用：加一个非 Box 形状（用例只关心"它不是 BoxCollider"）。</summary>
        public MeshCollider AddMeshBox(string name, Vector3 center, Vector3 size, int layer)
        {
            GameObject go = NewOwner(name, layer);
            MeshCollider mesh = go.AddComponent<MeshCollider>();
            mesh.name = name;
            mesh.layer = layer;
            mesh.AabbMin = center - size * 0.5f;
            mesh.AabbMax = center + size * 0.5f;
            Colliders.Add(mesh);
            return mesh;
        }

        public int SphereCast(Vector3 origin, float radius, Vector3 direction, RaycastHit[] results,
                              float maxDistance, int layerMask, QueryTriggerInteraction queryTriggerInteraction)
        {
            SphereCastCalls++;
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
                if (!IsQueryable(collider, layerMask, queryTriggerInteraction)) { continue; }

                float gap;
                Vector3 normal;
                MeasureSphereAabb(origin, radius, collider, out gap, out normal);

                float distance;
                Vector3 hitNormal;

                if (gap <= InitialContactTolerance)
                {
                    // 替身模型：起点已接触/重叠 ⇒ distance=0。
                    // 顺便把 normal 记成 -dir 只为复现 R4-B 的**一次**观测；适配器根本不读它，
                    // 因此不能把这个赋值读成"PhysX 的所有接触法线恒为 -dir"。
                    distance = 0f;
                    hitNormal = dir * -1f;
                }
                else
                {
                    // 正间隙：用"AABB 按半径外扩 + 射线 slab"求扫掠距离。
                    // 该写法在**面**接触上是精确的；在棱/角处等于把扫掠球当"外扩盒"，
                    // 属**保守**（宁可早报、绝不漏报）——比"一阶 TOI + 当前方向"更不容易误报
                    // （一阶近似会把"路过盒外侧"误判成撞角）。
                    float swept;
                    if (!TrySweepSphereAabb(origin, dir, radius, collider, maxDistance, out swept))
                    {
                        continue;
                    }

                    distance = swept;
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

            // 真实 PhysX 的批量查询只返回容量内的项，count 取 min(总命中, 容量)。
            return total < results.Length ? total : results.Length;
        }

        public int OverlapSphere(Vector3 position, float radius, Collider[] results, int layerMask,
                                QueryTriggerInteraction queryTriggerInteraction)
        {
            OverlapSphereCalls++;
            LastLayerMask = layerMask;
            OverlapLastQueryTriggerInteraction = queryTriggerInteraction;

            if (results == null) { throw new ArgumentNullException("results"); }

            int total = 0;
            int written = 0;

            for (int i = 0; i < Colliders.Count; i++)
            {
                Collider collider = Colliders[i];
                if (!IsQueryable(collider, layerMask, queryTriggerInteraction)) { continue; }

                float gap;
                Vector3 normal;
                MeasureSphereAabb(position, radius, collider, out gap, out normal);
                if (gap > InitialContactTolerance) { continue; }

                // 只报真穿透（见 ReportTouchingInOverlap 的说明）。
                if (!ReportTouchingInOverlap && gap >= 0f) { continue; }

                total++;
                if (written < results.Length)
                {
                    results[written] = collider;
                    written++;
                }
            }

            return total < results.Length ? total : results.Length;
        }

        private GameObject NewOwner(string name, int layer)
        {
            GameObject go = new GameObject(name);
            go.layer = layer;
            if (_ownerScene.handle != 0)
            {
                go.scene = _ownerScene;
            }

            return go;
        }

        private static bool IsQueryable(Collider collider, int layerMask, QueryTriggerInteraction triggerMode)
        {
            if (collider == null || collider.Destroyed || !collider.enabled) { return false; }
            if (collider.gameObject == null) { return false; }

            if ((layerMask & (1 << collider.gameObject.layer)) == 0) { return false; }

            if (triggerMode == QueryTriggerInteraction.Ignore && collider.isTrigger) { return false; }

            return true;
        }

        /// <summary>
        /// 球（球心 + 半径）与 Collider 轴对齐 AABB 的有符号分离。
        ///
        /// 推导：点到 AABB 的距离在三个轴上可分离 ⇒
        ///   separation = sqrt(dx²+dy²+dz²)（dx/dz/dy 是球心到各轴区间的间隙），
        ///   gap = separation − radius（&gt;0 间隙、=0 接触、&lt;0 穿透）。
        /// separation &gt; 0 时特征法线 = 各轴间隙正交合成的单位向量（面/棱/角都对）；
        /// 球心已进入 AABB 时（separation == 0）法线本替身不定义，因此返回零向量
        /// （调用方在 `gap &lt;= 容差` 分支里会把它覆盖成 `-dir`，同样只为复现观测）。
        /// 无论哪条分支，**适配器都不读这个值**。
        /// </summary>
        internal static void MeasureSphereAabb(Vector3 center, float radius, Collider collider,
                                              out float gap, out Vector3 normal)
        {
            gap = 0f;
            normal = Vector3.zero;

            float dx = AxisGap(center.x, collider.AabbMin.x, collider.AabbMax.x);
            float dy = AxisGap(center.y, collider.AabbMin.y, collider.AabbMax.y);
            float dz = AxisGap(center.z, collider.AabbMin.z, collider.AabbMax.z);

            float separation = (float)Math.Sqrt((double)(dx * dx + dy * dy + dz * dz));
            gap = separation - radius;

            if (separation <= 1e-6f)
            {
                return;
            }

            float sx = center.x < collider.AabbMin.x ? -1f : (center.x > collider.AabbMax.x ? 1f : 0f);
            float sy = center.y < collider.AabbMin.y ? -1f : (center.y > collider.AabbMax.y ? 1f : 0f);
            float sz = center.z < collider.AabbMin.z ? -1f : (center.z > collider.AabbMax.z ? 1f : 0f);

            normal = new Vector3(sx * dx, sy * dy, sz * dz) * (1f / separation);
        }

        /// <summary>
        /// 球 vs AABB 的扫掠距离（射线 vs "按半径外扩的 AABB" 的 slab 测试）。
        ///
        /// 语义：
        ///   · 起点已在扩展盒内或正好贴面 ⇒ 返回 true 且 distance = 0
        ///     （与真实 PhysX 的"起点已接触/重叠报零距离"一致）；
        ///   · 盒完全在射线反向侧 ⇒ 返回 false；
        ///   · 否则 distance = 进入距离（&gt; maxDistance 则不报）。
        /// **简化声明**：外扩盒在棱/角处是球体的**保守超集**（等价于"球被当成边长为 2r 的盒子"），
        /// 因此只会**早报**、不会漏报；面对面的几何是精确的。本替身不模拟旋转形状与网格。
        /// </summary>
        internal static bool TrySweepSphereAabb(Vector3 origin, Vector3 dir, float radius, Collider collider,
                                                float maxDistance, out float distance)
        {
            distance = 0f;

            float tMin = float.NegativeInfinity;
            float tMax = float.PositiveInfinity;

            if (!Slab(origin.x, dir.x, collider.AabbMin.x - radius, collider.AabbMax.x + radius, ref tMin, ref tMax)
                || !Slab(origin.y, dir.y, collider.AabbMin.y - radius, collider.AabbMax.y + radius, ref tMin, ref tMax)
                || !Slab(origin.z, dir.z, collider.AabbMin.z - radius, collider.AabbMax.z + radius, ref tMin, ref tMax))
            {
                return false;
            }

            if (tMax < 0f)
            {
                return false;
            }

            if (tMin <= 0f)
            {
                distance = 0f;
                return true;
            }

            if (tMin > maxDistance)
            {
                return false;
            }

            distance = tMin;
            return true;
        }

        private static bool Slab(float origin, float dir, float lo, float hi, ref float tMin, ref float tMax)
        {
            if (Math.Abs(dir) < 1e-12f)
            {
                return origin >= lo && origin <= hi;
            }

            float inverse = 1f / dir;
            float t1 = (lo - origin) * inverse;
            float t2 = (hi - origin) * inverse;

            if (t1 > t2)
            {
                float swap = t1;
                t1 = t2;
                t2 = swap;
            }

            if (t1 > tMin) { tMin = t1; }
            if (t2 < tMax) { tMax = t2; }
            return tMin <= tMax;
        }

        private static float AxisGap(float value, float lo, float hi)
        {
            if (value < lo) { return lo - value; }
            if (value > hi) { return value - hi; }
            return 0f;
        }

        private static Vector3 Normalize(Vector3 v)
        {
            float length = v.magnitude;
            if (length <= 1e-12f) { return Vector3.zero; }
            return v * (1f / length);
        }

    }

    /// <summary>Physics 替身：只提供 defaultPhysicsScene（适配器据此拒绝"默认物理世界"）。</summary>
    public static class Physics
    {
        public static readonly PhysicsScene defaultPhysicsScene = new PhysicsScene("<default>");

        /// <summary>被调用次数。适配器必须恒为 0（它只走 `_scene.*`，不碰全局 Physics）。</summary>
        public static int StaticQueryCalls;

        public static void SyncTransforms()
        {
            StaticQueryCalls++;
        }
    }

    /// <summary>
    /// PhysicsSceneExtensions 替身：`scene.GetPhysicsScene()` 与真实 Unity 同形
    /// （扩展方法，位于 UnityEngine 命名空间；无本地物理时返回默认物理世界）。
    /// </summary>
    public static class PhysicsSceneExtensions
    {
        public static PhysicsScene GetPhysicsScene(this SceneManagement.Scene scene)
        {
            return scene.physics != null ? scene.physics : Physics.defaultPhysicsScene;
        }
    }
}

namespace UnityEngine.SceneManagement
{
    /// <summary>
    /// Scene 替身：只保留 handle / IsValid 与"该场景绑定的物理世界"。
    /// 真实 Unity 的 Scene 由引擎创建；本替身提供一个带参构造，供用例显式造场景。
    /// </summary>
    public struct Scene
    {
        public int handle;

        /// <summary>替身语义：本场景的 3D 物理世界（null ⇒ 由扩展方法回落到默认物理世界）。</summary>
        public PhysicsScene physics;

        public Scene(int handle, PhysicsScene physics)
        {
            this.handle = handle;
            this.physics = physics;
        }

        public bool IsValid()
        {
            return handle != 0;
        }
    }
}
