using System;
using System.Collections.Generic;
using PMNet;

namespace PMNetLaunchCheck
{
    /// <summary>
    /// 计划 P2' 门禁：启动参数 → 进程网络形态 的契约校验。
    ///
    /// 为什么这条契约值得单独跑测：
    /// Unity 2019.4 没有 Windows 的 Dedicated Server 构建目标，也没有 UNITY_SERVER 宏
    /// （见计划 §2.8），因此 DS 与客户端是同一个二进制，只能靠命令行参数区分。
    /// 这意味着「参数解析错了」不会有任何编译期信号，表现是 DS 起来后按客户端跑
    /// （去连大厅、初始化 UI），在这种无头环境里极难定位。
    ///
    /// 本程序覆盖三类风险：
    ///   1. 身份判定（-server / 大小写 / 与 Unity 内建参数共存）；
    ///   2. 值消费边界（"-logFile -" 这种「值看起来像开关」的写法、负数的两种写法）；
    ///   3. 非法输入不崩（启动期应「起来 + 报错」，而不是异常退出）。
    ///
    /// 退出码：0 全部通过；1 存在失败。
    /// </summary>
    internal static class Program
    {
        private static int _checks;
        private static int _failures;

        private static int Main()
        {
            Console.WriteLine("== PMNet P2' 启动参数与进程形态校验 ==");
            Console.WriteLine();

            ScenarioDefaults();
            ScenarioServerIdentity();
            ScenarioBuiltinFlags();
            ScenarioKeyValueForms();
            ScenarioLogFileEdge();
            ScenarioValueConsumption();
            ScenarioInvalidValues();
            ScenarioLobbyParsing();
            ScenarioR3BParameters();
            ScenarioPrecedenceAndRaw();
            ScenarioRuntime();

            Console.WriteLine();
            Console.WriteLine("== 汇总: " + _checks + " 项检查, " + _failures + " 项失败 ==");
            return _failures == 0 ? 0 : 1;
        }

        // ---------------- 场景 1：默认值 ----------------

        private static void ScenarioDefaults()
        {
            Section("场景 1：无参数 → 客户端，且各处取默认值");

            PMNetLaunchOptions fromNull = PMNetLaunchOptions.Parse(null);
            CheckEqual("null 参数 → Kind", fromNull.Kind.ToString(), nameof(PMNetProcessKind.Client));
            CheckTrue("null 参数 → IsDedicatedServer=false", !fromNull.IsDedicatedServer);
            CheckEqual("null 参数 → TickRate 默认 30", fromNull.TickRate, PMNetLaunchOptions.DefaultTickRate);
            CheckEqual("null 参数 → ListenPort 默认 0", fromNull.ListenPort, PMNetLaunchOptions.DefaultListenPort);
            CheckEqual("null 参数 → ListenAddress 默认 0.0.0.0", fromNull.ListenAddress, PMNetLaunchOptions.DefaultListenAddress);
            CheckNull("null 参数 → LogFile 未指定", fromNull.LogFile);
            CheckEqual("null 参数 → RawArgs 为空数组", fromNull.RawArgs.Length, 0);
            CheckEqual("null 参数 → 无警告", fromNull.Warnings.Count, 0);

            PMNetLaunchOptions fromEmpty = PMNetLaunchOptions.Parse(new string[0]);
            CheckEqual("空数组 → Kind", fromEmpty.Kind.ToString(), nameof(PMNetProcessKind.Client));

            // 只有非身份参数时，仍是客户端。
            PMNetLaunchOptions clientWithArgs = PMNetLaunchOptions.Parse(new string[] { "-batchmode", "-nographics" });
            CheckEqual("无 -server 时仍为客户端", clientWithArgs.Kind.ToString(), nameof(PMNetProcessKind.Client));
        }

        // ---------------- 场景 2：服务端身份 ----------------

