# R6 宿主接点前置有界调查（结算 → HP/资源/死亡 → winner 回收）

范围（硬边界，未外扩）：`Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs`、`PMClientSessionHost.cs`、`PMDsLobbyAgent.cs`、`Client/Assets/Scripts/PMR3/PMR3Player.cs`、`PMR3Runtime.cs`、`PMR4MovementDriver.cs`、`PMNet/Declarations/PMNetDeclarations.cs`、`PMNet/PMWireType.cs`（PMCond）、`Server/DS/PMDsLobbyHost.cs` + `Server/Server/Server.cs` 的 gateway、`PMProjectile/PMProjectileCoordinator.cs`（Settlement 定义）、`PMUnity/PMUnity{Mover,Battle}Presentation.cs`、`PMR3/PMR5ProjectileDriver.cs`（只读接口面）。只读，未编译、未修改任何源码；本文是本任务唯一写入文件。

## 读取的文档与证据

| 顺序 | 文件 | 用途 |
|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 入口路由：客户端文档在 `Client/Assets/AGENTS.md`，网络迁移计划在 `Docs/plans/net-architecture-migration.md` |
| 2 | `Client/Assets/AGENTS.md` | 进程形态判定（`PMNetRuntime.IsDedicatedServer`）、DS 启动参数契约、HYLDManger 守卫；未涉及 R6 结算 |
| 3 | `Docs/plans/net-r5-network-contract.md` | §「C宿主首个可执行入口」「B2b/C适配可并行边界」「声明层」：`DrainSettlements` 是给未来 R6 的唯一消费者、诊断命中只计数不扣血、声明层新增 RPC/属性契约 |
| 4 | `Docs/plans/net-r3-control-contract.md` §7/§8/§9（结果/退出段） | §7.3 Lobby 宿主只是「控制结果通知」不伪造 BattleReview、§3 Result 消息字段与幂等、§9 心跳/看门狗时限 |
| 5 | 源码（见上述边界清单） | 一切结论的最终依据 |

文档决定了首搜入口：R5 契约把 `DrainSettlements` / `ProjectileDriver` / `PMR3Player` 声明层直接点名为 R6 消费口，因此本次从 `PMDsSessionHost.OnProjectileSettlement` 与 `PMR3Player` 属性表入手，未从业务昵称或全项目盲搜。

---

## 一、已确认（有源码位置的事实）

### 1. 结算消费的唯一接点（DS 侧）

- **驱动出口**：`Client/Assets/Scripts/PMR3/PMR5ProjectileDriver.cs`
  - `public event Action<PMProjectileSettlement> Settlement;`（:470）
  - `public int DrainSettlements(int max, out PMProjectileSettlement[] items)`（:2670）——「有订阅者走事件、无订阅者进有界缓冲」，两者**不重复投递**；`public int PendingSettlementCount`（:581）；`public const int MaxApplyPerPump = 64;`（:213）、`MaxPendingSettlements = 512`（:207）。
  - 计数可观察：`SettlementsHandedToR6` / `SettlementsWithNoConsumer` / `SettlementsQueued`（:444-446）——名字里已经预留 R6。
- **宿主订阅（唯一）**：`PMDsSessionHost.CreateProjectileWiring` 中 `_projectileDriver.Settlement += OnProjectileSettlement;`，并断言 `HostMotion/History/Policy` 非 null 后才算接线成功。
- **唯一消费者实现**：`PMDsSessionHost.cs:2232 private void OnProjectileSettlement(PMProjectileSettlement settlement)`——只做 `_diagnosticProjectileSettlementCount++` / `+= settlement.HitCount` + 日志；注释明写「**不扣血、不写 HP/Mana/SuperEnergy、不接旧普通攻击/资源系统**」。
- **兜底排空**：`DrainDiagnosticSettlementBuffer()` 在订阅者「未交付」时调 `DrainSettlements(MaxApplyPerPump, out buffered)` 并复用 `OnProjectileSettlement`。F8 不变式：事件 XOR 缓冲，一条结算只计一次（计数在日志之前完成，日志异常被吞）。
- **结算结构**（`PMProjectile/PMProjectileCoordinator.cs:414`）：
  ```csharp
  public struct PMProjectileSettlement {
      public PMProjectileKey Key;            // Epoch/OwnerNetId/ProjectileId/Origin
      public uint OwnerNetId, ActivationId, AuthorityNetId;
      public PMProjectileOrigin Origin;      // ClientPredicted=1 / ServerDirect=2
      public double WallTimeMs;
      public bool StopOnHit;
      public int HitCount;
      public PMProjectileValidatedHit[] Hits;
  }
  public struct PMProjectileValidatedHit {   // PMProjectileContracts.cs:114
      public uint TargetNetId, TargetStreamVersion;
      public PMVector3 ImpactPoint;
      public PMProjectileHistoryResolution Resolution;  // FrameAnchor/Rewind/Current
  }
  ```
