# R6-C DS 宿主接线对抗复核（PMDsSessionHost）

> 范围：`Docs/plans/net-r6-combat-contract.md` 尾段「C 宿主接线冻结」在 **DS 宿主**上的实际接线；
> 对抗复核（不是重述实现报告），对任务列出的 13 个疑点逐条给出源码证据与判据。
>
> 写入边界：**只**改 `Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs` 与本报告。
> `PMR6CombatDriver.cs` / `PMCombatSession.cs` / `PMR5ProjectileDriver.cs` / `PMProjectileCoordinator.cs` /
> `PMR4MovementDriver.cs` / `PMR3Player.cs` / `PMDsLobbyAgent.cs` / `PMClientSessionHost.cs` **只读**，
> 未改动（其中发现的越界问题在本报告 §4 精确点名，**不**在宿主叠旁路绕过去）。
> 未启动 Unity、未跑真 UDP 端到端、未提交、未 SVN 写、未递归委派。

---

## 0. 结论摘要

| 任务疑点 | 结论 | 证据落点（PMDsSessionHost.cs，行号为**修改后**） |
|---|---|---|
| ① roster 原始 Team/Hero/Uid 与 NetId 映射 | **正确** | 957 / 1879 / 1918-1920；`_formalTeamIndexByUid` 在正式路径未被引用 |
| ② AddPlayer 与首 flush 前 combat 初值 | **正确** | 1267（时钟早于 `_endpoint.Pump`）→ 1783-1893（初值在握手段内发布） |
| ③ Start 要求全 expected ready / 30s / 不误 smoke | **正确** | 793 / 486 / 1908-1939 / 1946-1959 / 2341-2342 |
| ④ 同一 monotonic now 初始化、每 Pump 资源与 policy | **正确** | 1267 / 1285 / 1295 / 2212-2215 / 2631-2632 |
| ⑤ R6.Pump 与 R5.Pump 是否每子步重复 Tick 或次序错 | **正确** | 1267-1305 次序；1966-1982 每宿主帧只 R6.Pump 一次；2826-2845 R5 子步 |
| ⑥ 死亡 Freeze 后被当断线 Unbind 触发额外 Forfeit | **正确（不会）** | 1408 判据为 `OwnerConnection.IsReady`；死亡路径 2032-2087 无 Unbind |
| ⑦ modelDead 的 history Alive=false 是否被 History 拒绝 | **未被拒**（且是硬闸） | 3112-3148 → `PMProjectileHistory.Record` 同帧 `ReplaceNewest`；`IsTargetStillSettleable` 读 `sample.Alive` |
| ⑧ 敌队 filter 用 rawTeam、无 0 fallback | **正确** | 2226-2262 只用 core 的 `TeamId`，未知身份 fail closed |
| ⑨ Ended 后停止推进、不再被 smoke/checkdrop Fail 提前退出 | **正确** | 1374 / 2099-2130 / 2815-2821 / 2326-2329 / 2341-2342 |
| ⑩ ResultReady 后 SubmitResult 恰好一次 / 冻结摘要 / ACK 退出 | **正确** | 2136-2170（`_combatSubmitted` 单次 + `ResultSummary` 深拷贝）+ 1305 早于 1313 |
| ⑪ Settlement 只 R6、无 diagnostic 双订阅/双 drain | **正确** | 2674-2682（`Settlement +=` 仅 smoke）+ 2856 |
| ⑫ Dispose 取消 R6 再 R5、失败初始化完整清理 | **正确** | 2730-2780 次序；6 条失败路径均 `ReleaseProjectileWiring()` |
| ⑬ PlayerDied 回调异常被吞 ⇒ host 必须 Fault 别假成功 | **真缺陷（P1，已修）** | §3：2078 新增 fail-closed；日志不再谎报成功；且**不**重放结算 |

**本次改动仅一处**（`OnCombatPlayerDied` fail-closed），未改任何 driver/core/声明/Lobby；未新增旁路。
**门禁**：`dotnet build Tools/PMR4UnityCheck -c Release -o Tools/PMR4UnityCheck/bin/r6-ds-review` → **0 错误 / 0 警告**
（上一轮报告 §0 里「官方门禁整体红」已被主侧补 `IMGUIModule` 修掉，本轮整体绿）。
**回归**：`Tools/PMR6NetworkTest` → **通过 383 / 失败 0**。
**真实运行仍未验**（见 §6）。

---

## 1. 复核基线（可核对的指纹）

| 项 | 值 |
|---|---|
| 复核起点 | `PMDsSessionHost.cs` **3559 行** / UTF-8 **BOM** + 全 CRLF（0 lone LF）/ MD5 `ad5446c33b531beff5e8b974ea5f47c5` |
| 该起点 == `_r6_ds_host_report.md` 登记的被审版本 | 是（报告 §1 表里同一 MD5）⇒ 本报告审的就是实现报告描述的那一版 |
| 复核终点 | **3591** CRLF（0 lone LF）/ BOM 保留 / MD5 `f0bdb5b26b722a46f718d05a7246e5db` |
| **字节级改动证明** | 把 §3 的新块**反向替换**回旧块后重新计算 MD5 == `ad5446…f47c5`（`/tmp/verify_reverse.py`）⇒ 全文件差异**只有** §3 那段 |
| 花括号/圆括号 | `Tools/check_cs_braces.py` PASS（`{}` 498/498、`()` 1125/1125、深度全程非负末尾归零） |

