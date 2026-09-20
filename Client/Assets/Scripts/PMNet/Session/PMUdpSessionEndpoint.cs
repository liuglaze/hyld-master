using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using PMNet.Control;
using PMNet.Transport;

namespace PMNet.Session
{
    // =====================================================================================
    //  R3-B：Unity 侧 UDP 入局宿主（契约 §7.2，公开签名**逐字**按契约冻结）
    //
    //  为什么这个类存在（而不是让 Unity 组自己写 socket）：
    //   ① 次序是安全性质，不是风格问题：**未认证的字节绝不能先进 Transport**
    //      （Transport 收到合法数据报就立刻回 ack，对端据此退休可靠消息 ⇒ 静默的可靠性破洞）。
    //      「先握手、验票、绑端点、激活，之后才 OnDatagram」这条链只能在**同时持有 socket 与桥**的地方落实。
    //   ② 线程：全部状态与 socket 收发都由调用方主线程 <see cref="Pump"/> 驱动，无后台线程、无 Unity API。
    //   ③ 有界：每 Pump 收包数、握手帧上限、连接数、失败端点表、墓碑表全部有上限。
    //
    //  契约 §7.2 原文与本实现的对应：
    //   - 「失败无回包」：验票/账本/激活任一环节失败 ⇒ 一个字节都不回。
    //   - 「成功应答不大于请求」：客户端按已知 ServerHello 长度填充，DS 侧回包前再核一次。
    //   - 「Pump(nowMs, nowUnixSeconds)：排空有限 UDP、握手重试，再 bridge.Update；宿主不要二次 Update」：
    //     <see cref="Pump"/> 内部**恰好**调用一次 <c>bridge.Update</c>。
    //   - 「成功后 Connected 事件只触发一次」：每条连接只在激活成功那一刻抛一次。
    //   - 「peer 连接未激活数据字节不进 Transport」：未认证端点/未激活连接的字节在这里就被丢弃并计数。
    //   - 「连接数 &lt;= 6」：容量取自名册人数（账本绑定上限）。
    //   - 「入场票据到期只影响新的入场/重试」：本类**不**按票据到期摘除任何已激活会话。
    // =====================================================================================

    /// <summary>
    /// 一局的 UDP 入局端点（服务端一份、每个客户端一份）。
    ///
    /// 生命周期：<c>OpenServer/OpenClient</c> → 每帧 <see cref="Pump"/> → <see cref="Dispose"/>。
    /// 线程：**单线程**（调用方主线程）。所有方法都必须在同一线程调用。
    /// </summary>
    public sealed class PMUdpSessionEndpoint : IDisposable
    {
        /// <summary>单次 <see cref="Pump"/> 最多收取的数据报数（契约 §7.2：socket 每 Pump 收包有预算）。</summary>
        public const int MaxBatchDatagramsPerPump = 64;

        /// <summary>握手帧上限（与 <see cref="PMHandshakeCodec.MaxFrameBytes"/> 同源）。</summary>
        public const int MaxHandshakeFrameBytes = PMHandshakeCodec.MaxFrameBytes;

        /// <summary>客户端握手重试间隔（毫秒）。</summary>
        public const int HandshakeRetryIntervalMs = 250;

        /// <summary>客户端握手最大尝试次数（超过即报失败，不无限重试）。</summary>
        public const int MaxHandshakeHellos = 24;

        /// <summary>服务端会话空闲超时默认值（毫秒；0 表示不做端点级空闲判定）。</summary>
        public const int DefaultSessionIdleTimeoutMs = 30000;

        /// <summary>接收缓冲字节数（大于传输层与握手两者的单帧上限）。</summary>
        public const int ReceiveBufferBytes = 2048;

        private enum ReceiveAction
        {
            Continue = 0,
            Stop = 1,
        }

        private sealed class ServerSession
        {
            public PMTransportConnection Connection;
            public IPEndPoint Remote;
            public string EndpointKey;
            public int ConnectionId;
            public long LastInboundMs;
        }

        private sealed class UdpLink : IPMTransportLink
        {
            private readonly PMUdpSessionEndpoint _owner;
            private readonly IPEndPoint _remote;
            private readonly string _name;

            public UdpLink(PMUdpSessionEndpoint owner, IPEndPoint remote, string name)
            {
                _owner = owner;
                _remote = remote;
                _name = name;
            }

            public bool Send(byte[] buffer, int offset, int count)
            {
                return _owner.SendDatagram(_remote, buffer, offset, count);
            }

            public string Describe()
            {
                return _name;
            }
        }

