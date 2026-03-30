## 1. LZJUDP 多战斗分发机制重构

- [x] 1.1 重构 LZJUDP.AddListenRecv 为按 battleID 注册：新增 `RegisterBattle(int battleID, Action<MainPack> handler)` 和 `UnregisterBattle(int battleID)` 方法，内部使用 `Dictionary<int, Action<MainPack>>` + 锁保护（文件: Server/ClientUdp.cs）
- [x] 1.2 重构 LZJUDP.RecvThread 路由逻辑：收到 UDP 包后按 ActionCode 提取 battleUserID，通过 BattleManage.DIC_BattleIDs 反查 battleID，查字典路由到对应 handler；无法路由时记录警告日志并丢弃（文件: Server/ClientUdp.cs）
- [x] 1.3 修改 BattleController 构造函数：将 `LZJUDP.Instance.AddListenRecv(Handle)` 改为 `LZJUDP.Instance.RegisterBattle(battleID, Handle)`（文件: Server/Battle.cs）
- [x] 1.4 在 HandleBattleEnd 中注销回调：调用 `LZJUDP.Instance.UnregisterBattle(battleID)`（文件: Server/Battle.cs）

## 2. BattleManage 线程安全

- [x] 2.1 新增实例锁 `_manageLock`，保护 BeginBattle 中的 battleID++、DIC_BattleIDs.Add、DIC_BattlePlayerUIDs.Add、dic_battles/dic_pattern/dic_matchUserInfo 写入（文件: Server/Battle.cs）
- [x] 2.2 保护 FinishBattle 中的 dic_battles.Remove、DIC_BattleIDs.Remove、DIC_BattlePlayerUIDs.Remove 等字典操作，Send 调用放在锁外（文件: Server/Battle.cs）
- [x] 2.3 修复 FinishBattle 第105行 `dic_pattern[battleID]` 改为 `dic_pattern[_battleID]`（文件: Server/Battle.cs）
- [x] 2.4 修复 FinishBattle 中 `server.GetClientByID(uid)` 返回 null 时的空判断（文件: Server/Battle.cs）

## 3. BattleController 帧同步线程安全

- [x] 3.1 将 `private static readonly object lockThis` 改为 `private readonly object _battleLock = new object()`，替换所有 lock(lockThis) 为 lock(_battleLock)（文件: Server/Battle.cs）
- [x] 3.2 将 dic_playerAckedFrameId 的写入（UpdatePlayerOperation 第408-411行）移入 lock 块内（文件: Server/Battle.cs）
- [x] 3.3 将 dic_playerGameOver、oneGameOver、allGameOver 的读写用 _battleLock 保护：UpdatePlayerGameOver 写入时加锁，BattleLoop 读取时加锁（文件: Server/Battle.cs）
- [x] 3.4 统一战斗结束路径：删除 UpdatePlayerGameOver 中的 Timer/WaitClientFinish 分支，只保留设置标志位逻辑；删除 waitBattleFinish 字段和 test/WaitClientFinish 方法（文件: Server/Battle.cs）

## 4. Controller 层线程安全

- [x] 4.1 为 MatchingController 新增实例锁 `_matchLock`，保护 MathingDic 和 PlayerIDMapRoomDic 的所有读写操作（AddMatchingPlayer、RemoveMatchingPlayer、Join、Exit、CloseClient）（文件: Controller/Controllers.cs）
- [x] 4.2 为 FriendRoomController 新增实例锁 `_roomLock`，保护 _rooms 列表的所有读写操作（CreateRoom、JoinFriendRoom、ExitRoom、RemoveFriendRoom、RejectInviteFriend）（文件: Controller/Controllers.cs）
- [x] 4.3 为 ClearSenceController 新增实例锁保护 _dic_ClearFinish 字典（文件: Controller/Controllers.cs）

## 5. FriendRoom 和 Client 线程安全

- [x] 5.1 为 FriendRoom 新增实例锁 `_roomLock`，保护 _clientsList 的所有操作（Join、Exit、BroadCastTCP、BroadcastToAll、GetPlayerInfo），广播时使用快照遍历（文件: Server/FriendRoom.cs）
- [x] 5.2 为 Client.FriendsDic 新增锁保护，UpdateMyselfInfo 和 GetActiveFriendInfoPack 遍历时使用快照（文件: Server/Client.cs）
- [x] 5.3 修复 Client.Send 竞态：将 count==1 判断和 BeginSend 调用移入 lock(writeQueue) 块内（文件: Server/Client.cs）

## 6. 验证

- [x] 6.1 执行 `dotnet build Server.sln` 确认编译通过，无错误无警告
- [x] 6.2 检查所有 lock 使用无嵌套死锁风险：确认不存在 锁A→锁B 和 锁B→锁A 的交叉持锁路径

## 7. 本轮执行记录（2026-03-13）

### 7.1 已完成并已落地
- LZJUDP 多战斗回调注册/注销与端点路由已落地（Server/ClientUdp.cs）。
- BattleManage / BattleController 并发保护与结束路径收敛已落地（Server/Battle.cs）。
- Controller 层 Matching / FriendRoom / ClearSence 锁保护已落地（Controller/Controllers.cs，包含 `RemoveFriendRoom` 加锁）。

### 7.2 本轮已收敛项（已完成）
- FriendRoom `_clientsList` 已全量加锁，并在 `GetPlayerInfo/BroadCastTCP/BroadcastToAll` 使用快照遍历（Server/FriendRoom.cs）。
- Client `FriendsDic` 已新增 `_friendsLock` 与线程安全访问方法；`UpdateMyselfInfo/GetActiveFriendInfoPack` 已改为快照遍历；DAO 侧下线路径已改调用 `RemoveFriend`（Server/Client.cs, DAO/UserData.cs）。
- Client.Send 已将入队 + `Count==1` 判断 + `BeginSend` 启动放入同一 `lock(writeQueue)` 临界区（Server/Client.cs）。
- 已执行 `dotnet build Server.sln`：0 error，存在环境/依赖警告（NETSDK1138、NU1701）。

### 7.3 额外验证结论（6.2）
- 关键锁对象为 `_manageLock`（BattleManage）、`_battleLock`（BattleController）、`_handlersLock`（LZJUDP）、`_matchLock/_roomLock/_clearLock`（Controllers）、`_roomLock`（FriendRoom）、`_friendsLock/writeQueue`（Client）。
- 未发现 A→B 与 B→A 的交叉嵌套持锁路径；外部调用（如 `Send`、`UpdateMyselfInfo`、`RemoveFriendRoom`）均在相应锁外执行，死锁风险可控。
