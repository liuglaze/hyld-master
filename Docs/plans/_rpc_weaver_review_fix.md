# P3：对真实 IL 编织器的独立对抗核查与窄修（`_rpc_weaver_review_fix.md`）

> 契约：`Docs/plans/net-rpc-weaving-contract.md`（唯一冻结接口）
> 状态与验收仍只在 `Docs/plans/net-architecture-migration.md` 登记（T-W2 / T-W3 / T-W5 的工具侧）。
> P1 证据：`_rpc_weaver_proof.md`；P2 生成器：`_rpc_weaving_generator.md`；P2 迁移/接线：`_rpc_weaving_migration.md`（其中 §3 是本次修复的 BLOCKER）。
> 本次写入边界（**只**动这些）：`Tools/PMNetWeaver/{RpcAssemblyWeaver.cs}`、
> `Tools/PMNetWeaverTest/{Program.cs, HardeningTests.cs（新增）, PMNetWeaverTest.csproj}`、本文件。
> `Tools/PMNetWeaver/{Program.cs, ILBodyCloner.cs}` 在边界内但**一行未改**（本次核查未发现需要改它们的缺陷）。
> 未动：生产 `Client/Assets/Scripts/PMR3/Generated/**`、生成器 `Tools/PMNetGen/**`、`Directory.Build.targets`、
> `Tools/PMNetE2E/**` 源码、Editor 便利层、ID 锁、任何 `bin/` 下的 `Server.dll`。

---

## 0. 一句话结论

对编织器做了**带真实变体**的对抗核查，确认并修好了 **8 个真实缺陷**（其中 1 个是 P2 已报的 BLOCKER、
其余 7 个是本次新发现/新证实的“看着绿、实际没守住”的形态），并把这些形态**逐条做成可复现反例**留在门禁里：

- P2 的 BLOCKER（PDB 核对按「类型名.方法名」做键 ⇒ 合法重载误拒）已修：**真实 PMNetE2E 与 Server 两个工程现在都能真正编织通过**
  （P2 记录的“18 个参与工程一个都织不了”已解除）。
- 另外 7 条里，有 **5 条** 我先用 **P1 时代的旧二进制**跑出「exit 0 / 实际改写=是 / --check 通过」的假绿，再用新实现跑出明确失败，
  证据见 §5；**不是推理，是复现**。
- 门禁：`Tools/PMNetWeaverTest` **314 通过 / 0 失败（exit 0）**（本次改动前基线 192/0）；
  `Tools/PMNetE2E` 真实 build **0 错误**并成功编织，真实 run **208 通过 / 1 失败**（那 1 条已定位为 E2E 自身
  §5② 故障注入探针在新写法下**语义失效**，见 §7 —— 不在本组写边界内，只报告不修）。

**不声称的东西**（见 §6）：不声称“两文件原子事务绝对保证”（进程崩溃/断电不在 `try/catch` 能力内）、
不声称 Unity Editor/Player 回调与 IL2CPP 实机、不声称全部门禁已回归（本次只跑 `PMNetWeaverTest` + 一个 `PMNetE2E`
+ 只读核查 `Server` 的编织步）。

---

## 1. 已读文档（按委派指定顺序）与它们如何决定首搜入口

| 顺序 | 文档 | 它决定了什么 |
|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 仓库入口与必读路由（客户端/服务端文档、主计划） |
| 2 | `Client/Assets/AGENTS.md` | §6 生成/编码约定（声明入口 `PMNetGen --decl-gen … --id-lock`，含中文源码 BOM+CRLF）、§7 门禁口径 |
| 3 | `Server/AGENTS.md` | 「PMNet 框架源码链接自 Client/Assets」「ProtocolHash=0xE6130FAA」⇒ 编织器的两个真实消费者是 Server 与 Unity 形态，本轮只读核查它们的编织步 |
| 4 | `net-architecture-migration.md` 末尾「RPC 自然 C# 接口／IL 编织」段 + `T-W1..T-W6` 表 | 冻结 P1/P2/P3 分工；T-W2「漏编织/损坏/幂等/符号」与 T-W5「增量构建」是本次核查的靶心 |
| 5 | `net-rpc-weaving-contract.md`（完整） | §2 冻结接口（helper 名、`PMNet_GetRpcWeaveVersion`/`PMNet_RequireRpcWeave`、实例 gate、`PMNet_BuildEntry` 首句 Require、原子替换与“不静默丢符号”）、§3 明确拒绝清单、§5 验收口径 |
| 6 | `_rpc_weaver_proof.md` §2/§5/§6 | §5.1 Cecil 0.11.6 换非空 SP 集合会写坏 blob（⇒ 独立读取器必须留在写盘路径上）、§5.2 `ReplaceFile` 文件身份陷阱、§6 未验证项 |
| 7 | `_rpc_weaving_generator.md` §0/§2 | 生成物的**逐字形态**（版本方法只写常量返回、guard 抛 `System.InvalidOperationException`、`#pragma warning disable 0414` + 实例 readonly gate、BuildEntry 首句 Require）——新校验都按这个形态写死并明确拒绝其它形态 |
| 8 | `_rpc_weaving_migration.md` §3（完整） | **BLOCKER 原始记录**：`VerifyPdbIntegrity` 用「声明类型名.方法名」当键 ⇒ 20 处合法重载被误判“PDB 损坏”；并给出复现方式与“建议补同程序集重载夹具” |
| 9 | `windows-shell-compat` skill | 工具解释器是 Git Bash：只用 bash/`dotnet`/`python`，未混用 PowerShell 语法与 cmd 变量 |

