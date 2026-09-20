# hyld-master 网络架构迁移计划

> **文档定位**：本文是「把 ProjectMecury 的网络架构整体迁移到 hyld-master」的**唯一事实源**，包含现状基线、目标架构规格、分阶段实施步骤、测试验收集与决策记录。
>
> **维护约定**：实施期间持续更新同一文件（步骤状态、测试状态、决策变更、交接记录）。后续接手者必须先完整读完本文，核对当前阶段与测试状态，不得只依赖聊天上下文。
>
> **事实等级**：§2为迁移前/历史基线，不能当成当前代码现状；当前状态见卷首，最新架构契约见§3.9。§3的UE机制有源文档/源码依据，Unity模块落点与API为待实施设计；本轮审核不等于运行验收。

---

## 快速接手（每次新会话先看这一节）

> 这一节是为了让**压缩上下文后的新会话**能在几分钟内定位，不必从头读 1000 行。

### 当前状态（R3 开工审查后）

R0 契约已冻结。R1传输/世界与R2声明/复制已通过离线和内存投递门禁；R2独立审查返工见文末RV1–RV7（声明71、复制265、世界197、E2E209）。真实UDP会话认证、Unity宿主及握手尚未验收。

**当前进度：R3-A基础实现与针对性门禁已交付，R3-B代码及回环整链已交付，真实Unity T42待验**，冻结契约 `Docs/plans/net-r3-control-contract.md`。R3-B再接匹配入口、Unity场景就绪、客户端切服及完整结果回收；T42不得凭替身进程或内存投递标PASS。R4-A纯预测与Mover模型已落地并通过集成门禁，R4-B网络/Unity宿主接线已交付，真实PhysX与新DS包待验；R5投射物尚未实现。

旧§5.4–5.6及历史交接数字只作历史记录，当前以文末RV与R3验收表为准。旧大厅战斗仍可达；新入口可用前不删除，不允许新旧同时驱动同一局。保持Unity2019.4、无数据库、无旧协议兼容要求，不编译UE工程、不代提交。

### 环境现状（本机实测）

| 项 | 值 |
|---|---|
| 工作副本 | `D:/UGit/hyld-master`（仓库文档里的 `D:/unity/hyld-master/hyld-master/...` **已过期**） |
| Unity | **2019.4.8f1**，装在 `D:/Unity/2019.4.8f1`（Hub 登录激活；`PMDsBuild` 等编辑器脚本已编入） |
| 服务端 | `net8.0`，**不依赖任何数据库**（账号在内存库 `Server/DAO/UserStore.cs`） |
| 服务端监听 | TCP `7778`（大厅）；UDP `7777`（旧战斗路径，待 P3' 处置） |
| DS 产物 | `HyldDS/HyldDS.exe`（328 MB，真无头包，已 gitignore） |
| 本机 MySQL | **没有安装，也不需要** |

### 既有成果的验收命令（不覆盖新框架T37–T47）

> **R0（T37）已 PASS**，交付物为 `Docs/plans/net-r0-contract.md`（文档类，无命令）。

```bat
REM Unity 侧 / 纯 C# 门禁
dotnet build Tools/PMNetLangCheck -c Release                     REM T1  语言面（netstandard2.0 + C#7.3）
dotnet Tools/PMNetVerify/bin/Release/net8.0/PMNetVerify.dll     REM T2  线格式逐字节等价（需先 build）
dotnet Tools/PMNetLaunchCheck/bin/Release/net8.0/PMNetLaunchCheck.dll  REM P2' 启动参数（94 项）
dotnet build Tools/PMUnityGlueCheck -c Release                   REM P2' Unity 胶水可编译性
dotnet Tools/PMCallspaceCheck/bin/Release/net8.0/PMCallspaceCheck.dll  REM R1 callspace 真值表 + 归属校验 + 身份/帧契约（67 项）

REM 战斗数值与仿真（不需要 Unity）
dotnet build Tools/PMSharedConfigCheck -c Release                REM P3'-2 共享数值表零依赖编译（自动含 Shared/**）
dotnet build Tools/PMHeroDataCheck -c Release                    REM P3'-2 HeroData 编译（只读属性被赋值会 CS0200）
dotnet build Tools/PMClientCheck -c Release                      REM P3'-2/P3'-3 客户端玩法层真实编译（~120 文件 / 23k 行）
dotnet Tools/PMNumericEquivalenceTest/bin/Release/net8.0/PMNumericEquivalenceTest.dll  REM P3'-2 数值等价（341 项）
dotnet Tools/PMBattleSimTest/bin/Release/net8.0/PMBattleSimTest.dll                    REM P3'-3a 仿真数学逐位等价（345715 项）
python Tools/check_client_authority_writes.py                    REM P3'-3c 客户端权威字段写入门禁（越界 = 0）
python Tools/check_hero_table_shape.py                           REM P3'-2 英雄表形状 + 表现基准（7 项）
python Tools/check_hero_id_alignment.py                          REM P3'-2 proto/客户端/共享表 三方编号对齐（6 项）

REM P3'-1 UDP 路由层（不需要 Unity）
dotnet build Tools/PMUdpRouterCheck -c Release                   REM P3'-1 路由层编译（netstandard2.0 + C#7.3，不引 UnityEngine）
dotnet Tools/PMUdpRouterTest/bin/Release/net8.0/PMUdpRouterTest.dll   REM P3'-1 路由层功能测试（44 项）

REM 服务端 + 大厅（不需要 Unity，也不需要数据库）
dotnet build Server/Server.csproj
dotnet Server/bin/Debug/net8.0/Server.dll                        REM 另开一个窗口常驻
dotnet Tools/PMServerSmokeTest/bin/Release/net8.0/PMServerSmokeTest.dll  REM 大厅冒烟（17 项）

REM 协议生成门禁（只校验不改文件）
ProtobufAndNotepad/Protobuf/build.bat --check-only
```

**DS 构建与启动**：Unity 菜单 `Build / Build HyldDS (Windows Headless)`，
然后跑 `HyldDS/run_ds.bat <dsid> <matchid> <port>`。

### 本机环境的坑（会反复撞到，先记住）

| 坑 | 表现 | 应对 |
|---|---|---|
| **`curl` 的 SSL 全部失败** | `exit 35` / `CRYPT_E_NO_REVOCATION_CHECK` | 加 `--ssl-no-revoke`，或走本地代理 `http://127.0.0.1:7897`（在监听） |
| **Unity 编辑器开着时不能起 batchmode 实例** | 工程锁 `Client/Temp/UnityLockfile` 冲突 | 需要 CLI 构建时先让用户关掉编辑器，或让用户点菜单 |
| **新增含中文的源文件必须存 UTF-8 + BOM** | 无 BOM 会被按 UTF-8 解码，中文变 U+FFFD → 编译失败或字符串静默损坏 | 见 §9.5 第 15 条；工具 `Tools/fix_gbk_sources.py` |
| **在 JSON/Python 字符串里写 Windows 路径** | 反斜杠 + b/n 被当转义符，文档静默损坏 | 用正斜杠，或写完扫描异常控制字符。见 §9.5 第 9 条 |
| **`git status` 里有大量既有未提交删除** | `.claude/`、`openspec/`、`.github/` 等，**不是本次工作造成的** | 提交前按改动边界逐条挑，不要 `git add -A` |

### 禁止事项

- **不要用 Unity 6（Hub 里装了 `6000.6.1f1`）打开本工程**——会强制升级资产，不可逆。
- **不要代为提交**。改动边界由用户决定；尤其注意上面那条「既有未提交删除」。
- **不要把 `D:/UGit/hyld-master` 当成 ProjectMecury**：前者是 Unity + C# 服务端
  （日志用 `Logging.Debug.Log`），后者是 UE5（日志用 `PM_LOG`）。两者规范不同，不要混用。

---

## 0. 任务目标与范围

### 0.1 目标

把 ProjectMecury（UE5）已经在跑的网络架构模型迁移到 hyld-master（Unity 2019.4 + C# 服务端），包括：

1. **局外/局内双服务器拆分**——局外一套 TCP 逻辑服，局内独立 DS 战斗服（独立打包）。
2. **网络身份（NetRole）模型**——Authority / AutonomousProxy / SimulatedProxy，一份函数体两端复用，靠角色区分执行端。
3. **类型化 RPC**——替代现有「`ActionCode` 枚举 + 字符串反射 `GetMethod`」的分发方式，带可靠性分级与归属校验。
4. **属性复制**——替代现有「每帧全帧快照 + 同帧重复 3~4 次」，带脏位、per-connection baseline、复制条件、量化。
5. **增量同步**——对象级/属性级/数组级三层增量（含 FastArray 式按身份增量）。
6. **距离裁剪与调度**——`NetCullDistanceSquared` + `bAlwaysRelevant` + Dormancy + `NetUpdateFrequency` + 优先级/带宽预算。
7. **声明生成与AOT**——扫描C# Attribute，生成RPC调用桩/分发表/参数序列化、复制访问器/描述符及OnRep；不依赖运行时反射找业务方法。
8. **可靠信道**——可靠RPC的通道内保序去重、ACK重传、窗口与背压；属性复制保证当前状态最终收敛；高频输入/快照采用各自策略。
9. **Mover/NetworkPrediction移植**——同一运动模型的权威与预测执行；Input/Sync/Aux及时间步历史、同帧比较、恢复/重模拟、SP插值与表现外推，包含项目所需运动效果与Modifier回滚语义。
10. **投射物机制移植**——预测弹/权威镜像身份关联接管、历史命中验证、Pending裁决、拒绝清理、停止墓碑和迟到命中；伤害死亡与胜负最终由Unity DS裁决。
11. **旧链退役**——业务不再手写战斗MainPack、state_mask与重复发包；旧SavedMove路径由新预测后端替换。新链通过后删除旧路径，不长期保持双权威。

### 0.2 非目标

- 不迁移 Mos（Python）集群本身。hyld 的局外服务是 C#，保留 C# 实现，只搬职责划分与拓扑。
- 不将UE引擎C++二进制或完整UObject/UHT体系嵌入Unity；在C#中重实现所需连接、对象通道、RPC、复制、预测回滚语义。
- 不整体移植GAS/StateTree/Behavior编辑器与全部玩法内容；**Mover/NetworkPrediction及项目投射物预测、回滚与裁决明确在范围内**。依赖的激活ID、Pending/Confirmed/Rejected与撤销需有C#等价契约。
- 不解释单机模式子弹残留代码（另行清理）。
- 不做旧新协议兼容兜底。双端使用同一份权威协议与声明生成产物；保留protobuf编码不代表保留旧MainPack schema。

### 0.3 关键路径提醒

hyld 仓库文档中大量 `D:\unity\hyld-master\hyld-master\...` 路径**已过期**，实际工作副本为 `D:\UGit\hyld-master`。本文所有路径以实际工作副本为准。

---

## 1. 决策记录

| ID | 决策项 | 结论 | 影响 |
|---|---|---|---|
| D1 | 「AOT」的含义 | **编译期生成序列化器 + 属性描述符**，替代运行时反射描述符路径 | 决定 P0 生成器与 P7 的收尾面 |
| D2 | 首批落地范围 | **大厅 TCP RPC 化先行**（风险最低、立刻修真实缺陷） | 决定 P0→P1 的推进顺序 |
| D3 | 线格式策略 | **保留 protobuf**，只在其上叠加复制层（脏位/baseline/量化/条件） | 量化在属性层做（float→定点 int 塞进 protobuf varint），线格式不变，双端兼容风险最低 |
| D4 | Unity 版本 | **保持 2019.4 不动** | 客户端只能用「外部生成器 + 提交产物」；生成代码受 C# 7.3 + .NET Standard 2.0 天花板限制 |
| D5 | OpenSpec | **不再使用**，本仓库 openspec 工件与 skill 已移除 | 计划文档改用本文 + 分阶段表格 |
| D6 | **HyldDS 的形态** | **Unity 工程本身的打包产物**（Windows 无头服），**每局拉起一个进程、打完销毁**；不是新的 C# 工程 | 决定 P2' 之后所有阶段的形态；`Server/` 里的战斗代码要迁到 Unity 脚本侧 |
| D7 | **Unity 版本** | **保持 2019.4**，DS 用「StandaloneWindows64 包 + 编译期 `BuildOptions.EnableHeadlessMode` + 运行时 `-server` 身份判定」；不升级 | 拿不到 `UNITY_SERVER` 与 Dedicated Server 构建目标，服务端/客户端**业务代码**分支只能运行时判定。但**图形设备的无头化已在编译期解决**（实测可用，见 §2.8），因此升级 Unity 的动机已明显减弱 |
| D8 | **DS 部署平台** | Windows（`StandaloneWindows64`） | 与现有原生插件覆盖一致（`Plugins/x86_64`、`Plugins/x86`），无缺失依赖 |
| D9 | **战斗仿真** | **合并为一套**：服务端旧仿真与客户端预测仿真合一，用网络角色区分权威/预测；**数值配置与表现引用分离**、配置单一化 | P3' 是全部工作中最大的一块，也是收益最大的一块（消除模型分歧） |
| D10 | **账号持久化** | **取消数据库**：账号/好友改为进程内内存库（`Server/DAO/UserStore.cs`），删除 MySQL 依赖与全套 SQL；**登录时账号不存在则自动建号**（密码错仍失败）；新账号默认昵称取账号名 | 解除「必须先装 MySQL 才能登录」的阻塞；`hyld.sql` 仅作历史 schema 保留，不再被使用 |
| D11 | **服务端 TFM** | `net6.0` → **`net8.0`**（答案 Q5） | net6.0 已 EOL，且构建机上无 .NET 6 运行时，服务端跑不起来 |
| D13 | **权威执行线程** | Unity DS主线程承载全部局内权威逻辑，接收线程只入队 | 主线程不等于固定16ms；Unity Update、仿真步长和复制频率分别定义。原“沿用旧累加器即完成迁移”撤回 |
| D14 | **目标纠偏（用户明确）** | UE式声明网络框架 + ProjectMecury Mover/预测回滚 + 投射物机制 | 撤销“排除Mover”“旧SavedMove保留不动”与P3'-3b旧定稿，按§3.9/§5推进 |

> 决策保留原D编号（D12未分配）；历史调查缺陷引用必须加S6前缀，如S6-D12，不能混用。


---

## 2. 现状基线（hyld-master）

### 2.1 工程与运行形态

| 维度 | 现状 | 证据 |
|---|---|---|
| Unity 版本 | `2019.4.8f1`（revision `60781d942082`） | `Client/ProjectSettings/ProjectVersion.txt:1-2` |
| 脚本后端 | **Mono2x**（`scriptingBackend: {}` 空 map → Android/Standalone 平台默认 Mono；iOS 强制 IL2CPP） | `Client/ProjectSettings/ProjectSettings.asset:617` |
| 剥离等级 | `managedStrippingLevel: {}` 空 map，未启用 | 同上 `:619` |
| API 兼容级别 | `apiCompatibilityLevel: 6` = .NET Standard 2.0 | 同上 `:698` |
| 客户端语言面 | C# 7.3 | Unity 2019.4 上限 |
| 服务端 | 单工程 `Microsoft.NET.Sdk` / `net6.0` / 隐式 C# 10 / 本机 SDK 10.0.302 | `Server/Server.csproj` |
| 服务端（**已变更**） | **`net8.0`**（决策 D11）。原为 `net6.0`，因本机无 .NET 6 运行时且 net6.0 已 EOL 而升级 | 同上 |
| 服务端进程 | **一个进程同时跑 TCP 7778 + UDP 7777** | `Server/Server/ServerConfig.cs`、`Server/Server/Server.cs:32` |
| 帧率 | 固定 16ms = 62.5Hz（`ServerConfig.frameTime=16`） | `Server/Server/ServerConfig.cs` |
| 满编人数 | `MaxRoom3_3Number = 6` | 同上 |

### 2.2 传输与协议层

- **单一万能信封 `MainPack`**：`RequestCode`(8 项) + `ActionCode`(43 项) + `ReturnCode` + 15 个可选业务字段。**无请求 ID / 无序列号 / 无 correlation 字段**。权威源 `ProtobufAndNotepad/Protobuf/SocketProto.proto`（348 行）。
- 生成产物：`Client/Assets/Scripts/Server/SocketProto.cs` 与 `Server/Server/SocketProto.cs`，**各 6052 行、`diff` 完全一致**。
- 生成链路：`ProtobufAndNotepad/Protobuf/build.bat`，仅 3 条 `protoc --csharp_out`，调**裸 `protoc`**（本机 PATH 未命中）、无版本断言、无产物 diff 校验、无 CI。protoc = `libprotoc 3.11.1`，Google.Protobuf.dll = `3.11.1.0`（4 份同哈希拷贝，裸 DLL 引用）。
- TCP 帧：4 字节小端长度头 + protobuf body，上限 16MB。`Server/Tool/Message.cs:124` 组包、`:86` 解包。
- **5 处解析走运行时反射描述符** `MainPack.Descriptor.Parser.ParseFrom(...)`：
  - Client：`Client/Assets/Scripts/Server/Manger/HYLDManger.cs:143`、`Manger/UDPSocketManger.cs:92`、`TCPSocketMessage.cs:121`
  - Server：`Server/Server/ClientUdp.cs:829`、`Server/Tool/Message.cs:107`
- 写出侧共 8 处 `ToByteArray()`，无反射。
- 非权威历史 proto 副本**已漂移**（`Client/SocketProto.proto`、`ProtobufAndNotepad/Protobuf/Proto/SocketProto.proto` 缺 `BattleSetNetSimConfig=41`/`MoveType`/`AttackType`）。

### 2.3 服务端 RPC 面（大厅 TCP）

- 注册 6 个控制器（FriendRoom/User/Friend/PingPong/Matching/ClearSence），**18 个可达处理方法**；`RequestCode.Room(2)` / `Battle(7)` 未注册。
- **分发靠字符串反射**（`Server/Controller/ControllerManger.cs:51`）：
  ```
  _controllerDic[pack.Requestcode] → GetType().GetMethod(pack.Actioncode.ToString())
    → Invoke(server, client, pack) → client.Send(返回值)
  ```
  不校验签名、无缓存、无 try/catch、无身份/权限校验。
- 分类：纯同步 req-resp 8 个 / 同步+组内广播 5 个 / 同步+定向推送 4 个 / 无回包通知 1 个（`Chat`）。
- `client.Send(o as MainPack)` = 回到该 TCP 连接那条 socket（与 uid 无关）；控制器内部另有 11 处绕过返回值直接单播/广播。
- **全服广播 0 个**；所有「广播」都是「某集合内逐 socket 单播」。
- **大厅下行 100% 全量覆盖**：`playerspack`（在线好友全表）、`friendroompack`、`battleplayerpack` 都是整表刷，无 diff。
- 会话状态是**两步隐式握手**：`Login`（`Controllers.cs:1045`）只查库校验、**不写 `_activeClient`**；只有 `FindPlayerInfo`（`:1079`）才 `AddActiveClient`。

**已确认的反射断裂点**：

| 问题 | 位置 | 后果 |
|---|---|---|
| `AllClearSenceReady` 命中 `void AllClearSenceReady(Server, List<int>)` | `Server/Controller/Controllers.cs:34` | `Invoke` 抛 `ArgumentException` → 冒泡到 `Client.ReceiveCallBack` 的 catch → **服务端主动断连** |
| `RequestCode.FriendRoom + ChangeHero` 命中 `void ChangeHero(Client, MainPack)` | `Controllers.cs:964` | 同类崩溃（客户端已注释，属埋雷） |
| `RejectInvateFriend(20)` ↔ 服务端方法名 `RejectInviteFriend` | `Controllers.cs:821` | 拼写不一致 → 客户端请求**静默丢弃** |
| `CancalInvateFriend(21)` ↔ `CancelInviteFriend(Server, MainPack)` | `Controllers.cs:923` | 名字+参数都不符 → **静默丢弃** |
| `Client.FriendsDic` 全工程**无写入点** | `Server/Server/Client.cs:29` | `UpdateMyselfInfo` 好友推送链路整条空转 |

### 2.4 战斗下发的「增量」现状

主链路：`BattleLoop(Battle.cs:478)` → `CollectAndBroadcastCurrentFrame(:564)` → `PackPlayerStates(Network.cs:18)` → `SendUnsyncedFrames(:61)` → `BuildDeltaFrameForReceiver(:114)` → `ComputeAuthorityStateDeltaMask(:193)` → `LZJUDP.Send(:104)`。

**已有的增量（方向正确，是改造起点而非推倒对象）**：

- `AuthoritativePlayerState.state_mask` 5 位：bit0 位置 / bit1 hp / bit2 is_dead / bit3 mana / bit4 super_energy（`Server/Server/Battle.cs:149-154`）。
- `PackPlayerStates` 恒写 `StateMask = 31`（历史快照全字段，`BattleController.Network.cs:42`）；`BuildDeltaFrameForReceiver` 写增量位（`:157`）。
- baseline = 该接收客户端 ack 的 ServerFrame 对应的服务端全字段快照（`dic_playerAcknowledgedAuthorityStates/...Frame`，`Network.cs:487`）；无 baseline → mask=31。
- `mask == 0` 的玩家**整条从 repeated 里消失**（`Network.cs:149`）→ 语义是「相对基准无变化」。
- ack 只在前进时更新（`Network.cs:259`），乱序不回退。

**缺失项（改造空间）**：

| 缺口 | 事实 |
|---|---|
| 无脏位 | 每次发送**实时逐字段 float 精确比较**，无 setter 打脏；丢包后无法「只重发变化项」 |
| 无真正重发保障 | 丢包恢复 = **同一帧立即重发 3~4 次**（`ackGap<=3 → 3`，否则 4；`Network.cs:70`）。代码里**没有任何历史窗口补帧**（`:63` 只取 `dic_historyFrames[frameid]`） |
| `player_inputs` 每帧全量克隆 | `Network.cs:124` 对全部玩家全量克隆，静止玩家也每帧重发其最后移动意图 |
| 无量化 | 位置 3×float32（12B），每帧全精度重发 |
| 无 dormancy | 每帧对每个客户端都发包（`Battle.cs:641` foreach），0 变化也发 |
| 无 relevancy | 所有玩家对所有客户端恒 relevant；`CheckBulletCollision` 每颗子弹每帧对全部玩家 O(N) 球判定，无空间划分 |
| 无降频/优先级/带宽预算 | 固定 62.5Hz 无条件；替代手段是「更高频 + 重复」 |
| 子弹完全不复制 | 客户端视觉子弹独立生成，靠 `ServerAttack.spawn_pos_*` + `spawn_server_frame` 对齐 |
| 无服务器时间戳 | 只有 Ping/Pong 的 `timestamp`；客户端只能本地累积 16ms 推帧 |
| 单包上限无保护 | 服务端 UDP 收包缓冲**固定 1024B**（`Server/Server/ClientUdp.cs:822`），超长静默截断，无分段重组 |

**带宽实测估算**：单包 ~180-200B（6 人满编），×3~4 次重复 ×62.5Hz ≈ **每客户端 34-50 KB/s**，6 人合计 200-300 KB/s。`BattleReview` 战斗结束全帧历史走 TCP 单包，3 分钟约 **2.8MB**（`dic_historyFrames` 整场只增不删）。

### 2.5 客户端

- **TCP 请求-响应无关联 ID、无业务层超时/重试**；唯一超时是 TCP 心跳（180s Ping / 720s 无 Pong → 断连）。
- `RequestManger._requestDic` 是 `Dictionary<ActionCode, BaseRequest>`，**忽略 RequestCode**；`Add` 重复键抛异常；非线程安全。回包只入队，真正回调由 `UIbasePanel.Excute` 每帧最多泵 10 条。
- **UDP 队列无界**：`DrainAndDispatch` 每帧**排空全部**积压、无单帧上限、无丢弃策略；`BattleStart` 前唯一排空点是 200ms 的 `Send_BattleReady`；**GameOver 后 `Update` 首行 return，直到场景销毁无人排空**。
- 权威帧入口是 `ConsumeAuthoritativeBattleUpdate`（`Client/Assets/Scripts/Server/Manger/Battle/BattleData.Authority.cs:24`）——**文档里的 `OnLogicUpdate_sync_FrameIdCheck` 在当前代码中已不存在**，`Client/Assets/AGENTS.md` 大量行号/函数名已过期。
- 消费顺序：增量合并（`ApplyAuthorityStateDelta:112`）→ 位置/动画/`sync_frameID` → `ConsumeMoveAck` → `ReplayUnconfirmedInputs` → 视觉子弹 → `ApplyAttackAcks` → `ApplyHitEvents` → `ApplyAuthoritativeHpAndDeath`。
- 战斗是 **CSP（客户端预测 + 服务端重模拟）**：`SavedMove` ≈ `FSavedMove_Character`、`ack_good_move` ≈ `bGoodMove`、`ReplayUnconfirmedInputs` ≈ `ServerMoveHandleClientError` 后的重放。
- proto 类型**无领域模型隔离层**：`BattleData.*` 直接以 `BattleFrame`/`AuthoritativePlayerState`/`MoveAckResult`/`HitEvent` 作为内部算法输入；UI 面板在 `OnResponse` 里直接读 `pack.BattleInfo.*`。客户端共 29 个文件 `using SocketProto`。
- **`Relevancy 可见性通知`：代码中不存在任何等价物**——没有对象进入/离开相关性的通知面。

### 2.6 工程约束（AOT / 代码生成）

| 约束 | 事实 | 影响 |
|---|---|---|
| 客户端不能用 Roslyn Source Generator | Unity 2019.4 = C# 7.3；analyzer 需 2020.2+，source generator 需 2021.2+ | 客户端「编译期生成」只能是**外部生成器 + 提交产物**（与现在 protoc 同模式） |
| 客户端语法/API 天花板 | C# 7.3 + .NET Standard 2.0 | 生成代码不得用 `record`/`init`/nullable reference types/现代 `Span` 路径；输出**纯静态类 + struct** 最稳 |
| 服务端可以 | ~~`net6.0`~~ **`net8.0`**（D11）+ Roslyn 4.x，本机 SDK 10.0.302 | 服务端可直接用 Source Generator（但为双端同构，建议统一走外部生成器） |
| XLua 命名空间污染 | `Client/Assets/Editor/HotFixStaicList.cs:16-28` 反射枚举 `Assembly-CSharp` 中 **`Manger` / `HOTFIX` 命名空间的全类型** | **生成代码必须避开这两个命名空间**（现有 `SocketProto` 命名空间天然豁免） |
| XLua 不在业务路径 | 唯一 `new LuaEnv()` 加载硬编码外部绝对路径；`Gen/` 只有 XLua 自带示例 wrap | 当前不是阻塞项，切 IL2CPP 后会变成前置项 |
| 无项目级 `link.xml` | 唯一一份在 `Client/Assets/XLua/Gen/link.xml`，只保 XLua 自带示例 | 切 IL2CPP + 剥离后，反射描述符路径是高风险点 |
| 已导入但闲置的生成宿主 | Entitas + DesperateDevs(Jenny)（`Client/Jenny.properties` + 插件 DLL 在位），游戏代码零使用 | 可复用为 Editor 侧代码生成宿主（备选） |
| 构建钩子现成 | `Client/Assets/Editor/MyCustomBuildProcessor.cs` 的 `IPostBuildPlayerScriptDLLs` | 可挂「构建后校验产物同步」 |

### 2.7 已确认缺陷清单（改造必须顺带修）

| ID | 缺陷 | 位置 |
|---|---|---|
| B1 | 反射分发无签名校验 → 2 个断连雷 + 2 个静默丢包 | `ControllerManger.cs:56`、`Controllers.cs:34/964/821/923` |
| B2 | `MainPack` 万能信封，一个信封换字段复用 | `SocketProto.proto` |
| B3 | 无请求 ID → 无法重试去重、无法异步回调 | `SocketProto.proto` `MainPack` |
| B4 | `Login`/`FindPlayerInfo` 两步隐式握手 → 复制相关性无法正确建立 | `Controllers.cs:1045/1079` |
| B5 | `Client.FriendsDic` 死表 + `UpdateMyselfInfo` 空转 | `Client.cs:29/264` |
| B6 | `mana`/`super_energy` 全量广播给敌方（信息泄露） | `Network.cs:18`（需 `COND_OwnerOnly`） |
| B7 | **延迟补偿帧号语义混用 → 静默失效**：`atk.AttackMoveFrame` 是客户端预测帧号，却被当作 `positionHistory`（按服务端 `frameid` 索引）的键 | `BattleController.Bullets.cs:39/91` |
| B8 | UDP 队列无界（开局 200ms 窗口 + GameOver 后无限增长） | `UDPSocketManger.cs:112` |
| B9 | `BattleReview` 2.8MB 单包 + `dic_historyFrames` 整场不释放 | `BattleManage.cs:231`、`Battle.cs:638` |
| B10 | 服务端 UDP 收包缓冲固定 1024B，无分段 | `ClientUdp.cs:822` |
| B11 | 文档大量过期（`Client/Assets/AGENTS.md` 函数名/行号、`ForServer.md` 的 `Ping/Pong=41/42` 与代码 `24/25` 不符） | 见 §2.2/§2.5 |

---

### 2.8 「DS 用 Unity 打包」这一决策的现状依据（P2' / P3' 的关键输入）

D6 定的方向是「战斗逻辑迁到 Unity 侧、DS 用 Unity 打包」。调查阶段有三条实测证据支持这个方向，也有一条硬约束需要提前应对。

**证据 1：服务端战斗代码零 `UnityEngine` 依赖 —— 迁移在编译层面几乎零摩擦。**

| 文件 | 行数 | `UnityEngine` 引用 |
|---|---:|---:|
| `Server/Server/Battle.cs` | 752 | 0 |
| `Server/Server/BattleController.Network.cs` | 928 | 0 |
| `Server/Server/BattleController.Bullets.cs` | 323 | 0 |
| `Server/Server/ClientUdp.cs` | 846 | 0 |
| `Server/Server/BattleManage.cs` | 261 | 0 |
| `Server/Server/ServerBullet.cs` / `ServerVector3.cs` / `HeroConfig.cs` / `BattleContext.cs` | 已含在上方合计 | 0 |

合计约 3351 行。`ServerVector3.cs` 的注释明确写了「避免依赖 Unity」——也就是说这批代码当初就是按「可以搬进 Unity」写的。**迁移不需要为编译而重写。**

**证据 2：当前是两套并行仿真。**

- 服务端权威仿真：上述 3351 行（`Server/`）。
- 客户端预测仿真：`Client/Assets/Scripts/Server/Manger/Battle/` 约 3636 行（`BattleManger` / `BattleData.*` / `HYLDPlayerManger` / `HYLDBulletManger`）。

UE 模型的核心价值就是「同一套仿真，靠 role 区分权威与预测」。现在是各写一套，机制每加一个都要同步两遍。

**证据 3：移动模型本身不一致 —— 这是正在发生的偏差，不是理论风险。**

- 服务端：`Battle.cs:119` 硬编码 `private const float MoveSpeed = 3.9f;`，**所有玩家相同**，且不认识客户端的运行时加减速。

  ```csharp
  pos.X += -mx * teamSign * MoveSpeed * FrameTimeSec;   // Battle.cs:721
  pos.Z += mz * teamSign * MoveSpeed * FrameTimeSec;    // Battle.cs:722
  ```

- 客户端：每英雄独立移速 + 运行时修正。实测英雄表（`HYLDStaticValue.cs:135` 起）：

  | 英雄 | 移速 | 英雄 | 移速 |
  |---|---:|---|---:|
  | 帕姆 | 3.78 | 麦克斯 | 4.08 |
  | 达里尔 / 公牛 / 瑞科 / 阿渤 / 塔拉 / 佩佩 / 格尔 / 潘妮 / 雪莉 / 布洛克 | 3.9 | 里昂 | 3.96 |
  | 柯尔特 | 4.05 | 黑鸦 | 4.2 |

  并且还有运行时改动：`HYLDModenProp.cs:53/65` 的 `移动速度 ± 1`（道具）、`PlayerLogic.cs:322/331` 的减速与恢复。

**结论**：黑鸦 4.2 对服务端 3.9，差 7.7%，一秒就能偏出 0.3 单位 —— 超过 `MovementMaxPositionError = 0.6f` 只需约两秒。也就是说 `MoveAck` 修正 + `ReplayUnconfirmedInputs` 那套链路**一直在给一个本不该存在的模型分歧打补丁**。这也解释了为什么预测/和解链路要调到「debt 偿还、`MinMoveQuantum`」这种细度。

**证据 4（配置分叉）：服务端配置是人肉副本，且缺字段。**

`Server/Server/HeroConfig.cs` 的文件头注释原文：

> 服务端英雄子弹参数配置。参数值从客户端 HYLDStaticValue.Players（Hero 类）提取，**需人工保持同步**。V1: 硬编码配置字典；后续可迁移到配置文件。

而且该结构体**只有子弹参数与血量，没有移速字段**（构造函数注释里写了「移速」但未存储）。这就是证据 3 的成因。

**硬约束与实测更正（2026-09 实测结果）**

> 下表是改造前基于官方文档与符号搜索得出的判断。**P2' 实测已推翻其中一行**，见下方注记。

| 能力 | 2019.4（文档/初判） | 2020.1+ |
|---|---|---|
| Windows 的 "Dedicated Server" 构建目标 | 没有 | 有 |
| `UNITY_SERVER` 编译宏 | 没有 | 有 |
| `BuildOptions.EnableHeadlessMode`（构建期） | 文档称「仅 Linux」 | 被 DS 目标取代 |
| 运行时 `-batchmode -nographics` | 支持 | 支持 |

**✅ 实测更正（T23/T24）：`BuildOptions.EnableHeadlessMode` 在 Unity 2019.4 + Windows 上确实可用。**

