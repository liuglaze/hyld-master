# R4-B 实际宿主 只读对抗审查（final review）

> 范围：**只读**审查 R4-B/B3 的两个实际生产宿主 `PMDsSessionHost`（DS）与 `PMClientSessionHost`（客户端），
> 以及回答问题所必需的 PMR4 Driver / PMUnity 三个适配器 / Session Endpoint / 生命周期应用路径的直接引用。
> 不接手实施、不改代码、不编译 UE、不操作 Unity、不递归委派、不扩大 R5/旧业务。
> 主侧刚做的两处改动（**AP 不再自造 AuthorityServer 帧**、**每渲染帧 `PollHardware`**）按**当前源码**审查，
> 不采信旧 `_r4b_host_integration_report.md` 的描述。
> 结论口径：net8 门禁（`Tools/PMR4IntegrationTest`，113/0）证明的是**核心 + 声明层**，
> 其宿主接线是测试工程自带的另一套驱动，**不能**当作这两个 Unity 宿主的逻辑证明。

---

## 0. 摘要（≤1500 字）

四类被点名的风险逐项结论：

1. **阻断 Unity 运行**：**未发现**。两个宿主各自只有唯一驱动点（`PMDsHost.cs:389` 是 `PMDsSessionHost.Pump()` 的
   唯一调用者；`PMClientSessionHostDriver.Update` 是 `PumpActive` 的唯一调用者），
   `Pump` 内 `bridge.Update` 只被 `endpoint.Pump` 调一次（无重复预算消费）；子步上限 8、累加器钳 200ms、
   命中缓冲上限 32 且**饱和显式抛异常**；无任何无界循环。另见 D1/D3：都不是"卡死 Unity"，最坏是**整局显式失败**。
2. **帧域混淆**：**未发现**，本次修复有效。AP 传入 `Tick` 的 `serverFrame` 取自
   `Timeline.GetSnapshot().ServerFrame`（`PMClientSessionHost.cs:565`），该值只可能来自最近一次权威 apply/step 记录，
   且 `PMPredictionTimeline.EnsureServerFrameDomain` 对 `PMFrameId.None` **放行**（`PMPredictionTimeline.cs:957-963`），
   因此"首个权威帧到达前"（DS 初值 `ServerFrame=0` → `ToAuthorityFrame` → `None`）不会抛；
   DS 侧反向只接受 `AuthorityServer` 命名空间（`PMR4MovementDriver.cs:1216-1221`），且 DS 水位自持
   （`_authority.Resync(epoch, _instanceId, _authorityBoundary)`），不吃客户端自报帧。
3. **断线继续模拟**：**未发现可达的"无限继续"**，但存在一个有界窗口 + 一处潜在静默失效（D5）。
   客户端链路：传输层空闲看门狗 10s（`PMTransportTypes.cs:140` → `PMTransport.cs:385-393`，判定在发送预算检查**之前**）
   → 连接失活 → `CheckClientSessionAfterUpdate` → `RaiseFailed` → 宿主 `Fail` → `FreezeMovements`；
   冻结后每帧**只** `Driver.Update`（不再 Tick/Advance/Apply，`PMR4MovementDriver.cs:1487-1494` SP 侧 `_frozen` 早退）。
   DS 链路：`PruneDisconnectedDrivers` **先 Freeze 再 Dispose**（`PMDsSessionHost.cs:752-800`），
   且 `CheckPlayerDrops`（`:939`）在任何早退判定**之前**无条件先调它。
4. **初始化早于 Create**：**成立**。DS 的 `Connected` 由接收段 `ServerHandshake`→`RaiseConnected` 触发，
   早于同一次 `Pump` 里的 `bridge.Update`；Create 记录的 `RepInitialState` 是 flush 时**现取**
   （`PMNetWorld.cs:585-589` 明确注释"发那一刻的值"）；客户端 `ApplyCreate` 先应用 `InitialState`/`RepInitialState`
   再调 `OnReplicatedCreate`（`PMNetWorld.cs:729-745`）→ 宿主建 Driver 时 `player.MovementSnapshotPayload` 已就绪。
5. **双驱动**：**未发现真实双驱动**（无第二个 `bridge.Update`、无旧链共存、无 `MovementSnapshotUpdated` 双路应用、
   `Ensure`/`Pump` 均单点）。但 D2（角色降级路径）与 P3（多 AP 共用一个 `session.Input`）是两条边界问题。

