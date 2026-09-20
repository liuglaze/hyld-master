# R3-B3 Lobby 宿主接线（PMDsPlayerLedger / PMDsControlListener / PMDsLobbyHost）——实施与验收报告

> 范围：`Docs/plans/net-architecture-migration.md` 的 **R3B3** 行 + `Docs/plans/net-r3-control-contract.md` §7.1/§7.3（冻结接口）。
> 边界：只实现 Lobby 侧宿主 / 全局账本 / 控制 listener / 匹配分流 / Server 生命周期接线；**不启动**任何真实服务与进程常驻、不编 Server 主输出（用临时输出）、不改共享 §7 网络/Unity/PMR3/proto、不提交、不递归委派。
> 本文只记录本轮实施与实测证据；阶段状态以主计划为准。

## 1. 结论摘要

- `Tools/PMDsLobbyTest`：`build` exit=0 → `run` exit=0，**230 项检查（含负向输入 28 项），0 项失败**。
- 回归：`Tools/PMDsControlTest`（我改了协调器）**473/0 保持全绿**；`Server/Server.csproj`（**临时输出**到 `%TEMP%/pmds-srv-check`）build exit=0；`Tools/PMNetLangCheck` build exit=0。
- **冻结共享 API 在本轮期间由其他组落盘**（`Client/Assets/Scripts/PMNet/Session/PMDsEntryOffer.cs`、`PMNet/R3/PMR3Runtime.cs`）。Lobby 生产接线**直接消费**它们；测试用**真实** `PMDsEntryCodec` 做字段往返，**没有**在测试或生产里另写替身 codec。
- 端口不再来自任何硬编码：`-port` 由 `Allocate` 后的**真实端口**拼进固定 args，并由协调器 `SetLaunchRequest` fail-closed 校验（错值/缺项/重复项一律拒绝）。
- uid 占用与端口**同点释放**（都以「确认 DS 进程已退出」为前提），因此不存在「uid 已能重新入局、旧进程还占着它」的窗口。
- **跨组对齐（只读交叉核对，非本组实现）**：Unity 组 `PMDsSessionHost` 用 `-port` 开 UDP 并在 `BoundPort != port` 时报错，`Ready.CollisionDigest = PMR3Runtime.CollisionDigest`；`PMNetLaunchOptions` 解析的正是本组发出的 `-bootstrap <abs>` / `-control host:port` / `-port` / `-dsid` / `-matchid`；`PMDsHost` 仅在 `-bootstrap` 非空时转交新宿主。两侧参数名、端口对账与摘要来源完全一致。
- **一处需主 Agent 定调的跨组语义差（本组不改）**：DS 侧收到匹配的 `ResultAck` 后立刻 `SendExited(0)`，而 A1 协调器把「`ResultPending` 期间的对端 `Exited`/进程退出」判为 `Failed("结果确认前进程退出")`。详见 §7 第 1 条。

## 2. 必读范围（实际读取，按序）

| 顺序 | 文档 | 对本实现的直接约束 |
|---|---|---|
| 1 | `AGENTS.md`（根） | 三处入口文档指向；项目为 Unity + C# 服务端 |
| 2 | `Client/Assets/AGENTS.md` | Unity 2019.4 语言面；DS 启动参数契约（`-server/-port/...`）；「战斗 UDP 端口硬编码 7777，同机多局互抢」 |
| 3 | `Server/AGENTS.md` | `net8.0`、无数据库、`Server/**` 隐式 compile、`PMNet/**` 链接共享、`Program.cs` 保活差异 |
| 4 | `net-architecture-migration.md`（卷首当前状态 + 文末 R3-B 分步计划） | R3B3 的验收口径；「A 阶段不得当作整个 R3 完成」；写边界分离 |
| 5 | `net-r3-control-contract.md`（全篇，重点 §3/§5/§7.1/§7.2/§7.3） | 状态链与默认超时；§7.1 offer 走 `MainPack.Str`；§7.3 宿主单线程所有者、全局端口池与 uid 占用、引导文件原子发布、`Ready` 才发 offer、掉线中止不伪胜、结果只做控制通知 |
| 6 | `_r3b_lobby_survey.md`（全篇） | A–G 已确认链路与行号；P1–P7 推断；S1/S2 建议接口与四条禁令；S4 运行时验证清单 |
| 7 | `_r3a_control_report.md`（§4 接口、§7、§9、§11、§12） | 协调器/票据/进程 API 与「释放以确认为前提」「发票据不暴露指纹」「必须共享端口池」等硬前提 |

