# R2 返工 — 复制组（RV1–RV4 + 声明式 Create 初值）实施报告

> 范围：`Client/Assets/Scripts/PMNet/Replication/**`、`World/**`、`PMNetObject.cs`、`PMNetProperty.cs`
> 及独立运行时门禁 `Tools/PMReplicationTest`、`Tools/PMNetWorldTest`。
> 未改动生成器 / `Declarations` / `PMRpc` / `PMNetE2E`（由并行组承担），未做 UDP 宿主与 R3 握手，
> 未编译 Unity、未跑对方生成器（避免并发写）。
> 证据来源：`Docs/plans/_r2_review_replication.md`（F1–F5、O1–O3）、`net-architecture-migration.md`
> 「R2 独立审查后返工」+ §5.4–§5.6、`net-r2-codegen-contract.md`（全文）、`net-r0-contract.md`
> （D-R0-05/09/12/13/14/15/16/18、§5、§9）。
>
> **RV2 返工第二轮（本次）**：修正接收侧的**提交/回调语义** —— ① 整条记录提交完成后才**统一**派发 OnRep
> （否则回调会看到同记录后续属性的旧值）；② 表现回调异常与协议解析失败**分离**（已完整提交的记录不因
> 回调异常丢 ACK，也不因此重放重复副作用）；③ 暂存工厂返回活对象/已登记对象时**拒绝整条记录**；
> ④ 并**限定**原子性的适用边界（不宣称完全解决，见 §4.4）。
>
> 本轮实际改动文件（与 §2 一致）：`Replication/PMReplicationChannel.cs`（逻辑 + 三个通道级计数 + 两个决策点）、
> `Replication/PMRepConnectionState.cs`（**仅** `PMRepObjectEntry.Factory` 的前置条件文档）、
> `Tools/PMReplicationTest/Program.cs`（断言与缺陷注入），以及本报告。
> **未创建、也未修改 `PMReplicationState.cs`** —— 该文件在本仓库中不存在；上一轮写边界曾把它笔误写成这个名字，
> 本轮写边界为**正确**文件 `PMRepConnectionState.cs`（`PMRepObjectEntry` 本来就定义在该文件里）。

## 1. 必读文档路由（实际按此顺序阅读，也是判据来源）

| 顺序 | 文档 | 本次用于判定什么 |
|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 三入口路由（Client / Server / 迁移计划） |
| 2 | `Client/Assets/AGENTS.md` | 进程形态、旧 `MainPack` 链与 PMNet 新链的边界 ⇒ 本次只动 PMNet |
| 3 | `Server/AGENTS.md` | 服务端权威边界；确认 R2 不涉及服务端改动 |
| 4 | `net-architecture-migration.md`（R2 行、§5.4–5.6、「R2 独立审查后返工」表 RV1–RV7） | 验收条目与「初始状态必须按接收连接过滤、不得泄露」等决策 |
| 5 | `net-r2-codegen-contract.md`（全文） | 冻结接口（§1）、协议摘要（§3）、生成物 API 面（§4）、`PMRepMask.MaxBits=256` 的设计意图 |
| 6 | `net-r0-contract.md`（§2.4 复制模型、§5 复制与生命周期契约、§9 参数） | D-R0-13/14/15/16/18（比较才是判据、8 项条件、跃迁补发、Create 原子性、有界）、D-R0-05/09（禁止跨域假设顺序） |
| 7 | `_r2_review_replication.md`（全文） | F1 掩码位序错配、F2 初始状态未闭合、F3 部分应用、F4 `_allObjects` 无界、F5 僵尸脏位、O1–O3 |

## 2. 改动清单（按文件）