- **NetId → 玩家身份反查已有现成私有工具**（R6 直接复用即可，不需新造）：
  - `private bool TryResolveUidByNetId(uint netId, out int uid)`（:2608）——遍历 `_playersByUid`。
  - `private bool TryResolveAuthoritativeTeam(int uid, out int team)`（:2634）——正式模式用 `_formalTeamIndexByUid`，诊断用名册 TeamId 或名册行奇偶。
- **客户端没有权威结算**：`PMClientSessionHost.Enter` 里 client driver 以 `policy = null` 构造（`new PMR5ProjectileDriver(world, bridge, epoch, history, null, motion)`），且 DS 才是 `AuthorityServer` 帧与历史的来源；客户端 `history` 是空实例、`PumpProjectiles` 只 `DrainViewChanges` 消费视图，**不消费 Settlement**。

### 2. PMDsSessionHost 的 smoke / 断线 / 退出实际 API 与约束

公开面（真实签名）：
```csharp
public static PMDsSessionHost Start(PMNetLaunchOptions options, out string error);   // :676
public void Pump();                                                                  // :1151
public void Dispose();                                                               // :1818
public Action<int> ExitRequested;   public Action<string> Log;
public int BoundPort {get;}  public PMNetWorld World {get;}  public PMNetSessionBridge Bridge {get;}
public PMUdpSessionEndpoint Endpoint {get;}  public PMDsLobbyAgent Lobby {get;}
public bool SceneReady {get;}  public bool IsFaulted {get;}  public string FaultReason {get;}
public int PlayerCount {get;}  public PMR5ProjectileDriver ProjectileDriver {get;}
public long DiagnosticProjectileHitCount {get;}  public long DiagnosticProjectileSettlementCount {get;}
public string Describe();
public const int SmokeDeadlineMs = 120000;   // :433
public const int MovementWorldVersion = 1;
```
- **Pump 次序**（:1151）：`Physics.SyncTransforms()` → `_endpoint.Pump`（内部含 bridge.Update，禁止二次调用）→ `PumpMovement`（:1209，逐副本 `driver.Update` + `driver.Pump(elapsed, _movementServerFrame, _movementHostTickId)`）→ `PumpProjectiles`（:1210+，先 `RecordProjectileHistory` 再固定 16ms 累加器 ≤8 子步，零子步仍 `Pump(0)` 排空裁决，最后 `DrainDiagnosticSettlementBuffer` + `DrainDsProjectileViews`）→ `CheckPlayerDrops` → `CheckSmoke` → `_lobby.Pump`。
- **smoke**（`CheckSmoke(long nowMs)` :1707）：
  - 仅在 `options.ServerSmoke` 且 `_lobby.ReadySent` 后生效；期限 `nowMs + 120000`（`_smokeDeadlineMs`）；
  - 完成条件 = `_playersByUid.Count == _expectedUids.Count && >0` 且**每个**副本 `ProbeCount > 0`（身份/计数真实，不用定时器伪造）；
  - 完成后 `_lobby.SubmitResult(0, BuildSmokeSummary(), out error)`；summary = `WriteInt32(1)` + `WriteStringValue("smoke")` + `[uid, probeCount]...`（自描述、界内）；
  - 超期 `Fail("smoke 验收超时…")`。
