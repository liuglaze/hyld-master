# R3-B 宿主接线调研（只读）

> 范围：`D:/UGit/hyld-master`。只读调研，未打开 Unity、未编译、未改任何生产文件。
> 硬边界：`Client/Assets/Scripts/Server/Boot/PMDsHost.cs`、`PMNetBootstrap.cs`、`Client/Assets/Scripts/PMNet/PMNetLaunchOptions.cs`、`Client/Assets/Editor/PMDsBuild.cs`、客户端匹配/进局直接链、ProjectSettings 场景配置。
> 阶段状态以 `Docs/plans/net-architecture-migration.md` 为准；本文件只记录本次调研证据，不标阶段完成。

## 1. 已确认（可引用证据）

### 1.1 环境与产物（实测）

| 项 | 实测结果 | 证据 |
|---|---|---|
| Unity 2019.4 路径 | 存在：`D:/Unity/2019.4.8f1/Editor/Unity.exe` | 目录列举 |
| Win64 headless variation | 存在（`win64_nondevelopment_mono` 等 4 个） | `Editor/Data/PlaybackEngines/windowsstandalonesupport/Variations/` |
| 工程锁 | `Client/Temp/UnityLockfile` **存在**，0 字节，mtime `2026-09-19 14:27`（当前 `2026-09-20 10:21`，≈20h 前） | `stat` |
| 编辑器进程 | **`Unity.exe` PID 58060 正在运行**（另有 `UnityCrashHandler64.exe`） | `tasklist` |
| 结论 | 现在起 batchmode 构建会撞工程锁；且 R3-B 需要的 DS 权威场景**本来就必须在编辑器里做** → 不能靠 CLI 构建闭环 | 上两行 |
| HyldDS 产物 | 完整：`HyldDS/HyldDS.exe` + `HyldDS_Data/`（`level0` 单场景），共 **328M**；`.gitignore:86` 忽略 `/HyldDS/` | `du -sh`、`.gitignore` |
| DS 上次真实运行证据 | `HyldDS/logs/ds_ds-p31.log`：`router recv=4 dispatched=4 battles=1`、`battleDispatch total=2 lastAction=BattlePushDowmPlayerOpeartions` | 日志尾部 |
| 日志中的缺口 | 全文件**无**任何 control/session/transport 关键字 → 新链路确实一处未接 | 同上 |

### 1.2 `PMDsHost` 当前职责与"旧诊断路径"的真实边界

`Client/Assets/Scripts/Server/Boot/PMDsHost.cs`（572 行）现状：

- `PMDsHost.Start`:`124` → `Initialize`:`132` → `TryBindSocket`:`209`（`new Socket(Dgram)` + `Bind(listen,port)` + `BeginReceiveFrom`）+ `InitializeRouter`:`159`。
- `InitializeRouter`:`159-172`：`new PMSingleBattleRegistry()` + `new PMUdpRouter(_socket, …)` + `RegisterBattle(battleId, HandleDiagnosticBattlePacket)`。
- `HandleDiagnosticBattlePacket`:`179-205`：**只计数 + 周期日志**，注释 `:174-178` 自认"临时/诊断"、"P3'-3 迁入权威战斗仿真时由真实 BattleController.Handle 取代"。
- `Update`:`334` → `DrainInbound`:`351`（`_router.DrainAndDispatch(MaxPacketsPerUpdate=256)`）+ `DriveTick`:`365`（**空骨架**，`// P3'-3 在此处接入权威战斗帧推进`）。
- `LogReady`:`418` 打印 `已注册诊断 handler；P3'-3 将由权威战斗仿真取代`；`-lobby` 缺失只打 warning（`:437`）。
- `Shutdown`:`534`、`OnDestroy`:`522`、`OnApplicationQuit`:`516`：幂等关 socket + `_router.ClearInbound()`。

