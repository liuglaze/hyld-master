# R5-B2b 声明网络驱动（PMR5ProjectileDriver）— 交付报告

> 范围：`Docs/plans/net-r5-network-contract.md`「B2b网络driver」+「B2b/C适配可并行边界」。
> 状态唯一源仍是 `Docs/plans/net-architecture-migration.md`；本文件只记录本批次**实际产物与实测证据**。
> 本批次**不含** C 的真实宿主接线（PMClientSessionHost / PMDsSessionHost 未创建本驱动）、不含 R6 伤害结算、不含 T45 实机。

---

## 1. 交付物

| 文件 | 变更性质 |
|---|---|
| `Client/Assets/Scripts/PMR3/PMR5ProjectileDriver.cs` | **新增**：会话级驱动 + `IPMR5ProjectileAuthorityPolicy` + `PMR5ProjectileFaultReason` + `PMR5ProjectileView` |
| `Client/Assets/Scripts/PMR3/PMR5ProjectileDriver.cs.meta` | 新增（noBOM+LF，guid `522f4b6c70a04ac3859731fd09a9118c`，全仓唯一） |
| `Client/Assets/Scripts/PMR3/PMR3Runtime.cs` | 追加（既有分支零改动）：`SendRemoteRpc` 对三条投射物 RPC 的失败路径**改抛明确异常** |
| `Tools/PMR5NetworkCheck/PMR5NetworkCheck.csproj` | **新增**：netstandard2.0 + C#7.3 语言面门禁（库，无 Program.cs） |
| `Tools/PMR5NetworkTest/PMR5NetworkTest.csproj` | **新增**：net8.0 真实字节链验收门禁 |
| `Tools/PMR5NetworkTest/Program.cs` | **新增**：12 段共 **226 项断言** |
| `Docs/plans/_r5_network_report.md` | 本报告 |

编码：两个 `.cs` 与 `Program.cs` 为 **UTF-8 BOM + CRLF**；`.meta` 与两个 `.csproj` 为 **noBOM + LF**（与同批既有产物一致）。

---

## 2. 精确 host API（驱动公开面，供后续 C/DS 宿主直接消费）

