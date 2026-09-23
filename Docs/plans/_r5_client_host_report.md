# R5-C 客户端真实宿主接线 + 可测试候选收集器（实现报告）

> 任务类型：**comprehensive**（客户端宿主接线 + 新增纯 C# 候选收集器 + 门禁/回归 + 报告）
>
> 只读输入（按委派顺序）：`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`（全文）
> → `Docs/plans/net-r5-network-contract.md`（全文，末段「C宿主首个可执行入口」为准）
> → `Docs/plans/_r5_network_review.md`（driver 新增 API 段）
> → `Docs/plans/_r5_unity_adapter_report.md`（C1/C2 精确 API 面）
> → `Client/Assets/Scripts/PMProjectile/PMProjectileDiagnosticConfig.cs`（只读）
> → `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`。
>
> 只读读过的生产代码：`PMClientSessionHost.cs`（全文）、`PMR5ProjectileDriver.cs`（公开面 + 客户端分支全文）、
> `PMProjectileContracts.cs`（全文）、`PMProjectileValidator.cs`（全文，几何口径来源）、
> `PMProjectileHistory.cs`（全文，帧锚/流量解析口径）、`PMUnityProjectileMotion.cs` / `PMUnityProjectilePresentation.cs`
> （公开面）、`PMR4MovementDriver.cs`（公开面：`SamplePresentation` / `StreamVersion` / `GetPredictedSync`）、
> `PMMover/PMMoverState.cs`（`PMVector3` / `PMMoverSyncState.Scale` / `PMMoverDefaults`）、
> `PMNet/PMNetIdentity.cs`（`PMFrameId` / `PMFrameDomain`）、`PMUnity/PMR3TestScene`（在 `PMDsSessionHost.cs` 内）、
> `PMUnity/PMUnityBattleMap.cs`（公开面）、`PMR3/PMR3Player.cs`（公开面）。
>
> **未依赖并行 DS 改动**（`PMDsSessionHost` 一行未动）；未启动 Unity、未点菜单、未提交（git/svn 无写操作）、
> 未递归委派、未改共享契约/生成物/锁文件/其它组文件。单位：**Y-up 米、Yaw 度**。

---

## 0. 交付物与验证命令（本机实测）

| 文件 | 性质 | 编码 |
|---|---|---|
| `Client/Assets/Scripts/PMProjectile/PMProjectileCandidateCollector.cs` | **新增**：纯 C# 候选收集器 | UTF-8 **BOM + CRLF** |
| `Client/Assets/Scripts/PMProjectile/PMProjectileCandidateCollector.cs.meta` | **新增** meta（唯一 GUID `b7c1d9a4e3f54a3d8c2b6f1e7a5d0c93`） | **无 BOM + LF** |
| `Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs` | **修改**：R5-C 宿主接线（+约 700 行） | UTF-8 **BOM + CRLF**（原样保持） |
| `Tools/PMProjectileCandidateTest/PMProjectileCandidateTest.csproj` | **新增**：收集器验收工程（net8.0 真跑） | 无 BOM + LF |
| `Tools/PMProjectileCandidateTest/Program.cs` | **新增**：123 项断言 | UTF-8 **BOM + CRLF** |
| `Tools/PMUnityGlueCheck/UnityStubs.cs` | **修改**：`KeyCode.F = 102`（真实 Unity API，stub 缺失） | UTF-8 **BOM + CRLF**（原样保持） |
| `Docs/plans/_r5_client_host_report.md` | 本报告 | UTF-8 **BOM + CRLF** |

```bat
REM ① 客户端宿主 + 收集器 + 驱动的真实编译门（netstandard2.0 + C#7.3 + Unity 形状替身）
dotnet build Tools/PMClientCheck -c Release -o Tools/PMClientCheck/bin/r5-client-host    REM 0 警告 / 0 错误

REM ② Unity 胶水门（含 PMClientSessionHost.cs 与 PMUnityProjectile* 适配器）
dotnet build Tools/PMUnityGlueCheck/PMUnityGlueCheck.csproj -c Release                  REM 0 警告 / 0 错误

REM ③ 收集器验收（net8.0 真跑真实收集器源码）
dotnet build Tools/PMProjectileCandidateTest/PMProjectileCandidateTest.csproj -c Release
dotnet Tools/PMProjectileCandidateTest/bin/Release/net8.0/PMProjectileCandidateTest.dll  REM 123 通过 / 0 失败，exit 0
```

