# P1：独立 IL 编织工具 + 真实编译/执行证明（T-W1 / T-W2 证据）

> 契约：`Docs/plans/net-rpc-weaving-contract.md`（唯一新冻结接口）。
> 本文件是 **P1 的证据与结论**，状态与验收仍只在 `net-architecture-migration.md` 登记。
> 边界：**没有改动任何既有生产源码、生成器或 Generated 产物**；新增文件只有
> `Tools/PMNetWeaver/**`（4 个）、`Tools/PMNetWeaverTest/**`（4 个）与本报告。

---

## 0. 一句话结论

`Tools/PMNetWeaver`（net8.0 CLI + Mono.Cecil 0.11.6）已按契约实现「预检 → 克隆业务体 →
原方法改发包入口 → 两个 helper 改指私有业务体 → stamp=1 → 暂存 + 原子替换 DLL/PDB」，
并且有一个**真实编译 + 真实执行**的独立门禁 `Tools/PMNetWeaverTest` 证明它：

- 夹具由**现有 PMNetGen** 扫出生成物、由**真实 `dotnet build`**（C# 7.3 / netstandard2.0 /
  Debug + Release / portable PDB）编成 DLL；
- 编织走**冻结的 CLI 契约**（`--weave` / `--check` / `--require-rpcs` / `--help`），
  以独立进程调用；
- 编织后的程序集被 `AssemblyLoadContext` 加载，在夹具内部跑完整行为链
  （普通调用发包 / 本地恰好一次 / 收包→校验→业务 / 不递归 / 数组调用点快照 /
   switch-循环-try-finally-委托体）；
- 负例族（未知版本 / 假 stamp / 缺 guard / 坏收包 helper / 坏发送 helper / 强名称 /
  无 RPC / 无 PDB / 非 PE 输入）全部**明确失败且零写**。

**门禁结果：通过 141 项，失败 0 项（exit 0）。** 26 次 CLI 调用、10 次真实夹具编译。

```
dotnet build Tools/PMNetWeaver/PMNetWeaver.csproj -c Release
dotnet build Tools/PMNetWeaverTest/PMNetWeaverTest.csproj -c Release -o <独立输出目录>
dotnet <独立输出目录>/PMNetWeaverTest.dll --repo D:/UGit/hyld-master
```

---

## 1. 交付物

| 文件 | 职责 |
|---|---|
| `Tools/PMNetWeaver/PMNetWeaver.csproj` | net8.0 可执行工具；唯一第三方依赖 `Mono.Cecil 0.11.6`（仅工具，不进 Unity runtime） |
| `Tools/PMNetWeaver/Program.cs` | CLI：`--weave` / `--check` / `--reference-dir`（可重复）/ `--require-rpcs` / `--help`；退出码 0 / 1 / 2 |
| `Tools/PMNetWeaver/RpcAssemblyWeaver.cs` | 预检 + 编织 + 结构校验 + 独立 PDB 校验 + 暂存/原子替换 |
| `Tools/PMNetWeaver/ILBodyCloner.cs` | 业务体的**完整** IL 克隆（指令/分支/跳转表/异常处理/局部/参数/sequence point/作用域） |
| `Tools/PMNetWeaverTest/PMNetWeaverTest.csproj` | net8.0 独立门禁；用 net8.0 内置 `System.Reflection.Metadata` 做**第三方视角**读回 |
| `Tools/PMNetWeaverTest/Program.cs` | 门禁主流程（造夹具 → 编译 → 调 CLI → 加载执行 → 负例族 → 汇总） |
| `Tools/PMNetWeaverTest/Fixture.cs` | 声明夹具 + 夹具内驱动 `FixtureDriver`（`RunPreWeave` / `RunWoven`） |
| `Tools/PMNetWeaverTest/Fixture.GeneratedGuard.cs` | **临时 partial guard**，模拟冻结格式 v1 里生成器未来会产出的版本/守卫方法 |

