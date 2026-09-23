using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Server.Controller;
using Server.DAO;
using SocketProto;
using System.Linq; // 需要引用 Linq 用于快速复制列表

namespace Server
{
    class Server
    {
        private Socket _socket;

        // 增加一把全局锁，保护下面两个集合
        private readonly object _lock = new object();
        private List<Client> _clients = new List<Client>();
        private Dictionary<int, Client> _activeClient = new Dictionary<int, Client>();

        public ControllerManger _controllerManger;

        public EndPoint EndPoint
        {
            get { return _socket.LocalEndPoint; }
        }

        public Server(int port)
        {
            _controllerManger = new ControllerManger(this);

            IPAddress ip = IPAddress.Parse("0.0.0.0");
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _socket.Bind(new IPEndPoint(ip, port));
            _socket.Listen(10);

            // 启动tcp监听线程
            Thread myThread = new Thread(ListenClientConnect);
            myThread.IsBackground = true; // 设为后台线程，程序关掉时线程自动结束
            myThread.Start();

            // 检查ping
            Thread myThread1 = new Thread(OnTimer);
            myThread1.IsBackground = true;
            myThread1.Start();

            Logging.Debug.Log("启动监听{0}成功", Logging.Debug.LogSeverity.Info, _socket.LocalEndPoint.ToString());

            // 把「服务端实际支持哪些上行入口」固化到启动日志，便于跟客户端协议清单核对。
            _controllerManger.LogRegisteredHandlers();

            // 旧战斗链已退役（Docs/plans/net-legacy-retirement-contract.md §A）：服务端只保留
            // 「大厅 TCP + DS 编排」两个权威面。这里装载 DS 部署配置并拉起 Lobby 宿主；
            // 不存在「启用/禁用旧链」的开关，配置不全或 manifest 非法时匹配会显式失败。
            InitializeDedicatedServerLobby();
        }

        // --- 以下所有涉及集合的操作都加了锁 ---

        public Client GetActiveClient(int id)
        {
            lock (_lock)
            {
                if (!_activeClient.ContainsKey(id))
                {
                    return null;
                }
                return _activeClient[id];
            }
        }

        public void AddActiveClient(int id, Client client)
        {
            lock (_lock)
            {
                if (!_activeClient.ContainsKey(id))
                {
                    _activeClient.Add(id, client);
                }
            }
        }

        /// <summary>
        /// 把该连接登记为活跃玩家。幂等。
        ///
        /// 与 AddActiveClient 的区别：若同一 uid 已存在**另一条**连接，先移除旧连接再登记，
        /// 保证 _activeClient 里不会留下已被顶替的断线连接。
        ///
        /// 计划 B4：把原来散在 FindPlayerInfo 里的登记动作提取出来，
        /// 使 Login 与 FindPlayerInfo 都能调用同一套逻辑。
        /// </summary>
        public void RegisterActiveClient(Client client)
        {
            if (client == null)
            {
                return;
            }

            lock (_lock)
            {
                Client existing;
                if (_activeClient.TryGetValue(client.UID, out existing) && !ReferenceEquals(existing, client))
                {
                    _activeClient.Remove(client.UID);
                    Logging.Debug.Log($"[ACTIVE-REPLACE] id={client.UID} 旧连接被新连接顶替");
                }

                _activeClient[client.UID] = client;
            }

            Logging.Debug.Log($"[ACTIVE-ADD] id={client.UID} 玩家{client.PlayerName} 加入活跃字典");
        }

        public void RemoveActiveClient(Client client)
        {
            lock (_lock)
            {
                if (_activeClient.ContainsKey(client.UID))
                {
                    _activeClient.Remove(client.UID);
                }
            }
        }

        /// <summary>
        /// 在已完成登录的活跃玩家中按 UserName 查找。
        /// 用于 Login 重复登录检查，避免查 _clients 列表导致竞态误判。
        /// </summary>
        public Client GetActiveClientByUserName(string username)
        {
            lock (_lock)
            {
                foreach (var kvp in _activeClient)
                {
                    if (kvp.Value.UserName == username) return kvp.Value;
                }
            }
            return null;
        }

