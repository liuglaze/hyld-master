# R2 实现大体把关审查（RPC / 代码生成 / 复制槽位）

> 审查范围：`Tools/PMNetGen/Decl*.cs`、`Client/Assets/Scripts/PMNet/Declarations/**`、`PMRpc.cs`、
> `PMNetReader/Writer.cs`，直接引用边界含 `PMNetObject` / `PMNetConnection` / `Tools/PMDeclCheck` / `Tools/PMNetE2E` 的相关测试。
> 入口文档：`AGENTS.md`（根）、`Client/Assets/AGENTS.md`、`Server/AGENTS.md`、
> `Docs/plans/net-r2-codegen-contract.md`、`net-architecture-migration.md` §5.4–§5.6 / T40–T41、`net-r0-contract.md`。
> 纪律：只读；不编译、不改源码、不递归委派。所有结论都标到 `文件:行号`。
> 判定口径：**仅报告可证明的正确性问题**；区分「已知缺口」（§5.4 已登记）与「新发现」，并区分「当前可达」与「未来接线风险」。

---

## 1. 已确认（可从源码逐条复现）

### C1 [P1] 调用桩闭包对**数组参数**不是真快照（引用捕获 + 编码被推迟）

**证据链**

1. 发射器把实参编码放进闭包，但对数组参数**只捕获引用**：
   - `Tools/PMNetGen/DeclEmitter.cs:604-616` — 生成
     `EnqueueRemote(this, PMGeneratedRpcId_X, delegate(PMNetObject t, PMNetWriter w) { <EmitWriteBody(p0)> })`。
   - `Tools/PMNetGen/DeclEmitter.cs:835-853`（`EmitWriteBody` 的 `Array` 分支）— 生成的是
     `T[] a = p0;`（**没有 copy**）+ `w.WriteSInt32(a.Length); for (int i = 0; i < a.Length; i++) { <写 a[i]> }`。
     `a.Length` 与 `a[i]` 都是**编码时刻**才读的，`p0` 本身就是调用方那个数组对象。
2. 编码确实会被推迟：`Tools/PMNetE2E/Generated/PMNetGeneratedRegistry.g.cs:70-105`
   —— `RemoteSender == null` 时把该 delegate 存进 `_pendingRpcs`（`Queue<PMNetPendingRpc>`），
   真正的编码发生在 `TryDequeueRpc` 之后（或在未来的批量/帧末发送里）。
3. **当前树里 `RemoteSender` 从未被赋值**（全仓 `grep "RemoteSender\s*="` 零命中；只有 `DeclEmitter.cs:142` 发射它的声明）。
   ⇒ 现状下**所有**远端 RPC 都走上面这条延迟队列，推迟窗口是 100% 常开的。

**最小触发**

```csharp
[PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
public void Push(float[] v) { /* ... */ }
```
```csharp
float[] buf = new float[] { 1f, 2f };
obj.PMNet_Push(buf);              // 进待发队列
buf[0] = 99f;                     // 调用之后、编码之前就地改写
PMNetGeneratedRegistry.TryDequeueRpc(out var p);
p.Write(p.Target, w);             // 线上是 99f —— 不是调用时刻的 1f
```

**实际后果**：与 §5.5 缺陷 1（实参帧被后续调用覆写）**同类**，但换成了可变引用类型；
`string` 因 C# 不可变而安全、标量安全，**`T[]` 不安全**。
契约 §6 明确把 `T[]` 列为「复制与 RPC 参数共用」的受支持类型，规则 4 也放行，
所以这不是"不支持的类型"，而是支持的类型上带着静默错误。

**文档口径错误（需一并修正）**：`net-r2-codegen-contract.md` §4.3.1 与生成物注释
（`Generated/PMNet.PMNetE2E.E2eReplicated.g.cs:171-175`、`PMNetDeclarations.cs:355-358`）断言
「闭包捕获没有这个窗口」「编码推迟到真正发送时也不会读到被覆盖的值」——
该论断**只对值类型成立**，对引用类型不成立。