**边界结论（这是 R3-B 的写入面）**：`PMDsHost` 现在等于「UDP socket + 裸 `MainPack` 路由（`PMUdpRouter`）+ 空 tick」。
R3-B 要新增的那条链（控制通道 + `PMTransportConnection`/`PMNetSessionBridge` + 权威帧）与它**没有任何共享代码**，
且**两者都不能直接挤在同一个 `-port` socket 上**：`PMUdpRouter` 吃裸 protobuf `MainPack`（无帧头），
`PMTransport` 吃自带长度/序号头的分片帧（见 1.5），同端口无法按字节区分。→ 必须由主 Agent 冻结端口归属（见 §4 F4）。

### 1.3 启动参数扩展点（`PMNetLaunchOptions.cs`）

- 现有键（`Apply` switch `:178`）：`server / batchmode / nographics / logfile / dsid / matchid / listen / port / lobby / tickrate`。
- **没有** `-bootstrap`、**没有** `-control`（全文件 grep 无命中）。
- 解析器是纯函数 `Parse`:`120`，`FromCurrentProcess` 读 `Environment.GetCommandLineArgs()`；支持 `-k v` 与 `-k=v`、大小写不敏感、非法值只记 `Warnings`。
- 门禁 `Tools/PMNetLaunchCheck`（94 项）按 `-lobby` 等分节断言（`:253-275` 为 `-lobby` 节）→ 新增参数必须在此扩断言，否则参数面无回归。
- 强制口径：`PMNetRuntime.IsDedicatedServer` 是唯一身份来源（`PMNetRuntime.cs:41`），`PMNetBootstrap.OnSubsystemRegistration`:`30` 判定、`OnAfterSceneLoad`:`71` 才 `PMDsHost.Start`（`:78`）。

**扩法（建议，最小改动）**：`Apply` 加两个 case + 两个字段，形如 `-bootstrap <绝对路径>`（同机引导文件，密钥不入命令行，符合 `net-r3-control-contract.md` §2）与 `-control <host:port>`（Lobby 控制通道；`PMDsControlWire.DefaultControlPort=7800`）。二者均为 DS 专属，缺省只记 warning，不影响客户端路径。

### 1.4 `PMDsBuild` 目前场景 + "适合权威碰撞场景"的构建方式

- `BootScenePath`:`35` = `Assets/Scenes/PMDsBoot.unity`；`BuildWindowsHeadlessDs`:`50` 用 `options.scenes = new[]{bootScene}`，与 `EditorBuildSettings.asset` **无关**（该 asset 里**没有** `PMDsBoot.unity`，实测 4 条 enabled 场景）。
- `EnsureBootScene`:`148`：缺失时用 `EditorSceneManager.NewScene(EmptyScene, Additive)` 生成**空场景**（`PMDsBoot.unity` 实测 123 行、**0 个 GameObject**）。
- `WriteLauncher`:`185` 生成 `run_ds.bat`（实测内容：`-batchmode -nographics -server -dsid -matchid -listen 0.0.0.0 -port %PORT% -lobby 127.0.0.1:7778 -tickrate 30 -logFile …`，**不含** bootstrap/control）。`ResolveProjectRoot`:`226`。
- 客户端战斗场景 `Assets/Scenes/HYLDGame.unity`（948K / 1582 个 `PrefabInstance`）：**地图不是静态几何**——
  地板/墙/障碍/树由 `ScenseBuildLogic.InitData()`（`HYLD1.0/Scripts/OldScripts/ScenseBuildLogic.cs:161` 起）按 `int[,] maps` 运行时 `Instantiate` 到 `MAP` 下（`MyInstantiate`:`251`），
  而 `floors/walls/obstacles/trees/Grasses` 是**场景组件上序列化的 prefab 数组引用**（`:31-40`）。
- DS 若直接加载整张 `HYLDGame.unity` 会跑客户端 Awake 链：场景含 `HYLDStaticValue`（guid `f425d8dc83f832340a6f80b7231893cf`，1 处），其 `Awake`:`155` 在 `:216`/`:226` `AddComponent<HYLDBaoShiZhengBaManger>()` / `AddComponent<BattleManger>()` 并调 `Init()`，
  随后 `BattleManger.Start`:`147` 调 `UDPSocketManger.Instance.InitSocket()` 绑**硬编码 UDP 7777**（`ConstValue.cs:23`）。**实测这两个类都没有 `IsDedicatedServer` 守卫**（全工程只有 `PMNetBootstrap`:`41`/`:73` 与 `HYLDManger`:`36`/`:95` 四处）。