**已确认缺陷 5 条**（均为"非阻断、可控"级别，唯一中危是 D3）：
D1 Enter 场景构建失败分支漏还原静态 `Warn` 且不释放 Bridge（`PMClientSessionHost.cs:239-245`）；
D2 角色/uid 不一致降级后造出"角色 AP 被当 SP 驱动"的 rig，立即以误导信息整局失败（`:664-675` + `:513` + `PMR4MovementDriver.cs:2320-2326`）；
D3 碰撞隔离只靠**后置**白名单，layer 掩码取自白名单自身层（Default=0），PhysX 命中饱和在**过滤之前**抛异常 →
   真实大厅场景同层碰撞体可能整局失败（`PMDsSessionHost.cs:531-545` / `PMClientSessionHost.cs:256-280` +
   `PMUnityMoverCollisionQuery.cs:88/356/410`）；net8 门禁的替身世界只有 2 个 Collider，**结构上不可能**暴露它；
D4 AP 每子步 `Timeline.GetSnapshot()` 仅为取一个 `ServerFrame`，却深克隆 Sync/Aux（含 `ActiveLayers` 数组）→ GC 尖峰；
D5 端点自身 `_disposed` 置位后 `Pump` 直接返回，既不 `ClientFailed` 也不走 `CheckClientSessionAfterUpdate` → 潜在"不 Pump 但一直预测"（当前仅可由自身 `Dispose` 触发，实际近不可达）。

最严重的实际风险是 **D3 + P1**：这正是"新 UDP 门禁 PASS ≠ 实际宿主逻辑"的落点。

---

## 1. 已确认（代码证据充分）

### C1 无 Unity 阻断缺陷（唯一驱动点 / 次序 / 有界）

| 断言 | 证据 |
|---|---|
| DS 唯一驱动点，不与旧诊断路由并存 | `PMDsHost.cs:387-393`：`if (_sessionHost != null) { _sessionHost.Pump(); DrainPendingLogs(); return; }`；全仓 `\.Pump()` 仅此一处（`grep` 0 例外） |
| 客户端唯一驱动点 | `PMClientSessionHostDriver.Update` → `PumpActive()`（`PMClientSessionHost.cs:365`）；`PumpActive` 仅在本文件内被调用 |
| `bridge.Update` 不重复消费预算 | `PMDsSessionHost.cs:668` 与 `PMClientSessionHost.cs:388` 都只调 `_endpoint.Pump(...)`，注释与 `PMUdpSessionEndpoint.Pump:354-381` 一致（`bridge.Update` 在其内部） |
| `Physics.SyncTransforms()` 每帧一次且在 `endpoint.Pump` 之前 | DS：`PMDsSessionHost.cs:666`（在 `:668` 之前）；客户端：`PMClientSessionHost.cs:386`（在 `:388` 之前）；适配器自身从不调用（`PMUnityMoverCollisionQuery.cs:26-29` 声明 + 无调用点） |
| 子步/预算有界 | `MaxSubstepsPerUpdate=8`、`MinStepMs=1`、`MaxStepMs=50`、`MaxStepAccumulatorMs=200`（`PMClientSessionHost.cs` 常量与 `:474-500` 循环）；DS 信用由 `PMAuthorityInputBuffer.Pump` 内 8 步/100ms + 墙钟信用封顶 |
| 同一宿主 tick 不重复充值 | `PMR4MovementDriver.cs:1236-1241`（`hostTickId == _lastHostTickId` → 零步返回 + `DuplicateHostTickRejections++`）；DS 侧 `_movementHostTickId++` 与 `_movementServerFrame` 同步递增（`PMDsSessionHost.cs:722-723`） |
| 查询饱和显式失败 | `HitBufferCapacity=32`（`PMUnityMoverCollisionQuery.cs:88`）、`RequireNotSaturated`（`:356`/`:410`） |
| 无死循环/无递归自驱 | 无 `while(true)`、无 `InvokeRepeating`、无自触发事件；`PMClientSessionHostDriver` 不驱动自己 |

### C2 初始化早于 Create 成立（正面结论，非缺陷）

1. DS 侧订阅与创建：`_endpoint.Connected += OnConnected`（`PMDsSessionHost.cs:580`）→ `OnConnected`（`:844`）
   → `SpawnPlayer` → `AttachMovementDriver`（`:887`）→ `driver.PublishInitialSnapshot()`（`:913`）。
2. 时序保证：`Connected` 由 `PMUdpSessionEndpoint.ServerHandshake`（`:587`）→ `RaiseConnected`（`:649`）触发，
   发生在 `Pump` 的 `ReceiveDatagrams` 段；`bridge.Update` 在同一 `Pump` 里位于其后（`:371-379`）。
3. Create 载体：`PMNetWorld.cs:585-589`——"初始状态在这里现取（而不是生成时快照）：对端要的是「发那一刻的值」"，
   `rec.RepInitialState = BuildReplicatedInitialState(obj, connection)` 在 **flush 时**构造。
