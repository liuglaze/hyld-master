# 旧 DS 无 bootstrap 诊断分支退役执行报告（C 段：PMDsHost 确定性重写 + 旧路由层删除）

日期：2026-09-21
执行方式：Pi CLI Delegation（comprehensive 后端；本地直接执行，**未递归委派**）
范围文档：`AGENTS.md` → `Client/Assets/AGENTS.md` → `Docs/plans/net-legacy-retirement-contract.md`（全文）→ `Docs/plans/_legacy_server_removal_survey.md`（§五 PMDsHost 部分）→ `skills/windows-shell-compat/SKILL.md`
任务性质：删除「无 `-bootstrap` 的裸 UDP 诊断分支」（运行入口 + 旧路由实现 + 只测它的工具），并把 `PMDsHost` **确定性重写**为只走新链的轻量 wrapper；不启动 Unity、不提交、不递归委派。

---

## 摘要（≤1300 字）

旧分支判据：`PMDsHost.Initialize` 在 `options.BootstrapPath` 非空时转 `PMDsSessionHost`，否则绑裸 UDP 并起 `PMUdpRouter`+`PMSingleBattleRegistry` 诊断路由。该分支是早期 P3「证明能收包」的脚手架；唯一生产调用方 `PMDsLobbyHost.BuildFinalArguments` 恒带 `-bootstrap`（`:1610`），故生产不可达。主侧按实码裁决：`PMUdpRouter` 属该诊断路径（新会话用 `PMUdpSessionEndpoint`），撤销客户端调查「须保留」的建议，随旧 DS 分支删除。

**删前 → 删后**

- `Boot/PMDsHost.cs` **644 → 195 行**：删裸 socket/接收线程、旧路由、空 tick/心跳、诊断 `MainPack` handler、跨线程日志队列；改为只接受 `-bootstrap` 的 wrapper（缺 bootstrap 或启动失败即 `Quit(1)` 且不监听端口；每帧唯一 `Pump`；退出/`OnDestroy`/`OnApplicationQuit` 幂等清理；`Instance` 收尾清）。
- 删 `Server/Net/PMUdpRouter.cs`(845)、`PMSingleBattleRegistry.cs`(150) 及两个 `.meta`；删只测旧路的 `Tools/{PMUdpRouterCheck,PMUdpRouterTest,PMDsProbe}` 源工程（历史 bin/obj 按契约保留）。
- `Editor/PMDsBuild.cs` 的 `run_ds` **单形态化**：`-bootstrap` 必需，缺则 `exit /b 1` 拒绝并说明「必须由 Lobby 编排」；删「旧形态」段与 `goto newchain`。
- `PMUnityGlueCheck.csproj` 去 `Server/Net/**` include；**新增** `Tools/PMDsHostCheck`（有限 HostStubs + 真实 `PMDsHost.cs`，行为门 + 结构门）。

**未动**：`PMNetLaunchOptions`、`PMNetBootstrap`、`PMDsSessionHost`、资产、proto、其它组；`PMDsHost.cs.meta` 无 diff（GUID 不变）。

**测试（无 Unity）**：`PMUnityGlueCheck`/`PMClientCheck`/`PMBattleContentBuildCheck` 均 0 错 0 警；`PMDsHostCheck` 68 通过/0 失败；负向注入临时副本 → 结构门报 5 处 FAIL（非空转）。

**边界（未做）**：未启动 Unity、未重生成 gitignored 的 `HyldDS/run_ds.bat`；`net-architecture-migration.md` 仍引用已删门禁的行不在写入面（交主侧收口）；实机 **PENDING_USER**。

---

## 1. 边界与授权

**硬写入面（仅此 16 个路径）**：

