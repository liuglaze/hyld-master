# R5-A3 真实核心整合报告（PMProjectileCoordinator）

> 任务类型 **comprehensive**（A3 子组：消费已落地两组真实库，补足联动，不重写第二套核心）
> 只读输入（按委派顺序）：`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`
> → `Docs/plans/net-r5-projectile-contract.md`（全文，取「A3/B1集成冻结」为最新）
> → `Client/Assets/Scripts/PMProjectile/PMProjectileContracts.cs`（全文，冻结只读）
> → `Docs/plans/_r5_lifecycle_review.md`（API 段 / 剩余风险）
> → `Docs/plans/_r5_validation_report.md`（API 段）
> → `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`。
> 生产代码只读读取：`PMProjectileLifecycle.cs`（全文）、`PMProjectilePending.cs`（全文）、
> `PMProjectileHistory.cs`（Record/TryResolve 全段）、`PMProjectileValidator.cs`（L0–L4 全段）。
> **未重查 UE / 旧大计划**；未改 A1/A2 任何文件；未引用或等待 B1 的 `PMProjectileCodec`（并行中）。

---

## 1. 写入边界与产物

| 文件 | 状态 | 说明 |
|---|---|---|
| `Client/Assets/Scripts/PMProjectile/PMProjectileCoordinator.cs` | **新建**（2079 行） | A3 整合器（唯一新增实现） |
| `Client/Assets/Scripts/PMProjectile/PMProjectileCoordinator.cs.meta` | **新建** | GUID `44481708196c4ccbb8a6378ecfbbbf59`（已全仓 6845 个 meta 去重校验） |
| `Tools/PMProjectileIntegrationCheck/PMProjectileIntegrationCheck.csproj` | **新建** | netstandard2.0 + C#7.3 零 Unity 编译门（逐文件 Include，**不 glob** B1 codec） |
| `Tools/PMProjectileIntegrationTest/PMProjectileIntegrationTest.csproj` | **新建** | net8.0 真实运行测试工程 |
| `Tools/PMProjectileIntegrationTest/Program.cs` | **新建**（1217 行） | 294 项断言（T5A6 的 A3 面） |
| `Docs/plans/_r5_integration_report.md` | **新建** | 本报告 |

编码：新 `.cs` = UTF-8 **BOM + CRLF**（`loneLF=0` 已实测）；`.cs.meta` = **LF 无 BOM**（与同目录既有 meta 一致）；
`.csproj` = **LF 无 BOM**（与既有 A1/A2 门禁工程一致）；报告 `.md` = BOM + CRLF（与同目录既有报告一致）。

`git status` 中另外三个已修改文件（`Client/Assets/Editor/PMBattleContentBuild.cs` 14:25、
`Docs/plans/net-architecture-migration.md` 14:49、`Tools/PMBattleContentBuildCheck/PMBattleContentBuildCheck.csproj` 11:34）
的 mtime 均早于本次会话，**非本组改动**；`Tools/PMProjectileCodec{Check,Test}/` 为并行 B1 组产物，本组未触碰。
`Tools/**/bin|obj` 为构建产物，已被 `.gitignore:78` 覆盖。**未提交（git/svn 均无写入）、未递归委派、未启动 Unity。**

---

## 2. 验收命令与结果（本机实测）

```bat
dotnet build Tools/PMProjectileIntegrationCheck/PMProjectileIntegrationCheck.csproj -c Release  REM 0 警告 / 0 错误
dotnet build Tools/PMProjectileIntegrationTest/PMProjectileIntegrationTest.csproj  -c Release  REM 0 警告 / 0 错误
dotnet Tools/PMProjectileIntegrationTest/bin/Release/net8.0/PMProjectileIntegrationTest.dll     REM 294 项断言全通过，退出码 0
```

| 命令 | 结果 |
|---|---|
| `PMProjectileIntegrationCheck` 构建（netstandard2.0 + C#7.3，零 UnityEngine） | **0 警告 / 0 错误** |
| `PMProjectileIntegrationTest` 构建（net8.0） | **0 警告 / 0 错误** |
| `PMProjectileIntegrationTest` 运行 | **通过 294 / 失败 0，退出码 0** |

