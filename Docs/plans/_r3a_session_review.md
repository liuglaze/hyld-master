# R3-A2 独立复核（Session 层）：方向闸门 / 预算 / 生命周期批次 / 激活连通性

> 复核对象：`Client/Assets/Scripts/PMNet/Session/PMTransportConnection.cs`、`PMNetSessionBridge.cs`；
> 门禁：`Tools/PMNetSessionTest/Program.cs`（259 项断言）。
> 判据：`net-r3-control-contract.md` §4/§5、`net-r0-contract.md`（D-R0-05/07/12/14/18/30/42/43/44/45/46）、
> `net-architecture-migration.md` R3A3/R3A4 行、`_r3a_session_report.md` 全文、`_r2_fix_replication.md` §7。
> 边界：只读 Session 及其直接引用的必要函数；唯一写入为本文件。**未编译、未改实现、未递归委派**。
> 口径：控制契约要求 `Activate` 由**已认证宿主**驱动，本文不把「缺 A1 真实票据 acceptor / 缺端点消费账本」当缺陷重复上报。

## 摘要

R3-A2 的激活闸门、信封编解码、RPC 方向与归属、断开成对摘除总体对齐契约；本轮复核确认
**4 个必须修复问题**，其中 2 个是安全/正确性硬缺陷：

1. **复制域没有方向闸门**：适配器只拦了 `Lifecycle`（服务端收到生命周期消息 ⇒ 断连），
   但 `Kind=Replication` 的 Update 在服务端**照单全收**并写入权威对象。已认证客户端可伪造
   一条合法 Update 覆盖服务端权威属性（并被服务端回 ACK、派发 OnRep）。契约 §4 的方向要求
   目前只落实成「连接级白名单」，没有消息级方向闸门。
2. **「每帧数据报预算 32」不成立，实际上界 2×32−1=63**：桥每帧调用两次 `Transport.Update`，
   帧首那次自身最多发 32 个数据报，帧尾那次只在「本帧已发 ≥32」时跳过，因此
   「帧首发 s≤31 + 帧尾补 32」可达 63。报告写的「帧内总发送量仍受预算约束」与门禁断言
   `≤ 32+1` 都基于错误模型；漏检原因是门禁只构造了「积压饱和」场景，帧尾没有本帧新产出。
3. **生命周期批次超限即整批永久丢失**：`BuildLifecycleBatch` 先把待发表清空，之后
   `SendLifecycleEnvelope`/`Wrap` 失败只记 `LifecycleOversize`/`LifecycleSendFailed` + Warn，
   不重排、不分片、不断连 ⇒ 对端**永远拿不到**这些对象的 Create（后续 Update 按未知对象丢弃），
   形成不可自愈的失同步。
4. **`TryActivate` 不检查传输连通性**：在**已经断开**的传输上调用也能返回 `true` 并登记进
   世界/复制层；由于断开回调（`_disconnectHandled`）早已跑完，这条连接不会再被摘下
   ⇒ 客户端受信服务器槽位被占死（后续合法服务器连接被拒）、服务端 uid 永久占用（重连被拒）、
   世界侧为死连接长期挂一份 Create 待发队列。这与「连接断开未清数据」「激活失败首包」两条关注点直接相关。

另确认：未激活不派发/不发送（有正向计数证据）、Epoch/双方摘要/注册表摘要/身份字段/接受规则、
RPC 的 ClassId/Layout/方向/归属（全部用 `world.IsServer` 口径，未误用 `IsServerSide`）、
`Disconnect`/`Dispose`/桥 `Dispose` 幂等、世界与复制成对摘除 —— 这些均正确。可靠性队列溢出、
消息长度超限、入站队列有界的**拒绝是可观测的**（计数 + 断开原因），但生命周期那一路
「可观测」并不等于「可恢复」（见问题 3）。M03 的 `ushort` 总长上限（65535、静默截断）
已被上一轮正确登记，本轮不重复。

