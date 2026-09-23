# R5-C DS 真实宿主接线报告（PMDsSessionHost 诊断投射物）

> 任务类型：**comprehensive**（实现 R5-C 的 **DS 真实宿主机接线**，只改 host 文件）。
> 写入边界（严格遵守）：只写 `Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs` 与**本报告**；
> **未**改 driver / core / 适配器 / 合同 / 主计划 / 生成物 / 锁文件 / csproj / 客户端宿主。
> **未启动 Unity、未关闭 Unity、未编 UE、未提交（git/svn 均无写操作）、未递归委派。**
> 状态唯一源仍是 `Docs/plans/net-architecture-migration.md`；本契约切片见
> `Docs/plans/net-r5-network-contract.md` 末段「C宿主首个可执行入口」。
> World 单位：**Y-up 米**，Yaw 度；墙钟 = 宿主既有的 `Time.realtimeSinceStartup` 毫秒。

---

## 0. 结论摘要

| # | 项 | 结果 |
|---|---|---|
| 1 | DS 每会话独立 History/Motion/Driver | ✅ 在 `_sceneReady = true` **之前**、`OpenServer` **之前**建立（L1018） |
| 2 | 每认证 player Bind / 断线 Unbind | ✅ L1592 / L1294（UnbindPlayer 走 driver 的完整 owner 收拾） |
| 3 | 固定 16ms 累加器、单帧最多 8 子步、零步仍 Pump(0) | ✅ L2055；`stepMs=16`，`maxSteps=8`，L2093 零子步排空 |
| 4 | 历史采样在 PumpMovement 之后、投射物推进之前 | ✅ L1167，七元组全部取真实来源（L2133） |
| 5 | 可信 policy（已认证绑定/名册/单调 activation/200ms 间隔/未 Freeze/共享诊断 spec/DS 权威枪口） | ✅ L2250（私有嵌套实现，L2488） |
| 6 | self/同队过滤从 DS 名册身份比较，缺数据拒绝 | ✅ L2392（私有嵌套实现，L2507） |
| 7 | 不接旧普通攻击/资源/HP；结算只记诊断命中计数与日志 | ✅ L2195 / L2226；**零**处引用旧 `CommandManger`/`BattleManger`/HP/Mana |
| 8 | 失败走现有 Fail/Exit，不吞 | ✅ L2102（异常 + `IsFaulted` 双判）→ `Fail` → `ExitRequested(1)` |
| 9 | 释放顺序 Driver → Motion → 地图 | ✅ L1836 → L2015；地图释放仍在 Dispose 尾部 |
| 10 | 编译门（真实 Unity 2019.4 DLL，netstandard2.0 + C#7.3） | ✅ **0 警告 / 0 错误** |

改动量：**1 个文件、787 insertions / 1 deletion**（`git diff --numstat` 口径），文件 1852 → 2638 行；
大括号/圆括号 `check_cs_braces.py` PASS；编码保持 **UTF-8 BOM + CRLF（0 条裸 LF）**。

---

## 1. 验证命令与本机实测结果

```bat
REM ① 真实 Unity API 编译门（D:/Unity/2019.4.8f1 真实 DLL；本组指定输出目录，避开并行 client 的共享 output）
dotnet build Tools/PMR4UnityCheck -c Release -o Tools/PMR4UnityCheck/bin/r5-ds-host
REM ② 桩件胶水门（同一宿主文件的第二套语言面自检）
dotnet build Tools/PMUnityGlueCheck -c Release
REM ③ 整客户端玩法层门（含本宿主 + PMClientSessionHost + 旧链）
dotnet build Tools/PMClientCheck -c Release
REM ④ 结构门（无编译器时的兜底）
python Tools/check_cs_braces.py Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs
```

| 命令 | 结果 |
|---|---|
| `PMR4UnityCheck`（真实 Unity DLL，`--no-incremental` 复跑） | **0 警告 / 0 错误**；产物 DLL 内含 `CreateProjectileWiring` / `PumpProjectiles` / `TryAuthorizeDiagnosticSpawn` / `DiagnosticProjectileHitCount`（确认真编了改动文件） |
| `PMUnityGlueCheck` | **0 错误 / 0 警告**（本组复跑时的最终状态；期间曾因主侧桩件 `KeyCode` 缺 `F` 报 1 条错，已由主侧自行闭合，见 §12） |
| `PMClientCheck` | **0 错误 / 0 警告**（期间曾因并行 client 组缺 `using PMNet.Projectile;` 报 2 条瞬态错，已由其自行修复，见 §12） |
| `check_cs_braces.py` | `{ } = 351/351`、`( ) = 838/838`，深度全程非负且末尾归零 → **PASS** |
| 编码自检 | BOM = true；裸 LF = **0**（CRLF 保持） |

