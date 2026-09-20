# R2 返工 — RPC 组（RV5 / RV6）实施报告

> 范围：RPC 生成器（`Tools/PMNetGen/Decl*.cs`）、声明运行时（`Client/Assets/Scripts/PMNet/Declarations/PMNetDeclarations.cs`、
> `PMNet/PMRpc.cs`）、端到端门禁（`Tools/PMNetE2E/**`）、声明门禁（`Tools/PMDeclCheck/**`）。
> 证据来源：`Docs/plans/_r2_review_rpc.md`（C1–C7、H1–H4）、`net-architecture-migration.md` §5.4/§5.5/§5.6 与
> 「R2 独立审查后返工」（RV1–RV7）、`net-r2-codegen-contract.md`、`net-r0-contract.md`（D-R0-05/06/07/18/41/42/43/45/46/48/49/50）。
> 另一个并行组的文件（`Replication/**`、`World/**`、`PMNetObject.cs`、`PMNetProperty.cs`）**未改动**。
> 初始 Create 接线未触碰（由另一组承担）；未做 UDP 宿主与 R3 握手。

## 1. 必读文档路由（实际按此顺序阅读，也是本次判据的来源）

| 顺序 | 文档 | 本次用于判定什么 |
|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 入口路由（Client / Server / 迁移计划） |
| 2 | `Client/Assets/AGENTS.md` | 客户端进程形态与 `PMNet` 归属；确认无业务侧 `[PMNetworkObject]` 声明 |
| 3 | `Server/AGENTS.md` | 服务端权威边界（HP/帧/结算）；确认 RPC 校验属上行红线 |
| 4 | `Docs/plans/net-architecture-migration.md`「R2 独立审查后返工」+ §5.4–§5.6 | RV5/RV6 的验收条目；「可靠 RPC 不得超限淘汰已接受调用」；「未接线本地队列超限显式失败」 |
| 5 | `Docs/plans/net-r2-codegen-contract.md`（全文） | §4.3.1 实参传递（闭包捕获的适用边界）、§4.3.2 校验路由动作差异、§5 规则 13、§6 类型集、§7 委派边界 |
| 6 | `Docs/plans/net-r0-contract.md`（§2.2 / §2.3 / §2.7 / §3.4 / §5） | D-R0-05/06/07（可靠语义 = 保序要求送达；溢出 = 断连，不得静默丢）、D-R0-18（有界）、D-R0-42（归属谓词形参）、D-R0-43（方向非法是确定行为）、D-R0-45（Reject 不断连 / 原生 Validate 断连）、D-R0-46（布局不匹配 = 协议不兼容）、D-R0-48（C# 7.3 + netstandard2.0、无反射） |
| 7 | `Docs/plans/_r2_review_rpc.md`（全文） | C1（数组非真快照）、C2（归属校验未接线）、C3（原学校验零覆盖）、C4（发射器注释兜底 = fail-open）、C5（解码后校验 / 尾部未校验）、C6（队列丢最旧与可靠语义冲突）、C6b（调用桩不传可靠性与收件人） |

## 2. 修复内容与落点

### 2.1 RV5-1 数组实参：调用点快照（C1）

- `Tools/PMNetGen/DeclEmitter.cs`：新增 `EmitRemoteArgPreflight`，在**调用桩**里按下述顺序发射：
  1. `callspace` 判定；
  2. `if (ShouldSendRemote) { 快照声明 + 长度门 }`（数组 `Clone()`；字符串 `EnsureWithinLimit`）；
  3. `if (ShouldExecuteLocal) { 调业务实现 }`；
  4. `if (ShouldSendRemote) { EnqueueRemote(..., 闭包写“快照/实参”) }`。
- 关键点：快照发生在**本地执行之前**。Multicast 在服务端是 `Local | Remote`（先本地执行、再外发），
  业务实现可以就地改写数组实参；旧形态（只捕获引用）会让远端收到**被本地实现改写过的值**。
- `string` 不可变 ⇒ 不做拷贝，只做长度门（契约 §6）。
- 生成物注释同步修正：撤掉「闭包捕获没有窗口」的绝对说法，改为「只对值类型与不可变引用成立；数组另有快照」。
- 覆盖夹具：`E2eFixtures.cs` 的 `Push(float[])`（**Multicast** + ForceValidate）——实现里就地写 `values[0] = 999f`，
  用于同时观察「本地 = 999」与「远端 = 调用时的 1.5」。

### 2.2 RV5-2 长度门在入队前执行（C1 / §5.4 #4 的写侧）

- 数组：`if (p0.Length > PMGeneratedMaxArrayLength) throw new System.FormatException(...)`，位置在入队之前。
- 字符串：新增 `PMNetString.EnsureWithinLimit(value, what)`（`PMNetDeclarations.cs`，与 `WriteBounded` 共用同一实现），
  调用桩在入队前调用它；编码期 `WriteBounded` 仍保留作为第二道门。
