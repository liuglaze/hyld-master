# R4-B 核心独立核验与修复报告（PMPredictionTimeline / PMR4MovementDriver / PMR4MovementCodec）

> 状态与验收以 `Docs/plans/net-architecture-migration.md` 为准；本文是本轮的**独立核验 + 修复 + 证据**记录。
> 接口契约：`net-r4-prediction-contract.md`、`net-r4-network-contract.md`（含「主侧集成前复核修订」）。
> 上一轮实施报告：`_r4b_network_report.md`。本轮只改 C#、不改/不编译 UE、不提交、不递归委派。

---

## 0. 摘要（≤1500 字）

按主侧三点独立核验，**三点全部复现为真实缺陷**；同链路内另查出两处真实缺陷，全部已修 + 补门禁。

**① 时间轴快照构造越界**：`PMPredictionTimeline` 的「可信完整快照」构造函数只写 `_pendingFrame/_confirmedFrame`、漏写 `_boundaryStartFrame`；基态停在 0，而读取走 `boundary-1-_boundaryStartFrame` ⇒ 快照 `OutputFrame>0` 时**首次读取即抛**「读取边界越界」。已改为一次写齐，并补 TotalSimTimeMs 有限非负、ServerFrame 域、边界非负、构造后自洽校验；同时删除 Driver 的 `CreateReboundTimeline` 占位 epoch 假绑定绕行（`RebindFallbacks` 保留、恒为 0）。

**② SP 接不到新流**：`ApplyPendingSnapshots` 对任何非当前流代次的复制快照一律拒绝，而 `ClientMovementResyncV1` 是 owner-only ⇒ DS 一升流 SP 就永久停摆。已改为**仅 SP**、在验证身份 + 世界版本 + 边界/仿真时间不倒退后允许较新代次快照更新插值绑定；冻结 SP 仍拒绝；AP「普通快照不得解冻」的门未放宽。

**③ 可靠域顺序**：事件与重同步分属两队列、先跑完全部 Resync 再跑 Events，同批「旧事件→Resync2→事件2→Resync3→事件3」会把更早代次事件当过期丢弃。已合并为单一到达序 FIFO、按到达顺序一趟消费（快照通道仍独立）；旧实现实测丢 1 条事件 + 拒 1 批。

**④（新查）codec 顺序/重复不变量未生效**：8 个读循环声明 `lastField` 却从未赋值，判断恒与 0 比较 ⇒「不得重复/不得倒退」形同虚设，重复标量被静默覆写。已改为「不得倒退 + 除数组承载字段（10/22/11）外不得重复」。

**⑤（新查）回调异常重放不可逆事件**：`DispatchTo` 先回调后推水位，宿主回调抛异常时水位未前移 ⇒ 下次派发会重复广播同一条不可逆事件；Driver 亦无异常隔离。已改为先推水位与计数再回调，并在 Driver 单独记录异常、继续其它通知。

**验收**（均本次 build 0 错误后 run）：PMR4NetworkCheck 与 PMPredictionCoreCheck（netstandard2.0+C#7.3）0 错误；PMPredictionTest **453/0**（417→+36）；PMR4NetworkTest **389/0**（327→+62）；PMMoverPredictionTest **264/0**；PMClientCheck / PMUnityGlueCheck / PMR4UnityCheck / Server.csproj 0 错误。
**负向注入 3 组**（temp 备份 + finally 逐字节还原 + md5 校验）：分别精确抓到 3 / 2 / 4+6+3 项失败，并报出对应判据。
**API 兼容**：无 public 方法签名变更，仅新增 5 个 public 诊断计数。

---

## 1. 必读文档与已检查证据（按任务指定顺序）

