# RPC 编织便利层 —— Editor 审查修复报告（续做：上下文隔离的未执行修复）

范围：`Client/Assets/Editor/PMNetWeaving/{PMNetWeavingEditor.cs, PMNetToolProcess.cs, PMNetWeavingPolicy.cs(新)}`
+ `Tools/PMNetWeavingEditorCheck/PMNetWeavingEditorCheck.csproj` + `Tools/PMNetWeavingEditorTest/{csproj, Program.cs}(新)`
+ 本报告。

契约：`Docs/plans/net-rpc-weaving-contract.md`（§4 Editor 与构建接口、§5 验收口径）。
状态与验收口径只在 `Docs/plans/net-architecture-migration.md`（T-W4/T-W5）。
上一轮（未执行的那一轮）的记录：`Saved/CLI-Delegation/checkpoints/cli-delegation-comprehensive-1790066311744-039c5eb0-2b2d-47c2-ae10-b40d87444dc6.md`
（worker 在 `trust.json.lock` mkdir EPERM 阶段于**任何工具调用之前**退出 ⇒ 0 工具、0 修改；主侧核盘确认 `review/policy/test` 均不存在）。

本轮**只**修改上面列出的文件：没有启动 Unity / DS / Lobby / Server，没有执行生产 weave，
没有改 Library、没有碰资产/Prefab/场景、没有改 `Tools/PMNetWeaver`、`Tools/PMNetGen`、
`Directory.Build.targets`、`Tools/PMNetE2E`、任何生产 `Generated/` 或业务源码，没有 git/svn 写操作，
没有递归委派。

---

## 0. 结论摘要

| 项 | 结果 |
|---|---|
| 已冻结的 Editor 修复（删休眠旁路 / 删门禁开关 / 删 Player 路径推测 / preflight 只核对 / 源 gen 标 PendingReload / 工具内容指纹 / 忙时不返回旧 InSync） | **全部落地**（逐条见 §3） |
| 主侧发现的 `PMNetToolProcess` 真实缺陷（Success 不看 Failure / 无参 WaitForExit 可永久挂 / 64 KiB 预算按行失效 / taskkill 顺序 ReadToEnd 无超时） | **全部修掉**，且有**反向对照实测**（§5.3） |
| 新纯策略文件 `PMNetWeavingPolicy.cs`（无 Unity API，可被两套编译面同时验证） | 新增（GUID `7a6377155a104b02819b7281c73f8938`，已在 `Client/Assets` 3145 个既有 GUID 中查重为 0 命中） |
| 真实 Unity2019 API 编译门禁（`Tools/PMNetWeavingEditorCheck`，含新 policy 文件） | **0 警告 0 错误**（隔离输出目录） |
| 纯 BCL 回归（`Tools/PMNetWeavingEditorTest`） | **25 / 0**（退出码 0） |
| 反向对照（旧模式） | 旧"无参 WaitForExit"在孙进程持有管道时 **8 s 未返回**（看门狗终止，exit 99）；旧"整行追加"实测收集 **200001** 字符且 `Truncated=false`（预算 65536） |
| 实机（Unity 回调顺序 / Player 管线 / IL2CPP） | **PENDING_USER**（不启动 Unity、不抢工程锁） |

---

## 1. 必读文档（按委派指定顺序）与它们如何决定入口