文档如何决定实现：§7.3 决定宿主是「唯一串行所有者 + 线程安全命令队列」；§7.1 决定 offer 走 `StartEnterBattle` 而非新 proto；§7 A1 的 `Allocate` 决定必须新增 `Allocated` 态 `SetLaunchRequest`（端口只在分配后才知道）；`_r3a_control_report.md` §12 的对称缺口（uid 占用无跨会话守卫）直接变成 `PMDsPlayerLedger`。

## 3. 交付物

| 文件 | 行数 | 说明 |
|---|---|---|
| `Server/DS/PMDsPlayerLedger.cs` | 204 | 新增：跨会话 uid 占用账本（整局原子预留、按局整体释放） |
| `Server/DS/PMDsControlListener.cs` | 835 | 新增：仅 loopback 的真实 TCP listener（4 字节 LE 分帧、worker 只入队、三重有界） |
| `Server/DS/PMDsLobbyHost.cs` | 1839 | 新增：唯一串行调度所有者、全局端口池 + uid 账本、引导文件原子发布、offer/结果路由 |
| `Server/DS/PMDsCoordinator.cs` | 2465（+） | 加法式：`IPMDsPlayerLedger`、`Allocate` 阶段 uid 预留、`SetLaunchRequest`、`RejectedPlayerOccupied`、同点释放 |
| `Server/Controller/Controllers.cs` | 1277（+215） | `StartFighting` 单一二分点（`HYLD_PMNET_DS=1`）；新链缺人整局拒绝不缩编；旧 `ClearSence` 不作用新局 |
| `Server/Server/Server.cs` | 421（+192） | 按配置初始化宿主；生产客户端网关（消费冻结 `PMDsEntryCodec`/`PMR3Runtime`） |
| `Server/Server/Client.cs` | 267 | 断线通知宿主（`NotifyClientDisconnected`），与旧 `BattleManage` 通知并列 |
| `Server/Server.csproj` | 60 | 链接 `Client/Assets/Scripts/PMR3/**/*.cs`（纯 C# 声明与生成物） |
| `Tools/PMDsLobbyTest/PMDsLobbyTest.csproj` | 64 | R3-B3 门禁工程（net8.0，直编真实 Control/Session/Server-DS 源） |
| `Tools/PMDsLobbyTest/Program.cs` | 1692 | 230 项检查（A–J 十节） |
| `Docs/plans/_r3b_lobby_report.md` | 本文件 | 报告（唯一文档写入） |

未触碰任何清单外文件。`.cs` 源为 UTF-8(BOM)+CRLF；`.csproj` 为 UTF-8 无 BOM + LF（沿用既有风格）。

## 4. 接口

### 4.1 Lobby 侧新增（`Server/DS`，namespace `PMNet.Control`）

