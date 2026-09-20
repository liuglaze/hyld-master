# R3-A 控制编排/票据/进程协调 —— 独立对抗审查报告

> 范围：`Client/Assets/Scripts/PMNet/Control/PMDsControlProtocol.cs`（2163 行）、`PMDsTicket.cs`（1075 行）、
> `Server/DS/PMDsCoordinator.cs`（1887 行）、`Server/DS/PMDsProcess.cs`（798 行）、
> `Tools/PMDsControlTest/Program.cs`（2429 行）中与上述四文件直接相关的函数。
> 契约：`Docs/plans/net-r3-control-contract.md`（冻结）。
> 边界：只读；不编译、不改实现、不递归委派。唯一写入例外即本文件。
> 立场：**不采信 `_r3a_control_report.md` 的「380 项全绿」作为正确性证明**。下文所有结论来自逐行读源码 + 可执行的最小复现设计。
> 主计划文末 R3A 表（`net-architecture-migration.md:1979-1983`）中 R3A1/R3A2/R3A3/R3A4/R3A5 仍为 **PENDING**；
> `_r3a_control_report.md` 声称 R3A1/R3A2 PASS，与其自身「阶段状态以主计划为准」的声明并存，不构成矛盾，但**任何「R3-A 已通过」的表述都应回到主计划**。

---

## 0. 必读范围（实际读取，按任务指定顺序）

| 顺序 | 文档 | 本次审查取用的直接约束 |
|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 指向客户端/服务端/迁移计划三个入口 |
| 2 | `Client/Assets/AGENTS.md` | Unity 2019.4 语言面、协议「只改权威源」、`.meta` 必须随资源提交 |
| 3 | `Server/AGENTS.md` | `net8.0`、无数据库、`Server/**` 隐式 compile、`Server/PMNet` 以链接共享同一份源码 |
| 4 | `Docs/plans/net-r3-control-contract.md`（全篇） | §2 身份/票据、§3 消息清单+状态链+默认超时、§5 门禁口径 |
| 5 | `net-architecture-migration.md` 文末 R3 分步计划 + R3A 表（1969-1986） | R3A1..R3A5/R3B1 全部 PENDING；「A 阶段不得当作整个 R3 完成」 |
| 6 | `_r3a_control_report.md`（全篇） | 交付物清单、字段口径、自述未闭合项（第 9 节）、测试分节 |

补充：为判定「MAC 覆盖原始字节」是否真的成立，沿直接依赖读了 `Client/Assets/Scripts/PMNet/PMNetReader.cs`
的 `Position`（第 52 行，语义为「相对整个缓冲区的绝对位置」）与 `ReadSubReader`（第 349 行）。
结论：`PMDsControlCodec.DecodeCore` 记的 `MacFieldOffset = tagStart - offset` 与 `RawPayload`（从 0 起的切片副本）
口径一致，`PMDsControlSigner.Verify` 用 `HMAC(RawPayload[0..MacFieldOffset))` 计算。**A1 报告 §5.2 的「不重新编码、
不存在规范化绕过面」这一条成立**；同时 `PMDsControlCodec.DecodeCore` 强制「MAC 字段之后不得有尾部字节」+
信封字段号严格递增（`field == lastField` 也抛）→ MAC 字段位置唯一，不构成绕过面。**此项未发现缺陷。**

---

## 1. 结论摘要

- **没有发现「密钥/票据可被无密钥方伪造」「MAC 可绕过」「名册可绕过」这类结构性漏洞。** 控制帧与票据的
  密码学路径（HMAC-SHA256 + 常量时间比对 + 明文长度前缀 + 严格字段号 + MAC 必须最后 + 分配前长度预检）
  在我读到的范围内是自洽的；`PMDsControlSigner` 不接受对端提供的密钥，`ResolveTicket` 只在 `IsValid` 后使用身份。
- **发现 4 项必须修**，其中 2 项是真实资源/状态缺陷（端口过早释放、Exited/Error 无状态校验且 Exited 零测试），
  1 项是可复现的判定精度缺陷（同 ID 不同内容只比 32 位摘要），1 项是口径缺陷（`ResultAcked` 按己方发送次数
  宣布对方已确认）。
