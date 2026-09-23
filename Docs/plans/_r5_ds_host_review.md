# R5-C DS 宿主独立复核（PMDsSessionHost 诊断投射物接线）

> 任务类型：**comprehensive**（独立复核 + 只修宿主）。
> 写入边界（严格遵守，仅此 2 个文件）：
> `Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs`、`Docs/plans/_r5_ds_host_review.md`（本报告）。
> **未**改 driver / core / 适配器 / 合同 / 主计划 / 生成物 / 锁文件 / csproj / 客户端宿主。
> **未**提交（git/svn 无写操作）、**未**启动 Unity、**未**递归委派。
> 复核基线：`Docs/plans/_r5_ds_host_report.md`（R5-C 接线报告，文件 2638 行）。
> 本次改动量：`PMDsSessionHost.cs` **2638 → 3008 行（+370 / -1）**；本报告为新增文件。
> 契约切片：`Docs/plans/net-r5-network-contract.md` 末段「C宿主首个可执行入口」。

---

## 0. 结论摘要

| # | 复核项 | 判定 | 落点 |
|---|---|---|---|
| F1 | `RebuildViews` 每帧标脏、脏集合只有 `DrainViewChanges` 才清；DS 不消费表现也必须周期 drain | ✅ **已修**（判定修正：不会 fault，但确实必须排空） | `DrainDsProjectileViews` L2348，调用点 L2150 |
| F2 | world history 断线清理 / 样本 `Alive` 不该继续可命中 | ✅ **已修**（双保险：断线补 `Alive=false` 样本 + 命中过滤取实时连接事实） | L2367 / L2408 / L2656 |
| F3 | 只读 `AuthorityTotalSimTimeMs`（无输入帧不推进）是否满足 `history.Record` | ✅ **成立**（原代码正确，仅补注释） | L2225（注释） |
| F4 | 固定步 max8：elapsed 不双计 / 循环中失效中止 | ✅ **成立**（不双计）；**已加固**中途失效中止 | L2116–L2131（中途中止 L2127） |
| F5 | ctor 失败与 Dispose 顺序 | ✅ **已加固**（每条构造失败路径自清理；释放次序原本已正确） | L1999 / L2014 / L2026 |
| F6 | policy 单调 activation；同 activation 的重传 Confirmed 不能被 Rejected 反向撤弹 | ✅ **已修**（宿主侧上行去重门；策略层无解，见 §6） | L2440 / L2792 |
| F7 | Sender Fault 必须 Fail 整个 host，不吞 | ✅ **成立**（原本已 Fail）；**已统一+加固** | L2177 / L2099 / L2102 / L2127 |
| F8 | 最终 Settlement 诊断计数只能一次（不能事件 + drain 重复） | ✅ **已修**（订阅者不再向驱动抛异常） | L2260 / L2286 |
| — | 不接 R6 伤害规则 | ✅ 未触碰（0 处 HP/Mana/死亡/旧 `ApplyHitEvents`） | — |
| — | 编译门（真实 Unity 2019.4 DLL / netstandard2.0 / C#7.3） | ✅ **0 警告 / 0 错误** | §2 |

---

## 1. 写入边界与不变式

* 只写 2 个白名单文件；其余文件（含并行组正在改的 `PMR5ProjectileDriver.cs`、`PMClientSessionHost.cs`、
  `PMProjectileCandidateCollector.cs` 等）**一行未改**。
* 宿主仍是「只做接线」的 DS 宿主：不新造 socket / 不手写 MainPack / 不手工广播 / 不创建表现对象。
* 旧链接触点为零：文件内 `CommandManger` / `BattleManger` / `playerBloodValue` / Mana 引用数仍为 **0**。
* R6 伤害规则：**未新增、未修改**。`OnProjectileSettlement` 依旧只做诊断计数 + 日志（字面写明 non-gameplay、不扣血）。

---

## 2. 复核证据（命令与真实结果）

