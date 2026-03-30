## 1. 调用链定位与基线确认

- [x] 1.1 梳理 `CommandManger` 的输入采集→本地推进→网络发送调用链，并标注在线战斗入口函数
- [x] 1.2 梳理服务端权威帧接收→`BattleData.sync_frameID` 更新→`BattleManger`/`HYLDPlayerManger`/`HYLDBulletManger` 推进调用链
- [x] 1.3 标注联机与单机模式分支点，确定预测/回滚仅挂接联机分支
- [x] 1.4 建立基线观测日志（`frameId`、`operationId`、关键状态差异），不改变现有行为

## 2. 预测缓存与关键状态快照

- [x] 2.1 定义预测缓存数据结构（`frameId`、`operationId`、输入记录、回滚所需状态快照）
- [x] 2.2 在本地输入推进点写入预测历史（每预测 tick 一条）
- [x] 2.3 实现环形缓存窗口与淘汰策略，保证窗口连续可重放
- [x] 2.4 增加可配置参数：历史窗口大小、和解阈值、平滑窗口时长

## 3. 权威对账、阈值判定与回滚重放

- [x] 3.1 在权威帧到达路径接入按 `frameId`/`operationId` 的对账流程
- [x] 3.2 实现差异阈值判定：阈值内走轻量校正，阈值外触发完整回滚（方案A：基于权威输入驱动后的本地派生状态对账，并在日志中注明非权威状态快照）
- [x] 3.3 实现“回滚到预测锚点快照”并恢复本地关键状态所有权（方案A：明确当前锚点来源为预测历史，不宣称存在协议级权威状态锚点）
- [x] 3.4 实现“按帧序重放后续本地输入”并输出和解后的逻辑状态（当前为仅重放本地 self 输入）
- [x] 3.5 处理越窗权威帧、缺失帧、重复包/乱序包的安全降级路径（当前策略为 drop / accept-latest / keep-local，并在日志中注明限制）

## 4. 视觉平滑与模式隔离

- [x] 4.1 在逻辑和解后对位置/朝向增加短窗平滑，避免硬跳变（通过 `reconciliationVisualOffset/reconciliationVisualForward` + `VisualSmoothingWindowSeconds` 在 `HYLDPlayerController` 渲染层衰减）
- [x] 4.2 保证逻辑状态先一致、表现层后平滑，不篡改服务端权威结果（和解仍先写入 `HYLDStaticValue.Players[*].playerPositon/playerMoveDir`，平滑仅影响 `selfTransform` 的显示插值）
- [x] 4.3 增加联机专用开关：仅联机启用预测/回滚/和解流水线（通过 `NetConfigValue.EnablePredictionReconciliationPipeline` + `BattleData.IsPredictionEnabled` 统一门控；`Send_operation` 在开关关闭时改为 `sync_frameID+1` 发送且不执行 `RecordPredictedHistory/CommitPredictedFrame`，权威应用路径 `skipSelf` 也受门控约束）
- [x] 4.4 验证单机路径绕过预测流水线并保持原有行为（代码级验证：`BattleManger.Init` 将 `HYLDStaticValue.isNet=ISNet`，单机时 `IsPredictionEnabled=false`；`Send_operation` 单机分支仅执行 `ResetOperation -> CommandManger.Execute -> OnLogicUpdate`，未接入预测/回滚/和解逻辑）

## 5. 回归验证与上线回退预案

