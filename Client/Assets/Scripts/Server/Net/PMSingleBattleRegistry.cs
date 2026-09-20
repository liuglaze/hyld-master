using System;
using System.Collections.Generic;

namespace PMNet.Server
{
    /// <summary>
    /// DS 单局形态下的战斗注册表（P3'-1）。
    ///
    /// <para>
    /// 旧服务端的对应物是 <c>Server/Server/BattleManage.cs</c>（单例、维护「玩家 uid ↔ 战斗 ↔ 战斗内编号」的全局映射）。
    /// DS 是**一局一进程**（D6），所以这里退化为「一场战斗」：<see cref="BattleId"/> 由宿主注入，
    /// 所有已知玩家都指向它。这不是功能删减，而是把全局映射表还原成它在本场景下的真实尺寸。
    /// </para>
    ///
    /// <para>
    /// **玩家编号的来源**：在旧架构里，玩家编号由局外服务器在匹配时分配并写进 <c>BattleManage</c>。
    /// 新架构里这个信息属于「大厅 → DS 的分配」（P4'）。在 P4' 落地之前，
    /// <see cref="TryGetBattlePlayerId"/> 会以 <c>0</c>（未分配）成功返回，
    /// 让路由层跳过编号一致性校验并打印一次性提示 —— 这样 DS 现在就能跑通链路，
    /// 而不会伪造一个假的编号让校验「看起来通过」。
    /// </para>
    ///
    /// <para>
    /// 线程安全：路由层会在主线程调用本类的查询，而 <see cref="RegisterPlayer"/> 可能来自
    /// 主线程的战斗初始化。全部走锁保护。
    /// </para>
    /// </summary>
    public sealed class PMSingleBattleRegistry : PMUdpRouter.IBattleRegistry
    {
        /// <summary>未分配玩家编号的哨兵值。路由层见到 0 会跳过一致性校验。</summary>
        public const int UnassignedPlayerId = 0;

        private readonly object _lock = new object();

        private readonly Dictionary<int, int> _playerIds = new Dictionary<int, int>();

        private int _battleId = 1;

        /// <summary>
        /// 本进程承载的战斗编号。默认 1（单局进程内只需一个稳定路由键）。
        /// P4' 接入大厅后应由大厅下发的匹配信息填入，以便与客户端持有的编号一致。
        /// </summary>
        public int BattleId
        {
            get
            {
                lock (_lock)
                {
                    return _battleId;
                }
            }

            set
            {
                if (value <= 0)
                {
                    return;
                }

                lock (_lock)
                {
                    _battleId = value;
                }
            }
        }

        /// <summary>
        /// 是否允许「未登记玩家」通过建链。P4' 之前为 <c>true</c>（DS 尚无玩家名册），
        /// P4' 接入大厅分配后应置为 <c>false</c>，届时未登记玩家会被路由层拒绝。
        /// </summary>
        public bool AllowUnassignedPlayers = true;

        /// <summary>登记一名玩家的战斗内编号（P4' 接入大厅分配后调用）。</summary>
        public void RegisterPlayer(int uid, int battlePlayerId)
        {
            if (uid <= 0 || battlePlayerId <= 0)
            {
                return;
            }

            lock (_lock)
            {
                _playerIds[uid] = battlePlayerId;
            }
        }

        /// <summary>清空玩家名册（一场战斗结束、或重新分配时调用）。</summary>
        public void ClearPlayers()
        {
            lock (_lock)
            {
                _playerIds.Clear();
            }
        }

        /// <summary>已登记的玩家数（诊断用）。</summary>
        public int RegisteredPlayerCount
        {
            get
            {
                lock (_lock)
                {
                    return _playerIds.Count;
                }
            }
        }

        /// <summary>
        /// 由 uid 反查战斗。DS 只有一场战斗：只要 uid 为正即返回本进程的 <see cref="BattleId"/>。
        ///
        /// <para>
        /// **已知缺口（有意保留，不在 P3'-1 修）**：这里不校验 uid 的真实性，
        /// 也就是说任意 uid 都能建链。旧实现同样如此（信任锚是客户端自报的 uid，见 S6-D12）。
        /// 真正的修法是让大厅下发一份**带凭据的玩家名册**，由 DS 校验后再登记 —— 属于 P4' 的范围。
        /// 在那之前，这里保持与旧实现一致的行为，不引入「看起来更安全但可能拒绝合法重连」的半吊子校验。
        /// </para>
        /// </summary>
        public bool TryGetBattleIdByUid(int uid, out int battleId)
        {
            if (uid <= 0)
            {
                battleId = 0;
                return false;
            }

            battleId = BattleId;
            return true;
        }

        /// <summary>
        /// 由 uid 反查战斗内编号。已登记则返回真实编号；未登记则依 <see cref="AllowUnassignedPlayers"/>
        /// 返回 <c>0</c>（未分配，路由层会跳过一致性校验）或直接失败。
        /// </summary>
        public bool TryGetBattlePlayerId(int uid, out int battlePlayerId)
        {
            lock (_lock)
            {
                int registered;
                if (_playerIds.TryGetValue(uid, out registered))
                {
                    battlePlayerId = registered;
                    return true;
                }
            }

            battlePlayerId = UnassignedPlayerId;
            return AllowUnassignedPlayers;
        }
    }
}
