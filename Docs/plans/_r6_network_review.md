# R6-B 网络驱动独立复核与修正（PMR6CombatDriver / 真实闭环）

> 任务类型：**comprehensive**（独立复核 + 修正 + 网络正负回归；只读消费 PMCombat / R5 / 声明层）。
> 必读输入（按委派顺序全文阅读）：`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`
> → `Docs/plans/net-r6-combat-contract.md`（含尾「B 驱动冻结补充」）→ `Docs/plans/_r6_network_report.md`
> → `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`。
> 写入范围（仅此四处）：`Client/Assets/Scripts/PMR3/PMR6CombatDriver.cs`、`Tools/PMR6NetworkTest/Program.cs`、
> 本文件（`Docs/plans/_r6_network_review.md`）。**`PMR5ProjectileDriver.cs` 与 `PMR3Runtime.cs` 经复核判定为正确，逐字未改。**
> **未启动 Unity；未执行 SVN/git 写操作；未提交；未递归委派；未改 core / Contracts / Declaration / 生成产物 / 主计划；
> 未接线 host（`PMClientSessionHost` / `PMDsSessionHost` 保持只读）。**

---

## 0. 结论摘要

| 项 | 结果 |
|---|---|
| 复核方式 | 读**真实源码**逐条验证任务列出的 12 组疑点（不是接受 `_r6_network_report.md` 的断言） |
| 确认的真缺陷 | **6 处**：① DS `Pump` 次序（回蓝晚于请求判定）② 授权策略次序（无效枪口预占 slot）③ 回蓝不触发状态复制 ④ AP pending 不是终态先到先胜 ⑤ `TryAttack` 满表检查晚于不可逆动作 ⑥ 宿主回调异常会打断结算/结果链 |
| 判定「不是缺陷 / 不可达」 | 3 组（R5 activation 终态、活镜像幽灵、Drain 双扣的 core 侧）—— 逐条给出源码证据 |
| 行为门禁 | `Tools/PMR6NetworkTest`：**通过 383 / 失败 0**（原 220 → 新增 **163** 条断言），exit 0 |
| 语言面门禁 | `Tools/PMR6NetworkCheck`（netstandard2.0 + C# 7.3，零 Unity）：**0 警告 / 0 错误** |
| 变异测试（证明门禁有牙齿） | **9 组变异全部被捕获**（含 1 组「旧行为组合」复现双消费通道） |
| 回归 | R5NetworkTest **PASS**；R3Runtime/R3Integration/R6Declaration/R5Declaration/PMDeclCheck/PMNetVerify 全 0 失败；编译面 11 个门禁 0 错误。唯一 FAIL 是 `PMR4NetworkTest` 的 2 条 **R6-A2 遗留过时计数断言**（写边界之外，未改） |
| 诚实边界 | **host 仍未接线**（`PMR6CombatDriver` 没有真实宿主创建）；T6B 的实机/UDP/PhysX 面继续 UNVERIFIED |

---

## 1. 逐条复核结果（任务列出的疑点）

### 1.1 DS `Pump` 在 `RequestAttack` 之前是否先 `Tick(now)` 回蓝 —— **真缺陷（P0，已修）**

**源码事实**：旧 `Pump` 的顺序是 `ApplyInboundAttacks()`（内部 `core.RequestAttack`）→ …→ `_model.Tick(wallNowMs)`。
core 的 `RequestAttack` **不自行推进回蓝**（`PMCombatSession.RequestAttackCore` 只读 `player.Mana`），
回蓝只在 `Tick → RechargeMana` 里发生。

**后果（比原报告更严重）**：恰好卡在「这一段回蓝刚好到达」边界上的**合法**攻击会被判
`InsufficientMana`，而且该拒绝会作为**终态**写进 core 的攻击账本（`RecordRejection` 占 `HighWaterActivationId`），
同一 `activationId` 的幂等回显**永远是拒绝**。也就是「一次 16ms 的次序错误 = 永久吃掉一次攻击」。

**修正**：`Pump` 里先 `TickModel(wallNowMs)`，再 `ApplyInboundAttacks()`（其余入站与 Drain 次序不变）。
**新测试**：L 段（边界帧构造：`frame1 = +1ms`、`frame2 = 不动`、`frame3(+2 帧交付延迟后) = +1000ms`）。

### 1.2 策略调用 slot 授权之前是否先验证 `hostPosition` —— **真缺陷（P1，已修）**

**源码事实**：旧 `AuthorityPolicy.TryAuthorizeSpawn` 先 `core.TryAuthorizeProjectile`（**成功即消耗一个方向槽 +
一条 `_authorized` key 记录，终态、等 TTL**），随后才 `_tryGetOwnerPosition`。位置不可信时**槽已经被吃掉**。

