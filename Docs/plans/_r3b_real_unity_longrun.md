# R3-B7 真实 Unity DS 长局 hold 验证（PMR3UnitySmoke --hold-seconds 40）

> 子任务：真实进程闭环（`_r3b_real_unity_smoke.md`）与控制面双向 liveness 修复（`_r3b_control_liveness_fix.md`）之后，
> 用**用户重新构建**的 HyldDS（`Assembly-CSharp.dll` 2026-09-20 13:02:38 / 1028608B，含 `RuntimeLivenessTimeoutMs` + `LastTrustedReceiveMs`）
> 实测「两客户端已激活且各见两对象后**延迟 40 秒**再发 owner 探针，真实 DS 不再因无下行自退，随后原闭环仍走完」。
> 写入边界：**仅** `Tools/PMR3UnitySmoke/Program.cs` + 本文件 + `Tools/PMR3UnitySmoke/artifacts/**`。
> **未改任何生产源码**（`PMDsSessionHost` / `PMDsLobbyAgent` / `PMDsCoordinator` / `PMDsProcess` 只**只读核查**）、
> 未改现有门禁、未动契约与主计划、未 git/svn 写、未打开或构建 Unity、未杀用户进程、未递归委派。

## 1. 目标与范围

| # | 目标 | 落点 |
|---|---|---|
| G1 | 新增 `--hold-seconds N`（默认 0 = 原快速模式不变）：两客户端已激活且各见两对象后，关闭探针闸门 N 秒 | `Program.cs` |
| G2 | hold 期间**不阻塞 pump**：仍以 ≤50ms 步进泵控制 host（TCP 控制面 + 心跳应答）与两个客户端 UDP | `Program.cs` |
| G3 | hold 期间持续断言：真实 DS 进程仍活、协调器 `Running`、无结果、所有连接仍 ready、真实心跳多次往返 | `Program.cs` |
| G4 | hold 时长从「两客户端各见两副本之后」开始计时，实测 ≥ 目标；证明 > 30s 不是只从进程启动计时 | `Program.cs` |
| G5 | hold 期间**绝不发 owner 探针**（否则 DS 的 `-server-smoke` 会立刻提交结果结束闭环） | `Program.cs` |
| G6 | 开闸后原路径不变：Probe → Echo → 复制结果 Ack → 真实 OS `exit 0` → 端口/uid 回收 全套仍过 | `Program.cs` |
| G7 | 核查 DS 自有 smoke 期限（`PMDsSessionHost.SmokeDeadlineMs = 120000`），把 hold 限制在其内，**不改生产绕过** | 只读核查 + 工具侧守卫 |
| G8 | 新增断言与 `summary` 记录 hold 实际起止/毫秒、心跳次数、DS 自报 `hbRx` 与 DS 新 dll sha256 | `Program.cs` |
| G9 | 可选故障模式 `--drop-control-after-ready`：真实 Lobby 停止控制下行，验证真实 DS 非 0 退出且资源仍回收 | `Program.cs` |

## 2. 非目标与约束

- 不改控制协议 / wire 类型 / 传输层 / 生成物；不改任何生产文件的**行为或常量**。
- 不改现有 122 项断言的检查项与其强度；新增断言独立编号（`S7b`、`S0`、`S73a`…、`S73m`…、`S104c`）。
- 不用虚拟时钟；全程真实系统时钟 + 真实 `HyldDS.exe` 进程 + 真实 TCP/UDP。
- 总预算 `TotalBudgetMs = 120000` **保持不变**；hold 需满足 `holdMs + 60000 <= 120000` 且 `holdMs < 120000(DS smoke 期限)`。
- 不把客户端 UnityUI 侧（`UIMatchingPanel` / `PMClientSessionHost` / 真实 `Application.Quit`）算作已验证；不宣称整个 T42 通过。

## 3. 实施步骤