        private static void ScenarioServerIdentity()
        {
            Section("场景 2：-server 身份判定");

            CheckEqual("-server → DedicatedServer", PMNetLaunchOptions.Parse(new string[] { "-server" }).Kind.ToString(), nameof(PMNetProcessKind.DedicatedServer));
            CheckTrue("-server → IsDedicatedServer=true", PMNetLaunchOptions.Parse(new string[] { "-server" }).IsDedicatedServer);

            // 大小写不敏感：Windows 命令行常被脚本以不同大小写拼接。
            CheckEqual("-SERVER 大写 → DedicatedServer", PMNetLaunchOptions.Parse(new string[] { "-SERVER" }).Kind.ToString(), nameof(PMNetProcessKind.DedicatedServer));
            CheckEqual("-Server 混合 → DedicatedServer", PMNetLaunchOptions.Parse(new string[] { "-Server" }).Kind.ToString(), nameof(PMNetProcessKind.DedicatedServer));

            // 可执行文件路径在 args[0]，必须被忽略。
            PMNetLaunchOptions withExe = PMNetLaunchOptions.Parse(new string[] { @"D:\HyldDS\HyldDS.exe", "-server" });
            CheckEqual("含 exe 路径 → DedicatedServer", withExe.Kind.ToString(), nameof(PMNetProcessKind.DedicatedServer));

            // "--server" 不是受支持写法（参数名精确匹配单个 '-'）。
            CheckEqual("--server 不支持 → Client", PMNetLaunchOptions.Parse(new string[] { "--server" }).Kind.ToString(), nameof(PMNetProcessKind.Client));

            // 单独的 "-" 不是开关，也不代表服务端。
            CheckEqual("单独的 - → Client", PMNetLaunchOptions.Parse(new string[] { "-" }).Kind.ToString(), nameof(PMNetProcessKind.Client));
        }

        // ---------------- 场景 3：与 Unity 内建参数共存 ----------------

        private static void ScenarioBuiltinFlags()
        {
            Section("场景 3：与 Unity 内建参数共存（真实的 DS 启动行）");

            PMNetLaunchOptions ds = PMNetLaunchOptions.Parse(new string[]
            {
                @"D:\HyldDS\HyldDS.exe",
                "-batchmode",
                "-nographics",
                "-server",
                "-logFile",
                @"D:\HyldDS\Logs\ds_1.log",
                "-dsid",
                "ds-001",
                "-matchid",
                "match-777",
                "-listen",
                "0.0.0.0",
                "-port",
                "7801",
                "-lobby",
                "10.0.0.5:7778",
                "-tickrate",
                "30",
            });

            CheckEqual("真实启动行 → Kind", ds.Kind.ToString(), nameof(PMNetProcessKind.DedicatedServer));
            CheckTrue("真实启动行 → BatchMode", ds.BatchMode);
            CheckTrue("真实启动行 → NoGraphics", ds.NoGraphics);
            CheckEqual("真实启动行 → LogFile", ds.LogFile, @"D:\HyldDS\Logs\ds_1.log");
            CheckEqual("真实启动行 → DsId", ds.DsId, "ds-001");
            CheckEqual("真实启动行 → MatchId", ds.MatchId, "match-777");
            CheckEqual("真实启动行 → ListenAddress", ds.ListenAddress, "0.0.0.0");
            CheckEqual("真实启动行 → ListenPort", ds.ListenPort, 7801);
            CheckEqual("真实启动行 → LobbyAddress", ds.LobbyAddress, "10.0.0.5");
            CheckEqual("真实启动行 → LobbyPort", ds.LobbyPort, 7778);
            CheckEqual("真实启动行 → TickRate", ds.TickRate, 30);
            CheckEqual("真实启动行 → 无警告", ds.Warnings.Count, 0);
        }

        // ---------------- 场景 4：两种赋值写法 ----------------

        private static void ScenarioKeyValueForms()
        {
            Section("场景 4：-key value 与 -key=value 等价");

            PMNetLaunchOptions spaced = PMNetLaunchOptions.Parse(new string[] { "-port", "7788" });
            PMNetLaunchOptions equals = PMNetLaunchOptions.Parse(new string[] { "-port=7788" });
            CheckEqual("-port 7788 → 7788", spaced.ListenPort, 7788);
            CheckEqual("-port=7788 → 7788", equals.ListenPort, 7788);

            PMNetLaunchOptions kv = PMNetLaunchOptions.Parse(new string[] { "-dsid=abc", "-matchid=xyz", "-tickrate=45" });
            CheckEqual("-dsid=abc", kv.DsId, "abc");
            CheckEqual("-matchid=xyz", kv.MatchId, "xyz");
            CheckEqual("-tickrate=45", kv.TickRate, 45);

            // 空值的 = 形式：不应把字段清成空串。
            PMNetLaunchOptions emptyVal = PMNetLaunchOptions.Parse(new string[] { "-listen=" });
            CheckEqual("-listen= 空值 → 保留默认监听地址", emptyVal.ListenAddress, PMNetLaunchOptions.DefaultListenAddress);
        }

