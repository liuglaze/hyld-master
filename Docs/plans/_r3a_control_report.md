# R3-A1 控制协议 / 票据 / Lobby 进程协调器 —— 实施与验收报告

> 范围：`Docs/plans/net-architecture-migration.md` 的 **R3A1 / R3A2** 两行，契约见 `Docs/plans/net-r3-control-contract.md`（已冻结，本任务未改）。
> 边界：只实现 Lobby 侧的控制协议、票据、进程编排状态机与独立门禁；**不接旧匹配入口、不启动真实 HyldDS、不改 `PMDsHost`、不做 Unity/UE 编译**。
> 本文只记录本次实施与实测证据；阶段状态以主计划为准。

## 1. 结论摘要

R3-A1 交付完成，`Tools/PMDsControlTest` 在本机 **build exit=0 → run exit=0，473 项检查全部通过（R3A1 149 / R3A2 324，含负向输入 50 项），0 项失败**。

交付四块：共享控制协议（`PMNet/Control/PMDsControlProtocol.cs`：DTO + 严格有界 codec + 4 字节 LE 分帧）、票据（`PMDsTicket.cs`：每局 256 位加密随机密钥、HMAC-SHA256 绑定对局/世代/摘要/身份/到期/Nonce、常量时间校验、不打印秘密）、Lobby 进程协调器（`Server/DS/PMDsCoordinator.cs`：真状态机、名册与端口分配回收、身份/摘要/sceneReady 校验、结果幂等与有界墓碑）、进程适配（`Server/DS/PMDsProcess.cs`：逐参、无 shell、白名单、输出有界、**日志不参与就绪判定**）。

**独立对抗审查（`_r3a_control_review.md`）提出的 4 项「必须修」已全部闭合**（详见第 11 节）：① 强杀失败 / 进程尚未退出时**不再提前归还共享端口与玩家占用**，改由 `Tick` 节流重试，释放前提是「确认进程已退出（`IPMDsProcess.WaitForExit`）或收到对端显式 `Exited`」；② `ResultAcked` 更名为 `ResultCommitted` 并明确「**本地受理 ≠ 对端确认**」，对端收尾只由 `PeerExitObserved` 决定（`ResultAckAbandoned` 记录的是「己方放弃重传」）；③ 同 `ResultId` 的内容判定改为 winner + summary **逐字节完整比对**（32 位摘要降级为诊断字段，墓碑冲突不覆盖原文）；④ `Exited` / `Error` 增加严格状态门，`Allocated`（未启动）阶段不再可能被推到终态。

关键实现选择（都带测试证据）：① 控制帧的 MAC 覆盖范围是「MAC 字段之前的**原始字节**」，验签不依赖「重新编码得到同样字节」，因此不存在规范化绕过面；② 唯一入站入口 `OnControlPayload` 内先解码再验 MAC，**验签失败不返回任何业务字段**，从结构上消除「先改状态后验签」；③ 协调器不订阅进程事件、不读 stdout，状态只在 `Tick()` 与显式入站调用这条主线程路径上变化，所以「进程存在 / 日志出现 ready」都不可能推进到 `Ready`；④ 测试用的 DS 密钥从 `ExportBootstrapDocument()` 解析（真实 DS 取密钥路径）。

三处必须让 R3-B 知道的接线前提（第 5 节展开）：**引导内容走同机文件而不是控制通道**；**端口池必须由 Lobby 全局共享**（各自 `new` 一个池会让所有会话选到同一端口，G50 钉住了这个后果）；**MAC 验证不等于防重放**，端点消费账本属 R3-B。

未闭合项（第 9 节）：真实 HyldDS 进程与真实 Unity 场景未启动（T42 仍 PENDING，A1 门禁用替身进程，**不得当作 T42 通过**）；跨程序集/跨进程端口唯一性、防重放账本、引导文件原子写入与权限、Unity 侧 `HMACSHA256` 可用性均留待 R3-B / A5 验证。

## 2. 必读范围（实际读取，按序）

