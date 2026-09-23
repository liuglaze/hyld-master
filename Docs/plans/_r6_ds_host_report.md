# R6-C DS 权威战斗宿主接线（PMDsSessionHost）

> 范围：`Docs/plans/net-r6-combat-contract.md` 尾段「C 宿主接线冻结」。
> 本批**只**改 `Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs`；`PMR6CombatDriver.cs` / `PMCombatSession.cs` /
> `PMR5ProjectileDriver.cs` / `PMR3Runtime.cs`（driver / core / 声明层）**只读**，均未改动；
> `PMClientSessionHost.cs`（Client host）由**另一组并行修改**，本批未触碰。
> 未启动 Unity；未执行任何 SVN/git 写操作；未提交；未递归委派；未改主计划 / Shared 契约 / 生成产物 / Lobby。

---

## 0. 结论摘要

| 项 | 结果 |
|---|---|
| 语言面（真实 Unity 2019.4 DLL + C# 7.3 + netstandard2.0，**完整依赖闭包**：PMNet/PMMover/PMPrediction/PMProjectile/PMCombat/Shared/PMR3/PMUnity + 三个 Boot 文件） | **0 错误 / 0 警告，且 0 条诊断涉及 PMDsSessionHost.cs**（exit 0） |
| 官方门禁 `Tools/PMR4UnityCheck`（同一 csproj、同一输出目录） | **26 错误，全部落在另一组正在改的 `PMClientSessionHost.cs`（24 条）与 `PMUnityCombatHud.cs`（2 条）**；`PMDsSessionHost.cs` **0 错误** |
| 行为回归 `Tools/PMR6NetworkTest`（真实字节链，383 断言） | **通过 383 / 失败 0**（driver/core 未改，回归不变） |
| 括号 / 编码自检 | `check_cs_braces.py` PASS（494/494、1120/1120、深度全程非负）；BOM=True + 3559 行全 CRLF、0 个 lone LF |
| 真实运行 | **未验证**（见 §6；未启动 Unity、未跑真 UDP 端到端） |

---

## 1. 写入边界与文件指纹

| 文件 | 变更 | 行数 | MD5 | 编码 |
|---|---|---|---|---|
| `Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs` | 修改（R6-C 接线） | 3559（CRLF 3559） | `ad5446c33b531beff5e8b974ea5f47c5` | UTF-8 **BOM** + CRLF（无 lone LF） |
| `Docs/plans/_r6_ds_host_report.md` | 新增（本报告） | 292 | 自指不入表（写完后 `md5sum` 即可） | UTF-8 **BOM** + CRLF（无 lone LF） |

> 修订边界核对（近 90 分钟 mtime）：本批窗口内由我写入的仅上述两个文件；
> `PMClientSessionHost.cs` / `PMUnityCombatHud.cs` 的 mtime 晚于我的最后一次写入，属另一组并行修改。

---

## 2. 契约条款 → 实现落点对照

