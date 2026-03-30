/****************************************************
    BattleData.Authority.cs  --  partial class: 权威帧入口 / 位置校正 / 动画更新
    从 BattleManger.cs 拆分，零逻辑变更
*****************************************************/

using System.Collections.Generic;
using UnityEngine;
using SocketProto;

namespace Manger
{
    public partial class BattleData
    {
        

        // ═══════ 权威帧入口 ═══════

        /// <summary>
        /// BattleManager 侧的整包权威更新入口。
        /// 这里就是BattleManager在接收到服务端权威帧（BattleInfo）时调用的函数，负责把整包 BattleInfo 吃掉并应用到本地状态。
        /// 这里统一吃掉：主状态同步 / HitEvent 表现 / HP&死亡真值覆写。
        /// BattleManager 只负责把整包 battleInfo 转交进来，不再外排内部顺序。
        /// </summary>
        public bool ConsumeAuthoritativeBattleUpdate(BattleInfo battleInfo)
        {
            if (battleInfo == null)
                return false;

            if (!ConsumeAuthoritativeFrameBatch(battleInfo.OperationID, battleInfo.AllPlayerOperation))
                return false;

            if (battleInfo.HitEvents != null && battleInfo.HitEvents.Count > 0)
            {
                Logging.HYLDDebug.FrameTrace($"[AHS-5] Seq: ApplyHitEvents START frame={battleInfo.OperationID} hitCount={battleInfo.HitEvents.Count}");
                //这个在HitEvent
                ApplyHitEvents(battleInfo.HitEvents);
                Logging.HYLDDebug.FrameTrace($"[AHS-5] Seq: ApplyHitEvents END frame={battleInfo.OperationID}");
            }

            Logging.HYLDDebug.FrameTrace($"[AHS-5] Seq: ApplyAuthHpDeath START frame={battleInfo.OperationID}");
            //这个在HitEvent
            ApplyAuthoritativeHpAndDeath(battleInfo.AllPlayerOperation, battleInfo.OperationID);
            Logging.HYLDDebug.FrameTrace($"[AHS-5] Seq: ApplyAuthHpDeath END frame={battleInfo.OperationID}");
            return true;
        }

        /// <summary>
        /// 接收并处理一个或多个服务端下发的权威帧（Client-Side Prediction 核心逻辑）。
        /// 步骤：
        /// 1. 判断是否过期帧。
        /// 2. 应用权威帧中的物理位置。
        /// 3. 应用权威帧中的玩家动画状态。
        /// 4. 同步帧号并清理已确认的本地预测历史。
        /// 5. 根据权威帧的位置，重新重放尚未被服务端确认的本地预测输入（Replay）。
        /// 6. 生成服务端确认的其他玩家或自己的子弹（视觉子弹）。
        /// </summary>
        /// <param name="authorityFrameId">当前批次的最新服务端帧号</param>
        /// <param name="authorityFrames">当前批次包含的所有服务端操作集合</param>
        /// <returns>如果批次有效并被处理返回 true，过时则返回 false</returns>
        public bool ConsumeAuthoritativeFrameBatch(int authorityFrameId,
            Google.Protobuf.Collections.RepeatedField<AllPlayerOperation> authorityFrames)
        {
            //判断是否应该拒绝这个权威帧批次（如果 authorityFrameId 已经过时了），如果过时了就直接丢弃不处理
            if (ShouldRejectAuthorityFrameBatch(authorityFrameId))
                return false;

            Logging.HYLDDebug.FrameLog_BeginFrame(authorityFrameId);

            int authorityBatchCount = authorityFrames.Count;
            int localSyncBeforeReconcile = sync_frameID;
            LogAuthorityFrameBatchBegin(authorityFrameId, authorityBatchCount);

            //应用权威状态并重放，把 authorityFrameId 之前的预测历史都重放一遍（如果有开启预测的话），
            //使本地状态和 authorityFrameId 帧的权威状态对齐
            ApplyAuthoritativeStateAndReplay(authorityFrameId, authorityFrames, authorityBatchCount, out AllPlayerOperation latestAuthorityFrame);
            SpawnVisualBulletsFromAuthorityFrames(authorityFrames, localSyncBeforeReconcile);

            int authorityOpCount = latestAuthorityFrame != null ? latestAuthorityFrame.Operations.Count : 0;
            LogAuthorityFrameBatchEnd(authorityFrameId);
            Logging.HYLDDebug.FrameLog_EndFrame(sync_frameID, predicted_frameID, authorityOpCount, PredictionHistoryCount);

            return true;
        }

        /// <summary>
        /// 判断是否应该拒绝此权威帧批次。
        /// 只有当传来的 authorityFrameId 小于本地已经同步过的 sync_frameID 时才拒绝（防止网络乱序导致旧包覆盖新状态）。
        /// </summary>
        private bool ShouldRejectAuthorityFrameBatch(int authorityFrameId)
        {
            return sync_frameID > authorityFrameId;
        }

