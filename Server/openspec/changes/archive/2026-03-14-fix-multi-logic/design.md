## Context

当前服务器采用多线程模型：TCP 监听线程、IOCP 回调线程池、UDP 接收线程、每场战斗一个帧同步线程、心跳检测线程。这些线程共享大量 Dictionary 和 List，但除了 Server.cs 层的 `_clients`/`_activeClient` 有锁保护外，其余共享状态基本无同步机制。

最关键的问题是 LZJUDP 单例的 Handle 回调采用直接赋值而非事件机制，导致多场战斗并存时只有最后创建的 BattleController 能收到 UDP 数据。

## Goals / Non-Goals

**Goals:**
- 消除所有已识别的多线程竞态条件，使服务器能安全地支持多场战斗同时进行
- 修复 LZJUDP 回调覆盖问题，使每场战斗都能独立收到自己的 UDP 消息
- 修复 BattleManage.FinishBattle 中的 battleID 变量误用 bug
- 统一战斗结束路径，消除 BattleLoop 和 UpdatePlayerGameOver 的重复处理

**Non-Goals:**
- 不重写网络层架构（不引入 async/await 替换 BeginXxx 模式）
- 不引入连接池替换每 Client 一个 MySqlConnection 的模式（留作后续优化）
- 不引入自动化测试框架（留作后续）
- 不修改 protobuf 协议定义或客户端代码
- 不做性能优化（如无锁并发集合 ConcurrentDictionary），只解决正确性问题

## Decisions

### D1: LZJUDP 多路分发 — 注册/注销 + battleID 路由

**选择**: LZJUDP 内部维护 `Dictionary<int, Action<MainPack>> _handlers`，以 battleID 为 key。BattleController 构建时注册、销毁时注销。UDP 收到包后从 `pack.BattleInfo.BattleUserInfo` 或 `pack.Battleplayerpack` 中提取 battleID，查字典路由。

**替代方案**: 改用 C# event（`event Action<MainPack>`）广播给所有 handler，每个 BattleController 自己过滤。
**放弃原因**: 广播模式下每个包都要通知所有战斗实例，浪费 CPU。字典路由是 O(1) 查找。

**替代方案**: 每场战斗创建独立 UDP socket。
**放弃原因**: 端口管理复杂，客户端需要知道目标端口，改动过大。

### D2: 锁策略 — 一律使用 lock + 普通 Dictionary

**选择**: 对所有需要保护的共享集合使用 `lock(obj)` + 普通 `Dictionary`/`List`。

**替代方案**: 使用 `ConcurrentDictionary`。
**放弃原因**: ConcurrentDictionary 的复合操作（先查后改）仍需额外同步，且项目当前代码风格统一用 lock，保持一致性更重要。引入混合模式反而增加复杂度。

### D3: BattleController.lockThis 改为实例锁

**选择**: 将 `private static readonly object lockThis` 改为 `private readonly object _battleLock = new object()`，每个 BattleController 实例独立。

**理由**: 当前 static 锁导致不同战斗的帧推进互相阻塞，而各战斗的 `dic_currentFrameOperationBuffer`、`dic_historyFrames` 等数据本就是实例级别的，不存在跨实例共享的需求。

### D4: BattleManage 同步 — 单例级锁

**选择**: BattleManage 新增 `private readonly object _manageLock = new object()`，保护 `dic_battles`、`DIC_BattlePlayerUIDs`、`DIC_BattleIDs`、`dic_matchUserInfo`、`battleID++` 等操作。

**注意**: `BeginBattle` 和 `FinishBattle` 的锁粒度要控制，只锁字典读写部分，不要把 `c.Send()` 包在锁内（避免死锁和阻塞）。

### D5: 战斗结束逻辑统一

**选择**: 删除 `UpdatePlayerGameOver` 中的 Timer + WaitClientFinish 分支，统一由 BattleLoop 的 `allGameOver` 倒计时处理。`oneGameOver` 触发后 BattleLoop 负责 `HandleBattleEnd`。`UpdatePlayerGameOver` 只负责设置标志位。

**理由**: 当前两条路径可能同时触发 FinishBattle，导致重复清理和 Dictionary 异常。单一出口更安全。

### D6: Client.Send 竞态修复

**选择**: 将 `count == 1` 的判断移入锁内，并在锁内直接调用 `BeginSend`。这样 Enqueue + 判断 + 启动发送是原子的。

## Risks / Trade-offs

- **[锁粒度 vs 性能]** → Controller 层加锁后，匹配、房间操作会串行化。当前并发量（预估 < 100 在线）不是问题。如果未来需要高并发，再考虑细粒度锁或无锁方案。

- **[UDP 路由依赖包内 battleID]** → 需要确认客户端发送的 UDP 包中 BattleReady/帧操作/GameOver 包是否包含可用于识别 battleID 的字段。经查看代码，`BattleReady` 包含 `Battleplayerpack[0].Battleid`（battleUserID），可通过 BattleManage 的 `DIC_BattleIDs` 反查 battleID。帧操作包含 `BattleInfo.SelfOperation.Battleid`。GameOver 用 `pack.Str` 传 battleId。路由可行，无需改客户端。

- **[FinishBattle 中 battleID vs _battleID bug]** → 第105行 `dic_pattern[battleID]` 应改为 `dic_pattern[_battleID]`。这是一个已确认的逻辑 bug，修复无风险。

- **[BattleManage.FinishBattle NPE]** → `server.GetClientByID(uid)` 可能返回 null（玩家已断线）。需加空判断。风险低，修复简单。