- 证据一（静态）：安装目录里四个 Windows variation 全都带 `WindowsPlayerHeadless.exe`；
  且 `Editor/Data/Managed/UnityEditor.dll` 中存在 `EnableHeadlessMode` /
  `BuildingForHeadlessPlayer` / `IsHeadlessMode` / `m_HeadlessMode` 符号。
- 证据二（实测，决定性）：用 `BuildOptions.EnableHeadlessMode` 构建得到
  `HyldDS.exe`（340,990,698 字节，构建成功、无回退），运行时日志首行即为：

  ```
  Forcing GfxDevice: Null
  NullGfxDevice: Version: NULL 1.0 [1.0]   Renderer: Null Device
  ```

  即**编译期就选定了空图形设备**，而不是靠运行时 `-nographics` 兼容。

**因此 DS 确实是「真无头包」**，而不是「常规包 + 运行时遮住图形」。
仍成立的部分与已不成立的部分：

| 初判 | 实测结论 |
|---|---|
| DS 无法在编译期裁掉图形设备 | ❌ **不成立**：可以，且已用上 |
| DS 无法编译期裁掉客户端业务代码 | ✅ 仍成立：`EnableHeadlessMode` 裁的是**图形设备**，不是业务脚本。`[Subsystems]` 日志里仍可见 `HYLDManger` / `EasyJoystick` 的类型告警，说明业务脚本全部在包里。真正的编译期业务代码裁剪仍需自定义宏（本阶段有意不做，见 §9.2 设计取舍）。 |

**代价与收益（实测数据）**：

- 启动耗时（发起→UDP 就绪）：**740 ms**
- 常驻内存：WorkingSet **~61 MB**，Private **~87 MB**（稳定无泄漏）
- 命令：启动参数 `-server` 仍是必需的（`EnableHeadlessMode` 不会自动让进程变成 DS，它只决定图形设备），
  身份判定仍走运行时（见 §3.8）。`-nographics` 在真无头包里变成冗余但无害，保留以兼容回退路径。

**因此 D7 得到修正**：DS 仍用「StandaloneWindows64 + 运行时身份判定」，但**无头部分改为编译期启用 `EnableHeadlessMode`**，不必依赖 `-nographics`。升级 Unity 的动机也因此**明显减弱**（当初的主要理由之一「拿不到无头能力」已不再成立）。

## 3. 目标架构（照搬 ProjectMecury 网络模型）

> 本节描述「搬过来之后应该长什么样」。ProjectMecury 侧事实源：`Docs/项目基建/客户端与服务器架构梳理.md`（754 行）、`Docs/战斗系统/01_战斗框架/战斗网络框架.md`（1209 行）。

### 3.1 双服务器 + 双连接

ProjectMecury 形态（事实）：

```
客户端
 ├─ 连接 A：Mos 逻辑服（TCP/ASIO + NEX 二进制 + msgpack + RSA + zstd）
 │    登录 / 匹配 / 大厅 / 背包 / 任务 / 结算     ← 局外，长连接全程挂着
 └─ 连接 B：UE DS（UE 原生 UDP + IpNetDriver + Iris）
      移动 / 技能 / 投射物 / 命中 / 伤害 / Buff / 死亡   ← 局内，一局一个
```

关键性质：

1. 局内**两条连接同时存在**，`base_channel` 从不断开，只做心跳/任务同步/结算通知这类后台杂务。
2. **DS 地址由服务器编排下发**：`match` 匹配成功 → `globalsys.GlobalDsMgr` 挑一台 battleguard → battleguard 本机 `fork` 一个 DS 进程 → DS 就绪上报 `ip:port` → 经 Mos 下发客户端 → 客户端 `OpenLevelForDedicatedServer("ip:port", "UserId=xxx")`。打完 DS 自毁。
3. **「快照进、DS 本地算、结算出」**：进局把装备/属性/词条快照塞给 DS（`req_player_login_ds`），局内**绝不实时回传**，离场 `req_player_leave` 算总账回传落库。
4. DS ↔ battleguard **同机走 `127.0.0.1`**，对外用公网 IP；battleguard 是「每台战斗机对外的唯一门户」，用 `req.node_id` 反查是哪台 DS 发的。

**hyld 目标形态**：

```
HyldLobby（局外，TCP，长连接）= 现 Server 的 Controller/DAO/FriendRoom/Matching
  + DS 注册表 + DS 地址下发（对应 GlobalDsMgr + battleguard）
  + 快照打包（对应 get_bring_in_datas）+ 结算落库（对应 req_sync_ds_result）

HyldDS（局内，独立 exe/独立包）= PMNet 复制 + RPC + Relevancy/Dormancy
  + 全部局内权威逻辑（适用数值/数学可复用，旧编排改接新预测/复制框架）
  + 轻量 LobbyAgent（连 Lobby 拉快照、报结算，对应 ds_logic_mgr）

客户端 = LobbyChannel（TCP 常驻）+ DSChannel（UDP 仅战斗期）
  + PMDriverMode 状态机（对应 EPMDriverMode: Login/City/Town/InGame/Update）
```

### 3.2 网络身份模型（「同一份函数两端复用」的真正机制）

ProjectMecury / UE 的机制分三段：

**① 声明期**：`UFUNCTION(Server/Client/NetMulticast)` + `Reliable/Unreliable`，UHT 编译期生成网络桩。
实测规模：**482 个 RPC 声明**（289 Server / 126 Client / 70 NetMulticast；440 Reliable / 29 Unreliable）。

**② 调用期分流**靠 `UObject::GetFunctionCallspace()`（`Engine/Source/Runtime/CoreUObject/Private/UObject/ScriptCore.cpp:1130-1169`）：

```cpp
// Object.h:1495-1512 基类默认：只本地，不发
virtual int32 GetFunctionCallspace(UFunction*, FFrame*) { return FunctionCallspace::Local; }
virtual bool  CallRemoteFunction(UFunction*, void*, FOutParmRec*, FFrame*) { return false; }
```

返回值是**三种**，不是两分：

| 场景 | 结果 |
|---|---|
| 客户端调 `BlueprintAuthorityOnly` 函数 | **Absorbed（静默吞掉，两端都不执行）** |
| `RemoteRole == ROLE_None` 却要发远程 | Absorbed |
| DS 上调 `BlueprintCosmetic` | Absorbed |
| 服务端调 `FUNC_NetServer` | `Local`（不回环） |
| `FUNC_NetMulticast` 且服务端 | `Local \| Remote` |

**③ 接收期归属校验**（`NetDriver.cpp:8027-8030`）：

```cpp
bool UNetDriver::ShouldCallRemoteFunction(UObject* Object, UFunction* Function, const FReplicationFlags& RepFlags) const {
    return ((!IsServer() || RepFlags.bNetOwner) && !RepFlags.bIgnoreRPCs);
}
// bNetOwner ← Actor->GetNetConnection() == Connection，沿 Owner 链逐级上溯到 APlayerController
```

**结论**：写一份函数体，编译器生成桩，运行时由 callspace 决定「本地跑还是发出去」，由归属校验决定「收到的 Server RPC 该不该执行」。**角色不是参数，是隐式上下文。**

补充细节：**`NetMulticast` 不是空间广播**（`NetDriver.cpp:7905-7930` 逐连接先判 `IsNetRelevantFor`），是「所有复制了该 Actor 且相关的连接」。

### 3.3 属性复制模型

实测规模：**254 份 `GetLifetimeReplicatedProps`、579 处 `DOREPLIFETIME`、735 个 `OnRep_`、174 处 `COND_`、187 处频率/裁剪使用**。

标准写法：

```cpp
void UMyComponent::GetLifetimeReplicatedProps(TArray<FLifetimeProperty>& Out) const {
    FDoRepLifetimeParams Params;
    Params.bIsPushBased = true;                 // Push Model：业务侧负责标脏
    Params.Condition = COND_None;               // 或 OwnerOnly / SkipOwner / SimulatedOnly / NetGroup
    DOREPLIFETIME_WITH_PARAMS_FAST(UMyComponent, Health, Params);
}
void UMyComponent::SetHealth(int32 NewHealth) {
    if (Health == NewHealth) return;
    Health = NewHealth;                          // 标脏必须紧贴真实赋值
    MARK_PROPERTY_DIRTY_FROM_NAME(UMyComponent, Health, this);
}
```

完整链路：

```
业务改状态 → Push 标脏 / Poller 发现差异 → 生成 ChangeMask
  → NetSerializer 生成网络表示（量化）
  → FastArray / DeltaCompression 进一步裁剪
  → 按连接的 Filter/Prioritizer/带宽预算调度
  → 客户端创建/更新/移除对应副本
```

**核心心智模型：复制是 `Connection × Actor` 二维决策，不是广播。** 同一个 Actor 对不同连接可同时具有：

| 维度 | 差异 |
|---|---|
| 对象范围 | 连接 A 有副本，B 因距离/队伍/玩法区域不相关而没有副本 |
| 副本身份 | 服务器上是 Authority；拥有者客户端通常 AutonomousProxy；其他相关客户端通常 SimulatedProxy |
| 属性集合 | 弹药 `COND_OwnerOnly`；模拟移动 `COND_SkipOwner` / `COND_SimulatedOnly`；外观发给所有相关连接 |
| **历史基线** | 连接 A 已知 90、B 已知 100，新值同为 70 时两条连接要发的增量不同 |
| 发送时机 | 观察位置、优先级、丢包、带宽预算不同，不保证同帧收到相同数据量 |

标脏两条禁忌：**漏标脏 → 静默不同步**；**未修改却标脏 → 增加无意义采集与比较**。

### 3.4 三种「增量」各有其位

| 形态 | 增量键 | 中间增删 / 排序 | 适用 |
|---|---|---|---|
| `TArray<T>` + `Replicated` | **下标** | 后续元素**全部重发**（移位放大） | 小规模、尾部增删、语义接近整体替换 |
| `FFastArraySerializer` | `ReplicationID` | 只发新增/删除/真实修改 | 兼容旧代码，但要承担 poll 比较成本 |
| **`FIrisFastArraySerializer`（项目标准）** | `ReplicationID` + push changemask | 同上，依赖调用方正确标脏 | 新代码默认 |

FastArray 线上格式：

```
int32 ArrayReplicationKey
int32 BaseReplicationKey
int32 NumDeletes ; int32 DeletedIDs[NumDeletes]
int32 NumChanged ; { int32 ID; <payload> }[NumChanged]
```

两个易忽略机制：

- **隐式删除**：客户端在 `PostReceiveCleanup` 补算「凡 `MostRecentArrayReplicationKey` 落在 `(BaseReplicationKey, ArrayReplicationKey)` 区间内的元素」判定为「该删而没收到」。这就是「没变化也要发一个只有 header 的包」的原因。
- **单次更新上限**：变化数与删除数默认各 2048（CVar 可调），超了只打 Warning。

`IrisFastArraySerializer` 的 changemask 是 **1 位数组位 + 63 位元素位**，元素位按 `ItemIdx % 63` **取模复用** → 元素数超 63 会产生**假脏**（多发数据，不丢正确性）。

操作约定：加/改元素后必须 `MarkItemDirty`；删除后必须处理移位段（**能用 `RemoveAtSwap` 就别用顺序删除**，代价差一整段重发）；身份用稳定 ID，不要用下标。

### 3.5 频率 / 相关性 / 裁剪 / 生命周期

- `FPMNetworkFrequencyConfiguration`（`Source/MecuryGame/System/Network/PMNetworkFrequencyConfiguration.h`）：角色默认 / 角色战斗 / PS 默认 / 小怪默认 / 小怪警戒 / 小怪战斗。`APMCharacterBase` 构造时用角色默认（**30Hz**），怪物按激活状态切档。
- **复制频率 ≠ 帧同步**（`战斗网络框架.md` §3.10）。DS `NetServerMaxTickRate=30`；项目**不是全局确定性 Lockstep**。
- Iris 配置：`net.Iris.UseIrisReplication=1`、`net.IsPushModelEnabled=1`、`net.Iris.MaxScheduledObjectsPerFrame=150`、`net.Iris.ClassPooling.Enable=1`、`net.Iris.RemoteActorFreeze.Enable=0`。
- 相关性：**当前项目用 Iris Spatial Filter + `COND_NetGroup`**；**Replication Graph 没有启用**（`Config/`、`Source/`、`Plugins/` 均查不到 `ReplicationDriverClassName`）。
- 连接级 Out-of-Scope → 服务器发 `EndReplication`（带 `bCanFreezeInstance=true`）→ 客户端 `Class Pool / Freeze Pool / Destroy` 三级。真正 `Destroy()` 则 `bCanFreezeInstance=false`。
- `APMPickupInstance` 实现 `IClientClassPoolable`；**池化必须完整清理跨世代本地状态**（非复制字段、GameplayTag、Delegate、Timer、旧 Owner/Inventory/Marker 引用、碰撞、交互 Tag、特效、UI）。

### 3.6 复制频率配置的分档语义

| 档位 | 用途 |
|---|---|
| `Character_Default` / `_Min` | 玩家角色非战斗态 |
| `Character_Combat` / `_Min` | 玩家角色战斗态 |
| `PlayerState_Default` / `_Min` | PlayerState |
| `MonsterMob_Default` / `_Min` | 小怪默认 |
| `MonsterMob_Alert` / `_Min` | 小怪警戒 |
| `MonsterMob_Combat` / `_Min` | 小怪战斗 |

`*_Min` 是频率下限（对应 UE `MinNetUpdateFrequency`）。

### 3.7 不能照搬的部分（UE 引擎本体清单）

| 层 | 能否照搬 | 说明 |
|---|---|---|
| 双服务器拓扑、DS 生命周期、快照进/结算出、driver mode 状态机 | ✅ 完整可搬 | 纯架构 |
| `Connection × Actor` 二维决策、Relevancy、Dormancy、频率分档、`COND_*`、FastArray 语义、Class Pool/Freeze | ✅ 可搬 | 纯模型，需 C# 重实现 |
| RPC「同一份函数靠 role 分流」 | ✅ 语义可搬 | UE 靠 UHT 生成桩 + `GetFunctionCallspace` 虚函数；Unity 无 UHT → **必须自建外部生成器 + 运行时 role 判定** |
| `UNetDriver` / `UNetConnection` / `UActorChannel` / `FRepLayout` / `FRepState` 影子状态 / Bunch | ❌ 引擎本体 | 必须自写等价物 |
| Iris（`UReplicationSystem` / ReplicationBridge / NetSerializer / Fragment） | ❌ 引擎本体 | 必须自写 |
| Push Model `MARK_PROPERTY_DIRTY_FROM_NAME` | ❌ 依赖编译期生成的 `FProperty` 指针 | 必须靠外部生成器产出属性描述符 |
| Mover/NetworkPrediction/项目投射物 | ✅ 语义移植 | C#实现输入/状态历史、同帧校正重模拟、历史命中与身份生命周期 |
| GAS/StateTree/Behavior编辑器与完整玩法体系 | 不整体移植 | 提取预测/投射物依赖的激活裁决与撤销契约 |
| `UE_NET_DECLARE_SERIALIZER` | 声明语义可搬 | 外部生成器与静态序列化实现 |
| Mos 集群（Python） | ⚠️ 不搬 | hyld 局外是 C# 服务，保留 C# 实现，只搬职责划分 |

**产出物定义**：一套 **C# 重实现的 UE 网络模型**（下称 `PMNet` 层），语义对齐 UE/Iris，宿主为 Unity + hyld 的 C# 服务端。

**hyld 已有资产（可复用与退役边界）**：

| UE 侧 | hyld 现状 | 处置 |
|---|---|---|
| Mover/NetworkPrediction | hyld现有SavedMove/MoveAck与位置Replay是CMC风格局部机制 | 作为旧行为基准，替换为完整预测后端；共享数学可复用，不锁死旧时间轴/协议 |
| 字段级增量 | 旧state_mask与按连接baseline | 参考基线思路，由通用复制运行时取代业务打包 |
| 属性复制 + Push Model + `COND_*` + 量化 + 频率 + Relevancy + Dormancy + FastArray | ❌ 完全没有 | 新建 |
| 类型化 RPC + 可靠性分级 + 归属校验 | ❌ 现为 `MainPack` + 反射 `GetMethod(name)`，无归属校验、无可靠性概念 | 新建 |
| 双服务器（局外/DS 分离） | ❌ 一个 C# 进程同时跑 TCP 7778 + UDP 7777 | 拆分 |

---

### 3.8 HyldDS 的确定形态（D6-D9）

**拓扑**

```
┌────────────────────┐        TCP（长连接，全程挂着）        ┌──────────────────────┐
│ 客户端              │ ───────────────────────────────────▶ │ HyldLobby（局外）      │
│ （Unity 玩家构建）   │ ◀─────────────────────────────────── │ 单个 C# 程序           │
│                    │      登录 / 好友 / 房间 / 匹配 / 结算    │ 账号·持久化·匹配       │
│                    │                                       │ + DS 注册表与分配      │
│                    │        UDP（仅战斗期）                  └──────────┬───────────┘
│                    │ ───────────────────────────────────▶            │ 局内快照 / 结算
└────────────────────┘                                       ┌──────────▼───────────┐
                                                            │ HyldDS（局内）        │
                                                            │ Unity 打包产物        │
                                                            │ 每局一个进程，打完销毁 │
                                                            │ 战斗权威              │
                                                            └──────────────────────┘
```

与 ProjectMecury 的对应关系：

| ProjectMecury | hyld 目标形态 |
|---|---|
| Mos 逻辑服集群（角色众多） | `HyldLobby`：单个 C# 程序（不再拆 gate/game/match 等角色） |
| `globalsys.GlobalDsMgr`（全局 DS 调度） | `HyldLobby` 内的 DS 分配器 |
| `battleguard`（每台战斗机拉起/销毁 DS、中转 RPC） | 首版由 `HyldLobby` 直接拉起本地 DS 进程（D6：按局拉起 + 销毁）；若后续需要多战斗机，再抽出独立的 guardian |
| `MecuryServer.Target.cs`（UE DS 构建目标，`ENABLE_NEPY_GAME_EXPORT=0`） | Unity 的 DS 构建配置：`StandaloneWindows64` + **编译期 `BuildOptions.EnableHeadlessMode`**（实测可用，见 §2.8）+ 运行时 `-server` 身份判定 |
| `server_entry.py` / `ds_logic_mgr`（DS 只跑通信，战斗权威在 C++） | DS 侧的轻量通信层 + 战斗权威全在 Unity C# 脚本 |

**服务端身份判定（D7 的落地方式）**

Unity 2019.4 没有 `UNITY_SERVER`，因此不能用编译期宏区分。落地方式：

```csharp
// 依据：Unity 2019.4 无 UNITY_SERVER，无法用编译期宏区分客户端/服务端 →
//       身份只能运行时从命令行参数判定（与图形无关；图形无头化已在构建期用
//       BuildOptions.EnableHeadlessMode 解决，见计划 §2.8 实测）。
// 因此打包成常规 StandaloneWindows64，靠运行时参数判定身份。
public static bool IsDedicatedServerProcess()
{
    string[] args = System.Environment.GetCommandLineArgs();
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "-server") { return true; }
    }
    return false;
}
```

启动分流：进程启动时先判身份，据此设置 `PMNetMode`（`DedicatedServer` / `Client`），再决定初始化哪条链路（DS 侧不初始化 UI/输入/相机，客户端侧不初始化战斗权威）。

**必须同时建立的纪律**：由于没有编译期裁剪，**服务端/客户端分支必须全部走 `PMNetMode` 与 `PMNetRole` 判定**，禁止用「当前是不是编辑器」「场景名」之类的间接条件代替（否则 DS 上会出现难以定位的表现层副作用）。

**配置单一化要求（D9 的前置）**

要把两套仿真合并，前提是**数值配置与表现引用分离**：

| 类别 | 例子 | 归属 |
|---|---|---|
| 数值参数 | 血量、移速、子弹速度/射程/伤害/散射角/数量、蓝耗、大招能量、碰撞半径 | **共享配置**（两端都能读，单一事实源） |
| 表现引用 | `GameObject shell`、`大招实体`、`Boom`、拖尾特效 | 仅客户端（DS 构建里不需要，也不该加载） |

当前客户端 `Hero` 类把两类字段混在一个结构里（`HYLDStaticValue.cs:460-518`），服务端 `HeroConfig` 是人工副本 —— 这一步不拆，迁移完仍是两份配置。

**DS 构建与运维（P2' 交付）**

| 项 | 做法 |
|---|---|
| 构建入口 | Editor 菜单项 + 命令行 `-executeMethod`，产出 `HyldDS/*.exe` |
| 启动参数 | `-server -logFile <path>` + 由 Lobby 传入的 DS 标识/端口。**图形无头化已在构建期完成**（`EnableHeadlessMode`），`-batchmode -nographics` 变为可选/冗余（保留以兼容回退路径） |
| 每局生命周期 | Lobby 匹配成功后拉起 → DS 就绪上报 `ip:port` → Lobby 下发客户端 → 打完 DS 自毁 |
| 必须实测的指标 | 单 DS **启动耗时**（引擎初始化）与**常驻内存**；这两个数字决定后续是否需要预热池（见 §8 的 DS-2） |

### 3.9 架构审核、目标与模块契约（本轮纠偏后的实施依据）

**目标：在 Unity 2019.4 内建立 C# 实现的 UE 式网络对象、声明 RPC、属性复制和可靠传输框架，迁入 ProjectMecury 的 Mover/NetworkPrediction 与投射物预测、历史校验机制。Unity 打包的 DS 在主线程承载全部局内权威逻辑，Lobby 负责局外与进程编排。业务通过声明和框架接口同步，不再手写战斗状态包。**

本节覆盖与其冲突的历史草案。此前“排除 Mover”“SavedMove/MoveAck 保留不动”“把旧 BattleController 搬进 DS 即完成仿真迁移”的判断撤销。保留旧代码中的数值与适用数学，不冻结旧时序、旧包结构和已知缺陷。用户允许删除重写，不要求长期兼容旧战斗协议。

本轮证据等级：静态架构审核，已读计划全篇、关键 C# 源码，并抽查 UE 项目直接实现；没有重新编译、抓包或实机验收。历史测试结果仍有效于当时被测范围，不据此推导新框架正确。

#### 3.9.1 审核发现与必须纠正的偏差

| ID | 严重度 | 证据与问题 | 处置 |
|---|---|---|---|
| A1 | 高 | 原 §0.2 排除 Mover，原 §3.7/P5' 要求旧 SavedMove/MoveAck/Replay 不动 | 与用户目标冲突；正式迁入预测后端与运动状态机制 |
| A2 | 高 | `PMNetConnection.Send` 只有抽象声明；`PMUdpRouter` 文件头明确裸 MainPack 无序号/ACK/重传 | Reliable 标记尚未兑现；可靠信道单列交付与测试 |
| A3 | 高 | `Tools/PMNetGen/Program.cs` 只读取 proto；未建立 C# 标记→业务网络桩链 | 扩展外部生成器扫描业务声明；不把 DTO 序列化当作声明式 RPC 已完成 |
| A4 | 高 | `BattleData.ReplayUnconfirmedInputs` 仍是移动积分重放；共享数学未覆盖完整可回滚状态 | 新后端保存 Input/Sync/Aux/时间步，恢复同帧状态后重模拟 |
| A5 | 高 | `BattleData.ApplyAttackAcks` 拒绝时只清 pending 和校正资源，未撤销预测子弹实例 | 建立预测 ID→实例→权威对象映射、接管与拒绝收尾 |
| A6 | 高 | 旧 P3'-3b 要先删除大厅仿真，启动器与结算却留在 P4' | 新开局/结算链具备接收者后才拆旧链；产品验收不能只手工注入名册 |
| A7 | 高 | `PMRpcDispatch.ShouldCallRemoteFunction` 在 receiverIsServer=true 时直接放行，与 §3.2 的 `!IsServer || bNetOwner` 相反 | 接线前修正并测试角色/Owner 真值表；本轮代码未修，不能宣称 RPC 校验已完成 |
| A8 | 中 | P0 编译/序列化 PASS 不证明 RPC/复制运行；权威写入门禁有狂暴瓶真实违反的豁免 | 完成口径拆开；“越界=0”只能说明没有未登记写入 |
| A9 | 中 | 原 API 草案使用 C#7.3 不支持的 partial property；普通 Attribute 不会自动拦截方法调用 | 用标记字段生成访问器、Implementation 生成调用入口；禁止依赖不存在的语言能力 |

主要证据：`Client/Assets/Scripts/PMNet/PMNetConnection.cs`、`PMRpc.cs`；`Tools/PMNetGen/Program.cs`；`Client/Assets/Scripts/Server/Boot/PMDsHost.cs`（仍为诊断 handler）；`Manger/Battle/BattleData.Prediction.cs`、`BattleData.Attack.cs`。来源语义见 ProjectMecury 的 `Docs/战斗系统/01_战斗框架/战斗预测与权威.md`、`Docs/战斗系统/06_实体与表现/投射物与位移.md`、`.agents/skills/mover-quick-start/references/npp-independent-tick-frame.md`。已抽查 `APMNetProjectileBase::BeginFakeProjectileSynch`（预测弹/镜像接管）、`APMNetProjectileBase::ExecuteVerifyProjectileHit`（分层验证与裁决）以及 `UPMMoverNetworkPredictionLiaisonComponent::GetConfirmedSimFrame`（按端取帧的辅助函数）。源码路径位于 `Source/MecuryGame/Gameplay/Projectile/` 与 `Pawn/Movement/Mover/`。

#### 3.9.2 完成后的产品形态

```text
客户端 Unity                              Lobby（独立 C# 程序）
  ├─ Lobby 连接：登录/好友/房间/匹配  ←→  会话、匹配、DS分配与进程管理
  │                                          │ 认证名册/入局快照
  │                                          │ 就绪/结果/退出确认
  └─ DS 连接：RPC/复制/预测输入        ←→  Unity DS（每局一个进程）
                                             局内权威世界/碰撞环境
                                             角色/道具/子弹/伤害/胜负
```

客户端、DS 共用运动模型与状态类型；DS 有最终裁决权，客户端有自己的预测副本和表现层。Lobby 不逐帧模拟战斗。DS 不加载客户端输入、UI、相机，但必须加载权威逻辑需要的场景、障碍和碰撞数据；空引导场景能启动不是完整战斗服验收。

#### 3.9.3 模块划分与依赖（命名为拟定落点，职责为硬边界）

不把所有模块塞进 `PMDsHost` 或 `BattleController`。既有平铺文件可以渐进归位；Unity 路径新增 C# 必须配 `.meta`。目录划分不等同于必须立即拆 `.asmdef`。

| 模块 | 拟定单一源码落点 | 负责什么 | 依赖/明确边界 | 首次验收 |
|---|---|---|---|---|
| M01 Core/Protocol | `Client/Assets/Scripts/PMNet/Core/`、`Protocol/` | NetId、ConnectionId、SessionEpoch、类型/RPC/属性ID、Role/Mode、时间步及帧标识、协议摘要 | 纯C#；不含Unity/玩法/Socket业务 | T37/T40 |
| M02 CodeGen | `Tools/PMNetGen/`；生成物在对应网络业务partial旁或专用Generated目录 | 扫描标记、语义校验、稳定ID、调用桩、分发表、参数编码、字段访问器、复制描述符 | 开发期工具；生成代码只依赖运行时契约；不可强制全部partial类进入同一namespace | T40 |
| M03 Transport/Channels | `PMNet/Transport/` | Socket适配、会话握手、序号、ACK、重传、去重、顺序域、分片/重组预算、背压、超时 | 依赖M01；载荷为不透明字节；不认识HP/英雄/子弹 | T38 |
| M04 NetWorld/ObjectRegistry | `PMNet/Runtime/` | 网络对象/组件登记、Owner链、NetId与世代、Create/Destroy、引用解析、连接状态、主线程派发入口 | 依赖M01/M03；生命周期与复制/RPC通过接口协作，不反向依赖玩法 | T39/T41 |
| M05 RPC | `PMNet/Rpc/` | callspace、生成桩的发送、接收分发、方向/Owner校验、可靠性选择、参数验证接口 | 依赖M03/M04及生成描述符；底层重传不重执行业务；不把RPC当属性历史 | T39/T40 |
| M06 Replication | `PMNet/Replication/` | 属性版本/脏位、每连接baseline、量化、条件、OnRep、FastArray、相关性/休眠/频率/预算 | 依赖M04/M03；只提供权威状态传递，不执行回滚；ACK按发送版本确认 | T41/T46 |
| M07 Prediction | `Client/Assets/Scripts/PMPrediction/` | Input/Sync/Aux历史、每实例时间轴、确认点、ShouldReconcile、Restore/Resimulate、Finalize、重同步 | 纯C#核心；经接口接M05/M06，不能直接依赖BattleData、MainPack或Mover具体玩法 | T43 |
| M08 Mover | `Client/Assets/Scripts/PMMover/` | 运动模式、叠加运动、瞬时效果、Modifier、混合、状态采集恢复、移动身份策略 | 依赖M07；碰撞查询经接口由Unity适配；状态必须足够重模拟，副作用单独处理 | T43/T44 |
| M09 Projectile/LagComp | `Client/Assets/Scripts/PMProjectile/` | 预测ID/权威ID关联、镜像接管、服务器直创、历史位置查询、命中校验、等待裁决、停止墓碑 | 依赖M04–M07的契约；不把全世界回滚，不直接调用UI或大厅 | T45 |
| M10 BattleGameplay | `Client/Assets/Scripts/Battle/` | 英雄、武器/攻击、资源、道具、伤害、死亡、胜负、激活裁决；读取Shared配置 | 依赖M05–M09；只能通过框架接口同步；最终真值归DS | T46 |
| M11 UnityHost/Presentation | 宿主沿用 `Scripts/Server/Boot/`；适配在 `Scripts/PMUnity/` | Update驱动、场景/碰撞适配、输入采样、Actor/GameObject绑定、渲染/音效/UI及平滑 | 外围依赖核心；核心不得反向依赖MonoBehaviour/UI；DS关闭表现 | T42/T44 |
| M12 Lobby/Control | `Server/` + 共享控制契约 + DS侧LobbyAgent | 登录/匹配/凭据/进程、入局快照、就绪、结果、退出确认与失败回收 | Lobby不引用Unity/Mover/战斗仿真；DS结果由此回到局外 | T42 |
| M13 Test/Diagnostics | `Tools/` 与Unity专用测试场景 | 可控丢包乱序重复、双端日志对账、帧/ID轨迹、故障注入、构建与生成校验 | 测试设施，不把旧调参脚手架作为生产依赖 | T38–T47 |

依赖原则：基础类型在最底层；传输只搬字节；网络对象运行时向RPC/复制提供注册与生命周期；预测核心通过适配器收输入/权威状态；Mover与投射物使用预测和历史契约；玩法依赖框架；Unity/Lobby作为宿主接线。互相协作的模块用底层接口消除循环引用，不各自再造第二套时钟、连接、对象ID或属性真值。

#### 3.9.4 必须共同遵守的运行时契约

**身份与生命周期**

- 对象身份由 `SessionEpoch + NetId + Generation` 区分，预测对象另有稳定 `PredictionKey`；不得用GameObject实例地址、数组下标或单个AttackId混代所有身份。
- Authority/AP/SP 是每个对象副本的角色，不是只有进程级 `IsDedicatedServer`。Owner来自认证会话/服务器分配，不信任客户端包内自报uid。
- 对象Create、初始状态、后续Update/RPC、Destroy有明确先后；未解析引用有有界等待/失败策略；旧世代包不能作用到复用对象。

**可靠性与复制**

- Reliable RPC 在连接有效且网络恢复的条件下，同一声明通道保序、重复包不重复交付，或明确失败/断连。无跨通道全局有序保证；无断线重连后业务恰好一次保证。结算用业务ID另做幂等。
- 高频输入/位置快照按用途走可丢弃或冗余窗口策略，不要求排队重传每个旧值。不能为了“可靠”把全部运动流堵在一个队列里。
- 属性复制保证当前状态最终收敛，不保证每次中间赋值都能被观察。需要逐次执行的事件用RPC/事件序列。
- 每连接baseline只能前进到已确认版本。旧ACK不能清除新修改的脏位；断线、失去相关性后重新进入、初始状态丢失都有恢复路径。序号、窗口、重组和排队内存必须有界。
- 可靠RPC不能推出另一个通道的属性已到达，也不能推出异步生成已完成。投射物Spawn/Verify共享顺序域；仍需Pending等待。

**预测、Mover与时间**

- 定义 `InputCmd`（意图）、`SyncState`（必须恢复的状态）、`AuxState`（影响模拟的辅助状态及版本）、`SimTimeStep`。同一输入、起始状态、环境查询和时间步应得到可接受误差内的一致结果；不能承诺Unity物理跨平台逐位确定性。
- AP保存每次输入与结果；权威确认对应输入/输出边界，比较同一帧状态，恢复完整状态，再按历史时间步重模拟未确认输入；不是只拉回Position重做位移。
- DS校验输入序号、时间步范围、每连接/每实例模拟预算与超前量；客户端DeltaTime不能任意扩大仿真进度。重复输入、缺帧补偿与超预算处理必须有明确规则及故障注入测试。
- 项目源为IndependentTick：每实例输入时间轴与权威输出对应；SP按仿真时间插值，项目额外外推/平滑属于表现路径。不能用DS全局Tick号替代角色输入帧号。
- `Unity Update`、仿真步长、复制发送频率分别定义。原16ms累加器只属于hyld旧实现，不能直接宣布与源项目等价。默认移植IndependentTick语义；任何固定帧简化须记录差异与验收，不能静默变成全局Lockstep。
- Mode、Velocity、运动参数、活动Effect/LayeredMove/Modifier及其ID、碰撞规则等凡会影响后续模拟的量都必须可恢复或确定性重建。表现Finalize与重模拟分离；重复模拟不重复播音效/生成子弹/扣血。
- 历史溢出、权威陈旧、断线恢复必须定义重同步策略；不能悄悄拿“当前值”替代丢失历史后继续假装校验成功。

