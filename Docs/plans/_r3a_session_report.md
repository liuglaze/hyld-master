# R3-A2 实施报告：已认证连接的会话接线（Session 层）

> 落点：`Client/Assets/Scripts/PMNet/Session/PMTransportConnection.cs`、`PMNetSessionBridge.cs`
> （本轮另含**必要最小**的 `Transport/PMTransport.cs`、`Transport/PMTransportTypes.cs` 修改）；
> 门禁：`Tools/PMNetSessionTest`（343 项断言）+ `Tools/PMTransportTest`（81 项断言），
> 均为真实 `PMTransport` + 真实 `PMNetWorld` + 真实 RPC 接收入口 + 真实复制层，不引 Unity、不依赖生成器。
> 事实来源：`net-r3-control-contract.md` §2/§4/§5、`net-r0-contract.md`、
> `_r2_fix_rpc.md`、`_r2_fix_replication.md`、`Docs/plans/_r3a_session_review.md` **全文**。
> **T42 仍为 PENDING**。

## 摘要（本轮结论）

会话适配器（`PMTransportConnection` + `PMNetSessionBridge`）已在上一轮完成接线；**本轮专门闭合
`_r3a_session_review.md` 的 4 项确定问题**，并把任务点名的 M03 上限问题从「适配层绕过」升级为
「传输层自己显式拒绝」：

1. **复制域方向闸门**（安全）：服务端**只接受** `ReplicationAck`、客户端**只接受** `Replication`
   (Update)，违反即 `ReplicationDirectionRejected++` + `ProtocolError` 断连。
   门禁用**完全合法 codec**（真实 `PMReplicationWriter`）构造上行 Update，证明：权威属性不被改写、
   不应用记录、**不回 ACK**；并用**同一份字节走合法方向**做对照，证明拒绝的原因是方向而不是载荷非法。
2. **帧内数据报预算**（有界性）：M03 新增**兼容重载** `Update(nowMs, maxDatagramsThisFlush)`
   （原 `Update(nowMs)` 语义不变），桥在帧首登记本帧预算、帧首与帧尾**共享同一余量**，
   帧内总量**严格** `<= FrameDatagramBudget(32)`（旧实现上界 2×32−1 = **63**）。
3. **生命周期批次发不出去**（可靠性）：`FlushLifecycle` 失败不再只记告警 —— 计数 + `Warn` +
   专用断开原因 `LifecycleBatchOversize` **显式断连**（世界/复制/桥成对摘除），断开后重连由世界的
   `AddConnection` 重新排队全量 Create。
4. **`TryActivate` 不校验连通性**（生命周期）：死链路上拒绝激活（`RejectedInactiveTransport++`），
   `RegisterConnection` 再加一道硬前置 —— 两层**各自充分**（注入验证过），消除僵尸连接。

另完成：`PMTransport.Send` 对 `count > 65535` **显式拒绝**（不再 `(ushort)` 静默截断）、
数组窗口校验**溢出安全**、分片数超限**单独记账且与可靠窗溢出分离**（含专用断开原因）。
**未改线格式**（总长仍是 `ushort`，没有扩成 `uint`），T38 语义未破坏（`PMTransportTest` 原 8 节全绿 + 新增 1 节）。

门禁实测：**343 项 / 0 失败（exit 0）** 与 **81 项 / 0 失败（exit 0）**；5 组缺陷注入全部被抓到。

---

## 1. 本轮闭合的 4 项确定问题（逐条：问题 → 修复 → 证据）

### 问题 1【安全】复制域缺消息级方向闸门

**审查结论**：适配器只拦了 `Lifecycle` 方向；`Kind=Replication` 的 Update 在服务端**照单全收**。
复制层 `PMReplicationChannel.OnMessage` → `TryApplyRecord` 全程**没有角色判断**，也**不校验来源与版本**，
因此一个已认证客户端可用同名描述符伪造一条格式完全合法的 Update，命中服务端世界里同 NetId 的
**权威对象**并写入它的属性，服务端还会把该版本当已确认**回 ACK** 并派发 OnRep。

**修复**（`Session/PMTransportConnection.cs`，`HandleReplicationMessage` 入口）：
在解析载荷之前先做**只看方向**的闸门 ——
服务端（`_world.IsServer`）只允许 `PMRepMessageKind.Ack`，客户端只允许 `PMRepMessageKind.Update`；
违反 ⇒ `ReplicationDirectionRejected++` + `ProtocolError(...)`（与生命周期方向违反同一纪律：拒绝并断连）。
当前设计中不存在「客户端自治上行」（客户端的复制通道不登记任何对象、`Tick()` 不产生 Update），
因此合法流量永远不会撞上这道闸门。