| 顺序 | 文档 | 对本实现的直接约束 |
|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 指明客户端/服务端/迁移计划三处入口文档 |
| 2 | `Client/Assets/AGENTS.md` | 客户端语言面（Unity 2019.4）、协议来源与「改协议只改权威源」约定；确认 `.meta` 必须随新资源提交 |
| 3 | `Server/AGENTS.md` | 服务端 `net8.0`、无数据库、`Server/**` 隐式 compile、`Server/PMNet` 以链接方式共享同一份源码、`Console.Read()` 在 stdin 重定向时秒退等既有事实 |
| 4 | `Docs/plans/net-architecture-migration.md`（卷首当前状态 / §3.9 / §5 / 文末 RV1–RV7 与 R3 分步计划） | M12 模块职责与硬边界；R3-A1/A2 的验收口径；「A 阶段不得当作整个 R3 完成」「进程用替身即明确仅状态机验收」 |
| 5 | `Docs/plans/net-r3-control-contract.md`（全篇） | §2 身份与票据规则、§3 消息清单与状态链与全部默认超时值、§5 门禁要求（真实 codec/票据拒绝面/半包粘包超限/可控时钟状态机/故障注入） |
| 6 | `Docs/plans/net-r0-contract.md` §11 | 明确「M12 Lobby↔DS 进程编排契约」正是本阶段要冻结的那一项，未覆盖部分留给 R3 |
| 7 | 周边源码（只读）：`PMNet/PMNetReader.cs`、`PMNetWriter.cs`、`PMNet/Declarations/PMNetDeclarations.cs`（`PMNetString` 的有界读写法）、`Tools/PMTransportTest/*`（门禁工程与退出码约定）、`HyldDS/run_ds.bat`（真实 DS 的参数形态） | 决定 codec 用 PMNetReader/Writer、上限校验在**分配前**用长度前缀预检、测试工程 `EnableDefaultCompileItems=false` + 直编真实源码、真实启动器参数逐个传递 |

文档如何决定实现：契约 §3 的消息清单直接决定 8 种消息类型与「未知类型/版本/尾部失败」；§3 的默认值（启动 30s / 心跳 15s / 结果确认重试 1s / 收尾 10s）直接变成 `PMDsCoordinatorOptions` 的默认值；§2 的「256 位随机 / 120 秒 / 常量时间比对 / 绝不日志输出密钥或完整票据」逐条对应 `PMDsMatchKey`/`PMDsTicketPolicy`/`PMDsCrypto.FixedTimeEquals`/`ToString()` 脱敏；§5 的门禁条目逐项变成测试的分节结构。

## 3. 交付物与编码约定

| 文件 | 行数 | 说明 |
|---|---|---|
| `Client/Assets/Scripts/PMNet/Control/PMDsControlProtocol.cs` | 2163 | 共享：常量/分帧/DTO/codec/签名器/加密原语（C#7.3 + netstandard2.0） |
| `Client/Assets/Scripts/PMNet/Control/PMDsTicket.cs` | 1075 | 共享：每局密钥、票据签发/校验、引导文件编解码 |
| `Client/Assets/Scripts/PMNet/Control.meta` + 上述两文件的 `.cs.meta` | — | 目录 meta + 文件 meta，GUID 与既有 3095 个 GUID 比对唯一 |
| `Server/DS/PMDsCoordinator.cs` | 2235 | Lobby 侧一局协调器（状态机/端口池/墓碑账本/计数器；含本轮审查闭合改动） |
| `Server/DS/PMDsProcess.cs` | 838 | 进程接口 + 真实固定 exe 适配器 + 有界输出环形缓冲（含 `WaitForExit` 有界确认） |
| `Tools/PMDsControlTest/PMDsControlTest.csproj` | 53 | R3-A1 门禁工程（net8.0，直编真实 Control/Coordinator 源码） |
| `Tools/PMDsControlTest/Program.cs` | 2843 | 473 项检查（A–K 十一节） |

- 共享源遵守「源 UTF-8 带 BOM + CRLF」，`.meta` 遵守「无 BOM + LF」（已按字节校验：BOM=True/CRLF=True、loneLF=0）。
- 未改 `Server/Server.csproj`：`Server/**` 隐式 compile 自动纳入 `Server/DS/*.cs`，`PMNet/**` 已链接。
- 未新增依赖：共享层只用 BCL（`HMACSHA256` / `RandomNumberGenerator` / `SHA256`），无 Unity、无 `System.Text.Json`、无 protobuf 引用。

## 4. 接口（R3-B 接入样例，以下 API 均已交付并被测试覆盖）

