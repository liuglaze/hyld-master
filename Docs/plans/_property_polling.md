# 轮询（PushBased=false）复制调度修复报告

**任务**：独立修复属性复制层的轮询调度，并补"先证明旧实现失败"的真行为反例。不依赖自动属性编织（生成器/编织器由其它组负责，本轮不写）。

**结论（本轮实现范围内）**：`PMReplicationTest` 干净实现 **317 项通过 / 0 失败**，缺陷注入 **11 个中命中 11 个**；
原有 **265/0 + 5/5** 检测力完整保留（旧断言与旧注入逐条未弱化）。其余 7 个消费复制层的门禁全部与主计划记录的项数一致。

---

## 0. 硬边界（本轮实际遵守）

| 项 | 实际 |
|---|---|
| 改动文件 | 仅 `PMReplicationChannel.cs`、`PMRepConnectionState.cs`、`PMReplicationTypes.cs`、`Tools/PMReplicationTest/Program.cs` 四个（+ 本报告） |
| 生成器 / 编织器 | **未改**（`Tools/PMNetGen/**`、`Tools/PMNetWeaver/**` 未触碰） |
| PMR3 生成物 | **未重新生成**、未改（`PMR3/Generated/**`、`pmnet-ids.json` 未触碰） |
| 运行时反射 | 未引入（仍是静态描述符表） |
| 其它工程 | 只读构建/运行用于回归，未改其源码 |
| Unity / 服务 / git 写 | 未启动 Unity、未启停任何服务、未提交/未 `svn`/未 `git add` |

改动量（`git diff --numstat`）：

```
11      0   Client/Assets/Scripts/PMNet/Replication/PMRepConnectionState.cs
246    16   Client/Assets/Scripts/PMNet/Replication/PMReplicationChannel.cs
11      0   Client/Assets/Scripts/PMNet/Replication/PMReplicationTypes.cs
715     1   Tools/PMReplicationTest/Program.cs
```

编码：四个中文源码 + 本报告均 **UTF-8 BOM + CRLF**（写盘后逐文件复核 `bom=True lone_lf=0`）。

---

## 1. 已读文档与固定入口（按指定顺序）

1. `D:/UGit/hyld-master/AGENTS.md`（完整）
2. `Client/Assets/AGENTS.md`（完整）
3. `Server/AGENTS.md`（完整）
4. `Docs/plans/net-architecture-migration.md` 文末「自动属性复制开工」段（T-P1..T-P6 表 + 冻结口径）
5. `Docs/plans/net-property-authoring-contract.md`（完整，重点 §3「轮询调度修复」、§1 语义、§5 验收 T-P3）
6. `Docs/plans/net-r2-codegen-contract.md`（完整，重点 §9 独立审查修订：注册键/接收路径/掩码/生命周期/上层计数口径）
7. `Docs/plans/net-r0-contract.md`（§2.4 D-R0-12..18、§5 复制与生命周期契约：每连接基线 / ACK 只能前进 / 条件跃迁 / 调度预算 / 初始状态）

被 `AGENTS.md` 路由到、与本项直接相关的实现入口（文档决定首搜范围，不是全项目盲搜）：

- `Client/Assets/Scripts/PMNet/Replication/`（复制层四文件）—— 契约 §3 明确「与生成/编织可独立」
- `Client/Assets/Scripts/PMNet/PMNetProperty.cs`（`PMDirtyTracker` 位宽 256 与清位语义）
- `Client/Assets/Scripts/PMNet/Declarations/PMNetDeclarations.cs`（`PMPropertyDescriptor.PushBased` 的定义处）
- `Tools/PMReplicationTest/**`（R2 B 块门禁：用手写描述符测，不依赖生成器）
- `Client/Assets/Scripts/PMNet/Session/PMNetSessionBridge.cs`（真实宿主如何驱动 `Tick()`）

---

## 2. 缺陷与修复（真实证据）

### 2.1 改前行为（代码级证据）

改前 `PMReplicationChannel.NeedsWork` 只有四个触发源：

```csharp
if (state.UnknownBaselineCount > 0) return true;      // 未知基线
if (state.ForceIncludeCount > 0)   return true;      // 强制补发
if (obj.Dirty.HasAny)              return true;      // 脏位
return anyTransition;                                // 仅 met && !ConditionActive[slot]
```