4. 客户端应用次序：`PMNetWorld.ApplyCreate`（`:659`）先 `OnDeserializeInitialState`（`:720`）
   再 `TryApplyReplicatedInitialState`（`:734-741`），**最后**才 `obj.OnReplicatedCreate()`（`:745`）
   （注释即 RV5/D-R0-16）；`PMR3Player.OnReplicatedCreate`（`PMR3Player.cs:211`）→ `NotifyPlayerReplicated` → 宿主
   `EnsureMovementRig` → `CreateFromPlayerInitialSnapshot` 读 `player.MovementSnapshotPayload` 已非空。
5. 承载字段唯一性：`OnRep_MovementSnapshot`（`PMR3Player.cs:172-186`）→ `MovementDriver.OnSnapshotReplicated()`；
   全仓无 `MovementSnapshotUpdated` 订阅者 ⇒ 快照只经一条路进 Driver，**无双重应用**。

### C3 帧域未混淆（本次 AP 修复有效）

- AP 取样点：`PMClientSessionHost.cs:565` `PMFrameId serverFrame = rig.Driver.Timeline.GetSnapshot().ServerFrame;`
  → `PMR4MovementDriver.Tick`（`:917`）→ `_timeline.Tick`，全程无"自造 AuthorityServer 帧"的计数器。
- 合法性：`PMPredictionTimeline.EnsureServerFrameDomain`（`:957-963`）`if (serverFrame.IsValid && Domain != AuthorityServer) throw`
  ⇒ `None` 合法通过；`GetSnapshot()` = `BuildSnapshot(PendingFrame)`（`:823-825`），
  `GetBoundaryServerFrame`（`:1458-1462`）在 `index<0` 时回 `_baseServerFrame`（初始为 `initialSnapshot.ServerFrame`）。
- 初值语义：DS 在首次 `PumpMovement` 之前发布初值，此时 `_authorityServerFrame == PMFrameId.None`
  ⇒ `blob.ServerFrame = 0`（`PMR4MovementDriver.cs:872`）⇒ 客户端 `ToAuthorityFrame(0) → PMFrameId.None`（`:2175-2177`）。
  即"首权威帧到达前 AP 步进带 `None`"，属**有意且合法**，不是帧域污染。
- DS 反向：`Pump` 拒绝非 `AuthorityServer` 的 `serverFrame`（`:1216-1221`）；水位只由 DS 自己 `Resync`（`BeginServerResync :1439`、内部 `_authority.Resync(_epoch, _instanceId, _authorityBoundary) :1466`）。
- 上行不含任何服务器帧号（`TryBuildInputPayload` 只编码 `epoch/instance/stream/entries`，`:1008-1045`）⇒ 客户端自报帧无法影响 DS 进度。

### C4 断线不继续模拟（有界 + 先 Freeze）

- 客户端存活判定链：`PMTransport.IdleTimeoutMs=10000`（`PMTransportTypes.cs:140`）；判定在
  `PMTransport.Update` 中位于 `budget<=0` 早退**之前**（`PMTransport.cs:385-399`），因此数据报预算耗尽也不会失去看门狗；
  `NormalizeClock` 处理时钟倒退（`:409+`）。
- 客户端失败链：`PMTransportConnection.IsReady = _activated && Transport.IsConnected`（`:508`）
  → `PMUdpSessionEndpoint.CheckClientSessionAfterUpdate`（`:849-863`）`RaiseFailed`
  → 宿主 `OnEndpointFailed`（`PMClientSessionHost.cs:736-750`，条件 `ClientConnection != null && !IsReady`）
  → `Fail`（`:756`）→ `FreezeMovements`（`:774`）。
- 冻结语义：`PumpActive` 在 `Faulted` 时只走 `FrozenWallClockPump`（`:435-450`）→ **只** `Driver.Update`；
  SP 侧 `Advance` 有 `_frozen` 早退（`PMR4MovementDriver.cs:1478-1494`）；AP 侧 `Freeze()` 清空入站队列并冻结时间轴（`:2205-2224`）。
- DS 冻结+释放链：`PruneDisconnectedDrivers`（`PMDsSessionHost.cs:752-800`）判定 `OwnerConnection == null || !IsReady`
  → `driver.Freeze(); driver.Dispose();`（顺序与契约 B3 一致），由 `CheckPlayerDrops`（`:939-941`）**先无条件调用**，
  不受 `_playerDropReported` 早退影响。

### C5 清理链基本完整

- 客户端 `Stop()`（`:329-360`）：置 `_active=null` → 退订 `PlayerReplicated` → `Endpoint.Dispose()` → `ReleaseSession`
  → `PMClientSessionHostDriver.Release()`；`ReleaseMovements`（`:793`）顺序为 表现 `Dispose` → 驱动 `Freeze`+`Dispose`
  → 清 `Movements`/累加器/`Query`/`Input`；`ReleaseSession`（`:839`）为 Detach → 还原 `Warn` → `bridge.Dispose`
  → 销毁场景对象 → 清各表；幂等由 `_stopping` + `_active==null` 双闸。