```csharp
// ── Lobby 侧：一局一个协调器；端口池必须全局唯一 ─────────────────────────
PMDsCoordinatorOptions options = new PMDsCoordinatorOptions();
options.PortRangeFirst = 7801;  options.PortRangeLast = 7899;
options.ProtocolHash = PMNetRegistry.ProtocolHash;   // 非 0 时强制与分配请求一致
options.MaxRosterPlayers = 6;

PMDsPortPool lobbyPorts = new PMDsPortPool(options.PortRangeFirst, options.PortRangeLast); // ★ 共享

PMDsCoordinator session = new PMDsCoordinator(
    options, PMDsSystemClock.Instance,
    new PMDsSystemProcessLauncher(whitelistFromConfig),   // 固定 exe 白名单（来自配置）
    mySink,                                              // 实现 IPMDsCoordinatorSink
    lobbyPorts);                                         // ★ 不共享会让所有会话撞同一端口

PMDsCoordinatorReply r = session.Allocate(new PMDsAllocationRequest {
    MatchId = matchId, DsId = dsId, Epoch = epoch, ProtocolHash = hash,
    CollisionDigest = collisionDigest, Roster = roster,     // 1..6 人，Uid/PlayerId 唯一
    Process = new PMDsProcessLaunchRequest(dsExeFromConfig, dsWorkDir,
        "-batchmode", "-nographics", "-server",
        "-dsid", dsId, "-matchid", matchId,
        "-port", "7801", "-bootstrap", bootstrapFilePath),  // ★ 只传路径，密钥不进命令行
});
byte[] document = session.ExportBootstrapDocument();        // R3-B 原子写文件（临时文件 + rename）
session.BeginStart();                                       // → Starting
while (running) { session.Tick(); Thread.Sleep(200); }       // 时钟驱动超时/重发/强杀
// 入站（真实 socket 收帧后）：
session.OnControlPayload(payload, 0, payload.Length);        // 内部先解码再验 MAC，失败不改状态
// 客户端持票入局时的所有权判定：
PMDsTicketVerification v = session.ResolveTicket(ticket, nowUnixSeconds);  // 身份只来自票据
```

```csharp
// ── 宿主副作用接收器（A1 已交付的事件面；逐项对应状态推进/需要对外发送的东西）──
sealed class MySink : IPMDsCoordinatorSink {
  public void OnEffect(PMDsCoordinatorEvent e) {
    switch (e.Effect) {
      case PMDsCoordinatorEffect.ProcessStartRequested:  Log("ds started pid=" + e.ProcessId); break;
      case PMDsCoordinatorEffect.ReadyAddressPublished:  PublishToClients(e.Port); break;       // 唯一一次
      case PMDsCoordinatorEffect.ControlMessageOut:      SendToDs(PMDsControlFraming.Frame(e.ControlPayload)); break;
      case PMDsCoordinatorEffect.ResultAccepted:         ApplyResult(e.ResultId, e.WinnerTeamId, e.ResultSummary); break;
      case PMDsCoordinatorEffect.GracefulShutdownRequested:
      case PMDsCoordinatorEffect.ProcessKillRequested:   Record(e.Effect, e.Reason); break;
      case PMDsCoordinatorEffect.ResourcesReleased:      Log("port released " + e.Port); break;
      case PMDsCoordinatorEffect.SessionEnded:           ReleaseMatch(e.State, e.Reason, e.ExitCode); break;
    }
  }
}
```

```csharp
// ── DS 侧（R3-B 的 LobbyAgent）：密钥与票据只从同机引导文件取 ──────────────
PMDsBootstrappedMatch boot; string error;
PMDsBootstrapDocument.TryDecode(File.ReadAllBytes(bootstrapPath), out boot, out error);
PMDsControlSigner dsSigner = boot.Key.CreateControlSigner();     // 与 Lobby 同一把密钥
PMDsTicketVerifier verifier = boot.Key.CreateTicketVerifier(
    new PMDsTicketExpectation(boot.MatchId, boot.DsId, boot.Epoch, boot.ProtocolHash));
// 上行 Ready/Heartbeat/Result：PMDsControlMessage.Create(...) → dsSigner.Sign(msg) → PMDsControlFraming.Frame(...)
```

## 5. A1 必须明确的四项决策