| 命令 | 结果 |
|---|---|
| `PMClientCheck`（`-o bin/r5-client-host`） | **0 警告 / 0 错误** |
| `PMUnityGlueCheck` | **0 警告 / 0 错误** |
| `PMProjectileCandidateTest`（build） | **0 警告 / 0 错误** |
| `PMProjectileCandidateTest`（run） | **通过 123 / 失败 0**，退出码 **0** |
| **全部 12 个 glob `PMProjectile/**` 的消费者**（见 §5.3） | 全部 **0 错误 / 0 警告** |
| `PMR5UnityAdapterTest`（glob `PMProjectile/**` 的既有回归） | **74 通过 / 0 失败**，exit 0 |
| 编码自检 | `.cs`/`.md` = BOM + CRLF（无裸 LF）；`.meta`/`.csproj` = 无 BOM + LF |

---

## 1. 新增：`PMProjectileCandidateCollector`（纯 C# 候选收集器）

命名空间 `PMNet.Projectile`（与契约/验证器同命名空间，便于按 `(Key, Target)` 语义阅读）。

### 1.1 精确 API 面

```csharp
public enum PMProjectileCandidateRejectReason : byte { /* 23 个值，见 §1.3 */ }

public struct PMProjectileCandidateSegment
{
    public PMProjectileKey Key;          // 必须 owner == 本地 owner 且 origin == ClientPredicted
    public PMVector3 PreviousPosition;   // 本子步线段起点
    public PMVector3 Position;           // 本子步线段终点（DS 的 L2 预算锚也用它）
    public float RadiusM;                // 弹半径（可信 spec）
    public bool Stopped;                 // 终态 → 本次是收官段（提交后关闭该 key）
}

public sealed class PMProjectileCandidateCollector
{
    public const int   MaxTargetsPerProjectile = 100;   // 每弹去重上限
    public const int   MaxKeys                 = 1024;  // key 数上限
    public const float HitToleranceM           = PMProjectileLimits.HitToleranceM;  // 0.30m
    public const float MaxSegmentM             = PMProjectileLimits.MaxSegmentM;    // 20m

    public PMProjectileCandidateCollector(uint epoch, uint localOwnerNetId);   // 两者都必须非 0

    public uint Epoch { get; }               public uint LocalOwnerNetId { get; }
    public int  KeyCount { get; }            public int  PendingKeyCount { get; }
    public long Collected { get; }           public long Rejected { get; }
    public long Commits { get; }             public long Rollbacks { get; }   public long ForgottenKeys { get; }

    public bool TryCollect(PMProjectileCandidateSegment segment, PMProjectileTargetSample target,
                           out PMProjectileHitCandidate candidate,
                           out PMProjectileCandidateRejectReason reason);

    public int Collect(PMProjectileCandidateSegment segment, IList<PMProjectileTargetSample> targets,
                       List<PMProjectileHitCandidate> results);        // 追加，返回追加条数

    public bool Commit(PMProjectileKey key);      // 在途 → 去重账（**只在发送成功后调**）
    public bool Rollback(PMProjectileKey key);    // 丢弃在途（发送失败 → 不占位）
    public bool Forget(PMProjectileKey key);      // 视图移除/退出 → 退休该 key
    public void Clear();                          // 会话退出

    public int  CommittedTargetCount(PMProjectileKey key);
    public int  PendingTargetCount(PMProjectileKey key);
    public bool HasPending(PMProjectileKey key);
    public int  CopyPending(PMProjectileKey key, PMProjectileHitCandidate[] buffer);
    public string Describe();
}
```

**依赖面（硬）**：只 `System` / `System.Collections.Generic` / `PMNet.Mover`（`PMVector3`）+ 同命名空间契约类型。
**文件内不出现** `UnityEngine`、`PMNet.R3`、`PMNet.Session` —— 这正是"防 core 其它测试 glob 引循环"的落地：
有 12 个工程用 `PMProjectile/**` 通配 include，其中 `PMR5UnityCheck` / `PMR4UnityCheck` 编真实 Unity DLL、
`PMR3UnitySmoke` 编旧玩法层；本文件若引入 Unity 或 PMR3 会把循环依赖灌进这些工程。

### 1.2 判定算法（与 DS 的 L3 **逐式一致**）

