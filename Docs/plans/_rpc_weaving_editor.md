# RPC 编织便利层（Editor / Player 接线）实现报告

范围：**独立 Unity2019.4 Editor-only 接线层**（`Client/Assets/Editor/PMNetWeaving/`）+ 真实 Unity API 编译门禁
（`Tools/PMNetWeavingEditorCheck/`）。本轮**只写接线代码**：没有启动 Unity、没有执行生产 weave、
没有编织任何程序集、没有改 Library、没有改任何资产、没有 git 写入。

契约：`Docs/plans/net-rpc-weaving-contract.md`（§2 工具/CLI 冻结、§4 Editor 与构建接口、§5 验收口径）。
状态与测试口径只在 `Docs/plans/net-architecture-migration.md`（T-W4）。

---

## 0. 结论摘要

| 项 | 结果 |
|---|---|
| Editor 编译路径 | `CompilationPipeline.assemblyCompilationFinished`，**真实签名 `Action<string, CompilerMessage[]>`**（2019.4 已核）；只处理文件名**恰好**为 `Assembly-CSharp.dll` 的程序集；编译含 `CompilerMessageType.Error` 时**不写程序集** |
| Player 打包路径 | `UnityEditor.Build.IPostBuildPlayerScriptDLLs` **在 2019.4 真实存在且为 public**，`void OnPostBuildPlayerScriptDLLs(BuildReport)`，文档原文"just after the player scripts have been compiled"；据此在 IL2CPP/Mono 转换之前处理脚本 DLL |
| 目标路径来源 | 编辑器=回调 `assemblyPath`（记入 SessionState）；Player=`report.files` 中同名的绝对路径，或 `summary.outputPath` 推导的 StandaloneWindows `<Exe>_Data/Managed`。**不硬编码 Library/ScriptAssemblies** |
| 第三方 DLL | 只按精确文件名匹配，不做前缀匹配（避免误伤 `Assembly-CSharp-Editor.dll` / `-firstpass.dll`） |
| 真实编译 | `dotnet build Tools/PMNetWeavingEditorCheck -c Release` → **退出码 0，0 警告 0 错误**（netstandard2.0 + C#7.3 + 真实 UnityEditor.dll） |
| 门禁是否真的有效 | 真实编译首轮**抓到 2 个问题**（1 个真实 API 形状差异 `CS0019` + 1 个特性摆放错误 `CS0592`，见 §3.3），不是"假绿" |
| 生产 weave | **未执行**（`Tools/PMNetWeaver` 本轮由另一组实现，当前仓库尚不存在该工程） |
| 实机回调 | **PENDING_USER**（不启动 Unity、不抢锁） |

---

## 1. 必读文档与实际阅读顺序（委派项要求）

按委派项指定的顺序**完整**阅读：

1. `D:/UGit/hyld-master/AGENTS.md`（入口路由：客户端→`Client/Assets/AGENTS.md`、服务端→`Server/AGENTS.md`、网络迁移计划路径）
2. `D:/UGit/hyld-master/Client/Assets/AGENTS.md`（完整）
3. `D:/UGit/hyld-master/Server/AGENTS.md`（完整）
4. `D:/UGit/hyld-master/Docs/plans/net-architecture-migration.md` 末段 RPC 自然 C# 接口／IL 编织段（第 2498–2508 行的 T-W1..T-W6 表与"用户已批准，覆盖前段 T-UX"结论）
5. `D:/UGit/hyld-master/Docs/plans/net-rpc-weaving-contract.md`（完整，本层的唯一冻结接口来源）
6. `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`（完整；本报告所有命令均按该规则用 Bash 语法执行）

文档如何决定实现入口（可验收证据，不是"优先看文档"的口号）：