| # | 文档 | 得到的**可执行**约束（不是"优先看"口号） |
|---|---|---|
| 1 | 委派项指定的 checkpoint | 上一轮 worker 在工具启动**前**因 `trust.json.lock` mkdir EPERM 退出；台账"没有观察到工具执行记录"⇒ 0 修改。据此判定本次是**续做**而非重跑 |
| 2 | `D:/UGit/hyld-master/AGENTS.md` | 入口路由：客户端 → `Client/Assets/AGENTS.md`；服务端 → `Server/AGENTS.md`；网络迁移计划在 `Docs/plans/net-architecture-migration.md` |
| 3 | `Client/Assets/AGENTS.md`（完整） | §7 真实声明入口 `Tools/PMNetGen --decl-gen Client/Assets/Scripts/PMR3 --out-dir .../Generated --id-lock Docs/plans/pmnet-r3-ids.json`；"必须本次 build 成功后再 run"；§6 中文源码 UTF-8 BOM+CRLF、新 `.cs` 配唯一 `.meta`（无 BOM/LF）；§8 禁止无范围暂存与代提交 |
| 4 | `Server/AGENTS.md`（完整） | 服务端不承载局内仿真；PMNet 框架源码链接自 Client/Assets；"编译检查可输出独立 Tools 目录、不覆盖运行中的 Server.dll" ⇒ 本轮所有构建都用**独立输出** |
| 5 | `Docs/plans/net-architecture-migration.md` 末段（RPC 自然 C# 接口／IL 编织段，含 T-W1..T-W6 与"P3 工具审查修复…Editor 复核 worker 在 trust.json.lock mkdir EPERM 阶段退出…后续仅继续未执行的 Editor 修复"） | 确认本轮定位：**只续做未执行的 Editor 修复**，不重开 P3 工具审查、不改 E2E |
| 6 | `Docs/plans/net-rpc-weaving-contract.md`（完整） | §2 冻结 CLI（`--weave` / `--check` / `--require-rpcs`；`PMNet_<M>` private、`PMNet_RpcInvoke_<M>`、`PMNet_GetRpcWeaveVersion`）；§4 "Editor 独立 asmdef、编译有 error 不得处理、不硬编码猜缓存目录、Player 在托管 DLL 之后且 IL2CPP 之前、工具构建/启动有超时与并发/输出预算、排空 stdout/stderr"；§5 T-W4/T-W5 口径 |
| 7 | `Docs/plans/_rpc_weaving_editor.md`（完整） | 上一轮的 API 证据（2019.4 `assemblyCompilationFinished` 委托形态、`BuildFile`/`BuildSummary` 是**结构体**、`IPostBuildPlayerScriptDLLs` 存在）与"自动化/门禁自愈"设计；**本轮把其中"契约未落地则整体休眠"的过渡设计删掉**（见 §3/F1） |
| 8 | `Docs/plans/_rpc_weaver_review_fix.md` §7（兼容影响）及其 §5/§6/§8/§9 | P3 工具侧**未改** CLI/退出码/格式名（接线仍成立）；两处收紧需知情：`--check` 现在核对符号、`--weave` 会在目标旁建/删锁文件并拒绝并发 weave；§6.2 真实 E2E 208/1、§7 的 E2E 探针语义失效属主侧范围 |
| 9 | `windows-shell-compat` skill（完整） | Pi 的 `bash` 工具一律按 **Bash** 解释；需要 PowerShell 时用 `pwsh.exe`；含空格路径必须引用。本轮所有命令都是 Bash 形式（`dotnet build ...`、`python - <<PY`） |
| 10 | 源码事实核对（只读） | `Client/Assets/Scripts/PMR3/Generated/PMNet.PMNet.R3.PMR3Player.g.cs` 里**已存在** `PMNet_GetRpcWeaveVersion` + `PMNet_RequireRpcWeave`（P2 已落地）⇒ "契约缺失"不再是常态，而是**异常状态**，必须 fail closed 而不是休眠 |

---

## 2. 改动清单（本轮唯一写入）