1. **Bootstrap 交付方式：同机引导文件，不走控制通道。** `PMDsBootstrapDocument` 只做「魔数 + 版本 + 无 MAC 的规范 Bootstrap 信封 + 32 字节密钥」的**纯字节编解码**（不碰文件系统）；原子发布与文件权限由 R3-B 实现。理由：R3-A 阶段控制通道只有 loopback 且**请求方尚未被认证到「就是本机那一局的 DS」**，而票据是 bearer 凭据——把它交给一个未认证端点等于把入场券发给任意本机进程。因此协调器**拒绝**入站 Bootstrap（测试 H17）。引导文件**不含对文件自身的 MAC**：文件里的密钥同时是验签材料，用它给自己签名不构成认证，这一点是有意的。
2. **MAC 覆盖口径：覆盖 MAC 字段之前的原始字节。** `MacFieldOffset` 由解码器记录，验签直接对 `RawPayload[0..MacFieldOffset)` 求 HMAC，**不重新编码**。这样「同一语义多种字节表示」的规范化差异不会成为验签绕过面；MAC 之后出现任何尾部字节即整帧拒绝（测试 A/B16）。
3. **端口池必须共享，否则跨局撞端口。** `PMDsPortPool` 是跨局资源，而协调器是一局一实例。A1 因此提供共享池构造重载，并在测试里同时钉住正反两面：共享池耗尽→拒绝分配、释放后可再分配（G45–G49）；各自 `new` 池→两会话拿到同一端口（G50，明确写为「R3-B 必须用共享池」的理由）。
4. **不做防重放，且不宣称做了。** 票据模块只验证真实性与字段；同一张票据被校验两次都会通过。「同一票据只能被唯一真实端点消费、重连由 Lobby 显式重发」属 R3-B 的连接接受器。协调器不保存「已用票据」账本。

## 6. 测试证据

```bat
dotnet build Tools/PMDsControlTest/PMDsControlTest.csproj -c Release --no-incremental   REM 退出码 0，0 警告 0 错误
dotnet Tools/PMDsControlTest/bin/Release/net8.0/PMDsControlTest.dll                     REM 退出码 0
```

实测输出（末行）：

```
  R3A1：149 项检查
  R3A2：324 项检查
  全部通过：473 项检查（含负向输入 50 项），0 项失败
```

