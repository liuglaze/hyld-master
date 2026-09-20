using System;
using System.Collections.Generic;
using System.Globalization;

namespace PMNet
{
    /// <summary>
    /// 进程网络形态。描述「这个进程是被当成客户端拉起，还是被当成专用服务器拉起」。
    ///
    /// 与 PMNetMode 的分工：PMNetMode 是运行期网络模式（含 Standalone），供复制层做执行端判定；
    /// 本枚举只承载启动期身份，是 PMNetMode 的初始来源。
    /// </summary>
    public enum PMNetProcessKind : byte
    {
        /// <summary>玩家客户端。</summary>
        Client = 0,

        /// <summary>局内专用服务器（HyldDS）。</summary>
        DedicatedServer = 1,
    }

    /// <summary>
    /// 启动期命令行参数解析结果（对应 UE 的 FCommandLine / FParse::Value 语义）。
    ///
    /// 背景：Unity 2019.4 既没有 Windows 的 Dedicated Server 构建目标，也没有 UNITY_SERVER 宏
    /// （见计划 §2.8）。因此 DS 只能打包成常规 StandaloneWindows64，由 Lobby 拉起时用命令行
    /// 参数标明身份，进程启动时在运行时判定。本类就是那个「运行时判定」的唯一依据。
    ///
    /// 本类刻意放在 PMNet 核心里、且不引用 UnityEngine，原因有二：
    ///   1) 客户端构建与 DS 构建是同一个二进制，两端都要用；
    ///   2) 纯 C# 才能被 Tools/PMNetLaunchCheck 直接编译运行，不依赖 Unity 编辑器即可验证。
    ///
    /// 解析规则（需与 Unity 自身参数共存）：
    ///   - 以 '-' 开头的参数视为开关；单独的 "-" 视为值（Unity 的 `-logFile -` 表示输出到 stdout）。
    ///   - 支持 `-key value` 与 `-key=value` 两种写法。
    ///   - 参数名大小写不敏感；同名参数重复出现时以最后一次为准。
    ///   - 无法识别的参数忽略，但完整保留在 <see cref="RawArgs"/> 中。
    ///   - 非法取值不抛异常：保留默认值并记入 <see cref="Warnings"/>。
    ///     启动期不应因为一个手滑的参数导致进程直接崩掉，而应「起来 + 报错」。
    /// </summary>
    public sealed class PMNetLaunchOptions
    {
        /// <summary>默认逻辑帧率（Hz）。与 ProjectMecury 的角色默认复制频率一致。</summary>
        public const int DefaultTickRate = 30;

        /// <summary>默认监听端口。0 表示「由系统分配」，即调用方尚未指定。</summary>
        public const int DefaultListenPort = 0;

        /// <summary>默认监听地址：所有网卡。</summary>
        public const string DefaultListenAddress = "0.0.0.0";

        // ================= 身份 =================

        /// <summary>进程形态。</summary>
        public PMNetProcessKind Kind;

        /// <summary>是否被拉起为专用服务器。</summary>
        public bool IsDedicatedServer
        {
            get { return Kind == PMNetProcessKind.DedicatedServer; }
        }

        // ================= Unity 内建参数（本层关心的子集）=================

        /// <summary>是否处于 `-batchmode`（无窗口、非交互）。</summary>
        public bool BatchMode;

        /// <summary>是否处于 `-nographics`（不初始化图形设备）。</summary>
        public bool NoGraphics;

        /// <summary>
        /// `-logFile` 的取值。null 表示未指定；"-" 表示输出到 stdout。
        /// 本层只负责解析，不负责打开文件（那是宿主进程的事）。
        /// </summary>
        public string LogFile;

        // ================= DS 参数（由 Lobby 拉起时传入）=================

        /// <summary>DS 实例标识（一局一个，用于日志与 Lobby 侧对账）。</summary>
        public string DsId;

        /// <summary>对局标识（由 Lobby 分配）。</summary>
        public string MatchId;

