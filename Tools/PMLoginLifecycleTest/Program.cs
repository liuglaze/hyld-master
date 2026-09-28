using System;
using System.Collections.Generic;
using System.Text;
using Server;
using Server.DAO;
using SocketProto;

namespace PMLoginLifecycleTest
{
    /// <summary>
    /// T-LOOP2 验收门禁：登录身份写入 / uid=0 残留 / 活跃索引引用一致删除。
    ///
    /// 事实来源：
    ///   `Docs/plans/net-architecture-migration.md` 的 T-LOOP2 行与 T-LOOP1 收口段；
    ///   实机锚点 `Server/log/2026-09-26_15时34分34秒/server.log`：
    ///     登录时 `ACTIVE-ADD id=0`，查信息后 `ACTIVE-ADD id=2`，断开 uid2 后
    ///     同账号仍因 UID2 活跃被拒 —— 因为键 0 仍指向同一 Client（UID 后来才变成 2），
    ///     而 RemoveActiveClient 只按当时的 UID 删了键 2。
    ///
    /// 门禁结构（逐条对应 T-LOOP2 的验收口径）：
    ///   A 登录身份写入：真实 `UserData.Login` 成功后必须有真实 UID / PlayerName；
    ///     密码错误 / 空账号名仍失败（既有密码校验不得被削弱）。
    ///   B uid=0 残留可执行负例：完整复现「登录 → FindPlayerInfo → 断线 → 同账号重登」，
    ///     断线后活跃索引必须为空，同账号第二条连接必须能重新登记。
    ///   C UID 别名清理：同一连接 UID 变化产生的旧键必须被清掉，索引里只留一个键。
    ///   D 删除按引用一致：旧连接的迟到 Close 不得误删同 uid 的新连接（关键负例）。
    ///   E 真正重复登录：同 uid 的另一条连接登记被拒（不顶号、不踢人），幂等登记不新增键；
    ///     UID&lt;=0 / 空账号名一律拒绝登记。
    ///
    /// 被测对象是**生产源**（只读链接，见 csproj）：
    ///   · `Server/DAO/UserData.cs`（Login / FindPlayerInfo 的真实实现）；
    ///   · `Server/DAO/UserStore.cs`（UID 分配真值）；
    ///   · `Server/Server/PMActiveClientIndex.cs`（Server 活跃表的登记/删除/查找判定）。
    /// Server.cs 只保留日志与委派，因此这里驱动索引等价于驱动生产行为；
    /// 本门禁不含任何「对源码做字符串搜索」的伪测试。
    ///
    /// 注意事项：
    ///   · 不建真实 TCP、不启服务、不读写用户正在运行的 Server.dll / Lobby；
    ///   · 生产日志门面 `Logging.Debug` 仍被链接（`UserData` 会调用它），
    ///     这里把 `LogSeverityLevel` 置 0 只关掉 console 打印，避免淹没门禁结论；
    ///     它不影响被验证的逻辑，也不写任何文件（`TraceSavePath` 为空）。
    ///   · `UserStore` 是进程内静态库，各场景使用互不相同的账号名，避免互相污染。
    ///
    /// 退出码：0 = 全部通过；1 = 有失败。
    /// </summary>
    internal static class Program
    {
        private static int _checks;
        private static int _failures;
        private static int _sectionsPassed;
        private static int _sectionsFailed;
        private static readonly List<string> _failureLog = new List<string>();

        private static int Main()
        {
            // 关掉 console 打印（默认 OnMessage 处理器），保持门禁结论可读；
            // 日志本身仍在 Logging.Debug 的队列里，无文件输出（TraceSavePath 为空）。
            Logging.Debug.LogSeverityLevel = 0;

            Console.WriteLine("=== T-LOOP2：登录身份写入 / uid=0 残留 / 活跃索引引用一致删除 ===");
            Console.WriteLine("事实来源：Docs/plans/net-architecture-migration.md 的 T-LOOP2 行、T-LOOP1 收口段");
            Console.WriteLine("实机锚点：2026-09-26 server.log 的 ACTIVE-ADD id=0 → ACTIVE-ADD id=2 → 断线后同账号仍被拒");
            Console.WriteLine();

            Section("A. 登录身份写入（真实 UserData + 真实 UserStore）", TestLoginIdentity);
            Section("B. uid=0 残留端到端负例（登录→查信息→断线→同账号重登）", TestUidZeroResidue);
            Section("C. 同一连接 UID 变化后的别名清理", TestAliasCleanup);
            Section("D. 删除按引用一致（旧 Close 不得误删新连接）", TestReferenceConsistentRemoval);
            Section("E. 真正重复登录：不顶号、不踢人；无效身份拒绝登记", TestDuplicateOccupancy);
            Section("F. 查信息请求不能绕过已认证账号身份", TestFindInfoIdentityGate);

            Console.WriteLine("合计 " + _checks + " 项检查：通过 " + (_checks - _failures) + "，失败 " + _failures + "。");
            Console.WriteLine("小节：" + _sectionsPassed + " 通过 / " + _sectionsFailed + " 失败。");

            if (_failures > 0)
            {
                Console.WriteLine();
                Console.WriteLine("失败明细：");
                for (int i = 0; i < _failureLog.Count; i++)
                {
                    Console.WriteLine("  - " + _failureLog[i]);
                }
            }

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "结论：T-LOOP2 代码侧通过（UID0 残留与旧 Close 误删新连接两个负例已锁死）。"
                : "结论：T-LOOP2 未通过（存在残留/误删/身份写入缺陷）。");
            Console.WriteLine("本门禁不覆盖：真实 TCP、真实 DS 入局、原局续玩（T-LOOP4/5/6）。");
            Console.WriteLine();
            return _failures == 0 ? 0 : 1;
        }