        // ---------------- 场景 5：-logFile 的两种边界 ----------------

        private static void ScenarioLogFileEdge()
        {
            Section("场景 5：-logFile 的值消费边界");

            // 关键边界：Unity 用 "-logFile -" 表示输出到 stdout。
            // "-" 长度不足 2，必须被判为「值」而非「开关」。
            PMNetLaunchOptions stdoutLog = PMNetLaunchOptions.Parse(new string[] { "-server", "-logFile", "-" });
            CheckEqual("-logFile - → LogFile=\"-\"（stdout）", stdoutLog.LogFile, "-");
            CheckEqual("-logFile - 不吞掉身份", stdoutLog.Kind.ToString(), nameof(PMNetProcessKind.DedicatedServer));

            // 缺值：Unity 允许 -logFile 不带值（用默认路径），此处不应把字段改成 null 或空串。
            PMNetLaunchOptions noValue = PMNetLaunchOptions.Parse(new string[] { "-logFile" });
            CheckNull("-logFile 缺值 → 保持未指定", noValue.LogFile);

            PMNetLaunchOptions beforeSwitch = PMNetLaunchOptions.Parse(new string[] { "-logFile", "-server" });
            CheckNull("-logFile 后接开关 → 不缺值，保持未指定", beforeSwitch.LogFile);
            CheckEqual("-logFile 后接开关 → 开关仍生效", beforeSwitch.Kind.ToString(), nameof(PMNetProcessKind.DedicatedServer));

            // 带空格的路径：由 OS 拆分，此处应是一个完整参数。
            PMNetLaunchOptions spacedPath = PMNetLaunchOptions.Parse(new string[] { "-logFile", @"C:\Program Files\Hyld DS\ds.log" });
            CheckEqual("-logFile 含空格路径", spacedPath.LogFile, @"C:\Program Files\Hyld DS\ds.log");
        }

        // ---------------- 场景 6：值消费不误吞后续开关 ----------------

        private static void ScenarioValueConsumption()
        {
            Section("场景 6：值消费不误吞后续参数");

            // Unity 自身带值开关不应破坏我们的解析。
            PMNetLaunchOptions mixed = PMNetLaunchOptions.Parse(new string[] { "-screen-width", "1280", "-screen-height", "720", "-server" });
            CheckEqual("越过未知带值开关 → DedicatedServer", mixed.Kind.ToString(), nameof(PMNetProcessKind.DedicatedServer));

            // -port 的值为开关形式时不应被消费（见场景 7 的负值用例）。
            PMNetLaunchOptions flagAsValue = PMNetLaunchOptions.Parse(new string[] { "-port", "-server" });
            CheckEqual("-port 后接开关 → 端口保持默认", flagAsValue.ListenPort, PMNetLaunchOptions.DefaultListenPort);
            CheckEqual("-port 后接开关 → 开关仍生效", flagAsValue.Kind.ToString(), nameof(PMNetProcessKind.DedicatedServer));

            // 未知开关不应吞掉紧随其后的已知开关。
            PMNetLaunchOptions unknown = PMNetLaunchOptions.Parse(new string[] { "-unknown-flag", "-nographics" });
            CheckTrue("未知开关不吞后续开关 → NoGraphics", unknown.NoGraphics);
        }

        // ---------------- 场景 7：非法值不崩 ----------------