| 文档结论 | 对实现的直接约束 |
|---|---|
| `net-rpc-weaving-contract.md` §2「工具：Tools/PMNetWeaver（net8.0 CLI，Mono.Cecil 0.11.6）」「CLI：`--weave <assembly.dll> [--reference-dir <dir>]...`；`--check <assembly.dll>`；`--require-rpcs`」 | 本层只按这三个开关驱动 CLI，绝不发明第二套参数；工具自身不引入 Unity runtime 依赖 |
| 同文件 §2「生成的发送helper仍叫 `PMNet_<M>`…接收helper仍 `PMNet_RpcInvoke_<M>`」「每个有RPC的生成partial类增加 `internal static int PMNet_GetRpcWeaveVersion()`」 | 生成"编织格式是否落地"的判据 = 生成物里出现 `int PMNet_GetRpcWeaveVersion(` |
| 同文件 §4「Editor文件独立Editor-only asmdef，不能引用Assembly-CSharp」「编译有error不得处理」「不要硬编码猜缓存目录」 | asmdef `"references": []` + `autoIncludePlatforms=Editor`；错误即跳过；路径只来自回调/BuildReport |
| 同文件 §4「Player打包必须在托管脚本DLL生成之后且Mono打包/IL2CPP转换之前处理；优先核实Unity2019真实 `IPostBuildPlayerScriptDLLs` 接口与BuildReport脚本DLL路径，API不支持则报告BLOCKED」 | 本轮真实核接口（§2），并把"找不到目标 DLL 就构建失败"写进实现 |
| 同文件 §4「当前runtime没有asmdef，只Assembly-CSharp需处理」 | 精确文件名匹配 `Assembly-CSharp.dll`，只此一个 |
| 同文件 §4「源声明变化仍需要生成元数据；便利层必须检查/刷新源生成物，不能只weave过时发送签名」 | 外部 `PMNetGen --decl-check` 先跑、**只有逐字节不一致（exit 2）才 `--decl-gen`**，并复验 |
| 同文件 §4「工具构建/启动有超时与并发/输出预算、排空stdout/stderr」 | `PMNetToolProcess.cs` 四条纪律（超时/并发拒绝/双流异步排空/输出上限） |
| 同文件 §4「初次启动hook尚未加载不能保证自动救援，需外部生成/编织命令+运行guard」 | 启动 banner 明写该边界；菜单留外部修复入口；不承诺自动恢复 |
| 同文件 §5 T-W4「真实Unity2019 API编译及Player管线位置证据；自动回调和真实Mono/IL2CPP运行只可PENDING_USER」 | 本报告 §2/§3 给 API 证据，运行期一律标 PENDING_USER |
| 同文件 §5「中文源码BOM+CRLF，Unity新增cs/asmdef/目录配唯一meta（无BOM/LF）」 | 新增 `.cs` = UTF-8 BOM + CRLF；asmdef/meta = 无 BOM + LF；4 个新 GUID 已在 `Client/Assets` 全局查重为 0 命中 |
| `Client/Assets/AGENTS.md` §7「声明入口：`Tools/PMNetGen --decl-gen Client/Assets/Scripts/PMR3 --out-dir Client/Assets/Scripts/PMR3/Generated --id-lock Docs/plans/pmnet-r3-ids.json`」「必须本次build成功后再run」 | 本层刷新元数据用**同一组**真实参数（目录递归 + 专用锁），不另造入口 |
| `Client/Assets/AGENTS.md` §6「含中文源码 UTF-8 BOM + CRLF；新 .cs 配唯一 .meta（无BOM、LF）」 | 同上编码/  meta 纪律 |

辅助参考（只读，不改）：`Docs/plans/_rpc_csharp_feasibility.md` C1–C9（旧 `#if` 方案的只读调查，其中
C3 API 存在性、C7「Editor 默认程序集引用 Assembly-CSharp ⇒ 它编不出来时 hook 也活不了」、C8「首次编译边界」、
C9「不要在 hook 里无脑 Refresh」四项事实对本轮仍然成立，并直接决定了本层的 asmdef 与刷新策略）；
`Tools/PMNetGen/Program.cs` 与 `Tools/PMNetGen/DeclScanner.cs`（退出码语义与输入过滤规则）；
`Docs/plans/_r6_declaration_report.md`（真实 decl-gen 命令原文）。

---

## 2. API 证据（真实 `D:/Unity/2019.4.8f1/Editor/Data/Managed/UnityEditor.dll`）

方法：Windows PowerShell 5.1（.NET Framework）`ReflectionOnlyLoadFrom` 反射真实 DLL + Unity 自带
`UnityEditor.xml` 文档 + 官方 2019.4 ScriptReference。**未启动 Unity**。

### 2.1 `CompilationPipeline.assemblyCompilationFinished`（编辑器编译路径）

反射结果（事件存在，处理器类型需先加载 UnityEngine 才能解析）：

```
E: assemblyCompilationStarted     :: Action`1[System.String]
E: assemblyCompilationFinished    :: (需解析 UnityEngine 依赖，见下)
E: compilationStarted / compilationFinished :: Action`1[System.Object]
```