        /// <summary>
        /// 核心：应用权威状态，确认攻击，并执行重放（Replay）。
        /// 这个函数实现了 Client-Side Prediction (CSP) 的主流程：
        /// 覆盖权威位置 -> 确认攻击移除缓存 -> 裁剪旧预测历史 -> 重放未确认输入
        /// </summary>
        private void ApplyAuthoritativeStateAndReplay(int authorityFrameId, 
            Google.Protobuf.Collections.RepeatedField<AllPlayerOperation> authorityFrames, 
            int authorityBatchCount, out AllPlayerOperation latestAuthorityFrame)
        {
            ApplyAuthoritativePositions(authorityFrames);
            UpdateAnimationStateFromAuthority(authorityFrames);

            sync_frameID = authorityFrameId;
            //“拿服务器最新确认的帧号（authorityFrameId）去对比一下我本地的预测帧号（predicted_frameID）。
            //如果发现我本地跑得还不如服务器跑得快（或者我因为掉线卡住了），那我就把我自己内部的 predicted_frameID 强制拔高、对齐到服务器的帧号。
            AlignPredictedFrameWithAuthority(authorityFrameId);

            // 当前批次默认以最后一帧作为“最新权威帧”。
            // 本文件里的位置校正和动画更新也都基于 authorityFrames[authorityFrames.Count - 1]，
            // 这里保持同一语义，不再额外按 frameId 再搜索一次。
            latestAuthorityFrame = authorityFrames != null && authorityFrames.Count > 0
                ? authorityFrames[authorityFrames.Count - 1]
                : null;

            RecordAuthorityConfirmation(authorityFrameId, authorityFrameId, authorityBatchCount, latestAuthorityFrame);

            //RecordAuthorityConfirmation
            TrimPredictionHistoryThroughFrame(authorityFrameId);

            if (IsPredictionEnabled)
            {
                //和解重放
                ReplayUnconfirmedInputs(authorityFrameId);
            }

            //兜个底：如果 authorityFrames 里没有找到 authorityFrameId 对应的帧数据（理论上不应该发生），
            //也要记录一下权威确认，避免丢帧后预测历史无限积压
            RecordAuthoritySnapshotForFrame(authorityFrameId);

        }

        // ═══════ 权威位置校正 + 重放（CSP 模式，11.1-11.2） ═══════

        /// <summary>
        /// 11.1: 从权威帧批次中取最后一帧的 player_states，将所有玩家逻辑位置设为权威位置。
        /// 对本地玩家额外记录 lastAuthorityPosition 作为重放起点。
        /// </summary>
        private void ApplyAuthoritativePositions(Google.Protobuf.Collections.RepeatedField<AllPlayerOperation> frames)
        {
            if (frames == null || frames.Count == 0) return;

            // 取批次中最后一帧的 player_states
            AllPlayerOperation lastFrame = frames[frames.Count - 1];
            var playerStates = lastFrame.PlayerStates;

            if (playerStates == null || playerStates.Count == 0) return;

            int selfPlayerIndex = -1;
            int stateCount = playerStates.Count;

            // 判断本地客户端是否需要翻转坐标：
            // 客户端总是把"自己"放在 X=15 侧（myteam 队列）。
            // 服务端 team1(teamId较小) 在 X=15，team2 在 X=-15。
            // 所以 team1 客户端不需要翻转，team2 客户端需要对所有坐标取反。
            // 判定方法：如果 P0 的 teamID != 本地玩家的 teamID，说明本地是 team2（被镜像了）
            bool needFlip = HYLDStaticValue.Players.Count > 0
                && HYLDStaticValue.playerSelfIDInServer < HYLDStaticValue.Players.Count
                && HYLDStaticValue.Players[0].teamID != HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].teamID;
            float sign = needFlip ? -1f : 1f;

            for (int s = 0; s < stateCount; s++)
            {
                AuthoritativePlayerState state = playerStates[s];
                int bpId = state.BattleId;

                // 找到 battleId 对应的 playerIndex
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

                // 使用循环外计算的全局 sign（needFlip），对所有玩家统一翻转
                Vector3 authorityPos = new Vector3(state.PosX * sign, state.PosY, state.PosZ * sign);

                // 设置逻辑位置
                HYLDStaticValue.Players[playerIndex].playerPositon = authorityPos;

                // 本地玩家：记录权威位置作为重放起点
                if (bpId == battleID)
                {
                    lastAuthorityPosition = authorityPos;
                    selfPlayerIndex = playerIndex;
                }
            }

