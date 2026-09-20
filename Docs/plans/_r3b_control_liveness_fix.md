# R3-B 控制面双向 liveness 修复（长局误退出 / 心跳回声风暴边界）

> 触发事实：真实用户用新构建的 `HyldDS.exe` 跑 `Tools/PMR3UnitySmoke` **122/0 通过**，但**长局（>30 秒）必然误退出**：
> `PMDsLobbyAgent` 的「Ready 后零回应」看门狗（`StartupReadyTimeoutMs = 30000`，判据 `_readySent && FramesReceived == 0 && now - _readySentAtMs >= 30000`）
> 会在 Lobby 未下行任何控制帧时把 DS 判为失败 → exit 1。该现象在 `_r3b_real_unity_smoke.md` §7 观察项 3 里已被记录，本轮按主 Agent 的裁决实施修复。
>
> 写边界：`Server/DS/PMDsCoordinator.cs`、`Client/Assets/Scripts/Server/Boot/PMDsLobbyAgent.cs`、
> `Tools/PMDsControlTest/Program.cs`、`Tools/PMR3RuntimeTest/Program.cs`、本文件；
> **外加一处已披露的边界偏离**：`Tools/PMDsLobbyTest/Program.cs`（见 §8.1，两处读取改成「按消息类型读」，未删任何断言）。
> **未改**契约 / 主计划 / `PMDsLobbyHost.cs` / `PMDsControlListener.cs` / `PMNet/**` / `PMR3/**` / Unity 资产；
> 未打开或构建 Unity；未动用户服务与 7777/7778；未 git 提交；未递归委派。

## 1. 目标与范围

| # | 目标 | 落点 |
|---|---|---|
| G1 | **不新增 wire 类型**，用既有 `Heartbeat`（`PMDsControlMessageType.Heartbeat = 3`，已有 body/编解码）建立**双向**控制面 liveness | 协议文件不动 |
| G2 | Lobby（`PMDsCoordinator`）在**合法 Ready / 合法 Heartbeat**后回一条**本局密钥签名**的 `Heartbeat`，且**有节流**（不形成心跳回声风暴） | `PMDsCoordinator.cs` |
| G3 | DS（`PMDsLobbyAgent`）**允许**来自已验 MAC + 局/epoch/hash 的 `Heartbeat` 下行，并记录「最后可信接收时间」 | `PMDsLobbyAgent.cs` |
| G4 | `StartupReadyTimeoutMs` **只用于「首个 Ready 响应」等待**；进入运行期后改用**独立、明确**的 liveness 超时 | `PMDsLobbyAgent.cs` |
| G5 | **持续上行发送成功不算 Lobby 存活**：运行期看门狗只由「收到的可信下行」续命 | `PMDsLobbyAgent.cs` |
| G6 | DS **不因收到 Heartbeat 立即回 Heartbeat**：本机心跳只按自身定时节奏发 | `PMDsLobbyAgent.cs` |
| G7 | Coordinator **不允许**未启动（`Allocated`/`Starting`）或**错身份**/坏 MAC 的 `Heartbeat` 改变状态；**回复前的 MAC/身份/状态校验一律保持** | `PMDsCoordinator.cs` |
| G8 | 既有门禁的计数对照若被新增回复影响，**按消息类型读**（不删保护、不降断言强度） | `PMDsControlTest` / `PMR3RuntimeTest` / `PMDsLobbyTest`（§8.1） |

## 2. 非目标

- 不改控制协议线格式、不加消息类型、不改 protobuf、不加 JSON。
- 不改 `PMDsLobbyHost` / `PMDsControlListener` / `PMDsPlayerLedger` / `PMDsProcess`。
- 不改契约 §3 的**超时值**（启动 30s / 心跳 15s / 结果确认 1s / 收尾 10s）。
- 不新增传输层 RTO / 不改 `PMNet/**`（那一层由 `_r3b_transport_liveness.md` 负责，且已明确「加 RTO 要先在契约开口子」）。
- 不修 Unity 构建、不重打 `HyldDS.exe`（本机不代跑 Unity；真实 DS 验证由用户重 build 后执行）。
- 不改 `Tools/PMR3IntegrationTest`、`Tools/PMR3UnitySmoke`（未越界，且已实测它们不被本改动打破）。

