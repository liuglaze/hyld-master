# 旧链删除前调查：旧服战斗仿真 / UDP7777 / 匹配 fallback / 断线旧通知 / DS 旧 UDP 诊断分支

范围：`D:/UGit/hyld-master`。只读调查，未修改任何业务文件（本报告是唯一写入）。
必读文档已完整读过：`AGENTS.md` → `Server/AGENTS.md` → `Client/Assets/AGENTS.md` → `Docs/plans/net-r6-combat-contract.md`（R6 当前新链范围）。
用户口径：**主要删除旧代码，不再保留旧协议兼容**（因此旧模式入口允许硬切到新链，不需要「旧客户端仍可用」这一约束）。

## 结论摘要

五组目标在代码上互相咬合，但**咬合面只有 4 个缝**，删完后服务端只剩「大厅 TCP + DS 编排」两个权威面：

1. `Server/Server/Battle*.cs`（7 文件）只被 `BattleManage` / `LZJUDP` / `MatchingController.StartFighting` 旧分支 / `BattleController` 自身引用，**没有任何 Server 之外的程序集编译它们**（`Server.csproj` 用 SDK 默认 glob）。可整文件删。
2. `LZJUDP`（`ClientUdp.cs`）唯一启动点是 `Server.cs:40`，唯一调用方是 `BattleController`。删 `BattleController` 后 `LZJUDP` 零引用。
3. 匹配入口 `MatchingController.StartFighting`（`Controllers.cs:668`）是**唯一二分点**：`NewChainEnabled` 为真走 DS，否则静默走旧链。硬切 = 删掉 `else` 分支 + 删 `NewChainEnabled` 门，则未配 DS 时匹配显式失败。
4. `Client.Close` 里两条断线通知：旧的是 `BattleManage.Instance.HandleClientDisconnect`（`:232`），新的是 `lobbyHost.NotifyClientDisconnected`（`:236`）。只删前者。
5. Unity 侧 `PMDsHost` 的「无 `-bootstrap`」分支是**唯一的旧 UDP 路由使用面**，连带 `Net/PMUdpRouter.cs`、`Net/PMSingleBattleRegistry.cs`、`Tools/PMUdpRouterCheck`、`Tools/PMUdpRouterTest`、`Tools/PMDsProbe`、`PMUnityGlueCheck.csproj` 的一行 include、以及 `PMDsBuild.cs` 生成的 `run_ds.bat` 旧形态分支。

**不阻塞但必须一并决策的邻接项**：`ClearSenceController` 旧清场就绪（依赖 `BattleManage.TryGetBattleContextByUid`）；proto 里旧战斗 ActionCode / `BattleInfo` 家族的彻底下线（客户端旧链 `BattleData/BattleManger/UDPSocketManger`、`PMNetVerify`、`Tools/PMBattleSimTest` 仍引用）。见「§7 需要一并决策的两项」。

---

## 一、证据一：旧 Server 战斗仿真链路（可达调用 + 外部引用）

### 1.1 文件与对外依赖

| 文件 | 行数 | 对外依赖（删它之前必须处理的缝） |
|---|---|---|
| `Server/Server/Battle.cs` | 762 | `LZJUDP`（`:170` RegisterBattle）、`Server.GetActiveClient(...).Send(StartEnterBattle)`（`:200`）、`ServerConfig.frameTime`（`:29`）、`PMNet.Shared`（`BattleNumericConfig`/`PMBattleSim`）、`Server.Controller.MatchUserInfo` / `FightPattern` |
| `Server/Server/BattleController.Bullets.cs` | 324 | 仅 `PMNet.Shared` + 同 partial 字段，无外部类型 |
| `Server/Server/BattleController.Network.cs` | 927 | `LZJUDP.Instance.Send/ClearBattleNetSimConfig/UnregisterBattle`（`:105`/`:891`/`:893`）、`BattleManage.Instance.FinishBattle`（`:894`）、`PMNet.Shared` |
| `Server/Server/BattleManage.cs` | 259 | `Server.GetClientByID(...).Send(BattleReview)`（`:244`）、`BattleContext`、`MatchUserInfo`、`MatchingController.FightPattern` |
| `Server/Server/BattleContext.cs` | 36 | `MatchUserInfo`、`MatchingController.FightPattern` |
| `Server/Server/ServerBullet.cs` | 19 | `ServerVector3` |
| `Server/Server/ServerVector3.cs` | 30 | 无（纯 struct；`PMBattleSim.cs` 注释里提到它，但**不引用**它） |

### 1.2 全部外部引用（rg --no-ignore 实测，排除 bin/obj/Library/旧报告）

`BattleController` / `BattleContext` / `ServerBullet` / `ServerVector3` 在**代码**中的外部引用只有：

- `Server/Server/BattleManage.cs`（同组，删除）
- `Server/Controller/Controllers.cs:81`（`ClearSenceController`，见 §7-1）
- `Server/Server/ClientUdp.cs`（同组，删除）
- 其余全部命中都在 **文档/注释**：`Server/AGENTS.md`、`Server/Docs/ForClient.md`、`Client/Assets/Scripts/Math/BattleFloatMath.cs`（注释）、`Client/Assets/Scripts/PMCombat/PMCombat{WeaponPlanner,Session}.cs`（注释，说明「旧实现对应物」）、`Tools/check_client_authority_writes.py`（表头文档）、`Tools/PMBattleSimTest/Program.cs`（把旧公式**冻结重写**成 `OldServer.*`，**不编译** `Battle.cs`）。