            // 验证日志（每 60 帧打印一次）
            if (lastFrame.Frameid % 60 == 0)
            {
                Vector3 localPos = selfPlayerIndex >= 0 ? HYLDStaticValue.Players[selfPlayerIndex].playerPositon : Vector3.zero;
                float delta = selfPlayerIndex >= 0 ? Vector3.Distance(localPos, lastAuthorityPosition) : 0f;
                Logging.HYLDDebug.FrameTrace($"[AuthPos] frame={lastFrame.Frameid} stateCount={stateCount} selfIdx={selfPlayerIndex} authPos=({lastAuthorityPosition.x:F2},{lastAuthorityPosition.y:F2},{lastAuthorityPosition.z:F2}) delta={delta:F3}");
            }
        }

        /// <summary>
        /// 1.5: 从权威帧批次最后一帧的操作中，更新所有玩家的动画驱动参数。
        /// 仅写 playerMoveMagnitude / playerMoveDir，不修改位置（位置由 ApplyAuthoritativePositions 处理）。
        /// 不在操作列表中的玩家清零动画参数（表示该帧没有移动输入）。
        /// </summary>
        private void UpdateAnimationStateFromAuthority(Google.Protobuf.Collections.RepeatedField<AllPlayerOperation> frames)
        {
            if (frames == null || frames.Count == 0) return;

            // 取批次最后一帧的操作列表
            AllPlayerOperation lastFrame = frames[frames.Count - 1];
            if (lastFrame.Operations == null || lastFrame.Operations.Count == 0)
            {
                // 没有操作 -> 所有玩家清零
                for (int i = 0; i < playerIndexBattleIds.Count && i < HYLDStaticValue.Players.Count; i++)
                {
                    HYLDStaticValue.Players[i].playerMoveDir = Vector3.zero;
                    HYLDStaticValue.Players[i].playerMoveMagnitude = 0f;
                }
                return;
            }

            // 队伍翻转 sign（与 HYLDPlayerManger.ApplyPlayerOperation(opt) 一致）
            int selfIdx = HYLDStaticValue.playerSelfIDInServer;

            // 记录本帧有操作的 playerIndex 集合
            HashSet<int> updatedIndices = new HashSet<int>();

            for (int i = 0; i < lastFrame.Operations.Count; i++)
            {
                PlayerOperation op = lastFrame.Operations[i];
                int bpId = op.Battleid;

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

                updatedIndices.Add(playerIndex);

                // 队伍翻转（与 HYLDPlayerManger.ApplyPlayerOperation(opt) 一致）
                int flipSign = 1;
                if (selfIdx >= 0 && selfIdx < HYLDStaticValue.Players.Count
                    && HYLDStaticValue.Players[playerIndex].teamID != HYLDStaticValue.Players[selfIdx].teamID)
                {
                    flipSign = -1;
                }

                float mx = flipSign * op.PlayerMoveX;
                float mz = flipSign * op.PlayerMoveY;

                LZJ.Fixed3 tempDir = new LZJ.Fixed3(-mx, 0f, mz);
                LZJ.Fixed tempMagnitude = tempDir.magnitude;

                if (tempMagnitude.ToFloat() < 0.001f)
                {
                    HYLDStaticValue.Players[playerIndex].playerMoveDir = Vector3.zero;
                    HYLDStaticValue.Players[playerIndex].playerMoveMagnitude = 0f;
                }
                else
                {
                    HYLDStaticValue.Players[playerIndex].playerMoveDir = tempDir.ToVector3();
                    HYLDStaticValue.Players[playerIndex].playerMoveMagnitude = tempMagnitude.ToFloat();
                }
            }

            // 不在操作列表中的玩家清零（该帧没有移动输入）
            for (int i = 0; i < playerIndexBattleIds.Count && i < HYLDStaticValue.Players.Count; i++)
            {
                if (!updatedIndices.Contains(i))
                {
                    HYLDStaticValue.Players[i].playerMoveDir = Vector3.zero;
                    HYLDStaticValue.Players[i].playerMoveMagnitude = 0f;
                }
            }
        }

