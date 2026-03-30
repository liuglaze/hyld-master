## 1. Proto 扩展 + 帧时长统一（两端基础设施）

- [x] 1.1 Proto 文件新增 timestamp 字段：MainPack 新增 `int64 timestamp = 14`，两端 proto 文件同步修改
- [x] 1.2 两端重新生成 Protobuf 代码（客户端 SocketProto.cs、服务端 SocketProto.cs）
- [x] 1.3 帧时长统一：客户端 `ConstValue.frameTime` 从 `0.0167f` 改为 `0.016f`
- [x] 1.4 单元测试/节点验证：Proto 兼容性 — 构造一个不含 timestamp 的旧 MainPack 二进制，用新版 proto 反序列化，验证 timestamp 默认值为 0 且其他字段正常解析；构造含 timestamp=12345 的 MainPack，序列化→反序列化，验证 timestamp 保持 12345

## 2. UDP RTT 测量 — 服务端 Pong 回复

- [x] 2.1 服务端 `ClientUdp.cs` UDP 路由增加 Ping 识别：在 `TryResolveBattleID` 或 Handle 分发前，检测 `ActionCode.Ping`
- [x] 2.2 服务端 Pong 回复逻辑：收到 UDP Ping 后，构造 Pong 包（ActionCode=Pong，timestamp 原样回传），立即 SendTo 原 endpoint
- [x] 2.3 非战斗状态 Ping 处理：若 endpoint 未关联活跃战斗，忽略 Ping（不回复，不报错）
- [x] 2.4 单元测试/节点验证：服务端 Pong — 用测试脚本向服务端 UDP 端口发送 Ping 包（timestamp=某个值），验证收到 Pong 且 timestamp 原样返回；向未关联战斗的 endpoint 发 Ping，验证无 Pong 回复

## 3. UDP RTT 测量 — 客户端 Ping 发送 + EWMA 计算

- [x] 3.1 客户端 RTT 状态字段：在 BattleManger 或 BattleData 中新增 `smoothedRTT`（float，ms）、`rttVariance`（float，ms）、`_rttInitialized`（bool）、`_lastPingTime`（float）
- [x] 3.2 客户端 Ping 发送逻辑：战斗中每 200ms 通过 `UDPSocketManger` 发送 Ping 包（ActionCode=Ping，timestamp=当前毫秒时间戳），战斗未开始或已结束不发送
- [x] 3.3 客户端 Pong 接收处理：在 `HandleMessage` 中识别 ActionCode.Pong，计算 `rttSample = localNow - pong.timestamp`，过滤异常样本（<=0 或 >2000ms 丢弃）
- [x] 3.4 EWMA 平滑算法：首次样本直接赋值，后续 `smoothedRTT = (1-0.125)*smoothedRTT + 0.125*rttSample`，`rttVariance = (1-0.25)*rttVariance + 0.25*|rttSample-smoothedRTT|`
- [x] 3.5 RTT 状态清理：`ClearPredictionRuntimeState` 中重置 smoothedRTT=0、rttVariance=0、_rttInitialized=false、_lastPingTime=0
- [x] 3.6 单元测试/节点验证：RTT 计算 — 编写测试方法模拟 Pong 序列 [80, 120, 60, 200, -5, 2500]，验证：首次 smoothedRTT=80；第二次 ≈85；异常样本（-5, 2500）被丢弃不更新 smoothedRTT；清理后 smoothedRTT=0

## 4. 服务端 Input Buffer

- [x] 4.1 Buffer 数据结构：BattleController 新增 `Dictionary<int, PlayerOperation[]> inputBuffer`（key=battlePlayerId，value=大小 inputBufferSize 的数组）、`Dictionary<int, (float moveX, float moveY)> lastConsumedMove`、常量 `inputBufferSize = 2`
- [x] 4.2 BeginBattle 初始化：为每个玩家创建 buffer 数组（slot 全 null）、lastConsumedMove 初始化为 (0,0)
- [x] 4.3 UpdatePlayerOperation 改造 — 按帧号入队：收到操作后按 `clientFrameId % inputBufferSize` 写入 slot（覆写旧值）。移动输入和 Ack 更新逻辑保持现有行为
- [x] 4.4 CollectAndBroadcastCurrentFrame 改造 — 按帧号消费：处理第 F 帧时从 `F % inputBufferSize` 取操作，消费后 slot 设 null；缺帧时移动复制 lastConsumedMove、攻击为空列表；消费后更新 lastConsumedMove
- [x] 4.5 Buffer Miss 日志：缺帧时输出 Warn 日志（battlePlayerId, frameid, 连续缺帧次数），限流每 60 帧最多一次汇总
- [x] 4.6 战斗结束清理：HandleBattleEnd 中释放 inputBuffer、lastConsumedMove
- [x] 4.7 单元测试/节点验证：Input Buffer — 编写测试场景：(a) 正常入队消费：帧号 100 的操作入队到 slot 0，frameid=100 时消费成功且 slot 变 null；(b) 缺帧补偿：slot 为 null 时验证移动值=上帧值、攻击列表为空；(c) 覆写：同一 slot 连续写入两次，验证取出的是后写入的操作

## 5. 客户端累加器改造（InvokeRepeating → Update 驱动）

