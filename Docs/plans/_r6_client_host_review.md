# R6-C 客户端宿主（PMClientSessionHost）+ 只读 HUD（PMUnityCombatHud） —— 独立对抗复核 / 窄修复

> 任务类型：**comprehensive**（独立对抗复核 R6-C 客户端宿主与只读 HUD，并在**允许边界内**修掉已证实缺陷）
>
> 只读输入（按委派顺序，全文读完）：`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`（全文）
> → `Docs/plans/net-r6-combat-contract.md`（全文，含尾段「B 驱动冻结补充」「C 宿主接线冻结」）
> → `Docs/plans/_r6_client_host_report.md`（全文） → `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`（全文）。
>
> 直读的实现面（用于验证每一条「事实」）：`PMClientSessionHost.cs`（全文 3280→3333 行）、
> `PMUnityCombatHud.cs`（全文）、`PMR6CombatDriver.cs`（`TryAttack` / `IsAliveForAttack` / `Pump` /
> `ApplyInbound*` / `HandleAttackResult` / `HandleMatchResult` / `IsKnownWinner` / `HandleResultAck` /
> `FlushState` / `FlushClientResultAck` / `PublishStateIfDirty` / `SendMatchResult` / `TickModel` /
> `LocalOwner` / `TryGetStoredMatchResult` / `MatchResultsReceived` / `EnqueueMatchResult` / `Dispose` / `Describe`）、
> `PMUdpSessionEndpoint.cs`（全文：`Pump` / `RaiseFailed` / `ClientFailed` / `IsDisposed` /
> `CheckClientSessionAfterUpdate` / `ClientHandshakeTick` / `ActivateClient` / `Dispose`）、
> `PMNetSessionBridge.cs`（`SendRpc` / `ResolveRpcTargets`）、`PMRpc.cs`（`ShouldCallRemoteFunction` 归属语义）、
> `PMR3Player.cs`（战斗声明段 / `ClientCombatMatchResultV1` 收侧 / `ClientCombatAttackResultV1` 收侧 /
> `PublishCombatState` 权限门 / `ServerCombatResultAckV1`）、`PMR3Runtime.cs`（`SendRemoteRpc` / 生成接缝）、
> `PMCombatWeaponPlanner.cs`、`PMCombatContracts.cs`、`PMProjectileCandidateCollector` 调用点、
> `PMR4MovementDriver`（`Freeze`/`Tick`/`Advance` 语义）、`PMProjectileDiagnosticConfig`（`MuzzleOffsetM`）、
> `Tools/PMClientCheck/*`、`Tools/PMUnityGlueCheck/*`（含桩件）；并只读扫过 `Tools/PMR6NetworkTest`、
> `Tools/PMR3IntegrationTest`、`Tools/PMR4IntegrationTest` 里对宿主/驱动的引用面。
>
> **未修改** R6 driver / R5 driver / core（PMCombat）/ DS 宿主（`PMDsSessionHost.cs`）/ 任何 `Tools/**` / 契约与主计划 /
> 生成物 / 锁文件 / csproj / 任何 `.meta`。**未**启动 Unity、**未**执行 git/svn 写操作、**未**提交、**未**递归委派。
> 本报告是本次**唯一**文档写入（`Docs/plans/_r6_client_host_review.md`）。

---

## 0. 结论摘要

| # | 主侧提出的疑点 | 复核结论 | 处置 |
|---|---|---|---|
| A1 | `HasKnownTerminalOutcome` 类判据返回 `combat.MatchResultsReceived > 0`，会把**已被拒绝/丢弃的 malformed terminal 包**当成可信结束 | **确认缺陷**（且失效模式比主侧描述更严重：不是「误判胜利」而是**静默挂死**） | **已修**：删掉该判据，只用 `TryGetStoredMatchResult` / `TerminalResultObserved`，见 §2.1 / §3.1 |
| A2 | 正常 same-frame「结果 + 断连」必须先处理已排队合法消息再定性，不能新造 race | **成立**，原实现靠「入队计数」来争取这一帧，语义错但意图对 | **已修**：改为**先跑本帧 R6.Pump 取出结果**再定性，见 §3.1 |
| A3 | `TerminalResult` 的 ACK 是否真的 Flush 发送才「正常退出」 | **部分成立**：ACK 只在端点可用时经 `R6.FlushState` 发；端点已关时按契约**跳过**；正常退出还可能是 7000ms 有界退场（此时并不保证 ACK 已发出，契约原文即如此） | 判定成立，**未改**（改动会违反契约 D2/D3），见 §2.3 |
| A4 | 队列/Endpoint Failed 后合法未处理消息不会被丢掉 | **一半成立**：端点失败已入队的合法结果**原实现会保留但永远不处理**（挂死）；队列满时的丢弃在 driver 侧（越界，已记录） | 端点侧**已修**（同 A1）；队列侧**记录**，见 §2.4 / §5.3 |
| A5 | R6 Faulted 的冲突结果是否被静默当成胜利 | **不成立**（无静默胜利）：冲突 → driver `Fault(ConflictingOutcome)` 且**不改写**已存结论；宿主只发布**第一份通过校验**的结论 | 判定为「安全」，**未改**，见 §2.5 |
| A6 | 终局 HUD 能 survive 正常释放、下一 Enter/Stop 清、无 static 事件泄漏、旧对象 `OnDestroy` 不会关掉新局 | **全部成立**（含 R6-C 报告里的 D9/D10 两条延迟销毁竞态） | 判定成立，**未改**，见 §2.6 |
| A7 | `IsActive` / `LastError` 不能留下与 normalResult 矛盾的旧 Fail | **成立**（正常退场后 `_active == null` ⇒ 两者都干净）；但发现一个**残余限制**：`Fail` 后会话无自动退场路径 | 判定成立 + 限制**记录**（越界：属 R4/R5 既有失败语义），见 §2.7 / §5.2 |
| A8 | 自己 Hero/Team 确认时机 firstCreate / OnRep 都支持 | **成立**（`OnPlayerReplicated` 立即核一次 + 每帧 `UpdateCombatReplicationState` 再核，两条时机都覆盖） | 判定成立，**未改**，见 §2.8 |
| A9 | `MaxHp` 默认 0 不误 Fail | **成立**（`MaxHp <= 0` 只进「未就绪/不开火」，不 `Fail`） | 判定成立，**未改**，见 §2.9 |
| A10 | F/G 边沿 one-Update、与 `FireInterval` 两按钮同帧不 double | **成立**（同帧最多 1 次 `TryAttack`）；另有一处**行为观察**：同帧同时按下 F+G 时 G 胜出、F 被丢弃 | 判定成立 + 观察**记录**，见 §2.10 |
| A11 | `predictionMs` 硬编码 100 是诊断遗留，不得谎称 RTT 自适应 | **成立**（源码注释已诚实标注；本报告如实记录边界） | 判定成立，**未改**，见 §2.11 |
| A12 | death/endedFreeze 不是全局冻结、不误伤其它 player | **成立**（死亡只冻对应 rig；终局才全局冻，且是契约要求） | 判定成立，**未改**，见 §2.12 |
| A13 | Candidates 只 owner+origin、非 dead enemy | **成立**（上行只收 `ClientPredicted` + 本 owner；死者/同队（两侧队伍已知时）本地先滤，DS 仍终判） | 判定成立，**未改**，见 §2.13 |
| A14 | 资源只读、HUD 不写复制字段 | **成立**（客户端唯一写复制字段的路 `PublishCombatState` 只在 `_isServer` 分支被调） | 判定成立，**未改**，见 §2.14 |
| A15 | `LastAttackResult` 只计数没 reason，导致 HUD 显示假拒绝 | **部分成立**：HUD **不会**显示假拒绝（`LastAttackError` 只在本地真拒绝时写、成功即清空），但它**永远显示不出 DS 侧真拒绝原因**（driver 无公开 accessor） | 判定为「API 缺口」而非「假拒绝」，**未改**（越界），见 §2.15 / §5.4 |
| B1 | （复核中自行发现）HUD 的 `[就绪]` 与宿主真实开火门不一致 | **确认缺陷**（终局结果已到、复制位未到的帧里会一边显示「就绪」一边显示「结果：胜/负」） | **已修**，见 §3.2 |
| B2 | （复核中自行发现）`Enter` 构造失败路径漏关 UDP 端点（socket 泄漏） | **确认缺陷**（`ReleaseSession` 只把 `Endpoint` 置 null，不 Dispose） | **已修**，见 §3.3 |

