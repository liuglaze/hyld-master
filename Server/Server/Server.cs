using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Server.Controller;
using MySql.Data.MySqlClient;
using Server.DAO;
using SocketProto;
using System.Reflection;
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

            // 创建udp线程
            LZJUDP.Instance.Init();

            // 启动tcp监听线程
            Thread myThread = new Thread(ListenClientConnect);
            myThread.IsBackground = true; // 设为后台线程，程序关掉时线程自动结束
            myThread.Start();

            // 检查ping
            Thread myThread1 = new Thread(OnTimer);
            myThread1.IsBackground = true;
            myThread1.Start();

            Logging.Debug.Log("启动监听{0}成功", Logging.Debug.LogSeverity.Info, _socket.LocalEndPoint.ToString());
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
    }
}