- 「380 项绿」只能证明**它自己列出的那些断言**成立；本次指出的一条（`HandlePeerExited`）在整套 380 项里
  **没有任何调用点**，即门禁存在整条分支的覆盖空洞。另有若干断言之所以通过，是因为**替身的即时性**掩盖了
  真实进程的异步性（见 M1）。
- A 基础层与 R3-B 未接线的区分：端点消费账本/防重放、真实 Unity DS、客户端切服、`Activate` 前票据校验、
  bootstrap 文件原子发布与权限，均属 R3-B，**本报告不判为 bug**（见 §3 S1/S2 的说明）。

---

## 2. 必须修的 4 项

### M1（真实缺陷）「失败/强杀 → 终态」会在进程真正退出之前把端口还给共享池

**证据**
- `Server/DS/PMDsCoordinator.cs:1703-1747` `Finalize()`：`_state = terminalState;`
  → `if (_process != null && _process.IsRunning) KillIfRunning("终态清理");`（1731-1734）
  → `ReleaseResources();`（1736，函数体 1703-1746）。**Kill 与释放端口之间没有任何「确认已退出」的等待。**
- `Server/DS/PMDsCoordinator.cs:1749-1794` `ReleaseResources()`：`_ports.Release(port)` → `_port = 0`
  → `_process.Dispose()`（不等待、也不查询退出码）。
- `Server/DS/PMDsCoordinator.cs:1796-1825` `KillIfRunning()`：**幂等门 `_killRequested` 一旦置位永不复位**，
  `_process.Kill()` 的异常被 `catch (Exception) {}` 吞掉，因此「第一次 Kill 失败」之后**既不会重试也不会降级**。
- `Server/DS/PMDsProcess.cs:679-698` `PMDsSystemProcess.Kill()`：`_process.Kill(true)`（树杀）**不调用
  `WaitForExit`**；`Kill` 的语义是「请求终止」，进程可能仍在运行并仍持有监听端口。
- 同一个过早释放也出现在 `Tick()` 的启动超时路径：`PMDsCoordinator.cs:1084-1090`
  （`KillIfRunning(...)` 紧接 `Finalize(TimedOut, ...)`，Finalize 内部再 ReleaseResources）。
- 端口池是跨局共享资源（`PMDsCoordinator.cs:794` 构造参数 `sharedPortPool`，报告 §5.3 与契约要求 Lobby 级唯一），
  分配是「从小到大确定性取最小空闲」（`PMDsCoordinator.cs:670-684 TryAcquire`，池类 645-693），所以**刚被释放的端口正是最可能被
  下一局立刻重新分配的那个**。

**最小复现（可确定性执行，无需真进程）**
在 `Tools/PMDsControlTest/Program.cs` 的 G7 用例（约 1518-1550）里，把替身设成「Kill 不会让进程退出」：
```csharp
launcher.Process.KillExitsProcess = false;   // FakeProcess.Kill() 只累加 KillCount，不 Exit()
clock.Advance(30000);
PMDsCoordinatorReply r = coordinator.Tick();     // 触发启动超时 → KillIfRunning → Finalize(TimedOut)
Debug.Assert(coordinator.State  == PMDsSessionState.TimedOut);
Debug.Assert(coordinator.PortsInUse == 0);       // 端口已归还共享池
Debug.Assert(launcher.Process.IsRunning);        // 但进程仍在运行
```
现有 G7 之所以「通过」，是因为 `FakeProcess.KillExitsProcess` 默认为 `true`（`Program.cs:270`），
`Kill()` 同步把 `_running=false`（`Program.cs:329-336`），于是 **替身的即时退出掩盖了真实适配器的异步退出**。
G33-G36 只覆盖「宽限期内 Kill 失败 → 继续等」，没有覆盖「终态转移时 Kill 成功但未退出就归还端口」。

