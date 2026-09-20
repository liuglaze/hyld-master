# R3-B6 真实 Unity DS 进程烟测实施与验收报告（PMR3UnitySmoke）

> 子任务：把 R3-B 从「进程替身」推进到**真实 Unity 2019.4 无头 DS 进程**，并实测整条闭环。
> 写入边界：新增 `Tools/PMR3UnitySmoke/{PMR3UnitySmoke.csproj,Program.cs}` + 本文件；运行期写 `Tools/PMR3UnitySmoke/bin|obj|artifacts/**`。
> **未改任何生产源码、未改现有门禁、未动主计划与契约、未 git/svn 写操作、未递归委派。**
> 阶段状态仍以 `net-architecture-migration.md` 为准；本文只记录本轮实施与实测证据。

## 1. 结论摘要

- **真实 Unity DS 进程闭环打通**：`Tools/PMR3UnitySmoke` build **0 警告 0 错误** → run **exit 0，122 项检查 / 0 失败**，连续 5 次全绿（run4–run8），单次总耗时 **730–760 ms**（上限 120000 ms）。
- 闭环实测（全部真实主体，无 DS 替身）：真实 `PMDsLobbyHost` 写引导文件并生成启动参数 → 真实 `PMDsSystemProcessLauncher` 拉起
  `D:\UGit\hyld-master\HyldDS\HyldDS.exe`（pid 真实、`Forcing GfxDevice: Null` 真无头）→ 真实控制 TCP Ready → 真实 `PMDS1:` offer
  → 两个**纯 C#（非 UnityUI）**客户端在真实 UDP 上握手/验票/激活 → 权威创建 2 副本 → 各 1 次 owner `ServerProbe`
  → `ClientEcho` 回流 + `ProbeCount` 双向收敛 → 真实 DS 的 `-server-smoke` 结果 → `ResultAck` → 认证显式 `Exited(0)`
  → **真实 OS 进程退出，退出码 0** → 端口/uid 回收 + bootstrap 删除 + OS 归还 UDP 端口。
- **T42 仍不能标 PASS**：本轮证明的是「**DS 侧真实进程与真实场景**」，**客户端侧真实 UnityUI 分流**（`UIMatchingPanel` 的 `PMDS1:` 分支、
  `PMClientSessionHost`、关闭旧 UDP socket、真实客户端 `Application.Quit`）与**跨机 MTU/NAT** 仍未验。见 §8。
- 3 个可复核的观察项（**均为观察，非本轮修改**，精确文件与最小修复建议见 §7）。

## 2. 必读范围（实际读取，按序）

| 顺序 | 文档 | 对本次实施的直接约束 |
|---|---|---|
| 1 | `AGENTS.md`（根） | 三处入口文档指向 |
| 2 | `Client/Assets/AGENTS.md` | DS 启动参数契约（`-server/-port/-bootstrap/-control/-logFile`）、真无头包、战斗端口硬编码 7777 的历史坑 |
| 3 | `Server/AGENTS.md` | `net8.0`、`PMNet/**` 链接共享、非交互保活差异 |
| 4 | `net-architecture-migration.md`（卷首当前状态 + 文末 R3-B 验收表） | R3B5 已 PASS 但仍是「进程替身」；T42 真实 UnityDS 必须保持 PENDING 直到真进程证据齐全 |
| 5 | `net-r3-control-contract.md` §7/§8 | §7.3 启动参数必须由 Lobby 从 Allocate 后真实端口生成；§7.4 `-server-smoke` 显式验收 + 固定碰撞场景 + SceneReady 口径；§8 终态语义与「资源释放只认本地进程观测」 |
| 6 | `_r3b_integration_report.md` | 可复用的装配模式（网关/客户端宿主/断言口径），以及「唯一边界外替身 = 进程」正是本轮要替换掉的东西 |
| 7 | `windows-shell-compat` skill | 命令按实际解释器写（本机 Git Bash 驱动 `dotnet`，不混用 PowerShell/cmd 语义） |

文档如何决定入口：§7.3 决定「启动参数只能来自真实 `PMDsLobbyHost`，测试仅允许薄包装追加」；§7.4 决定烟雾走 `-server-smoke`、
场景证据看 `PMDsSessionHost` 的 SceneReady/CollisionDigest 日志；§8 决定终态必须同时核对**认证显式 Exited(0)** 与**真实 OS 进程退出**。

