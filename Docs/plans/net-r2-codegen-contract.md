# R2 — 声明驱动代码生成与最小复制（契约）

> **定位**：本文是 `net-architecture-migration.md` 中 **R2 阶段**的细节冻结，展开 R0 契约的
> D-R0-12/13/14/15/16/46/47/48/49/50。**主计划仍是状态与验收的唯一事实源**。
> **事实来源**：`Docs/plans/net-r0-contract.md` §2.4/§2.8/§3.4/§3.5/§5、UE 源码（逐条标 `文件:行号`）。
> **生成器的模板与 ID 分配算法细节**：R0 契约 §11 明确把它留给 R2 —— 就是本文。

---

## 0. 一句话目标

业务只写**声明**（Attribute 标记的字段与 `_Implementation` 方法），
外部生成器扫出声明、分配**稳定 ID**、生成**零反射**的静态表；
运行期靠这张表完成「属性增量复制 + RPC 双向调用」。

---

## 1. 冻结的接口（已落地，改动需同步本文与 R0 契约）

| 产物 | 路径 | 内容 |
|---|---|---|
| 声明属性 | `Client/Assets/Scripts/PMNet/Declarations/PMNetDeclarations.cs` | `[PMNetworkObject]` / `[PMReplicated]` / `[PMRepNotify]` / `[PMQuantized]` |
| 描述符与注册表 | 同上 | `PMPropertyDescriptor` / `PMReplicationDescriptor` / `PMRpcDescriptor` / `PMNetRegistry` |
| 稳定哈希 | `Client/Assets/Scripts/PMNet/Declarations/PMStableHash.cs` | **生成器与运行期共用同一实现** |
| 复制条件 | `Client/Assets/Scripts/PMNet/PMWireType.cs` 的 `PMCond` | **8 项，数值与 UE `ELifetimeCondition` 一致** |
| RPC 枚举 | `Client/Assets/Scripts/PMNet/PMRpc.cs` | `PMRpcKind` / `PMRpcReliability` / `PMRpcValidator` |
| 声明模型 IR | `Tools/PMDeclModel/PMDeclModel.cs` | `PMDeclModel` / `PMDeclClass` / `PMDeclProperty` / `PMDeclRpc` |
| ID 锁 | `Tools/PMDeclModel/PMIdLock.cs` | `PMIdLock` + 极简 JSON 读写 |

**已在 R2 修正的两处 R0 不一致（记录，避免被当成新问题）**：

1. **`PMCond` 与 D-R0-14 不符**：P0 骨架里是 6 值（含 `InitialOnly=4` / `NetGroup=5`），
   而 D-R0-14 冻结的是另外 8 项且明确把 `InitialOnly` / `NetGroup` 列入**不做**。
   已按 D-R0-14 重写为 8 项，**数值沿用 UE `ELifetimeCondition` 原值**
   （`CoreNetTypes.h`：`None=0 / OwnerOnly=2 / SkipOwner=3 / SimulatedOnly=4 /
   AutonomousOnly=5 / Custom=8 / Dynamic=14 / Never=15`），
   从而免去两套编号之间的换算表（那是最容易悄悄写错一行的地方）。
2. **R0 契约 §2.3 把 `Dynamic` 列进了"不做"清单**，但 D-R0-14 把 `Dynamic` 列进"实现的 8 项"。
   已裁定以 D-R0-14 为准并修正 §2.3；同时补上"未实现的 9 项"的准确清单
   （UE 共 18 项含 `COND_Max` 哨兵；`NetGroup` 在 UE 注释里写明 *Not usable on properties*）。

**与 R0 契约 §3.5 的一处有意偏差**：契约写 `byte[] ChangeMaskBits`，实现改为
`MaskOffset + MaskBitCount`（对象级变更掩码里的位区间）。理由：`byte[]` 意味着每个类
N 次分配，且热路径上要先读那段字节才能判定 —— 位区间等价且与
`PMReplicationDescriptor.ChangeMaskBitCount` 天然对齐。

