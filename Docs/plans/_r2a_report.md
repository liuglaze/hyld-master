# R2-A 实现报告：声明扫描器 + 代码发射器 + 声明门禁

**任务**：在 `D:/UGit/hyld-master` 实现 R2 契约（`Docs/plans/net-r2-codegen-contract.md`）的
§2（稳定 ID）、§4（生成物 API 面）、§5（12 条声明报错规则）、§6（支持类型集）。
**范围**：只做「声明扫描 + 代码发射 + 声明门禁」；不做复制层、不改 `Client/Assets/Scripts/PMNet/**`、
不改 `Tools/PMDeclModel/**`、不为 proto 生成器加功能、不引入除 `Microsoft.CodeAnalysis.CSharp` 之外的包。

## 0. 范围说明（三处需要主 Agent 知晓的事实）

1. **硬写入边界里的绝对路径与任务正文不一致**。任务正文写的是
   `D:/UGit/hyld-master`，而「comprehensive 硬写入边界」列的是
   `D:\UE_Project\ProjectMecury\Tools\PMNetGen\...` 等路径。已核实
   `D:\UE_Project\ProjectMecury\Tools` 下**不存在** `PMNetGen` / `PMDeclModel`（那是 UE 工程，
   有 `MoverTools`、`DataTableTools` 等），而 `D:/UGit/hyld-master/Tools` 下
   `PMNetGen` / `PMDeclModel` / `PMNetLangCheck` 等齐备 —— 即边界块里的 `ProjectMecury`
   前缀是模板残留，真实目标是 `hyld-master`。**本轮按任务正文在 `hyld-master` 实现，
   未创建/修改 `D:\UE_Project\ProjectMecury` 下的任何文件**（只读参考了该仓库的
   `.agents/skills/forcevalidate-implementation/SKILL.md`，用于对齐规则 8 的反外挂口径）。
2. **未执行任何 git 操作**（无 `add`/`commit`/`reset`/`checkout`）。当前工作副本里
   `Tools/` 整体处于未跟踪状态（`git ls-files Tools | wc -l` = 0），这是**既有状态**，
   不是本轮造成的；提交边界留给用户。
3. **同窗口存在并行改动，不要把它们算到本轮头上**。按文件修改时间看，同一时段内
   `Client/Assets/Scripts/PMNet/**`（新增 `Replication/`、`Transport/` 等）、`Tools/PMDeclModel/*`、
   `Tools/PMNetWorldTest` / `PMReplicationTest` / `PMTransportTest` / `PMCallspaceCheck`、
   `Docs/plans/net-*.md` 与 `Docs/plans/_r2b_report.md` 也被改动了 —— 那些是其它委派项（B/C）的工作。
   本轮**只写**了 §7 清单里列出的 11 个文件。
   附带的一个正向信号：B 项新增的 `PMNet/Replication/**` 与 `Transport/**` 已在我最后一次
   门禁编译里与生成物一起通过（§9 的 `C# 7.3 + netstandard2.0` 零错误），说明生成物与并行演进的
   运行时仍然兼容；若后续运行时接口再变，该门禁会第一时间报红。

---

## 1. 已检查范围

### 1.1 按任务要求完整读过的文档（顺序即为实际阅读顺序）

| # | 文档 | 读到的内容 |
|---|---|---|
| 1 | `AGENTS.md`（仓库入口，5 行） | 指向下面 3 份文档的位置 |
| 2 | `Client/Assets/AGENTS.md`（622 行） | 客户端形态（Unity 2019.4 同一二进制靠 `-server` 判身份）、战斗主链路、§11「代码变动后必须检查文档」、§12「唯一权威 proto 源 + 生成约定（`build.bat`）**」 |
| 3 | `Server/AGENTS.md`（428 行） | 服务端形态、§12 协议来源与生成约定、`net8.0` 目标框架、日志用 `Logging.Debug.Log`（不是 `UE_LOG`） |
| 4 | `Docs/plans/net-architecture-migration.md` §3.9 全节（含 §3.9.1 A3/A9、§3.9.3 M02、§3.9.4、§3.9.5、§3.9.6）与 §5 的 R2 行、§5.1/§5.2 | **A3** 明确「`Tools/PMNetGen/Program.cs` 只读 proto，未建立 C# 标记→业务网络桩链 ⇒ 要扩展外部生成器扫描业务声明」；**A9** 明确「Attribute 不拦截普通方法调用 ⇒ 用标记字段生成访问器、`_Implementation` 生成调用入口」；§3.9.5 给出「外部工具可在 net8.0 用 Roslyn 扫描，产物必须 C# 7.3/.NET Standard 2.0」以及「缺生成入口/重复 ID/非法签名/未知类型必须明确失败」 |
| 5 | `Docs/plans/net-r0-contract.md` §2.4（D-R0-12..18）、§2.7（D-R0-41..46）、§2.8（D-R0-47..50）、§3.4、§3.5、§5 | **D-R0-49**「稳定 ID，禁止按声明顺序临时编号」；**D-R0-48**「生成产物必须 C# 7.3 + .NET Standard 2.0」；**D-R0-50**「声明错误编译期失败」；**D-R0-14** 8 项条件与 UE 原值；**D-R0-45** `WithValidation` 与 `ForceValidate` 互斥；**D-R0-46** 三种协议摘要口径 |
| 6 | `Docs/plans/net-r2-codegen-contract.md`（228 行，**逐节读完**） | §1 冻结接口清单、§2.1 稳定键、§2.2 分配算法、**§2.3 三条不变性**、§3 协议摘要、**§4 生成物 API 面**、**§5 12 条规则**、**§6 类型集**、§7 委派边界（A 项写路径）、§8 待收敛参数 |

### 1.2 为落地而额外读的源码（冻结接口，只读）

- `Tools/PMDeclModel/PMDeclModel.cs`（169 行）、`Tools/PMDeclModel/PMIdLock.cs`（625 行）——
  冻结 IR 与锁文件语义（`AllocateClass/AllocateMember`、`MarkRetired`、`Serialize/Deserialize`）。
- `Client/Assets/Scripts/PMNet/Declarations/PMNetDeclarations.cs`（属性/描述符/注册表）、
  `Declarations/PMStableHash.cs`（**唯一哈希实现**）、`PMWireType.cs`（`PMCond` 8 项）、
  `PMRpc.cs`（`PMRpcKind/Reliability/Validator`、`PMRpcDispatch`、`PMServerRpcAttribute` 等）、
  `PMNetObject.cs`（`PMRepList`/`MarkPropertyDirty`/`CollectLifetimeReplicatedProps`/`GetNetConnection`）、
  `PMNetProperty.cs`（`PMLifetimeProperty`/`PMDirtyTracker`）、`PMNetWriter.cs`/`PMNetReader.cs`（公开方法名）、
  `World/PMNetOwnerChain.cs`（`PMNetControllerObject`/`PMNetPawnObject`）。
