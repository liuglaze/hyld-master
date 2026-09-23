# 自动属性生成器（A 组 / T-P1）交付报告

- 委派类型：comprehensive（实现 + 门禁 + 测试）。
- 冻结接口：`Docs/plans/net-property-authoring-contract.md`（本次唯一冻结接口）。
- 状态源：`Docs/plans/net-architecture-migration.md` 末尾「自动属性复制开工（用户批准）」。
- 一句话结论：生成器（`Tools/PMNetGen`）现在能对 `[PMReplicated]` 自动属性发射契约 §2 的四个成员与
  `PMGeneratedPropertyIndex_<P>` 常量，收包 Reader 改为「解码到局部值 → 直接 RawSet」；
  字段旧模式**逐字节不变**（R3/E2E `--decl-check` 退出 0）；一切不支持形态在**生成前**硬失败。
  门禁 `Tools/PMDeclCheck` 由 107/0 提升到 **157/0**，老断言一条没删。

---

## 1. 实际必读入口（按委派顺序，逐一完整阅读）

| # | 入口 | 读了什么 |
|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 完整（仓根入口，指向下面两份与主计划） |
| 2 | `Client/Assets/AGENTS.md` | 完整（客户端边界：Unity 2019.4/C# 7.3、生成入口、门禁、BOM+CRLF） |
| 3 | `Server/AGENTS.md` | 完整（服务端边界：不启用户服务、不覆盖运行中 Server.dll） |
| 4 | `Docs/plans/net-architecture-migration.md` | 末尾「自动属性复制开工（用户批准）」段（含 T-P1..T-P5 表）+ §3.3 属性复制模型 + §3.9.5 声明生成 |
| 5 | `Docs/plans/net-property-authoring-contract.md` | **完整**（§1 作者语义 / §2 生成器与编织器冻结接口 / §3 轮询 / §4 ID 与集成 / §5 验收 / §6 约束） |
| 6 | `Docs/plans/net-r2-codegen-contract.md` | §1 冻结产物、§2 稳定 ID、§3 协议摘要、§4.1 生成物 API 面、§5 规则集、§6 类型集、§9/§10 当前实现口径 |
| 7 | `Docs/plans/net-rpc-weaving-contract.md` | §2 冻结格式 v1（version/Require/实例 gate/BuildEntry 门）、§3 分阶段边界、§6 集成收口 |
| 8 | `windows-shell-compat` skill | 完整（Bash 工具按 Bash 解析、需要 PowerShell 用 pwsh、路径与引用规则） |

### 1.1 交叉必读（非委派清单，但不读会写出「自洽却被编织器拒绝」的产物）

| 文件 | 为什么必读 | 是否改动 |
|---|---|---|
| `Tools/PMNetWeaver/PropertyWeaver.cs` | B 组已落地的编织器预检/check 口径：它逐条断言生成物的形态（stub 恰 3 条指令、PropertySet 有界抽象求值、Reader 只走 RawSet、槽位与注册表三来源一致）。生成物必须**恰好**长成它认识的形状 | 只读，未改 |
| `Docs/plans/_property_weaver.md` | B 组报告，明确列出「A 组待落地」的两项（Reader 改 RawSet、纯属性类也要发射门）与「PushBased=false 完全不读 HasAuthority」 | 只读 |
| `Docs/plans/_property_polling.md` | C 组报告，明确它的轮询层依赖「生成物把 `PushBased=false` 写进描述符」 | 只读 |

### 1.2 文档如何决定首搜边界

- 主计划末尾段把工作切成 A（生成器）/B（编织）/C（轮询）三组，并给出 T-P1..T-P5；
  因此**本轮写路径只有** `Tools/PMNetGen/{DeclScanner,DeclValidation,DeclEmitter}.cs` +
  `Tools/PMDeclCheck/{Program.cs,Fixtures/*}` + 本报告。
- 契约 §2 给出四个生成成员与断言口径 ⇒ 生成器侧实现目标明确，无需从业务昵称或宽泛目录猜测。
- r2 契约 §4.1 固定 `PMNet_Set<P>` / `PMNet_Read_<P>` 命名 ⇒ 字段模式既有产物必须保持。
- rpc 契约 §2 固定 `PMNet_GetRpcWeaveVersion` / `PMNet_RequireRpcWeave` / `PMNet_rpcWeaveGate` /
  BuildEntry 首句 ⇒ 自动属性复用同一套门，**不新增第二套命名**。
