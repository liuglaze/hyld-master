## Context

当前战斗帧同步采用固定节拍模型：客户端 `InvokeRepeating("Send_operation", 0.0167f, 0.0167f)` 以 ~60fps 固定推进逻辑帧，服务端 `BattleLoop` 用累加器以 16ms 间隔推进。两端均无 RTT 测量，客户端超前量（`predicted_frameID - sync_frameID`）是网络波动的被动结果而非主动控制目标。

**当前问题**：
- RTT 变大时，客户端操作到达服务端已过期（超过 MaxAcceptableAttackDelay=6 被 REJECT）
- RTT 变小时，超前量过大，预测误差增加，和解校正频繁
- 两端帧时长不一致（服务端 0.016f vs 客户端 0.0167f），长时间运行累积位置偏差
- 服务端无 input buffer，操作"来了就用"，无法利用提前到达的操作

**约束**：
- 服务端为自定义 C# 服务，可自由修改
- 协议基于 Protobuf，MainPack 可扩展字段
- ActionCode.Ping(24)/Pong(25) 已定义但仅用于 TCP 心跳
- 现有 CSP 和解、攻击重发、视觉子弹系统不能被破坏

## Goals / Non-Goals

**Goals:**
- 客户端能实时测量 UDP RTT 并平滑滤波
- 客户端根据平衡公式 `targetFrame = serverFrame + RTT/2/frameTime + inputBuffer` 主动控制超前量
- 逻辑帧 tick 间隔可动态调节（加速/减速），平滑受限不鬼畜
- 服务端引入 input buffer，允许操作提前到达排队，缺帧时移动复制上帧、攻击 No-op
- 两端帧时长统一

**Non-Goals:**
- 不改变 CSP 权威位置校正流程
- 不改变攻击重发窗口机制（pendingAttacks）
- 不改变视觉子弹双路径
- 不改变 HitEvent / ApplyAuthoritativeHpAndDeath 链路
- 不做 UDP 可靠传输层改造
- 不做服务端帧率动态调节（服务端保持固定帧率）

## Decisions

### D1：RTT 测量方案 — 显式 UDP Ping/Pong

**选择**：客户端定期发送 UDP Ping 包，服务端立即 echo Pong，客户端计算 RTT。

**替代方案**：利用权威帧回包隐式估算（记录 sendTimestamp，收到权威帧时算差值）。

**理由**：隐式估算包含服务端处理延迟且不稳定（服务端广播时机不固定），显式 Ping/Pong 测量最准确。服务端改动量极小（收到 Ping 立即回 Pong，几行代码）。

**实现要点**：
- 复用 ActionCode.Ping(24)/Pong(25)，通过 UDP 通道发送（非 TCP）
- MainPack 新增 `timestamp` 字段（int64，field number 14），客户端填入发送时的本地毫秒时间戳
- 服务端收到 Ping 后将 timestamp 原样写入 Pong 回传，不做任何处理
- 客户端收到 Pong 后：`rttSample = localNow - pong.timestamp`
- 平滑算法采用 EWMA（指数加权移动平均）：
  ```
  smoothedRTT = (1 - alpha) * smoothedRTT + alpha * rttSample
  rttVariance = (1 - beta) * rttVariance + beta * |rttSample - smoothedRTT|
  ```
  初始参数：`alpha = 0.125`，`beta = 0.25`（TCP RFC 6298 标准值，已被广泛验证）
- Ping 发送间隔：200ms（5次/秒），足够追踪 RTT 变化且不增加显著带宽

### D2：客户端累加器改造 — 替代 InvokeRepeating

**选择**：在 MonoBehaviour.Update() 中用累加器驱动逻辑帧。

**理由**：`InvokeRepeating` 间隔在运行时无法修改（只能 CancelInvoke + 重新 Invoke，不稳定）。累加器模式是帧同步游戏的标准做法，与服务端 BattleLoop 的驱动模式一致。

**管线职责隔离**（红线）：

```
Update()
├── 输入收集（TouchLogic 写入 HYLDStaticValue，已有）
├── DrainAndDispatch()（消费 UDP 队列，已有，移到 Update 开头）
├── RTT Ping 调度（每 200ms 发一次）
├── 目标帧号计算 + tick 间隔调节
└── 累加器循环
    while (accumulator >= currentTickInterval)
    ├── accumulator -= currentTickInterval
    ├── ResetOperation()
    ├── CommandManger.Execute()
    ├── 本地预测子弹生成
    ├── RecordPredictedHistory
    ├── OnLogicUpdate（逻辑推进）
    ├── CommitPredictedFrame
    └── SendOperation()  ← 发包严格在循环末尾
```

**关键改动**：
- 移除 `InvokeRepeating("Send_operation", ...)`
- `Send_operation()` 的全部逻辑移入累加器循环体
- `currentTickInterval` 为动态调节的值（默认 = frameTime）

### D3：动态 Tick 调节算法

**目标帧号计算**：
```
rttFrames = smoothedRTT / 1000f / frameTime    // RTT 转换为帧数
targetFrame = sync_frameID + (rttFrames / 2) + inputBufferSize
frameDiff = targetFrame - predicted_frameID     // 正=落后需加速，负=超前需减速
```