- DS `Dispose()`（`:1076-1200`）：先 `Freeze`+`Dispose` 每个 driver（注释明确"脱离 player 引用"），再 `lobby`
  → `endpoint`（含事件退订）→ `Detach` → 条件还原 `Warn`（`:1180` 用委托相等判定）→ `bridge` → 场景对象；幂等 `_disposed`。
- 表现层：`PMUnityMoverPresentation.Dispose` 幂等，运行期 `Destroy` / 编辑期 `DestroyImmediate`（`:214-238`）；
  primitive 自带 Collider **先 disable 再销毁**（`:259-276`），并说明"避免留下帧内仍可被 PhysX 命中的窗口"。

### C6 已确认缺陷 D1：Enter 场景构建失败分支漏还原静态 Warn、且不释放 Bridge

- 位置：`PMClientSessionHost.cs:239-245`
  ```csharp
  if (!PMR3TestScene.Build("[PMClientScene]", true, session.SceneObjects, out sceneError))
  {
      PMR3Runtime.Detach(session.World);
      HYLDDebug.LogError(...);
      return;                       // ← 没有走 ReleaseSession
  }
  ```
  相邻的四个失败分支（`:254`、`:264`、`:278`、`:300`）**都**走 `ReleaseSession(session)`，后者会
  `PMR3Runtime.Warn = session.PreviousRuntimeWarn;`（`:848-853`）并 `session.Bridge.Dispose()`。
- 触发：`PMR3TestScene.Build` 返回 false（`CreatePrimitive` 抛异常、`ValidateCollider` 判定未激活/无 Collider）。
- 后果：（a）全局静态 `PMR3Runtime.Warn` 永久停在本会话的 lambda（`[PMClientSessionHost] runtime: ...` 假前缀）；
  （b）下一次 `Enter` 会把"上一次泄漏的 lambda"当作 `PreviousRuntimeWarn` 存下，`Stop()` 时**又装回去** ⇒ 泄漏永久化；
  （c）失败局的 `Bridge`（持 World 引用）不在该分支释放，只能等 GC。
- 最小修法：把该分支改为 `ReleaseSession(session); HYLDDebug.LogError(...); return;`（与相邻分支逐字一致，1 行改动，不引入新语义）。

### C7 已确认缺陷 D2：角色/uid 不一致降级后，必然以误导信息整局失败

- 触发链：`PMClientSessionHost.cs:664-675`
  `bool isOwner = player.Role == AutonomousProxy;` → uid 不符则 `isOwner = false`（`:672`）→ `EnsureMovementRig(session, player, isOwner)`（`:675`）
  → 工厂内角色**取自 `player.Role`**（`PMR4MovementDriver.cs:716-722`）⇒ driver `_role = AutonomousProxy`，
  而宿主 `rig.IsOwner = isOwner(false)`（`PMClientSessionHost.cs:642`）。
- 崩点：`PumpMovement` 第 3 段按 `!IsOwner` 当 SP 处理（`:503-524`）→ `:513 rig.Driver.Advance(elapsedMs)`
  → `EnsureRole(SimulatedProxy)`（`PMR4MovementDriver.cs:2320-2326`）抛 `InvalidOperationException`
  → `:522` 捕获并 `Fail(session, "SP 插值异常：...")` ⇒ 整局失败，且错误信息指向 SP 插值（真因是角色不一致）。
- 反向缺口（同一处）：若 owner 副本被判为 `SimulatedProxy`（`player.Role` 与 uid 不符的另一种组合），
  宿主只把它当 SP 插值，**不报错**，本地玩家永远无法操作（静默失去控制）。
- 最小修法：`CreateFromPlayerInitialSnapshot` 增加显式 `role` 形参（宿主传入"有效角色"），
  或在降级/反向缺口时对"owner 副本不是 AP"同样告警并直接跳过该 rig 的 `Advance`/`ApplyPredicted`。

### C8 已确认缺陷 D3（中危）：碰撞隔离只做后置过滤，饱和在过滤前就抛异常

- 掩码构造：`PMDsSessionHost.cs:531-545` 与 `PMClientSessionHost.cs:256-280`
  `movementLayerMask |= 1 << allowlist[i].gameObject.layer;` ⇒ 测试地板/墙在 **Default(0)** 层，
  掩码 = `1<<0`，对旧场景**没有隔离作用**；隔离完全依赖 `IsAllowed` 后置引用比对
  （`PMUnityMoverCollisionQuery.cs:366`、`:423`、`:499`）。
- 顺序问题：`CapsuleCast` / `OverlapCapsule` 之后**立刻** `RequireNotSaturated(count, ...)`
  （`:356`、`:410`），`count == HitBufferCapacity(32)` 即抛（`:88`），**先于**白名单过滤。