**后果**：宿主位置暂时不可用（Mover 未就绪 / freeze 未完成）时，同一笔攻击的 N 颗弹把 N 个槽一次性消耗；
位置恢复后这批弹再也无法被授权（`DirectionMismatch`）——「无效枪口消费所有 slot」成立。

**修正**：策略内**先**位置（可用 + finite），**再** core。**新测试**：M 段（`AuthorizedProjectileCount == 0`、
`ServerSpawnsAuthorized == 0`；枪口恢复后同一颗弹**仍能被授权**）。

### 1.3 `RequestAttack` rejected/duplicate 与 R5 `ResolveActivation` 终态冲突 —— R5 侧**已被保护**，AP 侧**是真缺口（已修）**

- **R5 侧（不是缺陷）**：`PMProjectileLifecycle.TrySetActivationResult`（1893–1973）对已终态（含 Confirmed）
  一律返回 `AlreadyTerminal`；被容量淘汰的终态判决还留在有界 `_terminalMemory` 里
  （`EvictOldestTerminalLedgerEntry → RememberTerminalVerdict`），`TryGetActivationVerdict`（822–840）
  先查 ledger 再查该记忆。因此**任何后来（含反向）的写入都不可能撤掉/翻掉已 Confirmed 的弹**。
  另一道：`PMProjectileCoordinator.ResolveActivation` 对 `AlreadyTerminal` 直接返回
  `AlreadyResolved`，**不重复释放、不重复结算**。
- **AP 侧（真缺口）**：R6 driver 的 `_pendingAttacks` 旧实现**只有 Confirmed/Revoked 两个布尔位**，
  没有「终态先到先胜」：已 Accepted 的 activation 收到**迟到 Rejected** 时会
  `CancelPredictedActivation` 反向撤销假弹。**修正**：`HandleAttackResult` 增加终态优先
  （`Revoked` 后忽略迟到 Accepted；`Confirmed` 后忽略迟到 Rejected）并计入
  `AttackResultsLateAfterTerminal`。**新测试**：N 段（伪造真实下行迟到 Rejected）。

### 1.4 `TryAttack` 多弹部分 `TryFire` 失败 —— 逻辑**已正确**，补测试 O

源码即「逐颗失败 ⇒ `CancelPredictedActivation(该 activation)` + `Fault(SendFailed)` + 不外泄 activationId」。
**测试难点与解法**：不能用「连发上百条 spawn RPC 填表」逼近容量（实测**可靠发送窗口 ≈512 条 RPC** 就会 `RpcSendRejected`），
改为直接 `Coordinator.Lifecycle.TryRegisterPredicted` 把**客户端预测登记表**填到只剩 1 个空位，
且**用另一个 owner** 当填充流（全局 predicted 计数共享、`projectileId` 高水位是 per `(owner, origin)`），
从而精准制造「第 1 颗成功、第 2 颗 `RegistryCapacity` 失败」。

### 1.5 `CancelPredictedActivation` 是否被已接管镜像绕过 ⇒ Rejected 活镜像幽灵 —— **不可达（已给出证据）**

- 接管的前提是「本端存在权威镜像**且**收到该 key 的 Confirmed 裁决」（`HandleClientDecision` → `MirrorObjectExists` → `TakeOverFake`），
  而权威镜像只可能由 DS 授权生成 ⇒ core 已接受该攻击；
- 攻击级裁决由 DS 在**处理该 attack 的那次 `Pump`** 内发出，可靠域保证它在同 activation 的 spawn 决定**之前**到达客户端；
  因此「攻击级 Rejected」到达时，该 activation 不可能已经有被接管的镜像；
- 若攻击级 Accepted 到达，则修正 1.3 之后**不可能**再被翻成 Rejected；
- 反向（先逐颗 Rejected）也不留幽灵：逐颗路径 `HandleClientDecision → RevokeFake` **不跳过** `TakenOver`
  （只有 R6 的攻击级批量撤销才跳过，而它只在 `pending.Revoked == false` 时被调用）。
- 测试固定：N 段断言 `PredictionsCancelled`/`FakesRevoked`/`FakeCount` 全部不变且未被标记 Rejected。

### 1.6 结算「订阅 + 缓冲」是否双扣 —— **core 侧不双扣；R6 侧曾打开过这条通道（已修）**

**R5 侧源码证据**：`DrainCoordinatorOutlets` 对每条结算**要么**同步交付订阅者（成功即 `continue`，**不入缓冲**），
**要么**在「无订阅者 / 订阅者抛异常」时入 `_pendingSettlements`（有界 512，满则会话 fault）。
**core 侧**：`ApplySettlement` 的 `AuthorizedKeyRecord.SettledTargets` 保证**每 key-target 只结算一次**，
`damage > 0` 分支才回能 ⇒ 重复应用不会二次扣血/二次回能。