        // ── 组成 ────────────────────────────────────────────────────────
        private readonly bool _server;
        private readonly PMNetSessionBridge _bridge;
        private readonly Socket _socket;
        private readonly int _boundPort;
        private readonly PMTransportConfig _transportConfig;
        private readonly int _sessionIdleTimeoutMs;
        private readonly byte[] _recvBuffer = new byte[ReceiveBufferBytes];

        // 服务端
        private readonly PMDsEndpointLedger _ledger;
        private readonly PMHandshakeServer _handshakeServer;
        private readonly int _maxConnections;
        private readonly List<ServerSession> _sessions = new List<ServerSession>(8);
        private readonly Dictionary<string, ServerSession> _byEndpoint =
            new Dictionary<string, ServerSession>(StringComparer.Ordinal);
        private readonly List<ServerSession> _deadScratch = new List<ServerSession>(4);

        // 客户端
        private readonly PMHandshakeClient _handshakeClient;
        private readonly IPEndPoint _remote;
        private PMTransportConnection _clientConnection;
        private bool _clientActivated;
        private bool _clientFailed;
        private long _lastHelloMs;

        private EndPoint _anyEndpoint;
        private bool _disposed;
        private long _nowUnixSeconds;

        // ── 事件 ────────────────────────────────────────────────────────
        /// <summary>一条连接**激活成功**后触发（每条连接只触发一次）。</summary>
        public event Action<PMTransportConnection> Connected;

        /// <summary>端点级失败（握手超时/被拒、激活失败、会话断开、socket 层不可恢复错误）。</summary>
        public event Action<string> Failed;

        // ── 计数（诊断/门禁断言；非 0 才算「真的跑到了」）────────────────
        /// <summary>成功发出的数据报数。</summary>
        public long DatagramsSent;

        /// <summary>成功收取的数据报数。</summary>
        public long DatagramsReceived;

        /// <summary>socket 发送失败次数（对端不可达等；不抛出、不作废连接）。</summary>
        public long SendFailures;

        /// <summary>socket 接收错误次数（ICMP 复位、超限报文等）。</summary>
        public long ReceiveErrors;

        /// <summary>非预期的异常次数（既不是 socket 也不是已分类错误的兜底计数）。</summary>
        public long UnexpectedErrors;

        /// <summary>握手帧被拒次数（服务端；**其中每一次都没有回包**）。</summary>
        public long HandshakeRejections;

        /// <summary>幂等重试次数（同票同端点，未新建连接）。</summary>
        public long HandshakeRetries;

        /// <summary>因本端点连接数已满而撤回消费的次数。</summary>
        public long CapacityRejections;

        /// <summary>激活失败次数（身份/摘要/世代被拒 ⇒ 回滚消费，不写墓碑）。</summary>
        public long ActivationFailures;

        /// <summary>已认证会话收到的数据报数（真正进了 Transport 的那部分）。</summary>
        public long BoundDatagrams;

        /// <summary>未认证端点发来的数据报数（**未进 Transport**）。</summary>
        public long DroppedUnboundDatagrams;

        /// <summary>客户端在激活前收到的非握手数据报数（**未进 Transport**）。</summary>
        public long DroppedPreActivationDatagrams;

        /// <summary>客户端收到的非预期来源数据报数。</summary>
        public long DroppedForeignDatagrams;

        /// <summary>激活后收到的握手帧数（重发的 ServerHello；忽略）。</summary>
        public long PostActivationHandshakeFrames;

        /// <summary>格式非法的 ServerHello 次数（客户端）。</summary>
        public long MalformedHandshakeFrames;

        /// <summary>与 offer 不符的 ServerHello 次数（错 nonce/摘要/对局/世代/身份）。</summary>
        public long IdentityMismatches;

        /// <summary>客户端发出的 ClientHello 次数。</summary>
        public long HellosSent;

        /// <summary>服务端会话关闭次数（含断开与空闲超时）。</summary>
        public long SessionsClosed;

        /// <summary>最近一次 socket 发送错误（诊断）。</summary>
        public string LastSendError;

        /// <summary>最近一次 Pump 的 UTC 秒（诊断）。</summary>
        public long LastPumpUnixSeconds { get { return _nowUnixSeconds; } }

