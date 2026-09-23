# P2：调用者迁移 + .NET 构建接线（证据与结论）

> 契约：`Docs/plans/net-rpc-weaving-contract.md`（§2 冻结接口 / §4 构建接口）
> 状态与验收仍只在 `Docs/plans/net-architecture-migration.md` 登记（T-W3 / T-W5）——本文件只给证据。
> 本组硬边界（只写这些文件）：`Directory.Build.targets`、`Client/Assets/Scripts/PMR3/{PMR3Player,PMR4MovementDriver,PMR5ProjectileDriver,PMR6CombatDriver}.cs`、
> `Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs`、
> `Tools/{PMR3IntegrationTest,PMR3RuntimeTest,PMR3UnitySmoke,PMR4IntegrationTest,PMR4NetworkTest,PMR5DeclarationTest,PMR5NetworkTest,PMR6DeclarationTest,PMR6NetworkTest}/Program.cs`、
> `Tools/PMNetE2E/{Program.cs,E2eFixtures.cs,PMNetE2E.csproj}`、本文件。
> 生成器（`Tools/PMNetGen/**`）与编织器（`Tools/PMNetWeaver/**`）由另一组/主侧负责，本组**只读**。

---

## 0. 一句话结论

两个目标都落地了：13 条 PMR3 声明 RPC（+ PMNetE2E 的 6 条夹具 RPC）的**业务/Tools 调用点已从
`PMNet_<M>` 改成普通名**（16 个文件、107 处、逐文件保持原编码），并新增了**仓库级 `Directory.Build.targets`**：
只有「编入了 `PMNetGeneratedRegistry.g.cs` 生成集合」的工程才会在 `CoreCompile` 之后、
`CopyFilesToOutputDirectory` 之前对 `@(IntermediateAssembly)` 做 `--weave` + `--check`；
工具自身与无码工程完全惰性（无递归、不改既有构建行为）。

**但端到端正向链路当前被一个编织器缺陷 BLOCK：** `Tools/PMNetWeaver` 的「写出后 PDB 完整性核对」
用 **「声明类型名.方法名」做键**（`RpcAssemblyWeaver.cs` `VerifyPdbIntegrity()`），把**同类型的合法重载**
（例如两个 `.ctor`）误判成「同名方法 SP 条数不一致」而明确失败。PMNet 核心被编进业务程序集
（Unity/Server 真实形态）后天然含 20 处这类重载，因此**全部 18 个参与工程当前都无法编织**。
该文件不在本组写边界内 ⇒ 只报告、不修（见 §3）。

---

## 1. 目标 1：调用者迁移（`PMNet_<M>` → 普通名 `M`）

### 1.1 范围与清单

迁移对象是「生成调用桩」的旧公开名 `PMNet_<M>`。涉及两个声明集合：

| 声明集合 | RPC 名 |
|---|---|
| `PMR3Player`（生产，13） | `ServerProbe` / `ClientEcho` / `ServerMovementInputV1` / `ServerMovementResyncV1` / `ClientMovementEventsV1` / `ClientMovementResyncV1` / `ServerProjectileSpawnV1` / `ServerProjectileHitV1` / `ClientProjectileDecisionV1` / `ServerCombatAttackV1` / `ServerCombatResultAckV1` / `ClientCombatAttackResultV1` / `ClientCombatMatchResultV1` |
| `PMNetE2E` 夹具（6） | `Fire` / `Notify` / `Push` / `Checked` / `Warp` / `Say` |

改动前逐文件清点（`PMNet_<名称>` 且后接非标识符字符，含注释引用）共 **107 处**，全部落在本组写边界内：

```
PMR3Player.cs 5 / PMR4MovementDriver.cs 4 / PMR5ProjectileDriver.cs 9 / PMR6CombatDriver.cs 4
PMClientSessionHost.cs 3 / PMR3IntegrationTest 3 / PMR3RuntimeTest 5 / PMR3UnitySmoke 8
PMR4IntegrationTest 3 / PMR4NetworkTest 7 / PMR5DeclarationTest 9 / PMR5NetworkTest 5
PMR6DeclarationTest 17 / PMR6NetworkTest 12 / PMNetE2E/Program.cs 13 / E2eFixtures.cs 0
```

