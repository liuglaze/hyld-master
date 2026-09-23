# R5-B2d 生成入口重复上行幂等收口（driver 根治 + 撤 host 旁路）

> 任务类型：**comprehensive**。
> 写入边界（严格遵守，仅此 4 个文件）：
> `Client/Assets/Scripts/PMR3/PMR5ProjectileDriver.cs`、
> `Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs`、
> `Tools/PMR5NetworkTest/Program.cs`、
> `Docs/plans/_r5_spawn_idempotency_fix.md`（本报告）。
> **未**改 core（`PMProjectile/**`）、生成物、锁文件、csproj、主计划/契约、客户端宿主。
> **未**启动 Unity、**未**提交、**未**递归委派。
>
> 基线：`Docs/plans/_r5_ds_host_review.md`（F6 在宿主侧加 `DiagnosticProjectileUplinkGate` 装饰器的旁路）。
> 本轮**撤掉**该旁路，把 F6 收口到 driver 的**已受理 key 无副作用幂等门**，并补**真实生成入口**重复上行测试。

---

## 0. 结论摘要

| # | 项 | 判定 | 落点 |
|---|---|---|---|
| 1 | F6 真根因在 driver：同 key 重传 → 策略/准入任一拒绝 → **无条件**下行 `Rejected` → 客户端 `RevokeFake` 反向撤销已 Confirmed 弹 | ✅ **已在 driver 根治** | `PMR5ProjectileDriver.cs` L1069–L1101（门）、L1228（判据）、L1266（吸收） |
| 2 | 宿主 `DiagnosticProjectileUplinkGate` 装饰器是**旁路**（替换 `player.ProjectileDriver`、破坏 `UnbindPlayer` 身份判定、依赖 32 条指纹窗口） | ✅ **已完全删除** | `PMDsSessionHost.cs`：类 / 字典 / Install / Clear / 3 处调用 / 2 项诊断字段 |
| 3 | 已受理 key 的重复上行不得再扣 policy / 预留 NetId / RequestSpawn / 发 Rejected | ✅ **已保证**（零副作用） | 门在 codec + 身份断言之后、`TryReserveNetId` **之前** |
| 4 | 同 key 改 activation/配置冲突：不改原对象/原决策，可计数，但**不得**发 Rejected 撤旧弹 | ✅ **已保证** | `RejectedDuplicateConflict` + 限流告警 |
| 5 | retired 迟到请求按终态拒、无副作用、不新增**无界** fingerprint 缓存 | ✅ **已保证**（用既有终态环 + 登记事实，不新增缓存） | L1096–L1101 |
| 6 | 新增 generated 可靠入口重复请求测试（非 Transport 同包去重） | ✅ **新增 N 段 57 项断言，全绿** | `Tools/PMR5NetworkTest/Program.cs` N 段 |
| 7 | F1 脏视图 drain / F2 断线 Alive=false + 实时 filter / F8 日志计数隔离 | ✅ **独立修正保留** | 见 §3.2 取证 |
| 8 | 编译门 / 测试门 | ✅ 全绿（见 §5） | 0 警告 / 0 错误；`PMR5NetworkTest` 417/0 |

---

## 1. 真根因（为什么必须改 driver，而不是在宿主加装饰器）

### 1.1 旧路径的坏链

`PMR5ProjectileDriver.HandleServerSpawn`（改前）在完成身份断言后**直接**走：
`TryReserveNetId` → `_policy.TryAuthorizeSpawn` → `ResolveActivationInternal` → `_coordinator.RequestSpawn`。

对**同一颗弹**的第二条可靠上行（重连 / 传输重发 / 换流）：

1. **再扣一次策略额度**：DS 宿主策略用 `_projectileLastActivationByOwner`（单调 activation）与
   `_projectileLastFireWallMsByOwner`（200ms 间隔）记账，重传被无谓推进；
2. **再预留一个真实 NetId**（`ReservationCount` 一度 +1，最终还要 `CancelReservedNetId`）；
3. `RequestSpawn` → 生命周期 `TryRegisterPredicted` 判 `DuplicateId` → `MapRegisterFailure` 映射为
   `AlreadyAdmitted`；
4. `!admission.IsAdmitted` 分支**无条件** `SendDecisionFor(..., Rejected, "admission-...")`；
5. 客户端 `HandleClientDecision` 收到 `Rejected` → `RevokeFake` → `Lifecycle.Retire`
   ⇒ 一条**已 Confirmed、镜像已在客户端接管**的合法假弹被反向撤销（终态倒退）。