**R6 侧缺口**：旧 `OnProjectileSettlement` 直接调 `ApplySettlementNow`，**异常会外泄到 R5** ⇒ 结算被退回缓冲
⇒ 下一次 `Pump` 的 Drain 取回**再应用一遍**（血量安全但「已应用」记账与 `PlayerDied` 重复）。
**修正**：`OnProjectileSettlement` 自己消化异常（`SettlementApplyFailures` + `SettlementFailed` 会话 fault，fail closed），
`DrainBufferedSettlements` 同样逐个消化；死亡上报改用**驱动本地真值** `_deathsReported`（不再依赖复制字段
`player.CombatDead`，后者要等 `FlushState`）。
**新测试**：V 段（真实挂第三个会抛的订阅者，断言 `SettlementsQueued == 1`、`SettlementsBuffered == 1`、
缓冲清 0、**HP 只扣一次**）；R 段（宿主回调异常不得退回缓冲）。

### 1.7 「只有资源 regen 时」脏 state 是否正确发现并 publish —— **真缺陷（P1，已修）**

**源码事实**：旧 `PublishStateIfDirty` 以 `if (!_stateDirty) return;` 为门；`_stateDirty` 只由
攻击/结算/名册/开战这些**显式入口**置位；而**回蓝只发生在 `core.Tick`** 里。后果：客户端蓝量卡在扣费后的值，
直到下一次命中/攻击才跳到正确值（owner-only 资源长期显示错误）。
**修正**：新增 `_lastModelTickMs` + `_tickAdvancedSincePublish`（`TickModel` 在 core 接受时钟后置位）
+ `PublishedCombatState` 值语义比对：`FlushState` 在「显式脏 **或** 时钟前进过」时重新比对快照，
**只写有变化的玩家**（顺带消除「没变也每条 Flush 都触发 9 条 RepNotify」）。
**新测试**：Q 段（只推进 3s 墙钟、零战斗事件，断言 AP 副本 `CombatMana` 从 60 跟到 90）。

### 1.8 Result 冻结 / Ready 条件 / ack 归属 / 重发 1000 / 宽限 5000 / 断线少一方 / 重复与冲突 / 不在回调发

逐条判定与处置：

| 子项 | 判定 | 证据 / 处置 |
|---|---|---|
| 冻结一次、不被后续改写 | 正确 | `_outcomeFrozen` 只在 `UpdateOutcome` 写一次；G 段 |
| Ready = 全在线 owner ACK **或** 5000ms 宽限 | 正确 | `AllConnectedOwnersAcked` 只遍历**名册**（不看 `_resultAcked` 大小）；H 段（无 ACK ⇒ 1100ms 仍未就绪 ⇒ 5000ms 就绪且 `ResultReadyByGraceCount >= 1`） |
| 重发 1000ms、内容冻结 | 正确 | `ResultResendMs == PMCombatLimits.ResultResendMs == 1000`；A/H 段；`ResultSummary` 每次 `Clone()`（G 段就地改不影响内部） |
| **ACK 不在回调里发** | 正确 | 下行只入队（`EnqueueMatchResult`），ACK 在 `FlushState` 发；H 段用 `SuppressClientFlush` 证明「只收不发」⇒ `ResultAckCount == 0` 是最直接的证据 |
| 断线允许少一个等待 | 正确 | `UnbindPlayer` 移除该 netId 的 ack 期待 + `core.Disconnect`；**新测试 U 段**（2v2 摘掉 1 人 ⇒ `ResultAckCount == 3` 即就绪、且被摘的 owner 不再收到结果） |
| 重复 outcome 幂等、冲突 fail closed | 正确 | 重复：G 段；冲突：**新测试 T 段**（同 outcomeId 不同胜方 ⇒ `ConflictingOutcome` fault、已存结论不被改写） |
| ack 归属 | **加固** | 认证归属由 RPC 层（Owner 判定）保证；新增「必须是本会话名册成员」守卫，否则计 `ResultAckMismatches`（避免非名册对象虚增可观测 `ResultAckCount`） |

### 1.9 `PlayerDied` / `OutcomeFrozen` 回调异常是否导致整包资源未复制或结果重发漏 —— **真缺陷（P1，已修）**

