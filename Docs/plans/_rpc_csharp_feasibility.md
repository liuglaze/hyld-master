# RPC C# 语法迁移可行性（只读调查，未实现）

> 任务类型：explore（有界只读调查）。本轮**未修改任何生产源码、未编译、未启动 Unity/服务、未提交**。
> 唯一写入文件：本文件。
> 冻结需求（用户）：UE 样式 —— 标记放**普通名字的声明**、调用**普通名字**、业务额外实现 `_Name_Implementation`；
> 不能只标记 `_Implementation` 就冒称同体验。约束：Unity 2019.4 / C# 7.3 / netstandard2.0 不升级；
> 不新增运行反射；保留稳定 RPC/类 ID 与线上布局。
> 目标边界：`Tools/PMNetGen/{DeclScanner,DeclValidation,DeclEmitter,Program}.cs`、`Tools/PMDeclModel`、
> `Client/Assets/Scripts/PMR3/PMR3Player.cs`、相关 csproj 构建 hook、`Client/Assets/Editor/PMDsBuild.cs`。

---

## 0. 实际必读文档与首搜入口（本轮真正读过的东西）

按委派要求顺序完整阅读：

1. `D:/UGit/hyld-master/AGENTS.md`（完整）——确认客户端/服务端文档路由与网络迁移计划位置。
2. `Client/Assets/AGENTS.md`（完整）——Unity 2019.4.8f1 / C# 7.3 / netstandard2.0 硬约束；§7 给出**声明生成真实入口**
   （`Tools/PMNetGen --decl-gen Client/Assets/Scripts/PMR3 --out-dir ... --id-lock Docs/plans/pmnet-r3-ids.json`）；
   §7 还列出常用门禁（`PMClientCheck` / `PMUnityGlueCheck` / `PMR4UnityCheck` / `PMR6DeclarationTest` 等）。
3. `Server/AGENTS.md`（完整）——PMNet 框架源码链接自 Client/Assets；PMR3 声明产物两端同集合；ProtocolHash=0xE6130FAA（历史值）。
4. `Docs/plans/net-architecture-migration.md` 末尾 R6 / 旧链退役 / **退旧后框架审计**段（2395–2482 行）；
   另**沿直接依赖**读了 §3.9.1（A3/A9 行）、**§3.9.5「声明生成与编程体验」**、§4.1–§4.3（C# API 草案）。
   §3.9.5 是本次最关键的前置：它已经冻结了"业务写 `_Implementation`、生成器生成正常方法名入口"的方向，
   并明确标注"以下是**拟定 API，不是已存在的功能**"。
5. `Docs/plans/net-r2-codegen-contract.md`（完整）——§1 冻结接口（IR/锁/哈希在 `Tools/PMDeclModel`）、§2 稳定 ID 算法、
   §3 协议摘要、§4 生成物 API 面、§5 十三条规则、§6 类型集、§7 A/B/C 委派边界、§9 独立审查修订。
6. `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`（完整）——本轮未执行任何写入型 shell；
   `git ls-files`/`grep`/`find` 均为只读 Bash 语义。

首搜入口（本轮实际使用的入口，不是猜的）：

- 生成器：`fffind PMNetGen` → `Tools/PMNetGen/{DeclScanner,DeclValidation,DeclEmitter,Program}.cs`。
- 业务声明：`grep -n "PMServerRpc\|PMClientRpc\|PMNetMulticast" Client/Assets/Scripts/PMR3/PMR3Player.cs`（13 条 RPC 全部在此类）。
- 稳定键权威：`Docs/plans/pmnet-r3-ids.json`（22–34 行，13 个 `RPC:` 键）。
- 生成物：`Client/Assets/Scripts/PMR3/Generated/*.g.cs`（+ `.meta`）。
- Unity API 事实：`D:/Unity/2019.4.8f1/Editor/Data/Managed/UnityEditor.dll`（符号 grep）。
- 构建 hook：`Tools/PMNetE2E/PMNetE2E.csproj`（`PMNetGenE2eFixtures` pre-build target）、
  `ProtobufAndNotepad/Protobuf/build.bat`、`Client/Assets/Editor/PMDsBuild.cs`、`Client/Assets/Editor/PMBattleContentBuild.cs`。

