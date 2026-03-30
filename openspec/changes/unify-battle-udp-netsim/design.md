## Context

当前战斗期 UDP 网络模拟存在三处割裂：一是 Pong 在 `Server/Server/ClientUdp.cs` 中被单独拦截并做下行 NetSim，二是权威帧在 `Server/Server/BattleController.Network.cs` 中使用另一套局部延迟/丢包逻辑，三是上行战斗操作包未经过与下行一致的统一模拟。这导致客户端 RTT 观测、服务端输入缓冲承压、权威帧实际到达节奏三者不一致，客户端 `CalcTargetFrame()` 与 `AdjustTickInterval()` 依据的 RTT 不能稳定代表真实战斗帧流。

本改动采用“服务端集中式、双向生效”的建模方式：不在 Unity 客户端新增本地 NetSim 模块，而是在服务端统一控制 client→server 的上行 UDP 进入业务前的到达时机，以及 server→client 的下行 UDP 实际发出时机。该改动同时触及服务端 UDP 接收/发送、战斗生命周期、客户端 RTT/追帧行为观测和双端联调文档。项目现状要求仅在战斗阶段启用模拟，避免影响大厅、房间、匹配等非战斗链路；同时战斗控制类 UDP 包仍需进入统一入口，但应允许采用比高频数据包更保守的默认策略，以降低开局/收尾偶发故障对调试的污染。

## Goals / Non-Goals

**Goals:**
- 在战斗生命周期内，为所有战斗相关 UDP 包提供统一 NetSim 入口，消除当前散落在 Pong、权威帧等处的局部实现。
- 采用服务端集中式建模，让上行战斗操作、下行权威帧以及 Ping/Pong 共享同一套战斗期网络参数来源，而不要求客户端新增本地 NetSim 代码。
- 明确梳理各战斗 UDP ActionCode 的上下行方向、模拟插入点与默认策略，使 RTT 观测与实际战斗流量处于一致实验条件。
- 保持 BattleReady 建链、BattleStart、GameOver 等控制包纳入统一框架，但允许采用保守默认策略，避免战斗控制面过于脆弱。
- 保持非战斗阶段 UDP 行为不变，确保网络模拟范围仅限战斗生命周期。
- 为后续联调提供统一日志口径，便于区分上行/下行、数据类/控制类 UDP 包的模拟结果。

**Non-Goals:**
- 不在本次设计中修改客户端追帧算法公式本身，例如 `targetFrame = sync + RTT/2 + inputBuffer` 的核心策略。
- 不在客户端新增本地 NetSim 模块或镜像网络模拟器；客户端侧仅承担观测、联调验证与必要的日志/文档同步职责。
- 不引入更高阶网络模型，如 burst loss、按玩家独立网络画像、动态网络场景切换或严格重排模拟。
- 不改变 TCP 大厅/房间/匹配链路，也不把非战斗期 UDP 纳入统一 NetSim。
- 不在本次改动中重做战斗协议结构或可靠传输机制。

## Decisions

### 1. 统一战斗 UDP NetSim 入口，仅在战斗生命周期内启用

**Decision**
在 `LZJUDP` 层引入统一的战斗 UDP NetSim 入口。所有战斗期 UDP 包在进入业务处理前或实际发送前，都先经过同一套模拟判定；战斗开始时由 `BattleController.BeginBattle` 激活配置，战斗结束时由 `HandleBattleEnd` 清空配置。

**Rationale**
现有参数生命周期已经与 battle 生命周期耦合（`Battle.cs:305`、`BattleController.Network.cs:205`），继续沿用这一时机成本最低，也最符合“仅战斗期启用”的边界。将统一入口收敛到 `LZJUDP` 可以避免在 `ClientUdp.cs` 和 `BattleController.Network.cs` 各自维持一套局部逻辑。

**Alternatives considered**
- 继续保留各业务点局部模拟：实现快，但会继续造成口径漂移和双重模拟风险。
- 在客户端额外加入镜像模拟层：可以补齐上行视角，但无法替代服务端对真实输入到达节奏的控制，且会增加双端耦合复杂度。

### 2. 所有战斗相关 UDP 包都经过统一入口，但按包型分策略

**Decision**
统一 NetSim 对以下战斗期包型生效：`Ping`、`Pong`、`BattlePushDowmPlayerOpeartions`、`BattlePushDowmAllFrameOpeartions`、`BattleStart`、`ClientSendGameOver`、`BattlePushDowmGameOver`。其中数据类包（Ping/Pong/操作包/权威帧）使用完整 delay/drop/jitter；控制类包（BattleStart/GameOver）同样经过统一入口，但默认采用保守策略（允许延迟，默认不丢或极低丢）。BattleReady 保持不参与高风险丢包，以保护 endpoint 建链。

**Rationale**
“统一入口”解决的是代码和口径分裂问题；“分策略”解决的是工程可调试性问题。BattleReady 负责 `TryResolveBattleID` 路由建立，若以高概率丢弃，会把建链故障与战斗延迟实验混在一起。控制类包也属于战斗生命周期的一部分，进入同一入口才能保证框架统一，但应避免第一版就把开局和收尾做成高脆弱链路。

**Alternatives considered**
- 所有战斗 UDP 包完全同权同策略：概念最纯，但会显著增加开局/结束偶发失败，降低联调效率。
- 只对数据类包做统一模拟：实现更简单，但会继续保留控制类绕行路径，难以保证“所有战斗 UDP 都统一经过同一出口/入口”。

