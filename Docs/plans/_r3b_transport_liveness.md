# R3-B 传输活性（周期 keepalive / 静默期丢包恢复 / 核心长度守卫）

> 写边界（**第 1 轮**：keepalive / 核心长度守卫）：`Client/Assets/Scripts/PMNet/Transport/PMTransport.cs`、
> `PMTransportTypes.cs`、`Tools/PMTransportTest/Program.cs`、`Tools/PMNetSessionTest/Program.cs`、本文件。
>
> 写边界（**第 2 轮**：纯 ack 追赶修复 `ackloop` + 门禁收口，见 §6）：`PMTransport.cs`、
> `Tools/PMTransportTest/Program.cs`、`Tools/PMUdpAdmissionTest/Program.cs`、本文件。
> 第 2 轮**未再改** `PMTransportTypes.cs` 与 `Tools/PMNetSessionTest/Program.cs`。
>
> 两轮都未改协调器 / Unity / PMR3 / 旧业务 / 主计划 / R3 契约；未执行 git/svn 写操作；未编译 Unity。

## 0. 必读文档与首搜入口（本轮的搜索依据）

按委派要求的顺序完整读过：

1. `D:/UGit/hyld-master/AGENTS.md` —— 只给出两份分仓入口文档的指针。
2. `Client/Assets/AGENTS.md`、`Server/AGENTS.md` —— 双端链路（旧 UDP 7777 / 新 `PMNet` 链路）、
   参数表（`pingIntervalMs=200f`、`frameTime=0.016f`）与「协议来源只有一份」的约定。
3. `Docs/plans/net-r3-control-contract.md` §4/§7 —— **冻结了本轮所有可动边界**：
   `MaxDatagramBytes=1200`、`MaxFragmentsPerMessage=64`、每帧数据报预算 32、
   `PMTransport.Update(now, sendBudget)` 的语义、以及「A2 如发现现有 Transport 限制不足必须报告，
   禁止在边界外悄悄修」。
4. `Docs/plans/_r3b_network_review.md` §4 —— 明确登记了**两条 M03 边界**并指出「须在 M03 写边界内提出」：
   - 「`PMTransport.ProcessDatagram` 的未守卫读属 M03 写边界」；
   - 「可靠重传是 ack/NAK 驱动，M03 无定时器 …… 发送方完全静默且双方再无流量时补齐依赖后续流量」。

这两条就是本轮的首搜入口：`Client/Assets/Scripts/PMNet/Transport/**`（`PMTransport.cs` /
`PMTransportTypes.cs` / `PMTransportInternals.cs`）与两个门禁 `Tools/PMTransportTest`、
`Tools/PMNetSessionTest`。**未**按业务昵称或宽目录做任何盲搜。

## 1. 已确认（复现级证据，先复现后修）

| # | 结论 | 证据（均为修复前实跑或源码级事实） |
|---|---|---|
| A-1 | `ControlPing` 在既有线格式里**只有解析分支、没有发送方** | `PMTransport.ReadControl` 对 `ControlPing` 直接 `return true`；全文件无任何写出 `ControlPing` 的位置。即控制帧类型 1 是既有协议里的「死代码」。 |
| A-2 | `IdleTimeoutMs=10000` 会把**完全健康**的静默会话判成超时 | 探针 S2（两端零业务、keepalive 关闭、12s 驱动）：双方 `IsConnected=false / reason=Timeout`，且 `DatagramsSent=0`。原因是看门狗判据是「没收到任何数据报」，而两端都没业务时本来就没有数据报。 |
| A-3 | 静默期**首个**可靠数据报丢失后无从恢复（既无 NAK 也无 ack 推断） | 探针/J2a：丢 A→B 第 1 个数据报后不再有任何业务 ⇒ 对端 `ReliableDelivered=0`、发送端 `ReliableOutstanding=1`、`ReliableResent=0`。接收端把收到的第一个包当基线（看不到它之前的缺口），发送端的 `RetransmitOlderThan` 又需要 ack 水位。 |
| A-4 | **初始静默期不永生**这一条此前不成立 | 旧判据带 `_lastRecvMs >= 0L` 前置：一个字节都没收到过的连接，`_lastRecvMs` 恒为 -1 ⇒ 看门狗永不触发。 |
| A-5 | 19 字节畸形数据报会让核心抛异常并中断宿主整帧 | 会话层注释与 PMUdpAdmissionTest §G/I 逐字记录了该现实路径（核心消息循环只判一次 `p >= pkt.Length` 就读两字节）。本轮 K 组在**修复后**把这一条翻成可验收断言；去掉守卫（注入 C）即复现 2 次抛出。 |
| A-6 | 周期 keepalive 不需要新的线格式 | 控制帧的写出形式（`FlagControl` + stream 占位 + type 字节）在 NAK 路径里已经存在，Ping 只是 type=1 的同类帧。**未改 header**。 |

## 2. 修复（全部落在写边界内；兼容性新增，不改既有语义）