---

## 1. 已确认

### 1.1 结论一（A）：迁移到"标记普通名 + 调用普通名 + 业务写 `_Implementation`"的最小修改点是**命名级**的，不触及 IR/锁/协议

**当前实现事实（今天的样子）**

- 标记直接写在**带业务体的方法**上；扫描器把方法名当 RPC 名（`DeclScanner.cs:773` 收集方法名，
  `:1003-1004` 用 `MethodNames.Contains(<M> + "_ForceValidate")` 判同伴）。
- 发射器产出两处：接收侧分发 `PMNet_RpcInvoke_<M>` 里 `self.<M>(...)` 调业务实现（`DeclEmitter.cs:591`）；
  业务可见调用桩 `public void PMNet_<M>(...)`（`DeclEmitter.cs:604`），桩内本地执行也是 `<M>(...)`（`DeclEmitter.cs:642`）。
- 消费侧调 `PMNet_<M>`：`PMR4MovementDriver.cs:1055`、`PMR6CombatDriver.cs:888`、`PMClientSessionHost.cs:1615`、
  `PMR3IntegrationTest/Program.cs:679,1086`、`PMR3RuntimeTest`、`PMR3UnitySmoke`、`PMR5/PMR6*Test` 等。

**最小修改点（逐条，含定位）**

扫描器 `Tools/PMNetGen/DeclScanner.cs`

- **A1（必需，1 行级）**：`DeclScanner.cs:404` 的 `new CSharpParseOptions(LanguageVersion.Latest)` 必须补上
  `PMNET_DECLARATIONS` 符号（`WithPreprocessorSymbols` / 构造函数重载）。
  **理由（硬性）**：Roslyn 在符号未定义时把 `#if` 区变成 `DisabledTextTrivia`，`root.DescendantNodes()` **不会**遍历到内部节点
  → 声明对扫描器完全不可见 → 13 个 `RPC:` 稳定键全部消失（锁文件把这些键标退役、新键重新分配）→ 线上协议被静默改写。
- **A2**：新增"声明式（无体）"与"实现伴生存在"两个事实：`Body == null && ExpressionBody == null`（含 `SemicolonToken`），
  以及 `acc.MethodNames.Contains(<M> + "_Implementation")`（复用 773 行已有的全量方法名集合与 1003/1004 行的同伴检测写法）。
  落点必须是 `PMDeclRawFacts.Rpcs`（`PMDeclRpcFact`）——它是 R2 契约里**专门为"IR 之外的语法层事实"预留**的容器
  （`DeclScanner.cs` 头注释与 `PMDeclRawFacts` 的注释都写明了这一点），**不动冻结 IR**（`Tools/PMDeclModel/PMDeclModel.cs`，
  契约 §1 把 IR 列入冻结接口）。
- **A3（建议）**：记录声明的修饰符前缀，用于让生成入口的可见性与声明一致（`facts.TypeModifierPrefix` 已有同类先例）。

校验 `Tools/PMNetGen/DeclValidation.cs`

- **A4（建议新增规则 14，`RuleCount` 13→14 并同步 `RuleTitles`）**：
  ① 带 `[PMServerRpc]/[PMClientRpc]/[PMNetMulticast]` 的方法必须是**无体声明**（带体即报错——否则发射器产出的同名入口会撞 CS0111）；
  ② 必须存在 `<M>_Implementation`；
  ③ 声明与实现**逐参数同名 + 同类型**（详见 §2 H2：这是本方案唯一的静默风险点）；
  ④ 禁止 `ref/out/params/可选参数默认值` 静默漂移。
- **A5（必须改文案）**：`DeclValidation.cs:1045` 现在是"RPC 方法名以 `_Implementation` 结尾"的**告警**，文案直接引用
  "迁移计划 §3.9.5 的示例用的是另一种写法"。契约改成"标记普通名"后，这条告警的**指向刚好相反**（新契约下业务方法名就该是 `_Implementation`，
  而带标记的声明名不应以 `_Implementation` 结尾），必须同步改向，否则门禁会给出与契约相反的建议。