```csharp
namespace PMNet.R3
{
    public enum PMR5ProjectileFaultReason : byte { None=0, SendFailed=1, WorldNotAttached=2,
        OwnerNotReady=3, CoordinatorFaulted=4, OutputOverflow=5, InternalInvariant=6,
        NonMonotonicWallClock=7, PolicyMissing=8, ThreadViolation=9, Disposed=10 }

    public interface IPMR5ProjectileAuthorityPolicy      // DS 必须有；定义在驱动同文件
    {
        bool TryAuthorizeSpawn(PMR3Player player, PMProjectileSpawnIntent intent,
            out PMProjectileSpec trustedSpec, out PMVector3 ownerPosition,
            out PMActivationResult verdict, out string error);
    }

    public struct PMR5ProjectileView
    {   // Key / OwnerNetId / AuthorityNetId / MirrorObjectNetId / Position / PreviousPosition /
        // Velocity / Yaw / RadiusM / MoveTimeMs / Hidden / Stopped / LocalFake / TakenOver
    }

    public sealed class PMR5ProjectileDriver : IDisposable
    {
        public const int MaxTargetsPerHitRpc = 48;   // 上行命中 ≤48 目标/RPC
        public const int MaxVerifyPerKey     = 5;    // 与 PMProjectileLimits.MaxVerifyCalls 同源
        public const int MaxInboundSpawns = 256, MaxInboundHits = 256, MaxInboundDecisions = 512;
        public const int MaxInboundMirrors = 128, MaxUnboundMirrors = 128, UnboundMirrorTtlMs = 5000;
        public const int MaxDirtyViews = 4096, MaxApplyPerPump = 64;

        public PMR5ProjectileDriver(PMNetWorld world, PMNetSessionBridge bridge, uint epoch,
            PMProjectileHistory history,
            IPMR5ProjectileAuthorityPolicy policy = null,     // DS 必须提供
            IPMProjectileHostMotion hostMotion = null,        // fail-closed 语义由 Coordinator 持有
            IPMProjectileHitFilter hitFilter = null);         // null ⇒ 权威 accept-all（本批不接友伤规则）

        // 接线
        public bool BindPlayer(PMR3Player player);            // 建立该 player 的 .ProjectileDriver 适配
        public bool UnbindPlayer(PMR3Player player);
        public bool IsPlayerBound(PMR3Player player);
        public PMR3Player LocalOwner { get; }                 // 客户端：uid == 本地连接 uid 的副本
        public int LocalUid { get; }

        // 主线程步进（唯一 Pump）
        public void Pump(double wallNowMs, int stepMs);       // 墙钟单调；入站→运动/发布→Drain 全部有界

        // AP
        public bool TryFire(PMR3Player player, uint activationId, PMVector3 position, PMVector3 direction,
            float yaw, PMProjectileSpec localSpec, int predictionMs, double wallNowMs,
            out PMProjectileKey key, out string error);
        public bool SubmitPredictedHits(PMR3Player player, PMProjectileHitBatch batch, double wallNowMs,
            out int rpcCount, out string error);
        public int UplinkVerifyUsed(PMProjectileKey key);

        // DS 显式权威入口
        public bool ResolveActivation(uint ownerNetId, uint activationId, PMActivationResult outcome,
            double wallNowMs, out PMProjectileActivationApplyResult result);   // Pending 的显式后续入口
        public bool TryServerDirectSpawn(uint ownerNetId, uint projectileId, uint activationId,
            PMVector3 position, PMVector3 direction, float yaw, PMProjectileSpec trustedSpec,
            uint[] trustedAllowedTargets, int predictionMs, double wallNowMs,
            out PMProjectileKey key, out string error);                        // 唯一允许 ActivationId=0

        // C 表现消费（核心不引用 UnityEngine）
        public int CopyViews(PMR5ProjectileView[] buffer);
        public int DrainViewChanges(int max, out PMR5ProjectileView[] changes); // 增量：含「已移除」信号

        // R6 唯一结算消费点（本驱动不扣血）
        public event Action<PMProjectileSettlement> Settlement;

        // 观测
        public PMProjectileCoordinator Coordinator { get; }
        public PMProjectileHistory History { get; }  public IPMProjectileHostMotion HostMotion { get; }
        public IPMR5ProjectileAuthorityPolicy Policy { get; }
        public bool IsFaulted { get; }  public PMR5ProjectileFaultReason FaultReason { get; }
        public string FaultError { get; }  public int ReservationCount { get; }
        public int ViewCount { get; }  public int BoundPlayerCount { get; }
        public long Pumps, ServerSpawnsReceived, ServerSpawnsAuthorized, ServerSpawnsRejected,
            ServerSpawnsSpawned, ServerDirectSpawned, HitsReceived, HitsReported, HitRpcsSent,
            HitReportRejected, DecisionPayloadsQueued, DecisionsSent, DecisionsReceived, FakesCreated,
            FakesTakenOver, FakesRevoked, MirrorPayloadsQueued, MirrorPayloadsApplied,
            MirrorIdentityRejected, MirrorPositionGated, MirrorHiddenNoRevive, MirrorPayloadQueueDrops,
            UnboundMirrorsStaged, UnboundMirrorsExpired, UnboundMirrorsDropped, InboundQueueOverflows,
            ReservationsCancelled, AuthorityObjectsDestroyed, ViewDirtyOverflows,
            SettlementsHandedToR6, SettlementsWithNoConsumer, ThreadViolations;

        public void Dispose();   // 幂等：取消预留 + 销毁/注销本会话对象 + 摘自己的订阅 + 摘 player 接缝
        public string Describe();
    }
}
```

**`PMR3Runtime` 的唯一改动**：`SendRemoteRpc` 新增 `IsProjectileRpcId(rpcId)` 判定
（三条生成的稳定 ID：21590 / 33011 / 38620）。命中时「未接线 / 未就绪 / 桥拒绝」由原来的
`WarnInternal` 改为抛 `InvalidOperationException`；**movement / probe 的原行为逐字不变**。

---

## 3. 管线（逐步，可对照代码）

### 3.1 会话建立