| 文件 | 变化 |
|---|---|
| `Client/Assets/Editor/PMNetWeaving/PMNetWeavingPolicy.cs` | **新增**：纯 BCL 决策策略（目标文件名精确匹配 / 生成物契约探针 / Player 目标收集 / 工具输入内容指纹 / 声明源指纹 / decl-check 退出码归类 / 指纹旁车读写）。无任何 Unity API |
| `.../PMNetWeavingPolicy.cs.meta` | **新增**：无 BOM + LF，`guid: 7a6377155a104b02819b7281c73f8938`（对 `Client/Assets/**/*.meta` 3145 个既有 GUID 查重 0 命中） |
| `.../PMNetWeavingEditor.cs` | 修改：删休眠旁路（4 个入口）、删门禁开关、删 Player 路径推测、preflight 改 check-only、源 gen 标 PendingReload、工具内容指纹、忙时不返回旧 InSync、状态/菜单同步更新 |
| `.../PMNetToolProcess.cs` | 重写读取/排空路径：分块有界读取（取代 `BeginOutputReadLine`）、有界排空（取代无参 `WaitForExit`）、无事件回调、`taskkill` 同样有界、`Success` 纳入 `Failure` |
| `Tools/PMNetWeavingEditorCheck/PMNetWeavingEditorCheck.csproj` | 修改：把 `PMNetWeavingPolicy.cs` 纳入真实 Unity API 编译面（1 行 Compile + 注释） |
| `Tools/PMNetWeavingEditorTest/PMNetWeavingEditorTest.csproj` | **新增**：net8.0 纯 BCL 回归工程，**链接**（非复制）上面两个文件 |
| `Tools/PMNetWeavingEditorTest/Program.cs` | **新增**：25 个用例（含受控子进程行为与源码级回归） |
| `Docs/plans/_rpc_weaving_editor_review.md` | **新增**：本报告 |

编码（`Client/Assets/AGENTS.md` §6）：4 个 Unity 侧文件为 UTF-8 **BOM + CRLF**，`.meta` 为**无 BOM + LF**；
`Tools/PMNetWeavingEditorTest/{Program.cs, csproj}` 按同目录族约定 BOM + CRLF；
`Tools/PMNetWeavingEditorCheck/PMNetWeavingEditorCheck.csproj` 保持原有**无 BOM + LF**（未改编码）。
`bin/`、`obj/` 是 `dotnet build` 的常规产物。

---

## 3. 已冻结修复逐条（现象 → 成因 → 修法 → 证据）

### F1 契约缺失时的"整体休眠放行"旁路被删除（4 个入口全部 fail closed）
- **现象**：生成物里没有 `PMNet_GetRpcWeaveVersion` 时，自动编织与 Play/Build 门禁**整体休眠**，
  即"缺契约 ⇒ 放行"。
- **成因**：P1 时代生成器还没发出该符号，为避免"编辑器每次编译都红灯"设计了过渡态；
  P2 已落地该符号（§1 第 10 条），过渡态的前提消失，而按"缺契约就放行"的口径，
  一旦生成链路坏掉，程序集会被**静默地**放行到运行期 guard 去炸。
- **修法**：四个入口全部改为 fail closed：
  1. `OnAssemblyCompilationFinished`：不编织 ⇒ 记 `WeaveState.Failed` + `LogError` + 排入
     `ScheduleMetadataRefreshIfFingerprintChanged()`（可恢复路径：生成 → Unity 重编译 → 回调自然编织）；
  2. `CanEnterPlay`：拦 Play 并给出菜单恢复指引；
  3. `RunBuildPreflight`：`FailBuild(...)`（构建失败）；
  4. `WeavePlayerScriptDlls`：`FailBuild(...)`（拒绝产出未编织的 Player）；
  5. 顺带修掉菜单 `Repair`：先尝试刷新生成元数据，仍缺失则**明确中止**（不再"反正织一次"）。
- **证据**：`Tools/PMNetWeavingEditorTest` 的源码级回归用例
  `接线: Editor 源码没有门禁开关/路径推测/假新鲜`（断言 `尚未落地，跳过` / `跳过编织门禁` 均不存在、
  `FailBuild(` 调用点 ≥6）。

### F2 可关闭硬门的菜单被删除（“自动元数据便利可关，Play/Build 永远硬门”）
- **现象**：`Tools/PMNet/Weaving/Enforce Gate Before Play & Build` 菜单可把 `GateEnabled` 置 false，
  于是"未编织也能 Play"。
- **成因**：把"便利开关"和"正确性门禁"混在一个偏好里。
- **修法**：删除菜单项、`MenuToggleGate/Validate`、`GateEnabled()`、`GateEnabledPrefKey`；
  `CanEnterPlay` 不再有 `if (!GateEnabled()) return true;` 分支；
  banner 与 `Log Menu Status` 明写"门禁=始终硬门（无开关）"。
  仍然保留可关的 **Auto Refresh Generated Metadata**（那只是省手工步骤）。