- 因此首搜目录确定为 `Tools/PMNetGen/` 与 `Tools/PMDeclCheck/`，未做全项目盲搜。

---

## 2. 交付内容

### 2.1 `Tools/PMNetGen/DeclScanner.cs`（rawfacts 扩展，**冻结 IR 未改**）

新增事实（全部走 `PMDeclRawFacts`，`PMDeclModel`/`PMDeclProperty` 字段一字未动）：

- `PropertyFacts`（`PMDeclPropertyFact`）：每个被 `[PMReplicated]` 标记成员的**声明形态**——
  字段/属性、indexer、显式接口实现、ref-return、virtual/override/abstract、static、
  是否有 get/set、访问器是否 auto（有体）、初始化器、表达式体、访问器显式可访问性、行号。
  并提供**唯一判据** `IsPlainAutoProperty` 与 `DescribeUnsupportedShapes()`。
- `ReplicatedOnUnsupportedMembers`：`[PMReplicated]`/`[PMQuantized]` 标在 event / delegate 等
  **不参与复制**的成员种类上（以前被完全静默忽略）。
- `NestedNetworkObjectTypes` / `NestedReplicatedMembers`：**嵌套类型**上的网络声明
  （扫描器只处理顶层类型，生成物是「顶层 partial class」，这类声明不会生效）。
- `QuantizedWithoutReplicated`：只写 `[PMQuantized]` 没有 `[PMReplicated]`（只告警，不改行为）。
- `PushBased` 现在从 `[PMReplicated(PushBased = false)]` 真解析并进 IR（`PMDeclProperty.PushBased` 是既有字段）。

同时修掉两个既有的**静默缺陷**：

1. `[PMReplicated(...)]` 的**位置实参**解析：以前取「第一个实参」，于是
   `[PMReplicated(PushBased = false)]` 会把具名实参当成复制条件 ⇒ 规则 11 误报。现在只取位置实参。
2. indexer（`IndexerDeclarationSyntax`，不是 `PropertyDeclarationSyntax`）带 `[PMReplicated]` 时
   以前**完全不被收集**（标记了但永不生效）。

### 2.2 `Tools/PMNetGen/DeclValidation.cs`（新增规则 14）

- `RuleCount` 13 → 14，新增标题与 `Rule14_ReplicatedPropertyShape`：
  - 14b：属性**形态**必须能证明是普通实例 auto-property（否则逐条报出具体原因）；
    `static` 由规则 3 报，规则 14 刻意不重复。
  - 14c：标记在不支持的**成员种类**上（event/delegate）。
  - 14d：标记在**嵌套类型**里。
  - 14a：`ParsedFileCount > 0` 时，IR 里的每个复制成员都必须有对应形态事实（防扫描器与 IR 分叉）。
- 「`[PMReplicated]` 属性没有 set 访问器」由**告警升级为规则 14 硬错误**（只读属性没有可改写点）。
- 新增 `QuantizedWithoutReplicated` 告警。

### 2.3 `Tools/PMNetGen/DeclEmitter.cs`

对每个 auto-property `P`（含 `PushBased=false`）发射契约 §2 的四样：

```csharp
public const int PMGeneratedPropertyIndex_<P> = <indexBase + slot>;
private void PMNet_PropertyRawSet_<P>(T value) { throw new System.InvalidOperationException("PMNet property has not been woven"); }
private void PMNet_PropertySet_<P>(T value)
{
    bool pmChanged = !System.Collections.Generic.EqualityComparer<T>.Default.Equals(this.<P>, value);
    PMNet_PropertyRawSet_<P>(value);
    if (pmChanged && HasAuthority) { MarkPropertyDirty(PMGeneratedPropertyIndex_<P>); }   // 仅 PushBased=true
}
```

- 收包 Reader：`PMNet_Read_<P>` 先解码到局部值，再 `self.PMNet_PropertyRawSet_<P>(...)`；
  **不写 `self.<P>`、不调 setter、不调 PropertySet**（数组走同一结构：先 `int[] pmValue = ...`，再 RawSet）。
