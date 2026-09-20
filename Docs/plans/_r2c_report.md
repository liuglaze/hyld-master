# R2-C 端到端总门禁 · 交付报告

> 任务：在 `D:/UGit/hyld-master` 新建 `Tools/PMNetE2E/`，把「声明 → 生成 → 复制收敛 → RPC 双向」
> 串成一条可执行验收，收口主计划 §6.1 的 **T40 / T41**。
> 契约依据：`Docs/plans/net-r2-codegen-contract.md`、`Docs/plans/net-r0-contract.md` §2.4/§5。

## 0. 交付物一览

| 文件 | 说明 |
|---|---|
| `Tools/PMNetE2E/PMNetE2E.csproj` | net8.0 可执行工程；**pre-build target 调 PMNetGen 生成** |
| `Tools/PMNetE2E/E2eFixtures.cs` | 声明夹具（2 个 `[PMNetworkObject]`、5 个复制属性、2 条 RPC） |
| `Tools/PMNetE2E/Program.cs` | 门禁本体（6 段，**89 项断言**，含 3 条负向注入） |
| `Tools/PMNetE2E/Generated/**` | PMNetGen 产物（2 个类 partial + 1 个程序集注册表） |
| `Tools/PMNetE2E/e2e-ids.json` | 本夹具专属 ID 锁文件（**不**动 `Docs/plans/pmnet-ids.json`） |

未修改：`Client/Assets/Scripts/PMNet/**`、`Tools/PMNetGen/**`、`Tools/PMDeclModel/**`、
`Tools/PMDeclCheck/**`、`Tools/PMReplicationTest/**`、`Docs/plans/pmnet-ids.json`。

## 1. 已检查范围（读了什么 + 如何决定实现方案）

### 1.1 文档

| 文档 | 读了哪一部分 | 对实现的决定性影响 |
|---|---|---|
| `AGENTS.md`（仓库根） | 通读（5 行） | 三份入口文档的路径 |
| `net-r2-codegen-contract.md` | **逐节通读**（304 行） | 决定生成物 API 面（§4.1 `PMNet_Set_*` / `PMNet_OnRepDispatch` / `PMNet_RpcInvoke_<M>` / `PMNet_<M>`）、§4.3.1 **实参闭包**不变性（→ P 段 + 注入①）、§4.3.2 **三态路由动作差异**（→ Q 段）、§7-C 的边界 |
| `net-architecture-migration.md` §5 的 R2 行 | 第 851 行 | 确认「剩余：端到端总门禁（T40/T41 收口）」就是本任务 |
| 同上 §5.4 / §5.5 | 完整两节 | §5.5 #1 给出「实参帧」缺陷的**精确形态**⇒注入①替身照此复刻；§5.5 #2「只记档位不发射校验调用」⇒注入②照此复刻；§5.4 两条「机制差异」说明「值未变也补发一次」**符合契约字面**⇒ M 段断言按此写 |
| 同上 §6.1 的 T40 / T41 两行 | 两行 | T40：无手写注册 / 二次生成无 diff / 运行不扫反射；T41：最终 C / 旧 ACK 不清新脏版本 / 条件隔离 / OnRep 按契约触发 ⇒ 断言清单逐条对齐 |
| 同上 §9.5 | 教训 7 / 16 / 26 / 28 / 31 | 7⇒`GeneratedClassCount` 交叉断言；16⇒`FindRepoRoot()` 向上找根；26⇒负向验证块整块重写过一次；28⇒加「前提真的成立」的对照断言（J2/M1/M2）；31⇒验收先看 build 错误数 |
| `net-r0-contract.md` §2.4 | D-R0-12..18 全表 | D-R0-13⇒L 段（标脏仍要比较）；D-R0-15⇒M 段（跃迁全掩码）；D-R0-16⇒客户端副本由**生命周期消息**创建 |
| 同上 §5 | 复制与生命周期契约表 | 「ACK 只能前进，旧 ACK 不得清除新脏位」⇒K 段；「首次进入范围 ⇒ 全量」⇒ NewRig 建链方式 |