| 文件 | 落点 |
|---|---|
| `Replication/PMReplicationTypes.cs` | `PMRepMask` 增加 `MaxBytes=32`；`GetByte/SetByte` 改为**字节下标**语义（与写侧同源）；`SetByte` 由「或上」改为「覆盖」；新增 `TryLoadBytes`；`ByteCountFor` 超上限直接抛；`PMRepStats` 新增 `RecordsRejected` / `AckWithoutInflight` |
| `Replication/PMReplicationReader.cs` | 掩码按字节语义载入；新增形状规则（尾字节非 0 = 最短形态、字节数 ≤32）、消息尾部必须为空；失败路径清空输出容器 |
| `Replication/PMReplicationWriter.cs` | 新增 `ValidateRecordShape`：槽位严格升序且不重复、在 `[0,256)` 内、三个平行数组长度一致，**违反即抛**；去掉 `maskBytes` 静默钳位与越界槽位静默 `continue` |
| `Replication/PMReplicationChannel.cs` | `ApplyRecord` → `TryApplyRecord` 三阶段（结构校验 → 暂存对象全量解码 → **3a 整条提交、3b 统一派发 OnRep**）；只有整条生效才 `QueueAck`；`UpdateMessagesApplied` 只在真的应用了 ≥1 条时自增（O2）；`OnAck` 对「无在途记录」的版本不推进水位（新决策点 `ShouldAdvanceWithoutInflight`）；`TryClearSettledDirty` 支持 256 位并清「非属性伪位」；新增 `ReplicatedPropertyFilter` 的条件接线；新增暂存工厂前置校验（拒绝活对象/已登记对象，计 `StagingRejected`）、OnRep 异常隔离（`OnRepExceptions`）与提交阶段失败计数（`CommitFailures`）三个**通道级计数**；新增 `DispatchOnRepPerProperty` / `IsolateOnRepExceptions` 两个决策点（仅供门禁注入） |
| `Replication/PMRepConnectionState.cs` | `PMRepObjectEntry.Factory`（暂存解码所需，兼容性新增）；RV2 返工第二轮补「工厂必须产出**全新、未登记**对象」前置条件与「该委托**按对象缓存**」的说明（**仅文档**，无逻辑改动）。本轮写边界为正确文件（上一轮曾笔误写作不存在的 `PMReplicationState.cs`） |
| `World/PMNetWorld.cs` | `_allObjects` 改为**只持有存活对象**（置 null + 延迟压缩）；`TotalObjectCount` 改存活口径 + 新增 `AllObjectSlotCount`；`DetachFromAllObjects`（销毁与回滚共用）；`ReplicatedPropertyFilter` 兼容入口 + 声明式初值构包/应用 |
| `World/PMNetObjectLifecycle.cs` | `FormatVersion` 1 → 2；`PMNetLifecycleRecord.RepInitialState`；Create 记录新增 `repStateLen + repState`；写侧对两种初值都加长度上限（与读侧对称） |
| `PMNetObject.cs` | `internal int WorldSlot`（O(1) 摘除登记表项） |
| `PMNetProperty.cs` | `PMDirtyTracker` 位宽 64 → **256**（与 `PMRepMask.MaxBits` 一致）；越界**明确抛**；新增 `HasAnyBelow` / `ClearBit` / `ClearBitsAbove`；文件本身由 noBOM+LF 归一为 UTF-8 BOM + CRLF |
| `Tools/PMReplicationTest/Program.cs` | 新增 J/K/L/M/N 五组共 97 项断言；F1 缺陷注入扩为「旧 ACK 判定 + 无在途推进 + 无条件清脏」三处成对注入；**RV2 返工第二轮**新增 O/P/Q/R 四组共 39 项断言（回调看到整条新值 / 回调异常与协议失败分离 / 暂存工厂必须产出新对象 / 非纯 Reader 的剩余边界），并新增 F4/F5 两个缺陷注入；接收侧通道也接入注入工厂（否则接收路径的两处决策点注入不到） |
| `Tools/PMNetWorldTest/Program.cs` | B2 口径随 RV3 修正；A1/A3–A8/E6 改用 `PMLifecycleCodec.FormatVersion` 并补 A12；新增 H（RV3）/I（声明式初值）两组共 46 项断言 |

## 3. RV1 掩码位序与形状（F1）

**缺陷**：写侧按 `slot >> 3` / `slot & 7` 铺掩码字节，读侧却把**字节号当 ulong 下标**（`_w{index} |= value`，且 `index > 3` 静默丢弃）
⇒ 槽位 8..31 被映射到 64..95（越界 ⇒ 丢弃），≥33 属性时第 5 字节起整段丢失；
更糟的是 `ApplyRecord` 不抛异常 ⇒ 仍回 ACK ⇒ 发送侧用自己那份在途槽位表推进基线并清脏 ⇒ **静默永久不一致**。