        private static void ScenarioInvalidValues()
        {
            Section("场景 7：非法取值 → 保留默认 + 记警告，不抛异常");

            PMNetLaunchOptions nonNumeric = PMNetLaunchOptions.Parse(new string[] { "-port", "abc" });
            CheckEqual("-port abc → 保留默认", nonNumeric.ListenPort, PMNetLaunchOptions.DefaultListenPort);
            CheckEqual("-port abc → 记 1 条警告", nonNumeric.Warnings.Count, 1);

            PMNetLaunchOptions overflow = PMNetLaunchOptions.Parse(new string[] { "-port", "99999" });
            CheckEqual("-port 99999 → 保留默认", overflow.ListenPort, PMNetLaunchOptions.DefaultListenPort);
            CheckEqual("-port 99999 → 记 1 条警告", overflow.Warnings.Count, 1);

            // 负数的两种写法：空格形式会被当成开关（值未被消费，走「缺值」分支），
            // = 形式才会进入范围校验。两种都会告警，但文案不同。
            PMNetLaunchOptions negativeSpaced = PMNetLaunchOptions.Parse(new string[] { "-port", "-1" });
            CheckEqual("-port -1（空格）→ 值未被消费，保持默认", negativeSpaced.ListenPort, PMNetLaunchOptions.DefaultListenPort);
            CheckEqual("-port -1（空格）→ 按「缺值」告警", negativeSpaced.Warnings.Count, 1);
            CheckTrue("-port -1（空格）→ 告警文案指向缺值", negativeSpaced.Warnings[0].Contains("缺少取值"));

            PMNetLaunchOptions negativeInline = PMNetLaunchOptions.Parse(new string[] { "-port=-1" });
            CheckEqual("-port=-1（内联）→ 保留默认", negativeInline.ListenPort, PMNetLaunchOptions.DefaultListenPort);
            CheckEqual("-port=-1（内联）→ 记 1 条警告", negativeInline.Warnings.Count, 1);
            CheckTrue("-port=-1（内联）→ 告警文案指向越界", negativeInline.Warnings[0].Contains("越界"));

            // 端口 0 是合法值（表示由系统分配）。
            PMNetLaunchOptions zeroPort = PMNetLaunchOptions.Parse(new string[] { "-port", "0" });
            CheckEqual("-port 0 → 合法", zeroPort.ListenPort, 0);
            CheckEqual("-port 0 → 无警告", zeroPort.Warnings.Count, 0);

            // 端口上界 65535 合法，65536 越界。
            CheckEqual("-port 65535 → 合法", PMNetLaunchOptions.Parse(new string[] { "-port", "65535" }).ListenPort, 65535);
            CheckEqual("-port 65536 → 保留默认", PMNetLaunchOptions.Parse(new string[] { "-port", "65536" }).ListenPort, PMNetLaunchOptions.DefaultListenPort);

            PMNetLaunchOptions badTick = PMNetLaunchOptions.Parse(new string[] { "-tickrate", "0" });
            CheckEqual("-tickrate 0 → 保留默认 30", badTick.TickRate, PMNetLaunchOptions.DefaultTickRate);
            CheckEqual("-tickrate 0 → 记 1 条警告", badTick.Warnings.Count, 1);

            PMNetLaunchOptions badTickText = PMNetLaunchOptions.Parse(new string[] { "-tickrate", "fast" });
            CheckEqual("-tickrate fast → 保留默认", badTickText.TickRate, PMNetLaunchOptions.DefaultTickRate);

            // 缺值与非法值应给出可区分的文案，便于运维定位。
            PMNetLaunchOptions missingPort = PMNetLaunchOptions.Parse(new string[] { "-server", "-port" });
            CheckEqual("-port 结尾缺值 → 保留默认", missingPort.ListenPort, PMNetLaunchOptions.DefaultListenPort);
            CheckTrue("-port 结尾缺值 → 告警文案指向缺值", missingPort.Warnings.Count == 1 && missingPort.Warnings[0].Contains("缺少取值"));
        }

        // ---------------- 场景 8：-lobby 解析 ----------------

        private static void ScenarioLobbyParsing()
        {
            Section("场景 8：-lobby 主机:端口 解析");

            PMNetLaunchOptions hostPort = PMNetLaunchOptions.Parse(new string[] { "-lobby", "127.0.0.1:9000" });
            CheckEqual("-lobby host:port → 主机", hostPort.LobbyAddress, "127.0.0.1");
            CheckEqual("-lobby host:port → 端口", hostPort.LobbyPort, 9000);

            PMNetLaunchOptions hostOnly = PMNetLaunchOptions.Parse(new string[] { "-lobby", "127.0.0.1" });
            CheckEqual("-lobby 仅主机 → 主机", hostOnly.LobbyAddress, "127.0.0.1");
            CheckEqual("-lobby 仅主机 → 端口保持 0", hostOnly.LobbyPort, 0);

            // 端口部分非法：整串按主机名处理，避免把合法主机切掉一半。
            PMNetLaunchOptions badPort = PMNetLaunchOptions.Parse(new string[] { "-lobby", "127.0.0.1:abc" });
            CheckEqual("-lobby 端口非法 → 整串当主机", badPort.LobbyAddress, "127.0.0.1:abc");
            CheckEqual("-lobby 端口非法 → 记 1 条警告", badPort.Warnings.Count, 1);

            // IPv6：按最后一个 ':' 切分才正确。
            PMNetLaunchOptions v6 = PMNetLaunchOptions.Parse(new string[] { "-lobby", "::1:9000" });
            CheckEqual("-lobby IPv6 → 主机", v6.LobbyAddress, "::1");
            CheckEqual("-lobby IPv6 → 端口", v6.LobbyPort, 9000);
        }

