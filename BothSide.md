# BothSide.md — 两端协议与同步语义记录

> 记录客户端与服务端之间需要双方对齐的协议字段、同步语义、约定变更。

---

## 0. 当前 BattleInfo 单包结构（battle-protocol-split，2026-06）

- **外层不变**：战斗 UDP 仍然通过一个 `MainPack` 收发，`RequestCode.Battle` + 原有 `ActionCode` 不变，战斗载荷仍在 `MainPack.battleInfo`。
- **BattleInfo 当前结构**：
  - `server_frame`：服务端权威帧号；下行权威帧包填写，上行可为 0。
  - `rand_seed`、`battle_users`：开局初始化信息。
  - `client_input`：客户端上行输入，包含 `battle_player_id`、`client_tick`、`acked_server_frame`、`rtt_ms`、`moves`、`attacks`。
  - `server_update`：服务端下行更新，包含 `frames`、`move_ack`、`hit_events`。
- **移动上行**：只走 `BattleClientInput.moves`（`ClientMove`），按 `move_frame` 做 CMC-style 单调处理。
- **攻击上行**：只走 `BattleClientInput.attacks`（`ClientAttack`），字段为 `attack_id`、`attack_move_frame`、`toward_x/y`。
- **权威下行**：只走 `BattleServerUpdate.frames`（`BattleFrame`），每帧包含 `player_inputs` 与 `player_states`；攻击表现使用 `ServerAttack`，包含 `spawn_pos_x/y/z` 与 `spawn_server_frame`。
- **已删除旧字段/旧类型**：`BattleInfo.selfOperation`、`BattleInfo.client_moves`、`BattleInfo.client_acked_frame`、`BattleInfo.client_rtt_ms`、`BattleInfo.acked_move_frame`、顶层 `BattleInfo.move_ack`、顶层 `BattleInfo.hit_events`、`BattleFrameSync`、`PlayerOperation`、`AttackOperation`。
- **不做兼容兜底**：客户端与服务端必须由同一份权威 `SocketProto.proto` 生成后一起运行。

---

## 1. AttackOperation.client_frame_id（新增字段）

> 历史记录：当前协议已由 `ClientAttack.attack_move_frame` / `ServerAttack.attack_move_frame` 取代 `AttackOperation.client_frame_id`。

- **proto 字段**：`AttackOperation.client_frame_id = 4`（int32）
- **语义**：客户端构造 AttackOperation 时，填入当前 `BattleData.predicted_frameID`，表示攻击发出时客户端的预测帧号。
- **客户端写入位置**：`BattleData.FlushPendingAttacksToOperation()` — `attackOp.ClientFrameId = predicted_frameID`
- **服务端用途**：V1 阶段忽略（服务端当前使用实时位置判定）；V2 延迟补偿时服务端可以用此字段回溯到 `client_frame_id` 对应的历史玩家位置做碰撞检测。
- **引入版本**：server-authoritative-damage（2026-03）

---

## 2. HitEvent（新增消息）

- **proto 消息**：`message HitEvent`，字段：
  - `attack_id`（int32）：攻击唯一 ID，对应 AttackOperation.attack_id
  - `attacker_battle_id`（int32）：攻击者 battleId
  - `victim_battle_id`（int32）：被攻击者 battleId
  - `damage`（int32）：扣血量
  - `hit_frame_id`（int32）：服务端判定命中的帧号
  - `hit_pos_x/y/z`（float）：命中世界坐标
  - `is_kill`（bool）：是否击杀
- **下行路径**：服务端每帧子弹模拟命中 → 生成 HitEvent → 存入当前帧结果 → 随 BattleInfo.hit_events 广播所有客户端
- **BattleInfo 字段**：`repeated HitEvent hit_events = 6`
- **客户端消费**：`HandleMessage` 收到 BattlePushDowmAllFrameOpeartions 后调用 `BattleData.ApplyHitEvents(pack.BattleInfo.HitEvents)`
- **去重机制**：`_appliedHitEventKeys`（HashSet<long>，key = `attackId * 100000 + victimBattleId`），防止帧重传重复扣血
- **引入版本**：server-authoritative-damage（2026-03）

---

## 3. 伤害判定权威化（架构变更记录）