`UnityEditor.xml`（Unity 自带文档，第 7483 行）给出**逐字**签名与参数语义：

```
?:UnityEditor.Compilation.CompilationPipeline.assemblyCompilationFinished(System.Action`2<System.String,UnityEditor.Compilation.CompilerMessage[]>)
  <summary>An event that is invoked on the main thread when compilation of an assembly finishes.</summary>
  <param name="value">First parameter is the output assembly path. Second parameter are the compiler messages.</param>
```

⇒ **第一个参数是"输出程序集路径"**（不是程序集名、不是目录），因此本层直接把它当目标文件路径使用，
完全满足"来源路径必须真实回调不猜缓存位置"。

`CompilerMessage` 与 `CompilerMessageType`（真实反射 + XML 第 7689–7733 行）：

```
CompilerMessage  isPublic=True  isValueType=True（结构体）
  F: System.String message   public
  F: System.String file      public
  F: System.Int32  line      public
  F: System.Int32  column    public
  F: UnityEditor.Compilation.CompilerMessageType type   public   ← 是**字段**不是属性
CompilerMessageType public=True，成员：Error / Warning
```

⇒ "有 CompilerError 不处理"可实现为 `messages[i].type == CompilerMessageType.Error`（本层已这样写）。

`UnityEditor.Compilation.AssembliesType`：`Editor / Player / PlayerWithoutTestAssemblies`（用于在拿不到
回调路径时按 `Assembly.name` 取 `outputPath` 兜底，不猜目录）。

### 2.2 `IPostBuildPlayerScriptDLLs`（Player 打包路径）—— **支持，且是正确窗口**

真实反射（UnityEditor.dll）：

```
UnityEditor.Build.IPostBuildPlayerScriptDLLs   public=True, IsInterface=True
  M: Void OnPostBuildPlayerScriptDLLs(UnityEditor.Build.Reporting.BuildReport)
  I: UnityEditor.Build.IOrderedCallback
