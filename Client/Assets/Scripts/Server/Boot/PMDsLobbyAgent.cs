// R3-B：DS 侧的 Lobby 控制通道代理（契约 Docs/plans/net-r3-control-contract.md §3 / §7.3 / §7.4）。
//
// 职责（只做控制面，不碰局内玩法）：
//   - 用**引导文件里的同一把密钥**与 Lobby 的控制 listener 建立 loopback TCP 连接；
//   - 4 字节 LE 分帧（支持半包/粘包/超限即拒）→ 解码 → **验 MAC**；
//   - 上行 Ready / Heartbeat / Result / Exited / Error；下行只接受 ResultAck / Shutdown / Error；
//   - 结果以 (MatchId, Epoch, ResultId) 幂等：同 ID 重发直到收到**匹配的 ResultAck** 才请求退出；
//   - 超时（连接/就绪/结果确认）一律**明确失败退出**，绝不假装确认成功。
//
// 线程纪律（本类最重要的约束）：
//   - 后台接收线程**只做**「Receive → 入有界队列」，绝不解析、绝不验签、绝不改状态、绝不打日志；
//   - 全部解码/验签/状态推进都发生在宿主主线程的 <see cref="Pump"/> 里；
//   - 因此本类可以在 Unity 主线程上安全使用，也可以在纯 C# 门禁里直接跑。
//
// 语言面：纯 C#（C# 7.3 / netstandard2.0）。**不得**引用 UnityEngine ——
// 退出请求经 <see cref="ExitRequested"/> 回调交给宿主（Unity 侧接 Application.Quit）。
// 日志同理走 <see cref="Log"/> / <see cref="Warn"/>，不由本类直接输出。

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using PMNet.Control;

namespace PMNet.Unity
{
    /// <summary>
    /// DS 侧控制通道代理（契约 §7.3 的「DS 侧 LobbyAgent」）。
    ///
    /// 生命周期：宿主构造 → <see cref="Start"/> → 每帧 <see cref="Pump"/> → <see cref="Dispose"/>。
    /// 退出只能经 <see cref="ExitRequested"/> 请求宿主执行，本类自己不结束进程。
    /// </summary>
    public sealed class PMDsLobbyAgent : IDisposable
    {
        /// <summary>控制通道默认端口（与 <see cref="PMDsControlWire.DefaultControlPort"/> 同源，不另立一份）。</summary>
        public const int DefaultControlPort = PMDsControlWire.DefaultControlPort;

        /// <summary>TCP 连接超时（loopback 实测瞬时，这里只是不无限等）。</summary>
        public const int ConnectTimeoutMs = 5000;

        /// <summary>就绪期限：连接成功之后这么久仍未发出 Ready（sceneReady/端口未就绪）即明确失败。</summary>
        public const int StartupReadyTimeoutMs = 30000;

        /// <summary>Ready 重发间隔（在收到 Lobby 任何回应之前周期性重发；Lobby 侧重复 Ready 幂等）。</summary>
        public const int ReadyRetransmitMs = 1000;

        /// <summary>
        /// 心跳**发送间隔**：5 秒。
        ///
        /// 为什么是 5 秒而不是契约 §3 里那个 15 秒：15 秒那个值是 Lobby 侧的**心跳超时**
        /// （<c>PMDsCoordinatorOptions.HeartbeatTimeout</c>）。发送间隔与超时相等是 1:1 的竞态 ——
        /// 本机心跳由宿主帧驱动（「首个 now >= 本次到期时刻的帧」才发，可晚一帧），
        /// 于是 Lobby 的下一次 Tick 完全可能落在「已到期、心跳还没到」的窗口里，
        /// 把一个**健康**会话判成心跳超时。改成 3:1 余量后，连续丢两拍也不会误判。
        /// 本值只影响**发送节奏**，不改变任何超时语义。
        /// </summary>
        public const int HeartbeatIntervalMs = 5000;

        /// <summary>
        /// **运行期** liveness 超时：15 秒（= 3 × <see cref="HeartbeatIntervalMs"/>）。
        ///
        /// 判据只有一条：**距最后一次收到 Lobby 可信控制帧的时间**（见 <see cref="LastTrustedReceiveMs"/>）。
        /// 持续上行发送成功（<see cref="FramesSent"/> 增长、<c>Send</c> 返回正数）**不代表** Lobby 存活：
        /// TCP 发送成功只证明本机写进了内核缓冲，对端死活完全未知。
        ///
        /// 它只用于「已经收到过至少一帧可信下行」之后的运行期；
        /// 「首个 Ready 响应」这一段仍由 <see cref="StartupReadyTimeoutMs"/> 负责。
        /// </summary>
        public const int RuntimeLivenessTimeoutMs = 15000;

