# BothSide.md — 两端协议与同步语义记录

> 记录客户端与服务端之间需要双方对齐的协议字段、同步语义、约定变更。

---

## 1. AttackOperation.client_frame_id（新增字段）

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
  - `UpdatePlayerOperation` 中 `incomingAckFrameId = syncFrameId - 1` 后钳位到 `max(0, frameid - 1)`，自动安全处理超前帧号
  - Input Buffer 按 `clientFrameId % inputBufferSize` 入队，按服务端 `frameid % inputBufferSize` 消费
- **引入版本**：dynamic-tick-adjustment（2026-03）

---

## 13. 服务端 Input Buffer（新增，dynamic-tick-adjustment）

- **数据结构**：`Dictionary<int, PlayerOperation[]> inputBuffer`，每玩家一个 `inputBufferSize=2` 的环形数组
- **入队**：`UpdatePlayerOperation` 按 `clientFrameId % inputBufferSize` 写入 slot
- **消费**：`CollectAndBroadcastCurrentFrame` 处理第 F 帧时从 `F % inputBufferSize` 取操作，消费后 slot 设 null
- **缺帧补偿**：移动复制 `lastConsumedMove`、攻击为空列表
- **两端参数对齐**：客户端 `ConstValue.inputBufferSize = 2`，服务端 `inputBufferSize = 2`，必须一致
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

## 16. 服务端移动输入窗口语义调整（2026-03）

- **变更前**：旧版滑动窗口语义仍偏向“最新合法输入优先”，缺帧时可能直接越过更早但仍未消费的输入，且没有显式的“最后已消费移动帧”进度，迟到旧帧更多依赖清理副作用被动移除。
- **变更后**：服务端移动输入已改为**按 `SyncFrameId` 升序推进的滑动窗口**模型：
  - 数据结构：`Dictionary<int, List<BufferedMoveInput>> dic_movementInputBuffer` + `dic_lastConsumedMoveFrame` + `dic_lastValidMove`
  - 窗口参数：`InputBufferSize=4`、`InputFutureLeadTolerance=6`
  - 入队时按 `SyncFrameId` 排序，同帧覆盖；若 `syncFrameId <= lastConsumedMoveFrame`，直接拒绝并记录 `REJECT_STALE`
  - 缓冲满时淘汰最小 `SyncFrameId`，保留更新窗口，并记录 `EVICT_OLDEST_ON_FULL`
  - 消费时优先取 `lastConsumedMoveFrame + 1`；若缺失，则取 `> lastConsumedMoveFrame` 且 `<= frameid + 6` 的最小合法帧
  - 命中严格下一帧时记录 `ACCEPT_IN_ORDER`；跳过缺帧前进时记录 `SKIP_GAP_ACCEPT`
  - 没有合法新输入时，继续沿用 `lastValidMove` 做短时惯性，但**不推进**已消费进度
- **语义**：移动输入不再是“最新状态优先”，而是“按序前进，缺帧可跳，但一旦前进就拒绝迟到旧帧回灌”；攻击仍按事件语义独立处理，不走该窗口。
- **联调结果**：在双端乱序联调下，客户端日志已能观测到权威帧乱序与预测积压；服务端同时稳定出现 `ACCEPT_IN_ORDER`、`SKIP_GAP_ACCEPT`、`REJECT_STALE`、`EVICT_OLDEST_ON_FULL`，且未发现旧帧回灌导致消费倒退。