而 `PMPropertyDescriptor.PushBased` / `PMLifetimeProperty.PushBased` 在 `Replication/` 目录下**运行期零消费**
（改前对 `Client/Assets/Scripts/PMNet/` 检索 `PushBased`，只命中 `Declarations/PMNetDeclarations.cs` 的定义与
`PMNetProperty.cs` 的注册项字段，`Replication/**` 0 命中）。于是 `PushBased=false` 声明的"业务直接写字段、
由复制层每轮比较"**从未生效**：只要业务不调用 `MarkPropertyDirty`，该属性的修改永不参与比较。

由此连带四个真缺陷（均已由新断言 + 缺陷子类钉死）：

| # | 缺陷 | 可观测后果 |
|---|---|---|
| D1 | 调度判据不含"可见且非 Push" | `PushBased=false` 的直接字段写静默不同步（无脏位 ⇒ 永不比较） |
| D2 | 未知基线只看计数、不看可见性 | 对某连接**永远不可见**的槽位（OwnerOnly 之于非拥有者 / Never）永远拿不到基线 ⇒ 该对象**终身每轮**占用该连接的调度预算 |
| D3 | `ConditionActive` 只在被扫描时才更新 | "满足 → 不满足"不被记录 ⇒ 条件再变回满足时判不出跃迁 ⇒ **重新可见的连接不补发**（值相同、无脏位时最明显） |
| D4 | `IsSlotFullyConfirmed` 先 `SerializeProperty` 再判可见性 | 对**完全不可见**的槽位也执行一次业务 Writer（委托可能有成本甚至副作用） |
| D5 | 每轮固定从对象表表头分配预算 | 补齐轮询候选后前缀对象常驻候选 ⇒ 后面的对象无限顺延（顺延无限次等价于丢弃） |

D2 的放大链条：`UnknownBaselineCount > 0` 恒真 ⇒ 每轮都 `ScanAndAppend` ⇒ `InitialFullSends` / `ConditionFiltered`
无界增长 + 每轮白跑一次整对象逐槽序列化。

### 2.2 修复点（5 个决策点，默认实现即契约）

| 决策点（新增/改写） | 默认实现 | 对应契约 |
|---|---|---|
| `ShouldPollProperty(int slot)` | `true` | 可见且 `PushBased=false` ⇒ 本对象每轮进入比较；发不发仍由基线字节比较决定 |
| `IsVisibilityTransition(int slot, bool met, bool wasActive)` | `met != wasActive` | 可见性**两个方向**都进扫描：gain 由 `ScanAndAppend` 置 `ForceInclude` 补发；loss 落 `ConditionActive=false` |
| `ShouldWorkOnUnknownBaseline(bool slotVisible)` | `slotVisible` | 只有**可见**槽位的缺失基线算工作（不可见槽位永远补不齐，不算） |
| `UseFairBudgetRotation()` | `true` | 每连接桶持 Round-robin 游标，超预算仍**顺延**，但起点轮转 ⇒ 无前缀饥饿 |
| `IsSlotFullyConfirmed(entry, slot)`（原 private → protected virtual） | 先判可见性、**惰性**取当前值 | 不可见槽位不采样（不执行 Writer）；"先采样再判可见性"是旧形态 |

配套实现细节：

- `PMRepObjectEntry.HasPoll`（`PMRepConnectionState.cs`）：在 `ResolveEntry` 里随 `HasConditional` 一起算，
  **缓存到每对象条目**，避免每轮重扫描述符。
- `PMRepStats.PollSampledSlots`（`PMReplicationTypes.cs`）：把"采样了但值没变"与"压根没调度"在统计上分开
  —— 否则"无变化不发"与"该发的没发"在数字上完全一样。
- `HasVisibleUnknownBaseline()`：只在 `UnknownBaselineCount > 0` 时才逐槽位确认可见性（稳态为 0，不进循环）。
- `NormalizeObjectCursor()`：对象表会在登记/注销间变化，游标读取前必须规约；越界取模，
  **不重置为 0**（重置会把轮转悄悄退化成前缀优先）。
- 未改 `ScanAndAppend` 的比较/暂存/OnRep 语义，未改 ACK/基线/在途/条件过滤/初始全量中的任何一条既有规则。

---

## 3. 测试与结果

### 3.1 数字（本次 build0 后 run0）

| 项 | 结果 |
|---|---|
| `PMReplicationTest` 干净实现 | **317 通过 / 0 失败**（原 265 条全部保留，新增 52 条） |
| `PMReplicationTest` 缺陷注入 | **11 个中命中 11 个**（原 F1..F5 保留原期望前缀） |
| 运行退出码 | 0 |