| 步 | 内容 | 状态 |
|---|---|---|
| S1 | 本文件落盘（计划 + 测试集） | DONE |
| S2 | `Program.cs`：CLI 解析 `--hold-seconds` / `--drop-control-after-ready` + 预算/smoke 期限守卫（`S0`） | DONE |
| S3 | `Program.cs`：`ClientHarness.ProbeGateOpen` 探针闸门（hold/drop 期间保留待发探针但不发） | DONE |
| S4 | `Program.cs`：`RunHoldPhase`（泵 + 5s 进度 + 不变量断言 + DS 日志 `hbRx` 证据） | DONE |
| S5 | `Program.cs`：`RunDropControlPhase`（停控制下行 → DS 非 0 退出 → 资源回收） | DONE |
| S6 | `Program.cs`：预检新增 `RuntimeLivenessTimeoutMs` / `LastTrustedReceiveMs` 符号（`S7b`）+ `summary` 增列 | DONE |
| S7 | build 0 → run `--hold-seconds 40` | DONE |
| S8 | run（默认快速模式）/ `--hold-seconds 90`（守卫）/ `--drop-control-after-ready` | DONE |
| S9 | 本文件补齐实测证据与诚实边界 | DONE |

## 4. 测试验收集

| ID | 实施映射 | 场景 | 执行者 | 方法/命令 | 预期结果 | 状态 | 证据 |
|---|---|---|---|---|---|---|---|
| T1 | S2 | 默认快速模式不被破坏 | AI | `PMR3UnitySmoke.dll`（无参数） | 原口径不变、exit 0 | PASS | 124/0，737ms，`artifacts/20260920-132541-c7bb9ab6` |
| T2 | S2 | 过大 hold 被拒（不改生产绕过 smoke 期限） | AI | `--hold-seconds 90` | 预算守卫报失败、不启动 DS | PASS | `S0` FAIL、exit 1、0 passed/1 failed，`artifacts/20260920-132542-686c2cbd` |
| T3 | S6 | 用户新构建含保活符号 | AI | 预检 `S7b` | 含 `RuntimeLivenessTimeoutMs`+`LastTrustedReceiveMs` | PASS | dll 13:02:38 / 1028608B / sha256 `13ab0a77…24a65`，`S7b` OK |
| T4 | S3/G5 | hold 期间零探针 | AI | `--hold-seconds 40`，`S73g` | `ProbeNonceSent=0/0` | PASS | `S73g` OK（40014ms 内 0 探针） |
| T5 | S4/G3 | hold 期间 DS 仍活 | AI | 同上，`S73a`/`S73b` | 存活采样全程真、`ExitObservations=0` | PASS | `S73a/S73b` OK，存活采样 643 |
| T6 | S4/G3 | hold 期间协调器 Running 且无结果 | AI | 同上，`S73d`/`S73e` | `State=Running`、`ResultAccepted=0` | PASS | `S73d/S73e` OK |
| T7 | S4/G3 | hold 期间连接仍 ready | AI | 同上，`S73f` | 各 1 条已激活 + 控制连接已绑定 + 无端点失败 | PASS | `S73f` OK |
| T8 | S4/G3 | hold 期间真实心跳多次 | AI | 同上，`S73h/h2/i/j/k` | DS→Lobby ≥5、应答 ≥5、无心跳超时、DS 日志 `hbRx≥2`、心跳 tick ≥2 | PASS | 8/8 次、`HeartbeatTimeouts=0`、`hbRx=8`、9 条 tick |
| T9 | S4/G4 | 实际延后 ≥ 40s 且从「各见两副本」起算 | AI | 同上，`S73c` | `holdMilliseconds >= 40000` | PASS | 40014ms（13:24:54.055→13:25:34.069），中途破坏点=无 |
| T10 | S6/G6 | 开闸后原闭环全套仍过 | AI | 同上，S74–S121 | Probe/Echo/收敛/ResultAck/`exit 0`/端口 uid 回收全过 | PASS | 136/0；`osExitCode=0`、`SessionReleased`、`InUseCount=0`、退出后独占 bind 成功，`artifacts/20260920-132453-adfbbecd` |
| T11 | S5/G9 | 停控制下行 → DS 非 0 退出 | AI | `--drop-control-after-ready` | `Exited` 非 0、≥9s、根因日志、资源回收 | PASS | 89/0；停下行 15091ms 后 `exitCode=1`；终态 `Failed` 原因=`运行期 15000ms 未收到 Lobby 可信控制帧`；端口/uid/bootstrap 全回收，`artifacts/20260920-132432-72cba80e` |
| T12 | S4/G7 | hold 限制在 DS smoke 期限内 | AI | 只读核查 + 守卫 | `SmokeDeadlineMs=120000` > hold 40s；未改生产 | PASS | 源码 `PMDsSessionHost.cs:182`；守卫 `S0`；未改生产 |
| T13 | S4/G6 | `S104` 真实 OS `exit 0` 是否稳定 | AI | 3 次 `--hold-seconds 40` | 期望 0 | **RISK（观察）** | 第 1 次 `exitCode=-1`（`S104` FAIL，134/1，`artifacts/20260920-131946-eedd66a7`）；第 2/3 次 `exitCode=0`（136/0）。根因见 §8.3 |

