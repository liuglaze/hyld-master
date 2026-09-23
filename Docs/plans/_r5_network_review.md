# R5-B2c 网络驱动（PMR5ProjectileDriver）返工复核 — 发现、修改与证据

> 范围：`Docs/plans/net-r5-network-contract.md`「B2b网络driver」与「B2b/C适配可并行边界」、
> `Docs/plans/net-r5-projectile-contract.md`（A3/B1 冻结语义）。
> 本文只记录**本批次实际产物与实测证据**；状态唯一源仍是 `Docs/plans/net-architecture-migration.md`。
> 本批次**不含**：C 的真实 Unity 宿主接线（`PMClientSessionHost` / `PMDsSessionHost` 仍未创建本驱动）、
> R6 伤害结算、T45 实机。**不启动 Unity、不提交、不递归委派。**

---

## 0. 结论摘要

主侧给出的 6 条重点缺陷假设，逐条实证结果：

| # | 假设 | 实证结论 | 处理 |
|---|---|---|---|
| 1 | `TryFire` 只调 `Lifecycle.TryRegisterPredicted`，`AdvanceLocalFakes` 调 `Coordinator.AdvanceMotion` 会因缺 `KeyRecord` 报 `UnknownKey`，假弹下一帧消失 | **不成立**（实测：真实阻断上行 112ms 后假弹仍在且真的推进 1.12m）。原因：`AdvanceMotion → StepMotion` 只经**生命周期账本**解析（`IsRegistered`/`TryGetFrozen`），不需要 Coordinator 自有 `KeyRecord` | 不引入 `RegisterLocalPrediction`、不伪造 NetId；把该事实固化成 M1 回归测试（断言真实位移，不把「view 创建」当预测成功） |
| 2 | `HandleClientDecision` 收到 Confirm 立刻 `TakeOverFake`，镜像未到就提前接管；接管用了过时 snapshot | **成立**（实测：镜像未到时 `FakesTakenOver` 已 0→1；镜像到达后视图位置**回退** `rewind=True`） | 接管只在镜像对象**真实存在**时发生一次；接管沿用假弹自身运动时基并建**高水位**挡住旧 snapshot；已接管的假弹不再本地推进。新增 `MirrorStaleDropped` 计数 |
| 3 | DS 代码没有 `CatchUpMotion` 调用，初始 snapshot 未追赶 | **成立**（实测：`prediction=100ms` 时 `MoveTimeMs=48`（纯正常运动），线上初值停在枪口；挂起 64ms 后确认 `MoveTimeMs=0`） | 新增 `Coordinator.CatchUpRegisteredMotion`（预算 = `prediction/2 + 挂起时长`，两项均取整合器自记账），`DrainSpawnReady` 在**编码初值之前**调用，并用追赶后的冻结状态编码 |
| 4 | `_fakes/_mirrors/_uplinkVerifyUsed/NextId/脏views` 需有界且退休；Destroy 是否恢复 fake | **成立**（实测：DS 销毁权威对象后 owner 侧 `fakes=1 views=1` —— 幽灵弹留到对局结束） | 客户端每帧有界收拾（`PurgeExpired` + 排空本地释放项 + 退休扫描）；镜像销毁时退休本地 key；新增 `FakesRetired`；`NextId` 显式按 owner 上限拒绝；视图脏集合沿用既有上限 |
| 5 | 结算无订阅直接丢并计数，不等于唯一出口 | **成立**（实测：`noConsumer=1 handed=0`，且整合器队列已被取空 ⇒ 无任何可取回的出口 = 静默丢真实伤害） | 新增**有界待取缓冲** `MaxPendingSettlements=512` + 公开 `DrainSettlements`；缓冲满则显式 fault（新枚举 `SettlementUnconsumed`）；订阅者抛异常也不再吞 |
| 6 | 移除 owner 的未消费预留/队列要取消；Sender 判定须 `(ClassId, RpcId)` | **成立**（实测：`UnbindPlayer` 后 `reserved=1 cancelled=0 stillPending=True`；非 `PMR3Player` 目标带投射物 RpcId 会**抛异常**而不是告警） | `UnbindPlayer` 走完整 owner 收拾（取消预留 / 丢入站队列 / 退休本地账 / `ClearOwner` / 销毁该 owner 的悬空权威对象）；`PMR3Runtime` 改按 `(ClassId, RpcId)` 判定 |

世界预留 API（`PMNetWorld.TryReserveNetId/CancelReservedNetId/SpawnReserved`）与 M03 既有真实通道（movement/probe RPC）**零改动**。