### 1.1 为什么有 `Fixture.GeneratedGuard.cs`（以及为什么没改生成器）

契约 §2 要求生成物产出 `PMNet_GetRpcWeaveVersion()`（0 → 1）与 `PMNet_RequireRpcWeave()`，
P1 的硬边界又是「不改任何现有生成器」。因此：

- **版本/守卫方法**放在一个独立 partial 文件里（`Fixture.GeneratedGuard.cs`），
  与同一 partial 类的生成物合成同一个类型 ⇒ 格式与契约一致；
- **`PMNet_BuildEntry` 开头的 guard 调用**无法用第二个 partial 补（同一类型不能有两个同名方法），
  由门禁在**临时生成的 `.g.cs` 文本**里插入（`PMNet_RequireRpcWeave();`）。

门禁对这两处补丁都做了「锚点必须**恰好命中一次**」的断言：生成器格式一变，补丁立刻失败，
而不是静默漏改（这正是「不被静默跳过」在门禁侧的对应物）。

---

## 2. 冻结接口的实现口径

### 2.1 CLI（契约 §2，冻结）

```
PMNetWeaver --weave <assembly.dll> [--reference-dir <dir>]... [--require-rpcs]
PMNetWeaver --check <assembly.dll> [--reference-dir <dir>]... [--require-rpcs]
PMNetWeaver --help
```

- 退出码：`0` 成功 / 无 RPC（未要求非空）；`1` 失败（预检、结构校验、写盘）；`2` 用法错误。
- `--check` 只验证、**绝不写盘**（`InMemory = true` + 不调用写入路径）。
- 输出至少含：RPC 方法数、含 RPC 的类数、**是否实际改写**、符号读入/写出状态、逐条说明。
- 便利项：允许裸路径（等价 `--weave <path>`）。

### 2.2 marker 与辅助方法名（冻结，逐字）

| 名字 | 用途 |
|---|---|
| `PMNet.PMServerRpcAttribute` / `PMNet.PMClientRpcAttribute` / `PMNet.PMNetMulticastAttribute` | 只认这三个**精确全类型名**，不碰第三方同名 Attribute |
| `PMNet_<M>` | 发送 helper（契约要求 **private**） |
| `PMNet_RpcInvoke_<M>` | 收包 helper（`static void (PMNet.PMNetObject, PMNet.PMNetReader)`） |
| `PMNet_RpcBody_<M>` | 编织器产出的**私有业务体**（无 RPC Attribute） |
| `PMNet_GetRpcWeaveVersion()` | `internal static int`，0 = 未编织，1 = 已编织 |
| `PMNet_RequireRpcWeave()` | `private static int`，版本 != 1 抛 `InvalidOperationException` |
| `PMNet_BuildEntry()` | 类注册入口（开头必须调用 guard） |

### 2.3 编织后的不变式（`--check` 逐条验证，不是「只看 stamp」）

1. `M` 的 IL 恰好是 `ldarg.0 + 各参数 → call PMNet_<M> → ret`（无局部变量、无异常处理）；
2. 私有业务体存在、参数与 `M` 一致、**不带任何 RPC Attribute**；
3. 发送 helper 的唯一业务调用指向业务体（且**不再调用 `M`**，否则入口/helper 互相递归）；
4. 收包 helper 的唯一业务调用指向业务体（且**不再调用 `M`**，否则收包会递归回网络入口）；
5. guard 形态正确：版本方法能读成整型常量、guard **真的读取了版本方法**、
   `PMNet_BuildEntry` **真的调用了 guard**（防「假 guard 恒返回 1」）。

「原调用点身份保留」由实现保证：编织器**只**改两个 helper 内的那一次调用，
业务里其它对 `M` 的普通调用原样保留，因此方法组引用（委托）仍指向 `M`（网络入口）。

---

## 3. 明确拒绝的形态（不静默跳过）

预检阶段（**全部通过之前零写**）：