---

## 一、已确认（代码可证）

### 问题 1【必须修复·安全】复制域缺消息级方向闸门：客户端可改写服务端权威属性

**路径/行号**

- 适配器入站：`Session/PMTransportConnection.cs:758/762`（分派 `Replication`/`ReplicationAck`）
  → `:894 HandleReplicationMessage` → 只做 `:911` 头解码、`:917` 魔数/版本、`:921` 信封类型 ↔ 内部类型
  → `:928 _bridge.Replication.OnMessage(this, payload, offset, count)`（**无任何 role/方向判断**）。
- 对照：同一文件 `:788 HandleLifecycleMessage` → `:793` 明确 `if (_world.IsServer) ProtocolError(...)`。
- 核心：`Replication/PMReplicationChannel.cs:981 OnMessage` 全程无角色判断（全文件 `grep IsServer/IsServerSide`
  仅命中 103/105 的注释与 `ResolveViewRole`，接收侧 0 命中）；每条记录直接 `World.TryFind(rec.NetId)`
  → `ResolveEntry` → `TryApplyRecord`（`:1101`）写活对象、`:1257-1266` 统一派发 OnRep、
  `:1050 QueueAck` 回 ACK。`TryApplyRecord` 只做「结构 + 值可解码」校验，**不校验来源连接是否有权写该对象**。
- 服务端世界侧对象必然可被客户端按 NetId 命中（Create 记录里就带 NetId），攻击者还能用
  同一份生成描述符构造合法记录（双方 `ProtocolHash` 必须一致才会激活，描述符口径相同）。

**复现（最小）**

1. 建立已激活双端（测试里即 `MakeActivatedPair`）：服务端 `World.Spawn(actor)` +
   `Bridge.RegisterReplicatedObject(actor)`，跑几帧让客户端拿到副本。
2. 客户端侧用 `PMReplicationWriter.WriteUpdate(writer, records)`（`Replication/PMReplicationWriter.cs:49`）
   造一条记录：`NetId = actor.NetId`、槽位/`PropertyId`/值都取本端同名描述符（值字节恰好解码到末尾）。
3. `PMApplicationEnvelope.Wrap(scratch, PMSessionMessageKind.Replication, body, ...)`
   后 `client.Conn.Transport.Send(PMStream.Replication, false, env, 0, env.Length)`。
4. 服务端跑 1–2 帧：`:917/:921` 两道检查都通过（魔数 `0xA7`、版本 `1`、内部 kind `Update=1`），
   记录被应用 ⇒ **服务端权威 `actor` 的属性被改写**，`Replication.Stats.UpdateRecordsApplied` 增长，
   并且服务端把该版本当已确认回 ACK（`QueueAck`），`OnRep` 也会在服务端派发。

**为何门禁漏检**：门禁**尝试过**这个攻击但载荷构造错了 ——
`Tools/PMNetSessionTest/Program.cs:1082` 的 `fakeRep` 正文是 `{1,1,1,1}`，第一字节魔数读成 `1 ≠ 0xA7`，
于是死在 `:917` 的「魔数/版本不符」分支，`:921`「信封类型与内部消息类型不一致」这条分支**从未被执行**；
断言文案（「复制信封与内部消息类型不一致」）与实际走的代码路径不一致，属于「断言名与路径错位」。
全文件也**没有**任何「客户端上行复制 Update 必须被拒」的断言（`grep` 无命中）。
即：唯一一次伪造复制的用例因为魔数写错而「因错误的原因通过」。

**最小修复方向**：在 `HandleReplicationMessage` 前置角色闸门 —— 服务端世界拒绝 `Replication`(Update)
（仅允许 `ReplicationAck`）、客户端世界拒绝 `ReplicationAck`；若要为将来的「客户端自治上行」留口，
则必须同时按对象归属过滤（`TryApplyRecord` 入口校验来源连接是 `OwnerConnection`/Owner 链），
并补一条「拒绝 + 计数 + 活对象未被改写」的门禁断言。**这是 T42 之前必须闭环的一条。**

