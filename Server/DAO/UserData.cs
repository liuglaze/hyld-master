using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Google.Protobuf.Collections;
using MySql.Data.MySqlClient;
using SocketProto;
namespace Server.DAO
{

    /*
    SELECT * FROM `friends`


    查找lzj的所有好友信息
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

    //注册
    INSERT INTO `users` SET `UserName` = 'llf',`Password`='jjy',`name`=''; 
     */


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
        /// 注册
        /// </summary>
        /// <param name="pack"></param>
        /// <param name="mySqlConnection"></param>
        /// <returns></returns>
        public bool Logon(MainPack pack, MySqlConnection mySqlConnection)
        {
            //1.3将账号密码信息录入数据库
            string username = pack.Loginpack.Username;
            string password = pack.Loginpack.Password;

            // 重要安全提示: 密码在存入数据库前应进行哈希处理。
            // 例如: string hashedPassword = BCrypt.Net.BCrypt.HashPassword(password);

            string sql = "INSERT INTO `users` SET `UserName` = @username, `Password` = @password, `name` = '';";
            try
            {
                using (MySqlCommand cmd = new MySqlCommand(sql, mySqlConnection))
                {
                    cmd.Parameters.AddWithValue("@username", username);
                    cmd.Parameters.AddWithValue("@password", password); // 在生产环境中，这里应该使用哈希后的密码
                    cmd.ExecuteNonQuery();
                    return true;
                }
            }
            catch (MySqlException ex)
            {
                // 错误码 1062: 主键或唯一键冲突 (即用户名已存在)
                // 这种方式比先SELECT后INSERT更高效且能避免竞态条件
                if (ex.Number == 1062)
                {
                    Logging.Debug.Log("该账户已存在啊！！");
                }
                else
                {
                    Logging.Debug.Log("数据库错误: " + ex.Message);
                }
                return false;
            }
            catch (Exception ex)
            {
                Logging.Debug.Log("注册时发生未知错误: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 登陆
        /// </summary>
        /// <param name="pack"></param>
        /// <param name="mySqlConnection"></param>
        /// <returns></returns>
        public bool Login(MainPack pack, MySqlConnection mySqlConnection)
        {
            //0.3.查寻数据库用户信息
            string username = pack.Loginpack.Username;
            string password = pack.Loginpack.Password;

            Logging.Debug.Log(username + "      " + password);
            try
            {
                // 使用参数化查询防止SQL注入
                string sql = "SELECT * FROM `users` WHERE `UserName` = @username AND `Password` = @password";
                using (MySqlCommand cmd = new MySqlCommand(sql, mySqlConnection))
                {
                    cmd.Parameters.AddWithValue("@username", username);
                    cmd.Parameters.AddWithValue("@password", password); // 在生产环境中，应先从数据库获取哈希，再用BCrypt.Verify比较

                    using (MySqlDataReader read = cmd.ExecuteReader())
                    {
                        bool res = read.HasRows;
                        Logging.Debug.Log(res);
                        if (res)
                        {
                            UserName = username;
                        }
                        return res;
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Debug.Log(ex);
                return false;
            }
        }
        /// <summary>
        /// 查找好友信息
        /// </summary>
        /// <param name="pack"></param>
        /// <param name="client"></param>
        /// <param name="mySqlConnection"></param>
        /// <param name="server"></param>
        /// <returns></returns>
        public bool FindFriendsInfo(ref MainPack pack, Client client, MySqlConnection mySqlConnection, Server server)
        {
            Logging.Debug.Log(UserName + " ??? FindFriendsInfo  ??:" + UID);
            string sql = "SELECT DISTINCT u.`name`,u.`id` FROM `users` u JOIN `friends` f ON " +
                "(f.`UserID` = @userid AND f.`FriendID` = u.`id`) OR (f.`FriendID` = @userid AND u.`id` = f.`UserID`)";
            try
            {
                using (MySqlCommand comd = new MySqlCommand(sql, mySqlConnection))
                {
                    comd.Parameters.AddWithValue("@userid", UID);
                    using (MySqlDataReader read = comd.ExecuteReader())
                    {
                        PlayerPack myinfo = new PlayerPack
                        {
                            Playername = PlayerName,
                            Id = UID,
                            State = PlayerState.PlayerOnline
                        };
                        while (read.Read())
                        {
                            PlayerPack playerinfo = new PlayerPack
                            {
                                Playername = read["name"].ToString(),
                                Id = Convert.ToInt32(read["id"]),
                                State = server.GetPlayerState(Convert.ToInt32(read["id"]))
                            };

                            pack.Friendspack.Add(playerinfo);

                            Logging.Debug.Log($"找到好友: {playerinfo.Playername} (ID: {playerinfo.Id})");
                        }
                    }
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
        ///查找玩家信息
        /// </summary>
        /// <param name="pack"></param>
        /// <param name="client"></param>
        /// <returns></returns>
        public bool FindPlayerInfo(ref MainPack pack, Client client)
        {
            try
            {
                MySqlConnection mySqlConnection = client.GetMysqlConnecet;
                string username = pack.Loginpack.Username;
                Logging.Debug.Log(username + "  FindPlayerInfo");
                string sql = "SELECT `name`, `id` FROM `users` WHERE `UserName` = @username";
                using (MySqlCommand comd = new MySqlCommand(sql, mySqlConnection))
                {
                    comd.Parameters.AddWithValue("@username", username);
                    using (MySqlDataReader read = comd.ExecuteReader())
                    {
                        if (read.Read())
                        {
                            PlayerPack playerinfo = new PlayerPack();
                            playerinfo.Username = username;
                            playerinfo.Playername = read["name"].ToString();
                            playerinfo.Id = Convert.ToInt32(read["id"]);
                            UserName = username;
                            PlayerName = playerinfo.Playername;
                            UID = playerinfo.Id;
                            pack.UserInfopack = playerinfo;
                            Logging.Debug.Log($"找到玩家: {PlayerName} (ID: {UID})");
                        }
                        else
                        {
                            return false;
                        }
                    }
                }
                client.UpdateMyselfInfo();
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
            if (pack.UserInfopack == null )
                Logging.Debug.Log($"[ChangeHero-UserData-ERROR] UID={UID} UserName={UserName} 收到空Hero: pack={pack}");

            PlayerHero = pack.UserInfopack.Hero;
            Logging.Debug.Log($"[ChangeHero-UserData-AFTER] UID={UID} 新PlayerHero={PlayerHero}");
        }
        /// <summary>
        /// 更新名字
        /// </summary>
        /// <param name="pack"></param>
        /// <param name="mySqlConnection"></param>
        /// <returns></returns>
        public bool UpdateName(MainPack pack, MySqlConnection mySqlConnection)
        {
            string username = pack.Loginpack.Username;
            string newPlayerName = pack.Str;
            Logging.Debug.Log(username + "UpdateName:  " + newPlayerName);
            string sql = "UPDATE `users` SET `name` = @name WHERE `UserName` = @username;";
            try
            {
                using (MySqlCommand comd = new MySqlCommand(sql, mySqlConnection))
                {
                    comd.Parameters.AddWithValue("@name", newPlayerName);
                    comd.Parameters.AddWithValue("@username", username);
                    comd.ExecuteNonQuery();
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
        /// 申请加好友
        /// </summary>
        /// <param name="pack"></param>
        /// <param name="mySqlConnection"></param>
        /// <returns></returns>
        public bool AplyAddFriend(MainPack pack, MySqlConnection mySqlConnection, Server server)
        {
            string Playername = pack.UserInfopack.Playername;
            Logging.Debug.Log(Playername + "  !!!AplyAddFriend!!!:  " + pack.Str);
            ///根据姓名查找好友账号
            try
            {
                string sql = "SELECT `UserName` FROM `users` WHERE `id` = @id";
                string res = "";
                using (MySqlCommand comd = new MySqlCommand(sql, mySqlConnection))
                {
                    comd.Parameters.AddWithValue("@id", int.Parse(pack.Str));
                    using (MySqlDataReader read = comd.ExecuteReader())
                    {
                        if (read.Read())
                        {
                            res = read["UserName"].ToString();
                            Logging.Debug.Log("找到用户: " + res);
                        }
                    }
                }

                if (res != "")
                {
                    MainPack SendToFriendpack = new MainPack();
                    SendToFriendpack.Returncode = ReturnCode.AddFriend;
                    SendToFriendpack.Actioncode = ActionCode.AcceptAddFriend;
                    SendToFriendpack.Str = Playername;
                    server.GetClientByUserName(res).Send(SendToFriendpack);
                    Logging.Debug.Log("AplyAddFriend!!!!:  " + pack);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Logging.Debug.Log("ex  :" + ex.Message);
                return false;
            }

        }
        /// <summary>
        /// 同意加好友
        /// </summary>
        /// <param name="pack"></param>
        /// <param name="mySqlConnection"></param>
        /// <returns></returns>
        public bool AcceptAddFriend(ref MainPack pack, Client client, MySqlConnection mySqlConnection, Server server)
        {
            int userid = pack.UserInfopack.Id;
            Logging.Debug.Log(" AcceptAddFriend:  " + pack.Str);
            Client friend = server.GetClientByPlayerName(pack.Str);
            string sql = "INSERT INTO `friends` SET `UserID` = @userid, `FriendID` = @friendid;";
            try
            {
                using (MySqlCommand comd = new MySqlCommand(sql, mySqlConnection))
                {
                    comd.Parameters.AddWithValue("@userid", userid);
                    comd.Parameters.AddWithValue("@friendid", friend.UID);
                    comd.ExecuteNonQuery();
                }

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
        /// 拒绝加好友
        /// </summary>
        /// <param name="pack"></param>
        /// <param name="mySqlConnection"></param>
        /// <param name="server"></param>
        /// <returns></returns>
        public bool RejectAddFriend(MainPack pack, MySqlConnection mySqlConnection, Server server)
        {
            int userid = pack.UserInfopack.Id;
            Logging.Debug.Log(" RejectAddFriend:  " + pack.Str);
            Client friend = server.GetClientByPlayerName(pack.Str);
            MainPack SendToFriendpack = new MainPack();
            SendToFriendpack.Returncode = ReturnCode.Fail;
            SendToFriendpack.Actioncode = ActionCode.RejectAddFriend;
            friend.Send(SendToFriendpack);
            return false;
        }
        public void BordCaseToFriendLogout(MySqlConnection mySqlConnection, Server server, Client client, Action UpdateActiveFriendInfo)
        {
            Logging.Debug.Log(UserName + "  BordCaseToFriendLogout  :" + UID);
            string sql = "SELECT DISTINCT u.`name`,u.`id` FROM `users` u JOIN `friends` f ON (f.`UserID` = @userid AND f.`FriendID` = u.`id`) OR (f.`FriendID` = @userid AND u.`id` = f.`UserID`)";
            try
            {
                if (PlayerName == null)
                {
                    return;
                }

                using (MySqlCommand comd = new MySqlCommand(sql, mySqlConnection))
                {
                    comd.Parameters.AddWithValue("@userid", UID);
                    using (MySqlDataReader read = comd.ExecuteReader())
                    {
                        while (read.Read())
                        {
                            int friendId = Convert.ToInt32(read["id"]);
                            PlayerState friendState = server.GetPlayerState(friendId);

                            //如果好友在线，就告诉他，老子下号了！
                            if (friendState != PlayerState.PlayerOutline)
                            {
                                Client friendclient = server.GetActiveClient(friendId);
                                if (friendclient != null)
                                {
                                    friendclient.RemoveFriend(client.UID);
                                    Logging.Debug.Log($"通知好友 {read["name"]} 我已下线。");
                                }
                            }
                        }
                    }
                }
                client.PlayerState = PlayerState.PlayerOutline;
                client.UpdateMyselfInfo();
            }
            catch (Exception ex)
            {
                Logging.Debug.Log(ex);
            }
        }
    }
}