**影响**
- 新一局可能从共享池拿到同一端口，而旧进程尚未消失 → 新 DS `bind` 失败 / 报告 `Ready.BoundPort` 冲突，
  会话被误判为 `Failed`；调用方从状态机与日志上看不出「端口被别人占着」。
- `_killRequested` 永不复位 + Kill 异常静默 ⇒ 强杀失败后**残留进程永久存在**，且协调器已进入终态、端口已释放，
  没有任何后续机制会再回收它。契约 §3「失败/崩溃必须可观测并回收资源」在「回收」这一半没有闭合。

**建议最小修法**
1. `Finalize`/`ReleaseResources` 分离：**端口归还排在「确认进程已退出」之后**；真实适配器提供有界等待
   （`Kill()` 后 `WaitForExit(graceMs)`），超时才归还端口（并可选把端口标记为 quarantine 一轮）。
2. `KillIfRunning` 拆成「请求」与「确认」两步，失败重试至少一次，并把失败写进 `PMDsCoordinatorEvent.Reason`
   （现在是完全静默）。
3. 端口归还时校验「本进程已退出」这个前置条件（由 `IPMDsProcess` 暴露 `TryGetExitCode`/有界等待）。

---

### M2（口径缺陷，非数据丢失）`ResultAcked` 由「己方发送次数用尽」触发，没有任何对端确认信号

**证据**
- `PMDsCoordinator.cs:1106-1125` `Tick()` 第 4 步：`_state == ResultPending` 且到达重试点后，
  若 `_ackRetransmits >= _options.MaxResultAckRetransmits`，则
  `_state = PMDsSessionState.ResultAcked; BeginShutdownAfterResult(now, "结果确认重发预算用尽");`
  → 唯一输入是**本进程的累计发送计数**。
- 状态枚举自述（`PMDsCoordinator.cs:57-58`）：`ResultAcked = 6 // 结果确认流程结束，正在收尾`。
  而协议消息清单里 DS→Lobby 只有 `Ready/Heartbeat/Result/Exited/Error`（`PMDsControlProtocol.cs:80-108`），
  **不存在任何「DS 已收到 ResultAck」的上行消息**。因此 `ResultAcked` 在严格语义上**不可由对端确认到达**，
  只能由放弃重传到达。`ResultAcksSent` 这个计数器名是诚实的，`ResultAcked` 这个状态名不是。
- `Tools/PMDsControlTest/Program.cs:1437-1451`（F47-F50）正是把这条件路径钉成「重发预算用尽 → ResultAcked」，
  整个过程**没有任何来自 DS 的输入**。所以「380 项绿」在这里证明的是「预算用尽会改状态」，不是「对方已确认」。

**最小复现**
就是 F47-F50 本身：用一个永不回任何消息的 DS 视图（`DsView` 只负责签名上行），
`clock.Advance(1000)` 三次 + 第四次 `Tick()` → `State == ResultAcked`。
不需要对端做任何事，状态就宣称「已确认」。

**影响（诚实口径）**
- **不是结算丢失**：结果在 `HandleResult` 接受时已通过 `ResultAccepted` 副作用并入业务（`PMDsCoordinator.cs:1411-1424`），
  且终态后 DS 重发的同一 Result 会被墓碑兜住并重发同一 Ack（`1342-1364`）。丢 Ack 不会让业务拿不到结果。
- **真实危害在可观测性**：`ResultAcked` 无法区分「对端确认了」与「我们放弃了」。任何据 `State == ResultAcked`
  做告警/对账/「结算成功」判定的上层逻辑都会过度声称；也无法触发「Ack 全丢 → 升级告警/重放结算」这类补偿。

**建议最小修法**
1. 拆成两个状态或在事件里带明确布尔（例如 `ResultAckConfirmed` vs `ResultAckAbandoned`），
   只有收到对端显式确认（或观察到 DS 已 `Exited` 且其结果已被墓碑覆盖）才置 `Confirmed`。
2. 若契约不接受新增消息，则**至少把内部/报告里的措辞改成「已放弃确认重传」**，
   并在主计划/报告的验收口径里删掉一切「结果确认完成」的等价表述。

---

