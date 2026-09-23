# R5-A3 独立审查与返工报告（PMProjectileCoordinator）

> 任务类型：**comprehensive**（独立审查 A3 整合器并返工；主 Agent 已明确拒绝三项偏离）
> 只读输入（按委派顺序）：`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`
> → `Docs/plans/net-r5-projectile-contract.md`（全文，以「A3/B1集成冻结」为最新）
> → `Docs/plans/_r5_integration_report.md`（被审查对象）
> → `Docs/plans/_r5_lifecycle_review.md`（A1 公开 API 与口径）
> → `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`。
> 生产代码只读读取：`PMProjectileLifecycle.cs`（全文）、`PMProjectilePending.cs`（全文）、
> `PMProjectileContracts.cs`（全文）、`PMProjectileHistory.cs`/`PMProjectileValidator.cs`（L0/L1/预算段）。
> **未启动 Unity；未提交（git/svn 均无写入）；未递归委派；未改主计划；未改 Pending/Validator/Codec/共享 Contracts。**

**结论**：主 Agent 拒绝的三项偏离**全部按「真实方案」返工**（不是绕开、不是记账掩盖）；
另**独立发现 2 个真缺陷**（命中上报未绑定认证 owner / C 队列暂存期无去重预留）与 2 个语义级真缺陷
（StopOnHit 在 Pending 不停 / 终态激活判决淘汰后可复活），并修正 A1 侧一处「同一事实两个真值」
（G1 墓碑未写入冻结 State）。断言数：LifecycleTest **470 → 510**、IntegrationTest **→ 952**，全部通过。

---

## 0. 验证命令与结果（本机实测，全部 exit 0）

```bat
REM 语言面门禁（netstandard2.0 + C#7.3，零 UnityEngine）
dotnet build Tools/PMProjectileCoreCheck/PMProjectileCoreCheck.csproj        -c Release   REM 0 警告 / 0 错误
dotnet build Tools/PMProjectileIntegrationCheck/PMProjectileIntegrationCheck.csproj -c Release REM 0 警告 / 0 错误

REM 真实运行测试（net8.0）
dotnet build Tools/PMProjectileLifecycleTest/PMProjectileLifecycleTest.csproj   -c Release REM 0 警告 / 0 错误
dotnet build Tools/PMProjectileIntegrationTest/PMProjectileIntegrationTest.csproj -c Release REM 0 警告 / 0 错误
dotnet Tools/PMProjectileLifecycleTest/bin/Release/net8.0/PMProjectileLifecycleTest.dll     REM 510 / 0
dotnet Tools/PMProjectileIntegrationTest/bin/Release/net8.0/PMProjectileIntegrationTest.dll REM 952 / 0
```

| 命令 | 结果 |
|---|---|
| `PMProjectileCoreCheck`（A1 门禁，netstandard2.0 + C#7.3） | **0 警告 / 0 错误** |
| `PMProjectileIntegrationCheck`（A3 门禁，netstandard2.0 + C#7.3） | **0 警告 / 0 错误** |
| `PMProjectileLifecycleTest`（net8.0 运行） | **510 通过 / 0 失败**，退出码 0 |
| `PMProjectileIntegrationTest`（net8.0 运行） | **952 通过 / 0 失败**，退出码 0 |
| `python Tools/check_cs_braces.py <四个 .cs>` | BALANCED，深度全程非负且末尾归零，PASS |
| 编码自检（四个 `.cs`） | 全部 `BOM=True CRLF=n loneLF=0 loneCR=0 utf-8 ok` |

IntegrationTest 分段：A 43 / B 23 / C 16 / D 13 / E 47 / F 18 / G 28 / H 6 / I 8 / J 40 / K 16 / L 23 /
M 552 / **N 32 / P 25 / Q 38 / R 12 / S 12**（N/P/Q/R/S 为本次返工新增）。
LifecycleTest 分段：A–K 原样 + **L 40**（本次新增：原地升级 + 终态判决淘汰后的复活防护）。

---

## 1. 主 Agent 明确拒绝的三项偏离 —— 逐项返工

### 1.1 偏离 1：`RequestSpawn` 用 `TryRegisterPredicted` 预留且**从未升级权威** ⇒ 玩家弹不能追赶