| 顺序 | 文档 | 从文档得到的、决定了本轮判断的结论 |
|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 工作副本为 `D:/UGit/hyld-master`；客户端/服务端/迁移计划三份下游文档的入口 |
| 2 | `Client/Assets/AGENTS.md` | 客户端现状基线（CSP、MoveAck/Replay、8.x 动态追帧、参数表）；也确认了文档里大量函数名/行号已过期，不能当现状事实 |
| 3 | `Server/AGENTS.md` | 服务端基线（LZJUDP/NetSim/BattleLoop）；服务端与客户端**不共用**战斗仿真，R4 的「同一套仿真靠 role 分流」是新增语义 |
| 4 | `Docs/plans/net-architecture-migration.md`（2088 行，全篇分块读完） | 状态唯一事实源：R4「进行中：R4-A 预测 417/Mover 245/集成 264 通过；R4-B 网络/Unity 碰撞/裁决未接，T43/T44 整体仍 PENDING」；§9.5 #31「门禁结论只在本次构建 0 错误前提下可信」；§9.5 #30「工作目录 ≠ 目标仓库时写入边界须绝对路径」；§3.9.4 运行时契约（可靠 RPC 同通道保序、属性复制只保证当前状态收敛、历史/窗口/排空内存必须有界）；§5.4/§5.5 R2 待收口项（继承、有副作用 Reader、MTU 未冻结） |
| 5 | `Docs/plans/net-r4-prediction-contract.md` | 帧域（输入帧 n 在边界 n 起模拟、产出 n+1；PendingFrame/ConfirmedFrame 均在 Input 域）；历史 128+129；`ApplyAuthority` 在 Frozen/NeedsResync 返回 Stalled 且零改动；「Snapshot 可携带 ConfirmedEvents / ConfirmedEventFrames，null=未提供证据，空数组=确定无事件」；每帧 256 / 批 256 边界 / 总 4096 事件、深 clone、同帧同 key 去重、重复/旧快照不重播 |
| 6 | `Docs/plans/net-r4-network-contract.md`（尤其末尾「主侧集成前复核修订」） | §B1 承载与上限（blob ≤ 4096、一包 ≤ 8 条、未确认 ≤ 128、层 ≤ 16、完整 Sync/Aux 必填含 `ActiveLayers.ElapsedMs`）；重同步只由 DS 递增 StreamVersion、新流不得回退输出边界/累计时间；**「SP 不会接收 owner-only ClientResync，故较新 StreamVersion 的合法 DS 复制快照必须允许 SP 在验证身份、世界版本、边界/仿真时间不倒退后更新插值绑定；这不适用于冻结 SP，更不能放松 AP 普通快照不得解冻的门」**；**「不能假设传输保序自动等于消费保序；实现应保留可靠消息接收次序 … 至少覆盖同一次 Update 收旧流事件→Resync2→流2事件→Resync3→流3事件」**；「测试出生点必须在墙外」「Aux.WorldVersion 统一 1」 |
| 7 | `Docs/plans/_r4b_network_report.md` | B1 的精确 API 面与初始化次序；§6 阻塞③ 明确登记了 R4-A 缺陷（快照构造漏写 `_boundaryStartFrame`，`OutputFrame > 0` 首次读取越界）与「本批用 public API 绕开」的处置；§6 观察项（复制通道会对同一值重复下发 ⇒ SP 常见 From==To、Alpha=1；发送→投递→接纳跨帧，需要显式再跑真实帧） |
| 8 | `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md` | 一律 bash（Git Bash）语义；需要 PowerShell 时显式 `pwsh.exe`；不混引号/路径/转义；批量改动前确认作用范围 |
| 9 | `.agents/skills/mover-quick-start/SKILL.md` + `references/network-prediction-checklist.md` | **仅作 C# 移植约束**：SyncState 必须覆盖所有影响未来模拟的可变量（检查项 1）；InputCmd 只含输入意图、不含派生量（检查项 2）；生成/模拟必须确定性（检查项 3）；模拟内不得有帧外副作用、副作用放 Finalize（检查项 4）；NetSerialize 必须完整、遗留 `StartTimeMs` 这类字段最易漏（检查项 5）；带宽最小化用 bHas* 模式、同帧合并（检查项 6）；「墙钟口径区分」：`GenerateMove` 禁墙钟 ≠ 断线兜底**必须**用墙钟。本轮据此复核了 codec 的字段完整性（含 `ElapsedMs`）、上行输入纯净性（禁 Effects/Layers）、确定性（无墙钟/无随机）、以及 0 占位不调模型 |

代码事实（读到的实现性质，决定了修法）：`PMPredictionTimeline` 的边界索引算法 `boundary - 1 - _boundaryStartFrame`；`PMInterpolationBuffer.OnAuthority` 自身已按 `TotalSimTimeMs` 拒绝倒退并做首次绑定；驱动所有「步进入口」都会先调 `ApplyPending`（宿主漏调也不丢入站）；`PMR4MovementEventJournal` 的 `Rebind/RecoverAfterResync/DispatchTo` 语义。