| # | 文件 | 改动 |
|---|---|---|
| F-1 | `PMTransportTypes.cs` | 新增 `PMTransportConfig.KeepAliveIntervalMs`，**默认 1000ms**，`0` = 禁用（测试用）。新增统计 `PMTransportStats.KeepAlivesSent/KeepAlivesReceived`（并进 `ToString`）。`IdleTimeoutMs` 默认仍为 10000ms，**只是判据的基准被补齐**。 |
| F-2 | `PMTransport.cs` | **周期 keepalive**：`NormalizeClock`（首次登记时钟起点 + 墙钟倒退重基准）→ `IsKeepAliveDue(now)` 按 `now - _lastSendMs >= KeepAliveIntervalMs` 判定 → `FlushOutbound(budget, now)` 只在本轮**第一个**数据报上施加 `keepAlive`，`BuildDatagram(keepAlive, out written)` 把控制 Ping 与 NAK/业务消息**并进同一个数据报**。任何一次成功发送都刷新 `_lastSendMs`。 |
| F-3 | `PMTransport.cs` | **不额外绕过预算**：keepalive 走同一条 `FlushOutbound`，`budget <= 0` 时 `Update` 直接 return（连 ping 也不发）；传入预算仍被夹到 `MaxDatagramsPerUpdate` 以内。 |
| F-4 | `PMTransport.cs` | **不回 ping**：`ReadControl` 的 `ControlPing` 分支只 `_stats.KeepAlivesReceived++`。对端的 ack 反馈走既有的 `_ackDirty`（收到任何新数据报都会置位），因此一个 ping 至多换来一个普通纯 ack 数据报，**不存在 ping 互相追赶**。 |
| F-5 | `PMTransport.cs` | **初始静默期看门狗**：`IdleTimeoutMs > 0` 时基准改为 `_lastRecvMs >= 0 ? _lastRecvMs : _firstUpdateMs`，覆盖「从未收到过任何数据报」的区间，消除 A-4 的永生。 |
| F-6 | `PMTransport.cs` | **核心侧最小长度守卫**（review §4 建议的 M03 自补）：消息循环里读完 `flags` 后补 `p >= pkt.Length` 检查，并为 `seq(4)` / 分片头`(8)` / `payloadLen(2)` 三段定宽读取各补一条越界守卫（截断消息一律计 `ParseErrors` 并 `break`，不抛）。**不吞业务异常**：本方法只做长度判定，不引入任何 try/catch。 |
| F-7 | `Tools/PMTransportTest/Program.cs` | `MakePair` **显式** `KeepAliveIntervalMs = 0`（旧 T38 断言统计绝对报文数）；`SimLink` 新增方向性 `DropNext`（既有的 `DropNth/DropSecond` 按全局发送序号丢，无法表达单向丢失）；新增 `DeliverAll/Frames/FramesOneSided/Nth/RawDatagram/FeedQuiet` 与 `CheckEq/CheckGt/CheckStrEq`；新增 J 组（47 项）、K 组（7 项）。 |
| F-8 | `Tools/PMNetSessionTest/Program.cs` | `Scenario` 新增 `TuneTransportConfig` 钩子；`AddConnectionTo` **显式** `KeepAliveIntervalMs = 0`（A–L 组统计绝对报文/入站计数）；新增 M 组（33 项，真实桥 + 真实 `PMTransportConnection`）。 |
| F-9 | 不动契约文件 | §4/§7 的冻结面（`MaxDatagramBytes`/`MaxFragmentsPerMessage`/32 预算/`Update(now,budget)` 语义/`PMUdpSessionEndpoint` 形参）**一个都没动**；所有新增都是兼容性新字段与只读计数。 |

### 2.1 为什么这样就能修 A-2/A-3（口径：不发明新 RTO）

周期 Ping 的作用是**把两端的 ack/NAK 机制重新接上电源**，它自己不做任何重传：

- 发送端侧：ping 数据报携带本端 ack 水位 ⇒ 对端据此推进 `_highestAckedPacketId`
  ⇒ `RetransmitOlderThan` 才能推断「首个数据报丢了」。
- 接收端侧：ping 数据报**携带一个更新的 packetId** ⇒ 对端 `TrackIncomingPacketId` 立刻看到缺口
  ⇒ 发 NAK ⇒ 发送端重传。

**未新增定时重传（RTO）**。原因：本轮全部被测场景都能由上面两条既有机制恢复（见 §3 的 J2b/J3/M3）。
口径区别（诚实记录）：keepalive 是**活性 + 缺口可见性**信号，它不是重传定时器 ——
若未来出现「ping 本身在两端持续被丢」的极端形态，仍需另行讨论有界重传，那属于新增传输层行为、
应在契约里开口子，本轮不做。

## 3. 测试（口径：先 `build` 退出 0，再 `run`；数字均为本次实际值）