### 1.2 为什么旧的宿主装饰器是旁路（本轮撤销的理由）

`DiagnosticProjectileUplinkGate` 做的事是：在 `OnConnected` 里 `BindPlayer` 之后**替换**
`player.ProjectileDriver` 为装饰器，用「每 owner 32 条 `(activationId, projectileId)` 环形记忆」抑制
「三连完全相同」的重传。问题：

* **它替换了驱动的接缝身份**：`PMR5ProjectileDriver.UnbindPlayer` / `Dispose` 用
  `ReferenceEquals(player.ProjectileDriver, adapter)` 判定是否置空字段；装饰器在场时该判定不成立，
  于是宿主必须在**每一条断线/释放路径**手工补 `ClearProjectileUplinkGate` —— 已经把「幂等」变成了
  「宿主生命周期耦合」，而不是不变量。
* **它是窗口假设**：只记 32 条，超窗口的同载荷重传仍会走到 driver 并产生第二条 `Rejected`（旧报告 V7 自述）。
* **它不是通用幂等**：真正的不变量是「driver 对自己**已受理**的 key 不再产生任何二次裁决」，
  这与「指纹是否在 32 条环内」无关。

因此本轮把口径放回 driver：**用 driver 自己的登记事实**（预留令牌 / 权威对象 / 协调器-生命周期登记记录 /
有界终态环）判定「已受理」，在其上做**零副作用**吸收。

---

## 2. 修改 1：driver 侧「已受理 key 无副作用幂等门」

文件：`Client/Assets/Scripts/PMR3/PMR5ProjectileDriver.cs`。

### 2.1 落点（精确行号，最终状态）

| 内容 | 行 |
|---|---|
| 文件头铁律新增第 9 条 | L35–L39 |
| `DuplicateMuzzleToleranceM = 0.05f` / `MaxDuplicateConflictWarnings = 8L` | L219 / L222 |
| 新计数 `ServerSpawnsDuplicateIgnored` / `RejectedDuplicateConflict` / `RetiredSpawnsDropped` | L458 / L464 / L467 |
| **幂等门插入点**（身份断言之后、`TryReserveNetId` 之前） | L1069–L1101 |
| `TryFindAdmittedKey(...)` | L1228 |
| `HandleDuplicateUplinkSpawn(...)` | L1266 |
| `RejectOwnerUplinkSpawn` 的同一铁律守卫 | L1296 |
| `Describe()` 新增 `dupIgnored` / `dupConflict` / `retiredDropped` | L3057–L3059 |

### 2.2 门的判据（零副作用、只读登记事实）

```csharp
bool admittedByOwned = false;
if (_reservations.ContainsKey(key))      { evidence = "reservation";     admittedByOwned = true; }
else if (_authorityObjects.ContainsKey(key)) { evidence = "authority-object"; admittedByOwned = true; }

PMProjectileRegistration snapshot;
if (_coordinator.TryObserveRegistration(key, out snapshot) && snapshot != null)  // 冲突检测需要 activationId/枪口
{
    admitted = snapshot;                       // 仅在「未登记」时不分配
    if (!admittedByOwned) { evidence = "registration"; }
    return true;
}
if (admittedByOwned) { return true; }
if (_coordinator.IsSpawnPending(key)) { evidence = "spawn-pending"; return true; }
if (_coordinator.IsSpawned(key))      { evidence = "spawned";        return true; }
return false;
```

* **四条登记事实**覆盖「已受理」的全部窗口：`_reservations`（Pending / 已预留未上线）、
  `_authorityObjects`（已 `SpawnReserved` 上线）、`TryObserveRegistration`（生命周期登记记录，最贴近
  「同一颗弹」的事实，也覆盖 `ForgetRecord` 之后仍登记在册的窗口）、`IsSpawnPending` / `IsSpawned`。
* 命中即 `return`，**不再**调用 `_policy.TryAuthorizeSpawn`、**不再** `TryReserveNetId`、
  **不再** `RequestSpawn`、**不再** `SendDecisionFor`。
* 未登记时 `TryGetRegistration` 直接返回 false（**不分配**），因此正常首次上行无额外 GC 负担。

### 2.3 冲突处理（同 key 改 activation/枪口）