**门禁为何漏检**
- 所有 RPC 夹具都只有标量参数：`Tools/PMNetE2E/E2eFixtures.cs:90`（`Fire(int,float)`）、`:120`（`Notify(int)`）、
  `Tools/PMDeclCheck/Fixtures/Good.cs:88`、`:145`（`Teleport(float,float,float)`）；**全仓没有任何数组参数的 RPC 声明**
  （`grep` 仅命中 `PMNetRpcEntry[]` 这类无关数组）。
- `P4 ★核心不变性`（`Tools/PMNetE2E/Program.cs:830-880`）只在 `int/float` 上断言"实参未被后续调用覆盖"，
  是**标量替身证据**，不能外推到引用类型。
- 负向注入 #1 用 `FaultyArgFrameSender` 替身（`Program.cs:1173-1195`）复刻**已被删除的旧设计**；
  它是有效的"断言非空"对照，但**不是**对现行闭包设计在引用类型上的 mutation 证据。
  ⇒ 结论：**现有测试无法发现 C1**。

---

### C2 [P1] RPC 归属校验（D-R0-42）**未接线**；且接收侧结构上无法校验

**证据链**

1. `Client/Assets/Scripts/PMNet/PMRpc.cs:497-503` 的 `PMRpcDispatch.ShouldCallRemoteFunction`
   实现正确（`(!receiverIsServer || receiverIsObjectOwner) && !ignoreRpcs`），
   但**生产代码里零调用点**。全仓调用者只有两个测试：
   `Tools/PMNetE2E/Program.cs:966-973`、`Tools/PMCallspaceCheck/Program.cs:398-411`。
2. 接收侧入口的委托签名**没有连接/接收方身份参数**：
   `PMNetDeclarations.cs:349` `public delegate void PMRpcInvoker(PMNetObject target, PMNetReader reader);`
3. 生成的接收分发同样不含归属判定：`Generated/PMNet.PMNetE2E.E2eReplicated.g.cs:180-205`
   —— 读参数 → 跑 `Fire_ForceValidate` → 直接 `self.Fire(p0, p1)`，全程不接触 `PMNetConnection`。

**实际后果**：D-R0-42 与 T20（"未授权 Server RPC 被拒"）在接收点没有落点。
反外挂链路上，ForceValidate 只覆盖"业务参数合法性"，**覆盖不到"这条连接有没有资格调这条 RPC"**。
`IgnoreRpcs`（`PMNetConnection.cs:24`）同样无消费者。

**门禁为何漏检 / 文档口径失真**
- `Tools/PMNetE2E/Program.cs:964-974` 的 `TestOwnershipCheck`（R1–R4）是**对谓词的纯函数断言**，
  从未把包从"非 Owner 连接"投递到 `entry.Invoke`。
- 但主计划 §5.6 的 T40 表格把「归属校验拒绝非 Owner」列为端到端已覆盖项 ——
  这是**把函数级测试记成了集成级证据**，属口径失真（不是新代码缺陷，而是门禁描述问题）。

---

### C3 [P2] 接收侧 `Validate`（原生、失败断连）分支**零覆盖**

**证据**

1. 分支代码在 `DeclEmitter.cs:703-716`（`if (!self.<M>_Validate(...)) { NotifyValidateFailed(...); return; }`）。
2. 全仓 `*.g.cs` 搜不到 `_Validate(`（零命中）⇒ **该分支从未被发射过**。
3. 正向夹具缺失：`Tools/PMDeclCheck/Fixtures/Good.cs` 无 `Validator = PMRpcValidator.Validate` 的用例；
   `Bad.cs:350` 只有负向用例（预期校验失败、永不进入发射阶段）。
4. `PMDeclCheck` 唯一一次"生成物可编译"检查（`Tools/PMDeclCheck/Program.cs:868-870`）也只编译 `Good.cs` 的产物。