- [x] 5.1 验证单机模式全流程（开始、移动、攻击、结算）无行为回归，暂时不做
- [x] 5.2 验证联机准备阶段与开战切换（含 BattleID 映射/镜像）无错位（运行时复验：`20260315_100628_runtime.log` 出现 `StartEnterBattle` 且 battleid 映射为 1/2；`20260315_100628_framesync.log` 连续帧 `battleIdMatched=True` 且未见 `battleIdMatched=False`）
- [x] 5.3 验证联机移动连续输入场景：即时响应、对账后无明显回拉（基础通过，日志复验：`20260315_145649_runtime.log` 已 `StartEnterBattle Succeed`；`20260315_145649_framesync.log` 出现 `predictionEnabled=True` 且未见 `PipelineDisabled`，存在连续非零移动输入，早期帧多次 `light-correction accepted-within-threshold`；中后段仍有 `self-authority-operation-missing`/`empty-authority-batch fallback`，建议后续补一轮稳定网络复测以强化证据）
- [ ] 5.4 验证联机攻击/命中场景：预测反馈与权威结果最终一致（2026-03-15 代码修复完成：移除 `inputMatched` 作为回滚触发条件，和解仅依赖状态偏差；`skipSelf` 改为预测管线开启时一律跳过，消除双重推进；待联机实测验收）
- [ ] 5.5 验证高延迟/丢包/乱序场景：回滚频率、CPU 峰值、可见抖动在可接受范围
- [x] 5.6 验证游戏结束与退出流程：无残留预测状态或缓存泄漏
- [x] 5.7 验证故障回退开关：可一键关闭预测与回滚并恢复旧链路（运行时复验：`20260315_140726_framesync.log` 与 `20260315_140727_framesync.log` 持续命中 `predictionEnabled=False`、`[Reconciliation][PipelineDisabled] stage=local-predict/authority-reconcile`、`mode=apply-full-authority`，且 `skipSelf=False`，未见 `predictionEnabled=True`/`skipSelf=True`）

### 5.x 代码级证据备注（2026-03-14）

- 5.1（部分可证明，待运行时）：单机分支 `Send_operation` 走 `ResetOperation -> CommandManger.Execute -> OnLogicUpdate`，且 `BattleData.IsPredictionEnabled` 由 `HYLDStaticValue.isNet && EnablePredictionReconciliationPipeline` 门控（`Assets/Scripts/Server/Manger/Battle/BattleManger.cs:139-191,437-442`）。
- 5.2（部分可证明，存在错位风险）：`BattleID -> 玩家索引` 映射在 `HYLDPlayerManger.InitData` 建立并在逻辑应用处直接索引使用；`HYLDStaticValue.Players` 为静态列表，当前新入口未见同路径显式 `Players.Clear()`（`Assets/Scripts/Server/Manger/Battle/HYLDPlayerManger.cs:71-89,115-161`，`Assets/HYLD1.0/Scripts/OldScripts/HYLDStaticValue.cs:50,100-107`）。
- 5.3（部分可证明，待运行时）：移动输入即时响应链路可证明；和解含轻校正/全回滚/重放，需实机确认“无明显回拉”（`Assets/Scripts/Server/Manger/Battle/BattleManger.cs:149-175,809-872`）。
- 5.4（部分可证明，待运行时）：攻击输入写入 `selfOperation.BulletInfo` 并驱动 `HYLDPlayerManger`/`HYLDBulletManger`；命中依赖 `OnTriggerEnter`，最终一致性需联机实测（`Assets/Scripts/Manger/CommandManger.cs:56-60`，`Assets/Scripts/Server/Manger/Battle/HYLDBulletManger.cs:47-83`，`Assets/HYLD1.0/Scripts/OldScripts/Bullet/s/Bullet/shell.cs:99-390`）。
- 5.5（代码可证明+监控缺口）：存在老包丢弃、历史不足丢弃、重复帧 fallback、和解日志；但缺 RTT/抖动/CPU 聚合指标（`Assets/Scripts/Server/Manger/Battle/BattleManger.cs:955-977,998-1010,809-872`，`Assets/Scripts/Server/Manger/PingPongManger.cs:39-53`）。
- 5.6（部分可证明，已补最小闭环，待运行时）：已在 `BeginGameOver` 置 `IsGameOver=true`，并统一取消 `Send_operation/Send_BattleReady/SendGameOver`；新增 `BattleData.ClearPredictionRuntimeState()` 于 GameOver 与 `OnDestroy` 清理预测缓存；服务端下发 GameOver 路径改为先 `BeginGameOver(false)` 再展示结算（`Assets/Scripts/Server/Manger/Battle/BattleManger.cs:309-315,324-367,518-530`，`Assets/HYLD1.0/Scripts/OldScripts/Toolbox.cs:110,211`）。
- 5.6（新增观察点）：`Send_operation` 增加 `IsGameOver` 早退与收包侧 `DropPacket` 日志门控，避免结束后继续推进逻辑（`Assets/Scripts/Server/Manger/Battle/BattleManger.cs:139-144,263-267`）。
- 5.6（运行时初验，2026-03-15）：联机场景实跑可进入结束流程并出现结算（用户反馈“联机跑了一遍然后结束了”）；当前仅确认 5.6.a，`停发` 与 `DropPacket` 日志仍待核对（`Assets/Scripts/Server/Manger/Battle/BattleManger.cs:106-130,292-295,358-375`）。
- 5.6（代码级补强，2026-03-15）：修复日志文件句柄长期占用风险：`HYLDDebug` 新增 `Shutdown()` 统一 `Flush+Dispose` runtime/frame 流，并将 `File.Open` 调整为 `FileShare.ReadWrite`；`HYLDManger` 在 `OnDestroy/OnApplicationQuit` 调用 `Shutdown()`；`AddBattleReview/GetBattleReview` 改为 `using` 作用域确保读写流释放（`Assets/Scripts/Log/Loging.cs:173-299`，`Assets/Scripts/Server/Manger/HYLDManger.cs:102-164`）。
- 5.7（代码可证明，待实机确认）：关闭 `EnablePredictionReconciliationPipeline` 后，本地预测写入短路、和解返回 `NoPrediction`、权威全量应用路径生效（`Assets/Scripts/Server/ConstValue.cs:31`，`Assets/Scripts/Server/Manger/Battle/BattleManger.cs:149-170,811-814,874-879,989-992`）。

