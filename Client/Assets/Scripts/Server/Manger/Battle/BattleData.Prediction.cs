/****************************************************
    BattleData.Prediction.cs  --  partial class: 预测历史 / 权威确认 / 输入重放
    从 BattleManger.cs 拆分，零逻辑变更
*****************************************************/

using System.Collections.Generic;
using UnityEngine;
using SocketProto;

namespace Manger
{
    public partial class BattleData
    {
        /****************************************************
    BattleData.Prediction.cs  --  partial class: 预测历史 / 权威确认 / 输入重放
    
    【类的整体作用（CSP核心）】
    这个分片类是 Client-Side Prediction (客户端预测) 和 Reconciliation (和解/回滚重算) 的心脏。
    它负责管理“本地超前预测带来的偏差与纠正”，保证本地画面流畅，同时防作弊。

    它的核心工作流是：
    1. 【记录】本地每跑一帧，就把自己键盘按了啥记到账本(predictionHistory)里。（调用方：BattleManger 或 HYLDPlayerManger）
    2. 【核对】网线传来服务器权威大包，对比账本，看哪些操作服务器已经收到了。（与 BattleData.Authority.cs 交互）
    3. 【撕账本】把服务器已经算过的历史记录从账本里撕掉，扔进垃圾桶。
    4. 【时光倒流与重演】先退回到服务器发来的过去时空，把账本里剩下的（服务器还没算）的未来操作拿出来，在1毫秒内重新模拟一遍，覆盖此时画面上人物的位置。
    5. 【场景拍照】为了给子弹做延迟补偿渲染，将和解后的瞬间拍个快照存下来。

*****************************************************/
        /// </summary>
        public void RecordPredictedHistory(int frameId, PlayerOperation input)
        {
            if (!IsPredictionEnabled)
            {
                return;
            }

            PredictedFrameHistoryEntry entry = new PredictedFrameHistoryEntry()
            {
                FrameId = frameId,
                Input = ClonePlayerOperation(input),
            };

            if (predictionHistoryIndex.TryGetValue(frameId, out LinkedListNode<PredictedFrameHistoryEntry> existingNode))
            {
                existingNode.Value = entry;
                return;
            }

            LinkedListNode<PredictedFrameHistoryEntry> node = predictionHistory.AddLast(entry);
            predictionHistoryIndex[frameId] = node;
            TrimPredictionHistory();
        }

        /// <summary>
        /// 【保障机制】限制预测流水账本的容量。
        /// 如果账本条数超过规定长度（PredictionHistoryWindowSize，通常设为 60 或几百），就开始把最老的第一条强行丢掉。
        /// 这种情况通常发生在玩家断网卡主，本地一直在“盲走”，导致迟迟收不到服务器确认包来消耗链表。
        /// </summary>
        private void TrimPredictionHistory()
        {
            while (predictionHistory.Count > Server.NetConfigValue.PredictionHistoryWindowSize)
            {
                LinkedListNode<PredictedFrameHistoryEntry> oldest = predictionHistory.First;
                if (oldest == null)
                {
                    break;
                }
                predictionHistory.RemoveFirst();
                predictionHistoryIndex.Remove(oldest.Value.FrameId);
            }
        }

        /// <summary>
        /// 从当前下发的权威帧中，找到属于本地玩家（自己）的操作，
        /// 然后调用 ConfirmAttacks，把那些“服务端已经收到并确认”的攻击请求从本地待发送队列（pendingAttacks）中删掉。
        /// 为什么只确认最新帧：因为服务端的攻击确认机制是基于"最大已处理AttackId"（MaxConfirmedId）的批量确认。
        /// 只要最新帧里包含了你的攻击操作，说明这个AttackId以及之前的攻击服务端都收到了，直接移除本地缓存即可。
        /// </summary>
        private void RecordAuthorityConfirmation(int serverFrameId, int serverOperationId, int authorityBatchCount, AllPlayerOperation authorityFrame)
        {
            // 只做攻击确认，不再做输入匹配判定
            PlayerOperation selfAuthorityOperation = null;
            bool HasSelfAuthorityInput = authorityFrame != null && TryFindSelfAuthorityOperation(authorityFrame, out selfAuthorityOperation);
            if (HasSelfAuthorityInput)
            {
                // 权威帧确认了这些攻击，从待确认队列移除
                ConfirmAttacks(selfAuthorityOperation);
            }

            Logging.HYLDDebug.FrameTrace($"[AuthorityTrack][Confirm] frame={serverFrameId} serverOp={serverOperationId} batchCount={authorityBatchCount} hasSelfAuth={HasSelfAuthorityInput}");
        }

