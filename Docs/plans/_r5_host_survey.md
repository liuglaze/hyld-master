# R5 宿主侧调查：身份/时间 API 与投射物声明最小扩展点

范围：`D:/UGit/hyld-master`。只读调查，实机后置。语言面 Unity 2019.4 / C# 7.3 / netstandard2.0。

## 已确认

### A. R5 目标历史所需的七元组：真实可用 API

| 需求字段 | 真实可用 API（文件:行） | 命名空间/类型 | 适用角色 |
|---|---|---|---|
| SessionEpoch | `PMR4MovementDriver.Epoch`（PMR4MovementDriver.cs:769） | uint | 全部 |
| | `PMPredictionTimeline<TI,TS,TA>.SessionEpoch`（PMPredictionTimeline.cs:324） | uint | AP |
| | `PMPredictionSnapshot<TI,TS,TA>.Epoch`（PMPredictionContracts.cs:66） | uint | 全部（快照） |
| | `PMR4MovementSnapshotBlob.Epoch` / `...InputBatch.Epoch` / `...EventBatch.Epoch`（PMR4MovementCodec.cs:86/77/119） | uint | 全部 |
| | `PMNetWorld.Session.Epoch`（PMNetWorld.cs:189 → PMNetIdentity.cs:127） | uint | 全部 |
| | 宿主来源：DS `PMDsBootstrappedMatch.Epoch`（PMDsTicket.cs:1056）；Client `PMDsEntryOffer.Epoch` | uint | 宿主 |
| NetId | `PMNetObject.NetId`（PMNetObject.cs:18，`PMNetId`：`.Value`/`.IsStatic`/`.IsValid`，PMNetIdentity.cs:23） | PMNetId | 全部 |
| | `PMR4MovementDriver.InstanceId`（PMR4MovementDriver.cs:772，== `player.NetId.Value`，构造期写死 PMR4MovementDriver.cs:640） | uint | 全部 |
| | `PMPredictionTimeline.InstanceId`（:325）/ `PMPredictionSnapshot.InstanceId`（:67） | uint | AP/快照 |
| StreamVersion | `PMR4MovementDriver.StreamVersion`（:775） | uint，非 0 | 全部 |
| | `PMR4MovementSnapshotBlob/InputBatch/EventBatch.StreamVersion`（Codec:88/79/121） | uint | 全部 |
| | **`PMPredictionTimeline` / `PMPredictionSnapshot` 没有 StreamVersion 字段** | — | — |
| OutputFrame（Input 域边界） | `PMR4MovementDriver.OutputBoundary`（:790；Authority→当前权威边界，AP→PendingFrame，SP→interp LastOutputFrame） | PMFrameId(Input) | 全部 |
| | `PMR4MovementDriver.ConfirmedBoundary`（:802，仅 AP） | PMFrameId(Input) | AP |
| | `PMPredictionTimeline.PendingFrame / ConfirmedFrame / OldestRetainedBoundaryFrame / AcceptedBoundaryFrame`（:328/342/354/348） | PMFrameId(Input) | AP |
| | `PMPredictionSnapshot.OutputFrame`（:68） | PMFrameId(Input) | 全部（快照） |
| AuthorityServer frame | `PMPredictionTimeline.PendingServerFrame`（:339） | PMFrameId(AuthorityServer) | AP |
| | `PMPredictionSnapshot.ServerFrame`（:69，经 `GetSnapshot()`:834 / `TryGetBoundarySnapshot`:840） | PMFrameId(AuthorityServer) | AP/快照 |
| | `PMInterpolationBuffer.LastServerFrame`（PMInterpolationBuffer.cs:132）/ `PMInterpolatedState.ServerFrame`（:31） | PMFrameId(AuthorityServer) | SP |
| | `PMTimeStep.ServerFrame`（PMNetIdentity.cs:293 区块） | PMFrameId | 模型 |
| | **`PMR4MovementDriver` 无公开 `AuthorityServerFrame` 访问器**：`_authorityServerFrame` 为 private（:473），只在 `Pump` 写入（:1247）、写进快照 blob（:872） | — | DS |
| | **DS 宿主也无公开访问器**：`_movementServerFrame` private（PMDsSessionHost.cs:512），每宿主 tick +1（:1096 附近） | — | DS |
| TotalTimeMS | `PMR4MovementDriver.AuthorityTotalSimTimeMs`（:811） | double | DS |
| | `PMPredictionTimeline.CurrentTotalSimTimeMs`（:373）/ `PMPredictionSnapshot.TotalSimTimeMs`（:70） | double | AP/快照 |
| | `PMInterpolationBuffer.LastTotalSimTimeMs / PreviousTotalSimTimeMs`（:126/129）/ `PMInterpolatedState.TotalSimTimeMs`（:34） | double | SP |
| | blob `TotalSimTimeMs`（Codec:97） | double | 全部 |
| Position | `PMMoverSyncState.Position`（PMMoverState.cs:526 区块，`PMVector3`，米，胶囊中心） | 结构 | 全部 |
| | AP：`GetPredictedSync()`（:981）/ `Timeline.GetSyncSnapshot()`（:824）/ `Timeline.TryGetBoundarySnapshot(b).Sync`（:840） | 深克隆 | AP |
| | DS：`GetAuthoritativeSync()`（`PMR4MovementDriver.cs:1407`，深克隆当前权威状态） | 深克隆 | DS |
| | SP：`SamplePresentation().Sync`（:1494） | `PMInterpolatedState` | SP |