| 节 | 覆盖内容 | 项数（含负向） |
|---|---|---|
| A | 分帧：往返、逐字节半包、粘包、半包+粘包混合、超限立即拒绝且不吞数据、0x7FFFFFFF 前缀、零长、恰好 64KiB、滞留峰值 ≤ 64KiB+4、写侧拒绝 | 34（7） |
| B | 编解码严格性：8 种消息往返、未知类型/版本/字段、字段重复、字段乱序、MAC 后尾部字节、缺 MAC、载荷超限/空载荷、超长 MatchId（读写两侧）、名册 >6/重复 Uid/重复 PlayerId、超长票据/摘要、ResultId=0、Epoch=0、Hash=0、信封 body 类型不一致 | 54（20） |
| C | MAC：正确/错密钥、篡改正文、篡改 MAC、指纹、常量时间比较（含越界与 null 不抛）、**HMAC-SHA256 对齐 RFC 4231 测试向量** | 15（0） |
| D | 票据：256 位、两次生成不同、长度校验、默认 120s、到期排他边界、错密钥、身份不符、错局/错世代/错摘要/错 DsId、篡改、截断、错魔数、错版本、有效期超上限、签发时间在未来、**幂等重发字节完全一致**、过期重签、撤销、ToString 不含密钥/MAC/完整票据、带偏移量校验 | 32（1） |
| E | 引导文件：解析各字段、逐张票据验签、换密钥失败、错魔数/截断/长度不符/版本不符 | 14（4） |
| F | 协调器正常路径：分配/重复分配、启动、Ready 三类负向、Ready 正向且只发布一次、重复 Ready 幂等、Running、心跳、结果接受与业务通知、Ack 字节一致、重复结果、冲突结果、定时重发 3 次后 **ResultCommitted（本地受理，且断言未由此推断对端确认）**、Shutdown 消息、进程退出、终态释放端口/名册、墓碑服务终态重复、终态幂等 | 77（0） |
| G | 故障注入：启动失败、启动超时（29.999s/30s 边界）、就绪前崩溃、**日志出现 ready 不构成就绪**、运行中崩溃、心跳超时（14.999s/15s 边界）、持续心跳不误判、Error→Failed、宿主关闭与宽限期强杀（**强杀受 `KillRetryInterval` 节流且到期会重试**、不伪造退出）、启动前取消、DS 主动关闭、共享池耗尽、分配参数校验、非法进程请求、墓碑 TTL 与条数上限、**同 ID 同胜方但摘要不同 → Conflict**、端口池语义 | 86（1） |
| H | 拒绝面：坏 MAC 不改状态、错密钥（客户自带密钥无效）、结构非法、错 MatchId/世代/摘要/DsId、Starting 阶段拒心跳与结果、Lobby 拒 Bootstrap/ResultAck、票据名册校验（他局票据/过期/非名册 uid）、终态名册释放后不再解析 | 32（1） |
| I | **真实 loopback TCP**：真 `TcpListener`/`TcpClient`，Ready 逐字节发送（半包）、Heartbeat+Result 一次写入（粘包）、篡改 MAC、超限前缀；断言 4 帧解出、篡改被拒 1 次、超限立即拒绝、状态推进到 ResultPending、地址只发布一次 | 10（0） |
| J | 真实固定 exe 适配：有界环形缓冲语义、无 shell/相对路径/换行参数/null 拒绝、白名单命中与未命中、不存在 exe、**真实启动本测试自身作子进程**（stdout 202890 字节而尾部保留 8192、退出码 7、stderr 有输出、强杀长驻子进程）、日志不推进状态 | 32（8） |
| K | **审查闭合回归（M1–M4 最小复现）**：M1 未退出不得释放端口/名册 + `Tick` 节流重试 + `Kill` 抛异常不静默；M2 无对端入站不得宣布确认（只在显式 Exited 后才置 `PeerExitObserved`）；M3 账本层与协调器层「同 hash 不同 summary → Conflict」且墓碑不覆盖；M4 `Allocated` 阶段 `Exited`/`Error`/宿主退出通知全部被拒且状态/端口/名册不动，`Starting`/`Running` 阶段 `Exited` 分别落到 Failed；M1×M4 交叉：终态延迟收尾中显式 `Exited` 也不能代替本地确认 | 87（8） |
| 合计 | 与门禁末行一致（R3A1 149 / R3A2 324） | **473（50）** |

**负向覆盖的可达性**：负向 50 项均在有相应拒绝计数器/状态断言的分支上，不存在「只把返回值当 false 判」的空断言。其中 M4 的三条（K56/K62/K65）正是审查指出的「整条分支零调用点」的补测——旧 380 项里 `DsView.Exited` 全文件零调用点。

补充证据（**上一轮 R3-A1 交付时实测，本轮未重跑、仅作历史记录**）：`Tools/PMNetLangCheck`（netstandard2.0 + C#7.3，编全部 `PMNet/**`）**exit=0，0 警告 0 错误**；`PMTransportTest`/`PMNetWorldTest`/`PMReplicationTest`/`PMClientCheck`/`PMUnityGlueCheck` 均 **build exit=0**。本轮修复的硬边界是「只编 `PMDsControlTest` build0 后 run」，因此本轮只重跑该门禁（末行 473 项，exit=0）；上表其它门禁是否仍绿不属本轮的验证范围。

## 7. 契约逐项对照

- **R3A1（控制协议：编解码/超限/非法票据/过期/篡改）**：PASS。严格有界 codec + 分帧（A/B）、票据全拒绝面（D/E）、真实回环控制消息（I）。
- **R3A2（Lobby 编排：启动失败/Ready/崩溃/重发结果/重复结果/退出）**：PASS（**仅状态机口径**）。受控进程接口 + 注入时钟，覆盖启动失败/超时/崩溃/心跳/重复与冲突结果/Ack 重发/终态回收/墓碑（F/G/H），以及本轮审查闭合回归（K）。
- **契约 §3 状态名的一处修正（需主 Agent 同步）**：契约写的是 `… -> ResultPending -> ResultAcked -> Exited`。实现改为 `ResultCommitted`（数值仍为 6），因为 DS→Lobby 的上行消息只有 Ready/Heartbeat/Result/Exited/Error，**不存在 ResultAck 的上行确认消息**，「对端确认」不可能由上行的确认帧达成；进入该状态的唯一条件是自己数够重发次数（`ResultAckAbandoned`）。对端收尾一律看 `PeerExitObserved`（DS 的 `Exited` 消息或进程退出）。**本次未改契约文件，只在本报告里登记修正点。**
- **R3A3/R3A4（会话接线与弱网）**：不在本任务，由 A2 组与 R3-B 负责。
- **R3A5（C#7.3/netstandard2.0 与既有回归）**：共享源语言门禁与 5 个既有门禁 build 已实测通过；完整回归运行由主 Agent 统一执行。
- **T42**：仍 **PENDING**。A1 未启动任何真实 DS、未加载 Unity 权威场景、未接客户端，**不具备标 PASS 的任何证据**。