- `Tools/PMNetGen/Program.cs`、`PMNetGen.csproj`（既有三个模式的调用约定与退出码语义）。
- `ProtobufAndNotepad/Protobuf/build.bat`（既有 `--proto/--check/--normalize-eol` 的调用方式）。
- `D:/UE_Project/ProjectMecury/.agents/skills/forcevalidate-implementation/SKILL.md`（只读）：
  确认 `_ForceValidate` 同伴 + 三态 `NetAccept/NetReport/NetReject` + 「Reject 只跳过实现、不断连」，
  与 `PMRpcValidator.ForceValidate` 的注释完全一致 ⇒ 规则 8 的报错文本按这个口径写。

### 1.3 文档如何决定了我的入口与实现方案

| 文档结论 | 对我的直接决定 |
|---|---|
| 迁移计划 A3「`Tools/PMNetGen` 只读 proto，缺标记→桩链」 | 入口就选在 **`Tools/PMNetGen`**（扩展它，而不是另起一个工具目录）：新子命令 `--decl-scan/--decl-gen/--decl-check` 追加在既有 proto 模式之上 |
| R2 §1「IR 已冻结，改动需同步契约」+ §7「A/B 的共同前提不得改动」 | `PMDeclModel`/`PMIdLock`/`PMStableHash`/`PMDescriptor` **一律只读复用**；ID/哈希/条件枚举全部走 `ProjectReference → PMDeclModel`（它 `<Compile Include>` 了运行时 PMNet 核心），**绝不抄第二份实现** |
| R2 §4.2「每程序集一个注册表」+ 任务正文「`--out-dir <目录>`」 | 一次调用 = 一个程序集 = 一个 `PMNetGeneratedRegistry.g.cs`；`--out-dir` 定义为产物目录本身（不隐式追加 `Generated/`），并在 `--help` 里写明 |
| R2 §2.2「锁文件是权威；已存在键一律沿用」+ §2.3「二次生成零 diff」 | ID 分配的唯一来源是 `Docs/plans/pmnet-ids.json`；分配前先 `Serialize()` 快照，用于「已存在键 ID 变更 ⇒ 硬失败」与「退役键只告警」两条 |
| R2 §5 规则 9 判「源码里显式写了什么」 | IR 里只存「生效后的档位」，因此补一份**语法层事实**（`PMDeclRawFacts`）承载规则 9/5/6/8 的判据，而不是改冻结 IR |
| R2 §5 规则 1「能推到 PMNetObject」 | 只读语法树不够（中间基类可能在运行时程序集里），因此生成期用反射枚举 `typeof(PMNetObject).Assembly` 里**已存在的** PMNetObject 派生类型做白名单 —— 这是**生成期**反射，不违反 D-R0-48/T40 的「发货产物运行不扫反射」 |
| R0 §2.8 D-R0-48/49/50 + 迁移计划 §3.9.5 | 产物语言面锁 C# 7.3（不用插值字符串/`?.`以外糖/新 API）；`RegisterAll` 显式列类；声明错误 → 非 0 退出并**拒绝写盘** |
| 仓库既有约定（`AGENTS.md` §12、`build.bat`） | 新增子命令沿用既有退出码语义：`0` 成功 / `1` 参数或声明错误 / `2` 不同步（对齐 `--check`） |

---

## 2. 实现概要（每个文件做了什么）

### 2.1 `Tools/PMNetGen/PMNetGen.csproj`（修改）
- 新增唯一第三方依赖：`<PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.11.0" />`
  （与本机 NuGet 缓存内版本一致，**可完全离线还原**，实测无网络仍成功）。
- 新增 `<ProjectReference Include="..\PMDeclModel\PMDeclModel.csproj" />`：一次性拿到冻结 IR、
  `PMIdLock`、以及**运行时侧**的 `PMStableHash` / `PMCond` / `PMRpcKind` / `PMDescriptor` 系列 —— 不抄枚举、不抄哈希。
- 保持 `TreatWarningsAsErrors=true`，新增 `NoWarn=CS1591`。

### 2.2 `Tools/PMNetGen/DeclScanner.cs`（新建，约 1100 行）
Roslyn **纯语法级**扫描器（不建 `Compilation`、不引业务程序集）。产出 = 冻结 IR + 语法层补充事实。

- 识别 7 个 Attribute（按**全名字符串**匹配：`PMNet.PMReplicated` / `PMReplicated` / `PMReplicatedAttribute` 归一为 `PMReplicated`）：
  `PMNetworkObject`、`PMReplicated`、`PMRepNotify`、`PMQuantized`、`PMServerRpc`、`PMClientRpc`、`PMNetMulticast`，
  另加 `PMNetworkStruct` 仅用于 §6 的「声明即报错」判定。
- 属性实参解析覆盖：位置实参、`NameEquals`（`Condition = ...`）、`NameColon`、
  **`nameof(X)`**（还原为标识符末段）、`(PMCond)1` 强制转换、字符串字面量。
- 聚合多个 partial 部分；`BaseTypeName` 取「唯一出现的那条基类列表」（避免第二部分覆盖掉第一部分）。
- **ID 分配**：`PMIdLock.AllocateClass/AllocateMember`；分配顺序按**稳定键 Ordinal 升序**（与源文件顺序无关）；
  属性先按成员名排序分配、再按 `PropertyId` 排序落 IR；RPC 同理。
- **掩码区间**：按 `PropertyId` 升序紧凑排布，每属性 1 位；`ChangeMaskBitCount = 属性数`。
- **RepNotify 槽位**：IR 只有 `OnRepMethodName`（无 `OnRepMethodId`），槽位由 `RepNotifySlotOf()` 按
  「PropertyId 升序、只对有 OnRep 的属性从 1 起算」**确定性推导**，生成期与运行期同源。
- **协议摘要**：`ClassProtocolHash = PMStableHash.ClassProtocolHash(ClassId, props)`；
  `GlobalProtocolHash = PMStableHash.GlobalProtocolHash(BuildClassEntries(model))`，
  而 `BuildClassEntries` 必须与生成物 `PMNet_BuildEntry()` 逐字段同形（有门禁断言）。
- **继承链上的复制属性序号基址** `PropertyIndexBase`：沿「本次扫描到的已声明祖先」累加属性数，
  避免派生类从 0 编号时与基类槽位撞车。
- **退役键检测**：`PMIdLock` 没有枚举键的 API，因此复用 `Serialize()` 的输出形状做一次只读键扫描
  （`ExtractLockKeys`），只告警、不回收 ID。
- **刻意不做规则判定**：所有 §5 规则都在 `DeclValidation.cs`；扫描器只额外产生「读取失败」「语法错误数」等告警。

### 2.3 `Tools/PMNetGen/DeclValidation.cs`（新建，约 700 行）
§5 的 **全部 12 条**规则 + §6 类型集。错误格式统一为 `[规则 N] ...`（门禁靠这个前缀统计命中）。

- 规则 1：`partial` + 继承链三态（确定是 / 确定不是 / 跨程序集无法证明 ⇒ 告警放行）；
  另拦「`[PMNetworkObject]` 标在 struct/interface/record 上」。