---

## 2. 逐条复核（任务列出的 13 个疑点）

### ① 真实 roster 的原始 Team/Hero/Uid 与 NetId 映射 —— 正确

- `_combatEnabled = !options.ServerSmoke;`（793）。
- 名册在 `Initialize` 的引导文件环里登记（`boot.Bootstrap.AsBootstrap.Players`，即 Lobby 下发的认证名册）：
  `_rosterRowByUid` / `_rosterTeamByUid` / `_rosterHeroByUid[uid] = roster[i].Identity.HeroId`（957），
  三个字典**同一次写入**（同一 `if (!_rosterRowByUid.ContainsKey(uid))` 体内）⇒ 不会出现「行/队有了、英雄没登记」。
  登记发生在 `OpenServer`（1141）**之前**，所以任何 `OnConnected` 都能查到。
- `AttachCombatPlayer`：`int teamId = _rosterTeamByUid[uid]` / `int heroId = _rosterHeroByUid[uid]`，
  然后 `_combatDriver.AddPlayer(player, uid, teamId, heroId)`（1879）。core 的 netId 取 `player.NetId`（DS 自己
  `world.Spawn` 出来的），不是上行字段。
- **不含 formalTeamIndex**：`_formalTeamIndexByUid` 只在 `TryResolveAuthoritativeTeam`（smoke 的
  `AcceptDiagnosticHit`）里被读；R6 授权/命中/结算路径**零引用**（grep 证据：该标识符仅出现在
  `BuildFormalRoster` 与 `TryResolveAuthoritativeTeam`）。契约「原始 TeamId 不可替换成 formalTeamIndex0/1」成立。
- 附带核对（`TryGetCombatOwnerPosition`:2178 / `OnCombatPlayerDied`:2032 依赖它）：`player.Uid` 与
  `_playersByUid`/`_driversByUid` 的键同源 —— `PMR3Runtime.SpawnPlayer` 里 `player.PMNet_Set_uid(owner.Identity.Uid)`
  （PMR3Runtime.cs:371），而宿主用 `connection.Identity.Uid` 作键 ⇒ 一致。

### ② AddPlayer 与「首 Flush 之前」的 combat 初值 —— 正确

- `OnConnected`（1722）由 `_endpoint.Pump` 的握手接纳段内调用；其中依次
  `AttachMovementDriver`（1783，内部 `PublishInitialSnapshot`）与 `AttachCombatPlayer`（1848），
  后者在 `AddPlayer` 之后立刻 `_combatDriver.FlushState(_projectileWallNowMs)`（1891）。
  桥的 Update/生命周期派发在同一次 Pump 的**之后**发生，因此 9 条战斗复制值随 Create 记录到达客户端
  （不是「先收到 hero/team/HP 全 0」）。
- 时钟前提：`_projectileWallNowMs = (double)nowMs;`（1267）在 `_endpoint.Pump(nowMs, ...)`（1276）**之前**赋值
  ⇒ 握手回调里发布初值用的是本帧同一个 clock，R6 驱动 `_wallStarted/_wallMs` 从该值起算，
  不出现「初始发布用 0 时钟、下一帧 Pump 与它比单调」的错配。

### ③ StartMatch 门 / 30s 期限 / smoke 不被误门 —— 正确

- `TryStartCombatMatch`（1908-1939）：`_expectedUids.Count != 0 && _playersByUid.Count == _expectedUids.Count`，
  且逐成员 `OwnerConnection.IsReady`（1918）+ `_combatDriver.IsPlayerBound(member)`（1919）+
  `_combatModel.GetPlayer(netId) != null`（1920）+ 运动 driver 存活；全过后才 `StartMatch(_expectedUids.Count)`（1929），
  `_combatStarted` 只置一次。
- 非名册 uid **无法**混进来凑齐：`OnConnected` 对名册外 uid 在 `AttachCombatPlayer` 处 fail closed
  （无 `_rosterTeamByUid`/`_rosterHeroByUid` ⇒ `Fail`），不会「悄悄少加一个」。
- 30s：`CombatStartDeadlineMs = 30000`（486）；`CheckCombatStartDeadline`（1946-1959）在第一帧（`_combatStartDeadlineMs == 0L`，1950）
  起算，超时 `Fail` 且**不伪造胜负**。它挂在 `_combatEnabled && !_combatStarted` 上。
- smoke 不被该门拦：`_combatEnabled=false`（793）⇒ 1908 早退；`CheckSmoke`（2339 起）只有在
  `_options.ServerSmoke` 为真时才提交（判定在 2341-2342）⇒ 两条路径互斥，smoke 不受 30s 开局门约束，也不走 R6 结算。

