// ============================================================================
//  临时性桩件：UnityEngine / UnityEditor 的最小替身
// ============================================================================
//
//  用途
//  ----
//  本工程（Tools/PMUnityGlueCheck）在**没有 Unity 编辑器**的环境下，把「引用 UnityEngine
//  的胶水代码」真正编译一遍，用来抓语法错误、拼写错误、类型错用、括号不配对这类问题。
//  这类问题如果只能等开 Unity 才发现，代价很高（本项目的 DS 改造正是在无 Unity 环境下推进的）。
//
//  ⚠ 它验证什么、不验证什么（务必看清）
//  ------------------------------------
//  验证：胶水代码自身是否语法正确、类型是否自洽、是否用到了桩件里存在的成员。
//  不验证：真实 Unity API 的**签名与语义**。桩件里的签名是手写的，只能保证「形状接近」。
//         例如桩件里的 AddComponent<T>() 永远返回 null；真实 Unity 会返回实例。
//         因此本门禁通过 ≠ 逻辑正确，运行时行为仍需在 Unity 中验证。
//
//  维护约定
//  --------
//  当胶水代码用到新的 Unity API 时，需要在此补充对应桩件——这一步是刻意的：
//  它强迫调用者显式确认「我正在使用哪个 Unity API」。若某 API 无法用桩件表达
//  （例如依赖 IL 注入的魔术方法），请在该 API 处加注释说明。
//
//  已知刻意简化：
//   - MonoBehaviour 不声明 Awake/Update/Start 等方法。真实 Unity 里它们不是基类成员，
//     而是按名字反射调用的魔术方法；若在此声明为 virtual，会让子类的
//     `private void Update()` 触发 CS0114 假警告，掩盖真实问题。
//   - Object 的 implicit operator bool 恒为 false；真实语义依赖 Unity 的对象生命周期。
//
// ============================================================================

// 临时 UnityEngine / UnityEditor 桩件：仅用于在无 Unity 环境下编译校验胶水代码的语法与调用形状。
// 不进入仓库，不构成对真实 Unity API 签名的验证。
using System;

namespace UnityEngine
{
    public class Object
    {
        public string name;
        public static void DontDestroyOnLoad(Object target) { }
        public static void Destroy(Object obj) { }
        // R4-B（B3）：表现层在编辑模式下走 DestroyImmediate（真实 Unity 同语义）。
        public static void DestroyImmediate(Object obj) { }
        public static implicit operator bool(Object o) { return false; }

        // R4-C（C3）：C2 的正式内容加载走 Object.Instantiate(prefab)（泛型重载，返回同型）。
        public static T Instantiate<T>(T original) where T : Object { return null; }
        public static Object Instantiate(Object original) { return null; }
        public static T Instantiate<T>(T original, Transform parent) where T : Object { return null; }
        public static T Instantiate<T>(T original, Transform parent, bool worldPositionStays) where T : Object { return null; }
        public static T Instantiate<T>(T original, Vector3 position, Quaternion rotation) where T : Object { return null; }
    }

    public class Component : Object
    {
        public GameObject gameObject { get { return null; } }
        public T AddComponent<T>() where T : Component { return null; }
        public T GetComponent<T>() { return default(T); }
    }

    // R4-C（C3）：真实签名含 <c>Behaviour.enabled</c> / <c>isActiveAndEnabled</c>
    //（宿主临时关相机、C2 校验 Animator 激活态时都要用）。
    public class Behaviour : Component
    {
        public bool enabled { get; set; }
        public bool isActiveAndEnabled { get { return true; } }
    }

    // 真实 Unity 的 MonoBehaviour 不声明 Awake/Update —— 它们是按名字反射调用的魔术方法。
    // 若在此声明为 virtual，会让子类的 private void Update() 触发 CS0114 假警告，掩盖真实问题。
    public class MonoBehaviour : Behaviour
    {
    }

    public class GameObject : Object
    {
        public GameObject() { }
        public GameObject(string n) { name = n; }

        public bool activeSelf { get { return true; } }
        public bool activeInHierarchy { get { return true; } }

