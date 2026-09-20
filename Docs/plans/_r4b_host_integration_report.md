# R4-B / B3 宿主接线、消费者与新 UDP 集成门禁 实施报告

> 状态与验收以 `Docs/plans/net-architecture-migration.md` 的 **P4B4** 行为准；本文是该阶段的**实施与证据报告**。
> 冻结接口展开见 `Docs/plans/net-r4-network-contract.md`（§B3 与末尾「主侧集成前复核修订」）、`net-r4-prediction-contract.md`。
> B1/B2 交付物与 API 见 `_r4b_network_report.md`、`_r4b_unity_report.md`。

---

## 0. 摘要（≤1500 字）

按契约 §B3 完成 **B3 实际宿主接线 + 消费者构建 + 真实 UDP 两客户端集成门禁**，全部落在允许写入清单内。

- **DS 宿主（`PMDsSessionHost`）**：新增每玩家 `PMR4MovementDriver`；建场景后按 `WorldVersion=1` 建 `PMUnityMoverCollisionQuery`（allowlist 恰为地板 + 墙两个 Collider，白名单不完整即拒绝启动）；`Connected → SpawnPlayer → 建 Driver → PublishInitialSnapshot`（**在首次生命周期 Flush 之前**，所以初值随 Create 原子到达，不靠重同步补）；出生点按名册给 x=±3（墙外）、z=名册行居中、y=半高，**刻意避开默认原点**（原点正落在测试墙盒内）；每 `Pump` 先 `Physics.SyncTransforms()` 一次 → `endpoint.Pump`（内含 bridge.Update，不重复调）→ 逐实例 `Update+Pump(elapsedMs, serverFrame, hostTickId)`（实际墙钟、显式 hostTickId、DS 自己的 AuthorityServer 帧）；断线按「连接 `IsReady` 为假」先 `Freeze` 再 `Dispose`；释放顺序为 driver → 端点 → 世界 → 场景。
- **客户端宿主（`PMClientSessionHost`）**：`OnPlayerReplicated` 不再只处理 AP —— **全部 AP/SP 副本**都建 Driver（旧过滤会让别人的角色在本地永不动）与 `PMUnityMoverPresentation`；AP 用真实整 ms 余数累加出 1..50 子步、每 `Update` 最多 8 步，每子步「先 `Sample`（不消费边沿）→ `Tick` → **接受后才 `Consume`**」；SP 只 `Advance` + `SamplePresentation`（只插值、不外推）；每帧先 `Physics.SyncTransforms()` 再 `endpoint.Pump`，发送留到下一次 `endpoint.Pump`（不抢预算）；只有本地 owner 建可见测试相机（由表现层 Dispose 销毁，不改旧相机）；失联/失败 `Freeze` 全部驱动，之后进入「只推墙钟」的冻结帧（不再发输入、不再推进表现）。
- **消费者**：`Server.csproj` 用 `Exclude` 把 `PMR4MovementDriver/Codec` 排除出 Lobby（声明层接缝已够用，Lobby 不背仿真）；`PMR3RuntimeTest` 位宽/属性数 2→3 并新增新字段与 4 条运动 RPC 的真实注册断言（180/0）；`PMClientCheck`、`PMUnityGlueCheck` 真编新核心/Driver/适配/两个宿主（补 PhysicsScene/Physics/CameraClearFlags 等桩，旧 socket 只留最小边界桩）；`PMR4UnityCheck` 引**真实 Unity2019 DLL** 编译两个宿主 + 全部新核心（`HostDependencies.cs` 只替旧 socket 边界）。
- **新增门禁 `Tools/PMR4IntegrationTest`**：真实回环 UDP + 真实票据签发/逐身份验签 + 真实 `PMUdpSessionEndpoint` + 真实生成桩/复制 + 真实 codec/Driver，跑两客户端各 AP、互见 SP：**113 项 0 失败，exit 0**。覆盖移动收敛与 SP 插值、跳跃（Mode/Landed）可靠事件、DS 可信 Teleport 触发权威差异+重放收敛、DS 升流重同步（新流快照先被拒、旧流输入被 DS 丢、旧流快照不回写、重绑后继续收敛）、断线冻结+释放。
- **负向验证**：注入两处缺陷（复刻「SP 被跳过」、断线忘了先 Freeze）→ 分别被抓出 8 项 / 1 项失败（负向 exit 1），随后按 sha256 逐字节还原（`e8dbeff5…`），还原后 113/0 exit 0。
- **回归**：R3Runtime 180/0、R3Integration 143/0、R4Network PASS、Prediction 453/0、Mover 245/0、MoverPrediction 264/0、NetE2E/World/Replication/Session/Transport/Decl/Callspace/DsControl/DsLobby/UdpRouter/UdpAdmission 全 0 失败；Server/Client/Glue/UnityCheck 构建 0 错误。
- **诚实边界**：net8 门禁的碰撞环境是确定性 AABB 替身（**不是 PhysX**），没有表现层与真实玩家输入；真实 Unity 物理、真实 DS 进程、跨机项仍是 **P4B6 / PENDING_USER**。

