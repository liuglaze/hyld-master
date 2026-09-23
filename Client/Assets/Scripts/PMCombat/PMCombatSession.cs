// ============================================================================
//  PMCombatSession —— R6-A1：权威战斗会话（攻击账本 / 资源 / 投射物授权 / 伤害结算 / 胜负）
// ============================================================================
//
//  契约来源
//  --------
//  · Docs/plans/net-r6-combat-contract.md「A1 纯核心」与 D-R6-01/02/03；
//  · Docs/plans/_r6_combat_semantics_survey.md §1.1–1.7（共享数值 / PMBattleSim / 旧服务端语义）；
//  · Client/Assets/Scripts/PMCombat/PMCombatContracts.cs（主 Agent 冻结，只读）；
//  · PMProjectile 冻结类型：PMProjectileSpawnIntent（codec）/ PMProjectileSettlement（coordinator）。
//
//  本文件是什么
//  ------------
//  「谁有多少蓝/能量、这一次攻击准不准放、哪些弹被授权、命中扣多少血、谁先死、谁赢」——
//  这些在旧实现里散落在 `BattleController.Network.cs` / `BattleController.Bullets.cs` / `Battle.cs`
//  三个文件里，靠一堆并行字典（playerMana / playerSuperEnergy / playerManaRegenTimerMs /
//  dic_lastProcessedAttackId / playerHp / playerIsDead / oneGameOver）互相隐式约定。
//  R6 把它们收进**一个**可编译、可自动测试的纯核心对象：没有 Unity、没有网络、没有线程池。
//
//  三条硬边界
//  ----------
//  1) **零引擎依赖**：只引用 PMNet.Shared / PMNet.Projectile。`Tools/PMCombatCoreCheck`
//     用 netstandard2.0 + C# 7.3 编译本文件，把「核心与引擎解耦」变成门禁。
//  2) **只读共享配置**：数值一律来自 <see cref="BattleNumericConfig"/> / <see cref="PMBattleSim"/>，
//     绝不回写（回写等于客户端能改服务端数值）。
//  3) **不替代 R5**：本对象**不知道枪口在哪、也不做几何判定**。它只回答
//     「这个（owner, activation, projectileKey）是不是被批准的、方向是否匹配、伤害冻结成多少」；
//     真实位置/历史/胶囊/Director 预算仍由 PMProjectile（R5）裁决。上行 payload 里的
//     spec/damage/速度/寿命一律**不信**（PMProjectileSettlement 里也不存在伤害字段）。
//
//  记账铁律（每条都有对应测试）
//  ---------------------------
//  · **先校验、后扣费**：任何拒绝（含满表、配置非法、cooldown、资源不足）都发生在扣 mana/energy
//    之前，因此拒绝**没有副作用**；被拒绝的请求不会留下半个账本。
//  · **幂等**：同一个 (owner, activationId) 再次到达时返回**原始** decision（`Duplicate = true`），
//    不双扣资源、不刷新记录 TTL、也**不推翻**已 Accepted 的结论；内容不一致只累加冲突计数。
//  · **ID 空间是「单 owner + 单 epoch 单调递增」**（与 R5「activation 单 owner 单调不重用」一致），
//    因此账本按 (ownerNetId, activationId) 索引：不同 owner 用同一个 activationId 是**合法**的
//    两笔攻击，不是冲突。
//  · **水位保留**：每个 owner 记录见过的最大 activationId。记录被 TTL/容量回收后，
//    旧 ID 只会得到 `StaleId`，绝不会被当成一笔新攻击复活（`id 非 0 不回绕`）。
//    被**容量**拒绝的 ID 同样推进水位：拒绝必须留下「这个 ID 已被见过」的事实，
//    否则回收后重发同一 ID 会得到一笔「扣了资源却生成不出东西」的空接受。
//  · **授权回显有闸门**：`TryAuthorizeProjectile` 的重复 key 回显只说明「这个 key 曾经被授权」，
//    不能反推「现在还可以生成」——返回 spec 之前仍要过已开战/未终局/owner Connected 且未 Dead/
//    原 activation 未过 TTL；失败时不占槽、不撤销既有授权（R5 已拦住已登记 key 的重发）。
//  · **结算身份**：只收 ClientPredicted key；epoch / owner / origin（Key 与 settlement 双向一致）/
//    **非 0 AuthorityNetId** / attacker 在册且 Connected 且未 Dead 全部通过才写血。
//    AuthorityNetId 非 0 是「本地受信 DS」的边界标记，不是「本局权威是谁」的证明
//    （纯核心不知道 World/DS 名单，不新增假字典；R5 唯一出口 + AP 永不调用是宿主侧保证）。
//  · **有限资源**：账本 session 上限 512、已授权投射物 key 上限 2048、每笔攻击方向槽 =
//    计划弹数（≤ 64）。满了就拒，不无界增长、不丢已受理结论。
//  · **单调墙钟**：所有入口（Tick / RequestAttack / TryAuthorizeProjectile / ApplySettlement）
//    统一使用同一根 double 墙钟；NaN/负/倒退一律 `InvalidClock` 且不改任何战斗状态。
//    宿主必须**只从一处时钟喂入**（例如同一个 Stopwatch）。
//  · **补蓝有界**：每 ReloadSeconds 回 30，封顶 90；一次 Tick 最多结算 3 段，
//    时钟跳变再大也不会退化成巨大 while（旧服务端那处 while 是已知隐患）。
//  · **首杀即终局**：沿用旧服语义（调查 §1.5）。死亡锁 OutcomeId/WinnerTeamId/FirstKill，
//    只写一次；结束后攻击与结算**不再产生任何变化**。
//  · **断线**：保留水位与死亡真值；StartMatch 之后若只剩唯一一支在线的队伍 → 该队 Forfeit 获胜；
//    一支都不剩（无唯一队伍）→ winner 0（**不硬编码 1**）；未 StartMatch 不提前终局。
//
//  线程模型
//  --------
//  与 PMProjectile 核心一致：**单线程宿主所有**。本对象不加锁，宿主必须只在一条泵线程上调用。
// ============================================================================

using System;
using System.Collections.Generic;
using PMNet.Mover;
using PMNet.Projectile;
using PMNet.Shared;

