using Server.Controller;
using PMNet.Shared;
using SocketProto;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using static Server.Controller.MatchingController;

namespace Server
{
	// ==================== BattleController ====================
	// partial class 拆分：
	//   BattleController.cs         — 字段声明 + 生命周期 + 帧循环 + 位置追踪（本文件）
	//   BattleController.Bullets.cs — 子弹生成 / 碰撞检测 / 追帧模拟 / HP 扣减
	//   BattleController.Network.cs — 操作接收 / 帧下行广播 / 战斗结束 / 网络模拟

	partial class BattleController
	{
		public int battleId { get; private set; }

		private readonly BattleContext battleContext;
		private readonly Dictionary<int, int> uidToBattlePlayerId;
		private readonly HashSet<int> disconnectedBattlePlayerIds = new HashSet<int>();
		private readonly Dictionary<int, string> battlePlayerIdToIp = new Dictionary<int, string>();
		private readonly Dictionary<int, bool> dic_battleReady = new Dictionary<int, bool>();
		private readonly object _battleLock = new object();
		private readonly int frameIntervalMs = ServerConfig.frameTime;
		private readonly int maxCatchupFrame = 5;
		private const float FrameTimeSec = BattleNumericConfig.FrameTimeSec;
		// 帧长是两端共用的时间基准，已收敛到 BattleNumericConfig.FrameTimeSec（P3'-2）。
		// 旧实现有三个独立声明点：这里（16/1000f）、ServerConfig.frameTime、客户端 ConstValue.frameTime。

		private sealed class LastProcessedMoveInput
		{
			public int MoveFrame;
			public float MoveX;
			public float MoveY;
			public MoveType MoveType;
		}

		private sealed class PendingClientMove
		{
			public int MoveFrame;
			public float MoveX;
			public float MoveY;
			public MoveType MoveType;
			public ServerVector3 PredictedPosition;
			public ServerVector3 PredictedVelocity;
		}

		private sealed class FullAuthorityState
		{
			public int BattleId;
			public float PosX;
			public float PosY;
			public float PosZ;
			public int Hp;
			public bool IsDead;
			public int Mana;
			public int SuperEnergy;

			public FullAuthorityState Clone()
			{
				return new FullAuthorityState
				{
					BattleId = BattleId,
					PosX = PosX,
					PosY = PosY,
					PosZ = PosZ,
					Hp = Hp,
					IsDead = IsDead,
					Mana = Mana,
					SuperEnergy = SuperEnergy,
				};
			}
		}

		// 战斗状态
		private int playerCount;
		private int frameid;
		private Dictionary<int, BattleFrame> dic_historyFrames;
		private Dictionary<int, PlayerFrameInput> dic_pendingAttacks;
		private Dictionary<int, List<AttackAck>> dic_pendingAttackAcks;
		private Dictionary<int, Dictionary<int, AttackAck>> dic_recentAttackAcks;
		private Dictionary<int, int> dic_playerAckedFrameId;
		private Dictionary<int, int> dic_lastProcessedAttackId;
		private Dictionary<int, bool> dic_playerGameOver;
			// ── CMC-style Move Timeline（客户端 SavedMove 时间轴） ──
			private const int MaxDeltaFramesPerMove = 8;
			private const int MinMoveQuantum = 1;
			private const float MoveDiscrepancyResolutionRate = 0.5f;
			private const float MovementMaxPositionError = 0.6f;
			private Dictionary<int, int> dic_lastReceivedMoveFrame;
			private Dictionary<int, int> dic_lastAckedMoveFrame;
			private Dictionary<int, List<PendingClientMove>> dic_pendingClientMoves;
			private Dictionary<int, LastProcessedMoveInput> dic_lastProcessedMoveInput;
			private Dictionary<int, int> dic_lastProcessedClientMoveFrame;
			private Dictionary<int, MoveAckResult> dic_lastMoveAck;
			private Dictionary<int, int> dic_moveAckCorrectionLockedServerFrame;
			private Dictionary<int, int> dic_moveFrameDiscrepancyDebt;
			private Dictionary<int, int> dic_lastServerFrameWhenProcessedMove;
			private Dictionary<int, bool> dic_isResolvingFrameDiscrepancy;
		private Dictionary<int, double> dic_moveFrameDiscrepancyResolutionCarry;
		private Dictionary<int, Dictionary<int, FullAuthorityState>> dic_authorityStateHistory;
		private Dictionary<int, Dictionary<int, FullAuthorityState>> dic_playerAcknowledgedAuthorityStates;
		private Dictionary<int, int> dic_playerAcknowledgedAuthorityStateFrame;
		private bool isAllReady;
		private bool _battleStarted;
		private bool _isRun;
		private float gameOverConfirmTimeoutMs;
		private bool hasAnyPlayerDied;
		private bool allClientsConfirmedGameOver;
		private bool isWaitingClientConfirm;
		private bool _hasEnded;