迁移后同类清点为 **0 处**（`grep` 验证，见 §1.4）。

**刻意没有替换的符号**：`PMNet_Set_<成员>`（属性访问器）、`PMGeneratedClassId` / `PMGeneratedRpcId_*`
等 `PMGenerated*` 常量、`PMNet_RpcInvoke_<M>`（收包 helper）、`PMNet_BuildEntry`、
`PMNetGeneratedRegistry.EnqueueRemote / RemoteSender / PendingRpcCount / ClearPendingRpcs` 等运行时面。
**没有把真实 Deliver 负例改成直调业务体**：E2E 里经 `PMNetRpcReceive.Deliver` / `PMRpcEntry.Invoke`
的收包路径断言、`PMNetRpcReceive.ResetStats` 等一字未动；改动只发生在**发送侧调用点**。
也没有手改任何 `Generated/` 产物、没有动 ID 锁。

### 1.2 两处需要说明的处置

1. **`PMR5ProjectileDriver` 的「发送接缝」适配器改名**：该文件原先有 3 个
   `private static void PMNet_<M>(PMR3Player, byte[])` 薄封装（注释自称「唯一发送入口（生成桩）」），
   名字与冻结的**发送 helper** 完全同名（靠「类不同 + 参数不同」共存）。迁移时改名为
   `SendServerProjectileSpawnV1` / `SendServerProjectileHitV1` / `SendClientProjectileDecisionV1`，
   内部调用改为普通名（`player.ServerProjectileSpawnV1(payload)` 等）。
   理由：契约 §2 把 `PMNet_<M>` 这个名字**冻结给 private 发送 helper**；业务侧再留一个同前缀符号
   会让「旧前缀不再业务可见」这条验收无法判定。这是本组唯一一处**符号改名**（private static，
   3 个定义 + 3 个调用点，全在边界文件内）。
2. **`PMR3Player.ServerProbe` 业务体里的下行调用**：`PMNet_ClientEcho(nonce)` → `ClientEcho(nonce)`。
   该行的旧注释写着「`ClientEcho` 直接调用只会本地执行、不过网」——在编织方案下这句话**已经错了**
   （普通名就是网络入口），已同步改写，避免留下会误导维护者的错误断言。

### 1.3 注释改写（12 处，6 个文件）

`PMR3Player.cs` 5 处、`PMClientSessionHost.cs` 1 处、`PMR3IntegrationTest` 1 处、`PMR3UnitySmoke` 2 处、
`PMR4NetworkTest` 1 处（清掉写成旧前缀 `PMNet_*` 的引用）、`PMR5ProjectileDriver.cs` 2 处。
新措辞统一为「声明层入口（普通名 `X`）」，并说明「编译后由 Tools/PMNetWeaver 把业务体拆到私有
`PMNet_RpcBody_<M>`」。其余仍写「生成桩」的描述（指发送路径由构建期工具产出）保留原样，
不为改词扩大改动面。

### 1.4 编译面证据（本组能给出的最强证据）

| 工程 | TFM | 编译 | 编织步 | 交付 |
|---|---|---|---|---|
| `PMR3RuntimeTest` | net8.0 | **CS 错误 0** | 执行 `--weave ... --require-rpcs` → 失败 | 输出目录**无** `PMR3RuntimeTest.dll` |
| `PMR3RuntimeTest`（第 2 次，增量） | net8.0 | CoreCompile 被跳过 | **编织仍执行** → 失败 | 同上 |
| `PMNetE2E` | net8.0 | CS 错误 0 | 执行 → 失败（PDB 核对，见 §3） | 无 `PMNetE2E.dll` |
| `PMR6DeclarationTest` | net8.0 | CS 错误 0 | 执行 → 失败（缺 guard） | 无自身 DLL |
| `PMClientCheck` | netstandard2.0 | CS 错误 0 | 执行 → 失败（缺 guard） | 无自身 DLL |
| `Server` | net8.0 | CS 错误 0 | 执行 → 失败（缺 guard） | 独立输出无 `Server.dll`；`Server/bin` 前后 md5 一致 |