### 1.2 已落地代码（只取公开 API 面）

| 文件 | 读法 | 用途 / 结论 |
|---|---|---|
| `PMNet/Declarations/PMNetDeclarations.cs` | `grep "public\|internal"` | 取 `PMPropertyDescriptor`、`PMNetClassEntry`、`PMNetRpcEntry.Invoke`（`PMRpcInvoker`）、`PMRpcValidationSink` 两钩子 + `Unhandled`、`PMNetString.MaxBytes` |
| `PMNet/PMRpc.cs` | 公开签名 + 分支体第 360–492 行 | **确认 Remote 判定**：客户端侧 `Server` RPC ⇒#16 Remote；服务端侧有连接的 `Authority` 调 Client RPC ⇒ Remote；服务端侧**无主**对象调 Client RPC ⇒#14-① **本地执行**（这条纠正了初版的错误预期） |
| `PMNet/Replication/PMReplicationChannel.cs` | `grep "public\|protected virtual"` + `BuildUpdate`/`OnAck`/`TryClearSettledDirty`/`ResolveViewRole` 片段 | 取 `Tick`/`BuildUpdate`/`OnAck`/`BuildAckMessage`/`TryGetAckedVersion`/`TryGetBaselineValue`/`GetInflightVersions`/`Stats`；确认 5 个 `protected virtual` 决策点⇒注入③用 `HasValueChangedSinceBaseline` 的缺陷子类 |
| `PMNet/World/PMNetWorld.cs` | 公开签名 + `Spawn`/`AddConnection`/`BuildLifecycleBatch`/`RegisterClass` | 建链方式；另确认 `RegisterClass` 要求非 null 工厂、重复 ClassId 抛异常 |
| `PMNet/PMNetObject.cs` | 公开签名 + `MarkPropertyDirty`/`GetNetConnection`/`ShouldReplicateProperty` | 标脏入口；`GetNetConnection` 走 `Owner` 链⇒`ConnCarrier` 替身；`OwnerConnection`⇒条件跃迁 |
| `PMNet/Replication/*.cs`（另 4 个）、`PMNetConnection/Writer/Reader/Identity/Property.cs` | 公开签名 | 连接替身、载荷解码、槽位/条件表 |

### 1.3 写法范例

| 文件 | 读法 | 借用之处 |
|---|---|---|
| `Tools/PMDeclCheck/Fixtures/Good.cs` | 头部 60 行 + 第 70–228 行 | `[PMRepNotify(nameof(_x))]` 写法；`Validator = ForceValidate` **必须配 `_ForceValidate` 同伴**（规则 13）的范例 |
| `Tools/PMReplicationTest/Program.cs` | 头部 60 行 + 第 80–300、400–560、1128–1172 行 | **整套替身与投递范式**：`TestConn` 手指针连接、`Rig`/`AddClient`、`Deliver` 回灌 ACK、`Tick()` 外发、`Decode`/`SentSlots`，以及它的 `FaultCase` 负向验证骨架 |
| `Tools/PMDeclCheck/PMDeclCheck.csproj` | 通读（48 行） | `EnableDefaultCompileItems`/`LangVersion`/`TargetFramework` 写法；netstandard2.0 引用集取法（§4 段照搬该口径） |
| `Tools/PMNetGen/DeclEmitter.cs` | `grep` 关键发射点（第 120–240、400–470、500–600、640–710、750–800 行） | **确认生成物确切形状**：`PMNet_Set_<成员名>`（含前导下划线）、`private static PMNet_OnRepDispatch`、`private static PMNet_RpcInvoke_<M>`、`EnqueueRemote`+`TryDequeueRpc`、`PMNet_BuildEntry` ⇒ 由此决定「OnRep 开 partial 转发口」「接收侧走 `PMRpcEntry.Invoke`」 |
| `Tools/PMNetGen/Program.cs` | 参数解析（第 78–100 行） | 确认声明模式接受**目录或 `.cs` 文件**⇒ 夹具钉到单文件 |

## 2. 实现概要

### 2.1 生成方式（二选一，本项目选 **csproj pre-build target**）