namespace PMNet.Combat
{
    /// <summary>
    /// 一局战斗的权威核心：名册 / 资源 / 攻击账本 / 投射物授权 / 伤害与胜负。
    /// </summary>
    public sealed class PMCombatSession
    {
        // ------------------------------------------------------------------ 内部账本

        private sealed class PlayerRecord
        {
            public uint NetId;
            public int Uid;
            public int TeamId;
            public int HeroId;
            public int MaxHp;
            public int Hp;
            public int Mana;
            public int SuperEnergy;
            public bool Dead;
            public bool Connected = true;

            /// <summary>回蓝进度（毫秒）。普通攻击会清零（打断回蓝）。</summary>
            public double ReloadAccumMs;

            /// <summary>上一次被批准攻击的墙钟（毫秒）。负无穷 = 还没放过，首次必定不受 cooldown 影响。</summary>
            public double LastAttackWallTimeMs = double.NegativeInfinity;

            /// <summary>见过的最大 activationId（含被拒绝的请求），用于拦截旧 ID 复活。</summary>
            public uint HighWaterActivationId;
        }

        private sealed class AttackRecord
        {
            public uint ActivationId;
            public uint OwnerNetId;
            public bool Accepted;
            public bool IsSuper;
            public float AimX;
            public float AimZ;
            public double WallTimeMs;

            /// <summary>已冻结的结论；重复请求必须原样返回它（不得被后续冲突推翻）。</summary>
            public PMCombatAttackDecision Decision;

            /// <summary>被批准的计划（仅 Accepted 记录非 null）。方向/伤害/spec 的**唯一**来源。</summary>
            public PMCombatAttackPlan Plan;

            /// <summary>方向槽占用标记，长度 = 计划弹数。</summary>
            public bool[] SlotUsed;

            /// <summary>本笔攻击已授权的 projectile key（记录被回收时一并撤销）。</summary>
            public readonly List<PMProjectileKey> Keys = new List<PMProjectileKey>();
        }

        private sealed class AuthorizedKeyRecord
        {
            public uint Epoch;
            public uint OwnerNetId;
            public uint ActivationId;
            public PMProjectileOrigin Origin;
            public int Damage;
            public bool IsSuper;

            /// <summary>被授权时的 spec 快照（后续配置变化不得改写已冻结的弹）。</summary>
            public PMProjectileSpec Spec;

            /// <summary>已经结算过的目标（每个 key-target 只结算一次）。</summary>
            public readonly HashSet<uint> SettledTargets = new HashSet<uint>();
        }

        private readonly uint _epoch;
        private readonly Dictionary<uint, PlayerRecord> _players = new Dictionary<uint, PlayerRecord>();
        private readonly List<PlayerRecord> _order = new List<PlayerRecord>();
        private readonly Dictionary<ulong, AttackRecord> _attacks = new Dictionary<ulong, AttackRecord>();
        private readonly Dictionary<PMProjectileKey, AuthorizedKeyRecord> _authorized =
            new Dictionary<PMProjectileKey, AuthorizedKeyRecord>();

        private readonly List<ulong> _purgeBuffer = new List<ulong>();

        private bool _started;
        private PMCombatMatchOutcome _outcome;
        private uint _outcomeCounter;
        private int _idConflictCount;
        private double _lastWallNow;

        /// <summary>
        /// 建立一局会话。
        /// </summary>
        /// <param name="epoch">
        /// 本局的单调身份纪元，**必须非 0**（与 PMProjectile 核心同口径：
        /// <c>PMProjectileKey.IsValid</c> 要求 epoch 非 0）。0 是宿主编程错误，直接抛。
        /// </param>
        public PMCombatSession(uint epoch)
        {
            if (epoch == 0u)
            {
                throw new ArgumentOutOfRangeException("epoch",
                    "[PMCombatSession] epoch 0 无效（PMProjectileKey.IsValid 要求非 0）。");
            }

            _epoch = epoch;
            _outcome = new PMCombatMatchOutcome();
        }

        // ------------------------------------------------------------------ 只读视图

        /// <summary>本局身份纪元。</summary>
        public uint Epoch { get { return _epoch; } }

        /// <summary>是否已 StartMatch（只有 start 后才允许攻防与资源变化）。</summary>
        public bool Started { get { return _started; } }

        /// <summary>是否已终局。</summary>
        public bool Ended { get { return _outcome.Ended; } }

        /// <summary>当前胜负结论（值拷贝；未终局时 Ended=false / OutcomeId=0）。</summary>
        public PMCombatMatchOutcome Outcome { get { return _outcome; } }

        /// <summary>名册人数。</summary>
        public int PlayerCount { get { return _players.Count; } }

        /// <summary>同 ID 但请求内容不一致的次数（诊断用；结论始终以首次为准）。</summary>
        public int IdConflictCount { get { return _idConflictCount; } }

        /// <summary>当前存活的已授权投射物 key 数（上限 <see cref="PMCombatLimits.MaxAuthorizedProjectiles"/>）。</summary>
        public int AuthorizedProjectileCount { get { return _authorized.Count; } }

        /// <summary>当前攻击记录数（上限 <see cref="PMCombatLimits.MaxAttacks"/>）。</summary>
        public int AttackRecordCount { get { return _attacks.Count; } }

        /// <summary>取玩家只读快照（深拷贝一份，改它不影响会话）。未知 netId 返回 null。</summary>
        public PMCombatPlayerSnapshot GetPlayer(uint netId)
        {
            PlayerRecord record;
            if (!_players.TryGetValue(netId, out record))
            {
                return null;
            }

            return Snapshot(record);
        }

        /// <summary>按登记顺序取全部玩家快照（每个都是深拷贝；调用方随意改不会污染会话）。</summary>
        public PMCombatPlayerSnapshot[] CapturePlayers()
        {
            PMCombatPlayerSnapshot[] result = new PMCombatPlayerSnapshot[_order.Count];
            for (int i = 0; i < _order.Count; i++)
            {
                result[i] = Snapshot(_order[i]);
            }

            return result;
        }

        // ------------------------------------------------------------------ 名册

