# R5-C 客户端宿主接线 + 候选收集器 —— 独立复核 / 修复报告

> 任务类型：**comprehensive**（独立复核 R5-C 客户端宿主与候选收集器的真实接线 + 在允许边界内修复 + 门禁验证）
>
> 只读输入（按委派顺序）：`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`（全文）
> → `Docs/plans/net-r5-network-contract.md`（尾段「C宿主首个可执行入口」为准）
> → `Docs/plans/_r5_client_host_report.md`（全文） → `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`。
>
> 直接读过的实现面：`PMClientSessionHost.cs`（全文）、`PMR5ProjectileDriver.cs`（`Pump` / `RebuildViews` /
> `MarkViewDirty` / `DrainViewChanges` / `CopyViews` / `ViewCount` / `MaxDirtyViews` / `TryFire` /
> `SubmitPredictedHits` / `UplinkVerifyUsed` / `AdvanceLocalFakes` / `SweepLocalState` / 镜像与接管路径 / `Dispose`）、
> `PMProjectileContracts.cs`、`PMProjectileLifecycle.cs`（登记/水位/上限/墙钟/`TryRegisterPredicted`）、
> `PMProjectileCoordinator.cs`（`CheckClock`/`AcceptWallClock`/`AdvanceMotion`/`PurgeExpired`）、
> `PMProjectileCandidateCollector.cs`（全文）、`PMInterpolationBuffer.cs`（`PMInterpolatedState.ServerFrame` 语义）、
> `PMR4MovementDriver.cs`（`SamplePresentation`/`StreamVersion`/`GetPredictedSync`）、`PMProjectileCodec.cs`（快照 spec 可缺省）、
> `PMDsSessionHost.cs`（只读，用于确认它不消费视图增量）。
>
> **未修改** driver / coordinator / codec / DS 宿主 / 契约 / 生成物 / 锁文件 / 任何 csproj；
> **未**启动 Unity、**未**执行 git/svn 写操作、**未**提交、**未**递归委派。
> 单位：**Y-up 米、Yaw 度**；所有空间结论都以「驱动给的视图」为唯一真值。

---

## 0. 本次写入与验证（真实命令、真实结果）

| 文件 | 性质 |
|---|---|
| `Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs` | **修改**：脏通道排空 + 容量对齐/显式失败 + 视图追踪集 + Verify 配额空转闸 + 诊断行 |
| `Tools/PMProjectileCandidateTest/Program.cs` | **修改**：新增 J 段 14 条断言（123 → **137**） |
| `Docs/plans/_r5_client_host_review.md` | 本报告（唯一文档写入） |

| 命令 | 结果 |
|---|---|
| `dotnet build Tools/PMProjectileCandidateTest/PMProjectileCandidateTest.csproj -c Release` | **0 警告 / 0 错误** |
| `dotnet Tools/PMProjectileCandidateTest/bin/Release/net8.0/PMProjectileCandidateTest.dll` | **通过 137 / 失败 0**，退出码 **0** |
| `dotnet build Tools/PMClientCheck -c Release -o Tools/PMClientCheck/bin/r5-client-review` | **0 警告 / 0 错误** |
| `dotnet build Tools/PMUnityGlueCheck/PMUnityGlueCheck.csproj -c Release`（附加证据，非委派要求） | **0 警告 / 0 错误** |
| 编码自检 | `PMClientSessionHost.cs` / `Program.cs` = UTF-8 **BOM + CRLF**（无裸 LF），与改前一致 |

