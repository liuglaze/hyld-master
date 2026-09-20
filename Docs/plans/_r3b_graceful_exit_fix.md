# R3-B8 真实 DS 认证正常退出的优雅退出宽限修复

> 前置：`_r3b_real_unity_smoke.md`（真实进程闭环）、`_r3b_control_liveness_fix.md`（双向 liveness）、
> `_r3b_real_unity_longrun.md` §8.3（真实长局暴露的生产竞态）。
> 本任务只闭合 §8.3 记录的**一处生产次序缺陷**：`S104 真实 OS 退出码 = 0` 对「处理 `Exited(0)`」与
> 「OS 进程真正退出」的先后敏感。写入边界：**仅**
> `Server/DS/PMDsCoordinator.cs`、`Tools/PMDsControlTest/Program.cs`、`Tools/PMDsLobbyTest/Program.cs`、
> `Tools/PMR3UnitySmoke/Program.cs` + 本文件。未改契约、未改主计划、未改 `PMDsLobbyHost`/`PMDsProcess`、
> 未 git/svn 写、未构建或打开 Unity、未递归委派。

## 1. 根因（代码 + 日志，非猜测）

真实正常路径：DS 收到匹配 `ResultAck` → `SendExited(0)` → `Application.Quit(0)`（Unity 收尾需要时间）。
Lobby 侧 `PMDsCoordinator.ApplyProcessExit`（`Server/DS/PMDsCoordinator.cs`）在 `ResultPending` + 经 MAC/对局/世代
认证的显式 `Exited(0)` 时进入 `Finalize(Exited, …)`；旧实现在 `Finalize` 里**无条件**执行
`if (_process != null && _process.IsRunning) RequestKill("终态清理（…)")`。

于是「`Exited(0)` 已收到、Unity 还在收尾」这个窗口里进程被 `Process.Kill()`（.NET 以 `-1` 终止）→
OS 退出码 `-1`，而 DS 日志同时自报 `R3-B 新链请求退出，exitCode=0`（`S104c` 固化了这条对照）。
`_r3b_real_unity_longrun.md` T13 记录了 3 次复现 1 次的实测：`exitCode=-1`（`S104` FAIL）1 次、`0` 2 次
（第 2/3 次只是「Kill 恰好打在已退出进程上是空操作」的运气，不是正确性）。

## 2. 修复方案（生产侧，只改 Lobby 侧协调器）

新增可配置项 `PMDsCoordinatorOptions.GracefulExitGracePeriod`（默认 **5 秒**，`0` 表示不宽限，
负值被 `Validate()` 拒绝）。语义：

1. **认证正常退出**（`ResultPending` + 经认证显式 `Exited(0)` + 已有结果）进入终态后：状态可立刻标 `Exited`，
   但**不请求强杀**，进入有界宽限 `<see IsAwaitingGracefulExit>`；`ResourcesHeld` 保持 true。
2. **宽限内** `Tick` 只做**非阻塞** `IPMDsProcess.IsRunning` 查询（不调 `WaitForExit` 的 500ms 阻塞等待），
   因此宿主单线程 pump 不被占住；端口与名册**不释放**。
3. **宽限内进程自行退出** ⇒ 正常收尾（`GracefulExitObserved++`），全程 `KillRequests == 0`。
4. **宽限到期**仍未退出 ⇒ 记 `GracefulExitTimeouts++`、把「异常收尾」追加进 `LastReason`，转入
   **节流**强杀（沿用 `KillRetryInterval`）；强杀失败继续占用资源。
5. **失败路径不变**：非认证 / 非 0 退出码 / 纯进程退出 / 启动失败 / 心跳超时等一律**立即**按需强杀，
   不进入宽限；且**仍然只有本地确认进程退出才释放**端口与名册（旧不变量未被放宽）。
6. 纵深防御：`BeginGracefulExitWait()` 把 `_nextKillRetryMilliseconds` 推到宽限到期，
   避免任何其它入口在宽限内因「从未强杀过」而立即放行强杀。

新增可观测计数：`GracefulExitWaits` / `GracefulExitObserved` / `GracefulExitTimeouts`；新增只读属性
`IsAwaitingGracefulExit`。**生产只改 Server 侧，无需重新构建 Unity DS 包**。

## 3. 实施步骤

