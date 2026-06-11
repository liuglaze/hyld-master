- ForServer.md

  > 面向服务端/联调同学的客户端协作文档（以当前联机主链路为准）。

  ## 1. 文档目标

  - 快速回答三件事：
    1. 客户端现在怎么采集并上报输入（发包频率、输入格式）；
    2. 客户端如何消费权威帧、做和解、以及判定伤害；
    3. 空间坐标系与战斗参数的基准是什么。

  ## 2. 坐标系与空间基准（重要）

  服务端在做反外挂、范围校验或状态同步时，请参考以下客户端基准：

  - **平面与高度**：使用 X-Z 平面。X 为水平，Z 为屏幕纵向（上下）。Y 轴固定高度层为 Y=1。
  - **摄像机视角**：摄像机沿 Z 轴俯视，且 Z 轴锁死（跟随玩家的 X/Y 移动，但不跟随 Z）。
  - **出生点（镜像对称）**：
    - 我方（左侧）：主城/出生点基准 (15, 1, -5) 或 (15, 1, 5) 等。
    - 敌方（右侧）：主城/出生点基准 (-15, 1, 5) 或 (-15, 1, -5) 等。
  - **碰撞判定**：子弹命中判定基于纯距离计算（默认 Distance <= 0.8f 即视为命中）。

  ## 3. 当前客户端战斗架构（2026-03）

  ### 3.1 输入与上报

  - 输入源：Assets/HYLD1.0/Scripts/OldScripts/TouchLogic.cs
    - 移动：连续输入 + 滞回阈值（抑制摇杆边缘抖动），归一化为方向向量。
    - 攻击：松手触发离散输入。
  - 命令聚合：Assets/Scripts/Manger/CommandManger.cs
    - AddCommad_Move 缓存最新移动值（不再按事件频率堆命令）。
    - AddCommad_Attack 以离散命令形式缓存（分配唯一 AttackID）。
  - **动态 Tick 驱动（已变更为累加器 + 动态追帧）**：Assets/Scripts/Server/Manger/Battle/BattleManger.cs
    - 节拍：已从 InvokeRepeating 改为 Update 累加器驱动。BattleStart 后客户端只打开战斗网络泵；首个有效权威帧消费成功后才开启 `_battleTickActive`，随后 `currentTickInterval = 0.016f / actualSpeedFactor`（动态调节），每帧最多 3 次 BattleTick()
    - 顺序：DrainAndDispatch → Ping 调度 → 首权威帧门控 → CalcTargetFrame + AdjustTickInterval → 累加器循环（BattleTick: ResetOperation → CommandManger.Execute → SendOperation）
    - 上行 `uploadOperationId = nextFrame`，严格表示“客户端当前真实推进并上报的本地逻辑帧”。`targetFrame` 仅用于调节 Tick 频率；当客户端落后目标帧时，依靠加快 BattleTick 真实地产出更多连续帧来追赶，而不是篡改上行帧号
    - 动态目标就绪后，客户端最多预测到 `targetFrame + 1`；当 `predicted_frameID >= targetFrame + 1` 时，本帧不再生成新的 BattleTick。
    - 每个 BattleTick 生成项目内 `SavedMove`，记录移动输入、预测起点和预测终点。
    - 客户端维护 `pendingMove`：普通帧先挂起当前 SavedMove，下一帧若输入连续且足够接近则合并成一个 `NewMove`；不可合并时同包按顺序发送 pending + current 两个 `NewMove`，等价于 DualMove，不新增 proto 字段。
    - 每个上行包最多附带 4 个未确认 important `OldMove`，按 `moveFrame` 升序发送；OldMove 不重复选择当前帧或 pendingMove。
    - 停步边沿或新增攻击属于关键输入，会立即 flush pending，并沿用同帧 burst-send。
    - **开局握手语义**：客户端初始化后每 200ms 重发一次 `BattleReady`；收到 `BattleStart` 后停止重发并进入“等待首权威帧”状态。首个有效权威帧到达前客户端不发送 `ClientMove`。若首次 `BattleStart` 丢失，服务端会在后续 `BattleReady` 到达时对该客户端单播补发 `BattleStart`，且不会重复执行 `BeginBattle`
    - **预测暂停语义**：若权威帧超过 1000ms 未刷新，或客户端 SavedMove 历史达到 `PredictionHistoryWindowSize`，客户端暂停生成新的预测 tick；收到新权威帧或 MoveAck 裁剪历史后从下一帧继续，不补跑暂停期间累计的 tick。
  - **攻击重发与超时**：
    - 客户端每帧重发所有未确认攻击（pendingAttacks），`ClientFrameId` 在本次 BattleTick 的 `nextFrame` 入队时锁定不变。
    - 客户端超时清理：帧龄 `(predicted_frameID - ClientFrameId) > 8` 的攻击自动移除。
    - 服务端通过 `dic_lastProcessedAttackId` 去重 + `MaxAcceptableAttackDelay=8` 拒绝过期攻击。
  - **关键输入同帧 burst-send**：
    - 若本地 BattleTick 发生“停步边沿”（non-zero -> zero）或本帧新增攻击，则客户端会在同一逻辑帧内把同一份 `BattleInfo` 连发 3 次。
    - 3 次发送复用相同 `OperationID`、`ClientAckedFrame`、`ClientMoveFrame`、移动值、OldMove 列表与 `AttackId` 集合，不引入新协议字段。
    - 服务端保持现有幂等语义：重复 `ClientMoveFrame` 会被 `[ClientMove][STALE]` 丢弃，重复攻击仍由 `AttackId` 去重。

  ### 3.2 权威帧消费与伤害判定

  - 收包入口：Assets/Scripts/Server/Manger/Battle/BattleManger.cs 的 HandleMessage（主线程，由 DrainAndDispatch 调用）。
  - 主处理：BattleData.OnLogicUpdate_sync_FrameIdCheck(...)
    - 先做帧序合法性检查（过旧帧丢弃、空批次推进 sync_frameID）。
    - 预测模式：远端玩家直接应用权威位置；本地玩家只消费服务端 `BattleInfo.move_ack`。`ack_good_move=true` 只解链，`ack_good_move=false` 使用 `correct_pos_x/y/z` 校正并重放未确认 SavedMove。
    - 权威帧处理后生成**视觉子弹**（SpawnVisualBullet，仅表现，不判伤害）。
  - **伤害判定（服务端权威）**：
    - 服务端 BattleController 每帧做服务端子弹模拟（ServerBullet）。
    - **V2 延迟补偿**：收到延迟攻击时，从 `positionHistory[clientFrameId]` 取历史位置生成子弹，`SimulateBulletCatchUp`（BattleController.Bullets.cs:233）逐帧追帧模拟（每帧用该帧的历史玩家位置做碰撞检测）。追帧期间命中直接生成 HitEvent；未命中的子弹加入 activeBullets 正常模拟。
    - `CheckBulletCollision`（BattleController.Bullets.cs:163）为碰撞检测共享方法，`TickServerBullets` 和 `SimulateBulletCatchUp` 复用。
    - `SpawnServerBullets`（BattleController.Bullets.cs:70）将攻击者历史位置写入 `atk.SpawnPosX/Y/Z`，随帧下行广播。
    - 命中后生成 HitEvent，随 BattleInfo.hit_events 下行广播给所有客户端。
    - 客户端 HandleMessage 解析 hit_events，调用 BattleData.ApplyHitEvents(...)，仅触发受击动画（不扣血）。
    - ApplyHitEvents 在主线程执行：查找 victim → 受击动画（不修改 HP）→ 记录 hitAnimatedPlayers。
    - 去重机制：_appliedHitEventKeys（HashSet<long>，key=attackId*100000+victimBattleId），防止帧重传重复播放动画。
  - **权威 HP/IsDead 消费**：ApplyHitEvents 之后调用 `ApplyAuthoritativeHpAndDeath`，从 `PlayerStates.Hp` 覆写客户端 `playerBloodValue`，从 `PlayerStates.IsDead` 驱动死亡判定。首帧初始化 `maxHp` 和 `hero.BloodValue`（血条比例）。HP 下降但无 HitEvent 时补播兜底受击动画。
  - **PlayerStates 增量下行**：服务端仍每个 ServerFrame 下发权威帧时钟包，但 `BattleFrame.player_states` 可只携带变化字段。客户端先按 `AuthoritativePlayerState.state_mask` 合并到本地权威状态缓存，再把完整状态视图交给位置、HP、死亡和视觉子弹逻辑。`state_mask` bit0=position，bit1=hp，bit2=is_dead；客户端不通过 protobuf 默认值判断字段是否存在。
  - **死亡判定**：以 `PlayerStates.IsDead` 为权威。`ApplyHitEvents` 中 `IsKill` 兜底作为安全网（IsDead 未到达前的备用路径）。
  - ~~**HP 不同步临时状态**~~ **已解决**：客户端 HP 现在由服务端 `PlayerStates.Hp` 覆写，血条比例显示正确。
  - 和解完成后，外部仅更新 sync_frameID，不再额外逐帧调用 OnLogicUpdate。

  ### 3.3 可调网络参数（客户端）

  文件：Assets/Scripts/Server/ConstValue.cs

  - frameTime = 0.016f **（当前基准逻辑帧长 16ms，动态追帧下实际 tick 间隔可变）**
  - PredictionHistoryWindowSize = 20
  - EnablePredictionReconciliationPipeline = true
  - ReconciliationPositionThreshold = 0.6f （历史参数；当前本地玩家是否校正由服务端 `move_ack.ack_good_move` 决定）
  - VisualSmoothingWindowSeconds = 0.1f
  - inputBufferSize = 4 （历史参数，当前仅保留为兼容配置项，不再直接参与 targetFrame 公式）
  - targetFrameSafetyFrames = 1 （历史兼容参数；当前不再直接参与 targetFrame 公式）
  - adjustRate = 0.05f、minSpeedFactor = 0.85f、maxSpeedFactor = 1.15f
  - pingIntervalMs = 200f （Ping 发送间隔）
  - AuthorityStalePauseMs = 1000f （客户端超过 1000ms 没有新权威帧时暂停预测 tick）

  ## 4. 当前和解策略（authority-state-snapshot 模型）

  - **服务端 MoveAck 驱动本地校正**：远端玩家始终应用服务端权威位置；本地玩家收到 `move_ack.ack_good_move=false` 才拉回到 `correct_pos_x/y/z` 并 replay。
  - **权威状态快照锚点**：和解回滚目标为 lastAuthorityStateSnapshot（上次权威帧应用后的世界状态）。首个本地预测 tick 前必须已经消费过有效权威帧。
  - **彻底剥离渲染与逻辑**（子线程崩溃修复）：HandleMessage 通过 DrainAndDispatch 在主线程执行，视觉子弹生成（SpawnVisualBullet）、ApplyHitEvents 和 ApplyAuthoritativeHpAndDeath 均在主线程直接调用，无需 AddAction。
  - **子弹与和解**：
    - 视觉子弹在和解时**不会被销毁**，保留在对象池中继续飞行，避免网络波动导致满屏子弹闪烁。
    - 伤害判定完全由服务端 HitEvent 驱动，不受和解影响。
  - **移除旧机制**：
    - inputMatched 已降级为纯监控日志，不影响和解决策。
    - skipSelf 模式已完全移除，和解时全量应用所有人的权威操作。
    - isNotDie（死亡状态）属于主线程渲染 Guard 控制，不参与帧同步快照与恢复。
    - AuthorityBullet 客户端子弹系统已完全删除，伤害判定由服务端 HitEvent 驱动。

  ## 5. 协作检查清单（服务端联调时）

  1. **帧与操作ID语义**
     - ServerFrame：服务端 `frameid`，下行 `BattleInfo.server_frame` 与 `BattleFrame.server_frame`。
     - ClientMoveFrame：客户端本地预测 tick，写入 `ClientMove.move_frame`。
     - ClientAckedFrame：客户端已应用的最新 ServerFrame，写入 `BattleInfo.client_input.acked_server_frame`。
     - MoveAck：服务端已处理的最新 ClientMoveFrame 与确认/修正结果，下行 `BattleInfo.server_update.move_ack`。
  2. **发包频率期望**
     - 客户端 BattleStart 后先等待首个有效权威帧；首权威帧到达前不会发送 `ClientMove`。首权威帧到达后先按固定 `frameTime` 发送移动，RTT 就绪后 tick 频率动态可变。上行移动以 `ClientMove` 单调排序；服务端接收合法 move 后入队，随后每个 ServerFrame 按固定预算推进权威位置。
  3. **目标帧余量 vs ClientMoveFrame**
     - 客户端 `CalcTargetFrame()` 取 `EstimateServerFrameNow()`，只调节本地 tick 速度，不决定服务端移动消费帧。
     - 首个权威帧未到时，客户端不计算目标帧，也不生成 ClientMoveFrame。RTT 未初始化但已有首权威帧时，客户端按固定 tick 生成真实 ClientMoveFrame。
     - 客户端不会把服务端 `ServerFrame` 写入 `predicted_frameID`；`ClientMoveFrame` 只由本地 BattleTick 连续推进。
     - `inputBufferSize` 是历史兼容配置项，服务端不再按 Input Buffer 消费移动。
  4. **同帧输入一致性**
     - 重点核对客户端 `BattleInfo.client_input.battle_player_id`，移动在 `client_input.moves`，攻击在 `client_input.attacks`。
  5. **权威批次完整性 / 当前帧重复下发**
     - 服务端已从“按客户端 ack 组织最近窗口补帧”切换为“每个权威帧只下发当前帧，但在同一帧内重复发送多次”。客户端应支持 `BattleInfo.server_update.frames` 长期仅含 1 个当前帧元素，并正确处理同一当前帧的重复到达。
     - `PlayerStates` 已支持按接收客户端的 acknowledged state baseline 增量下发；即使本帧没有状态变化，也仍会下发当前 `server_frame`，避免影响动态追帧对时。
  6. **结束包边界**
     - 客户端进入 GameOver 后会丢弃大部分战斗包（保留 BattlePushDowmGameOver）。

  ## 6. 协议锚点

  - 协议文件：Assets/Scripts/Server/SocketProto.cs
  - 重点字段：
    - RequestCode
    - ActionCode（含新增 Ping=41, Pong=42）
    - MainPack.timestamp（int64，UDP Ping/Pong 时间戳）
    - BattleInfo.server_frame
    - BattleInfo.client_input（battle_player_id / client_tick / acked_server_frame / rtt_ms / moves / attacks）
    - BattleInfo.server_update（frames / move_ack / hit_events / state_base_frame）
    - AuthoritativePlayerState.state_mask（bit0=position，bit1=hp，bit2=is_dead）
    - ClientMove.move_frame
    - ClientAttack.attack_move_frame
    - ServerAttack.spawn_pos_x/y/z 与 spawn_server_frame
  - HitEvent 字段说明：
    - attack_id：攻击唯一 ID（对应 ClientAttack / ServerAttack 的 attack_id）
    - attacker_battle_id：攻击者 battleId
    - victim_battle_id：被攻击者 battleId
    - damage：本次扣血量
    - hit_frame_id：服务端判定命中的帧号
    - hit_pos_x/y/z：命中位置（用于特效）
    - is_kill：是否击杀
  - 位置历史缓存接口（服务端）：
    - positionHistory[frameId][battleId] = ServerVector3
    - 环形窗口 N=30 帧，超出自动淘汰最旧帧
    - TryGetPositionSnapshot(frameId, out snapshot)：供 V2 延迟补偿回溯历史位置

  ## 7. 攻击方向编解码对齐（重要）

  **proto 字段语义（摇杆轴与世界轴互换）**：
  - `ClientAttack.toward_x` / `ServerAttack.toward_x` = `joystickAxis.x`（对应世界 Z 轴分量）
  - `ClientAttack.toward_y` / `ServerAttack.toward_y` = `joystickAxis.y`（对应世界 X 轴分量）

  **客户端消费**（HYLDPlayerManger / BattleManger）：
  ```
  dir = xAndY2UnitVector3(Towardy, Towardx)  // (sin, 0, cos) 基于 atan2
  dir.x *= -1 * sign   // sign=1 同队, sign=-1 对方队
  dir.z *= sign
  ```

  **服务端消费**（BattleController.Bullets.cs:70 SpawnServerBullets）：
  ```
  teamSign = (playerTeam != baseTeamId) ? -1 : 1
  baseX = -Towardy * teamSign   // 取反 + 队伍镜像
  baseZ = Towardx * teamSign    // 队伍镜像
  baseDir = Normalize(baseX, 0, baseZ)
  ```

  **关键点**：服务端必须同时做 X 轴取反（对应客户端 `dir.x *= -1`）和队伍镜像翻转（对应客户端 `sign=-1`），与移动方向的 teamSign 逻辑一致。

  ## 8. 服务端 ClientMove 移动时间轴（CMC-style）

  服务端不再使用移动 Input Buffer。客户端上行移动写入 `BattleInfo.client_input.moves`，服务端按 ClientMoveFrame 单调接受合法 Move，并在 BattleLoop 中慢消化 pending move segment。

  - **数据结构**：每个玩家维护 `dic_lastAcceptedMoveFrame`、`dic_lastSimulatedMoveFrame`、`dic_pendingMoveSegments`、`dic_lastProcessedMoveInput` 与最新 `MoveAckResult`。
  - **接收**（`UpdatePlayerOperation` / `ProcessClientMove`）：
    - 先处理所有 `OldMove`，再按包内顺序处理所有非 `OldMove`。客户端 DualMove 用两个顺序 `NewMove` 表达，服务端无需新增协议枚举。
    - `moveFrame <= lastAcceptedMoveFrame` 直接丢弃并记录 `[ClientMove][STALE]`。
    - `moveFrame > serverFrame + 2` 直接拒绝并记录 `[ClientMove][REJECT_FUTURE]`。
    - 合法 move 按 `moveFrame - lastAcceptedMoveFrame` 入队为 pending segment，并更新 `lastAcceptedMoveFrame`。
  - **消费**（`CollectAndBroadcastCurrentFrame`）：
    - 每个 ServerFrame 对每个玩家最多消化 `MaxMoveApplyFramesPerServerFrame = 3` 帧 pending move，推进 `playerPositions` 与 `lastSimulatedMoveFrame`。
    - 每个 ServerFrame 将当前移动意图打包进权威帧，供客户端动画使用。
    - `RecordPositionSnapshot(frameid)` 记录当前服务端权威位置历史。
  - **Ack 语义**：
    - 服务端使用客户端上行 `ClientAckedFrame` 更新下行权威帧确认统计。
    - 服务端下行 `MoveAckResult.AckedMoveFrame = lastSimulatedMoveFrame`，客户端只删除已经被服务端权威位置实际模拟过的 SavedMove。
    - `ack_good_move=true`：客户端只解链。
    - `ack_good_move=false`：客户端校正到 `correct_pos_x/y/z`，再重放剩余 SavedMove。

  ## 9. UDP Ping/Pong 路由（dynamic-tick-adjustment）

  客户端通过 UDP Ping/Pong 测量 RTT，驱动动态追帧的目标帧号计算。

  - **客户端 Ping**：战斗中每 200ms 发送 `ActionCode.Ping`（`MainPack.timestamp` 填当前毫秒时间戳），通过 `UDPSocketManger` 发送
  - **服务端 Pong 路由**（`ClientUdp.cs`）：在 UDP 包路由中识别 `ActionCode.Ping`，构造 Pong 包（`ActionCode.Pong`，`timestamp` 原样回传）。**Pong 发送经过 NetSim**：`LZJUDP` 类持有 `SimDropRate/SimDelayMinMs/SimDelayMaxMs` 公共静态字段，由 `BattleController.BeginBattle` 写入、`HandleBattleEnd` 清零。战斗期间 Pong 与战斗帧共享相同的丢包/延迟模拟参数，确保客户端 RTT 测量反映真实模拟延迟
  - **非战斗 endpoint**：若 endpoint 未关联活跃战斗，忽略 Ping（不回复、不报错）
  - **Proto 扩展**：`MainPack` 新增 `int64 timestamp = 14`，`ActionCode` 新增 `Ping=41`、`Pong=42`

  - **当前帧重复下发**：
    - 服务端 `BattleController.Network.SendUnsyncedFrames` 不再按 `ClientAckedFrame` 组织最近窗口补帧，而是每个权威帧只发送当前 `frameid`。
    - 同一当前帧会在服务端侧按可调参数重复发送（当前实现默认 `CurrentFrameRepeatSendCount = 3`，可调 1~N，建议压测区间 3~5）。
    - `BattleInfo.HitEvents` 与 `PlayerStates` 会随当前帧一起重复到达；客户端继续依赖既有去重与幂等消费逻辑处理。

  ## 10. 文档联动约定

  - 会话默认架构说明在：Assets/CLAUDE.md。
  - 本文件提供“服务端协作视角”的细化链路。
  - 若改动涉及两端协议/同步行为，请同步记录：D:/unity/hyld-master/hyld-master/BothSide.md。