```csharp
public sealed class PMDsPlayerLedger : IPMDsPlayerLedger {
    bool TryReserve(string matchId, PMDsRosterIdentity[] roster, out string reason); // 整局原子；失败不留部分占用
    bool Release(string matchId);                                                    // 幂等
    bool IsOccupied(int uid);
    bool TryGetMatchId(int uid, out string matchId);
    int Count { get; } int MatchCount { get; }
}

public sealed class PMDsControlListener : IDisposable {
    PMDsControlListener(string listenAddress, int port);            // 非 loopback 直接拒绝 Start
    bool Start(out string error); int BoundPort { get; }            // port=0 → 系统分配（测试）
    bool TryDequeueInbound(out PMDsControlInbound inbound);         // 有界队列；超限 ⇒ 关闭连接（显式失败）
    bool TryDequeueConnectionEvent(out PMDsControlConnectionEvent evt);
    bool TrySendFrame(int connectionId, byte[] payload, int off, int count);
    void CloseConnection(int connectionId, string reason);
    int ConnectionCount { get; } int QueuedInboundCount { get; } long QueuedInboundBytes { get; }
}

public sealed class PMDsLobbyHost : IDisposable {
    PMDsLobbyHost(PMDsLobbyHostOptions, IPMDsClock, IPMDsProcessLauncher, IPMDsLobbyClientGateway);
    static bool NewChainEnabled { get; }  static PMDsLobbyHost Instance { get; }   // 由 Server 按配置置位
    bool Start(out string error); void Stop(); bool IsRunning { get; }
    PMDsLobbyStartReply TryStartMatch(PMDsLobbyMatchRequest request);   // 线程安全：校验 + 入队
    void NotifyClientDisconnected(int uid);                            // 线程安全：入队中止
    void PumpOnce();                                                   // 测试用确定性单趟（生产为后台泵线程）
    bool IsUidInMatch(int uid); bool TryGetSessionState(string matchId, out PMDsSessionState state);
    bool HasBoundControlConnection(string matchId); PMDsCoordinator GetCoordinator(string matchId);
    event Action<PMDsLobbySessionRecord> SessionStarted;
    event Action<string,string> SessionFailed;
    event Action<string, PMDsSessionState, string> SessionReleased;
    event Action<PMDsLobbyResultNotice> ResultAccepted;                // 公开 event，不含回放
    PMDsPortPool PortPool { get; } PMDsPlayerLedger PlayerLedger { get; }
}

public interface IPMDsLobbyClientGateway {          // 宿主与大厅 TCP 的**唯一**缝（生产实现在 Server.cs）
    bool IsClientAuthenticated(int uid);
    bool TrySendEntryOffer(PMDsLobbyEntryNotice notice, out string error);  // 生产用冻结 PMDsEntryCodec.Encode
    void NotifyMatchEnded(PMDsLobbyResultNotice notice);                    // 允许空实现；不得伪造 BattleReview
    void RestorePlayerOnline(int uid);
}
```

`PMDsLobbyEntryNotice` **不是** `PMDsEntryOffer` 的替身：它只承载宿主产出的字段，生产网关把它们原样填进 `PMNet.Session.PMDsEntryOffer` 再调 `PMDsEntryCodec.Encode`；J 节用**真实** codec 做了往返校验。

### 4.2 协调器加法式新增（`PMDsCoordinator.cs`）

| 项 | 说明 |
|---|---|
| `interface IPMDsPlayerLedger` | 声明在协调器文件内（实现在 `PMDsPlayerLedger.cs`）。**原因**：A1 门禁工程只链接 `PMDsCoordinator.cs`+`PMDsProcess.cs`，把依赖收窄成接口后新文件不会强加给 A1 门禁，同时协调器仍表达「必须先占 uid」。 |
| `PMDsCoordinator(..., PMDsPortPool, IPMDsPlayerLedger)` | 新增 6 参构造；旧 4/5 参构造保留（内部委托，传 null） |
| `Allocate` 内 `TryReserve` | 顺序：结构校验 → 身份校验 → **uid 预留** → 取端口；端口取不到时**回滚** uid 预留（否则「占着人、没端口」泄漏） |
| `PMDsCoordinatorOutcome.RejectedPlayerOccupied` | 新增枚举值（追加，旧值不变） |
| `PMDsAllocationRequest.BootstrapFilePath` | 可选字段（A1 调用不填即跳过该项校验；R3-B 宿主始终填） |
| `SetLaunchRequest(PMDsProcessLaunchRequest)` | **仅 Allocated 态**；fail-closed 校验：结构合法、exe/工作目录不得更换、恰好一个 `-port` 且值 == 真实分配端口、恰好一个 `-dsid` == DsId、恰好一个 `-matchid` == MatchId、若给了 `BootstrapFilePath` 则恰好一个 `-bootstrap` 逐字符相等。任一失败 ⇒ `RejectedMalformed`，**不改状态、不释放资源** |
| `ReleaseResources` 末尾 `_playerLedger.Release(_matchId)` | 与端口归还**同点**（只在 `TryFinishTerminalCleanup` 确认进程已退出后到达） |