- **证据**：同上用例断言 `GateEnabledPrefKey` / `GateEnabled()` / 菜单文案均不存在；
  真实 Unity API 编译 0 警告（删掉 `Menu.SetChecked` 的一处调用没有留下未用成员）。

### F3 Player 目标不再从 `summary.outputPath` 推测旧包 DLL
- **现象**：Player 路径在 `report.files` 拿不到时，按 StandaloneWindows 的
  `<Exe>_Data/Managed/Assembly-CSharp.dll` 布局"推导"一个路径；该路径此刻很可能还是
  **上一轮打包遗留的旧 DLL**（或根本不存在）。
- **成因**：把"看起来很合理的布局知识"当成证据；而且这条回退会掩盖"BuildReport 没给出目标"
  这一真实故障。
- **修法**：删除整段推导与 `BuildSummary`/`BuildTarget` 用法；候选只来自
  `report.files[].path` 中**文件名精确等于** `Assembly-CSharp.dll` 且 **`Path.IsPathRooted`** 的项
  （`PMNetWeavingPolicy.CollectPlayerScriptDllCandidates`，仅一个参数）；
  无候选一律 `FailBuild(PMNetWeavingPolicy.DescribeMissingPlayerTarget(...))`。
- **证据**：策略用例（精确名 / 相对路径不认 / `-Editor`、`-firstpass` 不认 / 大小写 / 去重 / 空项）；
  API 形状用例（反射断言**只有一个参数**，防止 outputPath/platform 参数被悄悄加回）。

### F4 构建 preflight 不再"gen 后声称当前程序集新鲜"
- **现象**：preflight 里 `EnsureMetadataVerified(true)` 可能改写生成源码，随后仍对
  **当前已编译**的 `Assembly-CSharp.dll` 跑 `--check`，并在日志里说"本次构建会自行编译这些产物"。
- **成因**：把"刚生成过"当成"当前程序集新鲜"；实际上此刻 DLL 是**改写前**那版源码编出来的。
- **修法**：preflight 改 **check-only**（`EnsureMetadataVerified(false)`）；非 `InSync` 一律
  `FailBuild`，并明确指引"先刷新生成元数据 → 等 Unity 重编译（本层会自动重新编织）→ 再构建"。
- **证据**：源码级回归断言 `EnsureMetadataVerified(false)` 存在、`本次构建会自行编译这些产物` 不存在。

### F5 源生成一写盘就标 `PendingReload`，Play 若刚 gen 必须 Refresh 并取消
- **现象**：`decl-gen` 改写生成源码后，`_generatedDuringGate` 只在当次内存里为 true；
  下一次 Play 可能看到"元数据已同步"就直接放行，而**内存里跑的还是旧源码编出来的程序集**。
- **修法**：`EnsureMetadataVerified` 在 `Regenerated` 分支写 `SessionState.PendingReload = true`；
  `CanEnterPlay` 把元数据核对**提到** PendingReload 判定之前，`Regenerated || _generatedDuringGate`
  时 `AssetDatabase.Refresh()` 并**取消本次 Play**（提示等编译完成再按一次），
  之后再按 PendingReload 拦（静态构造在域重载时清除该标记 ⇒ "重载过"与"没重载"可区分）。
- **证据**：源码级回归断言 `SessionState.SetBool(PendingReloadKey, true);` 出现 ≥2 次（Repair + 重新生成）。

### F6 工具新鲜度改成"输入内容指纹"（含生成器的真实依赖）
- **现象**：旧实现只比"工具工程目录下 `*.cs`/`*.csproj` 的 mtime > 产物 mtime"。
- **成因与后果**：`Tools/PMNetGen` 的语义依赖包含 `Tools/PMDeclModel`（IR + ID 锁）、
  被**链接**进 IR 的 `Client/Assets/Scripts/PMNet` 核心、以及仓库级 `Directory.Build.targets`；
  只看自身 mtime 会在这些依赖变化时继续复用旧工具；更糟的是删文件不会让任何 mtime 变新、
  时钟回拨/版本控制恢复会让 mtime 变**旧** ⇒ 两种漏检。