---

## 1. 必读文档与实际阅读次序

按委派指定顺序完整阅读，未跳读：

| 顺序 | 文档 | 对本次落点的直接作用 |
|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 仓库入口；确认三份下游文档指向 |
| 2 | `Client/Assets/AGENTS.md` | 客户端现状（输入/命令/发送节拍/权威消费/CSP）、坐标系（X 水平、Z 纵向、Y 固定高度 = 1）、`frameTime=0.016`、旧 UI/相机链路不在新链 |
| 3 | `Server/AGENTS.md` | 服务端现状（LZJUDP/NetSim/BattleLoop）、「服务端不含 Unity」的历史边界与 DS 归属 |
| 4 | `Docs/plans/net-architecture-migration.md`（2087 行，分块读） | 状态唯一事实源；§3.9.4 运行时契约（每对象 Role、回滚语义、不得另造第二套时钟/身份）；§5 的 P4B1–P4B6 表（**P4B4 的文字就是本次验收标准**）；文末 R4-B 实施计划与主侧裁决（Y-up、整 ms 1..50、历史 128、DS 8 步/100ms + 墙钟信用、SP 首批只插值） |
| 5 | `Docs/plans/net-r4-prediction-contract.md` | AP 帧域/128 历史、DS 预算（8 步/100ms、信用上限 200、初值 50）、SP 只插值、事件证据线、`ApplyAuthority` 在 Frozen/NeedsResync 返回 Stalled |
| 6 | `Docs/plans/net-r4-network-contract.md` | **本批冻结依据**：§B1 承载/上限/流代次/事件契约；§B2 Unity 适配器契约；§B3 主侧集成（含末尾「主侧集成前复核修订」：出生点在墙外 x=±3/z 分行/y=1、Aux.WorldVersion 统一 1、`endpoint.Pump` 前 `Physics.SyncTransforms` 一次、Input 在 Tick 真实接受后 Consume） |
| 7 | `Docs/plans/_r4b_network_report.md` | **冻结的实际 API**（声明面、Driver 公开面、初始化次序、诊断计数）；两个已知阻塞（PMR3RuntimeTest 期望值、Server.csproj 需要处理）与 R4-A 时间轴缺陷记录 |
| 8 | `Docs/plans/_r4b_unity_report.md` | **冻结的实际 API**（三个适配器构造/方法签名、跳跃边沿状态表、接触过滤与饱和策略）；场景几何常量（地板顶 y=0、墙右面 x=0.5）；B3 接线适配器契约（SyncTransforms 由宿主做、WorldVersion 必须一致、allowlist 隔离） |
| 9 | `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md` | bash/PowerShell 边界；命令一律按 Git Bash 写 |
| 10 | `ProjectMecury/.agents/skills/mover-quick-start/SKILL.md` + `references/network-prediction-checklist.md` | 作为**已授权的 C# 移植约束**使用：InputCmd 只放输入意图、SimTick 无帧外副作用、影响模拟的值必须可恢复、断线兜底用墙钟（与 GenerateMove 禁墙钟区分）。按委派要求**未发起问卷、未改 UE** |

**由文档决定的首搜入口**（不是盲搜）：Driver 公开面 → `Client/Assets/Scripts/PMR3/PMR4MovementDriver.cs`；Unity 适配器 → `Client/Assets/Scripts/PMUnity/*.cs`；场景几何/宿主骨架 → `Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs`（`PMR3TestScene`）；身份与帧域 → `PMNet/PMNetIdentity.cs`、`PMNetRole.cs`；票据/引导/入局材料 → `PMNet/Control/PMDsTicket.cs`、`PMNet/Session/PMDsEntryOffer.cs`、`PMNet/Session/PMUdpSessionEndpoint.cs`。