**实际后果**：契约 §4.3.2 第二行（`Validate`：`false` ⇒ 上报并请求断连、不执行实现）
既无编译覆盖也无运行覆盖。该分支若写错（同伴签名、返回值处理、断连钩子调用），
门禁全绿也不会暴露 —— 这正好是 §5.5 缺陷 2/#2b 的同类风险面，只是换了个档位存活下来。

---

### C4 [P2] 失败关闭只在**声明期**成立；发射器自身没有断言（"注释兜底"是 fail-open）

**证据**

- `DeclEmitter.cs:718-724`（`EmitValidationRouting` 末尾分支）：
  当"档位 ≠ None 但同伴不可得"时，**只发射一条注释**，随后照常 `self.<M>(...)` 执行实现。
  即：产物形态是"声称有校验、实际没有"。
- 该分支当前不可达，因为 `Program.cs:485-500` 保证 `Model.Ok == false` 时拒绝发射，
  而规则 13（`DeclValidation.cs:877-920`）会拦住"档位有、同伴无"。
- 但这是**声明期门禁在兜底**，发射器自己没有 fail-closed 断言。
  代码注释（`DeclEmitter.cs:706-710`）声称"保证「声称有校验却没有校验调用」不可能被静默发射出来" ——
  **该论断不成立**：注释不是断言。

**未来接线风险**：`PMDeclEmitter.EmitAll` 是公开 API，`PMDeclCheck`（`:450/493/669/870/1056`）与
`PMNetE2E` 都直接调用它，不经过 CLI 的校验前置。任何"用 subset/游离 facts 调 EmitAll"的路径
（增量生成、编辑器内生成、未来工具复用）都会静默 fail-open。
**最小硬化**：该分支改为 `#error` 风格产出（或抛异常），不要只留注释。

---

### C5 [P2] 接收侧：校验发生在**解码之后** ⇒ 异常路径绕过校验、且没有失败通道

**证据**

1. 生成顺序：先读全部参数（`E2eReplicated.g.cs:183-185`），再调校验同伴（`:188`）。
2. 解码**会抛** `FormatException`：
   - 数组长度越界：`DeclEmitter.cs:930-935`（`throw new System.FormatException("PMNet 复制数组长度越界：…")`）；
   - 字符串超限：`PMNetDeclarations.cs:264-270`（`ReadBounded` 在分配前预检）；
   - reader 越界：`PMNetReader.cs` 的 `ReadFixed32/64`、`ReadStringValue`、`ReadRawBytesCopy`、`ReadRawByte` 等。
3. 生成的 invoke **没有 try/catch，也没有错误返回通道**（返回 `void`），异常直接向外抛。

**实际后果（分三点，对应任务问的"非法枚举 / 异常 / 尾部能否绕过"）**

- **异常可绕过校验**：畸形载荷在到达 `ForceValidate` 同伴之前就抛异常 ⇒ 反外挂侧对这类包**没有任何记录**，
  且 `PMRpcValidationSink` 也不会被调用。契约 D-R0-46 要求"布局不匹配 = 协议不兼容（断连）"，
  而现产物既不 catch、也不产生可消费的错误信号。
  *（R2 无法判定严重度：接收循环尚不存在，是否有每包 catch 属 M03/M05 范围 —— 见"无法确定"。）*
- **尾部未校验**：invoke 读完声明参数后**不检查 `reader.IsAtEnd` / `Consumed`**，
  尾随字节被静默忽略；`ParamLayoutId`（`PMNetDeclarations.cs:210`）只在描述符与全局摘要里
  （`PMStableHash.cs:319`）参与握手比较，**接收点不做单条 RPC 的布局校验**。
  布局漂移只能靠 M03 握手兜底（R2 不可验证）。
- **非法枚举**：`DeclEmitter.cs:986-988` 生成 `(<EnumType>)r.ReadEnum()` —— **无范围校验**，
  任意 int 直接进业务逻辑。契约 §6 未强制枚举校验，于是"失败关闭"完全落在业务同伴身上，
  生成器与运行时都没有闸门；一条 `Validator = None`（或同伴漏判）的枚举参数 RPC 就是无约束入口。