`PMNetE2E.csproj` 的 `PMNetGenE2eFixtures` target 在 `BeforeCompile` 前执行
`PMNetGen --decl-gen <PMNetE2E/E2eFixtures.cs> --out-dir <PMNetE2E/Generated> --id-lock <PMNetE2E/e2e-ids.json>`。

**为什么不用运行时 `Process.Start` 生成**：产物必须参与本工程编译（`<Compile Include="Generated/**/*.cs" />`），
而 `Compile` 通配符在**工程求值期**展开 —— 改成运行期生成，夹具里对 `PMNet_Set_*` / `PMNet_<M>` /
生成描述符的引用会在**首次编译**时找不到符号。

**为什么扫描文件而不是目录**：任务书写的是 `<夹具目录>`，但生成物落在我自己的目录里，
传目录会把 `Program.cs` 一并当声明源扫。PMNetGen 的声明模式本身接受「目录或 `.cs` 文件」，
传单文件不改变语义，只是让「夹具里声明了什么」可复核。

**运行时再加一道校验模式**（只校验不写盘，逐字节比对）：把「已编进 DLL 的产物」与
「此刻的声明 + 锁文件」钉在一起，即 §2.3 的「二次生成零 diff」不变性在端到端层再验一遍。
另加交叉断言 `PMNetRegistry.ClassCount == PMNetGeneratedRegistry.GeneratedClassCount`（生成期常量），
防住「新增 `.g.cs` 没被编进来」（§9.5 教训 7）。

**必须补的一处（实测踩到）**：`Compile` 通配符在**工程求值期**展开，而生成目标跑在编译前 ——
在「checkout 里还没有 `Generated/`」的首次构建里，通配符展开为空，目标虽然生成了文件却不会被编译，
表现为一堆 `CS0103: 不存在名称 PMNet_OnRepDispatch` / `CS0117: 未包含 PMGeneratedClassId`。
已用「求值期快照」修掉：求值期把 `Generated/**/*.cs` 记进 `PME2eGeneratedAtEvalTime`，
生成目标在**求值期确实为空**时才补一次 `Include`（有产物时不补，避免重复）。
**已实测**：删掉 `Generated/` 与 `e2e-ids.json` 后直接构建 ⇒ `0 个警告 0 个错误`，
门禁仍 `89 项 / 0 失败`。

### 2.2 夹具（`E2eFixtures.cs`）

| 声明 | 覆盖 |
|---|---|
| `[PMReplicated] int _health` | 整数 varint；「值未变不发」「旧 ACK 不清新脏位」用它 |
| `[PMReplicated] float _speed` | 浮点 fixed32；收敛断言用它 |
| `[PMReplicated] string _title` | 字符串 length-delimited（自带 `PMNetString.MaxBytes` 门） |
| `[PMReplicated(PMCond.OwnerOnly)] int _ammo` | **条件必须真能翻转**，否则 D-R0-15 的跃迁路径永远走不到 |
| `[PMRepNotify(nameof(_health))] void OnRep_Health()` | 规则 5/6；计数器证明「真被调用一次」 |
| `[PMServerRpc(Validator = ForceValidate)] Fire(int, float)` + `Fire_ForceValidate` | 规则 8/13；三态按 `targetId` 分流（`<0` Reject / `==7` Report / 其余 Accept），使**同一段断言代码**分别走通三条分支 |
| `[PMClientRpc] Notify(int)` | 方向位 |
| 第二个类 `E2eScoreboard` | 单类时「显式列出每个类」与「碰巧只有一个」无法区分 |

生成结果：`ClassId=2761782480`（props=4, rpcs=2, maskBits=4）、`ClassId=2767219303`（props=1）；
生成期 `GlobalProtocolHash = 0x71692EA4`，运行期复算一致。

### 2.3 复制驱动方式与 6 段结构

与 B 块门禁同构：`TestConn`（记 `Sent`）手指针连接 + `PMNetWorld`（服务端 `Spawn`；
客户端由 `BuildLifecycleBatch` → `OnLifecycleMessage` 创建副本）+ `PMReplicationChannel`
（`Tick()` 外发、`Deliver()` 投递并回灌 ACK）。**手工投递**让「落后若干次更新」「迟到/乱序 ACK」
这类时序可确定性构造。