---

## 1. 实证方法与基线（先证明、再修）

为避免「改完才自证」，先用**临时探针段**（与最终测试同一套真实链路：真实生成桩 + 真实 `PMTransport` 字节链 + 手工 link/hub）
在**修改前**跑一遍，记录原始数值；探针随后被替换成带断言的 M1–M6 测试（判据不再依赖日志）。

修改前的关键实测（节选）：

```
P1 dsRecv=0 spawned=0 spView=0 viewExists=True viewZ=1.1200001
   describe=... fakes=1 mirrors=0 authority=0 views=1            // 假设 1 证伪：预测真的在跑
P2 dsRecv=1 dsSpawned=1 held=6 zNow=0.9600001
P2 takenBefore=0 after=1 localFake=False hidden=False mirrorNetId=0   // 假设 2a：提前接管
P2 afterRelease maxZ=2.8800004 rewind=True mirrorApplied=20           // 假设 2b：旧 snapshot 使位置回退
P3 dsMoveTime=48 dsZ=0.48 spZ=0.16                                    // 假设 3：无追赶（48ms 全是正常运动）
P3b registeredWall=1080 resolveWall=1144 expectedMoveTime=114 actualMoveTime=0 actualZ=0
P4 after dsDestroyed=1 ownerViewCount=1 owner=... fakes=1 mirrors=0 views=1   // 假设 4：Destroy 后假弹仍在
P5 noConsumer=1 handed=0 coordQueue=0                                 // 假设 5：取走后无处可取（静默丢失）
P6 reservedBefore=1 reservedAfter=1 cancelled=0 stillPending=True     // 假设 6a：owner 移除不回收集
P6b 非 PMR3Player 目标 + 投射物 RpcId: threw=True (InvalidOperationException) // 假设 6b：误伤无关类
```

修改后同一探针的对应数值（随后固化为断言）：

```
P1 viewZ=1.1200001（不变 ⇒ 证实假设 1 不成立）
P2 after=0（不提前接管）; rewind=False; takenOver=1
P3 dsMoveTime=98（= 追赶 50 + 正常 48）
P3b expectedMoveTime=114 actualMoveTime=114 actualZ=1.14（位置与总时长都吻合）
P4 after: fakes=0 mirrors=0 views=0（无幽灵、无复活）
P6 reservedAfter=0 cancelled=1 stillPending=False
P6b threw=False (warn)（走告警路径）
```

---

## 2. 逐条修改（精确到落点）

### 2.1 【假设 3】DS 权威注册后的首次有界追赶

- `Client/Assets/Scripts/PMProjectile/PMProjectileCoordinator.cs`
  - 新增公开入口
    `public PMProjectileAdvanceOutcome CatchUpRegisteredMotion(PMProjectileKey key, double wallNowMs)`：
    预算 `= predictionMs / PredictionCatchUpDivisor + 挂起时长`。
    - `predictionMs` 取 `KeyRecord.PredictionMs`（`RequestSpawn` / `ServerDirectSpawn` 时写入）；
    - 挂起时长取 `_lifecycle.TryGetRegistration(key).RegisteredWallTimeMs` 与 `wallNowMs` 之差；
    - **两项都由整合器自记账**，调用方不自报预算（自报预算就是可伪造的输入）；
    - 有界性完全复用 `CatchUpMotion`（预算钳制 `MaxCatchUpBudgetMs` + 单步 ≤50ms + `MaxCatchUpSteps` + `ClampedCatchUps` 计数）；
    - 未登记 ⇒ `NotRegistered`、未升级的预测身份 ⇒ `NotAuthority`，**不回退直线**。
  - `PMProjectileCoordinatorLimits` 新增 `PredictionCatchUpDivisor = 2`、`MaxPendingCatchUpMs = 8000`（防御墙钟异常）。
- `Client/Assets/Scripts/PMR3/PMR5ProjectileDriver.cs` → `DrainSpawnReady`
  - 取到预留令牌后**先** `CatchUpRegisteredMotion`，**再** `TryObserveFrozen` 重新读冻结状态并据此编码 snapshot
    （原来用的是 `PMProjectileSpawnEvent.State`，那是「发事件那一刻」的旧值）。
  - 追赶失败/取不到冻结状态 ⇒ `CancelReservedNetId` + 会话 fault（宁可失败也不发布假初值）。
  - 新增计数 `CatchUpsApplied`，`AuthorityEntry.CatchUpMs` 记录本弹实际追赶量。