回归（只读复核，证明本组没有把已落地两组搞坏）：

| 命令 | 结果 |
|---|---|
| `dotnet build Tools/PMProjectileCoreCheck`（A1 门禁） | 0 / 0 |
| `dotnet Tools/PMProjectileLifecycleTest/.../PMProjectileLifecycleTest.dll`（A1） | **470 / 0，退出码 0** |
| `dotnet build Tools/PMProjectileValidationCheck`（A2 门禁） | 0 / 0 |
| `dotnet Tools/PMProjectileValidationTest/.../PMProjectileValidationTest.dll`（A2） | **264 / 0，退出码 0** |
| `python Tools/check_cs_braces.py <本组两个 .cs>` | BALANCED，深度全程非负且末尾归零，PASS |

---

## 3. 公开 API（交付 B/C/D 与网络层）

命名空间 `PMNet.Projectile`。构造：

```csharp
public PMProjectileCoordinator(uint epoch,
    IPMProjectileTargetHistory history,   // A2 真实历史（null ⇒ 所有目标 HistoryUnavailable，不结算）
    IPMProjectileHitFilter filter,        // 权威 filter（null ⇒ FilterUnavailable fail-closed）
    IPMProjectileHostMotion hostMotion);  // 可选宿主运动/停止 hook（null ⇒ 直线核心）
```

只读面：`Epoch / Lifecycle / PendingSpawns / PendingVerifies / PendingHits`、
`TryObserveFrozen(key,out spec,out state)`（A1 深 clone 观察，改返回值不影响账本）、
`TryObserveRegistration`、`VerifyUsedCount(key)`、`IsSpawnPending(key)`、`IsSpawned(key)`。

### 3.1 登记入口

```csharp
public PMProjectileAdmissionOutcome RequestSpawn(
    PMProjectileSpawnRequest request, uint authenticatedOwnerNetId, uint authorityNetId,
    uint sourceBehaviorInstanceId, PMVector3 ownerPosition, PMProjectileSpec trustedSpec,
    uint[] trustedAllowedTargets, double wallNowMs);

public PMProjectileAdmissionOutcome ServerDirectSpawn(   // 可信 ServerDirect 独立入口
    PMProjectileState state, uint authorityNetId, PMProjectileSpec trustedSpec,
    uint[] trustedAllowedTargets, int predictionMs, double wallNowMs);
```

`PMProjectileAdmissionOutcome`：`Result / Key / ActivationId / Activation / SpawnPending / Spawned /
Register(A1 精确原因) / PendingSpawnResult(A1 精确原因) / Clock`。
`PMProjectileAdmissionResult`：`Admitted | AlreadyAdmitted | UntrustedOwner | InvalidAuthority | StaleEpoch |
InvalidRequest | InvalidDirection | InvalidPosition | MuzzleTooFar | InvalidActivationId | InvalidSpec |
RevokedActivation | IdNotReserved | Capacity | InvalidClock`。

### 3.2 激活裁决 / 命中上报 / 运动

```csharp
public PMProjectileActivationApplyOutcome ResolveActivation(uint ownerNetId, uint activationId,
    PMActivationResult outcome, double wallNowMs);
public PMProjectileHitReportOutcome ReportHits(PMProjectileHitBatch batch, double wallNowMs);
public PMProjectileAdvanceOutcome AdvanceMotion(PMProjectileKey key, int deltaMs, double wallNowMs);
public PMProjectileAdvanceOutcome CatchUpMotion(PMProjectileKey key, int totalMs, double wallNowMs);
public PMProjectilePurgeOutcome PurgeExpired(double wallNowMs);
public PMProjectileOwnerClearOutcome ClearOwner(uint ownerNetId, double wallNowMs);
public int ResetState(double wallNowMs);
public bool AdvanceEpoch(uint newEpoch);
```