        /// <summary>
        /// 登记一名玩家（**可信 roster**，只能由 DS 侧从权威名册调用；客户端上行永远走不到这里）。
        ///
        /// <para>校验的是「是不是一个真实身份」而不是「是不是策划期望的编队」：
        /// netId/uid/team 必须为正、hero 必须在 0..19、uid 不重复、最多 6 人 2 队、
        /// StartMatch 之后不得再登记。刻意**不**要求 team 只能是 1/2 —— team 使用真实名册 TeamId。</para>
        /// </summary>
        public bool AddPlayer(uint netId, int uid, int teamId, int heroId)
        {
            if (_started)
            {
                return false;
            }

            if (netId == 0u || uid <= 0 || teamId <= 0)
            {
                return false;
            }

            if (heroId < 0 || heroId >= PMHeroId.Count)
            {
                return false;
            }

            if (_players.Count >= PMCombatLimits.MaxPlayers)
            {
                return false;
            }

            if (_players.ContainsKey(netId))
            {
                return false;
            }

            HashSet<int> teams = new HashSet<int>();
            teams.Add(teamId);
            for (int i = 0; i < _order.Count; i++)
            {
                PlayerRecord other = _order[i];
                if (other.Uid == uid)
                {
                    return false;
                }

                teams.Add(other.TeamId);
            }

            if (teams.Count > PMCombatLimits.MaxTeams)
            {
                return false;
            }

            HeroNumeric numeric = BattleNumericConfig.Get(heroId);

            PlayerRecord player = new PlayerRecord();
            player.NetId = netId;
            player.Uid = uid;
            player.TeamId = teamId;
            player.HeroId = heroId;
            player.MaxHp = numeric.MaxHp;
            player.Hp = numeric.MaxHp;
            player.Mana = BattleNumericConfig.ManaMax;
            player.SuperEnergy = 0;

            _players.Add(netId, player);
            _order.Add(player);
            return true;
        }

        /// <summary>
        /// 全名册接入后的开战门：必须同时满足**两个**条件才允许开战
        /// —— ① <paramref name="expectedPlayerCount"/> 与实际名册人数完全一致；
        /// ② 名册里每一名玩家都 <see cref="PMCombatPlayerSnapshot.Connected"/> 且未 Dead。
        ///
        /// <para>为什么需要人数一致：没有这道门，「名册还没接完就先按已有玩家判 forfeit」会提前终局。
        /// 未 StartMatch 之前，攻击一律 <see cref="PMCombatRejectReason.NotStarted"/>，
        /// 资源与胜负都不发生变化。</para>
        ///
        /// <para>为什么还要连接态：人数一致只保证「名册条目齐了」，不保证「人真的在」。
        /// 若允许用断线成员把名册名义人数补齐就开局，开局瞬间的在线队伍集合已经残缺，
        /// <see cref="EvaluateForfeit"/> 之外的一切（攻击、结算、回合推进）都会围绕一个
        /// 不成立的参赛集合展开；而 forfeit 只在 <see cref="Disconnect"/> 时判定，
        /// 于是会产生「名义上开了、实际从一开始就不完整」的对局。契约 B 要求宿主在
        /// 「全名册接入后」调 StartMatch，本方法把这句话变成可执行的门槛。</para>
        ///
        /// <para><c>!Dead</c> 半边是**防御性**的：Dead 只能由结算写入，而结算要先通过
        /// <c>_started</c>，所以「开战前有人 Dead」在当前实现里不可达；保留它是为了不让
        /// 「Dead 也算可开战成员」成为将来某条新路径的隐式假设。</para>
        ///
        /// <para>下界是 1 而不是 2：本对象不替产品判断「一局至少几人」，也不扩大成玩法选择；
        /// 单队 / 单人局仍然完整可用（<see cref="PMCombatLimits.MaxTeams"/> 只是上限），
        /// 且必须能表达「只有一个参与者且他掉线 → 没有唯一赢家（winner 0）」这条契约语义。</para>
        /// </summary>
        public bool StartMatch(int expectedPlayerCount)
        {
            if (_started)
            {
                return false;
            }

            if (expectedPlayerCount < 1 || expectedPlayerCount > PMCombatLimits.MaxPlayers)
            {
                return false;
            }

            if (expectedPlayerCount != _players.Count)
            {
                return false;
            }

            // 名册人数只是「条目齐了」；参赛集合还必须每一名都在线且存活。
            for (int i = 0; i < _order.Count; i++)
            {
                PlayerRecord member = _order[i];
                if (!member.Connected || member.Dead)
                {
                    return false;
                }
            }

            _started = true;
            return true;
        }

        /// <summary>
        /// 标记断线。保留水位与死亡真值；StartMatch 之后若只剩唯一在线队伍 → 该队 Forfeit 获胜。
        /// </summary>
        /// <returns>状态是否发生变化（未知/已断线的重复调用返回 false）。</returns>
        public bool Disconnect(uint netId)
        {
            PlayerRecord player;
            if (!_players.TryGetValue(netId, out player))
            {
                return false;
            }

            if (!player.Connected)
            {
                return false;
            }

            player.Connected = false;
            EvaluateForfeit();
            return true;
        }

        // ------------------------------------------------------------------ 时钟 / Tick

        /// <summary>
        /// 推进墙钟并按 <c>每 ReloadSeconds 回 30</c> 补蓝（封顶 90）。
        ///
        /// <para>不补的对象：未 StartMatch / 已终局 / 已死亡 / 已断线 —— 与契约「死/断/ended 不回」一致。
        /// 一次 Tick 最多结算 3 段（90 蓝 = 3 段），因此时钟跳变再大也不会退化成巨大循环。</para>
        /// </summary>
        /// <returns>时钟合法并已被接受返回 true；非法/倒退返回 false 且**不修改任何状态**。</returns>
        public bool Tick(double wallNow)
        {
            if (!IsFinite(wallNow) || wallNow < 0.0 || wallNow < _lastWallNow)
            {
                return false;
            }

            double deltaMs = wallNow - _lastWallNow;
            _lastWallNow = wallNow;

            if (!_started || _outcome.Ended)
            {
                return true;
            }

            if (deltaMs < 0.0)
            {
                deltaMs = 0.0;
            }

            for (int i = 0; i < _order.Count; i++)
            {
                PlayerRecord player = _order[i];
                if (!player.Connected || player.Dead)
                {
                    continue;
                }

                RechargeMana(player, deltaMs);
            }

            return true;
        }