---

## 2. 交付物与写入边界

| 文件 | 动作 | 说明 |
|---|---|---|
| `Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs` | 改 | 每玩家权威 Driver、allowlist 查询、墙外出生、每帧 SyncTransforms + 逐实例 Pump、断线 Freeze/Dispose、释放链；`PMR3TestScene.TryCollectColliders` 新增 |
| `Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs` | 改 | AP/SP 全量接线、子步驱动、接受后 Consume 边沿、只插值的 SP、失败 Freeze、释放链、诊断输出 |
| `Server/Server.csproj` | 改 | PMR3 glob 用 `Exclude` 排除 `PMR4MovementDriver.cs`/`PMR4MovementCodec.cs` |
| `Tools/PMR3RuntimeTest/Program.cs` | 改 | 位宽/属性数 2→3 + 新复制字段真实注册断言 + 4 条运动 RPC 注册断言（原用例全保留） |
| `Tools/PMClientCheck/PMClientCheck.csproj` | 改 | 真编 `PMPrediction/**`、`PMMover/**`、`PMR4MovementCodec/Driver`、`PMUnity/**` |
| `Tools/PMClientCheck/ClientStubs.cs` | 改 | 补 `PhysicsScene`（`IsValid()` 是方法、批量 `CapsuleCast`/`OverlapCapsule`）、`Physics.defaultPhysicsScene`/`SyncTransforms`、`Camera.clearFlags`/`CameraClearFlags` |
| `Tools/PMUnityGlueCheck/PMUnityGlueCheck.csproj` | 改 | 真编新核心/Driver/适配 + `PMClientSessionHost.cs` + `Loging.cs` |
| `Tools/PMUnityGlueCheck/UnityStubs.cs` | 改 | 补 Vector3 运算符/up/down、Quaternion、Bounds、RaycastHit、Collider.bounds、PhysicsScene/Physics、Camera/CameraClearFlags、Input/KeyCode、Transform.parent/rotation、Application.isPlaying、Time.deltaTime、`Object.DestroyImmediate`；新增旧 socket 边界桩 `Server.UDPSocketManger.CloseExisting()` |
| `Tools/PMR4UnityCheck/PMR4UnityCheck.csproj` | 改 | 引**真实 Unity2019 DLL**，扩为真编两个宿主 + 全部新核心（PMNet/PMPrediction/PMMover/PMR3/PMUnity/Loging/PMDsLobbyAgent） |
| `Tools/PMR4UnityCheck/HostDependencies.cs` | 新增 | 旧链唯一接触点的最小替身（`Server.UDPSocketManger.CloseExisting()`，签名与真实实现逐字一致）；**不替** Driver/Physics/Session |
| `Tools/PMR4IntegrationTest/Program.cs` | 新增 | 真实回环 UDP 集成门禁（A–G 七节 113 项） |
| `Tools/PMR4IntegrationTest/PMR4IntegrationTest.csproj` | 新增 | net8 工程，真编 PMNet/PMPrediction/PMMover/PMR3 |
| `Docs/plans/_r4b_host_integration_report.md` | 新增 | 本报告 |

**未写**（逐项确认只读）：`PMR3/PMR4MovementDriver.cs`、`PMR3/PMR4MovementCodec.cs`、`PMR3/PMR3Player.cs`、`PM3/Generated/*.g.cs`、`PMPrediction/**`、`PMMover/**`、`PMUnity/**`、`Client/Assets/Editor/PMR4UnityValidation.cs`、主计划与两份契约文档、任何 UE 工程文件、任何 `.meta`（未新增 Unity 资产，无需 meta）。未执行 svn/git 提交、未编译 UE、未启动第二个 Unity 实例、未抢编辑器锁。

---

## 3. 主机接线的精确调用次序

### 3.1 DS 侧（`PMDsSessionHost`）

启动（一次性，全部在主线程）：
```
读引导文件 → 校验 matchid/dsid/epoch/协议摘要/碰撞摘要 → 收集名册(uid→行/队)
→ Register() → world/bridge/Attach → PMR3TestScene.Build(不可见 BoxCollider)
→ TryCollectColliders(2) → 组 layerMask → new PMUnityMoverCollisionQuery(mask, 1, allowlist)
   （allowlist != 2 或 WorldVersion != 1 或没有白名单 ⇒ 拒绝启动）
→ PMUdpSessionEndpoint.OpenServer → 订阅 Connected → PMDsLobbyAgent.Start → Ready/Heartbeat
```