        // ────────────────────────────────────────────────────────────────
        // A. 登录身份写入
        // ────────────────────────────────────────────────────────────────

        private static void TestLoginIdentity()
        {
            const string account = "tloop2_identity";

            Client fresh = new Client();
            MainPack login = MakeLoginPack(account, "pw-A");
            bool ok = fresh.GetUserData.Login(login);

            Check(ok, "A1 账号不存在时 Login 自动建号并返回成功");
            Check(string.Equals(fresh.UserName, account, StringComparison.Ordinal),
                "A2 Login 后本连接 UserName 等于账号名（既有行为回归）");
            Check(fresh.UID > 0,
                "A3 Login 返回成功前已写入真实 UID（实际 UID=" + fresh.UID + "）");

            UserSnapshot snapshot;
            bool found = UserStore.TryGetByName(account, out snapshot);
            Check(found && snapshot.Id > 0 && snapshot.Id == fresh.UID,
                "A4 连接 UID 与 UserStore 权威快照一致（快照=" + (found ? snapshot.Id.ToString() : "无") + "，连接=" + fresh.UID + "）");
            Check(!string.IsNullOrEmpty(fresh.PlayerName),
                "A5 Login 后 PlayerName 非空（实际='" + (fresh.PlayerName ?? "null") + "'）");
            Check(found && string.Equals(fresh.PlayerName, snapshot.Name, StringComparison.Ordinal),
                "A6 连接 PlayerName 与 UserStore 昵称一致");

            // 回归：已有账号 + 错误密码 → 仍失败，且不得写任何身份
            Client wrong = new Client();
            bool wrongOk = wrong.GetUserData.Login(MakeLoginPack(account, "pw-WRONG"));
            Check(!wrongOk, "A7 已有账号密码错误仍登录失败（密码校验未被削弱）");
            Check(wrong.UID == 0 && wrong.UserName == null && wrong.PlayerName == null,
                "A8 失败登录不写入任何身份（UID 仍 0、账号名/昵称仍空）");

            // 回归：已有账号 + 正确密码 → 成功且身份与建号快照一致
            Client again = new Client();
            bool againOk = again.GetUserData.Login(MakeLoginPack(account, "pw-A"));
            Check(againOk, "A9 已有账号正确密码登录成功");
            Check(found && again.UID == snapshot.Id && string.Equals(again.PlayerName, snapshot.Name, StringComparison.Ordinal),
                "A10 再次登录得到的 UID / PlayerName 与首次一致");

            // 回归：空账号名明确失败（不产生空身份）
            Client blankName = new Client();
            bool blankOk = blankName.GetUserData.Login(MakeLoginPack("", "pw"));
            Check(!blankOk && blankName.UID == 0,
                "A11 空账号名登录明确失败且不产生身份");

            // 回归：FindPlayerInfo 仍能回填身份（Login 已写入后为幂等）
            MainPack info = MakeLoginPack(account, "pw-A");
            bool infoOk = fresh.GetUserData.FindPlayerInfo(ref info, fresh);
            Check(infoOk && info.UserInfopack != null && info.UserInfopack.Id == fresh.UID,
                "A12 FindPlayerInfo 仍回填一致身份（与 Login 写入的 UID 相同）");
        }

