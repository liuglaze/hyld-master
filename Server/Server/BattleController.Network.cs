using SocketProto;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Server
{
	// ==================== BattleController — 网络收发（操作接收/帧广播/权威状态） ====================
	partial class BattleController
	{
		// ==================== 权威状态打包 ====================

		/// <summary>
		/// 将当前帧所有玩家的权威状态打包到 AllPlayerOperation.PlayerStates 中下发。
		/// 填充位置（pos_x/y/z）、HP 和死亡状态。
		/// </summary>
		private void PackPlayerStates(AllPlayerOperation frameOp, int currentFrameId)
		{
			foreach (var kvp in playerPositions)
			{
				int bpId = kvp.Key;
				ServerVector3 pos = kvp.Value;
				int hp = playerHp.TryGetValue(bpId, out int hpVal) ? hpVal : 0;
				bool isDead = playerIsDead.TryGetValue(bpId, out bool deadVal) && deadVal;
				frameOp.PlayerStates.Add(new AuthoritativePlayerState
				{
					BattleId = bpId,
					PosX = pos.X,
					PosY = pos.Y,
					PosZ = pos.Z,
					Hp = hp,
					IsDead = isDead,
				});
			}

			// 每 60 帧打印一次验证日志（约每秒一次），避免刷屏
			if (currentFrameId % 60 == 0)
			{
				var sb = new StringBuilder();
				sb.Append($"[AuthState] frame={currentFrameId} count={frameOp.PlayerStates.Count} ");
				foreach (var s in frameOp.PlayerStates)
					sb.Append($"bp{s.BattleId}=({s.PosX:F2},{s.PosY:F2},{s.PosZ:F2}) ");
				Logging.Debug.Log(sb.ToString());
			}
		}

		// ==================== 帧下行广播 ====================

		private void SendUnsyncedFrames(string endpoint, int battlePlayerId, List<HitEvent> hitEventsThisFrame)
		{
			MainPack pack = new MainPack();
			pack.Requestcode = RequestCode.Battle;
			pack.Actioncode = ActionCode.BattlePushDowmAllFrameOpeartions;
			BattleInfo battleInfo = new BattleInfo();
			int ackedFrameId = dic_playerAckedFrameId[battlePlayerId];
			
			// 限制最多只重传最近的 5 帧（含当前帧），防止网络极差时包体过大导致 UDP 雪崩
			// 例如：当前 frameid=100，最多发 [96, 97, 98, 99, 100]
			int maxCatchupFrames = 5;
			int startFrame = Math.Max(ackedFrameId + 1, frameid - maxCatchupFrames + 1);
			
			for (int i = startFrame; i <= frameid; i++)
			{
				if (dic_historyFrames.ContainsKey(i))
				{
					battleInfo.AllPlayerOperation.Add(dic_historyFrames[i]);
				}
			}
			battleInfo.OperationID = frameid;

			// 将本帧 HitEvent 搭载在最新帧的 BattleInfo 中下发（重传时也会附带）
			// 注意：重传的旧帧中不会重复包含 HitEvent，只有最新帧携带。
			// 这依赖于客户端的去重逻辑（attack_id + victim 去重）避免重复扣血。
			if (hitEventsThisFrame != null && hitEventsThisFrame.Count > 0)
			{
				foreach (HitEvent evt in hitEventsThisFrame)
					battleInfo.HitEvents.Add(evt);
			}

			pack.BattleInfo = battleInfo;
			LZJUDP.Instance.Send(pack, endpoint);
		}

		// ==================== 操作接收 ====================

		/// <summary>
		/// 接收客户端上行的单个玩家操作，并分别写入两条缓冲链路：
		/// 1. 移动：进入 dic_movementInputBuffer 的按 SyncFrameId 升序滑动窗口，等待 BattleLoop 消费。
		/// 2. 攻击：进入 dic_pendingAttacks，等待 CollectAndBroadcastCurrentFrame 合并进当前权威帧。
		///
		/// 注意这里“只负责接收与缓存”，并不会立刻推进玩家位置或结算伤害；
		/// 真正的状态推进仍发生在 BattleLoop -> CollectAndBroadcastCurrentFrame 中。
		/// </summary>
		public void UpdatePlayerOperation(BattleInfo battleInfo)
		{
			lock (_battleLock)
			{
				// SelfOperation 是客户端这次上行的“当前玩家自己的输入快照”。
				// OperationID 在当前协议语义里不是服务端 frameid，而是客户端为这次输入声明的目标帧号。
				// ClientAckedFrame 则是客户端显式确认“自己已收到并消费到哪一帧服务端权威状态”。
				PlayerOperation operation = battleInfo.SelfOperation;
				int clientFrameId = battleInfo.OperationID; // 客户端为本次输入声明的目标帧号
				int clientAckedFrame = battleInfo.ClientAckedFrame; // 客户端已确认收到的最新权威帧

				int battlePlayerId = operation.Battleid;
				// battlePlayerId 不存在通常意味着：玩家已退出战斗、战斗结束，或该输入属于非法/过时 battle 上下文。
				// 这里直接忽略，避免把脏数据写进当前战斗缓冲区。
				if (!dic_playerAckedFrameId.ContainsKey(battlePlayerId))
				{
					return;
				}

				// Ack 现在完全以客户端显式上报为准。
				// 语义上它表示“该玩家已经收到并应用到了 clientAckedFrame 为止的权威帧”，
				// 后续下行补帧窗口、旧输入清理都会依赖这个值。
				// 这里只允许单调递增，防止乱序 UDP 把确认进度回退。
				if (dic_playerAckedFrameId[battlePlayerId] < clientAckedFrame)
				{
					dic_playerAckedFrameId[battlePlayerId] = clientAckedFrame;
				}

				// dic_pendingAttacks 里存的 PlayerOperation 这里只把它当作“攻击缓冲容器”使用，
				// 不是完整的当帧输入本体。这样可以复用 proto 结构，避免再定义一套单独的待发攻击容器类型。
				if (!dic_pendingAttacks.TryGetValue(battlePlayerId, out PlayerOperation bufferedAttackOperation)
					|| bufferedAttackOperation == null)
				{
					bufferedAttackOperation = new PlayerOperation { Battleid = battlePlayerId };
					dic_pendingAttacks[battlePlayerId] = bufferedAttackOperation;
				}

                // ── Move Input Buffer：把移动输入写入按 SyncFrameId 升序的滑动窗口 ──
                // 接收阶段只负责入缓冲 / 同帧覆盖 / 容量控制
                // 一旦某个 chosenInput 真正被消费，旧输入会在消费阶段统一删除。
                //把某个玩家刚收到的一条移动输入，写进这个玩家的待消费输入列表里。
                if (dic_movementInputBuffer.TryGetValue(battlePlayerId, out List<BufferedMoveInput> pendingInputs))
				{
					// 读取“服务端已经实际消费到哪一帧移动输入”。
					// 注意这不是客户端 ack，而是服务端消费进度；两者一个是下行确认，一个是上行输入消费。
					int lastConsumedMoveFrame = dic_lastConsumedMoveFrame.TryGetValue(battlePlayerId, out int consumedMoveFrame)
						? consumedMoveFrame
						: 0;
					if (clientFrameId <= lastConsumedMoveFrame)
					{
                        // 如果新来的输入比已消费进度还老，直接拒绝
                        Logging.Debug.Log($"[MoveBuffer][REJECT_STALE] bp={battlePlayerId} clientFrame={clientFrameId} lastConsumed={lastConsumedMoveFrame} ack={clientAckedFrame}");
					}
					else
					{
                        //同一帧号的移动输入，以最后收到的版本为准
                        BufferedMoveInput existing = pendingInputs.Find(item => item.SyncFrameId == clientFrameId);
                        //如果这个帧号还没有，就新增
                        if (existing != null)
						{
							existing.MoveX = operation.PlayerMoveX;
							existing.MoveY = operation.PlayerMoveY;
							existing.ReceivedServerFrame = frameid;
						}
						else
						{
							pendingInputs.Add(new BufferedMoveInput
							{
								SyncFrameId = clientFrameId,
								MoveX = operation.PlayerMoveX,
								MoveY = operation.PlayerMoveY,
								ReceivedServerFrame = frameid,
							});
						}

						// 始终维持升序，便于 BattleLoop 之后按“最小合法帧”去取输入。
						pendingInputs.Sort((a, b) => a.SyncFrameId.CompareTo(b.SyncFrameId));
						if (pendingInputs.Count > InputBufferSize)
						{
                            //如果 buffer 太大，就删掉最老的
                            BufferedMoveInput evicted = pendingInputs[0];
							pendingInputs.RemoveAt(0);
							Logging.Debug.Log($"[MoveBuffer][EVICT_OLDEST_ON_FULL] bp={battlePlayerId} evicted={evicted.SyncFrameId} accepted={clientFrameId} size={pendingInputs.Count}/{InputBufferSize}");
						}
					}
				}

				// 攻击与移动解耦：
				// - 移动依赖滑动窗口和按帧消费；
				// - 攻击是离散事件，只要未过期且未重复，就先缓存进 pendingAttacks。
				// 后续 CollectAndBroadcastCurrentFrame 会把这些待处理攻击并入当前权威帧。
				if (operation.AttackOperations != null && operation.AttackOperations.Count > 0)
				{
					foreach (var incomingAttack in operation.AttackOperations)
					{
						Logging.Debug.Log($"[SERVER DEBUG] 收到来自 BattlePlayerID {battlePlayerId} 的攻击. 原始方向: ({incomingAttack.Towardx}, {incomingAttack.Towardy}) clientFrameId={incomingAttack.ClientFrameId}");

						// 攻击允许有一定延迟补偿空间，但不是无限回补。
						// 若攻击声明的 clientFrameId 距当前服务端帧已经太老，就拒绝进入待处理队列，
						// 避免超迟到攻击重新命中过去状态，拉高补偿复杂度和作弊面。
						int frameDelay = frameid - incomingAttack.ClientFrameId;
						if (incomingAttack.ClientFrameId > 0 && frameDelay > MaxAcceptableAttackDelay)
						{
							Logging.Debug.Log($"[AttackTimeout] REJECT bp={battlePlayerId} attackId={incomingAttack.AttackId} clientFrame={incomingAttack.ClientFrameId} serverFrame={frameid} delay={frameDelay} max={MaxAcceptableAttackDelay}");
							continue;
						}

						// 攻击去重依据是 attackId 单调递增：
						// 只接受比 lastProcessed 更大的 attackId。
						// 这允许客户端在 UDP 丢包时持续重发“同一次攻击”，而服务端只真正处理一次。
						if (incomingAttack.AttackId > dic_lastProcessedAttackId[battlePlayerId])
						{
							bufferedAttackOperation.AttackOperations.Add(incomingAttack);
							dic_lastProcessedAttackId[battlePlayerId] = incomingAttack.AttackId;
							Logging.Debug.Log($"[AttackDedup] ACCEPT bp={battlePlayerId} attackId={incomingAttack.AttackId} lastProcessed={dic_lastProcessedAttackId[battlePlayerId]} delay={frameDelay}");
						}
						else
						{
							Logging.Debug.Log($"[AttackDedup] SKIP bp={battlePlayerId} attackId={incomingAttack.AttackId} lastProcessed={dic_lastProcessedAttackId[battlePlayerId]} (duplicate)");
						}
					}
				}
			}
		}

		// ==================== 战斗结束 ====================

		private void HandleBattleEnd()
		{
			Dictionary<int, string> endpointSnapshot;
			Dictionary<int, AllPlayerOperation> historySnapshot;

			lock (_battleLock)
			{
				if (_hasEnded)
				{
					return;
				}
				_hasEnded = true;
				_isRun = false;
				endpointSnapshot = new Dictionary<int, string>(battlePlayerIdToIp);
				historySnapshot = new Dictionary<int, AllPlayerOperation>(dic_historyFrames);

				// 清理子弹和位置历史
				activeBullets?.Clear();
				positionHistory?.Clear();
				positionHistoryOrder?.Clear();
				// ── Input Buffer 清理 ──
				dic_movementInputBuffer?.Clear();
				dic_lastConsumedMoveFrame?.Clear();
				dic_lastValidMove?.Clear();
				dic_consecutiveMissedFrames?.Clear();
			}

			Logging.Debug.Log($"Battle循环结束，BattleID: {battleId}");
			foreach (var item in endpointSnapshot)
			{
				SendFinishBattle(item.Value);
			}
			// GameOver 控制包完成统一 NetSim 调度后，再清除战斗期网络模拟与路由
			LZJUDP.ClearBattleNetSimConfig();
			Logging.Debug.FlushTrace(); // 战斗结束后强制写入日志文件
			LZJUDP.Instance.UnregisterBattle(battleId);
			BattleManage.Instance.FinishBattle(battleId, historySnapshot);
			Console.WriteLine("战斗结束咯......");
		}

		private void SendFinishBattle(string endpoint)
		{
			MainPack pack = new MainPack();
			pack.Requestcode = RequestCode.Battle;
			pack.Actioncode = ActionCode.BattlePushDowmGameOver;
			// 传递赢家 teamId（击杀者的队伍），客户端根据自己的 teamId 判断胜负
			pack.Str = _killerTeamId > 0 ? _killerTeamId.ToString() : "1";
			LZJUDP.Instance.Send(pack, endpoint);
			Logging.Debug.Log($"[SendFinishBattle] endpoint={endpoint} winnerTeamId={pack.Str}");
		}

		public void UpdatePlayerGameOver(int battlePlayerId)
		{
			lock (_battleLock)
			{
				hasAnyPlayerDied = true;
				dic_playerGameOver[battlePlayerId] = true;
				allClientsConfirmedGameOver = true;
				foreach (bool playerGameOver in dic_playerGameOver.Values)
				{
					if (!playerGameOver)
					{
						allClientsConfirmedGameOver = false;
						break;
					}
				}
			}
		}
	}
}