每帧 `Pump()`：
```
Physics.SyncTransforms()                 ← 契约要求：每帧一次，且在 endpoint.Pump 之前
_endpoint.Pump(nowMs, unix)              ← 含 bridge.Update（不再二次调用）
PumpMovement(nowMs):
    elapsedMs = nowMs - lastPumpMs
    hostTickId++; serverFrame++          ← DS 自己的 AuthorityServer 帧域
    foreach driver: driver.Update(elapsedMs)
                    driver.Pump(elapsedMs, serverFrame, hostTickId)
PruneDisconnectedDrivers()（在 CheckPlayerDrops 内）：
    OwnerConnection == null || !IsReady ⇒ driver.Freeze(); driver.Dispose();
CheckSmoke / lobby.Pump / 心跳日志
```

`Connected(connection)`（由 `endpoint.Pump` 的接收段调用，**早于**同一次 Pump 内的 `bridge.Update`）：
```
SpawnPlayer(world, bridge, connection)
BuildSpawnSync(uid)  → x = ±3（team 1→-，2→+，未知按行奇偶）, z = 行居中×1.5, y = 1.0
new PMR4MovementDriver(player, query, Authority, epoch, initialSync, aux)
driver.PublishInitialSnapshot()           ← 必须在首次 Flush 前
```

`Dispose()`：drivers(Freeze→Dispose) → lobby → endpoint(退订事件) → Detach(world) → 还原 Warn → bridge → 场景对象。

### 3.2 客户端侧（`PMClientSessionHost`）

`Enter(offer)`：摘要校验 → 世界/桥/Attach → `PMR3TestScene.Build(可见)` → `TryCollectColliders` + `PMUnityMoverCollisionQuery(mask, 1, allowlist)` + `new PMUnityMoverInput()` → 关旧战斗 UDP socket → `OpenClient` → 订阅 `PMR3Runtime.PlayerReplicated` → 起驱动组件。

每帧 `PumpActive()`：
```
if (Faulted) { FrozenWallClockPump(); return; }        ← 失败后只推墙钟（只 driver.Update）
Physics.SyncTransforms()                               ← 每帧一次，且在 endpoint.Pump 之前
_endpoint.Pump(nowMs, unix)                            ← 入站/校正（OnRep/RPC 只入队）
PumpMovement(Time.deltaTime*1000):
    foreach rig: driver.Update(elapsedMs)
    acc += elapsedMs（上限 200ms）
    while (steps < 8 && acc >= 1):
        stepMs = clamp(floor(acc), 1, 50)
        foreach AP rig:
            input = Input.Sample(stepMs)               ← 采样但不消费边沿
            r = driver.Tick(stepMs, input, serverFrame)
            if (!r.Accepted) break 本帧（不消费、不从 acc 扣时间、保留边沿）
            Input.Consume(stepMs)                      ← 接受后才消费
            driver.SendInputPayload()
        acc -= stepMs; steps++
    foreach SP rig: driver.Advance(elapsedMs); s = driver.SamplePresentation();
                    if (s.HasValue) presentation.ApplyInterpolated(s.Sync)
    foreach AP rig: presentation.ApplyPredicted(driver.GetPredictedSync())
SendPendingProbes()                                     ← 探针语义不变
```

`OnPlayerReplicated(player)`：`isOwner = (Role == AutonomousProxy && player.Uid == offer.uid)`（不一致告警并降为观察副本）→ **对全部副本**建 `PMR4MovementDriver.CreateFromPlayerInitialSnapshot(player, query, epoch, out err)` + `PMUnityMoverPresentation(label, role, createTestCamera: isOwner)` → 仅 owner 排队一次上行探针。

`Fail()`：`Faulted = true` + `FreezeMovements()`（全部 driver.Freeze）。`Stop()`：退订 static 事件 → 各 rig（表现 Dispose → driver Freeze+Dispose）→ 端点 → Detach → 桥 → 场景对象 → 输入 Reset。

---

## 4. 使用的冻结 API（B1/B2 实际签名）

