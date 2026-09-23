# 旧服务端战斗链删除报告（契约 A 组）

范围：`D:/UGit/hyld-master`，只动契约 `Docs/plans/net-legacy-retirement-contract.md` §A 的服务端边界。
本报告是本次唯一的白名单外新增文件（任务指定的报告落盘点）。
未做：`git reset/checkout`、任何提交/推送/SVN 写入、未启动服务端、未改 proto、未改客户端、未改主计划文档。

必读文档（已完整读）：`AGENTS.md` → `Server/AGENTS.md` → `Client/Assets/AGENTS.md` →
`Docs/plans/net-legacy-retirement-contract.md`（全文）→ `Docs/plans/_legacy_server_removal_survey.md`（删改列表与外部入边）
→ `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`。

---

## 0. 摘要

服务端旧战斗仿真子图（7 个 `Battle*` 文件 + `LZJUDP`/UDP 7777）已**整文件删除**（不是注释/ifdef 隐藏），
3 处外部入边全部清掉，匹配入口硬切为「只有 DS 一条链」（不再有二分点），
`ServerConfig` 去掉 `UDPservePort/frameTime`，`Client` 去掉旧回放重要帧分支与旧断线通知，
旧 `ClearSenceController` 与 `MatchUserInfo/BuildMatchUsers` 随旧链删除，
`PMDsLobbyHostOptions.FromEnvironment()` 改为 `Enabled` 恒 true（旧开关 `HYLD_PMNET_DS` 被忽略并打日志），
`PMDsLobbyHost.NewChainEnabled` 静态选链开关删除。
`dotnet build Server` 0 error 后三个新链门禁全绿：PMDsLobbyTest 268/268、PMDsControlTest 623/623、PMR3IntegrationTest 143/143。

---

## 1. 删前记录（节选：`git status --short` + 基线）

- 基线 commit：`2084a67178eca1f3e9ae7c76eabfdf34ef9274e9`
- 删前工作区已有他组未提交改动（`Client/**`、`Docs/plans/**`、`Server/AGENTS.md`、`Server/Server.csproj`、若干 `Tools/**`），本次**未触碰**这些文件。
- 删前 `git hash-object`（本次范围内的文件）：

| 文件 | 删前 hash | 行数 |
|---|---|---|
| Server/Server/Battle.cs | `da975719db67bd8519e5e686f5889249e3dfe8a0` | 762 |
| Server/Server/BattleController.Bullets.cs | `6dfaa627aa27ff37241edcab1cab8cdcb11d14e0` | 324 |
| Server/Server/BattleController.Network.cs | `90209c9fb3ea00063d833dc50e7932cdc4ff4062` | 927 |
| Server/Server/BattleManage.cs | `cccb1ec11401bcabbed144cebb6c46eb3deb0a5a` | 259 |
| Server/Server/BattleContext.cs | `cb792addd82ef38e25f5eb2ab48595e3af9ac306` | 36 |
| Server/Server/ServerBullet.cs | `bee38bae7405fb0c1771768816f9a76c443bb828` | 19 |
| Server/Server/ServerVector3.cs | `e6fbbcaf2245b5f650ed90d5e656fdc603fc750a` | 30 |
| Server/Server/ClientUdp.cs | `6714fd8a8ebcdb15c5d58e144450c3fa194eec39` | 846 |
| Server/Server/Server.cs | `2763f89963a56414462354ae3aaa0798183cd992` | 442 |
| Server/Server/Client.cs | `f8d3175c9b86c6bb3448380ea02b4132d86cb1ab` | 267 |
| Server/Server/ServerConfig.cs | `3e3d06b7787989d19143e7b2318e4d5486212c98` | 14 |
| Server/Controller/Controllers.cs | `c2e9640f984704840b2241829de34e44cc4327e1` | 1277 |
| Server/Controller/ControllerManger.cs | `18fb2bb4d877c7606724b8fa0b6fa1bb4e7caed9` | 297 |
| Server/DS/PMDsLobbyHost.cs | `64e8d36a19035892b7cf1f3383df3f173e4c84e7` | 1839 |
| Tools/PMDsLobbyTest/Program.cs | `76aa8bdfd31a30406e39223eafca33e098439d03` | 1913（未改） |

删后 hash：