		// ---- 服务端伤害判定系统 ----
		private Dictionary<int, ServerVector3> playerPositions;
		private Dictionary<int, int> playerTeamIds;
		private Dictionary<int, Hero> playerHeroes;
		// 移速不再是一个全局常量（P3'-2）：旧服务端全职 3.9 与客户端逐英雄设计值不一致，
		// 会让权威位置与客户端预测位置持续偏差、反复触发位置校正。
		// 现在逐英雄从 BattleNumericConfig 取，见 GetMoveSpeedFor。
		private int baseTeamId;
		// 位置历史环形缓冲区（V2 延迟补偿）
		private Dictionary<int, Dictionary<int, ServerVector3>> positionHistory;
		private List<int> positionHistoryOrder;
		private const int PositionHistoryWindowSize = 30;
		// 活跃子弹列表
		private List<ServerBullet> activeBullets;
		private List<HitEvent> pendingHitEvents;
		// ---- 服务端权威 HP 系统 ----
		private Dictionary<int, int> playerHp;
		private Dictionary<int, bool> playerIsDead;
		private Dictionary<int, int> playerMana;
		private Dictionary<int, float> playerManaRegenTimerMs;
		private Dictionary<int, int> playerSuperEnergy;
		private int _killerBattlePlayerId;
		private int _killerTeamId;

		// 出生点规则
		private static readonly float[] SpawnZ = { -5f, 0f, 5f };

		// ---- 网络模拟（测试用，发布前设为 0） ----
		private const float DefaultSimDropRate = 0.1f;
		private const int DefaultSimDelayMinMs = 30;
		private const int DefaultSimDelayMaxMs = 60;
		private const int NetSimDelayLimitMs = 2000;
		private const int MaxAcceptableAttackDelay = 8;
		private const int AckGapRepeatThreshold = 3;
		private const int CurrentFrameRepeatSendCount = 3;
		private const int AuthorityStateHistoryWindowSize = 120;
		private const uint AuthorityStateMaskPosition = 1u;
		private const uint AuthorityStateMaskHp = 2u;
		private const uint AuthorityStateMaskDead = 4u;
		private const uint AuthorityStateMaskMana = 8u;
		private const uint AuthorityStateMaskSuperEnergy = 16u;
		private const uint AuthorityStateMaskAll = AuthorityStateMaskPosition | AuthorityStateMaskHp | AuthorityStateMaskDead | AuthorityStateMaskMana | AuthorityStateMaskSuperEnergy;
		private readonly Random _simRandom = new Random();

		// ==================== 构造 / 初始化 ====================