### 问题 2【必须修复·有界性】帧内数据报预算实际是 2×budget−1，不是 32

**路径/行号**

- `Session/PMNetSessionBridge.cs:362-375`：帧首 `:372 FrameDatagramStart = Stats.DatagramsSent`，
  `:373 connection.Update(nowMs)` —— 该调用**内部含一次完整 FlushOutbound**。
- `Session/PMNetSessionBridge.cs:415-433`：帧尾 `:425 sentThisFrame = DatagramsSent − FrameDatagramStart`，
  `:426 if (sentThisFrame >= FrameDatagramBudget) { DatagramsDeferredByBudget++; continue; }`，
  否则 `:432` 再 `connection.Update(nowMs)` —— 又一次完整 FlushOutbound。
- `Transport/PMTransport.cs:594-597`：`FlushOutbound` 循环条件是 `sent < _config.MaxDatagramsPerUpdate`，
  即**单次调用最多 32 个数据报**（`Transport/PMTransportTypes.cs:85 MaxDatagramsPerUpdate = 32`）。
- 结论：单次 `Bridge.Update` 的数据报上界 = 「帧首 s 个（0≤s≤32）」+「帧尾 min(32, 余量) 个」，
  当 s ≤ 31 时帧尾**不再受限**，故上界 = 31 + 32 = **63**。
- 触发条件很普通：帧首只要还剩 1 个以上未发完的数据报（例如上一帧被顺延、或丢包后的可靠重传），
  帧尾又有本帧新产出（生命周期/复制更新/复制 ACK，或业务在入站处理里发的 RPC），就会 > 32。
  例：帧首发 10 + 本帧新产出 32 = 42 个数据报。

**为何门禁漏检**：`Program.cs:1207-1222` 只测了一个「积压饱和」场景 ——
直接把一条 70002 字节消息塞给传输层（约 61 个分片，帧首必然发满 32，于是帧尾必被跳过），
帧尾**没有本帧新产出**（该场景没有对象、没有 RPC），所以只能观测到 32。
断言 `Check(maxPerFrame <= DefaultFrameDatagramBudget + 1)` 的上界 33 来自报告 §7.2 的
「闸门允许 33 的上界」这一**错误模型**：真正的模型是「帧首 ≤32，帧尾可再加 ≤32」。
即漏检不是容差问题，而是**场景形状缺一种组合：帧首有余量 + 帧尾有新产出**。

**最小修复方向**：把预算做成「共用余量」而不是「各次独立」。M03 目前没有 flush 预算入口，
所以要么（a）在 M03 上开 `MaxDatagramsThisFlush`（或 drain-only/flush-only 拆分），
帧尾传 `FrameDatagramBudget − sentThisFrame`；要么（b）在 M03 不变的前提下把
`FrameDatagramBudget` 默认值降到 16，用「2×16−1=31」守住「每帧 ≤32」这一可验收上界；
要么（c）承认并写清真实上界（63），把门禁断言改成 `≤ budget + MaxDatagramsPerUpdate`。
无论选哪条，**报告与契约里的数字必须同步**，不能保留「帧内总发送量仍受预算约束」这句不成立的结论。

### 问题 3【必须修复·可靠性】生命周期批次超限/发送失败 ⇒ 整批永久丢失（无重试）

**路径/行号**

- `Session/PMNetSessionBridge.cs:455-486 FlushLifecycle`：
  `:462 byte[] batch = _world.BuildLifecycleBatch(connection)`（**此调用已把待发表清空**）；
  `:468 PMApplicationEnvelope.Wrap(...)`；`:470-477` `envelope == null` ⇒ `LifecycleOversize++` + Warn + `return false`；
  `:478-485` `SendLifecycleEnvelope` 返回 false ⇒ `LifecycleSendFailed++` + `return false`。
  **两条失败路径都没有把记录放回待发表，也没有断连。**