| 契约条款（尾 C） | 落点 | 行号 |
|---|---|---|
| **只非 `options.ServerSmoke` 正式玩法启用 core** | `_combatEnabled = !options.ServerSmoke;` | 793 |
| smoke 保留 R3 probe 自动结果 + R5 diagnostic | `CheckSmoke` 早退条件未变；诊断结算订阅只在 smoke 分支 | 2307 / 2649 |
| **model → CreateAuthorityPolicy(model, TryGetCombatOwnerPosition, () => 本帧 Pump now) → R5 → R6** | `CreateProjectileWiring`：`new PMCombatSession(_boot.Epoch)` → `PMR6CombatDriver.CreateAuthorityPolicy(...)` → `new PMR5ProjectileDriver(...)` → `new PMR6CombatDriver(...)` | 2586 / 2599 / 2633 |
| **非 smoke 不再订阅 diagnostic `OnProjectileSettlement`、不双 Drain** | `_projectileDriver.Settlement += OnProjectileSettlement;` **只在 smoke 分支**；`DrainDiagnosticSettlementBuffer()` 被 `if (!_combatEnabled)` 包住 | 2649 / 2811 |
| **R6 driver 唯一消费者** | R6 driver 在自己构造里订阅；宿主在正式玩法不订阅 | 2633 / 2642 |
| OnConnected 用 **roster Uid/HeroId/原始 TeamId**，走 `CombatDriver.AddPlayer` | `AttachCombatPlayer`：`_rosterTeamByUid`（**原始 TeamId**）+ `_rosterHeroByUid` + `AddPlayer(player, uid, teamId, heroId)` | 1848 / 1860 |
| **不能把 formalTeamIndex0/1 当 team 传** | team 只取 `_rosterTeamByUid[uid]`；`_formalTeamIndexByUid`（TeamId 2→0 / 1→1 的出生几何索引）在该路径**完全未被引用** | 1860 |
| **初始 combatstate 在首个 flush 前发布** | `AttachCombatPlayer` 末尾 `_combatDriver.FlushState(_projectileWallNowMs)`（与运动 `PublishInitialSnapshot` 同一时序：均在 `_endpoint.Pump` 的握手接纳段内、桥 flush 之前） | 1891 |
| **all expected connected && ready/已添加 才 StartMatch，一次** | `TryStartCombatMatch`：人数齐 + 每名成员 `OwnerConnection.IsReady` + `IsPlayerBound` + core 有名册 + 运动 driver 就绪；`_combatStarted` 只置一次 | 1908 |
| **SceneReady 起 30s 无人/缺玩家 Fail（smoke 不走）** | `CombatStartDeadlineMs = 30000` + `CheckCombatStartDeadline`（首帧起算，超时 `Fail`，**不伪造胜负**） | 486 / 1946 |
| **Pump：endpoint → movement → R6.Pump(now)（先 Tick 再授权）→ R5 所有子步 → R6.FlushState(now)** | `Pump`：`_endpoint.Pump` → `PumpMovement` → `PumpCombatOnce` → `PumpProjectiles` → `FlushCombatOnce` → 开战门 / 期限 / 提交 | 1252-1305 |
| **每个 core 入口同一 clock 值** | `_projectileWallNowMs = (double)nowMs;`（在 `_endpoint.Pump` **之前**）；R6.Pump / R5.Pump / R6.FlushState / OnConnected 初始发布 / 策略时钟全部取它 | 1264 |
| **policy position 从真实 movement sync 且非 Frozen/Dead** | `TryGetCombatOwnerPosition`：core `Connected && !Dead` + 运动 driver 未释放 / 未 Freeze + `GetAuthoritativeSync().Position` 有限 | 2146 |
| 策略时钟不读 Unity Time | `GetCombatPumpWallNowMs()` 返回本帧 Pump 墙钟；作为 `Func<double>` 注入 | 2180 / 2599 |
| **History.Alive = core Connected && !Dead，未知 false** | `RecordProjectileHistory`：`sample.Alive = combat != null && combat.Connected && !combat.Dead;`（smoke 仍恒 true） | 2925 |
| **HitFilter 查真实 model 队伍与 alive** | `AcceptCombatHit`（新 `CombatModelHitFilter`）：core 名册解析 owner/victim → self 拒 → owner/victim `Connected && !Dead` → `TeamId` 同队拒；身份不可解析 fail closed | 2194 / 2238 |
| **PlayerDied → Freeze 对应 Mover + 立即补 Alive=false 历史（真 serverframe/inputBoundary）** | `OnCombatPlayerDied`：`RecordProjectileUnavailableSample(..., false)`（用 `_movementServerFrame` + `driver.OutputBoundary`）→ `driver.Freeze()` | 2032 / 3080 |
| **OutcomeFrozen 冻结全部 Mover，之后 R5 仅 step0 排入站** | `OnCombatOutcomeFrozen` 全量 `Freeze()`；`PumpProjectiles` 顶部 `if (_combatOutcomeFrozen)` 只 `PumpProjectileOnce(0)` | 2067 / 2775 |
| **断线 `CombatDriver.UnbindPlayer`（同时 R5 unbind）→ core.Disconnect 结果可出** | `PruneDisconnectedDrivers` 正式玩法分支调 `_combatDriver.UnbindPlayer(player)`（内含 R5 unbind + `core.Disconnect`） | 1447 |
| **真实 smoke 掉线 Fail 保留** | `CheckPlayerDrops` 的 `ServerSmoke` 掉线 Fail 分支未改 | 2281-2305 |
| **死亡 ≠ 掉线：仍允许 ACK 结果** | 死亡路径只 `Freeze`，**不** unbind、不 `Disconnect`；结果等待只按 core `Connected` 统计 | 2032 / driver 内 |
| **ResultReadyForLobby 后单次 `Lobby.SubmitResult(WinnerTeamId, ResultSummary)`** | `SubmitCombatResultIfReady`：`_combatSubmitted` 单次 + `_lobby.HasResult` 幂等短路；summary 取驱动深拷贝（冻结不改） | 2104 |
| **既有 ResultAck/Exited 链不变** | 结果 ACK 由 driver 处理；`_lobby.Pump` 仍在其后同帧被调用一次（结果可同帧上线） | 1303-1314 |
| **不重复提交 / 不再 smoke 提交** | `_combatSubmitted` 单次；`_combatEnabled == !ServerSmoke` ⇒ 两路互斥（`CheckSmoke` 对正式玩法早退） | 2104 / 2307 |
| **Dispose：combat 取消订阅 → R5 → motion → bridge/world/map** | `ReleaseProjectileWiring` 次序：`_combatDriver`（先解事件订阅再 `Dispose`）→ `_combatModel = null` → R5 `Dispose` → `Motion.Dispose`；`Dispose()` 内它仍在 `_bridge.Dispose` / 场景对象 / 地图之前 | 2698 / 2418 |
| **失败初始化也完整清理** | `CreateProjectileWiring` 的每条失败路径都 `ReleaseProjectileWiring()`（幂等） | 2583-2665 |
| **对 fault core/driver 不吞** | `FaultHostIfCombatDriverFaulted` 在 Pump/Flush/OnConnected 入口各查一次并 `Fail` 宿主 | 2012 |
| **公开 CombatDriver / CombatStarted / CombatResultSubmitted 读视图** | `CombatDriver`/`CombatEnabled`/`CombatStarted`/`CombatResultSubmitted`/`CombatOutcomeFrozen` + `DiagnosticCountersMeaningfulOnlyInSmoke` | 742-757 |
| **诊断统计只在 smoke 有效** | 诊断计数只在 smoke 的订阅 / `DrainDiagnosticSettlementBuffer` 里累加（正式玩法恒 0） | 2649 / 2811 / 2960 |