		public BattleController(Server server, BattleContext battleContext)
		{
			int randSeed = (new Random()).Next(0, 100);
			this.battleContext = battleContext;
			battleId = battleContext.BattleId;
			uidToBattlePlayerId = new Dictionary<int, int>(battleContext.UidToBattlePlayerId);
			LZJUDP.Instance.RegisterBattle(battleId, Handle);

			ThreadPool.QueueUserWorkItem((obj) =>
			{
				MainPack pack = new MainPack();
				pack.Requestcode = RequestCode.Matching;
				pack.Returncode = ReturnCode.Succeed;
				pack.Actioncode = ActionCode.StartEnterBattle;
				BattleInfo battleInfo = new BattleInfo();
				battleInfo.RandSeed = randSeed;
				playerCount = battleContext.MatchUsers.Count;

				foreach (MatchUserInfo matchUser in battleContext.MatchUsers)
				{
					int battlePlayerId = uidToBattlePlayerId[matchUser.uid];
					dic_battleReady[battlePlayerId] = false;

					BattlePlayerPack battleUser = new BattlePlayerPack();
					battleUser.Id = matchUser.uid;
					battleUser.Battleid = battlePlayerId;
					battleUser.Playername = matchUser.userName;
					battleUser.Hero = matchUser.hero;
					battleUser.Teamid = matchUser.teamid;
					battleInfo.BattleUsers.Add(battleUser);
				}

				pack.BattleInfo = battleInfo;
				Logging.Debug.Log("向客户端发送战场数据！" + pack);
				foreach (MatchUserInfo matchUser in battleContext.MatchUsers)
				{
					server.GetActiveClient(matchUser.uid)?.Send(pack);
				}
			}, null);
		}

		public bool TryGetBattlePlayerId(int uid, out int battlePlayerId)
		{
			return uidToBattlePlayerId.TryGetValue(uid, out battlePlayerId);
		}

		public bool OwnsBattlePlayerId(int battlePlayerId)
		{
			return uidToBattlePlayerId.ContainsValue(battlePlayerId);
		}

		public void HandlePlayerDisconnect(int uid)
		{
			if (!TryGetBattlePlayerId(uid, out int battlePlayerId))
			{
				Logging.Debug.Log($"[BattleDisconnect][ControllerSkip] battleId={battleId} uid={uid} reason=battle_player_not_found");
				return;
			}

			bool shouldEndBattle = false;
			lock (_battleLock)
			{
				disconnectedBattlePlayerIds.Add(battlePlayerId);
				dic_battleReady.Remove(battlePlayerId);
				battlePlayerIdToIp.Remove(battlePlayerId);

				if (dic_playerGameOver != null)
				{
					dic_playerGameOver[battlePlayerId] = true;
				}

				Logging.Debug.Log($"[BattleDisconnect][ControllerMark] battleId={battleId} uid={uid} battlePlayerId={battlePlayerId} hasEnded={_hasEnded} isRun={_isRun} readyCount={dic_battleReady.Count} endpointCount={battlePlayerIdToIp.Count}");
				if (!_hasEnded)
				{
					hasAnyPlayerDied = true;
					allClientsConfirmedGameOver = true;
					isWaitingClientConfirm = false;
					gameOverConfirmTimeoutMs = 0;
					shouldEndBattle = true;
					Logging.Debug.Log($"[BattleDisconnect][ControllerEndBattle] battleId={battleId} uid={uid} battlePlayerId={battlePlayerId} reason=client_disconnect_sets_gameover");
				}
			}

			if (shouldEndBattle)
			{
				Logging.Debug.Log($"HandlePlayerDisconnect 提前结束战斗，battleId={battleId}, uid={uid}, battlePlayerId={battlePlayerId}");
				HandleBattleEnd();
			}
		}

		// ==================== UDP 消息路由 ====================

		public void Handle(MainPack pack)
		{
			switch (pack.Actioncode)
			{
				case ActionCode.BattleReady:
					if (pack.Battleplayerpack == null || pack.Battleplayerpack.Count == 0)
					{
						return;
					}
					int readyBattlePlayerId = pack.Battleplayerpack[0].Battleid;
					if (!dic_battleReady.ContainsKey(readyBattlePlayerId) || disconnectedBattlePlayerIds.Contains(readyBattlePlayerId))
					{
						Logging.Debug.Log($"BattleReady 收到非法或已断线玩家，battleId={battleId}, battlePlayerId={readyBattlePlayerId}");
						return;
					}
					dic_battleReady[readyBattlePlayerId] = true;
					battlePlayerIdToIp[readyBattlePlayerId] = pack.Str;
					isAllReady = true;
					foreach (bool ready in dic_battleReady.Values)
					{
						isAllReady = isAllReady && ready;
					}
					if (!isAllReady)
					{
						return;
					}
					if (!_battleStarted)
					{
						_battleStarted = true;
						LZJUDP.ApplyBattleNetSimConfig(battleId, DefaultSimDropRate, DefaultSimDelayMinMs, DefaultSimDelayMaxMs);
						Logging.Debug.Log($"[NetSim] PREPARE battleId={battleId} dropRate={DefaultSimDropRate} delayMs={DefaultSimDelayMinMs}~{DefaultSimDelayMaxMs} (BattleStart enters unified NetSim)");
						BroadcastBattleStart();
						BeginBattle();
						return;
					}
					Logging.Debug.Log($"BattleReady 触发 BattleStart 补发，battleId={battleId}, battlePlayerId={readyBattlePlayerId}, endpoint={pack.Str}");
					SendBattleStart(pack.Str);
					break;

				case ActionCode.BattlePushDowmPlayerOpeartions:
					if (!isAllReady) return;
					BattleInfo battleInfo = pack.BattleInfo;
					UpdatePlayerOperation(battleInfo);
					break;

				case ActionCode.ClientSendGameOver:
					UpdatePlayerGameOver(int.Parse(pack.Str));
					break;

				case ActionCode.BattleSetNetSimConfig:
					ApplyNetSimConfigFromClient(pack.BattleNetSimConfig);
					break;
			}
		}