        private void SpawnVisualBulletsFromAuthorityFrames(Google.Protobuf.Collections.RepeatedField<AllPlayerOperation> authorityFrames, int localSyncBeforeReconcile)
        {
            for (int i = 0; i < authorityFrames.Count; i++)
            {
                int frameId = authorityFrames[i].Frameid;
                // 跳过本地已经处理过的旧权威帧
                if (frameId <= localSyncBeforeReconcile)
                    continue;
                foreach (var playerOp in authorityFrames[i].Operations)
                {
                    if (playerOp.AttackOperations == null || playerOp.AttackOperations.Count == 0)
                        continue;

                    Vector3 spawnPos = Vector3.zero;
                    int ownerTeamId = 0;
                    int playerIndex = -1;

                    // 尝试从权威快照中获取生成子弹时的权威位置（延迟补偿前的默认位置）
                    TryGetAuthorityPosition(frameId, playerOp.Battleid, out spawnPos);

                    for (int p = 0; p < HYLDStaticValue.Players.Count; p++)
                    {
                        if (p < playerIndexBattleIds.Count && playerIndexBattleIds[p] == playerOp.Battleid)
                        {
                            ownerTeamId = HYLDStaticValue.Players[p].teamID;
                            playerIndex = p;
                            break;
                        }
                    }

                    foreach (var attack in playerOp.AttackOperations)
                    {
                        // 路径B（权威帧子弹）：如果这是本地玩家自己的攻击，并且已经在本地预测过了，则跳过，避免重复生成两波子弹
                        if (playerOp.Battleid == battleID && _predictedBulletAttackIds.Contains(attack.AttackId))
                        {
                            Logging.HYLDDebug.FrameTrace($"[PredBullet] SKIP_AUTH attackId={attack.AttackId} reason=already-predicted");
                            _predictedBulletAttackIds.Remove(attack.AttackId);
                            continue;
                        }

                        int bulletSign = ownerTeamId != teamID ? -1 : 1;
                        Vector3 dir = LZJ.MathFixed.xAndY2UnitVector3(attack.Towardy, attack.Towardx);
                        dir.x *= -1 * bulletSign;
                        dir.z *= bulletSign;

                        Vector3 bulletSpawnPos = spawnPos;

                        // 延迟补偿V2: 如果服务端发来了具体的 SpawnPos（服务端追帧计算后的实际开火点），就使用服务端下发的坐标
                        if (attack.SpawnPosX != 0f || attack.SpawnPosY != 0f || attack.SpawnPosZ != 0f)
                        {
                            bool needFlip = HYLDStaticValue.Players.Count > 0
                                && HYLDStaticValue.playerSelfIDInServer < HYLDStaticValue.Players.Count
                                && HYLDStaticValue.Players[0].teamID 
                                != HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].teamID;
                            float flipSign = needFlip ? -1f : 1f;
                            // 翻转坐标，因为客户端的敌我视角是镜像的
                            bulletSpawnPos = new Vector3(attack.SpawnPosX * flipSign, attack.SpawnPosY, attack.SpawnPosZ * flipSign);
                        }

                        // 客户端半空推进（Lag Compensation Visual）：计算子弹应该飞了多远，并直接将其推移到那个位置
                        int elapsedFrames = attack.ClientFrameId > 0 ? (frameId - attack.ClientFrameId) : 0;
                        if (elapsedFrames > 0 && playerIndex >= 0 && playerIndex < HYLDStaticValue.Players.Count)
                        {
                            float bulletSpeed = HYLDStaticValue.Players[playerIndex].hero.speed;
                            float advance = elapsedFrames * bulletSpeed * Server.NetConfigValue.frameTime;
                            bulletSpawnPos += dir.normalized * advance;
                            Logging.HYLDDebug.FrameTrace($"[LagComp][Visual] attackId={attack.AttackId} elapsed={elapsedFrames} advance={advance:F2} spawnPos=({bulletSpawnPos.x:F2},{bulletSpawnPos.z:F2})");
                        }

                        // 实际生成纯表现层子弹
                        if (playerIndex >= 0 && BattleManger.Instance != null && BattleManger.Instance.bulletManger != null)
                        {
                            BattleManger.Instance.bulletManger.SpawnVisualBullet(playerIndex, bulletSpawnPos, dir, FireState.PstolNormal);
                        }
                    }
                }
            }
        }


        private void LogAuthorityFrameBatchBegin(int authorityFrameId, int authorityBatchCount)
        {
            if (authorityFrameId % 60 == 0)
            {
                Logging.HYLDDebug.FrameTrace($"[SyncCheck] IN serverFrame={authorityFrameId} localSync={sync_frameID} predicted={predicted_frameID} batchSize={authorityBatchCount} historyCount={PredictionHistoryCount}");
            }
        }

        private void LogAuthorityFrameBatchEnd(int authorityFrameId)
        {
            if (authorityFrameId % 60 == 0)
            {
                Logging.HYLDDebug.FrameTrace($"[SyncCheck] OUT syncAfter={sync_frameID} predicted={predicted_frameID} historyRemaining={PredictionHistoryCount} localPos=({(HYLDStaticValue.Players.Count > HYLDStaticValue.playerSelfIDInServer ? HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].playerPositon.x.ToString("F2") : "?")}," +
                    $"{(HYLDStaticValue.Players.Count > HYLDStaticValue.playerSelfIDInServer ? HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].playerPositon.z.ToString("F2") : "?")})");
            }
        }
    }
}
