// PMClientCheck 的 Unity 引擎替身 + 少量遗留类型替身。
//
// 设计原则（踩过坑后定下的）：
//   1) **桩三方/遗留、编一方**。仓库里的项目类型一律用真实文件编（见 PMClientCheck.csproj）——
//      它们是真代码，编进来比手写替身可信得多。只有 Unity 引擎类型，以及明确属于
//      「第三方/遗留且只需要极小接口面」的东西（EasyJoystick、遗留 TCPSocket），才写替身。
//   2) 只补**被校验代码真正用到**的成员。桩件越大，它与真实 Unity 的偏差越可能
//      掩盖问题、或制造真实编译里不存在的假错误。
//
// 已经踩过的两个"桩件保真度"坑（都是假阳性，方向安全但浪费时间）：
//   - 漏了 `UnityEngine.Object` 的 `implicit operator bool` → 到处报 `if (component)` 的 CS0029；
//   - `HideInInspector` 只声明 Field，而真实 Unity 允许用在属性上 → CS0592。
//
// ⚠ 边界：签名是**手写**的。它能抓「类型不匹配 / 参数个数顺序 / 拼写 / 缺成员」，
//   抓不到「桩件与真实签名不一致」。真实 Unity 编译仍是最终裁判。

using System;
using System.Collections;
using System.Collections.Generic;

namespace UnityEngine
{
    // ==================== 基础对象模型 ====================

    public class Object
    {
        public string name { get; set; }
        public int GetInstanceID() { return 0; }

        public static bool operator ==(Object a, Object b) { return ReferenceEquals(a, b); }
        public static bool operator !=(Object a, Object b) { return !ReferenceEquals(a, b); }
        public override bool Equals(object other) { return ReferenceEquals(this, other); }
        public override int GetHashCode() { return base.GetHashCode(); }
        public override string ToString() { return name ?? "<Object>"; }

        // 真实 Unity 的关键语义：UnityEngine.Object 可隐式转 bool（"已销毁则视作假"）。
        // 漏了它会到处报 `if (someComponent)` 的 CS0029。
        public static implicit operator bool(Object exists) { return !ReferenceEquals(exists, null); }

        public static void Destroy(Object o) { }
        public static void Destroy(Object o, float t) { }
        public static void DestroyImmediate(Object o) { }
        public static void DontDestroyOnLoad(Object o) { }

        public static T Instantiate<T>(T original) where T : Object { return original; }
        public static T Instantiate<T>(T original, Transform parent) where T : Object { return original; }
        public static T Instantiate<T>(T original, Transform parent, bool worldPositionStays) where T : Object { return original; }
        public static T Instantiate<T>(T original, Vector3 pos, Quaternion rot) where T : Object { return original; }
        public static T Instantiate<T>(T original, Vector3 pos, Quaternion rot, Transform parent) where T : Object { return original; }
        public static Object Instantiate(Object original, Vector3 pos, Quaternion rot, Transform parent) { return original; }

        public static T[] FindObjectsOfType<T>() where T : Object { return new T[0]; }
        public static T FindObjectOfType<T>() where T : Object { return null; }
        public static Object FindObjectOfType(Type type) { return null; }
        public static Object[] FindObjectsOfType(Type type) { return new Object[0]; }
    }

    public class Component : Object
    {
        public GameObject gameObject { get { return null; } }
        public Transform transform { get { return null; } }
        public string tag { get; set; }

        public T GetComponent<T>() { return default(T); }
        public Component GetComponent(Type t) { return null; }
        public T GetComponentInChildren<T>() { return default(T); }
        public T GetComponentInChildren<T>(bool includeInactive) { return default(T); }
        public T GetComponentInParent<T>() { return default(T); }
        public T[] GetComponentsInChildren<T>() { return new T[0]; }
        public T[] GetComponentsInChildren<T>(bool includeInactive) { return new T[0]; }
        public T[] GetComponents<T>() { return new T[0]; }
        public T AddComponent<T>() where T : Component { return default(T); }
        public void SendMessage(string methodName) { }
        public void SendMessage(string methodName, object value) { }
        public void SendMessage(string methodName, SendMessageOptions options) { }
        public void BroadcastMessage(string methodName) { }
    }

    public class Behaviour : Component
    {
        public bool enabled { get; set; }
        public bool isActiveAndEnabled { get { return true; } }
    }

    public class MonoBehaviour : Behaviour
    {
        public void Invoke(string methodName, float time) { }
        public void InvokeRepeating(string methodName, float time, float repeat) { }
        public void CancelInvoke() { }
        public void CancelInvoke(string methodName) { }
        public bool IsInvoking() { return false; }
        public Coroutine StartCoroutine(IEnumerator routine) { return null; }
        public Coroutine StartCoroutine(string methodName) { return null; }
        public void StopCoroutine(Coroutine c) { }
        public void StopCoroutine(IEnumerator c) { }
        public void StopAllCoroutines() { }
        public void print(object message) { }
        public static void print(object message, UnityEngine.Object context) { }
    }

    public class Coroutine { }

    public enum SendMessageOptions { RequireReceiver, DontRequireReceiver }

    public class GameObject : Object
    {
        public GameObject() { }
        public GameObject(string name) { this.name = name; }
        public GameObject(string name, params Type[] components) { this.name = name; }

        public bool activeSelf { get { return true; } }
        public bool activeInHierarchy { get { return true; } }

        /// <summary>
        /// R4-B（B3）：对象所属场景（真实签名为 <c>UnityEngine.SceneManagement.Scene</c>）。
        /// 宿主用它在建完地板/墙后**校验**对象确实在隔离的本地物理场景里，
        /// 而不是“以为切了活动场景”却落在默认世界。
        /// </summary>
        public UnityEngine.SceneManagement.Scene scene
        {
            get { return new UnityEngine.SceneManagement.Scene(); }
        }
        public string tag { get; set; }
        public int layer { get; set; }
        public Transform transform { get { return null; } }
        public GameObject gameObject { get { return this; } }

        public T GetComponent<T>() { return default(T); }
        public T GetComponentInChildren<T>() { return default(T); }
        public T GetComponentInChildren<T>(bool includeInactive) { return default(T); }
        public T GetComponentInParent<T>() { return default(T); }
        public T[] GetComponentsInChildren<T>() { return new T[0]; }
        public T[] GetComponentsInChildren<T>(bool includeInactive) { return new T[0]; }
        public T[] GetComponents<T>() { return new T[0]; }
        public T AddComponent<T>() where T : Component { return default(T); }
        public Component AddComponent(Type t) { return null; }
        public void SetActive(bool value) { }
        public bool CompareTag(string t) { return false; }

        // 真实 Unity 的 GameObject 自身也有 SendMessage 系列（不只 Component 有）。
        public void SendMessage(string methodName) { }
        public void SendMessage(string methodName, object value) { }
        public void SendMessage(string methodName, SendMessageOptions options) { }
        public void SendMessage(string methodName, object value, SendMessageOptions options) { }
        public void BroadcastMessage(string methodName) { }
        public void BroadcastMessage(string methodName, SendMessageOptions options) { }

        public static GameObject Find(string name) { return null; }
        public static GameObject FindWithTag(string tag) { return null; }
        public static GameObject FindGameObjectWithTag(string tag) { return null; }
        public static GameObject[] FindGameObjectsWithTag(string tag) { return new GameObject[0]; }

        // R3-B：固定测试碰撞场景用原语建「可见简单几何」（客户端侧）。
        public static GameObject CreatePrimitive(PrimitiveType type) { return null; }
    }

    // R3-B 新增：可见原语类型（真实 UnityEngine 同名枚举）。
    public enum PrimitiveType { Sphere, Capsule, Cylinder, Cube, Plane, Quad }

    public class Transform : Component, IEnumerable
    {
        public Vector3 position { get; set; }
        public Vector3 localPosition { get; set; }
        public Vector3 localScale { get; set; }
        public Vector3 lossyScale { get { return Vector3.one; } }
        public Vector3 eulerAngles { get; set; }
        public Vector3 localEulerAngles { get; set; }
        public Quaternion rotation { get; set; }
        public Quaternion localRotation { get; set; }
        public Vector3 forward { get { return Vector3.forward; } set { } }
        public Vector3 right { get { return Vector3.right; } }
        public Vector3 up { get { return Vector3.up; } }
        public Transform parent { get; set; }
        public Transform root { get { return this; } }
        public int childCount { get { return 0; } }

