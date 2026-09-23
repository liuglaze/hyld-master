# R5-A1 独立对抗审查与修复报告（生命周期 / 假弹镜像接管 / 三类 Pending）

> 输入：`Docs/plans/_r5_lifecycle_report.md`（被审查对象）+ `Client/Assets/Scripts/PMProjectile/PMProjectileLifecycle.cs`
> + `PMProjectilePending.cs`（审查对象实现）+ `Docs/plans/net-r5-projectile-contract.md`（冻结契约）
> + `PMProjectileContracts.cs`（主侧冻结共享类型，只读）。
> 边界：只核**生命周期与 Pending 的实际实现**，不重新调查 UE / 旧业务；不改共享 Contracts、不改主计划。
> 结论：**9 项实测缺陷/未授权偏离已修**（其中 4 项为语义错误，5 项为边界/一致性缺陷），
> 新增 1 个 A3 必需的写回 API，断言从 294 → **470**（全部通过，exit 0）。

---

## 0. 验证命令与结果（本机实测）

```
dotnet build Tools/PMProjectileCoreCheck/PMProjectileCoreCheck.csproj   -c Release   # 0 警告 0 错误
dotnet build Tools/PMProjectileLifecycleTest/PMProjectileLifecycleTest.csproj -c Release # 0 警告 0 错误
dotnet Tools/PMProjectileLifecycleTest/bin/Release/net8.0/PMProjectileLifecycleTest.dll   # 470 项断言全部通过，退出码 0

-- A. 身份与登记                                                         通过 57 / 失败 0
-- B. 假弹镜像接管                                                       通过 69 / 失败 0
-- C. 停止与墓碑                                                         通过 41 / 失败 0
-- D. Pending A：挂起 Spawn                                              通过 40 / 失败 0
-- E. Pending B：Verify 先于 Spawn                                       通过 41 / 失败 0
-- F. Pending C：命中结论                                                通过 66 / 失败 0
-- G. 契约偏离修正（散弹键 / 总预算 1024 / 终态记忆与高水位）              通过 33 / 失败 0
-- H. 释放队列与撤销边界（不双消费 / 只撤真实假弹 / 登记被拒不撤）          通过 32 / 失败 0
-- I. 镜像先到后到 / 停止态保护 / finite / 身份与域                       通过 50 / 失败 0
-- J. 原始上报 vs 可信结论（两侧口径刻意不同）                             通过  6 / 失败 0
-- K. 运动推进 API（A3 宿主推进已登记弹；受保护字段不被覆盖）               通过 35 / 失败 0
```

首轮运行 2 项失败，**两项都是真缺陷**（不是测试期望写错）：
`D: source=0 -> Invalid（实际 Enqueued）` —— 实现缺 source==0 校验；
`F: 第 1 组应 Created，实际 Invalid` —— 暴露我自己测试载荷复用了错误 owner（修正测试后 F 由 43 → 66）。
文件编码保持 `.cs` = UTF-8 BOM + CRLF（三个文件均已核验：`lone_lf = 0`）。

写入边界：只改 `PMProjectileLifecycle.cs` / `PMProjectilePending.cs` / `Tools/PMProjectileLifecycleTest/Program.cs`
与本报告；`git status` 中其它已修改文件（`PMBattleContentBuild.cs`、`net-architecture-migration.md`、
`PMBattleContentBuildCheck.csproj`）的 mtime 均早于本次会话（14:25 / 14:49），非本次改动。
A2 的 `PMProjectileHistory.cs` / `PMProjectileValidator.cs`（mtime 15:20）为同目录另一组并发写入，未被我触碰；
两个门禁工程的 Include 面**逐文件给定**，不含 A2 文件，因此没有跨组编译耦合。

---

## 1. 已修正的实测缺陷（按严重度）

### 缺陷 1（真实语义错误，必须修）同一 activation / 同一生成源的散弹被 `DuplicateSource` 挡掉

- **证据**：原 `PMProjectilePendingSpawnKey = (OwnerNetId, SourceBehaviorInstanceId)`。
  一次散弹攻击（同一 source、同一 activation、20 个不同 `ProjectileId`）在 `TryEnqueue` 第二次起
  返回 `DuplicateSource`，**只剩 1 颗能入队**。这与契约「激活 ID + 一次激活多颗弹」、
  D-R0-30..40「接管一次性消费完整运动/停止/命中集合」直接冲突，而且失败是静默的（调用方只能看到 1 颗）。