**门禁证据**（`PMNetSessionTest` E 节）：
- `④`：用 `PMReplicationWriter.WriteUpdate` 造合法正文（魔数 0xA7/版本 1/kind=Update/记录指向真实对象），
  客户端上行 ⇒ `ReplicationDirectionRejected` 增长、`actor.Health` **仍是 11**、`UpdateRecordsApplied` 不变、
  `Replication.Stats.AcksSent` 不变、连接层 `ReplicationAckSent == 0`、`Transport` 断开且原因 `ProtocolError`。
- `④′`（对照，防「因载荷非法而通过」）：**同一份正文字节**由服务端发往客户端 ⇒ 客户端 `Health == 7777`、
  `UpdateRecordsApplied` 增长、客户端回传 ACK、连接保持连通。
- `④″`：在**方向合法**的信封里（`ReplicationAck`）塞 Update 内部类型 ⇒ 走「信封↔内部类型一致性检查」
  （`ReplicationDirectionRejected` 不增长）并断连 —— 两条分支各自可达。
- `④‴`：服务端下行 `ReplicationAck`（合法 Ack 正文）⇒ 客户端判方向非法并断连。

**缺陷注入**：把服务端分支条件改成恒 `false`（去掉闸门）⇒ **6 项失败**，其中关键两条是
`服务端权威对象的属性**未被改写**（实际 7777，期望 11）` 与 `服务端**没有回 ACK**（实际 1，期望 0）`
—— 即审查描述的利用链在门禁里真实可复现，闸门是唯一阻挡点。

### 问题 2【有界性】「每帧数据报预算 32」实际是 2×32−1 = 63

**审查结论**：桥每帧调用两次 `Transport.Update`，而 `MaxDatagramsPerUpdate` 只约束**单次**
`FlushOutbound`；帧尾只在「本帧已发 ≥32」时跳过，于是「帧首 31 + 帧尾 32」可达 63。

**修复**：
- `Transport/PMTransport.cs`：新增**兼容重载** `Update(long nowMs, int maxDatagramsThisFlush)`；
  原 `Update(long nowMs)` **语义不变**（等价于传 `_config.MaxDatagramsPerUpdate`）；
  传入值被**夹到** `MaxDatagramsPerUpdate` 以内（调用方传大数不能突破传输层自己的上限）；
  传 0/负数表示「只 drain、本次不发数据报」。新增只读 `MaxDatagramsPerUpdate`。
- `Session/PMTransportConnection.cs`：`BeginFrameDatagramBudget(budget)`（帧首登记起点与总量）、
  `RemainingFrameDatagramBudget`（= 帧总预算 − 本帧已发）、`internal Update(now, maxDatagramsThisFlush)`。
- `Session/PMNetSessionBridge.cs`：帧首 `BeginFrameDatagramBudget(FrameDatagramBudget)` 后
  `Update(now, RemainingFrameDatagramBudget)`；帧尾同样传余量；余量为 0 才跳过并计入
  `DatagramsDeferredByBudget`（消息留在传输层有界队列里，**可靠消息不丢**）。

**门禁证据**（新增 `J` 节，**严格上界**）：
- 「31 + 32 形状」：帧首积压 **31** 个数据报（31 条 1166 字节不可靠消息，各占一个数据报），
  同一帧内有**本帧新产出**（250 条 200 字节初值的 Create ⇒ 约 53KB 生命周期批次 ⇒ 约 **46** 个分片）
  ⇒ 帧内总量实测 **32**（旧实现此处为 63），`ReliableOutstanding > 32`（证明需求大于预算、
  且可靠分片没被丢），后续 10 帧峰值 `<= 32`，末帧不再产生数据报（积压排空）。
- 「饱和形状」：帧首积压 40 ⇒ 帧首发满 32、帧尾 flush 被**跳过**并计入 `DatagramsDeferredByBudget`，
  本帧仍然只发 32（旧实现会再补 32 ⇒ 64），且被跳过的只是 flush（生命周期消息仍在传输层队列里）。

**缺陷注入**：仅把帧尾改回 `connection.Update(nowMs)`（等价旧行为）⇒ 首帧实测 **63**、
多帧峰值 **63**（2 项失败），与审查给出的数字完全一致。