## 3. 根因（源码级事实）

1. **`PMDsLobbyAgent.HandlePayload` 的 `switch` 只接受 `ResultAck` / `Shutdown` / `Error`**，
   其它类型一律 `FramesRejected++` + `Fail(...)`。因此 Lobby 若下发 `Heartbeat`，DS 会直接失败。
2. **`FramesReceived` 只在「MAC + 身份都通过并被受理」时自增**，而当前协议下 Lobby 只在**结果受理后**才下行 `ResultAck`。
   于是运行期 `FramesReceived` 长期为 0，30 秒看门狗必然触发 → 这就是长局误退出的**直接原因**。
3. **`StartupReadyTimeoutMs` 被复用于运行期看门狗**（`else if (FramesReceived == 0L)`），语义被撑大。
4. **Coordinator 从不主动下行任何活性帧**（只有结果/关闭/错误），所以 (2) 不可能靠现状自愈。
5. **附带发现的潜在 1:1 竞态**：DS 心跳间隔（15s）与 Coordinator `HeartbeatTimeout`（15s）**相等**，
   而 DS 心跳由宿主帧驱动（「首个 `now >= _nextHeartbeatAtMs` 的帧」才发，可晚一帧），
   于是「Lobby 的下一次 Tick（宿主 200ms 节拍）恰好落在 15s 到期而心跳尚未到达」的窗口真实存在
   → 长时间运行会周期性把健康会话判成心跳超时。本轮按「发送间隔必须显著小于超时」消除（3:1）。

## 4. 设计（精确到接口）

### 4.1 DS 侧 `PMDsLobbyAgent`（最终常量）

| 项 | 值 | 说明 |
|---|---|---|
| `StartupReadyTimeoutMs` | **30000**（不变，语义收窄） | **只**用于「发出 Ready 等待首个可信响应」这一段 |
| `ReadyRetransmitMs` | 1000（不变） | 首个可信回应到达前幂等重发 Ready |
| `HeartbeatIntervalMs` | **15000 → 5000** | DS→Lobby 心跳**发送间隔**；对 Coordinator 的 15s 超时形成 3:1 余量 |
| `RuntimeLivenessTimeoutMs` | **15000**（新） | **运行期**「Lobby 可信下行」看门狗 = 3 × 心跳间隔 ⇒ 容忍连续 2 次丢失 |
| `_lastTrustedRxMs` / `LastTrustedReceiveMs` | 0 = 尚无（新） | 只有**通过 MAC + 身份并受理**的帧才刷新它 |
| `HeartbeatsReceived` | 0（新） | **下行**心跳计数，与上行 `FramesSent` 严格分开 |

`Pump` 三段式：

```
未发 Ready                → 场景就绪门（StartupReadyTimeoutMs 只在这里判「场景迟迟不就绪」）
已发 Ready 但尚无可信下行  → 首个 Ready 响应等待：StartupReadyTimeoutMs + 1s 幂等重发 Ready
已有可信下行（运行期）     → now - _lastTrustedRxMs >= RuntimeLivenessTimeoutMs ⇒ Fail
                            （原因文本明确写「持续上行发送成功不代表 Lobby 存活」）
```

`HandlePayload`：MAC/身份通过后与 `FramesReceived++` 同点写 `_lastTrustedRxMs`；
新增 `case Heartbeat`：要求 `body != null` 且 **`_readySent == true`**（Ready 之前下发心跳 = 时序/方向不符 → fail-closed），
计入 `HeartbeatsReceived` 后**直接返回，不回心跳**。

### 4.2 Lobby 侧 `PMDsCoordinator`（最终常量）