---

## 2. 稳定 ID 分配算法（D-R0-49）

### 2.1 稳定键

```
类：   "CLASS:" + 命名空间 + "." + 类型名            （可被 [PMNetworkObject("显式键")] 覆盖）
属性： "PROP:"  + 类稳定键主体 + "." + 成员名
RPC：  "RPC:"   + 类稳定键主体 + "." + 方法名
```

「类稳定键主体」= 类稳定键去掉 `CLASS:` 前缀。

**成员键必须以类稳定键为前缀，而不是用「命名空间.类型名」重拼一遍** —— 这条在实现期被改过一次，
记在这里以免回退：

- 早期写法：`PROP:` + `命名空间.类型名` + `.` + 成员名。
  后果是 `[PMNetworkObject("显式键")]` 那条「改名但保持线协议兼容」的承诺**只成立一半**：
  实测把 `FixturePinned` 改名为 `FixturePinnedRenamed`，`ClassId` 保持 `1121439345` 不变，
  而成员 `_value` 的 `PropertyId` 从 `39454` 变成 `29275` —— 类 ID 钉住了，成员 ID 全丢了。
- 现在的写法对**未显式指定** StableKey 的类**产出完全相同的键**（因为那时
  「类稳定键主体」恰好就是 `命名空间.类型名`），因此这个修正**不改变任何既有 ID**，
  没有兼容代价；而对显式钉住的类，成员 ID 会跟着类键一起锚定。

门禁为此新增一条断言：「钉住 StableKey 的类改名后成员 PropertyId 也不变」（实测 `57348 → 57348`）。

### 2.2 分配

- **哈希**：`FNV-1a 32`（生成期与运行期同一实现，见 `PMStableHash`）。
- **类 ID**：32 位；属性 / RPC：16 位，且**按所属类分桶**（类内唯一即可，与 UE `FLifetimeProperty` 同口径）。
- **0 保留为无效值**：命中 0 时改为 1。
- **碰撞**：按**字典序确定性探测**下一个空位；**已有键优先**（不允许新键挤走老键）。
- **锁文件是权威**：`Docs/plans/pmnet-ids.json`。已有键一律沿用；只对新键分配。
- **ID 永不回收**：声明的键消失只告警（`Retired`），不释放编号 —— 与 D-R0-02「NetId 不复用」同一条道理。
- **硬错误**：若某次生成会让**已存在键的 ID 改变** ⇒ 生成失败（那等于悄悄改了线协议）。

### 2.3 必须能被门禁验证的三条不变性

| 不变性 | 验证方式 |
|---|---|
| **重排不变**：交换两个类 / 两个成员在源文件里的顺序，ID 与生成物逐字节相同 | 对同一份声明的两种排列各生成一次，`fc /b` 比对 |
| **二次生成零 diff**：连续生成两次，产物与锁文件逐字节相同 | 生成 → 保存 → 再生成 → 比对 |
| **改名显式**：改类名会得到新 ID（除显式钉住 StableKey），并有告警 | 改名后比对锁文件 diff，确认出现新键而非"静默沿用" |

---

## 3. 协议摘要（D-R0-46）

| 值 | 计算 | 用途 |
|---|---|---|
| `PMRpcDescriptor.ParamLayoutId`（16 位） | 参数**类型**有序列表的归一化签名的哈希（**与参数名无关**） | 单条 RPC 的两端比对 |
| `PMReplicationDescriptor.ProtocolHash`（32 位） | `(PropertyId, Condition, QuantizerId, MaskOffset, MaskBitCount, MemberName)` 按 `PropertyId` 升序 | 单类的两端比对 |
| `PMNetRegistry.ProtocolHash`（32 位） | 各 `ClassId` 升序，混入类摘要 + 各类 RPC 的 `(RpcId, Direction, Reliable, ParamLayoutId)`（`RpcId` 升序） | 握手期整体比对 |

