# 独立对抗审查：复制调度「可见性过滤」的剩余缺口 + 顺延期间的可见性丢失

**任务**：对上一轮轮询调度修复（`_property_polling.md`）做独立对抗审查；先用反例核实主侧怀疑，再只修**确认的缺陷**，
并把「不可见项不触发 Writer / 不长期吃预算」「失去可见性必须记住、重新可见必须补发」钉成可执行断言。
不碰生成器/编织器/其它生产类。

**结论（本轮实现范围内）**：

1. `PMReplicationTest` 干净实现 **364 通过 / 0 失败**（原 317 条**逐条保留**，新增 47 条）。
2. 缺陷注入 **14 个中命中 14 个**：原 F1..F11（含原期望前缀）全部命中，新增 F12/F13/F14。
3. **确认并修复两个真实缺陷**（都在上一轮「未知基线按可见性过滤」之外的**同一处剩余缺口**）：
   - **D-A**：`state.ForceIncludeCount > 0` 与 `obj.Dirty.HasAny` 是**对象级**触发源，仍不分可见性
     ⇒ 一个挂在「对这条连接永远不可见」槽位上的标记，会让该对象**终身每轮**占用这条连接的调度预算。
   - **D-B**：「满足 → 不满足」只在进入 `ScanAndAppend` 时才写 `ConditionActive`；预算耗尽的轮次**顺延不进扫描**
     ⇒ 这次丢失只活在本轮 `_condScratch` 里、下一轮被覆盖 ⇒ 条件在顺延期间变回满足时**漏强制补发**
     （T-c2 反例里客户端**永久**停在旧值）。
4. **游标规则：审了、用反例压了（T-d 对象表增删/下标整体位移），未发现缺陷，因此没有改**（诚实结论，不假装改了）。
5. 上游回归（本次 build0 后 run0）：`PMNetE2E 209/0`、`PMR6DeclarationTest 235/0`、`PMR6NetworkTest 385/0`、
   `PMNetSessionTest 405/0`、`PMR4NetworkTest 389/0`、`PMR5NetworkTest 417/0`、`PMR3IntegrationTest 143/0`、
   `PMDsControlTest 623/0`、`PMDsLobbyTest 268/0`、`PMR3RuntimeTest 180/0`；编译门 `PMNetLangCheck` /
   `PMClientCheck` / `PMUnityGlueCheck` / `PMR4UnityCheck`（后两者用真实 Unity2019 DLL）**0 错 0 警告**。

---

## 0. 硬边界（本轮实际遵守）

| 项 | 实际 |
|---|---|
| 本轮实际改动 | `Client/Assets/Scripts/PMNet/Replication/PMReplicationChannel.cs`（+99/-2，相对上一轮基线）、`Tools/PMReplicationTest/Program.cs`（+616/-1）+ 本报告 |
| 未改 | `PMRepConnectionState.cs` / `PMReplicationTypes.cs`（在允许清单内，但本轮**不需要**改） |
| 生成器 / 编织器 / 生产其它类 | **未改**（`Tools/PMNetGen/**`、`Tools/PMNetWeaver/**`、`PMR3/Generated/**`、`World/**`、`PMNetObject/Property` 均未触碰） |
| 沙盒旧版文件 | **未创建** —— comprehensive 硬写入边界只允许 4 个具体文件 + 本报告，因此「旧实现 vs 修后」对照走**受保护的 virtual 缺陷子类**（F12/F13/F14，与 F1..F11 同一机制、同一纪律），不在生产文件里做无保护注入 |
| 其它工程源码 / 生成物 | 只读构建/运行；`Tools/PMNetE2E/Generated/*`、`Client/Assets/Scripts/PMR3/Generated/*`、`PMNet/Generated/*` 运行前后 **MD5 逐文件一致**，`git status` 前后 **无差异** |
| 运行期反射 | 未引入（仍静态描述符表） |
| Unity / 服务 / git 写 / 递归委派 | 未启动 Unity、未启停服务、未提交/未 `git add`/未 svn、未调用任何 delegate |

改动量（`git diff --numstat`，含上一轮在该文件上的已存在改动）：

