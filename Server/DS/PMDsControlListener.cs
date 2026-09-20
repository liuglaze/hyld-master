using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace PMNet.Control
{
    /// <summary>入站连接生命周期事件（只由监听线程投递，宿主单线程消费）。</summary>
    public enum PMDsControlConnectionEventKind : byte
    {
        /// <summary>已接受一条新的 loopback 连接（尚未绑定任何会话）。</summary>
        Opened = 1,

        /// <summary>连接已关闭（对端关闭 / 读写错误 / 分帧超限 / 被宿主主动关闭）。</summary>
        Closed = 2,
    }

    /// <summary>连接生命周期事件。</summary>
    public struct PMDsControlConnectionEvent
    {
        /// <summary>连接 ID（监听器内单调递增，不复用）。</summary>
        public int ConnectionId;

        /// <summary>事件类型。</summary>
        public PMDsControlConnectionEventKind Kind;

        /// <summary>远端端点（诊断用；首版恒为 loopback）。</summary>
        public string RemoteEndpoint;

        /// <summary>关闭/拒绝原因（不含秘密）。</summary>
        public string Reason;
    }

    /// <summary>一条已解帧的控制帧载荷（**不含** 4 字节长度前缀）。</summary>
    public struct PMDsControlInbound
    {
        /// <summary>来源连接 ID。</summary>
        public int ConnectionId;

        /// <summary>载荷字节（每次投递都是独立副本，宿主可安全入队/持有）。</summary>
        public byte[] Payload;

        /// <summary>载荷长度（Payload 可能大于它）。</summary>
        public int Length;

        /// <summary>到达时刻（UTC 毫秒，监听器本地时钟，仅用于诊断/限流）。</summary>
        public long ReceivedAtUnixMilliseconds;
    }

    /// <summary>
    /// Lobby 侧的控制通道 TCP 监听器（R3-B，契约 §3「首版 TCP 仅 loopback」+ §7.3）。
    ///
    /// ## 职责边界（刻意收得很窄）
    /// - **只**绑定 loopback（<c>127.0.0.1</c> / <c>::1</c>）；给一个非 loopback 地址直接拒绝启动。
    /// - **只**做三件事：accept 连接 → 按 4 字节 LE 分帧解包 → 把载荷**入队**。
    ///   接收 worker **不做**验签、不做会话查找、不改任何业务状态 —— 那是
    ///   <see cref="PMDsLobbyHost"/> 在唯一一条宿主线程上的事。
    ///   这样「socket 线程」与「coordinator 线程」被队列隔开，协调器状态只在一个线程里变化（契约 §7.3）。
    /// - 出站只提供「按连接 ID 投递一帧」的入队能力，实际发送由该连接自己的工作线程完成，
    ///   宿主**不会**因为对端不读而阻塞。
    ///
    /// ## 有界性（契约 §7.2「有界连接队列」「socket 每 Pump 收包有预算」）
    /// - 连接数上限：超过即**接受后立即关闭**并计数（不给对端无限挂起的机会）。
    /// - 入站队列：按条数与字节数双上限；超限时**关闭该连接**（显式失败），不静默丢帧。
    /// - 出站队列：按条数与字节数双上限；超限时返回 false，由宿主决定是否断开。
    /// - 单帧载荷上限沿用 <see cref="PMDsControlWire.MaxFramePayloadBytes"/>（64KiB）；
    ///   长度前缀声明超限时**立即**断开（不给对端免费的内存放大器）。
    ///
    /// ## 与 R3-A1 的关系
    /// 分帧/解码直接用共享的 <see cref="PMDsControlFrameDecoder"/> 与 <see cref="PMDsControlFraming"/>，
    /// 不在这里另写一套解析（否则就是「测试/实现各一份」的自欺）。
    /// </summary>
    public sealed class PMDsControlListener : IDisposable
    {
        /// <summary>默认最大并发连接数（契约名册上限 6，留出重连余量）。</summary>
        public const int DefaultMaxConnections = 16;

        /// <summary>默认入站队列条数上限。</summary>
        public const int DefaultMaxQueuedInboundFrames = 4096;

        /// <summary>默认入站队列字节上限（4 MiB）。</summary>
        public const int DefaultMaxQueuedInboundBytes = 4 * 1024 * 1024;

        /// <summary>默认出站队列条数上限。</summary>
        public const int DefaultMaxQueuedOutboundFrames = 256;

        /// <summary>默认出站队列字节上限（1 MiB）。</summary>
        public const int DefaultMaxQueuedOutboundBytes = 1024 * 1024;

        /// <summary>连接工作线程单次 Poll 的等待（微秒）：足够短以保证关闭及时，足够长以避免空转。</summary>
        private const int PollWaitMicroseconds = 20000;

        private readonly object _gate = new object();
        private readonly Dictionary<int, Connection> _connections = new Dictionary<int, Connection>();
        private readonly Queue<PMDsControlInbound> _inbound = new Queue<PMDsControlInbound>();
        private readonly Queue<PMDsControlConnectionEvent> _events = new Queue<PMDsControlConnectionEvent>();

        private readonly string _listenAddress;
        private readonly int _requestedPort;
        private readonly int _maxConnections;
        private readonly int _maxQueuedInboundFrames;
        private readonly int _maxQueuedInboundBytes;
        private readonly int _maxQueuedOutboundFrames;
        private readonly int _maxQueuedOutboundBytes;

        private TcpListener _listener;
        private Thread _acceptThread;
        private bool _started;
        private bool _disposed;
        private int _nextConnectionId;
        private long _queuedInboundBytes;

        /// <summary>实际绑定的端口（<c>Start</c> 之后有效；请求 0 时是系统分配的端口）。</summary>
        public int BoundPort { get; private set; }

        /// <summary>实际绑定的地址。</summary>
        public string BoundAddress { get; private set; }

        /// <summary>当前存活连接数。</summary>
        public int ConnectionCount
        {
            get
            {
                lock (_gate)
                {
                    return _connections.Count;
                }
            }
        }

        /// <summary>入站队列中待处理载荷条数（宿主每 Pump 有预算地消费）。</summary>
        public int QueuedInboundCount
        {
            get
            {
                lock (_gate)
                {
                    return _inbound.Count;
                }
            }
        }

        /// <summary>入站队列当前占用字节数。</summary>
        public long QueuedInboundBytes
        {
            get
            {
                lock (_gate)
                {
                    return _queuedInboundBytes;
                }
            }
        }

        // ── 计数（诊断/门禁证据） ────────────────────────────────────────
        /// <summary>接受过的连接总数。</summary>
        public long AcceptedConnections;

        /// <summary>因超过连接上限而被接受后立即关闭的连接数。</summary>
        public long RejectedOverConnectionLimit;

        /// <summary>因入站队列超限被关闭的连接数（显式失败，非静默丢帧）。</summary>
        public long RejectedOverInboundQueue;

        /// <summary>因分帧超限/解码故障被关闭的连接数。</summary>
        public long RejectedFramingFault;

        /// <summary>成功投递给宿主的载荷条数。</summary>
        public long DeliveredInboundFrames;

        /// <summary>成功入队等待发送的出站帧数。</summary>
        public long QueuedOutboundFrames;

        /// <summary>因出站队列超限被拒绝的发送次数。</summary>
        public long RejectedOutboundQueueFull;

        /// <summary>观测到的入站队列字节峰值（用于证明「有界」不是空话）。</summary>
        public long PeakQueuedInboundBytes;

        public PMDsControlListener(string listenAddress, int port)
            : this(listenAddress, port, DefaultMaxConnections, DefaultMaxQueuedInboundFrames,
                DefaultMaxQueuedInboundBytes, DefaultMaxQueuedOutboundFrames, DefaultMaxQueuedOutboundBytes)
        {
        }

        public PMDsControlListener(string listenAddress, int port, int maxConnections,
            int maxQueuedInboundFrames, int maxQueuedInboundBytes,
            int maxQueuedOutboundFrames, int maxQueuedOutboundBytes)
        {
            _listenAddress = string.IsNullOrEmpty(listenAddress) ? "127.0.0.1" : listenAddress;
            _requestedPort = port;
            _maxConnections = maxConnections <= 0 ? DefaultMaxConnections : maxConnections;
            _maxQueuedInboundFrames = maxQueuedInboundFrames <= 0
                ? DefaultMaxQueuedInboundFrames : maxQueuedInboundFrames;
            _maxQueuedInboundBytes = maxQueuedInboundBytes <= 0
                ? DefaultMaxQueuedInboundBytes : maxQueuedInboundBytes;
            _maxQueuedOutboundFrames = maxQueuedOutboundFrames <= 0
                ? DefaultMaxQueuedOutboundFrames : maxQueuedOutboundFrames;
            _maxQueuedOutboundBytes = maxQueuedOutboundBytes <= 0
                ? DefaultMaxQueuedOutboundBytes : maxQueuedOutboundBytes;
        }

        /// <summary>
        /// 启动监听。只接受 loopback 地址；非 loopback 或端口非法时返回 false 并给出原因。
        /// </summary>
        public bool Start(out string error)
        {
            error = null;
            if (_started)
            {
                error = "监听器已启动";
                return false;
            }

            IPAddress address;
            if (!TryResolveLoopback(_listenAddress, out address))
            {
                error = "控制通道首版只允许 loopback 地址，收到：" + _listenAddress;
                return false;
            }

            if (_requestedPort < 0 || _requestedPort > 65535)
            {
                error = "端口非法：" + _requestedPort;
                return false;
            }

            try
            {
                _listener = new TcpListener(address, _requestedPort);
                _listener.Start(16);
                IPEndPoint endpoint = (IPEndPoint)_listener.LocalEndpoint;
                BoundPort = endpoint.Port;
                BoundAddress = endpoint.Address.ToString();
            }
            catch (Exception ex)
            {
                error = "控制监听启动失败：" + ex.GetType().Name + " " + ex.Message;
                SafeStopListener();
                return false;
            }

            _started = true;
            _acceptThread = new Thread(AcceptLoop);
            _acceptThread.IsBackground = true;
            _acceptThread.Name = "PMDsControlAccept";
            _acceptThread.Start();
            return true;
        }

        /// <summary>是否有待宿主处理的生命周期事件。</summary>
        public bool TryDequeueConnectionEvent(out PMDsControlConnectionEvent evt)
        {
            lock (_gate)
            {
                if (_events.Count == 0)
                {
                    evt = default(PMDsControlConnectionEvent);
                    return false;
                }

                evt = _events.Dequeue();
                return true;
            }
        }

        /// <summary>取出一条待处理载荷（宿主唯一入口）。</summary>
        public bool TryDequeueInbound(out PMDsControlInbound inbound)
        {
            lock (_gate)
            {
                if (_inbound.Count == 0)
                {
                    inbound = default(PMDsControlInbound);
                    return false;
                }

                inbound = _inbound.Dequeue();
                _queuedInboundBytes -= inbound.Length;
                if (_queuedInboundBytes < 0)
                {
                    _queuedInboundBytes = 0;
                }

                DeliveredInboundFrames++;
                return true;
            }
        }

        /// <summary>
        /// 向指定连接入队一帧（自动补 4 字节长度前缀）。返回 false 表示连接不存在或出站队列已满
        /// —— 宿主应据此计数并考虑断开，**不要**在此重试阻塞。
        /// </summary>
        public bool TrySendFrame(int connectionId, byte[] payload, int offset, int count)
        {
            if (payload == null)
            {
                return false;
            }

            byte[] frame;
            try
            {
                frame = PMDsControlFraming.Frame(payload, offset, count);
            }
            catch (Exception)
            {
                // 载荷超限/范围非法：这是宿主侧编程错误，显式失败而不是截断。
                return false;
            }

            return TrySendRawFrame(connectionId, frame);
        }

        /// <summary>向指定连接入队一条**已带长度前缀**的帧字节。</summary>
        public bool TrySendRawFrame(int connectionId, byte[] frame)
        {
            if (frame == null)
            {
                return false;
            }

            Connection connection;
            lock (_gate)
            {
                if (!_connections.TryGetValue(connectionId, out connection))
                {
                    return false;
                }

                if (connection.Outbound.Count >= _maxQueuedOutboundFrames
                    || connection.OutboundBytes + frame.Length > _maxQueuedOutboundBytes)
                {
                    RejectedOutboundQueueFull++;
                    return false;
                }

                connection.Outbound.Enqueue(frame);
                connection.OutboundBytes += frame.Length;
                QueuedOutboundFrames++;
                Monitor.PulseAll(_gate);
                return true;
            }
        }

        /// <summary>关闭一条连接（幂等）。宿主用于「首个控制消息未通过身份/MAC 校验」等拒绝路径。</summary>
        public void CloseConnection(int connectionId, string reason)
        {
            Connection connection;
            lock (_gate)
            {
                if (!_connections.TryGetValue(connectionId, out connection))
                {
                    return;
                }
            }

            connection.Close(reason);
        }

        /// <summary>取连接的远端端点（诊断用）。</summary>
        public bool TryGetRemoteEndpoint(int connectionId, out string remoteEndpoint)
        {
            lock (_gate)
            {
                Connection connection;
                if (_connections.TryGetValue(connectionId, out connection))
                {
                    remoteEndpoint = connection.RemoteEndpoint;
                    return true;
                }
            }

            remoteEndpoint = null;
            return false;
        }

        /// <summary>停止监听并关闭所有连接（幂等）。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _started = false;
            SafeStopListener();

            List<Connection> snapshot;
            lock (_gate)
            {
                snapshot = new List<Connection>(_connections.Values);
            }

            for (int i = 0; i < snapshot.Count; i++)
            {
                snapshot[i].Close("监听器关闭");
            }

            Thread accept = _acceptThread;
            if (accept != null)
            {
                try
                {
                    accept.Join(1000);
                }
                catch (Exception)
                {
                }

                _acceptThread = null;
            }

            lock (_gate)
            {
                _connections.Clear();
                _inbound.Clear();
                _queuedInboundBytes = 0;
            }
        }

        // ────────────────────────────────────────────────────────────────
        // 内部：accept 与连接工作线程
        // ────────────────────────────────────────────────────────────────

        private void SafeStopListener()
        {
            try
            {
                if (_listener != null)
                {
                    _listener.Stop();
                }
            }
            catch (Exception)
            {
            }

            _listener = null;
        }

        private void AcceptLoop()
        {
            while (_started)
            {
                TcpClient client = null;
                try
                {
                    TcpListener listener = _listener;
                    if (listener == null)
                    {
                        return;
                    }

                    client = listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    if (!_started)
                    {
                        return;
                    }

                    // accept 失败（含 listener.Stop()）不改变已建立的会话状态；短暂退避避免忙等。
                    Thread.Sleep(10);
                    continue;
                }

                if (client == null)
                {
                    continue;
                }

                Accept(client);
            }
        }

        private void Accept(TcpClient client)
        {
            int connectionId;
            Connection connection;

            lock (_gate)
            {
                if (_connections.Count >= _maxConnections)
                {
                    RejectedOverConnectionLimit++;
                    // 已接受但超限：**立即关闭**并记事件，绝不把连接挂在半开状态。
                    _events.Enqueue(new PMDsControlConnectionEvent
                    {
                        ConnectionId = 0,
                        Kind = PMDsControlConnectionEventKind.Closed,
                        RemoteEndpoint = SafeRemote(client),
                        Reason = "超过连接上限 " + _maxConnections + "，已拒绝",
                    });
                    CloseQuietly(client);
                    return;
                }

                AcceptedConnections++;
                connectionId = ++_nextConnectionId;
                connection = new Connection(this, connectionId, client);
                _connections[connectionId] = connection;
                _events.Enqueue(new PMDsControlConnectionEvent
                {
                    ConnectionId = connectionId,
                    Kind = PMDsControlConnectionEventKind.Opened,
                    RemoteEndpoint = connection.RemoteEndpoint,
                    Reason = null,
                });
            }

            connection.StartWorker();
        }

        private static string SafeRemote(TcpClient client)
        {
            try
            {
                return client.Client != null && client.Client.RemoteEndPoint != null
                    ? client.Client.RemoteEndPoint.ToString()
                    : "unknown";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        private static void CloseQuietly(TcpClient client)
        {
            try
            {
                client.Close();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>把一条已解帧载荷投递到有界入站队列；超限返回 false（调用方据此断开连接）。</summary>
        private bool EnqueueInbound(int connectionId, byte[] payload, int length)
        {
            lock (_gate)
            {
                if (_inbound.Count >= _maxQueuedInboundFrames
                    || _queuedInboundBytes + length > _maxQueuedInboundBytes)
                {
                    RejectedOverInboundQueue++;
                    return false;
                }

                PMDsControlInbound inbound = new PMDsControlInbound();
                inbound.ConnectionId = connectionId;
                inbound.Payload = payload;
                inbound.Length = length;
                inbound.ReceivedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _inbound.Enqueue(inbound);
                _queuedInboundBytes += length;
                if (_queuedInboundBytes > PeakQueuedInboundBytes)
                {
                    PeakQueuedInboundBytes = _queuedInboundBytes;
                }

                return true;
            }
        }

        private void RemoveConnection(Connection connection, string reason)
        {
            lock (_gate)
            {
                Connection existing;
                if (!_connections.TryGetValue(connection.ConnectionId, out existing)
                    || !ReferenceEquals(existing, connection))
                {
                    return;
                }

                _connections.Remove(connection.ConnectionId);
                _events.Enqueue(new PMDsControlConnectionEvent
                {
                    ConnectionId = connection.ConnectionId,
                    Kind = PMDsControlConnectionEventKind.Closed,
                    RemoteEndpoint = connection.RemoteEndpoint,
                    Reason = reason,
                });
            }
        }

        private static bool TryResolveLoopback(string text, out IPAddress address)
        {
            address = null;
            if (string.IsNullOrEmpty(text))
            {
                address = IPAddress.Loopback;
                return true;
            }

            if (string.Equals(text, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                address = IPAddress.Loopback;
                return true;
            }

            IPAddress parsed;
            if (!IPAddress.TryParse(text, out parsed))
            {
                return false;
            }

            if (!IPAddress.IsLoopback(parsed))
            {
                return false;
            }

            address = parsed;
            return true;
        }

        /// <summary>
        /// 一条连接的收/发工作线程状态。工作线程**只**做：读字节 → 解帧 → 入队；以及把出站队列写回 socket。
        /// 它不解析业务、不验签、不触碰任何会话状态。
        /// </summary>
        private sealed class Connection
        {
            private readonly PMDsControlListener _owner;
            private readonly TcpClient _client;
            private readonly Socket _socket;
            private readonly PMDsControlFrameDecoder _decoder;
            private readonly byte[] _readBuffer = new byte[4096];
            private readonly object _gate = new object();
            private Thread _worker;
            private volatile bool _closed;

            public readonly int ConnectionId;
            public readonly string RemoteEndpoint;
            public readonly Queue<byte[]> Outbound = new Queue<byte[]>();
            public long OutboundBytes;

            public Connection(PMDsControlListener owner, int connectionId, TcpClient client)
            {
                _owner = owner;
                _client = client;
                ConnectionId = connectionId;
                RemoteEndpoint = SafeRemote(client);
                _decoder = new PMDsControlFrameDecoder();
                _socket = client.Client;
                try
                {
                    _socket.NoDelay = true;
                }
                catch (Exception)
                {
                }
            }

            public void StartWorker()
            {
                _worker = new Thread(Run);
                _worker.IsBackground = true;
                _worker.Name = "PMDsControlConn-" + ConnectionId;
                _worker.Start();
            }

            private void Run()
            {
                string closeReason = null;
                try
                {
                    while (!_closed)
                    {
                        if (_socket.Poll(PollWaitMicroseconds, SelectMode.SelectRead))
                        {
                            if (!PumpRead())
                            {
                                closeReason = "对端关闭连接";
                                break;
                            }
                        }

                        if (!PumpWrite())
                        {
                            closeReason = "写失败";
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    closeReason = "连接工作线程异常：" + ex.GetType().Name;
                }

                CloseInternal(closeReason == null ? "连接结束" : closeReason);
            }

            /// <summary>返回 false 表示对端已关闭/读失败/分帧故障，连接应结束。</summary>
            private bool PumpRead()
            {
                int read;
                try
                {
                    read = _socket.Receive(_readBuffer, 0, _readBuffer.Length, SocketFlags.None);
                }
                catch (SocketException)
                {
                    return false;
                }

                if (read <= 0)
                {
                    return false;
                }

                try
                {
                    _decoder.Append(_readBuffer, 0, read);
                }
                catch (PMDsControlProtocolException)
                {
                    // 长度前缀超限/零长：**立即**断开，不等对端把 64KiB+ 发完。
                    _owner.RejectedFramingFault++;
                    return false;
                }

                while (true)
                {
                    byte[] payload;
                    if (!_decoder.TryDequeue(out payload))
                    {
                        break;
                    }

                    if (!_owner.EnqueueInbound(ConnectionId, payload, payload.Length))
                    {
                        // 入站队列超限：显式失败（断开），不静默丢帧。
                        return false;
                    }
                }

                return true;
            }

            /// <summary>返回 false 表示写失败，连接应结束。</summary>
            private bool PumpWrite()
            {
                while (true)
                {
                    byte[] frame = null;
                    lock (_gate)
                    {
                        if (_closed)
                        {
                            return true;
                        }

                        if (Outbound.Count > 0)
                        {
                            frame = Outbound.Dequeue();
                            OutboundBytes -= frame.Length;
                            if (OutboundBytes < 0)
                            {
                                OutboundBytes = 0;
                            }
                        }
                    }

                    if (frame == null)
                    {
                        return true;
                    }

                    try
                    {
                        int sent = 0;
                        while (sent < frame.Length)
                        {
                            sent += _socket.Send(frame, sent, frame.Length - sent, SocketFlags.None);
                        }
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                }
            }

            public void Close(string reason)
            {
                if (_closed)
                {
                    return;
                }

                lock (_gate)
                {
                    if (_closed)
                    {
                        return;
                    }

                    _closed = true;
                }

                CloseInternal(reason);
            }

            private void CloseInternal(string reason)
            {
                lock (_gate)
                {
                    _closed = true;
                }

                try
                {
                    _client.Close();
                }
                catch (Exception)
                {
                }

                lock (_gate)
                {
                    Outbound.Clear();
                    OutboundBytes = 0;
                }

                _owner.RemoveConnection(this, reason);
            }
        }
    }
}