实码只读参考：`Tools/PMNetWeaver/RpcAssemblyWeaver.cs`（旧实现全文）、`Tools/PMNetE2E/{Program.cs,E2eFixtures.cs}`、
`Client/Assets/Scripts/PMNet/PMRpc.cs`（`EvaluateCallspace` 真值表）、真实生成物
`Client/Assets/Scripts/PMR3/Generated/PMNet.PMNet.R3.PMR3Player.g.cs`（13 条 RPC 的真实形态）。

> 说明：`Directory.Build.targets`（P2 新增的构建接线）本次**只读**，未改。它把编织钩在 `@(IntermediateAssembly)`，
> 因此本次核查的 `Server` 构建只写了 `Server/obj/**`，`Server/bin/Release/net8.0/Server.dll` 的 mtime 与哈希不变。

---

## 2. 核查与修复方法（为什么不是“看代码猜”）

1. **先固化“真实形态”**：用 Mono.Cecil 写了一个一次性 IL dumper（在 `%TEMP%`，不入库），把 Debug/Release
   真实产物里 `PMNet_GetRpcWeaveVersion` / `PMNet_RequireRpcWeave` 的 IL **逐条打印**出来，得到两种配置的精确形态
   （Debug: `nop; ldc.i4.0; stloc.0; br.s; ldloc.0; ret`；Release: `ldc.i4.0; ret`；
   guard Debug: `call; ldc.i4.1; ceq; ldc.i4.0; ceq; stloc; ldloc; brfalse.s; …; newobj IOE; throw; …`；
   guard Release: `call; ldc.i4.1; beq.s; …`）。新校验只白名单这些**真实出现过**的指令，其它一律明确失败。
2. **每个怀疑都用变体跑出前后对照**：把注入做在 `%TEMP%` 沙箱副本的生成物文本上，用**真实 `dotnet build`**
   （netstandard2.0 / C# 7.3 / 带 portable PDB）编出 DLL，再分别用 **P1 旧二进制**（开跑前先另存一份）
   与**新二进制**过一遍，比较退出码/是否实际改写/`--check` 结论。
3. **独立读取器优先**：PDB 相关结论一律用 `System.Reflection.Metadata`（不引 Mono.Cecil）读回；
   门禁里还用它复现“旧核对键”的碰撞面并断言 `> 0`（防止用例空跑）。

---

## 3. 确认并修好的缺陷（逐条：现象 → 成因 → 修法）

### F1（P2 已报的 BLOCKER）PDB 核对按「声明类型名.方法名」做键 ⇒ 合法重载被误判“PDB 损坏”

- **现象（本次先复现）**：把 `Tools/PMNetE2E/obj/Release/net8.0/PMNetE2E.{dll,pdb}` 拷到 `%TEMP%` 直接调 CLI：
  ```
  [PMNetWeaver] 失败：写出后的 PDB 里同名方法 PMNet.PMNetworkObjectAttribute..ctor 的 sequence point 条数不一致（2 vs 3）：无法完成完整性核对。
  → exit 1，DLL 未被改写
  ```
- **成因**：`VerifyPdbIntegrity` 先建 `rid → "类型名.方法名"`，再用**名字**当字典键 `observed[name]`；
  同一类型里的合法重载（两个 `.ctor`、同名不同签名）键相同、SP 条数不同 ⇒ 被当成损坏。
- **修法**：改为**精确 RID 映射**：先用独立读取器把写出的 DLL 读成
  `(rid, 声明类型简单名, 方法名, 参数个数)`，再用探针 `(方法名 + 参数个数 [+ 类型简单名])` **唯一定位 RID**
  （命中 0 个 / >1 个都明确失败，不靠名字后缀猜），最后按 RID 取该方法的 SP 条数并核对期望值；
  另外新增“PDB 有 DLL 里不存在的方法行 ⇒ DLL/PDB 不对应 ⇒ 失败”。