		private void ApplyNetSimConfigFromClient(BattleNetSimConfig config)
		{
			if (config == null || !OwnsBattlePlayerId(config.BattlePlayerId))
			{
				return;
			}

			float dropRate = Math.Max(0f, Math.Min(1f, config.DropRate));
			int delayMinMs = Math.Max(0, Math.Min(NetSimDelayLimitMs, config.DelayMinMs));
			int delayMaxMs = Math.Max(0, Math.Min(NetSimDelayLimitMs, config.DelayMaxMs));
			if (delayMinMs > delayMaxMs)
			{
				delayMinMs = delayMaxMs;
			}

			LZJUDP.ApplyBattleNetSimConfig(battleId, dropRate, delayMinMs, delayMaxMs);
			Logging.Debug.Log($"[NetSim][ClientConfig] battleId={battleId} bp={config.BattlePlayerId} dropRate={dropRate} delayMs={delayMinMs}~{delayMaxMs}");
		}

		private void BroadcastBattleStart()
		{
			foreach (var item in battlePlayerIdToIp)
			{
				SendBattleStart(item.Value);
			}
		}
		private void SendBattleStart(string endpoint)
		{
			if (string.IsNullOrEmpty(endpoint))
			{
				return;
			}

			MainPack packStart = new MainPack();
			packStart.Requestcode = RequestCode.Battle;
			packStart.Actioncode = ActionCode.BattleStart;
			packStart.Str = "1";
			LZJUDP.Instance.Send(packStart, endpoint);
		}

		// ==================== 战斗开始 ====================

