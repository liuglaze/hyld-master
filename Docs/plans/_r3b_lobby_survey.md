# R3-B Lobby 接线调查报告（只读）

> 范围：`D:/UGit/hyld-master`。必读顺序根 `AGENTS.md` → `Client/Assets/AGENTS.md` → `Server/AGENTS.md` → `Docs/plans/net-architecture-migration.md`（卷首 + 文末 `§R3 开工审查与分步计划`）→ `net-r3-control-contract.md`（全篇）→ `_r3a_control_report.md`（接口 §4 + 遗留 §9/§12）。
> 边界：`Server/Controller/Controllers.cs`、`Server/Server/BattleManage.cs`、`Server/Server/{Server,Client,Battle,ClientUdp,Program}.cs`、`Server/DS/*.cs` 及其直接引用协议（`SocketProto.proto`/`.cs`、`PMNet/Control/*`、`PMNet/Session/*`）与直接消费者（客户端 `Request*`/`UIMatchingPanel`/`TCPSocketManger`）。只读；未改实现、未编译、未委派。

---

## 已确认

### A. 匹配完成 → BattleManage 的完整调用（含行号）

1. `MatchingController.AddMatchingPlayer`（`Controllers.cs:425`）在 `lock(_matchLock)` 内：`Join`(503) → `room.CheckCanFight()` → `BuildMatchResult`(573) → `ReleaseRoom`(585)；**出锁后** `461` 调 `StartFighting(server, matchResult.Value)`。
2. `StartFighting`（`Controllers.cs:657`）→ `BuildMatchUsers`(598)（逐个 `server.GetActiveClient(uid)`，**离线玩家被 `continue` 丢弃**，取 hero/name/teamid/socketIP）→ `666` `BattleManage.Instance.TryBeginBattle(server, matchUsers, fightPattern, out battleId)`。
3. `BattleManage.TryBeginBattle`（`BattleManage.cs:106`）：`_uidToBattleIds` 查重（任一 uid 已在战斗即整体失败）→ `_nextBattleId++` → `new BattleContext`（`BattleContext.cs`）→ 注册 `_battleContexts` + `_uidToBattleIds` → `new BattleController(server, ctx)`；失败走 143-160 回滚。
4. `BattleController`（`Battle.cs:84`）：`LZJUDP.Instance.RegisterBattle(battleId, Handle)`（`ClientUdp.cs:42`）+ `ThreadPool` 内 **TCP 广播 `ActionCode.StartEnterBattle`**（`BattleInfo.RandSeed` + `BattlePlayerPack` 名册）。
5. 客户端 UDP `BattleReady` → `LZJUDP.RecvThread`（`ClientUdp.cs:250`）→ 端点/uid 解析处 `ClientUdp.cs:525 / 708 / 712` **经 `BattleManage.Instance.TryGetBattleIDByUID` / `TryGetBattlePlayerId`** → `Battle.cs:173 Handle` → 全员 ready → UDP `BattleStart` + `BeginBattle`(224)。
6. 结束：`BattleController.Network.cs:183 HandleBattleEnd` → `894 BattleManage.FinishBattle`（`BattleManage.cs:191`）→ 清 `_uidToBattleIds` → `client.PlayerState = PlayerOnline` → TCP `BattleReview`。
7. 断线：`Client.Close(310)` → `BattleManage.HandleClientDisconnect`(`BattleManage.cs:168`) → `BattleController.HandlePlayerDisconnect`(`Battle.cs:135`) → GameOver。

**结论**：`BattleManage._uidToBattleIds` 同时是「uid→battleId 业务占用表」与「UDP 端点路由依赖表」，是新旧路径**唯一会冲突的共享状态**。

### B. 控制通道与 DS 侧已有产物