- `Client/Assets/Scripts/Server/Boot/PMDsHost.cs`（重写）
- `Client/Assets/Scripts/Server/Net/PMUdpRouter.cs`、`PMUdpRouter.cs.meta`（删）
- `Client/Assets/Scripts/Server/Net/PMSingleBattleRegistry.cs`、`PMSingleBattleRegistry.cs.meta`（删）
- `Client/Assets/Editor/PMDsBuild.cs`（改 `run_ds` 生成器）
- `Tools/PMUdpRouterCheck/PMUdpRouterCheck.csproj`（删）
- `Tools/PMUdpRouterTest/PMUdpRouterTest.csproj`、`Program.cs`（删）
- `Tools/PMDsProbe/PMDsProbe.csproj`、`Program.cs`（删）
- `Tools/PMUnityGlueCheck/PMUnityGlueCheck.csproj`（去 `Server/Net/**` include）
- `Tools/PMDsHostCheck/PMDsHostCheck.csproj`、`Program.cs`、`HostStubs.cs`（新增）
- `Docs/plans/_legacy_ds_removal_report.md`（本报告）

**明确未触碰**：`PMNetLaunchOptions.cs`、`PMNetBootstrap.cs`、`PMDsSessionHost.cs`、`PMClientSessionHost.cs`、`Server/DS/**`、proto 与生成物、任何资产/prefab/scene、其它组在飞文件、`Tools/PMUnityGlueCheck/UnityStubs.cs`（客户端组拥有）、任何 `.meta`（除随源文件删除的两个）。未做父目录/祖先目录/相邻文件推断；未创建边界外文件。

历史 `bin/obj/log` 按契约允许保留不动：`Tools/PMUdpRouterCheck/{bin,obj}`、`Tools/PMUdpRouterTest/{bin,obj}`、`Tools/PMDsProbe/{bin,obj}` 仍在（目录内已无源文件）。

---

## 2. 写前事实复核（证据）

1. **旧分支结构**（写前读真实 `PMDsHost.cs`）：`Initialize` → `if (!string.IsNullOrEmpty(options.BootstrapPath)) { StartNewChainSessionHost(options); return; }`，否则 `TryBindSocket(options)` → `InitializeRouter()`（`new PMSingleBattleRegistry()` + `new PMUdpRouter(_socket, _registry, EnqueueLog×3)`）→ `LogReady`。
2. **唯一生产调用方恒带 `-bootstrap`**：`Server/DS/PMDsLobbyHost.cs:1610 BuildFinalArguments` 必带 `-bootstrap`（方法自 `:1600` 起）；故无 bootstrap 分支在生产不可达（`_legacy_server_removal_survey.md` §五 推断 A，置信高）。
3. **旧路由零外部消费者**：`rg --no-ignore` 实时扫描全仓（排除 `bin/obj/Library/HyldDS/Docs/Temp/artifacts`）后，`PMUdpRouter`/`PMSingleBattleRegistry` 的活代码引用只有 `PMDsHost.cs`、`PMUdpRouterTest/Program.cs` —— 两者都在本任务删除/重写集合内。
4. **门禁覆盖面不因删除而消失**：`PMDsHost.cs` 的编译面由 `PMUnityGlueCheck`（真实 PMNet + 真实 `Loging.cs` + 真实 `PMDsSessionHost.cs`）与 `PMClientCheck`（`Server/**` 全量）继续覆盖；`PMDsBuild.cs` 由 `PMBattleContentBuildCheck`（真实 Unity 2019.4 DLL）覆盖。
5. **`HYLDDebug` API**：真实 `Loging.cs` 提供 `Log/LogWarning/LogError(object, params object[])` 与 `FlushTrace()` —— 新 wrapper 只使用这四个（无 `UE_LOG` 类调用，符合项目日志宏约定口径）。

---

## 3. 改动明细

### 3.1 `Boot/PMDsHost.cs`：644 行 → 195 行（确定性重写）

**删除（旧分支全部脚手架）**