1. **弹自证**：`Key.IsValid` → `Key.Epoch == epoch` → `Key.OwnerNetId == localOwnerNetId` →
   `Key.Origin == ClientPredicted` → 端点有限 → 半径有限且 ≥ 0 → 线段长度 ≤ 20m。
2. **目标自证**：`NetId != 0` → `NetId != localOwnerNetId` → `Epoch == epoch` → `StreamVersion != 0` →
   `ServerFrame` 是 `AuthorityServer` 域 → `Alive` → 位置有限 → `RadiusM > 0` 且 `HalfHeightM >= RadiusM`。
3. **去重账**：已提交 → `AlreadyCommitted`；在途 → `AlreadyPending`；停止段已提交 → `StopSegmentAlreadyReported`。
4. **几何**（与 `PMProjectileValidator` 的 L3 完全同口径）：
   - 阈值 `threshold = 弹半径 + 目标半径 + 0.30m`；
   - 目标中心线 = 竖直胶囊轴线段：`halfLength = HalfHeightM - RadiusM`，
     `bottom/top = (px, py ∓ halfLength, pz)`（Y-up）；
   - **线段–线段最短距离**：Ericson 稳健算法（双精度），覆盖"两段皆点 / 单段退化 / 平行共线 / 端点钳制"，
     与验证器 `SegmentSegmentDistance` 同一实现（本文件多回传参数 `s/t`）；
   - `distance > threshold` → `GeometryMiss`（真实未命中，不是错误）。
5. **impact = 候选中心线附近点**：取弹线段上离中心线最近的点（必落在阈值内），
   最终值由 DS 的 L3 消毒（`ClampImpactToThreshold`）决定；`VisualOffset` **恒为 0**（首批不支持额外视觉偏移）。
6. **产物**：候选携带 `TargetNetId / TargetStreamVersion / TargetServerFrame / ImpactPoint / VisualOffset`，
   写入该 key 的**在途**集合（此时**还**不参与去重）。

### 1.3 拒绝原因（23 值，全部可观测）

`None`、`SegmentKeyInvalid`、`SegmentEpochMismatch`、`SegmentOwnerNotLocal`、`SegmentOriginNotPredicted`、
`SegmentNotFinite`、`SegmentRadiusInvalid`、`SegmentTooLong`、`KeyCapacityExceeded`、`TargetIdInvalid`、
`TargetIsOwner`、`TargetEpochMismatch`、`TargetNotAlive`、`TargetSizeInvalid`、`TargetPositionNotFinite`、
`TargetStreamInvalid`、**`TargetServerFrameInvalid`**、`TargetCapacityExceeded`、`AlreadyCommitted`、
`AlreadyPending`、`StopSegmentAlreadyReported`、`GeometryMiss`、`NumericOverflow`。

> `TargetServerFrameInvalid` 是契约「**不拿本地 input frame 冒充 DS 权威帧**」的唯一判据：
> 目标样本的 `ServerFrame` 必须是 `PMFrameDomain.AuthorityServer` 的合法帧，`Input` 域或 `None` 一律拒。

### 1.4 三条关键语义（本组最容易搞错的点）

| 语义 | 落地 |
|---|---|
| **失败发送不永久预占去重** | `TryCollect` 只写**在途**；`Commit` 才并入去重账；宿主发送失败必须 `Rollback`。宿主实现见 §4。 |
| **停止只允许最后一段** | `segment.Stopped` 的在途候选提交后置 `StopCommitted`，此后该 key **任何**目标一律 `StopSegmentAlreadyReported`（有界、无无界重复）；回滚则不落该标记。 |
| **接管后仍上报自己的预测来源** | 收集器**没有** `LocalFake/TakenOver` 这类输入；判定只看 `owner + origin + 终态`。宿主侧同样只按 `Key.Origin == ClientPredicted && Key.OwnerNetId == 本地` 决定是否收集（见 §4）。 |

---

## 2. 宿主接线：`PMClientSessionHost`（客户端）

### 2.1 会话建立（`Enter`）

在**两条模式分支各自**给出「本局实际物理场景 + 白名单 Collider」，随后统一构造投射物链：

| 模式 | 物理场景 | 白名单来源 |
|---|---|---|
| 诊断（`digest == 0x52334201`） | `PMR3TestScene.BuildIsolated` 的 `movementPhysicsScene` | `TryCollectColliders` 收集到的地板/墙（**去掉未填充槽位**） |
| 正式（其它非 0 digest） | `PMUnityBattleMap.PhysicsScene` | `PMUnityBattleMap.Colliders` |

