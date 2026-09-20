using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Logging;
using PMNet;
using PMNet.Server;
using UnityEngine;

namespace PMNet.Unity
{
    /// <summary>
    /// 局内专用服务器（HyldDS）的进程宿主。
    ///
    /// <para>
    /// **P2' 阶段**（已完成）：证明 DS 进程能无头起来、能绑定端口、能收包、能回包——即「能连」；
    /// 建立按 TickRate 驱动的逻辑帧骨架；周期性心跳日志。那时回包是固定的 <c>PMDS-PONG</c> 占位。
    /// </para>
    ///
    /// <para>
    /// **P3'-1 阶段**（当前）：占位回包已移除，改由 <see cref="PMUdpRouter"/> 承担真实收发——
    /// 收到的是真实 protobuf <c>MainPack</c>，按 <c>ActionCode</c> 与来源端点路由到已注册的战斗 handler。
    /// 战斗仿真的实际迁移属 P3'-3；本类现在提供一个**诊断 handler** 来打通并验证整条路由链路。
    /// </para>
    ///
    /// 线程纪律（这是本类最重要的约束）：
    ///   1) 接收回调运行在线程池线程上，**绝不允许在其中调用 UnityEngine API**；
    ///   2) 接收回调也不允许执行战斗 handler —— 只允许「解析 + 入队」，由 <see cref="Update"/> 在主线程消费；
    ///   3) 全部日志在主线程输出（后台只往锁保护的队列里塞字符串）。
    ///
    /// 第 2 条是 P3'-1 相对旧服务端 <c>LZJUDP</c> 的**结构性修正**：旧实现直接在接收线程上执行战斗 handler 并打日志，
    /// 那在 Unity 里必然崩溃。详见 PMUdpRouter 的类注释。
    /// </summary>
    public sealed class PMDsHost : MonoBehaviour
    {
        /// <summary>
        /// P2' 的连通性占位回包内容。P3'-1 起 DS 不再应答它（改由 PMUdpRouter 承载真实协议），
        /// 保留该常量仅为了让陈旧引用在编译期就报错，而不是静默地继续发一个没人认的字符串。
        /// </summary>
        [Obsolete("P3'-1 起由 PMUdpRouter 承担真实收发，DS 不再应答 PMDS-PONG", true)]
        private const string ScaffoldPongPayload = "PMDS-PONG";

        /// <summary>心跳日志间隔（秒）。</summary>
        private const float HeartbeatIntervalSeconds = 5f;

        /// <summary>单次 Update 最多补偿的逻辑帧数，避免卡顿后追帧螺旋。</summary>
        private const int MaxTicksPerUpdate = 8;

        /// <summary>
        /// 单次 Update 最多分发的入站包数。设上限是为了把「一帧内集中到达的包」摊到多帧，
        /// 避免洪泛时单帧卡死；正常帧流量远低于此值。
        /// </summary>
        private const int MaxPacketsPerUpdate = 256;

        /// <summary>最多记录的远端端点数量，避免被扫描流量撑爆内存。</summary>
        private const int MaxTrackedRemotes = 32;

        private PMNetLaunchOptions _options;
        private Socket _socket;
        private EndPoint _receiveEndPoint;
        private byte[] _receiveBuffer;

        /// <summary>DS 单局形态下的战斗注册表（P4' 接入大厅后由大厅填充玩家名册）。</summary>
        private PMSingleBattleRegistry _registry;

        /// <summary>真实 UDP 路由层（P3'-1 自 Server/ClientUdp.cs 迁移）。</summary>
        private PMUdpRouter _router;

        /// <summary>
        /// R3-B 新链会话宿主。**非 null 时本类不再使用 socket/_router 那条旧诊断路径**
        /// （两者在同端口上按字节无法共存：旧路由吃裸 MainPack，新链吃带长度/序号头的分片帧）。
        /// </summary>
        private PMDsSessionHost _sessionHost;

        private float _tickAccumulator;
        private float _tickInterval;
        private float _nextHeartbeatTime;