		private void BeginBattle()
		{
			lock (_battleLock)
			{
				frameid = 1;
				_isRun = true;
				hasAnyPlayerDied = false;
				allClientsConfirmedGameOver = false;
				isWaitingClientConfirm = false;
				gameOverConfirmTimeoutMs = 0;
				_hasEnded = false;
				dic_historyFrames = new Dictionary<int, BattleFrame>();
				dic_pendingAttacks = new Dictionary<int, PlayerFrameInput>();
				dic_pendingAttackAcks = new Dictionary<int, List<AttackAck>>();
				dic_recentAttackAcks = new Dictionary<int, Dictionary<int, AttackAck>>();
				dic_playerAckedFrameId = new Dictionary<int, int>();
				dic_playerGameOver = new Dictionary<int, bool>();
				dic_lastProcessedAttackId = new Dictionary<int, int>();
				// ── CMC-style Move Timeline 初始化 ──
				dic_lastReceivedMoveFrame = new Dictionary<int, int>();
				dic_lastAckedMoveFrame = new Dictionary<int, int>();
				dic_pendingClientMoves = new Dictionary<int, List<PendingClientMove>>();
				dic_lastProcessedMoveInput = new Dictionary<int, LastProcessedMoveInput>();
				dic_lastProcessedClientMoveFrame = new Dictionary<int, int>();
				dic_lastMoveAck = new Dictionary<int, MoveAckResult>();
				dic_moveAckCorrectionLockedServerFrame = new Dictionary<int, int>();
				dic_moveFrameDiscrepancyDebt = new Dictionary<int, int>();
				dic_lastServerFrameWhenProcessedMove = new Dictionary<int, int>();
				dic_isResolvingFrameDiscrepancy = new Dictionary<int, bool>();
				dic_moveFrameDiscrepancyResolutionCarry = new Dictionary<int, double>();
				dic_authorityStateHistory = new Dictionary<int, Dictionary<int, FullAuthorityState>>();
				dic_playerAcknowledgedAuthorityStates = new Dictionary<int, Dictionary<int, FullAuthorityState>>();
				dic_playerAcknowledgedAuthorityStateFrame = new Dictionary<int, int>();

				// ---- 初始化伤害判定系统 ----
				playerPositions = new Dictionary<int, ServerVector3>();
				playerTeamIds = new Dictionary<int, int>();
				playerHeroes = new Dictionary<int, Hero>();
				positionHistory = new Dictionary<int, Dictionary<int, ServerVector3>>();
				positionHistoryOrder = new List<int>();
				activeBullets = new List<ServerBullet>();
				pendingHitEvents = new List<HitEvent>();
				playerHp = new Dictionary<int, int>();
				playerIsDead = new Dictionary<int, bool>();
				playerMana = new Dictionary<int, int>();
				playerManaRegenTimerMs = new Dictionary<int, float>();
				playerSuperEnergy = new Dictionary<int, int>();
				_killerBattlePlayerId = 0;
				_killerTeamId = 0;

				// 建立 battlePlayerId -> teamId/hero 映射，初始化出生位置
				var teamGroups = new Dictionary<int, List<int>>();
				foreach (MatchUserInfo matchUser in battleContext.MatchUsers)
				{
					int bpId = uidToBattlePlayerId[matchUser.uid];
					playerTeamIds[bpId] = matchUser.teamid;
					playerHeroes[bpId] = matchUser.hero;
					playerHp[bpId] = BattleNumericConfig.Get((int)matchUser.hero).MaxHp;
					playerIsDead[bpId] = false;
					playerMana[bpId] = BattleNumericConfig.ManaMax;
					playerManaRegenTimerMs[bpId] = 0f;
					playerSuperEnergy[bpId] = 0;
					if (!teamGroups.ContainsKey(matchUser.teamid))
						teamGroups[matchUser.teamid] = new List<int>();
					teamGroups[matchUser.teamid].Add(bpId);
				}

				// 确定每个队伍对应的 X 轴
				var sortedTeams = new List<int>(teamGroups.Keys);
				sortedTeams.Sort();
				baseTeamId = sortedTeams[0];
				float[] teamX = new float[sortedTeams.Count];
				float[] teamXFlip = { 15f, -15f };
				for (int t = 0; t < sortedTeams.Count; t++)
					teamX[t] = t < teamXFlip.Length ? teamXFlip[t] : 0f;

				for (int t = 0; t < sortedTeams.Count; t++)
				{
					int tid = sortedTeams[t];
					float x = teamX[t];
					var members = teamGroups[tid];
					for (int i = 0; i < members.Count; i++)
					{
						float z = i < SpawnZ.Length ? SpawnZ[i] : 0f;
						if (t > 0) z = -z;
						playerPositions[members[i]] = new ServerVector3(x, 1f, z);
					}
				}

				foreach (int battlePlayerId in uidToBattlePlayerId.Values)
				{
					dic_pendingAttacks[battlePlayerId] = null;
					dic_pendingAttackAcks[battlePlayerId] = new List<AttackAck>();
					dic_recentAttackAcks[battlePlayerId] = new Dictionary<int, AttackAck>();
					dic_playerAckedFrameId[battlePlayerId] = 0;
					dic_playerGameOver[battlePlayerId] = false;
					dic_lastProcessedAttackId[battlePlayerId] = 0;
					dic_lastReceivedMoveFrame[battlePlayerId] = 0;
					dic_lastAckedMoveFrame[battlePlayerId] = 0;
					dic_pendingClientMoves[battlePlayerId] = new List<PendingClientMove>();
					dic_moveFrameDiscrepancyDebt[battlePlayerId] = 0;
					dic_lastProcessedClientMoveFrame[battlePlayerId] = 0;
					dic_lastServerFrameWhenProcessedMove[battlePlayerId] = frameid - 1;
					dic_isResolvingFrameDiscrepancy[battlePlayerId] = false;
					dic_moveFrameDiscrepancyResolutionCarry[battlePlayerId] = 0d;
					dic_moveAckCorrectionLockedServerFrame[battlePlayerId] = 0;
					dic_lastProcessedMoveInput[battlePlayerId] = new LastProcessedMoveInput
					{
						MoveFrame = 0,
						MoveX = 0f,
						MoveY = 0f,
						MoveType = MoveType.NewMove,
					};
					dic_playerAcknowledgedAuthorityStates[battlePlayerId] = null;
					dic_playerAcknowledgedAuthorityStateFrame[battlePlayerId] = 0;
				}
			}
			// ---- 网络模拟启动日志 ----
			if (DefaultSimDropRate > 0f || DefaultSimDelayMaxMs > 0)
			{
				LZJUDP.ApplyBattleNetSimConfig(battleId, DefaultSimDropRate, DefaultSimDelayMinMs, DefaultSimDelayMaxMs);
				Logging.Debug.Log($"[NetSim] ACTIVE battleId={battleId} dropRate={DefaultSimDropRate} delayMs={DefaultSimDelayMinMs}~{DefaultSimDelayMaxMs} (synced to unified battle UDP NetSim)");
			}

			Thread thread = new Thread(BattleLoop) { IsBackground = true };
			thread.Start();
		}