**按帧查询历史状态的唯一 API**（R5 目标历史的直接依据）：

- `PMPredictionTimeline<TI,TS,TA>.TryGetBoundarySnapshot(PMFrameId boundary, out PMPredictionSnapshot<TSync,TAux>)`（PMPredictionTimeline.cs:840-860）
  - boundary **必须是 `PMFrameDomain.Input`**，否则抛异常（:845）；
  - 仅在窗口 `[OldestRetainedBoundaryFrame, PendingFrame]` 内返回 true；越界返回 false（不抛）；
  - 返回的 snapshot 含 `Epoch / InstanceId / OutputFrame / ServerFrame / TotalSimTimeMs / Sync / Aux`（`BuildSnapshot` :899-911）；
  - **只存在于 AP**：DS（Authority）不建 timeline（构造分支 PMR4MovementDriver.cs:659-673），SP 只有 2 帧插值缓冲。
- `PMPredictionTimeline.TryGetBoundaryEvents(boundary, out PMPredictionEvent[] events, out bool authoritative)`（:862）。
- 反向查询（AuthorityServer frame → Input boundary）**不存在**：`GetBoundaryTotalMs`/`GetBoundaryServerFrame` 均为 private（:1463/:1469），且只做 边界→(serverFrame,totalMs) 正向映射。

### B. 每对象 Driver 公开 API 面（R5 可直接消费）

构造/生命周期：`PMR4MovementDriver(...)`（:596，`epoch`/`streamVersion` 必须非 0，`player.NetId` 必须有效）→ `PublishInitialSnapshot()`（:850）→ `Update(double wallElapsedMs)`（:1606）→ `Freeze()`（:2205）/`Dispose()`（:2226，幂等，摘 `player.MovementDriver`）。

- DS：`Pump(double elapsedMs, PMFrameId serverFrame, long hostTickId)`（:1203，同 hostTickId 不重复充值）、`ServerSubmitTrustedEffect/Layer/LayerRemoval`（:1140/:1156/:1172）、`GetAuthoritativeSync/Aux`（:1407/:1417）、`AuthorityNextInputFrame`（:814）、`BeginServerResync(reason)`（:1439）。
- AP：`Tick(int stepMs, PMMoverInput input, PMFrameId serverFrame)`（:917）、`GetPredictedSync/Aux`（:981/:991）、`TryBuildInputPayload(out byte[])`（:1008）、`SendInputPayload()`（:1059）、`Timeline`（:817，诊断/只读）、`Journal`（:820）、`event EventDispatched`（:838）。
- SP：`Advance(double deltaMs)`（:1478）、`SamplePresentation()`（:1494）。
- 只读视图：`Player/Role/Epoch/InstanceId/StreamVersion/ConfigVersion/OutputBoundary/ConfirmedBoundary/UnackedInputCount/AuthorityTotalSimTimeMs/AuthorityBuffer/Interpolation/IsFrozen/IsDisposed/InitialSnapshotPublished`（:767-838）。
- 全部公开入口都先 `EnsureThread()`（:2309，跨线程抛 `InvalidOperationException` 并计 `ThreadViolations`）与 `ThrowIfDisposed()`（:2329）。