**投射物与权威裁决**

- 原项目 `APMNetProjectileBase` 使用假弹与权威镜像ID关联/接管，服务器直创是另一条路径；不是所有子弹套Mover，也不是把世界整体倒带。
- 必须支持预测生成→权威确认接管/拒绝撤销；假弹已结束后镜像迟到不能再次可见；同一颗弹/同一目标不能重复结算。
- 命中候选携带约定的历史锚/时间和轨迹信息；DS按权威子弹、目标历史、资源与生命周期校验后结算。客户端可以即时表现，但不能直接决定伤害。
- 移植源项目三类等待语义：生成尚待激活裁决、Verify先到但Spawn未完成、验证完成但最终裁决Pending。分别有容量、TTL和清理策略；Pending不得提前扣血。
- 停止与彻底销毁分开；墓碑期允许合法迟到命中验证，最终清理后旧世代请求无效。没有照搬GAS/Behavior时，也必须有轻量激活ID与Pending/Confirmed/Rejected等价协议。
- 狂暴瓶等影响预测的局内效果由DS裁决并进入对应复制/预测状态，不能保留永久“客户端加数值”豁免。

#### 3.9.5 声明生成与编程体验

C# Attribute 对应UE声明标记，但不会自动改变普通方法调用。采用外部源码生成约定：业务编写标记的 `_Implementation`，生成器生成正常方法名的入口；属性标记放字段上，由生成器生成访问器、属性ID与复制元数据。以下是**拟定API，不是已存在的功能**：

```csharp
public partial class PMBattlePlayer : PMNetObject
{
    [PMServerRpc(Reliability = PMRpcReliability.Reliable)]
    private void ServerCastSkill_Implementation(int skillId) { /* DS业务 */ }

    [PMReplicated(Notify = nameof(OnRep_Hp))]
    private int _hp;
    private void OnRep_Hp(int oldValue) { /* 客户端表现 */ }
}
// 生成器生成 ServerCastSkill(...)、Hp访问器、参数编码、接收分发与复制描述符。
```

外部工具可以在net8.0中使用Roslyn扫描业务C#，产物必须是C#7.3/.NET Standard 2.0。Unity构建前检查生成产物同步，运行时不再扫描反射寻找RPC。缺生成入口、重复ID、非法签名、未知类型、继承冲突、协议摘要不一致要明确失败。RPC属性的具体字段名应和实际API一致（现有声明是 `Reliability`，不是草案曾写的 `Reliable=true`）。

protobuf可继续作为底层编码；它既不实现可靠性，也不定义回滚。旧万能MainPack不是必须保留的协议形态。生成器需要稳定ID/协议版本检查，不得以源码声明顺序临时编号。

#### 3.9.6 何时才能说迁移完成

必须完成：Lobby自动开局与回收、Unity DS承载全部局内权威、声明生成RPC/属性实际运行、可靠通道弱网验证、角色完整状态回滚、投射物身份/命中/拒绝闭环、旧业务状态收发与旧预测退出。一个未参与旧代码的新增测试对象，只通过声明即可复制属性/调用RPC，是框架可复用性的最低验收。

无需为了完成迁移照搬整个UObject/UE编辑器/GAS/StateTree，也无需优先复刻Iris的内部位图布局和池化细节；但这些裁剪不能成为排除用户要求的预测/回滚/命中语义的理由。模块职责与依赖按本节执行；具体窗口大小、MTU预算、历史容量、误差阈值、碰撞适配细节在R0/R1/R4按实测收敛，并记录参数来源。

## 4. C# 侧 API 规格（PMNet 层，设计草案）

> 目标：让业务代码的心智模型与写 UE 时一致。以下均为**待实现**的设计。

### 4.1 网络身份

```csharp
namespace PMNet   // 禁止落在 Manger / HOTFIX 命名空间
{
    public enum PMNetRole : byte { None, Authority, AutonomousProxy, SimulatedProxy }
    public enum PMNetMode : byte { Standalone, DedicatedServer, Client }

    public abstract class PMNetObject
    {
        public uint NetGuid;                       // 对应 NetGUID / NetRefHandle
        public PMNetRole Role;                     // 当前副本身份
        public PMNetMode NetMode;
        public PMNetConnection OwnerConnection;    // 对应 Owning Connection
        public bool HasAuthority => Role == PMNetRole.Authority;

        protected virtual void GetLifetimeReplicatedProps(PMRepList Out) { }
        public virtual bool IsNetRelevantFor(PMNetConnection c, PMNetObject viewer) => true;
    }
}
```

### 4.2 RPC

采用§3.9.5的“标记Implementation + 生成正常方法名入口”。Attribute本身不拦截调用。生成桩按role/方向/Owner判定Local/Remote/Absorbed；接收侧通过生成分发表调用Implementation；可靠性由M03兑现。

当前PMRpcDispatch尚未接入业务且存在归属判断错误，其他callspace分支也必须按UE源码真值表审核，不能把现有注释直接当作规范。

### 4.3 属性复制

采用§3.9.5的标记字段与生成访问器，不使用C#7.3不支持的partial property。生成稳定属性ID、复制条件、量化、OnRep和脏版本入口；对直接绕过Setter改字段的行为制定分析器/规范约束。

复制运行时按连接版本baseline工作。量化可以在protobuf编码之前进行；量化误差须与预测校正阈值一起验收。预测状态与权威复制镜像的写入边界显式分开。

### 4.4 相关性 / 休眠 / 频率

```csharp
bAlwaysRelevant = false;
NetCullDistanceSquared = 100f * 100f;                    // 平方比较，免 sqrt
NetUpdateFrequency = PMFreqConfig.CharacterCombat;       // 对应 PMNetworkFrequencyConfiguration
MinNetUpdateFrequency = PMFreqConfig.CharacterCombat_Min;

SetNetDormancy(PMNetDormancy.Dorm_Initial);              // 静止不发
FlushNetDormancy();                                      // 属性变化时唤醒
```

### 4.5 FastArray

```csharp
public class PMFastArray<T> where T : PMFastArrayItem
{
    public void MarkItemDirty(T item);   // 新增/修改
    public void MarkArrayDirty();        // 删除/重排
}
```

目标是按稳定身份增量增删改、丢包恢复与通知语义。`{ArrayReplicationKey, BaseReplicationKey, DeletedIDs[], ChangedItems[]}` 可作参考；63位取模等Iris内部布局不强制照搬，具体结构由R2/R6验证决定。

### 4.6 客户端副本池

```csharp
public interface IPMClientClassPoolable { bool IsClientClassPoolingEnabled(); void ResetForPoolReuse(); }
```
池化是R6按需要接入的优化，首版可直接销毁；不强制复刻Class/Freeze两级池。一旦复用，`ResetForPoolReuse()`必须清理跨世代本地状态，并遵守对象Generation契约。

---

## 5. 分阶段实施（架构审核后的当前执行顺序）

原P0/P1/P2'/P3'-1/P3'-2/P3'-3a/P3'-3c保留为历史交付，不等于完整框架交付。原P3'-3b、P4'–P7的未完成项重新映射至下表；不得继续执行“旧预测不动，只搬BattleController”的旧路线。R表示阶段，T表示验收，A表示本轮审核发现。

| 阶段 | 模块/交付 | 依赖 | 验收 | 状态 |
|---|---|---|---|---|
| R0 详细契约与来源映射 | M01：冻结ID/世代、RPC调用API、顺序域、复制版本与ACK、每实例帧与状态、投射物裁决；逐项标注源实现与Unity适配差异 | 本轮目标与模块边界 | T37 | **完成**（交付物 `Docs/plans/net-r0-contract.md`：50 条冻结决策 D-R0-01..50、13 模块来源映射、M01 API 契约、有意裁剪清单、待收敛参数；证据附录 4 份于 `D:/hyld-refactor-survey/R0_*.md`） |
| R1 连接/通道/对象运行时 | M03/M04/M05最小部分：认证连接、可靠/不可用通道、ACK重传去重保序、MTU与分片预算、背压、对象Create/Destroy/Owner；修RPC骨架归属判断 | R0 | T38、T39 | **基本完成**：M01 + M05（经独立对抗审查修正 6 处缺陷，门禁 93/93）；**M03 可靠传输**（三种顺序域、序号/去重/保序、ack 位图 + NAK + 发送端丢包推断、分片重组有界、背压、会话世代，门禁 **47/47**，实现中查出并修正 4 处缺陷 —— §5.2）；**M04 对象运行时**（对象登记、NetId 单调不复用、Create/Destroy 走可靠域且创建与初始状态原子到达、Owner 链逐字对齐 UE 三处覆写、每连接待发与全量补齐、有界与回滚，门禁 **152/152**，实现中查出 1 处缺陷、负向验证又补出 1 处测试盲区 —— §5.3）。**剩余仅「认证连接」**（依赖 M12/R3 的大厅↔DS 握手）与 MTU 等参数实测收敛（契约 §9） |
| R2 声明生成与最小复制 | M02/M05/M06：声明生成、复制及内存投递闭环 | R0；接线依赖R1 | T40、T41 | **独立审查返工已通过针对性门禁，非真实网络验收**：声明71、复制265、世界197、E2E209项通过；详见文末RV1–RV7。真实UDP/Unity、跨程序集继承、字节预算及自定义有副作用Reader限制仍未闭合。此前“完成”口径已撤回。 |
| R3 Lobby/Unity DS全链路 | M11/M12：大厅拉起DS→凭据/入局快照→就绪地址→客户端进局→结果幂等回传与确认→退出；DS加载权威场景/碰撞环境 | R1/R2；编排实现可并行 | T42 | **进行中**：R3-A控制与会话适配已交付（473/343），独立审查问题已返工；R3-B真实宿主、两客户端T42仍PENDING |
| R4 Mover/NetworkPrediction移植 | M07/M08：先一个角色Input/Sync/Aux历史、同帧校正、恢复重模拟、Finalize、SP插值；再接运动模式/叠加/效果/Modifier、参数变更与拒绝撤销。帧锚/B7在此解决 | R1/R2；测试场景可先于R3完成 | T43、T44 | **进行中**：R4-A预测417/Mover245/集成264项通过；R4-B接线与自动化门禁通过，真实PhysX/Unity与完整裁决待验，T43/T44整体仍PENDING |
| R5 投射物与历史命中 | M09：预测ID/权威对象关联、接管、服务器直创、历史查询、Pending裁决、拒绝清理、停止墓碑；DS最终伤害 | R2、R4帧锚契约 | T45 | PENDING |
| R6 全业务切换/旧链退役 | M10及集成：角色/攻击/资源/道具/死亡/胜负改接框架；完善FastArray/裁剪/休眠/调度；删除旧战斗MainPack/state_mask/连发/旧SavedMove路径；回放与文档收尾 | R3/R4/R5 | T46、T47及旧T项适用部分 | PENDING |

R0契约冻结后，R1与R2生成器部分可并行；R3与R4可按文件边界并行。最终切换必须确认新开局与结算都有接收者，再拆除大厅旧仿真。允许开发中短暂并存用于对照，但不要求兼容旧协议，不允许两套权威长期共同写同一对象。

每阶段先做一个可运行的最小闭环，再扩大业务。R2最低成果是新增对象只加声明即可跨端RPC/复制；R4最低成果是强制差异后完整状态回滚；R5最低成果包含被拒子弹的实际撤销。不能以“生成了几个类”“公式逐位等价”“编译通过”替代这些验收。

### 5.1 距离裁剪的诚实口径（P5' 相关）

3v3 玩家之间做距离裁剪**收益接近于零**（地图出生点 ±15，玩家必然互相可见；当前「所有玩家对所有客户端恒 relevant」在 3v3 下本就不是瓶颈）。

**真正的裁剪收益必须先有「可复制对象集合」才存在**——现在子弹完全不复制，所以没有可裁对象。收益来自新复制链的量化、休眠与发送调度，先通过可靠性和正确性验收再评估带宽。

### 5.2 两条必须守住的验收基线

1. **正确性与手感**：旧日志作为历史基准，新链记录输入帧/确认帧、状态差异、重模拟次数、预测ID和命中裁决。数学等价只约束保留的公式，不冻结旧帧号混用、错误行为或旧MoveAck编排。
2. **带宽可量化**：抓包记录每客户端KB/s、单包峰值、ACK/重传与队列。历史估算基线为34–50KB/s；新MTU/分片预算在R1确定，不把旧1024B收包上限沿用为设计事实。

---

### 5.1 R1 首轮的独立审查与修正（记录，便于后续避免同类）

M05/M01 落地后做了**三路独立只读审查**（对抗性审查 callspace 与门禁、审查 M01 身份/帧契约、审查 R0 契约文档一致性）。审查共确认 **6 处真实缺陷**，均已修正并补入门禁：

| # | 缺陷 | 性质 | 修正 |
|---|---|---|---|
| 1 | `PMFunctionCallspace` 位值与 UE 相反（写成 `Local=1, Remote=2`，UE 是 `Remote=0x1, Local=0x2`） | **与自己的契约都矛盾** | 改回 UE 位值 |
| 2 | `#14-②` 的 LocalPlayer 分支写成**无守卫的独立 `if`**（UE 里它嵌在 `if (NetConnection == nullptr)` 内） | 实现偏离 UE；用例还用了 UE 不可达的配置 | 改为嵌套结构，并补 UE 合法形态用例 |
| 3 | `ComputeGlobalCallspace` 三处偏离：判据用 `LocalRole`（应为 NetMode）、范围写成 `!= Client`（应为 `== DedicatedServer`）、默认值可被漏赋为 Absorbed（UE 默认 Local） | 实现偏离 UE 且**门禁零覆盖** | 按 `UEngine::GetGlobalFunctionCallspace` 原文重写；删注入式字段改为现场推导；新增 G 组断言 |
| 4 | 便捷重载把 `HasNetConnection` 写成 `serverSide \|\| isOwner`，DS 上恒 true | **为让用例变绿而引入的回归**：无主对象调 Client RPC 从 UE 的 Local 变 Remote | 改回 `isOwner`；补 `isOwner=false` 回归用例 |
| 5 | `PMNetIdAllocator` 注释理由写错（声称"加锁会导致复用"，实际 `Interlocked` 只保证唯一）+ 单实例前提未声明 + 未挡跨线程 | 文档误导 + 静默发重号风险 | 纠正理由、声明 3 条前提、加线程守卫 |
| 6 | `PMFrameId` 缺顺序比较运算符（只有 `-`/`==`/`!=`） | API 缺口，调用方无法判"哪一帧更新" | 补 `<`/`>`/`<=`/`>=`，同域才允许，否则抛 |

**契约文档同步修正 5 处**：D-R0-02 依据改为"本项目自定"而非"UE 推出"、D-R0-14 范围与裁剪对齐、D-R0-35 与 §9 的"三类等待都有容量上限"纠正为"挂起 Spawn 无数量上限"（与附录矛盾）、§2.3 裁剪清单补 4 项、§4 补 Replication 流的包级机制行。

**两条值得记住的教训**：
1. **自建门禁最大的风险是"把门禁改弱以让它变绿"**。本次 3 次 FAIL 中，2 次是测试期望错、1 次是两者都错；只有引入外部对抗审查才把"期望按实现写"这类问题分开。后续每个阶段落地后都应做一次独立审查。
2. **连续打补丁会在文件里累积形式损伤**（本次出现过一次误删换行把 `}` 并到上一行，虽能编译但让后续编辑无法精确匹配）。同一文件多次修补后应整份重写。

### 5.2 M03（可靠传输）实现中查出的 4 处缺陷

这些不是审查发现的，而是**写测试时被断言抓出来的**——说明"把契约写成可执行断言"本身就在产出正确性。

| # | 缺陷 | 后果 | 修法 |
|---|---|---|---|
| 1 | **确认位图解码 off-by-one**：`ackBits` 的 bit i 表示包 `ackId - 1 - i`，实现写成 `ackId - i` 且多跳过了 bit0 | **最危险的一类**：对端报"包 1 收到"时，本端把**包 2** 上的可靠消息退休 —— 静默丢掉一条从未送达的消息。表现为"偶发丢事件"，且不报错 | 按 `ackId - 1 - i` 解码；位图编解码必须成对核对 |
| 2 | **发送端没有丢包推断**，只做了接收端 NAK | 接收端把"收到的第一个包"当基线，**看不到"第一个包就丢"**；那条可靠消息永远不被重传，最终靠发送窗溢出断连 | 增加 `RetransmitOlderThan`：收到 ack 后，若某消息最近一次发送已在水位之前仍未退休 ⇒ 判定丢失并重传 |
| 3 | **没有纯 ack 包**：ack 只搭车在数据报上 | 若本端没有数据要发，就永不把 ack 送出去 ⇒ 发送端无法退休可靠消息、也无法推断丢包 ⇒ 靠溢出断连 | 增加 `_ackDirty`：有未告知的 ack 且无消息要发时，发一个 header-only 数据报（UE 同样靠 keepalive 携带 ack） |
| 4 | **分片偏移两端不同源**：发送端按 `maxPayload` 切、接收端按 `CeilDiv(totalLen, fragCount)` 拼 | 两者在多数规模下不等（例：5000/266 → 19 片，而 5000/19 → 264）⇒ **长度对、字节错**的静默损坏 | 统一为 `FragmentSize(count, fragCount)`，发送端按它切片 |

另有一处**带宽浪费**（不影响正确性）：接收端 NAK 与发送端 ack 推断可能同时决定重传同一条消息，把两份塞进同一数据报。已加 `Queued` 标记去重。

**已知缺口（诚实记录）**：`PMTransportConfig.MaxDatagramBytes=1200` 等取值是初值，需按 R2/R6 的实测（真实 MTU 与丢包率）收敛并回写契约 §9。

### 5.3 M04（对象运行时）实现中查出的缺陷与测试盲区

| # | 缺陷 / 盲区 | 怎么发现的 | 后果 | 修法 |
|---|---|---|---|---|
| 1 | **「筛选 + 清空」把未发送的记录静默丢掉**：相关性不满足时 `continue`，循环结束后却无条件 `Pending.Clear()` | 写代码时自查（注释里已写下这个问题，但当时没改） | 不相关的对象**永远不会**出现在对端，且进入范围后也补不上 —— 表现为「远处物体永远不出现」，极难归因 | 用一个 `_stillPending` 列表重建「仍未发出」的集合，再整体替换待发表 |
| 2 | **测试对象全是「永远相关」，相关性分支零覆盖** | **负向验证**：注入 #1 的缺陷后门禁 **0 项失败** | 门禁对整条相关性路径是**假的绿** —— 若没有负向验证，这个缺陷会连同它的测试一起被当成「已验证」 | 新增 D8/D9：不相关对象不产生字节但**保持待发**；进入范围后补发**发那一刻**的全量初始状态；混合批次只送相关的那条 |

7 个负向注入中 6 个被抓，第 7 个（注入 #1）暴露出上述盲区。补上 D8/D9 后重跑注入，**4 项失败** ⇒ 盲区封闭。

**顺带修正（非缺陷）**：`PMNetReader.cs` 原本是 noBOM + LF，与其自身的项目规则（含中文源文件 ⇒ UTF-8 + BOM + CRLF）不一致。
在为其新增 `ReadRawBytesCopy` 时一并归一化；归一化后 `PMNetVerify` 的 27 项字节级测试仍全绿，证明内容未被破坏。

**已知未覆盖（诚实记录）**：
- M04 只覆盖「服务端 → 客户端」单向下行。**客户端上行到服务端**要等 R3 接线后才有真实宿主。
- 相关性判定只用到对象自带的 `IsNetRelevantFor` 钩子；距离迟滞、AlwaysRelevant 继承、附着继承属 M06。
- 相关性不满足的待发事件会一直留在队列里（有界于对象数上限），尚未做「长时间不相关则转为按需重建」的优化。

### 5.4 R2 待收口缺陷登记（两路并行实现的独立审查产出）

R2 采用「冻结前置 + 两路并行实现」，两路都在报告里主动登记了自己发现的接口张力。以下 8 项**尚未修复**，按影响排序。

| # | 问题 | 后果 | 建议处置 |
|---|---|---|---|
| 1 | **RPC 实参靠「对象上的实参帧」传递**：契约 §4.1 把发送委托定成 `PMNet_RpcWrite_<方法>(PMNetObject, PMNetWriter)`，没有实参位；实现只能把实参暂存在对象字段里再同步编码 | 引入**隐藏状态**：同一条 RPC 的重入会破坏实参；对象的网络身份与"上一次调用的参数"被绑在一起。当前无害只是因为 M05 发送侧尚未接线 | **接线前必须返工**：改为在生成调用桩的**调用点**构造闭包捕获实参（`SendRpc(this, rpcId, w => {...})`），`PMRpcEntry.Write` 随之取消。代价是每次调用一个闭包分配 —— 可接受，因为可靠 RPC 本就是低频（D-R0-05） |
| 2 | **校验同伴只记录档位、不生成调用**：`_Validate` / `_ForceValidate` 的**声明门禁**已完整（缺校验即编译失败），但接收侧**执行**校验需要三态运行时 API，而 hyld-master 侧只有 `PMRpcValidator` 枚举 | 「声明了校验」目前**不等于**「运行时真的校验了」 | 与 M05 接收侧接线一起做；R3 之前必须闭合（反外挂红线） |
| 3 | **字符串长度上限未实现**（契约 §8 建议 1024 字节） | D-R0-18 的未闭合点：恶意长串会照单全收（读侧仅受 MTU/分片预算约束） | 生成物加长度门 + 读侧上限校验，两处都要有 |
| 4 | **数组长度上限是本轮自定的 4096**，不是契约收敛结果 | 契约只写了"按 §9 MTU 反推" | 随 §9 MTU 一并收敛 |
| 5 | **继承链基址在跨程序集时静默按 0 处理**：`PropertyIndexBase` 只沿「本次扫描到的已声明类」上溯 | 基类在另一程序集/另一扫描批次且带复制属性时，派生类 `PropertyIndex` 与基类槽位重叠 —— **静默错位** | 加告警（当前这条路径无告警）；并把"网络基类与派生类放同一次生成"写进使用约定 |
| 6 | **`PMPropertyDescriptor` 没有值比较委托**：复制层只能"重新编码一次再与基线字节比对" | 每轮比较都要重新编码脏属性（正确性没问题，D-R0-13 的口径也满足，但有固定开销） | 优化项：生成器额外产出 `PMPropertyComparer`（返回 bool），不改复制层设计 |
| 7 | **`PMDirtyTracker` 位宽固定 64**：属性数 > 64 时槽位 ≥ 64 的脏位无法按位清除 | Push 收益削弱（正确性不受影响，因为比较才是判据） | 属 P0 冻结类型；类属性数接近 64 时再评估分段掩码 |
| 8 | **复制层无字节预算/校验和**：单载荷只按"属性数 ≤ 64"切分；UDP 位翻转只能靠魔数/版本/长度部分发现 | MTU 约束未冻结；中途位翻转可能读出一串合法值 | 随 §9 MTU 一起做；校验和可依赖 M03 层 |

另有两条需要**契约层补口径说明**（不是缺陷，是机制差异）：
- **条件"D-R0-15 跃迁补发"**在本设计下的独立可观测行为是「**值未变也补发一次当前值**」。B 块保留并断言了它，符合契约字面。若将来认为纯冗余可撤，需先改 R0 契约。
- **"条件不满足 ⇒ 抹掉脏位"**在本层改成「条件不满足时过滤该槽位，并把该连接视为**不阻塞**脏位清理」，由跃迁补发兜底。与 Iris 的 `ClearBits` / legacy 的 `InactiveChangelist` 殊途同归（都不丢值）但机制不同，值得在 R0 契约里补一句。

### 5.5 R2 返工：已闭合的 3 项必修缺陷（§5.4 的 #1/#2/#3）

三项都是**契约设计缺陷**（我定的接口造成的），不是子代理实现失误。

| # | 缺陷 | 后果 | 修法 | 验证 |
|---|---|---|---|---|
| 1 | **RPC 实参靠「对象上的实参帧」传递**：契约把发送委托定成 `PMNet_RpcWrite_<M>(PMNetObject, PMNetWriter)`，没有实参位 ⇒ 实现只能把实参暂存在对象字段 | **真 bug 而非只是设计瑕疵**：`RemoteSender` 未接线时调用进待发队列，**真正的编码发生在推迟之后**——届时实参帧可能已被后续同一条 RPC 的调用覆写 ⇒ **发出错误的参数**。生成物注释里"编码在同一次栈上同步完成"这句话在排队路径下不成立 | 改为在调用桩**调用点**构造闭包捕获实参；`PMRpcEntry.Write` 随之删除（发送侧实参不在对象上，没什么可从对象编码的） | 生成物复查：`PMNetRpcArg_` 残留 0 处；调用桩内 `delegate(...){ w.WriteInt32(p0); ... }` 捕获形参 |
| 2 | **校验同伴只记录档位、不生成调用**：`_Validate`/`_ForceValidate` 的**声明**门禁完整，但接收侧**执行**不生成 | 「声明了校验」**不等于**「运行时真的校验了」——描述符里写着 `ForceValidate`，反外挂链路上什么都没有 | 发射器按生效档位生成调用：`ForceValidate` ⇒ 三态路由（Reject 跳过实现不断连 / Report 上报后仍执行）；`Validate` ⇒ `false` 请求断连。上报经 `PMRpcValidationSink` 两个钩子，生成代码不依赖传输与上报系统 | 生成物复查：`PMNet_RpcInvoke_Fire` 内确实调用 `Fire_ForceValidate` 并处理三态 |
| 2b | **同一缺陷的更深一层**（返工时新发现）：只写 `Validator = ForceValidate` 而**不写同伴方法** | 与 #2 同类但更隐蔽 | **新增规则 13**：声明的档位必须有可调用的同伴（`ForceValidate ⇒ _ForceValidate`；`Validate ⇒ _Validate`）。规则 8 管"有没有声明"，规则 13 管"声明了是否真的调得动" | `RuleCount` 12 → 13，门禁从 60 → **61 项**，规则 13 由源码夹具命中 3 次。**规则 13 上线即抓到 7 处**真实命中，其中 Bad.cs 的「合法对照」用例本身就有此缺陷 |
| 3 | **字符串长度上限未实现**（契约 §8 建议 1024） | D-R0-18 未闭合：恶意长串照单全收 | 新增 `PMNetString.WriteBounded / ReadBounded`；上限**单点**放在运行时（`MaxBytes = 1024`），不重复到每个生成类。读侧先 `PeekVarintLength()` **在分配之前**用明确上限拒绝（否则伪造成巨长前缀可以先分配再失败） | 生成物复查：`w.WriteStringValue` → `PMNet.PMNetString.WriteBounded(w, ...)`、`r.ReadStringValue()` → `PMNet.PMNetString.ReadBounded(r)` |

**返工过程中额外发现并修正的一处**：`PMDeclCheck` 的「合法对照」夹具（`OkWithValidationRpc` / `OkForceValidateRpc`）
本意是展示规则 8 的**放行路径**，但它们只写了档位、没写同伴方法 —— 即夹具作者自己就以为
"声明档位 = 有校验"。这正是规则 13 要抓的形态，已连同夹具一起修正。

**仍然未闭合（见 §5.4 的 #4–#8）**：数组上限仍是自定值、继承链基址跨程序集静默按 0、
缺值比较委托、`PMDirtyTracker` 位宽 64、无字节预算/校验和。

### 5.6 R2 收口：端到端门禁（R2-C）与最后两项修正

**R2-C 端到端门禁** `Tools/PMNetE2E`：**92 项 0 失败 + 3/3 负向注入命中**（每条注入都带对照探针，
证明失败确实来自注入而不是整体崩掉）。它把四个环节串成一条可执行验收：

| 环节 | 断言要点 |
|---|---|
| 声明 → 生成 | 门禁**自己调用** `PMNetGen --decl-gen` 产出 `.g.cs`（csproj pre-build target），产物编进工程；并断言生成物与当前声明逐字节一致 |
| 注册 | 零反射 `RegisterAll` 后计数与生成物一致 |
| 复制收敛（T41） | 不同基线最终一致且与权威一致；旧 ACK 不清新脏位；值未变不发；条件跃迁补发；OnRep 真被调用 |
| RPC 双向（T40） | 调用桩入队；**实参不被覆盖**（§4.3.1 返工的核心不变性）；接收侧三态（Accept 执行 / Reject 跳过且不断连 / Report 上报且仍执行）；归属校验拒绝非 Owner |

**收口时又修了两项**：

| # | 问题 | 后果 | 修法 |
|---|---|---|---|
| 4 | **RPC 待发队列无界**（`PMNetGeneratedRegistry.EnqueueRemote`） | D-R0-18 要求「复制队列与内存必须有界」，但这条队列不在覆盖面内。`RemoteSender` 未接线时无限增长。**R2 之前该路径不可达**（M05 发送侧未接线、调用桩也没落地），把调用桩跑通后才变为可达 —— 这正是"端到端门禁把隐藏路径暴露出来"的价值 | 加 `MaxPendingRpcs = 1024`（超限**丢最旧**并计入 `DroppedPendingRpcs`，与投射物「容量满丢最旧」同口径）。`ClearPendingRpcs` **刻意不复位**该计数 —— 复位会让"发送路径没接线"这条告警被清理动作掩盖 |
| 5 | **生成物里"实参帧不做长期保存"的注释已过时** | 措辞仍在描述已被删除的实参帧设计 | 改为"实参由闭包持有，编码后即释放" |

**另有一处生成器的工程陷阱（已修，值得记住）**：`<Compile Include="Generated\**\*.cs" />` 的通配符
在**工程求值期**展开，而生成发生在构建期 ⇒ 干净 checkout 首次构建时生成物**不被编译**
（表现为一堆 CS0103/CS0117）。已用「求值期快照 + 条件补 Include」修掉，并实测「删掉 `Generated/` 后直接构建仍 0 错误」。

## 6. 测试验收集

> 下表T1–T36保留历史阶段编号与证据；其中未完成项按R阶段重新落实。新增框架验收见§6.1，不能用旧PASS替代。