- `World/PMNetWorld.cs:534 BuildLifecycleBatch` → `:599 state.Pending.Clear();`（在调用方知道发送结果之前）。
- 调用点不止帧内一次：`Session/PMNetSessionBridge.cs:571`（`SendRpc` 对每个目标连接先 `FlushLifecycle`）
  与 `:380`（②阶段）都会走这条路径。
- 后果链：对端拿不到 Create ⇒ 之后所有复制 Update 在客户端被 `DroppedUnknownObject` 丢掉；
  同一连接上的后续 RPC 指向未知对象被 `UnknownTarget` 拒。**没有自愈路径**（NetId 不复用、
  待发表已清空、无重发源）。

**阈值与可达性**：`PMApplicationEnvelope.MaxMessageBytes = 64 KiB`（`PMTransportConnection.cs:54`）
是分界；Create 记录含初值，单条约 40–100 字节 ⇒ 约 **700–1600 个对象在同一帧内创建**即越界。
R3-B 接真实战斗世界（一次性注册大量场景物/召唤物）时属于现实规模，不是理论极值。

**为何门禁漏检**：门禁里**没有任何**构造大生命周期批次的用例（`grep LifecycleOversize` 在
`Program.cs` 无命中，也没有对 `LifecycleSendFailed` 的断言）；`_r3a_session_report.md` §3/§5
只声明「超限被前置闸门拒绝（不静默截断）」，而那句是针对 **RPC** 路径（`RpcOversize`，
确实有断言、且确实不交给传输层）。生命周期那一路被默认为同一结论，实际行为不同：
RPC 超限是「本次调用失败、调用方可感知」，生命周期超限是「**已从待发队列摘除的可靠数据被丢弃**」。

**最小修复方向**：`BuildLifecycleBatch` 增加「按字节预算分批」的出口（保留剩余事件在 `Pending`），
或让 `FlushLifecycle` 在 Wrap/发送失败时把批次事件重新排到 `Pending` 队首 + 按契约「可靠消息发不出去
就断连」处理。两者都要补门禁断言：批次超过上限时，要么下次帧继续发（分批），要么连接被显式断开，
**不得**留下「世界有对象、对端永远没有」的状态。

### 问题 4【必须修复·生命周期】`TryActivate` 不校验连通性 ⇒ 登记出无法自动摘除的僵尸连接

**路径/行号**

- `Session/PMTransportConnection.cs:475 TryActivate`：`_disposed` 检查 → `_activated` 短路 →
  身份字段（`:483`）→ 世界会话（`:490`）→ 世代（`:497`）→ 双方摘要（`:504`）→ 注册表摘要（`:512`）→
  `:527 _activated = true;` → `:528 _bridge.RegisterConnection(...)` → `:536 return true;`。
  **全程没有 `Transport.IsConnected` 判断。**
- `Session/PMNetSessionBridge.cs:227-235`：登记成功即 `_connections.Add` + `_world.AddConnection` +
  `_replication.AddConnection`。
- `World/PMNetWorld.cs:278-317 AddConnection`：服务端侧为「当前所有存活对象」补 Create 待发事件
  （`:311 state.Pending.Add(evt)`），而这些事件的唯一排水口是 `FlushLifecycle`，其前置是
  `connection.IsReady`（`PMNetSessionBridge.cs:457`）= `_activated && Transport.IsConnected` ⇒ **永远为 false**。
- `Session/PMTransportConnection.cs:968 HandleDisconnected`：`_disconnectHandled` 置位后
  **只有 `wasActivated` 为真**才调 `_bridge.OnConnectionClosed`。断开发生在激活之前时该分支不执行，
  且传输层不会再次回调（`PMTransport.cs:742 Disconnect` 幂等）⇒ 事后补的登记**没有对手能摘除它**。