- **证据**：同一份 E2E 程序集新实现 `--weave` **exit 0 / 实际改写=是 / PDB 独立校验通过**，`--check` 通过；
  `Server`（真实 PMR3，13 RPC，运行时与业务同程序集）同样 `--weave` exit 0 且 `--check` 通过。

### F2 `ReadWeaveVersion` 只数「1 个 ret / 无调用 / 1 个整型常量」⇒ 可能接受**实际返回不同值**的版本方法（假绿）

- **现象（用真实变体证伪）**：把版本方法改成 `int v = 1; return -v;`（IL 恰好 1 个整型常量、1 个 ret、无调用，
  满足旧实现的三条不变式，**实际返回 -1**）。对**已编织**的程序集只改这一个方法体后：
  - 旧二进制 `--check` → **exit 0**（报“全部 RPC 类 stamp=1 …”，判为已编织）；
  - 新二进制 `--check` → **exit 1**：`… 返回未知版本 -1：只支持 0（未编织）/ 1（已编织）。`
  - 而运行期该程序集自己就会抛：`PMNet_RequireRpcWeave()` 读到 -1 ≠ 1 ⇒ 实例字段初始化抛异常（`new` 被自己的 guard 拒掉）。
    即“工具说 OK、程序集不可用”。
- **修法**：新增**有界抽象求值器**（`SimulateBody`）：只白名单实际出现过的指令（`nop` / `ldc.i4*` / `ldloc`·`stloc` /
  无条件与条件分支 / `ceq`·`cgt`·`clt` / `neg`·`not`·四则与位运算 / `call <版本方法>` / `ldstr` / `newobj` / `throw` / `ret`），
  真的把返回值**算出来**；不认识的指令（如 `newarr`）直接
  `… 含求值器不支持的指令 newarr：拒绝猜测（只支持冻结格式实际产出的指令形态）`。

### F3 `ValidateClassGuard` 只证明“调了版本方法”⇒ 假 guard（读版本后恒 `return 1`）被放行

- **现象（旧二进制复现）**：把 guard 改成
  ```csharp
  PMNet_GetRpcWeaveVersion();   // 读了版本但忽略结果
  if (false) { throw ...; }
  return 1;
  ```
  旧二进制 `--weave` → **exit 0，是否实际改写=是**：一个“未编织也永不拒绝”的程序集被正常交付。
- **修法**：对 guard 做两条**语义**断言（同一求值器，把版本调用当成参数化的未知整型）：
  · 版本 == 1 ⇒ 返回整型 1（否则已编织程序集会被自己的 guard 拒绝）；
  · 版本 ∈ {0, 2, -1} ⇒ **真的抛** `System.InvalidOperationException`（`throw` 的目标必须来自 `newobj` 该类型）。
  新实现失败信息：`… 的 PMNet_RequireRpcWeave() 在版本为 0 时没有抛异常（返回 1）：这是「假 guard」…`；
  把异常类型换成 `System.Exception` 时：`… 构造了 System.Exception（IL_0016）：guard 必须抛 System.InvalidOperationException…`。

### F4 没有验证**实例构造 guard** ⇒ 删掉实例 gate 后“未编织时 `new` 被拒”的一半防线静默消失

- **现象（旧二进制复现）**：把生成物的 `private readonly int PMNet_rpcWeaveGate = PMNet_RequireRpcWeave();`
  改成 `= 1;`（字段还在、`PMNet_BuildEntry` 仍调 guard）⇒ 旧二进制 `--weave` **exit 0，实际改写=是**。
  运行期实测（同一份夹具，反射核对）：`new HardeningAlpha()` **不再抛**，`new HardeningBeta()`（gate 未改）仍抛，
  `RegisterAll()` 仍被拒 —— 正是契约 §2 要求的“正常 new 实例**和**注册均拒绝未编织程序集”缺了一半。
- **修法**：新增 `ValidateInstanceGate`：断言
  · 存在 `private readonly` 实例 `int` 字段 `PMNet_rpcWeaveGate`；
  · **每个**非 `: this(...)` 链式构造函数都调用了 `PMNet_RequireRpcWeave()`（`this` 链只查根构造函数）；
  · 至少一个实例构造函数调用它。
  新实现失败信息：`… 的实例构造函数（.ctor）没有调用 PMNet_RequireRpcWeave()：未编织程序集可以被直接 new（实例 gate 失效）。明确失败。`

### F5 `--check` 完全不看符号 ⇒「已编织但 PDB 是旧的/损坏的」也能报绿