        /// <summary>监听地址。默认 <see cref="DefaultListenAddress"/>。</summary>
        public string ListenAddress = DefaultListenAddress;

        /// <summary>
        /// 监听端口。默认 <see cref="DefaultListenPort"/>（0 = 未指定）。
        ///
        /// 注意：现有客户端把战斗 UDP 端口硬编码为 7777（Server/ConstValue.cs 的
        /// NetConfigValue.ServiceUDPPort）。「每局一个 DS」的形态下端口必须每局不同，
        /// 否则同机多局会互相抢占，因此这里必须由 Lobby 显式下发。
        /// </summary>
        public int ListenPort = DefaultListenPort;

        /// <summary>Lobby 地址（DS 反向注册用）。null 表示未指定。</summary>
        public string LobbyAddress;

        /// <summary>Lobby 端口。0 表示未指定。</summary>
        public int LobbyPort;

        // ================= R3-B 新链参数（契约 net-r3-control-contract.md §7.3 / §7.4）=================

        /// <summary>
        /// `-bootstrap &lt;绝对路径&gt;`：本局引导文件（由 Lobby 原子发布、受本机账户边界保护）。
        ///
        /// **非空即表示走 R3-B 新链**（控制通道 + `PMUdpSessionEndpoint`），旧 UDP 诊断路由不启动；
        /// 为 null 时保留历史启动形态（旧诊断路由）。只传路径：密钥与票据绝不会出现在命令行上。
        /// </summary>
        public string BootstrapPath;

        /// <summary>`-control &lt;host:port&gt;`：Lobby 控制 listener（首版只允许 loopback）。null 表示未指定。</summary>
        public string ControlHost;

        /// <summary>`-control` 的端口部分。0 表示未指定（新链会明确失败，不静默退旧路径）。</summary>
        public int ControlPort;

        /// <summary>
        /// `-server-smoke`：显式自动验收开关。**默认 false**。
        ///
        /// 打开时，全部名册玩家完成探针后提交一份明确标注 smoke 的测试结果；
        /// 缺省时**不自动胜利**（正常路径的结果由未来权威玩法调 SubmitResult 提交）。
        /// </summary>
        public bool ServerSmoke;

        /// <summary>逻辑帧率（Hz）。默认 <see cref="DefaultTickRate"/>。</summary>
        public int TickRate = DefaultTickRate;

        // ================= 诊断 =================

        /// <summary>原始参数，原样保留，便于日志复现与排查。</summary>
        public string[] RawArgs = new string[0];

        /// <summary>解析期的非致命问题（非法端口、格式不对的 lobby 地址等）。</summary>
        public readonly List<string> Warnings = new List<string>();

        // ================= 解析 =================

        /// <summary>
        /// 解析命令行参数。纯函数：不读环境、不写全局状态、不抛异常，便于直接跑测。
        /// </summary>
        /// <param name="args">命令行参数。null 或空数组表示全部取默认值。</param>
        public static PMNetLaunchOptions Parse(string[] args)
        {
            PMNetLaunchOptions options = new PMNetLaunchOptions();
            options.RawArgs = args ?? new string[0];

#if UNITY_SERVER
            // 前向兼容：若将来升级到带 Dedicated Server 构建目标的 Unity（2020.1+），
            // 该平台会自动定义 UNITY_SERVER。此时即使没传 -server，也应视为 DS。
            // 在 2019.4 上该分支恒不成立，等价于不存在。
            options.Kind = PMNetProcessKind.DedicatedServer;
#endif

            for (int i = 0; i < options.RawArgs.Length; i++)
            {
                string arg = options.RawArgs[i];
                if (!IsSwitch(arg))
                {
                    // 非开关：可执行文件路径、或上一步已消费的值。忽略。
                    continue;
                }

                string key = arg.Substring(1);
                string value = null;

                int eq = key.IndexOf('=');
                if (eq >= 0)
                {
                    value = key.Substring(eq + 1);
                    key = key.Substring(0, eq);
                }
                else if (i + 1 < options.RawArgs.Length && !IsSwitch(options.RawArgs[i + 1]))
                {
                    value = options.RawArgs[i + 1];
                    i++;
                }

                options.Apply(key, value);
            }

            return options;
        }

