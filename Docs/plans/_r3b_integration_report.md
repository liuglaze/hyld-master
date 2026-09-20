# R3-B5 主集成实施与验收报告（Lobby→DS 控制 + 真实 UDP 两客户端 + 结果/退出语义修复）

> 范围：主计划 **R3B5** 行 + `net-r3-control-contract.md` §7（冻结接口）+ 本轮**用户冻结裁决**（`ResultPending` 退出语义）。
> 写入边界：新增 `Tools/PMR3IntegrationTest/`、改 `Server/DS/PMDsCoordinator.cs` 的成功退出语义、补 `Tools/PMDsControlTest/Program.cs` oracle、本报告与 `_r3b_lobby_report.md` 的误判修复注记。**未动**网络/Unity 宿主与主计划。
> 本文只记录本轮实施与实测证据；阶段状态以 `net-architecture-migration.md` 为准，**T42（真实 UnityDS）仍 PENDING**。

## 1. 结论摘要

- **冻结裁决已落地**：`ResultPending` + **经 MAC/对局/世代认证的显式 `Exited(0)`** ⇒ 终态 `Exited`（正常完成）；**单纯进程提前退出（无协议证据）** ⇒ 仍 `Failed`。`ResultCommitted` 语义未改；两条路径的资源释放**都**要求本地确认进程已退出，**不从控制消息推断进程死亡**。
- 新门禁 `Tools/PMR3IntegrationTest`：build **0 警告 0 错误** → run **exit 0，143 项检查 / 0 失败 / 3 个场景**（`LobbyHost`→真实引导文件→真实 `PMDsLobbyAgent`→真实 UDP 端点→真实 `PMR3Runtime`/生成桩→smoke 结果→`ResultAck`→显式 `Exited(0)`→回收）。
- `Tools/PMDsControlTest`：**506 项 / 0 失败**（新增 L 节 33 项；原 473/0；负向输入由 50 增至 54）。
- 回归：`Tools/PMDsLobbyTest` **230/0**（它也编协调器）；`Server/Server.csproj` 临时输出 build **0 错误**（仅既有 8 个警告）。
- **两个方向的缺陷注入都被抓到**，注入后按 md5 逐字节还原（`Server/DS/PMDsCoordinator.cs` = `5e1220d7335deb21c70d26fb90818ace`）。
- **唯一边界外替身＝受控进程替身**（`IPMDsProcessLauncher`/`IPMDsProcess`）。不拉真实 HyldDS.exe（需 Unity 构建）、不开旧服务、不用 7777/7778/7800。

## 2. 必读范围（实际读取，按序）