### 问题 3【可靠性】生命周期批次超限/发送失败 ⇒ 整批永久丢失

**审查结论**：`PMNetWorld.BuildLifecycleBatch` 在返回批次前已 `state.Pending.Clear()`；
旧 `FlushLifecycle` 在 `Wrap` 失败或传输层拒绝时只记 `LifecycleOversize`/`LifecycleSendFailed` + Warn，
**不重排、不分片、不断连** ⇒ 对端永远拿不到这些 Create，后续复制 Update 被 `DroppedUnknownObject` 丢、
指向这些对象的 RPC 被 `UnknownTarget` 拒，且 NetId 不复用、无重发源 ⇒ **不可自愈**。

**修复**（`Session/PMNetSessionBridge.cs`）：
- `FlushLifecycle` **返回值语义**改为「true = 可以继续（无待发事件或已成功交给传输层）；
  false = 失败且已显式处理（连接被断开）」；客户端/非权威侧视为「无需处理」返回 true。
- 两条失败路径统一处置：计数（`LifecycleOversize` / `LifecycleSendFailed`）+ `Warn` +
  `DisconnectForLifecycleFailure`（`LifecycleDisconnects++` + `Disconnect(PMDisconnectReason.LifecycleBatchOversize)`）。
- `SendRpc` 在 `FlushLifecycle` 返回 false 时**不再**往该连接发 RPC（不再发一条指向
  「对端永远不会知道的对象」的 RPC）。

**门禁证据**（新增 `K` 节）：小批次正常送达（对照）；一帧内 400 条带 200 字节初值的 Create（约 86KB > 64KiB）
⇒ `LifecycleOversize` 与 `LifecycleDisconnects` 增长、`Transport` 断开、原因 `LifecycleBatchOversize`、
告警含「生命周期批次」、世界/复制/桥三处连接计数归零、旧连接的待发表随之丢弃；
新连接激活后 `PendingEventCount == ObjectCount`（重连由世界重新排队全量 Create ⇒ 自愈路径存在）。

**缺陷注入**：把 `Disconnect(...)` 调用去掉（模拟旧的「只记 Warn」）⇒ **5 项失败**，
`World.ConnectionCount`/`Replication.ConnectionCount`/`Bridge.ConnectionCount` 全为 **1**
（世界里有 400 个对象、对端永远没有），且 `IsReady == false` ⇒ 没有任何摘除者。

**边界（未闭合，见 §5）**：真正的「按字节预算分批」需要把剩余事件留在待发表里，
即在 M04 的 `BuildLifecycleBatch` 上开一个字节预算出口 —— **不在本轮写边界**，已登记为待办。

### 问题 4【生命周期】`TryActivate` 不校验连通性 ⇒ 登记出无法自动摘除的僵尸连接

**审查结论**：在已断开的传输上也能 `TryActivate` 成功并登记进世界/复制；由于断开回调
（`_disconnectHandled`）早已跑完且传输层 `Disconnect` 幂等，这条连接再没有对手能摘除它：
客户端受信服务器槽位被占死、服务端 uid 永久占用、世界侧长期挂一份发不出去的 Create 待发队列。

**修复**（两层，各自充分）：
- `PMTransportConnection.TryActivate`：在身份校验之前要求 `Transport != null && Transport.IsConnected`，
  否则 `RejectedInactiveTransport++` + `TryActivate` 返回 false（不触发 `Disconnect`，因为已经断开）。
  这同时钉住了不变量「`TryActivate` 成功 ⇒ `IsReady` 为真」。
- `PMNetSessionBridge.RegisterConnection`：硬前置「连接必须是活的」，否则 `RejectedConnections++` 并拒绝登记。

**门禁证据**（`PMNetSessionTest` B 节 `⑩`）：先 `Transport.Disconnect(PeerClosed)` 再 `TryActivate`
⇒ 返回 false、原因可归因、`RejectedInactiveTransport == 1`、`IsActivated == false`、`IsReady == false`、
世界/复制/桥计数均为 0、且 `RejectedConnections == 0`（说明拒绝发生在桥的接受规则**之前**）；
另加不变量正例「活链路 `TryActivate` 成功 ⇒ `IsReady == true`」。

**缺陷注入**：
- 两层都去掉 ⇒ **7 项失败**：`IsActivated == true`、`IsReady == false`、世界/复制/桥各 1、无摘除者
  —— 审查描述的僵尸连接被完整复现。