| 文件 | 删后 hash | 行数 |
|---|---|---|
| Server/Server/Server.cs | `714799e2f5ae5e2970b9f93199e5a68e9c64fd8e` | 448 |
| Server/Server/Client.cs | `7652c709ddf5892f79c8522f1ad5808e2abcd30d` | 262 |
| Server/Server/ServerConfig.cs | `6f76148750978879b4056fd80e36404911015e31` | 11 |
| Server/Controller/Controllers.cs | `10cecde0b3e1253f176795c179231e4b72e85687` | 1131 |
| Server/Controller/ControllerManger.cs | `1b032682855e08079718bfb781bbfb823e1a718b` | 293 |
| Server/DS/PMDsLobbyHost.cs | `ce12a8bba72c13a60311479bbfca90fab6265202` | 1848 |

> 删文件用 `git rm`（只 stage，无 commit）。因此 `git status` 里 8 个删除是 `D `（已 staged），
> 其余修改是 ` M`（未 staged）。该暂存是未要求的副作用，主侧已仅撤销8条暂存并保留工作区删除；禁止无范围暂存，不代提交。

---

## 2. 实际删除（8 个整文件，仅契约给定路径）

```
Server/Server/Battle.cs
Server/Server/BattleController.Bullets.cs
Server/Server/BattleController.Network.cs
Server/Server/BattleManage.cs
Server/Server/BattleContext.cs
Server/Server/ServerBullet.cs
Server/Server/ServerVector3.cs
Server/Server/ClientUdp.cs            ← LZJUDP（UDP 7777 服务端监听）
```

- `Server/Server.csproj` 用 SDK 默认 glob（未设 `EnableDefaultCompileItems=false`），删源文件不影响工程。
- **没有任何 Tools 工程编译这 8 个文件**：`rg "Server/Server" Tools/*/*.csproj` 只命中
  `PMNumericEquivalenceTest.csproj` 的一行注释。
- 未保留任何 `BattleManage`/`LZJUDP` stub；旧类型已从编译面彻底消失（不是注释或 `#if` 隐藏）。

---

## 3. 实际修改（6 个文件）

### 3.1 `Server/Server/Server.cs`
- 删构造里的 UDP 线程启动：`// 创建udp线程` + `LZJUDP.Instance.Init();`（原 :40，**外部入边 1/3**）。
- `InitializeDedicatedServerLobby()`：
  - 删 `PMNet.Control.PMDsLobbyHost.NewChainEnabled = options.Enabled;`；
  - 新增「旧开关被忽略」的显式日志（`options.DeprecatedLegacySwitchIgnored/Value`）；
  - `if (!options.Enabled)` 保留，但语义改为「宿主被显式禁用 ⇒ 匹配显式失败（无旧链可回退）」，
    不再声称「保持旧匹配/旧战斗链路」；
  - `IsLaunchConfigured` 与内容 manifest 的失败日志去掉 `HYLD_PMNET_DS=1` 前缀，改为「DS 启动配置不完整 / 不启动宿主 ⇒ 匹配显式失败」；
  - 文档注释同步改写（原文引用 `NewChainEnabled`）。
- **不猜用户目录**：部署路径/端口范围仍全部来自环境变量；`PMDsBattleContentConfig`（只读文件，未改）
  默认从仓库相对路径 `Client/Assets/Resources/PMNet/BattleContentV1.json` 推导，并做存在性 + 强校验，
  也支持 `HYLD_PMNET_CONTENT_MANIFEST` 绝对路径覆盖；缺失/非法只 `return`（不启动宿主）。

### 3.2 `Server/Server/Client.cs`
- 删旧断线通知：`BattleManage.Instance.HandleClientDisconnect(_server, UID);`（原 :232，**外部入边 2/3**）；
  保留 `_controllerManger.CloseClient`（房间/匹配清理）、`BordCaseToFriendLogout`、
  `lobbyHost.NotifyClientDisconnected`、`RestorePlayerOnline`。
- `isImportantTcpPack` 去掉 `ActionCode.BattleReview`（服务端不再产生该包，保留即死条件）。
- 删随 `BuildMatchUsers` 变成零引用的 `Client.socketIp`（属性 + 赋值）。

