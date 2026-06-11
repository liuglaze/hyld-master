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
		/// 将当前帧所有玩家的权威状态打包到 BattleFrame.PlayerStates 中下发。
		/// 填充位置（pos_x/y/z）、HP 和死亡状态。
		/// </summary>
		private void PackPlayerStates(BattleFrame frameOp, int currentFrameId)
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
			if (string.IsNullOrEmpty(endpoint) || !dic_historyFrames.TryGetValue(frameid, out BattleFrame currentFrame))
			{
				return;
			}

			int ackedFrame = dic_playerAckedFrameId.TryGetValue(battlePlayerId, out int ackFrame) ? ackFrame : 0;
			int ackGap = frameid - ackedFrame;
			int repeatCount = ackGap <= AckGapRepeatThreshold ? CurrentFrameRepeatSendCount : CurrentFrameRepeatSendCount + 1;

			for (int repeatIndex = 0; repeatIndex < repeatCount; repeatIndex++)
			{
				MainPack pack = new MainPack();
				pack.Requestcode = RequestCode.Battle;
				pack.Actioncode = ActionCode.BattlePushDowmAllFrameOpeartions;
				BattleInfo battleInfo = new BattleInfo();
				battleInfo.ServerFrame = frameid;
				battleInfo.ServerUpdate = new BattleServerUpdate();
				battleInfo.ServerUpdate.Frames.Add(currentFrame);
				if (dic_lastMoveAck != null && dic_lastMoveAck.TryGetValue(battlePlayerId, out MoveAckResult moveAck))
				{
					battleInfo.ServerUpdate.MoveAck = moveAck.Clone();
				}

				if (hitEventsThisFrame != null && hitEventsThisFrame.Count > 0)
				{
					foreach (HitEvent evt in hitEventsThisFrame)
						battleInfo.ServerUpdate.HitEvents.Add(evt);
				}

				pack.BattleInfo = battleInfo;
				LZJUDP.Instance.Send(pack, endpoint);
			}

			if (repeatCount > 1 || ackGap >= AckGapRepeatThreshold || frameid % 60 == 0)
			{
				Logging.Debug.Log($"[AckGap][FrameSend] bp={battlePlayerId} frame={frameid} ackedFrame={ackedFrame} ackGap={ackGap} threshold={AckGapRepeatThreshold} repeats={repeatCount} hitEvents={(hitEventsThisFrame != null ? hitEventsThisFrame.Count : 0)} endpoint={endpoint}");
			}
		}

		// ==================== 操作接收 ====================

		/// <summary>
		/// 接收客户端上行的单个玩家操作，并分别写入两条链路：
		/// 1. 移动：按 ClientMove.MoveFrame 单调接受并入队，BattleLoop 按固定预算推进服务端权威位置。
		/// 2. 攻击：进入 dic_pendingAttacks，等待 CollectAndBroadcastCurrentFrame 合并进当前权威帧。
		///
		/// 注意这里不会结算伤害；伤害仍发生在 BattleLoop -> CollectAndBroadcastCurrentFrame 中。
		/// </summary>
		public void UpdatePlayerOperation(BattleInfo battleInfo)
		{
			lock (_battleLock)
			{
				BattleClientInput input = battleInfo.ClientInput;
				if (input == null)
				{
					return;
				}

				int clientAckedFrame = input.AckedServerFrame;
				int battlePlayerId = input.BattlePlayerId;
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
				int previousAckedFrame = dic_playerAckedFrameId.TryGetValue(battlePlayerId, out int storedAckedFrame)
					? storedAckedFrame
					: 0;
				if (previousAckedFrame < clientAckedFrame)
				{
					dic_playerAckedFrameId[battlePlayerId] = clientAckedFrame;
				}
				int effectiveAckedFrame = dic_playerAckedFrameId[battlePlayerId];
				int ackGap = frameid - effectiveAckedFrame;
				bool ackAdvanced = effectiveAckedFrame > previousAckedFrame;
				if (ackAdvanced || ackGap >= AckGapRepeatThreshold || frameid % 60 == 0)
				{
					Logging.Debug.Log($"[AckGap][Recv] bp={battlePlayerId} serverFrame={frameid} clientTick={input.ClientTick} clientAckedFrame={clientAckedFrame} storedAckedFrame={effectiveAckedFrame} prevAckedFrame={previousAckedFrame} ackGap={ackGap} ackAdvanced={ackAdvanced} clientRttMs={input.RttMs}");
				}

				if (!dic_pendingAttacks.TryGetValue(battlePlayerId, out PlayerFrameInput bufferedAttackOperation)
					|| bufferedAttackOperation == null)
				{
					bufferedAttackOperation = new PlayerFrameInput { BattlePlayerId = battlePlayerId };
					dic_pendingAttacks[battlePlayerId] = bufferedAttackOperation;
				}

				ProcessClientMoves(battlePlayerId, input);

				// 攻击与移动解耦：
				// - 移动使用 ClientMoveFrame 做上行排序，接收合法 move 后排队等待 BattleLoop 慢消化。
				// - 攻击是离散事件，只要未过期且未重复，就先缓存进 pendingAttacks。
				// 后续 CollectAndBroadcastCurrentFrame 会把这些待处理攻击并入当前权威帧。
				if (input.Attacks != null && input.Attacks.Count > 0)
				{
					foreach (var incomingAttack in input.Attacks)
					{
						Logging.Debug.Log($"[SERVER DEBUG] 收到来自 BattlePlayerID {battlePlayerId} 的攻击. 原始方向: ({incomingAttack.TowardX}, {incomingAttack.TowardY}) attackMoveFrame={incomingAttack.AttackMoveFrame}");

						// 攻击允许有一定延迟补偿空间，但不是无限回补。
						// 若攻击声明的 clientFrameId 距当前服务端帧已经太老，就拒绝进入待处理队列，
						// 避免超迟到攻击重新命中过去状态，拉高补偿复杂度和作弊面。
						int frameDelay = frameid - incomingAttack.AttackMoveFrame;
						if (incomingAttack.AttackMoveFrame > 0 && frameDelay > MaxAcceptableAttackDelay)
						{
							Logging.Debug.Log($"[AttackTimeout] REJECT bp={battlePlayerId} attackId={incomingAttack.AttackId} attackMoveFrame={incomingAttack.AttackMoveFrame} serverFrame={frameid} delay={frameDelay} max={MaxAcceptableAttackDelay}");
							continue;
						}

						// 攻击去重依据是 attackId 单调递增：
						// 只接受比 lastProcessed 更大的 attackId。
						// 这允许客户端在 UDP 丢包时持续重发“同一次攻击”，而服务端只真正处理一次。
						if (incomingAttack.AttackId > dic_lastProcessedAttackId[battlePlayerId])
						{
							bufferedAttackOperation.Attacks.Add(new ServerAttack
							{
								AttackId = incomingAttack.AttackId,
								AttackerBattlePlayerId = battlePlayerId,
								AttackMoveFrame = incomingAttack.AttackMoveFrame,
								TowardX = incomingAttack.TowardX,
								TowardY = incomingAttack.TowardY,
							});
							dic_lastProcessedAttackId[battlePlayerId] = incomingAttack.AttackId;
							Logging.Debug.Log($"[AttackDedup] ACCEPT bp={battlePlayerId} attackId={incomingAttack.AttackId} lastProcessed={dic_lastProcessedAttackId[battlePlayerId]} delay={frameDelay}");
						}
						else
						{
                            bool repeatedSameAttack = incomingAttack.AttackId == dic_lastProcessedAttackId[battlePlayerId];
							Logging.Debug.Log($"[AttackDedup] SKIP bp={battlePlayerId} attackId={incomingAttack.AttackId} lastProcessed={dic_lastProcessedAttackId[battlePlayerId]} duplicateSameId={repeatedSameAttack} (duplicate)");
                            if (repeatedSameAttack)
                            {
                                Logging.Debug.Log($"[CriticalInput][AttackBurstIdempotent] bp={battlePlayerId} attackId={incomingAttack.AttackId} frame={frameid} delay={frameDelay}");
                            }
						}
					}
				}
			}
		}

		private void ProcessClientMoves(int battlePlayerId, BattleClientInput input)
		{
			if (input.Moves == null || input.Moves.Count == 0)
			{
				return;
			}

			for (int i = 0; i < input.Moves.Count; i++)
			{
				ClientMove move = input.Moves[i];
				if (move.MoveType == MoveType.OldMove)
				{
					ProcessClientMove(battlePlayerId, move);
				}
			}

			for (int i = 0; i < input.Moves.Count; i++)
			{
				ClientMove move = input.Moves[i];
				if (move.MoveType != MoveType.OldMove)
				{
					ProcessClientMove(battlePlayerId, move);
				}
			}
		}

		private void ProcessClientMove(int battlePlayerId, ClientMove move)
		{
			if (!playerPositions.TryGetValue(battlePlayerId, out ServerVector3 pos))
			{
				return;
			}

			int lastAcceptedFrame = dic_lastAcceptedMoveFrame.TryGetValue(battlePlayerId, out int storedFrame)
				? storedFrame
				: 0;
			if (move.MoveFrame <= lastAcceptedFrame)
			{
				Logging.Debug.Log($"[ClientMove][STALE] bp={battlePlayerId} type={move.MoveType} moveFrame={move.MoveFrame} lastAccepted={lastAcceptedFrame} serverFrame={frameid}");
				return;
			}

			int segmentFrames = move.MoveFrame - lastAcceptedFrame;
			int maxAcceptedMoveFrame = frameid + MaxClientMoveFrameLead;
			if (move.MoveFrame > maxAcceptedMoveFrame)
			{
				Logging.Debug.Log($"[ClientMove][REJECT_FUTURE] bp={battlePlayerId} type={move.MoveType} moveFrame={move.MoveFrame} lastAccepted={lastAcceptedFrame} delta={segmentFrames} serverFrame={frameid} maxAccepted={maxAcceptedMoveFrame} leadLimit={MaxClientMoveFrameLead}");
				return;
			}

			if (!dic_pendingMoveSegments.TryGetValue(battlePlayerId, out Queue<PendingMoveSegment> queue))
			{
				queue = new Queue<PendingMoveSegment>();
				dic_pendingMoveSegments[battlePlayerId] = queue;
			}

			queue.Enqueue(new PendingMoveSegment
			{
				MoveFrame = move.MoveFrame,
				RemainingFrames = segmentFrames,
				MoveX = move.MoveX,
				MoveY = move.MoveY,
				MoveType = move.MoveType,
				PredictedPosition = new ServerVector3(move.PredictedPosX, move.PredictedPosY, move.PredictedPosZ),
			});

			dic_lastAcceptedMoveFrame[battlePlayerId] = move.MoveFrame;
			dic_lastProcessedMoveInput[battlePlayerId] = new LastProcessedMoveInput
			{
				MoveFrame = move.MoveFrame,
				MoveX = move.MoveX,
				MoveY = move.MoveY,
				MoveType = move.MoveType,
			};

			bool zeroMove = Math.Abs(move.MoveX) <= 1e-6f && Math.Abs(move.MoveY) <= 1e-6f;
			int backlogFrames = GetPendingMoveBacklogFrames(battlePlayerId);
			if (segmentFrames > MaxMoveApplyFramesPerServerFrame || backlogFrames > MaxMoveApplyFramesPerServerFrame || zeroMove)
			{
				Logging.Debug.Log($"[ClientMove][ACCEPT] bp={battlePlayerId} type={move.MoveType} moveFrame={move.MoveFrame} segment={segmentFrames} backlog={backlogFrames} move=({move.MoveX:F4},{move.MoveY:F4}) zero={zeroMove} serverFrame={frameid} pos=({pos.X:F2},{pos.Z:F2})");
			}
		}

		private void ApplyPendingMoveSegments()
		{
			foreach (int battlePlayerId in uidToBattlePlayerId.Values)
			{
				if (!dic_pendingMoveSegments.TryGetValue(battlePlayerId, out Queue<PendingMoveSegment> queue)
					|| queue.Count == 0
					|| !playerPositions.TryGetValue(battlePlayerId, out ServerVector3 pos))
				{
					continue;
				}

				int frameBudget = MaxMoveApplyFramesPerServerFrame;
				int appliedThisServerFrame = 0;

				while (frameBudget > 0 && queue.Count > 0)
				{
					PendingMoveSegment segment = queue.Peek();
					int framesToApply = Math.Min(frameBudget, segment.RemainingFrames);
					pos = SimulateAuthoritativeMove(battlePlayerId, pos, segment.MoveX, segment.MoveY, framesToApply);
					playerPositions[battlePlayerId] = pos;

					segment.RemainingFrames -= framesToApply;
					frameBudget -= framesToApply;
					appliedThisServerFrame += framesToApply;

					int simulatedFrame = dic_lastSimulatedMoveFrame.TryGetValue(battlePlayerId, out int storedSimulated)
						? storedSimulated
						: 0;
					simulatedFrame += framesToApply;
					dic_lastSimulatedMoveFrame[battlePlayerId] = simulatedFrame;

					if (segment.RemainingFrames <= 0)
					{
						queue.Dequeue();
						if (segment.MoveType != MoveType.OldMove)
						{
							float error = ServerVector3.Distance(pos, segment.PredictedPosition);
							bool ackGoodMove = error <= MoveCorrectionThreshold;
							SetMoveAck(
								battlePlayerId,
								simulatedFrame,
								ackGoodMove,
								pos,
								GetPendingMoveBacklogFrames(battlePlayerId));

							if (!ackGoodMove || segment.MoveFrame % 60 == 0)
							{
								Logging.Debug.Log($"[ClientMove][SIM_DONE] bp={battlePlayerId} moveFrame={segment.MoveFrame} appliedThisFrame={appliedThisServerFrame} move=({segment.MoveX:F4},{segment.MoveY:F4}) serverFrame={frameid} pos=({pos.X:F2},{pos.Z:F2}) predicted=({segment.PredictedPosition.X:F2},{segment.PredictedPosition.Z:F2}) error={error:F3} ackGood={ackGoodMove} backlog={GetPendingMoveBacklogFrames(battlePlayerId)}");
							}
						}
						else
						{
							SetMoveAck(
								battlePlayerId,
								simulatedFrame,
								true,
								pos,
								GetPendingMoveBacklogFrames(battlePlayerId));
						}
					}
					else
					{
						SetMoveAck(
							battlePlayerId,
							simulatedFrame,
							true,
							pos,
							GetPendingMoveBacklogFrames(battlePlayerId));
					}
				}

				int backlogFrames = GetPendingMoveBacklogFrames(battlePlayerId);
				if (appliedThisServerFrame > 0 && (backlogFrames > 0 || frameid % 60 == 0))
				{
					int simulatedFrame = dic_lastSimulatedMoveFrame.TryGetValue(battlePlayerId, out int storedSimulated)
						? storedSimulated
						: 0;
					int acceptedFrame = dic_lastAcceptedMoveFrame.TryGetValue(battlePlayerId, out int storedAccepted)
						? storedAccepted
						: 0;
					Logging.Debug.Log($"[ClientMove][SIM_APPLY] bp={battlePlayerId} serverFrame={frameid} applied={appliedThisServerFrame} simulated={simulatedFrame} accepted={acceptedFrame} backlog={backlogFrames} pos=({pos.X:F2},{pos.Z:F2})");
				}
			}
		}

		private void SetMoveAck(int battlePlayerId, int ackedMoveFrame, bool ackGoodMove, ServerVector3 correctPosition, int pendingBacklogFrames)
		{
			dic_lastMoveAck[battlePlayerId] = new MoveAckResult
			{
				BattleId = battlePlayerId,
				AckedMoveFrame = ackedMoveFrame,
				AckGoodMove = ackGoodMove,
				CorrectPosX = correctPosition.X,
				CorrectPosY = correctPosition.Y,
				CorrectPosZ = correctPosition.Z,
				FrameDiscrepancy = pendingBacklogFrames,
				ResolvingFrameDiscrepancy = pendingBacklogFrames > 0,
			};
		}

		private int GetPendingMoveBacklogFrames(int battlePlayerId)
		{
			if (!dic_pendingMoveSegments.TryGetValue(battlePlayerId, out Queue<PendingMoveSegment> queue) || queue.Count == 0)
			{
				return 0;
			}

			int backlog = 0;
			foreach (PendingMoveSegment segment in queue)
			{
				backlog += segment.RemainingFrames;
			}
			return backlog;
		}

		private ServerVector3 SimulateAuthoritativeMove(int battlePlayerId, ServerVector3 startPos, float moveX, float moveY, int frameCount)
		{
			if (frameCount <= 0)
			{
				return startPos;
			}

			float len = (float)Math.Sqrt(moveX * moveX + moveY * moveY);
			if (len <= 1e-6f)
			{
				return startPos;
			}

			float mx = moveX / len;
			float mz = moveY / len;
			float teamSign = 1f;
			if (playerTeamIds.TryGetValue(battlePlayerId, out int tid) && tid != baseTeamId)
			{
				teamSign = -1f;
			}

			ServerVector3 result = startPos;
			float distance = MoveSpeed * FrameTimeSec * frameCount;
			result.X += -mx * teamSign * distance;
			result.Z += mz * teamSign * distance;
			return result;
		}

		// ==================== 战斗结束 ====================

		private void HandleBattleEnd()
		{
			Dictionary<int, string> endpointSnapshot;
			Dictionary<int, BattleFrame> historySnapshot;

			lock (_battleLock)
			{
				if (_hasEnded)
				{
					return;
				}
				_hasEnded = true;
				_isRun = false;
				endpointSnapshot = new Dictionary<int, string>(battlePlayerIdToIp);
				historySnapshot = new Dictionary<int, BattleFrame>(dic_historyFrames);

				// 清理子弹和位置历史
				activeBullets?.Clear();
				positionHistory?.Clear();
				positionHistoryOrder?.Clear();
				// ── ClientMove 状态清理 ──
				dic_lastAcceptedMoveFrame?.Clear();
				dic_lastSimulatedMoveFrame?.Clear();
				dic_pendingMoveSegments?.Clear();
				dic_lastProcessedMoveInput?.Clear();
				dic_lastMoveAck?.Clear();
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