**一句话**：主侧指出的核心问题（用「收过结果消息的计数」当可信终局判据）**确认成立**，且其真实后果是**真实断连下会话既不失败也退不出去（`IsActive` 仍为真、运动不再冻结）**；已按「先取出已到达的合法结果、再只用『已保存/已验证』定性」修复，并顺带修掉两个同区域的小缺陷（HUD 就绪矛盾、失败路径 socket 泄漏）。

---

## 1. 事实基线（先把「真实代码」钉住，再谈判定）

### 1.1 一帧的真实次序（`PumpActive`，`PMClientSessionHost.cs:1025` 起）

```
Faulted?  → 终局已落定则 EndSessionNormally，否则 FrozenWallClockPump
Endpoint == null || IsDisposed? → 终局已落定则 EndSessionNormally，否则 Fail
TerminalResultObserved && 已过 TerminalExitGraceMs(7000) → EndSessionNormally（有界退场）
Physics.SyncTransforms()
endpoint.Pump(nowMs, nowUnixSeconds)      ← 入站 drain + bridge.Update(RPC 派发/入队) + 客户端连接自检
                                          ← 期间可能同步抛 Failed 事件（→ OnEndpointFailed）
Faulted? → FrozenWallClockPump
Endpoint.ClientFailed? → 【本次改动区】见 §3.1
UpdateCombatReplicationState()            ← VerifyOwnerIdentity / CombatDead 冻结 / MatchEnded 全局冻结
SampleCombatInputEdges()                  ← F / G 每 Update 各采样一次
R6.Pump(nowMs)                            ← 处理入站：上行攻击(DS) / 攻击裁决 / 比赛结果 / 结果 ACK；AP 侧推进 core 时钟
终局闭环（TryGetStoredMatchResult）→ OnTerminalResult → R6.FlushState(ACK) → UpdateCombatHud → 条件性 EndSessionNormally → return
PumpMovement() → PumpProjectiles(含 TryCombatAttack + R5 子步) → R6.FlushState(nowMs) → 探针 → UpdateCombatHud
```

关键不变量：**`nowMs` 全帧只算一次**（`Time.realtimeSinceStartup * 1000f` 截断为 long），同时喂
`endpoint.Pump` / `R6.Pump` / `R6.FlushState` / 每个 `R5.Pump`；`R5` 的推进量由 `stepMs` 单独表达。

### 1.2 「可信终局」在代码里的唯一真值链

| 层 | 真值 | 位置 |
|---|---|---|
| 传输/RPC | `ClientCombatMatchResultV1` **只发给该对象 owner 的连接** | `PMNetSessionBridge.ResolveRpcTargets` 的 `PMRpcKind.Client` 分支：`ResolveOwnerConnection(target)` |
| 客户端收侧 | 客户端侧**不额外判归属**（"能收到就说明服务端发的"） | `PMRpc.ShouldCallRemoteFunction`（D-R0-42 口径） |
| 玩家对象 | 无 driver 时计 `CombatMatchResultDiscardedCount` 后丢弃 | `PMR3Player.ClientCombatMatchResultV1` |
| R6 driver | 入队瞬间 `MatchResultsReceived++`；`Pump` 时逐条 `HandleMatchResult`：owner 不符**静默丢**，`outcomeId==0` / 未知胜方 / 结论冲突 → `Fault`；通过则 `_clientResultStored = true` | `PMR6CombatDriver.cs:1085` / `:1380` / `:1536-1579` |
| 宿主 | `TryGetStoredMatchResult` 为真 ⇒ `OnTerminalResult` | `PMClientSessionHost.cs:1104`（终局闭环） |