### C. 新投射物网络声明：最小扩展点（已确认的事实）

1. **声明文件位置与生成集合不可分裂。** 生成的程序集注册表是**单一静态类型** `PMNet.Generated.PMNetGeneratedRegistry`，`RegisterAll()` 逐个显式列出类（Generated registry:132-152，`GeneratedClassCount = 1`，当前只有 `PMR3Player`；`PMNet_OnRepDispatch`/`PMGeneratedClassId` 每类各一份）。因此新增声明**必须并入同一生成集合**，否则同一程序集出现两份注册表类型（CS0101）。现有生成命令（_r4b_network_report.md:52-56）：
   ```
   dotnet Tools/PMNetGen/bin/Release/net8.0/PMNetGen.dll --decl-gen Client/Assets/Scripts/PMR3 \
     --out-dir Client/Assets/Scripts/PMR3/Generated --id-lock Docs/plans/pmnet-r3-ids.json
   ```
   即输入目录 = `Client/Assets/Scripts/PMR3/`（`PMNetGen --decl-check` 逐字节校验）。
2. **类注册是强制项。** `PMR3Runtime.Attach(world, bridge)`（PMR3Runtime.cs:150 附近）当前**硬编码** `PMR3Player.PMGeneratedClassId` 做两件事：`world.RegisterClass(classId, factory)` 与 `bridge.Replication.RegisterOnRepDispatcher(classId, PMR3Player.PMR3DispatchOnRep)`（PMR3Runtime.cs:151-172）。缺前者 → 客户端收到 Create 走 `PMNetWorld.cs:673` 的 `_stats.DroppedUnknownClass++` 直接丢弃；缺后者 → `[PMRepNotify]` 永不派发。
3. **客户端创建出口是对象自己的 override。** `PMNetObject.OnReplicatedCreate()`（PMNetObject.cs:168，`protected internal virtual`）由 `PMNetWorld.OnLifecycleMessage` 在 `PMNetWorld.cs:745` 调用。`PMR3Player` 的做法（PMR3Player.cs）是 override 后转发到静态事件 `PMR3Runtime.NotifyPlayerReplicated` → `PMR3Runtime.PlayerReplicated`（PMR3Runtime.cs:79/96）。**`PMR3Runtime` 目前没有泛型 `Action<PMNetObject>` 出口**，`PlayerReplicated`/`PlayerSpawned` 都是 `Action<PMR3Player>`。
4. **DS 创建出口是 `PMR3Runtime.SpawnPlayer`**（PMR3Runtime.cs:229 附近）：仅 `world.IsServer`、要求 `IsAttached(world)`、`owner.IsReady`，顺序为 `player.OwnerConnection = owner` → 写权威初值 → `world.Spawn(player, classId)` → `bridge.RegisterReplicatedObject(player)`。它写死 `PMR3Player`，新类型需要新增同构 spawn 入口（或抽泛型）。
5. **DS 每帧挂载点**：`PMDsSessionHost.Pump()`（:1031）→ `Physics.SyncTransforms()`（:1046，全端唯一调用点，在 `endpoint.Pump` 之前）→ `_endpoint.Pump(...)`（:1048）→ `PumpMovement(nowMs)`（:1084）→ `CheckPlayerDrops/CheckSmoke` → `_lobby.Pump`。`PumpMovement` 每个宿主 tick 先把 `_movementServerFrame = AuthorityServer(值+1)`（:1096 附近）、`_movementHostTickId++`，再对 `_driversByUid`（`Dictionary<int, PMR4MovementDriver>`，:500）逐个 `driver.Update(elapsed)` + `driver.Pump(elapsed, _movementServerFrame, _movementHostTickId)`。
6. **Client 每帧挂载点**：`PMClientSessionHost.PumpActive()`（:554）→ `Physics.SyncTransforms()`（:589）→ `session.Endpoint.Pump(...)`（:591）→ `PumpMovement(session)`（:652）→ `SendPendingProbes`。`PumpMovement` 内部顺序：全 rig `Driver.Update`（:661）→ `Input.PollHardware()`（:681）→ AP 累加器子步 `TickAutonomousProxies`（:750，每 Update ≤8 步，1..50ms）→ SP `Advance`+`SamplePresentation`（:706）→ AP `ApplyPredicted(GetPredictedSync())`（:726）。`OnPlayerReplicated`（:879）在**入站 drain 内**触发，只入队 `session.PendingProbes`，发送延后到本帧 `SendPendingProbes`（契约「不在复制回调里发送」）。
7. **可复用的声明词汇**（PMR3Player.cs 已示范全部四种）：`[PMNetworkObject]`、`[PMReplicated] private T _x` + 生成的 `PMNet_Set_x(...)`、`[PMRepNotify(nameof(_x))] private void OnRep_X()`、`[PMServerRpc(Reliability=..., Validator=PMRpcValidator.ForceValidate)] public void X(...)` + **同名 `X_ForceValidate(...)` 伴生**（项目红线 13：声明了 ForceValidate 就必须有它）、`[PMClientRpc(Reliability=...)] public void Y(...)`。生成桩即 `PMNet_X(...)` / `PMNet_Y(...)`；远端唯一发送接缝是 `PMNetGeneratedRegistry.RemoteSender`（由 `PMR3Runtime.Attach` 装成 `SendRemoteRpc`，按 `target.World` 反查桥）。

