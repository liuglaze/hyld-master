using System;
using System.Collections.Generic;

namespace PMNet.Control
{
    /// <summary>
    /// Lobby 级**跨会话**的玩家占用账本（R3-B，契约 §3「终态释放玩家与端口占用」+ §7.3）。
    ///
    /// ## 为什么必须存在
    /// R3-A1 的 <see cref="PMDsCoordinator"/> 只做**本局内**的名册唯一性校验
    /// （<c>PMDsControlCodec.ValidateIdentities</c>），因此同一个 uid 可以同时出现在两个会话的名册里。
    /// 端口资源有 Lobby 级共享的 <see cref="PMDsPortPool"/> 兜住，玩家资源却没有对应的守卫 ——
    /// 结果就是「同一账号被两局同时占用」。本类补上这一半：
    /// **一个 uid 在任意时刻最多只能属于一个未终结的对局**。
    ///
    /// ## 与端口池对称的释放语义
    /// 占用由 <see cref="PMDsCoordinator.Allocate"/> 在拿到端口之前预留（失败即回滚），
    /// 由 <see cref="PMDsCoordinator"/> 的 <c>ReleaseResources</c> 在**确认进程已退出**之后释放 ——
    /// 与端口同点，不存在「进程还活着但 uid 已经放出去」的中间态。
    ///
    /// ## 线程安全
    /// 宿主（<see cref="PMDsLobbyHost"/>）是单线程所有者，但匹配回调线程会在入队前做
    /// 「uid 是否已被占用」的快速预检，因此本类内部用一把锁保护两张表，不假设单线程调用。
    ///
    /// ## 刻意不做
    /// - 不做持久化：与端口池、结果墓碑一样，Lobby 重启即丢（当前无数据库，契约 §3 已声明）；
    /// - 不校验 HeroId / TeamId 之类业务字段（那是名册校验的事），只占 uid。
    /// </summary>
    public sealed class PMDsPlayerLedger : IPMDsPlayerLedger
    {
        private readonly object _gate = new object();

        /// <summary>uid -> matchId（占用者）。</summary>
        private readonly Dictionary<int, string> _uidToMatch = new Dictionary<int, string>();

        /// <summary>matchId -> 该局占用的全部 uid（用于按局整体释放）。</summary>
        private readonly Dictionary<string, int[]> _matchToUids =
            new Dictionary<string, int[]>(StringComparer.Ordinal);

        /// <summary>当前被占用的 uid 总数。</summary>
        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _uidToMatch.Count;
                }
            }
        }

        /// <summary>当前持有占用的对局数。</summary>
        public int MatchCount
        {
            get
            {
                lock (_gate)
                {
                    return _matchToUids.Count;
                }
            }
        }

        /// <summary>该 uid 是否已被某个对局占用。</summary>
        public bool IsOccupied(int uid)
        {
            lock (_gate)
            {
                return _uidToMatch.ContainsKey(uid);
            }
        }

        /// <summary>取占用该 uid 的对局 ID。</summary>
        public bool TryGetMatchId(int uid, out string matchId)
        {
            lock (_gate)
            {
                return _uidToMatch.TryGetValue(uid, out matchId);
            }
        }

        /// <summary>
        /// 为一局预留整个名册的 uid 占用。**要么全部成功，要么全部不动**（原子）。
        ///
        /// 拒绝条件（都会给出不含秘密的原因）：
        /// - matchId 为空 / 名册为空 / 名册超过 6 人；
        /// - 名册内有 Uid &lt;= 0 或 Uid 重复；
        /// - 任一 uid 已被**其它**对局占用；
        /// - 该 matchId 已经有占用记录（重复分配）。
        /// </summary>
        public bool TryReserve(string matchId, PMDsRosterIdentity[] roster, out string reason)
        {
            reason = null;

            if (string.IsNullOrEmpty(matchId))
            {
                reason = "matchId 不得为空";
                return false;
            }

            if (roster == null || roster.Length == 0)
            {
                reason = "名册不得为空";
                return false;
            }

            if (roster.Length > PMDsControlWire.MaxRosterPlayers)
            {
                reason = "名册超过上限：" + roster.Length + " > " + PMDsControlWire.MaxRosterPlayers;
                return false;
            }

            lock (_gate)
            {
                if (_matchToUids.ContainsKey(matchId))
                {
                    reason = "该对局已预留过玩家占用：" + matchId;
                    return false;
                }

                for (int i = 0; i < roster.Length; i++)
                {
                    int uid = roster[i].Uid;
                    if (uid <= 0)
                    {
                        reason = "名册第 " + i + " 项 uid 非法：" + uid;
                        return false;
                    }

                    string owner;
                    if (_uidToMatch.TryGetValue(uid, out owner))
                    {
                        reason = "uid " + uid + " 已被对局占用：" + owner;
                        return false;
                    }

                    for (int j = 0; j < i; j++)
                    {
                        if (roster[j].Uid == uid)
                        {
                            reason = "名册内 uid 重复：" + uid;
                            return false;
                        }
                    }
                }

                int[] uids = new int[roster.Length];
                for (int i = 0; i < roster.Length; i++)
                {
                    uids[i] = roster[i].Uid;
                    _uidToMatch[uids[i]] = matchId;
                }

                _matchToUids[matchId] = uids;
                return true;
            }
        }

        /// <summary>
        /// 释放某个对局的全部 uid 占用。返回之前是否真的持有占用（幂等）。
        /// **与端口归还同点调用**：只有确认 DS 进程已退出才会走到这里。
        /// </summary>
        public bool Release(string matchId)
        {
            if (string.IsNullOrEmpty(matchId))
            {
                return false;
            }

            lock (_gate)
            {
                int[] uids;
                if (!_matchToUids.TryGetValue(matchId, out uids))
                {
                    return false;
                }

                _matchToUids.Remove(matchId);
                for (int i = 0; i < uids.Length; i++)
                {
                    string owner;
                    // 只移除**仍然指向本局**的条目：万一未来出现同 uid 的重新预留，不会误删新占用。
                    if (_uidToMatch.TryGetValue(uids[i], out owner)
                        && string.Equals(owner, matchId, StringComparison.Ordinal))
                    {
                        _uidToMatch.Remove(uids[i]);
                    }
                }

                return true;
            }
        }

        /// <summary>清空全部占用（仅供测试/宿主显式换池时使用）。</summary>
        public void ReleaseAll()
        {
            lock (_gate)
            {
                _uidToMatch.Clear();
                _matchToUids.Clear();
            }
        }
    }
}