- **修法**：`PMNetWeavingPolicy.CollectToolInputFiles`（工程目录 + 显式额外依赖，排除
  `bin/obj/editor-tool`，gen 侧另排除 `Generated`）+ `ComputeFileSetFingerprint`
  （相对路径 + 长度 + **内容 FNV-1a 64**），指纹写在产物旁车 `<tool>.dll.pmnet-inputs`；
  指纹不一致（含缺失/不可读）即陈旧 ⇒ 重建；构建成功后回写指纹。
  删除/时间倒退/同长度改写全部被捕捉，纯 mtime 变化**不**触发重建。
- **证据**：4 个策略用例（输入集合排除规则、内容敏感/ mtime 不敏感 / 删文件 / 加文件 / 重命名、
  `root.targets` 属于真实输入、旁车读写往返）。

### F7 `EnsureMetadataVerified` 忙时不再返回旧 `InSync`
- **修法**：新增 `MetadataState.Busy`，忙时写 Busy 状态并返回 Busy（门禁按"无法确认同步"处理）。
- **证据**：真实 Unity API 编译通过（新增枚举成员）；源码级回归覆盖门禁判据
  （`metadata != MetadataState.InSync` ⇒ 拦）。

### F8 `PMNetToolResult.Success` 纳入 `Failure`
- **现象**：旧 `Success = Started && !TimedOut && ExitCode == 0`。
- **后果**：新引入的"进程 exit 0 但输出流不关闭"这类显式失败会被当成成功，调用方随即按
  "编织完成"继续往下走。
- **修法**：`Success` 增加 `string.IsNullOrEmpty(Failure)`。
- **证据**：`进程: 孙进程继承管道不无限挂且按失败处理` 用例（`Started=true, ExitCode=0, TimedOut=false`
  但 `Success=false`，`Failure` 明写输出流未关闭）。

### F9 有界分块读取（替换 `BeginOutputReadLine`）与按**块**执行的预算
- **现象**：旧实现 `BeginOutputReadLine` + `Append(e.Data)`：投递是**按行**的，
  没有换行时 StreamReader 内部缓存无界增长；`Append` 又会把**整行**（可能 200 k 字符）先塞进
  `StringBuilder`，预算只能在下一次 Append 才生效 —— 实测旧形态收集 200001 字符、`Truncated=false`。
- **修法**：两条后台任务直接 `StreamReader.Read(char[], 4096)` 分块读取，预算在**每块**上裁剪
  （`BoundedTextSink.Append(char[], int)`），超限即丢并置 `Truncated`。
- **证据**：`进程: stdout 无换行超 64KiB 有界且不挂` 断言"恰好保留 65536 个 `x` 字符 + Truncated"；
  `stdout+stderr 同时超 64KiB 有界` 断言两路都恰好 65536 且不死锁。

### F10 有界排空（替换无参 `WaitForExit`）+ 放弃排空不阻塞调用方
- **现象**：`WaitForExit(timeout)` 之后调用**无参** `WaitForExit()` 收尾异步读取；
  当重定向管道被孙进程继承时，这一步会一直等到孙进程关闭管道。
- **修法**：进程退出后用 `Task.WaitAll(pumps, DrainWaitMs=3000)` **有界**排空；
  超时即记 `Failure`（"进程已退出但输出流在 3000 ms 内没有关闭…按失败处理"）并放弃；
  `ForceClose` 把 `StreamReader.Dispose()` 放到**另一个线程**（fire-and-forget）执行 ——
  调用方**必须**在有界时间内返回，不能把 Dispose 挂在调用线程上。
  同时彻底移除事件回调（`OutputDataReceived`/`ErrorDataReceived`），
  从结构上消除"对象 Dispose 之后事件回调再触发"的生命周期竞态。
- **证据**：`进程: 孙进程继承管道不无限挂且按失败处理`（<30 s 有界、`Success=false`、原因含"输出流"）；
  另见 §5.3 的正反对照实测。