---

## 2. 真实根因（逐条：现象 → 机制 → 证据）

### 2.1 【确认项①】时间轴快照构造漏写 `_boundaryStartFrame`

- **位置**：`Client/Assets/Scripts/PMPrediction/PMPredictionTimeline.cs` 第二个构造函数（「用可信完整快照构造」）。
- **机制**：边界状态读取统一走 `ResolveBoundaryIndex(boundary)`：`boundary == _boundaryStartFrame` 才返回 `-1`（表示读 `_base*`），否则按 `index = boundary - 1 - _boundaryStartFrame` 取环形槽并检查 `index < _count`。旧构造只写 `_pendingFrame/_confirmedFrame`，`_boundaryStartFrame` 保持默认 0、`_count` 为 0 ⇒ 当 `OutputFrame = 7` 时 `index = 6 >= 0 = _count` ⇒ 抛 `读取边界越界：boundary=7`。边界 0 的快照恰好「撞对」，所以只有非零边界暴露。
- **证据（负向注入，非推断）**：把构造里的赋值改回「只写 pending/confirmed」后，新增用例立即失败并打印
  `[PMPredictionTimeline] 读取边界越界：boundary=7`，栈为 `ResolveBoundaryIndex → GetBoundarySyncRef → GetSyncSnapshot`，即**首次读取**就抛（与 `_r4b_network_report.md` §6 阻塞③一致）。
- **影响面**：任何「以非零输出边界建立预测时间轴」的路径。B1 里 `CreateReboundTimeline` 用它来重绑；B3 宿主若直接以 DS 快照建 AP 时间轴，同样首帧即崩。

### 2.2 【确认项②】`ApplyPendingSnapshots` 拒绝所有新流快照 ⇒ SP 永久停摆

- **位置**：`Client/Assets/Scripts/PMR3/PMR4MovementDriver.cs`（`ApplyPendingSnapshots` 的 `blob.StreamVersion != _streamVersion` 分支）。
- **机制**：DS 的 `BeginServerResync` 递增 `StreamVersion`，只把新流完整快照经 **owner-only** 的 `ClientMovementResyncV1` 下发；普通复制属性 `_movementSnapshotV1` 才是所有客户端都收到的通道。旧实现对该属性的新流代次一律 `SnapshotRejectedStreamNewer` 后丢弃，且 SP 永远收不到 Resync ⇒ SP 的 `_streamVersion` 永不前进、插值缓冲再也不更新。契约末尾「主侧集成前复核修订」已明确要求放宽 SP。
- **证据**：注入「SP 走 AP 分支」后新增用例失败并打印 `SP 经复制快照追上 DS 的新流代次…（期望 2，实际 1）`——即 DS 已到流 2、SP 仍停在流 1。

### 2.3 【确认项③】可靠域顺序被两趟消费破坏

- **位置**：同上，`ApplyPending` 旧顺序 `… ApplyPendingResyncs(); ApplyPendingEvents();`（两个独立 `List<byte[]>`）。
- **机制**：`ClientMovementEventsV1` 与 `ClientMovementResyncV1` 都是 Reliable，共享同一顺序域；DS 侧一律「先 flush 事件批、再发升流重同步」（`Pump` 内 `FlushAuthorityEvents` 在 `BeginServerResync` 之前）。接收侧却先跑完**所有** Resync 再跑**所有** Events：`Rebind` 会把 `_previousStream` 设为上一次重绑的流并重置 `_lastSequence`；连续两次 Rebind 后更早代次的事件既不在当前流、也不在「上一流」⇒ 被当作过期丢弃。这正是「不能假设传输保序等于消费保序」所指的形态。
- **证据**：注入「两趟消费」后新增用例精确失败：`三条事件都不丢（期望 3，实际 2）`、`三个事件批全部被接受（期望 3，实际 2）`、`没有事件批被拒（期望 0，实际 1）`、`旧流事件（边界 3）确实被派发`。

### 2.4 【本轮新查】codec 的「字段号不得重复/不得倒退」从不生效