**结论**：`MatchResultsReceived` 是「**入站队列计数**」（在 `EnqueueMatchResult` 里自增，`HandleMatchResult` 之前），
它与「可信终局」之间隔着一整层校验；而通过校验的**唯一**信号是 `TryGetStoredMatchResult`（或宿主已落定的 `TerminalResultObserved`）。
这正是主侧指出的语义错误。

---

## 2. 逐条判定（证据 → 结论）

### 2.1 A1：`MatchResultsReceived > 0` 不是可信终局判据 —— **确认缺陷（P1）**

原实现（本次已删）：

```csharp
private static bool IsTerminalResultPendingOrStored(Session session)
{
    ...
    if (combat.TryGetStoredMatchResult(out outcomeId, out winnerTeamId)) { return true; }
    return combat.MatchResultsReceived > 0;   // ← 入队计数，含被拒/被丢的包
}
```

它被两处使用：`OnEndpointFailed`（提前 return，**不**判失败）与 `PumpActive` 的 `ClientFailed` 分支
（`!IsTerminalResultPendingOrStored` 才 `Fail`）。逐案推导「计数 >0 但未保存」的四种来源：

| 来源 | `HandleMatchResult` 行为 | `IsTerminalResultPendingOrStored` | 原实现最终结果 |
|---|---|---|---|
| 非本人 owner / `LocalOwner` 尚未解析 | **静默 return**（无 fault、无 store） | true | **跳过 Fail → 继续跑 → 永不 `TerminalResultObserved` → 无限 Pump（挂死）** |
| `outcomeId == 0` | `Fault(InvalidField)` | true | 跳过 Fail，但随后的 `PumpCombatInbound` 因 `IsFaulted` 而 `Fail`（最终还是失败） |
| 胜方不在本端已知名册 | `Fault(InvalidField)` | true | 同上 |
| 与已存结论冲突 | `Fault(ConflictingOutcome)`，**不改写**已存值 | true | 同上（且已存的那份仍会被发布，见 §2.5） |
| 入站队列溢出（≥64 条同帧） | 先 `MatchResultsReceived++`，再 `Fault(QueueOverflow)`，**不入队** | true | 同第二行 |

**所以主侧说的「把已拒绝的 malformed 包当可信结束」成立；但它的真实后果不是「误判胜利」**
（宿主没有任何地方把「计数 >0」翻译成胜负，结论只从 `TryGetStoredMatchResult` 来），
而是第 1 行那种更隐蔽的失效：`Fail` 被跳过、`TerminalResultObserved` 永不为真、
7000ms 有界退场也不触发 ⇒ **会话停在 failed 端点上无限 Pump**：`IsActive` 仍为真、
`MovementFrozen` 未被置位（本地角色继续预测）、玩家回不了大厅也看不到任何失败。
同时它还会打出一条**内容为假**的日志：`"端点已关闭但可信终局结果已到达：按正常退场处理（不判失败）"`。

**可达性诚实说明**：第 1 行要求「可靠结果已到达，但 driver 的 `LocalOwner` 与 `item.Player` 不等」。
`LocalOwner = _adapters 中 Uid == _projectiles.LocalUid 的玩家`（`PMR6CombatDriver.cs:565-581`），
而结果 RPC 只发给 owner 连接（§1.2），正常情况下两者是同一个对象 ⇒ 该分支**时机很窄**
（结果早于本端 AP 副本绑定完成）。但：① 判据本身语义错（把「收到过」当「可信」）；
② 失效模式是挂死而非报错（安全方向相反）；③ 另三种 source 也被这个错误判据「洗白」了一帧。
因此按 P1 修，而不是按「路径罕见」忽略。

### 2.2 A2：same-frame「结果 + 断连」不能被新 race 破坏 —— **成立，改法已保留这一语义**

原始意图（正确）：`endpoint.Pump` 期间完成「drain → bridge.Update → RPC 入队 → 客户端连接自检 → RaiseFailed」，
此刻合法结果**已在 R6 入站队列里但尚未 Pump**；若在 `ClientFailed`/`OnEndpointFailed` 处直接 `Fail`，
就会把一份**已经到达**的合法终局降级为断线失败（等价于"DS 发结果并退出 ⇒ 客户端判输"）。

本次改法不再用「计数」来争取这一帧，而是**真的先把结果取出来**：
`PumpActive` 的端点失败分支里先调一次 `PumpCombatInbound(session, nowMs)`（= 本帧那次 `R6.Pump`），
再看 `TryGetStoredMatchResult`。因此：
- 同帧合法结果 + 断连 ⇒ 正常退场（`OnTerminalResult` → ACK 尝试 → HUD → `EndSessionNormally`）；
- 同帧非法/无结果 + 断连 ⇒ `Fail`（并带上端点给出的真实原因）。
一帧内 `R6.Pump` 仍然**只调一次**（该分支必然 `return`），固定次序与「同一根 now」都不变。

### 2.3 A3：ACK 是否真的发出才「正常退出」 —— **部分成立**

| 事实 | 证据 |
|---|---|
| AP 的 `FlushState` 唯一职责就是回 ACK | `PMR6CombatDriver.FlushState`：`if (_isServer) {...} else { FlushClientResultAck(); }`（`:1656-1665`） |
| ACK 需要「已保存 + 待发」两个位 | `FlushClientResultAck`：`if (!_clientResultAckPending || !_clientResultStored) return;`（`:1893`） |
| 宿主在终局闭环里**同帧**尝试发 ACK | `PMClientSessionHost.cs:1104-1112`：`OnTerminalResult(...)` → `FlushCombatState(...)` |
| 端点不可用时**故意不发** | `FlushCombatState` → `IsEndpointUsable`（`Endpoint.ClientFailed/IsDisposed` 或 `!ClientConnection.IsReady` ⇒ 直接 return）；理由：发给没人收的 socket 必抛 ⇒ R6 `SendFailed` fault ⇒ 把正常退场变成失败 |
| 重复结果会**重新武装** ACK | `HandleMatchResult` 幂等分支 `_clientResultAckPending = true`；DS 每秒重发 ⇒ 宿主后续帧 `FlushCombatState` 再发 |