        // ---- 以下字段跨线程访问，必须按各自的纪律使用 ----
        private readonly object _logLock = new object();
        private readonly List<string> _pendingLogs = new List<string>();

        private readonly object _remoteLock = new object();
        private readonly List<string> _knownRemotes = new List<string>();

        private int _receivedDatagrams;
        private int _receiveErrors;

        /// <summary>收到的数据报批次数（仅用于把逐包日志压成周期行）。</summary>
        private int _receiveBatches;

        // 注意：发送计数不再由本类维护。发送由 PMUdpRouter 同步完成，
        // 它自己的计数（DescribeCounters 的 sent/sendErr）才是唯一事实源。

        // ---- 以下字段只在主线程访问（handler 只在主线程执行）----

        /// <summary>已分发给战斗 handler 的包数（P3'-1 诊断用途）。</summary>
        private long _battlePacketsDispatched;

        /// <summary>诊断 handler 见到的最近一个 ActionCode（仅用于日志展示）。</summary>
        private object _lastBattleActionCode;

        /// <summary>最近一次收到战斗包时的帧号。</summary>
        private int _lastBattleActionSeenTicks;

        /// <summary>ActionCode 非空计数（与包数分开，便于发现「收到了空包」）。</summary>
        private int _battleActionAllowance;

        /// <summary>为 null 的战斗包数。</summary>
        private int _battleNullPackets;

        private long _tickCount;

        /// <summary>实际绑定的端口（传入 0 时由系统分配，此值是最终结果）。</summary>
        public int BoundPort { get; private set; }

        /// <summary>
        /// 当前 DS 宿主。供启动探针做幂等判定（Unity 可能重复触发初始化）。
        /// 未启动时为 null；销毁时置回 null。
        /// </summary>
        public static PMDsHost Instance { get; private set; }

        /// <summary>
        /// 启动 DS 宿主：创建常驻对象并开始监听。
        /// 只能由 <see cref="PMNetBootstrap"/> 在已确认 DS 身份、且**首张场景已加载完成**后调用
        /// （先加载完成才能用 DontDestroyOnLoad，详见 PMNetBootstrap.OnAfterSceneLoad 的注释）。
        /// </summary>
        public static PMDsHost Start(PMNetLaunchOptions options)
        {
            GameObject go = new GameObject("[PMDsHost]");
            UnityEngine.Object.DontDestroyOnLoad(go);
            PMDsHost host = go.AddComponent<PMDsHost>();
            Instance = host;
            host.Initialize(options);
            return host;
        }

        private void Initialize(PMNetLaunchOptions options)
        {
            _options = options;
            _tickInterval = 1f / Mathf.Max(1, options.TickRate);

            // 无头 DS 没有前台窗口，若被当成窗口化进程拉起，需要显式要求后台运行。
            Application.runInBackground = true;

            // R3-B：`-bootstrap` 非空 ⇒ 走新链（控制通道 + PMUdpSessionEndpoint），
            // 并且**绝不**启动旧 UDP 诊断路由。契约 §7.4：「带 bootstrap 不能旧 router 启动」。
            if (!string.IsNullOrEmpty(options.BootstrapPath))
            {
                StartNewChainSessionHost(options);
                return;
            }

            bool bound = TryBindSocket(options);
            _nextHeartbeatTime = Time.unscaledTime + HeartbeatIntervalSeconds;

            if (bound)
            {
                InitializeRouter();
            }

            LogReady(bound, options);
        }

        /// <summary>
        /// 启动 R3-B 新链会话宿主（`PMDsSessionHost`）。
        ///
        /// 失败语义：**明确失败并退出进程**，不静默退回旧诊断路径 ——
        /// 后者等于对同一个 -port 生成第二套权威与第二套端口占用。
        /// </summary>
        private void StartNewChainSessionHost(PMNetLaunchOptions options)
        {
            HYLDDebug.Log("[PMDsHost] 检测到 -bootstrap，转入 R3-B 新链宿主（旧 UDP 诊断路由不启动）");

            string error;
            _sessionHost = PMDsSessionHost.Start(options, out error);
            if (_sessionHost == null)
            {
                HYLDDebug.LogError("[PMDsHost] R3-B 新链宿主启动失败：" + (error ?? "<unknown>")
                                   + "（不回退旧诊断路径）");
                HYLDDebug.FlushTrace();
                Application.Quit(1);
                return;
            }

            // 退出请求（含失败退出）统一走这里：新链宿主只请求，不直接结束进程。
            _sessionHost.ExitRequested = OnNewChainExitRequested;
        }