```
11      0   Client/Assets/Scripts/PMNet/Replication/PMRepConnectionState.cs   （上一轮）
345    18   Client/Assets/Scripts/PMNet/Replication/PMReplicationChannel.cs    （上一轮 246/16 + 本轮 99/2）
11      0   Client/Assets/Scripts/PMNet/Replication/PMReplicationTypes.cs      （上一轮）
1331    1   Tools/PMReplicationTest/Program.cs                                 （上一轮 715/1 + 本轮 616/0）
```

编码：改动的两个中文源码 + 本报告均 **UTF-8 BOM + CRLF**（写盘后逐文件复核 `bom=True lone_lf=0`）。

---

## 1. 已读文档与固定入口（按指定顺序）

1. `D:/UGit/hyld-master/AGENTS.md`（完整）
2. `Client/Assets/AGENTS.md`（完整）
3. `Server/AGENTS.md`（完整）
4. `Docs/plans/net-architecture-migration.md` 文末「自动属性复制开工」段（T-P1..T-P6 表 + 冻结口径）
5. `Docs/plans/net-property-authoring-contract.md`（完整，重点 **§3 轮询调度修复** 原文三条：
   「不可见 OwnerOnly/Never 不能因未知基线占预算」「失去可见性必须记录、重新可见强制补发」
   「每连接预算公平推进、Round-robin 游标」）
6. `Docs/plans/_property_polling.md`（上一轮报告，完整；本轮以它声明的「已修五轴」为**待验证的假设**，不当作事实）
7. `Docs/plans/net-r2-codegen-contract.md`（完整，重点 §9 独立审查修订：注册键/接收路径/掩码/生命周期/计数口径）
8. `Docs/plans/windows-shell-compat`（`C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`，完整）

被文档路由到、与本项直接相关的实现入口（文档决定首搜范围，不做全项目盲搜）：

- `Client/Assets/Scripts/PMNet/Replication/`（复制层；契约 §3 明确「与生成/编织可独立」）
- `Client/Assets/Scripts/PMNet/PMNetProperty.cs`（`PMDirtyTracker` 位宽 256 / `HasAnyBelow` / `ClearBitsAbove`）
- `Client/Assets/Scripts/PMNet/PMNetObject.cs`（`Dirty` 与 `MarkPropertyDirty` / `FlushNetDormancy`：**唯一**的标脏入口）
- `Tools/PMReplicationTest/**`（R2 B 块门禁：手写描述符，不依赖生成器）

首搜结论（用于确定「谁在消费脏位」）：全 `Client/Assets/Scripts` 内 `Dirty.HasAny` 的**生产**消费点只有
`PMReplicationChannel.NeedsWork`（1 处），`IsDirty` 只有 `TryClearSettledDirty`（1 处）—— 这决定了
D-A 的修法可以只动调度判据、不必动脏位本体的读写。

---

## 2. 缺陷 D-A：对象级触发源（`ForceInclude` / 脏位）不分可见性

### 2.1 旧行为（代码级证据）

上一轮把「未知基线」按可见性过滤了，但**同一条纪律没有覆盖另外两个对象级触发源**：

```csharp
// 上一轮的 NeedsWork 尾部（缺陷形态）
if (state.UnknownBaselineCount > 0 && HasVisibleUnknownBaseline(entry, state)) return true;  // ← 已按可见性过滤
if (state.ForceIncludeCount > 0) return true;   // ← 不分可见性（计数是整对象/整连接的聚合）
if (obj.Dirty.HasAny)            return true;   // ← 不分可见性（脏位是**整对象共享**的）
```

两者都是**对象级**的，不区分连接：

- `state.ForceInclude[slot]` 由 `SetCustomConditionActive` / `SetDynamicCondition` 经
  `RequireSendOnAllConnections` **一次写给所有连接**；而 `ScanAndAppend` 对 `!met` 的槽位直接
  `continue`（在 `ClearRequireSend` 之前），所以「槽位不可见」时这个标记**永远清不掉**。
- `PMNetObject.Dirty` 是整对象一份的 `PMDirtyTracker`，所有连接看到同一批脏位。

于是两条真实路径都会让**非拥有者连接**终身每轮占预算：

1. **owner-only 字段服务端频繁变脏**（业务正常行为）：该对象对每个连接都「有脏位」，
   即使这条连接根本看不到那个槽位。