### D. 已检查范围（实际读取）

必读文档（按给定顺序）：`AGENTS.md`（根，3 行跳转）→ `Client/Assets/AGENTS.md`（全文）→ `Docs/plans/net-r4-network-contract.md`（全文）→ `Docs/plans/net-r0-contract.md` §2.5/§2.6/§2.7/§3.7/§4/§5/§6/§7（含 §2.6 投射物 D-R0-30..40、§7 投射物契约全文）。

源码（直接调用面）：
- `Client/Assets/Scripts/PMR3/PMR3Player.cs`（全文）、`PMR3Runtime.cs`（全文）
- `Client/Assets/Scripts/PMR3/PMR4MovementDriver.cs`（构造:581-760、只读视图:765-838、DS 发布:840-911、Tick:917-1080、DS 输入/Pump:1082-1470、SP:1475-1500、入站:1502-1620、ApplyPending 系列:1625-2100、Freeze/Dispose/Describe:2198-2338）
- `Client/Assets/Scripts/PMR3/PMR4MovementCodec.cs`（公开结构与常量表、字段号）
- `Client/Assets/Scripts/PMNet/PMNetIdentity.cs`（全文）、`Client/Assets/Scripts/PMNet/PMNetObject.cs`（公开成员）、`Client/Assets/Scripts/PMNet/World/PMNetWorld.cs`（公开成员 + 生命周期应用点）、`Client/Assets/Scripts/PMNet/Session/PMNetSessionBridge.cs`（公开成员）、`Client/Assets/Scripts/PMNet/PMNetRole.cs`
- `Client/Assets/Scripts/PMPrediction/PMPredictionContracts.cs`（全文）、`PMPredictionTimeline.cs`（公开面 + TryGetBoundarySnapshot/BuildSnapshot/GetBoundary* 实现）、`PMPrediction/PMInterpolationBuffer.cs`（公开面）
- `Client/Assets/Scripts/PMMover/PMMoverState.cs` §PMMoverInput/PMMoverSyncState/PMMoverAuxState/PMMoverDefaults
- `Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs`（场景类签名、Pump:1031-1070、PumpMovement:1084-1132、PruneDisconnectedDrivers:1133、BuildSpawnSync、OnConnected:1423-1518、Initialize 关键段:639-790）
- `Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs`（Session/Rig 结构:94-219、PumpActive:554-612、PumpMovement:652-748、TickAutonomousProxies:750-793、EnsureMovementRig:795-877、OnPlayerReplicated:879-953）
- `Client/Assets/Scripts/PMR3/Generated/PMNetGeneratedRegistry.g.cs`（RegisterAll 段）、`Generated/PMNet.PMNet.R3.PMR3Player.g.cs`（头部 + 关键成员）
- `Tools/PMR4NetworkCheck/PMR4NetworkCheck.csproj`（netstandard2.0 库，编译 PMNet+PMPrediction+PMMover+PMR3，无 Program.cs）、`Tools/PMR4IntegrationTest/PMR4IntegrationTest.csproj`（net8.0 Exe，同源码面 + Program.cs）
- `Docs/plans/_r4b_network_report.md`（生成命令与 ID 锁清单）
- 确认不存在 `Client/Assets/Scripts/PMProjectile/` 目录（M09 尚未开工）。