发射 `Tools/PMNetGen/DeclEmitter.cs`（共 3 处命名替换 + 注释，无结构变化）

- **A6**：`DeclEmitter.cs:604` `public void PMNet_<M>(` → `public void <M>(`。
- **A7**：`DeclEmitter.cs:642` 桩内本地执行 `<M>(` → `<M>_Implementation(`。
- **A8**：`DeclEmitter.cs:591` 接收侧 `self.<M>(` → `self.<M>_Implementation(`。
- **A9（明确保持不变，这是"线上布局零变化"的关键）**：`DeclEmitter.cs:558`（`PMGeneratedRpcId_<M>`）、
  `:564`（`PMNet_RpcInvoke_<M>`）、`:777`/`:801`（`<M>_ForceValidate` / `<M>_Validate` 同伴调用）、
  `:887`（`d.MethodName = "<M>"`）、`PMNet_Set<Prop>` / `PMNet_OnRepDispatch` / `PMNet_BuildEntry`、
  `PMNetGeneratedRegistry.g.cs` 全部**不需要改**。
- **A10**：`DeclEmitter.cs:601` 的注释"业务请调用本方法，不要直接调用 `<M>`"必须改写（新契约下业务**调用**的就是 `<M>`）。

消费侧（call site）

- **A11**：`PMNet_<M>` → `<M>`。实测规模（**排除 `Generated/`**，`Client/Assets` + `Tools`）：
  命中 105 行，其中 15 行是纯注释 ⇒ **约 90 处真实调用点**，分布在 **14 个文件 + 2 个夹具**：
  `Client/Assets/Scripts/PMR3/{PMR3Player,PMR4MovementDriver,PMR5ProjectileDriver,PMR6CombatDriver}.cs`、
  `Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs`、
  `Tools/{PMR3IntegrationTest,PMR3RuntimeTest,PMR3UnitySmoke,PMR4IntegrationTest,PMR4NetworkTest,PMR5DeclarationTest,PMR5NetworkTest,PMR6DeclarationTest,PMR6NetworkTest}/Program.cs`、
  `Tools/PMNetE2E/{Program,E2eFixtures}.cs`、`Tools/PMDeclCheck/Fixtures/Good.cs`。
  按桩名分布（前几名）：`ServerProbe` 17、`ServerProjectileSpawnV1` 16、`ServerCombatAttackV1` 15、`ServerMovementInputV1` 11、
  `ServerCombatResultAckV1` 8、`Fire`(E2E) 8、`ServerProjectileHitV1` 7、`ClientProjectileDecisionV1`/`ClientCombatMatchResultV1`/`ClientCombatAttackResultV1` 各 6。
- **A12**：业务方法改名 `<M>` → `<M>_Implementation` + 新增 `#if PMNET_DECLARATIONS` 声明块（含 Attribute）。
  范围：`PMR3Player.cs` **13 条** RPC（`pmnet-r3-ids.json:22-34` 的 13 个键全部属于 `PMNet.R3.PMR3Player`，
  即 `PMR5Projectile` 自己没有 RPC，因此**不需要**改它的 RPC 名）。同伴方法名 `<M>_ForceValidate`/`<M>_Validate` **不变**。
- **A13**：夹具同步迁移（`E2eFixtures.cs` 6 条、`PMDeclCheck/Fixtures/{Good,Bad}.cs`）；`PMDeclCheck/Program.cs:876,934`
  的"生成物 + 夹具在 C# 7.3 下零编译错误"断言必须改用**普通符号集**（不定义 `PMNET_DECLARATIONS`）编译 —— 这正是"普通编译"视图。
- **A14（建议新增负向断言）**：用 `PMNET_DECLARATIONS` 定义后编译同一夹具，断言出现"必须有体"类错误。
  这把"Unity/普通编译侧绝不能定义该符号"钉成可失败的门禁。