2. **`Dynamic` 条件被改写成 `Never`**：`SetDynamicCondition` 的 `RequireSend` 留下的
   `ForceInclude` 挂在不可见槽位上，永远清不掉。

**为什么「轮转」不能算修复**：上一轮补的 `UseFairBudgetRotation` 只改变「先给谁分配预算」，
被顺延的对象下一轮会被轮到 —— 但 `DeferredByBudget` 仍然每轮增长，且对象级后果可观测：
预算 1 + 两个对象时，真正有修改的那个对象**只能每隔一轮**被处理（T-a7/F12 实测 `A=102` 而不是 `103`）。
轮转把「永久饥饿」压成「周期性饥饿」，并没有消除「不可见项吃预算」。

### 2.2 修复（最小可见性候选）

`NeedsWork` 的逐槽位扫描本来就为「条件 / 轮询」算好了本轮可见性 `_condScratch`，因此**复用同一份结果**即可，
不引入新的每轮全表扫描：

```csharp
bool visibleForce = false;   // met && state.ForceInclude[slot]
bool visibleDirty = false;   // met && obj.Dirty.IsDirty(slot)
...
bool filterByVisibility = entry.HasConditional && ShouldFilterForceAndDirtyByVisibility();

if (state.ForceIncludeCount > 0 && (!filterByVisibility || visibleForce)) return true;

if (obj.Dirty.HasAny && (!filterByVisibility || visibleDirty || !obj.Dirty.HasAnyBelow(slotCount))) return true;
```

- **不改任何标记本身**：脏位是对象级共享的，按某条连接在这里清掉会破坏
  `TryClearSettledDirty` 的「所有**可见**连接都已追平才清脏」不变式（会提前释放另一条连接尚欠的值）。
- 第三项 `!HasAnyBelow(slotCount)` 保留 `MarkAllPropertiesDirty()` / `FlushNetDormancy` 留下的
  `[SlotCount, 256)` **伪位**仍然算工作，好让对象进入一次扫描把它们清掉（RV4 的口径，已有 N6/N7/N11/N13 守着）。
- 对**没有任何条件属性**的对象 `entry.HasConditional == false` ⇒ `filterByVisibility == false` ⇒
  两条判断逐字退化成旧形态，**零额外开销**（也保证既有语义不被改动）。

### 2.3 反例（先失败、后通过）

| 断言 | 场景 | 修复前 | 修复后 |
|---|---|---|---|
| `T-a5` | `Dynamic→Never` 留下粘住 `ForceInclude`，预算 1、两个对象连跑 4 轮 | `DeferredByBudget +4`（失败） | `+0` |
| `T-a6` | 同上，断言该对象**不再进入扫描**（`ConditionFiltered` 不再增长 ⇒ Writer 不执行） | `+2`（失败） | `+0` |
| `T-a7` | 同上，第二个对象必须**每轮**都被处理 | `A=102`（失败，周期性饥饿） | `A=103` |
| `T-b4` | owner-only 字段服务端**每轮**变脏，预算 1、两个对象连跑 4 轮 | `DeferredByBudget +4`（失败） | `+0` |
| `T-b5` | 同上，第二个对象每轮都被处理 | `A=202`→失败 | `A=203` |
| `T-a9/T-a10` | `Dynamic` 改回 `None` 后，**按 NetId 过滤**断言该对象的 slot 1 被补发 | 旧语义下 `[]`（失败） | `[1]` |

> `T-a9` 之所以必须按 NetId 过滤：同一装置里多个对象共用同一张描述符（也就共用同一批条件属性），
> 「某个载荷里有 slot 1」会被**另一个对象**的同类槽位满足。本轮为此新增 `SentSlotsFor(conn, netId, ...)`
> 辅助函数；这个漏洞本身是首次写 `T-c1-9` 时真实踩到的（F13 曾因此**漏命中**），见 §3.5。

---

## 3. 缺陷 D-B：预算顺延期间「失去可见性」被丢掉

### 3.1 旧行为（代码级证据）

