# R3-B Unity 组（PMR3 声明产物 / DS 宿主 / 客户端入口 / 参数 / 构建脚本）实施报告

> 范围：`D:/UGit/hyld-master`。契约与冻结接口：`Docs/plans/net-r3-control-contract.md` §7（本轮未改契约）。
> 本文件只记录本组的实施与实测证据；阶段状态以 `Docs/plans/net-architecture-migration.md` 为准。
> **T42（真实 UnityDS + 两客户端）保持 PENDING_USER**：本轮未打开 Unity、未编译 Unity、未抢编辑器锁。

## 1. 摘要

按契约 §7.4 交付了「纯 PMR3 声明 + 固定 API + Boot 宿主 + 参数面 + 客户端切服 + 构建脚本 + 门禁」，全部新增/修改文件都在本组 writePaths 内。

- **声明与生成**：`PMR3Player`（`Uid`/`ProbeCount` 两个纯字段复制 + `ServerProbe`(Reliable+ForceValidate) + `ClientEcho`(Reliable)），由**外部 PMNetGen** 生成 `Generated/PMNet.PMNet.R3.PMR3Player.g.cs` 与 `Generated/PMNetGeneratedRegistry.g.cs`，ID 锁用**专用** `Docs/plans/pmnet-r3-ids.json`（未动共享锁 `pmnet-ids.json`）。`PMNetGen --decl-check` 逐字节同步校验通过。未手写任何业务状态包/分发。
- **运行时固定 API**：`PMR3Runtime.Register/ProtocolHash/CollisionDigest(=0x52334201)/Attach/SpawnPlayer` 按 §7.4 落地；`Attach` 装世界工厂、OnRep 分发表与生成 `RemoteSender`（按 `target.World` 反查桥，支持多世界并跑）；`SpawnPlayer` 是服务端唯一创建入口（Authority + **只有 `OwnerConnection` 一个 owner 来源**，`PMR3Player.GetNetConnection()` 直返它，避免双身份）。
- **DS 宿主**：`PMDsHost` 在 `-bootstrap` 非空时**转交** `PMDsSessionHost`，**旧 PMUdpRouter 诊断路径不启动**；新宿主读引导文件并校验局/摘要/端口，建立世界/桥、运行时程序化建固定测试碰撞场景（地板 (0,-0.5,0)/(40,1,40) + 墙 (0,1,0)/(1,2,8)，均 BoxCollider，**校验 enabled+active 后才 SceneReady**）、`OpenServer`、订阅 `Connected` → `SpawnPlayer`，`Pump` 单点驱动（不再二次 `bridge.Update`）。控制面由纯 C# 的 `PMDsLobbyAgent` 承担（loopback TCP + 4 字节 LE 分帧 + MAC 验签 + Ready/Heartbeat/Result/Exited；后台只入队，主线程 Pump 消费；Result 同 ID 重发至收到**匹配的** ResultAck 才请求退出；连接/就绪/结果确认超时一律明确失败退出）。
- **客户端入口**：`PMClientSessionHost.Enter(offer)/Stop` 先校验协议摘要与碰撞摘要（不一致**报错且不回旧链**），建共享固定场景（可见原语），关闭旧战斗 UDP socket（`UDPSocketManger.CloseExisting`，幂等）后 `OpenClient`，仅本地 owner 副本经生成桩发**一次** `ServerProbe`；`UIMatchingPanel.OnResponse` **最先**识别 `PMDS1:` 前缀，解码成功后 `Enter` 并 `return`，不进旧 `BattleData/ClearSence`；保留 Lobby TCP。退出清理世界/socket/场景根/static 事件；重复 Enter 同一局不重绑定。
- **参数与构建**：`-bootstrap`/`-control`/`-server-smoke` 落到 `PMNetLaunchOptions` 并有 39 项新断言（`PMNetLaunchCheck` 133/0）；`PMDsBuild.WriteLauncher` 补两种启动形态的用法说明，路径全部由 `%~dp0`/环境变量推导，**不写死绝对路径**（未打开场景、未构建）。
- **实测门禁**：`Tools/PMR3RuntimeTest` **134 项 / 0 失败 / exit 0**（真实 PMTransport 字节链 + 真实 loopback 控制通道）；`PMNetLaunchCheck` 133/0；`PMUnityGlueCheck`、`PMClientCheck`、`PMNetLangCheck`、`Server/Server.csproj` 全部 **0 错误**；既有门禁回归 `PMTransportTest 81/0`、`PMReplicationTest PASS`、`PMNetWorldTest 197/0`、`PMNetE2E 209/0`。
- **待集成/缺口**：① `Tools/PMNetSessionTest` 现有 **3 项失败**，根因是网络组新加的 `PMTransportConnection.OnDatagram` 未激活前置闸门（§7.2 要求）与 R3-A 旧断言冲突，**不在本组文件范围**（证据见 §5）；② `-server-smoke` 的「全名册探针完成→提交」聚合逻辑只在 DS 代码里编译通过，**未做运行时验证**（需 Unity 或测试宿主）；③ `PMDsSessionHost`/`PMClientSessionHost` 只经编译与静态审查，其真实 Unity 路径属 T42。