构建产物仅落 `Tools/PMR4UnityCheck/bin/r5-ds-host`、`Tools/PMUnityGlueCheck/bin`、`Tools/PMClientCheck/bin`。

---

## 2. 冻结 C 段逐条落点（契约 → 代码）

| 契约原文要点 | 落点（PMDsSessionHost.cs） |
|---|---|
| DS 宿主为每会话创建 History/Motion/Driver（场景就绪后、连接接入前） | `CreateProjectileWiring` **L1927**，调用点 **L1018**（在 `_sceneReady = true` 之前、`OpenServer` 之前） |
| 每 Pump 次序：先 `endpoint.Pump` → `PumpMovement` → 再投射物 | Pump 既有次序不变；仅在 `PumpMovement` 与 `CheckPlayerDrops` **之间**插入 **L1167** |
| 为所有 driver 记「本次 host AuthorityServer 帧 + OutputBoundary + TotalSimTimeMs + world 时间」的胶囊样本（半径/半高 `PMMoverDefaults`、按 `Sync.Scale` 有效缩放） | **L2133** `RecordProjectileHistory`（L2173 `history.Record`） |
| 再固定 16ms 累计最多 8 子步 Pump 投射物；**零步也 Pump(0)** 排裁决 | **L2055** `PumpProjectiles`；**L2093** 零子步分支 |
| history 新 stream 由 `history.Record` 清旧 | 直接用 `PMProjectileHistory.Record`（L2173），**不做**任何旁路/自行清表 |
| 可信 policy 检查：已认证绑定、来源 activation 单 owner 单调不重用、200ms 墙钟间隔、已存在运动 driver 且未 Freeze | **L2250** 逐条（顺序见 §6） |
| 使用共享诊断 spec 与 DS 真位置；客户端任何本地 spec 不影响 | L2347 `PMProjectileDiagnosticConfig.CreateSpec()` + L2357 DS 权威枪口 |
| team/self filter 从 DS roster 查，**缺数据 fail closed** | **L2392** `AcceptDiagnosticHit` + **L2462** `TryResolveAuthoritativeTeam` |
| `DrainSettlements` 作为诊断命中计数/日志，不直接扣血，必须记录尚无 R6 消费者 | **L2195** `OnProjectileSettlement`（唯一消费者）+ **L2226** 待取缓冲兜底排空 |
| expose `DiagnosticProjectileHitCount` 与 `ProjectileDriver` | **L653** / **L647** |
| host Error 走现有 Fail/Exit 路径不能吞 | **L2102** `PumpProjectileOnce`：异常与 `IsFaulted` 都 `Fail`；策略/过滤异常无 catch-swallow |
| 先 `Driver.Dispose` → `Motion.Dispose` → 地图释放，不泄 reserved/event | **L1836** → **L2015**；地图/场景释放仍在 `Dispose` 尾部（既有顺序） |
| 构造 `physicsScene`/`allowedColliders` 用正式 `_battleMap` 或诊断 isolated scene 的**实际现存字段**，不碰默认场景 | 正式 = **L901** `battleMap.Colliders`；诊断 = **L981** 既有 allowlist 数组；`CreateProjectileWiring` 内再校验 scene 有效且 `!= Physics.defaultPhysicsScene`（L1931） |
| DS **禁止**创建表现 | 未引用 `PMUnityProjectilePresentation`，不创建任何 GameObject/材质（C2 在 DS 上本就会抛） |
| 不接旧普通攻击/资源/HP | 全文件对 `CommandManger` / `BattleManger` / `playerBloodValue` / Mana 引用数为 **0** |

---

## 3. 精确新 hook 位置（行号 = 最终文件）