- **A15（生成器细节，已确认）**：`Program.cs:558` / `:673` 清理过期产物只匹配 `PMNet*.g.cs`，
  **不会匹配 `PMNet*.g.cs.meta`**，而 `PMNetGen` 全文**从不碰 `.meta`**（grep 为空）。
  ⇒ 声明删除时会留下孤儿 `.meta`（Unity 报 missing/重生成噪声）。最小修法：删除产物时一并删同名 `.meta`。

**ID / 协议不变性（这是"保留稳定 RPC/类 ID 及线上布局"的直接依据）**

- 稳定键取名于**带标记的方法名**：`PMStableHash.RpcKey`（`PMStableHash.cs:117`）= `"RPC:" + 类键主体 + "." + methodName`。
  用户方案把标记留在**普通名**（`ServerProbe`）上 ⇒ 键与今天逐字节相同 ⇒ **13 个 RpcId 一个都不漂移**。
- `PMRpcDescriptor.MethodName` **不参与任何哈希**：`ClassProtocolHash` 只混属性 `(PropertyId, Condition, QuantizerId, MaskOffset, MaskBitCount, MemberName)`
  （`PMStableHash.cs:224-250`）；`GlobalProtocolHash` 只混类摘要 + 每条 `(RpcId, Direction, Reliable, ParamLayoutId)`（`PMStableHash.cs:260-325`）。
  ⇒ `ClassProtocolHash` / `GlobalProtocolHash`（当前 0xE6130FAA）**不变**。
- 反例（**本轮最重要的取舍证据**）：若按 §3.9.5 字面把标记放在 `<M>_Implementation` 上，
  稳定键会变成 `RPC:...ServerProbe_Implementation` ⇒ **新 RpcId** ⇒ 与现有 DS/客户端线上不兼容，
  除非扫描器专门剥离 `_Implementation` 后缀。即：**"标记普通名"是唯一默认零 ID 漂移的写法**，与冻结需求天然一致。

### 1.2 结论二（B）：`#if PMNET_DECLARATIONS` 封装无体声明在 C# 7.3 下成立，且是几种可选路径里风险最低的一种

已确认机制（B-1）：

- 业务写法：

  ```csharp
  public partial class PMR3Player : PMNetObject
  {
  #if PMNET_DECLARATIONS
      [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
      public void ServerProbe(int nonce);
  #endif

      private void ServerProbe_Implementation(int nonce) { /* 业务体 */ }
      private PMRpcValidation ServerProbe_ForceValidate(int nonce) { ... }  // 同伴名不变
  }
  ```

  普通编译：符号未定义 ⇒ 声明与 Attribute **完全不进编译**（无 CS0501、无 CS0111），只有 `_Implementation`；
  生成物提供入口 `public void ServerProbe(int)`；调用点写 `ServerProbe(...)`。
  扫描器编译以外（只做**语法级**解析、不建 Compilation —— `DeclScanner.cs` 头注释明确写了这条设计）：
  定义符号后看到声明节点。
- **A1 是 B 的必需前提**，反之亦然：符号只能由扫描器定义，普通编译（Unity / 各 Tools 门禁）绝不能定义。
  今天 Unity 侧 define 只有 `HOTFIX_ENABLE`（`Client/ProjectSettings/ProjectSettings.asset:614-615`），
  `Client/Assets/mcs.rsp` 只有 `-unsafe` ⇒ 现状没有冲突，但**必须加门禁**（A14）防止有人把符号写进 Player Settings / rsp。
- 扫描器对"无体方法"的节点形态已天然支持：被扫描目录里今天就存在无体方法（接口 `IPMProjectileNetworkDriver`，
  `PMR3Player.cs:64-72`），`CollectMembers` 只按成员种类分派、不检查 `Body`。
- 即便 Roslyn 在 parse diagnostics 里对"类内无体非 partial 方法"报错，扫描器也只把它计入 `facts.SyntaxErrorCount`，
  而 `PMDeclValidation` 对该计数只发**告警**（不是 error），不改变可行性。

语言面排除的替代写法（B-2，有依据）：

