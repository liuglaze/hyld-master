## Purpose
TBD: 规范服务端核心并发共享状态的锁保护与可见性，避免多线程下的数据竞争和异常。

## Requirements

### Requirement: BattleManage 字典操作线程安全
BattleManage 单例的所有共享字典（dic_battles、DIC_BattlePlayerUIDs、DIC_BattleIDs、dic_matchUserInfo、dic_pattern）的读写操作 SHALL 在锁保护下进行。battleID 的自增操作 SHALL 是原子的。

#### Scenario: 两场战斗同时开始和结束
- **WHEN** 战斗 A 正在调用 FinishBattle 清理字典，同时匹配系统调用 BeginBattle 创建战斗 B
- **THEN** 两个操作 SHALL 串行执行，字典内部状态保持一致，不抛出异常

#### Scenario: FinishBattle 使用正确的 battleID 参数
- **WHEN** FinishBattle(_battleID) 被调用
- **THEN** SHALL 使用参数 _battleID 而非成员变量 battleID 访问 dic_pattern，确保取到正确的战斗模式

#### Scenario: FinishBattle 时玩家已断线
- **WHEN** FinishBattle 遍历玩家发送战报，某玩家已断线（GetClientByID 返回 null）
- **THEN** SHALL 跳过该玩家，不抛出 NullReferenceException，继续处理其余玩家

### Requirement: BattleController 实例级锁隔离
每个 BattleController 实例 SHALL 使用独立的锁对象，不同战斗实例的帧同步操作 SHALL 互不阻塞。

#### Scenario: 两场战斗同时推帧
- **WHEN** 战斗 A 的 BattleLoop 正在 CollectAndBroadcastCurrentFrame，同时战斗 B 的 BattleLoop 也在推帧
- **THEN** 两场战斗 SHALL 独立执行，互不等待

### Requirement: BattleController 帧同步状态完整保护
BattleController 中被 BattleLoop 线程和 UDP 接收线程共同访问的状态（dic_playerAckedFrameId、dic_playerGameOver、oneGameOver、allGameOver）SHALL 在锁保护下读写。

#### Scenario: UDP 线程更新 playerAckedFrameId 同时 BattleLoop 读取
- **WHEN** UpdatePlayerOperation 写入 dic_playerAckedFrameId，同时 send_unsync_frames 读取同一字典
- **THEN** 读写 SHALL 串行执行，不出现脏读

#### Scenario: UDP 线程收到 GameOver 同时 BattleLoop 检查 oneGameOver
- **WHEN** UpdatePlayerGameOver 设置 oneGameOver = true，同时 BattleLoop 正在检查 oneGameOver
- **THEN** 标志位变更 SHALL 对 BattleLoop 线程可见，且 HandleBattleEnd SHALL 只执行一次

### Requirement: 战斗结束路径唯一化
战斗结束 SHALL 只通过 BattleLoop 的主循环触发 HandleBattleEnd，UpdatePlayerGameOver SHALL 只负责设置标志位（oneGameOver/allGameOver/dic_playerGameOver），不直接触发结束流程。

#### Scenario: 单个玩家 GameOver
- **WHEN** 收到第一个玩家的 ClientSendGameOver
- **THEN** UpdatePlayerGameOver 设置 oneGameOver = true，BattleLoop 在下一帧检测到后调用 HandleBattleEnd

#### Scenario: 所有玩家 GameOver
- **WHEN** 所有玩家都发送了 ClientSendGameOver
- **THEN** BattleLoop 通过 allGameOver 倒计时后统一调用 HandleBattleEnd，不存在重复调用

### Requirement: MatchingController 集合线程安全
MatchingController 的 MathingDic 和 PlayerIDMapRoomDic SHALL 在锁保护下进行所有读写操作。

#### Scenario: 两个玩家同时加入匹配
- **WHEN** 两个 IOCP 回调线程同时调用 AddMatchingPlayer
- **THEN** MathingDic 和 PlayerIDMapRoomDic 的操作 SHALL 串行执行，数据保持一致

### Requirement: FriendRoom 和 FriendRoomController 集合线程安全
FriendRoom._clientsList 和 FriendRoomController._rooms 的所有读写操作 SHALL 在锁保护下进行。

#### Scenario: 玩家加入房间同时另一玩家退出
- **WHEN** 一个 IOCP 线程处理 Join 同时另一个处理 Exit
- **THEN** _clientsList 操作 SHALL 串行执行，不出现 InvalidOperationException

#### Scenario: 广播消息同时有人加入
- **WHEN** BroadCastTCP 遍历 _clientsList 同时有新玩家 Join
- **THEN** SHALL 不抛出集合修改异常（使用快照遍历或锁保护）

### Requirement: Client.FriendsDic 线程安全
Client.FriendsDic 的遍历和修改 SHALL 在锁保护下进行。

#### Scenario: UpdateMyselfInfo 遍历同时 BordCaseToFriendLogout 删除
- **WHEN** 一个线程遍历 FriendsDic 通知好友，另一个线程删除好友
- **THEN** SHALL 不抛出集合修改异常

### Requirement: Client.Send 写队列原子性
Client.Send 的 Enqueue + 队列长度判断 + BeginSend 启动决策 SHALL 是原子操作，不允许两个 BeginSend 同时进行。

#### Scenario: 两个线程同时调用 Send
- **WHEN** 线程 A 和线程 B 同时调用同一 Client 的 Send
- **THEN** SHALL 只有一个线程启动 BeginSend，另一个仅入队等待链式发送