- `Client/Assets/Scripts/PMProjectile/PMProjectileLifecycle.cs`：**无需改动**（`TryBeginCatchUp` 已保证「注册先于追赶、每 key 只开始一次」）。

### 2.2 【假设 2】接管时机与旧镜像高水位

- `PMR5ProjectileDriver.HandleClientDecision`：`Confirmed` 只置 `fake.Confirmed`；
  仅当 `MirrorObjectExists(key)`（镜像**对象**在手）才 `TakeOverFake`。新增 `MirrorObjectExists`。
- `PMR5ProjectileDriver.ApplyMirrorPayload`：
  - 每次到达都更新 `MirrorEntry.Obj/Spec/AuthorityNetId`（身份信息），但运动写入受**高水位**约束：
    `state.MoveTimeMs < entry.HighWaterMoveTimeMs` 且**不是**停止/隐藏 ⇒ 整条丢弃（`MirrorStaleDropped++`，不写账本）；
    高水位起点 = 接管时假弹自身的运动时基（`FakeEntry.LastMoveTimeMs`，由 `AdvanceMotion` 返回值直接记下，走复制回调路径不产生无谓分配）。
  - 停止/隐藏属**状态转移**，不受高水位限制（否则真实停止会被旧时基永远挡住）。
  - 仅在 `wroteLedger`（`Applied`/`StoppedNotMoved`/`UnknownKey`/`HiddenNoRevive`）时才更新 `entry.State` 与显示状态。
  - 末尾：假弹仍存活 ⇒ 一次性 `TakeOverFake`（`TryTakeoverPredicted` 本身幂等）。
- `PMR5ProjectileDriver.AdvanceLocalFakes`：已接管的弹**不再本地推进**（避免「本地直线把权威停止推回去」的双驱动）。
- `PMR5ProjectileDriver.RebuildViews`：假弹视图的权威 NetId 改读 `MirrorEntry.AuthorityNetId`（身份随最新快照，位置仍由本地账本闸门控制）。

### 2.3 【假设 4】有界与退休清理（含「Destroy 不复活假弹」）

- 新增 `SweepLocalState(wallNowMs)`（客户端每帧，**有界**）：`PurgeExpired` → 排空本地释放项（`ClientReleasesDrained`）→ `SweepRetiredLocalKeys`。
  过去客户端**从不**调 `PurgeExpired`，这正是「飞完的 key 留到对局结束」的根因。
- 新增 `SweepRetiredLocalKeys`：生命周期已不再登记 **且** 没有活着的镜像对象 ⇒ `_fakes`/`_uplinkVerifyUsed` 退休（`FakesRetired`）；
  镜像登记按 `_world.TryFind` 存活判定回收；被权威驱动过（`Applied`）的镜像消失时同步 `RetireLocalKey`。
- 新增 `RetireLocalKey(key)`：移除假弹登记 + 忘掉上行 Verify 账 + 生命周期 `Retire`（终态置位后迟到载荷不复活）。
- `RemoveMirrorByObject` 改为「找到镜像条目 → 若曾被权威驱动则 `RetireLocalKey`」。
- `_nextProjectileId`：新增 owner 流容量上限判定（`PMProjectileLimits.MaxOwners`），超出**明确拒绝**并给出 error；
  游标**刻意不重置**（重置等于允许 ID 复用，会被 DS 的高水位整批拒掉）。
- 视图脏集合沿用既有 `MaxDirtyViews` 上限（溢出即整体作废并计数），未改语义。

### 2.4 【假设 5】结算唯一出口不再静默丢伤害

- 新增 `List<PMProjectileSettlement> _pendingSettlements` + `MaxPendingSettlements = 512`。
- `DrainCoordinatorOutlets`：有订阅者 ⇒ 走事件（订阅者抛异常**不算交付**，转入缓冲）；无订阅者/未交付 ⇒ 进缓冲（`SettlementsQueued`、`SettlementsWithNoConsumer`）；
  缓冲已满 ⇒ `Fault(SettlementUnconsumed)`（新增枚举值 `11`）并停止推进。
- 新增公开 `int DrainSettlements(int max, out PMProjectileSettlement[] items)`、`int PendingSettlementCount { get; }`。
- **R6 未接入前**：宿主必须每帧 `DrainSettlements`（或订阅事件）；本条只保证「不静默丢」，**不声称已扣血**。

### 2.5 【假设 6】owner 收拾与 RpcId 判定