        /// <summary>从当前进程的命令行解析（Unity 播放器侧的正常入口）。</summary>
        public static PMNetLaunchOptions FromCurrentProcess()
        {
            return Parse(Environment.GetCommandLineArgs());
        }

        /// <summary>
        /// 判断是否为一个开关参数。单独的 "-" 不是开关，而是合法的值
        /// （Unity 用 `-logFile -` 表示输出到 stdout，必须能作为值被消费）。
        /// </summary>
        private static bool IsSwitch(string arg)
        {
            return !string.IsNullOrEmpty(arg) && arg.Length > 1 && arg[0] == '-';
        }

        /// <summary>把单个参数落到字段上。参数名大小写不敏感。</summary>
        private void Apply(string key, string value)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            switch (key.ToLowerInvariant())
            {
                case "server":
                    Kind = PMNetProcessKind.DedicatedServer;
                    break;

                case "batchmode":
                    BatchMode = true;
                    break;

                case "nographics":
                    NoGraphics = true;
                    break;

                case "logfile":
                    // Unity 的 -logFile 允许缺值（用默认路径）；缺值时不改动本字段。
                    if (value != null)
                    {
                        LogFile = value;
                    }
                    break;

                case "dsid":
                    DsId = value;
                    break;

                case "matchid":
                    MatchId = value;
                    break;

                case "listen":
                    if (IsNonEmpty(value))
                    {
                        ListenAddress = value;
                    }
                    break;

                case "port":
                    ListenPort = ParsePort(value, "port", ListenPort);
                    break;

                case "lobby":
                    ParseLobby(value);
                    break;

                case "bootstrap":
                    // 与 -logFile 同口径：缺值时不改动本字段（父级会给 warning 的另外一条路）。
                    if (value != null)
                    {
                        BootstrapPath = value;
                        if (value.Length > 0 && !System.IO.Path.IsPathRooted(value))
                        {
                            // 引导文件是本局秘密的载体，只允许绝对路径（不信任相对路径推导出的目标）。
                            Warnings.Add("-bootstrap 应为绝对路径，当前为 '" + value + "'");
                        }
                    }
                    break;

                case "control":
                    ParseControl(value);
                    break;

                case "server-smoke":
                    // 纯开关：不带取值。写成 `-server-smoke 1` 时那个 "1" 会被当普通 token 忽略。
                    ServerSmoke = true;
                    break;

                case "tickrate":
                    TickRate = ParseTickRate(value, TickRate);
                    break;

                default:
                    // 未知参数（含 Unity 自己的大量开关）一律忽略，但 RawArgs 里仍可查。
                    break;
            }
        }

        /// <summary>解析端口：0~65535 合法（0 表示由系统分配）。非法值保留旧值并记警告。</summary>
        private int ParsePort(string value, string name, int fallback)
        {
            if (!IsNonEmpty(value))
            {
                // 注意：以 '-' 开头的取值不会被当作值消费（见 IsSwitch），
                // 因此「-port -1」走到这里，而「-port=-1」会走下面的范围校验。
                Warnings.Add("-" + name + " 缺少取值，已保留原值 " + fallback.ToString(CultureInfo.InvariantCulture)
                    + "（若取值以 '-' 开头，请写成 -" + name + "=值）");
                return fallback;
            }

            int parsed;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                Warnings.Add("-" + name + " 取值不是整数，已保留原值 " + fallback.ToString(CultureInfo.InvariantCulture) + "：'" + value + "'");
                return fallback;
            }

            if (parsed < 0 || parsed > 65535)
            {
                Warnings.Add("-" + name + " 越界（合法范围 0~65535），已保留原值 " + fallback.ToString(CultureInfo.InvariantCulture) + "：" + parsed.ToString(CultureInfo.InvariantCulture));
                return fallback;
            }