- 控制协议/票据/引导文件编解码：`PMNet/Control/PMDsControlProtocol.cs`、`PMDsTicket.cs`；`PMDsControlWire.DefaultControlPort = 7800`（`PMDsControlProtocol.cs:70`）；`PMDsBootstrapDocument`（`PMDsTicket.cs:864`）**只做字节编解码，不碰文件系统**。
- Lobby 协调器：`Server/DS/PMDsCoordinator.cs`（`PMDsPortPool:749`、`Allocate:983`、`BeginStart:1089`、`MarkRunning:1122`、`Tick:1180`、`OnControlPayload:1314`、`ExportBootstrapDocument:1717`、`ResolveTicket:1767`、`IssueTicket:1812`、`ReleaseResources:2074`）；状态枚举 `45-86`（`Idle/Allocated/Starting/Ready/Running/ResultPending/ResultCommitted/Exited/Failed/TimedOut`）。
- 进程适配：`Server/DS/PMDsProcess.cs`（`PMDsProcessLaunchRequest:67`、`IPMDsProcess:121`（含 `WaitForExit`）、`PMDsProcessSafety.ValidateStructure:236`、`PMDsSystemProcessLauncher:498`）。
- **不存在** DS 侧 LobbyAgent、不存在引导文件写入器、Lobby 侧不存在控制 TCP listener（全库 grep 仅命中协调器自身的 `Encode`）。

### C. Ready 一致性守卫已存在（不需要新增）

`PMDsCoordinator.HandleReady`(1390) → `DescribeReadyMismatch`(1401)：要求 `body.BoundPort == _port`、`CollisionDigest` 一致、`SceneReady == true`；通过后**只发一次** `ReadyAddressPublished`（1428），重复 Ready 计 `ReadyDuplicates` 且不重复发布。

### D. 控制 listener 现状：Lobby 没有主循环

`Program.cs` 结尾是 `Console.Read()`（交互）或 `Thread.Sleep(Timeout.Infinite)`（重定向），**没有任何 tick/调度线程**。`Server.cs:32` 只起两个线程：`ListenClientConnect`(174) accept 线程 + 每连接 `Client` 的异步接收回调（`Client.cs:155 ReceiveCallBack` → `HandleRequest` 在**接收回调线程**上直接执行业务）。`Server.cs:209 CheckPing` 由 1s 定时线程驱动。

### E. 两个确认的接口缺口（阻止「端口/引导一致」）

1. **端口不注入启动参数**：`Allocate`（`983-1085`）先 `ValidateStructure(request.Process)` 再 `_ports.TryAcquire(out port)`，**从未把 port 写回 `Arguments`**，也**不校验** args 是否含 `-port`。`_r3a_control_report.md §4` 示例硬编码 `"-port","7801"` 只在空池首次分配时成立。
2. **引导文件路径与协调器无关**：`ExportBootstrapDocument`(1717) 只返回字节；文件由 R3-B 自己写，协调器既不校验 args 的 `-bootstrap <path>` 指向同一文件，也没有「写文件成功再 BeginStart」的时序钩子。

### F. 客户端最小 MainPack 承载入口（已确认链路）