- `PMR5ProjectileDriver.UnbindPlayer` → 新增 `CleanupOwner(player)`：
  1. 取消该 owner **全部未消费 NetId 预留**（`CancelReservedNetId` + `ReservationsCancelled`）；
  2. 丢弃入站队列中属于该 player 的 spawn/hit/decision 载荷；
  3. 退休该 owner 的 `_fakes`/`_mirrors`/`_uplinkVerifyUsed` 与未绑定镜像暂存；
  4. `_coordinator.ClearOwner`（清暂存与登记，**保留 ID 高水位** ⇒ 旧 ID 不复活）；客户端排空由此产生的释放项；
  5. DS 侧：销毁该 owner 的权威对象并计数（否则 `ClearOwner` 退役登记后它们会变成「既不复制也不销毁」的悬空对象）。
  - 新增计数 `OwnerCleanups`。`Dispose` 仍走整会话清理（幂等）。
- `Client/Assets/Scripts/PMR3/PMR3Runtime.cs`：`IsProjectileRpcId(ushort)` → `IsProjectileRpc(uint classId, ushort rpcId)`，
  先判 `classId == PMR3Player.PMGeneratedClassId` 再判三条稳定 RpcId；
  movement/probe 的**告警原行为逐字不变**，投射物三条仍走「抛异常 → 驱动转会话 fault」。

---

## 3. 修改文件（严格在授权边界内）

| 文件 | 变更性质 |
|---|---|
| `Client/Assets/Scripts/PMR3/PMR5ProjectileDriver.cs` | 修改：接管时机 / 高水位 / 追赶调用 / 退休扫描 / 结算缓冲 / owner 收拾 / 新观测面 |
| `Client/Assets/Scripts/PMR3/PMR3Runtime.cs` | 修改：投射物 RPC 判定改为 `(ClassId, RpcId)` |
| `Client/Assets/Scripts/PMProjectile/PMProjectileCoordinator.cs` | 修改：新增 `CatchUpRegisteredMotion` + 2 个限额常量 |
| `Tools/PMR5NetworkTest/Program.cs` | 修改：新增 M1–M6 真行为测试（+ 夹具 `ClearHold`/`AttachDsMotion`/`StopAfterMotion`/无消费者 harness）；B/F/L 段判据按修正后的语义更新 |
| `Tools/PMProjectileIntegrationTest/Program.cs` | 修改：新增 T 段 25 项 `CatchUpRegisteredMotion` 真实核心测试 |
| `Docs/plans/_r5_network_review.md` | 本报告（唯一新增文档） |

**未改动**：`PMProjectileLifecycle.cs`、`PMProjectileContracts.cs`、`PMNet/**`、`PMR5Projectile.cs`、生成产物、锁文件、
全部 `.csproj`、主计划与契约文档；未改世界预留 API；未改 M03 既有通道。

---

## 4. 公开 host API（供 C/DS 宿主消费；相对上一批的增量）

```csharp
// PMNet.R3.PMR5ProjectileDriver —— 新增
public const int MaxPendingSettlements = 512;         // 无消费者结算的有界待取缓冲
public int FakeCount { get; }                          // 本地假弹登记数（退休后会回落）
public int MirrorCount { get; }                        // 镜像登记数
public int AuthorityObjectCount { get; }               // DS 存活权威对象数
public int UplinkVerifyKeyCount { get; }               // 上行 Verify 账里的 key 数
public int PendingSettlementCount { get; }
public int DrainSettlements(int max, out PMProjectileSettlement[] items);   // R6 接入前的合规出口

// 新计数（观测）：FakesRetired / MirrorStaleDropped / CatchUpsApplied / OwnerCleanups
//                 ClientReleasesDrained / SettlementsQueued
// 新故障原因： PMR5ProjectileFaultReason.SettlementUnconsumed = 11

// 语义变更（签名不变）：
//   UnbindPlayer(player) —— 现在会取消该 owner 的未消费预留、丢弃其入站队列、退休本地账，
//                           并（DS）销毁其悬空权威对象；返回 false 仅当该 player 本就未绑定。
//   Pump(wallNowMs, stepMs) —— 客户端分支现在也做有界 TTL/墓碑清理与退休扫描（单帧工作量仍 ≤ MaxApplyPerPump）。

// PMNet.Projectile.PMProjectileCoordinator —— 新增
public PMProjectileAdvanceOutcome CatchUpRegisteredMotion(PMProjectileKey key, double wallNowMs);
public const int PredictionCatchUpDivisor = 2;   // PMProjectileCoordinatorLimits
public const int MaxPendingCatchUpMs = 8000;     // PMProjectileCoordinatorLimits
```