        /// <summary>R4-B（B3）：对象所属场景（真实签名为 UnityEngine.SceneManagement.Scene）。</summary>
        public UnityEngine.SceneManagement.Scene scene
        {
            get { return new UnityEngine.SceneManagement.Scene(); }
        }
        public Transform transform { get { return null; } }

        // R4-B（B3）：运动碰撞白名单按 GameObject.layer 组 mask（宿主需要它）。
        public int layer { get { return 0; } set { } }

        public T AddComponent<T>() where T : Component { return null; }
        public T GetComponent<T>() { return default(T); }
        /// <summary>R4-C（C3）：C2 的地图/角色组件白名单校验会遍历整棵子树。</summary>
        public T[] GetComponentsInChildren<T>(bool includeInactive) { return new T[0]; }
        public T[] GetComponentsInChildren<T>() { return new T[0]; }
        public void SetActive(bool value) { }

        // R3-B：固定测试碰撞场景用原语建「可见简单几何」（客户端侧）。
        public static GameObject CreatePrimitive(PrimitiveType type) { return null; }
    }

    // R3-B 新增：固定测试场景的碰撞几何与可见原语。
    public enum PrimitiveType { Sphere, Capsule, Cylinder, Cube, Plane, Quad }

    public struct Vector3
    {
        public float x;
        public float y;
        public float z;

        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }

        public static Vector3 zero { get { return new Vector3(0f, 0f, 0f); } }
        public static Vector3 one { get { return new Vector3(1f, 1f, 1f); } }

        // R4-B：运动碰撞适配器用 Y-up 胶囊端点（center ± up*offset），
        // 因此运算符面是必需的，不是“顺手加的”。
        public static Vector3 up { get { return new Vector3(0f, 1f, 0f); } }
        public static Vector3 down { get { return new Vector3(0f, -1f, 0f); } }
        public static Vector3 right { get { return new Vector3(1f, 0f, 0f); } }
        public static Vector3 forward { get { return new Vector3(0f, 0f, 1f); } }

        public float magnitude { get { return 0f; } }
        public float sqrMagnitude { get { return 0f; } }

        public static Vector3 operator +(Vector3 a, Vector3 b) { return new Vector3(a.x + b.x, a.y + b.y, a.z + b.z); }
        public static Vector3 operator -(Vector3 a, Vector3 b) { return new Vector3(a.x - b.x, a.y - b.y, a.z - b.z); }
        public static Vector3 operator -(Vector3 a) { return new Vector3(-a.x, -a.y, -a.z); }
        public static Vector3 operator *(Vector3 a, float s) { return new Vector3(a.x * s, a.y * s, a.z * s); }
        public static Vector3 operator *(float s, Vector3 a) { return new Vector3(a.x * s, a.y * s, a.z * s); }
        public static Vector3 operator /(Vector3 a, float s) { return new Vector3(a.x / s, a.y / s, a.z / s); }

