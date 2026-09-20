using System;
using System.Collections.Generic;

namespace Server.DAO
{
    /// <summary>
    /// 用户快照：从 <see cref="UserStore"/> 取出的只读副本。
    ///
    /// 为什么不直接返回内部记录：内部记录是可变对象且受锁保护，
    /// 一旦泄漏给调用方，就会出现「锁外读可变状态」的竞态。
    /// 复制成结构体（值语义）可以彻底断掉这条路径。
    /// </summary>
    public struct UserSnapshot
    {
        /// <summary>用户 ID（相当于原 MySQL 的 users.id 自增主键）。</summary>
        public int Id;

        /// <summary>账号（相当于 users.UserName）。</summary>
        public string UserName;

        /// <summary>昵称（相当于 users.name）。</summary>
        public string Name;
    }

    /// <summary>
    /// 进程内用户库（替代原 MySQL 的 users / friends 两张表）。
    ///
    /// 为什么改成内存：
    ///   本工程已不需要账号持久化（决策见迁移计划）。原实现要求本机装 MySQL 才能登录，
    ///   而构建/联调环境并没有数据库，导致「服务器起不来 → 客户端连不上」。
    ///   内存实现在行为上与原 SQL 版逐条对齐（见各方法的注释），但零外部依赖。
    ///
    /// 线程安全：服务端每个 Client 一个接收线程，本类被并发访问，因此所有公开方法内部加锁。
    /// 向外只返回 <see cref="UserSnapshot"/> 值副本，不泄漏内部可变对象。
    ///
    /// 数据模型（与 hyld.sql 一致）：
    ///   users   : UserName(PK) / Password / name / id
    ///   friends : (UserID, FriendID) —— 原库以**单向边**存储，
    ///             查询时用「(f.UserID=me AND f.FriendID=u.id) OR (f.FriendID=me AND u.id=f.UserID)」
    ///             做双向匹配。本实现保持同样的语义（存单向、查双向），以便行为等价。
    ///
    /// 注意：进程重启后数据全部丢失。这是内存实现的定义，不是缺陷。
    /// </summary>
    internal static class UserStore
    {
        private sealed class UserRecord
        {
            public int Id;
            public string UserName;
            public string Password;
            public string Name;
        }

        private static readonly object _lock = new object();

        // 账号大小写敏感：与原 MySQL 的 VARCHAR 默认排序规则（utf8_general_ci 之外，
        // 原库建表为 DEFAULT CHARSET=utf8，未指定 _ci），这里取「区分大小写」更保守，
        // 且与客户端输入一致，避免「Liuli」和「liuli」被当成两个号却看起来一样。
        private static readonly Dictionary<string, UserRecord> _byName =
            new Dictionary<string, UserRecord>(StringComparer.Ordinal);

        private static readonly Dictionary<int, UserRecord> _byId =
            new Dictionary<int, UserRecord>();

        // 好友边：key = (userId << 32) | friendId。存单向、查双向。
        private static readonly HashSet<long> _friendEdges = new HashSet<long>();

        private static int _nextId = 1;

        /// <summary>把一条好友边编码成 64 位 key。两个 id 都是非负 int，不会溢出。</summary>
        private static long EdgeKey(int userId, int friendId)
        {
            return ((long)userId << 32) | (uint)friendId;
        }

        private static UserSnapshot ToSnapshot(UserRecord r)
        {
            UserSnapshot s;
            s.Id = r.Id;
            s.UserName = r.UserName;
            s.Name = r.Name;
            return s;
        }

        /// <summary>
        /// 注册新账号。
        /// 对应原实现：`INSERT INTO users SET UserName=@u, Password=@p, name=''`，
        /// 主键冲突（MySQL 错误码 1062）时返回 false。
        ///
        /// 有意偏离：原实现把 name 写成空串，本实现写成账号名。
        /// 理由：数据库已移除，没有别的途径产生昵称，而空昵称会让好友相关流程
        /// （AcceptAddFriend 用 PlayerName 找连接）不可用；默认账号名让大厅直接可用。
        /// 客户端「昵称为空则引导改名」的判断（UIStartMainPanel）仍保留，改名流程不受影响。
        /// </summary>
        public static bool TryRegister(string userName, string password, out string error)
        {
            error = null;

            if (string.IsNullOrEmpty(userName))
            {
                error = "账号不能为空";
                return false;
            }

            lock (_lock)
            {
                if (_byName.ContainsKey(userName))
                {
                    error = "该账户已存在";
                    return false;
                }

                UserRecord record = new UserRecord();
                record.Id = _nextId++;
                record.UserName = userName;
                record.Password = password ?? string.Empty;
                record.Name = userName;

                _byName[userName] = record;
                _byId[record.Id] = record;
                return true;
            }
        }