### F11 `taskkill` 自身也有界
- **现象**：旧代码 `taskkill` 后顺序 `StandardOutput.ReadToEnd()` + `StandardError.ReadToEnd()`（无超时）。
- **修法**：`TaskKillOwnTree` 用同一套分块排空 + `WaitForExit(KillerWaitMs=5000)` + 有界放弃；
  `KillOwnProcessTree` 仍然**只用 `/PID <pid> /T /F`**（绝不 `/IM`，那会命中用户自己的 dotnet/Unity/MSBuild），
  并在 kill 后 `WaitForExit(5000)` 兜底。
- **证据**：超时用例真实杀死子进程树（`超时被终止且之后可复用`，超时调用 <15 s 且闸门复位）。

### F12 "参数数组"口径诚实化
- **修法**：文件头与 `Arguments` 字段注释明确写清：Windows 上 `ProcessStartInfo` 接受的是
  **一个命令行字符串**，本执行器收 `IList<string>` 后按 MSVCRT 规则引用成该字符串，
  **不经 shell**，但**不声称**"绕过了命令行字符串"。MSVCRT 引用规则（引号前奇数反斜杠、
  结尾反斜杠翻倍、空串写 `""`）保持并新增边界用例。
- **证据**：`QuoteArgument` 边界用例 + 真实往返用例（空格/制表符/引号/反斜杠/中文/
  `%|^&<>;` 等 cmd 元字符/空串逐字节一致）。

---

## 4. 新增的"可失败"回归：纯 BCL 测试工程

`Tools/PMNetWeavingEditorTest`（net8.0，**链接**仓库里同一份 `PMNetToolProcess.cs` 与
`PMNetWeavingPolicy.cs`，不是复制）—— 25 个用例，全部真实执行：

1. 引用规则 2 条（含 MSVCRT 边界）；
2. 真实子进程 8 条：参数往返、stdout 无换行 200 KB、stdout+stderr 各 200 KB、
   非 0 退出、不存在的 exe、并发拒绝、超时后复用、**孙进程继承管道**；
3. Player 目标策略 4 条（含反射断言 API 形状、"无 outputPath 回退"）；
4. 工具输入内容指纹 4 条；
5. 契约探针 / 声明指纹 / decl-check 归类 5 条；
6. **对照真实生成物** 1 条（读 `Client/Assets/Scripts/PMR3/Generated/PMNet.PMNet.R3.PMR3Player.g.cs`，
   断言策略里的 token 真能命中该产物 —— 防止 token 漂移导致门禁误拦）；
7. **Unity 侧接线源码级回归** 1 条（本工程不编译需要 Unity 的 `PMNetWeavingEditor.cs`，
   因此对它做只读文本断言：无门禁开关、无 Player 路径推测、preflight 是 check-only、
   PendingReload 至少两处、无"缺契约就跳过"的旁路、`FailBuild` 调用点 ≥6）。

**这套回归确实能失败**（不是假绿）：
- 首轮运行 3 项失败 —— 其中 **1 项是真实遗留缺陷**：源码级用例抓到 `RunBuildPreflight` 与
  `WeavePlayerScriptDlls` 里**仍然保留**的"契约缺失 ⇒ 跳过"旁路（我上一轮替换了编译回调与 Play 门禁，
  漏了这两个入口）。这两个入口已按 F1 修掉。
- 另 2 项是我**自己的用例期望错误**（诚实记录）：
  (a) 断言"无空白/无引号的结尾反斜杠必须翻倍"—— 实现是对的（MSVCRT 只在引号前处理反斜杠），
  已改成"无触发条件不加引号 / 有空白时结尾反斜杠必须翻倍"两条分别断言；
  (b) Player 候选用例把大小写变体放在了**同一个目录**，被 `OrdinalIgnoreCase` 去重（这正是预期行为），
  已改为两个不同目录。

---

## 5. 门禁与真实运行结果

### 5.1 真实 Unity2019 API 编译门禁（`Tools/PMNetWeavingEditorCheck`）