        public static float Dot(Vector3 a, Vector3 b) { return 0f; }
        public static float Distance(Vector3 a, Vector3 b) { return 0f; }
        public static Vector3 Normalize(Vector3 v) { return v; }
        public static Vector3 normalized { get { return zero; } }
    }

    public class Transform : Component
    {
        public Vector3 position { get; set; }
        public Vector3 localPosition { get; set; }
        public Vector3 localScale { get; set; }
        public Vector3 eulerAngles { get; set; }
        public Quaternion rotation { get; set; }
        public Quaternion localRotation { get; set; }
        // R4-B：表现层把胶囊挂在根节点下（capsule.transform.parent = root.transform）。
        public Transform parent { get; set; }
        public void SetParent(Transform parent) { }
        public void SetParent(Transform parent, bool worldPositionStays) { }
    }

    public struct Quaternion
    {
        public float x, y, z, w;
        public static Quaternion identity { get { return new Quaternion(); } }
        public static Quaternion Euler(float x, float y, float z) { return new Quaternion(); }
        public static Quaternion Euler(Vector3 euler) { return new Quaternion(); }
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b) { this.r = r; this.g = g; this.b = b; this.a = 1f; }
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }

    public struct Bounds
    {
        public Vector3 center { get; set; }
        public Vector3 size { get; set; }
        public Vector3 extents { get; set; }
        public Vector3 min { get { return center; } }
        public Vector3 max { get { return center; } }
        public Bounds(Vector3 center, Vector3 size) { this.center = center; this.size = size; this.extents = size; }
        public bool Contains(Vector3 p) { return false; }
        public bool Intersects(Bounds b) { return false; }
        public void Encapsulate(Vector3 p) { }
        public void Encapsulate(Bounds b) { }
        public void Expand(float amount) { }
    }

    public struct RaycastHit
    {
        public Vector3 point { get { return Vector3.zero; } }
        public Vector3 normal { get { return Vector3.up; } }
        public float distance { get { return 0f; } }
        public Collider collider { get { return null; } }
        public Transform transform { get { return null; } }
    }

    public enum QueryTriggerInteraction { UseGlobal = 0, Ignore = 1, Collide = 2 }

    public class Collider : Component
    {
        public bool enabled { get; set; }
        public bool isTrigger { get; set; }
        public Bounds bounds { get { return new Bounds(); } }
    }

    public class SphereCollider : Collider
    {
        public float radius { get; set; }
        public Vector3 center { get; set; }
    }

    public class CapsuleCollider : Collider
    {
        public float radius { get; set; }
        public float height { get; set; }
        public Vector3 center { get; set; }
    }

    public class MeshCollider : Collider
    {
        public bool convex { get; set; }
    }

    /// <summary>R4-C（C3）：地图允许集里的 kinematic Rigidbody；动态（非 kinematic）一律拒绝。</summary>
    public class Rigidbody : Component
    {
        public bool isKinematic { get; set; }
        public float mass { get; set; }
    }

    public class BoxCollider : Collider
    {
        public Vector3 size { get; set; }
        public Vector3 center { get; set; }
    }

    /// <summary>
    /// R4-B（B3）：运动碰撞适配器的最小 PhysicsScene 替身（签名与真实 Unity 2019.4 一致）。
    /// <c>IsValid()</c> 是方法；CapsuleCast/OverlapCapsule 都是批量重载。
    /// </summary>
    public struct PhysicsScene
    {
        public bool IsValid() { return true; }

        /// <summary>
        /// R4-B（B3）隔离判定的**真实**比较面（已用 MetadataLoadContext 读 2019.4
        /// UnityEngine.PhysicsModule.dll 核实：真实 PhysicsScene 实现 IEquatable&lt;PhysicsScene&gt;，
        /// 并定义 Equals(PhysicsScene)/Equals(object)/GetHashCode/op_Equality/op_Inequality）。
        /// 替身只保证形状（无字段，Equals 恒 true），不模拟“默认物理世界”语义。
        /// </summary>
        public bool Equals(PhysicsScene other) { return true; }

        public override bool Equals(object obj) { return obj is PhysicsScene; }

        public override int GetHashCode() { return 0; }

        public static bool operator ==(PhysicsScene left, PhysicsScene right) { return left.Equals(right); }

        public static bool operator !=(PhysicsScene left, PhysicsScene right) { return !left.Equals(right); }

        public int CapsuleCast(Vector3 point1, Vector3 point2, float radius, Vector3 direction,
                               RaycastHit[] results, float maxDistance, int layerMask,
                               QueryTriggerInteraction queryTriggerInteraction)
        {
            return 0;
        }

        public bool CapsuleCast(Vector3 point1, Vector3 point2, float radius, Vector3 direction,
                                out RaycastHit hitInfo, float maxDistance, int layerMask,
                                QueryTriggerInteraction queryTriggerInteraction)
        {
            hitInfo = default(RaycastHit);
            return false;
        }

        public int OverlapCapsule(Vector3 point0, Vector3 point1, float radius, Collider[] results,
                                  int layerMask, QueryTriggerInteraction queryTriggerInteraction)
        {
            return 0;
        }
    }

    /// <summary>
    /// R4-B（B3）：真实 Unity 中 <c>Scene.GetPhysicsScene()</c> 是 PhysicsModule 的扩展方法
    /// <c>UnityEngine.PhysicsSceneExtensions.GetPhysicsScene(this Scene)</c>（不在 CoreModule）。
    /// 宿主用扩展方法语法取场景的物理世界，桩件必须有同名扩展。
    /// </summary>
    public static class PhysicsSceneExtensions
    {
        public static PhysicsScene GetPhysicsScene(this UnityEngine.SceneManagement.Scene scene)
        {
            return new PhysicsScene();
        }
    }

    public static class Physics
    {
        /// <summary>R4-B（B3）：适配器用默认真实物理场景。</summary>
        public static PhysicsScene defaultPhysicsScene { get { return new PhysicsScene(); } }

        /// <summary>R4-B（B3）：契约要求同步只由宿主单次做；替身不模拟同步。</summary>
        public static void SyncTransforms() { }

        public static bool Raycast(Vector3 origin, Vector3 dir, out RaycastHit hit, float maxDist) { hit = default(RaycastHit); return false; }
    }

    public enum CameraClearFlags { Skybox = 1, Color = 2, SolidColor = 2, Depth = 3, Nothing = 4 }

    public class Camera : Behaviour
    {
        public static Camera main { get { return null; } }
        /// <summary>R4-C（C3）：场景中**已启用**的相机（正式模式相机接管用）。</summary>
        public static Camera[] allCameras { get { return new Camera[0]; } }
        public float fieldOfView { get; set; }
        public float nearClipPlane { get; set; }
        public float farClipPlane { get; set; }
        public float depth { get; set; }
        public Color backgroundColor { get; set; }
        public CameraClearFlags clearFlags { get; set; }
    }

    public enum KeyCode
    {
        None = 0,
        Space = 32,
        A = 97, D = 100, S = 115, W = 119,
        LeftArrow = 276, RightArrow = 275, UpArrow = 273, DownArrow = 274,
    }

    public static class Input
    {
        public static bool GetKey(KeyCode key) { return false; }
        public static bool GetKeyDown(KeyCode key) { return false; }
        public static bool GetKeyUp(KeyCode key) { return false; }
        public static float GetAxisRaw(string axisName) { return 0f; }
        public static float GetAxis(string axisName) { return 0f; }
    }

    // R4-C（C3）：C2 的组件清单/摘要会读 Renderer.sharedMaterials。
    public class Renderer : Component
    {
        public Material[] sharedMaterials { get; set; }
        public Material sharedMaterial { get; set; }
        public Material[] materials { get; set; }
        public bool enabled { get; set; }
    }

    public class MeshRenderer : Renderer { }

    public static class Debug
    {
        public static void Log(object message) { }
        public static void LogWarning(object message) { }
        public static void LogError(object message) { }
    }

    public static class Application
    {
        public static bool runInBackground { get; set; }
        public static string dataPath { get { return ""; } }

        // R4-B（B3）：表现层在运行时走 Destroy、编辑模式走 DestroyImmediate。
        public static bool isPlaying { get { return false; } }

        // R3-B：新链以退出码区分「结果确认后正常退出」与「明确失败退出」。
        public static void Quit(int exitCode) { }
    }

    public static class Time
    {
        public static float unscaledTime { get { return 0f; } }
        public static float unscaledDeltaTime { get { return 0f; } }
        public static float realtimeSinceStartup { get { return 0f; } }

        // R4-B（B3）：子步驱动用真实帧时间（整 ms 余数累加）。
        public static float deltaTime { get { return 0f; } }

        // P3'-1 新增：PMDsHost 的诊断 handler 用它记录「最近一次收到战斗包是在哪一帧」。
        // 真实 UnityEngine.Time 自 5.x 起就有 frameCount（int），此处补上以免桩件缺面。
        public static int frameCount { get { return 0; } }
    }

    public static class Mathf
    {
        public static int Max(int a, int b) { return a > b ? a : b; }
        public static float Max(float a, float b) { return a > b ? a : b; }
    }

    public enum RuntimeInitializeLoadType { AfterSceneLoad, BeforeSceneLoad, AfterAssembliesLoaded, BeforeSplashScreen, SubsystemRegistration }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute
    {
        public RuntimeInitializeOnLoadMethodAttribute() { }
        public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType loadType) { }
    }

    // ============================================================================================
    //  R4-C / C3 补充的真实签名替身
    //
    //  来源：C2 的正式内容类（Client/Assets/Scripts/PMUnity/*.cs）与两个宿主现在都被本工程编入，
    //  它们用到下面这些 Unity 类型。签名按 Unity **2019.4** 真实 API 写（不是“看着像”）：
    //    · Animator.applyRootMotion / parameters / SetFloat / runtimeAnimatorController；
    //    · AnimatorControllerParameter(.name/.type) + AnimatorControllerParameterType（Float=1）；
    //    · MeshFilter / SkinnedMeshRenderer / LODGroup —— 地图/角色允许集白名单里的组件；
    //    · AudioListener —— 明确被拒绝的旧组件（拒绝路径必须能引用它）；
    //    · TerrainCollider —— 碰撞形状序号分类；
    //    · Rigidbody.isKinematic —— “动态刚体一律拒绝”判定；
    //    · JsonUtility / TextAsset / Resources —— manifest + prefab 加载面；
    //    · Camera.allCameras —— 正式模式“临时关掉其它相机确保可见”需要它。
    //  仍不保证语义（桩件不模拟行为），只保证**形状**。
    // ============================================================================================

    public enum AnimatorControllerParameterType
    {
        Float = 1,
        Int = 3,
        Bool = 4,
        Trigger = 9,
    }

    /// <summary>
    /// R4-C（C3）：真实签名里它是**引用类型**（C2 会做 <c>parameter == null</c> 判定），
    /// 因此这里必须是 class 而不是 struct。
    /// </summary>
    public class AnimatorControllerParameter
    {
        public string name { get { return ""; } }
        public AnimatorControllerParameterType type { get { return AnimatorControllerParameterType.Float; } }
        public float defaultFloat { get { return 0f; } }
        public int defaultInt { get { return 0; } }
        public bool defaultBool { get { return false; } }
    }

    public class RuntimeAnimatorController : Object { }

    public class Animator : Behaviour
    {
        public bool applyRootMotion { get; set; }
        public float speed { get; set; }
        public RuntimeAnimatorController runtimeAnimatorController { get; set; }
        public AnimatorControllerParameter[] parameters { get { return new AnimatorControllerParameter[0]; } }

        public void SetBool(string name, bool value) { }
        public void SetFloat(string name, float value) { }
        public void SetInteger(string name, int value) { }
        public void SetTrigger(string name) { }
        public void ResetTrigger(string name) { }
        public float GetFloat(string name) { return 0f; }
    }

    public class Mesh : Object { }

    public class Material : Object
    {
        public Color color { get; set; }
        public void SetFloat(string name, float value) { }
        public void SetColor(string name, Color value) { }
    }

    public class MeshFilter : Component
    {
        public Mesh mesh { get; set; }
        public Mesh sharedMesh { get; set; }
    }

    // R4-C（C3）：C2 允许角色 prefab 带 SkinnedMeshRenderer，并会读它的 sharedMesh/bones。
    public class SkinnedMeshRenderer : Renderer
    {
        public Mesh sharedMesh { get; set; }
        public Transform[] bones { get; set; }
    }

    public class LODGroup : Component { }

    public class AudioListener : Behaviour { }

    public class TerrainCollider : Collider { }

    public class TextAsset : Object
    {
        public string text { get { return null; } }
        public byte[] bytes { get { return null; } }
    }

    public static class Resources
    {
        public static T Load<T>(string path) where T : Object { return null; }
        public static Object Load(string path) { return null; }
    }

    public static class JsonUtility
    {
        public static string ToJson(object obj) { return "{}"; }
        public static string ToJson(object obj, bool prettyPrint) { return "{}"; }
        public static T FromJson<T>(string json) { return default(T); }
        public static void FromJsonOverwrite(string json, object obj) { }
    }
}

