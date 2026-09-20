// R3-B5 主集成门禁：Lobby→DS 控制面 + 真实 UDP 两客户端数据面（有界非 Unity 测试宿主）。
//
// 事实来源：Docs/plans/net-r3-control-contract.md §7（冻结接口）、
//           Docs/plans/net-architecture-migration.md 的 R3B5 行、
//           Docs/plans/_r3b_lobby_report.md / _r3b_network_report.md / _r3b_unity_report.md（三组产物与遗留）。
//
// 这条门禁要证明的是**跨模块闭环**（单模块门禁各自都绿也可能接不起来）：
//   LobbyHost 真的分配端口/uid → 真的原子发布引导文件 → 受控进程替身拿到固定参数
//   → 测试宿主按真实次序读引导文件、建世界/桥、OpenServer、起真实 PMDsLobbyAgent
//   → 真 TCP 上 Ready（真实 MAC/局/世代）→ Lobby 真的发 PMDS1 offer（真实 PMDsEntryCodec）
//   → 两个客户端真实解码 offer 后 OpenClient → 真 UDP 握手/激活/复制/RPC 收敛
//   → 测试主动触发 smoke 结果 → ResultAck → DS 显式 Exited(0) → 模拟进程退出
//   → Lobby 确认退出后回收端口与 uid。
//
// **边界（诚实口径）**：
//   - 唯一的边界外替身是**受控进程替身**（IPMDsProcessLauncher/IPMDsProcess）。不拉起真实
//     HyldDS.exe —— 那需要 Unity 构建（T42）。因此这里只证明控制面+数据面接线，不代表真实 Unity DS。
//   - Unity 宿主（PMDsSessionHost / PMClientSessionHost）含 UnityEngine，不在此编译；本门禁的
//     测试宿主复刻它们的**非 Unity 核心次序**，并在报告里明确区分。
//   - 不注入任何绕开应用 codec/真实 TCP/UDP 主体 的消息：offer 走真实 PMDsEntryCodec，
//     控制面走真实 TCP + 真实分帧 + 真实 MAC，数据面走真实 UDP + 真实 Transport/Replication/RPC。
//
// 运行：dotnet Tools/PMR3IntegrationTest/bin/Release/net8.0/PMR3IntegrationTest.dll
// 退出码：0 = 全部通过；1 = 存在失败

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using PMNet;
using PMNet.Control;
using PMNet.R3;
using PMNet.Session;
using PMNet.Unity;

namespace PMR3IntegrationTest
{
    internal static class Program
    {
        /// <summary>名册第 1 名玩家的 uid（uid/PlayerId 只用票据里的认证身份，不从业务包采纳）。</summary>
        private const int UidA = 201;

        /// <summary>名册第 2 名玩家。</summary>
        private const int UidB = 202;

        /// <summary>每 Pump 轮推进的虚拟毫秒（真实 socket + 虚拟时钟，测试不 sleep 等超时）。</summary>
        private const int StepMs = 20;

        /// <summary>单个阶段的 Pump 轮预算（上界，避免无界循环）。</summary>
        private const int PhaseBudgetRounds = 700;

        private static int _passed;
        private static int _checks;
        private static readonly List<string> _failures = new List<string>();