`Tools/PMNumericEquivalenceTest/PMNumericEquivalenceTest.csproj` 里出现的 `Server/Server/HeroConfig.cs` 只在**注释**中；它实际只编 `Client/Assets/Scripts/Shared/**` + `_old_heroconfig_snapshot.json`。→ **无门禁依赖旧战斗源文件**。

### 1.3 可删边界

整文件删 7 个 + `ServerConfig.cs` 里两条常量（`UDPservePort` 随 §2 删，`frameTime` 仅被 `Battle.cs:29` 使用 → 一并删）。
`Server/Server/SocketProto.cs` **保留**（新链仍用 `MainPack`/`ActionCode.StartEnterBattle`/`Ping`/`Pong` 走大厅 TCP；见 §7-2）。

---

## 二、证据二：UDP 7777（`LZJUDP`）

### 2.1 事实

- `Server/Server/ClientUdp.cs`（`public class LZJUDP`，846 行）：单例 + `ServerConfig.UDPservePort`(7777) bind（`:151`）+ 接收线程 + battle-scoped NetSim 调度线程 + `_endpointRouteMap`/`_handlers` 路由表 + Ping→Pong。
- 启动点：`Server/Server/Server.cs:40` `LZJUDP.Instance.Init();`（`Init()` 本体是空方法，真正的 bind 在 `LZJUDP` 私有构造里，即 `Instance` 首次访问）. 该行是**唯一**启动点。
- 调用点（全部在旧战斗组内）：`Battle.cs:170` `RegisterBattle`；`Battle.cs:285/326/473` `ApplyBattleNetSimConfig`；`Battle.cs:348` `Send`；`BattleController.Network.cs:105/905` `Send`；`BattleController.Network.cs:891` `ClearBattleNetSimConfig`；`BattleController.Network.cs:893` `UnregisterBattle`。
- `ServerConfig.UDPservePort` 其他引用：`Client/Assets/Scripts/Server/Manger/TCPSocketManger.cs:51` 是**已注释掉**的一行 → 不是阻塞。
- NetSim 脚手架（`BattleNetSimConfig` 等）只服务旧链，`BattleSetNetSimConfig` ActionCode 只被 `ClientUdp.cs`/`Battle.cs` 使用。

### 2.2 可删边界

- 删：`Server/Server/ClientUdp.cs` 整文件；`Server.cs:40`；`ServerConfig.UDPservePort`。
- `PMNet` 核心的同名概念要区分：`Client/Assets/Scripts/PMNet/**` 里的连接/分帧/ACK 是新链自己的传输，**与 `LZJUDP` 无关**，不要删。

### 2.3 与 DS 端口的关系（重要）

新链**不共用** 7777：DS 由 Lobby 用 `HYLD_PMNET_DS_PORT_FIRST/LAST`（默认 7801–7899）分配每局端口，见 `Server/DS/PMDsLobbyHost.cs:1591 BuildFinalArguments`（必带 `-bootstrap`）。7777 只在「客户端旧链」与「旧服」之间有意义 → 删除服务端 7777 后，客户端旧 UDP 将永远连不上（这正是硬切的目标，且旧链已不再被新局使用）。

---

## 三、证据三：匹配 fallback

### 3.1 事实（唯一二分点）

```
MatchingController.AddMatchingPlayer        Controllers.cs:417
  → CheckCanFight → BuildMatchResult → ReleaseRoom
  → StartFighting(server, matchResult)      Controllers.cs:668   ← 唯一二分点
       if (PMDsLobbyHost.NewChainEnabled)   Controllers.cs:673
           → StartFightingDedicatedServer   Controllers.cs:705   ← 新链（保留）
       else
           BuildMatchUsers                  Controllers.cs:661   ← 旧链（删）
           → BattleManage.TryBeginBattle    Controllers.cs:686   ← 旧链（删）
```

（精确锚点：`ClearSenceController`:26 / `ClientSendClearSenceReady`:68 / `MatchUserInfo`:112-122 / `BuildMatchUsers`:639-666（`socketIP` 赋值:661）/ `StartFighting`:668 / 新链门:673 / else 分支出现在 `:679-693` / `StartFightingDedicatedServer`:705）

- `NewChainEnabled` 的唯一赋值点：`Server.cs:290`（= `options.Enabled` = `HYLD_PMNET_DS`）。唯一读取点：`Controllers.cs:673`。→ 硬切后该静态属性可整体删除。
- `BuildMatchUsers`（`:639-666`）只被旧分支调用；它消费 `MatchUserInfo`（`Controllers.cs` 内 struct，`:114-122`）与 `Client.socketIp`（`Client.cs:65/75`，**唯一读取点就是 `Controllers.cs:661`**）。
- `MatchUserInfo`（`:112-122`）其他使用者只有旧战斗组（`BattleManage`/`BattleContext`/`Battle.cs`）。
- `MatchResult` / `MatchedPlayerEntry` / `FightPattern` / `BattleRoom` **保留**：新链用 `matchResult.roomId` / `matchResult.fightPattern.ToString()` / `matchResult.players`（`Controllers.cs:730-766`）。
- `Server.InitializeDedicatedServerLobby`（`Server.cs:285-352`）当前被 `options.Enabled` 提前 `return`（`Server.cs:292-296`）。