- **`partial void`**：C# 7.3 的 partial method 隐式 `private`、不得带可访问性修饰符、必须返回 `void`、不得有 `out` 参数
  （官方文档原文：*"a partial method that does not require an implementing declaration because it is private, returns void,
  has no out parameters, and lacks specific modifiers"*；放宽到可访问性/返回值是 C# 9 的扩展）。
  本项目现有调用点**跨类**（`PMR6CombatDriver.cs:888` / `PMR4MovementDriver.cs:1055` / `PMClientSessionHost.cs:1615`）
  ⇒ partial 方法提供的入口是 private，外部类调不到 ⇒ **不满足"调用普通名字"**，淘汰。
- **`extern`**：C# 不允许给 `extern` 方法任何托管实现体；若生成物再往同一个类补同名带体方法 ⇒ CS0111 重复成员。
  extern 只能与"编译后 IL 重写把调用点/方法体接到生成实现"配合，**本身不解决"业务写实现"**。
- **IL weaving（Mono.Cecil / Fody / Unity 自带 Unity.Cecil.dll）**：本机 2019.4 的 `UnityEditor.dll` 内
  **找不到** `IAssemblyPostProcessor` / `AssemblyPostProcessor` 符号（grep 命中 0），即 2019.4 **没有** asmdef 级程序集后处理器 API；
  只能自建编译后重写步骤。额外风险：新增不可逐字节复核的构建阶段、失败只能在编译后暴露、IL2CPP/AOT 与托管剥离需另行验证；
  而且它**不减少**本方案的前提（仍要给扫描器一份声明，只是把 `#if` 换成 `extern`）。**判定：不构成更小风险的替代。**
- **仅标记 `<M>_Implementation`（§3.9.5 现行草案）**：语言面最简单（不需要符号），但
  ① 不是用户要的 UE 体验（被标记的名字不是可调用的普通名）；② 默认让全部 RpcId 漂移（见 1.1 反例）；
  ③ `DeclValidation.cs:1045` 的告警正指向这条，改契约必须同步改写。
- **把声明放进 Unity 不编译的文件**（非 `.cs` 后缀 / `~` 结尾目录 / `Assets` 之外）：可彻底免符号，
  但声明与业务分居两处、IDE 不把它当同一个类的一部分（不在 Unity 生成的 csproj 内），并引入新的工程约定。列为备选，不作首选。

### 1.3 结论三（C）：真正覆盖"初次编译"的是**已提交的生成物 + decl-check 门禁**；Unity 侧 hook 无法修复"已经出错的 Assembly-CSharp"

已确认事实：

- **C1 生成物已入库**：`git ls-files` 命中 `Client/Assets/Scripts/PMR3/Generated/*.g.cs`、对应 `.meta`、
  以及 `Docs/plans/pmnet-r3-ids.json`；`git check-ignore` 未忽略。⇒ **新 checkout 首次打开 Unity 不需要跑生成器**，
  只要产物与声明同步；同步由 `--decl-check` 门禁保证（`Client/Assets/AGENTS.md:74` 的入口 + `build.bat` + `PMNetE2E.csproj` 的 pre-build target 先例）。
- **C2 工程约定明确拒绝隐式生成**：`Client/Assets/Editor/PMBattleContentBuild.cs:573-579` 写明
  "只有在用户点菜单（或构建 hook）时才执行 —— 本文件**不会**自己启动 Unity，也**不在任何静态构造/InitializeOnLoad 里偷偷生成**"。
  引入 hook 需要显式决策突破该约定，或照既有模式（菜单 + 构建前 preflight）实现。
- **C3 API 可用性（本机实测符号）**：`D:/Unity/2019.4.8f1/Editor/Data/Managed/UnityEditor.dll` 中存在
  `CompilationPipeline`（`compilationStarted`/`compilationFinished`）、`IPreprocessBuildWithReport`、
  `AssetPostprocessor`/`OnPostprocessAllAssets`、`InitializeOnLoad(Method)`、`AssemblyReloadEvents`；
  `IAssemblyPostProcessor` **不存在**。
- **C4 已有"构建前 preflight"先例**：`Client/Assets/Editor/PMDsBuild.cs:66-76` 在 `BuildPipeline.BuildPlayer` 之前
  调 `PMBattleContentBuild.PrepareForBuild()`，失败即中止构建（"缺资源不允许假成功"）。声明同步校验可以照抄这条缝。