- **变更前**：客户端维护 AuthorityBullet 纯数据系统，在客户端执行子弹模拟和碰撞检测，产生本地伤害判定结果。各客户端独立判定，可能产生不一致。
- **变更后**：伤害判定完全由服务端 BattleController 执行（ServerBullet 模拟 + 碰撞检测），结果通过 HitEvent 下行广播所有客户端，客户端统一消费。客户端子弹系统（视觉子弹）保留，仅作表现层，不参与任何伤害计算。
- **客户端删除内容**：AuthorityBullet 类、authorityBullets 列表、SpawnAuthorityBullet()、TickAuthorityBullets()、ClearAuthorityBullets()
- **引入版本**：server-authoritative-damage（2026-03）

---

## 4. ActionCode.BattlePushDownHitEvents = 40（预留）

- 当前 V1 阶段 HitEvent 搭载在帧下行包（BattlePushDowmAllFrameOpeartions = 33）中随 BattleInfo.hit_events 下行。
- ActionCode.BattlePushDownHitEvents = 40 预留供未来独立下行 HitEvent 通道使用。

---

## 5. 服务端子弹方向镜像修复（两端对齐）

- **问题**：服务端 `SpawnServerBullets` 直接使用 proto 原始值 `(Towardy, Towardx)` 作为子弹方向，未做 X 轴取反和队伍镜像翻转，导致非基准队伍子弹方向反转。
- **修复**：服务端新增与客户端一致的方向变换（`BattleController.Bullets.cs:70 SpawnServerBullets`）：
  - `baseX = -atk.Towardy * teamSign`（X 轴取反 + 队伍镜像）
  - `baseZ = atk.Towardx * teamSign`（队伍镜像）
  - `teamSign = (playerTeam != baseTeamId) ? -1 : 1`
- **客户端对应代码**：`dir.x *= -1 * sign; dir.z *= sign`（HYLDPlayerManger / BattleManger）
- **约定**：后续修改方向编解码时，必须同时更新两端对应代码。
- **修复版本**：server-authoritative-damage（2026-03）

---

## 6. 服务端 HP 临时降低（测试阶段）

- **变更**：服务端 `HeroConfig._hpConfig` 全英雄 HP 临时降至约原值 1/5（例：XueLi 4680 → 960）
- **原因**：原始 HP 下需要多轮攻击才能击杀，每次攻击生成大量视觉子弹 GameObject，累积导致 Editor 崩溃
- **客户端影响**：~~客户端 `HYLDStaticValue` 中英雄 `BloodValue` 仍为原始值（4680 等），血条显示比例不匹配~~ **已解决**：客户端 `ApplyAuthoritativeHpAndDeath` 首帧从服务端 `PlayerStates.Hp` 初始化 `maxHp` 和 `hero.BloodValue`，血条比例自动对齐
- **死亡判定不受影响**：客户端死亡判定以服务端 `PlayerStates.IsDead` 为权威
- **后续计划**：实现对象池优化后恢复原始 HP 值
- **修复版本**：server-authoritative-damage（2026-03）

---

## 7. 客户端死亡判定权威化（D8 → AHS 升级）

- **变更前**：客户端 `ApplyHitEvents` 依赖 `playerBloodValue <= 0` 或 `evt.IsKill` 判定死亡
- **变更后（authoritative-hp-sync）**：
  - `ApplyHitEvents` 降级为纯表现层（仅触发受击动画，不修改 HP）
  - HP 唯一修改来源：`ApplyAuthoritativeHpAndDeath`，从服务端 `PlayerStates.Hp` 覆写
  - 死亡判定权威来源：`PlayerStates.IsDead`（由 `ApplyAuthoritativeHpAndDeath` 消费）
  - `ApplyHitEvents` 中 `IsKill` 兜底保留为安全网（IsDead 未到达前的备用路径），后续可移除
- **强制 HP 置零**：当 `IsDead=true` 且 `isNotDie==true` 时，强制 `playerBloodValue = -1`，确保 `PlayerLogic.playerDieLogic()` 触发
- **修复版本**：server-authoritative-damage（2026-03），authoritative-hp-sync（2026-03）

---

## 8. PlayerStates 权威 HP/IsDead 下行（authoritative-hp-sync，2026-03）