		// ==================== 帧循环 ====================

		private void BattleLoop()
		{
			try
			{
				Stopwatch sw = new Stopwatch();
				sw.Start();
				long lastTick = sw.ElapsedMilliseconds;
				double accum = 0;

				while (_isRun)
				{
					long now = sw.ElapsedMilliseconds;
					long dt = now - lastTick;
					lastTick = now;
					accum += dt;

					int stepCount = 0;
					while (accum >= frameIntervalMs && stepCount < maxCatchupFrame)
					{
						bool shouldEndNow = false;
						lock (_battleLock)
						{
							if (hasAnyPlayerDied)
							{
								shouldEndNow = true;
							}
							else
							{
								CollectAndBroadcastCurrentFrame();
								frameid++;
							}
						}

						if (shouldEndNow)
						{
							HandleBattleEnd();
							return;
						}

						accum -= frameIntervalMs;
						stepCount++;
					}

					bool shouldFinishAfterWait = false;
					lock (_battleLock)
					{
						if (allClientsConfirmedGameOver && !isWaitingClientConfirm)
						{
							isWaitingClientConfirm = true;
							gameOverConfirmTimeoutMs = 1000f;
						}
						if (isWaitingClientConfirm)
						{
							gameOverConfirmTimeoutMs -= dt;
							if (gameOverConfirmTimeoutMs <= 0)
							{
								shouldFinishAfterWait = true;
							}
						}
					}

					if (shouldFinishAfterWait)
					{
						HandleBattleEnd();
						return;
					}

					Thread.Sleep(1);
				}

				sw.Stop();
			}
			catch (Exception ex)
			{
				_isRun = false;
				Logging.Debug.Log($"[BattleLoop][Fatal] battleId={battleId} frame={frameid} ex={ex}");
				Logging.Debug.FlushTrace();
			}
		}