        private static int Main()
        {
            Console.WriteLine("=== R3-B5：Lobby→DS 控制 + 真实 UDP 两客户端集成（有界非 Unity 宿主）===");
            Console.WriteLine();

            try
            {
                // 生成表必须先注册：摘要/ClassId/桩的唯一来源（未注册时 ProtocolHash 为 0）。
                PMR3Runtime.Register();

                Check(PMR3Runtime.IsRegistered && PMR3Runtime.ProtocolHash != 0u,
                    "G1 PMR3 生成表已封板（hash=0x" + PMR3Runtime.ProtocolHash.ToString("X8", CultureInfo.InvariantCulture) + "）");
                Check(PMR3Runtime.CollisionDigest == 0x52334201u, "G2 固定测试碰撞摘要 = 契约 §7.4 冻结值");

                using (Fixture fixture = new Fixture())
                {
                    fixture.RunHappyPath();
                    fixture.RunCrashBeforeResult();
                    fixture.RunResultAcceptedThenCrash();
                }
            }
            catch (Exception ex)
            {
                Fail("门禁出现未捕获异常：" + ex.GetType().Name + " " + ex.Message
                     + Environment.NewLine + ex.StackTrace);
            }
            finally
            {
                PMR3Runtime.Shutdown();
            }

            Console.WriteLine();
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
        //  断言
        // =================================================================================

        private static void Check(bool ok, string label)
        {
            _checks++;
            if (ok) { _passed++; } else { _failures.Add(label); }
            Console.WriteLine((ok ? "    OK   " : "    FAIL ") + label);
        }

        private static void CheckEq(object actual, object expected, string label)
        {
            Check(Equals(actual, expected), label + "（实际 " + (actual == null ? "<null>" : actual.ToString())
                + "，期望 " + (expected == null ? "<null>" : expected.ToString()) + "）");
        }

        private static void CheckGt(long actual, long bound, string label)
        {
            Check(actual > bound, label + "（实际 " + actual + "，要求 > " + bound + "）");
        }

        private static void Fail(string label)
        {
            Check(false, label);
        }

        private static void Section(string name)
        {
            Console.WriteLine("── " + name);
        }

        // =================================================================================
        //  虚拟时钟
        // =================================================================================

        /// <summary>
        /// Lobby 侧与 DS 侧共用同一个虚拟时钟：真实 socket + 受控时间，
        /// 因此超时/重发全部确定性可测（契约 §3「注入时钟可配置，不用测试 sleep」）。
        /// </summary>
        private sealed class VirtualClock : IPMDsClock
        {
            public long Ms = 1700000000000L;

            public long UtcNowUnixMilliseconds { get { return Ms; } }

            public long UnixSeconds { get { return Ms / 1000L; } }

            public void Advance(long milliseconds) { Ms += milliseconds; }
        }

        // =================================================================================
        //  受控进程替身（**唯一的边界外替身**）
        // =================================================================================

        /// <summary>
        /// DS 进程替身：只表示「有一个进程”，不做任何真实 I/O。
        ///
        /// 它的语义**刻意不动**：Kill 只记录请求（不自动退出），退出必须由测试**显式**标定，
        /// 这样「协调器确认进程退出前后资源占用」这条断言才有意义（与 A1 门禁同口径）。
        /// </summary>
        private sealed class ProcessDouble : IPMDsProcess
        {
            private volatile bool _running = true;
            private int _exitCode;

            public int ProcessId { get; set; }
            public int KillCount;
            public int WaitForExitCount;
            public bool Killed;
            public bool GracefulExitRequested;

            public event Action<int> Exited;

            public bool IsRunning { get { return _running; } }

            public bool TryGetExitCode(out int exitCode)
            {
                exitCode = _exitCode;
                return !_running;
            }

            public bool WaitForExit(int timeoutMilliseconds)
            {
                WaitForExitCount++;
                return !_running;
            }

            public long StdOutTotalBytes { get { return 0; } }
            public long StdErrTotalBytes { get { return 0; } }
            public string StdOutTail { get { return string.Empty; } }
            public string StdErrTail { get { return string.Empty; } }

            public void RequestGracefulExit()
            {
                GracefulExitRequested = true;
            }

            public void Kill()
            {
                KillCount++;
                Killed = true;
            }

            public void Dispose()
            {
            }

            /// <summary>测试显式标定进程退出（模拟 Unity 侧 Application.Quit 之后的真实退出）。</summary>
            public void MarkExited(int exitCode)
            {
                if (!_running)
                {
                    return;
                }

                _exitCode = exitCode;
                _running = false;
                Action<int> handler = Exited;
                if (handler != null)
                {
                    handler(exitCode);
                }
            }
        }

        /// <summary>进程启动器替身：记录真实启动请求，返回进程替身（**不**执行任何外部程序）。</summary>
        private sealed class ProcessDoubleLauncher : IPMDsProcessLauncher
        {
            public readonly List<PMDsProcessLaunchRequest> Requests = new List<PMDsProcessLaunchRequest>();
            public readonly List<ProcessDouble> Processes = new List<ProcessDouble>();

            public bool TryStart(PMDsProcessLaunchRequest request, out IPMDsProcess process,
                out PMDsProcessFault fault, out string detail)
            {
                Requests.Add(request == null ? null : request.Clone());

                ProcessDouble created = new ProcessDouble();
                created.ProcessId = 9000 + Processes.Count;
                Processes.Add(created);

                process = created;
                fault = PMDsProcessFault.None;
                detail = null;
                return true;
            }
        }

        // =================================================================================
        //  客户端网关（冻结缝）：宿主字段 → 真实 PMDsEntryCodec → 真实解码
        // =================================================================================

        /// <summary>
        /// Lobby 与「已认证大厅客户端」之间的缝（<see cref="IPMDsLobbyClientGateway"/>）。
        ///
        /// 它**不是**第二个 offer 替身：这里把宿主产出的字段原样填进**冻结的** <c>PMDsEntryOffer</c>，
        /// 再用**真实** <see cref="PMDsEntryCodec"/> 编码成 `MainPack.Str` 文本，
        /// 然后按客户端入口的同一严格路径 <c>TryDecode</c>（等于 UIMatchingPanel 的 `PMDS1:` 分流）。
        /// 于是「Lobby 发出去的字节」与「客户端读到的东西」之间没有任何自造 codec。
        /// </summary>
        private sealed class ClientGateway : IPMDsLobbyClientGateway
        {
            public readonly HashSet<int> Online = new HashSet<int>();
            public readonly Dictionary<int, string> EntryTexts = new Dictionary<int, string>();
            public readonly Dictionary<int, PMDsEntryOffer> Offers = new Dictionary<int, PMDsEntryOffer>();
            public readonly List<PMDsLobbyResultNotice> Results = new List<PMDsLobbyResultNotice>();
            public readonly List<int> Restored = new List<int>();
            public long OfferFailures;

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
                    return false;
                }

                if (text == null || text.IndexOf(PMDsEntryCodec.Prefix, StringComparison.Ordinal) != 0)
                {
                    OfferFailures++;
                    error = "编码结果缺少 PMDS1: 前缀";
                    return false;
                }

                // 客户端入口（UIMatchingPanel）：识别前缀后**严格解码**，失败即报错、不退旧链。
                PMDsEntryOffer parsed;
                string decodeError;
                if (!PMDsEntryCodec.TryDecode(text, out parsed, out decodeError))
                {
                    OfferFailures++;
                    error = "客户端严格解码失败：" + decodeError;
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
        //  DS 侧测试宿主（非 Unity）：真实次序的装配
        // =================================================================================

        /// <summary>
        /// DS 侧测试宿主。它按 <c>PMDsSessionHost.Initialize</c> 的**非 Unity 次序**装配真实组件：
        /// 读引导文件 → 校验局/摘要/端口 → Register → 世界/桥/Attach → OpenServer（真实 UDP）
        /// → 真实 <see cref="PMDsLobbyAgent"/> 连控制通道 → Connected 时 <c>SpawnPlayer</c>。
        ///
        /// 区别（必须在报告里讲清）：<c>PMDsSessionHost</c> 还会建 Unity 碰撞场景并据此置 SceneReady；
        /// 这里没有 UnityEngine，因此 SceneReady 直接为真（碰撞摘要仍是契约冻结值，两侧一致）。
        /// 其余参数对账（matchid/dsid/port/hash/digest）逐条与真实宿主同口径。
        /// </summary>
        private sealed class DsHarness : IDisposable
        {
            public PMDsBootstrappedMatch Boot;
            public PMSession Session;
            public PMNetWorld World;
            public PMNetSessionBridge Bridge;
            public PMUdpSessionEndpoint Endpoint;
            public PMDsLobbyAgent Lobby;
            public int BoundPort;
            public ProcessDouble Process;

            public readonly Dictionary<int, PMR3Player> PlayersByUid = new Dictionary<int, PMR3Player>();
            public readonly List<PMR3Player> SpawnedOrder = new List<PMR3Player>();
            public bool ExitRequestedObserved;
            public int ExitRequestedCode = -1;
            public bool SceneReady = true;

            private bool _disposed;

            public static DsHarness Start(string bootstrapPath, string expectedMatchId, string expectedDsId,
                int expectedPort, string controlHost, int controlPort, List<string> warnings)
            {
                if (!File.Exists(bootstrapPath))
                {
                    throw new InvalidOperationException("引导文件不存在：" + bootstrapPath);
                }

                byte[] document = File.ReadAllBytes(bootstrapPath);

                PMDsBootstrappedMatch boot;
                string decodeError;
                if (!PMDsBootstrapDocument.TryDecode(document, out boot, out decodeError))
                {
                    throw new InvalidOperationException("引导文件解码失败：" + decodeError);
                }

                if (!string.Equals(boot.MatchId, expectedMatchId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("引导文件 MatchId 与 -matchid 不一致：'"
                        + boot.MatchId + "' vs '" + expectedMatchId + "'");
                }

                if (!string.Equals(boot.DsId, expectedDsId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("引导文件 DsId 与 -dsid 不一致：'"
                        + boot.DsId + "' vs '" + expectedDsId + "'");
                }

                if (boot.ProtocolHash != PMR3Runtime.ProtocolHash)
                {
                    throw new InvalidOperationException("协议摘要不一致：引导文件 0x"
                        + boot.ProtocolHash.ToString("X8", CultureInfo.InvariantCulture) + " vs 本机 0x"
                        + PMR3Runtime.ProtocolHash.ToString("X8", CultureInfo.InvariantCulture));
                }

                if (boot.CollisionDigest != PMR3Runtime.CollisionDigest)
                {
                    throw new InvalidOperationException("碰撞摘要不一致：引导文件 0x"
                        + boot.CollisionDigest.ToString("X8", CultureInfo.InvariantCulture));
                }

                if (boot.Epoch == 0u)
                {
                    throw new InvalidOperationException("引导文件 Epoch 为 0");
                }

                DsHarness harness = new DsHarness();
                harness.Boot = boot;

                harness.Session = new PMSession(boot.Epoch, true);
                harness.World = new PMNetWorld(harness.Session);
                harness.World.Warn = delegate(string m) { warnings.Add("[ds.world] " + m); };
                harness.Bridge = new PMNetSessionBridge(harness.World);
                harness.Bridge.Warn = delegate(string m) { warnings.Add("[ds.bridge] " + m); };

                PMR3Runtime.Attach(harness.World, harness.Bridge);

                harness.Endpoint = PMUdpSessionEndpoint.OpenServer(boot, harness.Bridge, controlHost, expectedPort);
                harness.BoundPort = harness.Endpoint.BoundPort;
                if (harness.BoundPort != expectedPort)
                {
                    harness.Dispose();
                    throw new InvalidOperationException("实际绑定端口 " + harness.BoundPort
                        + " 与分配端口 " + expectedPort + " 不一致");
                }

                harness.Endpoint.Connected += harness.OnConnected;

                harness.Lobby = new PMDsLobbyAgent(boot, controlHost, controlPort, harness.BoundPort,
                    PMR3Runtime.CollisionDigest);
                harness.Lobby.SceneReady = true;
                harness.Lobby.Log = delegate(string m) { warnings.Add("[ds.lobby] " + m); };
                harness.Lobby.Warn = delegate(string m) { warnings.Add("[ds.lobby][warn] " + m); };
                harness.Lobby.ExitRequested = harness.OnExitRequested;
                harness.Lobby.PlayerCountProvider = delegate { return harness.PlayersByUid.Count; };

                string startError;
                if (!harness.Lobby.Start(out startError))
                {
                    harness.Dispose();
                    throw new InvalidOperationException("控制通道连接失败：" + startError);
                }

                return harness;
            }

            private void OnConnected(PMTransportConnection connection)
            {
                if (connection == null)
                {
                    return;
                }

                int uid = connection.Identity.Uid;
                PMR3Player player = PMR3Runtime.SpawnPlayer(World, Bridge, connection);
                if (player == null)
                {
                    throw new InvalidOperationException("SpawnPlayer 失败（uid=" + uid + "）");
                }

                PlayersByUid[uid] = player;
                SpawnedOrder.Add(player);
            }

            private void OnExitRequested(int code)
            {
                // 真实宿主这里是 Application.Quit(code)；测试宿主只记账，**由测试决定何时标定进程退出**
                // （真实退出是异步的，必须能在「Lobby 已处理显式 Exited 之后」才被观测到）。
                ExitRequestedObserved = true;
                ExitRequestedCode = code;
            }

            public PMTransportConnection FindServerConnection(int uid)
            {
                return Bridge.FindConnectionByUid(uid);
            }

            public void Pump(long nowMs, long nowUnixSeconds)
            {
                if (_disposed)
                {
                    return;
                }

                Endpoint.Pump(nowMs, nowUnixSeconds);
                Lobby.Pump(nowMs, nowUnixSeconds);
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                if (Lobby != null)
                {
                    try { Lobby.Dispose(); }
                    catch (Exception) { }
                    Lobby = null;
                }

                if (Endpoint != null)
                {
                    try
                    {
                        Endpoint.Connected -= OnConnected;
                        Endpoint.Dispose();
                    }
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

                World = null;
                Session = null;
            }
        }

        // =================================================================================
        //  客户端侧测试宿主（非 Unity）：真实次序的装配
        // =================================================================================

        /// <summary>
        /// 客户端测试宿主。按 <c>PMClientSessionHost.Enter/PumpActive</c> 的**非 Unity 核心**装配：
        /// 校验协议/碰撞摘要 → 世界/桥/Attach → OpenClient（真实 UDP）→
        /// 本端 owner 副本经**生成桩**发一次 ServerProbe → 观察 Echo 与属性收敛。
        /// 探针只在宿主帧步里发（不在复制回调里发），避免重入桥（与真实宿主同纪律）。
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
            public int ReplicatedCreateCount;

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
                harness.World.Warn = delegate(string m) { warnings.Add("[client.world uid=" + offer.Identity.Uid + "] " + m); };
                harness.Bridge = new PMNetSessionBridge(harness.World);
                harness.Bridge.Warn = delegate(string m) { warnings.Add("[client.bridge uid=" + offer.Identity.Uid + "] " + m); };

                PMR3Runtime.Attach(harness.World, harness.Bridge);

                harness.Endpoint = PMUdpSessionEndpoint.OpenClient(offer, harness.Bridge);
                harness.Endpoint.Failed += delegate(string reason) { harness.Failures.Add(reason); };
                return harness;
            }

            /// <summary>复制回调：只入队/记账，**不发包**（发送发生在宿主帧步里）。</summary>
            public void OnReplicated(PMR3Player player)
            {
                ReplicatedCreateCount++;

                if (player == null)
                {
                    return;
                }

                ReplicasByUid[player.Uid] = player;

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

                    // 经**生成的调用桩**发（PMNet_ServerProbe），不手写业务状态包（契约 §7.4）。
                    player.PMNet_ServerProbe(LastProbeNonce);
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
                World = null;
                Session = null;
            }
        }

        // =================================================================================
        //  集成夹具：一个 LobbyHost + 一局的 DS/客户端宿主
        // =================================================================================

        private sealed class Fixture : IDisposable
        {
            public readonly string Root;
            public readonly VirtualClock Clock = new VirtualClock();
            public readonly ProcessDoubleLauncher Launcher = new ProcessDoubleLauncher();
            public readonly ClientGateway Gateway = new ClientGateway();
            public readonly List<string> Warnings = new List<string>();
            public readonly List<string> ResultAcceptedMatches = new List<string>();
            public readonly List<string> ReleasedMatches = new List<string>();
            public readonly Dictionary<string, PMDsSessionState> ReleasedStates =
                new Dictionary<string, PMDsSessionState>(StringComparer.Ordinal);
            public readonly List<PMNetRpcReceiveResult> RpcObservations = new List<PMNetRpcReceiveResult>();

            public PMDsLobbyHost Host;
            public DsHarness Ds;
            public readonly List<ClientHarness> Clients = new List<ClientHarness>();
            public bool PumpHost = true;

            /// <summary>
            /// 是否泵 DS 侧（UDP 端点 + 控制代理）。
            ///
            /// 场景 3 需要把「进程死了」与「DS 代理还没来得及读到 ResultAck」这两件事拆开：
            /// 真实世界里进程可以在任意时刻消失，而本测试里代理是个活对象，
            /// 只靠 MarkExited 无法阻止它继续读 ResultAck 并发出 Exited(0)。
            /// 因此确认窗口内必须能**冻结** DS 侧。
            /// </summary>
            public bool PumpDs = true;

            private bool _disposed;

            public Fixture()
            {
                Root = Path.Combine(Path.GetTempPath(), "pmr3-integration-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);

                PMR3Runtime.PlayerReplicated += OnPlayerReplicated;
                PMNetRpcReceive.Observer = OnRpcObserved;

                PMDsLobbyHostOptions options = new PMDsLobbyHostOptions();
                options.Enabled = true;
                options.ListenAddress = "127.0.0.1";
                options.ControlPort = 0;                     // 系统分配的临时控制端口
                options.DsExecutablePath = Path.Combine(Root, "HyldDS.exe");
                options.DsWorkingDirectory = Root;
                options.BootstrapRootDirectory = Path.Combine(Root, "boot");

                // 端口段随机落在高位：避免与现役服务/其它测试抢占（绝不用 7777/7778/7800）。
                int first = 30000 + (new Random(Guid.NewGuid().GetHashCode()).Next(0, 200)) * 20;
                options.PortRangeFirst = first;
                options.PortRangeLast = first + 19;
                options.ProtocolHash = PMR3Runtime.ProtocolHash;   // 唯一来源：生成表封板摘要
                options.CollisionDigest = PMR3Runtime.CollisionDigest;
                options.RunBackgroundPumpThread = false;          // 测试自己驱动 PumpOnce（确定性）
                options.PumpIntervalMilliseconds = 1;

                Host = new PMDsLobbyHost(options, Clock, Launcher, Gateway);
                Host.Log = delegate(string m) { RecordWarning("[host] " + m); };
                Host.ResultAccepted += delegate(PMDsLobbyResultNotice n) { ResultAcceptedMatches.Add(n.MatchId); };
                Host.SessionReleased += delegate(string matchId, PMDsSessionState state, string reason)
                {
                    ReleasedMatches.Add(matchId);
                    ReleasedStates[matchId] = state;
                    RecordWarning("[host] 会话已释放 match=" + matchId + " state=" + state + " reason=" + reason);
                };

                string error;
                if (!Host.Start(out error))
                {
                    throw new InvalidOperationException("PMDsLobbyHost 启动失败：" + error);
                }
            }

            public int ControlPort { get { return Host.ControlPort; } }

            public long NowMs { get { return Clock.Ms; } }

            public long NowUnixSeconds { get { return Clock.UnixSeconds; } }

            private void OnPlayerReplicated(PMR3Player player)
            {
                if (player == null)
                {
                    return;
                }

                for (int i = 0; i < Clients.Count; i++)
                {
                    ClientHarness client = Clients[i];
                    if (ReferenceEquals(client.World, player.World))
                    {
                        client.OnReplicated(player);
                        return;
                    }
                }
            }

            private void OnRpcObserved(PMNetRpcReceiveResult result)
            {
                if (RpcObservations.Count < 256)
                {
                    RpcObservations.Add(result);
                }
            }

            private void RecordWarning(string message)
            {
                if (Warnings.Count < 4000)
                {
                    Warnings.Add(message);
                }
            }

            // ── 泵 ────────────────────────────────────────────────────────────

            /// <summary>单轮：客户端 → DS（UDP）→ 控制代理（TCP）→ Lobby 宿主。</summary>
            public void PumpOnce()
            {
                long now = Clock.Ms;
                long unix = Clock.UnixSeconds;

                for (int i = 0; i < Clients.Count; i++)
                {
                    Clients[i].Pump(now, unix);
                }

                if (Ds != null && PumpDs)
                {
                    Ds.Pump(now, unix);
                }

                if (PumpHost)
                {
                    Host.PumpOnce();
                }

                Clock.Advance(StepMs);
                Thread.Sleep(1);
            }

            public void Pump(int rounds)
            {
                for (int i = 0; i < rounds; i++)
                {
                    PumpOnce();
                }
            }

            /// <summary>有界等待条件成立；不成立时返回 false（调用方据此记一次失败，不做无界 sleep）。</summary>
            public bool PumpUntil(Func<bool> condition, string label, int maxRounds)
            {
                for (int i = 0; i < maxRounds; i++)
                {
                    PumpOnce();
                    if (condition())
                    {
                        return true;
                    }
                }

                Console.WriteLine("    （未在 " + maxRounds + " 轮内满足：" + label + "）");
                return condition();
            }

            // ── 场景 1：完整成功闭环 ───────────────────────────────────────────

            public void RunHappyPath()
            {
                Section("场景 1：Lobby→DS 控制 + 真实 UDP 两客户端 → 探针/回声/收敛 → smoke 结果 → ResultAck → 显式 Exited(0) → 回收");

                string matchId = "r3b5-happy-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                Gateway.Online.Add(UidA);
                Gateway.Online.Add(UidB);

                PMDsLobbyStartReply reply = Host.TryStartMatch(MakeMatchRequest(matchId, "happy"));
                CheckEq((int)reply.Outcome, (int)PMDsLobbyStartOutcome.Queued,
                    "G3 新链开局请求被接受并入队（" + reply.Detail + "）");

                bool started = PumpUntil(delegate { return Launcher.Requests.Count == 1; },
                    "受控进程替身收到启动请求", PhaseBudgetRounds);
                Check(started, "G4 受控进程替身收到一次启动请求（唯一边界外替身）");
                if (!started)
                {
                    return;
                }

                PMDsProcessLaunchRequest launch = Launcher.Requests[0];
                string bootstrapPath = ArgValue(launch.Arguments, "-bootstrap");
                string dsId = ArgValue(launch.Arguments, "-dsid");
                int dsPort = ParseInt(ArgValue(launch.Arguments, "-port"));
                string controlEndpoint = ArgValue(launch.Arguments, "-control");

                Check(!string.IsNullOrEmpty(bootstrapPath) && File.Exists(bootstrapPath),
                    "G5 LobbyHost 真正写出的引导文件存在（路径来自启动参数）");
                CheckEq(ArgValue(launch.Arguments, "-matchid"), matchId, "G6 启动参数 -matchid 与请求一致");
                CheckEq(ArgValue(launch.Arguments, "-control"), "127.0.0.1:" + Host.ControlPort,
                    "G7 启动参数 -control 指向真实监听端口");
                Check(HasFlag(launch.Arguments, "-server") && HasFlag(launch.Arguments, "-batchmode")
                    && HasFlag(launch.Arguments, "-nographics"), "G8 启动参数含 -server/-batchmode/-nographics");
                CheckGt(dsPort, 0, "G9 启动参数 -port 来自 Allocate 后的真实端口");
                Check(dsPort != 7777 && dsPort != 7778 && dsPort != 7800,
                    "G10 本局端口不是任何现役/旧服务端口（" + dsPort + "）");
                Check(Host.ControlPort != 0 && Host.ControlPort != 7778 && Host.ControlPort != 7800,
                    "G11 控制端口是系统临时端口（" + Host.ControlPort + "）");

                PMDsCoordinator coordinator = Host.GetCoordinator(matchId);
                Check(coordinator != null, "G12 可按 matchId 取到协调器（诊断面）");
                if (coordinator == null)
                {
                    return;
                }

                int scenarioStartRequests = Launcher.Requests.Count;
                Ds = DsHarness.Start(bootstrapPath, matchId, dsId, dsPort, "127.0.0.1", Host.ControlPort, Warnings);
                Ds.Process = Launcher.Processes[scenarioStartRequests - 1];
                CheckEq(Ds.BoundPort, dsPort, "G13 DS 侧 OpenServer 实际绑定端口与分配端口一致");
                Check(Ds.Boot.Key != null && Ds.Boot.PlayerCount == 2,
                    "G14 引导文件可解码且名册 2 人（密钥只经文件，不进命令行）");

                bool ready = PumpUntil(delegate { return Gateway.EntryTexts.Count == 2; },
                    "Lobby 在真实 Ready 之后发布两份 offer", PhaseBudgetRounds);
                Check(ready, "G15 真实控制链路完成 Ready 并发布 2 份 PMDS1 offer");
                CheckEq((int)coordinator.State, (int)PMDsSessionState.Running,
                    "G16 发布 offer 后协调器进入 Running");
                Check(Host.HasBoundControlConnection(matchId), "G17 控制连接已由 MAC 校验后的首帧绑定");
                CheckGt(Ds.Lobby.FramesSent, 0, "G18 DS 控制代理真的发出过控制帧（真实 TCP）");
                CheckGt(Host.InboundFramesDispatched, 0, "G19 Lobby listener 真的分帧并派发过控制帧");
                CheckEq(Ds.Lobby.MacFailures, 0L, "G20 全链路没有任何控制帧 MAC 失败");
                CheckEq(Gateway.OfferFailures, 0L, "G21 没有 offer 投递失败（codec 往返全部成功）");
                CheckEq(Ds.Lobby.FramesReceived, 0L,
                    "G22 Ready 阶段 Lobby 尚未下行任何控制帧（结果确认才下行，方向语义可复核）");

                PMDsEntryOffer offerA = Gateway.Offers[UidA];
                PMDsEntryOffer offerB = Gateway.Offers[UidB];
                CheckEq(offerA.MatchId, matchId, "G23 offer A 的 MatchId 来自会话");
                CheckEq(offerA.Port, dsPort, "G24 offer A 的 Port 等于真实分配端口");
                CheckEq(offerA.Epoch, Ds.Boot.Epoch, "G25 offer A 的 Epoch 与引导文件一致");
                CheckEq(offerA.ProtocolHash, PMR3Runtime.ProtocolHash, "G26 offer A 的协议摘要 = PMR3 生成表摘要");
                CheckEq(offerA.CollisionDigest, PMR3Runtime.CollisionDigest, "G27 offer A 的碰撞摘要 = 契约冻结值");
                CheckEq(offerA.Identity.Uid, UidA, "G28 offer A 的身份 uid 来自名册");
                Check(offerA.Ticket != null && offerA.Ticket.Length > 0, "G29 offer A 携带票据字节");
                Check(Gateway.EntryTexts[UidA].IndexOf(PMDsEntryCodec.Prefix, StringComparison.Ordinal) == 0,
                    "G30 编码文本以 PMDS1: 开头（MainPack.Str 的冻结载体）");

                // ── 客户端入局（真 UDP + 真握手 + 真验票）──────────────────────
                Clients.Add(ClientHarness.Enter(offerA, Warnings));
                Clients.Add(ClientHarness.Enter(offerB, Warnings));

                bool connected = PumpUntil(
                    delegate
                    {
                        return Ds.Endpoint.ConnectionCount == 2
                            && Clients[0].Endpoint.ClientConnection != null
                            && Clients[1].Endpoint.ClientConnection != null;
                    },
                    "两个客户端在真实 UDP 上完成入局", PhaseBudgetRounds);

                Check(connected, "G31 两个客户端在真实 UDP 上完成握手/验票/激活");
                CheckEq(Ds.Endpoint.ConnectionCount, 2, "G32 服务端 2 条已激活连接");
                CheckEq(Clients[0].Endpoint.ConnectionCount, 1, "G33 客户端 A 1 条已激活连接");
                CheckEq(Clients[1].Endpoint.ConnectionCount, 1, "G34 客户端 B 1 条已激活连接");
                CheckEq(Ds.Endpoint.HandshakeRejections, 0L, "G35 服务端没有拒绝任何握手（票据/摘要/世代全对）");
                CheckEq(Ds.Endpoint.DroppedUnboundDatagrams, 0L, "G36 没有任何未认证端点数据报（全部走握手）");
                CheckEq(Ds.Endpoint.Ledger.ActiveBindingCount, 2, "G37 端点消费账本活跃绑定 2 个");
                CheckEq(Clients[0].Endpoint.DroppedPreActivationDatagrams, 0L, "G38 客户端 A 没有激活前的数据报被丢");
                CheckEq(Clients[0].Endpoint.IdentityMismatches, 0L, "G39 客户端 A 没有摘要/nonce 不匹配的 ServerHello");
                CheckGt(Ds.Endpoint.DatagramsReceived, 0, "G40 服务端真的收到过 UDP 数据报");
                CheckGt(Ds.Endpoint.DatagramsSent, 0, "G41 服务端真的发出过 UDP 数据报");

                // ── 权威创建：两端各看到两个对象 ──────────────────────────────
                bool replicated = PumpUntil(
                    delegate
                    {
                        return Ds.PlayersByUid.Count == 2
                            && Clients[0].ReplicasByUid.Count == 2
                            && Clients[1].ReplicasByUid.Count == 2;
                    },
                    "两端各自看到两个玩家副本", PhaseBudgetRounds);

                Check(replicated, "G42 服务端 2 个权威副本，两个客户端各看到 2 个副本");
                CheckEq(Ds.PlayersByUid[UidA].Uid, UidA, "G43 服务端 A 副本 uid 来自已认证连接");
                CheckEq(Ds.PlayersByUid[UidB].Uid, UidB, "G44 服务端 B 副本 uid 来自已认证连接");
                CheckEq(Ds.PlayersByUid[UidA].Role, PMNetRole.Authority, "G45 服务端副本角色 = Authority");
                CheckEq(Ds.World.ObjectCount, 2, "G46 DS 世界对象数 = 2");
                CheckEq(Clients[0].World.ObjectCount, 2, "G47 客户端 A 世界对象数 = 2");
                CheckEq(Clients[1].World.ObjectCount, 2, "G48 客户端 B 世界对象数 = 2");

                PMR3Player clientAOwn = Clients[0].OwnReplica;
                PMR3Player clientAOther = FindReplica(Clients[0], UidB);
                Check(clientAOwn != null && clientAOwn.Role == PMNetRole.AutonomousProxy,
                    "G49 客户端 A 的本端副本角色 = AutonomousProxy（Create 初值已带 uid）");
                Check(clientAOther != null && clientAOther.Role == PMNetRole.SimulatedProxy,
                    "G50 客户端 A 看到的对手副本角色 = SimulatedProxy");
                Check(clientAOwn != null && clientAOwn.Uid == UidA, "G51 客户端 A 本端副本 uid 初值正确");

                // ── 探针/回声/收敛 ────────────────────────────────────────────
                bool converged = PumpUntil(
                    delegate
                    {
                        PMR3Player serverA;
                        PMR3Player serverB;
                        return Ds.PlayersByUid.TryGetValue(UidA, out serverA) && serverA.ProbeCount == 1
                            && Ds.PlayersByUid.TryGetValue(UidB, out serverB) && serverB.ProbeCount == 1
                            && Clients[0].OwnReplica != null && Clients[0].OwnReplica.EchoCount == 1
                            && Clients[0].OwnReplica.ProbeCount == 1
                            && Clients[1].OwnReplica != null && Clients[1].OwnReplica.EchoCount == 1
                            && Clients[1].OwnReplica.ProbeCount == 1;
                    },
                    "服务端 ProbeCount 与客户端 Echo/属性收敛", PhaseBudgetRounds);

                Check(converged, "G52 每个 owner 一次探针 → 服务端 ProbeCount=1 → Echo 回流 → 属性收敛");
                CheckEq(Clients[0].ProbeNonceSent, 1, "G53 客户端 A 只发过一次探针");
                CheckEq(Clients[1].ProbeNonceSent, 1, "G54 客户端 B 只发过一次探针");
                CheckEq(Clients[0].OwnReplica.ProbeSentCount, 1, "G55 客户端 A 本端副本的本地探针计数 = 1");
                CheckEq(Clients[1].OwnReplica.ProbeSentCount, 1, "G56 客户端 B 本端副本的本地探针计数 = 1");
                CheckEq(Clients[0].OwnReplica.EchoCount, 1, "G57 客户端 A 收到恰好一次 ClientEcho");
                CheckEq(Clients[0].OwnReplica.LastEchoNonce, Clients[0].LastProbeNonce,
                    "G58 客户端 A 的 Echo nonce 原样回流");
                CheckEq(Clients[1].OwnReplica.LastEchoNonce, Clients[1].LastProbeNonce,
                    "G59 客户端 B 的 Echo nonce 原样回流");
                CheckEq(FindReplica(Clients[1], UidA).EchoCount, 0,
                    "G60 客户端 B 上的 A 副本不收 ClientEcho（RPC 只回 owner）");
                CheckEq(Ds.PlayersByUid[UidA].ProbeCount, 1, "G61 服务端 A 副本 ProbeCount = 1");
                CheckEq(Ds.PlayersByUid[UidB].ProbeCount, 1, "G62 服务端 B 副本 ProbeCount = 1");
                CheckEq(FindReplica(Clients[0], UidB).ProbeCount, 1, "G63 客户端 A 也看到 B 的 ProbeCount 收敛到 1");
                CheckEq(FindReplica(Clients[1], UidA).ProbeCount, 1, "G64 客户端 B 也看到 A 的 ProbeCount 收敛到 1");
                CheckGt(Ds.Bridge.Replication.Stats.UpdateRecordsSent, 0, "G65 DS 发出过复制更新记录（真实复制通道）");
                CheckGt(Clients[0].Bridge.Replication.Stats.UpdateRecordsApplied, 0,
                    "G66 客户端 A 应用过复制更新记录");
                CheckGt(Clients[0].Bridge.RpcSent, 0, "G67 客户端 A 的桥发出过上行 RPC");
                CheckGt(Clients[1].Bridge.RpcSent, 0, "G68 客户端 B 的桥发出过上行 RPC");

                // 多世界 static route：生成物的 RemoteSender 是单静态接缝，必须按 target.World 反查桥。
                Check(ReferenceEquals(PMR3Runtime.GetBridge(Clients[0].World), Clients[0].Bridge),
                    "G69 世界→桥按世界反查（客户端 A 未被覆盖）");
                Check(ReferenceEquals(PMR3Runtime.GetBridge(Clients[1].World), Clients[1].Bridge),
                    "G70 世界→桥按世界反查（客户端 B 未被覆盖）");
                Check(!ReferenceEquals(Clients[0].Bridge, Clients[1].Bridge), "G71 两个客户端世界各有一条独立桥");
                Check(ReferenceEquals(PMR3Runtime.GetBridge(Ds.World), Ds.Bridge), "G72 DS 世界的桥同样按世界反查");
                CheckGt(Ds.World.ObjectCount, 0, "G73 多世界并跑时权威世界仍然有效");

                // ── 非 owner 拒绝（真实 UDP 上的归属校验）──────────────────────
                long rejectsBefore = 0L;
                PMTransportConnection connA = Ds.FindServerConnection(UidA);
                if (connA != null)
                {
                    rejectsBefore = connA.RpcRejected;
                }

                int serverBBefore = Ds.PlayersByUid[UidB].ProbeCount;
                PMR3Player forgedTarget = FindReplica(Clients[0], UidB);
                Check(forgedTarget != null, "G74 客户端 A 持有 B 的副本（用于冒名探针）");
                if (forgedTarget != null)
                {
                    forgedTarget.PMNet_ServerProbe(0x5150);   // 经生成桩发出，真实 UDP
                    Pump(12);
                }

                CheckEq(Ds.PlayersByUid[UidB].ProbeCount, serverBBefore,
                    "G75 非 owner 的探针**没有**执行实现（B 的 ProbeCount 不变）");
                if (connA != null)
                {
                    CheckGt(connA.RpcRejected, rejectsBefore, "G76 服务端在 A 的连接上拒绝了该 RPC（RpcRejected 增长）");
                }

                bool sawNotOwner = false;
                for (int i = 0; i < RpcObservations.Count; i++)
                {
                    if (RpcObservations[i].Status == PMRpcReceiveStatus.NotOwner)
                    {
                        sawNotOwner = true;
                        break;
                    }
                }

                Check(sawNotOwner, "G77 接收入口给出的拒绝状态可归因为 NotOwner");
                CheckEq(Clients[0].OwnReplica.EchoCount, 1, "G78 冒名探针没有让 A 收到额外 Echo（对照）");
                CheckEq(Ds.PlayersByUid[UidA].ProbeCount, 1, "G79 A 自己的探针仍然生效（证明链路没坏）");

                // ── smoke 结果：测试主动触发，不用定时器伪胜 ────────────────────
                byte[] summary = BuildSmokeSummary(Ds);
                Check(ContainsAscii(summary, "smoke"), "G80 smoke 摘要自带 'smoke' 字面量（不是正常胜负伪装）");

                string submitError;
                bool submitted = Ds.Lobby.SubmitResult(0, summary, out submitError);
                Check(submitted, "G81 测试主动触发 SubmitResult（" + (submitError ?? "ok") + "）");

                // 结果重发（同一 ResultId）：先只泵 DS（不让 Lobby 处理），制造真实的重传窗口。
                Ds.Lobby.Pump(Clock.Ms, Clock.UnixSeconds);
                Clock.Advance(1100L);
                Ds.Lobby.Pump(Clock.Ms, Clock.UnixSeconds);
                CheckGt(Ds.Lobby.ResultSends, 1, "G82 同一 ResultId 被真实重发（制造幂等样本）");

                bool accepted = PumpUntil(
                    delegate
                    {
                        return coordinator.Counters.ResultAccepted == 1L
                            && coordinator.Counters.ResultDuplicates >= 1L;
                    },
                    "Lobby 受理结果并识别重复", PhaseBudgetRounds);
                Check(accepted, "G83 Lobby 受理结果并识别出一次重复（幂等样本已入库）");
                CheckEq((int)coordinator.Counters.ResultAccepted, 1,
                    "G84 重发结果**不重复结算**（ResultAccepted 仍为 1）");
                CheckGt(coordinator.Counters.ResultDuplicates, 0,
                    "G85 第二次同内容结果被计为 Duplicate（幂等由内容比对保证）");
                CheckEq(ResultAcceptedMatches.Count, 1, "G86 宿主只对外通知一次结果");
                CheckEq((int)Gateway.Results.Count, 1, "G87 网关只收到一次结果通知（不伪造 BattleReview）");
                Check(ContainsAscii(Gateway.Results[0].Summary, "smoke"),
                    "G88 结果摘要经控制协议原样到达 Lobby（smoke 标记保留）");
                CheckEq(Gateway.Results[0].MatchId, matchId, "G89 结果通知携带正确 matchId");

                // ── ResultAck → DS 显式 Exited(0) → 模拟进程退出 → 回收 ─────────
                bool acked = PumpUntil(delegate { return Ds.Lobby.ResultAcknowledged; },
                    "DS 收到匹配的 ResultAck", PhaseBudgetRounds);
                Check(acked, "G90 DS 收到匹配的 ResultAck（只有它才允许请求正常退出）");
                Check(Ds.ExitRequestedObserved, "G91 DS 请求退出（真实宿主此处是 Application.Quit）");
                CheckEq(Ds.ExitRequestedCode, 0, "G92 正常完成请求的退出码 = 0");
                CheckEq(Ds.Lobby.MacFailures, 0L, "G93 结果确认往返没有 MAC 失败");
                CheckGt(Ds.Lobby.FramesReceived, 0, "G93b DS 控制代理真的收到过控制帧（真实 TCP + 真实 MAC + 真实验签）");

                bool exitedSeen = PumpUntil(
                    delegate { return (int)coordinator.State == (int)PMDsSessionState.Exited; },
                    "Lobby 依据认证显式 Exited(0) 判正常完成", PhaseBudgetRounds);
                Check(exitedSeen, "G94 终态 = Exited（修复前这里会误判 Failed）");
                Check(coordinator.PeerExitObserved, "G95 对端收尾已被观测");
                Check(!ReleasedMatches.Contains(matchId),
                    "G96 进程未退出前不得宣布释放（端口/uid 仍占用）");
                CheckEq(Ds.Endpoint.ConnectionCount, 2, "G97 未退出前 UDP 入局连接仍在");
                Check(Ds.Process.IsRunning, "G98 进程替身仍活着（没有伪造退出）");

                // 真实退出是异步的：显式 Exited(0) 已被 Lobby 处理之后，才标定进程退出。
                Ds.Process.MarkExited(0);

                bool released = PumpUntil(delegate { return ReleasedMatches.Contains(matchId); },
                    "确认进程退出后回收端口与 uid", PhaseBudgetRounds);
                Check(released, "G99 确认进程实际退出后完成收尾（资源释放）");
                CheckEq((int)ReleasedStates[matchId], (int)PMDsSessionState.Exited,
                    "G100 释放事件携带的终态 = Exited");
                CheckEq(Host.PortPool.InUseCount, 0, "G101 共享端口池已归还（InUseCount=0）");
                CheckEq(Host.PlayerLedger.Count, 0, "G102 跨会话 uid 占用账本已清空");
                Check(Host.PlayerLedger.IsOccupied(UidA) == false && Host.PlayerLedger.IsOccupied(UidB) == false,
                    "G103 两名玩家的 uid 已释放");
                Check(!coordinator.ResourcesHeld, "G104 协调器资源占用已释放");
                Check(!File.Exists(bootstrapPath), "G105 引导文件随收尾删除（只传路径的交付面无残留）");
                Check(Gateway.Restored.Contains(UidA) && Gateway.Restored.Contains(UidB),
                    "G106 确认退出后才恢复两名玩家的 PlayerOnline");
                CheckEq(Ds.Lobby.ExitCode, 0, "G107 控制代理记录的退出码 = 0");

                // ── 收尾本场景 ───────────────────────────────────────────────
                for (int i = 0; i < Clients.Count; i++)
                {
                    Clients[i].Dispose();
                }

                Clients.Clear();
                Ds.Dispose();
                Ds = null;

                CheckEq(Host.SessionCount, 0, "G108 释放后宿主会话表不再保留该局");
                Check(!Host.TryGetSessionState(matchId, out PMDsSessionState ignored),
                    "G109 已释放的局不可再查到状态（幂等墓碑仍在协调器内，不影响新局）");

                RecordWarning("== 场景 1 结束：控制帧派发计数=" + Host.InboundFramesDispatched + " ==");
            }

            // ── 场景 2：结果前纯崩溃 ───────────────────────────────────────────

            public void RunCrashBeforeResult()
            {
                Section("场景 2：结果前纯崩溃（无任何协议证据）→ Failed + 端口/uid 回收 + 同一 uid 可再次入局");

                string matchId = "r3b5-crash-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                Gateway.EntryTexts.Clear();
                Gateway.Offers.Clear();

                // 复用场景 1 的同一批 uid：若上一局正确释放，这里必须能再次入局。
                PMDsLobbyStartReply reply = Host.TryStartMatch(MakeMatchRequest(matchId, "crash"));
                CheckEq((int)reply.Outcome, (int)PMDsLobbyStartOutcome.Queued,
                    "G110 同一批 uid 可再次入局（证明上一局已释放 uid 占用）");

                int before = Launcher.Requests.Count;
                bool started = PumpUntil(delegate { return Launcher.Requests.Count == before + 1; },
                    "第二次启动请求", PhaseBudgetRounds);
                Check(started, "G111 第二局的受控进程替身收到启动请求");
                if (!started)
                {
                    return;
                }

                PMDsProcessLaunchRequest launch = Launcher.Requests[before];
                string bootstrapPath = ArgValue(launch.Arguments, "-bootstrap");
                string dsId = ArgValue(launch.Arguments, "-dsid");
                int dsPort = ParseInt(ArgValue(launch.Arguments, "-port"));

                PMDsCoordinator coordinator = Host.GetCoordinator(matchId);
                Check(coordinator != null, "G112 第二局协调器可取到");
                if (coordinator == null)
                {
                    return;
                }

                Ds = DsHarness.Start(bootstrapPath, matchId, dsId, dsPort, "127.0.0.1", Host.ControlPort, Warnings);
                Ds.Process = Launcher.Processes[before];

                bool ready = PumpUntil(delegate { return Gateway.EntryTexts.Count == 2; },
                    "第二局 Ready 并发布 offer", PhaseBudgetRounds);
                Check(ready, "G113 第二局真实控制链路就绪（数据面已建好）");
                CheckEq((int)coordinator.State, (int)PMDsSessionState.Running, "G114 第二局进入 Running");
                CheckEq(coordinator.Counters.ResultAccepted, 0L, "G115 崩溃前没有受理任何结果");

                // 纯崩溃：直接标定进程退出，**没有任何** Result/Exited 协议消息。
                Ds.Process.MarkExited(3);

                bool terminal = PumpUntil(delegate { return coordinator.IsTerminal; },
                    "协调器观测到进程提前退出", PhaseBudgetRounds);
                Check(terminal, "G116 协调器观测到进程提前退出");
                CheckEq((int)coordinator.State, (int)PMDsSessionState.Failed,
                    "G117 结果前纯崩溃 → Failed（不得当成正常完成）");
                Check(coordinator.LastReason != null
                    && coordinator.LastReason.IndexOf("退出", StringComparison.Ordinal) >= 0,
                    "G118 失败原因可归因（" + coordinator.LastReason + "）");

                bool released = PumpUntil(delegate { return ReleasedMatches.Contains(matchId); },
                    "崩溃局回收资源", PhaseBudgetRounds);
                Check(released, "G119 崩溃局完成回收");
                CheckEq((int)ReleasedStates[matchId], (int)PMDsSessionState.Failed, "G120 释放事件携带 Failed 终态");
                CheckEq(Host.PortPool.InUseCount, 0, "G121 崩溃局的端口已归还");
                CheckEq(Host.PlayerLedger.Count, 0, "G122 崩溃局的 uid 占用已释放");
                CheckEq(coordinator.Counters.ResultAccepted, 0L, "G123 崩溃局始终没有受理结果");
                CheckEq(Gateway.Results.Count, 1, "G124 没有为崩溃局伪造任何结果通知");

                Ds.Dispose();
                Ds = null;

                // 再次复用同一批 uid：证明收尾是真释放而不是「账本里换个说法」。
                // 这里刻意**只读**账本而不排一个新局 —— 排局会在下一轮被真正分配并占住 uid，
                // 反而把场景 3 的开局挤掉（真实的复用证据由场景 3 的开局本身给出）。
                Check(!Host.PlayerLedger.IsOccupied(UidA) && !Host.PlayerLedger.IsOccupied(UidB),
                    "G125 崩溃收尾后同一批 uid 不再被占用（可再次入局，由场景 3 真实开局复核）");
            }

            // ── 场景 3：结果已受理后落在确认窗口内的纯崩溃 ───────────────────

            /// <summary>
            /// 「结果已受理」+「进程没了」+「**没有**任何显式 Exited」⇒ 仍必须 Failed。
            ///
            /// 这条路径与场景 1 的区别正是本次冻结裁决要钉住的那一对：
            ///   场景 1：ResultPending + 认证显式 Exited(0) ⇒ 正常完成（Exited）；
            ///   场景 3：ResultPending + 单纯进程消失（无协议证据）⇒ 仍 Failed。
            /// 实现上必须冻结 DS 侧，否则真实代理会在下一帧读到 ResultAck 并发 Exited(0)，
            /// 使崩溃点落在确认窗口之外（那样测的就不是本路径了）。
            /// </summary>
            public void RunResultAcceptedThenCrash()
            {
                Section("场景 3：结果已受理但落在确认窗口内的纯崩溃（无 Exited）→ 仍 Failed + 回收");

                string matchId = "r3b5-win-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                Gateway.EntryTexts.Clear();
                Gateway.Offers.Clear();

                PMDsLobbyStartReply reply = Host.TryStartMatch(MakeMatchRequest(matchId, "confirm-window"));
                CheckEq((int)reply.Outcome, (int)PMDsLobbyStartOutcome.Queued,
                    "G126 第三局开局请求入队（同一批 uid 此时必须已可复用）");

                int before = Launcher.Requests.Count;
                bool started = PumpUntil(delegate { return Launcher.Requests.Count == before + 1; },
                    "第三次启动请求", PhaseBudgetRounds);
                Check(started, "G127 第三局的受控进程替身收到启动请求");
                if (!started)
                {
                    return;
                }

                PMDsProcessLaunchRequest launch = Launcher.Requests[before];
                PMDsCoordinator coordinator = Host.GetCoordinator(matchId);
                Check(coordinator != null, "G128 第三局协调器可取到");
                if (coordinator == null)
                {
                    return;
                }

                Ds = DsHarness.Start(ArgValue(launch.Arguments, "-bootstrap"), matchId,
                    ArgValue(launch.Arguments, "-dsid"), ParseInt(ArgValue(launch.Arguments, "-port")),
                    "127.0.0.1", Host.ControlPort, Warnings);
                Ds.Process = Launcher.Processes[before];

                bool ready = PumpUntil(delegate { return Gateway.EntryTexts.Count == 2; },
                    "第三局 Ready", PhaseBudgetRounds);
                Check(ready, "G129 第三局真实控制链路就绪（Running）");

                string submitError;
                Check(Ds.Lobby.SubmitResult(0, BuildSmokeSummary(Ds), out submitError),
                    "G130 主动提交 smoke 结果（" + (submitError ?? "ok") + "）");

                bool accepted = PumpUntil(delegate { return coordinator.Counters.ResultAccepted == 1L; },
                    "Lobby 受理结果", PhaseBudgetRounds);
                Check(accepted, "G131 Lobby 已受理结果（结果已被结算记账）");
                CheckEq((int)coordinator.State, (int)PMDsSessionState.ResultPending,
                    "G132 受理后处于 ResultPending（结果确认窗口）");
                Check(!Ds.Lobby.ResultAcknowledged,
                    "G133 崩溃点落在确认窗口内：DS 尚未处理 ResultAck（也没有发出任何 Exited）");
                CheckGt(Ds.Lobby.ResultSends, 0, "G134 DS 确实已经发出过 Result（前置）");

                // 冻结 DS 侧（等价于进程在读到 ResultAck 之前就没了），然后标定进程退出。
                PumpDs = false;
                Ds.Process.MarkExited(0);

                bool terminal = PumpUntil(delegate { return coordinator.IsTerminal; },
                    "协调器观测到进程消失", PhaseBudgetRounds);
                Check(terminal, "G135 协调器观测到进程退出");
                CheckEq((int)coordinator.State, (int)PMDsSessionState.Failed,
                    "G136 结果已受理但对端未声明完成就消失 ⇒ 仍判 Failed（不得从进程消失推断确认）");
                Check(coordinator.LastReason != null
                    && coordinator.LastReason.IndexOf("结果确认前进程退出", StringComparison.Ordinal) >= 0,
                    "G137 原因明确指向「结果确认前退出」：" + coordinator.LastReason);
                CheckEq(coordinator.Counters.ResultAccepted, 1L,
                    "G138 该局结果确实被受理过（与终态 Failed 并不矛盾）");

                bool released = PumpUntil(delegate { return ReleasedMatches.Contains(matchId); },
                    "确认退出后回收", PhaseBudgetRounds);
                Check(released, "G139 确认进程已退出后完成回收");
                CheckEq(coordinator.Counters.ResultAccepted, 1L,
                    "G140 回收之后受理计数仍为 1（结果不会被二次结算）");
                CheckEq(Host.PortPool.InUseCount, 0, "G141 第三局端口已归还");
                CheckEq(Host.PlayerLedger.Count, 0, "G142 第三局 uid 占用已释放");

                Ds.Dispose();
                Ds = null;
                PumpDs = true;
            }

            // ── 工具 ──────────────────────────────────────────────────────────

            public PMDsLobbyMatchRequest MakeMatchRequest(string matchId, string pattern)
            {
                PMDsLobbyMatchRequest request = new PMDsLobbyMatchRequest();
                request.MatchId = matchId;
                request.FightPattern = pattern;
                request.CollisionDigest = PMR3Runtime.CollisionDigest;
                request.RequestId = 1L;
                request.Roster = new PMDsRosterIdentity[]
                {
                    new PMDsRosterIdentity(UidA, 1, 0, 3),
                    new PMDsRosterIdentity(UidB, 2, 1, 5),
                };
                return request;
            }

            private static PMR3Player FindReplica(ClientHarness client, int uid)
            {
                PMR3Player player;
                return client.ReplicasByUid.TryGetValue(uid, out player) ? player : null;
            }

            private static string ArgValue(string[] args, string key)
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

            private static bool HasFlag(string[] args, string key)
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

            private static int ParseInt(string text)
            {
                int value;
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0;
            }

            private static bool ContainsAscii(byte[] data, string text)
            {
                if (data == null || text == null || data.Length < text.Length)
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

            /// <summary>
            /// smoke 结果摘要（与 <c>PMDsSessionHost.BuildSmokeSummary</c> 同格式）：
            /// 自描述 "smoke" + 真实 uid/ProbeCount。刻意由**测试主动触发**，不用定时器伪造正常胜利。
            /// </summary>
            private static byte[] BuildSmokeSummary(DsHarness ds)
            {
                List<int> uids = new List<int>(ds.PlayersByUid.Keys);
                uids.Sort();

                PMNetWriter writer = new PMNetWriter(128);
                writer.WriteInt32(1);
                writer.WriteStringValue("smoke");
                writer.WriteInt32(uids.Count);
                for (int i = 0; i < uids.Count; i++)
                {
                    PMR3Player player = ds.PlayersByUid[uids[i]];
                    writer.WriteInt32(uids[i]);
                    writer.WriteInt32(player != null ? player.ProbeCount : 0);
                }

                return writer.ToArray();
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                for (int i = 0; i < Clients.Count; i++)
                {
                    try { Clients[i].Dispose(); }
                    catch (Exception) { }
                }

                Clients.Clear();

                if (Ds != null)
                {
                    try { Ds.Dispose(); }
                    catch (Exception) { }
                    Ds = null;
                }

                PMR3Runtime.PlayerReplicated -= OnPlayerReplicated;
                PMNetRpcReceive.Observer = null;

                if (Host != null)
                {
                    try { Host.Stop(); }
                    catch (Exception) { }
                    try { Host.Dispose(); }
                    catch (Exception) { }
                    Host = null;
                }

                if (_failures.Count > 0 && Warnings.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("  ── 现场日志（最多 60 条，仅失败时输出）──");
                    int start = Warnings.Count > 60 ? Warnings.Count - 60 : 0;
                    for (int i = start; i < Warnings.Count; i++)
                    {
                        Console.WriteLine("    " + Warnings[i]);
                    }
                }

                try
                {
                    if (Directory.Exists(Root))
                    {
                        Directory.Delete(Root, true);
                    }
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
