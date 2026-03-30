## Why

当前服务端虽然已经具备多场战斗并行的基础结构，但“匹配 -> 建立战斗 -> UDP 路由 -> 清理回收”这条链路仍然依赖多张平行字典、混淆的 battle 标识命名以及分散的状态访问方式。随着多场战斗并发、断线、迟到包和后续功能扩展增多，这些问题会持续放大，导致状态串扰、边界异常和维护成本上升，因此需要先收敛战斗框架本身。

## What Changes

- 新增战斗上下文与注册能力，将战斗模式、玩家列表、uid 映射、控制器实例等元数据从平行字典收敛为统一的 battle context
- 规范匹配成功后的建战斗入口，引入明确的匹配结果到战斗实例的转换流程，减少 MatchingController 与 BattleController 的直接耦合
- 统一 battleId、battlePlayerId、uid 的命名与查询接口，修正清场、战斗准备、战斗结束等流程中的标识混用问题
- 补齐匹配房退出清理与空房回收，避免 PlayerIDMapRoomDic 和房间集合残留脏状态
- 收敛 ClearSence、UDP 路由等边界流程对 BattleManage 内部字典的直接访问，统一通过 battle context 查询
- 保持现有 TCP/UDP 双通道与 protobuf 主协议兼容，不在本次变更中引入新的网络协议格式

## Capabilities

### New Capabilities
- `battle-context-registry`: 定义战斗上下文、全局战斗注册与统一查询接口，覆盖 battle 创建、查询、结束与回收流程
- `match-to-battle-lifecycle`: 定义从匹配房组局到战斗实例创建、房间清理、清场同步的生命周期边界与状态流转

### Modified Capabilities
- `thread-safety`: 共享状态的线程安全要求将扩展到 battle context 与匹配房回收路径，要求新的上下文访问入口在并发下保持一致性
- `udp-multi-battle-dispatch`: UDP 路由要求调整为与 battle context 和统一标识命名保持一致，避免 battleID 与 battlePlayerID 混用

## Impact

- **核心文件**: `Server/Battle.cs`, `Controller/Controllers.cs`, `Server/ClientUdp.cs`，并可能影响 `Server/FriendRoom.cs` 与 `Server/Client.cs` 的调用方式
- **帧同步一致性**: 战斗实例状态与路由查询收口后，多场战斗的 ready、推帧、game over 与回收路径边界更清晰，可降低串场和迟到包误处理风险
- **TCP/UDP 双通道兼容性**: 保持 TCP 负责非实时建战斗/房间流程、UDP 负责战斗同步的双通道架构，不要求本次修改传输层模型
- **协议层**: 预期不修改 proto 与 DAO；Controller 和 Battle 层的内部模型与调用接口会调整。如后续发现客户端现有字段无法稳定区分 battleId / battlePlayerId，再单独提出协议变更
