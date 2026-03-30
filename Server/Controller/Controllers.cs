using System;
using System.Collections.Generic;
using System.Text;
using SocketProto;
using Server;
using System.Linq;

namespace Server.Controller
{
    /// <summary>
    /// 通过反射机制找到RequestCode对应的方法来调用
    /// </summary>
    abstract class BaseControllers
    {
        protected RequestCode requestCode = RequestCode.RequestNone;
        public RequestCode GetRequestCode
        {
            get { return requestCode; }
        }
        public virtual void CloseClient(Client client, int id)
        {
            
        }
    }
    class ClearSenceController : BaseControllers
    {
        private readonly object _clearLock = new object();
        private Dictionary<int, bool> _dic_ClearFinish;
        public ClearSenceController()
        {
            requestCode = RequestCode.ClearSence;
            _dic_ClearFinish = new Dictionary<int, bool>();
        }
        public void  AllClearSenceReady(Server server, List<int> playeruids)
        {
            MainPack pack = new MainPack();
            pack.Actioncode = ActionCode.AllClearSenceReady;
            pack.Requestcode = RequestCode.ClearSence;
            pack.Str = "1";

            List<int> snapshotPlayerUids = new List<int>(playeruids);
            lock (_clearLock)
            {
                foreach (int id in snapshotPlayerUids)
                {
                    _dic_ClearFinish.Remove(id);
                }
            }

            foreach (int id in snapshotPlayerUids)
            {
                Client activeClient = server.GetActiveClient(id);
                activeClient?.Send(pack);
            }

            //return pack;

        }
        public MainPack ClientSendClearSenceReady(Server server, Client client, MainPack pack)
        {
            int uid = int.Parse(pack.Str);
            if (!BattleManage.Instance.TryGetBattleContextByUid(uid, out BattleContext battleContext))
            {
                Logging.Debug.Log($"ClientSendClearSenceReady 未找到 battle context, uid={uid}");
                return pack;
            }

            List<int> playeruids = new List<int>(battleContext.PlayerUids);
            bool isOK;
            lock (_clearLock)
            {
                _dic_ClearFinish[uid] = true;
                isOK = true;
                foreach (int id in playeruids)
                {
                    if (!_dic_ClearFinish.ContainsKey(id))
                    {
                        isOK = false;
                        break;
                    }
                }
            }
            if (isOK)
            {
                AllClearSenceReady(server,playeruids);
            }
            return pack;
        }
    }



    struct MatchUserInfo
    {
        public int uid;
        public string userName;
        public Hero hero;
        public int teamid;
        public string socketIP;
        public override string ToString()
        {
            return $"[uid: {uid}  userName: {userName}  hero: {hero}  teamid: {teamid}  socketIP:{socketIP}]";
        }
    }

    struct MatchedPlayerEntry
    {
        public int uid;
        public string roomId;
        public string teamId;
    }

    struct MatchResult
    {
        public MatchingController.FightPattern fightPattern;
        public string roomId;
        public List<MatchedPlayerEntry> players;
    }
    class MatchingController : BaseControllers
    {
        private readonly object _matchLock = new object();
        private Dictionary<FightPattern, List<BattleRoom>> MathingDic;
        private Dictionary<int, BattleRoom> PlayerIDMapRoomDic;
        public MatchingController()
        {
            MathingDic = new Dictionary<FightPattern, List<BattleRoom>>();
            foreach (FightPattern pattern in Enum.GetValues(typeof(FightPattern)))
            {
                MathingDic.Add(pattern, new List<BattleRoom>());
            }
            PlayerIDMapRoomDic = new Dictionary<int, BattleRoom>();
            requestCode = RequestCode.Matching;
        }

        /// <summary>
        /// 雪花算法获得时间戳ID
        /// </summary>
        public class TimestampID
        {
            private long _lastTimestamp;
            private long _sequence; //计数从零开始
            private readonly DateTime? _initialDateTime;
            private static TimestampID _timestampID;
            private const int MAX_END_NUMBER = 9999;

            private TimestampID(DateTime? initialDateTime)
            {
                _initialDateTime = initialDateTime;
            }

            /// <summary>
            /// 获取单个实例对象
            /// </summary>
            /// <param name="initialDateTime">最初时间，与当前时间做个相差取时间戳</param>
            /// <returns></returns>
            public static TimestampID GetInstance(DateTime? initialDateTime = null)
            {
                if (_timestampID == null) System.Threading.Interlocked.CompareExchange(ref _timestampID, new TimestampID(initialDateTime), null);
                return _timestampID;
            }