- Driver 构造：`PMR4MovementDriver(PMR3Player, IPMMoverCollisionQuery, PMNetRole, uint epoch, PMMoverSyncState, PMMoverAuxState, long initialOutputFrame = 0, PMFrameId initialServerFrame = default, double initialTotalSimTimeMs = 0, int historyCapacity = 128, uint streamVersion = 1)`。
- 客户端工厂：`PMR4MovementDriver.CreateFromPlayerInitialSnapshot(PMR3Player, IPMMoverCollisionQuery, uint epoch, out string error)`。
- DS：`PublishInitialSnapshot()`、`Pump(double elapsedMs, PMFrameId serverFrame, long hostTickId)`、`ServerSubmitTrustedEffect(PMMoverEffectRequest)`、`BeginServerResync(string)`、`GetAuthoritativeSync/Aux()`。
- AP：`Tick(int stepMs, PMMoverInput, PMFrameId) -> PMTickResult{Accepted,Reject,NeedsResync}`、`SendInputPayload()`、`GetPredictedSync()`、`Timeline`（`ReplayedSteps`/`ConfirmedEventsEmitted`）、`OutputBoundary`/`ConfirmedBoundary`/`UnackedInputCount`、`event EventDispatched`。
- SP：`Advance(double deltaMs)`、`SamplePresentation() -> PMInterpolatedState{Sync,HasValue}`、`Interpolation.AuthorityAccepted`。
- 通用：`Update(double wallElapsedMs)`、`Freeze()`、`Dispose()`、`Describe()`、诊断计数（`SnapshotPayloadsApplied`/`InputRejectedStream`/`SnapshotRejectedStreamNewer`/`SnapshotRejectedIdentity`/`EventBatchesAccepted`…）。
- 声明面写入/发送：`PMR3Player.PublishMovementSnapshot(byte[])`、`PMNet_ServerMovementInputV1(byte[])`（测试用它注入旧流输入，走真实生成桩）；`PMR3Player.MovementSnapshotPayload`（只读借出）。
- Unity 适配器：`new PMUnityMoverCollisionQuery(int layerMask, int worldVersion, Collider[] allowlist)`、`PMUnityMoverInput.Sample/Consume/Reset`、`PMUnityMoverPresentation(label, role, createTestCamera)` + `ApplyPredicted/ApplyInterpolated/Dispose`。
- 常量：`PMDsSessionHost.MovementWorldVersion = 1`、`PMClientSessionHost.MovementWorldVersion = 1`（两侧必须同值）。

---

## 5. 消费者工程改动与理由

1. **`Server/Server.csproj`**：PMR3 glob 加 `Exclude="…PMR4MovementDriver.cs;…PMR4MovementCodec.cs"`。理由：Lobby 是局外（会话/匹配/DS 编排），**不参与仿真**；它对 PMR3 的依赖全在声明层，而声明层早已留好 `IPMMovementNetworkDriver` 接缝。另一条可选路（把 PMPrediction/PMMover 也链进 Lobby）会让 Lobby 白背整套预测/运动数学与 Unity 语言面约束。已核实 `Server/**` 内**无任何** `PMR4Movement*` 引用（`grep` 为 0），排除不会漏编译。
2. **`Tools/PMR3RuntimeTest`**：期望 2→3；并新增断言「描述符里真的注册了 `_movementSnapshotV1`」（PushBased / Condition=None / OnRepMethodId≠0 / SetterName==`PMNet_Set_movementSnapshotV1`）与 4 条新运动 RPC 的方向/可靠性/校验档位。只改数字会退化成「数字对不对」，断言成员名才能证明它就是那个新字段。**原用例一条未删**（171 → 180）。
3. **`Tools/PMClientCheck`**：需要真编新核心/Driver/适配/两个宿主（宿主已在 `Scripts/Server/**` 通配里），因此补 `PMPrediction/**`、`PMMover/**`、两个 PMR3 实现文件、`PMUnity/**`；`ClientStubs.cs` 补 `PhysicsScene`/`Physics`/`Camera.clearFlags` 等必要面（**未替任何本项目新类型**）。
4. **`Tools/PMUnityGlueCheck`**：同上，并把 `PMClientSessionHost.cs` 与真实 `Loging.cs` 编进来；`UnityStubs.cs` 补齐适配器/宿主所需的 Unity 面，并新增**旧 socket 边界桩**（`Server.UDPSocketManger.CloseExisting()` —— 客户端宿主对旧链的唯一接触点）。
5. **`Tools/PMR4UnityCheck`**：改为引**真实 Unity 2019.4 DLL** 编译两个宿主 + 全部新核心；只用一个独立文件 `HostDependencies.cs` 替旧 socket 边界。**签名真实门禁未被弱化**：产物里可查到 `PMDsSessionHost`/`PMClientSessionHost`/`PMR4MovementDriver`/`PMUnityMover*`/`UDPSocketManger`。