| 门禁 | build | run | 基线（本文件记录值的来源见备注） |
|---|---|---|---|
| `Tools/PMTransportTest`（新增 J=47 / K=7） | 0 警告 0 错误 | **135 项 / 0 失败** | 81 / 0（**旧断言集**；在本轮实现改动之后仍全绿，随后才追加 J/K） |
| `Tools/PMNetSessionTest`（新增 M=33） | 0 警告 0 错误 | **405 项 / 0 失败** | 372 / 0（**旧断言集**；在本轮实现改动之后仍全绿，随后才追加 M） |
| `Tools/PMUdpAdmissionTest` | 0 错误 | **345 项 / 4 失败** | 349 / 0（**见 §4：本轮的既有门禁变化**） |
| `Tools/PMR3IntegrationTest` | 0 错误 | 143 / 0 | 与复核报告一致 |
| `Tools/PMR3RuntimeTest` | 0 错误 | 134 / 0 | 一致 |
| `Tools/PMNetWorldTest` | 0 错误 | 197 / 0 | 一致 |
| `Tools/PMReplicationTest` | 0 错误 | 265 / 0（PASS） | 一致 |
| `Tools/PMNetE2E` | 0 错误 | 209 / 0（PASS） | 一致 |
| `Tools/PMDsControlTest` | 0 错误 | 506 / 0 | 一致 |
| `Tools/PMDsLobbyTest` | 0 错误 | 230 / 0 | 一致 |
| `Tools/PMUdpRouterTest` | 0 错误 | 44 / 0 | 一致 |
| `Tools/PMNetVerify` | 0 错误 | 27 / 0 | 一致 |
| `Tools/PMDeclCheck` | 0 错误 | PASS 71 / 0 | 一致 |
| `Tools/PMNetLaunchCheck` | 0 错误 | 133 / 0 | 一致 |
| `Tools/PMBattleSimTest` | 0 错误 | 345715 / 0 | 一致 |
| `Tools/PMCallspaceCheck` | 0 错误 | 93 / 0 | 一致 |
| `Tools/PMNumericEquivalenceTest` | 0 错误 | 341 / 0 | 一致 |
| `Tools/PMNetLangCheck`（C#7.3 + netstandard2.0，库工程，仅 build） | 0 警告 0 错误 | — | 一致 |
| `PMClientCheck` / `PMUnityGlueCheck` / `PMUdpRouterCheck` / `PMSharedConfigCheck` / `PMHeroDataCheck`（库工程，仅 build） | 退出 0 | — | 一致 |
| `Server/Server.csproj`（链接同一份 PMNet 共享源码） | **0 错误 / 8 警告**（4×NU1701 BouncyCastle+iTextSharp、4×CS8981 `pb/pbc/pbr/scg`，全部既有且与本改动无关） | — | 与复核报告「8 个警告全为既有 NU1701」同量级 |

> 上表中 `PMTransportTest` / `PMNetSessionTest` / `PMUdpAdmissionTest` 三行是**第 1 轮**的值；
> 它们已被第 2 轮的 ackloop 修复改写（含一条仍未解除的 Session 耦合），最新值与差异见 §6.3。

### 3.1 J 组（`PMTransportTest`，真实 `PMTransport` + 可控链路；47 项）

| 用例 | 关键断言（节选，实际值） |
|---|---|
| J1 纯静默 12s，keepalive=1000 / idle=10000 | 两端 `IsConnected=true`；`KeepAlivesSent=1/1`（有界 ≤13）；`KeepAlivesReceived>0`；静默期每帧发送量有界（50 帧共 100 个数据报） |
| J1c 同场景 keepalive=0 | t=9980ms 时仍连通；t=10380ms 时双方 `Timeout`；**全程 `DatagramsSent=0`** ⇒ keepalive 确实是纯静默下唯一活性来源 |
| J2a 丢首个可靠数据报 + keepalive=0 | 对端 0 条、`ReliableOutstanding=1`、`ReliableResent=0`（复现旧缺口） |
| J2b 同场景 keepalive=1000 | 交付 **恰好一次**、内容一致、`ReliableResent=1`、`ReliableOutstanding=0`；再静默 2s 仍恰好一次 |
| J3 末个可靠数据报**与其 ack 同时丢失**（单向丢失） | keepalive 下两条都补齐且保序（m0,m1）、`ReliableOutstanding=0` |
| J3c 同 J3 但 keepalive=0 | 仅 1 条到达，`ReliableOutstanding=2`、`ReliableResent=0` ⇒ 永久不可恢复 |
| J4 对端停止 `Update` | 本端 `Timeout`；`KeepAlivesSent=10`（≤12）；`DatagramsSent==KeepAlivesSent`（口径可归因）；对端本地仍连通（不单方面宣布对方断开） |
| J5 预算 | `Update(now,0)` ⇒ 数据报与 keepalive **都不动**；`Update(now,1)` ⇒ 恰 1 个且就是 keepalive；到期 + 80 条待发 ⇒ **恰 32**，且该轮 `KeepAlivesSent` +1（与业务同包） |
| J6 时钟倒退 | 倒退后 `DatagramsSent`/`KeepAlivesSent` 均不变（无补偿风暴）、不误判断连；从倒退点再推进 10.6s 后仍 `Timeout`（负差值不让连接永生） |

### 3.2 K 组（核心长度守卫；7 项）