**判定**：ACK 在「端点可用」时会被真实发出（同帧 + 每个后续可用帧），这是正常退出**前**的动作；
但「正常退出」本身**不要求** ACK 已发出 —— 7000ms 有界退场（`TerminalExitGraceMs = ResultGraceMs(5000)+2000`）
在 ACK 丢失或端点早已关闭时同样会正常退场，这正是契约 §C「超时是有界退场，不是结果已被观察的证明」。
所以主侧要求「ACK 真正发送才正常退出」**不能**作为退出前置条件；否则 `Endpoint.ClientFailed` 的终局路径将无法退出。
**未改**（若强行要求，会与契约 D2/D3 冲突）。

### 2.4 A4：队列/Endpoint Failed 后合法未处理消息 —— **端点侧已修；队列侧记录**

- **端点失败后**：原实现在「计数 >0」时既不 `Fail` 也不把结果取出来，等于**保留但永不处理**（见 §2.1）。
  现在：本帧 `R6.Pump` 会把它取出来并按可靠结果闭环退场 ⇒ 合法未处理消息**不再被丢掉，也不再被挂住**。
- **队列满**（`MaxInboundMatchResults = 64`）时 `EnqueueMatchResult` 先自增计数、再 `Fault(QueueOverflow)` 且
  **不入队** ⇒ 该条合法结果确实被丢。这属于 **driver 侧**（越界，本次只读）；实际不可达：
  可靠结果每条至多每秒 1 条、且每帧 `ApplyInboundMatchResults` 会排空（单帧上限同为 64）。
  已记录在 §5.3 供 owner 决定。
- **`MaxApplyPerPump = 64`** 意味着「单帧最多处理 64 条」；对一个结果包（1 条/次）不构成截断。

### 2.5 A5：R6 Faulted 的冲突结果是否静默当胜利 —— **不成立**

- 冲突（同 outcome 不同胜方 / 不同 outcome）走 `Fault(ConflictingOutcome)` 且 **`_clientResultStored` 不被改写**
  （`PMR6CombatDriver.cs:1557-1572`）⇒ 「结论只能冻结一次」被强制。
- 宿主从不把「Fault」翻译成胜负：胜负只来自 `TryGetStoredMatchResult` 的 `winnerTeamId`（与本地队伍比较）。
- 若冲突发生在**已有合法结论之后**：宿主会发布**第一份**（已通过校验的那份）结论，然后因 driver 已 fault 走失败/退出路径
  —— 这与主路径既有行为一致（终局闭环先发布再短路），不是「静默当胜利」。
- 若第一条结果本身就是非法（`outcomeId==0`/未知胜方）：不存、fault ⇒ 宿主 `Fail`，无胜负结论。
- 附带确认：`HandleMatchResult` 对**未知胜方**会 fault（`IsKnownWinner` 要求胜方出现在本端已复制的队伍号里）。
  这是一条**可能误伤合法终局**的风险（客户端名册/队伍复制不完整时），但它在 driver 侧（越界），记录于 §5.5。

### 2.6 A6：终局 HUD 生命周期 —— **全部成立**

| 要求 | 证据 |
|---|---|
| 终局后 HUD **不**被 `ReleaseSession` 销毁 | `EndSessionNormally` 先把 `session.Hud` 摘到 `static _retainedHud` 并置 `session.Hud = null`（`:2809-2813`），再 `ReleaseSession`（只销毁「会话自己那一份」） |
| 下一 Enter / 显式 Stop 清 | `Stop()`（`:989-991`）与 `Enter()`（`:637-639`）都调 `ReleaseRetainedHud()` + `ClearLastCombatResult()`；`Stop()` 覆盖「无会话但保留 HUD」 |
| 不泄漏无限创建 | `_retainedHud` 最多 1 个；`Enter` 创建新 HUD 前先 `ReleaseRetainedHud`；失败路径由 `ReleaseSession` 销毁本会话那份 |
| 无 static 事件泄漏 | `PMR3Runtime.PlayerReplicated` 在 `Stop` 与 `EndSessionNormally` 都退订；`PMUnityCombatHud` 自身**没有** static 可变状态/事件；`CombatResultObserved` 是给大厅 UI 的只读事件（订阅方生命周期自管，文档已写明）；`session.Endpoint.Failed` 是端点实例事件，随端点释放 |
| 旧对象 `OnDestroy` 不关新局 | `PMClientSessionHostDriver.OnDestroy` 有 `ReferenceEquals(_instance, this)` 门（`:3317-3327`，即 R6-C 报告 D10）；`EndSessionNormally` **刻意不**调 `Driver.Release()`（D9），因此 Unity 帧末延迟销毁不会把刚保留的终局 HUD/结果快照清掉 |

### 2.7 A7：`IsActive` / `LastError` 与 normalResult 不矛盾 —— **成立 + 一条残余限制**

- 正常退场：`EndSessionNormally` 置 `_active = null` ⇒ `IsActive == false`、`LastError == null`
  （`LastError` 只是 `_active.FaultReason` 的透出），同时 `HasLastCombatResult == true`
  —— 「正常结果 + 无失败残留」自洽。
- 下一 Enter 会 `ClearLastCombatResult()`，新会话的 `LastError` 从 null 开始。
- **残余限制（未改，记录）**：`Fail()` 之后没有任何自动退场路径。`PumpActive` 的 `Faulted` 分支只做
  `FrozenWallClockPump`（且**不再 `endpoint.Pump`**）⇒ 会话驻留、`IsActive=false`、`LastError=<失败原因>`，
  且此时**连回大厅的出路都没有**（要外部 `Stop`）。若这种会话之后 DS 正常收官并下发结果，
  客户端**不会**采纳（不再读 socket）。这是 R4/R5 既有「失败即冻结、不自作主张退出、不回旧链」的语义，
  不在本次窄修范围；建议 owner 决定是否给 Failed 会话补一条有界退场/自动 Stop（见 §5.2）。

### 2.8 A8：Hero/Team 确认时机（firstCreate / OnRep）—— **成立**

