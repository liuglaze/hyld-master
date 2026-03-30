## Why

服务器当前存在严重的多线程并发缺陷：核心业务对象（BattleManage、Controller、FriendRoom）的共享集合几乎全部无锁保护，多玩家并发操作会导致 Dictionary 内部结构损坏、服务器崩溃。同时 LZJUDP 的回调注册机制用直接赋值而非事件订阅，导致多场战斗并存时只有最后一场能收到 UDP 数据。这些问题使得服务器在超过 1 场同时战斗时必然出错，必须修复才能支持正常的多人在线运行。

## What Changes

- **BREAKING**: 重构 LZJUDP 回调分发机制，从单 delegate 覆盖改为按 battleID 路由，支持多场战斗并行收包
- 为 BattleManage 单例的所有共享字典添加同步保护
- 修复 BattleManage.FinishBattle 中 `dic_pattern[battleID]` 误用成员变量而非参数 `_battleID` 的逻辑 bug
- 将 BattleController.lockThis 从 static 改为实例锁，消除多场战斗间不必要的互斥
- 为 BattleController 中 `dic_playerAckedFrameId`、`dic_playerGameOver`、`oneGameOver`/`allGameOver` 等帧同步相关状态补全锁保护
- 为 MatchingController 的 `MathingDic`、`PlayerIDMapRoomDic` 添加同步保护
- 为 FriendRoomController._rooms 和 FriendRoom._clientsList 添加同步保护
- 为 Client.FriendsDic 添加同步保护
- 修复 Client.Send 写队列的潜在双重 BeginSend 竞态
- 修复 BattleManage.FinishBattle 中 `c.Send(mainPack)` 未做空判断导致的 NPE
- 消除 UpdatePlayerGameOver 与 BattleLoop 的重复结束逻辑，统一战斗结束路径

## Capabilities

### New Capabilities
- `thread-safety`: 服务器核心共享状态的线程安全保护，覆盖 BattleManage、Controller、FriendRoom、Client 各层的并发访问控制
- `udp-multi-battle-dispatch`: LZJUDP 多战斗并行 UDP 消息分发机制，支持同时进行多场独立战斗

### Modified Capabilities
<!-- 无已有 spec 需要修改 -->

## Impact

- **核心文件**: Server/Battle.cs, Server/ClientUdp.cs, Server/Client.cs, Server/FriendRoom.cs, Controller/Controllers.cs
- **协议层**: 无 proto 定义变更，UDP 包需携带 battleID 用于服务端路由（需确认客户端是否已在包中包含此字段）
- **兼容性**: LZJUDP.AddListenRecv 接口签名变更为多路注册/注销，BattleController 构造和销毁流程需配套调整
- **帧同步一致性**: lockThis 从 static 改实例后，各战斗帧推进完全独立，不会再互相阻塞
- **客户端**: 如果 UDP 包中已包含 battleID 或 battlePlayerID 字段则无需客户端改动，否则需要客户端配合在 UDP 包中添加标识