```bat
REM ① 真实 Unity 2019.4 DLL 编译门（本组指定输出目录，避开并行 client 组的共享 output）
dotnet build Tools/PMR4UnityCheck -c Release --no-incremental -o Tools/PMR4UnityCheck/bin/r5-ds-review
REM ② 结构门（无编译器时的兜底，同时看括号配平）
python Tools/check_cs_braces.py Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs
REM ③ 编码/行尾自检
python -c "d=open('Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs','rb').read(); print(d[:3]==b'\xef\xbb\xbf', d.count(b'\r\n'), d.count(b'\n')-d.count(b'\r\n'))"
```

| 命令 | 结果 |
|---|---|
| `PMR4UnityCheck`（`--no-incremental`，最终状态） | **0 警告 / 0 错误** |
| 产物取证（DLL 内含本次新增符号） | `DrainDsProjectileViews` / `DiagnosticProjectileUplinkGate` / `RecordProjectileDisconnectSample` / `FaultHostIfProjectileDriverFaulted` / `IsProjectileTargetAuthoritative` / `LogProjectileSettlement` / `ClearAllProjectileUplinkGates` 全部 **True**（确认编的是**改动后**的宿主，不是缓存产物） |
| `check_cs_braces.py` | `{ } = 400/400`、`( ) = 939/939`、深度全程非负末尾归零 → **PASS** |
| 编码自检 | BOM = **True**；裸 LF = **0**（全 CRLF） |

**未**运行 `PMUnityGlueCheck` / `PMClientCheck`：它们会写共享 `bin`，与并行 client 组的构建互相污染
（委派项明确「另组 client 不编此项目」「临时并行编译不完整报告主侧不要越界」）。这两道门是**本批未执行项**，
不是「已通过」。

---

## 3. 逐项复核（证据 → 判定 → 修复）

### F1 视图脏通道：DS 必须周期排空（判定修正）

**代码事实**

* `PMR5ProjectileDriver.RebuildViews()`（driver L2565）每帧把新增 / 变化 / 移除的 key 全部 `MarkViewDirty`；
  `_dirtyViewOrder` / `_dirtyViewSet` **只有** `DrainViewChanges` 才清（driver L2527）。
* `MarkViewDirty`（driver L2690）在 `_dirtyViewOrder.Count >= MaxDirtyViews(4096)` 时**整体作废自清**
  并 `ViewDirtyOverflows++`，**不抛不 fault**。

**判定（对委派假设的修正）**：委派项写「不能累计 4096 弹后 fault」——按当前 driver 实现，**不会 fault**，
只会静默自清并让 `ViewDirtyOverflows` 失真。但「DS 必须周期 drain」这个结论**依然成立且必要**：
不排空就是每帧白填 4096 条有界诊断通道（纯浪费），并让该诊断量失去意义。

**修复**：新增 `DrainDsProjectileViews()`（L2348），在每帧 `PumpProjectiles` 的子步循环与结算兜底之后调用（L2150），
把驱动本帧的脏增量**取空并丢弃**（DS 禁止创建表现对象，丢弃即正确语义）。
上界来自驱动自身（脏集合 ≤ `MaxDirtyViews`），排空量 = 本帧真实脏数；DS 是唯一消费者，不会与任何其它消费者抢。
新增可观察计数 `viewDrains`。

### F2 断线样本不得继续可命中（两处真实缺口）

**代码事实**

1. `PMProjectileHistory` **没有**「按目标移除」API（只有 `Clear()` 全清与 `Reset(epoch)` 升代；二者都会牵连其它目标）。
2. 断线路径 `PruneDisconnectedDrivers`（L1269）原本只 Freeze/Dispose 运动 driver 并 Unbind 投射物接缝，
   **不碰 history**；而 `RecordProjectileHistory` 给每个 driver 记的样本是 `sample.Alive = true` 常量（无 HP 系统，见 R5-C 报告 U3）。
   ⇒ 断线后该副本不再被采样，历史里最后一条样本仍是 `Alive=true`。
3. `PMProjectileHistory.Prune` 只在 `Record`/`TryResolve` 时按 `WorldTimeMs` 惰性裁剪；
   命中验证的**帧锚档**（`TryResolve` 第 1 档）按 `candidate.TargetServerFrame` 精确查旧样本，
   旧锚不会因为「不再采样」而失效 ⇒ **断线者在其后 1000ms 保留窗口内仍可被命中**（旧锚更久）。