        // ------------------------------------------------------------------ 攻击请求

        /// <summary>
        /// 请求一次攻击（普通或大招）。
        ///
        /// <para><b>返回的 decision 是深拷贝</b>：调用方改它不影响账本（「冻结 decision」）。
        /// 所有拒绝都发生在扣费之前；同一个 (owner, activationId) 重复到达时返回**原始**结论
        /// （<c>Duplicate = true</c>），不双扣、不刷新 TTL、不推翻已 Accepted。</para>
        /// </summary>
        /// <param name="ownerNetId">发出请求的玩家 NetId（可信身份，来自宿主绑定，不是上行字段）。</param>
        /// <param name="activationId">该 owner 单调递增的激活 ID（0 非法）。</param>
        /// <param name="isSuper">是否大招。</param>
        /// <param name="aimX">瞄准世界方向 X。</param>
        /// <param name="aimZ">瞄准世界方向 Z。</param>
        /// <param name="wallNow">单调墙钟（毫秒）。</param>
        public PMCombatAttackDecision RequestAttack(uint ownerNetId, uint activationId, bool isSuper,
            float aimX, float aimZ, double wallNow)
        {
            PMCombatAttackDecision decision =
                RequestAttackCore(ownerNetId, activationId, isSuper, aimX, aimZ, wallNow);
            return decision.Clone();
        }

        private PMCombatAttackDecision RequestAttackCore(uint ownerNetId, uint activationId, bool isSuper,
            float aimX, float aimZ, double wallNow)
        {
            if (!AcceptClock(wallNow))
            {
                return MakeDecision(activationId, PMCombatRejectReason.InvalidClock, null);
            }

            if (activationId == 0u)
            {
                return MakeDecision(activationId, PMCombatRejectReason.InvalidId, null);
            }

            // 幂等：必须在「比赛状态」检查**之前**。否则比赛结束后的一次重复请求会把
            // 已经 Accepted 的结论改写成 MatchEnded（等于推翻已受理结论）。
            ulong attackKey = AttackKey(ownerNetId, activationId);
            AttackRecord existing;
            if (_attacks.TryGetValue(attackKey, out existing))
            {
                bool sameContent = existing.OwnerNetId == ownerNetId
                    && existing.IsSuper == isSuper
                    && PMCombatWeaponPlanner.IsSameDirection(existing.AimX, existing.AimZ, aimX, aimZ);
                if (!sameContent)
                {
                    _idConflictCount++;
                }

                PMCombatAttackDecision echo = existing.Decision.Clone();
                echo.Duplicate = true;
                return echo;
            }

            PlayerRecord player;
            if (!_players.TryGetValue(ownerNetId, out player))
            {
                return MakeDecision(activationId, PMCombatRejectReason.UnknownPlayer, null);
            }

            // 未 StartMatch / 已终局：**不记账、不写水位**（避免开战前或终局后的上行把账本塞满）。
            if (!_started)
            {
                return MakeDecision(activationId, PMCombatRejectReason.NotStarted, null);
            }

            if (_outcome.Ended)
            {
                return MakeDecision(activationId, PMCombatRejectReason.MatchEnded, null);
            }

            if (!player.Connected)
            {
                return MakeDecision(activationId, PMCombatRejectReason.Disconnected, null);
            }

            if (player.Dead)
            {
                return MakeDecision(activationId, PMCombatRejectReason.Dead, null);
            }

            // 水位：低于「见过的最大值」说明是旧 ID（可能已被 TTL/容量回收），不得当成新请求。
            if (activationId <= player.HighWaterActivationId)
            {
                return MakeDecision(activationId, PMCombatRejectReason.StaleId, null);
            }

            // 从这里开始，每一个新 ID 都要占用一个账本槽位（含被拒绝的结论 —— 幂等需要它）。
            if (!EnsureCapacity(wallNow))
            {
                // 容量拒绝**也要推进该 owner 的单调水位**。否则同一个 activationId 在记录被 TTL
                // 回收之后会被当成一笔全新攻击接受：R5 已经因为这次 Rejected 撤销了该 activation
                // 的假弹，重接受只会扣掉资源却生成不出东西（无法生成 = 资源黑洞）。
                // 不需要插入满表记录（那正是容量拒绝要避免的），水位足够：既有的已记账 ID 仍由
                // 上面的 _attacks 查询在**水位之前**返回原结论（Duplicate 语义不受影响）。
                player.HighWaterActivationId = activationId;
                return MakeDecision(activationId, PMCombatRejectReason.Capacity, null);
            }

            PMCombatAttackPlan plan;
            PMCombatRejectReason reason;
            if (!PMCombatWeaponPlanner.TryBuild(player.HeroId, isSuper, aimX, aimZ, out plan, out reason))
            {
                return RecordRejection(player, activationId, isSuper, aimX, aimZ, wallNow, reason);
            }

            if (wallNow - player.LastAttackWallTimeMs < (double)plan.FireIntervalMs)
            {
                return RecordRejection(player, activationId, isSuper, aimX, aimZ, wallNow,
                    PMCombatRejectReason.Cooldown);
            }

            if (isSuper)
            {
                if (player.SuperEnergy < BattleNumericConfig.SuperEnergyMax)
                {
                    return RecordRejection(player, activationId, isSuper, aimX, aimZ, wallNow,
                        PMCombatRejectReason.InsufficientEnergy);
                }
            }
            else if (player.Mana < plan.ManaCost)
            {
                return RecordRejection(player, activationId, isSuper, aimX, aimZ, wallNow,
                    PMCombatRejectReason.InsufficientMana);
            }

            // ---- 通过全部校验，才允许改资源 ----
            if (isSuper)
            {
                // 大招：能量清零，蓝量不变。
                player.SuperEnergy = 0;
            }
            else
            {
                player.Mana -= plan.ManaCost;
                // 普通攻击打断回蓝进度（与旧服务端 playerManaRegenTimerMs = 0 等价）。
                player.ReloadAccumMs = 0.0;
            }

            player.LastAttackWallTimeMs = wallNow;

            PMCombatAttackDecision accepted = MakeDecision(activationId, PMCombatRejectReason.None, plan);
            accepted.Accepted = true;

            AttackRecord record = new AttackRecord();
            record.ActivationId = activationId;
            record.OwnerNetId = ownerNetId;
            record.Accepted = true;
            record.IsSuper = isSuper;
            record.AimX = aimX;
            record.AimZ = aimZ;
            record.WallTimeMs = wallNow;
            record.Decision = accepted;
            record.Plan = plan;
            record.SlotUsed = new bool[plan.Directions == null ? 0 : plan.Directions.Length];

            _attacks[attackKey] = record;
            player.HighWaterActivationId = activationId;
            return record.Decision;
        }

