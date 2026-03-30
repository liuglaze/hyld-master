## ADDED Requirements

### Requirement: Server maintains authoritative player positions
服务端 SHALL 在 BattleController 中维护每个玩家的实时位置状态。位置初始值根据 teamId 和 battlePlayerId 按出生点规则设定。每个 BattleLoop step 中，服务端 SHALL 根据当前帧缓冲区中的 PlayerOperation（playerMoveX / playerMoveY）推进玩家位置，公式与客户端一致。

#### Scenario: Player position initialized at battle start
- **WHEN** BattleController 调用 BeginBattle 初始化战斗
- **THEN** 每个玩家的位置 SHALL 根据 teamId 设定到对应出生点（我方 X=15，敌方 X=-15，Y=1，Z 按 battlePlayerId 分配）

#### Scenario: Player position updated each frame
- **WHEN** BattleLoop 执行一个 step，且玩家的 PlayerOperation 包含非零移动输入
- **THEN** 服务端 SHALL 将该玩家位置按 `pos += normalizedMoveDir * moveSpeed * frameTime` 推进

#### Scenario: No operation received for a player
- **WHEN** 某帧未收到某玩家的 PlayerOperation
- **THEN** 该玩家位置 SHALL 保持不变（不移动）

### Requirement: Server maintains position history ring buffer
服务端 SHALL 维护一个环形缓冲区，记录最近 N 帧（建议 N=30）每个玩家的位置快照。每帧在推进玩家位置后、生成子弹前，SHALL 记录当前帧的位置快照。超出窗口的旧帧 SHALL 被自动淘汰。

#### Scenario: Position snapshot recorded each frame
- **WHEN** BattleLoop 执行一个 step 完成位置推进
- **THEN** 服务端 SHALL 调用 RecordPositionSnapshot(frameId)，将当前所有玩家位置写入 positionHistory

#### Scenario: Old snapshots evicted
- **WHEN** positionHistory 中的帧数超过窗口大小 N
- **THEN** 最旧的帧快照 SHALL 被移除

#### Scenario: Historical position queried (V2 interface)
- **WHEN** 调用 TryGetPositionSnapshot(frameId, out snapshot)，且 frameId 在窗口范围内
- **THEN** SHALL 返回 true 并输出该帧的所有玩家位置快照

#### Scenario: Historical position query out of window
- **WHEN** 调用 TryGetPositionSnapshot(frameId, out snapshot)，且 frameId 已被淘汰
- **THEN** SHALL 返回 false

### Requirement: Server spawns bullets from AttackOperation
服务端 SHALL 在处理含 AttackOperation 的帧时，为每个攻击操作创建子弹数据。子弹初始位置为攻击者当前服务端位置，方向由 AttackOperation 的 towardx/towardy 决定（含镜像处理）。散弹英雄 SHALL 按配置的 bulletCount 生成多颗子弹，方向按扇形角度分散。

#### Scenario: Single bullet hero attacks
- **WHEN** 服务端处理一个含 AttackOperation 的帧，且攻击者英雄为单发类型（bulletCount=1）
- **THEN** 服务端 SHALL 创建 1 颗子弹，位置=攻击者当前位置，方向=AttackOperation 的方向向量（经镜像处理）

#### Scenario: Shotgun hero attacks
- **WHEN** 服务端处理一个含 AttackOperation 的帧，且攻击者英雄为散弹类型（bulletCount>1）
- **THEN** 服务端 SHALL 创建 bulletCount 颗子弹，方向在基础方向的扇形角度范围内均匀分布

#### Scenario: Attack direction mirroring
- **WHEN** 攻击者的 teamId 对应敌方阵营
- **THEN** 子弹方向的 X 分量 SHALL 取反（与客户端镜像逻辑一致）

#### Scenario: Bullet spawn records clientFrameId (V2 preparation)
- **WHEN** 服务端生成子弹
- **THEN** 子弹数据 SHALL 记录 AttackOperation.client_frame_id（V1 不使用，V2 用于历史回溯）

### Requirement: Server simulates bullet flight and collision
服务端 SHALL 每个 BattleLoop step 推进所有活跃子弹的位置，并执行碰撞检测。碰撞判定使用距离检测：`Distance(bullet.pos, player.pos) <= hitRadius`。

#### Scenario: Bullet advances each frame
- **WHEN** BattleLoop 执行一个 step，且存在活跃子弹
- **THEN** 每颗子弹的位置 SHALL 按 `pos += dir * bulletSpeed * frameTime` 推进，traveledDistance 累加

#### Scenario: Bullet hits enemy player
- **WHEN** 子弹推进后与某敌方玩家的距离 <= hitRadius
- **THEN** 该子弹 SHALL 标记为已命中，生成 HitEvent，并从活跃列表移除

#### Scenario: Bullet does not hit teammates
- **WHEN** 子弹推进后与同队玩家的距离 <= hitRadius
- **THEN** 该子弹 SHALL 不产生命中判定（跳过同 teamId 的玩家）

#### Scenario: Bullet does not hit owner
- **WHEN** 子弹推进后与发射者自身的距离 <= hitRadius
- **THEN** 该子弹 SHALL 不产生命中判定（跳过 ownerBattleId）

#### Scenario: Bullet exceeds max distance
- **WHEN** 子弹的 traveledDistance >= bulletMaxDist
- **THEN** 该子弹 SHALL 从活跃列表移除，不生成 HitEvent

### Requirement: Server stores hero bullet configuration
服务端 SHALL 维护每种英雄的子弹参数配置，包括 bulletSpeed、bulletMaxDist、hitRadius、damage、bulletCount（散弹数量）、spreadAngle（扇形角度，散弹用）。

#### Scenario: Configuration accessed at bullet spawn
- **WHEN** 服务端需要为某英雄的攻击创建子弹
- **THEN** SHALL 从 HeroConfig 中查询该英雄的子弹参数，所有参数值 MUST 与客户端 HYLDStaticValue.Players 中的对应值一致

#### Scenario: Unknown hero type
- **WHEN** 收到的英雄类型不在配置字典中
- **THEN** SHALL 使用默认参数值并记录警告日志