| 步 | 内容 | 状态 |
|---|---|---|
| S1 | 根因定位：`Finalize` 无条件强杀 + 长局实测证据 | DONE |
| S2 | 协调器：`GracefulExitGracePeriod` + `Validate` + 三个计数 + `IsAwaitingGracefulExit` | DONE |
| S3 | 协调器：`Finalize(…, bool gracefulPeerExit)` 重载 + `BeginGracefulExitWait` | DONE |
| S4 | 协调器：`ContinueTerminalCleanup` 宽限分支（非阻塞轮询 / 到期异常收尾）+ `TryFinishTerminalCleanup(…, allowBlockingWait)` 参数 | DONE |
| S5 | 控制门禁：`L1`/`L3` 旧 oracle 按**正常/故障**区别补强（不删旧断言） | DONE |
| S6 | 控制门禁：新增 N 节（默认 5s / 可配置 / 宽限内零强杀 / 到期强杀 / 强杀失败继续占用） | DONE |
| S7 | Lobby 门禁：新增 K 节（真实 `PMDsLobbyHost` + 真实 loopback TCP 宿主链路） | DONE |
| S8 | 烟测：`S104d/S104e/S109b/S109c` + `summary` 增列 | DONE |
| S9 | build 0 → run：ControlTest / LobbyTest / IntegrationTest / UnitySmoke | DONE |
| S10 | 负向验证（故障注入，字节级备份 + finally 恢复） | DONE |

## 4. 测试验收集

| ID | 实施映射 | 场景 | 执行者 | 方法/命令 | 预期结果 | 状态 | 证据 |
|---|---|---|---|---|---|---|---|
| T1 | S2 | 默认宽限 5s 且可配置；负值被拒 | AI | `PMDsControlTest` N1/N2 | `GracefulExitGracePeriod==5000ms`；负值抛 `ArgumentOutOfRangeException` | PASS | N1/N2 OK |
| T2 | S3/S4 | `Exited(0)` 时进程仍活：宽限内不 Kill / 不阻塞 / 不释放 | AI | `PMDsControlTest` N4–N19 | 终态 `Exited`、`IsAwaitingGracefulExit`、`KillRequests==0`、`KillCount==0`、`WaitForExitCount==0`、端口/名册仍占用 | PASS | N4–N19 全 OK |
| T3 | S4 | 宽限内重复 `Exited` 也不强杀 | AI | `PMDsControlTest` N20/N21 | 受理（终态收尾）且 `KillRequests==0` | PASS | N20/N21 OK |
| T4 | S4 | 宽限内自行 `exit 0` ⇒ 释放且全程零强杀 | AI | `PMDsControlTest` N22–N31 | `GracefulExitObserved==1`、`KillRequests==0`、端口/名册释放、`ResourcesReleased==1` | PASS | N22–N31 OK |
| T5 | S2/S4 | 宽限**可配置**：1200ms 内不强杀、1201ms 才强杀 | AI | `PMDsControlTest` N32–N36 | 1199ms 仍宽限且零强杀；到期触发异常收尾 | PASS | N32–N36 OK |
| T6 | S4 | 超宽限才 Kill；kill 失败继续占用 + 节流重试 | AI | `PMDsControlTest` N37–N56 | `GracefulExitTimeouts==1`、`KillRequests` 1→2→3、`KillRequestFailures` 同步、端口/名册仍占用；确认退出后才释放 | PASS | N37–N56 OK |
| T7 | S5 | 旧 oracle 区分正常/故障（不删断言） | AI | `PMDsControlTest` L1/L3 增补 | 正常：`IsAwaitingGracefulExit` + 零强杀；故障（`Exited(7)`）：立即 `KillRequests==1` 且不走宽限 | PASS | L11b–L11e / L18b–L18c / L28b–L28e OK |
| T8 | S7 | 宿主链路（真实 host + 真实 TCP）：宽限内零强杀、自行退出后回收 | AI | `PMDsLobbyTest` K1–K21 | `KillCount==0`、端口/uid 仍占用、无 `SessionReleased`；退出后端口/uid 回收、恰一次 `SessionReleased` | PASS | K1–K21 OK |
| T9 | S7 | 宿主链路：宽限到期才强杀；强杀失败继续占用 | AI | `PMDsLobbyTest` K22–K38 | `GracefulExitTimeouts>=1`、`KillRequests==1`、进程仍活、端口/uid 仍占用；真退出后才回收 | PASS | K22–K38 OK |
| T10 | S8 | 真实 Unity DS（用户 13:02 包）+ 40s hold：`osExit 0` 且正常路径无 Kill | AI | `PMR3UnitySmoke --hold-seconds 40` | 140/0、exit 0、`osExitCode=0`、`coordinatorKillRequests=0` | PASS | `artifacts/20260920-133720-12b48d29`、`artifacts/20260920-133923-ec2eedff`（两次均 140/0 / exit 0） |
| T11 | S8 | 默认快速模式未被破坏 | AI | `PMR3UnitySmoke`（无参数） | 128/0、exit 0、`osExitCode=0` | PASS | `artifacts/20260920-133808-df201dc5` |
| T12 | S9 | 既有回归无退化 | AI | IntegrationTest + ControlTest + LobbyTest | IntegrationTest 143/0；ControlTest 623/0；LobbyTest 268/0 | PASS | 各工程 run 输出 |
| T13 | S10 | 负向验证：三处注入必须被新断言抓住 | AI | 字节级备份 → 注入 → build0/run≠0 → finally 恢复 | 每次只让**对应**断言失败，恢复后 sha256 与备份逐字节一致 | PASS | 见 §6 |