六种截断形态（19B flags-only / 20B 缺 payloadLen / 22B 缺 seq / 24B 缺 payloadLen / 24B 缺分片头 /
19B 控制帧缺 type）直接打真实 `PMTransport`：`throws=0`、`ParseErrors` +6、`DatagramsReceived` +6、
连接不断开；对照放一条完整合法 `ControlPing`（21B）：不抛、不计解析错误、`KeepAlivesReceived` +1。

### 3.3 M 组（`PMNetSessionTest`，真实桥 + 真实连接；33 项）

| 用例 | 关键断言（节选，实际值） |
|---|---|
| M1 双端静默 12.16s（`Bridge.Update` 驱动，零对象/零 RPC/零复制） | 双方 `IsReady`；真实桥路径产生 keepalive（合计 2）；服务端 `KeepAlivesSent=1`（≤14）；客户端 `KeepAlivesReceived>0`；服务端 50 帧共 50 个数据报（受 32/帧预算约束） |
| M2 对照（keepalive=0） | 9.98s 仍就绪；10.3s 双方 `Transport.IsConnected=false`、服务端 `DisconnectReason=Timeout`、`DatagramsSent=0` |
| M3 静默期丢掉服务端**第一个**数据报（承载 Create）后无任何业务 | 客户端最终拿到该对象；`client.LifecycleInbound=1`（恰好一次）；`ReliableResent=1`；`ReliableOutstanding=0`；初值 `Health=42`；两端保持就绪 |
| M4 19 字节畸形数据报走真实连接 + 真实帧 | 不抛；`ParseErrors` +1；**`TransportUpdateFaults` 不变（0）**；`ProtocolErrors=0`；连接保持 `IsReady` |

### 3.4 故障注入（先 temp 备份 → 内存改写 → `os.replace` 原子替换 → `finally` 恢复 → 核 sha256）

| 组 | 注入 | 观察到的失败（摘） | 恢复 |
|---|---|---|---|
| A | `IsKeepAliveDue` 恒假（取消 keepalive 发送） | `PMTransportTest` **117/18**：J1（4）、J2b（4）、J3（3）、J3c、J6…；`PMNetSessionTest` **402/3**：M1 全部 3 条 | sha256 一致 |
| B | 取消初始静默期看门狗（恢复 `_lastRecvMs >= 0` 前置） | `PMTransportTest` **128/7**：J1c（2）、J4（3）、J6（2）；`PMNetSessionTest` **403/2**：M2（2） | sha256 一致 |
| C | 取消核心 19 字节长度守卫 | `PMTransportTest` **132/3**：K 组（3）；`PMNetSessionTest` **401/4**：M4 四条全红（`TransportUpdateFaults=1`、`ProtocolErrors=1`、不再 Ready）——正是会话层兜底 F-3 被触发的形态 | sha256 一致 |
| C′ | 同一注入下跑 `PMUdpAdmissionTest` | **349/0**（回到其本文件记录的基线）⇒ 直接证明 §4 那 4 条失败正是本轮的守卫所引起 | sha256 一致 |

固定哈希（本轮最终态，全部注入均已还原到该值）：

```
PMTransport.cs      a82b926317ec95abebf2a66455e75564e0db52d8b65c811b469afd4404535fe1
PMTransportTypes.cs 93aa575ec06b1a138d130e619a2bdcd482e6d55fb60a1d17ab9c799dc59516d2
PMTransportTest     27d40c3d40cfec3b3a81a748beb31daee610eba9b4fcbcff3a5afb8dde4af063
PMNetSessionTest    4677373a7173c4299bfb3ba543155921a70f4921de6044dd3185d1bde0ca669a
```

（其中 `PMTransportTest` 的哈希在补 `Nth()` 后为最终值；A/B/C 三组注入均在该最终态上重跑。）

### 3.5 新增默认参数（准确值）

| 参数 | 值 | 说明 |
|---|---|---|
| `PMTransportConfig.KeepAliveIntervalMs` | **1000 ms**（新） | 0 = 禁用。生产默认开启（`PMTransportConnection`/`PMUdpSessionEndpoint` 用 `new PMTransportConfig()`）。 |
| `PMTransportConfig.IdleTimeoutMs` | 10000 ms（**未变**） | 变化只在基准：新增「从未收到数据报则用本连接第一次被驱动的时刻」。 |
| `PMTransportConfig.MaxDatagramsPerUpdate` | 32（未变） | keepalive 计入同一预算，不额外绕过。 |
| `PMTransportStats` | `+KeepAlivesSent` / `+KeepAlivesReceived`（新） | 兼容性新增字段；`ToString` 追加 `kaTx=/kaRx=`。 |

## 4. 已有门禁变化（必须知道，且**不在本轮写边界内**）

`Tools/PMUdpAdmissionTest`：**349 / 0 → 345 / 4**。四条失败**全部**是「19 字节畸形数据报」用例
（§G/I 的「④ 已认证连接收到畸形数据报」）：