### 3.2 可删边界

| 动作 | 位置 |
|---|---|
| 删 `StartFighting` 的 else 分支（`BuildMatchUsers`+`TryBeginBattle`） | `Controllers.cs:679-693` |
| 删 `BuildMatchUsers` | `Controllers.cs:639-666` |
| 删 `MatchUserInfo` struct | `Controllers.cs:112-122`（注意 `ToString()` 仅用于旧链日志） |
| 删 `Client.socketIp`（随之零引用） | `Client.cs:65,75`（可选清理） |
| `StartFighting` 改为「始终 DS；宿主未就绪即显式失败」 | `Controllers.cs:668-678` |
| 删 `NewChainEnabled` 属性 + 赋值 + 判断 | `PMDsLobbyHost.cs:431`、`Server.cs:290`、`Controllers.cs:673` |
| `InitializeDedicatedServerLobby` 去掉 `options.Enabled` 门（改为「配置不全 ⇒ 拒绝拉局」） | `Server.cs:285-352`（早退在 `:292`） |

---

## 四、证据四：Client 断线旧通知

### 4.1 事实

`Server/Server/Client.cs:227-240`（`Close` 内）：

```
_server._controllerManger?.CloseClient(this, UID);        // :230 保留（房间/匹配清理）
BattleManage.Instance.HandleClientDisconnect(_server, UID); // :232 ← 旧链断线通知（删）
PMNet.Control.PMDsLobbyHost lobbyHost = ...Instance;        // :237 保留
if (lobbyHost != null) lobbyHost.NotifyClientDisconnected(UID); // :239 ← 新链通知（保留）
```

旧链路内部（`BattleManage.HandleClientDisconnect:169` → `TryGetController` → `BattleController.HandlePlayerDisconnect`）在删掉 `BattleManage` 后整链消失。
`Client.cs:139` 的 `isImportantTcpPack` 判定里含 `ActionCode.BattleReview` → `BattleReview` 不再由服务端产生后，这一行必须改成不含它（否则只是留一个永不命中的条件，属死代码）。

### 4.2 可删边界

- 删 `Client.cs:232`（`BattleManage.Instance.HandleClientDisconnect`）。
- 改 `Client.cs:139`：`isImportantTcpPack` 去掉 `ActionCode.BattleReview`。
- 保留：`_controllerManger.CloseClient`（`MatchingController.CloseClient:708` 房间/匹配清理）、`UserData.BordCaseToFriendLogout`、`lobbyHost.NotifyClientDisconnected`、`RestorePlayerOnline`。
- 关联文档：`Client/Assets/AGENTS.md` §1 / `Server/AGENTS.md` §2 里「BattleReview 大包」段落需要改（服务端不再发 `BattleReview`；`Server.cs:424-428` 已明确「不发送 BattleReview」）。

---

## 五、证据五：Unity `PMDsHost` 无 `-bootstrap` 的旧 `PMUdpRouter` 分支

### 5.1 分支判定与可达性

`Client/Assets/Scripts/Server/Boot/PMDsHost.cs:140-163 Initialize()`：

```
if (!string.IsNullOrEmpty(options.BootstrapPath)) { StartNewChainSessionHost(options); return; }  // :150-154 新链
bool bound = TryBindSocket(options);        // :156  ← 旧：裸 UDP socket on -port
if (bound) InitializeRouter();              // :161  ← 旧：PMUdpRouter + PMSingleBattleRegistry
LogReady(bound, options);                   // :163  ← 旧：就绪日志
```

**可达性证据**：Lobby 拉起 DS 时**必带** `-bootstrap`（`Server/DS/PMDsLobbyHost.cs:1601`，注释 `「带 bootstrap 不能旧 router 启动」`）。所以该分支只在「手工执行产物 / `run_ds.bat` 旧形态 / 探针」时可达 → 生产路径已不可达（高置信，见 §9-推断 A）。

### 5.2 旧分支的全部可达调用（同文件内）

| 位置 | 内容 |
|---|---|
| `:205-214` | `InitializeRouter`：`_registry = new PMSingleBattleRegistry()`(`:207`)；`_router = new PMUdpRouter(_socket, _registry, EnqueueLog×3)`(`:208`)；`RegisterBattle(battleId, HandleDiagnosticBattlePacket)`(`:211`) |
| `:225-249` | `HandleDiagnosticBattlePacket`（P3'-1 诊断 handler，注释明写「P3'-3 由 BattleController.Handle 取代」——**该取代已作废**，新链走 `PMDsSessionHost`） |
| `:255-302` | `TryBindSocket`（裸 Dgram socket bind `-port`；调用点 `:156`） |
| `:305-358` | `BeginReceive/OnReceive`（`_router.OnDatagramReceived`，`:327`） |
| `:380-408` | `Update()` 旧分支：`DrainInbound()` / `DrainPendingLogs()` / `DriveTick()` / `LogHeartbeat()`（`:387` 新链提前 return；`:398` `DriveTick`；`:403` `LogHeartbeat`） |
| `:410-419` | `DrainInbound` → `_router.DrainAndDispatch`(`:417`) |
| `:424-437` | `DriveTick`（空 tick，`P3'-3 在此接入` 注释已作废） |
| `:504-540` | `LogHeartbeat`（打 `_router.DescribeCounters()` / `RegisteredBattleCount` / battle dispatch 计数） |
| `:477-499` | `LogReady`（`UDP 路由层已就绪…P3'-3 将由权威战斗仿真取代` 等） |
| `:593-641` | `Shutdown` 旧路径：`_router.ClearInbound()`（`:620`）、`_router.UnregisterBattle`、`_socket.Close()` |
| `:60-136` 区段 | 字段 `_socket/_receiveEndPoint/_receiveBuffer/_registry/_router/_knownRemotes/_tickAccumulator/_tickInterval/_receivedDatagrams/_receiveErrors/_receiveBatches/_battlePacketsDispatched/_lastBattleActionCode/_lastBattleActionSeenTicks/_battleActionAllowance/_battleNullPackets/_tickCount` |
| `:39-43` | `[Obsolete(..., true)] ScaffoldPongPayload`（P2' 占位，编译期防陈旧引用） |