- firstCreate：`OnPlayerReplicated` 在拿到 AP 副本当帧就调 `VerifyOwnerIdentity(session)`（`:1503-1508`）。
- OnRep：`UpdateCombatReplicationState` 每帧调 `VerifyOwnerIdentity`（`:2554`），只要复制字段变化就被看到。
- 未就位不误判：`VerifyOwnerIdentity` 只在 `owner.CombatTeamId > 0` 时才比较；未就位则下一帧再看（`:2585-2587`）。
- 不一致 -> **整局 Fail**（不是静默接着打），并写入 `OwnerIdentityMismatch` 幂等位。

### 2.9 A9：`MaxHp` 默认 0 不误 Fail —— **成立**

`IsCombatFireReady`（`:2615`）对 `CombatMaxHp <= 0` 返回 `false + error`，调用方只
`AttackDriverRejected++ / LastAttackError = ...`，**不** `Fail`。`VerifyOwnerIdentity` 也不看 MaxHp。

### 2.10 A10：F/G 边沿 one-Update、同帧不 double —— **成立 + 一条观察**

- `SampleCombatInputEdges`（`:2034`）每 `PumpActive` **只**采样一次；`TryCombatAttack` 也只被
  `PumpProjectiles` 调一次 ⇒ 一帧最多一次 `R6.TryAttack`。
- 消费点在确认「本帧能尝试」之后（`:2084-2089`：`isSuper = SuperAttackEdgeBuffered`，随后两个边沿一起清）。
  ⇒ 同帧 F+G 不会 double（只出一次攻击）。
- **观察（未改）**：同帧同时按下时 `isSuper` 取 G ⇒ 出**大招**、F 被丢弃。属于键位优先级取舍，
  不影响权威（DS 仍按真实资源裁决）；若要更确定，可让 F 优先或分别排队，留给 owner 决定。

### 2.11 A11：`predictionMs` 硬编码 —— **成立，如实记录**

`CombatAttackPredictionMs = 100`（`:175`），注释原文已写明：「本批**不**做实测 RTT 折算（客户端没有 RTT 测量口）」。
本报告同样**不**声称任何 RTT 自适应；该值只影响 DS 侧权威初值是否追赶，不参与任何判定。
客户端 `R6.Pump` 的墙钟来自 `Time.realtimeSinceStartup`（单调、非 RTT 折算），与契约「同一根单调 clock」一致。

### 2.12 A12：death / endedFreeze 不误伤 —— **成立**

- 死亡：`UpdateCombatReplicationState` 只对 `rig.Player.CombatDead` 为真的 **rig 自身**调 `FreezeRigOnce`（`:2554-2570`，`:2650`）。
- 终局：`CombatMatchEnded` 才 `FreezeOnMatchEnded` → `FreezeMovements`（全局冻结，契约明确要求）。
- Tick/Advance 侧另有双保险：`TickAutonomousProxies` 与 SP 插值循环显式跳过 `rig.DeathFrozen`（`:1358`、`:1310`），
  不依赖驱动自身 frozen 语义。
- 冻结后协议仍 Pump（`PumpProjectiles` 冻结分支只 `Pump(0)` + 排空视图脏通道，不推进/不开火/不收候选）。

### 2.13 A13：Candidates 只 owner+origin、非 dead enemy —— **成立（含一处口径说明）**

- 上行：`CollectAndSubmitProjectileHits`（`:2215`）只收 `view.Key.Origin == ClientPredicted` 且
  `view.Key.OwnerNetId == ownerNetId`，并跳过已用尽 `MaxVerifyPerKey` 配额的 key（计数可见）。
- 目标：`BuildProjectileTargets`（`:1931`）跳过自己、跳过 `CombatDead`，并在
  `localTeamId > 0 && target.CombatTeamId == localTeamId` 时跳过同队（**未知队伍不当同队**）。
- 口径说明：`target.Alive` 走到那里恒为 true（死者已先滤），字段保留是命中协议要求；DS 侧仍用
  `model.Dead/Connected` 复核 ⇒ 客户端**不做**存活裁决。真正的「敌/友」终判在 DS。

### 2.14 A14：资源只读、客户端不写复制字段 —— **成立**

- 客户端（AP）**写复制字段的唯一入口**是 `PMR3Player.PublishCombatState`，它的唯一调用者是
  `PMR6CombatDriver.PublishStateIfDirty`，而后者只在 `FlushState` 的 `if (_isServer)` 分支被调用
  （`PMR6CombatDriver.cs:1656-1671`）。AP 传 `model = null` + `_isServer == false` ⇒ 永不发布。
- 宿主与 HUD 侧：`grep` 复核「`PMClientSessionHost.cs` / `PMUnityCombatHud.cs` 内不存在对
  `CombatHp/CombatMana/CombatSuperEnergy/CombatDead/...` 的赋值」，只有读（`hud.SetLocal(owner.CombatHp, ...)`）。
- 既有门禁 `python Tools/check_client_authority_writes.py` 覆盖的是**旧链**的 8 个权威字段（`playerBloodValue` 等），
  不含新的 `_combat*`；本次以「唯一调用者 + `_isServer` 门」作为证据，不把它算作该门禁的能力。

### 2.15 A15：`LastAttackResult` 无 reason 导致 HUD 显示假拒绝 —— **部分成立（实为 API 缺口）**

- 复核结论：**不存在** `LastAttackResult` 这个成员（全树无命中）。相关面是 driver 的
  `AttackResultsApplied / AttackResultsUnknown / AttackResultsLateAfterTerminal / PredictionsCancelled`
  —— 它们是**计数**，且 **driver 没有任何公开 accessor 能给出 DS 的拒绝原因**
  （唯一带 reason 的地方在 `HandleAttackResult` 内部，`item.Reason` 只用于 `IsKnownRejectReason`）。
- HUD 的「最近拒绝」只吃 `session.LastAttackError`，而它**只**在本地路径写：
  `IsCombatFireReady` 未就绪 / 预测位姿非 finite / planner 拒绝 / `FireInterval` 门 / `TryAttack` 返回 false；
  本地成功即置 null（`:2180`）。
