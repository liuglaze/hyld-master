## Context

当前客户端 HP 系统存在两个独立问题：

1. **HP 扣减依赖不可靠事件**：`ApplyHitEvents` 是唯一的扣血路径，HitEvent 作为一次性事件通过 UDP 发送，丢包即丢失。服务端已在 `PlayerStates` 中每帧下发权威 `Hp` 和 `IsDead`，且随帧重传机制自动补发，但客户端从未消费这些字段。

2. **HP 初始值不一致**：服务端使用 `HeroConfig.GetHp(hero)` 初始化（当前已临时降至约 1/5），客户端在 `HYLDStaticValue.cs` 中硬编码原始值，导致血条比例失真。

架构约束：
- 服务端不做改动（`PackPlayerStates` 已正确下发 Hp/IsDead）
- 协议层不做改动（`AuthoritativePlayerState` 已有所需字段）
- 客户端仍需 HitEvent 驱动受击动画（纯表现）

## Goals / Non-Goals

**Goals:**
- 客户端 HP 唯一真值来源为服务端 `PlayerStates.Hp`，消除因 HitEvent 丢包导致的血量不同步
- 死亡判定由服务端 `PlayerStates.IsDead` 驱动，不依赖本地 HP 值
- HP 初始值从服务端首帧 PlayerStates 获取，消除硬编码不一致
- HitEvent 丢失时通过 HP 差值兜底补播受击动画，保证表现连贯

**Non-Goals:**
- 不修改服务端代码或协议定义
- 不实现 HitEvent 可靠传输（已明确否决）
- 不处理单机模式（计划单独剥离）
- 不做 HP 插值/平滑显示（后续优化）

## Decisions

### D1: HP 覆写时机 — 权威帧到达时直接覆写

在 `OnLogicUpdate_sync_FrameIdCheck` 处理权威帧批次时，取最后一帧的 `PlayerStates`，直接将每个玩家的 `playerBloodValue` 设为 `state.Hp`。

**替代方案**：每帧增量计算差值 → 复杂度高，且无法处理累积误差，不如直接覆写简单可靠。

### D2: 死亡判定 — 以 IsDead 为权威，保留 HP<=0 兜底

收到 `PlayerStates.IsDead == true` 时触发死亡逻辑。同时保留 `playerBloodValue <= 0` 作为兜底（防止极端情况下 IsDead 标记延迟）。

**替代方案**：完全移除 HP 判定只看 IsDead → 过于激进，IsDead 可能在帧重传中与 HP 不同步。

### D3: HitEvent 降级策略 — 保留动画触发，移除扣血

`ApplyHitEvents` 保留受击动画和 VFX 触发（`SetTrigger("Hit")`），删除 `playerBloodValue -= evt.Damage` 行。去重机制 `_appliedHitEventKeys` 保留（防止重复播放动画）。

### D4: HP 差值兜底受击动画

每次覆写 HP 时，比较新旧值。如果 HP 下降但该玩家没有在本批次中收到过 HitEvent（即 HitEvent 丢失），补播一次通用受击动画。

### D5: HP 初始值来源 — 服务端首帧

战斗开始后收到的第一批权威帧包含所有玩家的 `PlayerStates.Hp`，用该值初始化 `playerBloodValue` 和 HP 上限（血条显示比例）。移除 `HYLDStaticValue.cs` 中的硬编码 HP。

## Risks / Trade-offs

- **[HP 覆写导致血条跳变]** → 当前不做平滑插值，50% 丢包率下可能看到血条突降。后续可加 Lerp 平滑，但当前优先正确性。
- **[HitEvent 和 PlayerStates 到达时序]** → HitEvent 可能比对应帧的 PlayerStates 先到或后到。扣血以 PlayerStates 为准，动画以 HitEvent 为准，两者解耦无冲突。
- **[首帧 PlayerStates 延迟]** → 战斗开始到收到第一帧之间，HP 显示可能为默认值。用一个合理默认值（如 100）占位，首帧到达后立即覆写。
- **[兜底受击动画误触发]** → HP 差值可能由多个 HitEvent 造成但只收到部分，兜底动画可能在已有正常动画时多播一次。影响轻微（多一次受击抖动），可接受。