```
Register → Attach(world, bridge)         // 注册 PMR5Projectile 工厂 + OnRep 分发表（B2a 已就位）
SpawnPlayer(world, bridge, ownerConn)    // DS 玩家副本：Authority + OwnerConnection
new PMR5ProjectileDriver(world, bridge, epoch, history, policy, motion?, filter?)
  └ 订阅 PMR5ProjectileEvents.Subscribe(world, Created, Updated, Destroyed)   // 按 world 筛选
BindPlayer(p)  × N                       // p.ProjectileDriver = 适配器；回调携带 p 身份
```

### 3.2 DS：上行生成 → 预留 → 上线

```
client: PMNet_ServerProjectileSpawnV1(payload)            // 生成桩（Reliable + ForceValidate）
  → DS: ServerProjectileSpawnV1 → ProjectileDriver.OnServerSpawnPayload   // 只入队（有界 256）
DS Pump:
  ApplyInboundSpawns → HandleServerSpawn(player, payload)
    1. authenticatedOwnerNetId = player.NetId.Value           // 认证源；payload 自报值只做一致断言
    2. player.OwnerConnection 必须存在且 IsReady              // 否则拒绝
    3. codec.TryDecodeSpawnIntent                             // 失败 ⇒ 拒绝（不 fault）
    4. 断言 epoch / origin=ClientPredicted / owner / activationId!=0
    5. world.TryReserveNetId(out token)                       // 先预留真实 NetId
    6. policy.TryAuthorizeSpawn(player, intent, out spec, out ownerPos, out verdict, out err)
       · false / spec==null ⇒ Cancel(token) + 账本 Rejected + 下行 Rejected
       · verdict==Rejected ⇒ Cancel(token) + 账本 Rejected + 下行 Rejected
       · verdict Pending|Confirmed ⇒ 账本写入（ResolveActivation）
    7. coordinator.RequestSpawn(request, owner, token.NetId.Value, owner, ownerPos, spec, …, wall)
       · !Admitted ⇒ Cancel(token) + 下行 Rejected（带 admission 原因）
       · Admitted ⇒ _reservations[key] = token                    // 令牌必须被记住
  DrainSpawnReady（同一 Pump）
       · 取 token；编码 PMProjectileSnapshot{State,Spec}
       · obj.Owner = player; obj.OwnerConnection = player.OwnerConnection   // 复制层 IsOwner 判定
       · obj.PublishProjectileSnapshot(payload)                // **初值先写好**
       · world.SpawnReserved(obj, token, PMR5Projectile.PMGeneratedClassId) // 再上线
       · bridge.RegisterReplicatedObject(obj)
  AdvanceAuthorityMotion(stepMs)  → coordinator.AdvanceMotion + PublishAuthoritySnapshot（仅变化时发布）
  DrainStopped                    → 补发最终 stopped 快照（墓碑期只发一次）
  DestroyExpiredTombstones        → wallNow ≥ entry.TombstoneUntilMs ⇒ bridge.DestroyObject（世界 + 注销）
  DrainDecisions                  → 逐条 PMProjectileDecision → PMNet_ClientProjectileDecisionV1(owner)
  SweepStaleReservations          → coordinator.PurgeExpired + 「既不挂起也从未上线」的预留一律 Cancel
```

### 3.3 DS：上行命中

```
client: SubmitPredictedHits(player, batch, wallNow)
   · 本地 owner + 身份一致 + 目标数 1..100
   · needed = ceil(targets / 48)；used + needed ≤ 5（每 key 配额，不重置）否则整次拒绝
   · 逐分批 codec 编码；任一 payload > 4096 ⇒ 整次拒绝（不做半发送）
   · 全部编码完成后再统一 PMNet_ServerProjectileHitV1（每包一条 RPC，各占 1 次 Verify）
   · 任意发送异常 ⇒ 会话 fault（SendFailed）
DS Pump: ApplyInboundHits → HandleServerHit
   · codec 解码 + owner/epoch 断言
   · coordinator.ReportHits(batch, authenticatedOwnerNetId, wallNow)  // 只经**宿主注入的真实 history** 验证
   · Settled / Pending / StashedForSpawn 计 HitsReported；其余 HitReportRejected
DrainSettlements → Settlement 事件（**R6 唯一消费点**；无订阅者时只计 SettlementsWithNoConsumer）
```

### 3.4 AP：TryFire → 假弹 → 接管/撤销