- 所以：HUD **不会**显示「假拒绝」（不会凭空造出一次拒绝），但也**显示不出 DS 的真拒绝**
  （本地预测弹被 DS 拒绝后由 driver 静默撤销假弹，HUD 只显示「最近拒绝：<无>」）。
  另有一处**易误读**：一次 DS 拒绝之后，若玩家紧接着再按 F 被本地间隔门挡住，HUD 会显示
  「开火间隔未到」——它描述的是**本次**本地门，容易被读成「上一枪被拒的原因」。
- 处置：**代码未改**（driver 在写边界之外）。建议 owner 在 `PMR6CombatDriver` 加一个只读 accessor
  （例如 `TryGetLastAttackResult(out uint activationId, out bool accepted, out PMCombatRejectReason reason, out bool stale)`），
  宿主在 `TryCombatAttack` 成功后把「本地已发射、等待裁决」也推给 HUD，从而不再出现「无原因消失」。

---

## 3. 本次修掉的缺陷（窄改动，逐条给机制与影响）

改动文件只有 `Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs`（HUD 文件本次**未改**）。

### 3.1 缺陷 1：用「入站结果计数」当可信终局判据（P1，挂死）——**已修**

改动点（当前行号）：

| 位置 | 改动 |
|---|---|
| `:1077-1126` | `PumpActive` 的 `Endpoint.ClientFailed` 分支重写：① 终局已落定 ⇒ `EndSessionNormally`（原样）；② 否则**先 `PumpCombatInbound(session, nowMs)` 把本帧已入站的合法结果取出来**；③ `TryGetStoredMatchResult` 为真 ⇒ `OnTerminalResult` → `FlushCombatState`（端点已关时按 D3 跳过 ACK）→ `UpdateCombatHud` → `EndSessionNormally`；④ 否则 `Fail(真实断开原因或握手失败文案)` → `FrozenWallClockPump`；⑤ 分支**必然 return**，因此一帧内 `R6.Pump` 仍只调一次 |
| `:1070-1072` | 把 `terminalOutcomeId / terminalWinnerTeamId` 两个出参**提前声明**（端点失败分支与本帧终局闭环共用同一次定性） |
| `:2923-2947` | `OnEndpointFailed` 改为**不判失败**：终局已到达则只记日志；已激活后断开则把原因写进新增的 `Session.PendingDisconnectReason`；握手阶段只告警（保持原行为，失败文案仍由本帧 `ClientFailed` 分支给出） |
| `:442` | 新增 `Session.PendingDisconnectReason`（带理由注释） |
| `:3130` | `ReleaseSession` 复位 `PendingDisconnectReason` |
| 删除 | `IsTerminalResultPendingOrStored`（唯一用途已消失，保留即死代码） |

为什么 `OnEndpointFailed` 也必须一起改：该回调由 `endpoint.Pump` **同步**抛出，位置在
`endpoint.Pump` 内部（客户端侧全部 4 处 `RaiseFailed` 都在 `Pump` 里，且都在 `_clientFailed = true` **之前**），
早于本帧的 `R6.Pump`。若它继续在这里 `Fail`，会先置 `Faulted`，本帧 `PumpCombatInbound` 根本不会执行
⇒ 一份已到达的合法终局仍会被降级为断线失败（A2 场景）。改后定性统一发生在 `R6.Pump` **之后**。

**行为影响（对照表）**：

| 场景 | 改前 | 改后 |
|---|---|---|
| 同帧「合法结果 + 端点关闭」 | 正常退场（靠计数放行） | **正常退场**（靠真实取出放行，语义正确） |
| 端点关闭 + 只有被丢弃/被拒的结果包 | 跳过 Fail 后**挂死**（`IsActive=true`、不冻结、无退场） | **`Fail`**（原因取端点给的文案），随后冻结 |
| 端点关闭 + 无任何结果 | `Fail`（原文案） | `Fail`（文案不变；已激活后断开时更精确到端点原因） |
| 握手阶段失败（未激活） | `ClientFailed` 分支 `Fail("入局握手失败（对端不在本局/票据已过期/关联校验不通过）")` | **同上**（`ClientConnection == null` ⇒ 不记具体原因，文案逐字不变） |
| 已激活后断开 | `OnEndpointFailed` 内 `Fail("入局会话已断开：…")` | 同帧稍后 `Fail("入局会话已断开：…")`（文案逐字不变，位置后移到 `R6.Pump` 之后） |
| 内部身份核对（`VerifyOwnerIdentity`）与终局同帧冲突 | 先核对（可能 Fail）再取终局 ⇒ 终局可能被掩掉 | 端点失败分支里**先取终局**、不再走身份核对 ⇒ 合法终局优先（正向变化，见下） |

一处**刻意的次序变化**（记录在此以免被当成回归）：端点失败且存在合法终局时，本改动**不再**执行
`UpdateCombatReplicationState`（含 `VerifyOwnerIdentity`）与 `SampleCombatInputEdges`。理由：这两个调用与被取出的
终局结论无关，而「身份不一致」不应把一份 DS 已合法收官的终局掩成断线失败；改后由终局结论优先、随后正常退场。

### 3.2 缺陷 2：HUD `[就绪]` 与宿主真实开火门不一致（P3，误导实机联调）——**已修**

- 机制：`UpdateCombatHud` 原来只按 `IsCombatFireReady` 推 `SetReady`，而真正的开火路径
  `TryCombatAttack` 还会因 `session.Faulted || MovementFrozen || MatchEndedFrozen` 直接不开火（`:2065`）。
  于是「可靠终局结果已到、复制位 `CombatMatchEnded` 还没到」的那些帧里，HUD 会同时显示
  「`F = 普通攻击 G = 大招 [就绪]`」与「结果：胜/负」——自相矛盾（终局后 `MovementFrozen` 已被置位）。
- 改动：`:2862-2868`，`hud.SetReady(fireReady && !session.Faulted && !session.MovementFrozen && !session.MatchEndedFrozen)`。
- 影响：**纯显示**，不触及任何权威/资源/输入；只是让「就绪」与「按下去真的会开火」等价。