- 只去掉桥前置（保留 `TryActivate` 自己的闸门）⇒ **全绿 exit 0**，证明内层闸门单独充分。

---

## 2. M03 上限的显式化（任务点名要求）

审查与本轮都确认：分片头用 `ushort` 写「整条消息总长」，因此**实际可搬运上限是 65535 字节**
（与契约 64 KiB 差 1 字节），而旧实现在超限时直接 `(ushort)count` ⇒ **静默截断**
（不报错、不断连；对端要么永远重组不出来、要么拼出长度不符的脏数据）。

**处理（不扩线格式）**：
- `PMTransport.MaxTransportableMessageBytes = ushort.MaxValue`（公开常量），
  `Send` 对 `count > 65535` **显式拒绝**（`SendRejectedOversize++` + `LastSendRejectReason = MessageTooLarge`）。
  **没有**把总长字段扩成 `uint`（那会静默改变与既有对端的字节契约）。
- 数组窗口校验改为**溢出安全**（`offset > payload.Length || count > payload.Length - offset`）：
  旧写法 `offset + count > payload.Length` 在 `offset`/`count` 接近 `int.MaxValue` 时回绕成负数，
  于是越界窗口被放行、随后组包 `BlockCopy` 抛异常。新增 `SendRejectedWindow` 计数。
- 分片数超限**单独拒绝并单独记账**（`SendRejectedFragmentLimit` + `LastSendRejectReason = FragmentLimitExceeded`），
  并与可靠窗溢出**分离**：新增 `PMSendRejectReason`（None/Disconnected/InvalidWindow/MessageTooLarge/
  FragmentLimitExceeded/ReliableBufferOverflow）与新增断开原因
  `PMDisconnectReason.MessageTooLarge(8)` / `FragmentLimitExceeded(9)`；
  适配层 `SendEnvelope` 按真实原因选择断开理由（修掉审查「原因误标为 ReliableBufferOverflow」）。

**门禁证据**：
- `PMTransportTest` 新增 `I` 节：窗口 6 例（含 `int.MaxValue` 两例）全部拒绝且不抛、计数与原因正确；
  70000/65536 显式拒绝且不产生数据报、无残留、不断连；**临界 65535 字节被接受且对端完整重组**
  （逐字节比对）；分片超限拒绝且**不断连**（与可靠窗溢出区分）；`Update(now, 0)` 只 drain、
  `Update(now, 7)` 恰好 7 个、传 1000 被夹到 32、原 `Update(now)` 仍是 32/批且最终能排空。
- `PMNetSessionTest` G 节 `①`：略过适配器、直接把 70002 字节交给传输层 ⇒ **显式拒绝**、
  无数据报产生、原因 `MessageTooLarge`、计数增长、无残留、不断连（旧断言「被传输层接受」已按新语义改写）。
  `①′`：60016 字节的**合法 RPC 信封**（目标 NetId 不存在 ⇒ 只拒绝不断连）⇒ 跨帧发完，
  单帧峰值 `<= 32`，对端**完整**收到（`LastInboundMessageBytes == 60016`），连接保持连通。

**缺陷注入**：
- 恢复静默截断（去掉超限检查）⇒ **4 项失败**（`70000 ⇒ 显式拒绝`、`65536 ⇒ 显式拒绝`、
  `MessageTooLarge`、`SendRejectedOversize == 2`）。
- 恢复回绕窗口算术 ⇒ **5 项失败**（含新加的「offset 远超长度且回绕 ⇒ 拒绝」与
  「可靠缓冲里没有残留半个消息」—— 旧实现把这条越界消息放进了可靠缓冲，随后组包才会抛）。

---

## 3. 接口变更（供 R3-B 宿主与主 Agent 集成）