        /// <summary>
        /// 登录：账号存在且密码匹配则成功。
        ///
        /// 有意扩展（按需求）：**账号不存在时自动建号**。
        /// 这是开发/联调环境的便利措施——不需要先走注册流程，任意账号密码都能进。
        /// 行为边界（刻意保留，避免密码形同虚设）：
        ///   - 账号存在但密码不符 → 仍然失败（与原 SQL 版一致）。
        /// 返回 <paramref name="created"/> 告诉调用方本次是否新建了账号，便于日志区分。
        /// </summary>
        public static bool TryLoginOrCreate(string userName, string password, out bool created, out string error)
        {
            created = false;
            error = null;

            if (string.IsNullOrEmpty(userName))
            {
                error = "账号不能为空";
                return false;
            }

            lock (_lock)
            {
                UserRecord record;
                if (_byName.TryGetValue(userName, out record))
                {
                    if (!string.Equals(record.Password, password ?? string.Empty, StringComparison.Ordinal))
                    {
                        error = "密码错误";
                        return false;
                    }

                    return true;
                }

                // 账号不存在：自动建号
                record = new UserRecord();
                record.Id = _nextId++;
                record.UserName = userName;
                record.Password = password ?? string.Empty;
                record.Name = userName;

                _byName[userName] = record;
                _byId[record.Id] = record;
                created = true;
                return true;
            }
        }

        /// <summary>按账号取用户。对应 `SELECT name, id FROM users WHERE UserName = @u`。</summary>
        public static bool TryGetByName(string userName, out UserSnapshot user)
        {
            user = default(UserSnapshot);

            if (string.IsNullOrEmpty(userName))
            {
                return false;
            }

            lock (_lock)
            {
                UserRecord record;
                if (!_byName.TryGetValue(userName, out record))
                {
                    return false;
                }

                user = ToSnapshot(record);
                return true;
            }
        }

        /// <summary>按 ID 取用户。对应 `SELECT UserName FROM users WHERE id = @id`。</summary>
        public static bool TryGetById(int id, out UserSnapshot user)
        {
            user = default(UserSnapshot);

            lock (_lock)
            {
                UserRecord record;
                if (!_byId.TryGetValue(id, out record))
                {
                    return false;
                }

                user = ToSnapshot(record);
                return true;
            }
        }

        /// <summary>
        /// 改名。对应 `UPDATE users SET name = @name WHERE UserName = @u`。
        /// 原实现不检查受影响行数、总是返回 true（除非 SQL 异常），这里保持一致：
        /// 账号不存在时也返回 true，但记录一条警告，避免把「静默什么都没改」藏起来。
        /// </summary>
        public static bool TryRename(string userName, string newName, out string error)
        {
            error = null;

            if (string.IsNullOrEmpty(userName))
            {
                error = "账号不能为空";
                return false;
            }

            lock (_lock)
            {
                UserRecord record;
                if (!_byName.TryGetValue(userName, out record))
                {
                    error = "账号不存在，改名未生效";
                    return false;
                }

                record.Name = newName ?? string.Empty;
                return true;
            }
        }

        /// <summary>
        /// 取某用户的全部好友（双向匹配，与原 SQL 的 OR 条件一致）。
        /// 对应：
        ///   SELECT DISTINCT u.name, u.id FROM users u JOIN friends f
        ///   ON (f.UserID=@me AND f.FriendID=u.id) OR (f.FriendID=@me AND u.id=f.UserID)
        /// </summary>
        public static List<UserSnapshot> GetFriends(int userId)
        {
            List<UserSnapshot> result = new List<UserSnapshot>();

            lock (_lock)
            {
                foreach (long key in _friendEdges)
                {
                    int a = (int)(key >> 32);
                    int b = (int)(uint)key;

                    int otherId;
                    if (a == userId)
                    {
                        otherId = b;
                    }
                    else if (b == userId)
                    {
                        otherId = a;
                    }
                    else
                    {
                        continue;
                    }

                    UserRecord other;
                    if (_byId.TryGetValue(otherId, out other))
                    {
                        result.Add(ToSnapshot(other));
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 添加好友边。对应 `INSERT INTO friends SET UserID=@userid, FriendID=@friendid`。
        /// 重复添加不报错（原库无唯一约束，会插入重复行；这里用集合天然去重，结果更干净）。
        /// </summary>
        public static void AddFriend(int userId, int friendId)
        {
            if (userId == friendId)
            {
                return;
            }

            lock (_lock)
            {
                _friendEdges.Add(EdgeKey(userId, friendId));
            }
        }

        /// <summary>当前账号总数，供启动日志/排查使用。</summary>
        public static int UserCount
        {
            get
            {
                lock (_lock)
                {
                    return _byName.Count;
                }
            }
        }

        /// <summary>当前好友边总数，供启动日志/排查使用。</summary>
        public static int FriendEdgeCount
        {
            get
            {
                lock (_lock)
                {
                    return _friendEdges.Count;
                }
            }
        }
    }
}