### ④ 同一 monotonic now 初始化 / 每 Pump 资源与 policy —— 正确

- 一帧内只有**一个**墙钟值（1267 赋值），喂给：
  `R6.Pump`（1285 → 1973）、`R5.Pump` 全部子步（2807 重新赋同一个 `(double)nowMs`，2867-2875 使用）、
  `R6.FlushState`（1295 → 1995）、`OnConnected` 初始发布（1891）、授权策略 `GetCombatPumpWallNowMs`（2212-2215）。
- 策略注入面不含 Unity 时间：`PMR6CombatDriver.CreateAuthorityPolicy(_combatModel, TryGetCombatOwnerPosition,
  GetCombatPumpWallNowMs)`（2631-2632），R6 侧 `AuthorityPolicy` 用 `_wallClockNow()` 作为 `now`；宿主传的
  就是本帧 Pump 值 ⇒ 「同一帧所有 core 入口共用一个单调 clock」成立。
- 结算应用时钟：R6 `ApplySettlementNow` 取 `max(_wallMs, settlement.WallTimeMs)`，而 `settlement.WallTimeMs`
  来自 R5 的 `_lastWallMs`（同一帧值）⇒ core `AcceptClock` 不会因「结算比 Tick 旧」而 `InvalidClock` 丢伤害。
- 时间源 `Time.realtimeSinceStartup * 1000f` 转 `long`：float32 舍入只会让相邻帧取到**相同**毫秒，
  不会反向 ⇒ R6/R5 的 `NonMonotonicWallClock` 不会误触发（长时间运行精度约 1ms/3h，单局无影响）。

### ⑤ R6.Pump 与 R5.Pump 是否每子步重复 Tick 或排错 —— 正确

- 宿主每帧次序（`Pump`，1267-1313）：`_endpoint.Pump`（1276）→ `PumpMovement`（1279）→ `PumpCombatOnce`（1285）
  → `PumpProjectiles`（1290）→ `FlushCombatOnce`（1295）。与契约「endpoint→movement→R6.Pump→R5.Pump→R6.FlushState」逐字一致。
- **R6.Pump 每宿主帧只一次**（1966-1982 内只有一处 `_combatDriver.Pump`，1973）；core.Tick（回蓝）只在
  R6 驱动的 `TickModel` 里发生 ⇒ 不会被 R5 的 8 个子步重复 Tick，也不会出现「R5 子步 × core 时钟」错配。
- R5 子步（2826-2845）是**固定 16ms 累加器**，全部用同一个 `_projectileWallNowMs`；R5 的
  `Pump(wallNowMs, stepMs)` 对墙钟是幂等的、只按 `stepMs` 推进 ⇒ 这是单一 catch-up，不是第二次 tick。
- 次序不是风格而是正确性：同一帧到达的「AttackV1（可靠）+ 逐颗 spawn（不可靠）」中，spawn 由
  `R5.Pump` 处理，而攻击授权账（`ResolveActivation` / core `RequestAttack`）在**更早**的 `R6.Pump` 里建好
  ⇒ 「宿主队列处理必须先战斗授权后 ProjectilePump」成立；结算在 `R5.Pump` 内回调、`R6.FlushState` 在之后，
  ⇒ 本帧「伤害/死亡/胜负」与「回蓝」在同一个 FlushState 一起复制。

### ⑥ 死亡 Freeze 之后是否被当断线 Unbind、触发额外 Forfeit —— 正确（不会）

- 死亡路径（2032-2087）**只**做两件事：补 `Alive=false` 历史样本 + `driver.Freeze()`；
  **不**摘接缝、**不** `UnbindPlayer`、**不** `core.Disconnect`。
- 断线判据在 `PruneDisconnectedDrivers`（1393-1470）：先看 `player.OwnerConnection != null &&
  player.OwnerConnection.IsReady`（1408）→ 死者连接仍就绪 ⇒ `continue`，不进入 Freeze/Dispose/Unbind 分支。
  `core.Disconnect`（⇒ `EvaluateForfeit`）只在 `_combatDriver.UnbindPlayer(player)`（1447）里发生。
- 结论链路也按此对齐：`AllConnectedOwnersAcked` 跳过非 `Connected` 的成员 ⇒ 死者仍要 ACK，
  终局不会因死亡而少一个等待者；而 `_resultAcked` 只在 `UnbindPlayer` 里被移除（R6 驱动），
  即「真断线才少一个」。契约「死亡 ≠ 掉线」成立。

### ⑦ modelDead 的 `history.Alive=false` 更新是否被 History 拒绝 —— 未被拒（且该样本是硬闸）

- 写入端 `RecordProjectileUnavailableSample`（3112-3148）：`Epoch/NetId/StreamVersion(=1)`、
  `ServerFrame = _movementServerFrame`（在 `PumpMovement` 里已推进为 `AuthorityServer` 域，1357）、
  `OutputFrame = driver.OutputBoundary`、`TotalSimTimeMs = driver.AuthorityTotalSimTimeMs`、
  `WorldTimeMs = _projectileWallNowMs`、`Alive=false`（3136）。