`TCPSocketManger.cs:151` → `RequestManger.HandleRequest`(`RequestManger.cs:41`) → `_requestDic[pack.Actioncode]` → `BaseRequest.OnResponse`(`BaseRequest.cs:86`，**加锁入队**）→ `UIbasePanel.Update:98` → `BaseRequest.Update`(`BaseRequest.cs:47`) → `panel.OnResponse(MainPack)`（**Unity 主线程**）。
最小现有承载：复用已注册的 `UIMatchingPanel.cs:28`（`Requests.Add(new BaseRequest(this, RequestCode.Matching, ActionCode.StartEnterBattle))`）→ `UIMatchingPanel.OnResponse:35`。

### G. 结果通知与释放（已确认）

`HandleResult`(1481) → 状态 `ResultPending` + emit `ResultAccepted{ResultId,WinnerTeamId,ResultSummary}`(1545) + `ControlMessageOut(ResultAck)`；重复/冲突按 `(ResultId) + winner + summary 逐字节` 判定（墓碑）。释放只在「确认进程退出」后由 `ReleaseResources`(2074) 清 `_roster`/归还端口（M1 语义）。

---

## 高概率推断（依据与置信度）

| # | 推断 | 依据 | 置信度 |
|---|---|---|---|
| P1 | DS 局必须在 `StartFighting`(657) 处二分，**不能**在 `TryBeginBattle` 内部二分（后者已写 `_uidToBattleIds`） | `BattleManage.cs:106` 在构造 `BattleController` 前就落库 uid | 高 |
| P2 | 新路径若不改 `ClientUdp`，Lobby 会抢 DS 的 UDP 包 | `ClientUdp.cs:525/708` 无条件经 `BattleManage` 解析 | 高 |
| P3 | 控制 listener 必须「收包只入队 + 单线程 drain/Tick」 | 协调器状态只在 `Tick()` 与 `OnControlPayload()` 变化；A1 明确不订阅进程事件、不读 stdout（`_r3a_control_report.md §1`） | 高 |
| P4 | 控制通道方向为 Lobby 监听 7800、DS 反向连接 | `PMDsControlWire.DefaultControlPort` 注释「R3-B 接线用」；契约 §3「首版 TCP 仅 loopback」；`Client/Assets/AGENTS.md` 表含 `-lobby <host:port>` | 中高 |
| P5 | 票据消费账本只能以「原始票据字节的哈希」为键 | `PMDsTicketVerification`(`PMDsTicket.cs:141-178`) 只暴露 Verdict/Identity/局世代/签发到期，**无 Nonce/指纹/原始票据** | 高 |
| P6 | 客户端必须改为从票据读 DS 端口 | `Client/Assets/AGENTS.md` 明确「战斗 UDP 端口硬编码 7777，同机多局互抢」 | 高 |
| P7 | `Result` 不足以保证结算包完整 | `Result` body 仅 ResultId/winner/summary（契约 §3）；而 `BattleReview` 需要 `Dictionary<int,BattleFrame>`（`BattleManage.cs:191`） | 中 |

---

## 无法确定（缺少证据）

1. **结算回放（BattleReview）在新路径由谁发**：DS 自己经 UDP 发，还是 Lobby 经控制通道取回放再发？`Result` 契约不含帧历史，控制帧上限 64KiB（`PMDsControlProtocol.cs:40`），而回放可 >1024B。需架构决策。
2. **DS 侧 `PMDsHost`/LobbyAgent 的实际实现形态**：`Client/Assets/Scripts/Server/Boot/PMDsHost.cs` 未在本次边界内读取，其 `-lobby`/`-bootstrap` 参数解析、控制连接发起时机、`ResultId` 生成规则均无证据。
3. **`CollisionDigest` 的产生口径**：`Allocate` 要求非 0 且 Ready 必须回填一致，但摘要如何计算（场景/碰撞资产哈希？）在本边界内无定义。
4. **`ScenarioEpoch` 的分配者**：契约要求 Epoch 非 0 且与世界会话一致，Lobby 侧由谁自增、跨局是否复用，无证据。
5. **新路径下「玩家掉线」的期望语义**：legacy 是「一人掉线即 GameOver」，新路径是否保持（影响 `HandleClientDisconnect` 分流）未定。
6. **Unity 2019.4 的 `HMACSHA256` 可用性**（A1 报告 §9.1 自认最大未验证假设）仍未验证。

---

## 已检查范围

- 文档（按序全读/指定节）：根 `AGENTS.md`、`Client/Assets/AGENTS.md`、`Server/AGENTS.md`；`Docs/plans/net-architecture-migration.md`（卷首 1-140、560-610、830-845、1840-1900、1955-1999、关键 grep）；`Docs/plans/net-r3-control-contract.md`（全篇）；`Docs/plans/_r3a_control_report.md`（全篇）。文档如何决定入口：`net-r3-control-contract.md §2/§3` 决定端口池共享、票据账本、状态链与超时默认值；`net-architecture-migration.md` 文末 `R3-B 前置` 决定「票据指纹→端点账本、共享 uid 占用、引导文件原子发布」三项必做。
- 源码（只读）：`Server/Controller/Controllers.cs`(25-125, 395-700, 全部 `BattleManage.Instance` 引用)、`Server/Server/BattleManage.cs`(全)、`Server/Server/Server.cs`(全)、`Server/Server/Client.cs`(100-250)、`Server/Server/Battle.cs`(80-210)、`Server/Server/ClientUdp.cs`(500-540, 695-730)、`Server/Server/ServerConfig.cs`、`Server/Program.cs`、`Server/DS/PMDsCoordinator.cs`(45-90, 223-300, 749-905, 983-1135, 1388-1467, 1480-1610, 1700-1840, 2074-2115)、`Server/DS/PMDsProcess.cs`(60-350)、`PMNet/Control/PMDsControlProtocol.cs`(31-76, 80-110, 155-185)、`PMNet/Control/PMDsTicket.cs`(141-200, 864-900)、`ProtobufAndNotepad/Protobuf/SocketProto.proto`(19-63, 134-155)。
- 直接消费者（为回答问题 6，最小只读）：`Client/Assets/Scripts/Server/Manger/RequestManger.cs`(1-80)、`.../Request/BaseRequest.cs`(1-118)、`.../Manger/TCPSocketManger.cs`(120-175)、`.../Panel/UIMatchingPanel.cs`(23-45)、`.../Manger/Battle/BattleManger.cs`(355-440)。未扩散到其它客户端模块。
- 未做：未编译、未运行、未启动任何 DS/Lobby、未改任何实现文件（唯一写入为本报告）。

---

## 建议下一步

### S1 可冻结接口草案（供 DS/client 并行消费，均未实现）

**(1) Lobby 侧新增文件**

| 新文件 | 职责 |
|---|---|
| `Server/DS/PMDsPlayerLedger.cs` | 跨会话 uid/PlayerId 占用账本：`bool TryReserve(PMDsRosterIdentity[] roster, string matchId, out string reason)`、`void Release(string matchId)`、`bool IsOccupied(int uid)`、`int Count` |
| `Server/DS/PMDsControlListener.cs` | 仅绑 `127.0.0.1:7800` 的 TCP listener：4 字节 LE 分帧（`PMDsControlFraming`）+ 半包/粘包/超限（>64KiB 立即断开）；**只入队** `(endpointKey,payload)` |
| `Server/DS/PMDsLobbyHost.cs` | 唯一调度所有者（单线程循环）：drain 入站队列 → 按 `(MatchId,Epoch)` 查会话 → `Coordinator.OnControlPayload(payload,0,len)` → 对所有活跃会话 `Tick()` → 发 pending `ControlMessageOut`；持有全局唯一 `PMDsPortPool` + `PMDsPlayerLedger`；实现 `IPMDsCoordinatorSink` |
| `Server/Controller/PMBattleLaunchRouter.cs` | 唯一新旧分流点：`TryStartMatch(Server, List<MatchUserInfo>, MatchingController.FightPattern, out int battleId, out PMBattleAuthorityKind kind)`；`kind = Legacy | DedicatedServer` |

**(2) 对 A1 已有文件的加法式改动（`Server/DS/`）**

- `PMDsCoordinator` 构造增加 `PMDsPlayerLedger sharedPlayerLedger`（与 `sharedPortPool` 同构，`887`）；`Allocate` 在 `ValidateIdentities` 之后、`TryAcquire` **之前**调 `TryReserve`（失败返回新增 `PMDsCoordinatorOutcome.RejectedPlayerOccupied`，状态与端口不动）；`ReleaseResources`(2074) 末尾 `Release(matchId)`——**与端口同点释放**，保持 M1「确认进程退出才释放」。
- `PMDsAllocationRequest` 增加 `string BootstrapFilePath`（绝对路径，必填）。
- `PMDsControlWire` 增加 `public const string PortPlaceholder = "{port}";`；`Allocate` 取到端口后对 `Arguments` 做占位替换，并 fail-closed 断言：恰好一处 `-port <port>`、恰好一处 `-bootstrap <BootstrapFilePath>`，否则归还端口 + `RejectedMalformed`。
- 新增 `PMDsCoordinatorEffect.BootstrapDocumentOut`（`ControlPayload = ExportBootstrapDocument()`，`Reason = BootstrapFilePath`），在 `Allocate` 成功之后、`BeginStart` 之前 emit；Lobby sink 用「临时文件 + 原子改名」发布，**成功后才 `BeginStart()`**（把「文件已发布」与「进程已启动」做成因果，而不是靠调用顺序纪律）。
- 会话路由键固定为 `MatchId + ":" + Epoch`（两字段已在控制帧头，见契约 §3）。

**(3) 协议（权威源 `ProtobufAndNotepad/Protobuf/SocketProto.proto`，追加字段）**

```proto
message BattleEntryTicket {          // MainPack 追加：BattleEntryTicket battle_entry=17;
    string match_id        = 1;
    string ds_id           = 2;
    uint32 epoch           = 3;
    uint32 protocol_hash   = 4;
    uint32 collision_digest= 5;
    string ds_host         = 6;      // 首版 127.0.0.1
    uint32 ds_port         = 7;      // = PMDsCoordinator.AllocatedPort
    bytes  ticket          = 8;      // PlayerTicket ≤512B（PMDsControlWire.MaxTicketBytes）
}
```

- 承载入口：复用 `ActionCode.StartEnterBattle`（`proto:51`）+ 已有注册点 `UIMatchingPanel.cs:28`；**字段不设即为旧路径**，旧客户端行为零变化。
- 过渡期最小替代（不动 proto）：`MainPack.str`(field 5) 放 base64 票据 JSON/自定义串——`str` 已被用作自由串（`BattleManage.cs:236` 赛制、GameOver winnerTeamId、匹配 "3/6"）。

**(4) DS 侧（Unity）新增文件**

| 新文件 | 职责 |
|---|---|
| `Client/Assets/Scripts/Server/Boot/PMDsLobbyAgent.cs` | 读 `-bootstrap` 文件（`PMDsBootstrapDocument.TryDecode`）→ 连 `-lobby` → 主线程发 Ready(`BoundPort`=真实 bound)/Heartbeat/Result/Exited；控制帧只从 Unity 主线程（`PMDsHost.Update`）收发 |
| `Client/Assets/Scripts/Server/Boot/PMDsEndpointTicketLedger.cs` | 端点消费账本：键 `SHA256(原始票据字节)`，值 `(uid, playerId, epoch, endpointKey)`；同票二次来自不同 endpoint → 拒；必须在调用 `PMTransportConnection.Activate` **之前**完成校验与端点绑定 |

### S2 顺序与硬约束（不双权威）

- 目标链路：`StartFighting`(657) → Router → `PMDsLobbyHost.Allocate(...)` → 写引导文件（原子）→ `BeginStart` → DS 连接并 `Ready` → sink `ReadyAddressPublished` → Lobby 向参战客户端 TCP 发 `StartEnterTicket`（唯一一次）→ `MarkRunning` → （战斗全在 DS）→ DS `Result` → `ResultAccepted` → Lobby 结算 + `ResultAck` → DS 退出 → `ResourcesReleased` → 释放玩家/端口。
- 四条禁令：① DS 局**不得** `new BattleController`、**不得** `LZJUDP.RegisterBattle`；② `ClientUdp.TryResolveBattleID`(166) 对 DS 局必须返回 false；③ `ClearSenceController.ClientSendClearSenceReady`(`Controllers.cs:67`) 对无 BattleContext 的 DS 局需显式语义；④ 每 battleId 只有一个权威，`_uidToBattleIds` 增加 `kind` 后由 Router 独占写入。
- 开关：`PMDsMatchRoutingOptions { Mode = Legacy|DedicatedServer|Percent, Percent }`，默认 `Legacy`，T42 通过后切换；不存在「同一局两套同时驱动」的中间态。

### S3 最小补充查询（如需继续深挖）

1. 读 `Client/Assets/Scripts/Server/Boot/PMDsHost.cs`：确认 `-lobby`/`-bootstrap` 解析现状与控制连接发起时机（回答「无法确定 2」）。
2. 读 `Client/Assets/Scripts/PMNet/Session/PMNetSessionBridge.cs` + `PMTransportConnection.cs` 的 `Activate` 前置检查点，确定端点账本的插入位置。
3. 找 `CollisionDigest`/`ScenarioEpoch` 的既定来源（grep `CollisionDigest` 在生产代码中的赋值点）。

### S4 运行时验证（R3-B 完成后的最小证据）

- 单机两局并发：断言两个会话 `AllocatedPort` 不同、`PMDsPlayerLedger` 拒绝同一 uid 二次入局。
- 引导文件原子性：`BeginStart` 之前文件必然已完整（可读到合法 `PMDsBootstrapDocument`）；进程启动参数里 `-port`/`-bootstrap` 与协调器内部值一致（可加断言日志）。
- 一致性负向：DS 回报 `BoundPort != 分配端口`、`CollisionDigest` 不合、`sceneReady=false` 三种情况下，客户端**收不到**任何地址票据。
- 收尾：强杀 DS 后端口与 uid 占用**保持占用**直到确认进程退出；`SessionEnded` 与 `ResourcesReleased` 允许跨 Tick（`_r3a_control_report.md §12.4`）。
