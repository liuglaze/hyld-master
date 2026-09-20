using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace PMNet.Control
{
    /// <summary>
    /// Lobby 宿主的可注入配置（R3-B 契约 §7.3）。
    ///
    /// 全部字段都允许由配置/环境变量注入：**不猜**固定用户目录、不硬编码端口。
    /// 其中 <see cref="DsExecutablePath"/> / <see cref="DsWorkingDirectory"/> /
    /// <see cref="BootstrapRootDirectory"/> 为空时，新链**不会**被启用（避免拿一个猜出来的路径
    /// 去启动真实进程）。
    /// </summary>
    public sealed class PMDsLobbyHostOptions
    {
        /// <summary>是否启用新链（由 <c>HYLD_PMNET_DS=1</c> 显式打开；默认关闭 = opt-in）。</summary>
        public bool Enabled;

        /// <summary>控制通道监听地址（首版只允许 loopback）。</summary>
        public string ListenAddress = "127.0.0.1";

        /// <summary>控制通道监听端口；0 表示由系统分配（测试用）。</summary>
        public int ControlPort = PMDsControlWire.DefaultControlPort;

        /// <summary>DS 可执行文件绝对路径（配置白名单；不从客户端输入取）。</summary>
        public string DsExecutablePath = string.Empty;

        /// <summary>DS 工作目录绝对路径。</summary>
        public string DsWorkingDirectory = string.Empty;

        /// <summary>每局引导文件的父目录（引导文件写在该目录下的每局子目录里）。</summary>
        public string BootstrapRootDirectory = string.Empty;

        /// <summary>全局共享端口池下界。</summary>
        public int PortRangeFirst = 7801;

        /// <summary>全局共享端口池上界。</summary>
        public int PortRangeLast = 7899;

        /// <summary>协议摘要（由 <c>PMR3Runtime.ProtocolHash</c> 提供，宿主只透传）。</summary>
        public uint ProtocolHash;

        /// <summary>碰撞配置摘要（由 <c>PMR3Runtime.CollisionDigest</c> 提供，宿主只透传）。</summary>
        public uint CollisionDigest;

        /// <summary>名册人数上限（契约 §2：最多 6 人）。</summary>
        public int MaxRosterPlayers = PMDsControlWire.MaxRosterPlayers;

        /// <summary>泵循环间隔（毫秒）。宿主线程按此节拍 drain + Tick，不阻塞等 socket。</summary>
        public int PumpIntervalMilliseconds = 50;

        /// <summary>未通过身份/MAC 校验的连接最多允许存活多久（毫秒），超时由泵关闭。</summary>
        public int UnboundConnectionTimeoutMilliseconds = 20000;

        /// <summary>是否启动后台泵线程（测试可关掉，改为手工 <c>PumpOnce</c>）。</summary>
        public bool RunBackgroundPumpThread = true;

        /// <summary>启动超时（透传给协调器，测试可注入更短的值）。</summary>
        public TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

        /// <summary>心跳超时（透传给协调器）。</summary>
        public TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(15);

        /// <summary>结果确认重发间隔（透传给协调器）。</summary>
        public TimeSpan ResultAckRetryInterval = TimeSpan.FromSeconds(1);

        /// <summary>收尾宽限期（透传给协调器）。</summary>
        public TimeSpan ShutdownGracePeriod = TimeSpan.FromSeconds(10);

        /// <summary>进程退出确认等待（透传给协调器）。</summary>
        public TimeSpan ProcessExitConfirmWait = TimeSpan.FromMilliseconds(500);

        /// <summary>强杀重试间隔（透传给协调器）。</summary>
        public TimeSpan KillRetryInterval = TimeSpan.FromSeconds(1);

        /// <summary>新链是否具备启动一局的最小配置（不满足时拒绝开局，而不是猜路径）。</summary>
        public bool IsLaunchConfigured
        {
            get
            {
                return !string.IsNullOrEmpty(DsExecutablePath)
                    && !string.IsNullOrEmpty(DsWorkingDirectory)
                    && !string.IsNullOrEmpty(BootstrapRootDirectory)
                    && PortRangeFirst > 0
                    && PortRangeLast >= PortRangeFirst;
            }
        }

        /// <summary>
        /// 从环境变量装载配置。**不提供任何猜测出来的默认路径**：
        /// 未显式配置 exe/工作目录/引导目录时，新链保持关闭。
        ///
        /// 变量清单：
        /// - <c>HYLD_PMNET_DS</c> = 1|true 打开新链；
        /// - <c>HYLD_PMNET_DS_EXE</c> / <c>HYLD_PMNET_DS_WORKDIR</c> / <c>HYLD_PMNET_DS_BOOTSTRAP_DIR</c>；
        /// - <c>HYLD_PMNET_DS_PORT_FIRST</c> / <c>HYLD_PMNET_DS_PORT_LAST</c>；
        /// - <c>HYLD_PMNET_DS_CONTROL_ADDR</c> / <c>HYLD_PMNET_DS_CONTROL_PORT</c>。
        /// </summary>
        public static PMDsLobbyHostOptions FromEnvironment()
        {
            PMDsLobbyHostOptions options = new PMDsLobbyHostOptions();
            options.Enabled = ReadBool("HYLD_PMNET_DS", false);
            options.DsExecutablePath = ReadString("HYLD_PMNET_DS_EXE", string.Empty);
            options.DsWorkingDirectory = ReadString("HYLD_PMNET_DS_WORKDIR", string.Empty);
            options.BootstrapRootDirectory = ReadString("HYLD_PMNET_DS_BOOTSTRAP_DIR", string.Empty);
            options.ListenAddress = ReadString("HYLD_PMNET_DS_CONTROL_ADDR", options.ListenAddress);
            options.ControlPort = ReadInt("HYLD_PMNET_DS_CONTROL_PORT", options.ControlPort);
            options.PortRangeFirst = ReadInt("HYLD_PMNET_DS_PORT_FIRST", options.PortRangeFirst);
            options.PortRangeLast = ReadInt("HYLD_PMNET_DS_PORT_LAST", options.PortRangeLast);
            return options;
        }

        private static string ReadString(string name, string fallback)
        {
            string value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        private static int ReadInt(string name, int fallback)
        {
            string value = Environment.GetEnvironmentVariable(name);
            int parsed;
            if (!string.IsNullOrEmpty(value) && int.TryParse(value, out parsed))
            {
                return parsed;
            }

            return fallback;
        }

        private static bool ReadBool(string name, bool fallback)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value))
            {
                return fallback;
            }

            return value == "1"
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // 客户端网关（Lobby → 已认证客户端）
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 入局通知的字段集合。
    ///
    /// ⚠ **它不是 <c>PMDsEntryOffer</c> 的替身**：本类型只承载宿主产出的字段，
    /// 生产实现必须用冻结的 <c>PMNet.Session.PMDsEntryCodec.Encode</c> 把这些字段编码成
    /// <c>MainPack.Str</c>。把它做成「另一个 offer 实现」会绕过共享契约，本类刻意只做透传。
    /// </summary>
    public sealed class PMDsLobbyEntryNotice
    {
        /// <summary>对局 ID。</summary>
        public string MatchId = string.Empty;

        /// <summary>DS ID。</summary>
        public string DsId = string.Empty;

        /// <summary>DS 主机（首版 loopback）。</summary>
        public string Host = string.Empty;

        /// <summary>DS 实际监听端口（= Allocate 后的真实端口）。</summary>
        public int Port;

        /// <summary>会话世代。</summary>
        public uint Epoch;

        /// <summary>协议摘要。</summary>
        public uint ProtocolHash;

        /// <summary>碰撞配置摘要。</summary>
        public uint CollisionDigest;

        /// <summary>该玩家在名册中的身份。</summary>
        public PMDsRosterIdentity Identity;

        /// <summary>该玩家的入场票据原始字节（**秘密材料**，生产实现不得写日志）。</summary>
        public byte[] Ticket = new byte[0];
    }

    /// <summary>结算结果通知的字段集合（**不含**帧历史，不伪造 BattleReview）。</summary>
    public sealed class PMDsLobbyResultNotice
    {
        /// <summary>对局 ID。</summary>
        public string MatchId = string.Empty;

        /// <summary>DS ID。</summary>
        public string DsId = string.Empty;

        /// <summary>会话世代。</summary>
        public uint Epoch;

        /// <summary>结果 ID。</summary>
        public ulong ResultId;

        /// <summary>胜方队伍。</summary>
        public int WinnerTeamId;

        /// <summary>结算摘要字节（可为空）。</summary>
        public byte[] Summary = new byte[0];

        /// <summary>名册快照（宿主自己的副本）。</summary>
        public PMDsRosterIdentity[] Roster = new PMDsRosterIdentity[0];
    }

    /// <summary>
    /// 客户端网关：宿主与大厅 TCP 之间的**唯一**缝。
    ///
    /// 为什么要有这个接口：<see cref="PMDsLobbyHost"/> 必须能脱离完整 Server 类型被测试
    /// （契约 §5「有界测试宿主（非Unity）」），而「找到当前已认证的 Client 并发 StartEnterBattle」、
    /// 「结果通知」、「恢复 PlayerOnline」都是大厅侧的事。生产实现放在 Server 侧，
    /// **必须**用冻结的 <c>PMDsEntryCodec.Encode</c> 编码。
    /// </summary>
    public interface IPMDsLobbyClientGateway
    {
        /// <summary>该 uid 当前是否存在**已认证**的活跃连接。</summary>
        bool IsClientAuthenticated(int uid);

        /// <summary>
        /// 把入局通知发给该 uid 当前已认证的连接。返回 false 时必须给出原因（不含秘密）。
        /// 实现不得把 offer / MainPack.Str / 完整票据写入日志。
        /// </summary>
        bool TrySendEntryOffer(PMDsLobbyEntryNotice notice, out string error);

        /// <summary>
        /// 明确的对局结束通知（可选）。**不得**在此伪造 BattleReview 帧历史（回放属 R6）。
        /// 允许是空实现。
        /// </summary>
        void NotifyMatchEnded(PMDsLobbyResultNotice notice);

        /// <summary>确认 DS 进程退出、资源已释放之后，把参战玩家恢复为在线状态。</summary>
        void RestorePlayerOnline(int uid);
    }

    // ────────────────────────────────────────────────────────────────────
    // 匹配请求 / 返回
    // ────────────────────────────────────────────────────────────────────

    /// <summary>一次新链开局请求（由匹配层构造成员名册之后交给宿主）。</summary>
    public sealed class PMDsLobbyMatchRequest
    {
        /// <summary>对局 ID（非空，≤128 字节）。</summary>
        public string MatchId = string.Empty;

        /// <summary>名册（1..6 人；必须与**已认证活跃客户端**一致，缺人整局拒绝）。</summary>
        public PMDsRosterIdentity[] Roster = new PMDsRosterIdentity[0];

        /// <summary>赛制描述（诊断用，不参与协议）。</summary>
        public string FightPattern = string.Empty;

        /// <summary>碰撞摘要覆盖（0 = 用宿主配置）。</summary>
        public uint CollisionDigest;

        /// <summary>业务请求 ID（诊断用）。</summary>
        public long RequestId;
    }

    /// <summary>开局请求的处理结论。</summary>
    public enum PMDsLobbyStartOutcome : byte
    {
        /// <summary>已接受并入队，将在泵循环里落地（分配 → 写引导文件 → 启动）。</summary>
        Queued = 0,

        /// <summary>宿主未运行 / 新链未启用。</summary>
        RejectedNotRunning = 1,

        /// <summary>请求结构非法（空 matchId / 空名册 / 超上限 / uid 重复）。</summary>
        RejectedMalformed = 2,

        /// <summary>名册里有人不在线/未认证 —— **整局拒绝，不缩编**。</summary>
        RejectedOffline = 3,

        /// <summary>名册里有人已被其它对局占用。</summary>
        RejectedPlayerOccupied = 4,

        /// <summary>启动所需配置缺失（exe / 工作目录 / 引导目录 / 端口范围）。</summary>
        RejectedNotConfigured = 5,
    }

    /// <summary>开局请求的即时回执。</summary>
    public struct PMDsLobbyStartReply
    {
        /// <summary>结论。</summary>
        public PMDsLobbyStartOutcome Outcome;

        /// <summary>原因（不含秘密）。</summary>
        public string Detail;

        /// <summary>是否已入队（不代表已经启动成功）。</summary>
        public bool IsQueued { get { return Outcome == PMDsLobbyStartOutcome.Queued; } }

        public override string ToString()
        {
            return Outcome + (string.IsNullOrEmpty(Detail) ? string.Empty : "（" + Detail + "）");
        }
    }

    /// <summary>一局真正启动成功后的公开记录（供匹配层对账/日志）。</summary>
    public struct PMDsLobbySessionRecord
    {
        /// <summary>对局 ID。</summary>
        public string MatchId;

        /// <summary>DS ID。</summary>
        public string DsId;

        /// <summary>会话世代。</summary>
        public uint Epoch;

        /// <summary>真实分配端口。</summary>
        public int Port;

        /// <summary>进程 ID。</summary>
        public int ProcessId;

        public override string ToString()
        {
            return "match=" + MatchId + " ds=" + DsId + " epoch=" + Epoch
                + " port=" + Port + " pid=" + ProcessId;
        }
    }

    /// <summary>
    /// Lobby 宿主（R3-B，契约 §7.3）：**唯一**串行调度所有 <see cref="PMDsCoordinator"/> 的所有者。
    ///
    /// ## 线程模型（关键约束）
    /// - socket 线程（<see cref="PMDsControlListener"/> 的连接 worker）**只入队**，绝不触碰会话状态；
    /// - 匹配层（大厅 TCP 接收回调线程）只调用 <see cref="TryStartMatch"/> / <see cref="NotifyClientDisconnected"/>，
    ///   二者都是「校验 + 入队」，不直接进协调器；
    /// - 泵线程（或测试里的 <see cref="PumpOnce"/>）是**唯一**会调用
    ///   <see cref="PMDsCoordinator"/> 的线程：drain 控制入站 → drain 命令 → 应用后置动作 → 对所有会话 Tick。
    ///
    /// 因此「不要阻塞整个 host 等待外部 socket 读」是结构性的：socket 读在各自 worker 上，
    /// 泵只做有界 drain，绝不等待任何 socket。
    ///
    /// ## 资源所有权
    /// - <see cref="PMDsPortPool"/>：**全局唯一**，注入给每个协调器（否则两局撞端口）；
    /// - <see cref="PMDsPlayerLedger"/>：**全局唯一**，跨会话 uid 占用；
    ///   与端口**同点释放**（确认 DS 进程退出后由协调器释放）。
    ///
    /// ## 引导文件
    /// 路径 = <c>&lt;BootstrapRootDirectory&gt;/&lt;matchId&gt;/bootstrap-&lt;epoch&gt;.bin</c>；
    /// 先写临时文件再原子改名（<c>File.Replace</c>/<c>File.Move</c>），**只有发布成功才 BeginStart**，
    /// 失败即回滚（分配态直接收尾并归还端口/uid）。文件受本机账户边界保护（不额外做 DPAPI）。
    ///
    /// ## 刻意不做
    /// - 不调用 <c>BattleManage</c>、不写 <c>_uidToBattleIds</c>（新链不借用旧路由）；
    /// - 不伪造 BattleReview；不回退旧链（调用方选链一次，失败即失败）；
    /// - 不把密钥/票据写日志。
    /// </summary>
    public sealed class PMDsLobbyHost : IDisposable
    {
        /// <summary>控制通道与 <c>-control</c> 参数的地址分隔符。</summary>
        private const char KeySeparator = '\u0001';

        /// <summary>每次 Pump 最多处理的控制载荷条数（有界，避免单次长饿死 Tick）。</summary>
        private const int InboundBudgetPerPump = 64;

        /// <summary>每次 Pump 最多处理的命令条数。</summary>
        private const int CommandBudgetPerPump = 16;

        /// <summary>未绑定会话的连接最多可堆积的出站帧数。</summary>
        private const int PendingOutboundLimit = 16;

        private readonly PMDsLobbyHostOptions _options;
        private readonly IPMDsClock _clock;
        private readonly IPMDsProcessLauncher _launcher;
        private readonly IPMDsLobbyClientGateway _gateway;
        private readonly PMDsControlListener _listener;
        private readonly PMDsPortPool _ports;
        private readonly PMDsPlayerLedger _ledger = new PMDsPlayerLedger();

        private readonly Dictionary<string, LobbySession> _sessions = new Dictionary<string, LobbySession>(StringComparer.Ordinal);
        private readonly Dictionary<int, long> _connectionOpenedAt = new Dictionary<int, long>();
        private readonly Dictionary<int, LobbySession> _connectionToSession = new Dictionary<int, LobbySession>();
        private readonly ConcurrentQueue<LobbyCommand> _commands = new ConcurrentQueue<LobbyCommand>();
        private readonly List<PMDsLobbyResultNotice> _pendingResultNotices = new List<PMDsLobbyResultNotice>();
        private readonly List<PMDsLobbySessionRecord> _startedSessions = new List<PMDsLobbySessionRecord>();
        private readonly object _statusGate = new object();

        private Thread _pumpThread;
        private volatile bool _running;
        private volatile bool _disposed;
        private uint _epochSequence;

        /// <summary>日志出口。默认丢弃；生产由 Server 接到 <c>Logging.Debug.Log</c>。</summary>
        public Action<string> Log;

        /// <summary>一局真正启动成功（含真实端口与 pid）时触发。</summary>
        public event Action<PMDsLobbySessionRecord> SessionStarted;

        /// <summary>一局被拒绝/失败（含原因）时触发。</summary>
        public event Action<string, string> SessionFailed;

        /// <summary>会话终态且资源已释放时触发。</summary>
        public event Action<string, PMDsSessionState, string> SessionReleased;

        /// <summary>
        /// 收到权威结果时触发（控制面结果：ResultId / 胜方 / 摘要）。
        /// **不含**回放帧历史，也不得据此伪造 BattleReview（回放属 R6）。
        /// </summary>
        public event Action<PMDsLobbyResultNotice> ResultAccepted;

        // ── 计数（诊断/门禁证据） ────────────────────────────────────────
        public long InboundFramesDispatched;
        public long InboundFramesDroppedUnknownSession;
        public long InboundFramesDroppedMacFailed;
        public long InboundFramesDroppedMalformed;
        public long HijackAttempts;
        public long SessionStartsAccepted;
        public long SessionStartsRejected;
        public long BootstrapFilesPublished;
        public long BootstrapPublishFailures;
        public long EntryOffersSent;
        public long EntryOfferFailures;
        public long ClientDisconnectAborts;
        public long ControlFramesUndeliverable;
        public long UnboundConnectionsClosedByTimeout;
        public long PumpExceptions;

        /// <summary>是否启用新链（由 <see cref="Server"/> 在启动时按配置置位；缺省关闭 = opt-in）。</summary>
        public static bool NewChainEnabled { get; internal set; }

        /// <summary>进程内唯一实例（未启用时为 null）。</summary>
        public static PMDsLobbyHost Instance { get; internal set; }

        public PMDsLobbyHost(PMDsLobbyHostOptions options, IPMDsClock clock,
            IPMDsProcessLauncher launcher, IPMDsLobbyClientGateway gateway)
        {
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }

            if (gateway == null)
            {
                throw new ArgumentNullException("gateway");
            }

            _options = options;
            _clock = clock ?? PMDsSystemClock.Instance;
            _launcher = launcher;
            _gateway = gateway;
            _ports = new PMDsPortPool(options.PortRangeFirst, options.PortRangeLast);
            _listener = new PMDsControlListener(options.ListenAddress, options.ControlPort);
        }

        /// <summary>实际控制端口（监听启动后有效）。</summary>
        public int ControlPort { get { return _listener.BoundPort; } }

        /// <summary>全局端口池（测试/诊断用）。</summary>
        public PMDsPortPool PortPool { get { return _ports; } }

        /// <summary>跨会话 uid 占用账本（测试/诊断用）。</summary>
        public PMDsPlayerLedger PlayerLedger { get { return _ledger; } }

        /// <summary>是否已启动。</summary>
        public bool IsRunning { get { return _running; } }

        /// <summary>当前会话数（含延迟收尾中的会话）。</summary>
        public int SessionCount
        {
            get
            {
                lock (_statusGate)
                {
                    return _sessions.Count;
                }
            }
        }

        /// <summary>已成功启动过的会话记录（有界快照）。</summary>
        public PMDsLobbySessionRecord[] StartedSessions
        {
            get
            {
                lock (_statusGate)
                {
                    return _startedSessions.ToArray();
                }
            }
        }

        // ────────────────────────────────────────────────────────────────
        // 生命周期
        // ────────────────────────────────────────────────────────────────

        /// <summary>启动监听与泵线程。</summary>
        public bool Start(out string error)
        {
            error = null;
            if (_running)
            {
                error = "宿主已启动";
                return false;
            }

            if (!_options.IsLaunchConfigured)
            {
                error = "新链启动配置不完整（需要 DS exe / 工作目录 / 引导目录 / 合法端口范围）；"
                    + "本实现不猜路径，拒绝启动";
                return false;
            }

            if (!_listener.Start(out error))
            {
                return false;
            }

            _running = true;
            if (_options.RunBackgroundPumpThread)
            {
                _pumpThread = new Thread(PumpLoop);
                _pumpThread.IsBackground = true;
                _pumpThread.Name = "PMDsLobbyPump";
                _pumpThread.Start();
            }

            Info("PMDsLobbyHost 已启动：control=" + _options.ListenAddress + ":" + _listener.BoundPort
                + " ports=" + _options.PortRangeFirst + "-" + _options.PortRangeLast
                + " bootstrapRoot=" + _options.BootstrapRootDirectory);
            return true;
        }

        /// <summary>停止泵与监听；未终结的会话交给各自的协调器收尾（可能保持资源占用）。</summary>
        public void Stop()
        {
            if (!_running && _pumpThread == null)
            {
                return;
            }

            _running = false;

            Thread pump = _pumpThread;
            if (pump != null)
            {
                try
                {
                    pump.Join(2000);
                }
                catch (Exception)
                {
                }

                _pumpThread = null;
            }

            _listener.Dispose();
            Info("PMDsLobbyHost 已停止");
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _running = false;

            Thread pump = _pumpThread;
            if (pump != null)
            {
                try
                {
                    pump.Join(2000);
                }
                catch (Exception)
                {
                }

                _pumpThread = null;
            }

            _listener.Dispose();

            List<LobbySession> sessions;
            lock (_statusGate)
            {
                sessions = new List<LobbySession>(_sessions.Values);
                _sessions.Clear();
            }

            for (int i = 0; i < sessions.Count; i++)
            {
                try
                {
                    sessions[i].Coordinator.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }

        private void PumpLoop()
        {
            while (_running)
            {
                try
                {
                    PumpOnce();
                }
                catch (Exception ex)
                {
                    PumpExceptions++;
                    Warn("泵循环异常（已隔离，不影响其他会话）：" + ex.GetType().Name + " " + ex.Message);
                }

                int interval = _options.PumpIntervalMilliseconds;
                if (interval < 1)
                {
                    interval = 1;
                }

                Thread.Sleep(interval);
            }
        }

        /// <summary>
        /// 单趟泵：drain 连接事件 → drain 控制入站 → drain 命令 → 应用后置动作 → Tick 所有会话 → 回收。
        /// **不阻塞**在任何 socket 上。测试可直接手工调用它获得确定性。
        /// </summary>
        public void PumpOnce()
        {
            DrainConnectionEvents();
            DrainInbound();
            DrainCommands();
            ApplyDeferredWork();
            TickSessions();
            ReapSessions();
        }

        // ────────────────────────────────────────────────────────────────
        // 对外入口（线程安全：只校验 + 入队）
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 提交一次新链开局（匹配层调用，线程安全）。
        ///
        /// 这里做的是「**整局**准入」的快速判定：名册与已认证活跃客户端一致、无人被占用。
        /// 缺任何一人即整局拒绝（**不缩编**），因为「悄悄少一个人」会让名册与客户端认知不一致。
        /// 真正的分配/启动在泵线程上落地。
        /// </summary>
        public PMDsLobbyStartReply TryStartMatch(PMDsLobbyMatchRequest request)
        {
            PMDsLobbyStartReply reply = new PMDsLobbyStartReply();

            if (!_running)
            {
                reply.Outcome = PMDsLobbyStartOutcome.RejectedNotRunning;
                reply.Detail = "宿主未运行";
                SessionStartsRejected++;
                return reply;
            }

            if (!_options.IsLaunchConfigured)
            {
                reply.Outcome = PMDsLobbyStartOutcome.RejectedNotConfigured;
                reply.Detail = "新链启动配置不完整（exe/工作目录/引导目录/端口范围）";
                SessionStartsRejected++;
                return reply;
            }

            if (request == null || string.IsNullOrEmpty(request.MatchId))
            {
                reply.Outcome = PMDsLobbyStartOutcome.RejectedMalformed;
                reply.Detail = "MatchId 不得为空";
                SessionStartsRejected++;
                return reply;
            }

            PMDsRosterIdentity[] roster = request.Roster;
            if (roster == null || roster.Length == 0)
            {
                reply.Outcome = PMDsLobbyStartOutcome.RejectedMalformed;
                reply.Detail = "名册不得为空";
                SessionStartsRejected++;
                return reply;
            }

            if (roster.Length > _options.MaxRosterPlayers)
            {
                reply.Outcome = PMDsLobbyStartOutcome.RejectedMalformed;
                reply.Detail = "名册超过上限：" + roster.Length + " > " + _options.MaxRosterPlayers;
                SessionStartsRejected++;
                return reply;
            }

            try
            {
                PMDsControlCodec.ValidateIdentities(roster, "新链名册");
                // MatchId 的 UTF-8 字节上限在分配前先查，避免「开了局才发现名字太长」。
                PMDsControlCodec.EnsureWithinBytes(request.MatchId, PMDsControlWire.MaxMatchIdBytes, "MatchId");
            }
            catch (PMDsControlProtocolException ex)
            {
                reply.Outcome = PMDsLobbyStartOutcome.RejectedMalformed;
                reply.Detail = ex.Message;
                SessionStartsRejected++;
                return reply;
            }

            for (int i = 0; i < roster.Length; i++)
            {
                int uid = roster[i].Uid;
                if (_ledger.IsOccupied(uid))
                {
                    string owner;
                    _ledger.TryGetMatchId(uid, out owner);
                    reply.Outcome = PMDsLobbyStartOutcome.RejectedPlayerOccupied;
                    reply.Detail = "uid " + uid + " 已被对局占用：" + owner;
                    SessionStartsRejected++;
                    return reply;
                }

                if (!_gateway.IsClientAuthenticated(uid))
                {
                    reply.Outcome = PMDsLobbyStartOutcome.RejectedOffline;
                    reply.Detail = "uid " + uid + " 不在线或未认证（整局拒绝，不缩编）";
                    SessionStartsRejected++;
                    return reply;
                }
            }

            LobbyCommand command = new LobbyCommand();
            command.Kind = LobbyCommandKind.StartMatch;
            command.MatchId = request.MatchId;
            command.FightPattern = request.FightPattern;
            command.CollisionDigest = request.CollisionDigest;
            command.RequestId = request.RequestId;
            command.Roster = (PMDsRosterIdentity[])roster.Clone();
            _commands.Enqueue(command);

            reply.Outcome = PMDsLobbyStartOutcome.Queued;
            reply.Detail = "已入队";
            return reply;
        }

        /// <summary>
        /// 大厅侧检测到客户端断线时调用（线程安全）。
        /// 首版语义（契约 §7.3）：**中止该测试局**，绝不伪造正常胜利。
        /// </summary>
        public void NotifyClientDisconnected(int uid)
        {
            if (uid <= 0)
            {
                return;
            }

            LobbyCommand command = new LobbyCommand();
            command.Kind = LobbyCommandKind.ClientDisconnected;
            command.Uid = uid;
            _commands.Enqueue(command);
        }

        /// <summary>该 uid 是否正被新链的某个会话占用（旧 ClearSence/旧路由用它避免误作用新局）。</summary>
        public bool IsUidInMatch(int uid)
        {
            return _ledger.IsOccupied(uid);
        }

        /// <summary>按对局 ID 查询会话当前状态。</summary>
        public bool TryGetSessionState(string matchId, out PMDsSessionState state)
        {
            state = PMDsSessionState.Idle;
            if (string.IsNullOrEmpty(matchId))
            {
                return false;
            }

            lock (_statusGate)
            {
                foreach (KeyValuePair<string, LobbySession> pair in _sessions)
                {
                    if (string.Equals(pair.Value.MatchId, matchId, StringComparison.Ordinal))
                    {
                        state = pair.Value.Coordinator.State;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>该对局当前是否已绑定控制连接（诊断/门禁用，只读）。</summary>
        public bool HasBoundControlConnection(string matchId)
        {
            if (string.IsNullOrEmpty(matchId))
            {
                return false;
            }

            lock (_statusGate)
            {
                foreach (KeyValuePair<string, LobbySession> pair in _sessions)
                {
                    if (string.Equals(pair.Value.MatchId, matchId, StringComparison.Ordinal))
                    {
                        return pair.Value.ControlConnectionId != 0;
                    }
                }
            }

            return false;
        }

        /// <summary>按对局 ID 查询协调器实例（**只读**用途：诊断/门禁断言）。</summary>
        public PMDsCoordinator GetCoordinator(string matchId)
        {
            if (string.IsNullOrEmpty(matchId))
            {
                return null;
            }

            lock (_statusGate)
            {
                foreach (KeyValuePair<string, LobbySession> pair in _sessions)
                {
                    if (string.Equals(pair.Value.MatchId, matchId, StringComparison.Ordinal))
                    {
                        return pair.Value.Coordinator;
                    }
                }
            }

            return null;
        }

        // ────────────────────────────────────────────────────────────────
        // 泵的内部步骤
        // ────────────────────────────────────────────────────────────────

        private void DrainConnectionEvents()
        {
            PMDsControlConnectionEvent evt;
            while (_listener.TryDequeueConnectionEvent(out evt))
            {
                if (evt.Kind == PMDsControlConnectionEventKind.Opened)
                {
                    lock (_statusGate)
                    {
                        _connectionOpenedAt[evt.ConnectionId] = _clock.UtcNowUnixMilliseconds;
                    }

                    continue;
                }

                LobbySession session;
                lock (_statusGate)
                {
                    _connectionOpenedAt.Remove(evt.ConnectionId);
                    if (_connectionToSession.TryGetValue(evt.ConnectionId, out session))
                    {
                        _connectionToSession.Remove(evt.ConnectionId);
                        if (session != null && session.ControlConnectionId == evt.ConnectionId)
                        {
                            // 绑定的控制连接消失 ⇒ 解绑。重连策略：只有在**这一步之后**
                            // 新的连接才允许通过身份/MAC 校验占据该会话的控制连接
                            // （断开期间发生的第二条连接一律按冒用处理，见 HandleInbound）。
                            session.ControlConnectionId = 0;
                            Info("会话 " + session.MatchId + " 的控制连接已断开（id=" + evt.ConnectionId
                                + "，reason=" + (evt.Reason ?? "unknown") + "），等待重连");
                        }
                    }
                }
            }

            // 未认证连接超时：不给对端无限挂起的机会（有界）。
            List<int> idle = null;
            long now = _clock.UtcNowUnixMilliseconds;
            lock (_statusGate)
            {
                foreach (KeyValuePair<int, long> pair in _connectionOpenedAt)
                {
                    if (_connectionToSession.ContainsKey(pair.Key))
                    {
                        continue;
                    }

                    if (now - pair.Value >= _options.UnboundConnectionTimeoutMilliseconds)
                    {
                        if (idle == null)
                        {
                            idle = new List<int>();
                        }

                        idle.Add(pair.Key);
                    }
                }
            }

            if (idle != null)
            {
                for (int i = 0; i < idle.Count; i++)
                {
                    UnboundConnectionsClosedByTimeout++;
                    _listener.CloseConnection(idle[i], "未在超时内通过身份/MAC 校验");
                }
            }
        }

        private void DrainInbound()
        {
            for (int i = 0; i < InboundBudgetPerPump; i++)
            {
                PMDsControlInbound inbound;
                if (!_listener.TryDequeueInbound(out inbound))
                {
                    return;
                }

                HandleInbound(inbound);
            }
        }

        /// <summary>
        /// 处理一条已解帧的控制载荷。
        ///
        /// 选路分两段，且**刻意区分「选路」与「授权」**：
        /// 1. 解出信封身份（MatchId/DsId/Epoch）用来找候选会话 —— 这一步**未认证**，不能据此改状态；
        /// 2. 交给候选协调器 <c>OnControlPayload</c> 做 MAC 校验与状态推进；
        ///    只有在 MAC 通过之后，连接才被允许**占据**该会话的控制连接。
        /// </summary>
        private void HandleInbound(PMDsControlInbound inbound)
        {
            PMDsControlMessage envelope;
            try
            {
                envelope = PMDsControlCodec.Decode(inbound.Payload, 0, inbound.Length);
            }
            catch (Exception)
            {
                // 载荷结构非法：连接不获任何信任。
                _listener.CloseConnection(inbound.ConnectionId, "控制载荷结构非法");
                return;
            }

            string key = SessionKey(envelope.MatchId, envelope.DsId, envelope.Epoch);

            LobbySession session;
            lock (_statusGate)
            {
                _sessions.TryGetValue(key, out session);
            }

            if (session == null)
            {
                InboundFramesDroppedUnknownSession++;
                return;
            }

            // 已经绑定过的连接只能服务于它绑定的会话（防止一条连接改身份顶替别的会话）。
            if (session.ControlConnectionId != 0 && session.ControlConnectionId != inbound.ConnectionId)
            {
                HijackAttempts++;
                Warn("拒绝第二控制连接冒用会话 " + session.MatchId + "：已绑定 id="
                    + session.ControlConnectionId + "，新来 id=" + inbound.ConnectionId);
                _listener.CloseConnection(inbound.ConnectionId, "该会话已有控制连接");
                return;
            }

            PMDsCoordinatorReply reply = session.Coordinator.OnControlPayload(
                inbound.Payload, 0, inbound.Length);

            if (reply.Outcome == PMDsCoordinatorOutcome.RejectedMac)
            {
                InboundFramesDroppedMacFailed++;
                if (session.ControlConnectionId == 0)
                {
                    // 首条消息就没通过 MAC：这条连接不获得会话控制权，直接关闭（fail-closed）。
                    _listener.CloseConnection(inbound.ConnectionId, "首个控制消息未通过 MAC 校验");
                }

                return;
            }

            if (reply.Outcome == PMDsCoordinatorOutcome.RejectedMalformed)
            {
                InboundFramesDroppedMalformed++;
                if (session.ControlConnectionId == 0)
                {
                    // 结构非法（含验签之后的未知消息类型）：同样不允许占据控制连接。
                    _listener.CloseConnection(inbound.ConnectionId, "控制消息结构非法");
                }

                return;
            }

            InboundFramesDispatched++;

            if (session.ControlConnectionId == 0)
            {
                // 身份/MAC 校验通过才允许占据控制连接（契约 §7.3）。
                session.ControlConnectionId = inbound.ConnectionId;
                lock (_statusGate)
                {
                    _connectionToSession[inbound.ConnectionId] = session;
                }

                FlushPendingOutbound(session);
                Info("会话 " + session.MatchId + " 的控制连接已绑定：id=" + inbound.ConnectionId);
            }
        }

        private void DrainCommands()
        {
            for (int i = 0; i < CommandBudgetPerPump; i++)
            {
                LobbyCommand command;
                if (!_commands.TryDequeue(out command))
                {
                    return;
                }

                switch (command.Kind)
                {
                    case LobbyCommandKind.StartMatch:
                        ProcessStartMatch(command);
                        break;

                    case LobbyCommandKind.ClientDisconnected:
                        ProcessClientDisconnected(command.Uid);
                        break;
                }
            }
        }

        private void ProcessStartMatch(LobbyCommand command)
        {
            PMDsLobbyMatchRequest request = new PMDsLobbyMatchRequest();
            request.MatchId = command.MatchId;
            request.Roster = command.Roster;
            request.FightPattern = command.FightPattern;
            request.CollisionDigest = command.CollisionDigest;
            request.RequestId = command.RequestId;

            uint epoch = _epochSequence + 1u;
            if (epoch == 0u)
            {
                epoch = 1u;
            }

            _epochSequence = epoch;

            string dsId = BuildDsId(request.MatchId, epoch);
            string key = SessionKey(request.MatchId, dsId, epoch);
            string bootstrapPath = BuildBootstrapPath(request.MatchId, epoch);
            uint collisionDigest = request.CollisionDigest != 0u
                ? request.CollisionDigest : _options.CollisionDigest;
            if (collisionDigest == 0u)
            {
                RejectUnregistered(request.MatchId, "碰撞摘要为 0（必须由 PMR3Runtime.CollisionDigest 或调用方提供）");
                return;
            }

            LobbySession session = new LobbySession();
            session.MatchId = request.MatchId;
            session.DsId = dsId;
            session.Epoch = epoch;
            session.Key = key;
            session.Roster = (PMDsRosterIdentity[])request.Roster.Clone();
            session.BootstrapFilePath = bootstrapPath;
            session.CollisionDigest = collisionDigest;
            PMDsCoordinatorOptions coordinatorOptions = new PMDsCoordinatorOptions();
            coordinatorOptions.PortRangeFirst = _options.PortRangeFirst;
            coordinatorOptions.PortRangeLast = _options.PortRangeLast;
            coordinatorOptions.ProtocolHash = _options.ProtocolHash;
            coordinatorOptions.MaxRosterPlayers = _options.MaxRosterPlayers;
            coordinatorOptions.StartTimeout = _options.StartTimeout;
            coordinatorOptions.HeartbeatTimeout = _options.HeartbeatTimeout;
            coordinatorOptions.ResultAckRetryInterval = _options.ResultAckRetryInterval;
            coordinatorOptions.ShutdownGracePeriod = _options.ShutdownGracePeriod;
            coordinatorOptions.ProcessExitConfirmWait = _options.ProcessExitConfirmWait;
            coordinatorOptions.KillRetryInterval = _options.KillRetryInterval;

            PMDsCoordinator coordinator = new PMDsCoordinator(coordinatorOptions, _clock, _launcher,
                new SessionSink(this, session), _ports, _ledger);
            session.Coordinator = coordinator;

            PMDsAllocationRequest allocation = new PMDsAllocationRequest();
            allocation.MatchId = request.MatchId;
            allocation.DsId = dsId;
            allocation.Epoch = epoch;
            allocation.ProtocolHash = _options.ProtocolHash;
            allocation.CollisionDigest = collisionDigest;
            allocation.Roster = (PMDsRosterIdentity[])request.Roster.Clone();
            allocation.BootstrapFilePath = bootstrapPath;
            allocation.Process = new PMDsProcessLaunchRequest(
                _options.DsExecutablePath, _options.DsWorkingDirectory, BuildBaseArguments(dsId, request.MatchId));

            PMDsCoordinatorReply allocated = coordinator.Allocate(allocation);
            if (!allocated.IsAccepted || coordinator.State != PMDsSessionState.Allocated)
            {
                // 注意：协调器的失败路径也会返回 Accepted（它把状态推到终态后返回 Applied），
                // 因此判定必须看**状态**而不是只看 outcome，否则会把「启动失败」误当成成功开局。
                RejectUnregistered(request.MatchId, "分配失败：" + allocated);
                return;
            }

            // 分配成功（端口 + uid 账本已预留）之后才把会话登记进宿主视图。
            // 登记早了会在分配失败时留下一个永远不会被回收的条目。
            lock (_statusGate)
            {
                _sessions[key] = session;
            }

            // 引导文件必须先**安全发布**，然后才能启动进程（因果，而不是调用顺序纪律）。
            byte[] document;
            try
            {
                document = coordinator.ExportBootstrapDocument();
            }
            catch (Exception ex)
            {
                RejectSession(session, "引导内容导出失败：" + ex.GetType().Name);
                return;
            }

            string publishError;
            if (!TryPublishBootstrapFile(bootstrapPath, document, out publishError))
            {
                BootstrapPublishFailures++;
                RejectSession(session, "引导文件发布失败：" + publishError);
                return;
            }

            BootstrapFilesPublished++;

            string controlEndpoint = _options.ListenAddress + ":" + _listener.BoundPort;
            PMDsProcessLaunchRequest finalLaunch = new PMDsProcessLaunchRequest(
                _options.DsExecutablePath, _options.DsWorkingDirectory,
                BuildFinalArguments(dsId, request.MatchId, bootstrapPath, controlEndpoint, coordinator.AllocatedPort));

            PMDsCoordinatorReply launchReply = coordinator.SetLaunchRequest(finalLaunch);
            if (!launchReply.IsAccepted || coordinator.State != PMDsSessionState.Allocated)
            {
                RejectSession(session, "启动参数校验失败：" + launchReply);
                return;
            }

            PMDsCoordinatorReply started = coordinator.BeginStart();
            if (!started.IsAccepted || coordinator.State != PMDsSessionState.Starting)
            {
                // 同上：BeginStart 失败时协调器会把状态推到 Failed 并归还资源，但 outcome 仍是 Applied。
                RejectSession(session, "进程启动失败：" + started);
                return;
            }

            SessionStartsAccepted++;

            PMDsLobbySessionRecord record = new PMDsLobbySessionRecord();
            record.MatchId = request.MatchId;
            record.DsId = dsId;
            record.Epoch = epoch;
            record.Port = coordinator.AllocatedPort;
            record.ProcessId = coordinator.ProcessId;

            lock (_statusGate)
            {
                _startedSessions.Add(record);
                if (_startedSessions.Count > 64)
                {
                    _startedSessions.RemoveAt(0);
                }
            }

            Info("新链开局已落地：" + record + " pattern=" + request.FightPattern);

            Action<PMDsLobbySessionRecord> handler = SessionStarted;
            if (handler != null)
            {
                try
                {
                    handler(record);
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>分配前就失败的拒绝（会话尚未登记，无需回滚）。</summary>
        private void RejectUnregistered(string matchId, string reason)
        {
            SessionStartsRejected++;
            Warn("新链开局失败（match=" + matchId + "）：" + reason);

            Action<string, string> handler = SessionFailed;
            if (handler != null)
            {
                try
                {
                    handler(matchId, reason);
                }
                catch (Exception)
                {
                }
            }
        }

        private void RejectSession(LobbySession session, string reason)
        {
            SessionStartsRejected++;
            Warn("新链开局失败（match=" + session.MatchId + "）：" + reason);

            // 分配态/启动态失败都由协调器自行收尾：Allocated 无进程会立即归还端口与 uid；
            // Starting 之后的失败会经 Kill + 确认退出再释放。
            try
            {
                session.Coordinator.RequestShutdown(9u);
            }
            catch (Exception ex)
            {
                Warn("失败回滚时请求关闭异常（match=" + session.MatchId + "）：" + ex.GetType().Name);
            }

            TryDeleteBootstrapFile(session.BootstrapFilePath);

            Action<string, string> handler = SessionFailed;
            if (handler != null)
            {
                try
                {
                    handler(session.MatchId, reason);
                }
                catch (Exception)
                {
                }
            }
        }

        private void ProcessClientDisconnected(int uid)
        {
            string matchId;
            if (!_ledger.TryGetMatchId(uid, out matchId))
            {
                return;
            }

            LobbySession session = null;
            lock (_statusGate)
            {
                foreach (KeyValuePair<string, LobbySession> pair in _sessions)
                {
                    if (string.Equals(pair.Value.MatchId, matchId, StringComparison.Ordinal))
                    {
                        session = pair.Value;
                        break;
                    }
                }
            }

            if (session == null || session.Coordinator.IsTerminal)
            {
                return;
            }

            ClientDisconnectAborts++;
            Info("客户端断线（uid=" + uid + "，match=" + matchId + "）：中止该测试局，不伪造正常胜利");

            // 不伪造胜利：直接请 DS 收尾，没有 Result 就没有 Result。
            session.Coordinator.RequestShutdown(7u);
        }

        /// <summary>应用「后置动作」——在协调器回调里只置位，统一在这一步落地，避免深层重入。</summary>
        private void ApplyDeferredWork()
        {
            List<LobbySession> snapshot;
            lock (_statusGate)
            {
                snapshot = new List<LobbySession>(_sessions.Values);
            }

            for (int i = 0; i < snapshot.Count; i++)
            {
                LobbySession session = snapshot[i];
                if (session.EntryPublishPending && !session.EntryPublished)
                {
                    PublishEntryOffers(session);
                }
            }

            if (_pendingResultNotices.Count > 0)
            {
                PMDsLobbyResultNotice[] notices = _pendingResultNotices.ToArray();
                _pendingResultNotices.Clear();
                for (int i = 0; i < notices.Length; i++)
                {
                    Action<PMDsLobbyResultNotice> handler = ResultAccepted;
                    if (handler != null)
                    {
                        try
                        {
                            handler(notices[i]);
                        }
                        catch (Exception ex)
                        {
                            Warn("结果事件处理异常（match=" + notices[i].MatchId + "）：" + ex.GetType().Name);
                        }
                    }

                    try
                    {
                        _gateway.NotifyMatchEnded(notices[i]);
                    }
                    catch (Exception ex)
                    {
                        Warn("结果通知失败（match=" + notices[i].MatchId + "）：" + ex.GetType().Name);
                    }
                }
            }
        }

        /// <summary>
        /// Ready 之后把入局通知发给**当前同一认证客户端**（每人一张票据）。
        /// 任何一人发不出去即整局判失败（不缩编、不伪装成功）。
        /// </summary>
        private void PublishEntryOffers(LobbySession session)
        {
            session.EntryPublishPending = false;

            long nowSeconds = _clock.UtcNowUnixMilliseconds / 1000L;

            for (int i = 0; i < session.Roster.Length; i++)
            {
                PMDsRosterIdentity identity = session.Roster[i];
                if (!_gateway.IsClientAuthenticated(identity.Uid))
                {
                    EntryOfferFailures++;
                    RejectSession(session, "uid " + identity.Uid + " 在就绪前掉线，整局失败（不缩编）");
                    return;
                }

                byte[] ticket;
                try
                {
                    ticket = session.Coordinator.IssueTicket(identity.Uid, nowSeconds).ExportTicketBytes();
                }
                catch (Exception ex)
                {
                    EntryOfferFailures++;
                    RejectSession(session, "签发入场票据失败（uid=" + identity.Uid + "）：" + ex.GetType().Name);
                    return;
                }

                PMDsLobbyEntryNotice notice = new PMDsLobbyEntryNotice();
                notice.MatchId = session.MatchId;
                notice.DsId = session.DsId;
                notice.Host = _options.ListenAddress;
                notice.Port = session.Coordinator.AllocatedPort;
                notice.Epoch = session.Epoch;
                notice.ProtocolHash = session.Coordinator.ProtocolHash;
                notice.CollisionDigest = session.CollisionDigest;
                notice.Identity = identity;
                notice.Ticket = ticket;

                string error;
                if (!_gateway.TrySendEntryOffer(notice, out error))
                {
                    EntryOfferFailures++;
                    // 注意：此处可能已经给前面的玩家发过 offer（部分投递）。
                    // 我们**不**把这种局面包装成成功：直接中止该局，让已通知的客户端在连 DS 时失败，
                    // 而不是假装 6 个人都能进。
                    RejectSession(session, "入局通知发送失败（uid=" + identity.Uid + "）：" + error);
                    return;
                }

                EntryOffersSent++;
            }

            session.EntryPublished = true;
            session.Coordinator.MarkRunning();
            Info("会话 " + session.MatchId + " 已发布入局通知给 " + session.Roster.Length
                + " 名玩家并进入 Running（port=" + session.Coordinator.AllocatedPort + "）");
        }

        private void TickSessions()
        {
            List<LobbySession> snapshot;
            lock (_statusGate)
            {
                snapshot = new List<LobbySession>(_sessions.Values);
            }

            for (int i = 0; i < snapshot.Count; i++)
            {
                try
                {
                    snapshot[i].Coordinator.Tick();
                }
                catch (Exception ex)
                {
                    Warn("会话 Tick 异常（match=" + snapshot[i].MatchId + "）：" + ex.GetType().Name);
                }
            }
        }

        private void ReapSessions()
        {
            List<LobbySession> doomed = null;
            lock (_statusGate)
            {
                foreach (KeyValuePair<string, LobbySession> pair in _sessions)
                {
                    if (pair.Value.Released)
                    {
                        if (doomed == null)
                        {
                            doomed = new List<LobbySession>();
                        }

                        doomed.Add(pair.Value);
                    }
                }

                if (doomed != null)
                {
                    for (int i = 0; i < doomed.Count; i++)
                    {
                        _sessions.Remove(doomed[i].Key);
                    }
                }
            }

            if (doomed == null)
            {
                return;
            }

            for (int i = 0; i < doomed.Count; i++)
            {
                LobbySession session = doomed[i];

                // 「确认退出再恢复 PlayerOnline / 释放 uid」——uid 已由协调器在同点释放，
                // 这里只负责恢复大厅在线状态（与断线/顶号无关的第三件事）。
                for (int j = 0; j < session.Roster.Length; j++)
                {
                    try
                    {
                        _gateway.RestorePlayerOnline(session.Roster[j].Uid);
                    }
                    catch (Exception ex)
                    {
                        Warn("恢复 PlayerOnline 失败（uid=" + session.Roster[j].Uid + "）：" + ex.GetType().Name);
                    }
                }

                TryDeleteBootstrapFile(session.BootstrapFilePath);

                Action<string, PMDsSessionState, string> handler = SessionReleased;
                if (handler != null)
                {
                    try
                    {
                        handler(session.MatchId, session.LastState, session.LastReason);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        // ────────────────────────────────────────────────────────────────
        // 协调器副作用（SessionSink 转发；只置位 + 路由控制消息，不做深层重入）
        // ────────────────────────────────────────────────────────────────

        internal void HandleEffect(LobbySession session, PMDsCoordinatorEvent effect)
        {
            if (session == null)
            {
                return;
            }

            switch (effect.Effect)
            {
                case PMDsCoordinatorEffect.ReadyAddressPublished:
                    // 就绪只是「可以发地址了」；真正发出去由 ApplyDeferredWork 完成。
                    session.EntryPublishPending = true;
                    break;

                case PMDsCoordinatorEffect.ControlMessageOut:
                case PMDsCoordinatorEffect.GracefulShutdownRequested:
                    SendControlPayload(session, effect.ControlPayload);
                    break;

                case PMDsCoordinatorEffect.ResultAccepted:
                    RecordResult(session, effect);
                    break;

                case PMDsCoordinatorEffect.SessionEnded:
                    session.LastState = effect.State;
                    session.LastReason = effect.Reason;
                    break;

                case PMDsCoordinatorEffect.ResourcesReleased:
                    session.Released = true;
                    break;
            }
        }

        private void SendControlPayload(LobbySession session, byte[] payload)
        {
            if (payload == null || payload.Length == 0)
            {
                return;
            }

            int connectionId = session.ControlConnectionId;
            if (connectionId != 0 && _listener.TrySendFrame(connectionId, payload, 0, payload.Length))
            {
                return;
            }

            if (session.PendingOutbound.Count < PendingOutboundLimit)
            {
                session.PendingOutbound.Enqueue(payload);
                return;
            }

            ControlFramesUndeliverable++;
            Warn("控制消息无法投递（match=" + session.MatchId + "）：无绑定连接或队列已满");
        }

        private void FlushPendingOutbound(LobbySession session)
        {
            while (session.PendingOutbound.Count > 0)
            {
                byte[] payload = session.PendingOutbound.Dequeue();
                if (!_listener.TrySendFrame(session.ControlConnectionId, payload, 0, payload.Length))
                {
                    ControlFramesUndeliverable++;
                    return;
                }
            }
        }

        private void RecordResult(LobbySession session, PMDsCoordinatorEvent effect)
        {
            PMDsLobbyResultNotice notice = new PMDsLobbyResultNotice();
            notice.MatchId = session.MatchId;
            notice.DsId = session.DsId;
            notice.Epoch = session.Epoch;
            notice.ResultId = effect.ResultId;
            notice.WinnerTeamId = effect.WinnerTeamId;
            notice.Summary = effect.ResultSummary ?? new byte[0];
            notice.Roster = session.Roster;

            lock (_statusGate)
            {
                _pendingResultNotices.Add(notice);
            }

            Info("会话 " + session.MatchId + " 收到权威结果：resultId=" + effect.ResultId
                + " winner=" + effect.WinnerTeamId + "（只做控制结果通知，不伪造 BattleReview）");
        }

        // ────────────────────────────────────────────────────────────────
        // 启动参数与引导文件
        // ────────────────────────────────────────────────────────────────

        private static string[] BuildBaseArguments(string dsId, string matchId)
        {
            return new string[]
            {
                "-batchmode",
                "-nographics",
                "-server",
                "-dsid", dsId,
                "-matchid", matchId,
            };
        }

        /// <summary>
        /// 组装真实启动参数：**固定 exe + 逐个参数**，端口来自 Allocate 后的真实值（不硬编码），
        /// 密钥/票据一律不进命令行（只给引导文件路径）。
        /// </summary>
        private static string[] BuildFinalArguments(string dsId, string matchId, string bootstrapPath,
            string controlEndpoint, int port)
        {
            return new string[]
            {
                "-batchmode",
                "-nographics",
                "-server",
                "-dsid", dsId,
                "-matchid", matchId,
                "-bootstrap", bootstrapPath,
                "-control", controlEndpoint,
                "-port", port.ToString(),
            };
        }

        private string BuildDsId(string matchId, uint epoch)
        {
            // DsId 同样受 128 字节上限约束；截断时保留 epoch 后缀（它是会话身份的一部分）。
            string sanitized = SanitizeSegment(matchId);
            string suffix = "-ds-" + epoch.ToString();
            int maxBody = PMDsControlWire.MaxDsIdBytes - suffix.Length;
            if (maxBody < 1)
            {
                maxBody = 1;
            }

            if (sanitized.Length > maxBody)
            {
                sanitized = sanitized.Substring(0, maxBody);
            }

            return sanitized + suffix;
        }

        private string BuildBootstrapPath(string matchId, uint epoch)
        {
            string directory = Path.Combine(_options.BootstrapRootDirectory, SanitizeSegment(matchId));
            return Path.Combine(directory, "bootstrap-" + epoch + ".bin");
        }

        /// <summary>
        /// 路径段净化：只保留字母/数字/下划线/减号/点，其余替换为 <c>_</c>。
        /// 这样 matchId 再脏也不会逃逸出配置的每局目录（不做路径推断）。
        /// </summary>
        private static string SanitizeSegment(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "match";
            }

            StringBuilder builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool ok = (c >= '0' && c <= '9')
                    || (c >= 'a' && c <= 'z')
                    || (c >= 'A' && c <= 'Z')
                    || c == '_' || c == '-' || c == '.';
                builder.Append(ok ? c : '_');
            }

            string sanitized = builder.ToString();
            if (sanitized.Length == 0 || sanitized == "." || sanitized == "..")
            {
                return "match";
            }

            return sanitized;
        }

        /// <summary>
        /// 临时文件 + 原子改名发布引导文件。任一步失败都清掉临时文件并返回 false（调用方据此回滚）。
        ///
        /// **本机信任边界（有意）**：文件落在 <see cref="PMDsLobbyHostOptions.BootstrapRootDirectory"/> 下的
        /// 每局子目录，依靠同机同账户的文件系统权限保护（Windows 下继承父目录 ACL）。
        /// 本实现不做 DPAPI/自加密 —— 因为密钥就是给同机 DS 进程读的，
        /// 「同账户可读」与「必须可读」是同一件事；换成别的账户跑 DS 需自行收紧目录 ACL。
        /// 写入用 <c>Flush(true)</c> 落盘后再改名，保证 DS 只会看到「不存在」或「完整」两种状态。
        /// </summary>
        internal static bool TryPublishBootstrapFile(string path, byte[] document, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path))
            {
                error = "引导文件路径为空";
                return false;
            }

            if (document == null || document.Length == 0)
            {
                error = "引导内容为空";
                return false;
            }

            string directory = Path.GetDirectoryName(path);
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");

            try
            {
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(document, 0, document.Length);
                    stream.Flush(true);
                }

                if (File.Exists(path))
                {
                    File.Replace(temporary, path, null);
                }
                else
                {
                    File.Move(temporary, path);
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + " " + ex.Message;
                try
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
                catch (Exception)
                {
                }

                return false;
            }
        }

        private static void TryDeleteBootstrapFile(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
            }
        }

        private static string SessionKey(string matchId, string dsId, uint epoch)
        {
            return (matchId ?? string.Empty) + KeySeparator + (dsId ?? string.Empty) + KeySeparator
                + epoch.ToString();
        }

        private void Info(string message)
        {
            Action<string> log = Log;
            if (log != null)
            {
                try
                {
                    log("[PMDsLobbyHost] " + message);
                }
                catch (Exception)
                {
                }
            }
        }

        private void Warn(string message)
        {
            Info("WARN " + message);
        }

        // ────────────────────────────────────────────────────────────────
        // 内部类型
        // ────────────────────────────────────────────────────────────────

        private enum LobbyCommandKind : byte
        {
            StartMatch = 1,
            ClientDisconnected = 2,
        }

        private sealed class LobbyCommand
        {
            public LobbyCommandKind Kind;
            public string MatchId;
            public PMDsRosterIdentity[] Roster;
            public string FightPattern;
            public uint CollisionDigest;
            public long RequestId;
            public int Uid;
        }

        /// <summary>宿主侧的一局视图（协调器状态的影子 + 控制连接绑定）。</summary>
        internal sealed class LobbySession
        {
            public string MatchId = string.Empty;
            public string DsId = string.Empty;
            public uint Epoch;
            public string Key = string.Empty;
            public PMDsRosterIdentity[] Roster = new PMDsRosterIdentity[0];
            public PMDsCoordinator Coordinator;
            public string BootstrapFilePath = string.Empty;

            public int ControlConnectionId;
            public readonly Queue<byte[]> PendingOutbound = new Queue<byte[]>();

            public bool EntryPublishPending;
            public bool EntryPublished;
            public bool Released;
            public PMDsSessionState LastState;
            public string LastReason;

            public uint CollisionDigest;
        }

        /// <summary>每个会话一个的副作用接收器：把协调器事件绑定回具体会话，再交给宿主。</summary>
        private sealed class SessionSink : IPMDsCoordinatorSink
        {
            private readonly PMDsLobbyHost _host;
            private readonly LobbySession _session;

            public SessionSink(PMDsLobbyHost host, LobbySession session)
            {
                _host = host;
                _session = session;
            }

            public void OnEffect(PMDsCoordinatorEvent effect)
            {
                _host.HandleEffect(_session, effect);
            }
        }
    }
}