- `PMNet_Set<P>`：自动属性**仅** `P = value;`（不再 Mark，标脏由编织后的 setter 单点完成）；
  字段**原样保留** `X = value; MarkPropertyDirty(base+i);`。
- `CollectLifetimeReplicatedProps`：自动属性用 `PMGeneratedPropertyIndex_<P>` 具名常量注册（与标脏同源）。
- 编织门触发条件由「`Rpcs.Count > 0`」扩为「**有 RPC 或 有自动属性**」，
  名称/格式（version0 / Require / 实例 readonly gate / BuildEntry 首句）**完全不变**。
- 头注只在真的含自动属性时多一行；字段模式产物**逐字节不变**。
- 发射器对「属性但形态无法证明是 auto-property」的情况 **fail-closed 拒绝发射**
  （照抄 `EmitValidationRouting` 的既有先例，理由相同：`EmitAll` 是公开 API，声明期规则不是唯一入口）。

### 2.4 `Tools/PMDeclCheck`

- 新增 `§16`（自动属性：helper 形态 / 字段兼容 / Reader 不回环 / 规则 14 / BCL 白名单自测）
  与 `§17`（**独立沙盒**：新写一份自动属性声明集，跑 扫描→校验→发射→C# 7.3 真编译）。
- `FindUnexpectedSystemUsage` 的 BCL 白名单新增 `System.Collections.Generic.EqualityComparer`，
  并加**负向自测**（`System.Console.WriteLine` 必须仍被拒），保证白名单没被放水。
- 「非 RPC 类不发射版本门」改为「**纯字段且无 RPC** 的类不发射版本门」，并加反向断言：
  **纯自动属性零 RPC 的类必须**带门（version/实例 gate/BuildEntry 首句）。
- 负例全部落在 `Bad.cs`，且断言「负例是**语法成立**的 C# 7.3」（拒绝来自形态规则，不是解析噪声）。

---

## 3. 关键实现决策（含被否决的方案）

| 决策 | 理由 / 被否决的方案 |
|---|---|
| 形态判据单一化：`PMDeclPropertyFact.IsPlainAutoProperty` 由扫描器、校验器、发射器**共用** | 两处各算一套会出现「校验放行、发射器拒绝」或更糟的「校验放行、发射器静默写错」 |
| 发射器对无形态事实的属性 **fail-closed** | 与 `EmitValidationRouting` 同一条理由：公开 API 有多个入口；注释不是断言 |
| `PushBased=false` 只发射 `changed + RawSet` **两行**，**不发射** `if (... HasAuthority)` | B 组的 `RequireAssignmentHelper` 明确断言「PushBased=false 完全不读 HasAuthority、完全不标脏」。曾考虑用「空 if 分支」压掉未用局部变量，**已否决**（它会读 HasAuthority，直接被 B 组拒绝）。实测确认非恒定初值的未用局部变量**不触发 CS0219**，因此两行形态既满足契约也无编译噪声 |
| 属性一侧写 `this.<P>`，局部名用 `pmChanged` | 属性若恰好叫 `value` / `changed`，不加限定会自引用（`bool changed = ...Equals(changed, value)` 是 CS0841）。`this.` 只影响 `ldarg.0`，IL 与 `get_<P>` 调用完全一致，B 组求值器照样识别 |
| 字段路径 **零改动** | 生产/R3/E2E 的产物已在工作副本里（其它组已生成），`--decl-check` 逐字节比对是硬门；新增能力只走 auto-property 分支 |
| 位置实参 vs 具名实参区分 | `[PMReplicated(PMCond.OwnerOnly)]` 与 `[PMReplicated(PushBased = false)]` 必须区分；既有实现把后者误判成条件（真实缺陷） |
| 嵌套类型 / event / delegate 也算「不支持声明位置」 | 契约 §1 的不支持清单以「等」结尾；这三类声明**根本进不了 IR**，不报就是「写了标记但完全不参与复制」的静默漏扫 |
| 没有扩 `PMDeclModel` | 委派与契约都要求「不改冻结 IR，补 rawfacts 即可」；事实走 `PMDeclRawFacts`，与规则 7/9 的既有语法事实同一处理方式 |

---

## 4. 测试与证据（本轮 build0 才 run，未使用旧 DLL）

