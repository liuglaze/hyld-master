using System;
using Google.Protobuf.Collections;
using SocketProto;

namespace Server.DAO
{
    /// <summary>
    /// 单个连接的用户数据门面（每个 <see cref="Client"/> 持有一个实例）。
    ///
    /// 本类持有「这条连接当前是谁」的会话状态（UID / UserName / PlayerName / PlayerHero），
    /// 而账号与好友的**共享数据**放在 <see cref="UserStore"/>（进程内内存库）。
    ///
    /// 历史沿革：原实现直接对 MySQL 执行 SQL。数据库已按需求移除（本工程不做账号持久化），
    /// 因此这里全部改为读写 <see cref="UserStore"/>。各方法的语义与原 SQL 版逐条对齐，
    /// 有意偏离之处都已在方法注释中标出。
    ///
    /// 与原实现的对照（原 SQL 保留在文件末尾注释里，便于回溯）：
    ///   Logon           <- INSERT INTO users ...
    ///   Login           <- SELECT * FROM users WHERE UserName=@u AND Password=@p
    ///   FindPlayerInfo  <- SELECT name, id FROM users WHERE UserName=@u
    ///   FindFriendsInfo <- SELECT u.name,u.id FROM users u JOIN friends f ON (...)
    ///   UpdateName      <- UPDATE users SET name=@n WHERE UserName=@u
    ///   AplyAddFriend   <- SELECT UserName FROM users WHERE id=@id
    ///   AcceptAddFriend <- INSERT INTO friends SET UserID=@u, FriendID=@f
    ///   RejectAddFriend <- 无 SQL，只通知对方
    ///   BordCaseToFriendLogout <- 同 FindFriendsInfo 的查询
    /// </summary>
    class UserData
    {
        public int UID
        {
            get; private set;
        }
        public Hero PlayerHero
        {
            get; private set;
        }
        public string UserName
        {
            get; private set;
        }
        public string PlayerName
        {
            get; private set;
        }

        /// <summary>
        /// 注册。
        /// 对应原实现 `INSERT INTO users SET UserName=@u, Password=@p, name=''`，
        /// 主键冲突（MySQL 1062）返回 false。
        /// </summary>
        public bool Logon(MainPack pack)
        {
            string username = pack.Loginpack.Username;
            string password = pack.Loginpack.Password;

            string error;
            if (UserStore.TryRegister(username, password, out error))
            {
                Logging.Debug.Log($"[Logon] 注册成功 username={username} (当前账号数={UserStore.UserCount})");
                return true;
            }

            // 与原文案保持一致，便于对照日志
            if (error == "该账户已存在")
            {
                Logging.Debug.Log("该账户已存在啊！！");
            }
            else
            {
                Logging.Debug.Log("[Logon] 注册失败：" + error + " username=" + username);
            }
            return false;
        }

        /// <summary>
        /// 登录。
        /// 对应原实现 `SELECT * FROM users WHERE UserName=@u AND Password=@p`：
        /// 命中则记录 UserName 并返回 true。
        ///
        /// 有意扩展：账号不存在时自动建号（开发环境不需要先注册）。
        /// 账号存在但密码不符时仍失败——保留密码校验，避免原语义被悄悄削弱。
        /// </summary>
        public bool Login(MainPack pack)
        {
            string username = pack.Loginpack.Username;
            string password = pack.Loginpack.Password;

            Logging.Debug.Log(username + "      " + password);

            bool created;
            string error;
            if (!UserStore.TryLoginOrCreate(username, password, out created, out error))
            {
                Logging.Debug.Log("[Login] 登录失败：" + error + " username=" + username);
                return false;
            }

            UserName = username;

            if (created)
            {
                Logging.Debug.Log($"[Login] 账号不存在，已自动建号 username={username} (当前账号数={UserStore.UserCount})");
            }
            else
            {
                Logging.Debug.Log($"[Login] 登录成功 username={username}");
            }
            return true;
        }

        /// <summary>
        /// 查找好友信息，结果写入 <c>pack.Friendspack</c>。
        /// 对应原实现的双向好友查询；状态从 <paramref name="server"/> 实时取。
        ///
        /// 原实现里有一个从未被使用的局部变量 PlayerPack myinfo（死代码），此处不再保留。
        /// </summary>
        public bool FindFriendsInfo(ref MainPack pack, Client client, Server server)
        {
            Logging.Debug.Log(UserName + " ??? FindFriendsInfo  ??:" + UID);

            try
            {
                System.Collections.Generic.List<UserSnapshot> friends = UserStore.GetFriends(UID);
                for (int i = 0; i < friends.Count; i++)
                {
                    UserSnapshot friend = friends[i];

                    PlayerPack playerinfo = new PlayerPack
                    {
                        Playername = friend.Name,
                        Id = friend.Id,
                        State = server.GetPlayerState(friend.Id)
                    };

                    pack.Friendspack.Add(playerinfo);

                    Logging.Debug.Log($"找到好友: {playerinfo.Playername} (ID: {playerinfo.Id})");
                }

                return true;
            }
            catch (Exception ex)
            {
                Logging.Debug.Log(ex);
                return false;
            }
        }