        public Transform GetChild(int i) { return null; }
        public Transform Find(string n) { return null; }
        public void SetParent(Transform p) { }
        public void SetParent(Transform p, bool worldPositionStays) { }
        public void LookAt(Vector3 target) { }
        public void LookAt(Transform target) { }
        public void LookAt(Vector3 target, Vector3 worldUp) { }
        public void Rotate(Vector3 eulers) { }
        public void Rotate(Vector3 eulers, Space relativeTo) { }
        public void Rotate(float x, float y, float z) { }
        public void Rotate(Vector3 axis, float angle) { }
        public void RotateAround(Vector3 point, Vector3 axis, float angle) { }
        public void Translate(Vector3 translation) { }
        public void Translate(Vector3 translation, Space relativeTo) { }
        public void Translate(float x, float y, float z) { }
        public void SetSiblingIndex(int index) { }
        public int GetSiblingIndex() { return 0; }
        public void DetachChildren() { }
        public void SetAsLastSibling() { }
        public void SetAsFirstSibling() { }
        public IEnumerator GetEnumerator() { yield break; }
    }

    public class RectTransform : Transform
    {
        public Vector2 anchoredPosition { get; set; }
        public Vector2 sizeDelta { get; set; }
        public Vector2 anchorMin { get; set; }
        public Vector2 anchorMax { get; set; }
        public Vector2 pivot { get; set; }
        public Rect rect { get { return new Rect(); } }
    }