```csharp
bool activationChanged = admitted != null && admitted.ActivationId != intent.ActivationId;
bool muzzleDrifted = admitted != null && admitted.State != null
    && PMVector3.Distance(admitted.State.SpawnPosition, intent.Position) > DuplicateMuzzleToleranceM;
if (!activationChanged && !muzzleDrifted) { return; }   // 完全相同重传：静默吸收
RejectedDuplicateConflict++;                            // 冲突：只计数 + 限流告警（≤8 条）
```

* 冲突**只计数**（`RejectedDuplicateConflict`）并限流告警；**不**改写已建对象、**不**改写激活账本、
  **不**据此下发 `Rejected`。原裁决在可靠域上会照常送达，重复下行只会撤销合法假弹。

### 2.4 retired 迟到请求

```csharp
if (_coordinator.Lifecycle.IsRetired(intent.Key)) { RetiredSpawnsDropped++; return; }
```

* 按终态拒、**无副作用**：不预留 / 不授权 / 不登记，**不**补 `Rejected`（迟到请求对应的假弹要么早已接管，
  要么由客户端本地 TTL/墓碑清理收束）。
* **不新增任何 fingerprint 缓存**：直接复用生命周期既有的**有界终态环**（`MaxRetiredMarkers = 2048`）。

### 2.5 同一条铁律覆盖入站队列溢出路径

`RejectOwnerUplinkSpawn`（入站生成队列满时的补发 Rejected 路径）也加了同一守卫：
若该 key **已被本会话受理**（或已 retired），则 `ServerSpawnsDuplicateIgnored++` 后 `return`，**不补 Rejected**。
理由：队列溢出只应淘汰「尚未受理」的载荷，不能借溢出路径反向撤销已确认弹。

### 2.6 真实性边界与安全口径（“仅凭已存在 key 忽略重复”成立的前提）

这是本收口的关键正确性论证，必须写明：

1. **位置**：门在 **codec 解码 + 认证身份断言之后**。能走到门的 key 必然满足
   `Key.Epoch == 会话 epoch`、`Key.Origin == ClientPredicted`、`Key.OwnerNetId == player.NetId（认证 owner）`、
   `ActivationId != 0`、`Key.IsValid`。
2. **只信服务端自己的登记事实**：门**不读** payload 的任何自报标志（不读自报 owner / authorityId / 状态位）。
3. **同 key ⇒ 同一颗弹**：生命周期对每 `(owner, origin)` 的 `projectileId` 维护**单调高水位、永不复用**
   （`CreateEntry` 的 `NonMonotonicId`，`_watermarks` 在 `Retire`/`ClearOwner`/终态淘汰后仍保留）。
   因此一个 key 不可能被「另一颗伪装成新弹的伪造」复用 —— 命中登记事实的 key 只能是同一颗弹的重复投递。
4. **无界风险为零**：门不新增任何按 key 的缓存；存储全部复用 driver/coordinator 既有的**有界**结构
   （`_reservations` / `_authorityObjects` 随 key 生命周期回落、登记记录随 `Purge/Retire` 清除、
   终态环 2048 有界）。
5. **超出有界终态环时的诚实边界**：若登记事实已被清（`Purge`/`Retire`）且终态环也已淘汰该 key，
   门**不命中**，会退回原路径（预留 + policy + 生命周期水位 `NonMonotonicId` 拒绝）。
   此时**不会**出现「反向撤销已 Confirmed 弹」，因为该 key 的假弹早已接管/终结；
   代价是一次无谓的 NetId 预留/取消与一条对已消失 key 的 `Rejected`（客户端 `_fakes` 已无此 key ⇒ 幂等 no-op）。
   这是**有界且显式**的取舍，不引入无界缓存。

---

## 3. 修改 2：从 PMDsSessionHost 完整删除 host 旁路

文件：`Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs`（3007 → **2814** 行，净 −193）。

### 3.1 删除清单（逐项）