- **修正**：唯一性改为**完整 `PMProjectileKey`(Epoch, OwnerNetId, ProjectileId, Origin)**；
  `SourceBehaviorInstanceId` 降级为载荷字段（诊断/分组），不参与唯一性。
  重复语义改为 `DuplicateKey`（只有同一完整 key 重复才算重复）。
  解挂仍是按 `ActivationId` 一次解挂该 activation 的**全部**弹（散弹一次全解）。
- **测试**：`G` 段 —— 同 source/activation 连续入队 20 颗全部 `Enqueued`；
  `ResolveByActivation(Confirmed)` 一次返回 20 条且 `Spawn.Key` 与载荷一一对应（无错位）；
  同 source 不同 activation 共存合法；只有同一完整 key 才 `DuplicateKey`。

### 缺陷 2（未授权偏离）挂起 Spawn 总预算 8192 无说明

- **证据**：`MaxPendingSpawnsTotal = MaxOwners × MaxPendingSpawnsPerOwner = 64×128 = 8192`；
  契约只写「权威/预测活对象各上限 1024」，8192 在契约里没有任何依据（原报告 §D1 也未说明）。
- **修正**：`MaxPendingSpawnsTotal = PMProjectileLimits.MaxProjectiles`（= **1024**），
  口径写成「挂起项与活对象共享同一份 1024 预算」；每 owner 的 128（契约明示）继续独立生效，两者取小。
- **测试**：`G` 段 —— `MaxPendingSpawnsTotal == MaxProjectiles`；
  8 owner × 128 恰好装 1024 条；第 1025 条 `TotalCapacity` 且 `RejectedByCapacityCount` 可观测。
- **需主侧确认的后果（诚实标注）**：口径收紧后，4 个 owner 各灌满 128 就会吃掉一半全局预算，
  8 个 owner 各灌满即用尽。若业务上「多玩家同时大量挂起」是常态，请主侧给出**显式**总量数字，
  我不擅自放大。

### 缺陷 3（真实语义错误）镜像可写入已停止的弹，并把 `Stopped`/`Hidden` 翻回去

- **证据**：原 `TryApplyMirror` 的 Applied 分支无条件执行
  `e.State.Position = mirrorState.Position; ...; e.State.Stopped = mirrorState.Stopped;`，
  只在之后用 `if (mirrorState.Stopped && !e.Stopped)` 打补丁。于是
  （a）停止后到达的镜像会把弹**再往前挪**（停止后继续漂移的幽灵轨迹）；
  （b）`mirrorState.Stopped=false` 会把 `State.Stopped` 从 true 改回 false，
  而账本标记 `e.Stopped` 仍是 true —— **同一事实两个真值**，下游读哪个都能得到相反结论。
- **修正**：新增分支 `StoppedNotMoved`：已停止时镜像只更新 `LastMirrorWallTimeMs`，
  不写 Position/PreviousPosition/Velocity/Yaw/MoveTimeMs，不翻动 `Stopped`/`Hidden`；
  Applied 分支只在未停止时执行，并保证 `State.Stopped == false` 与 `e.Stopped == false` 一致。
  理由（写在代码里）：预测假弹「已停止」即「已结束」，`TryTakeoverPredicted` 也会因 `PredictedNotAlive`
  拒绝接管 ⇒ 契约要求「不复活、不二次停止通知」；权威实例的停止本身就是权威观察结果，
  若要被推翻应以新的接管/停止语义显式表达，而不是靠位置写入隐式改状态。
  另：判定顺序调整为「先校验、后写入」，Invalid 镜像不再产生半写。
- **测试**：`C`（顺序一/顺序二）与 `I3` —— 停止后镜像返回 `StoppedNotMoved` + `PositionApplied=false`；
  位置仍是停止位置、速度仍为 0、`MoveTimeMs` 不被推高、`Stopped`/`Hidden`（含冻结状态）不被翻回；
  停止后 `TryAdvanceMotion` 也返回 `NotMovable`。
- **对 A3 的可见行为变更**：停止后到达的镜像由 `Applied` 变为 `StoppedNotMoved`（枚举新增，追加在末尾，不破坏既有取值）。

### 缺陷 4（真实语义错误）镜像带来的 `Stopped` 不建立墓碑窗口 ⇒ 合法迟到 Verify 被误判过期