        private void OnNewChainExitRequested(int exitCode)
        {
            HYLDDebug.Log("[PMDsHost] R3-B 新链请求退出，exitCode=" + exitCode);
            HYLDDebug.FlushTrace();
            Application.Quit(exitCode);
        }

        /// <summary>
        /// 建立路由层并注册本局的战斗 handler。
        ///
        /// 日志回调全部接到 <see cref="EnqueueLog"/>：路由层可能在接收线程上产生日志，
        /// 而 EnqueueLog 是线程安全的入队（真正输出在主线程），因此这里接线是安全的。
        /// </summary>
        private void InitializeRouter()
        {
            _registry = new PMSingleBattleRegistry();
            _router = new PMUdpRouter(_socket, _registry, EnqueueLog, EnqueueLog, EnqueueLog);

            int battleId = _registry.BattleId;
            bool registered = _router.RegisterBattle(battleId, HandleDiagnosticBattlePacket);
            if (!registered)
            {
                LogWarning("[PMDsHost] 诊断 handler 注册失败（battleId=" + battleId + "）");
            }
        }

        /// <summary>
        /// **P3'-1 诊断 handler（临时）**：只把收到的 ActionCode 计数并周期性摘要输出，
        /// 用于证明「真实 protobuf 包 → 解析 → 路由 → 主线程分发」整条链路已跑通。
        ///
        /// P3'-3 迁入权威战斗仿真时，此处由真实的 <c>BattleController.Handle</c> 取代。
        /// 在那之前不要往这里叠协议——它的目的是可观测，不是功能。
        /// </summary>
        private void HandleDiagnosticBattlePacket(global::SocketProto.MainPack pack)
        {
            Interlocked.Increment(ref _battlePacketsDispatched);

            int byAction = 0;
            if (pack != null)
            {
                _lastBattleActionCode = pack.Actioncode;
                _lastBattleActionSeenTicks = Time.frameCount;
                byAction = 1;
            }

            Interlocked.Add(ref _battleActionAllowance, byAction);

            // 逐包打日志会淹掉输出（一局每秒几十包），只在首次与每 64 包输出一行。
            long total = Interlocked.Read(ref _battlePacketsDispatched);
            if (total == 1 || (total % 64) == 0)
            {
                HYLDDebug.Log("[PMDsHost] 战斗包已分发 " + total + " 个，最近 ActionCode=" + _lastBattleActionCode);
            }

            if (byAction == 0)
            {
                Interlocked.Increment(ref _battleNullPackets);
            }
        }

        // ================= 监听 =================

        /// <summary>绑定并开始异步接收。失败不抛异常：DS 要「起来 + 报错」，而不是静默消失。</summary>
        private bool TryBindSocket(PMNetLaunchOptions options)
        {
            try
            {
                IPAddress address;
                if (!IPAddress.TryParse(options.ListenAddress, out address))
                {
                    LogWarning("[PMDsHost] -listen 不是合法 IP，回退到 Any：" + options.ListenAddress);
                    address = IPAddress.Any;
                }

                _socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
                _socket.Bind(new IPEndPoint(address, options.ListenPort));

                IPEndPoint bound = (IPEndPoint)_socket.LocalEndPoint;
                BoundPort = bound.Port;

                _receiveBuffer = new byte[64 * 1024];
                _receiveEndPoint = new IPEndPoint(address.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                BeginReceive();
                return true;
            }
            catch (Exception e)
            {
                LogError("[PMDsHost] UDP 绑定失败（listen=" + options.ListenAddress + ":" + options.ListenPort + "）：" + e.Message);
                return false;
            }
        }

        /// <summary>发起一次异步接收。回调线程只做计数与入队，不做日志。</summary>
        private void BeginReceive()
        {
            try
            {
                _socket.BeginReceiveFrom(
                    _receiveBuffer,
                    0,
                    _receiveBuffer.Length,
                    SocketFlags.None,
                    ref _receiveEndPoint,
                    OnReceive,
                    null);
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _receiveErrors);
                EnqueueLog("[PMDsHost] BeginReceiveFrom 失败：" + e.Message);
            }
        }

