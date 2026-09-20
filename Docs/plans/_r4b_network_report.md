# R4-B / B1 网络预测层实施报告

> 状态与验收以 `Docs/plans/net-architecture-migration.md` 为准；本文是该阶段的**实施与证据报告**。
> 冻结接口展开见 `Docs/plans/net-r4-network-contract.md` §B1；预测核心契约见 `net-r4-prediction-contract.md`。

---

## 0. 摘要（≤1500 字）

B1 已按契约落地：**codec（PMNetReader/Writer protobuf 原语，完整 Sync/Aux，含 ActiveLayers.ElapsedMs）+ 声明 RPC/属性 + 每对象 Driver（真实 AP timeline / DS 预算 / SP 插值）+ 有限输入重发 + 可靠事件与 Resync 流代次 + 防非 owner/旧流 + 上行不开放可信效果**。

- 新增：`PMR4MovementCodec.cs`（4 种带 kind 头的有界 blob：Inputs/Snapshot/Events/Resync；严格字段升序、完整 Sync 必填、NaN/未知 Mode/越界/尾部/跨通道错用全部 fail-closed）、`PMR4MovementDriver.cs`（三角色一实现；有界事件日志闭合迟到事件）。
- 声明：`PMR3Player.cs` 新增 1 个复制属性 + 4 条 RPC（方向/可靠性/校验档位按契约冻结），并新增声明层接缝 `IPMMovementNetworkDriver`，**使只编译声明层的消费者工程无需改 csproj**。
- 生成：真实调用 `PMNetGen --decl-gen`，沿用 `pmnet-r3-ids.json`；**原 ID 全部不变**（ClassId 405815557、_uid 18801、_probeCount 12656、ServerProbe 34232、ClientEcho 63853）；新 ID：`_movementSnapshotV1` 61580、`ServerMovementInputV1` 25428、`ServerMovementResyncV1` 15021、`ClientMovementEventsV1` 5180、`ClientMovementResyncV1` 28381；`--decl-check` 退出 0。
- 验收：`Tools/PMR4NetworkTest` **327 项 0 失败**（真实 PMTransport 字节链 + 真实生成桩 + 真实世界/复制/生命周期；含丢首包、重复、乱序、完整状态差异回滚、事件先后/迟到/去重、历史与窗口耗尽后升流重同步、旧流丢弃、SP 只插值、预算防加速、多角色隔离、非 owner 拒绝）；`Tools/PMR4NetworkCheck`（netstandard2.0 + C#7.3，零 UnityEngine）**0 错误 0 警告**。反向验证：注入 2 处缺陷各被精确抓出（4 项 / 12 项失败），并按 md5 逐字节还原。
- 阻塞项（**需主侧处理，均不在本任务写入边界内**）：① `Server/Server.csproj` 以 `PMR3\**\*.cs` 通配，需补 2 行 include（已在本机仓库外验证 0 错误）；② `Tools/PMR3RuntimeTest/Program.cs:192/194` 的位宽/属性数期望 2 → 3。另发现 **R4-A 依赖缺陷**（`PMPredictionTimeline` 的“快照构造”不写 `_boundaryStartFrame`，OutputFrame>0 时首次读取越界），本批用 public API 绕开并保留证据。

---

## 1. 必读证据（已按顺序完成）

| 顺序 | 文档 | 用途 |
|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 仓库入口与三份下游文档指向 |
| 2 | `Client/Assets/AGENTS.md` | 客户端现状基线（预测/权威消费链、CSP、参数表） |
| 3 | `Server/AGENTS.md` | 服务端现状基线（LZJUDP/NetSim/BattleLoop） |
| 4 | `Docs/plans/net-architecture-migration.md`（全篇分块读，2088 行） | 状态唯一事实源、R 阶段与 T 编号、§9.5 坑清单（#31 门禁只在“本次构建 0 错误”下可信） |
| 5 | `Docs/plans/net-r4-network-contract.md` | **本批冻结接口依据**（§B1 承载/上限/流代次/事件契约） |
| 6 | `Docs/plans/net-r4-prediction-contract.md` | AP 帧域、128 历史、DS 预算（8 步/100ms、信用上限 200、初值 50）、SP 只插值、事件证据线 |
| 7 | `Docs/plans/net-r2-codegen-contract.md` | 稳定 ID 算法、生成物 API 面、`byte[]` 线格式（sint32 长度 + 元素 varint）、数组上限 4096 |
| 8 | `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md` | bash/PowerShell 边界 |
| 9 | `.agents/skills/mover-quick-start/SKILL.md` + `references/network-prediction-checklist.md` | 仅作 C# 移植约束（InputCmd 纯洁性、无帧外副作用、NetSerialize 完整性、确定性、bHas* 最小化） |