**方向契约**：不一致 ⇒ **协议不兼容**（服务端断连 / 客户端标记不兼容），
**不是**"忽略这条 RPC"。因此生成器必须把全局摘要写进生成产物，供握手读取。

---

## 4. 生成物的 API 面（**这是启动后续两项实现的冻结接口**）

生成器对每个 `[PMNetworkObject]` 的 partial 类产出一个文件
`Generated/PMNet.<命名空间>.<类型名>.g.cs`，名字空间与该类相同（避开 `Manger` / `HOTFIX`，
见 `HotFixStaicList.cs:16-28` 的 XLua 反射范围约束）。

### 4.1 每类必须产出

```csharp
// 全部成员都带 PMGenerated 前缀，避免与业务命名冲突。
public const uint  PMGeneratedClassId = <32 位>;          // = PMNetRegistry 里的 ClassId
public const ushort PMGeneratedChangeMaskBitCount = <N>;

// 1) 复制属性注册（覆盖 PMNetObject 的虚方法）
protected override void CollectLifetimeReplicatedProps(PMRepList outProps);

// 2) 每个被标记成员的"赋值即标脏"访问器（业务用它写，不要裸露字段）
public void PMNet_Set<成员名>(<成员类型> value);            // 赋值 + MarkPropertyDirty

// 3) 属性读写器（供描述符持委托；static，避免每实例分配）
private static void PMNet_Write_<成员名>(PMNetObject t, PMNetWriter w);
private static void PMNet_Read_<成员名>(PMNetObject t, PMNetReader r);

// 4) RepNotify 分发（0 = 无）
//    PMPropertyDescriptor.OnRepMethodId 指向本表下标
private static void PMNet_OnRepDispatch(PMNetObject t, ushort onRepMethodId);

// 5) RPC：每个被标记方法各一个接收侧分发 + 一个业务可见调用桩
//    接收侧（读参数 → 过校验 → 调用业务 _Implementation）
private static void PMNet_RpcInvoke_<方法名>(PMNetObject t, PMNetReader r);
//    业务可见调用桩（契约 §4.3）：callspace 判定 → 本地执行 / 发远端
public void PMNet_<方法名>(<参数表>);

// 6) 静态注册表（本类贡献给 PMNetRegistry 的条目）
internal static PMNetClassEntry PMNet_BuildEntry();
```

### 4.2 每个程序集产出一个

`Generated/PMNetGeneratedRegistry.g.cs`：

```csharp
namespace PMNet.Generated
{
    public static class PMNetGeneratedRegistry
    {
        /// <summary>注册本程序集的全部生成产物。幂等：重复调用不重复注册。</summary>
        public static void RegisterAll();

        /// <summary>本程序集的整体协议摘要（D-R0-46）。</summary>
        public static uint ProtocolHash { get; }
    }
}
```

**`RegisterAll` 必须显式列出每个类**（`PMNetRegistry.RegisterClass(PMFoo.PMNet_BuildEntry())`），
**不得用反射扫描**（D-R0-48 / T40 的断言：「运行不扫反射」）。
最后调用 `PMNetRegistry.Seal(ProtocolHash)`。

### 4.3 RPC 的业务侧书写约定

```csharp
[PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
public void Fire(int targetId, float angle)
{
    // ★ 这层是**生成器产出的接收侧分发**在调用它；业务不要直接调用本方法，
    //   业务调用的是 PMNet_ 前缀的生成桩。
    //   直接调用只会本地执行、不过网 —— 与 UE 的 UFUNCTION 语义一致。
}

// 规则 13 要求：声明了 ForceValidate 档位就必须真的存在这个同伴。
private PMRpcValidation Fire_ForceValidate(int targetId, float angle)
{
    if (targetId <= 0) { return PMRpcValidation.Reject; }   // 真实实现里会先调 NET_FORCE_VALIDATE_REASON(...)
    return PMRpcValidation.Accept;
}
```

