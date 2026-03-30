## Why

客户端 HP 扣减完全依赖 HitEvent（一次性事件），UDP 丢包时 HitEvent 丢失导致客户端血量不同步——玩家看到攻击命中但血条不变。服务端已在每帧 `PlayerStates` 中下发权威 HP 和 IsDead，且该数据随帧重传机制（`SendUnsyncedFrames`）自动补发，但客户端从未消费这些字段。此外客户端 HP 初始值硬编码在 `HYLDStaticValue.cs`，与服务端 `HeroConfig` 不一致，血条显示比例失真。

## What Changes

- 客户端每次收到权威帧时，用 `PlayerStates.Hp` 覆写本地 `playerBloodValue`，作为 HP 唯一真值来源
- 客户端用 `PlayerStates.IsDead` 驱动死亡判定，替代当前依赖 `playerBloodValue < 0` 的本地判定
- `ApplyHitEvents` 降级为纯表现层：仅触发受击动画/VFX，不再执行 HP 扣减
- 增加 HP 差值兜底机制：当权威 HP 下降但无对应 HitEvent 到达时，补播通用受击动画
- 客户端 HP 初始值改为从服务端首帧 `PlayerStates.Hp` 读取，移除硬编码
- **BREAKING**：`ApplyHitEvents` 不再修改 `playerBloodValue`，依赖旧扣血路径的逻辑需迁移

## Capabilities

### New Capabilities
- `authoritative-hp-sync`: 客户端消费服务端权威 HP/IsDead 状态，实现基于状态同步的血量系统（替代基于事件的扣血）

### Modified Capabilities

## Impact

- **客户端 BattleManger.cs**：`OnLogicUpdate_sync_FrameIdCheck` 新增 HP/IsDead 消费逻辑；`ApplyHitEvents` 移除扣血代码保留动画触发
- **客户端 HYLDStaticValue.cs**：移除硬编码 HP 初始值，改为动态赋值
- **客户端 PlayerLogic / HYLDPlayerController**：死亡判定条件从 `playerBloodValue < 0` 改为服务端 `IsDead` 驱动
- **协议层**：无变更，`AuthoritativePlayerState` 已包含 `Hp` 和 `IsDead` 字段
- **服务端**：无变更，`PackPlayerStates` 已正确下发所有需要的数据