### 3.3 缺陷 3：`Enter` 构造失败路径漏关 UDP 端点（socket 泄漏，P3）——**已修**

- 机制：`session.Endpoint = PMUdpSessionEndpoint.OpenClient(...)` 之后、`_active = session` 之前，
  有两处失败分支会 `ReleaseSession(session); return;`（只读 HUD 创建失败 / 创建抛异常，`:917-930`）。
  而 `ReleaseSession` 原来只是 `session.Endpoint = null;`（**不** Dispose）⇒ 那个已 `Bind` 的 UDP socket 永不被关闭。
  `Stop` / `EndSessionNormally` 之所以没事，是因为它们**先**显式 `Dispose` 端点再进 `ReleaseSession`。
- 改动：`:3041-3050`，在 `ReleaseSession` **开头**加幂等的端点兜底释放（`try { session.Endpoint.Dispose(); } catch`）。
- 安全性论证：`Dispose` 幂等（`PMUdpSessionEndpoint.Dispose` 首行 `if (_disposed) return;`）；不触发 `Failed`
  事件（`Dispose` 里没有 `RaiseFailed`）⇒ 不会重入会话失败；`ReleaseSession` 的 20 个调用点全部是
  「终局/失败/构造失败」语义，调用后**没有任何**调用者再使用 `session.Endpoint`（已逐个核对，见 §4 命令）；`Stop` /
  `EndSessionNormally` 的原显式 Dispose 保持不变（此处变成 no-op）。

---

## 4. 验证与回归（真实命令、真实结果）

| 门禁 | 结果 |
|---|---|
| `dotnet build Tools/PMClientCheck/PMClientCheck.csproj -c Release -o Tools/PMClientCheck/bin/r6-client-host-review --no-incremental` | **0 警告 / 0 错误**（改前基线同为 0/0） |
| `dotnet build Tools/PMUnityGlueCheck/PMUnityGlueCheck.csproj -c Release -o Tools/PMUnityGlueCheck/bin/r6-client-host-review --no-incremental` | **0 警告 / 0 错误** |
| `python Tools/check_cs_braces.py <两个 .cs>` | **PASS**（`PMClientSessionHost.cs` 458/458、967/967 配平，深度非负、末尾归零；`PMUnityCombatHud.cs` 46/46、60/60） |
| `python Tools/check_client_authority_writes.py` | **越界写入 = 0**，PASS（覆盖旧链 8 个权威字段；新 `_combat*` 另以「唯一调用者 + `_isServer` 门」证据判定，见 §2.14） |
| 编码自检 `file` | 两个目标 `.cs` 均 **UTF-8 BOM + CRLF**（与改前一致；`PMUnityCombatHud.cs.meta` 本次未动） |
| 静态复核（本次改动的证据面） | `grep` 确认：`MatchResultsReceived` 在宿主侧已 **0 引用**（仅剩 driver 定义 + `PMR6NetworkTest` 断言）；`IsTerminalResultPendingOrStored` 已不存在；`ReleaseSession` 的 20 个调用点后续语句均为 `return`/`Release()`，无「调用后继续用端点」 |
| **未执行（诚实声明）** | **未**跑 `PMR4UnityCheck`（另一组正在使用其输出，本次刻意不抢；且它只覆盖「真实 Unity API 面」，与本次主机逻辑无关）；**未**跑 `PMR6NetworkTest`（driver 逐字未改，其 383 条断言与本改动无交集，且不覆盖真实宿主） |

**这次改动的验证强度诚实口径**：仓库内**没有**覆盖**真实** `PMClientSessionHost` 端点失败分支的自动化测试
（`Tools/PMR3IntegrationTest` / `PMR4IntegrationTest` 里的「客户端宿主」是**复刻非 Unity 次序**的测试替身，
不编译真实宿主；`PMClientCheck`/`PMUnityGlueCheck` 是编译面门禁）。因此本修复的验证 = **编译面 0/0 + 括号/编码门禁 +
对 `PMUdpSessionEndpoint`/`PMR6CombatDriver`/`PMNetSessionBridge`/`PMRpc` 的真实代码逐行推导 + 改动前后行为对照表**。
**运行时行为（真 UDP / 真 DS 退场时序）仍属 UNVERIFIED**，必须实机才能确认，见 §6。

---

## 5. 已确认但**未**修（越界或需 owner 决策）——诚实清单

### 5.1 `PMR6CombatDriver` 的终局结果校验可能误伤合法终局（driver 侧，P2 待评估）
`HandleMatchResult` 用 `IsKnownWinner` 要求胜方队伍号出现在**本端已复制**的 `CombatTeamId` 里；
若某队成员的队伍复制尚未到达（或该成员已被 `UnbindPlayer` 摘掉），一份**合法**的终局会被
`Fault(InvalidField)` 打成失败（宿主随后 `Fail`）。契约只要求「winner 使用名册真实 TeamId」，
未要求客户端必须已复制到该队伍。建议 owner 评估：未知胜方是否应改为「延迟保存/等待队伍复制」而不是 fault。
（本次只读 driver，未改。）

### 5.2 `Fail` 后没有自动退场路径（既有失败语义，P2 记录）
见 §2.7。`Faulted` 会话不再 `endpoint.Pump`，因此连「之后到达的 DS 结果」都读不到；玩家必须等外部 `Stop`。
本次未改（属 R4/R5「失败即冻结、不回旧链」的既有约定），但建议 owner 明确一条有界退场（例如失败 N 秒后
走 `EndSessionNormally` 的兄弟路径回大厅并广播原因）。

### 5.3 入站结果队列满时丢弃合法结果（driver 侧，P3 理论）
见 §2.4。`EnqueueMatchResult` 先自增计数再 fault 且不入队。可达性极低（64 条/帧 + 每秒 1 条重发），
但语义上「合法结果被丢」应在 driver 侧改成「先留一条位置」或显式区分计数语义。

### 5.4 DS 攻击拒绝原因无法到达 HUD（API 缺口，P3）
见 §2.15。需要 driver 侧新增只读 accessor（越界）。

### 5.5 F+G 同帧时 F 被丢弃（键位优先级，P4 观察）
见 §2.10。不影响权威判定。

