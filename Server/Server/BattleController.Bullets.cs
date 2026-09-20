using SocketProto;
using PMNet.Shared;
using System;
using System.Collections.Generic;

namespace Server
{
	// ==================== BattleController — 子弹系统（生成/碰撞/追帧/HP） ====================
	partial class BattleController
	{
		// ==================== 子弹生成 ====================

		private void SpawnBulletsFromOperations(BattleFrame frameOp)
		{
			foreach (PlayerFrameInput op in frameOp.PlayerInputs)
			{
				int bpId = op.BattlePlayerId;
				if (op.Attacks == null || op.Attacks.Count == 0) continue;
				if (!playerPositions.TryGetValue(bpId, out _)) continue;
				if (!playerTeamIds.TryGetValue(bpId, out int teamId)) continue;
				if (!playerHeroes.TryGetValue(bpId, out Hero hero)) continue;

				foreach (ServerAttack atk in op.Attacks)
				{
					bool isSuper = atk.AttackType == AttackType.Super;
					if (isSuper && !BattleNumericConfig.TryGetSuper((int)hero, out _))
					{
						Logging.Debug.Log($"[SuperBullet][Skip] bp={bpId} attackId={atk.AttackId} hero={hero} reason=missing_super_config");
						continue;
					}

					// 普通攻击与大招在「本次生成多少颗弹」上用的是不同字段（每次发射数 vs 一轮总数），
					// 该差异统一由 ResolveAttack 解释，调用侧不再需要知道查哪张表、读哪个字段。
					ResolvedAttack cfg = BattleNumericConfig.ResolveAttack((int)hero, isSuper);
					int clientFrameId = atk.AttackMoveFrame;
					// 服务端 clamp：防止客户端帧号超过服务端当前帧（丢包恢复后 predicted_frameID 跳跃导致）
					if (clientFrameId > frameid)
					{
						Logging.Debug.Log($"[LagComp] CLAMP clientFrame={clientFrameId} -> {frameid} bp={bpId} attackId={atk.AttackId}");
						clientFrameId = frameid;
						atk.AttackMoveFrame = clientFrameId;
					}

					// 按攻击独立解析出生点：优先使用 clientFrameId 对应的历史快照，缺失时回退到当前权威位置。
					List<ServerBullet> bullets = SpawnServerBullets(bpId, teamId, atk, cfg, clientFrameId);

					// V2 追帧模拟：若 clientFrameId < 当前帧，逐帧追帧模拟
					// 追帧上界为 frameid-1：当前帧(frameid)的推进和碰撞检测由 TickServerBullets 统一处理，
					// 避免同一帧被处理两次（追帧 + TickServerBullets 重复推进/碰撞）。
					int catchUpFrames = frameid - clientFrameId;
					if (clientFrameId > 0 && catchUpFrames > 0)
					{
						int catchUpToFrame = frameid - 1;
						Logging.Debug.Log($"[LagComp] BEGIN catchup bp={bpId} attackId={atk.AttackId} clientFrame={clientFrameId} serverFrame={frameid} catchUpTo={catchUpToFrame} bullets={bullets.Count}");
						foreach (var bullet in bullets)
						{
							bool destroyed = SimulateBulletCatchUp(bullet, clientFrameId, catchUpToFrame, pendingHitEvents);
							if (!destroyed)
							{
								// 追帧完成未命中，加入正常模拟
								activeBullets.Add(bullet);
								Logging.Debug.Log($"[LagComp] SURVIVE attackId={bullet.AttackId} pos=({bullet.Position.X:F2},{bullet.Position.Z:F2}) dist={bullet.TraveledDistance:F2}");
							}
						}
					}
					else
					{
						// 无延迟（clientFrameId == 当前帧），直接加入 activeBullets
						foreach (var bullet in bullets)
							activeBullets.Add(bullet);
					}
				}
			}
		}