- 场景根无 `BattleManger` 组件（`HYLDGame.unity` 对该 guid 命中 0 次）→ 它是运行时 AddComponent，**没有任何"禁用组件"的现成开关**。

**适配方式（三条候选，按风险排序）**：
1.（推荐）新建 DS 专属场景，只放 `ScenseBuildLogic`（连同其 prefab 数组引用）+ 需要的碰撞体，**不放** `HYLDStaticValue`/UI/相机；由 `PMDsBuild` 在编辑器里生成并加入 `options.scenes`。
2. 让 `ScenseBuildLogic` 与 `BattleManger` 具备 DS 门（加 `IsDedicatedServer` 守卫），再加载 `HYLDGame.unity` 的几何子集——改动面更大，且违背"不加载含客户端 Awake 的整张旧场景"。
3. 由 DS 侧代码在空场景里用 prefab 引用重建地图——需要把 prefab 数组从场景搬到可被 DS 引用的资产（DataAsset/静态表），改动最大。
- **确定性风险（已确认，非推断）**：`ScenseBuildLogic.InitData` 用 `UnityEngine.Random.Range` 逐格选 prefab，**没有播种**；`BattleInfo.rand_seed` 虽被 `InitBattleInfo(pack.BattleInfo.RandSeed, …)`（`UIMatchingPanel.cs:44`）收下，但 `HYLDRandom.srand`（`BattleManger.cs:770`）**全工程零调用点**。→ 当前客户端与 DS 各自建图会不一致；权威碰撞环境必须先冻结播种方案。
- **索引陷阱**：`SceneConfig`（`HYLD2.0/Config/GameConfig.cs:15`）用**整数索引**（`battleScene=2`/`clearScene=3`），与 `EditorBuildSettings` 顺序强绑定；DS 构建的 `options.scenes` 只有一张 → DS 构建里索引 2 不是 `HYLDGame`。DS 路径禁止走 `SceneConfig` 索引。

### 1.5 客户端匹配/进局/网络句柄/切服（直接链）

- 注册：`UIMatchingPanel.Init`:`26-29` `Requests.Add(new BaseRequest(this, RequestCode.Matching, ActionCode.StartEnterBattle))`。
- 响应：`UIMatchingPanel.OnResponse`:`36-47`：`ReturnCode.Succeed` → `BattleData.Instance.InitBattleInfo(pack.BattleInfo.RandSeed, pack.BattleInfo.BattleUsers)` + `ClearSenceManger.LoadScene(SceneConfig.battleScene)`。
- 服务端来源：`Server/Battle.cs:168-200`（`BattleController` 构造）组 `StartEnterBattle` 包，只带 `RandSeed` + `BattleUsers`（`id/battleid/playername/hero/teamid`），**不带任何 DS 地址或票据**。
- 中间场景：`ClearSenceManger.LoadScene`:`105` → `SceneManager.LoadScene(SceneConfig.clearScene)` → `AsyncLoadScene`:`131`（进度条 → 等 `UISliderPanel.IsCanEnterBattle` → `allowSceneActivation=true`）。
- UDP 创建：`BattleManger.Start`:`147-150`（`InitSocket()` 后 `Handle = HandleMessage`）；`UDPSocketManger.InitSocket`:`49` 内 `client.Connect(NetConfigValue.ServiceIP, 7777)` 并起后台 `ReceiveLoop`:`75`（只解析入队），`DrainAndDispatch`:`110` 主线程出队。
- **UDP 关闭：确认缺失**。`UDPSocketManger` 没有 `Close`/`Dispose`；`BattleManger.OnDestroy`:`753-762` 只停 tick + `ClearPredictionRuntimeState()`，**不关 UDP socket**。→ 切服/退局会泄漏旧 socket，R3-B 必须补关闭口径。
- Lobby 长连接：`HYLDManger.Awake`:`28-85` 建 `TCPServerManger`（`TCPSocketManger.cs:45` `TcpClient.Connect(ServiceIP, 7778)`）+ `PingPongManger`；`HYLDManger.OnDestroy`:`205-216` → `CloseSocket()`。`ChangeHero` 等局外请求走这条 TCP。**R3-B 要保留它**：它既是"局外链路不动"的要求，也是客户端拿到 DS 地址/票据最自然的现成通道（`RequestManger`/`BaseRequest` 已有 `request_id` 回带与超时，`BaseRequest.cs:100-110`）。
- 票据/地址交付面：`SocketProto.proto` 的 `MainPack` 最大字段号 `request_id=16`；`BattleInfo` 只有 `server_frame/rand_seed/battle_users/client_input/server_update`；`ActionCode` 最大 `BattleSetNetSimConfig=41`。**全 proto 无 ds_address/ds_port/ticket/bootstrap 字段**（grep 无命中）→ 交付载体必须由主 Agent 冻结（见 §4 F3）。