「CS 错误 0」证明**迁移后的调用点能编译**（普通名解析到声明方法，位置实参/命名实参/委托引用形式都成立）。
这里刻意**不**声称「编织成功」：生产 `Generated` 尚未由主侧重生成（仍是旧格式），失败点停在
「有 RPC 标记但缺 `PMNet_GetRpcWeaveVersion()`」这一预期护栏上；`PMNetE2E` 因为生成物由它自己的
`BeforeCompile` 目标重生成，才走到更深的 PDB 核对（见 §3）。

---

## 2. 目标 2：`Directory.Build.targets`（新增）

### 2.1 钩点选择与理由

`PMNetRpcWeave` 目标：`AfterTargets="CoreCompile"`，作用对象 `@(IntermediateAssembly)`（obj 里的中间程序集）。

1. `CopyFilesToOutputDirectory` 的正文就是
   `<Copy SourceFiles="@(IntermediateAssembly)" DestinationFolder="$(OutDir)">`
   （SDK 10 `Microsoft.Common.CurrentVersion.targets:4921`，已实读），
   而 `CoreCompile` 在 `CoreBuildDependsOn` 里排在 `PrepareForRun`（= `CopyFilesToOutputDirectory`）之前
   ⇒ **被拷到 bin / 交给依赖与运行方的那一份，就是织过的那一份**。
2. 只写工程自己的中间目录：不碰 `Server/bin/**` 这类可能正被运行中进程占用的输出，
   也不会因为目标输出目录被锁而让编织失败（`Server/bin` 实测 md5 前后一致，见 §1.4）。
3. 反过来，若改成「拷完之后再织 `$(TargetPath)`」：一旦 `CoreCompile` 增量重跑（obj 里未编织 DLL 被重写），
   下一次 Copy 就会把未编织 DLL 盖回 bin —— 那正是「磁盘看起来是新构建、其实是未编织」的静默失败形态。
   本方案从结构上排除了它。
4. **失败即止**：编织失败 = 构建失败，`CopyFilesToOutputDirectory` 不执行 ⇒ 不交付未编织 DLL（实测 §1.4）。

### 2.2 目标结构（5 个目标）

| 目标 | 分批 | 职责 |
|---|---|---|
| `PMNetRpcWeave_Collect` | 是（按 `@(Compile)`） | 找文件名恰为 `PMNetGeneratedRegistry.g.cs` 的项（参与判据），以及全部 `*.g.cs` 生成源码 |
| `PMNetRpcWeave_Commit` | 否 | 两个项列表齐了之后定稿 `_PMNetRpcWeaveActive`，并把两份列表打进日志 |
| `PMNetRpcWeave_ScanGeneratedFiles` | 是（按生成源码） | 逐文件把 `Contains('PMGeneratedRpcId_')` 结果打进日志；命中则**单调**提升 `_PMNetRpcWeaveHasRpcs` |
| `PMNetRpcWeave_ResolveRpcRequirement` | 否 | 记录 `require-rpcs` 最终结论 |
| `PMNetRpcWeave` | — | `AfterTargets=CoreCompile`：嵌套构建 weaver → `GetTargetPath` 取路径 → `--weave` → `--check` |

设计要点：

- **参与判据用执行期而非求值期**：PMNetE2E 的生成物由 `BeforeCompile` 目标刷新并补 `Compile` 项，
  求值期的 `@(Compile)` 快照可能看不到（该工程 csproj 里已有同样的教训注释）。实测 E2E 被正确识别。
- **收集与定稿拆开**：分批目标里「同批次刚建立的项」不能可靠地喂给同批次后续 `PropertyGroup` 条件。
  踩坑已写进 targets 文件头（另一个更隐蔽的坑：`IntermediateAssembly` 是**项**不是属性，
  写成 `$(IntermediateAssembly)` 会恒为空，表现为「接了线却从不编织」）。