```bash
dotnet build Tools/PMNetWeavingEditorCheck/PMNetWeavingEditorCheck.csproj -c Release \
  -o "$TEMP/pmnet-editor-check-<unique>"
```
```
PMNetWeavingEditorCheck -> ...\pmnet-editor-check-1790069662\PMNetWeavingEditorCheck.dll
已成功生成。  0 个警告  0 个错误
EXIT=0
```
（这是**真实** `D:/Unity/2019.4.8f1/Editor/Data/Managed` 下的 UnityEditor/UnityEngine DLL +
netstandard2.0 + C#7.3 + `TreatWarningsAsErrors=true`；新增的 policy 文件也在同一编译面里。）

### 5.2 纯 BCL 回归（`Tools/PMNetWeavingEditorTest`）

```bash
dotnet build Tools/PMNetWeavingEditorTest/PMNetWeavingEditorTest.csproj -c Release     # 0 警告 / 0 错误
dotnet Tools/PMNetWeavingEditorTest/bin/Release/net8.0/PMNetWeavingEditorTest.dll --repo D:/UGit/hyld-master
```
```
[PMNetWeavingEditorTest] 通过 25 / 失败 0
EXIT=0
```

### 5.3 反向对照（**临时脚手架，位于 `%TEMP%`，未写入仓库**）

三个对照都用同一组输入，意义是证明"被修掉的是真实缺陷，而不是理论洁癖"：

**(a) 管道被孙进程继承时，旧模式的收尾等待没有上界**
- 旧模式（复现 `WaitForExit(timeout)` + 无参 `WaitForExit()`）：
```
[OLD] WaitForExit(timeout) 返回=True，耗时 72 ms（子进程确实已经退出）。
[OLD] HUNG: 旧模式在 8s 内没有返回（子进程早已 exit 0，但重定向管道被孙进程持有）。
OLD_EXIT=99
```
- 新模式（链接仓库同一份 `PMNetToolProcess.cs`，**同一输入**）：
```
[NEW] 耗时 4134 ms；Started=True ExitCode=0 TimedOut=False Success=False
[NEW] Failure=进程已退出但输出流在 3000 ms 内没有关闭（可能有子进程继承了 stdout/stderr 管道），按失败处理。
NEW_EXIT=0
```

**(b) 旧"整行追加"的 64 KiB 预算实测失效**
```
[OLD-FLOOD] exited=True 捕获字符数=200001（预算 65536） Truncated=False
[OLD-FLOOD] 结论：整行追加使收集器超出预算 134465 个字符，且 Truncated 标记=False
```
（新模式在同一输入下捕获**恰好 65536** 且 `Truncated=true` —— 见 5.2 的用例断言。）

**(c) 源码级回归抓到两个真实遗留旁路** —— 见 §4 末尾（首次运行即失败，随后按 F1 修复）。

---

## 6. 诚实边界 / 未验证项

| 项 | 状态 | 说明 |
|---|---|---|
| Unity Editor `assemblyCompilationFinished` 的实际触发顺序（域重载前） | PENDING_USER | 本轮未启动 Unity；只在源码级与 API 编译面验证 |
| Player `IPostBuildPlayerScriptDLLs` 在 IL2CPP/Mono 转换**之前**触发、`BuildFailedException` 能否让构建确实失败 | PENDING_USER | 契约 §5 T-W4 口径不变 |
| **放弃排空时 `StreamReader.Dispose()` 在 Mono/Unity 2019.4 上的具体行为** | 未验证（已按最坏情况设计） | .NET 8 实测：Dispose 放在独立线程后调用方 4134 ms 有界返回。Mono 上若 Dispose 长时间等待，也只影响那个后台线程，**不再**阻塞调用方（F10） |
| 并发拒绝/超时终止在真实编辑器进程里的表现（是否命中用户进程） | 设计+纯 BCL 实测 | 只用 `/PID <own pid> /T /F`；并发第二次调用显式拒绝（有用例） |
| 真实 Play/Build 拦阻、Unity 自动重编译后的自动编织 | PENDING_USER | 需要实机；本轮不启动 Unity/不抢锁 |
| 生产 weave / E2E / R3..R6 回归 | 不在本轮范围 | 本轮只改 Editor 便利层与其门禁；生产路径未被触碰 |
| 指纹算法 | 非密码学 | FNV-1a 64 用于"是否重建"的判定，不用于安全边界 |
| `%TEMP%` 临时脚手架 | 不写入仓库 | §5.3 的三个对照工程只在 `%TEMP%/pmnet-*` 下，属验证步骤，不是交付物 |