namespace UnityEngine.SceneManagement
{
    public struct Scene
    {
        public string name { get { return ""; } }
        public bool isLoaded { get { return true; } }
        public int handle { get { return 0; } }
        public bool IsValid() { return true; }
    }

    /// <summary>
    /// R4-B（B3）：创建本地物理场景的参数（真实签名：ctor 取 LocalPhysicsMode，属性名 localPhysicsMode）。
    /// </summary>
    public struct CreateSceneParameters
    {
        public CreateSceneParameters(LocalPhysicsMode physicsMode) { localPhysicsMode = physicsMode; }

        public LocalPhysicsMode localPhysicsMode { get; set; }
    }

    /// <summary>R4-B（B3）：场景是否自带本地 2D/3D 物理世界（真实枚举值 None=0 / Physics2D=1 / Physics3D=2）。</summary>
    public enum LocalPhysicsMode { None = 0, Physics2D = 1, Physics3D = 2 }

    /// <summary>
    /// R4-B（B3）：宿主用到的 SceneManager 面（建/切/卸本地物理场景）。
    /// 桩件只保证形状：所有成员返回默认值，不做任何真实场景管理。
    /// </summary>
    public static class SceneManager
    {
        public static Scene GetActiveScene() { return new Scene(); }

        public static Scene CreateScene(string sceneName) { return new Scene(); }