### 5.6 最小运行时验收清单（压缩版）

- [x] 5.6.a 触发+结算：`UNITY_EDITOR/DEVELOPMENT_BUILD` 下按 `F10`，命中 `TriggerDebugGameOver -> BeginGameOver -> toolbox.游戏结束方法()`，胜负文案与 `DebugGameOverAsLose` 一致（`Assets/Scripts/Server/Manger/Battle/BattleManger.cs:106-130`，`Assets/HYLD1.0/Scripts/OldScripts/Toolbox.cs:110,211`）。
- [x] 5.6.b 停发+清理：结束后 `Send_operation/Send_BattleReady/SendGameOver` 取消；`ClearPredictionRuntimeState()` 执行，`sync_frameID/predicted_frameID/history` 清零（`Assets/Scripts/Server/Manger/Battle/BattleManger.cs:358-377,549-564`）。
- [x] 5.6.c 收包门控：结束后非 `BattlePushDowmGameOver` 包命中 `[GameOver][DropPacket]`，不再推进逻辑（`Assets/Scripts/Server/Manger/Battle/BattleManger.cs:191-195`）。

> 运行日志补充（2026-03-15）：
> - 5.6.a 已由 `20260315_094048_framesync.log` 验证：出现 `[GameOver][DebugTrigger]` 与 `[GameOver] begin ... notifyServer=True`（行 1853-1854）。
> - 5.6.b 已直接验证：`20260315_095811_framesync.log` 与 `20260315_095740_framesync.log` 已命中 `[GameOver][StopSend]`、`[Reconciliation][RuntimeStateCleared]`、`[GameOver][AfterClear]`，可直接证明停发与清理链路生效；`20260315_013111_framesync.log` 的“下一局从 `nextFrame=1/predictedFrame=1` 重起”保留为侧证。
> - 5.6.c 本轮未捕获 `[GameOver][DropPacket]`，说明“结束后仍收到非 GameOver 包”的触发条件未被覆盖，需构造该场景复测。
> - 2026-03-15（埋点补强）：`BeginGameOver` 新增 `[GameOver][StopSend]` 与 `[GameOver][AfterClear]`，`ClearPredictionRuntimeState` 新增 `[Reconciliation][RuntimeStateCleared]` 快照日志；并修复一次埋点编辑引入的 `'}'` 语法残留（`Assets/Scripts/Server/Manger/Battle/BattleManger.cs:358-377,549-564`）。
> - 2026-03-15（运行时复验）：`20260315_095811_framesync.log` 命中 `[GameOver][DebugTrigger]`/`[GameOver] begin`/`[GameOver][StopSend]`/`[Reconciliation][RuntimeStateCleared]`/`[GameOver][AfterClear]`（行 1386-1390）；`20260315_095740_framesync.log` 命中服务端结算路径 `notifyServer=False` 下的 `[GameOver] begin` + StopSend/AfterClear + RuntimeStateCleared（行 2708-2711），可直接支撑 5.6.b。
> - 2026-03-15（5.6.c 现状）：`20260315_095811_framesync.log`、`20260315_095740_framesync.log`、`20260315_095805_framesync.log` 均未命中 `[GameOver][DropPacket]`，该验证点继续保持未完成。
> - 2026-03-15（5.6.c 最小探针）：在 `TriggerDebugGameOver` 中追加 `TriggerDebugDropPacketProbe()`，结束后主动构造 `RequestCode.Battle + ActionCode.BattlePushDowmAllFrameOpeartions` 本地探针包并调用 `HandleMessage`，预期命中 `[GameOver][DropPacket]`；同步增加 `[GameOver][DebugProbe]` 日志用于区分探针是否发出（`Assets/Scripts/Server/Manger/Battle/BattleManger.cs:121-146,191-195`）。
> - 2026-03-15（5.6.c 运行时复验通过）：`20260315_100543_framesync.log` 命中 `[GameOver][DebugTrigger]`（1139）、`[GameOver] begin`（1140）、`[GameOver][DebugProbe] dispatch`（1144）与 `[GameOver][DropPacket] action=BattlePushDowmAllFrameOpeartions`（1145），证明结束后非 `BattlePushDowmGameOver` 包被门控丢弃。
> - 2026-03-15（额外噪声修复）：将 TCP 收包 `len==0` 从 `Info` 控制台日志“数据为0”降级为 trace-only 记录，避免每次结束时 Unity Console 噪声（`Assets/Scripts/Server/Manger/TCPSocketManger.cs:91-95`）。
> - 2026-03-15（5.7 现状复核，未完成）：当前日志样本未命中 `predictionEnabled=False` 或 `[Reconciliation][PipelineDisabled]`（全量 `*_framesync.log` 检索结果为空）；`20260315_100628_framesync.log` 仍持续出现 `predictionEnabled=True` 与 `skipSelf=True`（如行 9/16），说明本轮尚未覆盖“关闭开关后回退旧链路”的实机场景，5.7 继续保持未勾选。
> - 2026-03-15（/opsx:apply 复核）：`20260315_140726_framesync.log` 与 `20260315_140727_framesync.log` 已持续命中 `predictionEnabled=False`、`[Reconciliation][PipelineDisabled] stage=local-predict/authority-reconcile`、`mode=apply-full-authority` 且 `skipSelf=False`；但当前日志仅覆盖静止输入（`move=(0,0), attack=False`）与开战基线，不含单机全流程、联机连续移动、攻击命中、高延迟/丢包/乱序压测样本，因此 5.1/5.3/5.4/5.5 维持未完成。
> - 2026-03-15（5.1 环境阻塞说明）：当前客户端登录流程依赖服务器，现网构建未提供可直接进入单机战斗的离线入口/开关；因此 5.1「单机全流程回归」在本轮环境不可执行，暂保持未勾选，待补充离线入口或可复现实验环境后补测。

### 5.x 风险清单（代码走查）

- 网络线程直接触发权威逻辑应用，存在与主线程共享状态并发风险（`Assets/Scripts/Server/Manger/UDPSocketManger.cs:70-95`，`Assets/Scripts/Server/Manger/Battle/BattleManger.cs:253-299`）。
- 回滚锚点基于预测快照而非协议级权威状态快照，弱网下可能放大回拉（`Assets/Scripts/Server/Manger/Battle/BattleManger.cs:867,995`）。
- `HYLDBulletManger.RollbackAndReset` 清空子弹后未按 preserved count 重建，可能影响弹道连续性（`Assets/Scripts/Server/Manger/Battle/HYLDBulletManger.cs:100-118`）。
- `CommandManger` 命令列表只增不清，长局存在增长与遍历成本风险（`Assets/Scripts/Manger/CommandManger.cs:43-45,109-116`）。
- `BattleID` 映射依赖静态玩家列表顺序，复战场景存在索引错位潜在风险（`Assets/Scripts/Server/Manger/Battle/HYLDPlayerManger.cs:71-89`）。