		/// <summary>
		/// 根据 ServerAttack 生成服务端子弹。
		/// 每个攻击按各自 clientFrameId 独立解析出生点：优先历史快照，缺失时回退到当前权威位置。
		/// 返回生成的子弹列表，由调用者决定是否做追帧模拟或直接加入 activeBullets。
		/// </summary>
		private List<ServerBullet> SpawnServerBullets(int ownerBattleId, int ownerTeamId,
			ServerAttack atk, ResolvedAttack cfg, int clientFrameId)
		{
			var bullets = new List<ServerBullet>();
			if (!playerPositions.TryGetValue(ownerBattleId, out ServerVector3 spawnPos))
				return bullets;

			// 按攻击帧独立回溯出生点，避免同一批攻击共用当前帧出生位置。
			if (TryGetPositionSnapshot(clientFrameId, out var historicSnapshot)
				&& historicSnapshot.TryGetValue(ownerBattleId, out ServerVector3 historicPos))
			{
				spawnPos = historicPos;
			}

			// 写入 spawn_pos 到 AttackOperation（供下行广播给客户端）
			atk.SpawnPosX = spawnPos.X;
			atk.SpawnPosY = spawnPos.Y;
			atk.SpawnPosZ = spawnPos.Z;
			atk.SpawnServerFrame = frameid;

			// proto 字段语义（俯视角，摇杆轴与世界轴互换）：
			//   towardy = joystickAxis.y -> 世界 X 轴分量
			//   towardx = joystickAxis.x -> 世界 Z 轴分量
			// 客户端消费侧：dir = xAndY2UnitVector3(Towardy, Towardx) 然后 dir.x *= -1 * sign, dir.z *= sign
			// 服务端必须做相同的变换。P3'-3 起这段数学（换轴 + 取反 + 队伍镜像 + 归一化）
			// 在 PMBattleSim.TryGetAimDirection —— 与客户端 BattleFloatMath.ToWorldDirection
			// 是同一份实现（改造前两端各写一份，漂移一处就会持续对不上）。
			float teamSign = 1f;
			if (playerTeamIds.TryGetValue(ownerBattleId, out int bulletTid) && bulletTid != baseTeamId)
				teamSign = -1f;

			float baseDirX, baseDirZ;
			if (!PMBattleSim.TryGetAimDirection(atk.TowardX, atk.TowardY, teamSign, out baseDirX, out baseDirZ))
				return bullets; // 方向无效

			ServerVector3 baseDir = new ServerVector3(baseDirX, 0f, baseDirZ);

			Logging.Debug.Log($"[BulletDir] bp{ownerBattleId} type={atk.AttackType} teamSign={teamSign} raw=({atk.TowardX:F3},{atk.TowardY:F3}) -> dir=({baseDir.X:F3},{baseDir.Z:F3}) pos=({spawnPos.X:F2},{spawnPos.Z:F2})");

			// 散弹扇形生成。用 SpawnBulletCount（本次生成数），不是 BulletCountTotal（一轮总数）。
			// P3'-3：单发（count <= 1）由 PMBattleSim.SpreadDirection **原样返回**基准方向，
			// 所以这里用一个循环盖住「单发」与「扇形」两种情形，不再需要分支 ——
			// 顺带消除了「同一个方向公式在两个分支里各写一遍」这个漂移点。
			int bulletCount = cfg.SpawnBulletCount;
			if (bulletCount < 1)
			{
				bulletCount = 1;
			}

			for (int i = 0; i < bulletCount; i++)
			{
				float dirX, dirZ;
				PMBattleSim.SpreadDirection(baseDir.X, baseDir.Z, cfg.LaunchAngle, bulletCount, i,
					out dirX, out dirZ);

				// 旋转后退化（与改造前 `.Normalized()` 返回零向量的行为一致）——跳过这颗
				if (dirX == 0f && dirZ == 0f)
				{
					continue;
				}

				bullets.Add(CreateServerBullet(ownerBattleId, ownerTeamId, spawnPos,
					new ServerVector3(dirX, 0f, dirZ), cfg, atk.AttackId, clientFrameId));
			}

			return bullets;
		}