**过程中的一个真实阻塞（已闭合，非本边界问题）**：第一次跑 `PMClientCheck` 时失败于
`Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs(617,42): error CS0246: DiagnosticProjectileUplinkGate`
—— 该文件当时是**并行组的半成品**（1151 行未提交改动），`DiagnosticProjectileUplinkGate` 全树不存在；
它被 `PMClientCheck` / `PMUnityGlueCheck` / `PMR4UnityCheck` 三个门共享（`Server/**` 与显式 include），
而 `PMClientHost` 又**合法依赖**同一文件里的 `PMR3TestScene`，所以不能靠「排除该文件」验证。
处理方式：① 不在边界内改它（只读 + 记录阻塞）；② 在**仓库外**临时工程
`%TEMP%\pm_r5_client_harness`（把 `PMClientCheck` 的编译面原样搬过去，仅把那份半成品换成
`git HEAD` 版本）证明本文件 **0 警告 / 0 错误**；③ 并行组随后补齐类型
（`PMDsSessionHost.cs:2792`），同一条委派命令重跑 → **0 警告 / 0 错误**。三条命令全部复核通过。

---

## 1. 真实问题与修复（逐条）

### 1.1 视图增量通道从不排空 —— 已退休 key 常驻 + 增量粒度被反复整体作废

**机制先纠正主侧描述**：`MarkViewDirty` 在 `_dirtyViewOrder.Count >= MaxDirtyViews(4096)` 时**不是 fault**，
而是 `_dirtyViewSet.Clear(); _dirtyViewOrder.Clear(); ViewDirtyOverflows++` 后重新开始
（`PMR5ProjectileDriver.cs:2692-2704`）。所以「4096 后 fault」不准确；**真实的两个后果**是：

1. 脏集合会**常驻最多 4096 个已退休 key**（`MarkViewDirty` 只在 Set 命中时短路，退休 key 不会被移除，
   因为只有 `DrainViewChanges` 会 `Remove`），对这局来说是持续的内存/哈希成本；
2. `ViewDirtyOverflows` **无界增长**，且增量通道被反复整体作废 —— 只要将来出现任何增量消费者
   （或 DS/别的宿主复用同一驱动），它拿到的增量都会被周期性清零吞掉。

宿主侧的现实是：本宿主真值一直用**全量 `CopyViews`**（正确，见 1.3），于是那条增量通道**没有任何消费者**。
**修复**（`PMClientSessionHost.cs:1349` 调用点 + `:1363` `DrainProjectileViewChanges`）：每帧子步结束后调用一次
`driver.DrainViewChanges(PMR5ProjectileDriver.MaxDirtyViews, out drained)` 并**丢弃结果**。
- 为什么 max 取 `MaxDirtyViews`：`DrainViewChanges` 内部按 `take = min(order.Count, max)` 分配
  （`new PMR5ProjectileView[take]`），所以**分配量 = 实际脏条数**（稳态每帧几条），单次调用即可清掉任何积压；
- 真值不变：排空结果不参与任何判定，表现/移除仍只由 `CopyViews` 差集决定（注释已写进方法文档头）。

**附带观察（边界外，未改）**：`PMDsSessionHost` 同样不消费视图增量；并行组的工作区半成品里已经有
`DrainDsProjectileViews`（`PMDsSessionHost.cs:600` 附近的注释）——两侧独立发现了同一问题。

### 1.2 容量 512 会静默丢视图/候选 —— 已与驱动容量对齐，超容量显式整局失败

**真实缺陷**：`CopyViews` 在缓冲填满时只拷前 N 条，超出部分是「既不建表现、也不参与候选收集」的**静默丢弃**；
表现字典满时也只 `ProjectileViewOverflows++` 后 `continue`（幽灵弹：弹存在但看不见）。
按契约「不得静默丢视图」，两处都不合格。且 512 **小于**驱动自身的对象容量
（`PMProjectileLimits.MaxProjectiles = 1024`，`PMProjectileLifecycle` 的预测/权威两类各 ≤1024），
所以这不是理论边界。

**修复**：
- `MaxPresentationViews` 由 `512` 改为 **`PMProjectileLimits.MaxProjectiles`**（`PMClientSessionHost.cs:139`），
  缓冲与表现字典同值；
- `SyncProjectileViews` 开头用**驱动的真实视图数**判定截断：`driver.ViewCount > buffer.Length` → **`Fail` 整局**
  （`:1427-1434`），注释写明「对齐 MaxProjectiles；不得静默丢视图与候选」；