### 4.3 Server / 匹配 / 断线接线

- `Server` 构造末尾调 `InitializeDedicatedServerLobby()`：读环境变量 → `NewChainEnabled = Enabled` → 配置不完整时**只报日志并保持启用位**（匹配层显式失败，**不回退旧链**）→ `PMR3Runtime.Register()` 取 `ProtocolHash`/`CollisionDigest` → 白名单 + `PMDsSystemProcessLauncher` → `new PMDsLobbyHost(...)` → `Start()`。
- `ServerLobbyClientGateway`（生产网关）：`GetActiveClient(uid)` + `UserName` 非空 = 已认证；`PMDsEntryCodec.Encode(offer)` → `MainPack{Request=Matching, Return=Succeed, Action=StartEnterBattle, Str=text}`；日志只打 match/ds/port/uid，**不打 offer/Str/票据**；`NotifyMatchEnded` 只记日志（**不发 BattleReview**）；`RestorePlayerOnline` 设 `PlayerState.PlayerOnline`。
- `MatchingController.StartFighting`：`PMDsLobbyHost.NewChainEnabled` → `StartFightingDedicatedServer`，否则旧链。新链**不调** `BuildMatchUsers`/`TryBeginBattle`，因此 `_uidToBattleIds` 不被写入（旧 UDP/清场路由自然不会作用新局）。
- `Client.Close`：并列调用 `PMDsLobbyHost.Instance?.NotifyClientDisconnected(UID)`。
- `ClearSenceController.ClientSendClearSenceReady`：`IsUidInMatch(uid)` 时直接忽略并记日志。

### 4.4 宿主线程模型与重连策略

- 线程：listener 连接 worker **只做**「读字节 → 分帧 → 入队」；匹配回调线程只 `TryStartMatch`/`NotifyClientDisconnected`（校验 + 入队）；**只有**泵线程（生产后台线程 / 测试 `PumpOnce`）调用协调器，顺序固定为 `drain 连接事件 → drain 控制入站(≤64) → drain 命令(≤16) → 应用后置动作 → 对全部会话 Tick → 回收`。泵从不等待 socket。
- 绑定：连接在**第一条通过 MAC 校验**的控制消息之后才占据该会话的控制连接；`Ready` 到达只置 `EntryPublishPending`，真正发 offer + `MarkRunning` 在「后置动作」里落地（避免协调器回调深层重入）。
- 重连：观测到连接关闭才解绑；解绑之前第二条连接（即使 MAC/身份都合法，例如重放）一律按冒用处理：**关闭新连接、不计入协调器、不改状态**。未认证连接 20s 超时关闭。
- 结果：`ResultAccepted` → 记入待发列表 → 后置动作里触发公开 `ResultAccepted` 事件 + `NotifyMatchEnded`；回放（BattleReview）明确不做。
- 释放：`ResourcesReleased` → 会话标记释放 → `ReapSessions` 里 `RestorePlayerOnline`（仅此一次）+ 删除引导文件 + `SessionReleased` 事件。进程未确认退出时端口与 uid 保持占用。

## 5. 契约 §7.1/§7.3 逐项对照