- **proto 字段**：`PlayerState` 消息新增 `hp`（int32）和 `is_dead`（bool）
- **服务端写入**：`PackPlayerStates`（BattleController.Network.cs:18）每帧将 `playerHp[battleId]` 和 `playerIsDead[battleId]` 写入 `AllPlayerOperation.PlayerStates`
- **客户端消费**：`ApplyAuthoritativeHpAndDeath` 从批次最后一帧的 `PlayerStates` 覆写 `playerBloodValue`，并以 `IsDead` 驱动死亡判定
- **帧序保护**：`_lastAuthHpFrameId` 跳过乱序到达的旧批次，防止 UDP 包乱序导致 HP 回弹
- **首帧初始化**：首次到达时从服务端 HP 初始化 `maxHp` 和 `hero.BloodValue`，统一两端 HP 基准
- **兜底受击动画**：HP 下降但无 HitEvent 时，`ApplyAuthoritativeHpAndDeath` 补播 `SetTrigger("Hit")`
- **与 HitEvent 的分工**：
  - `ApplyHitEvents`：纯表现层，仅触发受击动画，不修改 HP
  - `ApplyAuthoritativeHpAndDeath`：HP 覆写 + 死亡判定
- **引入版本**：authoritative-hp-sync（2026-03）

---

## 9. MainPack.timestamp（新增字段，dynamic-tick-adjustment）

- **proto 字段**：`MainPack.timestamp = 14`（int64）
- **语义**：UDP Ping/Pong 时间戳，客户端发送 Ping 时填入当前毫秒时间戳，服务端 Pong 原样回传
- **客户端写入**：BattleManger 的 Ping 调度逻辑，`pack.Timestamp = 当前毫秒时间戳`
- **服务端消费**：`ClientUdp.cs` 收到 ActionCode.Ping 后，构造 Pong 包原样回传 timestamp
- **用途**：客户端计算 `rttSample = localNow - pong.timestamp`，EWMA 平滑后驱动动态追帧
- **引入版本**：dynamic-tick-adjustment（2026-03）

---

## 10. ActionCode.Ping / Pong（新增枚举，dynamic-tick-adjustment）

- **ActionCode.Ping = 41**：客户端→服务端，UDP Ping 探测
- **ActionCode.Pong = 42**：服务端→客户端，UDP Pong 回复
- **Proto 位置**：`enum ActionCode` 新增 `Ping = 41; Pong = 42;`
- **引入版本**：dynamic-tick-adjustment（2026-03）

---

## 11. 帧时长统一（两端对齐，dynamic-tick-adjustment）

- **变更前**：客户端 `ConstValue.frameTime = 0.0167f`
- **变更后**：客户端 `ConstValue.frameTime = 0.016f`（16ms），与服务端 `FRAME_INTERVAL_MS = 16` 精确对齐
- **影响**：动态追帧的 `CalcTargetFrame` 公式 `rttFrames = smoothedRTT / (frameTime * 1000)` 依赖两端帧时长一致
- **引入版本**：dynamic-tick-adjustment（2026-03）

---

## 12. uploadOperationId 语义变更（dynamic-tick-adjustment）

- **变更前**：`uploadOperationId = sync_frameID + 1`（跟随服务端下发帧号）
- **变更后**：`uploadOperationId = nextFrame`（= `predicted_frameID + 1`，客户端本地预测帧号 + 1）
- **影响**：动态追帧下 `uploadOperationId` 可能远大于服务端当前 `frameid`
- **服务端兼容**：
  - `OperationID` 不再作为服务端移动消费目标帧；移动以上行 `BattleInfo.client_moves` 为准
  - `ClientAckedFrame` 只表示客户端已应用的服务端权威帧，用于下行确认统计
- **引入版本**：dynamic-tick-adjustment（2026-03）

---

## 13. 服务端 Input Buffer（历史记录，已废弃）

- 本节记录 dynamic-tick-adjustment 早期方案，当前不再使用。
- 当前移动同步以第 25 节 `ClientMoveFrame` 方案为准。
- 服务端不再维护 `dic_movementInputBuffer`、`lastValidMove` 或按 `SyncFrameId == frameid` 精确命中消费。
- **引入版本**：dynamic-tick-adjustment（2026-03）

---

## 14. 战斗期 UDP 统一 NetSim（unify-battle-udp-netsim，2026-03）