- **断线**：
  - `PruneDisconnectedDrivers()`（:1258）：逐 uid 检查 `player.OwnerConnection == null || !IsReady` → `RecordProjectileDisconnectSample(...)`（补 `Alive=false` 样本，:2339）→ `driver.Freeze(); driver.Dispose();` → `_projectileDriver.UnbindPlayer(player)`（取消失效预留/入站队列/悬空权威对象）。
  - `CheckPlayerDrops()`（:1681）：先 `PruneDisconnectedDrivers`，再比较 `_endpoint.ConnectionCount < _playersByUid.Count`；若 `ServerSmoke` → `Fail("smoke 验收期玩家掉线，测试局中止")`（**不伪造正常胜利**）。
  - `OnEndpointFailed`（:1674）只告警（端点事件也用于「入局会话关闭」）。
- **退出时序**：`Fail(reason)`（:1802）→ `_faulted = true` + `ExitRequested(1)`；`OnLobbyExitRequested(int code)`（:1793）把 Lobby 的退出请求（ResultAck / Shutdown）转发给 `ExitRequested`。`PMDsHost` 里 `_sessionHost.ExitRequested = OnNewChainExitRequested` → `Application.Quit(code)`（`PMDsHost.cs:189/192/196`）。
- **Dispose 次序**（:1818）：`_lobby.Dispose` → `_endpoint.Dispose` → `ReleaseProjectileWiring()`（Driver → Motion，**在 bridge 之前**）→ `PMR3Runtime.Detach(_world)` → 逐 `driver.Freeze()+Dispose()` → 还原 `PMR3Runtime.Warn` → `_bridge.Dispose` → 销毁场景对象 → `_battleMap.Dispose` / `ReleaseIsolatedScene`。

### 3. PMDsLobbyAgent 实际 API 与约束

```csharp
public const int ConnectTimeoutMs = 5000, StartupReadyTimeoutMs = 30000, ReadyRetransmitMs = 1000;
public const int HeartbeatIntervalMs = 5000, RuntimeLivenessTimeoutMs = 15000;
public const int ResultRetransmitMs = 1000, ResultAckTimeoutMs = 30000;
public const int MaxInboundChunks = 512, ReceiveChunkBytes = 8192, MaxChunksPerPump = 64;
public bool Start(out string error);
public void Pump(long nowMs, long nowUnixSeconds);
public bool SubmitResult(int winnerTeamId, byte[] summary, out string error);   // :408
public bool ReadySent {get;}  public bool IsFaulted {get;}  public string FaultReason {get;}
public bool HasResult {get;}  public ulong ResultId {get;}  public bool ResultAcknowledged {get;}  public int ExitCode {get;}
```
- **SubmitResult 幂等口径**：summary 非空且 ≤ `PMDsControlWire.MaxSummaryBytes`；`_hasResult` 后同 `winner+summary` 重复提交返回 true，内容不同则 false（同 ResultId 不得改内容）。
- **Pump 阶段**（:311）：`DrainInbound` → 对端关闭/解码故障 → 阶段0 未发 Ready（`SceneReady` 才发；`StartupReadyTimeoutMs` 内未就绪即 Fail）→ 阶段1 等首个可信下行（30s）→ 阶段2 运行期 liveness（15s 无可信下行即 Fail，**不因本机发送成功续命**）→ Heartbeat（5s）→ 若 `_hasResult` 未 Ack：`ResultAckTimeoutMs` 超时 Fail，否则每 1s 重发 `Result`。
- **ResultAck 时序**：`_resultAcknowledged = true; SendExited(0); RequestExit(0);`；`Shutdown` → `SendExited(0); RequestExit(0);`；`Fail` → `SendError(1); SendExited(1); RequestExit(1);`。`RequestExit` 只回调一次。

### 4. winner 经 Lobby 回收的实际链路（**当前是断的**）

1. DS：`Lobby.SubmitResult(winnerTeamId, summary)` → 控制帧 `Result(ResultId, WinnerTeamId, Summary)` → Lobby `PMDsCoordinator`；
2. Lobby：`PMDsCoordinatorEffect.ResultAccepted` → `PMDsLobbyHost.RecordResult`（`Server/DS/PMDsLobbyHost.cs:1553`）填 `PMDsLobbyResultNotice { MatchId, DsId, Epoch, ResultId, WinnerTeamId, Summary, Roster[] }` 入 `_pendingResultNotices`；
3. Lobby 的**两个出口**（`ApplyDeferredWork` 一带，:1296-1317）：
   - `public event Action<PMDsLobbyResultNotice> ResultAccepted`（:411）——**进程内**消费者（net8 门禁 `PMDsLobbyTest` 等）；
   - `_gateway.NotifyMatchEnded(notice)`（:1317）。