        public PlayerState GetPlayerState(int id)
        {
            // GetActiveClient 内部已经加锁了，所以这里不用重复加
            Client c = GetActiveClient(id);
            if (c != null)
            {
                return c.PlayerState;
            }
            return PlayerState.PlayerOutline;
        }

        public Client GetClientByUserName(string username)
        {
            lock (_lock)
            {
                foreach (Client client in _clients)
                {
                    if (client.UserName == username) return client;
                }
            }
            return null;
        }

        public Client GetClientByID(int id)
        {
            lock (_lock)
            {
                foreach (Client client in _clients)
                {
                    if (client.UID == id) return client;
                }
            }
            return null;
        }

        public Client GetClientByPlayerName(string playername)
        {
            lock (_lock)
            {
                foreach (Client client in _clients)
                {
                    if (client.PlayerName == playername) return client;
                }
            }
            return null;
        }

        // 回调函数，给Client调用
        public void HandleRequest(MainPack pack, Client client)
        {
            _controllerManger.HandleRequest(pack, client);
        }

        // 回调函数，给Client调用
        public void RemoveClient(Client client)
        {
            lock (_lock)
            {
                _clients.Remove(client);
            }
        }

        /// <summary> 
        /// 监听客户端连接 
        /// </summary> 
        private void ListenClientConnect()
        {
            while (true)
            {
                try
                {
                    Socket clientSocket = _socket.Accept();
                    string remoteEndpoint = clientSocket.RemoteEndPoint?.ToString() ?? "unknown";
                    Client client = new Client(clientSocket, this);

                    // 加锁添加新客户端
                    lock (_lock)
                    {
                        _clients.Add(client);
                    }
                    Logging.Debug.Log("新客户端连接: " + remoteEndpoint);
                }
                catch (Exception ex)
                {
                    Logging.Debug.Log(ex);
                }
            }
        }

        #region EventHandler回调
        public void OnTimer()
        {
            while (true)
            {
                Thread.Sleep(1000);
                if (Tool.PingPongTool.isUserPing)
                    CheckPing();
            }
        }

        public void CheckPing()
        {
            // 重点修改：快照模式
            // 为了避免锁住整个列表太久，我们先把当前所有的客户端复制一份出来
            // 这样我们在遍历检查时，不会影响新玩家进来(Add)或者旧玩家断开(Remove)
            List<Client> snapshot;
            lock (_lock)
            {
                snapshot = _clients.ToList(); // 复制一份
            }

            long timenow = Tool.PingPongTool.GetTimeStamp();

            // 遍历复本
            foreach (Client c in snapshot)
            {
                if (timenow - c.lastPingTime > Tool.PingPongTool.pingInterval * 4)
                {
                    Logging.Debug.Log("Ping Timeout Close " + c._socket.RemoteEndPoint.ToString());
                    c.Close(); // Close 内部会调用 RemoveClient，那里会再次加锁，这是安全的
                }
            }
        }
        #endregion

        #region R3-B：新链（专用服务器）宿主