4. `PMProjectileValidator` 只在 `sample.Alive == false` 时跳过（`TargetNotAlive`），
   然后把样本交给权威 filter（`request.Filter.Accept(key, sample, sanitized)`）。

**判定**：真实缺口（两处：history 事实 + 实时连接事实都缺）。

**修复（双保险，都在宿主内，不改 core）**

* **① 断线补一条不可命中样本**：`RecordProjectileDisconnectSample`（L2367），在 `Freeze/Dispose` **之前**（L1301）
  用断线前最后一份权威事实（`GetAuthoritativeSync()` / `StreamVersion` / `OutputBoundary` / `AuthorityTotalSimTimeMs`）
  写一条 `Alive=false` 样本。它与本帧正常采样**同 `ServerFrame` + 同 `OutputFrame`**，
  因此走 `Record` 的「同帧取最后输出」分支 → 把本帧那条样本覆盖为不可命中；之后随保留窗口自然过期。
* **② 命中过滤取实时连接事实**：`IsProjectileTargetAuthoritative`（L2408）在 `AcceptDiagnosticHit`（L2656）
  对 owner 与 target **各查一次**：连接 `IsReady` + 运动 driver 存活且未 `IsFrozen`，缺数据一律拒（fail closed）。
  这一层覆盖了「旧帧锚解析到活样本」的全部残余窗口。

新增可观察计数 `disconnectSamples`（被拒时走既有 `historyRejects` 限流告警，不静默吞）。

**残余（诚实）**：`PMProjectileHistory` 仍按 64 目标上限保留这些「已断线」的 stream 直到保留窗口过期；
即「目标流数量」在长局中不会被断线立即回收。这是 core 的有界容量语义，本批不改 core，仅登记。

### F3 只读 `AuthorityTotalSimTimeMs` 与 `history.Record`（成立）

**代码事实**

* `PMR4MovementDriver` 的 `_authorityTotalSimTimeMs` **只在 `stepInput.StepMs != 0` 时累加**（driver L1305）；
  占位步（`StepMs == 0`）只推进输出边界，不推进仿真时间。
* `PMProjectileHistory.Record` 对仿真时间只做**回退拒绝**（`sample.TotalSimTimeMs < newest.TotalSimTimeMs` → `totaltime-regression`）；
  **相等是合法**的。`WorldTimeMs` 同理（宿主用同一只单调墙钟）。
* 帧锚新鲜度窗口 `AnchorAgeMs(600) + extraDefer` 也是按仿真时间算；契约明令**禁止**「帧号 × 16ms」换算
  （`net-r0-contract.md` D-R0-19）。

**判定**：**成立**。无输入帧让仿真时间停在原地只会让锚新鲜度判定**更宽松**（age 保持 0），
不会产生 `Record` 失败，也不会把真实命中判掉。故仅补注释说明口径（L2225），**不改代码**。

### F4 固定步 max8：elapsed 不双计 / 循环中失效中止

**代码事实（不双计）**：`elapsedMs = nowMs - _lastProjectilePumpMs` 每帧只算一次、只累加一次；
每个子步恰好 `-= stepMs`；`_lastProjectilePumpMs = nowMs` 先于循环；
超出 `16 × 8 = 128ms` 的余量钳到上限并计入 `droppedMs`。⇒ 一帧推进量 ≈ elapsed，**不双计**。

**修复（加固）**：循环内新增 `if (_faulted) { return; }`（L2127），使「宿主在本轮子步中途已失效」
（例如某一子步触发了 `Fail`）**立即中止**，不再做剩余子步。`PumpProjectileOnce` 仍按 `driver.IsFaulted` 退出。
零子步 `Pump(0)` 分支不变（仍排空声明入站）。新增注释固化「elapsed 只加一次」的口径（L2116–L2118）。

### F5 ctor 失败与 Dispose 顺序

**原本已正确的部分（复核结论，非改动）**

* `Dispose()` 中 `ReleaseProjectileWiring()` 位于 `PMR3Runtime.Detach(_world)` **之前**、`_bridge.Dispose()` **之前**，
  满足「释放发生在 world/bridge 仍完整接线」；次序为 **Driver → Motion →（更后面的）地图/场景**，符合契约。
