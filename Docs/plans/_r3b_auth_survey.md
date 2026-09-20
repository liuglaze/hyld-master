# R3-B 安全与 socket 接入调研（只读，未实施）

范围：`Client/Assets/Scripts/PMNet/{Control,Session,Transport,World,Replication}`（只读）、`Tools/PMNetSessionTest`（只读）、`Server/DS/*`（只读）。
必读已读：根 `AGENTS.md` → `Client/Assets/AGENTS.md` → `Server/AGENTS.md` → `net-architecture-migration.md` 末 R3 → `net-r3-control-contract.md` 全文 → `_r3a_session_report.md` 全文。
结论口径：本文只给「最小冻结字段与 API 提案」，不设计第二套加密协议，不改现有传输头。

## 1. 用现有 verifier 结果建端点账本：键只能取“票据全量哈希”，不能取 Fingerprint

事实（证据）：
- `PMDsTicketVerification`（`Control/PMDsTicket.cs:141-183`）字段只有 `Verdict/Identity/MatchId/DsId/Epoch/ProtocolHash/IssuedAt/ExpiresAt`。**没有 Nonce、没有票据哈希**。
- codec 解析时把 16 字节 Nonce 直接跳过（`pos += PMDsTicketWire.NonceBytes;`，同文件 Verify 内），`PMDsTicket.Nonce` 只在 **Issuer 路径**（`FromParsed`）可见，verifier 路径拿不到。
- `PMDsCrypto.Fingerprint`（`Control/PMDsControlProtocol.cs:1706`）= SHA-256 前 **4 字节**（32 位）。R3-A 审查已因「32 位结果摘要碰撞」判其只能做诊断 → **不得**作为账本键。
- `PMDsCrypto.Sha256` / `HmacSha256` / `FixedTimeEquals` 均为 public（同文件 1615/1651/1677），所以消费侧可自行对票据字节做全量哈希，**无需改 A1**。
- `Server/DS/PMDsCoordinator.ResolveTicket`（`Server/DS/PMDsCoordinator.cs:1767`）自己就写明：同一张票据被校验两次都通过，**它不是防重放**。

最小冻结提案（零线格式改动）：
```
键 = SHA-256(整张票据字节，含 MAC) = TicketDigest(32B)   // 由消费侧算，不依赖 A1
备选（需 A1 加字段，非线格式）：PMDsTicketVerification 增 Nonce(16B) + TicketDigest(32B)
```
- 为什么不用 Nonce-only：Nonce 目前不可达；且 Nonce 相同但内容不同（同 nonce 不同 uid）在键语义上还是应分开，全量哈希天然区分。
- 为什么不改 A1：`PMDsTicketVerification` 是 struct，加字段源兼容；但账本自算哈希可让 R3-B 与 A1 完全解耦，A1 保持冻结。

### 1.1 账本（DS 侧，每 MatchId/Epoch 一个）
```
public enum PMDsLedgerReject : byte { None, Expired, EndpointTaken, DigestRebound, UidBusy, Capacity }
public struct PMSessionBinding { public int ConnectionId; public int Uid, PlayerId, TeamId, HeroId;
                                 public uint Epoch; public string MatchId, DsId, EndpointKey; }
public sealed class PMDsEndpointLedger {
  public const int MaxBindings = 16;              // 名册≤6 + 重连余量
  public const int MaxUnverifiedEndpoints = 64;   // 验证失败端点短期黑名单上限（有界）
  public PMDsLedgerResult TryConsume(byte[] ticketBytes, int off, int count,
      in PMDsTicketVerification ticket, string endpointKey, long nowUnixSeconds,
      out PMSessionBinding binding, out PMDsLedgerReject reject);
  public void ReleaseEndpoint(string endpointKey);  // 断开/心跳超时
  public void ReleaseSession();                     // 终态整体释放
  public PMDsEndpointLedgerStats Stats { get; }     // 非空可达计数（门禁要求）
}
```
消费顺序（**先验证、再查账本、再动任何状态**）：`verify(MAC+字段+名册) → digest → ledger.TryConsume`。
规则：① digest 已绑同一 endpoint ⇒ 幂等，**返回同一 ConnectionId/绑定的同一 session，不再分配**；② digest 已绑别的 endpoint ⇒ `DigestRebound` 拒绝、不回载荷；③ endpoint 已绑别的 digest ⇒ `EndpointTaken`；④ 新 digest 但 Uid 已被别的 digest 占用 ⇒ `UidBusy`（**早于**桥的 `RegisterConnection` uid 查重，避免白烧一次激活）；⑤ `now >= ExpiresAt + grace` ⇒ `Expired` 并释放绑定；⑥ 超 `MaxBindings`/`MaxUnverifiedEndpoints` ⇒ `Capacity`，失败端点计数递增，超阈进短期黑名单（TTL 与容量都有界）。
终态（`HandleBattleEnd` 等）调 `ReleaseSession()`；Lobby 侧另有全局 uid 占用表与端口池（R3-B 前置，不属本账本）。