- **证据**：原 Applied 分支遇到 `mirrorState.Stopped` 只写 `e.Stopped = true`，
  **不写** `StopWallTimeMs/TombstoneUntilMs`（保持 0）。随后 `AdmitVerify` 判 `tombstoned=true` 且
  `wallNowMs > 0` ⇒ 直接 `TombstoneExpired`，把条目清进终态标记环。
  于是「停止后墓碑窗口内仍应接受迟到 Verify」这条契约在有镜像参与的顺序下失效（窗口长度变成 0）。
- **修正**：镜像首次带来 `Stopped` 时按同一公式
  `ComputeTombstoneMs(Spec.DelayDestroyMs, 0)` 建立墓碑，并同步 `State.StopWallTimeMs/TimeAfterStoppedMs/TombstoneUntilMs`；
  若 `Spec.HideOnStop` 同时置 `Hidden`。镜像停止仍**不算**停止事件（事件只能来自 `TryRecordStop`）。
- **测试**：`I4` —— 镜像停止后 `TombstoneUntilMs = now + 300`（`delayDestroy=300`），
  `now+100` 的 `AdmitVerify` 返回 `AllowedInTombstone`，随后的 `TryRecordStop` 返回 `AlreadyStopped` 且不发事件。

### 缺陷 5（真实语义错误）命中结论暂存把「原始上报」当「可信结论」

- **证据**：原 `PMProjectilePendingHits.TryStash(key, PMProjectileHitBatch batch, ...)` 存的是
  `PMProjectileHitBatch` —— 契约里它是**原始上行**载荷（含未消毒的 `ImpactPoint` / `VisualOffset` /
  `PMProjectileHitCandidate` 候选），`TryResolve` 又把它当「结算数据」发出去。
  这等于把「Pending 期间绝不结算 / 结算的必须是 L0–L4 之后的结论」降级成命名约定。
  A2 的 `PMProjectileValidateResult` 文档已明确边界：「Pending 的 `Hits` 是**候选**：账本确认后应直接结算
  这批候选，**不得重跑 Validate**」，其载荷类型正是 `PMProjectileValidatedHit[]`。
- **修正**：`C` 类只接受**已消毒结论**容器 `PMProjectileValidatedHitSet{ Key, Hits[] }`
  （字段 = 冻结契约的 `PMProjectileValidatedHit`：TargetNetId / TargetStreamVersion / ImpactPoint / Resolution）。
  入口做廉价边界 sanity（不是 L0–L4）：身份一致、`Hits.Length <= 100`、每个 `TargetNetId != 0`、
  `ImpactPoint` finite；`TryResolve` 返回的也是该类型（`Sets`）。
  同时把 `B` 类（Verify 暂存）的定位写成「刻意保留未消毒原始上行」——非 finite 点不在 A1 静默丢弃，
  否则调用方永远看不到「整包 L0 Rejected」。
- **测试**：`J` 段刻意对比两侧 —— B 侧 NaN 命中点 `Stashed` 且回放原样带出未消毒坐标；
  C 侧 NaN 命中点 / `TargetNetId=0` / 身份不符 / 101 目标一律 `Invalid`。
  `F` 段 `Resolution` 透传（退化信息不丢）。

### 缺陷 6（一致性缺陷）目标集合两处真值（List 与 `State.HitTargets/AllowedTargets` 数组）

- **证据**：`TryAddHitTarget` / `TrySetAllowedTargets` 只写 `Entry` 的两份 `List`，
  而 `TryGetRegistration`/`TryGetFrozen` 直接 `State.Clone()` —— 于是**登记后**任何一次新增命中，
  冻结状态里的 `State.HitTargets` 立刻变陈旧，读哪一份是「碰巧」。
  这正是 A3 要「保留 HitTargets/AllowedTargets 不可被来路覆盖」时最容易被坑的地方。
- **修正**：唯一真值是两份 `List`；新增 `Entry.StateCopy()` 统一从 List 派生数组，
  `Snapshot()` 与 `TryGetFrozen()` 都走它；登记时用入参数组**播种** List（迟到登记不丢已有命中）。
- **测试**：`I5` 末尾 —— `TryAddHitTarget(61)` / `TrySetAllowedTargets({62})` 后快照的
  `State.HitTargets[0]==61`、`State.AllowedTargets[0]==62`。

