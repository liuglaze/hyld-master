## Context

当前服务端伤害判定系统（V1）已完整运行：服务端 `TickServerBullets` 做碰撞检测，`HitEvent` 下行广播，客户端 `ApplyHitEvents` 扣血。但子弹生成使用当前帧位置（`useHistoryLookup: false`），在高延迟下命中判定与玩家视觉预期不符。

基础设施已就位：
- `positionHistory` 环形缓冲区（30 帧窗口）每帧记录所有玩家位置
- `TryGetPositionSnapshot(frameId)` 查询接口已实现
- `AttackOperation.ClientFrameId` 字段已有，客户端每次攻击都上报
- `SpawnServerBullets` 已有 `useHistoryLookup` 参数，当前硬编码为 false

客户端视觉子弹有两条路径：
- 路径 A（本地预测）：零延迟，不需要补偿
- 路径 B（权威帧到达）：当前从玩家当前位置生成，需要改为从历史位置生成 + 半空推进

## Goals / Non-Goals

**Goals:**
- 服务端延迟补偿：收到延迟攻击后，从历史位置生成子弹，逐帧追帧模拟（用每帧的历史玩家位置做碰撞检测），追到当前帧后未命中的子弹加入正常模拟
- 扩展 proto，服务端将攻击者发起攻击时的位置下发给客户端
- 客户端权威帧子弹（路径 B）从服务端下发的历史位置生成，并瞬间推进已飞行距离
- 在现有 50% 丢包 + 50~150ms 动态延迟测试条件下验证

**Non-Goals:**
- 不做"射线回溯"（hitscan 类型武器），当前均为投射物
- 不修改本地预测子弹路径 A 的逻辑
- 不做客户端侧的历史位置缓存或回溯（仅服务端回溯）
- 不重构子弹系统（BulletLogic/shell），仅在生成阶段添加位置偏移和飞行时间补偿

## Decisions

### D1: 服务端回溯策略 — 逐帧追帧模拟

服务端用 `AttackOperation.ClientFrameId` 查 `positionHistory`，取攻击者在该帧的位置生成子弹。然后从 `clientFrameId` 到当前 `frameid` 逐帧模拟子弹飞行：

1. 在 `clientFrameId` 帧：用历史位置生成子弹
2. 对每个中间帧 f（clientFrameId → frameid）：
   - 推进子弹位置 `pos += dir * speed * frameTime`
   - 用 `positionHistory[f]` 取出该帧所有玩家位置做碰撞检测
   - 如果命中：生成 HitEvent，子弹销毁，停止追帧
   - 如果超距：子弹销毁，停止追帧
3. 追帧完成未命中：子弹加入 `activeBullets`，后续每帧正常模拟

实现方式：新增 `SimulateBulletCatchUp` 方法，接收子弹和帧范围，复用 `TickServerBullets` 中的碰撞检测逻辑（提取为 `CheckBulletCollision` 共享方法）。

替代方案：只回溯生成位置，碰撞检测用当前帧位置。不选择，因为这会错过子弹飞行过程中在中间帧本该命中的碰撞。

### D2: 下发 spawn 位置的载体 — 复用 AttackOperation proto 字段

在 `AttackOperation` 中新增 `spawn_pos_x/y/z` 三个 float 字段。服务端在 `SpawnServerBullets` 内部确定最终 spawnPos 后，直接写入 atk 字段，随权威帧下行广播。

替代方案：新增独立的 proto 消息。不选择，因为 spawn 位置与攻击操作强绑定，放在同一消息中最简洁。

### D3: 半空子弹推进方式 — 生成时直接偏移初始位置

客户端路径 B 生成视觉子弹时：
1. 使用 `spawn_pos` 而非玩家当前位置作为子弹起点
2. 计算 `elapsedFrames = 当前权威帧 frameid - clientFrameId`
3. 子弹初始位置沿飞行方向推进 `elapsedFrames * speed * frameTime` 的距离

这样子弹从"已经飞了一段"的位置出现，视觉上自然衔接。

替代方案：生成后连续快进多帧 OnUpdateLogic。不选择，因为直接偏移更简单且不产生额外计算开销。

## Risks / Trade-offs

- **[Risk] 追帧模拟计算量** → 最坏情况回溯 13 帧（MaxAcceptableAttackDelay），散弹 20 颗 × 13 帧 = 260 次碰撞检测。2 人战斗中每次仅对 1 个目标，开销可忽略。
- **[Trade-off] spawn_pos 增加帧数据量** → 每个 AttackOperation 增加 12 字节（3×float），攻击操作低频（每秒 1~2 次），影响可忽略。