### 3.3 对外出口（有界、FIFO、独占数组；无外部 callback 回入）

```csharp
public int DrainDecisions(int max, out PMProjectileActivationRelease[] items); // 激活结论（含 RemovedInstance）
public int DrainSpawnReady(int max, out PMProjectileSpawnEvent[] items);       // 可复制的权威 spawn
public int DrainStopped(int max, out PMProjectileStopEvent[] items);           // 停止通知（含墓碑时刻）
public int DrainSettlements(int max, out PMProjectileSettlement[] items);      // R6 的**唯一**结算接收点
```

`PMProjectileSettlement`：`Key / OwnerNetId / ActivationId / AuthorityNetId / Origin / WallTimeMs /
StopOnHit / HitCount / Hits(PMProjectileValidatedHit[])`。**本文件没有任何扣血出口。**

有界性与可观测量：`PMProjectileCoordinatorLimits`（`MaxCatchUpStepMs=50`、`MaxCatchUpBudgetMs=1000`、
`MaxDecisions/MaxSpawnEvents/MaxStopEvents/MaxSettlements=512`、`MaxKeyRecords=4096`、`MaxCatchUpSteps=64`）；
属性 `DecisionCount/SpawnEventCount/StopEventCount/SettlementCount`、
`Dropped{Decision,SpawnEvent,StopEvent,Settlement}Count`、`EvictedKeyRecordCount`、`ClampedCatchUpCount`、
`RejectedAdmissionCount`、`StashedVerifyCount`、`StashCapacityRejectionCount`、`RejectedStaleTargetCount`、`QuotaRejectionCount`。

### 3.4 宿主运动 / 停止 hook

```csharp
public interface IPMProjectileHostMotion
{
    bool TryStep(PMProjectileKey key, PMProjectileSpec spec, PMProjectileState snapshot, int deltaMs,
        PMVector3 straightLinePosition, PMVector3 straightLineVelocity, float straightLineYaw,
        out PMVector3 position, out PMVector3 velocity, out float yaw);
    bool TryStop(PMProjectileKey key, PMProjectileSpec spec, PMProjectileState snapshot,
        double wallNowMs, out PMVector3 stopPosition);
}
```

**契约（写在接口文档注释里）**：纯函数、不得回调整合器/生命周期/队列、**不得决定伤害**；
返回非 finite 时按直线核心结果处理（不把 NaN 写进冻结状态）；hook 抛异常同样退回直线核心。

---

## 4. 流程（契约条款 → 实现落点）