**源码事实**：旧实现直接 `handler(player)` / `handler(outcomeId, winner)`（无隔离）。两条真实危害：
① `PlayerDied` 在结算消费链上抛 ⇒ 异常穿到 R5 ⇒ 结算被退回缓冲（见 1.6，双应用通道）；
② `OutcomeFrozen` 在 `UpdateOutcome` 里抛 ⇒ 打断同帧后续的「重发 + 就绪判定」，宿主（Unity 帧）通常直接崩。
**修正**：`NotifyPlayerDied` / `NotifyOutcomeFrozen` 逐订阅者 try/catch + `CallbackExceptions` + `Warn`
（沿用 `PMR3Runtime.NotifyPlayerReplicated` 的既有手法），**不改权威状态、不 fault**。
**新测试**：R 段（两个订阅者都抛：断言 core 仍终局、摘要仍生成、两个客户端仍收到结果、全 ACK 仍能就绪、
死亡仍被复制、`SettlementsQueued == 0`、`CallbackExceptions >= 2`、不 fault）。

### 1.10 Snapshot 本地 Spec / uint wrap / 各 queue / 字典 TTL / Dispose 多 world

| 子项 | 判定 | 证据 / 处置 |
|---|---|---|
| `TryGetPlayerSnapshot` 深拷贝 | 正确 | `PMCombatSession.GetPlayer → Snapshot(...)` 造新对象；G 段一并验 `ResultSummary` 深拷贝 |
| uint wrap（activationId） | 正确且不可达 | `NextActivation` 到 `uint.MaxValue` 即 `false` + `ActivationWraps` + `InvalidActivation` fault，**不回绕 0**；到达需 4e9 次激活，本批不构造成例（属有界防御） |
| 各 queue 有界 | **修 1 处** | 入站 4 条队列 + Drain 每帧 64 条 + `_pendingAttacks` 512 都有界；但**满表检查曾在「已发包 + 已造弹」之后** ⇒ 改为**任何不可逆动作之前**（**P 段**：填满 512 条后第 513 次 ⇒ `QueueOverflow` fault 且 `FakesCreated`/`CancelledPredictions` 不变） |
| 字典 TTL | 正确 | `_pendingAttacks` 按 `PendingAttackTtlMs == PMCombatLimits.AttackRecordTtlMs(5000)` 退休（未终态计 `PendingAttackTimeouts`）；`_nextActivationByOwner` 有界（owner 数）；`_playersByNetId` 随 Unbind/Dispose 清理 |
| Dispose 多 world | 正确 | 只撤自己那一份（Settlement 订阅 + 本地账 + 接缝引用），不 Dispose R5、不碰别的会话；K 段（A 释放后 B 仍能完整跑通一次攻击） |
| **死代码清理** | 已删 | `_bufferedSettlements`（从未写入的死缓冲）与 `_auditBuffer`（从未读）；`BufferedSettlementCount` 改为**观测 R5 真实待取缓冲** |

### 1.11 `PMR3Runtime` Sender：只新 4 条复合 ID、fail-throw、旧逻辑不动 —— **复核为正确，逐字未改**

`SendRemoteRpc` 用 `IsProjectileRpc/IsCombatRpc`（**均按 `(ClassId, RpcId)` 对**）判定临界集合；
`ServerProbe` 与 movement 四条仍只 warn。J2 段给出正负对照：4 条战斗 RPC 在未接线世界**全抛**，
`ServerProbe` **不抛**、投射物 RPC **仍抛**。旁证：当前 ID 锁里只有 `PMR3Player` 承载 RPC，
不存在跨类同 `RpcId` 的值，判定口径仍按复合对写（防将来漂移）。

### 1.12 OwnerOnly 资源走真实 transport —— 正确（B 段双向）

AP 副本拿到自己的 mana/energy；SP 副本恒 0；受害者在自己客户端上的副本未被误扣；
攻击者在别的客户端上是 SP ⇒ 0。发布被权限拒绝时 `StatePublishRejected` 可观测（J1）。

---

## 2. 修正清单（精确落点）

| # | 文件 | 落点 | 修正内容 |
|---|---|---|---|
| F1 | `PMR6CombatDriver.cs` | `Pump` / 新 `TickModel` | DS 先 `core.Tick(now)`（回蓝）再处理上行攻击 |
| F2 | 同上 | `AuthorityPolicy.TryAuthorizeSpawn` | 先验宿主位置（可用 + finite），再 `core.TryAuthorizeProjectile` |
| F3 | 同上 | `PublishStateIfDirty` + `PublishedCombatState` | 「显式脏 **或** 时钟前进过」+ 值语义比对 ⇒ 回蓝可复制、无变化不重复写；断线名册成员不再让状态永久保持脏 |
| F4 | 同上 | `HandleAttackResult` | pending attack 终态先到先胜（Confirmed 后不吃 Rejected；Revoked 后不吃 Accepted）+ `AttackResultsLateAfterTerminal` |
| F5 | 同上 | `TryAttack` | 满表检查前移到「发包 / 造假弹」之前；失败时 `activationId = 0`（不外泄已消耗 ID） |
| F6 | 同上 | `OnProjectileSettlement` / `DrainBufferedSettlements` / `ApplySettlementFailure` | 结算异常在订阅边界消化并 `Fault(SettlementFailed)`，绝不退回 R5 缓冲 |
| F7 | 同上 | `NotifyPlayerDied` / `NotifyOutcomeFrozen` | 逐订阅者隔离异常 + `CallbackExceptions` + warn，不打断结算/结果链 |
| F8 | 同上 | `ApplySettlementNow` | 死亡上报改用本地 `_deathsReported`（不依赖 `player.CombatDead` 的发布时机） |
| F9 | 同上 | 死代码 / 观测 | 删 `_bufferedSettlements`、`_auditBuffer`；`BufferedSettlementCount`/`SettlementsBuffered` 改为 R5 真实缓冲口径 |
| F10 | `Tools/PMR6NetworkTest/Program.cs` | 新增 L–V 共 10 段（163 条断言）+ A 段常量补 2 条 | 见 §3 |