- **现象（关键反例，旧二进制假绿）**：编织一份程序集，然后把**编织前**的 PDB 放回去。
  直接回灌会被 Cecil 的“符号与程序集不匹配”拦住（读写阶段），所以我进一步**把 DLL 的 CodeView GUID 改成编织前
  PDB 的 GUID**（Cecil 只比 GUID，见 §2 实测），使 PDB **读得进、但方法级完全不对应**：
  - 旧二进制 `--check` → **exit 0**（“--check 通过：全部 RPC 类 stamp=1 …”，完全没看符号）；
  - 新二进制 `--check` → **exit 1**：`--check：PDB 与 DLL 必须逐方法对应：方法 ClientNotify 的 sequence point 条数为 3（期望 0）…`
- **修法**：`--check` 在有 PDB 时做同样的 RID 级核对，并额外断言
  · wrapper 在 PDB 里**没有**残留行号（编织器写的是合成转发桩，SP 已被清空 ⇒ 旧 PDB 会立刻暴露）；
  · 每个 `PMNet_RpcBody_<M>` 在 PDB 里都有对应调试信息行；
  · 没有 PDB 时按契约允许（无符号可工作），跳过并明确记录说明。

### F6 写盘路径的暂存 PDB 在“PDB 校验失败”时不会被清理（**仓库里有真实残留**）

- **现象（物证）**：`Tools/PMNetE2E/obj/Release/net8.0/PMNetE2E.dll.pmweave-b79a9e4c.pdb`（mtime 16:29，P2 时代）
  就是这条留下的：旧实现 `staged.Add(tmpPdb)` 写在校验**之后**，而校验在真实 E2E 程序集上恰好会抛 ⇒ 暂存 PDB 残留。
  本次复现时也在 `%TEMP%` 沙箱里再次留下了一份 `PMNetE2E.dll.pmweave-8ffd6dca.pdb`。
- **修法**：写成暂存文件后**立刻**登记进清理列表，再做 PDB 校验；门禁新增
  · 每条失败路径后的「无 `.tmp`/`.bak`/`.pmweave-*`/`.pmweave.lock` 残留」断言；
  · **全工作区**残留扫描；
  · 一条**源码顺序守卫**（`staged.Add(tmpPdb)` 必须出现在 `VerifyPdb(tmpDll, …)` 之前），因为这个失败在正常操作下
    已无外部触发点（校验现在按 RID，不会再误报），用顺序断言防止被改回去。
- 注：仓库里那份旧残留**我没有删**（不在写边界内），它是 F6 的物证；本次所有构建/运行**未新增**任何残留（已扫描确认）。

### F7 `CommitWithRollback` 的四个弱点（并发覆盖备份 / 备份登记太晚 / 回滚失败被吞 / 无提交后核对）

- 旧实现：备份名写死 `<目标>.pmweave.bak`；`backups.Add(...)` 在 `File.Move` **之后**；
  回滚里 `catch {}` 吞掉一切，调用者只看到“已尝试回滚”；提交后不核对内容。
- **修法**：
  · 备份名带**本次调用唯一 stamp**（`<目标>.pmweave-<8位GUID>.bak`），并发 weave 不再互相覆盖/删除对方的备份；
  · 备份**复制后立刻登记**，失败路径由 `finally` 统一清理；
  · 回滚逐文件核对“恢复后的长度 == 备份长度”，失败时**保留备份**并把具体文件写进错误信息
    （`★ 回滚未完成，以下目标可能仍是新内容：…（回滚失败：…；原始内容备份保留在 …）`）；
  · 回滚还覆盖“替换前目标不存在”的分支（必须删掉新文件）；
  · 提交后在**删除备份之前**核对两个目标的长度 == 暂存内容长度（不通过仍可回滚）。
- **证据（真实反例）**：独占**读**打开 PDB（允许 Cecil 读符号、允许备份复制，但不允许 delete/替换）⇒ 第二个文件提交失败：
  ```
  [PMNetWeaver] 失败：原子替换失败：Access to the path is denied.。已从备份回滚全部已替换文件（目标为替换前内容）。
  ```
  断言通过：DLL 与 PDB 哈希都与编织前一致、无残留、`--check` 仍失败（回到“未编织”状态），失败信息给出回滚结论。
- 另新增**进程间独占锁**（锁文件写在目标旁边，`FileShare.None`）：拿不到锁立即失败
  （`另一个 PMNetWeaver 正在处理同一个程序集（锁文件 …）…`），把“并发 weave 交错替换”变成“后到者明确失败”；
  锁文件在 `finally` 释放并删除（别人持有时删除会因共享冲突失败，不会误删别人的锁）。