        /// <summary>结果确认重试间隔（契约 §3：结果确认重试 1 秒）。</summary>
        public const int ResultRetransmitMs = 1000;

        /// <summary>结果确认总期限；超过即明确失败退出，不假装确认成功（契约 §3 的收尾口径）。</summary>
        public const int ResultAckTimeoutMs = 30000;

        /// <summary>后台入站队列上限（有界：对端乱写不能把内存撑爆）。</summary>
        public const int MaxInboundChunks = 512;

        /// <summary>单次接收的字节数。</summary>
        public const int ReceiveChunkBytes = 8192;

        /// <summary>单次 Pump 最多消费的入站块数（把洪泛摊到多帧）。</summary>
        public const int MaxChunksPerPump = 64;

        private readonly PMDsBootstrappedMatch _boot;
        private readonly PMDsControlSigner _signer;
        private readonly string _host;
        private readonly int _port;
        private readonly int _boundPort;
        private readonly uint _collisionDigest;
        private readonly PMDsControlFrameDecoder _decoder = new PMDsControlFrameDecoder();

        // ---- 后台线程只碰这几样 ----
        private readonly ConcurrentQueue<byte[]> _inbound = new ConcurrentQueue<byte[]>();
        private Socket _socket;
        private Thread _receiveThread;
        private volatile bool _stopRequested;
        private int _peerClosed;
        private long _chunksDropped;

        // ---- 以下只在主线程访问 ----
        private bool _started;
        private bool _disposed;
        private long _startedAtMs;
        private long _lastPumpMs;
        private long _nextReadyAtMs;
        private long _nextHeartbeatAtMs;
        private bool _readySent;
        private long _readySentAtMs;

        /// <summary>
        /// 最后一次收到**可信**（MAC + 局/epoch/hash 全等）Lobby 控制帧的时刻；0 = 尚未收到过。
        /// 只有它续命运行期 liveness；坏 MAC / 错身份 / 被拒状态的帧一律不刷新。
        /// </summary>
        private long _lastTrustedRxMs;

        private bool _hasResult;
        private ulong _resultId;
        private int _resultWinnerTeamId;
        private byte[] _resultSummary;
        private bool _resultAcknowledged;
        private long _nextResultSendMs;
        private long _resultDeadlineMs;

        private bool _faulted;
        private string _faultReason;
        private int _exitCode;
        private bool _exitRequested;
        private bool _exitedSent;

        /// <summary>诊断/正常日志出口（宿主接项目日志；null = 丢弃）。</summary>
        public Action<string> Log;

        /// <summary>告警出口（null = 丢弃）。</summary>
        public Action<string> Warn;

        /// <summary>
        /// 退出请求（**只会被调用一次**）：宿主应据此执行 `Application.Quit(code)`。
        /// 本类刻意不直接结束进程：纯 C# 层不该依赖 Unity 的退出 API，
        /// 也便于门禁断言「失败路径确实请求了非 0 退出」。
        /// </summary>
        public Action<int> ExitRequested;

        /// <summary>DS 上报的名册人数（心跳字段；null 时按 0 上报）。</summary>
        public Func<int> PlayerCountProvider;

        /// <summary>
        /// 真实就绪标志：**只有碰撞场景已实际建立且校验通过才允许为 true**（契约 §3 / §7.4）。
        /// 为 false 时本类不会发出任何 Ready，超期即明确失败。
        /// </summary>
        public bool SceneReady;