---

## 3. host API 变化（**精确列明**，含语义变化）

### 3.1 新增

| 成员 | 说明 |
|---|---|
| `PMR6CombatFaultReason.SettlementFailed = 13` | 结算应用链抛异常（已在订阅边界消化；一次故障 = 本会话报销） |
| `long SettlementApplyFailures` | 结算应用抛异常次数（本地观测） |
| `long AttackResultsLateAfterTerminal` | 终态不倒退被命中次数（迟到 Rejected / 迟到 Accepted，只观测不改写） |
| `long CallbackExceptions` | 宿主回调（`PlayerDied` / `OutcomeFrozen`）抛异常并被隔离的次数 |

### 3.2 语义变化（签名不变）

| 成员 | 旧语义 | 新语义 |
|---|---|---|
| `TryAttack(..., out uint activationId, ...)` | 发包后失败时可能返回**已消耗**的 ID | **失败恒为 0**（已消耗 ID 不外泄，避免宿主把失败调用当成一次激活） |
| `BufferedSettlementCount` | 本地（恒空）缓冲条数 ⇒ 恒 0 | **R5 真实待取缓冲**的条数（`_projectiles.PendingSettlementCount`） |
| `long SettlementsBuffered` | 恒 0（死缓冲） | 由 `Pump` 从 R5 待取缓冲**取回**的条数（正常路径恒 0；仅在「第三方订阅者异常/晚期接管」时 > 0） |
| `FlushState` 的发布行为 | 只要显式脏就整包 9 字段重写 | 显式脏**或** Tick 前进过时重新比对，**只写有变化的玩家**（无变化不再触发 9 条 RepNotify） |

### 3.3 未变（冻结面保持）

ctor 参数与校验、`BindPlayer/UnbindPlayer/IsPlayerBound`、`AddPlayer/StartMatch`、
`Pump/FlushState/UpdateClock/WallTimeMs`、全部只读视图与事件、`CreateAuthorityPolicy`、
`Dispose` 语义（只撤自己那一份）、`MaxInbound*/MaxPendingAttacks/MaxBufferedSettlements/MaxApplyPerPump/ResultResendMs/ResultGraceMs/PendingAttackTtlMs` 常量值。
**`PMR5ProjectileDriver` 与 `PMR3Runtime` 的公开面本次零变化**（`CancelPredictedActivation` 沿用上一批，未加 out 参数）。

---

## 4. 测试（新增 10 段 / 163 条断言）

