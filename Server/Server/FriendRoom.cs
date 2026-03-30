using Google.Protobuf.Collections;
using Server.Controller;
using SocketProto;
using System;
using System.Collections.Generic;
using System.Text;

namespace Server
{
    class FriendRoom
    {       
        private FriendRoomPack _friendroomInfo;//房间编号，最大人数，当前人数，房间状态
        private Server _server;
        private readonly object _roomLock = new object();
        private List<Client> _clientsList = new List<Client>();//房间内所有客户端

        private List<Client> GetClientsSnapshot()
        {
            lock (_roomLock)
            {
                return new List<Client>(_clientsList);
            }
        }

        /// <summary>
        /// 获取房间信息
        /// </summary>
        public FriendRoomPack GetRoomInfo
        {
            get
            {
                lock (_roomLock)
                {
                    _friendroomInfo.Curnum = _clientsList.Count;
                    return _friendroomInfo;
                }
            }
        }
        public string RoomID
        {
            get 
            {
                return _friendroomInfo.Roomid;
            }
        }

        public FriendRoom(Client client, FriendRoomPack pack, Server server)
        {
            _friendroomInfo = pack;
            _server = server;
            lock (_roomLock)
            {
                _clientsList.Add(client);
            }
            client.FriendRoom = this;
        }

        /// <summary>
        /// 获取当前房间所有用户信息
        /// </summary>
        /// <returns></returns>
        public RepeatedField<PlayerPack> GetPlayerInfo()
        {
            RepeatedField<PlayerPack> playerPacks = new RepeatedField<PlayerPack>();
            List<Client> clientsSnapshot = GetClientsSnapshot();
            foreach (Client c in clientsSnapshot)
            {
                PlayerPack playerPack = new PlayerPack();
                playerPack.Playername = c.PlayerName;
                playerPack.Id = c.UID;
                playerPack.Hero = c.PlayerHero;
                playerPacks.Add(playerPack);
            }
            return playerPacks;
        }


        // (保留原来的 BroadCastTCP 方法，用于聊天等功能)
        public void BroadCastTCP(Client client, MainPack pack)
        {
            List<Client> clientsSnapshot = GetClientsSnapshot();
            foreach (Client c in clientsSnapshot)
            {
                if (!c.Equals(client))
                {
                    c.Send(pack);
                }
            }
        }

        // 【新增】这个方法会给房间里的每一个成员发送消息
        public void BroadcastToAll(MainPack pack)
        {
            List<Client> clientsSnapshot = GetClientsSnapshot();
            foreach (Client c in clientsSnapshot)
            {
                c.Send(pack);
            }
        }
        // 【修改】简化 Join 方法
        public void Join(Client client)
        {
            lock (_roomLock)
            {
                _clientsList.Add(client);
                if (_clientsList.Count >= _friendroomInfo.Maxnum)
                {
                    //满人了
                    _friendroomInfo.State = RoomState.RoomFull;
                }
            }
            client.FriendRoom = this;
            // 不再在这里创建和发送包
        }
        public void Exit(Server server, Client client)
        {
            MainPack pack = new MainPack();
            RoomState roomState;
            lock (_roomLock)
            {
                roomState = _friendroomInfo.State;
            }

            if (roomState == RoomState.RoomGame)//游戏已经开始
            {
                ExitGameWhenStart(client);
                return;
            }

            bool roomIsEmpty;
            lock (_roomLock)
            {
                _clientsList.Remove(client);
                roomIsEmpty = _clientsList.Count == 0;
                if (!roomIsEmpty)
                {
                    _friendroomInfo.State = RoomState.RoomNormal;
                }
            }

            client.PlayerState = PlayerState.PlayerOnline;
            /*
            Logging.Debug.Log("Start Find  FriendsDic:\n");
            foreach (Client friend in client.FriendsDic.Values)
            {
                Logging.Debug.Log(friend.PlayerName+":  "+friend.FriendActiveList.Count);
                client.FriendActiveList.Add(friend);
            }
            Logging.Debug.Log("End Find  FriendsDic:\n");*/
            client.UpdateMyselfInfo();

            client.FriendRoom = null;
            if (roomIsEmpty)
            {
                FriendRoomController friendRoomController = (FriendRoomController)_server._controllerManger.GetControllerByName(nameof(FriendRoomController));
                friendRoomController.RemoveFriendRoom(this);
                client.UpdateActiveFriendInfo();
                return;
            }

            pack.Requestcode = RequestCode.FriendRoom;
            pack.Returncode = ReturnCode.Succeed;
            pack.Actioncode = ActionCode.ExitRoom;
            pack.Str = "Have a Friend Exit";
            foreach (PlayerPack player in GetPlayerInfo())
            {
                pack.Playerspack.Add(player);
            }
            BroadCastTCP(client, pack);
        }


       

        public void ExitGameWhenStart(Client client)
        {
            /*
            MainPack pack = new MainPack();
            if (client == clientList[0])
            {
                //房主退出
                pack.Actioncode = ActionCode.ExitGame;
                pack.Str = "r";
                Broadcast(client, pack);
                server.RemoveRoom(this);
                client.GetRoom = null;
            }
            else
            {
                //其他成员退出
                clientList.Remove(client);
                client.GetRoom = null;
                pack.Actioncode = ActionCode.UpCharacterList;
                foreach (var VARIABLE in clientList)
                {
                    PlayerPack playerPack = new PlayerPack();
                    playerPack.Playername = VARIABLE.GetUserInFo.UserName;
                    playerPack.Hp = VARIABLE.GetUserInFo.HP;
                    pack.Playerpack.Add(playerPack);
                }
                pack.Str = client.GetUserInFo.UserName;
                Broadcast(client, pack);
            }
            */
        }

    }
}