未检查（越界即停）：`PMUnity*` 适配器实现体、`PMNet/Replication|Transport|Control` 内部、lobby/DS 控制链、旧 `Server/Manger/Battle` 链路、PMMover 模型算法细节。

## 高概率推断（依据 + 置信度）

1. **R5 目标历史在 DS 侧目前完全不存在**（置信度 高）。`PMAuthorityInputBuffer<PMMoverInput>`（PMR4MovementDriver.cs:464）只存**输入**（`PMAuthorityInput.InputFrame/StepMs/Input`），不存状态；DS 只保留单个当前权威状态 `_authoritySync/_authorityAux/_authorityBoundary/_authorityTotalSimTimeMs`。依据：Driver 全文件无任何 per-boundary 状态数组；`GetAuthoritativeSync()` 只返回当前值。R5 若需「帧锚 → 时间回溯」，必须**新增** DS 侧按边界（或按宿主 tick）记录的权威位置历史。
2. **AP 时间轴可作为客户端侧历史**，但受两重限制（置信度 高）：只覆盖 `[OldestRetainedBoundaryFrame, PendingFrame]`，且 `PrunedConfirmedSteps` 会随确认推进裁剪（`AvailableHistory` 默认 128，`DefaultHistoryCapacity`）。对 R5 的「迟到 Verify / 墓碑期命中」而言窗口可能过短，需要独立于时间轴的记录。
3. **新投射物声明应放 `Client/Assets/Scripts/PMR3/` 下的新文件**（置信度 中）。契约 §B1 只冻结了 R4「新增运动声明直接写 PMR3Player.cs」；但生成输入目录是**整目录**扫描（`--decl-gen Client/Assets/Scripts/PMR3`），且注册表类型唯一、必须同集合，因此在同目录新增 `[PMNetworkObject]` 文件是**不改生成策略**的唯一路径。若另建 `Client/Assets/Scripts/PMProjectile/` 独立生成集合，会得到第二份 `PMNetGeneratedRegistry` → 同程序集类型冲突。该结论未经契约背书，落地前需与主 Agent 确认（见「下一步」）。
4. **`PMR3Runtime` 是最小且必须改动的宿主接线点**（置信度 高）：`Attach` 里的 ClassId 注册与 OnRep 分发表注册是硬编码，新增类必须在此追加；同时需要一个非 `PMR3Player` 的创建事件出口（新增事件或改为 `Action<PMNetObject>`）。
5. **DS 侧 `Pump` 产出的边界数 ≠ 宿主 tick 数**（置信度 高）：`PMAuthorityInputBuffer.Pump(elapsedMs, steps)` 按墙钟信用出 0..8 步（每 100ms ≤8 步，见契约 §B1），而 `_movementServerFrame` 每宿主 tick 只 +1。因此「一个 AuthorityServer frame ↔ 多个 Input boundary」，且存在**零边界**的宿主 tick。R5 的帧锚转换必须显式定义，不能假设一一对应。

## 无法确定（缺少证据）