    // ==================== 数学 ====================

    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero { get { return new Vector2(0, 0); } }
        public static Vector2 one { get { return new Vector2(1, 1); } }
        public static Vector2 up { get { return new Vector2(0, 1); } }
        public static Vector2 right { get { return new Vector2(1, 0); } }
        public float magnitude { get { return 0f; } }
        public float sqrMagnitude { get { return 0f; } }
        public Vector2 normalized { get { return this; } }
        public void Normalize() { }
        public static float Distance(Vector2 a, Vector2 b) { return 0f; }
        public static Vector2 Lerp(Vector2 a, Vector2 b, float t) { return a; }
        public static float Dot(Vector2 a, Vector2 b) { return 0f; }
        public static Vector2 operator +(Vector2 a, Vector2 b) { return new Vector2(a.x + b.x, a.y + b.y); }
        public static Vector2 operator -(Vector2 a, Vector2 b) { return new Vector2(a.x - b.x, a.y - b.y); }
        public static Vector2 operator *(Vector2 a, float s) { return new Vector2(a.x * s, a.y * s); }
        public static Vector2 operator *(float s, Vector2 a) { return new Vector2(a.x * s, a.y * s); }
        public static Vector2 operator /(Vector2 a, float s) { return new Vector2(a.x / s, a.y / s); }
        public static bool operator ==(Vector2 a, Vector2 b) { return a.x == b.x && a.y == b.y; }
        public static bool operator !=(Vector2 a, Vector2 b) { return !(a == b); }
        public override bool Equals(object o) { return false; }
        public override int GetHashCode() { return 0; }
        public static implicit operator Vector3(Vector2 v) { return new Vector3(v.x, v.y, 0f); }
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y) { this.x = x; this.y = y; this.z = 0f; }
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }

        public static Vector3 zero { get { return new Vector3(0, 0, 0); } }
        public static Vector3 one { get { return new Vector3(1, 1, 1); } }
        public static Vector3 up { get { return new Vector3(0, 1, 0); } }
        public static Vector3 down { get { return new Vector3(0, -1, 0); } }
        public static Vector3 left { get { return new Vector3(-1, 0, 0); } }
        public static Vector3 right { get { return new Vector3(1, 0, 0); } }
        public static Vector3 forward { get { return new Vector3(0, 0, 1); } }
        public static Vector3 back { get { return new Vector3(0, 0, -1); } }

        public float magnitude { get { return 0f; } }
        public float sqrMagnitude { get { return 0f; } }
        public Vector3 normalized { get { return this; } }

        public void Normalize() { }
        public void Set(float nx, float ny, float nz) { x = nx; y = ny; z = nz; }

        public static float Distance(Vector3 a, Vector3 b) { return 0f; }
        public static float SqrMagnitude(Vector3 v) { return 0f; }
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) { return a; }
        public static Vector3 LerpUnclamped(Vector3 a, Vector3 b, float t) { return a; }
        public static Vector3 MoveTowards(Vector3 cur, Vector3 target, float maxDelta) { return cur; }
        public static Vector3 Normalize(Vector3 v) { return v; }
        public static float Dot(Vector3 a, Vector3 b) { return 0f; }
        public static Vector3 Cross(Vector3 a, Vector3 b) { return zero; }
        public static Vector3 Scale(Vector3 a, Vector3 b) { return a; }
        public static float Angle(Vector3 a, Vector3 b) { return 0f; }
        public static Vector3 ClampMagnitude(Vector3 v, float maxLength) { return v; }
        public static Vector3 Project(Vector3 v, Vector3 onNormal) { return v; }
        public static Vector3 ProjectOnPlane(Vector3 v, Vector3 planeNormal) { return v; }
        public static Vector3 Reflect(Vector3 inDirection, Vector3 normal) { return inDirection; }
        public static Vector3 SmoothDamp(Vector3 cur, Vector3 target, ref Vector3 vel, float smoothTime) { return cur; }
        public static Vector3 SmoothDamp(Vector3 cur, Vector3 target, ref Vector3 vel, float smoothTime, float maxSpeed) { return cur; }
        public static Vector3 SmoothDamp(Vector3 cur, Vector3 target, ref Vector3 vel, float smoothTime, float maxSpeed, float deltaTime) { return cur; }

        public static Vector3 operator +(Vector3 a, Vector3 b) { return new Vector3(a.x + b.x, a.y + b.y, a.z + b.z); }
        public static Vector3 operator -(Vector3 a, Vector3 b) { return new Vector3(a.x - b.x, a.y - b.y, a.z - b.z); }
        public static Vector3 operator -(Vector3 a) { return new Vector3(-a.x, -a.y, -a.z); }
        public static Vector3 operator *(Vector3 a, float s) { return new Vector3(a.x * s, a.y * s, a.z * s); }
        public static Vector3 operator *(float s, Vector3 a) { return new Vector3(a.x * s, a.y * s, a.z * s); }
        public static Vector3 operator /(Vector3 a, float s) { return new Vector3(a.x / s, a.y / s, a.z / s); }
        public static bool operator ==(Vector3 a, Vector3 b) { return a.x == b.x && a.y == b.y && a.z == b.z; }
        public static bool operator !=(Vector3 a, Vector3 b) { return !(a == b); }
        public override bool Equals(object o) { return false; }
        public override int GetHashCode() { return 0; }
        public override string ToString() { return "(" + x + ", " + y + ", " + z + ")"; }

        public static implicit operator Vector2(Vector3 v) { return new Vector2(v.x, v.y); }
    }

    public struct Vector4
    {
        public float x, y, z, w;
        public Vector4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Vector4 zero { get { return new Vector4(0, 0, 0, 0); } }
    }

    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Quaternion identity { get { return new Quaternion(0, 0, 0, 1); } }
        public static Quaternion Euler(float x, float y, float z) { return identity; }
        public static Quaternion Euler(Vector3 euler) { return identity; }
        public static Quaternion LookRotation(Vector3 forward) { return identity; }
        public static Quaternion LookRotation(Vector3 forward, Vector3 up) { return identity; }
        public static Quaternion Slerp(Quaternion a, Quaternion b, float t) { return a; }
        public static Quaternion Lerp(Quaternion a, Quaternion b, float t) { return a; }
        public static Quaternion RotateTowards(Quaternion from, Quaternion to, float maxDegrees) { return from; }
        public static Quaternion AngleAxis(float angle, Vector3 axis) { return identity; }
        public static Quaternion FromToRotation(Vector3 from, Vector3 to) { return identity; }
        public static Quaternion Inverse(Quaternion q) { return q; }
        public Vector3 eulerAngles { get { return Vector3.zero; } set { } }
        public static float Angle(Quaternion a, Quaternion b) { return 0f; }
        public static float Dot(Quaternion a, Quaternion b) { return 0f; }
        public void SetLookRotation(Vector3 view) { }
        public static Quaternion operator *(Quaternion a, Quaternion b) { return a; }
        public static Vector3 operator *(Quaternion q, Vector3 v) { return v; }
        public static bool operator ==(Quaternion a, Quaternion b) { return true; }
        public static bool operator !=(Quaternion a, Quaternion b) { return false; }
        public override bool Equals(object o) { return false; }
        public override int GetHashCode() { return 0; }
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b) { this.r = r; this.g = g; this.b = b; this.a = 1f; }
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color white { get { return new Color(1, 1, 1); } }
        public static Color black { get { return new Color(0, 0, 0); } }
        public static Color red { get { return new Color(1, 0, 0); } }
        public static Color green { get { return new Color(0, 1, 0); } }
        public static Color blue { get { return new Color(0, 0, 1); } }
        public static Color yellow { get { return new Color(1, 1, 0); } }
        public static Color cyan { get { return new Color(0, 1, 1); } }
        public static Color magenta { get { return new Color(1, 0, 1); } }
        public static Color gray { get { return new Color(0.5f, 0.5f, 0.5f); } }
        public static Color clear { get { return new Color(0, 0, 0, 0); } }

        public static Color Lerp(Color a, Color b, float t) { return a; }
        public static Color LerpUnclamped(Color a, Color b, float t) { return a; }

        // 真实 Unity 的 Color 支持四则运算（含与 Color 相加/相减）。
        public static Color operator +(Color a, Color b) { return new Color(a.r + b.r, a.g + b.g, a.b + b.b, a.a + b.a); }
        public static Color operator -(Color a, Color b) { return new Color(a.r - b.r, a.g - b.g, a.b - b.b, a.a - b.a); }
        public static Color operator *(Color a, float s) { return new Color(a.r * s, a.g * s, a.b * s, a.a * s); }
        public static Color operator *(float s, Color a) { return new Color(a.r * s, a.g * s, a.b * s, a.a * s); }
        public static Color operator *(Color a, Color b) { return new Color(a.r * b.r, a.g * b.g, a.b * b.b, a.a * b.a); }
        public static Color operator /(Color a, float s) { return new Color(a.r / s, a.g / s, a.b / s, a.a / s); }
    }

    public struct Color32
    {
        public byte r, g, b, a;
        public Color32(byte r, byte g, byte b, byte a) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }

    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float w, float h) { this.x = x; this.y = y; width = w; height = h; }
        public Vector2 position { get { return new Vector2(x, y); } set { } }
        public Vector2 size { get { return new Vector2(width, height); } set { } }
        public bool Contains(Vector2 p) { return false; }
    }

    public struct RaycastHit
    {
        public Vector3 point { get { return Vector3.zero; } }
        public Vector3 normal { get { return Vector3.up; } }
        public float distance { get { return 0f; } }
        public Collider collider { get { return null; } }
        public Transform transform { get { return null; } }
        public GameObject colliderObject { get { return null; } }
    }

    public struct Ray
    {
        public Vector3 origin, direction;
        public Ray(Vector3 origin, Vector3 direction) { this.origin = origin; this.direction = direction; }
    }

    /// <summary>注意：真实 Unity 的 Plane.normal / distance 是**可写属性**。</summary>
    public struct Plane
    {
        public Vector3 normal { get; set; }
        public float distance { get; set; }
        public Plane(Vector3 normal, Vector3 point) { this.normal = normal; distance = 0f; }
        public Plane(Vector3 normal, float d) { this.normal = normal; distance = d; }
        public bool Raycast(Ray ray, out float enter) { enter = 0f; return false; }
        public Vector3 ClosestPointOnPlane(Vector3 point) { return point; }
        public float GetDistanceToPoint(Vector3 point) { return 0f; }
    }

    // ==================== 物理 ====================

    public enum ForceMode { Force, Acceleration, Impulse, VelocityChange }
    public enum Space { World, Self }
    public enum RigidbodyConstraints { None, FreezePosition = 2, FreezeRotation = 16, FreezeAll = 18 }
    public enum RigidbodyInterpolation { None, Interpolate, Extrapolate }
    public enum CollisionDetectionMode { Discrete, Continuous, ContinuousDynamic, ContinuousSpeculative }
    public enum QueryTriggerInteraction { UseGlobal, Ignore, Collide }

    public class Collider : Component
    {
        public bool isTrigger { get; set; }
        public bool enabled { get; set; }
        public Bounds bounds { get { return new Bounds(); } }
        public Transform transform { get { return null; } }
        public GameObject gameObject { get { return null; } }
        public Rigidbody attachedRigidbody { get { return null; } }
        public Material material { get; set; }
    }

    public class BoxCollider : Collider { public Vector3 size { get; set; } public Vector3 center { get; set; } }
    public class SphereCollider : Collider { public float radius { get; set; } public Vector3 center { get; set; } }
    public class CapsuleCollider : Collider { public float radius { get; set; } public float height { get; set; } public Vector3 center { get; set; } }
    public class MeshCollider : Collider { public bool convex { get; set; } }

    // R4-C（C3）：C2 正式内容/宿主用到的其余碰撞与渲染组件（允许集白名单的判定面）。
    public class TerrainCollider : Collider { }
    public class AudioListener : Behaviour { }
    public class LODGroup : Component { }

    public class CharacterController : Collider
    {
        public void Move(Vector3 motion) { }
        public bool isGrounded { get { return false; } }
        public float height { get; set; }
        public float radius { get; set; }
        public bool detectCollisions { get; set; }
        public Vector3 velocity { get { return Vector3.zero; } }
    }

    public struct Bounds
    {
        public Vector3 center { get; set; }
        public Vector3 size { get; set; }
        public Vector3 extents { get { return Vector3.zero; } set { } }
        public Vector3 min { get { return Vector3.zero; } }
        public Vector3 max { get { return Vector3.zero; } }
        public Bounds(Vector3 center, Vector3 size) { this.center = center; this.size = size; }
        public bool Contains(Vector3 p) { return false; }
        public bool Intersects(Bounds b) { return false; }
        public void Encapsulate(Vector3 p) { }
        public void Encapsulate(Bounds b) { }
        public void Expand(float amount) { }
    }

    public class Rigidbody : Component
    {
        public Vector3 velocity { get; set; }
        public Vector3 angularVelocity { get; set; }
        public Vector3 position { get; set; }
        public Quaternion rotation { get; set; }
        public float mass { get; set; }
        public float drag { get; set; }
        public float angularDrag { get; set; }
        public bool useGravity { get; set; }
        public bool isKinematic { get; set; }
        public bool freezeRotation { get; set; }
        public RigidbodyConstraints constraints { get; set; }
        public RigidbodyInterpolation interpolation { get; set; }
        public CollisionDetectionMode collisionDetectionMode { get; set; }

        public void AddForce(Vector3 force) { }
        public void AddForce(Vector3 force, ForceMode mode) { }
        public void AddForce(float x, float y, float z) { }
        public void AddForce(float x, float y, float z, ForceMode mode) { }
        public void AddRelativeForce(Vector3 force) { }
        public void AddTorque(Vector3 torque) { }
        public void MovePosition(Vector3 position) { }
        public void MoveRotation(Quaternion rot) { }
        public void Sleep() { }
        public void WakeUp() { }
        public bool IsSleeping() { return false; }
        public void SetDensity(float d) { }
    }

    public class Collision
    {
        public GameObject gameObject { get { return null; } }
        public Collider collider { get { return null; } }
        public Transform transform { get { return null; } }
        public Vector3 relativeVelocity { get { return Vector3.zero; } }
        public Vector3 impulse { get { return Vector3.zero; } }
        public ContactPoint[] contacts { get { return new ContactPoint[0]; } }
        public int contactCount { get { return 0; } }
    }

    public struct ContactPoint
    {
        public Vector3 point { get { return Vector3.zero; } }
        public Vector3 normal { get { return Vector3.up; } }
        public Collider thisCollider { get { return null; } }
        public Collider otherCollider { get { return null; } }
    }

    /// <summary>
    /// R4-B（B3）运动碰撞适配器的最小 PhysicsScene 替身。
    ///
    /// 签名与 Unity 2019.4 一致（已在 Tools/PMR4UnityCheck / Tools/PMR5UnityCheck 里用**真实 DLL** 校验过）：
    ///   · <c>IsValid()</c> 是**方法**（不是属性）；
    ///   · <c>CapsuleCast</c> 的批量重载返回命中个数（结果写进数组）；
    ///   · <c>OverlapCapsule</c> 返回重叠个数；
    ///   · R5-C1（投射物）新增用到的 <c>SphereCast</c> / <c>OverlapSphere</c> 批量重载 ——
    ///     它们与 CapsuleCast/OverlapCapsule **同为批量重载**（返回个数、结果写进数组），
    ///     签名逐字对应 Unity 2019.4 的 <c>PhysicsScene</c>（由 PMR5UnityCheck 引真实 DLL 校验）。
    /// 替身不实现任何几何（无 PhysX），只保证“新适配器+宿主”能在本语言面上编译。
    /// </summary>
    public struct PhysicsScene
    {
        public bool IsValid() { return true; }

        /// <summary>
        /// R4-B（B3）隔离判定的**真实**比较面。已用 MetadataLoadContext 读 2019.4
        /// <c>UnityEngine.PhysicsModule.dll</c> 核实：真实 <c>PhysicsScene</c> 实现
        /// <c>IEquatable&lt;PhysicsScene&gt;</c>，并定义 <c>Equals(PhysicsScene)</c>、<c>Equals(object)</c>、
        /// <c>GetHashCode</c>、<c>op_Equality</c>、<c>op_Inequality</c>。
        /// 替身只保证**签名形状**（无字段，Equals 恒 true）；**不**模拟“是否默认物理世界”的语义，
        /// 因此门禁通过不等于隔离正确——隔离必须在 Unity 内跑真实 PhysX 才能证明。
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
            hitInfo = new RaycastHit();
            return false;
        }

        public int OverlapCapsule(Vector3 point0, Vector3 point1, float radius, Collider[] results,
                                  int layerMask, QueryTriggerInteraction queryTriggerInteraction)
        {
            return 0;
        }

        /// <summary>
        /// R5-C1（投射物 C1）：批量球体扫掠。签名逐字对应 Unity 2019.4
        /// <c>PhysicsScene.SphereCast(Vector3, float, Vector3, RaycastHit[], float, int, QueryTriggerInteraction)</c>
        /// （返回命中个数；真实 DLL 已由 Tools/PMR5UnityCheck 校验）。
        /// 替身不实现几何，恒返回 0（= 未命中）；本门禁只验证**调用面**能否编译。
        /// </summary>
        public int SphereCast(Vector3 origin, float radius, Vector3 direction, RaycastHit[] results,
                              float maxDistance, int layerMask,
                              QueryTriggerInteraction queryTriggerInteraction)
        {
            return 0;
        }

        /// <summary>
        /// R5-C1（投射物 C1）：批量球体重叠。签名逐字对应 Unity 2019.4
        /// <c>PhysicsScene.OverlapSphere(Vector3, float, Collider[], int, QueryTriggerInteraction)</c>
        /// （返回重叠个数：语义是 "touching or inside"）。替身不实现几何，恒返回 0。
        /// </summary>
        public int OverlapSphere(Vector3 position, float radius, Collider[] results,
                                int layerMask, QueryTriggerInteraction queryTriggerInteraction)
        {
            return 0;
        }
    }

    /// <summary>
    /// R4-B（B3）：真实 Unity 里 <c>Scene.GetPhysicsScene()</c> 不是 Scene 的实例方法，
    /// 而是 PhysicsModule 的扩展方法 <c>UnityEngine.PhysicsSceneExtensions.GetPhysicsScene(this Scene)</c>
    /// （已用 MetadataLoadContext 核实其存在于 PhysicsModule，而不在 CoreModule）。
    /// 宿主与 Editor 验证都用扩展方法语法取场景的物理世界，替身必须提供同名扩展，
    /// 否则“宿主实际写下的调用”无法在门禁里编译。
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
        /// <summary>
        /// R4-B（B3）：运动碰撞适配器用的默认真实物理场景（对应 UnityEngine.Physics.defaultPhysicsScene）。
        /// 替身只保证**形状**（返回一个可用的 PhysicsScene 值），不保证行为。
        /// </summary>
        public static PhysicsScene defaultPhysicsScene { get { return new PhysicsScene(); } }

        /// <summary>
        /// R4-B（B3）：契约要求“构建/每帧开始的 Physics.SyncTransforms 由宿主单次做”。
        /// 替身不模拟同步（无 PhysX），方法存在只是为了验证宿主的调用面。
        /// </summary>
        public static void SyncTransforms() { }

        public static bool Raycast(Vector3 origin, Vector3 dir, out RaycastHit hit, float maxDist) { hit = new RaycastHit(); return false; }
        public static bool Raycast(Vector3 origin, Vector3 dir, out RaycastHit hit, float maxDist, int layerMask) { hit = new RaycastHit(); return false; }
        public static bool Raycast(Vector3 origin, Vector3 dir, out RaycastHit hit, float maxDist, int layerMask, QueryTriggerInteraction q) { hit = new RaycastHit(); return false; }
        public static bool Raycast(Ray ray, out RaycastHit hit, float maxDist, int layerMask) { hit = new RaycastHit(); return false; }
        public static RaycastHit[] RaycastAll(Vector3 origin, Vector3 dir, float maxDist) { return new RaycastHit[0]; }
        public static RaycastHit[] SphereCastAll(Vector3 origin, float radius, Vector3 dir, float maxDist) { return new RaycastHit[0]; }
        public static bool SphereCast(Vector3 origin, float radius, Vector3 dir, out RaycastHit hit, float maxDist) { hit = new RaycastHit(); return false; }
        public static Collider[] OverlapSphere(Vector3 pos, float radius) { return new Collider[0]; }
        public static Collider[] OverlapSphere(Vector3 pos, float radius, int layerMask) { return new Collider[0]; }
        public static void IgnoreCollision(Collider a, Collider b) { }
        public static void IgnoreCollision(Collider a, Collider b, bool ignore) { }
        public static void IgnoreLayerCollision(int layer1, int layer2) { }
        public static void IgnoreLayerCollision(int layer1, int layer2, bool ignore) { }
    }

    public struct LayerMask
    {
        public int value { get; set; }
        public static int NameToLayer(string name) { return 0; }
        public static string LayerToName(int layer) { return ""; }
        public static int GetMask(params string[] names) { return 0; }
        public static implicit operator int(LayerMask m) { return m.value; }
        public static implicit operator LayerMask(int v) { return new LayerMask { value = v }; }
    }

    // ==================== 渲染 / 资源 ====================

    public class Sprite : Object { public Rect rect { get { return new Rect(); } } public Texture2D texture { get { return null; } } }
    public class Font : Object { public static Font CreateDynamicFontFromOSFont(string name, int size) { return null; } }
    /// <summary>
    /// 材质替身。
    ///
    /// R5-C2（表现层 C2）：适配器用 <c>new Material(内建共享材质)</c> **克隆一次**占位材质
    /// （然后只改克隆体的 color，从不写源材质），因此必须提供 <c>Material(Material)</c> 重载
    /// —— 该重载在 Unity 2019.4 真实程序集里存在（由 Tools/PMR5UnityCheck 引真实 DLL 校验）。
    /// 注意：一旦显式声明任何构造函数，隐式无参构造就**不再生成**，因此这里两者都给。
    /// </summary>
    public class Material : Object
    {
        public Color color { get; set; }

        public Material() { }

        public Material(Material source) { }

        public void SetFloat(string n, float v) { }
        public void SetColor(string n, Color c) { }
    }
    public class Texture : Object { }
    public class Texture2D : Texture { public Texture2D(int w, int h) { } public void Apply() { } }
    public class RenderTexture : Texture { }
    public class Shader : Object { public static Shader Find(string name) { return null; } }
    public class Mesh : Object { }

    /// <summary>R4-C（C3）：地图允许集里的 MeshFilter（C2 白名单逐组件判定要用）。</summary>
    public class MeshFilter : Component
    {
        public Mesh mesh { get; set; }
        public Mesh sharedMesh { get; set; }
    }
    public class Animation : Behaviour
    {
        public void Play(string n) { }
        public void Play() { }
        public void Stop() { }
        public void CrossFade(string animation) { }
        public void CrossFade(string animation, float fadeLength) { }
        public bool IsPlaying(string name) { return false; }
    }

    public class Renderer : Component
    {
        public Material material { get; set; }
        public Material sharedMaterial { get; set; }
        public Material[] materials { get; set; }
        // R4-C（C3）：C2 的组件清单/摘要要读 sharedMaterials（渲染允许集的一部份）。
        public Material[] sharedMaterials { get; set; }
        public bool enabled { get; set; }
        public Bounds bounds { get { return new Bounds(); } }
    }

    public class MeshRenderer : Renderer { }

    public class SpriteRenderer : Renderer
    {
        public Sprite sprite { get; set; }
        public Color color { get; set; }
        public bool flipX { get; set; }
        public bool flipY { get; set; }
        public int sortingOrder { get; set; }
        public string sortingLayerName { get; set; }
    }

    public class LineRenderer : Renderer
    {
        public int positionCount { get; set; }
        public float startWidth { get; set; }
        public float endWidth { get; set; }
        public float widthMultiplier { get; set; }
        public Color startColor { get; set; }
        public Color endColor { get; set; }
        public bool useWorldSpace { get; set; }
        public bool enabled { get; set; }
        public int numCapVertices { get; set; }
        public void SetPosition(int index, Vector3 pos) { }
        public void SetPositions(Vector3[] positions) { }
    }

    public enum CameraClearFlags { Skybox = 1, Color = 2, SolidColor = 2, Depth = 3, Nothing = 4 }

    public class Camera : Behaviour
    {
        public static Camera main { get { return null; } }
        /// <summary>R4-C（C3）：场景中**已启用**的相机（正式模式相机接管用）。</summary>
        public static Camera[] allCameras { get { return new Camera[0]; } }
        public float orthographicSize { get; set; }
        public bool orthographic { get; set; }
        public float fieldOfView { get; set; }
        public float nearClipPlane { get; set; }
        public float farClipPlane { get; set; }
        public Color backgroundColor { get; set; }
        public CameraClearFlags clearFlags { get; set; }
        public Vector3 WorldToScreenPoint(Vector3 pos) { return Vector3.zero; }
        public Vector3 ScreenToWorldPoint(Vector3 pos) { return Vector3.zero; }
        public Vector3 ScreenToViewportPoint(Vector3 pos) { return Vector3.zero; }
    }

    public class Light : Behaviour { public float intensity { get; set; } public Color color { get; set; } }

    public class AudioClip : Object { public float length { get { return 0f; } } }

    public class AudioSource : Behaviour
    {
        public AudioClip clip { get; set; }
        public float volume { get; set; }
        public float pitch { get; set; }
        public bool loop { get; set; }
        public bool isPlaying { get { return false; } }
        public Vector3 position { get; set; }
        public void Play() { }
        public void Stop() { }
        public void Pause() { }
        public void PlayOneShot(AudioClip clip) { }
        public void PlayOneShot(AudioClip clip, float volumeScale) { }
        public static void PlayClipAtPoint(AudioClip clip, Vector3 pos) { }
    }

    public class ParticleSystem : Component
    {
        public bool isPlaying { get { return false; } }
        public void Play() { }
        public void Stop() { }
        public void Clear() { }
        public void Emit(int count) { }
    }

    public class Animator : Behaviour
    {
        public void SetBool(string name, bool value) { }
        public void SetBool(int id, bool value) { }
        public void SetFloat(string name, float value) { }
        public void SetFloat(string name, float value, float dampTime, float deltaTime) { }
        public void SetFloat(int id, float value) { }
        public void SetInteger(string name, int value) { }
        public void SetInteger(int id, int value) { }
        public void SetTrigger(string name) { }
        public void SetTrigger(int id) { }
        public void ResetTrigger(string name) { }
        public float GetFloat(string name) { return 0f; }
        public bool GetBool(string name) { return false; }
        public int GetInteger(string name) { return 0; }
        public void Play(string stateName) { }
        public void Play(string stateName, int layer) { }
        public void CrossFade(string stateName, float duration) { }
        public void CrossFadeInFixedTime(string stateName, float duration) { }
        public void SetLayerWeight(int layer, float weight) { }
        public float speed { get; set; }
        public RuntimeAnimatorController runtimeAnimatorController { get; set; }
        // R4-C（C3）：正式角色表现要校验 root motion 关闭 + 确实存在 Float 参数 Speed。
        public bool applyRootMotion { get; set; }
        public AnimatorControllerParameter[] parameters { get { return new AnimatorControllerParameter[0]; } }
    }

    /// <summary>真实 Unity 的枚举值：Float=1 / Int=3 / Bool=4 / Trigger=9。</summary>
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
    public class AnimatorOverrideController : RuntimeAnimatorController { }
    public class AnimationClip : Object { public float length { get { return 0f; } } }
    public class Avatar : Object { }
    // R4-C（C3）：C2 允许角色 prefab 带 SkinnedMeshRenderer，并会读它的 sharedMesh/bones。
    public class SkinnedMeshRenderer : Renderer
    {
        public Mesh sharedMesh { get; set; }
        public Transform[] bones { get; set; }
    }

    public static class Resources
    {
        public static T Load<T>(string path) where T : Object { return null; }
        public static Object Load(string path) { return null; }
        public static Object Load(string path, Type systemTypeInstance) { return null; }
        public static T[] LoadAll<T>(string path) where T : Object { return new T[0]; }
        public static Object[] LoadAll(string path) { return new Object[0]; }
        public static T GetBuiltinResource<T>(string path) where T : Object { return null; }
        public static Object GetBuiltinResource(Type type, string path) { return null; }
        public static void UnloadUnusedAssets() { }
    }

    public class AssetBundle : Object
    {
        public T LoadAsset<T>(string name) where T : Object { return null; }
        public Object LoadAsset(string name) { return null; }
        public void Unload(bool unloadAllLoadedObjects) { }
        public static AssetBundle LoadFromFile(string path) { return null; }
        public static AssetBundleCreateRequest LoadFromFileAsync(string path) { return null; }
    }

    public class AssetBundleCreateRequest : AsyncOperation
    {
        public AssetBundle assetBundle { get { return null; } }
    }

    public class AsyncOperation
    {
        public bool isDone { get { return true; } }
        public float progress { get { return 1f; } }
        public bool allowSceneActivation { get; set; }
        public event Action<AsyncOperation> completed;
        public void completedHandler() { if (completed != null) completed(this); }
    }

    // ==================== 协程挂起点 ====================

    public class YieldInstruction { }
    public class WaitForSeconds : YieldInstruction { public WaitForSeconds(float seconds) { } }
    public class WaitForSecondsRealtime : CustomYieldInstruction { public WaitForSecondsRealtime(float seconds) { } public override bool keepWaiting { get { return false; } } }
    public class WaitForEndOfFrame : YieldInstruction { }
    public class WaitForFixedUpdate : YieldInstruction { }
    public abstract class CustomYieldInstruction : IEnumerator
    {
        public abstract bool keepWaiting { get; }
        public object Current { get { return null; } }
        public bool MoveNext() { return keepWaiting; }
        public void Reset() { }
    }
    public class WaitUntil : CustomYieldInstruction { private readonly Func<bool> _p; public WaitUntil(Func<bool> p) { _p = p; } public override bool keepWaiting { get { return !_p(); } } }
    public class WaitWhile : CustomYieldInstruction { private readonly Func<bool> _p; public WaitWhile(Func<bool> p) { _p = p; } public override bool keepWaiting { get { return _p(); } } }

    // ==================== 静态工具 ====================

    public static class Debug
    {
        public static void Log(object message) { }
        public static void Log(object message, Object context) { }
        public static void LogFormat(string format, params object[] args) { }
        public static void LogWarning(object message) { }
        public static void LogWarning(object message, Object context) { }
        public static void LogWarningFormat(string format, params object[] args) { }
        public static void LogError(object message) { }
        public static void LogError(object message, Object context) { }
        public static void LogErrorFormat(string format, params object[] args) { }
        public static void LogException(Exception e) { }
        public static void DrawLine(Vector3 a, Vector3 b) { }
        public static void DrawLine(Vector3 a, Vector3 b, Color c) { }
        public static void DrawRay(Vector3 origin, Vector3 dir, Color c) { }
    }

    public static class Mathf
    {
        public const float PI = 3.14159265f;
        public const float Infinity = float.PositiveInfinity;
        public const float NegativeInfinity = float.NegativeInfinity;
        public const float Deg2Rad = 0.0174532924f;
        public const float Rad2Deg = 57.29578f;
        public const float Epsilon = 1.4e-45f;

        public static int Max(int a, int b) { return a > b ? a : b; }
        public static float Max(float a, float b) { return a > b ? a : b; }
        public static float Max(params float[] v) { return 0f; }
        public static int Min(int a, int b) { return a < b ? a : b; }
        public static float Min(float a, float b) { return a < b ? a : b; }
        public static float Min(params float[] v) { return 0f; }
        public static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
        public static float Clamp(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }
        public static float Clamp01(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }
        public static float Abs(float v) { return v < 0 ? -v : v; }
        public static int Abs(int v) { return v < 0 ? -v : v; }
        public static float Sqrt(float v) { return (float)Math.Sqrt(v); }
        public static float Pow(float a, float b) { return (float)Math.Pow(a, b); }
        public static float Sin(float v) { return (float)Math.Sin(v); }
        public static float Cos(float v) { return (float)Math.Cos(v); }
        public static float Tan(float v) { return (float)Math.Tan(v); }
        public static float Atan2(float y, float x) { return (float)Math.Atan2(y, x); }
        public static float Atan(float v) { return (float)Math.Atan(v); }
        public static float Acos(float v) { return (float)Math.Acos(v); }
        public static float Asin(float v) { return (float)Math.Asin(v); }
        public static float Floor(float v) { return (float)Math.Floor(v); }
        public static float Ceil(float v) { return (float)Math.Ceiling(v); }
        public static float Round(float v) { return (float)Math.Round(v); }
        public static int FloorToInt(float v) { return (int)Math.Floor(v); }
        public static int CeilToInt(float v) { return (int)Math.Ceiling(v); }
        public static int RoundToInt(float v) { return (int)Math.Round(v); }
        public static float Lerp(float a, float b, float t) { return a + (b - a) * Clamp01(t); }
        public static float LerpAngle(float a, float b, float t) { return a; }
        public static float LerpUnclamped(float a, float b, float t) { return a + (b - a) * t; }
        public static float InverseLerp(float a, float b, float v) { return 0f; }
        public static float MoveTowards(float cur, float target, float maxDelta) { return target; }
        public static float MoveTowardsAngle(float cur, float target, float maxDelta) { return target; }
        public static float SmoothDamp(float cur, float target, ref float vel, float smoothTime) { return cur; }
        public static float SmoothDamp(float cur, float target, ref float vel, float smoothTime, float maxSpeed) { return cur; }
        public static float SmoothDamp(float cur, float target, ref float vel, float smoothTime, float maxSpeed, float deltaTime) { return cur; }
        public static float SmoothDampAngle(float cur, float target, ref float vel, float smoothTime) { return cur; }
        public static float SmoothStep(float from, float to, float t) { return from; }
        public static float Repeat(float t, float length) { return 0f; }
        public static float PingPong(float t, float length) { return 0f; }
        public static float Sign(float v) { return v < 0f ? -1f : 1f; }
        public static float DeltaAngle(float a, float b) { return 0f; }
        public static bool Approximately(float a, float b) { return Math.Abs(a - b) < 1e-5f; }
        public static float Log(float v) { return (float)Math.Log(v); }
        public static float Log10(float v) { return (float)Math.Log10(v); }
        public static float Exp(float v) { return (float)Math.Exp(v); }
        public static bool IsPowerOfTwo(int v) { return true; }
        public static int NextPowerOfTwo(int v) { return v; }
    }

    public static class Time
    {
        public static float time { get { return 0f; } }
        public static float deltaTime { get { return 0f; } }
        public static float fixedDeltaTime { get { return 0.02f; } set { } }
        public static float fixedTime { get { return 0f; } }
        public static float unscaledTime { get { return 0f; } }
        public static float unscaledDeltaTime { get { return 0f; } }
        public static float realtimeSinceStartup { get { return 0f; } }
        public static float timeScale { get { return 1f; } set { } }
        public static int frameCount { get { return 0; } }
        public static float smoothDeltaTime { get { return 0f; } }
    }

    public static class Application
    {
        public static bool runInBackground { get; set; }
        public static bool isEditor { get { return false; } }
        public static bool isPlaying { get { return true; } }
        public static string dataPath { get { return ""; } }
        public static string persistentDataPath { get { return ""; } }
        public static string streamingAssetsPath { get { return ""; } }
        public static string temporaryCachePath { get { return ""; } }
        public static int targetFrameRate { get; set; }
        public static void Quit() { }

        // R3-B：新链以退出码区分「结果确认后正常退出」与「明确失败退出」。
        public static void Quit(int exitCode) { }
    }

    public static class Screen
    {
        public static int width { get { return 1920; } }
        public static int height { get { return 1080; } }
        public static bool fullScreen { get; set; }
        public static int sleepTimeout { get; set; }
    }

    public static class Random
    {
        public static int Range(int min, int max) { return min; }
        public static float Range(float min, float max) { return min; }
        public static float value { get { return 0f; } }
        public static Vector3 insideUnitSphere { get { return Vector3.zero; } }
        public static Vector2 insideUnitCircle { get { return Vector2.zero; } }
        public static Vector3 onUnitSphere { get { return Vector3.forward; } }
        public static Quaternion rotation { get { return Quaternion.identity; } }
        public static void InitState(int seed) { }
        public static int seed { get; set; }
    }

    public static class Input
    {
        public static Vector3 mousePosition { get { return Vector3.zero; } }
        public static bool GetKey(KeyCode k) { return false; }
        public static bool GetKeyDown(KeyCode k) { return false; }
        public static bool GetKeyUp(KeyCode k) { return false; }
        public static bool GetKey(string name) { return false; }
        public static bool GetKeyDown(string name) { return false; }
        public static bool GetMouseButton(int b) { return false; }
        public static bool GetMouseButtonDown(int b) { return false; }
        public static bool GetMouseButtonUp(int b) { return false; }
        public static float GetAxis(string name) { return 0f; }
        public static float GetAxisRaw(string name) { return 0f; }
        public static bool GetButton(string name) { return false; }
        public static bool GetButtonDown(string name) { return false; }
        public static bool GetButtonUp(string name) { return false; }
        public static bool anyKeyDown { get { return false; } }
        public static int touchCount { get { return 0; } }
        public static Touch GetTouch(int i) { return new Touch(); }
        public static Touch[] touches { get { return new Touch[0]; } }
    }

    /// <summary>
    /// R6-C：IMGUI 的最小替身（只含 PMUnityCombatHud 真正用到的两个真实重载）。
    ///
    /// 真实签名（Unity 2019.4）：<c>public static void Box(Rect position, string text)</c> /
    /// <c>public static void Label(Rect position, string text)</c>。
    /// 本门禁只能保证调用形状正确，**不**验证绘制语义（那必须回到 Unity）。
    /// </summary>
    public static class GUI
    {
        public static void Box(Rect position, string text) { }
        public static void Label(Rect position, string text) { }
    }

    public struct Touch
    {
        public int fingerId { get { return 0; } }
        public Vector2 position { get { return Vector2.zero; } }
        public Vector2 deltaPosition { get { return Vector2.zero; } }
        public TouchPhase phase { get { return TouchPhase.Began; } }
    }

    public enum TouchPhase { Began, Moved, Stationary, Ended, Canceled }

    public enum KeyCode
    {
        None, Backspace, Delete, Tab, Return, Escape, Space,
        A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
        Alpha0, Alpha1, Alpha2, Alpha3, Alpha4, Alpha5, Alpha6, Alpha7, Alpha8, Alpha9,
        UpArrow, DownArrow, LeftArrow, RightArrow,
        Mouse0, Mouse1, Mouse2, LeftShift, RightShift, LeftControl, RightControl,
        LeftAlt, RightAlt, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12
    }

    public static class JsonUtility
    {
        public static string ToJson(object obj) { return "{}"; }
        public static string ToJson(object obj, bool prettyPrint) { return "{}"; }
        public static T FromJson<T>(string json) { return default(T); }
        public static void FromJsonOverwrite(string json, object obj) { }
    }

    public static class PlayerPrefs
    {
        public static void SetInt(string key, int value) { }
        // 真实 Unity 的第二个参数有默认值（= 0），所以调用方可以只传一个参数。
        public static int GetInt(string key, int defaultValue = 0) { return defaultValue; }
        public static void SetFloat(string key, float value) { }
        public static float GetFloat(string key, float defaultValue = 0f) { return defaultValue; }
        public static void SetString(string key, string value) { }
        public static string GetString(string key, string defaultValue = "") { return defaultValue; }
        public static bool HasKey(string key) { return false; }
        public static void DeleteKey(string key) { }
        public static void DeleteAll() { }
        public static void Save() { }
    }

    // ==================== 生命周期 / 特性 ====================

    public enum RuntimeInitializeLoadType
    {
        AfterSceneLoad, BeforeSceneLoad, AfterAssembliesLoaded, BeforeSplashScreen, SubsystemRegistration
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute
    {
        public RuntimeInitializeOnLoadMethodAttribute() { }
        public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType type) { }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class HeaderAttribute : Attribute { public HeaderAttribute(string header) { } }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeFieldAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class FormerlySerializedAsAttribute : Attribute { public FormerlySerializedAsAttribute(string oldName) { } }

    // 真实 Unity 允许 HideInInspector 用于**属性**（项目里确有 `[HideInInspector] public int Id { get; private set; }`
    // 且能在 Unity 中编译）。桩件最初只声明 Field，产生了一批假阳性。
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class HideInInspectorAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class RangeAttribute : Attribute { public RangeAttribute(float min, float max) { } }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class TooltipAttribute : Attribute { public TooltipAttribute(string tooltip) { } }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SpaceAttribute : Attribute { public SpaceAttribute() { } public SpaceAttribute(float height) { } }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class TextAreaAttribute : Attribute { public TextAreaAttribute() { } public TextAreaAttribute(int min, int max) { } }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class MultilineAttribute : Attribute { public MultilineAttribute(int lines) { } }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class RequireComponentAttribute : Attribute
    {
        public RequireComponentAttribute(Type t) { }
        public RequireComponentAttribute(Type t, Type t2) { }
        public RequireComponentAttribute(Type t, Type t2, Type t3) { }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class AddComponentMenuAttribute : Attribute { public AddComponentMenuAttribute(string menu) { } }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class ExecuteInEditModeAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class DisallowMultipleComponentAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.All)]
    public sealed class ContextMenuAttribute : Attribute { public ContextMenuAttribute(string itemName) { } }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class CreateAssetMenuAttribute : Attribute
    {
        public string fileName { get; set; }
        public string menuName { get; set; }
        public int order { get; set; }
    }

    public class ScriptableObject : Object
    {
        public static T CreateInstance<T>() where T : ScriptableObject { return null; }
    }

    // ==================== 杂项 ====================

    public class TextAsset : Object { public string text { get { return ""; } } public byte[] bytes { get { return new byte[0]; } } }

    // 真实 Unity 把这两个枚举放在 UnityEngine 根命名空间（不是 UnityEngine.UI）。
    public enum FontStyle { Normal, Bold, Italic, BoldAndItalic }

    public enum TextAnchor
    {
        UpperLeft, UpperCenter, UpperRight,
        MiddleLeft, MiddleCenter, MiddleRight,
        LowerLeft, LowerCenter, LowerRight
    }

    public enum TextClipping { Overflow, Clip }
    public enum HorizontalWrapMode { Wrap, Overflow }
    public enum VerticalWrapMode { Truncate, Overflow }

    public static class Terrain { }

    public class Motion : Object { }

    public class RuntimePlatform { }
}

