## ADDED Requirements

### Requirement: 目标帧号计算（平衡公式）
客户端 MUST 在每次 Update 中根据以下公式计算目标帧号：
```
rttFrames = smoothedRTT / 1000f / frameTime
targetFrame = sync_frameID + ceil(rttFrames / 2) + inputBufferSize
```
其中 `smoothedRTT` 来自 udp-rtt-measurement 模块，`inputBufferSize` 为可配置常量（默认 2）。当 smoothedRTT 尚未初始化（= 0）时，MUST 使用默认超前量（= inputBufferSize）。

#### Scenario: RTT 80ms 时的目标帧号
- **WHEN** sync_frameID = 95，smoothedRTT = 80ms，frameTime = 0.016f，inputBufferSize = 2
- **THEN** rttFrames = 80 / 1000 / 0.016 = 5.0，targetFrame = 95 + ceil(5.0 / 2) + 2 = 95 + 3 + 2 = 100

#### Scenario: RTT 尚未初始化
- **WHEN** smoothedRTT = 0（未收到首个 Pong），sync_frameID = 50，inputBufferSize = 2
- **THEN** targetFrame = 50 + 0 + 2 = 52（使用默认超前量）

#### Scenario: RTT 突然变大
- **WHEN** smoothedRTT 从 60ms 变为 140ms，sync_frameID = 200，inputBufferSize = 2
- **THEN** targetFrame 从 200 + 2 + 2 = 204 变为 200 + 5 + 2 = 207，客户端需加速追上

### Requirement: InvokeRepeating 替换为累加器驱动
BattleManger MUST 移除 `InvokeRepeating("Send_operation", ...)`，改用 `Update()` 中的累加器模式驱动逻辑帧。累加器逻辑 MUST 遵循：
```
accumulator += Time.deltaTime;
while (accumulator >= currentTickInterval) {
    accumulator -= currentTickInterval;
    执行逻辑帧（原 Send_operation 全部逻辑）;
}
```

#### Scenario: 正常帧推进
- **WHEN** currentTickInterval = 0.016f，距上次 Update 经过 0.017f
- **THEN** 累加器触发一次逻辑帧，accumulator 剩余 0.001f

#### Scenario: 渲染帧率低于逻辑帧率
- **WHEN** currentTickInterval = 0.016f，距上次 Update 经过 0.048f
- **THEN** 累加器触发三次逻辑帧（0.048 / 0.016 = 3）

#### Scenario: 单次 Update 追帧上限
- **WHEN** 累加器需要推进超过 maxCatchupPerUpdate（默认 3）帧
- **THEN** MUST 最多推进 maxCatchupPerUpdate 帧，多余的累积量丢弃（accumulator 钳位），防止帧爆发

### Requirement: 管线职责隔离
累加器改造后的 Update 管线 MUST 遵循以下严格顺序，禁止违反：
1. DrainAndDispatch()（消费 UDP 队列）
2. RTT Ping 调度
3. 目标帧号计算 + tick 调节
4. 累加器循环（内含逻辑推进 + 发包）

发包操作（SendOperation）MUST 严格位于累加器循环体的最后一步。MUST NOT 在累加器循环外的任何位置执行发包。输入收集（TouchLogic 写入 HYLDStaticValue）保持在 Unity 原生 Update 中，不受累加器影响。

#### Scenario: 发包位置正确
- **WHEN** 累加器循环推进一帧逻辑
- **THEN** SendOperation MUST 在该帧的 CommitPredictedFrame 之后、循环体结束之前执行

#### Scenario: 禁止循环外发包
- **WHEN** 累加器条件不满足（accumulator < currentTickInterval）
- **THEN** MUST NOT 执行任何 SendOperation

### Requirement: 动态 Tick 调节（Clamp 边界）
客户端 MUST 根据 `frameDiff = targetFrame - predicted_frameID` 动态调节 `currentTickInterval`。调节 MUST 遵循以下约束：

1. **速率因子计算**：`targetSpeedFactor = Clamp(1.0 + frameDiff * adjustRate, minSpeedFactor, maxSpeedFactor)`
2. **平滑过渡**：`actualSpeedFactor = Lerp(actualSpeedFactor, targetSpeedFactor, smoothRate * deltaTime)`
3. **Tick 间隔**：`currentTickInterval = baseTickInterval / actualSpeedFactor`
4. **Clamp 边界**：MUST NOT 允许 actualSpeedFactor 超出 [minSpeedFactor, maxSpeedFactor] 范围

默认参数：
| 参数 | 默认值 |
|---|---|
| adjustRate | 0.05f |
| minSpeedFactor | 0.85f |
| maxSpeedFactor | 1.15f |
| smoothRate | 5.0f |

#### Scenario: 落后 2 帧需要加速
- **WHEN** frameDiff = 2，adjustRate = 0.05
- **THEN** targetSpeedFactor = 1.0 + 2 * 0.05 = 1.1（加速 10%），currentTickInterval = 0.016 / 1.1 ≈ 0.01455f

#### Scenario: 超前 3 帧需要减速
- **WHEN** frameDiff = -3，adjustRate = 0.05
- **THEN** targetSpeedFactor = 1.0 + (-3) * 0.05 = 0.85（减速 15%），currentTickInterval = 0.016 / 0.85 ≈ 0.01882f

#### Scenario: 极端落后触发 Clamp
- **WHEN** frameDiff = 10，adjustRate = 0.05
- **THEN** targetSpeedFactor = Clamp(1.0 + 10 * 0.05, 0.85, 1.15) = 1.15（不超过上限）

#### Scenario: 平滑过渡不突变
- **WHEN** actualSpeedFactor = 1.0，targetSpeedFactor = 1.15，smoothRate = 5.0，deltaTime = 0.016
- **THEN** actualSpeedFactor = Lerp(1.0, 1.15, 5.0 * 0.016) = Lerp(1.0, 1.15, 0.08) = 1.012（平滑上升，不跳变）

#### Scenario: 严重超前暂停推进
- **WHEN** frameDiff < -5（超前超过 5 帧）
- **THEN** MUST 跳过本次 Update 的累加器循环（不推进逻辑帧），等待服务端追上

### Requirement: 两端帧时长统一
客户端 `ConstValue.frameTime` MUST 从 `0.0167f` 改为 `0.016f`，与服务端 `frameIntervalMs = 16`（即 `FrameTimeSec = 0.016f`）保持一致。

#### Scenario: 帧时长一致性
- **WHEN** 读取客户端 frameTime 和服务端 FrameTimeSec
- **THEN** 两个值 MUST 相等（均为 0.016f）

### Requirement: 动态追帧状态清理
战斗结束时，累加器状态（accumulator、currentTickInterval、actualSpeedFactor）和目标帧号计算相关状态 MUST 全部重置为初始值。

#### Scenario: 战斗结束清理
- **WHEN** 战斗结束调用清理逻辑
- **THEN** accumulator = 0，currentTickInterval = baseTickInterval，actualSpeedFactor = 1.0