        private static void TestFindInfoIdentityGate()
        {
            const string account = "tloop2_find_owner";
            const string other = "tloop2_find_other";
            Client owner = new Client();
            Check(owner.GetUserData.Login(MakeLoginPack(account, "owner-pass")), "F1 先创建目标账号");
            Client fresh = new Client();
            MainPack unauthenticated = MakeLoginPack(account, "anything");
            Check(!fresh.GetUserData.FindPlayerInfo(ref unauthenticated, fresh),
                "F2 未登录连接不能自报目标账号取得身份");
            Check(fresh.UID == 0 && string.IsNullOrEmpty(fresh.UserName),
                "F3 未认证查信息不改变连接身份");

            Client logged = new Client();
            Check(logged.GetUserData.Login(MakeLoginPack(other, "other-pass")), "F4 第二账号登录成功");
            int originalUid = logged.UID;
            MainPack crossAccount = MakeLoginPack(account, "anything");
            Check(!logged.GetUserData.FindPlayerInfo(ref crossAccount, logged),
                "F5 已登录连接不能请求别的账号查信息后改绑UID");
            Check(logged.UID == originalUid && string.Equals(logged.UserName, other, StringComparison.Ordinal),
                "F6 被拒的跨账号请求不改变原本认证身份");
        }

        // ────────────────────────────────────────────────────────────────
        // B. uid=0 残留端到端负例
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 逐字复现实机流程：
        ///   UserController.Login        → UserData.Login 成功后 RegisterActiveClient
        ///   客户端登录后必发 FindPlayerInfo → UserController.FindPlayerInfo 里幂等再登记
        ///   断线                        → Client.Close → RemoveActiveClient
        /// 断言「断线后索引必须为空、同账号必须能重新登记」。
        /// </summary>
        private static void TestUidZeroResidue()
        {
            const string account = "tloop2_residue";
            PMActiveClientIndex<Client> index = new PMActiveClientIndex<Client>();

            Client first = new Client();
            MainPack login = MakeLoginPack(account, "pw");
            Check(first.GetUserData.Login(login), "B1 首次登录成功");
            index.Register(first);
            Console.WriteLine("      登记①后 键=[" + JoinUids(index) + "] UID=" + first.UID);

            MainPack info = MakeLoginPack(account, "pw");
            Check(first.GetUserData.FindPlayerInfo(ref info, first), "B2 FindPlayerInfo 成功回填身份");
            index.Register(first);
            Console.WriteLine("      登记②后 键=[" + JoinUids(index) + "] UID=" + first.UID);

            Check(index.Count == 1,
                "B3 登录 + FindPlayerInfo 后索引只有 1 个键（实际 " + index.Count + "，键=[" + JoinUids(index) + "]）");
            Check(!HasUid(index, 0),
                "B4 索引内不存在 uid=0 别名（键=[" + JoinUids(index) + "]）");

            index.Remove(first);
            Console.WriteLine("      断线删除后 键=[" + JoinUids(index) + "]");
            Check(index.Count == 0,
                "B5 断线后活跃索引为空（实际 " + index.Count + "，键=[" + JoinUids(index) + "]）");
            Check(index.FindByUserName(account) == null,
                "B6 断线后重复登录门（GetActiveClientByUserName）取不到已断开的旧连接");

            // 同一账号的第二条连接：必须能通过重复登录门并重新登记
            Client second = new Client();
            Check(second.GetUserData.Login(MakeLoginPack(account, "pw")), "B7 旧连接断开后同账号可再次 Login 成功");
            Check(index.FindByUserName(account) == null,
                "B8 新连接登记前重复登录门为空（否则真实服务器会拒绝这次登录）");

            PMActiveRegistration registered = index.Register(second);
            Check(registered == PMActiveRegistration.Registered
                    && ReferenceEquals(index.FindByUserName(account), second),
                "B9 新连接登记成功并成为该账号的活跃连接（结果=" + registered + "）");
            Check(!HasUid(index, 0) && index.Count == 1,
                "B10 新连接登记后仍无 uid=0 别名且只有一个键（键=[" + JoinUids(index) + "]）");

            index.Remove(second);
            Check(index.Count == 0, "B11 再次断线后索引为空（可循环重登）");
        }

        // ────────────────────────────────────────────────────────────────
        // C. UID 别名清理
        // ────────────────────────────────────────────────────────────────

