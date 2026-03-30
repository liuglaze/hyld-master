## Why

当前客户端以固定 16.7ms 节拍（InvokeRepeating）推进逻辑帧，predicted_frameID 与 sync_frameID 的差值完全取决于网络波动的"碰巧结果"。没有 RTT 测量、没有目标帧号计算、没有动态变速机制。当网络延迟变化时，客户端无法主动调整超前量——RTT 变大会导致操作到达服务端时已过期被 REJECT（MaxAcceptableAttackDelay=6），RTT 变小则浪费超前量增加预测误差。引入动态追帧后，客户端能主动维持 `targetFrame = serverFrame + RTT/2 + inputBuffer` 的平衡公式，显著改善变网络条件下的操作响应性和位置预测准确性。

## What Changes

### 新增
- **UDP RTT 测量**：客户端定期发送 UDP Ping（复用 ActionCode.Ping=24），服务端立即 echo Pong（ActionCode.Pong=25），客户端计算 smoothedRTT（指数移动平均）
- **Proto 扩展**：MainPack 新增 `timestamp` 字段（int64，毫秒级），用于 Ping/Pong 携带客户端发送时间戳
- **目标帧号计算器**：根据平衡公式 `targetFrame = sync_frameID + (smoothedRTT / 2) / frameTime + inputBufferSize` 计算客户端应处于的帧号
- **动态 Tick 调节器**：根据 `targetFrame - predicted_frameID` 的差值，平滑调整逻辑帧的 tick 间隔（加速/减速），替代固定 InvokeRepeating。**必须设定 Clamp 边界**：最大加速/减速阈值（如 ±10%~20%），防止 RTT 剧烈抖动时画面鬼畜；每帧调节步长平滑受限，即使未达平衡也不允许突变
- **管线职责隔离**：累加器改造后，输入收集严格留在 Update 中放入队列；网络 Send 操作严格卡在累加器 `while(accum >= tickRate)` 循环的最后一步统一执行，禁止在其他位置散落发包逻辑
- **服务端输入缓冲区**：服务端 BattleController 引入 N 帧 input buffer，允许客户端操作提前到达排队等待消费，而非当前的"来了就用"模式。**缺帧策略（已决定）**：当到帧时操作未到达（buffer 跑空），移动输入复制上一帧（保持移动方向连续性），攻击输入 No-op（攻击已有 pendingAttacks 可靠重发机制，不需要 buffer 层面复制）

### 修改
- **BREAKING**：`BattleManger.Send_operation` 从 `InvokeRepeating` 驱动改为 `Update` 累加器驱动
- 服务端 `BattleController.CollectAndBroadcastCurrentFrame` 改为从 input buffer 按帧号消费操作
- 服务端 `UpdatePlayerOperation` 改为按帧号入队到 input buffer
- 两端帧时长统一：服务端 `frameIntervalMs=16`（0.016f）与客户端 `frameTime=0.0167f` 不一致，统一为相同值

### 不变
- CSP 权威位置校正流程不变（ApplyAuthoritativePositions + ReplayUnconfirmedInputs）
- 攻击重发窗口机制不变（pendingAttacks + ConfirmAttacks）
- 视觉子弹双路径（本地预测 + 权威帧）不变
- HitEvent / ApplyAuthoritativeHpAndDeath 消费链路不变

## Capabilities

### New Capabilities
- `udp-rtt-measurement`：UDP 链路的 RTT 实时测量，包括客户端 Ping 发送、服务端 Pong 回复、smoothedRTT 计算（EWMA）、proto timestamp 字段扩展
- `dynamic-tick-adjustment`：客户端动态帧率追赶机制，包括目标帧号计算（平衡公式）、tick 间隔动态调节（加速/减速/平滑/Clamp）、InvokeRepeating → 累加器改造、管线职责隔离（输入收集 vs 逻辑推进 vs 发包）
- `server-input-buffer`：服务端输入缓冲区，按帧号排队客户端操作、到帧时消费、缓冲区大小配置、缺帧/迟到补偿策略、与动态追帧的 inputBufferSize 参数联动

### Modified Capabilities
<!-- 无已有 spec 需要修改 -->

## Impact

### 客户端
- `BattleManger.cs`：Send_operation 驱动模式重写（InvokeRepeating → Update 累加器）、新增 RTT 测量调度、新增目标帧号计算、新增 tick 调节逻辑
- `UDPSocketManger.cs`：新增 UDP Ping 发送和 Pong 接收处理
- `ConstValue.cs`：新增动态追帧相关配置常量（inputBufferSize、RTT 平滑系数、tick 加速/减速上下限等）
- `SocketProto.cs`（proto 文件）：MainPack 新增 timestamp 字段

### 服务端
- `Battle.cs`：BattleController 引入 input buffer 数据结构、CollectAndBroadcastCurrentFrame 改为按帧号消费、UpdatePlayerOperation 改为按帧号入队
- `ClientUdp.cs`：UDP 路由增加 Ping/Pong 处理（战斗链路内）
- `SocketProto.cs`（proto 文件）：同步 MainPack timestamp 字段

### Proto 协议
- MainPack 新增 `timestamp` 字段（int64，field number 待定）——protobuf 向后兼容，旧客户端不受影响

### 两端帧时长对齐
- 服务端 `frameIntervalMs` 和客户端 `frameTime` 统一为相同值（16ms 或 16.67ms，需确定）
