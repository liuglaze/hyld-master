# 旧链删除后运行入口正确性 —— 独立只读复核

范围：`D:/UGit/hyld-master`。任务类型 explore（只读）。本轮**未**修改任何源码/资产、未编译、未启动 Unity/服务端、未提交、未递归委派。
本文件是本任务唯一写入例外（任务指定报告落盘路径）。

必读文档（已完整读）：`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`（含 §14/§15）→ `Server/AGENTS.md` → `Docs/plans/net-legacy-retirement-contract.md`（全文）。
`_legacy_server_removal_report` / `_legacy_client_removal_report` / `_legacy_ds_removal_report` **只用于定位修改入口**，其 PASS 结论一律未采信，实际结论均以源码/资产/diff/扫描为准。

复核的精确问题：
1. 是否确实只有新 DS；missing config / bootstrap fail-closed；无 UDP 7777。
2. 删除旧 ClearSence/回放是否误删大厅注册请求或破坏 `Requests[n]` 索引。
3. TCP 断线回 main thread 会否抹掉新的合法结果 / 误跳不存在的 scene。
4. `PMDsHost` 异常在 Start/Pump/Dispose 是否逃逸，导致常驻无用进程。
5. 残留 `run_lobby.bat` / `run_ds` 旧 mode 提示是否有实际阻断。

---

## 已确认

### A. 服务端确实只剩新 DS，且无 UDP 7777（问题 1）

- `Server/Server/Server.cs` 构造只做两件事：TCP `7778` 监听（`_socket`/`ListenClientConnect`/`OnTimer`）+ 末尾 `InitializeDedicatedServerLobby()`。**没有任何 UDP socket 创建**。
- 完备性扫描：`rg --no-ignore -g 'Server/**/*.cs'` 对 `7777|UDPservePort|LZJUDP|SocketType.Dgram|UdpClient|ProtocolType.Udp` **零命中**；`Server/DS/*.cs` 对 `Udp|UDP|SocketType.Dgram` 亦零命中。
- `Server/Server/ServerConfig.cs`（11 行）只剩 `TCPservePort / MaxRoom3_3Number / MaxTeam3_3Number`，`UDPservePort` 与 `frameTime` 已删。
- `Server/Controller/Controllers.cs:541 StartFighting` 方法体**只有一条语句** `StartFightingDedicatedServer(server, matchResult);` —— 无选链判断、无 `else`、无旧 `TryBeginBattle`。`StartFightingDedicatedServer`（:559）在宿主 `null`/未 `IsRunning`、或名册缺任一已认证 `Client` 时**只记日志并 return**（`[PMDsMatch] DS 宿主未就绪，整局失败（已无旧链可回退）` / `新链开局失败：玩家X不在线…整局拒绝（不缩编）`），不生成第二个权威。
- `Server/Controller/ControllerManger.cs`：`ClearSenceController` 的实例化 / `_controllerDic.Add` / `_controllerNameDic.Add` / `RegisterAll` 形参 / `Register(RequestCode.ClearSence, …)` **全部不存在**；`RegisterAll` 只剩 User/Friend/FriendRoom/Matching/PingPong；未注册组合落 `[RPC][未注册] request=… action=…` 明示日志（不静默当旧链、不抛异常）。
- `Server/DS/PMDsLobbyHost.cs` `PMDsLobbyHostOptions.FromEnvironment()`（:119）：`options.Enabled = true;` 恒真；`HYLD_PMNET_DS` 只被读来置 `DeprecatedLegacySwitchIgnored/Value`（仅供启动日志「忽略旧开关」），**显式 `0` 也不能恢复旧链**；`NewChainEnabled` 已删。`IsLaunchConfigured`（:95）要求 `HYLD_PMNET_DS_EXE` / `_WORKDIR` / `_BOOTSTRAP_DIR` 非空且 `PortRangeFirst>0 && PortRangeLast>=PortRangeFirst`，三者默认均为 `string.Empty`（**不猜任何用户目录**）。
- `Server/Server/Client.cs`：`isImportantTcpPack` 只剩 `UpDateActiveFriendInfo|AddMatchingPlayer`（`BattleReview` 死条件已清）；`Close` 里只通知 `PMDsLobbyHost.Instance.NotifyClientDisconnected(UID)`，旧 `BattleManage.HandleClientDisconnect` 已无。
- 旧战斗组 8 文件（`Battle.cs`/`BattleController.*`/`BattleManage`/`BattleContext`/`ServerBullet`/`ServerVector3`/`ClientUdp`）已从编译面消失；`Server/**` 内对旧符号的命中全是注释（无活代码）。