### 1.2 “不把 MAC 验证当防重放完成”的落地判据
账本必须满足三条可测断言：同一票据来自**第二个端点**被拒且**不返回任何业务字节**；同一票据同端点重发 ⇒ 无新 ConnectionId、无新 session；票据过期后绑定被回收且重放被拒。

## 2. UDP 握手：服务器身份确认、重试不新分配、无反射放大

事实：
- 每局控制密钥只在 Lobby 与 DS 之间（`PMDsMatchKey` 类注释与 `PMDsBootstrapDocument`：DS 读同机引导文件，**没有任何入口接受来自对端的密钥**）。
- 客户端只持票据（bearer），**没有密钥**（`PMDsTicket` 只暴露 Nonce/Fingerprint/字节）。
- 于是：**客户端无法验证 DS 的 HMAC**。任何“DS 回一个 MAC 让客户端验”的方案都需要给客户端分发验证密钥 —— 这是新开一条密钥分发路径，本轮无依据，**不做**。

因此首版“服务器身份确认”定为**关联性证明**（不是密码学证明）：
1. ClientHello 带 **16 字节客户端新鲜 nonce**；
2. ServerHello 必须**回带该 nonce**、回带 **TicketDigest**、并声明 `MatchId/DsId/Epoch/ProtocolHash`；
3. 客户端拿三处交叉比对：与自己发出的 nonce、与自己所持票据解析出的字段、与 Lobby 经**已认证 TCP** 下发的 Ready（端口/DsId/摘要）一致；
4. 任一处不符 ⇒ 丢弃并重新握手（不采信该端点为 DS）。
效果：挡住盲打/伪造地址、挡住旧响应重放（nonce 新鲜）、挡住错局/错代次；**挡不住**能读到票据的本机攻击者（见 §7）。

“重试不新分配 session”：
- 握手状态机以 **TicketDigest** 为键，不由“收到一个包”驱动分配；`ConnectionId` 只在首次成功 `TryConsume` 时单调分配一次；
- `PMSession`/`PMNetWorld` 是**每局一个**（Epoch 来自引导文件），绝不在握手路径里 new；
- 幂等重试返回同一 ServerHello 内容（nonce 回带每次都新），客户端可无脑重发；
- 同一 Uid 的第二张票据（新 Nonce）⇒ `UidBusy`，必须由 Lobby 显式重发/撤销后替换（契约 §2：重连票据由 Lobby 显式重发）。

“无 UDP 反射/放大”（硬规则）：
- **不验证不回包**：MAC 无效 / 未绑端点 / 账本拒绝 ⇒ 一律不回（或仅回 ≤ 请求长度的 ServerReject，二者选一，见 §4）；
- `len(ServerHello) <= len(ClientHello)`（见 §4 尺寸预算，比例 <1）；
- 未验证端点不获得任何生命周期/复制/RPC 载荷（由 §3 的“进 Transport 前拒绝”保证）；
- 有界：握手前不按未认证端点分配内存（待验证端点表 ≤64，TTL 秒级）；每端点/IP 限速 + 失败计数；
- 清量：握手帧上限固定 512B，超限当场丢，不解析。

