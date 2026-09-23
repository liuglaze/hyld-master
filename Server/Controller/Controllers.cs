using System;
using System.Collections.Generic;
using System.Text;
using SocketProto;
using Server;
using System.Linq;
using PMNet.Control;

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
            Logging.Debug.Log($"[Matching][AddRequest] clientUid={client?.UID} clientState={client?.PlayerState} fightPattern={fightPattern} playerCount={playerids.Count} players={string.Join(",", playerids)} activeClient={(client != null && server.GetActiveClient(client.UID) != null)}");
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

        private void StartFighting(Server server, MatchResult matchResult)
        {
            // 旧链已退役（Docs/plans/net-legacy-retirement-contract.md §A）：开局链路**只有一条** ——
            // 由 Lobby 宿主拉起专用服务器（DS）承载整局。
            // 不再读 HYLD_PMNET_DS 之类的选链开关；宿主未就绪/配置不全/manifest 非法时显式失败，
            // 不生成第二个权威（旧 BattleController）。
            StartFightingDedicatedServer(server, matchResult);
        }

        /// <summary>
        /// DS 新链开局：由 Lobby 宿主拉起专用服务器（DS）承载整局。这是**唯一**开局实现。
        ///
        /// 硬约束（契约 §7.3）：
        /// - 名册必须与**已认证活跃 Client** 一致；缺任何一人 ⇒ **整局拒绝**，不缩编
        ///   （悄悄少人会让客户端认知与名册不一致）；
        /// - 不再有旧链可回退，也没有旧 <c>BattleManage</c>/<c>_uidToBattleIds</c> 路由可写；
        /// - 宿主未就绪/配置不全/manifest 非法时只报失败。
        /// </summary>
        private void StartFightingDedicatedServer(Server server, MatchResult matchResult)
        {
            PMDsLobbyHost host = PMDsLobbyHost.Instance;
            if (host == null || !host.IsRunning)
            {
                Logging.Debug.Log($"[PMDsMatch] DS 宿主未就绪，整局失败（已无旧链可回退） roomId={matchResult.roomId}");
                return;
            }

            Dictionary<string, int> teamID = new Dictionary<string, int>();
            int curMaxID = 1;
            int playerId = 1;
            List<PMDsRosterIdentity> roster = new List<PMDsRosterIdentity>();

            foreach (MatchedPlayerEntry player in matchResult.players)
            {
                Client c = server.GetActiveClient(player.uid);
                if (c == null)
                {
                    Logging.Debug.Log(
                        $"[PMDsMatch] 新链开局失败：玩家{player.uid}不在线/未认证，整局拒绝（不缩编） roomId={matchResult.roomId}");
                    return;
                }

                if (!teamID.ContainsKey(player.teamId))
                {
                    teamID.Add(player.teamId, curMaxID++);
                }

                roster.Add(new PMDsRosterIdentity(player.uid, playerId++, teamID[player.teamId], (int)c.PlayerHero));
            }

            if (roster.Count == 0)
            {
                Logging.Debug.Log($"[PMDsMatch] 新链开局失败：名册为空 roomId={matchResult.roomId}");
                return;
            }

            PMDsLobbyMatchRequest request = new PMDsLobbyMatchRequest();
            request.MatchId = matchResult.roomId;
            request.Roster = roster.ToArray();
            request.FightPattern = matchResult.fightPattern.ToString();

            PMDsLobbyStartReply reply = host.TryStartMatch(request);
            if (!reply.IsQueued)
            {
                Logging.Debug.Log($"[PMDsMatch] 新链开局被拒 roomId={matchResult.roomId}: {reply}");
                return;
            }

            Logging.Debug.Log(
                $"[PMDsMatch] 新链开局已提交 roomId={matchResult.roomId} pattern={request.FightPattern} players={roster.Count}");
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
                    //
                    // 原本这里还会调 client.UpdateMyselfInfo() 与 client.UpdateActiveFriendInfo()，两者都已移除：
                    //   - UpdateMyselfInfo 遍历 Client.FriendsDic，而该表在服务端从未被写入，所以一直是空转；
                    //   - UpdateActiveFriendInfo 推送的活跃好友包恒为空，而客户端
                    //     UIInvatingFriendPanel.UpDateActiveFriendInfo 遇到空 Playerspack 会直接清空好友列表，
                    //     等于「加入房间就把好友面板清空」。
                    // 好友列表现在由 FindFriendsInfo 按需从数据库派生（见 UserController.FindFriendsInfo）。
                    // 实时的好友状态推送留到 P3 用复制替代，不再维护这套半成品缓存。
                    client.PlayerState = PlayerState.PlayerOnRoom;

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
                    pack.Returncode = ReturnCode.Fail;
                    return pack;
                }
            }

            client.PlayerState = PlayerState.PlayerOnline;
            pack.Returncode = ReturnCode.NotRoom;
            return pack;
        }

        public MainPack CancelInviteFriend(Server server, Client client, MainPack pack)
        {
            string targetName = pack.Str;
            Client friend = server.GetClientByPlayerName(targetName);

            // 取消邀请的通知对象是「被邀请人」：他那边弹出的是接受/拒绝面板，
            // 收到本消息后应当把面板关掉（客户端 UIAplyInvateFriendPanel 的处理逻辑）。
            // 这里用独立包发送，避免与「回给发起者的响应」共用同一个实例而互相污染
            // （原实现把同一个 pack 既发给被邀请人又返回给发起者，并把 Str 改成了 "?"）。
            if (friend != null)
            {
                MainPack notify = new MainPack();
                notify.Requestcode = RequestCode.FriendRoom;
                notify.Actioncode = ActionCode.CancalInvateFriend;
                notify.Returncode = friend.FriendRoom != null ? ReturnCode.Fail : ReturnCode.Succeed;
                notify.Str = client.PlayerName;
                friend.Send(notify);
            }

            // 回给发起者：ActionNone 会被客户端路由层直接忽略，仅用于清掉它的 request_id 待确认项。
            pack.Returncode = ReturnCode.Succeed;
            pack.Str = string.Empty;
            pack.Actioncode = ActionCode.ActionNone;
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

        /// <summary>
        /// 房间内广播「某成员切换了英雄」（纯下行）。
        ///
        /// 原名 ChangeHero，与 ActionCode.ChangeHero 同名，会被旧反射路由在
        /// RequestCode.FriendRoom 下误命中，并因参数不匹配在 Invoke 时抛异常导致断连（计划 B1-2）。
        /// 改为 internal + 改名，彻底消除该风险。
        /// </summary>
        internal void BroadcastHeroChangedToRoom(Client client, MainPack pack)
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
            if (client.GetUserData.AplyAddFriend(pack, server))
            {
                pack.Returncode = ReturnCode.Succeed;
            }
            else pack.Returncode = ReturnCode.Fail;
            return pack;
        }
        public MainPack AcceptAddFriend(Server server, Client client, MainPack pack)
        {
            if (client.GetUserData.AcceptAddFriend(ref pack, client, server))
            {
                pack.Returncode = ReturnCode.Succeed;
            }
            else pack.Returncode = ReturnCode.Fail;
            return pack;
        }

        public MainPack RejectAddFriend(Server server, Client client, MainPack pack)
        {
            client.GetUserData.RejectAddFriend(pack, server);
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
            //1.3将账号密码信息录入用户库（内存实现，见 UserStore）
            if (client.GetUserData.Logon(pack))
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
            //0.3.查寻用户信息（内存实现；账号不存在时由 UserData.Login 自动建号）
            if (client.GetUserData.Login(pack))
            {
                pack.Returncode = ReturnCode.Succeed;

                // 计划 B4：原本「登记为活跃玩家」只发生在后续的 FindPlayerInfo 里，
                // 形成一个「已登录但 GetActiveClient 返回 null」的窗口期，
                // 使匹配/邀请/开战这些依赖在线集合的功能有几率看不到刚登录的玩家。
                // 现在登录成功即登记；FindPlayerInfo 仍会幂等地再调一次做兜底。
                server.RegisterActiveClient(client);
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

                // 幂等登记（Login 已登记过一次）。保留这里是为了兼容
                // 「重登录 / 旧连接残留」场景：同 uid 的旧连接会被顶掉。
                server.RegisterActiveClient(client);
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
            // UserData.FindFriendsInfo 从用户库（内存）拉取好友，并为每项写入 State
            // （server.GetPlayerState：在线好友是真实状态，离线为 PlayerOutline）。
            if (!client.GetUserData.FindFriendsInfo(ref pack, client, server))
            {
                pack.Returncode = ReturnCode.Fail;
                return pack;
            }

            // 客户端 UIInvatingFriendPanel.UpDateActiveFriendInfo 读的是 Playerspack，不是 Friendspack，
            // 所以这里要按客户端契约把「在线好友」搬到 Playerspack。
            //
            // 原实现是 `pack = client.GetActiveFriendInfoPack();`：它直接丢掉上面刚查出来的数据库结果，
            // 换成一个由 Client.FriendsDic 驱动的包；而该字典在服务端从未被写入，
            // 于是这里恒返回空列表，好友面板永远为空。这是计划 B5 真正的功能性缺陷。
            pack.Playerspack.Clear();
            for (int i = 0; i < pack.Friendspack.Count; i++)
            {
                PlayerPack friend = pack.Friendspack[i];
                if (friend.State != PlayerState.PlayerOutline)
                {
                    pack.Playerspack.Add(friend);
                }
            }

            pack.Actioncode = ActionCode.FindFriendsInfo;
            pack.Requestcode = RequestCode.User;
            pack.Returncode = ReturnCode.Succeed;
            return pack;
        }
        /// <summary>
        /// 修改名字
        /// </summary>
        /// <returns></returns>
        public MainPack UpdateName(Server server, Client client, MainPack pack)
        {
            if (client.GetUserData.UpdateName(pack))
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
                friendRoomController.BroadcastHeroChangedToRoom(client, pack);
            }            
            pack.Actioncode = ActionCode.ActionNone;
            return pack;
        }
    }
}