- 两个可观测后果：
  - 客户端：`_serverConnection` 被这条死连接占住，之后真正的服务器连接在
    `PMNetSessionBridge.cs:221`「客户端已绑定受信服务器连接 … 拒绝第二条」被拒 ⇒ 该客户端此后再无复制；
  - 服务端：`PMNetSessionBridge.cs:204`「Uid … 已有活跃连接」被死连接占住 ⇒ 同一玩家重连被拒（本意是防顶号）。
  另外 `FindConnectionByUid`/`ConnectionCount`/世界 `ConnectionCount` 都会被这条僵尸污染。

**复现（最小）**

1. 建一条连接（未激活），先 `conn.Transport.Disconnect(PMDisconnectReason.PeerClosed)`
   （或让空闲超时先触发；等价于「对端在宿主完成票据验证前就已经不在了」）。
2. 再 `conn.TryActivate(out error)` ⇒ 返回 **true**（身份/世代/摘要都合法）。
3. 断言观察：`conn.IsActivated == true`、`conn.IsReady == false`、`World.ConnectionCount == 1`、
   `Replication.ConnectionCount == 1`，且服务端该 uid 再无法被第二条连接使用；
   世界侧 `PendingEventCount(conn) > 0` 且帧驱后仍然不降。
   只有显式 `conn.Dispose()`（`:991`，靠 `if (_activated)` 兜底）才能清掉。

**为何门禁漏检**：B 段（`:721-833`）只测「未激活不派发」与各类**拒绝**路径；I 段（`:1389-1455`）
只在 `Disconnect`/`Dispose` **之后**断言三处计数归零。**没有**用例在「传输已断开」之后调用
`TryActivate`，也没有断言「激活成功后 `IsReady` 必须为真」这一不变量（若断言了，本问题会当场暴露）。

**最小修复方向**：`TryActivate` 在登记前要求 `Transport.IsConnected`（不满足即返回 false + 断连原因），
或在 `RegisterConnection` 里加「连接必须 IsReady」的硬前置；并补门禁断言
「传输断开后 TryActivate ⇒ false，且 world/replication 计数不变」。

### 已确认无问题（同轮对照，供主 Agent 排除）

| 关注点 | 证据 |
|---|---|
| 未激活不派发 | `PMTransportConnection.cs:720` 首判 `IsReady`，否则 `InboundDroppedNotActivated++`（门禁 B① 有正向计数断言） |
| 未激活不发送 | `:589 SendEnvelope` 首判 `IsReady`（`:596`）⇒ `SendRejectedNotReady++`；B① 断言 `Hub.Enqueued == 0` |
| 信封严格性 | 版本≠1 / Kind∉[1,4] / 窗口越界 / >64KiB 均显式失败（A 段断言） |
| 生命周期方向 | `:788/:793` 服务端收到生命周期 ⇒ `ProtocolError` + 断连（**这一条是对的，复制域缺的正是同一道闸门**） |
| RPC 方向/归属口径 | `PMRpc.cs:785 receiverIsServer = world.IsServer`；服务端只收 `Server` 方向、客户端拒 `Server` 方向；归属用 `IsOwnerConnection(target, source)`（786-806）——**未误用 `IsServerSide`** |
| RPC ClassId/Layout | `PMTransportConnection.cs:859-879`（ClassId 必须等于当前目标；Layout 必须等于描述符，否则 `ProtocolError`）；解码一律 `ReadUInt64` + 范围检查，避免 varint 截断成另一个合法 ID |
| 激活校验顺序 | 身份字段 → 世界会话存在 → `Epoch` → 双方摘要 → 注册表摘要（`IsSealed` 时）→ 接受规则 |
| 断开成对摘除 | `:968 HandleDisconnected` → `Bridge.OnConnectionClosed`（`:265`）→ `UnregisterConnection`（`:240`）→ 世界 + 复制成对移除；`Disconnect`/`Dispose`/桥 `Dispose` 幂等（I 段断言） |
| 帧内迭代安全 | `SnapshotConnections()` + 每阶段重查 `IsOpenForPump`/`IsReady`，不会「迭代中改集合」崩 |
| 可靠性拒绝可观测 | 可靠缓冲溢出 ⇒ `PMTransport.cs:192` `Disconnect(ReliableBufferOverflow)` + `SendEnvelope`（`:633`）`SendFailed++` + Warn；入站队列上限 ⇒ `DroppedInbound`（G③ 断言） |