```
TryFire(player, activationId, pos, dir, yaw, localSpec, predictionMs, wallNow)
   · 本地 owner + identity 有效（epoch != 0、owner = player.NetId，均由驱动持有）
   · 每 owner 单 epoch 单调 projectileId（NextProjectileId）
   · coordinator.Lifecycle.TryRegisterPredicted(...)          // **先真实 Lifecycle 预测登记**
   · codec.TryEncodeSpawnIntent（上行**只含** Key/Activation/Position/Direction/Yaw/PredictionMs）
   · PMNet_ServerProjectileSpawnV1（生成桩；失败 ⇒ RevokeFake + fault）
AP Pump:
   AdvanceLocalFakes → coordinator.AdvanceMotion(key, stepMs, wallNow)   // ≤50ms 步；同一 fail-closed hook
       · Stopped/HostMotionFaulted/NotMovable ⇒ Lifecycle.NotifyPredictedEnded（墓碑保留）
   ApplyInboundDecisions → HandleClientDecision（只处理本地 owner 副本）
       · Rejected ⇒ Retire(key) + 移除假弹（**真正撤销**）
       · Confirmed ⇒ Lifecycle.TryTakeoverPredicted（一次性接管）
   ApplyMirrors → 解码快照 → 断言 Epoch + AuthorityNetId == obj.NetId + owner != 0
       · owner 未绑定 ⇒ 暂存（上限 128 / TTL 5000ms），BindPlayer 时立即兑现
       · Lifecycle.TryApplyMirror：GateFakeAlive / HiddenNoRevive / StoppedNotMoved / Applied / UnknownKey
       · 权威副本出现 ⇒ 若假弹尚未接管则一次性接管（无双弹）
   RebuildViews → 整体重建 + 脏集合（新增/变化/移除都通知 C）
```

### 3.5 SP

纯镜像：`BindPlayer` 后只消费 `PMR5ProjectileEvents` 的 Created/Updated/Destroyed；
`_isServer == false` ⇒ `SubmitPredictedHits` 直接拒绝；`LocalOwner == null` ⇒ 不处理裁决；
其本地账本没有该 key（预测登记在 owner 客户端），因此 `TryApplyMirror` 返回 `UnknownKey`
被显式按「已应用」计数（这是 SP 的**正常路径**，不是失败）。

---

## 4. 契约落地映射（要点）

