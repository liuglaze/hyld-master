## ADDED Requirements

### Requirement: Client receives and parses HitEvent from authority frames
客户端 SHALL 在 HandleMessage（UDP 回调）中解析权威帧下行包中的 HitEvent 列表。解析后的 HitEvent 数据 SHALL 通过 AddAction 排队到主线程处理。

#### Scenario: Authority frame contains HitEvents
- **WHEN** 客户端收到的权威帧 BattleInfo 中 hit_events 非空
- **THEN** 客户端 SHALL 逐条解析 HitEvent，并为每条 HitEvent 调用 AddAction 排队到主线程

#### Scenario: Authority frame contains no HitEvents
- **WHEN** 客户端收到的权威帧 BattleInfo 中 hit_events 为空
- **THEN** 客户端 SHALL 不执行任何扣血操作（正常帧同步流程不受影响）

### Requirement: Client applies damage from HitEvent
客户端 SHALL 在主线程中根据 HitEvent 执行扣血逻辑。被击者的 HP SHALL 减少 HitEvent.damage 的值。

#### Scenario: Player takes damage
- **WHEN** 主线程处理一条 HitEvent，victim_battle_id 对应一个存活玩家
- **THEN** 该玩家的 HP SHALL 减少 damage 值，并触发受击表现（动画/特效）

#### Scenario: Damage applied to local player
- **WHEN** HitEvent 的 victim_battle_id 等于本地玩家的 battlePlayerId
- **THEN** 本地玩家 SHALL 执行扣血、受击动画、屏幕震动等表现

#### Scenario: Damage applied to remote player
- **WHEN** HitEvent 的 victim_battle_id 对应远程玩家
- **THEN** 远程玩家 SHALL 执行扣血、受击动画等表现

#### Scenario: Duplicate HitEvent (same attack_id + victim)
- **WHEN** 客户端收到重复的 HitEvent（由帧重传导致）
- **THEN** 客户端 SHALL 通过 attack_id + victim_battle_id 去重，不重复扣血

### Requirement: Client handles player death from HitEvent damage
客户端 SHALL 在扣血后检查玩家 HP 是否 <= 0，若是则触发死亡判定。

#### Scenario: Player HP reaches zero
- **WHEN** 扣血后玩家 HP <= 0
- **THEN** 客户端 SHALL 触发死亡流程（playerDieLogic），设置 isNotDie=false，播放死亡动画

#### Scenario: Player HP above zero after damage
- **WHEN** 扣血后玩家 HP > 0
- **THEN** 客户端 SHALL 仅执行受击表现，不触发死亡

### Requirement: Client fills client_frame_id in AttackOperation
客户端 SHALL 在构造 AttackOperation 时填充 `client_frame_id = predicted_frameID`，以便服务端 V2 延迟补偿使用。

#### Scenario: Attack operation created
- **WHEN** 客户端 AddCommad_Attack 分配新的 AttackOperation
- **THEN** AttackOperation.client_frame_id SHALL 被设置为当前 BattleData.predicted_frameID

### Requirement: Client removes AuthorityBullet system
客户端 SHALL 完全移除 AuthorityBullet 相关代码。包括：AuthorityBullet 类定义、authorityBullets 列表、SpawnAuthorityBullet()、TickAuthorityBullets()、authoritySnapshotHistory（如仅供子弹碰撞使用）。OnLogicUpdate_sync_FrameIdCheck 中的权威子弹生成和 Tick 段落 SHALL 被移除。

#### Scenario: Authority frame received with AttackOperations
- **WHEN** 客户端收到含 AttackOperation 的权威帧
- **THEN** 客户端 SHALL 仅生成视觉子弹（SpawnVisualBullet），不再创建 AuthorityBullet 或执行 TickAuthorityBullets

#### Scenario: Reconciliation path
- **WHEN** 客户端执行和解回滚
- **THEN** 和解路径中 SHALL 不再包含任何 AuthorityBullet 相关的重置/恢复逻辑

### Requirement: Visual bullet system remains unchanged
视觉子弹系统（SpawnVisualBullet / BulletLogic / shell）SHALL 不受此次改动影响。视觉子弹继续作为纯表现层运作，isVisualOnly=true 标记、Collider 禁用逻辑、shell 飞行更新逻辑 SHALL 保持原样。

#### Scenario: Visual bullet spawned on attack
- **WHEN** 客户端收到含 AttackOperation 的权威帧
- **THEN** SpawnVisualBullet SHALL 照常生成视觉子弹 GameObject，飞行表现不受伤害判定迁移的影响

#### Scenario: Visual bullet during reconciliation
- **WHEN** 和解发生
- **THEN** isVisualOnly=true 的 shell SHALL 继续保留在 AllShells 中，不被销毁（行为与当前一致）