        /// <summary>
        /// 接收回调（线程池线程）。纪律：不调用任何 UnityEngine API，只做计数、入队与回包。
        /// </summary>
        private void OnReceive(IAsyncResult ar)
        {
            string remote = null;
            int received = 0;

            try
            {
                EndPoint from = _receiveEndPoint;
                received = _socket.EndReceiveFrom(ar, ref from);
                remote = from != null ? from.ToString() : "<unknown>";
                Interlocked.Increment(ref _receivedDatagrams);
                TrackRemote(remote);

                // 真实路由（P3'-1）：把字节交给路由层「解析 + 入队」。
                // 路由层刻意为 UnityEngine 无关（日志走注入的回调），因此这一步在后台线程上是安全的；
                // handler 的执行与日志输出都不会在这里发生，而是在 Update()（主线程）。
                if (_router != null)
                {
                    _router.OnDatagramReceived(_receiveBuffer, received, from);
                }
            }
            catch (ObjectDisposedException)
            {
                // 正常关闭路径，不视为错误。
                return;
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _receiveErrors);
                EnqueueLog("[PMDsHost] 收包异常：" + e.Message);
            }
            finally
            {
                // 仅在套接字仍可用时继续下一次接收。
                if (_socket != null)
                {
                    BeginReceive();
                }
            }

            if (remote != null && received > 0)
            {
                // 逐包打日志会淹掉输出（扫描流量下更甚），只在首次与每 256 包输出一行。
                int seen = Interlocked.Increment(ref _receiveBatches);
                if (seen == 1 || (seen % 256) == 0)
                {
                    EnqueueLog("[PMDsHost] 收到 " + received + " 字节 from " + remote
                        + "（第 " + seen + " 个数据报）");
                }
            }
        }

        /// <summary>记录见过的远端端点（限量），用于判断「有没有真的连上来过」。</summary>
        private void TrackRemote(string remote)
        {
            lock (_remoteLock)
            {
                if (_knownRemotes.Count >= MaxTrackedRemotes)
                {
                    return;
                }

                if (!_knownRemotes.Contains(remote))
                {
                    _knownRemotes.Add(remote);
                }
            }
        }

        // ================= 逻辑帧与心跳 =================

        private void Update()
        {
            // R3-B 新链：唯一驱动点。
            //
            // PMDsSessionHost.Pump 内部会调 endpoint.Pump，而 endpoint.Pump 已经调用过
            // bridge.Update —— 宿主**绝不能**再调一次（契约 §7.2；R3-A 报告记录了
            // 「每帧两次 Update 会把数据报预算翻倍」这条缺陷）。
            if (_sessionHost != null)
            {
                _sessionHost.Pump();
                DrainPendingLogs();
                return;
            }

            // 旧诊断路径（不带 -bootstrap 的历史启动）保持原样：
            // 顺序很重要：先把上一帧/本帧到达的包分发完（战斗逻辑的输入），再推进逻辑帧。
            DrainInbound();
            DrainPendingLogs();
            DriveTick();

            if (Time.unscaledTime >= _nextHeartbeatTime)
            {
                _nextHeartbeatTime = Time.unscaledTime + HeartbeatIntervalSeconds;
                LogHeartbeat();
            }
        }