- **`require-rpcs` 按实际有无 RPC 决定**：判据是生成源码里是否出现 `PMGeneratedRpcId_`
  （纯复制属性的类不产出它）。实测 `PMR3Player.g.cs`=True，`PMR5Projectile.g.cs` / `PMNetGeneratedRegistry.g.cs` /
  `SocketProto.PMNet.g.cs`=False；无码工程不参与、不会因此判红。
- **工具自身与依赖防递归**：`Tools/PMNetWeaver` 不编入任何 `PMNetGeneratedRegistry.g.cs` ⇒ 天然惰性。
  实测 `PMNetGen` / `PMDeclCheck` / `PMNetWeaver` 三个工程 `rc=0` 且 `weave_attempted=0`。
- **嵌套构建剔除父工程属性**（`RemoveProperties`：`OutDir/OutputPath/Target*/*Intermediate*/TargetFramework*/RID/SelfContained`）：
  外层用 `-o` 独立输出时，weaver 被建到自己的 `Tools/PMNetWeaver/bin/Release/net8.0/`（不污染调用方输出）；
  故意用 `-p:TargetFramework=netstandard2.0` 污染外层时，嵌套构建仍以 **net8.0** 产出并使用
  `Tools/PMNetWeaver/bin/Release/net8.0/PMNetWeaver.dll`（实测 Exec 命令行）。
- **先建工具再编织**：`<MSBuild Targets="Build">` + `<MSBuild Targets="GetTargetPath">`（不硬编码 TFM/输出目录），
  再校验路径存在，最后 `--weave` / `--check`。
- **没有全局旁路开关**：本文件只提供定位类覆盖（`PMNetWeaverProjectPath` / `PMNetRpcWeaveDotNetHost`）。
  未编织程序集在运行期本来就被 `PMNet_RequireRpcWeave()` 拒绝，构建期 Fail-closed 是唯一诚实的口径。

### 2.3 参与工程清单（18 个）

11 个 net8.0：`Server`、`PMNetE2E`、`PMR3IntegrationTest`、`PMR3RuntimeTest`、`PMR3UnitySmoke`、
`PMR4IntegrationTest`、`PMR4NetworkTest`、`PMR5DeclarationTest`、`PMR5NetworkTest`、`PMR6DeclarationTest`、`PMR6NetworkTest`；
6 个 netstandard2.0 替身编译门禁：`PMClientCheck`、`PMR4NetworkCheck`、`PMR4UnityCheck`、`PMR5NetworkCheck`、
`PMR6NetworkCheck`、`PMUnityGlueCheck`。后者恰好是「剔除父工程 TFM」这条防护真正有用的场景。

---

## 3. 发现 F-P2-1（BLOCKER，不在本组写边界）：编织器 PDB 完整性核对按「类型名.方法名」做键，合法重载误报

### 3.1 现象

`PMNetE2E` 的生成物被它自己的目标重生成成新格式（private helper + guard）后，编织器越过了预检，
在**写出后 PDB 核对**阶段失败：

```
[PMNetWeaver] 失败：写出后的 PDB 里同名方法 PMNet.PMNetworkObjectAttribute..ctor 的
sequence point 条数不一致（2 vs 3）：无法完成完整性核对。
```

**与 MSBuild 接线无关**（隔离实验）：把 `Tools/PMNetE2E/obj/Release/net8.0/PMNetE2E.{dll,pdb}` 拷到
临时目录后**直接调 CLI**，同一条错误、退出码 1：

```
dotnet Tools/PMNetWeaver/bin/Release/net8.0/PMNetWeaver.dll --weave <tmp>/PMNetE2E.dll --require-rpcs
→ [PMNetWeaver] 失败：写出后的 PDB 里同名方法 PMNet.PMNetworkObjectAttribute..ctor 的 sequence point 条数不一致（2 vs 3）
```

### 3.2 成因（读码 + 独立读取器交叉验证）

`Tools/PMNetWeaver/RpcAssemblyWeaver.cs` 的 `VerifyPdbIntegrity()`：