可行组合（最小、不重写源码）：

- **C5 Build 覆盖（DS/客户端打包，菜单或 batchmode）**：在 `PMDsBuild` 现有 preflight 位置加一次
  `dotnet PMNetGen.dll --decl-check <PMR3 目录> --out-dir ... --id-lock ...`，不一致即构建失败。
  完全不需要 Unity C# 侧"修复编译"。
- **C6 Play / 会话内编译覆盖（仅在上一轮编译成功的会话里可用）**：用 `CompilationPipeline.compilationStarted`
  （或 `AssetPostprocessor.OnPostprocessAllAssets`）先跑 `--decl-check`，**只有不一致才**跑 `--decl-gen`
  （避免 Refresh 风暴/重入），再让 Unity 继续编译。
- **C7 硬约束（诚实结论）**：`[InitializeOnLoad*]` 与 `AssetPostprocessor` 都要求相应程序集**编译并加载成功**。
  `Client/Assets/Editor/*.cs` 默认属于 `Assembly-CSharp-Editor`，而它**引用** `Assembly-CSharp`；
  `Client/Assets` 下**一个 asmdef 都没有**（46 个 asmdef 全在 `Client/Library/PackageCache`）。
  ⇒ **Assembly-CSharp 编译失败时，默认 Editor 程序集同样编不出来，hook 根本不会运行**。
  即"用 Unity 侧 hook 修复已经出错的 Assembly-CSharp"在默认程序集划分下**不成立**。能改善的只有两条：
  (i) 把 hook 放进**不引用 Assembly-CSharp 的独立 Editor asmdef**，使其不被牵连（但"任一程序集失败→不重载域"的行为仍不能保证触发，见 §3 U1）；
  (ii) 更可靠：把修复路径放到 **Unity 之外**（`Tools/PMNetGen` 是 net8.0 独立进程），即"打开 Unity 之前跑 build.bat / decl-gen"，
  这条**不依赖任何 Unity 程序集能否编译**。
- **C8 首次编译的边界**：若入库产物与声明不同步，Unity 首次编译失败且此时无可用 hook ⇒ 只能外部跑 `--decl-gen`。
  ⇒ **门禁（decl-check）才是首次编译的真正保障，hook 只是便利**。
- **C9 hook 明确不该做的事**：不要在 `compilationStarted` 里无脑 `AssetDatabase.Refresh()`（重入/循环）；
  不要在 hook 里重写业务源码（用户约束"不全源码重写"）；不要指望生成器写 `.meta`（它不写，孤儿 meta 见 A15）；
  不要依赖 Editor 静态构造顺序。

### 1.4 其他已确认的技术事实（对实现有直接影响）

- 运行期没有任何"按名字找 RPC"的反射：`PMRpcDescriptor.MethodName` 只用于异常/告警文案
  （`PMNetDeclarations.cs:531,576,586`、`PMRpc.cs:778,824`、`PMNetSessionBridge.cs:613,622`），派发走 `entry.Invoke` 委托。
- RPC 表入口（`PMNet_RpcInvoke_<M>`）与 `PMGeneratedRpcId_<M>` 常量名不变 ⇒ 门禁 `PMDeclCheck/Program.cs:740` 的
  "生成物内嵌 RPC ID"断言与 `PMNetE2E/Program.cs` 的 `E2eReplicated.PMGeneratedRpcId_*` 断言**不需要改**。
- `PMNet.Set<Prop>` / `PMNet_OnRepDispatch` / `PMNet_BuildEntry` / `ProjectIndexBase` 与本次迁移正交，不受影响。
- `PMKey` 顺序/`--decl-check` 逐字节比对机制保持不变：本次变更后仍需整批重新生成产物 + 锁文件（预计锁文件**零变化**）。

---

## 2. 高概率推断（依据与置信度）

- **H1（高，≈0.9）**：扫描器启用符号后，`#if` 区声明会以普通 `MethodDeclarationSyntax` 出现，
  `CollectMembers` 现有分支足以产出 IR（该分支已处理无体方法形态：同目录 `IPMProjectileNetworkDriver`，`PMR3Player.cs:64-72`）。
  依据：`DeclScanner.CollectMembers` 只按成员种类分派、不检查 `Body`。