| 要求 | 落点 | 测试证据 |
|---|---|---|
| offer 走已认证 Lobby TCP 的 `StartEnterBattle`，`Str = PMDS1:`+codec | `ServerLobbyClientGateway.TrySendEntryOffer` + `PMDsLobbyHost.PublishEntryOffers` | J：真实 `PMDsEntryCodec` 往返；D：offer 字段逐项校验 |
| 解码失败不得退旧链 | 生产网关失败即整局失败（`SessionFailed`），无旧链回退分支 | C：失败即失败、无进程启动 |
| `Ready` 才发 offer；重复 `Ready` 不重复通知 | `ReadyAddressPublished` → 后置动作 | D：`EntryOffersSent==2`，重复 Ready 后仍为 2 |
| 端口不得硬编码 | `Allocate` → `BuildFinalArguments(port)` | B：`-port == record.Port`（第二局亦然） |
| 协调器可增 `Allocated` 态 `SetLaunchRequest` 校验 `-port/-bootstrap/matchid/dsid` | `SetLaunchRequest` | B：错 port / 缺 bootstrap / 重复 port / 错 dsid / 错 matchid 全部拒绝且状态不变 |
| 全局端口池 + uid 占用共享 | `PMDsLobbyHost` 持有唯一 `PMDsPortPool` 与 `PMDsPlayerLedger` | A：两局不同端口；同 uid 拒绝（宿主层 + 协调器层） |
| 引导文件临时写再 rename、每局目录、失败回滚、BeginStart 只在发布成功后 | `TryPublishBootstrapFile` + `ProcessStartMatch` 顺序 | B：文件在参数里且已存在；C：发布失败 ⇒ 未启动进程 + 端口/uid 归还 |
| 启动参数固定 exe | `SetLaunchRequest` 拒绝换 exe/换工作目录 | A/B 断言 exe 等于配置值 |
| 匹配分流一次只选一条；缺人整局拒绝不缩编 | `StartFighting` 二分 + 新链逐个在线校验 | A：缺人 ⇒ `RejectedOffline`，未创建会话、无部分 uid 预留 |
| uid 占用与资源释放同点 | `ReleaseResources` | G：未退出 ⇒ 端口与 uid 都仍占用；退出 ⇒ 同时归零 |
| 断线中止局且不伪胜 | `NotifyClientDisconnected` → `RequestShutdown` | G：`Results.Count == 0`、未进入结果态 |
| 确认退出才恢复 PlayerOnline | `ReapSessions` | F/G：`RestoredWhileProcessAlive == false` |
| 结果只做控制通知、不伪造 BattleReview | `ResultAccepted` 事件 + `NotifyMatchEnded` | F：事件恰一次；冲突不触发 |
| listener 首个消息身份/MAC 成功才占连接；第二连接不得冒用/顶替；重连策略明确 | `HandleInbound` 绑定规则 | D：坏 MAC 不占连接；E：重放第二连接被拒、原连接有效、断开后可重连 |
| 不阻塞整个 host 等 socket | worker 只入队；泵有界 drain | H：半包/粘包/超限在 worker 侧完成，泵只消费队列 |
| 不把秘密写日志 | 全部日志只含 match/ds/port/uid/计数 | 代码审查（`Info`/`Warn` 调用点） |

## 6. 测试证据

```bat
dotnet build Tools\PMDsLobbyTest\PMDsLobbyTest.csproj -c Release --no-incremental   REM exit=0，0 错误
dotnet Tools\PMDsLobbyTest\bin\Release\net8.0\PMDsLobbyTest.dll                     REM exit=0，230 项 / 负向 28 / 0 失败

REM 回归（我改了协调器，A1 门禁必须仍绿）
dotnet build Tools\PMDsControlTest\PMDsControlTest.csproj -c Release
dotnet Tools\PMDsControlTest\bin\Release\net8.0\PMDsControlTest.dll                 REM exit=0，473 项 / 负向 50 / 0 失败

REM 集成编译（**临时输出**，不覆盖 Server 主输出）
dotnet build Server\Server.csproj -c Release -o %TEMP%\pmds-srv-check                REM exit=0（仅既有 NU1701/CS8981 警告）
dotnet build Tools\PMNetLangCheck -c Release                                        REM exit=0
```