- 触发：真实 Unity 大厅场景（`Enter` 时所在场景）在出生点胶囊范围内若有 ≥32 个 Default 层 Collider，
  PhysX 返回数达上限 ⇒ 抛 ⇒ `PumpMovement` 的 `Fail` ⇒ **整局失败**（不是穿墙，是直接失败）。
- 为什么门禁抓不到：`PMR4IntegrationTest` 的碰撞世界是确定性替身/仅地板+墙（报告 §10.1 自述），
  命中数恒为 1~2，**结构上**不可能触发饱和；这是"新 UDP 门禁 PASS ≠ 实际宿主逻辑"的最具体落点。
- 最小修法（任一）：测试地板/墙放专用 layer 并让掩码=该 layer（掩码本身即隔离）；
  或把饱和降级为"按已返回命中裁定 + 计数上报 + 不视为整局失败"。

### C9 已确认缺陷 D4（低，性能）：AP 每子步深克隆只为取一个帧号

- 调用点：`PMClientSessionHost.cs:565`；`Timeline.GetSnapshot()` → `BuildSnapshot(PendingFrame)`
  （`PMPredictionTimeline.cs:823-825`、`:882-896`）→ `CloneSyncOrThrow`/`CloneAuxOrThrow`
  （`:464-465`、`:686-687`）；`PMMoverSyncState.ActiveLayers` 是数组（`PMMoverState.cs:559`）⇒ 非空时逐数组克隆。
- 量级：每渲染帧最多 8 子步 × 每子步 1 次（+ 每帧 1 次 `GetPredictedSync`）⇒ 每个 AP 每秒数百次堆分配，
  在 Unity 2019.4 非增量 GC 下是可见的卡顿来源之一。
- 最小修法：给 `PMPredictionTimeline` 增只读 `PendingServerFrame` 访问器（或 `ServerFrame` 属性），宿主改用它。

### C10 已确认缺陷 D5（低，潜在静默失效）：端点 `_disposed` 后宿主既不失败也不 Pump

- `PMUdpSessionEndpoint.cs:458-460`：`catch (ObjectDisposedException) { _disposed = true; break; }`；
  `:358-362`：`Pump` 首行 `if (_disposed) return;` ⇒ 其后的 `ClientHandshakeTick`（`:371`）与
  `CheckClientSessionAfterUpdate`（`:379`）**永不执行**；宿主只在 `Endpoint.ClientFailed`（`PMClientSessionHost.cs:452`）
  与 `Failed` 事件（`:735`）上失败 ⇒ 该状态下"端点不再 Pump、宿主继续预测"。
- 可达性：当前唯一关闭 socket 的是 `Endpoint.Dispose()`（由 `Stop()` 调用，届时 `_active` 已置空），
  故实际**近不可达**；但这是一条无告警的静默通道。
- 最小修法：在 `PumpActive` 里补一条 `if (session.Endpoint.IsDisposedLike && !stopping) Fail(...)`，
  或在端点暴露"已释放"只读标志。

---

## 2. 高概率推断（依据与置信度）

### P1（置信度 高）D3 是本批唯一"真实宿主可能出现、而门禁在结构上看不见"的缺陷

依据：`PMUnityMoverCollisionQuery.cs:88/356/410` 的"先饱和、后白名单"次序 + 两宿主把掩码取自白名单自身层
（`PMDsSessionHost.cs:540`、`PMClientSessionHost.cs:274`）+ 集成门禁用替身世界（报告 §10.1）。
置信度高的原因：这是**纯代码次序**问题，不依赖运行时数据；不确定的只是"真实大厅场景在该点是否有 ≥32 个同层 Collider"。

### P2（置信度 中）单帧 ≥200ms 时会"丢时间"而不是"追赶"

`MaxStepAccumulatorMs = 200.0` 且累加器被硬钳（`PMClientSessionHost.cs:437-441`，`StepAccumulatorMs > 200 ⇒ =200`），
`elapsedMs` 超过 200ms 的部分被丢弃。契约就是这个取舍（"超长帧不拉大 dt"），但表现是"卡顿后本地预测落后墙钟"，
随后由 DS 权威快照/`ack_good_move=false` 拉回。**不是缺陷**，是需要在真实长跑里观察的口径。

### P3（置信度 中）输入是"每会话一份"，多 AP 副本会共享同一次按键

`session.Input` 是单例（`PMClientSessionHost.cs:140`、`:283`），`TickAutonomousProxies` 逐 rig 循环
（`:547-585`），每个 rig 都 `Sample(stepMs)` + `Consume(stepMs)`。正常每客户端恰有一个 AP
（`PMNetWorld.cs:704` `obj.Role = rec.IsOwner ? AP : SP`，逐连接生成），故正常不可达；
若 DS 角色分配异常导致一个客户端持有两个 AP，则同一按键被两个 rig 各消费一次 ⇒ 近似"双驱动同一输入"。