UnityEditor.Build.IOrderedCallback   P: Int32 callbackOrder   （接口必须实现）
UnityEditor.Build.IPostprocessBuild  M: Void OnPostprocessBuild(BuildTarget, String)   （存在，但**不是**本层用的窗口）
UnityEditor.Build.IPostBuildPlayerScriptDLLsWithContext  → MISSING（2019.4 没有，别照新版文档写）
```

Unity 自带 XML（第 5257/5262 行）与官方 2019.4 ScriptReference 逐字一致：

> "Implement this interface to receive a callback just after the player scripts have been compiled."

UNITY 安装目录里还留有内部处理器注册名 `BuildPlayerScriptDLLProcessors` /
`OnPostBuildPlayerScriptDLLs`（对 UnityEditor.dll 做字节串检索命中），与该接口是同一件事。

⇒ **API 支持**：可以在"脚本 DLL 已生成"之后、"IL2CPP/Mono 转换"之前处理脚本 DLL。
本层据此实现 `PMNetPlayerScriptDllsWeaver : IPostBuildPlayerScriptDLLs`，
并且**不使用** `IPostprocessBuild`（那是打包完成后改包，契约明确禁止用它冒充）。

**支持边界（必须诚实登记）**：本轮只证明"接口存在、签名正确、文档语义就是该窗口"；
"回调确实在 IL2CPP 之前触发、且此时 DLL 已经是最终托管令牌"属于**运行期事实**，未启动 Unity ⇒ **PENDING_USER**。

### 2.3 构建产物定位（`BuildReport` / `BuildFile` / `BuildSummary`）

```
UnityEditor.Build.Reporting.BuildReport     P: BuildFile[] files / BuildStep[] steps / BuildSummary summary / ...
UnityEditor.Build.Reporting.BuildFile       **结构体**  P: UInt32 id / String path / String role / UInt64 size
UnityEditor.Build.Reporting.BuildSummary    **结构体**  P: ... / String outputPath / BuildTarget platform / ...
BuildFile 没有嵌套 BuildFileRole 类型；UnityEditor.dll 里检索不到 `BuildFileRole`
```

两个被真实编译门禁抓出来的**关键差异**（对本层实现有直接影响，也是与新版 Unity 文档不同的地方）：

1. **2019.4 的 `BuildFile.role` 是 `System.String`**，不是后来版本的 `BuildFile.Role` 枚举。
   因此本层**不按 role 猜**（`role == ScriptDLL` 这类写法在 2019.4 根本编不过，也会把语义押在一个字符串上），
   只按"文件名精确等于 `Assembly-CSharp.dll`"从 `report.files` 里取**绝对路径**。
2. **`BuildFile` / `BuildSummary` 是结构体** ⇒ 代码里不能写 `== null`（CS0019），只能判 `path`/`outputPath` 是否为空串。
   （这条是本轮真实编译报错后修正的；见 §3.3。）

⇒ 目标路径的两个来源都可解释：`report.files[].path`（文档保证是绝对路径）与
`summary.outputPath` + StandaloneWindows 的 `<Exe>_Data/Managed` 布局（**只对该平台族**推导，其它平台不猜）。
两者都拿不到时，**构建直接失败**，不产出未编织的 Player。

### 2.4 编辑器侧其它被使用的 API（全部真实反射确认，而非记忆）

| API | 反射结论 | 本层用途 |
|---|---|---|
| `EditorApplication.playModeStateChanged` | `E: Action`1[PlayModeStateChange] | Play 门禁入口 |
| `PlayModeStateChange` | `EnteredEditMode / ExitingEditMode / EnteredPlayMode / ExitingPlayMode` | 只在 `ExitingEditMode` 拦 |
| `EditorApplication.isPlaying` | `P: Boolean`，**setter 存在且 static** | 拒绝进入 Play（`isPlaying=false`） |
| `EditorApplication.isCompiling` / `isUpdating` | `P: Boolean static` | 编译中不写盘/不刷新、门禁判"无法验证" |
| `EditorApplication.update` / `delayCall` | **静态字段**（`EditorApplication.CallbackFunction`），不是事件 | debounce 驱动（空闲后再刷新） |
| `EditorApplication.timeSinceStartup` | `P: Double static` | debounce 计时 |
| `EditorApplication.applicationContentsPath` | `P: String static` | 推导 `Managed` 与 `NetStandard/ref/2.0.0` 引用目录 |
| `EditorUtility.RequestScriptReload` | `M: Void RequestScriptReload()` | 手动 Repair 后的"让磁盘结果进内存"入口 |
| `EditorUtility.DisplayDialog(String,String,String)` | 3 参重载存在 | 门禁拦截提示（batchmode 下不弹） |
| `AssetDatabase.Refresh()` | `M: Void Refresh()` / `Refresh(ImportAssetOptions)` | 生成元数据后重新导入 |
| `SessionState.Set/GetBool/Int/String`、`EditorPrefs.*` | 全部存在 | 跨域重载状态 / 门禁开关 |
| `UnityEditor.Menu.SetChecked(String, Boolean)` | `M: Void SetChecked(System.String, System.Boolean)` | 开关菜单的勾选态 |
| `UnityEditor.Build.IPreprocessBuildWithReport` | `M: Void OnPreprocessBuild(BuildReport)` | 构建前 preflight |
| `UnityEditor.Build.BuildFailedException` | 类型存在（继承 Exception） | 构建失败闭合 |
| `UnityEditor.InitializeOnLoadAttribute` | 类型存在（**只能贴在类上**，贴到静态构造会 CS0592） | 域重载后接线 |
| `UnityEditor.Compilation.Assembly.outputPath` | `P: String`（实例属性） | 兜底解析目标路径 |

### 2.5 未在 UnityEditor.dll / Common 里找到的东西（避免照抄新版文档）

- `UnityEditor.Build.IPostBuildPlayerScriptDLLsWithContext`：**MISSING**（2019.4 无）。
- `UnityEditor.Compilation.CompilerMessage` 是**经典类 + 公开字段**（非属性）。
- `Unity.CompilationPipeline.Common.dll` 里只有 `ILPostProcessor` / `ICompiledAssembly` 等（ILPP 路线），
  本层**不使用** ILPP：契约指定的是"编译后、域重载前编织磁盘程序集"的路线，而 ILPP 不能落盘改写，
  且与冻结 CLI 不是同一套用法。

### 2.6 asmdef 字段选择（按真实 2019.4 兼容面）

仓库内 Unity 自带包（`Client/Library/PackageCache/com.unity.2d.animation@3.2.6/Editor/*.asmdef`）的 JSON 含
`rootNamespace` / `versionDefines` / `noEngineReferences` 等较新字段。本层只写**自 2017.3 起就存在**的核心字段
（`name / references / includePlatforms / excludePlatforms / allowUnsafeCode / overrideReferences /
precompiledReferences / autoReferenced / defineConstraints`），理由：

- 缺省字段一律取默认值，而默认值恰好就是本层意图（无 `versionDefines` = 不定义版本宏；
  不写 `noEngineReferences` = 保留引擎引用）；
- `rootNamespace` / `noEngineReferences` 是 2020.1+ 才有的字段，本项目用不到，写进去只会被旧编辑器忽略；
- `"references": []` + `"autoReferenced": false` ⇒ **双向零 Assembly-CSharp 耦合**：本程序集不引用它，
  它也不会自动引用本程序集（预定义程序集不会把 Editor-only 程序集带进 Player 引用链）。
  注意 `autoReferenced` 只影响"预定义程序集是否自动引用"，不影响本程序集被编译与加载，
  因此 `[InitializeOnLoad]` 与 Build 回调的发现不受影响。

---

## 3. 真实编译验证（`Tools/PMNetWeavingEditorCheck`）

### 3.1 工程取向

与仓库既有 `Tools/PMBattleContentBuildCheck`（真实 Unity DLL 门禁）同构：
`netstandard2.0` + `LangVersion 7.3`（对齐 `Client/ProjectSettings/ProjectSettings.asset` 的
`apiCompatibilityLevel: 6`），引用 **真实** `D:/Unity/2019.4.8f1/Editor/Data/Managed` 下的
`UnityEngine.dll` / `UnityEngine.CoreModule.dll` / `UnityEngine.IMGUIModule.dll` /
`UnityEngine.SharedInternalsModule.dll` / `UnityEditor.dll`，精确编译本层两个源文件：

```
Client/Assets/Editor/PMNetWeaving/PMNetWeavingEditor.cs
Client/Assets/Editor/PMNetWeaving/PMNetToolProcess.cs
```

`TreatWarningsAsErrors=true`（这份代码要在用户编辑器里天天跑，不接受"只是警告"的积累）。
它证明：接口实现签名、委托形态、结构体/字段 vs 属性、静态字段 vs 事件、枚举成员**全部真实存在且用法正确**。
它**不证明**运行期：没有启动 Unity、没有触发任何回调、没有编织任何程序集。

### 3.2 结果

```
> dotnet build Tools/PMNetWeavingEditorCheck -c Release
  PMNetWeavingEditorCheck -> Tools/PMNetWeavingEditorCheck/bin/Release/netstandard2.0/PMNetWeavingEditorCheck.dll
已成功生成。
    0 个警告
    0 个错误
EXIT=0
```

### 3.3 门禁不是"假绿"：首轮真实失败记录（本层因此改掉 2 个问题）

| 轮次 | 退出码 | 报错 | 说明与处置 |
|---|---|---|---|
| 1 | 1 | `error CS0592: 特性"InitializeOnLoad"对此声明类型无效。它仅对"类"声明有效` | 我把它误贴在静态构造上；改到类上。（同类问题的真实教训：该类特性只能贴类） |
| 2 | 1 | `error CS0019: 运算符"=="无法应用于"BuildFile"和"<null>"` 与 `BuildSummary` 同错 | **暴露 2019.4 的 `BuildFile`/`BuildSummary` 是结构体**：删掉 null 判定，改判 `path`/`outputPath` 空串。若照新版文档写 `role == BuildFile.Role.ScriptDLL` 或 `summary == null`，在 Unity 里就是一个必炸的编译错误 —— 这正是本门禁的价值 |
| 3 | **0** | 0 警告 0 错误 | 通过 |

### 3.4 生成元数据参数的空跑验证（只读，不改仓库）

用本层将要使用的**同一组参数**直接跑一次真实 `--decl-check`（不写盘）：

```
> dotnet Tools/PMNetGen/bin/Release/net8.0/PMNetGen.dll --decl-check Client/Assets/Scripts/PMR3 \
    --out-dir Client/Assets/Scripts/PMR3/Generated --id-lock Docs/plans/pmnet-r3-ids.json
[PMNetGen] 声明扫描：文件 7 个（语法错误 0 处），网络类 2 个，复制属性 13 个，RPC 13 条
    ClassId=227098277  PMNet.R3.PMR5Projectile  props=1  rpcs=0  maskBits=1  hash=0xD4CB0B42
    ClassId=405815557  PMNet.R3.PMR3Player     props=12 rpcs=13 maskBits=12 hash=0xB09BCD1C
[PMNetGen] 同步校验通过: 3 个产物 + 锁文件
EXIT=0
```

⇒ （a）参数构造正确（工作目录=仓库根、源=目录递归）、（b）**当前仓库生成物与声明同步**，
因此本层的自动刷新路径今天**不会**触发任何生成写盘（无副作用）。

---

## 4. 实现内容与设计判据

### 4.1 文件清单（本轮唯一写入）

| 文件 | 说明 |
|---|---|
| `Client/Assets/Editor/PMNetWeaving.meta` | 新目录 meta（folderAsset，无BOM/LF，GUID `e6c44760478d49759516b6fd001260e8`） |
| `Client/Assets/Editor/PMNetWeaving/PMNet.Weaving.Editor.asmdef` | Editor-only asmdef；`references: []`、`autoReferenced: false`（双向零 Assembly-CSharp 耦合） |
| `.../PMNet.Weaving.Editor.asmdef.meta` | GUID `31440cbc519f473e815278cb81c289a5` |
| `.../PMNetWeavingEditor.cs` | 接线主体：编译/Player 编织、Play/Build 门禁、生成元数据刷新、菜单（UTF-8 BOM+CRLF） |
| `.../PMNetWeavingEditor.cs.meta` | GUID `153c3adfab214235aa48a5404b86b629` |
| `.../PMNetToolProcess.cs` | 外部工具执行器：超时/并发拒绝/双流排空/输出上限/只杀自己的子进程（UTF-8 BOM+CRLF） |
| `.../PMNetToolProcess.cs.meta` | GUID `eda9dcced467442094a469b237165eff` |
| `Tools/PMNetWeavingEditorCheck/PMNetWeavingEditorCheck.csproj` | 真实 Unity API 编译门禁（LF/无BOM，与 Tools 其它 csproj 一致） |
| `Docs/plans/_rpc_weaving_editor.md` | 本报告 |

4 个新 GUID 已对 `Client/Assets` 全量 `*.meta` 查重：**除自身文件外 0 命中**。
`bin/`、`obj/` 为 `dotnet build` 的常规产物（与其它 Tools 工程一致）。

### 4.2 关键设计判据（每条都对应一个真实风险）

**(1) 只处理 `Assembly-CSharp.dll`，且用**精确**文件名相等**
`Client/Assets` 下没有任何 asmdef，唯一含 RPC 的是 `Assembly-CSharp`（契约 §4）。
用 `StartsWith("Assembly-CSharp")` 会把 `Assembly-CSharp-Editor.dll` / `Assembly-CSharp-firstpass.dll` 也卷进来，
那等于去改第三方/编辑器程序集。实现用的是 `string.Equals(fileName, "Assembly-CSharp.dll", OrdinalIgnoreCase)`。

**(2) "编写格式是否落地"的判据必须存在，否则会误拦整个编辑器**
编织格式 v1 要求生成器发出 `internal static int PMNet_GetRpcWeaveVersion()`（契约 §2）。核对当前仓库真实状态：
`Client/Assets/Scripts/PMR3/Generated/*.g.cs` 与 `Client/Assets/Scripts/PMNet/Generated/*.g.cs` 里
**都还没有**该符号（`Tools/PMNetGen` 侧也检索不到 `GetRpcWeaveVersion/RequireRpcWeave/RpcBody`，即 P2 尚未落地），
且当前生成的发送 helper 仍是 **public** `PMNet_<M>`、接收 helper 仍直接调用业务的旧形态。

若此时把"编织失败"当作失败闭合，用户编辑器每次编译都会红灯、Play 被拦 —— 而实际上根本没有"编织契约"可违反。
因此本层以生成物里是否出现 `int PMNet_GetRpcWeaveVersion(` 作为"格式已落地"判据（读**真实生成物文本**，
跳过注释行，带 mtime 缓存）：

- **未落地**（当前）→ 自动编织与 Play/Build 门禁**整体休眠**，只记一次性提示；
- **已落地**（P2 之后）→ 严格 fail closed：编织失败 / 自检失败 / 无法验证 一律拦。

这是一个自愈设计：P2 一生效，无需改本层任何代码，门禁与自动编织自动转为严格模式。

**(3) 生成元数据：先 `--decl-check`，只有 exit 2（逐字节不一致）才 `--decl-gen`，然后复验**
- 只在声明源集合**指纹变化**时才 debounce 触发（1.0 s），并由 `EditorApplication.update` 在
  `!isCompiling && !isUpdating` 的空闲点执行 —— 绝不在编译回调里写盘或 `AssetDatabase.Refresh()`（契约/可行性调查 C9）。
- 同一份源状态只处理一次：成功记 `HandledFingerprint`，失败记 `AttemptedFingerprint`，
  避免"每次编译起一个 dotnet"变成新的噪音源。
- **不自激循环的结构保证**：指纹的输入集合与 `PMNetGen` 的扫描输入**同规则**
  （递归 `*.cs`，排除 `*.g.cs`、`/obj/`、`/bin/`，见 `DeclScanner.IsScanCandidate`），
  生成物 `*.g.cs` 本身不进指纹 ⇒ 生成器写自己的产物不会改变指纹 ⇒ "生成→重编译→再生成"被结构切断。
- `--decl-gen` 之后**再跑一次 `--decl-check` 复验**，这一步同时就是"二次生成零 diff"的可观察证据。

**(4) Play/Build 门禁：只对"已落地"的契约 fail closed，且区分"磁盘已改 / 内存未改"**
门禁在 `playModeStateChanged(ExitingEditMode)` 与 `IPreprocessBuildWithReport` 两处生效，判据依次是：
契约未落地 → 放行；正在编译 → 拦（无法验证）；**PendingReload** → 拦；
生成元数据未能确认同步 → 拦；目标路径拿不到 → 拦；`--check --require-rpcs` 失败 → 拦。
`--require-rpcs` 在这一层有明确语义：契约已落地就意味着该程序集必然含 RPC，非空断言能抓
"编织了错文件 / 工具什么都没找到"这类静默失败（对应契约 §5 T-W2 的"非空断言"）。

**(5) "磁盘改完 ≠ 内存已修好"被做成了真实状态，而不是一句注释**
手动 Repair（菜单）成功后写入 `SessionState.PendingReload = true`，Play 门禁据此拦截，
并提供 `Request Script Reload` 菜单作为唯一放行手段；静态构造（每次域重载都会跑）
会把该标记清掉，因此"重载过"与"没重载"是可区分的真实状态。
手动 Repair 的日志明写"已加载程序集仍是旧映像，必须脚本重载"。

**(6) 工具执行器的四条纪律**（`PMNetToolProcess.cs`）
- 超时（weave/check 120 s，build 300 s）；超时只对**本次自己启动的 PID** 做 `taskkill /PID <pid> /T /F`
  （连子进程树收掉，避免 MSBuild 孤儿持锁），**绝不按镜像名杀**（那会命中用户自己的 dotnet/Unity）。
- 并发：全局闸门，第二个调用**显式拒绝**而不是排队（排队只是把"两个进程同时改同一程序集"的窗口藏起来）。
- 双流：`BeginOutputReadLine/BeginErrorReadLine` + 每个流 64 KiB 上限（超出打截断标记），
  先带超时 `WaitForExit(ms)`，成功退出后再调无参 `WaitForExit()` 以确保异步读取收尾；超时路径靠 `ManualResetEvent` 有界等待。
- 不做 shell 拼接：`UseShellExecute=false` + 参数数组 + 自实现的 MSVCRT 引号规则（含反斜杠/引号边界），无注入面。

**(7) 工具构建到独立 `editor-tool` 输出**
`dotnet build <项目> -c Release -o Tools/PMNetWeaver/editor-tool`，之后**只从这个目录**加载工具 DLL，
避免复用源码目录 `bin/` 下别人构建的旧 DLL（"源代码旧 dll 继续用"正是本任务点名的坑）。
新鲜度按项目自身 `*.cs`/`*.csproj` 的 mtime 判定，另有"Rebuild Weaver Tool"菜单强制重建。
PMNetGen 同规则使用 `Tools/PMNetGen/editor-tool`。
`dotnet` 解析顺序：EditorPrefs 覆盖 → `PMNET_DOTNET` → `DOTNET_ROOT` → PATH（失败在结果里显式报错，不静默）。

**代价与失败模式（诚实登记）**：首次使用（`editor-tool` 目录不存在或工具源码比产物新）时，
工具构建发生在调用它的那一刻（编辑器编译回调 / Play 门禁 / 构建 preflight 内部），
`PMNetGen` 带 Roslyn 依赖、`PMNetWeaver` 带 Mono.Cecil，首次 `restore` 可能需要数十秒并阻塞当次回调；
这是"自动构建 + 独立输出"的代价，且是**一次性**的（之后按 mtime 直接复用产物）。
若工具工程在当时的中间状态自身编不过（或离线且包未缓存），则工具不可用 ⇒ 契约已落地时表现为
`weave` 失败并**拦截 Play/Build**（fail closed）；契约未落地时只记日志。
手动补救入口：菜单 `Rebuild Weaver Tool (editor-tool)`，或设置 `PMNET_DOTNET` / EditorPrefs 覆盖 dotnet 路径。

**(8) 首编译边界（不做虚假承诺）**
hook 只有在脚本域加载成功之后才存在。启动 banner 与代码注释都写明：
首编译若已损坏则无从自动恢复；能救的是（i）本 asmdef **不引用 Assembly-CSharp**，
因此 Assembly-CSharp 编译失败时它仍可加载、菜单仍可用（可行性调查 C7/H5；**实机未验**），
（ii）`Tools/PMNetGen` 是独立 net8.0 进程，不依赖任何 Unity 程序集。

---

## 5. 支持边界 / 未验证项（诚实清单）

| 项 | 状态 | 说明 |
|---|---|---|
| Editor 回调在**域重载之前**触发（因此磁盘编织结果会被后续加载拾取） | 证据支持，运行期未验 | 官方 2019.4 文档：该事件"invoked on the main thread when compilation of an assembly finishes"，第一参数为**输出程序集路径**；`EditorApplication.beforeAssemblyReload` 的存在说明重载发生在编译之后。**真实顺序 PENDING_USER** |
| Player 回调位于 IL2CPP/Mono 转换之前 | 文档语义支持，运行期未验 | 2019.4 文档逐字"just after the player scripts have been compiled"；未启动 Unity ⇒ PENDING_USER |
| 实际 weave 行为（幂等、失败不半写、PDB） | **未执行** | `Tools/PMNetWeaver` 本轮由另一组实现，当前仓库**不存在**该工程；本层已把它当"不可用"分支处理并显式报错。生产 weave 属 P2 |
| 自动编织/门禁在 P2 后的实际效果 | 设计就绪 | 判据是真实生成物里的 `int PMNet_GetRpcWeaveVersion(`；落地即自动严格化 |
| 报错对话框与 `EditorApplication.isPlaying=false` 能否成功取消一次 Play | PENDING_USER | 2019.4 `isPlaying` 有 static setter（已核），但"在 ExitingEditMode 里取消"是运行期行为 |
| `BuildFailedException` 从 `OnPostBuildPlayerScriptDLLs` 抛出能否让构建确实失败 | PENDING_USER | 这是 Unity 文档给出的构建失败机制；未启动 Unity 不能声称已验证 |
| Assembly-CSharp 编译失败时本 asmdef 是否仍加载（菜单可用性） | PENDING_USER | 可行性调查 H5 为中置信推断；本轮未启动 Unity |
| 非 StandaloneWindows 平台的 Player 脚本 DLL 路径 | 明确不支持（不猜） | 只对 StandaloneWindows/64 从 `summary.outputPath` 推导；其它平台只认 `report.files`，都没有则**构建失败** |
| 运行时 guard（`PMNet_RequireRpcWeave`） | 另一组后续接 | 本层只做接线闭合，不冒充已完成运行期拒绝 |
| 真实 Unity 内自动回调 / Mono / IL2CPP 运行 | PENDING_USER | 未启动 Unity、未改 Library、未抢工程锁 |

### 与既有工程的关系
本轮只读参考了 `Client/Assets/Editor/PMDsBuild.cs`、`PMBattleContentBuild.cs`、
`Tools/PMBattleContentBuildCheck`（真实 Unity DLL 门禁的写法先例）与 `Tools/PMDsBuild` 相关的报告，
**未修改**其中任何文件。已核实仓库既有门禁（`PMClientCheck` / `PMUnityGlueCheck` / `PMR4UnityCheck` /
`PMLegacyRetirementTest` 等）的编译集都是 `Client/Assets/Scripts/**` 通配 + `Client/Assets/Editor/<具体文件>.cs`，
**没有任何门禁通配 `Client/Assets/Editor/**`**，因此新增目录不会被既有替身门禁吸入。

### 安全边界遵守情况
未启动 Unity、未启动或终止任何用户进程/服务、未执行生产 weave、未改任何资产/Prefab/场景、
未改 Library、未 git add/commit/reset、未 SVN 操作、未递归委派、未创建委派列表之外的文件
（`Tools/PMNetWeavingEditorCheck/` 下的 `bin`、`obj` 为编译产物）。