### M3（可复现的判定精度缺陷）「同 ResultId 不同内容」只比 32 位摘要，且是全链唯一的判据

**证据**
- `PMDsCoordinator.cs:1834-1850` `ComputeResultContentHash(PMDsResultBody)`：把 `winnerTeamId`(4B) + `Summary`
  拼成缓冲后 `SHA256`，**只取前 4 字节**（`return (uint)(hash[0] | hash[1]<<8 | hash[2]<<16 | hash[3]<<24);`）。
- 比较点 1（非终态）：`PMDsCoordinator.cs:1369` `if (contentHash == _resultContentHash && body.WinnerTeamId == _winnerTeamId)`
  → 相等即判 `Duplicate` 并重发同一 Ack；不等才 `RejectedConflict`。
- 比较点 2（终态墓碑）：`PMDsCoordinator.cs:1347` `if (tombstone.ContentHash == contentHash && tombstone.WinnerTeamId == body.WinnerTeamId)`。
- 墓碑结构：`PMDsCoordinator.cs:426-441` `PMDsResultTombstone.ContentHash` 是 `uint`。
- 账本接口：`PMDsCoordinator.cs:531-553 Record(...)`，冲突判据同样是 `existing.ContentHash != entry.ContentHash || existing.WinnerTeamId != entry.WinnerTeamId`。
- **`PMDsResultBody.Summary` 的全字节从不参与比较**，只通过这 4 字节摘要间接参与。
- 报告第 9 节第 7 条已自述该风险并给出「可升级为 32 字节摘要」的选项——本次确认它不是理论问题而是**可构造的**。

**最小复现（两种，任选其一）**
- 直证接口精度（10 行，无需碰撞搜索）：`PMDsResultTombstoneLedger ledger = new PMDsResultTombstoneLedger(4, TimeSpan.FromMinutes(5));`
  用 `CreateEntry(id, winner, hashX, ack, now)` 写入，再用 **同一个 `hashX`** 但代表不同 `Summary` 的内容写第二次
  → `Record` 返回 `Updated`（幂等），而不是 `Conflict`。即账本自身**没有能力**区分内容，只能信调用方递进来的 32 位。
- 端到端（约 2^16 次哈希即可，代价极低）：生成随机 `Summary` 直到出现两条 SHA-256 前 4 字节相同的（生日界，期望 ~65k 次），
  两条都作为同一个 `ResultId`、同一 `winnerTeamId` 发上行 → 第二条被判 `Duplicate` 并回同一 Ack，
  **内容不同却静默吞掉、不报 `RejectedConflict`、业务也拿不到「第二次内容是什么」**。

**影响**
- 契约 §3「结果以 (MatchId,Epoch,ResultId) 幂等，**同 ID 不同内容拒绝**」在「不同内容」这一半的实际保证是
  「以 2^-32 的概率漏判」。对「结算结果」这类不可重算的权威数据，用可碰撞摘要代替内容比较没有必要，
  且摘要是在**同一局内**、最多几条记录之间比较，成本上完全不需要截断。

**建议最小修法**
最省事且零风险：把判据改成**完整字节比较**——`winnerTeamId` 相等 **且** `Summary` 逐字节相等
（`Summary` 上限 512 字节，`PMDsResultBody` 已在手，无需再算哈希）。若想保留「只存摘要」的墓碑形态，
则把 `ContentHash` 换成 32 字节 SHA-256（`byte[32]`）并做常量时间/顺序比较。

---

### M4（真实缺陷 + 覆盖空洞）`HandlePeerExited` / `HandlePeerError` 不做状态校验；`Exited` 整条分支零测试

**证据**
- `PMDsCoordinator.cs:1457-1468` `HandlePeerExited`：只校验 `body != null`，随后
  `_counters.FramesAccepted++;` 立刻 `ApplyProcessExit(body.ExitCode, "收到 Exited 消息")`。
  **没有** `_state` 判断（对比同文件的 `HandleReady` 1254-1266、`HandleHeartbeat` 1313-1319、
  `HandleResult` 1389-1392、`HandlePeerShutdown` 1436-1442，全部都有状态门）。