- 规则 2：泛型类。
- 规则 3：`static` / `readonly` / `const`（`const` 折叠为 `static+readonly`）。
- 规则 4：**§6 类型集**，覆盖整数族（含 `System.*` 全名与别名）、`bool`、`float`/`double`、`string`、
  任意已声明 enum、一维 `T[]`；拒绝泛型集合、多维/交错数组、可空、`char`、
  以及被 `[PMNetworkStruct]` 标记的类型（明确提示「R2 不实现」）。
  按 §6 的「复制与 RPC 参数共用」同时检查 **RPC 参数类型**；另拦「标记在非 `[PMNetworkObject]` 类上」。
- 规则 5/6：`[PMRepNotify]` 的 `ForMember` 必须命中已声明的 `[PMReplicated]` 成员；目标方法无参、返回 `void`；
  反向一致性检查（IR 里的 `OnRepMethodName` 必须真的是该类的 `[PMRepNotify]`）。
- 规则 7：RPC 非 `void` / `static`；另拦「RPC 声明在非网络类里会被静默忽略」。
- 规则 8：**反外挂红线** —— `[PMServerRpc]` 必须 `Validator != None` 或存在 `<方法名>_ForceValidate` 同伴；
  报错文本给出三条合法出路（`ForceValidate` 三态不断连 / `Validate`/`WithValidation` 失败即断连 / 同伴方法）。
- 规则 9：`WithValidation = true` 与 `Validator = ForceValidate` 同时出现 ⇒ 报错（判源码显式写法）。
- 规则 10：同一类内 `PropertyId`/`RpcId` 重复、`ClassId` 跨类重复、**同一成员名重复声明**
  （两个 partial 部分各写一次 ⇒ 稳定键相同 ⇒ ID 相同），以及**生成的访问器名冲突**
  （`_hp` 与 `hp` 会都生成 `PMNet_Set_hp` ⇒ 直接编译失败，必须在声明期拦下）。
- 规则 11：条件必须是 D-R0-14 的 8 项；对「属于明确不做的 9 项」与「无法解析」给出不同提示，不静默降级。
- 规则 12：掩码区间重叠 / 越界 / 总位数与 `ChangeMaskBitCount` 不一致 / `MaskBitCount == 0`。
- 告警（不阻断）：属性无 setter、abstract 或无无参构造（工厂置 null）、
  业务已自行覆写 `CollectLifetimeReplicatedProps`、只写 `_Validate` 同伴、形如 `X_Implementation` 的 RPC 名、
  语法错误计数、以及「0 个文件被扫描」。

### 2.4 `Tools/PMNetGen/DeclEmitter.cs`（新建，约 1100 行）
严格按 §4 发射，确定性输出（无时间戳、无绝对路径、无字典枚举顺序依赖）。

- 每类一个 `PMNet.<命名空间>.<类型名>.g.cs`（命名空间与该类相同；全局命名空间时省略前缀）；
  每程序集一个 `PMNetGeneratedRegistry.g.cs`。写盘统一 **UTF-8 + BOM + CRLF**。
- §4.1 逐项落地：`PMGeneratedClassId`、`PMGeneratedChangeMaskBitCount`、
  （补充）`PMGeneratedPropertyIndexBase`、`CollectLifetimeReplicatedProps` 覆写、
  `PMNet_Set<成员名>`、`PMNet_Write_/Read_<成员名>`、`PMNet_OnRepDispatch`、
  `PMNet_RpcWrite_/PMNet_RpcInvoke_<方法名>`、`internal static PMNetClassEntry PMNet_BuildEntry()`。
- §4.3：额外产出业务可见调用桩 `PMNet_<方法名>(...)`，内部走
  `PMRpcDispatch.EvaluateCallspace(NetMode, Role, kind, GetNetConnection() != null, false)`
  → 本地执行 / 发远端 / 吞掉。
- §4.2：`RegisterAll()` **逐个显式列出每个类**（`entries[i] = global::Ns.Type.PMNet_BuildEntry();`），
  然后 `PMNetRegistry.Seal(PMStableHash.GlobalProtocolHash(entries))` —— 摘要用**唯一的** `PMStableHash` 现算，
  不写死字面量，从根上消除「生成期/运行期摘要不一致」。
- 类型编码（§6）：varint / zigzag（有符号）/ fixed32 / fixed64 / length-delimited UTF-8 / 枚举 varint；
  一维数组 = `zigzag 长度 + 元素`，`-1` 作为 null 哨兵（否则 null 与空数组不可区分），
  读取侧长度上限 `PMGeneratedMaxArrayLength = 4096`，越界抛 `System.FormatException`（不静默截断）。
- 生成的 partial 声明复制源文件的**可访问性修饰符前缀**与 `using`（去重 + Ordinal 排序），
  且 PMNet 运行时类型一律写全限定名（`PMNet.PMNetObject` 等），因此源文件是否 `using PMNet;` 都能编。
- 抽象类 / 无可用无参构造时 `entry.Factory = null`（并告警），不生成必然编译失败的 `new`。

### 2.5 `Tools/PMNetGen/Program.cs`（修改，追加而不破坏）
- 追加三个子命令，参数解析与既有 `--proto/--check/--normalize-eol` **完全并列**：
  `--decl-scan <源路径>...`、`--decl-gen <源路径>... --out-dir <目录> --id-lock <锁文件>`、
  `--decl-check <源路径>... --id-lock <锁文件> --out-dir <目录>`。
- 源路径支持**目录（递归 `*.cs`）与单个 `.cs` 文件**；自动跳过 `*.g.cs` 与 `bin/obj`（避免二次生成把产物当输入）。
- `--decl-gen`：先校验，有错误则打印并 **拒绝写盘**（D-R0-50）；否则写产物 + 写锁文件（UTF-8 无 BOM、LF），
  并删除「上次生成、这次不再产出」的 `PMNet*.g.cs` 残留。
- `--decl-check`：产物与锁文件**逐字节比对**（比对前归一 BOM/行尾），不一致退出码 `2`；另报「残留的过期产物」。
- 既有 proto 三模式与退出码语义未改（已回归验证，见 §3.5）。

### 2.6 `Tools/PMDeclCheck/PMDeclCheck.csproj` + `Program.cs`（新建）
- `net8.0` 可执行工程，`ProjectReference → PMNetGen`（直接驱动三个部件，而不是把生成器当黑盒）。
- `<Compile Remove="Fixtures/**" />`：夹具是**输入数据**，`Bad.cs` 是故意非法的，不能被当源码编进本工程。
- 门禁 11 个小节、59 条断言，见 §3 与 §4。
- `--emit-fixtures <dir>` 附加模式：把「原序 / 重排」两份声明写到调用方指定的目录，供 CLI 级 `fc /b` 复核。

### 2.7 `Tools/PMDeclCheck/Fixtures/Good.cs` / `Bad.cs`（新建）
- `Good.cs`：合法声明集，刻意覆盖 §6 每一类类型 + 三种 RPC 方向 + 两种校验档位 +
  `_ForceValidate` 同伴 + 多 partial 部分 + 派生自运行时基类（`PMNetControllerObject`）+
  显式钉住 `StableKey` 的类 + 未钉住的类（供「改名显式」不变性用）。
- `Bad.cs`：非法声明集，规则 1–9、11 **各至少一个源码层用例**，规则 10 另有一个
  「同一成员拆两个 partial 部分」的源码层用例。规则 12 无法在源码层构造（见 §4.3）。

