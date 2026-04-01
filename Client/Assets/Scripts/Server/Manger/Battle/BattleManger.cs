
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;

using SocketProto;


namespace Manger
{

    public class BattleManger : MonoBehaviour
    {
        public static BattleManger Instance { get; private set; }
        public bool ISNet = true;
        public HYLDPlayerManger playerManger;
        public HYLDCameraManger cameraManger;
        public HYLDBulletManger bulletManger;
        public ScenseBuildLogic ScenseBuildLogic;
        public GameObject _StartGameAni;
        public GameObject BG;
        private bool isBattleStart;
        private string Loacal_IP_port;
        public bool IsGameOver { get; private set; }
        // ── 累加器驱动（动态追帧系统 Task 5） ──
        private float _tickAccumulator;
        private float currentTickInterval = 0.016f;
        private bool _battleTickActive;
        // ── 动态追帧（Task 6） ──
        private float _actualSpeedFactor = 1.0f;
        // 客户端允许相对 targetFrame 的最大超前量；超过后不再继续生成新的 predicted tick。
        private const int MaxPredictedLeadBeyondTarget = 6;
        public Toolbox toolbox;
        public bool EnableDebugGameOverHotkey = true;
        public KeyCode DebugGameOverHotkey = KeyCode.F10;
        public bool DebugGameOverAsLose = false;
        public virtual void Init()
        {
            //1.9更新战场数据，跳转战斗场景。进入战场，
            //创建角色预制体，生成场景，生成战斗管理，
            //开启协程WaitInitData，
            //等待几个初始化脚本初始化完成。
            //开始循环发送Send_FightReady
            Instance = this;
            IsGameOver = false;
            HYLDStaticValue.isNet = ISNet;

            playerManger = gameObject.AddComponent<HYLDPlayerManger>();
            cameraManger = gameObject.AddComponent<HYLDCameraManger>();
            bulletManger = gameObject.AddComponent<HYLDBulletManger>();
            ScenseBuildLogic.InitData();
            //HYLDStaticValue.InitData();
            playerManger.InitData();
            cameraManger.InitData();
            bulletManger.InitData();

            NetGlobal.Instance.Init();
        }
        private void Start()
        {
            isBattleStart = false;
            //开启协程WaitInitData，
            Loacal_IP_port = Server.UDPSocketManger.Instance.InitSocket();
            Server.UDPSocketManger.Instance.Handle = HandleMessage;
            StartCoroutine(WaitInitData());
        }

        private void FixedUpdate()
        {
            cameraManger.OnLogicUpdate();
        }

        private void Update()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (EnableDebugGameOverHotkey && !IsGameOver && Input.GetKeyDown(DebugGameOverHotkey))
            {
                TriggerDebugGameOver();
            }
#endif

            if (!_battleTickActive || IsGameOver)
            {
                return;
            }

            // ── 管线步骤 1: 消费 UDP 队列中的权威帧 ──
            Server.UDPSocketManger.Instance.DrainAndDispatch();

            // ── 管线步骤 2: Ping 调度 ──
            float pingIntervalSec = Server.NetConfigValue.pingIntervalMs / 1000f;
            if (Time.time - Manger.BattleData.Instance._lastPingTime >= pingIntervalSec)
            {
                Manger.BattleData.Instance._lastPingTime = Time.time;
                MainPack pingPack = new MainPack();
                pingPack.Actioncode = ActionCode.Ping;
                pingPack.Timestamp = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Server.UDPSocketManger.Instance.Send(pingPack);
            }

            // ── 管线步骤 3: 目标帧号计算 + tick 调节 ──
            int targetFrame = CalcTargetFrame();
            AdjustTickInterval(targetFrame);

            // ── 管线步骤 4: 累加器循环 ──
            //如果已经超过预留的目标帧号上限，说明客户端已经超前太多了
            //但如果累加器里已经积攒了足够的时间可以推进一帧了，就先推进一帧，避免完全停掉。
            int predictedLeadBeyondTarget = Manger.BattleData.Instance.predicted_frameID - targetFrame;
            if (predictedLeadBeyondTarget > MaxPredictedLeadBeyondTarget)
            {
                if (_tickAccumulator > currentTickInterval)
                {
                    _tickAccumulator = currentTickInterval;
                }
                Logging.HYLDDebug.FrameTrace($"[TickAdj] SOFT_CAP predicted={Manger.BattleData.Instance.predicted_frameID} target={targetFrame} sync={Manger.BattleData.Instance.sync_frameID} lead={predictedLeadBeyondTarget} cap={MaxPredictedLeadBeyondTarget}");
            }