| 位置 | 行 | 说明 |
|---|---|---|
| 头部 R5-C 说明 | L25–L35 | 契约来源、接线范围、非玩法声明 |
| `using PMNet.Projectile;` | L36 | 本批唯一新增 using |
| `MaxProjectileDiagnosticWarnings = 20` | L452–L455 | 诊断日志**限流**（只影响日志，计数照常） |
| R5-C 字段块 | L540–L605 | history/motion/driver/policy/filter/账/累加器/计数 |
| `ProjectileDriver` / `DiagnosticProjectileHitCount` / `DiagnosticProjectileSettlementCount` | L647 / L653 / L656 | 公开只读观察面（供后续测试） |
| 正式白名单 | L901 | `_movementAllowlist = battleMap.Colliders` |
| 诊断白名单 | L981 | `_movementAllowlist = allowlist`（既有地板+墙） |
| 接线创建（step 7c） | L1018–L1023 | `CreateProjectileWiring` + 失败即 `ReleaseProjectileWiring` + 启动失败 |
| `_sceneReady = true` | L1025 | 接线**之前**完成 |
| `Pump()` / 墙钟 / `Physics.SyncTransforms` | L1141 / L1145 / L1157 | 时钟与几何提交点（未改） |
| `PumpProjectiles(nowMs)` | L1167 | 插在 `PumpMovement` 之后、`CheckPlayerDrops` 之前 |
| 断线 Unbind | L1288–L1305 | `PruneDisconnectedDrivers`（L1248）内，Freeze/Dispose 之后 |
| `OnConnected` / BindPlayer | L1556 / L1590–L1596 | 建运动 driver 之后 |
| `Dispose` / `ReleaseProjectileWiring()` | L1804 / L1836 | 在 `PMR3Runtime.Detach` **之前**、`_bridge.Dispose` **之前** |
| `CreateProjectileWiring` | L1927 | 见 §4 |
| `ReleaseProjectileWiring` | L2015–L2043 | 见 §8 |
| `PumpProjectiles` / `PumpProjectileOnce` | L2055 / L2102 | 见 §5 |
| `RecordProjectileHistory` / `RejectProjectileHistory` | L2133 / L2181 | 见 §7 |
| `OnProjectileSettlement` / `DrainDiagnosticSettlementBuffer` | L2195 / L2226 | 见 §9 |
| `TryAuthorizeDiagnosticSpawn` / `RejectProjectileRequest` | L2250 / L2376 | 见 §6 |
| `AcceptDiagnosticHit` / `RejectProjectileHit` | L2392 / L2423 | 见 §6 |
| `TryResolveUidByNetId` / `TryResolveAuthoritativeTeam` | L2436 / L2462 | 见 §6 |
| `DiagnosticProjectileAuthorityPolicy` / `RosterIdentityHitFilter` | L2488 / L2507 | 私有嵌套（反向依赖为零） |
| `DescribeProjectileWiring` / `DescribeProjectileKey` | L2524 / L2542 | 进 `Describe()`（L2619，已在 L2634 拼接） |

---

## 4. `CreateProjectileWiring`：构建与前置校验（L1927）

依赖的每一件东西都是**现存字段**，不另造第二套：

1. **物理场景** = `_movementPhysicsScene`（本地物理场景）。显式拒绝 `!IsValid()` 与
   `Equals(Physics.defaultPhysicsScene)` —— 与 R4-B 的隔离判据同一口径；
2. **白名单** = `_movementAllowlist`（诊断 = 本地场景刚建的地板/墙；正式 = `PMUnityBattleMap.Colliders`），
   空/ null 直接失败（`PMUnityProjectileMotion` 自身也拒绝空白名单，此处是宿主侧的显式前置）；
3. **层掩码** = `_movementQuery.LayerMask`（与运动 CapsuleCast 同口径，不另算一套 layer）；
4. `new PMProjectileHistory(_boot.Epoch)` —— epoch 已在 `Initialize` 校验非 0；
5. `new PMUnityProjectileMotion(scene, allowlist, layerMask)`（C1 适配器，构造期会二次校验同场景/有效项，并**拷贝**白名单）；
6. `new PMR5ProjectileDriver(world, bridge, epoch, history, policy, motion, filter)` —— 7 个入参全部注入；
7. `driver.Settlement += OnProjectileSettlement`（唯一消费者接线）；
8. 二次自检 `HostMotion / History / Policy` 均非 null，否则启动失败。

失败路径：逐级清理（已建的 Motion 立即 `Dispose`），返回 false → `Initialize` 失败 →
`PMDsSessionHost.Start` 返回 null → `PMDsHost` 以非 0 退出码结束进程。**不回退**任何"绕过"模式
（诊断探针不可用就不谎报 SceneReady）。

DS 侧**不**创建任何表现对象：`PMUnityProjectilePresentation` 在本文件出现 0 次。

---

## 5. 时钟与推进（`PumpProjectiles` L2055）

### 5.1 时钟口径（唯一）

- 宿主墙钟：`long nowMs = (long)(Time.realtimeSinceStartup * 1000f)`（L1145，**既有**代码，未改）；
- **运动** Pump 的 `elapsedMs` 由同一个 `nowMs` 与 `_lastMovementPumpMs` 相减得到；
- **投射物** Pump 的 `wallNowMs = (double)nowMs`，`elapsedMs` 由同一个 `nowMs` 与 `_lastProjectilePumpMs` 相减得到；
- 策略里的「200ms 墙钟间隔」用 `_projectileWallNowMs`（= 本帧同一 `nowMs`），
  **不**再引第二只时钟（无 `Stopwatch`、无 `Time.time`、无 `Time.unscaledTime`）。