### 2.8 `Docs/plans/pmnet-ids.json`（新建）
用 `Good.cs` 跑一次 `--decl-gen` 产出的初始锁文件（4 个类 / 17 个成员 ID）。
格式 `formatVersion=1`，UTF-8 **无 BOM + LF** —— 与 `PMIdLock.Serialize()` 的输出逐字节一致，
这样「二次生成零 diff」对锁文件也成立。

---

## 3. 验收输出（实际执行，原样贴回）

### 3.1 四条验收命令

```
### dotnet build Tools/PMDeclModel -c Release
    0 个错误

已用时间 00:00:00.74

### dotnet build Tools/PMNetGen -c Release
    0 个错误

已用时间 00:00:00.89

### dotnet build Tools/PMDeclCheck -c Release
    0 个错误

已用时间 00:00:01.07
```

> 另做了一次**从零构建**验证（删掉三个工程的 `bin/`+`obj/` 后重建）：
> `PMNetGen` 与 `PMDeclCheck` 均 `0 个警告 / 0 个错误`，即 Roslyn 依赖可以完全离线还原，
> 验收命令在干净副本上同样成立。

### 3.2 产出锁文件（前置步骤，用最小示例声明）

```
$ dotnet Tools/PMNetGen/bin/Release/net8.0/PMNetGen.dll \
    --decl-gen Tools/PMDeclCheck/Fixtures/Good.cs \
    --out-dir <TEMP>/r2out --id-lock Docs/plans/pmnet-ids.json
[PMNetGen] 声明扫描：文件 1 个（语法错误 0 处），网络类 4 个，复制属性 13 个，RPC 4 条
    ClassId=1121439345  PMNetFixtures.FixturePinned  props=1  rpcs=0  maskBits=1  hash=0x1C484519
    ClassId=2171341074  PMNetFixtures.FixtureController  props=1  rpcs=0  maskBits=1  hash=0x07B40857
    ClassId=2810388711  PMNetFixtures.FixtureHud  props=2  rpcs=0  maskBits=2  hash=0xAE98B85A
    ClassId=3825713861  PMNetFixtures.FixturePlayer  props=9  rpcs=4  maskBits=9  hash=0x21A55EFA
[PMNetGen] 已生成 5 个文件到 C:/Users/LUOMIN~1/AppData/Local/Temp/r2out
[PMNetGen] 已写出 ID 锁文件 Docs/plans/pmnet-ids.json
[PMNetGen] 生成期整体协议摘要 GlobalProtocolHash = 0x2D95F26A
exit=0
```

### 3.3 门禁