| 项 | 说明 |
|---|---|
| 裸 UDP 绑定 / 接收 | `TryBindSocket`、`BeginReceive`、`OnReceive`、`TrackRemote`、`_socket`/`_receiveEndPoint`/`_receiveBuffer`、`BoundPort`、`DescribeLocalAddresses` |
| 旧路由接线 | `InitializeRouter`、`_registry`、`_router`、`HandleDiagnosticBattlePacket`、`_battlePacketsDispatched`、`_lastBattleActionCode`、`_battleNullPackets` 等诊断计数 |
| 空 tick / 心跳 | `DriveTick`、`LogHeartbeat`、`_tickAccumulator`、`_tickInterval`、`_tickCount`、`_nextHeartbeatTime`、`MaxTicksPerUpdate`、`MaxPacketsPerUpdate` |
| 就绪/收包日志 | `LogReady`（含「UDP 路由层已就绪…P3'-3 由权威战斗仿真取代」段） |
| 跨线程日志队列 | `_pendingLogs`/`_logLock`/`EnqueueLog`/`DrainPendingLogs`（旧队列是为**接收线程**跨线程入队而存在；无 socket ⇒ 无后台线程，队列成为死代码） |
| P2' 占位回包 | `[Obsolete(..., true)] ScaffoldPongPayload`、`MaxTrackedRemotes`、`_knownRemotes`、`_receivedDatagrams`、`_receiveErrors`、`_receiveBatches` |

**保留 / 新增（新链语义不变）**

- `static PMDsHost Start(PMNetLaunchOptions)`：`new GameObject("[PMDsHost]")` → `DontDestroyOnLoad` → `AddComponent` → 登记 `Instance` → `Initialize`（与 HEAD 一致）。
- `Initialize`：`Application.runInBackground = true` → `options == null` → `Fail`；`string.IsNullOrEmpty(options.BootstrapPath)` → `Fail`（**不创建任何监听面**）；否则 `PMDsSessionHost.Start(options, out error)` 成功才挂 `ExitRequested`。
- `Fail(reason)`：`_exitRequested = true` → `HYLDDebug.LogError` → `FlushTrace` → `Application.Quit(1)`。
- `OnSessionExitRequested(code)`：幂等（`_exitRequested` 门）→ 日志 → `FlushTrace` → `Application.Quit(code)`。
- `Update()`：`_exitRequested || _shutdown` → 早退；`_sessionHost == null` → 早退；否则 `_sessionHost.Pump()`（**唯一驱动点**，不二次 `bridge.Update`，符合契约 §7.2）。
- `OnApplicationQuit` / `OnDestroy` → `Shutdown(reason)`：`_shutdown` 幂等门 → 摘 `ExitRequested` → `_sessionHost.Dispose()` → `Instance` 收尾清 → 日志 + `FlushTrace`。
- 新增只读属性 `SessionHost`（诊断/门禁观察用；移除旧 `BoundPort`）。

### 3.2 `Editor/PMDsBuild.cs`：`run_ds.bat` 模板单形态化（330 → 325 行）

- 变量序改为 `BOOTSTRAP=%~1 / DSID=%~2 / MATCHID=%~3 / PORT=%~4 / CONTROL=%~5`，`smoke` 为 `%~6`。
- 启动前 `if "%BOOTSTRAP%"=="" goto noboot`；`:noboot` 分支：`echo [run_ds] 缺少 -bootstrap：HyldDS 必须由 Lobby 编排拉起，拒绝裸 -port 启动（旧诊断路由已删除）。 1>&2` + `exit /b 1`。
- 删除「REM == 旧形态 …」段、旧形态命令行（只有 `-dsid/-matchid/-port/-lobby` 就能起来）与 `goto newchain` 跳转；保留唯一命令行（带 `-bootstrap` + `-control` + `-server-smoke` 开关），结尾 `exit /b %ERRORLEVEL%` 透传退出码。
- 注释明确：DS **必须**由 Lobby 用 `-bootstrap` 编排拉起；`-port` 由 Lobby 独占分配、DS 不监听其它端口。
- 方法文档注释同步改写（原文「同时描述两种启动形态」→ 单形态 + 拒绝裸端口）。