| 类别 | 判据 |
|---|---|
| 缺生成物 | 有 RPC 标记但缺 `PMNet_<M>` / `PMNet_RpcInvoke_<M>` / `PMNet_BuildEntry` |
| 缺 guard | 缺 `PMNet_GetRpcWeaveVersion` / `PMNet_RequireRpcWeave`；guard 不读版本；BuildEntry 不调 guard |
| 未知版本 | 版本方法返回 0/1 以外的值；版本方法含调用指令或常量个数 != 1 |
| 结构自相矛盾 | 版本 = 0 却已存在 `PMNet_RpcBody_<M>`；版本 = 1 却缺业务体或 wrapper 未改写 |
| 签名不支持 | `static` / `virtual` / `abstract` / `extern`-native / `async` / 泛型 / 无体 / 非 void / 构造函数 |
| 参数不支持 | `ref` / `out` / `in` / `params` / 默认值 / 指针 |
| 重载 | RPC 同名重载；一个方法带多个 RPC 标记 |
| helper 形态 | 发送 helper 是 `public`（冻结格式要求 private）；helper 未**恰好一次**调用原方法 |
| 程序集 | 强名称（公钥）；不是托管 PE；无法处理的 PE |
| 符号 | PDB 存在但读不到符号；非 portable（如 Windows native PDB）⇒ 明确失败，不静默丢也不偷偷换格式 |

---

## 4. 真实编译 + 真实执行的证据链

### 4.1 夹具与生成物

- 夹具声明源：`Tools/PMNetWeaverTest/Fixture.cs`（`[PMNetworkObject] WeaveFixture` + **7 条 RPC**：
  3 个 Server / 2 个 Client / 1 个 Multicast + 1 个 Server(Branch)）。
- 生成物：门禁在运行期调**现有** `PMNetGen --decl-gen` 产出（本文件不复制生成逻辑）。
  本次生成期整体协议摘要 = **0x4CCAD4FD**。
- 夹具程序集：`netstandard2.0` + `LangVersion 7.3`，Debug 与 Release **各编一次**，两者都有 portable PDB。
  PMNet 运行时被编成独立程序集 `PMNet.Runtime.Temp`（与 Unity 形态同构：业务程序集引用运行时）。

### 4.2 夹具内行为链（47 个用例，逐条断言）

| 组 | 覆盖 |
|---|---|
| 结构面 | stamp=1；`PMNet_RequireRpcWeave()` 返回 1；`PMNet_RpcBody_*` 存在且非 public；发送 helper 非 public；业务体不带 RPC Attribute |
| 注册 | `RegisterAll` 后 `ClassCount=1 / RpcCount=7`；`ServerAttack` 注册档位 = `ForceValidate`；8 个稳定 ID 与全局摘要上报 |
| 普通调用发包 | 客户端对象调 `ServerAttack(...)` ⇒ 本端**不执行**、进待发队列；再把队列项编码成字节并解码校验参数 |
| 同步编码 | 接线 `RemoteSender` 后同一次调用**同步**编码出正确参数（且不本地执行、不进队列） |
| 收包 | 字节 → `PMNetRpcReceive.Deliver` → 校验 → 业务体；`Applied` / 参数正确 / 计数 +1；再投一次仍「每次一次」⇒ **不递归** |
| 归属 | 非 Owner 连接投递 ⇒ `NotOwner` 且不执行 |
| 三态校验 | Reject ⇒ 跳过实现、**不断连**、上报 `ServerAttack:Reject`；Report ⇒ 上报且**仍执行**；Accept ⇒ 静默；`(PMRpcValidation)99` ⇒ **失败关闭**（不执行） |
| 原生校验 | `_Validate` false ⇒ `ValidationFailed` + **请求断连**（落到连接的 `RequestRpcDisconnect`）且不执行；true ⇒ 执行 |
| 数组参数 | 多播本地执行看到**调用时**的值；调用方数组被业务体就地改写；外发载荷里仍是**调用时**的值；远端副本收到调用时的值；`null` 数组在收包侧被 Reject |
| 业务体形态 | `switch` + `for` + `try/catch/finally` + 委托（闭包）：本地与远端**结果一致**；负数计数走 catch；finally 每次执行 |
| string 参数 | 下行 RPC 的字符串参数经收包链正确还原 |
| 下行 RPC | 服务端调 Client RPC ⇒ callspace = `Remote` ⇒ 载荷 → 客户端副本 ⇒ 执行一次、参数正确 |
| 委托入口 | `Action<int> d = obj.ServerAttack; d(13);` ⇒ 仍走网络入口（不本地执行、入队 1） |