`PMR3Runtime` 无新增公开 API（内部判定修正）。

---

## 5. 测试与命令（全部本批 build + run，退出码 0）

| 命令 | 结果 |
|---|---|
| `dotnet build Tools/PMR5NetworkCheck -c Release` | 0 警告 / **0 错误**（netstandard2.0 + C#7.3，零 UnityEngine） |
| `dotnet build Tools/PMR5NetworkTest -c Release` | **0 错误** |
| `dotnet Tools/PMR5NetworkTest/bin/Release/net8.0/PMR5NetworkTest.dll` | **通过 360 / 失败 0**（上一批 226；本批 +134） |
| `dotnet build Tools/PMProjectileCoreCheck -c Release` | **0 错误** |
| `dotnet build Tools/PMProjectileIntegrationCheck -c Release` | **0 错误** |
| `dotnet build Tools/PMProjectileValidationCheck -c Release` | **0 错误** |
| `dotnet build Tools/PMProjectileCodecCheck -c Release` | **0 错误** |
| `dotnet build Tools/PMR5UnityCheck -c Release` | **0 错误** |
| `dotnet .../PMProjectileIntegrationTest.dll` | **977 / 0**（上一批 952；本批 +25 = 新 T 段） |
| `dotnet .../PMProjectileLifecycleTest.dll` | **510 / 0**（回归，无改动） |
| `dotnet .../PMProjectileValidationTest.dll` | **264 / 0** |
| `dotnet .../PMProjectileCodecTest.dll` | **453 / 0** |
| `dotnet .../PMR3RuntimeTest.dll` | **180 / 0**（`PMR3Runtime` 改动回归） |
| `dotnet .../PMR3IntegrationTest.dll` | **143 / 0** |
| `dotnet .../PMR5DeclarationTest.dll` | **139 / 0** |

### 5.1 PMR5NetworkTest 新增 M 段（真行为断言，走真实生成桩 + 真实 Transport）

| 子段 | 覆盖与关键断言 |
|---|---|
| M1 | 真实阻断上行 112ms：`ServerSpawnsReceived==0`（真断网）且假弹仍存活、`LocalFake`、`Z` 前进 1.0–1.3m（断言**真实位移**，不把 view 创建当成功） |
| M2 | 扣住下行 ⇒ DS 已建对象但客户端无镜像；声明接缝先投 `Confirmed` ⇒ **不提前接管**（`FakesTakenOver` 不变、`LocalFake`、未隐藏、位置继续前进）；放开后**恰好接管一次**、`rewind=False`、`MirrorStaleDropped≥1`、权威 NetId 一致、展示**收敛到权威位置**（≤0.5m） |
| M3 | M3a 立即确认 `prediction=100` ⇒ `MoveTimeMs≥50` 且 **observer 首次观测**的线上初值 `Z≥0.5`；M3b `Pending` ⇒ `MoveTimeMs == 50 + 挂起时长`（精确到 0.001ms）且位置与时长自洽；M3c `prediction=0` ⇒ 不得凭空多推 |
| M4 | 宿主 hook 使弹 32ms 撞停 ⇒ 墓碑到期 DS 销毁：owner/observer 的 `ViewCount==0`、`FakeCount==0`、`MirrorCount==0`、`UplinkVerifyKeyCount==0`；迟到 `Confirmed` 不复活 |
| M5 | 无订阅者：`SettlementsWithNoConsumer≥1`、`HandedToR6==0`、缓冲可取、Drain 后不重复、未满不 fault |
| M6 | owner 移除：预留取消（`ReservationCount==0`、`ReservationsCancelled≥1`）、挂起清理、存活权威对象被销毁；客户端侧假弹/视图/Verify 账清理；非 `PMR3Player` 目标 + 投射物 RpcId **不抛**且走告警；**正对照**：投射物所属类同一 RpcId 仍抛 |

测试纪律：除 M2「决策先于镜像」（线上该顺序只能由重连/换流产生，沿用上一批 F 段的**声明接缝**构造手法并在注释中标注）外，
其余全部经真实生成 RPC 桩与真实字节链；不断言日志、不用替身网络层。

---

## 6. 未验证 / 诚实边界