## 8. 明确不做（与任务边界一致）

- 未启动、未杀死任何真实服务；未执行 `svn`/`git` 提交；未递归委派。
- 未编译 `Server` 主工程（本机可能锁文件），改为由门禁工程直编真实 `Server/DS/*.cs` 验证可编译性。
- 未改 `PMDsHost`、未动 `BattleManage`/匹配入口/旧开局路径、未改主计划与契约文档、未碰 A2 的 `PMNet/Session/**`。
- 未写引导文件到磁盘（只做字节编解码），未做文件权限与原子发布。
- 未把替身进程的结论当成 Unity/真实 DS 验收。

## 9. 未完成项与剩余风险（交给 R3-B / A5 / 主 Agent）

1. **Unity 侧 `System.Security.Cryptography.HMACSHA256` 可用性未经 Unity 编译验证**。现有证据：`netstandard2.0 + C#7.3` 门禁通过、且 Unity 工程内已有运行时使用 `System.Security.Cryptography`（`Assets/XLua/Src/SignatureLoader.cs`）。若 Unity 2019.4 的 .NET Standard 2.0 profile 缺少 `HMACSHA256`，需改托管实现或换 API——**这是本任务最大的未验证假设**。
2. **真实 DS 进程与真实控制链路未跑**：真实 `Process` 适配器只以「本测试自身」作子进程验证（有界输出/退出码/强杀），未对 `HyldDS.exe` 验证；控制通道只有 loopback 单连接用例，未做长稳/大流量/多并发。
3. **端口唯一性、防重放账本、票据端点绑定**属 R3-B：必须用 Lobby 级唯一 `PMDsPortPool`、维护「票据→真实端点」消费账本、并在调用 `Activate` 前完成 A1 票据校验。审查补充了两条**对称缺口**（见第 12 节）：uid 占用同样没有跨会话守卫；`PMDsTicketVerification` 当前不暴露 Nonce/票据指纹。
4. `MaxResultAckRetransmits=3` 与「结果确认重试 1 秒」的具体次数/时长是 A1 的合理默认，未做真实网络下的收敛实测；契约 §3 只给了默认值口径。本轮新增的 `KillRetryInterval=1s` / `ProcessExitConfirmWait=500ms` 同样是 A1 补充默认，未在真实进程上量测（真进程 Kill→退出的实际耗时未知）。
5. 协调器**不跨进程持久化**：Lobby 重启即丢墓碑与结果幂等（当前无数据库，契约 §3 已声明）；跨进程「恰好一次」不承诺。
6. 控制帧的 varint 未强制最短编码（安全性由「MAC 覆盖原始字节」保证，不构成绕过面）；如需第三方互通可后续收紧。
7. ~~「同 ID 不同内容」的判定基于 `winnerTeamId + summary` 的 SHA-256 前 4 字节~~ **已修（审查 M3）**：判定改为 winner + summary **逐字节完整比对**，32 位摘要降级为 `ContentHash` 诊断字段（`ToString` 只打长度与诊断哈希）；墓碑保存摘要原文（≤ 512 字节，乘条数上限有界）且冲突分支**不覆盖**已有条目。K47/K53 直接证明了这条路径。
8. **“僵死进程永远不退出”的语义选择**：现在会无限节流重试强杀且**永不**释放端口/名册（宁可为真，不为好看）；这意味着极端情况下共享池上的一个端口会被永久占用。若 R3-B 需要「带 quarantine 的最终回收」，应由 Lobby 层在告警后显式 `ReleaseAll`/换池，而不能退回「Kill 完就算释放」。