| 变更 | 位置 | 语义 | 兼容性 |
|---|---|---|---|
| `PMTransport.Update(long nowMs, int maxDatagramsThisFlush)` | `Transport/PMTransport.cs` | 带本次发送预算的驱动；`<=0` 只 drain；传入值夹到配置上限 | **新增重载**；原 `Update(long)` 语义不变（等价传配置值） |
| `PMTransport.MaxDatagramsPerUpdate` | 同上 | 配置值的只读视图 | 新增只读属性 |
| `PMTransport.MaxTransportableMessageBytes` | 同上 | 线格式可表达上限 `65535` | 新增常量 |
| `PMTransport.LastSendRejectReason` + `enum PMSendRejectReason` | `Transport/PMTransportTypes.cs` | `Send` 最近一次被拒的**具体原因** | 新增；`Send` 返回 false 的语义不变 |
| `PMTransportStats.SendRejectedWindow/Oversize/FragmentLimit` | 同上 | 三类拒绝分别记账 | 新增字段（`ToString` 追加 `sendRej`） |
| `PMDisconnectReason.MessageTooLarge(8)` / `FragmentLimitExceeded(9)` / `LifecycleBatchOversize(10)` | 同上 | 更精确的断开原因（1 字节枚举值，线格式宽度不变） | **新增枚举值**；既有取值不变 |
| `PMTransportConnection.BeginFrameDatagramBudget(int)` / `RemainingFrameDatagramBudget` / `FrameDatagramBudget` | `Session/PMTransportConnection.cs` | 帧预算登记与余量查询（桥用） | 新增（`BeginFrameDatagramBudget` 为 internal） |
| `PMTransportConnection.Update(long, int)`（internal）/ `ReplicationDirectionRejected` / `RejectedInactiveTransport` | 同上 | 帧内带预算驱动；方向拒绝与死链路激活拒绝计数 | 新增 |
| `PMNetSessionBridge.FlushLifecycle` 返回值语义 | `Session/PMNetSessionBridge.cs` | true = 可以继续；false = 失败且已断连 | **行为变更**（旧调用方忽略返回值者不受影响） |
| `PMNetSessionBridge.LifecycleDisconnects` | 同上 | 生命周期批次失败导致的显式断连次数 | 新增 |

R3-B 注意：`Bridge.Update(now)` 一帧调用一次即可（内部已按帧预算驱动两次 transport）；
`FlushLifecycle` 返回 false 时该连接已被断开，不要继续在其上发送。

---

## 4. 门禁结果（原始退出码为准）

```
dotnet build Tools/PMNetSessionTest/PMNetSessionTest.csproj -c Release   -> 0 警告 / 0 错误
dotnet Tools/PMNetSessionTest/bin/Release/net8.0/PMNetSessionTest.dll    -> exit 0
    全部通过：343 项检查，0 项失败
dotnet build Tools/PMTransportTest/PMTransportTest.csproj -c Release     -> 0 警告 / 0 错误
dotnet Tools/PMTransportTest/bin/Release/net8.0/PMTransportTest.dll      -> exit 0
    全部通过：81 项检查，0 项失败
```

分段断言数（实际打印，`PMNetSessionTest` 合计 343）：

| 段 | 内容 | 断言数 | 本轮变化 |
|---|---|---|---|
| A | 应用信封：版本/Kind/有界/严格拒绝 | 28 | — |
| B | 激活闸门：未激活不派发不发送、摘要/世代/角色规则 | 48 | +12（`⑩` 死链路激活 + 不变量正例） |
| C | 双端连接与真实 PMTransport：Create 初值 | 27 | — |
| D | 双向 RPC 与方向/归属 | 33 | — |
| E | 摘要/世代/布局/ClassId 不符 ⇒ 拒绝或断连 | 60 | +27（复制方向闸门 4 组用例） |
| F | 原生校验失败 ⇒ 真实 Transport 断连 | 18 | — |
| G | 未知来源/超限/入站有界/每帧数据报预算 | 36 | +12（70002 显式拒绝 + ①′ 上限内大消息） |
| H | 丢包重排：属性收敛、可靠 RPC 一次交付、Destroy 后拒绝 | 32 | — |
| I | Dispose/断开幂等、成对摘除、计数非空 | 26 | — |
| J | **每帧数据报预算严格上界（帧首 31 + 帧尾 32 场景）** | 17 | +17（新增） |
| K | **生命周期批次超限：显式断连而非静默丢弃** | 18 | +18（新增） |

`PMTransportTest` 由 47 项扩到 **81** 项（新增 `I` 节：直接 `Send` 的窗口/超上限/分片上限
与发送预算入口），原 A–H 节（T38 语义）**全部保持通过**。

---

## 5. 缺陷注入复核（5 组，全部被抓住；生产代码按 md5 还原）