        public static Scene CreateScene(string sceneName, CreateSceneParameters parameters) { return new Scene(); }

        public static bool SetActiveScene(Scene scene) { return true; }

        public static void MoveGameObjectToScene(GameObject go, Scene scene) { }

        /// <summary>真实返回 AsyncOperation；替身返回 void（唯一调用点是语句，忽略返回值）。</summary>
        public static void UnloadSceneAsync(Scene scene) { }
    }
}

namespace UnityEditor
{
    using UnityEngine;

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class MenuItemAttribute : Attribute
    {
        public MenuItemAttribute(string itemName) { }
    }

    public static class EditorApplication
    {
        public static void Exit(int returnValue) { }
    }

    public static class EditorUtility
    {
        public static bool DisplayDialog(string title, string message, string ok) { return true; }
    }

    public static class AssetDatabase
    {
        public static void Refresh() { }
    }

    public enum BuildTarget { StandaloneWindows64 }

    [Flags]
    public enum BuildOptions
    {
        None = 0,
        Development = 1,
        // 真实 Unity 里用于「编译期无头包」。本项目在 2019.4 + Windows 上实测其可用性，
        // 因此胶水代码会引用它，桩件必须跟上。
        EnableHeadlessMode = 2,
    }

    public class BuildPlayerOptions
    {
        public string[] scenes;
        public string locationPathName;
        public BuildTarget target;
        public BuildOptions options;
    }