| 项 | 值 | 说明 |
|---|---|---|
| `PMDsCoordinatorOptions.HeartbeatReplyMinInterval` | **1000ms**（新，`Validate` 拒绝 ≤0） | 两次心跳应答之间的最小间隔 ⇒ 有节流 |
| `PMDsSessionCounters.HeartbeatReplies` | 新 | 真正发出的应答数 |
| `PMDsSessionCounters.HeartbeatRepliesThrottled` | 新 | 被节流压掉的次数（证明限流真的生效） |
| `HeartbeatTimeout` | 15s（不变） | DS 5s 心跳 ⇒ 3:1 余量 |

- `HandleReady`：**状态已推进为 Ready 且 `ReadyAddressPublished` 已发出之后**调用 `ReplyHeartbeat(...)`；
  重复 Ready（`Duplicate`）**不**再回。
- `HandleHeartbeat`：**状态门通过并刷新 `_lastHeartbeatMilliseconds` 之后**调用 `ReplyHeartbeat(...)`；
  被拒（`RejectedState` / `RejectedIdentity` / `RejectedMac`）**一律不回、不刷新任何 liveness 时钟**。
- 应答经既有 `EmitControl(ControlMessageOut, payload, Heartbeat, reason)` 出栈，宿主（未改动）按既有路径投递；
  未绑定连接时进 `PendingOutbound`（有界 16），绑定后 `FlushPendingOutbound` 补发。
- **不可能回声风暴**：Coordinator 只对 Ready/Heartbeat 回；DS **不**对收到的 Heartbeat 回；Coordinator 侧再叠一层时间节流。

## 5. 实施步骤

| 步 | 内容 | 状态 |
|---|---|---|
| S1 | 本文件落盘（计划 + 测试集） | DONE |
| S2 | `PMDsLobbyAgent.cs`：三段式 `Pump` + `RuntimeLivenessTimeoutMs` + `_lastTrustedRxMs`/`HeartbeatsReceived` + 接受 Heartbeat + 心跳间隔 5s | DONE |
| S3 | `PMDsCoordinator.cs`：`HeartbeatReplyMinInterval` + 两个计数 + `ReplyHeartbeat`，在 Ready/Heartbeat 接受路径回 | DONE |
| S4 | `Tools/PMR3RuntimeTest/Program.cs`：H 段（H1–H36）+ G 段常量更新 + `FakeLobby.AutoReplyHeartbeats` 替身 | DONE |
| S5 | `Tools/PMDsControlTest/Program.cs`：M 段（M1–M50） | DONE |
| S6 | `Tools/PMDsLobbyTest/Program.cs`：两处「读下一条帧」→「按消息类型读」（**边界偏离，见 §8.1**） | DONE（已披露） |
| S7 | build0 → run：Control / Runtime / Integration / Lobby / ClientCheck / GlueCheck（+ Server 主工程编到临时输出） | DONE |
| S8 | 负向注入 A–D（备份 → 注入 → 跑 → 还原 → 核 sha256） | DONE |
| S9 | 本文件补齐实测证据与诚实边界 | DONE |

## 6. 测试验收集