        /// <summary>
        /// 主线程出口：把接收线程入队的包取出来分发。这是 handler 唯一被允许执行的线程。
        /// </summary>
        private void DrainInbound()
        {
            if (_router == null)
            {
                return;
            }

            _router.DrainAndDispatch(MaxPacketsPerUpdate);
        }

        /// <summary>
        /// 按 TickRate 推进逻辑帧骨架。
        /// P3'-1 只计数；P3'-3 迁入权威战斗仿真后在此驱动权威帧（对应旧实现 <c>Battle.cs</c> 的 <c>CollectAndBroadcastCurrentFrame</c>）。
        /// </summary>
        private void DriveTick()
        {
            _tickAccumulator += Time.unscaledDeltaTime;

            int ticks = 0;
            while (_tickAccumulator >= _tickInterval && ticks < MaxTicksPerUpdate)
            {
                _tickAccumulator -= _tickInterval;
                ticks++;
                _tickCount++;

                // P3'-3 在此处接入权威战斗帧推进。
            }
        }

        // ================= 日志（全部在主线程）=================

        /// <summary>接收线程入队，Update 输出。避免在线程池线程触碰 UnityEngine API。</summary>
        private void EnqueueLog(string message)
        {
            lock (_logLock)
            {
                // 限流：避免异常风暴或扫描流量把队列撑爆。
                if (_pendingLogs.Count < 256)
                {
                    _pendingLogs.Add(message);
                }
            }
        }

        private void DrainPendingLogs()
        {
            List<string> batch = null;
            lock (_logLock)
            {
                if (_pendingLogs.Count > 0)
                {
                    batch = new List<string>(_pendingLogs);
                    _pendingLogs.Clear();
                }
            }

            if (batch == null)
            {
                return;
            }

            for (int i = 0; i < batch.Count; i++)
            {
                HYLDDebug.Log(batch[i]);
            }
        }

        private void LogReady(bool bound, PMNetLaunchOptions options)
        {
            HYLDDebug.Log("[PMDsHost] ===== HyldDS 就绪 =====");
            HYLDDebug.Log("[PMDsHost] dsid=" + (options.DsId ?? "<none>")
                + " matchid=" + (options.MatchId ?? "<none>")
                + " tickrate=" + options.TickRate);

            if (bound)
            {
                HYLDDebug.Log("[PMDsHost] 监听=" + options.ListenAddress + ":" + BoundPort + "（实际绑定端口 " + BoundPort + "）");
                HYLDDebug.Log("[PMDsHost] UDP 路由层已就绪（battleId=" + _registry.BattleId
                    + "，已注册诊断 handler；P3'-3 将由权威战斗仿真取代）");
                HYLDDebug.Log("[PMDsHost] 本机 IPv4=" + DescribeLocalAddresses());
            }
            else
            {
                HYLDDebug.LogError("[PMDsHost] 端口绑定失败，进程仍存活但不接收数据；请检查 -listen/-port");
            }

            if (string.IsNullOrEmpty(options.LobbyAddress))
            {
                HYLDDebug.LogWarning("[PMDsHost] 未提供 -lobby，DS 无法向大厅注册（P4' 接入注册协议后必需）");
            }

            HYLDDebug.Log("[PMDsHost] ====================");
        }

        private void LogHeartbeat()
        {
            int remotes;
            lock (_remoteLock)
            {
                remotes = _knownRemotes.Count;
            }

            HYLDDebug.Log("[PMDsHost] heartbeat tick=" + Interlocked.Read(ref _tickCount)
                + " recv=" + Interlocked.CompareExchange(ref _receivedDatagrams, 0, 0)
                + " recvErr=" + Interlocked.CompareExchange(ref _receiveErrors, 0, 0)
                + " remotes=" + remotes);

            // 路由层计数器：这一行是判断「包到底有没有进来、为什么没被处理」的关键证据。
            if (_router != null)
            {
                HYLDDebug.Log("[PMDsHost] router " + _router.DescribeCounters()
                    + " battles=" + _router.RegisteredBattleCount);
                HYLDDebug.Log("[PMDsHost] battleDispatch total=" + Interlocked.Read(ref _battlePacketsDispatched)
                    + " lastAction=" + (_lastBattleActionCode != null ? _lastBattleActionCode.ToString() : "<none>")
                    + " lastFrame=" + _lastBattleActionSeenTicks
                    + " nullPackets=" + _battleNullPackets);
            }

            // DS 上不跑 HYLDManger.Update，因此客户端的「每帧 Trace 刷盘」不生效。
            // 无头进程被强杀不会执行 Shutdown，日志必须主动落盘，否则排查时什么都没留下。
            HYLDDebug.FlushTrace();
            HYLDDebug.FlushFrameTrace();
        }