    public static class BuildPipeline
    {
        public static UnityEditor.Build.Reporting.BuildReport BuildPlayer(BuildPlayerOptions options)
        {
            return null;
        }
    }
}

namespace UnityEditor.Build.Reporting
{
    public enum BuildResult { Unknown, Succeeded, Failed, Cancelled }

    public struct BuildSummary
    {
        public BuildResult result { get { return BuildResult.Unknown; } }
        public ulong totalSize { get { return 0; } }
        public int totalErrors { get { return 0; } }
        public int totalWarnings { get { return 0; } }
    }

    public class BuildReport
    {
        public BuildSummary summary { get { return default(BuildSummary); } }
    }
}

namespace UnityEditor.SceneManagement
{
    using UnityEngine.SceneManagement;

    public enum NewSceneSetup { EmptyScene, DefaultGameObjects }
    public enum NewSceneMode { Single, Additive }

    public static class EditorSceneManager
    {
        public static Scene NewScene(NewSceneSetup setup, NewSceneMode mode) { return default(Scene); }
        public static bool SaveScene(Scene scene, string dstScenePath) { return true; }
        public static bool CloseScene(Scene scene, bool removeScene) { return true; }
    }
}

// ============================================================================
//  旧链边界替身：Server.UDPSocketManger
// ============================================================================
//  R4-B（B3）把客户端宿主也编进了本门禁，而它对旧链只有一个接触点：
//      global::Server.UDPSocketManger.CloseExisting();   // 关掉旧战斗 UDP socket
//  真实实现（Client/Assets/Scripts/Server/Manger/UDPSocketManger.cs）会把整套旧战斗链拖进来，
//  而本门禁要验证的是「新宿主的运动接线能否编译」。因此只替旧链边界，
//  **不替本项目任何新类型**（Driver / Physics / Session / PMNet / PMMover / PMPrediction 全编真实源码）。
//  签名与真实实现逐字一致（internal class + public static void CloseExisting()），
//  否则这里会给出假绿灯。
namespace Server
{
    internal static class UDPSocketManger
    {
        public static void CloseExisting()
        {
        }
    }
}