### 5.3 整文件删除候选与门禁

| 对象 | 类型 |
|---|---|
| `Client/Assets/Scripts/Server/Net/PMUdpRouter.cs`（845 行，`public sealed class PMUdpRouter`） | delete（唯一引用者 = `PMDsHost` 旧分支 + `PMUdpRouterTest` + 注释） |
| `Client/Assets/Scripts/Server/Net/PMSingleBattleRegistry.cs`（150 行） | delete（唯一引用者 = `PMDsHost:207` + `PMUdpRouterTest`） |
| `Client/Assets/Scripts/Server/Net/*.cs.meta` | delete（随源文件） |
| `Tools/PMUdpRouterCheck/`（csproj 全量 include `Server/Net/**`） | delete（删后 glob 为空 ⇒ 门禁失去意义） |
| `Tools/PMUdpRouterTest/`（44 项，真实 UDP 回环测该路由层） | delete |
| `Tools/PMDsProbe/`（四步探针：未建链 Ping→BattleReady 建链→Ping→业务包，**只为该路由层存在**） | delete（或改造；默认删） |
| `Tools/PMUnityGlueCheck/PMUnityGlueCheck.csproj:62` 的 `Server/Net/**` include + 文件头注释 | modify |
| `Client/Assets/Editor/PMDsBuild.cs:221,260-268`（生成 `run_ds.bat` 的「旧形态」分支与注释） | modify（**必须改生成器**，`HyldDS/` 已 gitignore，只改产物无效） |
| `Client/Assets/Scripts/Server/Boot/PMDsHost.cs` | modify（删旧分支，保留 `-bootstrap` 新链 + 日志/心跳/`FlushTrace` 收尾职责） |
| `Client/Assets/Scripts/PMNet/PMNetLaunchOptions.cs`（`-port` 仍被新链使用；注释里「硬编码 7777」需改） | modify（注释级） |
| `Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs:8,888` 与 `PMClientSessionHost.cs:888`（注释里引用旧 7777 / 旧 socket 关闭） | modify（注释级） |

**新链必须保留的 `PMDsHost` 职责**（删旧分支时不要误删）：`Instance` 幂等、`-bootstrap` 分支、`StartNewChainSessionHost` / `OnNewChainExitRequested`、`OnApplicationQuit`/`OnDestroy`→`Shutdown`（新链分支）、`_pendingLogs`+`EnqueueLog`/`DrainPendingLogs`（**新链仍在用**，`:387-392` 会调）、心跳日志（若仍需要）、`HYLDDebug.FlushTrace`。`Update()` 里旧分支的 `DriveTick` 是**空实现**，删除不丢功能。

---

## 六、按文件的 delete / modify / keep 清单

### 6.1 delete（整文件）

```
Server/Server/Battle.cs
Server/Server/BattleController.Bullets.cs
Server/Server/BattleController.Network.cs
Server/Server/BattleManage.cs
Server/Server/BattleContext.cs
Server/Server/ServerBullet.cs
Server/Server/ServerVector3.cs
Server/Server/ClientUdp.cs                       // LZJUDP / UDP 7777
Client/Assets/Scripts/Server/Net/PMUdpRouter.cs
Client/Assets/Scripts/Server/Net/PMUdpRouter.cs.meta
Client/Assets/Scripts/Server/Net/PMSingleBattleRegistry.cs
Client/Assets/Scripts/Server/Net/PMSingleBattleRegistry.cs.meta
Tools/PMUdpRouterCheck/            (整个目录)
Tools/PMUdpRouterTest/            (整个目录)
Tools/PMDsProbe/                  (整个目录，见 §7 备注)
```
（可选：`Server/Server/ClientUdp.cs` 删除后 `Server/Server/*.cs` 不再有 UDP 服务；`Server/Tool/IPManager.cs` 仍被 TCP/其他处使用 → **keep**。）

### 6.2 modify