- **问题**：原实现中 Pong 与权威帧分别走局部模拟分支，RTT 采样、上行操作到达时机、下行权威帧/控制包发送时机不在同一套 battle-scoped 网络模型下，导致客户端追帧依据与实际战斗帧节奏脱节。进一步压测后确认，旧版“每包 `ThreadPool + Sleep`”延迟实现还会把线程调度排队误差混入 RTT，形成“前期爆卡、后期突然顺滑”的假性高 RTT 污染。
- **修复**：
  - `LZJUDP` 新增统一 battle UDP NetSim 入口，对 **上行收包前** 与 **下行发包前** 都执行相同的 battle-scoped 判定
  - 按 `ActionCode` 明确策略分层：
    - `BattleReady`：建链保护策略（进入统一框架，但不采用激进丢包）
    - `BattlePushDowmPlayerOpeartions`、`BattlePushDowmAllFrameOpeartions`、`Ping`、`Pong`：数据包策略（完整 delay/drop/jitter）
    - `BattleStart`、`ClientSendGameOver`、`BattlePushDowmGameOver`：控制包策略（允许延迟，默认保守丢包）
  - `BattleController.Handle` 在全员 `BattleReady` 后预写入 NetSim 参数，使 `BattleStart` 也进入统一入口
  - `BattleController.Network.HandleBattleEnd` 先调度 `BattlePushDowmGameOver`，再清理 NetSim 与 battle 路由，保证收尾控制包仍受统一框架管理
  - 删除旧的 Pong 局部模拟分支与权威帧局部 delay/drop 分支，权威帧 drop 改为由 `LZJUDP.Send` 中的统一入口真实跳过发送
  - `ClientUdp.cs` 的延迟执行从“每包 `ThreadPool + Sleep`”改为“统一调度线程 + 延迟队列”，避免 ThreadPool backlog 污染 RTT 与 battle 帧节奏
  - 统一日志格式：`[BattleNetSim] dir=... action=... battleId=... endpoint=... strategy=... decision=... delayMs=...`
- **客户端影响**：无需新增本地 NetSim 模块。现有 Ping/Pong RTT 平滑、`CalcTargetFrame()`、`AdjustTickInterval()` 直接观测统一后的 battle 网络条件
- **联调关注点**：
  - `BattleManger.cs` 中 RTT 现在代表“统一 NetSim 下的战斗期往返时延”而不再只是 Pong 特例
  - 权威帧与 RTT 都受同一套 battle-scoped 参数影响，因此追帧速度与权威帧到达节奏应更一致
  - 经实测，在 `8% + 70~100ms` 与 `10% + 80~120ms` 两档下，整体仍保持顺滑，说明当前主瓶颈已不再是 NetSim 调度污染
  - 非战斗 UDP 保持原行为，不进入 battle-scoped NetSim
- **修复版本**：unify-battle-udp-netsim（2026-03）

---

## 15. BattleStart 补发语义（battle-start-resend, 2026-03）

- **问题**：`BattleStart` 为 UDP 控制包，原实现仅在“首次全员 ready”时广播一次；若某客户端首包丢失，该客户端会继续周期性发送 `BattleReady`，但服务端此前在 `isAllReady` 后直接忽略后续 `BattleReady`，导致客户端可能永久停留在等待开局阶段。
- **服务端修复**：`BattleController.Handle` 中将“全员 ready”与“战斗已开始”拆分：
  - 首次全员 ready：广播 `BattleStart` 并执行一次 `BeginBattle`
  - 战斗已开始后：若某客户端继续发送 `BattleReady`，服务端视为该客户端可能未收到 `BattleStart`，对该 endpoint **单播补发** `BattleStart`
  - 补发只重发 `BattleStart`，**不会重复执行** `BeginBattle`
- **客户端现状**：客户端 `BattleManger.Send_BattleReady()` 会每 200ms 重发 `BattleReady`；收到 `BattleStart` 后 `HandleBattleStart()` 立即 `CancelInvoke("Send_BattleReady")` 并开启 `_battleTickActive`。因此无需新增协议字段，即可形成“BattleReady 重试 → BattleStart 补发”的弱可靠开局握手。
- **联调结论**：已验证首次 `BattleStart` 未生效时，客户端后续 `BattleReady` 可触发服务端补发，并成功进入战斗；服务端 `BeginBattle` 仅执行一次。
- **影响范围**：属于两端状态流语义修复，不涉及 protobuf 变更。

---

## 16. strict target frame + strict consume（2026-04）