| 契约（A3/B1集成冻结） | 实现 |
|---|---|
| 可信 owner/muzzle admission | `RequestSpawn`：`authenticatedOwnerNetId` 必须等于 `request.State.Key.OwnerNetId`（跨 owner 注入 `UntrustedOwner`）；`MuzzleTooFar = \|SpawnPosition − ownerPosition\| > PMProjectileLimits.MuzzleToleranceM(10m)` |
| 不能信 request.State 的 Owner/权威 ID/命中集合/停止状态 | `SanitizeState` 重建：只保留 `SpawnPosition`（枪口）/ 归一化后的 velocity 方向 / `Yaw`；`AuthorityNetId` 从可信入参盖章；`HitTargets/AllowedTargets` 清空（白名单只从 `trustedAllowedTargets` 写）；`Stopped/Hidden/TakenOver/StopWallTimeMs/TimeAfterStoppedMs/TombstoneUntilMs/MoveTimeMs` 一律归零 |
| client 请求 spec 不得当权威配置 | `request.Spec` **整份丢弃**，一律用 `trustedSpec.Clone()`（测试断言客户端伪造的 `SpeedMps=9999 / RadiusM=999 / LifetimeMs=999999` 全部被替换为可信值） |
| 速度由 trustedSpec 决定 | `velocity = PMVector3.Normalized(request.State.Velocity) * trustedSpec.SpeedMps`；方向为 0/非 finite → `InvalidDirection` |
| 可信 ServerDirect 走独立入口 | `ServerDirectSpawn` 只接受 `Origin=ServerDirect` 的 state，且是**唯一**允许 `ActivationId=0` 的入口（预测路径 `ActivationId==0` → `InvalidActivationId`）；ServerDirect 的 L0 按构造 Confirmed ⇒ 永不进 C 队列、不自行结算 |
| epoch 一致 | 所有入口先 `key.Epoch != _epoch → StaleEpoch`，再委托 A1（A1 自身也按 epoch 判） |
| 同 activation 多弹 + **请求时预留 ID** | admission 立即 `PMProjectileLifecycle.TryRegisterPredicted`（含 Pending activation 的等待登记）⇒ ID/水位在**收请求时**就被保留；解挂阶段只 `TrySetActivationResult` + `PendingSpawns.ResolveByActivation`，**绝不二次注册**。测试 `E` 20 颗散弹一次解挂；测试 `C`/`D` 证明「不同 activation 的弹乱序解挂」不会被 ID 高水位误拒 |
| 乱序解挂不得按 ID 高水位拒已受理项 | 同上：解挂路径无注册调用；`RevokedRejected`/失败时只 `Retire` 回滚 |
| 三队列 Verify-before-Spawn 联动 + 超时清理 | A=`PMProjectilePendingSpawns`（activation 未定 ⇒ 挂起 payload、不发 SpawnReady）；B=`PMProjectilePendingVerifies`（**该 key 连登记都还没有** ⇒ 原样暂存未消毒上行，生成后 FIFO 回放）；C=`PMProjectilePendingHits`（已登记但 activation 仍 Pending ⇒ 只收已消毒结论）。`PurgeExpired` 一次联动三队列 TTL + A1 墓碑 + 待取释放项 → 决策出口 |
| lifecycle 注册先于运动追赶 | admission 内部先注册再入队；`CatchUpMotion` 先 `TryBeginCatchUp`（未登记 → `NotRegistered`、预测弹 → `NotAuthority`）再逐步推进 |
| 追赶 ≤50ms 步 + 总预算，不能恶意巨大 loop | `AdvanceMotion` 单步 `0..50ms`（越界 `Invalid`）；`CatchUpMotion` 把 `totalMs` 钳到 `MaxCatchUpBudgetMs=1000`（`Clamped=true` + `ClampedCatchUpCount++`），循环另有 `MaxCatchUpSteps=64` 硬上限 |
| 普通 Advance 由统一 wallNow 支持有限步、Lifetime 停弹与墓碑 | `AdvanceMotion(key, deltaMs, wallNowMs)`；`MoveTimeMs >= spec.LifetimeMs` 时 `TryRecordStop` + 墓碑公式 `PMProjectileTombstone.ComputeTombstoneMs(DelayDestroyMs, PredictionMs)` + Stopped 出口；非 finite/倒退墙钟 `InvalidClock` |
| Validate 后目标去重 / StopOnHit / Pending 结论唯一消费 | `SettleHits`：逐目标（1）最终有效性复核（2）`TryAddHitTarget` 去重提交（3）才 `PushSettlement`；`spec.StopOnHit && !reg.Stopped` → `TryRecordStop` + Stopped 出口；C 队列 `TryResolve` 只在 `ConfirmedOnce` 时把 `Sets` 交出去 |
| **结果去重先提交再排队** | 提交 `TryAddHitTarget` 严格在 `PushSettlement` 之前；测试 `I` 证明第二次同弹同目标不结算、Drain 第二次为空 |
| Pending 只收 sanitized hit set | 进 C 的类型是 `PMProjectileValidatedHitSet`（A2 `PMProjectileValidator` 的 `Hits` 投影），原始 `PMProjectileHitBatch` 构造上无法进入 C |
| Confirmed 才对外 DrainSettlements | `IsSettleable` 语义等价：只有 `Status==Confirmed && Hits.Length>0` 且逐个通过最终复核才产出 settlement；Pending/Rejected/TTL 全部 0 出口 |
| Reject 实际移除 | `TrySetActivationResult(Rejected)` 的释放项 `RemovedInstance = Predicted && !TakenOver`（A1 判定），经 `DrainDecisions` 交出去；同时丢弃 B/C 暂存 |
| 最终结算前确认目标 epoch/stream/alive 仍有效 | `IsTargetStillSettleable`：用真实 A2 历史 `TryResolve(epoch, {netId, streamVersion}, worldNow, 0, 0)` 复核 stream 版本与 `Alive`；**不重跑 L0–L4 几何**（因此不再消耗配额、不因时间推进改判）；不通过则 `RejectedStaleTargetCount++` 且不进结算 |
| 纯核心不直接扣血，Settlement 让 R6 唯一接收 | `PMProjectileSettlement` 只带 key/activation/authority/已消毒 Hits；本文件无 HP/伤害字段与出口 |
| 全部对外队列有界、溢出显式失败 | 四个出口队列容量 512，溢出丢最旧 + `Dropped*Count`；B 队列容量拒绝返回 `Capacity` 并计数（测试 `M` 断言 6/key 与总 128 的边界与计数） |
| 无外部 callback 回入 | 全部 API 都是「先提交状态、再独占返回数组」；无任何注册回调；hook 被限定为纯函数（§3.4） |
| 断开 Owner / Reset 清暂存与记录但不让旧 ID 复活 | `ClearOwner`：清 A（`DiscardOwner`）+ B/C（按自有记录逐 key `Discard`/`TryResolve(Rejected)`）+ A1 `ClearOwner`（保留 ID 水位）→ 之后同 ID 复用返回 `IdNotReserved`；`ResetState`：退全部记录 + 清三队列，保留水位；`AdvanceEpoch`：五层整体清理 + 旧 epoch 一律 `StaleEpoch` |
| authority spec/state 深 clone 只读观察 | `TryObserveFrozen` 直接委托 A1 `TryGetFrozen`（深 clone） |
| 运动写回保留保护字段 | 一律走 A1 `TryAdvanceMotion`（只写运动面）；测试 `L` 断言推进不改 `AuthorityNetId/ActivationId/AllowedTargets` |
| 无场景墙碰撞接口 ⇒ 不假称不穿墙 | 本组只有直线核心运动（`pos += vel*dt`）；场景/Unity 碰撞查询留 C 阶段，由 `IPMProjectileHostMotion` 或替换运动内核接入；报告与代码注释均明确不做「不穿墙」承诺 |
| 不依赖未知 B Codec | 两个工程逐文件 Include，**无 glob**；全文件无 `PMProjectileCodec` 引用 |