## 3. 交付物

| 文件 | 行数 | 说明 |
|---|---|---|
| `Tools/PMR3UnitySmoke/PMR3UnitySmoke.csproj` | 52 | 新增：net8.0 console，直编真实 `Client/Assets/Scripts/PMNet/**`、`PMR3/**`（+Generated）、`Server/DS/**`；**不编** `Server/Boot/*`（需 UnityEngine，跑在真实 DS 里） |
| `Tools/PMR3UnitySmoke/Program.cs` | 1584 | 新增：真实 Lobby 宿主 + 真实系统启动器 + 薄包装 Launcher + 两个纯 C# 客户端宿主 + 122 项检查 |
| `Docs/plans/_r3b_real_unity_smoke.md` | 本文件 | 新增：计划/测试集/实测证据/观察项 |
| `Tools/PMR3UnitySmoke/artifacts/<run>/` | — | 运行产物：真实 DS 日志、启动记录、预检、timeline、summary |

编码：`Program.cs` UTF-8(BOM)+CRLF；`.csproj` UTF-8 无 BOM+LF（与 `Tools/PMR3IntegrationTest` 同惯例）；`.md` UTF-8(BOM)+LF。
`Tools/` 下无 `.meta` 约定（实测该目录无 `.meta`），故未新增。

## 4. 设计要点（为什么这样装配）

- **唯一被包装的缝 = 参数尾部追加**：`SmokeLauncher : IPMDsProcessLauncher` 先 `request.Clone()`，再追加
  `-server-smoke` 与 `-logFile <artifacts 绝对路径>`，然后原样交给真实 `PMDsSystemProcessLauncher`（真实白名单只含固定 exe）。
  exe / 工作目录 / `-port` / `-bootstrap` / `-control` / `-dsid` / `-matchid` **全部**来自真实 `PMDsLobbyHost`，测试不生成、不替换。
- **真实进程启动器 + 白名单**：`PMDsProcessWhitelist.Add(D:\UGit\hyld-master\HyldDS\HyldDS.exe)`；
  `ProcessStartInfo.UseShellExecute=false` + `ArgumentList`（无 shell 拼接），与生产同一条路径。
- **真实时钟**：宿主用 `PMDsSystemClock.Instance`；客户端 `Pump` 传 `DateTimeOffset.UtcNow` 毫秒/秒。
  刻意**不**用虚拟时钟——虚拟时钟会把真实 Unity 启动、真实 TCP/UDP 往返压成假超时。
- **轮询纪律**：测试主循环每步 `Thread.Sleep(≤50ms)`，每次驱动 `_host.PumpOnce()` + 两个客户端 `Pump`；总预算 120000 ms，每阶段另有上界。
- **客户端是纯 C# 非 UnityUI**：没有 GameObject/UI/渲染，只有 `PMSession/PMNetWorld/PMNetSessionBridge/PMUdpSessionEndpoint.OpenClient/PMR3Runtime`；
  owner 探针只在宿主帧步里经生成桩 `PMNet_ServerProbe` 发出（不在复制回调里发，避免重入桥）。
- **端口回收证据（双向）**：用 `Socket(ExclusiveAddressUse=true)` 探测同一 UDP 端口——DS 运行期 bind 必须**失败**（证明端口真被 DS 持有），
  进程退出后 bind 必须**成功**（证明 OS 真的归还）。
- **OS 退出码获取时机**：在启动瞬间就给真实进程对象挂 `Exited` 订阅，退出码在事件里抓（原因见 §7 观察 1）。

## 5. 测试集与结果（T1–T9 全部 PASS）