### 3.2 新增断言（S 节，8 个子用例 / 52 条）

| 子用例 | 钉死的行为 | 断言 |
|---|---|---|
| S-a 轮询直接字段写与无变化抑制 | 全基线 ACK + 脏位清空后，`A = 42`（不标脏）仍被采样发送并送达；无变化时**零载荷**但 `PollSampledSlots`/`SuppressedUnchanged` 增长 | SA1..SA9 |
| S-b 轮询与 Push 混合 | Push 标脏+改值照常发；轮询改动一定被带上；未改动槽位不被带上；并**如实断言**"轮询候选触发整对象扫描时会顺便发现未标脏的 Push 变化"（契约允许，不写成禁止） | SB1..SB5b |
| S-c 全 Push 对象仍然只认脏位 | 直接写字段不标脏 ⇒ 不调度、不发、`PollSampledSlots == 0`（与轮询对照，排除"整层退化成轮询"） | SC1..SC4 |
| S-d 不可见槽位不被采样 | 计数 Writer 证明不可见槽位 `writerCalls[1] == 0`，可见槽位正常；脏位不被卡住 | SD1..SD4b |
| S-e 不可见槽位不消耗每连接预算 | 预算=1、两个对象（一个含永远补不齐的基线、一个每轮真实修改）连跑 4 轮：`DeferredByBudget` **0 增长**且第二个对象每轮都被处理 | SE1..SE3 |
| S-f 预算轮转 | 预算=1、三个常驻轮询对象同时改值，在有限轮次内**全部**推进（实际 `[100,101,102]`） | SF1..SF3 |
| S-g 失去可见性 / 重新可见 | 失去可见性本身零载荷但 `ConditionActive=false` 被记录；重新可见时**值相同、无脏位也强制补发**并计数 | SG1..SG6 |
| S-h 两条连接 | 轮询式落后 5 版、旧 ACK 过期不污染、丢包通知归还容量、最终收敛、快连接不回退 | SH1..SH9 |

### 3.3 缺陷注入（负向验证）

| 注入 | 还原的旧行为 | 期望被抓（命中） |
|---|---|---|
| F1（原） | 旧 ACK 被接受 + 无在途也推进 + 无条件清脏 | B5 / B6 / B8 / B9 ✓ |
| F2（原） | 跃迁不补发 | E6 / E7 / E8 ✓ |
| F3（原） | 标脏即发不比较 | C2 / C3 ✓ |
| F4（原） | 每写一个属性立刻回调 | O4 ✓ |
| F5（原） | 回调异常穿透 | `[P.` ✓ |
| **F6（新）** | `ShouldPollProperty = false`（调度不看 `PushBased=false`） | **SA4 / SB3** ✓ |
| **F7（新）** | 只认 `met && !wasActive`（失去可见性不记录） | **SG3 / SG4** ✓ |
| **F8（新）** | 恒 `true`（不可见槽位未知基线永久占预算） | **SE2**（实际 +4 次顺延）✓ |
| **F9（新）** | 恒 `false`（每轮从表头分配预算） | **SF3**（实际 `[100,0,0]`）✓ |
| **F10（新）** | 确认前先采样（不可见槽位也执行 Writer） | **SD3**（实际 1 次）✓ |
| **F11（新）** | **一整块**还原补丁前的调度语义（五个决策点同时取旧形态） | SA4 / SB3 / SD3 / SE2 / SF3 / SG3 / SG4 ✓ —— 该轮共 **24 条断言失败** |

F11 是"旧实现对照"的关键证据：单个缺陷子类只证明"某一条断言抓得住某一个轴"，把五个轴同时还原后**成片失败 24 条**，
说明这些新断言测的确实是旧实现没有的行为，而不是靠某一条断言独扛。

### 3.4 其它消费本复制层的门禁回归（build0 后 run0）

| 门禁 | 结果 | 与主计划记录 |
|---|---|---|
| `PMNetSessionTest`（真实 bridge/传输/世界/RPC 全链） | **405 / 0** | 一致 |
| `PMNetE2E`（真实生成物 + 真实复制条件路径） | **209 / 0** | 一致 |
| `PMR6DeclarationTest`（真实描述符，OwnerOnly 泄资源/条件过滤） | **235 / 0** | 一致 |
| `PMR4NetworkTest` | **389 / 0** | 一致 |
| `PMR5NetworkTest` | **417 / 0** | 一致 |
| `PMR6NetworkTest` | **385 / 0** | 一致 |
| `PMR3IntegrationTest` | **143 / 0** | 一致 |
| `PMClientCheck` / `PMNetLangCheck` / `PMUnityGlueCheck` / `PMR4UnityCheck`（纯编译门，后两者用**真实 Unity2019 DLL**） | **0 错 0 警告** | 一致 |