namespace UnityEngine.SceneManagement
{
    public struct Scene
    {
        public string name { get { return ""; } }
        public string path { get { return ""; } }
        public int buildIndex { get { return 0; } }
        public bool isLoaded { get { return true; } }
        public int handle { get { return 0; } }
        public bool IsValid() { return true; }
        public GameObject[] GetRootGameObjects() { return new GameObject[0]; }
    }

    /// <summary>
    /// R4-B（B3）：创建本地物理场景的参数（真实签名：ctor 取 <see cref="LocalPhysicsMode"/>，
    /// 属性名 <c>localPhysicsMode</c>）。宿主用它建 <c>LocalPhysicsMode.Physics3D</c> 的隔离世界。
    /// </summary>
    public struct CreateSceneParameters
    {
        public CreateSceneParameters(LocalPhysicsMode physicsMode) { localPhysicsMode = physicsMode; }

        public LocalPhysicsMode localPhysicsMode { get; set; }
    }

    /// <summary>R4-B（B3）：场景是否自带本地 2D/3D 物理世界（真实枚举值 None=0 / Physics2D=1 / Physics3D=2）。</summary>
    public enum LocalPhysicsMode { None = 0, Physics2D = 1, Physics3D = 2 }

    public static class SceneManager
    {
        public static Scene GetActiveScene() { return new Scene(); }
        public static Scene GetSceneAt(int index) { return new Scene(); }
        public static int sceneCount { get { return 1; } }