		private ServerBullet CreateServerBullet(int ownerBattleId, int ownerTeamId, ServerVector3 pos,
			ServerVector3 dir, ResolvedAttack cfg, int attackId, int clientFrameId)
		{
			var bullet = new ServerBullet
			{
				AttackId = attackId,
				OwnerBattleId = ownerBattleId,
				OwnerTeamId = ownerTeamId,
				Position = pos,
				Direction = dir,
				Speed = cfg.BulletSpeed,
				MaxDistance = cfg.ShootDistance,
				TraveledDistance = 0f,
				Damage = cfg.BulletDamage,
				ClientFrameId = clientFrameId,
			};
			return bullet;
		}

		// ==================== 子弹碰撞检测（共享方法） ====================

		/// <summary>
		/// 检测单颗子弹是否命中目标。
		/// positions: 用于碰撞检测的玩家位置（可以是当前帧或历史帧位置）。
		/// 返回 HitEvent（命中时）或 null（未命中）。同时处理 HP 扣减、击杀判定。
		/// </summary>
		private HitEvent CheckBulletCollision(ServerBullet bullet, Dictionary<int, ServerVector3> positions, int hitFrameId)
		{
			foreach (var kvp in positions)
			{
				int targetBpId = kvp.Key;
				ServerVector3 targetPos = kvp.Value;

				// 跳过自己和队友
				if (targetBpId == bullet.OwnerBattleId) continue;
				if (playerTeamIds.TryGetValue(targetBpId, out int targetTeam) && targetTeam == bullet.OwnerTeamId) continue;
				// 跳过已断线玩家
				if (disconnectedBattlePlayerIds.Contains(targetBpId)) continue;
				// D8: 跳过已死亡玩家
				if (playerIsDead.TryGetValue(targetBpId, out bool isDead) && isDead) continue;

				// 命中半径：用**服务端判定半径**，不是客户端的 ShootWidth（弹体表现宽度）。
				// 两者语义不同，早期注释把二者混为一谈，已在共享表里拆开命名。
				if (!playerHeroes.TryGetValue(bullet.OwnerBattleId, out Hero ownerHero)) continue;
				float hitRadius = BattleNumericConfig.Get((int)ownerHero).ServerHitRadius;

				// 命中判定的公式（球-点距离 ≤ 半径）在 PMBattleSim.IsHit；
				// 「半径取自发射者英雄」是**调用侧的策略**，不放进核心。
				if (PMBattleSim.IsHit(bullet.Position.X, bullet.Position.Y, bullet.Position.Z,
					targetPos.X, targetPos.Y, targetPos.Z, hitRadius))
				{
					// D8: HP 扣减
					bool isKill = false;
					if (playerHp.ContainsKey(targetBpId))
					{
						playerHp[targetBpId] -= bullet.Damage;
						if (playerSuperEnergy != null && playerSuperEnergy.ContainsKey(bullet.OwnerBattleId))
						{
							playerSuperEnergy[bullet.OwnerBattleId] = PMBattleSim.RechargeSuperEnergy(
								playerSuperEnergy[bullet.OwnerBattleId], bullet.Damage,
								BattleNumericConfig.SuperEnergyMax);
						}
						Logging.Debug.Log($"[HP] bp{targetBpId} hp={playerHp[targetBpId]} (dmg={bullet.Damage} from bp{bullet.OwnerBattleId})");

						if (playerHp[targetBpId] <= 0 && !playerIsDead[targetBpId])
						{
							playerIsDead[targetBpId] = true;
							isKill = true;
							_killerBattlePlayerId = bullet.OwnerBattleId;
							_killerTeamId = bullet.OwnerTeamId;
							Logging.Debug.Log($"[KILL] bp{targetBpId} killed by bp{bullet.OwnerBattleId} at frame={hitFrameId}");
						}
					}

					HitEvent evt = new HitEvent
					{
						AttackId = bullet.AttackId,
						AttackerBattleId = bullet.OwnerBattleId,
						VictimBattleId = targetBpId,
						Damage = bullet.Damage,
						HitFrameId = hitFrameId,
						IsKill = isKill,
					};
					Logging.Debug.Log($"[HitEvent] attackId={evt.AttackId} attacker={evt.AttackerBattleId} victim={evt.VictimBattleId} dmg={evt.Damage} frame={evt.HitFrameId} isKill={isKill}");

					// D8: 击杀触发 GameOver
					if (isKill)
					{
						hasAnyPlayerDied = true;
						dic_playerGameOver[targetBpId] = true;
						Logging.Debug.Log($"[GameOver] triggered by kill: victim=bp{targetBpId} killer=bp{bullet.OwnerBattleId} killerTeam={bullet.OwnerTeamId}");
					}

					return evt;
				}
			}
			return null;
		}

