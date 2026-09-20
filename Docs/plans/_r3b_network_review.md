# R3-B 网络组主集成复核报告（Session 准入 / 握手 / 端点账本）

> 边界：只读复核 + 仅在允许清单内修确定缺陷。允许写：`Session/{PMDsEntryOffer,PMHandshake,PMUdpSessionEndpoint,PMTransportConnection}.cs`、
> `Tools/PMNetSessionTest/Program.cs`、`Tools/PMUdpAdmissionTest/Program.cs`、本报告。未改协调器 / Unity / PMR3 / 旧业务 / 主计划 / R3 契约。
> 必读已读：根 `AGENTS.md` → `Client/Assets/AGENTS.md` → `Server/AGENTS.md` → `net-architecture-migration.md` 末 R3/R3-B →
> `net-r3-control-contract.md` §7 → `_r3b_network_report.md` 全文 → `_r3b_auth_survey.md` 全文。**不采信 303 绿**：先复现、后修、再回归。
> 契约 §7.1/§7.2 的冻结签名（`PMDsEntryOffer`/`PMDsEntryCodec`/`PMUdpSessionEndpoint`）**形参未动**；本轮只做兼容性新增。

## 0. 本轮计划与执行顺序

1. 复现 `PMNetSessionTest` 三条旧期望失效（根因是该轮有意的语义变更，不是回归）。
2. 按 6 项风险逐条独立复核：**只在有真实复现/明确代码证据时**修；无缺陷项只保留回归。
3. 修确定缺陷 → 同步修正受影响的门禁断言（不删覆盖，改为更严的负例 + 正例对照）。
4. 逐项 `build` 退出 0 后再 `run`；新增用例用真实 loopback UDP + 随机端口。
5. 生产代码故障注入：先存原字节到唯一 temp，再内存改写，`finally` 恢复并核对 sha256。
6. 固定口径回填本报告：已确认 / 修复 / 测试 / 未验 / 范围。主计划不改。

## 1. 已确认（证据级）