        /// <summary>R4-B（B3）：建本地物理场景的唯一入口（真实签名为返回 Scene）。</summary>
        public static Scene CreateScene(string sceneName) { return new Scene(); }

        public static Scene CreateScene(string sceneName, CreateSceneParameters parameters) { return new Scene(); }

        public static bool SetActiveScene(Scene scene) { return true; }

        public static void MoveGameObjectToScene(GameObject go, Scene scene) { }

        public static void LoadScene(string sceneName) { }
        public static void LoadScene(int sceneIndex) { }
        public static void LoadScene(string sceneName, LoadSceneMode mode) { }
        public static AsyncOperation LoadSceneAsync(string sceneName) { return null; }
        public static AsyncOperation LoadSceneAsync(int sceneIndex) { return null; }
        public static AsyncOperation LoadSceneAsync(string sceneName, LoadSceneMode mode) { return null; }
        public static AsyncOperation LoadSceneAsync(int sceneIndex, LoadSceneMode mode) { return null; }

        /// <summary>
        /// 真实返回 <c>AsyncOperation</c>；替身返回 void——唯一调用点是语句（忽略返回值），
        /// 这样桩件不必再引入 AsyncOperation 的完整形状。
        /// </summary>
        public static void UnloadSceneAsync(Scene scene) { }
    }