- 与 driver 的契约一致：`PMR5ProjectileDriver.Pump` 只允许墙钟**单调不减**；同一宿主帧内的多次子步
  Pump 传**相同** `wallNowMs`（相等是允许的，只有减小才 fault），运动推进量完全由 `stepMs` 决定
  （已在 driver `AdvanceAuthorityMotion` 中核实：`stepMs > 0` 才 `AdvanceMotion`，`stepMs == 0` 只发布快照）。

### 5.2 固定步长累加器

```
_projectileAccumulatorMs += elapsedMs;                  // 大 elapsed 不放大 dt
while (acc >= 16 && steps < 8) { Pump(wallNow, 16); acc -= 16; steps++; }
cap = 16 * 8 = 128ms; if (acc > cap) { droppedMs += acc - cap; acc = cap; }   // 有界追赶
if (steps == 0) { Pump(wallNow, 0); }                   // 零子步仍排空声明入站
DrainDiagnosticSettlementBuffer();
```

- 单帧 Pump 次数 ∈ {1..8}（有步）或 {1}（零步），因此单帧工作量有界；
- `stepMs = 16 < PMProjectileCoordinatorLimits.MaxCatchUpStepMs(50)`，不会被 driver 截断；
- 剩余累积量钳到 128ms：长时间停滞后不会一帧内追赶出巨量子步（丢弃量记 `droppedMs`，见 `Describe()`）。

### 5.3 历史与几何的时序

`Pump()` 顶部（L1157）每帧**一次** `Physics.SyncTransforms()`，位于 `endpoint.Pump` 之前，而
投射物运动查询发生在其后 —— 满足 C1 适配器声明的宿主前置条件（"查询前世界几何已提交给 PhysX；
适配器自己永不调用 SyncTransforms"）。
历史采样（L1167 → L2133）在 `PumpMovement` 之后、任何投射物子步**之前**：本帧的命中验证因此
读得到本帧的位置（而不是上一帧的）。

---

## 6. 可信策略与身份过滤（L2250 / L2392）

### 6.1 授权（`TryAuthorizeDiagnosticSpawn`，L2250）

按**固定顺序**逐条拒绝（任一不成立即 `RejectProjectileRequest`：计数 + 限流告警 + 返回 false）：

1. `player`/`intent` 非 null；
2. `player.NetId.IsValid && Value != 0`；
3. `uid > 0 && _expectedUids.Contains(uid)` —— **无名册不授权**（名册来自引导文件，是权威输入）；
4. `_projectileDriver.IsPlayerBound(player)` —— **已认证绑定**（回调虽来自该绑定，仍显式复核）；
5. `player.OwnerConnection as PMTransportConnection` 非 null 且 `IsReady` —— 连接未就绪不授权；
6. **activation 单 owner 单调、不重用**：`activationId > _projectileLastActivationByOwner[ownerNetId]`
   （相等或更小一律拒）。拒绝是**策略层**拒绝，driver 随后写入的 `Rejected` 由协调器
   `PMActivationLedgerResult.AlreadyTerminal` 保护，**不会**把已 `Confirmed` 的合法旧 id 覆成 `Rejected`；
7. **200ms 墙钟间隔**：`_projectileWallNowMs - lastFire >= PMProjectileDiagnosticConfig.FireIntervalMs`；
   同时显式拒绝墙钟倒退（`now < lastFire`）；
8. **已存在运动 driver**（`_driversByUid[uid]` 非 null、未 `IsDisposed`）且 **未 `IsFrozen`**；
9. 权威 `Sync.Position` / `YawDegrees` 有限；
10. **共享诊断 spec 工厂** `PMProjectileDiagnosticConfig.CreateSpec()` 非 null（10 m/s、0.1 m、1500 ms、
    DelayDestroy 150 ms、StopOnHit、HideOnStop —— 与客户端"只读共享"同一份常量）；
11. **DS 权威枪口** = `sync.Position + forward(sync.YawDegrees) * MuzzleOffsetM(0.6)`（Y-up 前向）。

成功才写账（单调 + 间隔），因此**拒绝不吃配额**；返回 `verdict = Confirmed`。

**为什么 `Confirmed`**：本批**不接**资源/HP 门（无蓝耗、无能量、无冷却），所以没有 Pending 的理由；
`Pending` / 显式 `ResolveActivation` 的完整路径在 driver/协调器里存在，但**本宿主未使用**（见 §11 U2）。

**客户端伪造面为零**：上行 payload 的 codec DTO（`PMProjectileSpawnIntent`）只承载
Key/ActivationId/Position/Direction/Yaw/PredictionMs，**表达不了** spec、权威 NetId、AllowedTargets；
其中 Position/Direction 只被协调器用于"10 m 枪口容差"与方向意图，速度/半径/寿命一律来自
`trustedSpec`。DS 侧生成位置取**自己的**权威位置 + 权威 yaw，不采信客户端位置。