| 段 | 覆盖与关键断言 |
|---|---|
| **L** 回蓝先于请求判定 | 耗干 90 蓝后构造边界：`frame1=+1ms`、`frame2` 不动、`frame3=+1000ms`（已实测「发出后第 3 帧」被处理）。断言：被接受（`WasActivationConfirmed`）、core 蓝量 `0 = 回 30 - 扣 30`、`AttacksProcessed == 3+1`。**旧次序下该攻击被 `InsufficientMana` 终态拒绝** |
| **M** 无效枪口不占槽 | 位置不可信 ⇒ 两颗 spawn 全拒、`coreKeys == 0`、`ServerSpawnsAuthorized == 0`、无权威对象、客户端两颗假弹被真实撤销；**枪口恢复后同一颗弹仍被授权**（`+1`、有权威对象） |
| **N** 终态不倒退 | 完整 Accepted 流程后伪造**真实下行迟到 Rejected**：仍 Confirmed、未 Revoked、`PredictionsCancelled`/`FakesRevoked`/`FakeCount` 不变、`AttackResultsLateAfterTerminal` +1、不 fault |
| **O** 部分 TryFire 失败 | 预测登记表填到只剩 1 位（用另一 owner 填、零 RPC）：返回 false、原因含「部分投射物预测失败」、`SendFailed` fault、**已发出的那颗被真撤**（`CancelledPredictions`/`FakesRevoked` 各 +1）、无 pending 残留、`activationId == 0` |
| **P** pending 表满 | 佩佩（1 发/次）分批填满 512（每批后跑 2 帧清可靠窗口）：第 513 次拒绝 + `QueueOverflow` fault，且 `FakesCreated`/`CancelledPredictions` **不变**（满表在不可逆动作之前被发现）、表大小仍 512 |
| **Q** 回蓝复制 | 一次攻击后只推进 3s 墙钟（零战斗事件）：core 90 且 **AP 副本也 90**、`StatePublishCount` 增长 |
| **R** 宿主回调异常隔离 | 两个订阅者都抛：core 仍终局、摘要仍生成、结果仍下发且全 ACK 仍能就绪、**死亡仍被复制**、`SettlementsQueued == 0`、`CallbackExceptions >= 2`、不 fault |
| **S** 结算恰好消费一次 | 2 颗弹 × 1 目标 ⇒ `SettlementsApplied == 2` 精确、`SettlementsRejected == 0`、R5 缓冲 0、HP 精确；重复/迟到命中不再产生第二次应用 |
| **T** 冲突 outcome 保持终局 | 伪造同 outcomeId 不同胜方 ⇒ `ConflictingOutcome` fault、已存 outcomeId/胜方不被改写、DS 冻结结论不受影响 |
| **U** Unbind 少一个等待 | 2v2 摘掉 1 个 owner（不触发 forfeit）：`ResultAckCount == 3` 即就绪且来自 ACK 路径；被摘 owner **不再收到结果** |
| **V** Drain 兜底幂等 | 真实挂第三个会抛的订阅者 ⇒ R5 退回缓冲（`SettlementsQueued == 1`）⇒ 驱动 Drain 取回（`SettlementsBuffered == 1`）⇒ 缓冲归零且 **HP 只扣一次** |
| A 段补充 | `PendingAttackTtlMs == PMCombatLimits.AttackRecordTtlMs`；`SettlementFailed == 13` |

**测试独立性**：全部走**真实 PMNetWorld / PMNetSessionBridge / 真实 PMTransport 字节链 / 真实生成 RPC 桩 /
真实 PMCombatSession / 真实 PMR5ProjectileDriver / 真实 PMR6CombatDriver**；不直接调业务方法冒充网络；
新增两处「帧级时钟控制」（`Rig.Now` 显式赋值）用于把边界落在确定帧上，并在注释里写明依据。
**未依赖任何「另一组正在收口」的旧宽容行为**：不依赖「重复 key 绕过 Expired/Ended/Connected」、
不依赖「满表拒绝不占 ID 水位」、不依赖「StartMatch 允许断线成员」、不依赖「attacker 断线仍可结算」
（现有 220 条 + 新增 163 条均在 `PMCombatSession.cs` 收口后（MD5 `0ab0efe7d7ff6da0c756a862a9f9aa5d`）实测通过）。

---

## 5. 变异测试（证明门禁有牙齿）

| 变异 | 结果 |
|---|---|
| M1 `Pump` 退回「先请求后 Tick」 | ✅ 捕获：`L ★ 该攻击被判 Accepted`、`L 该攻击未被反向拒绝` |
| M2 策略退回「先 core 后位置」 | ✅ 捕获：`M ★ 无效枪口没有在 core 占掉授权槽/key（期望 0，实际 2）` |
| M3 去掉 pending 终态先到先胜 | ✅ 捕获：`N ★ 未被标记为拒绝终态`、`N 该迟到裁决被显式计数` |
| M4 满表检查退回 TryFire 之后 | ✅ 捕获：`P ★ 满表在发包/生成假弹之前被发现（FakesCreated 512→513）`、`P 没有需要撤销的假弹` |
| M5 回调不隔离（直接调用） | ✅ 捕获 8 条：`OutcomeFrozen` 被调用 0 次、未冻结、无摘要、未下发、死亡未复制、未就绪、`CallbackExceptions == 0`、会话 fault |
| M6 发布退回只看 `_stateDirty` | ✅ 捕获：`Q ★ AP 副本蓝量（期望 90，实际 60）`、`Q 回蓝确实触发了一次复制发布` |
| M9 `DrainBufferedSettlements` 早退 | ✅ 捕获：`V ★ 驱动 Drain 兜底确实取回了它（期望 1，实际 0）`、`V 缓冲已清空（期望 0，实际 1）` |
| M5+M8（= 上一批真实行为：回调不隔离 **且** 结算不自己消化异常） | ✅ 捕获：`R ★ 结算没有因订阅者异常被退回 R5 待取缓冲（期望 0，实际 1）`、`R 无「无消费者」结算（实际 1）`、`R 回调异常被显式计数（实际 1）` —— **直接复现了「双消费通道」** |
| M7 死亡幂等退回读 `player.CombatDead` | ❌ **未被捕获（不可达）**：当前「首杀即终局」⇒ 同一帧的第二次结算先被 `MatchEnded` 拒，死亡扫描根本不会再看到「已死但复制字段尚未更新」的玩家。该改动属**防御性**（解除对发布时机的耦合），已在 §7 列明无可失败断言 |