        public PMDsLobbyAgent(PMDsBootstrappedMatch boot, string controlHost, int controlPort,
                              int boundPort, uint collisionDigest)
        {
            if (boot == null) { throw new ArgumentNullException("boot"); }
            if (boot.Key == null) { throw new ArgumentException("引导文件缺少控制密钥", "boot"); }
            if (string.IsNullOrEmpty(boot.MatchId)) { throw new ArgumentException("引导文件缺少 MatchId", "boot"); }
            if (string.IsNullOrEmpty(boot.DsId)) { throw new ArgumentException("引导文件缺少 DsId", "boot"); }
            if (boot.Epoch == 0u) { throw new ArgumentException("引导文件 Epoch 不得为 0", "boot"); }
            if (boot.ProtocolHash == 0u) { throw new ArgumentException("引导文件 ProtocolHash 不得为 0", "boot"); }
            if (!IsLoopbackHost(controlHost))
            {
                // 契约 §3：首版控制通道**只允许 loopback**。远端控制面不在本轮范围内，
                // 而且「把本局密钥/票据交给一个非本机对端」是明确的越界。
                throw new ArgumentException("控制通道首版仅允许 loopback 地址，收到 '" + (controlHost ?? "<null>") + "'", "controlHost");
            }

            if (controlPort <= 0 || controlPort > 65535)
            {
                throw new ArgumentOutOfRangeException("controlPort", "控制端口必须在 1..65535（实际 " + controlPort + "）");
            }

            if (boundPort <= 0 || boundPort > 65535)
            {
                throw new ArgumentOutOfRangeException("boundPort", "本局实际监听端口必须在 1..65535（实际 " + boundPort + "）");
            }

            _boot = boot;
            _signer = boot.Key.CreateControlSigner();
            _host = controlHost;
            _port = controlPort;
            _boundPort = boundPort;
            _collisionDigest = collisionDigest;
            _resultId = ((ulong)boot.Epoch << 32) | 1UL;
        }

        /// <summary>是否已连接（TCP 已建立）。</summary>
        public bool IsConnected { get { return _socket != null && _socket.Connected; } }

        /// <summary>是否已发出过 Ready。</summary>
        public bool ReadySent { get { return _readySent; } }

        /// <summary>是否已进入明确失败态。</summary>
        public bool IsFaulted { get { return _faulted; } }

        /// <summary>失败原因（<see cref="IsFaulted"/> 为真时非空）。</summary>
        public string FaultReason { get { return _faultReason; } }

        /// <summary>是否已提交结果。</summary>
        public bool HasResult { get { return _hasResult; } }

        /// <summary>已提交结果的 ResultId（未提交时为 0）。</summary>
        public ulong ResultId { get { return _hasResult ? _resultId : 0UL; } }

        /// <summary>是否收到了**匹配的** ResultAck（只有它为真才允许请求正常退出）。</summary>
        public bool ResultAcknowledged { get { return _resultAcknowledged; } }

        /// <summary>实际请求的退出码。</summary>
        public int ExitCode { get { return _exitCode; } }

        /// <summary>控制通道端点（诊断用，不含任何秘密）。</summary>
        public string ControlEndpoint
        {
            get { return _host + ":" + _port.ToString(CultureInfo.InvariantCulture); }
        }

        /// <summary>统计：成功发出的控制帧数。</summary>
        public long FramesSent;

        /// <summary>统计：成功验签并受理的控制帧数。</summary>
        public long FramesReceived;

        /// <summary>统计：被拒绝的控制帧数（MAC 校验通过但身份/方向不合格）。</summary>
        public long FramesRejected;

        /// <summary>
        /// 统计：MAC 校验失败的帧数。
        ///
        /// 刻意与 <see cref="FramesRejected"/> 分开：「验签就过不了」与「验签过了但身份/方向不对」
        /// 是两类不同的故障（后者意味着对端用对了密钥、却说错了局），分开记才有诊断价值。
        /// </summary>
        public long MacFailures;

        /// <summary>统计：因队列满而丢弃的接收块数（观测有界性）。</summary>
        public long ChunksDropped { get { return Interlocked.Read(ref _chunksDropped); } }

        /// <summary>统计：已重发的 Result 次数（同 ResultId）。</summary>
        public long ResultSends;

        /// <summary>
        /// 统计：**下行**心跳（Lobby → DS）被受理的次数。
        ///
        /// 刻意与 <see cref="FramesSent"/> / 心跳**上行**分开：「本机发了多少心跳」与「对端是否还活着」
        /// 是两件不同的事，混在一个计数里正是旧看门狗误判的温床。
        /// </summary>
        public long HeartbeatsReceived;

        /// <summary>最后一次收到可信 Lobby 控制帧的时刻（UTC 毫秒；0 = 尚未收到过）。</summary>
        public long LastTrustedReceiveMs { get { return _lastTrustedRxMs; } }