## 10. 复现命令

```bat
cd /d D:\UGit\hyld-master
dotnet build Tools\PMDsControlTest\PMDsControlTest.csproj -c Release --no-incremental   REM 本轮实测 exit=0，0 警告 0 错误
dotnet Tools\PMDsControlTest\bin\Release\net8.0\PMDsControlTest.dll                      REM 本轮实测 exit=0：473 项（R3A1 149 / R3A2 324），负向 50，0 失败
echo %ERRORLEVEL%                       REM 期望 0
```

> 本轮为避免不属修复范围的副作用，**未重跑** `PMNetLangCheck` 等其它门禁（它们不引入新增文件，且本轮只改了 `Server/DS/*.cs` 与测试 `Program.cs`）。

## 11. 审查闭合（针对 `_r3a_control_review.md` 的 M1–M4）

本轮在「只改协调器/进程适配/门禁/本报告」的硬边界内修复了审查提出的 4 项必须修，并**保留了既有测试意图、只纠正了错误的 oracle**。

### M1 真实缺陷：异步 Kill 失败/尚未退出时不得释放端口与玩家占用

改动（`PMDsCoordinator.cs` / `PMDsProcess.cs`）：

| 原来 | 现在 |
|---|---|
| `Finalize`：`_state = 终态` → `KillIfRunning`（一次性闩锁）→ **无条件** `ReleaseResources()` | `Finalize`：终态 → **请求强杀** → 有界确认进程已退出 → 确认到才 `ReleaseResources(reason)` |
| 确认不到没有任何后续：进程残留 + 端口已归还，无人再回收 | 确认不到 → `IsAwaitingProcessExit=true`，**端口与名册保持占用**，`Tick` 每轮继续有界确认 + 节流重试强杀 |
| `KillIfRunning`：`_killRequested` 闩锁永不复位；`Kill()` 异常被 `catch(Exception){}` 吞掉 | `RequestKill`：每次真实尝试；失败计入 `KillRequestFailures` 并写进 `ProcessKillRequested` 的 `Reason`；下次请求受 `KillRetryInterval` 节流 |
| `IPMDsProcess` 无「等退出」能力；`PMDsSystemProcess.Kill()` 不调 `WaitForExit` | 接口新增 `WaitForExit(int)`；真实适配器 `Kill()` 在发出终止请求后做一次**有界等待**（`KillWaitMilliseconds=2000`），超时不抛也不假装退出 |

语义边界（有意，写进代码注释）：显式 `Exited` 消息只把 `PeerExitObserved` 置真——**释放仍以本地确认进程已退出为前提**；若对端说走了而本地仍观测到它占着端口，则继续收尾并如实报告，不用「已释放」掩盖。

证据：K1–K32、K76–K87；其中 **K7/K8/K28/K81**（负向）断言「进程还在 → 端口不得归还」，**K26** 断言强杀失败原因可见。

### M2 口径缺陷：不得依据发送次数宣布对端确认

- `PMDsSessionState.ResultAcked` → `PMDsSessionState.ResultCommitted`（数值仍为 6，文档明写「本地受理，不代表对端确认」；契约文本未改，修正点已在第 7 节登记）。
- 新增计数器 `ResultAckAbandoned`（进入该状态的唯一条件是「己方放弃重传」）；进入时的 `Detail` 明确写「未由重发次数推断对端确认」。
- 新增 `PMDsCoordinator.PeerExitObserved`：只在 `Exited` 消息或进程退出时置真。**这是当前协议下唯一可用的「对端已收尾」信号**——协议里没有 ResultAck 的上行确认消息（这属契约层面的缺消息，不在本次修改范围）。

证据：F50/F50b/F50c、K33–K45。K36/K39 断言「无对端入站 ⇒ `PeerExitObserved` 仍为 false」，K37/K38 断言不得自行进入 `Exited`。

### M3 判定精度缺陷：同 ID 内容按 winner + summary 完整比对

- 墓碑新增 `Summary`（摘要原文，逐字节复制），比对改为 `WinnerTeamId` 相等 **且** summary 逐字节相等；`Record` 的 Conflict 分支**不覆盖也不刷新 TTL**。
- 协调器非终态与墓碑两条路径均改用完整比对；`ComputeResultContentHash` 保留但改为 **`diagHash` 诊断字段**（注释明确不参与判定）。

