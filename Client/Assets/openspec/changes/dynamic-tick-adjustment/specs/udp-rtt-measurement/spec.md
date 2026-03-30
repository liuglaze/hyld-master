## ADDED Requirements

### Requirement: Proto timestamp 字段扩展
MainPack 消息 MUST 新增 `timestamp` 字段（int64，field number 14），用于携带毫秒级时间戳。该字段为 protobuf optional，默认值 0，旧客户端/服务端不受影响。

#### Scenario: 新字段向后兼容
- **WHEN** 旧版客户端（无 timestamp 字段）向新版服务端发送 MainPack
- **THEN** 服务端解析 timestamp 为默认值 0，不影响任何现有逻辑

#### Scenario: Ping 包携带时间戳
- **WHEN** 客户端构造 Ping 包时
- **THEN** MUST 将当前本地毫秒时间戳写入 `MainPack.timestamp`

### Requirement: 客户端 UDP Ping 发送
客户端在战斗期间 MUST 定期通过 UDP 通道发送 Ping 包（ActionCode.Ping=24），发送间隔 MUST 为可配置参数（默认 200ms）。Ping 包 MUST 通过 UDP 发送（非 TCP），MUST 携带客户端本地毫秒时间戳。

#### Scenario: 战斗中定期发送 Ping
- **WHEN** 战斗已开始（BattleStart 已收到）且战斗未结束（IsGameOver=false）
- **THEN** 客户端每 200ms 通过 UDP 发送一个 Ping 包，ActionCode=Ping，timestamp=当前本地毫秒时间戳

#### Scenario: 战斗未开始不发送 Ping
- **WHEN** 战斗未开始（未收到 BattleStart）
- **THEN** 客户端 MUST NOT 发送 UDP Ping

#### Scenario: 战斗结束停止发送 Ping
- **WHEN** IsGameOver=true
- **THEN** 客户端 MUST 停止发送 UDP Ping

### Requirement: 服务端 UDP Pong 回复
服务端收到 UDP Ping 包后 MUST 立即回复 Pong 包（ActionCode.Pong=25），MUST 将 Ping 包中的 timestamp 原样写入 Pong 包的 timestamp 字段。服务端 MUST NOT 对 timestamp 做任何计算或修改。

#### Scenario: 正常 Ping-Pong 往返
- **WHEN** 服务端通过 UDP 收到 ActionCode=Ping 的 MainPack，其 timestamp=T
- **THEN** 服务端立即回复 ActionCode=Pong 的 MainPack，其 timestamp=T

#### Scenario: 非战斗状态收到 Ping
- **WHEN** 服务端通过 UDP 收到 Ping 但该 endpoint 未关联任何活跃战斗
- **THEN** 服务端 MUST 忽略该 Ping（不回复，不报错）

### Requirement: 客户端 RTT 计算（EWMA 平滑）
客户端收到 Pong 包后 MUST 计算 RTT 样本并用 EWMA 平滑。算法 MUST 遵循：
```
rttSample = localNow - pong.timestamp
smoothedRTT = (1 - alpha) * smoothedRTT + alpha * rttSample
rttVariance = (1 - beta) * rttVariance + beta * |rttSample - smoothedRTT|
```
参数：`alpha = 0.125f`，`beta = 0.25f`（可配置）。smoothedRTT 初始值 MUST 取第一个有效 rttSample。

#### Scenario: 首次 Pong 到达
- **WHEN** 客户端收到第一个 Pong，rttSample = 80ms
- **THEN** smoothedRTT = 80ms，rttVariance = 0

#### Scenario: 后续 Pong 到达（RTT 变化）
- **WHEN** smoothedRTT = 80ms，收到新 Pong 的 rttSample = 120ms，alpha = 0.125
- **THEN** smoothedRTT = (1 - 0.125) * 80 + 0.125 * 120 = 85ms

#### Scenario: 异常 RTT 样本过滤
- **WHEN** rttSample <= 0 或 rttSample > 2000ms
- **THEN** MUST 丢弃该样本，不更新 smoothedRTT

### Requirement: RTT 状态清理
战斗结束时（ClearPredictionRuntimeState 或等效清理路径），RTT 相关状态（smoothedRTT、rttVariance、Ping 计时器）MUST 全部重置。

#### Scenario: 战斗结束清理
- **WHEN** 战斗结束调用清理逻辑
- **THEN** smoothedRTT = 0，rttVariance = 0，Ping 计时器停止