### 4.1 verify 配额：AdmitVerify 与 Validate 不双计数

- `AdmitVerify`（A1 墓碑准入）只做「窗口内/已过期」闸门，**不计配额**；
- 配额唯一来源 = A2 `PMProjectileValidateResult.VerifyConsumed`：`rec.VerifyUsed += 1`；
- 第 6 次 `ReportHits` 直接返回 `QuotaExhausted`（`Reason=VerifyQuotaExhausted`）且不再消耗；
- 测试 `M4` 断言：5 次逐次 `VerifyUsed=1..5`、第 6 次 `QuotaExhausted`、`QuotaRejectionCount=1`，
  且此时 `Lifecycle.AdmitVerify` 仍返回 `Allowed`（证明两个口径独立）。

---

## 5. 测试覆盖（T5A6 的 A3 面，294 项断言）

| 分组 | 覆盖点 | 断言 |
|---|---|---|
| A 完整权威闭环 | 激活 Confirmed → admission 立即生成 → 5×50ms 直线运动（位置/MoveTimeMs 手算）→ 真实历史目标 → Verify → **恰好一次** settlement → 重复 Drain 为空 → StopOnHit 停止出口 + 墓碑 + 运动终态；权威面消毒（速度强度、命中集合、白名单、权威 ID 域口径）、生成出口带可信 authorityNetId | 38 |
| B Verify-before-Spawn | 未知 key 的上报进 B（2 条）→ admission 挂起（**不发 SpawnReady**）→ 确认后生成 + **FIFO 回放** → 结算顺序 = 入队顺序（55 先、56 后） | 23 |
| C activation Pending 命中结论 | L0 Pending ⇒ 进 C（已消毒）⇒ 确认时**直接结算候选**，`VerifyUsed` 仍为 1（证明未重跑 Validate/历史）⇒ 重复确认幂等、无第二次结算 | 16 |
| D 拒绝 / TTL | 拒绝：不结算、C 清空、登记被实际移除、决策带 `RemovedInstance=true`、同 activation 新弹 `RevokedActivation`（终态不倒退）；TTL：挂起 Spawn 超 2000ms 被清理且**收回 ID 预留**、不结算 | 13 |
| E 同 activation 20 散弹 | 20 颗同源同 activation 全部入队（键=完整 key，不被 `DuplicateSource` 挡）→ 一次解挂 20 条 → 20 条生成出口且 projectileId 无错位 | 47 |
| F 墓碑窗口内外 | ServerDirect ×2 + Lifetime 停弹；墓碑窗口**手算 = 150ms**；窗口内可完成校验并结算；窗口外 `TombstoneExpired` 且不结算 | 18 |
| G admission 注入与 trustedSpec | 跨 owner / owner=0 / 跨 epoch / 冒充 ServerDirect / 预测侧 activationId=0 / 零方向 / NaN 方向 / 10m 正好通过 vs 10.01m 拒 / 重复 key / 回退 ID 不复用；客户端伪造 spec 与权威字段全部被消毒；可信白名单生效 | 28 |
| H 最终确认复核 | 目标 55 升 stream、目标 56 死亡 ⇒ 确认时两个命中都被拒（`RejectedStaleTargetCount=2`）、0 结算 | 6 |
| I Drain 一次性与去重 | 先提交再排队；第二次 Drain 为空；同弹同目标第二次不结算；Pending 只记账不结算 | 8 |
| J 压力 / 升代 / 断连 | 600 次生成 ⇒ 出口队列有界 512 + `DroppedSpawnEventCount=88`；断连清 A/B/C + 登记 + 记账，且**旧 ID 不复活**；`AdvanceEpoch` 清空全部且旧 epoch 请求 `StaleEpoch`；`ResetState` 亦不让旧 ID 复活 | 22 |
| K NaN / 时钟 / 预算 | NaN/Inf 墙钟、NaN 枪口、时钟倒退（推进 + 上报）、单步 51ms、单步 −1ms、零步、NaN 命中点整包拒、追赶 100000ms 被钳制到总预算 | 16 |
| L 运动 / 终态 / hook | 未登记不能追赶、预测弹不能走权威追赶、追赶只开始一次；推进不覆盖保护字段；宿主 hook 替换运动（X 不被内核推进）、hook 不产生结算；StopOnHit 终态；hook 触发停止；拒绝性 filter / 无 filter 一律不结算 | 23 |
| M 负例与容量边界 | B 队列单 key 上限 5（第 6 条 `Capacity` 可观测）、总量 128、TTL 联动清空且不结算；真实 Verify 配额 5 与墓碑准入独立；批内 stream 版本不符 / 未知目标 skip 不结算；结算队列有界 512 + 显式溢出 | 36 |