| 契约要求 | 落点 |
|---|---|
| `ctor World+Bridge+epoch+PMProjectileHistory+可空policy+可空motion` | 构造签名逐参对齐（另加可选 hitFilter，不破坏冻结签名） |
| 接口 policy 定义同文件、DS 必须有 | `IPMR5ProjectileAuthorityPolicy` 在本文件；DS 无 policy ⇒ `Fault(PolicyMissing)` |
| callback 带 player 身份 | `PlayerAdapter` 持有 `PMR3Player`，三条回调全部携带它 |
| 绝不采信 payload owner/spec | owner 取 `player.NetId.Value`；spec/ownerPos/verdict 只来自 policy |
| DS 先 `TryReserveNetId`，`RequestSpawn` 用 token.NetId.Value | `HandleServerSpawn` 第 5/7 步 |
| 拒绝/TTL/dispose ⇒ Cancel | policy/admission 拒绝立即 Cancel；`SweepStaleReservations` 覆盖 TTL；`Dispose` 全量 Cancel |
| SpawnReady 才 PublishSnapshot→SpawnReserved→Register | `DrainSpawnReady`（顺序即契约顺序） |
| 权威 object owner 绑定真实玩家 | `obj.Owner = player`（`PMR5Projectile` 未覆写 `GetNetConnection` ⇒ 复制层 `IsOwner` 判定沿 Owner 链） |
| 持续状态只经生成 snapshot 复制 | 唯一写入口 `PublishProjectileSnapshot`（生成 setter 标脏）；无 socket / 无 MainPack / 无手工广播 |
| 墓碑期停止快照直到 DestroyObject+注销 | 墓碑期不再重发（payload 未变）；到期 `bridge.DestroyObject` |
| 生成 RPC 唯一发送入口 | 三条 RPC 全部走生成桩（`PMNet_*`） |
| TryFire 需要本地 owner AP + identity，单调 id | `LocalOwner` 校验 + `NextProjectileId` |
| 先 Lifecycle 预测登记再上行 | `TryRegisterPredicted` → 编码 → 发送 |
| 本地直线 ≤50ms，可用 motion 接口 | `coordinator.AdvanceMotion`（stepMs 由 Pump 钳到 ≤50） |
| 权威 Decision 经生成可靠接缝排队 | `_inboundDecisions`（上限 512，溢出即 fault）只入队，Pump 应用 |
| Rejected 必须真正移除/隐藏 | `RevokeFake` ⇒ `Lifecycle.Retire` + 从 `_fakes` 移除（视图随之消失） |
| Confirmed 与 Create 不同顺序正确 | 接管不依赖顺序（`FakesTakenOver` 两种顺序都成立） |
| Create+OnRep 校验 Epoch+AuthorityNetId==obj.NetId+owner | `ApplyMirrorObject` 的身份断言；不符 ⇒ `MirrorIdentityRejected`，不污染 |
| 假弹存活位置闸门 / 一次接管无双弹 / 迟到镜像不复活 | `TryApplyMirror` 四分支 + `_fakes` 与 `_mirrors` 的展示优先级 |
| SP 不收上行命中 | `_isServer == false` ⇒ `SubmitPredictedHits` 直接拒绝 |
| public view 枚举 / DrainViewChanges，不引用 Unity | `PMR5ProjectileView` + `CopyViews` / `DrainViewChanges`（整体重建 + 脏集合） |
| 事件按 world 过滤 + 各自 Dispose + 有界入队 + Pump 主线程应用 | `Subscribe(world, …)` + `OnMirror*` 只入队（上限 128，当前状态语义丢最旧保最新） |
| 未绑 player 的镜像暂存上限+TTL | `_unboundMirrors`（128 / 5000ms）+ `ReplayUnboundMirrorsFor` |
| 命中分 ≤48 + 事前检查 RPC 数与剩余配额 5 + >4096 拒 + 失败不吞 | `SubmitPredictedHits` 四道检查；发送异常 ⇒ `Fault(SendFailed)` |
| 输出队列满 / CoordinatorFaulted ⇒ session fault | `Fault` 状态机 + 所有 coordinator 调用捕获 `InvalidOperationException` |
| Pump 墙钟单调 + 全部批次有界 | 单调校验（倒退即 fault）+ `MaxApplyPerPump=64` |
| DrainSettlements 保留 R6 唯一消费、不假称扣血 | `Settlement` 事件 + 计数；无消费者时只记 `SettlementsWithNoConsumer` |
| history 由宿主注入、不伪造 | `_history` 只读传给 Coordinator；驱动**从不**写入 history |
| 断线/Dispose 清本会话，不清别的 world | `Dispose` 只摘自己的订阅；不调用 `PMR5ProjectileEvents.ClearAll`（那是进程级 Shutdown） |

---

## 5. 测试与实测数据（全部本次 build + run，退出码 0）

| 命令 | 结果 |
|---|---|
| `dotnet build Tools/PMR5NetworkCheck -c Release` | **0 警告 / 0 错误**（netstandard2.0 + C#7.3，零 UnityEngine） |
| `dotnet build Tools/PMR5NetworkTest -c Release` | **0 警告 / 0 错误** |
| `dotnet Tools/PMR5NetworkTest/bin/Release/net8.0/PMR5NetworkTest.dll` | **通过 226 / 失败 0，exit 0** |
| `PMR5DeclarationTest`（本批回归） | 139 / 0 |
| `PMR3RuntimeTest`（本批回归，验证 `SendRemoteRpc` 改动未破坏原行为） | 180 / 0 |
| `PMR3IntegrationTest`（本批回归） | 143 / 0 |
| `dotnet build Tools/PMR3UnitySmoke -c Release` | 0 警告 / 0 错误 |

### 5.1 PMR5NetworkTest 分段（226）