> 历史记录：本节的服务端 strict current-frame consume 已被第 25 节 CMC-style ClientMove 方案替换。

- **客户端目标帧变更**：
  - `CalcTargetFrame()` 从 `sync_frameID + ceil(RTT/2) + inputBufferSize` 改为 `estimatedServerFrame + ceil(halfRTT) + jitterBufferFrames + safetyFrames`
  - 当前实现中 RTT 和首个权威帧未初始化时，客户端不计算目标帧，但 BattleStart 后仍按固定 `frameTime` 推进并发送 `ClientMove`
  - `inputBufferSize` 不再直接参与 targetFrame 公式，仅保留为兼容配置项
- **OperationID 新语义**：
  - 客户端上行 `uploadOperationId = nextFrame`
  - 语义为“当前 BattleTick 真实推进并上报的本地逻辑帧号”；`targetFrame` 仅用于调节客户端 Tick 频率，不再参与改写上行帧号
- **服务端移动输入消费变更（历史）**：
  - 早期方案使用 `dic_movementInputBuffer` 维护 future input，当前已删除。
  - 当前服务端只保存每个玩家最新合法 `ClientMove` 对应的移动意图，合法 move 在接收阶段直接推进权威位置。
- **跨端约定（当前以第 25 节为准）**：
  - 客户端 `targetFrame` 只调节本地 tick 速度，不决定服务端移动消费帧。
  - 服务端权威历史只按 `ServerFrame` 记录。
- **联调关注点**：
  - 客户端日志应看到 `[TargetFrame] WAIT/CALC`，以及 `input upload=... target=... sync=...`
  - 服务端日志应看到 `[ClientMove][NEW]`、`[ClientMove][OLD]`、`[ClientMove][STALE]`、`[ClientMove][REJECT_FUTURE]`
  - 联调时应确认不再出现 `ACCEPT_CURRENT_FRAME`、`EVICT_OLDEST_ON_FULL` 这类旧 Input Buffer 日志

## 17. 文档导航约定（2026-04）

- **跨端约定先看这里**：`BothSide.md`
  - 记录协议字段、跨端时序、输入/权威帧语义、谁依赖谁、联调时双方必须一致的约定
- **客户端实现细节看这里**：`Client/Assets/CLAUDE.md`
  - 重点看 BattleTick、预测/和解、客户端参数、视觉子弹、`CalcTargetFrame()`、`uploadOperationId` 的客户端生成语义
- **服务端实现细节看这里**：`Server/CLAUDE.md`
  - 重点看 `UpdatePlayerOperation()`、`CollectAndBroadcastCurrentFrame()`、输入缓冲、权威子弹/HP/HitEvent、GameOver
- **使用建议**：
  - 先在 `BothSide.md` 确认跨端约定是否已存在
  - 再分别去 `Client/Assets/CLAUDE.md` 与 `Server/CLAUDE.md` 落到具体实现
  - 若改动涉及协议、同步、时序或状态所有权，三份文档应一起检查是否需要同步更新

## 18. 关键输入同帧 burst-send（critical-input-burst-send，2026-04）

- **目标**：提高“停步 zero-move”与“本帧新攻击”在单次本地逻辑帧里的首帧送达概率，降低关键移动意图或攻击在 UDP 丢包下延迟生效的概率。
- **客户端触发条件**：
  - 停步边沿：`BattleTick` 中本帧移动从 non-zero 切到 zero
  - 新攻击边沿：本帧 `CommandManger` 确实新增了攻击命令，并产生了新的 `AttackId`
- **客户端发送语义**：
  - 关键输入帧使用同一份 `BattleInfo` 在同一逻辑帧内重复发送 3 次
  - 3 次发送复用相同的 `OperationID`、`ClientAckedFrame`、移动值与 `AttackId` 集合
  - 非关键输入帧仍保持单发
- **服务端幂等语义**：
  - 重复 `ClientMoveFrame` 会因 `moveFrame <= lastProcessedMoveFrame` 被丢弃
  - 同一 `AttackId` 的重复攻击继续按 `dic_lastProcessedAttackId` 去重，只处理一次
- **验证日志**：
  - 客户端：`[CriticalInput][BurstSend]`、`[CriticalInput][Send]`
  - 服务端：`[ClientMove][STALE]`、`[CriticalInput][AttackBurstIdempotent]`