        /// <summary>
        /// DS 宿主初始化（旧战斗链退役后是**唯一**开局链路）。
        ///
        /// 关键口径：
        /// - 新链是默认且唯一的链路：不读 <c>HYLD_PMNET_DS</c> 之类的选链开关，显式 0 也不会恢复旧链
        ///   （旧开关被忽略，并在启动日志里明确记录）；
        /// - 部署配置不全或内容 manifest 缺失/非法 ⇒ **不启动宿主** ⇒ 匹配层 **显式失败**
        ///   （契约 §7「失败只能报失败」，已不存在可回退的旧链）；
        /// - 协议摘要从 Unity 组产出的 <c>PMR3Runtime</c> 取，**不接受客户端提供的 hash**；
        /// - R4-C / C3：**碰撞摘要不再固定为诊断保留值**，而是从正式内容 manifest
        ///   （<c>Client/Assets/Resources/PMNet/BattleContentV1.json</c>，或
        ///   <c>HYLD_PMNET_CONTENT_MANIFEST</c> 指定的绝对路径）读取并强校验后取
        ///   <c>collisionDigest</c>；manifest 缺失/非法 => **拒绝拉局**，不回落诊断内容；
        /// - 部署路径/端口范围全部来自配置/环境变量，本方法**不猜**任何用户目录。
        /// </summary>
        private void InitializeDedicatedServerLobby()
        {
            PMNet.Control.PMDsLobbyHostOptions options = PMNet.Control.PMDsLobbyHostOptions.FromEnvironment();

            // 旧选链开关已被忽略（旧链代码已删）：显式记一条日志，避免运维以为还能回退。
            if (options.DeprecatedLegacySwitchIgnored)
            {
                Logging.Debug.Log("[PMDsLobby] 忽略旧开关 HYLD_PMNET_DS=" + options.DeprecatedLegacySwitchValue
                    + "：旧战斗链已退役，DS 新链是唯一开局链路（显式 0 也不会恢复旧链）");
            }

            // Enabled 由 FromEnvironment 恒置 true（production 只走新链）。
            // 保留该判断只为「显式禁用宿主」的测试语义：宿主不启动 ⇒ 匹配显式失败，不回退旧链。
            if (!options.Enabled)
            {
                Logging.Debug.Log("[PMDsLobby] 宿主被显式禁用：匹配将显式失败（无旧链可回退）");
                return;
            }

            if (!options.IsLaunchConfigured)
            {
                Logging.Debug.Log("[PMDsLobby] DS 启动配置不完整（需要 HYLD_PMNET_DS_EXE / "
                    + "HYLD_PMNET_DS_WORKDIR / HYLD_PMNET_DS_BOOTSTRAP_DIR / 合法端口范围）；"
                    + "不会拉起任何一局（匹配将显式失败，不回退旧链）");
                return;
            }

            // 声明产物（Unity 组）注册：hash 是内部真值，不来自业务包。
            PMNet.R3.PMR3Runtime.Register();
            options.ProtocolHash = PMNet.R3.PMR3Runtime.ProtocolHash;

            // R4-C / C3：碰撞摘要来自**正式内容 manifest**（不再固定诊断保留值 0x52334201）。
            //
            // 失败即 "拒绝拉局"：这里直接 return（宿主不启动），
            // 因此匹配入口会显式失败而不是静默回落旧链/诊断内容。
            // 0 与保留诊断值都在 PMDsBattleContentConfig 里被拒绝（正式内容不得撞保留值）。
            PMNet.Control.PMDsBattleContentInfo content;
            string contentError;
            if (!PMNet.Control.PMDsBattleContentConfig.TryLoad(out content, out contentError))
            {
                Logging.Debug.Log("[PMDsLobby] 正式内容 manifest 不可用，拒绝新链启动（不回退诊断摘要）：" + contentError);
                return;
            }

            options.CollisionDigest = content.CollisionDigest;

            PMNet.Control.PMDsProcessWhitelist whitelist = new PMNet.Control.PMDsProcessWhitelist();
            whitelist.Add(options.DsExecutablePath);
            PMNet.Control.PMDsSystemProcessLauncher launcher =
                new PMNet.Control.PMDsSystemProcessLauncher(whitelist);

            PMNet.Control.PMDsLobbyHost host = new PMNet.Control.PMDsLobbyHost(
                options, PMNet.Control.PMDsSystemClock.Instance, launcher, new ServerLobbyClientGateway(this));
            host.Log = delegate(string message)
            {
                Logging.Debug.Log(message);
            };

            string error;
            if (!host.Start(out error))
            {
                Logging.Debug.Log("[PMDsLobby] 宿主启动失败（匹配将显式失败，不回退旧链）：" + error);
                return;
            }

            PMNet.Control.PMDsLobbyHost.Instance = host;
            Logging.Debug.Log("[PMDsLobby] 新链宿主已启动：control=127.0.0.1:" + host.ControlPort
                + " protocolHash=0x" + options.ProtocolHash.ToString("X8")
                + " collisionDigest=0x" + options.CollisionDigest.ToString("X8")
                + " worldVersion=" + content.WorldVersion
                + " contentDigest=" + content.ContentDigest
                + " manifest=" + content.ManifestPath);
        }