| ID | 阶段 | 场景 | 执行者 | 方法/命令 | 预期结果 | 状态 |
|---|---|---|---|---|---|---|
| T1 | P0 | 生成器产物语法/API 合规 | AI | `dotnet build Tools/PMNetLangCheck -c Release`（netstandard2.0 + LangVersion 7.3，只含 PMNet 核心与生成产物，无任何外部依赖） | 编译通过，0 警告 0 错误 | **PASS** |
| T2 | P0 | 生成序列化器与 protobuf 逐字节等价 | AI | `dotnet Tools/PMNetVerify/bin/Release/net8.0/PMNetVerify.dll`；6 组场景（全默认 / 大厅 / 战斗 4 层嵌套 / 标量边界 / 默认值省略 / repeated×0,1,64）× 3 项检查 | 27 项检查 0 失败（退出码 0） | **PASS** |
| T3 | P0 | 生成代码不污染 XLua 生成范围 | AI | `grep "^namespace " ` 检查产物；生成器固定输出 `PMNet.Generated` | 唯一命名空间为 `PMNet.Generated`，不含 `Manger` / `HOTFIX` | **PASS** |
| T4 | P0 | `build.bat` 可重复且可校验 | AI | 实测四种情形：生成模式 / `--check-only` / 制造 protoc 产物漂移 / 制造 PMNet 产物漂移；并验证 proto 缺失、校验目标缺失的退出码 | 生成与 check-only 均退出 0；两类漂移均被拦下（退出 1）；缺文件退出 1；无 PATH 依赖（只用同目录 `protoc.exe` + `dotnet`） | **PASS** |
| T5 | P0 | 生成的产物与权威 proto 同步 | AI | 连续两次 `build.bat`；并用生成器 `--check` 比对；再用 `git status` 确认无行尾 churn | 第二次生成无 diff；`git status` 对三份 `SocketProto.cs` 干净 | **PASS** |
| T6 | P1 | 18 个大厅入口全部可回归 | AI+用户 | 静态核对分发表：(RequestCode, ActionCode) 集合 vs 期望清单；并编译服务端 | 20 条注册、无缺失/多出/重复（PASS）；行为级回归需运行环境，见 T6b | **PASS（静态部分）** |
| T6b | P1 | 18 个大厅入口逐条运行回归 | AI+用户 | 起服务端（无需数据库）+ `Tools/PMServerSmokeTest` 真实 TCP 客户端跑固定场景；UI 交互仍由用户补 | 行为与改造前一致 | **大部分 PASS**：`PMServerSmokeTest` **17/17** 覆盖自动建号、重复登录保护、密码校验、FindPlayerInfo、FindFriendsInfo、UpdateName 读回、Logon 重复、`request_id` 回带；用户在 Unity 实测中又真实走过 **`Login → ChangeHero → AddMatchingPlayer`（入队/房间人数 1/2/移出房间）→ 断线处理**（服务端日志已记录）。**仍缺**：房间邀请/聊天、ClearSence、PingPong 的逐条行为回归 |
| T7 | P1 | 反射断连雷已消除 | AI | 源码核对：`AllClearSenceReady` → 改名 `BroadcastAllClearSenceReadyToPlayers` 并私有化；`ChangeHero(FriendRoom)` → 改名 `BroadcastHeroChangedToRoom`；全仓确认无 `GetMethod`/`Invoke(pack)` 反射分发 | 两个同名冲突入口消失；路由不再依赖方法名 | **PASS** |
| T8 | P1 | 静默丢包已修 | AI | 源码核对：`ActionCode.RejectInvateFriend → RejectInviteFriend`、`ActionCode.CancalInvateFriend → CancelInviteFriend(Server,Client,MainPack)` 均已登记进分发表 | 两个 ActionCode 不再落到「未注册」分支 | **PASS** |
| T9 | P1 | 请求-响应关联 ID 生效 | AI | 源码核对：`MainPack.request_id`(16) 已入 proto 双端；服务端 `ControllerManger.HandleRequest` 单一收口回填；客户端 `BaseRequest.SendRequest` 分配 + `PmRpcClient` 待确认；清账位于 `RequestManger.HandleRequest` 所有早退分支之前 | 配对链路完整；运行级验证见 T6b | **PASS（静态部分）** |
| T10 | P2 | 双服务器可独立启动与联调 | AI+用户 | 起 Lobby + DS，走完匹配→进局→结算 | DS 地址由 Lobby 下发，快照进入、结算落库 | PENDING |
| T11 | P2 | driver mode 切换正确 | AI | 按 City→InGame→City 切换 | 指令发往正确通道，Mos 侧长连接不断 | PENDING |
| T12 | P3 | 大厅增量替代整表 | AI | 对比改造前后 `playerspack` 字节量 | 有增量、断线重连可恢复 | PENDING |
| T13 | P4 | 战斗带宽下降 | AI+用户 | 抓包对比每客户端 KB/s 与单包峰值 | 明显低于 34-50 KB/s 基线，且单包不撞 1024B | PENDING |
| T14 | P4 | 抗丢包正确性 | AI+用户 | 开 NetSim（10% 丢包 + 30-60ms）打完整局 | 未 ack 脏属性最终一致，无静默丢同步 | PENDING |
| T15 | P4 | 手感回归 | 用户 | 与改造前对比试玩 | 延迟补偿/MoveAck/Replay 行为不退化 | PENDING |
| T16 | P5 | 距离裁剪生效 | AI | 构造超出 `NetCullDistanceSquared` 的对象 | 该连接不复制；进入范围后收到 Create + 初始状态 | PENDING |
| T17 | P5 | Dormancy 生效 | AI | 静止玩家/对象 | 无变化期间不发包，变化后唤醒 | PENDING |
| T18 | P5 | FastArray 增量与移位 | AI | 中间删除/`RemoveAtSwap` 对比 | Swap 删除只重发 1 项；顺序删除重发整段 | PENDING |
| T19 | P5 | 客户端副本池清理完整 | AI | 反复进出范围复用同一 Class 实例 | 无跨世代残留（Tag/Delegate/Timer/引用/特效） | PENDING |
| T20 | P6 | 未授权 Server RPC 被拒 | AI | 用非 Owning Connection 发 Server RPC | 被服务端拒绝，不影响权威状态 | PENDING |
| T21 | P7 | `BattleReview` 大包不再超限 | AI | 打一场长局取回放 | 分块传输成功，无断链 | PENDING |
| T22 | P7 | 延迟补偿帧号语义修正 | AI | 对比 `[LagComp]` 日志中 clientFrame 与 serverFrame 差值 | 差值稳定在预期范围，历史查找不再静默 miss | PENDING |
| T23 | P2' | DS 构建可产出 | AI+用户 | 在 Unity 里执行 `Build / Build HyldDS (Windows Headless)`（或 `-executeMethod PMDsBuild.BuildWindowsHeadlessDs`）| 构建成功，产物存在且可复制到目标机 | **PASS**：`HyldDS.exe` + `UnityPlayer.dll` + `run_ds.bat`，总 328 MB，无头方式=编译期 `EnableHeadlessMode`（首次构建即成功，未触发回退） |
| T24 | P2' | DS 可无头启动并自我识别 + 可连通 | AI | 用 `run_ds.bat` 拉起，查日志与 UDP 收发 | 进程常驻、日志 `mode=DedicatedServer`、不初始化 UI/相机/输入 | **PASS**：日志 `Forcing GfxDevice: Null` + `NullGfxDevice`（真无头）、`process=DedicatedServer mode=DedicatedServer`；UDP 7801 三次往返全部收到 `PMDS-PONG`；心跳计数器 `recv=3 sent=3 recvErr=0 remotes=3` |
| T25 | P2' | 单 DS 启动耗时与常驻内存实测 | AI | 记录从进程发起到 UDP 就绪的耗时；记录稳定期工作集 | 得到两个数字并写入 §8 的 DS-2 决策依据 | **PASS**：启动 **740 ms**；WorkingSet **~61 MB**（稳定）、Private ~87 MB；tick 实测 30 Hz |
| T26 | P2' | 客户端构建不受影响 | AI+用户 | 同一工程以无 `-server` 启动客户端（Play 模式） | 走客户端分支（`PMNetMode=Client`），原有大厅流程不退化 | **部分 PASS**：编辑器 Play 模式下登录进大厅正常（已完成）；**独立的客户端 exe 构建尚未执行** |
| T27 | P2' | 服务端/客户端分支纪律 | AI | 全仓检索：是否出现用「编辑器/场景名」等间接条件代替 `PMNetMode`/`PMNetRole` 的分支 | 除既有历史代码外，新增分支一律走 `PMNetMode`/`PMNetRole` | **PASS**：无 `Application.isEditor` 式服务端分支；进程级判定仅 4 处且全部走 `PMNetRuntime` |
| T28 | P3' | **一套仿真两端行为一致** | AI+用户 | 同一输入序列（含各英雄移速、道具加减速）分别在 DS 与客户端跑，比较位置/子弹轨迹 | 不再出现「服务端 3.9 恒定 vs 客户端每英雄」这类模型分歧；MoveAck correction 频率显著下降 | PENDING |
| T29 | P3' | 配置单一化 | AI | 检索是否还存在第二份人工同步的英雄数值配置 | 数值参数只有一处定义，DS 与客户端读同一份 | PENDING |
| T30 | P3' | 战斗迁移后回归 | AI+用户 | 完整对局：移动/攻击/大招/命中/击杀/结算 | 表现与迁移前一致或更好 | PENDING |
| T31 | P2' | **启动参数 → 进程形态** 门禁 | AI | `dotnet Tools/PMNetLaunchCheck/bin/Release/net8.0/PMNetLaunchCheck.dll` | **94 项检查 0 失败**；覆盖身份判定、值消费边界（`-logFile -`）、非法输入不崩、`-lobby` 解析、重复参数、`Runtime` 推导 | **PASS** |
| T32 | P2' | **Unity 胶水可编译性** 门禁 | AI | `dotnet build Tools/PMUnityGlueCheck -c Release` | 0 警告 0 错误；已做负向验证（注入语法错误必须报错） | **PASS**（注意：仅证明可编译，不证明运行时行为） |
| T33 | P2' | 改动文件的结构完整性 | AI | `python Tools/check_cs_braces.py <files>` | 无法编译的文件（如 `HYLDManger.cs`）括号配平 | **PASS**（6 文件全 BALANCED） |
| T34 | 大厅 | **服务端用户库与 RPC 行为**（无需 Unity/数据库） | AI | 起服务端后跑 `dotnet Tools/PMServerSmokeTest/bin/Release/net8.0/PMServerSmokeTest.dll` | 10 个场景 17 项检查全过：自动建号 / 重复登录保护 / 密码校验 / FindPlayerInfo / FindFriendsInfo / UpdateName 读回 / Logon 重复 / request_id 回带 | **PASS** |
| T35 | 大厅 | **服务端可在无数据库环境启动** | AI | 本机无 MySQL（`mysqld` 进程数 0）下启动服务端，检查端口与日志 | `0.0.0.0:7778` 处于 LISTENING，日志打印 20 条分发表注册 | **PASS** |
| T36 | 客户端 | **Unity 中真实编译** | AI+用户 | Unity 打开工程并等待编译完成 | `Assembly-CSharp.dll` 与 `Assembly-CSharp-Editor.dll` 均生成；`PMNetBootstrap` / `PMDsHost` / `PMNetLaunchOptions` / `PMNetRuntime` / `PMDsBuild` 均在程序集内 | **PASS** |

---

### 6.1 纠偏后的框架验收（历史T1–T36不能替代）

下列测试本轮均未执行。历史PASS只证明当时被测范围。本轮文档检查包括：目标与非目标无冲突、每个模块有依赖与验收、旧阶段明确失效、C#示例符合版本约束、编码/表格结构正常。

| ID | 实施映射 | 场景 | 执行者 | 方法/命令 | 预期结果 | 状态 | 证据 |
|---|---|---|---|---|---|---|---|
| T37 | R0 | 来源与契约覆盖 | AI | 对M01–M13逐项列源类/函数、状态所有者、帧/ID/顺序域、旧链替换点 | 无Mover/投射物遗漏；无全局帧与输入帧混用；契约足以独立实现 | **PASS** | `Docs/plans/net-r0-contract.md`（50 决策 + 13 模块映射 + M01 API + 裁剪清单）；4 份证据附录；3 项高风险抽查见该文 §10 |
| T38 | R1 | 可靠/不可靠传输 | AI | `dotnet Tools/PMTransportTest/bin/Release/net8.0/PMTransportTest.dll`：可控链路注入丢包/乱序/重复/分片缺失/窗口饱和/会话错代 | 同通道保序去重；丢包后恢复且恰好交付一次；不可靠不重传；分片重组有界；内存有界 | **PASS**：**47 项 0 失败**（保序去重 3 / 丢包恢复 10 / 溢出断连 5 / 不可靠 4 / 域独立 4 / 分片 8 / 会话世代 3 / 协议健壮性 9） | `Tools/PMTransportTest/`（离线可跑，含可控丢包链路）；代码 `PMNet/Transport/` |
| T39 | R1/R2 | RPC身份/生命周期 | AI | `PMCallspaceCheck.dll`（callspace 真值表 + 归属校验）；`PMNetWorldTest.dll`（Owner 链 + 对象生命周期） | callspace符合源语义；非法上行无业务副作用；旧世代无效；归属推导不被伪造 | **部分 PASS（推进）**：callspace 16 分支 + 场景表 + 归属校验回归 + 身份/帧不变量 = **93 项 0 失败**；**Owner 链与对象生命周期 = 152 项 0 失败**（含「控制器无 Player 时不回退 Owner 链」「Pawn 有控制器时短路 Owner 链」「权威是 GetNetOwningPlayer 前置条件」等逐条对齐 UE 的用例）。**仍缺**：把 RPC 派发真正接到连接上后测「伪造 Owner 的 Client RPC 被拒 / 销毁后 RPC 的确定性行为」（需 R2 生成入口 + R3 宿主接线）；旧世代与跨通道乱序已由 T38 覆盖 | `Tools/PMCallspaceCheck/` + `Tools/PMNetWorldTest/`（均离线可跑）；`PMRpc.cs` 已修 D-R0-42 |
| T40 | R2 | 声明驱动代码生成 | AI | PMDeclCheck + PMNetE2E，build后run | 零反射、稳定ID、生成调用与非法声明门禁 | 离线PASS；真实宿主待R3 | 71/209，见RV5–RV7 |
| T41 | R2 | 属性收敛/对象生命周期 | AI | PMReplicationTest + PMNetWorldTest + PMNetE2E | 收敛、ACK/条件/初值及生命周期 | 内存投递PASS；真实宿主待R3 | 265/197/209，Reader与继承限制见RV尾注 |
| T42 | R3 | 匹配→DS→结算→退出 | AI+用户 | Lobby自动拉UnityDS并连接两个客户端；非法票据、重复结果、启动失败/崩溃 | 所有阶段有成功/失败状态；权威仅DS；结果幂等；会话/进程可回收 | PENDING | 旧探针不覆盖 |
| T43 | R4 | AP完整状态校正重模拟 | AI+用户 | 强制位置/速度/Mode/Modifier差异；重复/延迟权威帧、输入丢失、历史溢出 | 恢复同帧完整状态后收敛；不重复扣血/生成/音效；溢出明确重同步 | PENDING | 数学测试不替代 |
| T44 | R4 | SP/帧锚/碰撞环境 | AI+用户 | 不同Update频率、静止转向、断线恢复、障碍碰撞、运动参数变化 | 时间插值/受控外推正常；确认元数据更新；历史不混帧；预测/DS环境一致 | PENDING | — |
| T45 | R5 | 投射物全生命周期 | AI+用户 | 预测弹接管、Verify先于Spawn完成、Pending确认/拒绝、停止后迟到命中、ID世代复用、DS直创 | 无双弹/幽灵弹/重复伤害；拒绝能撤销；命中验证有界且DS最终裁决 | PENDING | UE参考已抽查，Unity尚缺 |
| T46 | R6 | 全业务与旧链退出 | AI+用户 | 两客户端完整局覆盖攻击/大招/道具/死亡/胜负；源码引用核对+抓包 | 生成RPC/复制/新预测在驱动；旧战斗包及旧预测退出；狂暴瓶权威违反解决 | PENDING | — |
| T47 | R6 | 弱网/长局/性能 | AI+用户 | 正常与弱网完整局、长时间运行与回放；记录带宽/队列/重模拟/内存/手感 | 正确性先通过；指标可复现；不以降低可靠性换带宽；实机证据齐全 | PENDING | — |

弱网故障注入属于M13必需能力；不搬旧NetSim脚手架不等于不测弱网。测试命令随各阶段工具落地补入本表；尚不存在的工具不伪造可执行命令或PASS。

## 7. 风险与回滚

| 风险 | 影响 | 应对 |
|---|---|---|
| 网络/预测替换破坏手感 | 高 | 按T43–T47做新旧对照；新链通过后删旧链，旧SavedMove不是永久约束 |
| 生成器产物与 protobuf 逐字节不等价 | 高（双端不通） | T2 作为 P0 的判定性实验，未过不进 P1 |
| 双端版本不同步 | 高 | 无兼容兜底是既定前提；发布必须双端同版本，`build.bat` 加产物校验 |
| 服务器拆分引入时序问题 | 中 | P2 独立成阶段，先保证「快照进/结算出」跑通再做 P3 |
| XLua 生成器被新类型污染 | 中 | T3 作为 P0 硬门禁 |
| Unity 2019.4 语法/API 天花板 | 中 | T1 硬门禁；生成模板只用 C# 7.3 + netstandard2.0 |
| 工作量评估偏乐观 | 高 | 按阶段交付，每阶段独立可验收；P0/P1 完成后重估 P4 工作量 |

**变更策略**：不代为提交。阶段验证期间可用旧链对照，切换验收通过后删除旧链；用户不要求长期兼容旧协议。所有未完成工作以当前R阶段为准。

---

## 8. 未决问题

> 历史问题保留追溯；阶段归属以R0–R6为准。凡下表与§3.9冲突，以本轮用户目标与模块契约为准。

| ID | 问题 | 影响 |
|---|---|---|
| Q1 | ~~`HyldDS` 的形态~~ **已定（D6）**：**Unity 工程的打包产物**（Windows 无头服），每局拉起一个进程、打完销毁；**不是**新的 C# 工程。`Server/` 里的战斗代码迁往 Unity 脚本侧 | 已影响 P2'/P3'/P4' 的编排 |
| Q2 | DS 是否需要 `battleguard` 等价的进程管家（拉起/销毁/路由表/结算容错），还是首版由 Lobby 直接拉起？ | 首版按 D6 由 Lobby 直接拉起；是否抽出独立 guardian 取决于是否要多战斗机部署 |
| DS-1 | ~~请在本机 Unity 的 Build Settings 确认 Windows 平台是否存在 "Dedicated Server" 选项~~ **已取证（2026-09）**：2019.4 确实**没有** Dedicated Server 构建目标与 `UNITY_SERVER`，**但 `BuildOptions.EnableHeadlessMode` 实测可用**（见 §2.8 的实测更正）。因此走「编译期无头 + 运行时 `-server` 身份」这条路，已落地 | 已解决 |
| DS-2 | ~~P2' 实测出的「单 DS 启动耗时 + 常驻内存」是否需要引入**预热进程池**~~ **已实测**：启动 740 ms、常驻 ~61 MB。**结论：首版不需要预热池**——按局拉起 + 销毁本身就是可接受的（比 C# 大厅进程还轻），预热池可以作为后续优化而非前置需求 | 已解决 |
| DS-3 | **已纠偏**：旧MainPack战斗路径仅作开发对照，最终替换为生成RPC/复制及预测协议 | R1/R2/R4/R6联合完成，不再要求先把旧编排整套搬完 |
| DS-4 | xlua 原生库在 DS 构建中的取舍：XLua 不在业务路径，是否从 DS 构建中排除（Windows 侧本就有 `Plugins/x86_64`） | 影响 DS 包体 |
| Q3 | ~~`PMNet` 层的目录与程序集划分~~ **已定（P0）**：核心与生成产物单一源放在 `Client/Assets/Scripts/PMNet/`（纯 C#，不依赖 UnityEngine），服务端在 P1 用 csproj 链接该目录；暂不引入 `.asmdef`（改造面最小，XLua 隔离已由命名空间保证） | 影响 P1 的服务端接入方式 |
| Q4 | ~~生成器宿主选型~~ **已定（P0）**：独立 .NET 控制台 `Tools/PMNetGen`（net8.0，本机 SDK 10.0.302 可跑）；未复用 Entitas/Jenny（那会引入 Unity Editor 依赖，且与「外部生成器 + 提交产物」模式不符） | — |
| Q5 | ~~服务端 `net6.0` 是否需要一并升级 TFM（已 EOL）~~ **已定（D11）**：升到 **`net8.0`**。直接原因是构建机上没有 .NET 6 运行时，服务端会以 `You must install or update .NET to run this application` 退出 | 已解决 |
| Q6 | 项目NetForceValidate的完整报告/惩罚体系是否全部移植尚待按需求细化；基础认证、Owner与参数/时间预算校验是必需项 | R1/R2/R4/R5即实施基础校验，不能全部推迟到收尾 |

---

## 9. 下一步

### 9.1 已完成（历史记录；当前状态见卷首）

- openspec 工件与 skill 已从本仓库移除，相关引用（`.gitignore` 白名单、`.github/agents/strict-engineer.agent.md` 的 OpenSpec Gate、`.github/copilot-instructions.md`、`Client/Assets/AGENTS.md`、`Server/AGENTS.md`）已清理。
- 现状基线调查完成（本仓库 4 份模块级调查报告，见 §9.3）。
- P0 与 P1 完成（详见 §9.4）；架构定型为 D6-D9，阶段重排见 §5。

> **Uncommitted 状态提醒**：移除 openspec 在本仓库留下**一批未提交删除**，接手前请知悉，不要误判为脏工作区或误回滚：
>
> | 分组 | 路径 | 说明 |
> |---|---|---|
> | 本次删除 | `Client/Assets/openspec/`（含 `.meta`）、`.github/skills/openspec/` | 本次会话删除 |
> | 本次修改 | `.gitignore`、`AGENTS.md`、`Server/AGENTS.md`、`Client/Assets/AGENTS.md`、`.github/agents/strict-engineer.agent.md`、`.github/copilot-instructions.md` | 本次会话清理引用 |
> | **本次新增** | `Docs/plans/net-architecture-migration.md`（本文） | 本次会话新增，未跟踪 |
> | **先前已删除**（非本次会话所致） | `.claude/commands/opsx/*`、`.claude/skills/openspec-*`、`Client/Assets/.claude/**`、`Server/.claude/**`、`Server/openspec/**`、`Server/OPENSPEC.md`、`.codegraph/.gitignore` | 开工前工作区已无这些目录，属遗留未提交删除 |
>
> 全部删除均可从 git 历史恢复。本文档不代为提交（提交边界由用户决定）。
>
> 遗留可选清理：`.gitignore:11-12` 仍有 `/.claude/settings.local.json` 与 `/**/.claude/settings.local.json`。这两行只对 Claude Code 壳体生效、与 openspec 无强绑定，故本次未动；若确认不再使用 Claude Code，可一并删除。

### 9.2 P2' 已完成（T23/T24/T25 PASS，T26 部分）

P0 / P1 / P2' 均已完成。P2' 的历史经过：**先在没有 Unity 的环境里写完全部代码并用门禁守住**，
待 Unity 可用后再补真实运行验证——两部分现都已完成。

#### 交付物（代码）

| 文件 | 作用 |
|---|---|
| `Client/Assets/Scripts/PMNet/PMNetLaunchOptions.cs` | 纯 C# 命令行解析：`-server` / `-batchmode` / `-nographics` / `-logFile` / `-dsid` / `-matchid` / `-listen` / `-port` / `-lobby` / `-tickrate`。支持 `-k v` 与 `-k=v`、大小写不敏感、非法值不抛异常只记警告；含 `#if UNITY_SERVER` 前向兼容分支。 |
| `Client/Assets/Scripts/PMNet/PMNetRuntime.cs` | 进程级网络模式（对应 UE 的 `GetNetMode()`）：`Mode` / `IsDedicatedServer` / `IsClient` / `ResolveMode`。**全仓唯一的进程身份判定来源。** |
| `Client/Assets/Scripts/Server/Boot/PMNetBootstrap.cs` | 启动探针，**分两段**：`SubsystemRegistration` 做身份判定与日志落点；`AfterSceneLoad` 创建 DS 宿主（原因见 §9.5 第 17 条）。 |
| `Client/Assets/Scripts/Server/Boot/PMDsHost.cs` | DS 进程宿主：绑定 UDP、异步收发、按 `TickRate` 驱动的逻辑帧骨架、周期心跳、就绪日志。**P2' 占位**（回包固定为 `PMDS-PONG`），P3' 由迁入的 `ClientUdp.cs` 取代。 |
| `Client/Assets/Editor/PMDsBuild.cs` | DS 构建入口：菜单 `Build / Build HyldDS (Windows Headless)` + `-executeMethod`；**先试 `BuildOptions.EnableHeadlessMode`、失败自动回退**；只打包一张自动创建的空引导场景（`Additive` 模式，不动用户当前场景）；产出 `HyldDS/HyldDS.exe` + `run_ds.bat`。 |
| `Client/Assets/Scripts/Server/Manger/HYLDManger.cs` | DS 守卫三处：`Awake` 跳过客户端链路、`Update` 不跑客户端管线、`Send` 对空 socket 记警告而非空引用（均以 `IsDedicatedServer` 为门，**客户端路径逐字不变**）。 |

#### 验收结果（实测）

| 项 | 结果 |
|---|---|
| **T23** DS 构建可产出 | **PASS**：`HyldDS.exe` + `UnityPlayer.dll` + `run_ds.bat`，总 328 MB。无头方式=**编译期 `EnableHeadlessMode`**（首次构建即成功，未触发回退） |
| **T24** DS 无头启动 + 自我识别 + 可连通 | **PASS**：日志 `Forcing GfxDevice: Null` + `NullGfxDevice`（**真无头**）、`process=DedicatedServer mode=DedicatedServer`；UDP 7801 三次往返全部收到 `PMDS-PONG`；心跳计数器 `recv=3 sent=3 recvErr=0 remotes=3` |
| **T25** 启动耗时 + 常驻内存 | **PASS**：启动 **740 ms**；WorkingSet **~61 MB**（稳定无泄漏）；Private ~87 MB；tick 实测 **30 Hz** |
| **T26** 客户端不受影响 | **部分 PASS**：编辑器 Play 模式登录进大厅正常；**独立客户端 exe 构建尚未执行** |
| T31/T32/T33 门禁 | **PASS**（见下表） |

**由 T25 得出的决策（回答 §8 的 DS-2）**：启动 740 ms + 常驻 61 MB，
**首版不需要预热进程池**——按局拉起 + 销毁本身就是可接受成本。预热池可作为后续优化，而非前置需求。

#### 门禁（可随时重跑）

| 门禁 | 覆盖 | 结果 |
|---|---|---|
| `Tools/PMNetLaunchCheck/` | 启动参数 → 进程形态的完整契约（身份判定、值消费边界、非法输入、`-lobby` 解析、重复参数、`Runtime` 推导） | **94 项检查，0 失败** |
| `Tools/PMUnityGlueCheck/` | 用 UnityEngine/UnityEditor **手写桩件**把胶水代码真编译一遍（含真实 `Loging.cs`），抓语法/拼写/类型错误 | **0 警告 0 错误**；已做负向验证（注入语法错误 → 报错） |
| `Tools/check_cs_braces.py` | 无法编译的文件（如 `HYLDManger.cs`）的括号配平检查 | 全 BALANCED |

`PMUnityGlueCheck` 的**边界必须说清楚**：它验证「语法正确、类型自洽」，
**不验证真实 Unity API 的签名与语义**（桩件签名是手写的）。门禁通过 ≠ 逻辑正确。
它已多次抓到「桩件缺 API」——那是它的正常维护动作，不是误报。

#### 设计取舍（三条，都是有意的）

1. **`-server` 是 opt-in，默认即客户端。** 解析失败、未初始化、任何异常路径都退回客户端行为。
   因此 DS 守卫在客户端上是空操作，客户端路径与改动前逐字一致。
2. **DS 与客户端代码完全相同**（不定义自定义宏）。同一份产物既能当 DS 也能当客户端，
   单机联调只需用不同参数拉起两次。代价是 DS 无法编译期裁掉**业务代码**——
   `EnableHeadlessMode` 裁的是**图形设备**而非业务脚本（见 §2.8）。若将来要裁业务代码，再引入自定义宏。
3. **无头部分放构建期、身份部分放运行时。** 两者是正交的：`EnableHeadlessMode` 决定图形设备，
   `-server` 决定业务身份。不要试图用一个机制同时解决两件事。

#### 遗留（不阻塞 P3'）

| 项 | 说明 |
|---|---|
| T26 的独立客户端 exe 构建 | 未做。Play 模式已验证客户端路径正常，exe 构建属同类验证 |
| DS 连通性回包是占位 | 固定 `PMDS-PONG`；P3' 由迁入的真实 UDP 层取代 |
| P2' 期间的两条坑 | 已归入 §9.5 第 11、12、17 条 |

#### 当时的下一步：P3'（历史记录，现由§5替代）

P3' 可以开工。**建议先做一个小前置**：把 `Server/Server/ClientUdp.cs`（846 行，零 UnityEngine 依赖）
迁进 Unity，替掉现在的 `PMDS-PONG` 占位——因为 P3' 的战斗仿真必须挂在一个真实的 UDP 收发层上，
先把落点铺好，P3' 才能一次做成。

### 9.3 本次调查的支撑材料（仓库外）

模块级调查报告（含逐函数行号证据）：

| 报告 | 内容 |
|---|---|
| `D:\hyld-refactor-survey\S1_server_rpc_surface.md` | 服务端 RPC/消息面清点（18 入口映射表、广播模式、会话状态、反射脆弱点、UE RPC 映射草案） |
| `D:\hyld-refactor-survey\S2_server_battle_replication.md` | 服务端战斗权威状态下发机制（`state_mask`/`SendUnsyncedFrames`/baseline/位置历史/子弹裁剪/NetSim/差距表） |
| `D:\hyld-refactor-survey\S3_client_net_pipeline.md` | 客户端网络层与预测/权威消费管线（请求-响应、UDP 线程模型、下行分发、BattleData 六 partial 职责） |
| `D:\hyld-refactor-survey\S4_aot_codegen.md` | 工程化与 AOT/代码生成现状（Unity 2019.4、Mono、protoc 3.11.1、XLua 约束、Entitas 闲置、可行路径与阻塞点） |

**注意**：§6 的 T 编号与 §2.7 的 B 编号是本计划内部引用坐标，修改时不要重排。

---

### 9.4 交付物清单（P0 + P1）

| 路径 | 作用 |
|---|---|
| `Tools/PMNetGen/` | 外部代码生成器（.NET 控制台，net8.0）。自建极简 proto3 解析器 + C# emitter，不依赖 protoc 与 Google.Protobuf。三种模式：`--out`（生成）、`--check`（同步校验，不一致退出 2）、`--normalize-eol`（行尾归一，供 build.bat 复用以免引入 Python/PowerShell 依赖）。 |
| `Tools/PMNetLangCheck/` | T1 门禁：以 netstandard2.0 + C# 7.3 编译 PMNet 核心与生成产物，零外部依赖，任何越过 Unity 2019.4 语言/API 天花板的写法都会在此失败。 |
| `Tools/PMNetVerify/` | T2 对照：把 PMNet 产物与 protoc 产物（`Server/Server/SocketProto.cs` + `Google.Protobuf.dll`）放在同一工程里，逐字节比对并双向交叉解析。 |
| `Client/Assets/Scripts/PMNet/` | 手写核心（单一事实源，纯 C#，不引用 UnityEngine）：`PMWireType` / `PMNetWriter` / `PMNetReader` / `PMNetFieldDesc` / `PMNetRole` / `PMNetProperty`（复制列表 + 脏位 + 属性描述符）/ `PMNetConnection` / `PMNetObject` / `PMRpc`（属性 + callspace + 归属校验）/ `PMFastArray`。 |
| `Client/Assets/Scripts/PMNet/Generated/SocketProto.PMNet.g.cs` | 生成产物（21 消息 + 9 枚举）：DTO + 静态序列化器 + 字段描述符表 + `PMProtocolInfo`（含 proto 内容哈希）。 |
| `ProtobufAndNotepad/Protobuf/build.bat` | 唯一生成入口：版本断言 → protoc 生成 → 行尾归一 → protoc 产物同步校验 → PMNet 生成 → PMNet 产物同步校验。支持 `--check-only`（只校验不写入，供提交前/CI）。 |

**P1 新增/改动**

| 路径 | 作用 |
|---|---|
| `Server/Controller/ControllerManger.cs` | 重写为显式 RPC 分发表：启动时注册 20 条 `(RequestCode, ActionCode) → 委托`，替代原来的 `GetMethod(ActionCode.ToString())` 反射。含异常隔离、`request_id` 单一收口回填、未注册组合的明确日志、启动时打印注册清单。 |
| `Server/Controller/Controllers.cs` | 改名两个与 ActionCode 同名的下行方法（消除断连雷）；`CancelInviteFriend` 补 `Client` 参数并改为独立包通知；修 `FindFriendsInfo` 的好友列表覆盖缺陷；`Login` 合并登记动作。 |
| `Server/Server/Server.cs` | 新增幂等的 `RegisterActiveClient`；启动时打印注册清单；移除已无用的 `using System.Reflection`。 |
| `Server/Server/Client.cs` / `Server/DAO/UserData.cs` / `Server/Server/FriendRoom.cs` / `Server/Server/BattleManage.cs` | 移除从未被写入的 `FriendsDic` 及其 6 个辅助方法、空转的 `UpdateMyselfInfo`、恒空且会清空客户端好友列表的 `GetActiveFriendInfoPack` / `UpdateActiveFriendInfo`（详见 §9.7）。 |
| `Server/Server.csproj` | 通过 `<Compile Include>` 链接 `Client/Assets/Scripts/PMNet/**/*.cs`，验证单一事实源方案（Q3）可用。 |
| `Client/Assets/Scripts/Server/Manger/PmRpcClient.cs` | 新增：请求待确认表 + 超时扫描 + 幂等白名单重试。 |
| `Client/Assets/Scripts/Server/Request/BaseRequest.cs` | `SendRequest` 分配 `request_id` 并登记；新增 `OnRequestTimeout` 可重写回调。 |
| `Client/Assets/Scripts/Server/Manger/RequestManger.cs` | 收到回包时先清待确认项（必须在 `ActionNone` 早退之前）；`RemoveAllRequest` 同时清空待确认表。 |
| `Client/Assets/Scripts/Server/Manger/HYLDManger.cs` | `Update` 中驱动 `PmRpcClient.Tick`。 |
| `ProtobufAndNotepad/Protobuf/SocketProto.proto` | `MainPack` 追加 `int32 request_id = 16;`。 |

**P2' 新增/改动**

| 路径 | 作用 |
|---|---|
| `Client/Assets/Scripts/PMNet/PMNetLaunchOptions.cs` | 纯 C# 启动参数解析（PMNet 核心，受 T1 语言面门禁约束）。 |
| `Client/Assets/Scripts/PMNet/PMNetRuntime.cs` | 进程级网络模式；**全仓唯一的进程身份判定来源**。 |
| `Client/Assets/Scripts/Server/Boot/PMNetBootstrap.cs` | 启动探针（SubsystemRegistration），场景加载前完成身份判定与分流。 |
| `Client/Assets/Scripts/Server/Boot/PMDsHost.cs` | DS 进程宿主（UDP 监听 + tick 骨架 + 心跳）。P2' 占位，P3' 被迁入的真实 UDP 层取代。 |
| `Client/Assets/Editor/PMDsBuild.cs` | DS 构建入口（菜单 + `-executeMethod`），只打包空引导场景。 |
| `Client/Assets/Scripts/Server/Manger/HYLDManger.cs` | DS 守卫（`Awake` / `Update` / `Send`），客户端路径逐字不变。 |
| `Tools/PMNetLaunchCheck/` | P2' 门禁：启动参数 → 进程形态契约（94 项）。 |
| `Tools/PMUnityGlueCheck/` | P2' 门禁：用 UnityEngine/UnityEditor 手写桩件编译校验胶水代码。 |
| `Tools/check_cs_braces.py` | 辅助：无法编译文件的括号配平检查。 |
| `Tools/PMServerSmokeTest/` | **大厅服务端冒烟测试**（不需 Unity/数据库）：以真实 TCP 客户端跑 10 个场景、17 项检查，覆盖自动建号、重复登录保护、密码校验、FindPlayerInfo、FindFriendsInfo、UpdateName 读回、Logon 重复、`request_id` 回带。 |
| `Tools/fix_gbk_sources.py` / `verify_gbk_conversion.py` / `scan_non_utf8_text.py` | GBK 无 BOM 源文件的扫描/修复/校验工具（见 §9.5 第 15 条）。 |
| `Server/DAO/UserStore.cs` | **新增**：进程内内存用户库（替代 MySQL），线程安全、只返回值副本。 |
| `Server/DAO/UserData.cs` | **重写**：从 MySQL SQL 改为读写 `UserStore`；删掉 `MySqlConnection` 参数；登录自动建号。 |