| # | 结论 | 证据 |
|---|---|---|
| A-1 | `PMNetSessionTest` 3 条旧期望失效 = 本轮有意语义变更的后果，不是回归 | 基线实跑 **340/3**；三条同因：`PMTransportConnection.OnDatagram` 的 `!IsReady` 纵深闸门使字节不进 Transport、不 ACK |
| A-2 | **真实缺陷：墓碑容量淘汰会丢掉「仍有效已消费旧票」的重放保护** | `EnforceTombstoneCapacity` 直接 `_byDigest.Remove(oldest)`；该票尚未过期（`ReleaseEndpoint` 只在未过期时写墓碑），其 `_byEndpoint`/`_byUid` 已在释放时摘除 ⇒ 同票**换端点**再消费被当作「全新票据」接纳。**复现**：新用例失败并显示 `reject=None`、`ConnectionsAllocated` 由 2 涨到 3（真的新建了认证连接） |
| A-3 | **真实缺陷：账本摘要数组以引用暴露，且同时是字典键** | `entry.Digest` 与 `created.TicketDigest` 是**同一数组**，也是 `PMTicketDigestKey` 持有的字节。外部就地改写 ⇒ `ReleaseEndpoint`/`RemoveEntry` 的 `Remove` 找不到条目、而探查键哈希又不匹配 ⇒ 重放被当新票。**复现**：改写后同票换端点重放被接纳（`reject=None`） |
| A-4 | **真实缺陷：单条畸形数据报可让 `Transport.Update` 抛异常并中断宿主 `Pump`** | `PMTransport.ProcessDatagram` 消息循环只判一次 `p >= pkt.Length` 就读 `flags`+`streamRaw`；`PacketHeaderBytes=18`、`messageCount=1` 的 19 字节已认证数据报在 `pkt[19]` 抛 `IndexOutOfRangeException`，`DrainInbound` 无 try/catch ⇒ 冒泡出 `PMUdpSessionEndpoint.Pump`（同一帧**所有**端点的收包/握手/flush/复制 Tick 一起中断）。**复现**：`threw=true`，`ProtocolErrors=0`，世界登记未摘除 |
| A-5 | 重复 ClientHello 不重复 Connected、不重复 Spawn（现状正确） | `ServerHandshake` 先按 `_byEndpoint[key].ConnectionId == binding.ConnectionId` 走幂等重发；账本按全量 SHA-256 幂等返回同一 `ConnectionId`。**回归**：两次 Hello（首次应答尚未处理）⇒ 连接 1、ConnectionId 1、Connected 1、Spawn 1、`HandshakeRetries>0` |
| A-6 | 未认证/未注册数据报**无回包**、不进 Transport、不 ACK（现状正确） | 次序 `握手→验票→账本→激活→OnDatagram`；`ServerReject` 从不发送；失败路径恒 `Response==null`；`OnDatagram` 的 `!IsReady` 闸门。既有 E/F/H 覆盖全绿 |
| A-7 | 同端点第二张（有效）票据不扰动已认证活连接；票据到期不踢已激活会话（现状正确） | `TryHandle` 失败分支不触碰 `_sessions`/`_byEndpoint`；`TryConsume` 先 `_byDigest` 再 `_byEndpoint`/`_byUid`；`PruneExpired` 只清墓碑。**回归**：同端点第二张有效票 ⇒ 无回包、无新建、活连接仍就绪且仍能收数据；票据过期后会话与数据流保持 |
| A-8 | 票据与名册身份**四字段全绑定**，且 offer.Identity 全字段被两侧校验（现状正确） | DS 侧 `PMDsTicketCodec.Verify(..., checkIdentity:true, expectedIdentity,…)`（`identity.Equals` 比 `Uid/PlayerId/TeamId/HeroId`）由 `PMHandshakeServer` 对名册逐身份调用；客户端再把 ServerHello 四字段与 `offer.Identity` 逐一比对 |
| A-9 | 可靠流在「未激活期被丢」后**不会静默消失**，但恢复依赖后续流量（见 §4） | **回归**：未激活期字节被闸门丢且 `AcksSent=0`、`ReliableOutstanding>0`；就绪后一旦有真实流量把确认水位推过丢包点，发送端由 `RetransmitOlderThan` 推断丢包并重传 ⇒ Create 补齐（`ReliableResent>0`、`ReliableOutstanding=0`） |

## 2. 修复（均在允许清单内；全部最小改动 + 兼容性新增）

| # | 文件 | 改动 |
|---|---|---|
| F-1 | `Session/PMHandshake.cs` | 墓碑淘汰改为 **fail-closed 盲区窗口**：淘汰未过期墓碑时把它的到期时刻记为 `_replayBlindUntilUnixSeconds`；在该时刻之前，**未知摘要**的新消费一律 `Capacity` 拒（已有绑定/墓碑的幂等重试不受影响）。票面过期后重放会被验票阶段直接拒（`Expired`），账本自动恢复接纳。保住「墓碑 ≤ 上限」的有界性，同时消除「旧票换端点复用」。新增 `PMDsEndpointLedgerStats.ReplayBlindRefusals`、只读 `ReplayBlindUntilUnixSeconds`；`ReleaseSession()` 同时复位该窗口（终态已放弃整局重放保护） |
| F-2 | `Session/PMHandshake.cs` | 账本**自持**摘要字节：字典键/`entry.Digest` 用内部数组，`PMSessionBinding.TicketDigest` 改为**独立副本**（`Clone()`）⇒ 外部就地改写不再破坏账本键与重放保护；按内容查找的 `RenounceConsume` 语义不变（已加回归） |
| F-3 | `Session/PMTransportConnection.cs` | 两处 `Transport.Update` 调用统一走 `RunTransportUpdate`：捕获**传输层解析**异常 ⇒ `TransportUpdateFaults++` 并 `ProtocolError` 断连本连接（不再冒泡中断宿主 `Pump`）；**业务异常仍按 R2 口径向上传播**（以 `BusinessExceptions` 计数区分，未吞） |
| F-4 | `Tools/PMNetSessionTest/Program.cs` | 三条旧期望按新语义改写（未就绪 ⇒ Transport 入站/ACK/出站计数为 0）+ **已激活连接的正例对照**；旧世代覆盖改用**已激活连接**真实走到 `DroppedStaleSession`（覆盖未删）；新增 §L「未激活期丢弃不得误 ACK ⇒ 就绪后可靠流补齐」16 项 |
| F-5 | `Tools/PMUdpAdmissionTest/Program.cs` | 墓碑容量用例改为断言 fail-closed（超出后新票被拒 + 旧票换端点仍被拒 + 盲窗结束恢复接纳）；新增摘要所有权 / `RenounceConsume` 按内容查找 / 重复 ClientHello 幂等 / 同端点第二有效票不扰动活连接 / 超长与垃圾数据报不中断端点进度 / 19 字节畸形数据报不使 `Update` 抛出（新增 §I 共 30 项） |
| F-6 | 不动契约文件 | §7.1/§7.2 冻结签名形参未变；新增项均为**兼容性新增字段/只读属性**，`OpenServer`/`OpenClient` 的可选参数面不变 ⇒ 无需改 `net-r3-control-contract.md` |

