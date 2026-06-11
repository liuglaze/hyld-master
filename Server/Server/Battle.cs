using Server.Controller;
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
		private const float FrameTimeSec = 16 / 1000f;  // = ServerConfig.frameTime / 1000f = 0.016f

		private sealed class LastProcessedMoveInput
		{
			public int MoveFrame;
			public float MoveX;
			public float MoveY;
			public MoveType MoveType;
		}

		private sealed class PendingMoveSegment
		{
			public int MoveFrame;
			public int RemainingFrames;
			public float MoveX;
			public float MoveY;
			public MoveType MoveType;
			public ServerVector3 PredictedPosition;
		}

		// 战斗状态
		private int playerCount;
		private int frameid;
		private Dictionary<int, BattleFrame> dic_historyFrames;
		private Dictionary<int, PlayerFrameInput> dic_pendingAttacks;
		private Dictionary<int, int> dic_playerAckedFrameId;
		private Dictionary<int, int> dic_lastProcessedAttackId;
		private Dictionary<int, bool> dic_playerGameOver;
		// ── CMC-style Move Timeline（客户端 SavedMove 时间轴） ──
		private const int MaxClientMoveFrameLead = 2;
		private const int MaxMoveApplyFramesPerServerFrame = 3;
		private const float MoveCorrectionThreshold = 0.6f;
		private Dictionary<int, int> dic_lastAcceptedMoveFrame;
		private Dictionary<int, int> dic_lastSimulatedMoveFrame;
		private Dictionary<int, Queue<PendingMoveSegment>> dic_pendingMoveSegments;
		private Dictionary<int, LastProcessedMoveInput> dic_lastProcessedMoveInput;
		private Dictionary<int, MoveAckResult> dic_lastMoveAck;
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
		private const float MoveSpeed = 3.9f;
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
		private int _killerBattlePlayerId;
		private int _killerTeamId;

		// 出生点规则
		private static readonly float[] SpawnZ = { -5f, 0f, 5f };

		// ---- 网络模拟（测试用，发布前设为 0） ----
		private const float SimDropRate = 0.3f;
		private const int SimDelayMinMs = 50;
		private const int SimDelayMaxMs = 75;
		private const int MaxAcceptableAttackDelay = 8;
		private const int AckGapRepeatThreshold = 3;
		private const int CurrentFrameRepeatSendCount = 3;
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
						LZJUDP.ApplyBattleNetSimConfig(SimDropRate, SimDelayMinMs, SimDelayMaxMs);
						Logging.Debug.Log($"[NetSim] PREPARE battleId={battleId} dropRate={SimDropRate} delayMs={SimDelayMinMs}~{SimDelayMaxMs} (BattleStart enters unified NetSim)");
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
			}
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
				dic_playerAckedFrameId = new Dictionary<int, int>();
				dic_playerGameOver = new Dictionary<int, bool>();
				dic_lastProcessedAttackId = new Dictionary<int, int>();
				// ── CMC-style Move Timeline 初始化 ──
				dic_lastAcceptedMoveFrame = new Dictionary<int, int>();
				dic_lastSimulatedMoveFrame = new Dictionary<int, int>();
				dic_pendingMoveSegments = new Dictionary<int, Queue<PendingMoveSegment>>();
				dic_lastProcessedMoveInput = new Dictionary<int, LastProcessedMoveInput>();
				dic_lastMoveAck = new Dictionary<int, MoveAckResult>();

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
				_killerBattlePlayerId = 0;
				_killerTeamId = 0;

				// 建立 battlePlayerId -> teamId/hero 映射，初始化出生位置
				var teamGroups = new Dictionary<int, List<int>>();
				foreach (MatchUserInfo matchUser in battleContext.MatchUsers)
				{
					int bpId = uidToBattlePlayerId[matchUser.uid];
					playerTeamIds[bpId] = matchUser.teamid;
					playerHeroes[bpId] = matchUser.hero;
					playerHp[bpId] = HeroConfig.GetHp(matchUser.hero);
					playerIsDead[bpId] = false;
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
					dic_playerAckedFrameId[battlePlayerId] = 0;
					dic_playerGameOver[battlePlayerId] = false;
					dic_lastProcessedAttackId[battlePlayerId] = 0;
					dic_lastAcceptedMoveFrame[battlePlayerId] = 0;
					dic_lastSimulatedMoveFrame[battlePlayerId] = 0;
					dic_pendingMoveSegments[battlePlayerId] = new Queue<PendingMoveSegment>();
					dic_lastProcessedMoveInput[battlePlayerId] = new LastProcessedMoveInput
					{
						MoveFrame = 0,
						MoveX = 0f,
						MoveY = 0f,
						MoveType = MoveType.NewMove,
					};
				}
			}
			// ---- 网络模拟启动日志 ----
			if (SimDropRate > 0f || SimDelayMaxMs > 0)
			{
				LZJUDP.ApplyBattleNetSimConfig(SimDropRate, SimDelayMinMs, SimDelayMaxMs);
				Logging.Debug.Log($"[NetSim] ACTIVE battleId={battleId} dropRate={SimDropRate} delayMs={SimDelayMinMs}~{SimDelayMaxMs} (synced to unified battle UDP NetSim)");
			}

			Thread thread = new Thread(BattleLoop) { IsBackground = true };
			thread.Start();
		}

		// ==================== 帧循环 ====================

		private void BattleLoop()
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

		// ==================== 帧收集与广播 ====================
		// 帧号语义：
		// 1. frameid 是服务端权威帧，只由 BattleLoop 每 16ms 推进一次。
		// 2. ClientMove.MoveFrame 是客户端本地预测移动帧，只用于上行移动排序与确认。
		// 3. 合法 ClientMove 在 UDP 接收阶段只入队；BattleLoop 每帧按预算消化 pending move 并推进 playerPositions。
		// 4. 本函数组织当前 ServerFrame 的移动消化、广播、位置历史、攻击、子弹、HP 与死亡状态。
		private void CollectAndBroadcastCurrentFrame()
		{
			ApplyPendingMoveSegments();

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

			// 1. 玩家位置已在 ApplyPendingMoveSegments 中按固定预算慢消化；
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
		}

		// ==================== 服务端位置追踪 ====================

		private void UpdatePlayerPositions(BattleFrame frameOp)
		{
			foreach (PlayerFrameInput op in frameOp.PlayerInputs)
			{
				int bpId = op.BattlePlayerId;
				if (!playerPositions.TryGetValue(bpId, out ServerVector3 pos)) continue;

				float mx = op.MoveX;
				float mz = op.MoveY; // 客户端 MoveY 对应世界 Z 轴
				float len = (float)Math.Sqrt(mx * mx + mz * mz);
				if (frameid % 120 == 0)
					Logging.Debug.Log($"[MoveInput] frame={frameid} bp{bpId} mx={mx:F4} mz=" +
						$"{mz:F4} len={len:F6} pos=({pos.X:F2},{pos.Z:F2})");
				if (len > 1e-6f)
				{
					mx /= len; mz /= len;

					float teamSign = 1f;
					if (playerTeamIds.TryGetValue(bpId, out int tid) && tid != baseTeamId)
						teamSign = -1f;

					pos.X += -mx * teamSign * MoveSpeed * FrameTimeSec;
					pos.Z += mz * teamSign * MoveSpeed * FrameTimeSec;
					playerPositions[bpId] = pos;
				}
			}
		}

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