- **约束**：
  - 不新增协议字段
  - 不替代攻击现有 `pendingAttacks` 跨帧重发窗口
  - 不改变服务端每个 `ServerFrame` 只推进一次位置的语义

---

## 20. 服务端当前帧重复下发（current-frame-repeat-send，2026-04）

- **目标**：放弃按 `ClientAckedFrame` 组织最近窗口补帧，改为每个服务端权威帧只下发“当前帧”，但在同一帧内重复发送多次，提高当前帧首达概率。
- **服务端发送语义**：
  - `BattleController.Network.SendUnsyncedFrames` 只从 `dic_historyFrames[frameid]` 取当前帧
  - 每次下发的 `BattleInfo.Frames` 仅包含 **1 个元素**（当前 `frameid`）
  - `BattleInfo.OperationID` 仍写当前 `frameid`
  - `BattleInfo.HitEvents` 仍跟随当前帧一起重复下发，客户端继续依赖既有去重逻辑消费
- **重复发送参数**：
  - 服务端常量：`Battle.cs:91` `CurrentFrameRepeatSendCount = 3`
  - 可直接调成 1~N；当前建议压测区间 3~5
- **影响**：
  - `ClientAckedFrame` 不再参与下行帧窗口裁剪，仅保留上行兼容字段地位
  - 客户端不应再假设单个权威包内一定带最近多帧历史，而应支持“同一当前帧被重复收到”
  - `HitEvent`、`PlayerStates` 会随当前帧重复到达，客户端必须保持幂等消费
- **联调日志**：
  - 服务端新增：`[FrameSend][RepeatCurrentFrame] bp=... frame=... repeats=...`

---


## 19. 协议源治理约定（protocol-source-convergence，2026-04）

- **唯一权威 proto 源**：`ProtobufAndNotepad/Protobuf/SocketProto.proto`
- **运行时生成产物**：
  - 客户端：`Client/Assets/Scripts/Server/SocketProto.cs`
  - 服务端：`Server/Server/SocketProto.cs`
- **生成命令**：`ProtobufAndNotepad/Protobuf/build.bat:1-6`
- **历史/非权威文件**：
  - `Client/SocketProto.proto`：历史 proto 副本，不再作为运行时维护入口
  - `ProtobufAndNotepad/Protobuf/Proto/SocketProto.proto`：历史/工具链副本，不再作为运行时维护入口
  - `ProtobufAndNotepad/Protobuf/CSharp/**`、`ProtobufAndNotepad/Protobuf/ProtoOut/SocketProto.cs`：工具输出或中间产物，不视为客户端/服务端运行时权威文件
  - `ProtobufAndNotepad/Protobuf/SocketProto.cs`：旧生成产物已删除，避免误用为运行时权威文件
- **维护要求**：
  - 修改协议字段时只改权威 proto 源，然后重新生成客户端/服务端运行时 `SocketProto.cs`
  - 不在历史 proto 副本上继续并行改字段
  - 本治理只收敛来源与生成链路，不改变 battle 语义或线协议含义

---

---

## 25. CMC-style ClientMove 移动上行（2026-06）

> 本节为当前移动同步准绳；旧 dynamic-tick-adjustment 中关于服务端 Input Buffer 精确命中 `SyncFrameId == frameid`、`lastValidMove` 缺帧惯性的描述已废弃。

### 25.1 四类帧号语义

- **ServerFrame**：服务端 `BattleController.frameid`，只由 `BattleLoop` 每 16ms 推进一次。下行 `BattleInfo.OperationID` 和 `BattleFrameSync.frameid` 都是 ServerFrame。位置历史、子弹推进、攻击延迟补偿、HP/死亡结算都只绑定 ServerFrame。
- **ClientMoveFrame**：客户端本地预测 tick 序号，写在 `ClientMove.move_frame`。它只用于上行移动排序、OldMove 去重、SavedMove 确认，不等同于 ServerFrame。
- **ClientAckedFrame**：客户端上行 `BattleInfo.client_acked_frame`，表示客户端已经应用到的最新 ServerFrame。
- **MoveAckFrame**：服务端下行 `BattleInfo.move_ack.acked_move_frame`，表示服务端已经接收并处理到的最新 ClientMoveFrame。
- **AckedMoveFrame**：兼容字段，值与 `move_ack.acked_move_frame` 同步。