| 节 | 覆盖内容 | 项数 |
|---|---|---|
| A | 两局不同端口 / 同 uid 拒绝（宿主预检 + 协调器兜底）/ 缺人整局拒绝 / 端口耗尽回滚 uid 预留 | 28 || B | 引导文件可解码且票据可验签 / 启动参数 exe·port·bootstrap·matchid·dsid·control 一致 / `SetLaunchRequest` 五项校验 | 33 |
| C | 引导发布失败 ⇒ 未启动进程 + 端口与 uid 归还；进程启动失败 ⇒ 回滚；offer 投递失败 ⇒ 不进入 Running、进程死后才归还 | 24 |
| D | 真实 loopback：坏 MAC 不占连接 / 半包 Ready / 就绪才发 offer（逐字段 + 票据验签）/ 重复 Ready 幂等 / 心跳续用 | 22 |
| E | 第二 TCP 连接重放同一合法 Ready 被拒、原连接无损、断开后可重连 | 12 |
| F | 结果接受 + `ResultAck` 验签与字节幂等 / 冲突拒绝 / 公开事件一次 / 预算走完 → `ResultCommitted` / 退出后释放与恢复 | 26 |
| G | 客户端断线中止（无伪胜）/ 宽限期强杀后进程赖着不走仍占用 / 退出后才释放与恢复 / 协调器层「终态未退出不释放」 | 28 |
| H | listener 半包·粘包·超限立即断开 / 连接上限 / 入站队列上限显式失败 / 非 loopback 拒绝 / 未知连接发送返回 false | 21 |
| I | 账本单元语义：原子预留、跨局冲突、重复/非法输入、幂等释放、ReleaseAll | 18 |
| J | 宿主 offer 字段 → **真实** `PMDsEntryCodec` → 严格解码逐字段比对（含票据字节保真、文本 ≤4096） | 18 |

关键负向可达性：坏 MAC 计数与「不推进状态/不发 offer/连接被关」三条断言同节奏；`HijackAttempts` 与「无额外 offer / `ReadyDuplicates == 0`（冒用帧根本没进协调器）」同节奏；`RejectedOverInboundQueue` 与「连接被关、队列占用 ≤1」同节奏。

**由负向用例查出的实现缺陷（已修，具证伪能力）**：宿主最初用 `BeginStart()` 的 `IsAccepted` 判定开局是否成功，但协调器的失败路径（`FailAndRelease`）也会返回 `Applied`（它先推终态再返回）。于是「进程启动失败」会被误报成一次成功的 `SessionStarted`。C2 用例（注入 `TryStart` 失败）稳定复现，改为**按状态判定**（`State == Allocated` / `Starting`）后转绿。

## 7. 未验证事项 / 剩余风险（交给主 Agent / T42）