```csharp
// 上一轮：NeedsWork 只判出「有跃迁」，把记录留给真正扫描时做
if (RequiresEvaluation(prop.Condition) && IsVisibilityTransition(slot, met, state.ConditionActive[slot]))
{
    anyTransition = true;                 // ← 只置一个局部标志
}
...
return anyTransition;

// ScanAndAppend（只有**真的被调度**才会执行到这里）
if (entry.HasConditional)
{
    if (met && !state.ConditionActive[slot] && ...) state.RequireSend(slot);
    state.ConditionActive[slot] = met;    // ← "失去可见性"只在这里落库
}
```

而预算耗尽的轮次在 `BuildUpdate` 里是**直接顺延**：

```csharp
if (scheduled >= _options.MaxObjectsPerConnectionPerTick) { _stats.DeferredByBudget++; continue; }
```

于是「本轮判出的丢失」只活在 `_condScratch` 里，下一轮一开始就被覆盖。若条件在顺延期间变回满足，
`IsVisibilityTransition(met=true, wasActive=true)` 判不出跃迁 ⇒ **不补发**。
（上一轮注释写的「顺延期间不改 ConditionActive，因此条件跃迁会在下一轮被重新检出，不会漏」只在
「丢失状态**持续**到下一轮」时成立；对「丢失后很快恢复」不成立。）

### 3.2 修复（当场落库）

```csharp
anyTransition = true;

if (!met && ShouldCommitVisibilityLossUpfront())
{
    // 预算顺延的轮次不进 ScanAndAppend；这半条记录只有放进**每轮对每个对象都会执行**的
    // 调度判据里，才不会被顺延丢掉。
    state.ConditionActive[slot] = false;
}
```

「gain（不满足 → 满足）」方向**刻意仍然留给 `ScanAndAppend`**（那里才有 `ShouldForceIncludeOnTransition`
与落库顺序）：gain 被顺延是无害的 —— 只要条件仍是可见，下一轮会重新判出同一次 gain；即便期间又变回不可见，
下次真正 gain 时仍会被判出。**只有 loss 方向必须当场落库。**

### 3.3 反例一：同值、无脏位、视图关一轮再开（T-c1）

编排（预算 1、两个对象；用「轮转游标停在 X 之后」把 X 在这一轮挤出预算）：

| 步骤 | 动作 | 结果 |
|---|---|---|
| `T-c1-4` | 只有 X 需要工作 ⇒ X 必被处理，游标落到 X 之后 | `A=2` 送达 |
| `T-c1-5` | `role=Simulated`（失去可见性）+ 第二个对象变脏 | `DeferredByBudget +1`（**X 确实被顺延**） |
| `T-c1-8` | 断言「失去可见性」这个事实被记住 | 修复前 `ConditionActive=True`（**失败**）→ 修复后 `False` |
| `T-c1-9` | `role=Autonomous`（值仍是 5、无脏位） | 修复前该对象 `[]`（**失败**）→ 修复后 `[1]`（强制补发） |
| `T-c1-10` | 跃迁补发计数 | 修复前 `2 → 2`（失败）→ 修复后 `2 → 3` |

### 3.4 反例二：脏位被「外部 ACK 路径」结清 ⇒ 永久停在旧值（T-c2）

这是把 D-B 放大成**真实数据不一致**的构造（也是本轮最有力的一条）：

1. 三个对象、预算 1；X 的首个 owner-only 基线是 5（`role=Autonomous`）。
2. `T-c2-4`：X 的 `slot 0` 更新发出但**故意不投递**（确认落后，模拟真实在途）。
3. `T-c2-5`、`T-c2-6`：`role=Simulated` 起，X 连两轮被顺延（`DeferredByBudget` 各 +2）；
   期间 `B = 9` 并 `MarkPropertyDirty(1)`。
4. `T-c2-7`：投递全部在途载荷 ⇒ ACK 到达 ⇒ `TryClearSettledDirty` 里
   `IsSlotFullyConfirmed` 对**不可见**槽位直接 `continue`（「这条连接不要求拥有该值」），
   于是 slot 1 的脏位被当作**已结算**清掉（构造前提，旧/新实现都成立）。
5. `T-c2-8`：`role=Autonomous` 重新可见。此时该对象对这条连接**再无任何触发源**：
   基线非空（5）、脏位已清、非 Push、旧实现里 loss 从未被记录 ⇒ 不扫描 ⇒ 客户端**永久**停在 5。
   修复后：loss 在顺延轮就已落库 ⇒ 重新可见判出 gain ⇒ 强制补发 9 ⇒ `B=9`。