```csharp
session.ProjectileHistory    = new PMProjectileHistory(offer.Epoch);   // 空实例：客户端从不 Record
session.ProjectileViewBuffer = new PMR5ProjectileView[512];            // 有界视图快照缓冲
session.ProjectileMotion     = new PMUnityProjectileMotion(physicsScene, colliders, layerMask);
session.ProjectileDriver     = new PMR5ProjectileDriver(world, bridge, offer.Epoch,
                                                        session.ProjectileHistory,
                                                        null,                    // policy = null（客户端）
                                                        session.ProjectileMotion); // hostMotion = 撞墙即停
PMR3Player.ProjectileWarn = ← 本会话日志出口（退出无条件还原）
```

- **history 是空占位**：客户端**不记录**任何目标样本 —— DS 才是历史唯一来源；该实例只是驱动的构造入参，
  绝不伪造 DS 历史（契约原文要求）。
- **policy = null**：客户端不持有可信授权策略（`TryFire` 用本机 spec，上行 payload 不携带 spec）。
- **hostMotion = 上面的 motion**：本地预测弹因此具备「撞墙即停」的真实几何（coordinator 的 fail-closed hook）。
- 任一环节失败（白名单空/项无效、motion 构造抛、driver 构造抛）→ `ReleaseSession` + 显式报错 + **不回旧链**。

### 2.2 每个副本绑定（`OnPlayerReplicated`）

- `EnsureMovementRig` 成功后立刻 `EnsureProjectileBinding(session, player)`：**AP 与 SP 都绑**（SP 也要能收权威镜像）；
  失败即 `Fail`（拒绝以探针假成功掩盖）。
- 首次拿到 **AP** 副本时构造候选收集器：`new PMProjectileCandidateCollector(offer.Epoch, player.NetId.Value)`。
  > 为什么在这里：`localOwnerNetId` 是收集器的**构造入参**，而 `offer.Identity.Uid` **不是** NetId；
  > AP 的 NetId 只有在其副本复制下来之后才可知。拿到之前**不收集任何候选**（而不是猜一个）。
- 另有每帧一次的**补绑定扫描**（`PumpProjectiles` 开头，遍历 `session.Movements`，幂等、有界）：
  覆盖"本帧之前就已存在但漏接"的副本。

### 2.3 每帧时序（`PumpActive`）

```
session.TickCount++
[Faulted] → FrozenWallClockPump（只推墙钟）→ return
[Endpoint 释放/ClientFailed] → Fail → return
Physics.SyncTransforms()                        // 唯一调用点，每帧一次
endpoint.Pump(nowMs, nowUnix)                   // 含 bridge.Update
PumpMovement(session)                           // ← Mover：采样/Tick/插值 + 表现写入（本帧位姿落地）
PumpProjectiles(session, nowMs)                 // ← R5-C：投射物链（**必须**在 Mover 之后）
SendPendingProbes(session)
heartbeat
```

`PumpProjectiles` 内部：

1. 补绑定扫描（见 §2.2）；
2. **采样一次** `Input.GetKeyDown(KeyCode.F)` → `FireEdgeBuffered`（每 Update 一次，不在子步里重复采样）；
3. `BuildProjectileTargets`：采集目标样本（见 §2.5）——**必须在 `PumpMovement` 之后**；
4. `TryDiagnosticFire`（见 §3）；
5. `ProjectileAccumulatorMs += Time.deltaTime * 1000`（上限 8×16 = 128ms），
   按**固定 16ms** 取整出 `steps`（≤ 8）；
   - `steps == 0` → 只调一次 `Pump(wallNowMs, 0)`：**零步也 Pump(0)**（排裁决、清墓碑、重建视图，不因帧率停摆）；
   - 否则逐个 `Pump(wallNowMs, 16)`；
6. **每个子步**都做：`Pump` → fault 检查 → `SyncProjectileViews` → `CollectAndSubmitProjectileHits`。
   → 因此候选是**逐子步**收的（不是只收最后一个子步的轨迹），与契约「每子步收一次」一致。

### 2.4 视图 → 薄表现（有界）

- 用 **`CopyViews`（全量）**而非 `DrainViewChanges`：后者对"视图消失"给出的是 `Hidden+Stopped` 墓碑，
  而契约明说「**不能**依据 Hidden 判断 Destroyed」；全量集合的**差集**才是"该 key 真的不在了"的唯一可靠判据。