- **H2（高，≈0.85）——本方案唯一真正的静默风险**：若只靠"生成物调用 `<M>_Implementation(...)`"来对齐签名，
  **参数类型漂移会在隐式转换下静默编译通过**（声明 `float angle` / 实现 `double angle`：`_Implementation(p0)` 是合法隐式转换，
  线格式仍按声明的 `float` 走）。必须由 A4 的声明期规则（逐参数**同名**+同类型）兜住；同名还能顺带保住"命名实参"调用点。
  依据：`DeclEmitter.EmitRpcs` 只按声明类型生成读写体与调用，实现侧签名不参与任何校验。
- **H3（中高，≈0.75）**：Roslyn 对"类内无体非 partial 方法"报的 CS0501 属于**声明/语义**诊断，不出现在
  `SyntaxTree.GetDiagnostics()` 里（扫描器只统计该集合，且只发告警）。依据：Roslyn 语法层允许 `;` 结尾的方法声明
  （abstract/extern/partial/interface 共用这一形态），"必须有体"需要符号/绑定阶段才能判定。
  **即使推断错误也不影响可行性**（只多一条告警）。
- **H4（中高，≈0.75）**：Unity 存在编译错误时不会重载脚本域，因此 `[InitializeOnLoad*]` 类回调无法在"已损坏"状态下运行；
  `compilationStarted` 也只可能由**上一轮成功加载**的编辑器程序集触发。依据：与 C7 同一推理链 + `PMBattleContentBuild.cs` 的既有规避实践。
- **H5（中，≈0.6）**：把 hook 放进独立且不引用 Assembly-CSharp 的 Editor asmdef 后，Assembly-CSharp 失败时该 asmdef 仍能编译加载，
  从而有机会在下一次编译前修复。**需要実機验证**（见 §3 U1）。
- **H6（中高，≈0.8）**：`--decl-check` 先跑、仅在 diff 时 `--decl-gen`，可以避免 Unity 重导入/重编译风暴；
  与 `PMNetE2E.csproj`（pre-build 生成 + 运行期 decl-check 二次比对）的双保险思路一致。

---

## 3. 无法确定（缺少证据）

- **U1**：Unity 2019.4 在 Assembly-CSharp 编译失败时的域重载/回调触发行为（是否仍加载"不引用它"的独立 Editor asmdef、
  `[InitializeOnLoad]` 是否触发）。本地只证明了 API **存在**；本轮按约束**未运行 Unity**。→ 只能実機验证。
- **U2**：`compilationStarted` 回调内写生成物、并依赖 Unity 在**同一次**编译里拾取新产物是否可靠（重入、文件时间戳、编译文件快照时机）。
- **U3**：`PMNET_DECLARATIONS` 被误定义在 Unity 侧时的完整后果链（错误条数、是否连带 Editor 程序集）未実測；
  只能确定是**硬错误、fail-loud**。
- **U4**：Roslyn 4.11 在"符号启用 + 类内无体方法"下的 parse diagnostics 确切内容（H3 的反面）——
  在不编译任何工程的前提下无法实测（未编译）。
- **U5**：`Client/Assets/mcs.rsp` 在 2019.4 是否仍被当作响应文件读取（今天内容仅 `-unsafe`）。
  若仍被读取，它是"误定义符号"的另一条通道；未查证 Unity 2019.4 的 rsp 读取规则。
- **U6**：`Docs/plans/net-architecture-migration.md` §3.9.5 与用户新口径（标记放普通名）不一致时，
  以哪一份为最终契约——本轮按"用户冻结需求优先"推演，但**契约文本尚未更新**，属于文档欠账。

---

## 4. 已检查范围

**已读（完整）**：`AGENTS.md`、`Client/Assets/AGENTS.md`、`Server/AGENTS.md`、
`Docs/plans/net-r2-codegen-contract.md`、`windows-shell-compat/SKILL.md`、`Tools/PMDeclModel/PMDeclModel.cs`、
`Tools/PMNetGen/Program.cs`、`Tools/PMNetGen/DeclValidation.cs`（全）、
`Tools/PMNetGen/DeclScanner.cs` 与 `Tools/PMNetGen/DeclEmitter.cs`（全，含行号级定位）。