1. **R5 目标历史的确切键与保留窗口**：`Docs/plans/net-architecture-migration.md` §R5 只写「R2、R4 帧锚契约」为依赖，未冻结 R5 自己的历史键（是 Input boundary、AuthorityServer frame 还是 TotalSimTimeMs 单调锚）、容量、TTL。R0 §7 只说「帧锚新鲜度用**单调仿真时间**」，未给转换公式。→ 需要主 Agent/R5 契约冻结。
2. **投射物对象是否需要参与复制（`[PMReplicated]` + `RegisterReplicatedObject`）还是纯 RPC 驱动**：R0 §7 只说「直创镜像」「ID 按连接隔离」，未定 PMNet 对象形态。当前代码里没有任何 PMProjectile 实现可参照。
3. **DS 侧权威事件通道是否会成为投射物事件的承载**：`ClientMovementEventsV1` 是 owner-only 可靠 RPC（PMR3Player.cs），R5 的「命中结论」需要对观察者可见（契约 §B1 明说「R5/R6 另定观察者事件」），现无观察者事件通道。
4. **`PMNetSessionBridge.Replication` 的 RegisterOnRepDispatcher 是否支持运行期追加类**（只确认了它是「字典赋值、天然幂等」，未读实现体是否绑定在 Seal 之后仍可加类）。这只影响接线顺序，不影响扩展点存在性。
5. **真实 Unity/DS 行为**（本机无 Unity）：`Physics.SyncTransforms` 的帧内可见性、真实 PhysX 命中判定均未验证，属 P4B6/PENDING_USER。

## 范围

- **属于本次调查**：上表 A/B/C/D 所列文件与 API；任务的精确问题（七元组来源、投射物声明最小扩展点）。
- **本次明确不覆盖**：R5 契约文本制定、任何代码修改、生成 ID 分配、Unity 编译/实机、`PMUnity*` 表现层、旧战斗链（`Server/Manger/Battle`）退役、M09 目录结构决策的最终拍板。
- **后续 R5 落地时的改动边界（建议，供主 Agent 判断）**：`Client/Assets/Scripts/PMR3/` 新增声明文件 + `PMR3Runtime.cs` 接线 + 两个 SessionHost 的挂载点 + 新的 `Client/Assets/Scripts/PMProjectile/`（纯 C# 与历史/裁决逻辑）+ `Docs/plans/pmnet-r3-ids.json` 与 `PMR3/Generated/**` 重新生成（生成命令/锁不变）。

## 下一步（最小补充查询 / 运行时验证）

1. **冻结 R5 历史契约**（必须先做，否则实现会分叉）：在 `Docs/plans/net-r5-*.md` 明确 —— 历史键（建议：`(Epoch, InstanceId, StreamVersion, Input boundary)` 为主键，`AuthorityServer frame` 与 `TotalSimTimeMs` 作锚元数据）、保留窗口与容量、DS 侧记录时机（建议紧跟 `SimulatePumpSteps` 的每个真实边界，跳过占位步）、查询 API 名与 miss 语义。
2. **决定声明归属**：确认「同一生成集合 + `PMR3` 目录新增文件」是否被接受；若不接受，需先定义第二生成集合如何避免 `PMNetGeneratedRegistry` 重名（例如改生成器输出命名空间/类名，属生成器改动，成本更高）。同时确认 R5 声明是 `[PMNetworkObject]` 对象还是纯 RPC + 客户端本地表现。
3. **最小补充查询（各 1 次）**：(a) `PMNetSessionBridge.Update` 内 `RegisterOnRepDispatcher` 的实际实现（是否 Seal 后仍可追加类）；(b) `PMAuthorityInputBuffer.Pump` 的步数/信用算法（确认「零边界宿主 tick」与「一 tick 多边界」的精确条件）；(c) `PMDsSessionHost` 是否有 `Dispose` 里可复用的 per-driver 收尾模式（投射物对象也需要 Freeze→Dispose 顺序）。
4. **需要新增的 API（实现阶段的判断，本报告不实现）**：`PMR4MovementDriver` 暴露权威服务帧只读访问器（或由 R5 宿主侧自持帧号）；DS 侧权威位置历史 recorder（建议与 Driver 同层、由宿主在 `Pump` 后调用，避免 Driver 承担业务记录）；`PMR3Runtime` 增加投射物类注册与新对象创建事件出口。
5. **运行时验证（后置，不得用替身冒充）**：真实 Unity + 真实 DS 双客户端下验证 —— DS 权威历史在重同步升流后不串流、AP 历史窗口裁剪不影响迟到 Verify、投射物 Create/RPC 在 owner/非 owner 两侧的行为。
