// ============================================================================
//  PMCombatHudTest 的 UnityEngine / PMNet 替身（**功能性替身**，不是只保证形状的桩）
// ============================================================================
//
//  本文件存在的理由
//  ----------------
//  T-LOOP3 的行为（「居中结算弹窗只在可信结论 + 同帧 ACK 后出现」「返回大厅按钮经会话身份令牌
//  回调」「退场后按钮变只读、重复点击不再回调」「无结论时不得弹窗」）**全是状态机性质**：
//  它们失败的样子是"提前弹了 / 弹不出来 / 二次退场 / 陈旧回调关了新会话"，编译期完全看不出来。
//  因此本工程把 PMUnityCombatHud.cs（**真实源码，不复制、不快照**）编进 net8，用**可执行的
//  功能性替身**驱动它的 OnGUI：替身真的建组件实例、真的记录每次 IMGUI 调用、真的可以注入点击。
//
//  与另外三个 Unity 面的分工（都不是重复劳动）
//  ------------------------------------------
//    · Tools/PMR4UnityCheck（真实 Unity 2019 **Editor** DLL）→ 校验 GUI.Button(Rect,string,GUIStyle)
//      等真实签名与可用性；
//    · Tools/PMNetUnityPlayerCheck（真实 **Player** DLL）→ 校验 Player 面无编辑器专有 API 分叉；
//    · Tools/PMUnityGlueCheck / Tools/PMClientCheck（手写形状桩）→ 校验两个宿主整体可编译；
//    · 本工程 → 只负责**行为**（谁在什么时候能弹、按钮点了会怎样），不假装验真实渲染/字体/皮肤。
//
//  刻意简化（必须知道，否则会把替身的默认值当成真实语义）
//  ------------------------------------------------------
//    · <c>Object.Destroy/DontDestroyOnLoad</c> 只置标志位，不做任何真实销毁/场景管理；
//    · <c>GameObject.AddComponent&lt;T&gt;</c> 用 <c>Activator.CreateInstance</c> 造实例并回填
//      <c>gameObject</c>（真实 Unity 同语义；这是能跑 HUD 行为的前提）；
//    · <c>GUI</c> 只记录"画了什么"并允许注入一次点击，不模拟版面/字体/皮肤渲染；
//    · <c>Screen</c> 可写（便于测 0/NaN 兜底），真实 Unity 是只读属性；
//    · <c>PMNet.PMNetRuntime</c> 只提供 HUD 用到的 <c>IsDedicatedServer</c> 一位。
// ============================================================================

using System;
using System.Collections.Generic;

namespace UnityEngine
{
    /// <summary>Unity <c>Object</c> 的最小功能替身（Destroy 只置标志）。</summary>
    public class Object
    {
        public string name;
        public bool Destroyed;

        public static void DontDestroyOnLoad(Object target)
        {
            // 真实语义是「跨场景保留」；替身没有场景，因此只保证调用形状（不记任何状态）。
        }

        public static void Destroy(Object obj)
        {
            if (obj == null) { return; }

            obj.Destroyed = true;

            GameObject go = obj as GameObject;
            if (go != null) { go.Alive = false; }
        }

        public static void DestroyImmediate(Object obj) { Destroy(obj); }
    }

    /// <summary>组件替身：真的持有 <c>gameObject</c> 反向引用（HUD 的 Dispose 依赖它）。</summary>
    public class Component : Object
    {
        public GameObject gameObject { get; internal set; }
    }

    public class Behaviour : Component
    {
        public bool enabled { get; set; }
        public bool isActiveAndEnabled { get { return enabled; } }
    }

    /// <summary>MonoBehaviour 替身（刻意不声明 Awake/Update/OnGUI —— 它们是魔术方法）。</summary>
    public class MonoBehaviour : Behaviour
    {
    }

    /// <summary>GameObject 替身：<c>AddComponent&lt;T&gt;</c> 真的造实例。</summary>
    public class GameObject : Object
    {
        private readonly List<Component> _components = new List<Component>();

        public bool Alive = true;

        public GameObject() { name = string.Empty; }

        public GameObject(string n) { name = n; }

        public bool activeSelf { get { return Alive; } }

        public bool activeInHierarchy { get { return Alive; } }

        public T AddComponent<T>() where T : Component
        {
            T component = (T)Activator.CreateInstance(typeof(T));
            component.gameObject = this;
            _components.Add(component);
            return component;
        }

        public T GetComponent<T>() where T : Component
        {
            for (int i = 0; i < _components.Count; i++)
            {
                T typed = _components[i] as T;
                if (typed != null) { return typed; }
            }

            return null;
        }

        public int ComponentCount { get { return _components.Count; } }

        public void SetActive(bool value) { Alive = value; }
    }

    /// <summary>Unity <c>Rect</c>（<c>public Rect(float,float,float,float)</c>）。</summary>
    public struct Rect
    {
        public float x;
        public float y;
        public float width;
        public float height;

        public Rect(float x, float y, float width, float height)
        {
            this.x = x;
            this.y = y;
            this.width = width;
            this.height = height;
        }
    }

    /// <summary>Unity <c>Color</c>（HUD 只用到 3 分量构造）。</summary>
    public struct Color
    {
        public float r;
        public float g;
        public float b;
        public float a;

        public Color(float r, float g, float b)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = 1f;
        }