- [x] 5.1 新增累加器字段：`_tickAccumulator`（float）、`currentTickInterval`（float，初始=0.016f）、`_battleTickActive`（bool）
- [x] 5.2 移除 InvokeRepeating：删除 `InvokeRepeating("Send_operation", _time, _time)` 及对应的 `CancelInvoke`
- [x] 5.3 BattleTick 方法抽取：将原 `Send_operation()` 的全部逻辑提取为 `BattleTick()` 方法（纯逻辑帧推进 + 发包），确保内部不依赖 Time.deltaTime
- [x] 5.4 Update 累加器驱动：在 BattleManger.Update() 中实现累加器循环 `while(_tickAccumulator >= currentTickInterval)`，循环体调用 `BattleTick()`，循环后 `_tickAccumulator -= currentTickInterval`（保留余量）。maxCatchupPerUpdate=3，超出部分丢弃（钳位 accumulator）
- [x] 5.5 管线顺序：Update 中严格按 DrainAndDispatch → Ping 调度 → 目标帧号计算/tick 调节 → 累加器循环 的顺序执行
- [x] 5.6 战斗结束清理：`_tickAccumulator=0`、`currentTickInterval=0.016f`、`_battleTickActive=false`
- [x] 5.7 单元测试/节点验证：累加器驱动 — (a) 启动战斗，验证 InvokeRepeating 不再被调用；(b) 模拟 Update deltaTime=0.048f，验证 BattleTick 被调用 3 次（0.048/0.016=3）；(c) 模拟 deltaTime=0.010f（<0.016），验证 BattleTick 不被调用但 accumulator 保留 0.010f 到下次 Update

## 6. 动态 Tick 调节（平衡公式 + Clamp + 平滑）

- [x] 6.1 新增配置常量（ConstValue.cs）：`inputBufferSize=2`、`adjustRate=0.05f`、`minSpeedFactor=0.85f`、`maxSpeedFactor=1.15f`、`smoothRate=5.0f`、`maxCatchupPerUpdate=3`、`pingIntervalMs=200f`
- [x] 6.2 目标帧号计算方法：`CalcTargetFrame()` 实现平衡公式 `targetFrame = sync_frameID + ceil(rttFrames/2) + inputBufferSize`，RTT 未初始化时用默认超前量 `inputBufferSize`
- [x] 6.3 Tick 调节方法：`AdjustTickInterval()` 计算 `frameDiff = targetFrame - predicted_frameID`，算 `targetSpeedFactor = Clamp(1 + frameDiff*adjustRate, min, max)`，用 `Lerp(actual, target, smoothRate*deltaTime)` 平滑过渡，输出 `currentTickInterval = 0.016f / actualSpeedFactor`
- [x] 6.4 严重超前暂停：`frameDiff < -5` 时跳过本次 Update 的累加器循环
- [x] 6.5 接入 Update 管线：在累加器循环前调用 `CalcTargetFrame()` + `AdjustTickInterval()`，将结果写入 `currentTickInterval`
- [x] 6.6 状态清理：战斗结束时 `actualSpeedFactor=1.0f`、`currentTickInterval=0.016f`
- [x] 6.7 单元测试/节点验证：动态追帧 — (a) 目标帧号：syncFrame=95, smoothedRTT=80ms, inputBuffer=2 → targetFrame=100；(b) 加速：frameDiff=2 → speedFactor=1.1, tickInterval≈0.01455；(c) 减速：frameDiff=-3 → speedFactor=0.85, tickInterval≈0.01882；(d) Clamp：frameDiff=10 → speedFactor 不超过 1.15；(e) 平滑：actualSpeedFactor 从 1.0 到 1.15 不跳变（单帧变化量 < 0.02）；(f) 严重超前：frameDiff=-6 → 累加器循环被跳过

## 7. 客户端上报帧号适配

- [x] 7.1 `uploadOperationId` 改为 `predicted_frameID`：Send_operation（现 BattleTick）中将 `uploadOperationId = sync_frameID + 1` 改为 `uploadOperationId = predicted_frameID`
- [x] 7.2 验证服务端 Ack 钳位逻辑兼容：确认 `UpdatePlayerOperation` 中 `incomingAckFrameId = syncFrameId - 1` 和 `ackUpperBound = max(0, frameid-1)` 在新帧号下仍正确（predicted_frameID 可能远大于 frameid，钳位到 frameid-1）
- [x] 7.3 单元测试/节点验证：帧号对齐 — (a) 客户端 predicted=105, sync=98 → uploadOperationId=105；(b) 服务端 frameid=100，收到 uploadOperationId=105 → ack 钳位到 99

## 8. 文档更新

- [x] 8.1 更新 `Assets/CLAUDE.md`：§3.3 发送与本地预测层（InvokeRepeating → 累加器驱动 + 动态 tick）、§7 参数值表（新增追帧参数）、新增 §X 动态追帧系统章节
- [x] 8.2 更新 `Assets/Docs/ForServer.md`：新增 Input Buffer 章节、UDP Ping/Pong 路由说明
- [x] 8.3 更新 `D:/unity/hyld-master/hyld-master/BothSide.md`：记录 proto timestamp 字段、帧时长统一、uploadOperationId 语义变更、input buffer 大小参数

## 9. Pong NetSim 同步（服务端修复）

- [x] 9.1 诊断 RTT 与 NetSim 不匹配：Pong 绕过 NetSim 直接发送导致 RTT=7ms（实际模拟延迟 60-150ms），客户端动态追帧目标帧号计算偏低，攻击 REJECT 率升高
- [x] 9.2 `ClientUdp.cs` 新增 `LZJUDP.SimDropRate/SimDelayMinMs/SimDelayMaxMs` 公共静态字段，Pong 发送路径增加 NetSim 丢包/延迟模拟
- [x] 9.3 `Battle.cs` `BeginBattle` 同步 NetSim 参数到 `LZJUDP`，`HandleBattleEnd` 清零参数
- [x] 9.4 验证：50% 丢包 + 60-150ms 延迟下，RTT 从 7ms 修正为 ~117ms，攻击确认率从 78%(7/9) 提升至 100%(11/11)
