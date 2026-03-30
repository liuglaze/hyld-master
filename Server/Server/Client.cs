using System;
using System.Net.Sockets;
using Server.Tool;
using Server.DAO;
using SocketProto;
using MySql.Data.MySqlClient;
using System.Collections.Generic;
using System.Threading;

namespace Server
{
    /// <summary>
    /// 拥有Send，Receive操作异步接受消息，同步发送消息
    /// </summary>
    class Client
    {
        public Socket _socket { get; private set; }
        public long lastPingTime = 0;
        private Message _message;
        private UserData _userdata;
        private Server _server;

        private MySqlConnection _mysqlConnection;
        
        public FriendRoom FriendRoom
        {
            get; set;
        }
        public Dictionary<int, Client> FriendsDic = new Dictionary<int, Client>();
        private readonly object _friendsLock = new object();

        public List<Client> GetFriendsSnapshot()
        {
            lock (_friendsLock)
            {
                return new List<Client>(FriendsDic.Values);
            }
        }

        public bool RemoveFriend(int uid)
        {
            lock (_friendsLock)
            {
                return FriendsDic.Remove(uid);
            }
        }

        public void AddOrUpdateFriend(Client friend)
        {
            if (friend == null) return;
            lock (_friendsLock)
            {
                FriendsDic[friend.UID] = friend;
            }
        }

        public int GetFriendCount()
        {
            lock (_friendsLock)
            {
                return FriendsDic.Count;
            }
        }

        public bool TryGetFriend(int uid, out Client friend)
        {
            lock (_friendsLock)
            {
                return FriendsDic.TryGetValue(uid, out friend);
            }
        }