### 1.6 R3-A 产物在宿主侧的真实可用面（本次逐个核对）

- 控制协议：`PMNet/Control/PMDsControlProtocol.cs`（2163 行）——`PMDsControlWire`:`31`、`PMDsControlMessageType`:`80`（8 型）、`PMDsControlFraming`:`1847`（4 字节 LE 分帧）、`PMDsControlFrameDecoder`:`1946`（**支持半包/粘包**，`Append`/`TryDequeue`/`IsFaulted`）、`PMDsCrypto`:`1594`、`PMDsControlSigner`:`1744`。
- 票据：`PMDsTicket.cs`——`PMDsMatchKey.Create()`:`208`、`CreateControlSigner`:`255`、`CreateTicketIssuer`:`261`、`CreateTicketVerifier`:`267`；`PMDsTicketVerifier`/`PMDsTicketVerification`:`141`（**不暴露 Nonce/票据指纹**，见 `_r3a_control_report.md` §12 第 2 条）。
- Lobby 编排：`Server/DS/PMDsCoordinator.cs`（2235 行）——`PMDsAllocationRequest`:`195`（含 `CollisionDigest`:`210`、`Roster`:`213`、`Process`:`216`）、`IPMDsCoordinatorSink`、`Allocate`、`ExportBootstrapDocument`、`BeginStart`、`Tick`、`OnControlPayload`、`ResolveTicket`；`Server/DS/PMDsProcess.cs`（838 行）真实固定 exe 适配。
- 会话适配：`PMNet/Session/PMTransportConnection.cs`（`ctor`:`464`、`TryActivate`:`520`、`OnDatagram`:`719`、`Update`:`737`、`Disconnect`:`1054`、`BeginFrameDatagramBudget`/`RemainingFrameDatagramBudget`）与 `PMNetSessionBridge.cs`（`ctor`:`113`、`RegisterConnection`:`176`、`Update`:`379`、`FlushLifecycle`:`499`、`SendRpc`:`574`）。
- **缺口（已确认）**：`IPMTransportLink`（`Transport/PMTransportTypes.cs:204`）**全工程零生产实现**，只有 `Tools/PMTransportTest:102` 与 `Tools/PMNetSessionTest:368` 的测试 link；`IPMTransportLink.Send` 无端点参数（per-connection 语义）→ R3-B 必须自写真实 UDP link + 端点解复用。
- **缺口（已确认）**：全工程**没有任何文件**消费 `PMDsCoordinator`/`PMDsPortPool`（`grep` 只命中 `Server/DS/` 自身与 `Tools/PMDsControlTest`）→ 匹配入口与 `PMDsCoordinator` 之间是空的。
- **缺口（已确认）**：客户端与 DS 之间的**票据出示（presentation）**通道不存在——A2 报告明写"适配器本身不是对外握手服务器"，`PMTransportConnection` 只认已认证身份；`PMNetSessionBridge`/`PMApplicationEnvelope` 的 Kind 只有 `Lifecycle/Rpc/Replication/ReplicationAck`，无 Ticket。
- 生成链：`PMNet/Generated/` 目前**只有** `SocketProto.PMNet.g.cs`（无 Unity 侧 decl 生成产物）；声明生成产物现有唯一实例在 `Tools/PMNetE2E/Generated/`（`PMNetGeneratedRegistry.RegisterAll()`:`132` + `Seal(PMStableHash.GlobalProtocolHash(entries))`:`151`），ID 锁文件 `Docs/plans/pmnet-ids.json`（当前只含 `PMNetFixtures.*` 与 `PDeclCheck.Fixture.Pinned`）。
- 探针可用：`Tools/PMDsProbe`（真实 UDP 四步探针，期望序列见 `Program.cs` 头注释）可直接用于 R3-B 旧链路侧的回归对照。