4. 真实 gateway 实现 `ServerLobbyClientGateway.NotifyMatchEnded`（`Server/Server/Server.cs:422`）**只写一条 Logging.Debug.Log**，注释明写「不发送 BattleReview」。**它不发任何 `MainPack` 给客户端。**

结论：**当前 winner 到不了客户端**。DS→Lobby 的 Result 完备（幂等/Ack/重发/超时全在），Lobby→客户端的结果通知**不存在**。契约 §7.3 只要求「不伪造 BattleReview」，把「控制结果通知」留给后续；这就是 R6 需要补的接缝。

作为对照，**入场**方向的 gateway 是完整的：`PMDsLobbyHost.PublishEntryOffers`（:1330）逐名册 uid 验「客户端在线」→ `Coordinator.IssueTicket` → `PMDsLobbyEntryNotice { …, Identity = roster identity, Ticket }` → `TrySendEntryOffer` → `Server.cs:385` 组 `PMDsEntryOffer` + `MainPack(RequestCode.Matching, ActionCode.StartEnterBattle, Str = "PMDS1:" + Base64(...))` → `client.Send(pack)`。

### 5. 客户端 OwnerPlayer 的 hero/team 来源（不能信客户端选择）

- 客户端**唯一入局入口**：`PMClientSessionHost.Enter(PMDsEntryOffer offer)`（:433）。分流点 `Client/Assets/Scripts/Server/Panel/UIMatchingPanel.cs:44`：`PMDsEntryCodec.HasPrefix(pack.Str)` 命中即交新宿主并 `return`，**绝不回退** `BattleData.InitBattleInfo` / `ClearSenceManger.LoadScene`。
- 客户端可得的可信身份：`offer.Identity { Uid, PlayerId, TeamId, HeroId }`（该 Identity 由 Lobby 从**已认证名册**逐人下发，见 §4；`PMDsEntryCodec` 只做范围校验）。客户端对 `offer.Identity` 的实际使用**仅** `Uid`：
  - `PMClientSessionHost.OnPlayerReplicated`（:1122）比对 `player.Uid != session.Offer.Identity.Uid` → 不一致即 `Fail`（拒绝降级成 SP，也拒绝静默失去控制）。
  - `TeamId` / `HeroId` 在 `PMClientSessionHost` 中**零使用**。
- 其他玩家的身份：`PMR3Player` 复制成员只有 `_uid`、`_probeCount`、`_movementSnapshotV1`；表现层用 `player.Role`（AP/SP）与 `player.NetId` 区分，**不用 hero/team**。
- 故：**客户端知道自己的 team/hero（来自已认证名册 offer），完全不知道别人的 team/hero**；客户端侧任何 UI 选择都不被新链读取。若 R6 需要客户端展示他人 hero/team 或本地判定，只能新增声明（见建议）或由 DS 决定后复制下发，**不得让客户端自选**。

### 6. R3Player 声明层现况与新增 OwnerOnly 资源 / 公共 HP·死亡的实际落点