| 文件 | 修改 |
|---|---|
| `Server/Server/Server.cs` | 删 `:40 LZJUDP.Instance.Init()`；`InitializeDedicatedServerLobby` 去掉 `options.Enabled` 早退（`:281-287`），使「未配 DS ⇒ 匹配显式失败」；删 `:290 NewChainEnabled=...` |
| `Server/Server/ServerConfig.cs` | 删 `UDPservePort`(7777)、`frameTime`(16)；保留 `TCPservePort`/`MaxRoom3_3Number`/`MaxTeam3_3Number` |
| `Server/Server/Client.cs` | 删 `:232 BattleManage.Instance.HandleClientDisconnect`；`:139` `isImportantTcpPack` 去掉 `ActionCode.BattleReview`；可选删 `socketIp`（`:65,75`） |
| `Server/Controller/Controllers.cs` | `StartFighting`(`:668`) 硬切新链；删 else 分支(`:679-693`)、`BuildMatchUsers`(`:645-666`)、`MatchUserInfo`(`:114-122`)；处理 `ClearSenceController`（§7-1） |
| `Server/DS/PMDsLobbyHost.cs` | 删/固化 `NewChainEnabled`(`:431`)；保留 `FromEnvironment`/`BuildFinalArguments`（必带 `-bootstrap`） |
| `Server/Controller/ControllerManger.cs` | 若按 §7-1 删 ClearSence 旧路径：去掉实例化(`:65`)、`_controllerDic.Add`(`:72`)、`_controllerNameDic.Add`(`:79`)、`RegisterAll` 形参与实参(`:81`/`:109`)、`Register(RequestCode.ClearSence, ...)`(`:145`)；文件头注释里的 B1-1 历史说明保留（属历史记录） |
| `Client/Assets/Scripts/Server/Boot/PMDsHost.cs` | 删旧分支（§5.2 全表），保留新链 |
| `Client/Assets/Editor/PMDsBuild.cs` | 生成 `run_ds.bat` 去掉「旧形态」分支(`:221,260-268`)，只留新链 |
| `Tools/PMUnityGlueCheck/PMUnityGlueCheck.csproj` | 删 `Server/Net/**` include(`:62`) 与文件头对应说明 |
| `Docs/plans/net-architecture-migration.md` | 删门禁命令两行(`:55-56` PMUdpRouterCheck/PMUdpRouterTest)；`§` 现状表里「旧战斗路径，待 P3' 处置」(`:30` 附近) 改为「已删除」 |
| `Server/AGENTS.md` | §2(UDP 7777/BattleReview)、§3.1(战斗 7 文件)、§3.2(ClientUdp)、§4(战斗主链路全链)、§5(追帧系统)、§6(NetSim)、§9(场景速查)、§13 需重写/删除 |
| `Server/Docs/ForClient.md` | 旧战斗链段落(`:44-56,100-110,143,155,194,214`) 需重写 |
| `BothSide.md` | `:9`「战斗 UDP 仍通过 MainPack / RequestCode.Battle」等旧协议承诺、§Ping/Pong、§NetSim、§BattleStart 补发、`BattleManage`/`BattleReview` 段落需重写 |
| `Client/Assets/AGENTS.md` | §1 末 BattleReview 承诺、§14/§15 中「旧链退役」口径更新（客户端旧链本身本次不动） |

### 6.3 keep（明确不要删）

```
Server/Program.cs, Server/Server/SocketProto.cs, Server/Server/FriendRoom.cs, Server/DAO/**, Server/Tool/**
Server/DS/**（PMDsLobbyHost/PMDsCoordinator/PMDsControlListener/PMDsProcess/PMDsBattleContentConfig/PMDsPlayerLedger）
Server/Controller/ControllerManger.cs 的显式注册表机制
Client/Assets/Scripts/Server/Boot/{PMNetBootstrap,PMClientSessionHost,PMDsSessionHost,PMDsLobbyAgent}.cs
Client/Assets/Scripts/PMNet/**, PMR3/**, PMMover/**, PMPrediction/**, PMProjectile/**, PMCombat/**, PMUnity/**
Tools/PMDsControlTest, PMDsLobbyTest, PMR3*, PMR4*, PMR5*, PMR6*, PMNet*, PMClientCheck, PMSharedConfigCheck, PMHeroDataCheck, PMNumericEquivalenceTest, PMBattleSimTest(冻结旧公式作基准，不依赖旧源码), PMServerSmokeTest
```
`FightPattern` / `MatchResult` / `MatchedPlayerEntry` / `BattleRoom` / `PlayerState` / `ActionCode.StartEnterBattle|Ping|Pong|BattleReview(枚举值本身)`：**保留**（新链仍用 `StartEnterBattle` 送 entry offer、`Ping/Pong` 大厅心跳、`BattleReview` 枚举值仍存在于生成物中）。

---

## 七、需要一并决策的两项（不阻塞本批，但会被本批「露出来」）

### 7-1 `ClearSenceController`（旧清场就绪）

- `Controllers.cs:26`（`ClearSenceController`）/ `:68`（`ClientSendClearSenceReady`）：先用 `PMDsLobbyHost.Instance.IsUidInMatch(uid)` 挡掉新局，再走 `BattleManage.Instance.TryGetBattleContextByUid(:81)` → 广播 `AllClearSenceReady`。
- 删 `BattleManage` 后这段编译不过 ⇒ 必须二选一：
  - **(A) 一并删**：整个 `ClearSenceController` + `ControllerManger` 注册 + `ActionCode.ClientSendClearSenceReady/AllClearSenceReady`（客户端 `ClearSenceManger.cs` / `RequestCode.ClearSence` 仍会发但服务端会记「未注册」日志），与 R6「旧链退役」一致。
  - **(B) 只删旧分支**：保留控制器但把 `BattleManage` 段改成直接 `return pack`（纯空转）。这属于「保留旧协议兼容」，与用户口径冲突。