### 3.5 一次真实的「断言假阳性」修复

首版 `T-c1-9` 写成「载荷里出现 slot 1」而没有过滤 NetId，结果 F13（还原旧 loss 记录）
**漏命中**：`second` 与 X 共用同一张描述符，也含 owner-only slot 1，在同一个 regain 轮里由它自己
的 gain 补发把断言满足了（实测 `2 → 3` 的计数也来自它）。改成 `SentSlotsFor(conn, netId, ...)`
后 F13 命中 `T-c1-8/T-c1-9/T-c2-8`（3/3）。这条记下来是因为它说明：
**负向验证不过就是断言有洞**，本次正是靠它才发现洞。

---

## 4. 游标规则审查（结论：无需修改）

初始怀疑：游标是按 `_objects` **下标**存的，对象注销（`List.Remove` ⇒ 其后下标整体前移）与登记
（追加到尾部）会让下标位移，可能「永久跳过」某个对象。

**审查结论：没有发现缺陷，因此没有改游标规则。** 理由是结构性的 + 有反例支撑：

- 每轮都会对**全部**对象求 `NeedsWork`（预算只约束「是否进入扫描」），被顺延的对象其脏位 / `ForceInclude` /
  条件状态都原样留在原地；
- 游标只在「本轮确实调度过对象」时推进到 `lastScheduled + 1`，因此覆盖是**环上的连续推进**，
  不依赖对象下标稳定；`NormalizeObjectCursor` 在读取前按当前表长取模（越界不重置为 0）。

反例（`T-d`）：3 个常驻轮询对象 + 每轮「注销一个陪跑对象、再登记一个新对象」（表长在 4..5 之间抖动、
下标整体位移），预算 1：

| 断言 | 结果（修复后） |
|---|---|
| `T-d3` 最长**连续**顺延轮数（3 个被跟踪对象） | 实际 `3/3/3`，断言 `<= 6`（预算 1 × 表长 4..5，量级符合 `~N/B`） |
| `T-d4` 扰动结束后全部收敛 | `A=4242`（3/3 到位） |

诚实口径：`T-d3` 的界 `<=6` 是**该抖动模式下的实测界**，不是对任意增删模式的证明；
但「连续顺延」这个量本身就是饥饿的直接度量 —— 真出现永久饥饿时它会随轮数线性增长而不是停在 3。

---

## 5. 测试与结果

### 5.1 数字（本次 build0 后 run0）

| 项 | 结果 |
|---|---|
| `PMReplicationTest` 干净实现 | **364 通过 / 0 失败**（原 317 条**逐条保留**，新增 47 条） |
| `PMReplicationTest` 缺陷注入 | **14 个中命中 14 个**（F1..F11 原期望前缀不变 + F12/F13/F14） |
| 运行退出码 | 0 |

### 5.2 新增断言（T 节，6 个子用例 / 47 条）

| 子用例 | 钉死的行为 | 断言 |
|---|---|---|
| T-a 不可见槽位上的 `ForceInclude` 不占预算（`Dynamic→Never`） | 占预算、不进入扫描、第二个对象每轮照常、改回可见后按 NetId 补发 | T-a1..T-a11（11） |
| T-b 不可见槽位的脏位不占预算（owner-only 频繁变脏） | 预算 1 下 0 次顺延、第二个对象每轮 | T-b1..T-b5（5） |
| T-c1 顺延期间失去可见性必须被记住 | 该轮**确实被顺延**、事实被记录、同值无脏位也强制补发 | T-c1-1..T-c1-11（11） |
| T-c2 顺延 + 外部 ACK 结清脏位 ⇒ 重新可见仍必须补发 | 构造前提（脏位被清）+ 最终收敛到 9（否则永久停在旧值） | T-c2-1..T-c2-8（8） |
| T-d 对象表增删（下标位移）下游标不产生永久饥饿 | 基线建立、最长连续顺延有界、扰动后收敛 | T-d1..T-d4（4） |
| T-e ACK 落后 + 可见性切换（两条连接） | 旧 ACK 不推进水位、重新可见收敛、快连接不回退 | T-e1..T-e8（8） |

