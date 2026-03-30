## ADDED Requirements

### Requirement: 服务端输入缓冲区数据结构
服务端 BattleController MUST 为每个玩家维护一个 input buffer（大小 = inputBufferSize 帧），按帧号索引存储客户端上报的操作。`inputBufferSize` MUST 为可配置参数（默认 2）。

#### Scenario: 缓冲区初始化
- **WHEN** BattleController 开始战斗（BeginBattle）
- **THEN** 为每个 battlePlayerId 创建大小为 inputBufferSize 的 buffer，所有 slot 初始化为 null

### Requirement: 操作按帧号入队
`UpdatePlayerOperation` 收到客户端操作后，MUST 按客户端上报的帧号将操作写入对应的 buffer slot。slot 索引 MUST 使用 `clientFrameId % inputBufferSize`。若该 slot 已有旧操作，MUST 覆写（以最新到达的为准）。

#### Scenario: 操作正常入队
- **WHEN** 客户端上报操作，uploadOperationId = 100，inputBufferSize = 2
- **THEN** 操作写入 slot = 100 % 2 = 0

#### Scenario: 操作提前到达
- **WHEN** 服务端当前 frameid = 98，客户端上报操作 uploadOperationId = 100
- **THEN** 操作写入 slot = 100 % 2 = 0，等待 frameid=100 时被消费

#### Scenario: 同一 slot 被覆写
- **WHEN** slot 0 已存有帧 100 的操作，新操作（也指向 slot 0）到达
- **THEN** 旧操作被覆写为新操作

### Requirement: 按帧号消费操作
`CollectAndBroadcastCurrentFrame` 处理第 F 帧时，MUST 从每个玩家的 buffer 中按 `F % inputBufferSize` 取出操作。消费后 MUST 将该 slot 设为 null。

#### Scenario: 正常消费
- **WHEN** frameid = 100，slot = 100 % 2 = 0，slot 中有有效操作
- **THEN** 取出操作用于本帧逻辑，slot 设为 null

#### Scenario: 消费后 slot 清空
- **WHEN** frameid = 100 的操作被消费
- **THEN** slot 0 变为 null，可被 frameid = 102 的操作写入

### Requirement: 缺帧补偿策略（移动复制 + 攻击 No-op）
当服务端到帧消费时 buffer slot 为 null（操作未到达），MUST 执行以下补偿：
1. **移动输入**：复制上一帧消费的移动值（PlayerMoveX、PlayerMoveY），保持玩家运动连续性
2. **攻击输入**：插入空列表（No-op），MUST NOT 复制上一帧的攻击操作（攻击已有 pendingAttacks 可靠重发机制保障）

服务端 MUST 为每个玩家维护 `lastConsumedMove`（上一帧消费的移动值），初始值为 (0, 0)。

#### Scenario: 操作迟到 — 移动保持
- **WHEN** frameid = 100，玩家 A 的 buffer slot 为 null，上一帧移动值为 (0.5, 0.3)
- **THEN** 本帧对玩家 A 使用移动 (0.5, 0.3)，攻击列表为空

#### Scenario: 操作迟到 — 攻击不复制
- **WHEN** frameid = 100，玩家 A 的 buffer slot 为 null，上一帧有攻击操作
- **THEN** 本帧对玩家 A 的攻击列表 MUST 为空（不复制攻击）

#### Scenario: 首帧缺失
- **WHEN** frameid = 1，玩家 A 的 buffer slot 为 null，无上一帧记录
- **THEN** 使用默认移动 (0, 0)，攻击列表为空

### Requirement: Ack 帧号逻辑适配
客户端上报操作时，`uploadOperationId` MUST 改为 `predicted_frameID`（当前预测帧号），不再使用 `sync_frameID + 1`。服务端 `UpdatePlayerOperation` 中的 Ack 计算逻辑 MUST 相应适配，确保 `dic_playerAckedFrameId` 仍正确反映客户端已确认的最新帧号。

#### Scenario: 上报帧号与预测帧号对齐
- **WHEN** 客户端 predicted_frameID = 105，sync_frameID = 98
- **THEN** uploadOperationId = 105（而非旧逻辑的 99）

#### Scenario: 服务端 Ack 不超前
- **WHEN** 服务端 frameid = 100，客户端上报 uploadOperationId = 105
- **THEN** Ack 帧号 MUST 钳位到 max(0, frameid - 1) = 99，不超前于服务端当前帧

### Requirement: Buffer 状态清理
战斗结束时，所有玩家的 input buffer、lastConsumedMove MUST 全部清理释放。

#### Scenario: 战斗结束清理
- **WHEN** 战斗结束（HandleBattleEnd 或等效路径）
- **THEN** 所有 inputBuffer slot 设为 null，lastConsumedMove 清除

### Requirement: Buffer Miss 日志
当 buffer slot 为 null（缺帧）时，服务端 MUST 输出 Warn 级别日志，包含 battlePlayerId、frameid、连续缺帧次数。日志频率 MUST 受限（如每 60 帧最多输出一次汇总），防止日志洪泛。

#### Scenario: 偶发缺帧日志
- **WHEN** 玩家 A 在 frameid=100 缺帧，且距上次日志已超过 60 帧
- **THEN** 输出 Warn 日志：`[InputBuffer] Player {battlePlayerId} miss at frame {frameid}, consecutive={N}`

#### Scenario: 高频缺帧限流
- **WHEN** 玩家 A 连续 10 帧缺帧，距上次日志不足 60 帧
- **THEN** MUST NOT 输出日志（等待限流窗口过期）
