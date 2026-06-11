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
		/// 将当前帧所有玩家的权威状态打包到 BattleFrameSync.PlayerStates 中下发。
		/// 填充位置（pos_x/y/z）、HP 和死亡状态。
		/// </summary>
		private void PackPlayerStates(BattleFrameSync frameOp, int currentFrameId)
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
			if (string.IsNullOrEmpty(endpoint) || !dic_historyFrames.TryGetValue(frameid, out BattleFrameSync currentFrame))
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
				battleInfo.Frames.Add(currentFrame);
				battleInfo.OperationID = frameid;
				battleInfo.AckedMoveFrame = dic_lastProcessedMoveFrame != null && dic_lastProcessedMoveFrame.TryGetValue(battlePlayerId, out int moveFrame)
					? moveFrame
					: 0;
				if (dic_lastMoveAck != null && dic_lastMoveAck.TryGetValue(battlePlayerId, out MoveAckResult moveAck))
				{
					battleInfo.MoveAck = moveAck.Clone();
					battleInfo.AckedMoveFrame = moveAck.AckedMoveFrame;
				}

				if (hitEventsThisFrame != null && hitEventsThisFrame.Count > 0)
				{
					foreach (HitEvent evt in hitEventsThisFrame)
						battleInfo.HitEvents.Add(evt);
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
		/// 1. 移动：按 ClientMove.MoveFrame 单调处理，接收阶段重模拟并推进服务端权威位置。
		/// 2. 攻击：进入 dic_pendingAttacks，等待 CollectAndBroadcastCurrentFrame 合并进当前权威帧。
		///
		/// 注意这里不会结算伤害；伤害仍发生在 BattleLoop -> CollectAndBroadcastCurrentFrame 中。
		/// </summary>
		public void UpdatePlayerOperation(BattleInfo battleInfo)
		{
			lock (_battleLock)
			{
				// SelfOperation 当前仍承载 battlePlayerId 和攻击列表；移动已迁移到 ClientMoves。
				// ClientAckedFrame 则是客户端显式确认“自己已收到并消费到哪一帧服务端权威状态”。
				PlayerOperation operation = battleInfo.SelfOperation;
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
					Logging.Debug.Log($"[AckGap][Recv] bp={battlePlayerId} serverFrame={frameid} uploadOperationId={battleInfo.OperationID} clientAckedFrame={clientAckedFrame} storedAckedFrame={effectiveAckedFrame} prevAckedFrame={previousAckedFrame} ackGap={ackGap} ackAdvanced={ackAdvanced} clientRttMs={battleInfo.ClientRttMs}");
				}

				// dic_pendingAttacks 里存的 PlayerOperation 这里只把它当作“攻击缓冲容器”使用，
				// 不是完整的当帧输入本体。这样可以复用 proto 结构，避免再定义一套单独的待发攻击容器类型。
				if (!dic_pendingAttacks.TryGetValue(battlePlayerId, out PlayerOperation bufferedAttackOperation)
					|| bufferedAttackOperation == null)
				{
					bufferedAttackOperation = new PlayerOperation { Battleid = battlePlayerId };
					dic_pendingAttacks[battlePlayerId] = bufferedAttackOperation;
				}

				ProcessClientMoves(battlePlayerId, battleInfo);

				// 攻击与移动解耦：
				// - 移动使用 ClientMoveFrame 做上行排序，并在接收合法 move 时推进权威位置。
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

		private void ProcessClientMoves(int battlePlayerId, BattleInfo battleInfo)
		{
			if (battleInfo.ClientMoves == null || battleInfo.ClientMoves.Count == 0)
			{
				return;
			}

			for (int i = 0; i < battleInfo.ClientMoves.Count; i++)
			{
				ClientMove move = battleInfo.ClientMoves[i];
				if (move.MoveType == MoveType.OldMove)
				{
					ProcessClientMove(battlePlayerId, move);
				}
			}

			for (int i = 0; i < battleInfo.ClientMoves.Count; i++)
			{
				ClientMove move = battleInfo.ClientMoves[i];
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

			int lastProcessedFrame = dic_lastProcessedMoveFrame.TryGetValue(battlePlayerId, out int storedFrame)
				? storedFrame
				: 0;
			if (move.MoveFrame <= lastProcessedFrame)
			{
				Logging.Debug.Log($"[ClientMove][STALE] bp={battlePlayerId} type={move.MoveType} moveFrame={move.MoveFrame} lastProcessed={lastProcessedFrame} serverFrame={frameid}");
				return;
			}

			int deltaFrames = move.MoveFrame - lastProcessedFrame;
			int maxAcceptedMoveFrame = frameid + MaxClientMoveFrameLead;
			if (move.MoveFrame > maxAcceptedMoveFrame)
			{
				Logging.Debug.Log($"[ClientMove][REJECT_FUTURE] bp={battlePlayerId} type={move.MoveType} moveFrame={move.MoveFrame} lastProcessed={lastProcessedFrame} delta={deltaFrames} serverFrame={frameid} maxAccepted={maxAcceptedMoveFrame} leadLimit={MaxClientMoveFrameLead}");
				return;
			}

			int effectiveFrames = CalculateEffectiveSimulationFrames(battlePlayerId, deltaFrames);
			ServerVector3 serverPosAfterMove = SimulateAuthoritativeMove(battlePlayerId, pos, move.MoveX, move.MoveY, effectiveFrames);
			playerPositions[battlePlayerId] = serverPosAfterMove;

			dic_lastProcessedMoveFrame[battlePlayerId] = move.MoveFrame;
			dic_lastProcessedMoveServerFrame[battlePlayerId] = frameid;
			dic_lastProcessedMoveInput[battlePlayerId] = new LastProcessedMoveInput
			{
				MoveFrame = move.MoveFrame,
				MoveX = move.MoveX,
				MoveY = move.MoveY,
				MoveType = move.MoveType,
			};

			bool zeroMove = Math.Abs(move.MoveX) <= 1e-6f && Math.Abs(move.MoveY) <= 1e-6f;
			ServerVector3 predicted = new ServerVector3(move.PredictedPosX, move.PredictedPosY, move.PredictedPosZ);
			float error = ServerVector3.Distance(serverPosAfterMove, predicted);
			bool ackGoodMove = error <= MoveCorrectionThreshold;
			int frameDiscrepancy = dic_accumulatedFrameDiscrepancy.TryGetValue(battlePlayerId, out int discrepancy) ? discrepancy : 0;
			bool resolvingFrameDiscrepancy = dic_resolvingFrameDiscrepancy.TryGetValue(battlePlayerId, out bool resolving) && resolving;

			if (move.MoveType != MoveType.OldMove)
			{
				MoveAckResult ack = new MoveAckResult
				{
					BattleId = battlePlayerId,
					AckedMoveFrame = move.MoveFrame,
					AckGoodMove = ackGoodMove,
					CorrectPosX = serverPosAfterMove.X,
					CorrectPosY = serverPosAfterMove.Y,
					CorrectPosZ = serverPosAfterMove.Z,
					FrameDiscrepancy = frameDiscrepancy,
					ResolvingFrameDiscrepancy = resolvingFrameDiscrepancy,
				};
				dic_lastMoveAck[battlePlayerId] = ack;

				if (!ackGoodMove || effectiveFrames != deltaFrames || move.MoveFrame % 60 == 0 || zeroMove)
				{
					Logging.Debug.Log($"[ClientMove][NEW] bp={battlePlayerId} moveFrame={move.MoveFrame} delta={deltaFrames} effective={effectiveFrames} move=({move.MoveX:F4},{move.MoveY:F4}) zero={zeroMove} serverFrame={frameid} pos=({serverPosAfterMove.X:F2},{serverPosAfterMove.Z:F2}) predicted=({predicted.X:F2},{predicted.Z:F2}) error={error:F3} ackGood={ackGoodMove} discrepancy={frameDiscrepancy} resolving={resolvingFrameDiscrepancy}");
				}
			}
			else
			{
				Logging.Debug.Log($"[ClientMove][OLD] bp={battlePlayerId} moveFrame={move.MoveFrame} delta={deltaFrames} effective={effectiveFrames} move=({move.MoveX:F4},{move.MoveY:F4}) zero={zeroMove} serverFrame={frameid} pos=({serverPosAfterMove.X:F2},{serverPosAfterMove.Z:F2}) error={error:F3} ackGood={ackGoodMove} discrepancy={frameDiscrepancy} resolving={resolvingFrameDiscrepancy}");
			}
		}

		private int CalculateEffectiveSimulationFrames(int battlePlayerId, int clientDeltaFrames)
		{
			int lastServerFrame = dic_lastProcessedMoveServerFrame.TryGetValue(battlePlayerId, out int storedServerFrame)
				? storedServerFrame
				: 0;
			int serverDeltaFrames = Math.Max(1, frameid - lastServerFrame);
			int frameDiscrepancy = dic_accumulatedFrameDiscrepancy.TryGetValue(battlePlayerId, out int storedDiscrepancy)
				? storedDiscrepancy
				: 0;

			frameDiscrepancy += clientDeltaFrames - serverDeltaFrames;
			if (frameDiscrepancy < 0)
			{
				frameDiscrepancy = 0;
			}

			bool resolving = dic_resolvingFrameDiscrepancy.TryGetValue(battlePlayerId, out bool storedResolving) && storedResolving;
			if (frameDiscrepancy > FrameDiscrepancyMaxMargin)
			{
				resolving = true;
			}

			int effectiveFrames = clientDeltaFrames;
			if (resolving && frameDiscrepancy > 0)
			{
				int paybackFrames = Math.Min(FrameDiscrepancyPaybackPerMove, Math.Min(frameDiscrepancy, effectiveFrames));
				effectiveFrames -= paybackFrames;
				frameDiscrepancy -= paybackFrames;
				if (frameDiscrepancy <= 0)
				{
					frameDiscrepancy = 0;
					resolving = false;
				}
			}

			dic_accumulatedFrameDiscrepancy[battlePlayerId] = frameDiscrepancy;
			dic_resolvingFrameDiscrepancy[battlePlayerId] = resolving;
			return Math.Max(0, effectiveFrames);
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
			Dictionary<int, BattleFrameSync> historySnapshot;

			lock (_battleLock)
			{
				if (_hasEnded)
				{
					return;
				}
				_hasEnded = true;
				_isRun = false;
				endpointSnapshot = new Dictionary<int, string>(battlePlayerIdToIp);
				historySnapshot = new Dictionary<int, BattleFrameSync>(dic_historyFrames);

				// 清理子弹和位置历史
				activeBullets?.Clear();
				positionHistory?.Clear();
				positionHistoryOrder?.Clear();
				// ── ClientMove 状态清理 ──
				dic_lastProcessedMoveFrame?.Clear();
				dic_lastProcessedMoveServerFrame?.Clear();
				dic_lastProcessedMoveInput?.Clear();
				dic_accumulatedFrameDiscrepancy?.Clear();
				dic_resolvingFrameDiscrepancy?.Clear();
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