- 覆盖夹具：`Say(string)`（Client RPC），测试断言「超长抛异常 **且** 队列长度仍为 0」，
  并以「恰好等于上限的字符串能正常入队并正确还原」作对照（防止断言在“什么都发不出去”时空过）。

### 2.3 RV5-3 未接线队列：有界但禁止丢最旧（C6 / D-R0-06/07）

- 生成物（`PMNetGeneratedRegistry.g.cs`，模板在 `DeclEmitter.EmitRegistry`）：
  - 删除「`while (Count >= Max) Dequeue()` + `DroppedPendingRpcs++`」；
  - 改为 `if (Count >= Max) { RejectedPendingRpcs++; throw new InvalidOperationException(...); }`
    ⇒ **不接受新调用、原样保留已接受调用与其顺序**；
  - `MaxPendingRpcs` 上限值不变（1024）。
- 门禁 O2b 按新语义重写：压满 1024 条 → 第 1025 条抛 `InvalidOperationException`、长度仍 1024、
  `RejectedPendingRpcs` == 1、**队首仍是最早被接受的调用（实参 0）** ⇒ 直接证明“没有丢最旧”。

### 2.4 RV6-1 统一 RPC 接收入口（C2 / C5）

- 新增于 `Client/Assets/Scripts/PMNet/PMRpc.cs`：`PMNetRpcReceive.Deliver(...)`，判定顺序即契约：
  1. 上下文（world/source/reader 非空）→ 2. `PMNetRegistry.TryGetRpc` → 3. `world.TryFind(NetId)` →
  4. `State == Active` → 5. 方向合法（D-R0-43：服务端只收 `Server`；客户端只收 `Client`/`Multicast`）→
  6. 归属 + `IgnoreRpcs`（**直接调用既有谓词** `PMRpcDispatch.ShouldCallRemoteFunction(receiverIsServer, isOwner, ignoreRpcs)`，
     三个入参全部现场推导，不接受注入）→ 7. 交给生成 Invoker 解码/校验/执行。
- **前六步都在解码之前** ⇒ 未授权/方向错的包连参数都不读，更不会执行实现。
- 返回 `PMNetRpcReceiveResult{Status, RpcId, DisconnectRequested, Detail}`；状态含
  `Applied / InvalidContext / UnknownRpc / UnknownTarget / TargetNotActive / WrongDirection / NotOwner / IgnoreRpcs / Malformed / ValidationFailed`。
- 归属来源同时接受 `OwnerConnection`（显式绑定，复制层条件也用它）与 `GetNetConnection()`（UE Owner 链推导），
  两者都只是“这条连接是不是该对象的拥有者连接”，不接受包内提供的数据。
- 计数器：`Delivered / Rejected / MalformedCount / DisconnectRequests / UnroutableDisconnectRequests` + `Observer`/`Warn` 出口。

### 2.5 RV6-2 畸形 / 尾随字节在业务实现之前拒绝（C5）

- 生成 Invoker 在读完声明参数后、**校验同伴之前**新增：`if (!r.IsAtEnd) throw new System.FormatException("RPC X 载荷存在尾随字节：…")`。
- 截断 / 越界由 `PMNetReader` 与生成读体（数组上限、`PMNetString.ReadBounded` 预检）抛 `FormatException`；
  接收入口捕获 `FormatException` ⇒ `Malformed`，捕获 `InvalidCastException` ⇒ `Malformed`（对象类型与 RPC 不匹配）。
- 业务实现自身抛出的其它异常**不吞**（继续向上传播，避免把业务 bug 记成协议错误）。

### 2.6 RV6-3 原生 Validate 失败真的请求断连（C3 / D-R0-45）

- 生成物（`EmitValidationRouting` 的 `Validate` 分支）：`_Validate` 返回 false ⇒
  先 `PMRpcValidationSink.NotifyValidateFailed(...)` 留痕，再
  `throw new PMNet.PMNetRpcValidationFailedException(rpcId, methodName)`。
- 为什么要抛：`PMRpcInvoker` 的签名是 R2 冻结面（`void(PMNetObject, PMNetReader)`），校验发生在生成物内部、
  那里拿不到连接；异常是唯一能把结论交给“持有连接的那一层”的通道。
- 接收入口捕获它 ⇒ 经 `IPMNetRpcDisconnectTarget.RequestRpcDisconnect(target, rpcId, method)` 向**来源连接**发出断连请求，
  并返回 `ValidationFailed`。
- `ForceValidate` 的 `Reject` **不**走这条路 ⇒ 只跳过实现、不断连（对照断言 V6）。

### 2.7 RV6-4 非法 ForceValidate 结果失败关闭