策略以**私有嵌套类** `DiagnosticProjectileAuthorityPolicy`（L2488）实现，只经
`IPMR5ProjectileAuthorityPolicy` 注入 driver —— driver/核心对宿主零反向依赖。

### 6.2 self / 同队过滤（`AcceptDiagnosticHit`，L2392）

`IPMProjectileHitFilter.Accept(key, targetSample, sanitizedImpact)` 的判定：

1. `key.OwnerNetId → uid`（`TryResolveUidByNetId`，L2436：在**本局已创建**的 `_playersByUid` 里按
   `player.NetId.Value` 反查）；
2. `target.NetId → uid`（同一反查）；
3. 两侧 `uid → team`（`TryResolveAuthoritativeTeam`，L2462）；
4. `ownerUid == targetUid` → **拒**（self）；
5. `ownerTeam == targetTeam` → **拒**（同队）；
6. **任一环节解析不出来 → 拒**（缺数据 fail closed，绝不"默认放行"）。

队伍身份来源：
- **正式内容**：已校验的名册队伍索引 `_formalTeamIndexByUid`（TeamId 2 → +X 侧 = 0，TeamId 1 → -X 侧 = 1）；
- **诊断场景**：名册 `TeamId` 取值为 1/2 时直接用；否则退到**与 `BuildSpawnSync` 出生侧同一条规则**
  （名册行奇偶：偶行 → -X → team 1，奇行 → +X → team 2）。这不是"凭空分队"，而是复用本宿主
  给诊断模式定侧的那份名册事实（诊断局没有独立队伍表可用）。

过滤同样以**私有嵌套类** `RosterIdentityHitFilter`（L2507）实现，只经 `IPMProjectileHitFilter` 注入；
它**只决定"是否接受这次命中"**，不做任何伤害计算（伤害属 R6）。

---

## 7. 历史采样七元组（`RecordProjectileHistory`，L2133）

对**每个**运动 driver（`_driversByUid`）每宿主帧记一条 `PMProjectileTargetSample`：

| 字段 | 来源 | 备注 |
|---|---|---|
| `Epoch` | `_boot.Epoch` | 与 `PMProjectileHistory` 构造用的同一 epoch |
| `NetId` | `player.NetId.Value` | 副本对象 NetId（= 客户端上报的 `TargetNetId` 口径） |
| `StreamVersion` | `driver.StreamVersion` | DS 运动 driver 默认 1；`BeginServerResync` 升代时 `history.Record` 会清旧流 |
| `ServerFrame` | `_movementServerFrame` | 宿主自己决定的 `AuthorityServer` 帧（`PumpMovement` 每帧 +1，**不**用客户端帧号） |
| `OutputFrame` | `driver.OutputBoundary` | Authority 角色 ⇒ `Input` 域，满足 history 的域校验 |
| `TotalSimTimeMs` | `driver.AuthorityTotalSimTimeMs` | 只用于"同 stream 帧锚新鲜度"（契约禁止帧号×16ms 换算） |
| `WorldTimeMs` | 本帧 `nowMs`（double） | 与运动/投射物 Pump **同一**单调墙钟 |
| `Position` | `driver.GetAuthoritativeSync().Position` | 权威位置 |
| `RadiusM` | `PMMoverDefaults.CapsuleRadiusMeters * sync.Scale` | **含有效缩放** |
| `HalfHeightM` | `PMMoverDefaults.CapsuleHalfHeightMeters * sync.Scale` | 同上 |
| `Teleported` / `Alive` | `false` / `true` | 见 §11 U3（本批无传送判定源与 HP 系统，**不伪造**未观测事实） |

`Scale` 非有限或 ≤ 0 时跳过并计数（否则 history 会因 `radius-not-positive` 拒收）。
记录失败**不静默吞**：`_diagnosticProjectileHistoryRejects` 计数 + 前 20 条限流告警（`RejectProjectileHistory`，L2181）。
`Describe()` 暴露 `historyTargets` / `historyRejects`。

---

## 8. 释放与清理（L1836 / L2015）

`Dispose()` 中的插入点（L1836）位于：

```
_lobby.Dispose → _endpoint.Dispose → [R5-C: ReleaseProjectileWiring()] → PMR3Runtime.Detach(_world)
   → 运动 driver Freeze/Dispose → _bridge.Dispose() → 场景对象 Destroy → 地图/隔离场景释放
```

`ReleaseProjectileWiring()`（L2015）顺序与幂等性：

