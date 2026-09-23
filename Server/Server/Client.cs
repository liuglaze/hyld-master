using System;
using System.Net.Sockets;
using Server.Tool;
using Server.DAO;
using SocketProto;
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

        public FriendRoom FriendRoom
        {
            get; set;
        }

        // 说明：原先这里有一张 Dictionary<int, Client> FriendsDic 及 6 个访问辅助方法
        // （GetFriendsSnapshot / RemoveFriend / AddOrUpdateFriend / GetFriendCount /
        //   TryGetFriend / ClearFriends），以及基于它的 UpdateMyselfInfo / GetActiveFriendInfoPack /
        // UpdateActiveFriendInfo 三件套。
        //
        // 它们在服务端从未被写入过（FriendsDic 零写入点），因此：
        //   - UpdateMyselfInfo 遍历空表，是空转；
        //   - GetActiveFriendInfoPack 恒返回空好友列表；
        //   - UpdateActiveFriendInfo 推送空包，而客户端遇到空 Playerspack 会直接清空好友列表，
        //     等于「玩家一进房间好友面板就被清空」。
        // 好友列表现改为由 UserController.FindFriendsInfo 按需从数据库派生；
        // 实时的好友状态推送留到 P3 用复制机制重做。

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
        public Client(Socket socket, Server server)
        {
            lastPingTime = Tool.PingPongTool.GetTimeStamp();
            _userdata = new UserData();
            _message = new Message();
            _server = server;
            _socket = socket;

            // 说明：原先这里会打开一条 MySQL 连接，失败则直接 Close() 并放弃这个客户端。
            // 数据库已按需求移除（账号存在内存库 UserStore 里），因此不再有「连不上库就接入失败」这种状态。

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
                _message.EnsureWritableSpace();
                _socket.BeginReceive(_message.Buffer, _message.StartIndex, _message.Remsize, SocketFlags.None, ReceiveCallBack, null);
            }
            catch (Exception EX)
            {
                Logging.Debug.Log($"[TCP_CLOSE][ReceiveMessage] uid={UID} remote={_socket?.RemoteEndPoint} local={_socket?.LocalEndPoint} ex={EX}");
                Close("ReceiveMessage exception", EX);
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
                    Logging.Debug.Log($"[TCP_CLOSE][ReceiveCallBack] uid={UID} remote={_socket?.RemoteEndPoint} local={_socket?.LocalEndPoint} len=0 reason=remote_closed");
                    Close("ReceiveCallBack len=0 (remote closed)");
                    return;
                }
                //处理存好的数据
                _message.ReadBuffer(len, HandleRequest);
                ReceiveMessage();
            }
            catch (Exception ex)
            {
                Logging.Debug.Log($"[TCP_CLOSE][ReceiveCallBack] uid={UID} remote={_socket?.RemoteEndPoint} local={_socket?.LocalEndPoint} ex={ex}");
                Close("ReceiveCallBack exception", ex);
            }
        }
        private int _closeStarted = 0;
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
                bool isImportantTcpPack = pack.Actioncode == ActionCode.UpDateActiveFriendInfo
                    || pack.Actioncode == ActionCode.AddMatchingPlayer;
                lock (writeQueue)
                {
                    writeQueue.Enqueue(ba);
                    if (isImportantTcpPack)
                    {
                        Logging.Debug.Log($"[TCP_SEND][Queued] uid={UID} action={pack.Actioncode} request={pack.Requestcode} bytes={sendbyte.Length} queue={writeQueue.Count} remote={_socket?.RemoteEndPoint}");
                    }
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
                Logging.Debug.Log($"[TCP_CLOSE][Send] uid={UID} action={pack?.Actioncode} request={pack?.Requestcode} remote={_socket?.RemoteEndPoint} ex={ex}");
                Close("Send exception", ex);
            }
        }
        private void SendBackCall(IAsyncResult ar)
        {
            try
            {
                Socket socket = (Socket)ar.AsyncState;
                int count = socket.EndSend(ar);
                ByteArray ba;
                int queueCount;
                lock (writeQueue)
                {
                    ba = writeQueue.Peek();
                    queueCount = writeQueue.Count;
                }
                Logging.Debug.Log($"[TCP_SEND][Callback] uid={UID} sent={count} queue={queueCount} remote={_socket?.RemoteEndPoint} socketConnected={_socket?.Connected}");
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
            catch (Exception ex)
            {
                Logging.Debug.Log($"[TCP_CLOSE][SendBackCall] uid={UID} remote={_socket?.RemoteEndPoint} local={_socket?.LocalEndPoint} ex={ex}");
                Close("SendBackCall exception", ex);
            }

        }
        //回调函数，给message调用，message提供处理好的pack
        void HandleRequest(MainPack pack)
        {
            _server.HandleRequest(pack, this);
        }
        public void Close()
        {
            Close("Close()", null);
        }

        public void Close(string reason)
        {
            Close(reason, null);
        }

        public void Close(string reason, Exception ex)
        {
            if (Interlocked.Exchange(ref _closeStarted, 1) == 1)
            {
                return;
            }

            Logging.Debug.Log($"[TCP_CLOSE][Close] uid={UID} user={PlayerName} remote={_socket?.RemoteEndPoint} local={_socket?.LocalEndPoint} reason={reason} ex={(ex != null ? ex.ToString() : "null")}");
            Logging.Debug.Log("client  Close||||!!!!!!!!!");
            Logging.Debug.FlushTrace();
            try
            {
                if (_server != null)
                {
                    _server._controllerManger?.CloseClient(this, UID);

                    // 旧战斗链已退役：局内对局由专用服务器（DS）承载，旧 BattleManage 断线通知已删除。
                    // 这里只通知 Lobby 宿主；宿主会**中止**该对局（不伪造正常胜利），
                    // 若该 uid 不在对局里则是 no-op。
                    PMNet.Control.PMDsLobbyHost lobbyHost = PMNet.Control.PMDsLobbyHost.Instance;
                    if (lobbyHost != null)
                    {
                        lobbyHost.NotifyClientDisconnected(UID);
                    }
                }
            }
            catch (Exception ex2)
            {
                Logging.Debug.Log(ex2);
            }

            try
            {
                _userdata.BordCaseToFriendLogout(_server, this);
            }
            catch (Exception ex2)
            {
                Logging.Debug.Log(ex2);
            }

            _server.RemoveClient(this);
            _server.RemoveActiveClient(this);
            if (FriendRoom != null)
            {
                FriendRoom.Exit(_server, this);
            }
            _socket.Close();
            Logging.Debug.FlushTrace();
        }
    }
}
