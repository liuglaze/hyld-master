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

		private sealed class BufferedMoveInput
		{
			public int SyncFrameId;
			public float MoveX;
			public float MoveY;
			public int ReceivedServerFrame;
		}

		// 战斗状态
		private int playerCount;
		private int frameid;
		private Dictionary<int, AllPlayerOperation> dic_historyFrames;
		private Dictionary<int, PlayerOperation> dic_pendingAttacks;
		private Dictionary<int, int> dic_playerAckedFrameId;
		private Dictionary<int, int> dic_lastProcessedAttackId;
		private Dictionary<int, bool> dic_playerGameOver;
		// ── Input Buffer（动态追帧系统） ──
		private const int InputBufferSize = 4;
		private const int InputFutureLeadTolerance = 6;
		private Dictionary<int, List<BufferedMoveInput>> dic_movementInputBuffer;
		private Dictionary<int, int> dic_lastConsumedMoveFrame;
		private Dictionary<int, (float moveX, float moveY)> dic_lastValidMove;
		private Dictionary<int, int> dic_consecutiveMissedFrames;
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
		private const float SimDropRate = 0.1f;
		private const int SimDelayMinMs = 25;
		private const int SimDelayMaxMs = 30;
		private const int MaxAcceptableAttackDelay = 8;
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
					battleInfo.BattleUserInfo.Add(battleUser);
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

				if (!_hasEnded)
				{
					hasAnyPlayerDied = true;
					allClientsConfirmedGameOver = true;
					isWaitingClientConfirm = false;
					gameOverConfirmTimeoutMs = 0;
					shouldEndBattle = true;
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
				dic_historyFrames = new Dictionary<int, AllPlayerOperation>();
				dic_pendingAttacks = new Dictionary<int, PlayerOperation>();
				dic_playerAckedFrameId = new Dictionary<int, int>();
				dic_playerGameOver = new Dictionary<int, bool>();
				dic_lastProcessedAttackId = new Dictionary<int, int>();
				// ── Input Buffer 初始化 ──
				dic_movementInputBuffer = new Dictionary<int, List<BufferedMoveInput>>();
				dic_lastConsumedMoveFrame = new Dictionary<int, int>();
				dic_lastValidMove = new Dictionary<int, (float moveX, float moveY)>();
				dic_consecutiveMissedFrames = new Dictionary<int, int>();

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
					dic_movementInputBuffer[battlePlayerId] = new List<BufferedMoveInput>(InputBufferSize);
					dic_lastConsumedMoveFrame[battlePlayerId] = 0;
					dic_lastValidMove[battlePlayerId] = (0f, 0f);
					dic_consecutiveMissedFrames[battlePlayerId] = 0;
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
		// 当前语义（2026-03）：
		// 1. 移动输入不再按旧的环形 slot / frame%buffer 直接消费。
		// 2. 现在每个玩家维护一个按 SyncFrameId 升序的滑动窗口（dic_movementInputBuffer）。
		// 3. 每个服务端帧优先消费 lastConsumedMoveFrame + 1；若该帧缺失，则接受窗口内“最小的更大合法帧”。
		// 4. 一旦接受更大的输入帧，说明服务端消费进度已经前推，之后迟到的旧帧会在接收阶段被拒绝（REJECT_STALE）。
		// 5. 如果当前没有合法新输入，则沿用 lastValidMove 做短时惯性，但不推进 lastConsumedMoveFrame。
		// 6. 攻击不走该移动窗口，而是独立缓存在 dic_pendingAttacks 中，并在这里合并进当前权威帧。
		private void CollectAndBroadcastCurrentFrame()
		{
			// nextFrameOp 表示“本次服务端权威帧最终采用的所有玩家操作集合”，
			// 后续会继续用于：位置推进 -> 子弹生成/碰撞 -> PlayerStates 打包 -> 下行广播。
			AllPlayerOperation nextFrameOp = new AllPlayerOperation();
			try
			{
				foreach (int battlePlayerId in uidToBattlePlayerId.Values)
				{
					// frameOp 是“当前服务端帧最终决定采用”的这个玩家操作：
					// - 可能来自新消费到的移动输入
					// - 也可能只有惯性移动
					// - 也可能本来无移动，但后面会因攻击而被创建
					PlayerOperation frameOp = null;

					// ── 按序消费移动输入：优先 nextExpected，缺失则跳到最小更大合法帧 ──
					if (dic_movementInputBuffer.TryGetValue(battlePlayerId, out List<BufferedMoveInput> pendingInputs))
					{
						// chosenInput = 本帧真正要消费的输入；chosenIndex 用于后续把“已被越过/已被消费”的旧输入一起删掉。
						BufferedMoveInput chosenInput = null;
						int chosenIndex = -1;
						int lastConsumedMoveFrame = dic_lastConsumedMoveFrame.TryGetValue(battlePlayerId, out int consumedMoveFrame)
							? consumedMoveFrame
							: 0;
						int nextExpectedFrame = lastConsumedMoveFrame + 1;
						int maxAllowedFrame = frameid + InputFutureLeadTolerance;

						// 扫描顺序依赖 pendingInputs 已按 SyncFrameId 升序排列：
						// - 优先找严格下一帧 nextExpectedFrame
						// - 若找不到，则保留第一个“比 lastConsumed 更大且不超前过多”的候选
						for (int i = 0; i < pendingInputs.Count; i++)
						{
							BufferedMoveInput candidate = pendingInputs[i];
							if (candidate.SyncFrameId <= lastConsumedMoveFrame)
							{
								// 理论上旧输入大多会在接收阶段被拒绝；这里再次防守式跳过，避免消费倒退。
								continue;
							}
							if (candidate.SyncFrameId > maxAllowedFrame)
							{
								// 列表已升序；后面的帧只会更大，因此可以直接停止扫描。
								break;
							}
							if (candidate.SyncFrameId == nextExpectedFrame)
							{
								chosenInput = candidate;
								chosenIndex = i;
								break;
							}
							if (chosenInput == null)
							{
								chosenInput = candidate;
								chosenIndex = i;
							}
						}

						if (chosenInput != null)
						{
							// 命中新输入：
							// 1) 用该输入构造本帧移动
							// 2) 更新 lastValidMove，供后续缺帧时做惯性补偿
							// 3) 推进 lastConsumedMoveFrame，表示服务端已正式前进到 chosenInput.SyncFrameId
							frameOp = new PlayerOperation { Battleid = battlePlayerId };
							frameOp.PlayerMoveX = chosenInput.MoveX;
							frameOp.PlayerMoveY = chosenInput.MoveY;
							dic_consecutiveMissedFrames[battlePlayerId] = 0;
							dic_lastValidMove[battlePlayerId] = (frameOp.PlayerMoveX, frameOp.PlayerMoveY);
							dic_lastConsumedMoveFrame[battlePlayerId] = chosenInput.SyncFrameId;

							if (chosenInput.SyncFrameId == nextExpectedFrame)
							{
								Logging.Debug.Log($"[MoveBuffer][ACCEPT_IN_ORDER] bp={battlePlayerId} " +
									$"frame={frameid} consumed={chosenInput.SyncFrameId} " +
									$"nextExpected={nextExpectedFrame} pending={pendingInputs.Count}");
							}
							else
							{
								Logging.Debug.Log($"[MoveBuffer][SKIP_GAP_ACCEPT] bp={battlePlayerId} " +
									$"frame={frameid} consumed={chosenInput.SyncFrameId} expected={nextExpectedFrame} " +
									$"pending={pendingInputs.Count}");
							}

							if (chosenIndex >= 0)
							{
								// 这里会把“被真正消费的输入”以及它之前所有更老输入一起删掉。
								// 含义：一旦服务端前进到更大的 SyncFrameId，旧帧就失去回灌意义，不允许再倒退消费。
								pendingInputs.RemoveRange(0, chosenIndex + 1);
							}
						}
						else
						{
							// 没有合法新输入时：
							// - 不消费未来输入
							// - 不推进 lastConsumedMoveFrame
							// - 仅沿用 lastValidMove 做短时惯性，避免角色因瞬时缺包立刻停住
							int missCount = dic_consecutiveMissedFrames.TryGetValue(battlePlayerId, out int mc) ? mc + 1 : 1;
							dic_consecutiveMissedFrames[battlePlayerId] = missCount;
							var lastMove = dic_lastValidMove.TryGetValue(battlePlayerId, out var lm) ? lm : (0f, 0f);
							frameOp = new PlayerOperation { Battleid = battlePlayerId };
							frameOp.PlayerMoveX = lastMove.Item1;
							frameOp.PlayerMoveY = lastMove.Item2;
						}
					}

                    // 攻击与移动窗口解耦：
                    // pendingAttacks 在网络接收阶段完成去重/超时过滤，这里只负责把“当前仍有效”的攻击并入本帧权威操作。
					//playeroperation只是装攻击的容器，只是拿这个方便用不用另外定义别的
                    if (dic_pendingAttacks.TryGetValue(battlePlayerId, out PlayerOperation pendingAttackOp)
						 && pendingAttackOp != null
						 && pendingAttackOp.AttackOperations != null
						 && pendingAttackOp.AttackOperations.Count > 0)
                    {
                        if (frameOp == null)
                        {
                            frameOp = new PlayerOperation { Battleid = battlePlayerId };
                        }

                        foreach (var attack in pendingAttackOp.AttackOperations)
                        {
                            frameOp.AttackOperations.Add(attack);
                        }
                    }

                    if (frameOp != null)
					{
						// 只有本帧最终确实产出了“可广播的该玩家操作”才加入 nextFrameOp。
						// 这里的操作可能包含：新移动、惯性移动、攻击，或它们的组合。
						nextFrameOp.Operations.Add(frameOp);
					}
				}
			}
			catch (Exception ex)
			{
				Logging.Debug.Log(ex);
				nextFrameOp = new AllPlayerOperation();
			}
            //上面都只是拿输入，下面才是把输入真正应用到游戏状态，并生成对客户端的反馈：


            // 1. 根据 nextFrameOp 中最终选定的移动输入/惯性移动推进玩家权威位置
            UpdatePlayerPositions(nextFrameOp);

			// 2. 记录本帧推进后的权威位置，供延迟补偿子弹按历史帧回溯
			RecordPositionSnapshot(frameid);

			// 3. 处理本帧攻击，生成新的服务端子弹（攻击来自上面合并进 nextFrameOp 的 AttackOperations）
			SpawnBulletsFromOperations(nextFrameOp);

            // 4. 推进所有活跃子弹并收集本帧 HitEvent；这一步可能修改 HP / IsDead
            pendingHitEvents = new List<HitEvent>();
            TickServerBullets(frameid, pendingHitEvents);

			// 5. 最后再打包 PlayerStates，保证下发的是“本帧位置推进 + 子弹结算”后的最终权威状态
			PackPlayerStates(nextFrameOp, frameid);

			nextFrameOp.Frameid = frameid;
			dic_historyFrames[frameid] = nextFrameOp;

			// 6. 向所有尚未确认 GameOver 的客户端发送补帧包；包内会带上最新帧和本帧 HitEvent
			foreach (var item in battlePlayerIdToIp)
			{
				if (!dic_playerGameOver.TryGetValue(item.Key, out bool isGameOver) || !isGameOver)
				{
					SendUnsyncedFrames(item.Value, item.Key, pendingHitEvents);
				}
			}
			// 7. 当前帧的攻击已经被并入并广播，清空待攻击缓存，等待后续网络线程写入新攻击
			dic_pendingAttacks.Clear();
		}

		// ==================== 服务端位置追踪 ====================

		private void UpdatePlayerPositions(AllPlayerOperation frameOp)
		{
			foreach (PlayerOperation op in frameOp.Operations)
			{
				int bpId = op.Battleid;
				if (!playerPositions.TryGetValue(bpId, out ServerVector3 pos)) continue;

				float mx = op.PlayerMoveX;
				float mz = op.PlayerMoveY; // 客户端 PlayerMoveY 对应世界 Z 轴
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