        /// <summary>
        /// Lobby 宿主 → 已认证客户端的**生产**网关。
        ///
        /// 契约 §7.1：入局信息走现有已认证 Lobby TCP 的 <c>StartEnterBattle</c>，
        /// <c>MainPack.Str</c> = 前缀 + Base64(<c>PMDsEntryCodec.Encode(PMDsEntryOffer)</c>)。
        /// 这里**不另写一套编码**：未就绪之前不得用替身充数（offer/完整 Str/票据均不入日志）。
        /// </summary>
        private sealed class ServerLobbyClientGateway : PMNet.Control.IPMDsLobbyClientGateway
        {
            private readonly Server _server;

            public ServerLobbyClientGateway(Server server)
            {
                _server = server;
            }

            public bool IsClientAuthenticated(int uid)
            {
                Client client = _server.GetActiveClient(uid);
                return client != null && !string.IsNullOrEmpty(client.UserName);
            }

            public bool TrySendEntryOffer(PMNet.Control.PMDsLobbyEntryNotice notice, out string error)
            {
                Client client = _server.GetActiveClient(notice.Identity.Uid);
                if (client == null)
                {
                    error = "该 uid 当前没有活跃连接";
                    return false;
                }

                PMNet.Session.PMDsEntryOffer offer = new PMNet.Session.PMDsEntryOffer();
                offer.MatchId = notice.MatchId;
                offer.DsId = notice.DsId;
                offer.Host = notice.Host;
                offer.Epoch = notice.Epoch;
                offer.ProtocolHash = notice.ProtocolHash;
                offer.CollisionDigest = notice.CollisionDigest;
                offer.Port = notice.Port;
                offer.Identity = notice.Identity;
                offer.Ticket = notice.Ticket;

                string text;
                try
                {
                    text = PMNet.Session.PMDsEntryCodec.Encode(offer);
                }
                catch (Exception ex)
                {
                    // 错误信息不得携带 offer / 票据字节。
                    error = "入局信息编码失败：" + ex.GetType().Name;
                    return false;
                }

                MainPack pack = new MainPack();
                pack.Requestcode = RequestCode.Matching;
                pack.Returncode = ReturnCode.Succeed;
                pack.Actioncode = ActionCode.StartEnterBattle;
                pack.Str = text;
                client.Send(pack);

                // 只记公开对账字段，不记 offer / 完整 Str / 票据。
                Logging.Debug.Log("[PMDsLobby] 入局通知已发送 uid=" + notice.Identity.Uid
                    + " match=" + notice.MatchId + " ds=" + notice.DsId
                    + " port=" + notice.Port + " epoch=" + notice.Epoch);
                error = null;
                return true;
            }

            public void NotifyMatchEnded(PMNet.Control.PMDsLobbyResultNotice notice)
            {
                // 契约 §7.3 / 任务口径：优先只记录公开 event；**不伪造 BattleReview 帧历史**（回放属 R6）。
                Logging.Debug.Log("[PMDsLobby] 对局结果已受理 match=" + notice.MatchId
                    + " resultId=" + notice.ResultId + " winner=" + notice.WinnerTeamId
                    + " summaryBytes=" + (notice.Summary == null ? 0 : notice.Summary.Length)
                    + "（不发送 BattleReview）");
            }

            public void RestorePlayerOnline(int uid)
            {
                Client client = _server.GetActiveClient(uid);
                if (client != null)
                {
                    client.PlayerState = PlayerState.PlayerOnline;
                }
            }
        }

        #endregion
    }
}