- 表现字典达上限（理论不可达，防御性）→ **`Fail`** 而不是静默跳过（`:1498`）。

### 1.3 `n == buffer.Length` 不是截断判据 —— 改为 `driver.ViewCount`，并删除会漏退休的早退分支

`CopyViews` 返回 `min(views.Count, buffer.Length)`，所以 `n == buffer.Length` 只说明「缓冲刚好填满」。
旧代码把 `n >= buffer.Length` 当作「可能被截断」并**直接 return**，连移除判定一起跳过 ——
于是那一帧「已经消失的视图」既不 Dispose 也不 `Forget`（旧表现/旧候选账双双残留）。
**修复**：截断判据换成 `driver.ViewCount`（1.2），该早退分支整体删除；容量足够时差集判定**每帧都跑**。

### 1.4 没有表现的 key 永远等不到 `Forget` —— 候选账会永久占住 100 目标上限（最严重）

**真实缺陷**（主侧第 4 点，成立）：旧移除循环只遍历 `ProjectileViews` 字典（= 只装「成功建过表现」的 key）。
而「`RadiusM` 非法 → 跳过表现」与「表现上限 → 跳过」的 key **从未进过字典**，
即便驱动视图里一直有它、候选也一直在收（`CollectAndSubmitProjectileHits` 直接读缓冲，不看字典）：

- 该 key 的收集器账（每 key 最多 **100 目标**）**永久占住**，直到对局结束；
- 更糟的是它会持续消耗收集器的 key 容量：`MaxKeys = 1024` 用尽后，
  **后续所有新 key 的真实命中都会被 `KeyCapacityExceeded` 拒掉**，且这些拒绝发生在客户端（DS 根本收不到）。

**修复**：新增 `Session.ProjectileTrackedKeys`（`:338`）作为「上帧快照里出现过的**全部** key」的追踪集：
- 每个快照 key 在建表现**之前**就入账（`:1478`，位于半径判定之前）；
- 退休判定改为 `追踪集 \ 本帧快照`（`:1443-1448`），逐条 `Dispose`（若在字典里）+ `Forget` + 出追踪集（`:1451-1470`）；
- 顺序改为**先退休再新建**：同帧整体换一批弹时，先建后删会先撞上表现字典上限（这也顺手消除了一个假溢出）；
- 释放时一并清空（`:1868`）。

### 1.5 附加修复：Verify 配额已尽的 key 不再逐子步空转

**事实**：`SubmitPredictedHits` 的 Verify 配额是**按 key** 计的（每 key 5 次，`MaxVerifyPerKey`），
配额用尽后必然返回 `false` 且**不 fault**（`PMR5ProjectileDriver.cs:2295-2301`）。宿主旧逻辑会为它
**每个子步**重新收集 + 编码（≤48 目标/包、payload ≤4096B、多包），一直空转到该弹视图消失 ——
纯浪费，且让「本子步有界预算」被无效工作占掉。

**修复**：收集前先闸一道 `driver.UplinkVerifyUsed(key) >= PMR5ProjectileDriver.MaxVerifyPerKey` → 计数
`ProjectileHitQuotaSkipped++` 后跳过（`:1718-1721`），并进诊断行（`:1909`）。
**这是本报告额外加的一处改动**（主侧清单未点名），若认为属于范围外可直接回退这 6 行，不影响 1.1–1.4。

---

## 2. 主侧清单逐项复核（含判定为「不是缺陷」的项）