**fail-closed 结论**：配置不全 ⇒ `Server.InitializeDedicatedServerLobby` 只 `return`（宿主不启动、`Instance` 为 null）⇒ 匹配经 `StartFightingDedicatedServer` 显式失败。不存在「静默回退旧链」的结构可能性（旧类型已编译期不存在）。

### B. 客户端旧链活引用归零（问题 1/2 前置）

`rg --no-ignore` 扫 `Client/Assets/**/*.cs`（排 generated `SocketProto.cs`）对 `BattleManger|BattleData|CommandManger|UDPSocketManger|BattleFrameHud|HYLDBulletManger|HYLDPlayerManger|HYLDCameraManger|HYLDBaoShiZhengBaManger|GameManger|PMUdpRouter|PMSingleBattleRegistry` 的命中**全部落在注释行**；`7777|ServiceUDPPort` 同样只剩注释（`ConstValue.cs:26` 为退役说明）。旧链无活调用。

### C. `Requests[n]` 索引**未被破坏**，大厅/匹配注册请求**未误删**（问题 2，重点结论）

- `UIStartMainPanel.Init` 追加顺序为 `UpdateName(0) / FindFriendsInfo(1) / FriendLogin(2) / FriendLogout(3) / ChangeHero(4) /【已删】BattleReview(5)`。被删项是**最后一个**，因此 `Requests[0..4]` 无位移；`ChangeHero()`（:124/:133）读的 `Requests[4]` 仍等于 `(User, ChangeHero)`，与 Init 一致。`git diff` 确认被删行紧跟在 `ChangeHero` 之后。
- `UIMatchingPanel.Init` 的 `Requests.Add(new BaseRequest(this, RequestCode.Matching, ActionCode.StartEnterBattle))` **未被本轮改动**（diff 只动了 `OnResponse` 的 `ReturnCode.Succeed` 分支）；同文件里 `AddMatchingPlayer` 那条在改造前就已是注释。
- 匹配发起入口仍在：`UIInvatingFriendPanel`（未被本轮修改）注册 `Requests.Add((Matching, AddMatchingPlayer))` / `(Matching, RemoveMatchingPlayer)`，并在收到 `AddMatchingPlayer` 时 `UIMatchingManager…Open(nameof(UIMatchingPanel))`。服务端入局通知为 `RequestCode.Matching + ActionCode.StartEnterBattle`（`Server.cs` `ServerLobbyClientGateway.TrySendEntryOffer`），与客户端注册的组合一致。
- `UISliderPanel` 不再注册 `(ClearSence, ClientSendClearSenceReady/AllClearSenceReady)`；其原 `Requests[0]`（`SendLoadOver`）与 `IsCanEnterBattle` 的**唯二**消费者 `ClearSenceManger.AsyncLoadScene` 已同步改成本地进度/放行，无悬空消费者。
- 资产侧无残留引用：`.unity/.prefab/.asset` 对 `AddBattleReview` / `GetBattleReview` / `SendLoadOver` / `IsCanEnterBattle` / `AllClearSenceReady` / `ClientSendClearSenceReady` / `InitBattleInfo` / `AddCommad_Move|Attack|SuperAttack` / `BeginGameOver` **均 0 命中**；`HYLDManger : Singleton<HYLDManger>`（非 MonoBehaviour）不可能作为 UnityEvent 目标。
- `HYLDManger.AddBattleReview/GetBattleReview` 删除后，`RequestManger` 的 `ActionCode.BattleReview` 特判分支也删除，落到 `_requestDic` 查询 → 未注册时打 `[不能找到对应的处理]` 日志（无异常放大）。