* `ReleaseProjectileWiring` 幂等，且被 `Initialize` 的失败路径调用。
* 断线路径为 `Freeze + Dispose`（先冻结再释放）。

**缺口（已修）**：`CreateProjectileWiring` 的三条内部失败路径里，
「接线不完整」（`HostMotion/History/Policy` 任一为 null）这条在 **driver 已构造且已订阅** 之后 `return false`，
把清理责任完全推给调用方/`Dispose`；另外两条（motion ctor 抛、driver ctor 抛）各自手写零散半清理。
⇒ 现在三条失败路径统一 `ReleaseProjectileWiring()` 自清理（L1999 / L2014 / L2026），
保证「任何构造成败点都不会留下半成品接线 / 悬空订阅」，且与调用方的二次调用幂等共存。

**未变**：`_movementAllowlist` 仍只持引用副本，地图/隔离场景的释放仍由原有尾部逻辑负责（本接线不持场景所有权）。

### F6 policy 单调 activation：同 activation 的重传不得反向撤已确认弹（本轮最重项）

**证据链（为什么策略层无解）**

1. 契约要求策略层拒绝「来源 activation 单 owner 单调且不重用」——现实现用
   `_projectileLastActivationByOwner` 做到（L2532 起），单调检查本身**必须保留**。
2. driver 对**策略拒绝**的处理是**无条件**下行一条 `Rejected` 裁决
   （`PMR5ProjectileDriver.HandleServerSpawn`：`SendDecisionFor(..., PMActivationResult.Rejected, "policy")`），
   只有**账本**受 `TrySetActivationResult` 的 `AlreadyTerminal` 保护（不会把 Confirmed 改成 Rejected），
   **线上裁决照样发**。
3. 试图用「策略返回 true」绕开也不行：同一 key 的重复登记在生命周期被判 `DuplicateId`
   → 准入 `AlreadyAdmitted` → driver 走的是 `!admission.IsAdmitted` 分支，
   **同样**发 `SendDecisionFor(..., Rejected, "admission-...")`；更糟的是这会额外预留/取消一个 NetId。
4. 客户端对同 key 的 `Rejected` 会 `RevokeFake` → `Lifecycle.Retire(key)`
   ⇒ 一条**已 Confirmed** 的弹被判为「被驳回」（终态被反向）；此后该 key 的镜像更新会命中
   `_retiredSet` → `TryApplyMirror` 返回 `Retired` → 表现停更（客户端可见瑕疵）。

**判定**：真实缺口；**唯一**既不重复生成、又不发第二条 Rejected 的落点是
「**别让完全相同的那条重传走到 driver**」。

**修复（宿主侧上行去重门）**

* 新增 `DiagnosticProjectileUplinkGate`（L2792）：实现声明层接缝 `IPMProjectileNetworkDriver` 的**装饰器**，
  在 `OnConnected` 里 `BindPlayer` 之后装上（`InstallProjectileUplinkGate` L2440 / 调用点 L1634）。
* 去重口径严格限定为**三连完全相同**：`(认证 owner 由接缝携带, activationId, projectileId)`。
  命中载荷与下行裁决**原样转发**（不参与去重）。
* ⇒ 完全相同重传：**在投递给 driver 之前被抑制**，因此不会产生第二条 `Rejected` 裁决，
  已确认弹不会再被反向撤销；换了一个 `projectileId` 的新尝试仍照常投递 → 策略单调拒绝（**新 key 的新假弹应当被撤**，语义正确）。
* 有界性：每 owner 环形记住最近 32 条已转发意图（重传紧跟原包），每玩家一份，玩家数由名册上限约束。
* 可观察：`uplinkDuplicates` 计数 + 限流告警（不静默）；`Describe()` 暴露 `uplinkGates`。