            return parsed;
        }

        /// <summary>解析 -tickrate：必须为正整数，否则保留旧值。</summary>
        private int ParseTickRate(string value, int fallback)
        {
            if (!IsNonEmpty(value))
            {
                Warnings.Add("-tickrate 缺少取值，已保留原值 " + fallback.ToString(CultureInfo.InvariantCulture)
                    + "（若取值以 '-' 开头，请写成 -tickrate=值）");
                return fallback;
            }

            int parsed;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) || parsed <= 0)
            {
                Warnings.Add("-tickrate 取值非法（需为正整数），已保留原值 " + fallback.ToString(CultureInfo.InvariantCulture) + "：'" + value + "'");
                return fallback;
            }

            return parsed;
        }

        /// <summary>
        /// 解析 -lobby，接受 "host:port" 或单独的 "host"。
        /// 用最后一个 ':' 切分，以兼容形如 "::1:9000" 的写法。
        /// </summary>
        private void ParseLobby(string value)
        {
            if (!IsNonEmpty(value))
            {
                return;
            }

            int sep = value.LastIndexOf(':');
            if (sep < 0)
            {
                LobbyAddress = value;
                return;
            }

            string host = value.Substring(0, sep);
            string portText = value.Substring(sep + 1);

            int port;
            if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port < 0 || port > 65535)
            {
                // 端口部分不合法时，把整串当作主机名，避免误切分掉合法主机。
                Warnings.Add("-lobby 端口部分非法，已按纯主机名处理：'" + value + "'");
                LobbyAddress = value;
                return;
            }

            LobbyAddress = host;
            LobbyPort = port;
        }

        private static bool IsNonEmpty(string value)
        {
            return !string.IsNullOrEmpty(value);
        }

        /// <summary>
        /// 解析 `-control`，接受 "host:port" 或单独的 "host"（与 -lobby 同口径）。
        /// 用最后一个 ':' 切分，以兼容形如 "::1:7800" 的写法。
        /// </summary>
        private void ParseControl(string value)
        {
            if (!IsNonEmpty(value))
            {
                Warnings.Add("-control 缺少取值，已保留原值（若取值以 '-' 开头，请写成 -control=值）");
                return;
            }

            int sep = value.LastIndexOf(':');
            if (sep < 0)
            {
                ControlHost = value;
                return;
            }

            string host = value.Substring(0, sep);
            string portText = value.Substring(sep + 1);

            int port;
            if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port <= 0 || port > 65535)
            {
                // 端口非法时把整串当主机名：宁可后面显式报“端口未指定”，也不静默切掉一半主机名。
                Warnings.Add("-control 端口部分非法（需为 1~65535），已按纯主机名处理：'" + value + "'");
                ControlHost = value;
                ControlPort = 0;
                return;
            }

            ControlHost = host;
            ControlPort = port;
        }

        /// <summary>单行摘要，供启动日志使用。</summary>
        public string Describe()
        {
            return "process=" + Kind
                + " mode=" + (IsDedicatedServer ? PMNetMode.DedicatedServer : PMNetMode.Client)
                + " batchmode=" + (BatchMode ? 1 : 0)
                + " nographics=" + (NoGraphics ? 1 : 0)
                + " listen=" + ListenAddress + ":" + ListenPort.ToString(CultureInfo.InvariantCulture)
                + " lobby=" + (LobbyAddress ?? "<none>") + ":" + LobbyPort.ToString(CultureInfo.InvariantCulture)
                + " bootstrap=" + (BootstrapPath ?? "<none>")
                + " control=" + (ControlHost ?? "<none>") + ":" + ControlPort.ToString(CultureInfo.InvariantCulture)
                + " serverSmoke=" + (ServerSmoke ? 1 : 0)
                + " tickrate=" + TickRate.ToString(CultureInfo.InvariantCulture)
                + " dsid=" + (DsId ?? "<none>")
                + " matchid=" + (MatchId ?? "<none>")
                + " logfile=" + (LogFile ?? "<none>");
        }
    }
}