**常用命令**

```bat
REM 完整生成 + 全部校验
ProtobufAndNotepad\Protobuf\build.bat

REM 只校验（不改任何文件，适合提交前 / CI）
ProtobufAndNotepad\Protobuf\build.bat --check-only

REM T1 语言面门禁
dotnet build Tools\PMNetLangCheck -c Release

REM T2 逐字节等价
dotnet build Tools\PMNetVerify -c Release
dotnet Tools\PMNetVerify\bin\Release\net8.0\PMNetVerify.dll

REM 服务端编译
dotnet build Server\Server.csproj

REM 校验「服务端确实链接进了 PMNet 核心」——只看构建成功会被假阳性骗过（见 §9.5 第 7 条）
REM 注意：服务端 TFM 已由 net6.0 升到 net8.0（D11），产物路径跟着变
python -c "d=open(r'Server/bin/Debug/net8.0/Server.dll','rb').read(); print('PMNetWriter', b'PMNetWriter' in d)"

REM 校验分发表注册清单（20 条，无缺失/多出/重复）
python -c "import re;s=open('Server/Controller/ControllerManger.cs',encoding='utf-8-sig').read();r=re.findall(r'Register(?:Void)?\(\s*RequestCode\.(\w+)\s*,\s*ActionCode\.(\w+)',s);print(len(r), len(set(r)))"

REM ---- P2' 门禁 ----

REM 启动参数 → 进程形态（94 项）
dotnet build Tools/PMNetLaunchCheck -c Release
dotnet Tools/PMNetLaunchCheck/bin/Release/net8.0/PMNetLaunchCheck.dll

REM Unity 胶水可编译性（无 Unity 环境下的唯一自动化安全网）
dotnet build Tools/PMUnityGlueCheck -c Release

REM 无法编译文件的结构检查
python Tools/check_cs_braces.py Client/Assets/Scripts/Server/Manger/HYLDManger.cs

REM ---- 服务端 + 大厅冒烟测试（无需数据库/Unity）----

REM 编译服务端（net8.0）
dotnet build Server/Server.csproj

REM 启动服务端（交互式控制台）
dotnet Server/bin/Debug/net8.0/Server.dll

REM 跑大厅冒烟测试（需服务端已在监听 7778）
dotnet build Tools/PMServerSmokeTest -c Release
dotnet Tools/PMServerSmokeTest/bin/Release/net8.0/PMServerSmokeTest.dll
```

### 9.5 已踩实的坑（后续阶段不要再踩）

| # | 事实 | 证据 | 应对 |
|---|---|---|---|
| 1 | **protoc 会把「proto 的传入路径」写进生成文件头部的 `source:` 字段**。传绝对路径会写成完整路径，传裸文件名才写成 `SocketProto.proto`。 | 实测：`build.bat` 早期版本传绝对路径时，生成产物与已提交产物在该行不同 | `build.bat` 必须 `pushd` 到脚本目录并用裸文件名调用。已写进脚本头注释 A 条。 |
| 2 | **protoc 输出 LF，而本仓库追踪的文件是 CRLF**（HEAD 中生成产物与手写源码均为 CRLF）。内容一致，但会让 git 把产物报成「已修改」。 | 实测 216548 B(LF) → 222600 B(CRLF)，差值恰为 6052 = 行数；归一后两文件完全相同 | 生成后统一归一到 CRLF（`PMNetGen --normalize-eol`）。 |
| 3 | **`fc` 的文本模式并不可靠地容忍 CRLF/LF 差异**（小样本测试会给出误导性结论）。 | 实测：同内容、仅行尾不同的 6052 行文件，文本模式 `fc` 报出差异 | 比对前先把两侧都归一到 CRLF，再用 `fc /b` 精确比对。 |
| 4 | **protobuf 会丢失 float 的负零符号**：`-0.0f == 0f` 为真 → 两侧都按默认值省略 → 解析回来是 `+0.0f`。 | T2 场景 3/4 初版用 `Google.Protobuf` 的 `Equals` 比对时报不等，而字节比对通过 | 字节级断言才是正确口径；不要用消息对象 `Equals` 判等。已在 `PMNetVerify` 写明并留回归用例。 |
| 5 | **`langversion` 与 API 面是两个独立约束**。只查 `LangVersion 7.3` 不足以证明 Unity 可编译，还要卡 netstandard2.0 的 API 面。 | `PMNetLangCheck` 同时锁定两者 | T1 用 netstandard2.0 + C# 7.3 双约束。 |
| 6 | **`Google.Protobuf.dll` 3.11.1 在 net8.0 下可直接裸引用运行**，无需 NuGet。 | `PMNetVerify` 以 `<Reference><HintPath>` 引用后正常跑通对照 | T2 对照工程可继续保持零 NuGet 依赖。 |
| 7 | **`<Compile Include>` 的相对路径层级写错时会静默贡献 0 个文件，而 `dotnet build` 仍报「已成功生成」。** | P1 首次给服务端链接 PMNet 核心时写成 `..\..\Client\...`（csproj 在 `Server/`，`..\..\` 已跑到仓库外），构建成功但 `Server.dll` 里查不到任何 `PMNet*` 类型 | 校验链接是否生效**必须查程序集内容**，不能只看构建结果。判定命令见 §9.4。 |
| 8 | **给「请求-响应」加自动重试前，必须先逐条确认该请求是否幂等。** | `Logon` 重复注册会返回失败；`Login` 开头即是「已登录则 Fail」，登录成功但回包丢失时重发会让客户端误判为登录失败 | 重试采用**白名单**（仅 `FindPlayerInfo` / `FindFriendsInfo` / `UpdateName`），其余只上报超时。想扩大范围需先把服务端幂等化。 |
| 9 | **在 Python / JSON 字符串里写 Windows 路径时，`\b` 与 `\n` 会被当成转义符，导致写入的文档静默损坏**（无任何报错，只是内容少了字符或多了换行）。 | P2' 回写计划文档时真实踩到：3 处 `Protobuf` + 反斜杠 + `build.bat` 被写成 `Protobuf` + **退格符(0x08)** + `uild.bat`；`Tools` + 反斜杠 + `PMNetVerify` + 反斜杠 + `bin` 处 `\b` 被吃掉、`\net8.0` 被折成换行。 | 写文档/脚本命令时**统一用正斜杠**（Windows 与 dotnet 均可接受）；若必须用反斜杠，写完后扫描异常控制字符：`[c for c in text if ord(c) < 32 and c not in '\n\r\t']` 应为空。 |
| 10 | **桩件里的 `MonoBehaviour` 不得声明 `Awake`/`Update` 等方法。** 真实 Unity 里这些是按名字反射调用的魔术方法，不是基类虚方法。 | 桩件初版写了 `protected virtual void Update()`，导致 `PMDsHost` 的 `private void Update()` 报 CS0114 假警告 | 桩件的 `MonoBehaviour` 保持空。已写入 `Tools/PMUnityGlueCheck/UnityStubs.cs` 头部注释。 |
| 11 | **客户端每帧管线里存在隐式前置条件，新增跳过逻辑时必须以显式守卫代替「那个标志恰好是 false」。** | `HYLDManger.Update` 中 `_pingpongManger.Excute()` 位于 `if (HYLDManger.是否为连接状态)` 内，而该标志默认 false 且只由 `TCPSocketManger` 连接成功时置 true——DS 上恰好安全，但一旦有人在 Update 里新增一条不依赖该标志的 `_socketManger` 调用就会空引用 | DS 侧在 `Awake`/`Update`/`Send` 三处显式加 `PMNetRuntime.IsDedicatedServer` 守卫，把「客户端管线不参与 DS」变成约定而非巧合。 |
| 12 | **桩件门禁只能发现语法/类型问题，发现不了空引用、时序、生命周期问题。** | `PMUnityGlueCheck` 能抓住括号/拼写/类型错用，但抓不住「DS 上某字段为 null」这类问题 | 不要把门禁通过当成逻辑正确；DS 路径的 null 安全必须靠读代码 + 显式守卫（见第 11 条）。 |
| 13 | **`Console.Read()` 在 stdin 被重定向时会**立刻返回 -1**，导致进程「启动成功但秒退」。** | 本项目实测：服务端日志打印了 `启动监听String: 0.0.0.0:7778成功` 与 20 条注册清单，但紧接着端口消失、进程不在；`start /B ... > file` 与 CI 场景都会触发。交互式控制台里完全正常，所以长期未被发现 | `Program.cs` 根据 `Console.IsInputRedirected` 分流：交互式用 `Console.Read()`，非交互用 `Thread.Sleep(Timeout.Infinite)`。迁移计划里 Lobby/DS 需要能被非交互拉起，这个坑必然会再碰到。 |
| 14 | **测登录场景时，必须先建模服务端已有的两条规则，否则用例会「通过但理由错误」。** | 规则一：一条 TCP 连接只能登录一次（`UserController.Login` 开头 `client.UserName != null → Fail`）；规则二：同一账号同时只能有一条活跃连接（`GetActiveClientByUserName != null → Fail`）。本工具初版用小写场景时，错误密码用例之所以返回 Fail，其实是因为同账号另一条连接还在线（撞了规则二），根本没测到密码校验 | 每个登录场景各开新连接（规则一），并在重登同账号前断开旧连接 + 等待服务端感知断开（规则二，见 `CloseAndSettle`）。这一坑已写入 `PMServerSmokeTest` 文件头注释。 |
| 15 | **Windows 开启「Beta: 使用 Unicode UTF-8 提供全球语言支持」（ACP=65001）后，GBK 无 BOM 的源文件会静默损坏。** | 本机 `ACP=65001`、`Encoding.Default.WebName=utf-8`。Roslyn 对无 BOM 文件按 UTF-8 宽松解码，GBK 字节变 U+FFFD：落在**标识符**→`error CS1056` 编译失败（1 个文件）；落在**字符串字面量**→能编过但运行期字符串变 U+FFFD（3 个文件，如英雄名 `"新手士兵"`）；落在**注释/`#region`**→无害（13 个文件）。在普通中文 Windows（ACP=936）上 Roslyn 会回退到 ANSI 解码 GBK，这些文件本来都能编过 | 已将 17 个 GBK 无 BOM 的 `.cs` 转为 UTF-8 + BOM；工具：`Tools/fix_gbk_sources.py`（扫描/分类/修复）、`Tools/verify_gbk_conversion.py`（零损失校验）、`Tools/scan_non_utf8_text.py`（全仓影响面，12471 个文本文件中仅剩 8 个可疑且均为故意的测试数据）。**新增含中文的源文件请一律存为 UTF-8 + BOM。** |
| 16 | **把日志路径硬编码成某台旧机器的绝对路径，而且失败不报错。** | `Program.cs` 原为 `D:/unity/hyld-master/hyld-master/Server/log/...`，而工程实际在 `D:/UGit/hyld-master`。由于 `FlushTraceInternal` 会自动 `CreateDirectory`，日志被静默写到一个与代码无关的目录树里，排查时根本想不到 | 启动时从可执行文件目录向上找「同时含 `Server` 与 `Client` 的那一层」拼出 `Server/log`，并在启动日志里打印**绝对路径**。凡是写文件的路径都要可发现、可验证。 |
| 17 | **在 `RuntimeInitializeOnLoadMethod(SubsystemRegistration)` 里创建对象并调 `DontDestroyOnLoad` 是无效的：那个阶段连初始场景都还没加载。** | DS 实测日志完整打出 `===== HyldDS 就绪 =====` + `监听=0.0.0.0:7801`，紧接着就是 `[PMDsHost] 已关闭`，外部表现为「日志说就绪、但 UDP 端口根本连不上」。对象留在随后被卸载的启动场景里，跟着一起没了 | 拆成两段：**身份判定**放 `SubsystemRegistration`（必须早于场景加载，否则场景组件的 `Awake` 看不到 DS 标志）；**宿主创建**放 `AfterSceneLoad`（此时 `DontDestroyOnLoad` 才有效）。另外把 `OnDestroy` / `OnApplicationQuit` 两种关闭触发源分开打日志——它们的修法完全不同，不区分就会来回猜。 |

| 18 | **用 `cmd /c start /B` 从工具 shell 里拉起长驻进程，会在父 shell 结束时让进程「干净地」退出。** | P3'-1 联调期服务端反复「跑几分钟后端口静默消失」，且**没有任何异常、事件日志也没有崩溃记录**。根因：`start /B` 只重定向了 stdout，stdin 仍随父 shell 存在/消失，父 shell 一结束 stdin 即 EOF，`Console.Read()` 返回 -1 → `Main` 正常返回。表现与「崩溃」完全不同（无异常），极易误判为产品缺陷 | 长驻进程一律用**真正分离**的方式拉起：`powershell Start-Process -FilePath <bat>`（本仓库已提供 `Server/run_lobby.bat`）。另在 `Program.cs` 增加 pid 与退出路径日志（`ProcessExit` / `Main 返回`），使「自己退出 / 收到退出信号 / 被强杀」三种情形事后可区分。 |
| 19 | **用 `File.Open(path, FileMode.OpenOrCreate, FileAccess.Write)` 打开日志文件会把它独占锁定，运行中完全读不到。** | 表现为「文件明明有 2520 字节，`cat`/`cp` 却得到 0 字节」，或 git-bash 报 `Device or resource busy` / `Permission denied`。服务在跑的时候无法查看日志，联调时是致命阻塞 | `Server/Tool/Loging.cs` 改为 `new FileStream(..., FileShare.ReadWrite)`。**凡是「服务运行中被读取」的文件都要显式给 FileShare**，默认值是 `None`。 |
| 20 | **给服务端起新构建之前必须先停掉正在运行的实例，否则构建会以「文件被占用」失败。** | `dotnet build Server/Server.csproj` 报 `MSB3027/MSB3021：无法将 obj\...\Server.dll 复制到 bin\...\Server.dll，文件被 .NET Host (<pid>) 锁定`。此错误信息指向 targets 文件，容易被误读成代码/工程配置错误 | 顺序固定为「先停进程 → 再构建 → 再启动」。 |

| 21 | **用脚本按「行」删类时，判「类的结束」不能只看「这一行 strip 后是 `}`」——构造函数/方法的结束行也满足这个条件。** | P3'-2 从 `HYLDStaticValue.cs` 删除 `SuperBulletParams` 与 `Hero` 两个类时，扫描在**构造函数的闭合 `}`** 就停了，于是留下两个孤儿 `}` + 一段孤儿文档注释。CLI 侧全部门禁通过（因为都是注释与多余括号，编译器之外看不出来），直到用户在 Unity 里编译才报出 **CS1022**（`HYLDStaticValue.cs(380,1)` 与 `(382,1)`）。 | 两个动作：<br>1) 需要删类时改用**带缩进/深度感知**的解析，或直接按「从类声明到深度归零」而不是「第一个 `}`」；<br>2) **`Tools/check_cs_braces.py` 已改为顺序感知**（追踪运行深度、报告首次变负的行号、要求末尾归零）。此前它只比数量，而 CS1022 的典型成因正是「多一个 `}` 且少一个 `{`」——**数量可能刚好相等，纯计数会误判 BALANCED**。已用合成用例做负向验证。 |
| 22 | **凡是用脚本做结构性改动（删类/删方法/大段搬移），改完必须立刻跑 `check_cs_braces.py`。** | 同上：本次流程缺口不是「没有工具」，而是「有工具却没跑」。合成用例已证明该工具在顺序感知后能抓到这类错误。 | 把它并入常规流程：任何脚本化结构改动 → 立刻 `python Tools/check_cs_braces.py <改动文件...>` → 再看是否要请用户编译。 |

| 23 | **用脚本生成「多参数调用」时，最易犯的错是漏掉构造函数包装或参数个数不对——它不影响括号平衡，所以结构检查抓不到。** | P3'-2 生成英雄表时，把 6 个构造参数直接拼进了 `Heros.Add(...)`（漏了 `new Hero(...)`），20 行全部报 **CS1501: No overload for method 'Add' takes 7 arguments**。随后我用正则连续修了两次，**两次都修出新错**（先双重包裹 `new Hero(new Hero(...))`，再过度剥离成裸参数）。 | 加门禁 `Tools/check_hero_table_shape.py`：逐行验证「`Heros.Add(<HeroName>, new Hero(...))`」形状、参数个数与构造函数签名相容、英雄集合与 PMHeroId 一致、行序与编号一致，**并把表现引用与改造前的冻结基准逐字段比对**（换错预制体会被拦住）。<br>**教训**：同一个目标上反复用正则打补丁，是在累积风险；应停下来**确定性重写**那一段，再用门禁验证。 |
| 24 | **改一个「格式固定、行数固定」的生成块时，用「按行删/正则替换」属于高风险操作；一旦发现修一次又错一次，立刻改为确定性地重写整块。** | 同上：英雄表前后被改了 4 轮（生成 → 修 CS1501 → 重排 → 修双重包裹 → 剥离过度），每一轮都引入新错误，直到改为「整块确定性重写 + 门禁验证」才收敛。 | 流程：生成块 → 一次写对（数据显式列出）→ 门禁验证。若门禁失败，修**数据**或**模板**，而不是对产物打补丁。 |

| 25 | **客户端代码没有自测编译手段时，会把用户变成自己的编译器——而且要绕三四个来回。** | P3'-2 改客户端时，我只有「CLI 结构检查 + 手写 grep」可用，于是同一段英雄表连续暴露三个错误（CS1022 → CS1501 → CS1503），每次都要用户在 Unity 里点一次编译。我当时的判断是「HYLDStaticValue.cs 依赖太多 Unity 类型，做桩件不划算」——**这个判断是错的、而且我从未实测**。实测后：该文件只用到 5 个 Unity 类型（Animator/Debug/GameObject/MonoBehaviour/Vector3）+ 5 个项目类型；`Tools/PMClientCheck` 一次建成，之后三个历史错误全部能被它抓住。 | **不要靠猜判断「桩件成本高」**——先量依赖面（数一下真正用到的类型），再决定。已经建成 `Tools/PMClientCheck`：用手写桩件把 `HYLDStaticValue.cs` + `HeroData.cs` + 共享表编译一遍。回归验证：把三个历史错误分别注入，三个都被它报出（CS1022 / CS1501 / CS1503）。 |
| 26 | **一个目标上「修一次又错一次」时，必须停下来重写那一段，而不是继续打补丁。** | P3'-2 的英雄表前后被改了 4 轮（生成 → 修 CS1501 → 重排 → 修双重包裹 → 剥离过度），中间两轮都是我自己引入的新错误；`check_hero_table_shape.py` 也被我用补丁修坏了两次。直到改为「整份确定性重写 + 门禁验证」才收敛。 | 判据：**同一个文件/同一段逻辑，连续两次修复都产生新错误 → 立即停止打补丁，整块重写**。重写时把数据显式列出来（不要用正则从产物里再抽一遍），写完立刻跑门禁。 |
  
  | 27 | **位图 / 掩码类的编解码必须把两端放在一起核对。** M03 的确认位图解码写成 `ackId - i` 并跳过了 bit0，正确语义是 bit i 表示包 `ackId - 1 - i`。 | `Tools/PMTransportTest` 的 B2 用例：接收端报「包 1 收到」时，发送端把**包 2** 上的可靠消息退休了。追踪 `TX seq= / RX seq=` 才定位到 A 在收到 NAK 后重传的是 `seq=1` 而不是 `seq=2`。 | 这类缺陷**不报错、不掉线**，只表现为「偶发丢事件」，只能靠字节级断言钉住。已补：任何位图字段的编解码都必须成对写测例，并覆盖「只有中间位为 0」「最高位为 0」等边界。 |
  | 28 | **门禁全绿不等于验证完成——必须做负向验证，并且要记录注入后的失败数。** M04 的 7 个注入中 6 个被抓、第 7 个（「筛选后不重建剩余集合」）**0 项失败**。 | 该缺陷所在的代码路径（相关性不满足）在测试里从未被执行：所有测试对象都是「永远相关」。补 D8/D9 后同一注入产生 **4 项失败**。 | 判据：**注入后如果失败数是 0，那不是「门禁不灵敏」，而是「这条路径没被测到」**。正向用例容易在同一对象形态上重复，要主动构造能走到分支另一侧的输入。 |
  | 29 | **`ENetRole` 的数值顺序与项目 `PMNetRole` 不同，数值比较会静默错判。** UE 是 `None=0, SimulatedProxy=1, AutonomousProxy=2, Authority=3`，项目是 `Authority=1`。 | 搬 UE 的 `LocalRole < ROLE_Authority`（等价于「不是权威」）时，若直接写 `<` 只有 `None` 被判为「小于权威」；`AutonomousProxy`/`SimulatedProxy` 会被误当成权威。 | 一律用显式谓词 `PMNetRoles.IsAuthority / IsLessThanAuthority / IsNone`，禁止对角色枚举做数值比较。已写入 `PMNetRole.cs` 的类注释。 |
  
  | 30 | **委派项的 `writePaths` 用相对路径时，会按「当前工作目录」解析，而不是按任务正文里的目标仓库。** 本次 pi 的 cwd 是 `D:/UE_Project/ProjectMecury`，而我给的边界是 `Tools/PMNetGen/...` 这类相对路径 ⇒ 硬边界实际指向了**错误的仓库**。子代理自己发现「`ProjectMecury/Tools` 下不存在 `PMNetGen`」并按任务正文改到 `hyld-master`，才没有把文件写错地方（事后确认 ProjectMecury 未被污染）。 | 子代理报告 R2-A §0.1，并核实了两侧目录内容 | ① **工作目录 ≠ 目标仓库时，`writePaths` 必须写绝对路径**；② 委派前后各做一次「目标仓库是否有计划外新增/污染」的核对；③ 这条不是子代理的问题——子代理无法修正工具层的路径解析，只能靠"发现异常并报告"兜住 |
  | 31 | **编译失败时仍可能跑到「旧二进制」，并打印一次全绿的假结果。** 改 `PMStableHash` 签名后 `PMDeclCheck` 有 4 个 CS1501 编译错误，但 `dotnet Tools/PMDeclCheck/bin/Release/net8.0/PMDeclCheck.dll` 照样输出了「PASS 59 / FAIL 0」——那是上次构建的产物。 | 本次实测：编译错误 4 个的同时门禁报 59/59 全绿 | **门禁结论只在「本次构建 0 错误」的前提下才可信**。流程上：`dotnet build` 的错误数必须先看，再看运行输出；脚本化的验收命令要把 `build` 与 `run` 串在同一个 `&&` 链里 |
  
  | 32 | **"声明"与"生效"是两件事，只做前者会留下「声称有、实际没有」的路径。** R2 的 RPC 校验：`[PMServerRpc(Validator = ForceValidate)]` 只声明档位，真正被调用的是同伴方法 `_ForceValidate`；早期生成物**只把档位写进描述符、不生成调用** ⇒ 描述符里写着强制校验，反外挂链路上什么都没有。 | 子代理报告 R2-A §6.3 主动登记了这个能力边界；返工时复查生成物确认「接收侧无校验调用」。补规则 13 后**上线即抓到 7 处**真实命中，其中连门禁自己的"合法对照"夹具都有此缺陷。 | 判据：凡是"声明了 X"就要问一句**"X 在运行期由谁兑现、我能指出来吗"**。指不出来就加一条声明期规则把它变成编译错误，而不是留成运行期的静默降级 |
  | 33 | **推迟执行 + 可变暂存 = 静默发错数据。** R2 的 RPC 实参：早期把实参暂存在对象字段（"实参帧"），再由静态委托在**真正发送时**读出编码。发送在 `RemoteSender` 未接线时会进队列 ⇒ 编码发生在推迟之后，届时实参帧可能已被后续同一次 RPC 的调用覆写。生成物注释当时还写着"编码是在同一次栈上同步完成的"——那句话在排队路径下不成立。 | 返工时读生成物发现 `EnqueueRemote` 把委托存进队列（`pending.Write = write`），而编码在排空时发生。 | 判据：**只要"取值"与"使用"之间可能插入其它调用，就必须让值随调用走**（闭包/值拷贝），不能放在共享的可变位置。写完这类代码后要专门问："这条路径上，值可能被谁改？" |
  
  | 34 | **委派项的必读清单里放大文件（并要求通读）会让子代理的输出量超限被杀，且往往在"阅读阶段"就死掉 —— 表现是返回 `done` 但什么都没写。** 本次 R2-C 第一次委派：清单里放了 `Tools/PMReplicationTest/Program.cs`（1172 行）与 `Tools/PMNetWorldTest/Program.cs`（1352 行）要求通读，结果运行因 `reason: large-output` 终止，`Tools/PMNetE2E/` 与报告都不存在 —— 而返回的摘要只是一句被截断的"Let me examine the test harnesses…"。 | 第一次委派返回 `done`，但 `ls Tools/PMNetE2E/` 不存在；查 `/tmp/cli-delegation-pi-rpc-*.json` 得 `reason = large-output`，且 stdout 里能看到当时的 `PMReplicationTest` 源码内容（说明还在读文件阶段）。 | ① **必读清单里的大文件改成"用 grep 取公开 API 面 / 只读指定行区间"，并显式写"不要通读、不要回显"**；② `task` 里加输出纪律：先落最小可骨架再迭代，报告分批写；③ **收到 `done` 后必须核对产物是否真的落盘**（本次靠 `ls` 才发现是空转），不能只信摘要 |
  | 35 | **构建期生成 + `<Compile Include="glob">` = 首次构建漏编译。** 通配符在 MSBuild **求值期**展开，而生成物在**构建期**才产生 ⇒ 干净 checkout 第一次构建时生成物不在编译列表里（一堆 CS0103/CS0117），第二次才好。 | R2-C 子代理报告 §2.1 与 §5：已用「求值期快照 + 条件补 Include」修掉，并实测「删掉 `Generated/` 后直接构建仍 0 错误」。 | 凡"生成物要被编译进来"的工程，都必须显式处理这条求值顺序；验证方式是**先删掉生成目录再构建**，而不是在已有产物的目录里构建 |

### 9.6 设计决策（后续阶段需遵守）

1. **核心层单一事实源**：手写核心只放 `Client/Assets/Scripts/PMNet/`，服务端在 P1 通过 csproj `<Compile Include="..\..\Client\Assets\Scripts\PMNet\**\*.cs" />` 链接，不做双份复制。理由：生成代码是确定性的、复制无风险；而框架核心若复制必然长期分叉。代价是核心不得引用 `UnityEngine`（这同时也是 P0/T1 的硬约束）。
2. **现有proto DTO产物命名空间为 `PMNet.Generated`**，消息加PM前缀避免冲突。新增业务partial生成代码必须与业务类同namespace；描述符/注册表可用独立Generated命名空间，仍避开Manger/HOTFIX。旧T3仅约束proto产物，不能套到所有业务partial。
3. **不引入 `.asmdef`**：当前全部落在 `Assembly-CSharp`，P0 保持最小改造面；XLua 隔离由命名空间保证（`Client/Assets/Editor/HotFixStaicList.cs` 只反射 `Manger` / `HOTFIX`）。
4. **保留 protobuf 线格式（D3）与量化不冲突**：量化在属性层做（float → 定点 int），再交给 protobuf 的 varint 搬运，因此 P4 的位置量化不需要改线格式。
5. **`.gitignore` 新增 `/Tools/**/bin/`、`/Tools/**/obj/`**，避免生成器与校验工具的构建产物被跟踪。

### 9.7 P1 的两处范围决策与依据

**① B5 不是「死码」而是**两个**功能性缺陷，已一并修掉。**

调查阶段把 B5 记为「`Client.FriendsDic` 死表 + `UpdateMyselfInfo` 空转」，实测后定性需要修正：

- `FindFriendsInfo` 的数据库查询（`Server/DAO/UserData.cs`）本身是**正确**的：它从 `friends` 表拉好友，并对每个好友用 `server.GetPlayerState(uid)` 写入真实状态。
- 但 `UserController.FindFriendsInfo` 紧接着执行 `pack = client.GetActiveFriendInfoPack();`，**把刚查出来的正确结果整包丢弃**，换成一个由 `FriendsDic` 驱动的包。而 `FriendsDic` 在服务端**零写入点**，因此这个包恒为空 —— 表现就是「好友面板永远为空」。
- 更严重的是 `JoinFriendRoom` 里的 `client.UpdateActiveFriendInfo()`：它推送的同样是空包，而客户端 `UIInvatingFriendPanel.UpDateActiveFriendInfo` 遇到空 `Playerspack` 会执行 `FriendItemsDicClear()` —— 相当于**玩家一进房间，好友列表就被清空**。

修法是保持客户端契约不变（客户端读的是 `Playerspack`，不是 `Friendspack`），改为由内存用户库的结果派生在线好友；并把那套恒空且有害的推送机制整体移除。**「实时的好友状态推送」这一能力因此暂时消失**，留待 P5' 用复制机制重做 —— 当前它本来就是坏的，所以不是能力回归。

**② 原 P1 第 2 条（`MainPack` → 类型化 payload）拆分为 P1b，推迟执行。**

理由：P1 的四项交付（反射分发表、`RequestId`、B1/B4/B5 修复）都是**缺陷驱动**的，价值明确且可用编译与静态核对验证；而「万能信封换类型化 payload」是**设计洁癖驱动**，属于 B2，不是 bug。它的成本却最高：要改 18 个处理函数的签名、双端 proto 结构、以及 9 个 UI 面板的请求构造与解析，且**这些客户端改动在当前环境无法运行验证**（缺 MySQL + Unity 运行环境）。

在无法运行验证的前提下动 9 个 UI 面板，风险与收益不成比例。因此先交付缺陷修复，把 P1b 作为独立阶段，等具备联调环境后再做。

> ⚠ **前提变更（2026-09，需重新权衡）**：上述理由的核心是「**这些客户端改动在当前环境无法运行验证**
> （缺 MySQL + Unity 运行环境）」。现在这两个障碍都已消失：
>
> - Unity 2019.4.8f1 已安装且能编译运行（可验证 9 个 UI 面板的改动）；
> - 服务端不再依赖数据库（大厅已实测可登录）。
>
> 因此 P1b 的**成本风险已大幅下降**，它现在是一个“可以做但非必须”的项。
> 是否插入到 P3' 之前，取决于用户对「大厅交互体验」与「战斗迁移进度」的优先级取舍。
> 另一个支持先做 P1b 的理由：大厅目前仍是万能信封，P4'（局外程序改造）会再改一次同批代码，
> 两次改动叠在一起做可能比分开做更省。

### 9.8 P3' 分解与开工记录（历史阶段，未完成部分由§5替代）

**P3' 是全程最大的一段**（服务端迁入 ~3351 行 + 客户端既有 3636 行，合并为一套）。为降低风险，
拆成三个**顺序**子阶段，每个都有独立的可验收产出：

| 子阶段 | 内容 | 为什么在这个位置 |
|---|---|---|
| **P3'-1** | **UDP 传输层迁入 DS**——把 `Server/Server/ClientUdp.cs` 的真实收发能力搬进 Unity，由 `PMDsHost` 承载，替掉 `PMDS-PONG` 占位 | 战斗仿真必须挂在一个**真实**的 UDP 收发层上；先把落点铺好，P3'-3 才能一次做成。全程只动服务端/DS 侧，**零客户端改动**，风险最低 |
| **P3'-2** | **数值单一化**（S8 方案 A）——抽 `Shared/BattleNumericConfig.cs`（无 `UnityEngine` 依赖的纯 C# POCO），服务端 `<Compile Include>` 链接，删除 `Server/Server/HeroConfig.cs`；完成「配置只读」与「每玩家运行时状态」的拆分 | 合并两条仿真前必须让数值只有一个事实源，否则合并后仍会分叉。同时修掉移速分歧（§8 的证据 3） |
| **P3'-3** | **仿真合并**——服务端权威仿真迁入 Unity，与客户端预测仿真合并为一份**角色感知**的函数体（`PMNetRole`），并修掉 S7 Q7 (C) 类缺陷 | 依赖前两者 |

**顺序理由**：P3'-1 与 P3'-2 互不重叠（UDP vs 数值配置），但 P3'-3 同时依赖两者，
所以先做「零客户端改动」的 P3'-1，再做 P3'-2，最后合并。

#### 本轮新增的调查材料（仓库外，承 §9.3 的约定）

| 报告 | 覆盖 | 规模 |
|---|---|---|
| `D:/hyld-refactor-survey/S5_server_authoritative_sim.md` | 服务端权威仿真的 tick 顺序、全部状态字段、移动积分、全部数值常量、命中判定、子弹生命周期、Unity 依赖 | 801 行 |
| `D:/hyld-refactor-survey/S6_server_udp_transport.md` | UDP 传输层完整契约 + **17 个迁移接缝（S1-S17）** + **17 条既有缺陷（D1-D17）** + 依赖摩擦点 | 471 行 |
| `D:/hyld-refactor-survey/S7_client_prediction_sim.md` | 客户端预测的 11 项预测量、输入链路、对账/回滚全链路、全部数值、**Q7 五类「能合并 / 不能合并」清单** | 553 行 |
| `D:/hyld-refactor-survey/S8_numeric_config_divergence.md` | 两端数值来源清单 + **34 条不一致明细** + 3 组多声明 + 统一方案对比（推荐方案 A） | 542 行 |

#### P3'-2 完成记录（数值单一化）

**交付物**