| 段 | 内容 | 断言数 |
|---|---|---|
| [0] | 生成物与当前声明逐字节一致（校验模式） | 4 |
| [1] | 零反射注册表：类/属性/方向/档位/协议摘要 | 9 |
| [2] | 复制收敛 T41：J 不同基线最终一致 / K 旧 ACK 不清新脏位 / L 值未变不发 / M 条件跃迁补发 / N OnRep 真被调用 | 33 |
| [3] | RPC 双向 T40：O 调用桩与 callspace / P 实参不被覆盖 / Q 接收侧三态 / R 归属校验 | 21 |
| [4] | 语言面：生成物 + 夹具 + PMNet 运行时在 **C# 7.3 + netstandard2.0** 下真编译 | 2 |
| [5] | 负向验证：3 个缺陷各一对探针，打印注入前后失败数 | 10 |
| [6] | 门禁自身：9 类「实际跑到的数量」逐条 > 0 | 9 |

## 3. 验收输出

`dotnet build` 的 **错误数先看**（§9.5 教训 31：编译失败时可能跑到旧二进制给出假绿灯），全部为 **0**。

```
$ dotnet build Tools/PMDeclModel -c Release         → 0 个错误
$ dotnet build Tools/PMNetGen -c Release            → 0 个错误
$ dotnet build Tools/PMNetE2E -c Release
  [PMNetGen] 声明扫描：文件 1 个（语法错误 0 处），网络类 2 个，复制属性 5 个，RPC 2 条
      ClassId=2761782480  PMNetE2E.E2eReplicated  props=4  rpcs=2  maskBits=4  hash=0x50AF2E09
      ClassId=2767219303  PMNetE2E.E2eScoreboard  props=1  rpcs=0  maskBits=1  hash=0xDCD02534
  [PMNetGen] 已生成 3 个文件到 D:\UGit\hyld-master\Tools\PMNetE2E\Generated
  [PMNetGen] 已写出 ID 锁文件 D:\UGit\hyld-master\Tools\PMNetE2E\e2e-ids.json
  [PMNetGen] 生成期整体协议摘要 GlobalProtocolHash = 0x71692EA4
已成功生成。 0 个警告 0 个错误
$ dotnet Tools/PMNetE2E/bin/Release/net8.0/PMNetE2E.dll
=== 汇总 ===  通过 89 项，失败 0 项   结果：PASS      （进程退出码 = 0）
$ dotnet build Tools/PMClientCheck -c Release       → 0 个错误
```

**干净树复现**：删掉 `Tools/PMNetE2E/Generated/` 与 `Tools/PMNetE2E/e2e-ids.json` 后重跑整条验收，
构建仍 `0 个警告 0 个错误`、门禁仍 `通过 89 项，失败 0 项 / 退出码 0`（见 §2.1 的求值期快照说明）。

### 3.1 门禁输出（节选；完整输出可由上面的命令原样复现）