        // =================================================================================
        //  启动 / 关闭
        // =================================================================================

        /// <summary>
        /// 建立控制连接并启动后台接收线程。失败返回 false 并写 <paramref name="error"/>（不抛）。
        /// </summary>
        public bool Start(out string error)
        {
            error = null;

            if (_disposed) { error = "代理已释放"; return false; }
            if (_started) { return true; }

            try
            {
                IPAddress address = ResolveLoopback(_host);
                Socket socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                socket.NoDelay = true;

                IAsyncResult ar = socket.BeginConnect(new IPEndPoint(address, _port), null, null);
                if (!ar.AsyncWaitHandle.WaitOne(ConnectTimeoutMs))
                {
                    SafeClose(socket);
                    error = "连接控制 listener 超时（" + ConnectTimeoutMs + "ms）：" + ControlEndpoint;
                    return false;
                }

                socket.EndConnect(ar);
                _socket = socket;
                _started = true;
                _startedAtMs = 0L;
                _nextReadyAtMs = 0L;
                _nextHeartbeatAtMs = HeartbeatIntervalMs;

                _receiveThread = new Thread(ReceiveLoop);
                _receiveThread.IsBackground = true;
                _receiveThread.Name = "PMDsLobbyAgent";
                _receiveThread.Start();

                Info("控制通道已连接：" + ControlEndpoint + "（密钥指纹 " + _signer.KeyFingerprint + "）");
                return true;
            }
            catch (Exception ex)
            {
                SafeClose(_socket);
                _socket = null;
                error = "连接控制 listener 失败：" + ex.GetType().Name + " " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 主线程帧步。次序固定：先排空并处理入站，再按计划上行 Ready/Heartbeat/Result。
        /// **必须在任何可能触发发送的复制/RPC 处理之后仍然只被调用一次**（宿主唯一驱动点）。
        /// </summary>
        public void Pump(long nowMs, long nowUnixSeconds)
        {
            if (_disposed || _faulted) { return; }

            _lastPumpMs = nowMs;
            if (_startedAtMs == 0L) { _startedAtMs = nowMs; }

            DrainInbound();

            if (_faulted) { return; }

            if (Interlocked.CompareExchange(ref _peerClosed, 0, 0) != 0)
            {
                Fail("控制通道被对端关闭（Lobby 进程可能已退出）");
                return;
            }

            if (_decoder.IsFaulted)
            {
                Fail("控制分帧解码进入故障态：" + _decoder.Fault);
                return;
            }

            if (!_readySent)
            {
                if (SceneReady)
                {
                    if (!SendReady(nowMs, false)) { return; }

                    _readySent = true;
                    _readySentAtMs = nowMs;
                    _nextReadyAtMs = nowMs + ReadyRetransmitMs;
                    _nextHeartbeatAtMs = nowMs + HeartbeatIntervalMs;
                }
                else if (nowMs - _startedAtMs >= StartupReadyTimeoutMs)
                {
                    Fail("碰撞场景在 " + StartupReadyTimeoutMs + "ms 内未就绪，拒绝上报就绪");
                    return;
                }
            }
            else if (_lastTrustedRxMs == 0L)
            {
                // —— 阶段 1：等待**首个** Ready 响应 ——
                // 只有这一段用 StartupReadyTimeoutMs；它的语义被刻意收窄成「首个响应等待」，
                // 不再兼任运行期看门狗（旧实现把它复用到运行期，导致长局必被误判失败）。
                if (nowMs - _readySentAtMs >= StartupReadyTimeoutMs)
                {
                    // 已发 Ready 但 Lobby 一直没有回过任何一帧可信响应：控制面事实上不可用，明确失败。
                    Fail("发出 Ready 后 " + StartupReadyTimeoutMs + "ms 内未收到 Lobby 任何可信回应");
                    return;
                }

                if (nowMs >= _nextReadyAtMs)
                {
                    // 幂等重发：Lobby 侧对重复 Ready 是幂等的（A1 已钉住这条语义）。
                    SendReady(nowMs, true);
                }
            }
            else if (nowMs - _lastTrustedRxMs >= RuntimeLivenessTimeoutMs)
            {
                // —— 阶段 2：运行期 liveness ——
                // 只由「收到的可信下行」续命。**不看**本机发送是否成功：
                // 上行 Send 成功只说明字节进了本机内核缓冲，对端可以是死的。
                Fail("运行期 " + RuntimeLivenessTimeoutMs + "ms 未收到 Lobby 可信控制帧（持续上行发送成功不代表 Lobby 存活）");
                return;
            }

            if (_readySent && nowMs >= _nextHeartbeatAtMs)
            {
                _nextHeartbeatAtMs = nowMs + HeartbeatIntervalMs;
                SendHeartbeat(nowMs);
            }

            if (_hasResult)
            {
                if (_resultAcknowledged) { return; }

                if (nowMs >= _resultDeadlineMs)
                {
                    Fail("结果确认超时（" + ResultAckTimeoutMs + "ms 未收到匹配的 ResultAck）");
                    return;
                }

                if (nowMs >= _nextResultSendMs)
                {
                    _nextResultSendMs = nowMs + ResultRetransmitMs;
                    SendResult();
                }
            }
        }

        /// <summary>
        /// 提交结果（权威玩法或显式 smoke 验收调用）。
        ///
        /// 幂等口径（契约 §3）：同一个 ResultId 重发得到同一个 Ack；同 ID 不同内容拒绝。
        /// 一旦提交，本类会按 <see cref="ResultRetransmitMs"/> 重发，直到收到匹配的 ResultAck。
        /// </summary>
        public bool SubmitResult(int winnerTeamId, byte[] summary, out string error)
        {
            error = null;

            if (_disposed) { error = "代理已释放"; return false; }
            if (!_started) { error = "控制通道尚未建立"; return false; }
            if (_faulted) { error = "代理已失败：" + _faultReason; return false; }
            if (_resultAcknowledged) { error = "结果已被确认"; return false; }

            if (summary == null || summary.Length == 0)
            {
                error = "结果摘要不得为空";
                return false;
            }

            if (summary.Length > PMDsControlWire.MaxSummaryBytes)
            {
                error = "结果摘要 " + summary.Length + " 字节超过上限 " + PMDsControlWire.MaxSummaryBytes;
                return false;
            }

            if (_hasResult)
            {
                if (_resultWinnerTeamId == winnerTeamId && BytesEqual(_resultSummary, summary))
                {
                    return true;   // 完全相同的重复提交 = 幂等
                }

                error = "结果已提交且内容不同（同 ResultId 不得改内容）";
                return false;
            }

            _hasResult = true;
            _resultWinnerTeamId = winnerTeamId;
            _resultSummary = summary;
            _nextResultSendMs = 0L;
            _resultDeadlineMs = _lastPumpMs + ResultAckTimeoutMs;
            return true;
        }

        /// <summary>幂等释放：停线程、关 socket、清队列。</summary>
        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            _stopRequested = true;

            SafeClose(_socket);
            _socket = null;

            Thread thread = _receiveThread;
            _receiveThread = null;
            if (thread != null && thread != Thread.CurrentThread)
            {
                try { thread.Join(500); }
                catch (ThreadStateException) { }
            }

            byte[] ignored;
            while (_inbound.TryDequeue(out ignored)) { }
        }

        // =================================================================================
        //  主线程入站
        // =================================================================================

        private void DrainInbound()
        {
            int processed = 0;
            byte[] chunk;
            while (processed < MaxChunksPerPump && _inbound.TryDequeue(out chunk))
            {
                processed++;
                try
                {
                    _decoder.Append(chunk, 0, chunk.Length);
                }
                catch (PMDsControlProtocolException ex)
                {
                    Fail("控制分帧非法：" + ex.Message);
                    return;
                }
                catch (Exception ex)
                {
                    Fail("控制分帧异常：" + ex.GetType().Name);
                    return;
                }

                byte[] payload;
                while (_decoder.TryDequeue(out payload))
                {
                    if (!HandlePayload(payload))
                    {
                        return;
                    }

                    if (_faulted) { return; }
                }
            }
        }

        /// <summary>处理一条已分帧的载荷：解码 + 验签 + 身份/方向校验 + 状态推进。</summary>
        private bool HandlePayload(byte[] payload)
        {
            PMDsControlMessage message;
            PMDsControlVerifyFault fault;
            if (!_signer.VerifyRaw(payload, 0, payload.Length, out message, out fault))
            {
                MacFailures++;
                // 契约 §3：MAC 验证前不得修改状态。这里更进一步 —— 本机对端是 Lobby，
                // 坏 MAC 意味着密钥/局号/摘要根本对不上，属于「控制面不可信」，
                // 明确失败比继续跑更安全（也更可观测）。
                Fail("控制帧验签失败：" + fault);
                return false;
            }

            if (!IdentityMatches(message, out string identityError))
            {
                FramesRejected++;
                Fail("控制帧身份不符：" + identityError);
                return false;
            }

            FramesReceived++;

            // 与 FramesReceived 同点：只有 MAC + 身份都通过的帧才算「可信下行」并续命运行期 liveness。
            // 坏 MAC / 错身份在上面已经 return，因此它们**不能**续命。
            _lastTrustedRxMs = _lastPumpMs;

            switch (message.Type)
            {
                case PMDsControlMessageType.Heartbeat:
                    return HandleHeartbeat(message);

                case PMDsControlMessageType.ResultAck:
                    return HandleResultAck(message);

                case PMDsControlMessageType.Shutdown:
                    return HandleShutdown(message);

                case PMDsControlMessageType.Error:
                    return HandleError(message);

                default:
                    FramesRejected++;
                    Fail("Lobby 不得下发 " + message.Type + " 类型的控制帧");
                    return false;
            }
        }

        /// <summary>
        /// 受理 Lobby 的 Heartbeat 下行（运行期 liveness 的续命信号，见 <see cref="Pump"/> 阶段 2）。
        ///
        /// 三条纪律：
        /// 1. 只有已经通过 **MAC + 局/epoch/hash** 校验的帧才会走到这里（<see cref="HandlePayload"/> 里先验后派）；
        /// 2. <see cref="ReadySent"/> 必须为真：Lobby 在收到本机 Ready 之前就下发心跳属于时序/方向不符，fail-closed；
        /// 3. **本机不因收到 Heartbeat 而立即回 Heartbeat** —— 上行心跳只按自身定时节奏发（<see cref="HeartbeatIntervalMs"/>）。
        ///    这条是「心跳回声风暴」的根本防线：两端若互相回，就会形成永不停歇的对拍。
        /// </summary>
        private bool HandleHeartbeat(PMDsControlMessage message)
        {
            PMDsHeartbeatBody body = message.AsHeartbeat;
            if (body == null)
            {
                Fail("Heartbeat 缺少业务体");
                return false;
            }

            if (!_readySent)
            {
                Fail("Lobby 在收到本机 Ready 之前下发了 Heartbeat（时序/方向不符）");
                return false;
            }

            HeartbeatsReceived++;
            return true;
        }

        private bool HandleResultAck(PMDsControlMessage message)
        {
            PMDsResultAckBody body = message.AsResultAck;
            if (body == null)
            {
                Fail("ResultAck 缺少业务体");
                return false;
            }

            if (!_hasResult)
            {
                // A1 口径：终态之外不接受 ResultAck。忽略但明确记录，不当成「确认成功」。
                WarnInternal("收到 ResultAck(" + body.ResultId + ") 时本机尚未提交任何结果，已忽略");
                return true;
            }

            if (body.ResultId != _resultId)
            {
                WarnInternal("收到不匹配的 ResultAck(" + body.ResultId + ")，期望 " + _resultId + "，已忽略");
                return true;
            }

            _resultAcknowledged = true;
            Info("收到匹配的 ResultAck(" + _resultId + ")，结果确认完成，准备退出");
            SendExited(0);
            RequestExit(0);
            return true;
        }

        private bool HandleShutdown(PMDsControlMessage message)
        {
            PMDsShutdownBody body = message.AsShutdown;
            uint reasonCode = body != null ? body.ReasonCode : 0u;
            Info("收到 Lobby 的 Shutdown（reason=" + reasonCode.ToString(CultureInfo.InvariantCulture) + "），优雅收尾");
            SendExited(0);
            RequestExit(0);
            return true;
        }

        private bool HandleError(PMDsControlMessage message)
        {
            PMDsErrorBody body = message.AsError;
            string text = body != null ? body.Message : "<无消息体>";
            uint code = body != null ? body.Code : 0u;
            Fail("Lobby 上报协议错误 code=" + code.ToString(CultureInfo.InvariantCulture) + " msg=" + text);
            return false;
        }

        private bool IdentityMatches(PMDsControlMessage message, out string error)
        {
            error = null;

            if (!string.Equals(message.MatchId, _boot.MatchId, StringComparison.Ordinal))
            {
                error = "MatchId 不符（收到 '" + message.MatchId + "'，期望 '" + _boot.MatchId + "'）";
                return false;
            }

            if (!string.Equals(message.DsId, _boot.DsId, StringComparison.Ordinal))
            {
                error = "DsId 不符（收到 '" + message.DsId + "'，期望 '" + _boot.DsId + "'）";
                return false;
            }

            if (message.Epoch != _boot.Epoch)
            {
                error = "Epoch 不符（收到 " + message.Epoch + "，期望 " + _boot.Epoch + "）";
                return false;
            }

            if (message.ProtocolHash != _boot.ProtocolHash)
            {
                error = "ProtocolHash 不符（收到 0x" + message.ProtocolHash.ToString("X8", CultureInfo.InvariantCulture)
                        + "，期望 0x" + _boot.ProtocolHash.ToString("X8", CultureInfo.InvariantCulture) + "）";
                return false;
            }

            return true;
        }

        // =================================================================================
        //  上行
        // =================================================================================

        private bool SendReady(long nowMs, bool quiet)
        {
            PMDsReadyBody body = new PMDsReadyBody();
            body.BoundPort = _boundPort;
            body.SceneReady = SceneReady;
            body.CollisionDigest = _collisionDigest;

            string error;
            if (!SendMessage(PMDsControlMessageType.Ready, body, 0UL, out error))
            {
                if (!quiet)
                {
                    Fail("Ready 发送失败：" + error);
                    return false;
                }

                WarnInternal("Ready 重发失败：" + error);
                return true;
            }

            _nextReadyAtMs = nowMs + ReadyRetransmitMs;
            if (!quiet)
            {
                Info("已上报 Ready：boundPort=" + _boundPort + " sceneReady=" + SceneReady
                     + " digest=0x" + _collisionDigest.ToString("X8", CultureInfo.InvariantCulture));
            }

            return true;
        }

        private void SendHeartbeat(long nowMs)
        {
            PMDsHeartbeatBody body = new PMDsHeartbeatBody();
            body.UptimeMilliseconds = nowMs - _startedAtMs;
            body.PlayerCount = PlayerCountProvider != null ? PlayerCountProvider() : 0;

            string error;
            if (!SendMessage(PMDsControlMessageType.Heartbeat, body, 0UL, out error))
            {
                WarnInternal("Heartbeat 发送失败：" + error);
            }
        }

        private void SendResult()
        {
            PMDsResultBody body = new PMDsResultBody();
            body.ResultId = _resultId;
            body.WinnerTeamId = _resultWinnerTeamId;
            body.Summary = _resultSummary;

            string error;
            if (!SendMessage(PMDsControlMessageType.Result, body, _resultId, out error))
            {
                WarnInternal("Result(" + _resultId + ") 发送失败：" + error);
                return;
            }

            ResultSends++;
            if (ResultSends <= 2L)
            {
                Info("已上报结果 ResultId=" + _resultId + " winner=" + _resultWinnerTeamId
                     + " summary=" + _resultSummary.Length + "B");
            }
        }

        private void SendExited(int exitCode)
        {
            if (_exitedSent) { return; }
            _exitedSent = true;

            PMDsExitedBody body = new PMDsExitedBody();
            body.ExitCode = exitCode;

            string error;
            if (!SendMessage(PMDsControlMessageType.Exited, body, 0UL, out error))
            {
                WarnInternal("Exited 发送失败：" + error);
            }
        }

        private void SendError(uint code, string text)
        {
            PMDsErrorBody body = new PMDsErrorBody();
            body.Code = code;
            body.Message = text == null ? string.Empty : text;

            string error;
            if (!SendMessage(PMDsControlMessageType.Error, body, 0UL, out error))
            {
                WarnInternal("Error 发送失败：" + error);
            }
        }

        private bool SendMessage(PMDsControlMessageType type, PMDsControlBody body, ulong requestId, out string error)
        {
            error = null;

            if (_socket == null || !_started)
            {
                error = "控制通道未建立";
                return false;
            }

            try
            {
                PMDsControlMessage message = PMDsControlMessage.Create(
                    type, _boot.MatchId, _boot.DsId, _boot.Epoch, _boot.ProtocolHash, requestId, body);
                byte[] payload = _signer.Sign(message);
                byte[] frame = PMDsControlFraming.Frame(payload);

                int sent = 0;
                while (sent < frame.Length)
                {
                    int n = _socket.Send(frame, sent, frame.Length - sent, SocketFlags.None);
                    if (n <= 0) { throw new SocketException((int)SocketError.ConnectionReset); }
                    sent += n;
                }

                FramesSent++;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + " " + ex.Message;
                return false;
            }
        }

        // =================================================================================
        //  失败 / 退出
        // =================================================================================

        private void Fail(string reason)
        {
            if (_faulted) { return; }

            _faulted = true;
            _faultReason = reason;
            WarnInternal("控制通道失败：" + reason);

            // 尽力告知对端（失败不影响本地退出决定）。
            SendError(1u, reason);
            SendExited(1);
            RequestExit(1);
        }

        private void RequestExit(int code)
        {
            if (_exitRequested) { return; }
            _exitRequested = true;
            _exitCode = code;

            Action<int> handler = ExitRequested;
            if (handler != null)
            {
                handler(code);
            }
        }

        // =================================================================================
        //  后台接收线程（只入队）
        // =================================================================================

        private void ReceiveLoop()
        {
            byte[] buffer = new byte[ReceiveChunkBytes];

            while (!_stopRequested)
            {
                Socket socket = _socket;
                if (socket == null) { break; }

                int read;
                try
                {
                    read = socket.Receive(buffer, 0, buffer.Length, SocketFlags.None);
                }
                catch (SocketException)
                {
                    if (_stopRequested) { break; }
                    Thread.Sleep(20);
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception)
                {
                    if (_stopRequested) { break; }
                    Thread.Sleep(20);
                    continue;
                }

                if (read <= 0)
                {
                    break;   // 对端关闭
                }

                if (_inbound.Count >= MaxInboundChunks)
                {
                    // 有界：对端乱写不能把内存撑爆。丢弃并计数，由主线程观测。
                    Interlocked.Increment(ref _chunksDropped);
                    continue;
                }

                byte[] chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                _inbound.Enqueue(chunk);
            }

            Interlocked.Exchange(ref _peerClosed, 1);
        }

        // =================================================================================
        //  小工具
        // =================================================================================

        private static bool IsLoopbackHost(string host)
        {
            if (string.IsNullOrEmpty(host)) { return false; }
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) { return true; }
            if (string.Equals(host, "ip6-localhost", StringComparison.OrdinalIgnoreCase)) { return true; }

            IPAddress address;
            return IPAddress.TryParse(host, out address) && IPAddress.IsLoopback(address);
        }