所有变异均已按原文本逐字还原；还原后 `PMR6CombatDriver.cs` MD5 与变异前一致（`c45ac6be61f30548da75d589d63c302c`），并重跑 build(0/0)+run(383/0)。

---

## 6. 回归门禁（本批实测，最终指纹）

| 门禁 | 结果 |
|---|---|
| `Tools/PMR6NetworkCheck`（netstandard2.0 + C# 7.3，零 Unity） | **0 警告 / 0 错误** |
| `Tools/PMR6NetworkTest` 构建 / 运行 | **0 警告 / 0 错误** / **通过 383 / 失败 0**，exit 0 |
| `Tools/PMR5NetworkCheck` / `Tools/PMR5NetworkTest` | 0 错误 / **PASS**（417 项） |
| `Tools/PMR3RuntimeTest` / `Tools/PMR3IntegrationTest` | **180 / 0** / **143 / 0** |
| `Tools/PMR6DeclarationTest` / `Tools/PMR5DeclarationTest` | **235 / 0** / **139 / 0** |
| `Tools/PMCombatCoreCheck` / `Tools/PMCombatCoreTest` | 0 错误 / **1699 / 0**（另一组本轮收口后的计数） |
| `Tools/PMProjectileIntegrationCheck` / `Test` / `LifecycleTest` | 0 错误 / **977 断言** / **510 断言**（全通过） |
| `Tools/PMR4NetworkCheck` / `PMR4IntegrationTest` / `PMR4NetworkTest` | 0 错误 / **113 / 0** / **FAILED：2 条 R6-A2 遗留过时计数断言**（`复制属性位宽 == 3`、`复制描述符属性数 == 3`，实际 12；写边界之外，未改） |
| `Tools/PMDeclCheck` / `Tools/PMNetVerify` | **全部通过** / **27 / 0** |
| `Tools/PMClientCheck` / `PMUnityGlueCheck` / `PMR3UnitySmoke` | **0 错误**（C#7.3 编译回归，顺带证明 R6 driver 改动在 7.3 下可编） |
| `Server/Server.csproj`（Lobby，`Exclude` 掉 R6 driver） | **0 错误** |
| `python Tools/check_cs_braces.py`（两份 .cs） | **PASS**（括号配平、深度非负、末尾归零） |
| 编码自检 | 两份 .cs 均 `BOM=True` + 仅 CRLF（无 lone LF） |

文件指纹（供对账）：

| 文件 | MD5 | BOM/换行 |
|---|---|---|
| `Client/Assets/Scripts/PMR3/PMR6CombatDriver.cs` | `c45ac6be61f30548da75d589d63c302c` | BOM + CRLF |
| `Tools/PMR6NetworkTest/Program.cs` | `1dfb76fca94a69eb9270b0296f8d1501` | BOM + CRLF |
| `Client/Assets/Scripts/PMCombat/PMCombatSession.cs`（**只读消费**，另一组本轮收口） | `0ab0efe7d7ff6da0c756a862a9f9aa5d` | BOM + CRLF |

---

## 7. 未验证 / 诚实边界

| # | 项 | 说明 |
|---|---|---|
| U1 | **host 仍未接线** | `PMClientSessionHost` / `PMDsSessionHost` 尚未创建本驱动；本批**未**改宿主（写边界外）。§1 的所有修正都在驱动内部，接线后**必须**同时：移除 `PMDsSessionHost` 的诊断 `Settlement` 订阅（否则双消费者） |
| U2 | 死亡后的引擎副作用 | `PlayerDied` 只提供事件；freeze Mover / 写 `history.Alive` 仍属宿主 |
| U3 | 「只有回蓝」在**真实客户端**的表现 | Q 段断言的是 DS→AP 的复制链路（真实字节链 + OwnerOnly 权限）；Unity 表现层未验 |
| U4 | `ActivationWraps` 路径 | 需 4e9 次激活，属有界防御，本批不构造成例 |
| U5 | `SettlementFailed` / `CallbackExceptions` 的生产可达性 | 需要会抛的宿主回调或 core 异常；测试用宿主回调构造（R/V 段），core 自身异常未构造 |
| U6 | `_deathsReported`（F8） | 防御性：当前「首杀即终局」下同帧双应用不可达（变异 M7 无法被捕获），已在 §5 注明 |
| U7 | 「已接管镜像 + 攻击级 Rejected」组合 | 靠**可靠性保序 + 修正 F4** 判定为不可达（§1.5），非实测可达路径 |
| U8 | 实机 / 真 UDP / PhysX | 仍用「真实 PMTransport 字节链 + 手工 link/hub」，不是跨机 MTU / 真实 socket |
| U9 | 结果摘要的 Lobby 消费面 | 仍只 `log`；本批未改 Lobby（`Docs/plans/_r6_network_report.md` §7 U9 欠账仍在） |
| U10 | `PMR4NetworkTest` 的 2 条过时断言 | 需把 3 改成 12；写边界之外，未改 |