```
### dotnet Tools/PMDeclCheck/bin/Release/net8.0/PMDeclCheck.dll
PMDeclCheck - R2 声明门禁
  仓库根   : D:\UGit\hyld-master
  临时工作区: C:\Users\luomingcong\AppData\Local\Temp\PMDeclCheck

== 0. 夹具可用性 ==
  [PASS] Good.cs 存在  —— D:\UGit\hyld-master\Tools/PMDeclCheck/Fixtures/Good.cs
  [PASS] Bad.cs 存在  —— D:\UGit\hyld-master\Tools/PMDeclCheck/Fixtures/Bad.cs

== 1. 正向扫描（Good.cs） ==
  扫描：文件 1 个，类 4 个，属性 13 个，RPC 4 条，错误 0 个，告警 0 个
  [PASS] 解析到源文件（ParsedFileCount > 0）  —— ParsedFileCount=1
  [PASS] 扫描到网络类（> 0）  —— 类数=4
  [PASS] 扫描到复制属性（> 0）  —— 属性数=13
  [PASS] 扫描到 RPC（> 0）  —— RPC 数=4
  [PASS] Good.cs 零声明错误
  [PASS] Good.cs 零语法错误  —— SyntaxErrorCount=0
  [PASS] 已提交锁文件与当前声明一致（同键同 ID）  —— 比对 4 个类键，不一致 0 处

== 2. 负向验证：契约 §5 十二条规则逐条命中 ==
  Bad.cs 扫描：类 12 个，错误 25 个，告警 1 个
  [PASS] Bad.cs 确实触发了声明错误  —— 错误数=25

== 3. 负向验证（补充）：注入损坏模型覆盖 IR 级不变量（规则 10 / 12） ==
      [规则 10] 同一类内 PropertyId 重复：Injected.Broken 的 A 与 B 都用 7（线协议歧义）（<未知源文件>:0）
      [规则 10] 同一类内 RpcId 重复：Injected.Broken 的 R1 与 R2 都用 5（<未知源文件>:0）
      [规则 12] 变更掩码区间重叠：Injected.Broken 的位 0 被多个属性占用（B）
      [规则 12] 变更掩码位数与属性占用不一致：Injected.MaskGap 的属性合计 2 位，但 ChangeMaskBitCount = 5
  [PASS] 注入模型触发了错误  —— 错误数=4
  [PASS] 注入模型命中规则 10  —— 命中 2 次
  [PASS] 注入模型命中规则 12  —— 命中 2 次

== 4. 规则命中总表 ==
  [PASS] 规则  1  命中   3 次（源码夹具 3 + 注入模型 0）  [PMNetworkObject] 的类必须是 partial 且能推到 PMNetObject
  [PASS] 规则  2  命中   1 次（源码夹具 1 + 注入模型 0）  [PMNetworkObject] 的类不得是泛型类
  [PASS] 规则  3  命中   3 次（源码夹具 3 + 注入模型 0）  [PMReplicated] 成员不得是 static / const / readonly
  [PASS] 规则  4  命中   4 次（源码夹具 4 + 注入模型 0）  [PMReplicated] 成员与 RPC 参数的类型必须落在支持的类型集内
  [PASS] 规则  5  命中   1 次（源码夹具 1 + 注入模型 0）  [PMRepNotify] 的 ForMember 必须对应一个已声明的 [PMReplicated] 成员
  [PASS] 规则  6  命中   2 次（源码夹具 2 + 注入模型 0）  [PMRepNotify] 目标方法必须无参、返回 void
  [PASS] 规则  7  命中   3 次（源码夹具 3 + 注入模型 0）  [PMRpc] 方法必须返回 void、不得 static（且必须位于 [PMNetworkObject] 类内）
  [PASS] 规则  8  命中   1 次（源码夹具 1 + 注入模型 0）  [PMServerRpc] 必须声明校验（Validator != None 或存在 _ForceValidate 同伴）
  [PASS] 规则  9  命中   1 次（源码夹具 1 + 注入模型 0）  WithValidation = true 与 Validator = ForceValidate 不得同时出现
  [PASS] 规则 10  命中   5 次（源码夹具 3 + 注入模型 2）  同一类内 PropertyId / RpcId 不得重复（含跨程序集）
  [PASS] 规则 11  命中   3 次（源码夹具 3 + 注入模型 0）  声明的条件必须是 D-R0-14 的 8 项之一
  [PASS] 规则 12  命中   2 次（源码夹具 0 + 注入模型 2）  同一个类的 MaskOffset 区间不得重叠，且总数 == ChangeMaskBitCount
  规则命中合计：29 次

== 5. 不变性 1：重排不变（两种排列各生成一次，逐字节比对） ==
  [PASS] 重排后的源码与原源码文本不同（否则这条不变性是空测）  —— 长度 5790 → 5409
  [PASS] 重排后的源码零语法错误  —— SyntaxErrorCount=0
  [PASS] 两种排列都零声明错误
  [PASS] 两种排列的声明集合相同（类/属性/RPC 计数）  —— A=4/13/4  B=4/13/4
  [PASS] 重排后 ClassId 不变  —— 不一致 0 个
  [PASS] 两种排列的生成物逐字节相同  —— 5 个文件逐字节相同

== 6. 不变性 2：二次生成零 diff（含锁文件） ==
  [PASS] 二次生成的锁文件逐字节相同  —— 长度 1490 vs 1490
  [PASS] 二次生成的产物逐字节相同  —— 5 个文件逐字节相同
  [PASS] 二次生成没有新增 ID（Added 为空）  —— Added=0
  [PASS] 夹具级 ID 无哈希碰撞（探测未发生）  —— Collisions=0

== 7. 不变性 3：改名显式（新 ID + 退役告警；钉住 StableKey 则 ID 不变） ==
  [PASS] 夹具包含未钉住键的 FixtureHud  —— ClassId=2810388711
  [PASS] 夹具包含钉住 StableKey 的 FixturePinned  —— ClassId=1121439345
  [PASS] 改名后的源码文本确实变了
  [PASS] 改名后出现新类键  —— FixtureHudRenamed ClassId=2218972243
  [PASS] 改名后得到新 ClassId（不是静默沿用）  —— 2810388711 → 2218972243
  [PASS] 旧键的 ID 未被回收（仍在锁文件里）  —— 旧键 = 2810388711
  [PASS] 改名产生了退役告警（ID 永不回收）  —— [告警] 稳定键已退役（ID 不回收，线协议里该编号永久保留）：CLASS:PMNetFixtures.FixtureHud
  [PASS] 钉住 StableKey 的类改名后 ClassId 不变（D-R0-49 的兼容路径）  —— 1121439345 → 1121439345

== 8. 生成物保真：ID 内嵌一致 / RegisterAll 显式且不扫反射 ==
  [PASS] 产物数量 = 类数 + 1（注册表）  —— 文件 5 个 / 类 4 个
  [PASS] 产出了注册表文件 PMNetGeneratedRegistry.g.cs
  [PASS] 每个类的产物都存在且内嵌 ClassId / 掩码位数与 IR 一致  —— ID 不一致 0 处，缺失 0 个
  [PASS] 每个 RPC 的产物内嵌 RpcId 与 IR 一致  —— 不一致 0 处
  [PASS] RegisterAll 显式列出每个类  —— 未列出 0 个
  [PASS] 产物不含反射扫描（D-R0-48 / T40：运行不扫反射）  —— 未发现 System.Reflection / Activator / GetTypes / typeof 等记号
  [PASS] 产物的 BCL 用法限于白名单（System.FormatException）  —— 无其它 System.* 用法

== 9. 生成物可编译（C# 7.3 + netstandard2.0 + PMNet 运行时） ==
  [PASS] 编译引用集可用  —— netstandard2.0 引用程序集（113 个，来自 C:\Users\luomingcong\.nuget\packages\netstandard.library\2.0.3\build\netstandard2.0\ref）
      编译输入：生成物 5 个 + 夹具 1 个 + PMNet 运行时 27 个；引用集：netstandard2.0 引用程序集（113 个，来自 ...）
  [PASS] 生成物 + 夹具 + 运行时在 C# 7.3 下零编译错误  —— 错误 0 个

== 10. 全局协议摘要自洽（生成期与运行期同一实现） ==
  [PASS] 生成期 GlobalProtocolHash 与用 PMStableHash 重算的一致  —— 0x2D95F26A vs 0x2D95F26A
  [PASS] 每个类的 ClassProtocolHash 自洽  —— 4 个类
  [PASS] ClassId 与 MemberId 不返回 0（0 是保留值）  —— ClassId("")=2166136261 MemberId("")=64226
  [PASS] ParamLayoutId 对同类型列表稳定、对不同类型区分  —— 26502 / 26502 / 31961
  [PASS] PMCond 已实现条件数 = 8  —— None / OwnerOnly / SkipOwner / SimulatedOnly / AutonomousOnly / Custom / Dynamic / Never

== 11. 边界：空声明集也要产出注册表 ==
  [PASS] 空声明集只产出注册表  —— 文件 1 个
  [PASS] 空注册表仍然可编译（GeneratedClassCount = 0 且条目数组长度为 0）

== 汇总 ==
  PASS 59 / FAIL 0
  临时工作区（保留，供 fc /b 复现）: C:\Users\luomingcong\AppData\Local\Temp\PMDeclCheck
  结果：全部通过
exit=0
```

### 3.4 「重排不变」与「二次生成零 diff」：CLI 级 `fc /b` 证据

准备（全部落在 `%TEMP%`，不写仓库）：

```
dotnet .../PMDeclCheck.dll --emit-fixtures <TEMP>/r2fc/src          # 写出 A（原序）/ B（类型与成员顺序都反转）
cp Docs/plans/pmnet-ids.json <TEMP>/r2fc/ids.json                  # 用同一把锁
dotnet .../PMNetGen.dll --decl-gen <TEMP>/r2fc/src/A --out-dir <TEMP>/r2fc/out-A  --id-lock <TEMP>/r2fc/ids.json
dotnet .../PMNetGen.dll --decl-gen <TEMP>/r2fc/src/B --out-dir <TEMP>/r2fc/out-B  --id-lock <TEMP>/r2fc/ids.json
cp <TEMP>/r2fc/ids.json <TEMP>/r2fc/ids-after.json
dotnet .../PMNetGen.dll --decl-gen <TEMP>/r2fc/src/A --out-dir <TEMP>/r2fc/out-A2 --id-lock <TEMP>/r2fc/ids.json
```

`fc /b` 结果（11 处比较，**全部 "no differences encountered"**）：

```
=== [1] perm-A (original declaration order) vs perm-B (types AND members reversed) ===
Comparing files OUT-A\PMNet.PMNetFixtures.FixtureController.g.cs and OUT-B\PMNET.PMNETFIXTURES.FIXTURECONTROLLER.G.CS
FC: no differences encountered
Comparing files OUT-A\PMNet.PMNetFixtures.FixtureHud.g.cs and OUT-B\PMNET.PMNETFIXTURES.FIXTUREHUD.G.CS
FC: no differences encountered
Comparing files OUT-A\PMNet.PMNetFixtures.FixturePinned.g.cs and OUT-B\PMNET.PMNETFIXTURES.FIXTUREPINNED.G.CS
FC: no differences encountered
Comparing files OUT-A\PMNet.PMNetFixtures.FixturePlayer.g.cs and OUT-B\PMNET.PMNETFIXTURES.FIXTUREPLAYER.G.CS
FC: no differences encountered
Comparing files OUT-A\PMNetGeneratedRegistry.g.cs and OUT-B\PMNETGENERATEDREGISTRY.G.CS
FC: no differences encountered

=== [2] out-A vs out-A2 (two consecutive generations, same lock) ===
Comparing files OUT-A\PMNet.PMNetFixtures.FixtureController.g.cs and OUT-A2\PMNET.PMNETFIXTURES.FIXTURECONTROLLER.G.CS
FC: no differences encountered
... （其余 3 个类产物 + 注册表同样 no differences）

=== [3] lock file after 2nd gen vs after 1st gen ===
Comparing files ids-after.json and IDS.JSON
FC: no differences encountered

=== [4] negative control (fc MUST report differences) ===
Comparing files OUT-A\PMNetGeneratedRegistry.g.cs and IDS.JSON
00000000: EF 7B
00000001: BB 0A
00000002: BF 20
00000003: 2F 20
...
```