- **新增 proto**：
  - `enum MoveType { NewMove=0; OldMove=1; }`
  - `message ClientMove { move_frame, move_x, move_y, predicted_pos_x/y/z, move_type }`
  - `message MoveAckResult { battle_id, acked_move_frame, ack_good_move, correct_pos_x/y/z, frame_discrepancy, resolving_frame_discrepancy }`
  - `BattleInfo.client_moves = 9`
  - `BattleInfo.acked_move_frame = 10`
  - `BattleInfo.move_ack = 11`
- **OperationID**：下行表示 ServerFrame；上行仍填当前客户端预测帧用于日志和兼容，移动处理以 `client_moves` 为准。
- **客户端发送**：
  - 每个 `BattleTick` 生成项目内 `SavedMove`，记录移动输入、预测起点和预测后位置。
  - 客户端维护一个 `pendingMove`。普通帧先挂起当前 SavedMove；下一帧若 pending 与 current 连续且输入足够接近，则合并为一个 `NewMove` 发送（`move_frame` 使用 current 帧，服务端按帧差重模拟）。
  - 若 pending 与 current 不可合并，则同一个上行包按顺序携带两个 `NewMove`，等价于 DualMove；服务端现有“非 OldMove 按列表顺序处理”语义可直接消费，不新增 proto 枚举。
  - 若本帧是关键输入（停步边沿或新增攻击），客户端立即 flush pending：可合并条件不参与延迟，必要时同包发送 pending + current 两个 `NewMove`，并沿用关键输入 burst-send。
  - 若存在未确认的重要 move，则附带一个最旧重要 `OldMove`；OldMove 不会重复选择当前帧或 pendingMove。
  - 重要 move 判定参考 UE CMC：零/非零切换、幅度变化超过阈值、方向 dot 低于阈值。停步属于重要 move。
  - 合并阈值：`moveCombineMagnitudeThreshold = 0.01f`，`moveCombineDotThreshold = 0.996f`。
  - BattleStart 后先按固定 `frameTime` 发送 `ClientMove`；RTT 与首个权威帧就绪后，`targetFrame` 只调节本地 tick 频率，不伪造上行 ClientMoveFrame。
- **服务端处理**：
  - 每个玩家维护 `lastProcessedMoveFrame`、`lastProcessedMoveServerFrame`、当前移动输入、帧差异累计状态和最新 `MoveAckResult`。
  - 处理顺序固定为先处理所有 `OldMove`，再按包内顺序处理所有非 `OldMove`；因此 DualMove 可用两个 `NewMove` 表达。
  - `moveFrame <= lastProcessedMoveFrame` 直接丢弃。
  - `moveFrame > ServerFrame + MaxClientMoveFrameLead` 直接拒绝，防止客户端移动时间轴过度超前。
  - 合法 move 按 `moveFrame - lastProcessedMoveFrame` 做服务端权威重模拟，并推进 `playerPositions`。
  - `OldMove` 只重模拟并推进服务端权威状态，不生成最终 `MoveAck`；`NewMove` 比较重模拟位置与 `ClientMove.predicted_pos_x/y/z`，误差小于阈值下发 `ack_good_move=true`，否则下发 `ack_good_move=false + correct_pos_x/y/z`。
  - 客户端 MoveFrame 增量长期大于服务端帧增量时累计 `frame_discrepancy`；超过阈值后进入按帧偿还模式，限制本次可模拟帧数。
  - `CollectAndBroadcastCurrentFrame` 每个 ServerFrame 广播当前移动意图和当前权威位置；BattleLoop 不再额外推进玩家移动。
  - 服务端不回滚历史帧，按到达且合法的 ClientMoveFrame 单调推进当前权威状态。
- **客户端确认/重放**：
  - 服务端下行 `move_ack`，客户端删除 `moveFrame <= move_ack.acked_move_frame` 的 SavedMove。
  - 客户端收到权威 `PlayerStates` 后，远端玩家始终应用权威位置。
  - `ack_good_move=true`：本地玩家只解链，不改位置。
  - `ack_good_move=false`：本地玩家拉回到 `correct_pos_x/y/z`，再重放剩余 SavedMove。
- **对时**：客户端用最近收到的服务端权威帧 + 半 RTT + 本地经过时间估算当前服务端帧，只用于 tick 调速，不作为移动上行帧号命中目标。
