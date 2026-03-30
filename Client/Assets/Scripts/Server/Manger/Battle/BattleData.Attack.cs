/****************************************************
    BattleData.Attack.cs  --  partial class: 待确认攻击队列 / 预测子弹去重
    从 BattleManger.cs 拆分，零逻辑变更
*****************************************************/

using System.Collections.Generic;
using SocketProto;

namespace Manger
{
    public partial class BattleData
    {
        // =========================================================================================
        // 【模块作用】：管理客户端玩家的攻击操作，包括攻击的排队、重发、确认清理，以及本地预测子弹的去重。
        // 
        // 【交互关系】：
        // 1. 输入产生: 
        //    - `CommandManger.AddCommad_Attack` 收到玩家松开摇杆的攻击指令时，调用 `EnqueueAttack` 放入待确认队列。
        // 2. 数据发送:
        //    - `CommandManger.Execute` 每逻辑帧调用 `FlushPendingAttacksToOperation`，
        //    将所有待确认的攻击打包进发送给服务端的协议包(selfOperation)中。
        // 3. 权威确认:
        //    - `BattleData.RecordAuthorityConfirmation` 收到服务端权威帧时，调用 `ConfirmAttacks` 移除服务端已经确认收到的攻击。
        // 4. 预测表现:
        //    - `BattleManger.BattleTick` 本地预测出子弹时，调用 `MarkAttackPredicted` 记录 AttackId。
        //    - 收到权威帧时，如果发现别人发来的操作里的 AttackId 在本地已经预测过了（`IsAttackPredicted`），就跳过，防止自己发射两次子弹。
        //
        // 【核心设计原则】：
        // - 保证攻击指令一定到达服务器（丢包自动重发），直到收到服务端的确认。
        // - 客户端预测表现与服务端确认表现完美去重，不双黄蛋。
        // =========================================================================================

        // ═══════ 预测子弹去重 ═══════

        /// <summary>
        /// 已在预测路径中生成视觉子弹的 AttackId 集合。
        /// 权威帧到达时跳过本地玩家已预测的攻击，避免重复生成子弹。
        /// </summary>
        private readonly HashSet<int> _predictedBulletAttackIds = new HashSet<int>();

        public bool IsAttackPredicted(int attackId)
        {
            return _predictedBulletAttackIds.Contains(attackId);
        }

        public void MarkAttackPredicted(int attackId)
        {
            _predictedBulletAttackIds.Add(attackId);
        }

        // ═══════ 待确认攻击队列 ═══════

        private readonly List<PendingAttack> pendingAttacks = new List<PendingAttack>();
        private int nextAttackId = 0;

        public class PendingAttack
        {
            public int AttackId;
            public float Towardx;
            public float Towardy;
            /// <summary>
            /// 攻击产生时的客户端预测帧号（入队时锁定，重发时不刷新）。
            /// 供服务端 V2 延迟补偿回溯到攻击真正发生时的玩家位置。
            /// </summary>
            public int ClientFrameId;
        }

        /// <summary>
        /// 【函数作用】：将玩家最新的一次攻击操作加入待发送队列。
        /// 【核心逻辑】：
        /// 1. 分配一个递增且唯一的 AttackId。
        /// 2. 锁定并记录当前客户端的预测帧号（ClientFrameId = predicted_frameID）。
        ///    - 这是为了告诉服务器：“我是在哪一帧开的火”。
        ///    - 锁定后即使因为网络丢包导致这笔攻击重复发送，它的 ClientFrameId 永远不变。
        /// </summary>
        public PendingAttack EnqueueAttack(float towardx, float towardy)
        {
            nextAttackId++;
            PendingAttack attack = new PendingAttack()
            {
                AttackId = nextAttackId,
                Towardx = towardx,
                Towardy = towardy,
                ClientFrameId = predicted_frameID,
            };
            pendingAttacks.Add(attack);
            Logging.HYLDDebug.FrameTrace($"[AttackPipeline] EnqueueAttack id={nextAttackId} toward=({towardx:F4},{towardy:F4}) clientFrame={predicted_frameID} pendingCount={pendingAttacks.Count}");
            return attack;
        }