`PMR3Player`（声明阶段输入，由 `Tools/PMNetGen --decl-gen` 生成）：
```csharp
[PMNetworkObject] public partial class PMR3Player : PMNetObject {
    [PMReplicated] private int _uid;                     // ID 18801
    [PMReplicated] private int _probeCount;              // ID 12656
    [PMReplicated] private byte[] _movementSnapshotV1;   // ID 61580
    public int Uid {get;}  public int ProbeCount {get;}  public byte[] MovementSnapshotPayload {get;}
    public void PublishMovementSnapshot(byte[] payload) { PMNet_Set_movementSnapshotV1(payload); }
    [PMRepNotify(nameof(_movementSnapshotV1))] private void OnRep_MovementSnapshot();
    public IPMMovementNetworkDriver MovementDriver;      // 接缝（接口定义在同文件）
    public IPMProjectileNetworkDriver ProjectileDriver;  // R5-B2a 接缝
    public const int ProjectileMaxPayloadBytes = 4096;
    public override PMNetConnection GetNetConnection() { return OwnerConnection; }
    public static void PMR3DispatchOnRep(PMNetObject target, ushort onRepMethodId);
}
```
- 条件复制：`PMCond`（`PMNet/PMWireType.cs:69`）= `None=0, OwnerOnly=2, SkipOwner=3, SimulatedOnly=4, AutonomousOnly=5, Custom=8, Dynamic=14, Never=15`；`[PMReplicatedAttribute]` 有 `public PMCond Condition = PMCond.None;`（`PMNetDeclarations.cs:52-68`），生成器 `DeclScanner.PMCondNames.Implemented` 明确支持以上 8 项，未实现的 9 项（InitialOnly 等）在**声明期报错**。
- **ID 锁**：`Docs/plans/pmnet-r3-ids.json`（勿手改）：`classes{CLASS:PMNet.R3.PMR3Player:405815557, CLASS:PMNet.R3.PMR5Projectile:227098277}` + `members{...}`。新增复制成员/RPC = 追加新 ID。
- **生成命令**（`Tools/PMNetGen/Program.cs` CLI 契约）：
  - `PMNetGen --decl-gen <src...> --out-dir <dir> --id-lock <lock>`（写生成物 + 锁）
  - `PMNetGen --decl-check <src...> --id-lock <lock> --out-dir <dir>`（逐字节比对，不一致退出码 2）
  - 产物：`PMR3/Generated/PMNet.PMNet.R3.PMR3Player.g.cs`、`PMNet.PMNet.R3.PMR5Projectile.g.cs`、`PMNetGeneratedRegistry.g.cs`；`RegisterAll()` → `PMNetRegistry.Seal(PMStableHash.GlobalProtocolHash(entries))`。
- **改声明的连带后果（重要）**：新增复制成员/RPC 会改 `PMNetRegistry.ProtocolHash`，而该值是 `Initialize` 里与引导文件、以及客户端 `Enter` 里与 offer 的**强一致校验项**（不一致直接拒绝入局/启动失败）。因此 R6 必须**同步重建 Lobby 与客户端**，且 Lobby 用 `PMR3Runtime.ProtocolHash` 分配局。

### 7. 死亡如何停止 Mover / client 表现消费入口

- **停止运动的权威入口（trusted effect，DS-only）**：
  ```csharp
  public bool ServerSubmitTrustedEffect(PMMoverEffectRequest effect)   // PMR4MovementDriver.cs:1140
  ```
  仅 `_role == PMNetRole.Authority` 才接受；入 `_trustedEffects`，在**下一个真正执行的模拟步**随该步 Input 进入模型。
  - `PMMoverEffectKind`：`SetVelocity=0, Teleport=1, SetMode=2, SetParameters=3`（`PMMoverState.cs:344`）；
  - `PMMoverMode`：`Walking=0, Falling=1, Flying=2, Inactive=3`（`PMMoverState.cs:161`）；**`Inactive` 语义 = 「不施加任何速度/加速度，但时间轴继续」**。
  - ⇒ 死亡停运动 = `driver.ServerSubmitTrustedEffect(PMMoverEffectRequest.SetMode(PMMoverMode.Inactive))`。（另有 `SetVelocity(Zero)` 可作补充，但 `Inactive` 是语义最正的「停」。）
- **`Freeze()` 不是死亡**：`public void Freeze()`（:2205）语义是「断线冻结」——AP 停止推进并只等显式 Resync、清空入站队列；当前唯一使用点是断线收尾与 Dispose。**不要把它当死亡停运动**。
- **客户端表现消费入口（唯一写入点）**：`PMClientSessionHost.PumpMovement(Session session)`（:895）
  - AP：`rig.Presentation.ApplyPredicted(rig.Driver.GetPredictedSync())`（:978）
  - SP：`rig.Driver.Advance(elapsedMs)` → `rig.Driver.SamplePresentation()` → `rig.Presentation.ApplyInterpolated(sample.Sync)`（:959）
  - 统一面 `IRigPresentation`（私有）→ `PMUnityMoverPresentation.Apply/ApplyPredicted/ApplyInterpolated(PMMoverSyncState)`（:149/163/170）与 `PMUnityBattlePresentation.Apply/ApplyPredicted/ApplyInterpolated(PMMoverSyncState)`（:258/279/286）。
  - **两者都只消费 `PMMoverSyncState` 的位置/朝向/缩放**（`PMUnityBattlePresentation` 另驱动 Animator `"Speed"`）。**没有任何 HP/死亡/hidden 通道**。