1. `Settlement -= OnProjectileSettlement`（先摘订阅，避免 Dispose 过程中回调进宿主）；
2. `driver.Dispose()` —— driver 侧自会：**取消全部未消费 NetId 预留**（`CancelReservedNetId`）、
   `bridge.DestroyObject` 销毁本会话存活权威对象、`_mirrorSubscription.Dispose()`（**只摘本 world 的**订阅，
   不调 `PMR5ProjectileEvents.ClearAll`）、摘掉每个 player 的 `ProjectileDriver` 引用、清空本地账；
3. `motion.Dispose()` —— 释放扫描缓冲与停止标记表（不持有 GameObject）；
4. 清宿主侧字段与账（`_projectileLastActivationByOwner` / `_projectileLastFireWallMsByOwner` /
   累加器 / `_movementAllowlist`）。**不动**地图与场景对象（那是宿主原有释放职责，在更后面）。

放置在 `PMR3Runtime.Detach` **之前**是刻意的：让释放发生在"world/bridge 仍完整接线"的稳态下
（`DestroyObject` 只依赖 world/bridge，不依赖 `PMR3Runtime.Bridges`/`RemoteSender`，但稳态更不易出错）；
同时 `_bridge.Dispose()` 在其后，因此 `DestroyObject` 的注销路径仍然有效。
`ReleaseProjectileWiring` 也被 `Initialize` 的失败路径调用（L1020），因此**任何失败起点**都不会漏预留/事件。

**断线联动**（L1288）：`PruneDisconnectedDrivers` 在 `Freeze + Dispose` 运动 driver 之后调
`driver.UnbindPlayer(player)`；该调用会取消该 owner 的未消费预留、丢弃它的入站队列、退休本地账，
并销毁它**悬空**的权威对象（否则 `ClearOwner` 退役登记后会变成"既不复制也不销毁"）。
任何异常 → `Fail`（不吞）。

---

## 9. 结算出口（诊断口径，non-gameplay）

- 主路径：`driver.Settlement += OnProjectileSettlement`（L1986 订阅，L2195 处理）。
  `PMProjectileDriver.DrainCoordinatorOutlets` 在**有订阅者**时只走事件，因此不会重复投递；
- 兜底路径：`DrainDiagnosticSettlementBuffer`（L2226）每帧先看 `PendingSettlementCount > 0` 才 Drain
  （避免每帧分配），一旦有内容即说明"订阅者未交付"，记 `_diagnosticProjectileUnconsumedSettlements`
  并**仍然消费计数**，绝不静默丢；
- 计数口径：
  - `DiagnosticProjectileSettlementCount` = 已消费**结算条数**；
  - `DiagnosticProjectileHitCount` = `settlement.HitCount` 之和 = 已消费**命中条数**（≠ 扣血次数）；
- 日志：每条结算一行 `Info`（key/activation/authority/origin/stopOnHit/hits/累计），每个目标一行
  `Info`（netId/stream/还原档/impact），字面写明「**non-gameplay，不扣血**」；
- **明确不接 R6**：不写 HP/Mana/SuperEnergy、不触发死亡、不调旧 `ApplyHitEvents`/`ApplyAuthoritativeHpAndDeath`；
  R6 接入时这里就是唯一替换点（`DiagnosticProjectileHitCount` 只作可观察证据，不能当作伤害）。

---

## 10. 新增公开 API（本文件）

```csharp
// PMNet.Unity.PMDsSessionHost —— 新增
public PMR5ProjectileDriver ProjectileDriver { get; }        // 未接线/已释放时为 null；只读观察
public long DiagnosticProjectileHitCount { get; }            // 累计已消费命中条数（≠ 扣血）
public long DiagnosticProjectileSettlementCount { get; }     // 累计已消费结算条数
// Describe() 的 projectile= 段新增：faulted/bound/authority/reserved/views/historyTargets/
//   historyRejects/policyRejects/filterRejects/settlements/hits/droppedMs
```

驱动/核心/适配器的公开 API **未改**（本批只消费 `BindPlayer` / `UnbindPlayer` / `Pump` /
`Settlement` / `DrainSettlements` / `PendingSettlementCount` / `HostMotion` / `History` / `Policy` /
`IsFaulted` / `FaultReason` / `FaultError` / `IsDisposed` / `Epoch` / `IsPlayerBound` /
`BoundPlayerCount` / `AuthorityObjectCount` / `ReservationCount` / `ViewCount`）。

---

## 11. 未验证 / 诚实边界

