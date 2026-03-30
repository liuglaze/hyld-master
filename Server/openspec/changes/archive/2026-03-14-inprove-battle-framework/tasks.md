1. Battle context 注册与查询收口

- [x] 1.1 在 `Server/Battle.cs` 中引入 `BattleContext` 模型，收拢 battleId、FightPattern、MatchUsers、PlayerUids、UidToBattlePlayerId 与 `BattleController` 引用
- [x] 1.2 调整 `BattleManage` 的内部索引为 `battleId -> BattleContext` 与 `uid -> battleId`，并补充 `TryGetBattleContext`、`TryGetBattleContextByUid` 等查询接口（文件：`Server/Battle.cs`）
- [x] 1.3 重写 `BeginBattle` / `FinishBattle` 的创建与回收流程，使 battle context 与 uid 映射在同一生命周期内完成注册和清理（文件：`Server/Battle.cs`）

## 2. 匹配到战斗的交接重构

- [x] 2.1 在 `Controller/Controllers.cs` 中为匹配成功结果引入显式数据结构（如 `MatchResult` / `MatchedPlayerEntry`），替代裸 `Tuple<int,string,string>` 作为 battle 创建输入
- [x] 2.2 调整 `MatchingController.StartFighting` 与相关调用路径，使匹配层只负责输出匹配结果，由 battle 层入口负责创建 `BattleContext` 和 `BattleController`（文件：`Controller/Controllers.cs`, `Server/Battle.cs`）
- [x] 2.3 补齐匹配退出时 `PlayerIDMapRoomDic` 清理与空房回收逻辑，确保取消匹配、掉线和开战后不会残留脏状态（文件：`Controller/Controllers.cs`）



##  3. 边界控制与命名统一 -

3.1 在 `Server/Battle.cs`、`Server/ClientUdp.cs`、`Controller/Controllers.cs` 中统一使用 `battleId`、`battlePlayerId`、`uid` 的命名语义，并保留与现有 protobuf 字段的兼容映射 

- [x] 3.2 调整 `ClearSenceController` 通过 `BattleManage` 查询接口解析 battle context，去掉对内部并行字典的直接拼接访问（文件：`Controller/Controllers.cs`, `Server/Battle.cs`） 
- [x] - [x] 3.3 调整 `LZJUDP` 路由与端点信息结构，使 UDP 侧基于统一命名和 battle context 查询工作，并对无法解析上下文的迟到包执行日志记录与安全丢弃（文件：`Server/ClientUdp.cs`, `Server/Battle.cs`） - [ ] Battle -> BattleReady -> 帧同步 -> GameOver -> BattleReview”主链路，确认 TCP/UDP 双通道未被破坏（文件：`Server/Battle.cs`, `Server/ClientUdp.cs`, `Controller/Controllers.cs`）



## 4. 验证与回归检查   

- [x] 4.1 执行 `dotnet build Server.sln`，确认 battle context 与匹配重构后的服务端可编译通过   

- [x] 4.2.1 验证战斗创建链路：`MatchingController.StartFighting -> BattleManage.TryBeginBattle -> BattleController`，确认 `battleId`、`uid -> battleId`、`uid -> battlePlayerId` 在创建阶段注册完整（文件：`Controller/Controllers.cs`, `Server/Battle.cs`）

- [x] 4.2.2 验证进入战斗准备链路：`StartEnterBattle -> ClearSenceController -> BattleReady`，确认 TCP 通知与 UDP 准备阶段仍基于同一 battle context 工作（文件：`Controller/Controllers.cs`, `Server/Battle.cs`, `Server/ClientUdp.cs`）

- [x]  4.2.3 验证开战与帧同步链路：`BattleReady -> BattleStart -> BattleLoop`，确认 `ClientUdp` 的端点路由不会混用不同 battle 的上下文（文件：`Server/Battle.cs`, `Server/ClientUdp.cs`）  - [x] 4.2.4 验证结算与回收链路：`GameOver -> FinishBattle -> BattleReview`，确认 battle context 与 uid 映射会被清理，不残留脏状态（文件：`Server/Battle.cs`, `Controller/Controllers.cs`）

- [x] 4.3.1 验证多场战斗并发场景：确认 `BattleManage` 与 `ClientUdp` 都按 `battleId` 隔离上下文，不会串场（文件：`Server/Battle.cs`, `Server/ClientUdp.cs`）

- [x] 4.3.2 验证玩家中途退出匹配场景：确认房间与匹配索引会清理，且不会创建缺员 battle（文件：`Controller/Controllers.cs`）

- [x] 4.3.3 验证战斗结束后迟到包场景：确认无法解析 battle context 的 UDP 包会记录日志并安全丢弃，不抛异常（文件：`Server/ClientUdp.cs`, `Server/Battle.cs`）

  

  

  

  