**修法**：`PMRepMask.GetByte/SetByte` 统一为「线上第 b 字节承载槽位 8b..8b+7」，与写侧同源；
`SetByte` 改为覆盖（重复/乱序写入结果与顺序无关）；`ByteCountFor` 超过 256 位直接抛，不再钳位。
读侧还新增三条形状规则：字节数 ≤ 32、尾字节非 0（最短形态）、消息尾部必须为空（拒绝截断/拼接）。
写侧新增 `ValidateRecordShape`：乱序 / 重复 / 越界槽位 / 平行数组长度不一致全部**抛出**，不再静默跳过或截断。
（乱序与重复会让「掩码位序」与「值流顺序」不同源，值会写到错误槽位；属性 ID 兜底只能把它降级成逐槽位静默丢弃，仍是难查缺陷。）

## 4. RV2 接收应用原子性与错误 ACK（F3 + O1）

`TryApplyRecord` 三阶段，顺序不可换：

1. **结构校验**（不碰活对象）：槽位范围 / 属性 ID / Reader 存在 / 值字节非 null；
2. **暂存对象全量解码**：用 `PMNetClassEntry.Factory` 造一个暂时对象，逐个值解码，并要求**恰好读完**
   （多一字节也算失败）。无工厂 ⇒ **整条记录拒绝**并明确告警，不静默半应用；
3. **提交并派发 OnRep**：`3a` 把**整条记录**全部写入活对象，`3b` 再**统一**派发 OnRep（返工第二轮修正，见 §4.1–§4.2）。

`OnMessage` 只在记录完整生效后才 `QueueAck`。**失败记录不回 ACK** 是关键：发送侧只有收到 ACK 才会把该版本当成
「对方已有」并推进基线、清除脏位 —— 对未生效的记录回 ACK 会把它变成永久不一致。

`OnAck` 增加 `ShouldAdvanceWithoutInflight`（默认 false）：水位比已确认版本更新却**找不到在途记录**时不推进水位，
只计 `AckWithoutInflight`。在途记录只在「收到它的 ACK」或「丢包通知」时移除，因此该情形只能来自伪造/重放/错配；
盲目推进会让后续**真实** ACK 被判过期（基线永远追不上），并提前释放容量账本。

### 4.1 提交完成后才派发 OnRep（返工第二轮）

旧实现在提交循环里「写入一个属性 → 立刻派发它的 OnRep」。两条后果都只在运行期可见：

1. **回调看到半新半旧的状态**：第 k 个属性的回调执行时，同一条记录里 k+1..n 的属性还停在旧值上。
   业务在回调里读「同记录另一个字段」做派生计算（典型：用新 HP 配旧 MaxHP 算血条比例）就会算错。
   门禁用**真实 OnRep 读取同记录另一个字段**把它钉住（`O4`：只写 `A` 与 `B` 时，`A` 的回调必须看到 `B`
   的新值；旧行为会拿到 `B=2`）。
2. **回调抛异常会让已提交的记录丢 ACK**：旧实现里异常从提交循环一路穿透 `OnMessage` 到网络层，
   那条记录因此拿不到 ACK ⇒ 发送侧下一轮重发同一批值 ⇒ **已经执行过的回调再执行一次**（重复副作用）。

现在：`3a` 先全部提交，`3b` 再统一派发；回调异常在 `DispatchOnRep` 内部隔离，且**每次派发都独立隔离**
（一个属性回调失败不会吞掉同记录其他属性的通知）。

### 4.2 表现回调异常与协议解析失败**分离**（ACK 策略）

「回调抛异常」与「包坏了」是两件事，必须分开处理：

| 情形 | 记录是否生效 | ACK | 计数 | 后续 |
|---|---|---|---|---|
| 结构校验 / 暂存解码失败 | **完全未生效**（活对象零写入） | **不回** | `ProtocolErrors` + `RecordsRejected` | 发送侧重发，最终收敛 |
| 提交阶段失败（Reader 非纯字段写入） | **可能部分生效**（见 §4.4） | **不回** | 同上 + `CommitFailures` | 发送侧重发；纯字段写入下幂等 |
| **已完整提交、但 OnRep 抛异常** | **已生效** | **照常回** | `OnRepExceptions`（**不进**协议计数） | 不回退、不重放；继续派发后续属性的回调 |

ACK 策略不是写在注释里的约定，而是被断言钉死的（`P5`–`P11`）：回调异常不改变提交结论、不伪装成协议错误
（`RecordsRejected`/`ProtocolErrors` 不变）、ACK 照常入队（`P8`）；把该 ACK 回给发送侧后**下一轮无载荷**（`P10`）
且 `OnRepDispatched` 不变（`P11`）—— 即「不丢 ACK、不重放重复副作用」两条都被验到。