```
── 0. 生成物与当前声明逐字节一致（PMNetGen 的声明校验模式，只读）
    OK   PMNetGen.dll 存在 / 夹具文件存在 / ID 锁文件存在
    OK   声明校验模式退出码 0（产物与锁文件都与当前声明逐字节一致），实际 0

── 1. 声明 → 注册表：零反射注册后计数与生成物一致
    OK   RegisterAll 之后注册表已封板
    OK   注册类数 == 生成期常量 GeneratedClassCount（2 == 2）
    OK   生成描述符里的复制属性总数 == 5（主类 4 + 第二类 1），实际 5
    OK   主夹具 ChangeMaskBitCount == 4，实际 4
    OK   运行期 ProtocolHash 与生成期一致：0x71692EA4
    OK   Fire 注册为 Server 方向 / Validator 档位来自声明（ForceValidate）/ 为可靠 RPC
    OK   Notify 注册为 Client 方向

── 2. 复制收敛（T41）                        [33 项全 OK，摘关键]
    OK   J2 慢连接确实落后（health=0 vs 50）—— 这条保证「不同基线」前提真的成立
    OK   J3 慢连接的落后更新仍在待投递队列里（5 个载荷）
    OK   J4/J5/J6 慢连接最终收敛 / 两条连接最终一致（int/float/string）/ 与权威值一致
    OK   K4 基线被推进到 v1 的**值**（100），而不是当前值（200），实际 100
    OK   K5 旧 ACK 之后新脏位仍在（health 槽位仍为脏）—— R0 §5「旧 ACK 不得清除新脏位」
    OK   K6 旧 ACK 之后下一轮仍补发 health，实际槽位 [3]
    OK   K8 重复的旧 ACK 被判为过期（StaleAckIgnored 0 → 1）；K9 版本号未回退
    OK   L2 值未变（仅标脏）⇒ 无载荷，实际发了 0 个
    OK   L3 被抑制的计数确实增长（SuppressedUnchanged 0 → 3）
    OK   L4 写回同值（走生成物 PMNet_Set_health）⇒ 仍无载荷
    OK   L5 真的改值 ⇒ 一定有载荷（对照，1 个）；L6 客户端收到新值 43
    OK   M1 非 Owner 连接上 OwnerOnly 属性被条件过滤，实际槽位 []
    OK   M3 条件跃迁后补发 _ammo（值未发生新变化也补发），实际槽位 [2]
    OK   M4 跃迁补发计数增长（0 → 1）；M5 客户端拿到补发的当前值 5
    OK   N1 属性变化后 RepNotify 正好被调用一次（1 → 2）
    OK   N2 复制层的 OnRep 分发计数增长（1 → 2）；N3 客户端值已更新（888）

── 3. RPC 双向（T40）                        [21 项全 OK，摘关键]
    OK   O1 客户端侧对象调 Server RPC 的 callspace 判定为 Remote（Remote）
    OK   O2 调用桩把该次调用放进了待发队列（PendingRpcCount=1）
    OK   O3/O4 服务端侧调 Server RPC 不入队（本地执行），实现确实执行
    OK   O5/O6 服务端侧**无主**对象调 Client RPC 本地执行、不外发（无收件人）
    OK   O7 服务端侧**有连接**的对象调 Client RPC 进待发队列（方向位生效）
    OK   P4 ★核心不变性：第一次的实参未被后续调用覆盖（收到 11 / 1.5，期望 11 / 1.5）
    OK   P6 第二次调用的实参也正确（收到 99 / 9.5）
    OK   Q1/Q2 Accept ⇒ 实现执行一次、不产生上报
    OK   Q3/Q4/Q5 Reject ⇒ 实现**未**执行、上报 Reject、没有请求断连（OnValidateFailed 未被调用）
    OK   Q6/Q7/Q8 Report ⇒ 实现仍然执行、上报 Report、也不请求断连
    OK   Q9 Report 路径上实参读取正确（7 / 2）
    OK   R1 非 Owner 连接发来的 Server RPC 被拒（服务端 + 不是对象拥有者）
    OK   R2/R3 Owner 连接接受 / IgnoreRpcs 下即使 Owner 也被拒
    OK   R4 客户端侧不额外判归属（能收到说明服务端已放行）

── 4. 语言面：生成物 + 夹具 + PMNet 运行时在 C# 7.3 + netstandard2.0 下真编译
    OK   编译引用集可用：netstandard2.0 引用程序集（113 个）
      编译输入：生成物 3 个 + 夹具 1 个 + PMNet 运行时 26 个
    OK   生成物 + 夹具 + 运行时在 C# 7.3 下零编译错误（实际 0 个）

── [6] 门禁自身：实际跑到的数量必须全部 > 0（不得「0 个用例也通过」）
    类数=2  复制属性数=5  RPC 数=2  复制载荷数=25  复制记录数=22
    OnRep 分发数=2  RPC 入队数=4  RPC 执行数=6  校验上报数=2       [9 项全 OK]
```