        // ------------------------------------------------------------------ 投射物授权

        /// <summary>
        /// 授权一颗真实投射物（AP 上行意图 / DS 侧同一口径）。
        ///
        /// <para>只认**已被批准**的 activation，并且只在计划里**尚未消费**的方向槽上匹配
        /// （角度容差 <see cref="PMCombatLimits.DirectionToleranceDegrees"/> 度，同方向多槽逐个占用）。
        /// 每个 projectile key 只授权一次。</para>
        ///
        /// <para><b>重复 key 不是无条件回显</b>：只有该 key 当前仍处于「可以生成」的状态
        /// （已开战、未终局、owner 仍在册且 Connected/未 Dead、原 activation 记录存在且未过
        /// <see cref="PMCombatLimits.AttackRecordTtlMs"/>）时，才原样返回既有 spec；
        /// 状态已变化则按当前状态拒绝。拒绝**不占槽、不改资源、也**不会撤销既有授权元数据
        /// （契约「任何 Rejected 不得影响既有批准攻击」指的是账本与资源，不是「必须回显 true」）。
        /// R5 驱动已在策略之前拦下已登记 key 的重发（<c>TryFindAdmittedKey</c> / <c>IsRetired</c>），
        /// 所以这里返回 false 不会反向撤销一颗已经出膛的弹。</para>
        ///
        /// <para><b>本方法不知道枪口位置</b>：<c>intent.Position</c> 一律不参与判定，
        /// 也不替代 R5 的枪口/历史/胶囊检查。伤害与 spec 只来自冻结的计划。</para>
        /// </summary>
        /// <param name="ownerNetId">可信 owner（来自宿主绑定）。</param>
        /// <param name="intent">上行 spawn 意图（其 spec/伤害字段不存在，也一律不信）。</param>
        /// <param name="wallNow">单调墙钟（毫秒）。</param>
        /// <param name="spec">成功时为计划 spec 的深拷贝（R5 用它生成真实弹）。</param>
        /// <param name="reason">失败原因；成功时为 None。</param>
        public bool TryAuthorizeProjectile(uint ownerNetId, PMProjectileSpawnIntent intent,
            double wallNow, out PMProjectileSpec spec, out PMCombatRejectReason reason)
        {
            spec = null;
            reason = PMCombatRejectReason.None;

            if (!AcceptClock(wallNow))
            {
                reason = PMCombatRejectReason.InvalidClock;
                return false;
            }

            if (intent == null)
            {
                reason = PMCombatRejectReason.InvalidId;
                return false;
            }

            if (ownerNetId == 0u || intent.ActivationId == 0u || intent.Key.ProjectileId == 0u)
            {
                reason = PMCombatRejectReason.InvalidId;
                return false;
            }

            // 身份：owner / epoch / origin 三者必须完全对上，否则就是伪造或串局。
            // ServerDirect 由 DS 自身产生、不走这条上行授权路径（契约「不同 owner/epoch/ServerDirect 拒」）。
            if (intent.Key.OwnerNetId != ownerNetId
                || intent.Key.Epoch != _epoch
                || intent.Key.Origin != PMProjectileOrigin.ClientPredicted)
            {
                reason = PMCombatRejectReason.IdentityMismatch;
                return false;
            }

            // 重复 key：不占槽、不改资源，但**不等于「现在仍然允许生成」**。
            // 必须重新确认：已开战、未终局、owner 仍在册且 Connected/未 Dead、
            // 原 activation 记录仍在且未过 TTL。否则宿主会据这份回显在终局后 / 记录过期后 /
            // 玩家已断线或已死后真的再生成一颗弹，而它的结算会被 session 静默丢弃
            // （客户端看到弹、HP 不动 = 不可达伤害）。
            // 契约「重复 key 回既有授权」约束的是「不占槽、不改资源、不翻既有结论」，
            // 不是「绕过当前战斗状态」；R5 驱动已在策略之前拦下已登记 key 的重发，
            // 因此这里按当前状态拒绝**不会**反向撤销已经出膛的弹。
            AuthorizedKeyRecord duplicate;
            if (_authorized.TryGetValue(intent.Key, out duplicate))
            {
                if (duplicate.ActivationId != intent.ActivationId || duplicate.OwnerNetId != ownerNetId)
                {
                    // 同一个 key 绑定到了另一笔 activation：这是身份冲突，不是「重复请求」。
                    reason = PMCombatRejectReason.IdentityMismatch;
                    return false;
                }

                if (!_started)
                {
                    reason = PMCombatRejectReason.NotStarted;
                    return false;
                }

                if (_outcome.Ended)
                {
                    reason = PMCombatRejectReason.MatchEnded;
                    return false;
                }

                PlayerRecord duplicateOwner;
                if (!_players.TryGetValue(ownerNetId, out duplicateOwner))
                {
                    reason = PMCombatRejectReason.UnknownPlayer;
                    return false;
                }

                if (!duplicateOwner.Connected)
                {
                    reason = PMCombatRejectReason.Disconnected;
                    return false;
                }

                if (duplicateOwner.Dead)
                {
                    reason = PMCombatRejectReason.Dead;
                    return false;
                }

                AttackRecord duplicateAttack;
                if (!_attacks.TryGetValue(AttackKey(ownerNetId, intent.ActivationId), out duplicateAttack)
                    || !duplicateAttack.Accepted)
                {
                    reason = PMCombatRejectReason.UnknownAttack;
                    return false;
                }

                if (wallNow - duplicateAttack.WallTimeMs > (double)PMCombatLimits.AttackRecordTtlMs)
                {
                    reason = PMCombatRejectReason.Expired;
                    return false;
                }

                spec = duplicate.Spec == null ? null : duplicate.Spec.Clone();
                return true;
            }

            float dirX;
            float dirZ;
            if (!PMCombatWeaponPlanner.TryNormalizeDirection(intent.Direction.X, intent.Direction.Z,
                    out dirX, out dirZ))
            {
                reason = PMCombatRejectReason.InvalidAim;
                return false;
            }

            if (!_started)
            {
                reason = PMCombatRejectReason.NotStarted;
                return false;
            }

            if (_outcome.Ended)
            {
                reason = PMCombatRejectReason.MatchEnded;
                return false;
            }

            PlayerRecord player;
            if (!_players.TryGetValue(ownerNetId, out player))
            {
                reason = PMCombatRejectReason.UnknownPlayer;
                return false;
            }

            if (!player.Connected)
            {
                reason = PMCombatRejectReason.Disconnected;
                return false;
            }

            if (player.Dead)
            {
                reason = PMCombatRejectReason.Dead;
                return false;
            }

            AttackRecord record;
            if (!_attacks.TryGetValue(AttackKey(ownerNetId, intent.ActivationId), out record))
            {
                // 从没见过的 ID：高于水位 = 未知；低于水位 = 已被回收的旧 ID（不得复活）。
                reason = intent.ActivationId <= player.HighWaterActivationId
                    ? PMCombatRejectReason.StaleId
                    : PMCombatRejectReason.UnknownAttack;
                return false;
            }

            if (record.OwnerNetId != ownerNetId)
            {
                reason = PMCombatRejectReason.IdentityMismatch;
                return false;
            }

            if (!record.Accepted)
            {
                reason = PMCombatRejectReason.UnknownAttack;
                return false;
            }

            if (wallNow - record.WallTimeMs > (double)PMCombatLimits.AttackRecordTtlMs)
            {
                reason = PMCombatRejectReason.Expired;
                return false;
            }

            if (_authorized.Count >= PMCombatLimits.MaxAuthorizedProjectiles)
            {
                reason = PMCombatRejectReason.ProjectileLimit;
                return false;
            }

            PMVector3[] directions = record.Plan.Directions;
            if (directions == null || record.SlotUsed == null
                || record.Keys.Count >= directions.Length)
            {
                reason = PMCombatRejectReason.ProjectileLimit;
                return false;
            }

            int slot = FindFreeSlot(record, dirX, dirZ);
            if (slot < 0)
            {
                reason = PMCombatRejectReason.DirectionMismatch;
                return false;
            }

            record.SlotUsed[slot] = true;
            record.Keys.Add(intent.Key);

            AuthorizedKeyRecord authorized = new AuthorizedKeyRecord();
            authorized.Epoch = intent.Key.Epoch;
            authorized.OwnerNetId = ownerNetId;
            authorized.ActivationId = intent.ActivationId;
            authorized.Origin = intent.Key.Origin;
            authorized.Damage = record.Plan.Damage;
            authorized.IsSuper = record.IsSuper;
            authorized.Spec = record.Plan.Spec == null ? null : record.Plan.Spec.Clone();
            _authorized.Add(intent.Key, authorized);

            spec = authorized.Spec == null ? null : authorized.Spec.Clone();
            return true;
        }