- 生成物从「Reject → 跳过；Report → 上报；否则执行」改为「先归一成 `Report` 或 `Reject`（非 `Accept` 即 `Reject`），
  再按归一结果决定是否 `return`」。
- 于是 `(PMRpcValidation)99`（C# 枚举合法）不再掉进“否则执行”的放行分支，而是被当作 `Reject`
  上报并跳过实现（断言 V7/V8）。

### 2.8 RV6-5 发射器自身 fail-closed（C4）

- `EmitValidationRouting` 末尾「档位 ≠ None 但同伴不可得」分支由**发射一条注释**改为
  `throw new InvalidOperationException(...)`（拒绝发射）。
- 理由：`PMDeclEmitter.EmitAll` 是公开 API，声明期规则 13 不是它唯一入口（增量生成/编辑器内生成/未来复用），
  注释不是断言；CLI 侧该异常被 `Program.RunDecl` 捕获为退出码 1（硬错误）。
- 门禁新增断言：绕过校验、直接用手工 IR 调 `EmitAll` 必须抛异常。

### 2.9 RV6-6 断连接缝（`PMNetConnection` 冻结的处置）

- `PMNetConnection` 只有 `Send/IsReady/IgnoreRpcs`，没有断连 API，且不在本次硬写边界内。
  因此在 `PMRpc.cs` 新增最小接口 `IPMNetRpcDisconnectTarget.RequestRpcDisconnect(PMNetObject, ushort, string)`，
  由宿主连接实现；**R3 的真实实现应直接委托给既有传输层 API**
  `PMNet.Transport.PMTransport.Disconnect(PMDisconnectReason)`（`PMTransport.cs:742`，已含“尽力通知对端一次再本地清场”）。
- 连接未实现该接口时**不静默**：计入 `UnroutableDisconnectRequests` 并走 `PMNetRpcReceive.Warn`（断言 V15/V16）。
  这样「未接线 ⇒ 既不断连也无痕迹」（H3）不再成立。

## 3. 测试与退出码（本次实际执行）

| 命令 | 结果 |
|---|---|
| `dotnet build Tools/PMNetGen/PMNetGen.csproj -c Release` | **退出 0**，0 警告 0 错误 |
| `dotnet Tools/PMNetGen/bin/Release/net8.0/PMNetGen.dll --decl-gen Tools/PMNetE2E/E2eFixtures.cs --out-dir Tools/PMNetE2E/Generated --id-lock Tools/PMNetE2E/e2e-ids.json` | **退出 0**；2 类 / 5 属性 / **6 RPC**；GlobalProtocolHash `0x5C0B6B49` |
| `dotnet build Tools/PMDeclCheck/PMDeclCheck.csproj -c Release` | **退出 0**，0 错误 |
| `dotnet Tools/PMDeclCheck/bin/Release/net8.0/PMDeclCheck.dll` | **退出 0**：**71 PASS / 0 FAIL**（返工前 61）。新增 §12 十条：数组快照、数组长度门、字符串长度门、尾随字节、原生 `_Validate(` 真的被发射、`PMNetRpcValidationFailedException`、非法值 fail-closed 归一、注册表超限显式失败且旧“丢最旧”写法消失、`EmitAll` fail-closed。§2.3 三条不变性与 §5 十三条规则原断言**全部保留未删** |
| `dotnet build Tools/PMNetE2E/PMNetE2E.csproj -c Release` | **退出 0**，0 错误 |
| `dotnet Tools/PMNetE2E/bin/Release/net8.0/PMNetE2E.dll` | **退出 0**：**162 PASS / 0 FAIL**（返工前 92），**4/4 负向注入命中**且对照探针在注入下仍通过 |

E2E 新增的关键断言（均为“喂包级”，不是谓词级）：
- `O2b*`：队列满 1024 → 第 1025 条抛异常、长度不变、队首仍是实参 0（评审 C6 的语义冲突）。
- `M1–M8`：服务端多播 `Local|Remote`；本地实现把 `buffer[0]` 改成 999；远端仍收到 **1.5**（评审 C1）。
- `M9–M14`：数组/字符串超长在调用点抛异常且队列不变；上限值正常入队并正确还原。
- `R5–R21`：经**真实接收入口**投递：Owner 放行并执行；非 Owner ⇒ `NotOwner` 且不执行、不断连；
  `IgnoreRpcs` ⇒ 拒绝；服务端收 Client 方向 ⇒ `WrongDirection`；客户端收 Server 方向 ⇒ `WrongDirection`；
  客户端收 Client 方向 ⇒ 放行并执行；已销毁 ⇒ `UnknownTarget`；未注册 RpcId ⇒ `UnknownRpc`。