**问题（成立，已修）**：admission 必须保留「收请求时预留 ID」（否则同一 activation 的多弹乱序解挂会被
ID 高水位拒），但预留项在 A1 的 `TryBeginCatchUp` 眼里是 `NotAuthority`
（`PMProjectileLifecycle.cs:1153` 起：`if (e.Predicted) → NotAuthority`）。
原实现把「受信任的 authorityNetId 只记在整合器自有 KeyRecord、冻结 `State.AuthorityNetId` 恒为 0」
（旧报告 §7 G3）当作「域口径正确」，结果是 **activation 被权威 Confirmed 之后，这颗弹永远无法开始权威追赶**
——玩家弹没有任何延迟补偿。原测试 L 用 `CheckEq(..., NotAuthority, "L: 预测弹不能走权威追赶")`
把这个缺陷固化成「正确行为」，属于把缺陷写进验收。

**返工（真实方案，不是绕过）**：新增 A1 可信**原地**升级入口

```csharp
// PMProjectileLifecycle.cs:1283
public PMProjectilePromoteOutcome TryPromoteReservedToAuthority(
    PMProjectileKey key, uint authorityNetId, double wallNowMs);
// PMProjectilePromoteResult: Promoted / AlreadyPromoted / NotPredicted / NotConfirmed /
//                            UnknownKey / StaleEpoch / Retired / InvalidAuthority / InvalidClock / Capacity
```

不变式（逐条对应主 Agent 的要求）：

| 要求 | 落点 |
|---|---|
| 不重新分配 ID | 原地改 `Entry` 字段，不动 `Key`；新增 `Entry.Promoted` 标记（`Lifecycle.cs:614`） |
| 不改 Origin | 只写 `Predicted/PredictedAlive/Promoted/AuthorityNetId/State.AuthorityNetId`，不碰 `Key.Origin` |
| authorityNetId 真实非 0 | `authorityNetId==0 → InvalidAuthority`；成功时同时写账本与冻结 State |
| **只有 Confirmed 可启动** | `e.Activation != Confirmed → NotConfirmed`（Pending 等待、Rejected 已撤销都不得升级） |
| 幂等 | 重复升级 → `AlreadyPromoted`（不重复计数、不覆盖已有权威 ID） |
| 升级后可追赶 | `_predictedCount-- / _authorityCount++` ⇒ `TryBeginCatchUp` 返回 `Started` |

**整合器接线**：
- admission 时 activation 已 Confirmed ⇒ 生成前先 `PromoteReservedOrFail`
  （`Coordinator.cs` RequestSpawn「activation 已 Confirmed」分支）；
- `ResolveActivation(Confirmed)` 的 A 队列解挂循环里，**先升级再发 SpawnReady**
  （顺序关键：下行 `Snapshot` 要求「权威 ID 非 0」，生成出口里的冻结状态必须已经带真实权威 ID）；
- 升级失败（除幂等 `AlreadyPromoted` 外）⇒ **故障**（`InternalInvariant`），不假装成功。

**测试（正反）**：
- `N` 段：`RequestSpawn`（Pending）→ `CatchUpMotion` = `NotAuthority`（预留期确实不能追赶，这是设计，
  但不再是「永久不能」）→ `ResolveActivation(Confirmed)` → `IsPromoted` / 权威 ID = 900 →
  **`CatchUpMotion(100ms)` = `Advanced` 且位置真的沿 +Z 推了 1.0m**（再 50ms → 1.5m）→
  `AlreadyStarted` 只开始一次；冻结状态权威 ID 同步；生成出口的 `State.AuthorityNetId` 非 0；
  幂等 `AlreadyPromoted`；`NotConfirmed`（Pending 不得升级）/`InvalidAuthority`(0)/`UnknownKey`/`StaleEpoch`；
  权威原生登记 → `NotPredicted`；已 Rejected 的预留 → `Retired`。
- A 段（原闭环）：确认后的弹在 admission 当场升级，冻结 `AuthorityNetId == 900`（原断言「归 0」已按返工改正）。
- L 段：`Pending 预留 → NotAuthority` 保留（语义改为「还没被确认」），并新增 N 段的正向追赶。

### 1.2 偏离 2：四个出口队列满时「丢最旧 + 计数」⇒ 丢真实伤害、去重已提交

**问题（成立，已修）**：原 `Push{Decision,SpawnEvent,StopEvent,Settlement}` 在队列满时
`RemoveAt(0)` + 计数（旧报告 §3.3「溢出丢最旧并计数」）。后果：**已受理的激活确认/结算被丢弃**，
而结算前的去重（`TryAddHitTarget`）已经提交 ⇒ 该目标**永远不会再结算**（真实伤害静默丢失，
`DroppedSettlementCount` 只是事后统计，不能把伤害换回来）。这正违反契约
「全部对外队列有界、**溢出显式失败**，不能静默丢确认导致永久假弹」。