### 4.3 未编织形态（`RunPreWeave`，6 个用例）

`PMNet_GetRpcWeaveVersion()==0`；`new WeaveFixture()` **抛** `InvalidOperationException`；
`PMNetGeneratedRegistry.RegisterAll()` **抛**（栈里能看到 `PMNet_BuildEntry → PMNet_RequireRpcWeave`）；
`ClassCount==0`；没有 `PMNet_RpcBody_ServerAttack`；发送 helper 已是非 public 的冻结形态。

### 4.4 独立 PDB 证据（第三方读取器）

用 `System.Reflection.Metadata`（**不是** Cecil）逐方法解析写出的 PDB：

```
TOTAL methods=96 corrupt=0
ok  WeaveFixture.PMNet_RpcBody_ServerAttack   sp=23  first[off=0 (89,9)]  last[off=93 (112,9)]
ok  WeaveFixture.PMNet_RpcBody_ServerBranch   sp=45  first[off=0 HIDDEN]  last[off=299 (262,9)]
ok  WeaveFixture.PMNet_RpcBody_MulticastPush  sp=10
ok  WeaveFixture.PMNet_RpcBody_ServerProbe    sp=4
ok  WeaveFixture.PMNet_RpcBody_ServerWarp     sp=3
ok  WeaveFixture.PMNet_RpcBody_ServerShout    sp=4
ok  WeaveFixture.PMNet_RpcBody_ClientNotify   sp=4
ok  WeaveFixture.ServerAttack                 sp=0   ← wrapper（见 §5.1：合成转发桩，行号已清）
ok  WeaveFixture.PMNet_GetRpcWeaveVersion     sp=0
ok  WeaveFixture.PMNet_BuildEntry             sp=103
```

- 克隆业务体的 sequence point **逐条搬移**：门禁先量未编织 `ServerAttack` 的条数，编织后要求
  `PMNet_RpcBody_ServerAttack` 条数**完全相等**（本次 23 == 23）；Release 同样通过（16 == 16）。
- 克隆体里 `switch` 跳转表、`try/finally` 区间、闭包类引用都被正确重映射（`ServerBranch` 45 条 SP 全部落在真实指令偏移上）。

### 4.5 幂等 / 零写 / 原子性证据

| 断言 | 结果 |
|---|---|
| 未编织时 `--check` 必须失败且 DLL/PDB 哈希不变 | 通过 |
| `--weave` 后 DLL 与 PDB **都**确实改变（未静默丢符号） | 通过 |
| `--check`（已编织）退出 0 且 DLL/PDB 哈希不变 | 通过 |
| 二次 `--weave` 报告「是否实际改写：否」，DLL/PDB **逐字节不变** | 通过 |
| 每个负例：`--weave` 非 0、错误信息命中**预期关键词**、DLL/PDB 哈希不变 | 通过 |
| 失败后不残留 `*.pmweave-*.tmp` / `*.pmweave.bak` | 通过 |

负例族的实际失败信息（节选，证明**失败的是预期那条检查**）：