        /// <summary>
        /// 工具函数：拿到某一帧全场所有人的操作操作后，把主角自己的那一份抽离出来。
        /// 比如场上十个人推摇杆，我只找 battleID 跟我匹配的那一个。
        private bool TryFindSelfAuthorityOperation(AllPlayerOperation authorityFrame, out PlayerOperation selfAuthorityOperation)
        {
            for (int i = 0; i < authorityFrame.Operations.Count; i++)
            {
                if (authorityFrame.Operations[i].Battleid == battleID)
                {
                    selfAuthorityOperation = authorityFrame.Operations[i];
                    return true;
                }
            }
            selfAuthorityOperation = null;
            return false;
        }

        /// 【第三步：撕账本】 清除已经被服务器消化完的历史记录。

        private void TrimPredictionHistoryThroughFrame(int frameId)
        {
            while (predictionHistory.First != null && predictionHistory.First.Value.FrameId <= frameId)
            {
                LinkedListNode<PredictedFrameHistoryEntry> oldest = predictionHistory.First;
                predictionHistory.RemoveFirst();
                predictionHistoryIndex.Remove(oldest.Value.FrameId);
            }
        }

        // ═══════ 权威快照历史（供视觉子弹生成位置查询） ═══════
        // 数据结构：帧号 -> (玩家BattleId -> 该帧下的实际正确3D位置)  (这是一个嵌套字典)
        private readonly Dictionary<int, Dictionary<int, Vector3>> authoritySnapshotHistory = new Dictionary<int, Dictionary<int, Vector3>>();

        /// <summary>
        /// 【第五步：场景拍照留档】
        /// 每次处理完一波权威包并让所有人就位后，按一下快门，记录全场最新确认帧（frameId）里，大家真真实实踩在地上的坐标。
        /// 用处：如果后面有人开枪了，子弹的子弹管理器 (HYLDBulletManager) 会拿着过去的开火帧号来这儿查那个人的原始起跳坐标，用于画纯展现/视觉效果的假子弹。
        /// </summary>
        public void RecordAuthoritySnapshotForFrame(int frameId)
        {
            // 生成当前所有玩家 ID 对应 位置的 映射。
            Dictionary<int, Vector3> positions = new Dictionary<int, Vector3>(playerIndexBattleIds.Count);
            for (int i = 0; i < HYLDStaticValue.Players.Count && i < playerIndexBattleIds.Count; i++)
            {
                positions[playerIndexBattleIds[i]] = HYLDStaticValue.Players[i].playerPositon;
            }
            
            // 放入总史册大字典中
            authoritySnapshotHistory[frameId] = positions;

            // 定期做清查（只保留近期的照片），防止内存无限爆炸。
            // 只保留当前帧往回数 PredictionHistoryWindowSize 范围内的记录
            List<int> toRemove = null;
            foreach (var kvp in authoritySnapshotHistory)
            {
                if (kvp.Key < frameId - Server.NetConfigValue.PredictionHistoryWindowSize)
                {
                    if (toRemove == null) toRemove = new List<int>();
                    toRemove.Add(kvp.Key);
                }
            }
            if (toRemove != null)
            {
                for (int i = 0; i < toRemove.Count; i++)
                {
                    authoritySnapshotHistory.Remove(toRemove[i]);
                }
            }
        }