### 4.3 暂存工厂必须产出**全新、未登记**的对象

暂存解码是原子性的前提，而它的前提是「工厂不返回活对象」。旧实现没有校验：工厂若返回活对象本身
（或任何已登记对象），「验证性解码」写的就是活对象 —— 验证失败时活对象已被污染，而本层还以为自己什么都没碰。
现在 `TryApplyRecord` 在拿到暂存对象后立刻校验：

- `ReferenceEquals(staging, obj)` ⇒ 拒绝；
- `staging.NetId.IsValid || staging.WorldSlot >= 0 || staging.World != null || staging.State != Unregistered` ⇒ 拒绝。

拒绝即**整条记录不生效**、不回 ACK，并计 `StagingRejected`（`Q1`–`Q7` 覆盖两种形态：返回活对象、
返回另一个已登记对象；`Q8`–`Q10` 是「返回全新对象」的对照组）。这与世界创建路径
`PMNetWorld.ApplyCreate` 要求工厂产出 `Unregistered` 对象是同一条纪律。

> 顺带记一条实测事实（不是缺陷，但会咬人）：`PMRepObjectEntry.Factory` 是**按对象缓存**的
> （`ResolveEntry` 首次解析该对象时从 `PMNetRegistry` 拷贝），所以注册表必须在处理任何消息之前完成注册。
> 门禁 Q 组的对照组因此换了一个**新的接收通道** —— 否则改注册表里的工厂对已缓存条目无效。

### 4.4 诚实边界：双解码**不**等于普遍原子性

不要把这轮改动读成「原子性已彻底解决」。暂存解码能给出的保证有明确前提：

- **可以保证**（生成代码路径）：`PMNet_Read_<成员>` 是**纯字段写入** —— 只从 `PMNetReader` 取值写给自己的成员。
  此时「暂存解码成功」等价于「提交必然成功」，`3a` 不会中途失败，记录要么整体生效、要么整体不生效。
- **保证不了**：Reader 带副作用（写别的状态、改别的对象、按活对象当前状态分支），或只在活对象上抛。
  此时 `3a` 可能在第 k 个属性上失败，**前 k 个属性已经落在活对象上且无法可靠回退**
  （`R1`–`R5` 把这个形态跑成可执行断言：`A` 已写、`S` 还是旧值、不回 ACK、**不派发任何 OnRep**）。
  之后靠发送侧重发整条记录收敛（`R6`–`R9`：纯字段写入下重放幂等）。

**为什么不用现有 `Writer` 做回滚**：`Writer` 同样要读活对象，自身也可能抛；一旦回滚失败，
本层没有「回滚失败自身可见」的机制（`CommitFailures` 只记第一次失败），结果会是比不回滚更坏且**看不见**的状态。
与其造一个不能保证的保证，不如把失败当场暴露（计数 + 告警 + 不回 ACK + 不派发 OnRep），让发送侧重发。
这一条是**接受的取舍**，不是遗漏。

## 5. RV3 世界登记表只持有存活对象（F4）

`_allObjects` 旧实现「只增不减」：销毁不摘、创建回滚也不摘 ⇒ 每个已销毁对象被永久强引用（违反 D-R0-18），
`TotalObjectCount`/`GetObjectAt` 语义失真，且每次新加连接都要线性扫过全部历史对象。

现在：销毁与创建回滚都走 `DetachFromAllObjects`（槽位置 null + 解除强引用），必要时延迟压缩
（死槽位 ≥ 32 且 ≥ 存活数时压缩；全部死光直接清空）；`_allObjects.Count` 因此有界于 `2 * MaxObjects`。
登记顺序保持不变（引用方仍排在被引用方之后），`GetObjectAt` 只遍历存活对象。
`TotalObjectCount` 改为存活口径，**累计**创建数由既有的 `Stats.ObjectsSpawned` 承担；
新增只读 `AllObjectSlotCount` 供门禁断言「无界增长已消除」。NetId 分配器**不复位**（D-R0-02 保持）。

## 6. RV4 脏位与 256 位契约一致（F5）