		// ==================== 帧收集与广播 ====================
		// 帧号语义：
		// 1. frameid 是服务端权威帧，只由 BattleLoop 每 16ms 推进一次。
		// 2. ClientMove.MoveFrame 是客户端本地预测移动帧，只用于上行移动排序与确认。
		// 3. 合法 ClientMove 在 UDP 接收阶段只入队；BattleLoop 固定阶段一次性完整模拟 pending move。
		// 4. 本函数组织当前 ServerFrame 的移动模拟、广播、位置历史、攻击、子弹、HP 与死亡状态。
		private void CollectAndBroadcastCurrentFrame()
		{
			ApplyPendingClientMoves();
			RegeneratePlayerMana();

			// nextFrameOp 表示“本次服务端权威帧最终采用的所有玩家操作集合”，
			// 后续会继续用于：子弹生成/碰撞 -> PlayerStates 打包 -> 下行广播。
			BattleFrame nextFrameOp = new BattleFrame();
			try
			{
				foreach (int battlePlayerId in uidToBattlePlayerId.Values)
				{
					PlayerFrameInput frameOp = null;

					if (dic_lastProcessedMoveInput.TryGetValue(battlePlayerId, out LastProcessedMoveInput lastMove))
					{
						frameOp = new PlayerFrameInput { BattlePlayerId = battlePlayerId };
						frameOp.MoveX = lastMove.MoveX;
						frameOp.MoveY = lastMove.MoveY;
					}

                    // 攻击与移动意图解耦：
                    // pendingAttacks 在网络接收阶段完成去重/超时过滤，这里只负责把“当前仍有效”的攻击并入本帧权威操作。
					//playeroperation只是装攻击的容器，只是拿这个方便用不用另外定义别的
                    if (dic_pendingAttacks.TryGetValue(battlePlayerId, out PlayerFrameInput pendingAttackOp)
						 && pendingAttackOp != null
						 && pendingAttackOp.Attacks != null
						 && pendingAttackOp.Attacks.Count > 0)
                    {
                        if (frameOp == null)
                        {
                            frameOp = new PlayerFrameInput { BattlePlayerId = battlePlayerId };
                        }

                        foreach (var attack in pendingAttackOp.Attacks)
                        {
                            frameOp.Attacks.Add(attack);
                        }
                    }

					if (frameOp != null)
					{
						// 只有本帧最终确实产出了“可广播的该玩家操作”才加入 nextFrameOp。
						// 这里的操作可能包含：当前移动意图、攻击，或它们的组合。
						nextFrameOp.PlayerInputs.Add(frameOp);
					}
				}
			}
			catch (Exception ex)
			{
				Logging.Debug.Log(ex);
				nextFrameOp = new BattleFrame();
			}
            //上面只是组织本帧要广播/结算的操作，下面按服务端权威帧推进位置。

			// 1. 玩家位置已在 ApplyPendingClientMoves 中按 pending move 一次性完整模拟；
			// BattleLoop 后续只消费当前权威位置并组织本 ServerFrame 的广播/伤害判定。

			// 2. 创建本帧 HitEvent 列表。追帧命中和正常 Tick 命中都写入同一个列表。
			pendingHitEvents = new List<HitEvent>();

			// 3. 记录本帧权威位置，供延迟补偿子弹按历史帧回溯
			RecordPositionSnapshot(frameid);

			// 4. 处理本帧攻击，生成新的服务端子弹（攻击来自上面合并进 nextFrameOp 的 AttackOperations）
			SpawnBulletsFromOperations(nextFrameOp);

            // 5. 推进所有活跃子弹并收集本帧 HitEvent；这一步可能修改 HP / IsDead
            TickServerBullets(frameid, pendingHitEvents);

			// 6. 最后再打包 PlayerStates，保证下发的是“服务端帧移动推进 + 子弹结算”后的最终权威状态
			PackPlayerStates(nextFrameOp, frameid);

			nextFrameOp.ServerFrame = frameid;
			dic_historyFrames[frameid] = nextFrameOp;

			// 7. 向所有尚未确认 GameOver 的客户端发送当前权威帧；包内会带上本帧 HitEvent
			foreach (var item in battlePlayerIdToIp)
			{
				if (!dic_playerGameOver.TryGetValue(item.Key, out bool isGameOver) || !isGameOver)
				{
					SendUnsyncedFrames(item.Value, item.Key, pendingHitEvents);
				}
			}
			// 8. 当前帧的攻击已经被并入并广播，清空待攻击缓存，等待后续网络线程写入新攻击
			dic_pendingAttacks.Clear();
			ClearPendingAttackAcks();
		}