| # | 项 | 说明 |
|---|---|---|
| U1 | **未启动 Unity** | 按用户要求"先代码后实机"。因此本批**不标实机 PASS**、不声称 R5-C 完成、不声称 T45 完成。真机需验：真实 PhysX 的 `SphereCast`/`OverlapSphere` 行为、`SceneManager.CreateScene(LocalPhysicsMode.Physics3D)` 在 Play 下的 `GetPhysicsScene()`、`Physics.SyncTransforms` 与投射物查询的同帧可见性、8 子步实际追赶量 |
| U2 | **Pending 路径未被使用** | 策略恒返回 `Confirmed`（本批无资源/HP 门），因此 `PMR5ProjectileDriver.ResolveActivation` 的显式后续入口与 Pending→Confirmed 的门控流程在本宿主上**从未被执行过**（代码路径存在，未被本宿主驱动） |
| U3 | `Teleported=false` / `Alive=true` 为**常量** | 本批没有传送判定源，也没有生命值系统（HP 属 R6）。不伪造未观测事实；但 R6 接 HP 后**必须**改成真实值，否则命中验证会把已死者当活人（`PMProjectileValidator` 只按 `sample.Alive` 判断） |
| U4 | 诊断队伍身份的**兜底规则** | 名册 `TeamId` ∈ {1,2} 时直接用；否则用"名册行奇偶"（与 `BuildSpawnSync` 定侧同一规则）。若名册 `TeamId` 全为 0 且只有 1 名玩家，则任何命中都属于 self/同队 → 全部被拒（fail-closed 的预期表现，不是缺陷） |
| U5 | **重连 gap（继承）** | `OnConnected` 对同 uid 重复入局直接 `return`，因此重连后**不会**重建运动 driver、也不会重新 `BindPlayer`。这与本宿主既有的运动接线行为一致（原有 gap），本批**未扩大也未修复** |
| U6 | 每帧一次 `new List<int>(_driversByUid.Keys)` | 与既有 `PumpMovement` / `PruneDisconnectedDrivers` 写法一致（名册 ≤ 6），未做池化；不是有界性问题，只是少量 GC |
| U7 | 累加器丢弃量只计数 | 128 ms 上限之外的丢弃量记入 `droppedMs`，未做告警节流或指标上报 |
| U8 | 历史记录失败只计数 + 限流告警 | 历史缺失会让命中验证报 `HistoryUnavailable` 并 skip（**不是**静默扣血），因此按"可观察"处理；若产品口径要求"历史必成"，需把该路径改成会话 `Fail` |
| U9 | 10 m 枪口容差未量化 | DS 枪口用**权威 yaw**、客户端用本地 yaw，转向瞬间二者有非零偏差；容差由协调器 `MuzzleToleranceM=10m` 判定，未在真实链路量化过最坏偏差 |
| U10 | 结算日志量 | 每条结算 + 每目标一行 `Info`；长局会产生较多日志行（仅诊断用途，未做采样） |
| U11 | 未压测 | 多 owner 并发、`SaturationFailures`（C1 的 32 命中缓冲）与 `layerMask` 是否需收紧、`MaxInbound*` 队列上限在真实负载下的表现，均未量化 |
| U12 | 客户端宿主未接线（他组） | `PMClientSessionHost` 的 C 接线由并行组进行；本组**未读/未改**其实现细节，只观测到它的编译状态（见 §12） |

---

## 12. 并行组瞬态与已闭合项（均**不在**本组写入边界，仅作对账）

> 本组写入期间，另两个文件被并行工作流改动，导致门禁在几分钟窗口内变红。
> 两者现均已自行修复，**最终三个门禁全绿**；本组对这两个文件**一行未改**。

1. **`Tools/PMUnityGlueCheck/UnityStubs.cs` 的 `KeyCode` 缺 `F`（主侧文件，已闭合）**：
   并行 client 组的 `PMClientSessionHost.cs:1277` 使用 `Input.GetKeyDown(KeyCode.F)`（契约要求的 F 键单次诊断开火）。
   真实 Unity 2019.4 的 `KeyCode` 有 `F`（=102），`PMR4UnityCheck`（真实 DLL）因此一通过；只有**桩件**门禁一度失败：
   `PMUnityGlueCheck` → `PMClientSessionHost.cs(1277,42) CS0117: "KeyCode"未包含"F"的定义`（1 个错误）。
   主侧已自行在桩里补上 `F = 102`（`UnityStubs.cs` mtime 18:03:23），`PMUnityGlueCheck` 复跑 **0 错误**。
2. **并行组缺 `using PMNet.Projectile;`（已闭合）**：本组在 18:00 左右观测到 `PMClientSessionHost.cs`
   引用 `PMProjectileDiagnosticConfig` 却未导入命名空间（`PMClientCheck` 报 2 条瞬态 `CS0103`）。
   本组**未改**它；其后该组自行补齐，`PMClientCheck` 复跑 **0 错误 / 0 警告**。