- 与「同帧先写的那条 Alive=true 样本」逐字段相等（同帧、同输出边界、同仿真时间、同墙钟），
  因此 `PMProjectileHistory.Record` 走 **同 ServerFrame 分支**：新记录的 OutputFrame 不更旧 ⇒
  `stream.ReplaceNewest(sample)`（不是 append/regression 拒绝）。**该分支不是猜测**：`Record` 的时间单调
  检查在「同 ServerFrame」之前，只有它先失败才会拒；而两值相等 ⇒ 不失败。⇒ 本帧样本被就地翻成 Alive=false。
- 这条写入是**载荷**而不是装饰：DS 侧命中结论确实读它 ——
  `PMProjectileCoordinator.IsTargetStillSettleable` 里 `if (!sample.Alive) { return false; }`
  （PMProjectileCoordinator.cs:1917），即 Alive=false 会真实挡掉该目标的结算。
  （旁证：`PMR5ProjectileDriver` 自身不读 `_history`，history 由它对 `_coordinator` 的构造注入消费经
   `HandleServerHit → _coordinator.ReportHits`；初看像「没人读」，实际是**活的**，见 §4 备注。）
- 第二道兜底：命中过滤 `AcceptCombatHit`（2226-2262）复核 core 的 `Dead/Connected`。

### ⑧ 敌队 filter 是否用 rawTeam、有无 0 fallback —— 正确

- `AcceptCombatHit`（2226-2262）：`owner = _combatModel.GetPlayer(projectile.OwnerNetId)`、
  `victim = _combatModel.GetPlayer(target.NetId)`；任一为 null ⇒ 拒（fail closed，不猜身份）；
  self 拒；`!Connected || Dead` 拒；`owner.TeamId == victim.TeamId` 拒。
  team 只来自 core 名册（即 §① 的原始 TeamId），**没有** `formalTeamIndex`、**没有** 0/偶数行之类 fallback。
- 带 fallback 的 `TryResolveAuthoritativeTeam` 只服务 smoke 的 `AcceptDiagnosticHit`；R6 路径不经过它
  （`CreateProjectileWiring` 2645 只给 smoke 装 `RosterIdentityHitFilter`）。

### ⑨ Ended 之后停止推进 / 不被 smoke、checkdrop 提前 Fail —— 正确

- 终局：`OnCombatOutcomeFrozen`（2099-2130）冻结全部运动 driver 并置 `_combatOutcomeFrozen`；
  次帧 `PumpProjectiles` 顶部分支（2815-2821）清累加器、只 `PumpProjectileOnce(0)`（排声明入站）+
  视图排空，**不再有任何运动子步**；`PumpMovement` 对 `IsFrozen` 的副本只 `Update` 不 `Pump`（1374）。
- 权威侧也封住：core 已 `Ended` ⇒ `RequestAttack`/`TryAuthorizeProjectile`/`ApplySettlement` 全拒
  （PMCombatSession：`_outcome.Ended` 分支），R5 的 spawn/hit 也过不了 core 授权。
- 提前退出面：`CheckPlayerDrops`（2313-2332）只有在 `_options != null && _options.ServerSmoke` 时才 `Fail`
  （2326-2329）⇒ 正式玩法里「结果之后客户端断线」不会把局判失败；`CheckSmoke`（2339-2342）在非 smoke 早退。
- 次序：`SubmitCombatResultIfReady()`（1305）在 `CheckPlayerDrops()`（1308）之前 ⇒ 「先提交结果、再做断线收尾」。

### ⑩ ResultReadyForLobby 后的提交与退出闭环 —— 正确

- `SubmitCombatResultIfReady`（2136-2170）：`_combatSubmitted` 单次门（入口 2138 + 置位 2145/2163）；
  `_lobby.HasResult` 时只置位不二次提交（2142-2146）；摘要取 `_combatDriver.ResultSummary`
  （R6 侧返回 `(byte[])summary.Clone()`，即**冻结摘要的深拷贝**，之后不会被任何断线/重复结算改写）；
  winner 取 `_combatDriver.WinnerTeamId`（`_outcomeFrozen` 后才有值，即名册原始 TeamId，0 表示无唯一胜者）。
- 提交发生在 `_lobby.Pump`（1313）**之前** ⇒ 结果可同帧上线；重发/ResultAck/Exited 全在
  `PMDsLobbyAgent`（本轮未改）里闭环，宿主只负责「提交一次」。

### ⑪ Settlement 只由 R6 消费、无 diagnostic 双订阅/双 drain —— 正确

- `CreateProjectileWiring`：R6 分支订阅 `PlayerDied`/`OutcomeFrozen`（2674-2675）；
  `_projectileDriver.Settlement += OnProjectileSettlement;` 只在 smoke 的 `else` 分支（2681）。
  R6 会话里 R5 结算的**唯一**消费者是 `PMR6CombatDriver` 构造里的订阅。
