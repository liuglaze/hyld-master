using System;
using System.Collections.Generic;

namespace Server
{
    /// <summary>
    /// 活跃客户索引的条目面：索引只依赖「这条连接是谁」这两个只读事实，
    /// 不依赖 Socket / 协议生成物 / 控制器，因此可以被独立可执行测试直接消费。
    ///
    /// 真实生产实现者只有 <see cref="Client"/>（它本来就公开 UID / UserName）。
    /// </summary>
    internal interface IPMActiveClientEntry
    {
        /// <summary>服务端权威 UID（未完成身份写入时为 0）。</summary>
        int UID { get; }

        /// <summary>账号名（未登录 / 非法身份时可能为空）。</summary>
        string UserName { get; }
    }

    /// <summary>活跃登记的判定结果。</summary>
    internal enum PMActiveRegistration
    {
        /// <summary>身份不合法（UID&lt;=0 或账号名为空），未建立任何键。</summary>
        Rejected = 0,

        /// <summary>登记成功（新建或同一连接的幂等刷新）。</summary>
        Registered = 1,

        /// <summary>该 UID 已被**另一条**活跃连接占用：不顶号、不踢人。</summary>
        Conflict = 2,
    }

    /// <summary>
    /// 活跃客户索引（uid -&gt; 连接）。
    ///
    /// 为什么把它从 <see cref="Server"/> 里抽出来：
    ///   `T-LOOP2` 要求「uid=0 残留」与「旧连接迟到 Close 误删新连接」两个缺陷必须有
    ///   **可执行负例**，而负例不能写成对源码的字符串搜索。把登记/删除/查找的判定集中到
    ///   这个零依赖类之后，测试可以直接驱动生产实现本身（Server 只剩日志与委派）。
    ///
    /// 三条不变量（对应改造前 `Server._activeClient` 字典的三个真实缺陷）：
    ///   1. **只收合法身份**：UID&lt;=0 或账号名为空一律 <see cref="PMActiveRegistration.Rejected"/>。
    ///      这类条目一旦进表，断线时按「可变 UID」删除就会漏键，留下 uid=0 别名，
    ///      使同账号在旧连接断开后仍被「重复登录」拒绝
    ///      （2026-09-26 server.log：`ACTIVE-ADD id=0` → `ACTIVE-ADD id=2` → 断线只删键 2）。
    ///   2. **一连接一键**：登记时清掉同一连接的旧键（UID 变化产生的别名），
    ///      不让同一条连接以两个身份同时留在表里。
    ///   3. **删除按引用一致**：<see cref="Remove"/> 删掉**所有**以该连接为值的键，
    ///      而不是按 <c>entry.UID</c> 删一个键 —— 否则旧连接的迟到 Close
    ///      会在该 uid 已被同账号新连接占用时把新连接删掉。
    ///
    /// 冲突语义：同一 uid 已属于**另一条**连接时返回 <see cref="PMActiveRegistration.Conflict"/>
    /// 并保持原连接（既不顶号也不踢人）。真正在线的同账号重复登录仍由
    /// `UserController.Login` 的活跃账号门拒绝，本类不承担该策略，只保证表不被顶替。
    ///
    /// 线程安全：内部自带锁，调用方无需（也不得）再持 <see cref="Server"/> 的列表锁。
    /// </summary>
    internal sealed class PMActiveClientIndex<TEntry> where TEntry : class, IPMActiveClientEntry
    {
        private readonly object _gate = new object();
        private readonly Dictionary<int, TEntry> _byUid = new Dictionary<int, TEntry>();

        /// <summary>当前索引内的键数（诊断 / 测试断言用）。</summary>
        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _byUid.Count;
                }
            }
        }

        /// <summary>
        /// 登记一条活跃连接（幂等）。
        /// 判定顺序：身份校验 → 同 uid 冲突（保持原连接）→ 清同一连接旧键 → 落键。
        /// </summary>
        public PMActiveRegistration Register(TEntry entry)
        {
            if (entry == null)
            {
                return PMActiveRegistration.Rejected;
            }

            int uid = entry.UID;
            string userName = entry.UserName;

            // 身份不合法：拒绝登记，且**不建立任何键**（这是 uid=0 残留的根因门）。
            if (uid <= 0 || string.IsNullOrEmpty(userName))
            {
                return PMActiveRegistration.Rejected;
            }

            lock (_gate)
            {
                TEntry existing;
                if (_byUid.TryGetValue(uid, out existing) && !ReferenceEquals(existing, entry))
                {
                    // 真正重复：保持已有活跃连接，不顶号、不踢人；也不为新连接建立别名。
                    return PMActiveRegistration.Conflict;
                }

                // 同一连接的旧键（UID 变化产生的别名）与本次落键在同一把锁里完成。
                RemoveAliasesLocked(entry, uid);
                _byUid[uid] = entry;
                return PMActiveRegistration.Registered;
            }
        }

        /// <summary>
        /// 注销一条活跃连接：按**引用一致**删掉所有以它为值的键。
        /// 返回值表示本次是否真的删掉了键（false = 该连接早已不在表里，迟到 Close 的安全情形）。
        /// </summary>
        public bool Remove(TEntry entry)
        {
            if (entry == null)
            {
                return false;
            }

            lock (_gate)
            {
                List<int> owned = null;
                foreach (KeyValuePair<int, TEntry> pair in _byUid)
                {
                    if (ReferenceEquals(pair.Value, entry))
                    {
                        if (owned == null)
                        {
                            owned = new List<int>();
                        }

                        owned.Add(pair.Key);
                    }
                }

                if (owned == null)
                {
                    return false;
                }

                for (int i = 0; i < owned.Count; i++)
                {
                    _byUid.Remove(owned[i]);
                }

                return true;
            }
        }

        /// <summary>按 uid 取连接。</summary>
        public bool TryGet(int uid, out TEntry entry)
        {
            lock (_gate)
            {
                return _byUid.TryGetValue(uid, out entry);
            }
        }

        /// <summary>按账号名取连接（Login 的重复登录门用它）。</summary>
        public TEntry FindByUserName(string userName)
        {
            // 空账号名不是任何合法连接的账号名，直接判定为「不在线」，
            // 避免拿空串去匹配到非法历史条目。
            if (string.IsNullOrEmpty(userName))
            {
                return null;
            }

            lock (_gate)
            {
                foreach (KeyValuePair<int, TEntry> pair in _byUid)
                {
                    if (pair.Value != null && string.Equals(pair.Value.UserName, userName, StringComparison.Ordinal))
                    {
                        return pair.Value;
                    }
                }
            }

            return null;
        }

        /// <summary>当前索引内的 uid 快照。调用方拿到的是副本，不回写索引。</summary>
        public int[] GetUids()
        {
            lock (_gate)
            {
                int[] uids = new int[_byUid.Count];
                _byUid.Keys.CopyTo(uids, 0);
                return uids;
            }
        }

        /// <summary>清掉该连接除 <paramref name="keepUid"/> 以外的全部键（须在锁内调用）。</summary>
        private void RemoveAliasesLocked(TEntry entry, int keepUid)
        {
            List<int> aliases = null;
            foreach (KeyValuePair<int, TEntry> pair in _byUid)
            {
                if (pair.Key != keepUid && ReferenceEquals(pair.Value, entry))
                {
                    if (aliases == null)
                    {
                        aliases = new List<int>();
                    }

                    aliases.Add(pair.Key);
                }
            }

            if (aliases == null)
            {
                return;
            }

            for (int i = 0; i < aliases.Count; i++)
            {
                _byUid.Remove(aliases[i]);
            }
        }
    }
}