        /// <summary>
        /// 提供给外部（例如表现层 BulletManger 等）专门查询旧快照的接口。
        /// 外部查：第 1500 帧时，局内编号为 2 的那个敌人站在哪个坐标？
        /// </summary>
        public bool TryGetAuthorityPosition(int frameId, int battleId, out Vector3 position)
        {
            if (authoritySnapshotHistory.TryGetValue(frameId, out Dictionary<int, Vector3> positions))
            {
                return positions.TryGetValue(battleId, out position);
            }
            position = Vector3.zero;
            return false;
        }

        /// <summary>
        /// 【函数作用】：核心 CSP 和解逻辑 —— 收到服务端发来的确切位置后，将期间尚未被服务端确认的“未来操作”快速重做一遍。
        /// 【核心逻辑】：
        /// 1. 此时玩家的位置已经被强行拉回到了服务端发来的 `lastAuthorityPosition`（过去的真实位置）。
        /// 2. 遍历预测历史记录，挑出属于 `权威帧号+1` 到 `当前预测帧号` 之间的所有输入。
        /// 3. 拿这些输入，用服务端的物理公式（极简版：dir * 速度 * deltaTime）重新计算一遍，在瞬间把玩家推到最终应该在的位置。
        /// 4. 这也是为什么网络不好时人会瞬移/拉扯的原因——服务端说你在A，客户端算了半天发现你在B，最后强行切到了B。
        /// </summary>
        private void ReplayUnconfirmedInputs(int authorityFrameId)
        {
            // 找到本地玩家的 playerIndex
            int selfPlayerIndex = -1;
            for (int p = 0; p < playerIndexBattleIds.Count; p++)
            {
                if (playerIndexBattleIds[p] == battleID)
                {
                    selfPlayerIndex = p;
                    break;
                }
            }
            if (selfPlayerIndex < 0 || selfPlayerIndex >= HYLDStaticValue.Players.Count) return;

            // 以权威位置为起点
            Vector3 pos = lastAuthorityPosition;
            int replayedCount = 0;

            // 遍历预测历史，重放 authorityFrameId+1 到 predicted_frameID 的输入
            LinkedListNode<PredictedFrameHistoryEntry> node = predictionHistory.First;
            while (node != null)
            {
                PredictedFrameHistoryEntry entry = node.Value;
                if (entry.FrameId > authorityFrameId && entry.FrameId <= predicted_frameID)
                {
                    // 取本地输入中的移动分量
                    float mx = entry.Input.PlayerMoveX;
                    float mz = entry.Input.PlayerMoveY;

                    // 移动公式与 HYLDPlayerManger.ApplyPlayerOperation 一致：
                    // dir = (-moveX, 0, moveY)  -> 经过 Fixed3 -> magnitude -> move = dir * 移动速度 * frameTime
                    LZJ.Fixed3 tempDir = new LZJ.Fixed3(-mx, 0f, mz);
                    LZJ.Fixed3 move = tempDir * HYLDStaticValue.Players[selfPlayerIndex].移动速度 * Server.NetConfigValue.frameTime;
                    pos = (new LZJ.Fixed3(pos) + move).ToVector3();
                    replayedCount++;
                }
                node = node.Next;
            }

            // 将重放后的位置写回
            HYLDStaticValue.Players[selfPlayerIndex].playerPositon = pos;

            // 验证日志（每 60 帧打印一次）
            if (authorityFrameId % 60 == 0)
            {
                Logging.HYLDDebug.FrameTrace($"[Replay] authorityFrame={authorityFrameId} predicted={predicted_frameID} replayedCount={replayedCount} startPos=({lastAuthorityPosition.x:F2},{lastAuthorityPosition.y:F2},{lastAuthorityPosition.z:F2}) endPos=({pos.x:F2},{pos.y:F2},{pos.z:F2})");
            }
        }
    }
}