## 3. 测试（口径：先 `build` 退出 0，再 `run`；全部为本次实际退出码）

| 门禁 | 结果 |
|---|---|
| `PMNetSessionTest`（L 组新增） | build 0/0；**run 0：372 项 / 0 失败**（基线 340/3） |
| `PMUdpAdmissionTest`（真实 loopback UDP、随机端口；I 组新增） | build 0/0；**run 0：349 项 / 0 失败**（基线 303/0；修复前新用例 329/12） |
| `PMTransportTest` / `PMNetWorldTest` / `PMReplicationTest` | 0：81/0；0：197/0；0：265/0（含 5/5 缺陷注入） |
| `PMNetE2E` / `PMCallspaceCheck` / `PMNetVerify` / `PMDeclCheck` | 0：209/0；0：93/0；0：27/0；0：71/0 |
| `PMNetLaunchCheck` / `PMDsControlTest` / `PMDsLobbyTest` | 0：133/0；0：506/0；0：230/0 |
| `PMR3RuntimeTest` / `PMR3IntegrationTest` / `PMUdpRouterTest` / `PMBattleSimTest` | 0：134/0；0：143/0；0：44/0；0：345715/0 |
| `PMNetLangCheck`（C#7.3 + netstandard2.0，库工程）| build 0 警告 0 错误 |
| `PMClientCheck` / `PMUnityGlueCheck` / `PMUdpRouterCheck` / `PMSharedConfigCheck` / `PMHeroDataCheck`（**库工程，仅 build**）| build 退出 0 |
| `Server/Server.csproj` | build 0 错误（8 个警告全为既有 NU1701 包兼容警告，与本改动无关） |

**复现证据（修复前）**：新增用例先跑出 **12 项失败**，恰好覆盖 A-2（4 项：fail-closed、Capacity、ReplayBlindRefusals、没有新建 ConnectionId）、A-3（2 项）、A-4（4 项）以及 2 项我自己的测试构造错误（后已修正：回包未排空、19 字节计数断言）。这是真实红→绿，不是模拟。

## 4. 未验 / 诚实边界（不在本轮能力内，或属他人写边界）