    public enum LoadSceneMode { Single, Additive }
}

namespace UnityEngine.UI
{
    public abstract class Graphic : Behaviour
    {
        public Color color { get; set; }
        public bool raycastTarget { get; set; }
        public RectTransform rectTransform { get { return null; } }
        public void CrossFadeAlpha(float alpha, float duration, bool ignoreTimeScale) { }
        public void CrossFadeColor(Color targetColor, float duration, bool ignoreTimeScale, bool useAlpha) { }
    }

    public class Text : Graphic
    {
        public string text { get; set; }
        public int fontSize { get; set; }
        public FontStyle fontStyle { get; set; }
        public TextAnchor alignment { get; set; }
        public Font font { get; set; }
        public float lineSpacing { get; set; }
        public bool resizeTextForBestFit { get; set; }
        public bool supportRichText { get; set; }
    }

    public class Image : Graphic
    {
        public Sprite sprite { get; set; }
        public float fillAmount { get; set; }
        public bool preserveAspect { get; set; }
        public Type type { get; set; }
        public bool fillCenter { get; set; }
        public new enum Type { Simple, Sliced, Tiled, Filled }
    }

    public class RawImage : Graphic { public Texture texture { get; set; } }

    public class Slider : Behaviour
    {
        public float value { get; set; }
        public float minValue { get; set; }
        public float maxValue { get; set; }
        public float normalizedValue { get; set; }
        public SliderEvent onValueChanged { get { return new SliderEvent(); } }
        public class SliderEvent { public void AddListener(Action<float> call) { } public void RemoveAllListeners() { } }
    }