---

## 6. `Tools/PMR4IntegrationTest` 场景清单（113 项）

| 节 | 覆盖 |
|---|---|
| A（24） | 真实 `PMDsMatchKey`/`PMDsTicketIssuer` 签发、引导文件 Encode/TryDecode、offer `PMDS1:` 编解码往返、`OpenServer`（系统分配端口）、两个 `OpenClient`、真实握手完成、`HandshakeServer.Accepted==2`、`VerifyFailures==0` |
| B（28） | DS 两名册副本 + 两个 Driver、两个 Driver 都发布初值；两个客户端各复制到 2 个副本、各 1 AP + 1 SP；**所有副本都有非空 Driver**（旧实现会在此失败）；SP 无预测时间轴；出生 |x|=3 且 y=半高、z=行居中、两人在墙的两侧/不同行、都在墙碰撞范围外；AP epoch/streamVersion/输出边界/初值载荷 |
| C（16） | 24 步真实 UDP 往返后 DS 消费全部输入、权威边界推进、AP 边界/确认边界跟进、AP 应用快照、AP 预测与 DS 权威收敛、Δz≈1.148m（与手算一致）、速度达上限、客户端 B 反向位移、**对方 SP 收到并采样到权威状态**、时间轴不广播事件、未确认窗口退役 |
| D（10） | 跳跃边沿只在 Tick 接受后消费；可靠事件批到达、无解码失败、真的派发；含 Mode 变化与落地两类 kind；事件 key 不重复派发、序号无缺口、日志未失败；快照与事件两条链都在工作 |
| E（4） | DS `ServerSubmitTrustedEffect(Teleport)` 改变权威位置（Δz>3m）→ AP `ReplayedSteps` 增长（回滚重放）→ 预测与权威重新收敛 |
| F（15） | DS `BeginServerResync` 升流 → AP `ResyncApplied` 且流代次对齐；**未重绑前的普通新流快照被拒**；**旧流输入**用真实 codec 编码 + 真实生成桩 + 真实 UDP 发送 → DS `InputRejectedStream` 增长；**旧流快照**用真实 `PublishMovementSnapshot` 经真实复制发出 → AP `SnapshotRejectedIdentity` 增长且状态不回写；重绑后继续推进/边界增长/再次收敛 |
| G（16） | 客户端 B 静默后按**逐步推进时钟**让真实空闲超时剪除其会话（DS 连接数 2→1）；`PruneDisconnectedDrivers` 剪除 1 个 Driver 且该 uid 记入 Frozen（**先 Freeze 再 Dispose**）；player 引用被摘除；客户端侧 Freeze/幂等 Dispose；Stop 释放全部运动链；世界从 `PMR3Runtime` 摘除（`GetBridge==null`）；宿主 Stop 退订后 static 事件清空；重复释放不抛 |

---

## 7. 运行证据（真实执行）

```bash
# 需要先 build 再 run（计划 §9.5 #31 的坑：编译失败仍跑旧二进制会打印全绿）
dotnet build Server/Server.csproj -c Release -v q -nologo                       # 0 错误
dotnet build Tools/PMClientCheck        -c Release -v q -nologo                 # 0 错误
dotnet build Tools/PMUnityGlueCheck     -c Release -v q -nologo                 # 0 错误
dotnet build Tools/PMR4UnityCheck       -c Release -v q -nologo                 # 0 错误（真实 Unity DLL）
dotnet build Tools/PMR3RuntimeTest      -c Release -v q -nologo                 # 0 错误
dotnet build Tools/PMR4IntegrationTest  -c Release -v q -nologo                 # 0 错误

dotnet Tools/PMR3RuntimeTest/bin/Release/net8.0/PMR3RuntimeTest.dll             # 180 项 0 失败 exit 0
dotnet Tools/PMR3IntegrationTest/bin/Release/net8.0/PMR3IntegrationTest.dll     # 143 项 0 失败 exit 0
dotnet Tools/PMR4NetworkTest/bin/Release/net8.0/PMR4NetworkTest.dll             # PASS exit 0
dotnet Tools/PMR4IntegrationTest/bin/Release/net8.0/PMR4IntegrationTest.dll     # 113 项 0 失败 exit 0
```