## 3. `PMTransportConnection.OnDatagram` 未激活先 ACK 缺口与最小修法

缺口（精确）：`PMTransportConnection.OnDatagram` 无条件转发 `Transport.OnDatagram`（`Session/PMTransportConnection.cs`），Transport 侧 `ProcessDatagram` 先 `ProcessIncomingAcks`、再 `TrackIncomingPacketId`（置 `_ackDirty`/`_recvWindowMax`），**之后**才 `DeliverMessage → OnTransportMessage`；而适配器在 `HandleInboundMessage` 开头 `if (!IsReady) { InboundDroppedNotActivated++; return; }` 丢弃。于是：**应用层丢了、传输层却已经把我们“收到”的 ack 回给对端**，对端据此退休可靠消息（生命周期/RPC/可靠复制），既不会 NAK 也不会重传 ⇒ 静默的可靠性破洞。

最小修法（推荐 (a)，与 R3-A 交接记录一致：“未激活数据报必须在进入 Transport 前拒绝”）：
- **(a) 宿主次序（零核心改动，冻结决策）**：握手走**独立于 Transport 的原始数据报路径**；只有 `TryActivate` 成功后才把该链路的数据报交给 `OnDatagram`。未激活期间的包根本不进 Transport ⇒ 无跟踪、无 ack、无 `_lastRecvMs` 更新。语义正确性：对端可靠消息保持未确认 ⇒ 下轮重传，正确体现“不静默丢可靠消息”（窗口期靠对端可靠窗上限兜底，故激活必须快）。
- **(b) 纵深防御（可选，M03 加一个钩子）**：`PMTransport` 增
  `public Func<byte[], int, int, bool> InboundAdmission;   // null = 全部放行（T38 语义不变）`
  在 `OnDatagram` 入队口判定；返回 false ⇒ 丢弃且**不计** `DatagramsReceived`/`_lastRecvMs`/不跟踪、不 ack（新增 `DroppedNotAdmitted` 计数）。适配器在未激活期把它设为恒 false。
- **不推荐**：改成“先交付、后 ack”（需要把 ack 延迟一帧、并让 sink 回报接受结果）—— 改动面大、动到 ack 语义与交付顺序，不是最小修法。

配套：`TryActivate` 之前的握手必须**不能**依赖 Transport（Transport 在构造时 `IsConnected == true`，只是没被 Disconnect；`IsReady` 才是真判据）。

## 4. 最小冻结握手 codec 与 API（供并行实现）

线格式（**不进现有传输头**：首字节 `0x50` ≠ Transport `ProtocolVersion = 1`，宿主按首字节分流，Transport 永远只见到 version 1 的数据报）：
```
0  magic  4B  'P','M','H','S'
4  version 1B = 1
5  kind    1B  ClientHello=1 ServerHello=2 ServerReject=3
body       PMNetReader/PMNetWriter（varint，与 PMApplicationEnvelope 同源，不引入第三套编码）
           严格恰好消费、拒绝尾部；整帧 <= 512B，超限当场拒绝
```
字段（全部显式、无 varint 歧义处一律用定长 raw 写二进制）：
- `ClientHello`：`ticketLen(1..352)` + `ticket` + `clientNonce(16B raw)` + `clientProtocolHash(uint)` + `clientEpoch(uint)`。
  `clientProtocolHash/clientEpoch` 与票据内字段重复是**故意的**：让 DS 在花钱验 MAC 前就能廉价拒掉摘要/代次不符的包。