            _tickAccumulator += Time.deltaTime;

            // 防弹簇：累加器不超过 maxCatchupPerUpdate 次 tick 的量
            float maxAccum = currentTickInterval * Server.NetConfigValue.maxCatchupPerUpdate;
            if (_tickAccumulator > maxAccum)
            {
                _tickAccumulator = maxAccum;
            }

            while (_tickAccumulator >= currentTickInterval)
            {
                BattleTick();
                _tickAccumulator -= currentTickInterval;
            }
        }

        private void TriggerDebugGameOver()
        {
            HYLDStaticValue.玩家输了吗 = DebugGameOverAsLose;
            Logging.HYLDDebug.FrameTrace($"[GameOver][DebugTrigger] hotkey={DebugGameOverHotkey} lose={DebugGameOverAsLose}");
            BeginGameOver();
            TriggerDebugDropPacketProbe();
            if (toolbox != null)
            {
                toolbox.游戏结束方法();
            }
        }

        private void TriggerDebugDropPacketProbe()
        {
            MainPack probePack = new MainPack();
            probePack.Requestcode = RequestCode.Battle;
            probePack.Actioncode = ActionCode.BattlePushDowmAllFrameOpeartions;
            Logging.HYLDDebug.FrameTrace($"[GameOver][DebugProbe] dispatch action={probePack.Actioncode} expected=DropPacket");
HandleMessage(probePack, System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        IEnumerator WaitInitData()
        {
            //等待几个初始化脚本初始化完成。
            yield return new WaitUntil(() => {
                return playerManger.initFinish && cameraManger.initFinish && 
                bulletManger.initFinish && ScenseBuildLogic.InitFinish;
            });

            //开始循环发送Send_FightReady
            this.InvokeRepeating("Send_BattleReady", 0.2f, 0.2f);
        }
        void Send_BattleReady()
        {
            // 在等待 BattleStart 阶段也需要消费 UDP 队列，
            // 否则 BattleStart 包会一直卡在队列里无人处理
            Server.UDPSocketManger.Instance.DrainAndDispatch();

            MainPack pack = new MainPack();
            pack.Requestcode = RequestCode.Battle;
            pack.Actioncode = ActionCode.BattleReady;
            BattlePlayerPack playerPack = new BattlePlayerPack();
            playerPack.Battleid = BattleData.Instance.battleID;
            playerPack.Id = HYLDStaticValue.PlayerUID;
            pack.Battleplayerpack.Add(playerPack);
            pack.Str = Loacal_IP_port.ToString();
            Server.UDPSocketManger.Instance.Send(pack);
        }

        // ═══════ 动态追帧（Task 6） ═══════

        /// <summary>
        /// 6.2: 计算客户端应达到的目标帧号。
        /// 公式: targetFrame = sync_frameID + ceil(rttFrames/2) + inputBufferSize + jitterBufferFrames
        /// RTT 未初始化时使用默认超前量 inputBufferSize。
        /// 在高抖动/高丢包下，为 RTT 方差增加一个有限的抖动缓冲，避免 target 偏低导致频繁“超前暂停”。
        /// </summary>
        private int CalcTargetFrame()
        {
            int syncFrame = Manger.BattleData.Instance.sync_frameID;
            int inputBuf = Server.NetConfigValue.inputBufferSize;

            if (!Manger.BattleData.Instance.IsRttInitialized)
            {
                // RTT 未初始化，用默认超前量
                return syncFrame + inputBuf;
            }
            
            float rttMs = Manger.BattleData.Instance.smoothedRTT;
            float varianceMs = Manger.BattleData.Instance.rttVariance;
            float frameTimeMs = Server.NetConfigValue.frameTime * 1000f; // 16ms
            float rttFrames = rttMs / frameTimeMs;
            int halfRttFrames = Mathf.CeilToInt(rttFrames / 2f);
            int jitterBufferFrames = Mathf.Clamp(
                Mathf.CeilToInt((varianceMs / frameTimeMs) * Server.NetConfigValue.jitterBufferRatio),
                0,
                Server.NetConfigValue.maxJitterBufferFrames);

            return syncFrame + halfRttFrames + inputBuf + jitterBufferFrames;
        }

        /// <summary>
        /// 6.3+6.4: 根据当前帧号与目标帧号的差值动态调节 tick 间隔。
        /// frameDiff > 0 → 客户端落后，加速（speedFactor > 1）
        /// frameDiff < 0 → 客户端超前，减速（speedFactor < 1）
        /// frameDiff < -severeLeadPauseFrames → 严重超前时不再硬暂停，而是压到最低速度持续推进，避免输入上报断流。
        /// </summary>
        private void AdjustTickInterval(int targetFrame)
        {
            int predictedFrame = Manger.BattleData.Instance.predicted_frameID;
            int frameDiff = targetFrame - predictedFrame;

            // 6.4: 严重超前时不再直接暂停，否则 BattleTick / 发包也会一起停掉。
            float targetSpeedFactor;
            if (frameDiff < -Server.NetConfigValue.severeLeadPauseFrames)
            {
                targetSpeedFactor = Server.NetConfigValue.minSpeedFactor;
                if (_tickAccumulator > currentTickInterval)
                {
                    _tickAccumulator = currentTickInterval;
                }
                Logging.HYLDDebug.FrameTrace($"[TickAdj] SLOW frameDiff={frameDiff} " +
                    $"target={targetFrame} predicted={predictedFrame} speed={targetSpeedFactor:F2}");
            }
            else
            {
                targetSpeedFactor = Mathf.Clamp(
                    1f + frameDiff * Server.NetConfigValue.adjustRate,
                    Server.NetConfigValue.minSpeedFactor,
                    Server.NetConfigValue.maxSpeedFactor);
            }

            // Lerp 平滑过渡
            _actualSpeedFactor = Mathf.Lerp(
                _actualSpeedFactor,
                targetSpeedFactor,
                Server.NetConfigValue.smoothRate * Time.deltaTime);

            // 输出 tick 间隔
            currentTickInterval = Server.NetConfigValue.frameTime / _actualSpeedFactor;
        }

        /// <summary>
        /// 纯逻辑帧推进 + 发包。由 Update 累加器循环调用，不依赖 Time.deltaTime。
        /// BattleTick 的职责不是“收权威帧”，而是“基于当前本地状态推进 1 帧预测，并把这一帧输入上报给服务端”。
        /// </summary>
        void BattleTick()
        {
            // 记录主线程时间，供其他只允许在主线程访问的逻辑读取（避免子线程直接碰 Unity 时间 API）。
            Manger.BattleData.Instance.cachedMainThreadTime = Time.time;

            bool predictionPipelineEnabled = Manger.BattleData.Instance.IsPredictionEnabled;
            int nextFrame = predictionPipelineEnabled
                // 开预测时，推进的是“下一个预测帧”。
                ? Manger.BattleData.Instance.NextPredictedFrameId
                // 不开预测时，退化为“权威同步帧 + 1”。
                : Manger.BattleData.Instance.sync_frameID + 1;

            // ★ 开帧：给这一帧的本地推进打日志锚点。
            Logging.HYLDDebug.FrameLog_BeginFrame(nextFrame);

            // 上报帧号直接等于本次要推进的逻辑帧号。
            // 这样动态追帧时，即使 predicted_frameID 暂时跑在服务端前面，服务端也能把输入放进对应 frame slot。
            int uploadOperationId = nextFrame;

            // “先把本帧待发包里的移动槽位清零，但保留待确认攻击队列。
            //到时候从commandmanager里读新的
            Manger.BattleData.Instance.ResetOperation();
            // 再从 CommandManger 把“这一发送帧”要上传的移动/攻击写回 selfOperation。
            // 注意：selfOperation 是客户端本地即将上报给服务端的 PlayerOperation，不是服务端回填对象。
            // 其中移动量直接写入 selfOperation；攻击则先进入 pendingAttacks，
            // 再由 FlushPendingAttacksToOperation 刷进 selfOperation.AttackOperations。
            CommandManger.Instance.Execute();

            // ★ 本地预测子弹：只要本帧 selfOperation 里有攻击，就立刻先出表现，不等待服务端回包。
            // 注意：这里的 selfOperation.AttackOperations 并不等于“仅本帧新攻击”，
            // 而是“这一发送帧准备上报的全部待确认攻击”——既可能包含本帧新攻击，也可能包含前几帧未确认而重发的旧攻击。
            if (Manger.BattleData.Instance.selfOperation.AttackOperations.Count > 0)
            {
                int selfIdx = HYLDStaticValue.playerSelfIDInServer;
                // 只有本地玩家索引有效且角色未死亡，才允许生成预测视觉子弹。
                if (selfIdx >= 0 && selfIdx < HYLDStaticValue.Players.Count && HYLDStaticValue.Players[selfIdx].isNotDie)
                {
                    Vector3 selfPos = HYLDStaticValue.Players[selfIdx].playerPositon;
                    foreach (var attackOp in Manger.BattleData.Instance.selfOperation.AttackOperations)
                    {
                        // 预测视觉子弹只能对同一个 AttackId 生成一次。
                        // 如果这次只是 pendingAttacks 因为还没被权威帧 Confirm 而再次刷进 selfOperation 的“重发攻击”，这里必须跳过。
                        if (Manger.BattleData.Instance.IsAttackPredicted(attackOp.AttackId))
                            continue;

                        // 协议里保存的是二维朝向分量，这里还原成世界空间方向。
                        // 本地视角下 x 需要取反，和当前战斗坐标系保持一致。
                        Vector3 dir = LZJ.MathFixed.xAndY2UnitVector3(attackOp.Towardy, attackOp.Towardx);
                        dir.x *= -1;

                        // 这里只生成视觉子弹；真正命中与伤害仍以服务端权威结果为准。
                        bulletManger.SpawnVisualBullet(selfIdx, selfPos, dir, FireState.PstolNormal);
                        // 标记该 AttackId 已做过预测，后续重发或权威帧回放时用于去重。
                        Manger.BattleData.Instance.MarkAttackPredicted(attackOp.AttackId);
                        Logging.HYLDDebug.FrameTrace($"[PredBullet]" +
                            $" SPAWN attackId={attackOp.AttackId} pos=({selfPos.x:F2},{selfPos.z:F2}) dir=({dir.x:F2},{dir.z:F2})");
                    }
                }
            }

            if (predictionPipelineEnabled)
            {
                // 先记录“推进前快照 + 本帧输入”，这样后续收到权威帧后才能做裁剪/重放。
                Manger.BattleData.Instance.RecordPredictedHistory(nextFrame, Manger.BattleData.Instance.selfOperation);
                // 当前本地预测路径只消费自己的 selfOperation，不再额外包一层单元素操作列表。
                ApplyLocalPredictedInputAndRefreshAllObjects(Manger.BattleData.Instance.selfOperation);
                // 推进完成后提交 predicted_frameID，表示客户端已经走到了 nextFrame。
                Manger.BattleData.Instance.CommitPredictedFrame(nextFrame);
            }

            // 子模式逐逻辑帧扩展点：挂在当前真实 BattleTick 主链路上，不再依赖失效的 ApplyFullFrame 链。
            OnBattleLogicTick(nextFrame);

            // ★ 输入摘要：记录“这一帧到底上传了什么输入”。
            Logging.HYLDDebug.FrameLog_Authority(
                $"input upload={uploadOperationId} sync={Manger.BattleData.Instance.sync_frameID} " +
                $"move=({Manger.BattleData.Instance.selfOperation.PlayerMoveX},{Manger.BattleData.Instance.selfOperation.PlayerMoveY}) " +
                $"attackCount={Manger.BattleData.Instance.selfOperation.AttackOperations.Count} " +
                $"predict={predictionPipelineEnabled}");

            // 把本帧输入发给服务端；服务端稍后会在对应权威帧里消费并回传结果。
            Server.UDPSocketManger.Instance.SendOperation(uploadOperationId);

            // ★ 结帧：这里只能输出“本地推进结束”的状态。
            // 此时还没收到新的权威帧，因此 opCount 仍然填 0，真正的权威结果要等 HandleMessage 路径更新。
            Logging.HYLDDebug.FrameLog_EndFrame(
                Manger.BattleData.Instance.sync_frameID,
                Manger.BattleData.Instance.predicted_frameID,
                0, // opCount：此时还没收到权威帧
                Manger.BattleData.Instance.PredictionHistoryCount);
        }


        /// <summary>
        /// 子模式逐逻辑帧扩展点。默认无行为；像宝石模式这类玩法可在这里挂接自己的逐帧逻辑。
        /// </summary>
        protected virtual void OnBattleLogicTick(int frameid)
        {

        }
        /// <summary>
        /// 本地预测路径：只消费自己的 selfOperation，把本地输入应用到逻辑状态。
        /// 这里虽然不推进远端玩家输入，但会把当前全体玩家逻辑状态统一刷新到场景对象。
        /// 缺失移动清零由 ApplyPlayerOperation 内部的零输入分支负责，这里不再额外补 clear。
        /// </summary>
        protected void ApplyLocalPredictedInputAndRefreshAllObjects(PlayerOperation selfOperation)
        {
            playerManger.ApplyPlayerOperation(selfOperation);
            playerManger.UpdateAllPlayerLogics();
            LogPlayerStates();
            bulletManger.OnLogicUpdate();
        }

        private void LogPlayerStates()
        {
            // ★ 玩家状态 → playerstate.log（替代原来每帧的坐标大段日志）
            for (int i = 0; i < HYLDStaticValue.Players.Count; i++)
            {
                var p = HYLDStaticValue.Players[i];
                bool flip = HYLDStaticValue.Players[0].teamID != HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].teamID;
                float sx = flip ? -1f : 1f;
                float sz = flip ? -1f : 1f;
                Logging.HYLDDebug.FrameLog_PlayerState(
                    $"P{i}({p.playerName}) pos=({p.playerPositon.x * sx:F2},{p.playerPositon.z * sz:F2}) " +
                    $"fire={p.fireState} toward=({p.fireTowards.x * sx:F2},{p.fireTowards.y:F2},{p.fireTowards.z * sz:F2})");
            }
        }


        private void HandleMessage(MainPack pack, long receivedAtMs)
        {
            if (TryHandlePong(pack, receivedAtMs))
            {
                return;
            }

            switch (pack.Requestcode)
            {
                case RequestCode.Battle:
                    HandleBattleMessage(pack);
                    break;
            }
        }

        private bool TryHandlePong(MainPack pack, long receivedAtMs)
        {
            // Pong 不走 Battle 分发链，直接用于 RTT 采样。
            if (pack.Actioncode != ActionCode.Pong)
            {
                return false;
            }

            BattleData.Instance.ProcessPongRttSample(pack.Timestamp, receivedAtMs);
            return true;
        }

        private void HandleBattleMessage(MainPack pack)
        {
            if (IsGameOver && pack.Actioncode != ActionCode.BattlePushDowmGameOver)
            {
                Logging.HYLDDebug.FrameTrace($"[GameOver][DropPacket] action={pack.Actioncode} reason=already-game-over");
                return;
            }

            switch (pack.Actioncode)
            {
                case ActionCode.BattleStart:
                    HandleBattleStart(pack);
                    break;
                case ActionCode.BattlePushDowmAllFrameOpeartions:
                    HandleAuthorityFrames(pack);
                    break;
                case ActionCode.BattlePushDowmGameOver:
                    HandleServerGameOver(pack);
                    break;
            }
        }

        private void HandleBattleStart(MainPack pack)
        {
            Logging.HYLDDebug.LogError("BattleStatr:  " + pack);
            if (isBattleStart)
            {
                return;
            }

            isBattleStart = true;
            this.CancelInvoke("Send_BattleReady");
            // BattleStart 只负责打开本地逻辑帧推进；真正的首帧权威状态稍后由权威帧分支消费。
            _tickAccumulator = 0f;
            currentTickInterval = Server.NetConfigValue.frameTime;
            _battleTickActive = true;
            StartCoroutine(WaitForFirstMessage());
        }

        private void HandleAuthorityFrames(MainPack pack)
        {
            Logging.HYLDDebug.FrameTrace($"[Baseline][AuthorityFrameRecv] serverFrame={pack.BattleInfo.OperationID} batchCount={pack.BattleInfo.AllPlayerOperation.Count} localSyncBefore={BattleData.Instance.sync_frameID}");

            // BattleManager 在这里不再编排“主状态 / HitEvent / HPDeath”的内部顺序，
            // 而是把整包 BattleInfo 直接交给 BattleData 统一消费。
            if (!BattleData.Instance.ConsumeAuthoritativeBattleUpdate(pack.BattleInfo))
            {
                // 返回 false 通常表示收到旧批次/乱序批次，当前包被帧序保护跳过。
                Logging.HYLDDebug.FrameTrace("好像数据包寄了？");
            }
        }

        private void HandleServerGameOver(MainPack pack)
        {
            // D8: 服务端下发赢家 teamId，客户端对比自己的 teamId 判断胜负。
            int winnerTeamId = 0;
            if (!string.IsNullOrEmpty(pack.Str))
            {
                int.TryParse(pack.Str, out winnerTeamId);
            }
            HYLDStaticValue.玩家输了吗 = (winnerTeamId != BattleData.Instance.teamID);
            Logging.HYLDDebug.FrameTrace($"[GameOver][ServerAuth] winnerTeamId={winnerTeamId} myTeamId={BattleData.Instance.teamID} iLost={HYLDStaticValue.玩家输了吗}");
            BeginGameOver(false);
            toolbox.游戏结束方法();
        }

        IEnumerator WaitForFirstMessage()
        {
            yield return new WaitUntil(() => {
                return BattleData.Instance.GetFrameDataNum > 0; // 在这里等待第一帧，第一帧没更新之前不会做更新。
            });
            //1.18收到第一帧，正式开始游戏 播放开场动画
            NetGlobal.Instance.AddAction(() => { BG.SetActive(false); _StartGameAni.SetActive(true); });
        }
        public void BeginGameOver()
        {
            BeginGameOver(true);
        }

        private void BeginGameOver(bool shouldNotifyServer)
        {
            if (IsGameOver)
            {
                return;
            }

            IsGameOver = true;
            Logging.HYLDDebug.FrameTrace($"[GameOver] begin frameSync={BattleData.Instance.sync_frameID} predictedFrame={BattleData.Instance.predicted_frameID} historyCount={BattleData.Instance.PredictionHistoryCount} notifyServer={shouldNotifyServer}");
            // ── Task 5+6: 停止累加器 + 追帧状态清理 ──
            _battleTickActive = false;
            _tickAccumulator = 0f;
            currentTickInterval = Server.NetConfigValue.frameTime;
            _actualSpeedFactor = 1.0f;
            this.CancelInvoke("Send_BattleReady");
            this.CancelInvoke("SendGameOver");
            Logging.HYLDDebug.FrameTrace($"[GameOver][StopSend] sendOperation=false sendBattleReady=false sendGameOver=false");
            BattleData.Instance.ClearPredictionRuntimeState();
            Logging.HYLDDebug.FrameTrace($"[GameOver][AfterClear] frameSync={BattleData.Instance.sync_frameID} predictedFrame={BattleData.Instance.predicted_frameID} historyCount={BattleData.Instance.PredictionHistoryCount}");
            if (shouldNotifyServer)
            {
                this.InvokeRepeating("SendGameOver", 0f, 0.5f);
            }
        }
        private void SendGameOver()
        {
            MainPack pack = new MainPack();
            pack.Requestcode = RequestCode.Battle;
            pack.Actioncode = ActionCode.ClientSendGameOver;
            pack.Str = BattleData.Instance.battleID.ToString();
            Server.UDPSocketManger.Instance.Send(pack);
        }

        private void OnDestroy()
        {
            // ── Task 5: 停止累加器 ──
            _battleTickActive = false;
            this.CancelInvoke("Send_BattleReady");
            this.CancelInvoke("SendGameOver");
            BattleData.Instance.ClearPredictionRuntimeState();
        }
    }

    // BattleData 已拆分为 partial class，见 BattleData.*.cs 文件

    public class HYLDRandom
    {
        static ulong next = 1;
        public static void srand(ulong seed)
        {
            next = seed;
        }
        public static int Range()
        {
            next = next * 1103515245 + 12345;
            return (int)((next / 65536) % 1000);
        }
    }

        
}