结论：本组所有门禁失败都可归因到上述**两个非本组文件**，**没有一条**来自 `PMDsSessionHost.cs`；
截至本报告，三个门禁（`PMR4UnityCheck` / `PMUnityGlueCheck` / `PMClientCheck`）均为 0 错误。

---

## 13. 已检查范围

**完整读取的文档（按委派顺序）**：`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`（全文）
→ `Docs/plans/net-r5-network-contract.md`（全文，重点末段「C宿主首个可执行入口」）
→ `Docs/plans/_r5_network_review.md`（全文，重点新增 API/语义变更段）
→ `Docs/plans/_r5_unity_adapter_report.md`（全文，重点 C1/C2 精确 API 面与 PENDING_USER 段）
→ `Client/Assets/Scripts/PMProjectile/PMProjectileDiagnosticConfig.cs`（全文，共享只读）
→ `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`（全文）。

**文档如何决定实现入口**：契约末段给出唯一的接线时序（创建时点 / Pump 次序 / 累积器 / 策略检查项 /
过滤口径 / 结算口径 / 释放顺序 / 地图字段来源），因此实现逐条从它落点；
`_r5_network_review.md` 给出 driver 的**实际**公开 API 与三处语义变更（`UnbindPlayer` 会收拾 owner、
`Pump` 客户端分支也做清理、`CatchUpRegisteredMotion` 在 SpawnReady 内自调用），
因此宿主**不**自己补追赶、也不自己收拾预留；
`_r5_unity_adapter_report.md` 给出 C1 的构造签名（`PhysicsScene, Collider[], int`）、
fail-closed 三出口与"宿主不得调 `Physics.SyncTransforms`、但查询前必须已同步"的前置条件；
`Client/Assets/AGENTS.md` §1.1 明确进程形态唯一来源是 `PMNetRuntime.IsDedicatedServer`（DS 侧无需再判）。

**只读读取的生产代码（用于确认真实 API，不信旧报告假设）**：
`Server/Boot/PMDsSessionHost.cs`（全文，被改对象）、`PMR5ProjectileDriver.cs`（全文关键段：
ctor / BindPlayer / UnbindPlayer / CleanupOwner / Pump / ApplyInbound* / HandleServerSpawn /
`AdvanceAuthorityMotion` / TryFire / SubmitPredictedHits / ResolveActivation / TryServerDirectSpawn /
CopyViews / DrainSettlements / DrainViewChanges / RebuildViews / Dispose / Describe / 计数）、
`PMProjectileCoordinator.cs`（`RequestSpawn` 的 10 m 枪口与 spec 克隆、`ResolveActivation` 的终态不倒退、
`AdvanceMotion`/`CatchUpRegisteredMotion` 预算、`PMProjectileSettlement`、`PMProjectileCoordinatorLimits`）、
`PMProjectileContracts.cs`（全文）、`PMProjectileHistory.cs`（全文，Record/TryResolve 的拒收条件）、
`PMProjectileValidator.cs`（filter 调用点与 sample.Alive/size 判定）、`PMR3/PMR3Player.cs`（公开面）、
`PMR3/PMR4MovementDriver.cs`（`OutputBoundary` 域、`StreamVersion`、`AuthorityTotalSimTimeMs`、
`GetAuthoritativeSync`、`IsFrozen`）、`PMMover/PMMoverState.cs`（`PMVector3`、`PMMoverSyncState.Scale`、
`PMMoverDefaults` 胶囊尺寸）、`PMNet/PMNetIdentity.cs`（`PMFrameId.IsValid/Domain`）、
`PMNet/Session/PMNetSessionBridge.cs`（`DestroyObject`）、`PMR3/PMR3Runtime.cs`（`Detach` 段）、
`PMUnity/PMUnityProjectileMotion.cs`（构造校验与白名单拷贝）、`PMUnity/PMUnityBattleMap.cs`（`Colliders`/`PhysicsScene`/`Query`）、
`PMUnity/PMUnityMoverCollisionQuery.cs`（`LayerMask`/`Scene`/`AllowlistCount`）、
`Tools/PMR4UnityCheck/PMR4UnityCheck.csproj`、`Tools/PMUnityGlueCheck/{PMUnityGlueCheck.csproj,UnityStubs.cs}`、
`Tools/PMClientCheck/PMClientCheck.csproj`。

**本次写入的文件（仅此 2 个）**：`Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs`（唯一代码改动）、
`Docs/plans/_r5_ds_host_report.md`（本报告）。

**未做**：未启动 Unity；未编 UE；未改 driver/core/适配器/合同/主计划/生成物/锁文件/csproj；
未改 `PMClientSessionHost.cs` 及任何并行组文件；未执行任何 git/svn 写操作；未提交；未递归委派。