| # | 主侧要点 | 复核结论与证据 |
|---|---|---|
| 1 | 只 `CopyViews` 从不 `DrainViewChanges`，脏集合累计 4096 后 fault | **问题成立、机制需纠正**：不 fault，而是整体作废 + `ViewDirtyOverflows` 无界增长 + 常驻 4096 个退休 key。已修（§1.1）。 |
| 2 | buffer/presentation cap 512 只 continue → 漏不可见弹与候选 | **成立**：截断（>512）会连候选一起丢；表现上限满会出幽灵弹。已修（§1.2）。**残留**：`RadiusM` 非法（镜像快照缺 spec，codec 允许 spec 省略）仍只跳过表现并计数 —— 这是 C2「表现层 fail loud、不拿默认值冒充尺寸」的 fail-closed 选择，候选**不会**因此丢（仍按该视图半径参与，半径非法时由收集器 `SegmentRadiusInvalid` 明确拒绝）。若主侧要求「不可见弹一律整局失败」，改这一处分支即可，但会牺牲「快照首帧未带 spec 时整局不崩」的容忍度，故保留并记录。 |
| 3 | `n == bufferLen` 不一定截断，应看 `driver.ViewCount` | **成立**：已改为 `driver.ViewCount > buffer.Length`（§1.3）。 |
| 4 | collector 的 key 即使 view 没 presentation 也要能 Forget | **成立且严重**：会导致候选账永久占位 → key 容量用尽后新命中全发不出。已修（§1.4）。 |
| 5a | 每子步候选的「前后线段」 | **无缺陷**：`PumpProjectileOnce` 每个子步 `driver.Pump(step)` → `RebuildViews()`（驱动 Pump 末尾）→ `SyncProjectileViews` → `CollectAndSubmitProjectileHits`；视图的 `PreviousPosition→Position` 正是**该子步**的线段。零步帧 `Pump(now,0)` 的退化线段由收集器去重（`AlreadyPending/AlreadyCommitted`）吸收。测试面：`SectionE`（在途/提交/回滚）+ `SectionJ`。 |
| 5b | owner fake 被接管后的命中不全丢 | **无缺陷**：`CollectAndSubmitProjectileHits` 只看 `Key.Origin == ClientPredicted && Key.OwnerNetId == 本地`，**不读** `LocalFake/TakenOver`；接管（`TakeOverFake`）不改 `Origin`（登记与晋升用同一个 key，`TryRegisterPredicted` 固定 `ClientPredicted`），镜像分支 `RebuildViews` 沿用 `kv.Key`，因此接管瞬间仍在收集。测试面：`SectionE6` 明确钉住「已提交 A 后仍可收集 B（不因接管停报）」。 |
| 5c | Stopped 最后一段只一次 | **无缺陷**：`segment.Stopped = view.Stopped \|\| view.Hidden` → 收集器置 `StopPending`，`Commit` 后 `StopCommitted`，此后任何目标都是 `StopSegmentAlreadyReported`；发送失败 `Rollback` 会清掉停止标记（可重试，不永久关账）。测试面：`SectionF1-F5`。 |
| 5d | 历史 anchor 必须来自当前 SP 表现 | **无缺陷**：`BuildProjectileTargets` 取 `rig.Driver.SamplePresentation()` 的 `ServerFrame`（`PMInterpolatedState.ServerFrame` = **最新权威快照的 AuthorityServer 帧**，见 `PMInterpolationBuffer.cs:34-35` 注释）与 `rig.Driver.StreamVersion`（目标**当前**输入流）；非 `AuthorityServer` 域或无效帧直接跳过，收集器再校验一次（`TargetServerFrameInvalid`）。测试面：`SectionD8`（`Input` 域与 `None` 均拒）、`SectionH3`（帧/流透传）。 |
| 5e | Input F 边沿只取一次 | **无缺陷**：`Input.GetKeyDown(KeyCode.F)` 在 `PumpProjectiles` 每 Update 采样**一次**并缓冲；`TryDiagnosticFire` 消费一次，且**先确认本帧能开火再消费边沿**（冻结/失去 AP/驱动不可用时边沿留到下一帧），200ms 墙钟门在被消费之后（该帧内的按键被门拦掉是限流的预期语义，不是丢输入）。 |
| 5f | 初帧 `LastTime=0` 超大 delta | **未发现缺陷**：驱动/生命周期/协调器的墙钟全是**绝对时刻**（`AcceptWallClock` 取 max；TTL/墓碑存 `deadline = now + TTL`），唯一用 delta 的地方（`CatchUpRegisteredMotion` 的 `wallNowMs - RegisteredWallTimeMs`）减的是登记时写入的绝对时刻；`Pump` 首帧 `_wallStarted=false` 跳过倒退判定后再写 `_lastWallMs`；宿主 `LastFireWallMs = double.NegativeInfinity`（首帧 `- (-inf) = +inf` 直接放行）、`ProjectileAccumulatorMs` 从 0 起累加。**结论：客户端路径上没有「用 0 初始化时间戳做减法」的点**。 |
| 5g | `Fail` 之后继续访问已释放字段 | **未发现缺陷**：`Fail` 只置 `Faulted/FaultReason` + `FreezeMovements`，**不释放任何字段**；唯一的释放路径 `ReleaseSession`（`Stop` / `Enter` 失败分支）不调用 `Fail`，且每个失败分支都是「先 `ReleaseSession` 再 `return`」。`Fail` 的全部调用点后面要么立即 `return`，要么 `break` 出子步循环；本报告新增的排空调用发生在 `break` 之后，只读 `session.ProjectileDriver`（非空）。**残留（非缺陷，未改）**：`PumpProjectiles` 失败后本帧 `PumpActive` 仍会走一次 `SendPendingProbes` + 心跳（端点仍有效，下一帧才进 `Faulted` 分支），不涉及已释放字段。 |
| 5h | Destroy/Dispose/事件取消与 map 销毁顺序 | **无缺陷**：`ReleaseSession` 里 `ReleaseProjectiles`（逐副本 `UnbindPlayer` → `driver.Dispose()` → `motion.Dispose()` → 逐个 `Presentation.Dispose()` → 清字典/追踪集/缓冲 → `collector.Clear()` → 无条件还原 `PMR3Player.ProjectileWarn`）**先于** `ReleaseMovements`、`PMR3Runtime.Detach`、`Bridge.Dispose`、场景对象 `Destroy`、最后 `BattleMap.Dispose()` / 诊断场景卸载 —— 表现对象先销毁、场景/地图后拆，顺序正确。 |
| 6 | collector NaN/跨 owner/跨 stream/计次提交失败必须不占位；不得新增 Unity/R3 | **无缺陷**：所有拒绝路径都在「建账之前」返回（`Segment*`/`Target*`/`Geometry*` 均在 `_ledgers` 增删之前），`TryCollect` 只写**在途**、`Commit` 才占位、`Rollback` 撤回；文件依赖面只有 `System` / `PMProjectile` 自身契约 / `PMNet.Mover`，**无 UnityEngine、无 PMR3/R3**（这由 `PMProjectileCandidateTest` 的 csproj 只编 4 个纯 C# 文件且能编译通过来证明）。测试面：`SectionC7/D10`（被拒不建账）+ 新增 `SectionJ`。 |

