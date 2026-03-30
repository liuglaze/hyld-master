/****************************************************
    BattleData.HitEvent.cs  --  partial class: HitEvent 消费 / 权威 HP 覆写 / 死亡判定
    从 BattleManger.cs 拆分，零逻辑变更
*****************************************************/

using System.Collections.Generic;
using UnityEngine;
using SocketProto;

namespace Manger
{
    public partial class BattleData
    {
        // =========================================================================================
        // 【模块作用】：处理服务端的伤害事件(HitEvent)与权威血量/死亡状态(Hp/IsDead)。
        // 
        // 【交互关系】：
        // 1. 调用方:
        //    - 由 `UDPSocketManger.HandleMessage` (收到权威帧 `BattlePushDowmAllFrameOpeartions` 时) 调用。
        //    - 调用顺序: 必须先调用 `ApplyHitEvents`，再调用 `ApplyAuthoritativeHpAndDeath`。
        // 2. 被影响方:
        //    - `HYLDStaticValue.Players`: 更新角色的动画(受击/死亡)、生存状态(`isNotDie`)。
        //    - `PlayerLogic`: 当 `isNotDie` 被置为 false 或 `playerBloodValue` <= 0 时，触发其 `playerDieLogic`。
        //
        // 【核心设计原则】：
        // - HitEvent (ApplyHitEvents) 仅作为【视觉表现】（触发受击动画），绝对不修改真实血量。
        // - 真实血量与死亡判定必须由 【权威帧状态】 (ApplyAuthoritativeHpAndDeath) 覆写决定。
        // =========================================================================================

        // ═══════ HitEvent 消费（7.1-7.6） ═══════

        /// <summary>
        /// 已处理的 HitEvent 去重键集合（attackId * 100000 + victimBattleId）。
        /// 防止帧重传导致重复扣血。
        /// </summary>
        private readonly HashSet<long> _appliedHitEventKeys = new HashSet<long>();

        /// <summary>
        /// [AHS-1] 本批次已触发受击动画的玩家索引集合，供 HP 差值兜底动画查询。
        /// 在 ApplyHitEvents 开头清空，每次触发受击动画时 Add。
        /// </summary>
        private readonly HashSet<int> _hitAnimatedPlayers = new HashSet<int>();

        private static long HitEventKey(int attackId, int victimBattleId)
            => (long)attackId * 100000 + victimBattleId;