---

## 二、高概率推断（依据与置信度）

1. **客户端可伪造 `ReplicationAck` 推前服务端该连接的已确认版本**（`PMReplicationChannel.cs:779 OnAck`
   无方向判断，`RemoveConnection` 也不清 `_ackQueues`）。当前只影响伪造者自己那条连接的基线
   （让自己少收更新），单独危害有限；但它是问题 1 的同源缺口，修方向闸门时应一并覆盖。**置信度：高**（纯代码可读证）。
2. **`MaxFragmentsPerMessage=64` 在当前信封闸门下不可达**：`64 × MaxPayloadPerMessage(≈1166) ≈ 74624 > 65535`，
   而三条出站路径都先过 `EnforceableMaxBytes=65535` ⇒ `PMTransport.Send` 的 `fragCount > 64` 分支
   （`PMTransport.cs:154`）实际打不到；一旦打到，它在 `PMTransportConnection.cs:632-641` 会被统一标注为
   `ReliableBufferOverflow`（**原因误标**：真实原因是分片数上限，与可靠窗口无关）。**置信度：高**。
3. **`Wrap` 的上限（65536）与 `SendEnvelope` 的上限（65535）存在 1 字节夹缝**（`PMTransportConnection.cs:603-620`）：
   恰好 65536 字节的复制消息会走 `SendFailed++` 分支；因为复制域 `reliable=false`，只 Warn 丢弃、不断连。
   这是 M03 `ushort` 总长上限的衍生，与前一轮登记的 M03 条目同源，不是新缺陷。**置信度：高**。
4. **未激活期间被丢弃的可靠消息仍被传输层 ACK，对端会退休它**：`PMTransport.cs:343/491/499`
   对任何通过 epoch 校验的数据报都登记收包并置 `_ackDirty`，与适配器的 `IsReady` 无关。
   因此在「对端先发、本端后激活」的窗口里，丢的是**可靠消息**且对端无感（本端只有
   `InboundDroppedNotActivated` 计数）。契约只要求「不派发」，但 §4 同时要求「不静默丢可靠消息」，
   两者在实现上并存为矛盾。**置信度：中** —— 取决于 R3-B 宿主是否保证「先
   `TryActivate` 再放行 socket 数据」。**这是「激活失败首包」关注点的实际风险面**：
   失败/未激活的**首包不会污染状态**（已确认），但它的可靠语义会被静默吞掉。
5. **`Multicast` 的「相关性」实际是空转**：`PMNetSessionBridge.cs:663` 传 `viewer = null`，
   而 `PMNetObject.cs:205-214` 在 `viewer == null` 时**直接 `return true`** ⇒ 只要连接非 null 就恒相关，
   等价于广播；契约 §4 的「已拥有该副本且相关」在桥层无从校验（复制层 `ConnectionBucket.Objects`
   没有对外查询接口，桥也没有「该连接是否已知该 NetId」的 API）。**置信度：高**（代码直读），
   当前无距离裁剪配置 ⇒ 危害表现为「相关筛选未落实」，而非错误裁剪。
6. **`_ackQueues` 在 `RemoveConnection`（`PMReplicationChannel.cs:188-202`）中未清理**
   （`:69` 声明，`:1353/1374/1393` 使用 ⇒ 无 `Remove`）⇒ 每连接生命周期泄漏一个 `List<PMRepAck>`；
   同一连接对象被重新登记时会复活旧 ACK 队列。属 M06 侧，影响小。**置信度：高**。