- **T42 仍 `PENDING_USER`**：真实 Unity DS 进程 + 权威场景 + 两客户端闭环未做；本轮全部为纯 C# 门禁 + loopback UDP。
- **可靠重传是 ack/NAK 驱动，M03 无定时器**（A-9 实测）：接收方就绪后**发送方完全静默且双方再无流量**时，被丢的可靠消息要等到下一次真实流量或传输层空闲超时重连才补齐。实战中 DS 每帧都有生命周期/复制流量，故影响有限；**加定时重传等于新增传输层行为/协议，本轮不做**，登记为 M03 边界。
- **`PMTransport.ProcessDatagram` 的未守卫读属 M03 写边界**：本轮只在会话层做纵深兜底（F-3），未改核心；建议 M03 自行补齐长度守卫。
- **失败端点冷却与端点键同源**：`IsRateLimited` 在解析前生效，理论上可由同端点（需伪造源端口）的恶意帧把合法客户端的重试一起冷却 30 秒。属「本机可伪造源地址」的既定威胁边界，未引入第二套限速。
- 跨机网络（RTT/MTU/NAT/丢包分布）与流量层面放大评估未做；「应答不大于请求」仅为 1200B 内字节级约束。
- 客户端不本地判票据到期（无密钥），表现为「重试到上限后 `Failed`」。
- 另：并行组的 `Tools/PMR3IntegrationTest/Program.cs` 曾在一次构建中报 `CS0030`（该目录为未跟踪、非本轮写边界）；随后重跑 build 0 + run 143/0，判定为对方编辑中的瞬时状态，非本轮引入。

## 5. 范围（本轮只读审过 / 未改）

`Control/**`（A1 冻结）、`Transport/**`、`World/**`、`Replication/**`、`Server/DS/**`、Unity/`PMR3` 产物、旧业务链路、
`net-architecture-migration.md`、`net-r3-control-contract.md`、`_r3b_auth_survey.md`、`_r3b_network_report.md`。
未执行任何 git/svn 写操作；未编译 Unity。

## 6. 生产代码故障注入（先存原字节→内存改写→跑→`finally` 恢复→核 sha256）

| 组 | 注入 | 观察到的失败（摘） | 结果 |
|---|---|---|---|
| 1 | 取消墓碑淘汰的 fail-closed 盲区拒绝（`nowUnixSeconds < 0L`） | 旧票换端点重放被拒 / 原因 Capacity / ReplayBlindRefusals / 没有新建 ConnectionId（4 项） | build 0、run 1、**4 项失败**；恢复后 sha256 与注入前一致 |
| 2 | 账本把内部摘要数组直接暴露（`= digest`） | 绑定摘要被外部改写后重放仍被拒 / 拒绝原因 DigestTombstoned（2 项） | build 0、run 1、**2 项失败**；sha256 恢复一致 |
| 3 | 取消解析异常收敛（`BusinessExceptions >= businessBefore` 恒真⇒重新外泄） | 不得让 `Update` 抛出 / TransportUpdateFaults / ProtocolErrors / 只断本连接 / 世界登记已摘除（5 项） | build 0、run 1、**5 项失败**；sha256 恢复一致 |
| 4 | 取消 `OnDatagram` 的未就绪纵深闸门（回归旧语义） | 未激活时不应有应用消息到达 / **Transport 入站计数应为 0** / 不应有任何出站数据报 / 字节被闸门丢弃计数 / 发送端从确认水位推断丢包（5 项） | build 0、run 1、**5 项失败**；sha256 恢复一致 |

固定哈希（本轮最终态，全部注入均已还原到该值）：
`PMHandshake.cs` = `ab86cf960d8de1f594cf9060c02034e1559d6111a493b3f7b349e409c2b91be1`（注入期间为 `eb52ba17…`，注入后用 `ReleaseSession` 复位窗口仅再加一行，现值见左）；
`PMTransportConnection.cs` = `c892fe7d026831927431246e3bf5ded9605b62249e606908e75ea626093378f4`。

## 7. 交接要点

- 四个生产文件自洽可用；`PMNetSessionTest` 的三条旧断言已按契约 §7.2 语义收口，且旧世代覆盖**未删**（改用已激活连接）。
- 若后续要处置 §4 的两条 M03 边界（解析长度守卫、静默期可靠重传），须在 M03 写边界内提出，并同步 R3 契约与本计划。
- 真实验收仍待 T42；本轮结论只适用于纯 C# + loopback 范围。