| # | 删除对象 | 原位置 |
|---|---|---|
| 1 | 字段 `_diagnosticProjectileUplinkDuplicates` | 原 L608 |
| 2 | 字典 `_projectileUplinkGates` | 原 L617–L618 |
| 3 | `InstallProjectileUplinkGate(...)` 方法 + 其 F6 文档注释 | 原 L2440 起 |
| 4 | `ClearProjectileUplinkGate(...)` 方法 | 原 L2454 起 |
| 5 | `ClearAllProjectileUplinkGates(...)` 方法 | 原 L2467 起 |
| 6 | `DiagnosticProjectileUplinkGate` 类（含 `MaxRememberedUplinks` / `ForwardedUplink` / `IsDuplicateUplink` / `RememberUplink`） | 原 L2792 起 |
| 7 | 调用点 `InstallProjectileUplinkGate(uid, player)`（OnConnected） | 原 L1634 |
| 8 | 调用点 `ClearProjectileUplinkGate(uid, player)`（断线 player==null 分支的 `_projectileUplinkGates.Remove`、driver==null 分支） | 原 L1271、L1284 |
| 9 | 调用点 `ClearProjectileUplinkGate(uid, player)`（正常断线路径） | 原 L1335 |
| 10 | 调用点 `ClearAllProjectileUplinkGates()`（`ReleaseProjectileWiring`） | 原 L2080 |
| 11 | `DescribeProjectileWiring` 的 `uplinkDuplicates=` 与 `uplinkGates=` 两项 | 原 L2905、L2908 |
| 12 | `max` F6 段注释头改写为「根治点已收口到 driver」的说明（保留节标题，防止被装回） | 原 L2415 附近 |

**验证**：`grep -c "DiagnosticProjectileUplinkGate|_diagnosticProjectileUplinkDuplicates|_projectileUplinkGates|
InstallProjectileUplinkGate|ClearProjectileUplinkGate|ClearAllProjectileUplinkGates|uplinkDuplicates|uplinkGates"`
→ **0**（改前 26）。

### 3.2 独立修正保留（本批**未**回退）

| 项 | 取证（最终行号） |
|---|---|
| F1 脏视图 drain：`DrainDsProjectileViews()` 定义 @L2320、调用 @L2122 | ✅ 保留 |
| F2-① 断线补 `Alive=false` 样本：`RecordProjectileDisconnectSample` @L2339、调用 @L1287 | ✅ 保留 |
| F2-② 命中取实时连接事实：`IsProjectileTargetAuthoritative` @L2380、调用 @L2575 / @L2581 | ✅ 保留 |
| F7 会话失败上抛宿主：`FaultHostIfProjectileDriverFaulted` @L2149、调用 @L2074 / @L2139 | ✅ 保留 |
| F8 日志计数隔离：`LogProjectileSettlement` @L2258、调用包 try/catch @L2243、诊断 @L2714 | ✅ 保留 |
| `MaxProjectileDiagnosticWarnings`（仍被其它诊断使用） | ✅ 保留（5 处） |

「唯一 driver 适配器不被宿主覆盖」已恢复：`player.ProjectileDriver` 现在**只**由
`PMR5ProjectileDriver.BindPlayer` 写入、`UnbindPlayer`/`Dispose` 清空，宿主不再参与该字段的写入/清空。

---

## 4. 修改 3：新增测试（`Tools/PMR5NetworkTest/Program.cs`）

新增 Section N（`TestUplinkDuplicateIdempotency` + 夹具 `ReplaySpawnRpc`），共 **57 项断言**。

`ReplaySpawnRpc` 的关键性质：它**重新编码**一份与首次同 key/同 activation 的 `PMProjectileSpawnIntent`，
再调 `h.ApReplica().PMNet_ServerProjectileSpawnV1(payload)` —— 每次都会**重新走一遍生成桩、编码出一份新数据报**，
因此验证的是 **driver 侧幂等**，**不是** Transport 同包去重（满足任务要求）。

| 子段 | 场景 | 关键断言 |
|---|---|---|
| **N1** | 首次 Confirmed + 镜像到齐 + 已接管 → 同 key/同 activation 重复 | `ServerSpawnsDuplicateIgnored +1`；`policy.Calls` 不增；`ServerSpawnsAuthorized` / `Spawned` 不增；`ServerSpawnsRejected` 不增；`DecisionsSent` 不增；AP `DecisionsReceived` 不增；AP `FakesRevoked` 不增；`ReservationCount` 不增；`AuthorityObjectCount == 1`；原 key 视图仍在 |
| **N2** | Pending → 推进墙钟 ~320ms → 重复 | 挂起项仍在；`PendingSpawn.EnqueuedWallTimeMs` **不变**（TTL 不被刷新）；`ReservationCount` 不增；`policy.Calls` 不增；`DecisionsSent` 不增；`IsSpawnPending` 仍为 true |
| **N3** | 同 key **改 activation** → 重复 | `RejectedDuplicateConflict +1`；`FakesRevoked` 不增；`DecisionsSent` 不增；`policy.Calls` 不增；旧弹视图仍在；`AuthorityObjectCount == 1`（原对象未被改/新增） |
| **N4** | **不同 key 同 activation** | `policy.Calls +1`（按新请求规则走 policy，未被门误挡）；`ServerSpawnsSpawned +1`；`ServerSpawnsDuplicateIgnored` 不变 |