---

### C6 [P2] 待发队列「丢最旧」与可靠 RPC 语义冲突；调用点丢掉了可靠性与收件人信息

**证据**

- 队列上限与丢弃策略：`PMNetGeneratedRegistry.g.cs:64-105`（`MaxPendingRpcs = 1024`，超限 `Dequeue()` + `DroppedPendingRpcs++`）。
- 契约对照：D-R0-06「`Reliable`：连接级序号、保序、去重、**要求送达**」；
  D-R0-07「可靠缓冲溢出 = **断开连接**（不静默丢弃）」。
  把可靠 RPC 按"容量满丢最旧"处理（代码注释自称"与投射物同口径"）是**类别错误**：
  投射物命中结论是尽力而为缓存，可靠 RPC 是送达保证。
- 可达性：仅当 `RemoteSender == null` 时队列有内容 —— 而当前树 `RemoteSender` 从未赋值，
  即**现状 100% 的远端 RPC 都落在这条队列上**；文档把它定位为"开发/测试形态"，
  故本条按"当前可达但语义被文档豁免"处理。
- 另一处接口表达力缺口：调用桩只传 `(target, rpcId, write)`（`DeclEmitter.cs:604-606`），
  **不传 `IsReliable`、不传收件人集合**。而 Multicast 的语义是"相关连接集合"、不是空间广播（D-R0-44），
  Client RPC 是"拥有者连接"。M05 接线时必须自行从描述符 + Owner 反推，
  接口本身没有任何机制阻止实现退化成广播。

**门禁为何漏检**：E2E 只断言"有界 + 被计数 + 丢最旧"（`Program.cs:752-786`），
没有断言"可靠 RPC 不得静默丢弃"，所以这条语义冲突在门禁视角下不存在。

---

### C7 [P1/P2，**新发现**（与 §5.4 #5 不是同一件事）] 继承类：生成期标脏索引与 R2 复制层槽位索引**不是同一个索引空间**

**证据链**

1. 生成期是"**对象级连续索引**"：
   - `PMGeneratedPropertyIndexBase`（`E2eReplicated.g.cs:32`，发射于 `DeclEmitter.cs:325`）；
   - setter 用 `MarkPropertyDirty(indexBase + i)`（`DeclEmitter.cs:426`）；
   - rep list 用 `outProps.Add(indexBase + i, …)`（`DeclEmitter.cs:395`）。
2. R2 复制层（真正的消费者）是"**类内描述符下标**"：
   - `PMReplicationChannel.cs:805` `obj.Dirty.IsDirty(slot)`、`:815` `obj.Dirty.ClearBits(1UL << slot)`、
     `:1198` `entry.SlotCount = desc.Properties.Length`，`slot ∈ [0, SlotCount)`；
   - 属性元数据也按同一槽位取：`entry.Descriptor.Properties[slot]`。
3. **同批扫描的继承链**（正是 `PropertyIndexBase` 被设计出来支持的正常场景）：`indexBase = N > 0`
   ⇒ `MarkPropertyDirty(N + i)` 落在复制层**永远不看、也永远不清**的位上（`TryClearSettledDirty`
   只清 `0 … min(SlotCount, 64) - 1`，`PMReplicationChannel.cs:786-816`）。
   实际后果：派生类对象的 `Dirty.HasAny`（`:528`）恒真 ⇒ **Push 短路彻底失效**、
   每轮都进比较集合、脏位泄漏（`§5.4 #7` 的"Push 收益削弱"在继承类上退化为"完全失效"）。
   正确性由 D-R0-13 的"值比较"兜住 —— **不会发错值**，所以这不是数据正确性 bug，而是性能/设计不一致 bug。