> 说明：`PMClientCheck` / `PMNetLangCheck` 是**类库式编译门禁**（无 `runtimeconfig.json`），"直接运行"必然报
> `hostpolicy.dll not found`，因此它们只以**构建结果**记录，不产生运行日志（相关误导性日志已删除）。

日志（均在 `Tools/<工程>/property-polling.log`；`*.log` 已被 `.gitignore` 忽略，不影响 git 状态）：

```
Tools/PMReplicationTest/property-polling.log   （主门禁：317/0 与 F1..F11 逐条命中）
Tools/PMNetSessionTest/property-polling.log
Tools/PMNetE2E/property-polling.log
Tools/PMR6DeclarationTest/property-polling.log
Tools/PMR4NetworkTest/property-polling.log
Tools/PMR5NetworkTest/property-polling.log
Tools/PMR6NetworkTest/property-polling.log
Tools/PMR3IntegrationTest/property-polling.log
```

---

## 4. 本轮明确**不做**（不夸大）

- **不声称**数组元素原地改自动编织/自动标脏：本轮只保证"可检测"——`PushBased=false` 槽位若其 Writer 能取到被改动的值
  （整体替换同理）就会被采样发现；**同引用原地改元素仍不被承诺**（契约 §1 原文口径）。
- **不扩**复制频率分档、休眠唤醒、距离/相关性裁剪；轮询就是"每轮无条件采样一次 + 基线比较抑制"。
- **不重写** push 逐槽优化、不做网络继承索引模型、不动 Delta Compression / 每对象在途链语义。
- 未改生成器与编织器、未生成 PMR3、未把生产 13 成员迁成 auto-property（那是同一契约的 T-P4，属其它组）。
- 未启动 Unity、未做实机/Player/IL2CPP 验证（T-P6 仍 PENDING_USER）。
- 未改 ACK 单调/基线推进/条件跃迁/多连接确认/初始全量中的任何一条既有规则。

---

## 5. 剩余风险与后续

| 风险 | 说明 / 建议 |
|---|---|
| 轮询的常态开销 | 含非 Push 可见属性的对象会**每轮**进入整对象序列化比较。当前没有频率分档；若后续有大对象走轮询，应另起一项做"轮询频率/分片"（本轮按契约刻意不扩）。可用 `PollSampledSlots` 监控。 |
| 轮转改变"同轮内对象顺序" | 报文里对象出现的顺序会随游标变化（不改变正确性：掩码/版本/ACK 都是 per-object）。若将来有依赖报文内对象顺序的逻辑，需要显式化。 |
| 游标与对象表增删 | 游标按 `_objects` 下标规约（越界取模、不重置）；对象大量注销后可能短暂"跳过"已注销位（只影响顺序，不影响覆盖）。 |
| `InitialFullSends` 计数口径 | 该计数仍是**对象级**（`UnknownBaselineCount > 0`），因此含不可见槽位的对象每轮被扫描时都会 +1。它只是诊断计数、未参与任何调度判定；若将来要用它做断言，需先改成"逐可见槽位"口径。 |
| 与生成组（T-P1/T-P2）的交叉 | 本轮未碰生成/编织；若生成组产出 `PushBased=false` 的 auto-property，本层已能正确轮询（生成物只需把 `p.PushBased = false` 写进描述符）。 |
| 实机 | 与主计划既有口径一致：编译与受控测试通过**不等于**真实双客户端/弱网验收。T-P6 仍后置。 |

---

## 6. 复现命令

```bash
# 主门禁（独立输出目录，先 build0 再 run）
dotnet build Tools/PMReplicationTest/PMReplicationTest.csproj -c Release -o Tools/PMReplicationTest/bin/property-polling
dotnet Tools/PMReplicationTest/bin/property-polling/PMReplicationTest.dll
# 退出码 0 = 干净实现 317/0 且 11/11 注入命中

# 回归（同一模式，逐工程）
dotnet build Tools/<工程>/<工程>.csproj -c Release -o Tools/<工程>/bin/property-polling
dotnet Tools/<工程>/bin/property-polling/<工程>.dll
```

（本轮在 Windows + `dotnet`（.NET SDK）下执行；未启动 Unity、未启停任何服务、未提交。）