| 段 | 覆盖 | 关键实测值 |
|---|---|---|
| A | `--decl-check` 退出码 0；新旧 ID 逐条；三条 RPC 方向/可靠性/校验档；常量一致性 | 旧 ID 405815557 / 18801 / 34232 / 25428 未漂移；新 ID 227098277 / 6683 / 21590 / 33011 / 38620 |
| B | 真实链路 AP TryFire→DS 授权→SpawnReserved 上线→AP/SP 收敛 | `ServerSpawnsReceived≥1`、`policy.Calls==1`、`Authorized==1`、`Spawned==1`（无双弹）、`ReservationCount==0`；AP `FakesTakenOver≥1`、视图 `AuthorityNetId==MirrorObjectNetId==权威 NetId`；SP `LocalFake==false`；**权威位置沿 +Z 真实推进** |
| C | 非 owner 冒名拒 + 旧 epoch 拒 | `ServerView(1).RpcRejected` 增长、`Ds.ServerSpawnsReceived==0`、`policy.Calls==0`；旧 epoch：`Received≥1 / Authorized==0 / Spawned==0`、owner `DecisionsReceived≥1`；`TryFind(999)` 不存在（不伪造权威 ID） |
| D | Pending 无 Create + 显式兑现 + 拒绝撤销预留 | Pending：`Spawned==0`、`ReservationCount==1`、`IsSpawnPending==true`、客户端 `ViewCount==1`（只有假弹）且 `MirrorObjectNetId==0`；`ResolveActivation(Confirmed)` ⇒ `Spawned==1 / ReservationCount==0`；策略拒绝 ⇒ `ReservationCount==0`、`ReservationsCancelled≥1`、`FakesRevoked≥1`、被撤销视图消失；**重复确认 ⇒ `AlreadyResolved` 且不重复生成** |
| E | 弱网裁决 | 真实丢包（`Hub.Dropped≥1`）后可靠重传仍 `FakesRevoked≥1`；同裁决重复投递两次 ⇒ 撤销计数不增；接管后迟到反向 Rejected ⇒ 视图不被撤销 |
| F | 顺序鲁棒 | 真实顺序：镜像对象已绑定 + 接管；**伪顺序**（声明接缝先把 Confirmed 投给 AP，再放开被扣住的上行）⇒ 一次接管、`ServerSpawnsSpawned==2`（两条各一颗，无双弹）、第二颗视图已绑定权威镜像对象 |
| G | 迟到镜像 | 假弹（16ms 寿命）先本地结束（视图 `Stopped`），随后镜像才到 ⇒ `MirrorHiddenNoRevive≥1`、`FakesTakenOver==0`、视图 `Hidden==true` |
| H | stop→墓碑→Destroy | ServerDirect 上线后权威 `Stopped`；客户端看到停止快照；越过 150ms 墓碑 ⇒ `AuthorityObjectsDestroyed≥1`、`ViewCount==0`、DS 与 owner 世界都不再有该对象、`DrainViewChanges` 给出「已移除」信号 |
| I | 分包 / 配额 / 4096 / 结算 | 100 目标 ⇒ `rpcCount==3`、`HitRpcsSent==3`、`UplinkVerifyUsed==3`、DS `HitsReceived≥3`；第二次 100 目标（3+3>5）⇒ **整次拒绝**（`rpcCount==0`、错误含「配额」）；codec 单包 100 目标 **>4096 字节**（故必须 ≤48）；生成桩对 4097 字节数组实参抛 `FormatException`；真实命中（宿主记录 history 样本）⇒ `Settlements.Count≥1` 且 `Origin==ClientPredicted`、`HitCount≥1` |
| J | 资源回收 | Dispose 后 `ReservedNetIdCount==0`、双方世界都摘除对象、player 接缝引用为 null、`Dispose` 幂等；客户端 Dispose 精确摘掉自己 1 张订阅；释放第一个 harness 不清第二个 harness 的订阅且后者事件链仍工作 |
| K | 发送失败可见 | 正常路径不 fault；`PMR3Runtime.Detach(clientWorld)` 后 `TryFire` 返回 false、`IsFaulted`、`FaultReason==SendFailed`、`FaultError` 非空；fault 后 `Pump` 不再推进墙钟、`TryFire` 继续失败 |
| L | 多 world 隔离 | 两 harness 各 3 张订阅（基线+6）；A 的事件不串到 B（B 的 `MirrorPayloadsApplied==0`）；A 的 DS 退出后 B 仍收到自己的事件；全部释放后订阅回到基线 |

