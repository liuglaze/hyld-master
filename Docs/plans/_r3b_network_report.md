# R3-B 网络组实施报告：入局通知 / 握手 / UDP 准入

> 落点：`Client/Assets/Scripts/PMNet/Session/{PMDsEntryOffer,PMHandshake,PMUdpSessionEndpoint,PMTransportConnection}.cs`
> 门禁：`Tools/PMUdpAdmissionTest`（**真实 localhost UDP + 生产票据 / Bridge / Transport**，303 项断言）
> 事实来源：`Docs/plans/net-r3-control-contract.md` §2/§4/§5/**§7.1/§7.2（冻结 API）**、
> `_r3b_auth_survey.md`（只读调研，**不采纳**其 512B 握手帧估算——契约 §7.2 已冻结 1200B）、
> `_r3a_session_report.md` 全文。
> **T42 仍为 PENDING**（真实 Unity DS 进程、权威场景、两客户端闭环属主集成）。

## 摘要

本轮交付「入局通知 codec + UDP 握手 + 端点消费账本 + UDP 宿主」四件套，并把「未认证字节不得先进
Transport（否则先 ACK 后丢）」从**宿主次序**升级为**连接层纵深闸门**。全部为纯共享代码
（`netstandard2.0` + C# 7.3，零 Unity 依赖），由 `Server/` 与各 Tools 工程链接编译。

门禁实测：**`PMUdpAdmissionTest` 303 项 / 0 失败 / exit 0**（先 build 0 警告 0 错误再 run）；
`PMNetLangCheck` 0/0；其余既有门禁逐项 exit 0（§7）。**3 组缺陷注入全部被抓到**（§5）。
有 **3 项旧断言按预期失效**（`PMNetSessionTest`），成因单一、是本次有意行为变更，**不改对方测试**（§6）。

## 1. 交付文件（全部在硬写入边界内）

| 文件 | 编码 | 变更 |
|---|---|---|
| `Session/PMDsEntryOffer.cs` | UTF-8 BOM + CRLF（495 行） | 新增：`PMDsEntryOffer` + `PMDsEntryCodec`（`PMDS1:` + Base64） |
| `Session/PMHandshake.cs` | UTF-8 BOM + CRLF（1986 行） | 新增：`PMHandshakeCodec`/`PMHandshakeClient`/`PMHandshakeServer`/`PMDsEndpointLedger`（写边界只给一个文件名，故四个类型同文件） |
| `Session/PMUdpSessionEndpoint.cs` | UTF-8 BOM + CRLF（1054 行） | 新增：UDP 宿主（非阻塞 socket、主线程 Pump、有界收包） |
| `Session/PMTransportConnection.cs` | UTF-8 BOM + CRLF | 修改：`OnDatagram` 纵深闸门（未就绪直接丢，**不进 Transport、不 ACK**）；`InboundDroppedNotActivated` 文档补充两个丢弃点 |
| `Session/{PMDsEntryOffer,PMHandshake,PMUdpSessionEndpoint}.cs.meta` | 无 BOM + LF，唯一 GUID | 新增（`cf281d5b…` / `026c4618…` / `ee523187…`，已与全仓 meta 去重） |
| `Tools/PMUdpAdmissionTest/{PMUdpAdmissionTest.csproj,Program.cs}` | 新增门禁 | 真实 UDP 接纳测试 |
| `Docs/plans/_r3b_network_report.md` | 本报告 | — |

未触碰：Lobby/DS 宿主、Unity 场景与 `PMR3` 生成物、`Server/` 业务、`Replication/`、`World/`、
`Control/`（A1 冻结）、旧 `MainPack` 链路。**未执行任何 git/svn 写操作，未编译 Unity。**

## 2. 冻结接口（逐条对应契约）

### 2.1 §7.1 入局通知

- `PMDsEntryOffer{MatchId,DsId,Host,Epoch,ProtocolHash,CollisionDigest,Port,Identity,Ticket}` 字段名与契约一致；
  `PMDsEntryCodec.Prefix = "PMDS1:"`、`Encode(offer)`、`TryDecode(text,out offer,out error)` 签名一致。
- 线格式：`PMNetWriter`（varint）顺序布局，首字段为**自描述版本 1**；整条文本 ≤ 4096 字节，标识 ≤ 128 UTF-8 字节，
  票据 1..352（= 既有 codec 上限，不另立一套），端口 1..65535，**严格拒绝尾部**（`IsAtEnd`）。
- Base64 用**规范校验**（字母表内、长度 %4==0、`=` 只在末尾且 ≤2），不依赖 `Convert.FromBase64String` 的宽松行为；
  解码路径**不抛**（对端乱写不得变成自己崩溃），编码路径不合法**显式抛**（调用方是 Lobby，是编程错误）。
- `ToString()` 只给公开字段与票据**长度**：`offer/完整 Str/票据字节一律不进日志`（契约 §2/§7.1）。

### 2.2 §7.2 UDP 宿主（公开签名逐字实现，未增加必需参数）

```
public static PMUdpSessionEndpoint OpenServer(PMDsBootstrappedMatch boot, PMNetSessionBridge bridge,
    string listenAddress, int port, PMTransportConfig transportConfig = null, int sessionIdleTimeoutMs = 30000)
public static PMUdpSessionEndpoint OpenClient(PMDsEntryOffer offer, PMNetSessionBridge bridge,
    PMTransportConfig transportConfig = null)
public int BoundPort {get;}   public int ConnectionCount {get;}
public event Action<PMTransportConnection> Connected;   public event Action<string> Failed;
public void Pump(long nowMs, long nowUnixSeconds);      public void Dispose();
```

- 新增的两个参数都是**可选**（并行方按 4 参 / 2 参调用不受影响）；`OpenClient` 的可选参数仅为测试注入传输配置。
- `OpenServer` 要求 `bridge.IsServer`、`OpenClient` 要求 `!bridge.IsServer`（用世界判据，**不用**连接 `IsServerSide`）；
  `port` 允许 0（系统分配，联调/门禁用），`BoundPort` 回报实际值，`PMDsSessionHost` 据此与 Lobby 分配端口对账。
- `Pump` 内部**恰好一次** `bridge.Update(nowMs)`（契约 §7.2「宿主不要二次 Update」）；复制 Tick / 生命周期 /
  数据报预算按「每帧一次」记账。
- `Connected` 只在**激活成功**那一刻触发，每条连接一次（`ConnectionCount` 只统计 `IsReady` 的连接）。

### 2.3 握手帧（Transport 之外）

`magic 'P','M','H','S'` + `version(1B)=1` + `kind(1B: ClientHello=1/ServerHello=2/ServerReject=3)` +
`PMNetReader/Writer` 正文；**单帧 ≤ 1200 字节**（契约 §7.2，覆盖票据上限），超限当场拒绝、不解析、不回包。

- `ClientHello`：`ticketLen(1..352)+ticket + clientNonce(16B raw) + clientProtocolHash + clientEpoch + padding(≤512)`。
  摘要/世代是**故意冗余**的：让 DS 在花 MAC 的钱之前就能廉价拒掉。
- `ServerHello`：`matchId/dsId + epoch + protocolHash + collisionDigest + ticketDigest(32B) + clientNonceEcho(16B) +
  uid/playerId/teamId/heroId + serverNonce(16B)`。
- `ServerReject` **本实现从不发送**（契约冻结「失败无回包」）；保留解码只为识别别版本/别实现发来的拒绝帧
  （客户端不把它当成功、也不无限重试）。

### 2.4 端点消费账本（`PMDsEndpointLedger`）

- 键 = **票据全量 SHA-256（32 字节）**，由消费侧自算，**不用** A1 的 32 位指纹（R3-A 审查已判其只能做诊断）。
- 规则：同票同端点 ⇒ **幂等**（返回同一 `ConnectionId`，不新建）；同票**第二端点** ⇒ `DigestRebound`；
  端点被别的票占用 ⇒ `EndpointTaken`；uid 被别的票占用 ⇒ `UidBusy`（早于桥的 uid 查重，避免白烧一次激活）；
  超容量 ⇒ `Capacity`；端点键非法/过长 ⇒ `BadEndpoint`。
- **断开即墓碑**：`ReleaseEndpoint` 在票据未过期时留墓碑（旧票不得换端点或同端点重连）；
  已过期则直接回收（重放会在验票阶段被 `Expired` 拒）。
- **票据过期不影响已激活会话**：账本不按到期摘除 Active 绑定；到期只驱动「新入场验票拒绝」与「墓碑回收」。
- `RenounceConsume`：已消费但**未能建链/回包**（激活失败、容量满、回包会放大）时撤回消费且**不写墓碑**
  ——否则一个因环境问题失败的客户端会被永久挡在门外。
- 有界：Active ≤ 名册人数（≤6），墓碑 ≤ 64（超出淘汰最旧）。

### 2.5 未认证字节的闸门（纵深）

次序冻结为：`IsHandshakeDatagram → PMHandshakeServer.TryHandle → 逐身份验票 → 账本消费 →
PMSessionIdentity → PMTransportConnection → TryActivate → 此后才 OnDatagram`。
`PMTransportConnection.OnDatagram` 再加一道**各自充分**的纵深闸门：`!IsReady` 直接丢，
**不进 Transport** ⇒ 不 `TrackIncomingPacketId`、不 ack（等价证据：`Transport.Stats.DatagramsReceived`
与 `AcksSent` 都不变）。原因是 Transport 收到合法数据报就立刻回 ack，对端据此**退休可靠消息**，
之后应用层再丢弃 ⇒ 静默的可靠性破洞（无 NAK、无重传）。

## 3. 测试（`Tools/PMUdpAdmissionTest`，303 项 / 0 失败 / exit 0）

用**生产实现**：`PMDsMatchKey.Create` + `PMDsTicketIssuer` + `PMDsBootstrapDocument`（引导文件字节往返）、
`PMHandshakeCodec/Client/Server`、`PMDsEndpointLedger`、`PMUdpSessionEndpoint`（真实 localhost UDP、随机端口）、
`PMNetSessionBridge` + `PMNetWorld` + `PMReplicationChannel` + `PMTransport`（真实分片/序号/ack）。
只有**描述符**是手工的（生成链由 R2 门禁覆盖）；**未使用 7777/7778/7800**。

| 段 | 覆盖 | 项数 |
|---|---|---|
| A | 入局通知 codec：前缀/严格尾部/Base64 规范/范围/版本/票据魔数/上限形态 | 40 |
| B | 握手 codec：帧上限/严格尾部/非握手帧不误判/`ComputeServerHelloBytes` 与实际逐字节一致/ServerReject/客户端关联校验（错 nonce/摘要/对局/世代/身份）+「不符达上限即明确拒绝」 | 44 |
| C | 账本：幂等/第二端点/端点占用/uid/容量/墓碑/过期/端点键/`RenounceConsume`/墓碑淘汰/过期清理 | 51 |
| D | 真实 UDP 两玩家：入局、`Connected` 各 1 次、身份来自票据、Create 初值、客户端→服务端 RPC→属性回程收敛、复制 ACK 双向、无协议错误 | 45 |
| E | 闸门：未激活不进 Transport/不 ACK（含**正例对照**证明同一字节在已激活连接上会被接收并 ack）、未认证端点数据报被丢且**无回包**、客户端激活前/非预期来源字节被丢 | 25 |
| F | 票据负例：错 MAC/错声明摘要（廉价预检）/错声明世代/错局/错摘要/名册外身份/第二端点/帧超限/垃圾帧，逐条断言**无回包 + 无新建 + 未消费票据**；正例断言**应答不大于请求** + nonce/摘要/对局/身份回带一致 + 幂等重试不新建 | 54 |
| G | 断开⇒墓碑（旧票换端点被拒）、Lobby 重发新票可重入场、端点级空闲超时回收并留墓碑、**票据过期 120 秒后已激活会话保持且数据仍流动**（新入场被验票拒绝） | 28 |
| H | 帧边界：上限 1200 常量、最大合法帧被接受且应答不大于请求、填充超限的「等于上限帧」被拒、上限 +1 拒绝、超限数据报不进 Transport、offer 文本上限 | 16 |

## 4. build / run 实证（原始退出码）

```
dotnet build Tools/PMUdpAdmissionTest/PMUdpAdmissionTest.csproj -c Release   -> 0 警告 / 0 错误
dotnet Tools/PMUdpAdmissionTest/bin/Release/net8.0/PMUdpAdmissionTest.dll    -> exit 0
    全部通过：303 项检查，0 项失败
```

## 5. 缺陷注入（每组「临时改生产代码 → build → run → 按 md5 还原」，全部抓到）

| 组 | 注入 | 观察到的失败（摘） | 结果 |
|---|---|---|---|
| 1 | 去掉 `OnDatagram` 的 `!IsReady` 闸门 | `未激活时数据报被丢弃` 实际 0、`Transport 入站计数为 0` 实际 1、`链路上没有产生任何出站数据报` 实际 1（3 项） | 300/3 |
| 2 | 账本接受「同票第二端点」 | `同票第二端点 ⇒ 拒绝`、`DigestRebound` 计数 0、`第二端点：没有消费任何票据` 实际 3（4 项） | 299/4 |
| 3 | DS 侧丢掉**逐身份**验票（只用不带身份的 MAC 校验） | `名册外身份：**无回包**`、`没有新建连接`、`没有消费任何票据`（4 项） | 299/4 |

还原后 `md5sum -c` 对两个被注入文件均 `OK`；上行复跑 303/0。

## 6. 旧门禁的行为变更（3 项失效，**未改对方测试**，需主集成调整）

`PMNetSessionTest`：**340 项通过 / 3 项失败 / exit 1**，三项同一根因（本次有意变更）：

| 断言（原文） | 实际/期望 | 原因 |
|---|---|---|
| `未激活时字节仍到达传输回调（计数 1）` | 0 / 1 | `OnDatagram` 现在未就绪即丢，字节**不再进 Transport** |
| `旧世代数据报没有到达应用层（仍是 1 条）` | 0 / 1 | 该用例喂的是**未激活**连接，同样被闸门拦下 |
| `旧世代丢弃被传输层记账` | 0 / >0 | 同上（`DroppedStaleSession` 不再增长，因为根本没进 Transport） |

这正是契约 §7.2 要求的语义（「未认证数据报必须在进入 Transport 前拒绝以免先 ACK 后丢业务」）。
建议主集成在 `PMNetSessionTest` 中把这三条改为「未就绪 ⇒ 不增加 Transport 入站/ACK 计数」，
并**另用一条已激活连接**（正例）覆盖旧世代记账（本报告 §3 E 段已给出该写法的可复用断言）。
**本轮未改动任何对方测试文件。**

## 7. 已检查的回归门禁（本次全部重新 build + run）

| 门禁 | 结果 |
|---|---|
| `PMNetLangCheck` build | exit 0（C#7.3 + netstandard2.0，0 警告 0 错误） |
| `PMUdpAdmissionTest` build/run | 0 / 0（303 项） |
| `PMNetVerify` | exit 0（27 项） |
| `PMNetLaunchCheck` | exit 0（133 项） |
| `PMCallspaceCheck` build/run | 0 / 0（93 项） |
| `PMTransportTest` | exit 0（81 项） |
| `PMNetWorldTest` | exit 0（197 项） |
| `PMReplicationTest` | exit 0（265 项 / 0 失败） |
| `PMNetE2E` | exit 0（209 项） |
| `PMDeclModel` build | exit 0 |
| `PMNetSessionTest` | **exit 1（340/3）**，见 §6（预期） |
| `PMClientCheck` build | exit 1（14 个 `PMNet.R3/PMR3Player` 缺失）——**并行 Unity 组源码未落地，与本次无关** |
| `PMUnityGlueCheck` build | exit 1（2 个 `PMDsSessionHost` 缺失）——同上 |

**并行接口已对上**（集成证据，只读）：`PMDsSessionHost.cs:410` 调
`OpenServer(boot, _bridge, options.ListenAddress, port)`、`PMClientSessionHost.cs:189` 调
`OpenClient(offer, session.Bridge)`，并已使用 `BoundPort / Connected / Failed / Pump / ConnectionCount / ClientFailed`
（`:418/427/428/501/555`、`:199/268/271`）——与 §2.2 冻结签名一致。

## 8. 未闭合 / 诚实边界

1. **不是密码学服务器认证**：客户端只持 bearer 票据、没有密钥，因此首版只做「与 Lobby 通知的关联校验」
   （nonce 新鲜 + 票据 SHA-256 + 对局/DS/世代/摘要/身份）。它挡不住能读到票据的本机攻击者，也不构成 MITM 防护；
   本轮**未引入** DTLS/自研加密或第二把密钥（契约 §7.2 明确不自造）。
2. **未验证真实跨机网络**：门禁是真实 UDP 但仅 loopback；RTT/MTU/NAT/丢包分布未实测；
   「应答不大于请求」是 1200B 内的字节级约束，未做流量分析层面的评估。
3. **握手帧上限 1200 而最大良构帧更小**：填充上限 512，因此良构 `ClientHello` 实际远小于 1200；
   1200 是拒绝畸形帧的天花板，不是常规尺寸。
4. **客户端不本地判票据到期**（无密钥、也不解析票据）：到期一律由 DS 在验票阶段拒绝，
   客户端表现为「重试到上限后 `Failed`」。
5. **端点级空闲超时**（默认 30s）是本模块自定值，尚未与 Lobby 心跳口径对齐；
   传输层自带 10s 空闲判定在「收到过流量之后」生效，两者互补。
6. **`Dispose` 不发断开通知**（走 `ProtocolError` 语义，Transport 只在 LocalClosed/PeerClosed 时通知对端）：
   避免向未认证端点泄漏「这里有个 DS」。
7. **不做 UDP 层限速/黑名单**：只有「失败端点表（≤64）+ 单端点失败 8 次后 30s 冷却」这一层有界保护。
8. **不覆盖**：Lobby 控制 listener/全局 uid 占用/端口池/引导文件原子发布（Lobby 组）；
   Unity 场景、`PMR3` 声明产物与客户端分流（Unity 组）；`BuildLifecycleBatch` 字节预算（M04 边界，仍按 R3-A 的显式断连兜底）。
9. **T42 仍 PENDING**：真实 Unity DS 进程 + 权威场景 + 两客户端闭环未做（编辑器占用时不得抢锁）。

## 9. 交接要点（主 Agent / 集成方）

- 四个生产文件与三个 meta 已就位并自洽（§1）；并行宿主的调用签名已核对（§7 末）。
- 主集成需处理 `PMNetSessionTest` 的 **3 项**旧断言（§6），改法建议已给出；**本轮未代改**。
- 若要在 Lobby 侧落实「全局 uid 占用与端口池」，属 Lobby 组写入边界；本模块只负责**每局端点账本**。
- 真实 Unity 验收（T42）依赖：引导文件由 Lobby 原子发布 → DS `PMDsSessionHost` 建场景并 `OpenServer` →
  Ready 带 `BoundPort` → Lobby 下发 `PMDS1:` offer → 客户端 `PMClientSessionHost` `OpenClient` → 结算退出。
  本模块在上述链路中承担的正是「UDP 准入 + 端点账本」这一段。