- `V1–V16`：ForceValidate `Accept/Report/Reject/99` 四态；原生 `Validate=false` ⇒ `ValidationFailed`
  **且真的向来源连接发出 1 次断连请求**、实现未执行；`Validate=true` ⇒ 正常执行；
  连接未实现接缝 ⇒ 仍拒绝实现、不谎报断连、计数 + 告警。
- `X1–X9`：截断 / 尾随字节 / 数组长度前缀越界 / 超长字符串 ⇒ `Malformed` 且实现未执行。
- 注入 ④（新）：还原“接收侧只读参数、直接调实现”的旧形态 ⇒ 探针 F8 失败（非 Owner 投递被执行），
  证明“非 Owner 不得执行”这条断言对旧形态真的会失败（评审 C2）。

## 4. 接口调用说明（宿主侧怎么用）

```csharp
// 1) 接收一条 RPC（世界 / 来源连接 / RpcId / 目标身份 / 载荷）
PMNetRpcReceiveResult r = PMNetRpcReceive.Deliver(world, sourceConn, rpcId, targetNetId, payload, 0, payload.Length);
// r.Status: Applied | UnknownRpc | UnknownTarget | TargetNotActive | WrongDirection
//          | NotOwner | IgnoreRpcs | Malformed | ValidationFailed | InvalidContext
// r.DisconnectRequested: 已就“原生 Validate 失败”向来源连接发出断连请求（其余情况恒 false）

// 2) 宿主连接实现断连接缝（R3）
public sealed class MyNetConnection : PMNetConnection, IPMNetRpcDisconnectTarget
{
    public void RequestRpcDisconnect(PMNetObject target, ushort rpcId, string methodName)
    {
        _transport.Disconnect(PMNet.Transport.PMDisconnectReason.ProtocolError);  // 既有 API
    }
}

// 3) 可观测性（可选）
PMNetRpcReceive.Observer = result => { /* 每种拒绝原因一条日志 */ };
PMNetRpcReceive.Warn     = msg    => { /* 断连请求不可路由等 */ };
```

约定与边界：
- 入口**先权限、后解码**；`Malformed` 表示“参数没读完 / 有尾随字节 / 长度越界”，实现未执行。
- 入口**不代替宿主**决定“畸形包是否断连”（D-R0-46 的握手期断连属 M03/M05）；只有原生校验失败会发起断连请求。
- 业务**调用生成桩**（`PMNet_<方法名>`）；直接调 `_Implementation` 只本地执行、不过网。
- 数组实参在调用桩内快照，因此每次远端调用有一次浅拷贝分配（可靠 RPC 低频，可接受）；`string` 不拷贝。

## 5. 未验证事项（诚实记录）

1. **真实 UDP 宿主 / 端到端网络**：R3 未开工。本次全部为**内存投递**证据（真实世界查找、真实连接对象、真实生成物），
   不代表真实网络的丢包/重排/MTU 行为。
2. **R3 握手的 `ParamLayoutId` / `ProtocolHash` 比对**：未接线（属 M03/M05）。
   入口具备 `Malformed` 判定，但“布局漂移 ⇒ 断连”的落点仍未确定。
3. **接收循环的每包 try/catch**：入口自己 catch `FormatException`/`InvalidCastException`/校验失败异常；
   其它异常按“业务 bug”向上传播。宿主是否需要最终兜底属 R3。
4. **`PMDeclCheck` / `PMNetE2E` 之外的门禁未运行**（按硬边界不编译对方测试）：`PMNetLangCheck`、
   `PMCallspaceCheck`、`PMReplicationTest`、`PMNetWorldTest`、`PMNetVerify` 未复跑。
   间接证据：`PMDeclCheck` §9 与 `PMNetE2E` §4 都把**整个 `Client/Assets/Scripts/PMNet` 运行时**
   （含 Transport/Replication/World）与生成物、夹具一起在 **C# 7.3 + netstandard2.0** 下编译，0 错误 ⇒
   新增运行时类型不破坏 API 面。
5. **并发编辑造成的瞬时编译失败**：构建 `PMNetGen` 时曾观测到对方正在编辑的
   `Client/Assets/Scripts/PMNet/Replication/PMRepConnectionState.cs:43` 缺 `using System;`（CS0246），
   导致 `PMDeclModel`（链接运行时 PMNet 核心）编译失败；对方完成编辑后即恢复正常，**未越界修改该文件**。
6. **未改动主计划/契约文档**（不在写路径内）：`net-architecture-migration.md` §5.6 仍写着「超限丢最旧并计入
   `DroppedPendingRpcs`」，与本次修复后的语义（显式失败 + `RejectedPendingRpcs`）相反，
   需主 Agent 在集成复核时同步该行与 RV5/RV6 状态。

## 6. 变更文件清单（全部在 writePaths 内）