4. **跨批/跨程序集继承**（= 已登记的 `§5.4 #5`）：`DeclScanner.cs:1169-1211` 的
   `ComputePropertyIndexBase` 在 `!declared.TryGetValue(resolved, out baseCls)` 时**直接 break，无告警**
   ⇒ `indexBase` 静默按 0。此时可补一条 #5 未写出的具体后果：
   `PMRepList.Add` 不做唯一性校验（`PMNetProperty.cs:52-55`），`TryGet` 返回**首个匹配**（`:58-71`）
   ⇒ rep list 里基类/派生类同号，派生属性的**条件会被解析成基类条目的条件**
   （例如 `OwnerOnly` 被读成 `None`，即本该只发给拥有者的属性被放宽）。
   *当前 inert*：rep list 路径未被复制层使用（唯一调用者是 `PMNetObject.ShouldReplicateProperty`，
   而它只被 `Tools/PMReplicationTest/Program.cs:837-846` 用作交叉校验）；
   一旦 M04/M05 用 `GetLifetimeReplicatedProps()` 做条件判定，这条就会生效。

**门禁为何漏检**：两个索引空间在测试里**被碰巧对齐**了 ——
E2E 的 `E2eReplicated` 直接继承 `PMNetObject`（`indexBase = 0`），且**全部夹具里没有任何
`[PMNetworkObject]` 继承另一个 `[PMNetworkObject]`**；`PMReplicationTest` 用手写描述符 +
`MarkPropertyDirty(i)` 直接传类内下标（`:461/693/898/1046/1150`）。

---

## 2. 高概率推断（附依据与置信度）

| # | 推断 | 依据 | 置信度 |
|---|---|---|---|
| H1 | M05 一旦把 `RemoteSender` 实现为"入包/批量缓冲、帧末统一编码"，C1 立刻从"潜在"变成**生产发错参数** | C1 证据链 + §4.3.1 要求"必须同步编码"只能靠实现者自觉（`grep "RemoteSender ="` 零命中 ⇒ 该约束没有任何可判定的载体） | 高 |
| H2 | C7 在"首个基线未建立"的 `initialFull` 路径下被掩盖（首次全量发送不受脏位影响），只有进入稳态后才表现为"每轮全属性重编码" | `PMReplicationChannel.cs:530-545`（`UnknownBaselineCount/ForceInclude/HasAny/条件跃迁` 四条都返回 true，比较才是真判据） | 中 |
| H3 | 宿主未接线时 `PMRpcValidationSink` 只 `Unhandled++`（`PMNetDeclarations.cs:316-345`），对 `Validate` 而言"未接线"= 既不断连也不执行 ⇒ 行为上等价于软拒绝、反外挂侧无痕 | 该 sink 两个钩子默认 null 且没有任何默认断连动作 | 中高 |
| H4 | 类属性数进入 65..256 区间时，"有界"闸门（`PMRepMask.MaxBits = 256`）放行超过脏位位宽（64）的类，脏位按 `Mark()` 的"置满"退化（`PMNetProperty.cs:99-106`），表现为该对象长期参与比较 | 两处常量口径不一致：复制层 256 vs `PMDirtyTracker` 64 | 中 |

---

## 3. 无法确定（缺少证据）

1. **接收循环是否存在每包 try/catch**：M03/M05 未接线，仓库内不存在 RPC 接收入口
   ⇒ C5 的"异常是否演变为 DoS"、"D-R0-46 布局不兼容是否真的断连"均**不可判定**。
2. **握手是否真的比对 `PMNetRegistry.ProtocolHash`**：属 R1/M03 范围；R2 只产出了摘要值
   （`PMStableHash.cs:319`、`PMNetDeclarations.cs:485-490`），没有比对方。
3. **`RemoteSender` 未来是否会同步编码**：直接决定 C1 是否立即生效（H1）。
4. **跨程序集继承的实际发生率**：取决于后续业务如何切程序集；`§5.4 #5` 的处置建议是"写进使用约定"，
   属流程约束而非代码约束，无法从代码判定。
5. **数组长度上限 4096（`DeclEmitter.cs:44`）与 MTU 的关系**：契约 §8 只说"按 §9 MTU 反推"，
   MTU 未冻结（`§5.4 #4`），无法判定 4096 是否安全。