**与 driver 的交互（必须登记，已由宿主补偿）**：门是 `player.ProjectileDriver` 的**当前值**，
而 `PMR5ProjectileDriver.UnbindPlayer` 用 `ReferenceEquals(player.ProjectileDriver, adapter)` 判定是否置空该字段 ——
门在场时该判定不成立，因此宿主在**断线路径**（L1335）与**释放路径**（`ClearAllProjectileUplinkGates` L2467，L2080）
自行置空/清表。`IsPlayerBound` / `BoundPlayerCount` / `_adapters` 完全不受影响（driver 内部账本从未被门改变）。

**残余（诚实）**：若某条完全相同的重传晚于 32 次其它开火之后才到达（远超正常重传窗口），门会放行，
届时仍会出现上述「第二条 Rejected」；根治点是 driver 侧（见 §6 建议）。本轮不改 driver。

### F7 Sender Fault 必须 Fail 整个 host

**复核结论：原本已满足，本轮统一并加固。**
`PMR3Runtime.SendRemoteRpc` 对三条投射物 RPC（按 `(ClassId, RpcId)` 判定）失败时**抛异常**；
`PMNetGeneratedRegistry.EnqueueRemote` 在 `RemoteSender` 已接线时是**同步**调用 ⇒ 异常在调用栈内抛出，
driver 捕获后置 `IsFaulted/FaultReason/FaultError`；宿主在 `PumpProjectileOnce`（L2154）检测到
`driver.IsFaulted` 即 `Fail(...)` → `ExitRequested(1)`（`PMDsHost` 以非 0 退出）。链路无「只 log 仍继续」。

**加固**：
* 抽出唯一判定点 `FaultHostIfProjectileDriverFaulted(where)`（L2177），`PumpProjectileOnce` L2167 与
  `PumpProjectiles` 入口 L2102 都走它 —— 后者覆盖「上一帧或任何非投射物 Pump 路径造成的 driver fault 未被消费」的情况。
* 配合 F4 的中途失效中止，一旦宿主已 `Fail`，本轮不再继续驱动 driver。

### F8 Settlement 诊断计数只能一次

**证据（双重计数的真实成因）**：driver 的 `DrainCoordinatorOutlets` 把结算**要么**交给 `Settlement` 订阅者
（`SettlementsHandedToR6++`），**要么**（订阅者不存在**或抛异常**）放进有界待取缓冲 `_pendingSettlements`。
宿主原本在 `OnProjectileSettlement` 里**先加计数**、**再**打日志；
若日志（`Info` → `HYLDDebug` / 宿主 `Log` 回调）抛异常，driver 会判为「未交付」而入缓冲，
宿主的兜底排空 `DrainDiagnosticSettlementBuffer` 又调一次 `OnProjectileSettlement` ⇒ **同一条结算计两次**
（`settlementCount` / `hitCount` / `hits` 全被放大）。

**修复**：计数与日志**分离**——`OnProjectileSettlement`（L2260）只负责计数，并把日志调用
`LogProjectileSettlement`（L2286）包在 `try/catch` 内**吞掉日志异常**（新增 `settlementLogFailures` 计数 + 限流告警）。
这样订阅者永远向 driver 交付成功，driver 不会把已计数的条目再入缓冲 ⇒
「事件 + drain 同一条重复计数」在构造上不可能发生。兜底排空路径保留（真正无订阅者时的唯一合规出口），
并补注释写明「事件 XOR 缓冲」的交付不变式。

---

## 4. 新增可观察量（都进 `Describe()` 的 `projectile=` 段）

`viewDrains`、`disconnectSamples`、`uplinkDuplicates`、`settlementLogFailures`、`unconsumedSettlements`、`uplinkGates`
（原有 `faulted/bound/authority/reserved/views/historyTargets/historyRejects/policyRejects/filterRejects/settlements/hits/droppedMs` 不变）。

---

## 5. 未改动项（有意）

* **策略层 activation 单调 + 200ms 墙钟间隔**：契约硬要求，**不动**（F6 的冲突在「重传抑制」层解决，而不是放宽策略）。
* **`Confirmed` verdict**：本批仍无资源/HP 门，没有 `Pending` 的理由；`ResolveActivation` 显式后续入口仍未被宿主使用。
* **history 采样七元组**：字段与来源全部不变（`Teleported=false` / `Alive=true` 常量仍在正常采样路径；
  仅断线补写那一条用 `Alive=false`）。