**返工**：引入**永久 Faulted** 语义（`Coordinator.cs:2258` `Fault` / `2273` `ThrowIfFaulted` /
`2288` `EnsureOutputHeadroom`）：

1. 四个出口队列任一**在调用入口已达容量**（`EnsureOutputHeadroom`）或**推送时写入将越界**（`Push*`）⇒
   `Fault(reason, message)`：先提交故障状态（`IsFaulted=true` + `FaultReason` + `FaultMessage`），
   再抛 `InvalidOperationException`。
2. **既有排队项目不被覆盖**：`Fault` 不 `RemoveAt`、不 `Add`，队列原样保留；`Drain*` 在 Faulted 下仍可用
   （宿主需要能取出现有项目做处置），且不解除故障。
3. **状态变化前尽量容量预检**：所有变更型入口（`RequestSpawn` / `ServerDirectSpawn` / `ResolveActivation` /
   `ReportHits` / `AdvanceMotion` / `CatchUpMotion` / `PurgeExpired` / `ClearOwner` / `ResetState`）
   首行 `ThrowIfFaulted() + EnsureOutputHeadroom()`；
   另外 `DrainLifecycleReleasesIntoDecisions` 在做「从 A1 队列取出」之前先比对
   `PendingReleaseCount` 与决策队列剩余空间，避免「取出后放不进 ⇒ 真丢激活结论」。
4. **所有调用可见 Faulted**（含 `PurgeExpired`/`ResolveActivation`/`ClearOwner`/`ResetState`）：
   不靠计数隐瞒；`IsFaulted/FaultReason/FaultMessage` 是只读可观测量。
5. **恢复路径唯一**：`AdvanceEpoch`（宿主断连 → 新 epoch 重建）清除故障；同 epoch 的 `ResetState`
   在故障下也抛异常（数据已不完整，不得假装恢复）。
6. 故障原因枚举：`DecisionQueueOverflow / SpawnEventQueueOverflow / StopEventQueueOverflow /
   SettlementQueueOverflow / KeyRecordTableOverflow / InternalInvariant / LifecycleReleaseDrop`。
   已删除 `Dropped{Decision,SpawnEvent,StopEvent,Settlement}Count` 与 `EvictedKeyRecordCount`
   （整合器自身记账表溢出也改为故障，不再「淘汰最旧 + 计数」）。

**测试（正反）**：
- `J` 段：连续 `ServerDirectSpawn` 到第 513 次 ⇒ **抛 `InvalidOperationException`**、`IsFaulted`、
  `FaultReason=SpawnEventQueueOverflow`、`SpawnEventCount==512` 且 **Drain 出来的正是最早 512 条（1..512，
  未被后来者覆盖）**；Drain 后 `SpawnEventCount==0` 但**仍 Faulted**；
  `PurgeExpired`/`ResolveActivation`/`ClearOwner`/`ResetState`/`ReportHits`/`AdvanceMotion`/`CatchUpMotion`/
  `ServerDirectSpawn` **全部抛异常**；`AdvanceEpoch` 恢复（`IsFaulted=false`、reason 归零）。
- `M6` 段：把生成出口每轮抽干，只让结算出口涨到 512 ⇒ 第 513 条上报在入口即 `Fault`
  （`FaultReason=SettlementQueueOverflow`），**512 条已受理结算一条不少**（逐条断言命中目标）、
  既有结算项不被覆盖、Faulted 下 Drain 可取 512 条。

### 1.3 偏离 3：宿主运动 hook 失败/非 finite/抛异常时**回退直线**（= 穿墙）

**问题（成立，已修）**：原实现在 `TryStep` 返回 false / 抛异常 / 输出非 finite 时「退回直线核心结果」，
等于把「碰撞查询失败」当成「无障碍」——直线内核完全不知道墙 ⇒ **穿墙**。

**返工**：`IPMProjectileHostMotion` 契约重写为 **fail closed**（`Coordinator.cs:355` 起接口文档）：

| 失败形态 | 新行为 |
|---|---|
| `TryStep` 返回 false | 本步**不推进**（不写账本），停在**账本真实当前位置**，`RecordStop` 建墓碑 + 停止出口，结果 `HostMotionFaulted` |
| `TryStep` 抛异常 | 同上（`finally` 之外显式 catch 后走同一 fail-closed 落点） |
| `TryStep` 输出 position/velocity/yaw 非 finite | 同上（NaN 不得进入冻结状态） |
| `TryStop` 抛异常 | 当步运动已生效，但**立即就地停止**（`HostMotionFaulted`），不得「继续飞」 |
| `TryStop` 返回 true 但 stopPosition 非 finite | 同上（不把 NaN 写进账本） |
| `TryStop` 返回 false | **正常语义**（本步不停止），不是失败 |