### 缺陷 7（一致性缺陷）已结束假弹的冻结状态与账本标志矛盾；接管未同步 `State.TakenOver`

- **证据**：(a) `NotifyPredictedEnded` 写了 `PredictedEnded=true` 与墓碑，但 `e.Stopped` 仍为 false、
  `State.Stopped` 也为 false —— 「假弹已结束」在状态面上看不出来，后续 `TryRecordStop` 还能发出**第二次停止事件**；
  (b) `TryTakeoverPredicted` 置 `e.TakenOver=true` 但不置 `e.State.TakenOver`，冻结状态滞后。
- **修正**：(a) `NotifyPredictedEnded` 同时置 `e.Stopped=true` 并同步 `State.Stopped/StopWallTimeMs/TimeAfterStoppedMs/TombstoneUntilMs`
  ⇒ 之后的停止只能是 `AlreadyStopped`（无二次事件）；(b) 接管同时置 `State.TakenOver`。
- **测试**：`B`（`已结束 -> 接管不成立` / `迟到镜像 HiddenNoRevive` / `重复迟到镜像仍不复活`）与 `K`
  （结束后 `NotMovable`）保持通过；`C` 顺序二证明两种到达顺序都只产生一次停止事件。

### 缺陷 8（资源缺陷）「ID 永不复用」的记忆（高水位）自身无界

- **证据**：`_watermarks` 按 `(OwnerNetId, Origin)` 累加且**只**在 `AdvanceEpoch` 清理；
  `Retire`/`ClearOwner` 会增加 owner churn（`_ownerCounts` 归零后 owner 可再进来），
  于是一个长会话里不同 owner ID 会无界累积水位条目 —— 与契约「owner 状态最多 64」口径不一致。
- **修正**：`(owner, origin)` 流数上限 = `MaxOwners × 2`（两种 origin），超过则拒新流（`OwnerCapacity`）。
  已记录流的更高 ID 仍可登记 ⇒ 不破坏「断开不清水位、同 ID 不复用」。
- **测试**：`G4` —— 64 owner × 2 origin = 128 条流全部登记并逐个断开（`ClearOwner` 各返回 2）；
  此后新 owner 的流被拒（此时 `_ownerCounts=0`，只能是流数上限挡的）；已记录流的更高 ID 仍 `Registered`、同 ID 重放 `DuplicateId`。

### 缺陷 9（可观测性缺陷）账本终态淘汰静默；`ReleaseWaiters` 预分配数组会留 `default` 洞

- **证据**：(a) `EvictOldestTerminalLedgerEntry` 淘汰终态账本条目但不计数，退化不可观测；
  (b) `ReleaseWaiters` 预分配 `releases[snapshot.Length]` 后按下标写，`_entries` 缺 key 时 `continue`
  会留下全 0 的 `default` 释放项，调用方无法区分「真的没事」与「这条是洞」。
- **修正**：(a) 新增可观测计数 `EvictedTerminalLedgerCount`，并**拒绝淘汰仍有等待者的终态条目**；
  (b) 改为 `List<...>` + `Add` 后 `ToArray()`（无洞）；Rejected 路径的 `RemovedInstance` 直接取 `Retire` 的判定结果。
- **测试**：`B`（账本 512 上限淘汰最旧终态仍可写）与 `H1`（out releases 两条、无 default 洞）。

---

## 2. 主侧点名要求项的逐条结论（含「不成立」的项）