### 3. 上下行映射由服务端统一管理：上行在收包后分发前模拟，下行在 `LZJUDP.Send` 统一出口模拟

**Decision**
上行包在 `LZJUDP.RecvThread` 成功解析 `MainPack` 后、路由到 BattleController/Pong 处理前进入统一 NetSim；下行包在所有战斗 UDP 发送调用实际 `SendTo` 之前进入统一 NetSim，由统一逻辑决定立即发送、延迟发送或丢弃。客户端不新增本地 NetSim，仅继续发送原始 UDP 包并消费被服务端统一模拟后的下行结果。

战斗阶段各 ActionCode 的方向与默认策略定义如下：
- `BattleReady`：client → server，进入统一入口，但使用建链保护策略，不采用与高频数据包相同的激进丢包。
- `BattlePushDowmPlayerOpeartions`：client → server，数据类策略，使用完整 delay/drop/jitter。
- `Ping`：client → server，数据类策略，使用完整 delay/drop/jitter。
- `ClientSendGameOver`：client → server，控制类策略，允许延迟，默认使用保守丢包策略。
- `BattleStart`：server → client，控制类策略，允许延迟，默认使用保守丢包策略。
- `Pong`：server → client，数据类策略，使用完整 delay/drop/jitter。
- `BattlePushDowmAllFrameOpeartions`：server → client，数据类策略，使用完整 delay/drop/jitter。
- `BattlePushDowmGameOver`：server → client，控制类策略，允许延迟，默认使用保守丢包策略。

**Rationale**
这样可以最小化调用点改动：上行链路集中于 `RecvThread`，下行链路集中于 `Send`。虽然代码集中在服务端，但逻辑上已经覆盖 client→server 与 server→client 两个方向。上行延迟可以真实影响服务端 `UpdatePlayerOperation` 的到达时机、Input Buffer 缺帧概率和攻击超时判定；下行延迟可以统一作用于权威帧、Pong 与控制类包。客户端继续保留现有 Ping、RTT 平滑与追帧逻辑，使其在统一 NetSim 条件下自然观测更真实的网络结果。

**Alternatives considered**
- 在 BattleController 内部对上行操作单独延迟：无法覆盖 Ping/Pong 与控制类包，也会让 NetSim 继续散落在业务层。
- 在每个调用点分别包一层 helper：表面统一，实则仍依赖调用方自觉，容易遗漏。
- 在客户端增加镜像 NetSim：能增加视觉上的“对称感”，但会重复建模，且不如服务端集中控制更易保证上下行口径一致。
### 4. 删除旧的局部 Pong/权威帧模拟分支，避免双重模拟和假掉包

**Decision**
实现统一战斗 UDP NetSim 后，移除 `ClientUdp.cs` 中 Pong 的局部 delay/drop 逻辑，以及 `BattleController.Network.cs:50` 中权威帧的局部 NetSim 分支。权威帧当前的 drop 逻辑必须修正为真正丢弃，否则会继续制造“记录 DROP 日志但仍发送”的假象。

**Rationale**
统一入口只有在不存在绕行和重复判定时才有意义。保留局部逻辑会造成同一包被模拟两次，或者不同包型继续吃到不同规则，回到现有问题。修掉权威帧假掉包是保证实验可信度的最低要求。

**Alternatives considered**
- 先保留旧逻辑，再逐步迁移：迁移期会出现双重模拟与难以解释的延迟分布，调试代价高。

### 5. 日志与统计必须按方向和包型统一输出

**Decision**
统一 NetSim 层输出结构化日志，至少包含方向（uplink/downlink）、包型（ActionCode）、endpoint、battleId、模拟决策（pass/delay/drop）、delayMs 和是否处于保守策略。

**Rationale**
本改动的目的之一是让 RTT 与真实战斗帧体验可被验证。没有统一日志，就很难判断 Ping、操作包、权威帧是否真的经过了同一套模拟口径，也难以联调 Input Buffer 缺帧与客户端 RTT 波动之间的关系。

**Alternatives considered**
- 复用现有零散 `Logging.Debug.Log`：信息分布在多个文件，无法形成单一解释口径。

## Risks / Trade-offs

- **[BattleReady 不参与高风险丢包会让“所有战斗 UDP 完全同权”不成立]** → 通过规格明确“统一入口”而非“统一策略”，并在日志中标记控制/建链包策略，保持设计可解释。
- **[上行延迟/丢包会显著改变 Input Buffer 与攻击超时行为]** → 在规格中明确这是预期效果，并通过集成验证观察缺帧补偿、攻击 REJECT 率与 RTT 变化。
- **[控制类包若默认也高丢包，会导致开局/结束异常污染联调]** → 第一版采用保守策略，后续若需要更激进实验再单独扩展参数。
- **[统一入口迁移不彻底会造成双重模拟]** → 实施任务中显式列出删除旧 Pong/权威帧局部分支的步骤，并在联调时检查日志是否只出现统一入口记录。
- **[ThreadPool + Sleep 继续扩大使用会让行为更难预测]** → 第一版允许沿用现有延迟调度方式，但把策略决策集中到统一层；若后续再扩展 reorder/burst loss，再考虑独立调度器。