| 组 | 注入形态 | 观察到的失败（摘） | 结论 |
|---|---|---|---|
| 1 | 去掉复制方向闸门（服务端分支恒 false） | `属性**未被改写**（实际 7777，期望 11）`、`没有回 ACK（实际 1，期望 0）`、`UpdateRecordsApplied 实际 1`、`ReplicationDirectionRejected 实际 0`、`未断开`（6 项） | 利用链真实可复现；闸门是唯一阻挡点 |
| 2 | 帧尾改回 `connection.Update(nowMs)` | `帧内总发送量**严格**等于预算（实际 63，期望 32）`、`多帧峰值 63 ≤ 32`（2 项） | 与审查预测的 2×32−1 数字**精确一致** |
| 3 | `FlushLifecycle` 失败时不 `Disconnect` | 世界/复制/桥计数各为 1、`IsReady=false`、原因 `None`（5 项） | 「世界有对象、对端永远没有」的不可自愈态被复现 |
| 4a | 两层连通性前置都去掉 | `IsActivated=true`、`IsReady=false`、世界/复制/桥各 1（7 项） | 僵尸连接被完整复现 |
| 4b | 只去掉桥前置（保留 `TryActivate` 闸门） | 无（exit 0） | 内层闸门单独充分（两层是纵深防御） |
| 5a | 恢复 `(ushort)count` 静默截断 | `70000 ⇒ 显式拒绝`、`65536 ⇒ 显式拒绝` 等（4 项） | 上限回归有效 |
| 5b | 恢复回绕窗口算术 | `offset 远超长度且回绕 ⇒ 拒绝`、`窗口拒绝全部被记账（实际 4）` 等（5 项） | 溢出安全回归有效 |

---

## 6. 仍未闭合 / 边界外待办（诚实口径）

1. **M04：`BuildLifecycleBatch` 的字节预算出口缺失**（本轮以显式断连兜底）。
   当前行为：单帧生命周期批次 > 应用消息上限 ⇒ `LifecycleBatchOversize` 断连 + 重连自愈；
   世界侧统计（`CreatesSent`）已把「被丢掉的批次」记为已发（世界内部计数，M04 边界内修正）。
2. **M06：`PMReplicationChannel.RemoveConnection` 未清理 `_ackQueues`**（每连接生命周期泄漏一个列表）；
   同一个类的「`Multicast` 相关性」以 `viewer=null` 调用 `IsNetRelevantFor` ⇒ 恒真（等价广播）。
   两项都在 `Replication/`，**不在本轮写边界**。
3. **M03：桥的两次 `Update` 也把单帧入站解析预算翻倍**（`MaxDatagramsPerUpdate*4=128` → 256）。
   入站队列本身有界（4096）且可观测，本轮未改（不在验收条目内），仅登记。
4. **未激活期间被丢弃的可靠消息仍被传输层 ACK**（审查「高概率推断」第 4 条）：
   契约要求「不静默丢可靠消息」，而实现上「不派发」与「已 ACK」并存。
   建议 R3-B 明确宿主次序「先 `TryActivate` 再放行该链路数据报」，或在未激活时**拒收入队**；
   本轮不改 `OnDatagram`（会改变「未激活不派发」既有证据口径），仅在报告登记。
5. **1 字节夹缝**：`PMApplicationEnvelope.Wrap` 的上限是契约的 64 KiB，而发送闸门是 65535；
   发送路径已统一用 `EnforceableMaxBytes = min(二者)` 收口（`SendEnvelope`、`BuildRpcEnvelope`），
   `Wrap` 自身语义未改。
6. **T42 保持 PENDING**：真实 Lobby 启动 Unity DS、加载权威场景、两客户端闭环未做。

---

## 7. 未验证 / 诚实边界

- **真实 UDP 与真实网络**：门禁仍是**同进程可控链路**（`IPMTransportLink` 注入丢包/重复/乱序），
  M03 的序号/ACK/NAK/去重/分片路径真实参与，但没有真实 socket、RTT/MTU/乱序分布。
- **Unity 宿主**：未编译 Unity、未接 `MonoBehaviour.Update`、未接项目日志（`Warn` 由宿主赋值）。
- **生成物路径**：仍用**手工描述符**独立验收适配器；`RemoteSender` 只做签名逐参数静态核对（生成链由 R2 覆盖）。
- **A1 的控制协议/票据**：零依赖；「`TryActivate` 之前必须验证票据并绑定真实端点」是**契约要求**，
  本轮只在代码与报告声明，未在运行时强制。