- **位置**：`Client/Assets/Scripts/PMR3/PMR4MovementCodec.cs` 的 8 个读取循环（`TryDecodeInputs` / `TryReadInputEntry` / `TryDecodeSnapshot` / `TryReadSync` / `TryReadLayer` / `TryReadAux` / `TryDecodeEvents` / `TryReadEventRecord`）。
- **机制**：每个循环都写了 `int lastField = …;` 并在循环内调 `IsStrictlyAscending(lastField, field, …)`，但**没有任何一处给 `lastField` 赋值**。于是判据恒为 `field <= 0/5` ⇒ 只要字段号 > 起始值就放行。文档承诺（文件头不变式 6、B1 报告「严格字段升序」、测试注释「重复字段…拒绝」）因此**全部不成立**：重复标量会被静默读两次并以后者覆写前者（最后一处 `seen` 位掩码只保证必填字段存在，不判重复）。这是 fail-closed 面被削弱的一类问题，且**编译期与既有测试都看不出来**（既有用例只覆盖「缺字段」「未知字段」「帧号非升序」，没有覆盖标量重复）。
- **修复口径**：改为「字段号**不得倒退**」+「**除数组承载字段外不得重复**」。数组在 protobuf 里就是重复 tag：输入条目 `10`、活跃层 `22`、事件条目 `11`，必须继续允许（第一次修得过严会导致所有多元素载荷被拒——本轮实测到过，已回退到精确口径）。
- **证据**：注入「Sync 读侧不推进 lastField」后新增用例精确失败：`Sync 标量字段重复（yaw 两次）被拒`、`Sync 标量字段重复（positionX 两次）被拒`（且其余用例仍全绿，说明修复面精确）。

### 2.5 【本轮新查】事件回调抛异常会导致不可逆事件被重复派发

- **位置**：`PMR4MovementEventJournal.DispatchTo`（回调在推进 `_entries/_pendingEvents/_dispatchedThrough` **之前**）+ `PMR4MovementDriver.DispatchConfirmedEvents`（宿主 `EventDispatched` 无异常隔离）。
- **机制**：`DispatchTo` 边遍历边 `sink(...)`，异常沿调用栈抛出时「移除条目 / 递减未派发计数 / 推进派发水位」三件事都还没做。下一次派发（下一帧 `ApplyPending`）会重新看到同一条目 ⇒ **同一条不可逆事件再次广播**。契约要求不可逆事件只通知一次；表现层会重复播音效/重复生成子弹。
- **修复**：先推进水位与计数、再回调；Driver 侧对宿主回调加 try/catch，异常只计入 `EventDispatchFailed`/`EventDispatchLastError` 并继续其它通知（对应 R2 已确立的「表现回调异常单独记录并继续」口径）。
- **证据**：注入「先回调后推进水位」后新增用例精确失败：`回调抛异常后未派发事件数归零（水位已前移）（期望 0，实际 1）`、`回调抛异常后派发水位已推进（期望 5，实际 0）`、`再次派发不会重发同一条事件（日志层也不重放）（期望 0，实际 1）`。

---

## 3. 修复内容（逐文件）

### 3.1 `Client/Assets/Scripts/PMPrediction/PMPredictionTimeline.cs`

1. 快照构造函数**一次写齐** `_boundaryStartFrame = boundary;` 与 `_pendingFrame/_confirmedFrame/_baseTotalSimTimeMs/_baseServerFrame`，并补防御性自洽断言。
2. 新增异常状态校验：`ServerFrame` 必须 `AuthorityServer` 域（未建立允许）、`TotalSimTimeMs` 必须有限且非负、`OutputFrame` 不得为负。`Sync`/`Aux` 为 null 仍抛 `ArgumentNullException`（原语义）。
3. 新增私有 `IsFiniteDouble` 助手（不引入 public API）。
4. **未改动**：帧域隔离、128+129 历史、`Resync` 的 epoch 重绑/同绑定分支、Stalled 语义、事件证据的 256/256/4096 上限与深克隆去重。

### 3.2 `Client/Assets/Scripts/PMR3/PMR4MovementDriver.cs`