- **另一条已存在但无人消费的客户端事件通道**：`PMR4MovementDriver.EventDispatched`（`public event Action<PMPredictionEvent> EventDispatched;` :838，不可逆权威事件 = ModeChanged/Landed，经 `Journal.DispatchTo(confirmed, sink)`）。全项目 `grep` 显示新链宿主**零订阅者**——可作为 R6 死亡/受击表现的一个现成接缝（无需新增声明），但需自行判断其语义是否够用。

### 8. DS 权威历史里的 Alive 是硬编码 → R6 必须改这里

- `PMDsSessionHost.RecordProjectileHistory(...)`（:2160 附近，末尾 :2207-2208）：
  ```csharp
  sample.Teleported = false;
  sample.Alive = true;      // 注释：「本批没有传送判定源与生命值系统：恒 false/true，不伪造未观测的事实」
  ```
- 实时过滤 `IsProjectileTargetAuthoritative(int uid)`（:2380）= owner 连接 `IsReady` + driver 存活 + `!driver.IsFrozen`——**无 HP/死亡项**。
- ⇒ R6 接 HP/死亡后，这两处必须从权威 HP/死亡状态派生（否则死人仍可被命中/仍写 Alive=true 历史）。

---

## 二、高概率推断（依据 + 置信度）

1. **winner 回收的最小改动面在 gateway，不在 DS**（置信度 高）。依据：DS 侧 `SubmitResult`→控制帧→`ResultAccepted` 已闭环且通过验收；唯一缺环是 `IPMDsLobbyClientGateway.NotifyMatchEnded` 的实现体只 log（`Server.cs:422`）。因此「把 winner 给客户端」= 在该实现里按 `notice.Roster` 逐 uid 找活跃 Client 并 `client.Send(MainPack)`（形如现有 `TrySendEntryOffer` 的写法），客户端在 Lobby 面板侧识别一个新 code。**不需要改 DS/PMDsLobbyAgent 协议**。
2. **客户端要展示他人 hero/team，只能走新增声明**（置信度 高）。依据：`PMR3Player` 只复制 `_uid/_probeCount/_movementSnapshotV1`，且 `PMClientSessionHost` 对 `offer.Identity.TeamId/HeroId` 零使用；`PMDsEntryOffer` 只承载本机身份。若不加字段，客户端物理上无法知道他人 hero/team。
3. **HP 与资源应有不同的复制条件**（置信度 中高）。依据：`PMCond.OwnerOnly` 在 `PMWireType.cs` 的注释明写「用于修 mana/能量信息泄露」，而「公共 HP/死亡」在任务措辞里与 OwnerOnly 资源并列。推断取向：资源（Mana/SuperEnergy）用 `[PMReplicated(PMCond.OwnerOnly)]`，HP/Alive 用无条件 `[PMReplicated]`（所有人需要看到血条/死亡）。
4. **`EventDispatched` 比新增 RPC 更适合「死亡一次性表现」**（置信度 中）。依据：它已是可靠下行（`ClientMovementEventsV1` → `journal.TryAccept` → `DispatchTo(confirmed, ...)`）且带已确认边界，天然防重放/防晚到；但当前**无订阅者**也意味着**未经验证**，需要新的集成测试。
5. **`Settlement.Hits` 的 `TargetNetId` 与 `_playersByUid` 的 NetId 同域**（置信度 高）。依据：`TryResolveUidByNetId` 已是 DS 命中过滤（`AcceptDiagnosticHit`）的实现基础，且历史采样的 `sample.NetId = player.NetId.Value` 与之同源。

---

## 三、无法确定（缺少证据 / 有界停止点）