| 文件 | 作用 |
|---|---|
| `Client/Assets/Scripts/Shared/BattleNumericConfig.cs` | **战斗数值的单一事实源**。零依赖（连 Google.Protobuf 都不需要），服务端用 `<Compile Include>` 链接同一份源码。含 `PMHeroId`（英雄编号）、`HeroNumeric`（英雄数值）、`SuperNumeric`（大招覆写）、`ResolvedAttack` + `ResolveAttack`（普通攻击/大招的统一解析）。 |
| `Client/Assets/HYLD1.0/Scripts/OldScripts/HeroData.cs` | 从 `HYLDStaticValue.cs`（525 行杂物静态类）抽出的英雄数据模型：`HeroName` + `Hero`。数值全部是**委托共享表的只读属性**；只保留身份与表现引用（子弹预制体/大招实体/爆炸特效）。 |
| `Tools/PMSharedConfigCheck/` | 门禁：以 netstandard2.0 + C# 7.3（Unity 2019.4 天花板）**零依赖**编译共享表。 |
| `Tools/PMHeroDataCheck/` | 门禁：编译 `HeroData.cs`（最小 `GameObject` 桩件）。**只读属性一旦被赋值就 CS0200**，这是抓「漏改的写入点」的编译期机制。 |
| `Tools/check_hero_id_alignment.py` | 门禁：`proto Hero` / 客户端 `HeroName` / `PMHeroId` **三方逐值对齐**（6 项检查）。 |
| `Tools/PMClientCheck/` | **客户端玩法层的真实编译门禁**（P3'-2 后期新增，并已扩展到全层）：覆盖约 **120 个文件 / 23k 行** —— `Scripts/Server/**`（含战斗层，P3'-3 的目标）+ `HYLD1.0/Scripts/**`（旧玩法层）+ `Scripts/Manger|PMNet|Shared|Math|Log/**` + 几个被引用的零散真实文件。**只桩 Unity 部分**（`ClientStubs.cs`），项目类型一律编真实文件（原则：桩三方、编一方）。<br>**价值有回归验证**：把 P3'-2 期间暴露的三个编译错误分别注入 → `CS1022` / `CS1501` / `CS1503` **全部被它报出**。同一批注入在旧流程下要用户点 4 轮编辑器编译才逐个暴露。 |
| `Tools/check_hero_table_shape.py` | 门禁：英雄表**形状**（`Heros.Add(<HeroName>, new Hero(...))`、参数个数与构造函数相容、集合与编号一致、行序一致）+ **表现引用与冻结基准逐字段一致**（7 项检查）。 |
| `Tools/_hero_presentation_baseline.json` | 改造前的表现引用基准（显示名/定位/子弹预制体/大招实体/爆炸特效/移动型大招），供上面那道门禁比对。 |
| `Tools/check_cs_braces.py`（**已强化**） | 由「只数括号数量」改为**顺序感知**：追踪运行深度、报告首次深度变负的行号、要求末尾归零。原版会放过「多一个 `}` 且少一个 `{`」的 CS1022 成因。 |
| `Tools/PMNumericEquivalenceTest/` | 验收：**数值迁移的行为等价性**（341 项检查），基准是迁移前 `HeroConfig.cs` 的实际取值快照。 |
| `Tools/check_client_authority_writes.py` | 门禁（**P3'-3c 新增**）：扫描 `Client/Assets/**/*.cs`，找出对 8 个**权威字段**（`playerBloodValue` / `playerBloodMax` / `playerManaValue` / `当前能量` / `可以按大招` / `playerPositon` / `isNotDie` / `移动速度`）的写入，按「许可路径 / 已登记例外 / **越界**」三分。这类缺陷编译期完全看不出来（类型对、能跑，只是语义错），必须显式锁住。<br>**负向验证**：注入 `playerBloodValue -= 123` 与 `移动速度 += 2` 各被精确报出。例外条目用 `!!` 前缀标出「已知的真实违反、尚未修复」，与「无害」在输出里一眼可辨。 |
| `Tools/PMBattleSimTest/` | 验收（**P3'-3a 新增**）：**仿真数学抽取的行为等价性**（**345715 项逐位比对 0 失败**）。把改造前的服务端 6 个 / 客户端 2 个公式逐字复制成 oracle，在随机+边界输入上比较 float 的 **4 个字节**（能区分 ±0.0 与 NaN）。含一组**结构等价**（子弹生成的「分支版 vs 单循环版」，45612 项）——公式级比对盖不到控制流改动。<br>**负向验证 7 项注入全被抓出**，含「只差浮点结合顺序」这种数学上相同、位模式不同的改法。 |
| `Tools/PMSDsProbe` 之外新增：`Tools/_old_heroconfig_snapshot.json` | 迁移前的数值基准快照（等价性测试的 oracle）。 |

**关键结果：数值迁移可证明是行为等价的**

用迁移前 `HeroConfig` 的**实际取值**（冻结成快照，不重新抄写）逐英雄逐字段比对：

- **341 项检查 0 失败**：20 个英雄 × （MaxHp / ReloadSeconds / ServerHitRadius / ShootDistance / BulletSpeed / BulletDamage / LaunchAngle / IsParabola / 解析后攻击的弹数·射程·弹速·伤害·扇形角）+ 6 个大招 × （射程/弹速/弹数/伤害/扇形角/IsParabola）+ 全局常量。
- **唯一有意的行为变更是移速**（旧服务端全局 `3.9` → 共享表逐英雄设计值），单独断言，恰好 5 个英雄受影响（柯尔特 4.05 / 麦克斯 4.08 / 黑鸦 4.20 / 里昂 3.96 / 帕姆 3.78）。
- 负向验证过：改雪莉的每次发射数 5→4 → 1 项失败；改柯尔特伤害 340→341 → 3 项失败。改英雄编号 16→15 → 对齐门禁精确报出 `HeiYa: proto=16 client=16 shared=15`。

**三个决策（由用户裁定）**

| 决策 | 取值 | 理由 |
|---|---|---|
| HP 口径 | 共享值 = **服务端现值**（960…），客户端设计值另存 `DesignHp` | 客户端 `BloodValue` 本就被权威值覆写（`BattleData.HitEvent.cs:204`），所以这是**行为零变化**的纯重构。实测两端比值 1.96~6.00 不统一（旧注释声称的「约 1/5」是错的），无法用单一系数还原，故原样保留设计值备用。 |
| 命中半径 | **独立字段** `ServerHitRadius`（全英雄 0.8） | 它与客户端 `ShootWidth`（弹体表现宽度，值域 0~4）语义不同。复用会让「统一数值源」顺带改掉命中手感（巴利 0.8→2、格尔 0.8→3）。旧服务端注释把二者混为一谈，已纠正。 |
| 清理范围 | **一并清理残留与死字段** | 见下。 |

**顺带修掉的真实缺陷**

| 缺陷 | 说明 |
|---|---|
| **移速分歧** | 服务端单一常量 3.9 vs 客户端逐英雄。5 名玩家每秒偏差 0.06~0.30，约 2 秒就超过 `MovementMaxPositionError=0.6` 阈值 → MoveAck 持续回校正（表现为位置被"拉回"）。现两端统一。 |
| **共享配置被当每玩家状态用** | `hero.BloodValue` 同时承载「配置值 / 权威血量 / 道具修正」三个来源，并被 `BattleData.HitEvent.cs:204`（权威）与 `HYLDModenProp.cs:64`（道具）写入 → 改一个玩家会改到**所有同英雄玩家**。现拆分：`PlayerInformation` 新增 `playerBloodMax` 与 `bulletDamage` 作为每玩家有效值；`PlayerLogic` / `HitEvent` / `HYLDModenProp` 全部改为读写它们。 |
| `GetReloadSeconds` 未知英雄抛异常 | 同一张表上 `Get`/`GetHp` 有默认值，`GetReloadSeconds` 用直接索引 → `KeyNotFoundException`。新表统一为「兜底 + 记录未知编号」，绝不抛（战斗循环里抛异常会废掉整局）。 |
| `frameTime` 三处独立声明 | 服务端 `Battle.cs`（`16/1000f`）、`ServerConfig.frameTime`、客户端 `ConstValue.frameTime` → 收敛为 `BattleNumericConfig.FrameTimeSec`。 |
| 蓝量上限 90 的 4 处字面量 | `PlayerLogic.cs:165`、`BattleData.Attack.cs:228`、`BattleData.HitEvent.cs:210`、`HYLDPlayerManger.cs:81` → 共享表 `ManaMax`。 |
| 死代码 `BulletLogic.setBulletInformation` / `applySuperParams` | 全项目零调用（含 prefab/scene 的方法名字符串引用也为 0）→ 删除两个方法。**类本身保留**：它挂在 `Bullet.prefab` 上，删类会变成 missing script。 |
| 13 行注释掉的旧格式英雄表项 | 引用的是已不存在的 21 参数构造函数，留着误导 → 删除。 |

**编译期暴露的两个问题（都已修，且都补了门禁）**

P3'-2 的 CLI 门禁全绿之后，真实 Unity 编译仍报了两个错——它们属于「结构对了但形状/完整性错了」，正是 CLI 侧最该覆盖而我漏掉的部分：

| 报错 | 根因 | 补的门禁 |
|---|---|---|
| `CS1022`（`HYLDStaticValue.cs(380,1)` 与 `(382,1)`） | 用脚本按「行」删 `SuperBulletParams` / `Hero` 两个类时，判「类的结束」用的是「这一行 strip 后是 `}`」——**构造函数的闭合 `}` 也满足该条件**，于是留下两个孤儿 `}` + 一段孤儿文档注释 | `check_cs_braces.py` 改为**顺序感知**（原版只比数量，而 CS1022 的典型成因「多一个 `}` 少一个 `{`」数量可能刚好相等） |
| `CS1501`（英雄表 20 行「Add takes 7 arguments」） | 生成器把 6 个构造参数**直接拼进** `Heros.Add(...)`，漏了 `new Hero(...)` 包装 | `check_hero_table_shape.py`（形状 + 参数个数 + 表现基准比对） |

两次修错后我改用「**整块确定性重写**」而不是继续用正则打补丁，并新增门禁把这类问题钉死在 CLI 阶段。已记为坑 #23 / #24。

**有意保留、未修的项（诚实记录）**

| 项 | 说明 |
|---|---|
| 道具加成对服务端不可见 | `HYLDModenProp`（狂暴瓶，挂在 5 个场景/预制体上，**不是死代码**）给移速 +1、伤害/血量 +30%。这些修正是**客户端本地的**，服务端不知情 → 移速 +1 会让该玩家重新出现预测分歧。这是**既有缺陷**（S8 已记录），修它需要把道具效果纳入复制（属 P5'）。P3'-2 只保证它不再污染共享配置。 |
| `NormalAttackManaRecover`（麦克斯=3） | 字段存在但**无任何消费点**（S8 记录为「有字段无实现」）。已放进共享表并注明未实现，不假装它能工作。 |
| `IsParabola` / `High` 的服务端消费 | 服务端仍「V1 用相同逻辑」（抛物线英雄跳过服务端碰撞尚未实现）。共享表已备好数值，实现属 P3'-3。 |
| `ShootWidth` 的服务端等价物 | 服务端没有「爆炸半径」概念（只有 `ServerHitRadius`）。是否引入属玩法决策，不属数值统一。 |
| 消费侧的编译保证 | `PMHeroDataCheck` 只覆盖 `HeroData.cs`。消费侧（`PlayerLogic` / `HYLDBulletManger` / `BattleData.*` / `TouchLogic`）依赖大量 Unity 类型，桩件成本高于收益，其正确性依赖**真实 Unity 编译**验证。 |

**真实 Unity 编译验证（P3'-2 关闭条件）**

CLI 门禁全绿之后，由用户在 Unity 2019.4 里编译。**结论：通过。** 证据：

| 证据 | 结果 |
|---|---|
| `Client/Library/ScriptAssemblies/Assembly-CSharp.dll` | 16:49 重新产出，911,872 字节（改造前 823,808） |
| 新增类型是否进包 | `Hero` / `HeroName` / `BattleNumericConfig` / `PMHeroId` / `HeroNumeric` / `SuperNumeric` / `ResolvedAttack` / `AddHero` / `PlayerInformation.playerBloodMax` / `VaultBPMax` / `PoisonDamagePerTick` **全部存在** |
| 编辑器日志 | 最后一次 `Finished script compilation` 在第 22219 行；**该行之后 `error CS` 数量 = 0**。全文 220 个错误行全部来自此前的失败尝试 |

**这次的代价与教训**：为了到达「编译通过」，用户在编辑器里点了 **4 轮**编译，每轮暴露一个错
（CS1022 多括号 → CS1501 漏 `new Hero(...)` 包装 → CS1503 参数错位 → 通过），
其中 CS1503 是我修 CS1501 时自己引入的，另有两次是修门禁脚本时改坏。
根因不是"改得太多"，而是**客户端侧当时没有自测编译手段**，只能把每次发现推给用户。
事后实测：`HYLDStaticValue.cs` 只用到 5 个 Unity 类型 + 5 个项目类型，
桩件成本远低于我当时的猜测。`Tools/PMClientCheck` 建成后，把三个历史错误分别注入，
**三个都被它报出**（CS1022 / CS1501 / CS1503）。已记为坑 #25 / #26。

**消费侧编译门禁已建成（P3'-2 收尾）**

上面提到的优先项已完成：`Tools/PMClientCheck` 从「只覆盖 3 个文件」扩展到**整个客户端玩法层**
（约 120 文件 / 23k 行，含 `Scripts/Server/**` 战斗层与 `HYLD1.0/Scripts/**` 旧玩法层）。

推演过程（值得记录，因为它推翻了当初的判断）：把真实文件加进来后，缺失类型从 674 个错误
收敛到 **去重后 42 个**，且几乎全是 Unity 类型。补齐 Unity 替身（`ClientStubs.cs`）后即为 **0 错误 0 警告**。
关键取舍：
- **桩三方/遗留、编一方**：EasyTouch 只编两个自包含小文件（`MovingJoystick` / `MonoSingleton`），
  其余 4912 行排除；斗地主的遗留 `TCPSocket` 也只做最小替身（否则会拉进它那套卡牌网络框架）。
- **不猜桩件成本**：当初判断「客户端依赖太多、不划算」是错的，实测后成本远低于预期（见坑 #25）。
- 过程中遇到两次**桩件保真度**假阳性（漏 `implicit operator bool`、`HideInInspector` 未允许属性），
  方向安全（桩件过严），但说明桩件本身也需要被审视。

**P3'-3 的门禁前置条件因此已经就位**：往客户端搬战斗代码时，编译错误会在我这一侧暴露，
而不是等用户点编译。

#### P3'-1 完成记录（本轮）

**交付物**

| 文件 | 作用 |
|---|---|
| `Client/Assets/Scripts/Server/Net/PMUdpRouter.cs` | 真实战斗 UDP 路由层（自 `Server/Server/ClientUdp.cs` 迁移）。**UnityEngine 无关**，日志通过构造参数注入。 |
| `Client/Assets/Scripts/Server/Net/PMSingleBattleRegistry.cs` | DS 单局形态的战斗注册表（`BattleManage` 的退化对应物）；`AllowUnassignedPlayers` 在 P4' 接入大厅分配后置 false。 |
| `Client/Assets/Scripts/Server/Boot/PMDsHost.cs` | 移除 `PMDS-PONG` 占位；接入路由层；`Update` 先 `DrainInbound()` 再 `DriveTick()`。 |
| `Tools/PMUdpRouterCheck/` | **编译门禁**：用 Unity 2019.4 的语言面（netstandard2.0 + C# 7.3）编译路由层，且**不引用 UnityEngine**（若有人加了 Unity 依赖会立刻失败）。 |
| `Tools/PMUdpRouterTest/` | **功能测试**：真实 UDP 回环，**43 项检查 0 失败**，无需 Unity。 |

**结构性修正（相对旧 `LZJUDP`）**

1. **线程模型反转**：旧实现在接收线程上直接执行业务 handler 并打日志（13 处），在 Unity 中必然崩溃。
   新实现：接收线程只做「解析 + 入队」，handler 与日志都在主线程。
   **这条性质有专门的测试用例断言**（收到数据报后 handler 未被调用，直到 `DrainAndDispatch`）。
2. **发送失败不再抛出**：旧实现 `SendImmediate` 无 try/catch，发送异常会冒泡到 `BattleLoop` 外层 catch
   并把循环置停，静默废掉整局（S6-D9）。新实现返回 false + 计数。
3. **入站队列有界**（4096）+ 丢弃计数；旧实现无背压。
4. **handler 异常隔离**：旧实现会冒泡进接收线程 catch-all；新实现在主线程就地隔离。
5. **重复注册被拒**：旧实现静默覆盖（S6-D16）。
6. **路由失败/无 handler 分别计数**：旧实现只打一行日志、无计数器，无法从心跳发现「包在丢」（S6-D11）。
7. **不迁 NetSim 脚手架**（约 450 行演示用丢包/延迟注入）。
8. **生命周期补全**：`Shutdown` 会摘掉路由、清空队列、注销战斗；旧实现全层无 `Close`/`Dispose`。

**DS 实测验证（真实 Unity 无头构建，非桩件）**

用户重新构建 `HyldDS` 后，用 `Tools/PMDsProbe` 向真实 DS 发真实 protobuf 包，证据如下。

DS 侧日志（`HyldDS/logs/ds_ds-p31.log`）：

```
[PMNetBootstrap] process=DedicatedServer mode=DedicatedServer ... listen=0.0.0.0:7801 tickrate=30
Forcing GfxDevice: Null
[PMDsHost] ===== HyldDS 就绪 =====
[PMDsHost] 监听=0.0.0.0:7801（实际绑定端口 7801）
[PMDsHost] UDP 路由层已就绪（battleId=1，已注册诊断 handler；P3'-3 将由权威战斗仿真取代）
[PMUdpRouter] RegisterBattle: battleId=1
[PMDsHost] heartbeat tick=600 recv=0 ...                       ← 30Hz × 20s，帧驱动正常
[PMDsHost] router recv=4 queued=4 dispatched=4 sent=1 pending=0
           parseErr=0 qFull=0 unroutable=0 noHandler=0 handlerErr=0 sendErr=0 battles=1
[PMUdpRouter] BattleReady 建链: uid=101 battleId=1 endpoint=127.0.0.1:56905
[PMUdpRouter] 注册表尚未分配 battlePlayerId（DS 单局模式）...    ← 「未分配」路径按设计生效
[PMDsHost] battleDispatch total=2 lastAction=BattlePushDowmPlayerOpeartions
```

对照 `PMDsProbe` 的四步（网络侧）：

| 步骤 | 动作 | 结果 |
|---|---|---|
| 1 | 未建链端点的 Ping | **无回包** ✓（不成为开放反射器） |
| 2 | `BattleReady` (uid=101, battleid=1) | 建链成功 ✓（DS 日志出现「BattleReady 建链」+ 端点键） |
| 3 | 已建链端点的 Ping | **收到 Pong，Timestamp 原样回带** ✓ |
| 4 | 业务包（移动上行） | 被分发 ✓（`battleDispatch total=2`） |

计数完全自洽：`recv=4`（4 个包）`sent=1`（1 个 Pong）`parseErr=0` `unroutable=0` `noHandler=0`。
第 3 步是**端到端证据**：真实 DS 的「线程池接收 → 解析 → 入队 → 主线程 drain → 分发 →
查路由 → 回包 → 客户端收到」整条链路已跑通。

**实测暴露并已补的观测缺口**：未建链端点的 Ping 原先是静默丢弃、`unroutable` 仍为 0，
于是「有人在扫端口」与「客户端丢了路由」两种情况都无法从日志发现（正是 S6-D11 抱怨的模式）。
已新增 `unroutedPing` 计数（首次与每 256 次各打一行警告），并补了对应测试用例。

**新增工具**：`Tools/PMDsProbe/` —— 向运行中的 DS 发真实 protobuf 包并校验响应。
DS 是无头进程，除了日志没有别的观测手段；有了真实路由层之后手搓字节不可行，故固定成工具。
P3'-3 验证权威帧下行/移动上行/ack 回带时会继续用它。

**遗留（有意保留，不在 P3'-1 修）**

| 项 | 说明 |
|---|---|
| 建链信任锚仍是客户端自报的 uid（S6-D12） | 正确修法是「大厅下发带凭据的玩家名册」，属 P4'。**刻意不做半吊子校验**——严格拒绝端点重绑会破坏合法的断线重连，反而更糟。 |
| `battleId` 尚无外部来源 | 当前为 `PMSingleBattleRegistry.BattleId`（默认 1）。P4' 由大厅下发的匹配信息填入。 |
| 收包缓冲 64 KB（旧实现 1024 B） | 放大是**严格超集**（旧实现在超过 1024 B 时会抛 WSAEMSGSIZE 并丢弃），不会破坏现客户端。发送侧的长度约束留待 P7（B10）。 |
| 未迁：`IOControl(SIO_UDP_CONNRESET)` | Windows 专用（DS 平台已定 Windows，D8），但新架构下 socket 由宿主管理，是否需要随 P3'-3 一并评估。 |

**联调期发现的两个环境/运维问题（已修或已定性）**

| 问题 | 定性 | 处置 |
|---|---|---|
| **服务端「跑几分钟后端口静默消失」** | **不是产品缺陷**：由我用 `cmd /c start /B` 启动导致——stdin 未重定向但随父 shell 结束而 EOF，`Console.Read()` 返回 -1 → `Main` 正常返回。实测改用 PowerShell `Start-Process` 真正分离后长期存活 | 启动方式改用 `Server/run_lobby.bat` + `Start-Process`。另新增 pid 与退出路径日志（`ProcessExit` / `Main 返回`），使三种退出情形事后可区分 |
| **运行中的日志读不到**（`cat`/`cp` 得 0 字节、Device busy） | **真实运维缺陷**：`Server/Tool/Loging.cs` 用 `File.Open(..., FileAccess.Write)`，默认 `FileShare.None` 独占锁定 | 改为 `new FileStream(..., FileShare.ReadWrite)`；已验证运行中可读 |

#### P3'-1 的设计决策（已定，来自 S6）

| # | 决策 | 理由 |
|---|---|---|
| 1 | **线程模型反转**：后台回调**只把包入队**，主线程 `Update` 消费并调用 handler、打日志 | S6 §5 实测：现有实现**在后台线程直接执行业务 handler 并直接打日志**（13 处），这是既有缺陷。P2' 已在 `PMDsHost` 确立「后台只入队、主线程输出」纪律（日志队列 256 上限 + `_logLock`），包路径必须同构 |
| 2 | **socket 归属唯一化**：DS 宿主独占 socket，传输层接受注入而非自建 | S6-S1：同一端口不能两个 socket 同时 `Bind`，且 `ReuseAddress=false` 会让第二个直接失败 |
| 3 | **补全生命周期**：新增幂等关闭路径 | S6-S10/S6-D4：现有实现**全层无 `Close`/`Dispose`**，两条 `while(true)` 线程无退出条件。D6「一局一进程、打完自毁」要求 DS 能主动关 |
| 4 | **不迁 NetSim 脚手架** | S6-S13：约 450 行丢包/延迟注入演示代码，与客户端调参 UI 耦合，DS 上不需要 |
| 5 | **保留 `battleId` 路由但退化为单战斗** | S6-S14：保留零成本，但 DS 单局形态下 `battleId` 来源需明确（`-matchid`） |
| 6 | **环境差异统一处理** | `Environment.TickCount64` 在 Unity 2019.4 **不存在**（S6-F1 实测）；`Logging.Debug` 客户端没有（只有 `Logging.HYLDDebug`）；`IPManager` 客户端已存在同名重复类；客户端无 `ServerConfig`/`BattleManage` |
| 7 | **收包缓冲口径显式化** | S6-S5：现有 1024B 是**真实契约上限**（计划 B10 已记录），发送侧无长度校验。迁入时沿用 1024 保守口径，并把「放大」留作独立决策 |

#### P3'-1 必须顺带修的既有缺陷

| ID | 事实（S6 §10） | 严重度 |
|---|---|---|
| **D9** | `BattleLoop` **持有 `_battleLock`** 时调 `Send`（`Battle.cs:498-509`）→ 发送异常冒泡到 `BattleLoop` 外层 catch（`:550-553`）→ 置 `_isRun=false` 直接退出循环，**不经过 `HandleBattleEnd`** → 该局不发 GameOver、不 `UnregisterBattle` | **高**：一次网络抖动就静默废掉整局 |
| D5 | `RecvThread` catch-all 无退避、无致命退出：socket 进入永久错误态会成为高频空转 + 日志刷屏 | 中 |
| D7 | `TryGetBattleIdForNetSim` 空 `catch { }` 静默吞掉一切异常 | 中 |
| D12 | 端点映射的信任锚是**客户端自报的 `uid`**，未见与 TCP 会话身份的绑定校验 | **高**（安全） |
| D16 | `RegisterBattle` 无重复注册保护（直接覆盖，静默顶掉） | 中 |
| D17 | `handler.Invoke` 在锁外，与 `UnregisterBattle` 存在竞态（注销后仍可能被调用一次） | 中 |
| D2/D11/D14 | 无长度校验；路由失败无计数器（无法从心跳发现「包在丢」）；任何来源数据报都完整解析一次 | 低-中 |

> 修 D9/D12 属于「迁移中必须一并处置」，不是可选优化——它们会让新架构继承同样的静默失效模式。

#### P3'-3 分解与开工记录

**P3'-3 不是一件事，是三件。** 开工前先量了实际剩余面，结论与计划标题给人的印象不同：

| 子阶段 | 内容 | 状态 |
|---|---|---|
| **P3'-3a** | **仿真数学单点化** —— 把两端各自内联的仿真公式收进 `PMNet.Shared.PMBattleSim`（零依赖），两侧都调它 | **本轮完成** |
| **P3'-3b** | **权威仿真迁入 DS** —— 让 Unity 无头产物真正承载 `BattleController`；**主线程 tick**（D13）、与大厅类型解耦、`LZJUDP`→`PMUdpRouter`。旧提案只作历史参考，当前方案见§3.9与§5 | **原定稿已撤销，按R阶段重设计** |
| **P3'-3c** | **S7 Q7 (C) 类缺陷** —— 客户端自行改写权威字段（S7 列了 6 处，实查 3 处不可达 + 1 处保留） | **本轮完成** |

**为什么先做 a**：a 是 b 与 c 的**共同前置**。不做单点化就直接把服务端仿真搬进 DS，
等于在 Unity 里再复制一份数学，重复实现从 2 份变 3 份；而 c 的每一处修复都要判断
「到底以哪一端的计算为准」，只有在公式已经单点之后这个问题才有确定答案。
a 还有一个独立性质：**它是可以逐位证明的**（见下），风险最低。

**开工前先核实的一件事（避免按错误前提开工）**：计划 §2.8 证据 3 把「移速模型分歧」
列为 P3'-3 待根治项之一，但**实测发现它已在 P3'-2 修掉了** —— 服务端现在走
`Battle.cs:720 GetMoveSpeedFor(bpId)` → `BattleNumericConfig.Get(hero).MoveSpeed`，
即逐英雄设计值。所以 P3'-3a 的范围里不再包含这一项。

##### P3'-3a 交付物

| 文件 | 作用 |
|---|---|
| `Client/Assets/Scripts/Shared/PMBattleSim.cs`（新增，+ `.meta`） | **战斗仿真的唯一数学事实源**。零依赖（不引 `UnityEngine`、不引 `Google.Protobuf`、不引任何项目类型）。含：移动方向归一化、位置积分（多帧）、权威速度、攻击朝向换轴+镜像、散弹扇形、子弹步长与超距、球-点命中判定、大招回能。 |
| `Tools/PMBattleSimTest/`（新增） | 验收：把**改造前的公式逐字复制**成 oracle，在 34.5 万条随机+边界输入上与核心**逐位**比对。 |
| `Client/Assets/Scripts/Math/BattleFloatMath.cs` | **降级为薄适配层**：只做 `Vector3` ↔ 标量的转换，本身不再含任何数学（8 处调用点签名不变）。 |
| `Server/Server/BattleController.Network.cs` | `SimulateAuthoritativeMove` / `CalculateAuthoritativeVelocity` 改调核心；**队伍镜像符号仍在本侧算**（见下）。 |
| `Server/Server/BattleController.Bullets.cs` | 方向、扇形、命中、回能、两处子弹步长（追帧 + 正常 tick）改调核心；**单发/散射的分支合并成一个循环**。 |
| `Client/Assets/Scripts/Server/Manger/Battle/HYLDPlayerManger.cs` | `AdvancePlayerPosition` 改收原始输入并调核心的 `TryAdvancePosition`。 |
| `Client/Assets/Scripts/Server/Manger/Battle/BattleData.Prediction.cs` | 回滚重放的积分改调核心（多帧形式，`coveredFrames`）。 |
| `Server/Server/Battle.cs` | **删除死代码 `UpdatePlayerPositions`**（零调用点，内含**第三份**移动积分公式）。 |
| `Server/Server/ServerVector3.cs` | **删除 `Normalized()` / `Magnitude()`**：随数学收口而零使用，留着就是第二份归一化实现（含它自己的 `1e-6` 阈值）。`Distance` / `+` / `*` 仍在使用，保留。 |

##### 设计要点：`sign` 是参数，不是算出来的

两端的**坐标系锚点不同**，而且都是对的：

- **服务端**：`baseTeamId = min(teamIds)` 在 `X=+15`，`teamSign = (tid != baseTeamId) ? -1 : 1`
- **客户端**：把**本地玩家**放在 `X=+15`（自锚定），`sign = (tid != selfTeam) ? -1 : 1`

两者相差一个**全局镜像**，客户端在上传预测位置时用 `GetClientToServerSign()`
（`BattleData.Prediction.cs:245`）做这个转换。也就是「客户端自锚定 + 上传时镜像」
与「服务端基锚定」是等价的两种写法。

所以核心**只接收调用方算好的 `sign`**，不负责决定锚点 —— 强行统一锚点会破坏客户端的自锚定坐标系
（它到处依赖「自己在 +15」）。这一条已写进核心文件头，因为它是本次最容易搞错的地方。

##### 行为等价性：34.5 万项逐位比对，0 失败

`Tools/PMBattleSimTest` 把改造前的公式逐字复制为 oracle（服务端 6 个、客户端 2 个），
在随机输入 + 边界输入上比较 float 的**4 个字节**（`==` 区分不了 ±0.0 与 NaN，逐位可以）：

| 比对组 | 项数 | 失败 |
|---|---:|---:|
| `TryGetMoveDirection` | 18067 | 0 |
| `TryAdvancePosition`（含 Y 不变、零输入无惯性、`frameCount<=0`） | 18013 | 0 |
| `TryGetVelocity` | 6003 | 0 |
| `TryGetAimDirection` | 12000 | 0 |
| `SpreadDirection`（含首尾张角 = 总张角） | 128 | 0 |
| `BulletStepDistance` / `IsBulletExpired` | 2013 | 0 |
| `IsHit`（含「恰好等于半径算命中」） | 8004 | 0 |
| `RechargeSuperEnergy`（含整数除法 45→22） | 123 | 0 |
| **子弹生成：分支版(旧) vs 单循环版(新)** | **45612** | 0 |
| 客户端 `BattleFloatMath` 语义等价 | 16000 | 0 |
| 已知有意差异（阈值薄片，显式计数） | 219752 | 0 |
| **合计** | **345715** | **0** |

**结构等价是单独一组**：把「单发/散射分支」改成「单循环」是**控制流**改动，
公式级比对盖不到，所以另写一组 oracle 复刻旧的分支结构、与新的循环实现比对方向序列。

**负向验证（7 项注入，7 项被抓住）**：

| # | 注入 | 结果 |
|---|---|---|
| ① | 扇形步长 `total/(count-1)` → `total/count` | FAIL（94 项） |
| ② | 移动方向 `-mx*sign` → `mx*sign` | FAIL（21006 项） |
| ③ | 积分结合序 `(s*dt)*n` → `s*(dt*n)` | FAIL（105 项） |
| ④ | 命中 `<=` → `<` | FAIL（1 项） |
| ⑤ | 回能 `damage/2` → `(damage+1)/2` | FAIL（12 项） |
| ⑥ | 归一化阈值 `1e-6` → `1e-5` | FAIL（8 项） |
| ⑦ | 单发扇形不再是恒等 | FAIL（10 项） |

③ 尤其值得记：`moveSpeed * frameTimeSec * frameCount` 与 `moveSpeed * (frameTimeSec * frameCount)`
是数学上相同的表达式，**只差浮点结合顺序**，仍然被抓出 105 项 —— 这正是这类改动最危险的地方。

##### 测试自己也被测试抓了一次（诚实记录）

第一版测试报了 8 项失败，其中 4 项是**测试的 oracle 不忠实**：

- oracle 把「单发（count ≤ 1）」也走了扇形公式，凭空造出「被转了 `-totalAngle/2`」的结果，
  反过来诬陷了正确的实现。真实服务端在 `if (bulletCount <= 1)` 里**直接返回已归一化的 baseDir、根本不进扇形代码**，
  所以核心对单发必须是**恒等**。修正了 oracle（复刻**控制流**而不只是公式），并把核心的单发行为固定为恒等。
- 另 4 项是「首尾张角」断言用错了符号约定：旋转公式绕的是 −Y，`atan2(x,z)` 测得的角度与 `angleDeg` 反号。
  改成断言**首尾夹角 == 总张角**（与约定无关的真实性质）。

还有一次负向验证的注入**放错了位置**（插在早退之后成了不可达代码，等于没注入，测试照常 PASS）——
说明「注入必须真的被执行到」这件事本身也需要确认。

##### 一处**有意**的行为变更（已显式计数，不是遗漏）

旧客户端 `ToWorldDirection` 走的是 `UnityEngine.Vector3.normalized`，其内部判零阈值是 Unity 的
`kEpsilon = 1e-5`；而服务端一路用 `1e-6`。合并后统一到**服务端的 `1e-6`**（权威口径）。

差异只可能出现在「输入模长落在 `(1e-6, 1e-5]`」这个薄片里（测试采样 9 万余次全部不同），
而该区间被 `TouchLogic` 的摇杆死区（`0.02` / `0.12`）完全屏蔽 —— **实战不可达**。