```
[FAIL] 畸形数据报被记为传输层解析故障（计数非空）（实际 0，要求 > 0）
[FAIL] 畸形数据报被定为本连接的协议错误（实际 0，要求 > 0）
[FAIL] 畸形数据报只断掉本连接（不越界影响其它连接）
[FAIL] 断连后世界侧登记已成对摘除（实际 1，期望 0）
```

原因：该组断言的是**会话层兜底**的形态（核心抛 `IndexOutOfRangeException` → `RunTransportUpdate`
记 `TransportUpdateFaults` → `ProtocolError` 断连 → 世界成对摘除）。这正是
`_r3b_network_review.md` §4 自己登记的 M03 边界与其建议（「建议 M03 自行补齐长度守卫」）。
本轮按委派要求补了核心守卫后，同一条数据报在核心就被判为 `ParseErrors` 并正常结束消息循环，
**不再抛异常**，因此会话层那 4 条路径按设计不再被触发。
注入 C′ 反向证明：去掉守卫时该门禁精确回到 **349/0**。

建议的新期望（属 `Tools/PMUdpAdmissionTest/Program.cs`，本轮无权修改，交主 Agent 或该门禁负责人）：

```
Check(!threw, ...)                                                   // 保持
CheckGt(parseConn.Transport.Stats.ParseErrors, 0, "核心侧记为解析错误")  // 新增
CheckEq(parseConn.TransportUpdateFaults, 0, "核心不抛 ⇒ 不再走会话层兜底")
CheckEq(parseConn.ProtocolErrors, 0, "不算应用层协议错误")
Check(parseConn.IsReady, "本机噪声不断开健康连接")
CheckEq(parseServer.World.ConnectionCount, 1, "世界侧登记保持不变")
```

（该门禁的其余 345 项全绿 ⇒ **默认 keepalive=1000ms 没有污染它的绝对报文计数**。）

> **第 2 轮已按上述建议收口**（§6.2/§6.3），并在同一门禁里另发现一条同样依赖「纯 ack 回程」的
> 正例对照（§I ②「已激活连接会被 ack」），一并收口。畸形数据报覆盖与核心/会话两道兜底全部保留。

**不要因此删掉会话层兜底（F-3）**：`RunTransportUpdate` 的「把传输层解析异常收敛成本连接协议错误」
仍然必须保留 —— 它现在覆盖的是**其它**未预期的核心故障（而不是这条已知的 19 字节读），
且它的判据仍是 `BusinessExceptions` 计数未变，业务异常照旧向上传播。
本轮只在核心侧补守卫，**没有**在 `Update` 里加任何 try/catch、也没有吞掉任何业务异常。

## 5. 未验 / 诚实边界

1. **T42 仍 `PENDING_USER`**：没有真实 Unity DS 进程、权威场景与两客户端闭环；本轮全部为
   纯 C# 门禁 + 受控链路/loopback。
2. **「纯 ack 互相追赶」现象（第 1 轮观察到、未改）**：只要两端都在被驱动且有任意一次入站，
   `_ackDirty` 就会让每端每帧产生一个 18 字节纯 ack 数据报（实测：12s 静默探针 A/B 各 `tx=1100`；
   M1 中 50 帧共 50 个数据报）。后果：一旦静默期被 keepalive 打破，后续活性实际上由 ack 流承担，
   所以 `KeepAlivesSent` 在长时间静默里只剩 1（探针 S1：`kaTx=1`）。
   ~~本轮**没有**改 ack 生成策略~~ → **第 2 轮已修**：改成「header-only（`messageCount == 0`）的纯 ack
   数据报不再置 `_ackDirty`」，同时保留它的 ack 消费与包号跟踪。修法与证据、以及它带出的
   `PMNetSessionTest §H` 耦合（不在第 2 轮写边界内）见 §6。
3. **未加定时重传（RTO）**：见 §2.1。所有被测恢复路径都由既有 NAK / 发送端 ack 推断完成。
4. **跨机网络未验**：RTT/MTU/NAT/真实丢包分布未测；「不造成风暴」只在受控链路与真实 loopback
   字节链上成立，未做流量级放大评估。
5. **两级空闲超时的先后关系未单独验收**：传输层 `IdleTimeoutMs=10000` 现在会早于
   `PMUdpSessionEndpoint.DefaultSessionIdleTimeoutMs=30000` 触发（前提是对端真的不响应）。
   两者收敛方向一致（双方同时超时），但该交互未做独立用例。
6. **未激活连接不被桥驱动**，因此其 `PMTransport.Update` 永不执行 ⇒ 初始静默期看门狗对它不适用；
   该窗口仍由宿主/端点的握手冷却与票据账本负责（R3-B 既有边界，本轮未改）。
7. 工具类程序（`PMNetGen` / `PMDsProbe` 需参数、`PMServerSmokeTest` 需先起服务端）按各自用法
   不可单独作为门禁运行，因此未列入 §3 表格：它们的「失败」是用法/环境所致，与本改动无关。