7. **两次 `Transport.Update` 也把单帧入站解析量翻倍**：`PMTransport.cs:285` 单次 `DrainInbound`
   预算 `MaxDatagramsPerUpdate*4 = 128`，一帧两次 ⇒ 单帧最多解析 256 个数据报
   （入站队列本身仍有界 4096）。上一轮只登记了出站预算，未登记入站这一项。**置信度：高**，影响小。
8. **生命周期相关性判定同样以 `viewer=null` 调用**（`PMNetWorld.cs:560`，`FlushLifecycle` 不传 viewer）
   ⇒ 距离裁剪对生命周期不生效；与第 5 条同源，属「有意保守」，但应在报告里写清而不是称「按相关性裁剪」。**置信度：高**。

---

## 三、无法确定（缺少证据）

1. **真实 UDP / Unity 宿主 / 真实 Lobby-DS / 两客户端 T42**：与本轮门禁一致（同进程可控链路），未验证。
2. **R3-B 的宿主调用次序**：是否严格保证「验证票据 → 绑端点 → `TryActivate` → 才放行数据报」。
   这决定高概率第 4 条是否可实际触发（窗口是否存在）。本轮无法从 A2 代码判定。
3. **问题 3 的真实触发概率**：需要真实战斗世界里「同一帧创建对象数 × 单条 Create 字节数」的统计；
   我能证明的是阈值（约 700–1600 个对象/帧）与失败后果，不能证明线上必然发生。
4. **问题 2 在真实负载下的发生频率**：需要真实帧内发送量分布（复制更新 + 回放 + 可靠重传）。
   代码上界 63 是确定的，能否常态 >32 未实测。
5. **在真实生成物（`PMNetGeneratedRegistry` + 生成描述符）下问题 1 的利用难度**：
   我按「双方 ProtocolHash 必须一致、描述符同源」推断攻击者可自行构造合法值字节，
   但未跑生成产物验证（生成链属 R2/R3-B 范围）。
6. **`PMTransport` 的 `_immediateOutbox` 无上限**（`PMTransport.cs:73`）在极端发送失败时的增长曲线；
   属 M03 既有设计，本轮未越界评估。

---

## 四、已检查范围

**项目文档（按委派要求顺序）**：仓库根 `AGENTS.md`（三入口路由）；
`Client/Assets/AGENTS.md`（进程形态判定、新 PMNet 链与旧 `MainPack` 链边界、协议来源）；
`Server/AGENTS.md`（服务端权威边界，确认本轮不涉 Server 代码）；
`Docs/plans/net-r3-control-contract.md`（**全文 45 行**，§1-§5）；`net-architecture-migration.md`
（R3A1-R3A5 表 `:1979-1983` + 卷首路由）；`Docs/plans/_r3a_session_report.md`（**全文 226 行**）；
`net-r0-contract.md`（`grep` 命中：D-R0-14/30/32/43 与「方向非法 = 确定行为」条目）；
`_r2_fix_replication.md`（`grep` §7「客户端上行为 R3 之后才有真实宿主」）。

**被审代码（全文）**：`Session/PMTransportConnection.cs`（1045 行）、`Session/PMNetSessionBridge.cs`（824 行）。