1. `CreateReboundTimeline`（约 1998 行）**删除占位 epoch 假绑定绕行**，改为普通快照构造；同时删掉只被该绕行使用的 `BuildSnapshotWithIdentity`。`RebindFallbacks` 字段保留（现恒为 0），注释说明历史原因。
2. `ApplyPendingSnapshots`：新增**仅 SP** 的新流快照重绑路径——`_frozen` ⇒ 拒绝（`SnapshotRebindRejectedFrozen`）；`blob.StreamVersion < _streamVersion` ⇒ 旧流不回写（`SnapshotRejectedIdentity`）；有权威且「输出边界或累计仿真时间倒退」⇒ 拒绝（`SnapshotRebindRejectedRegressing`）；否则绑定新流（`SnapshotStreamRebinds`）并交给插值缓冲。AP 分支语义不变（新流等待 Resync、旧流不回写）。
3. **可靠域单一到达序 FIFO**：`_pendingEvents`/`_pendingResyncs` 合并为 `_pendingReliable`（含 kind、各自计数上限不变：事件 256 / 重同步 4），`ApplyPendingEvents`/`ApplyPendingResyncs` 合并为 `ApplyPendingReliable`（按到达顺序一趟派发）+ 单条处理函数 `ApplyEventPayload`/`ApplyResyncPayload`。`ApplyPending` 顺序变为 重同步请求 → 输入 → 快照 → **可靠域（有序）** → 待发重同步请求。
4. `PMR4MovementEventJournal.DispatchTo`：先 `RemoveAt`/减计数/推水位，再回调。
5. `DispatchConfirmedEvents`：宿主回调加 try/catch，异常单独记录并继续。
6. 新增 public 诊断计数（纯增量）：`SnapshotStreamRebinds`、`SnapshotRebindRejectedRegressing`、`SnapshotRebindRejectedFrozen`、`EventDispatchFailed`、`EventDispatchLastError`。
7. `Freeze()`/`Dispose()` 清空合并后的入站队列与计数（避免残留）。

### 3.3 `Client/Assets/Scripts/PMR3/PMR4MovementCodec.cs`

1. 8 个读循环每轮 `lastField = field;`（含 `TryReadSync` 的 `field == 22` 提前 `continue` 分支）。
2. `IsStrictlyAscending` → `IsFieldOrderAccepted(lastField, field, repeatableField, error)`：倒退即拒；`field == lastField` 且不是该载荷的数组承载字段即拒（重复标量）。8 处调用点分别传入 `FInputEntry(10)` / `0` / `0` / `22` / `0` / `0` / `FEvent(11)` / `0`。
3. 文件头不变式 6 与头部注释改为与实现一致的准确表述（「不得倒退 + 标量不得重复（数组用重复 message 字段承载）」）。
4. **未改动**：kind/version/身份三件套、`MaxBlobBytes=4096`、一包 8 条、层 ≤ 16、事件 ≤ 64、NaN/未知 Mode/负值/尾部字节/跨通道错用拒绝、严格 Sync/Aux 必填、yaw 规范化。

### 3.4 测试文件

- `Tools/PMPredictionTest/Program.cs`：新增 **M 段**（构造后读 + Tick 非 0 + 非法快照 fail-fast），并新增 `CheckNoThrow` 使「被测路径抛异常」也记成可读 FAIL 而不是让门禁自己崩掉。
- `Tools/PMR4NetworkTest/Program.cs`：新增 B4b（手写字节的「重复标量/字段号倒退」拒绝 + 数组重复仍接受）、**F2**（可靠域顺序）、**F3**（事件回调异常隔离，含日志层直调）、**H2**（SP 流重绑 / 旧流不回写 / 冻结不升流）；新增 `SendEventBatch`/`SendResyncSnapshot` 两个**经真实生成桩 + 真实 Transport** 的发包助手。

---

## 4. 测试与证据

### 4.1 验收（命令一律 build 与 run 串在同一链；全部为本次构建产物）