### 3.3 `Tools/PMUnityGlueCheck/PMUnityGlueCheck.csproj`

- 删除 `<!-- P3'-1 新增的战斗 UDP 路由层（同时由 PMUdpRouterCheck 单独门禁） -->` 与 `<Compile Include="..\..\Client\Assets\Scripts\Server\Net\**\*.cs" LinkBase="Client\Net" />`。
- 头注释里 `PMDsHost.cs` 说明由「socket / tick / 心跳 / 路由接线」改为「新链会话 wrapper：bootstrap 校验 / Pump / 幂等清理」，并删掉 `Server/Net/*.cs` 一行。
- **未改** `UnityStubs.cs`（客户端组拥有）。

### 3.4 删除文件清单（含删前行数）

```
Client/Assets/Scripts/Server/Net/PMUdpRouter.cs                845 行  (+ .meta)
Client/Assets/Scripts/Server/Net/PMSingleBattleRegistry.cs     150 行  (+ .meta)
Tools/PMUdpRouterCheck/PMUdpRouterCheck.csproj                 （glob 覆盖已删目录，门禁退化）
Tools/PMUdpRouterTest/PMUdpRouterTest.csproj
Tools/PMUdpRouterTest/Program.cs                               609 行
Tools/PMDsProbe/PMDsProbe.csproj
Tools/PMDsProbe/Program.cs                                     188 行
```

---

## 4. 新增门禁 `Tools/PMDsHostCheck/`（659 行：csproj 53 + HostStubs 197 + Program 409）

**做法**：`EnableDefaultCompileItems=false`，只编入真实 `Client/Assets/Scripts/Server/Boot/PMDsHost.cs` +
`HostStubs.cs`（有限替身：`UnityEngine.{Object,Component,Behaviour,MonoBehaviour,GameObject,Application,Time}`、
`Logging.HYLDDebug`、`PMNet.PMNetLaunchOptions`、**受控** `PMNet.Unity.PMDsSessionHost`）。
刻意**不引** Google.Protobuf / 旧路由层 —— 门禁自身即证明 wrapper 无旧协议依赖。`net8.0 + LangVersion 7.3`（与 Unity 2019.4 编译上限一致）。私有 `Update/OnDestroy/OnApplicationQuit` 经反射驱动（不改产品代码加测试钩子）。

**A. 行为（受控替身，断言 wrapper 调用次序与次数）**

- `options=null` / `BootstrapPath=null` / `BootstrapPath=""` → `Application.Quit(1)` 恰一次、`PMDsSessionHost.StartCalls == 0`（**无第二监听面**）、`SessionHost == null`、`OnDestroy` 后 `Instance == null`。
- 有效 bootstrap → `StartCalls == 1`、已挂 `ExitRequested`；连续两次 `Update()` → `PumpCalls == 2`（每帧恰一次）。
- 会话 `ExitRequested(7)` → `Quit(7)` 恰一次；此后 `Update()` 不再 `Pump`；重复 `ExitRequested(9)` 不再 `Quit`。
- `OnDestroy` → `Dispose` 恰一次；再 `OnApplicationQuit` + `OnDestroy` → 仍恰一次（幂等）。
- 故障注入（`FailNextStart`）→ `Quit(1)`、不持有会话、`Update` 不重试启动、清理幂等不抛。

**B. 结构门（token 断言，注释会被剥离后再扫代码，避免把「说明为什么删」的文档注释当引用）**