        private static void TestAliasCleanup()
        {
            const string account = "tloop2_alias";
            PMActiveClientIndex<Client> index = new PMActiveClientIndex<Client>();

            Client client = new Client();
            Check(client.GetUserData.Login(MakeLoginPack(account, "pw")), "C1 登录成功");

            // 别名窗口：登记时 UID 可能还是 0，随后 FindPlayerInfo 才回填真实 UID
            index.Register(client);
            MainPack info = MakeLoginPack(account, "pw");
            client.GetUserData.FindPlayerInfo(ref info, client);
            index.Register(client);

            Check(index.Count == 1,
                "C2 同一连接只保留一个键（实际 " + index.Count + "，键=[" + JoinUids(index) + "]）");
            Check(!HasUid(index, 0), "C3 索引内不存在键 0（键=[" + JoinUids(index) + "]）");

            Client same;
            Check(client.UID > 0 && index.TryGet(client.UID, out same) && ReferenceEquals(same, client),
                "C4 索引内该连接的当前 UID 键指向同一连接（UID=" + client.UID + "）");

            // 幂等：重复登记同一连接不产生新键
            index.Register(client);
            Check(index.Count == 1, "C5 同一连接重复登记不新增键");

            Check(index.Remove(client) && index.Count == 0,
                "C6 删除该连接后索引为空（别名与当前键一起清掉）");
        }

        // ────────────────────────────────────────────────────────────────
        // D. 删除按引用一致
        // ────────────────────────────────────────────────────────────────

        private static void TestReferenceConsistentRemoval()
        {
            const string account = "tloop2_close";
            PMActiveClientIndex<Client> index = new PMActiveClientIndex<Client>();

            Client oldClient = new Client();
            Client newClient = new Client();

            Check(oldClient.GetUserData.Login(MakeLoginPack(account, "pw")), "D1 旧连接登录成功");
            Check(newClient.GetUserData.Login(MakeLoginPack(account, "pw")), "D2 同账号新连接登录成功");
            Check(oldClient.UID > 0 && oldClient.UID == newClient.UID,
                "D3 两条连接解析到同一权威 UID（旧=" + oldClient.UID + "，新=" + newClient.UID + "）");

            Check(index.Register(oldClient) == PMActiveRegistration.Registered, "D4 旧连接登记成功");
            Check(index.Remove(oldClient), "D5 旧连接正常断开时确实移除了键");
            Check(index.Count == 0, "D6 旧连接移除后索引为空");

            Check(index.Register(newClient) == PMActiveRegistration.Registered, "D7 新连接登记成功");

            // 关键负例：旧连接的 Close 迟到（例如接收线程最后一条异常路径），
            // 此时 uid 已被新连接占用 —— 按引用删除必须一个键都不删。
            bool lateRemoved = index.Remove(oldClient);
            Check(!lateRemoved, "D8 旧连接的迟到 Close 未删除任何键（关键负例）");

            Client current;
            Check(index.TryGet(newClient.UID, out current) && ReferenceEquals(current, newClient),
                "D9 新连接仍在活跃索引内");
            Check(ReferenceEquals(index.FindByUserName(account), newClient),
                "D10 重复登录门仍指向新连接（新连接未被误删，同账号不会被误判为可重复登录）");
            Check(index.Count == 1, "D11 索引键数仍为 1（实际 " + index.Count + "，键=[" + JoinUids(index) + "]）");

            // 反向确认：只有新连接自己断开才会移除
            Check(index.Remove(newClient) && index.Count == 0,
                "D12 新连接自身断开时正常移除");
        }

        // ────────────────────────────────────────────────────────────────
        // E. 真正重复登录 / 无效身份
        // ────────────────────────────────────────────────────────────────