| ID | 场景 | 方法/命令 | 预期 | 状态 | 证据 |
|---|---|---|---|---|---|
| T1 | 构建产物预检 | 工具阶段 0（S1–S8） | mtime/size/sha256 + 4 个关键符号 | PASS | mtime=2026-09-20 12:12:46 size=1028096 sha256=`6df6285f…22ce9`，含 `PMDsSessionHost`/`PMUdpSessionEndpoint`/`PMR3Player`/`PMDsLobbyAgent` |
| T2 | 工具编译 | `dotnet build Tools\PMR3UnitySmoke\PMR3UnitySmoke.csproj -c Release` | exit 0 | PASS | **0 警告 0 错误**，exit 0 |
| T3 | 真实进程启动 | 工具运行（S17–S34） | 真 exe/pid/参数 | PASS | pid=69596；args 见 §6；`SessionStarted … pid=69596` 与真实 pid 一致（S22） |
| T4 | 控制面就绪 | 工具运行（S36–S48） | Ready/SceneReady/digest | PASS | DS 日志：`已上报 Ready：boundPort=40860 sceneReady=True digest=0x52334201`；协调器 → Running |
| T5 | 真实 UDP 两客户端 | 工具运行（S49–S70） | 2 offer + 握手/激活 + 各 2 副本 | PASS | 2 份 `PMDS1:` offer；`HandshakeRejections=0`；A 收 9 发包 3 发 |
| T6 | 探针/回声/收敛 | 工具运行（S74–S91） | 各 1 次 Echo + ProbeCount=1 | PASS | `EchoCount=1`、nonce 原样、双方互相看到对方 `ProbeCount=1`；DS 日志 `玩家副本已创建 uid=301/302` |
| T7 | 结果幂等 + 退出语义 | 工具运行（S92–S101） | ResultAccepted=1 + 终态 Exited | PASS | `ResultAccepted=1`、网关结果通知 1 次、摘要含 `smoke`、`LastReason=…对端经认证显式 Exited(0)…` |
| T8 | **真实 OS 退出 + 端口回收** | 工具运行（S102–S113） | 真退出 + 资源回收 | PASS | `Process.Exited` 观测 1 次、**exitCode=0**；`PortPool.InUseCount=0`、`PlayerLedger.Count=0`、bootstrap 已删、退出后独占 bind 成功 |
| T9 | 失败可诊断 | 工具运行（artifacts） | 保留真实 DS 日志/错误 | PASS | artifacts 含 DS 日志、`launch-args.txt`（无票据密钥）、`preflight.txt`、`timeline.txt`、`summary.txt` |

## 6. 测试证据（命令、退出码、关键日志）

```bat
dotnet build Tools\PMR3UnitySmoke\PMR3UnitySmoke.csproj -c Release     REM exit 0，0 警告 0 错误
dotnet Tools\PMR3UnitySmoke\bin\Release\net8.0\PMR3UnitySmoke.dll      REM exit 0，122 项 / 0 失败（连跑 5 次全绿）
```

单次真实运行（run8，`artifacts/20260920-122357-0f8c2771/`）：

```
matchId=r3b6-smoke-a14ddce5  dsId=r3b6-smoke-a14ddce5-ds-1  dsPid=69596
controlPort=9802（系统临时）  dsUdpPort=40860（随机高位段 40860-40879，非 7777/7778/7800）
osExitCode=0  osExitObservations=1  exitedTerminal=True  sessionReleased=True
lobbyTerminalReason=结果已本地受理，且对端经认证显式 Exited(0)（收到 Exited 消息）
midRunPortProbeError=SocketException（端口被真实 DS 持有）  postExitPortProbeError=<ok>（已归还）
checks=122 passed=122 failed=0
```

启动参数（`launch-args.txt`，只有引导文件路径，**无票据/密钥**）：

```
"-batchmode" "-nographics" "-server" "-dsid" "…-ds1" "-matchid" "r3b6-smoke-a14ddce5"
"-bootstrap" "…\artifacts\…\bootstrap\r3b6-smoke-a14ddce5\bootstrap-1.bin"
"-control" "127.0.0.1:9802" "-port" "40860" "-server-smoke" "-logFile" "…\hyldds-1-….log"
```

真实 Unity DS 日志关键证据（`hyldds-1-r3b6-smoke-a14ddce5-ds-1.log`）：