- 先用 `System.Reflection.Metadata` 读**刚写出的** DLL，建 `rid → "声明类型名.方法名"` 映射
  （键里**没有方法签名**）；
- 再遍历写出后 PDB 的 `MethodDebugInformation`，把 SP 条数记进 `observed[名字]`：

```csharp
int existing;
if (observed.TryGetValue(name, out existing)) {      // name = "typeName.methodName"，无签名
    if (existing != count) { throw new WeaverException("写出后的 PDB 里同名方法 " + name + " ..."); }
} else { observed[name] = count; }
```

- 于是**同类型的合法重载**（两个 `.ctor`、任何同名不同签名的重载）只要 SP 条数不同，就被判成「损坏」。

`PMNet.PMNetworkObjectAttribute` 正好是这种类型（`PMNetDeclarations.cs:34` / `:38`）：

```csharp
public PMNetworkObjectAttribute() { }                     // 空体
public PMNetworkObjectAttribute(string stableKey) { StableKey = stableKey; }
```

用独立读取器（`System.Reflection.Metadata`，PowerShell 只读脚本）扫 `PMNetE2E.dll` + PDB，
**复现出完全一致的 2 vs 3**，并给出碰撞面：

```
COLLISION  PMNet.PMNetworkObjectAttribute..ctor  -> SP counts: 2,3
COLLISION  PMNet.PMNetReader..ctor               -> SP counts: 2,10
COLLISION  PMNet.PMNetRpcReceive.Deliver         -> SP counts: 1,36
COLLISION  PMNet.PMRpcDispatch.EvaluateCallspace -> SP counts: 12,50
... （共 20 处）
TOTAL distinct method keys=1043; colliding keys (same name, different SP count)=20
```

即该程序集里有 **20 处合法重载**会被这条检查误判。PMNet 核心（`Client/Assets/Scripts/PMNet/**`）
被链接进每个参与工程（Unity 形态与 Server 形态都是「运行时编进同一程序集」），所以 **18 个参与工程全部命中**。

### 3.3 为什么 P1 与现有门禁没抓到

编织器自己的门禁 `Tools/PMNetWeaverTest` 本次实测 **通过 192 项 / 失败 0**（P1 报告当时是 141 项，
说明门禁与工具此后都扩展过）。它抓不到这条，是因为夹具形态是「PMNet 运行时编成**独立程序集**
`PMNet.Runtime.Temp`，只编织夹具程序集」——被编织的程序集里没有 `PMNetworkObjectAttribute` 的重载。
P2 的真实形态是**运行时与业务同程序集**，这条缺陷因此第一次被触发。
**结论：不是环境问题，是工具缺陷 + 夹具形态覆盖不到。**

### 3.4 影响与修复方向（交主侧 / 编织器负责组）

- 影响：T-W3/T-W5 的**正向链路（编织成功 → 织过的 DLL 被拷到 bin）当前无法达成**。
  本组接线能给出的证据只到「参与判定正确 + 编译正确 + Fail-closed 不交付未编织 DLL + 增量不跳过」。
- 修复方向（不在本组权限内，仅供主侧参考）：`observed` 的键改成**方法行号（rid，本来就是遍历键）**
  或「类型 + 方法名 + 签名」；`VerifyPdbIntegrity` 末尾按 `suffix` 反查 `PMNet_RpcBody_<M>` 的那段
  （约 1650 行）同样建议改成「按期望 RID / 精确签名」而不是「名字后缀唯一」。
- 建议同时给 `PMNetWeaverTest` 增一条**同程序集重载**夹具（运行时与业务同程序集 + 两个同名不同签名方法），
  否则这条缺陷还会再溜过一次。
- 最快复现（不需要 MSBuild）：
  ```
  cp Tools/PMNetE2E/obj/Release/net8.0/PMNetE2E.{dll,pdb} <tmp>/
  dotnet Tools/PMNetWeaver/bin/Release/net8.0/PMNetWeaver.dll --weave <tmp>/PMNetE2E.dll --require-rpcs
  ```

---