### D. 断线回主线程不会抹掉合法结果，场景存在（问题 3）

- `HYLDManger.CloseClient`：判断**在 `NetGlobal.Instance.AddAction(...)` 的主线程闭包内重新读取** `PMNet.Unity.PMClientSessionHost.IsActive` / `HasLastCombatResult`（外层局部变量只用于日志）——不存在「读旧值」窗口；已拿到可信终局 ⇒ **不 Stop**（保留只读终局 HUD），未终局才 `Stop()`。
- 跳转目标 `"HuangYeLuanDouStart"` 确实存在：`Client/Assets/HYLD1.0/Scenses/HuangYeLuanDouStart.unity`，且列在 `Client/ProjectSettings/EditorBuildSettings.asset:27`。**不是不存在场景**；该字符串亦非本轮新增。

### E. `PMDsHost` / `PMDsBuild` 源码侧 fail-closed 成立（问题 1/4 前置）

- `PMDsHost.Initialize`：`options == null` 或 `string.IsNullOrEmpty(options.BootstrapPath)` → `Fail(reason)` → `HYLDDebug.LogError` + `FlushTrace` + `Application.Quit(1)`，且**不创建任何监听面**（无裸 socket、无接收线程、无 tick）；`PMDsSessionHost.Start` 返回 null 亦同路 `Fail`。
- `Update()`：`_exitRequested || _shutdown` 早退；`_sessionHost == null` 早退；否则唯一驱动一次 `_sessionHost.Pump()`。
- `OnDestroy` / `OnApplicationQuit` → `Shutdown(reason)`：`_shutdown` 幂等门 + 先摘 `ExitRequested` 再 `Dispose()` + `Instance` 收尾清。
- `PMDsBuild.WriteLauncher`（:208–285）生成的 `run_ds.bat` 已单形态：`set BOOTSTRAP=%~1` + `if "%BOOTSTRAP%"=="" goto noboot` + 唯一一条带 `-bootstrap` 的命令行 + `exit /b %ERRORLEVEL%` + `:noboot` → 提示「必须由 Lobby 编排拉起，拒绝裸 -port 启动」+ `exit /b 1`。**无**「旧形态」段、**无** `goto newchain`。

---

## 高概率推断（依据与置信度）

1. **新链局内不存在第二套权威**（置信度高）。`PMDsSessionHost.cs:7` 明确「不加载旧 HYLDGame 场景：那片场景的 Awake 链会拉起 HYLDManger/BattleManger/UDPSocketManger」，且 `HYLDStaticValue.Awake` 的 `AddComponent<BattleManger>()`/`AddComponent<HYLDBaoShiZhengBaManger>()` 分支已删（只剩一行退役日志）。未见实机加载验证。
2. **`Pump` 内部异常逃逸概率偏低**（置信度中）。`PMDsSessionHost.Pump` 的每个阶段（`_endpoint.Pump`/`PumpMovement`/`PumpCombatOnce`/`PumpProjectiles`/`FlushCombatOnce`/`TryStartCombatMatch`/`CheckCombatStartDeadline`/`SubmitCombatResultIfReady`/`CheckPlayerDrops`/`CheckSmoke`/`_lobby.Pump`/`LogHeartbeat`）内部均有零散 `catch`，并且阶段后紧跟 `if (_faulted) return;`；整体缺的是**顶层**兜底，而非「已知会抛」。故 B2 定位为「缺兜底的潜在风险」。
3. **B1 的真实触发面取决于部署配置**（置信度中）。陈旧 `HyldDS` 产物是否就是 `HYLD_PMNET_DS_EXE` 指向的文件，需看部署 env；若是，则 Lobby 路径因恒带 `-bootstrap` 不受影响（旧二进制在带 bootstrap 时同样走新链分支），风险仅在「人工/旧自动化按 `run_ds.bat` 无 bootstrap 启动」。

---

## 无法确定（缺少证据）