---

## 3. 精确位置表（创建 / Start / Pump / 死亡 / ACK / 退出）

| 语义 | 位置 | 行号 |
|---|---|---|
| R6-C 字段块（`_combatEnabled` / `_combatModel` / `_combatDriver` / `_combatStarted` / `_combatOutcomeFrozen` / `_combatSubmitted` / 期限 / 观测计数） | `PMDsSessionHost` 字段区 | 640-700 |
| `_rosterHeroByUid` 字段 | 同上 | 553-557 |
| 名册 hero 登记（可信来源） | `Initialize` 名册环 | 957 |
| 模式判定（core 只在正式玩法启用） | `Initialize` | 793 |
| **创建链路**：core → policy → R5 → R6 driver | `CreateProjectileWiring` | 2586 / 2599 / 2633 |
| 结算订阅二选一（smoke 诊断 / 正式 R6） | `CreateProjectileWiring` | 2649 / 2633 |
| R6 事件订阅（PlayerDied / OutcomeFrozen） | 同上 | 2642-2643 |
| **玩家接入**：core.AddPlayer + 接缝绑定 | `AttachCombatPlayer` | 1848 |
| 初始 combatstate 首次发布 | `AttachCombatPlayer` | 1891 |
| **开战门 StartMatch（一次）** | `TryStartCombatMatch` | 1908 / 1930 |
| 开局 30s 期限 | `CheckCombatStartDeadline` | 1946 |
| **每帧次序** | `Pump` | 1252-1305 |
| R6.Pump | `PumpCombatOnce` | 1966 |
| R5 全部子步（含终局 step0 短路） | `PumpProjectiles` | 2759 / 2775 |
| R6.FlushState（最新 HP / 资源 / 胜负） | `FlushCombatOnce` | 1988 |
| 历史 Alive（core Connected && !Dead） | `RecordProjectileHistory` | 2925 |
| 授权策略真实位置 | `TryGetCombatOwnerPosition` | 2146 |
| 授权策略时钟 | `GetCombatPumpWallNowMs` | 2180 |
| 权威命中过滤 | `AcceptCombatHit` / `CombatModelHitFilter` | 2194 / 2238 |
| **死亡**：Freeze Mover + 立即补 Alive=false | `OnCombatPlayerDied` | 2032 |
| 死亡历史样本写入（与断线共用） | `RecordProjectileUnavailableSample` | 3080 |
| 断线历史样本（wrapper） | `RecordProjectileDisconnectSample` | 3060 |
| **终局**：冻结全部 Mover | `OnCombatOutcomeFrozen` | 2067 |
| 冻结 Mover 真正生效（冻结者不再被 Pump） | `PumpMovement` | 1374 |
| **断线**：CombatDriver.UnbindPlayer | `PruneDisconnectedDrivers` | 1447 |
| **ACK**：结果确认由 driver 处理；单次提交 Lobby | `SubmitCombatResultIfReady` | 2104 |
| **退出 / 释放**：combat → R5 → motion | `ReleaseProjectileWiring` | 2698 |
| 宿主总释放（combat 接线 → bridge → 场景 → 地图） | `Dispose` | 2418 |
| 诊断读视图 | `Describe` / `LogHeartbeat` | 3539 / 3473 |