| # | 主侧要求 | 结论与证据 |
|---|---|---|
| 1 | 同一 activation/source 可出多颗散弹，不得 `DuplicateSource` 挡不同 `ProjectileId`；pending key 按完整 ProjectileKey 唯一 | **成立（缺陷 1，已修）**。`G` 段 20 颗散弹全入队、一次解挂 20 条 |
| 2 | 总 1024 登记/挂起共同预算，不得擅自 8192 | **成立（缺陷 2，已修）**。`MaxPendingSpawnsTotal = 1024`，`G2` 断言 |
| 3 | 终态记忆淘汰后不得被迟到镜像/激活复活，水位策略须有覆盖 | **成立（已覆盖）**。`G3`：`Retire` 后迟到镜像/Verify/停止全 `Retired`；迟到 `Confirmed` 的 `Releases.Length==0`（未复活）；把 `MaxRetiredMarkers+10` 条弹挤过标记环后，迟到镜像 `UnknownKey`、同 key 复用 `NonMonotonicId`（靠水位而非标记环）。**唯一残留**见 §4 U1 |
| 4 | `TryApplyMirror` 先到/后到 | **成立**。`I1`：未登记 `UnknownKey`；追赶前 `Applied` 且不消耗追赶资格（`TryBeginCatchUp` 仍 `Started`）。先到=追赶前、后到=接管后/停止后（`B`/`C`/`I3`） |
| 5 | 停止态保护 | **成立（缺陷 3/4/7，已修+已测）**。`C`/`I3`/`I4` |
| 6 | spec/state 输入 finite 与 identity 一致 | **成立（已补）**。`I5`：NaN/Inf 的 Position/Velocity/Yaw/MoveTimeMs/Speed/Radius → `InvalidPayload`；`state.Key` 指向另一颗弹 → `InvalidPayload`；镜像 NaN/身份不符/null → `Invalid`（且不半写）；挂起载荷非 finite / 身份不符 → `Invalid` |
| 7 | client/server 域 | **成立（已补）**。`I5`：预测登记时入参自称 `AuthorityNetId=999` 被归 0、`ActivationId` 按参数盖章、入参 `Stopped/Hidden/TakenOver=true` 被归一（登记=新实例，不与账本标志矛盾）；权威登记按 `authorityNetId` 参数盖章。原实现只跑「`ActivationId==0` 仅 ServerDirect」这一条，其余域字段入参可污染冻结状态 |
| 8 | 墙钟倒退 | **成立（原实现正确，已覆盖）**。`Validate` 对 NaN/±Inf/倒退显式拒绝，相等合法；登记/追赶/镜像/停止/Verify/三类 Pending 各自都有倒退与 NaN 用例（`A`/`D`/`E`/`F`） |
| 9 | 释放队列是否双消费（`TrySetActivationResult` 的 out releases 同时又 Drain） | **不成立（原实现没有双消费，但缺保障性测试）**。`ReleaseWaiters` 走 out 独占返回，**不入** `_pendingReleases`；只有 `AdmitVerify`-超窗 / `PurgeExpired` / `ClearOwner` 入队。`H1` 断言：Rejected 的 2 条 out releases 后 `DrainPendingReleases == 0`；断连入队的 1 条只能取到 1 次（第二次 0）。原报告 §D10「零回调 + 独占返回」这条已由测试固化 |
| 10 | 拒绝移除真实假弹 | **成立（已修+已测）**。`RemovedInstance = Predicted && !TakenOver`：活跃假弹 true（`H2` 第 1 条）、**已接管**的弹 false（`H2` 第 2 条，本地实例已被权威镜像取代，不得误删）、权威实例 false（`H2` 第 3 条，`DrainPendingReleases==0`）；**登记被拒**（`RevokedRejected`）原实现错误地报 `RemovedInstance=true`，已改为 false（`H3`：登记没落地就没有实例可撤销） |
| 11 | PendingHit 存原始上报还是消毒结论不能混淆 | **成立（缺陷 5，已修）**。C 类只收 `PMProjectileValidatedHitSet`（消毒结论）；B 类刻意留原始上行。`J` 段两侧对比断言 |
| 12 | A3 运动状态更新 API | **已补（见 §3）**。`TryAdvanceMotion` |

---

## 3. 新增/变更 API 清单（A3 集成口径）

### 3.1 新增：`PMProjectileLifecycle.TryAdvanceMotion`（A3 宿主 / 共享轨迹模型唯一写回入口）

```csharp
public enum PMProjectileMotionResult : byte
{
    Advanced = 0,      // 写入生效
    NotMovable = 1,    // 已停止 / 假弹已结束：运动是终态，拒绝推进（回显当前位置防表现层误拉）
    UnknownKey = 2,
    StaleEpoch = 3,
    Retired = 4,
    Invalid = 5,       // 入参非 finite
}

public struct PMProjectileMotionOutcome
{
    public PMProjectileMotionResult Result;
    public PMProjectileKey Key;
    public PMVector3 Position;    // 生效后位置；NotMovable 时为当前冻结位置
    public PMVector3 Velocity;
    public double MoveTimeMs;
    public bool IsAdvanced { get; }   // == (Result == Advanced)
}

public PMProjectileMotionOutcome TryAdvanceMotion(
    PMProjectileKey key,
    PMVector3 position,
    PMVector3 previousPosition,
    PMVector3 velocity,
    float yaw,
    double moveTimeMs);
```