代码事实（读到的实现性质，决定了实现方式）：`PMNetRpcReceive` 的 Owner 校验是 Server RPC 的唯一准入；`PMNetSessionBridge.SendRpc` 立即编码（不可靠=Unreliable 域、可靠=Reliable 域且先排生命周期）；复制初始状态走生命周期 v2 的 `RepInitialState`（声明式初值），因此“Spawn 首次 Flush 前写好快照”即可随 Create 原子到达；`PMMoverInput.Empty()` 的 Effects/Layers 为 null（便于上行裁剪）。已确认 **R4A 通过 417/245/264**，本批不动 PMR3 既有 Probe/Echo 语义、不动 PMPrediction/PMMover 任何文件。

---

## 2. 交付物

| 文件 | 说明 |
|---|---|
| `Client/Assets/Scripts/PMR3/PMR4MovementCodec.cs`(+meta) | 专用载荷 codec：kind/version/身份三件套 + 严格拒绝 + 有界 |
| `Client/Assets/Scripts/PMR3/PMR4MovementDriver.cs`(+meta) | 每对象 Driver（Authority / AutonomousProxy / SimulatedProxy）+ `PMR4MovementEventJournal` |
| `Client/Assets/Scripts/PMR3/PMR3Player.cs` | 新增声明与委托出口 + `IPMMovementNetworkDriver` 接缝 |
| `Client/Assets/Scripts/PMR3/Generated/*.g.cs` | 真实生成产物（两个文件，未手写） |
| `Docs/plans/pmnet-r3-ids.json` | ID 锁（原 ID 不变 + 5 个新 ID） |
| `Tools/PMR4NetworkTest/{Program.cs,*.csproj}` | B1 门禁（net8.0，真实 Transport/World/Session/生成桩） |
| `Tools/PMR4NetworkCheck/PMR4NetworkCheck.csproj` | 语言面门禁（netstandard2.0 + C#7.3，零 UnityEngine） |

生成命令（**未手写 g.cs**）：
```
dotnet build Tools/PMNetGen -c Release
dotnet Tools/PMNetGen/bin/Release/net8.0/PMNetGen.dll --decl-gen Client/Assets/Scripts/PMR3 \
  --out-dir Client/Assets/Scripts/PMR3/Generated --id-lock Docs/plans/pmnet-r3-ids.json
dotnet Tools/PMNetGen/bin/Release/net8.0/PMNetGen.dll --decl-check Client/Assets/Scripts/PMR3 \
  --out-dir Client/Assets/Scripts/PMR3/Generated --id-lock Docs/plans/pmnet-r3-ids.json   # exit 0
```

运行：
```
dotnet build Tools/PMR4NetworkCheck -c Release     # 0 错误 0 警告
dotnet build Tools/PMR4NetworkTest  -c Release && dotnet Tools/PMR4NetworkTest/bin/Release/net8.0/PMR4NetworkTest.dll
```
> 纪律：build 与 run 一律串在同一个 `&&` 链里 —— 计划 §9.5 #31 的记录（编译失败仍跑旧二进制打印全绿）在本轮**真实发生过一次**（H 段改动的第一次运行即命中）。

---

## 3. 精确 API 与初始化次序（宿主集成按此接线）

### 3.1 声明（`PMR3Player`）

```csharp
[PMReplicated] private byte[] _movementSnapshotV1;            // PMCond.None，OnRep 只通知/入队
[PMRepNotify(nameof(_movementSnapshotV1))] private void OnRep_MovementSnapshot();

[PMServerRpc(Reliability = Unreliable, Validator = ForceValidate)] public void ServerMovementInputV1(byte[] payload);
[PMServerRpc(Reliability = Reliable,   Validator = ForceValidate)] public void ServerMovementResyncV1(uint streamVersion);
[PMClientRpc(Reliability = Reliable)]  public void ClientMovementEventsV1(byte[] payload);   // 仅 owner
[PMClientRpc(Reliability = Reliable)]  public void ClientMovementResyncV1(byte[] payload);   // 仅 owner

public IPMMovementNetworkDriver MovementDriver;              // 声明层接缝（实现在 Driver）
public byte[] MovementSnapshotPayload { get; }               // 只读借出
public void PublishMovementSnapshot(byte[] payload);         // DS 唯一写入口（赋值即标脏）
public static Action<string> MovementWarn;                   // 告警出口（宿主接日志）
```

