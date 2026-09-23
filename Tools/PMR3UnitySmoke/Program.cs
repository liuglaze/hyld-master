// R3-B6：真实 Unity DS 进程烟测（契约 Docs/plans/net-r3-control-contract.md §7.3/§7.4 + §8）。
//
// 与 Tools/PMR3IntegrationTest 的唯一区别：**进程不再是替身**。
//   - 真实 PMDsLobbyHost 产出控制启动参数与引导文件；
//   - 真实 PMDsSystemProcessLauncher 拉起固定白名单里的 D:\UGit\hyld-master\HyldDS\HyldDS.exe
//     （Unity 2019.4 真无头包，身份由 -server/-bootstrap 在运行期判定）；
//   - 唯一被包装的缝是「参数尾部追加」：-server-smoke（显式 smoke 验收）与绝对 -logFile
//     （把真实 Unity 宿主日志落到本工具 artifacts）。接口仍是 IPMDsProcessLauncher，
//     不伪造、不改 exe/工作目录/端口/bootstrap。
//
// 客户端侧**不是 Unity，也不是 UI**：两个纯 C# 测试客户端各自装配真实的
//   PMSession / PMNetWorld / PMNetSessionBridge / PMUdpSessionEndpoint.OpenClient / PMR3Runtime，
// 用真实 UDP 走真实握手与票据验证，各发一次 owner ServerProbe（经声明层入口 ServerProbe），
// 观察 ClientEcho / ProbeCount / 两个副本。真实 UnityUI 分流（UIMatchingPanel 的 PMDS1: 分支）
// 与真实 Application.Quit 收尾不在本工具范围（属 T42 的 UI/跨机待办）。
//
// 时钟：真实系统时钟（不虚拟化）。总预算 120 秒，轮询步进 ≤50ms，不长 sleep。
// 端口：control 用系统临时端口；本局 UDP 端口取随机高位段；绝不用 7777/7778/7800。
// 收尾：finally 只回收本工具自己启动的 DS 进程与其临时端口，绝不触碰用户进程/服务。
//
// ---------------------------------------------------------------------------------
// 模式（默认 smoke；两种模式共用同一条真实进程/真实 UDP 闭环，只是插入不同阶段）：
//   （默认）     原 R3-B6 烟测：Ready → offer → 两客户端入局 → 探针/回声 → smoke 结果
//                → ResultAck → 认证 Exited(0) → OS 退出 0 → 端口/uid 回收。
//                `--hold-seconds N` 暂缓探针做真实长局；`--drop-control-after-ready`
//                停控制下行验证 DS 非 0 退出。三者互不改变默认行为。
//   --movement  在**探针开闸之前**插入 R4-B 真实 DS 权威运动验收（见 RunMovementPhase）：
//                用**真实生成的运动 input RPC**（ServerMovementInputV1）驱动真实
//                Unity DS，逐项验收：Create 初值非空 / 每客户端 1 AP + 1 SP / 权威位移 /
//                撞墙停在 ±0.9 不穿墙 / Space 起跳峰值 > 1.5 后落回 y≈1 且 Walking /
//                owner 可靠 Mode+Landed 事件各 kind 到达且 Key 去重 / 对手 SP 副本收到
//                运动快照与最终状态 / 重同步升流后旧流输入无效且新流继续推进。
//                验收完再开闸发探针，让**原 smoke 闭环照旧**收尾。
//
//                --movement 的诚实口径（不得误读）：
//                  · **权威**来自真实 Unity DS（PhysX 隔离物理场景）；断言读的是复制下来的
//                    `_movementSnapshotV1` 原始载荷与 owner 可靠事件，**不读**客户端预测；
//                  · 客户端 AP 的碰撞世界是 R4-A 的确定性 AABB 替身（PMMoverTestWorld，
//                    WorldVersion 被包装成 1 以匹配 DS 的 MovementWorldVersion=1），
//                    它**不是** PhysX，只让 AP 能产出行输入并做回滚重放；
//                  · 节拍用真实 Stopwatch 墙钟，输入 dt 恒 16ms；DS 的仿真预算由它自己的
//                    墙钟累计，本工具**不**虚拟加速、不代为步进 DS；
//                  · --movement 与 --hold-seconds / --drop-control-after-ready 互斥（组合即失败）。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PMNet;
using PMNet.Control;
using PMNet.Mover;
using PMNet.Prediction;
using PMNet.R3;
using PMNet.Session;

namespace PMR3UnitySmoke
{
    internal static class Program
    {
        /// <summary>名册第 1 名玩家 uid（uid/PlayerId 只来自票据里的认证身份）。</summary>
        internal const int UidA = 301;

        /// <summary>名册第 2 名玩家 uid。</summary>
        internal const int UidB = 302;

        /// <summary>轮询步进（毫秒）。契约要求「每步 ≤50ms，不长 sleep」。</summary>
        internal const int PollIntervalMs = 50;

        /// <summary>本工具的总时间上限（毫秒）。真实 Unity 启动 + 握手 + smoke + 退出都在这之内。</summary>
        internal const int TotalBudgetMs = 120000;

        /// <summary>默认 hold 秒数：0 = 保持原快速模式（不延后探针）。</summary>
        internal const int DefaultHoldSeconds = 0;

        /// <summary>
        /// DS 侧 <c>PMDsSessionHost.SmokeDeadlineMs</c> 的**只读副本**（本工具不编译需要 UnityEngine 的宿主，
        /// 故按源码常量核对）。hold 必须显著小于它，否则真实 DS 的 smoke 验收期会到期失败；
        /// 本工具**只限制自己的 hold**，不改动生产常量来绕过。
        /// </summary>
        internal const int DsSmokeDeadlineMs = 120000;

        /// <summary>hold 之外还要留给「启动 + 握手 + 复制 + 结果 + 退出」的安全余量（毫秒）。</summary>
        internal const int HoldSafetyMarginMs = 60000;

        /// <summary>故障模式：真实 Lobby 停止控制下行的时长上限（毫秒）。</summary>
        internal const int DropControlBlackoutLimitMs = 25000;

        /// <summary>固定白名单：唯一允许被拉起的真实 DS 可执行文件（绝对路径，永不来自客户端输入）。</summary>
        internal const string DsExecutablePath = @"D:\UGit\hyld-master\HyldDS\HyldDS.exe";

        /// <summary>DS 工作目录（Unity 数据目录就在它下面）。</summary>
        internal const string DsWorkingDirectory = @"D:\UGit\hyld-master\HyldDS";

        /// <summary>真实 DS 的托管程序集（预检对象：mtime/size/sha256 + 符号）。</summary>
        internal const string DsAssemblyPath = @"D:\UGit\hyld-master\HyldDS\HyldDS_Data\Managed\Assembly-CSharp.dll";

        /// <summary>真实 DS 程序集必须含有的关键符号（R3-B 宿主接线的构建指纹）。</summary>
        private static readonly string[] RequiredDsSymbols = new string[]
        {
            "PMDsSessionHost",
            "PMUdpSessionEndpoint",
            "PMR3Player",
            "PMDsLobbyAgent",
        };

        /// <summary>
        /// <c>--movement</c> 额外要求的 DS 符号：只有**重新 build 过**（含 R4-B 运动接线与
        /// PhysX 零距离命中修复）的真实 DS 才可能同时含这些名字。
        /// 这是「不得拿旧包验新代码」的构建期预检，不是运行期桩件。
        /// </summary>
        private static readonly string[] RequiredDsMovementSymbols = new string[]
        {
            "PMR4MovementDriver",
            "PMR4MovementCodec",
            "PMUnityMoverCollisionQuery",
            "MovementWorldVersion",
            "BuildIsolated",
            "PendingServerFrame",
            "ZeroDistanceContactsIgnored",
            "ServerMovementInputV1",
            "ClientMovementEventsV1",
        };

        /// <summary>
        /// DS 的 R4-B 运动碰撞世界版本（<c>PMDsSessionHost.MovementWorldVersion</c> 的只读副本）。
        /// 本工具不编译需要 UnityEngine 的宿主，故按源码常量核对；两侧必须同值。
        /// </summary>
        internal const int MovementCollisionWorldVersion = 1;

        /// <summary>运动上行输入的整毫秒 dt（契约 1..50；本工具固定 16ms ≈ 60Hz）。</summary>
        internal const int MovementInputStepMs = 16;

        private static readonly List<string> _failures = new List<string>();
        private static int _checks;
        private static int _passed;
        private static long _startedAtMs;

        private static int Main(string[] args)
        {
            _startedAtMs = NowMs();

            Console.WriteLine("=== R3-B6：真实 Unity DS 进程烟测（真实 PMDsLobbyHost + 真实 HyldDS.exe + 真实 UDP 两客户端）===");
            Console.WriteLine();

            SmokeRun run = null;

            try
            {
                string artifactsRoot = Path.Combine(AppRootDirectory(), "artifacts",
                    DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-"
                    + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(artifactsRoot);

                Progress("artifacts=" + artifactsRoot);

                // 生成表必须先注册：ProtocolHash / ClassId / 生成桩的唯一来源。
                PMR3Runtime.Register();

                run = new SmokeRun(artifactsRoot, args);
                run.Execute();
            }
            catch (Exception ex)
            {
                Fail("工具出现未捕获异常：" + ex.GetType().Name + " " + ex.Message
                     + Environment.NewLine + ex.StackTrace);
            }
            finally
            {
                if (run != null)
                {
                    try { run.Dispose(); }
                    catch (Exception) { }
                }

                try { PMR3Runtime.Shutdown(); }
                catch (Exception) { }

                if (run != null)
                {
                    try { run.WriteSummaryFile(); }
                    catch (Exception) { }
                }
            }

            Console.WriteLine();
            Console.WriteLine("  总耗时 " + (NowMs() - _startedAtMs) + "ms（上限 " + TotalBudgetMs + "ms）");

            if (_failures.Count == 0)
            {
                Console.WriteLine("  全部通过：" + _checks + " 项检查，0 项失败");
                return 0;
            }

            Console.WriteLine("  " + _passed + " 项通过，" + _failures.Count + " 项失败：");
            for (int i = 0; i < _failures.Count; i++)
            {
                Console.WriteLine("    - " + _failures[i]);
            }

            return 1;
        }

        // =================================================================================
        //  断言 / 日志
        // =================================================================================

        internal static void Check(bool ok, string label)
        {
            _checks++;
            if (ok) { _passed++; } else { _failures.Add(label); }
            Console.WriteLine((ok ? "    OK   " : "    FAIL ") + label);
            Console.Out.Flush();
        }

        internal static void CheckEq(object actual, object expected, string label)
        {
            Check(Equals(actual, expected), label + "（实际 " + (actual == null ? "<null>" : actual.ToString())
                + "，期望 " + (expected == null ? "<null>" : expected.ToString()) + "）");
        }

        internal static void CheckGt(long actual, long bound, string label)
        {
            Check(actual > bound, label + "（实际 " + actual + "，要求 > " + bound + "）");
        }

        internal static void Fail(string label)
        {
            Check(false, label);
        }

        internal static void Section(string name)
        {
            Console.WriteLine("── " + name);
            Console.Out.Flush();
        }

        internal static void Progress(string message)
        {
            Console.WriteLine("    ... [" + (NowMs() - _startedAtMs) + "ms] " + message);
            Console.Out.Flush();
        }

        internal static int FailureCount { get { return _failures.Count; } }

        internal static int CheckCount { get { return _checks; } }

        internal static int PassedCount { get { return _passed; } }

        internal static string[] FailureSnapshot()
        {
            return _failures.ToArray();
        }

        // =================================================================================
        //  基础工具
        // =================================================================================

        internal static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        internal static string ArgValue(string[] args, string key)
        {
            if (args == null)
            {
                return null;
            }

            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (string.Equals(args[i], key, StringComparison.Ordinal))
                {
                    return args[i + 1];
                }
            }

            return null;
        }

        internal static bool HasFlag(string[] args, string key)
        {
            if (args == null)
            {
                return false;
            }

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], key, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        internal static int ParseInt(string text)
        {
            int value;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        /// <summary>在字节流里找 ASCII 串（.NET 元数据 #Strings 堆是 UTF-8，类型名可直接搜）。</summary>
        internal static bool ContainsAscii(byte[] data, string text)
        {
            if (data == null || text == null || text.Length == 0 || data.Length < text.Length)
            {
                return false;
            }

            for (int i = 0; i + text.Length <= data.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < text.Length; j++)
                {
                    if ((char)data[i + j] != text[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>读文本尾部（最多 maxBytes）。写入中的文件用 FileShare.ReadWrite|Delete 打开，失败返回 null。</summary>
        internal static string ReadTextTail(string path, int maxBytes)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    long length = stream.Length;
                    long start = length > maxBytes ? length - maxBytes : 0L;
                    stream.Seek(start, SeekOrigin.Begin);

                    byte[] buffer = new byte[length - start];
                    int read = 0;
                    while (read < buffer.Length)
                    {
                        int n = stream.Read(buffer, read, buffer.Length - read);
                        if (n <= 0) { break; }
                        read += n;
                    }

                    return Encoding.UTF8.GetString(buffer, 0, read);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static string Sha256OfFile(string path)
        {
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(stream);
                    StringBuilder builder = new StringBuilder(hash.Length * 2);
                    for (int i = 0; i < hash.Length; i++)
                    {
                        builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                    }

                    return builder.ToString();
                }
            }
            catch (Exception ex)
            {
                return "<hash 失败: " + ex.GetType().Name + ">";
            }
        }

        /// <summary>
        /// 独占绑定探测：<c>ExclusiveAddressUse=true</c> 的 UDP socket。
        /// 端口被任何进程持有时 bind 必然失败；空闲时必然成功。
        /// 因此它同时是「DS 真的占着这个端口」与「退出后端口真的被 OS 归还」的证据。
        /// </summary>
        internal static bool TryExclusiveUdpBind(int port, out string error)
        {
            error = null;
            Socket socket = null;
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.ExclusiveAddressUse = true;
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                if (socket != null)
                {
                    try { socket.Close(); } catch (Exception) { }
                }
            }
        }

        internal static string AppRootDirectory()
        {
            // net8.0 输出在 Tools/PMR3UnitySmoke/bin/<Config>/net8.0/ ⇒ 回溯到含工程文件的目录。
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                if (File.Exists(Path.Combine(dir, "PMR3UnitySmoke.csproj"))
                    || File.Exists(Path.Combine(dir, "Program.cs")))
                {
                    return dir;
                }

                dir = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
            }

            return AppContext.BaseDirectory;
        }

        /// <summary>参数展示串：逐项引用，便于人工核对（参数里只有引导文件路径，没有票据/密钥）。</summary>
        internal static string DescribeArguments(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return "<none>";
            }

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) { builder.Append(' '); }
                builder.Append('"').Append(args[i]).Append('"');
            }