## 2. 冻结接口落地对照（契约 §7.4）

| 契约要求 | 实际落点 | 证据 |
|---|---|---|
| `PMR3Runtime.Register()` → 生成 `RegisterAll()` | `PMR3/PMR3Runtime.cs` | 测试 A：封板、类数==`GeneratedClassCount` |
| `ProtocolHash` | 同上（`PMNetRegistry.ProtocolHash`） | 测试 A：生成期==运行期摘要 |
| `const uint CollisionDigest = 0x52334201u` | 同上 | 测试 G |
| `Attach(world, bridge)`：世界工厂/OnRep/RemoteSender | 同上 | 测试 B：重复 Attach 幂等；测试 C：生成桩不落待发队列 |
| `SpawnPlayer(world,bridge,owner)`：Authority+唯一 owner+登记复制 | 同上 | 测试 B：角色/状态/`OwnerConnection`/`GetNetConnection()`/`Owner==null` |
| 声明：`Uid`/`ProbeCount` 纯字段复制 | `PMR3/PMR3Player.cs` | 测试 A：掩码位宽 2、属性 2 个 |
| `ServerProbe(int)` 带 ForceValidate、执行后增 ProbeCount 并 ClientEcho | 同上（经生成桩 `PMNet_ServerProbe`/`PMNet_ClientEcho`） | 测试 C：ProbeCount 1→2、回声 nonce 原样 |
| `PMDsHost` 在 `-bootstrap` 非空时转新 host，旧 router 不启动 | `Server/Boot/PMDsHost.cs`、`PMDsSessionHost.cs` | §3 静态核对 + `PMUnityGlueCheck` 编译 |
| DS 读引导文件并校验局/端口，建场景后 `SceneReady`，`OpenServer` 订阅 `Connected` | `PMDsSessionHost.cs` | 场景校验有显式失败路径；端到端运行态属 T42 |
| 客户端 `Enter/Stop`、PMDS1 分流、关旧 UDP、仅本地 owner 发一次探针 | `PMClientSessionHost.cs`、`UIMatchingPanel.cs`、`UDPSocketManger.cs` | `PMClientCheck` 真实编译；运行态属 T42 |
| `-bootstrap/-control/-server-smoke` | `PMNet/PMNetLaunchOptions.cs` | 测试 U11：133/0 |
| `PMDsBuild` 补参数说明、不写死绝对路径 | `Editor/PMDsBuild.cs` | 静态核对（未构建） |

## 3. 测试验收集（实际结果）