生成桩（业务调用这些，**不**直接调实现体）：`PMNet_ServerMovementInputV1` / `PMNet_ServerMovementResyncV1` / `PMNet_ClientMovementEventsV1` / `PMNet_ClientMovementResyncV1`。

### 3.2 Driver 公开面（`PMNet.R3.PMR4MovementDriver`）

- 构造：`PMR4MovementDriver(PMR3Player player, IPMMoverCollisionQuery query, PMNetRole role, uint epoch, PMMoverSyncState initialSync, PMMoverAuxState initialAux, long initialOutputFrame = 0, PMFrameId initialServerFrame = default, double initialTotalSimTimeMs = 0, int historyCapacity = 128, uint streamVersion = 1)`；另有客户端工厂 `CreateFromPlayerInitialSnapshot(player, query, epoch, out error)`（按副本角色 + Create 记录初值建 AP/SP）。构造即把 `player.MovementDriver` 指向自己。
- DS：`PublishInitialSnapshot()`（**必须早于首次生命周期 Flush**）、`Pump(elapsedMs, serverFrame)` / `Pump(elapsedMs, serverFrame, hostTickId)`（同 `hostTickId` 不重复充值）、`ServerSubmitTrustedEffect/TrustedLayer/TrustedLayerRemoval(...)`（**只允许 DS**，在下一次真正模拟步生效）、`BeginServerResync(reason)`、`GetAuthoritativeSync/Aux()`。
- AP：`Tick(stepMs, input, serverFrame)`、`GetPredictedSync/Aux()`、`TryBuildInputPayload(out bytes)` / `SendInputPayload()`、`OutputBoundary`/`ConfirmedBoundary`/`UnackedInputCount`、`Freeze()`。
- SP：`Advance(deltaMs)`、`SamplePresentation()`。
- 通用：`Update(wallElapsedMs)`（消费入站 + 断线墙钟；所有步进入口都会自动 `ApplyPending`，宿主漏调也不会丢入站）、`Dispose()`、`Describe()`。
- 入站转发（由声明承载调用）：`OnSnapshotReplicated()`、`OnServerInputPayload(bytes)`、`OnServerResyncRequest(uint)`、`OnClientEventsPayload(bytes)`、`OnClientResyncPayload(bytes)`。
- 诊断计数：`InputPayloadsReceived/InputsAdmitted/InputsRejectedByBuffer/InputRejectedStream/InputRejectedIdentity/SnapshotPayloadsApplied/SnapshotRejectedWorldVersion/EventBatchesAccepted/EventPayloadsSent/ResyncServed/ResyncRejectedRegressing/RebindFallbacks/...`。

### 3.3 初始化次序（契约 §B1）

```
DS 侧：  world.Spawn(player, ClassId)  →  new PMR4MovementDriver(..., Authority, ...)  →
         driver.PublishInitialSnapshot()   ← 必须在此刻（首次 bridge.Update/Flush 之前）
         → 每宿主帧： driver.Update(wallMs) ; driver.Pump(elapsedMs, serverFrameId)
AP 侧：  收到 Create（初值已随记录到达）→ 宿主在 PlayerReplicated 里
         PMR4MovementDriver.CreateFromPlayerInitialSnapshot(player, query, epoch, out err)
         → 每帧：driver.Tick(dt, input, serverFrame) ; driver.SendInputPayload() ; driver.Update(wallMs)
SP 侧：  同上工厂（角色 SimulatedProxy）→ 每帧 driver.Update(wallMs) ; driver.Advance(wallMs) ; SamplePresentation()
断线：   driver.Freeze()   （只冻结；恢复只走 DS 升流 + 显式 Resync）
退出：   driver.Dispose()  （幂等，并摘除 player 上的引用）
```
`IPMMoverCollisionQuery` 由宿主注入：DS 与客户端必须用**同一套几何与 `WorldVersion`**（不一致的快照会被拒并请求重同步）。

---

## 4. 关键实现决策（与契约的对应）