            /// <summary>
            /// 最初时间，作用时间戳的相差
            /// </summary>
            protected DateTime InitialDateTime
            {
                get
                {
                    if (_initialDateTime == null || _initialDateTime.Value == DateTime.MinValue) return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    return _initialDateTime.Value;
                }
            }
            /// <summary>
            /// 获取时间戳ID
            /// </summary>
            /// <returns></returns>
            public string GetID()
            {
                long temp;
                var timestamp = GetUniqueTimeStamp(_lastTimestamp, out temp);
                return $"{timestamp}{Fill(temp)}";
            }
            //前面补0
            private string Fill(long temp)
            {
                var num = temp.ToString();
                IList<char> chars = new List<char>();
                for (int i = 0; i < MAX_END_NUMBER.ToString().Length - num.Length; i++)
                {
                    chars.Add('0');
                }
                return new string(chars.ToArray()) + num;
            }

            /// <summary>
            /// 获取一个时间戳字符串
            /// </summary>
            /// <returns></returns>
            private long GetUniqueTimeStamp(long lastTimeStamp, out long temp)
            {
                lock (this)
                {
                    temp = 1;
                    var timeStamp = GetTimestamp();
                    if (timeStamp == _lastTimestamp)
                    {
                        _sequence = _sequence + 1;
                        temp = _sequence;
                        if (temp >= MAX_END_NUMBER)
                        {
                            timeStamp = GetTimestamp();
                            _lastTimestamp = timeStamp;
                            temp = _sequence = 1;
                        }
                    }
                    else
                    {
                        _sequence = 1;
                        _lastTimestamp = timeStamp;
                    }
                    return timeStamp;
                }
            }

            /// <summary>
            /// 
            /// </summary>
            /// <returns></returns>
            private long GetTimestamp()
            {
                if (InitialDateTime >= DateTime.Now) throw new Exception("最初时间比当前时间还大，不合理");
                var ts = DateTime.UtcNow - InitialDateTime;
                return (long)ts.TotalMilliseconds;
            }
        }
        /// <summary>
        /// 匹配房间
        /// </summary>
        public class BattleRoom
        {
            public class BattleTeam
            {
                public string Teamid { get; private set; }
                public int num { get { return playerIDs.Count; } }
                private int maxnum;//{ get; private set; }
                public bool IsFull { get { return num >= maxnum; } }

                public void Join(int id)
                {
                    playerIDs.Add(id);
                }
                public List<int> playerIDs { get; private set; }
                public BattleTeam(string teamid,List<int> playerIDs, int maxnum)
                {
                    Logging.Debug.Log($"BattleTeam:  {teamid}  {playerIDs}   {maxnum}");
                    Teamid = teamid;
                    this.maxnum = maxnum;
                    this.playerIDs = new List<int>();
                    this.playerIDs.AddRange(playerIDs);
                }
            }
            
            public BattleRoom(string roomid,FightPattern fightPattern)
            {
                this.roomid = roomid;
                switch (fightPattern)
                {
                    case FightPattern.BaoShiZhengBa:
                        RoomMaxNumber = 2;
                        maxTeamnum = 2;
                        break;
                    case FightPattern.JinKuGongFang:
                        RoomMaxNumber = 2;
                        maxTeamnum = 2;
                        break;
                    case FightPattern.LuanDouZuQiu:
                        RoomMaxNumber = ServerConfig.MaxRoom3_3Number;
                        maxTeamnum = ServerConfig.MaxTeam3_3Number;
                        break;
                    case FightPattern.HuangYeJueDou:
                        RoomMaxNumber = 10;
                        maxTeamnum = 5;
                        break;
                }
            }
            public string roomid { get; private set; }
            public int RoomMaxNumber { get; private set; }
            private int maxTeamnum;//{ get; private set; }
            private int curTeamnum{ 
                get {return battleTeams.Count;} 
            }
            public int RoomNumber
            {
                get {
                    int res = 0;
                    foreach (var room in battleTeams)
                    {
                        res += room.num;
                    }
                    return res;
                }
            }
            private List<BattleTeam> battleTeams = new List<BattleTeam>();

            public void Exit(int id)
            {
                List<int> team = null;
                foreach (var room in battleTeams)
                {
                    foreach (var xid in room.playerIDs)
                    {
                        if (xid == id)
                        {
                            team = room.playerIDs;//.Remove(id);
                            break;
                        }
                    }
                }
                team.Remove(id);
            }