- `PMDsCoordinator.cs:1471-1486` `HandlePeerError`：同样无状态门，直接 `KillIfRunning` + `Finalize(Failed, ...)`。
- `PMDsCoordinator.cs:1632-1676` `ApplyProcessExit`：状态 `Allocated`（以及任何未列出的状态）落到 `default`
  → `Finalize(PMDsSessionState.Failed, "进程在 Allocated 状态退出…")`；`_process == null` 时 `_processId` 置 0。
  `Finalize` 随后 `ReleaseResources()` → 端口归还、名册清空、票据 `RevokeAll`。
- **测试缺口**：`Tools/PMDsControlTest/Program.cs:446-449` 定义了 `DsView.Exited(int exitCode)`，
  但**全文件没有任何调用点**（`grep -n "Exited"` 只命中定义、`RoundTrip` 的 B6 编解码用例、
  以及 `OnProcessExited` 宿主 API 和状态字符串）。也就是说 380 项里 **`HandlePeerExited` 一次都没被执行过**。

**最小复现**
```csharp
PMDsCoordinator c = NewCoordinator(clock, launcher, sink, MakeOptions(),
        MakeRequest("m", "d", 1u, 0xAABBCCDDu, 1u, DefaultRoster));   // Allocate 后 state == Allocated
DsView ds = DsView.FromCoordinator(c);                                 // 从引导文件拿到本局密钥（真实 DS 取密钥路径）
byte[] exited = ds.Exited(0);                                          // 合法身份 + 合法 MAC
c.OnControlPayload(exited, 0, exited.Length);
Debug.Assert(c.State == PMDsSessionState.Failed);   // 未启动进程却已终态
Debug.Assert(c.PortsInUse == 0);                    // 端口已归还
Debug.Assert(c.BeginStart().Outcome == PMDsCoordinatorOutcome.RejectedState); // 会话已被"宣告死亡"
```
同理可对 `Allocated`/`Starting` 发 `Error`，得到同一后果。

**影响与定级（诚实）**
- 可利用性**低**：能通过 `Dispatch` 身份校验（MatchId/DsId/Epoch/ProtocolHash 全等）且 MAC 正确的，只有持有本局密钥的
  Lobby 自身或**同一局的 DS**；`Allocated` 阶段 DS 尚未启动。因此这不是「外部攻击者能打」的洞，
  而是**状态机不封闭**：契约 §3 的状态链在 `Exited`/`Error` 两条入边上没有守卫，
  一个迟到/重复/乱序的自签名 `Exited` 就能把未启动的会话判死并归还端口（若 Lobby 侧存在重发逻辑即会命中）。
- 真正的严重性在于**它同时暴露了门禁的盲区**：一条完整入站分支从未被执行，却被计入「380 项全绿」。

**建议最小修法**
1. `HandlePeerExited`：加状态门——只接受 `Starting/Ready/Running/ResultPending/ResultAcked`
   （或明确要求 `_process != null`），其余返回 `RejectedState` 且不触碰状态与计数器。
2. `HandlePeerError`：同门口径（终态应返回 `Duplicate` 而不是 `Finalize` 内部再兜一次）。
3. `FramesAccepted++` 移到**确认真正生效之后**（现在 M4 与 S5 同源：终态 no-op 也计成「已接受」）。
4. 门禁补 `HandlePeerExited` 的正/负用例（至少：Starting 阶段 Exited → Failed；Allocated 阶段 Exited → RejectedState）。

---

## 3. 疑似（明确标注为推断，未证实为缺陷）