        // ---------------- 场景 8b：R3-B 参数（-bootstrap / -control / -server-smoke）----------------

        /// <summary>
        /// R3-B 参数面（契约 net-r3-control-contract.md §7.3 / §7.4）。
        ///
        /// 为什么必须单列：这些参数的“错”不会有任何编译期信号，而表现是
        /// 「DS 起来了但走错链路 / 控制通道连不上 / 不确定地自动胜利」，
        /// 在无头环境里极难定位。尤其是“无参数时客户端行为不变”必须被钉住。
        /// </summary>
        private static void ScenarioR3BParameters()
        {
            Section("场景 8b：R3-B 参数（-bootstrap / -control / -server-smoke）");

            // ---- 缺省：客户端与旧形态不受影响 ----
            PMNetLaunchOptions plain = PMNetLaunchOptions.Parse(null);
            CheckNull("缺省 → BootstrapPath 为 null", plain.BootstrapPath);
            CheckNull("缺省 → ControlHost 为 null", plain.ControlHost);
            CheckEqual("缺省 → ControlPort 为 0（未指定）", plain.ControlPort, 0);
            CheckTrue("缺省 → ServerSmoke=false", !plain.ServerSmoke);

            PMNetLaunchOptions client = PMNetLaunchOptions.Parse(new string[] { "-batchmode" });
            CheckNull("无 -bootstrap 的客户端 → BootstrapPath 仍为 null", client.BootstrapPath);
            CheckTrue("无 -server-smoke 的客户端 → ServerSmoke 仍为 false", !client.ServerSmoke);
            CheckEqual("无 -bootstrap 的客户端 → 无告警", client.Warnings.Count, 0);

            // ---- -bootstrap ----
            string bootstrapAbs = @"D:\HyldDS\boot\match-777.json";
            PMNetLaunchOptions boot = PMNetLaunchOptions.Parse(new string[] { "-server", "-bootstrap", bootstrapAbs });
            CheckEqual("-bootstrap 绝对路径 → 原样保留", boot.BootstrapPath, bootstrapAbs);
            CheckEqual("-bootstrap 绝对路径 → 无告警", boot.Warnings.Count, 0);
            CheckTrue("-bootstrap 存在 ⇒ 仍为 DedicatedServer", boot.IsDedicatedServer);

            PMNetLaunchOptions bootEquals = PMNetLaunchOptions.Parse(new string[] { "-bootstrap=" + bootstrapAbs });
            CheckEqual("-bootstrap=值 写法 → 同样生效", bootEquals.BootstrapPath, bootstrapAbs);

            PMNetLaunchOptions bootUpper = PMNetLaunchOptions.Parse(new string[] { "-BOOTSTRAP", bootstrapAbs });
            CheckEqual("-BOOTSTRAP 大小写不敏感", bootUpper.BootstrapPath, bootstrapAbs);

            PMNetLaunchOptions bootRelative = PMNetLaunchOptions.Parse(new string[] { "-bootstrap", @"boot\m.json" });
            CheckEqual("相对路径 → 仍被记录", bootRelative.BootstrapPath, @"boot\m.json");
            CheckTrue("相对路径 → 记 1 条告警", bootRelative.Warnings.Count == 1);
            CheckTrue("相对路径 → 告警文案指向绝对路径",
                bootRelative.Warnings.Count == 1 && bootRelative.Warnings[0].Contains("绝对路径"));

            PMNetLaunchOptions bootMissing = PMNetLaunchOptions.Parse(new string[] { "-server", "-bootstrap" });
            CheckNull("-bootstrap 结尾缺值 → 不设置路径", bootMissing.BootstrapPath);

            // ---- -control ----
            PMNetLaunchOptions control = PMNetLaunchOptions.Parse(new string[] { "-control", "127.0.0.1:7800" });
            CheckEqual("-control host:port → 主机", control.ControlHost, "127.0.0.1");
            CheckEqual("-control host:port → 端口", control.ControlPort, 7800);

            PMNetLaunchOptions controlV6 = PMNetLaunchOptions.Parse(new string[] { "-control", "::1:7800" });
            CheckEqual("-control IPv6 → 主机", controlV6.ControlHost, "::1");
            CheckEqual("-control IPv6 → 端口", controlV6.ControlPort, 7800);

            PMNetLaunchOptions controlHostOnly = PMNetLaunchOptions.Parse(new string[] { "-control", "127.0.0.1" });
            CheckEqual("-control 仅主机 → 主机", controlHostOnly.ControlHost, "127.0.0.1");
            CheckEqual("-control 仅主机 → 端口保持 0", controlHostOnly.ControlPort, 0);

            PMNetLaunchOptions controlBad = PMNetLaunchOptions.Parse(new string[] { "-control", "127.0.0.1:abc" });
            CheckEqual("-control 端口非法 → 整串当主机", controlBad.ControlHost, "127.0.0.1:abc");
            CheckEqual("-control 端口非法 → 端口 0", controlBad.ControlPort, 0);
            CheckTrue("-control 端口非法 → 记 1 条告警", controlBad.Warnings.Count == 1);

            PMNetLaunchOptions controlRange = PMNetLaunchOptions.Parse(new string[] { "-control", "127.0.0.1:70000" });
            CheckEqual("-control 端口越界 → 端口 0", controlRange.ControlPort, 0);

            // ---- -server-smoke ----
            PMNetLaunchOptions smoke = PMNetLaunchOptions.Parse(new string[] { "-server", "-server-smoke" });
            CheckTrue("-server-smoke → ServerSmoke=true", smoke.ServerSmoke);
            CheckTrue("-server-smoke → 不影响身份判定", smoke.IsDedicatedServer);

            // 真实 R3-B 启动行（Lobby 按 §7.3 组装）：
            PMNetLaunchOptions real = PMNetLaunchOptions.Parse(new string[]
            {
                @"D:\HyldDS\HyldDS.exe",
                "-batchmode",
                "-nographics",
                "-server",
                "-bootstrap", bootstrapAbs,
                "-control", "127.0.0.1:7800",
                "-dsid", "ds-001",
                "-matchid", "match-777",
                "-listen", "0.0.0.0",
                "-port", "7811",
                "-tickrate", "30",
                "-server-smoke",
                "-logFile", @"D:\HyldDS\logs\ds_ds-001.log",
            });
            CheckEqual("真实启动行 → BootstrapPath", real.BootstrapPath, bootstrapAbs);
            CheckEqual("真实启动行 → ControlHost", real.ControlHost, "127.0.0.1");
            CheckEqual("真实启动行 → ControlPort", real.ControlPort, 7800);
            CheckTrue("真实启动行 → ServerSmoke", real.ServerSmoke);
            CheckEqual("真实启动行 → DsId", real.DsId, "ds-001");
            CheckEqual("真实启动行 → MatchId", real.MatchId, "match-777");
            CheckEqual("真实启动行 → ListenPort", real.ListenPort, 7811);
            CheckEqual("真实启动行 → 无告警", real.Warnings.Count, 0);

            // ---- Describe 需含新参数，便于只看一行日志定位 ----
            string described = real.Describe();
            CheckTrue("Describe 含 bootstrap", described.Contains("bootstrap="));
            CheckTrue("Describe 含 control", described.Contains("control="));
            CheckTrue("Describe 含 serverSmoke", described.Contains("serverSmoke=1"));
        }