### 4.3.1 实参传递：**调用桩闭包捕获**（不是「对象上的实参帧」）

生成器产出的调用桩形态：

```csharp
public void PMNet_Fire(int p0, float p1)
{
    PMFunctionCallspace callspace = PMRpcDispatch.EvaluateCallspace(
        NetMode, Role, PMRpcKind.Server, GetNetConnection() != null, false);

    if (PMRpcDispatch.ShouldExecuteLocal(callspace)) { Fire(p0, p1); }

    if (PMRpcDispatch.ShouldSendRemote(callspace))
    {
        PMNetGeneratedRegistry.EnqueueRemote(
            this, PMGeneratedRpcId_Fire,
            delegate(PMNetObject t, PMNetWriter w)   // ← 闭包捕获 p0/p1
            {
                w.WriteInt32(p0);
                w.WriteFloat(p1);
            });
    }
}
```

**这条在实现期被返工过一次，记在这里以免回退**：早期版本把实参暂存在对象的私有字段
（`PMNetRpcArg_<方法名>_<i>`，叫「实参帧」），再由一个 `PMNet_RpcWrite_<方法名>(PMNetObject, PMNetWriter)`
静态方法从字段里读出来编码。问题是**发送可能被推迟**（`RemoteSender` 未接线时进待发队列），
而真正的编码发生在推迟之后 —— 届时实参帧可能已被**后续同一条 RPC 的调用**覆写，
于是发出**错误的参数**。生成物注释里当时写的是"编码是在同一次栈上同步完成的"，
这句话在排队路径下是不成立的。

闭包仅对标量和不可变字符串保持调用值。数组必须在调用点、且在Multicast本地实现之前Clone快照；入队前检查数组与字符串长度。闭包编码使用该快照，不能保留调用方可变数组。
代价是每次远端调用一个闭包分配 —— 可接受，因为可靠 RPC 本就是低频（D-R0-05）。
随之 `PMRpcEntry` **不再持有 `Write` 委托**（发送侧实参不在对象上，没什么可从对象编码的）。

### 4.3.2 校验路由（生成器必须发射调用，而不只是记档位）

| 生效档位 | 生成物发射的调用 | 动作（**动作差异是契约**，D-R0-45） |
|---|---|---|
| `ForceValidate` | `<M>_ForceValidate(...)` 返回 `PMRpcValidation` | `Reject` ⇒ 上报后**跳过实现、不断连**；`Report` ⇒ 上报后**仍执行**；`Accept` ⇒ 静默放行 |
| `Validate` | `<M>_Validate(...)` 返回 `bool` | `false` ⇒ 上报并**请求断连**，不执行实现 |
| `None` | 不发射校验调用 | 直接执行实现 |

上报经 `PMRpcValidationSink`（`OnValidateFailed` / `OnReported` 两个钩子 + `Unhandled` 计数），
生成代码因此**不依赖传输、日志与反外挂上报系统**。宿主在启动期接线。

**这条也是返工结果**：早期版本的生成物只把档位写进 `PMRpcDescriptor.Validator`，
接收侧**不生成任何校验调用** ⇒「声明了校验」不等于「运行时真的校验了」，
描述符里写着 `ForceValidate` 而反外挂链路上什么都没有。

---

## 5. 声明阶段必须报错的规则（D-R0-50，编译期失败）