---

## 7. 复现命令

```bash
# 1) 真实 Unity2019 API 编译门禁（隔离输出，不复用旧产物）
dotnet build Tools/PMNetWeavingEditorCheck/PMNetWeavingEditorCheck.csproj -c Release \
  -o "$TEMP/pmnet-editor-check-$(date +%s)"

# 2) 纯 BCL 回归（含受控子进程行为）
dotnet build Tools/PMNetWeavingEditorTest/PMNetWeavingEditorTest.csproj -c Release
dotnet Tools/PMNetWeavingEditorTest/bin/Release/net8.0/PMNetWeavingEditorTest.dll --repo D:/UGit/hyld-master
# 期望：通过 25 / 失败 0，退出码 0
```

---

## 8. 交给主侧的发现（不在本组写边界内，只报告）

1. **`Saved/CLI-Delegation` 的 checkpoint 阶段描述**：上一轮 worker 的 checkpoint 记的是
   "当前阶段：running / 是否已执行工具：否"，但收尾原因写的是 `Pi RPC process error`。
   对"未执行"的判定，靠的是`台账里没有工具执行记录`+**核盘**，不是阶段字段本身；
   后续若再有同类崩溃，建议把"已执行工具数"与"阶段"分开看（本轮据此确认是续做而非重跑）。
2. **工具产物的指纹旁车文件**：`Tools/PMNetWeaver/editor-tool/PMNetWeaver.dll.pmnet-inputs` 与
   `Tools/PMNetGen/editor-tool/PMNetGen.dll.pmnet-inputs` 会在**首次在编辑器里跑工具后**出现。
   它们是"上一次构建对应的输入内容指纹"，属构建产物（`editor-tool/` 本来就非源码目录）；
   若主侧希望它不进任何同步/Git，请确认 `editor-tool/` 的忽略规则已覆盖（本轮无 git 写操作，未验证忽略规则）。
3. **首次使用代价（与 P1 报告一致，未变）**：`editor-tool` 目录不存在/指纹缺失时，工具构建发生在
   调用它的那一刻（编译回调 / 门禁 / preflight 内部），首次 `restore` 可能数十秒并阻塞当次回调；
   这是"独立输出 + 自动构建"的代价，且是一次性的（之后按内容指纹复用）。
4. **`WeaveState.SkippedNoContract`** 现在不再被任何路径写入（枚举成员保留，仅为让持久化的
   旧 `SessionState` 整数值不改变语义）。如果主侧希望彻底清理，这属于一个纯改名操作，风险为零。
5. **E2E 探针语义失效**（`_rpc_weaver_review_fix.md` §7 第 1 条）仍未修：本轮边界明确排除 `Tools/PMNetE2E`，
   该失败项的主侧修法（faulty 分支改调 `PMNet_RpcBody_Fire`，或把对象做成 Local callspace）保持原样。

---

## 9. 安全边界遵守情况

未启动 Unity / DS / Lobby / Server，未执行生产 weave 或任何生成器写盘（只有构建产物），
未改 `Client/Library`、任何资产/Prefab/场景/`.meta`（除新增 policy 的 `.meta`）、
未改 `Tools/PMNetWeaver` / `Tools/PMNetGen` / `Directory.Build.targets` / `Tools/PMNetE2E` /
任何生产 `Generated/` 或业务源码；未 git add/commit/reset、未 SVN 操作；未递归委派；
未创建委派列表之外的文件（`bin`/`obj` 与 `%TEMP%` 脚手架为验证产物）。