        private static string DescribeLocalAddresses()
        {
            try
            {
                IPAddress[] addresses = Dns.GetHostAddresses(Dns.GetHostName());
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < addresses.Length; i++)
                {
                    if (addresses[i].AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    if (sb.Length > 0)
                    {
                        sb.Append(", ");
                    }

                    sb.Append(addresses[i]);
                }

                return sb.Length > 0 ? sb.ToString() : "<none>";
            }
            catch (Exception e)
            {
                return "<查询失败: " + e.Message + ">";
            }
        }

        private void LogWarning(string message)
        {
            HYLDDebug.LogWarning(message);
        }

        private void LogError(string message)
        {
            HYLDDebug.LogError(message);
        }

        // ================= 收尾 =================

        private void OnApplicationQuit()
        {
            HYLDDebug.Log("[PMDsHost] OnApplicationQuit 触发（应用正在退出）——若 DS 本应常驻，说明有东西在主动退出进程");
            Shutdown("OnApplicationQuit");
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(Instance, this))
            {
                Instance = null;
            }

            HYLDDebug.Log("[PMDsHost] OnDestroy 触发（对象被销毁/场景卸载）——若 DS 本应常驻，说明 DontDestroyOnLoad 未生效");
            Shutdown("OnDestroy");
        }

        /// <summary>幂等关闭：套接字关闭后接收回调会以 ObjectDisposedException 正常退出。</summary>
        private void Shutdown(string reason)
        {
            if (_sessionHost != null)
            {
                HYLDDebug.Log("[PMDsHost] 开始关闭 R3-B 新链宿主，reason=" + reason
                              + " 已存活 " + Time.realtimeSinceStartup.ToString("F2") + " 秒");
                _sessionHost.ExitRequested = null;
                _sessionHost.Dispose();
                _sessionHost = null;
                DrainPendingLogs();
                HYLDDebug.Log("[PMDsHost] 已关闭（reason=" + reason + "，新链）");
                HYLDDebug.FlushTrace();
                return;
            }

            if (_socket == null)
            {
                HYLDDebug.Log("[PMDsHost] Shutdown(" + reason + ") 重入，套接字已关闭");
                return;
            }

            // 存活时长：DS 应长期常驻，这里能直接看出「是不是刚起来就被拆了」
            HYLDDebug.Log("[PMDsHost] 开始关闭，reason=" + reason + " 已存活 " + Time.realtimeSinceStartup.ToString("F2") + " 秒，tick=" + Interlocked.Read(ref _tickCount));

            // 1) 先把路由层摘掉，避免套接字关闭后仍有 handler 被分发。
            if (_router != null)
            {
                int discarded = _router.ClearInbound();
                _router.UnregisterBattle(_registry != null ? _registry.BattleId : 1);
                HYLDDebug.Log("[PMDsHost] 路由层已停止，丢弃了 " + discarded + " 个待处理包；" + _router.DescribeCounters());
            }

            try
            {
                _socket.Close();
            }
            catch (Exception e)
            {
                EnqueueLog("[PMDsHost] 关闭套接字异常：" + e.Message);
            }
            finally
            {
                _socket = null;
            }

            DrainPendingLogs();
            HYLDDebug.Log("[PMDsHost] 已关闭（reason=" + reason + "）");
            HYLDDebug.FlushTrace();
            HYLDDebug.FlushFrameTrace();
        }
    }
}