1. **一包内输入不要求连续**（只要求严格升序）：这样同一包能“最旧 4 条 + 最新 4 条”，同时满足“按最旧未确认窗口持续重发防缺帧饥饿”与“可兼带最新”。未确认窗口上限 128；包内最多 8 条。
2. **事件通道必须有活的产出者**：R4-A 的 `PMMoverModel.Simulate` 目前自产 **0 条事件**。B1 要求事件可被真实输入驱动出证据，因此 Driver 在 DS 侧把两次权威状态之间的**离散不可逆跃迁**记成事件：Mode 变化（kind=1）与落地（Grounded false→true，kind=2），Key 只由 (边界, kind) 决定。AP **从不**本地派生事件。
3. **不双重派发**：Driver 下发的快照**从不携带事件证据**（`ConfirmedEvents=null`/`ConfirmedEventFrames=null`），因此预测时间轴 `ConfirmedEventsEmitted` 恒为 0；事件唯一派发者是独立的有界日志。
4. **事件日志跨重同步不丢事件**：`Rebind(新流, 重绑边界)` 保留边界 ≤ 重绑边界的未派发事件、丢弃越界条目，并继续接纳**上一流**的在途批，直到收到第一条新流的批（可靠通道保序 ⇒ 那时上一流已发完）才闭合。
5. **重同步只由 DS 递增 StreamVersion**；AP 只能请求。旧流输入在 DS 丢弃并计数；旧流/更高流快照在 AP 丢弃并等 Resync 载荷整体重绑；“不倒退”只按**已确认**边界与已确认权威时间判定（否则 AP 领先权威的正常情形会把重同步永远拒掉）。
6. **可信效果只由 DS 在帧边界提交**：随该步的 Input 进入真实模型；上行载荷里带 Effects/Layers 在**编码阶段**即失败。

---

## 5. 测试结果（真实执行）

| 门禁 | 结果 |
|---|---|
| `Tools/PMR4NetworkCheck`（netstandard2.0 + C#7.3，零 UnityEngine 引用） | **0 错误 / 0 警告** |
| `Tools/PMR4NetworkTest`（net8.0，真实 Transport/World/Session/生成桩） | **327 项 0 失败（exit 0）** |
| `PMNetGen --decl-check`（产物与声明/锁逐字节） | **exit 0**；原 ID 全部不变 |
| 反向验证①：codec 层必填位掩码 `0x1FE→0xFF` | 被抓（4 项失败），md5 逐字节还原（`2babe1db…`） |
| 反向验证②：DS 输入流代次校验取反 | 被抓（核心链路 12+ 项失败），md5 逐字节还原（`07af4c64…`） |
| 回归：PMDeclCheck / PMNetE2E / PMNetWorldTest / PMReplicationTest / PMNetSessionTest / PMTransportTest | 71 / 209 / 197 / 265 / 405 / 173 全 0 失败 |
| 回归：PMCallspaceCheck / PMDsControlTest / PMDsLobbyTest / PMUdpRouterTest / PMUdpAdmissionTest | 93 / 623 / 268 / 44 / 360 全 0 失败 |
| 回归：PMPredictionTest / PMMoverTest / PMMoverPredictionTest | 417 / 245 / 264 全 0 失败（R4-A 未退化） |
| 回归：PMR3IntegrationTest / PMClientCheck / PMUnityGlueCheck / PMNetLangCheck / PMSharedConfigCheck / PMHeroDataCheck | 143 全 0 失败 / 0 错误 ×5 |
| `Tools/PMR3RuntimeTest` | 构建 0 错误；**2 项期望过期**（见 §6 阻塞①） |
| `Server/Server.csproj` | **166 错误**（全部来自新增两文件缺 PMPrediction/PMMover，见 §6 阻塞②） |

覆盖的 B1 场景：全字段往返（含 `ActiveLayers.ElapsedMs`、yaw 规范化）、上限/NaN/未知 Mode/缺必填/尾部/重复字段/跨 kind/身份非法全部拒绝且**无部分应用**；真实链路 AP→DS→AP 收敛与边界推进；输入首包丢失后重发补齐；重复输入只模拟一次；窗口“最旧+最新”选取；完整状态差异（可信 Teleport+SetMode）后恢复重放并收敛；事件先后/去重/迟到仍派发/序号缺口显式失败/溢出不淘汰；未确认窗口与历史耗尽后升流重同步 + 旧流输入/快照丢弃 + 倒退拒绝；SP 只插值（Alpha∈(0,1)、不外推、拒绝 Tick/Pump、冻结不推进）；DS 预算 8 步/100ms、同 tick 不重复充值、0dt 拒绝、缺帧不跨越；多角色隔离 + 非 owner 上行被拒。

---

## 6. 未解问题 / 阻塞（交主侧）