- `ServerHello`：`matchId(≤128 utf8)` + `dsId(≤128 utf8)` + `epoch(uint)` + `protocolHash(uint)` + `ticketDigest(32B raw)` + `clientNonceEcho(16B raw)` + `uid/playerId/teamId/heroId(int)` + `serverNonce(16B raw, 预留)`。
- `ServerReject`：`verdict(byte = PMDsTicketVerdict)` + `stage(byte: 0=帧 1=验证 2=账本 3=名册)` + `retryAfterMs(uint varint, 0=别重试)`；长度 ≤ 请求。
尺寸预算：ClientHello ≤ 6+2+352+16+5+5 = **386B**；ServerHello ≤ 6+129+129+5+5+32+16+20+16 = **358B** ⇒ 放大系数 <1。

API（冻结签名；实现可分两路并行，互不阻塞）：
```
// 共享（Client/Assets/Scripts/PMNet/Session/，C#7.3 + netstandard2.0，零 Unity 依赖）
public enum PMHandshakeKind : byte { ClientHello = 1, ServerHello = 2, ServerReject = 3 }
public struct PMHandshakeClientHello { public byte[] Ticket; public int TicketOffset, TicketCount;
                                       public byte[] ClientNonce; public uint ProtocolHash, Epoch; }
public struct PMHandshakeServerHello { public string MatchId, DsId; public uint Epoch, ProtocolHash;
                                       public byte[] TicketDigest, ClientNonce, ServerNonce;
                                       public int Uid, PlayerId, TeamId, HeroId; }
public static class PMHandshakeCodec {
  public const int FrameVersion = 1;  public const int MaxFrameBytes = 512;
  public static bool IsHandshakeDatagram(byte[] d, int off, int count, out PMHandshakeKind kind);
  public static void WriteClientHello(PMNetWriter w, byte[] ticket, int tOff, int tCount,
                                      byte[] clientNonce16, uint protocolHash, uint epoch);
  public static bool TryReadClientHello(byte[] d,int off,int count, out PMHandshakeClientHello h, out string error);
  public static void WriteServerHello(PMNetWriter w, in PMHandshakeServerHello h);
  public static bool TryReadServerHello(byte[] d,int off,int count, out PMHandshakeServerHello h, out string error);
  public static void WriteServerReject(PMNetWriter w, PMDsTicketVerdict v, byte stage, uint retryAfterMs);
  public static bool TryReadServerReject(byte[] d,int off,int count, out PMDsTicketVerdict v, out byte stage, out string error);
}
// 客户端状态机（只回答“发什么/收到什么算通过”，不碰 socket、不碰 Unity）
public sealed class PMHandshakeClient {
  public PMHandshakeClient(byte[] ticketBytes, string expectedMatchId, string expectedDsId,
                           uint expectedEpoch, uint expectedProtocolHash, Func<long> nowUnixSeconds);
  public byte[] BuildClientHello();                                    // 每次新 nonce；限速重试由调用方
  public PMHandshakeOutcome Handle(byte[] d,int off,int count);        // Accepted / Rejected(stage) / Ignored
  public PMSessionBinding Binding { get; }                             // 成功后供构造 PMSessionIdentity
}
// DS 侧入口（socket 由宿主给，本类只做“帧→判定→响应字节”）
public sealed class PMHandshakeServer {
  public PMHandshakeServer(PMDsTicketExpectation expectation, Func<byte[],int,int,long,PMDsTicketVerification> verify,
                           PMDsEndpointLedger ledger, Func<long> nowUnixSeconds);
  public bool TryHandle(byte[] d,int off,int count, string endpointKey,
                        out byte[] response, out PMSessionBinding binding, out PMHandshakeOutcome outcome);
}
```
接线顺序（冻结）：`IsHandshakeDatagram` → `PMHandshakeServer.TryHandle` → 成功才 `new PMSessionIdentity(binding…)` → `new PMTransportConnection(identity, world, bridge, link)` → `TryActivate` → **此后**才允许 `OnDatagram`（§3-a）。

## 5. 桥/registry 静态接口与“对象上线前需要哪些注册”