```
Initialize engine version: 2019.4.8f1 (60781d942082)      ← 真实 Unity 2019.4
Forcing GfxDevice: Null / Renderer: Null Device            ← 真无头（编译期 EnableHeadlessMode）
[PMDsHost] 检测到 -bootstrap，转入 R3-B 新链宿主（旧 UDP 诊断路由不启动）
[PMDsSessionHost] ===== R3-B 新链 DS 就绪 =====
[PMDsSessionHost] … epoch=1 hash=0x7B986DB4 digest=0x52334201
[PMDsSessionHost] UDP boundPort=40860 名册人数=2 控制通道=127.0.0.1:9802 serverSmoke=on
[PMDsSessionHost] 固定测试碰撞场景已建立并校验：地板/墙 BoxCollider enabled+active
[PMDsSessionHost] PMDsLobbyAgent: 已上报 Ready：boundPort=40860 sceneReady=True digest=0x52334201
[PMDsSessionHost] 玩家副本已创建 uid=301 playerId=1 netId=1 名册进度=1/2
[PMDsSessionHost] 玩家副本已创建 uid=302 playerId=2 netId=2 名册进度=2/2
[PMDsSessionHost] smoke 验收：名册 2 人全部完成探针，已提交 smoke 结果（summary=14B）
[PMDsSessionHost] PMDsLobbyAgent: 已上报结果 ResultId=4294967297 winner=0 summary=14B
[PMDsSessionHost] PMDsLobbyAgent: 收到匹配的 ResultAck(4294967297)，结果确认完成，准备退出
[PMDsHost] R3-B 新链请求退出，exitCode=0
[PMDsHost] 开始关闭 R3-B 新链宿主，reason=OnApplicationQuit 已存活 0.34 秒
```

**DS 单局真实寿命 ≈ 0.34 秒**（从 R3-B 宿主就绪到 OnApplicationQuit）：本机 loopback + ≤50ms 泵下，Ready→offer→两客户端入局→复制→探针/回声→smoke→ResultAck→退出 全在亚秒级完成。
时间线（`timeline.txt`）：`SessionStarted(+0) → ResultAccepted(+537ms) → SessionReleased(+654ms)`。

## 7. 观察项（**未修改生产代码**；精确文件 + 最小修复建议，由主 Agent 决定是否处理）

1. **协调器终态收尾后 `Dispose` 掉进程句柄，事后读不到真实 OS 退出码。**
   落点：`Server/DS/PMDsCoordinator.cs` → `ReleaseResources()` 内 `_process.Dispose()`（约 2362–2372 行）。
   后果：任何在释放之后再问 `IPMDsProcess.IsRunning` / `TryGetExitCode` 的宿主或门禁，得到的是「对象已释放」而不是「进程还在/退出码」，
   容易被误读（`IsRunning` 会静默返回 false，`TryGetExitCode` 静默返回 false）。本工具第一版即踩到：S102「进程已退出」曾以「对象被释放」为真而通过，
   退出码读不到。**本轮修复方式在测试侧**：启动瞬间订阅真实 `Process.Exited` 事件，在事件里抓退出码。
   最小修复建议（若要）：`ReleaseResources` 之前把 OS 退出码快照进会话计数器/终态事件（或在 `PMDsCoordinator` 暴露只读 `ProcessExitCode`）。
2. **控制帧与连接关闭落在同一次 Pump 时，host 会输出「已断开 → 已绑定」成对日志。**
   落点：`Server/DS/PMDsLobbyHost.cs` → `PumpOnce()`（`DrainConnectionEvents` 在 `DrainInbound` 之前，约 635–645 行）+ `HandleInbound` 的
   `session.ControlConnectionId == 0` 重绑分支（约 1004–1014 行）。
   证据：`PMDsControlListener.cs` 明示 `ConnectionId`（监听器内单调递增，**不复用**），而重绑日志的 id 与断开日志相同（`id=1`），
   因此可确认**不是第二条连接**，而是「同一次 Pump 里先处理了 Closed、随后又把该连接已入队的认证帧派发掉」。
   功能影响：无（该帧正是认证过的 `Exited`，会话随即进入终态 `Exited`；S40/S41 证明 `MacFailed=0`、`Malformed=0`，没有被冒用）。
   最小修复建议（若要）：把 `DrainInbound` 提到 `DrainConnectionEvents` 之前，或对「已上报 Closed 的连接 id」的残留入站帧直接丢弃。