### P4（置信度 中）"接受后才 Consume"在宿主侧成立，但 `PollHardware` 被重复调用

`Sample(stepMs)` 内部**已经**调 `PollHardware()`（`PMUnityMoverInput.cs:226-230`），宿主又在循环前显式调一次
（`PMClientSessionHost.cs:474`）。该重复是**幂等且必要**的（"不足 1ms 的渲染帧也必须缓存边沿"），
`_spacePressObserved` 锁保证"一次按下只锁一次"（`:158-195`）。因此**不构成缺陷**；但需注意：
`Consume` 只在 `IsRealStepMs(1..50)` 时清缓冲（`:248-263`），故"键按住不放 + 长期冻结"期间边沿会一直留缓冲
（`_bufferedEdgePolls` 只计数、无超时丢弃）⇒ 解冻后可能补一次跳跃。低概率、可接受，建议在解冻路径显式 `ClearBufferedJumpEdge()`。

---

## 3. 无法确定（缺少证据）

- **U1（关键）真实 Unity/PhysX 几何语义**：`ContactOppositionEpsilon=1e-3`
  （`PMUnityMoverCollisionQuery.cs:66-93`）在"出生 y == 半高、足底恰好贴地"时能否稳定水平移动；
  `TryResolveStartOverlap`（`:392-470`）在"出生即与地板轻微重叠"时的确定性与推出方向。
  只能在 Unity 内跑（B2 的 `Tools/PMR4/验证 Unity 碰撞适配（真实 PhysX）` 菜单）；net8 不可证。
- **U2 真实 DS 二进制与真实客户端**：`HyldDS/HyldDS.exe` 为旧包（报告 §9.1/§10.2），
  本次两个宿主的代码**从未在任何真实进程里跑过**；"WASD 走动 / 空格跳 / 对方 SP 插值 / 断线两侧冻结"
  目前只有代码级证据。
- **U3 `PMUnityMoverInput` 真实键位**：`UseLegacyAxes` 回退与 `Horizontal/Vertical` 轴未配置时的行为、
  `_spacePressObserved` 在高帧率抖动下的边沿完整性（`:376-430`）。
- **U4 大厅场景的 Default 层碰撞体数量/分布**：直接决定 D3 是否真的可触发；需要场景级统计（本次未做）。
- **U5 D2 的真实可达性**：取决于 DS 的角色分配是否严格绑定 owner 连接；从 `PMNetWorld.cs:704`
  看 `rec.IsOwner = ReferenceEquals(obj.GetNetConnection(), connection)`，理论上客户端只会看到自己一个 AP，
  故 D2 更像"防御分支写错"，而非可达缺陷。
- **U6 `GetSnapshot()` 分配量的实测**：未做 Profiler 采样，D4 的量级是按代码路径推算（非实测）。

---

## 4. 已检查范围

### 4.1 必读文档（按委派指定顺序，全文读完）

1. `D:/UGit/hyld-master/AGENTS.md`
2. `Client/Assets/AGENTS.md`（客户端现状、坐标系、`frameTime=0.016`、DS 身份判定与旧链抑制）
3. `Server/AGENTS.md`（服务端 LZJUDP/NetSim/BattleLoop、"服务端不含 Unity"的历史边界）
4. `Docs/plans/net-r4-network-contract.md`（§B1 承载/流代次/事件、§B2 适配器、§B3 主侧集成 + 末尾复核修订）
5. `Docs/plans/_r4b_host_integration_report.md`（**只作为被审查对象的自述**，结论不采信其 PASS）
6. 主计划：仅读 R4-B 实施相关卷首/末尾实施表（有界，不接手实施）

**由文档决定的首搜入口**（非盲搜）：两宿主 → `Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs`、
`PMClientSessionHost.cs`；Driver 公开面 → `PMR3/PMR4MovementDriver.cs`；帧域 → `PMPrediction/PMPredictionTimeline.cs`；
适配器 → `PMUnity/*.cs`；生命周期/复制应用 → `PMNet/World/PMNetWorld.cs`、`PMNet/Session/*.cs`。

### 4.2 逐文件检查清单（含关键行区间）