* **不创建表现对象**：`PMUnityProjectilePresentation` 在本文件出现 0 次。

---

## 6. 契约级根因与建议（不在本批写入边界）

F6 的根因在 driver：`HandleServerSpawn` 的**策略拒绝**与**准入失败**两条分支都无条件
`SendDecisionFor(..., Rejected, ...)`，其前置只查「账本终态」（`TrySetActivationResult` 的 `AlreadyTerminal`），
不查「这条 Rejected 是否会反向撤销一个**已 Confirmed 的同一 key**」。建议的 driver 侧收口（供后续批次评估，本批不改）：

1. 在 `SendDecisionFor` 里对 `result == Rejected` 增加一次账本查询
   （`_coordinator.Lifecycle.TryGetActivationResult(ownerNetId, activationId, out outcome)`，该 API 已公开存在）；
   若该 activation 已是 `Confirmed` → **不下行 Rejected**（幂等吸收），只计数 + 告警。
2. 或对「该 key 已 spawned/authority」的重复上行直接返回（不产生任何裁决）。

两条都能把 F6 从「宿主边界去重」升级为「不变量级保证」，且不再依赖 32 条环形记忆的窗口假设。

---

## 7. 已检查范围

**按委派顺序完整读取的文档**：
`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`（全文，重点 §1.1 进程形态唯一来源
`PMNetRuntime.IsDedicatedServer`、§4 子弹/表现分层、§6 状态所有权）→ `Docs/plans/net-r5-network-contract.md`
（全文 33 行，重点末段「C宿主首个可执行入口」）→ `Docs/plans/_r5_ds_host_report.md`（全文 397 行）
→ `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`（全文）。

**文档如何决定本次复核入口**：C 段冻结了接线的时序/累积器/策略检查项/过滤口径/结算口径/释放顺序，
因此复核逐条对齐它；`_r5_ds_host_report.md` 给出「已接线」的自述与 U1–U12 诚实边界（含 U3 `Alive` 常量、
U2 Pending 未使用），本次 F2/F3/F8 正是对它自述边界与实现的交叉验证；
`Client/Assets/AGENTS.md` 明确 DS 身份判据与「HP/死亡属服务端权威」，因此 F2 只在**命中可性**层面收口、
不引入任何生命值语义。

**只读读取的生产代码（用于确认真实 API/语义，不信旧报告假设）**：
`Server/Boot/PMDsSessionHost.cs`（被改对象，全文）、`PMR3/PMR5ProjectileDriver.cs`（全文：
`HandleServerSpawn` 的 4 条 Rejected 出口、`RebuildViews`/`MarkViewDirty`/`DrainViewChanges`/`CopyViews`、
`DrainCoordinatorOutlets` 的「事件 XOR 待取缓冲」、`DrainSettlements`、`BindPlayer`/`UnbindPlayer`/`CleanupOwner`、
`Dispose`、`MaxDirtyViews`、`SendDecisionFor`）、
`PMProjectile/PMProjectileCoordinator.cs`（`RequestSpawn` 全链路与 `MapRegisterFailure`/`PromoteReservedOrFail`、
`ResolveActivation`/`ReportHits`、`EnsureOutputHeadroom` 与 Fault 语义、`AdvanceMotion`/`CatchUpMotion`/
`CatchUpRegisteredMotion`、`PurgeExpired`/`ClearOwner`、四个 Drain）、
`PMProjectile/PMProjectileHistory.cs`（全文：`Record` 校验顺序与「相等合法」、`TryResolve` 三档退化、
`Prune`、`Clear`/`Reset`、无按目标移除 API）、
`PMProjectile/PMProjectileLifecycle.cs`（`TryRegisterPredicted`/`TryRegisterAuthority`/`CreateEntry` 的
`DuplicateId`/`NonMonotonicId`、`TrySetActivationResult` 的 `AlreadyTerminal` 与终态记忆、
`TryApplyMirror` 的 `_retiredSet → Retired`）、
`PMProjectile/PMProjectileValidator.cs`（`Alive` 跳过点与 `Filter.Accept` 调用顺序）、
`PMR3/PMR4MovementDriver.cs`（`_authorityTotalSimTimeMs` 只在 `StepMs != 0` 累加、`AuthorityTotalSimTimeMs`/
`StreamVersion`/`OutputBoundary`/`IsFrozen`/`IsDisposed`/`GetAuthoritativeSync`）、
`PMR3/PMR3Player.cs`（`IPMProjectileNetworkDriver` 三方法、`ProjectileDriver` 字段、
`ServerProjectileSpawnV1`/`HitV1`/`ClientProjectileDecisionV1` 的接缝调用、`ProjectileMaxPayloadBytes`）、
`PMR3/PMR3Runtime.cs`（`SendRemoteRpc` 的 `(ClassId, RpcId)` 投射物判定与抛异常策略）、
`PMR3/Generated/PMNetGeneratedRegistry.g.cs`（`EnqueueRemote` 同步调用 `RemoteSender`）、
`PMR3/Generated/PMNet.PMNet.R3.PMR3Player.g.cs`（`PMNet_ClientProjectileDecisionV1` 的 callspace/长度门）、
`PMNet/PMNetIdentity.cs`（`PMFrameDomain` / `PMFrameId.IsValid` 与默认值语义）、
`Server/Boot/PMDsHost.cs`（`Update` → `PMDsSessionHost.Pump` 的唯一驱动点与 `ExitRequested` → `Application.Quit`）、
`Tools/PMR4UnityCheck/PMR4UnityCheck.csproj`（确认宿主被真实 Unity DLL 编译面覆盖）。