## 5. 决策与范围变更记录

- hold 用「探针闸门」实现而非改写 `ClientHarness` 业务逻辑：闸门只影响「是否发探针」，不动复制/泵路径。
- hold 起点选在 `replicated` 断言（各见两副本）之后，而非进程启动之后，以证明「不是只从启动计时」。
- 故障模式用「停止驱动 `PMDsLobbyHost.PumpOnce()`」模拟 Lobby 停止下行，不伪造接口、不注入伪造帧。
- 核查结论：DS 自有 smoke 期限 `SmokeDeadlineMs=120000` 远大于 hold 40s，**无需**修改生产；工具侧守卫 `holdMs+60000 <= 120000` 同时满足预算与 smoke 期限。
- `S121`（零控制帧投递失败）改为只对**正常闭环**断言：故障路径下对端突然死亡后，本端仍会把一条已排队的空闲帧（对 Error 的回执）投到已关闭连接上，这是预期行为，不作为失败（`ArchiveEvidence(bool strictControlFrames)`）。

## 6. 交接记录

- 接手者必须先读：根 `AGENTS.md` → `Client/Assets/AGENTS.md` → `Server/AGENTS.md` → `net-architecture-migration.md` 末「真实DS验收与控制保活修复」
  → `net-r3-control-contract.md` §9 → `_r3b_real_unity_smoke.md` → `_r3b_control_liveness_fix.md`。
- 前置条件：用户已重 build `HyldDS.exe`（含控制面 liveness 修复）。若无该构建，`S7b` 会直接失败，长局会复现 30s 自退。
- 复现命令：`dotnet build Tools\PMR3UnitySmoke\PMR3UnitySmoke.csproj -c Release`（0 警告 0 错误）→
  `dotnet Tools\PMR3UnitySmoke\bin\Release\net8.0\PMR3UnitySmoke.dll --hold-seconds 40`。

## 7. 最终结果

- **核心目标（40s 长局 hold）PASS**：`--hold-seconds 40` 最终二进制 **136 项检查 / 0 失败 / exit 0**，总耗时 40732ms。
  hold 实测 **40014ms**，全程 DS 存活（643 次采样）、协调器 `Running`、零结果、连接全 ready、**零探针**、
  真实心跳 **DS→Lobby 8 次 / Lobby→DS 应答 8 次**、DS 自报 `hbRx=8`。
- **开闸后原闭环仍全套 PASS**：Probe→Echo→`ProbeCount` 收敛→权威结果→`ResultAck`→认证显式 `Exited(0)`→
  真实 OS `exitCode=0`→`SessionReleased`→端口池/uid 账本清空→bootstrap 删除→退出后独占 bind 成功。
- **可选故障模式 PASS**：`--drop-control-after-ready` **89 项 / 0 失败 / exit 0**；真实 Lobby 停止控制下行 15091ms 后
  真实 DS 以 `exitCode=1` 退出，终态 `Failed`，根因日志精确为「运行期 15000ms 未收到 Lobby 可信控制帧」。
- **回归**：默认快速模式 **124/0 / exit 0**；守卫 `--hold-seconds 90` 被 `S0` 拒绝（exit 1，不启动 DS）。
- **T42 仍 PENDING**：客户端真实 UnityUI 分流/收尾与跨机 MTU/NAT 未验；本文不宣称整个 T42 通过。
- **一处生产竞态（未修，属生产范围）**：见 §8.3。

## 8. 实测证据

### 8.1 命令与退出码

```bat
dotnet build Tools\PMR3UnitySmoke\PMR3UnitySmoke.csproj -c Release      REM 0 警告 0 错误
dotnet Tools\PMR3UnitySmoke\bin\Release\net8.0\PMR3UnitySmoke.dll       REM exit 0，124 项 / 0 失败（默认快速模式，737ms）
dotnet ...PMR3UnitySmoke.dll --hold-seconds 40                          REM exit 0，136 项 / 0 失败（40732ms）
dotnet ...PMR3UnitySmoke.dll --drop-control-after-ready                 REM exit 0，89 项 / 0 失败（20576ms）
dotnet ...PMR3UnitySmoke.dll --hold-seconds 90                          REM exit 1，S0 拒绝（不启动 DS）
```