- 建议 **(A)**，但它是独立动作，需要用户确认客户端 `UIStartMainPanel/ClearSenceManger` 是否也要同步（客户端旧链本次不在范围）。

### 7-2 proto/旧战斗消息的彻底下线（「不再保留旧协议兼容」的最大口径）

删完 §6 后，`SocketProto` 里下列内容在**服务端**变成零引用，但仍有外部引用 ⇒ 现在**不能**直接删：

| proto 内容 | 仍引用它的地方 |
|---|---|
| `ActionCode.BattleReady/BattleStart/BattlePushDowmPlayerOpeartions/BattlePushDowmAllFrameOpeartions/BattlePushDowmGameOver/ClientSendGameOver/BattleSetNetSimConfig/BattleReview` | 客户端旧链（`BattleManger.cs`/`UDPSocketManger.cs`/`RequestManger.cs`/`UIStartMainPanel.cs`）、`Tools/PMNetVerify/Program.cs:182,341,682,691`（`PMMainPack` ↔ `MainPack` 逐字节等价门禁）、`Tools/PMBattleSimTest` 注释 |
| `RequestCode.Battle` | 同上（客户端 + `PMNetVerify`） |
| `BattleInfo`/`BattleFrame`/`ClientMove`/`ClientAttack`/`HitEvent`/`PlayerStates`/`BattleServerUpdate`/`AttackAck`/`BattleNetSimConfig` 家族 | 客户端旧链 + `PMNetVerify` + `Client/Assets/Scripts/PMNet/Generated/SocketProto.PMNet.g.cs`（PM 侧镜像枚举/消息） |

结论：**proto 级下线是一个独立、更大的批次**（要同时改权威 proto 源 `ProtobufAndNotepad/Protobuf/SocketProto.proto`、重生成两端 + PMNet 镜像、退役客户端旧链、改写 `PMNetVerify` 的等价性口径）。本批只删服务端/DS 的旧实现，**不动 proto**。请用户确认是否把 proto 下线排进后续批次。

---

## 八、实测口径：旧模式入口如何硬切新链

现状（**已确认**，读码 + 文档一致）：

- `HYLD_PMNET_DS` 未设（默认）⇒ `ServerInitialize` 早退、`NewChainEnabled=false` ⇒ `StartFighting` 走 旧 `BattleManage.TryBeginBattle` ⇒ **旧链是默认路径**。
- `HYLD_PMNET_DS=1` 但 `HYLD_PMNET_DS_EXE/WORKDIR/BOOTSTRAP_DIR` 不全或 manifest 缺失/非法 ⇒ `Server.cs` 只记日志并 `return`，`NewChainEnabled` 仍为 `true` ⇒ 匹配走 `StartFightingDedicatedServer`，宿主未就绪时**显式失败并打日志**（`Controllers.cs:707-711`），不回退旧链。

硬切后的目标行为（本批实现）：

```
StartFighting(matchResult)
  → 恒走 StartFightingDedicatedServer
       host == null || !host.IsRunning  → 日志「新链不可用，整局失败（无旧链可回退）」并 return
       名册缺任一已认证 Client        → 整局拒绝（不缩编）
       其余                          → host.TryStartMatch → DS 拉起（必带 -bootstrap）
```
即：**旧模式入口不再存在**，只有「新链成功」与「新链显式失败」两个结果。`HYLD_PMNET_DS` 开关随之消失；`HYLD_PMNET_DS_EXE/WORKDIR/BOOTSTRAP_DIR/PORT_*` 从「可选」变为「必需」。

**验证方式（改完后执行，替换旧门禁）**：

1. `dotnet build Server/Server.csproj`（编译期证明旧符号零引用；`ControllerManger` 的显式注册表会在启动时暴露漏注册）。
2. `dotnet Tools/PMServerSmokeTest/.../PMServerSmokeTest.dll`（大厅 10/17 项，不依赖旧战斗；证明删旧链不伤登录/好友/房间/匹配广播）。
3. `dotnet Tools/PMDsLobbyTest/...`、`dotnet Tools/PMR3IntegrationTest/...`、`dotnet Tools/PMDsControlTest/...`（新链控制/结果闭环，含 G87「不伪造 BattleReview」）。
4. `dotnet build Tools/PMUnityGlueCheck -c Release` + `dotnet build Tools/PMClientCheck -c Release`（`PMDsHost` 改完后仍可编译；后者覆盖 `Server/**` 与 `Net/**` glob）。
5. 反向验证（不能只看绿）：在 `StartFighting` 里临时注入「不设宿主」分支，确认日志出现「整局失败（无旧链可回退）」而不是静默开局；再撤掉注入。
6. 真机（用户侧）：`HYLD_PMNET_DS=1` + 双客户端匹配 → 观察 `[PMDsMatch] 新链开局已提交`，且 `server.log` 中**不再有** `[LZJUDP]`/`StartFighting 创建战斗成功`。

---

## 九、需要更新/移除的旧门禁