| ID | 实施映射 | 场景 | 执行者 | 方法/命令 | 预期结果 | 状态 | 证据 |
|---|---|---|---|---|---|---|---|
| T1 | S2 | >30s 长局保持（注入时钟 + 真实 loopback TCP，Lobby 按 Ready/Heartbeat 回心跳） | AI | `PMR3RuntimeTest` H1–H11 | 虚拟 65s 内不失败、未请求退出、`HeartbeatsReceived=13`、上行心跳有界 12、Ready 只 1 条 | PASS | §7.2 |
| T2 | S2 | 丢下行可信流量仍退出 | AI | `PMR3RuntimeTest` H28–H36 | 到期即失败 exit 1，原因含「运行期」，实际经过 15000ms | PASS | §7.2 |
| T3 | S2 | 坏 MAC 不续命 | AI | `PMR3RuntimeTest` H19–H27 | `LastTrustedReceiveMs=0`、`FramesReceived=0`、`MacFailures=1`、原因含「验签」 | PASS | §7.2 |
| T4 | S2 | 合法 Heartbeat 不影响 ResultAck 与退出 | AI | `PMR3RuntimeTest` H12–H18 | 心跳流中 ResultAck 仍被接受、退出码 0、发出 Exited | PASS | §7.2 |
| T5 | S2 | DS 不回声 + 双方发送有界 | AI | `PMR3RuntimeTest` H7–H11 | 收到 13 条下行心跳，只上行 12 条（回声会 ≥24） | PASS | §7.2 |
| T6 | S3 | 合法 Ready → 回一条签名 Heartbeat | AI | `PMDsControlTest` M1–M13 | 应答 1 条、类型/签名/身份/PlayerCount=4 正确、状态与发布次数不变 | PASS | §7.1 |
| T7 | S3 | 合法 Heartbeat → 回一条；同刻连发只回一条（节流） | AI | `PMDsControlTest` M14–M21 | 跨窗 +1；同刻 8 连发 0 条新应答且 `Throttled=8` | PASS | §7.1 |
| T8 | S3/G7 | 未启动（Allocated）Heartbeat 变不了状态、不回 | AI | `PMDsControlTest` M32–M36 | `RejectedState`、`Heartbeats=0`、`HeartbeatReplies=0`、不发布地址 | PASS | §7.1 |
| T9 | S3/G7 | 错身份（MAC 合法）Heartbeat 不续命、不回 | AI | `PMDsControlTest` M37–M43 | `RejectedIdentity`、应答仍只有 Ready 那 1 条、15s 仍 `TimedOut` | PASS | §7.1 |
| T10 | S3/G7 | 坏 MAC Heartbeat 不续命、不回 | AI | `PMDsControlTest` M44–M50 | `RejectedMac`、应答仍 1 条、15s 仍 `TimedOut` | PASS | §7.1 |
| T11 | S3 | 应答不改变 Result 语义（按消息类型读） | AI | `PMDsControlTest` M22–M26 | `ResultAck` 仍恰好 1 条、`ResultAccepted=1` | PASS | §7.1 |
| T12 | S3 | Coordinator 发送有界（60s @1Hz 心跳） | AI | `PMDsControlTest` M27–M31 | 应答 61 ≤ `elapsed/interval + 1`，`Throttled=0` | PASS | §7.1 |
| T13 | S2/S3 | 既有门禁回归 | AI | build+run：Control / Runtime / Integration / Lobby（+ Server 主工程） | 全部 build 0 错误、run 退出码 0 | PASS | §7.3 |
| T14 | S2/S3 | 编译边界（Unity 客户端层 / 胶水桩 / smoke 工具） | AI | `PMClientCheck`、`PMUnityGlueCheck`、`PMR3UnitySmoke`（仅 build） | 退出码 0 | PASS | §7.3 |
| T15 | S2 | 注入 A：运行期看门狗失效必须被抓出 | AI | 备份 → 注入 → 跑 → 还原 → sha256 | 4 项转红；还原后 sha256 一致 | PASS | §7.4 |
| T16 | S2 | 注入 B：DS 不接受 Heartbeat 必须被抓出 | AI | 同上 | 12 项转红（无未捕获异常）；sha256 一致 | PASS | §7.4 |
| T17 | S3 | 注入 C：取消节流必须被抓出 | AI | 同上 | 3 项转红；sha256 一致 | PASS | §7.4 |
| T18 | S3/G7 | 注入 D：回复早于校验必须被抓出 | AI | 同上 | 5 项转红（含未启动/错身份/坏 MAC 三处「不回」）；sha256 一致 | PASS | §7.4 |
| T19 | S2/S3 | 真实 Unity DS 长局不再误退出 | 用户 | **重 build** `HyldDS.exe` 后跑长局 + `PMR3UnitySmoke` | 长局不再 exit 1 | PENDING_USER | §8.2 |