- **未编译、未启动 Unity、未启动服务端**：T-L1…T-L6 的实际门禁结果、`Tools/PMLegacyRetirementTest` 的 G2（词法剥离注释/字符串后不得出现旧类型与硬编码 7777）是否真绿、以及「PMDS1 匹配 → DS 拉起 → `-bootstrap` → 结果回收」端到端，本轮**均未验证**；只核了静态源码/资产/diff/产物与扫描。
- `ExitMathcing` 的确切交互（是否 Button、有无 UnityEvent 目标、是否只是视觉遮罩）未做资产级展开，故 B4 严重性定为「低」而非「中」。
- 陈旧 DS 产物与部署 env 的对应关系（见高概率推断 3）。

---

## 真 bug / 缺陷（点名 + 严重性 + 最小修复）

### B1（中高）磁盘上的 DS 构建产物是改造前的旧产物，仍含已退役的裸 UDP 诊断路由与旧形态 `run_ds.bat`

- 证据：`HyldDS/HyldDS_Data/Managed/Assembly-CSharp.dll` mtime `2026-09-21 14:35`，**早于** `Client/Assets/Scripts/Server/Boot/PMDsHost.cs`（22:28）与 `Client/Assets/Editor/PMDsBuild.cs`（22:29）。其字节串仍含 `PMUdpRouter`(2 次)、`PMSingleBattleRegistry`、`ScaffoldPongPayload`、`PMDS-PONG`、`DiagnosticBattle`，而**不含**新文案「缺少 -bootstrap」→ 该二进制就是改造前的 `PMDsHost`（带旧诊断分支）。
- `HyldDS/run_ds.bat`（14:35，gitignored 产物）仍是旧模板：`set BOOTSTRAP=%~4`、`if not "%BOOTSTRAP%"=="" goto newchain`、旧形态段 `HyldDS.exe -batchmode -nographics -server -dsid … -port %PORT% -lobby …`（**不带 `-bootstrap`**），且注释仍自称「旧形态（不带 -bootstrap，走 P3' 诊断路由）」。
- 影响：按该 bat 的「旧形态」用法运行 → 用**旧二进制**真正拉起诊断路由、绑 UDP 端口并空转（旧形态无退出条件）→ **常驻无用进程**，完全绕过新 `PMDsHost` 的 fail-closed。Lobby 编排路径不受影响（`BuildFinalArguments` 恒带 `-bootstrap`）。
- 门禁盲区：`Tools/PMDsHostCheck` 的结构门只读 `Client/Assets/Editor/PMDsBuild.cs` **源码文本**与 `PMDsHost.cs` **源码文本**，**不检查**已生成的 `run_ds.bat`，也不检查已编译 DLL → 门绿但产物陈旧。
- 最小修复：实机前**重建 HyldDS**（会重写 `run_ds.bat` 为单形态并刷新 DLL），或在联调清单显式写「必须先删除/覆盖 `HyldDS/` 旧产物」。不建议为扫用户构建产物而扩大门禁（契约允许保留历史产物）。

### B2（中）`PMDsHost` 对 `Pump` 未做顶层异常兜底，异常逃逸会形成短窗口「常驻但无用」DS 进程

- 证据：`PMDsSessionHost.Pump()`（:1252 起）**没有整体 try/catch**，只有阶段内部的零散 catch；`Physics.SyncTransforms()`、`DateTimeOffset.UtcNow`、`Time.realtimeSinceStartup` 等无 catch；`PMDsHost.Update()` 同样不包 try/catch。
- 影响链：意外异常 → 逃出 `Update` → Unity 记日志后继续帧（不退出进程）→ `_exitRequested` 永不置位 → `Application.Quit` 不触发；同时 `_lobby.Pump` 不再执行 ⇒ 心跳停 ⇒ Lobby 侧 `HeartbeatTimeout = 15s` 触发强杀（`PMDsProcess.Kill()` 用 `Process.Kill(true)` + `WaitForExit(2000ms)`）。终态**有界**（约 15–17s + 日志刷屏），**并非永久**常驻，但窗口期内是一个无心跳、无结果的孤儿进程。
- 最小修复（< 6 行、不动新链语义）：
  ```csharp
  // PMDsHost.Update()
  try { _sessionHost.Pump(); }
  catch (Exception ex) { Fail("Pump 异常：" + ex.GetType().Name); }   // Fail 已含 LogError+FlushTrace+Quit(1)
  ```
  `Shutdown` 里对 `_sessionHost.Dispose()` 亦可加同构兜底。