### 3.3 `Server/Server/ServerConfig.cs`
- 删 `public const int UDPservePort = 7777;` 与 `public const int frameTime = 16;`。
- 保留 `TCPservePort`、`MaxRoom3_3Number`、`MaxTeam3_3Number`（三者在 `Program.cs`/`BattleRoom` 仍有引用）。

### 3.4 `Server/Controller/Controllers.cs`
- 删整个 `ClearSenceController`（旧清场就绪，**外部入边 3/3** 之 `BattleManage.TryGetBattleContextByUid`），
  连带 `_dic_ClearFinish` 与 `BroadcastAllClearSenceReadyToPlayers`。
- 删 `MatchUserInfo` struct 与 `BuildMatchUsers`（旧链专用；`socketIP` 随之无人消费）。
- `StartFighting` 硬切：

```csharp
private void StartFighting(Server server, MatchResult matchResult)
{
    StartFightingDedicatedServer(server, matchResult);
}
```
  —— 方法体内**只剩一条语句**，无 `NewChainEnabled` 判断、无 `else` 分支、无旧 `TryBeginBattle` 调用。
- `StartFightingDedicatedServer` 硬约束不变：名册缺任一已认证 `Client` ⇒ 整局拒绝（不缩编）；
  宿主 `null`/未 `IsRunning` ⇒ 记「DS 宿主未就绪，整局失败（已无旧链可回退）」并返回。
- 保留：登录/好友/房间/匹配全部逻辑、`MatchResult/MatchedPlayerEntry/FightPattern/BattleRoom`、
  新链 `StartEnterBattle` 入局 offer、`Ping/Pong`。

### 3.5 `Server/Controller/ControllerManger.cs`
- 删 `ClearSenceController` 的实例化、`_controllerDic.Add`、`_controllerNameDic.Add`、
  `RegisterAll` 形参/实参、`Register(RequestCode.ClearSence, ActionCode.ClientSendClearSenceReady, ...)`；
  留注释说明该组合已退役、客户端若仍上行会落到 `[RPC][未注册]`（不静默当旧链）。
- 显式注册表机制本身保留（原样）。

### 3.6 `Server/DS/PMDsLobbyHost.cs`
- `PMDsLobbyHostOptions.Enabled`：文档改为「production 恒 true；保留只为测试可显式禁用宿主（此时匹配显式失败）」。
- 新增 `DeprecatedLegacySwitchIgnored` / `DeprecatedLegacySwitchValue`（仅用于启动日志）。
- `FromEnvironment()`：`options.Enabled = true;`（**不再读 `HYLD_PMNET_DS` 作开关**）；
  若该变量被显式设置，则记录值供 `Server` 打「已忽略」日志。显式 `HYLD_PMNET_DS=0` **不能**恢复旧链。
- 删 `private static bool ReadBool(...)`（失去唯一调用点）。
- 删 `public static bool NewChainEnabled { get; internal set; }`（production 写入点与唯一读取点已删；
  见 §6 说明）。`Instance` 文档由「未启用时为 null」改为「未启动时为 null」。
- `IsUidInMatch` 文档去掉「旧 ClearSence/旧路由用它」表述（旧调用点已删，方法本身保留为只读查询 API）。

---

## 4. 明确保留（未动）

- `Server/Program.cs`、`Server/Server/SocketProto.cs`（proto 产物，本组不可碰）、
  `Server/Server/FriendRoom.cs`、`Server/DAO/**`、`Server/Tool/**`（含 `IPManager`，TCP 仍在用）。
- `Server/DS/**` 的 `PMDsCoordinator/PMDsControlListener/PMDsProcess/PMDsBattleContentConfig/PMDsPlayerLedger`
  与新链控制面语义（除 §3.6 的 options 部分）。
- 全部登录 / 好友 / 房间 / 匹配 / `CloseClient` 广播 / 新 DS 开局与结果回收。
- `FightPattern`、`MatchResult`、`MatchedPlayerEntry`、`BattleRoom`、`PlayerState`、
  `ActionCode.StartEnterBattle|Ping|Pong|BattleReview(枚举值)`（生成物内仍存在，供后续 proto 批次统一裁）。

---

## 5. 三处外部入边的清理证据

survey 给出的旧战斗组外部入边（代码）只有三条，已全部处理：