		private void RegeneratePlayerMana()
		{
			foreach (int battlePlayerId in uidToBattlePlayerId.Values)
			{
				if (!playerMana.TryGetValue(battlePlayerId, out int mana))
				{
					continue;
				}
				if (mana >= BattleNumericConfig.ManaMax)
				{
					playerManaRegenTimerMs[battlePlayerId] = 0f;
					continue;
				}
				if (!playerHeroes.TryGetValue(battlePlayerId, out Hero hero))
				{
					continue;
				}

				float reloadMs = BattleNumericConfig.Get((int)hero).ReloadSeconds * 1000f;
				playerManaRegenTimerMs[battlePlayerId] += frameIntervalMs;
				while (playerManaRegenTimerMs[battlePlayerId] >= reloadMs && mana < BattleNumericConfig.ManaMax)
				{
					playerManaRegenTimerMs[battlePlayerId] -= reloadMs;
					mana = Math.Min(BattleNumericConfig.ManaMax, mana + BattleNumericConfig.ManaPerSegment);
				}
				playerMana[battlePlayerId] = mana;
				if (mana >= BattleNumericConfig.ManaMax)
				{
					playerManaRegenTimerMs[battlePlayerId] = 0f;
				}
			}
		}

		private void ClearPendingAttackAcks()
		{
			if (dic_pendingAttackAcks == null)
			{
				return;
			}
			foreach (List<AttackAck> acks in dic_pendingAttackAcks.Values)
			{
				acks.Clear();
			}
		}

		// ==================== 服务端位置追踪 ====================

		/// <summary>
		/// 取某战斗内玩家的移速。
		///
		/// <para>
		/// **逐英雄**（P3'-2）。旧服务端用全局常量 3.9，而客户端预测一直是逐英雄的
		/// （黑鸦 4.2 / 麦克斯 4.08 / 柯尔特 4.05 / 里昂 3.96 / 帕姆 3.78），
		/// 于是这五名玩家的权威位置每秒会比预测多/少 0.06~0.30 单位，
		/// 约 2 秒就超过 <c>MovementMaxPositionError=0.6</c> 阈值，导致 MoveAck 持续回校正（表现为位置被“拉回”）。
		/// </para>
		///
		/// <para>
		/// 道具加成（如客户端 <c>HYLDModenProp</c> 的 +1）属于**玩家运行期状态**，不写回配置表；
		/// 将来若要支持，应在这里叠加一个 per-player 修正量，而不是改 BattleNumericConfig。
		/// </para>
		/// </summary>
		private float GetMoveSpeedFor(int battlePlayerId)
		{
			Hero hero;
			if (playerHeroes.TryGetValue(battlePlayerId, out hero))
			{
				return BattleNumericConfig.Get((int)hero).MoveSpeed;
			}

			// 拿不到英雄（玩家尚未完成建链）不是「未知英雄」，用兼底值但不污染未知英雄记录。
			return BattleNumericConfig.Fallback.MoveSpeed;
		}

		// P3'-3: 这里原本还有一个 UpdatePlayerPositions(BattleFrame)，内含**第三份**移动积分公式。
		// 它在整个仓库里零调用点（实际推进走 ApplyPendingClientMoves -> SimulateAuthoritativeMove），
		// 是一段历史遗留。既然移动公式已经单点化到 PMBattleSim，这份重复实现就没有保留价值 ——
		// 留着只会让后来的人以为它是活的。
		// 它的正常路径日志 [MoveInput] 一并消失；如需该信息，请在 SimulateAuthoritativeMove 侧加。

		// ==================== 位置历史缓冲区 ====================

		private void RecordPositionSnapshot(int frameId)
		{
			var snapshot = new Dictionary<int, ServerVector3>(playerPositions);
			positionHistory[frameId] = snapshot;
			positionHistoryOrder.Add(frameId);

			while (positionHistoryOrder.Count > PositionHistoryWindowSize)
			{
				int oldest = positionHistoryOrder[0];
				positionHistoryOrder.RemoveAt(0);
				positionHistory.Remove(oldest);
			}

			if (frameId % 60 == 0)
				Logging.Debug.Log($"[PositionHistory] frameId={frameId} window={positionHistoryOrder.Count}/{PositionHistoryWindowSize}");
		}

		public bool TryGetPositionSnapshot(int frameId, out Dictionary<int, ServerVector3> snapshot)
		{
			return positionHistory.TryGetValue(frameId, out snapshot);
		}
	}
}