6. **`ParamLayoutId` 在接收点被用于校验的可行性**：当前只在描述符里，没有任何消费点。

---

## 4. 已检查范围（文档与路由）

**项目文档（按任务给定顺序完整读）**

- `D:/UGit/hyld-master/AGENTS.md`（全文，15 行，作为入口路由到 Client/Server 文档）。
- `D:/UGit/hyld-master/Client/Assets/AGENTS.md`（全文）。
- `D:/UGit/hyld-master/Server/AGENTS.md`（全文）。
- `Docs/plans/net-r2-codegen-contract.md`（**全文**，§1 冻结接口 / §2 ID 算法 / §3 协议摘要 /
  §4 生成物 API 面（含 §4.3.1 实参闭包、§4.3.2 校验路由）/ §5 规则 1-13 / §6 类型集 / §7 委派边界 / §8 待收敛参数）。
- `Docs/plans/net-r0-contract.md`（§0 验收口径、§1 来源映射、§2.1–§2.8 冻结决策
  （D-R0-02/05/06/07/12/13/14/15/16/18/41/42/43/44/45/46/48/49/50）、§3.4/§3.5 契约类型、§5 复制与生命周期、
  §6 预测与 Mover（身份路由）、§7 投射物、§8 验收映射、§9 待收敛、§10 抽查）。
- `Docs/plans/net-architecture-migration.md`（§3.9.3 模块划分、§3.9.4 运行时契约、§3.9.5 声明生成、
  §5.4 待收口缺陷 8 项、§5.5 返工 3 项 + 2b、§5.6 收口与最后两项修正、§6 测试集、§6.1 T40/T41 行、§10 交接记录第 17 条）。

**源码（只读到函数/分支级，未整文件通读无关部分）**

- `Tools/PMNetGen/DeclEmitter.cs`：`:44`（`MaxArrayLength`）、`:139-186`（注册表/`EnqueueRemote` 模板）、
  `:263-336`（`indexBase` 与常量段）、`:382-430`（`EmitRepList`/`EmitSetters`）、
  `:522-620`（`EmitRpcs`：接收分发 + 调用桩闭包）、`:665-724`（`EmitValidationRouting` 三分支 + 末尾注释分支）、
  `:824-995`（`EmitWriteBody`/`WriteStatement`/`EmitReadBody`/`ReadExpression`）。
- `Tools/PMNetGen/DeclScanner.cs`：`:484-520`（排序与 ID 分配顺序）、`:1169-1211`（`ComputePropertyIndexBase`）、
  `:1055-1130`（成员排序）、`:1548-1615`（基类型解析）。
- `Tools/PMNetGen/DeclValidation.cs`：`:257-330`（`RuleCount = 13` 与规则注册）、`:862-935`（规则 13 全文与 `FindRpc`）、
  `:982-1050`（规则 12 与相邻检查）。
- `Tools/PMNetGen/Program.cs`：`:20-40`（CLI 契约）、`:434-500`（`--decl-gen/--decl-check` 前置、扫描、**校验先于发射**、拒绝发射）。
- `Tools/PMDeclModel/PMIdLock.cs`：`:90-197`（`AllocateClass`/`AllocateMember` 探测与 `0` 保留）。
- `Tools/PMDeclModel/PMDeclModel.csproj`（链接运行时 PMNet 核心 → 哈希"唯一实现"证据）、`Tools/PMNetGen/PMNetGen.csproj`。
- `Client/Assets/Scripts/PMNet/PMRpc.cs`：`:186-232`（三个 RPC Attribute 默认值）、`:240-260`（注释中声明"接收侧归属校验"）、
  `:481-503`（`ShouldCallRemoteFunction` 实现）、`:505-530`（`ShouldExecuteLocal/ShouldSendRemote`）。