- 新增 → `PMUnityProjectilePresentation.Create(label)`；已存在 → `Apply`（复用同一对 `state/spec`，就地覆写）；
  **消失 → `Dispose` + 从字典移除 + 候选去重账 `Forget(key)`**。
- 字典上限 512；满时不再新建并计数（`ProjectileViewOverflows`）。`CopyViews` 返回条数 == 缓冲容量时
  **跳过移除判定**（否则会把"没拷进来"误当成"已消失"并销毁活的表现）。
- `Hidden / Stopped` **只走 `Apply`**（表现层无一次性副作用；重复 Apply 幂等）。
- `RadiusM` 非法（例如镜像快照尚未带来 spec）→ **跳过该视图表现并计数**，不拿默认值冒充尺寸
  （表现层是 fail loud，写坏值会让整局失败）。

### 2.5 目标样本（`BuildProjectileTargets`）

对**每个非本地 owner 的 rig**（AP 自己不是目标）取：

| 字段 | 来源 |
|---|---|
| `Epoch` | `offer.Epoch` |
| `NetId` | `rig.Player.NetId.Value` |
| `StreamVersion` | `rig.Driver.StreamVersion`（目标**当前**输入流；DS 要求 `candidate.TargetStreamVersion == stream.Version`） |
| `ServerFrame` | `SamplePresentation().ServerFrame`（**展示快照的权威帧** → 帧锚；`Input`/`None` 域直接跳过） |
| `OutputFrame` / `TotalSimTimeMs` | 同一次 `SamplePresentation()` |
| `Position` | `sample.Sync.Position`（本帧**已经算完**的呈现位姿） |
| `RadiusM` / `HalfHeightM` | `PMMoverDefaults.CapsuleRadiusMeters / CapsuleHalfHeightMeters × sample.Sync.Scale`（**含 Scale**） |
| `Alive` | `true`（新链本批**没有**生命值语义：客户端不做存活裁决，命中与否最终由 DS 权威判定） |

> 「不拿本地 input 帧冒充」是**双重**保证：采集侧只取 `AuthorityServer` 帧；收集器侧再校验一次
> （`TargetServerFrameInvalid`）。
> 采集条件（`HasValue` / 帧域 / 位置有限 / Scale 有限且 > 0）任一不满足即**跳过该目标**（fail closed）。

### 2.6 释放（`Stop` / 换局 / 失败）

`ReleaseSession` 现在**先** `ReleaseProjectiles` **再** `ReleaseMovements`，顺序为契约要求的
**Driver → Motion → presentation → 地图**（地图仍由 `ReleaseSession` 随后按模式释放）：

1. 对每个已复制副本 `driver.UnbindPlayer(player)`（幂等：取消该 owner 未消费预留、丢弃其入站队列、
   退休本地账、`ClearOwner`；客户端排空由此产生的释放项）；
2. `driver.Dispose()`（取消全部预留、销毁本会话权威对象、摘掉本 world 的事件订阅、摘掉 player 接缝引用）；
3. `motion.Dispose()`；
4. 字典里每个 `PMUnityProjectilePresentation.Dispose()` + 清空；
5. `collector.Clear()`；清各暂存；`PMR3Player.ProjectileWarn` 无条件还原（与 `PMR3Runtime.Warn` 同纪律）。

失败路径：任何 `driver.IsFaulted` 或处理异常一律走**既有** `Fail(...)`（置 fault + `FreezeMovements` + 报错），
此后 `PumpActive` 只推墙钟、**不再**推进投射物，绝不吞掉继续。

---

## 3. 诊断开火（输入面）

