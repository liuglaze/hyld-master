## 1. 服务端移动输入状态建模

- [x] 1.1 在 `Server/Server/Battle.cs` 中为每个玩家新增移动消费进度字段，并在 `BeginBattle` / `HandleBattleEnd` 生命周期内完成初始化与清理
- [x] 1.2 节点验证归属: 本任务需与 2.1、2.2 联合完成后，在 3.1 节点验证 中统一验证

## 2. 服务端移动输入接收与消费改造

- [x] 2.1 修改 `Server/Server/BattleController.Network.cs` 的移动输入入缓冲逻辑：拒绝 `SyncFrameId <= lastConsumedMoveFrame` 的迟到旧帧，并在满缓冲时淘汰最旧输入保留新输入
- [x] 2.2 修改 `Server/Server/Battle.cs` 的 `CollectAndBroadcastCurrentFrame()`：按 `lastConsumedMoveFrame + 1` 优先消费，否则取最小更大合法帧；无合法候选时沿用 `lastValidMove`
- [x] 2.3 节点验证归属: 本任务需与 1.1 联合完成后，在 3.1 节点验证 中统一验证

## 3. 日志与验证

- [x] 3.1 节点验证: 服务端移动输入按序消费 - [构造乱序/缺帧/满缓冲输入序列并运行服务端战斗链路，检查日志] - [应看到 `ACCEPT_IN_ORDER`、`SKIP_GAP_ACCEPT`、`REJECT_STALE`、`EVICT_OLDEST_ON_FULL`，且消费顺序符合 `31,32,33 + 34 => 32,33,34`]
- [x] 3.2 集成验证归属: 若节点验证通过，再在联机环境补充一次双端乱序联调验证；实现阶段视日志结果决定是否新增独立联调任务
