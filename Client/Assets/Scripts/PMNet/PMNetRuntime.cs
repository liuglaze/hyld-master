using System;

namespace PMNet
{
    /// <summary>
    /// 进程级网络运行时状态（对应 UE 的 GetNetMode() / GIsServer 这套全局判定）。
    ///
    /// 为什么需要它：Unity 2019.4 没有 UNITY_SERVER 宏，客户端构建与 DS 构建是同一个二进制
    /// （见计划 §2.8 的硬约束），因此「当前进程是服务端还是客户端」不是编译期常量，
    /// 而是启动期从命令行解析出来、此后全局只读的状态。所有需要用角色区分的代码
    /// 都应通过这里判定，禁止改用「是否编辑器」「场景名」之类的间接条件。
    ///
    /// 本类不引用 UnityEngine，因此可被 Tools/PMNetLaunchCheck 直接跑测。
    /// </summary>
    public static class PMNetRuntime
    {
        private static PMNetLaunchOptions _options;
        private static PMNetMode _mode = PMNetMode.Standalone;

        /// <summary>是否已完成初始化（未初始化时 Mode 恒为 Standalone）。</summary>
        public static bool Initialized
        {
            get { return _options != null; }
        }

        /// <summary>当前进程的启动参数。未初始化时为 null。</summary>
        public static PMNetLaunchOptions Options
        {
            get { return _options; }
        }

        /// <summary>当前进程的网络模式。</summary>
        public static PMNetMode Mode
        {
            get { return _mode; }
        }

        /// <summary>本进程是否为局内专用服务器（战斗权威所在）。</summary>
        public static bool IsDedicatedServer
        {
            get { return _mode == PMNetMode.DedicatedServer; }
        }

        /// <summary>本进程是否为玩家客户端。</summary>
        public static bool IsClient
        {
            get { return _mode == PMNetMode.Client; }
        }

        /// <summary>
        /// 由启动参数推导网络模式。这是「客户端/服务端」判定的唯一来源。
        /// </summary>
        public static PMNetMode ResolveMode(PMNetLaunchOptions options)
        {
            if (options == null)
            {
                return PMNetMode.Standalone;
            }

            return options.IsDedicatedServer ? PMNetMode.DedicatedServer : PMNetMode.Client;
        }

        /// <summary>
        /// 用启动参数初始化进程网络状态。
        /// 幂等：重复调用以最后一次为准（Unity 的域重载与测试会重入，不应因此报错）。
        /// </summary>
        /// <param name="options">已解析的启动参数；null 表示按默认（客户端）处理。</param>
        /// <returns>实际采用的启动参数，便于调用方直接打日志。</returns>
        public static PMNetLaunchOptions Initialize(PMNetLaunchOptions options)
        {
            _options = options ?? PMNetLaunchOptions.Parse(null);
            _mode = ResolveMode(_options);
            return _options;
        }

        /// <summary>从当前进程命令行解析并初始化（Unity 播放器侧的正常入口）。</summary>
        public static PMNetLaunchOptions InitializeFromCurrentProcess()
        {
            return Initialize(PMNetLaunchOptions.FromCurrentProcess());
        }

        /// <summary>单行摘要，供启动日志使用。</summary>
        public static string Describe()
        {
            if (_options == null)
            {
                return "netmode=" + _mode + " (uninitialized)";
            }

            return _options.Describe();
        }

        /// <summary>
        /// 复位到未初始化状态。仅供测试与工具使用：
        /// 播放器生命周期内不存在「切换进程形态」这种操作，不要在生产路径调用。
        /// </summary>
        public static void Reset()
        {
            _options = null;
            _mode = PMNetMode.Standalone;
        }
    }
}