## 4. 验证矩阵（本组已跑 / 未跑）

| 编号 | 内容 | 命令 | 结果 |
|---|---|---|---|
| V1 | 接线不侵入无码工程 | `dotnet build Tools/{PMNetGen,PMDeclCheck,PMNetWeaver} -c Release -o <独立输出>` | rc=0 / CS 错误 0 / **weave 尝试 0 次**（惰性，无递归） |
| V2 | 参与判定 + RPC 探针（net8.0） | `dotnet msbuild Tools/PMR3RuntimeTest/… -t:PMNetRpcWeave_ResolveRpcRequirement` | 注册表 1 个、生成源码 4 个、逐文件 `PMHasRpcMarker`=False/True/False/False、结论 HasRpcs=true、Active=true |
| V3 | 参与判定（netstandard2.0 / E2E / Server） | 同上，目标换 `PMClientCheck` / `PMNetE2E` / `Server` | 三者 Active=true；E2E 为它自己的 3 个生成文件、HasRpcs=true |
| V4 | 编译面（迁移后可编译） | `dotnet build <6 个参与工程> -c Release -o <独立输出>` | 全部 **CS 错误 0**；失败只发生在 `--weave` 步骤 |
| V5 | Fail-closed：不交付未编织 DLL | 同 V4 | 输出目录**没有**工程自身 DLL（只有 CopyLocal 引用）；`CopyFilesToOutputDirectory` 未执行 |
| V6 | 增量：CoreCompile 被跳过时仍会编织 | `PMR3RuntimeTest` 连跑两次 | 第 2 次日志 `正在跳过目标“CoreCompile”…` 之后仍有 `PMNetRpcWeave:` + Exec 调用 |
| V7 | 不覆盖运行中 Server | Server 构建前后 `md5sum Server/bin/*/net8.0/Server.dll` | 两个 md5 均 OK，mtime 不变 |
| V8 | 嵌套构建不被父工程属性污染 | `dotnet build Tools/PMClientCheck -p:TargetFramework=netstandard2.0` | 无 NETSDK/TFM 错误；Exec 用的是 `Tools/PMNetWeaver/bin/Release/net8.0/PMNetWeaver.dll` |
| V9 | 工具先建后织 | 见 V4 日志 | 日志先出现 `PMNetWeaver -> …\Tools\PMNetWeaver\bin\Release\net8.0\PMNetWeaver.dll`，随后才是 `--weave` |
| V10 | 编织器自身门禁（对照） | `dotnet Tools/PMNetWeaverTest/bin/…/PMNetWeaverTest.dll --repo D:/UGit/hyld-master` | **192 通过 / 0 失败**（夹具形态为「运行时独立程序集」） |
| V11 | 碰撞面独立扫描 | PowerShell + `System.Reflection.Metadata` 读 PDB | 20 处同名重载 SP 条数不同（含 `PMNetworkObjectAttribute..ctor` = 2 vs 3） |

### 4.1 增量目标的诚实口径（不能只看 exit 0）

- 已证：**CoreCompile 被跳过时编织目标依然执行**（V6）；`--weave` 幂等、`--check` 不写盘（工具侧保证）。
- 未证（必须主侧重跑）：**「增量构建不会把未编织 DLL 覆写回 bin」**——正向编织从未成功过（被 §3 挡住），
  无法观察「已织好的 obj / bin 在下一次增量构建后仍保持编织态」。主侧修好 §3 后的必跑清单
  （同版本、独立输出、先 build0 再 run）：
  1. `dotnet build Tools/PMR3RuntimeTest -c Release -o <独立>` 连续两次：第 1 次必须
     `是否实际改写：是`，第 2 次必须 `是否实际改写：否` 且 `--check` 通过；两次 `bin/<X>.dll` 逐字节相同；
  2. obj 的 `PMR3RuntimeTest.dll` 与 bin 的**必须逐字节相同**（证明拷过去的是织过的那份）；
  3. 删掉 bin 只留 obj，再 `dotnet build`（不改源码）→ bin 必须再次出现且与 obj 一致；
  4. 16 + 3 个门禁（R3/R4/R5/R6/World/E2E/…）本次 build0 后 run0；E2E/R3/R4/R5/R6 必须真跑；
  5. 用「16 + 独立输出」的真实增量矩阵再确认一遍，不要只挑一个工程。