**S1（推断）票据校验结果不暴露 Nonce 与票据指纹，R3-B 的「端点消费账本」缺少直接句柄。**
契约 §2 要求「票据重发只对同一已绑定会话幂等；禁止第二来源端点拿同票据顶替」，R3-B 要做「票据→真实端点」账本；
`PMDsTicket` 内部持有 `Nonce`（`PMDsTicket.cs:308` 自述「重连重发的依据之一，由 R3-B 的端点账本使用」），
但对外返回类型 `PMDsTicketVerification`（`PMDsTicket.cs:141-215`）**只有 Verdict/Identity/MatchId/DsId/Epoch/ProtocolHash/签发到期时间**，
没有 Nonce、也没有票据指纹；`PMDsTicketCodec` 是 `internal`（`PMDsTicket.cs:587`），R3-B 无法从原始票据字节取出 Nonce。
推断：R3-B 只能用「原始票据字节的哈希」当账本键。**这是接口缺口而非实现错误**（可绕过），
但报告 §5.4 声称「MAC 验证不等于防重放、账本属 R3-B」时未指出「Nonce 当前拿不到」，会让 R3-B 以为有现成句柄。

**S2（推断）多局 uid 占用没有跨会话守卫，而端口有。**
`_uidToRosterIndex` 是**实例字段**（`PMDsCoordinator.cs:954` 写入，分配时只做**本局内** Uid/PlayerId 唯一性校验
`ValidateIdentities`，`PMDsControlProtocol.cs:1477-1530`）。A1 为端口专门提供了跨局共享资源 `PMDsPortPool`
（`PMDsCoordinator.cs:645-693`），并用测试 G50「各自 new 池 → 两会话拿到同一端口」把「必须共享」钉住
（报告 §5.3）；**但没有对应的共享 uid 名册**。因此同一个 `Uid` 可以同时出现在两个会话的名册里，两个 DS 都会
`ResolveTicket` 通过。契约 §3「终态释放**玩家**与端口占用」暗示 Lobby 需要持有玩家占用账本。
推断：这属于匹配层（R3-B）职责，A1 不接匹配入口，故**不判为 A1 的 bug**；但报告把「端口池必须共享」写进
「必须让 R3-B 知道的三处前提」，却没有对称地写「uid 占用也必须共享」，属交接文档的不对称，建议补齐。

**S3（推断）`PMDsControlFrameDecoder` 的「有界」只在调用方分块喂入时成立。**
`PMDsControlProtocol.cs:2134-2160 EnsureCapacity`：当 `required > _maxBufferedBytes` 时走 `needed = required` 分支，
把缓冲扩到本次喂入大小；`Append`（`1998-2025`）在 `Drain()` 之前就更新 `PeakPendingBytes`（`2050-2060` 区间），
所以单次超大 `Append` 会瞬时占用与喂入等量的内存，类自述「只保留一个未完成帧的字节（≤ 4 + 64KiB）」
（`1941`）在该路径下不成立。测试 A29/A30（`Program.cs:648-665`）用 `chunk = 4096` 分块喂入，因此必然通过。
真实 R3-B 若用 `Socket.Receive(64KB 缓冲)` 则 `required ≤ 65536 < _maxBufferedBytes(131080)`，实际不触发；
**这是「有界性依赖调用方纪律」的未声明前提**，不是可利用漏洞（对端无法强迫服务端一次喂入超大块）。

**S4（推断）墓碑账本以 `ResultId` 为键，与契约写的 (MatchId,Epoch,ResultId) 字面不符，但实例内等价。**
`PMDsCoordinator.cs:1345`（`TryGet(body.ResultId, ...)`）与 `PMDsResultTombstoneLedger` 的
`Dictionary<ulong, PMDsResultTombstone>`（`478`）。因为账本是**协调器实例级**（构造于 `795`，随会话创建），
实例内 MatchId/Epoch 恒定 ⇒ 键等价。仅报告/契约措辞与实现不对齐（报告 §1 与 `PMDsCoordinator` 类注释都写三元组）。

**S5（推断）计数器把「未生效的帧」计成已接受。**
`HandlePeerExited`（`1457-1468`）与 `HandlePeerError`（`1471-1486`）先 `_counters.FramesAccepted++`，再进入可能
no-op（终态 `ApplyProcessExit` 直接 return）或 `Finalize` 返回 `Duplicate` 的路径；`Finalize` 在已终态时返回
`Duplicate`（`1705-1708`），但 `FramesAccepted` 已经加过。属观测口径缺陷，与 M4 同源，修 M4 时一并处理。