> 第 [4] 项是**必需的反向对照**：如果 `fc /b` 只会打印 "no differences"，上面的绿就是假绿。
> 该项比对两个确实不同的文件，`fc` 正确报出了首个差异字节 `EF` vs `7B`（也正是生成物的 UTF-8 BOM）。

### 3.5 既有 proto 模式回归（证明没有破坏原行为）

```
$ dotnet .../PMNetGen.dll --proto ProtobufAndNotepad/Protobuf/SocketProto.proto \
    --source-path ProtobufAndNotepad/Protobuf/SocketProto.proto \
    --check Client/Assets/Scripts/PMNet/Generated/SocketProto.PMNet.g.cs
[PMNetGen] 同步校验通过: Client/Assets/Scripts/PMNet/Generated/SocketProto.PMNet.g.cs
[PMNetGen] 枚举 9 个，消息 21 个，产物 114399 字符
EXIT=0

$ dotnet .../PMNetGen.dll --normalize-eol Client/Assets/Scripts/PMNet/Generated/SocketProto.PMNet.g.cs
[PMNetGen] 行尾已是 CRLF，无需改写: ...
EXIT=0
```

### 3.6 `--decl-scan` / `--decl-check` 的退出码语义

| 命令 | 场景 | 退出码 |
|---|---|---|
| `--decl-scan Good.cs` | 0 错误 | `0` |
| `--decl-scan Bad.cs` | 25 个声明错误（逐条打印） | `1` |
| `--decl-check`（产物与锁都同步） | — | `0` |
| `--decl-check`（篡改一个产物 3 字节） | 报「产物与声明不同步」 | `2` |
| `--decl-check`（删掉一个类产物） | 报「缺少生成产物」 | `2` |

---

## 4. 负向验证结果（逐条规则的实际命中）

### 4.1 源码夹具（`Bad.cs`，25 条错误）逐规则命中数

| 规则 | 命中 | 非法输入（`Bad.cs` 中的具体声明） |
|---|---|---|
| 1 | **3** | `BadNotPartial`（非 partial）、`BadWrongBase : PlainBase`（推不到 PMNetObject）、`[PMNetworkObject] partial struct BadStructHost`（标在 struct 上） |
| 2 | **1** | `BadGeneric<T>` |
| 3 | **3** | `static int StaticHp`、`readonly int ReadOnlyHp`、`const int ConstHp = 1` |
| 4 | **4** | `List<int> BadList`、`BadNetStruct BadStructField`（`[PMNetworkStruct]`）、`char BadChar`、RPC 参数 `List<string> items` |
| 5 | **1** | `[PMRepNotify("DoesNotExist")]`（没有对应复制成员） |
| 6 | **2** | `OnRep_HpWithParam(int oldValue)`（有参）、`int OnRep_HpNotVoid()`（非 void） |
| 7 | **3** | `static void StaticRpc()`、`int NonVoidRpc()`、`PlainHost.RpcOnPlainClass()`（不在网络类内） |
| 8 | **1** | `[PMServerRpc] void NoValidatorRpc()`（既无 Validator 也无 `_ForceValidate` 同伴） |
| 9 | **1** | `[PMServerRpc(WithValidation = true, Validator = ForceValidate)] void ConflictRpc()` |
| 10 | **3** | `BadDuplicateMember` 的 `Hp` 拆在两个 partial 部分各声明一次（稳定键相同 ⇒ `PropertyId` 相同 = 1972），触发 3 条：「同一类内 PropertyId 重复」「同一类内成员重复声明」「生成的访问器名冲突」（两者都生成 `PMNet_SetHp`） |
| 11 | **3** | `PMCond.InitialOnly`（名字形式）、`Condition = PMCond.NetGroup`（命名参数形式）、`(PMCond)1`（强制转换数值形式）——三者都按「D-R0-14 明确不做的 9 项」报错，不做静默降级 |
| 12 | **0（源码层不可构造，见 4.3）** | — |

> 规则 1–9、11 的命中消息都会带「类限定名 + 成员名 + 源文件:行号」，实际日志示例（规则 11）：
> `[规则 11] 复制条件不在首版允许的 8 项内：PMNetBadFixtures.BadCondition.LegacyInitialOnly 写了 `PMCond.InitialOnly`
> —— 该条件属于 D-R0-14 明确不做的 9 项（UE 原值 1），不做静默降级。允许值：None / OwnerOnly / ... （Bad.cs:263）`
> `Bad.cs` 的完整 25 条错误可用 `--decl-scan Tools/PMDeclCheck/Fixtures/Bad.cs`（退出码 1）复现。

### 4.2 规则 8 的**放行**路径也已验证（不是只测了报错）

`Good.cs` 同时覆盖三种合法写法且零错误，说明规则 8 没有误杀：
`Validator = PMRpcValidator.ForceValidate`（`Fire`）、
`WithValidation = true`（`Bad.cs` 里的 `OkWithValidationRpc` 对照项，无报错）、
存在 `Teleport_ForceValidate` 同伴（`Good.cs` 的 `Teleport`，IR 里的生效档位被折叠为 `ForceValidate`）。

### 4.3 明确的「未命中」：只有规则 12 在源码层 0 命中

- **规则 12 在源码夹具里 0 命中，这是有意为之，必须明说。**
  规则 12 判的是 `MaskOffset` 区间重叠与总位数一致，而 `MaskOffset` **完全由生成器自行分配**
  （按 `PropertyId` 升序紧凑排布），源码层没有任何语法能构造出重叠区间 ——
  「源码层能构造」与「分配器正确」这两件事在这里是同一件事。
- 因此改由**注入一个手工损坏的 `PMDeclModel`** 覆盖（门禁 §3）：直接构造
  `PropertyId` 重复 / `MaskOffset` 重叠 / 总数不匹配的 IR，断言校验器确实报出规则 10 与规则 12。
  这一条是必要的，否则会留下「校验器根本没生效」的盲区。
- 规则 10 同理**部分**依赖注入：`ClassId` 跨程序集重复无法在一次扫描里看到；
  「拆两个 partial 部分重复声明成员」可以在源码层构造，已放进 `Bad.cs`。

### 4.4 门禁在开发期真实抓到的 3 个缺陷（证明门禁不是形式主义）

