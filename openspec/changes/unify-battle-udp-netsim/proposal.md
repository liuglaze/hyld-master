## Why

当前战斗期 UDP 延迟/丢包模拟分散在 Pong 与权威帧等局部链路中，导致 RTT 测量口径、权威帧实际到达口径、客户端追帧依据三者不一致，无法稳定模拟真实战斗网络环境。现在需要将战斗阶段的 UDP 流量统一纳入由服务端集中管理的同一套网络模拟层，通过服务端统一控制上行收包前后的分发时机与下行发包时机，使客户端追帧、服务端输入处理与权威帧下发建立一致的实验条件，而不要求客户端新增本地 NetSim 模块。

## What Changes

- 新增由服务端集中管理的战斗阶段统一 UDP NetSim 能力，在战斗生命周期内对战斗相关 UDP 包应用统一的延迟、丢包和抖动策略。
- 将上行战斗操作包、下行权威帧以及 Ping/Pong 纳入同一套战斗期网络模拟口径，并明确上行在服务端收包后、业务分发前模拟，下行在服务端发包前模拟。
- 保留客户端现有 Ping、RTT 平滑与追帧代码路径，不新增客户端本地 NetSim 模块；客户端侧主要承担观测、验证与必要的日志/文档同步职责。
- 保留非战斗阶段 UDP 行为不变，避免登录、房间、匹配等非战斗链路受到实验性网络模拟影响。
- 收敛现有分散在 `ClientUdp.cs` 与 `BattleController.Network.cs` 中的局部模拟逻辑，避免双重模拟或口径漂移。
- 为战斗控制类 UDP 包定义保守策略，使其经过统一入口但不默认采用与高频战斗数据包相同的激进丢包策略。

## Capabilities

### New Capabilities
- `battle-udp-netsim`: 定义战斗阶段统一 UDP 网络模拟的作用范围、包分类、生命周期与一致性要求。

### Modified Capabilities
- 无

## Impact

- 服务端 UDP 收发与分发：`Server/Server/ClientUdp.cs`
- 服务端战斗网络下行：`Server/Server/BattleController.Network.cs`
- 服务端战斗生命周期：`Server/Server/Battle.cs`
- 客户端 RTT/追帧观测与验证链路：`Client/Assets/Scripts/Server/Manger/Battle/BattleManger.cs`、`Client/Assets/Scripts/Server/Manger/Battle/BattleData.Rtt.cs`
- 客户端/服务端联调与跨端行为文档：`BothSide.md`、相关 CLAUDE/联调文档
