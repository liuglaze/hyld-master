// PMDsHostCheck 的最小替身（**不是**真实 Unity，也不冒充真实 Unity）。
//
// 这些替身只为「PMDsHost 这个 wrapper 的控制流」提供形状与被观察点：
//   · UnityEngine 侧只保留 wrapper 真正触达的成员：GameObject / MonoBehaviour / Application / Time / Object；
//   · Logging / PMNet 侧用最小替身，避免把 PMNet 核心与真实 Loging.cs 的整条依赖链拉进本门禁；
//   · PMDsSessionHost 是**受控替身**：它记录 StartCalls / PumpCalls / DisposeCalls，
//     于是「wrapper 是否恰好只启动一次、是否每帧 Pump、是否幂等 Dispose」才可断言。
//
// 边界（不要误读本门禁的结论）：
//   · 本文件**不保证**与真实 Unity 签名/语义一致；
//   · 本文件**不**编真实 PMDsSessionHost，因此不证明真实会话装配；
//   · 真实编译面由 Tools/PMUnityGlueCheck 与 Tools/PMClientCheck 用真实源码 + 各自桩件覆盖。

namespace UnityEngine
{
    /// <summary>UnityEngine.Object 的最小替身（wrapper 只用到 DontDestroyOnLoad）。</summary>
    public class Object
    {
        /// <summary>供测试观察：DontDestroyOnLoad 被调用次数（包装对象的常驻化）。</summary>
        public static int DontDestroyOnLoadCalls;

        public string name;

        public static void DontDestroyOnLoad(Object target)
        {
            DontDestroyOnLoadCalls++;
        }
    }

    public class Component : Object
    {
    }

    public class Behaviour : Component
    {
    }

    public class MonoBehaviour : Behaviour
    {
    }

    public class GameObject : Object
    {
        /// <summary>供测试观察：创建过的 GameObject 个数。</summary>
        public static int CreatedCount;

        private readonly System.Collections.Generic.List<Component> _components =
            new System.Collections.Generic.List<Component>();

        public GameObject()
        {
            CreatedCount++;
        }

        public GameObject(string goName)
            : this()
        {
            name = goName;
        }

        public T AddComponent<T>() where T : Component, new()
        {
            T component = new T();
            _components.Add(component);
            return component;
        }
    }

    public static class Application
    {
        public static bool runInBackground { get; set; }

        /// <summary>供测试观察：Quit 被调用次数。</summary>
        public static int QuitCalls;

        /// <summary>供测试观察：最近一次 Quit 的退出码（未调用过时为 int.MinValue）。</summary>
        public static int LastQuitCode = int.MinValue;

        public static void Quit(int exitCode)
        {
            QuitCalls++;
            LastQuitCode = exitCode;
        }
    }

    public static class Time
    {
        private static float _realtimeSinceStartup;

        public static float realtimeSinceStartup { get { return _realtimeSinceStartup; } }

        /// <summary>测试专用：设置读取值。</summary>
        public static void SetForTest(float value)
        {
            _realtimeSinceStartup = value;
        }
    }
}

namespace Logging
{
    /// <summary>HYLDDebug 的最小替身（wrapper 只用到 Log/LogWarning/LogError/FlushTrace）。</summary>
    public static class HYLDDebug
    {
        public static readonly System.Collections.Generic.List<string> Lines =
            new System.Collections.Generic.List<string>();

        /// <summary>供测试观察：FlushTrace 被调用次数。</summary>
        public static int FlushTraceCalls;

        public static void Log(string message)
        {
            Lines.Add("LOG " + message);
        }

        public static void LogWarning(string message)
        {
            Lines.Add("WARN " + message);
        }

        public static void LogError(string message)
        {
            Lines.Add("ERROR " + message);
        }

        public static void FlushTrace()
        {
            FlushTraceCalls++;
        }
    }
}

namespace PMNet
{
    /// <summary>
    /// PMNetLaunchOptions 的最小替身：只保留 wrapper 读到的字段。
    /// （真实类型由 PMNetLaunchCheck / PMUnityGlueCheck / PMClientCheck 覆盖，本门禁只测 wrapper 控制流。）
    /// </summary>
    public sealed class PMNetLaunchOptions
    {
        public string BootstrapPath;
        public string DsId;
        public string MatchId;
        public int ListenPort;
    }
}

namespace PMNet.Unity
{
    /// <summary>
    /// PMDsSessionHost 的**受控替身**（真实实现由 PMUnityGlueCheck / PMClientCheck 编真实源码覆盖）。
    ///
    /// 它是本门禁的主要观察点：wrapper 只能通过 Start/Pump/Dispose/ExitRequested 触达它。
    /// </summary>
    public sealed class PMDsSessionHost : System.IDisposable
    {
        /// <summary>供测试观察：Start 被调用次数（断言「缺失 bootstrap 时不得为 1」）。</summary>
        public static int StartCalls;

        /// <summary>故障注入：下一次 Start 返回 null（模拟真实宿主的启动失败路径）。</summary>
        public static bool FailNextStart;
        public static bool ThrowOnStart, ThrowOnPump, ThrowOnDispose;

        /// <summary>最近一次成功启动的替身会话（供测试取 PumpCalls / DisposeCalls）。</summary>
        public static PMDsSessionHost LastStarted;

        public int PumpCalls;
        public int DisposeCalls;

        /// <summary>退出请求回调；wrapper 必须挂上它，并在调用后停止 Pump。</summary>
        public System.Action<int> ExitRequested;

        public static PMDsSessionHost Start(PMNet.PMNetLaunchOptions options, out string error)
        {
            StartCalls++;
            if (ThrowOnStart) throw new System.InvalidOperationException("start injection");

            if (FailNextStart)
            {
                error = "<stub> 注入的启动故障";
                return null;
            }

            error = null;
            LastStarted = new PMDsSessionHost();
            return LastStarted;
        }

        public void Pump()
        {
            PumpCalls++;
            if (ThrowOnPump) throw new System.InvalidOperationException("pump injection");
        }

        public void Dispose()
        {
            DisposeCalls++;
            if (ThrowOnDispose) throw new System.InvalidOperationException("dispose injection");
        }
    }
}