## 7. 实测证据

工作树：`D:\UGit\hyld-master`；全部命令为「先 build（看退出码与错误数），再 run（看退出码）」。
改动文件 sha256（最终态）：

```
Server/DS/PMDsCoordinator.cs                          2e022242afba9d86475dcf1e11cfcb95abc6a08539f735359ba9fe1cb4f6f78d
Client/Assets/Scripts/Server/Boot/PMDsLobbyAgent.cs    6d92b9c92cee2990350a029d0518d7de0e3eeaf053087890e0aa804b89ab75d6
Tools/PMDsControlTest/Program.cs                      6a113d0f211a07f091fdcd07c133862c19051c9c5c216ebda6bfaa7fdc11eed2
Tools/PMR3RuntimeTest/Program.cs                      ffd9a2ea2ab863cecd52a85b29dc17e463340bd8b0d22fe16f07c11c07a8b249
Tools/PMDsLobbyTest/Program.cs                        a38b6d56f02011d003e770f194e1885bd95e68a56c7330770932c7611338619e
```

### 7.1 `Tools/PMDsControlTest`（协调器 / 控制协议 / 真实 loopback TCP）

```
dotnet build Tools\PMDsControlTest\PMDsControlTest.csproj -c Release   → 退出码 0，0 警告 0 错误
dotnet Tools\PMDsControlTest\bin\Release\net8.0\PMDsControlTest.dll    → 退出码 0，556 项 / 0 失败（含负向输入 54）
```

506 → 556（新增 M 段 50 项，`R3A1 149 / R3A2 407`）。关键实测量：

- 合法 Ready → 恰好 1 条 Heartbeat 出栈；`ds.Signer.Verify` 通过；MatchId/DsId/Epoch/ProtocolHash 四元组一致；`PlayerCount = 4`（名册人数）。
- 重复 Ready → `Duplicate`，心跳应答仍为 1（幂等语义未变）。
- 合法 Heartbeat（跨 1s 窗口）→ 应答 +1；同刻连发 8 条 → 应答 **+0**、`HeartbeatRepliesThrottled = 8`；再过 1s → 应答恢复 +1。
- 心跳应答之后受理 Result → `ResultAck` 仍**恰好 1 条**（按 `ControlType` 读）、`ResultAccepted = 1`。
- 60s @1Hz（60 条心跳）→ `HeartbeatReplies = 61`，上界 `60/1 + 1 = 61`，`Throttled = 0`（频率恰等窗口时不压）。
- `Allocated`（未启动）心跳 → `RejectedState`，`Heartbeats = 0`、`HeartbeatReplies = 0`、不发布地址。
- 14.999s 存活 → 错身份心跳（MAC 合法）`RejectedIdentity`、应答仍 1 条 → 15.000s `TimedOut`（被拒帧**没有**续命）。
- 同一场景换成坏 MAC 心跳 → `RejectedMac`、应答仍 1 条 → 15.000s `TimedOut`。

### 7.2 `Tools/PMR3RuntimeTest`（DS 侧 `PMDsLobbyAgent`，真实 loopback TCP + 注入时钟）

```
dotnet build Tools\PMR3RuntimeTest\PMR3RuntimeTest.csproj -c Release  → 退出码 0，0 警告 0 错误
dotnet Tools\PMR3RuntimeTest\bin\Release\net8.0\PMR3RuntimeTest.dll   → 退出码 0，171 项 / 0 失败
```

134 → 171（新增 H 段 36 项 + G 段 +1 项常量）。关键实测量（1s/步；第三组 500ms/步）：

- **长局（H3–H11）**：虚拟 **65000ms**（跨过旧 30s 看门狗两次以上）后 `IsFaulted=false`、未请求退出；
  `HeartbeatsReceived = 13`（Ready 应答 1 + 心跳应答 12）；`LastTrustedReceiveMs = 63000`；
  DS 上行心跳 **12** 条（若回声会 ≥24）；Ready 只发 **1** 条（旧行为会发 ~30）。