---

## 3. 新增测试：`SectionJ_ReviewRegressions`（14 条，123 → 137）

把主侧关心的「宿主侧性质」在收集器侧钉成可执行断言（预期值独立手算，不引用实现常量）：

| 断言 | 内容 |
|---|---|
| J1（4 条） | 同一收集器连续 10 次非法调用（NaN 线段 / NaN 弹半径 / 目标 NetId 0 / 目标位置 NaN / 跨 owner / `ServerDirect` / 跨 epoch / stream 0 / 帧锚 `Input` 域 / 纯几何未命中）→ `KeyCount == 0`、`Rejected == 10`、`PendingKeyCount == 0`、`HasPending == false`（**被拒一律不占账**） |
| J2（5 条） | **在途未提交**就 `Forget`（宿主「视图消失」路径，且该 key 可能没建过表现）→ `KeyCount/PendingTargetCount` 归零、`ForgottenKeys > 0`（账整体消失，不永久占位） |
| J3（2 条） | 同 key 收满 100 目标后 `Forget` → 换一批目标**可再收满 100**（100 目标容量**真的**被释放，对应 §1.4） |
| J4（3 条） | 发送失败 `Rollback` 不动已提交目标数、目标 B 可重收、已提交目标 A 仍 `AlreadyCommitted`（失败不占位 + 不误放行） |

