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
    - 节拍：已从 InvokeRepeating 改为 Update 累加器驱动，`currentTickInterval = 0.016f / actualSpeedFactor`（动态调节），每帧最多 3 次 BattleTick()
    - 顺序：DrainAndDispatch → Ping 调度 → CalcTargetFrame + AdjustTickInterval → 累加器循环（BattleTick: ResetOperation → CommandManger.Execute → SendOperation）
    - 上行 `uploadOperationId = predicted_frameID + 1`（动态追帧下可能远大于服务端 frameid），服务端通过 Input Buffer 缓存超前操作、通过 Ack 钳位安全处理
    - **开局握手语义**：客户端初始化后每 200ms 重发一次 `BattleReady`；收到 `BattleStart` 后才停止重发并开启 `_battleTickActive`。若首次 `BattleStart` 丢失，服务端会在后续 `BattleReady` 到达时对该客户端单播补发 `BattleStart`，且不会重复执行 `BeginBattle`
  - **攻击重发与超时**：
    - 客户端每帧重发所有未确认攻击（pendingAttacks），`ClientFrameId` 在入队时锁定不变。
    - 客户端超时清理：帧龄 `(predicted_frameID - ClientFrameId) > 10` 的攻击自动移除。
    - 服务端通过 `dic_lastProcessedAttackId` 去重 + `MaxAcceptableAttackDelay=6` 拒绝过期攻击。

  ### 3.2 权威帧消费与伤害判定

  - 收包入口：Assets/Scripts/Server/Manger/Battle/BattleManger.cs 的 HandleMessage（主线程，由 DrainAndDispatch 调用）。
  - 主处理：BattleData.OnLogicUpdate_sync_FrameIdCheck(...)
    - 先做帧序合法性检查（过旧帧丢弃、空批次推进 sync_frameID）。
    - 预测模式：执行无条件批次和解（回滚 → 批量应用权威帧 → 记录快照 → 重放预测）。
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
  - **死亡判定**：以 `PlayerStates.IsDead` 为权威。`ApplyHitEvents` 中 `IsKill` 兜底作为安全网（IsDead 未到达前的备用路径）。
  - ~~**HP 不同步临时状态**~~ **已解决**：客户端 HP 现在由服务端 `PlayerStates.Hp` 覆写，血条比例显示正确。
  - 和解完成后，外部仅更新 sync_frameID，不再额外逐帧调用 OnLogicUpdate。

  ### 3.3 可调网络参数（客户端）

  文件：Assets/Scripts/Server/ConstValue.cs

  - frameTime = 0.016f **（当前基准逻辑帧长 16ms，动态追帧下实际 tick 间隔可变）**
  - PredictionHistoryWindowSize = 20
  - EnablePredictionReconciliationPipeline = true
  - ReconciliationPositionThreshold = 0.6f （仅监控日志）
  - VisualSmoothingWindowSeconds = 0.1f
  - inputBufferSize = 2 （客户端目标前置缓冲参数；不再等同于服务端移动输入窗口大小）
  - adjustRate = 0.05f、minSpeedFactor = 0.85f、maxSpeedFactor = 1.15f
  - pingIntervalMs = 200f （Ping 发送间隔）

  ## 4. 当前和解策略（authority-state-snapshot 模型）

  - **无条件批次和解**：预测管线开启时，每次收到权威帧都执行完整和解，不再基于状态偏差做条件判定。
  - **权威状态快照锚点**：和解回滚目标为 lastAuthorityStateSnapshot（上次权威帧应用后的世界状态）。首帧无权威快照时退化到预测 bootstrap 锚点。
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
     - server_sync_frameid、上行 OperationID（= predicted_frameID + 1，动态追帧下可能超前服务端多帧）、客户端 sync_frameID 的关系。
  2. **发包频率期望**
     - 客户端 tick 频率动态可变（0.85x ~ 1.15x 基准 60fps），服务端需适应不均匀的上行频率。超前输入通过 Input Buffer 缓存消费。
  3. **目标前置缓冲 vs 服务端移动窗口**
     - 客户端 `inputBufferSize`（ConstValue.cs）用于 `CalcTargetFrame()` 的目标前置缓冲；服务端移动输入窗口由 `Battle.cs` 中 `InputBufferSize=4`、`InputFutureLeadTolerance=6` 单独控制，二者语义已分离，联调时不要再按“必须相等”处理。
  4. **同帧输入一致性**
     - 重点核对客户端自玩家 Battleid 对应的 PlayerOperation。
  5. **权威批次完整性**
     - 当收到权威包 batchCount < requiredHistoryCount 时，客户端会走降级/等待路径，请确认服务端的补帧/全量快照策略。
  6. **结束包边界**
     - 客户端进入 GameOver 后会丢弃大部分战斗包（保留 BattlePushDowmGameOver）。

  ## 6. 协议锚点

  - 协议文件：Assets/Scripts/Server/SocketProto.cs
  - 重点字段：
    - RequestCode
    - ActionCode（含新增 Ping=41, Pong=42）
    - MainPack.timestamp（int64，UDP Ping/Pong 时间戳）
    - BattleInfo.OperationID
    - BattleInfo.AllPlayerOperation
    - PlayerOperation.Battleid
    - AttackOperation.client_frame_id（客户端预测帧号，供服务端 V2 延迟补偿）
    - AttackOperation.spawn_pos_x/y/z（服务端回填的攻击者历史位置，供客户端半空推进）
    - BattleInfo.hit_events（HitEvent 列表，服务端伤害判定结果下行）
  - HitEvent 字段说明：
    - attack_id：攻击唯一 ID（对应 AttackOperation.attack_id）
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
  - `AttackOperation.towardx` = `joystickAxis.x`（对应世界 Z 轴分量）
  - `AttackOperation.towardy` = `joystickAxis.y`（对应世界 X 轴分量）

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

  ## 8. 服务端移动输入窗口（dynamic-tick-adjustment）

  动态追帧下客户端 `uploadOperationId` 可能超前服务端当前 `frameid` 若干帧。服务端现已改为**按 `SyncFrameId` 升序推进的滑动窗口**，不再使用旧的“按 `% inputBufferSize` 写环形 slot”模型：

  - **数据结构**：`Dictionary<int, List<BufferedMoveInput>> dic_movementInputBuffer`，并配套 `dic_lastConsumedMoveFrame`、`dic_lastValidMove`、`dic_consecutiveMissedFrames`（`Battle.cs:49`，`Battle.cs:51`）
  - **窗口参数**：`InputBufferSize=4`、`InputFutureLeadTolerance=6`（`Battle.cs:49-50`）
  - **入队**（`UpdatePlayerOperation`，`BattleController.Network.cs:87`）：
    - 先按客户端显式上报的 `ClientAckedFrame` 清理 `SyncFrameId <= clientAckedFrame - 2` 的旧输入
    - 若 `syncFrameId <= lastConsumedMoveFrame`，直接拒绝并记录 `REJECT_STALE`
    - 同帧输入覆盖更新；新帧插入后按 `SyncFrameId` 升序排序
    - 缓冲超过 4 条时淘汰最小帧号，并记录 `EVICT_OLDEST_ON_FULL`
  - **消费**（`CollectAndBroadcastCurrentFrame`，`Battle.cs:244`）：
    - 优先消费 `lastConsumedMoveFrame + 1`
    - 若下一帧缺失，则取 `> lastConsumedMoveFrame` 且 `<= frameid + 6` 的最小合法帧，并记录 `SKIP_GAP_ACCEPT`
    - 命中严格下一帧时记录 `ACCEPT_IN_ORDER`
    - 成功消费后推进 `lastConsumedMoveFrame`，并移除该输入及其之前更老的输入
  - **缺帧补偿**：没有合法新输入时，继续沿用 `lastValidMove` 做短时惯性，但不推进已消费进度；攻击仍独立走 `dic_pendingAttacks`
  - **Ack 语义**：服务端不再用 `syncFrameId - 1` 反推 ack，而是直接使用客户端显式上报的 `BattleInfo.ClientAckedFrame` 更新 `dic_playerAckedFrameId`（`BattleController.Network.cs:92-105`）
  - **清理**：`HandleBattleEnd` 释放 movement buffer、lastConsumedMoveFrame、lastValidMove、miss 计数（`BattleController.Network.cs:187`）

  ## 9. UDP Ping/Pong 路由（dynamic-tick-adjustment）

  客户端通过 UDP Ping/Pong 测量 RTT，驱动动态追帧的目标帧号计算。

  - **客户端 Ping**：战斗中每 200ms 发送 `ActionCode.Ping`（`MainPack.timestamp` 填当前毫秒时间戳），通过 `UDPSocketManger` 发送
  - **服务端 Pong 路由**（`ClientUdp.cs`）：在 UDP 包路由中识别 `ActionCode.Ping`，构造 Pong 包（`ActionCode.Pong`，`timestamp` 原样回传）。**Pong 发送经过 NetSim**：`LZJUDP` 类持有 `SimDropRate/SimDelayMinMs/SimDelayMaxMs` 公共静态字段，由 `BattleController.BeginBattle` 写入、`HandleBattleEnd` 清零。战斗期间 Pong 与战斗帧共享相同的丢包/延迟模拟参数，确保客户端 RTT 测量反映真实模拟延迟
  - **非战斗 endpoint**：若 endpoint 未关联活跃战斗，忽略 Ping（不回复、不报错）
  - **Proto 扩展**：`MainPack` 新增 `int64 timestamp = 14`，`ActionCode` 新增 `Ping=41`、`Pong=42`

  ## 10. 文档联动约定

  - 会话默认架构说明在：Assets/CLAUDE.md。
  - 本文件提供“服务端协作视角”的细化链路。
  - 若改动涉及两端协议/同步行为，请同步记录：D:/unity/hyld-master/hyld-master/BothSide.md。