| 项 | 实现 |
|---|---|
| 触发 | `Input.GetKeyDown(KeyCode.F)`，**每 Update 采样一次**，单次非连发 |
| 前置 | 只对这些情况开火：`!Faulted && !MovementFrozen`、driver 存在且未 fault/disposed、本地 AP 副本存在且其 rig 可用 |
| 边沿纪律 | 与 Mover 输入边沿**同纪律**：先确认本帧真的能开火，**再**消费边沿（冻结帧/失去 AP 帧不会把按键静静吃掉） |
| 客户端门 | `wallNowMs - LastFireWallMs < 200ms` → 拦截并计数（`PMProjectileDiagnosticConfig.FireIntervalMs`，墙钟门不依赖帧率） |
| activation | `NextActivationId` 从 1 起单调 +1；**到达 `uint.MaxValue` 直接拒绝开火**（绝不回绕到 1）并计数告警 |
| 位置 | `predicted.Position + forward × 0.6m`（`MuzzleOffsetM`），`predicted = rig.Driver.GetPredictedSync()` |
| 朝向 | `forward = (sin(yaw), 0, cos(yaw))`，`yaw = predicted.YawDegrees` —— **Yaw 决定世界前向**，与表现层 `Quaternion.Euler(0, yaw, 0)` 同口径 |
| spec | 共享只读 `PMProjectileDiagnosticConfig.CreateSpec()` 的只读模板（驱动内部 `Clone()`，非第二套配置） |
| `predictionMs` | 固定 **100ms** 占位（客户端没有 RTT 测量口 → **不假装**做了实测折算；DS 的追赶预算由整合器自记账） |
| 发送 | `driver.TryFire(...)` → 生成的 `ServerProjectileSpawnV1`（**不新造 socket、不手写 MainPack**） |
| 失败 | 非致命拒绝（返回 false）→ 计数 + 记错误，游标与墙钟门**都不推进**（下一帧可重试）；`IsFaulted` → `Fail` |
| 明确不做 | 不消费旧 `CommandManger`/`BattleManger`、不复用旧 shell、不改旧 UI/普通攻击/HP |

## 4. 候选收集与上报（`CollectAndSubmitProjectileHits`，每子步）

```
for view in CopyViews 快照:
    if view.Key.Origin != ClientPredicted:  continue      // ServerDirect 由 DS 自判
    if view.Key.OwnerNetId != 本地 owner:  continue       // SP 视角不上报
    segment = { Key, PreviousPosition, Position, RadiusM, Stopped = view.Stopped || view.Hidden }
    collector.Collect(segment, 目标样本列表, 候选暂存)     // 纯几何 + 去重
    if 0 条候选: continue
    batch = { Key, PreviousPosition, HitPosition, RewindMs = 0, Targets = 候选数组 }
    sent  = driver.SubmitPredictedHits(owner, batch, wallNowMs, out rpcCount, out error)  // 真实声明通道
    sent  ? collector.Commit(key) & HitRpcs += rpcCount
          : collector.Rollback(key) & HitReportRejected++（失败**不**永久预占去重）
```

- **`LocalFake` / `TakenOver` 完全不参与判定**：镜像接管后 `LocalFake=false`，但"这颗弹是我自己预测的"
  没变，因此继续上报（否则接管瞬间自己的预测来源永久失去命中）。
- `segment.Stopped = view.Stopped || view.Hidden`：权威已停止、或本地已结束/隐藏 —— 两者都表示"这段是收官段"。
- `RewindMs = 0`：候选自带 `AuthorityServer` 帧锚 + 目标当前 stream，因此走 DS 的 **FrameAnchor** 一档；
  真正的 rewind（按实测 RTT 折算）留给后续批次 —— 本批不做，也**不假装**做了。
- ≤48 条/包的**分包**与每 key 5 次 Verify 总配额由 driver 负责（宿主只递交整批）；被拒（配额/身份/编码）
  是**非致命**结局：回滚在途候选、计数、按错误串去重后告警，等下一子步重试。
- 只上报候选，**不结算**：宿主本批**不订阅** `Settlement`、也不调 `DrainSettlements`；
  R6 接入前"无消费者"是合法状态，本报告**不声称**已扣血。

---

## 5. 测试

### 5.1 `PMProjectileCandidateTest`（新增，123 断言全通过）

编**真实**收集器 + 真实契约 + `PMNetIdentity` + `PMMoverState`（**零 Unity、零 PMR3**）；预期值全部独立手算：