- 想表达「本步无阻挡」必须**显式返回 true 并回填入参给的直线位置/速度/朝向**（接口文档写明）。
- 传入 hook 的 `snapshot` 是 `TryGetFrozen` 的**深 clone**（`Entry.StateCopy()` + `Spec.Clone()`）：
  hook 可以随意改写它，但写不回账本，传入的总是弹的真实当前位置（供碰撞查询）。
- 新增可观测量 `HostMotionFailureCount`；新增结果 `PMProjectileAdvanceResult.HostMotionFaulted = 10`（追加，不破坏既有取值）；
  运动已是终态（`state.Stopped`）时**不调用 hook**（否则会把「已停止」误报成 hook 失败）。

**测试（新增 P 段，25 项）**：
hook 返回 false / NaN / 抛异常 ⇒ `HostMotionFaulted` + `Stopped` + **位置停在真实位置**（Z 不进位、账本无 NaN）+
停止出口 + 失败计数 + 事后运动 `NotMovable`（不偷偷继续飞）；
`TryStop` 抛异常 / 返回 NaN 停止点 ⇒ 当步位置保留（0.5m）但立即停止；
合法路径（显式 true + 回填直线 + 把 snapshot 改脏）⇒ 账本位置是直线结果 0.5m 而**不是** hook 写的 9999，
且 `SeenSnapshotPosition` 证明传给 hook 的是**真实当前位置**；`ZOnlyMotionHook` 断言其看到真实 X/Z。

---

## 2. 独立检查完整流程：主 Agent 点名项 + 新发现的真缺陷

> 委派要求「独立检查完整流程而非只改三处」。以下每项都给了**判定 + 证据 + 处置**，含判定为「已正确/不可达」的项。

### 2.1 【新发现，真缺陷】命中上报不绑定认证 owner ⇒ 跨 owner 伪造 + 未登记 ServerDirect「占位」

**证据**：原 `ReportHits(PMProjectileHitBatch batch, double wallNowMs)` **没有认证身份入参**，
owner 与 origin 全部取自 packet 自述的 `batch.Key`。契约「身份与安全」明写
**「OwnerNetId 必须取认证会话绑定网络玩家而非客户端自报；客户端只能 ClientPredicted」**。两个可利用面：
1. **跨 owner**：客户端可上报别人的弹（`OwnerNetId` 自报）= 替他人提交命中/触发停止/消耗他人 Verify 配额；
2. **未登记 key + 自报 ServerDirect**：B 队列（Verify-before-Spawn）对未登记 key 一律原样暂存，
   于是客户端可以**预先占位**一个 `(epoch, owner, 未来 ProjectileId, ServerDirect)`，
   等权威侧真的生成那颗弹时，暂存的伪造批次被 FIFO 回放 ⇒ **伪造命中进入 L0–L4 并可能结算**。

**返工**：
```csharp
// Coordinator.cs:1046
public PMProjectileHitReportOutcome ReportHits(
    PMProjectileHitBatch batch, uint authenticatedOwnerNetId, double wallNowMs);
```
- `authenticatedOwnerNetId == 0 || batch.Key.OwnerNetId != authenticatedOwnerNetId → UntrustedOwner`
  （**新增结果值** `PMProjectileHitReportResult.UntrustedOwner = 13`，追加不破坏既有取值）；
  该校验在任何分流（含 B 队列）之前，跨 owner 一律不进入任何路径；
- 未登记 key 的上报**只接受 `Origin=ClientPredicted`**（否则 `Invalid`）：ServerDirect 弹只能由权威入口产生，
  「未登记就自称 ServerDirect」本身就是伪造入口；
- 已登记 key（含权威弹）的上报流程与结算语义**不变**（F/I/L/M 段原有用例仍通过）。

**测试（新增 S 段 + 改写全部上报点）**：跨 owner ⇒ `UntrustedOwner`、owner=0 ⇒ `UntrustedOwner`、
被拒上报不进 C/B 队列且**不消耗 Verify 配额**；未登记 ServerDirect ⇒ `Invalid` 且不进 B 队列；
未登记 ClientPredicted 仍进 B 队列；已登记权威弹仍可校验结算；
200 次跨 owner 洪泛**全部被拒**、B 队列仍有界（≤128）。

**残留（供主侧裁决，见 §5）**：已登记 `ServerDirect` 弹若其 `OwnerNetId` 就是某玩家，
该玩家仍可构造自己 owner 下的 ServerDirect 命中批次（受白名单/几何/去重约束）。
B1 冻结已要求「上行 origin 只 ClientPredicted」，建议由 **B1 codec 读端**把这条关死（A3 不依赖 codec，故无法代劳）。