- **真实 Lobby/DS 全链路、两客户端 T42**：未做。
- 所有缺陷注入均为「临时改生产代码 → build → run → 按 md5 还原」，**未编译 Unity**；
  本轮**未执行任何 git/svn 写操作**（无提交、无回滚、无 `checkout`/`reset`）。

---

## 8. 事故与恢复记录（必须透明）

本轮在给 `Tools/PMTransportTest/Program.cs` 做缺陷注入时发生一次**误截断**，如实记录如下：

- **原因**：用 `python` 的 `open(path, 'wb')` 打开该文件后，脚本因参数类型错误提前抛异常；
  `'wb'` 打开时已把文件截断为 **0 字节**，写入从未发生。
- **可恢复性排查**：该文件**未纳入 git**（`git ls-files --error-unmatch` 不匹配，`HEAD` 中不存在）；
  仓库内无副本；无 `.bak`/编辑器本地历史（`%APPDATA%\Code\User\History` 全文检索无命中）；
  `D:/unity/hyld-master/...` 等其它 checkout 不存在；`~/.claude` 等其它 agent 日志无相关记录。
- **恢复方式**：从 pi 会话日志
  （`.../sessions/--D--UE_Project-ProjectMecury--/2026-09-18T06-17-42-594Z_*.jsonl`）
  找到该文件的 authoring 工具调用：**1 次 `write`（23470 字符）+ 2 次 `edit`（共 3 个编辑块）**，
  逐块校验 anchor 在原文中**恰好出现 1 次**后重放，得到 29766 字节的完整内容（并以该文件既有的
  **BOM + LF** 写回）。
- **恢复验证**：重放内容 build 0 警告 0 错误、run **47 项检查 / 0 失败 / exit 0**；
  且与同目录 `Tools/PMTransportTest/review-verification.log`（独立复核产物）的 **47 条断言逐条同名同结果**一致。
- **残差**：重放内容比事故前的磁盘版本少 **6 行 / 351 字节**，差异均为注释/空白
  （断言数量 47、名称与结果完全一致）；该 6 行来自某个**未落盘的后续会话注解**，无可达副本，
  因此**无法逐字节还原**。随后在本文件上重新施加本轮新增的 `I` 节（33 项断言），最终 **81 项 / 0 失败 / exit 0**。
- **后续防范**：批量改文件一律改为「先 `open(...,'rb')` 读取并校验 anchor → 在内存中替换 →
  以 `open(...,'wb')` 一次性写出（写不出就不打开）」，或用带 `oldText` 唯一性校验的工具编辑接口。

---

## 9. 变更文件清单（全部在硬写入边界内）

| 文件 | 编码约定 | 变更 |
|---|---|---|
| `Client/Assets/Scripts/PMNet/Session/PMTransportConnection.cs` | BOM + CRLF | 复制域方向闸门；`TryActivate` 连通性前置；帧预算字段/入口；发送失败原因分类；新增计数 |
| `Client/Assets/Scripts/PMNet/Session/PMNetSessionBridge.cs` | BOM + CRLF | 帧内**共享**数据报预算；`FlushLifecycle` 失败即显式断连；`SendRpc` 依返回值收敛；`RegisterConnection` 前置；新增计数 |
| `Client/Assets/Scripts/PMNet/Transport/PMTransport.cs` | BOM + LF | `Update(now, sendBudget)` 兼容重载；`Send` 显式拒绝超上限/溢出安全窗口/分片上限；`LastSendRejectReason`；新统计 |
| `Client/Assets/Scripts/PMNet/Transport/PMTransportTypes.cs` | 无 BOM + LF | `PMSendRejectReason`；3 个新断开原因；3 个新统计字段 |
| `Tools/PMNetSessionTest/Program.cs` | BOM + CRLF | E（复制方向 4 组）、B⑩（死链路激活）、G①/①′（超上限与大消息）、新增 J/K 节；`BigActor` 与复制正文构造辅助 |
| `Tools/PMTransportTest/Program.cs` | BOM + LF | 新增 `I` 节（直接 `Send` 边界 + 发送预算入口）；**曾误截断并按 §8 恢复** |
| `Docs/plans/_r3a_session_report.md` | BOM + LF | 本报告 |

未改动：`World/`、`Replication/`（含 `PMReplicationChannel`）、`Declarations/`、`PMRpc.cs`、
`Generated/`、Generator、`Control/`（A1）、`PMDsHost`、旧客户端 `MainPack` 链路、`Server/`（C# 服务端）。