| 文件 | 变更 |
|---|---|
| `Tools/PMNetGen/DeclEmitter.cs` | 数组快照/长度门前置；尾随字节检查；ForceValidate fail-closed；原生 Validate 抛异常；发射器 fail-closed 抛异常；注册表超限显式失败（`RejectedPendingRpcs`）；相关注释修正 |
| `Tools/PMDeclCheck/Program.cs` | 新增 §12「RV5/RV6 生成分支覆盖」十条断言 + `Detail()` 文案助手 |
| `Tools/PMDeclCheck/Fixtures/Good.cs` | 新增数组实参 Server RPC（`PushValues` + 同伴）与原生 Validate Server RPC（`CheckedTeleport` + 同伴） |
| `Client/Assets/Scripts/PMNet/Declarations/PMNetDeclarations.cs` | 新增 `PMNetString.EnsureWithinLimit`；`WriteBounded` 复用同一实现 |
| `Client/Assets/Scripts/PMNet/PMRpc.cs` | 新增 `PMNetRpcReceive`（统一接收入口）、`PMNetRpcReceiveStatus`、`PMNetRpcReceiveResult`、`PMNetRpcValidationFailedException`、`IPMNetRpcDisconnectTarget` |
| `Tools/PMNetE2E/E2eFixtures.cs` | 新增 `Push(float[])`(Multicast+ForceValidate)、`Checked(int)`(Server+Validate)、`Say(string)`(Client)、`Warp(int)`(ForceValidate 三态/非法值) 及计数器 |
| `Tools/PMNetE2E/Program.cs` | O2b 重写；新增 M/R/V/X 系列喂包级断言；新增注入 ④ 与 `NoGateDeliver`/`ProbeOwnershipEnforced`；`TestConn` 实现断连接缝 + `PlainConn`；新增可达计数器 |
| `Tools/PMNetE2E/Generated/PMNetGeneratedRegistry.g.cs` | 由生成器重写（超限显式失败） |
| `Tools/PMNetE2E/Generated/PMNet.PMNetE2E.E2eReplicated.g.cs` | 由生成器重写（新增 4 条 RPC 的桩/分发/描述符） |
| `Tools/PMNetE2E/e2e-ids.json` | 由生成器写入 4 条新 RPC 成员 ID |
| `Docs/plans/_r2_fix_rpc.md` | 本报告 |

**未写入但出现在写路径中的文件**：`Tools/PMNetGen/DeclValidation.cs`（规则 13 已完备，本次无需改）、
`Tools/PMDeclCheck/Fixtures/Bad.cs`（规则 13 负向用例已足够；发射器 fail-closed 用手工 IR 直接验证）、
`Tools/PMNetE2E/PMNetE2E.csproj`（生成/编译配置无需改）、
`Tools/PMNetE2E/Generated/PMNet.PMNetE2E.E2eScoreboard.g.cs`（生成器按同一夹具重写，内容逐字节不变）、
`Tools/PMNetE2E/Generated/PMNet.PMNetE2E.E2eAuxiliary.g.cs`（**未创建**：本次复用已有夹具类，不新增类 ⇒ 不新增产物文件）。

---

# 续做（主 Agent 复核后）：注册表复合键 + 真实生成物 Create 验收

> 触发原因：主 Agent 复核发现**确定缺陷** —— `PMNetRegistry` 用 `Dictionary<ushort, PMNetRpcEntry>`
> 把 RpcId 当**全局**唯一，而契约 `net-r2-codegen-contract.md` §2.2 明确「属性 / RPC：16 位，且**按所属类分桶**
> （类内唯一即可）」；新 `Deliver` 还在**查目标对象之前**按 RpcId 全局查表，并把「投错类」交给
> `InvalidCastException` 兜底。同时本轮接下了「真实生成物路径的 Create 初值验收」（RPC 组拥有 fixture/E2E 文件）。

## 7. 注册表改为 (ClassId, RpcId) 复合键

### 7.1 落点（`Client/Assets/Scripts/PMNet/Declarations/PMNetDeclarations.cs`）

| 项 | 修法 |
|---|---|
| 主表 | `Dictionary<ushort, PMNetRpcEntry>` → `Dictionary<long, PMNetRpcEntry>`，键 = `((long)ClassId << 16) \| RpcId`（32 + 16 位，48 位无碰撞） |
| 兼容索引 | 新增 `_rpcByBareId`（`ushort → entry`），**用 null 值当歧义标记**：同一裸 RpcId 第二次出现即置 null |
| `TryGetRpc(ushort)` | **保留**，但只在「整个注册表里该 RpcId 唯一」时返回 true；歧义 ⇒ `entry=null` + false（**不任意挑一个**） |
| `TryGetRpc(uint, ushort)` | 新增，接收侧的**唯一**查询方式 |
| `RegisterClass` 原子性 | 改为**先校验后提交**：先把整份 RPC 清单校完（null 项 / RpcId 0 / 归属类与 ClassId 不一致 / 类内重复 / 与既有键冲突），全部通过才写表；提交阶段的写入不可能失败（冲突已在前面排除）⇒ 失败时注册表与调用前逐项相同 |
| 行为变更 | **跨类**同号 RpcId 从「抛重复」变为**合法**（这正是缺陷所在）；**类内**重复仍然硬失败（`InvalidOperationException`） |
| `RegisterRpc` | 按 `OwningClassId` 分桶；`OwningClassId == 0` 明确抛异常（RPC 必须挂在某个类之下才能分桶） |