### 2.2 【新发现，真缺陷】C 队列（Pending 命中结论）**暂存期没有去重预留**

**证据**：`PMProjectilePendingHits.TryStash` 按 key 合并追加，上限 64 套/键；原实现在 Pending 期间
**每次都把同一目标重复入组**，去重只在 Confirm 结算时才发生（`TryAddHitTarget`）。
后果：重复上报（UDP 重传 / 客户端重报）会耗尽单键追加上限（`KeyAppendCapacity`，静默丢弃），
把后来**真正不同的目标**挤掉 ⇒ 真实伤害丢失；且重复结论毫无信息量。

**返工**：整合器自持 `KeyRecord.PendingHitTargets`（`Coordinator.cs:480`）作为**暂存期去重预留**：
- 入 C 队列前过滤「已在暂存中的目标」；仅在**真的入组**时才提交预留（`KeyAppendCapacity` 不提交）；
- 组被**兑现/被拒/被丢弃（TTL 或容量淘汰）**时**必须释放预留**：
  `ClearPendingHitTargets`（`2322`）+ `ReleaseOrphanPendingHitReservations`（扫描「还记着预留但 C 队列已无该组」）
  + `ReconcileDroppedPendingHitGroups`（容量淘汰是 A1 内部行为、只报计数不报 key，靠 `DroppedGroupCount` 变化触发清扫）；
- 与生命周期 `HitTargets` 的分工写清：后者是**结算期**提交的去重，前者只是**暂存期**预约；
  **故意不把预留写进生命周期** —— 因为预留一旦活得比「暂存组」久，那个目标就会被永久挡住（丢真实伤害）。

**测试（新增 Q 段 38 项）**：同一目标重复上报 4 次 ⇒ `DedupPendingHitSkipCount=3`、组内仍只有 1 套结论、
`DroppedAppendCount=0`；随后另一目标仍能入组（2 套）；Confirm 结算 2 命中且只有一条结算；
组被丢弃后预留释放并可重建结算；129 组填满后的容量淘汰路径：`GroupCount=128`、`DroppedGroupCount` 可观测、
**被淘汰 key 的预留确实释放**（重报能重新入组，且确认时该 key 真的结算了），
128 条结算对应 128 个不同 key（无重复）、每条只含 1 个命中。

### 2.3 【真缺陷】`StopOnHit` 只在 Confirm 才停（Pending 期间继续飞）

**证据**：原 `SettleHits` 才处理 `spec.StopOnHit`；L0 判 Pending 时弹继续 `AdvanceMotion` ⇒
幽灵轨迹 + 依据错误位置产生的后续命中。

**返工**：`ProcessVerify` 的 Pending 分支在暂存后**立即** `RecordStop`（`StopRecorded=true`），
并由此天然幂等：Confirm 时 `reg.Stopped` 已为真 ⇒ 不会再发第二次停止通知。

**测试（新增 R 段）**：Pending 上报 ⇒ `StopRecorded=true` + 停止出口 1 条 + 之后运动 `NotMovable`；
Confirm 时结算候选命中（墓碑窗口内）且**不产生第二次停止通知**；
对照：`StopOnHit=false` 时 Pending 不停、可继续飞。

### 2.4 【真缺陷】终态激活判决被容量淘汰后，迟到/重放裁决可「复活」

**证据**：`PMProjectileLifecycle.TrySetActivationResult` 在账本满时淘汰最旧终态项（只 512），
淘汰后 `_ledgerIndex` 查不到 ⇒ 一条迟到/重放的 `Confirmed` 会在同一 `(owner, activationId)` 上
**新建**账本项并返回 `Applied`；此后同一 activation 上的**新登记**会被当成 Confirmed **直接生成**
（已 Rejected 的 activation 因此复活）。

**返工（A1 侧，属允许的支撑提升）**：新增**有界终态判决记忆** `_terminalMemory`（`Lifecycle.cs:698`，
≤ `MaxActivationLedger`，插入序环 + 每次淘汰时记入 `RememberTerminalVerdict`（`1994`）），
只被**写路径**（`TrySetActivationResult`，`1948`）与**登记裁决**（`TryGetActivationVerdict`，`832`）查询：
- 淘汰后迟到/反向/同向重复/降回 Pending 的写入一律 `AlreadyTerminal`（不再新建账本项）；
- 淘汰后同 activation 的新登记仍能看到 `Rejected` ⇒ `RevokedRejected`；
- **刻意不改变** `TryGetActivationResult` 的「账本可查性」语义（A1 既有测试断言淘汰后返回 false，已保留）；
- 可观测量 `TerminalVerdictMemoryCount`；有界性诚实标注：超出环容量后**只能遗忘**（不假装「永远记得」）。