**阻塞①（消费者期望过期，1 行改动 ×2）**：`Tools/PMR3RuntimeTest/Program.cs:192` 与 `:194` 断言 `PMGeneratedChangeMaskBitCount == 2` 与 `Rep.Properties.Length == 2`（Uid+ProbeCount）。B1 依契约新增第 3 个复制属性，应改为 `3`（建议同时断言 `_movementSnapshotV1` 已注册）。这是本轮唯一因 B1 而变红的既有门禁项（其余 171 项通过）。

**阻塞②（消费者 csproj，2 行改动 ×1，已在本机仓库外验证）**：`Server/Server.csproj:57` 以 `..\Client\Assets\Scripts\PMR3\**\*.cs` 通配，本轮新增的两个文件依赖 `PMNet.Prediction` / `PMNet.Mover`，导致 166 个 CS0246。修复（供主侧应用）：
```xml
<Compile Include="..\Client\Assets\Scripts\PMPrediction\**\*.cs" LinkBase="PMPrediction" />
<Compile Include="..\Client\Assets\Scripts\PMMover\**\*.cs" LinkBase="PMMover" />
```
验证方式：在仓库外建了等价工程（PMNet+Shared+PMR3+PMPrediction+PMMover）→ **0 错误 0 警告**。若 Lobby 侧不希望引入运动/预测代码，替代方案是在该 glob 下排除这两个文件（同样由主侧决定）。
（其余 36 个 Tools 工程全部 0 构建错误；`PMClientCheck` / `PMR3RuntimeTest` / `PMR3IntegrationTest` / `PMR3UnitySmoke` / `PMUnityGlueCheck` 显式列文件，且声明层接缝改造后**无需**任何 csproj 修改。）

**③ R4-A 依赖缺陷（不在本任务写入边界，未修改）**：`Docs/plans/net-r4-prediction-contract.md` 的“可信完整快照构造”在 `PMPredictionTimeline.cs` 里只写 `_pendingFrame`/`_confirmedFrame`，**不写 `_boundaryStartFrame`**（该字段只在 `Resync` 的 epoch 重绑分支 `:647` 与裁剪 `:1492` 被赋值）。因此当 `PMPredictionSnapshot.OutputFrame > 0` 时，基态边界(0)与读取边界不一致，**首次读取**即在 `PMPredictionTimeline.cs:1439` 抛「读取边界越界（boundary=…）」。建议修法：在快照构造函数里补 `_boundaryStartFrame = boundary;`（一处），并补一条“以 OutputFrame>0 的快照构造并立即 Tick”的回归用例。本批在不改 PMPrediction 的前提下用**纯 public API**落地：先以“占位 epoch”构造，再用 `Resync` 的显式重绑分支把 epoch/instance/边界/基态一次对齐（`PMR4MovementDriver.CreateReboundTimeline`，含 `RebindFallbacks` 计数与告警）。修好后 Driver 可直接退回普通构造。

**④ 观察项（供 B2/B3 参考，不是缺陷）**：
- 复制通道会对同一值重复下发，SP 侧会出现连续两张 `TotalSimTimeMs` 相同的快照（`From==To`），此时插值窗口退化、`Sample()` 正确地钳到最新（Alpha=1）。真正的插值窗口需要表现时钟与权威进度同相位；表现层实现时不要用“同速推进 + 立即采样”的做法，否则永远看不到 Alpha∈(0,1)。
- 发送→传输层 flush→投递→派发→接纳都在帧边界上，最后一包通常要**再过一个帧周期**才被 DS 接纳。宿主/门禁的断言必须允许这一帧延迟（本批用 `Settle()` 显式再跑真实帧解决）。
- 初始状态 `PMMoverSyncState.CreateDefault()` 的位置是 `(0,1,0)`，而 `PMMoverTestWorld` 的墙盒是 `x∈[-0.5,0.5], y∈[0,2], z∈[-4,4]` —— 原点在**墙内**，直接用默认出生点会“测不出输入驱动位移”。B2 的场景布置必须避开。

**⑤ 本批明确不做（诚实边界）**：Unity PhysX 碰撞适配与真实胶囊查询（B2：`Tools/PMR4UnityCheck` 等）；真实 UDP socket / 跨机 MTU / NAT（B3/B4）；行为类 Modifier 的完整补偿、技能激活裁决、SP 观察者技能事件（契约 §B1 明确不在本批）；投射物（R5）；旧链退役（R6）。`Tools/PMR4UnityCheck`、`Tools/PMR4UnityAdapterTest` 由 B2 并行产出，本任务未读写。