- **只写运动面**：`PreviousPosition / Position / Velocity / Yaw / MoveTimeMs`。
- **一律不动（不可被来路覆盖）**：`Key / ActivationId / Activation / AuthorityNetId / HitTargets /
  AllowedTargets / Stopped / Hidden / Predicted / PredictedAlive / PredictedEnded / TakenOver /
  CatchUpStarted / RegisteredWallTimeMs / StopWallTimeMs / TimeAfterStoppedMs / TombstoneUntilMs /
  SpawnPosition / Spec`。
- **无墙钟参数**：运动推进用模型自己的 `MoveTimeMs`，与 TTL/墓碑的墙钟**不混用**（不跨基相减）。
- **`MoveTimeMs` 不要求单调**：共享轨迹模型重放早于当前的时刻是合法用法；新鲜度由调用方结合
  `TotalSimTimeMs` 帧锚判定。
- **不引回调**：写入发生在返回之前，返回值只是回显。
- **测试**：`K` 段 35 项断言（权威弹推进 + 受保护字段 + 预测假弹推进 + 结束后 `NotMovable` +
  未知/跨 epoch/已回收/非 finite + 推进不破坏激活账本）。

### 3.2 新增：`PMProjectileMirrorResult.StoppedNotMoved = 8`（追加，不破坏既有取值）

已停止的弹收到镜像时的结果；`PositionApplied=false`、`HideWithoutStopEvent=false`。
配合 `PositionApplied` 使用：A3 只要看到 `PositionApplied==false` 就保持视觉位置不动。

### 3.3 新增：`PMProjectileValidatedHitSet`（C 类可信结论容器，A1 自持，**不**依赖 A2）

```csharp
public sealed class PMProjectileValidatedHitSet
{
    public PMProjectileKey Key;
    public PMProjectileValidatedHit[] Hits = new PMProjectileValidatedHit[0];
    public PMProjectileValidatedHitSet Clone();
}
```

A3 接线（一行投影，避免 A1 依赖 A2 的 `PMProjectileValidator`）：

```csharp
PMProjectileValidateResult v = PMProjectileValidator.Validate(req);   // A2
if (v.Status == PMProjectileValidateStatus.Pending)
{
    pendingHits.TryStash(key, new PMProjectileValidatedHitSet { Key = key, Hits = v.Hits }, wallNowMs);
}
```

### 3.4 新增：`PMProjectileLifecycle.EvictedTerminalLedgerCount`（可观测）

账本元数满时淘汰掉的**终态**条目数（累计）。语义：账本只 512，淘汰后该 activation 的终态判决不再可查；
**key 复活仍被高水位拦住**；新弹在同一 activation 上会被当成 Pending 而不是 Confirmed/Rejected。

### 3.5 变更（**需主侧知悉的 API 变化**）

| 变更 | 旧 | 新 | 理由 |
|---|---|---|---|
| 挂起 Spawn 键 | `PMProjectilePendingSpawnKey(Owner, Source)` | **删除该类型**，统一用 `PMProjectileKey` | 缺陷 1：源不参与唯一性 |
| `TryEnqueue` | `(owner, source, activationId, projectileId, authorityNetId, predictionMs, request, wall)` | `(PMProjectileKey key, sourceBehaviorInstanceId, activationId, authorityNetId, predictionMs, request, wall)` | 同上；key 含 epoch/origin，跨 epoch 可判 `StaleEpoch` |
| `PMProjectilePendingSpawn` | 有 `Key`(旧类型) + `ProjectileId` | `Key`(PMProjectileKey) + `SourceBehaviorInstanceId`（`ProjectileId` 并入 Key） | 同上 |
| 枚举成员 | `PMProjectilePendingSpawnResult.DuplicateSource` | `DuplicateKey`（取值仍 1） | 语义改名 |
| C 类载荷 | `PMProjectileHitBatch`（原始上行） | `PMProjectileValidatedHitSet`（消毒结论） | 缺陷 5 |
| C 类结果字段 | `SettledBatchCount / SettledCandidateCount / Batches` | `SettledSetCount / SettledHitCount / Sets` | 同上 |
| C 类组信息 | `BatchCount / CandidateCount`、`PendingHitCount()` 原名 `CandidateCount()` | `SetCount / HitCount`、`PendingHitCount()` | 同上 |
| C 类上限常量 | `PMProjectilePendingLimits.MaxHitBatchesPerKey` | `MaxHitSetsPerKey` | 同上 |
| 新增枚举成员 | — | `PMProjectileHitStashResult.AlreadyResolved = 7` | 终态后不得再攒（缺陷 5 衍生） |
| `TryStash` 参数 | `(key, PMProjectileHitBatch)` | `(key, PMProjectileValidatedHitSet)` | 同上 |
| 悬空校验新增 | — | `activationId==0` / `sourceBehaviorInstanceId==0` → `Invalid` | 等待激活的挂起项配 0 永远解不开 |
| 新增校验 | — | 登记/镜像/停止/推进/挂起载荷的 finite + key 身份一致 | 主侧要求项 6 |
| 新可观测计数 | — | `EvictedTerminalLedgerCount` | 缺陷 9 |
| 行为变更 | 停止后镜像 `Applied` | `StoppedNotMoved` | 缺陷 3 |