```
未知版本      : PMNet_GetRpcWeaveVersion 返回未知版本 2：只支持 0 / 1。未知版本必须明确失败，不能用 define 跳过。
假 stamp      : 返回 1（声称已编织），但缺少私有业务体 PMNet_RpcBody_ClientNotify：损坏/半编织，明确失败。
缺 guard      : 声明了 PMNet RPC，但缺少生成物方法 PMNet_GetRpcWeaveVersion()（有 RPC 无生成/无 guard ⇒ 明确失败）。
坏收包 helper : 收包 helper ...PMNet_RpcInvoke_ServerAttack 必须**恰好一次**调用 ServerAttack（实际 0）。
坏发送 helper : 发送 helper ...PMNet_ServerAttack 必须**恰好一次**调用 ServerAttack（实际 2）。
强名称        : 输入程序集带公钥（强名称）：编织会破坏签名，契约明确不支持。
无 RPC        : --weave 退出 0 且不写盘；--require-rpcs 非 0 且不写盘。
无 PDB        : 编织成功、DLL 改写、**不凭空产生 PDB**，夹具仍能跑完 47 个用例。
非 PE / 不存在: 明确失败（退出 1 + “失败：...”信息），文件未被改写。
--help        : 退出 0 并打印冻结 CLI 契约；缺参数值 ⇒ 退出 2。
```

### 4.6 稳定 ID 不变

门禁在**未编织**与**编织后**分别用反射读取 `PMGeneratedClassId` / `PMGeneratedRpcId_*`
（8 个常量）并逐项比对，同时把运行期 `PMNetRegistry.ProtocolHash` 与生成物头部摘要比对：
**编织前后完全一致**（本次均为 `0x4CCAD4FD`）。编织器只改方法体与版本方法，不触碰 ID/描述符/摘要。

---

## 5. 本轮发现（含两个「不是猜的」的实证结论）

### 5.1 发现 F1：Mono.Cecil 0.11.6 的 portable-PDB 写入器在「替换某方法的 sequence point 集合」时产出**损坏的 SP blob**

**现象**：编织后的 wrapper 方法在 PDB 里的 sequence point 无法解析。用**独立读取器**
`System.Reflection.Metadata` 读同一份 PDB 得到 `BadImageFormatException: Invalid compressed integer`
（Cecil 自己的读取器则给出「隐藏 SP + 一行乱码」的错值，两者都说明 blob 真的坏了）。

**最小实验隔离**（同一输入、同一写入参数，只改一处；结果都由独立读取器判定）：

| 对 sequence point 集合做的操作 | Blob |
|---|---|
| 完全不动（round-trip） | 有效 |
| `Clear()`（→ 0 条） | **有效** |
| 原地改某条 SP 的值（条数不变） | **有效** |
| `Clear()` + `Add(新 SequencePoint)`（换成另一组非空集合） | **损坏** |
| 保留 1 条（RemoveAt 其余）+ 原地改值（条数 20 → 1） | **损坏** |

**结论**：这不是夹具错误，是 Cecil 0.11.6 侧缺陷（新建/替换非空 SP 集合时 blob 堆偏移/编码不一致）。

**应对（已实现，且在写盘前拦住）**：

1. wrapper 与版本方法一律**清空而不新增** sequence point（它们是两三条指令的合成桩，
   真正的行号信息完整保留在 `PMNet_RpcBody_<M>`），实测 PDB 有效；
2. 克隆业务体的 SP 集合与源方法的 blob **逐字节相同**（同偏移、同行列）⇒ 不引入新 blob；
3. 落盘前用 `System.Reflection.Metadata` **独立校验**暂存 PDB（每个方法的 SP 与局部作用域都要能解析，
   且克隆体的 SP 条数必须等于源方法），任何异常 ⇒ **拒绝提交**（不写出去）。

> 这条经验对 P2 直接有用：**不要给被改写的方法塞一组「新的非空 sequence point」**，
> 否则会静默写出坏 PDB（而 Cecil 自己读回来还可能「错得一致」看不出来）。

### 5.2 发现 F2：`File.Replace`（Windows `ReplaceFile`）会让**同一路径**在替换后仍加载到旧程序集

**现象**（已用最小实验复现）：在同一个进程里