**测试用真实现**：目标历史是真实 `PMProjectileHistory`（含 stream 升代 / Alive / 单调时间），
几何由真实 `PMProjectileValidator` L0–L4 计算，**没有替身生命周期、没有替身 validator、没有替身历史**。
手算 oracle 举例：L2 飞行预算 `10 * (2*0 + 100 + 0)/1000 = 1.0m`；墓碑 `max(150, 0, clamp(100,0,1000)) = 150ms`；
capsule 中心线半长 `1.0 − 0.5 = 0.5`；命中阈值 `0.1 + 0.5 + 0.30 = 0.9m`。

---

## 6. 未接网络 / 未接 Unity（诚实边界）

1. **无 RPC 承载**：本组只提供纯核心出口（`DrainDecisions/SpawnReady/Stopped/Settlements`）；
   `PMR3` 声明生成集合、`byte[]` 上限、上行 `SpawnIntent` 编解码全部属于 **B1/B2**，本组不依赖、不冒充。
2. **无复制 / 无 Unity 宿主**：不创建任何 GameObject/物理体；不引 `UnityEngine`。
3. **无场景墙碰撞**：本组只有直线核心运动，**不声称不穿墙**；Unity 侧碰撞查询留 **C 阶段**
   （接入点已留 `IPMProjectileHostMotion`）。