```bash
# 1) 门禁（含生成器）：本次构建 → 立即运行
dotnet build Tools/PMDeclCheck/PMDeclCheck.csproj -c Release
dotnet Tools/PMDeclCheck/bin/Release/net8.0/PMDeclCheck.dll

# 2) 生产字段模式逐字节回归（只读，不写生产产物）
dotnet Tools/PMNetGen/bin/Release/net8.0/PMNetGen.dll --decl-check Client/Assets/Scripts/PMR3 \
  --out-dir Client/Assets/Scripts/PMR3/Generated --id-lock Docs/plans/pmnet-r3-ids.json

# 3) E2E 夹具字段模式逐字节回归
dotnet Tools/PMNetGen/bin/Release/net8.0/PMNetGen.dll --decl-check Tools/PMNetE2E/E2eFixtures.cs \
  --out-dir Tools/PMNetE2E/Generated --id-lock Tools/PMNetE2E/e2e-ids.json

# 4) 夹具扫描（正向零错误 / 负向逐条命中）
dotnet Tools/PMNetGen/bin/Release/net8.0/PMNetGen.dll --decl-scan Tools/PMDeclCheck/Fixtures/Good.cs
dotnet Tools/PMNetGen/bin/Release/net8.0/PMNetGen.dll --decl-scan Tools/PMDeclCheck/Fixtures/Bad.cs
```

| 项 | 命令 | 结果 |
|---|---|---|
| 生成器声明门禁 | `PMDeclCheck.dll` | **157 PASS / 0 FAIL**（改前基线 107/0；老断言全部保留） |
| 生产字段模式字节一致 | R3 `--decl-check` | **exit 0**；13 属性 / 13 RPC；`PMR3Player ClassId=405815557 hash=0xB09BCD1C`（与契约一致，未漂移）；磁盘产物与本次生成逐字节相同 |
| E2E 字段模式字节一致 | E2E `--decl-check` | **exit 0**（`E2eReplicated hash=0x50AF2E09`、`E2eScoreboard hash=0xDCD02534`） |
| 正向夹具 | `--decl-scan Good.cs` | 6 类 / 21 复制属性 / 7 RPC，**0 错误 0 告警** |
| 负向夹具 | `--decl-scan Bad.cs` | 26 网络类；规则 14 命中 16 条；**0 语法错误** |
| 自动属性生成物真编译 | `PMDeclCheck §17` | 独立沙盒声明集（6 个 auto-property：public get/private set、私有、初始化器、PushBased=false、数组、string）→ 生成物 + PMNet 运行时在 **C# 7.3 + netstandard2.0** 下 **0 错误** |
| 编码 | python 逐字节核对 | 6 个改动 `.cs` 全部 **UTF-8 BOM + CRLF**（lone LF = 0） |

`PMDeclCheck §16` 覆盖的具体断言（摘要）：

- `PMGeneratedPropertyIndex_<P>` 值 == `indexBase + 槽位`，且**每个** auto-property 都有（0 缺失）。
- RawSet 桩：单条 `throw new System.InvalidOperationException("PMNet property has not been woven")`。
- PropertySet：`EqualityComparer<int>.Default.Equals(this.Hp, value)` +
  **无条件** `PMNet_PropertyRawSet_Hp(value)` + `if (pmChanged && HasAuthority)` +
  `MarkPropertyDirty(PMGeneratedPropertyIndex_Hp)`（本属性自己的槽位）。
- `PushBased=false`（`PingMs`）：helper 里 **0 处** `HasAuthority`、**0 处** `MarkPropertyDirty`，
  但仍发射 changed + RawSet。
- `PMNet_SetHp` 仅 `Hp = value;` 且不含 `MarkPropertyDirty`；字段 `PMNet_Set_hp` 仍
  `_hp = value;` + `MarkPropertyDirty(<槽位>)`；字段产物**不含** RawSet/PropertySet/槽位常量。
- Reader 不回环：遍历全部 8 个 auto-property Reader，断言「调用本属性 RawSet」且
  「不含 `self.<P> =`」且「不含 `PMNet_PropertySet_<P>(`」；13 个字段 Reader 仍直接写成员。
