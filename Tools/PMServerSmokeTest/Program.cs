using System;
using System.Net.Sockets;
using System.Threading;
using Google.Protobuf;
using SocketProto;

namespace PMServerSmokeTest
{
    /// <summary>
    /// 大厅服务端冒烟测试：以真实 TCP 客户端身份连接服务端，逐条验证 RPC 行为。
    ///
    /// 分帧格式（来自 Server/Tool/Message.cs）：
    ///   [int32 小端 长度][protobuf MainPack 包体]
    ///
    /// 必须建模的两条服务端规则（否则测试会「通过但理由错误」）：
    ///
    ///   规则 A：**一条 TCP 连接只能登录一次**
    ///     UserController.Login 开头是 `if (client.UserName != null) return Fail;`
    ///     所以同一连接上第二次 Login 必被拒。想重登必须开新连接。
    ///
    ///   规则 B：**同一账号同时只能有一条活跃连接**（防顶号）
    ///     Login 里还有 `server.GetActiveClientByUserName(...) != null → Fail`，
    ///     登录成功会 RegisterActiveClient(连接)。
    ///     这条规则会让「换新连接重登同账号」也被拒——除非旧连接已断开并被服务端察觉。
    ///
    ///   规则 B 的副作用（本工具真实踩到）：如果只想验证「密码错误应失败」，
    ///   却让同账号的另一条连接还在线，那么失败原因会是「重复登录」而不是「密码错误」，
    ///   测试看起来通过、实际没测到目标。因此本工具的每个登录场景都先断开上一条同账号连接
    ///   并等待服务端感知（见 CloseAndSettle），再进行下一步。
    ///
    /// 覆盖场景：
    ///   1. 自动建号（账号不存在 → 成功）
    ///   2. 重复登录保护（同账号第二条连接 → 失败）  ← 规则 B
    ///   3. 已存在账号 + 正确密码（旧连接断开后 → 成功）
    ///   4. FindPlayerInfo 回填 Id / 昵称
    ///   5. FindFriendsInfo 可达（新账号好友为空）
    ///   6. UpdateName 生效并可读回
    ///   7. 错误密码 → 失败（此时无同账号活跃连接，失败原因确实是密码）
    ///   8. Logon 新账号 → 成功
    ///   9. Logon 重复账号 → 失败
    ///  10. request_id 回带（计划 P1 / B3）
    ///
    /// 退出码：0 全通过；1 有失败。
    /// </summary>
    internal static class Program
    {
        private static int _checks;
        private static int _failures;

        private static string _host = "127.0.0.1";
        private static int _port = 7778;

        /// <summary>断开连接后等待服务端感知的时间（毫秒）。规则 B 依赖它。</summary>
        private const int DisconnectSettleMs = 800;

        private static int Main(string[] args)
        {
            if (args != null && args.Length >= 2)
            {
                _host = args[0];
                int parsed;
                if (int.TryParse(args[1], out parsed))
                {
                    _port = parsed;
                }
            }

            Console.WriteLine("== 大厅服务端冒烟测试 ==");
            Console.WriteLine("目标: " + _host + ":" + _port);
            Console.WriteLine();

            // 每次运行用不同账号，保证可重复执行（内存库在服务端进程内）
            string stamp = DateTime.Now.ToString("HHmmss");
            string autoUser = "smoke_auto_" + stamp;
            string logonUser = "smoke_logon_" + stamp;

            try
            {
                RunScenario1AutoCreate(autoUser);
                RunScenario2DuplicateLogin(autoUser);
                RunScenario3LoginExisting(autoUser);
                RunScenario4FindPlayerInfo(autoUser);
                RunScenario5FindFriendsInfo(autoUser);
                RunScenario6UpdateName(autoUser);
                RunScenario7WrongPassword(autoUser);
                RunScenario8LogonNew(logonUser);
                RunScenario9LogonDuplicate(logonUser);
                RunScenario10RequestIdEcho(logonUser);
            }
            catch (SocketException ex)
            {
                Console.WriteLine();
                Console.WriteLine("  [FAIL] 无法连接服务端：" + ex.Message);
                Console.WriteLine("         请先启动服务端：dotnet Server/bin/Debug/net8.0/Server.dll");
                _failures++;
                _checks++;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("  [FAIL] 未处理异常：" + ex);
                _failures++;
                _checks++;
            }

            Console.WriteLine();
            Console.WriteLine("== 汇总: " + _checks + " 项检查, " + _failures + " 项失败 ==");
            return _failures == 0 ? 0 : 1;
        }

        // ==================== 场景 ====================

        private static void RunScenario1AutoCreate(string user)
        {
            Section("场景 1：Login 自动建号（账号不存在 → 应成功）");

            TcpClient client = Connect();
            MainPack resp = Exchange(client.GetStream(), MakeLogin(user, "pw123"));
            CheckEqual("Returncode", resp.Returncode.ToString(), ReturnCode.Succeed.ToString());

            // 这条连接保持在线，供场景 2 验证「重复登录保护」
            _liveClient = client;
        }