| 顺序 | 文档 | 对本次实施的直接约束 |
|---|---|---|
| 1 | `AGENTS.md`（根） | 三处入口文档指向 |
| 2 | `Client/Assets/AGENTS.md` | DS 启动参数契约（`-server/-port/-bootstrap/-control`）；「战斗 UDP 端口硬编码 7777，同机多局互抢」 |
| 3 | `Server/AGENTS.md` | `net8.0`、`Server/**` 隐式 compile、`PMNet/**` 链接共享、保活差异 |
| 4 | `net-architecture-migration.md`（卷首当前状态 + R3-B 分步/R3B5 行） | R3B5 验收口径「一局单权威、Probe/Echo/初值/结果幂等/退出」；A 阶段不得当作 R3 完成 |
| 5 | `net-r3-control-contract.md`（§2/§3/**§7.1–§7.4**） | offer 走 `MainPack.Str`；UDP 宿主签名与「未激活不进 Transport」；宿主单线程所有者/端口池/uid 账本/引导文件原子发布；PMR3 声明与冻结 API |
| 6 | `_r3b_lobby_report.md`（§4/§5/§7/§9） | Lobby 侧真实 API 面；**§7 第 1 条**就是本轮要修的成功局误判 |
| 7 | `_r3b_unity_report.md`（§2–§5） | PMDsLobbyAgent/PMR3Runtime/SpawnPlayer/SceneReady 口径；其 §5 缺口正是本门禁要闭合的 |
| 8 | `_r3b_network_report.md`（§1–§3/§8–§9） | 端点账本/握手/「未认证不进 Transport」；其 §6 的旧断言失效与 §9 交接要点 |

文档如何决定首搜与入口：§7.1 决定 offer 必须走**真实** `PMDsEntryCodec`（本门禁因此在网关里只做字段透传、编码/解码都用生产实现）；§7.2 决定数据面必须用**真实** `PMUdpSessionEndpoint` 的 `OpenServer/OpenClient`；§7.4 决定 DS/客户端两侧都必须经 `PMR3Runtime.Attach/SpawnPlayer` 与**生成桩**收发，不得手写业务状态包。

## 3. 交付物

| 文件 | 行数 | 变更 |
|---|---|---|
| `Tools/PMR3IntegrationTest/PMR3IntegrationTest.csproj` | 60 | 新增：net8.0 门禁工程，直编真实 PMNet/PMR3 生成物/`PMDsLobbyAgent`/`Server/DS/**` |
| `Tools/PMR3IntegrationTest/Program.cs` | 1539 | 新增：3 场景 / 143 项检查（真实 TCP + 真实 UDP，虚拟时钟，端口随机，`finally` 全量释放） |
| `Server/DS/PMDsCoordinator.cs` | 2517 | 修改：`ApplyProcessExit` 增 `authenticatedPeerExited`，`ResultPending` 分支按裁决分成正常完成/失败两条；枚举文档补语义 |
| `Tools/PMDsControlTest/Program.cs` | 2995 | 修改：新增 L 节（33 项）钉住两条退出路径的区别 |
| `Docs/plans/_r3b_integration_report.md` | 本文件 | 新增 |
| `Docs/plans/_r3b_lobby_report.md` | — | 仅 §7 第 1 条追加「已修复」注记 |

`.cs` 为 UTF-8(BOM)+CRLF；`.csproj` 与 `.md` 为 UTF-8(BOM)+LF。`Tools/` 下无 `.meta` 约定（实测该目录无任何 `.meta`），故未新增。

## 4. 协调器修复（冻结裁决的落点）

`ApplyProcessExit(int exitCode, string why, bool authenticatedPeerExited = false)`：

- **唯一**传 `true` 的入口是 `HandlePeerExited`（该帧先过 MAC，再过 `Dispatch` 的 `MatchId/DsId/Epoch/ProtocolHash` 全等校验）；`Tick` 轮询与 `OnProcessExited` 仍传 `false`（无协议证据）。
- `case ResultPending`：`authenticatedPeerExited && exitCode == 0 && _hasResult` ⇒ `Finalize(Exited, "结果已本地受理，且对端经认证显式 Exited(0)（…）")`；否则维持原 `Finalize(Failed, "结果确认前进程退出（…）")`。
- `case ResultCommitted` 未改（仍是「本地受理」+ 进程退出 ⇒ `Exited`）。
- **释放前提未动**：`Finalize` 仍走 `RequestKill` → `TryFinishTerminalCleanup`（`WaitForProcessExit`）→ 只有确认/无进程才 `ReleaseResources`。因此「显式 Exited 先到、进程稍后才退」时端口与 uid 仍保持占用，直到 Tick 确认退出。

**为什么这不是「由消息推断进程死」**：判决所依据的是对端**自己声明**「我已完成并正在退出」这一可认证事实，而**不是**用发送/重发次数或心跳去推断对端状态；资源归属仍然只看本地进程观测。

## 5. 控制测试 oracle（两条路径的区别）

`PMDsControlTest` 新增 L 节 33 项（L1–L33），全部用真实 `PMDsControlSigner` 签名 + 真实协调器：

| 用例 | 注入 | 期望 |
|---|---|---|
| L1 | `ResultPending` + 认证显式 `Exited(0)`，**进程仍在跑** | 终态 `Exited`；`PeerExitObserved`；原因含「对端经认证显式 Exited」；**端口/名册仍占用**；进程真退出后才释放且释放恰一次 |
| L2 | `ResultPending` + 纯进程退出（`Tick` 轮询，exit=0，无任何 Exited） | 仍 `Failed`，原因含「结果确认前进程退出」 |
| L3 | `ResultPending` + 认证显式 `Exited(7)` | 仍 `Failed`（只有 `exit=0` 才算正常完成） |
| L4 | `ResultPending` + **别局密钥**签的 `Exited(0)` | `RejectedMac`、状态不变、不构成对端收尾观测；随后进程真退出仍按 `Failed` 收尾 |

## 6. 集成门禁 `Tools/PMR3IntegrationTest`

### 6.1 它编的都是真实实现（无 Unity、无 Server 主类型）

`Client/Assets/Scripts/PMNet/**`（Control/Session/Transport/World/Replication/Generated）、`Client/Assets/Scripts/PMR3/{PMR3Player,PMR3Runtime}.cs + Generated/**`、`Client/Assets/Scripts/Server/Boot/PMDsLobbyAgent.cs`（纯 C#）、`Server/DS/**`（协调器/进程抽象/uid 账本/控制 listener/Lobby 宿主）。

**唯一替身是进程**。`PMDsSessionHost`/`PMClientSessionHost` 含 `UnityEngine`，**不在此编译**；测试宿主按它们的**非 Unity 核心次序**装配（读引导→校验局/摘要/端口→Register→世界/桥/Attach→OpenServer→Connected→SpawnPlayer→owner 只发一次探针），测试宿主与真实 Unity 宿主在报告与代码注释里都明确区分。

### 6.2 三个场景

| 场景 | 覆盖 | 关键断言 |
|---|---|---|
| 1 成功闭环 | 真实引导文件、真实 `-control/-port/-bootstrap/-matchid/-dsid` 参数对账、真实 Ready、真实 `PMDS1:` offer、真 UDP 握手/验票/激活、两端各见 2 对象、owner 各 1 次探针、Echo nonce 回流、`ProbeCount` 双向收敛、**非 owner 拒绝**、**多世界 static route**、smoke 结果、**重发结果不重复结算**、`ResultAck`→显式 `Exited(0)`→模拟退出→端口/uid 回收 | G3–G109：`offer.Port == 真实分配端口`；`Endpoint.BoundPort == 分配端口`；`HandshakeRejections==0`；`Ledger.ActiveBindingCount==2`；`ResultAccepted==1` 且 `ResultDuplicates>=1`；宿主只通知一次结果且摘要含 `smoke`；未退出前 `PortsInUse==1`/`ConnectionCount==2`/引导文件仍在；确认退出后 `PortPool.InUseCount==0`、`PlayerLedger.Count==0`、引导文件已删、两名玩家恢复在线 |
| 2 结果前纯崩溃 | 第二局复用同一批 uid（证明上一局真释放）；Ready 后**无任何结果**时进程消失 | G110–G125：终态 `Failed`、释放事件携带 `Failed`、端口/uid 归零、无任何结果通知 |
| 3 确认窗口内的纯崩溃 | 结果**已被受理**（`ResultPending`），但在 DS 读到 `ResultAck` 之前进程消失 ⇒ **没有任何 `Exited`** | G126–G142：崩溃点由 `!ResultAcknowledged` 证明落在窗口内；终态仍 `Failed`、原因含「结果确认前进程退出」；受理计数保持 1（不重复结算）；随后正常回收 |

场景 3 的实现要点：`PumpOnce` 次序是「客户端 → DS → 宿主」，宿主在受理那一轮写出的 `ResultAck` 要到**下一轮**才会被 DS 代理读到；因此检测到 `ResultAccepted==1` 后立即置 `PumpDs=false` 即可把「进程没了」与「代理读到 Ack」拆开——否则真实代理会照契约发出 `Exited(0)`，崩溃点就跑到窗口外了。

### 6.3 真实主体证据（防「注入消息绕过 codec/网络主体」）

- 控制面：DS 侧 `FramesSent>0`/`FramesReceived>0`、宿主 `InboundFramesDispatched>0`、全链路 `MacFailures==0`；启动参数经协调器 `SetLaunchRequest` fail-closed 校验。
- 数据面：服务端 `DatagramsReceived>0`/`DatagramsSent>0`（单次运行 6/6）；`Bridge.Replication.Stats.UpdateRecordsSent>0`（8）、客户端 `UpdateRecordsApplied>0`（4）、两条客户端桥 `RpcSent>0`（各 1）。
- 非 owner：`connA.RpcRejected` 增长 + `PMNetRpcReceive.Observer` 给出 `PMRpcReceiveStatus.NotOwner`，且目标 `ProbeCount` 不变、链路对他仍可用（对照）。
- 多世界：`PMR3Runtime.GetBridge(world)` 对 DS/两客户端三个世界各自命中自己的桥，两个客户端桥都各自发过上行 RPC（证明生成物单一静态 `RemoteSender` 是按 `target.World` 反查桥，而不是「最后一个世界赢」）。
- offer：网关只做字段透传，编码/解码都用生产 `PMDsEntryCodec`；`Prefix` 与端口/摘要/世代/身份逐项比对。

## 7. 测试证据（命令与退出码）

```bat
dotnet build Tools\PMR3IntegrationTest\PMR3IntegrationTest.csproj -c Release   REM 0 警告 0 错误
dotnet Tools\PMR3IntegrationTest\bin\Release\net8.0\PMR3IntegrationTest.dll    REM exit 0，143 项 / 0 失败（连跑 5 次全绿）
dotnet build Tools\PMDsControlTest\PMDsControlTest.csproj -c Release           REM 0 错误
dotnet Tools\PMDsControlTest\bin\Release\net8.0\PMDsControlTest.dll           REM exit 0，506 项（负向 54）/ 0 失败
dotnet Tools\PMDsLobbyTest\bin\Release\net8.0\PMDsLobbyTest.dll               REM exit 0，230 项（负向 28）/ 0 失败（回归）
dotnet build Server\Server.csproj -c Release -o %TEMP%\pmds-srv-check-r3b5     REM 0 错误（临时输出，不动主输出）
```

端口纪律：控制端口取系统临时端口（本次运行 14796），本局 UDP 端口取自**随机高位段**（本次 30100）；两者都断言不等于 7777/7778/7800。循环全部有上界（每阶段 ≤700 轮，步进 20ms 虚拟时间），`using Fixture` + `finally` 释放世界/桥/端点/socket/代理/临时目录（实测无残留临时目录）。

## 8. 缺陷注入（证伪能力，注入后逐字节还原）

| 组 | 注入 | 观察到的失败 | 结果 |
|---|---|---|---|
| 1 | 让 `ResultPending` 分支永不按「认证显式 Exited(0)」判正常完成（`false && …`） | 集成门禁 `G94`、`G100`（终态变 `Failed`） | 124/2，exit 1 → 抓到 |
| 2 | 让 `ResultPending` 分支**忽略**协议证据（`if (_hasResult)`） | 集成门禁 `G136`、`G137`（场景 3 被误判成正常完成） | 141/2，exit 1 → 抓到 |

两次注入后 `md5sum -c` 均 `OK`，复跑门禁恢复 143/0。

## 9. 与三组产物的接口消费点（只读消费）

- **Lobby 组**：`PMDsLobbyHost/TryStartMatch/GetCoordinator/SessionReleased/ResultAccepted/PortPool/PlayerLedger`、`PMDsLobbyHostOptions`、`IPMDsLobbyClientGateway` —— 本门禁只按冻结签名消费，未改。
- **网络组**：`PMUdpSessionEndpoint.OpenServer/OpenClient/Pump/Connected/ConnectionCount/Ledger`、`PMDsEntryCodec`、`PMDsEndpointLedger` —— 未改。
- **Unity 组**：`PMR3Runtime.Register/ProtocolHash/CollisionDigest/Attach/SpawnPlayer/GetBridge`、`PMDsLobbyAgent`（`SceneReady/Log/Warn/ExitRequested/PlayerCountProvider/SubmitResult/ResultAcknowledged/ResultSends`）、生成桩 `PMNet_ServerProbe`/`PMNet_ClientEcho` —— 未改。

## 10. 未闭合 / 剩余风险（诚实口径）

1. **T42 仍 PENDING**：真实 Unity DS 进程、程序化碰撞场景、`Application.Quit` 真实退出、客户端 `PMDS1:` UI 分流与关闭旧 UDP socket 都未在真实 Unity 中跑过。本门禁证明的是**接线**，不是 Unity 构建可用性。
2. **真实退出时序**：真实 DS 的 `SendExited(0)` 在 `RequestExit` 之前发出，但进程实际消失要等 Unity 收尾；若某次观测到「进程先消失、Exited 后到」，协调器会按 `Failed` 收尾（这正是 L2/场景 3 的方向）。真实链路上本机 TCP 投递 + 50ms Tick 应使 Exited 先到，但**未做真机压测**，列为 T42 观察项。
3. **正常完成路径仍会请求强杀**：`Finalize` 对「进程仍在运行」会调 `RequestKill`（既有行为，与 `ResultCommitted` 路径共享）。此时对端已声明退出，强杀只是缩短收尾；本轮**未改**（超出裁决范围），仅记录为观察项。
4. **`PMDsSessionHost`/`PMClientSessionHost` 未编译**：其 Unity-only 部分（场景校验、`Application.Quit`、旧 UDP `CloseExisting`、`UIMatchingPanel` 分流）只有静态审查 + 编译门禁，运行态属 T42。
5. **门禁未挂到全局清单**：仓库内没有枚举各 `Tools` 门禁的总脚本（实测无 `.bat/.sh` 引用既有门禁），因此本门禁只由本报告与命令固化；把它写进主计划的验收命令表由主 Agent 决定（超出本任务写边界）。
6. **`PMNetSessionTest` 的 3 项旧断言**（网络组 §6 记录）本轮**未改**（不在写边界内），仍是已知的「有意行为变更」待收口项。
7. 端口段随机 + 真实 socket：连跑 5 次无碰撞，但**未做高频重复压测**。
8. **验证时点绑定**：本轮实施期间另有并行工作在写 `Client/Assets/Scripts/PMNet/**`（例如 `PMHandshake.cs` 在本次最后一次绿跑之后仍有写入）。本报告的全部绿色结果对应**我实测时的工作树**（`PMR3IntegrationTest` 143/0、`PMDsControlTest` 506/0、`PMDsLobbyTest` 230/0、`Server.csproj` 0 错误）；若 `PMNet/**` 继续变动，需重跑本门禁。
9. **环境观测（非本任务产生）**：`Server/log/2026-09-20_11时17分13秒/server.log` 由**外部进程**启动 Server 程序时生成（内容为运行时输出，含 pid）。本任务未运行任何服务端程序；实测 `dotnet build Server/Server.csproj` **不会**创建该目录。未对该文件/进程做任何处置。

## 11. 明确不做

- 未启动旧服务/旧 DS、未打开 Unity、未抢编辑器锁、未构建 Unity、未常驻任何进程。
- 未执行 `git`/`svn` 写操作、未提交、未改主计划与契约、未改网络/Unity 组文件、未递归委派。
- 未新增 JSON 协议、未改 `SocketProto`、未伪造 `BattleReview`、未回退旧链、未用注入消息绕开应用 codec 或真实 TCP/UDP 主体。