- 纯属性零 RPC 类带门；混合类（RPC + auto-property）两类 helper 共存。
- 发射器 fail-closed：注入「属性但无形态事实」的模型 ⇒ `EmitAll` 抛异常。
- 规则 14 逐形态命中（自定义 getter / 自定义 setter / 只读 / 表达式体 / 无 getter / indexer /
  virtual / override / abstract / ref-return / 显式接口 / event / 嵌套类型），
  规则 3 命中 static 属性（规则 14 不重复报）。

---

## 5. 与并行组的接口对齐（只读核对，未运行他们的工具）

### 5.1 与 B 组 `Tools/PMNetWeaver/PropertyWeaver.cs`

| 它的预检/check 要求 | 我的发射 | 状态 |
|---|---|---|
| `public const int PMGeneratedPropertyIndex_<P>`（static literal int32、Constant 非空） | 逐属性发射 | 一致 |
| `private void PMNet_PropertyRawSet_<P>(T)`；编织前 = `ldstr/newobj/throw` 恰 3 条指令、无局部变量 | 单条 throw stub | 一致 |
| `private void PMNet_PropertySet_<P>(T)`；抽象求值只认 `EqualityComparer<T>.Default` / `get_<P>` / `Equals` / `RawSet` / `get_HasAuthority` / `MarkPropertyDirty(常量)` | 只用这些指令；`this.<P>` 即 `ldarg.0 + call get_<P>` | 一致 |
| PushBased=false：不读 `HasAuthority`、不标脏 | 不发射该分支 | 一致 |
| Reader `PMNet_Read_<P>` 必须调 RawSet，**不得**调 setter / PropertySet | 解码局部 → RawSet | 一致 |
| 注册表三来源一致（`outProps.Add(index, cond, push)` 与常量/属性一致，`ChangeMaskBitCount` == 条数） | 用同一常量注册，`ChangeMaskBitCount` == 属性数 | 一致 |
| 「存在 RPC **或** auto-property 的类均发射 version/Require/实例 gate/BuildEntry 门」 | 已按此扩条件（B 组报告 §7.1 把这列为 A 组待落地项） | 一致 |

B 组报告 §7.1 自述另有两项归 A 组的缺口，本轮都已关闭：
（a）「`PMNet_Read_<P>` 对 auto-property 会写普通 setter（正是契约禁止的形态）」→ 已改走 RawSet；
（b）「生成器只在 `cls.Rpcs.Count > 0` 时发射门」→ 已扩为「RPC 或 auto-property」。

### 5.2 与 C 组轮询

C 组报告 §7 明确：轮询层要生效，**生成物只需把 `p.PushBased = false` 写进描述符**。
改前生成器把 `PMDeclProperty.PushBased` 硬编码为 `true`（无论源码怎么写），也就是说
`[PMReplicated(PushBased = false)]` 在生产上**根本不生效**——这是一个真实的跨组缺口。
本轮已修（解析属性 + 进 IR + 发射进描述符与注册表），沙盒产物可见
`p3.PushBased = false;` 与 `outProps.Add(..., false);`。

---

## 6. 边界、未做与残留

**未做（刻意）**

- 未改**冻结 IR**（`PMDeclModel` 字段零改动），事实全部走 rawfacts。
- 未改 **RPC 语义**、未改 / 未重算 **ID 与协议摘要**：R3 `--decl-check` 通过即证明
  `0xB09BCD1C` / 全局 `0xE6130FAA` 与 ID 锁未漂移。
- **未实现任意字段 `stfld` 自动拦截**：字段仍是旧模式（须手动 `PMNet_Set<P>` 或 `PushBased=false` 轮询）。
- 未做**生产迁移**（T-P4：PMR3Player 12 成员 + PMR5Projectile 1 成员改同名 auto-property）——
  属主侧 D 步；本轮不碰生产源码，也不写 `Client/Assets/Scripts/PMR3/Generated` 与 `Server/bin`。
- 未做**轮询/公平预算**（C 组）、未做**编织**（B 组）。
- 未启动 Unity / 未启动用户服务 / 未做任何 git 写操作 / 未递归委派。
- 未运行 B 组的 `PMNetWeaverTest` 或 `--weave`（委派要求本轮只运行生成器与 DeclCheck）；
  因此「生成物能过 B 组预检」是**读码核对**结论，不是实跑结论。