        public void ClearFriends()
        {
            lock (_friendsLock)
            {
                FriendsDic.Clear();
            }
        }
        public UserData GetUserData
        {
            get { return _userdata; }
        }
        public string UserName
        {
            get { return _userdata.UserName; }
        }
        public int UID
        {
            get { return _userdata.UID; }
        }
        public string PlayerName
        {
            get { return _userdata.PlayerName; }
        }
        public Hero PlayerHero
        {
            get { return _userdata.PlayerHero; }

        }
        public PlayerState PlayerState
        {
            get; set;
        }
        public MySqlConnection GetMysqlConnecet
        {
            get { return _mysqlConnection; }
        }
        public string socketIp { get; private set; }
        public Client(Socket socket, Server server)
        {
            lastPingTime = Tool.PingPongTool.GetTimeStamp();
            _userdata = new UserData();
            _message = new Message();
            _server = server;
            _socket = socket;


            socketIp = _socket.RemoteEndPoint.ToString().Split(':')[0];

            try
            {
                //打开数据库连接
                _mysqlConnection = new MySqlConnection(ServerConfig.DOMConectStr);
                _mysqlConnection.Open();
            }
            catch (Exception ex)
            {
                Logging.Debug.Log(ex.ToString());

                Close();
                return;
            }
            //4.开始异步接受消息
            ReceiveMessage();
        }
        /// <summary> 
        /// 接收消息 
        /// </summary> 
        /// <param name="clientSocket"></param> 
        private void ReceiveMessage()
        {
            try
            {
                Logging.Debug.Log("开始接收  client:" + _socket.LocalEndPoint + "  ---  sever:" + _socket.RemoteEndPoint);
                //数据存好
                _socket.BeginReceive(_message.Buffer, _message.StartIndex, _message.Remsize, SocketFlags.None, ReceiveCallBack, null);
            }
            catch (Exception EX)
            {
                Logging.Debug.Log(EX+"离谱");
                Close();
            }
        }
        private void ReceiveCallBack(IAsyncResult iar)
        {
            try
            {
                if (_socket == null || _socket.Connected == false) return;
                int len = _socket.EndReceive(iar);
                Logging.Debug.Log("接收成功");

                if (len == 0)
                {
                    //这个0在tcp里意思就是对方关闭连接
                    Logging.Debug.Log("接收数据为0");
                    Close();
                    return;
                }
                //处理存好的数据
                _message.ReadBuffer(len, HandleRequest);
                ReceiveMessage();
            }
            catch
            {
                Close();
            }
        }
        /// <summary>
        /// 使用读写队列优化
        /// </summary>
        private Queue<ByteArray> writeQueue = new Queue<ByteArray>();
        public void Send(MainPack pack)
        {
            try
            {

                byte[] sendbyte = Message.PackData(pack);
                ByteArray ba = new ByteArray(sendbyte);
                lock (writeQueue)
                {
                    writeQueue.Enqueue(ba);
                    if (writeQueue.Count == 1)
                    {
                        // 由队首 ByteArray 驱动发送，确保入队+判断+启动发送原子化
                        ByteArray head = writeQueue.Peek();
                        _socket.BeginSend(head.bytes, head.ReadIdx, head.Length, 0, SendBackCall, _socket);
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Debug.Log(ex);
            }
        }
        private void SendBackCall(IAsyncResult ar)
        {
            Socket socket = (Socket)ar.AsyncState;
            int count = socket.EndSend(ar);
            ByteArray ba;
            lock (writeQueue)
            {
                ba = writeQueue.Peek();
            }
            ba.ReadIdx += count;
            ///完整发送了消息
            if (ba.Length == 0)
            {
                lock (writeQueue)
                {
                    ba = null;
                    writeQueue.Dequeue();
                    if (writeQueue.Count != 0)
                        ba = writeQueue.Peek();
                }
            }

            if (ba != null)
            {
                socket.BeginSend(ba.bytes, ba.ReadIdx, ba.Length, 0, SendBackCall, socket);
            }

        }
        //回调函数，给message调用，message提供处理好的pack
        void HandleRequest(MainPack pack)
        {
            _server.HandleRequest(pack, this);
        }
        private PlayerState laststate = PlayerState.PlayerOutline;
        /// <summary>
        /// 你改变时通知别的好友更新你的状态，正常来说应该用事件，但也没别的地方用，先这么着吧.
        /// </summary>
        public void UpdateMyselfInfo()
        {
            try
            {
                    Logging.Debug.Log(PlayerName + "  :  " + laststate + "    ----UpdateMyselfInfo--->  " + PlayerState);
                    //Logging.Debug.Log(FriendActiveList.Count+"   "+GetFriendCount());
                    List<Client> friendsSnapshot = GetFriendsSnapshot();
                    foreach (Client player in friendsSnapshot)
                    {
                        if (player.PlayerState == PlayerState.PlayerGame || player.PlayerState == PlayerState.PlayerOutline) continue;
                        Logging.Debug.Log(player.PlayerName + "  " + player.PlayerState);
                        player.UpdateActiveFriendInfo();
                    }
                    laststate = PlayerState;

            }
            catch (Exception ex)
            {
                Logging.Debug.Log(ex);
            }
        }
        /// <summary>
        /// 得到活跃好友的信息包
        /// </summary>
        /// <returns></returns>
        public MainPack GetActiveFriendInfoPack()
        {
            MainPack pack = new MainPack();
            List<Client> friendsSnapshot = GetFriendsSnapshot();
            foreach (Client player in friendsSnapshot)
            {
                Logging.Debug.Log(player.PlayerName + "  :  " + player.PlayerState);
                if (player.PlayerState != PlayerState.PlayerOnline) continue;

                PlayerPack playerPack = new PlayerPack();
                playerPack.Playername = player.PlayerName;
                playerPack.Id = player.UID;
                playerPack.State = player.PlayerState;

                Logging.Debug.Log(playerPack.State + "   " + player.PlayerState);
                Logging.Debug.Log(playerPack);
                pack.Playerspack.Add(playerPack);
            }
            pack.Str = "x";//防止空包
            Logging.Debug.Log("UpdatePack    " + pack);
            return pack;
        }
        /// <summary>
        /// 更新客户端的好友状态
        /// </summary>
        public void UpdateActiveFriendInfo()
        {
            try
            {
                ///观察者模式:
                Logging.Debug.Log(PlayerName + "   UpdateActiveFriendInfo :  " + PlayerState);
                MainPack pack = GetActiveFriendInfoPack();
                pack.Actioncode = ActionCode.UpDateActiveFriendInfo;
                pack.Requestcode = RequestCode.FriendRoom;
                Send(GetActiveFriendInfoPack());

            }
            catch (Exception ex)
            {
                Logging.Debug.Log(ex);
            }
        }
        private int _closeStarted = 0;
        public void Close()
        {
            if (Interlocked.Exchange(ref _closeStarted, 1) == 1)
            {
                return;
            }

            Logging.Debug.Log("client  Close||||!!!!!!!!!");
            try
            {
                if (_server != null)
                {
                    _server._controllerManger?.CloseClient(this, UID);
                    BattleManage.Instance.HandleClientDisconnect(_server, UID);
                }
            }
            catch (Exception ex)
            {
                Logging.Debug.Log(ex);
            }

            try
            {
                _userdata.BordCaseToFriendLogout(_mysqlConnection, _server, this, UpdateActiveFriendInfo);
            }
            catch (Exception ex)
            {
                Logging.Debug.Log(ex);
            }

            _server.RemoveClient(this);
            _server.RemoveActiveClient(this);
            if (FriendRoom != null)
            {
                FriendRoom.Exit(_server, this);
            }
            _socket.Close();
            _mysqlConnection.Close();
        }
    }
}