        private static void RunScenario2DuplicateLogin(string user)
        {
            Section("场景 2：同账号第二条连接 → 必须被拒（防止顶号，规则 B）");

            using (TcpClient second = Connect())
            {
                MainPack resp = Exchange(second.GetStream(), MakeLogin(user, "pw123"));
                CheckEqual("Returncode", resp.Returncode.ToString(), ReturnCode.Fail.ToString());
            }
        }

        private static void RunScenario3LoginExisting(string user)
        {
            Section("场景 3：断开旧连接后登录已存在账号（密码正确）→ 应成功");

            CloseAndSettle(_liveClient);
            _liveClient = null;

            _liveClient = Connect();
            MainPack resp = Exchange(_liveClient.GetStream(), MakeLogin(user, "pw123"));
            CheckEqual("Returncode", resp.Returncode.ToString(), ReturnCode.Succeed.ToString());
        }

        private static void RunScenario4FindPlayerInfo(string user)
        {
            Section("场景 4：FindPlayerInfo → 应回填 Id 与昵称");

            MainPack resp = Exchange(_liveClient.GetStream(), MakeFindPlayerInfo(user));
            CheckEqual("Returncode", resp.Returncode.ToString(), ReturnCode.Succeed.ToString());
            CheckTrue("UserInfopack 非空", resp.UserInfopack != null);
            if (resp.UserInfopack != null)
            {
                CheckTrue("Id > 0", resp.UserInfopack.Id > 0);
                CheckEqual("Username 回填", resp.UserInfopack.Username, user);
                CheckTrue("Playername 非空（自动建号默认取账号名）", !string.IsNullOrEmpty(resp.UserInfopack.Playername));
            }
        }

        private static void RunScenario5FindFriendsInfo(string user)
        {
            Section("场景 5：FindFriendsInfo → 分表可达（新账号好友列表为空）");

            MainPack resp = Exchange(_liveClient.GetStream(), MakeFindFriendsInfo(user));
            CheckEqual("Returncode", resp.Returncode.ToString(), ReturnCode.Succeed.ToString());
            CheckEqual("好友数为 0（新账号）", resp.Friendspack.Count, 0);
        }

        private static void RunScenario6UpdateName(string user)
        {
            Section("场景 6：UpdateName → 改名应生效并可读回");

            string newName = "昵称_" + user;

            MainPack req = new MainPack();
            req.Requestcode = RequestCode.User;
            req.Actioncode = ActionCode.UpdateName;
            LoginPack lp = new LoginPack();
            lp.Username = user;
            req.Loginpack = lp;
            req.Str = newName;

            MainPack resp = Exchange(_liveClient.GetStream(), req);
            CheckEqual("Returncode", resp.Returncode.ToString(), ReturnCode.Succeed.ToString());

            // 读回验证：改名必须真的落到用户库，不能只是回了个 Succeed
            MainPack resp2 = Exchange(_liveClient.GetStream(), MakeFindPlayerInfo(user));
            CheckTrue("改名后 Playername 已更新",
                resp2.UserInfopack != null && resp2.UserInfopack.Playername == newName);
        }

        private static void RunScenario7WrongPassword(string user)
        {
            Section("场景 7：错误密码 → 必须失败（此时无同账号活跃连接，失败原因确实是密码）");

            // 先断开，确保不会因「重复登录」而 Fail（那会让本场景通过但没测到密码校验）
            CloseAndSettle(_liveClient);
            _liveClient = null;

            using (TcpClient client = Connect())
            {
                MainPack resp = Exchange(client.GetStream(), MakeLogin(user, "WRONG_pw"));
                CheckEqual("Returncode", resp.Returncode.ToString(), ReturnCode.Fail.ToString());
            }
        }

        private static void RunScenario8LogonNew(string user)
        {
            Section("场景 8：Logon 新账号 → 应成功");

            using (TcpClient client = Connect())
            {
                MainPack resp = Exchange(client.GetStream(), MakeLogon(user, "pw456"));
                CheckEqual("Returncode", resp.Returncode.ToString(), ReturnCode.Succeed.ToString());
            }
        }

        private static void RunScenario9LogonDuplicate(string user)
        {
            Section("场景 9：Logon 重复账号 → 必须失败（原有语义）");

            using (TcpClient client = Connect())
            {
                MainPack resp = Exchange(client.GetStream(), MakeLogon(user, "pw456"));
                CheckEqual("Returncode", resp.Returncode.ToString(), ReturnCode.Fail.ToString());
            }
        }