**直接引用（只读必要函数/字段，未扩散）**：
`Transport/PMTransport.cs`（`Send`/`EnqueueOne`/`Update`/`DrainInbound`/`ProcessDatagram`/`DeliverMessage`/
`TrackIncomingPacketId`/`ProcessIncomingAcks`/`FlushOutbound`/`BuildDatagram`/`Disconnect`）；
`Transport/PMTransportTypes.cs`（`PMStream`/`PMDisconnectReason`/`PMTransportConfig`/`PMTransportStats`/`IPMTransportSink`）；
`Replication/PMReplicationChannel.cs`（`OnMessage`/`OnAck`/`AddConnection`/`RemoveConnection`/`RegisterObject`/
`UnregisterObject`/`ResolveViewRole`/`ResolveEntry`/`TryApplyRecord`/`DispatchOnRep`/`QueueAck`/`BuildAckMessage`）；
`Replication/PMReplicationTypes.cs`（`PMRepProtocol.Magic=0xA7`/`Version=1`/`PMRepMessageKind`）；
`Replication/PMReplicationWriter.cs`（公开签名）、`Replication/PMRepConnectionState.cs`（字段）；
`World/PMNetWorld.cs`（`AddConnection`/`RemoveConnection`/`BuildLifecycleBatch`/`OnLifecycleMessage`/`ApplyCreate` 头部）；
`World/PMNetOwnerChain.cs`（`GetNetConnection` 链语义）；`PMNetObject.cs`（`IsNetRelevantFor`/`OwnerConnection`）；
`PMNetConnection.cs`（`IsServerSide`/`IsReady`/`Send`）；`PMRpc.cs`（`Deliver` 737-846、`PMRpcDispatch` 判定 486-505）。

**门禁**：`Tools/PMNetSessionTest/Program.cs`（1455 行；读取节选：1-120 节列表、250-330 描述符与 Actor、
400-660 场景/Hub/`Frame`/`MakeActivatedPair`/构造辅助、658-833 A/B 段、907-1013 D 段、1075-1130 E 段、
1129-1180 F 段、1182-1295 G 段、1295-1389 H 段、1389-1455 I 段）。

**明确未读（不在边界内）**：`Control/*`（A1 与本轮零依赖）、`Generated/*`、Generator、`PMDsHost`、
旧客户端 `MainPack` 链路、`Server/`（C# 服务端）、Unity 侧宿主。

**方法**：`ffgrep`/`sed` 定位 + 全文阅读；无编译、无运行（explore 只读边界）。

---

## 五、建议下一步（最小补充查询 / 运行时验证）

1. **先修问题 1 再加断言**（安全优先）：适配器补消息级方向闸门；门禁新增
   「客户端用 `0xA7/1/1` + 描述符一致记录发 Update ⇒ 服务端拒绝 + 计数增长 + 活对象属性未被改写 + 不断连（或按契约断连，需明确）」。
   同时把 `Program.cs:1082` 那条用例改成**合法正文**（现用 `{1,1,1,1}` 只测到魔数分支，
   断言名与路径错位，必须修正文案或载荷）。
2. **问题 2 收口三选一**（见上「最小修复方向」），并同步 `_r3a_session_report.md` §7.2、
   迁移计划 R3A4 行与门禁数字；门禁断言改为实测上界（若 M03 不改则应为 `budget + MaxDatagramsPerUpdate`），
   并补「帧首有余量 + 帧尾有新产出」这一缺失场景。
3. **问题 3**：`BuildLifecycleBatch` 支持按字节预算分批（保留剩余事件），或失败即断连；
   门禁新增「单帧生成 >64KiB 生命周期批次」用例，断言要么分批送达、要么显式断连，
   **不得**出现「待发表已清空但对端永远没有对象」。
4. **问题 4**：`TryActivate`/`RegisterConnection` 前置 `Transport.IsConnected`；
   门禁新增「传输断开后 TryActivate ⇒ false 且 world/replication 计数不变」和一条不变量断言
   「`TryActivate` 成功 ⇒ `IsReady` 为真」。
5. **补强（低风险、可同批做）**：`ReplicationAck` 方向断言；`Multicast` 给「非拥有者/不相关连接」
   的负例（需桥暴露「该连接是否已知该 NetId」查询）；登记 `_ackQueues` 的断开清理与
   「分片上限误标为 `ReliableBufferOverflow`」的口径。
6. **R3-B 集成契约**：在控制契约或 `Docs/ForClient.md` 写明宿主次序「`TryActivate` 必须在放行
   该链路数据报之前完成」，或让适配器在未激活时**拒收数据报入队**（而不是收了再丢），
   以消掉高概率第 4 条的可靠消息静默丢失窗口。