**已知残留（有意保留，已登记）**

- 只写 `[PMQuantized]` 无 `[PMReplicated]`：只**告警**（不改 IR、不改语义）；
  「先写量化器后补复制」是可能的过渡写法，但它必须可见而不是静默消失。
- 数组 auto-property 只自动跟踪**整体引用替换**；原地改元素不保证自动标脏
  （契约 §1 明确允许，需显式 `MarkPropertyDirty` 或 `PushBased=false`）。
- `static` 属性由规则 3（成员修饰符）报，规则 14 不重复报——同一问题一条错误。
- 本轮新增的「不支持声明位置」（event / delegate / 嵌套类型）比契约字面清单更宽，
  依据是契约 §1 清单以「等」结尾 + 委派里「不让 Unsupported attr 静默被漏扫」的硬要求。
  生产 / R3 / E2E 声明集里没有这类写法，`--decl-check` 已证明零影响。
- 服务端 / 客户端真实构建未跑（会写生产 `Generated` 与用户 `Server/bin`，超出写入边界）。

**风险 / 后续（归主侧）**

1. B 组 `Tools/PMNetWeaverTest/Fixture.cs` 里有一段**手写的** auto-property 生成物
   （B 组报告 §7.1 自述「A 组落地后应删除该手写区、改为断言真实生成物——这一步属于主侧最终验证」）。
   本轮生成器已就绪，主侧用真实 `--decl-gen` 产物替换该手写区并跑 `PMNetWeaverTest` 即可闭环。
2. 生成物与真实 weave 的组合尚未实跑（跨组集成）；建议主侧在 T-P2 用真实生成物跑
   `PMNetWeaverTest`（这是唯一能证明「生成物恰好落在编织器可接受形状内」的实测）。
3. Unity 实机 / Player / IL2CPP 仍 `PENDING_USER`（本轮未启动 Unity）。

**阻塞**：无。

---

## 7. 修改文件（严格限于委派写入边界）

| 文件 | 改动 |
|---|---|
| `Tools/PMNetGen/DeclScanner.cs` | rawfacts 扩展（形态事实 / 不支持成员种类 / 嵌套类型 / 量化器单独出现）；`PushBased` 解析；位置实参修正；indexer 收集 |
| `Tools/PMNetGen/DeclValidation.cs` | 规则 14 + `RuleCount` 13→14；只读属性告警升级为硬错误；量化器告警 |
| `Tools/PMNetGen/DeclEmitter.cs` | 槽位常量 / RawSet 桩 / PropertySet 冻结语义 / Reader 走 RawSet / 门条件扩展 / 发射器 fail-closed |
| `Tools/PMDeclCheck/Program.cs` | §16 §17 新断言、BCL 白名单 + 负向自测、门条件断言改口径、辅助函数 |
| `Tools/PMDeclCheck/Fixtures/Good.cs` | `FixtureAutoPropOnly`（纯属性零 RPC）、`FixtureAutoPropMixed`（属性+RPC） |
| `Tools/PMDeclCheck/Fixtures/Bad.cs` | 规则 14 负例 11 类 + 嵌套类型 + event + static 属性 |
| `Docs/plans/_property_generator.md` | 本报告 |

未触碰：`Tools/PMNetGen/Program.cs`、`Tools/PMDeclModel/**`、`Tools/PMNetWeaver/**`、
`Client/**`、`Server/**`、`Docs/plans/*ids.json`、`Docs/plans/net-*.md`、
`Docs/plans/_property_weaver.md`、`Docs/plans/_property_polling.md`。

## 8. 验收口径对照（T-P1）

| 契约要求 | 本轮证据 |
|---|---|
| 生成 auto/public-private/private 初始化器 | `FixtureAutoPropOnly`（`Hp` public get/private set、`Mana` private、`Armor` 带初始化器）+ §17 沙盒 |
| 字段旧模式 | R3/E2E `--decl-check` exit 0（逐字节一致）；字段产物不含自动属性三件套 |
| 拒绝自定义 / 只读 / indexer 等 | 规则 14 逐形态命中（16 条），负例语法成立 |
| ID 锁与 C# 7.3 编译 | R3 `--decl-check` exit 0（ID 锁未漂移）；§17 C# 7.3 真编译 0 错误 |