事实：
- `PMNetSessionBridge(world, replication=null, replicationOptions=null)`；生成的静态桩通过 `PMNetGeneratedRegistry.RemoteSender = bridge.SendRpc` 绑定（签名 `Action<PMNetObject, ushort, PMRpcWriter>`，`Session/PMNetSessionBridge.cs:565` 与 `Tools/PMNetE2E/Generated/PMNetGeneratedRegistry.g.cs:74`）。
- `RemoteSender` 是**进程级 static** ⇒ 一个进程只能有一个权威世界（DS 一侧天然满足；测试里两客户端必须分进程）。
- `PMNetRegistry` 无枚举 API：只有 `TryGetClass(classId)` / `TryGetRpc(classId,rpcId)` / `ClassCount` / `Seal`（`Declarations/PMNetDeclarations.cs:604-615`）。`PMNetClassEntry.Factory` 存在（`Func<PMNetObject>`，同文件:395），因此“按 registry 自动 wire 世界工厂”**差一个枚举出口**。
- **世界工厂与 registry 是两套表**：接收侧必须 `world.RegisterClass(classId, factory)`（`World/PMNetWorld.cs:246`），E2E 门禁逐类手写（`Tools/PMNetE2E/Program.cs:414/440/814/896/922/1442/1855`）。
- **OnRep 也是两套**：生成物 `PMNet_OnRepDispatch` 是 `private static`（`Tools/PMNetE2E/Generated/PMNet.PMNetE2E.E2eReplicated.g.cs:160`），只有手写 public 壳才能喂给 `channel.RegisterOnRepDispatcher`（E2E 用 `E2eDispatchOnRep`，`Tools/PMNetE2E/E2eFixtures.cs:317`）。生成 registry 的 `RegisterAll` **不**注册 OnRep。
- 生产工程当前 `Client/Assets/Scripts/PMNet/Generated/` 只有 proto 编解码（`SocketProto.PMNet.g.cs`），**没有** `PMNetGeneratedRegistry.g.cs`，业务侧零 PM 声明类（全项目 grep 无 `PMReplicated`/`PMNetObject` 业务类）。
- `world.ReplicatedPropertyFilter` 由复制层在 `AddConnection`/`RegisterObject` 时自动安装（`Replication/PMReplicationChannel.cs:184/221` + `TryInstallWorldConditionSource`），**宿主不要手写**。

对象上线前的完整注册清单（次序即依赖）：
1. `PMNet.Generated.PMNetGeneratedRegistry.RegisterAll()`（幂等；内部 `PMNetRegistry.RegisterClass` × N 后 `Seal(hash)`）；
2. `world = new PMNetWorld(new PMSession(epoch, isServer))`；
3. `bridge = new PMNetSessionBridge(world)`（复制通道由桥创建并持有一份）；
4. 逐类：`world.RegisterClass(entry.ClassId, entry.Factory)` + `bridge.Replication.RegisterOnRepDispatcher(entry.ClassId, dispatcher)`；
5. `PMNetGeneratedRegistry.RemoteSender = bridge.SendRpc`（**必须在任何生成桩触发之前**）；
6. 连接：ledger 绑定 → `PMSessionIdentity` → `PMTransportConnection` → `TryActivate`；
7. `Spawn` 后：`bridge.RegisterReplicatedObject(obj)`（有复制属性的对象必须做，否则“世界有、复制层不知道”）；
8. 每帧 `bridge.Update(now)` 一次（帧内预算由桥内部两次驱动共享，`FrameDatagramBudget = 32`）。
缺失项（需主 Agent 决策，本轮不改）：`PMNetRegistry` 缺枚举/`RegisterInto(world, channel)`；生成器缺 OnRep 注册壳与工厂表导出。生产 registry 不存在 ⇒ 步骤 1/4/5 目前**无生成产物可依赖**。

## 6. 复制 `_ackQueues` / Multicast viewer / 生命周期切批：实际阻塞点