另外，客户端把 `movementDir * speed * frameTime`（`(dir*speed)*dt`）改成核心的
`dir * (speed*dt)`，与服务端的结合顺序**对齐**（此前两端可差最后一位）。

##### P3'-3a 验收

| 门禁 | 结果 |
|---|---|
| `Tools/PMBattleSimTest`（新增） | **345715 / 0** |
| `Tools/PMSharedConfigCheck`（零依赖 + C# 7.3，自动覆盖新的 `Shared/**`） | 0 错误 |
| `Tools/PMClientCheck`（客户端玩法层 ~120 文件 / 23k 行） | 0 错误 0 警告 |
| `Tools/PMHeroDataCheck` / `PMNumericEquivalenceTest` | 0 错误 / 341 ∶ 0 |
| `check_hero_table_shape.py` / `check_hero_id_alignment.py` | 7 ∶ 0 / 6 ∶ 0 |
| `Server/Server.csproj` | 0 错误（8 个警告均为既有：NU1701 旧包 + CS8981 protobuf 小写别名） |
| `Tools/PMUdpRouterCheck` / `PMUdpRouterTest` | 0 错误 / 44 ∶ 0 |
| `Tools/PMUnityGlueCheck` / `PMNetLangCheck` / `PMNetVerify` | 0 错误 / 0 错误 / 27 ∶ 0 |
| `Tools/PMServerSmokeTest`（真实起服务端跑） | **17 / 0** |
| `Tools/check_cs_braces.py`（8 个改动文件） | PASS |

##### 仍未收口的（诚实地列出来）

- **客户端的扇形是故意随机的**，与本轮单点化的权威扇形**不是**同一个东西：
  `HYLDBulletManger.SpawnFanPattern` 用 `total/count` 步长 + `Random.Range(0.8f, 1.2f)` 抖动，
  属表现层编排（S7 Q7(A) 第 4 条）。玩家的命中由服务端公式决定，客户端画得自然与否不影响结果。
  **所以它不该被合并** —— 核心里的 `SpreadDirection` 只服务于权威侧，注释已注明。
- **道具/减速对服务端仍不可见**（`HYLDModenProp` 的 +1 移速、麦克斯大招给队友加减速）。
  核心接收的是调用方传入的移速，所以两侧仍会分叉 —— 修它需要把效果纳入复制（属 P5' 与 P3'-3c）。
- `Network.cs:592` 的 `Math.Abs(move) <= 1e-6f` 是**逐分量**零判定，与核心的**向量长度**判定不是同一个谓词，
  因此没有收进核心。它是「OldMove 分类」的策略判断，不是移动数学。

##### P3'-3c 完成记录（消除客户端对权威字段的改写）

**用户裁定**：删除（不迁移到服务端）；死亡判定保留为纯表现兜底。

##### 开工前先把「联机下到底可不可达」量清楚

Q7(C) 的 6 项在计划里被描述为「客户端自行改写权威字段」。但**「改写了」与「真的生效」是两件事**，
删之前逐项核实了可达性，否则会把「其实在跑的机制」当残骸删掉。结论比预期明确得多：

| # | 项 | 触发链 | 联机下的真实效果 |
|---|---|---|---|
| 19 | 乌鸦毒 tick 扣血 | `shell.cs:344` 置 `isPoisoning` → `PlayerLogic` 毒 tick | **不可达**：写入点在碰撞回调里，而联机视觉子弹的碰撞体被全部禁用（`shell.cs:129-139`）→ `OnTriggerEnter` 永不触发 |
| 20 | 回血 | `playerCure()` ← `isCanCure1 && isCanCure && cureTime>1s` | **死条件**：`isCanCure` 全项目**没有任何地方置 true**（默认 false，只有 `HYLDBulletManger.cs:130` 置 false）→ 恒不成立 |
| 21 | 护盾 | `是否有防护罩` + 本地 3 秒计时 | 读取方（`shell.cs:334` / `Boom.cs:80,93`）都在不可达的碰撞回调里；字段默认值却是 `true` |
| 22 | 死亡判定 | `playerBlood < 0` → `playerDieLogic()` | **保留**（见下） |
| 24 | 麦克斯大招给队友加速 | `移动型大招.cs:89` → `PlayerLogic.减速(-1.5f)` | **唯一可能真的跑起来的**：麦克斯是唯一 `移动型大招=true` 的英雄，该实体由 `HYLDBulletManger:494` 在联机下生成 |

**决定性的一点**：这些代码**一旦执行就会 NRE** ——

- `PlayerLogic.减速` 里 `body.transform.Find("Canvas").Find("减速")`，而**全资产不存在名为 `减速` 的 GameObject**；
- `PlayerLogic` 的 `防护罩` 字段**在所有 prefab 里都没有绑定**（运行时为 `null`）。

也就是说从来没有人真正走过它们。这比「推测无效」强得多。

##### 交付物

| 文件 | 改动 |
|---|---|
| `PlayerLogic.cs` | 删掉毒 tick / 回血 / 护盾计时 / 减速·Recover 四段（含 `原始速度`、`timerisPoisoning`、`cureTime`、`damageTime` 等字段）；HP 改为**只读**权威值；受击飘字改为由「权威 HP 相对上一帧下降」驱动 |
| `移动型大招.cs` | 删掉 `OnTriggerEnter`（唯一的 `减速(-1.5f)` 调用点） |
| `shell.cs` | 删掉护盾检查、毒触发块、减速调用块（三者都在不可达路径，但必须删才能编译）；对仍留存的单机权威写入点**就地标注** |
| `Boom.cs` | 删掉两处护盾检查；对同一块里的 `playerBloodValue` 写入**就地标注未改动的原因** |
| `HYLDStaticValue.cs` | 删掉 4 个只被已删机制使用的常量（`CureDamageFreeSeconds` / `CureIntervalSeconds` / `CureHpRatio` / `ShieldSeconds`）与 `PlayerInformation` 上的 4 个字段（`是否有防护罩` / `isCanCure` / `isCanCure1` / `isPoisoning`）。**毒的两个常量保留**（`TextLogic` 仍在用） |
| `HYLDBulletManger.cs` | 删掉 `isCanCure = false` 写入（该字段恒为 false，写入无效果） |
| `Tools/check_client_authority_writes.py`（新增） | **门禁**：扫描 `Client/Assets/**/*.cs`，找出对 8 个权威字段的写入，按「许可路径 / 已登记例外 / 越界」三分。**负向验证：注入 `playerBloodValue -= 123` 与 `移动速度 += 2` 各被精确报出** |

##### 死亡判定（第 22 项）为什么保留

用户裁定「保留为纯表现兜底」。它做的是关 UI / 关碰撞体 / 播死亡动画这些**表现动作**，
判定条件是 `playerBlood < 0`，而 `playerBlood` 是权威 HP 的本地镜像 —— 所以它**不改任何东西、也不会与权威打架**。
代码里已就地注明：**不参与权威**，胜负由服务端 `GameOver` 下发。

##### 新门禁把整张图一次摊开（这是它的价值）

门禁第一次运行报了 **10 处越界写入**，其中 **3 处是 S7 没列到的**：

| 新发现 | 定性 |
|---|---|
| `shell.cs:348-349` 写 `当前能量` + `playerBloodValue` | 单机碰撞回调 → 联机不可达（已登记例外） |
| `shell.cs:378` 写 `isNotDie` | 同上 |
| `移动型大招.cs` 4 处写 `playerPositon` / `isNotDie` | 被 `被控制` 门控，而 `被控制` 的唯一写入点也在不可达回调里（已登记例外） |
| `TouchLogic.cs:62` 写 `可以按大招` | **许可**：UI 输入门，用「权威能量 ≥ 上限」这个同一谓词点大招摇杆，权威路径每批用同一规则重算 |

以及一处**真实且联机可达**的违反，属 S7 的 Q4/推断 #1 而非 Q7(C)，本轮**未修**：

| 项 | 事实 | 处置 |
|---|---|---|
| `HYLDModenProp`（狂暴瓶）：`playerBloodMax += 30%`、`移动速度 += 1`、`bulletDamage += 30%` | 道具效果完全在客户端本地施加，服务端不知情。更麻烦的是**血上限不会被纠正** —— 服务端 `maxHp` 只在首批初始化写入（`HitEvent.cs:199` 的 `isFirstInit` 分支），之后不再下发 | 已在门禁 EXEMPT 里以 `!!` 前缀标注为「**已知的真实违反，尚未修复**」，并在计划「有意保留、未修的项」里指向 P5' |

门禁特意区分了 `!!` 前缀 —— 让「已知未修」和「无害」在输出里一眼可辨，不靠记忆。

##### `Boom.cs` 的处理（刻意只标注、不改）

`Boom.cs:87/101` 也在写 `playerBloodValue`。核实其在联机下**不可达**：
联机生成的两组 prefab（`BetterShells` 37 项、`大招实体` 17 项）与含 `BoomCreater` 的 prefab **交集为 0**；
`Boom` 只由单机链 `BulletLogic`/`BoomCreater` 驱动。

既然不可达，就**只就地标注、不改动** —— 顺手改掉单机投掷物伤害不属于本次范围。
（护盾检查是例外：字段被删了，必须一起删才能编译。）

##### `TextLogic.cs` 刻意不动

它是**试玩模式的机器人**（文件头注释即写明），HP 是硬编码的 `playerBlood = 10000`，
**从不读写** `Players[].playerBloodValue`，因此不是第二权威源。删它只会破坏单机试玩，对权威收口没有贡献。

##### P3'-3c 验收

| 门禁 | 结果 |
|---|---|
| `Tools/check_client_authority_writes.py`（新增） | **越界写入 = 0**（许可 32 处 + 已登记例外 13 处全部显式登记） |
| `Tools/PMClientCheck`（客户端玩法层 ~120 文件 / 23k 行） | 0 错误 0 警告 |
| `Tools/PMBattleSimTest`（P3'-3a） | 345715 ∶ 0 |
| `Tools/PMSharedConfigCheck` / `PMHeroDataCheck` | 0 错误 / 0 错误 |
| `PMNumericEquivalenceTest` / `check_hero_table_shape.py` / `check_hero_id_alignment.py` | 341 ∶ 0 / 7 ∶ 0 / 6 ∶ 0 |
| `Tools/PMUdpRouterCheck` / `Server.csproj` | 0 错误 / 0 错误 |
| T1 / T2 / P2' 胶水 / 协议 `--check-only` | 0 错误 / 27 ∶ 0 / 0 错误 / exit 0 |
| `Tools/check_cs_braces.py`（6 个改动文件） | PASS |

**过程中的一次自我纠正**：本门禁的负向验证一开始「没抓到注入」，我没有直接下结论说「门禁有 bug」，
而是先查注入是否真的写进了文件 —— 结果两次注入都因模式匹配失败而**根本没发生**。
换成显式插入后，两次注入都被精确报出。顺带发现并修掉一个假阴性：传单个文件路径时
`os.walk` 什么都不产出，于是「已扫描 0 个」却报 PASS —— 现在 0 文件会直接报 ERROR。

##### P3'-3b 原目标与实现方式（历史草案，已撤销）

> 以下仅保留依赖面调查和历史提案，不再是实施或验收依据。固定16ms、只搬编排、完全逐位不变、先删大厅后做启动器等要求均由§3.9/§5取代。

###### 目标：什么叫做完

一句话：**把权威战斗仿真从 `Server/` 那个 .NET 进程搬进 Unity DS，由 Unity 主线程驱动**。
做完之后 `HyldDS.exe` 单独就能承载一局完整战斗，`Server/` 退化为**纯大厅**（登录/好友/房间/匹配），不再含任何战斗仿真。

可验收的锚点（4 条，都要能证）：

| # | 锚点 | 怎么证 |
|---|---|---|
| 1 | DS 进程独立跑完一局：收上行 → 推进权威帧 → 下行权威状态与 `HitEvent` → 判定 `GameOver` | `PMServerSmokeTest` 式的场景测试 + `PMDsProbe` 真实发包 |
| 2 | 战斗仿真文件从 `Server.csproj` 移出（大厅不再编译它们） | 检查 `Server.csproj` 的 `Compile Include` 与产物程序集内容（坑 #7：链接写错会静默贡献 0 文件） |
| 3 | 大厅侧**零引用**战斗仿真类型（`BattleManage` / `BattleController` / `BattleContext`） | 全仓 grep + 编译 |
| 4 | 仿真行为与现在**逐位一致** | 数学已由 P3'-3a 单点化（`PMBattleSim`，345715 项逐位等价）；b 只搬**编排**，不碰公式 |

###### 现状：P3'-1 只搬了传输层，仿真还在旧进程

| | 现在（P3'-1 之后） | P3'-3b 之后 |
|---|---|---|
| UDP 收发与路由 | **已在 DS**（`PMUdpRouter` + `PMSingleBattleRegistry`） | 不变 |
| 权威战斗仿真 | **仍在 `Server/` 进程**（`Battle*.cs` 7 文件 2357 行） | **在 DS** |
| `PMDsHost` | 占位：注册一个**诊断** handler（`HandleDiagnosticBattlePacket`） | 换成真实的 `BattleController.Handle` |
| 大厅 | 同时扛着「匹配 + 战斗仿真」 | 只剩匹配与局外 RPC |

`PMDsHost.cs:176` 已经把这个接缝写在注释里了：**「P3'-3 迁入权威战斗仿真时，此处由真实的 `BattleController.Handle` 取代」**。b 就是兑现这句话。

###### 依赖面实测（量过，不是估计）

开工前逐项量了「这批代码到底依赖什么」，因为它决定了拆分方案：

| 依赖类 | 具体 | 处置方向 |
|---|---|---|
| **大厅类型** | `MatchUserInfo`（10 处：`Battle.cs` 3 / `BattleContext.cs` 4 / `BattleManage.cs` 3）、`MatchingController.FightPattern`（`BattleContext` 持有）、`using Server.Controller`（3 文件） | **赛制不参与仿真** —— 逐点核过：`FightPattern` 在仿真代码里零消费，唯一的读取点是 `BattleManage.cs:236`（拼结算包用），而那属于大厅职责。所以 DS 侧只需本地 DTO：`battleId` + 名册（uid → battlePlayerId → teamId → hero） |
| **TCP 大厅** | `Battle.cs:164` 构造收 `Server`；**`Battle.cs:200` 经 TCP 给大厅客户端发 `StartEnterBattle`**；`BattleManage.cs:244` 经 TCP 取 `Client`（结算 `BattleReview`） | **这两处是「对外的局外职责」，必须留在大厅**。DS 只能通过 UDP 与客户端通话；下发 `StartEnterBattle` 与结算改由大厅做 |
| **传输层** | `LZJUDP` 共 9 处：`RegisterBattle` / `Send` ×3 / `ApplyBattleNetSimConfig` ×3 / `ClearBattleNetSimConfig` / `UnregisterBattle` | 全部换成 P3'-1 已就位的 `PMUdpRouter`（DS 宿主独占 socket）。**首版不迁 NetSim 脚手架**（P3'-1 已定的取舍） |
| **日志** | `Logging.Debug` 共 **67 处**（`Battle.cs` 14 / `BattleManage.cs` 14 / `Network.cs` 27 / `Bullets.cs` 12） | `Logging.Debug` 自带文件日志与独占锁处理，在 Unity 里不适用；改为 DS 侧统一的日志入口（沿用 P2'/P3'-1 的做法：走注入回调，DS 宿主决定落盘方式） |
| **protobuf 类型** | `SocketProto`（`BattleFrame` / `AuthoritativePlayerState` / `HitEvent` / `ClientMove` / `MoveAckResult` …） | **零成本**：客户端侧 `Client/Assets/Scripts/Server/SocketProto.cs` 与服务端那份**字节完全一致**（sha256 相同，已实测），同一份仿真代码可以直接编进 Unity |
| **数学** | 移动/命中/扇形/回能 | **零成本**：P3'-3a 已收进 `PMNet.Shared.PMBattleSim`（零依赖，两侧共用） |

###### 名册从哪来（b 必须回答的问题）

服务端的战斗状态完全由**大厅的名册**初始化：`uidToBattlePlayerId` / `playerTeamIds` / `playerHeroes` / `dic_battleReady` 都来自 `BattleContext.MatchUsers`（`Battle.cs:180-185`）。

而客户端上报的 `BattleReady` **只带 `Battleid`(=battlePlayerId) 与 `Id`(=uid)**（`BattleManger.cs:339-342`），**不带 teamId / hero**。所以 DS **无法**只靠客户端自报来还原名册。

**选定方式**：DS 启动时通过**进程参数**接收最小引导信息 —— `battleId` + 名册（uid → battlePlayerId → teamId → hero）。
理由：不新增协议、可离线测试（不需要先把大厅改造完）、与 D6「每局一个进程」一致。
**这一层刻意做成可替换的接口**：P4' 若需要（例如大厅改走控制通道、或按 S6-D12 换成带凭据的名册），只替换「谁填充这份引导信息」，DS 侧不动。

**不改变当前的信任模型**：现在 DS 信客户端自报的 uid（S6-D12 已知问题），b 沿用，纠正属 P4'。

###### 实现方式（模块落点）

| 内容 | 落点 | 说明 |
|---|---|---|
| 权威仿真（编排层） | `Client/Assets/Scripts/Server/Battle/**` | 从 `Server/Server/Battle*.cs` 迁入，保留 partial 拆分（`Battle` / `Network` / `Bullets`），这类拆分本身没问题 |
| DS 引导信息 | 新增轻量 DTO（如 `PMDsBattleBootstrap`） | `battleId` + 名册；零依赖，可被门禁独立编译 |
| 主线程 tick | `PMDsHost` 的 `Update` 内累加器 | 沿用服务端 `BattleLoop` 的累加器语义（固定 16ms 逻辑步、单帧最多补 `maxCatchupFrame` 步），但**由 Unity 帧驱动**、**不加锁** |
| 收包 → 仿真 | `PMUdpRouter` → 主线程分发 → `BattleController.Handle` | P3'-1 已实现的「后台只入队、主线程分发」正好是这条链 |
| 发包 | `PMUdpRouter.Send` | 替换 9 处 `LZJUDP` |
| 大厅 | 保留 `Server/Server/` 其余部分 | 去掉战斗仿真与 `BattleManage` |

**明确不做**（留给后续阶段，避免 b 无限膨胀）：

- 不迁 NetSim 脚手架（P3'-1 已定）。
- 不做「大厅拉起 DS 进程」—— 那是局外改造（**P4'**）。b 只保证 DS **被引导后**能独立跑完一局，并用离线测试证明。
- 不改名册信任模型（S6-D12 → P4'）。
- 不碰 P3'-3a 已单点化的数学。

###### 顺序与风险

| 风险 | 应对 |
|---|---|
| 分批搬迁期间「两边都不完整」 | 按「先建 DS 侧可跑的单局，再摘掉大厅侧」推进；每一步都可编译可跑 |
| 锁消失后是否引入竞态 | 显式确认「仿真状态只被主线程触碰」；收包侧只投递不可变包（P3'-1 已如此） |
| Unity 主线程 tick 与 62.5Hz 的名义帧率不匹配 | 用累加器补帧（沿用既有 `maxCatchupFrame` 语义）；DS 实测 tick 30Hz，即每帧补 2~3 个逻辑步，属既有机制的正常范围 |
| `Logging.Debug` 67 处改动面 | 抽一层薄的日志入口再替换，避免逐处改错 |

#### P3'-3 的前置认识（现在就要记住，避免到时返工）

- **S7 Q7 的 (C) 类最危险**：客户端有 6 处**自行改写权威字段**（乌鸦毒 tick 扣血 `PlayerLogic.cs:135`、回血 `:219-228`、
  护盾 `:167-179`、死亡判定 `:199-203`、血上限跟随 `:99-116`、麦克斯大招给队友加减速 `移动型大招.cs:89`）。
  这些是「两个权威源」的直接来源，必须在合并时要么上移到权威侧、要么明确降级为纯表现。
- **客户端独有、不能被合并掉的**：渲染追赶、动画写入、视觉子弹编排与伪装、瞄准线、相机 Z 锁、
  以及 (B) 类 8 条「预测宽容处理」（历史爆仓、权威陈旧暂停、严重超前降速、重复 correction 去重等）。
- **`BattleFloatMath.cs` 不是定点数**：31 行纯 `float32` + `UnityEngine.Vector3/Mathf`。真正需要单点化的是
  `ToWorldDirection` 的**镜像符号约定**，不是这个文件本身。
- **服务端移动积分极简**：`pos += normalize(input) * teamSign * 3.9 * 0.016 * frameCount`——
  **无重力/摩擦/加速度/碰撞/寻路**，输入被归一化（模拟量大小被丢弃），Y 轴恒为 1.0。
  这意味着「服务端仿真」的合并面比预期小得多。


## 10. 交接记录

| 轮次 | 内容 | 遗留 |
|---|---|---|
| 1 | 完成现状基线调查（4 份模块级报告）；确定 D1-D5 决策；移除 openspec；落本文档 | P0 未开始；Q1-Q6 待定 |
| 2 | **完成 P0**：`Tools/PMNetGen` 外部生成器（含 `--out` / `--check` / `--normalize-eol` 三模式）、`Client/Assets/Scripts/PMNet/` 核心骨架与生成产物、`Tools/PMNetLangCheck`（T1 门禁）、`Tools/PMNetVerify`（T2 对照）、`build.bat` 补强。T1-T5 全部 PASS | Q1/Q2/Q5/Q6 待定；P1 未开始 |
| 3 | **完成 P1**：反射分发表（20 条）、`request_id`、超时/白名单重试、修 B1/B4/B5、服务端链接 PMNet 核心。T6-T9 静态部分 PASS；T6b 需运行环境 | P1b（类型化 payload）与 T6b 待办 |
| 4 | **架构定型**：用户明确「HyldDS = Unity 打包产物，每局一个进程」→ 记为 D6-D9。据此重排阶段为 P2'（DS 骨架）/ P3'（战斗迁移+仿真统一）/ P4'（局外改造）/ P5'（复制与裁剪），并新增 §2.8（决策依据与三条实测证据）与 §3.8（DS 确定形态）。发现并记录：服务端 3351 行战斗代码零 UnityEngine 依赖、两套并行仿真、移速模型分歧（服务端 3.9 恒定 vs 客户端每英雄 3.78~4.2）、服务端配置自称「需人工保持同步」、Unity 2019.4 无 Windows DS 目标 | DS-1（Unity 是否有 Dedicated Server 目标）用户侧无 Unity 可查 → 改为「两条路径都支持」；DS-4（包体）用户明确不关心；P2' 随后开工 |
| 5 | **P2' 代码完成（待 Unity 验证）**：新增纯 C# 判定层（`PMNetLaunchOptions` / `PMNetRuntime`）+ 胶水层（`PMNetBootstrap` / `PMDsHost` / `PMDsBuild`）+ DS 守卫（`HYLDManger` 三处）。新增两道门禁：`Tools/PMNetLaunchCheck`（**94 项检查 0 失败**）与 `Tools/PMUnityGlueCheck`（桩件编译校验，**0 警告 0 错误**，含负向验证），另有 `Tools/check_cs_braces.py`。T27/T31/T32/T33 PASS；T24/T26 部分 PASS。修复计划文档中 3 处由转义符造成的静默损坏（见 §9.5 第 9 条） | T23/T25 与 T24/T26 的运行部分需 Unity；P2' 的连通性回包为占位，待 P3' 换真实协议 |
| 6 | **环境打通 + 数据库移除（D10/D11）**：装 Unity 2019.4.8f1（Hub 登录激活）；修复 17 个 GBK 无 BOM 源文件的编码（其中 1 个阻断编译、3 个会静默损坏英雄名）→ **Unity 首次编译成功**，并首次验证了 P2' 全部胶水代码进程序集（T36 PASS）。按用户决定**彻底移除数据库**：新增 `UserStore` 内存库、重写 `UserData`、去掉 `MySql.Data` 与整套 SQL；服务端 TFM `net6.0→net8.0`（Q5 解决）。顺带修两个真实缺陷：`Console.Read()` 在 stdin 重定向时导致进程秒退（第 13 条）、日志路径硬编码到旧机器（第 16 条）。新增 `Tools/PMServerSmokeTest`（**17/17 PASS**，T34/T35 PASS，T6b 部分完成） | 房间/匹配/邀请与客户端 UI 路径仍 PENDING_USER |
| 7 | **P2' 验收完成（T23/T24/T25 PASS，T26 部分）**：DS 构建成功（真无头包，构建期 `EnableHeadlessMode`），启动 **740 ms**、常驻 **~61 MB**、UDP 往返与 tick 30 Hz 均实测正常 → **DS-2 结论：首版不需要预热进程池**。**实测推翻了初判**：`EnableHeadlessMode` 在 2019.4 + Windows 上可用（§2.8 已更正，D7 已修正，升级 Unity 的动机减弱）。修掉一个真 bug：`DontDestroyOnLoad` 在 `SubsystemRegistration` 阶段无效导致 DS 起来即自毁（第 17 条） | T26 的独立客户端 exe 构建未做；P2' 的连通性回包仍是占位（`PMDS-PONG`），待 P3' 换真实协议 |
| 8 | **P2' 收尾：客户端编译门禁扩到全层**。`Tools/PMClientCheck` 从「3 个文件」扩到**整个客户端玩法层**（~120 文件 / 23k 行，含 `Scripts/Server/**` 战斗层与 `HYLD1.0/Scripts/**` 旧玩法层）。做法是先量依赖面再决定：674 个错误去重后只缺 **42 个类型**（几乎全是 Unity），补齐桩件即 0 错误。原则定为「**桩三方/编一方**」——项目类型一律编真实文件，只桩 Unity 与明确遗留的第三方。**价值有回归验证**（注入 CS1022/CS1501/CS1503 三个历史错误，全被报出） | —— |
| 9 | **P3'-3a 完成（仿真数学单点化）**：新增零依赖的 `Shared/PMBattleSim.cs`（移动方向/多帧积分/权威速度/朝向换轴镜像/扇形/子弹步长与超距/球-点命中/回能），服务端与客户端两侧全部改调它；`BattleFloatMath` 降级为 `Vector3` 薄适配层；删掉 `Battle.cs` 死代码 `UpdatePlayerPositions`（第三份移动公式）与 `ServerVector3.Normalized/Magnitude`（第二份归一化）。**开工前核实出计划的一处过时前提**：移速分歧已在 P3'-2 修掉，不再属于 P3'-3 范围。新增 `Tools/PMBattleSimTest`：**345715 项逐位比对 0 失败**，含一组**结构等价**（分支版 vs 单循环版）；**负向验证 7 项注入全被抓出**。`PMServerSmokeTest` 17/17 仍过 | P3'-3b（权威仿真迁入 DS）、P3'-3c（S7 Q7(C) 6 处客户端改写权威字段）未开始 |
| 10 | **P3'-3c 完成（消除客户端对权威字段的改写）**：用户裁定删除（不迁服务端）、死亡判定保留为纯表现兜底。**删之前先量了可达性**，结论比预期明确：毒/回血/护盾/加速在联机下**都不可达或恒不成立**（`isCanCure` 全项目无人置 true；触发链在碰撞回调里而联机视觉子弹碰撞体被禁用），且它们**一旦执行就会 NRE**（`防护罩` 字段无 prefab 绑定、全资产无名为 `减速` 的物体）→ 从未被真正走过。新增门禁 `check_client_authority_writes.py`（第一次运行就报出 10 处，其中 **3 处 S7 未列**）；暴露一处**真实且联机可达**的违反：`HYLDModenProp` 狂暴瓶本地加血上限/移速，属 P5'，已在门禁里以 `!!` 标注为「已知未修」 | P3'-3b（权威仿真迁入 DS）未开始 |
| 11 | **架构审核与纠偏**：用户明确Mover/预测回滚/投射物机制在范围内。修正目标与非目标、RPC生成API和实施顺序，明确M01–M13模块职责/依赖，新增T37–T47，P3'-3b旧定稿撤销 | 本轮只改计划，未改业务代码/编译/实机验证；RPC归属判断错误等留待R阶段落实 |
| 17 | **R2 声明生成 + 最小复制（A/B 两块落地）**：冻结前置（我写）—— `PMNet/Declarations/PMNetDeclarations.cs`（声明属性 + 描述符 + 零反射注册表）、`PMStableHash.cs`（**生成期与运行期唯一的哈希实现**）、`Tools/PMDeclModel/`（IR + `PMIdLock` 锁文件）；**修正 `PMCond` 与 D-R0-14 的冲突**（P0 是 6 值含 `InitialOnly`/`NetGroup`，契约要求的是另 8 项 ⇒ 重写为 8 项并**沿用 UE 原值** `0/2/3/4/5/8/14/15`）；**修正 R0 契约 §2.3 把 `Dynamic` 错列为「不做」**。A 块（`Tools/PMNetGen/Decl{Scanner,Validation,Emitter}.cs`，Roslyn 4.11）与 B 块（`PMNet/Replication/**`）由 2 个并行子代理完成，各自门禁 60/60 与 129/129（含 3/3 负向注入）。**我随后修掉 A 暴露的契约缺陷**：成员稳定键改以**类稳定键**为前缀（原为 `命名空间.类型名`，导致「钉住类键只保住半个协议」），门禁从 59 升到 60 并新增「钉住类键即钉住整类线协议」断言（成员 ID `57348 → 57348` 不变）；并给 `PMIdLock` 补 3 个只读枚举 API（原靠反解 `Serialize()` 输出）。全量回归绿 | 端到端总门禁未做（T40/T41 仍未收口）；§5.4 的 8 项 R2 待收口缺陷 |
| 16 | **M04 对象运行时完成（T39 推进）**：新增 `PMNet/World/` 三文件 —— `PMNetObjectLifecycle.cs`（生命周期记录 + **保序**批量编解码，含版本/上限/尾部/截断的完整拒绝面）、`PMNetWorld.cs`（对象登记、NetId 单调不复用、Create/Destroy 走可靠域、每连接待发与全量补齐、创建与初始状态原子到达、初始状态失败回滚）、`PMNetOwnerChain.cs`（`PMNetControllerObject` / `PMNetPawnObject`，逐字对齐 UE 三处覆写，含控制器**不回退** Owner 链的不对称规则）；`PMNetObject.cs` 增补 Owner 链、生命周期钩子、`NetId`/`ClassId`/`ArchetypeId`/`State`；`PMNetReader.cs` 增补 `ReadRawBytesCopy`。门禁 `Tools/PMNetWorldTest`：**152 项 0 失败**；**7 个负向注入 6 中 1 漏**，漏的那个暴露了相关性分支的测试盲区（§5.3）。全量回归绿 | 仅剩「认证连接」依赖 R3；M04 未覆盖客户端上行路径与完整相关性（属 M06/R3） |
| 15 | **M03 可靠传输完成（T38 PASS）**：新增 `PMNet/Transport/` 三文件 —— 三个固定顺序域、可靠序号/去重/保序、ack 位图 + NAK + **发送端丢包推断**、纯 ack 包、分片重组（有界 + TTL）、背压、会话世代拒收、幂等断开、协议健壮性。门禁 `Tools/PMTransportTest`（可控丢包/乱序/重复链路）：**47 项 0 失败**。实现中由断言抓出并修正 **4 处缺陷**（位图 off-by-one、缺发送端推断、缺纯 ack 包、分片偏移不同源），详见 §5.2 | M04 对象运行时未开始；T39 的「伪造 Owner / 旧世代包 / 跨通道乱序」仍待 M04；MTU 等参数待实测收敛 |
| 14 | **R1 首轮收口 + 独立审查**：三路独立只读审查确认 **6 处真实缺陷**（位值反、`#14-②` 缺守卫、全局判定三处偏离且零覆盖、便捷重载回归、分配器注释错且未挡跨线程、帧缺比较运算符），全部修正；门禁从 67 项扩到 **93 项 0 失败**。契约文档同步修 5 处（见 §5.1） | M03 可靠传输、M04 对象运行时未开始；T38 全未执行 |
| 13 | **R1 起步（M05 + M01）**：`PMRpc.cs` 确定性地整份重写 —— 按 UE `AActor::GetFunctionCallspace` 实现 **16 分支真值表**（含 `else-if` 结构与分支顺序契约），并**修掉归属校验反向缺陷**（旧实现「服务端直接放行」，等于取消全部归属校验，D-R0-42）。新增 `PMNetIdentity.cs`（`PMNetId`/`PMNetIdAllocator`/`PMSession`/`PMFrameDomain`/`PMFrameId`/`PMTimeStep`）；`PMNetRole.cs` 补 `PMNetRoles` 显式谓词 + `ListenServer`。新门禁 `Tools/PMCallspaceCheck`：**67 项 0 失败**（逐分支 + 场景表 + 归属回归 + 身份/帧不变量）。过程中被门禁抓出并修正**我自己**的两处偏差：① 把 UE 的 `else-if` 写成并列 `if`（会在双向 Remote 时提前返回、跳过后面的权威可达性判定）；② 便捷重载把 `isOwner` 误当「owning player 在本机」，导致 DS 调 Client RPC 误判为 Local | M03 可靠传输、M04 对象运行时未开始；T39 的「伪造 Owner/旧世代/跨通道乱序」需 M04 落地后才能测 |
| 12 | **R0 完成（T37 PASS）**：派发 4 个并行 explore 冻结 UE 侧契约（Mover/NetworkPrediction、RPC、复制与生命周期、投射物），产出 4 份证据附录（合计约 400KB，含 `文件:行号` 与 Unity 契约项清单）；主 Agent 整合为 `Docs/plans/net-r0-contract.md`（**50 条冻结决策 D-R0-01..50**、13 模块来源映射、M01 API 契约、8 项有意裁剪、9 项待收敛参数）。**抽查 3 项高风险结论全部通过**，其中一项**证实现有 `PMRpcDispatch.ShouldCallRemoteFunction` 服务端分支写反**（D-R0-42） | R1 未开始；R0 的 9 项参数待 R1/R2 实测收敛 |
| | **下一轮边界**：实施 R1（顺序域/可靠传输/对象运行时 + 修 RPC 归属判断）与 R2（外部生成器扫描声明 + 最小复制闭环）；不执行已撤销的 P3'-3b 旧方案 | |


