## Why

当前架构是"纯转发帧同步"：服务端只收集操作并广播，不维护任何游戏状态权威性。所有游戏逻辑（移动推进、子弹碰撞、伤害判定）都在客户端各自执行，靠和解（Reconciliation）来修正预测偏差。这导致了两个核心问题：

1. **伤害无权威性**：AuthorityBullet 系统完全在客户端本地运行，扣血逻辑至今是 TODO，核心打击流程从未真正生效。
2. **和解复杂度过高**：客户端需要维护预测历史、对比所有玩家的输入是否匹配、不匹配时做全回滚+重放。为支撑这套逻辑引入了双轨子弹、authoritySnapshotHistory、ResetForReconciliation 等大量复杂度。

根本原因是服务端没有权威状态。解决方案：**将服务端从"纯转发"升级为"权威状态帧同步"** —— 服务端自己运行游戏逻辑、维护权威状态（玩家位置、HP 等），每帧随操作帧一起下发权威状态。客户端不再需要"猜测 + 验证"，而是"预测 + 直接校正"。

## What Changes

### 架构升级：纯转发帧同步 → 服务端权威帧同步

- **服务端角色变化**：从纯操作转发器 → 游戏逻辑运行者 + 权威状态源
- **客户端角色变化**：从"本地模拟 + 和解验证" → "本地预测 + 权威校正 + 重放未确认输入"
- **帧同步保留**：服务端仍按帧 tick（16.7ms），仍广播操作帧，但同时**下发每帧的权威玩家状态**

### 具体改动

- **新增**：服务端每帧下发权威玩家状态（位置等） —— `AllPlayerOperation` 新增 `repeated PlayerState player_states` 字段，包含所有玩家的权威位置，每帧全量下发
- **新增**：服务端子弹模拟与伤害判定 —— 服务端在 BattleController 中模拟子弹飞行、碰撞检测、生成 HitEvent 广播（已实现）
- **简化**：客户端和解逻辑大幅简化 —— 收到权威帧时：直接采用权威位置作为锚点 → 重放权威帧号之后的未确认本地输入 → 完成。不再需要"对比输入是否匹配 → 决定是否回滚"的判定逻辑
- **移除**：客户端 AuthorityBullet 整套系统（已完成）
- **移除**：输入匹配判定逻辑（TryMatchPredictedInputWithAuthority）—— 不再需要，因为有权威位置直接校正
- **移除**：权威状态快照历史（lastAuthorityStateSnapshot）的旧用法 —— 被服务端下发的权威位置替代
- **保留**：预测历史（PredictedFrameHistoryEntry）—— 仍需要记录未确认的本地输入，收到权威帧后重放
- **保留**：视觉子弹系统（SpawnVisualBullet / BulletLogic / shell）不变
- **保留**：攻击操作重发窗口机制（FlushPendingAttacksToOperation + dic_lastProcessedAttackId + ConfirmAttacks）不变

## 分阶段目标

### V1（本次实施）

**服务端权威伤害（已完成）**：
- 服务端维护玩家位置、模拟子弹飞行、执行碰撞检测、广播 HitEvent ✅
- 客户端接收 HitEvent 执行扣血/死亡 ✅
- 删除客户端 AuthorityBullet 系统 ✅

**服务端权威位置下发（本次新增）**：
- 协议扩展：`AllPlayerOperation` 新增 `repeated PlayerState player_states`
- 服务端每帧在 `CollectAndBroadcastCurrentFrame` 中将 `playerPositions` 打包到下行帧
- 客户端和解简化为"权威位置 + 重放未确认输入"模式

### V2（后续迭代）
- **增量状态同步**：不再每帧全量下发所有玩家位置，改为只下发有变化的玩家状态（delta sync）
- ~~**服务端权威 HP**：服务端维护 HP，HitEvent.is_kill 由服务端判定，客户端不再本地维护 HP~~ → **已完成（D8，2026-03）**：服务端 `HeroConfig.GetHp()` 维护 HP，击杀时 `HitEvent.is_kill=true`，触发 `HandleBattleEnd` 下发 GameOver。客户端以 `IsKill` 为死亡权威判定。HP 临时降至约 1/5 用于测试。
- **延迟补偿（Lag Compensation）**：服务端根据 client_frame_id 回溯位置历史做碰撞判定
- **客户端预测扣血**：收到攻击时客户端先预测扣血效果，服务端 HitEvent 到达后确认或回滚

## Capabilities

### New Capabilities
- `server-authoritative-state`: 服务端权威状态下发 —— 服务端每帧计算权威玩家状态并随帧下行包广播，客户端基于权威状态做校正+重放
- `server-bullet-simulation`: 服务端子弹飞行模拟与碰撞检测（已实现）
- `hit-event-protocol`: 命中事件协议与下行通道（已实现）
- `client-hit-consumption`: 客户端命中事件消费（已实现）

### Modified Capabilities
- `client-reconciliation`: 客户端和解逻辑 —— 从"输入匹配判定 + 条件回滚"简化为"权威位置采用 + 无条件重放未确认输入"

## Impact

### 服务端
- `Server/Battle.cs` - CollectAndBroadcastCurrentFrame 新增：将 playerPositions 打包到 AllPlayerOperation.player_states 中下发
- 服务端已有 playerPositions 维护逻辑（每帧推进），只需在广播时打包下发
- 性能影响极小：每帧额外序列化 6 个玩家的 xyz 坐标 = 72 字节

### 协议
- `SocketProto.proto` - 新增 `PlayerState` 消息（battle_id, pos_x, pos_y, pos_z）
- `SocketProto.proto` - `AllPlayerOperation` 新增 `repeated PlayerState player_states` 字段
- **BREAKING**：无，纯新增字段/消息。旧客户端会忽略未知字段

### 客户端
- `BattleManger.cs` - 和解逻辑大幅简化：删除 TryMatchPredictedInputWithAuthority、条件回滚判定，改为每次收到权威帧都直接采用权威位置 + 重放
- `BattleManger.cs` - 预测历史仍保留，但只需存储本地输入（不再需要存储所有玩家的状态快照用于对比）
- `BattleManger.cs` - OnLogicUpdate_sync_FrameIdCheck 中新增：解析权威帧中的 player_states，应用到本地玩家位置
- 视觉子弹链路 **不受影响**

### 文档
- `Assets/CLAUDE.md` - 和解章节需重写为"权威位置校正 + 重放"模式；架构描述从"纯转发帧同步"更新为"服务端权威帧同步"
- `Assets/Docs/ForServer.md` - 新增服务端权威状态下发链路描述
- `BothSide.md` - 记录新增 PlayerState 协议和交互语义