---

## 4. 关键时序与实现理由（为什么这样而不是别的）

### 4.1 创建次序（`CreateProjectileWiring`，行 2542）

```
（正式玩法）
  new PMCombatSession(_boot.Epoch)                                  ① core 必须先有
  PMR6CombatDriver.CreateAuthorityPolicy(core, 真实位置, 本帧墙钟)     ② policy 是 R5 的构造参数
  new PMR5ProjectileDriver(..., policy, motion, hitFilter)            ③
  new PMR6CombatDriver(..., r5, core) + PlayerDied/OutcomeFrozen      ④ R6 自己订阅 Settlement
（smoke 保持原状）
  DiagnosticProjectileAuthorityPolicy + RosterIdentityHitFilter → R5 → Settlement += 宿主诊断
```

policy 要引用 core，而 policy 又是 R5 的构造参数；R6 driver 依赖 R5 实例，必须最后。
任一环失败都走 `ReleaseProjectileWiring()`（幂等）并把 `error` 交给 `Initialize` → 启动失败
（**不回退诊断路径**：诊断探针不可用就不该谎报就绪）。

### 4.2 每帧次序（`Pump`，行 1252）

```
nowMs（唯一墙钟）→ _projectileWallNowMs = nowMs
Physics.SyncTransforms()
_endpoint.Pump(nowMs)            // 本帧接入的玩家在同一次 Pump 里 OnConnected（写初值）
PumpMovement(nowMs)              // 权威运动（冻结者只 Update 不 Pump）
PumpCombatOnce(now)              // R6：先 Tick(回蓝) 再处理上行攻击/裁决/结果 → 授权账
PumpProjectiles(nowMs)           // 记历史 → R5 全部子步（结算在回调里直投 R6.ApplySettlement）
FlushCombatOnce(now)             // R6：复制最新 HP/资源/胜负（含本帧回蓝）
TryStartCombatMatch / CheckCombatStartDeadline / SubmitCombatResultIfReady
CheckPlayerDrops / CheckSmoke / _lobby.Pump
```

两个「次序不是风格而是正确性」的点（与 `_r6_network_review.md` §1.1 / §1.7 一致）：

1. **R6.Pump 必须早于 R5.Pump**：core 的回蓝只在 `Tick` 里发生，而攻击合法性按请求时刻的权威资源判定；
   反序会让「这一段回蓝刚好到达」的合法攻击被判 `InsufficientMana`，且该拒绝会作为**终态**写进 core 的
   攻击账本（同一 activationId 的幂等回显永远是拒绝）= 一次 16ms 的次序错误永久吃掉一次攻击。
2. **R6.FlushState 必须晚于 R5.Pump**：结算发生在 R5 订阅回调内部，晚发布才能让本帧伤害与回蓝
   在同一个 `FlushState` 里一起复制出去（否则客户端会看到「扣了资源但 HP 下一帧才动」）。

### 4.3 统一墙钟（行 1264 / 2180）

同一个 `nowMs` 派生出的 double 被喂给：`R6.Pump`、`R5.Pump`、`R6.FlushState`、`OnConnected` 的初始发布，
以及授权策略的回调（`GetCombatPumpWallNowMs`）。策略**不读** `UnityEngine.Time`（R6 驱动引擎无关的硬约束：
位置与时钟都由宿主注入）。`_projectileWallNowMs` 在 `_endpoint.Pump` **之前**赋值，因此本帧接入的玩家在
握手回调里就能用同一个时钟发布初值。