- 9 个退役路径 `File.Exists == false`（2 个 `.cs` + 2 个 `.meta` + 3 个 csproj + 2 个 Program.cs）。
- `PMDsHost.cs` 代码不含：`System.Net`、`Socket`、`PMUdpRouter`、`PMSingleBattleRegistry`、`SocketProto`、`MainPack`、`System.Threading`、`Thread`、`InitializeRouter`、`HandleDiagnosticBattlePacket`、`DriveTick`、`LogHeartbeat`、`TryBindSocket`、`OnReceive`、`ScaffoldPongPayload`、`BoundPort`、`Pong`。
- `PMDsHost.cs` 仍含：`PMDsSessionHost`、`BootstrapPath`、`.Pump()`、`Application.Quit(1)`、`DontDestroyOnLoad`、`Instance`。
- `PMDsBuild.cs`：含 `-bootstrap`/`:noboot`/`exit /b 1`/`Lobby`；**不含** `旧形态`、`goto newchain`。
- `PMUnityGlueCheck.csproj`：不含 `Server\Net\**`。

**已知限制（写在 csproj/文件头）**：替身只保证形状、不保证语义；**不**证明真实 `PMDsSessionHost` 装配与 Unity 运行期行为（分别由 `PMUnityGlueCheck`/`PMClientCheck` 编真实源码、与用户实机承担）。

---

## 5. 测试与证据（本地执行，**未启动 Unity**）

| # | 命令 | 结果 |
|---|---|---|
| 1 | `dotnet build Tools/PMUnityGlueCheck -c Release --no-incremental` | exit 0，**0 error / 0 warning**（真实 `PMDsHost.cs` + `PMNet/**` + `Loging.cs` + 真实 `PMDsSessionHost.cs`） |
| 2 | `dotnet build Tools/PMClientCheck -c Release` | exit 0，**0 error / 0 warning**（`Server/**` 全量 glob，含 `PMDsHost.cs`） |
| 3 | `dotnet build Tools/PMBattleContentBuildCheck -c Release --no-incremental` | exit 0，**0 error / 0 warning**（真实 Unity 2019.4 托管 DLL；覆盖改动后的 `PMDsBuild.cs`） |
| 4 | `dotnet Tools/PMDsHostCheck/bin/Release/net8.0/PMDsHostCheck.dll` | exit 0，**通过 68 / 失败 0** |
| 5 | 负向注入：把 `System.Net.Sockets.Socket` 引用与退役文件塞回**临时副本**（不改仓库），再跑 4 | 结构门正确报 **5 处 FAIL**（`PMUdpRouter.cs` 复活、`System.Net`、`Socket`、`旧形态`、`goto newchain`）→ 门禁非空转 |

补充证据：

- 删除后实时 `rg --no-ignore` 全仓扫描：`PMUdpRouter`/`PMSingleBattleRegistry` 的活代码命中**仅剩**本报告新增门禁自身的断言字符串，与 `PMDsSessionHost.cs:446` 的文档注释提及（非 cref、非调用）。
- `git status`：写入面之外无新增改动；`PMDsHost.cs.meta` 无 diff（GUID 保持 `6232c3fb4fec4b48acfbda1181316f3b`）。
- 期间 `PMClientCheck` 曾出现报错，全部落在另一组在飞的旧客户端脚本（`OldScripts/TouchLogic|Toolbox|PlayerLogic|HYLDPlayerController|移动型大招` 引用已删的 `CommandManger`/`BattleManger`/`BattleData`），**与本任务写入面无关**（`PMDsHost.cs`/`Net/**` 从未出现在诊断里）；其 `PMClientSessionHost.cs` mtime 22:32 **晚于**本任务编辑（22:28/22:29），确认是并发改动导致；该组补齐后第 2 项转绿。按任务要求**未**为绕开该暂态而创建任何空旧类。

---

## 6. 残留与风险（交主侧收口）