## 2. 高概率推断（依据 + 置信度）

| # | 推断 | 依据 | 置信度 |
|---|---|---|---|
| I1 | 新游戏链路必须用**独立 UDP 端口**（不能与 `-port` 上的 `PMUdpRouter` 共用） | 裸 `MainPack` 与分片帧无共同前缀；`Ready.BoundPort` 语义是 DS 实际 bind 端口 | 高 |
| I2 | 客户端持票入局只能走"客户端 TCP→Lobby 领地址+票据，UDP→DS 出示票据"，DS 验证通过才 `Activate` | 契约 §2/§4 与 A2 报告明确方向；`PMTransportConnection.TryActivate` 注释要求宿主先认证 | 高 |
| I3 | DS 侧需要"每玩家一个 `PMTransportConnection` + endpoint→link 解复用"，客户端侧单连接 | 桥/连接的 per-connection 语义 + `IPMTransportLink` 无端点参数 | 高 |
| I4 | 保存 `PMDsBoot.unity` 单场景的现状下，`Ready.SceneReady=true` 会是**假就绪** | `PMDsBoot.unity` 0 GameObject，无碰撞几何；契约 §3 只认 `SceneReady=true` 才发地址，但 §3.9.2 要求权威碰撞必须真加载 | 高 |
| I5 | 若不冻结播种，DS 与客户端的地图会不一致 | `UnityEngine.Random.Range` 无播种 + `HYLDRandom.srand` 零调用 | 高 |
| I6 | R3-B 若把测试网络对象写进 `Docs/plans/pmnet-ids.json`，会改变全局 `ProtocolHash`，从而影响其他门禁/契约里已引用的摘要 | `DeclEmitter.cs:259` `Seal(GlobalProtocolHash(entries))` | 中（需主 Agent 决定是否用独立 id-lock） |

## 3. 无法确定（缺少证据）

1. **客户端拿 DS 地址/票据的载体**：无 proto 字段、无新 ActionCode、无设计文档条目（`net-r0-contract.md`/`net-r3-control-contract.md` 均未落到字段级）→ 属**未冻结决策**，不是遗漏。
2. **票据出示报文格式与通道**：客户端→DS 的出示既不在控制协议（那只面向 Lobby↔DS），也不在 Session 信封 Kind 里。
3. **`CollisionDigest` 的定义**：`AllocationRequest.CollisionDigest`:`210` 与 `Ready.CollisionDigest` 必须有同一计算口径，但全工程无计算实现、无文档定义。
4. **谁写引导文件、写在哪**：A1 只做字节编解码（`PMDsBootstrapDocument`），原子发布/权限"由 R3-B 实现"（`_r3a_control_report.md` §5 第 1 条），路径策略未定。
5. **`PMSessionIdentity` 的 Epoch 来源**：DS 侧可从引导文件取，客户端侧的 `Epoch` 由谁下发未定。
6. **DS 权威场景是否允许新增 Unity 侧生成产物目录**（`PMNet/Generated/` 目前只有 proto 产物），以及是否影响既有门禁白名单。
7. **编辑器当前是否真的锁着工程**：锁文件存在且 `Unity.exe` 在跑，但无法确认它是否打开了本工程（未打开 Unity，属硬边界）。

## 4. 需要主 Agent 冻结的 API / 决策