### F8 提交后没有独立核对（只靠 `File.Move` 不抛就当成功）

并入 F7 的“提交后长度核对（在删备份前）”。门禁里对成功路径也断言“无残留 + 二次 weave 零 diff”。

> 附：本次**未发现**需要动 `ILBodyCloner.cs` 的缺陷。业务体克隆的完整搬移已由既有 47 项 woven 用例 +
> 本次新增的“同程序集重载/同名多类 RPC”变体覆盖（switch/循环/try-finally/委托/数组/字符串/多播/收包不递归全部在真编织产物上跑通）。

---

## 4. 本次改动清单（逐文件）

| 文件 | 改动 |
|---|---|
| `Tools/PMNetWeaver/RpcAssemblyWeaver.cs` | ① RID 级 PDB 核对（`VerifyPdb` + `PdbProbe`/`DllMethodRow`/`ReadDllMethodRows`/`ReadPdbSequencePointCounts`），替换旧的“名字键”实现；② 有界 IL 抽象求值器（`SimulateBody` 等）驱动版本读取与 guard 语义证明；③ `ValidateInstanceGate` + `IsThisChainedConstructor`；④ `--check` 的 `VerifyWovenSymbols`；⑤ 写盘路径暂存 PDB 提前登记 + 唯一备份名 + 备份先登记 + 回滚结论如实上报 + 提交后长度核对；⑥ 进程间独占锁（`AcquireWeaveLock`，`Process` 拆出 `ProcessCore`）；⑦ 删掉不再使用的 `TryReadInt32Constant`；⑧ 头注释与新增摘要同步 |
| `Tools/PMNetWeaverTest/HardeningTests.cs` | **新增**：P3 加固夹具（`HardeningAlpha`/`HardeningBeta` 同名 RPC + 非 RPC 合法重载 `OverloadedMarker`）与 `HardeningDriver`（`RunPreWeave` / `RunWoven` / `RunPreWeaveGateDisabled`）。该文件**不参与** `PMNetWeaverTest` 自身编译，只被 `%TEMP%` 临时夹具工程编译 |
| `Tools/PMNetWeaverTest/Program.cs` | 新增 §12–§15 与 5 条 N 反例（`RunHardeningChains` / `RunHardeningVariant` / `RunHardeningNegativeChains` / `RunSymbolChecks` / `RunAtomicityChecks` / `WriteHardeningVariant` / `InspectPdbNameCollisions` / `FindWeaverLeftovers` / `RunConcurrently` / `ReadPortablePdbId` / `PatchDllCodeViewGuid` 等）；`RunDriver` 增加驱动类型参数（默认仍是原夹具驱动） |
| `Tools/PMNetWeaverTest/PMNetWeaverTest.csproj` | `<Compile Remove="HardeningTests.cs" />`（它与 `Fixture.cs` 一样是夹具源码，不能编进 net8.0 门禁自身） |
| `Docs/plans/_rpc_weaver_review_fix.md` | 本报告 |
| **未改** | `Tools/PMNetWeaver/{Program.cs, ILBodyCloner.cs}`、`Tools/PMNetGen/**`、`Directory.Build.targets`、`Tools/PMNetE2E/**` 源码、`Client/Assets/Scripts/PMR3/Generated/**`、`Docs/plans/pmnet-r3-ids.json`、Editor 便利层 |

编码：上述改动文件均为 **UTF-8 BOM + CRLF**（新增的 `HardeningTests.cs` 写入后已归一为 BOM+CRLF，逐字节核查：bareLF = 0）。

---

## 5. 证据：旧实现 vs 新实现（都是**真实产物 + 真实 CLI**）