            return builder.ToString();
        }

        internal static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "x";
            }

            StringBuilder builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                    || c == '_' || c == '-' || c == '.';
                builder.Append(ok ? c : '_');
            }

            return builder.ToString();
        }

        // =================================================================================
        //  --movement：客户端侧运动碰撞世界（显式声明不是 PhysX）
        // =================================================================================

        /// <summary>
        /// <c>--movement</c> 专用：把 R4-A 的确定性 AABB 替身（<see cref="PMMoverTestWorld"/>）原样委托出去，
        /// **只把 WorldVersion 覆盖成 <see cref="MovementCollisionWorldVersion"/>=1**。
        ///
        /// 为什么必须覆盖：真实 Unity DS 的 <c>PMDsSessionHost.MovementWorldVersion</c>=1
        /// （<c>PMUnityMoverCollisionQuery</c>（真实 PhysX）构造时写死 1），而 <see cref="PMMoverTestWorld"/>
        /// 的 WorldVersion 沿用 R3 冻结碰撞摘要 0x52334201。若客户端直接用后者，
        /// <c>PMR4MovementDriver.CreateFromPlayerInitialSnapshot</c> 会以
        /// 「初值快照的碰撞世界版本(1) 与本端(0x52334201) 不符」拒绝建 Driver。
        ///
        /// 诚实边界：本替身**不是** Unity PhysX，也不代表任何真实地图。客户端 AP 的
        /// 预测/回滚重放只用于产出行输入；所有权威位置/模式/事件断言都读真实 Unity DS
        /// 复制下来的载荷，不读本替身的预测。
        /// </summary>
        private sealed class MovementCollisionStub : IPMMoverCollisionQuery
        {
            private readonly PMMoverTestWorld _inner = new PMMoverTestWorld();

            /// <summary>与 DS 侧 <c>MovementWorldVersion</c> 同值，否则 Driver 会拒绝建立。</summary>
            public int WorldVersion { get { return MovementCollisionWorldVersion; } }

            /// <summary>被委托的确定性替身（仅供诊断：它是 AABB 替身，不是 PhysX）。</summary>
            public PMMoverTestWorld Inner { get { return _inner; } }

            public PMMoverHit Sweep(PMVector3 position, PMVector3 delta, float radius, float halfHeight)
            {
                return _inner.Sweep(position, delta, radius, halfHeight);
            }

            public PMMoverGround QueryGround(PMVector3 position, float radius, float halfHeight, float distance)
            {
                return _inner.QueryGround(position, radius, halfHeight, distance);
            }
        }

        // =================================================================================
        //  真实进程启动器：唯一被包装的缝 = 参数尾部追加（不改接口、不伪造）
        // =================================================================================

        /// <summary>
        /// 薄包装 <see cref="PMDsSystemProcessLauncher"/>：把 Lobby 产出的启动请求**原样**交给真实启动器，
        /// 只在其参数尾部追加两个显式项：
        ///   - <c>-server-smoke</c>：DS 侧的显式自动验收开关（全部名册玩家完成探针后提交 smoke 结果）；
        ///   - <c>-logFile &lt;绝对路径&gt;</c>：把真实 Unity 宿主日志落到本工具 artifacts（否则无头包日志落在
        ///     LocalLow 的默认位置，事后无法与本次运行对齐）。
        /// exe / 工作目录 / <c>-port</c> / <c>-bootstrap</c> / <c>-control</c> / <c>-dsid</c> / <c>-matchid</c>
        /// 一律来自真实 <see cref="PMDsLobbyHost"/>，本类不生成也不替换。
        /// </summary>
        private sealed class SmokeLauncher : IPMDsProcessLauncher
        {
            private readonly PMDsSystemProcessLauncher _inner;
            private readonly string _artifactsRoot;
            private int _sequence;

            public readonly List<PMDsProcessLaunchRequest> Requests = new List<PMDsProcessLaunchRequest>();
            public readonly List<IPMDsProcess> Processes = new List<IPMDsProcess>();
            public readonly List<string> LogPaths = new List<string>();
            public string LastFaultDetail;

            /// <summary>
            /// 真实 OS 进程退出观测（<c>Process.Exited</c> 由 OS 生命周期触发，带真实退出码）。
            ///
            /// 为什么必须在这里抢着记：真实协调器在终态收尾成功后会 <c>_process.Dispose()</c>
            /// （`PMDsCoordinator.ReleaseResources`），之后再问 `IsRunning`/`ExitCode` 拿到的是
            /// 「对象已释放」而不是「进程还在」。所以退出码在事件里抓，不在事后读。
            /// </summary>
            public int ExitObservations;
            public int LastExitCode = int.MinValue;
            public long ExitObservedAtMs;

            public SmokeLauncher(PMDsSystemProcessLauncher inner, string artifactsRoot)
            {
                _inner = inner;
                _artifactsRoot = artifactsRoot;
            }

            public bool TryStart(PMDsProcessLaunchRequest request, out IPMDsProcess process,
                out PMDsProcessFault fault, out string detail)
            {
                process = null;
                fault = PMDsProcessFault.None;
                detail = null;

                if (request == null)
                {
                    fault = PMDsProcessFault.NullRequest;
                    detail = "启动请求为 null";
                    LastFaultDetail = detail;
                    return false;
                }

                PMDsProcessLaunchRequest effective = request.Clone();

                string dsId = ArgValue(effective.Arguments, "-dsid");
                int sequence = ++_sequence;
                string logPath = Path.Combine(_artifactsRoot,
                    "hyldds-" + sequence.ToString(CultureInfo.InvariantCulture) + "-"
                    + Sanitize(dsId) + ".log");

                List<string> args = new List<string>(effective.Arguments ?? new string[0]);
                args.Add("-server-smoke");
                args.Add("-logFile");
                args.Add(logPath);
                effective.Arguments = args.ToArray();

                Requests.Add(effective);
                LogPaths.Add(logPath);

                bool ok = _inner.TryStart(effective, out process, out fault, out detail);
                LastFaultDetail = detail;

                if (ok && process != null)
                {
                    // 尽早在真实进程对象上挂退出观测：协调器收尾后会 Dispose 它。
                    process.Exited += delegate(int exitCode)
                    {
                        ExitObservations++;
                        LastExitCode = exitCode;
                        ExitObservedAtMs = NowMs();
                    };

                    Processes.Add(process);
                }

                return ok;
            }
        }

        // =================================================================================
        //  客户端网关（冻结缝）：宿主字段 → 真实 PMDsEntryCodec → 客户端严格解码
        // =================================================================================

        /// <summary>
        /// 大厅与「已认证客户端」之间的缝。这里只把宿主产出的字段填进冻结的 <c>PMDsEntryOffer</c>，
        /// 用**真实** <see cref="PMDsEntryCodec"/> 编码成 `MainPack.Str` 文本，
        /// 再按客户端入口的同一严格路径 <c>TryDecode</c>（等价 UIMatchingPanel 的 `PMDS1:` 分流）。
        /// 生产实现（Server 侧）用同一个 codec，因此本工具与生产在字节层一致。
        /// </summary>
        private sealed class EnvClientGateway : IPMDsLobbyClientGateway
        {
            public readonly HashSet<int> Online = new HashSet<int>();
            public readonly Dictionary<int, string> EntryTexts = new Dictionary<int, string>();
            public readonly Dictionary<int, PMDsEntryOffer> Offers = new Dictionary<int, PMDsEntryOffer>();
            public readonly List<PMDsLobbyResultNotice> Results = new List<PMDsLobbyResultNotice>();
            public readonly List<int> Restored = new List<int>();
            public long OfferFailures;
            public string LastOfferError;

            public bool IsClientAuthenticated(int uid)
            {
                return Online.Contains(uid);
            }

            public bool TrySendEntryOffer(PMDsLobbyEntryNotice notice, out string error)
            {
                error = null;

                if (!Online.Contains(notice.Identity.Uid))
                {
                    OfferFailures++;
                    error = "客户端不在线（uid=" + notice.Identity.Uid + "）";
                    LastOfferError = error;
                    return false;
                }

                PMDsEntryOffer offer = new PMDsEntryOffer();
                offer.MatchId = notice.MatchId;
                offer.DsId = notice.DsId;
                offer.Host = notice.Host;
                offer.Port = notice.Port;
                offer.Epoch = notice.Epoch;
                offer.ProtocolHash = notice.ProtocolHash;
                offer.CollisionDigest = notice.CollisionDigest;
                offer.Identity = notice.Identity;
                offer.Ticket = notice.Ticket;

                string text;
                try
                {
                    text = PMDsEntryCodec.Encode(offer);
                }
                catch (Exception ex)
                {
                    OfferFailures++;
                    error = "真实 PMDsEntryCodec.Encode 失败：" + ex.GetType().Name + " " + ex.Message;
                    LastOfferError = error;
                    return false;
                }

                if (text == null || text.IndexOf(PMDsEntryCodec.Prefix, StringComparison.Ordinal) != 0)
                {
                    OfferFailures++;
                    error = "编码结果缺少 PMDS1: 前缀";
                    LastOfferError = error;
                    return false;
                }

                PMDsEntryOffer parsed;
                string decodeError;
                if (!PMDsEntryCodec.TryDecode(text, out parsed, out decodeError))
                {
                    OfferFailures++;
                    error = "客户端严格解码失败：" + decodeError;
                    LastOfferError = error;
                    return false;
                }

                EntryTexts[notice.Identity.Uid] = text;
                Offers[notice.Identity.Uid] = parsed;
                return true;
            }

            public void NotifyMatchEnded(PMDsLobbyResultNotice notice)
            {
                Results.Add(notice);
            }

            public void RestorePlayerOnline(int uid)
            {
                Restored.Add(uid);
            }
        }

        // =================================================================================
        //  客户端侧测试宿主（**纯 C#，非 Unity、非 UI**）
        // =================================================================================

        /// <summary>
        /// 客户端测试宿主：按 <c>PMClientSessionHost.Enter/PumpActive</c> 的**非 Unity 核心**装配
        /// （校验协议/碰撞摘要 → 世界/桥/Attach → 真实 OpenClient → 本端 owner 副本经生成桩发一次探针）。
        /// 它没有 GameObject / 没有 UI / 不渲染——真实 UnityUI 分流属 T42。
        /// 探针只在宿主帧步里发（不在复制回调里发），避免重入桥。
        /// </summary>
        private sealed class ClientHarness : IDisposable
        {
            public PMDsEntryOffer Offer;
            public PMSession Session;
            public PMNetWorld World;
            public PMNetSessionBridge Bridge;
            public PMUdpSessionEndpoint Endpoint;

            public readonly List<PMR3Player> PendingProbes = new List<PMR3Player>();
            public readonly Dictionary<int, PMR3Player> ReplicasByUid = new Dictionary<int, PMR3Player>();
            public readonly List<string> Failures = new List<string>();
            public int ProbeNonceSent;
            public int LastProbeNonce;

            /// <summary>
            /// <c>--movement</c>：Create 时刻的<b>运动初值载荷</b>（按 uid 快照一份）。
            /// 用于证明「DS 在首次生命周期 Flush 之前调了 PublishInitialSnapshot」⇒
            /// Create 记录本身就带非空初值，而不是靠事后重同步补。
            /// </summary>
            public bool CaptureMovementInitialPayload;

            /// <summary>uid → Create 时刻的运动快照原始字节（深拷贝，不受后续复制更新影响）。</summary>
            public readonly Dictionary<int, byte[]> MovementInitialPayloadByUid = new Dictionary<int, byte[]>();

            /// <summary>
            /// 探针闸门：false 时**保留**待发探针但不发送（hold / 故障模式期间必须零探针，
            /// 否则真实 DS 的 <c>-server-smoke</c> 会在看到名册全部探针后立刻提交结果、结束闭环）。
            /// 只影响「是否发探针」，不改复制/泵路径。
            /// </summary>
            public bool ProbeGateOpen = true;

            private bool _disposed;

            public static ClientHarness Enter(PMDsEntryOffer offer, List<string> warnings)
            {
                if (offer.ProtocolHash != PMR3Runtime.ProtocolHash)
                {
                    throw new InvalidOperationException("客户端拒绝入局：协议摘要不一致（不回退旧链）");
                }

                if (offer.CollisionDigest != 0u && offer.CollisionDigest != PMR3Runtime.CollisionDigest)
                {
                    throw new InvalidOperationException("客户端拒绝入局：碰撞摘要不一致（不回退旧链）");
                }

                ClientHarness harness = new ClientHarness();
                harness.Offer = offer;
                harness.Session = new PMSession(offer.Epoch, false);
                harness.World = new PMNetWorld(harness.Session);
                harness.World.Warn = delegate(string m)
                {
                    Record(warnings, "[client.world uid=" + offer.Identity.Uid + "] " + m);
                };
                harness.Bridge = new PMNetSessionBridge(harness.World);
                harness.Bridge.Warn = delegate(string m)
                {
                    Record(warnings, "[client.bridge uid=" + offer.Identity.Uid + "] " + m);
                };

                PMR3Runtime.Attach(harness.World, harness.Bridge);

                harness.Endpoint = PMUdpSessionEndpoint.OpenClient(offer, harness.Bridge);
                harness.Endpoint.Failed += delegate(string reason) { harness.Failures.Add(reason); };
                return harness;
            }

            private static void Record(List<string> warnings, string message)
            {
                if (warnings.Count < 4000)
                {
                    warnings.Add(message);
                }
            }

            /// <summary>复制回调：只入队/记账，**不发包**（发送发生在宿主帧步里）。</summary>
            public void OnReplicated(PMR3Player player)
            {
                if (player == null)
                {
                    return;
                }

                ReplicasByUid[player.Uid] = player;

                if (CaptureMovementInitialPayload && !MovementInitialPayloadByUid.ContainsKey(player.Uid))
                {
                    byte[] payload = player.MovementSnapshotPayload;
                    if (payload != null && payload.Length > 0)
                    {
                        byte[] copy = new byte[payload.Length];
                        Buffer.BlockCopy(payload, 0, copy, 0, payload.Length);
                        MovementInitialPayloadByUid[player.Uid] = copy;
                    }
                }

                if (player.Role != PMNetRole.AutonomousProxy)
                {
                    return;   // 别人的副本（SimulatedProxy）
                }

                if (player.Uid != Offer.Identity.Uid)
                {
                    return;   // 角色说是我的、uid 说不是：不发送（与真实宿主同判据）
                }

                if (player.ProbeSentCount > 0)
                {
                    return;
                }

                for (int i = 0; i < PendingProbes.Count; i++)
                {
                    if (ReferenceEquals(PendingProbes[i], player))
                    {
                        return;
                    }
                }

                PendingProbes.Add(player);
            }

            public void Pump(long nowMs, long nowUnixSeconds)
            {
                if (_disposed)
                {
                    return;
                }

                Endpoint.Pump(nowMs, nowUnixSeconds);
                SendPendingProbes();
            }

            private void SendPendingProbes()
            {
                if (!ProbeGateOpen)
                {
                    return;   // hold / 故障模式：保留 PendingProbes，等开闸后再发（不丢、不提前结束 smoke）
                }

                if (PendingProbes.Count == 0)
                {
                    return;
                }

                for (int i = 0; i < PendingProbes.Count; i++)
                {
                    PMR3Player player = PendingProbes[i];
                    if (player == null || player.ProbeSentCount > 0)
                    {
                        continue;
                    }

                    if (player.State != PMNetObjectState.Active)
                    {
                        continue;
                    }

                    ProbeNonceSent++;
                    LastProbeNonce = ProbeNonceSent;
                    player.ProbeSentCount++;

                    // 经**声明层入口**发（普通名 ServerProbe），不手写业务状态包（契约 §7.4）。
                    player.ServerProbe(LastProbeNonce);
                }

                PendingProbes.Clear();
            }

            public PMR3Player OwnReplica
            {
                get
                {
                    PMR3Player player;
                    return ReplicasByUid.TryGetValue(Offer.Identity.Uid, out player) ? player : null;
                }
            }

            public PMR3Player FindReplica(int uid)
            {
                PMR3Player player;
                return ReplicasByUid.TryGetValue(uid, out player) ? player : null;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                if (Endpoint != null)
                {
                    try { Endpoint.Dispose(); }
                    catch (Exception) { }
                    Endpoint = null;
                }

                if (World != null)
                {
                    PMR3Runtime.Detach(World);
                }

                if (Bridge != null)
                {
                    try { Bridge.Dispose(); }
                    catch (Exception) { }
                    Bridge = null;
                }

                PendingProbes.Clear();
                ReplicasByUid.Clear();
                MovementInitialPayloadByUid.Clear();
                World = null;
                Session = null;
            }
        }

        // =================================================================================
        //  真实闭环
        // =================================================================================

        private sealed class SmokeRun : IDisposable
        {
            private readonly string _artifactsRoot;
            private readonly string _bootstrapRoot;
            private readonly List<string> _warnings = new List<string>();
            private readonly List<string> _timeline = new List<string>();
            private readonly List<ClientHarness> _clients = new List<ClientHarness>();
            private readonly SmokeLauncher _launcher;
            private readonly EnvClientGateway _gateway = new EnvClientGateway();

            private PMDsLobbyHost _host;
            private PMR3Player _clientAOwn;
            private string _matchId;
            private string _dsId;
            private int _dsPort = -1;
            private int _controlPort = -1;
            private string _bootstrapPath;
            private string _dsLogPath;
            private string _lastArgsText;
            private int _dsPid = -1;
            private long _deadlineMs;
            private long _lastProgressMs;
            private long _dsLogReadAtMs;
            private string _dsLogCache;
            private string _midRunPortProbeError;
            private string _postExitPortProbeError;
            private byte[] _resultSummary;
            private bool _sawExitedTerminal;
            private bool _sawProcessExit;
            private bool _sawRelease;
            private int _osExitCode = int.MinValue;
            private string _lobbyTerminalReason;
            private string _dllMtime;
            private long _dllSize;
            private string _dllSha256;
            private string _movementSymbolRecord;

            /// <summary>--movement 的总计数/流代次摘要（写入 summary.txt，便于主侧对账）。</summary>
            private long _movementApTicksAccepted;
            private long _movementApTicksRejected;
            private string _movementStreamSummary = "<none>";
            private int _launchPidFromHost = -1;
            private bool _disposed;

            /// <summary>--hold-seconds 配置值（0 = 原快速模式）。</summary>
            private readonly int _holdSeconds;

            /// <summary>--drop-control-after-ready：可选的「停控制下行」故障模式。</summary>
            private readonly bool _dropControlAfterReady;

            /// <summary>--movement：在探针开闸前插入 R4-B 真实 DS 权威运动验收。</summary>
            private readonly bool _movementMode;

            /// <summary>--movement 的两个客户端运动观察台（探针开闸前建立与释放）。</summary>
            private readonly List<MovementRig> _movementRigs = new List<MovementRig>();

            private long _holdStartMs;
            private long _holdEndMs;
            private long _holdMilliseconds;
            private string _holdStartWall;
            private string _holdEndWall;
            private long _holdHeartbeats;
            private long _holdHeartbeatReplies;
            private int _holdAliveSamples;
            private int _holdDsLogHeartbeatTicks;
            private long _holdDsMaxHbRx = -1L;
            private long _dropStartMs;
            private long _exitedTerminalAtMs;
            private long _osExitObservedAtMs;
            private long _killRequestsAtTerminal;
            private long _deferredCleanupTicksAtTerminal;

            /// <summary>终态时的优雅退出宽限证据（真实 DS 路径：Exited(0) 后 Unity 收尾，Lobby 不得强杀）。</summary>
            private long _gracefulExitObservedAtTerminal;
            private long _gracefulExitWaitsAtTerminal;
            private long _gracefulExitTimeoutsAtTerminal;
            private long _dropBlackoutMs;
            private int _dropExitCode = int.MinValue;

            public SmokeRun(string artifactsRoot, string[] args)
            {
                _artifactsRoot = artifactsRoot;
                _bootstrapRoot = Path.Combine(artifactsRoot, "bootstrap");
                Directory.CreateDirectory(_bootstrapRoot);
                _deadlineMs = NowMs() + TotalBudgetMs;
                _lastProgressMs = NowMs();

                // 默认快速模式（hold=0）保持不变；--hold-seconds N 才启用真实长局延后。
                _holdSeconds = ParseInt(ArgValue(args, "--hold-seconds"));
                if (_holdSeconds <= 0)
                {
                    _holdSeconds = ParseInt(ArgValue(args, "-hold-seconds"));
                }

                if (_holdSeconds < 0)
                {
                    _holdSeconds = 0;
                }

                _dropControlAfterReady = HasFlag(args, "--drop-control-after-ready")
                    || HasFlag(args, "-drop-control-after-ready");

                _movementMode = HasFlag(args, "--movement") || HasFlag(args, "-movement");

                Console.WriteLine("  模式：holdSeconds=" + _holdSeconds
                    + " dropControlAfterReady=" + _dropControlAfterReady
                    + " movement=" + _movementMode
                    + "（默认 " + DefaultHoldSeconds + " = 原快速模式）");
                Console.Out.Flush();

                PMDsProcessWhitelist whitelist = new PMDsProcessWhitelist();
                whitelist.Add(DsExecutablePath);

                _launcher = new SmokeLauncher(new PMDsSystemProcessLauncher(whitelist), artifactsRoot);

                // 客户端副本创建事件是 static 接缝：订阅一次，按「对象所属世界」派发给对应客户端宿主。
                // （不订阅 ⇒ 永远不发探针；真实 DS 会把它当成「Lobby 静默」而在 30s 后失败退出。）
                PMR3Runtime.PlayerReplicated += OnPlayerReplicated;
            }

            /// <summary>把复制创建事件派发给「拥有该世界」的客户端宿主（多世界并跑时不能张冠李戴）。</summary>
            private void OnPlayerReplicated(PMR3Player player)
            {
                if (player == null)
                {
                    return;
                }

                for (int i = 0; i < _clients.Count; i++)
                {
                    if (ReferenceEquals(_clients[i].World, player.World))
                    {
                        _clients[i].OnReplicated(player);
                        return;
                    }
                }
            }

            // ── 执行 ────────────────────────────────────────────────────────────

            public void Execute()
            {
                // --movement 与另两个模式正交但**不组合**：组合只会让阶段顺序变得不可判定。
                if (_movementMode && (_holdSeconds > 0 || _dropControlAfterReady))
                {
                    Fail("S0b --movement 不能与 --hold-seconds/--drop-control-after-ready 组合"
                        + "（三者各自都有独立阶段；请分开跑）");
                    return;
                }

                // hold 必须同时满足：总预算余量 + 真实 DS smoke 期限（PMDsSessionHost.SmokeDeadlineMs=120000）。
                // 只限制本工具的 hold，不改生产常量。
                if (_holdSeconds > 0)
                {
                    long holdMs = (long)_holdSeconds * 1000L;
                    if (holdMs + HoldSafetyMarginMs > TotalBudgetMs || holdMs >= DsSmokeDeadlineMs)
                    {
                        Fail("S0 --hold-seconds=" + _holdSeconds + " 过大：需 hold*1000 + " + HoldSafetyMarginMs
                            + " <= " + TotalBudgetMs + "ms，且 hold*1000 < DS smoke 期限 " + DsSmokeDeadlineMs
                            + "ms；请减小 hold（默认 " + DefaultHoldSeconds + " = 快速模式）");
                        return;
                    }
                }

                Console.WriteLine("  预检 hold 上限核对：hold=" + (_holdSeconds * 1000L)
                    + "ms，预算余量上限=" + (TotalBudgetMs - HoldSafetyMarginMs)
                    + "ms，DS smoke 期限=" + DsSmokeDeadlineMs + "ms");
                Console.Out.Flush();

                Preflight();

                Section("阶段 1：真实 PMDsLobbyHost 启动（control = 系统临时端口）");

                Check(PMR3Runtime.IsRegistered && PMR3Runtime.ProtocolHash != 0u,
                    "S9 PMR3 生成表已封板（hash=0x" + PMR3Runtime.ProtocolHash.ToString("X8", CultureInfo.InvariantCulture) + "）");
                CheckEq(PMR3Runtime.CollisionDigest, 0x52334201u, "S10 固定测试碰撞摘要 = 契约 §7.4 冻结值");

                int first = 40000 + (new Random(Guid.NewGuid().GetHashCode()).Next(0, 400)) * 20;
                int last = first + 19;

                Check(first >= 20000 && first <= 60000, "S11 本局 UDP 端口段落在随机高位区间（"
                    + first + "-" + last + "）");
                Check(!InReservedRange(first, last), "S12 端口段不含 7777/7778/7800");

                PMDsLobbyHostOptions options = new PMDsLobbyHostOptions();
                options.Enabled = true;
                options.ListenAddress = "127.0.0.1";
                options.ControlPort = 0;                     // 系统分配的临时控制端口
                options.DsExecutablePath = DsExecutablePath; // 固定白名单 exe（不从客户端输入取）
                options.DsWorkingDirectory = DsWorkingDirectory;
                options.BootstrapRootDirectory = _bootstrapRoot;
                options.PortRangeFirst = first;
                options.PortRangeLast = last;
                options.ProtocolHash = PMR3Runtime.ProtocolHash;
                options.CollisionDigest = PMR3Runtime.CollisionDigest;
                options.RunBackgroundPumpThread = false;     // 测试主循环显式驱动（真实时钟）

                _host = new PMDsLobbyHost(options, PMDsSystemClock.Instance, _launcher, _gateway);
                _host.Log = delegate(string m) { Warn("[host] " + m); };
                _host.SessionStarted += delegate(PMDsLobbySessionRecord record)
                {
                    _launchPidFromHost = record.ProcessId;
                    Timeline("SessionStarted match=" + record.MatchId + " dsId=" + record.DsId
                        + " epoch=" + record.Epoch + " port=" + record.Port + " pid=" + record.ProcessId);
                };
                _host.SessionFailed += delegate(string matchId, string reason)
                {
                    Timeline("SessionFailed match=" + matchId + " reason=" + reason);
                    Warn("[host] 会话失败：" + reason);
                };
                _host.SessionReleased += delegate(string matchId, PMDsSessionState state, string reason)
                {
                    _sawRelease = true;
                    Timeline("SessionReleased match=" + matchId + " state=" + state + " reason=" + reason);
                };
                _host.ResultAccepted += delegate(PMDsLobbyResultNotice notice)
                {
                    Timeline("ResultAccepted match=" + notice.MatchId + " winner=" + notice.WinnerTeamId
                        + " summary=" + (notice.Summary == null ? 0 : notice.Summary.Length) + "B");
                };

                string error;
                bool started = _host.Start(out error);
                Check(started, "S13 真实 PMDsLobbyHost 启动成功（" + (error ?? "ok") + "）");
                if (!started)
                {
                    return;
                }

                _controlPort = _host.ControlPort;
                Check(_controlPort > 0, "S14 控制端口为真实监听端口（" + _controlPort + "）");
                Check(_controlPort != 7777 && _controlPort != 7778 && _controlPort != 7800,
                    "S15 控制端口不是任何现役/旧服务端口（" + _controlPort + "）");

                // ── 开局 ───────────────────────────────────────────────────────

                Section("阶段 2：新链开局 → 真实 HyldDS.exe 进程启动");

                _gateway.Online.Add(UidA);
                _gateway.Online.Add(UidB);
                _matchId = "r3b6-smoke-" + Guid.NewGuid().ToString("N").Substring(0, 8);

                PMDsLobbyMatchRequest request = new PMDsLobbyMatchRequest();
                request.MatchId = _matchId;
                request.FightPattern = "smoke";
                request.CollisionDigest = PMR3Runtime.CollisionDigest;
                request.RequestId = 1L;
                request.Roster = new PMDsRosterIdentity[]
                {
                    new PMDsRosterIdentity(UidA, 1, 0, 3),
                    new PMDsRosterIdentity(UidB, 2, 1, 5),
                };

                PMDsLobbyStartReply reply = _host.TryStartMatch(request);
                CheckEq((int)reply.Outcome, (int)PMDsLobbyStartOutcome.Queued,
                    "S16 新链开局请求被接受并入队（" + reply.Detail + "）");

                bool launched = WaitUntil("真实启动器收到一次启动请求（真实进程已 Start）",
                    delegate { return _launcher.Requests.Count == 1; }, 30000);
                Check(launched, "S17 真实 PMDsSystemProcessLauncher 被调用且真实进程已启动（唯一边界：参数尾部追加）");
                if (!launched)
                {
                    return;
                }

                PMDsProcessLaunchRequest launch = _launcher.Requests[0];
                _bootstrapPath = ArgValue(launch.Arguments, "-bootstrap");
                _dsId = ArgValue(launch.Arguments, "-dsid");
                _dsPort = ParseInt(ArgValue(launch.Arguments, "-port"));
                string controlEndpoint = ArgValue(launch.Arguments, "-control");
                _dsLogPath = _launcher.LogPaths[0];
                _lastArgsText = DescribeArguments(launch.Arguments);
                IPMDsProcess process = _launcher.Processes[0];
                _dsPid = process.ProcessId;

                CheckEq(launch.ExecutablePath, DsExecutablePath, "S18 启动 exe 就是白名单里的真实 HyldDS.exe");
                Check(string.Equals(launch.WorkingDirectory, DsWorkingDirectory, StringComparison.OrdinalIgnoreCase),
                    "S19 工作目录 = 真实 DS 目录（Unity 数据目录可解析）");
                Check(_dsPid > 0, "S20 真实 OS 进程已启动：pid=" + _dsPid);
                Check(_dsPid != System.Diagnostics.Process.GetCurrentProcess().Id,
                    "S21 该 pid 不是本测试进程自身");
                CheckEq(_launchPidFromHost, _dsPid, "S22 LobbyHost 记账的 ProcessId 与真实进程一致");
                Check(process.IsRunning, "S23 启动瞬间进程仍在运行（不是立刻退出的冒烟）");

                Check(HasFlag(launch.Arguments, "-server-smoke"),
                    "S24 薄包装已追加 -server-smoke（显式 smoke 验收，非伪造接口）");
                CheckEq(ArgValue(launch.Arguments, "-logFile"), _dsLogPath,
                    "S25 薄包装已追加绝对 -logFile（真实 Unity 宿主日志落到 artifacts）");
                Check(Path.IsPathRooted(_dsLogPath) && _dsLogPath.StartsWith(_artifactsRoot, StringComparison.OrdinalIgnoreCase),
                    "S26 -logFile 是 artifacts 下的绝对路径（" + _dsLogPath + "）");
                CheckEq(ArgValue(launch.Arguments, "-matchid"), _matchId, "S27 -matchid 由真实 LobbyHost 产出且与本局一致");
                CheckEq(ArgValue(launch.Arguments, "-control"), "127.0.0.1:" + _controlPort,
                    "S28 -control 指向真实监听端口（由 LobbyHost 产出）");
                Check(HasFlag(launch.Arguments, "-server") && HasFlag(launch.Arguments, "-batchmode")
                    && HasFlag(launch.Arguments, "-nographics"), "S29 启动参数含 -server/-batchmode/-nographics");
                Check(_dsPort > 0 && _dsPort >= first && _dsPort <= last,
                    "S30 -port 来自 Allocate 后的真实端口且落在随机高位段（" + _dsPort + "）");
                Check(_dsPort != 7777 && _dsPort != 7778 && _dsPort != 7800 && _dsPort != _controlPort,
                    "S31 本局 UDP 端口不是现役/旧服务端口，也不与控制端口相同");
                Check(!string.IsNullOrEmpty(_bootstrapPath) && File.Exists(_bootstrapPath),
                    "S32 真实 LobbyHost 已原子发布引导文件（路径来自启动参数）");
                Check(_launcher.LastFaultDetail == null, "S33 启动器无错误细节（" + (_launcher.LastFaultDetail ?? "ok") + "）");
                Check(!_lastArgsText.Contains("Ticket") && !_lastArgsText.Contains("Key"),
                    "S34 启动参数记录里不含票据/密钥字样（只给引导文件路径）");

                WriteArtifact("launch-args.txt", "pid=" + _dsPid + Environment.NewLine
                    + "exe=" + launch.ExecutablePath + Environment.NewLine
                    + "workdir=" + launch.WorkingDirectory + Environment.NewLine
                    + "args=" + _lastArgsText + Environment.NewLine);

                // ── Ready / offer ─────────────────────────────────────────────

                Section("阶段 3：真实控制 TCP Ready → 真实 PMDS1 offer");

                bool ready = WaitUntil("Lobby 在真实 Ready 之后发布两份 offer",
                    delegate { return _gateway.EntryTexts.Count == 2; }, 45000);

                PMDsCoordinator coordinator = _host.GetCoordinator(_matchId);
                Check(coordinator != null, "S35 可按 matchId 取到真实协调器（诊断面）");
                if (coordinator == null)
                {
                    return;
                }

                Check(ready, "S36 真实 DS 上报 Ready 后，Lobby 发布 2 份 offer（真实 TCP 控制面）");
                CheckEq((int)coordinator.State, (int)PMDsSessionState.Running, "S37 发布 offer 后协调器进入 Running");
                Check(_host.HasBoundControlConnection(_matchId), "S38 控制连接已由 MAC 校验后的首帧绑定");
                CheckGt(_host.InboundFramesDispatched, 0, "S39 Lobby listener 真的分帧并派发过控制帧（真实 TCP）");
                CheckEq(_host.InboundFramesDroppedMacFailed, 0L, "S40 全链路没有任何控制帧 MAC 失败");
                CheckEq(_host.InboundFramesDroppedMalformed, 0L, "S41 没有畸形控制帧");
                CheckEq(_host.SessionStartsRejected, 0L, "S42 没有任何开局被拒绝");
                CheckEq(_gateway.OfferFailures, 0L, "S43 没有 offer 投递失败（真实 codec 往返全部成功）");

                string dsLog = DsLog();
                Check(dsLog != null && dsLog.Length > 0, "S44 真实 Unity DS 日志文件已产生（-logFile 生效）");
                Check(Contains(dsLog, "] 检测到 -bootstrap，转入 R3-B 新链宿主"),
                    "S45 DS 日志证明它走的是 R3-B 新链宿主（不是旧诊断路由）");
                Check(Contains(dsLog, "固定测试碰撞场景已建立并校验"),
                    "S46 DS 日志证明固定测试碰撞场景已建立并校验（SceneReady 前置）");
                Check(Contains(dsLog, "digest=0x" + PMR3Runtime.CollisionDigest.ToString("X8", CultureInfo.InvariantCulture)),
                    "S47 DS 日志的 collisionDigest 与本机冻结值一致（0x"
                    + PMR3Runtime.CollisionDigest.ToString("X8", CultureInfo.InvariantCulture) + "）");
                Check(Contains(dsLog, "UDP boundPort=" + _dsPort),
                    "S48 DS 日志报告真实绑定端口 = 分配端口（" + _dsPort + "）");

                PMDsEntryOffer offerA = _gateway.Offers[UidA];
                PMDsEntryOffer offerB = _gateway.Offers[UidB];

                CheckEq(offerA.MatchId, _matchId, "S49 offer A 的 MatchId 来自会话");
                CheckEq(offerA.Port, _dsPort, "S50 offer A 的 Port 等于真实分配端口");
                CheckEq(offerA.Host, "127.0.0.1", "S51 offer A 的 Host 是 loopback（首版口径）");
                CheckEq(offerA.ProtocolHash, PMR3Runtime.ProtocolHash, "S52 offer A 的协议摘要 = PMR3 生成表摘要");
                CheckEq(offerA.CollisionDigest, PMR3Runtime.CollisionDigest, "S53 offer A 的碰撞摘要 = 契约冻结值");
                CheckEq(offerA.Identity.Uid, UidA, "S54 offer A 的身份 uid 来自名册");
                CheckEq(offerB.Identity.Uid, UidB, "S55 offer B 的身份 uid 来自名册");
                Check(offerA.Ticket != null && offerA.Ticket.Length > 0 && offerB.Ticket != null && offerB.Ticket.Length > 0,
                    "S56 两份 offer 都携带真实票据字节");
                Check(_gateway.EntryTexts[UidA].IndexOf(PMDsEntryCodec.Prefix, StringComparison.Ordinal) == 0,
                    "S57 编码文本以 PMDS1: 开头（MainPack.Str 的冻结载体）");

                // 真实 DS 真的占着该 UDP 端口：独占 bind 必须失败。
                string bindError;
                bool heldByDs = !TryExclusiveUdpBind(_dsPort, out bindError);
                _midRunPortProbeError = bindError;
                Check(heldByDs, "S58 运行期独占 bind 本局 UDP 端口失败 ⇒ 该端口确实被真实 DS 进程持有（"
                    + (bindError ?? "意外的成功") + "）");

                // ── 两个纯 C# 客户端经真实 UDP 入局 ─────────────────────────────

                Section("阶段 4：两个纯 C#（非 UnityUI）客户端经真实 UDP 入局 + 探针/回声/收敛");

                _clients.Add(ClientHarness.Enter(offerA, _warnings));
                _clients.Add(ClientHarness.Enter(offerB, _warnings));

                // hold / 故障 / 运动模式：从「已入局」起就关闭探针闸门，直到显式开闸
                // （否则复制到达后的下一帧就会把探针发出去，DS 的 smoke 立刻结束）。
                if (_holdSeconds > 0 || _dropControlAfterReady || _movementMode)
                {
                    _clients[0].ProbeGateOpen = false;
                    _clients[1].ProbeGateOpen = false;

                    // --movement：从 Create 那一帧就快照一份运动初值载荷（证「初值随 Create 到达」）。
                    if (_movementMode)
                    {
                        _clients[0].CaptureMovementInitialPayload = true;
                        _clients[1].CaptureMovementInitialPayload = true;
                    }

                    Progress("探针闸门已关闭（holdSeconds=" + _holdSeconds + " dropControl=" + _dropControlAfterReady
                        + " movement=" + _movementMode + "）");
                }

                bool connected = WaitUntil("两个客户端在真实 UDP 上完成握手/验票/激活",
                    delegate
                    {
                        return _clients[0].Endpoint.ClientConnection != null
                            && _clients[1].Endpoint.ClientConnection != null;
                    }, 30000);

                Check(connected, "S59 两个客户端在真实 UDP 上完成握手/验票/激活");
                CheckEq(_clients[0].Endpoint.ConnectionCount, 1, "S60 客户端 A 1 条已激活连接");
                CheckEq(_clients[1].Endpoint.ConnectionCount, 1, "S61 客户端 B 1 条已激活连接");
                CheckEq(_clients[0].Endpoint.HandshakeRejections, 0L, "S62 客户端 A 没有握手被拒");
                CheckEq(_clients[1].Endpoint.HandshakeRejections, 0L, "S63 客户端 B 没有握手被拒");
                CheckEq(_clients[0].Endpoint.DroppedPreActivationDatagrams, 0L, "S64 客户端 A 没有激活前数据报被丢");
                CheckEq(_clients[0].Endpoint.IdentityMismatches, 0L, "S65 客户端 A 没有摘要/nonce 不匹配的 ServerHello");
                CheckGt(_clients[0].Endpoint.DatagramsReceived, 0, "S66 客户端 A 真的收到过 UDP 数据报");
                CheckGt(_clients[0].Endpoint.DatagramsSent, 0, "S67 客户端 A 真的发出过 UDP 数据报");

                bool replicated = WaitUntil("两个客户端各看到两个玩家副本",
                    delegate
                    {
                        return _clients[0].ReplicasByUid.Count == 2 && _clients[1].ReplicasByUid.Count == 2;
                    }, 30000);

                _clientAOwn = _clients[0].OwnReplica;
                PMR3Player clientAOther = _clients[0].FindReplica(UidB);
                Check(replicated, "S68 两个客户端各看到 2 个副本创建回调（A=" + ReplicaCount(_clients[0])
                    + " B=" + ReplicaCount(_clients[1]) + "）");
                CheckEq(_clients[0].World.ObjectCount, 2, "S69 客户端 A 世界对象数 = 2");
                CheckEq(_clients[1].World.ObjectCount, 2, "S70 客户端 B 世界对象数 = 2");

                Check(_clientAOwn != null && _clientAOwn.Role == PMNetRole.AutonomousProxy,
                    "S71 客户端 A 的本端副本角色 = AutonomousProxy（Create 初值已带 uid）");
                Check(clientAOther != null && clientAOther.Role == PMNetRole.SimulatedProxy,
                    "S72 客户端 A 看到的对手副本角色 = SimulatedProxy");
                Check(_clientAOwn != null && _clientAOwn.Uid == UidA, "S73 客户端 A 本端副本 uid 初值正确");

                IPMDsProcess dsProcess = _launcher.Processes.Count > 0 ? _launcher.Processes[0] : null;

                if (_holdSeconds > 0)
                {
                    RunHoldPhase(coordinator, dsProcess);
                }

                if (_dropControlAfterReady)
                {
                    RunDropControlPhase(coordinator, dsProcess);
                    Section("阶段 6：证据归档（故障模式）");
                    Check(_launcher.Requests.Count == 1, "S117 全程只启动过一次真实进程（没有复用旧 DS 冒充更新）");
                    ArchiveEvidence(false);
                    return;
                }

                if (_movementMode)
                {
                    // 仍然零探针（闸门在入局后就已关）：DS 的 smoke 验收必须等运动验收做完。
                    Check(_clients[0].ProbeNonceSent == 0 && _clients[1].ProbeNonceSent == 0,
                        "M0 运动验收开始时探针仍为零（真实 DS 的 smoke 不会提前结束闭环）");

                    RunMovementPhase(coordinator, dsProcess);
                    WriteMovementArtifacts();

                    Section("阶段 4e：运动验收完成 → 开闸回到原 smoke 闭环（探针 → 收敛 → 结果 → 退出）");
                }

                // 开闸：hold / 运动验收结束后回到原快速路径（探针 → 收敛 → 结果 → 退出）。
                _clients[0].ProbeGateOpen = true;
                _clients[1].ProbeGateOpen = true;

                bool converged = WaitUntil("探针 → 服务端 ProbeCount → ClientEcho → 属性收敛",
                    delegate
                    {
                        PMR3Player a = _clients[0].OwnReplica;
                        PMR3Player b = _clients[1].OwnReplica;
                        return a != null && b != null
                            && a.ProbeCount == 1 && a.EchoCount == 1
                            && b.ProbeCount == 1 && b.EchoCount == 1;
                    }, 30000);

                Check(converged, "S74 每个 owner 一次探针 → 属性 ProbeCount=1 收敛 + 收到一次 ClientEcho");
                CheckEq(_clients[0].ProbeNonceSent, 1, "S75 客户端 A 只发过一次探针");
                CheckEq(_clients[1].ProbeNonceSent, 1, "S76 客户端 B 只发过一次探针");
                CheckEq(_clients[0].OwnReplica.ProbeSentCount, 1, "S77 客户端 A 本端副本的本地探针计数 = 1");
                CheckEq(_clients[1].OwnReplica.ProbeSentCount, 1, "S78 客户端 B 本端副本的本地探针计数 = 1");
                CheckEq(_clients[0].OwnReplica.EchoCount, 1, "S79 客户端 A 收到恰好一次 ClientEcho");
                CheckEq(_clients[1].OwnReplica.EchoCount, 1, "S80 客户端 B 收到恰好一次 ClientEcho");
                CheckEq(_clients[0].OwnReplica.LastEchoNonce, _clients[0].LastProbeNonce,
                    "S81 客户端 A 的 Echo nonce 原样回流");
                CheckEq(_clients[1].OwnReplica.LastEchoNonce, _clients[1].LastProbeNonce,
                    "S82 客户端 B 的 Echo nonce 原样回流");
                CheckEq(ProbeCountOf(_clients[0], UidB), 1, "S83 客户端 A 也看到 B 的 ProbeCount 收敛到 1");
                CheckEq(ProbeCountOf(_clients[1], UidA), 1, "S84 客户端 B 也看到 A 的 ProbeCount 收敛到 1");
                CheckGt(_clients[0].Bridge.RpcSent, 0, "S85 客户端 A 的桥发出过上行 RPC（生成桩，真实 UDP）");
                CheckGt(_clients[1].Bridge.RpcSent, 0, "S86 客户端 B 的桥发出过上行 RPC（生成桩，真实 UDP）");
                CheckGt(_clients[0].Bridge.Replication.Stats.UpdateRecordsApplied, 0,
                    "S87 客户端 A 应用过复制更新记录");
                CheckEq(_clients[0].Failures.Count, 0, "S88 客户端 A 端点没有上报失败");
                CheckEq(_clients[1].Failures.Count, 0, "S89 客户端 B 端点没有上报失败");

                dsLog = DsLogFresh();
                Check(Contains(dsLog, "玩家副本已创建 uid=" + UidA), "S90 真实 DS 日志证明它创建了 uid=A 的权威副本");
                Check(Contains(dsLog, "玩家副本已创建 uid=" + UidB), "S91 真实 DS 日志证明它创建了 uid=B 的权威副本");

                // DS 侧的 smoke 结果由真实 DS 自己判定（全部名册玩家完成探针）。
                bool smokeOnDs = WaitUntil("真实 DS 判定 smoke 并提交结果",
                    delegate { return Contains(DsLog(), "smoke 验收：名册 2 人全部完成探针，已提交 smoke 结果"); },
                    30000);
                Check(smokeOnDs, "S92 真实 DS 的 -server-smoke 路径成立：名册 2 人全部完成探针并提交 smoke 结果");

                // ── 结果 / ResultAck / Exited / 真实进程退出 ──────────────────

                Section("阶段 5：smoke 结果 → ResultAck → 认证显式 Exited(0) → 实际 OS 进程退出 → 端口回收");

                bool accepted = WaitUntil("Lobby 受理真实结果",
                    delegate { return coordinator.Counters.ResultAccepted == 1L; }, 30000);
                Check(accepted, "S93 Lobby 受理了真实 DS 的 smoke 结果（ResultAccepted=1）");
                CheckEq((int)coordinator.Counters.ResultAccepted, 1, "S94 结果只被结算一次（幂等）");
                CheckEq((int)_gateway.Results.Count, 1, "S95 网关只收到一次结果通知（不伪造 BattleReview）");

                if (_gateway.Results.Count > 0)
                {
                    PMDsLobbyResultNotice notice = _gateway.Results[0];
                    _resultSummary = notice.Summary;
                    CheckEq(notice.MatchId, _matchId, "S96 结果通知携带正确 matchId");
                    CheckEq(notice.WinnerTeamId, 0, "S97 smoke 结果的 winnerTeamId = 0（不是伪造正常胜负）");
                    Check(notice.Summary != null && ContainsAscii(notice.Summary, "smoke"),
                        "S98 结果摘要自带 smoke 字面量（经真实控制协议原样到达 Lobby）");
                }
                else
                {
                    Fail("S96-S98 结果通知缺失（无法核对 matchId/winner/摘要）");
                }

                bool exitedTerminal = WaitUntil("认证显式 Exited(0) ⇒ 终态 Exited",
                    delegate { return (int)coordinator.State == (int)PMDsSessionState.Exited; }, 30000);
                _sawExitedTerminal = exitedTerminal;
                _exitedTerminalAtMs = NowMs();
                Check(exitedTerminal, "S99 终态 = Exited（只有「结果已受理 + 经 MAC 认证的显式 Exited(0)」才是正常完成）");
                Check(coordinator.PeerExitObserved, "S100 对端收尾已被观测（PeerExitObserved）");
                CheckEq((int)_host.InboundFramesDroppedMacFailed, 0, "S101 结果确认往返没有 MAC 失败");

                bool processGone = WaitUntil("真实 OS 进程退出（Process.Exited 事件，由 OS 触发）",
                    delegate { return _launcher.ExitObservations > 0; }, 30000);
                _sawProcessExit = processGone;
                _osExitObservedAtMs = _launcher.ExitObservedAtMs;
                Check(processGone, "S102 观测到真实 OS 进程退出事件（.NET Process.Exited；而不是只看到对象被释放）");

                int exitCode = _launcher.LastExitCode;
                bool hasExitCode = _launcher.ExitObservations > 0;
                _osExitCode = exitCode;
                _killRequestsAtTerminal = coordinator.Counters.KillRequests;
                _deferredCleanupTicksAtTerminal = coordinator.Counters.DeferredCleanupTicks;
                Check(hasExitCode, "S103 取到真实 OS 退出码（" + (hasExitCode ? exitCode.ToString(CultureInfo.InvariantCulture) : "不可用") + "）");
                Check(hasExitCode && exitCode == 0,
                    "S104 真实 OS 退出码 = 0（Unity 优雅退出，非强杀；实际 "
                    + (hasExitCode ? exitCode.ToString(CultureInfo.InvariantCulture) : "不可用")
                    + "，协调器强杀请求=" + _killRequestsAtTerminal + " 次，先后="
                    + (_osExitObservedAtMs > 0L && _exitedTerminalAtMs > 0L
                        ? (_osExitObservedAtMs >= _exitedTerminalAtMs ? "OS 退出晚于终态（可能被终态强杀）" : "OS 退出早于终态（自然退出）")
                        : "不可比")
                    + "）");
                Progress("退出诊断：osExitCode=" + exitCode + " ExitObservations=" + _launcher.ExitObservations
                    + " 协调器KillRequests=" + _killRequestsAtTerminal
                    + " GracefulExitWaits=" + coordinator.Counters.GracefulExitWaits
                    + " GracefulExitTimeouts=" + coordinator.Counters.GracefulExitTimeouts
                    + " DeferredCleanupTicks=" + _deferredCleanupTicksAtTerminal
                    + " exitedTerminalAt=" + _exitedTerminalAtMs + " osExitObservedAt=" + _osExitObservedAtMs);
                _lobbyTerminalReason = coordinator.LastReason;
                Check(containsReason(_lobbyTerminalReason, "Exited(0)"),
                    "S104b Lobby 侧的终态理由是「对端经认证显式 Exited(0)」：" + (_lobbyTerminalReason ?? "<none>"));
                Check(Contains(DsLogFresh(), "R3-B 新链请求退出，exitCode=0"),
                    "S104c 真实 DS 日志自报是优雅退出请求（R3-B 新链请求退出，exitCode=0）⇒ 非 0 的 OS 退出码不是 DS 自己要求的");
                // 本次修复的核心不变量：认证正常退出后 Lobby 在宽限内**不得**强杀。
                // 这是「真实 OS exit 0」的前提，也是旧实现（终态无条件 Kill）的失败点。
                _gracefulExitTimeoutsAtTerminal = coordinator.Counters.GracefulExitTimeouts;
                Check(_killRequestsAtTerminal == 0L,
                    "S104d 认证正常退出后 Lobby 未请求任何强杀（实际 KillRequests="
                    + _killRequestsAtTerminal + "）");
                Check(_gracefulExitTimeoutsAtTerminal == 0L,
                    "S104e 无优雅退出宽限超时（正常收尾而非异常收尾；实际 "
                    + _gracefulExitTimeoutsAtTerminal + "）");

                bool released = WaitUntil("确认进程退出后回收端口与 uid",
                    delegate { return _sawRelease; }, 30000);
                Check(released, "S105 确认进程实际退出后完成收尾（SessionReleased）");
                CheckEq(_host.PortPool.InUseCount, 0, "S106 共享端口池已归还（InUseCount=0）");
                CheckEq(_host.PlayerLedger.Count, 0, "S107 跨会话 uid 占用账本已清空");
                Check(!_host.PlayerLedger.IsOccupied(UidA) && !_host.PlayerLedger.IsOccupied(UidB),
                    "S108 两名玩家的 uid 均已释放");
                Check(!coordinator.ResourcesHeld, "S109 协调器资源占用已释放");

                // S109b：正常路径「不靠强杀」的精确口径。合法时序有**两条**，都得同时满足
                // 「OS 退出码 0 + 0 次强杀 + 资源真实回收」，差别只在宽限窗口是否存在：
                //   A) 协调器在终态后进过优雅退出宽限（GracefulExitWaits>0）
                //      ⇒ 必须在宽限内**自行**观测到进程退出（Observed>=1）且无宽限超时（Timeouts==0）；
                //   B) 协调器进终态时进程**已经**退出（OS 退出早于终态、宽限窗口从未建立）
                //      ⇒ Observed 天然为 0（该计数只在宽限分支自增），此时以「OS 退出码 0 +
                //         强杀 0 次 + 端口/名册/协调器资源全部回收 + OS 退出早于终态」为准。
                // 两条路径都**不**放宽强杀/非 0 退出的拒绝：exit0 与 KillRequests==0 是**共同**前提，
                // 宽限超时（GracefulExitTimeouts>0）在两条路径下都是失败。
                _gracefulExitObservedAtTerminal = coordinator.Counters.GracefulExitObserved;
                _gracefulExitWaitsAtTerminal = coordinator.Counters.GracefulExitWaits;

                bool exitEvidenceOk = hasExitCode && exitCode == 0 && _killRequestsAtTerminal == 0L;
                bool resourcesReclaimed = !coordinator.ResourcesHeld
                    && _host.PortPool.InUseCount == 0
                    && _host.PlayerLedger.Count == 0
                    && !_host.PlayerLedger.IsOccupied(UidA)
                    && !_host.PlayerLedger.IsOccupied(UidB)
                    && _sawRelease;
                bool graceWindowUsed = _gracefulExitWaitsAtTerminal > 0L;
                bool osExitedBeforeTerminal = _osExitObservedAtMs > 0L && _exitedTerminalAtMs > 0L
                    && _osExitObservedAtMs <= _exitedTerminalAtMs
                    && _gracefulExitWaitsAtTerminal == 0L;
                bool gracefulWithinWindow = _gracefulExitObservedAtTerminal >= 1L
                    && _gracefulExitTimeoutsAtTerminal == 0L;
                bool neverKilledTheNormalPath = graceWindowUsed ? gracefulWithinWindow : osExitedBeforeTerminal;

                Check(exitEvidenceOk && resourcesReclaimed && neverKilledTheNormalPath,
                    "S109b 正常路径不靠强杀（OS 退出码=" + (hasExitCode ? exitCode.ToString(CultureInfo.InvariantCulture) : "不可用")
                    + "，强杀请求=" + _killRequestsAtTerminal + " 次，资源已回收=" + resourcesReclaimed
                    + "，宽限窗口=" + (graceWindowUsed
                        ? "有（Waits=" + _gracefulExitWaitsAtTerminal + "，Observed=" + _gracefulExitObservedAtTerminal
                          + "，Timeouts=" + _gracefulExitTimeoutsAtTerminal + "）"
                        : "无（进程在协调器进终态前已自行退出：osExit=" + _osExitObservedAtMs
                          + " <= 终态=" + _exitedTerminalAtMs + "）") + "）");
                Check(!coordinator.IsAwaitingGracefulExit,
                    "S109c 收尾完成后优雅退出宽限标记已清");
                Check(!File.Exists(_bootstrapPath), "S110 引导文件随收尾删除（只传路径的交付面无残留）");
                Check(coordinator.PeerExitObserved, "S111 对端收尾观测在终态后仍保持为真");
                Check(_gateway.Restored.Contains(UidA) && _gateway.Restored.Contains(UidB),
                    "S112 确认退出后才恢复两名玩家的 PlayerOnline");

                bool rebound = TryExclusiveUdpBind(_dsPort, out _postExitPortProbeError);
                Check(rebound, "S113 退出后独占 bind 本局 UDP 端口成功 ⇒ OS 真的回收了玩家端口（"
                    + (_postExitPortProbeError ?? "ok") + "）");

                // 进程刚退出时 Unity 日志的最后几行可能还没落到盘上：用有界「重读」等它，不做长 sleep。
                bool ackLogged = WaitUntil("真实 DS 日志出现 ResultAck 确认行",
                    delegate { return Contains(DsLogFresh(), "收到匹配的 ResultAck"); }, 15000);
                Check(ackLogged, "S114 真实 DS 日志证明它收到了匹配的 ResultAck（ResultAck 真到了对端）");

                bool shutdownLogged = WaitUntil("真实 DS 日志出现收尾痕迹",
                    delegate
                    {
                        string text = DsLogFresh();
                        return Contains(text, "已关闭（reason=") || Contains(text, "OnApplicationQuit");
                    }, 15000);
                Check(shutdownLogged, "S115 真实 DS 日志留下收尾痕迹（优雅关闭/OnApplicationQuit）");

                Section("阶段 6：证据归档（DS 日志/stdout/stderr/启动记录/摘要）");

                Check(!string.IsNullOrEmpty(_dllSha256) && _dllSha256.Length == 64,
                    "S116 构建产物预检完整（sha256=" + _dllSha256 + "）");
                Check(_launcher.Requests.Count == 1, "S117 全程只启动过一次真实进程（没有复用旧 DS 冒充更新）");
                CheckGt(_dsPid, 0, "S118 启动 pid 已记录（pid=" + _dsPid + "）");
                Check(_dsPort != 7777 && _dsPort != 7778 && _dsPort != 7800
                    && _controlPort != 7777 && _controlPort != 7778 && _controlPort != 7800,
                    "S119 本工具使用的两个端口都不是现役/旧服务端口（udp=" + _dsPort + " control=" + _controlPort + "）");

                ArchiveEvidence(true);
            }

            // ── 预检 ────────────────────────────────────────────────────────────

            /// <summary>
            /// 真实长局验证：两客户端已激活且各见两副本之后，**关闭探针闸门**并持续泵 N 秒（默认 40）。
            /// 期间照常泵控制 host（TCP 控制面 + 心跳应答）与两客户端 UDP，不阻塞；并持续断言
            /// DS 进程仍活、协调器 Running、无结果、连接仍 ready。探针一旦提前发出，真实 DS 的
            /// smoke 验收会立刻结束闭环，因此闸门是这条验证成立的前提。
            /// </summary>
            private void RunHoldPhase(PMDsCoordinator coordinator, IPMDsProcess process)
            {
                long holdTargetMs = (long)_holdSeconds * 1000L;
                long holdStartMs = NowMs();
                _holdStartMs = holdStartMs;
                _holdStartWall = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

                long hbStart = coordinator.Counters.Heartbeats;
                long hbReplyStart = coordinator.Counters.HeartbeatReplies;

                Timeline("HoldStart match=" + _matchId + " requestedMs=" + holdTargetMs
                    + " dsPid=" + (process == null ? -1 : process.ProcessId));

                Section("阶段 4b：真实长局 hold " + _holdSeconds + "s（暂缓 owner 探针，持续泵控制/UDP）");
                Progress("hold 起点 = 「两客户端各见两副本」之后（不是进程启动时刻）："
                    + _holdStartWall + "，目标 " + holdTargetMs + "ms");

                long deadlineMs = Math.Min(holdStartMs + holdTargetMs, _deadlineMs);
                long nextProgressMs = holdStartMs + 5000L;

                bool dsAliveThroughout = process != null && process.IsRunning;
                bool stateRunningThroughout = true;
                bool noResultThroughout = true;
                bool connectionsReadyThroughout = true;
                int aliveSamples = 0;
                long breakAtMs = -1L;

                while (true)
                {
                    PumpAll();

                    long now = NowMs();
                    bool dsAlive = process != null && process.IsRunning && _launcher.ExitObservations == 0;
                    bool running = (int)coordinator.State == (int)PMDsSessionState.Running;
                    bool noResult = coordinator.Counters.ResultAccepted == 0L && _gateway.Results.Count == 0;
                    bool conns = _clients[0].Endpoint.ClientConnection != null
                        && _clients[1].Endpoint.ClientConnection != null
                        && _clients[0].Endpoint.ConnectionCount == 1
                        && _clients[1].Endpoint.ConnectionCount == 1
                        && _host.HasBoundControlConnection(_matchId)
                        && _clients[0].Failures.Count == 0
                        && _clients[1].Failures.Count == 0;

                    if (dsAlive)
                    {
                        aliveSamples++;
                    }

                    dsAliveThroughout = dsAliveThroughout && dsAlive;
                    stateRunningThroughout = stateRunningThroughout && running;
                    noResultThroughout = noResultThroughout && noResult;
                    connectionsReadyThroughout = connectionsReadyThroughout && conns;

                    if (!dsAlive || !running || !noResult || !conns)
                    {
                        breakAtMs = now - holdStartMs;
                        Progress("hold 期间不变量被破坏（t+" + breakAtMs + "ms）：dsAlive=" + dsAlive
                            + " running=" + running + " noResult=" + noResult + " conns=" + conns);
                        break;
                    }

                    if (now >= nextProgressMs)
                    {
                        nextProgressMs += 5000L;
                        Progress("hold 进度 " + (now - holdStartMs) + "ms/" + holdTargetMs + "ms（DS→Lobby 心跳累计 "
                            + (coordinator.Counters.Heartbeats - hbStart) + "，Lobby→DS 应答累计 "
                            + (coordinator.Counters.HeartbeatReplies - hbReplyStart) + "，DS 存活采样 " + aliveSamples + "）");
                    }

                    if (now >= deadlineMs)
                    {
                        break;
                    }

                    Thread.Sleep((int)Math.Min(PollIntervalMs, Math.Max(1L, deadlineMs - now)));
                }

                long holdEndMs = NowMs();
                _holdEndMs = holdEndMs;
                _holdMilliseconds = holdEndMs - holdStartMs;
                _holdEndWall = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                _holdHeartbeats = coordinator.Counters.Heartbeats - hbStart;
                _holdHeartbeatReplies = coordinator.Counters.HeartbeatReplies - hbReplyStart;
                _holdAliveSamples = aliveSamples;

                Timeline("HoldEnd actualMs=" + _holdMilliseconds + " heartbeats=" + _holdHeartbeats
                    + " replies=" + _holdHeartbeatReplies + " aliveSamples=" + aliveSamples);

                // 真实 DS 日志证据：DS 自报收到的可信 Lobby 下行数（hbRx=）与心跳 tick 次数。
                string dsHoldLog = DsLogFresh();
                _holdDsLogHeartbeatTicks = CountOccurrences(dsHoldLog, "heartbeat tick=");
                _holdDsMaxHbRx = MaxHeartbeatRx(dsHoldLog);

                Check(dsAliveThroughout, "S73a hold 期间真实 DS 进程始终存活（每帧采样均 IsRunning 且无 Exited 事件）");
                Check(_launcher.ExitObservations == 0, "S73b hold 期间没有观测到任何真实 OS 进程退出事件（ExitObservations=0）");
                Check(_holdMilliseconds >= holdTargetMs,
                    "S73c 实际延后 >= 目标（实际 " + _holdMilliseconds + "ms，目标 " + holdTargetMs
                    + "ms；从「两客户端各见两副本」之后起算，" + (_holdMilliseconds > 30000L ? "已 > 30s" : "尚未 > 30s")
                    + "，不是只从进程启动计时；中途破坏点=" + (breakAtMs < 0L ? "无" : breakAtMs + "ms") + "）");
                Check(stateRunningThroughout && (int)coordinator.State == (int)PMDsSessionState.Running,
                    "S73d hold 期间协调器始终 Running（最终 state=" + coordinator.State + "）");
                Check(noResultThroughout && coordinator.Counters.ResultAccepted == 0L && _gateway.Results.Count == 0,
                    "S73e hold 期间没有任何结果被提交/受理（ResultAccepted=" + coordinator.Counters.ResultAccepted
                    + "，网关结果=" + _gateway.Results.Count + "）⇒ 没有提前走完闭环");
                Check(connectionsReadyThroughout,
                    "S73f hold 期间两客户端与控制连接始终 ready（各 1 条已激活连接 + 控制连接已绑定 + 无端点失败）");
                Check(_clients[0].ProbeNonceSent == 0 && _clients[1].ProbeNonceSent == 0,
                    "S73g hold 期间没有任何 owner 探针被发出（ProbeNonceSent=" + _clients[0].ProbeNonceSent
                    + "/" + _clients[1].ProbeNonceSent + "；否则真实 DS 的 smoke 会立刻结束）");
                CheckGt(_holdHeartbeats, 4L, "S73h hold 期间真实 DS→Lobby 控制心跳多次到达（本次 " + _holdHeartbeats
                    + " 次，5s 间隔）");
                CheckGt(_holdHeartbeatReplies, 4L, "S73h2 hold 期间 Lobby→DS 心跳应答多次发出（本次 " + _holdHeartbeatReplies + " 次）");
                CheckEq(coordinator.Counters.HeartbeatTimeouts, 0L, "S73i 协调器没有发生心跳超时（HeartbeatTimeouts=0）");
                CheckGt(_holdDsMaxHbRx, 1L, "S73j 真实 DS 自报收到 Lobby 可信下行（日志 hbRx 最大值=" + _holdDsMaxHbRx
                    + "）⇒ DS 运行期 liveness(15s) 被持续续命，未自判失败退出");
                CheckGt(_holdDsLogHeartbeatTicks, 1L, "S73k 真实 DS 日志有 " + _holdDsLogHeartbeatTicks
                    + " 条心跳 tick（DS 确实处于运行期，而不仅仅启动了）");

                Progress("hold 实际窗口 " + _holdStartWall + " → " + _holdEndWall + "（" + _holdMilliseconds
                    + "ms）；DS dll mtime=" + _dllMtime + " size=" + _dllSize + " sha256=" + _dllSha256);
            }

            /// <summary>
            /// 故障模式（可选）：认证并各见两副本后，真实 Lobby **停止控制面下行**，
            /// 验证真实 DS 因运行期 liveness（15s 无可信下行）以**非 0**退出，且仍完成资源回收。
            /// 实现只用「停止驱动 PMDsLobbyHost.PumpOnce()」——宿主不再 drain 入站、不再回复心跳；
            /// 不伪造帧、不注入消息。全程关闭探针闸门，排除「正常结果→Exited(0)」这一解释。
            /// </summary>
            private void RunDropControlPhase(PMDsCoordinator coordinator, IPMDsProcess process)
            {
                Section("阶段 4c：故障模式 —— 真实 Lobby 停止控制下行，验证真实 DS 非 0 退出");

                // 先等到「刚有一条心跳应答发出」再停，让 DS 的 liveness 时钟从接近 0 开始计时。
                long hbReplyStart = coordinator.Counters.HeartbeatReplies;
                long awaitReplyDeadline = Math.Min(NowMs() + 8000L, _deadlineMs);
                while (coordinator.Counters.HeartbeatReplies == hbReplyStart && NowMs() < awaitReplyDeadline)
                {
                    PumpAll();
                    if (coordinator.Counters.HeartbeatReplies != hbReplyStart)
                    {
                        break;
                    }

                    Thread.Sleep(PollIntervalMs);
                }

                long startMs = NowMs();
                _dropStartMs = startMs;
                Timeline("DropControlStart dsPid=" + (process == null ? -1 : process.ProcessId)
                    + " replies=" + coordinator.Counters.HeartbeatReplies);

                long limitMs = Math.Min(startMs + DropControlBlackoutLimitMs, _deadlineMs);
                long nextProgressMs = startMs + 5000L;
                long elapsedMs = 0L;

                // 停控制面下行：只泵两客户端 UDP，不再驱动 host。
                while (true)
                {
                    long now = NowMs();
                    for (int i = 0; i < _clients.Count; i++)
                    {
                        _clients[i].Pump(now, now / 1000L);
                    }

                    elapsedMs = now - startMs;
                    if (_launcher.ExitObservations > 0)
                    {
                        break;
                    }

                    if (now >= nextProgressMs)
                    {
                        nextProgressMs += 5000L;
                        Progress("停下行进度 " + elapsedMs + "ms（DS 仍活="
                            + (process != null && process.IsRunning) + "，等待 Process.Exited）");
                    }

                    if (now >= limitMs)
                    {
                        break;
                    }

                    Thread.Sleep((int)Math.Min(PollIntervalMs, Math.Max(1L, limitMs - now)));
                }

                int exitCode = _launcher.LastExitCode;
                _dropExitCode = exitCode;
                _dropBlackoutMs = elapsedMs;
                Timeline("DropControlEnd blackoutMs=" + elapsedMs + " exitObservations="
                    + _launcher.ExitObservations + " exitCode=" + exitCode);

                Check(_launcher.ExitObservations > 0,
                    "S73m 停止控制下行后真实 DS 进程确实退出（Process.Exited 观测 " + _launcher.ExitObservations
                    + " 次，停下行 " + elapsedMs + "ms）");
                Check(exitCode != int.MinValue && exitCode != 0,
                    "S73n 真实 DS 退出码非 0（实际 " + (exitCode == int.MinValue ? "<不可用>" : exitCode.ToString(CultureInfo.InvariantCulture))
                    + "）⇒ 运行期 liveness 判定失败退出，而不是正常收尾");
                CheckGt(elapsedMs, 9000L, "S73o 停下行到 DS 退出的实测时长 > 9s（实际 " + elapsedMs
                    + "ms；DS RuntimeLivenessTimeoutMs=15000，且停前刚有一条可信下行）");
                Check(coordinator.Counters.ResultAccepted == 0L && _gateway.Results.Count == 0,
                    "S73p 故障路径下没有任何结果被受理（ResultAccepted=0）⇒ 不是「结果→Exited(0)」这条解释");
                Check(_clients[0].ProbeNonceSent == 0 && _clients[1].ProbeNonceSent == 0,
                    "S73q 故障路径下没有发出过 owner 探针（smoke 不会提前结束）");

                string dsLog = DsLogFresh();
                Check(Contains(dsLog, "运行期 15000ms 未收到 Lobby 可信控制帧"),
                    "S73r 真实 DS 日志给出精确根因（运行期 15000ms 未收到 Lobby 可信控制帧）");
                Check(!Contains(dsLog, "收到匹配的 ResultAck"),
                    "S73s 真实 DS 日志里没有 ResultAck（确认这是 liveness 失败而非正常收尾）");

                // 恢复泵控制面：让真实 LobbyHost 观测对端关闭并回收资源/端口/uid。
                long resumeDeadline = Math.Min(NowMs() + 20000L, _deadlineMs);
                while (!_sawRelease && NowMs() < resumeDeadline)
                {
                    PumpAll();
                    if (_sawRelease)
                    {
                        break;
                    }

                    Thread.Sleep(PollIntervalMs);
                }

                Check(_sawRelease, "S73t 对端异常退出后协调器仍完成收尾（SessionReleased）");
                CheckEq(_host.PortPool.InUseCount, 0, "S73u 故障路径同样归还端口池（InUseCount=0）");
                CheckEq(_host.PlayerLedger.Count, 0, "S73v 故障路径同样清空 uid 占用账本");
                Check(!File.Exists(_bootstrapPath), "S73w 故障路径下引导文件也被删除");

                string postBindError;
                Check(TryExclusiveUdpBind(_dsPort, out postBindError),
                    "S73x 故障路径退出后独占 bind 本局 UDP 端口成功（OS 已回收：" + (postBindError ?? "ok") + "）");

                Check(!string.IsNullOrEmpty(_dllSha256) && _dllSha256.Length == 64,
                    "S73y 构建产物预检完整（sha256=" + _dllSha256 + "）");
            }

            // =================================================================================
            //  R4-B：--movement 真实 DS 权威运动/复制验收
            // =================================================================================

            /// <summary>撞墙停住的位置（米）。墙盒 x∈[-0.5,0.5]、胶囊半径 0.4 ⇒ 中心停在 |x|=0.9。</summary>
            private const float MovementWallStopMeters = 0.9f;

            /// <summary>最终停在 ±0.9 的容许误差（含 PhysX contactOffset/skin 的亚毫米级差异）。</summary>
            private const float MovementWallStopTolerance = 0.06f;

            /// <summary>“从未穿墙”的下界：权威 |x| 全程不得小于此值（0.89 = 0.9 - 1cm 容差）。</summary>
            private const float MovementWallNeverInsideAbsX = 0.89f;

            /// <summary>真实 DS 权威起跳峰值的下界（一次 4 m/s 起跳 + g=9.8 理论峰值 ≈ 1.78）。</summary>
            private const float MovementJumpPeakMinY = 1.5f;

            /// <summary>落地后 y 应回到的高度与容差（胶囊中心 Y = 半高 = 1）。</summary>
            private const float MovementGroundY = 1f;
            private const float MovementGroundTolerance = 0.05f;

            /// <summary>
            /// <c>--movement</c>：一个客户端的运动观察台。
            /// AP = 本端副本（真 Driver，预测时间轴）；SP = 对手副本（真 Driver，只插值）。
            /// 所有“权威”断言都读两个副本上复制到的**真实 DS 载荷**，不读预测。
            /// </summary>
            private sealed class MovementRig
            {
                public ClientHarness Client;
                public int OwnUid;
                public int OtherUid;
                public PMR3Player ApPlayer;
                public PMR4MovementDriver ApDriver;
                public PMR3Player SpPlayer;
                public PMR4MovementDriver SpDriver;

                /// <summary>owner 侧经可靠通道派发的权威事件（去重后的 Key 集合即为证据）。</summary>
                public readonly List<PMPredictionEvent> OwnerEvents = new List<PMPredictionEvent>();

                /// <summary>本端（AP）副本上复制到的真实 DS 权威快照轨迹。</summary>
                public readonly List<MovementSample> ApSamples = new List<MovementSample>();

                /// <summary>对手（SP）副本上复制到的真实 DS 权威快照轨迹。</summary>
                public readonly List<MovementSample> SpSamples = new List<MovementSample>();

                public readonly List<string> Notes = new List<string>();

                /// <summary>AP 本地输入帧计数（只用于给 Tick 的 serverFrame 参数一个单调值）。</summary>
                public long LocalFrame;

                public long TicksAccepted;
                public long TicksRejected;

                /// <summary>Space 边沿待消费（某步被拒时不丢，下一个被接受的步消费）。</summary>
                public bool JumpEdgePending;

                /// <summary>
                /// 权威**最新**样本：取输出边界最大的那一条。
                /// 不取“最后 append 的那条”：复制更新在 UDP 上可能轻微乱序到达，
                /// 后 append 的不一定是最新的权威状态，用它会引入偶发飘忽。
                /// </summary>
                public MovementSample NewestAp { get { return NewestSample(ApSamples); } }

                public MovementSample NewestSp { get { return NewestSample(SpSamples); } }

                private static MovementSample NewestSample(List<MovementSample> samples)
                {
                    MovementSample best = new MovementSample();
                    bool has = false;
                    for (int i = 0; i < samples.Count; i++)
                    {
                        if (!has || samples[i].OutputFrame >= best.OutputFrame)
                        {
                            best = samples[i];
                            has = true;
                        }
                    }

                    return best;
                }
            }

            /// <summary>一条来自真实 Unity DS 的权威运动快照样本（已用 PMR4MovementCodec 解码）。</summary>
            private struct MovementSample
            {
                public long WallMs;
                public long OutputFrame;
                public long ServerFrame;
                public uint StreamVersion;
                public double TotalSimTimeMs;
                public PMMoverSyncState Sync;
            }

            /// <summary>
            /// <c>--movement</c> 主阶段：在**探针开闸之前**用真实生成的运动 input RPC 驱动真实 Unity DS，
            /// 逐项验收权威运动/复制/事件/重同步。详情见文件头 --movement 段。
            /// </summary>
            private void RunMovementPhase(PMDsCoordinator coordinator, IPMDsProcess process)
            {
                Section("阶段 4d：--movement 真实 Unity DS 权威运动验收（客户端 AP 用同源 AABB 替身，非 PhysX）");

                Progress("口径：权威位置/模式/事件全部来自真实 Unity DS 的复制与可靠 RPC；"
                    + "客户端 AP 的碰撞世界是 PMMoverTestWorld（WorldVersion 包装为 "
                    + MovementCollisionWorldVersion + "），**不是** PhysX；节拍用真实 Stopwatch 墙钟，输入 dt="
                    + MovementInputStepMs + "ms，DS 预算由 DS 自己的墙钟累计");

                BuildMovementRigs();

                if (_movementRigs.Count != 2)
                {
                    Fail("M1 未能建立两个客户端的运动观察台（实际 " + _movementRigs.Count + " 个）");
                    return;
                }

                CheckMovementCreate();

                // ── 1）向墙移动：真实 DS 权威位移 → 停在 ±0.9 不穿墙 ─────────────

                int[] approachMark = MarkMovementSamples();
                RunMovementWindow("approach-wall", 2400, 1f, 0f, false);
                RunMovementWindow("settle-at-wall", 500, 0f, 0f, false);
                MovementDrain(200);

                for (int i = 0; i < _movementRigs.Count; i++)
                {
                    MovementRig rig = _movementRigs[i];
                    float expectedZ = rig.OwnUid == UidA ? -0.75f : 0.75f;

                    float minAbsX = float.MaxValue;
                    for (int k = approachMark[i]; k < rig.ApSamples.Count; k++)
                    {
                        float absX = Math.Abs(rig.ApSamples[k].Sync.Position.X);
                        if (absX < minAbsX) { minAbsX = absX; }
                    }

                    float finalX = rig.NewestAp.Sync.Position.X;
                    float finalZ = rig.NewestAp.Sync.Position.Z;
                    float finalY = rig.NewestAp.Sync.Position.Y;

                    Progress("[diag] uid=" + rig.OwnUid + " 撞墙：min|X|=" + minAbsX.ToString("0.######", CultureInfo.InvariantCulture)
                        + " 终态=(" + finalX.ToString("0.######", CultureInfo.InvariantCulture) + ","
                        + finalY.ToString("0.######", CultureInfo.InvariantCulture) + ","
                        + finalZ.ToString("0.######", CultureInfo.InvariantCulture) + ") mode=" + rig.NewestAp.Sync.Mode
                        + " 样本=" + rig.ApSamples.Count + " 权威边界=" + rig.NewestAp.OutputFrame);

                    Check(minAbsX >= MovementWallNeverInsideAbsX,
                        "M14 客户端 " + Label(i) + "（uid=" + rig.OwnUid + "）向墙移动全程权威 |x| >= "
                        + MovementWallNeverInsideAbsX.ToString(CultureInfo.InvariantCulture) + "（不穿墙；实际最小 "
                        + minAbsX.ToString("0.######", CultureInfo.InvariantCulture) + "）");

                    Check(Math.Abs(Math.Abs(finalX) - MovementWallStopMeters) <= MovementWallStopTolerance,
                        "M15 客户端 " + Label(i) + " 权威停在墙前 ±" + MovementWallStopMeters.ToString(CultureInfo.InvariantCulture)
                        + "（容差 ±" + MovementWallStopTolerance.ToString(CultureInfo.InvariantCulture) + "；实际 x="
                        + finalX.ToString("0.######", CultureInfo.InvariantCulture) + "）—— 真实 PhysX 胶囊半径 0.4 + 墙面 0.5");

                    Check(Math.Abs(finalZ - expectedZ) <= 0.02f,
                        "M16 客户端 " + Label(i) + " 撞墙过程 z 未被推动（期望 " + expectedZ.ToString(CultureInfo.InvariantCulture)
                        + "，实际 " + finalZ.ToString("0.######", CultureInfo.InvariantCulture) + "；|z| 在墙的 z 范围内 ⇒ 确实是撞墙而非绕过");

                    Check(rig.NewestAp.Sync.Mode == PMMoverMode.Walking && rig.NewestAp.Sync.Grounded,
                        "M17 客户端 " + Label(i) + " 撞墙后仍是 Walking 且着地（mode=" + rig.NewestAp.Sync.Mode
                        + " grounded=" + rig.NewestAp.Sync.Grounded + "）");

                    Check(Math.Abs(finalX + 3f) > 1.0f,
                        "M18 客户端 " + Label(i) + " 权威位移是输入驱动的真实位移（|Δx|>1m；从 x=-3 到 x="
                        + finalX.ToString("0.######", CultureInfo.InvariantCulture) + "）");
                }

                // ── 2）退开 → Space 起跳 → 峰值 > 1.5 → 落回 y≈1 / Walking ────

                RunMovementWindow("back-off", 800, -1f, 0f, false);
                RunMovementWindow("settle-before-jump", 500, 0f, 0f, false);
                MovementDrain(200);

                int[] jumpMark = MarkMovementSamples();
                float[] xBeforeJump = new float[_movementRigs.Count];
                for (int i = 0; i < _movementRigs.Count; i++)
                {
                    xBeforeJump[i] = _movementRigs[i].NewestAp.Sync.Position.X;
                }

                RunMovementWindow("jump", 1500, 0f, 0f, true);
                MovementDrain(350);

                for (int i = 0; i < _movementRigs.Count; i++)
                {
                    MovementRig rig = _movementRigs[i];

                    float maxY = float.MinValue;
                    float minY = float.MaxValue;
                    bool sawFallingMode = false;
                    for (int k = jumpMark[i]; k < rig.ApSamples.Count; k++)
                    {
                        float y = rig.ApSamples[k].Sync.Position.Y;
                        if (y > maxY) { maxY = y; }
                        if (y < minY) { minY = y; }
                        if (rig.ApSamples[k].Sync.Mode == PMMoverMode.Falling) { sawFallingMode = true; }
                    }

                    float finalY = rig.NewestAp.Sync.Position.Y;

                    Progress("[diag] uid=" + rig.OwnUid + " 起跳：maxY=" + maxY.ToString("0.######", CultureInfo.InvariantCulture)
                        + " minY=" + minY.ToString("0.######", CultureInfo.InvariantCulture)
                        + " finalY=" + finalY.ToString("0.######", CultureInfo.InvariantCulture)
                        + " mode=" + rig.NewestAp.Sync.Mode + " grounded=" + rig.NewestAp.Sync.Grounded
                        + " 退开前x=" + xBeforeJump[i].ToString("0.######", CultureInfo.InvariantCulture)
                        + " 落点x=" + rig.NewestAp.Sync.Position.X.ToString("0.######", CultureInfo.InvariantCulture));

                    Check(rig.TicksRejected == 0L,
                        "M19 客户端 " + Label(i) + " 全程 AP 预测步无一步被拒（接受 " + rig.TicksAccepted
                        + "，拒 " + rig.TicksRejected + "；否则边沿/输入会丢）");

                    Check(maxY > MovementJumpPeakMinY,
                        "M20 客户端 " + Label(i) + " Space 边沿使真实 DS 权威起跳：峰值 y > "
                        + MovementJumpPeakMinY.ToString(CultureInfo.InvariantCulture) + "（实际 "
                        + maxY.ToString("0.######", CultureInfo.InvariantCulture) + "；一次 4 m/s 起跳 + g=9.8 的理论峰值 ≈1.78）");

                    Check(sawFallingMode,
                        "M21 客户端 " + Label(i) + " 起跳期间权威模式出现过 Falling（真的离地，而不是被地面挡住的假跳）");

                    Check(Math.Abs(finalY - MovementGroundY) <= MovementGroundTolerance,
                        "M22 客户端 " + Label(i) + " 落回 y≈" + MovementGroundY.ToString(CultureInfo.InvariantCulture)
                        + "（实际 " + finalY.ToString("0.######", CultureInfo.InvariantCulture) + "）");

                    Check(rig.NewestAp.Sync.Mode == PMMoverMode.Walking && rig.NewestAp.Sync.Grounded,
                        "M23 客户端 " + Label(i) + " 落地终态 = Walking + 着地（mode=" + rig.NewestAp.Sync.Mode
                        + " grounded=" + rig.NewestAp.Sync.Grounded + "）");
                }

                // ── 3）owner 可靠事件：Mode/Landed 各 kind 到达且 Key 去重 ────────

                for (int i = 0; i < _movementRigs.Count; i++)
                {
                    MovementRig rig = _movementRigs[i];

                    HashSet<ulong> keys = new HashSet<ulong>();
                    int duplicateKeys = 0;
                    int modeChangedCount = 0;
                    int landedCount = 0;
                    bool sawToFalling = false;
                    bool sawToWalking = false;
                    bool sawLandedWalking = false;

                    for (int k = 0; k < rig.OwnerEvents.Count; k++)
                    {
                        PMPredictionEvent evt = rig.OwnerEvents[k];
                        if (!keys.Add(evt.Key)) { duplicateKeys++; }

                        if (evt.Kind == PMR4MovementDriver.EventKindModeChanged)
                        {
                            modeChangedCount++;
                            if (evt.Value == (int)PMMoverMode.Falling) { sawToFalling = true; }
                            if (evt.Value == (int)PMMoverMode.Walking) { sawToWalking = true; }
                        }
                        else if (evt.Kind == PMR4MovementDriver.EventKindLanded)
                        {
                            landedCount++;
                            if (evt.Value == (int)PMMoverMode.Walking) { sawLandedWalking = true; }
                        }
                    }

                    Progress("[diag] uid=" + rig.OwnUid + " owner 事件：总数=" + rig.OwnerEvents.Count
                        + " 唯一Key=" + keys.Count + " 重Key=" + duplicateKeys
                        + " ModeChanged=" + modeChangedCount + " Landed=" + landedCount
                        + " journalAccepted=" + rig.ApDriver.Journal.AcceptedBatches
                        + " dispatched=" + rig.ApDriver.Journal.DispatchedEvents
                        + " duplicateEvents=" + rig.ApDriver.Journal.DuplicateEvents
                        + " seqGaps=" + rig.ApDriver.Journal.SequenceGaps + " failed=" + rig.ApDriver.Journal.Failed);

                    Check(rig.OwnerEvents.Count > 0,
                        "M24 客户端 " + Label(i) + " owner 经**可靠通道**收到权威运动事件（" + rig.OwnerEvents.Count + " 条）");

                    Check(duplicateKeys == 0,
                        "M25 客户端 " + Label(i) + " 事件 Key 无重复派发（收到 " + rig.OwnerEvents.Count
                        + " 条，重复 Key " + duplicateKeys + " 条）⇒ 去重成立");

                    Check(modeChangedCount >= 2 && sawToFalling && sawToWalking,
                        "M26 客户端 " + Label(i) + " ModeChanged（kind=" + PMR4MovementDriver.EventKindModeChanged
                        + "）至少 2 条且含 →Falling 与 →Walking（实际 " + modeChangedCount
                        + "，ToFalling=" + sawToFalling + " ToWalking=" + sawToWalking + "）");

                    Check(landedCount >= 1 && sawLandedWalking,
                        "M27 客户端 " + Label(i) + " Landed（kind=" + PMR4MovementDriver.EventKindLanded
                        + "）至少 1 条且值 = Walking（实际 " + landedCount + "）");

                    Check(!rig.ApDriver.Journal.Failed && rig.ApDriver.Journal.SequenceGaps == 0L,
                        "M28 客户端 " + Label(i) + " 事件日志无失败且无序号缺口（failed=" + rig.ApDriver.Journal.Failed
                        + " seqGaps=" + rig.ApDriver.Journal.SequenceGaps + "）");

                    Check(rig.ApDriver.EventPayloadsReceived > 0 && rig.ApDriver.EventBatchesAccepted > 0,
                        "M29 客户端 " + Label(i) + " 事件确实经 ClientMovementEventsV1 到达并被日志接纳（收到载荷 "
                        + rig.ApDriver.EventPayloadsReceived + "，批 " + rig.ApDriver.EventBatchesAccepted + "）");
                }

                // ── 4）对手 SP 副本：真实 DS 运动快照 / 最终状态 / 插值 ──────────

                for (int i = 0; i < _movementRigs.Count; i++)
                {
                    MovementRig rig = _movementRigs[i];

                    float maxSpY = float.MinValue;
                    float minSpAbsX = float.MaxValue;
                    for (int k = 0; k < rig.SpSamples.Count; k++)
                    {
                        float y = rig.SpSamples[k].Sync.Position.Y;
                        float ax = Math.Abs(rig.SpSamples[k].Sync.Position.X);
                        if (y > maxSpY) { maxSpY = y; }
                        if (k >= approachMark[i] && ax < minSpAbsX) { minSpAbsX = ax; }
                    }

                    MovementSample lastAp = rig.NewestAp;
                    MovementSample lastSp = rig.NewestSp;
                    float driftX = Math.Abs(lastSp.Sync.Position.X - lastAp.Sync.Position.X);
                    float driftY = Math.Abs(lastSp.Sync.Position.Y - lastAp.Sync.Position.Y);

                    PMInterpolatedState<PMMoverSyncState, PMMoverAuxState> presentation = rig.SpDriver.SamplePresentation();
                    float presentDrift = presentation.HasValue
                        ? Math.Abs(presentation.Sync.Position.X - lastSp.Sync.Position.X) : float.MaxValue;

                    Progress("[diag] SP uid=" + rig.OtherUid + " 样本=" + rig.SpSamples.Count
                        + " maxY=" + maxSpY.ToString("0.######", CultureInfo.InvariantCulture)
                        + " min|X|=" + minSpAbsX.ToString("0.######", CultureInfo.InvariantCulture)
                        + " 与AP终态差=(Δx=" + driftX.ToString("0.######", CultureInfo.InvariantCulture)
                        + ",Δy=" + driftY.ToString("0.######", CultureInfo.InvariantCulture) + ")"
                        + " 插值权威接受=" + rig.SpDriver.Interpolation.AuthorityAccepted);

                    Check(rig.SpSamples.Count > 0,
                        "M30 客户端 " + Label(i) + " 的对手 SP 副本收到真实 DS 运动快照（" + rig.SpSamples.Count + " 个样本）");

                    Check(minSpAbsX <= MovementWallStopMeters + MovementWallStopTolerance,
                        "M31 客户端 " + Label(i) + " 的 SP 副本也看到撞墙停住（最小 |x|="
                        + minSpAbsX.ToString("0.######", CultureInfo.InvariantCulture) + "）");

                    Check(maxSpY > MovementJumpPeakMinY,
                        "M32 客户端 " + Label(i) + " 的 SP 副本也看到起跳峰值（maxY="
                        + maxSpY.ToString("0.######", CultureInfo.InvariantCulture) + "）");

                    Check(driftX <= 0.05f && driftY <= 0.05f,
                        "M33 客户端 " + Label(i) + " 的 SP 最终状态与 AP 侧观察到的权威终态一致（Δx="
                        + driftX.ToString("0.######", CultureInfo.InvariantCulture) + " Δy="
                        + driftY.ToString("0.######", CultureInfo.InvariantCulture) + "）");

                    Check(rig.SpDriver.Interpolation.AuthorityAccepted > 0L,
                        "M34 客户端 " + Label(i) + " 的 SP 插值缓冲真的接纳了权威（AuthorityAccepted="
                        + rig.SpDriver.Interpolation.AuthorityAccepted + "）");

                    Check(presentation.HasValue && presentDrift <= 0.3f,
                        "M35 客户端 " + Label(i) + " 的 SP 只插值不外推且贴着最近权威值（HasValue="
                        + presentation.HasValue + " 与最近权威差=" + presentDrift.ToString("0.######", CultureInfo.InvariantCulture) + "）");
                }

                // ── 5）重同步升流：旧流输入无效、新流继续推进 ─────────────────

                RunMovementWindow("settle-for-resync", 600, 0f, 0f, false);
                MovementDrain(250);

                for (int i = 0; i < _movementRigs.Count; i++)
                {
                    RunMovementResyncCheck(_movementRigs[i]);
                }

                Check(coordinator.Counters.ResultAccepted == 0L && _gateway.Results.Count == 0,
                    "M60 运动验收期间真实 DS 没有提交/受理任何结果（ResultAccepted="
                    + coordinator.Counters.ResultAccepted + "；探针闸门确实挡住了 smoke 提前结束）");

                Check(process != null && process.IsRunning && _launcher.ExitObservations == 0,
                    "M61 运动验收期间真实 DS 进程始终存活（Exited 事件=" + _launcher.ExitObservations + "）");
            }

            /// <summary>建立两个客户端的运动观察台（真 Driver + 真实 DS 快照轨迹订阅）。</summary>
            private void BuildMovementRigs()
            {
                _movementRigs.Clear();

                for (int i = 0; i < _clients.Count; i++)
                {
                    ClientHarness client = _clients[i];
                    MovementRig rig = new MovementRig();
                    rig.Client = client;
                    rig.OwnUid = client.Offer.Identity.Uid;
                    rig.OtherUid = rig.OwnUid == UidA ? UidB : UidA;

                    rig.ApPlayer = client.FindReplica(rig.OwnUid);
                    rig.SpPlayer = client.FindReplica(rig.OtherUid);

                    uint epoch = client.Offer.Epoch;

                    string apError = null;
                    if (rig.ApPlayer != null)
                    {
                        try
                        {
                            rig.ApDriver = PMR4MovementDriver.CreateFromPlayerInitialSnapshot(
                                rig.ApPlayer, new MovementCollisionStub(), epoch, out apError);
                        }
                        catch (Exception ex)
                        {
                            apError = "构造异常 " + ex.GetType().Name + " " + ex.Message;
                        }
                    }
                    else
                    {
                        apError = "本端副本不存在";
                    }

                    string spError = null;
                    if (rig.SpPlayer != null)
                    {
                        try
                        {
                            rig.SpDriver = PMR4MovementDriver.CreateFromPlayerInitialSnapshot(
                                rig.SpPlayer, new MovementCollisionStub(), epoch, out spError);
                        }
                        catch (Exception ex)
                        {
                            spError = "构造异常 " + ex.GetType().Name + " " + ex.Message;
                        }
                    }
                    else
                    {
                        spError = "对手副本不存在";
                    }

                    _movementRigs.Add(rig);

                    Check(rig.ApPlayer != null && rig.ApPlayer.Role == PMNetRole.AutonomousProxy,
                        "M1 客户端 " + Label(i) + " 本端副本角色 = AP（uid=" + rig.OwnUid + "）");
                    Check(rig.SpPlayer != null && rig.SpPlayer.Role == PMNetRole.SimulatedProxy,
                        "M2 客户端 " + Label(i) + " 对手副本角色 = SP（uid=" + rig.OtherUid + "）");
                    Check(rig.ApDriver != null,
                        "M3 客户端 " + Label(i) + " AP Driver 由 Create 初值建立成功"
                        + (rig.ApDriver == null ? "（" + (apError ?? "?") + "）" : ""));
                    Check(rig.SpDriver != null,
                        "M4 客户端 " + Label(i) + " SP Driver 由 Create 初值建立成功"
                        + (rig.SpDriver == null ? "（" + (spError ?? "?") + "）" : ""));
                    Check(rig.SpDriver == null || rig.SpDriver.Timeline == null,
                        "M5 客户端 " + Label(i) + " 的 SP 副本没有预测时间轴（只插值，不预测）");
                    Check(rig.ApDriver == null || rig.ApDriver.Timeline != null,
                        "M6 客户端 " + Label(i) + " 的 AP 副本有预测时间轴（真 Driver，不是桩件）");

                    if (rig.ApPlayer != null)
                    {
                        rig.ApPlayer.MovementSnapshotUpdated += delegate(PMR3Player p) { RecordMovementSample(rig.ApSamples, p); };
                        RecordMovementSample(rig.ApSamples, rig.ApPlayer);
                    }

                    if (rig.SpPlayer != null)
                    {
                        rig.SpPlayer.MovementSnapshotUpdated += delegate(PMR3Player p) { RecordMovementSample(rig.SpSamples, p); };
                        RecordMovementSample(rig.SpSamples, rig.SpPlayer);
                    }

                    if (rig.ApDriver != null)
                    {
                        rig.ApDriver.EventDispatched += delegate(PMPredictionEvent evt) { rig.OwnerEvents.Add(evt); };
                        rig.LocalFrame = rig.ApDriver.OutputBoundary.IsValid ? rig.ApDriver.OutputBoundary.Value : 0L;
                    }

                    Notes(rig, "ap=" + (rig.ApDriver == null ? "<none>" : "ok")
                        + " sp=" + (rig.SpDriver == null ? "<none>" : "ok"));
                }
            }

            /// <summary>Create 初值与出生点验收（证明「Create 带非空初值」而不是靠重同步补）。</summary>
            private void CheckMovementCreate()
            {
                for (int i = 0; i < _movementRigs.Count; i++)
                {
                    MovementRig rig = _movementRigs[i];
                    uint epoch = rig.Client.Offer.Epoch;

                    byte[] payload = null;
                    rig.Client.MovementInitialPayloadByUid.TryGetValue(rig.OwnUid, out payload);
                    if (payload == null && rig.ApPlayer != null)
                    {
                        payload = rig.ApPlayer.MovementSnapshotPayload;
                    }

                    Check(payload != null && payload.Length > 0,
                        "M7 客户端 " + Label(i) + " 的 Create 记录带**非空**运动初值载荷（实际 "
                        + (payload == null ? 0 : payload.Length) + " 字节）—— DS 在首次生命周期 Flush 前发布了初值");

                    if (payload == null || payload.Length == 0)
                    {
                        continue;
                    }

                    PMR4MovementSnapshotBlob blob = null;
                    string error = null;
                    bool ok = PMR4MovementCodec.TryDecodeSnapshot(payload, 0, payload.Length,
                        PMR4PayloadKind.Snapshot, out blob, out error);

                    Check(ok && blob != null,
                        "M8 客户端 " + Label(i) + " 的 Create 初值可按 V1 完整解码（kind=Snapshot；" + (error ?? "ok") + "）");

                    if (!ok || blob == null)
                    {
                        continue;
                    }

                    float expectedZ = rig.OwnUid == UidA ? -0.75f : 0.75f;

                    CheckEq(blob.StreamVersion, 1u,
                        "M9 客户端 " + Label(i) + " Create 初值的 streamVersion = 1（uid=" + rig.OwnUid + "）");
                    CheckEq(blob.Epoch, epoch,
                        "M10 客户端 " + Label(i) + " Create 初值的 epoch = 局 epoch（" + epoch + "）");
                    CheckEq(blob.OutputFrame, 0L,
                        "M11 客户端 " + Label(i) + " Create 初值的输出边界 = 0（初值确实是初值，不是中期快照）");
                    CheckEq(blob.Aux.CollisionWorldVersion, MovementCollisionWorldVersion,
                        "M12 客户端 " + Label(i) + " Create 初值的碰撞世界版本 = DS 的 MovementWorldVersion="
                        + MovementCollisionWorldVersion + "（客户端替身必须同值，否则 Driver 拒建）");
                    CheckEq(blob.InstanceId, rig.ApPlayer.NetId.Value,
                        "M13 客户端 " + Label(i) + " Create 初值的 instanceId = 副本 NetId（"
                        + rig.ApPlayer.NetId.Value + "）");

                    Check(blob.Sync.Mode == PMMoverMode.Walking && blob.Sync.Grounded,
                        "M13b 客户端 " + Label(i) + " Create 初值模式 = Walking 且已着地");

                    Check(Math.Abs(blob.Sync.Position.X + 3f) <= 0.001f
                          && Math.Abs(blob.Sync.Position.Y - 1f) <= 0.001f
                          && Math.Abs(blob.Sync.Position.Z - expectedZ) <= 0.001f,
                        "M13c 客户端 " + Label(i) + " Create 出生点 = (-3,1," + expectedZ.ToString(CultureInfo.InvariantCulture)
                        + ")（墙外 ±3、名册行居中×1.5、y=半高；实际 " + DescribePosition(blob.Sync.Position) + "）");

                    Check(Math.Abs(blob.Sync.Position.X) >= 3f && Math.Abs(blob.Sync.Position.Z) <= 4f,
                        "M13d 客户端 " + Label(i) + " 出生点确实在测试墙盒外且在墙的 z 范围内（"
                        + DescribePosition(blob.Sync.Position) + "；否则撞墙用例不成立）");
                }
            }

            /// <summary>把一条真实 DS 复制快照解码后追加进轨迹（解码失败不抛，按缺失处理）。</summary>
            private void RecordMovementSample(List<MovementSample> target, PMR3Player player)
            {
                if (target == null || player == null)
                {
                    return;
                }

                byte[] payload = player.MovementSnapshotPayload;
                if (payload == null || payload.Length == 0)
                {
                    return;
                }

                PMR4MovementSnapshotBlob blob = null;
                string error = null;
                if (!PMR4MovementCodec.TryDecodeSnapshot(payload, 0, payload.Length,
                        PMR4PayloadKind.Snapshot, out blob, out error) || blob == null)
                {
                    return;
                }

                MovementSample sample = new MovementSample();
                sample.WallMs = NowMs();
                sample.OutputFrame = blob.OutputFrame;
                sample.ServerFrame = blob.ServerFrame;
                sample.StreamVersion = blob.StreamVersion;
                sample.TotalSimTimeMs = blob.TotalSimTimeMs;
                sample.Sync = blob.Sync;
                target.Add(sample);
            }

            private int[] MarkMovementSamples()
            {
                int[] marks = new int[_movementRigs.Count];
                for (int i = 0; i < _movementRigs.Count; i++)
                {
                    marks[i] = _movementRigs[i].ApSamples.Count;
                }

                return marks;
            }

            /// <summary>
            /// 按真实墙钟 16ms 节拍跑一个运动窗口：每拍让两个客户端的 AP 各推进一步（Tick + 真实生成桩上行），
            /// 再泵控制面与两条 UDP。totalMs 是**真实墙钟**时长；每步 sleep ≤8ms（不长 sleep）。
            /// jumpEdge=true 时在窗口第一个**被接受**的步上置 Space 边沿（被拒不丢）。
            /// </summary>
            private void RunMovementWindow(string label, int totalMs, float moveX, float moveZ, bool jumpEdge)
            {
                if (jumpEdge)
                {
                    for (int i = 0; i < _movementRigs.Count; i++)
                    {
                        _movementRigs[i].JumpEdgePending = true;
                    }
                }

                System.Diagnostics.Stopwatch wall = System.Diagnostics.Stopwatch.StartNew();
                System.Diagnostics.Stopwatch beat = System.Diagnostics.Stopwatch.StartNew();
                long beats = 0L;

                while (true)
                {
                    long target = beats * MovementInputStepMs;
                    long elapsed = beat.ElapsedMilliseconds;
                    if (elapsed < target)
                    {
                        Thread.Sleep((int)Math.Min(8L, Math.Max(1L, target - elapsed)));
                        continue;
                    }

                    if (wall.ElapsedMilliseconds >= totalMs || NowMs() >= _deadlineMs)
                    {
                        break;
                    }

                    MovementDriveStep(moveX, moveZ);
                    beats++;
                }

                Progress("运动窗口 " + label + "：真实墙钟 " + wall.ElapsedMilliseconds + "ms / " + beats
                    + " 步（输入 dt=" + MovementInputStepMs + "ms，墙钟节拍 " + MovementInputStepMs + "ms）");
            }

            /// <summary>
            /// 宿主每帧必做的那一步（真实 PMClientSessionHost.PumpMovement 同次序）：
            ///   全部 Driver <c>Update(elapsedMs)</c>（它是**入站队列的唯一排空入口**：复制快照 / 可靠事件 /
            ///   重同步载荷都只能在这里被应用）→ SP 再 <c>Advance</c> + <c>SamplePresentation</c>。
            /// 少了这一步，inbound 可靠载荷（例如显式重同步）会一直躺在队列里不被应用。
            /// </summary>
            private void MovementPumpDrivers(double elapsedMs)
            {
                for (int i = 0; i < _movementRigs.Count; i++)
                {
                    MovementRig rig = _movementRigs[i];

                    if (rig.ApDriver != null && !rig.ApDriver.IsDisposed)
                    {
                        rig.ApDriver.Update(elapsedMs);
                    }

                    if (rig.SpDriver != null && !rig.SpDriver.IsDisposed)
                    {
                        rig.SpDriver.Update(elapsedMs);
                        rig.SpDriver.Advance(elapsedMs);
                        rig.SpDriver.SamplePresentation();
                    }
                }
            }

            /// <summary>两个客户端各推进一步：AP Tick（接受才发）→ 真实生成桩上行 → 泵 UDP/控制面。</summary>
            private void MovementDriveStep(float moveX, float moveZ)
            {
                MovementPumpDrivers(MovementInputStepMs);

                for (int i = 0; i < _movementRigs.Count; i++)
                {
                    MovementRig rig = _movementRigs[i];
                    if (rig.ApDriver == null || rig.ApDriver.IsDisposed)
                    {
                        continue;
                    }

                    PMMoverInput input = new PMMoverInput();
                    input.MoveX = moveX;
                    input.MoveZ = moveZ;
                    input.MoveY = 0f;
                    input.YawDegrees = 0f;
                    input.JumpPressed = rig.JumpEdgePending;
                    input.Effects = null;
                    input.Layers = null;
                    input.RemovedLayerIds = null;

                    rig.LocalFrame++;
                    PMFrameId serverFrame = new PMFrameId(PMFrameDomain.AuthorityServer, rig.LocalFrame);

                    PMTickResult result = rig.ApDriver.Tick(MovementInputStepMs, input, serverFrame);
                    if (result.Accepted)
                    {
                        rig.TicksAccepted++;
                        rig.JumpEdgePending = false;

                        // 真实生成的 input RPC：ServerMovementInputV1（Unreliable + ForceValidate）。
                        rig.ApDriver.SendInputPayload();
                    }
                    else
                    {
                        rig.TicksRejected++;
                    }
                }

                PumpAll();
            }

            /// <summary>
            /// 运动阶段专用等待：每步都跑「端点泵 + Driver Update」（真实宿主每帧都做），短步不长 sleep。
            /// 不用通用 <see cref="WaitUntil"/>，因为那个只泵端点，入站可靠载荷不会被应用。
            /// </summary>
            private bool MovementWaitFor(Func<bool> condition, int timeoutMs)
            {
                long deadline = Math.Min(NowMs() + timeoutMs, _deadlineMs);
                while (true)
                {
                    PumpAll();
                    MovementPumpDrivers(MovementInputStepMs);

                    if (condition())
                    {
                        return true;
                    }

                    long now = NowMs();
                    if (now >= deadline)
                    {
                        return false;
                    }

                    Thread.Sleep((int)Math.Min(MovementInputStepMs, Math.Max(1L, deadline - now)));
                }
            }

            /// <summary>只泵（不发新输入）：把在途的复制快照/可靠事件/重同步载荷收完，短步不长 sleep。</summary>
            private void MovementDrain(int totalMs)
            {
                System.Diagnostics.Stopwatch wall = System.Diagnostics.Stopwatch.StartNew();
                while (wall.ElapsedMilliseconds < totalMs && NowMs() < _deadlineMs)
                {
                    PumpAll();
                    MovementPumpDrivers(MovementInputStepMs);
                    Thread.Sleep(MovementInputStepMs);
                }

                PumpAll();
                MovementPumpDrivers(MovementInputStepMs);
            }

            /// <summary>
            /// 重同步升流验收：
            ///   ① 客户端经**真实生成桩** <c>ServerMovementResyncV1</c> 请求重同步
            ///      ⇒ DS 升流 + 下发完整可信快照（Reliable，owner-only）；
            ///   ② 用**旧流代次**编码一包合法 V1 载荷（帧号正好是 DS 期望的下一帧）手工上行：
            ///      若旧流被接纳，胶囊会立即向 +X 反向加速；断言权威 x **从未反向** ⇒ 旧流输入无效；
            ///   ③ 对照组：**同一批帧号、同一结构**，只把 streamVersion 换成新流 ⇒ 必须被接纳并驱动位移
            ///      ⇒ 差别只能来自流代次；
            ///   ④ 断言新流下权威输出边界继续推进（新流继续推进）。
            /// </summary>
            private void RunMovementResyncCheck(MovementRig rig)
            {
                if (rig.ApDriver == null || rig.ApPlayer == null)
                {
                    Fail("M36 客户端 " + rig.OwnUid + " 没有 AP Driver，重同步验收无法进行");
                    return;
                }

                uint oldStream = rig.ApDriver.StreamVersion;
                uint instanceId = rig.ApPlayer.NetId.Value;
                uint epoch = rig.Client.Offer.Epoch;

                long boundaryAtRequest = rig.NewestAp.OutputFrame;
                long apResyncAppliedBefore = rig.ApDriver.ResyncApplied;
                long apResyncPayloadsBefore = rig.ApDriver.ResyncPayloadsReceived;

                Progress("[diag] 重同步前 uid=" + rig.OwnUid + " " + rig.ApDriver.Describe()
                    + " | snapshotApplied=" + rig.ApDriver.SnapshotPayloadsApplied
                    + " snapshotRejectedStreamNewer=" + rig.ApDriver.SnapshotRejectedStreamNewer
                    + " snapshotRebinds=" + rig.ApDriver.SnapshotStreamRebinds
                    + " resyncReceived=" + rig.ApDriver.ResyncPayloadsReceived
                    + " resyncApplied=" + rig.ApDriver.ResyncApplied
                    + " resyncRegressing=" + rig.ApDriver.ResyncRejectedRegressing
                    + " resyncOldStream=" + rig.ApDriver.ResyncRejectedOldStream
                    + " resyncInlineRejected=" + rig.ApDriver.ResyncInlineRejected
                    + " resyncRequestsSent=" + rig.ApDriver.ResyncRequestsSent);

                // ① 真实生成桩上行（Reliable + ForceValidate）；Owner 校验由生成物负责。
                rig.ApPlayer.ServerMovementResyncV1(oldStream);

                bool rebound = MovementWaitFor(delegate { return rig.ApDriver.StreamVersion == oldStream + 1u; }, 8000);
                MovementDrain(200);

                Progress("[diag] 重同步后 uid=" + rig.OwnUid + " " + rig.ApDriver.Describe()
                    + " | snapshotApplied=" + rig.ApDriver.SnapshotPayloadsApplied
                    + " snapshotRejectedStreamNewer=" + rig.ApDriver.SnapshotRejectedStreamNewer
                    + " snapshotRejectedIdentity=" + rig.ApDriver.SnapshotRejectedIdentity
                    + " snapshotRejectedWorldVersion=" + rig.ApDriver.SnapshotRejectedWorldVersion
                    + " snapshotRebinds=" + rig.ApDriver.SnapshotStreamRebinds
                    + " rebindRejectedRegressing=" + rig.ApDriver.SnapshotRebindRejectedRegressing
                    + " resyncReceived=" + rig.ApDriver.ResyncPayloadsReceived
                    + " resyncApplied=" + rig.ApDriver.ResyncApplied
                    + " resyncRegressing=" + rig.ApDriver.ResyncRejectedRegressing
                    + " resyncOldStream=" + rig.ApDriver.ResyncRejectedOldStream
                    + " resyncInlineRejected=" + rig.ApDriver.ResyncInlineRejected
                    + " resyncRequestsSent=" + rig.ApDriver.ResyncRequestsSent
                    + " frozen=" + rig.ApDriver.IsFrozen
                    + " | 复制快照 stream=" + rig.NewestAp.StreamVersion
                    + " 边界=" + rig.NewestAp.OutputFrame
                    + " 共 " + rig.ApSamples.Count + " 个样本");

                // DS 侧升流是否真的发生（以**复制下来的原始快照**为准，不依赖 AP 是否采纳）。
                uint observedNewStream = 0u;
                for (int k = 0; k < rig.ApSamples.Count; k++)
                {
                    if (rig.ApSamples[k].StreamVersion > observedNewStream)
                    {
                        observedNewStream = rig.ApSamples[k].StreamVersion;
                    }
                }

                Check(observedNewStream == oldStream + 1u,
                    "M36 客户端 " + rig.OwnUid + " DS 真的升了流：复制快照的 streamVersion " + oldStream
                    + " → " + observedNewStream + "（由真实 ServerMovementResyncV1 生成桩触发）");
                Check(rig.ApDriver.ResyncApplied > apResyncAppliedBefore
                      && rig.ApDriver.StreamVersion == observedNewStream,
                    "M37 客户端 " + rig.OwnUid + " AP 应用了显式重同步并重绑到新流（ResyncApplied "
                    + apResyncAppliedBefore + " → " + rig.ApDriver.ResyncApplied + "，driver.StreamVersion="
                    + rig.ApDriver.StreamVersion + "，收到重同步载荷 " + apResyncPayloadsBefore + " → "
                    + rig.ApDriver.ResyncPayloadsReceived + "，拒绝-倒退=" + rig.ApDriver.ResyncRejectedRegressing
                    + "，拒绝-旧流=" + rig.ApDriver.ResyncRejectedOldStream
                    + "，入站拒绝=" + rig.ApDriver.ResyncInlineRejected + "）");

                uint newStream = observedNewStream;

                // 帧号对齐（很关键）：重绑后 AP 时间轴的 PendingFrame 与 DS 输入缓冲的 _nextInputFrame
                // 都等于重同步快照的 OutputFrame（契约「输入帧 n 产出边界 n+1」），因此 DS 期望的下一帧
                // 就是 OutputBoundary 本身，**不是** OutputBoundary+1。写成 +1 会让 DS 看到缺帧，
                // 500ms 后触发多余的第二次升流。
                long nextFrame = rig.ApDriver.OutputBoundary.Value;
                float xBeforeOldStream = rig.NewestAp.Sync.Position.X;
                long boundaryBeforeOldStream = rig.NewestAp.OutputFrame;

                // ② 旧流反向输入：+X（当前朝向是 −X / 静止），帧号正好是 DS 期望的下一帧。
                // 每拍只发 1 帧：DS 的长期消耗率就是 1 帧/16ms 真实墙钟，一次多帧会把内部队列
                // （容量 128）越推越满，反而会让 DS 把后面的帧当成“远未来”拒收 —— 那就不是我们想测的东西了。
                byte[] oldPayload = BuildMovementInputPayload(epoch, instanceId, oldStream, nextFrame, 1, 1f, 0f);

                PMR4MovementInputBatch decodedOld = null;
                string decodeError = null;
                bool oldWellFormed = PMR4MovementCodec.TryDecodeInputs(oldPayload, 0, oldPayload.Length,
                    out decodedOld, out decodeError);

                Check(oldWellFormed && decodedOld != null && decodedOld.StreamVersion == oldStream
                      && decodedOld.Entries != null && decodedOld.Entries.Length == 1
                      && decodedOld.Entries[0].InputFrame == nextFrame,
                    "M39 客户端 " + rig.OwnUid + " 旧流注入包本身是**合法** V1 载荷（唯一致差是 streamVersion="
                    + oldStream + "；帧号=" + nextFrame + " 正是 DS 期望的下一帧；" + (decodeError ?? "ok") + "）");

                long oldFramesSent;
                RunManualInputWindow(rig, oldStream, nextFrame, 1, 1f, 0f, 420, out oldFramesSent);
                MovementDrain(150);

                float maxXDuringOldStream = float.MinValue;
                for (int k = 0; k < rig.ApSamples.Count; k++)
                {
                    if (rig.ApSamples[k].OutputFrame > boundaryBeforeOldStream
                        && rig.ApSamples[k].Sync.Position.X > maxXDuringOldStream)
                    {
                        maxXDuringOldStream = rig.ApSamples[k].Sync.Position.X;
                    }
                }

                float xAfterOldStream = rig.NewestAp.Sync.Position.X;

                Progress("[diag] uid=" + rig.OwnUid + " 旧流注入：发 " + oldFramesSent + " 帧（stream=" + oldStream
                    + "，帧号从 " + nextFrame + " 起）→ 权威 x " + xBeforeOldStream.ToString("0.######", CultureInfo.InvariantCulture)
                    + " → " + xAfterOldStream.ToString("0.######", CultureInfo.InvariantCulture)
                    + "（窗口内最大 x=" + (maxXDuringOldStream == float.MinValue ? float.NaN.ToString(CultureInfo.InvariantCulture)
                        : maxXDuringOldStream.ToString("0.######", CultureInfo.InvariantCulture)) + "）");

                Check(oldFramesSent >= 15,
                    "M40 客户端 " + rig.OwnUid + " 旧流窗口真的按 16ms 墙钟发了足够多的帧（" + oldFramesSent + " 帧）");

                Check(maxXDuringOldStream <= xBeforeOldStream + 0.05f
                      && xAfterOldStream <= xBeforeOldStream + 0.02f,
                    "M41 客户端 " + rig.OwnUid + " 旧流输入无效：旧流帧未让真实 DS 权威 x 反向（窗口内最大 x="
                    + (maxXDuringOldStream == float.MinValue ? "<无样本>"
                        : maxXDuringOldStream.ToString("0.######", CultureInfo.InvariantCulture))
                    + "，窗口末 x=" + xAfterOldStream.ToString("0.######", CultureInfo.InvariantCulture)
                    + "，窗口前 x=" + xBeforeOldStream.ToString("0.######", CultureInfo.InvariantCulture)
                    + "；若被接纳会在 " + MovementInputStepMs + "ms 内反向加速）");

                // ③ 对照组：同一帧号、同一结构，只换 streamVersion → 必须被接纳。
                float xBeforeControl = rig.NewestAp.Sync.Position.X;
                long boundaryBeforeControl = rig.NewestAp.OutputFrame;
                long controlFramesSent;
                RunManualInputWindow(rig, newStream, nextFrame, 1, -1f, 0f, 900, out controlFramesSent);
                MovementDrain(250);

                float xAfterControl = rig.NewestAp.Sync.Position.X;

                Progress("[diag] uid=" + rig.OwnUid + " 新流对照：发 " + controlFramesSent + " 帧（stream=" + newStream
                    + "，同一批帧号起于 " + nextFrame + "）→ 权威 x " + xBeforeControl.ToString("0.######", CultureInfo.InvariantCulture)
                    + " → " + xAfterControl.ToString("0.######", CultureInfo.InvariantCulture)
                    + "；权威边界 " + boundaryBeforeControl + " → " + rig.NewestAp.OutputFrame);

                Check(xAfterControl < xBeforeControl - 0.5f,
                    "M42 客户端 " + rig.OwnUid + " 对照组：同帧号的新流包被接纳并驱动真实 DS 位移（Δx="
                    + (xAfterControl - xBeforeControl).ToString("0.######", CultureInfo.InvariantCulture)
                    + "，发 " + controlFramesSent + " 帧）⇒ 差别只能来自 streamVersion");

                Check(rig.NewestAp.OutputFrame > boundaryBeforeControl,
                    "M43 客户端 " + rig.OwnUid + " 新流继续推进：权威输出边界 " + boundaryBeforeControl + " → "
                    + rig.NewestAp.OutputFrame + "（输入帧 n 产出边界 n+1）");

                Check(rig.ApDriver.StreamVersion == newStream && newStream == oldStream + 1u,
                    "M44 客户端 " + rig.OwnUid + " 全程只升了**一次**流（" + oldStream + " → " + newStream
                    + "），旧流注入没有触发额外升流");

                Notes(rig, "resync " + oldStream + "→" + newStream + " 旧流帧=" + oldFramesSent
                    + " 对照帧=" + controlFramesSent + " 边界=" + boundaryAtRequest + "→" + rig.NewestAp.OutputFrame);
            }

            /// <summary>
            /// 手工编排一包上行输入（帧号从 <paramref name="firstFrame"/> 起严格升序，共 <paramref name="count"/> 条）。
            /// 编码器对非法本地数据抛异常 ⇒ 这里用它的真实调用路径，不另造报文。
            /// </summary>
            private static byte[] BuildMovementInputPayload(uint epoch, uint instanceId, uint streamVersion,
                long firstFrame, int count, float moveX, float moveZ)
            {
                PMR4MovementInputEntry[] entries = new PMR4MovementInputEntry[count];
                for (int i = 0; i < count; i++)
                {
                    PMR4MovementInputEntry entry = new PMR4MovementInputEntry();
                    entry.InputFrame = firstFrame + i;
                    entry.StepMs = MovementInputStepMs;
                    entry.Input = MakeMovementInput(moveX, moveZ, false);
                    entries[i] = entry;
                }

                return PMR4MovementCodec.EncodeInputs(epoch, instanceId, streamVersion, entries);
            }

            private static PMMoverInput MakeMovementInput(float moveX, float moveZ, bool jumpPressed)
            {
                PMMoverInput input = new PMMoverInput();
                input.MoveX = moveX;
                input.MoveZ = moveZ;
                input.MoveY = 0f;
                input.YawDegrees = 0f;
                input.JumpPressed = jumpPressed;
                input.Effects = null;
                input.Layers = null;
                input.RemovedLayerIds = null;
                return input;
            }

            /// <summary>
            /// 手工窗口：每 16ms 墙钟用**指定流代次**编码下一帧输入并经真实生成桩上行（**不**经 AP Driver 的 tick），
            /// 然后泵 UDP。这是 --movement 里唯一「绕过 Driver 发输入」的通道，专门用来构造
            /// 「同帧号、同结构、只有 streamVersion 不同」的 A/B 对照。
            /// </summary>
            private void RunManualInputWindow(MovementRig rig, uint streamVersion, long firstFrame, int batchCount,
                float moveX, float moveZ, int totalMs, out long framesSent)
            {
                uint epoch = rig.Client.Offer.Epoch;
                uint instanceId = rig.ApPlayer.NetId.Value;

                framesSent = 0L;

                System.Diagnostics.Stopwatch wall = System.Diagnostics.Stopwatch.StartNew();
                System.Diagnostics.Stopwatch beat = System.Diagnostics.Stopwatch.StartNew();
                long beats = 0L;
                long frameCursor = firstFrame;

                while (true)
                {
                    long target = beats * MovementInputStepMs;
                    long elapsed = beat.ElapsedMilliseconds;
                    if (elapsed < target)
                    {
                        Thread.Sleep((int)Math.Min(8L, Math.Max(1L, target - elapsed)));
                        continue;
                    }

                    if (wall.ElapsedMilliseconds >= totalMs || NowMs() >= _deadlineMs)
                    {
                        break;
                    }

                    byte[] payload = BuildMovementInputPayload(epoch, instanceId, streamVersion,
                        frameCursor, batchCount, moveX, moveZ);

                    // 真实生成的调用桩（owner 校验/Condition/可靠性都由生成物决定）。
                    rig.ApPlayer.ServerMovementInputV1(payload);
                    framesSent += batchCount;
                    frameCursor += batchCount;

                    PumpAll();
                    MovementPumpDrivers(MovementInputStepMs);
                    beats++;
                }
            }

            private void Notes(MovementRig rig, string text)
            {
                if (rig != null && rig.Notes.Count < 64)
                {
                    rig.Notes.Add(text);
                }
            }

            private static string DescribePosition(PMVector3 position)
            {
                return "(" + position.X.ToString("0.###", CultureInfo.InvariantCulture)
                    + "," + position.Y.ToString("0.###", CultureInfo.InvariantCulture)
                    + "," + position.Z.ToString("0.###", CultureInfo.InvariantCulture) + ")";
            }

            /// <summary>--movement 的完整证据落盘（轨迹/事件/诊断），供主侧复核时逐值对照。</summary>
            private void WriteMovementArtifacts()
            {
                StringBuilder builder = new StringBuilder();
                builder.AppendLine("# R4-B --movement 真实 Unity DS 权威运动验收证据");
                builder.AppendLine("# 权威来源：真实 Unity DS（PhysX 隔离物理场景）复制下来的 _movementSnapshotV1 与 owner 可靠事件");
                builder.AppendLine("# 客户端 AP 碰撞世界：PMMoverTestWorld（AABB 替身，WorldVersion=" + MovementCollisionWorldVersion + "）——不是 PhysX");
                builder.AppendLine("# 输入 dt=" + MovementInputStepMs + "ms；节拍=真实 Stopwatch 墙钟；DS 预算由 DS 自己的墙钟累计");
                builder.AppendLine();

                for (int i = 0; i < _movementRigs.Count; i++)
                {
                    MovementRig rig = _movementRigs[i];
                    builder.AppendLine("## 客户端 " + Label(i) + "（本端 uid=" + rig.OwnUid + "，对手 uid=" + rig.OtherUid + "）");

                    for (int n = 0; n < rig.Notes.Count; n++)
                    {
                        builder.AppendLine("note: " + rig.Notes[n]);
                    }

                    builder.AppendLine("apTicksAccepted=" + rig.TicksAccepted + " apTicksRejected=" + rig.TicksRejected);
                    builder.AppendLine("apResyncApplied=" + (rig.ApDriver == null ? -1 : rig.ApDriver.ResyncApplied)
                        + " apStreamVersion=" + (rig.ApDriver == null ? 0u : rig.ApDriver.StreamVersion)
                        + " apUnacked=" + (rig.ApDriver == null ? -1 : rig.ApDriver.UnackedInputCount)
                        + " apSnapshotApplied=" + (rig.ApDriver == null ? -1 : rig.ApDriver.SnapshotPayloadsApplied)
                        + " spInterpAuthorityAccepted=" + (rig.SpDriver == null ? -1 : rig.SpDriver.Interpolation.AuthorityAccepted));

                    if (rig.ApDriver != null)
                    {
                        builder.AppendLine("apDescribe: " + rig.ApDriver.Describe());
                        builder.AppendLine("apCounters: snapshotQueued=" + rig.ApDriver.SnapshotPayloadsQueued
                            + " snapshotApplied=" + rig.ApDriver.SnapshotPayloadsApplied
                            + " snapshotDecodeFailed=" + rig.ApDriver.SnapshotDecodeFailed
                            + " snapshotRejectedIdentity=" + rig.ApDriver.SnapshotRejectedIdentity
                            + " snapshotRejectedStreamNewer=" + rig.ApDriver.SnapshotRejectedStreamNewer
                            + " snapshotRejectedWorldVersion=" + rig.ApDriver.SnapshotRejectedWorldVersion
                            + " snapshotQueueDrops=" + rig.ApDriver.SnapshotQueueDrops
                            + " snapshotPublished=" + rig.ApDriver.SnapshotPayloadsPublished
                            + " snapshotStreamRebinds=" + rig.ApDriver.SnapshotStreamRebinds
                            + " snapshotRebindRejectedRegressing=" + rig.ApDriver.SnapshotRebindRejectedRegressing
                            + " snapshotRebindRejectedFrozen=" + rig.ApDriver.SnapshotRebindRejectedFrozen
                            + " rebindFallbacks=" + rig.ApDriver.RebindFallbacks
                            + " inputPayloadsSent=" + rig.ApDriver.InputPayloadsSent
                            + " inputEntriesSent=" + rig.ApDriver.InputEntriesSent
                            + " unackedRetired=" + rig.ApDriver.UnackedRetired
                            + " ticks=" + rig.ApDriver.Ticks
                            + " ticksRejected=" + rig.ApDriver.TicksRejected
                            + " placeholders=" + rig.ApDriver.Placeholders
                            + " eventPayloadsReceived=" + rig.ApDriver.EventPayloadsReceived
                            + " eventBatchesAccepted=" + rig.ApDriver.EventBatchesAccepted
                            + " eventBatchesRejected=" + rig.ApDriver.EventBatchesRejected
                            + " eventDecodeFailed=" + rig.ApDriver.EventDecodeFailed
                            + " journalFailed=" + rig.ApDriver.Journal.Failed
                            + " journalSeqGaps=" + rig.ApDriver.Journal.SequenceGaps
                            + " journalDuplicateEvents=" + rig.ApDriver.Journal.DuplicateEvents
                            + " journalDispatched=" + rig.ApDriver.Journal.DispatchedEvents
                            + " journalLateDispatches=" + rig.ApDriver.Journal.LateDispatches
                            + " journalOverflows=" + rig.ApDriver.Journal.Overflows
                            + " resyncRequestsSent=" + rig.ApDriver.ResyncRequestsSent
                            + " resyncPayloadsReceived=" + rig.ApDriver.ResyncPayloadsReceived
                            + " resyncApplied=" + rig.ApDriver.ResyncApplied
                            + " resyncRejectedRegressing=" + rig.ApDriver.ResyncRejectedRegressing
                            + " resyncRejectedOldStream=" + rig.ApDriver.ResyncRejectedOldStream
                            + " resyncInlineRejected=" + rig.ApDriver.ResyncInlineRejected
                            + " frozen=" + rig.ApDriver.IsFrozen
                            + " threadViolations=" + rig.ApDriver.ThreadViolations);
                    }

                    builder.AppendLine("# owner 可靠事件（kind " + PMR4MovementDriver.EventKindModeChanged
                        + "=ModeChanged, " + PMR4MovementDriver.EventKindLanded + "=Landed）");
                    for (int k = 0; k < rig.OwnerEvents.Count; k++)
                    {
                        builder.AppendLine("event kind=" + rig.OwnerEvents[k].Kind + " value=" + rig.OwnerEvents[k].Value
                            + " key=" + rig.OwnerEvents[k].Key);
                    }

                    builder.AppendLine("# AP 侧收到的真实 DS 权威快照轨迹（wallMs,outputFrame,serverFrame,stream,totalMs,x,y,z,mode,grounded,velX,velY,velZ）");
                    for (int k = 0; k < rig.ApSamples.Count; k++)
                    {
                        builder.AppendLine(DescribeMovementSample(rig.ApSamples[k]));
                    }

                    builder.AppendLine("# 对手 SP 侧收到的真实 DS 权威快照轨迹");
                    for (int k = 0; k < rig.SpSamples.Count; k++)
                    {
                        builder.AppendLine(DescribeMovementSample(rig.SpSamples[k]));
                    }

                    builder.AppendLine();
                }

                WriteArtifact("movement-evidence.txt", builder.ToString());
                Progress("运动证据已写入 " + Path.Combine(_artifactsRoot, "movement-evidence.txt")
                    + "（" + builder.Length + " 字符）");

                long accepted = 0L;
                long rejected = 0L;
                StringBuilder streams = new StringBuilder();
                for (int i = 0; i < _movementRigs.Count; i++)
                {
                    MovementRig rig = _movementRigs[i];
                    accepted += rig.TicksAccepted;
                    rejected += rig.TicksRejected;
                    if (i > 0) { streams.Append(' '); }
                    streams.Append("uid").Append(rig.OwnUid).Append(":stream=")
                        .Append(rig.ApDriver == null ? 0u : rig.ApDriver.StreamVersion)
                        .Append(",boundary=").Append(rig.NewestAp.OutputFrame)
                        .Append(",events=").Append(rig.OwnerEvents.Count)
                        .Append(",apSamples=").Append(rig.ApSamples.Count)
                        .Append(",spSamples=").Append(rig.SpSamples.Count);
                }

                _movementApTicksAccepted = accepted;
                _movementApTicksRejected = rejected;
                _movementStreamSummary = streams.Length == 0 ? "<none>" : streams.ToString();
            }

            private static string DescribeMovementSample(MovementSample sample)
            {
                return sample.WallMs + "," + sample.OutputFrame + "," + sample.ServerFrame + "," + sample.StreamVersion
                    + "," + sample.TotalSimTimeMs.ToString("0.###", CultureInfo.InvariantCulture)
                    + "," + sample.Sync.Position.X.ToString("0.######", CultureInfo.InvariantCulture)
                    + "," + sample.Sync.Position.Y.ToString("0.######", CultureInfo.InvariantCulture)
                    + "," + sample.Sync.Position.Z.ToString("0.######", CultureInfo.InvariantCulture)
                    + "," + sample.Sync.Mode
                    + "," + sample.Sync.Grounded
                    + "," + sample.Sync.Velocity.X.ToString("0.######", CultureInfo.InvariantCulture)
                    + "," + sample.Sync.Velocity.Y.ToString("0.######", CultureInfo.InvariantCulture)
                    + "," + sample.Sync.Velocity.Z.ToString("0.######", CultureInfo.InvariantCulture);
            }

            private static string Label(int index)
            {
                return index == 0 ? "A" : (index == 1 ? "B" : ("#" + index));
            }

            private void Preflight()
            {
                Section("阶段 0：构建产物预检（不复用旧 DS 冒充更新）");

                bool dllExists = File.Exists(DsAssemblyPath);
                Check(dllExists, "S1 真实 DS 托管程序集存在：" + DsAssemblyPath);

                if (dllExists)
                {
                    FileInfo info = new FileInfo(DsAssemblyPath);
                    _dllMtime = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    _dllSize = info.Length;
                    _dllSha256 = Sha256OfFile(DsAssemblyPath);

                    Progress("Assembly-CSharp.dll mtime=" + _dllMtime + " size=" + _dllSize + " sha256=" + _dllSha256);

                    byte[] bytes = null;
                    try
                    {
                        bytes = File.ReadAllBytes(DsAssemblyPath);
                    }
                    catch (Exception ex)
                    {
                        Warn("读取程序集失败：" + ex.GetType().Name);
                    }

                    Check(bytes != null, "S2 程序集可读（大小 " + _dllSize + " 字节）");
                    Check(_dllSize > 100000, "S3 程序集体积合理（>100KB，非空壳）");

                    for (int i = 0; i < RequiredDsSymbols.Length; i++)
                    {
                        Check(ContainsAscii(bytes, RequiredDsSymbols[i]),
                            "S" + (4 + i) + " 程序集含关键符号 " + RequiredDsSymbols[i]);
                    }

                    // 本轮真实长局验证的前提：用户重 build 后的程序集必须含控制面保活修复符号。
                    Check(ContainsAscii(bytes, "RuntimeLivenessTimeoutMs") && ContainsAscii(bytes, "LastTrustedReceiveMs"),
                        "S7b 程序集含控制面保活修复符号（RuntimeLivenessTimeoutMs + LastTrustedReceiveMs）");

                    // R4-B 运动符号：--movement 下是硬前置（不得拿旧包验新代码）；
                    // 默认 smoke 下只记录到 preflight.txt（不改变原默认门禁）。
                    StringBuilder movementSymbols = new StringBuilder();
                    for (int i = 0; i < RequiredDsMovementSymbols.Length; i++)
                    {
                        bool present = ContainsAscii(bytes, RequiredDsMovementSymbols[i]);
                        movementSymbols.Append(RequiredDsMovementSymbols[i]).Append('=').Append(present ? "1" : "0").Append(' ');

                        if (_movementMode)
                        {
                            Check(present, "S7c." + (i + 1) + " 程序集含 R4-B 运动符号 " + RequiredDsMovementSymbols[i]);
                        }
                    }

                    _movementSymbolRecord = movementSymbols.ToString().Trim();
                    Progress("R4-B 运动符号（--movement 下逐项硬判定，否则只记录）：" + _movementSymbolRecord);
                }
                else
                {
                    Fail("S2-S7 程序集缺失，符号预检无法进行");
                }

                Check(File.Exists(DsExecutablePath), "S8 白名单可执行文件存在：" + DsExecutablePath);

                WriteArtifact("preflight.txt",
                    "assembly=" + DsAssemblyPath + Environment.NewLine
                    + "mtime=" + _dllMtime + Environment.NewLine
                    + "size=" + _dllSize.ToString(CultureInfo.InvariantCulture) + Environment.NewLine
                    + "sha256=" + _dllSha256 + Environment.NewLine
                    + "exe=" + DsExecutablePath + Environment.NewLine
                    + "exeMtime=" + (File.Exists(DsExecutablePath)
                        ? File.GetLastWriteTime(DsExecutablePath).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                        : "<missing>") + Environment.NewLine
                    + "protocolHash=0x" + PMR3Runtime.ProtocolHash.ToString("X8", CultureInfo.InvariantCulture) + Environment.NewLine
                    + "collisionDigest=0x" + PMR3Runtime.CollisionDigest.ToString("X8", CultureInfo.InvariantCulture) + Environment.NewLine
                    + "movementSymbols=" + (_movementSymbolRecord ?? "<none>") + Environment.NewLine
                    + "movementMode=" + _movementMode + Environment.NewLine);
            }

            // ── 驱动 / 等待 ──────────────────────────────────────────────────────

            private void PumpAll()
            {
                long nowMs = NowMs();
                long nowUnix = nowMs / 1000L;

                for (int i = 0; i < _clients.Count; i++)
                {
                    _clients[i].Pump(nowMs, nowUnix);
                }

                if (_host != null)
                {
                    _host.PumpOnce();
                }
            }

            /// <summary>有界等待；每步 ≤50ms，且整轮不超过总预算。绝不长 sleep。</summary>
            private bool WaitUntil(string label, Func<bool> condition, int timeoutMs)
            {
                long deadline = Math.Min(NowMs() + timeoutMs, _deadlineMs);

                while (true)
                {
                    PumpAll();
                    if (condition())
                    {
                        Progress("满足：" + label + "（累计 " + (NowMs() - _startedAtMs) + "ms）");
                        return true;
                    }

                    long now = NowMs();
                    if (now >= deadline)
                    {
                        break;
                    }

                    if (now - _lastProgressMs >= 5000L)
                    {
                        _lastProgressMs = now;
                        Progress("等待中：" + label + "（剩余约 " + (deadline - now) + "ms）");
                    }

                    int sleep = (int)Math.Min(PollIntervalMs, Math.Max(1L, deadline - now));
                    Thread.Sleep(sleep);
                }

                PumpAll();
                bool ok = condition();
                if (!ok)
                {
                    Progress("超时：" + label + "（" + timeoutMs + "ms 内未满足）");
                }

                return ok;
            }

            // ── DS 日志 ─────────────────────────────────────────────────────────

            private string DsLog()
            {
                if (string.IsNullOrEmpty(_dsLogPath))
                {
                    return string.Empty;
                }

                long now = NowMs();
                if (_dsLogCache != null && now - _dsLogReadAtMs < 500L)
                {
                    return _dsLogCache;
                }

                string text = ReadTextTail(_dsLogPath, 512 * 1024);
                if (text != null)
                {
                    _dsLogCache = text;
                    _dsLogReadAtMs = now;
                }

                return _dsLogCache ?? string.Empty;
            }

            private static bool Contains(string haystack, string needle)
            {
                return !string.IsNullOrEmpty(haystack) && haystack.IndexOf(needle, StringComparison.Ordinal) >= 0;
            }

            /// <summary>强制重新读一次 DS 日志（不做 500ms 缓存），用于「刚刚必须已经写到盘上」的证据检查。</summary>
            private string DsLogFresh()
            {
                _dsLogCache = null;
                _dsLogReadAtMs = 0L;
                return DsLog();
            }

            private static bool containsReason(string reason, string needle)
            {
                return Contains(reason, needle);
            }

            private static int ReplicaCount(ClientHarness client)
            {
                return client == null ? -1 : client.ReplicasByUid.Count;
            }

            /// <summary>某客户端上某个 uid 副本的 ProbeCount（副本不存在返回 -1，避免空引用掩盖失败）。</summary>
            private static int ProbeCountOf(ClientHarness client, int uid)
            {
                if (client == null)
                {
                    return -1;
                }

                PMR3Player player = client.FindReplica(uid);
                return player == null ? -1 : player.ProbeCount;
            }

            /// <summary>有界地统计子串出现次数（用于 DS 日志证据）。</summary>
            private static int CountOccurrences(string text, string needle)
            {
                if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle))
                {
                    return 0;
                }

                int count = 0;
                int index = 0;
                while (true)
                {
                    index = text.IndexOf(needle, index, StringComparison.Ordinal);
                    if (index < 0)
                    {
                        break;
                    }

                    count++;
                    index += needle.Length;
                }

                return count;
            }

            /// <summary>解析真实 DS 日志里所有 "hbRx=N" 的最大 N（DS 自报收到的可信 Lobby 下行数）；无则 -1。</summary>
            private static long MaxHeartbeatRx(string text)
            {
                if (string.IsNullOrEmpty(text))
                {
                    return -1L;
                }

                const string marker = "hbRx=";
                long max = -1L;
                int index = 0;
                while (true)
                {
                    index = text.IndexOf(marker, index, StringComparison.Ordinal);
                    if (index < 0)
                    {
                        break;
                    }

                    int start = index + marker.Length;
                    int end = start;
                    while (end < text.Length && text[end] >= '0' && text[end] <= '9')
                    {
                        end++;
                    }

                    long value;
                    if (end > start && long.TryParse(text.Substring(start, end - start),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > max)
                    {
                        max = value;
                    }

                    index = end > start ? end : start;
                }

                return max;
            }

            private static bool InReservedRange(int first, int last)
            {
                int[] reserved = new int[] { 7777, 7778, 7800 };
                for (int i = 0; i < reserved.Length; i++)
                {
                    if (reserved[i] >= first && reserved[i] <= last)
                    {
                        return true;
                    }
                }

                return false;
            }

            private void Warn(string message)
            {
                if (_warnings.Count < 4000)
                {
                    _warnings.Add(message);
                }
            }

            private void Timeline(string entry)
            {
                _timeline.Add(NowMs() + " " + entry);
            }

            private void WriteArtifact(string name, string content)
            {
                try
                {
                    File.WriteAllText(Path.Combine(_artifactsRoot, name), content, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    Warn("写 artifact 失败 " + name + "：" + ex.GetType().Name);
                }
            }

            // ── 归档与摘要 ───────────────────────────────────────────────────────

            private void ArchiveEvidence(bool strictControlFrames)
            {
                IPMDsProcess process = _launcher.Processes.Count > 0 ? _launcher.Processes[0] : null;

                string stdoutTail = process == null ? "<none>" : process.StdOutTail;
                string stderrTail = process == null ? "<none>" : process.StdErrTail;

                WriteArtifact("ds-stdout-tail.txt", stdoutTail ?? string.Empty);
                WriteArtifact("ds-stderr-tail.txt", stderrTail ?? string.Empty);
                WriteArtifact("ds-log-tail.txt", ReadTextTail(_dsLogPath, 512 * 1024) ?? string.Empty);
                WriteArtifact("timeline.txt", string.Join(Environment.NewLine, _timeline) + Environment.NewLine
                    + Environment.NewLine + "warnings:" + Environment.NewLine
                    + string.Join(Environment.NewLine, _warnings));

                Progress("DS 日志：" + _dsLogPath + "（" + (DsLog().Length) + " 字节）");
                Progress("DS stdout 累计 " + (process == null ? 0 : process.StdOutTotalBytes)
                    + " 字节 / stderr 累计 " + (process == null ? 0 : process.StdErrTotalBytes) + " 字节");
                Progress("本局 UDP 端口 " + _dsPort + "：运行期独占 bind 失败（被 DS 持有）→ 退出后 bind 成功（已回收）");

                CheckEq(_host.PumpExceptions, 0L, "S120 Lobby 泵循环没有出现异常（PumpExceptions=0）");
                if (strictControlFrames)
                {
                    CheckEq(_host.ControlFramesUndeliverable, 0L, "S121 没有任何控制帧投递失败（ControlFramesUndeliverable=0）");
                }
                else
                {
                    // 故障路径：对端突然死亡后，本端仍可能把一条已排队的空闲帧（例如对 Error 的回执）
                    // 投递到已关闭的连接上，因此「零投递失败」只适用于正常闭环，不适用于故障路径。
                    Progress("故障路径观察（不作断言）：ControlFramesUndeliverable=" + _host.ControlFramesUndeliverable
                        + "（对端突然死亡后确有空闲控制帧无处投递）");
                }
            }

            public void WriteSummaryFile()
            {
                if (string.IsNullOrEmpty(_artifactsRoot))
                {
                    return;
                }

                StringBuilder builder = new StringBuilder();
                builder.AppendLine("=== R3-B6 真实 Unity DS 进程烟测摘要 ===");
                builder.AppendLine("matchId=" + (_matchId ?? "<none>"));
                builder.AppendLine("dsId=" + (_dsId ?? "<none>"));
                builder.AppendLine("dsPid=" + _dsPid);
                builder.AppendLine("controlPort=" + _controlPort);
                builder.AppendLine("dsUdpPort=" + _dsPort);
                builder.AppendLine("bootstrap=" + (_bootstrapPath ?? "<none>"));
                builder.AppendLine("dsLog=" + (_dsLogPath ?? "<none>"));
                builder.AppendLine("assemblyMtime=" + (_dllMtime ?? "<none>"));
                builder.AppendLine("assemblySize=" + _dllSize);
                builder.AppendLine("assemblySha256=" + (_dllSha256 ?? "<none>"));
                builder.AppendLine("movementMode=" + _movementMode);
                builder.AppendLine("movementSymbols=" + (_movementSymbolRecord ?? "<none>"));
                builder.AppendLine("movementApTicksAccepted=" + _movementApTicksAccepted);
                builder.AppendLine("movementApTicksRejected=" + _movementApTicksRejected);
                builder.AppendLine("movementStreams=" + _movementStreamSummary);
                builder.AppendLine("resultSummaryBytes=" + (_resultSummary == null ? 0 : _resultSummary.Length));
                builder.AppendLine("exitedTerminal=" + _sawExitedTerminal);
                builder.AppendLine("processExited=" + _sawProcessExit);
                builder.AppendLine("osExitCode=" + (_osExitCode == int.MinValue ? "<none>" : _osExitCode.ToString(CultureInfo.InvariantCulture)));
                builder.AppendLine("osExitObservations=" + _launcher.ExitObservations);
                builder.AppendLine("lobbyTerminalReason=" + (_lobbyTerminalReason ?? "<none>"));
                builder.AppendLine("sessionReleased=" + _sawRelease);
                builder.AppendLine("midRunPortProbeError=" + (_midRunPortProbeError ?? "<none>"));
                builder.AppendLine("postExitPortProbeError=" + (_postExitPortProbeError ?? "<ok>"));
                builder.AppendLine("coordinatorKillRequests=" + _killRequestsAtTerminal);
                builder.AppendLine("coordinatorGracefulExitObserved=" + _gracefulExitObservedAtTerminal);
                builder.AppendLine("coordinatorGracefulExitWaits=" + _gracefulExitWaitsAtTerminal);
                builder.AppendLine("coordinatorGracefulExitTimeouts=" + _gracefulExitTimeoutsAtTerminal);
                builder.AppendLine("coordinatorDeferredCleanupTicks=" + _deferredCleanupTicksAtTerminal);
                builder.AppendLine("exitedTerminalAtMs=" + _exitedTerminalAtMs);
                builder.AppendLine("osExitObservedAtMs=" + _osExitObservedAtMs);
                builder.AppendLine("holdSecondsConfigured=" + _holdSeconds);
                builder.AppendLine("holdStartWall=" + (_holdStartWall ?? "<none>"));
                builder.AppendLine("holdEndWall=" + (_holdEndWall ?? "<none>"));
                builder.AppendLine("holdStartMs=" + _holdStartMs);
                builder.AppendLine("holdEndMs=" + _holdEndMs);
                builder.AppendLine("holdMilliseconds=" + _holdMilliseconds);
                builder.AppendLine("holdHeartbeats=" + _holdHeartbeats);
                builder.AppendLine("holdHeartbeatReplies=" + _holdHeartbeatReplies);
                builder.AppendLine("holdDsAliveSamples=" + _holdAliveSamples);
                builder.AppendLine("holdDsLogHeartbeatTicks=" + _holdDsLogHeartbeatTicks);
                builder.AppendLine("holdDsMaxHbRx=" + _holdDsMaxHbRx);
                builder.AppendLine("dropControlAfterReady=" + _dropControlAfterReady);
                builder.AppendLine("dropBlackoutMs=" + _dropBlackoutMs);
                builder.AppendLine("dropExitCode=" + (_dropExitCode == int.MinValue ? "<none>" : _dropExitCode.ToString(CultureInfo.InvariantCulture)));
                builder.AppendLine("checks=" + CheckCount + " passed=" + PassedCount + " failed=" + FailureCount);

                string[] failures = FailureSnapshot();
                for (int i = 0; i < failures.Length; i++)
                {
                    builder.AppendLine("FAIL: " + failures[i]);
                }

                WriteArtifact("summary.txt", builder.ToString());
                Console.WriteLine("    摘要已写入 " + Path.Combine(_artifactsRoot, "summary.txt"));
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                PMR3Runtime.PlayerReplicated -= OnPlayerReplicated;

                // 运动观察台先释放：Driver 先 Freeze 再 Dispose，避免释放后还有入站回调。
                for (int i = 0; i < _movementRigs.Count; i++)
                {
                    MovementRig rig = _movementRigs[i];
                    if (rig.ApDriver != null)
                    {
                        try { rig.ApDriver.Freeze(); } catch (Exception) { }
                        try { rig.ApDriver.Dispose(); } catch (Exception) { }
                        rig.ApDriver = null;
                    }

                    if (rig.SpDriver != null)
                    {
                        try { rig.SpDriver.Freeze(); } catch (Exception) { }
                        try { rig.SpDriver.Dispose(); } catch (Exception) { }
                        rig.SpDriver = null;
                    }
                }

                _movementRigs.Clear();

                for (int i = 0; i < _clients.Count; i++)
                {
                    try { _clients[i].Dispose(); }
                    catch (Exception) { }
                }

                _clients.Clear();

                if (_host != null)
                {
                    try { _host.Stop(); }
                    catch (Exception) { }

                    try { _host.Dispose(); }
                    catch (Exception) { }

                    _host = null;
                }

                // 只回收本工具自己启动的进程（绝不触碰用户进程/服务）。
                for (int i = 0; i < _launcher.Processes.Count; i++)
                {
                    IPMDsProcess process = _launcher.Processes[i];
                    if (process == null)
                    {
                        continue;
                    }

                    try
                    {
                        if (process.IsRunning)
                        {
                            Progress("finally：强杀本测试启动的 DS 进程 pid=" + process.ProcessId);
                            process.Kill();
                        }
                    }
                    catch (Exception)
                    {
                    }

                    try { process.Dispose(); }
                    catch (Exception) { }
                }
            }
        }
    }
}