| # | 规则 | 依据 |
|---|---|---|
| 1 | `[PMNetworkObject]` 的类必须是 `partial` 且能推到 `PMNetObject` | 要往同类补成员；复制需要基类 |
| 2 | `[PMNetworkObject]` 的类不得是泛型类 | 生成的静态表无法表达开放泛型 |
| 3 | `[PMReplicated]` 成员不得是 `static` / `const` / `readonly` | 复制层要写入 |
| 4 | `[PMReplicated]` 成员的类型必须落在支持的类型集内（见 §6） | 无法序列化 |
| 5 | `[PMRepNotify]` 的 `ForMember` 必须对应一个已声明的 `[PMReplicated]` 成员 | 拼错会让"收到更新但不触发表现"变成静默缺陷 |
| 6 | `[PMRepNotify]` 目标方法必须无参、返回 `void` | 同上 |
| 7 | `[PMRpc]` 方法必须返回 `void`、不得 `static` | 无返回值语义 |
| 8 | **`[PMServerRpc]` 必须同时声明校验（`Validator != None` 或存在 `_ForceValidate` 同伴）** | 项目红线：上行 RPC 必须过反外挂校验（对齐 UHT 对 Server RPC 的强制要求） |
| 9 | `WithValidation = true` 与 `Validator = ForceValidate` **不得同时出现** | D-R0-45：两者互斥，同时写会让"失败后断不断连"不确定 |
| 10 | 同一类内 `PropertyId` / `RpcId` 不得重复（含跨程序集） | 线协议歧义 |
| 11 | 声明的条件必须是 D-R0-14 的 8 项之一 | 未实现的条件**直接报错**，不静默降级 |
| 12 | 同一个类的 `MaskOffset` 区间不得重叠，且总数 == `ChangeMaskBitCount` | 掩码正确性 |
| **13** | **声明的校验档位必须有可调用的同伴方法**：`ForceValidate` ⇒ 必须存在 `<M>_ForceValidate`；`Validate`（含 `WithValidation = true`）⇒ 必须存在 `<M>_Validate` | **R2 返工时补上**。规则 8 只管"有没有声明"；规则 13 管"声明了是否真的调得动"。实测：只写 `Validator = ForceValidate` 而不写同伴，会让发射器走投无路 —— 要么调用不存在的方法，要么退化成「**声称有校验、实际没有**」，后者正是本项目最不能接受的失败形态 |

---

## 6. 首版支持的成员类型集（复制与 RPC 参数共用）

| 类别 | 类型 | 线格式 |
|---|---|---|
| 整数 | `byte / sbyte / short / ushort / int / uint / long / ulong` | varint（有符号用 zigzag） |
| 布尔 | `bool` | varint 0/1 |
| 浮点 | `float / double` | fixed32 / fixed64（**负零会丢符号**，见主计划坑 #4） |
| 字符串 | `string` | length-delimited UTF-8，**有上限**：经 `PMNetString.WriteBounded` / `ReadBounded`，上限 `PMNetString.MaxBytes = 1024`（契约 §8 的待收敛初值，**单点**放在运行时，不重复到每个生成类） |
| 枚举 | 任意底层为整型的 `enum` | varint |
| 定长数组 | `T[]`（T 为上表之一） | 长度 + 元素 |
| 结构 | 由 `[PMNetworkStruct]` 标记的类型（**R2 不实现**，声明即报错） | — |

不支持的类型 ⇒ 声明阶段报错（规则 4）。**不做静默跳过**。

---

## 7. 委派边界（三块工作，接口已冻结，彼此不依赖）

| 项 | 负责 | 写路径（硬边界） | 交付物与验收 |
|---|---|---|---|
| **A. 扫描 + 发射** | 子代理 | `Tools/PMNetGen/DeclScanner.cs`、`Tools/PMNetGen/DeclEmitter.cs`、`Tools/PMNetGen/Program.cs`、`Tools/PMNetGen/PMNetGen.csproj`、`Docs/plans/pmnet-ids.json`、`Tools/PMDeclCheck/**` | Roslyn 扫声明 → `PMDeclModel`（含 §5 全部规则）；发射 §4 的 API 面；ID 走 `PMIdLock`。门禁 `Tools/PMDeclCheck`：§2.3 三条不变性 + §5 十二条规则各一用例 |
| **B. 运行时复制层** | 子代理 | `Client/Assets/Scripts/PMNet/Replication/**`、`Tools/PMReplicationTest/**` | M06 最小：每连接基线、变更掩码比较、8 项条件、D-R0-15 条件跃迁全掩码、OnRep 分发、D-R0-16 初始状态全量。门禁 `Tools/PMReplicationTest`：用**手写描述符**测（不依赖生成器） |
| **C. 集成与总门禁** | 主 Agent | `Tools/PMNetE2E/**`、`Docs/plans/*` | 一个测试对象端到端：声明 → 生成 → 复制收敛 → RPC 双向；T40/T41 收口 |