| # | 症状 | 根因 | 处置 |
|---|---|---|---|
| 1 | 退役告警里的键变成 `"CLASS` | 锁文件行解析取了行内**第一个** `:`，而稳定键本身含 `:`（`CLASS:Ns.Type`） | 新增 `FindValueColon()`：先找键的右引号，再找其后的冒号 |
| 2 | `GlobalProtocolHash` 生成期 `0x05839E1D` ≠ 重算 `0x2D95F26A` | `Scan()` 里先算摘要、后把 `classes` 挂到 `model.Classes`，导致摘要算的是**空类表** | 调整顺序：先 `model.Classes = classes`，再 `ComputeProtocolHashes` |
| 3 | `CS0579 Duplicate 'PMNetworkObject' attribute` | 夹具把 `[PMNetworkObject]` 写在了两个 partial 部分上（`AllowMultiple = false`） | 夹具改为只写一处，并在夹具注释里记下这条约束 |

> 第 2 条是**最有价值的一次拦截**：它正是契约 §3 想防的「两端协议摘要静默不一致」。
> 生成物里的摘要由运行期 `PMStableHash` 现算、生成期的是另一条路径算的；
> 门禁 §10 用 `BuildClassEntries(model)` 重算并比对，直接把这类偏差钉死在 CI 里。

---

## 5. 冻结接口问题

### 5.1 【真实张力，建议主 Agent 决策】显式 `StableKey` 只钉住类 ID，不钉住成员 ID

- 契约 §2.1 明确：「`[PMNetworkObject("显式键")]` 可覆盖**类的**稳定键」，而属性/RPC 的稳定键仍是
  `PROP:命名空间.类型名.成员名`。
- 但 §2.1 的**动机**写的是「改名但要保持线协议兼容时用」。
  按字面实现后，这个动机并不成立：钉住类键后改类名，**类 ID 不变、成员 ID 全变**
  （因为它们按 `命名空间.类型名` 计算）。实测：`FixturePinned` → `FixturePinnedRenamed`，
  `ClassId` 保持 `1121439345`，而 `_value` 的 `PropertyId` 从 `39454` 变成 `29275`。
- 本轮**按字面实现**（未擅改冻结接口与契约），并在门禁里把这条观察明确打印出来。
  若主 Agent 希望「钉住类键即钉住整类线协议」，需要的是契约层修改
  （例如成员键改为 `memberStableKey` 以类 StableKey 为前缀），**不属于本轮权限范围**。

### 5.2 【设计约束，已按字面实现】`PMRpcWriter` 的形状装不下「本次调用的实参」

- §4.1 要求 `private static void PMNet_RpcWrite_<方法名>(PMNetObject t, PMNetWriter w)`，
  而它的形状又与 `PMRpcWriter`（描述符表里的发送委托）一致；`t` 只有 `PMNetObject`，没有参数位。
- 字面复用只能有一种自洽解释：**实参暂存在对象上**。因此生成器为每条 RPC 产出
  `private <T> PMNetRpcArg_<方法名>_<i>` 实参帧字段，写入器从中读取；
  调用桩先把实参写进帧、再**同步**调用 `EnqueueRemote`（编码在同一次栈上完成，因此同一条 RPC 的重入不会破坏它）。
- 副作用需要主 Agent 知晓：`PMNetRpcEntry.Write` 这个委托**只有在调用方先把实参填进帧之后才有意义**。
  当前 RPC 传输层（M05 发送侧）尚未接线，因此这是留给 B/C 项的**明确接口语义**，不是缺陷。

### 5.3 【本轮新增的最小接缝，需要后续接线方确认】

- 因为「发远端」在 M05 落地前没有可调用的运行时 API，生成器在注册表里产出一个**显式接缝**：
  `PMNetGeneratedRegistry.RemoteSender`（`Action<PMNetObject, ushort, PMRpcWriter>`）+ 待发队列
  `EnqueueRemote/TryDequeueRpc/PendingRpcCount/ClearPendingRpcs`。
  未接线时「已判定为 Remote」的调用进入待发队列（可被端到端测试断言）；接线时由 M05 赋值 `RemoteSender`。
- §4.1/§4.2 只规定了必须产出的成员，没有规定「发远端」的实现方式，因此这不与其冲突；
  但它是一个**新引入的、A/B/C 之间的事实接口点**，已在此显式登记。

### 5.4 【无缺陷但需记录】`PMIdLock` 没有枚举键的 API

- 「键退役只告警、ID 永不回收」需要枚举锁文件里的全部键，而 `PMIdLock` 只提供 `TryGet*`。
- 处置：复用 `PMIdLock.Serialize()` 的**输出形状**做一次只读键扫描（`ExtractLockKeys`），
  受 `formatVersion` 保护，不引入第二套 JSON 实现，也不改冻结接口。

---

## 6. 诚实记录的缺口与未验证项

### 6.1 生成物编译验证的口径（已做/未做）

- **已做**：把「生成物 + `Good.cs` + `PMNet` 运行时 27 个源文件」放进一次
  `CSharpCompilation`，`LanguageVersion.CSharp7_3`，引用
  `~/.nuget/packages/netstandard.library/2.0.3/build/netstandard2.0/ref/*.dll`（113 个），
  断言 **0 error**。这条同时覆盖了「生成物与业务 partial 能合成同一个类」「命名空间/using 正确」
  「引用了真实存在的 PMNet API」。
- **未做**：没有把生成物真的放进 Unity 2019.4 编译（本机无 Unity 环境，任务也明确不要用 Unity）。
  也没有把生成物纳入 `Tools/PMNetLangCheck` 那种「工程级 `netstandard2.0` 编译」——
  因为生成物落在运行时目录之外，把它编进 `PMNetLangCheck` 需要额外的 `.meta`/工程改动，超出本轮写边界。
  作为替代，门禁额外断言生成物的 **BCL 用法白名单**（除 `using` 行外只允许 `System.FormatException`），
  使它的 API 面成为可人工复核的有限集合。

### 6.2 首版**不强制**的两处上限（契约 §8 列为「待收敛」，本轮未收敛）

| 项 | 现状 | 缺口 |
|---|---|---|
| 字符串复制长度上限（契约 §8 建议 1024 字节） | **未在任何地方强制** | 生成物直接调 `WriteStringValue/ReadStringValue`，没有长度门；恶意长串会照单全收（虽然读侧受 MTU/分片预算约束） |
| 复制数组长度上限 | 生成期取 `4096`（`PMGeneratedMaxArrayLength`），越界抛 `System.FormatException` | 这个 4096 是**本轮自定的保守值**，不是契约收敛结果；契约只写了「按 §9 MTU 反推」 |

> 另：一维数组的元素编码是「逐元素」而非 protobuf 的 packed / bytes，因此 `byte[]` 会被编码成
> 「长度 + 每字节一个 varint」。语义正确（同一份声明两端对称），但带宽不是最优。已按 §6 表的
> 「长度 + 元素」字面实现，**记录为未来可优化项**。

### 6.3 校验同伴（`_Validate` / `_ForceValidate`）只记录档位，**不生成调用**

- 规则 8 的**声明门禁**已完整落地（缺校验即编译期失败，反外挂红线）。
- 但接收侧**执行**校验需要冻结的运行期 API（三态 `NetAccept/NetReport/NetReject`），
  而 hyld-master 的 `PMNet` 侧目前只有 `PMRpcValidator` 枚举、没有对应的运行时接口。