        private static IPAddress ResolveLoopback(string host)
        {
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "ip6-localhost", StringComparison.OrdinalIgnoreCase))
            {
                return IPAddress.Loopback;
            }

            return IPAddress.Parse(host);
        }

        private static void SafeClose(Socket socket)
        {
            if (socket == null) { return; }

            try { socket.Shutdown(SocketShutdown.Both); }
            catch (Exception) { }

            try { socket.Close(); }
            catch (Exception) { }
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b)) { return true; }
            if (a == null || b == null) { return false; }
            if (a.Length != b.Length) { return false; }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) { return false; }
            }

            return true;
        }

        private void Info(string message)
        {
            Action<string> handler = Log;
            if (handler != null) { handler(message); }
        }

        private void WarnInternal(string message)
        {
            Action<string> handler = Warn;
            if (handler != null) { handler(message); }
        }

        /// <summary>单行诊断摘要（宿主心跳日志用；不含任何秘密）。</summary>
        public string Describe()
        {
            return "lobby=" + ControlEndpoint
                   + " connected=" + (IsConnected ? 1 : 0)
                   + " ready=" + (_readySent ? 1 : 0)
                   + " sceneReady=" + (SceneReady ? 1 : 0)
                   + " sent=" + FramesSent
                   + " recv=" + FramesReceived
                   + " hbRx=" + HeartbeatsReceived
                   + " lastTrusted=" + _lastTrustedRxMs.ToString(CultureInfo.InvariantCulture)
                   + " reject=" + FramesRejected
                   + " macFail=" + MacFailures
                   + " result=" + (_hasResult ? _resultId.ToString(CultureInfo.InvariantCulture) : "<none>")
                   + " acked=" + (_resultAcknowledged ? 1 : 0)
                   + " resultSends=" + ResultSends
                   + " fault=" + (_faulted ? _faultReason : "<none>");
        }
    }
}