### 4.4 死亡（行 2032）——「死亡不是掉线」

```
R5.Pump → Settlement 回调 → R6.ApplySettlementNow → core 置 Dead → R6.PlayerDied
  → 宿主：RecordProjectileUnavailableSample(Alive=false, 真 AuthorityServer 帧 + 真 OutputBoundary)
  → 宿主：MovementDriver.Freeze()
```

1. 历史没有「按目标移除」的 API，不补一条同帧 `Alive=false` 样本，死亡者会被**旧帧锚**继续当可命中目标；
2. `Freeze()` 本身**不阻止 Authority 的 Pump**（`PMR4MovementDriver.Pump` 不检查 `_frozen`，只有 AP 时间轴 /
   表现层检查），因此宿主在 `PumpMovement` 里显式跳过 `IsFrozen` 的副本（行 1374）——**宿主是「冻结 Mover」
   这句话的唯一执行点**；仍调 `Update` 以排空入站（否则冻结副本的待处理载荷会无界堆积）；
3. 死亡路径**不** unbind、不 `core.Disconnect`：死者仍在名册与连接上，终局结果仍要等它的 ResultAck
   才闭环（判据混用会让战斗少一个 ACK 等待者而提前就绪）。

### 4.5 终局（行 2067）

`OutcomeFrozen` 一次性冻结**全部**运动驱动；`PumpProjectiles` 顶部随即转入「只 `Pump(0)`」分支
（清空累加器、排空声明入站、排空视图脏量、直接 return）——契约原文「R5 仅 step0 排入站」。
理由：终局后新命中不可能产生结算（core 已 `MatchEnded`），继续推进只会让冻结前的弹继续走、
继续消耗 CPU 与视图脏量，还会在下行制造无意义的裁决包。

### 4.6 断线（行 1447）与结果闭环（行 2104）

断线走 `_combatDriver.UnbindPlayer(player)`：它内含 R5 unbind（取消未消费 NetId 预留、丢入站队列、
退休本地账、销毁悬空权威对象）**以及** `core.Disconnect`（保留水位 / 死亡真值，仅剩唯一在线队伍时判
Forfeit 获胜）⇒ 结果可出。smoke 保持 `_projectileDriver.UnbindPlayer` 与「掉线 Fail」原语义。

`ResultReadyForLobby` 后单次 `Lobby.SubmitResult(WinnerTeamId, ResultSummary)`；winner 是**名册原始 TeamId**，
summary 是驱动返回的深拷贝（冻结、不因断线 / 重复结算改变）。`_lobby.HasResult` 时直接置 `_combatSubmitted`
（幂等，不重复提交）；提交发生在 `_lobby.Pump` 之前，因此结果同帧上线，既有 ResultAck → Exited 链不变。

### 4.7 释放（行 2698 / 2418）

```
_combatDriver.PlayerDied -= / OutcomeFrozen -=  →  _combatDriver.Dispose()   // 摘掉它那份 R5 订阅 + 本地账
_combatModel = null                                                          // core 非 IDisposable
_projectileDriver.Settlement -= OnProjectileSettlement  →  R5.Dispose()
_projectileMotion.Dispose()
...（之后才是）PMR3Runtime.Detach / bridge.Dispose / 场景对象 / battleMap（含隔离物理场景）
```

反序会让战斗驱动继续持有已释放的 R5；`Settlement -=` 在正式玩法是空操作（从未订阅），保留它是为了
smoke 路径与「幂等释放」的单一出口。

---

## 5. 编译与回归证据

```bash
# ① 官方门禁（任务指定输出目录；另一组不构建该输出）
dotnet build Tools/PMR4UnityCheck -c Release -o Tools/PMR4UnityCheck/bin/r6-ds-host
#   → 26 个错误，全部在 PMClientSessionHost.cs(24) / PMUnityCombatHud.cs(2)，PMDsSessionHost.cs 0 错误

# ② 隔离编译（同一 csproj 的 Include 面 + 同一 Unity DLL 集 + 同一 LangVersion/DefineConstants，
#    仅排除另一组正在改的 PMClientSessionHost.cs 与 PMUnityCombatHud.cs；不写新工程文件）
#    → sources 79 / exit 0 / errors 0 / 提及 PMDsSessionHost.cs 的诊断 0

# ③ 行为回归（driver/core 未改，确认无回归）
dotnet build Tools/PMR6NetworkTest -c Release
dotnet Tools/PMR6NetworkTest/bin/Release/net8.0/PMR6NetworkTest.dll     # 通过 383 / 失败 0

# ④ 结构 + 编码
python Tools/check_cs_braces.py Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs   # PASS
# 自检：BOM=True，3559 行全 CRLF，lone LF = 0
```