**调节策略**：
```
// 速率因子：frameDiff 映射到 speedFactor
// 使用线性插值 + clamp，不使用跳变
speedFactor = Clamp(1.0 + frameDiff * adjustRate, minSpeedFactor, maxSpeedFactor)
currentTickInterval = baseTickInterval / speedFactor
```

**参数**：
| 参数 | 默认值 | 说明 |
|---|---|---|
| baseTickInterval | 0.0167f | 标准帧时长（与服务端统一） |
| adjustRate | 0.05f | 每帧差值对应的速率变化比例 |
| minSpeedFactor | 0.85f | 最大减速（tick 拉长 ~18%） |
| maxSpeedFactor | 1.15f | 最大加速（tick 缩短 ~13%） |
| inputBufferSize | 2 | 服务端输入缓冲区大小（帧） |

**平滑机制**：speedFactor 本身通过 Lerp 平滑过渡，不直接跳变：
```
actualSpeedFactor = Lerp(actualSpeedFactor, targetSpeedFactor, smoothRate * deltaTime)
```
`smoothRate = 5.0f`，约 200ms 收敛到目标值。

**极端情况**：
- `frameDiff > 10`（严重落后）：允许单帧内累加器推进多帧（maxCatchupPerUpdate = 3），但每帧仍受 speedFactor 限制
- `frameDiff < -5`（严重超前）：跳过本次 Update 的累加器循环（等待服务端追上）

### D4：服务端 Input Buffer

**数据结构**：
```csharp
// 每个玩家一个环形 buffer，按目标消费帧号索引
Dictionary<int, PlayerOperation[]> inputBuffer;  // key=battlePlayerId, value=环形数组
int inputBufferSize = 2;  // 缓冲区大小（帧）
```

**入队**：`UpdatePlayerOperation` 收到客户端操作时，按客户端上报的帧号写入对应 buffer slot：
```
targetSlot = clientFrameId % inputBufferSize
inputBuffer[battlePlayerId][targetSlot] = operation
```

**消费**：`CollectAndBroadcastCurrentFrame` 在处理第 F 帧时：
```
consumeSlot = F % inputBufferSize
operation = inputBuffer[battlePlayerId][consumeSlot]
if operation == null:
    // 缺帧策略：移动复制上帧，攻击 No-op
    operation = lastConsumedOperation[battlePlayerId].CloneMovementOnly()
inputBuffer[battlePlayerId][consumeSlot] = null  // 消费后清空
lastConsumedOperation[battlePlayerId] = operation
```

**客户端帧号对齐**：客户端上报操作时，需要在包中携带"这个操作应该在服务端第几帧消费"的信息。当前 `uploadOperationId = sync_frameID + 1`，引入动态追帧后改为 `uploadOperationId = predicted_frameID`（即客户端当前预测帧号，服务端根据此帧号放入对应 buffer slot）。

### D5：两端帧时长统一

**选择**：统一为 16ms（0.016f）。

**理由**：服务端已使用整数 16ms，改动最小。客户端 0.0167f 与 16.67ms 更精确但与服务端不一致会累积偏差。16ms vs 16.67ms 的差异（约 4%）对物理模拟影响极小，但帧号漂移在长局中会体现。统一后消除此类问题。

**改动**：客户端 `ConstValue.frameTime` 从 `0.0167f` 改为 `0.016f`。

## Risks / Trade-offs

### R1：累加器改造引入 Update 耦合
- **风险**：原 InvokeRepeating 独立于 Update 时序，改为 Update 驱动后逻辑帧受渲染帧率影响（如果渲染帧率低于 60fps，单次 Update 可能需要推进多个逻辑帧）
- **缓解**：累加器模式天然处理此问题——accum 会累积，while 循环补帧。设 maxCatchupPerUpdate=3 防止极端情况

### R2：RTT 测量不准导致目标帧号抖动
- **风险**：EWMA 滤波不够或网络突发抖动导致 targetFrame 频繁跳变，tick 调节来回摆动
- **缓解**：双重平滑（EWMA 滤波 RTT + Lerp 平滑 speedFactor），加上 Clamp 边界。可调参数均暴露到 ConstValue

### R3：服务端 input buffer 增加复杂度
- **风险**：帧号对齐错误导致操作被放入错误的 slot，玩家动作延迟或丢失
- **缓解**：buffer 设计为容错型——slot 为空时回退到复制上帧，不会导致玩家卡死。日志记录 buffer miss 次数便于调优

### R4：与现有 CSP 和解的兼容性
- **风险**：predicted_frameID - sync_frameID 差值动态变化，PredictionHistoryWindowSize=20 可能不够
- **缓解**：动态追帧的 Clamp 限制了超前量变化幅度。最大超前量 = RTT/2 帧 + inputBuffer + Clamp 允许的偏移。在 200ms RTT 下约 6+2+2=10 帧，20 帧窗口足够。但需在 ConstValue 中暴露以便调整

### R5：两端帧时长统一后的短期帧号偏移
- **风险**：更新上线瞬间，客户端从 0.0167f 切到 0.016f，逻辑帧推进略快，短期 predicted_frameID 可能跳变
- **缓解**：动态追帧系统本身就会平滑调节，无需特殊处理