| # | 位置 | 处置 | 证据 |
|---|---|---|---|
| 1 | `Server/Server/Server.cs` `LZJUDP.Instance.Init()` | 删除 | §3.1 |
| 2 | `Server/Server/Client.cs` `BattleManage.Instance.HandleClientDisconnect` | 删除 | §3.2 |
| 3 | `Server/Controller/Controllers.cs` `BattleManage.Instance.TryGetBattleContextByUid`（`ClearSenceController`） | 随控制器整体删除 | §3.4 |

另加一条同类外部读取点：`Controllers.cs` 旧 `else` 分支的 `BattleManage.Instance.TryBeginBattle` —— 随 `StartFighting` 硬切删除。

---

## 6. 旧开关与选链语义

- `ServerConfig.UDPservePort`（7777）删除后，**服务端不再监听 7777**；新链每局端口来自
  `HYLD_PMNET_DS_PORT_FIRST/LAST`（默认 7801–7899），DS 启动参数必带 `-bootstrap`
  （`PMDsLobbyHost.BuildFinalArguments`，未改）。
- `HYLD_PMNET_DS` 已不是开关：`FromEnvironment` 恒置 `Enabled=true`，显式 `0` 只会产生一条
  「忽略旧开关」日志。production 只可能走新链。
- `PMDsLobbyHost.NewChainEnabled` **删除**的取舍说明：契约 §A 允许把它作为「内部服务可用性测试 API」保留，
  但本次删除后它在全仓（含 `Tools/**`、`Client/**`）已**零引用**，且其唯一历史语义就是「选回旧链」。
  为免留下一个可被再次接线的旧选链开关，选择删除；`Options.Enabled` 按任务要求保留，用于测试构造
  「显式禁用宿主」场景。已用 `rg --no-ignore` 全仓确认无其它引用（详见 §8）。

---

## 7. 硬切后唯一路径的静态完备性

1. `BuildMatchUsers`、`MatchUserInfo`、`ClearSenceController`、`BattleManage`、`LZJUDP`、
   `BattleController*`、`BattleContext`、`ServerBullet`、`ServerVector3` 全部**已从编译面删除**，
   因此「静默保留旧链」在结构上不可能：任何调用都会是编译错误，而 `dotnet build Server/Server.csproj`
   为 **0 error**。
2. `StartFighting` 方法体只有一条语句，指向 `StartFightingDedicatedServer`；后者在宿主缺失时
   只记录失败日志并 `return`（不打旧链分支）。
3. 反向注入验证（survey §8 第 5 条）需要「启动服务 + 发起匹配」，属任务明确禁止的
   「运行服务」范畴，本组**未执行**；以 §7.1/7.2 的静态证据 + §8 的终判扫描替代。这是本组唯一未做的验证项。

---

## 8. 验证（编译 + 门禁 + 终判扫描）

### 8.1 编译（独立临时输出，不覆盖运行中的 dll）

```
dotnet build Server/Server.csproj -o /tmp/hyld-legacy-server-build
→ 已成功生成。0 个错误（8 个警告：NU1701 BouncyCastle/iTextSharp、CS8981 pb/pbc/pbr/scg，均为既存告警）
→ Server.dll 输出到 C:\Users\luomingcong\AppData\Local\Temp\hyld-legacy-server-build\
→ Server/bin/Debug/net8.0/Server.dll 的 mtime 仍为 2026-09-20 21:08（未被触碰）
```
（`-o` 指向系统临时目录，因此不会覆盖可能在运行中的 `Server/bin/...`；`Server/obj` 为 gitignore 中间产物。）

### 8.2 门禁（全部 0 失败）

| 门禁 | 结果 |
|---|---|
| `Tools/PMDsLobbyTest`（Release 编译 + 运行） | **全部通过：268 项检查（含负向输入 28 项），0 项失败** |
| `Tools/PMDsControlTest`（Release 编译 + 运行） | **全部通过：623 项检查（含负向输入 68 项），0 项失败** |
| `Tools/PMR3IntegrationTest`（Release 编译 + 运行） | **全部通过：143 项检查，0 项失败** |

三个工程均**编译通过**（另组在飞的 `PMR5Projectile.cs` 等也已随 `PMR3IntegrationTest` 一起编过，无报错），
无需为其临时改任何东西。速度、退出码均为 0。