    public class Button : Behaviour
    {
        public ButtonClickedEvent onClick { get { return new ButtonClickedEvent(); } }
        public bool interactable { get; set; }
        public class ButtonClickedEvent
        {
            public void AddListener(Action call) { }
            public void RemoveListener(Action call) { }
            public void RemoveAllListeners() { }
            public void Invoke() { }
        }
    }

    public class Toggle : Behaviour
    {
        public bool isOn { get; set; }
        public ToggleEvent onValueChanged { get { return new ToggleEvent(); } }
        public class ToggleEvent { public void AddListener(Action<bool> call) { } public void RemoveAllListeners() { } }
    }

    public class InputField : Behaviour
    {
        public string text { get; set; }
        public OnChangeEvent onValueChanged { get { return new OnChangeEvent(); } }
        public OnSubmitEvent onEndEdit { get { return new OnSubmitEvent(); } }
        public class OnChangeEvent { public void AddListener(Action<string> call) { } public void RemoveAllListeners() { } }
        public class OnSubmitEvent { public void AddListener(Action<string> call) { } public void RemoveAllListeners() { } }
    }

    public class Scrollbar : Behaviour { public float value { get; set; } public float size { get; set; } }

    public class ScrollRect : Behaviour
    {
        public Vector2 normalizedPosition { get; set; }
        public float verticalNormalizedPosition { get; set; }
        public float horizontalNormalizedPosition { get; set; }
        public ScrollRectEvent onValueChanged { get { return new ScrollRectEvent(); } }
        public class ScrollRectEvent { public void AddListener(Action<Vector2> call) { } }
    }