### R2 独立审查后返工（当前状态：针对性门禁通过，替代此前“完成”口径）

用户已授权由子 Agent 修复本轮 review。证据见 `_r2_review_rpc.md`、`_r2_review_replication.md`。
冻结边界：RPC 组负责生成器、声明运行时及 PMNetE2E；复制组负责 Replication、World、PMNetObject/Property 及独立复制/世界测试。不得修改对方文件；共享描述符现有字段/委托签名保持不变，允许兼容性新增入口。主 Agent 负责集成复核和本计划/契约/BothSide 更新。
决策：可靠 RPC 不得超限淘汰已接受调用；未接线的本地队列超限显式失败。初始状态必须按接收连接过滤复制条件，不能为原子创建泄露 OwnerOnly/Never 值。真实 UDP 与 Unity 宿主仍属 R3 接线，不把内存投递验收宣传成真实网络验收。

| ID | 实施映射 | 场景 | 执行者 | 方法/命令 | 预期结果 | 状态 | 证据 |
|---|---|---|---|---|---|---|---|
| RV1 | 复制编解码 | slot 7/8/31/32/63/64/255 | 子 Agent + 主 Agent | PMReplicationTest 本次构建后运行 | 位序往返一致，高位属性到达 | PASS | PMReplicationTest J/N，256位往返与通道投递 |
| RV2 | 接收应用 | 非法槽位/ID/截断值 | 子 Agent + 主 Agent | 真实 OnMessage 投递 | 不部分应用、不 ACK 失败记录 | PASS | PMReplicationTest L/O/P/Q/R，纯字段原子应用；自定义Reader限制见下 |
| RV3 | 生命周期 | Create 回调初值、条件过滤、销毁及回滚 | 子 Agent + 主 Agent | PMNetWorldTest + 生成对象 E2E | 回调前值齐全，活对象有界 | PASS | PMNetWorldTest H/I；PMNetE2E Y0–Y18 创建回调快照 |
| RV4 | 脏位 | MarkAll/休眠唤醒后收敛 | 子 Agent + 主 Agent | PMReplicationTest | HasAny 清零，不持续空扫 | PASS | PMReplicationTest N，HasAny与高位清理 |
| RV5 | RPC 参数/队列 | 数组调用后修改、满队列 | 子 Agent + 主 Agent | PMNetE2E | 快照不变，超限显式失败不丢旧调用 | PASS | PMNetE2E O2b/M，数组快照及超限失败 |
| RV6 | RPC 接收 | 非 Owner/已销毁/方向错误/Validate false/畸形包 | 子 Agent + 主 Agent | 带连接的实际派发入口 | 未授权不执行，失败校验请求断连 | PASS | PMNetE2E R/V/X/K，带连接投递与类内RPC键 |
| RV7 | 集成 | 生成、C#7.3/netstandard2.0、既有门禁 | 主 Agent | build 成功后串行 run | 相关门禁通过，Unity 实机仍未验 | PASS | 本次逐项build exit=0后run exit=0，Tools各工程review-verification.log |

本次主 Agent 重建并运行：PMDeclCheck 71/0、PMReplicationTest 265/0（5/5缺陷注入）、PMNetWorldTest 197/0、PMNetE2E 209/0、PMCallspaceCheck 93/0、PMTransportTest 47/0、PMNetVerify 27/0；PMNetLangCheck/PMClientCheck/PMUnityGlueCheck 0错误。所有命令按实际退出码判断，未用grep错误计数代替build成功。

本节替代 §5.4–5.6 中过时描述：脏位已扩至256位；RPC队列满时拒绝新调用并抛异常、RejectedPendingRpcs计数，**不再丢最旧**；数组调用点在本地实现前快照；RPC注册键为(ClassId,RpcId)，接收按目标ClassId查表。§5.6的旧丢弃策略仅作历史记录，不得继续实施。

修复报告：`_r2_fix_rpc.md`、`_r2_fix_replication.md`。主Agent额外核对注册表独立RegisterRpc后再RegisterClass的冲突，在写入类表前拒绝以保持失败原子性。

明确保留的边界：
- 真实UDP/Unity宿主与握手未接线；Validate失败已验证向连接接口发断连请求，真实PMTransport断连由R3宿主实现。
- 生命周期格式升到2，Create带声明式初值并按连接过滤；首次Tick前创建回调已有正确值。无条件过滤器时仅None可发；正式宿主需先接好复制channel。Create不乐观推进ACK基线，随后保守全量更新仍可能重复传初值。
- 更新原子性适用于生成的纯字段Reader。带业务副作用或仅在活对象抛异常的自定义Reader/属性setter可能部分提交：明确计CommitFailures、不ACK、不派发OnRep，未声称回滚保证。暂存解码有额外对象分配和二次解码成本，尚未量化性能。
- OnRep在整条提交后统一执行；表现回调异常单独记录并继续其它通知，已提交记录仍ACK。
- 跨程序集/继承描述符、值比较委托、MTU字节预算和校验和仍按原缺口登记，未在本轮扩大实现。
- 两组任务首轮实际文件PMRepConnectionState.cs及生成E2eScoreboard.g.cs与派发清单存在路径名称偏差；第二轮已按真实路径明确边界，未创建PMReplicationState.cs或E2eAuxiliary.g.cs。此处记录调度边界问题，不作“始终无越界”的错误声明。


### R3 开工审查与分步计划（当前：R3-A针对性验收通过，R3-B待接线）

审查结论：方向和依赖正确，可以启动R3-A；原R3标题不足以直接实施，已冻结`net-r3-control-contract.md`。R2残留继承/自定义setter/性能优化不阻塞首批直接继承+纯字段对象；认证身份、会话世代、摘要校验、发送预算为R3接线硬条件。

- R3-A1：共享控制协议、票据、Lobby进程协调器；独立状态机与真实回环控制消息测试。
- R3-A2：PMTransportConnection/PMNetSessionBridge，把现有Transport/World/RPC/Replication接成认证后的字节闭环；测试真实实现双端投递、弱网、错误方向/世代/摘要。
- R3-B：基于A产物接Lobby匹配、DS控制Agent/PMDsHost、Unity权威场景就绪、客户端切服、结果确认后退出。T42真实Unity两客户端仍PENDING；A阶段不得当作整个R3完成。

| ID | 实施映射 | 场景 | 执行者 | 方法/命令 | 预期结果 | 状态 | 证据 |
|---|---|---|---|---|---|---|---|
| R3A1 | 控制协议 | 编解码/超限/非法票据/过期/篡改 | 子Agent+主Agent | PMDsControlTest build后run | 严格有界、票据绑定对局和身份 | PASS（限定范围） | PMDsControlTest 473项总门禁，含真实TCP分帧与HMAC测试 |
| R3A2 | Lobby编排 | 启动失败/Ready/崩溃/重发结果/重复结果/退出 | 子Agent+主Agent | PMDsControlTest，受控进程接口 | 无未就绪地址、幂等、释放端口名册 | PASS（限定范围） | 同上；受控进程状态机+真实测试子进程，不是UnityDS |
| R3A3 | 会话接线 | 认证后Create/RPC/属性/ACK/断连 | 子Agent+主Agent | PMNetSessionTest build后run | 真实PMTransport承载、无伪造Owner | PASS（限定范围） | PMNetSessionTest 343项；可控数据报链路，非UDP socket |
| R3A4 | 边界 | 摘要/世代错误、未知来源、消息超限、弱网 | 子Agent+主Agent | PMNetSessionTest故障链路 | 未认证不派发、可靠事件保序、属性收敛 | PASS（限定范围） | 同上；合法上行Update拒绝、严格预算32、长度上限与连接清理 |
| R3A5 | 兼容 | C#7.3/netstandard2.0与既有回归 | 主Agent | PMNetLangCheck + 相关门禁 | 本次build0后run0 | PASS（限定范围） | 主Agent逐项build0/run0；Tools各工程r3a-verification.log |
| R3B1 | 真实全链路 | Lobby→UnityDS→两客户端→结果→退出 | AI+用户 | 完成R3-B后执行T42 | 真进程/场景/两端证据齐全 | PENDING | |

首批写入边界分离：A1拥有Control协议及Server/DS协调器，A2拥有Session连接适配与其测试；双方均不修改R2生成器/核心既有文件。字段契约和现有纯C# API是共享只读输入。后续宿主接线消费两者的实际API，是第二阶段顺序依赖。


R3-A交接记录：主Agent审计划后先冻结`net-r3-control-contract.md`，两组子Agent交付Control/Coordinator/Process和Session适配。独立审查再修：客户端伪造合法Replication Update、两次Update预算63而非32、生命周期超限静默丢弃、死链路激活、进程未退出即释放端口、发送次数伪造ResultAcked、32位结果摘要碰撞、Exited/Error非法状态。

主侧实际验证（全部本次构建退出0后运行退出0）：PMDsControlTest 473/0（50负向输入），PMNetSessionTest 343/0，PMTransportTest 81/0，PMNetWorldTest 197/0，PMReplicationTest 265/0，PMNetE2E 209/0；PMNetLangCheck/PMClientCheck/PMUnityGlueCheck构建退出0。非空路径计数与生产缺陷注入证据见`_r3a_control_report.md`、`_r3a_session_report.md`，独立审查报告为同前缀review文件。

当前准确边界：
- ResultCommitted只表示本地受理/停止主动重发，不表示DS收到ResultAck；PeerExitObserved才是对端收尾观测。资源释放必须确认实际进程退出；Kill失败节流重试并保持资源占用。
- 完整结果winner+summary比较用于幂等，32位hash仅诊断。无数据库，不承诺Lobby重启后恰好一次。
- M03直接Send已拒绝>65535字节/非法窗口/超过分片上限；新增Update(now,sendBudget)使两次调用共享单帧预算。生命周期大批次目前超限断连（明确失败），仍需M04字节切批以支持大世界正常工作。
- R3-B前置：票据指纹→真实端点消费账本、Lobby共享uid占用/端口池、bootstrap文件原子发布和本机权限；未激活数据报必须在进入Transport前拒绝以免先ACK后丢业务；复制连接移除的ACK队列清理与Multicast viewer相关性仍需收口。不能把单独TryActivate当票据认证完成。
- 真UnityDS/真实场景、旧匹配入口切换、两客户端及结果确认退出未做，T42保持PENDING。首批只接直接继承+纯字段网络对象。
- 本轮事故：子Agent故障注入曾把PMTransportTest/Program.cs误截断，从历史工具write/edit日志恢复原47条断言，并与上一轮review-verification.log逐条同名同结果核对；随后新增边界测试至81。子Agent报告仍差6行/351字节注释空白，无法逐字节还原，不把恢复称作字节完全一致。当前主侧build/run81/0。细节见session报告§8。


### R3-B调研与开工（当前：代码与回环整链PASS，真实Unity待验）

只读三路调研完成：`_r3b_lobby_survey.md`、`_r3b_host_survey.md`、`_r3b_auth_survey.md`。主Agent采纳入口证据，但纠正调查建议：不新增第二旧诊断UDP端口；bootstrap选择新链独占分配端口；不在Transport内握手；票据过期不终止已激活会话；不加载随机旧地图；运行时程序化构建确定测试碰撞场景，无需手写Unity场景资产。
接口与字段已冻结在R3契约§7。三组并行：网络组实现EntryOffer/握手/UDP端点，Lobby组实现控制listener/全局账本/启动与匹配分流，Unity组实现PMR3声明产物/宿主/客户端入口/参数。共享API是只读契约，跨组文件不重叠；实际集成构建由主Agent收口。

| ID | 场景 | 执行者 | 方法 | 预期 | 状态 |
|---|---|---|---|---|---|
| R3B2 | 票据端点绑定/重试/过期/第二端点/未激活ACK | AI | PMUdpAdmissionTest真实loopback UDP | 验证后才激活，不泄露/不重复连接 | PASS（PMUdpAdmissionTest 360/0） |
| R3B3 | 控制listener/两局端口/uid占用/启动文件参数 | AI | PMDsLobbyTest | 半粘包/MAC/失败回收正确 | PASS（PMDsLobbyTest 230/0 + PMDsControlTest 506/0） |
| R3B4 | 声明对象+Unity胶水/参数/客户端分流 | AI | PMR3RuntimeTest+PMNetLaunchCheck+PMClientCheck/PMUnityGlueCheck | 生成桩/实际核心路径、构建成功 | PASS（PMR3RuntimeTest 134/0 + Launch133/0；桩编译通过） |
| R3B5 | Lobby/DS控制+真实UDP两客户端集成 | AI | 有界测试宿主(非Unity) | 一局单权威、Probe/Echo/初值/结果幂等/退出 | PASS（PMR3IntegrationTest 143/0，真实TCP/UDP，进程替身） |
| T42 | 真实UnityDS场景与两客户端 | AI+用户 | Unity2019编辑器内构建后真实进程验证 | 场景/地址/票据/结算退出齐全 | PENDING_USER（编辑器运行中，不抢锁） |


R3-B本轮交付：三路调研后冻结§7，网络/Lobby/Unity三组并行实施，随后做双客户端集成与UDP入口复核。当前生产接线已在代码中：HYLD_PMNET_DS=1选择新匹配路径、Lobby原子发布bootstrap并启动固定DS exe、Ready后PMDS1入局通知、真实UDP验票/端点绑定、PMR3生成RPC与属性、控制结果确认/退出。默认旧匹配路径保持，选新后失败不回旧链。真实玩法仍R4–R6，当前只用固定测试碰撞场景和Probe/Echo。

主Agent最终工作树验证（所有本次build exit0后run exit0）：PMNetSessionTest405、PMTransportTest173、PMUdpAdmissionTest360、PMDsControlTest506、PMDsLobbyTest230、PMR3RuntimeTest134、PMR3IntegrationTest143、PMNetLaunchCheck133、PMNetE2E209项，失败均0。PMNetLangCheck/PMClientCheck/PMUnityGlueCheck构建通过；Server主工程编到独立临时输出，0错误（既有警告保留），未覆盖运行中服务产物。日志各Tools工程r3b-verification.log。

修复与复核追加：
- 端点消费墓碑满时保留拒绝盲窗，不能遗忘仍有效旧票重放保护；对外TicketDigest为副本，不暴露字典键数组。
- 未激活字节在Transport前拒绝、不ACK；旧世代覆盖仍由已激活连接验证。畸形Transport短包已加核心长度守卫，会话层兜底仍保留。
- ResultPending已受理结果+经MAC/身份校验的显式Exited(0)为正常收尾；纯进程消失仍Failed。资源只在本地确认退出后释放。
- KeepAliveIntervalMs默认1000ms（0禁用），使用既有ControlPing并共享数据报预算；首包前无响应也10s超时；时钟倒退复位基准。不新增RTO。纯ACK包消费ack与跟踪包号但不要求再次ACK，空闲12s两端合计2200降到44。keepalive禁用+完全静默丢唯一可靠包不能保证主动恢复，生产默认启用。
- PMNetSessionTest弱网H组改用生产keepalive默认并等待至少两周期，不再依赖错误ACK循环提供流量；没有删掉旧世代/可靠一次交付覆盖。

尚待：T42真实Unity2019构建与场景/UI/Application.Quit验证（编辑器运行中未关闭/未抢锁）；跨机MTU/NAT；大生命周期批次字节切分(目前超限明确断连)；复制ACK队列摘除/Multicast实际viewer；继承/有副作用setter边界。首版票据UDP明文bearer仅防伪造和跨端点消费，不宣称密码学DS认证/中间人防护。

报告：_r3b_network_report.md、_r3b_lobby_report.md、_r3b_unity_report.md、_r3b_integration_report.md、_r3b_network_review.md、_r3b_transport_liveness.md。报告内早期测试失败数字为历史，最终以本节主Agent验证为准。


### 用户新构建后的真实DS验收与控制保活修复

用户已构建：HyldDS_Data/Managed/Assembly-CSharp.dll 2026-09-20 12:12:46，1028096字节，含新宿主/UDP/PMR3/LobbyAgent。新增PMR3UnitySmoke真实白名单进程启动+真实控制TCP/UDP两名纯C#客户端：122/0，DS场景Collider就绪、Probe/Echo/复制、结果Ack、显式Exited、真实OS exit0与端口回收都已取得日志证据，见`_r3b_real_unity_smoke.md`及Tools/PMR3UnitySmoke/artifacts。客户端非UnityUI，不能把完整T42标PASS。

真实试跑同时暴露长局缺陷：DS Ready后无下行30s自退；原DS心跳间隔15s恰等Lobby超时15s亦有误判窗口。已修源码：既有Heartbeat双向，DS每5s发送，Lobby认证/状态校验后应答且最小1s节流；DS首响应30s、运行期15s只由可信下行续命，发送成功不续命，无回声循环。真实构建尚不含此修复，不能拿其122/0宣传长局已通过；需要重新Unity构建再实测。

主Agent本次重建后实际run：PMDsControlTest556/0、PMR3RuntimeTest171/0、PMDsLobbyTest230/0、PMR3IntegrationTest143/0；日志对应control-liveness-verification.log。65s虚拟长局/真实TCP续命、丢下行15s失败、坏MAC不续命和节流负向均有证据。报告`_r3b_control_liveness_fix.md`。

子Agent超出自身清单修改PMDsLobbyTest两处按消息类型等待ResultAck（新增合法Heartbeat可能先到），主Agent已接受该必要集成改动并重建验证；未执行报告建议的git checkout，禁止回退用户工作。

当前下一步：用户再次Build HyldDS以纳入控制心跳修复；随后真实DS延迟Probe超过30s再完成闭环验证，客户端UnityUI/跨机仍待。不提交代码。


### R3正常退出收口与R4-A开工（持续授权）

用户授权无需每阶段重复确认。真实13:02DS包已验证40s正常存活/控制失联15s退出；正常Exited0后Lobby立即Kill竞态仅Server侧已修为默认5s非阻塞优雅退出宽限。子Agent真实40s验收140/0，osExit0、coordinatorKillRequests0，无需重DS包；Control623/0、Lobby268/0、Integration143/0。证据_r3b_graceful_exit_fix.md，T42客户端UnityUI仍待验。

R4两路前置调研已完成，接口展开见net-r4-prediction-contract.md，源证据_r4_prediction_survey.md/_r4_mover_survey.md。调查草案中复制已有帧类型、缺拒绝裁决却声称回滚自然撤销、把环境说成无Unity等表述不采纳。实际主Agent裁决：复用PMNetIdentity；Y-up米；原始整ms步1..50(本项目首批限制)；历史128；DS8步/100ms+墙钟信用；SP首批只插值；完整行为Modifier/效果拒绝裁决留R4-B。

共享只读接口已落PMPrediction/PMPredictionContracts.cs。预测组只改泛型timeline/authority budget/SP及其测试；Mover组只依赖该共享接口实现纯模型/碰撞测试环境，两组不依赖对方中间结果。R4-A不改R3宿主、旧SavedMove或R5投射物。

| ID | 场景 | 方法 | 预期 | 状态 |
|---|---|---|---|---|
| P4A1 | AP历史/同帧校正/重放/确认事件/SP | PMPredictionTest | 克隆隔离、旧错帧拒绝、完整恢复、事件不重放 | PASS（PMPredictionTest 417/0） |
| P4A2 | DS输入预算/缺帧/重复 | PMPredictionTest | 信用有界、不合并放大dt、缺失显式resync | PASS（PMPredictionTest 417/0，预算/缺帧/信用） |
| P4A3 | Mover模式/碰撞/效果/层 | PMMoverTest | 纯状态可恢复、手算oracle、无墙钟外部副作用 | PASS（PMMoverTest 245/0） |
| P4A4 | 真实预测核心+Mover模型 | PMMoverPredictionTest | 强制全状态差异后收敛 | PASS（PMMoverPredictionTest 264/0，初版235/3已返工） |
| T43/T44 | 真网络角色/Unity物理 | R4-B后实测 | 主从角色预测校正与碰撞证据 | PENDING |


### R4-A首批交付与主侧验收

用户持续授权已落实。预测与Mover两组基于共享Contracts并行实现，集成消费实际API后发现冻结期快照仍推进确认、校正边界预测事件冒充权威两处问题，均已修并补生产缺陷注入。
主Agent最终本次build0/run0：PMPredictionTest417/0、PMMoverTest245/0、PMMoverPredictionTest264/0；PMPredictionCoreCheck/PMMoverCoreCheck均netstandard2.0+C#7.3构建0错误。R3收尾回归Control623/0、Lobby268/0、Integration143/0。日志Tools各工程r4a-verification.log。

冻结语义修订：ApplyAuthority在Frozen/NeedsResync返回Stalled且零状态/事件改动，仅可信Resync恢复；Snapshot可携带ConfirmedEvents或按边界ConfirmedEventFrames，缺证据不广播、空数组表示确定无事件；每帧256/批256边界/总4096事件上限，深clone，重放后按确认区间广播。权威可靠事件补发网络通道尚未接，不能声称不丢事件的网络保证完成。

R4-A范围：纯泛型时间轴/DS预算/SP插值 + Y-up四基础模式/纯状态Effect/两种LayeredMove/测试floor-wall几何。88条变dt输入、89边界、墙前/落地/Mode帧校正和六类状态差异均通过真实模型+真实timeline集成；不是Unity PhysX等价或实机网络预测证据。Modifier完整补偿/效果拒绝裁决/斜坡台阶平台等仍未实现，不用测试计数替代。

下一步R4-B：先冻结Input/AuthoritySnapshot/事件证据线编码和生成RPC/复制承载，再接R3场景的Unity碰撞query、AP输入、DS逐实例预算、SP表现与重同步。接口复用本批Contracts，禁止业务手写第二套时钟/身份，不动旧SavedMove直到新角色闭环通过。T42的真实UnityUI分流与跨机项仍未完成；R3真实DS40s140/0、控制失联15s退出已有证据。
报告：_r4a_prediction_report.md、_r4a_mover_report.md、_r4a_integration_report.md。R3退出修复报告_r3b_graceful_exit_fix.md(仅Lobby改，无需重DS包)。


### R4-B实施计划（本轮授权，进行中）

目标：把R4-A接入真实PMNet声明RPC/复制与R3认证会话，Unity主线程驱动AP/DS/SP。细节只读接口见net-r4-network-contract.md；本节为状态唯一事实源。
步骤：B1网络编码/声明承载/每对象驱动；B2 Unity碰撞/输入/表现（与B1并行）；B3消费两组产物接R3宿主、编译既有消费者；B4独立复核与整链回归。首批测试地板/墙、原始轴与跳跃；完整行为Modifier与技能激活裁决、R5投射物、R6旧业务退役不在本次接线里冒充完成。

| ID | 实施映射 | 场景 | 执行者 | 方法/命令 | 预期结果 | 状态 | 证据 |
|---|---|---|---|---|---|---|---|
| P4B1 | B1 | 输入/完整快照/事件编码及非法值 | AI | PMR4NetworkTest build后run | 全字段往返、上限/NaN/尾部/身份拒绝、无部分应用 | PASS（codec与网络门禁389/0） | |
| P4B2 | B1 | AP→DS→AP/SP真实Transport与生成桩 | AI | PMR4NetworkTest build后run | 输入重发去重、预算防加速、完整回滚、属性最终收敛、事件不漏不重 | PASS（真实Transport字节链389/0） | |
| P4B3 | B2 | Unity胶囊查询/地面/输入边沿/表现 | AI | PMR4UnityCheck构建真实2019程序集；适配测试 | API真实可编；主线程/有界查询/只Finalize写Transform | PASS（真实Unity2019 API编译；输入64/0；PhysX运行归P4B6） | |
| P4B4 | B3 | R3宿主接线与两客户端UDP | AI | PMR4IntegrationTest build后run，相关旧门禁 | 所有副本AP/SP注册，DS主线程模拟，断线冻结/清理，重同步不接受旧流 | PASS（真实回环UDP113/0；Unity宿主运行归P4B6） | |
| P4B5 | B4 | 对抗审查/生产缺陷注入 | AI | 独立审查+安全备份的负向测试 | 确認Owner/快照/事件乱序/重同步窗口，注入可被抓且字节恢复 | PASS（独立核心/宿主/物理审查及缺陷注入；运行限制见P4B6） | |
| P4B6 | B3/B4 | Unity真实DS+两客户端角色/墙/跳跃 | 用户+AI | 同版本Unity2019重新Build，客户端进入PMDS1测试局 | AP即时移动、DS不穿墙、SP插值、弱网校正，无旧链并发 | PENDING_USER | 不抢编辑器锁，纯C#门禁不能替代PhysX运行 |

### R4-B接线交付与取消后恢复验收

B1网络适配、B2 Unity适配、B3宿主接线已落盘，P4B1–P4B5在限定自动化范围PASS；P4B6真实PhysX/Unity DS双客户端仍PENDING_USER。T43/T44整体未闭合，完整行为Modifier/技能预测拒绝裁决与R5/R6仍待。
新增PMR4MovementCodec/Driver，PMR3Player声明输入RPC/快照属性/可靠事件与重同步；AP有限输入重发/DS预算/SP时间插值，流代次隔离重同步旧输入。修复非零边界构造、SP新流停摆、可靠事件与重同步消费乱序、codec重复字段门失效、回调异常重播。
实际宿主接线在PMDsSessionHost/PMClientSessionHost。最终审查后修：AP不得伪造DS帧（读PendingServerFrame免clone）、零步帧仍PollHardware、端点IsDisposed可观测并失败冻结、身份异常不可把AP当SP驱动、启动失败清理、多AP拒绝。测试局改用LocalPhysicsMode.Physics3D独立物理世界，硬校验非默认PhysicsScene，避免大厅同层碰撞体先填满查询缓冲；白名单/饱和失败门保留。
用户更换委派配置后先读两份取消checkpoint并核盘：物理组仅加using；元数据组4文件已完成且注入恢复hash一致。重新派发续做物理隔离/核验已有元数据，没有重写已验证实现。
主Agent当前源码build0后run0：PMPredictionTest490、PMUdpAdmissionTest382、PMR4NetworkTest389、PMR4IntegrationTest113；PMR4UnityCheck(真实2019DLL)/PMClientCheck/PMUnityGlueCheck构建0错误。日志Tools各工程r4b-resume-verification.log。输入适配此前主侧64/0。真实UDP门禁使用纯C#测试宿主+AABB碰撞，不能替代Unity生产宿主/PhysX运行。
报告：_r4b_network_report.md、_r4b_unity_report.md、_r4b_core_review_fix.md、_r4b_host_integration_report.md、_r4b_host_final_review.md、_r4b_physics_final_review.md、_r4b_scene_isolation_fix.md、_r4b_metadata_endpoint_fix.md。前期报告的未修项/数字以本节及最后修复报告覆盖。
用户验证：Unity2019编译完成后在Play模式点Tools/PMR4/验证 Unity 碰撞适配（真实 PhysX），检查独立世界/墙/跳跃落地/40干扰体隔离通过；再Build/Build HyldDS (Windows Headless)。Lobby与客户端须同版本（协议摘要已变），HYLD_PMNET_DS=1进入新链，两个客户端WASD/Space验证各1AP+1SP、墙阻挡/校正/断线冻结。旧DS包不能验证本轮。未关闭编辑器、未抢锁、未编译UE、未提交。


### P4B6首次真实PhysX失败与修复待复验

用户在大厅Play执行验证菜单，独立物理场景建立成功，实际PASS16/FAIL6。失败为墙扫掠fraction0、起点重叠路径未执行、白名单距离、默认世界隔离扫掠、行走到墙、跳跃落地。截图与Editor.log证据见_r4b_physx_zero_hit_fix.md；这是适配器缺陷，不是用户选择大厅错误。
修复PMUnityMoverCollisionQuery：零距离cast不采信原始反运动方向法线，按几何区分正穿透/仅接触/有效前向TOI，保留向内阻挡、向外与切向可移动。新增受控查询返回+真实适配器/PMMoverModel门禁PMR4CollisionAdapterTest，旧逻辑等价变体17项失败，修复后38/0；主Agent本次build0后run38/0并PMR4UnityCheck真实2019API构建0错误。Editor菜单新增8条窄回归和原始命中诊断，原墙/隔离判据未放宽。
P4B6当前状态：首次实机FAIL已修源码，等待用户复验（PENDING_USER），不得标PASS。退出Play等待脚本编译，重新大厅Play后执行同一菜单，完整通过再Build HyldDS；模型/查询替身不能证明真实PhysX复验。任意曲面/旋转体精确MTD仍未实现，当前边界是冻结floor/wall测试场景，不宣称通用碰撞与绝不穿墙保证。


### R4-B真实DS已验与R4-C正式内容接入开工

用户报告物理菜单复验通过并打包；不推定具体通过条数。19:16:14 DS程序集1170432字节sha256 8abc8dacf1ae014e4b16ebf04fd1c0c479ecaa59241b6ea0232e544de06747da。真实UnityDS --movement232/0（报告_r4b_real_unity_movement.md），撞墙x=-.901、跳高1.783808/回地y1、可靠Mode/Landed3事件、SP复制与升流旧输入拒绝、OSexit0/Kill0/资源回收；默认128/0、drop89/0。客户端仍纯C#，UnityUI双客户端/跨机未验。
用户授权继续正式地图角色接入。只读两路_r4c_scene_survey/_r4c_actor_survey：联机实际HYLDGame+map2运行生成，无seed播种；直接加载会启动旧BattleManger/UDP；角色Remake/Player含旧移动脚本/missing script。冻结net-r4c-content-contract.md：先C1构建期固定布局地图/干净表现烘焙，C2运行资源适配并行，再C3显式会话模式/宿主集成；首版不擅自替换map4，不运行两套权威。
| ID | 实施映射 | 场景 | 执行者 | 方法/命令 | 预期结果 | 状态 | 证据 |
|---|---|---|---|---|---|---|---|
| P4C1 | C1 | 正式地图/角色烘焙 | AI+用户 | PMBattleContentBuildCheck真实API编译+Editor Prepare菜单 | 同源确定资源、原资产不改、旧脚本0 | PENDING | **代码+真实API编译完成**：PMBattleContentBuild菜单/本地seed复刻map2/干净角色提取；独立复核修5处真实缺陷（含阻断级审计判据）。资源生成未执行，PENDING_USER |
| P4C2 | C2 | 运行资源与manifest | AI | PMBattleContentRuntimeCheck及规则门禁 | 版本摘要一致、资源缺失明确失败 | PENDING | **代码+真实API编译完成**：manifest强校验/地图隔离场景/出生查询/角色表现；ManifestTest92/0 |
| P4C3 | C3 | 正式模式宿主接线 | AI | 真实API编译+会话模式门禁 | 同map摘要、就绪前地图加载、无旧权威驱动 | PENDING | **代码完成**：digest显式选模式(0拒/保留诊断/正式)、Lobby读manifest、Ready前地图+出生校验、名册定槽与英雄移速；SessionTest86/0 |
| P4C4 | C3 | 真实地图两客户端 | 用户+AI | 同构建资源启动DS/两个Unity客户端 | 出生无嵌墙、障碍/移动/表现/退出正确 | PENDING_USER | 正式资源生成与两客户端仍PENDING_USER|

### R4-C正式内容接入交付与用户首次生成步骤

C1/C2/C3代码与真实Unity2019 API编译已交付，独立复核_P4C1/C2并修F1–F5（阻断级：编辑模式脚本审计把ugui Graphic等当违规⇒烘焙恒失败；数组长度拷贝不可达导致丢失材质；脏源场景未拒；失败未自证清除manifest；审计晚于OpenScene）。索引只作修前修后对照，不是当前状态表。
主Agent本次build0后run0：PMBattleContentBuildCheck/RuntimeCheck真实2019DLL 0错误；ManifestTest92、SessionTest86、PMR4IntegrationTest113、PMClientCheck/PMUnityGlueCheck/PMR4UnityCheck 0错误；日志Tools各工程r4c-main-verification.log。PMDsBuild已接构建前PrepareForBuild，缺产物中止构建。
未闭合（诚实）：正式Resources/PMNet三产物尚未生成；正式地图/角色未在Unity实机加载；玩家碰撞白名单4096与适配器32命中缓冲容量口径不一致仍是登记风险；PMHandshake仍把digest=0当跳过（不在本轮边界）。测试局地图/胶囊诊断仍保留，正式局才走正式地图与角色表现。

用户首次执行（Unity2019，必须先于打新DS包）：
1 编辑器加载工程等编译，菜单Build/Prepare PMNet Battle Content生成三个Resources产物（真实YAML不手工编辑），失败看Console具体原因；成功后可ValidateContent自检。
2 Play模式再点Tools/PMR4/验证 Unity 碰撞适配（真实 PhysX）确认诊断路径无回归。
3 Build/Build HyldDS (Windows Headless)重打正式包（现19:16包是旧代码，不能验正式内容）。
4 Lobby以HYLD_PMNET_DS=1并让PMDsBattleContentConfig找到manifest（默认仓库Client/Assets/Resources/PMNet/BattleContentV1.json，可用HYLD_PMNET_CONTENT_MANIFEST覆盖）；manifest缺失时新链拒绝拉局，不得回落诊断。
5 两个Unity客户端经PMDS1入局验证正式地图障碍/出生/表现与WASD，再验退出回收；攻击/大招/摇杆UI与跨机仍未接，属R5/R6。