1. 用 `AssemblyLoadContext.LoadFromAssemblyPath(P)` 加载旧 DLL；
2. 用 `File.Replace(newDll, P, null)` 把 P 换成新 DLL（文件长度已变、Cecil 读到的也是新内容）；
3. 在**新的** ALC 里再次按同一个 `P` 加载 ⇒ **拿到旧内容的程序集**；把同一份新文件拷到另一个路径再加载 ⇒ 正确。

**原因**：`ReplaceFile` 语义让目标路径保留原来的**文件身份**，而宿主可能按文件身份缓存已加载的程序集。

**应对**：

- 编织器改用**显式备份 + `File.Move(..., overwrite: true)`**（重命名语义，目标获得源文件的身份），
  失败时从备份回滚 ⇒ 既无「半程序集」也没有身份陷阱；顺带修掉了替换后残留 `.pmweave.bak` 的问题；
- 门禁每次加载前把目标 DLL（+PDB）**拷到一个全新路径**再加载，结论不受宿主缓存影响。

> 这条对 **P2 的 Unity Editor 接线**是硬提醒：Editor 在 `assemblyCompilationFinished` 里编织后，
> 若用 `ReplaceFile` 语义换掉磁盘 DLL，域名重载后**可能仍跑旧代码**，而磁盘文件看上去已经换新。

### 5.3 发现 F3：生成器侧必须改的三件事（P2 前置条件），其中一个有连锁影响

1. 发送 helper 从 `public` 改成 **private**（否则本编织器按冻结格式**明确拒绝**，本次负例已验证）；
2. 每个含 RPC 的 partial 类产出 `PMNet_GetRpcWeaveVersion()` 与 `PMNet_RequireRpcWeave()`；
3. `PMNet_BuildEntry` 开头调用 `PMNet_RequireRpcWeave()`。

**连锁影响（需要 P2 一并处理）**：一旦发送 helper 变 private，**gate/测试里直接调用
`obj.PMNet_<M>(...)` 的既有代码会编译不过**（本次已确认 `Tools/PMNetE2E` 及若干门禁就是这种写法）。
按契约这不是回归而是迁移项（这些调用应当改成普通名 `M(...)`），但必须**与生成器改动同批完成**，
否则会出现「生成器改了、门禁编不过」的中间态。

### 5.4 发现 F4：版本方法的 IL **形态随配置不同**，不能按固定指令序列读版本

Debug 下 `internal static int PMNet_GetRpcWeaveVersion() { return 0; }` 编成
`nop; ldc.i4.0; stloc.0; br.s <end>; ldloc.0; ret`，Release 下才是 `ldc.i4.0; ret`。
最初按「恰好 `ldc.i4 N; ret`」读版本会在 Debug 上直接失败。现判据改为三条更稳的不变式：
**恰好一个 `ret`、不含任何调用、整型常量恰好一个**；读错 0/1 也不会静默放过（必然被结构校验抓住）。

### 5.5 发现 F5：几处运行时语义（不是缺陷，但接错会「看起来没生效」）

1. **Client 方向 RPC 在「无连接」时 callspace = Local**（`PMRpcDispatch` #14 ①：UE 的
   「AI 拥有的对象调 Client RPC → 本端执行」）。因此「服务端调 Client RPC 应该发下行」这句
   只在对象**能推导出连接**时成立（需要 `Owner` 链，而不是只设 `OwnerConnection` 字段）。
   本夹具为此加了 `ConnCarrier`（与 `PMNetE2E` 同一手法），否则该用例会变成「只本地执行」。
2. **Multicast / Client 方向的 RPC 不能投给服务端世界**：`PMNetRpcReceive.Deliver` 在
   `world.IsServer` 时只接受 `Server` 方向，其它方向直接 `WrongDirection`。接收侧测试必须用
   真实的**非服务端副本**（本夹具用服务端 `BuildLifecycleBatch` + 客户端 `OnLifecycleMessage` 创建）。