`PMDirtyTracker` 位宽 64 → 256（与 `PMRepMask.MaxBits` 一致），越界 `Mark`/`IsDirty` **明确抛异常**
（旧「置满退化」会把声明错误变成性能问题）。`TryClearSettledDirty` 不再把上限定成 `min(SlotCount, 64)`：

1. `[0, SlotCount)` 内逐位按「所有连接都已追平当前值」清除；
2. 若区间内已无剩余脏位，再 `ClearBitsAbove(SlotCount)` 清掉 `MarkAll()`/`FlushNetDormancy()` 留下的
   `[SlotCount, 256)` **非属性伪位**。

否则 `Dirty.HasAny` 永远为真，对象终身每 Tick 空扫（并让 `SuppressedUnchanged` 无界增长）。
现测试直接断言 `MarkAllPropertiesDirty()` / 休眠唤醒（`MarkPropertyDirty` 隐含 `FlushNetDormancy`）收敛后
`HasAny == false`、且稳态后续 Tick 不再产生载荷；70 属性类覆盖槽位 ≥ 64。

## 7. 声明式 Create 初值（D-R0-16 在复制路径上闭合；F2）

**不改生成器**：生成器不发射 `OnSerializeInitialState`，因此声明式复制类的 Create 记录常态没有初值，
只能靠复制层另发一条 **Unreliable** 全量更新来补 ⇒ 破了「创建与初始状态同包」（对象已 Active 但字段全默认值的可见窗口，且跨顺序域到达）。
现在由**世界**在 Create 构包/应用路径上补声明式初值：

- **构包**（`BuildReplicatedInitialState`）：从 `PMNetRegistry` 的静态描述符取属性（**零反射、可编译期裁剪**），
  按**接收连接**过滤后编码为 `(slot | propertyId | valueLen | value)*`（自定型，无条数前缀）。
- **手写钩子优先**：`OnSerializeInitialState` 写出了内容就以它为准，不再补声明式初值 —— **不重复、不覆盖**，
  手写钩子的现有语义与字节布局完全不变（`I12`–`I14` 逐字段核对）。
- **应用**（`TryApplyReplicatedInitialState`）：在 `OnReplicatedCreate()` **之前**逐条校验
  （条数 ≤ 属性数、槽位不越界、属性 ID 一致、值**恰好读完**）；任一条失败 ⇒ **整个创建回滚、不发布**
  （索引与登记表一起摘掉、`RolledBackCreate++`、不触发创建回调）。
- **条件过滤是必需的，不是优化**：初值包含哪些属性取决于接收连接。
  过滤源 = 新增的 `PMNetWorld.ReplicatedPropertyFilter`（签名 `(obj, connection, slot) → bool`），
  由 `PMReplicationChannel` 接入**与增量更新同一套**条件求值链（角色解析 + `Custom`/`Dynamic` 覆盖），
  接线点在 `AddConnection` / `RegisterObject` / `Tick`，因此 **Create 早于首次 Tick 也正确**。
- **未接线时的明确安全行为**：只发 `PMCond.None` 的属性（漏发可由复制层「基线缺失 ⇒ 全量」补齐，多发就是泄露）。
  另外 `PMCond.Never` **先于**过滤器被排除（纵深防御：即便接线方给全放行过滤器也不泄露）。
- **不乐观推进基线**：Create 路径完全不碰复制层的 per-connection 基线；没有 ACK 就不声称确认。
  **带宽代价（诚实口径）**：Create 之后复制层仍会发一次该对象相对该连接的全量 Unreliable 更新
  （基线仍缺失），即同一份初值在可靠域与不可靠域各出现一次 —— 代价约等于一份全对象状态/对象/连接，
  换来的是「不依赖 ACK 也能收敛」与「Create 原子可见」。

## 8. 实测（真实构建 + 运行，退出码为准）

> **RV2 返工第二轮的实际执行范围**：本轮未改动 `World/**`，所以 `PMNetWorldTest` 的数字沿用上一轮、**本轮未重跑**。
> 本轮只重新构建并运行了 `PMReplicationTest`（build 0 错误后才 run），并额外做了下面表格最后三行的注入验证。