| ID | 冻结项 | 建议 |
|---|---|---|
| F1 | 启动参数：`-bootstrap <abs path>`、`-control <host:port>` 的名字/缺省/错误语义 | 按 §1.3 扩 `Apply` + `Tools/PMNetLaunchCheck` 断言 |
| F2 | 引导文件的写入方/路径/原子发布/权限；DS 只读 | Lobby 写临时文件再 rename；DS 只接受路径参数 |
| F3 | 客户端→Lobby 领取「DS 地址+端口+票据」的载体 | 追加 `ActionCode`（=42）或 `BattleInfo` 追加字段；两者都要重生成 `SocketProto.cs` + `PMNet/Generated/SocketProto.PMNet.g.cs`，并重算 `ProtocolHash` |
| F4 | 新链路端口归属：新 port vs 复用 `-port`；`Ready.BoundPort` 语义 | 新链路独占新端口，旧 `PMUdpRouter` 留 `-port`，两者不互认字节 |
| F5 | 客户端→DS 票据出示报文（通道 + 字段 + 失败即断） | 在 Session 层新增一个 Kind 或作为首个 RPC；DS 验票后才 `TryActivate` |
| F6 | 票据端点消费账本 + 全局 uid 占用账本（A1 明确不做） | 键 = 原始票据字节哈希；uid 账本与端口池同级共享 |
| F7 | `CollisionDigest` 计算口径 + `SceneReady` 判据 | 由 DS 场景内容算稳定摘要；空场景不得报 ready |
| F8 | 地图播种：`rand_seed` 如何进入 `ScenseBuildLogic` | 冻结后 DS/客户端同图；否则权威碰撞不可比 |
| F9 | 名册注入：`PMSingleBattleRegistry.BattleId`、`RegisterPlayer(uid,battlePlayerId)`、`AllowUnassignedPlayers=false` 的调用点 | 由引导文件驱动 |
| F10 | `RegisterAll()` 的调用时机与位置（必须在任何连接激活前） | 新生成 Registry 放在 Unity 侧；调用点 = 会话宿主启动期 |
| F11 | R3-B 测试网络对象的 ID 锁文件归属 | 若进 `Docs/plans/pmnet-ids.json` 会改全局摘要，需明确是否可接受 |
| F12 | UDP 关闭口径（现缺失）
 | 补 `UDPSocketManger.Close()` 与切服/退局调用点 |

## 5. 最小可验收 R3-B 闭环提案

**目标**：Lobby 真拉 `HyldDS.exe` → DS 控制通道 `Ready`（真端口 + 真 `SceneReady` + `CollisionDigest`）→ 两客户端领地址+票据并 `Activate` → **一个测试网络对象**完成属性复制 + 双向 RPC → 权威结算 `Result` → `ResultAck` → 进程退出 → 端口/名册释放。**真实玩法（角色/子弹/伤害）仍留 R4–R6。**

| 段 | 内容 | 验收 |
|---|---|---|
| B-1 | Lobby：`PMDsCoordinator` + 共享 `PMDsPortPool` + uid 账本接入匹配入口（可先用测试触发点，不改旧匹配语义） | 分配/启动/超时/崩溃回收；地址只在 `Ready` 后发布一次 |
| B-2 | DS：`PMNetLaunchOptions` 扩 `-bootstrap/-control`；DS 侧 LobbyAgent 读引导文件 → TCP 控制通道 → `Ready/Heartbeat/Result/Exited` | 半包/粘包/坏 MAC 不改状态；`sceneReady` 不达标不发布地址 |
| B-3 | DS 权威场景：新建只含 `ScenseBuildLogic`（+碰撞/prefab 引用）的 DS 场景，`PMDsBuild` 纳入 `options.scenes` | 场景内**无** `HYLDStaticValue`/UI/相机；预热产出真实碰撞 |
| B-4 | 网络对象测试场景：Unity 侧 1 个 `[PMReplicated]` 属性 + 1 对 `[PMServerRpc]/[PMClientRpc]`，生成产物 + `RegisterAll()` | 属性最终收敛；RPC 保序一次交付；非 Owner 被拒 |
| B-5 | 客户端切服：从 Lobby 领地址+票据 → 建新 UDP link → 出示票据 → `TryActivate` | 保留 7778 长连接；旧 UDP 7777 显式关闭 |
| B-6 | 收尾：`Result` 幂等 → `Exited`/进程退出 → 端口与名册释放 | 未确认退出不得释放资源 |