        /// <summary>
        /// 【函数作用】：在每次准备通过 UDP 向服务端发送操作前调用，将所有还没被服务端确认的攻击全打包发走。
        /// 【核心逻辑】：
        /// 1. 超时清理：如果某个攻击在客户端队列里卡了太久（当前帧号 - 它的原始帧号 > MaxClientAttackAge(10帧)），
        ///    说明网络极其卡顿或者服务端没收到且服务端已经不可能再接受这么老的攻击了（防作弊/防超大延迟补偿）。
        ///    直接把它从队列里删掉，省得占用带宽。
        /// 2. 组装数据：把队列里剩下的所有 PendingAttack 打包到 selfOperation.AttackOperations 列表中。
        ///    重点是把一开始锁定的 `ClientFrameId` 传给服务器，供服务端的 V2 延迟补偿系统使用。
        /// </summary>
        public void FlushPendingAttacksToOperation()
        {
            // 超时清理：移除帧龄超过阈值的攻击（服务端也会 REJECT，这里减少无效重发带宽）
            // 阈值与服务端 MaxAcceptableAttackDelay=10 对齐
            const int MaxClientAttackAge = 10;
            int removedCount = pendingAttacks.RemoveAll(a =>
                a.ClientFrameId > 0 && (predicted_frameID - a.ClientFrameId) > MaxClientAttackAge);
            if (removedCount > 0)
            {
                Logging.HYLDDebug.FrameTrace($"[AttackPipeline] TimeoutCleanup removed={removedCount} currentFrame={predicted_frameID} remaining={pendingAttacks.Count}");
            }

            selfOperation.AttackOperations.Clear();
            for (int i = 0; i < pendingAttacks.Count; i++)
            {
                AttackOperation attackOp = new AttackOperation();
                attackOp.AttackId = pendingAttacks[i].AttackId;
                attackOp.Towardx = pendingAttacks[i].Towardx;
                attackOp.Towardy = pendingAttacks[i].Towardy;
                // 6.1: 填充客户端预测帧号，供服务端 V2 延迟补偿使用
                // 使用入队时锁定的帧号（重发时不刷新，避免丢包恢复后 predicted_frameID 跳跃导致 delay<0）
                attackOp.ClientFrameId = pendingAttacks[i].ClientFrameId;
                selfOperation.AttackOperations.Add(attackOp);
            }
            if (pendingAttacks.Count > 0)
            {
                Logging.HYLDDebug.FrameTrace($"[AttackPipeline] Flush pendingCount={pendingAttacks.Count} -> selfOp.AttackOps={selfOperation.AttackOperations.Count}");
            }
        }

        /// <summary>
        /// 【函数作用】：当收到服务端的权威帧后调用，用来清理已经被服务端确认收到的攻击。
        /// 【核心逻辑】：
        /// 1. 遍历权威帧中属于自己（本地玩家）的操作列表。
        /// 2. 找到服务端确认过的最大 `AttackId`（maxConfirmedId）。
        /// 3. 把本地 pendingAttacks 队列里所有 <= maxConfirmedId 的攻击全部移除（因为服务端的确认是累进的）。
        /// 4. 顺便把本地预测子弹的防重名集合 `_predictedBulletAttackIds` 中老的 AttackId 也清掉，防止内存泄露。
        /// </summary>
        public void ConfirmAttacks(PlayerOperation authorityOperation)
        {
            if (authorityOperation == null || authorityOperation.AttackOperations == null || authorityOperation.AttackOperations.Count == 0)
            {
                return;
            }
            int maxConfirmedId = 0;
            for (int i = 0; i < authorityOperation.AttackOperations.Count; i++)
            {
                if (authorityOperation.AttackOperations[i].AttackId > maxConfirmedId)
                {
                    maxConfirmedId = authorityOperation.AttackOperations[i].AttackId;
                }
            }
            pendingAttacks.RemoveAll(a => a.AttackId <= maxConfirmedId);
            _predictedBulletAttackIds.RemoveWhere(attackId => attackId <= maxConfirmedId);
        }

        public void ClearPendingAttacks()
        {
            pendingAttacks.Clear();
            nextAttackId = 0;
        }
    }
}