1. **`_ackQueues` 生命周期泄漏**：`PMReplicationChannel.RemoveConnection`（`Replication/PMReplicationChannel.cs:188`）只摘 `_buckets`/`_connectionOrder`，**不摘 `_ackQueues`**（:69，`QueueAck` :1345 懒建）；桥只在 `IsReady` 时发 ack，所以死连接不会误发，但队列与键随“历史连接数”单调增长。最小修法：`RemoveConnection` 内 `_ackQueues.Remove(connection)`（可选在 `UnregisterObject` 一并清理）。属 `Replication/`，不在 R3-A 写边界。
2. **Multicast viewer 相关性**：桥的 `ResolveRpcTargets` 走 `target.IsNetRelevantFor(connection, null)`（`Session/PMNetSessionBridge.cs:739`），而 `PMNetObject.IsNetRelevantFor` 对 `viewer == null` **直接 return true**（`PMNetObject.cs:205-222`）；`PMTransportConnection` 也没重写 `TryGetViewerLocation`（基类恒 false，`PMNetConnection.cs`）。⇒ 今天“相关连接集合”退化成“所有就绪连接”，契约 §4 的“已拥有该副本且相关”两条都没落地。最小修法（两步，可分开做）：(i) 传入真实 viewer（该连接的 Pawn 或权威 viewer 对象）；(ii) 增加“该连接已有该对象的复制基线（Create 已发/已确认）”判据（`PMRepConnectionState` 里有每对象基线状态可复用）。另外复制 Tick 自身**完全不做相关性裁剪**（全 `Replication/` 无 `IsNetRelevantFor` 调用），距离裁剪目前是死代码。
3. **生命周期切批阻塞点**（当前用“显式断连”兜底，`bridge.FlushLifecycle`）：
   - `PMNetWorld.BuildLifecycleBatch`（`World/PMNetWorld.cs:534`）**没有字节预算出口**，且在 return 前就 `state.Pending.Clear()`（只把“不相关”的塞回 `_stillPending`）⇒ 批次是最后机会；
   - `PMLifecycleCodec.Write` 只有“≤4096 条”与“单条初值 ≤64KiB”两个上限，**没有总字节上限**；`PMNetWriter` 容量自增长 ⇒ 批次可超应用消息上限（64KiB / 实际 65535）；
   - 统计口径：`CreatesSent/DestroysSent` 在建批次时就计（:604-607），与实际发送解耦；
   - 可靠域**队头阻塞**：Create/Destroy 与可靠 RPC 同域，大批次会拖住同域 RPC（即便切批成功也只是把延迟摊到多帧）。
   切批的最小接口（需 M04 开门，本轮不改）：`byte[] BuildLifecycleBatch(conn, viewer, int maxBytes, out int recordCount, out bool more)` + `PMLifecycleCodec.Write(w, records, maxBytes, out written)`；桥按返回值循环驱动，不再断连。

## 7. HMAC 票据不加密，首版威胁边界

- 票据 = `HMAC-SHA256` 覆盖 (MatchId/DsId/Epoch/ProtocolHash + Uid/PlayerId/TeamId/HeroId + iat/exp + Nonce)，提供**完整性与真实性**，**不提供机密性**：票据全量（含 MAC、nonce）在 UDP 上明文传输，落到同机文件（引导文件）与内存。它是**bearer 凭据**。
- 由此“端点账本”的真实能力边界：它能挡住跨端点转发/搬用与偶然重放（同一票据、不同来源地址）；**挡不住**能读到票据的本机/同用户攻击者（UDP 源地址在本机可伪造，本机进程也能直接读引导文件或票据内存）。这是 R3-B 有意接受的前提，必须在文档写明，不能宣传成“防重放完成”。
- 首版明确**不覆盖**：客户端↔DS 的 MITM（应用层消息无完整性标签，只有 epoch/版本校验；篡改会被解码器大部分拒掉，但不是密码学保证）；流量分析与元数据泄露；DS 被本机他人抢绑端口的抢占（Windows 上同端口 UDP 绑定冲突会先到先得）；拒绝服务的绝对防护（只做有界队列 + 限速 + 容量上限）。
- 本轮**不引入** TLS/DTLS/自研加密，也不新增第二把密钥：可用的密码学原语只有既有的 `PMDsCrypto`（HMAC-SHA256 / SHA-256 / 常量时间比较 / 加密随机）。若将来要机密性或真正的服务器认证，需**显式决策**新密钥分发路径（例如 Lobby 向客户端下发会话验签密钥，或上 DTLS），不属 R3-B。
- 与“票据不加密”并列的一条纪律：日志与异常里**不得**出现密钥/完整票据（A1 已实现 `ToString` 脱敏），R3-B 新代码同样只允许打 `TicketDigest`/指纹/身份。