## 5. 实测结果（全部本次 build 退出 0 后 run）

| 门禁 | 结果 | 说明 |
|---|---|---|
| `PMDsControlTest` | **623 / 0**（exit 0） | 旧 556 + 新增 N 节 57 + L 节补强 10 |
| `PMDsLobbyTest` | **268 / 0**（exit 0） | 旧 230 + 新增 K 节 38 |
| `PMR3IntegrationTest` | **143 / 0**（exit 0） | 与既有基线一致，无退化 |
| `PMR3UnitySmoke --hold-seconds 40` | **140 / 0**（exit 0，40846ms） | 旧 136 + 新增 4；`osExitCode=0`、`KillRequests=0`、`GracefulExitWaits=2`、`GracefulExitObserved=1`、`GracefulExitTimeouts=0` |
| `PMR3UnitySmoke`（默认） | **128 / 0**（exit 0，746ms） | 旧 124 + 新增 4 |

真实 DS 包（未重新构建，**不改 DS 侧**）：`Assembly-CSharp.dll` mtime `2026-09-20 13:02:38`、
size `1028608`、sha256 `13ab0a772332e158ef662f4ce36c4541d16a4cc453aef8b37ed0bb4b16e24a65`
（与主线「控制保活修复」记录一致，`S116` 预检通过）。

hold 运行关键量：`osExitCode=0`、`lobbyTerminalReason=结果已本地受理，且对端经认证显式 Exited(0)（收到 Exited 消息）`、
`sessionReleased=True`、`coordinatorKillRequests=0`、`coordinatorGracefulExitObserved=1`、
`coordinatorGracefulExitWaits=2`、`coordinatorGracefulExitTimeouts=0`、端口 43320 退出后独占 bind 成功。
对照修复前同命令：`coordinatorKillRequests=1`（且 3 次里 1 次 `osExitCode=-1`）。
本任务对同一条 hold 命令跑了 **2 次**（`20260920-133720-12b48d29` / `20260920-133923-ec2eedff`），
两次均为 140/0、exit 0、`osExitCode=0`、`coordinatorKillRequests=0`、`GracefulExitObserved=1`。

## 6. 负向验证（故障注入；先按原字节备份，finally 恢复）

备份：`Server/DS/PMDsCoordinator.cs` → 临时文件（sha256 `227e90a352a39504606874ff05aedf3f9d7f1a499f93c155cb9bdd732e31e913`），
恢复后 cp 校验与备份**逐字节一致**（实测 sha256 相同，工作区无注入残留）。

| 注入 | 注入内容 | 预期失败的断言 | 实测 |
|---|---|---|---|
| I1 | `if (gracefulPeerExit)` → `if (false && gracefulPeerExit)`（退回旧的无条件强杀） | 正常路径「宽限内零强杀」类 | **29 项失败**（`L11b–L11e`、`L18b/L18c`、`N7/N8…`），594/29，exit 1 |
| I2 | 宽限路径 `TryFinishTerminalCleanup(…, false, !graceStarted)` → `(…, false, true)`（宽限内变成 500ms 阻塞等待） | 「不阻塞 pump」类 | **3 项失败**（`L11e`、`N10`、`N18`），620/3，exit 1 |
| I3 | 宽限到期分支插入 `ReleaseResources("injected")`（未确认退出就释放） | 「失败保持资源直到实际退出」类 | **12 项失败**（`N37`、`N40`、`N43–N46`、`N48–N49` …），611/12，exit 1 |

三次注入都只在 build 0 之后运行，且每次只让**对应**断言失败（说明断言强度真实、不是恒真）。

## 7. 边界与未验

- 未改控制协议 / wire 类型 / 传输层 / 生成物；未改 `PMDsLobbyHost`、`PMDsProcess`、契约、主计划。
- 新增 `GracefulExitGracePeriod` 默认值由协调器提供；宿主未透传配置项（默认 5s 即生产口径），
  该透传属宿主私有配置面，本轮未做、不影响正确性。
- 未验：客户端真实 UnityUI 分流与 `Application.Quit`（T42 仍 PENDING）、跨机 MTU/NAT、弱网、
  票据过期/重放压力、数十次重复压测（`S104` 竞态是否在**所有**时序下稳定仅由 1 次 hold + 1 次快速模式覆盖）。
- 仍存在（**本任务范围外**）：若 DS 处理 `ResultAck` 超过 `MaxResultAckRetransmits × ResultAckRetryInterval`
  （≈3–4s）才发 `Exited(0)`，Lobby 已进 `ResultCommitted` 并走 `ShutdownGracePeriod`，随后仍可能被强杀；
  本轮未改该预算，也未在真实 DS 上刻意构造该延迟。
- 不提交代码；不宣称整条 T42 通过。