`RpcCount` 的口径随之变成「(ClassId, RpcId) 条目数」（两类各一条同号 RPC ⇒ 计 2），注释已同步。

### 7.2 `Deliver` 判定顺序改为「先目标、再 RPC」（`Client/Assets/Scripts/PMNet/PMRpc.cs`）

```
① 上下文 → ② world.TryFind(目标) → ③ 目标 Active
       → ④ TryGetRpc(target.ClassId, rpcId)   ← 不属于目标类即拒（UnknownRpc）
       → ⑤ 方向 → ⑥ 归属 / IgnoreRpcs → ⑦ 解码 + 校验 + 执行
```

- **为什么②必须早于④**（已写进生产注释）：RpcId 只在类内唯一，所以「是哪条 RPC」只有在拿到
  目标对象的 `ClassId` 之后才能确定。反过来先按裸 RpcId 全局查表，就只能靠「对象类型转换失败」
  去兜住「投错类」，那等于把**拒绝**退化成**解码期异常**（且只在载荷恰好可解时暴露）。
- `UnknownRpc` 的 `Detail` 会点名归属：目标类上没有该 RpcId 时附「（该 RpcId 属于类 X 的 M，
  不接受投到别的类）」（仅当该裸 RpcId 唯一、能唯一归因时）。
- `catch (InvalidCastException)` **保留但改口径**：注释明确它只是「工厂产出的运行时类型与 ClassId 不一致」
  的防御网（注册配置错），**不是**类归属判定 —— 归属已在第④步用 ClassId 定完。
- 网络继承仍未完整支持：第④步只做**精确 ClassId** 匹配，不沿基类链上查；本轮**不扩大继承实现**，
  不属于目标类的 RPC 一律拒绝。

## 8. Create 初值验收（真实生成物 + 真实世界路径，D-R0-16）

夹具侧（`Tools/PMNetE2E/E2eFixtures.cs`）：给 `E2eReplicated` 增 `protected internal override void OnReplicatedCreate()`，
在回调里读一遗四个复制成员，存入 `CreateCallbackCount / CreateHealthSeen / CreateSpeedSeen / CreateTitleSeen / CreateAmmoSeen`
（默认值刻意选 `int.MinValue` / `NaN` / `null`，避免「默认值」与「合法值」混淆）。
新增成员都不带声明 Attribute ⇒ 不进入生成器扫描面、不改变任何已分配 ID（`e2e-ids.json` 逐字节未变）。

验收（`Tools/PMNetE2E/Program.cs` 新增段 **2b**，`Y0–Y18`）：

| 判据 | 断言 |
|---|---|
| 初值随创建同包 | 服务端先赋 `health=1234 / speed=7.25 / title=create-title / ammo=88`，**首次 Tick 之前**调 `BuildLifecycleBatch`；`Y3` 断言两条连接的发送列表为空（**全程没有 Tick**）⇒ 初值不可能来自后续 Update |
| 线上真的带了初值 | `Y5` Create 记录的 `RepInitialState` 非空（拥有者 39 字节）；`Y7` 槽位表 = `[0,1,2,3]` |
| 创建回调里就读到 | `Y13` 回调恰好一次；`Y14` 回调读到的四个值 = 初值；`Y15` 本次创建**没有**派生 OnRep（初值不走增量路径） |
| 非 Owner 不得收到 OwnerOnly | `Y9` 非拥有者的初值槽位 = `[0,1,3]`（**没有** `_ammo` = 槽位 2）；`Y10` 另外三个无条件槽位仍在（排除是整体漏发）；`Y18` 非拥有者回调里 `_ammo` 仍是默认 0 |

未新增 `PMCond.Never` 属性（现有夹具没有 Never，任务明确「若现有无 Never 不必增属性」）：
新增属性会改变属性计数与 `ChangeMaskBitCount`，从而扰动既有断言。

## 9. 注释清理（生产 API 用技术原因）

- `PMRpc.cs` 段落标题 `RPC 接收入口（RV6）` → `（带世界查找 / 类归属 / 归属校验 / 校验路由的真实派发入口）`。
- `IPMNetRpcDisconnectTarget` 的「本任务硬边界不允许改它」→ 技术理由：`PMNetConnection` 只声明
  `Send / IsReady / IgnoreRpcs`，而「断开一条连接」是**宿主级**动作（原因码 / 对端通知 / 清场顺序由传输层决定），
  复制 / RPC 层不应另造一套。**保留**了明确的宿主接线指引（委托 `PMTransport.Disconnect`）。