**可单独并行修改的现有脚本（互不重叠）**：
- 组 A（纯 C#/工具面）：`Client/Assets/Scripts/PMNet/PMNetLaunchOptions.cs`、`Client/Assets/Scripts/PMNet/PMNetRuntime.cs`（如需）、`Tools/PMNetLaunchCheck/Program.cs`。
- 组 B（Lobby 侧）：`Server/DS/PMDsCoordinator.cs` 的新增包装、新 `Server/DS/PMDsMatchingBridge.cs`（不改 `Controllers.cs` 既有匹配语义）。
- 组 C（DS 宿主）：`Client/Assets/Scripts/Server/Boot/PMNetBootstrap.cs`、`PMDsHost.cs`（只加新链路入口，不动 `PMUdpRouter` 分支）。
- 组 D（构建/场景）：`Client/Assets/Editor/PMDsBuild.cs` + 新场景资产（**依赖编辑器**，必须串行且需用户关掉/使用编辑器）。
- 组 E（客户端切服）：`UIMatchingPanel.cs`、`ClearSenceManger.cs`、`BattleManger.cs`（仅补 UDP 关闭与切服钩子）。
- 组 F（网络测试对象 + 生成产物）：新 `PMNet/Fixtures/*.cs` + `Generated/*`（依赖 `PMNetGen`，与 A/B/C 无文件重叠）。
- **顺序依赖**：B-4/B-5 的实现位置取决于 F3/F5 冻结结果；`PMDsBuild.cs` 场景改造必须在编辑器内进行（当前 `Unity.exe` 在跑）。

## 6. 新文件 / `.meta` 清单（建议）

所有新增 `.cs` 与目录都必须配 `.meta`（`Client/Assets/AGENTS.md` 明写）。编码约定：共享源 UTF-8+BOM+CRLF；`.meta` 无 BOM+LF。

Unity 侧：
1. `Client/Assets/Scripts/Server/Boot/PMDsLobbyAgent.cs`（+`.meta`）— DS 控制通道客户端（引导文件、Ready/Heartbeat/Result/Exited、票据验证、端点账本、激活）。
2. `Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs`（+`.meta`）— DS 侧 `PMNetWorld`+`PMReplicationChannel`+`PMNetSessionBridge` 装配与主线程驱动。
3. `Client/Assets/Scripts/PMUnity/`（目录+`.meta`）`PMUdpTransportLink.cs`（+`.meta`）— 真实 `IPMTransportLink`（`Send`/`Describe`）+ 入站解复用。
4. `Client/Assets/Scripts/PMUnity/PMDsEndpointDemux.cs`（+`.meta`）— endpoint→link 映射（DS 侧）。
5. `Client/Assets/Scripts/PMNet/Fixtures/PMR3BObjects.cs`（+`.meta`）— 测试网络对象声明。
6. `Client/Assets/Scripts/PMNet/Generated/PMNetGeneratedRegistry.g.cs`（+`.meta`）— `PMNetGen --decl-gen` 产物（新增）。
7. `Client/Assets/Scenes/PMDsAuthority.unity`（+`.meta`）— DS 权威场景（编辑器生成）。
8. 客户端切服适配（建议新增文件而非改旧文件）：`Client/Assets/Scripts/Server/Boot/PMClientEnterBattle.cs`（+`.meta`）。

Lobby 侧：
9. `Server/DS/PMDsMatchingBridge.cs` — 匹配入口 ↔ `PMDsCoordinator` + 共享端口池 + uid 账本（`Server/**` 隐式 compile，无需改 csproj）。

工具侧：
10. `Tools/PMDsControlTest/` 扩断言（可在原文件内，不新增工程）；新门禁如需 R3-B 端到端探针，建议扩 `Tools/PMDsProbe`。

报告：`Docs/plans/_r3b_host_survey.md`（本文件）。

## 7. 已检查范围（实际读取）