4. **实机双端验收未做**：`T45` 仍为 `PENDING_USER`，本组不改变该状态。
5. **出口队列必须每 tick 抽干**：容量 512、溢出丢最旧并计数。若网络层长时间不 Drain，
   激活确认可能被挤掉 ⇒ 客户端留下「永久假弹」。因此 `DroppedDecisionCount`/`DroppedSpawnEventCount`
   是必须监控的量；这是有界内存与「不静默丢」之间的显式取舍，不是静默行为。
6. **未做骨骼精细校验**（A2 已登记后置）；本组沿用胶囊中心线判定。
7. **`MoveTimeMs` 语义**：本组按「弹自身累计毫秒」推进，与 TTL/墓碑的墙钟**不混用**；
   帧锚新鲜度仍由 A2 的 `TotalSimTimeMs` 负责。

---

## 7. 与两组真实核心的接口缺口（**未改 A1/A2**，按委派要求在集成器内补足 + 精确登记）

> 委派明确「A 某 API 无法满足明确阻塞时，报告确切函数与最小修复，别绕开真实核心重写第二套」。
> 以下**均不构成阻塞**（已用真实 API + 集成器侧补偿解决），但建议主侧在 B/C 冻结前裁决。

| # | 位置 | 现象 | 集成器现状 | 建议最小修复 |
|---|---|---|---|---|
| G1 | `PMProjectileLifecycle.TryRecordStop` | 写 `e.Stopped/StopWallTimeMs/TombstoneUntilMs` 与 `e.State.*`，但**漏写 `e.State.TombstoneUntilMs`**（镜像停止路径 `TryApplyMirror` 是写的）。于是 `TryGetFrozen` 出来的冻结 `State.TombstoneUntilMs` 恒为 0，A2 验证器的 L1 墓碑截止（`state.TombstoneUntilMs > 0 && WorldNowMs > …`）在「本地停止」路径上**静默失效** | 集成器在 `ProcessVerify` 里用 `PMProjectileRegistration.TombstoneUntilMs`（账本上的权威值）补齐只读入参，**不改 A1 文件**；墓碑窗口判据仍由 `AdmitVerify` 独立把关，测试 `F` 覆盖窗口内外 | `TryRecordStop` 内 `e.State.TombstoneUntilMs = e.TombstoneUntilMs;`（一行） |
| G2 | `PMProjectilePendingVerifies` / `PMProjectilePendingHits` | 只有按 key 的 `Discard(key)`/`TryResolve(key,…)`，**没有按 owner 的批量清理/枚举**；而契约要求「断连清所有暂存」 | 集成器维护自有 key 记录（`_records`）并按 owner 扫描逐 key 调真实 `Discard`/`TryResolve(Rejected)`，仍由真实队列执行删除 | 各加 `int DiscardOwner(uint ownerNetId)`（内部遍历即可） |
| G3 | `PMProjectileLifecycle.TryRegisterPredicted` | `CreateEntry(key, 0u, …)` 对**预测**登记一律把 `AuthorityNetId` 归 0（A1 审查要求项 7 的既定口径） | 集成器把可信 `authorityNetId` 记进自有记录，`PMProjectileSpawnEvent`/`PMProjectileSettlement` 出口用它的值；冻结 `State.AuthorityNetId` 保持 A1 的 0（不违反「无客户端权威字段写入」） | 无需修；若网络层要求 `State.AuthorityNetId` 也带可信值，需 A1 显式支持（本组不擅自改） |
| G4 | `PMProjectilePendingSpawns` | 只有 `DiscardOwner` / `ResolveByActivation`，**没有单 key 丢弃** | 集成器把顺序固定为「先 `TryRegisterPredicted`（预留 ID），后 `TryEnqueue`（挂起 payload）」；入队失败时用 `Lifecycle.Retire` 回滚 ID 预留（测试覆盖 `Capacity` 路径的语义） | 可选加 `bool Discard(PMProjectileKey key)` |
| G5 | `PMProjectileLifecycle.CreateEntry` 水位 | 同 `(owner, origin)` 的 `ProjectileId` 必须**按到达顺序单调**，否则 `NonMonotonicId`。本组已满足「请求时预留 ⇒ 乱序解挂不误拒」，但**乱序到达**（同 stream 的 7 先于 5 到达）仍会被水位拒 | 如实记录在报告；未擅自放宽 A1 冻结语义 | 若线上 UDP 乱序确实会打乱同激活散弹的 ID 顺序，需要 A1 提供「ID 预留集」或按 activation 批内重排的显式语义（属契约变更，需主侧裁决） |
| G6 | 契约 vs A1 容量口径 | 契约「挂起 Spawn 128/owner、总 1024」由 A1 `PMProjectilePendingSpawns` 独立实现；整合器 `_records` 上限 4096 > 「权威 1024 + 预测 1024 + 挂起 1024 + B key 128」，正常链路不会触发淘汰（`EvictedKeyRecordCount` 可观测） | 已按契约取小（每 owner 128、总量 1024） | 无 |