### B3（中，部署口径）`Server/run_lobby.bat` 未设置 DS 三个必需环境变量 ⇒ 按现成脚本启动必然「匹配显式失败」

- 证据：除 `PMDsLobbyHost.cs` 自身与文档外，全仓**无任何脚本/文件**设置 `HYLD_PMNET_DS_EXE` / `_WORKDIR` / `_BOOTSTRAP_DIR`；`Server/run_lobby.bat` 全文只有 `cd /d D:\UGit\hyld-master\Server` + `dotnet bin\Debug\net8.0\Server.dll`。三项默认空串 ⇒ `IsLaunchConfigured == false` ⇒ 宿主不启动 ⇒ 匹配失败，仅日志可见。
- 性质：这是契约要求的 fail-closed（「不瞎猜用户目录」），**不是代码 bug**；但按现成脚本联调会 100% 失败。
- 最小修复：在 `BothSide.md` / `Server/AGENTS.md` 的联调清单明确三个变量 + 端口范围 + 内容 manifest（`Client/Assets/Resources/PMNet/BattleContentV1.json` 或 `HYLD_PMNET_CONTENT_MANIFEST`）；如需便利脚本，另加一个「设置 env 后启动」的 bat，**不要**在代码里补默认路径。

### B4（低，UI 回归）`UIMatchingPanel` 丢失 `ExitMathcing.SetActive(false)`

- 证据：旧 `ReturnCode.Succeed` 分支里的 `ExitMathcing.SetActive(false)` 随旧回退一起被删；`rg ExitMathcing` 全仓只剩 `UIMatchingPanel.cs:23` 的声明与 `HYLDStart.unity` 的字段赋值（fileID 1823144908），**无第二处引用**。新链 `PMClientSessionHost` 对 `SceneManager|LoadScene|SetActive|UIManger` **零命中**（不切场景、不关面板），面板仅在 `RemoveMatchingPlayer` 且 `Str == "-1"` 时由 `UIBaseManger.Close()` 关闭。
- 影响：PMDS1 入局成功后，玩家仍停留在匹配面板且「退出匹配」对象处于激活态（视觉/交互残留；不影响战斗正确性）。
- 最小修复：在 PMDS1 分支 `PMClientSessionHost.Enter(offer)` 前补 `ExitMathcing.SetActive(false)`；若确认「新链入场由 HUD 接管、面板必须保留」，则把该字段用途注释清楚。

### B5（中，设计后果，待用户裁决/实机确认）局内大厅 TCP 超时断线会提前终止合法新链对局

- 证据：`HYLDManger.CloseClient` 已删除旧链「战斗场景内 TCP 断开不抢跑服务端权威」的保护（原 `isBattleScene && hasBattleManager && !isBattleGameOver` 早退分支），现只要 `IsActive && !HasLastCombatResult` 就 `Stop()` 并回 `HuangYeLuanDouStart`；而新链结果由 DS 经 UDP 交付、与大厅 TCP 无关。
- 影响：大厅 TCP 抖动达到 4×pingInterval 超时（`Server.CheckPing` 口径）时，客户端会主动放弃一个仍在进行的合法对局。契约 §C 明确要求「HYLDManger 断线清新 PMClientSessionHost」，故可能是**有意**语义；但旧链专门为此设过保护，新链没有等价的「结果到达等待窗」保护。
- 最小修复（若判为缺陷）：仅在尚未进入结果等待窗时 `Stop()`，或 `Stop()` 前对 DS 侧做一次有界确认；否则把该取舍写进文档并在实机确认可接受。

### B6（低，文档漂移，不阻断）残留旧 mode 的文档/注释提示