        // ---------------- 场景 9：重复参数与 RawArgs ----------------

        private static void ScenarioPrecedenceAndRaw()
        {
            Section("场景 9：重复参数以最后一次为准；RawArgs 完整保留");

            PMNetLaunchOptions repeated = PMNetLaunchOptions.Parse(new string[] { "-port", "1", "-port", "2" });
            CheckEqual("重复 -port → 最后一次生效", repeated.ListenPort, 2);

            PMNetLaunchOptions repeatedListen = PMNetLaunchOptions.Parse(new string[] { "-listen", "10.0.0.1", "-listen", "10.0.0.2" });
            CheckEqual("重复 -listen → 最后一次生效", repeatedListen.ListenAddress, "10.0.0.2");

            string[] raw = new string[] { @"D:\a.exe", "-server", "-whatever", "value", "-port=9" };
            PMNetLaunchOptions rawOpts = PMNetLaunchOptions.Parse(raw);
            CheckEqual("RawArgs 长度完整保留", rawOpts.RawArgs.Length, raw.Length);
            CheckEqual("RawArgs[0] 保留 exe 路径", rawOpts.RawArgs[0], @"D:\a.exe");
            CheckEqual("未知参数不产生警告", rawOpts.Warnings.Count, 0);
        }

        // ---------------- 场景 10：PMNetRuntime ----------------