**S6（推断）票据上限常量有三处不一致（fail-closed，无安全影响）。**
`PMDsControlWire.MaxTicketBytes = 512`（`PMDsControlProtocol.cs:58`）vs `PMDsTicketWire.MaxTicketBytes = 352`
（`PMDsTicket.cs:37`）vs 注释「真实长度 ≤ 348」（`PMDsControlProtocol.cs:57`）。控制层允许 512 字节的票据字段，
票据层按 352 拒绝 → 只会更严，不会更松。

**S7（推断）真实 DS 大概率拿不到「验收所需」的票据时效窗口，属 R3-B 设计约束而非缺陷。**
`PMDsTicketIssuer` 的默认有效期 120s 且**重发不刷新**（`PMDsTicket.cs:447-470`，`IsExpiredAt` 排他）；
`ExportBootstrapDocument`（`PMDsCoordinator.cs:1491-1523`）在调用时刻签发。若 R3-B 在 Lobby 分配时就写引导文件、
而玩家在 120s 之后才首次连 DS，则票已过期——契约本意是「重连由 Lobby 显式重发新票」，故成立；
但这也意味着**DS 不能长期缓存引导文件里的票据当会话凭据**。建议在 R3-B 接线文档里写明。

---

## 4. 无法确定（缺少证据）

1. **Unity 2019.4 的 .NET Standard 2.0 profile 是否提供 `System.Security.Cryptography.HMACSHA256`。**
   报告 §9.1 自认这是最大未验证假设。本环境无 Unity、不改实现、不编译，**无法验证**。
   现有间接证据仅：`PMNetLangCheck`（netstandard2.0 + C#7.3）编过、Unity 工程内他处用了 `System.Security.Cryptography`。
2. **真实 HyldDS 进程的 Kill→退出→端口真正释放时序**。M1 的窗口大小（毫秒级还是百毫秒级）无法量化：
   本次是只读审查，没有真机进程与端口占用观测。
3. **`Process.Kill(true)`（树杀）在 R3-B 目标进程树上的失败率与语义**（是否有子进程拒绝被树杀、是否需要 Job 对象）。
4. **`_r3a_control_report.md` 列出的 380 项断言我未逐项复核**（只抽查了 A/B/C 的抽样点与 F/G/H/I 中与本报告结论相关的断言，
   并横向 grep 了 `Exited`/`ResultAck`/`Tombstone`/`PortsInUse` 等关键字的全部调用点）。因此报告里**未被本次复核的项，
   不能算作「已确认通过」**，也不能算作「有问题」。
5. **跨程序集/跨进程的端口唯一性与「恰好一次」**：契约与报告都声明 Lobby 重启即丢墓碑（无数据库），
   我没有条件验证多进程同时分配的行为。

---

## 5. 已检查范围

**文档（全读）**：`AGENTS.md`、`Client/Assets/AGENTS.md`、`Server/AGENTS.md`、
`Docs/plans/net-r3-control-contract.md`、`net-architecture-migration.md` 的 1955-1986 段（R3 分步 + R3A 表）、
`Docs/plans/_r3a_control_report.md`。

**源码（逐行读）**
- `Client/Assets/Scripts/PMNet/Control/PMDsControlProtocol.cs`：全文 2163 行（DTO 1-380、codec 380-1500、
  加密原语 1500-1700、签名/验签 1700-1830、分帧 1830-1930、流式解码器 1930-2163）。
- `Client/Assets/Scripts/PMNet/Control/PMDsTicket.cs`：全文 1075 行（票据布局/签发/校验/引导文件编解码）。
- `Server/DS/PMDsCoordinator.cs`：全文 1887 行（状态枚举、options、端口池、墓碑账本、状态机、入站分发、
  Finalize/Release/Kill/内容摘要）。
- `Server/DS/PMDsProcess.cs`：全文 798 行（fault 枚举、启动请求、白名单、结构校验、有界环形缓冲、
  真实适配器 `PMDsSystemProcess`）。
- `Client/Assets/Scripts/PMNet/PMNetReader.cs`：只读 `Position`(52)、构造函数(22-40)、`ReadSubReader`(349-362)——
  仅为判定 MAC 覆盖口径，未扩展到其它 PMNet 文件。