隔离编译为何可信：Roslyn 在**一次编译**里对全部源文件做绑定并报告**所有**错误，不是遇到第一个文件就停；
因此「26 条错误无一条指向 PMDsSessionHost.cs」本身就说明该文件全部成员绑定成功。
我又用同一引用集把**完整依赖闭包**（含 `PMR6CombatDriver.cs`、`PMR5ProjectileDriver.cs`、`PMCombatSession.cs`、
`PMCombatWeaponPlanner.cs`、`PMProjectileHistory.cs`、`PMUnityBattleMap.cs`、`PMR4MovementDriver.cs`）
单独编成类库，exit 0 —— 因此「API 编译」这一项是**已验**的。

---

## 6. 未验证 / 诚实边界

| # | 项 | 说明 |
|---|---|---|
| U1 | **真实运行** | **未启动 Unity、未跑真 UDP 端到端**。所有结论都来自静态审查 + 编译 + 既有行为门禁（R6 driver 的 383 断言不覆盖宿主接线本身）。「按 F 能打出伤害并结算胜负」在真实进程里**未经证实**。 |
| U2 | 官方门禁整体绿 | `Tools/PMR4UnityCheck` 当前**整体失败**，原因是另一组正在改 `PMClientSessionHost.cs` / `PMUnityCombatHud.cs`（写边界之外，本批不允许修改）。等其收口后应重跑同一条命令，预期 0 错误。 |
| U3 | Client host 与端到端链 | 客户端侧（F 普攻 / G 大招 / HUD / `ClientCombatMatchResult` → ACK）由另一组实现，本批**未验**。因此 T6B 的「真实字节链 R6Attack → R5Spawn → 命中 → HP+资源复制 → 结果+ACK」在**含宿主的真实进程**上仍未验。 |
| U4 | 枪口 0.6m | DS 授权位置 = 运动同步位置（不含客户端 `predicted pos + aim*0.6` 的枪口偏移）。R5 枪口容差是 10m，因此不会因该差异被拒；但「DS 权威弹起点 vs 客户端预测弹起点」差约 0.6m 对**命中验证通过率**的影响未在真实运行中验证（结算靠客户端上报 + DS 历史验证，DS 起点只影响权威弹自身轨迹与 10m 门）。 |
| U5 | 终局后 movement 仍有 `Update` | 终局只保证「不再 `Pump`（不推进仿真）」；`driver.Update(elapsed)` 仍在跑（排空入站）。契约只要求「冻结全部 Mover」，本批按字面执行；若后续要求「终局后连 Update 都停」需另立条款。 |
| U6 | 终局后 R5 仍 `Pump(0)` | 这是契约要求（排空声明入站）；意味着终局后到达的 spawn/hit 声明仍会被 R5 消费一次（被 core 的 `MatchEnded` / R5 自身终局判据拒绝）。本批未额外做「终局后不再投递」。 |
| U7 | 死亡者的在飞弹 / 镜像 | 本批不处理（超边界）：死者的假弹 / 镜像按 R5 自身的 TTL / 墓碑收口。 |
| U8 | `_combatModel` 释放 | `PMCombatSession` 不是 `IDisposable`（纯数据 + 本地账），只放引用；下一局是新对象（epoch 不复用）。 |
| U9 | 30s 期限的起算点 | 用「场景就绪后的**第一帧**」近似 `SceneReady`（`Initialize` 已把 `_sceneReady` 置位，`Pump` 紧随其后，误差 ≤1 帧）。 |
| U10 | `ServerSmoke` 与正式内容模式正交 | 判定用 `!ServerSmoke`（契约原文「DS 非 ServerSmoke」），因此「正式内容 + smoke」仍走诊断投射物；这是有意的（smoke 不是玩法验收）。 |

---

## 7. 边界外缺口（需要别人补，具体指出）