            public bool Join(List<int> teamids)
            {
                if (teamids.Count == 1) return join(teamids[0]);
                return join(teamids);
            }
            private bool join(int id)
            {
                //搜索所有fightPattern模式的队伍
                //Logging.Debug.Log("搜索所有fightPattern模式的队伍");
                bool isJoin = false;
                foreach (BattleTeam Team in battleTeams)
                {
                    //如果有队伍没满的就加入
                    if (!Team.IsFull)
                    {
                        isJoin = true;
                        Team.Join(id);
                        break;
                    }
                }
                if (!isJoin)
                {
                    //队伍都满人了否则自己创建一个小队
                    //队伍已满
                    if (curTeamnum == maxTeamnum) return false;
                    //Logging.Debug.Log("队伍都满人了,否则自己创建一个小队");
                    battleTeams.Add(new BattleTeam(TimestampID.GetInstance().GetID(),new List<int>() { id },RoomMaxNumber/maxTeamnum));
                }
                return true;
            }
            private bool join(List<int> teamids)
            {
                if (curTeamnum == maxTeamnum) return false;
                //否则自己创建一个小队
                battleTeams.Add(new BattleTeam(TimestampID.GetInstance().GetID(),teamids, RoomMaxNumber / maxTeamnum));
                return true;   
            }
            public bool CheckCanFight()
            {
                if (curTeamnum == maxTeamnum)
                {
                    //bool isOk = false;
                    foreach (var team in battleTeams)
                    {
                        if (!team.IsFull) return false;
                    }
                    return true;
                }
                return false;
            }

            public List<MatchedPlayerEntry> GetRoomPlayerInfo()
            {
                List<MatchedPlayerEntry> res = new List<MatchedPlayerEntry>();
                string roomid = this.roomid;

                foreach (var team in battleTeams)
                {
                    string teamid = team.Teamid;
                    foreach (var player in team.playerIDs)
                    {
                        res.Add(new MatchedPlayerEntry
                        {
                            uid = player,
                            roomId = roomid,
                            teamId = teamid,
                        });
                    }
                }
                return res;
            }

            public bool IsEmpty()
            {
                return RoomNumber <= 0;
            }
        }
       
        public enum FightPattern
        {
            BaoShiZhengBa = 0,
            JinKuGongFang = 1,
            ShangJinLieRen = 2,
            LuanDouZuQiu = 3,
            HuangYeJueDou = 4,
        }
        
        /// <summary>
        /// 将把所有队伍的成员由房主传达List<BattlePlayerPack>
        /// 或者传自己的 BattlePlayerPack即可
        /// </summary>
        /// <param name="server"></param>
        /// <param name="client"></param>
        /// <param name="pack"></param>
        /// <returns></returns>
        public MainPack AddMatchingPlayer(Server server, Client client, MainPack pack)
        {
            //获取模式信息，玩家们id
            FightPattern fightPattern = (FightPattern)pack.Playerspack[0].Fightpattern;
            //Logging.Debug.Log($"{fightPattern}    {pack.Playerspack[0].Fightpattern}");
            List<int> playerids = new List<int>();
            foreach (var player in pack.Playerspack)
            {
                playerids.Add(player.Id);
            }
            /*
             1.2AddMatchingPlayer将当前玩家和信息加入到匹配队列
             */
            try
            {
                BattleRoom room;
                MatchResult? matchResult = null;
                List<MatchedPlayerEntry> roomPlayersSnapshot;
                string roomCount;

                lock (_matchLock)
                {
                    room = Join(playerids, fightPattern);
                    if (room.CheckCanFight())
                    {
                        matchResult = BuildMatchResult(room, fightPattern);
                        ReleaseRoom(room, fightPattern);
                    }

                    roomPlayersSnapshot = room.GetRoomPlayerInfo();
                    roomCount = room.RoomNumber + "/" + room.RoomMaxNumber;
                }

                if (matchResult.HasValue)
                {
                    StartFighting(server, matchResult.Value);
                }

                //广播房间人数
                pack.Returncode = ReturnCode.Succeed;
                pack.Str = roomCount;
                Logging.Debug.Log("房间人数" + pack.Str);
                foreach (var player in roomPlayersSnapshot)
                {
                    Client c = server.GetActiveClient(player.uid);
                    if (c != null && !c.Equals(client))
                    {
                        c.Send(pack);
                    }
                }
            }
            catch(Exception ex)
            {
                Logging.Debug.Log(ex);
            }
            return pack;
        }