| 文件 | 检查内容 |
|---|---|
| `Server/Boot/PMDsSessionHost.cs`（1236 行） | 全文；重点 `:500-620`（场景+掩码+查询构造）、`:650-700`（Pump 次序/SyncTransforms）、`:703-750`（PumpMovement/帧域/信用）、`:752-800`（Prune）、`:802-843`（出生点）、`:844-935`（OnConnected/AttachMovementDriver/PublishInitialSnapshot）、`:939-975`（CheckPlayerDrops）、`:1076-1200`（Dispose） |
| `Server/Boot/PMClientSessionHost.cs`（994 行） | 全文；重点 `:170-320`（Enter 六条失败/成功分支）、`:329-360`（Stop）、`:365-400`（PumpActive，`:386` SyncTransforms → `:388` endpoint.Pump）、`:435-450`（FrozenWallClockPump）、`:453-530`（PumpMovement 四段）、`:547-585`（TickAutonomousProxies，`:565` 取 ServerFrame）、`:594-650`（EnsureMovementRig）、`:659-700`（OnPlayerReplicated 降级，`:664-675`）、`:736-830`（失败/冻结）、`:839-885`（ReleaseSession）、`:900-994`（Driver MonoBehaviour） |
| `PMR3/PMR4MovementDriver.cs`（2338 行） | 构造/工厂 `:596-760`；`PublishInitialSnapshot`/`PublishAuthoritySnapshot` `:850-915`；`Tick` `:917-980`；`TryBuildInputPayload`/`SendInputPayload` `:1008-1060`；`Pump` 两个重载 `:1193-1265`；`SimulatePumpSteps` `:1268-1310`；`BeginServerResync` `:1439-1476`；`Advance`/`SamplePresentation` `:1478-1506`；`OnSnapshotReplicated` `:1508-1540`；`OnClientEventsPayload`/`OnClientResyncPayload` `:1541-1600`；`Update`/`ApplyPending` `:1606-1640`；`ApplyPendingSnapshots` `:1736-1830`；`ApplyPlainSnapshot` `:1830-1900`；`ApplyPendingReliable` `:1893-1930`；`Freeze`/`Dispose` `:2205-2268`；`Describe` `:2269-2308`；`EnsureThread`/`EnsureRole`/`ThrowIfDisposed` `:2309-2338` |
| `PMPrediction/PMPredictionTimeline.cs` | `EnsureServerFrameDomain` `:957-963`；`GetSnapshot` `:823-825`；`BuildSnapshot` `:882-896`；`GetBoundaryServerFrame`/`ResolveBoundaryIndex` `:1458-1478`；`Tick`/`TickCore` 关键段；`ValidateSnapshotStructural` `:966-990` |
| `PMPrediction/PMInterpolationBuffer.cs` | `OnAuthority` `:174-250`；`Advance` `:255-280`；`Sample`（外推钳制） `:290-350`；`_maxExtrapolateMs` `:72/106/318` |
| `PMUnity/PMUnityMoverCollisionQuery.cs`（710 行） | 头注释契约映射 `:1-60`；`HitBufferCapacity`/`ContactOppositionEpsilon` `:66-93`；allowlist 拷贝/`IsAllowed` `:101/156/170-173/524-538`；`Sweep` `:340-380`；`TryResolveStartOverlap` `:392-470` |
| `PMUnity/PMUnityMoverInput.cs`（456 行） | `PollHardware` `:158-195`；`Sample`/`SampleAndConsume`/`Consume`/`Reset` `:216-280`；`Convert` 纯转换 `:286-310`；`Build`/`ReadPlanar` `:351-430` |
| `PMUnity/PMUnityMoverPresentation.cs`（316 行） | 构造/`CreateTestCamera` `:78-210`；`Dispose` `:214-238`；`DisablePrimitiveCollider` `:259-276` |
| `PMNet/Session/PMUdpSessionEndpoint.cs`（1054 行） | `Pump` 次序 `:354-381`；`Dispose` `:384-430`；`ReceiveDatagrams` 与 `ObjectDisposedException` 吞咽 `:437-470`；`ServerHandshake`/`RaiseConnected` `:587-655`；`PruneServerSessions`/`RemoveServerSession` `:680-745`；`ClientHandshakeTick` `:748-800`；`CheckClientSessionAfterUpdate` `:849-863` |
| `PMNet/Session/PMTransportConnection.cs` | `IsReady` `:508`；激活拒绝死链路 `:546-620`；`Send`/`SendEnvelope` `:632-730`；`Update` `:776-830`；`HandleDisconnected` `:1166-1210` |
| `PMNet/Transport/PMTransport.cs` | `Update`（`DrainInbound`→`Expire`→溢出→`IdleTimeoutMs` 判定→预算→`FlushOutbound`） `:355-400`；`NormalizeClock` `:409-435` |
| `PMNet/Transport/PMTransportTypes.cs` | `IdleTimeoutMs=10000` `:140`、`:150` 语义注释 |
| `PMNet/World/PMNetWorld.cs` | `ApplyCreate` 次序 `:659-746`；Create 记录 `RepInitialState` 现取 `:535-600`；`RollBackCreate` `:750-760`；角色来源 `:704` |
| `PMR3/PMR3Runtime.cs`（358 行） | `PlayerReplicated`/`PlayerSpawned`/`Has*Handlers` `:63-72`；`Register` `:98`；`Attach`/`Detach`/`Shutdown` `:122-186`；`SpawnPlayer` `:249-300`；`NotifyPlayerReplicated` `:310-340` |
| `PMR3/PMR3Player.cs` | `PublishMovementSnapshot` `:165`；`OnRep_MovementSnapshot` `:172-186`；`GetNetConnection` `:196`；`OnReplicatedCreate` `:211`；`ServerProbe` `:223+` |
| `Server/Boot/PMDsHost.cs` | 新链分流 `:150-200`；`Update` 唯一驱动点 `:380-405`；`OnApplicationQuit`/`OnDestroy` `:575-605` |
| `Server/Panel/UIMatchingPanel.cs`（71 行） | 全文；旧 UI 入局分支 `:38-70`（`PMDS1:` 前缀判定 → 新链 `Enter` 并 `return`；否则走旧 `BattleData.InitBattleInfo` + `ClearSenceManger.LoadScene`） |
| `PMMover/PMMoverState.cs` | `PMMoverSyncState` 字段（含 `ActiveLayers` 数组） `:526-560`；`PMMoverDefaults.CapsuleRadiusMeters=0.4` `:653`、`CapsuleHalfHeightMeters=1` `:656` |