**退出码语义**：`failures.Count == 0 && passed > 0` ⇒ 0，否则 1。已实测「失败 0 ⇒ 退出码 0」。

## 4. 负向验证结果（逐条）

每个缺陷配一对探针：`prod`（生产形态 = 生成物 / 运行时真实现）与 `faulty`（缺陷形态替身）。
每组先只跑 `prod`（注入前），再跑 `prod + faulty`（注入后），打印两次失败数。

| 注入 | 缺陷形态（复刻自） | 注入前失败 | 注入后失败 | 命中断言 | 结论 |
|---|---|---|---|---|---|
| ① 实参被覆盖 | 实参暂存在对象字段、编码推迟到发送时从字段读出 —— 复刻 §5.5 #1 的返工前生成物 | 0 | 1 | `F2 实参帧（返工前形态） → 第一次的实参被覆盖：收到 99 / 9.5，期望 11 / 1.5` | **命中** |
| ② Reject 后仍执行了实现 | 接收侧只把档位写进描述符、不发射校验调用，直接调实现 —— 复刻 §5.5 #2 | 0 | 1 | `F4 只记档位、不发射校验的形态 → Reject 之后实现仍然被执行（FireCount=1）` | **命中** |
| ③ 值未变却发了载荷 | 复制层跳过值比较（`HasValueChangedSinceBaseline` 恒 true） | 0 | 1 | `F6 不做值比较的复制层 → 值未变却发了 1 个载荷` | **命中** |

**三条全部命中，无未命中。** 每条注入另带一条对照断言：`prod` 探针（F1 / F3 / F5）在
**注入下仍然通过** ⇒ 失败确实来自被注入的那一处，而不是整组崩掉。原始输出：

```
    [注入前] 三条生产形态探针（F1/F3/F5），失败 0 项 ; OK S1 全部通过
    [注入 ① 实参被覆盖]      注入前失败 0 项 → 注入后失败 1 项
        - F2 实参帧（返工前形态） → 第一次的实参被覆盖：收到 99 / 9.5，期望 11 / 1.5
      OK 命中：门禁抓住了该缺陷（探针 F2 失败） / OK 对照探针 F1 在注入下仍通过
    [注入 ② Reject 后仍执行了实现]  注入前失败 0 项 → 注入后失败 1 项
        - F4 只记档位、不发射校验的形态 → Reject 之后实现仍然被执行（FireCount=1）
      OK 命中 / OK 对照探针 F3 在注入下仍通过
    [注入 ③ 值未变却发了载荷]      注入前失败 0 项 → 注入后失败 1 项
        - F6 不做值比较的复制层 → 值未变却发了 1 个载荷
      OK 命中 / OK 对照探针 F5 在注入下仍通过
```

**为什么缺陷形态用「替身」而不是改上游**：写入边界禁止改 `Tools/PMNetGen/**` 与
`Client/Assets/Scripts/PMNet/**`。`F2/F4/F6` 是**同形状**替身（照 §5.5 记录的形态复刻），
目的不是指控上游仍有 bug（§5.5 已记录它们被修掉），而是证明 P4 / Q3 / L2 这类断言对
**那种形态**真的会失败 —— 即断言不是空的。

## 5. 上游缺陷

**未发现阻断端到端的 A/B 块真实缺陷**：89 项断言在干净实现下全绿，无需任何上游改动。
以下 3 条是本次顺带确认的**上游观察**（非缺陷，未修，供裁决/登记）：

1. **RPC 待发队列无界**（`Tools/PMNetGen/DeclEmitter.cs:144`、`:150`；产物
   `Tools/PMNetE2E/Generated/PMNetGeneratedRegistry.g.cs:74`）。`RemoteSender` 为 null 时
   `EnqueueRemote` 无条件 `_pendingRpcs.Enqueue`，**没有任何上限或告警**。
   最小复现：客户端侧对象连续调 `PMNet_Fire` N 次且不接线 `RemoteSender` ⇒
   `PendingRpcCount == N` 无限增长。D-R0-18 要求「复制队列与内存必须有界」，
   但 RPC 待发队列不在这条覆盖面内。R2 阶段这条路径此前**不可达**（M05 发送侧未接线，
   调用桩也没落地）；本门禁把调用桩跑通后它就是可达的了。**建议**：随 M05 接线时给该队列定界
   （超限丢弃 + 计数告警），或明确写进契约「未接线属开发期形态，不保证有界」。