		// ==================== 追帧模拟（V2 延迟补偿） ====================

		/// <summary>
		/// 从 fromFrame 到 toFrame 逐帧推进子弹，每帧用 positionHistory[f] 做碰撞检测。
		/// 命中时生成 HitEvent 并返回 true（子弹已销毁），未命中返回 false（子弹存活）。
		/// 注意：当前协议下 HitEvent.HitFrameId 仍记录“本次命中在当前权威处理帧被确认/下发时的 frameid”，
		/// 而不是历史回溯中的物理命中帧 f。若需排查真实回溯命中时机，请结合下方日志里的 historyHitFrame 与 resolvedFrame。
		/// </summary>
		private bool SimulateBulletCatchUp(ServerBullet bullet, int fromFrame, int toFrame, List<HitEvent> hitEvents)
		{
			for (int f = fromFrame; f <= toFrame; f++)
			{
				// 推进子弹位置（步长公式与 TickServerBullets 共用同一份：PMBattleSim.BulletStepDistance）
				float stepDist = PMBattleSim.BulletStepDistance(bullet.Speed, FrameTimeSec);
				bullet.Position = bullet.Position + bullet.Direction * stepDist;
				bullet.TraveledDistance += stepDist;

				// 超距检测
				if (PMBattleSim.IsBulletExpired(bullet.TraveledDistance, bullet.MaxDistance))
				{
					Logging.Debug.Log($"[LagComp] catchup bullet attackId={bullet.AttackId} expired at frame={f} dist={bullet.TraveledDistance:F2}/{bullet.MaxDistance:F2}");
					return true; // 子弹超距销毁
				}

				// 用该帧的历史玩家位置做碰撞检测
				if (TryGetPositionSnapshot(f, out var snapshot))
				{
					HitEvent evt = CheckBulletCollision(bullet, snapshot, frameid); // HitFrameId 仍保持当前权威处理帧；真实历史命中帧见下方日志。
					if (evt != null)
					{
						hitEvents.Add(evt);
						Logging.Debug.Log($"[LagComp] catchup HIT historyHitFrame={f} resolvedFrame={frameid} attackId={bullet.AttackId} victim=bp{evt.VictimBattleId}");
						return true; // 命中，子弹销毁
					}
				}
			}
			return false; // 追帧完成，子弹存活
		}

		// ==================== 子弹推进与碰撞检测 ====================

		private void TickServerBullets(int currentFrameId, List<HitEvent> hitEvents)
		{
			var toRemove = new List<ServerBullet>();

			// 诊断日志：每 120 帧打印一次活跃子弹数量
			if (currentFrameId % 120 == 0 && activeBullets.Count > 0)
				Logging.Debug.Log($"[BulletTick] frame={currentFrameId} activeBullets={activeBullets.Count}");

			foreach (ServerBullet bullet in activeBullets)
			{
				// 推进位置（步长与追帧路径共用同一份：PMBattleSim.BulletStepDistance）
				float stepDist = PMBattleSim.BulletStepDistance(bullet.Speed, FrameTimeSec);
				bullet.Position = bullet.Position + bullet.Direction * stepDist;
				bullet.TraveledDistance += stepDist;

				// 超距检测
				if (PMBattleSim.IsBulletExpired(bullet.TraveledDistance, bullet.MaxDistance))
				{
					toRemove.Add(bullet);
					continue;
				}

				// 碰撞检测（使用当前帧玩家位置）
				HitEvent evt = CheckBulletCollision(bullet, playerPositions, currentFrameId);
				if (evt != null)
				{
					hitEvents.Add(evt);
					toRemove.Add(bullet);
				}
			}

			foreach (ServerBullet b in toRemove)
				activeBullets.Remove(b);
		}
	}
}