        /// <summary>
        /// 查找玩家信息，结果写入 <c>pack.UserInfopack</c>，并更新本连接的会话身份。
        /// 对应 `SELECT name, id FROM users WHERE UserName = @u`；查不到返回 false。
        /// </summary>
        public bool FindPlayerInfo(ref MainPack pack, Client client)
        {
            try
            {
                string username = pack.Loginpack.Username;
                Logging.Debug.Log(username + "  FindPlayerInfo");

                UserSnapshot user;
                if (!UserStore.TryGetByName(username, out user))
                {
                    Logging.Debug.Log("[FindPlayerInfo] 账号不存在 username=" + username);
                    return false;
                }

                PlayerPack playerinfo = new PlayerPack();
                playerinfo.Username = username;
                playerinfo.Playername = user.Name;
                playerinfo.Id = user.Id;

                UserName = username;
                PlayerName = playerinfo.Playername;
                UID = playerinfo.Id;

                pack.UserInfopack = playerinfo;
                Logging.Debug.Log($"找到玩家: {PlayerName} (ID: {UID})");
                return true;
            }
            catch (Exception ex)
            {
                Logging.Debug.Log(ex);
                return false;
            }
        }

        public void ChangeHero(MainPack pack)
        {
            Logging.Debug.Log
            ($"[ChangeHero-UserData-BEFORE] UID={UID} PlayerHero={PlayerHero},收到UserInfopack.Hero ={ pack.UserInfopack?.Hero}");

            // 判空报警
            if (pack.UserInfopack == null)
                Logging.Debug.Log($"[ChangeHero-UserData-ERROR] UID={UID} UserName={UserName} 收到空Hero: pack={pack}");

            PlayerHero = pack.UserInfopack.Hero;
            Logging.Debug.Log($"[ChangeHero-UserData-AFTER] UID={UID} 新PlayerHero={PlayerHero}");
        }

        /// <summary>
        /// 改名。对应 `UPDATE users SET name=@n WHERE UserName=@u`。
        /// </summary>
        public bool UpdateName(MainPack pack)
        {
            string username = pack.Loginpack.Username;
            string newPlayerName = pack.Str;
            Logging.Debug.Log(username + "UpdateName:  " + newPlayerName);

            try
            {
                string error;
                if (!UserStore.TryRename(username, newPlayerName, out error))
                {
                    Logging.Debug.Log("[UpdateName] " + error + " username=" + username);
                    return false;
                }

                UserName = username;
                PlayerName = newPlayerName;
                return true;
            }
            catch (Exception ex)
            {
                Logging.Debug.Log(ex.Message);
                return false;
            }
        }

        //*********************加好友*************************//