1. **客户端如何「看到」结算结果**：本调查未找到任何客户端侧结果展示/回放入口（新链未发 `BattleInfo`，`BattleReview` 明确属 R6 且未实现）。R6 要展示什么（胜负横条 / 结算面板 / 回放）不在本次边界内，**无法确定**。
2. **HP/资源的具体数值表来源**：`Shared/BattleNumericConfig` 在本调查中只见 `Get(heroId).MoveSpeed`（`PMDsSessionHost.BuildFormalRoster`）；是否存在最大 HP / 技能消耗的权威表未被检查（超出边界）。
3. **`ClientMovementEventsV1` 与 `EventDispatched` 的实际投递时序**（是否必然早于同帧 `ApplyPredicted`）未追到 driver 的 `Update/ApplyPending` 内部实现细节（本次只读公开面），**无法断言**其与死亡表现的先后。
4. **客户端是否已有 Lobby TCP 结果 code 的现成识别点**：本次未检查客户端大厅消息分发（`UIMatchingPanel` 以外），**无法确定**接入点。
5. **`PMCond` 条件在复制通道上的实际过滤实现**（例如 OwnerOnly 是否按 `GetNetConnection()` 判定）未在本次范围内追踪；只知道「生成器支持、条件会进入描述符」。
6. **诊断模式与正式模式在 R6 的取舍**：本次未找到 R6 是否会禁用诊断投射物路径，**无法确定**。

---

## 四、已检查范围

**源码（逐文件读取/检索，均只读）**
- `Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs`（2814 行，通读）
- `Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs`（2263 行，重点段通读：1-560 / 895-1030 / 1038-1240 / 1917-1945）
- `Client/Assets/Scripts/Server/Boot/PMDsLobbyAgent.cs`（965 行，262-520 / 600-800 / 795-965）
- `Client/Assets/Scripts/Server/Boot/PMDsHost.cs`（ExitRequested / Pump 接线点）
- `Client/Assets/Scripts/PMR3/PMR3Player.cs`（536 行，全文）
- `Client/Assets/Scripts/PMR3/PMR3Runtime.cs`（418 行，全文）
- `Client/Assets/Scripts/PMR3/PMR4MovementDriver.cs`（2338 行；公开面 grep + 1130-1200 + 2100-2260）
- `Client/Assets/Scripts/PMR3/PMR5ProjectileDriver.cs`（3068 行；公开面 grep + 2640-2700）
- `Client/Assets/Scripts/PMR3/PMR5Projectile.cs`（`PMR5ProjectileEvents.Subscribe` 面）
- `Client/Assets/Scripts/PMNet/Declarations/PMNetDeclarations.cs`（647 行，属性/描述符面）
- `Client/Assets/Scripts/PMNet/PMWireType.cs`（`PMCond` 全文）
- `Client/Assets/Scripts/PMNet/Session/PMDsEntryOffer.cs`（全文）
- `Client/Assets/Scripts/PMNet/Control/PMDsControlProtocol.cs`（TeamId/HeroId/Result.WinnerTeamId 相关）
- `Client/Assets/Scripts/PMProjectile/PMProjectileCoordinator.cs`（`PMProjectileSettlement` :414）
- `Client/Assets/Scripts/PMProjectile/PMProjectileContracts.cs`（Hit/Sample/Key）
- `Client/Assets/Scripts/PMUnity/PMUnityMoverPresentation.cs`、`PMUnityBattlePresentation.cs`（公开面）
- `Client/Assets/Scripts/Server/Panel/UIMatchingPanel.cs`（分流点）
- `Server/DS/PMDsLobbyHost.cs`（gateway 接口/RecordResult/PublishEntryOffers 段）、`Server/Server/Server.cs`（`ServerLobbyClientGateway`）
- `Tools/PMNetGen/Program.cs`（CLI 契约）、`Tools/PMNetGen/DeclScanner.cs`（PMCond 支持列表）、`DeclEmitter.cs`（条件发射）
- `Docs/plans/pmnet-r3-ids.json`、`Client/Assets/Scripts/PMR3/Generated/PMNetGeneratedRegistry.g.cs`

**文档**：`AGENTS.md`、`Client/Assets/AGENTS.md`、`Docs/plans/net-r5-network-contract.md`、`Docs/plans/net-r3-control-contract.md`（§7/§8/§9）。

**未检查（边界外，按协议停止）**：`PMNet` 复制通道内部实现（`PMReplicationChannel`/描述符条件求值）、`net-architecture-migration.md` 全文（只按路由提及）、客户端大厅消息分发全集、`Shared/BattleNumericConfig` 数值表、R5/R6 测试门禁源码、任何 `Tools/*` 门禁工程。

---

## 五、建议下一步（最小补充查询 / 运行时验证 + 可独立写入边界）