- `Client/Assets/Scripts/PMNet/Declarations/PMNetDeclarations.cs`：`:231-272`（`PMNetString` 有界读写）、
  `:274-306`（`PMRpcValidation` 三态）、`:308-345`（`PMRpcValidationSink` 两钩子 + `Unhandled`）、
  `:349-359`（两个 delegate 签名）、`:370-405`（`PMNetRpcEntry`/`PMNetClassEntry`）、`:409-505`（`PMNetRegistry` 注册/封板/`TryGetRpc`）。
- `Client/Assets/Scripts/PMNet/PMNetReader.cs`（全量读：越界与 `FormatException` 行为、`PeekVarintLength`）。
- `Client/Assets/Scripts/PMNet/PMNetProperty.cs`：`:33-106`（`PMRepList.Add/TryGet` 无唯一性校验、`PMDirtyTracker.Mark/IsDirty/ClearBits` 的 64 位与"置满"退化）。
- `Client/Assets/Scripts/PMNet/PMNetObject.cs`：`:96-130`（`GetNetConnection`）、`:160-190`（`GetLifetimeReplicatedProps`）、
  `:255-330`（`ShouldReplicateProperty` 与 `MarkPropertyDirty`/`Dirty`）。
- `Client/Assets/Scripts/PMNet/Replication/PMReplicationChannel.cs`：`:505-560`（脏对象/条件跃迁/`HasAny` 门控）、
  `:770-830`（`IsAckStale`/`TryClearSettledDirty`/`IsSlotFullyConfirmed`）、`:1140-1205`（描述符校验与 `SlotCount`）。
- `Client/Assets/Scripts/PMNet/PMNetIdentity.cs`（`PMNetId`/`PMNetIdAllocator`/`PMSession`/`PMFrameId` 头 300 行扫描式核对，确认 RPC 路径未消费）。
- `Client/Assets/Scripts/PMNet/PMNetConnection.cs`（`IgnoreRpcs` 字段）。
- `Tools/PMNetE2E/E2eFixtures.cs`（全文，RPC/属性/RepNotify 夹具形态）。
- `Tools/PMNetE2E/Generated/PMNet.PMNetE2E.E2eReplicated.g.cs`（**全文**：调用桩闭包、接收分发、描述符、`PMNet_BuildEntry`）、
  `Generated/PMNetGeneratedRegistry.g.cs`（**全文**：`EnqueueRemote`/`MaxPendingRpcs`/`RegisterAll`/`Seal`）、
  `Generated/PMNet.PMNetE2E.E2eScoreboard.g.cs`（计数核对）。
- `Tools/PMNetE2E/Program.cs`：`:1-40`（门禁结构自述）、`:714-830`（RPC 调用桩/O 系列/有界队列）、
  `:830-880`（P1–P5 实参闭包）、`:886-975`（Q 系列三态 + R1–R4 归属）、`:1167-1270`（负向注入与替身形态）。
- `Tools/PMDeclCheck/Fixtures/Good.cs`（RPC/属性声明形态核对）、`Bad.cs`（规则 13/8/9 负向用例核对）。
- `Tools/PMDeclCheck/Program.cs`（`:97-110`、`:396-520`、`:640-680`、`:860-880`、`:1010-1060` 相关段：不变性、生成物编译、哈希重算）。
- `Tools/PMReplicationTest/Program.cs`（`:224-250`、`:455-470`、`:690-760`、`:1040-1160` 相关段：手写描述符 + `MarkPropertyDirty(i)` 的索引口径）。
- `Tools/PMCallspaceCheck/Program.cs`（`:390-415`：`ShouldCallRemoteFunction` 的第二个测试调用点）。

**未读 / 未做**

- `Tools/PMNetE2E/Program.cs` 未逐行通读（按函数局部读取，覆盖了 RPC/复制/注入相关段）。
- `Tools/PMDeclCheck/Program.cs`、`Tools/PMReplicationTest/Program.cs` 只读相关段。
- **未执行任何门禁**（只读审查，禁编译）；文档引用的 61/129/92 项结果按"文档声称"采信，未复跑。
- 未涉及 UE 侧、R3 业务、全仓扫描（按任务硬边界）。

---