| 门禁 | 处置 | 依据 |
|---|---|---|
| `Tools/PMUdpRouterCheck`（编译门禁，netstandard2.0+C#7.3，包含 `Server/Net/**`） | **remove** | 覆盖对象整体被删 ⇒ glob 为空，门禁退化为「什么都不编」 |
| `Tools/PMUdpRouterTest`（44 项 UDP 回环） | **remove** | 测的是即将删除的路由层 |
| `Tools/PMDsProbe`（真实 4 步探针） | **remove**（或改造为「新链 bootstrap 探针」） | 其四步全部是旧 `MainPack` 裸路由语义；新链是分帧 + 控制通道，探针口径不通用 |
| `Docs/plans/net-architecture-migration.md` 验收命令块（`:55-56`） | **update** | 删掉上述两条命令 |
| `Tools/PMUnityGlueCheck/PMUnityGlueCheck.csproj` `Server/Net/**` include + 头注释 | **update** | 文件被删后 include 应删除；头注释里「P3'-1 战斗 UDP 路由层」描述失效 |
| `Tools/PMClientCheck/PMClientCheck.csproj`（`Server/**` glob，含 `PMDsHost`/`Net`） | **keep（无需改动）** | 通配符对已删文件不报错；它是 `PMDsHost` 改动的**主要编译门禁** |
| `Tools/PMNetVerify`（`PMMainPack` ↔ `MainPack` 逐字节等价） | **keep** | 它不依赖旧战斗源文件，只依赖 proto 生成物；proto 未下线前仍然有效（见 §7-2） |
| `Tools/PMBattleSimTest`（旧服公式冻结重写 + 345715 项等价） | **keep** | `OldServer.*` 是**冻结基准**，不编译旧源码；删旧链后它仍是「PMBattleSim 没被改坏」的唯一证据 |
| `Tools/check_client_authority_writes.py` / `check_hero_table_shape.py` / `check_hero_id_alignment.py` / `check_cs_braces.py` | **keep** | 只扫客户端/共享表，与旧服无关（`check_client_authority_writes.py` 表头文档引用 `PackPlayerStates`，属历史说明） |
| `HyldDS/run_ds.bat` 旧形态分支（gitignore，由 `PMDsBuild.cs` 生成） | **update 生成器** | 只改产物会被下次构建覆盖 |
| `Server/AGENTS.md` §14「旧BattleController仅旧链基线」 | **update** | 删旧链后该表述应改为「旧链已退役」 |

---

## 十、固定结论段

### 已确认

1. 五组目标的**全部可达调用与外部引用**已在 §1–§5 逐条给出（含文件:行）。服务端旧战斗组 + `LZJUDP` 是**自闭合子图**：外部入边只有 `Server.cs:40`、`Controllers.cs:81/686`、`Client.cs:232` 三条（另加文档/注释）。
2. `Server.csproj` 使用 SDK 默认 glob（未设 `EnableDefaultCompileItems=false`），删源文件不会破坏工程；**没有任何 Tools 工程编译 `Server/Server/Battle*.cs`**（`PMNumericEquivalenceTest` 里的 `Server/Server/HeroConfig.cs` 仅出现在注释中）。
3. 匹配链的**唯一二分点**是 `Controllers.cs:668 StartFighting`；`NewChainEnabled` 只有 1 写 1 读。
4. DS 启动**必带** `-bootstrap`（`PMDsLobbyHost.cs:1601`）⇒ `PMDsHost` 无 bootstrap 分支在生产路径不可达。
5. `PMUdpRouter` / `PMSingleBattleRegistry` 的非 DS 引用只有 `Tools/PMUdpRouterTest`。
6. `Client.Close` 的断线双通知已定位（旧 `:232`、新 `:236`）。

### 高概率推断（依据与置信度）

- **A｜`PMDsHost` 无 bootstrap 分支当前无生产调用者**：依据 `PMDsLobbyHost.BuildFinalArguments` 恒带 `-bootstrap` + `run_ds.bat` 的两形态注释 + 契约 §7.4。置信度 **高**。残留风险：用户/脚本手工 `HyldDS.exe -server -port <n>`（不带 `-bootstrap`）仍会进入该分支——删除后此类手工调用会直接走新链并因缺 bootstrap 失败退进程（`PMDsHost` 新链失败语义就是 `Application.Quit(1)`），这是**预期的硬切**。
- **B｜删旧链不会破坏新链**：依据新链依赖面（`PMNet/**`、`PMR3/**`、`PMCombat/**`、`Server/DS/**`、`SocketProto` 的 `MainPack.Str` + `StartEnterBattle`/`Ping`/`Pong`）与旧链完全不相交；`PMDsHost` 保留项已在 §5.3 列出。置信度 **高**。风险点：`PMDsHost.Update` 旧分支里 `DrainPendingLogs()` 被新链分支复用，删除时若整段替换会丢掉日志输出（已在 §5.3 显式标注）。
- **C｜`ClearSenceController` 旧路径必须动**：依据 `Controllers.cs:81` 直接调 `BattleManage`。置信度 **确定**（编译期硬约束），但处置方案（删控制器 vs 留空转）需用户决策。
- **D｜proto 不能随本批一起删**：依据 `PMNetVerify`（`PMActionCode.BattlePushDowmAllFrameOpeartions`）+ 客户端旧链 + `SocketProto.PMNet.g.cs` 镜像。置信度 **高**。