| 段 | 断言 | 覆盖 |
|---|---|---|
| A（19） | 19 | 手算几何：**交叉**（距离 0，impact = 交点 `(0,1,0)`）、**擦边** 0.70 < 0.80 命中、**错过** 0.90 > 0.80 `GeometryMiss`、**退化点** 0.70 命中/0.81 未命中、**平行**（denom=0 分支：impact 手算 `(0.7,0.4,0)`）、阈值随弹半径变化（0.3 → 1.00） |
| B（7） | 7 | 目标尺寸**逐目标独立**：半径（0.4 vs 0.05 同一线段一个命中一个未命中）、半高（1.0 vs 2.5 在 y=3 一个未命中一个命中）、中心线退化为单点（`halfHeight == radius`） |
| C（11） | 11 | NaN/±Inf 线段与目标位置、弹半径非法、目标半径 NaN/0、`halfHeight < radius`、25m 超长线段、非法 key、**被拒调用不建账** |
| D（12） | 12 | owner 必须本地、origin 必须 `ClientPredicted`（`ServerDirect` 拒）、不能打自己、跨 epoch（弹/目标）、`NetId=0`、`StreamVersion=0`、**帧锚必须是 `AuthorityServer`**（`Input` 域与 `None` 均拒）、`Alive=false` |
| E（16） | 16 | 在途重复 → `AlreadyPending`；`Commit` 后才占位（`AlreadyCommitted`）；**`Rollback` 后可重收集**（未提交不占位）；`Commit`/`Rollback` 空账返回 false；提交 A 后仍可收集新目标 B；`CopyPending` 顺序与不改动在途 |
| F（7） | 7 | 停止段可收集；提交后同目标与新目标都 `StopSegmentAlreadyReported`；提交前可带多目标；回滚后可重来；非停止段不受收官限制（对照） |
| G（18） | 18 | 每弹恰好接受 100 目标、第 101 个 `TargetCapacityExceeded`、回滚后容量释放；key 数 1024 且第 1025 个 `KeyCapacityExceeded`、已存在 key 在满容量时仍可写；`Forget`/`Clear`/`Describe`/计数 |
| H（11） | 11 | 宿主真实调用面：`IList` 批量入口（近命中/远错过/自己排除 → 1 条）、空/null 输入安全、`StreamVersion`/`ServerFrame`/`NetId`/`VisualOffset=0` 字段透传 |
| I（10） | 10 | 跨 epoch 隔离（两个收集器各自独立）、构造参数校验（epoch/owner 为 0 抛异常）、只读回读 |

### 5.2 `PMClientCheck`（客户端真实编译门）

`Tools/PMClientCheck` 已（由主侧）把 `PMProjectile/**` 与 `PMR3/PMR5ProjectileDriver.cs` 纳入 include，
本批把 `PMClientSessionHost.cs` 的 R5-C 接线一起编进同一语言面（netstandard2.0 + C#7.3 + Unity 形状替身）：
**0 警告 / 0 错误**（`-o Tools/PMClientCheck/bin/r5-client-host`，避免与 DS 的 `PMR4UnityCheck` 输出互相覆盖）。

### 5.3 12 个 `PMProjectile/**` glob 消费者全部编译通过

`PMProjectileCodecCheck`、`PMProjectileCoreCheck`、`PMProjectileValidationCheck`、`PMR3UnitySmoke`、
`PMR4IntegrationTest`、`PMR4NetworkCheck`、`PMR4NetworkTest`、`PMR4UnityCheck`、`PMR5UnityAdapterTest`、
`PMR5UnityCheck`、`PMUnityGlueCheck`、`PMClientCheck` → 全部 **0 错误 / 0 警告**。
这是「纯收集器不得引入 Unity/R3 循环依赖」的**可执行证明**：其中两个工程编真实 Unity 2019.4 DLL。

### 5.4 stub 增补（**只为真实缺 API**）

`Tools/PMUnityGlueCheck/UnityStubs.cs` 的 `KeyCode` 枚举补 `F = 102`（真实 Unity 值；枚举原先只有
`Space/A/D/S/W/方向键`）。这是 stub 与真实 Unity 的缺口，**不是**为了迁就生产代码改生产逻辑：
`PMClientCheck/ClientStubs.cs` 的 `KeyCode` 本就含 `A..Z`（`F` 已在），故只补了 glue 那份。

---

## 6. 诚实边界（本批**未**完成/未验证）