- `Server/AGENTS.md:33` 仍写「TCP `7778` 登录…UDP `7777` 战斗实时消息」「`Server/ClientUdp.cs`（LZJUDP 类）」；`Client/Assets/AGENTS.md` §1/§3–§8 仍整段描述旧战斗链（与 §14/§15 新链段并存、未标覆盖）；`PMDsSessionHost.cs:446` 注释仍称「旧诊断路径（PMUdpRouter）」。
- 均为非活代码、不参与编译，**无实际阻断**；属契约 §E / T-L6 的文档收口项。

---

## 已检查范围

**完整阅读的文档**：`AGENTS.md`、`Client/Assets/AGENTS.md`、`Server/AGENTS.md`、`Docs/plans/net-legacy-retirement-contract.md`；`_legacy_server_removal_report.md` / `_legacy_client_removal_report.md` / `_legacy_ds_removal_report.md`（仅定位入口，未采信结论）。

**逐行阅读的源码**：`Server/Server/Server.cs`、`Server/Server/Client.cs`、`Server/Controller/ControllerManger.cs`、`Server/Controller/Controllers.cs`(:330–630 起)、`Server/DS/PMDsLobbyHost.cs`(:36–150)、`Client/Assets/Scripts/Server/Boot/PMDsHost.cs`（全文）、`…/Boot/PMDsSessionHost.cs`(:1240–1330, :2420–2480)、`…/Panel/UIMatchingPanel.cs`、`…/Panel/UISliderPanel.cs`、`…/Panel/UIStartMainPanel.cs`、`…/Manger/HYLDManger.cs`、`…/Manger/RequestManger.cs`、`Client/Assets/Scripts/Manger/ClearSenceManger.cs`、`Client/Assets/Editor/PMDsBuild.cs`(:205–300)、`Tools/PMDsHostCheck/Program.cs`(:150–235)。

**diff 复核**：本轮 60 个改动文件中的客户端/服务端关键项（`UIMatchingPanel`/`UISliderPanel`/`UIStartMainPanel`/`HYLDManger`/`RequestManger`/`ClearSenceManger`/`PMDsHost`/`PMDsBuild`/4 个资产）。

**扫描（`rg --no-ignore`；归零/完备性判定必须用真 `rg`，pi-fff 对 gitignored 与 `Library` 为 partial 语义）**：旧类型活引用、`7777|ServiceUDPPort`、资产 UnityEvent 方法名、`ExitMathcing`、`LoadScene|SceneManager`、`Requests[n]`、`HYLD_PMNET_DS*` 设置点；`HyldDS` 产物 mtime 与字节串探针；`EditorBuildSettings.asset` 场景清单；4 个改动资产的 `m_Script`/`m_Component` 差异。

**未检查（按非目标/边界停止）**：R6 战斗 core、PMR4/R5/R6 实现与生成物、第三方代码、proto 生成链与 `SocketProto.proto` 本体、其余 Tools 门禁内部实现、Unity 实机加载与 Play、`Server/**` 其它组在飞文件的实现细节。

---

## 建议下一步（最小补充查询 / 运行时验证）

1. **实机前重建 HyldDS**（或删除 `HyldDS/` 旧产物），消除 B1 的旧-mode 可达性与旧 `run_ds.bat` 误导；这是本轮唯一「能让旧链真正跑起来」的入口。
2. 给 `PMDsHost.Update` / `Shutdown` 加顶层 try/catch → `Fail(...)`（B2，极小改动，不影响新链语义与门禁）。
3. 联调清单补 DS 三项必需 env + 端口范围 + 内容 manifest（B3）；如需脚本，另建设置 env 的启动 bat，勿在代码里猜路径。
4. 需用户裁决两点后收口：PMDS1 入局后匹配面板/「退出匹配」按钮的去留（B4）；局内大厅 TCP 超时是否应终止合法对局（B5）。
5. 文档收口（B6）+ 实机跑 T-L1…T-L6 与 `Tools/PMLegacyRetirementTest`（含 G2 正/反向对照），把本轮未验证的编译/门禁/端到端补齐。