- `PumpProjectiles` 的兜底 drain 被门住：`if (!_combatEnabled) { DrainDiagnosticSettlementBuffer(); }`（2856）
  ⇒ 正式玩法不会有「事件 + 宿主 drain」两次消费。
- 宿主诊断计数（`DiagnosticProjectileHitCount` / `...SettlementCount`）在 R6 下恒为 0，
  `DiagnosticCountersMeaningfulOnlyInSmoke` 的语义与实现一致。

### ⑫ Dispose 取消 R6 再 R5、失败初始化完整清理 —— 正确

- `ReleaseProjectileWiring`（2730-2780）次序：解 `PlayerDied/OutcomeFrozen` 订阅（2738-2739）→
  `_combatDriver.Dispose()`（2743）→ `_combatModel = null`（2750，core 非 IDisposable）→
  解 `Settlement` 订阅 + `_projectileDriver.Dispose()`（2754-2757）→ `_projectileMotion.Dispose()`（2765）。
  反序会让驱动继续持有已释放的 R5 ⇒ 现在的次序正确。
- 宿主总释放：`Dispose()` 在 `_bridge.Dispose()`（2517）与场景/地图释放**之前**调用它（2482），
  因此 R5 销毁本会话权威对象时桥仍完整。
- 失败初始化完整性：`CreateProjectileWiring` 的每条失败路径都调 `ReleaseProjectileWiring()`
  （2608 / 2623 / 2638 / 2656 / 2670 / 2689，幂等），`Initialize` 的接线失败路径（1131）也调；
  `Start()` 在 `Initialize` 失败时仍 `host.Dispose()` ⇒ 无半成品接线悬空。

### ⑬ PlayerDied 回调异常被吞 ⇒ host 必须 Fault 别假成功 —— **真缺陷（P1，已修）**

见 §3。原实现里 `RecordProjectileUnavailableSample` 返回 false 与 `driver.Freeze()` 抛异常都只
`Warn`/静默，然后无条件 `_combatDeathsReported++` 并打印
「的 Mover 已 Freeze、历史已补 Alive=false」——**日志在谎报**，而失败模式下死者会继续被
`PumpMovement` 推进（`Freeze` 未生效 ⇒ `IsFrozen` 仍 false ⇒ 1374 不跳过）⇒ 正是
「history 与 movement 不同步 + 假成功」。

---

## 3. 本次修正（**唯一**改动，精确落点）

**文件**：`Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs`（3591 CRLF / BOM / MD5 `f0bdb5b26b722a46f718d05a7246e5db`）
**方法**：`OnCombatPlayerDied`（2032-2087）+ 新增 5 行私有静态助手 `CombineDeathFailure`（2089-2093）。

```csharp
// 修正前（**改前 3559 行版本**的 2041-2054）：失败只 Warn / 静默，然后无条件宣布成功
if (RecordProjectileUnavailableSample(uid, player, driver, false))
{
    _combatDeathHistorySamples++;
}
try { driver.Freeze(); }
catch (Exception ex) { Warn("死亡冻结运动驱动异常（uid=…）：" + ex.GetType().Name); }
// ↓ 无条件
_combatDeathsReported++;
Info("R6-C 权威死亡：… 的 Mover 已 Freeze、历史已补 Alive=false（仍在线上，仍需 ACK 结果）");
```

```csharp
// 修正后（2032-2087）：两条必达不变量各自判定；任一条没做到 ⇒ Fail（fail closed），日志只在成功后打
string failure = null;
if (RecordProjectileUnavailableSample(uid, player, driver, false)) { _combatDeathHistorySamples++; }
else { failure = "历史 Alive=false 样本未写入"; }

try
{
    driver.Freeze();
    if (!driver.IsFrozen) { failure = CombineDeathFailure(failure, "Freeze 未生效"); }
}
catch (Exception ex) { failure = CombineDeathFailure(failure, "Freeze 抛异常 " + ex.GetType().Name); }

_combatDeathsReported++;
if (failure != null)
{
    Fail("权威死亡收尾失败（uid=" + uid + "）：" + failure + "；history 与 movement 已不同步，拒绝假成功");
    return;
}
Info("R6-C 权威死亡：… 的 Mover 已 Freeze、历史已补 Alive=false（仍在线上，仍需 ACK 结果）");
```

**为什么是这样（三条硬理由）**

1. **必须 host 自己判定，不能靠「抛异常让驱动发现」**：`PMR6CombatDriver.NotifyPlayerDied` 对订阅者是
   **逐订阅者隔离**（`catch { CallbackExceptions++; Warn(...) }`，PMR6CombatDriver.cs:1209-1213），
   异常逃出去只会被吞掉、权威状态不受影响；而那个 `Warn` 的出口 `PMR3Player.CombatWarn`
   在两个生产宿主里**都没有接线**（只有 `Tools/PMR6*` 接了）⇒ 线上等于无人知晓。
   所以「被吞掉」这件事只有 host 自己记账才看得见。