| # | 项 | 说明 |
|---|---|---|
| U1 | **实机未验**（T45 / T5C3） | 未启动 Unity、未点菜单、未跑真实 PhysX/双客户端。本批只保证"真实 API 编译 + 纯核心可执行断言"。 |
| U2 | **诊断表现 ≠ 英雄弹外观** | 视图用 `PMUnityProjectilePresentation` 的**诊断占位球**（名字带 `diagnostic-sphere-not-hero-art`）。**不声称**英雄子弹外观已迁移，也不声称普通攻击/大招/资源校验已完成。 |
| U3 | **无伤害结算** | 宿主本批**不消费** `Settlement` / `DrainSettlements`；命中候选只上报，扣血属 R6。**不声称**已扣血。 |
| U4 | `predictionMs = 100` 为固定占位 | 客户端没有 RTT 测量口，未做实测折算（契约 U3/U4 同口径）。只影响 DS 发布权威初值时的追赶量。 |
| U5 | `RewindMs = 0`（走 FrameAnchor） | 逐帧回溯口径留给后续批次；本批未在真实弱网下量化"帧锚 vs 回溯"的命中率差异。 |
| U6 | 目标 `Alive = true` | 新链本批无生命值语义；客户端不做存活裁决，命中与否最终由 DS（filter/队伍/预算）判定。 |
| U7 | 队伍/友伤未过滤 | 客户端没有 roster 数据，不做队伍过滤（契约把该过滤放在 DS 的权威 side）。若临时同队互射，会由 DS 拒掉而不是本地静默丢弃。 |
| U8 | 正式地图的投射物碰撞未实机量过 | 白名单取 `PMUnityBattleMap.Colliders`（可达 4096），而 C1 的 `HitBufferCapacity = 32`：真机若出现 `SaturationFailures`（fail closed）需收紧 layerMask（与 `_r5_unity_adapter_report.md` 的 P3 同一条）。 |
| U9 | 诊断开火在**两种模式**都启用 | 契约只冻结了"共享诊断 spec + 200ms 门"，未区分模式；本批按"同一套探针"实现。若后续要求仅诊断模式可开火，只需在 `TryDiagnosticFire` 加一个 `session.ContentFormal` 判据。 |
| U10 | 视图上限 512 | 与驱动的 view 上界（`MaxProjectiles`/`MaxOwners` 量级的活对象）之间是**有意的**有界取舍：溢出只计数（不新建表现），不会误删已有表现。 |
| U11 | DS 侧未接线 | 本批**不含** `PMDsSessionHost` 的真实历史采样/可信策略接线（并行组负责）；本批客户端不读 DS 侧任何实现细节。 |
| U12 | `PMR3TestScene`/`PMUnityBattleMap` 未改动 | 只读复用其已有 API。 |

---

## 7. 已检查范围与写入边界

**完整读取的文档**：`AGENTS.md`、`Client/Assets/AGENTS.md`、`Docs/plans/net-r5-network-contract.md`（全文）、
`Docs/plans/_r5_network_review.md`（全文）、`Docs/plans/_r5_unity_adapter_report.md`（全文）、
`Client/Assets/Scripts/PMProjectile/PMProjectileDiagnosticConfig.cs`、
`C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`；
另按需读 `Docs/plans/net-architecture-migration.md` 的 R5-C 段。

**读取的实现（关键段）**：`PMClientSessionHost.cs`（全文）、`PMR5ProjectileDriver.cs`
（公开面 + ctor/BindPlayer/UnbindPlayer/CleanupOwner/Pump/TryFire/SubmitPredictedHits/CopyViews/
DrainViewChanges/RebuildViews/Dispose/Fault + 客户端分支全文）、`PMProjectileContracts.cs`（全文）、
`PMProjectileValidator.cs`（全文）、`PMProjectileHistory.cs`（全文）、`PMProjectileCoordinator.cs`（公开面）、
`PMUnityProjectileMotion.cs` / `PMUnityProjectilePresentation.cs`（公开面）、
`PMR4MovementDriver.cs`（公开面）、`PMMover/PMMoverState.cs`（相关段）、`PMNet/PMNetIdentity.cs`（`PMFrameId`）、
`PMDsSessionHost.cs`（`PMR3TestScene` 段，只读）、`PMUnityBattleMap.cs`（公开面）、`PMR3/PMR3Player.cs`（公开面）。

**本次写入的文件（仅任务边界内的 8 个）**：见 §0 表格。
**未做**：未改 `PMDsSessionHost` / driver / coordinator / 契约 / 生成物 / 锁文件 / 任何 csproj 的 include
（`PMClientCheck` 与 `PMUnityGlueCheck` 的 `PMProjectile/**` + driver include 由主侧先前加入，本批只新增了一个文件）；
未启动 Unity；未执行 SVN/git 写操作；未提交；未递归委派。
**构建产物**：仅 `Tools/PMClientCheck/{bin,obj}`（含 `bin/r5-client-host`）与
`Tools/PMProjectileCandidateTest/{bin,obj}`；另外为回归顺带重建了 12 个既有 glob 工程自身的 `bin/obj`。