8. 故障注入为**内存字节改写 + `os.replace` 原子替换**（非线格式级注入），且注入后曾出现一次测试
   进程以未捕获异常退出（已通过给 J3 增加 `Nth()` 空安全访问修正为干净 FAIL）；三组注入的最终态均已
   核 sha256 一致。
9. 未运行 Unity、未做任何 git/svn 写操作、未提交。

---

## 6. 第 2 轮：纯 ack 追赶修复（ackloop）+ 门禁收口 + 最终数

> 本轮写边界：`Client/Assets/Scripts/PMNet/Transport/PMTransport.cs`、`Tools/PMTransportTest/Program.cs`、
> `Tools/PMUdpAdmissionTest/Program.cs`、本文件。**未改** `PMTransportTypes.cs`、`PMNetSessionTest`、
> 协调器 / Unity / PMR3 / 旧业务 / 主计划 / R3 契约；未执行 git/svn 写操作、未编译 Unity。
> 源文件编码：本轮修改过的源码全部为 **UTF-8 BOM + CRLF**；`PMUdpAdmissionTest/Program.cs` 原本是
> LF / 无 BOM，本轮按同一约定转为 BOM+CRLF（正文逐字节不变，仅 BOM 与行尾）。

### 6.1 修什么（§5.2 自己登记的缺口）

ack 是搭车在数据报上的，而原来的 `_ackDirty` 对**任何**新入站数据报都置位 ⇒ 一端回了 ack，
对端又把「收到这个 ack」当成需要回 ack 的事，两端**每帧各发一个 18 字节 header-only 纯 ack**，
永远停不下来（实测 12s 静默：每端 `tx=1100`；注入证据见 §6.4）。它同时把 keepalive 干扰掉：
因为每帧都在发，`KeepAlivesSent` 在长静默里只剩 1。

修法（完全按委派冻结的语义，不新增定时器 / 不新增 RTO / 不改线格式）：

- `header-only`（`messageCount == 0`）的纯 ack 数据报**仍然**：
  ① 消费对端的 ack 信息（`ProcessIncomingAcks`，含位图语义与 `RetransmitOlderThan` 推断）；
  ② 跟踪包号（`_recvWindowMax` / `_recvMask`）并因此**仍能检测缺口发 NAK**。
  唯一被抑制的是「本端再产生一个纯 ack 数据报」（不置 `_ackDirty`）。
- 携带消息的数据报（可靠/不可靠业务、分片，以及控制 **Ping** 与控制 **NAK**）**照旧置 `_ackDirty`** ⇒
  一个 ping 至多换来一个 ack，ack 通路本身没有被关掉（否则会变成无法反证的假绿）。
- 因此 `Update(now, 0)` 依旧连 keepalive 与纯 ack 都不发（预算 0 = 只 drain）；
  `MaxDatagramsPerUpdate` 旧窗口语义未动（传输层单次上限 32）。

实现落点（`PMTransport.cs`，核心 1 处）：
`ProcessDatagram` 把 `messageCount > 0` 作为 `carriesMessages` 传给
`TrackIncomingPacketId(packetId, carriesMessages)`，后者用 `bool wantAck = carriesMessages;`
统一门控函数内的 **4 处** `_ackDirty = true`（首包基线、重复包补位、更新包、窗口内回补位）。
包号跟踪、缺口 NAK、入站 ack 消费全部保持无条件执行。

### 6.2 改动清单（本轮）

| # | 文件 | 改动 |
|---|---|---|
| G-1 | `Client/Assets/Scripts/PMNet/Transport/PMTransport.cs` | `TrackIncomingPacketId` 增参 `carriesMessages` + `wantAck` 门控 4 处 `_ackDirty`；调用点传 `messageCount > 0`；注释写明「消费/跟踪不变、只抑制回 ack」。**未加 try/catch、未加定时器、未改线格式、未改配置字段**。 |
| G-2 | `Tools/PMTransportTest/Program.cs` | `J1d`（双端空闲 12s 数据报总量有界 + 可归因到 ping/ack 对）、`J7`（keepalive 关闭且一次可靠消息完整确认后反复 Update 不再产生数据报）、`J8`（header-only 纯 ack 被丢 ⇒ 后来针对它的 NAK 不得误推可靠重传，保序+恰好一次）、`J9`（预算 0 不偷发 keepalive/纯 ack；旧窗口 32 不退化）；`J1` 每帧发送量上限由 100 收紧到 20；`DeliverTo` 手工单方向投递助手（`DropNext` 无法表达「丢 A→B 第 2 个、保住第 1/3 个」）。 |
| G-3 | `Tools/PMUdpAdmissionTest/Program.cs` | ① **[I] ④** 收口畸形数据报 4 条期望（见 §6.3）+ 新增「同一连接补一个完整合法数据报后仍能进展」对照（5 条）；② **[E] ②** 正例对照收口：header-only 纯 ack 按新口径**不再**回 ack，改用携带消息的控制 Ping 证明 ack 通路仍在（+4 条）；新增 `CraftTransportPing`。 |
| G-4 | 不动契约 / 主计划 | `MaxDatagramBytes=1200`、`MaxFragmentsPerMessage=64`、32/帧预算、`Update(now,budget)` 语义、`PMUdpSessionEndpoint` 形参**一个都没动**；`net-architecture-migration.md` 未改。 |