`PMProjectilePendingVerifies`（B 类）除文档口径外**签名未变**（它本来就该存原始上行）。

---

## 4. 未验项 / 交接（诚实口径）

| # | 项 | 说明 |
|---|---|---|
| U1 | 账本**终态判决**被淘汰后的语义 | 高水位只保证 **key** 不复活；若 `(owner, activationId)` 的终态判决条目被 512 元数挤出，此后同一 activation 上的**新**弹会被当成 `Pending`（等一个可能永不再来的裁决）。这是有界内存的必然代价，已用 `EvictedTerminalLedgerCount` 暴露。若主侧要求「终态判决也必须全量记忆」，需要显式给出容量或接受无界 —— 未擅自放大 |
| U2 | 挂起 Spawn 总预算收紧后的业务上限 | 见缺陷 2「需主侧确认的后果」 |
| U3 | `ClearOwner` 不清该 owner 的账本条目 | 断线重连后若 `(owner, activationId)` 复用，旧会话的终态判决会作用于新会话。契约未规定，未擅自改；请主侧在 B/C 阶段确认 owner 号段是否随重连变化 |
| U4 | `TryRecordStop` 用于预测假弹的语义 | 文档流程是 `NotifyPredictedEnded`（早结束）→ `HiddenNoRevive`。若宿主误用 `TryRecordStop` 停预测弹，现行为是「停止即终态、镜像 `StoppedNotMoved`」。已在代码注释中写明，请 A3 按文档选 API |
| U5 | 与 A2 的 `PMProjectileValidatedHitSet` 映射 | A1 只认冻结契约的 `PMProjectileValidatedHit`；A2 的 `PMProjectileValidateResult.Hits` 到它是逐字段同构投影（§3.3）。未做跨组编译验证（A1 门禁刻意不含 A2 文件） |
| U6 | 真网络 / Unity 宿主 / L0-L4 几何 | 同原报告：B/C 阶段与 A2，`T45 PENDING_USER`；本组不声称完成 |

---

## 5. 已检查范围

**完整读取的文档（按委派顺序）**：`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`
→ `Docs/plans/net-r5-projectile-contract.md`（全文）→ `Client/Assets/Scripts/PMProjectile/PMProjectileContracts.cs`（全文）
→ `Docs/plans/_r5_lifecycle_report.md`（全文）→ `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`。

**完整读取的实现**：`PMProjectileLifecycle.cs`（1726 行，改动前全文）、`PMProjectilePending.cs`（改动前全文）、
`Tools/PMProjectileLifecycleTest/Program.cs`（977 行，改动前全文）、两个门禁工程的 `.csproj`。

**只读旁证**：`PMProjectileValidator.cs` 的公开类型声明与 `PMProjectileValidateResult` 文档注释
（用于确认「候选 Hits = 消毒结论」这一 A1/A2 边界，并做重名检查：A2 拥有
`PMProjectileValidateStatus/RejectReason/SkipReason/SkippedTarget/ValidateRequest/ValidateResult/Validator`
与 `PMProjectileHistory`，与本次新增的 `PMProjectileFinite / PMProjectileMotionResult / PMProjectileMotionOutcome /
PMProjectileValidatedHitSet` **无重名**）。

**未做**：未启动 Unity；未改共享契约 / 主计划 / A2 文件；未提交（git/svn 均无写入）；
未递归委派；未做跨组通配 Include。