**测试（LifecycleTest 新增 L 段 40 项）**：写入 Rejected → 灌 512 条 Confirmed 触发淘汰 →
`TryGetActivationResult` 如实 false、`TerminalVerdictMemoryCount>0`、`EvictedTerminalLedgerCount>0`；
迟到 Confirmed ⇒ `AlreadyTerminal` 且**不新增账本项**；同 activation 新登记 ⇒ `RevokedRejected`；
同向重复 Confirmed 与降回 Pending 也 `AlreadyTerminal`。

### 2.5 【已核查】未知 key 的 Verify 暂存「可灌」与**源 marker 语义**

- **可灌**：已核对为**有界且显式**——单 key 上限 5、总量 128、TTL 2000ms、惰性清理，
  超出返回 `KeyCapacity`/`TotalCapacity` 并计数（A1 侧 + 整合器侧 `StashCapacityRejectionCount` 双计数），
  整合器映射为结果 `Capacity`（不是静默丢）。本次新增 §2.1 的 **owner 绑定与 origin 限制**后，
  连「用别人 owner / 用 ServerDirect 占位」这两条灌入路径也被拒；M/S 段覆盖（单 key 第 6 条 `Capacity`、
  总量 128、TTL 联动清空、200 次跨 owner 洪泛全拒）。
- **源 marker 语义**：已核对 `SourceBehaviorInstanceId` **不参与唯一性**（A1 已把挂起键改成完整
  `PMProjectileKey`），它只是载荷/诊断字段；整合器把 `0` 视为 `InvalidRequest`（0 号源不存在）。
  因此「同一 activation + 同一源的多颗散弹」不再被 `DuplicateSource` 挡（E 段 20 颗全部入队、一次解挂）。
  **结论：正确，无需改动。**

### 2.6 【已核查】C 队列过期后「同 key 可否重新建结算」

- **TTL 路径**：A 队列（挂起 Spawn）与 C 组共用 `PendingTtlMs=2000`，但 A 的计时起点更早（先生成请求、后上报命中），
  所以整合器 `PurgeExpired` 的联动顺序下**总是 A 先到期**：该 key 的 C 组被连带 `TryResolve(Rejected)`
  丢弃（`PendingHitGroupsExpired=0`），ID 预留退回、登记进终态标记。
  此时同 key 重报 ⇒ `Retired`，**不能重建结算**（终态不倒退），Q 段断言。
  ⇒ 也就是说 C 组自身的 TTL 分支在整合器联动下**不可达**，它是防御网（已保留，且预留释放逻辑同路径覆盖）。
- **容量淘汰路径（可达）**：`MaxPendingHitGroups=128` 满时 A1 内部丢最旧未结算组（只报计数）。
  整合器据此清扫并**释放该组的去重预留**，于是被淘汰的 key 重报后**可以**重新入组并在确认时结算
  **恰好一次**（Q 段 129 组用例：128 条结算 / 128 个不同 key，被淘汰后重报的那颗确实结算）。
  ⇒ 「重建结算」是允许且正确的，前提是**预留必须随组释放**（这正是 §2.2 的返工点）。

### 2.7 【真缺陷，顺手修】A1 `TryRecordStop` 未把墓碑写入冻结状态（旧报告 G1）

`TryRecordStop` 写了账本 `e.TombstoneUntilMs` 与 `State.Stopped/StopWallTimeMs/TimeAfterStoppedMs`，
但**漏写 `e.State.TombstoneUntilMs`**（镜像停止路径是写的）⇒ `TryGetFrozen` 出来的
`State.TombstoneUntilMs` 恒为 0，任何按 `State` 判墓碑的消费方（含 A2 验证器 L1 截止）在「本地停止」
路径上**静默失效**（同一事实两个真值）。已在 A1 内补一行（`Lifecycle.cs:1484`），
整合器原有的「用账本值补齐只读入参」保留（幂等、双保险）。既有 470 项断言无一依赖 0 值，全部继续通过。

### 2.8 【已核查 + 防御性提升】生命周期「待取释放队列」溢出

A1 的 `_pendingReleases` 满 1024 时「丢最旧 + 计数」。可达性分析：容量 = 预测活对象上限（1024），
且整合器**每次调用都会抽干**该队列（`DrainLifecycleReleasesIntoDecisions`，并先做容量预检），
故正常链路不可达。但「释放项」是**撤销假弹的指令**，静默丢 = 永久假弹，因此整合器把
`DroppedReleaseCount` 的增长**提升为显式故障**（`LifecycleReleaseDrop`，`Coordinator.cs:2227`）。
诚实标注：**未构造出可达用例**（受 1024/类别上限约束），该守卫是防御网。

