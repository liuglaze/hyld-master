using System;
using System.IO;
using System.Reflection;
using System.Text;
using Logging;
using PMNet;
using PMNet.Unity;
using UnityEngine;

namespace PMDsHostCheck
{
    /// <summary>
    /// 旧链退役（net-legacy-retirement-contract.md §C）后的 DS 宿主门禁。
    ///
    /// 两段检查，缺一不可：
    ///   A) 行为：用 HostStubs 的受控替身跑真实 PMDsHost.cs 的控制流 ——
    ///      缺 bootstrap 必须 Quit(1) 且不启动会话（= 不存在第二个监听面）；
    ///      有 bootstrap 时 Start 恰一次、Update 每帧 Pump 恰一次、退出请求后不再 Pump、
    ///      OnDestroy / OnApplicationQuit 重复到达时 Dispose 幂等。
    ///   B) 结构：读真实源码做 token 断言 —— 旧路由/裸 socket/线程/诊断 MainPack 不得复活，
    ///      run_ds 生成器必须要求 bootstrap（不得再诱导裸 -port 启动），GlueCheck 不再 include 已删目录。
    ///
    /// 边界：本门禁只证明 wrapper 自身的控制流与结构事实。真实 PMDsSessionHost 的装配与真实 Unity
    /// 运行期行为不在覆盖范围内（分别由 PMUnityGlueCheck / PMClientCheck 的编译面与用户实机验收承担）。
    /// </summary>
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static int Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch (Exception)
            {
                // 某些重定向/受限终端不允许改编码；不影响断言结果。
            }

            string repoRoot = ResolveRepoRoot(args);

            Console.WriteLine("===== PMDsHostCheck（旧链退役后的 DS 宿主 wrapper 门禁）=====");
            Console.WriteLine("仓库根目录 = " + repoRoot);
            Console.WriteLine();

            RunWrapperBehaviourChecks();
            RunExceptionChecks();
            RunStructuralChecks(repoRoot);