| # | 缺口 | 归属 |
|---|---|---|
| G1 | `PMR4UnityCheck` 官方门禁整体红：`PMClientSessionHost.cs` 缺 `TryCombatAttack` / `FlushCombatState` / `PumpCombatInbound` / `UpdateCombatReplicationState` / `UpdateCombatHud` / `SampleCombatInputEdges` / `ReleaseRetainedHud` / `ClearLastCombatResult` 定义；`Session` 缺 `FireEdgeBuffered` / `LastFireWallMs` / `NextActivationId` / `DiagnosticFiresSent` / `DiagnosticFiresRejected` / `LastFireError` / `DiagnosticFireGateBlocked` / `ProjectileFiresRejectedByWrap`；`DiagnosticPredictionMs` 未定义。另 `PMUnityCombatHud.cs:327/328` 用了不存在的 `GUI`。 | **另一组（Client host / HUD）** |
| G2 | 客户端 F/G 攻击、结果 ACK、终局 HUD；`PMUnityCombatHud` 的 `GUI` 引用（需 `UnityEngine.IMGUIModule` 引用或改用其它绘制）。 | 另一组 |
| G3 | Lobby 侧对 `ResultSummary`（`PMNetWriter` 原语，字段 1..11）的消费 / 对账仍只 `log`。 | Lobby |
| G4 | 实机前需重建 `HyldDS.exe`（`PMR3UnitySmoke` S35 的协议摘要过期问题见 A2 / R6-B 报告）。 | 集成时 |
| G5 | `Tools/PMR4NetworkTest` 的 2 条 R6-A2 遗留过时计数断言（3 → 12）。 | 主侧整合 |

---

## 8. 已检查范围

**完整读取的文档（按委派顺序）**：`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`（全文）
→ `Server/AGENTS.md`（全文）→ `Docs/plans/net-r6-combat-contract.md`（全文，尤其尾「C 宿主接线冻结」）
→ `Docs/plans/_r6_network_report.md`（全文，精确 host API）→ `Docs/plans/_r6_network_review.md`（全文，修订）
→ `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`（全文）。

**为落点而对读的真实实现（只读）**：`PMDsSessionHost.cs`（全文 2814 → 3560 行）、`PMR6CombatDriver.cs`（全文）、
`PMR5ProjectileDriver.cs`（公开面 + `Pump` / `BindPlayer` / `UnbindPlayer` / `CancelPredictedActivation` /
`Drain*` / policy 调用点）、`PMCombatSession.cs`（`AddPlayer` / `StartMatch` / `Disconnect` / `Tick` / `GetPlayer` /
`CapturePlayers` / `TryAuthorizeProjectile` / `ApplySettlement`）、`PMCombatContracts.cs`（`PMCombatPlayerSnapshot`）、
`PMCombatWeaponPlanner.cs`（`TryBuild` / `FireIntervalMs`）、`PMProjectileContracts.cs`（`PMProjectileTargetSample` /
`IPMProjectileHitFilter` / 限额）、`PMProjectileHistory.Record`、`PMR4MovementDriver.cs`（`Freeze` / `IsFrozen` /
`Pump` / `Update` / `GetAuthoritativeSync` / `StreamVersion` / `OutputBoundary` / `AuthorityTotalSimTimeMs`）、
`PMDsControlProtocol.cs`（`PMDsRosterIdentity`）、`PMDsLobbyAgent.cs`（`SubmitResult` / `HasResult` / `ReadySent`）、
`PMDsHost.cs`（唯一驱动点）、`PMClientSessionHost.cs`（**只读**：确认其尚未接 R6，且正被另一组修改）、
`Tools/PMR4UnityCheck/PMR4UnityCheck.csproj`（Include / 引用面）。

**本次写入的文件**：§1 表中两行。**未**修改任何其它源码 / 文档 / 资产 / 工程文件。

---

## 9. 复现命令

```bash
# 官方门禁（任务指定输出目录）
dotnet build Tools/PMR4UnityCheck -c Release -o Tools/PMR4UnityCheck/bin/r6-ds-host

# 结构 & 编码
python Tools/check_cs_braces.py Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs

# 行为回归（driver/core 未改）
dotnet build Tools/PMR6NetworkTest -c Release
dotnet Tools/PMR6NetworkTest/bin/Release/net8.0/PMR6NetworkTest.dll
```