| 门禁 | 命令 | 结果 |
|---|---|---|
| R4-B 语言面 | `dotnet build Tools/PMR4NetworkCheck -c Release` | **0 错误 0 警告**（netstandard2.0 + C#7.3，含 PMPrediction/PMMover/PMR3 全部源码） |
| 预测核心语言面 | `dotnet build Tools/PMPredictionCoreCheck -c Release` | **0 错误**（netstandard2.0 + C#7.3） |
| 预测核心行为 | `dotnet Tools/PMPredictionTest/bin/Release/net8.0/PMPredictionTest.dll` | **453 通过 / 0 失败**（本轮 417 → +36） |
| R4-B 网络行为 | `dotnet Tools/PMR4NetworkTest/bin/Release/net8.0/PMR4NetworkTest.dll` | **389 通过 / 0 失败，PASS**（本轮 327 → +62） |
| Mover×预测集成 | `dotnet Tools/PMMoverPredictionTest/bin/Release/net8.0/PMMoverPredictionTest.dll` | **264 通过 / 0 失败** |
| 客户端玩法层编译 | `dotnet build Tools/PMClientCheck -c Release` | 0 错误 0 警告 |
| Unity 胶水编译 | `dotnet build Tools/PMUnityGlueCheck -c Release` | 0 错误（**1 条 CS2002「Loging.cs 指定了多次」，为本 csproj 既有的重复 `<Compile Include>`，不在本轮边界内**） |
| Unity 碰撞适配编译 | `dotnet build Tools/PMR4UnityCheck -c Release` | 0 错误 0 警告 |
| 大厅服务端编译 | `dotnet build Server/Server.csproj` | 0 错误（4 条既有警告）；该工程**显式 Exclude** 了 PMR4MovementDriver/Codec，本轮未改其 csproj |
| 既有门禁（未全量重复） | `PMR3RuntimeTest` | 首次运行 H 组 8 项失败（真实墙钟长局 liveness，与并行构建争 CPU），**第二次运行 180/0 PASS**；该工程不编译本轮任何改动文件，属既有墙钟抖动 |

### 4.2 负向注入（每组：备份 → 注入 → build+run → finally 逐字节还原 → md5 校验）

| # | 注入点 | 预期 | 实测 | 还原校验 |
|---|---|---|---|---|
| ① | `PMPredictionTimeline` 快照构造不写 `_boundaryStartFrame` | 新 M 段失败且报出越界 | **3 项 FAIL**，含 `[PMPredictionTimeline] 读取边界越界：boundary=7` | md5 `a1f5d661f99b50619117a116d689b02c` 一致 |
| ② | `PMR4MovementCodec` Sync 读侧不推进 `lastField` | 「重复标量」用例失败 | **2 项 FAIL**（yaw 两次 / positionX 两次） | md5 `43d771033871e09f788ef23e3eed45d1` 一致 |
| ③a | `ApplyPendingReliable` 降级为「先所有 Resync、再所有 Events」 | F2 失败且报出漏事件 | **4 项 FAIL**（事件 3→2、批 3→2、被拒 0→1、旧流事件未派发） | md5 `49fbccfcec883176724533a0b2372f02` 一致 |
| ③b | SP 走 AP 分支（新流快照一律拒） | H2 失败且报出 SP 停摆 | **6 项 FAIL**（含 `SP 流代次期望 2 实际 1`、未走到重绑） | 同上 |
| ③c | `DispatchTo` 先回调后推水位 | F3 失败且报出可重放 | **3 项 FAIL**（水位未推进、未派发数未归零、再次派发重发） | 同上 |

还原后三个门禁全部恢复 453/0、389/0（md5 与本轮交付一致，见 §5）。

### 4.3 新增测试覆盖的契约条目

- 「以非零输出边界构造 + 立即读 + 立即 Tick」；非法初始快照（epoch/instance 0、OutputFrame 域错/未建立/为负、ServerFrame 域错、TotalSimTimeMs NaN/负、Sync/Aux null）构造期 fail-fast。
- 「字段号不得倒退 + 标量不得重复 + 数组重复仍接受」（输入条目/顶层快照/Sync/层/Aux/事件，含手写字节对照）；反向保留多条目/多事件。
- 「旧流事件→Resync2→流2事件→Resync3→流3事件」同一批**不漏不重**且最后一流落地。
- 事件回调抛异常：不重放、不吞掉其它接收者、日志层水位前移 + 再次派发不重发。
- SP：DS 真实升流后经**复制快照**追上；旧流快照（带更大边界/时间）不回写；冻结 SP 拒绝升流且插值基准不前进。
- 全部走**真实 PMTransport 字节链 + 真实生成桩 + 真实世界/复制/生命周期**；没有直调 AP 实现冒充过网（唯一例外是既有 H2 的「受控快照投喂」，本轮新用例不依赖它）。