证据：G64b、K46–K55。K53 用**真碰撞对**（测试内部生日搜索约 6.5 万次，本机亚秒级达成，实测命中 `0x87592A1F`）把旧判定必然漏判的那类输入真送进协调器：第二条内容不同却被旧实现当成「重复」静默吞掉，现在返回 `RejectedConflict`。

### M4 真实缺陷 + 覆盖空洞：`Exited` / `Error` 状态门

- 新增 `CanAcceptRunningSessionFrame()`：要求 `_process != null` 且状态属于 `Starting/Ready/Running/ResultPending/ResultCommitted`；`Idle`/`Allocated`/终态均拒绝（终态返回 `Duplicate`）。
- `FramesAccepted++` 移到「确认真正生效之后」（旧代码把终态 no-op 也计成已接受，即审查 S5）。
- `OnProcessExited` 也要求「有可归属的进程」，堵住「未启动就被宣告终态」。
- 终态 + `IsAwaitingProcessExit` 时，对端显式 `Exited` 可以完成收尾（此时**不**伪造状态转移，只补释放）。

证据：K56–K75（旧 380 项里 `HandlePeerExited` 整条分支零调用点）；另有 K76–K87 覆盖「终态延迟收尾中收到显式 `Exited`」这条交叉路径（对端说走了、本地仍观测到进程活着时不释放）。

### 可复现性（本轮额外做的反证）

为了不重复「拿全绿当证明」的错误，本轮在临时副本上**故意回退**三处修复（① `WaitForProcessExit` 恒真 = “Kill 完就算退出”；③ 判定退回只比 32 位摘要；④ 去掉 `Exited` 状态门）后重跑同一门禁：**473 项中 40 项失败**（G64b；K6–K22、K27–K32、K47–K67、K77–K84 区间内的相关断言），随后恢复原文件（md5 比对一致）重跑恢复为 **473/0**。这说明新增的 K 节用例确实具备证伪能力，而不是靠实现自我报告。

## 12. R3-B 交接补充（审查 S1/S2）

1. **uid/玩家占用没有跨会话守卫**：端口有 Lobby 级 `PMDsPortPool`，但名册 uids 只做**本局内**唯一性校验（`ValidateIdentities`），同一 uid 可以同时出现在两个会话的名册里。匹配层必须像端口一样持有**共享的玩家占用账本**（契约 §3「终态释放玩家与端口占用」暗示了这一点）。
2. **票据校验结果不暴露 Nonce/指纹**：`PMDsTicketVerification` 只有 Verdict/Identity/局与世代/签发到期时间；`Nonce` 与票据字节编解码器都是票据模块内部（`PMDsTicketCodec` 为 `internal`）。R3-B 的端点消费账本只能用「原始票据字节的哈希」当键，需自行实现，不要以为有现成句柄。
3. **票据时效窗口**：引导文件在 `ExportBootstrapDocument()` 调用时刻签发（默认 120s 且**重发不刷新**）。DS 不能把引导文件里的票据当长期会话凭据；需要重连时必须由 Lobby 显式重发新票。
4. **强杀/收尾的观测接口**：宿主可读 `IsAwaitingProcessExit` / `ResourcesHeld` / `PeerExitObserved` / `Counters.DeferredCleanupTicks` / `Counters.KillRequestFailures` 做告警；`SessionEnded` 与 `ResourcesReleased` 在延迟收尾下**不再是同一个 Tick 内发生**，对账逻辑不要假设两者同时到达。
5. **审查 S3/S6/S7 的处理口径（未改，有界降级说明）**：S3（流式解码器的内存有界性依赖调用方分块喂入）位于共享控制 codec（`PMDsControlProtocol.cs`），**不在本轮允许修改的文件清单内**，且对端无法强迫本机一次喂入超大块（Socket 接收缓冲 ≤ 65536 < `_maxBufferedBytes` 131080）；R3-B 接线时按 `≤64KiB` 缓冲读即可。S6（三处票据长度上限常量不一致）是 fail-closed 方向的不一致，不改也只会更严。S7（引导文件里的票据 120s 不刷新）已作为接线约束写在本节第 3 条。三项均已在审查里明确标注为「推断/未证实为缺陷」，本轮不做超出边界的修改。