            Console.WriteLine();
            Console.WriteLine("===== 结果：通过 " + _passed + " / 失败 " + _failed + " =====");
            return _failed == 0 ? 0 : 1;
        }

        // =================================================================================
        //  A. wrapper 行为（受控替身）
        // =================================================================================

        private static void RunWrapperBehaviourChecks()
        {
            Console.WriteLine("---- A. wrapper 行为（HostStubs 受控替身，编真实 PMDsHost.cs）----");

            // A1：options 为 null
            ResetStubs();
            PMDsHost h1 = PMDsHost.Start(null);
            Check(Application.QuitCalls == 1 && Application.LastQuitCode == 1,
                "A1 options=null → Application.Quit(1) 恰一次");
            Check(PMDsSessionHost.StartCalls == 0,
                "A1 options=null → 不启动会话（StartCalls=0，无第二监听面）");
            Check(h1 != null && h1.SessionHost == null, "A1 options=null → wrapper 不持有会话宿主");
            Check(ReferenceEquals(PMDsHost.Instance, h1), "A1 wrapper 已登记为 static Instance");
            InvokePrivate(h1, "OnDestroy");
            Check(PMDsHost.Instance == null, "A1 OnDestroy → static Instance 收尾清空");

            // A2：有 options、但 bootstrap 为 null
            ResetStubs();
            PMDsHost h2 = PMDsHost.Start(new PMNetLaunchOptions { BootstrapPath = null, ListenPort = 7801 });
            Check(Application.QuitCalls == 1 && Application.LastQuitCode == 1,
                "A2 缺 -bootstrap（null）→ Application.Quit(1) 恰一次");
            Check(PMDsSessionHost.StartCalls == 0, "A2 缺 -bootstrap → 不启动会话（不监听端口）");
            Check(h2.SessionHost == null, "A2 缺 -bootstrap → wrapper 不持有会话宿主");
            InvokePrivate(h2, "OnDestroy");
            Check(PMDsHost.Instance == null, "A2 OnDestroy → static Instance 收尾清空");

            // A3：bootstrap 为空字符串同样视为缺失
            ResetStubs();
            PMDsHost h3 = PMDsHost.Start(new PMNetLaunchOptions { BootstrapPath = string.Empty });
            Check(Application.QuitCalls == 1 && Application.LastQuitCode == 1,
                "A3 缺 -bootstrap（空串）→ Application.Quit(1) 恰一次");
            Check(PMDsSessionHost.StartCalls == 0, "A3 缺 -bootstrap（空串）→ 不启动会话");
            InvokePrivate(h3, "OnDestroy");

            // B：有效 bootstrap → 启动恰一次 + 每帧 Pump
            ResetStubs();
            PMNetLaunchOptions valid = new PMNetLaunchOptions
            {
                BootstrapPath = @"D:\tmp\pmds-boot.bin",
                DsId = "ds-1",
                MatchId = "match-1",
                ListenPort = 7801,
            };
            PMDsHost hb = PMDsHost.Start(valid);
            Check(Application.QuitCalls == 0, "B1 有 bootstrap → 不退出进程");
            Check(PMDsSessionHost.StartCalls == 1, "B1 有 bootstrap → 会话 Start 恰一次");
            Check(hb.SessionHost != null && ReferenceEquals(hb.SessionHost, PMDsSessionHost.LastStarted),
                "B1 wrapper 持有刚启动的会话宿主");

            PMDsSessionHost session = PMDsSessionHost.LastStarted;
            Check(session != null && session.ExitRequested != null, "B1 wrapper 已挂上会话退出回调");

            InvokePrivate(hb, "Update");
            InvokePrivate(hb, "Update");
            Check(session.PumpCalls == 2, "B2 Update() 每帧恰 Pump 一次（不重复驱动 bridge）");

            // C：退出请求 → Quit(code) 且此后不再 Pump
            session.ExitRequested(7);
            Check(Application.QuitCalls == 1 && Application.LastQuitCode == 7,
                "C1 会话请求退出 → Application.Quit(7) 恰一次");
            InvokePrivate(hb, "Update");
            InvokePrivate(hb, "Update");
            Check(session.PumpCalls == 2, "C2 退出请求之后 Update 不再 Pump");
            session.ExitRequested(9);
            Check(Application.QuitCalls == 1, "C3 重复退出请求只生效一次");

            // D：幂等清理
            InvokePrivate(hb, "OnDestroy");
            Check(session.DisposeCalls == 1, "D1 OnDestroy → 会话 Dispose 恰一次");
            Check(PMDsHost.Instance == null, "D2 OnDestroy → static Instance 收尾清空");
            InvokePrivate(hb, "OnApplicationQuit");
            InvokePrivate(hb, "OnDestroy");
            Check(session.DisposeCalls == 1, "D3 OnDestroy/OnApplicationQuit 重复到达不重复 Dispose（幂等）");

            // E：会话启动失败 → 明确 Quit(1)，且清理仍幂等
            ResetStubs();
            PMDsSessionHost.FailNextStart = true;
            PMDsHost failHost = PMDsHost.Start(new PMNetLaunchOptions { BootstrapPath = @"D:\tmp\pmds-boot.bin" });
            Check(Application.QuitCalls == 1 && Application.LastQuitCode == 1,
                "E1 会话启动失败 → Application.Quit(1)");
            Check(failHost.SessionHost == null, "E2 启动失败 → wrapper 不持有会话宿主");
            InvokePrivate(failHost, "Update");
            Check(PMDsSessionHost.StartCalls == 1, "E3 启动失败后 Update 不重试启动");
            InvokePrivate(failHost, "OnDestroy");
            InvokePrivate(failHost, "OnApplicationQuit");
            InvokePrivate(failHost, "OnDestroy");
            Check(PMDsHost.Instance == null, "E4 失败路径清理幂等且不抛");
        }

        // =================================================================================
        //  B. 结构门（退役面不得复活）
        // =================================================================================

        private static void RunExceptionChecks()
        {
            ResetStubs();
            PMDsSessionHost.ThrowOnStart = true;
            PMDsHost failed = PMDsHost.Start(new PMNetLaunchOptions { BootstrapPath = "test.bin" });
            Check(Application.LastQuitCode == 1, "启动异常必须Quit1而非逃逸");
            InvokePrivate(failed, "OnDestroy");
            ResetStubs();
            PMDsHost host = PMDsHost.Start(new PMNetLaunchOptions { BootstrapPath = "test.bin" });
            PMDsSessionHost session = PMDsSessionHost.LastStarted;
            PMDsSessionHost.ThrowOnPump = true;
            InvokePrivate(host, "Update");
            Check(Application.LastQuitCode == 1, "Pump异常必须立即Quit1");
            InvokePrivate(host, "Update");
            Check(session.PumpCalls == 1, "异常退出后不得继续Pump");
            PMDsSessionHost.ThrowOnDispose = true;
            InvokePrivate(host, "OnDestroy");
            Check(PMDsHost.Instance == null && host.SessionHost == null, "Dispose异常仍摘引用");
            InvokePrivate(host, "OnApplicationQuit");
            Check(session.DisposeCalls == 1, "Dispose异常不重复释放");
        }

        private static void RunStructuralChecks(string repoRoot)
        {
            Console.WriteLine();
            Console.WriteLine("---- B. 结构门（退役面不得复活 / run_ds 必须要求 bootstrap）----");

            // 1) 旧路由层与只测它的工具源文件必须不存在
            Absent(repoRoot, "Client/Assets/Scripts/Server/Net/PMUdpRouter.cs");
            Absent(repoRoot, "Client/Assets/Scripts/Server/Net/PMUdpRouter.cs.meta");
            Absent(repoRoot, "Client/Assets/Scripts/Server/Net/PMSingleBattleRegistry.cs");
            Absent(repoRoot, "Client/Assets/Scripts/Server/Net/PMSingleBattleRegistry.cs.meta");
            Absent(repoRoot, "Tools/PMUdpRouterCheck/PMUdpRouterCheck.csproj");
            Absent(repoRoot, "Tools/PMUdpRouterTest/PMUdpRouterTest.csproj");
            Absent(repoRoot, "Tools/PMUdpRouterTest/Program.cs");
            Absent(repoRoot, "Tools/PMDsProbe/PMDsProbe.csproj");
            Absent(repoRoot, "Tools/PMDsProbe/Program.cs");

            // 2) PMDsHost 真实源码：代码里不得再有旧路由 / 裸 socket / 线程 / 诊断 MainPack
            string hostPath = "Client/Assets/Scripts/Server/Boot/PMDsHost.cs";
            string hostText = ReadText(repoRoot, hostPath);
            string hostCode = StripComments(hostText);

            string[] forbidden = new string[]
            {
                "System.Net",
                "Socket",
                "PMUdpRouter",
                "PMSingleBattleRegistry",
                "SocketProto",
                "MainPack",
                "System.Threading",
                "Thread",
                "InitializeRouter",
                "HandleDiagnosticBattlePacket",
                "DriveTick",
                "LogHeartbeat",
                "TryBindSocket",
                "OnReceive",
                "ScaffoldPongPayload",
                "BoundPort",
                "Pong",
            };

            for (int i = 0; i < forbidden.Length; i++)
            {
                Check(hostCode.IndexOf(forbidden[i], StringComparison.Ordinal) < 0,
                    "PMDsHost 代码不含退役符号 '" + forbidden[i] + "'");
            }

            string[] required = new string[]
            {
                "PMDsSessionHost",
                "BootstrapPath",
                ".Pump()",
                "Application.Quit(1)",
                "DontDestroyOnLoad",
                "Instance",
            };

            for (int i = 0; i < required.Length; i++)
            {
                Check(hostText.IndexOf(required[i], StringComparison.Ordinal) >= 0,
                    "PMDsHost 仍包含必需结构 '" + required[i] + "'");
            }

            // 3) run_ds 生成器：必须要求 bootstrap，且不得再保留「旧形态」裸 -port 分支
            string buildText = ReadText(repoRoot, "Client/Assets/Editor/PMDsBuild.cs");
            Check(buildText.IndexOf("-bootstrap", StringComparison.Ordinal) >= 0,
                "PMDsBuild 生成的 run_ds 含 -bootstrap");
            Check(buildText.IndexOf(":noboot", StringComparison.Ordinal) >= 0,
                "PMDsBuild 生成的 run_ds 有「缺 bootstrap 即拒绝」分支");
            Check(buildText.IndexOf("exit /b 1", StringComparison.Ordinal) >= 0,
                "拒绝分支以非 0 退出（exit /b 1）");
            Check(buildText.IndexOf("Lobby", StringComparison.Ordinal) >= 0,
                "run_ds 说明 DS 由 Lobby 编排拉起");
            Check(buildText.IndexOf("旧形态", StringComparison.Ordinal) < 0,
                "run_ds 不再保留「旧形态」裸 -port 启动分支");
            Check(buildText.IndexOf("goto newchain", StringComparison.Ordinal) < 0,
                "run_ds 不再有 newchain 旧分支跳转");

            // 4) GlueCheck 不再显式 include 已删除的 Server/Net 通配
            string glueText = ReadText(repoRoot, "Tools/PMUnityGlueCheck/PMUnityGlueCheck.csproj");
            Check(glueText.IndexOf(@"Server\Net\**", StringComparison.Ordinal) < 0,
                "PMUnityGlueCheck 不再 include Server/Net/**");
        }

        // =================================================================================
        //  基础设施
        // =================================================================================

        private static void ResetStubs()
        {
            Application.QuitCalls = 0;
            Application.LastQuitCode = int.MinValue;
            PMDsSessionHost.StartCalls = 0;
            PMDsSessionHost.FailNextStart = false;
            PMDsSessionHost.ThrowOnStart = PMDsSessionHost.ThrowOnPump = PMDsSessionHost.ThrowOnDispose = false;
            PMDsSessionHost.LastStarted = null;
            GameObject.CreatedCount = 0;
            HYLDDebug.Lines.Clear();
            HYLDDebug.FlushTraceCalls = 0;
        }

        private static void InvokePrivate(PMDsHost host, string methodName)
        {
            MethodInfo method = typeof(PMDsHost).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
            {
                Check(false, "找不到 PMDsHost." + methodName + "（wrapper 结构变了）");
                return;
            }

            try
            {
                method.Invoke(host, null);
            }
            catch (TargetInvocationException ex)
            {
                Check(false, "PMDsHost." + methodName + " 抛出：" + (ex.InnerException ?? ex).GetType().Name
                    + " " + (ex.InnerException ?? ex).Message);
            }
        }

        private static void Check(bool condition, string description)
        {
            if (condition)
            {
                _passed++;
                Console.WriteLine("  OK   " + description);
            }
            else
            {
                _failed++;
                Console.WriteLine("  FAIL " + description);
            }
        }

        private static void Absent(string repoRoot, string relativePath)
        {
            string full = ToFullPath(repoRoot, relativePath);
            Check(!File.Exists(full), "已删除：" + relativePath);
        }

        private static string ReadText(string repoRoot, string relativePath)
        {
            string full = ToFullPath(repoRoot, relativePath);
            if (!File.Exists(full))
            {
                Check(false, "缺文件（无法做结构断言）：" + relativePath);
                return string.Empty;
            }

            Check(true, "读到源码：" + relativePath);
            return File.ReadAllText(full);
        }

        private static string ToFullPath(string repoRoot, string relativePath)
        {
            return Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>
        /// 去掉 C# 注释（行注释 / 块注释），**保留**字符串与字符字面量。
        ///
        /// 为什么需要：结构门要判定的是「代码里有没有旧路由引用」，而 PMDsHost 的文档注释里会
        /// 正当地提到被删除的旧类名（这正是「为什么删」的说明）。不剥注释就会把说明文字当引用。
        ///
        /// 限制（已知且可接受）：不区分 verbatim 字符串（@"..."）的转义规则；本门禁扫描的两个文件都不含它。
        /// </summary>
        private static string StripComments(string source)
        {
            StringBuilder sb = new StringBuilder(source.Length);
            int i = 0;

            while (i < source.Length)
            {
                char c = source[i];

                if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
                {
                    while (i < source.Length && source[i] != '\n')
                    {
                        i++;
                    }

                    continue;
                }

                if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                    {
                        i++;
                    }

                    i = i + 2 <= source.Length ? i + 2 : source.Length;
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    sb.Append(c);
                    i++;

                    while (i < source.Length)
                    {
                        char d = source[i];
                        sb.Append(d);
                        i++;

                        if (d == '\\' && i < source.Length)
                        {
                            sb.Append(source[i]);
                            i++;
                            continue;
                        }

                        if (d == quote || d == '\n')
                        {
                            break;
                        }
                    }

                    continue;
                }

                sb.Append(c);
                i++;
            }

            return sb.ToString();
        }

        private static string ResolveRepoRoot(string[] args)
        {
            if (args != null && args.Length > 0 && !string.IsNullOrEmpty(args[0]))
            {
                return Path.GetFullPath(args[0]);
            }

            DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string marker = Path.Combine(dir.FullName, "Client", "Assets", "Scripts", "Server", "Boot", "PMDsHost.cs");
                if (File.Exists(marker))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            // 兜底：<repo>/Tools/PMDsHostCheck/bin/<cfg>/net8.0 → 上 6 级即仓库根。
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
        }
    }
}