2. **`PMNetGeneratedRegistry` 是「每程序集固定类名」且落在 `PMNet.Generated`**，
   与 Client 侧 proto 产物（`Client/Assets/Scripts/PMNet/Generated/SocketProto.PMNet.g.cs`，
   同命名空间）同域。已全仓核实：**当前无同名类型、不冲突**。风险只在「同一程序集里编进两份
   注册表」这种配置错误下表现为 CS0101。建议把「业务声明产物落在哪个目录/哪个程序集」写进使用约定
   （本门禁自己就是把 Client 的 `Generated/**` 显式排除后再编译的）。
3. **`PMNetClassEntry.Factory` 可能为 null**（抽象类 / 无可用无参构造），而
   `PMNetWorld.RegisterClass` 要求非 null 工厂、并且重复 ClassId 直接抛异常。
   这是生成物注释里已声明的限制（「注册方必须另行提供实例化方式」），
   但**运行期没有任何诊断**：调用方若不处理，会在注册期抛异常、或在收到该类对象的 Create 时静默丢弃。
   非本次范围，登记备查。

## 6. 诚实记录的缺口

1. **本门禁只用我自己的夹具（2 个类）**，没有用真实业务声明跑一遍。
   `Generated/**` 是 `Tools/PMNetE2E/` 下的独立产物，与 `Docs/plans/pmnet-ids.json`（真实业务的
   ID 锁）完全隔离 —— 这是为了让本门禁可独立验收。代价是「真实业务声明能否一次通过生成」
   这一条**没有被本门禁覆盖**（那属于 A 块 `PMDeclCheck` 的职责）。
2. **只覆盖首版类型集的 int / float / string**。枚举、数组、`[PMQuantized]`、多条件组合、
   跨程序集继承链基址（§5.4 #5）**没有**在端到端里跑过。端到端选择「少而穿透链路」，
   类型集广度由 A 块覆盖。
3. **`TargetFramework` 仍是 net8.0 可执行工程**，不是 netstandard2.0。
   netstandard2.0 只作为**生成物的编译语言面断言**存在（§4 段用 Roslyn 真编译，113 个
   netstandard2.0 引用程序集）。若该引用集在别的机器上缺失，该段会降级为 TPA 并**在输出里明说**
   （不会假装通过），但那时它只强制语言面 C# 7.3。
4. **「不同基线最终一致」是单对象、单属性族的收敛**，不是多对象 × 相关性裁剪 × 休眠 ×
   频率预算的联合场景。调度预算（`MaxObjectsPerConnectionPerTick`）、在途上限
   （`MaxInflightPerObject`）、相关性距离裁剪**没有**在端到端里施加压力
   （B 块的 H 段覆盖了有界性，本门禁没有重复）。
5. **负向验证的 3 条是「断言有效性」验证，不是「上游无缺陷」证明**。`F2/F4/F6` 是替身；
   它们证明断言有牙，但**不能**证明生成器与复制层里没有其它形态的缺陷。
6. **没有实测真实 UDP / Unity**（任务明确的非目标）。`TestConn` 是手指针替身，
   因此「载荷真的能过 M03 传输层 / 分片 / MTU 预算」这条链**未经验证** ——
   与 §5.4 #4/#8（数组上限、MTU、校验和）的未收敛状态一致。
7. **§2.3 的另两条 ID 不变性（重排不变 / 改名显式）不在本门禁**，仍在 A 块 `PMDeclCheck`；
   本门禁只覆盖第三条（二次生成零 diff，由运行期校验模式重验）。
8. **本报告与代码未做 git 提交**（任务要求），且 `Tools/**` 在本仓库处于未被 git 跟踪的状态，
   因此「本门禁是否会被 CI 跑到」不取决于本次改动 —— 需要人工把它接进验收脚本。