### 8.2 关键实测量（最终二进制）

- 预检：`assemblyMtime=2026-09-20 13:02:38`、`size=1028608`、`sha256=13ab0a772332e158ef662f4ce36c4541d16a4cc453aef8b37ed0bb4b16e24a65`；`S7b` 含保活修复符号。
- hold：`holdStartWall=2026-09-20 13:24:54.055`、`holdEndWall=13:25:34.069`、`holdMilliseconds=40014`、
  `holdHeartbeats=8`、`holdHeartbeatReplies=8`、`holdDsAliveSamples=643`、`holdDsLogHeartbeatTicks=9`、`holdDsMaxHbRx=8`。
- 收尾：`exitedTerminal=True`、`osExitCode=0`、`osExitObservations=1`、`sessionReleased=True`、
  `midRunPortProbeError=SocketException（端口被真实 DS 持有）`、`postExitPortProbeError=<ok>`、`coordinatorKillRequests=1`。
- DS 日志：`UDP boundPort=<分配端口>`、`玩家副本已创建 uid=301/302`、`smoke 验收：名册 2 人全部完成探针`、
  `收到匹配的 ResultAck(4294967297)`、`R3-B 新链请求退出，exitCode=0`、`已关闭（reason=OnApplicationQuit，新链）`、
  多条 `heartbeat tick=… hbRx=…`（无秘密）。
- 故障模式：`DropControlStart replies=2 → DropControlEnd blackoutMs=15091 exitCode=1 → SessionReleased state=Failed
  reason=DS 上报错误 code=1：运行期 15000ms 未收到 Lobby 可信控制帧（持续上行发送成功不代表 Lobby 存活）`。

### 8.3 唯一一次失败与根因（生产竞态，未修改生产）

第 1 次 `--hold-seconds 40`（`artifacts/20260920-131946-eedd66a7`）**135 项 / 1 失败**，唯一失败是
`S104 真实 OS 退出码 = 0`，实际 `osExitCode=-1`；hold 本身 12 项全绿（40003ms / hbRx=8）。

根因（读源码 + 日志，非猜测）：正常结果路径下，真实 DS 先发**认证过**的 `Exited(0)` 控制消息，再走 `Application.Quit(0)`
（Unity 关闭需要时间）。`PMDsCoordinator.Finalize(...)`（`Server/DS/PMDsCoordinator.cs` ≈2313 行）在进入终态时无条件执行
`if (_process != null && _process.IsRunning) { RequestKill("终态清理（" + terminalState + "）"); }`。
于是「处理 `Exited(0)` 时 Unity 还没退完」→ 立刻 `Process.Kill()`（.NET 以 `-1` 终止）→ OS 退出码 `-1`，
而 DS 日志同时自报 `R3-B 新链请求退出，exitCode=0`（新增断言 `S104c` 固化了这条对照）。
第 2/3 次运行该竞态未命中（OS 退出比终态早 40ms / 更早）→ `exitCode=0`、`coordinatorKillRequests=1` 但打在已退出进程上是空操作。
默认快速模式（737ms）3 次均为 `exitCode=0`。

结论：`S104` 对**处理 `Exited(0)` 与 OS 进程真正退出之间的先后**敏感，是**生产侧**既有次序问题（终态先强杀、不等优雅退出宽限），
不是本工具或本次改动引入；按任务「不改生产」要求**未修**，仅在此记录，交由主 Agent 裁决。

## 9. 明确不做 / 未验

- 未改任何生产源码与现有门禁；未动契约/主计划；未 git/svn 写；未打开或构建 Unity；未启动旧服务（7777/7778）与旧 DS；未杀用户进程。
- 未验：客户端真实 UnityUI 分流与 `Application.Quit`（T42 仍 PENDING）、跨机 MTU/NAT、弱网、票据过期/重放压力、数十次重复压测。
- `S104` 竞态未修（见 §8.3），因此**不宣称**「真实 OS exit 0 在所有时序下稳定复现」。