**已读（有界/节选）**：
`Docs/plans/net-architecture-migration.md` 2395–2482（R6 / 旧链退役 / 框架审计）、
§3.9.1 A3/A9、**§3.9.5**、§4.1–§4.3；
`Client/Assets/Scripts/PMR3/PMR3Player.cs`（RPC/RepNotify 声明与调用桩用法）、
`Client/Assets/Scripts/PMR3/Generated/PMNet.PMNet.R3.PMR3Player.g.cs`（RPC 段）、
`Client/Assets/Editor/PMDsBuild.cs`（全）、`Client/Assets/Editor/PMBattleContentBuild.cs`（入口/约定段）、
`Client/Assets/Scripts/PMNet/Declarations/PMStableHash.cs`（哈希口径段）、
`Client/Assets/Scripts/PMNet/Declarations/PMNetDeclarations.cs`（AttributeUsage 段）、
`Docs/plans/pmnet-r3-ids.json`（RPC 键）、`Tools/PMNetE2E/PMNetE2E.csproj`（pre-build target）、
`Tools/PMNetE2E/E2eFixtures.cs`（RPC 声明段）、`Tools/PMDeclCheck/PMDeclCheck.csproj` 与 `Program.cs`（7.3 编译断言段）、
`ProtobufAndNotepad/Protobuf/build.bat`（生成/校验段）、`Client/ProjectSettings/ProjectSettings.asset`（define 段）、
`Client/Assets/mcs.rsp`。
**外部事实**：`D:/Unity/2019.4.8f1/Editor/Data/Managed/UnityEditor.dll` 符号检查（只读）。

**未做（守住边界）**：未扫描无关业务模块/目录；未读 `PMR5Projectile.cs`/`PMR3Runtime.cs` 全文（仅在首搜入口中按需引用）；
未编译任何工程、未运行任何测试或 Unity、未启动/停止任何服务；未执行 SVN/提交；除本文件外**零写入**。

---

## 5. 建议下一步（最小补充查询 / 运行时验证）

1. **[実機，必须先做]** 用一次真实 Unity 2019.4 打开工程验证 U1/U2：
   （a）在 `Assets/Editor` 放一个最小 hook（`CompilationPipeline.compilationStarted` + `InitializeOnLoadMethod` 各打一条日志），
   （b）人为让 Assembly-CSharp 编译失败，（c）观察 hook 是否运行、`compilationStarted` 是否触发；
   （d）再把它挪进**不引用 Assembly-CSharp 的独立 Editor asmdef** 重复一次。结论决定 C6/C7(i) 是否可用。
2. **[実機]** 验证误定义符号的后果（U3）：临时在 Player Settings 加 `PMNET_DECLARATIONS`，
   记录错误集合后**立即移除**（该项纯验证，不要入库）。
3. **[最小补充查询，不需実機]** 确认 Unity 2019.4 的响应文件读取规则（U5）：查官方 Manual 的
   "csc.rsp / mcs.rsp" 章节，确认 `mcs.rsp` 是否仍生效；结论只影响"误定义符号"的通道清单。
4. **[实现前冻结项]** 更新契约文本（U6）：把 `net-architecture-migration.md` §3.9.5 与
   `net-r2-codegen-contract.md` §4.3 改成"标记普通名声明 + 调用普通名 + 业务写 `<M>_Implementation`"，
   并把 A4 的规则 14（无体声明 / `_Implementation` 必存在 / 逐参数同名同类型）写入 §5 规则表。
5. **[实现顺序建议，供后续批次]** A1（符号）→ A2/A4（事实与规则 14）→ A6–A8（发射器 3 处命名）→
   一次性重新 `--decl-gen`（预期锁文件零 diff）+ 门禁（含 A14 负向断言）→ A11/A12 消费侧批量改名。
   任何"只改一半"的中间态都会**编译失败**（fail-loud，符合项目门禁取向），因此建议单批原子推进。