**明确阻塞**：无。本组全部 294 项断言在真实 A1/A2 实现上通过，未绕开真实核心、未重写第二套。

### 7.1 需要主侧知悉的接口决策（供 C 阶段接真机时对齐）

1. **枪口 = `request.State.SpawnPosition`**：它是唯一被采信的位置意图，必须 finite 且与 `ownerPosition` 距离 ≤10m；
   `request.State.Position` 只做 finite 校验、不采用（`SanitizeState` 把 `Position/PreviousPosition` 都设为枪口）。
2. **方向意图 = `request.State.Velocity`（未归一化向量）**：B1 的 `SpawnIntent.Direction` 接进上游 DTO 后，
   适配层需要把它写进 `State.Velocity`（或主侧给 A3 增一个显式 `direction` 入参）。
3. **`sourceBehaviorInstanceId != 0`**：A1 挂起队列要求非 0（0 号源不存在），因此 admission 把 0 视为 `InvalidRequest`。
4. **`predictionMs >= 0`**：用于墓碑公式；由可信宿主提供，客户端不得自报。

---

## 8. 已检查范围

**完整读取的文档（按委派顺序）**：`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`
→ `Docs/plans/net-r5-projectile-contract.md`（全文）→ `Client/Assets/Scripts/PMProjectile/PMProjectileContracts.cs`（全文）
→ `Docs/plans/_r5_lifecycle_review.md`（全文，重点 §3 API 与 §4 剩余风险）
→ `Docs/plans/_r5_validation_report.md`（全文，重点 §3 API）→ `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`。

**完整/必要段读取的实现（只读）**：`PMProjectileLifecycle.cs`（全文 2059 行）、`PMProjectilePending.cs`（全文 1308 行）、
`PMProjectileHistory.cs`（Record / TryResolve / 容量与裁剪段）、`PMProjectileValidator.cs`（L0–L4 与 L2 预算段）、
`PMNetIdentity.cs`（`PMFrameId/PMFrameDomain`）、`PMMoverState.cs`（`PMVector3`）、
A1/A2 的四个门禁工程 `.csproj`（作为 Include 面与编码约定的参照）。

**未做**：未启动 Unity；未运行 SVN/git 写操作；未提交；未递归委派；未改共享契约 / 主计划 / A1 / A2 / B1；
未引用 B1 codec；未做跨组通配 Include。