### 4.1 负向对照（证明测试有判别力）

临时把门短路（一行 `bool pmr5NegativeControlDisableGuard = true;`，**验证后已逐字还原**，未用 git 回退）：

```
── N. 真实生成入口重复上行：已受理 key 幂等（不扣 policy / 不重复预留 / 不补 Rejected）
      FAIL N1 重复被幂等吸收（dupIgnored +1）（期望 1，实际 0）
      FAIL N1 policy 调用不增（不扣授权额度）（期望 1，实际 2）
      FAIL N1 不新增拒绝计数（期望 0，实际 1）
      FAIL N1 不给已受理 key 下发任何新裁决（期望 0，实际 1）
      FAIL N1 AP 没收到任何新裁决（期望 0，实际 1）
      FAIL N1 AP 假弹未被撤销（无 Rejected）（期望 0，实际 1）
      FAIL N2 policy 不再被调用（期望 1，实际 2）
      FAIL N2 重复被幂等吸收（期望 1，实际 0）
      FAIL N2 未给 Pending 弹下发任何裁决（期望 0，实际 1）
      FAIL N3 冲突被计数（RejectedDuplicateConflict）（期望 1，实际 0）
      FAIL N3 不给已存在 key 发 Rejected（旧弹不被撤）（期望 0，实际 1）
      FAIL N3 没有新增下行裁决（期望 0，实际 1）
      FAIL N3 不再调用 policy（期望 1，实际 2）
通过 404 项，失败 13 项。
```

即：**没有本修复时，N 段正是「重复扣 policy + 重预留 + 补 Rejected + 撤销已确认假弹」的现场**。
还原后重跑为 **417 / 0**。

---

## 5. 测试与构建证据（全部本批执行）

| 命令 | 结果 |
|---|---|
| `dotnet build Tools/PMR5NetworkCheck -c Release --no-incremental` | **0 警告 / 0 错误**（netstandard2.0 + C#7.3，零 UnityEngine；Unity 语言面） |
| `dotnet build Tools/PMR5NetworkTest -c Release` | **0 警告 / 0 错误** |
| `dotnet Tools/PMR5NetworkTest/bin/Release/net8.0/PMR5NetworkTest.dll` | **通过 417 / 失败 0**（上一批 360；本批 +57 = N 段） |
| `dotnet build Tools/PMR4UnityCheck -c Release --no-incremental -o Tools/PMR4UnityCheck/bin/r5-idem` | **0 警告 / 0 错误**（真实 Unity 2019.4 DLL；含 `PMDsSessionHost` + `PMR5ProjectileDriver`；独立输出目录避开共享 bin） |
| `dotnet build Tools/PMClientCheck -c Release --no-incremental` | **0 警告 / 0 错误**（含 `PMR5ProjectileDriver.cs` + `Server/**`） |
| `dotnet build Tools/PMUnityGlueCheck -c Release --no-incremental` | **0 警告 / 0 错误**（含 `PMR5ProjectileDriver.cs` + `PMDsSessionHost.cs`） |
| `python Tools/check_cs_braces.py`（driver / host） | `{}` 490/490、807/807；`()` 807/807、892/892；深度非负末尾归零 → **PASS** |
| 编码自检（driver / host） | BOM=True；CRLF 全量；裸 LF = **0** |
| 产物取证 | 4 个 check 的 DLL 均为本轮写入时间（18:39:16–18:39:29），确认编的是**改动后**源码而非缓存 |

§4.1 的负向对照与本节「通过 417 / 失败 0」共同构成「**先证明会坏、再证明已修**」。

---

## 6. 未执行 / 诚实边界