### 8.3 Options 环境测试检查（结论：无需改动）

任务提示「Options 环境测试若旧 optin 断言过时按新语义改」。实测：

- 全 `Tools/**` 中 `FromEnvironment` **零调用**；`HYLD_PMNET_DS` 字面量零出现；
- `.Enabled` 只有 3 处，且全是赋值 `options.Enabled = true;`（PMDsLobbyTest:1316、PMR3IntegrationTest:772、PMR3UnitySmoke:1128）；
- 没有任何用例断言 `Enabled` 默认 false 或「未启用则走旧链」。

⇒ `Tools/PMDsLobbyTest/Program.cs` 虽然在本组白名单内，但**无需修改**（hash 保持
`76aa8bdfd31a30406e39223eafca33e098439d03`）。

### 8.4 引用完备终判（`rg --no-ignore`，限定 `Server/**/*.cs`，排 `bin/obj/log`）

理由：需对「已删类型在 Server 内零引用」做**完备性**判定。`ffgrep` 是 partial 语义（冷启动等待、grep 时间预算、
gitignored 文件不进索引），且本会话工作区 root 是 `D:/UE_Project/ProjectMecury`，不覆盖目标仓库，故此处按任务许可退回
`rg --no-ignore`。

```
rg -n --no-ignore -g 'Server/**/*.cs' -g '!**/bin/**' -g '!**/obj/**' -g '!**/log/**' \
   "\b(BattleManage|LZJUDP|BattleController|BattleContext|ServerBullet|ServerVector3|BuildMatchUsers|MatchUserInfo|ClearSenceController|NewChainEnabled)\b"
```
命中 5 处，**全部是注释**（无一条活代码）：

| 位置 | 内容 |
|---|---|
| `Server/Controller/Controllers.cs:546` | 「不生成第二个权威（旧 BattleController）」——历史说明 |
| `Server/Controller/Controllers.cs:556` | 「不再有旧链可回退，也没有旧 BattleManage/_uidToBattleIds 路由可写」——否定式说明 |
| `Server/Server/Client.cs:228` | 「旧 BattleManage 断线通知已删除」——历史说明 |
| `Server/DS/PMDsLobbyHost.cs:368` | 「不调用 BattleManage、不写 _uidToBattleIds」——否定式说明 |
| `Server/DS/PMDsCoordinator.cs:40` | 「不接匹配入口、不改旧 BattleManage/匹配流程」——否定式说明（本组未改该文件） |

补充扫描（`UDPservePort|ServerConfig.frameTime|socketIp|ActionCode.BattleReview|ClearSence`）：仅剩
`Server/Server/SocketProto.cs` 的生成枚举值（proto 产物，本组不可碰）、`ControllerManger.cs:31` 的历史故障说明注释、
`ControllerManger.cs:140` 的退役说明注释。**无活代码引用。**

`Client/**` 与 `Tools/**` 对已删符号的命中全部是注释（`PMBattleSimTest` 的 `OldServer.*` 是**冻结重写**的基准，
不编译旧源码；`PMBattleSimTest.csproj` 的 `BattleController.Network.cs:800-826` 等只是指针注释）。

---

## 9. 剩余旧名清单（活代码 vs 历史注释）

**活代码：无。** 旧战斗组类型/符号在 `Server/**` 已不存在。

**历史注释（保留，不构成引用）**：
`Server/Controller/Controllers.cs:546,556`；`Server/Server/Client.cs:228`；
`Server/DS/PMDsLobbyHost.cs:368`；`Server/DS/PMDsCoordinator.cs:40`；
`Tools/PMBattleSimTest/Program.cs` 多处 `BattleController.*.cs:行号` 指针注释；
`Client/Assets/Scripts/Shared/PMBattleSim.cs:10-11` 与 `Client/Assets/Scripts/PMCombat/**`
的「旧实现对应物」注释；`Server/Controller/ControllerManger.cs:31`（B1-1 历史故障记录）。

**proto 生成物内的枚举/消息名**（`Server/Server/SocketProto.cs` 的 `ClearSence=8`、
`ClientSendClearSenceReady=35`、`AllClearSenceReady=36`、`BattleReview=39` 等）：
仍是**活代码**（生成代码），但本组按任务边界**不可碰 proto**，留待主侧统一裁（契约 §E）。
服务端已无任何 handler/发送点使用它们。