        private PMUdpSessionEndpoint(PMNetSessionBridge bridge, Socket socket, PMTransportConfig transportConfig,
                                     int sessionIdleTimeoutMs, PMDsBootstrappedMatch boot,
                                     PMDsEndpointLedger ledger, PMHandshakeServer handshakeServer, int maxConnections,
                                     PMHandshakeClient handshakeClient, IPEndPoint remote)
        {
            _bridge = bridge;
            _socket = socket;
            _transportConfig = transportConfig ?? new PMTransportConfig();
            _sessionIdleTimeoutMs = sessionIdleTimeoutMs;

            _server = boot != null;
            _ledger = ledger;
            _handshakeServer = handshakeServer;
            _maxConnections = maxConnections;
            _handshakeClient = handshakeClient;
            _remote = remote;

            IPEndPoint local = socket.LocalEndPoint as IPEndPoint;
            _boundPort = local != null ? local.Port : 0;

            _anyEndpoint = new IPEndPoint(
                socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        }

        // =================================================================================
        //  构造（契约 §7.2 冻结签名；可选参数只做扩展，不改变必需参数面）
        // =================================================================================

        /// <summary>
        /// 服务端端点：绑定 <paramref name="listenAddress"/>:<paramref name="port"/>，
        /// 用 boot 名册**逐身份验票**后接纳玩家连接。
        ///
        /// <paramref name="port"/> 传 0 表示由系统分配（测试/联调用），实际端口见 <see cref="BoundPort"/>。
        /// </summary>
        public static PMUdpSessionEndpoint OpenServer(PMDsBootstrappedMatch boot, PMNetSessionBridge bridge,
                                                      string listenAddress, int port,
                                                      PMTransportConfig transportConfig = null,
                                                      int sessionIdleTimeoutMs = DefaultSessionIdleTimeoutMs)
        {
            if (boot == null) { throw new ArgumentNullException("boot"); }
            if (bridge == null) { throw new ArgumentNullException("bridge"); }
            if (!bridge.IsServer)
            {
                throw new ArgumentException("OpenServer 需要权威侧世界（bridge.IsServer 为真）", "bridge");
            }

            if (port < 0 || port > 65535)
            {
                throw new ArgumentOutOfRangeException("port", "端口必须在 0..65535 之间（0 = 系统分配）");
            }

            if (sessionIdleTimeoutMs < 0)
            {
                throw new ArgumentOutOfRangeException("sessionIdleTimeoutMs", "空闲超时不得为负");
            }

            IPAddress address = ResolveListenAddress(listenAddress);

            // 先建处理器（名册不完整时它直接抛），再建 socket —— 避免失败路径上留下已绑定的端口。
            int rosterCount = boot.Bootstrap != null && boot.Bootstrap.AsBootstrap != null
                ? boot.Bootstrap.AsBootstrap.Players.Length
                : 0;
            int maxBindings = rosterCount > 0 ? rosterCount : PMDsEndpointLedger.DefaultMaxBindings;

            PMDsEndpointLedger ledger = new PMDsEndpointLedger(maxBindings, PMDsEndpointLedger.DefaultMaxTombstones);
            PMHandshakeServer handshakeServer = new PMHandshakeServer(boot, ledger);

            Socket socket = CreateSocket(address, port);

            return new PMUdpSessionEndpoint(bridge, socket, transportConfig, sessionIdleTimeoutMs,
                boot, ledger, handshakeServer, maxBindings, null, null);
        }

        /// <summary>
        /// 客户端端点：连 <c>offer.Host:offer.Port</c>，用 offer 的票据与身份做**关联校验**。
        ///
        /// 注意：这不是密码学服务器认证（客户端没有密钥）——只做「与 Lobby 通知交叉比对」，
        /// 挡住盲打/旧响应重放/错局错代次，挡不住能读到票据的本机攻击者（契约 §7.2 明确的边界）。
        /// </summary>
        public static PMUdpSessionEndpoint OpenClient(PMDsEntryOffer offer, PMNetSessionBridge bridge,
                                                      PMTransportConfig transportConfig = null)
        {
            if (offer == null) { throw new ArgumentNullException("offer"); }
            if (bridge == null) { throw new ArgumentNullException("bridge"); }
            if (bridge.IsServer)
            {
                throw new ArgumentException("OpenClient 需要客户端世界（bridge.IsServer 必须为假）", "bridge");
            }

            PMHandshakeClient handshakeClient = new PMHandshakeClient(offer);
            IPEndPoint remote = ResolveRemote(offer.Host, offer.Port);

            IPAddress bindAddress = remote.AddressFamily == AddressFamily.InterNetworkV6
                ? IPAddress.IPv6Any
                : IPAddress.Any;
            Socket socket = CreateSocket(bindAddress, 0);

            return new PMUdpSessionEndpoint(bridge, socket, transportConfig, 0,
                null, null, null, 0, handshakeClient, remote);
        }

        // =================================================================================
        //  只读视图
        // =================================================================================

        /// <summary>本端实际绑定的端口（服务端必须等于分配端口，客户端为临时端口）。</summary>
        public int BoundPort { get { return _boundPort; } }

        /// <summary>当前处于就绪（已激活且传输未断开）状态的连接数。</summary>
        public int ConnectionCount
        {
            get
            {
                if (!_server)
                {
                    return _clientConnection != null && _clientConnection.IsReady ? 1 : 0;
                }

                int count = 0;
                for (int i = 0; i < _sessions.Count; i++)
                {
                    ServerSession session = _sessions[i];
                    if (session.Connection != null && session.Connection.IsReady)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>本端是否为服务端端点。</summary>
        public bool IsServerSide { get { return _server; } }

        /// <summary>服务端侧的点端消费账本（诊断/门禁）；客户端侧为 null。</summary>
        public PMDsEndpointLedger Ledger { get { return _ledger; } }

        /// <summary>服务端侧的握手处理器（诊断/门禁）；客户端侧为 null。</summary>
        public PMHandshakeServer HandshakeServer { get { return _handshakeServer; } }

        /// <summary>客户端侧已激活的连接（未激活时为 null）。</summary>
        public PMTransportConnection ClientConnection { get { return _clientConnection; } }

        /// <summary>客户端侧握手状态机（诊断）；服务端侧为 null。</summary>
        public PMHandshakeClient HandshakeClient { get { return _handshakeClient; } }

        /// <summary>客户端侧是否已确定失败（不再重试）。</summary>
        public bool ClientFailed { get { return _clientFailed; } }

        /// <summary>
        /// 本端点是否已释放。返回现有 <c>_disposed</c> 的只读快照：<see cref="Dispose"/> 置位后为 true，
        /// 内部 socket 层 <see cref="ObjectDisposedException"/> 兜底路径置位后同样为 true。
        /// 只读诊断用，不改变 <see cref="Dispose"/> / <see cref="Pump"/> 的任何语义；供宿主识别
        /// 「端点已被释放、不再 Pump」的静默通道并据此显式失败/冻结。
        /// </summary>
        public bool IsDisposed { get { return _disposed; } }

        // =================================================================================
        //  主线程驱动
        // =================================================================================

        /// <summary>
        /// 主线程驱动一帧（契约 §7.2 冻结次序）：排空有界 UDP → 握手（重试/接纳）→ <c>bridge.Update</c>。
        ///
        /// **宿主不要再调一次 bridge.Update**：本方法已经调用，复制 Tick / 生命周期 / 数据报预算
        /// 都按「每帧一次」记账（见 R3-A 报告：帧内共享 32 个数据报预算）。
        /// </summary>
        public void Pump(long nowMs, long nowUnixSeconds)
        {
            if (_disposed)
            {
                return;
            }

            _nowUnixSeconds = nowUnixSeconds;

            if (_server)
            {
                PruneServerSessions(nowMs, nowUnixSeconds);
            }

            ReceiveDatagrams(nowMs, nowUnixSeconds);

            if (!_server)
            {
                ClientHandshakeTick(nowMs);
            }

            _bridge.Update(nowMs);

            if (!_server)
            {
                CheckClientSessionAfterUpdate();
            }
        }

        /// <summary>释放：断开并释放全部连接、关闭 socket（幂等）。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_server)
            {
                for (int i = 0; i < _sessions.Count; i++)
                {
                    ServerSession session = _sessions[i];
                    if (session.Connection != null)
                    {
                        session.Connection.Dispose();
                    }
                }

                _sessions.Clear();
                _byEndpoint.Clear();

                if (_ledger != null)
                {
                    _ledger.ReleaseSession();
                }
            }
            else if (_clientConnection != null)
            {
                _clientConnection.Dispose();
                _clientConnection = null;
                _clientActivated = false;
            }

            try
            {
                _socket.Close();
            }
            catch (Exception)
            {
                // 关闭失败无处上报（本对象正在消失），但不能让宿主因为 Dispose 抛异常。
            }
        }

        // =================================================================================
        //  收包（有界）
        // =================================================================================

        private void ReceiveDatagrams(long nowMs, long nowUnixSeconds)
        {
            int budget = MaxBatchDatagramsPerPump;

            // 每轮重置远端槽：ReceiveFrom 会把它改写成实际来源。
            _anyEndpoint = new IPEndPoint(
                _socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

            while (budget-- > 0)
            {
                int received;

                try
                {
                    received = _socket.ReceiveFrom(_recvBuffer, 0, _recvBuffer.Length, SocketFlags.None, ref _anyEndpoint);
                }
                catch (SocketException ex)
                {
                    if (ClassifyReceiveError(ex) == ReceiveAction.Stop)
                    {
                        break;
                    }

                    continue;
                }
                catch (ObjectDisposedException)
                {
                    _disposed = true;
                    break;
                }
                catch (InvalidOperationException)
                {
                    // socket 已被关闭/未绑定：本 Pump 不再尝试。
                    UnexpectedErrors++;
                    break;
                }
                catch (Exception ex)
                {
                    UnexpectedErrors++;
                    LastSendError = ex.GetType().Name;
                    break;
                }

                if (received <= 0)
                {
                    continue;
                }

                DatagramsReceived++;

                IPEndPoint remote = _anyEndpoint as IPEndPoint;
                if (remote == null)
                {
                    DroppedForeignDatagrams++;
                    continue;
                }

                DatagramReceived(remote, received, nowMs, nowUnixSeconds);
            }
        }

        /// <summary>
        /// socket 接收错误的分类处置（**必须分类**：ICMP 复位是最常见的正常现象，
        /// 而「无数据可读」只表示本次排空结束）。
        /// </summary>
        private ReceiveAction ClassifyReceiveError(SocketException ex)
        {
            SocketError code = ex.SocketErrorCode;

            if (code == SocketError.WouldBlock || code == SocketError.IOPending
                || code == SocketError.NoBufferSpaceAvailable)
            {
                // 非阻塞 socket 无数据可读 ⇒ 本次排空结束（不是错误，不计数）。
                return ReceiveAction.Stop;
            }

            if (code == SocketError.ConnectionReset || code == SocketError.MessageSize
                || code == SocketError.NetworkUnreachable || code == SocketError.HostUnreachable
                || code == SocketError.TimedOut || code == SocketError.Interrupted)
            {
                // 对端不可达（ICMP）/对端发来超过缓冲的报文/被信号打断：计数后继续排空。
                ReceiveErrors++;
                LastSendError = code.ToString();
                return ReceiveAction.Continue;
            }

            ReceiveErrors++;
            LastSendError = code.ToString();
            return ReceiveAction.Stop;
        }

        private void DatagramReceived(IPEndPoint remote, int count, long nowMs, long nowUnixSeconds)
        {
            string endpointKey = EndpointKeyOf(remote);

            if (_server)
            {
                PMHandshakeKind kind;
                if (PMHandshakeCodec.IsHandshakeDatagram(_recvBuffer, 0, count, out kind))
                {
                    ServerHandshake(remote, endpointKey, count, nowMs, nowUnixSeconds);
                    return;
                }

                ServerSession session;
                if (_byEndpoint.TryGetValue(endpointKey, out session) && session.Connection != null)
                {
                    session.LastInboundMs = nowMs;
                    BoundDatagrams++;
                    session.Connection.OnDatagram(_recvBuffer, 0, count);
                    return;
                }

                // 未认证端点：**不建任何状态、不进 Transport**（否则传输层会先回 ack）。
                DroppedUnboundDatagrams++;
                return;
            }

            // ── 客户端 ──────────────────────────────────────────────────
            if (!IsExpectedSource(remote))
            {
                DroppedForeignDatagrams++;
                return;
            }

            if (_clientConnection == null)
            {
                PMHandshakeKind kind;
                if (!PMHandshakeCodec.IsHandshakeDatagram(_recvBuffer, 0, count, out kind))
                {
                    DroppedPreActivationDatagrams++;
                    return;
                }

                ClientHandshakeResponse(count);
                return;
            }

            PMHandshakeKind lateKind;
            if (PMHandshakeCodec.IsHandshakeDatagram(_recvBuffer, 0, count, out lateKind))
            {
                // 已激活后重发的 ServerHello：忽略（不重复激活、不进 Transport）。
                PostActivationHandshakeFrames++;
                return;
            }

            BoundDatagrams++;
            _clientConnection.OnDatagram(_recvBuffer, 0, count);
        }

        // =================================================================================
        //  服务端：握手接纳 + 会话维护
        // =================================================================================

        private void ServerHandshake(IPEndPoint remote, string endpointKey, int count, long nowMs, long nowUnixSeconds)
        {
            PMHandshakeServerResult result;
            bool accepted = _handshakeServer.TryHandle(_recvBuffer, 0, count, endpointKey, nowUnixSeconds, out result);

            if (!accepted || result.Response == null)
            {
                // 契约 §7.2：失败**没有回包**。这里连一个「拒绝帧」都不发。
                HandshakeRejections++;
                return;
            }

            ServerSession existing;
            if (_byEndpoint.TryGetValue(endpointKey, out existing)
                && existing.ConnectionId == result.Binding.ConnectionId)
            {
                // 幂等重试：同票同端点 ⇒ 回同一条（内容等价、nonce 回带为本次的）ServerHello，不新建连接。
                HandshakeRetries++;
                SendDatagram(existing.Remote, result.Response, 0, result.Response.Length);
                return;
            }

            if (_sessions.Count >= _maxConnections)
            {
                _ledger.RenounceConsume(result.Binding.TicketDigest, endpointKey);
                CapacityRejections++;
                return;
            }

            PMSessionIdentity identity = BuildServerIdentity(result.Binding);
            UdpLink link = new UdpLink(this, remote, endpointKey + "->ds");
            PMTransportConnection connection = new PMTransportConnection(
                identity, _bridge.World, _bridge, link, _transportConfig);

            string error;
            if (!connection.TryActivate(out error))
            {
                // 激活失败（摘要/世代/角色/uid 冲突）⇒ **不发回包**，并撤回消费。
                // 用 ProtocolError 而不是 Dispose：Transport 只在 LocalClosed/PeerClosed 时通知对端，
                // 绝不能让我们自己的断开通知泄漏给一个尚未认证的端点。
                connection.Disconnect(PMDisconnectReason.ProtocolError);
                _ledger.RenounceConsume(result.Binding.TicketDigest, endpointKey);
                ActivationFailures++;
                Warn("[PMUdp] 入局激活失败（" + endpointKey + "）：" + error);
                return;
            }

            ServerSession session = new ServerSession();
            session.Connection = connection;
            session.Remote = remote;
            session.EndpointKey = endpointKey;
            session.ConnectionId = result.Binding.ConnectionId;
            // 握手数据报就是本连接的第一次入站流量：从此刻开始计空闲时间。
            // （若留 0，则「握手完成后一个字节都没来过」的悬挂会话永远不会被端点级空闲判定清掉。）
            session.LastInboundMs = nowMs;

            _sessions.Add(session);
            _byEndpoint[endpointKey] = session;

            SendDatagram(remote, result.Response, 0, result.Response.Length);

            // 「Connected 在激活后触发且只一次」：这条路径每条连接只会走到一次。
            RaiseConnected(connection);
        }

        private PMSessionIdentity BuildServerIdentity(PMSessionBinding binding)
        {
            PMSessionIdentity identity = new PMSessionIdentity();
            identity.ConnectionId = binding.ConnectionId;
            identity.PeerRole = PMSessionPeerRole.Client;
            identity.Uid = binding.Uid;
            identity.PlayerId = binding.PlayerId;
            identity.TeamId = binding.TeamId;
            identity.HeroId = binding.HeroId;
            identity.Epoch = binding.Epoch;
            identity.LocalProtocolHash = LocalProtocolHash(binding.ProtocolHash);
            identity.PeerProtocolHash = binding.ProtocolHash;
            identity.MatchId = binding.MatchId;
            identity.DsId = binding.DsId;
            return identity;
        }

        /// <summary>
        /// 本端协议摘要：**优先取注册表**（TryActivate 会再核一次，从而「摘要一致」不是自说自话）。
        /// 未 Seal 的宿主（例如只跑纯 C# 门禁的工具）退回「对端声明值」。
        /// </summary>
        private static uint LocalProtocolHash(uint declared)
        {
            return PMNetRegistry.IsSealed ? PMNetRegistry.ProtocolHash : declared;
        }

        private void PruneServerSessions(long nowMs, long nowUnixSeconds)
        {
            if (_sessions.Count > 0)
            {
                _deadScratch.Clear();

                for (int i = 0; i < _sessions.Count; i++)
                {
                    ServerSession session = _sessions[i];
                    bool dead = session.Connection == null || !session.Connection.IsReady;

                    if (!dead && _sessionIdleTimeoutMs > 0
                        && nowMs - session.LastInboundMs > _sessionIdleTimeoutMs)
                    {
                        // 端点级空闲判定：传输层自己的空闲超时只在「收到过东西之后」生效，
                        // 这条覆盖「握手完成后一个字节都没来过」的悬挂会话（否则 6 个坑位会被慢慢占满）。
                        session.Connection.Disconnect(PMDisconnectReason.Timeout);
                        dead = true;
                    }

                    if (dead)
                    {
                        _deadScratch.Add(session);
                    }
                }

                for (int i = 0; i < _deadScratch.Count; i++)
                {
                    RemoveServerSession(_deadScratch[i], nowUnixSeconds, true);
                }
            }

            if (_ledger != null)
            {
                _ledger.PruneExpired(nowUnixSeconds);
            }
        }

        private void RemoveServerSession(ServerSession session, long nowUnixSeconds, bool raiseFailed)
        {
            _sessions.Remove(session);

            ServerSession mapped;
            if (_byEndpoint.TryGetValue(session.EndpointKey, out mapped) && ReferenceEquals(mapped, session))
            {
                _byEndpoint.Remove(session.EndpointKey);
            }

            // 断开即消费墓碑：旧票在过期前不得再被消费（含换端点重连）。
            if (_ledger != null)
            {
                _ledger.ReleaseEndpoint(session.EndpointKey, nowUnixSeconds);
            }

            SessionsClosed++;

            if (raiseFailed)
            {
                string reason = session.Connection != null
                    ? session.Connection.DisconnectReason.ToString()
                    : "null";
                RaiseFailed("入局会话关闭：conn#" + session.ConnectionId + " endpoint="
                            + (session.EndpointKey ?? string.Empty) + " reason=" + reason);
            }
        }

        // =================================================================================
        //  客户端：握手重试 + 激活
        // =================================================================================

        private void ClientHandshakeTick(long nowMs)
        {
            if (_clientActivated || _clientFailed)
            {
                return;
            }

            if (_handshakeClient.HellosSent >= MaxHandshakeHellos)
            {
                RaiseFailed("入局握手超时：已发送 " + _handshakeClient.HellosSent
                            + " 次 ClientHello 仍未通过关联校验（对端可能不在本局/票据已过期）");
                _clientFailed = true;
                return;
            }

            if (_handshakeClient.HellosSent > 0 && nowMs - _lastHelloMs < HandshakeRetryIntervalMs)
            {
                return;
            }

            byte[] hello;
            try
            {
                hello = _handshakeClient.BuildClientHello();
            }
            catch (Exception ex)
            {
                RaiseFailed("ClientHello 构造失败：" + ex.GetType().Name + " " + ex.Message);
                _clientFailed = true;
                return;
            }

            _lastHelloMs = nowMs;
            SendDatagram(_remote, hello, 0, hello.Length);
            HellosSent++;
        }

        private void ClientHandshakeResponse(int count)
        {
            PMSessionBinding binding;
            string error;
            PMHandshakeClientOutcome outcome = _handshakeClient.Handle(_recvBuffer, 0, count, out binding, out error);

            if (outcome == PMHandshakeClientOutcome.Accepted)
            {
                ActivateClient(binding);
                return;
            }

            if (outcome == PMHandshakeClientOutcome.Rejected)
            {
                RaiseFailed("入局握手被拒：" + error);
                _clientFailed = true;
                return;
            }

            if (outcome == PMHandshakeClientOutcome.Malformed)
            {
                MalformedHandshakeFrames++;
            }
            else
            {
                IdentityMismatches++;
            }

            Warn("[PMUdp] 客户端忽略一条 ServerHello（继续重试）：" + error);
        }

        private void ActivateClient(PMSessionBinding binding)
        {
            PMSessionIdentity identity = new PMSessionIdentity();
            identity.ConnectionId = 1;
            identity.PeerRole = PMSessionPeerRole.Server;
            identity.Uid = binding.Uid;
            identity.PlayerId = binding.PlayerId;
            identity.TeamId = binding.TeamId;
            identity.HeroId = binding.HeroId;
            identity.Epoch = binding.Epoch;
            identity.LocalProtocolHash = LocalProtocolHash(binding.ProtocolHash);
            identity.PeerProtocolHash = binding.ProtocolHash;
            identity.MatchId = binding.MatchId;
            identity.DsId = binding.DsId;

            UdpLink link = new UdpLink(this, _remote, "client->" + _remote);
            PMTransportConnection connection = new PMTransportConnection(
                identity, _bridge.World, _bridge, link, _transportConfig);

            string error;
            if (!connection.TryActivate(out error))
            {
                connection.Disconnect(PMDisconnectReason.ProtocolError);
                RaiseFailed("客户端激活失败：" + error);
                _clientFailed = true;
                return;
            }

            _clientConnection = connection;
            _clientActivated = true;
            RaiseConnected(connection);
        }

        private void CheckClientSessionAfterUpdate()
        {
            if (_clientConnection == null || _clientFailed)
            {
                return;
            }

            if (_clientConnection.IsReady)
            {
                return;
            }

            RaiseFailed("与 DS 的会话已断开（原因 " + _clientConnection.DisconnectReason + "）");
            _clientFailed = true;
        }

        // =================================================================================
        //  工具
        // =================================================================================

        /// <summary>
        /// 发送一个数据报。**永不抛出**（<see cref="IPMTransportLink"/> 的约定：发送失败不该废掉一局）。
        /// </summary>
        private bool SendDatagram(IPEndPoint target, byte[] buffer, int offset, int count)
        {
            if (_disposed || _socket == null || target == null)
            {
                SendFailures++;
                return false;
            }

            try
            {
                int sent = _socket.SendTo(buffer, offset, count, SocketFlags.None, target);
                if (sent != count)
                {
                    SendFailures++;
                    return false;
                }

                DatagramsSent++;
                return true;
            }
            catch (SocketException ex)
            {
                // 回环上「对端端口不可达」会以 ICMP 形式回到我们这里，属正常现象：计数、不抛出。
                SendFailures++;
                LastSendError = ex.SocketErrorCode.ToString();
                return false;
            }
            catch (ObjectDisposedException)
            {
                SendFailures++;
                return false;
            }
            catch (Exception ex)
            {
                SendFailures++;
                UnexpectedErrors++;
                LastSendError = ex.GetType().Name;
                return false;
            }
        }

        private bool IsExpectedSource(IPEndPoint remote)
        {
            return _remote != null
                && remote.Port == _remote.Port
                && remote.Address.Equals(_remote.Address);
        }

        private static string EndpointKeyOf(IPEndPoint remote)
        {
            // IPEndPoint.ToString() 对 IPv4 是 "a.b.c.d:port"、对 IPv6 是 "[addr]:port"：规范且唯一。
            return remote != null ? remote.ToString() : string.Empty;
        }

        private static IPAddress ResolveListenAddress(string address)
        {
            if (string.IsNullOrEmpty(address))
            {
                return IPAddress.Any;
            }

            IPAddress parsed;
            if (!IPAddress.TryParse(address, out parsed))
            {
                throw new ArgumentException("listenAddress 必须是 IP 字面量：" + address, "listenAddress");
            }

            return parsed;
        }

        private static IPEndPoint ResolveRemote(string host, int port)
        {
            if (string.IsNullOrEmpty(host))
            {
                throw new ArgumentException("offer.Host 为空", "offer");
            }

            if (port < 1 || port > 65535)
            {
                throw new ArgumentException("offer.Port 越界：" + port, "offer");
            }

            IPAddress address;
            if (!IPAddress.TryParse(host, out address))
            {
                // 首版承诺 IP 字面量；主机名做一次**有界**解析（优先 IPv4）。
                IPAddress[] resolved;
                try
                {
                    resolved = Dns.GetHostAddresses(host);
                }
                catch (Exception ex)
                {
                    throw new ArgumentException("无法解析 DS 主机 " + host + "：" + ex.GetType().Name, "offer");
                }

                address = null;
                for (int i = 0; i < resolved.Length; i++)
                {
                    if (resolved[i].AddressFamily == AddressFamily.InterNetwork)
                    {
                        address = resolved[i];
                        break;
                    }
                }

                if (address == null && resolved.Length > 0)
                {
                    address = resolved[0];
                }

                if (address == null)
                {
                    throw new ArgumentException("无法解析 DS 主机 " + host, "offer");
                }
            }

            return new IPEndPoint(address, port);
        }

        private static Socket CreateSocket(IPAddress bindAddress, int port)
        {
            Socket socket = new Socket(bindAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

            try
            {
                // 非阻塞：Pump 用「有预算的排空循环」收包，绝不阻塞主线程。
                socket.Blocking = false;

                // 不调用 Connect：UDP 每次收发都带地址，Connect 反而会引入 ICMP 引起的伪错误语义。
                socket.Bind(new IPEndPoint(bindAddress, port));
                return socket;
            }
            catch
            {
                try
                {
                    socket.Close();
                }
                catch (Exception)
                {
                    // 原异常更重要。
                }

                throw;
            }
        }

        private void Warn(string message)
        {
            Action<string> handler = _bridge != null ? _bridge.Warn : null;
            if (handler != null)
            {
                handler(message);
            }
        }

        private void RaiseConnected(PMTransportConnection connection)
        {
            Action<PMTransportConnection> handler = Connected;
            if (handler != null)
            {
                handler(connection);
            }
        }

        private void RaiseFailed(string message)
        {
            Action<string> handler = Failed;
            if (handler != null)
            {
                handler(message);
            }
        }

        public override string ToString()
        {
            return "PMUdpSessionEndpoint(" + (_server ? "server" : "client")
                + " port=" + _boundPort + " conns=" + ConnectionCount
                + " tx=" + DatagramsSent + " rx=" + DatagramsReceived + ")";
        }
    }
}