| # | 项 | 说明 |
|---|---|---|
| V1 | **未启动 Unity** | 按「先代码后实机」口径；不标实机 PASS，不声称 T45 或 R5-C 完成。 |
| V2 | 未做真实**双连接弱网**重复上行 | N 段走真实生成桩 + 真实字节链，但为**单机 1 owner + 1 observer**；真实双连接下的重连/换流重复未跑。 |
| V3 | 超出有界终态环的迟到请求 | 门不命中时会退回原路径（见 §2.6-5）；该窗口的行为**未**构造用例（需 2048+ 次退休）。 |
| V4 | 冲突只覆盖 activation + 枪口位置 | 不比较完整 payload 指纹（那需要按 key 缓存，违反「不加无界缓存」）；方向/yaw 差异不触发 `RejectedDuplicateConflict`。 |
| V5 | DSPending 重复的“TTL 不刷新”用 `EnqueuedWallTimeMs` 断言 | 该字段就是 TTL 计算源（`PMProjectilePendingSpawns.PurgeExpired` 用 `EnqueuedWallTimeMs + PendingTtlMs`），因此是等价断言。 |
| V6 | 无 R6 | 结算仍是诊断计数 + 日志，`hits` **不是**扣血次数；本轮未引入任何生命值语义。 |
| V7 | 客户端宿主未接线 | 本批只动 DS 宿主与 driver；未读/未改 `PMClientSessionHost.cs`。 |
| V8 | `RejectOwnerUplinkSpawn` 守卫未单独造用例 | 入站队列满（256）在 N 段未触发；该守卫是同一铁律的对称加固，靠编译与逻辑审查保证。 |

---

## 7. 已检查范围（按委派顺序）

**完整读取的文档**：`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`（全文，重点 §1.1 DS 身份唯一来源
`PMNetRuntime.IsDedicatedServer`、§4 子弹/表现分层、§6 服务端权威字段）→ `Docs/plans/net-r5-network-contract.md`
（全文，重点「B2b 网络 driver」与「C 宿主首个可执行入口」）→ `Docs/plans/_r5_ds_host_review.md`（全文，重点 F6
旁路与其自述残余 V7）→ `Docs/plans/_r5_network_review.md`（全文，重点 F1–F6 返工与公开 API）
→ `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`（全文）。

**文档如何决定本次入口**：契约要求「B2b/C 适配不修改 SessionHost/driver 之外的语义」「出口唯一且显式失败」；
`_r5_ds_host_review.md` F6 明写「根治点在 driver（§6 建议）」，本轮即执行该建议并把宿主旁路撤掉；
`_r5_network_review.md` 给出 driver 公开面与既有测试手法（真实生成桩 + 真实字节链 + 声明接缝），N 段沿用之。

**只读读取的实现**：`PMR5ProjectileDriver.cs`（全文）、`PMDsSessionHost.cs`（全文，被改对象）、
`PMProjectileCoordinator.cs`（`RequestSpawn` / `ServerDirectSpawn` / `ResolveActivation` / `MapRegisterFailure` /
`TryObserveFrozen` / `TryObserveRegistration` / `IsSpawnPending` / `IsSpawned` / `CatchUpRegisteredMotion` / Drain* / 限额）、
`PMProjectileLifecycle.cs`（`TryGetRegistration` / `IsRetired` / `TryGetActivationResult` / `TryRegisterPredicted` /
`CreateEntry` 的 `DuplicateId`/`NonMonotonicId`/watermark / `TrySetActivationResult` 的 `AlreadyTerminal` /
`TryApplyMirror` 的 `Retired`）、`PMProjectilePending.cs`（`PMProjectilePendingSpawns` 的 `TryGet` /
`EnqueuedWallTimeMs` / `DuplicateKey` / TTL）、`PMProjectileContracts.cs`（`MuzzleToleranceM`、`PMProjectileKey`）、
`PMR3Player.cs` + `Generated/PMNet.PMNet.R3.PMR3Player.g.cs`（`PMNet_ServerProjectileSpawnV1` 调用桩与
`ServerProjectileSpawnV1_ForceValidate`）、`PMNetGeneratedRegistry.g.cs`（`EnqueueRemote`，确认无 RPC 去重）、
`Tools/PMR5NetworkTest/Program.cs`（被改对象；A–M 段手法）、各 `Tools/PM*Check/*.csproj`（编译面覆盖确认）。

**本次写入的文件（仅此 4 个）**：`Client/Assets/Scripts/PMR3/PMR5ProjectileDriver.cs`、
`Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs`、`Tools/PMR5NetworkTest/Program.cs`、本报告。

**未做**：未启动 Unity；未执行 SVN/git 写操作；未提交；未递归委派；未改 core/生成物/锁文件/csproj/主计划。