1. **`ResultPending` 期间对端退出被判 `Failed`（跨组语义差，需定调）—— ✅ 已于 R3-B 主集成修复，见本条末注记**。证据：DS 侧 `PMDsLobbyAgent.HandleResultAck` 在收到匹配的 `ResultAck` 后执行 `SendExited(0)` + `RequestExit(0)`；协调器 `HandlePeerExited` → `ApplyProcessExit(0,"收到 Exited 消息")` → `case ResultPending: Finalize(Failed, "结果确认前进程退出…")`。因此**真实成功局**的终态标签会是 `Failed`，且 `ResultCommitted` 在真实流程里通常到不了。
   - 业务后果**不受影响**：`ResultAccepted` 已触发（结果已记、墓碑已写）、端口与 uid 仅在确认退出后释放、`RestorePlayerOnline` 正常。受影响的只有 `SessionEnded`/`SessionReleased` 携带的状态值（`_r3b_lobby_report` 的调用方需按「是否收到过 `ResultAccepted`」判成局）。
   - 本组**未改**该映射：`ApplyProcessExit` 属 A1 已独立审查过的语义（审查 M2 明确「不得由发送次数推断对端确认」），单方面改成 Exited 会改掉 A1 门禁的 oracle 且需契约层定调。建议二选一（由主 Agent 决定）：① DS 改为等 Lobby 的 `Shutdown`（`BeginShutdownAfterResult` 已发）后再退出；② 契约把「`ResultPending` + 对端**显式** `Exited`」列为成功收尾（对端已主动声明退出，不属于「由发送次数推断」），而纯进程退出仍保持 `Failed`。
   - **✅ 已修复（R3-B 主集成，用户冻结裁决采纳②）**：`PMDsCoordinator.ApplyProcessExit` 新增 `authenticatedPeerExited` 参数，**只有** `HandlePeerExited`（先过 MAC、再过 `MatchId/DsId/Epoch/ProtocolHash` 全等校验）传 `true`。`ResultPending` + 认证显式 `Exited(0)` ⇒ 终态 `Exited`（原因文案「结果已本地受理，且对端经认证显式 Exited(0)」）；**单纯进程提前退出（`Tick` 轮询/宿主报告，无协议证据）** 与显式非 0 退出码仍判 `Failed`。`ResultCommitted` 语义未改；两条路径的端口/名册释放**都**仍以「本地确认进程已退出」为前提，不从控制消息推断进程死亡。
     验证：`Tools/PMDsControlTest` 新增 L 节 33 项（506/0）钉住两条路径与「未认证 `Exited` 被拒」；端到端在 `Tools/PMR3IntegrationTest`（143/0，含「结果已受理但落在确认窗口内的纯崩溃仍 Failed」）。完整证据见 `Docs/plans/_r3b_integration_report.md`。
     因此本条原结论「真实成功局的终态标签会是 `Failed`」**不再成立**：真实成功局现在得到 `Exited`；调用方可继续按「是否收到过 `ResultAccepted`」判成局，也可改按终态 `Exited` 判。
2. **进程是受控替身，不是真实 `HyldDS.exe`**：进程相关结论只代表**控制面接线正确**，不代表真实 Unity DS 能启动。T42 仍需真机证据。
3. **未跑端到端两客户端**：本门禁只到「Ready → offer → 结果 → 退出」，数据面（`PMUdpSessionEndpoint`、`PMR3Runtime` 复制、`Probe/Echo`）属 R3B2/R3B5。
4. **生产网关无独立单测**：`ServerLobbyClientGateway` 的字段映射由 J 节（真实 codec 往返）与 `Server.csproj` 编译共同钉住；`Client.Send` 的端到端投递与客户端 `PMDS1:` 消费属客户端组。
5. **`-control` 未纳入 `SetLaunchRequest` 的严格校验**：宿主始终拼 `127.0.0.1:<真实控制端口>`，但协调器没有「期望控制端点」注入点，故只校验任务点名的 `-port/-bootstrap/-matchid/-dsid` 四项。
6. **宿主未做热重启/跨进程持久化**：端口池与 uid 账本都是进程内内存，Lobby 重启即丢（契约 §3 已声明无数据库、不承诺恰好一次）。
7. **未做真实多局并发压测**：仅覆盖 2 局同进程；端口池容量 99、连接上限 16、入站 4096 帧/4MiB 的有界性由 H 节单点验证。
8. **引导文件按同机同账户 ACL 保护**（有意不做 DPAPI）：若 DS 换成其它账户运行，需自行收紧每局目录 ACL。
9. **`PMDsControlTest` 的编译依赖面**：为不修改 A1 门禁工程，协调器只依赖 `IPMDsPlayerLedger` 接口；`PMDsPlayerLedger` 本体由 A1 门禁之外的工程覆盖（本门禁 I 节）。

## 8. 明确不做（与任务边界一致）

- 未启动旧服务、未常驻任何进程、未关闭用户进程；未编 Server 主输出（用 `%TEMP%` 临时输出）。
- 未执行 `git`/`svn` 提交、未改主计划与契约文档、未改共享 §7 的网络/Unity/PMR3/proto 文件、未递归委派。
- 未新增 JSON 协议、未改 `SocketProto`、未伪造 `BattleReview`、未回退旧链、未借用旧 `BattleManage` 字典给新 DS。