        public Color(float r, float g, float b, float a)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }
    }

    /// <summary>可写版本的 <c>Screen</c>（便于测 0/NaN 的版面兜底；真实 Unity 为只读）。</summary>
    public static class Screen
    {
        public static int width = 1920;
        public static int height = 1080;
    }

    // 真实 Unity 把这两个枚举放在 UnityEngine 根命名空间。
    public enum FontStyle { Normal, Bold, Italic, BoldAndItalic }

    public enum TextAnchor
    {
        UpperLeft, UpperCenter, UpperRight,
        MiddleLeft, MiddleCenter, MiddleRight,
        LowerLeft, LowerCenter, LowerRight
    }

    public class GUIStyleState
    {
        public Color textColor { get; set; }
    }

    public class GUIStyle
    {
        public GUIStyle() { normal = new GUIStyleState(); }

        public GUIStyle(GUIStyle other)
        {
            normal = new GUIStyleState();
            if (other == null) { return; }

            fontSize = other.fontSize;
            fontStyle = other.fontStyle;
            alignment = other.alignment;
            wordWrap = other.wordWrap;
        }

        public int fontSize { get; set; }
        public FontStyle fontStyle { get; set; }
        public TextAnchor alignment { get; set; }
        public bool wordWrap { get; set; }
        public GUIStyleState normal { get; set; }
    }

    public class GUISkin
    {
        public GUIStyle label { get; set; }
        public GUIStyle box { get; set; }
        public GUIStyle button { get; set; }
    }

    /// <summary>
    /// IMGUI 替身（**记录型**）：逐次记录 Box/Label/Button 的调用与文案，
    /// 并允许测试注入**一次**点击（<see cref="ClickNextButton"/>）。
    ///
    /// 为什么必须记录而不是空实现：T-LOOP3 的判据是"按钮到底有没有被画成可点的 Button"
    /// （不可点时必须画成 Box）与"点了之后回调了几次"，只有记录才能钉住这两件事。
    /// </summary>
    public static class GUI
    {
        /// <summary>本帧各 Box 的文案（角标标题、弹窗标题、不可点按钮的只读文字）。</summary>
        public static readonly List<string> BoxTexts = new List<string>();

        /// <summary>本帧各 Label 的文案（角标正文、弹窗大字、弹窗明细）。</summary>
        public static readonly List<string> LabelTexts = new List<string>();

        /// <summary>本帧画出的 Box 个数（角标标题、弹窗标题、不可点按钮的只读文字）。</summary>
        public static int BoxesDrawn;

        /// <summary>本帧画出的 Label 个数（角标正文、弹窗大字、弹窗明细）。</summary>
        public static int LabelsDrawn;

        /// <summary>本帧画出的 Button 个数（应为 0 或 1）。</summary>
        public static int ButtonsDrawn;

        /// <summary>最近一次 Button 的文案（无 Button 时为 null）。</summary>
        public static string LastButtonText;

        /// <summary>最近一次 Button 的矩形（用于断言"按钮在屏幕正中"的版面关系）。</summary>
        public static Rect LastButtonRect;

        /// <summary>注入一次点击：置真后，下一次 <see cref="Button"/> 返回 true 并自动复位。</summary>
        public static bool ClickNextButton;

        private static GUISkin _skin = CreateSkin();

        public static GUISkin skin
        {
            get { return _skin; }
            set { _skin = value; }
        }

        private static GUISkin CreateSkin()
        {
            GUISkin skin = new GUISkin();
            skin.label = new GUIStyle();
            skin.box = new GUIStyle();
            skin.button = new GUIStyle();
            return skin;
        }

        /// <summary>一帧开始：清掉上一帧的记录（**不**清 <see cref="ClickNextButton"/>）。</summary>
        public static void ResetFrame()
        {
            BoxTexts.Clear();
            LabelTexts.Clear();
            BoxesDrawn = 0;
            LabelsDrawn = 0;
            ButtonsDrawn = 0;
            LastButtonText = null;
            LastButtonRect = new Rect();
        }

        /// <summary>当前是否还有未消费的注入点击（用于断言"点击确实被消费/确实没被消费"）。</summary>
        public static bool HasPendingClick { get { return ClickNextButton; } }

        public static void Box(Rect position, string text)
        {
            BoxesDrawn++;
            BoxTexts.Add(text == null ? string.Empty : text);
        }

        public static void Box(Rect position, string text, GUIStyle style) { Box(position, text); }

        public static void Label(Rect position, string text)
        {
            LabelsDrawn++;
            LabelTexts.Add(text == null ? string.Empty : text);
        }

        public static void Label(Rect position, string text, GUIStyle style) { Label(position, text); }

        public static bool Button(Rect position, string text) { return Button(position, text, null); }

        public static bool Button(Rect position, string text, GUIStyle style)
        {
            ButtonsDrawn++;
            LastButtonText = text == null ? string.Empty : text;
            LastButtonRect = position;

            if (ClickNextButton)
            {
                ClickNextButton = false;
                return true;
            }

            return false;
        }
    }
}

namespace PMNet
{
    /// <summary>
    /// <c>PMNet.PMNetRuntime</c> 的最小替身（只提供 HUD 用到的身份位）。
    ///
    /// 真实类由 Client/Assets/Scripts/PMNet 提供，并由 PMClientCheck / PMR4UnityCheck /
    /// PMNetUnityPlayerCheck 真实编译；本工程只测 HUD 行为，不该拉进整个网络核心，
    /// 因此这里只复刻"DS 禁建 HUD"判据用到的那一位。
    /// </summary>
    public static class PMNetRuntime
    {
        public static bool IsDedicatedServer { get; set; }
    }
}