---

## 10. 部署缺口与行为变化（需运维/联调知晓）

1. **DS 部署配置从「可选」升级为「必需」**：`HYLD_PMNET_DS_EXE` / `HYLD_PMNET_DS_WORKDIR` /
   `HYLD_PMNET_DS_BOOTSTRAP_DIR` 任一缺失（或端口范围非法）⇒ 宿主不启动 ⇒ **匹配显式失败**，
   且**没有旧链可回退**（这正是硬切目标）。
2. **内容 manifest 必需**：默认 `Client/Assets/Resources/PMNet/BattleContentV1.json`（或
   `HYLD_PMNET_CONTENT_MANIFEST` 指定绝对路径）缺失/非法/摘要撞保留值 ⇒ 同样拒绝拉局。
   代码**不猜**任何用户目录。
3. **`HYLD_PMNET_DS` 失效**：置 1/0/不设都不再改变链路；服务端日志会出现「忽略旧开关 …」。
4. **不再有 UDP 7777**：服务端不监听 7777；旧客户端（仍硬编码 7777）连不上旧 UDP 是预期结果。
5. **不再有 `BattleReview`**：服务端不发回放包；`Client.cs` 也不再把它当重要帧（该判定只影响服务端
   `[TCP_SEND][Queued]` 日志，属死条件清理）。客户端仍保留注册
   （`UIStartMainPanel.cs:67/79`、`RequestManger.cs:55`、`HYLDManger.AddBattleReview/GetBattleReview`，
   属客户端批次 B/D 范围，本组未动）——服务端不发后它们是惰性注册，不会收到包。
6. **旧 `ClearSence` 上行变成未注册请求**：服务端只记录 `[RPC][未注册]`，不再广播 `AllClearSenceReady`。
   **唯一会被删除影响到的调用面已证明不再是活路径**：
   - 客户端只有在**加载战斗场景**时才会等待 `AllClearSenceReady`
     （`ClearSenceManger.AsyncLoadScene`：`if (scene != SceneConfig.battleScene) break;`，
     只有 `battleScene` 分支才 `WaitUntil(UISliderPanel.IsCanEnterBattle)`）；
   - 发往 `battleScene` 的那次调用原本在 `UIMatchingPanel` 旧分支，**已由客户端组移除**
     （现 `UIMatchingPanel.cs:62` 只剩一行注释；新链走 `PMClientSessionHost.Enter(offer)`，不读 ClearSence）；
   - 剩余 `ClearSenceManger.LoadScene(SceneConfig.mainScene)`（旧战斗场景 `HYLDGameOver` 收尾回大厅）
     走的是 `scene != battleScene` 的 `break` 分支，**不等待** `AllClearSenceReady`，因此不受影响。
   `UISliderPanel`/`ClearSenceManger` 源码仍在客户端保留（属 B/D 组清理），本组未修改。
7. **旧断线语义**：局内断线只经 `PMDsLobbyHost.NotifyClientDisconnected`（中止该局，不伪造胜利）；
   旧 `BattleManage.HandleClientDisconnect` 已不存在。
8. 遗留：`Tools/PMBattleSimTest` 的注释指针指向已删文件（仅注释，功能不受影响）；
   `Server/AGENTS.md` / `Docs/plans/net-architecture-migration.md` 等文档仍描述旧链，
   属契约 §C/§E 与文档批次，本组白名单未包含，**未修改**。

---

## 11. 边界与未做

- 未改 proto（含 `ProtobufAndNotepad/**`、`Server/Server/SocketProto.cs`）——留待主侧统一删。
- 未改客户端任何文件（`Client/**` 属 B/D 组）。
- 未改 `Server/AGENTS.md`、`Docs/plans/net-architecture-migration.md`、`BothSide.md`（不在白名单）。
- 未改 `Server/Server.csproj`、`Server/AGENTS.md`（工作区里它们的 `M` 是本次开工前既存的他组改动）。
- 未做父目录/相邻文件推断；`Server/DS/PMDsBattleContentConfig.cs` 等仅只读引用。
- 未提交、未 `git reset/checkout`、未启动服务端。