        /// <summary>
        /// 【函数作用】：处理服务端发来的命中事件（HitEvent）。
        /// 【核心职责】：仅负责纯视觉表现（播放受击动画）。坚决不在这里扣血！
        /// 
        /// 【执行流程】：
        /// 1. 清空本批次的受击动画记录 (_hitAnimatedPlayers.Clear)。
        /// 2. 遍历服务端下发的 HitEvents。
        /// 3. 去重拦截：如果这个攻击ID之前处理过（防止丢包重传导致重复播放），直接跳过。
        /// 4. 表现层触发：找到受击者，触发 Animator 的 "Hit" 动画，并记录到 _hitAnimatedPlayers 中。
        /// 5. 死亡兜底(安全网)：如果服务端明确标记 `IsKill=true`，强制把客户端 HP 置 -1，并播放死亡动画。
        ///    (这是在权威 IsDead 状态到达前的提前表现补偿)。
        /// </summary>
        public void ApplyHitEvents(Google.Protobuf.Collections.RepeatedField<HitEvent> hitEvents)
        {
            if (hitEvents == null || hitEvents.Count == 0)
                return;

            // [AHS-1] 每批次开头清空受击动画记录，供兜底动画查询
            _hitAnimatedPlayers.Clear();

            for (int i = 0; i < hitEvents.Count; i++)
            {
                HitEvent evt = hitEvents[i];
                long key = HitEventKey(evt.AttackId, evt.VictimBattleId);

                // 7.4: 去重，防止帧重传重复扣血
                if (_appliedHitEventKeys.Contains(key))
                {
                    Logging.HYLDDebug.FrameTrace($"[AHS-1] HitEvent dedup skip: key={key}");
                    continue;
                }
                _appliedHitEventKeys.Add(key);

                // 找到 victim 对应的 playerIndex
                int victimIndex = -1;
                for (int p = 0; p < playerIndexBattleIds.Count; p++)
                {
                    if (playerIndexBattleIds[p] == evt.VictimBattleId)
                    {
                        victimIndex = p;
                        break;
                    }
                }
                if (victimIndex < 0 || victimIndex >= HYLDStaticValue.Players.Count)
                {
                    Logging.HYLDDebug.FrameTrace($"[HitEvent][Skip] attackId={evt.AttackId} victim={evt.VictimBattleId} -- victim not found");
                    continue;
                }

                // [AHS-1] HitEvent 降级为纯表现：不再扣血，HP 由权威帧 PlayerStates 覆写
                Logging.HYLDDebug.FrameTrace($"[AHS-1] HitEvent anim-only: victim={victimIndex} attackId={evt.AttackId} damage={evt.Damage} bloodBefore={HYLDStaticValue.Players[victimIndex].playerBloodValue} bloodAfter={HYLDStaticValue.Players[victimIndex].playerBloodValue}");

                // 受击动画（body 可能未初始化，做空检查）
                if (HYLDStaticValue.Players[victimIndex].body != null)
                {
                    Animator anim = HYLDStaticValue.Players[victimIndex].bodyAnimator;
                    if (anim != null)
                    {
                        anim.SetTrigger("Hit");
                        _hitAnimatedPlayers.Add(victimIndex);
                    }
                }

                // [AHS-1] 死亡判定：仅保留 IsKill 兜底（IsDead 未到达前的安全网），
                // 移除 playerBloodValue <= 0 分支（HP 不再由 HitEvent 修改）
                if (evt.IsKill && HYLDStaticValue.Players[victimIndex].isNotDie)
                {
                    // 服务端权威死亡：强制将客户端 HP 置为 -1，
                    // 确保 PlayerLogic.Update() 的 playerBlood < 0 检测能触发 playerDieLogic()
                    if (HYLDStaticValue.Players[victimIndex].playerBloodValue >= 0)
                        HYLDStaticValue.Players[victimIndex].playerBloodValue = -1;

                    HYLDStaticValue.Players[victimIndex].isNotDie = false;
                    Logging.HYLDDebug.FrameTrace($"[HitEvent][Kill] victim={evt.VictimBattleId} playerIndex={victimIndex} isKill={evt.IsKill} remainHp={HYLDStaticValue.Players[victimIndex].playerBloodValue}");

                    if (HYLDStaticValue.Players[victimIndex].body != null)
                    {
                        Animator anim = HYLDStaticValue.Players[victimIndex].bodyAnimator;
                        if (anim != null)
                        {
                            anim.SetBool("Die", true);
                            anim.SetTrigger("DieTrigger");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 战斗结束时清理 HitEvent 去重状态。
        /// </summary>
        public void ClearHitEventState()
        {
            _appliedHitEventKeys.Clear();
        }

        // ═══════ 权威 HP/IsDead 消费（AHS-2/3/4） ═══════

        /// <summary>
        /// 【函数作用】：处理服务端的权威状态（血量 HP 和 是否死亡 IsDead）。
        /// 【核心职责】：这里才是真正修改客户端血量和决定生死的地方！绝对服从服务端。
        /// 
        /// 【执行流程】：
        /// 1. 帧序保护：检查批次最新帧号是否 <= 已处理的 _lastAuthHpFrameId，如果是，说明是乱序旧包，直接丢弃（防止血量回弹）。
        /// 2. 遍历最新一帧中所有玩家的权威状态(PlayerStates)。
        /// 3. 初始化(首次包)：如果本地还没记录过该玩家最大血量，以服务端第一次发来的血量为准初始化本地 UI 血条上限。
        /// 4. 【核心覆写】：直接用服务端的 Hp 强行覆盖客户端的 playerBloodValue。
        /// 5. 【兜底动画补偿】：如果发现血量减少了（挨打了），但是刚才 ApplyHitEvents 里没播过受击动画（可能 HitEvent 包丢了），
        ///    则在这里强行补播一个 "Hit" 受击动画，防止玩家莫名其妙掉血却没反馈。
        /// 6. 【权威死亡判定】：如果服务端说你死了(IsDead=true)且客户端还活着，立刻强制血量-1，置标志位 isNotDie=false，并播放死亡动画。
        /// </summary>
        public void ApplyAuthoritativeHpAndDeath(Google.Protobuf.Collections.RepeatedField<AllPlayerOperation> frames, int frameId)
        {
            if (frames == null || frames.Count == 0) return;

            AllPlayerOperation lastFrame = frames[frames.Count - 1];
            int batchLastFrameId = lastFrame.Frameid;

            // -- 帧序保护：跳过乱序到达的旧批次，防止 HP 回弹 --
            if (batchLastFrameId <= _lastAuthHpFrameId)
            {
                Logging.HYLDDebug.FrameTrace($"[AHS-2] SKIP stale batch: batchLastFrame={batchLastFrameId} lastConsumed={_lastAuthHpFrameId} operationId={frameId}");
                return;
            }
            _lastAuthHpFrameId = batchLastFrameId;

            var playerStates = lastFrame.PlayerStates;

            if (playerStates == null || playerStates.Count == 0) return;

            for (int s = 0; s < playerStates.Count; s++)
            {
                AuthoritativePlayerState state = playerStates[s];
                int bpId = state.BattleId;

                // battleId -> playerIndex
                int playerIndex = -1;
                for (int p = 0; p < playerIndexBattleIds.Count; p++)
                {
                    if (playerIndexBattleIds[p] == bpId)
                    {
                        playerIndex = p;
                        break;
                    }
                }
                if (playerIndex < 0 || playerIndex >= HYLDStaticValue.Players.Count) continue;

                // [AHS-2] 记录覆写前的旧 HP
                int oldHp = HYLDStaticValue.Players[playerIndex].playerBloodValue;
                int newHp = state.Hp;

                // [AHS-4] 首次初始化 maxHp（用于血条比例计算）
                bool isFirstInit = !_playerMaxHp.ContainsKey(playerIndex);
                if (isFirstInit)
                {
                    _playerMaxHp[playerIndex] = newHp;
                    // 同时更新 hero.BloodValue 确保 PlayerLogic 的 playerBloodMax 同步
                    HYLDStaticValue.Players[playerIndex].hero.BloodValue = newHp;
                    Logging.HYLDDebug.FrameTrace($"[AHS-4] MaxHpInit: player={playerIndex} maxHp={newHp} oldDefault={oldHp} frame={frameId}");
                }

                // [AHS-2] 覆写 HP
                HYLDStaticValue.Players[playerIndex].playerBloodValue = newHp;
                Logging.HYLDDebug.FrameTrace($"[AHS-2] AuthHP: player={playerIndex} oldHp={oldHp} newHp={newHp} isDead={state.IsDead} frame={frameId}");

                // [AHS-3] 兜底受击动画：HP 下降但无 HitEvent 动画时补播
                // 首次初始化跳过（硬编码->服务端真实值的覆写不是真实扣血）
                if (newHp < oldHp && !isFirstInit)
                {
                    if (!_hitAnimatedPlayers.Contains(playerIndex))
                    {
                        // HitEvent 丢包，补播通用受击动画
                        if (HYLDStaticValue.Players[playerIndex].body != null)
                        {
                            Animator anim = HYLDStaticValue.Players[playerIndex].bodyAnimator;
                            if (anim != null)
                            {
                                anim.SetTrigger("Hit");
                            }
                        }
                        Logging.HYLDDebug.FrameTrace($"[AHS-3] FallbackHitAnim: player={playerIndex} hpDrop={oldHp - newHp} noHitEvent=true frame={frameId}");
                    }
                    else
                    {
                        Logging.HYLDDebug.FrameTrace($"[AHS-3] FallbackHitAnim SKIP: player={playerIndex} hpDrop={oldHp - newHp} hasHitEvent=true frame={frameId}");
                    }
                }

                // [AHS-2] 死亡判定：以 IsDead 为权威
                if (state.IsDead && HYLDStaticValue.Players[playerIndex].isNotDie)
                {
                    HYLDStaticValue.Players[playerIndex].playerBloodValue = -1;
                    HYLDStaticValue.Players[playerIndex].isNotDie = false;
                    Logging.HYLDDebug.FrameTrace($"[AHS-2] AuthDeath: player={playerIndex} isNotDie_was=true -> playerDieLogic() frame={frameId}");

                    if (HYLDStaticValue.Players[playerIndex].body != null)
                    {
                        Animator anim = HYLDStaticValue.Players[playerIndex].bodyAnimator;
                        if (anim != null)
                        {
                            anim.SetBool("Die", true);
                            anim.SetTrigger("DieTrigger");
                        }
                    }
                }
            }
        }
    }
}
