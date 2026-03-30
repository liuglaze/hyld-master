## ADDED Requirements

### Requirement: HitEvent message definition
协议 SHALL 新增 `HitEvent` protobuf 消息，包含以下字段：attack_id（int32）、attacker_battle_id（int32）、victim_battle_id（int32）、damage（int32）、hit_frame_id（int32）、hit_pos_x（float，V2 用）、hit_pos_y（float，V2 用）、hit_pos_z（float，V2 用）、is_kill（bool，V2 用）。

#### Scenario: HitEvent message structure
- **WHEN** 服务端子弹命中检测判定命中
- **THEN** 生成的 HitEvent MUST 包含 attack_id、attacker_battle_id、victim_battle_id、damage、hit_frame_id 五个字段；hit_pos_*、is_kill 为 V2 预留，V1 使用默认值

### Requirement: AttackOperation extended with client_frame_id
`AttackOperation` 消息 SHALL 新增 `int32 client_frame_id = 4` 字段，表示客户端攻击发生时的预测帧号。客户端 SHALL 在构造 AttackOperation 时填充 `client_frame_id = predicted_frameID`。服务端 V1 不使用此字段，V2 用于延迟补偿历史回溯。

#### Scenario: Client fills client_frame_id
- **WHEN** 客户端构造 AttackOperation 对象
- **THEN** client_frame_id SHALL 被设置为当前 predicted_frameID

#### Scenario: Server receives client_frame_id (V1)
- **WHEN** 服务端收到含 client_frame_id 的 AttackOperation
- **THEN** 服务端 SHALL 将 client_frame_id 记录到 ServerBullet 数据中但不用于碰撞回溯（V1 行为）

### Requirement: HitEvent carried in authority frame broadcast
HitEvent SHALL 搭载在现有权威帧下行包中传输。`BattleInfo` 消息 SHALL 新增 `repeated HitEvent hit_events` 字段。服务端在 `SendUnsyncedFrames` 时将当前待广播的 HitEvent 附着在 BattleInfo 中一并下发。

#### Scenario: HitEvent delivered with frame data
- **WHEN** 服务端在某帧检测到子弹命中，且该帧通过 SendUnsyncedFrames 下发
- **THEN** 该帧的 BattleInfo MUST 包含对应的 HitEvent 在 hit_events 字段中

#### Scenario: No hit in frame
- **WHEN** 某帧无任何子弹命中
- **THEN** 该帧的 BattleInfo.hit_events SHALL 为空（repeated 字段默认行为）

#### Scenario: Multiple hits in same frame
- **WHEN** 同一帧有多颗子弹分别命中不同目标
- **THEN** 该帧的 BattleInfo.hit_events SHALL 包含所有对应的 HitEvent

### Requirement: HitEvent reliability through frame retransmission
HitEvent 的可靠性 SHALL 由现有帧重传机制保障。因 HitEvent 附着在帧数据中，`SendUnsyncedFrames` 的 ACK-based 重传逻辑 SHALL 自然覆盖 HitEvent 的可靠传输。

#### Scenario: Client has not ACKed frame containing HitEvent
- **WHEN** 客户端未 ACK 包含 HitEvent 的帧
- **THEN** 服务端下次 SendUnsyncedFrames 时 SHALL 重新包含该帧及其 HitEvent

### Requirement: Backward compatibility with old clients
新增的 HitEvent 消息、AttackOperation.client_frame_id 字段和 BattleInfo.hit_events 字段 SHALL 不破坏已有协议兼容性。

#### Scenario: Old client connects to new server
- **WHEN** 未更新的客户端连接到新服务端
- **THEN** 客户端 SHALL 正常接收帧数据，忽略未知的 hit_events 字段，不产生错误

#### Scenario: New client connects to old server
- **WHEN** 新客户端连接到未更新的服务端
- **THEN** client_frame_id 字段被忽略，hit_events 为空，客户端不执行扣血（与当前行为一致）