---

## 8. 已检查范围

**完整读取的文档（按委派顺序）**：`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`（全文）
→ `Docs/plans/net-r6-combat-contract.md`（全文，含「B 驱动冻结补充」）
→ `Docs/plans/_r6_network_report.md`（全文）→ `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`（全文）；
补充对齐（只读）：`Docs/plans/_r6_core_terminal_fix.md`（另一组本轮 core 收口口径）。

**为判定而对读的真实实现**：`PMR6CombatDriver.cs`（全文）、`PMR5ProjectileDriver.cs`（全文 + 关键段精读：
`HandleServerSpawn`/`HandleClientDecision`/`RevokeFake`/`CancelPredictedActivation`/`DrainCoordinatorOutlets`/
`ResolveActivation`/`TryFire`/`Pump`）、`PMR3Runtime.cs`（全文）、`PMR3Player.cs`（战斗声明段）、
`PMCombatSession.cs`（全文）、`PMCombatContracts.cs`、`PMCombatWeaponPlanner.cs`（关键段）、
`PMProjectileLifecycle.cs`（`TrySetActivationResult`/`TryGetActivationVerdict`/`TryRegisterPredicted`/
`CreateEntry`/容量与水位）、`PMProjectileCoordinator.cs`（`ResolveActivation`/`RequestSpawn` 段）、
`PMProjectileContracts.cs`（上限）、`BattleNumericConfig.cs`（英雄表 / `ResolveAttack`）、
`Docs/plans/pmnet-r3-ids.json`（交叉核对 4 条战斗 RPC 的 `(ClassId, RpcId)` 与跨类碰撞）、
`Tools/PMR6NetworkTest/Program.cs`（全文重写新增段）。

**本次写入的文件**：§6 指纹表中的前两行 + 本文件（共 3 个）。**未**创建/修改任何其他文件（含 `.meta`）。

---

## 9. 复现命令

```bash
# 语言面（netstandard2.0 + C# 7.3，零 Unity）
dotnet build Tools/PMR6NetworkCheck/PMR6NetworkCheck.csproj -c Release     # 0 警告 0 错误
# 行为面（真实字节链）
dotnet build Tools/PMR6NetworkTest/PMR6NetworkTest.csproj -c Release      # 0 警告 0 错误
dotnet Tools/PMR6NetworkTest/bin/Release/net8.0/PMR6NetworkTest.dll       # 通过 383 / 失败 0
# 回归
dotnet Tools/PMR5NetworkTest/bin/Release/net8.0/PMR5NetworkTest.dll       # PASS
dotnet Tools/PMR3RuntimeTest/bin/Release/net8.0/PMR3RuntimeTest.dll       # 180 / 0
dotnet Tools/PMR3IntegrationTest/bin/Release/net8.0/PMR3IntegrationTest.dll # 143 / 0
dotnet Tools/PMR6DeclarationTest/bin/Release/net8.0/PMR6DeclarationTest.dll # 235 / 0
dotnet Tools/PMCombatCoreTest/bin/Release/net8.0/PMCombatCoreTest.dll     # 1699 / 0
dotnet Tools/PMDeclCheck/bin/Release/net8.0/PMDeclCheck.dll               # 全部通过
python Tools/check_cs_braces.py Client/Assets/Scripts/PMR3/PMR6CombatDriver.cs Tools/PMR6NetworkTest/Program.cs
```

---

## 10. 本次未做

- 未接线 `PMClientSessionHost` / `PMDsSessionHost`（含 U1 的「移除诊断 Settlement 订阅」）—— 写边界之外；
- 未改 `PMR5ProjectileDriver.cs` / `PMR3Runtime.cs`（复核为正确）；
- 未改 core / `PMCombatContracts.cs` / 生成产物 / 锁文件 / 主计划 / Lobby；
- 未启动 Unity、未做任何 SVN/git 写操作、未提交、未递归委派。