| 门禁 | 结果 |
|---|---|
| `Tools/PMR4IntegrationTest`（新增） | **113 项 0 失败，exit 0** |
| `Tools/PMR3RuntimeTest` | **180 项 0 失败**（原 171，新增 9 项断言） |
| `Tools/PMR3IntegrationTest` | **143 项 0 失败** |
| `Tools/PMR4NetworkTest`（B1 回归） | **PASS** |
| `Tools/PMPredictionTest` | 453 / 0 |
| `Tools/PMMoverTest` | 245 / 0 |
| `Tools/PMMoverPredictionTest` | 264 / 0 |
| `Tools/PMNetE2E` / `PMReplicationTest` / `PMDeclCheck` | PASS |
| `Tools/PMNetWorldTest` / `PMNetSessionTest` / `PMTransportTest` | 197 / 405 / 173，全 0 失败 |
| `Tools/PMCallspaceCheck` / `PMDsControlTest` / `PMDsLobbyTest` | 93 / 623 / 268，全 0 失败 |
| `Tools/PMUdpRouterTest` / `PMUdpAdmissionTest` | 44 / 360，全 0 失败 |
| `Tools/PMNetLangCheck` / `PMSharedConfigCheck` / `PMHeroDataCheck` | 构建 0 错误 |
| `Server/Server.csproj`、`Tools/PMClientCheck`、`Tools/PMUnityGlueCheck`、`Tools/PMR4UnityCheck` | 构建 **0 错误** |

`PMR4IntegrationTest` 的关键诊断输出（节选，说明不是「碰巧没动」）：
```
[diag] DS uidA : role=Authority epoch=19457 instance=1 stream=1 boundary=24 nextInput=24
                creditMs=194 queued=0 totalMs=384 eventSeq=0
[diag] AP(A)   : role=AutonomousProxy pending=24 confirmed=24 unacked=0 journalFailed=False frozen=False
[diag] DS sync : pos=(-3, 1, 0.39816016) vel=(0, 0, 3.9) mode=Walking maxSpeed=3.9 accel=20
```
（出生 z=-0.75 → 权威 z=0.398 = 位移 1.148m，与 Walking `MoveTowards(+20 m/s², MaxSpeed 3.9)` 的 24×16ms 手算值逐位吻合。）

---

## 8. 负向验证（门禁不是空转）

| # | 注入 | 期望通道 | 实测 |
|---|---|---|---|
| N1 | 复刻旧缺陷：`OnReplicated` 里 `if (Role != AutonomousProxy) return;`（SP 被跳过） | PMR4IntegrationTest | **抓**：8 项失败（B4/B5/B7/B9/B10/B11/B16/C11） |
| N2 | 断线收尾去掉 `driver.Freeze()`（只 Dispose） | PMR4IntegrationTest | **抓**：G5 失败（FrozenUids 不含该 uid） |

两次注入后负向运行 **exit 1 / 9 FAIL**；随后按备份还原，**sha256 与注入前逐字节一致**（`e8dbeff5974b918cad70893bc47b7be072807ac7bfee10d4e8fcf46c1e8ba829`），`NEG-INJECT` 标记 0 个，还原后重新 build（0 错误）并重跑 **113/0 exit 0**。

---

## 9. 用户菜单与重新构建步骤（Unity 侧，P4B6）

> 本 Agent 未抢编辑器锁、未启动第二个 Unity 实例；以下步骤需要用户执行。

1. **重新 Build DS**（当前 `HyldDS/HyldDS.exe` 是旧版本，**不包含本次宿主接线**）：
   Unity 菜单 `Build / Build HyldDS (Windows Headless)`，或命令行
   `-executeMethod PMDsBuild.BuildWindowsHeadlessDs`。产物 `HyldDS/HyldDS.exe` + `run_ds.bat`。