| ID | 实施映射 | 场景 | 执行者 | 方法/命令 | 预期 | 状态 | 证据 |
|---|---|---|---|---|---|---|---|
| U1 | 声明→生成物同步 | 产物/锁文件与声明逐字节一致 | AI | `PMR3RuntimeTest` A 段（内部调 `PMNetGen --decl-check`） | 退出码 0 | PASS | exit 0；锁文件含 `CLASS:PMNet.R3.PMR3Player` 等 4 条 |
| U2 | 零反射注册表 | 类数/属性/掩码/摘要 | AI | 同上 A 段 | 1 类 2 属性 2 位摘要一致 | PASS | 断言全通过 |
| U3 | `Attach` 幂等 | 重复 Attach 不抛 | AI | 同上 B 段 | 不抛 | PASS | `RegisterClass` 先查再注册 |
| U4 | `SpawnPlayer` 唯一 owner | 角色/OwnerConnection/GetNetConnection/Owner | AI | 同上 B 段 | Authority + 单 owner | PASS | 6 项断言通过 |
| U5 | 前置拒绝 | 客户端侧 Spawn、未 Attach 的 Spawn | AI | 同上 B 段 | 返回 null | PASS | 两条告警可见 |
| U6 | Create 初值与收敛 | 客户端副本 Uid 与创建原子到达 | AI | 同上 C 段 | `Uid==uid` 且回调 1 次 | PASS | 真实 PMTransport 字节链 |
| U7 | 探针往返 | `ServerProbe`→ProbeCount→`ClientEcho` nonce | AI | 同上 C 段 | 1→2、nonce 原样 | PASS | `RpcApplied==2`、`ProbeCount==2`、`EchoCount==2` |
| U8 | 归属拒绝 | 非 owner 冒名探针 | AI | 同上 D 段 | 实现不执行 + `NotOwner` | PASS | `RpcRejected` 增长、`PMRpcReceiveStatus.NotOwner` |
| U9 | 清理 | `Detach`/`Shutdown` 清 static | AI | 同上 E 段 | RemoteSender/事件归零 | PASS | 4 项断言通过 |
| U10 | 控制通道正常往返 | Ready/Heartbeat/Result/ResultAck→退出 | AI | 同上 F 段（**真实 loopback TCP**） | 字段正确、退出码 0 | PASS | BoundPort/SceneReady/Digest/心跳 PlayerCount/同 ID 结果幂等 |
| U11 | 控制通道负向 | 坏 MAC、错 MatchId、非 loopback、无 listener、场景未就绪 | AI | 同上 F 段 | 明确失败退出 1 / 构造期拒绝 | PASS | `MacFailures==1`、`FaultReason` 指向 MatchId、未就绪不发 Ready |
| U12 | 启动参数面 | 新参数解析/降级/Describe | AI | `PMNetLaunchCheck` | 133/0 | PASS | 由 94 项扩到 133 项 |
| U13 | Unity 胶水真实编译 | Boot/Editor/PMNet/PMR3 | AI | `dotnet build Tools/PMUnityGlueCheck -c Release` | 0 错误 | PASS | 0 警告 0 错误 |
| U14 | 客户端玩法层真实编译 | 含 UIMatchingPanel/PMClientSessionHost | AI | `dotnet build Tools/PMClientCheck -c Release` | 0 错误 | PASS | 0 警告 0 错误（并抓到 `Server` 命名空间误解析） |
| U15 | 语言面（C#7.3/netstandard2.0） | PMNet/**（含本轮参数改动） | AI | `dotnet build Tools/PMNetLangCheck -c Release` | 0 错误 | PASS | 0 警告 0 错误 |
| U16 | Server 链接 PMR3 | net8.0 服务端工程 | AI | `dotnet build Server/Server.csproj` | 0 错误 | PASS | 8 个既有警告、0 错误 |
| U17 | 既有门禁回归 | transport/replication/world/E2E | AI | 各自 dll | 全绿 | PASS（3 项除外） | 81/0、PASS、197/0、209/0 |
| U18 | 编码约定 | 源 BOM+CRLF、新 `.meta`/锁/文档 无 BOM+LF | AI | 逐文件字节统计 | 符合约定 | PASS | 新增源 6 个文件 BOM+CRLF，`.meta` 9 个无 BOM+LF |
| T42 | 真实 UnityDS + 两客户端 | Lobby→UnityDS→两客户端→结果→退出 | AI+用户 | 需编辑器构建 | 真进程/场景/两端证据 | **PENDING_USER** | 编辑器运行中，本轮不抢锁 |

精确命令：

```bat
dotnet build Tools/PMR3RuntimeTest -c Release && dotnet Tools/PMR3RuntimeTest/bin/Release/net8.0/PMR3RuntimeTest.dll
dotnet build Tools/PMNetLaunchCheck -c Release  && dotnet Tools/PMNetLaunchCheck/bin/Release/net8.0/PMNetLaunchCheck.dll
dotnet build Tools/PMUnityGlueCheck -c Release
dotnet build Tools/PMClientCheck -c Release
dotnet build Tools/PMNetLangCheck -c Release
dotnet build Server/Server.csproj
dotnet Tools/PMTransportTest/bin/Release/net8.0/PMTransportTest.dll
dotnet Tools/PMReplicationTest/bin/Release/net8.0/PMReplicationTest.dll
dotnet Tools/PMNetWorldTest/bin/Release/net8.0/PMNetWorldTest.dll
dotnet Tools/PMNetE2E/bin/Release/net8.0/PMNetE2E.dll
```

## 4. 关键实现取舍（需主 Agent 知晓）