### 5.3 缺陷注入（负向验证）

| 注入 | 还原的旧行为 | 注入后失败数 | 期望被抓（命中） |
|---|---|---|---|
| F1..F11（原） | 前两轮记录的旧形态（旧 ACK / 跃迁不补发 / 标脏即发 / 逐属性回调 / 异常穿透 / 不采样 / 单向跃迁 / 未知基线恒工作 / 表头优先 / 先采样 / 旧轮询整块） | 逐一命中 | 原前缀全部命中 ✓ |
| **F12（新）** | `ShouldFilterForceAndDirtyByVisibility=false`（`ForceIncludeCount>0` / `Dirty.HasAny` 不分可见性） | 6 | `T-a5`、`T-a6`、`T-b4`（另带 `T-a7`、`T-a9`、`T-a10`）✓ |
| **F13（新）** | `ShouldCommitVisibilityLossUpfront=false`（失去可见性只在扫描时记录） | 3 | `T-c1-8`、`T-c1-9`、`T-c2-8` ✓ |
| **F14（新）** | **整块**还原本轮修复前的调度语义（两个轴同时取旧形态） | **9** | `T-a5`、`T-a6`、`T-b4`、`T-c1-8`、`T-c1-9`、`T-c2-8` 全中 ✓ |

F14 就是「旧实现 vs 修后」的失败证明（同一断言集合：旧语义 **9 条失败**、修后 **0 条失败**）。
全部注入都走**受保护的 `protected virtual` 决策点**（`Tools/PMReplicationTest/Program.cs` 内的缺陷子类），
生产文件里没有任何为测试开的口子。

### 5.4 上游回归（本次 build0 后 run0；独立输出目录 `Tools/<工程>/bin/property-polling-review`）

| 门禁 | 结果 | 与既有记录 |
|---|---|---|
| `PMNetE2E`（真实生成物） | **209 / 0** | 一致（主计划 209） |
| `PMR6DeclarationTest`（真实描述符：OwnerOnly 不泄资源/条件过滤） | **235 / 0** | 一致（上一轮 235） |
| `PMR6NetworkTest`（战斗字节闭环） | **385 / 0** | 一致（上一轮 385） |
| `PMNetSessionTest`（真实 bridge/传输/世界/RPC 全链） | **405 / 0** | 一致（上一轮 405） |
| `PMR4NetworkTest` | **389 / 0** | 一致 |
| `PMR5NetworkTest` | **417 / 0** | 一致 |
| `PMR3IntegrationTest`（真实 TCP/UDP + 进程替身） | **143 / 0** | 一致 |
| `PMDsControlTest` / `PMDsLobbyTest` / `PMR3RuntimeTest` | **623 / 0**、**268 / 0**、**180 / 0** | 项数高于主计划旧记录（556/230/171），非回归；0 失败 |
| `PMNetLangCheck` / `PMClientCheck` / `PMUnityGlueCheck` / `PMR4UnityCheck` | **0 错 0 警告** | `PMNetLangCheck` 通过即证明本轮改动满足 C#7.3 语言面；后两者用真实 Unity2019 DLL |

日志：`Tools/<工程>/property-polling-review.log`（`*.log` 已被 `.gitignore` 忽略）；
主门禁日志 `Tools/PMReplicationTest/property-polling-review.log`。

编织中间态：本轮构建 `PMNetE2E`（它带生成 RPC 集合、会走 `Tools/PMNetWeaver` 自动编织）**一次通过**，
未出现另一组工具中间态导致的阻塞，因此没有触发「如实记录暂阻塞」那条分支。

### 5.5 明确「未被弱化」的既有不变式（逐条对应既有断言，本轮全部仍绿）

- **基线 / ACK 单调**：B 节 `B1..B9` + `M` 节（无在途记录的 ACK 不盲目推进水位）→ 364 项全绿。
- **初始全量**：F 节（新连接 / 基线缺失 ⇒ 全量）。
- **无变化不发**：C 节（`D1..`/`C1..C3`）+ SA7/SA9。
- **OnRep 提交顺序**：O 节（整条记录提交完成后统一派发）+ P 节（回调异常隔离、已提交仍回 ACK）。
- **不可见槽位不执行 Writer**：SD3 + F10。
- **不可见槽位的未知基线不占预算**：SE2 + F8。
- **预算轮转**：SF3 + F9。