### 5.6 文档义务（本次**未**做，因写边界）
`Client/Assets/AGENTS.md` §11 要求「输入采集 / 权威帧消费 / GameOver 边界」类改动同步更新文档。
本次改动落在「宿主结果退场 + HUD 只读提示」上，**未**修改 `AGENTS.md` / `Docs/ForServer.md` / `BothSide.md`
（三者均在本次写入边界之外）。建议 owner 在本批合并时补一句：
「客户端端点失败时，退场判据是『可靠结果是否已通过校验并保存』，不再是收到结果消息计数」，
以及「HUD 就绪位已与真实开火门对齐（含冻结/终局）」。

---

## 6. 实机限制与未验证项（PENDING_USER）

| # | 项 | 说明 |
|---|---|---|
| V1 | 真 UDP 端到端 | 「DS 发结果 + 关会话」的真实到达次序（同帧/跨帧）、ACK 丢失后重发、7000ms 有界退场，均只由代码推导，**未实机** |
| V2 | 同帧竞态 | `ReceiverDatagrams → bridge.Update → CheckClientSessionAfterUpdate` 的同步次序由只读代码确认；真机抖动下是否出现「结果在端点失败之后才到达」（此时客户端必然 `Fail`，属 DS 侧过早退场）**未验证** |
| V3 | HUD 观感 | `DontDestroyOnLoad` 宿主在退局后的存活、隔离物理场景卸载后回大厅的观感、IMGUI 面板位置/遮挡，**未在实机观察**（本任务禁止启动 Unity） |
| V4 | 停止输入后本地预测 | 终局冻结期间本地角色的表现停帧是否与旧链观感一致，未机测 |
| V5 | `TerminalExitGraceMs = 7000` | 由「DS 5000ms 宽限 + 2000 余量」推得，**未实测计时**（沿用 R6-C 报告的结论） |
| V6 | 端点失败分支的挂死修复 | 修复效果只能在「结果 RPC 在 `LocalOwner` 未解析时到达」的真实时序里显现，构造该时序需要真机/仿真夹具（仓库无覆盖真实宿主的测试） |

---

## 7. 改动清单（严格限界）

| 文件 | 性质 |
|---|---|
| `Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs` | **修改**（§3.1 / §3.2 / §3.3 三处缺陷修复；均为宿主逻辑与显示面，**未**触及 R6/R5 调用次序、时钟、冻结语义、候选/命中链） |
| `Client/Assets/Scripts/PMUnity/PMUnityCombatHud.cs` | **未改**（复核确认三条硬边界成立：只读、零资源编辑、DS 禁建；生命周期符合契约） |
| `Docs/plans/_r6_client_host_review.md` | 本报告（本次唯一文档写入） |

**未改动**：`PMR6CombatDriver.cs`（逐字未改）、`PMR5ProjectileDriver.cs`、`PMCombat/**`、`Shared/**`、
`Client/Assets/Scripts/PMR3/**`（含生成物）、`PMDsSessionHost.cs`、`Tools/**`（含全部门禁工程与桩件）、
`Docs/plans/net-r6-combat-contract.md`、`Docs/plans/net-architecture-migration.md`、任何 `.meta`/锁文件/csproj。
**未执行**：Unity 启动、git/svn 写操作、提交、递归委派。

---

## 8. 已检查范围（文档 → 落点）

**按委派顺序完整读完的文档**：
`D:/UGit/hyld-master/AGENTS.md`（入口：客户端文档在 `Client/Assets/AGENTS.md`、网络迁移计划在 `Docs/plans/net-architecture-migration.md`）
→ `Client/Assets/AGENTS.md`（全文；§1.1 进程形态判定、§3.2 命令聚合、§4 子弹/HP/击杀、§8 动态追帧、§14「新 PMNet R5 投射物接线（优先于上文旧战斗链描述）」）
→ `Docs/plans/net-r6-combat-contract.md`（全文；尾段 **C 宿主接线冻结** 是本次落点依据：Hero/Team 必须与 offer 一致、
`Pump` 固定次序、死亡 freeze、终局冻结但继续协议 Pump、结果幂等 + ACK、只读 HUD 保留到下一 Enter/Stop、未收可信终局时断网仍 Fail）
→ `Docs/plans/_r6_client_host_report.md`（全文；§2 契约条款→落点表、§3 每帧时序、§4 结果退场状态机、§6 D1~D10 判定）
→ `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`（全文；本次全部命令按 Bash 语法在 Pi 的 `bash` 工具里执行，
Windows 路径用 `D:/…` 形式，未混用 PowerShell）。

**文档如何决定首搜范围**：`Client/Assets/AGENTS.md` §14 把「新局」入口钉在 `PMClientSessionHost` / `PMDsSessionHost`，
并声明旧 `BattleManger/shell` 不驱动新局 ⇒ 本次不复核旧链；契约 C 段逐条给出宿主必须实现的成员（`Enter` / `PumpActive` /
`OnPlayerReplicated` / `R6.Pump` / `R6.FlushState` / `EndSessionNormally` / `PMUnityCombatHud`），
据此只在这两个文件与其真实依赖（driver/transport/RPC/planner）里定点复核，未做全项目盲搜。

**本次实际读取的实现文件**（除 §0 列出的直读面外，另含）：`PMNetObject.cs`（OwnerOnly 条件语义）、
`PMReplicationTypes.cs` / `PMWireType.cs`（OwnerOnly 枚举）、`PMR3/Generated/PMNet.PMNet.R3.PMR3Player.g.cs`
（`PMNet_ClientCombatMatchResultV1` 的 callspace/EnqueueRemote 与收侧分发）、`PMTransportConnection`（`IsReady`）、
`PMTransportTypes`（`IdleTimeoutMs = 10000`）、`PMProjectileDiagnosticConfig`、`PMCombatWeaponPlanner`、
`Tools/PMClientCheck/ClientStubs.cs`、`Tools/PMUnityGlueCheck/UnityStubs.cs`、`.gitattributes`、
`Docs/plans/_r5_client_host_review.md`（仅参照报告格式，读前 70 行）。