2. **只 Fault、不重放**：修正里没有任何「把结算再交一次给 core/R5」的动作 —— 重放才是
   「为一次回调/日志异常重复伤害」。失败 ⇒ 会话整体失败（`Fail` ⇒ `ExitRequested(1)`），
   不产生假结果。
3. **日志放在状态收尾之后**：日志自身抛异常不得被当成状态失败（更不得触发重放）；
   即任务要求的「不得为日志异常重复伤害」。修正后日志**只在两条不变量都达成时**才打印，
   不再有「谎报已 Freeze / 已补历史」。

**为什么现在才算真缺陷**（而不是纯防御性）：`Freeze()` 一旦没生效，死者仍留在
`_driversByUid` 且 `IsFrozen == false`，`PumpMovement`（1374）会继续对它 `Pump` ——
「死亡立即停止推进」这条契约不变量**唯一**的执行点就是宿主这一句；没有第二处兜底
（`Freeze` 本身不阻止 Authority 的 Pump，见 `PMR4MovementDriver.Pump` 不检查 `_frozen`）。
在本批任务明确要求「host 应 Fault 别假成功」的语境下，这是必须收紧的 fail-open。

**边界**：本修正**不**触碰 driver/core/声明层，**不**新增旁路，也**不**改变成功路径的行为
（两条不变量都达成时，日志与计数与修正前逐字相同）。改动面 = 1 个方法 + 1 个 5 行助手。

---

## 4. 报告但**未修** / 越界精确点名

### F2（P2，未修）断线收尾：Freeze 抛异常会**跳过** Dispose，且日志声称已释放

`PruneDisconnectedDrivers` 1424-1432（try 体）/ 1435（Remove）/ 1470（成功告警）：

```csharp
try { driver.Freeze(); driver.Dispose(); }          // ← Freeze 抛异常 ⇒ Dispose 被跳过
catch (Exception ex) { Warn("断线驱动收尾异常（uid=…）：" + ex.GetType().Name); }
_driversByUid.Remove(uid);                          // ← 仍会移除（1435）
…
Warn("玩家断线：运动驱动已 Freeze + Dispose、投射物接缝已摘（uid=…）");   // ← 与事实可能不符
```

- **为什么不是正确性缺口**：紧接着的 `_driversByUid.Remove(uid)`（1435）让该 driver 不再被 `PumpMovement` 推进
  ⇒ 不会出现任务 ⑬ 说的「history 与 movement 不同步」；且 `RecordProjectileDisconnectSample` 在 try **之前**
  已写好 Alive=false、`core.Disconnect` 仍会执行 ⇒ 结果链路不受影响。后果仅限
  「未 Dispose 的 driver 仍挂在 `player.MovementDriver` 上（托管泄漏，进程即将退出）+ 日志不准」。
- **可达性**：`Freeze/Dispose` 在主线程上只是置标志 + `List/HashSet.Clear()`（`EnsureThread` 之后无可抛点）
  ⇒ 实际不可达。
- **建议**（留给 owner，本轮按「优先窄修」不动它）：拆成两个 try（Dispose 必须尝试），
  并把最后那条 Warn 改成实际结果（`Freeze=? Dispose=?`）。

### F3（P2，未修）终局冻结：单个 driver 的 Freeze 异常只 Warn，之后的「不推进」依赖它成功

`OnCombatOutcomeFrozen` 2114-2123：

```csharp
try { driver.Freeze(); frozen++; }
catch (Exception ex) { Warn("终局冻结运动驱动异常（uid=…）：" + ex.GetType().Name); }
```

- 若某 driver 的 `Freeze()` 抛异常，它既没冻结也没从集合移除 ⇒ 次帧 `PumpMovement` 仍会 `Pump` 它
  ⇒ 「Ended 后不再推进」在那一副本上不成立（任务 ⑨ 的字面要求）。
- **为什么**不**在这里 Fault**：`OnCombatOutcomeFrozen` 由 `FlushState → UpdateOutcome` 在
  `FlushCombatOnce`（1295）内同步触发，而它**早于**同帧的 `SubmitCombatResultIfReady`（1305）；
  `Fail` 会置 `_faulted`，`Pump` 在 `PumpProjectiles` 之后与帧首都会立刻 `return` ⇒
  **本局结果永远提交不到 Lobby**（把一次成功对局变成 DS 失败）。所以「终局回调抛异常 ⇒ Fault」
  是错的修法。
- **建议**：把「终局后不推进」改为在**唯一执行点** `PumpMovement` 上按 `_combatOutcomeFrozen` 直接跳过
  `Pump`（不再依赖每个 driver 的 `IsFrozen` 都成功）。本轮未做：同样是主线程不可达分支，
  加第二处执行点属于为防御而扩展面，按「优先窄修」留报告。

### O1（越界，属 driver/宿主公共面）`PMR3Player.CombatWarn` 生产环境未接线

- `PMR6CombatDriver` 的全部告警（会话 fault 详情、`CallbackExceptions`、`PublishCombatState` 被拒等）
  都走 `Action<string> handler = PMR3Player.CombatWarn;`（PMR6CombatDriver.cs:2063）。