1. **新链分流放在 `PMDsHost.Start` 内部**（`-bootstrap` 非空即转 `PMDsSessionHost`），保留 `PMDsHost.Instance` 语义与返回类型；PMNetBootstrap 调用点未变。带 bootstrap 时**旧 socket/router 一律不建**。
2. **`PMDsSessionHost` 不是 MonoBehaviour**：由 `PMDsHost.Update` 单点驱动，`Pump()` 内只调 `endpoint.Pump(...)`（该方法内部已 `bridge.Update`），从结构上杜绝「一帧两次 Update 把数据报预算翻倍」。
3. **客户端归属判据用副本 `Role == AutonomousProxy`**（DS 在派发 Create 时按真实连接现算 `IsOwner`），并用 offer 的认证 uid 复核；两者不一致时告警且不发探针。
4. **探针只在 Pump 里发**（不在复制回调里发）：`OnReplicatedCreate` 发生在入站 drain 之中，直接发会重入桥。
5. **`PMDsLobbyAgent` 不直接结束进程**：退出经 `ExitRequested(int)` 交宿主（Unity 侧 `Application.Quit(code)`），既保住纯 C# 可测性，也让门禁能断言「失败退出请求了非 0」。
6. **MAC 失败与「已验签但身份/方向不符」分开计数**（`MacFailures` vs `FramesRejected`），两类故障诊断含义不同。
7. **PMR3 放在 `Scripts/PMR3/`（PMNet 目录之外）**：`PMNet/**` 是多个门禁的编译通配符，且 E2E 自有一份 `PMNetGeneratedRegistry`，放进去会重名。

## 5. 缺口与待集成（诚实口径）

1. **`Tools/PMNetSessionTest` 3 项失败，非本组引入**（本组对 `PMNet/` 的唯一改动是 `PMNetLaunchOptions.cs` 的 3 个参数与 `Describe`，与世代/激活无关）：
   - 失败项：`未激活时字节仍到达传输回调（计数 1）`、`旧世代数据报没有到达应用层（仍是 1 条）`、`旧世代丢弃被传输层记账`；
   - 根因：`Client/Assets/Scripts/PMNet/Session/PMTransportConnection.cs:744` 新加的 `if (!IsReady) { InboundDroppedNotActivated++; return; }`（`OnDatagram` 纵深前置，契约 §7.2 要求「未激活绝不交 Transport」），而 `Tools/PMNetSessionTest/Program.cs:807` 仍按 R3-A 旧语义断言 `MessagesReceived == 1`；
   - 证据：`Tools/PMNetSessionTest/r3a-verification.log` 记录当时 **343/0**；`PMTransportConnection.cs` mtime 2026-09-20 10:45（本组工作期间被网络组改动），`PMNetSessionTest/Program.cs` mtime 00:39（未同步）。
   - 处置建议：**网络组**改断言（或改闸门位置），本组未越界修改这两个文件。
2. **`-server-smoke` 聚合逻辑未运行时验证**：`PMDsSessionHost.CheckSmoke/BuildSmokeSummary`（全名册 `ProbeCount>0` → 提交标注 smoke 的摘要、超期失败）只在编译层验证；`SubmitResult` 本身有 8 项真实测试。该逻辑需要 Unity 或测试宿主才能跑。
3. **`PMDsSessionHost`/`PMClientSessionHost` 无运行时验证**：其 UDP 端点交互依赖网络组的 `PMUdpSessionEndpoint`（本组按 §7.2 消费；`OpenServer/OpenClient` 自身的验收属网络组 `Tools/PMUdpAdmissionTest`）。本组用 `PMR3RuntimeTest` 覆盖了它们**所依赖的全部框架行为**（世界/桥/复制/探针/归属/清理）与 `PMDsLobbyAgent` 的全链路。
4. **UI 分流只编译验证**：`UIMatchingPanel` 的 `PMDS1` 前缀识别为先判、失败即 return 不回旧链，已按要求实现；真实点击路径属 T42。
5. **`PMDsBuild.WriteLauncher` 未执行**：遵守「不打开 Unity 场景、不 build」，脚本内容只做静态核对（两种形态、`%~dp0` 相对路径、无绝对路径硬编码）。
6. **强杀/收尾的观测面**：DS 侧未实现「Lobby 断线后换池回收」；`PMDsLobbyAgent` 在控制通道被对端关闭时按「失败退出 1」处理（不冒充确认）。
7. **协议摘要来源**：`PMR3Runtime.ProtocolHash` 由生成表 `Seal(...)` 现算（当前 `0x7B986DB4`）；Lobby 组已在 `Server/Server.csproj` 链接 `Client/Assets/Scripts/PMR3/**` 并调 `PMNet.R3.PMR3Runtime.Register()`，`Server` 工程 0 错误通过。

## 6. 已检查范围（实际读取顺序）