        private static void ScenarioRuntime()
        {
            Section("场景 10：PMNetRuntime 进程模式推导");

            PMNetRuntime.Reset();
            CheckTrue("Reset 后未初始化", !PMNetRuntime.Initialized);
            CheckEqual("Reset 后 Mode=Standalone", PMNetRuntime.Mode.ToString(), nameof(PMNetMode.Standalone));
            CheckEqual("null 参数 → ResolveMode=Standalone", PMNetRuntime.ResolveMode(null).ToString(), nameof(PMNetMode.Standalone));

            PMNetRuntime.Initialize(PMNetLaunchOptions.Parse(new string[] { "-server", "-port", "7801" }));
            CheckTrue("DS 初始化后 Initialized", PMNetRuntime.Initialized);
            CheckEqual("DS → Mode=DedicatedServer", PMNetRuntime.Mode.ToString(), nameof(PMNetMode.DedicatedServer));
            CheckTrue("DS → IsDedicatedServer", PMNetRuntime.IsDedicatedServer);
            CheckTrue("DS → !IsClient", !PMNetRuntime.IsClient);
            CheckEqual("DS → Options 可取", PMNetRuntime.Options.ListenPort, 7801);

            PMNetRuntime.Initialize(PMNetLaunchOptions.Parse(new string[0]));
            CheckEqual("客户端 → Mode=Client", PMNetRuntime.Mode.ToString(), nameof(PMNetMode.Client));
            CheckTrue("客户端 → IsClient", PMNetRuntime.IsClient);
            CheckTrue("客户端 → !IsDedicatedServer", !PMNetRuntime.IsDedicatedServer);

            // 幂等：Unity 的域重载/测试会重复初始化，不应报错。
            PMNetRuntime.Initialize(PMNetLaunchOptions.Parse(new string[] { "-server" }));
            PMNetRuntime.Initialize(PMNetLaunchOptions.Parse(new string[] { "-server" }));
            CheckEqual("重复初始化幂等 → DedicatedServer", PMNetRuntime.Mode.ToString(), nameof(PMNetMode.DedicatedServer));

            // 未初始化时 Describe 不应抛异常。
            PMNetRuntime.Reset();
            CheckTrue("未初始化时 Describe 可用", PMNetRuntime.Describe() != null);

            // Describe 需包含判定所需的关键信息，便于运维只看一行日志。
            PMNetLaunchOptions ds = PMNetLaunchOptions.Parse(new string[] { "-server", "-port", "7802", "-dsid", "d1" });
            string described = ds.Describe();
            CheckTrue("Describe 含 DedicatedServer", described.Contains("DedicatedServer"));
            CheckTrue("Describe 含端口", described.Contains("7802"));
            CheckTrue("Describe 含 dsid", described.Contains("d1"));

            PMNetRuntime.Reset();
            CheckTrue("收尾 Reset", !PMNetRuntime.Initialized);
        }

        // ---------------- 输出辅助 ----------------

        private static void Section(string title)
        {
            Console.WriteLine(title);
        }

        private static void CheckEqual(string name, int actual, int expected)
        {
            Report(name, actual == expected, "实际=" + actual + " 期望=" + expected);
        }

        private static void CheckEqual(string name, string actual, string expected)
        {
            Report(name, string.Equals(actual, expected, StringComparison.Ordinal), "实际='" + (actual ?? "<null>") + "' 期望='" + (expected ?? "<null>") + "'");
        }

        private static void CheckNull(string name, string actual)
        {
            Report(name, actual == null, "实际='" + (actual ?? "<null>") + "' 期望=<null>");
        }

        private static void CheckTrue(string name, bool actual)
        {
            Report(name, actual, "实际=" + actual + " 期望=True");
        }

        private static void Report(string name, bool ok, string detail)
        {
            _checks++;
            if (ok)
            {
                Console.WriteLine("  [PASS] " + name);
                return;
            }

            _failures++;
            Console.WriteLine("  [FAIL] " + name + "  (" + detail + ")");
        }
    }
}