- grep 证据：赋值 `CombatWarn` 的只有 `Tools/PMR6DeclarationTest/Program.cs:92`、`Tools/PMR6NetworkTest/Program.cs:50`；
  `PMDsSessionHost.cs` / `PMClientSessionHost.cs` **都没有**（对照：`PMClientSessionHost` 接了
  `PMR3Player.ProjectileWarn` 并保存/还原了前值）。
  ⇒ 线上 R6 驱动的告警全部被静默丢弃（本报告 §3 正是被这一点放大的：被吞的回调异常连告警都看不到）。
- 归属：R6 driver 的告警出口接线属两个宿主（或 `PMR3Runtime` 默认出口），**不在**本批写入边界内；
  本轮**不**在 DS 宿主单独接一个静态钩子（会把「谁负责还原」这件事变成本批单方面的约定）。

### 不是缺陷、但**看起来像**的两处（避免后人误改）

1. **DS 侧 `PMR5ProjectileDriver` 从不读 `_history`**（grep：`_history` 只出现在构造赋值、
   `History` 属性与 `Describe`）—— 结论**不是**「宿主白填历史」。history 被注入进驱动内的
   `_coordinator`，由 `HandleServerHit → _coordinator.ReportHits` 使用，命中最终复核就是
   `PMProjectileCoordinator.IsTargetStillSettleable` 的 `if (!sample.Alive) return false;`
   ⇒ 宿主每帧填样本 + 死亡/断线补 Alive=false 是**载荷**（见 §2⑦）。
2. **`_r6_host_survey.md` §7 曾建议「死亡停运动用 `ServerSubmitTrustedEffect(SetMode(Inactive))`，
   而 `Freeze()` 是断线语义、不要当死亡用」**；本批**冻结契约**（`net-r6-combat-contract.md` 尾 C）
   明确写「死亡对应 `MovementDriver.Freeze`」。宿主按契约执行（2062 处 `driver.Freeze()`），
   并在 `PumpMovement`（1374）补上「Freeze 不阻止 Authority Pump、因此宿主跳过」的落实。
   ⇒ 这是**契约覆盖前置调查**的有意取舍，不是缺陷；不要按 survey 改回去（除非契约更新）。

---

## 5. 测试与门禁（本轮实测）

```bash
# ① 任务指定门禁（主侧已补 UnityEngine.IMGUIModule）
dotnet build Tools/PMR4UnityCheck -c Release -o Tools/PMR4UnityCheck/bin/r6-ds-review
#   → 已成功生成。0 个警告 / 0 个错误（整体绿；上一轮报告里「门禁整体红」的 26 条已随主侧修好）

# ② 行为回归（driver/core 未改，回归不变）
dotnet build Tools/PMR6NetworkTest -c Release     # → 0 警告 / 0 错误
dotnet Tools/PMR6NetworkTest/bin/Release/net8.0/PMR6NetworkTest.dll
#   → 通过 383 / 失败 0（含 R「宿主回调异常隔离（死亡/冻结不被吞）」、S「结算恰好消费一次」、U「Unbind 允许少一个等待 Ack」）

# ③ 结构 + 编码
python Tools/check_cs_braces.py Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs
#   → PASS（{} 498/498、( ) 1125/1125、深度全程非负末尾归零）
```

**注**：现有门禁里**没有**能跑 `PMDsSessionHost` 的宿主级测试
（grep `PMDsSessionHost` 只命中 `Tools/PMR4UnityCheck`、`Tools/PMUnityGlueCheck` 两个**编译面** csproj），
所以 ⑬ 的修正只有「编译面 + 逐行证据」证明，没有行为断言覆盖 —— 见 §6 U2。

---

## 6. 未验证 / 诚实边界

| # | 项 | 说明 |
|---|---|---|
| U1 | **真实运行** | 未启动 Unity、未跑真 UDP 端到端（按任务要求）。所有结论来自静态逐行复核 + 官方编译门禁 + 既有 driver 行为门禁。「按 F/G 真能打出伤害并回收胜负」在真实进程里**仍未证**。 |
| U2 | ⑬ 修正的行为覆盖 | 无宿主级测试可注入「history 写失败 / Freeze 抛异常」；本修正由**代码路径穷举**证明（失败 ⇒ Fail、成功 ⇒ 行为与修正前逐字相同），非行为断言。 |
| U3 | F2/F3 的不可达性 | 依据「`Freeze/Dispose` 在 `EnsureThread` 之后只有标志位与集合清空」的读码判断，**非**实测；若将来在 `Freeze` 里加入可抛逻辑，这两处会立刻变成可达缺口。 |
| U4 | DS 起点 vs 客户端预测起点（0.6m 枪口差） | 沿用上一轮 U4：DS 授权位置 = 运动同步位置，不含客户端 `predicted + aim*0.6`；10m 枪口容差覆盖，但对**命中通过率**的影响仍未在真机量过。 |
| U5 | 终局后 `driver.Update` 仍在跑 | 终局只保证「不再 `Pump`（不推进仿真）」；`Update` 仍用于排空入站。契约只要求「冻结全部 Mover」，本批按字面执行。 |
| U6 | 客户端侧 / Lobby 侧 | Client host（F/G、HUD、结果 ACK）与 Lobby gateway 的结果消费属别的写入面，本轮**未验**；T6B 的完整字节链在含宿主的真实进程上仍未验。 |
| U7 | 30s 期限起算点 | 用「场景就绪后的第一帧」近似 `SceneReady`（`Initialize` 已置 `_sceneReady`，`Pump` 紧随），误差 ≤1 帧。 |
| U8 | 单局/单会话假设 | 一个 DS 进程只跑一局（`PMDsHost` 唯一驱动点）；多局同进程、断线重连（同 uid 重连会被 `_playersByUid.ContainsKey` 早退忽略）不在本批语义内。 |