| # | 变体（注入位置：`%TEMP%` 沙箱生成物文本 / 手工 IL） | 旧实现（P1 二进制） | 新实现 |
|---|---|---|---|
| F1 | 真实 `Tools/PMNetE2E/obj/…/PMNetE2E.{dll,pdb}`（运行时与业务同程序集，20 处同名重载） | `--weave` **exit 1**：`同名方法 PMNet.PMNetworkObjectAttribute..ctor 的 sequence point 条数不一致（2 vs 3）` | `--weave` **exit 0 / 实际改写=是 / PDB 独立校验通过**；`--check` exit 0 |
| F2 | 已编织产物 + 只把版本方法体改成 `int v = 1; return -v;`（Cecil 手工篡改） | `--check` **exit 0**（假绿） | `--check` **exit 1**：`返回未知版本 -1` |
| F3 | 默认夹具 + 假 guard（`PMNet_GetRpcWeaveVersion(); if (false) throw…; return 1;`） | `--weave` **exit 0 / 实际改写=是** | `--weave` **exit 1**：`在版本为 0 时没有抛异常（返回 1）：这是「假 guard」` |
| F3b | 同上但 guard 抛 `System.Exception` | `--weave` exit 0 | **exit 1**：`构造了 System.Exception（IL_0016）：guard 必须抛 System.InvalidOperationException` |
| F4 | 默认夹具 + `PMNet_rpcWeaveGate = 1;`（实例 gate 失效） | `--weave` **exit 0 / 实际改写=是** | `--weave` **exit 1**：`实例构造函数（.ctor）没有调用 PMNet_RequireRpcWeave()：未编织程序集可以被直接 new（实例 gate 失效）` |
| F5 | 已编织产物 + DLL CodeView GUID 改成**编织前** PDB 的 GUID（PDB 读得进但不对应） | `--check` **exit 0**（假绿） | `--check` **exit 1**：`PDB 与 DLL 必须逐方法对应：方法 ClientNotify 的 sequence point 条数为 3（期望 0）` |
| F6 | 写盘校验失败路径（旧实现必留 `*.pmweave-*.pdb`） | 仓库物证 `Tools/PMNetE2E/obj/Release/net8.0/PMNetE2E.dll.pmweave-b79a9e4c.pdb`；本次复现又多一份 | 每次失败后残留断言 + 全工作区扫描 + 源码顺序守卫 |
| F7 | PDB 被独占读占用（第二个文件提交失败） | 只报“已尝试回滚”，回滚失败被吞 | `原子替换失败：Access to the path is denied.。已从备份回滚全部已替换文件（目标为替换前内容）。` + 两文件哈希与编织前一致 + 无残留 |
| F7b | 锁文件被独占占用 | 无锁概念 | `--weave` 立即失败：`另一个 PMNetWeaver 正在处理同一个程序集（锁文件 …）`；释放后 `--weave` 成功 |
| F7c | 两个真实进程同时 weave 同一程序集 | 可能交错替换/互删备份 | 两进程退出码 ∈ {0,1}、无“未预期异常”、无残留、文件自洽（要么都未变、要么 `--check` 通过） |

其它关键实测数字（本次门禁运行）：

```
hardening-sep-Debug     旧核对键碰撞面：方法行 66，不同名键 63，同名不同 SP 条数的键 1
hardening-same-Debug    旧核对键碰撞面：方法行 936，不同名键 906，同名不同 SP 条数的键 21
  例：PMNet.PMNetworkObjectAttribute..ctor (3 vs 4) / PMNet.PMReplicatedAttribute..ctor (5 vs 6)
      PMNet.PMNetRegistry.TryGetRpc (3 vs 9) / PMNet.PMStableHash.Fnv1a32 (20 vs 17) / PMNet.PMNetReader..ctor (3 vs 15)
hardening-same-Release  旧核对键碰撞面：方法行 935，不同名键 905，同名不同 SP 条数的键 21
```

> 这条断言（碰撞数 > 0）是**防空跑**的关键：如果夹具不再包含同名重载，用例会直接变红，而不是继续“通过”。

---

## 6. 门禁与真实构建/运行结果

### 6.1 `Tools/PMNetWeaverTest`（本组自身门禁）

```
dotnet build Tools/PMNetWeaverTest/PMNetWeaverTest.csproj -c Release -o <独立目录>   # 0 警告 / 0 错误
dotnet <独立目录>/PMNetWeaverTest.dll --repo D:/UGit/hyld-master
→ 通过 314 项，失败 0 项（exit 0）；PMNetWeaver CLI 调用 57 次；夹具真实编译 22 次
```

本次改动前基线（同一命令、改动前源码）：**192 / 0**。新增 122 项包含：

- `§12` 加固夹具真实生成（2 个含 RPC 的类 + 注册表）；
- `§13/§14` 三个加固变体：**运行时与 RPC 同程序集**（Debug + Release）与**运行时独立程序集**（Debug）——
  真编译（带 PDB）→ `--weave`（含 `--require-rpcs`）→ `--check` → `AssemblyLoadContext` 加载后跑
  `RunPreWeave` / `RunWoven`：版本门、body 私有、**两类的同名 RPC 各自落到自己的业务体**（`alpha.Hits=6 / beta.Hits=13`）、
  非 RPC 重载行为不变、注册表 `ClassCount=2 / RpcCount=4`、稳定 ID 编织前后逐项一致、二次 weave 幂等；
- `N` 反例：假 guard、guard 抛错类型、版本方法实际返回≠常量、版本方法含未支持 IL、实例 gate 失效
  （含**运行期**证明“new 不再被拒”）、PDB 不可替换（回滚）、锁冲突、两进程并发、无残留扫描、暂存登记顺序守卫；