- 因此本文件**不声称**增量目标已「验证完成」，只登记 §4.1 第 1 条的前半段证据。

---

## 5. 构建副作用与编码口径（如实登记）

1. **生产 `Client/Assets/Scripts/PMR3/Generated/*.g.cs` 本组没有改**：内容仍是旧格式
   （`grep -c PMNet_GetRpcWeaveVersion` = 0，`PMNet_ServerProbe` 仍是 `public`），mtime 停留在**前一天**。
   git 里它们本来就在 modified 状态（本组接手前已如此）。
2. **`Tools/PMNetE2E/Generated/*.g.cs` 被「它自己的 `BeforeCompile` 目标」重生成**（构建 E2E 的副作用，
   不是手改）：现在含新格式（`PMNet_GetRpcWeaveVersion` / `private void PMNet_Fire` / 实例字段
   `PMNet_rpcWeaveGate = PMNet_RequireRpcWeave()` / `PMNet_BuildEntry` 开头调 guard）；`e2e-ids.json` 未变
   （ID 锁不动）。夹具 `E2eFixtures.cs` 保持**带体属性普通法**，一行未改。
   若主侧要求 E2E 生成物也走「主侧统一生成」的口径，重生成时统一覆盖即可（内容由生成器决定，不回写人工改动）。
3. **编码**：15 个文件保持原 UTF-8 BOM + CRLF；`Tools/PMR6DeclarationTest/Program.cs` 原本是
   **无 BOM + 纯 LF**，本组只改内容、**未改其换行/编码**（避免制造整文件重排 diff）。新增
   `Directory.Build.targets` 与本文件均为 UTF-8 BOM + CRLF。
4. **本组无 git 写操作**（未 add / commit / restore / stash）、无 SVN 写、未启动 Unity / DS / Lobby / Server，
   未触碰运行中的 `Server.dll`；构建统一输出到仓库外的独立临时目录（`%TEMP%/pmnet-p2*`）。
5. 边界外**一行未改**：`Tools/PMNetGen/**`、`Tools/PMNetWeaver/**`、`Client/Assets/Scripts/PMNet/**`、
   `Client/Assets/Scripts/PMR3/{PMR5Projectile.cs,PMR3Runtime.cs}`、`Docs/plans/net-architecture-migration.md`、
   `Docs/plans/net-r2-codegen-contract.md`。

---

## 6. 交主侧的下一步（顺序固定）

1. **先修 §3 的编织器缺陷**（`RpcAssemblyWeaver.cs` 的 PDB 核对键）——不修则 18 个参与工程一个都织不了；
   并给门禁补「同程序集重载」夹具。
2. 主侧重生成生产 `Client/Assets/Scripts/PMR3/Generated/`
   （`PMNetGen --decl-gen … --id-lock Docs/plans/pmnet-ids.json`）并跑 `--decl-check`；
   确认 ID 锁 / `ProtocolHash`（0xE6130FAA）/ 类摘要 0xB09BCD1C 不漂移。
3. 按 §4.1 清单跑真实 build0 / run0（含增量两连跑、obj↔bin 逐字节、16+3 门禁）；
   失败必须是护栏命中而不是「跳过编织」。
4. `Docs/plans/net-architecture-migration.md` 的 T-W3 / T-W5 状态，以及
   `Docs/plans/net-r2-codegen-contract.md` §4.1 / §4.3 的 API 面描述
   （`PMNet_<M>` 变 private、普通名入口、guard 方法）——两者都不在本组写边界内，需主侧 / 生成器组同步。
5. Unity Editor / Player 侧接线（`IPostBuildPlayerScriptDLLs`、P1 §5.2 的 `ReplaceFile` 身份陷阱）
   仍属 T-W4；本文件不声称任何 Unity 侧结论。