**并行组瞬态（仅对账，未改）**：本组复核期间 `PMR5ProjectileDriver.cs`（mtime 17:41）、
`PMClientSessionHost.cs`、`PMProjectileDiagnosticConfig.cs`、`PMProjectileCandidateCollector.cs`、
`Docs/plans/net-r5-network-contract.md` 等属其它工作流；本组**一行未改**，且本组 F6/F1 结论已按
**当前** driver 内容（`MarkViewDirty` 自清、策略拒绝无条件发 Rejected）逐行核对。

**本次写入的文件（仅此 2 个）**：`Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs`（唯一代码改动）、
`Docs/plans/_r5_ds_host_review.md`（本报告）。

---

## 8. 未执行实机与诚实边界

| # | 项 | 说明 |
|---|---|---|
| V1 | **未启动 Unity** | 按「先代码后实机」口径：本批不标实机 PASS、不声称 R5-C 完成或 T45 完成。 |
| V2 | 未做真实双连接网络测试 | 契约要求的「owner/AP + observer/SP 两连接、丢包/重复/错 owner/错 epoch/决策先于 Create/晚镜像/停止与 Destroy 序」**未跑**。F6 的门只做了**静态推演**（重传被抑制 → 无第二条裁决），实机需断言「客户端假弹在重传后不被撤销」。 |
| V3 | 未量化 F1 排空的 CPU/GC | 排空量为本帧真实脏数（DS 探针数量级很小）；未在多弹压测下测量分配。 |
| V4 | F2 未做真实断线回放 | 「断线后旧帧锚不得命中」只在代码层收口（补样本 + 实时连接过滤）；需实机/回放验证 `TargetNotAlive` 与 filter 拒绝计数确实增长。 |
| V5 | 未压多 owner 并发 | `SaturationFailures`（C1 的 32 命中缓冲）、`MaxInbound*` 队列上限、`ViewDirtyOverflows` 在真实负载下的表现未量化。 |
| V6 | 未跑 `PMUnityGlueCheck` / `PMClientCheck` | 见 §2：避免与并行 client 组共享 `bin` 互相污染，属未执行项。 |
| V7 | F6 的残余窗口 | 门只记 32 条已转发意图；超窗口的同载荷重传仍可能触发第二条 Rejected。根治点在 driver（§6）。 |
| V8 | 客户端宿主未接线（他组） | 本组只读 `PMR5ProjectileDriver.cs` 与声明层；未读/未改 `PMClientSessionHost.cs` 实现细节。 |
| V9 | 无 R6 | 结算仍是诊断计数 + 日志，`hits` **不是**扣血次数；未引入生命值/资源/死亡语义。 |