### 4.3 明确未做（边界）

- 未读 R5/R6 相关章节、未读旧业务（`BattleManger`/`BattleData`/`TouchLogic`/`CommandManger`）实现细节，
  仅在"入局分支是否并存"的必要处读了 `UIMatchingPanel`。
- 未运行任何工具/门禁（本任务只读；不编译、不跑 net8、不起 Unity、不动 DS 二进制）。
- 未做全仓检索、未清点资产、未统计大厅场景 Collider（U4）。
- 未递归委派、未改任何源码、未提交 svn/git。

---

## 5. 建议下一步（最小补充查询 / 运行时验证）

### S1 修 D1（1 行，零风险）
`PMClientSessionHost.cs:239-245` 改为 `ReleaseSession(session); HYLDDebug.LogError("[PMClientSessionHost] 测试场景不可用：" + sceneError + "，拒绝入局（不回旧链）"); return;`
——与相邻四个失败分支完全一致，顺带修掉静态 `Warn` 泄漏与 `Bridge` 不释放。

### S2 修 D2（显式角色）
`PMR4MovementDriver.CreateFromPlayerInitialSnapshot` 增加 `PMNetRole role` 形参（或重载），宿主传"有效角色"；
并在 `OnPlayerReplicated` 里对"owner 副本不是 AP"也告警（对应 D2 的反向静默缺口）。

### S3 修 D3（中危，优先）
把 `PMR3TestScene` 的地板/墙放到**专用 layer**（如 `PMR4Test`），两宿主用 `1 << 该layer` 组掩码
（掩码即隔离，白名单保留为纵深防御）；或在 `PMUnityMoverCollisionQuery` 把 `RequireNotSaturated` 改为
"记 `saturationFailures` 并按已返回命中裁定，仅当白名单项缺失时失败"。

### S4 修 D4 / D5（低）
D4：`PMPredictionTimeline` 增 `PendingServerFrame` 只读属性，`PMClientSessionHost.cs:565` 改用它。
D5：端点暴露"已释放"只读标志，`PumpActive` 在 `Faulted` 判定处补一条"端点已释放但会话未 Stop ⇒ Fail"。

### S5 真实宿主验证（P4B6 / PENDING_USER，必需，不能由 net8 代替）
1. 重 Build DS（当前 `HyldDS/HyldDS.exe` 为旧包）：`Build / Build HyldDS (Windows Headless)`。
2. Unity 内跑 `Tools/PMR4/验证 Unity 碰撞适配（真实 PhysX）`：断言"贴地水平扫掠不被地面判为阻挡"、
   "起点重叠推出确定性"、"饱和显式失败"（U1）。
3. 起 Lobby + 双客户端进 `PMDS1` 局，观察：
   - DS：`heartbeat … movementDrivers=N hostTick=…`、出生点日志、权威 `boundary` 是否随输入推进；
   - 客户端：`movementRigs=2(ap=1,sp=1)`、`snapshots/inputsSent/accumulatorMs` 是否正常增长；
   - 断线：kill 一个客户端 → 两侧该角色应**冻结**（客户端 ≤10s、DS 立即），日志不得出现
     `SP 插值异常`（D2）、`饱和`/`saturation`（D3）、`运动驱动异常`。
4. 记录 U4（大厅场景同层 Collider 数量）与 U3（真实键位/边沿）的实测结果，回填本文档。

### S6 补窄门禁（可选，用于把 D3/D5 变成可回归项）
- D3：net8 里在测试世界注入 ≥40 个同掩码层干扰 Collider，断言"宿主不因饱和而整局失败、且行为与白名单一致"。
- D5：构造"端点已释放但会话未 Stop"，断言宿主进入 `Faulted`。