**测试纪律**：上行/下行全部走**真实生成桩 + 真实 `PMTransport` 字节链**（手工 link + 手工 hub，
与 `PMR3RuntimeTest` / `PMR4NetworkTest` 同一手法），版本不与实现共享；除 F 段「决策先于 Create」
与 E 段「重复投递」两处**显式标注**使用声明接缝（`player.ProjectileDriver.OnClientDecisionPayload`，
即生成 RPC 的业务实现入口）构造外部时序外，不直接调用业务方法冒充网络。

### 5.2 唯一一条运行时告警（预期）

```
[PMR5ProjectileDriver] 会话失败（SendFailed）：ServerProjectileSpawnV1 发送失败：
InvalidOperationException [PMR3Runtime] 投射物 RPC 发送失败：目标所属世界没有接线（ClassId=405815557 RpcId=21590）。
```
即 K 段刻意制造的「未接线发送」路径——它是**可见的失败**，正是本批要求的语义。

---

## 6. 未接线限制与剩余问题（诚实口径）

| # | 项 | 说明 |
|---|---|---|
| **B1** | **6 个既有门禁工程的 Include 面需要补 `PMProjectile/**`（本轮唯一硬阻塞）** | 见 §7。属**写入边界之外**的 csproj，本批只读并报告。 |
| B2 | 真实 Unity 宿主未接线 | `PMClientSessionHost` / `PMDsSessionHost` 未创建本驱动；C 仍需消费 `CopyViews`/`DrainViewChanges` 并把 `Settlement` 交给 R6。**本批不声称 T45 或 C 已完成。** |
| B3 | 命中过滤器 | 未注入 `hitFilter` 时用「权威 accept-all」。队伍/友伤/部位规则后置（R6 才有伤害）；`BoneName` 精细校验仍不在本批。 |
| B4 | 无 R6 消费者 | `Settlement` 只是事件出口；本批不存在「已扣血」的任何断言。 |
| B5 | `>4096` 分支在 48 目标分包下不可达 | 48 目标的载荷实测 ≈2600 字节，因此驱动的「分包 >4096 ⇒ 整次拒绝」是**防御性分支**（未构造出可达用例）。真正的 4096 门由生成桩（发送侧 `FormatException`）与声明层 `ForceValidate` 守住，两者都在 I 段被真实断言。 |
| B6 | 「裁决先于 Create」用声明接缝构造 | 同一连接的 Create 与可靠 RPC 共享同一条**可靠有序流**，线上自然顺序恒为 Create 先到；因此该顺序用生成 RPC 的业务实现入口（即声明接缝）构造，已在测试注释与本节显式标注，不冒充实机时序。 |
| B7 | 视图重建成本 | `RebuildViews` 每 Pump 整体重建（弹数 ≤1024+1024），是本批的显式取舍：换来「移除也一定通知 C」；C 侧若成为热点可后置为增量式。 |
| B8 | `PMR3Runtime.SendRemoteRpc` 的抛异常面 | 只对三条投射物 RPC 生效（按生成 ID 判定）。若主侧希望 `PMR5ProjectileDriver` 之外也统一为结构化错误出口，需要主计划层面的契约变更。 |

---

## 7. 硬阻塞：4+2 个门禁工程的 Include 面（不在本批写入边界内）

`PMR5ProjectileDriver.cs` 按契约必须位于 `Client/Assets/Scripts/PMR3/`（生成目录 = 该目录，
且 `PMNetGeneratedRegistry` 是单一静态类型，声明集合不可分裂），而它**必须**引用
`PMNet.Projectile`（ctor 收 `PMProjectileHistory`，内部用 `PMProjectileCoordinator` / codec）。
于是所有以 `PMR3\**\*.cs` 通配的工程都会把它编进来，但它们没有编 `PMProjectile/**`：

| 门禁 | 失败归属 | 需要的修复（一行 Include） |
|---|---|---|
| `PMR4NetworkCheck` | **本批文件**（`PMR5ProjectileDriver.cs` 未解析 `PMNet.Projectile`） | `Client/Assets/Scripts/PMProjectile/**/*.cs` |
| `PMR4NetworkTest` | **本批文件** | 同上 |
| `PMR4IntegrationTest` | **本批文件** | 同上 |
| `PMR4UnityCheck` | **并行 C 组的 `PMUnity/PMUnityProjectile*.cs`**（+ 本批文件） | 同上 |
| `PMClientCheck` | **并行 C 组的 `PMUnity/PMUnityProjectile*.cs`**（不含本批文件：该工程逐文件列举 PMR3，未列 driver） | 同上 |
| `PMUnityGlueCheck` | **并行 C 组的 `PMUnity/PMUnityProjectile*.cs`**（同上） | 同上 |