        /// <summary>
        /// 申请加好友：按 ID 找到目标账号，向其连接发一条「有人申请加你」的通知。
        /// 对应 `SELECT UserName FROM users WHERE id = @id`。
        ///
        /// 注意（原样保留的行为）：这条通知用的是 ActionCode.AcceptAddFriend 且不写任何好友表，
        /// 真正的落库发生在对方回复 AcceptAddFriend 时。
        /// </summary>
        public bool AplyAddFriend(MainPack pack, Server server)
        {
            string Playername = pack.UserInfopack.Playername;
            Logging.Debug.Log(Playername + "  !!!AplyAddFriend!!!:  " + pack.Str);

            try
            {
                int targetId;
                if (!int.TryParse(pack.Str, out targetId))
                {
                    Logging.Debug.Log("[AplyAddFriend] pack.Str 不是合法 ID：" + pack.Str);
                    return false;
                }

                UserSnapshot target;
                if (!UserStore.TryGetById(targetId, out target))
                {
                    Logging.Debug.Log("[AplyAddFriend] 找不到 ID=" + targetId + " 的账号");
                    return false;
                }

                Logging.Debug.Log("找到用户: " + target.UserName);

                MainPack SendToFriendpack = new MainPack();
                SendToFriendpack.Returncode = ReturnCode.AddFriend;
                SendToFriendpack.Actioncode = ActionCode.AcceptAddFriend;
                SendToFriendpack.Str = Playername;
                server.GetClientByUserName(target.UserName).Send(SendToFriendpack);
                Logging.Debug.Log("AplyAddFriend!!!!:  " + pack);
                return true;
            }
            catch (Exception ex)
            {
                Logging.Debug.Log("ex  :" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 同意加好友：写入好友边，并通知对方。
        /// 对应 `INSERT INTO friends SET UserID=@u, FriendID=@f`（单向边，查询时双向匹配）。
        ///
        /// 有意加固：好友边用**服务端权威的 UID**，而不是客户端传来的 pack.UserInfopack.Id。
        /// 原实现用后者，一旦客户端没走 FindPlayerInfo（Id 仍为 0），就会写入一条永远匹配不上的脏边；
        /// 而这条边在内存库里会一直存在到进程结束。两者不一致时打日志，便于发现客户端状态问题。
        /// </summary>
        public bool AcceptAddFriend(ref MainPack pack, Client client, Server server)
        {
            Logging.Debug.Log(" AcceptAddFriend:  " + pack.Str);

            Client friend = server.GetClientByPlayerName(pack.Str);
            if (friend == null)
            {
                Logging.Debug.Log("[AcceptAddFriend] 找不到昵称为 '" + pack.Str + "' 的在线连接");
                return false;
            }

            try
            {
                int userId = UID;
                if (pack.UserInfopack != null && pack.UserInfopack.Id != UID)
                {
                    Logging.Debug.Log($"[AcceptAddFriend] 客户端自称 Id={pack.UserInfopack.Id} 与服务端 UID={UID} 不一致，以服务端为准");
                }

                if (userId <= 0)
                {
                    Logging.Debug.Log("[AcceptAddFriend] 本连接尚未完成身份初始化（UID<=0），拒绝写好友边");
                    return false;
                }

                UserStore.AddFriend(userId, friend.UID);
                Logging.Debug.Log($"[AcceptAddFriend] 已建立好友边 {userId} <-> {friend.UID} (当前好友边数={UserStore.FriendEdgeCount})");

                MainPack SendToFriendpack = new MainPack();
                SendToFriendpack.Returncode = ReturnCode.Succeed;
                SendToFriendpack.Actioncode = ActionCode.AcceptAddFriend;
                SendToFriendpack.Str = PlayerName + "#" + server.GetClientByPlayerName(PlayerName).UID;

                ///TODO：同意加好友可能要加入状态回滚
                friend.Send(SendToFriendpack);
                pack.Str = friend.PlayerName + "#" + server.GetClientByPlayerName(friend.PlayerName).UID;
                return true;
            }
            catch (Exception ex)
            {
                Logging.Debug.Log(ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 拒绝加好友：只通知对方，不写任何数据。
        ///
        /// 返回值恒为 false 是**原实现的既有行为**（控制器随后也无视该返回值、一律回 Fail），
        /// 这里原样保留，改动它会影响客户端对 ReturnCode 的判断。
        /// </summary>
        public bool RejectAddFriend(MainPack pack, Server server)
        {
            int userid = pack.UserInfopack.Id;
            Logging.Debug.Log(" RejectAddFriend:  " + pack.Str);
            Client friend = server.GetClientByPlayerName(pack.Str);
            if (friend == null)
            {
                Logging.Debug.Log("[RejectAddFriend] 找不到昵称为 '" + pack.Str + "' 的在线连接");
                return false;
            }

            MainPack SendToFriendpack = new MainPack();
            SendToFriendpack.Returncode = ReturnCode.Fail;
            SendToFriendpack.Actioncode = ActionCode.RejectAddFriend;
            friend.Send(SendToFriendpack);
            return false;
        }

        /// <summary>
        /// 断线时处理好友相关收尾：遍历好友、对在线者打日志，并把本连接置为离线。
        ///
        /// 说明：原实现里紧随其后的「主动通知好友我下线了」依赖 Client.FriendsDic，
        /// 而该表在服务端从未被写入，通知实际从未发生（详见迁移计划 B5）。
        /// 该缺陷的修复放在 P3 用复制机制重做，这里保持「只判定与记录」。
        /// </summary>
        public void BordCaseToFriendLogout(Server server, Client client)
        {
            Logging.Debug.Log(UserName + "  BordCaseToFriendLogout  :" + UID);

            try
            {
                if (PlayerName == null)
                {
                    return;
                }

                System.Collections.Generic.List<UserSnapshot> friends = UserStore.GetFriends(UID);
                for (int i = 0; i < friends.Count; i++)
                {
                    UserSnapshot friend = friends[i];
                    PlayerState friendState = server.GetPlayerState(friend.Id);

                    if (friendState != PlayerState.PlayerOutline)
                    {
                        Logging.Debug.Log($"[FriendPresence] 好友 {friend.Name}(id={friend.Id}) 在线，但当前无推送机制（待 P3 复制实现）");
                    }
                }

                client.PlayerState = PlayerState.PlayerOutline;
            }
            catch (Exception ex)
            {
                Logging.Debug.Log(ex);
            }
        }
    }

    /*
    ---- 原 MySQL 实现使用的 SQL（已随数据库移除，此处留档便于回溯语义）----

    SELECT * FROM `friends`

    查找 lzj 的所有好友信息
    SELECT DISTINCT u.`name`,u.`id`
    FROM `users` u
    CROSS JOIN `friends` f
    WHERE (f.`UserID`= 12 AND f.`FriendID`=u.`id`)OR(f.`FriendID`=12 AND u.`id`=f.`UserID`)

    改名
    UPDATE `users` SET `name` = '' WHERE `UserName` = 'lzj';

    添加好友
    INSERT INTO `friends` SET `UserID`= 12,`FriendID`=17;

    获取玩家名字
    SELECT `name` FROM `users` WHERE `UserName` = 'LZJ'

    注册
    INSERT INTO `users` SET `UserName` = 'llf',`Password`='jjy',`name`='';
     */
}