---

## 7. 已检查范围

**按委派顺序完整读取的文档**：`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`（全文）
→ `Docs/plans/net-r6-combat-contract.md`（全文，尤其尾段「C 宿主接线冻结」）
→ `Docs/plans/_r6_ds_host_report.md`（全文）→ `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`（全文）。
**为核对判据另读**：`Docs/plans/_r6_host_survey.md`（§7/§8 及标题）、`_r6_network_review.md`（标题与结论格式）、
`_r5_ds_host_review.md`（编码/结构惯例）。

**只读对读的真实实现**：`PMDsSessionHost.cs`（全文 3559 行，改后 3591）、`PMR6CombatDriver.cs`（全文：
`Pump`/`FlushState`/`TickModel`/`PublishStateIfDirty`/`UpdateOutcome`/`ApplySettlementNow`/`NotifyPlayerDied`/
`CreateAuthorityPolicy`/`AuthorityPolicy`/`UnbindPlayer`/`Dispose`）、`PMCombatSession.cs`（全文关键面：
`AddPlayer`/`StartMatch`/`Disconnect`/`Tick`/`RequestAttack`/`TryAuthorizeProjectile`/`ApplySettlement`/
`EndMatch`/`EvaluateForfeit`/`Snapshot`）、`PMProjectileHistory.cs`（全文，尤其 `Record` 的同帧分支与 `TryResolve`）、
`PMProjectileCoordinator.IsTargetStillSettleable`、`PMR5ProjectileDriver.cs`（`Pump`/`HandleServerSpawn`/
`HandleServerHit`/`ResolveActivation`/`CancelPredictedActivation`/`DrainSettlements`/`DrainViewChanges`/`Dispose`）、
`PMR4MovementDriver.cs`（构造默认 `streamVersion=1u`、`Freeze`/`IsFrozen`/`OutputBoundary`/
`AuthorityTotalSimTimeMs`/`Pump`）、`PMR3Player.cs`（`Uid`/`OwnerConnection`/`CombatDriver`/`PublishCombatState`/
`CombatWarn`/`ProjectileWarn`）、`PMR3Runtime.SpawnPlayer`、`PMDsLobbyAgent.cs`（`SubmitResult`/`HasResult`/
`ReadySent`/`ResultAcknowledged`/结果重发与超时）、`PMNetIdentity.cs`（`PMFrameId`/`PMFrameDomain`）、
`PMProjectileDiagnosticConfig.cs`、`Tools/PMR4UnityCheck/PMR4UnityCheck.csproj`、
`Tools/PMR6NetworkTest/Program.cs`（段 R/S/T/U/V）。

**本次写入的文件**：`Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs`（§3 唯一改动）
与本报告。**未**修改任何其它源码/文档/资产/工程文件；未提交。

---

## 8. 复现命令

```bash
# 官方门禁（任务指定输出目录）
dotnet build Tools/PMR4UnityCheck -c Release -o Tools/PMR4UnityCheck/bin/r6-ds-review

# 行为回归
dotnet build Tools/PMR6NetworkTest -c Release
dotnet Tools/PMR6NetworkTest/bin/Release/net8.0/PMR6NetworkTest.dll   # 通过 383 / 失败 0

# 结构 & 编码
python Tools/check_cs_braces.py Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs

# 指纹（BOM + CRLF + MD5）
python -c "import hashlib;d=open(r'Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs','rb').read();print(d[:3]==b'\xef\xbb\xbf', d.count(b'\r\n'), d.count(b'\n')-d.count(b'\r\n'), hashlib.md5(d).hexdigest())"
```

---

## 9. 本次未做（明确边界）

- 未改 driver/core/声明/生成物/Lobby/主计划/共享契约；未新增宿主旁路去绕开 driver 根因（越界项在 §4 点名）。
- 未运行 Unity、未跑真 UDP 端到端、未构建 `HyldDS.exe`、未做实机验收（T46/T47 继续 PENDING_USER）。
- 未修 F2/F3/O1（理由各自在 §4 写明：F2 无 desync 缺口、F3 在此处 Fault 会吞掉结果提交、O1 越界）。
- 未新增宿主级测试（现有门禁无此能力；若需要，应另立「DS 宿主可测接缝」批，本批不擅自扩边界）。