3. **校验只在收包侧执行**：调用桩的本地分支直接进业务体，不走 `_ForceValidate`/`_Validate`
   （与生成物一致，非本轮引入）。因此「Reject 跳过实现」这类断言必须在**投递路径**上做。

---

## 6. 未验证 / 明确不支持（诚实口径）

- **不支持**（按契约明确拒绝）：虚方法 / 网络继承 / 跨 assembly 的 RPC 定义 / 同名重载 /
  `ref|out|in|params|default` 参数 / 泛型 / async / 强名称 / 非 portable PDB。
- **未验证**：embedded PDB（代码路径写了对应 writer provider，但**没有实测用例**）；
  Windows native PDB 只做了「拒绝」路径（无样例可编）；
  多模块程序集、`netstandard2.0` 之外的 TFM。
- **未做（属 P2/P3）**：Unity Editor 的 `assemblyCompilationFinished` / Player 的
  `IPostBuildPlayerScriptDLLs` 接线、IL2CPP/Mono 实机运行、MSBuild 增量目标、
  真实 PMR3 迁移与 ID 锁、decl-check、E2E/R3/R4/R5/R6 回归。
  **本报告不声称任何 Unity 侧结论**，也不声称真实双端联机通过。
- 夹具的 `Fixture.cs` / `Fixture.GeneratedGuard.cs` 是**测试夹具**，不代表生产作者写法定稿；
  生产源码（PMR3 等）本轮**一行未改**。

---

## 7. 给 P2 的建议清单（按依赖顺序）

1. **生成器**（`Tools/PMNetGen`）：发送 helper 改 `private`；每个含 RPC 的 partial 类产出
   版本/守卫方法；`PMNet_BuildEntry` 开头插 guard；`--decl-check` 只读校验这些新契约。
2. **同批迁移**直接调用 `PMNet_<M>` 的既有代码（`Tools/PMNetE2E`、各 gate、业务源码）到普通名调用，
   并同步 `Docs/plans/net-r2-codegen-contract.md` §4.1/§4.3 的 API 面描述。
3. **构建接线**：`.NET` 目标在 DLL 复制完成后 `--weave` + `--check`；**只有**包含生成 RPC 集合的目标参与，
   排除工具自身及其依赖（防递归）；用独立输出目录，避免覆盖运行中的 `Server.dll`。
4. **Editor 接线**：优先核实 Unity 2019.4 的 `IPostBuildPlayerScriptDLLs`；API 不支持就报 BLOCKED，
   不要用 post-process 改包替代。**注意 F2**：替换磁盘 DLL 不要用 `ReplaceFile` 语义。
5. **回归**：把 `Tools/PMNetWeaverTest` 纳入常用门禁（本次 build 成功后再 run），
   并新增「真实 PMR3 编织 + decl-check + ID 锁不变 + E2E/R3/R4/R5/R6」的集成门禁。

---

## 8. 复现与门禁自身

- 独立输出构建（避免与并行工作串输出）：`dotnet build Tools/PMNetWeaverTest/PMNetWeaverTest.csproj -c Release -o <目录>`；
  **build 退出 0 之后**再 `dotnet <目录>/PMNetWeaverTest.dll`。
- 工具 DLL 定位：门禁优先用测试输出目录里的副本，其次 `Tools/<工具>/bin/{Release,Debug}/net8.0`，
  多个候选取**最新写入**的那个（避免跑到旧 DLL 打假绿），并打印实际选用路径。
- 门禁自身防「空跑」：要求 `_passed > 0`；驱动用例数必须**恰好等于**预期集合（47 个），
  少一个 / 多一个都判失败；负例必须命中**预期关键词**（不能因为撞上别的错误而「通过」）。
- 临时夹具与产物只落在 `%TEMP%/pmweaver-test/**` 与 `%TEMP%/pmweaver-test-out/**`；
  不写 `Client/Assets/**`、不碰 Unity `Library`、不启动 Unity / DS / Lobby、不提交、不暂存。