        private static void TestDuplicateOccupancy()
        {
            const string account = "tloop2_dup";
            PMActiveClientIndex<Client> index = new PMActiveClientIndex<Client>();

            Client online = new Client();
            Client intruder = new Client();
            Check(online.GetUserData.Login(MakeLoginPack(account, "pw")), "E1 在线连接 A 登录成功");
            Check(intruder.GetUserData.Login(MakeLoginPack(account, "pw")), "E2 同账号第二条连接 B 登录成功");
            Check(online.UID > 0 && online.UID == intruder.UID, "E3 A / B 解析到同一 UID=" + online.UID);

            Check(index.Register(online) == PMActiveRegistration.Registered, "E4 A 登记成功");

            PMActiveRegistration second = index.Register(intruder);
            Check(second == PMActiveRegistration.Conflict,
                "E5 同 uid 的第二条连接登记被拒（不顶号，实际=" + second + "）");

            Client current;
            Check(index.TryGet(online.UID, out current) && ReferenceEquals(current, online),
                "E6 活跃索引仍指向原连接 A（在线另一端未被顶替）");
            Check(index.Count == 1, "E7 冲突登记不增加键（实际 " + index.Count + "）");
            Check(ReferenceEquals(index.FindByUserName(account), online),
                "E8 重复登录门仍可见原活跃连接（真正在线时同账号仍被拒绝登录）");

            Check(index.Register(online) == PMActiveRegistration.Registered && index.Count == 1,
                "E9 同一连接重复登记幂等（Registered 且不新增键）");

            // 无效身份：UID<=0 或账号名为空一律拒绝，且不留任何键
            Client neverLoggedIn = new Client();
            PMActiveRegistration invalid = index.Register(neverLoggedIn);
            Check(invalid == PMActiveRegistration.Rejected,
                "E10 未登录连接（UID=0 / 账号名为空）拒绝登记（实际=" + invalid + "）");
            Check(index.Count == 1 && !HasUid(index, 0),
                "E11 拒绝登记不建立任何键（键=[" + JoinUids(index) + "]）");

            PMActiveClientIndex<FakeEntry> probes = new PMActiveClientIndex<FakeEntry>();
            Check(probes.Register(null) == PMActiveRegistration.Rejected, "E12 null 条目拒绝登记");
            Check(probes.Register(new FakeEntry { UID = 0, UserName = "someone" }) == PMActiveRegistration.Rejected,
                "E13 UID=0 拒绝登记");
            Check(probes.Register(new FakeEntry { UID = -1, UserName = "someone" }) == PMActiveRegistration.Rejected,
                "E14 负 UID 拒绝登记");
            Check(probes.Register(new FakeEntry { UID = 7, UserName = string.Empty }) == PMActiveRegistration.Rejected,
                "E15 UID>0 但账号名为空拒绝登记");
            Check(probes.Register(new FakeEntry { UID = 7, UserName = null }) == PMActiveRegistration.Rejected,
                "E16 UID>0 但账号名为 null 拒绝登记");
            Check(probes.Count == 0, "E17 无效登记未留下任何键（实际 " + probes.Count + "）");

            // 合法登记仍然可用
            FakeEntry valid = new FakeEntry { UID = 11, UserName = "valid" };
            Check(probes.Register(valid) == PMActiveRegistration.Registered, "E18 合法身份登记成功");
            FakeEntry found;
            Check(probes.TryGet(11, out found) && ReferenceEquals(found, valid), "E19 按 uid 可取回该条目");
            Check(ReferenceEquals(probes.FindByUserName("valid"), valid), "E20 按账号名可取回该条目");
            Check(probes.FindByUserName(null) == null, "E21 null 账号名查找返回 null");
            Check(probes.Remove(valid) && probes.Count == 0, "E22 正常删除后索引为空");
        }

        // ────────────────────────────────────────────────────────────────
        // 门禁骨架
        // ────────────────────────────────────────────────────────────────

        private static void Section(string title, Action body)
        {
            Console.WriteLine("── " + title);
            int before = _failures;

            try
            {
                body();
            }
            catch (Exception ex)
            {
                _failures++;
                _failureLog.Add(title + " 抛出未处理异常：" + ex);
                Console.WriteLine("  FAIL  抛出未处理异常：" + ex.GetType().Name + ": " + ex.Message);
            }

            if (before == _failures)
            {
                _sectionsPassed++;
                Console.WriteLine("   [SECTION-OK]");
            }
            else
            {
                _sectionsFailed++;
                Console.WriteLine("   [SECTION-FAILED] 本节失败 " + (_failures - before) + " 项");
            }

            Console.WriteLine();
        }

        private static void Check(bool condition, string description)
        {
            _checks++;
            if (condition)
            {
                Console.WriteLine("  PASS  " + description);
                return;
            }

            _failures++;
            _failureLog.Add(description);
            Console.WriteLine("  FAIL  " + description);
        }

        private static MainPack MakeLoginPack(string username, string password)
        {
            MainPack pack = new MainPack();
            pack.Requestcode = RequestCode.User;
            pack.Actioncode = ActionCode.Login;

            LoginPack login = new LoginPack();
            login.Username = username;
            login.Password = password;
            pack.Loginpack = login;
            return pack;
        }

        private static string JoinUids<T>(PMActiveClientIndex<T> index) where T : class, IPMActiveClientEntry
        {
            int[] uids = index.GetUids();
            if (uids.Length == 0)
            {
                return "空";
            }

            Array.Sort(uids);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < uids.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append(uids[i]);
            }

            return sb.ToString();
        }

        private static bool HasUid<T>(PMActiveClientIndex<T> index, int uid) where T : class, IPMActiveClientEntry
        {
            T entry;
            return index.TryGet(uid, out entry);
        }

        /// <summary>只用于「UID 合法但账号名非法」这类边界组合的最小条目替身。</summary>
        private sealed class FakeEntry : IPMActiveClientEntry
        {
            public int UID { get; set; }

            public string UserName { get; set; }
        }
    }
}