- `PMNetRpcValidationFailedException` 的「是 R2 冻结面，不能改」→「是生成物与接收侧之间已冻结的那一面
  （生成器按它发射执行器），不能改」。
- `E2eFixtures.cs` 里的 `RV5`/`RV6` 子任务标签 → 改为该夹具**测什么**的技术描述（新增第 [4] 条：创建回调）。

## 10. 本轮实测（真实构建 + 真实运行，退出码为准）

| 命令 | 结果 |
|---|---|
| `dotnet build Tools/PMNetGen/PMNetGen.csproj -c Release` | **退出 0**，0 警告 0 错误 |
| `dotnet build Tools/PMDeclCheck/PMDeclCheck.csproj -c Release` | **退出 0**，0 错误 |
| `dotnet Tools/PMDeclCheck/bin/Release/net8.0/PMDeclCheck.dll` | **退出 0**：**71 PASS / 0 FAIL**（与上一轮同量；本次未动生成器与声明校验） |
| `dotnet build Tools/PMNetE2E/PMNetE2E.csproj -c Release` | **退出 0**，0 警告 0 错误（pre-build 目标重跑 PMNetGen：2 类 / 5 属性 / 6 RPC，GlobalProtocolHash `0x5C0B6B49` 不变） |
| `dotnet Tools/PMNetE2E/bin/Release/net8.0/PMNetE2E.dll` | **退出 0**：**209 PASS / 0 FAIL**，**5/5 负向注入命中** |

分段断言数（实际打印）：`0=4 / 1=13 / 2=29 / 2b=19 / 3=89 / 3b=23 / 4=2 / 5=16 / 6=14` ⇒ 合计 209。

本轮新增断言 **46 项**可逐项归因：段 2b 19 项（Create 初值）、段 3b 23 项（复合键）、`R16b` 1 项（错误 RpcId 可观测）、
注入⑤ 3 项。总数为 209 而上一轮报告记录为 162（差值 47）—— 其中 1 项差异**无上一轮原始输出可复核**，如实标注。

### 10.1 复合键证据（段 3b，`Ka–Ku` + `Kn2/Kn3`）

手工构造三个类（`CompositeClassA/B/C`），其中 **A 与 B 共用 `RpcId = 777`**，C 用 778：

- `Ka` 两个类**都能登记**同一个 RpcId（旧形态在这里抛「RpcId 重复注册」）；`Kb` `RpcCount == 3`。
- `Kc/Kd/Ke` `(A,777)` 与 `(B,777)` 各查到**自己的**执行器，且两者不是同一个对象。
- `Kf` ★裸 RpcId 歧义 ⇒ `TryGetRpc(777)` **返回 false**；`Kg` 唯一号 `778` 的裸查询仍为 true（歧义不外溢）。
- `Kh/Ki` ★**真实投递**（`PMNetRpcReceive.Deliver` + 真实世界 + 真实连接）给 A 类目标 ⇒ 只有 A 类实现执行、
  实参读对（11）；`Kj/Kk` 给 B 类目标 ⇒ 只有 B 类实现执行（22）。
- `Kl/Km/Kn` 共享号投给 C 类目标 ⇒ `UnknownRpc`、不执行任何实现、`Detail` 点名目标类与该 RpcId。
- `Kn2/Kn3` ★C 类**独有**的 778 投给 A 类目标 ⇒ `UnknownRpc` + 不执行，且 `Detail` 点出
  「该 RpcId 属于类 C 的 OtherC，不接受投到别的类」（可观测性）。`Ko` 对照：778 投给 C 类目标正常放行。
- `Kp/Kq/Kr` **原子性**：类内重复 RpcId 抛异常，且该 ClassId 未登记、`RpcCount` 与调用前相同（不留半个类）。
- `Ks/Kt/Ku` **复原**：`Reset()` + `PMNetGeneratedRegistry.RegisterAll()` 后类数/RPC 数回到生成物状态，
  裸查询与复合键查询都恢复可用 ⇒ **不干扰既有计数与后续断言**。

新增注入⑤（`F9/F10`）：缺陷形态 = 「查目标之前先按裸 RpcId 全局查表」⇒ 缺陷探针**失败**
（A/B 共用 777 ⇒ 裸查无解，合法的 A 类调用也会失败），生产形态探针 `F9` 在注入下仍通过。

### 10.2 Create 回调证据（段 2b，`Y0–Y18`）