- `§15` `--check` 符号核对：无 PDB 必须通过 / 回灌旧 PDB 必须失败 / **GUID 篡改使 Cecil 接受**的旧 PDB 必须失败
  （新增层命中）/ 截断 PDB 必须失败。

同时，原有 192 项**一条未减**（P1/P2 的检测力保留）。

### 6.2 一个真实 `PMNetE2E`（build + run）

```
dotnet build Tools/PMNetE2E/PMNetE2E.csproj -c Release --nologo
  → PMNetWeaver --weave：RPC 方法数 6、是否实际改写：是、PDB 独立校验通过（6 个克隆业务体）
  → PMNetWeaver --check：符号核对通过（12 个方法）；已成功生成，0 警告 0 错误
dotnet Tools/PMNetE2E/bin/Release/net8.0/PMNetE2E.dll
  → 通过 208 项，失败 1 项（exit 1）
```

那 1 条失败的定位见 §7（E2E 自身探针语义问题，非编织器缺陷）。
E2E 的生成目标按设计重写了 `Tools/PMNetE2E/Generated/*.g.cs`（**只产生该项目构建/Generated 产物，未改其源码**）；
`e2e-ids.json` 未被改动（ID 无漂移）。

### 6.3 只读核查：生产路径（`Server`，真实 PMR3）

```
dotnet build Server/Server.csproj -c Release -o <独立目录>
  → PMNetWeaver --weave：RPC 方法数 13、含 RPC 的类数 1（PMNet.R3.PMR3Player）、是否实际改写：是、PDB 独立校验通过（13 个）
  → PMNetWeaver --check：符号核对通过（26 个方法）；已成功生成，0 错误（8 个 NU1701 包警告为既有）
```
副作用核查：`Server/obj` 无任何 `.pmweave*` 残留；`Server/bin/Release/net8.0/Server.dll` 未被触碰（mtime 保持 9-21）。

---

## 7. 交给主侧的发现（不在本组写边界内，只报告）

1. **E2E §5②「Reject 后仍执行了实现」探针在新写法下语义失效**（本次 E2E run 唯一失败）。
   - 该注入的“缺陷形态”实现是 `InvokeFire(..., validating:false)`，它直接 `target.Fire(p0, p1)`，注释写明
     “读参数后**直接调实现**”。在**编织前**的写法里 `Fire` 就是业务实现，这句是成立的；
     但迁移+编织之后 `Fire` 是**网络入口**（wrapper → `PMNet_Fire` → callspace 判定）。
   - 实测（反射加载编织后的真实 E2E 程序集）：
     ```
     E2eReplicated: NetMode=Standalone, Role=None
     调用 Fire(-1, 1.0f) 之后 FireCount = 0        ← callspace #7：Role < Authority 且 NetServer ⇒ Absorbed，本地不执行
     PMNet_RpcBody_Fire 存在 = True
     直接调私有业务体之后 FireCount = 1            ← “直接调实现”只有这条路径能到达
     ```
   - 因此缺陷形态**没有真的执行实现**，探针 F4 不失败 ⇒ `CheckInjection` 的“命中”断言失败。
     **产品行为没有被削弱**：同一节的干净探针 F1/F3/F5/F7/F9 全部通过（“注入前失败 0 项”），
     `PMNetWeaverTest` 的 `force-reject-skip`（Reject 后跳过实现、不断连）也在真编织产物上通过。
   - 建议修法（属 `Tools/PMNetE2E/Program.cs`，本组未改）：`InvokeFire` 的 faulty 分支改为调用私有业务体
     （`typeof(E2eReplicated).GetMethod("PMNet_RpcBody_Fire", NonPublic|Instance)`），或把对象做成 Local callspace
     （`PMNetWeaverTest` 的夹具手法：spawn 到服务端世界）。
2. **仓库里仍有一份旧残留**：`Tools/PMNetE2E/obj/Release/net8.0/PMNetE2E.dll.pmweave-b79a9e4c.pdb`（P2 时代，
   是 F6 的物证）。本组未删（不在写边界内）；可随时删除，不影响任何构建（下次 weave 也不会用它）。
3. **`VerifyPdb` 里“同名同参数个数”不唯一时会明确失败**：极端情况下（同一个类里两个同名同参数个数的
   `PMNet_RpcBody_<M>`）会拒绝而不是猜 —— 这与契约“RPC 不允许同名重载”一致，但请在生成器侧保持该前提。