---

## 3. API 变更清单（B/C 与网络层必须对齐；A3 冻结面变更需主侧确认）

| 变更 | 旧 | 新 | 理由 |
|---|---|---|---|
| 命中上报入参 | `ReportHits(batch, wallNowMs)` | **`ReportHits(batch, authenticatedOwnerNetId, wallNowMs)`** | §2.1：身份必须取认证会话绑定玩家；上行只能 ClientPredicted |
| 上报结果 | — | `PMProjectileHitReportResult.UntrustedOwner = 13`（追加） | 身份不符的显式原因 |
| 运动结果 | — | `PMProjectileAdvanceResult.HostMotionFaulted = 10`（追加） | §1.3：hook 失败的显式结果 |
| hook 契约 | `TryStep` false = 用直线；异常/非 finite = 用直线 | false/异常/非 finite = **失败（fail closed：不推进 + 就地停止）**；无阻挡必须显式 true 回填直线 | §1.3 |
| 故障面 | `Dropped{Decision,SpawnEvent,StopEvent,Settlement}Count`、`EvictedKeyRecordCount` | **删除**；新增 `IsFaulted` / `FaultReason`(`PMProjectileFaultReason`) / `FaultMessage` | §1.2：溢出即显式失败，不靠计数隐瞒 |
| 出口语义 | 满则丢最旧 | 满则**永久 Faulted + `InvalidOperationException`**；既有项目保留；`Drain*` 仍可用；`AdvanceEpoch` 唯一恢复 | §1.2 |
| 预热/观察 | — | `HostMotionFailureCount` / `DedupPendingHitSkipCount` / `ReleasedPendingHitReservationCount` / `PromotedProjectileCount` | §1.3 / §2.2 / §1.1 可观测 |
| 生命周期新增 | — | `TryPromoteReservedToAuthority` / `PMProjectilePromoteResult` / `PMProjectilePromoteOutcome` / `PMProjectileRegistration.Promoted` / `TerminalVerdictMemoryCount` / `Entry.Promoted` | §1.1 / §2.4 |
| 冻结状态真值 | 本地停止后 `State.TombstoneUntilMs == 0` | 与账本一致（非 0） | §2.7 |
| 未变 | — | `PMProjectileContracts.cs` / `PMProjectilePending.cs` / `PMProjectileValidator.cs` / `PMProjectileCodec.cs` / 主计划 | 委派硬边界 |

`EmitSpawnReady` 的 `AuthorityNetId` 仍优先取整合器可信记录（对尚未升级的挂起项兜底），
但**所有 Confirmed 生成出口都已在升级之后**，因此出口冻结状态必带真实权威 ID（N 段断言）。

## 4. 测试覆盖（新增/改写）

| 段 | 内容 | 断言 |
|---|---|---|
| N（新） | 预留 → Confirm → 原地升级 → **CatchUp 真实推进**；ID/Origin 不变、权威 ID 非 0、幂等、只限 Confirmed、跨 epoch/0 权威/未登记/权威原生/已拒绝 | 32 |
| P（新） | hook false / NaN / 抛异常 / TryStop 异常 / NaN 停止点 ⇒ fail closed；合法回填 + snapshot 深 clone 证明 | 25 |
| Q（新） | 暂存期去重预留；TTL 联动连带作废（不结算、预留释放、同 key 不可重建）；129 组容量淘汰后释放预留并重建结算一次 | 38 |
| R（新） | StopOnHit 在 Pending 立即停（不二次停止通知）+ 对照组 | 12 |
| S（新） | 认证 owner 绑定（跨 owner / owner=0 / 洪泛 200 次）+ 未登记 ServerDirect 拒绝 + 已登记权威弹不变 | 12 |
| J（改写） | 溢出 = Faulted + 抛异常 + 既有项目不被覆盖 + 所有调用可见 Faulted + 升 epoch 恢复 | 40 |
| M6（改写） | 结算出口溢出 = Faulted，512 条已受理结算一条不少 | — |
| A/G（改正） | 确认后权威 ID 真实非 0（原「归 0」断言是把缺陷当口径） | — |
| Lifecycle L（新） | 原地升级要求/幂等/升级后可追赶；终态判决淘汰后不可复活 | 40 |

