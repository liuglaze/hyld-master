## ADDED Requirements

### Requirement: LZJUDP 支持多战斗回调注册
LZJUDP SHALL 支持按 battleID 注册和注销回调处理函数，同时维护多个活跃战斗的回调。注册接口 SHALL 接受 battleID 和 Action<MainPack> 参数。

#### Scenario: 注册第二场战斗回调
- **WHEN** 战斗 A 已注册回调，战斗 B 调用注册
- **THEN** 两场战斗的回调 SHALL 同时存在于 LZJUDP 的处理字典中，战斗 A 的回调不被覆盖

#### Scenario: 战斗结束注销回调
- **WHEN** 战斗 A 结束，调用注销接口传入 battleID
- **THEN** LZJUDP SHALL 移除战斗 A 的回调，不影响其他战斗的回调

### Requirement: LZJUDP 按 battleID 路由 UDP 消息
LZJUDP 收到 UDP 包后 SHALL 从包内容中提取战斗标识，路由到对应的 BattleController 回调。

#### Scenario: 收到帧操作包
- **WHEN** LZJUDP 收到 ActionCode 为 BattlePushDowmPlayerOpeartions 的 UDP 包
- **THEN** SHALL 从 pack.BattleInfo.SelfOperation.Battleid 提取 battleUserID，通过 BattleManage.DIC_BattleIDs 反查 battleID，路由到正确的 BattleController

#### Scenario: 收到 BattleReady 包
- **WHEN** LZJUDP 收到 ActionCode 为 BattleReady 的 UDP 包
- **THEN** SHALL 从 pack.Battleplayerpack[0].Battleid 提取 battleUserID，通过 BattleManage.DIC_BattleIDs 反查 battleID，路由到正确的 BattleController

#### Scenario: 收到 GameOver 包
- **WHEN** LZJUDP 收到 ActionCode 为 ClientSendGameOver 的 UDP 包
- **THEN** SHALL 从 pack.Str 解析 battleId（battleUserID），通过 BattleManage.DIC_BattleIDs 反查 battleID，路由到正确的 BattleController

#### Scenario: 收到无法路由的包
- **WHEN** LZJUDP 收到的 UDP 包无法提取有效 battleID 或 battleID 不在已注册的处理字典中
- **THEN** SHALL 记录警告日志并丢弃该包，不影响其他战斗的正常运行

### Requirement: LZJUDP 回调字典线程安全
LZJUDP 的回调注册字典 SHALL 在锁保护下进行读写，因为注册/注销来自不同线程，而 RecvThread 线程持续读取。

#### Scenario: 注册回调同时接收 UDP 包
- **WHEN** BattleController 构造函数注册回调（来自 ThreadPool 线程），同时 RecvThread 正在查字典路由
- **THEN** 字典操作 SHALL 串行执行，不抛出异常