        public MainPack RemoveMatchingPlayer(Server server, Client client, MainPack pack)
        {
            try
            {
                //获取模式信息，玩家们id
                FightPattern fightPattern = (FightPattern)pack.Playerspack[0].Fightpattern;
                Logging.Debug.Log($"{fightPattern}    {pack.Playerspack[0].Fightpattern}");

                List<int> playerids = new List<int>();
                foreach (var player in pack.Playerspack)
                {
                    playerids.Add(player.Id);
                }
                Logging.Debug.Log("移出对战房间");

                BattleRoom room;
                List<MatchedPlayerEntry> roomPlayersSnapshot = new List<MatchedPlayerEntry>();
                string roomCount = "0/0";
                lock (_matchLock)
                {
                    room = Exit(playerids, fightPattern);
                    if (room != null)
                    {
                        roomPlayersSnapshot = room.GetRoomPlayerInfo();
                        roomCount = room.RoomNumber + "/" + room.RoomMaxNumber;
                    }
                }

                if (room == null)
                {
                    pack.Returncode = ReturnCode.Fail;
                    return pack;
                }

                //广播房间人数
                pack.Returncode = ReturnCode.Succeed;
                pack.Str = roomCount;
                Logging.Debug.Log("广播房间人数: " + pack.Str);
                foreach (var player in roomPlayersSnapshot)
                {
                    Client c = server.GetClientByID(player.uid);
                    if (c != null && !c.Equals(client))
                    {
                        c.Send(pack);
                    }
                }

                pack.Str = "-1";
                foreach (var player in playerids)
                {
                    Client c = server.GetClientByID(player);
                    if (c != null && !c.Equals(client))
                    {
                        c.Send(pack);
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Debug.Log(ex);
            }
            return pack;
        }
        private BattleRoom Join(List<int> playerids,FightPattern fightPattern)
        {
            lock (_matchLock)
            {
                bool isOk = false;
                BattleRoom battleroom = null;
                foreach (var room in MathingDic[fightPattern])
                {
                    //加入成功
                    if (room.Join(playerids))
                    {
                        battleroom = room;
                        isOk = true;
                        break;
                    }
                }
                Logging.Debug.Log($"加入是否房间成功： {isOk}");
                //加入失败
                if (!isOk)
                {
                    battleroom = new BattleRoom(TimestampID.GetInstance().GetID(), fightPattern);
                    battleroom.Join(playerids);

                    MathingDic[fightPattern].Add(battleroom);
                }
                foreach (int id in playerids)
                    PlayerIDMapRoomDic[id] = battleroom;
                return battleroom;
            }
        }
        private BattleRoom Exit(List<int> playerids, FightPattern fightPattern)
        {
            lock (_matchLock)
            {
                if (playerids == null || playerids.Count == 0)
                {
                    return null;
                }
                if (!PlayerIDMapRoomDic.TryGetValue(playerids[0], out BattleRoom battleroom) || battleroom == null)
                {
                    return null;
                }
                foreach (int id in playerids)
                {
                    battleroom.Exit(id);
                    PlayerIDMapRoomDic.Remove(id);
                }
                RecycleRoomIfEmpty(battleroom, fightPattern);
                return battleroom;
            }
        }

        private MatchResult BuildMatchResult(BattleRoom room, FightPattern fightPattern)
        {
            List<MatchedPlayerEntry> roomPlayers = room.GetRoomPlayerInfo();
            MatchResult result = new MatchResult
            {
                fightPattern = fightPattern,
                roomId = room.roomid,
                players = new List<MatchedPlayerEntry>(roomPlayers),
            };
            return result;
        }

        private void ReleaseRoom(BattleRoom room, FightPattern fightPattern)
        {
            foreach (MatchedPlayerEntry player in room.GetRoomPlayerInfo())
            {
                PlayerIDMapRoomDic.Remove(player.uid);
            }
            MathingDic[fightPattern].Remove(room);
        }

        private void RecycleRoomIfEmpty(BattleRoom room, FightPattern fightPattern)
        {
            if (room != null && room.IsEmpty())
            {
                MathingDic[fightPattern].Remove(room);
            }
        }

        private List<MatchUserInfo> BuildMatchUsers(Server server, MatchResult matchResult)
        {
            Dictionary<string, int> teamID = new Dictionary<string, int>();
            int curMaxID = 1;
            List<MatchUserInfo> matchUsers = new List<MatchUserInfo>();
            foreach (MatchedPlayerEntry player in matchResult.players)
            {
                MatchUserInfo userInfo = new MatchUserInfo();
                userInfo.uid = player.uid;
                Client c = server.GetActiveClient(userInfo.uid);
                if (c == null)
                {
                    Logging.Debug.Log($"匹配对战时，玩家{userInfo.uid}不在线");
                    continue;
                }
                userInfo.hero = c.PlayerHero;
                userInfo.userName = c.PlayerName;
                if (!teamID.ContainsKey(player.teamId))
                {
                    teamID.Add(player.teamId, curMaxID++);
                }
                userInfo.teamid = teamID[player.teamId];
                userInfo.socketIP = c.socketIp;
                matchUsers.Add(userInfo);
                Logging.Debug.Log(userInfo);
            }
            return matchUsers;
        }

        private void StartFighting(Server server, MatchResult matchResult)
        {
            List<MatchUserInfo> matchUsers = BuildMatchUsers(server, matchResult);
            if (matchUsers.Count == 0)
            {
                Logging.Debug.Log($"StartFighting 没有可用玩家，roomId={matchResult.roomId}");
                return;
            }

            if (!BattleManage.Instance.TryBeginBattle(server, matchUsers, matchResult.fightPattern, out int battleId))
            {
                Logging.Debug.Log($"StartFighting 创建战斗失败，roomId={matchResult.roomId}");
                return;
            }

            Logging.Debug.Log($"StartFighting 创建战斗成功，roomId={matchResult.roomId}, battleId={battleId}");
        }
        public override void CloseClient(Client client, int id)
        {
            base.CloseClient(client, id);
            lock (_matchLock)
            {
                if (PlayerIDMapRoomDic.TryGetValue(id, out BattleRoom room) && room != null)
                {
                    room.Exit(id);
                    PlayerIDMapRoomDic.Remove(id);
                    foreach (FightPattern fightPattern in MathingDic.Keys)
                    {
                        if (MathingDic[fightPattern].Contains(room))
                        {
                            RecycleRoomIfEmpty(room, fightPattern);
                            break;
                        }
                    }
                }
            }
        }
    }
    class PingPongController : BaseControllers
    {
        public PingPongController()
        {
            requestCode = RequestCode.PingPong;
        }
        public MainPack Ping(Server server, Client client, MainPack pack)
        {
            Logging.Debug.Log("ReceiveClientPing");
            pack.Actioncode = ActionCode.Pong;
            client.lastPingTime = Tool.PingPongTool.GetTimeStamp();
            return pack;
        }
    }
    class FriendRoomController : BaseControllers
    {
        // 添加下面这个构造函数
        public FriendRoomController()
        {
            requestCode = RequestCode.FriendRoom;
        }
        //Friendroom主要就是管理所有房间内client的管理，房间状态，房间最大人数，人数这些
        #region 房间集合
        private readonly object _roomLock = new object();
        private List<FriendRoom> _rooms = new List<FriendRoom>();
        #endregion

        #region 创建和邀请
        // 文件: Server/Controller/BaseControllers.cs
        // 在 FriendRoomController 类中

        public MainPack CreateRoom(Server server, Client client, MainPack pack)
        {
            try
            {
                // 如果玩家已经在房间里，先让他退出旧房间
                if (client.FriendRoom != null)
                {
                    client.FriendRoom.Exit(server, client);
                }

                FriendRoom room = new FriendRoom(client, pack.Friendroompack[0], server);
                lock (_roomLock)
                {
                    _rooms.Add(room);
                }

                // 清空旧的 playerpack，只返回当前房间的最新信息
                pack.Playerspack.Clear();
                foreach (PlayerPack p in room.GetPlayerInfo())
                {
                    pack.Playerspack.Add(p);
                }

                client.PlayerState = PlayerState.PlayerOnRoom;
                client.UpdateMyselfInfo();

                pack.Returncode = ReturnCode.Succeed; // 直接返回成功
                Logging.Debug.Log($"Player {client.PlayerName} created room {room.GetRoomInfo.Roomid} successfully.");
                return pack;
            }
            catch (Exception ex)
            {
                Logging.Debug.Log($"CreateRoom failed with exception: {ex}");
                pack.Returncode = ReturnCode.Fail;
                return pack;
            }
        }

        public bool InviteToRoom(Server server, Client client, string friendname, FriendRoomPack friendRoomPack)
        {
            Client friendclient = server.GetClientByPlayerName(friendname);
            Logging.Debug.Log(friendclient?.UserName);
            if (friendclient != null)
            {
                Logging.Debug.Log("Invite count: " + friendclient.UserName);
                if (server.GetActiveClient(friendclient.UID) != null)
                {
                    friendclient.PlayerState = PlayerState.PlayerOnInvated;
                    friendclient.UpdateMyselfInfo();
                    MainPack pack = new MainPack
                    {
                        Actioncode = ActionCode.InviteFriend,
                        Requestcode = RequestCode.FriendRoom,
                        Returncode = ReturnCode.Succeed,
                        Str = client.PlayerName
                    };
                    pack.Friendroompack.Add(friendRoomPack);
                    friendclient.Send(pack);
                    return true;
                }
            }
            return false;
        }
        #endregion

        #region 进出房间
        //这里socket包拼错了，没办法了，就先这样吧
        public MainPack AcceptInvateFriend(Server server, Client client, MainPack pack)
        {
            return JoinFriendRoom(server, client, pack);
        }
        // 在 FriendRoomController 类中添加这个方法
        public MainPack InviteFriend(Server server, Client client, MainPack pack)
        {
            // 检查发起邀请的玩家是否在房间里
            if (client.FriendRoom == null)
            {
                Logging.Debug.Log($"[InviteFriend-Error] Player {client.PlayerName} is not in a room.");
                pack.Returncode = ReturnCode.Fail;
                return pack;
            }

            // 获取朋友的名字和房间信息
            string friendname = pack.Str;
            FriendRoomPack roomPack = client.FriendRoom.GetRoomInfo;

            // 调用你已经写好的邀请逻辑
            if (InviteToRoom(server, client, friendname, roomPack))
            {
                Logging.Debug.Log($"[InviteFriend-Success] Player {client.PlayerName} invited {friendname}.");
                pack.Returncode = ReturnCode.Succeed;
            }
            else
            {
                Logging.Debug.Log($"[InviteFriend-Fail] Failed to invite {friendname}. Maybe offline or invalid.");
                pack.Returncode = ReturnCode.Fail;
            }

            // 这个响应是发给发起邀请的人的，告诉他邀请已发送或失败
            // 注意：不要把这个 pack 发给被邀请的人，InviteToRoom 内部会创建新的 pack 发送
            pack.Actioncode = ActionCode.ActionNone; // 设置为 None，避免客户端收到后重复处理
            return pack;
        }
        public MainPack RejectInviteFriend(Server server, Client client, MainPack pack)
        {
            try
            {
                FriendRoom targetRoom = null;
                lock (_roomLock)
                {
                    foreach (FriendRoom r in _rooms)
                    {
                        if (r.GetRoomInfo.Roomid.Equals(pack.Str))
                        {
                            targetRoom = r;
                            break;
                        }
                    }
                }

                if (targetRoom != null)
                {
                    pack.Returncode = ReturnCode.Succeed;
                    foreach (PlayerPack playerPack in targetRoom.GetPlayerInfo())
                    {
                        Client friendClient = server.GetClientByPlayerName(playerPack.Playername);
                        pack.UserInfopack = new PlayerPack { Id = client.UID };
                        friendClient?.Send(pack);
                    }
                    pack.Actioncode = ActionCode.ActionNone;
                    client.PlayerState = PlayerState.PlayerOnline;
                    client.UpdateMyselfInfo();
                    return pack;
                }
            }
            catch (Exception ex)
            {
                Logging.Debug.Log(ex);
                pack.Returncode = ReturnCode.Fail;
            }
            return pack;
        }

        // 【修改】JoinFriendRoom 方法
        public MainPack JoinFriendRoom(Server server, Client client, MainPack pack)
        {
            FriendRoom targetRoom = null;
            lock (_roomLock)
            {
                foreach (FriendRoom r in _rooms)
                {
                    if (r.GetRoomInfo.Roomid.Equals(pack.Str))
                    {
                        targetRoom = r;
                        break;
                    }
                }
            }

            if (targetRoom != null)
            {
                if (targetRoom.GetRoomInfo.State == RoomState.RoomNormal)
                {
                    // 1. 加入房间（只更新服务器内部状态）
                    targetRoom.Join(client);

                    // 2. 更新客户端状态
                    client.PlayerState = PlayerState.PlayerOnRoom;
                    client.UpdateMyselfInfo();
                    client.UpdateActiveFriendInfo();

                    // 3. 创建一个权威的状态更新包
                    MainPack updatePack = new MainPack();
                    updatePack.Actioncode = ActionCode.JoinRoom; // 统一使用 JoinRoom 作为更新信号
                    updatePack.Returncode = ReturnCode.Succeed;
                    updatePack.Requestcode = RequestCode.FriendRoom;
                    updatePack.Friendroompack.Add(targetRoom.GetRoomInfo); // 包含最新的房间信息
                    foreach (PlayerPack p in targetRoom.GetPlayerInfo()) // 包含最新的完整成员列表
                    {
                        updatePack.Playerspack.Add(p);
                    }

                    // 4. 将这个包广播给房间里的【所有人】
                    targetRoom.BroadcastToAll(updatePack);

                    // 5. 因为广播已经包含了新加入的玩家，所以不需要再单独返回一个包了
                    return null; // 或者返回一个不处理的 ActionNone 包
                }
                else
                {
                    client.PlayerState = PlayerState.PlayerOnline;
                    client.UpdateMyselfInfo();
                    client.UpdateActiveFriendInfo();
                    pack.Returncode = ReturnCode.Fail;
                    return pack;
                }
            }

            client.PlayerState = PlayerState.PlayerOnline;
            client.UpdateMyselfInfo();
            client.UpdateActiveFriendInfo();
            pack.Returncode = ReturnCode.NotRoom;
            return pack;
        }

        public MainPack CancelInviteFriend(Server server, MainPack pack)
        {
            Client friend = server.GetClientByPlayerName(pack.Str);
            Logging.Debug.Log(friend?.PlayerName);
            pack.Returncode = ReturnCode.Succeed;
            pack.Actioncode = ActionCode.CancalInvateFriend;
            if (friend?.FriendRoom != null) pack.Returncode = ReturnCode.Fail;
            pack.Str = "?";
            friend?.Send(pack);
            return pack;
        }

        public MainPack ExitRoom(Server server, Client client, MainPack pack)
        {
            if (client.FriendRoom == null)
            {
                pack.Returncode = ReturnCode.Fail;
                return pack;
            }
            client.FriendRoom.Exit(server, client);
            pack.Actioncode = ActionCode.ActionNone;
            pack.Returncode = ReturnCode.Succeed;
            return pack;
        }

        public void RemoveFriendRoom(FriendRoom room)
        {
            lock (_roomLock)
            {
                _rooms.Remove(room);
            }
        }
        #endregion

        #region 聊天与角色切换
        //应该就是pack里面有聊天内容，直接原样转发给房间里别的人
        public void Chat(Server server, Client client, MainPack pack)
        {
            client.FriendRoom.BroadCastTCP(client, pack);
        }

        public void ChangeHero(Client client, MainPack pack)
        {
            pack.Requestcode = RequestCode.FriendRoom;
            pack.Returncode = ReturnCode.Succeed;
            pack.Actioncode = ActionCode.UpDateActiveFriendInfo;
            pack.UserInfopack = new PlayerPack
            {
                Id = client.UID,
                Playername = client.PlayerName,
                Hero = client.PlayerHero
            };
            pack.Str = "ChangeHero";
            client.FriendRoom.BroadCastTCP(client, pack);
        }
        #endregion
    }
    class FriendController : BaseControllers
    {
        public FriendController()
        {
            requestCode = RequestCode.Friend;
        }
        /// <summary>
        /// 加好友
        /// </summary>
        /// <param name="server"></param>
        /// <param name="client"></param>
        /// <param name="pack"></param>
        /// <returns></returns>
        public MainPack AplyAddFriend(Server server, Client client, MainPack pack)
        {
            if (client.GetUserData.AplyAddFriend(pack, client.GetMysqlConnecet, server))
            {
                pack.Returncode = ReturnCode.Succeed;
            }
            else pack.Returncode = ReturnCode.Fail;
            return pack;
        }
        public MainPack AcceptAddFriend(Server server, Client client, MainPack pack)
        {
            if (client.GetUserData.AcceptAddFriend(ref pack,client, client.GetMysqlConnecet, server))
            {
                pack.Returncode = ReturnCode.Succeed;
            }
            else pack.Returncode = ReturnCode.Fail;
            return pack;
        }

        public MainPack RejectAddFriend(Server server, Client client, MainPack pack)
        {
            client.GetUserData.RejectAddFriend(pack, client.GetMysqlConnecet, server);
            pack.Returncode = ReturnCode.Fail;
            return pack;
        }
    }
    class UserController : BaseControllers
    {
        public UserController()
        {
            requestCode = RequestCode.User;
        }
        /// <summary>
        /// 注册 反射调用 返回包的结果：Succeed或者Fail
        /// </summary>
        /// <returns></returns>
        public MainPack Logon(Server server, Client client, MainPack pack)
        {
            //1.3将账号密码信息录入数据库
            if (client.GetUserData.Logon(pack, client.GetMysqlConnecet))
            {
                pack.Returncode = ReturnCode.Succeed;
            }
            else pack.Returncode = ReturnCode.Fail;
            //1.4返回结果
            return pack;
        }

        /// <summary>
        /// 登陆  反射调用
        /// </summary>
        /// <returns></returns>
        public MainPack Login(Server server, Client client, MainPack pack)
        {
            //Logging.Debug.Log(client.UserName);
            if (client.UserName != null)
            {
                pack.Returncode = ReturnCode.Fail;
                return pack;
            }
            // 检查是否已有同账号的活跃玩家（只查 _activeClient，断线时已正确清除）
            Client existingActive = server.GetActiveClientByUserName(pack.Loginpack.Username);
            if (existingActive != null)
            {
                Logging.Debug.Log($"[Login] 拒绝重复登录: username={pack.Loginpack.Username}, 已有活跃连接 UID={existingActive.UID}");
                pack.Returncode = ReturnCode.Fail;
                return pack;
            }
            //0.3.查寻数据库用户信息
            if (client.GetUserData.Login(pack, client.GetMysqlConnecet))
            {
                pack.Returncode = ReturnCode.Succeed;
            }
            else pack.Returncode = ReturnCode.Fail;

            //发送结果
            return pack;
        }
        /// <summary>
        /// 找名字
        /// </summary>
        /// <param name="server"></param>
        /// <param name="client"></param>
        /// <param name="pack"></param>
        /// <returns></returns>
        /// 客户端成功登录后会去查
        public MainPack FindPlayerInfo(Server server, Client client, MainPack pack)
        {
            //2.2FindPlayerInfo查找玩家名字
            if (client.GetUserData.FindPlayerInfo(ref pack, client))
            {
                pack.Returncode = ReturnCode.Succeed;
                if (server.GetActiveClient(client.UID) != null && server.GetActiveClient(client.UID) != client)
                {
                    server.RemoveActiveClient(server.GetActiveClient(client.UID));
                }
                server.AddActiveClient(client.UID, client);
                Logging.Debug.Log($"[ACTIVE-ADD] id={client.UID} 玩家{client.PlayerName} 加入活跃字典(FindPlayerInfo后)");
            }
            else pack.Returncode = ReturnCode.Fail;
            //2.4返回查询结果
            return pack;
        }

        /// <summary>
        /// 找好友
        /// </summary>
        /// <param name="server"></param>
        /// <param name="client"></param>
        /// <param name="pack"></param>
        /// <returns></returns>
        public MainPack FindFriendsInfo(Server server, Client client, MainPack pack)
        {
            if (client.GetUserData.FindFriendsInfo(ref pack,client, client.GetMysqlConnecet,server))
            {
                pack = client.GetActiveFriendInfoPack();
                pack.Actioncode = ActionCode.FindFriendsInfo;
                pack.Requestcode = RequestCode.User;
                pack.Returncode = ReturnCode.Succeed;
            }
            else pack.Returncode = ReturnCode.Fail;
            return pack;
        }
        /// <summary>
        /// 修改名字
        /// </summary>
        /// <returns></returns>
        public MainPack UpdateName(Server server, Client client, MainPack pack)
        {
            if (client.GetUserData.UpdateName(pack, client.GetMysqlConnecet))
            {
                pack.Returncode = ReturnCode.Succeed;
            }
            else pack.Returncode = ReturnCode.Fail;
            return pack;
        }
        public MainPack ChangeHero(Server server, Client client, MainPack pack)
        {
            Logging.Debug.Log($"[ChangeHero-Controller] UID={client.UID} UserName={client.UserName}pack.UserInfopack.Hero ={ pack.UserInfopack?.Hero}");
            client.GetUserData.ChangeHero(pack);
            if (pack.Str == "Room")
            {
                FriendRoomController friendRoomController = (FriendRoomController)server._controllerManger.GetControllerByName(nameof(FriendRoomController));
                friendRoomController.ChangeHero(client, pack);
            }            
            pack.Actioncode = ActionCode.ActionNone;
            return pack;
        }
    }
}