`PMProjectileCandidateCollector.cs` **本次未改动**（在写入边界内、但复核后确认无需改）：
它已经满足「被拒/失败不占位」「停止只允许最后一段」「容量有界」「纯 C# 无 Unity/R3」；
§1.4 的缺陷在**宿主侧的记账**，修复点也在宿主（追踪集），把 `Forget` 的触发条件补全即可。

---

## 4. 诚实边界（本批未做 / 未验）

| # | 项 | 说明 |
|---|---|---|
| R1 | **实机未验** | 未启动 Unity、未跑真实 PhysX/双端联机。本轮结论全部来自「真实源码编译 + 纯 C# 可执行断言 + 逐行语义复核」。 |
| R2 | **诊断表现 ≠ 英雄弹外观、无伤害结算** | 与 `_r5_client_host_report.md` U2/U3 同口径，本报告不翻案、不追加声明。 |
| R3 | `RadiusM` 非法的视图仍「不渲染但参与候选」 | 见 §2 第 2 点的残留；若要求整局失败需改该分支（当前保留 fail-closed 跳过 + `skipped` 计数）。 |
| R4 | 未跑 12 个 `PMProjectile/**` glob 消费者的全量回归 | 只跑了委派要求的 `PMProjectileCandidateTest`（build+run）与 `PMClientCheck`，附加跑了 `PMUnityGlueCheck`；`PMR4UnityCheck` 按委派要求**不并行编**，未跑。 |
| R5 | 排空的分配量 | 稳态每帧几条 `PMR5ProjectileView`（struct）的数组，与「本帧变化量」同阶；**未**在真机上量过它对 GC 的实际影响。 |
| R6 | DS 侧脏通道 | `PMDsSessionHost` 是否已彻底排空由并行组负责（其工作区已出现 `DrainDsProjectileViews`），本报告只做只读观察，不评价其正确性。 |
| R7 | 并行组文件在本轮发生过中途失败 | 见 §0；最终同一条命令已通过，但该文件在会话期间被改动，故「同一提交的一致性」需以主侧整合时机为准。 |

---

## 5. 已检查范围（证据链）

**完整读取**：`AGENTS.md`、`Client/Assets/AGENTS.md`、`Docs/plans/net-r5-network-contract.md`（末段为准）、
`Docs/plans/_r5_client_host_report.md`、`C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`。

**逐行复核的代码面**（见页首清单）：宿主 `Enter`/`PumpActive`/`PumpMovement`/`PumpProjectiles`/
`PumpProjectileOnce`/`SyncProjectileViews`/`BuildProjectileTargets`/`TryDiagnosticFire`/
`CollectAndSubmitProjectileHits`/`ReleaseProjectiles`/`ReleaseSession`/`Fail`/`FreezeMovements`；
驱动 `Pump`/`ApplyInbound`/`ApplyMirrors`/`ApplyMirrorPayload`/`TakeOverFake`/`RevokeFake`/
`AdvanceLocalFakes`/`SweepLocalState`/`SweepRetiredLocalKeys`/`TryFire`/`SubmitPredictedHits`/
`CopyViews`/`DrainViewChanges`/`RebuildViews`/`MarkViewDirty`/`ViewEquals`/`Dispose`；
生命周期与协调器的墙钟/上限/登记/晋升；`PMInterpolationBuffer.Sample` 的 `ServerFrame` 语义；
codec 的 `TryDecodeSnapshot`（spec 可缺省）。

**仓库外临时产物（不属交付、可删）**：`%TEMP%\pm_r5_edit_host.py`、`%TEMP%\pm_r5_edit_test.py`、
`%TEMP%\pm_r5_edit_doc.py`、`%TEMP%\pm_r5_client_harness\`（含 HEAD 版 `PMDsSessionHost.cs` 与一份
`PMClientCheck.csproj` 的派生副本）—— 仅用于「在并行组文件半成品期间」提供编译证据；
仓库内除上表 3 个文件外无任何新增/删除/修改。