- `Tools/PMDsControlTest/Program.cs`：A 分帧有界性（548-680）、F 协调器正常路径（约 1330-1500）、
  G 故障注入（1500-1830 的 G1/G7/G12/G15/G26/G29/G33/G39/G42/G45-G65）、H 拒绝面+票据（1900-2050）、
  I 真实 loopback TCP（2050-2150）、替身 `FakeProcess`/`FakeLauncher`/`DsView`（260-470）。
- 交叉验证用 grep（限定上述文件）：`Exited`、`ResultAcked|ResultAck`、`Tombstone|ContentHash`、
  `PortsInUse|ReleaseResources|KillRequested`、`Uid|uid`。

**未扩展到**（按硬边界停止）：`PMNet/Session/**`（R3-A2/A3/A4 的 `PMTransportConnection`/`PMNetSessionBridge`）、
生成器与 R2 产物、`PMDsHost`、`BattleManage`/匹配入口、UE/Unity 资产、任何编译与运行。

---

## 6. 建议下一步（最小补充查询 / 运行时验证）

1. **M1（最高优先）**：在共享端口池上做一次「真进程」验证 —— 用 `PMDsSystemProcessLauncher` 启动一个真实
   子进程占住端口（测试自身即可，参考 J 节做法），走到 `Finalize` 后立刻尝试用新协调器分配同一端口并 `bind`，
   观察是否 `EADDRINUSE`；同时记录 `Kill()` 到 `HasExited==true` 的实际耗时，作为「是否必须有界等待」的定量依据。
2. **M3**：写一个 30 行的一次性脚本，生成随机 `Summary` 直到出现 SHA-256 前 4 字节碰撞（期望 ~65k 次，
   亚秒级），把这一对作为同 `ResultId` 的两条 `Result` 发进协调器，确认第二条被判 `Duplicate`。
   该项一旦复现，**修法改判据为完整比较是无成本的选择**。
3. **M4**：门禁补 `HandlePeerExited` 的两个用例（`Starting` 阶段 → `Failed`；`Allocated` 阶段 → `RejectedState`），
   并同步把 `FramesAccepted` 移到生效之后。
4. **M2（口径）**：在主计划/报告里把 `ResultAcked` 的语义改成「ResultAck 重传预算用尽」，
   或在 `PMDsCoordinatorEvent` 上加一个明确的 `PeerConfirmed` 布尔，避免上层据状态名误判。
5. **S1/S2（交接补文档）**：在 `net-r3-control-contract.md` §2/§3 或 R3-B 接线文档里补两条 R3-A 实际交付的边界：
   「Nonce 当前不出现在 `PMDsTicketVerification`，端点账本需自行以票据字节为键」与
   「uid 占用无跨会话守卫，需与端口池同级的共享账本」。
6. 若要接受「R3A1/R3A2 PASS」，请在主计划表上补证据列（当前 R3A1/R3A2 行的证据列为空、状态 PENDING），
   并明确区分「状态机口径 PASS」与「真实 DS PASS」——报告已声明后者未做，**T42 继续 PENDING 是正确的**。

---

## 7. 对「380 项绿」的采信口径（本报告的立场）

- 采信：它能证明 A 节的编解码/分帧/超限拒绝、C 节的 HMAC 向量与常量时间比对、D/E 节的票据拒绝面、
  F/G 节中**被显式断言到**的状态转移和资源释放，确实如代码所写那样工作。
- 不采信：`380 == 0 失败` 不等于「分支都被走过」。反例已给出：`DsView.Exited`（`Program.cs:446`）
  在全文件零调用点，`HandlePeerExited` 整条分支从未执行；而 M1 揭示替身的同步性会让「端口过早释放」
  这类真实缺陷**在门禁里必然表现为绿色**。
- 因此本报告只接受「代码 + 可直接执行的最小复现」作为结论依据；`_r3a_control_report.md` 的
  「全部通过」只能作为**待复核的输入**，不能作为证明。