- **丢下行（H29–H36）**：先建立可信下行，再关掉应答 → 到期失败、`exitCode=1`，
  原因 `运行期 15000ms 未收到 Lobby 可信控制帧（持续上行发送成功不代表 Lobby 存活）`，
  实测经过 **15000ms**；期间 `FramesSent` 持续增长（上行一直成功）且失败时 TCP **仍连着**。
- **坏 MAC（H20–H27）**：全程无合法帧 → 基线 `LastTrustedReceiveMs=0`；篡改帧到达后
  `LastTrustedReceiveMs` 仍 **0**、`FramesReceived` 仍 **0**、`MacFailures=1`、原因 `控制帧验签失败：MacMismatch`。
- **心跳不影响结果（H12–H18）**：心跳流中匹配 ResultAck 仍被接受、退出码 0、发出 Exited、全程未失败。
- **常量（G）**：`StartupReadyTimeoutMs=30000`、`RuntimeLivenessTimeoutMs=15000`、`HeartbeatIntervalMs=5000`、`ResultRetransmitMs=1000`。

### 7.3 回归（同一工作树；先 build 退出 0 再 run）

| 门禁 | build | run | 退出码 | 说明 |
|---|---|---|---|---|
| `Tools/PMDsControlTest` | 0 错误 | **556 / 0** | 0 | 506 → 556 |
| `Tools/PMR3RuntimeTest` | 0 错误 | **171 / 0** | 0 | 134 → 171 |
| `Tools/PMR3IntegrationTest` | 0 错误 | **143 / 0** | 0 | **未改一行**；G22「Ready 阶段尚未下行任何控制帧」仍为 0（见 §7.3.1） |
| `Tools/PMDsLobbyTest` | 0 错误 | **230 / 0** | 0 | 检查数不变（26 项 F 全在）；仅两处读取改「按消息类型读」（§8.1） |
| `PMClientCheck`（库，仅 build） | 0 警告 0 错误 | — | 0 | 客户端玩法层全量（含 `Scripts/Server/**`） |
| `PMUnityGlueCheck`（库，仅 build） | 0 警告 0 错误 | — | 0 | Unity 胶水桩 |
| `PMR3UnitySmoke`（**仅 build，未 run**） | 0 警告 0 错误 | — | 0 | 用户 smoke 工具仍可编译；**未**用旧 `HyldDS.exe` 跑（§8.2） |
| `Server/Server.csproj`（`-o /tmp`，不覆盖运行中产物） | 0 错误 / 8 既有警告 | — | 0 | 4×NU1701 + 4×CS8981，与 `_r3b_transport_liveness.md` 记录基线一致 |

#### 7.3.1 为什么 `PMR3IntegrationTest` 的 G22 没有被打破（可复核的事实）

G22 断言「Ready 阶段 Lobby 尚未下行任何控制帧」，而本轮**确实**在 Ready 后立即下发一条心跳应答。它仍为 0 的原因是**泵顺序**：

1. `PumpOnce()` 次序为 `Clients → Ds.Pump → Host.PumpOnce`（`Tools/PMR3IntegrationTest/Program.cs:848-870`）；
2. DS 的 `FramesReceived` **只在 `Ds.Pump`（主线程）里自增**，后台接收线程只把原始字节入队；
3. Ready 被接受、offer 发布、心跳应答入队全部发生在**同一次 `Host.PumpOnce`** 里；
4. `PumpUntil` 在**同一轮**条件成立后立即返回，此后没有再调用 `Ds.Pump`，所以 G22 读到的仍是 0。

### 7.4 负向注入（先备份 → 注入 → 跑 → `finally` 还原 → 核 sha256）