        // ------------------------------------------------------------------ 结算

        /// <summary>
        /// 消费一条 R5 权威结算（DS 唯一出口；AP 永不调用）。
        ///
        /// <para>流程：时钟 → **整批结构先验** → key/owner/epoch/activation/授权记录存活 →
        /// 逐目标（敌队 / 非 self / 未 dead / 每 key-target 一次）→ 伤害冻结值 → 首杀锁终局。</para>
        ///
        /// <para>返回值语义：<c>true</c> = 这条结算被受理并消费（各目标按规则写血/回能，
        /// 不合格的目标只是被跳过，**不会**把整批的合法目标一起丢掉）；
        /// <c>false</c> = 拒绝，且**没有任何状态变化**。结构问题（空批、目标 0、同批重复目标、
        /// 超长批）属于「整批拒绝」，不会让部分目标先扣血。</para>
        /// </summary>
        public bool ApplySettlement(PMProjectileSettlement settlement, double wallNow)
        {
            PMCombatRejectReason ignored;
            return ApplySettlement(settlement, wallNow, out ignored);
        }

        /// <summary>带拒绝原因的结算入口。</summary>
        public bool ApplySettlement(PMProjectileSettlement settlement, double wallNow,
            out PMCombatRejectReason reason)
        {
            reason = PMCombatRejectReason.None;

            if (!AcceptClock(wallNow))
            {
                reason = PMCombatRejectReason.InvalidClock;
                return false;
            }

            // ---- 整批结构先验（任何一条不合格 → 整批拒绝，绝不部分扣血）----
            PMProjectileValidatedHit[] hits = settlement.Hits;
            if (hits == null || hits.Length == 0 || settlement.HitCount != hits.Length
                || hits.Length > PMProjectileLimits.MaxTargets)
            {
                reason = PMCombatRejectReason.InvalidId;
                return false;
            }

            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i].TargetNetId == 0u)
                {
                    reason = PMCombatRejectReason.InvalidId;
                    return false;
                }