### A. 建议的真实接点（按可独立写入边界组织，互不冲突）

| 边界 | 文件 | 建议内容（只描述，不含实现） |
|---|---|---|
| **B1 声明层** | `Client/Assets/Scripts/PMR3/PMR3Player.cs` + 重跑 `--decl-gen` | 新增 `[PMReplicated(PMCond.OwnerOnly)] private int _mana/_superEnergy`（资源）与 `[PMReplicated] private int _hp; [PMReplicated] private bool _alive;`（公共 HP/死亡），配 `[PMRepNotify]` 只做「通知/入队」，公开只读访问器 + `PMNet_Set_*` 包装写入器（沿用 `PublishMovementSnapshot` 的写法）。**必须**重跑 `--decl-check` 并同步重建 Lobby/客户端（ProtocolHash 变）。 |
| **B2 DS 死亡权威** | `PMDsSessionHost.cs` 私有区 | 在 `OnProjectileSettlement` 之后（新私有方法，例如 `ApplySettlementsToVitals()`）用 `TryResolveUidByNetId(settlement.Hits[i].TargetNetId)` 扣 HP；归零则 `_driversByUid[uid].ServerSubmitTrustedEffect(PMMoverEffectRequest.SetMode(PMMoverMode.Inactive))` 并置 `_alive=false`（经生成 setter）。**不要**在 `OnProjectileSettlement` 内直接改（保住「一条结算只计一次」的 F8 不变式与日志分离）。 |
| **B3 DS 历史 Alive** | `PMDsSessionHost.cs` `RecordProjectileHistory`/`IsProjectileTargetAuthoritative`（:2207、:2380） | `sample.Alive` 改为按权威 HP/死亡派生；`IsProjectileTargetAuthoritative` 增加 `alive` 项（fail closed）。 |
| **B4 winner 回收（Lobby→客户端）** | `Server/Server/Server.cs` `ServerLobbyClientGateway.NotifyMatchEnded` + 客户端大厅面板 | 按 `notice.Roster` 逐 uid 取活跃 `Client` 并 `client.Send(MainPack)`（承载 winner/summary 的新 code，形如 `TrySendEntryOffer`）；客户端按前缀严格识别、解码失败不回退旧链。DS/`PMDsLobbyAgent` **无需改**。 |
| **B5 客户端死亡表现** | `PMClientSessionHost.cs`（表现写入点）+ `PMUnity*Presentation.cs`（可选） | 在 `PumpMovement` 的 `ApplyPredicted/ApplyInterpolated` **之后**读新增的 `_alive` 做一次性表现切换（优先复用 `PMR4MovementDriver.EventDispatched` 若语义够用）；保持「表现唯一写入点」不拆出第二套权威。 |
| **B6 客户端他人 hero/team（若需要）** | 同 B1 + `PMClientSessionHost` | 新增 `[PMReplicated] _teamId/_heroId`（无条件，用于表现）；**绝不**读客户端本地选择。 |

### B. 最小补充查询（若进入实现前仍需证据）

1. `PMReplicationChannel` / 描述符如何对 `PMCond.OwnerOnly` 求值（确认「OwnerOnly 资源不会泄露给他人」），以及 `SetCustomIsActiveOverride` 是否必要。
2. `PMR4MovementDriver.Update/ApplyPending` 内部，确认 `EventDispatched` 与同帧 `GetPredictedSync()` 的先后（决定死亡表现能否复用该通道）。
3. 客户端大厅消息分发全集，确认结果通知 code 的最小冲突面。
4. `Shared/BattleNumericConfig` 是否已有 MaxHP / 技能消耗（决定 B2 的数值来源与是否要新表）。

### C. 运行时验证（代码完成后）

- DS 双客户端 + observer：命中 → HP 下降 → 归零 → `Inactive` 生效（历史 `Alive=false`、后续命中被拒）→ `SubmitResult(winner)` → ResultAck → DS 退出；客户端收到 winner。
- 断线回归：`Alive=false` 补样本 + `UnbindPlayer` 不回归（既有 F2 路径不被 R6 改坏）。
- 协议：`--decl-check` 退出 0；三端 ProtocolHash 一致；Lobby 用 `PMR3Runtime.ProtocolHash` 分配局。