        private static void RunScenario10RequestIdEcho(string user)
        {
            Section("场景 10：request_id 回带（计划 P1 / B3 的服务端收口）");

            const int probeId = 424242;

            // logonUser 只做过 Logon（不登记活跃连接），因此可以直接登录
            using (TcpClient client = Connect())
            {
                MainPack req = MakeLogin(user, "pw456");
                req.RequestId = probeId;

                MainPack resp = Exchange(client.GetStream(), req);
                CheckEqual("登录成功（前置条件）", resp.Returncode.ToString(), ReturnCode.Succeed.ToString());
                CheckEqual("回包 request_id 原样带回", resp.RequestId, probeId);
            }
        }

        // ==================== 连接辅助 ====================

        private static TcpClient _liveClient;

        private static TcpClient Connect()
        {
            TcpClient client = new TcpClient();
            client.Connect(_host, _port);
            client.NoDelay = true;
            return client;
        }

        /// <summary>
        /// 关闭连接并等待服务端感知断开。
        /// 规则 B（同账号仅一条活跃连接）依赖服务端处理 disconnect，
        /// 不等待就会让后续登录被「重复登录」误拒。
        /// </summary>
        private static void CloseAndSettle(TcpClient client)
        {
            if (client == null)
            {
                return;
            }

            try
            {
                client.Close();
            }
            catch
            {
                // 关闭失败不影响后续判断
            }

            Thread.Sleep(DisconnectSettleMs);
        }

        // ==================== 协议辅助 ====================

        private static MainPack MakeLogin(string user, string password)
        {
            MainPack req = new MainPack();
            req.Requestcode = RequestCode.User;
            req.Actioncode = ActionCode.Login;
            LoginPack lp = new LoginPack();
            lp.Username = user;
            lp.Password = password;
            req.Loginpack = lp;
            return req;
        }

        private static MainPack MakeLogon(string user, string password)
        {
            MainPack req = new MainPack();
            req.Requestcode = RequestCode.User;
            req.Actioncode = ActionCode.Logon;
            LoginPack lp = new LoginPack();
            lp.Username = user;
            lp.Password = password;
            req.Loginpack = lp;
            return req;
        }

        private static MainPack MakeFindPlayerInfo(string user)
        {
            MainPack req = new MainPack();
            req.Requestcode = RequestCode.User;
            req.Actioncode = ActionCode.FindPlayerInfo;
            LoginPack lp = new LoginPack();
            lp.Username = user;
            req.Loginpack = lp;
            return req;
        }

        private static MainPack MakeFindFriendsInfo(string user)
        {
            MainPack req = new MainPack();
            req.Requestcode = RequestCode.User;
            req.Actioncode = ActionCode.FindFriendsInfo;
            LoginPack lp = new LoginPack();
            lp.Username = user;
            req.Loginpack = lp;
            return req;
        }

        /// <summary>发送请求并等待回包；只接受 actioncode 与本请求一致的那一帧。</summary>
        private static MainPack Exchange(NetworkStream stream, MainPack request)
        {
            byte[] body = request.ToByteArray();
            byte[] head = BitConverter.GetBytes(body.Length);

            stream.Write(head, 0, 4);
            stream.Write(body, 0, body.Length);
            stream.Flush();

            for (int attempt = 0; attempt < 5; attempt++)
            {
                MainPack pack = ReadPack(stream);
                if (pack == null)
                {
                    break;
                }

                if (pack.Actioncode == request.Actioncode)
                {
                    return pack;
                }
            }

            MainPack empty = new MainPack();
            empty.Returncode = ReturnCode.Fail;
            return empty;
        }

        private static MainPack ReadPack(NetworkStream stream)
        {
            byte[] head = ReadExactly(stream, 4);
            if (head == null)
            {
                return null;
            }

            int len = BitConverter.ToInt32(head, 0);
            if (len < 0 || len > 16 * 1024 * 1024)
            {
                return null;
            }

            byte[] body = ReadExactly(stream, len);
            if (body == null)
            {
                return null;
            }

            return MainPack.Parser.ParseFrom(body);
        }

        private static byte[] ReadExactly(NetworkStream stream, int count)
        {
            byte[] buf = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buf, read, count - read);
                if (n <= 0)
                {
                    return null;
                }

                read += n;
            }

            return buf;
        }

        // ==================== 输出辅助 ====================

        private static void Section(string title)
        {
            Console.WriteLine(title);
        }

        private static void CheckEqual(string name, int actual, int expected)
        {
            Report(name, actual == expected, "实际=" + actual + " 期望=" + expected);
        }

        private static void CheckEqual(string name, string actual, string expected)
        {
            Report(name, string.Equals(actual, expected, StringComparison.Ordinal),
                "实际='" + (actual ?? "<null>") + "' 期望='" + (expected ?? "<null>") + "'");
        }

        private static void CheckTrue(string name, bool ok)
        {
            Report(name, ok, "实际=False 期望=True");
        }

        private static void Report(string name, bool ok, string detail)
        {
            _checks++;
            if (ok)
            {
                Console.WriteLine("  [PASS] " + name);
                return;
            }

            _failures++;
            Console.WriteLine("  [FAIL] " + name + "  (" + detail + ")");
        }
    }
}