4. 仍未验证项（与 P1/P2 口径一致，本组不扩大声明）：Unity Editor `assemblyCompilationFinished` /
   Player `IPostBuildPlayerScriptDLLs` 接线与实机、IL2CPP/Mono 运行、MSBuild 增量矩阵的**全量**回归
   （本次只跑了 `PMNetWeaverTest` + `PMNetE2E` + `Server` 的编织步）。

---

## 8. 诚实口径：这些修好**没有**做到什么
1. **进程崩溃/断电下的两文件原子性仍然做不到**。本实现是“暂存 → 备份 → `File.Move` 重命名 → 失败回滚”，
   加上进程间锁；它能覆盖的是“**本工具自己的**并发与可捕获的 I/O 失败”。
   如果进程在“DLL 已换、PDB 未换”之间被强杀（或机器掉电），磁盘上就会留下一版不匹配的 DLL/PDB 对。
   要做到“绝对原子”需要文件系统事务/日志（或把 PDB 内嵌进 DLL），**不在本工具的设计与本次改动范围内**。
   本次只保证：任何**被捕获**的失败都会回滚、会被如实报告，且不会留下暂存/备份垃圾。
2. **锁只对“本工具的并发”有效**：它靠目标的锁文件，不能阻止别的程序（编辑器、杀软、其它构建）在提交瞬间动同一批文件。
3. **IL 求值器是白名单而非通用解释器**：版本方法/guard 只要用了白名单外的指令就会**明确失败**（而不是被“猜”过去）。
   这是有意的取舍：宁可对未知形态报错，也不放行。
4. **`--check` 的符号核对只在“有 PDB”时生效**：无 PDB 构建按契约允许（会记录说明），不算绿也不算红。
5. **PDB 的“内容级”损坏仍可能超出检测能力**：新核对能发现“DLL/PDB 方法行不对应、wrapper 残留行号、
   克隆业务体缺行、SP/局部变量不可解析”，但它不是“逐字节重算 PDB”的校验器。
6. **本组不声称**任何 Unity 侧、IL2CPP、真实双端联机、全部门禁回归的结论。

7. **对 Editor 便利层的兼容性影响（需其负责组知情）**：本轮**未改**任何 CLI 开关、退出码语义与格式名，
   `Client/Assets/Editor/PMNetWeaving/**` 只用 `--weave` / `--check` + 退出码（已只读核对），因此接线仍然成立；
   但有两处**行为收紧**需要知情：
   · `--check` 现在会核对符号（见 F5）：如果某处对**不是本工具织出来的**程序集（其 portable PDB 在 RPC wrapper 上
     留着行号、或没有克隆业务体行）跑 `--check`，现在会明确失败而不是报绿 —— 这正是本次加固的目的；
   · `--weave` 现在会在目标旁边创建/删除一个锁文件（`<目标>.pmweave.lock`）并拒绝“另一个 weave 正在处理同一程序集”；
     这与它本来就需要的“目标目录可写 + 会写暂存文件”是同一前提，但若某调用方**并发**触发两次 weave，后到的那个会被拒（而非交错写入）。

---

## 9. 复现命令（全部只写 `%TEMP%` 或构建产物目录）

```bash
# 1) 工具 + 门禁（独立输出目录，避免与并行任务串输出）
dotnet build Tools/PMNetWeaver/PMNetWeaver.csproj -c Release                       # 0 警告 / 0 错误
dotnet build Tools/PMNetWeaverTest/PMNetWeaverTest.csproj -c Release -o %TEMP%/pmw-out
dotnet %TEMP%/pmw-out/PMNetWeaverTest.dll --repo D:/UGit/hyld-master                # 314 / 0（exit 0）

# 2) P2 BLOCKER 的最快复现（拷副本，不动仓库 obj）
cp Tools/PMNetE2E/obj/Release/net8.0/PMNetE2E.{dll,pdb} %TEMP%/repro/
dotnet Tools/PMNetWeaver/bin/Release/net8.0/PMNetWeaver.dll --weave %TEMP%/repro/PMNetE2E.dll --require-rpcs

# 3) 真实 PMNetE2E（build 会按设计重生成它自己的 Generated/）
dotnet build Tools/PMNetE2E/PMNetE2E.csproj -c Release
dotnet Tools/PMNetE2E/bin/Release/net8.0/PMNetE2E.dll                               # 208 / 1（失败项见 §7）

# 4) 只读核查生产路径（独立输出，不动 Server/bin）
dotnet build Server/Server.csproj -c Release -o %TEMP%/pmw-server
```

约束遵守：未启动 Unity / DS / Lobby / Server，未写 `Client/Assets/**`、未碰 Unity `Library`，
未执行任何 git/svn 写操作（`git status/log/show` 仅只读），未递归委派。