| # | 项 | 说明 |
|---|---|---|
| U1 | C 的真实 Unity 宿主未接线 | `PMClientSessionHost` / `PMDsSessionHost` 仍未创建本驱动；`CopyViews`/`DrainViewChanges`/`DrainSettlements` 尚无真实消费者。**不声称 T45 或 C 已完成。** |
| U2 | 无 R6 消费者 | `Settlement` 只是出口；本批**没有任何**「已扣血」断言。 |
| U3 | 接管后「等权威时基追上」的展示冻结窗口 | 高水位保证不回退的代价：`predictionMs` 偏小时展示会停在假弹最后位置直到权威时基反超。已由 M2 断言「最终收敛」，但**未在真实链路上量化**最优 `predictionMs`（C 宿主应传约一个 RTT；驱动不自行测 RTT）。 |
| U4 | `prediction/2` 是主侧指定口径 | 该折算不是驱动内测量结果；若后续采用实测 RTT 口径，只需改 `CatchUpRegisteredMotion` 一处。 |
| U5 | 高水位用的是 `MoveTimeMs`（对象内运动时基） | 未使用 `TotalSimTimeMs` 帧锚（驱动路径的 snapshot 不含帧锚）；契约中「同对象同 stream 帧锚」的新鲜度判定仍由 A2/B1 层负责。 |
| U6 | **陈旧但带 Stopped 的镜像** | 停止是终态转移，不受高水位限制 ⇒ 采用权威停止位置（`HideOnStop=true` 时表现已隐藏，无可见回退）。属有意取舍。 |
| U7 | 结算缓冲溢出 fault 路径 | 只有「缓冲可取」被断言；511→512 的溢出 fault 未构造用例（需要 512 条真实结算）。 |
| U8 | `MirrorPayloadQueueDrops` 后的恢复 | 队列满丢最旧是既有语义；本批新退休扫描覆盖「事件丢失导致条目滞留」，但**未**构造队列溢出场景。 |
| U9 | `NextId` 上限仅按 owner 数 | 到达 `MaxOwners` 时**拒绝新的 owner 预测量**（不是淘汰旧游标）；理由与影响写在代码注释。 |
| U10 | 多 owner / 大并发 | 各段均为 1 owner + 1 observer；`MaxOwners`/`MaxProjectiles` 量级的并发未压测。 |
| U11 | 世界预留 API / M03 通道 | 本批未改，也无新增验证（沿用 `PMNetWorldTest` / `PMR4*` 既有证据）。 |

---

## 7. 已检查范围

**完整读取的文档（按委派顺序）**：`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`（全文）
→ `Docs/plans/net-r5-network-contract.md`（全文）→ `Docs/plans/net-r5-projectile-contract.md`（全文）
→ `Docs/plans/_r5_network_report.md`（全文）→ `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`（全文）；
另按需读 `Docs/plans/net-architecture-migration.md`（R5-A/B1 交付段、R5-B2 表）与 `Docs/plans/_r5_coordinator_review.md` 相关段。

**读取的实现（必要段/全文）**：`PMR5ProjectileDriver.cs`（全文）、`PMR3Runtime.cs`（全文）、`PMR3Player.cs`（公开面）、
`PMR5Projectile.cs`（全文）、`PMProjectileCoordinator.cs`（公开面 + RequestSpawn/ServerDirectSpawn/ResolveActivation/
ReportHits/AdvanceMotion/CatchUpMotion/PurgeExpired/ClearOwner/StepMotion/CheckOnly/RecordStop/EnsureRecord/
PruneRecords/Drain* + 限额）、`PMProjectileLifecycle.cs`（TryRegisterPredicted/TryRegisterAuthority/TryPromote/
TryTakeoverPredicted/NotifyPredictedEnded/TryApplyMirror/TryAdvanceMotion/TryRecordStop/Retire/PurgeExpired/
TryBeginCatchUp/IsRegistered/TryGetFrozen/CreateEntry）、`PMProjectileContracts.cs`（全文）、
`PMProjectileCodec.cs`（常量与公开面）、`Tools/PMR5NetworkTest/Program.cs`（全文，作为夹具手法参照与被改对象）、
`Tools/PMProjectileIntegrationTest/Program.cs`（夹具与 N 段参照）、`PMNet/PMNetObject.cs`、`PMNetGeneratedRegistry.g.cs`、
`PMNetSessionBridge.cs`（发送面）。

**本次写入的文件**：§3 表中 6 个（3 源 + 2 测试 + 本报告）。

**未做**：未启动 Unity；未执行 SVN/git 写操作；未提交；未递归委派；未改主计划/契约/生成物/锁文件/csproj；
未改 `PMProjectile/**` 的 Language/Lifecycle/Validator/History（仅 Coordinator 增加一个入口）。