1. 根 `AGENTS.md` → `Client/Assets/AGENTS.md` → `Server/AGENTS.md`
2. `Docs/plans/net-architecture-migration.md`（卷首当前状态/环境/禁用项、§3.9 全节、文末 RV 与 R3 分步与 R3-B 行）
3. `Docs/plans/net-r3-control-contract.md`（全文，尤其 §7.1–§7.4）
4. `Docs/plans/_r3b_host_survey.md`（全文）、`_r3b_lobby_survey.md`、`_r3b_auth_survey.md`
5. `Docs/plans/_r3a_control_report.md`（§1/§4/§5/§11/§12）、`_r3a_session_report.md`（§1–§9）
6. 源码（只读）：`PMNet/{PMNetLaunchOptions,PMNetRuntime,PMNetObject,PMNetConnection,PMNetRole,PMRpc,PMNetProperty}.cs`、`PMNet/Declarations/PMNetDeclarations.cs`、`PMNet/Control/{PMDsControlProtocol,PMDsTicket}.cs`、`PMNet/Session/{PMNetSessionBridge,PMTransportConnection,PMDsEntryOffer,PMUdpSessionEndpoint}.cs`、`PMNet/World/PMNetWorld.cs`、`PMNet/Replication/PMReplicationChannel.cs`、`PMNet/Transport/PMTransportTypes.cs`、`Server/Boot/{PMDsHost,PMNetBootstrap}.cs`、`Server/{Panel/UIMatchingPanel,Manger/UDPSocketManger}.cs`、`Editor/PMDsBuild.cs`、`Tools/{PMNetGen/*,PMNetE2E/*,PMNetSessionTest/*,PMNetLaunchCheck/Program.cs}`、`Server/Server.csproj`
7. 技能/规范：`windows-shell-compat`（SKILL.md 全文，命令按 Bash 解释器书写）

文档如何决定入口：根 AGENTS → 三处入口文档；主计划 §3.9 → 模块职责（M11 宿主 / M12 Lobby）与「声明生成不靠反射」；R3 契约 §7.1–§7.4 → 全部待冻结 API、Boot 分流、场景口径、参数名；`_r3b_host_survey.md` → `PMDsHost` 现状、`IPMTransportLink` 缺口、参数扩展点；A1/A2 报告 → 控制协议/票据/会话适配的**实际**公开面（`PMDsBootstrappedMatch`、`PMDsControlSigner.VerifyRaw`、`TryActivate` 前置语义）。

## 7. 变更文件清单（全部在 writePaths 内）

新增：
`Client/Assets/Scripts/PMR3.meta`、`PMR3/Generated.meta`、`PMR3/PMR3Player.cs(+meta)`、`PMR3/PMR3Runtime.cs(+meta)`、
`PMR3/Generated/PMNet.PMNet.R3.PMR3Player.g.cs(+meta)`、`PMR3/Generated/PMNetGeneratedRegistry.g.cs(+meta)`、
`Server/Boot/PMDsSessionHost.cs(+meta)`、`Server/Boot/PMDsLobbyAgent.cs(+meta)`、`Server/Boot/PMClientSessionHost.cs(+meta)`、
`Tools/PMR3RuntimeTest/PMR3RuntimeTest.csproj`、`Tools/PMR3RuntimeTest/Program.cs`、
`Docs/plans/pmnet-r3-ids.json`（PMNetGen 产出）、`Docs/plans/_r3b_unity_report.md`（本文件）。

修改：
`Client/Assets/Scripts/PMNet/PMNetLaunchOptions.cs`（+3 参数与解析、Describe）、
`Client/Assets/Scripts/Server/Boot/PMDsHost.cs`（新链分流/驱动/收尾）、
`Client/Assets/Scripts/Server/Panel/UIMatchingPanel.cs`（PMDS1 优先分流）、
`Client/Assets/Scripts/Server/Manger/UDPSocketManger.cs`（幂等 `Close`/`CloseExisting`、接收线程 dispose、有界退避）、
`Client/Assets/Editor/PMDsBuild.cs`（启动脚本两形态与说明、去绝对路径）、
`Tools/PMNetLaunchCheck/Program.cs`（+场景 8b，39 项断言）、
`Tools/PMUnityGlueCheck/{PMUnityGlueCheck.csproj,UnityStubs.cs}`（新增真实文件与新 Unity API 桩）、
`Tools/PMClientCheck/{PMClientCheck.csproj,ClientStubs.cs}`（新增 PMR3 真实源与新 Unity API 桩）。

未改：`Server/**`（含 `Server.csproj`，由 Lobby 组链接 PMR3）、`Client/Assets/Scripts/PMNet/Session/**`、`Control/**`、`Transport/**`、`World/**`、`Replication/**`、`Declarations/**`、`Generated/SocketProto.PMNet.g.cs`、`Tools/PMNetGen/**`、契约与主计划文档、proto。