已实测确认：`PMClientCheck` / `PMUnityGlueCheck` 的错误**全部**落在 `PMUnity/PMUnityProjectileMotion.cs` /
`PMUnityProjectilePresentation.cs`，与本批文件无关（这两个工程根本没有 Include 驱动文件）；
`PMR4UnityCheck` 的报错首条同样是 C 组文件。也就是说：**同一处 include 缺口已由并行 C 组先行引入**，
本批只是把它扩大到另外 3 个门禁。

**未做**：没有修改这 6 个 csproj（超出本批写入边界）。已在本报告与最终摘要中显式上报。
修法就是给它们各加一行
`<Compile Include="..\..\Client\Assets\Scripts\PMProjectile\**\*.cs" LinkBase="PMProjectile" />`。

---

## 8. 已检查范围

**完整读取的文档（按委派顺序）**：`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`（全文）
→ `Docs/plans/net-r5-network-contract.md`（全文）→ `Docs/plans/net-r5-projectile-contract.md`（全文）
→ `Docs/plans/_r5_declaration_report.md`（全文）→ `Docs/plans/_r5_coordinator_review.md`（全文）
→ `Docs/plans/_r5_codec_report.md`（全文）→ `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`（全文）；
另按需读 `Docs/plans/_r5_host_survey.md`、`Docs/plans/net-architecture-migration.md`（T5B2a/b/c 行）。

**完整/必要段读取的实现**：`PMR5Projectile.cs`（全文）、`PMR3Player.cs`（全文）、`PMR3Runtime.cs`（全文）、
`PMProjectileContracts.cs`（全文）、`PMProjectileCoordinator.cs`（公开面 + RequestSpawn/ServerDirectSpawn/
ResolveActivation/ReportHits/AdvanceMotion/CatchUpMotion/PurgeExpired/ClearOwner/ResetState/AdvanceEpoch/
Drain*/StepMotion/CheckClock）、`PMProjectileLifecycle.cs`（TryRegisterPredicted/TryTakeoverPredicted/
NotifyPredictedEnded/TryApplyMirror/TryAdvanceMotion/TryRecordStop/TryRegisterAuthority/Promote + 公开枚举）、
`PMProjectileCodec.cs`（公开面与常量）、`PMProjectileValidator.cs`（L0–L4 与 PassesL2Budget）、
`PMProjectileHistory.cs`（公开面与 Record/TryResolve）、`PMNetWorld.cs`（预留段与生命周期应用）、
`PMNetSessionBridge.cs`（SendRpc/ResolveOwnerConnection/Register/Destroy）、`PMNetObject.cs`、
`PMNetRole.cs`、`PMNetIdentity.cs`、`PMRpc.cs`（callspace 与归属校验）、`PMTransportConnection.cs`（TryActivate/身份）、
`PMR4MovementDriver.cs`（全文，作为同构驱动范本）、`Tools/PMR4NetworkTest/Program.cs`（夹具手法）、
`Tools/PMR5DeclarationTest/*.csproj` 与 `Tools/PMR4*/**.csproj`（Include 面）。

**本次写入的文件（仅本批允许的 4 个 + 报告）**：
`Client/Assets/Scripts/PMR3/PMR5ProjectileDriver.cs`（+ `.meta`）、
`Client/Assets/Scripts/PMR3/PMR3Runtime.cs`（追加 1 处）、
`Tools/PMR5NetworkCheck/PMR5NetworkCheck.csproj`、`Tools/PMR5NetworkTest/PMR5NetworkTest.csproj`、
`Tools/PMR5NetworkTest/Program.cs`、`Docs/plans/_r5_network_report.md`。

**未做**：未启动 Unity；未执行 SVN/git 写操作；未提交；未递归委派；未改主计划/契约/生成物/锁文件；
未改 `PMProjectile/**` 核心、未改 `PMNet/**`、未改 `PMR5Projectile.cs`；未修改 §7 列出的 6 个 csproj。