文档（按序）：`D:/UE_Project/ProjectMecury/AI-Instruction.md`、`Client/Assets/AGENTS.md`、`Server/AGENTS.md`、
`Docs/plans/net-architecture-migration.md`（卷首当前状态、§3.9 全节、§5 分阶段、文末 RV1–RV7 与 R3 分步/交接）、
`Docs/plans/net-r3-control-contract.md`（全文）、`Docs/plans/_r3a_control_report.md`（§1/§3/§4/§5/§7/§9/§11/§12）、
`Docs/plans/_r3a_session_report.md`（§1–§9）、`Docs/plans/net-r0-contract.md`（M12/§2 定位）、`Docs/plans/pmnet-ids.json`。
文档如何决定入口：AGENTS → 仓库定位；主计划 §3.9 → 模块职责（M11 宿主、M12 Lobby）；R3 契约 §2/§3/§5 → 票据、消息、状态门与门禁口径；A1/A2 报告 → 已交付 API 与剩余缺口。

源码/资产（只读）：`Client/Assets/Scripts/Server/Boot/{PMDsHost,PMNetBootstrap}.cs`、`Client/Assets/Scripts/PMNet/{PMNetLaunchOptions,PMNetRuntime}.cs`、
`Client/Assets/Scripts/PMNet/Control/{PMDsControlProtocol,PMDsTicket}.cs`（公开面/枚举/Decoder/Ready/Bootstrap body）、
`Client/Assets/Scripts/PMNet/Session/{PMTransportConnection,PMNetSessionBridge}.cs`（公开面）、`Client/Assets/Scripts/PMNet/Transport/PMTransportTypes.cs`（`IPMTransportLink`）、
`Client/Assets/Scripts/PMNet/Declarations/PMNetDeclarations.cs`（`ProtocolHash`/`Seal`）、
`Client/Assets/Scripts/{Server/Panel/UIMatchingPanel.cs, Manger/ClearSenceManger.cs, Server/Request/BaseRequest.cs, Server/Manger/{HYLDManger,TCPSocketManger,UDPSocketManger}.cs, Server/Manger/Battle/BattleManger.cs, Server/ConstValue.cs, Server/Net/{PMSingleBattleRegistry,PMUdpRouter}.cs}`、
`Client/Assets/HYLD1.0/Scripts/OldScripts/{ScenseBuildLogic,HYLDStaticValue}.cs`、`Client/Assets/HYLD2.0/Config/GameConfig.cs`、
`Client/Assets/Editor/PMDsBuild.cs`、`Client/Assets/Scenes/{PMDsBoot,HYLDGame,OBJSence,HYLDAsyncScence}.unity`（结构/组件 guid 命中）、
`Client/ProjectSettings/EditorBuildSettings.asset`、`ProtobufAndNotepad/Protobuf/SocketProto.proto`、`.gitignore`；
`Server/Battle.cs`（构造/`StartEnterBattle`）、`Server/BattleManage.cs`（`TryBeginBattle`）、`Server/DS/{PMDsCoordinator,PMDsProcess}.cs`（公开面）、`Server/Controller/Controllers.cs`（`StartFighting` 定位）；
`Tools/PMNetLaunchCheck/Program.cs`（`-lobby` 节）、`Tools/PMNetE2E/Generated/PMNetGeneratedRegistry.g.cs`、`Tools/PMDsProbe/Program.cs`（头注释）、`Tools/` 目录清单。

未做（硬边界）：未打开 Unity、未编译、未跑门禁、未改任何生产文件、未做 svn/git 写操作、未运行 DS。

## 8. 建议下一步（最小补充查询）

1. 主 Agent 冻结 §4 的 F3/F4/F5/F11（这四项决定文件落点，其余可并行），并确认"DS 权威场景必须在编辑器里做"是否接受（当前编辑器已开）。
2. 补一条**有界**查询：`Tools/PMNetE2E/E2eFixtures.cs` + `PMNetGeneratedRegistry.g.cs` 的完整声明写法，作为 R3-B 测试对象的模板（避免重新发明 `PMReplicated`/`PMServerRpc` 用法）。
3. 运行期验证（由主 Agent/用户在编辑器内执行）：新建 DS 场景 → 只放 `ScenseBuildLogic` → 确认 `Awake` 链不触发；再跑 `HyldDS/run_ds.bat` 与 `Tools/PMDsProbe` 对照旧链路。