3. **DS 控制代理的「Ready 后零回应」看门狗在长局场景下会自判失败。**
   落点：`Client/Assets/Scripts/Server/Boot/PMDsLobbyAgent.cs`（`StartupReadyTimeoutMs = 30000`，条件 `_readySent && FramesReceived == 0 && now-_readySentAtMs >= 30000` → `Fail(...)`，约 313–320 行）。
   后果：正常玩法若在 Ready 后 30 秒内 Lobby 不下行**任何**控制帧（当前协议只有 `ResultAck` 会下行，而它只在结果被受理后发出），
   DS 会以 exit 1 自己退出。本 smoke 路径（`-server-smoke` 亚秒出结果）不触发；真实长局（R4–R6）会触发。
   最小修复建议（若要）：让 Lobby 对 Ready 回一个 ack/heartbeat 下行帧，或把该看门狗改成「自身心跳发送成功 + Lobby 心跳存活」而非「必须有下行帧」。
   另有环境观察：run1（工具尚未订阅 `PlayerReplicated`、因此从未发探针）真实复现了这条看门狗 —— DS 日志 `控制通道失败：发出 Ready 后 30000ms 内未收到 Lobby 任何回应` → `exitCode=1`，
   说明该失败路径**在真实进程里确实是可观测、可诊断的**（不是文档空谈）。

## 8. 仍未闭合 / 待 UI 与跨机项（诚实口径）

1. **T42 仍 PENDING**：本轮只证明 DS 侧真实进程 + 真实碰撞场景 + 真实控制/UDP 闭环；**客户端真实 UnityUI 侧**（`UIMatchingPanel` 的 `PMDS1:` 严格解码分支、
   `PMClientSessionHost` 建可见测试场景、关闭旧 UDP socket、真实客户端 `Application.Quit`）**未在真实 Unity 编辑器/包中跑过**。
2. **客户端非 UnityUI 已明确**：本工具的两个客户端是纯 C# 测试宿主（无 GameObject/无 UI/无渲染），其结论只覆盖「协议/网络接线」，不代表 UI 表现。
3. **跨机未验**：全部为 loopback（控制 TCP 首版仅 loopback；UDP 也走本机）。跨机 MTU/NAT/丢包重排仍属待办。
4. **长局/弱网未验**：无弱网注入、无长时间运行；观察项 3 与 keepalive 长跑表现需在真实玩法阶段复验。
5. **票据过期/重放压力未验**：真实链路只做了 1 局、2 uid 的正常路径（票据拒绝/重放/换端点已由 `PMUdpAdmissionTest` 360/0 覆盖，但不是在真实 Unity DS 进程上）。
6. **高频重复压测有限**：连跑 5 次全绿，未做数十次以上重复与端口段压力。
7. **未构建 Unity**：本轮**未**打开编辑器、未抢锁、未重新构建 HyldDS；只对既有产物做 mtime/size/sha256/符号预检（S1–S8）后才启动，**不存在用旧 DS 冒充更新**。
8. **验证时点绑定（与 R3-B5 报告同口径）**：本报告全部绿色结果对应**我实测时的工作树**（`2026-09-20 12:23–12:27`）。
   本工具 build 时间 `12:23:47`，晚于 `Client/Assets/Scripts/PMNet/**` 的最后一次写入（`PMTransport.cs` = `12:01:06`），
   因此编译进去的就是当前源码，不存在「源码已变而产物还旧」的错位；但 `PMNet/**` 若继续变动需重跑本门禁。
   同目录下 `Docs/plans/net-architecture-migration.md` / `net-r3-control-contract.md` 的 mtime `12:09:17`、`r3b-verification.log` 的 `12:07:41`
   均为**本轮之前**的并行工作所写，**不是本任务改动**（本任务全部写入都在 `12:20` 之后，且仅限 §3 列出的 3 个新增文件与 `artifacts/**`）。

## 9. 明确不做

未改任何生产源码与现有门禁；未动主计划/契约；未启动旧服务（7777/7778）与旧 DS；未打开 Unity 编辑器；未抢占用户端口或进程；
未 `git`/`svn` 写操作；未递归委派；未新增 JSON 协议；未伪造 `BattleReview`；未用注入消息绕开 codec 或真实 TCP/UDP 主体。
`finally` 仅回收本工具自己启动的 DS 进程与其临时端口（实测运行后无残留 `HyldDS` 进程、无残留临时目录）。