---

## 5. 交付物（含逐字节指纹，供主侧核对）

| 文件 | md5 | 字节 |
|---|---|---|
| `Client/Assets/Scripts/PMPrediction/PMPredictionTimeline.cs` | `a1f5d661f99b50619117a116d689b02c` | 66365 |
| `Client/Assets/Scripts/PMR3/PMR4MovementDriver.cs` | `49fbccfcec883176724533a0b2372f02` | 97028 |
| `Client/Assets/Scripts/PMR3/PMR4MovementCodec.cs` | `43d771033871e09f788ef23e3eed45d1` | 77241 |
| `Tools/PMPredictionTest/Program.cs` | `3f8695f4ec345bdd11ce2d034a6322d9` | 124288 |
| `Tools/PMR4NetworkTest/Program.cs` | `894fd95de1a1d985d34ca2ccc6551e7a` | 155098 |

编码：五个文件均保持 **UTF-8 BOM + CRLF**（逐字节校验 `bareLF = 0`、无异常控制字符）。`.meta` 未新增（未创建新文件）。

---

## 6. API 兼容性（并行集成方按 `_r4b_network_report.md` §3 消费）

- **无 public 签名变更**：`PMPredictionTimeline` 构造函数参数、`Tick/ApplyAuthority/Resync/Freeze/NotifyWallClock/GetSnapshot/TryGetBoundarySnapshot/TryGetBoundaryEvents/HasInput` 全部不变；`PMR4MovementDriver` 的构造/工厂/`PublishInitialSnapshot/Pump/Tick/Advance/SamplePresentation/Update/Freeze/Dispose/BeginServerResync/ServerSubmitTrusted*`、五个入站转发入口、`PMR4MovementCodec` 的全部 public 编解码与校验方法签名不变。
- **仅新增**（追加，不破坏现有消费）：Driver 的 5 个 public 诊断字段。
- **保留但语义收缩**：`RebindFallbacks` 保留（现恒为 0），文档注释已更新；若集成方把它当作「发生绕行」的信号，应改为读 0。
- **测试工程签名**：新增的测试助手都是 `private static`。

---

## 7. 余留风险 / 明确不做

1. **未做 Unity/PhysX 与真实跨机验证**：本轮是纯 C# 核验；B2 的 Unity 胶囊查询、B3 的真实宿主接线、T43/T44 的实机证据仍 PENDING（不在本任务边界）。
2. **SP 重绑依赖「DS 的流代次单调递增」**：SP 无法校验「新流是否真的比旧流新」，只能按 `StreamVersion` 大小判定（这是唯一的比较依据）。DS 侧 `BeginServerResync` 已保证 `+1` 单调（`next == 0` 时回到 1，理论上 uint 回绕才会出现非单调，实际不可达）。冻结的 SP 不会自动升流——恢复必须由宿主显式 Resync。
3. **codec 顺序/重复校验的边界**：只对已知字段号生效；未知字段号仍按原语义被拒（顺序检查先于字段范围检查）。同帧内**跨字段**的语义矛盾（如 Grounded=true 但 Mode=Flying）不在 codec 职责内（属模型/权威侧）。
4. **事件回调异常的诊断计数**：`DispatchTo` 的 `DispatchedEvents/LateDispatches` 仍在该次调用末尾累加，若宿主通过**直接调用 Journal**（非 Driver）并让回调抛异常，这两个计数会漏记（水位与「不重放」不受影响）。Driver 路径不受影响。
5. **既有项目级问题（非本轮引入、不在写入边界）**：`Tools/PMUnityGlueCheck.csproj` 重复列出 `Loging.cs`（CS2002 警告）；`PMR3RuntimeTest` 的 H 组用真实墙钟长局，负载下会抖动（二次运行 180/0）。
6. **有意不做**：不改任何 public 方法签名、不改宿主/Unity/声明/生成物/主计划、不提交、不递归委派、不编译 UE；`_r4b_network_report.md` 里登记的其它阻塞（Lobby csproj 的 include/exclude 方案已由主侧落地为 Exclude；R2 的继承/有副作用 Reader/MTU 未冻结）不在本轮范围。
