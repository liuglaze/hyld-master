## ADDED Requirements

### Requirement: Client consumes authoritative HP from PlayerStates
客户端收到权威帧时，SHALL 从 `PlayerStates.Hp` 读取每个玩家的权威 HP 值，并覆写本地 `playerBloodValue`。HP 的唯一真值来源为服务端 `PlayerStates`，客户端不再通过 HitEvent 修改 HP。

#### Scenario: Normal HP sync on authority frame
- **WHEN** 客户端收到包含 PlayerStates 的权威帧批次
- **THEN** 每个玩家的 `playerBloodValue` SHALL 被设为对应 `PlayerStates.Hp` 的值

#### Scenario: HitEvent lost but PlayerStates arrives
- **WHEN** 服务端发送的 HitEvent 因 UDP 丢包未到达客户端，但后续帧的 PlayerStates 包含更新后的 Hp 值
- **THEN** 客户端 `playerBloodValue` SHALL 正确反映扣血后的 HP（通过 PlayerStates 覆写）

#### Scenario: Frame retransmission carries HP
- **WHEN** 客户端未 ACK 某帧，服务端通过 `SendUnsyncedFrames` 重传该帧
- **THEN** 重传帧中的 `PlayerStates.Hp` SHALL 被客户端正常消费并覆写本地 HP

### Requirement: Client consumes authoritative IsDead from PlayerStates
客户端 SHALL 以 `PlayerStates.IsDead` 作为权威死亡判定。当 `IsDead == true` 时触发死亡逻辑，不依赖本地 `playerBloodValue` 的值。

#### Scenario: Server marks player dead
- **WHEN** 收到的 `PlayerStates.IsDead == true` 且该玩家本地 `isNotDie == true`
- **THEN** SHALL 触发 `playerDieLogic()`，设置 `isNotDie = false`，不自动复活

#### Scenario: HP desynced but IsDead authoritative
- **WHEN** 客户端本地 `playerBloodValue > 0` 但收到 `PlayerStates.IsDead == true`
- **THEN** SHALL 强制 `playerBloodValue = -1` 并触发死亡逻辑（以服务端判定为准）

### Requirement: ApplyHitEvents is presentation-only
`ApplyHitEvents` SHALL 仅触发受击动画和视觉效果，MUST NOT 修改 `playerBloodValue`。去重机制 `_appliedHitEventKeys` SHALL 保留以防止重复播放动画。

#### Scenario: HitEvent arrives normally
- **WHEN** 收到未去重的 HitEvent
- **THEN** SHALL 触发受击动画 `SetTrigger("Hit")`，MUST NOT 修改 `playerBloodValue`

#### Scenario: Duplicate HitEvent
- **WHEN** 收到已在 `_appliedHitEventKeys` 中的 HitEvent
- **THEN** SHALL 跳过，不触发动画

### Requirement: HP delta fallback hit animation
当权威 HP 下降但没有对应的 HitEvent 到达时，客户端 SHALL 补播一次通用受击动画，保证表现连贯。

#### Scenario: HP drops without HitEvent
- **WHEN** 权威帧覆写 HP 导致某玩家 HP 下降，且该批次中该玩家没有收到任何 HitEvent
- **THEN** SHALL 补播一次通用受击动画

#### Scenario: HP drops with HitEvent present
- **WHEN** 权威帧覆写 HP 导致某玩家 HP 下降，且该批次中该玩家已有 HitEvent 触发了受击动画
- **THEN** SHALL NOT 补播额外受击动画

### Requirement: HP initialization from server first frame
客户端 SHALL 从服务端首帧 `PlayerStates.Hp` 初始化玩家 HP 和 HP 上限，MUST NOT 使用硬编码值。

#### Scenario: First authority frame received
- **WHEN** 战斗开始后收到第一批权威帧
- **THEN** 每个玩家的 `playerBloodValue` 和 HP 上限 SHALL 被设为 `PlayerStates.Hp` 的值

#### Scenario: Before first frame arrives
- **WHEN** 战斗已开始但尚未收到任何权威帧
- **THEN** HP 显示 SHALL 使用合理默认值占位，不依赖硬编码英雄 HP

### Requirement: Remove hardcoded client HP
客户端 SHALL 移除 `HYLDStaticValue.cs` 中的硬编码 HP 初始值，HP 上限统一由服务端 `PlayerStates` 提供。

#### Scenario: No hardcoded HP in client
- **WHEN** 战斗初始化时
- **THEN** `playerBloodValue` MUST NOT 从 `HYLDStaticValue` 硬编码值赋值，SHALL 等待服务端首帧 PlayerStates