## 8. 建议的并行实现切分（无实现，仅边界）
- 组 1：`PMHandshakeCodec` + `PMHandshakeClient`（纯编解码/状态机，零 socket、零 Unity，可独立门禁）。
- 组 2：`PMDsEndpointLedger` + `PMHandshakeServer`（账本语义与拒绝原因可独立门禁：第二端点、幂等重试、过期回收、容量）。
- 组 3：宿主接线（socket 前分流 + 进 Transport 闸门 + registry/OnRep/工厂注册清单）依赖组 1/2 的冻结签名，但不依赖其内部实现；可先写“只读接线草案 + 计数断言”。
- 交主 Agent 决策项：`PMNetRegistry` 枚举/`RegisterInto` 出口、生成器 OnRep 壳、`BuildLifecycleBatch` 字节预算、`_ackQueues.Remove`、Multicast 相关性判据。

## 9. 已检查范围（证据清单）
`Client/Assets/Scripts/PMNet/Control/{PMDsTicket.cs,PMDsControlProtocol.cs}`、`Session/{PMTransportConnection.cs,PMNetSessionBridge.cs}`、`Transport/{PMTransport.cs,PMTransportTypes.cs(引用)}`、`World/{PMNetWorld.cs,PMNetObjectLifecycle.cs}`、`Replication/{PMReplicationChannel.cs(关键段),PMRepConnectionState.cs(引用)}`、`PMNetConnection.cs`、`PMNetObject.cs`、`Declarations/PMNetDeclarations.cs`、`Generated/SocketProto.PMNet.g.cs`、`Client/Assets/Scripts/Server/{Boot/PMDsHost.cs(节选),Net/PMUdpRouter.cs(节选),Net/PMSingleBattleRegistry.cs(存在性)}`、`Server/DS/{PMDsCoordinator.cs(关键段),PMDsProcess.cs(存在性)}`、`Tools/PMNetSessionTest/Program.cs(节选)`、`Tools/PMNetE2E/{Program.cs 关键行,E2eFixtures.cs,Generated/*}`、`Tools/PMTransportTest/Program.cs(存在性)`、`Docs/plans/{net-r3-control-contract.md,_r3a_session_report.md,net-r2-codegen-contract.md,net-architecture-migration.md 末 R3}`。
未读（超出边界或非必需）：`PMTransportInternals.cs`、`PMRpc.cs` 全文、`Replication/PMReplicationTypes|Reader|Writer`、`Server/` 旧战斗链路、`PMDsCoordinator.cs` 全文。

## 10. 无依据 / 无法确定
- 客户端↔DS 的真实网络条件（RTT/MTU/NAT）未实测；握手帧 512B 上限与“ServerHello ≤ ClientHello”是设计约束，未经验证。
- Lobby 侧全局 uid 占用表与端口池的现状未查（不在本次硬边界）。
- `PMTransportConnection` 目前乐观 `IsConnected == true`（未 Disconnect 即视为连通）是否适合作为“链路活性”判据，需要 R3-B 宿主心跳策略配合，未定。
- 生成的 OnRep/工厂壳的具体生成器改动量未评估（生成器不在本次边界）。