---

## 6. 本轮明确**不做**（不夸大）

- **不改游标规则**（审查后无缺陷，见 §4）——不为了让报告「有改动」而改。
- **不做逐槽位 push 优化**：`NeedsWork` 的过滤只决定「要不要进入本轮扫描」，扫描仍是整对象逐槽；
  「只比较脏槽位」属于另一项优化，本轮不动。
- **不做完整休眠 / 频率分档 / 相关性裁剪**：与上一轮一致，`IsDormant` 仍不参与调度（宿主分工未变）。
- **不动**生成器 / 编织器 / `PMR3.Generated` / 生产 auto-property 迁移（T-P1/T-P2/T-P4 属其它组）。
- **不动**任何标记本体（不在这里清脏位 / 不清 `ForceInclude`），避免破坏
  「所有可见连接都已追平才清脏」的不变式。
- 未启动 Unity、未做实机 / Player / IL2CPP 验证（T-P6 仍 PENDING_USER）。

---

## 7. 剩余风险（诚实口径）

| 风险 | 说明 / 建议 |
|---|---|
| **不可见标记会残留**（本轮修法的直接后果） | 对某条连接不可见的槽位：其脏位与 `ForceInclude` 不再被消费，也就不会被这条连接清掉。层内无副作用（没有任何别的生产代码用 `Dirty.HasAny` 做调度：全 `Client/Assets/Scripts` 只有本文件 2 处消费），但**外部观察者**若把 `Dirty.HasAny` 当作「必须扫描」会误判。若要彻底干净，应另起一项做「按可见性集合收敛标记」——那需要同时改 `TryClearSettledDirty` 的判据，风险高于收益。 |
| 全槽位恒不可见的条件对象 | 若某对象的**所有**条件槽位对**所有**连接都不可见（例如全 `Never`），它的脏位会一直挂着且对象不再被这条纪律驱动；语义上不欠任何连接，但没有「结算」时机。 |
| `TransitionForceSends` 计数含首次扫描 | 条件槽位的 `ConditionActive` 初值为 false，因此对象**第一次**被扫描时就会算一次「gain 补发」（值本来也要全量发，行为无差异，只是诊断计数偏大）。已有断言用「按 NetId 的载荷」而不是裸计数，避免被它误导（§3.5）。 |
| `T-d3` 的界是实测界 | `<=6` 是本轮抖动模式（表长 4..5、预算 1）下的实测值，不是任意增删模式的证明；真正的守卫是「连续顺延」这个量随轮数线性增长的形态。 |
| 轮询常态开销 | 与上一轮一致：含「可见且非 Push」属性的对象每轮进入整对象比较；本轮不引入频率分档。 |
| 实机 | 编译 + 受控测试通过**不等于**真实双客户端/弱网验收；T-P6 仍后置。 |

---

## 8. 复现命令

```bash
# 主门禁（独立输出目录，先 build0 再 run）
dotnet build Tools/PMReplicationTest/PMReplicationTest.csproj -c Release -o Tools/PMReplicationTest/bin/property-polling-review
dotnet Tools/PMReplicationTest/bin/property-polling-review/PMReplicationTest.dll
# 退出码 0 = 干净实现 364/0 且 14/14 注入命中

# 上游回归（同一模式，逐工程）
dotnet build Tools/<工程>/<工程>.csproj -c Release -o Tools/<工程>/bin/property-polling-review
dotnet Tools/<工程>/bin/property-polling-review/<工程>.dll
# <工程> ∈ PMNetE2E / PMR6DeclarationTest / PMR6NetworkTest / PMNetSessionTest /
#          PMR4NetworkTest / PMR5NetworkTest / PMR3IntegrationTest / PMDsControlTest /
#          PMDsLobbyTest / PMR3RuntimeTest
# 编译门（只构建，类库式无 runtimeconfig）：PMNetLangCheck / PMClientCheck / PMUnityGlueCheck / PMR4UnityCheck
```

（本轮在 Windows + `.NET SDK 10.0.302` 下执行；未启动 Unity、未启停任何服务、未提交、未写 git。）