- 处置：生成物把生效档位写进 `PMRpcDescriptor.Validator`，并在 `PMNet_RpcInvoke_<方法名>` 里留注释
  说明「由 M05 的接收校验流程接线」。**因此「声明了校验」目前不等于「运行时真的校验了」**，
  这一层的兑现属于 B/C 项。这是本轮最需要主 Agent 知晓的能力边界。

### 6.4 量化器（`[PMQuantized]`）只记录 `QuantizerId`，不生成量化代码

- 与冻结契约一致（`PMQuantizedAttribute` 注释写明「量化在**属性层**做，不改变线格式的选择」）；
  生成物按 `float` 原样写 fixed32，`QuantizerId` 进描述符供复制层使用。
  **本轮不含任何量化实现或量化正确性验证。**

### 6.5 继承链基址在跨程序集时会静默按 0 处理

- `PropertyIndexBase` 只沿「本次扫描到的已声明类」上溯。若业务类的中间基类在另一个程序集/另一个扫描批次里
  （且带复制属性），生成物写出的 `PropertyIndex` 基址会是 0，与基类的 `PMRepList` 槽位重叠。
- 未加告警（规则 1 的「无法证明继承关系」会对同类问题告警，但基址这条路径没有）。
  **规避方式**：把网络基类与派生类放进同一次 `--decl-gen` 的源路径集合。
  记为已知缺口。

### 6.6 其他已知限制（均已在生成物/门禁里显式呈现，未静默）

- 生成物访问器名按契约字面拼接：`_hp` ⇒ `PMNet_Set_hp` / `PMNet_Write__hp`（双下划线）。
  若同类的成员只差前导下划线（`_hp` 与 `hp`），会生成同名方法 ⇒ 已由规则 10 的补充检查在声明期拦下。
- 同一成员被多个 `[PMRepNotify]` 监听 ⇒ 只有先声明的生效，`DeclValidation` 给出告警。
- 同一 RPC 名以 `_Implementation` 结尾 ⇒ 生成器按**字面方法名**处理并告警，
  提示与迁移计划 §3.9.5 的另一种写法（`X_Implementation` + 生成 `X`）不同；本轮以 R2 §4.3 为准。
- `[PMNetworkObject]` 只能写在**一个** partial 部分上（`AllowMultiple=false`），
  写两处会得到 `CS0579`；本轮由编译门禁捕获，未单列为声明规则。
- 扫描 `--decl-gen` 会**删除** `--out-dir` 里「本次不再产出」的 `PMNet*.g.cs`（避免陈旧产物参与编译）；
  只匹配 `PMNet*.g.cs`，不会碰其它文件。
- 非 UTF-8（如 GBK）源文件里的非 ASCII 字符会被解码为 U+FFFD，但属性名/类型名/成员名都是 ASCII，
  声明扫描不受影响；门禁会打印 Roslyn 的语法错误计数，不静默。

### 6.7 编码约定的一处不一致（按任务书执行，记录在案）

- 任务书要求「新建含中文的源文件一律 UTF-8 + BOM + CRLF」。本轮新建的 7 个文件
  （`DeclScanner.cs`、`DeclValidation.cs`、`DeclEmitter.cs`、`PMDeclCheck.csproj`、`PMDeclCheck/Program.cs`、
  `Fixtures/Good.cs`、`Fixtures/Bad.cs`、以及本报告）已全部为 **UTF-8 + BOM + CRLF**。
- 但仓库 `Tools/` 目录既有文件是 **UTF-8 无 BOM + LF**（`Tools/PMDeclModel/*`、`Tools/PMNetGen/Program.cs` 等），
  只有 `Client/Assets/Scripts/PMNet/Declarations/*` 是 BOM + CRLF。
  因此 `Tools/PMNetGen/` 目录内现在是「新文件 BOM+CRLF / 旧文件 无BOM+LF」的混合。
- 我**没有**改动既有文件的编码（`Tools/PMNetGen/Program.cs`、`Tools/PMNetGen/PMNetGen.csproj` 保持原样，
  只做内容修改），以免制造与本任务无关的整文件 diff。若主 Agent 希望目录内统一，需要一次显式的编码归一提交。
- `Docs/plans/pmnet-ids.json` 是**数据文件**，保持 UTF-8 无 BOM + LF ——
  这样它与 `PMIdLock.Serialize()` 的输出逐字节一致，「锁文件二次生成零 diff」才成立。

---

## 7. 交付物清单

| 文件 | 状态 |
|---|---|
| `Tools/PMNetGen/DeclScanner.cs` | 新建 |
| `Tools/PMNetGen/DeclValidation.cs` | 新建 |
| `Tools/PMNetGen/DeclEmitter.cs` | 新建 |
| `Tools/PMNetGen/Program.cs` | 修改（追加 3 个子命令，既有 3 个模式行为不变，已回归） |
| `Tools/PMNetGen/PMNetGen.csproj` | 修改（+Roslyn 4.11.0、+ProjectReference→PMDeclModel） |
| `Tools/PMDeclCheck/PMDeclCheck.csproj` | 新建 |
| `Tools/PMDeclCheck/Program.cs` | 新建（59 条断言） |
| `Tools/PMDeclCheck/Fixtures/Good.cs` | 新建（合法声明集夹具） |
| `Tools/PMDeclCheck/Fixtures/Bad.cs` | 新建（非法声明集夹具） |
| `Docs/plans/pmnet-ids.json` | 新建（初始 ID 锁文件，4 类 / 17 成员） |
| `Docs/plans/_r2a_report.md` | 本报告 |
| `Tools/PMNetGen/**/*.meta`、`Tools/PMDeclCheck/**/*.meta` | **不需要**：`Tools/` 不在 Unity 工程（`Assets/`）内 |

未触碰：`Client/Assets/Scripts/PMNet/**`、`Tools/PMDeclModel/**`、`Tools/PMNetGen/` 下除上述 5 个文件以外的文件、
`D:\UE_Project\ProjectMecury\**`。

## 8. 复现命令速查

```bat
REM 1) 构建
dotnet build Tools\PMDeclModel -c Release
dotnet build Tools\PMNetGen -c Release
dotnet build Tools\PMDeclCheck -c Release

REM 2) 门禁（59 条断言；输出会打印每条规则的实际命中数）
dotnet Tools\PMDeclCheck\bin\Release\net8.0\PMDeclCheck.dll

REM 3) 生成（产出锁文件）
dotnet Tools\PMNetGen\bin\Release\net8.0\PMNetGen.dll --decl-gen Tools\PMDeclCheck\Fixtures\Good.cs --out-dir <输出目录> --id-lock Docs\plans\pmnet-ids.json

REM 4) 构建期门禁（build.bat 用；不同步退出码 2）
dotnet Tools\PMNetGen\bin\Release\net8.0\PMNetGen.dll --decl-check Tools\PMDeclCheck\Fixtures\Good.cs --id-lock Docs\plans\pmnet-ids.json --out-dir <输出目录>

REM 5) 只扫描打印摘要（有声明错误退出码 1）
dotnet Tools\PMNetGen\bin\Release\net8.0\PMNetGen.dll --decl-scan <源目录或 .cs>
```