| 注入 | 结果 | 还原核对 |
|---|---|---|
| A：`Pump` 运行期分支加 `false &&`（等于取消运行期看门狗） | `PMR3RuntimeTest` **167 / 4**（退出码 1）：H30/H31/H32/H34 转红 | sha256 一致 |
| B：删掉 DS 的 `case PMDsControlMessageType.Heartbeat` 分支 | `PMR3RuntimeTest` 退出码 1，**12 项干净 FAIL**（H4/H5/H7/H11/H12/H13/H15/H16/H18/H32/H33/H35），**无未捕获异常** | sha256 一致 |
| C：删掉 `ReplyHeartbeat` 的节流判断 | `PMDsControlTest` **553 / 3**（退出码 1）：M19/M20/M21 转红 | sha256 一致 |
| D：在 MAC/身份校验**之前**插入一次 `ReplyHeartbeat` | `PMDsControlTest` **551 / 5**（退出码 1）：M20/M29/M35/M41/M48 转红（含「未启动/错身份/坏 MAC 都不回」三条） | sha256 一致 |

> 注入 B 首次运行时暴露了**我自己测试代码**的一个健壮性缺陷（前置失败后仍用 `ResultId=0` 构造 ResultAck 会抛未捕获异常）。
> 已修成「前置不成立就不发那条 Ack，断言如实变红」；上表 12 项即修复后的干净结果（对应 `_r3b_transport_liveness.md` 里同样的修法约定）。

## 8. 未验 / 诚实边界

### 8.1 已披露的写边界偏离：`Tools/PMDsLobbyTest/Program.cs`

- **为什么必须动**：该门禁在 F 段用 `TryReadPayload` 读「Ready 之后的下一条下行帧」并断言它就是 `ResultAck`。
  本轮按裁决在合法 Ready 之后**必然**多下发一条心跳应答（这是任务明确要求的行为），于是那两次读取读到的是心跳，
  3 条断言（`aсk 类型是 ResultAck`、`ResultAck 回带同一 ResultId`、`重复结果的 Ack 字节完全一致`）转红。
- **实际改法（最小）**：新增 `TryReadPayloadOfType(...)`，在 F 段的两处调用改成「按消息类型读到 `ResultAck` 为止（有界 8 条，其余按类型丢弃）」。
  **没有删任何断言、没有放宽任何超时、没有改被测行为**；改动后 `PMDsLobbyTest` 仍是 **230 项 / 0 失败**（检查数与改动前一致）。
- 之所以动它而没有「只上报」：本条偏离是**任务要求的必然结果**，且任务原文已授权「既有计数对照要按消息类型读，不能删保护」；
  留一个已知可修的红色既有门禁，比一处 5 行的按类型读取更差。若主 Agent 认为不该越界，单独 `git checkout -- Tools/PMDsLobbyTest/Program.cs`
  即可完整回退（它与本修复的其余部分无耦合）。

### 8.2 仍然是 `PENDING_USER` 的部分（`T19`）

1. **`PMDsLobbyAgent.cs` 编进 `HyldDS.exe`**：本轮**没有**打开 Unity、**没有**重打 DS 产物（本机不代跑 Unity 构建）。
   用户**必须重 build** `HyldDS.exe` 后才能验证真实长局；拿旧产物跑 smoke 只能复现旧行为，**不能**作为本修复的证据。
2. **跨机 / 真实弱网未验**：本轮全部为注入时钟 + 真实 loopback TCP。
3. **DS→Lobby 心跳间隔 15s → 5s 是发送节奏变化**，契约 §3 的 15s **超时值**未动；
   1:1 的旧比例本身是潜在误判（§3 第 5 条），本轮把它变成 3:1 余量。
4. **DS 运行期超时 15s < 旧 30s**：这是有意的 —— 运行期「30 秒不知道对端死活」本身就是口径错误；15s 仍是 3 × 心跳间隔。

## 9. 明确不做

未改契约 / 主计划 / 宿主 / 监听器 / 传输层 / 生成物；未新增 wire 类型；未打开或构建 Unity；未重打 DS 产物；
未启动或停止用户服务；未 git/svn 写操作；未递归委派；未删除任何既有断言。