### 6.3 最终数（口径：先 `build` 退出 0，再 `run`；均为实跑退出码）

| 门禁 | build | 第 1 轮 | **第 2 轮（本轮最终）** | run 退出码 |
|---|---|---|---|---|
| `Tools/PMTransportTest` | 0 警告 0 错误 | 135 / 0 | **173 项 / 0 失败**（J 组 47 → 85） | **0** |
| `Tools/PMUdpAdmissionTest` | 0 警告 0 错误 | 345 / 4 | **360 项 / 0 失败**（[I] 的 4 条由红转绿 + [E] +4 / [I] +7 = 新增 11 条；总检查数 349 → 360） | **0** |
| `Tools/PMNetSessionTest` | 0 警告 0 错误 | 405 / 0 | **402 项 / 3 失败**（§6.5 的 H 耦合） | **1** |
| `Tools/PMR3IntegrationTest` | 0 警告 0 错误 | 143 / 0 | **143 项 / 0 失败** | **0** |

关键实测量（本轮最终态）：

- **双端空闲 12s（keepalive=1000 / idle=10000）数据报总量 = 44**（A 22 + B 22 = 11 ping + 11 ack/端），
  修复前同一探针是 **2200**（每端 1100 ≈ 每帧 1 个纯 ack）。J1d 还断言了
  `tx ≤ kaTx + kaRx`（每个 ping 至多换回一个 ack ⇒ 无额外纯 ack 流）。
- **keepalive 关闭 + 一次可靠消息完整确认后**：再泵 100 帧，两端 `DatagramsSent` / `DatagramsReceived`
  **完全不变**（修复前 A=102、B=102、入站 101/102）。
- **纯 ack 被丢 ⇒ 后来 NAK 不得误推可靠重传**（J8）：被丢的确实是 18 字节 header-only 纯 ack，
  B 确实为它发了 NAK（`NaksSent=1`）、A 确实收到（`NaksReceived=1`），而 A 的
  `ReliableResent` **仍为 0**；m1/m2 保序且恰好一次。
- **畸形数据报收口后的形态**：不抛、`核心 ParseErrors=1`、`TransportUpdateFaults=0`、`ProtocolErrors=0`、
  `MessagesReceived=0`（不误执行）、连接仍 `IsReady`、世界侧 `ConnectionCount=1`；
  紧接着补一条完整合法数据报 ⇒ 入站计数 +1、无解析错误、仍就绪（**还能进展**）。

### 6.4 负向注入（单点，原字节备份 → 注入 → `finally` 还原 → 核 sha256）

唯一注入：把 `bool wantAck = carriesMessages;` 改回 `bool wantAck = true;`
（= 恢复「对纯 ack 也回 ack」的第 1 轮行为），其余字节不动。

| 门禁 | 注入后 | 直接证据 |
|---|---|---|
| `PMTransportTest` | **161 / 12**（退出 1） | J1d：`< 80` 实测 **2200**；`< 600` 失败；`tx=1100` vs `kaTx=1/kaRx=1` 不可归因。J1：50 帧 **100** 个数据报。J7：A/B 各 **102**（期望 2）、入站 101/102。J8 前置：A 为 m1 发了 **2** 个数据报（第 2 个就是回 ack）。**12 条全部命中本轮新增/收紧的断言**。 |
| `PMUdpAdmissionTest` | **359 / 1**（退出 1） | 唯一失败即 §I ② 新增的「header-only 纯 ack 不再触发回程 ack」（实测 1，期望 0）。 |
| `PMNetSessionTest` | **405 / 0**（退出 0） | 反证 §6.5：**旧行为下 §H 才绿**（它的恢复靠 ack 流）。 |

还原后 `PMTransport.cs` sha256 = `5bd8280e2c63023b4c5a144b6d7bcff5dd8799336a9e6735343e8572029cc5e5`，已验证一致。

第 2 轮最终态固定哈希（供对账；均为 UTF-8 BOM + CRLF）：

```
Client/Assets/Scripts/PMNet/Transport/PMTransport.cs  5bd8280e2c63023b4c5a144b6d7bcff5dd8799336a9e6735343e8572029cc5e5
Tools/PMTransportTest/Program.cs                     f566a49f2e374f1f133f9f4f2fadac91ceed7798e0a735612170a663fabdae12（新增 J1d/J7/J8/J9 + DeliverTo）
Tools/PMUdpAdmissionTest/Program.cs                  a86f392eb60906d1d7d151334e5f1ee216a6b6c95379dcff2772df1ce1fdfbd9（收口；由 LF/无 BOM 转为 BOM+CRLF）
Docs/plans/_r3b_transport_liveness.md                （本文件自引用，不列哈希；其余四个哈希为“写入前对比值”口径）
Tools/PMNetSessionTest/Program.cs                    4677373a7173c4299bfb3ba543155921a70f4921de6044dd3185d1bde0ca669a  ← **未改**（与第 1 轮一致）
```