### 无法确定（缺少证据）

1. **客户端旧战斗链是否同一批退役**：任务非目标已排除 `Client/Assets/Scripts/Manger/Battle/**`、`UDPSocketManger.cs`、`ConstValue.ServiceUDPPort`（7777）与旧回放 UI。若不同批，服务端删 7777 后客户端旧链成为死代码但保留编译（`PMClientCheck` 仍会编它）。→ 需用户给出批次边界。
2. **`HYLD_PMNET_DS` 开关是否保留为「显式禁用新链」的安全阀**：用户口径是「删旧代码」，但删掉开关意味着服务端在未配 DS 环境（如纯大厅联调）下无法匹配。→ 需用户确认是否需要「无 DS 配置 ⇒ 匹配失败」。
3. **`Tools/PMDsProbe` 是删除还是改造**：它对新链（分帧 + 控制通道 + bootstrap）不可用；是否要一个等价的新链探针取决于后续实机验收方式。
4. **`PMUnityGlueCheck` 是否需要新增一个「新链 DS 宿主只读门禁」**：删掉 `PMUdpRouterCheck` 后，`PMDsHost` 的编译覆盖只剩 `PMUnityGlueCheck`（桩件）。是否需要保留一个「无 UnityEngine 的核心门禁」需用户决定。
5. **proto 下线批次是否包含 `BattleReview` 枚举值**：客户端 `UIStartMainPanel.cs:67/79` 与 `RequestManger.cs:55` 仍在注册/消费它，虽服务端已不发送。

### 已检查范围

- 必读文档（完整读）：`AGENTS.md`、`Server/AGENTS.md`、`Client/Assets/AGENTS.md`、`Docs/plans/net-r6-combat-contract.md`；抽读：`Docs/plans/net-architecture-migration.md`（卷首/状态/验收命令/门禁清单）、`Docs/plans/net-r3-control-contract.md:62`（§7.4 口径）、`HyldDS/run_ds.bat`、`Server/Docs/ForClient.md`（引用面）、`BothSide.md`（旧协议承诺段）。
- 源码（完整读）：`Server/Server/Server.cs`、`Client.cs`、`ClientUdp.cs`、`BattleManage.cs`、`ServerConfig.cs`、`Server/Controller/ControllerManger.cs`、`Server/Controller/Controllers.cs`（ClearSence + Matching 段）、`Client/Assets/Scripts/Server/Boot/PMDsHost.cs`、`PMNetBootstrap.cs`、`Server/Program.cs`、`Server/DS/PMDsLobbyHost.cs`（options + 契约段）。
- 抽读：`Server/Server/Battle.cs`（构造/Handle/实例化外部依赖）、`BattleController.Bullets.cs`、`BattleController.Network.cs`（外部符号引用）、`BattleContext.cs`。
- 工程/门禁（读 csproj + 引用面）：`Server/Server.csproj`、`Tools/PMUdpRouterCheck|PMUdpRouterTest|PMDsProbe|PMUnityGlueCheck|PMClientCheck|PMNumericEquivalenceTest` 的 `.csproj` 与 `Program.cs` 头部、`Client/Assets/Editor/PMDsBuild.cs`（run_ds 生成段）。
- 检索方式：`fffind`/`ffgrep`（绝对路径，`D:/UGit/hyld-master` 独立 aux 索引）作首搜；**零引用/完备性判定改用 `rg --no-ignore`**（原因：`ffgrep` 是 partial 语义——15s 冷启动等待、10s grep 预算、gitignored 文件（`HyldDS/`、`bin/`、`obj/`）不进索引，而本任务要断言「某符号在 Source/Server/Tools 内零引用」；同时工作区 root 是 `D:/UE_Project/ProjectMecury`，仓库相对路径索引不覆盖目标仓库）。已排除 `bin/`、`obj/`、`Library/`、`Docs/plans/_r*.md` 等历史报告。
- 未检查（按非目标边界停止）：`Client/Assets/Scripts/Manger/Battle/**` 内部实现、`Client/Assets/**` 资产/prefab、Unity 旧回放 UI 细节、其它游戏工程、proto 生成链 `ProtobufAndNotepad/**` 的具体改法（仅确认引用面）。

### 建议下一步（最小补充查询或运行时验证）

1. **先落 3 个决策**（其余可机械执行）：(a) `ClearSenceController` 删或留空转（§7-1）；(b) `HYLD_PMNET_DS` 开关是否保留；(c) proto 下线是否另立批次（§7-2）。
2. **按 §6.2 顺序改**（建议顺序：`Server.cs` → `Client.cs` → `Controllers.cs` → `ControllerManger.cs` → 删 `Battle*`/`ClientUdp.cs` → `PMDsHost.cs` → `PMDsBuild.cs` → 门禁目录 → 文档），每步后跑 `dotnet build Server/Server.csproj`。
3. **删完后跑 §8 的 6 条验证**，其中第 5 条（反向注入）必做——否则「旧链已删」与「旧链被静默保留」在绿门下无法区分。
4. 若需确认「新链默认路径」：不设任何 `HYLD_PMNET_DS*` 环境变量启动服务端，发起匹配，确认 `server.log` 出现「新链不可用，整局失败（无旧链可回退）」且**没有** `StartFighting 创建战斗成功`。