测试仍**全部使用真实现**（真 `PMProjectileLifecycle`/`Pending`/`History`/`Validator`，无替身），
期望值手算（如 10 m/s × 0.15 s = 1.5 m；墓碑 150ms；`HostMotionFaulted` 位置 Z 不进位）。

## 5. 未做 / 剩余问题（诚实口径）

| # | 项 | 说明 |
|---|---|---|
| R1 | **A3 API 破坏性变更需主侧确认** | `ReportHits` 新增认证 owner 入参（§3）。这是契约身份条款的必然结果，但会改 B/C 适配层签名；请在 B1/B2 冻结前裁决。 |
| R2 | 已有 ServerDirect 弹的「本 owner 伪造命中」残留 | §2.1 残留：建议 B1 codec 读端强制「上行 origin 只 ClientPredicted」把这条关死；A3 不依赖 codec，无法代劳。 |
| R3 | 「每次都要抽干出口」的运维约束 | Faulted 是硬失败：宿主必须每 tick `Drain*`（容量 512）。已用 `IsFaulted/FaultReason` 可观测，但它仍是**运行契约**（旧设计是静默降级）。 |
| R4 | C 组自身 TTL 分支不可达 | §2.6：联动顺序下 A 队列先到期。保留为防御网（同路径释放预留）。若主侧希望 C 组独立过期语义，需要显式把两条 TTL 解耦（契约变更）。 |
| R5 | 生命周期待取释放队列的故障守卫不可达 | §2.8：受 1024/类别上限约束，**未构造出可达用例**；守卫为防御网，未加针对性测试。 |
| R6 | A1 侧 `TryRegisterPredicted` 的 `NonMonotonicId` | 同一 `(owner,origin)` 的 ID 必须按到达顺序单调（旧报告 G5）。本组的「请求时预留」保证了解挂乱序不误拒，但**同一 stream 内 ID 到达顺序乱序**（7 先于 5）仍会被水位拒——属 A1 冻结语义，未擅自放宽。 |
| R7 | 骨骼精细校验 / 场景墙碰撞 | 后置登记项：本组只有直线内核 + 宿主 hook 接口；Unity 侧碰撞查询属 C 阶段（fail closed 语义已定）。 |
| R8 | 权威镜像 / 接管入口未接 | `TryApplyMirror`/`TryTakeoverPredicted`/`NotifyPredictedEnded` 在本文件**无调用点**：A3 冻结清单未包含它们（属 C 阶段宿主/复制接线）。注意 §1.1 升级后 `TryTakeoverPredicted` 对同 key 返回 `NoPredicted`（ID 保留项的「接管」由原地升级+激活决策承担），C 阶段接镜像时应按 `TryApplyMirror`（升级后为 `Applied`）语义接线。 |
| R9 | 实机 T45 | 仍为 `PENDING_USER`；本组不改变该状态；未启动 Unity。 |

## 6. 已检查范围

**完整读取的文档（按委派顺序）**：`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`
→ `Docs/plans/net-r5-projectile-contract.md`（全文）→ `Docs/plans/_r5_integration_report.md`（全文）
→ `Docs/plans/_r5_lifecycle_review.md`（全文）→ `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`。

**完整/必要段读取的实现**：`PMProjectileCoordinator.cs`（全文 2078 行，改动前）、
`PMProjectileLifecycle.cs`（全文 2059 行，改动前）、`PMProjectilePending.cs`（全文 1308 行）、
`PMProjectileContracts.cs`（全文 156 行）、`PMProjectileHistory.cs`（Record/TryResolve/容量段）、
`PMProjectileValidator.cs`（L0/L1/L2 预算段）、四个门禁/测试工程 `.csproj`（Include 面与编码约定）、
`Tools/PMProjectileIntegrationTest/Program.cs`（全文）、`Tools/PMProjectileLifecycleTest/Program.cs`（必要段）。

**写入的文件（仅此 4 个 .cs + 本报告）**：
`Client/Assets/Scripts/PMProjectile/PMProjectileCoordinator.cs`、
`Client/Assets/Scripts/PMProjectile/PMProjectileLifecycle.cs`、
`Tools/PMProjectileIntegrationTest/Program.cs`、
`Tools/PMProjectileLifecycleTest/Program.cs`、
`Docs/plans/_r5_coordinator_review.md`。

**未做**：未启动 Unity；未运行 SVN/git 写操作；未提交；未递归委派；未改主计划；
未改 `PMProjectilePending.cs` / `PMProjectileValidator.cs` / `PMProjectileCodec.cs` / 共享 `PMProjectileContracts.cs`；
未新增任何文件（除本报告）；未引入 UnityEngine 依赖（语言面门禁仍 0 警告 0 错误）。