1. **`Docs/plans/net-architecture-migration.md` 仍引用已删门禁**（不在本任务写入面）：`:55-56` 的 `PMUdpRouterCheck`/`PMUdpRouterTest` 验收命令；`:1487-1488` 现状表两行；`:1506/:1525/:1542` 的 `PMDsProbe` 四步探针叙述；`:1708/:1808` 门禁结果表；`:1830` 场景测试引用。建议由主侧统一改为「已删除」并补 `PMDsHostCheck` 一行。
2. **`PMDsSessionHost.cs:446`** 文档注释仍写「旧诊断路径（PMUdpRouter）」——**非** `<see cref>`、不影响编译，但语义已过期；该文件不在本任务写入面，交 Boot 归属侧顺手改为「旧诊断路径已退役」。
3. **`Client/Assets/Scripts/Server/Net.meta` 与空目录 `Net/`**：源文件与 `.meta` 删除后该目录为空。未删 `Net.meta`（不在写入面）。Unity 下次导入会保留空文件夹+meta，无功能影响；如需彻底清理由主侧决定。
4. **`HyldDS/run_ds.bat` 未重生成**：`HyldDS/` 为 gitignored 构建产物，本任务**未**声称已新构建；用户下次真实 Build 会写出新的单形态模板。现有磁盘上的旧模板（若还在）仍可能含旧形态分支。
5. **实机未验**：PMDS1 匹配 → DS 拉起 → `-bootstrap` 新链 → `server-smoke`（新链诊断，非旧 `PMDsProbe`）全链仍 **PENDING_USER**；`PMR3IntegrationTest`/`PMDsControlTest`/`PMDsLobbyTest` 等新链自动门禁未在本任务复跑（超出写入面，且会与在飞的其它组竞争共享 `obj/`）。
6. **历史工具产物**：`Tools/{PMUdpRouterCheck,PMUdpRouterTest,PMDsProbe}/{bin,obj}` 与历史日志保留（契约允许）；若主侧要求彻底清目录需另授权。

---

## 7. 已检查范围 / 未检查

**完整读**：`AGENTS.md`、`Client/Assets/AGENTS.md`、`Docs/plans/net-legacy-retirement-contract.md`、`Docs/plans/_legacy_server_removal_survey.md`（§五/§六/§九）、`skills/windows-shell-compat/SKILL.md`、`Client/Assets/Scripts/Server/Boot/PMDsHost.cs`、`PMNetBootstrap.cs`、`PMDsSessionHost.cs`（`Start`/`Pump`/`Dispose`/日志出口等关键段）、`PMNetLaunchOptions.cs`、`Editor/PMDsBuild.cs`、`Tools/{PMUdpRouterCheck,PMUdpRouterTest,PMDsProbe}/*.csproj` 与两个 `Program.cs`、`Tools/PMUnityGlueCheck/PMUnityGlueCheck.csproj`、`Tools/PMClientCheck/PMClientCheck.csproj`、`Tools/PMBattleContentBuildCheck/PMBattleContentBuildCheck.csproj`、`Client/Assets/Scripts/Log/Loging.cs`（`HYLDDebug` API 面）、`Tools/PMUnityGlueCheck/UnityStubs.cs`（只读，确认可复用成员）。

**实时扫描（`rg --no-ignore`）**：`PMUdpRouter` / `PMSingleBattleRegistry` / `PMDsHost` / `PMDsProbe` / `PMUdpRouterCheck` / `PMUdpRouterTest` 的全仓引用面（含 `*.csproj`/`*.sln`/脚本），排除 `bin`/`obj`/`Library`/`HyldDS`/`Client/Temp`/`Tools/PMR3UnitySmoke/artifacts`/`Docs`。用真 `rg` 而非 FF：归零属**完备性判定**，FF 索引为 partial 语义且不覆盖 gitignored 路径。

**未检查（按非目标边界停止）**：Unity 实机加载与 Play 运行、`HyldDS` 真实构建、PMDS1 端到端匹配、`Server/**` 其它组在飞文件内部实现、proto 生成链、资产/prefab/scene、`Tools/PMR3UnitySmoke` 实机烟测执行。