```
dotnet build Tools/PMReplicationTest -c Release     -> 0 错误
dotnet Tools/PMReplicationTest/bin/Release/net8.0/PMReplicationTest.dll
     干净实现：通过 265 项，失败 0 项；缺陷注入：5/5 命中；结果：PASS；exit = 0
# —— 以下两行是上一轮（RV3 世界层）的结果；本轮未改动 World/**，因此未重跑 ——
dotnet build Tools/PMNetWorldTest -c Release        -> 0 错误
dotnet Tools/PMNetWorldTest/bin/Release/net8.0/PMNetWorldTest.dll
     全部通过：197 项检查，0 项失败；exit = 0
```

旧基线为 129 项（复制层）/151 项（世界层）全绿；本次新增 97 + 46 项，且**旧断言一条未删、未放宽**。
RV2 返工第二轮再新增 39 项（复制层 226 → 265），同样一条未删、未放宽。

**新增测试确实击中旧缺陷（临时注入 → 构建 → 运行 → 按 md5 恢复，已核验恢复）**：

| 注入的旧行为 | 文件 | 被抓住的断言 |
|---|---|---|
| 读侧掩码按 ulong 下标 | `PMReplicationReader.cs` | `J6`/`J7`/`J8`（另有 N9/N10 连带） |
| 解码直写活对象（去掉暂存） | `PMReplicationChannel.cs` | `L5c`（前项未写入）、`L10` |
| 销毁/回滚不摘强引用 | `PMNetWorld.cs` | `B2`/`G1`/`H3`/`H6b`/`H7`/`H8`/`H8b` |
| 脏位清理上限 64 且不清伪位 | `PMReplicationChannel.cs` | `N7`/`N10`/`N10b`/`N11`/`N13` |
| Create 初值不做条件过滤 | `PMNetWorld.cs` | `I3`/`I6`/`I7`/`I9b` |
| 暂存工厂前置校验被移除（RV2 返工第二轮） | `PMReplicationChannel.cs` | `Q1`/`Q2`/`Q3`/`Q4`/`Q5`/`Q7`（失败 6 项；`Q6` 因工厂缓存而仍过，见 §4.3） |
| 接收侧「每写一个属性就回调」（RV2 返工第二轮） | 门禁缺陷子类 `F4`（`DispatchOnRepPerProperty=true`） | `O4` |
| OnRep 异常不隔离（RV2 返工第二轮） | 门禁缺陷子类 `F5`（`IsolateOnRepExceptions=false`） | `[P.` 测试体异常（异常穿透 `OnMessage`，ACK 丢失） |

覆盖矩阵：掩码位宽/槽位 `1/7/8/9/31/32/33/63/64/65/255/256` 的编解码往返；
9 属性类与 256 属性类的**通道路径**（槽位 7/8/31/32/63/64/255 真的到达并被 ACK、回写无残留、邻居未被误写）。

## 9. 与并行组 / E2E 的兼容点（需主 Agent 复核）

1. **生命周期线格式版本 1 → 2**：Create 记录尾部新增 `repState`。两端必须同版本；
   旧读端会把新字节判为「尾部多余」而**整条拒绝**（安全方向）。
   `PMNetE2E` 未手工构造生命周期字节 ⇒ 预期不受影响；若后续会话在 E2E 里手写字节，
   版本位请用 `PMLifecycleCodec.FormatVersion`（本次已把 `PMNetWorldTest` 的 A3–A8/E6 改成该常量，
   避免它们退化为「只测版本不符」）。
2. **`PMNetClassEntry.Factory` 现在是接收侧的硬前置**：暂存解码需要它；缺失时**整条更新被拒绝**
   （明确告警 + `RecordsRejected`）。已核对生成物 `Generated/*.g.cs` 均在 `PMNetBuildEntry` 里设置了
   `entry.Factory` ⇒ 真实生成路径满足该前置。
3. **OnRep 次数**：声明式初值**不**派发 OnRep（对象在创建期，业务入口是 `OnReplicatedCreate`），
   因此 Create 本身不会改变 OnRep 计数；Create 之后复制层首次全量更新照旧派发 OnRep（与改造前一致）。
   `ApplyRecord` 失败路径现在**不派发 OnRep**（旧实现可能已派发前几个）⇒ 若 E2E 有「畸形包导致 N 次 OnRep」
   这类断言，需按新语义调整。