    public class Dropdown : Behaviour
    {
        public int value { get; set; }
        public List<OptionData> options { get; set; }
        public DropdownEvent onValueChanged { get { return new DropdownEvent(); } }
        public class OptionData { public string text; public Sprite image; }
        public class DropdownEvent { public void AddListener(Action<int> call) { } }
    }

    public class Canvas : Behaviour
    {
        public RenderMode renderMode { get; set; }
        public int sortingOrder { get; set; }
        public Camera worldCamera { get; set; }
    }

    public enum RenderMode { ScreenSpaceOverlay, ScreenSpaceCamera, WorldSpace }

    public class CanvasScaler : Behaviour
    {
        public ScaleMode uiScaleMode { get; set; }
        public ScreenMatchMode screenMatchMode { get; set; }
        public Vector2 referenceResolution { get; set; }
        public float matchWidthOrHeight { get; set; }
        public float referencePixelsPerUnit { get; set; }
        public float scaleFactor { get; set; }
        public enum ScaleMode { ConstantPixelSize, ScaleWithScreenSize, ConstantPhysicalSize }
        public enum ScreenMatchMode { MatchWidthOrHeight, Expand, Shrink }
    }

    public class CanvasGroup : Behaviour
    {
        public float alpha { get; set; }
        public bool interactable { get; set; }
        public bool blocksRaycasts { get; set; }
    }

    public class GraphicRaycaster : Behaviour { public bool ignoreReversedGraphics { get; set; } }
    public class LayoutElement : Behaviour { public float minWidth { get; set; } public float preferredWidth { get; set; } public float minHeight { get; set; } public float preferredHeight { get; set; } }
    public class Mask : Behaviour { public bool showMaskGraphic { get; set; } }
    public class RectMask2D : Behaviour { }
    public class Outline : Behaviour { public Color effectColor { get; set; } public Vector2 effectDistance { get; set; } }
    public class Shadow : Behaviour { public Color effectColor { get; set; } public Vector2 effectDistance { get; set; } }
}

namespace UnityEngine.Events
{
    public class UnityEvent
    {
        public void AddListener(Action call) { }
        public void RemoveListener(Action call) { }
        public void RemoveAllListeners() { }
        public void Invoke() { }
    }

    public class UnityEvent<T>
    {
        public void AddListener(Action<T> call) { }
        public void RemoveAllListeners() { }
        public void Invoke(T arg) { }
    }
}

namespace UnityEngine.AI
{
    public class NavMeshAgent : Behaviour
    {
        public float speed { get; set; }
        public float angularSpeed { get; set; }
        public float acceleration { get; set; }
        public bool isStopped { get; set; }
        public float stoppingDistance { get; set; }
        public Vector3 destination { get; set; }
        public Vector3 velocity { get { return Vector3.zero; } }
        public float remainingDistance { get { return 0f; } }
        public bool hasPath { get { return false; } }
        public void SetDestination(Vector3 target) { }
        public void ResetPath() { }
        public void Warp(Vector3 pos) { }
    }
}

namespace UnityEngine.Serialization { }

namespace UnityEngine.SocialPlatforms { }

namespace UnityEngine.UIElements { }

namespace UnityEditor
{
    public static class EditorUtility
    {
        public static void SetDirty(Object target) { }
        public static bool DisplayDialog(string title, string message, string ok) { return false; }
    }

    public static class AssetDatabase
    {
        public static void Refresh() { }
        public static void SaveAssets() { }
        public static string GetAssetPath(Object obj) { return ""; }
        public static T LoadAssetAtPath<T>(string path) where T : UnityEngine.Object { return null; }
    }

    public enum BuildTarget { StandaloneWindows64, StandaloneWindows, StandaloneLinux64 }

    [Flags]
    public enum BuildOptions { None = 0, Development = 1, EnableHeadlessMode = 2 }

    public class BuildPlayerOptions
    {
        public string[] scenes { get; set; }
        public string locationPathName { get; set; }
        public BuildTarget target { get; set; }
        public BuildOptions options { get; set; }
    }

    public static class BuildPipeline
    {
        public static BuildReport BuildPlayer(BuildPlayerOptions options) { return null; }
    }

    public class BuildReport { public BuildSummary summary { get { return new BuildSummary(); } } }
    public struct BuildSummary { public BuildResult result { get { return BuildResult.Succeeded; } } }
    public enum BuildResult { Unknown, Succeeded, Failed, Cancelled }
}

// ============================================================================
//  遗留类型替身
// ============================================================================

/// <summary>
/// 斗地主的 TCPSocket：**遗留重复实现**（真正在用的是 Scripts/Server 下的 TCPSocketManger）。
///
/// <para>
/// 一方代码只用到它的 5 个静态成员（见下）。编真实文件会把它那套卡牌网络小框架
/// （MessageController / MessageTypes / StaticValue / D / RequestManager）全部拉进来，
/// 与本次改造无关，所以在 csproj 里排除了它，改在这里提供最小替身。
/// </para>
/// </summary>
public class TCPSocket : UnityEngine.MonoBehaviour
{
    public static TCPSocket Instance { get { return null; } }
    public static bool 是否单机 = false;
    public static bool 是否创建 = false;
    public static List<string> 被选择的英雄 = new List<string>();
    public static List<string> 玩家名 = new List<string>();

    // 真实签名：public bool Send(OldRequestCode requestCode, OldActionCode actionCode, string data)
    public bool Send(OldRequestCode requestCode, OldActionCode actionCode, string data) { return false; }
}

/// <summary>
/// 第三方摇杆插件 EasyJoystick 的最小替身。
/// 一方代码只订阅了它的两个静态事件（TouchLogic.cs:74-75），委托参数是真实的 MovingJoystick
/// （那个类自包含，已作为真实文件编入）。真实签名见 EasyJoystick.cs:102/106。
/// </summary>
public delegate void JoystickMoveHandler(MovingJoystick move);

public delegate void JoystickMoveEndHandler(MovingJoystick move);

// 不能是 static class：MovingJoystick.cs:45 有 `public EasyJoystick joystick;` 字段。
// 真实 EasyJoystick 也是 MonoBehaviour（实例类型 + 静态事件并存）。
public class EasyJoystick : UnityEngine.MonoBehaviour
{
    public static event JoystickMoveHandler On_JoystickMove;
    public static event JoystickMoveEndHandler On_JoystickMoveEnd;

    // 显式提供触发入口，避免"事件从未被赋值"的告警；桩件里无实际行为。
    public static void RaiseMove(MovingJoystick m) { if (On_JoystickMove != null) On_JoystickMove(m); }
    public static void RaiseMoveEnd(MovingJoystick m) { if (On_JoystickMoveEnd != null) On_JoystickMoveEnd(m); }
}