```
服务端：Spawn → PMNet_Set_health(1234)/speed(7.25)/title("create-title")/ammo(88)
        RegisterObject + AddConnection(owner/other) → serverObj.OwnerConnection = ownerConn
        ★ 未调用任何 Tick ⇒ BuildLifecycleBatch(owner) = 53 字节 / (other) = 47 字节
拥有者 Create：RepInitialState 39 字节，槽位 [0,1,2,3]
非拥有者 Create：槽位 [0,1,3]（OwnerOnly 的 _ammo 被过滤）
客户端：OnReplicatedCreate 回调（各 1 次）读到 health=1234 speed=7.25 title=create-title ammo=88 / 0
        OnRep 计数仍为 0
```

注：字节数（53/47/39）与槽位列表由断言打印，可复核「初值确实在 Create 记录里」而不是事后补的。

## 11. 本轮变更文件（均在 writePaths 内）

| 文件 | 变更 |
|---|---|
| `Client/Assets/Scripts/PMNet/Declarations/PMNetDeclarations.cs` | 注册表改 `(ClassId, RpcId)` 复合键 + 裸 RpcId 歧义索引 + `TryGetRpc(uint,ushort)` + `RegisterClass` 先校验后提交原子化 + `RpcCount` 口径注释 |
| `Client/Assets/Scripts/PMNet/PMRpc.cs` | `Deliver` 改为「先目标 Active、再按目标 ClassId 取执行器」；`UnknownRpc` 细节点名归属；`InvalidCastException` 改口径为防御网；段落标题 / 断连接口 / 异常类型三处注释去边界化 |
| `Tools/PMNetE2E/E2eFixtures.cs` | `E2eReplicated` 增 `OnReplicatedCreate` 与四个快照字段；头部注释补第 [4] 条；子任务标签技术化 |
| `Tools/PMNetE2E/Program.cs` | 新增段 2b（`TestCreateInitialState` + `DecodeInitialStateSlots`，`Y0–Y18`）、段 3b（`TestRpcRegistryCompositeKey` + 手工类/执行器/世界构造，`Ka–Ku`+`Kn2/Kn3`）、`R16` 改为用存活目标并拆出 `R16b`、注入⑤（`ProbeCompositeKey` + `F9/F10`）、`_nCreateCallbacks` 可达计数 |
| `Tools/PMNetE2E/Generated/*.g.cs`、`Tools/PMNetE2E/e2e-ids.json` | 由生成器重写；声明面未变 ⇒ 内容与 ID 逐项不变（`e2e-ids.json` 六个 RPC / 四个属性 ID 与上一轮相同；E2E 段 0 的 `--decl-check` 逐字节比对通过） |
| `Docs/plans/_r2_fix_rpc.md` | 本续做报告 |

编码：三个手改文件（`PMNetDeclarations.cs` / `PMRpc.cs` / `Program.cs` / `E2eFixtures.cs`）与生成物
均为 **UTF-8 BOM + CRLF**（`efbbbf` + 全部行以 CRLF 结尾，无 LF-only 行）。

## 12. 本轮未闭合 / 诚实记录

1. **只编了 `PMDeclCheck` 与 `PMNetE2E`**（按本轮硬边界）。`PMReplicationTest` / `PMNetWorldTest` / `PMNetVerify` /
   `PMNetLangCheck` / `PMCallspaceCheck` 未复跑。已用源码检索确认：全仓（除本轮两文件与 E2E 门禁）
   **没有任何调用点**引用 `TryGetRpc` / `RegisterRpc`，且本轮改动是纯增量新增 + 兼容保留，
   按 C# 7.3 / netstandard2.0 自审（未用新语法）；但**未跑就是未验**，建议主 Agent 串行复跑。
2. **不是真实网络验收**：段 2b/3b 全部是内存投递（真实世界查找、真实连接对象、真实生成物），
   不代表 UDP 丢包 / 重排 / MTU 行为（R3 未开工）。
3. **网络继承未支持**：第④步只做精确 ClassId 匹配。若将来支持继承声明，需**同时**改生成物归属发射与
   `Deliver` 的查表（已写入生产注释）。
4. **`BadCase` 覆盖度**：段 3b 只覆盖「类内重复 + 归属不一致（隐含）」两类注册失败；未逐条覆盖
   `RpcId 0` / `OwningClassId 0` / `entry=null` 的注册失败（这些在 `RegisterRpc`/`RegisterClass` 里已明确抛，
   但本轮未加断言）。
5. **主计划口径待同步**（不在本轮写路径内，属主 Agent 收口）：`net-architecture-migration.md` §5.6 仍写
   「超限丢最旧并计入 `DroppedPendingRpcs`」，与已修复语义（显式失败 + `RejectedPendingRpcs`）相反；
   RV1–RV7 状态列也仍为 `PENDING`。
6. **`PMNetRpcEntry.Write` 缺失**、数组快照浅拷贝、字符串不拷贝等既有取舍未变。