**A / B 的实际交付（已完成，见主计划 §5.4 的待收口缺陷登记）**：

| 项 | 门禁 | 结果 |
|---|---|---|
| A（扫描 + 发射 + 声明校验） | `Tools/PMDeclCheck` | **60 项 0 失败**（含 §2.3 三条不变性、§5 十二条规则逐条负向验证、生成物在 C# 7.3 + netstandard2.0 下真编译） |
| B（运行时复制层） | `Tools/PMReplicationTest` | **129 项 0 失败 + 3/3 缺陷注入命中** |

R2 收口前必须处理的 8 项缺陷（含 **RPC 实参传递方式返工**、**校验同伴的执行接线**、
**字符串/数组长度上限**）登记在主计划 §5.4。

**A 与 B 的共同前提**（已冻结，不得改动）：
§1 的接口、§2 的 ID 算法、§3 的协议摘要、§4 的生成物 API 面、§5 的规则集、§6 的类型集。

---

## 8. 待收敛参数（本文件不冻结）

| 参数 | 初值 | 收敛时机 |
|---|---|---|
| 单条复制的属性数上限（防止一个包过大） | 按 §9 MTU 反推 | R2 实测 |
| 字符串复制长度上限 | **已实现**：`PMNetString.MaxBytes = 1024`，两端都有门（写侧抛、读侧**分配前**预检抛） | 值待 R2 实测收敛 |
| 每帧每连接的复制对象调度上限 | 沿用项目 150（R0 §9） | R2 |
| 每对象在途记录上限 | 沿用 UE 65535（R0 §9） | R2 |


## 9. 独立审查修订（当前实现口径）

- RPC运行时注册键为 `(ClassId, RpcId)`，与§2.2类内ID一致；`TryGetRpc(classId,rpcId)`为网络派发入口使用的查询。裸RpcId查询只在无歧义时成功。类与RPC批量登记先校验后提交。
- 宿主接收调用 `PMNetRpcReceive.Deliver(world, source, rpcId, targetId, payload, offset, count)`：先查存活对象及其ClassId，再查RPC、方向、Owner/IgnoreRpcs，最后解码与业务校验。尾部/截断必须在业务实现前拒绝。原生Validate失败通过来源连接的 `IPMNetRpcDisconnectTarget` 请求断连；R3负责真实传输接线。ForceValidate Reject保持不断连。
- 未接线发送队列上限1024，满时增加 `RejectedPendingRpcs` 并抛异常；新调用不被接受、旧调用不被淘汰。已接线可靠传输溢出仍按R0断连契约。
- `PMRepMask` 与 `PMDirtyTracker` 均支持256位，线上32字节按小端位序处理；不合法槽位/重复/乱序/尾部明确拒绝。
- 生命周期格式版本2：手写初值钩子写出非空内容时优先；否则世界从静态描述符写入声明式初值，按连接过滤，在创建回调前应用。过滤器由复制channel接线；未接线只发送None条件，Never始终排除。Create不提前确认复制基线。
- 普通更新先结构验证和暂存解码，再整条提交，最后统一OnRep；畸形记录不ACK。工厂必须返回全新未登记对象。纯字段Reader保证解析失败不改活对象；有副作用的自定义Reader/属性setter不具备全面事务保证，提交异常计数且不ACK。OnRep异常与协议失败分开，整条已提交仍ACK。
- 当前验收数和状态以主计划文末RV1–RV7为准；§7旧数字保留为历史阶段结果，不代表真实UDP/Unity验证。