                for (int j = i + 1; j < hits.Length; j++)
                {
                    if (hits[i].TargetNetId == hits[j].TargetNetId)
                    {
                        reason = PMCombatRejectReason.InvalidId;
                        return false;
                    }
                }
            }

            if (settlement.ActivationId == 0u || settlement.Key.ProjectileId == 0u)
            {
                reason = PMCombatRejectReason.InvalidId;
                return false;
            }

            // 身份一致性：epoch / owner 双向绑定 / origin 三处口径（Key.Origin、settlement.Origin、
            // 允许集）必须互相一致；AuthorityNetId 必须非 0。
            //
            // AuthorityNetId 是「本地受信 DS」的边界标记，不是「本局权威是谁」的证明：本对象是纯核心，
            // 不知道自己在哪个 World / 哪台 DS 上跑，也不持有权威 DS 名单，**因此只校验非 0**
            // （0 表示「没有权威身份的伪结算」，与 PMProjectile codec 对下行快照的同一口径）。
            // 「这份 settlement 真的来自本局权威 DS」由 host 在把 R5 唯一出口接进来时保证：
            // R5 只把 Authoritative 验证器产出的结算交给 ApplySettlement，AP 永不调用。
            // 这里刻意不新增任何「权威字典」——那会是一个无法在纯核心内验证真伪的假字典。
            if (settlement.Key.Epoch != _epoch
                || settlement.Key.OwnerNetId != settlement.OwnerNetId
                || settlement.Key.OwnerNetId == 0u
                || settlement.Key.Origin != PMProjectileOrigin.ClientPredicted
                || settlement.Origin != settlement.Key.Origin
                || settlement.AuthorityNetId == 0u)
            {
                reason = PMCombatRejectReason.IdentityMismatch;
                return false;
            }

            if (!_started)
            {
                reason = PMCombatRejectReason.NotStarted;
                return false;
            }

            if (_outcome.Ended)
            {
                // 终局后结算一律无变化（首杀已锁，不能靠迟到结算改写胜负）。
                reason = PMCombatRejectReason.MatchEnded;
                return false;
            }

            AuthorizedKeyRecord authorized;
            if (!_authorized.TryGetValue(settlement.Key, out authorized))
            {
                reason = PMCombatRejectReason.UnknownAttack;
                return false;
            }

            if (authorized.ActivationId != settlement.ActivationId
                || authorized.OwnerNetId != settlement.Key.OwnerNetId
                || authorized.Epoch != settlement.Key.Epoch)
            {
                reason = PMCombatRejectReason.IdentityMismatch;
                return false;
            }

            AttackRecord record;
            if (!_attacks.TryGetValue(AttackKey(authorized.OwnerNetId, authorized.ActivationId), out record)
                || !record.Accepted)
            {
                reason = PMCombatRejectReason.UnknownAttack;
                return false;
            }

            // 「授权存活 record」：记录被 TTL 淘汰之后，再也不接受它的结算。
            if (wallNow - record.WallTimeMs > (double)PMCombatLimits.AttackRecordTtlMs)
            {
                reason = PMCombatRejectReason.Expired;
                return false;
            }

            PlayerRecord attacker;
            if (!_players.TryGetValue(authorized.OwnerNetId, out attacker))
            {
                reason = PMCombatRejectReason.UnknownPlayer;
                return false;
            }

            // attacker 必须「存在且当前可写」：断线者不得再结算（他的弹在权威侧已随会话失效，
            // 迟到或伪造的结算不能替一个已离场的身份改血量/回能）。
            // Dead 半边与 <see cref="PMCombatRejectReason.Dead"/> 同属防御性：Dead ⇒ 首杀终局 ⇒
            // 上面的 <c>_outcome.Ended</c> 已经先返回 MatchEnded，所以这里通常不可达。
            if (!attacker.Connected)
            {
                reason = PMCombatRejectReason.Disconnected;
                return false;
            }

            if (attacker.Dead)
            {
                reason = PMCombatRejectReason.Dead;
                return false;
            }

            int damage = authorized.Damage;

            for (int i = 0; i < hits.Length; i++)
            {
                uint targetNetId = hits[i].TargetNetId;

                if (authorized.SettledTargets.Contains(targetNetId))
                {
                    // 每个 key-target 只结算一次（重复回流不产生第二次伤害）。
                    continue;
                }

                PlayerRecord target;
                if (!_players.TryGetValue(targetNetId, out target))
                {
                    continue;
                }

                if (target.NetId == authorized.OwnerNetId || target.TeamId == attacker.TeamId)
                {
                    continue;
                }

                if (!target.Connected || target.Dead)
                {
                    continue;
                }

                authorized.SettledTargets.Add(targetNetId);

                int hp = target.Hp - damage;
                if (hp < 0)
                {
                    hp = 0;
                }
                else if (hp > target.MaxHp)
                {
                    hp = target.MaxHp;
                }

                target.Hp = hp;

                if (damage > 0)
                {
                    attacker.SuperEnergy = PMBattleSim.RechargeSuperEnergy(
                        attacker.SuperEnergy, damage, BattleNumericConfig.SuperEnergyMax);

                    if (target.Hp == 0 && !target.Dead)
                    {
                        target.Dead = true;
                        // 首杀即终局：只锁一次；damage == 0 永远走不到这里（伤害 0 不首杀）。
                        EndMatch(attacker.TeamId, PMCombatEndReason.FirstKill);

                        // 本批的这一个目标已经把比赛打成终局，后续目标**不再写入**。
                        // 否则「终局后所有结算一律拒绝且无任何变化」这条不变式会被同一批里
                        // 排在后面的命中破坏（同一颗弹的多个命中目标会继续掉血/被置 Dead）。
                        // 注意：结构校验仍发生在任何写入之前，所以这里退出的是「已确定的终局」，
                        // 而不是「半提交」；本批已写入的目标仍是完整一致的。
                        break;
                    }
                }
            }

            return true;
        }

        // ------------------------------------------------------------------ 内部：账本 / 容量 / 时钟

        private static ulong AttackKey(uint ownerNetId, uint activationId)
        {
            return ((ulong)ownerNetId << 32) | (ulong)activationId;
        }

        private PMCombatAttackDecision RecordRejection(PlayerRecord player, uint activationId,
            bool isSuper, float aimX, float aimZ, double wallNow, PMCombatRejectReason reason)
        {
            PMCombatAttackDecision decision = MakeDecision(activationId, reason, null);

            AttackRecord record = new AttackRecord();
            record.ActivationId = activationId;
            record.OwnerNetId = player.NetId;
            record.Accepted = false;
            record.IsSuper = isSuper;
            record.AimX = aimX;
            record.AimZ = aimZ;
            record.WallTimeMs = wallNow;
            record.Decision = decision;
            record.Plan = null;
            record.SlotUsed = null;

            _attacks[AttackKey(player.NetId, activationId)] = record;
            player.HighWaterActivationId = activationId;
            return decision;
        }

        private static PMCombatAttackDecision MakeDecision(uint activationId,
            PMCombatRejectReason reason, PMCombatAttackPlan plan)
        {
            PMCombatAttackDecision decision = new PMCombatAttackDecision();
            decision.ActivationId = activationId;
            decision.Accepted = reason == PMCombatRejectReason.None;
            decision.Duplicate = false;
            decision.Reason = reason;
            decision.Plan = plan;
            return decision;
        }

        /// <summary>回收已过 TTL 的记录（连同其已授权 key），为「满表」腾出真正可用的空间。</summary>
        private void PurgeExpired(double wallNow)
        {
            _purgeBuffer.Clear();
            foreach (KeyValuePair<ulong, AttackRecord> pair in _attacks)
            {
                if (wallNow - pair.Value.WallTimeMs > (double)PMCombatLimits.AttackRecordTtlMs)
                {
                    _purgeBuffer.Add(pair.Key);
                }
            }

            for (int i = 0; i < _purgeBuffer.Count; i++)
            {
                RemoveAttack(_purgeBuffer[i]);
            }

            _purgeBuffer.Clear();
        }

        private void RemoveAttack(ulong attackKey)
        {
            AttackRecord record;
            if (!_attacks.TryGetValue(attackKey, out record))
            {
                return;
            }

            for (int i = 0; i < record.Keys.Count; i++)
            {
                _authorized.Remove(record.Keys[i]);
            }

            _attacks.Remove(attackKey);
        }

        private bool EnsureCapacity(double wallNow)
        {
            PurgeExpired(wallNow);
            return _attacks.Count < PMCombatLimits.MaxAttacks;
        }

        /// <summary>接受严格单调（非递减）墙钟。成功时推进水位；失败时不改任何状态。</summary>
        private bool AcceptClock(double wallNow)
        {
            if (!IsFinite(wallNow) || wallNow < 0.0 || wallNow < _lastWallNow)
            {
                return false;
            }

            _lastWallNow = wallNow;
            return true;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>
        /// 找第一个「未占用且与给入方向在容差内」的方向槽。
        /// 同方向多槽（LaunchAngle == 0 的多弹，如柯尔特/格尔）因此会逐个被占用，而不是一颗弹占满全部槽。
        /// </summary>
        private static int FindFreeSlot(AttackRecord record, float dirX, float dirZ)
        {
            PMVector3[] directions = record.Plan.Directions;
            if (directions == null)
            {
                return -1;
            }

            for (int i = 0; i < directions.Length; i++)
            {
                if (record.SlotUsed[i])
                {
                    continue;
                }

                if (PMCombatWeaponPlanner.IsSameDirection(directions[i].X, directions[i].Z, dirX, dirZ))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>有界补蓝：每 ReloadSeconds 回 30，最多 3 段（90 蓝），封顶 ManaMax。</summary>
        private static void RechargeMana(PlayerRecord player, double deltaMs)
        {
            int manaMax = BattleNumericConfig.ManaMax;
            if (player.Mana >= manaMax)
            {
                player.ReloadAccumMs = 0.0;
                return;
            }

            HeroNumeric numeric = BattleNumericConfig.Get(player.HeroId);
            double reloadMs = (double)numeric.ReloadSeconds * 1000.0;
            if (!(reloadMs > 0.0))
            {
                // 坏配置 / NaN：不回蓝，不做除零。
                player.ReloadAccumMs = 0.0;
                return;
            }

            double timer = player.ReloadAccumMs + deltaMs;
            int steps;
            if (timer >= reloadMs * 3.0)
            {
                // 有界：90 蓝最多 3 段，时钟跳变再大也不会跑成巨大循环。
                steps = 3;
            }
            else
            {
                steps = (int)(timer / reloadMs);
            }

            for (int i = 0; i < steps; i++)
            {
                if (player.Mana >= manaMax)
                {
                    break;
                }

                int next = player.Mana + BattleNumericConfig.ManaPerSegment;
                player.Mana = next < manaMax ? next : manaMax;
                timer -= reloadMs;
            }

            if (player.Mana >= manaMax)
            {
                timer = 0.0;
            }

            player.ReloadAccumMs = timer;
        }

        private void EndMatch(int winnerTeamId, PMCombatEndReason reason)
        {
            if (_outcome.Ended)
            {
                return;
            }

            _outcomeCounter++;
            PMCombatMatchOutcome outcome = new PMCombatMatchOutcome();
            outcome.Ended = true;
            outcome.OutcomeId = _outcomeCounter;
            outcome.WinnerTeamId = winnerTeamId;
            outcome.Reason = reason;
            _outcome = outcome;
        }

        /// <summary>
        /// 断线后的终局判定：最多 2 队的约束让「剩余队伍是否唯一」总有确定答案。
        ///
        /// <para>0 支队伍（参与者全部掉线）时 winner 为 0 —— 这正是契约里
        /// 「无唯一队伍则 winner 0（不硬编码 1）」的可达路径。</para>
        /// </summary>
        private void EvaluateForfeit()
        {
            if (!_started || _outcome.Ended)
            {
                return;
            }

            bool found = false;
            int onlyTeam = 0;
            HashSet<int> teams = new HashSet<int>();
            for (int i = 0; i < _order.Count; i++)
            {
                PlayerRecord player = _order[i];
                if (!player.Connected)
                {
                    continue;
                }

                teams.Add(player.TeamId);
                if (teams.Count > 1)
                {
                    return;
                }

                onlyTeam = player.TeamId;
                found = true;
            }

            if (found && teams.Count == 1)
            {
                EndMatch(onlyTeam, PMCombatEndReason.Forfeit);
            }
            else if (!found)
            {
                EndMatch(0, PMCombatEndReason.Draw);
            }
        }

        private PMCombatPlayerSnapshot Snapshot(PlayerRecord record)
        {
            PMCombatPlayerSnapshot snapshot = new PMCombatPlayerSnapshot();
            snapshot.Epoch = _epoch;
            snapshot.NetId = record.NetId;
            snapshot.Uid = record.Uid;
            snapshot.TeamId = record.TeamId;
            snapshot.HeroId = record.HeroId;
            snapshot.Hp = record.Hp;
            snapshot.MaxHp = record.MaxHp;
            snapshot.Mana = record.Mana;
            snapshot.SuperEnergy = record.SuperEnergy;
            snapshot.Dead = record.Dead;
            snapshot.Connected = record.Connected;
            return snapshot;
        }
    }
}