2. **（可选，B2 遗留）真实 PhysX 适配验证**：菜单 `Tools/PMR4/验证 Unity 碰撞适配（真实 PhysX）`（临时隔离 PhysicsScene、不碰用户场景）；Play 模式下再点一次可得表现层证据。
3. **跑一局**：启动 Lobby（`dotnet Server/bin/Debug/net8.0/Server.dll`）→ 两个客户端登录并联机匹配进入 `PMDS1` 局；DS 由 Lobby 拉起并携带 `-bootstrap`。
4. **现场观察点**：
   - 客户端：`Enter` 后应出现可见胶囊（本地 owner 节点下还有一个测试相机，旧 `HYLDCameraManger` 不受影响）；**WASD 移动、Space 跳跃**；
   - 日志 `[PMClientSessionHost] movementRigs=2(ap=1,sp=1) snapshots=… inputsSent=…`（心跳每 5 s）；
   - DS 日志 `[PMDsSessionHost] heartbeat … movementDrivers=2 hostTick=…` 与 `R4-B 运动接线：碰撞白名单=2（地板+墙） WorldVersion=1 …`、`R4-B 出生规则：x=±3（墙外）…（禁止默认原点：原点在测试墙盒内部）`。
5. **预期行为**：AP 立即响应输入（本地预测）；DS 权威角色不穿墙（墙盒 x∈[-0.5,0.5]）；对方角色（SP）平滑插值跟随；断线时该角色两侧都冻结（不再继续外推）。

---

## 10. 诚实边界与剩余风险

**已证**：两个宿主在**真实 Unity 2019.4 程序集**上可编译；接线次序与契约逐条对应；两个客户端在**真实回环 UDP**上各自 AP、互见 SP 并收敛；跳跃事件经**可靠通道**到达且独立于快照游标；DS 可信效果造成权威差异后 AP 回滚重放并再收敛；重同步升流时新流快照先被拒、旧流输入被 DS 丢、旧流快照不回写、重绑后继续收敛；断线先 Freeze 再 Dispose；释放链完整且幂等。以上均由 `PMR4IntegrationTest` 113/0 与两处缺陷注入（被抓 + 逐字节还原）支撑。

**未证 / 剩余风险**（不得据此宣称 P4B4 全部完成）：

1. **真实 PhysX 未运行**：net8 用确定性 AABB 替身（`PMMoverTestWorld`）。Unity 侧 `PMUnityMoverCollisionQuery` 的**几何语义**（尤其「正好贴地时水平扫掠是否被地面报为阻挡」「起点重叠是否返回 t=0」「饱和是否显式失败」）仍只能在 Unity 内跑（B2 的菜单 + P4B6）。两侧 `WorldVersion` 的**一致性**已被真实走到（快照版本不符会拒绝并请求重同步）。
2. **真实 DS 进程与真实 Unity 客户端未跑**：`HyldDs/HyldDS.exe` 是旧版本，必须由用户重新 Build 才含本次接线；T42 的客户端 UnityUI 分流与跨机项仍未完成。
3. **表现层语义未运行验证**：`PMUnityMoverPresentation.Apply/Dispose`、测试相机只做了真实编译 + 构造/调用面验证；「Play 模式下表现层用例」需用户点菜单（B2 已注明为 PENDING_USER）。
4. **输入采样语义未在 Unity 内验证**：`PMUnityMoverInput`（WASD/Space/虚拟轴回退/一次按下只锁一次）在 net8 上不可运行，本次只保证**宿主的消费次序**（接受后才 Consume）与编译面。真实键位与 InputManager 行为待实测。
5. **宿主子步算法的门禁口径**：`PumpActive` 的「整 ms 余数累加 / 1..50 / 每 Update 最多 8 步」只由真实编译 + 代码审查保证（net8 门禁无法编译含 `UnityEngine` 的静态宿主）。集成门禁里的子步是**显式 16ms 步**，两条路径的等价性依赖代码审查，未做逐位对拍。
6. **重同步事件与 Resync 的消费次序**：契约「主侧集成前复核修订」要求覆盖「同一次 Update 收旧流事件→Resync2→流2事件→Resync3→流3事件」。本批未构造该序列（属 B1 Driver 的事件日志域，B1 已在内存投递下覆盖同族场景）；真实网络下的多变升流交错仍建议后续补一条窄门禁。
7. **多升流噪声**：F 节会真实触发 `BeginServerResync`（日志会出现 `DS 重同步` / `AP 请求重同步` 告警行），属**预期噪声**，不是失败。
8. **`UnityStubs.cs` 行尾归一**：该文件在 HEAD 处是 LF 且无 BOM（含中文），本次按任务要求（所有源中文 BOM+CRLF）与仓库主流约定（`Client/Assets/Scripts` 下 112/122 为 CRLF）转为 **UTF-8+BOM+CRLF**，因此该文件呈现整文件 diff。`.csproj` 保持仓库既有约定（LF、无 BOM）。