### 6.5 仍未解除的耦合：`PMNetSessionTest §H` 第 ② 段（**不在本轮写边界内**）

现象：`Tools/PMNetSessionTest` 由 **405/0 变为 402/3**（退出码 1），三条失败全在
`H. 丢包重排` 的第 ② 段（`TestLossAndConvergence`，epoch 11001）：
`可靠 RPC 恰好交付一次（实际 0）` / `可靠 RPC 参数正确（实际 0/期望 777）` /
`链路确实重复投递过数据报（实际 0）`。

根因（源码级 + 逐包 trace 实证）：A–L 组的连接由 `AddConnectionTo` 显式
`KeepAliveIntervalMs = 0L; IdleTimeoutMs = 0L;`（第 1 轮 F-8 为统计绝对报文数而设）。该段流程是：
`DropNextFrom("client", 1)` + `DuplicateNextFrom("client", 1)` → `scene.Frame(30)`（= 480ms）→ 立即断言。
在实际执行里：

- `DropNextFrom("client", 1)` 吃掉的是**客户端承载 RPC 的那个数据报**（修复前后都一样）。
- 修复**前**：客户端有一条**永不停歇的 18 字节纯 ack 流**（§6.1 的现象），于是紧接着就有下一个
  客户端数据报被 `DuplicateNextFrom` 重复投递（`Duplicated=1`），并且它把服务端 `_recvWindowMax`
  推过 RPC 的包号 ⇒ 服务端看见缺口 ⇒ NAK ⇒ 客户端重传 ⇒ 在 `Frame(30)` 内就补齐。
- 修复**后**：该段无任何业务、无 keepalive、客户端入站也没有「携带消息」的数据报 ⇒
  客户端在整段里**只发了那一个被丢掉的 RPC 数据报**，随后彻底安静 480ms。
  既无 NAK（接收端看不到缺口）也无 ack 水位（发送端拿不到推断依据）⇒ 无法重传，
  三条断言如实变红。RPC 最终是在**第 ③ 段**（`actor.SetHealth/SetArmor` 之后服务端重启复制流量、
  客户端开始回 ack）才被补上的——已经晚于断言点。

直白口径：**这段用例的活性本来就是 ack 流提供的**；它在修复前能绿，一部分是因为丢包与重复
落在了纯 ack 上，`Duplicated` 与恢复都是被 ack 流顺带完成的。删掉 ack 流后，这条用例第一次
真正命中 §2.1 / J2a 已登记的既有缺口——「既无 NAK 也无 ack 水位 ⇒ 无法重传」（keepalive 关闭时）。

**已验证的修法（未施加，因越出本轮写边界）**：给该 `Scenario` 打开 keepalive，即

```csharp
scene.TuneTransportConfig = delegate (PMTransportConfig c)
{
    c.KeepAliveIntervalMs = 1000L;
    c.IdleTimeoutMs = 10000L;
};
```

临时施加（字节级备份 → 改 → 跑 → `finally` 还原）实测：`PMNetSessionTest` **405/0、退出码 0**，
`[H] 32 项通过 / 0 项失败`；还原后文件 sha256 = `4677373a7173c4299bfb3ba543155921a70f4921de6044dd3185d1bde0ca669a`，
与原值一致。这与第 1 轮 M 组（`quiet`/`heal`）用 `TuneTransportConfig` 打开 keepalive 是同一手法。

**不在传输层里「为了这条用例」加东西的理由**：要让本节形态可恢复，只可能是
（a）重新制造永不收敛的 ack 流（与本轮冻结语义直接矛盾），或
（b）新增「有未确认可靠数据就周期性探测/重传」的定时行为 = 新 RTO——§2.1 已明确把它列为
**需要先在契约开口子**的新增传输层行为，本轮不做。因此按 R3 纪律**上报而不是悄悄修**。

### 6.6 本轮未验 / 诚实边界

1. **§6.5 是本轮唯一未达成项**：它使「保序丢包用例仍全绿」只在 `PMTransportTest`（A/B/J8 等）
   与加 keepalive 后的 Session 成立；`PMNetSessionTest` 的 `§H ②`（keepalive 关闭的连续丢包段）
   仍红，退出码 **1**。修法与证据已给出，但改动落在本轮写边界之外的文件上。
2. **未新增 RTO**：见 §2.1 与 §6.5；`keepalive 关闭 + 首个（且唯一）可靠数据报丢失 + 再无任何流量`
   这一形态仍然不可恢复，属既有登记缺口，本轮未扩大传输层行为。
3. **T42 仍 `PENDING_USER`**：没有真实 Unity DS 进程 / 权威场景 / 两客户端闭环；本轮全部为
   纯 C# 门禁 + 受控链路与真实 loopback 字节链。
4. **跨机网络未验**：RTT/MTU/NAT/真实丢包分布未测；「不造成放大」只在受控链路成立。
5. 未运行 Unity、未做任何 git/svn 写操作、未提交；未改主计划与 R3 契约。