4. **`UpdateMessagesApplied` 口径收紧**：一条消息里所有记录都被丢弃时不再自增（O2）。
5. **描述符/委托签名未改**：`PMPropertyDescriptor` / `PMReplicationDescriptor` / `PMRpcEntry` 等冻结字段
   与委托签名保持原样；本次只做**兼容性新增**（`PMRepObjectEntry.Factory`、
   `PMNetWorld.ReplicatedPropertyFilter`、`AllObjectSlotCount`、`PMRepStats` 两个计数、
   `ShouldAdvanceWithoutInflight` 决策点、`PMDirtyTracker` 的几个方法）。
6. **行为变更（可能影响外部调用者）**：`PMRepMask.GetByte/SetByte` 改为字节下标（旧语义下 `GetByte` 无调用者）；
   `PMDirtyTracker.Mark/IsDirty` 越界由「置满/返回 HasAny」改为抛异常；`PMNetWorld.TotalObjectCount` 由「历史累计」改为「存活」。
7. **RV2 返工第二轮的新增面（均为加法，不改既有签名）**：
   - `PMReplicationChannel.OnRepExceptions` / `CommitFailures` / `StagingRejected` 三个 **public 计数字段**。
     刻意**不**放进 `PMRepStats`（那一组是线协议统计；这三个是「本端接线 / 描述符契约错误」指标，
     混在一起会让「网络不干净」与「代码接错了」无法区分）。
   - `PMReplicationChannel.DispatchOnRepPerProperty()` / `IsolateOnRepExceptions()` 两个 `protected virtual`
     决策点（默认即契约，与既有五处同源；仅供门禁注入缺陷）。
   - **行为变更（接收侧）**：OnRep 由「每写一个属性派发一次」改为「整条记录提交完成后统一派发」；
     OnRep 异常不再穿透 `OnMessage`，也不影响该记录的 ACK。
   - `PMRepObjectEntry.Factory` 的**前置条件已声明并强制**（返回活对象 / 已登记对象 ⇒ 整条记录被拒）；
     生成产物 `PMNet_CreateInstance()` 天然满足（就是 `new` 一个新实例）。
8. 门禁内部变化（不影响生产）：接收侧通道也由 `_senderFactory` 构造，使接收路径的两处决策点可被注入。

## 10. 剩余限制与未闭合项（诚实记录）

1. **未做**：UDP/Unity 宿主接线、R3 握手、真实 MTU/分片实测；内存投递验收不等于真实网络验收。
2. **未编译** `PMNetLangCheck`（不在本次构建边界内）。改动按 C# 7.3 / netstandard2.0 自审：
   未使用新语法、`Span`、`BitOperations` 等越界 API；建议主 Agent 串行跑一次该门禁。
3. **未跑** `PMNetE2E`（并行组文件）；按 §9 的判断预期保持绿，但需主 Agent 用真实生成产物验收
   Create 初值路径（本组只做了「手工注册描述符」的独立世界验收）。
4. `ReplicatedPropertyFilter` 是**每世界单槽**入口：多个发送方接入同一世界时后写入者生效
   （已写入注释；真实拓扑下每世界一个发送方）。
5. 仍未闭合（沿用 §5.4 原登记）：数组/字符串上限值随 MTU 收敛（#4）、继承链基址跨程序集静默按 0（#5）、
   缺值比较委托（#6）、复制层无字节预算/校验和（#8）。
6. `PMDirtyTracker` 从 64 → 256 位使 `PMNetObject` 变大（+24 字节/对象）；`Bits` 属性现在只返回低 64 位
   （旧断言若依赖它需注意）。
7. 切片粒度：本次只覆盖「服务端 → 客户端」下行与手工描述符；客户端上行为 R3 之后才有真实宿主。
8. **原子性的边界尚未闭合**（见 §4.4）：`Reader` 带副作用（或只在活对象上抛）时，`3a` 可能部分提交，
   本层不做 `Writer` 回滚（回滚自身失败不可见，造不出保证），只如实暴露 + 不回 ACK + 由发送侧重发收敛。
   要闭合它需要描述符层面禁止非纯 Reader（生成器侧约束）或给提交阶段一个真正的原子上报通道，
   两者都超出本轮写边界。
9. **接收侧没有「按版本去重」**：同一条记录被重复投递（重放 / 伪造更高版本号）会再次派发 OnRep。
   本轮靠「已完整提交 ⇒ 回 ACK ⇒ 发送侧不再重发」把**正常路径**的重复副作用堵住（`P10`/`P11`），
   但没有 per-object 已应用版本水位；真正的重复投递仍会重复回调。
