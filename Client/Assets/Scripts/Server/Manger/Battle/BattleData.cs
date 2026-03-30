/****************************************************
    BattleData.cs  --  partial class: 核心数据 / 单例 / 初始化 / 帧号 / 清理
    从 BattleManger.cs 拆分，零逻辑变更
*****************************************************/

using System.Collections.Generic;
using UnityEngine;
using System;
using SocketProto;

namespace Manger
{
    public partial class BattleData
    {
        private static BattleData instance;

        // ═══════ 内部数据类型 ═══════
        //记录客户端已经本地执行过、但还未得到服务端确认的预测帧数据。
        public class PredictedFrameHistoryEntry
        {
            public int FrameId;
            public PlayerOperation Input;
        }

        //记录客户端收到的、已经被服务端确认过的每一帧快照元数据。
        public class AuthorityConfirmedFrameEntry
        {
            public int FrameId;
            public int ServerOperationId;
            public int AuthorityBatchCount;
            public int AuthorityOperationCount;
            public bool HasSelfAuthorityInput;
            public PlayerOperation SelfAuthorityInput;
        }

        // ═══════ 核心集合 ═══════

        private readonly LinkedList<PredictedFrameHistoryEntry> predictionHistory = new LinkedList<PredictedFrameHistoryEntry>();
        private readonly Dictionary<int, LinkedListNode<PredictedFrameHistoryEntry>> predictionHistoryIndex = new Dictionary<int, LinkedListNode<PredictedFrameHistoryEntry>>();
        private readonly List<int> playerIndexBattleIds = new List<int>();

        // ═══════ 权威位置校正（CSP 模式） ═══════
        private Vector3 lastAuthorityPosition;

        /// <summary>
        /// [AHS-4] 每个玩家的 HP 上限（首次从服务端 PlayerStates.Hp 初始化）。
        /// key = playerIndex, value = maxHp。用于血条比例计算。
        /// </summary>
        private readonly Dictionary<int, int> _playerMaxHp = new Dictionary<int, int>();

        /// <summary>
        /// [AHS-2] 已消费的最高权威 HP 帧号。
        /// UDP 包乱序到达时，跳过帧号 <= 此值的旧批次，防止 HP 回弹。
        /// </summary>
        private int _lastAuthHpFrameId = 0;

        public volatile float cachedMainThreadTime;

        // ═══════ 帧号 / 战斗 ID ═══════



        public int battleID { get; private set; }
        public int teamID { get; private set; }
        public PlayerOperation selfOperation;
        public List<BattlePlayerPack> list_battleUsers { get; private set; }
        public int sync_frameID { get; private set; }
        public int predicted_frameID { get; private set; }

        private BattleData()
        {
        }

        public int randSeed { get; private set; }

        public static BattleData Instance
        {
            get
            {
                if (instance == null)
                {
                    instance = new BattleData();
                }
                return instance;
            }
        }

        public int GetFrameDataNum
        {
            get
            {
                return sync_frameID;
            }
        }



        public bool IsPredictionEnabled
        {
            get { return HYLDStaticValue.isNet && Server.NetConfigValue.EnablePredictionReconciliationPipeline; }
        }

        public int PredictionHistoryCount
        {
            get { return predictionHistory.Count; }
        }

        public int NextPredictedFrameId
        {
            get { return predicted_frameID + 1; }
        }

        // ═══════ 帧号管理 ═══════

        public void CommitPredictedFrame(int frameId)
        {
            if (frameId > predicted_frameID)
            {
                predicted_frameID = frameId;
            }
        }

        public void AlignPredictedFrameWithAuthority(int authorityFrameId)
        {
            if (predicted_frameID < authorityFrameId)
            {
                predicted_frameID = authorityFrameId;
            }
        }

        // ═══════ 初始化 / 清理 ═══════

        public void ClearPredictionRuntimeState()
        {
            int syncBefore = sync_frameID;
            int predictedBefore = predicted_frameID;
            int historyBefore = predictionHistory.Count;
            int battleIdMapBefore = playerIndexBattleIds.Count;

            sync_frameID = 0;
            predicted_frameID = 0;
            predictionHistory.Clear();
            predictionHistoryIndex.Clear();
            playerIndexBattleIds.Clear();
            lastAuthorityPosition = Vector3.zero;
            authoritySnapshotHistory.Clear();
            ClearPendingAttacks();
            ClearHitEventState();
            _predictedBulletAttackIds.Clear();
            _hitAnimatedPlayers.Clear();
            _playerMaxHp.Clear();
            _lastAuthHpFrameId = 0;
            // -- RTT 状态清理 --
            smoothedRTT = 0f;
            rttVariance = 0f;
            _rttInitialized = false;
            _lastPingTime = 0f;
            _lastAcceptedPongTimestamp = 0;

            Logging.HYLDDebug.FrameTrace($"[AHS-5] Cleanup: hitAnimatedPlayers.Clear() maxHp reset lastAuthHpFrameId=0 rtt reset");
            Logging.HYLDDebug.FrameTrace($"[RuntimeStateCleared] syncBefore={syncBefore} predictedBefore={predictedBefore} historyBefore={historyBefore} battleIdMapBefore={battleIdMapBefore} syncNow={sync_frameID} predictedNow={predicted_frameID}");
        }

        public void InitBattleInfo(int _randSeed, Google.Protobuf.Collections.RepeatedField<BattlePlayerPack> battleUsersInfo)
        {
            Logging.HYLDDebug.Log("InitBattleInfo  初始化战场信息 " + Time.realtimeSinceStartup);
            ClearPredictionRuntimeState();
            list_battleUsers = new List<BattlePlayerPack>();
            randSeed = _randSeed;
            foreach (var user in battleUsersInfo)
            {
                list_battleUsers.Add(user);
                playerIndexBattleIds.Add(user.Battleid);
                if (user.Id == HYLDStaticValue.PlayerUID)
                {
                    battleID = user.Battleid;
                    selfOperation = new PlayerOperation();
                    selfOperation.Battleid = battleID;
                    teamID = user.Teamid;
                }
            }
        }

        /// <summary>
        /// 清空玩家操作（每帧发送前调用）
        /// 注意：不清空 AttackOperations，由 FlushPendingAttacksToOperation 统一管理
        /// </summary>
        public void ResetOperation()
        {
            selfOperation.PlayerMoveX = 0;
            selfOperation.PlayerMoveY = 0;
        }

        private PlayerOperation ClonePlayerOperation(PlayerOperation operation)
        {
            PlayerOperation copy = new PlayerOperation();
            copy.Battleid = operation.Battleid;
            copy.PlayerMoveX = operation.PlayerMoveX;
            copy.PlayerMoveY = operation.PlayerMoveY;
            foreach (var attackOp in operation.AttackOperations)
            {
                AttackOperation cloned = new AttackOperation();
                cloned.AttackId = attackOp.AttackId;
                cloned.Towardx = attackOp.Towardx;
                cloned.Towardy = attackOp.Towardy;
                copy.AttackOperations.Add(cloned);
            }
            return copy;
        }

    }
}