## 5. 建议下一步（最小补充查询 / 运行时验证）

**按优先级（每条都给出最小可执行动作）**

1. **补 C1 的可复现断言（先让它失败）**：在 `E2eFixtures.cs` 增加
   `[PMServerRpc(Validator=ForceValidate)] void Push(float[] v)` + `Push_ForceValidate`，
   在 `PMNetE2E` 增加：调用 `PMNet_Push(buf)` → **就地改写 `buf[0]`** → `TryDequeueRpc` + `Write` → 接收侧断言等于调用时刻快照。
   这条同时把"数组参数 RPC"引进编译面（顺带覆盖 `PMGeneratedMaxArrayLength` 的发射分支）。
2. **C1 的修法抉择**（供决策，不在本次范围）：(a) 发射器对数组参数在调用桩内先 `Clone()` 快照
   （上限已由 `PMGeneratedMaxArrayLength` 兜住，代价是一次分配）；或
   (b) 把"编码必须同步"从注释升级为**可判定的接缝** —— 让 `EnqueueRemote` 当场完成编码、
   队列只持有 `byte[]`（这样"推迟"不再影响正确性）。二者取一即可，但**必须撤掉/修正 §4.3.1 与生成物注释里"闭包没有窗口"的绝对说法**。
3. **把归属校验下移到接收点**：给 `PMRpcInvoker` 增加接收上下文（`PMNetConnection` / `isObjectOwner` / `ignoreRpcs`），
   在生成的 `PMNet_RpcInvoke_*` 开头发射 `PMRpcDispatch.ShouldCallRemoteFunction(...)` 判定；
   E2E 增加**喂包级**断言"非 Owner 连接投递 Server RPC ⇒ 实现不被执行"（替换/补齐现在的谓词级 R1–R4），
   并同步修正 §5.6 表格中"归属校验"的覆盖口径。
4. **补 `Validate` 档位的正向夹具**：`Good.cs` 与 `E2eFixtures.cs` 各加一条 `Validator = Validate` + `_Validate` 同伴，
   使 `DeclEmitter.cs:703-716` 的第二分支进入**编译 + 运行**覆盖；顺带断言未接线 sink 时 `Unhandled` 可观测。
5. **C4 硬化**：把 `EmitValidationRouting` 末尾的注释分支改为抛异常/产出编译错误，
   使发射器自身 fail-closed（不再依赖声明期门禁）；并把该断言写进 `PMDeclCheck` 的负向用例。
6. **C5 的最小处置**：在生成的 `PMNet_RpcInvoke_*` 末尾加 `reader` 消费完整性检查（`IsAtEnd`/`Consumed`），
   与 M05 的接收循环一起决定"越界 ⇒ 断连"的落点；枚举参数至少给一条"是否校验范围"的契约裁定
   （要么生成器发 `Enum.IsDefined` 式的门，要么在 R0 §6 明确"枚举范围由业务同伴负责"）。
7. **C6 口径修正**：在契约里把可靠 RPC 的溢出语义指回 D-R0-07（断连），
   并在 `EnqueueRemote` 签名里带上 `IsReliable`（或强制 M05 从描述符取），避免"丢最旧"被当成通用策略继承下去。
8. **C7 的裁决与告警**：明确"对象级连续索引"与"类内描述符槽位"二者择一
   （复制层改用 `base + slot`，或生成器改发射类内下标并撤回 `PMGeneratedPropertyIndexBase` 的语义）；
   同时把 `ComputePropertyIndexBase` 的跨批 `break` 从**静默**改成**硬告警/硬错误**
   （把 `§5.4 #5` 从"静默错位"降级为"可观测"）；补一个"`[PMNetworkObject]` 继承链"夹具
   （两个类同批声明、派生类复制属性 ≥1）进 E2E，覆盖两个索引空间的一致性。

**如果要一次性收敛到可验收**：建议先做 1 + 3 + 8（它们分别对应最高影响的两个 P1 与一个门禁盲区），
再补 4/5/6/7 的覆盖与口径。
