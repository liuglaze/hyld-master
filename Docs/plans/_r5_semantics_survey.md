# R5 首批核心实现契约 —— 只读语义整理（explore）

> 任务：只读整理 R5 首批可落地的投射物契约（假弹/权威镜像接管、三类 Pending、停止墓碑、L0–L4 命中验证），转成 Unity 米 + 整毫秒，并标出冻结契约冲突/缺口。
> 边界：只读下列文档与 `D:/hyld-refactor-survey/R0_4_projectile.md`；**未**新建 UE 全项目调查、**未**改代码、**未**编译。唯一写入＝本文件。
> 结论分级：**【确认】**＝文档中可逐条回链；**【推断】**＝由文档语义推出，附置信度；**【未知】**＝本次证据不足。

## 文档路由（必读顺序 → 它们决定的搜索入口）

| 顺序 | 文档 | 决定的入口 |
|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 客户端→`Client/Assets/AGENTS.md`；服务端→`Server/AGENTS.md`；计划→`Docs/plans/net-architecture-migration.md` |
| 2 | `Client/Assets/AGENTS.md` | ①§1 逻辑帧时长 `frameTime=0.016f`（旧实现口径，**与 D-R0-19 冲突**）；②§2 坐标系（Y 为高度层，玩家 Y=1）；③§4.3/§9 旧铁律「联网视觉子弹禁用所有 Collider + isKinematic」；④§4.2 HitEvent 去重键 `attackId*100000+victimBattleId`；⑤§7 参数表（`PredictionHistoryWindowSize=40`、`MaxClientAttackAge=8`） |
| 3 | `net-r0-contract.md` §2.6 / §3.7 / §7 / §9 | D-R0-30..40 冻结决策；`PMProjectileOrigin`/`PMActivationResult`/`PMProjectileRegistry`/三类等待空壳；§7 契约表（容量与墓碑口径）；§9「投射物各层阈值 + 三类等待 TTL/容量 由 R5 照抄为初值」 |
| 4 | `D:/hyld-refactor-survey/R0_4_projectile.md` §0–F、I–M | UE 侧唯一源证据：§A 分流与 ID、§B 假弹接管、§C L0–L4、§D 三类等待、§E 历史位置链、§F 停止/墓碑/回池、§I 阈值全表、§J U-01..U-25 契约项 |
| 5 | `net-architecture-migration.md` §3.9.3 / §3.9.4 + §5 R5 行 + T45 | M09 落点 `Client/Assets/Scripts/PMProjectile/`；运行时契约「三类等待分别有容量、TTL 和清理策略」；R5 三句硬要求（被拒子弹实际撤销 / ID世代复用 / 停止后迟到命中） |
| 6 | `net-r4-prediction-contract.md`（R5 直接依赖的帧锚契约） | **单位与时间基的 Unity 适配已在此冻结**：位置＝Unity 米；`DeltaTimeMs` 整毫秒 int 1..50；`TotalSimTimeMs`(double)；位姿阈值 0.05m/0.01m/s/1°；断线 Freeze + 墙钟 2s |

**入口判定说明**：本任务的入口不是 UE 源码，而是「R0 冻结契约 + R0_4 语义 + R4 已落地的单位/时间决策」三者的交叉。凡 R0_4 用 UE 口径（cm/秒/帧/Actor 存活）表述处，一律以 R4 契约的米与整毫秒口径重述；凡 R4 未覆盖（墙钟 TTL、比赛时间、墓碑长度）处，显式标为需 R5 冻结。

---

## 一、已确认

### 1.1 生成分流与 ProjectileID

| 项 | 语义 | 源 |
|---|---|---|
| 分流判据 | `bClientPredicted = IsOwnedByLocalController(Owner)`；`bServerDirectSpawn = !bClientPredicted && Owner.HasAuthority()`；**两者皆否 → 本次什么都不生成** | R0_4 §A.1 |
| 路径① 预测 | 主控端先本地生成假弹并登记 `FakeProjectiles[N]`，同时发 Spawn RPC；服务器由该 RPC 生成权威弹（**服务器不为这颗弹打直创标记**，`Internal` 显式传 `bServerDirectSpawn=false`） | R0_4 §A.1/§A.3/§M |
| 路径② 直创 | 仅权威弹，`ServerAuthoritativeProjectileID = ProjectileID`，不发 Spawn RPC、无假弹；所有端（含主控）都是纯镜像 | R0_4 §A.1/§B.4 |
| ID 分配 | 函数级 `static int32`：首次 1；`MAX_int32 → 1`；跳过 `-1`。**分配端＝调用方进程**，随 RPC 过网后服务器沿用同一 `N`；路径②由服务器自己分配 | R0_4 §A.2 |
| 注册表隔离 | 两张表都挂 Controller，**每玩家一份**：`FakeProjectiles`（仅主控端）/ `ServerProjectiles`（仅 DS）。不同玩家重号合法 | R0_4 §1.2 |
| 信任缺口 | `Server_SpawnNetProjectile_ForceValidate` **不校验** `ProjetileID` 范围/唯一性，也不校验 `SourceBehaviorInstanceID`；客户端可自选号覆盖自己 Controller 内的既有条目 | R0_4 §A.2 / §C.1 |
| 枪口约束 | 分流**之前**执行：`dist(SpawnTransform.Location, Pawn 权威位置) > 1000cm` → `REJECTED`；`Owner` 非 Pawn 放行；`<=0` 关闭；**挂起恢复路径不重检** | R0_4 §A.4 |
| 落地顺序（固定） | ①Owner 有效性 ②Spawn + 写 ID/直创标记 ③写 `SourceBehaviorInstanceID` + 绑裁决委托 ④`ActualPredictionTime = Min(客户端, GetPredictionTime())` ⑤`CatchupDelta = Actual*0.5 + ExtraDeferWait`（钳到 `MoveDuration`）⑥**`RegisterServerProjectile(N)` 必须先于追赶** ⑦共享伤害组 ⑧追赶 ⑨按需隐藏 | R0_4 §A.3 |

### 1.2 假弹 ↔ 权威镜像接管

| 项 | 语义 | 源 |
|---|---|---|
| 假弹判据 | **非权威端 + 有 LagComp 组件** → `InitFakeProjectile()`（仅置 `bFakeClientProjectile=true`）+ `AddFakeProjectile()` | R0_4 §B.1 |
| 接管条件 | `EnableLagCompensation() && !HasAuthority() && !bAlreadySyncAutonomousFakeProjectile && IsOwnedByLocalController(this)`；触发入口 `InitializeLagCompensation` / `OnTakeFromPool` / `OnRep_ProjectileID` | R0_4 §B.2 |
| 一次性消费 | `GetMatchedFakeProjectile`：按 ID 查表，命中即 **Remove**（消费），返回弱引用 | R0_4 §B.2 |
| 对齐字段（6 项） | `position`、`rotation`（+`PostNetReceiveLocationAndRotation`）、`movementTimeBase`（`SetMovetime`）、`cachedDamageTargets`（防重复命中）、`isStopped`、`timeAfterStopped` | R0_4 §B.2 / U-07 |
| 停止态附加动作（5 项） | 速度清零 + `UpdateComponentVelocity` + `SetUpdatedComponent(nullptr)`（顺带关组件 Tick）；`RootSphere → ProbeOnly` 且关 Overlap；`DamageDetectShape → NoCollision`；按 `bHideWhenStop` 隐藏并置 `bIgnoreFactionVisibility=true` | R0_4 §B.2 / U-07 |
| **刻意不做** | **不调** `StopMovementImmediately()` / `StopSimulating()`——避免 `OnProjectileStop` 二次广播导致停止表现双发 | R0_4 §B.2 |
| 收尾 | 假弹 `SetActorHiddenInGame(true)` + 回收；置 `bAlreadySyncAutonomousFakeProjectile = true` | R0_4 §B.2 |
| 迟到分支 | 表项存在但弱引用失效 && 曾经存在过假弹 → `StopMovementImmediately` + 隐藏 + 关 Tick + 关碰撞 + **置幂等标志**（否则 `OnRep_ProjectileID` 二次触发会因表项已 Remove 而漏走隐藏分支） | R0_4 §B.3 |
| 无匹配 | `OutFound == false` → 直接 return，**不置幂等标志** | R0_4 §B.3 |
| 位置写入闸门 | `HasLocalFakeProjectile()`（**只读，不消费**）为真 → `OnRep_PositionInfo` 不写位置/速度/MoveTime | R0_4 §B.3 / U-08 |
| 假弹销毁 | `HandleOnDestroyed` → `MarkFakeProjectileDirty`：表项置 nullptr 但**保留 key**，使 `OutFound` 仍为 true → 走迟到隐藏分支 | R0_4 §B.3 |
| 直创镜像 | `OnRep_ServerAuthoritativeProjectile`：关 `RootSphere`/`DamageDetectShape` 碰撞、`bDisableHomingArrivalStop=true`；`HandleDamageDetectShapeOverlap` 开头对直创镜像**直接 return**（不收集候选、不发 Verify）；停止态由 `OnRep_StoppedProjectileID` 驱动（需 ID 匹配 + 直创 + 非权威 + 未停止） | R0_4 §B.4 / U-09 |
| 假弹不能自接 RPC | 假弹是本地 Actor，其 Server RPC 会被网络层丢弃 → **Spawn 与 Verify 都由 Controller 上的 LagComp 代发**，保证两者同 Channel 保序；但**不保证**与激活裁决 RPC（PlayerState Channel）的顺序——这正是 Layer 0 与三类等待存在的原因 | R0_4 §B.5 |

### 1.3 三类 Pending

| | A 挂起 Spawn | B Verify 暂存 | C 命中结论暂存 |
|---|---|---|---|
| 问的问题 | 生成源激活了吗 | 子弹还没生成，这颗命中怎么办 | 命中已成立，外层激活算数吗 |
| 宿主 / 键 | Driver / `SourceBehaviorInstanceID` | LagComp / `ProjectileID` | LagComp / `ProjectileID` |
| 触发 | Spawn 时裁决 = `Pending`，记 `PendingWallTime`（**墙钟**） | Verify 时查不到子弹、但 ID 在「待生成」集合 | L0 = `Pending` 且 L1–3 结论非空 |
| 解挂 | `Confirmed` → `ExtraWait = now - PendingWallTime` → Internal 生成 → `ReplayPendingVerifyHits`；`Rejected`/Owner 失效/生成失败/TTL → 丢弃 + `DiscardPendingVerifyHits` | **无条件先清 pending 标记**，再按入队顺序逐条 `ExecuteVerifyProjectileHit(..., ExtraWait)`；回放必须在 `RegisterServerProjectile` 之后 | `Confirmed` → 弱引用存活过滤 → `SettleProjectileHit` 补 L4；`Rejected` → 丢弃 |
| 容量 | **无独立数量 cap（追加处）** | 单 ID ≤ **5** 或 总量 ≥ **128** → 丢新到 | **128**，满则扫 `EnqueueTimestamp` 丢最旧 |
| TTL | **2.0s**（`pm.Behavior.PendingGate.SpawnTTL` 或 Config），Tick 内超时清理 | **无独立 TTL**（随 A） | **2.0s**，`Stash`/`Resolve` 两入口**惰性**清理（组件不 Tick） |
| 合并 | — | 扁平数组**保序**，条目须持目标引用防悬空 | 同 ID 后续命中**合并追加 `HitResults` 且不刷新时间戳** |
| 其它清理 | `OnUnregister` 全作废并联动清 B；`ResolvePendingSpawnsAsDropped` 防御性丢弃 | `DiscardPendingVerifyHits`；`EndPlay` | 绑定漂移（换 Pawn）**清空**；`EndPlay` 清空 |
| 硬语义 | 挂起期**不生成幽灵子弹**；挂起时长须实测并叠加进追赶与校验窗口 | 暂存条目**原样四入参** | **Pending 期间绝不产生任何 GE/属性变化**；命中即停（`bStopWhenDamageSomebody`）在暂存时**立即执行** |

源：R0_4 §D.1–§D.4、U-04/U-20/U-21。

### 1.4 停止与墓碑

| 项 | 语义 | 源 |
|---|---|---|
| `bStopped` | 物理停止：置位 + 服务器写 `StoppedProjectileID` 并标脏复制 + Niagara 收尾 + 关 `RootSphere`/`DamageDetectShape` + 可选隐藏 + 停止时周围检测。**不解除 `ServerProjectiles[N]` 注册**（墓碑期关键） | R0_4 §F.1 |
| 墓碑长度 | `TimeToDestroyMyself = max(0.15s, DelayDestroyTime)`；服务器权威非假弹且 LagComp 有效时 `GraveDelay = clamp(GetPredictionTime()*2 + TickRateBufferSec, 0, MaxGraveDelay)`，最终取 `max`。AI 子弹/无 LagComp → `GraveDelay = 0` | R0_4 §F.2 / U-22 |
| 墓碑期语义 | 只是 verify 数据载体：碰撞已关、注册保留、**迟到 Verify 仍能按 N 完成完整 L0–L4**（走 §C.3 的 ②b 停止态锚定） | R0_4 §F.1/§F.2 |
| 停止 ≠ 销毁 | 销毁/回池＝生命真正终点：`OnReturnToPool` 内 `UnregisterServerProjectile(N)`，此后同 N 迟到 Verify 走「找不到子弹」告警丢弃 | R0_4 §F.1/§F.4 |
| 延迟隐藏 | `bStopped && !bHideWhenStop && DelayDestroyTime>0` → 累加 `TimeAfterStopped`，达阈值后隐藏 | R0_4 §F.2 |
| 停止补触发 | `bEnableDetectOnProjectileStop && StopDetectShape` 有效且（有权威或假弹）：`CachedDamageTargets>0` 立即 `DetectTargetsOnProjectileStop()`；`==0` 且要求需先有伤害 → 置 `bStopDetectPendingLateDamage`，在 Verify 尾部（`CachedDamageTargets>0`）补做 | R0_4 §F.3 |
| 回池清单（要点） | 关碰撞/关 Tick/隐藏；解生成源裁决委托 + `SourceBehaviorInstanceID=0`；`bFakeClientProjectile=false`、`bStopped=false`、`TimeAfterStopped=0`、`SpawnOrigin=ZeroVector`；复制字段重置（含 `StoppedProjectileID`）；`CachedDamageTargets.Reset()`、`VerifyHitRPCCount=0`、`bStopDetectPendingLateDamage=false`；`UnregisterServerProjectile` | R0_4 §F.4 / U-23 |
| **已确认缺陷** | 运行时白名单 `AllowedHitTargets` 在 `OnReturnToPool` 与 `OnTakeFromPool` **都未清空**，只有 `SetAllowedHitTargets` 会 Reset → 池化复用会继承上一命的白名单 | R0_4 §F.4（D-R0-39 要求 Unity 补上） |

### 1.5 L0–L4 命中验证算法

- **L0 激活裁决**：`SourceBehaviorInstanceID == 0` → 默认 `Confirmed`；server-origin 号段（`<= 32767`）→ 恒 `Confirmed`；命中 512 容量环形缓存 → 返缓存值；**miss → `Pending`**。`Rejected` → **丢弃整包，不进 L1–4**；`Pending` → 继续跑 L1–3，L4 改为暂存。
- **L1 三道闸门**（任一失败**整包丢弃**）：①生命周期 `!bInitialized || HitTargets.IsEmpty()` ②`++VerifyHitRPCCount > 5` ③`HitTargets.Num() > 100`。入口 ForceValidate 的 `MaxHitTargets` 与业务层 `MaxTargetsPerRPC` 已统一为 100。
- **L2 三分支预算**：
  - ① **飞行中**（`!bStopped && !skipTrajectoryValidation`）：锚＝服务器子弹**当前位置**；`maxDev = initialSpeed * (2*safeRewind + tickBuffer + extraDefer)`。
  - ②a **停止+回放**（`bStopped && extraDefer > 0`）：锚＝服务器权威**出生点** `SpawnOrigin`；同 ① 预算；`initialSpeed <= 0` → **跳过该约束**交 L3。
  - ②b **停止+正常**（`bStopped && extraDefer == 0`）：锚＝**停止位置**；`maxStoppedDist = damageShapeRadius + 500cm + tolerance`。
  - `skipTrajectoryValidation` **只跳 ①**，② 两分支永不跳过。
  - `safeRewind = clamp(clientRewindTime, 0, MaxRewindTime)`。
- **L3 逐目标**（O(N)，N ≤ 100；`RewindLocation`/半径/半高/`DynamicThreshold` **每目标独立算，不得跨目标复用**）：
  1. 前置：`IsValid(Target)`；去重（`!bCanDamageSameTargetMultitimes && CachedDamageTargets.Contains` → skip）；白名单 `IsTargetAllowed`。
  2. **位置还原优先级**：① **帧锚** `FrameAnchor`（需目标为 Mover + `TargetServerAnchorFrame != NONE` + `bExact` + 新鲜度窗口内）→ ② **Rewind**（`SavedPositions` 按 `MatchTime - PredictionTime` 线性插值，`bTeleported` 样本**不插值**）→ ③ **当前位置**；**所有分支统一 `+= TargetVisualOffset`**。
  3. **帧锚新鲜度**：`age = newestSnap.SimTimeMS - anchorSnap.SimTimeMS`，`maxAge = (MaxRewindTime + 0.1)*1000 + extraDefer*1000`；`age` 必须 ∈ `[0, maxAge]`（**未来帧负 age 也退化**）；**用单调仿真时间而非帧号差**；不通过 → **退化 Rewind，而非整次命中硬拒**。
  4. 目标尺寸：胶囊（缩放后）→ 无胶囊用 Bounds 近似 → **再与蓝图接口缓存值取 max**；`DynamicThreshold = damageShapeRadius + capsuleRadius + tolerance`。
  5. **`ImpactPoint` 消毒**：目标竖直中心线线段 + 客户端点距 > `threshold` → 钳到 threshold 球面；**只钳不拒**（不连坐其他目标）。
  6. **Filter 重放**：用消毒值重建**单元素** `FHitResult`，**补 `BoneName` 与 `Component`（目标主骨骼网格）** → 逐个 `DamageTargetFilters`；空 → skip。
  7. **几何判定**（默认开，线段–线段）：子弹线段 `[P_prev, P_cur]` 与目标竖直中心线的最短距离 ≤ `threshold`；退化处理（双点→点距、单点→点到线段、平行→固定 `s=0`）；旧点–点模型仅作回退开关。
  8. **可选骨骼追加校验**（在几何**通过之后**）：`boneName` 非空且目标持怪物骨骼组件 → 不通过则拒绝；**不能替代**几何判定；历史缺失退当前帧骨骼（非硬拒）；未走该分支的目标不携带客户端自报 `boneName` 进结算。
  9. 通过后：`CachedDamageTargets.Add`；构造服务端 `FHitResult`（`ImpactPoint = Location = 消毒值`，`BoneName` **仅骨骼校验通过才写**）；`RecordHitHistory`（重复元素即命中次数）。
  10. 尾部：`bStopDetectPendingLateDamage && CachedDamageTargets>0` → 补停止检测。
- **L4 结算**：结论空或 Driver 无效 → return；`bActivationPending` → `StashPendingProjectileHit` + return（**不结算**）；否则 `SettleProjectileHit` → 成功 → `BroadcastDealDamageEvent`；`bStopWhenDamageSomebody` → 立即停止。`SettleProjectileHit` 不依赖子弹实例，是正常路径与 Pending 回放的**唯一共用结算入口**。
- **入口硬校验**（`ForceValidate`，Reject 与 Report 分开）：目标数组 > 100 → Reject；`ClientHitLocation` 非 finite → Reject；`ClientProjPrevPos` 非 finite 或与命中点距离 > 2000cm → Reject；`ClientRewindTime` 非 finite 或 < 0 → Reject，有限值 > 1.0s → **只 Report 不 Reject**；逐目标 `TargetVisualOffset` 非 finite 或 `> 2000cm` → Reject；逐目标 `ImpactPoint` 非 finite → Reject。**Report 必须在元素校验之后**（提前 return 会跳过后续 cap）。
  源：R0_4 §C.1–§C.5、§E.2–§E.5、U-10..U-20。

### 1.6 Unity 米 + 整毫秒换算表（1 m = 100 cm；UE Z-up → Unity Y-up）

| UE 量 | UE 值 | Unity 目标 | 用途 |
|---|---:|---|---|
| `pm.NetProjectile.SpawnOriginToleranceCm` | 1000 cm | **10.00 m** | 枪口约束（`<=0` 关闭） |
| `MaxTargetBodyRadiusCm` | 500 cm | **5.00 m** | L2 ②b 目标体型余量 |
| `MaxVisualOffsetCm` | 2000 cm | **20.00 m** | `TargetVisualOffset` 硬校验 |
| `P_prev ↔ P_cur` 距离上限 | 2000 cm | **20.00 m** | 入口 Reject（防伪造超长扫描线段） |
| `VerifyHit.Tolerance` | 30 cm | **0.30 m** | `DynamicThreshold` 加项 |
| （历史）`ImpactPoint` 固定 500 cm 整批 Reject | 500 cm | 5.00 m | **已删除**，仅存于历史 |
| `VerifyHit.MaxRewindTime` | 0.5 s | **500 ms** | `safeRewind` 上限 + 帧锚窗口基底 |
| `VerifyHit.TickRateBufferSec` | 0.1 s | **100 ms** | L2/墓碑共用 tick 抖动预算 |
| 帧锚新鲜度附加余量 | 0.1 s | **+100 ms** | ⇒ `maxAnchorAgeMs = 600 + extraDeferMs` |
| `MaxGraveDelay` | 1.0 s | **1000 ms** | `GraveDelay` 上限 |
| 服务器延迟销毁保底 | 0.15 s | **150 ms** | 墓碑下界 |
| 等待 A TTL | 2.0 s | **2000 ms** | 挂起 Spawn（墙钟） |
| 等待 C TTL | 2.0 s | **2000 ms** | 命中结论（惰性） |
| `MaxSavedPositionAge` | 1.0 s | **1000 ms** | 回溯历史保留窗口 |
| `MaxPredictionPing` | 300 ms | **300 ms** | `RewindTime` 上限（`PredictionFudgeFactor=0`） |
| Spawn ForceValidate `PredictionTime` 上限 | 0.5 s | **500 ms** | 入口 Reject |
| RewindTime Report 阈值 | 1.0 s | **1000 ms** | **只 Report** |
| 计数类（无单位） | 5 / 100 / 100 / 128 / 5+128 / 512 / 512 | 同值 | RPC 次数、单批目标、入口目标、C 容量、B 单ID+总量、激活缓存、Snap buffer |

**公式（整毫秒 + 米）**：
```
predictionMs   = clamp(exactPingMs, 0, 300)                  // Standalone = 0
safeRewindMs   = clamp(clientRewindMs, 0, 500)
maxDeviationM  = initialSpeedMps * (2*safeRewindMs + tickBufferMs + extraDeferMs) / 1000
maxStoppedHitM = damageShapeRadiusM + 5.00 + toleranceM      // 0.30 默认
maxAnchorAgeMs = 600 + extraDeferMs
graveMs        = clamp(2*predictionMs + 100, 0, 1000)
tombstoneMs    = max(150, delayDestroyMs, graveMs)
```
服务器追赶用 `predictionMs * 0.5 + extraDeferMs`，客户端上报 `RewindTime` 用 `*1`，墓碑用 `*2`。

---

## 二、高概率推断（依据与置信度）

| # | 推断 | 依据 | 置信度 |
|---|---|---|---|
| I-1 | **旧 Unity 铁律「联网视觉子弹禁用所有 Collider」与假弹契约直接互斥**。R5 的假弹必须启用本地碰撞与候选收集（U-06/§B.1），镜像/直创才关碰撞。实现者若沿用 `Client/Assets/AGENTS.md` §4.3/§9-11 的既有结论，会把假弹碰撞一并关掉，预测路径整体失效 | `Client/Assets/AGENTS.md` §4.3/§9 与 R0_4 §B.1/§B.4/U-06 对读 | 高 |
| I-2 | **墓碑上界（1000 ms）与可靠投递延迟无时间上界不匹配 → 会漏伤（合法命中静默丢失）**。Verify 走 Reliable：NAK 驱动重传、无定时重传、无投递超时；持续丢包下延迟无时间上界（只有「512 条缓冲溢出即断连」这个**包数**上界）。故合法迟到 Verify 可能在墓碑结束后到达，被当作「找不到子弹」丢弃 | D-R0-06/D-R0-07/D-R0-38 + R0_4 §F.2/§F.1/§B.5（两 RPC 同 Channel） | 高（机制）／触发率未知 |
| I-3 | **等待 A 的隐式内存上界＝到达速率 × 2000 ms**。因 ForceValidate 不校验 `SourceBehaviorInstanceID`，攻击者可用伪造 ID 持续追加挂起项；实际只被 Reliable 缓冲与断连策略间接约束。风险等级：中等偏高（同时顶撞 T45「命中验证有界」） | R0_4 §D.1/§A.2/§C.1 + D-R0-07 | 中高 |
| I-4 | **`ProjectileID` 不能当身份键**。它是**每连接**、客户端自增、`int32` 回绕且**可复用**的关联键；权威对象身份是 `NetId`（D-R0-02 永不复用）。若以 `ProjectileID` 作身份，回收/回绕后旧 Verify 会命中新子弹 → 幽灵伤害。T45 的「ID世代复用」应理解为「验证关联键复用不会串到新世代」 | R0_4 §A.2/§1.2 + D-R0-01/02/31 + T45 | 高 |
| I-5 | **`TargetVisualOffset` 在 Unity 无现成对应物**。「客户端看到的位置 − 插值位置」这一量依赖 `FinalizeOverride`；R4 首批 SP 只插值、外推上限 0，未暴露等价偏移量。若 R5 开工时该量仍不可得，L3 只能取 0 → 高大/快速目标会偏向误拒 | R0_4 §E.1/§E.2/U-15 与 `net-r4-prediction-contract.md`「SP 只插值」对读 | 中高 |
| I-6 | **hyld 旧链路与新契约存在三处「同一语义两套口径」**：①帧 vs 毫秒（`frameTime=0.016f` 累加器、`MaxClientAttackAge=8` 帧 vs R5 的整毫秒 TTL）②去重键（`attackId*100000+victimBattleId`、`_predictedBulletAttackIds` vs R0_4 的 `ProjectileID` + `CachedDamageTargets`）③容差（`0.6f` vs R4 的 0.05 m vs R0_4 的 0.30 m）。三者都需要在 R5 给出显式映射/淘汰决定 | `Client/Assets/AGENTS.md` §3.2/§4.2/§7 + `ConstValue.cs` + R4 契约 + R0_4 §I | 高 |
| I-7 | **`AllowedHitTargets` 残留会造成跨命误伤/误拒**（源码已确认未清空，业务后果为推断） | R0_4 §F.4 标注 | 高（源码）／中（业务影响） |
| I-8 | **「对齐 6 项 + 停止态 4 项」（R0 契约 §7）计数偏少**，R0_4 U-07 实际列了 5 项停止态动作。按 4 项实现会漏掉「按 `hideWhenStop` 隐藏 + 关阵营显隐」或「断 UpdatedComponent」之一 | `net-r0-contract.md` §2.6/§7 与 R0_4 §B.2/U-07 对读 | 中高 |
| I-9 | **Unity 侧坐标轴必须换轴**：R0_4 的「竖直中心线 = `RewindLocation ± HalfHeight*Z`」在 Unity（Y-up，R4 `PMVector3` 明确 Y-up）应改为 Y 轴；`quaternion Euler(Towards)` 之类遗留欧拉角误用（`Client/Assets/AGENTS.md` §4.5）不得带进新链路 | R0_4 §E.3 + `Client/Assets/AGENTS.md` §2/§4.5 + R4 契约 | 高 |

---

## 三、无法确定（缺少证据）

| # | 项 | 为什么无法闭环 | 影响 |
|---|---|---|---|
| U-1 | 各子弹资产的 `m_TaskNetRole`、`bSkipTrajectoryValidation`、`bAutoDestroyWhenStop`、`bHideWhenStop`、`bDetectOnStopRequiresDamage`、`bEnableMoveParamsSync` 取值 | 均为资产/EditInstanceOnly 项，R0_4 §K.1 已列「需资产复核」；本次为文档级只读，无资产读取 | 决定两端双发风险与 L2 强度 |
| U-2 | `ExactPing` 实际分布 | 文档口径为「PingBucket 4 s 滑窗平均，上限 300 ms」，无实测分布 | 决定 `RewindTime`、追赶量、墓碑实际长度 |
| U-3 | `RewindTime` 是否叠加 NPP 缓冲 | 当前 UE 配置 `bEnableSimProxyPredictiveInterpolation=True` 使其被旁路；Unity 无 NPP 等价物 | 该合成公式在 Unity 侧需重写，无现成答案 |
| U-4 | UE `MatchTime` 与 R4 `TotalSimTimeMs` 是否等价 | R0_4 回溯用 `GetMatchTimeSeconds`；R4 只提供 `TotalSimTimeMs(double)` | 决定回溯历史用哪个时钟 |
| U-5 | hyld 旧 `ReconciliationPositionThreshold = 0.6f` 的单位 | `ConstValue.cs` 无注释；按 AGENTS.md §2「玩家 Y=1」推断为米，但未确认 | 影响「0.6 m vs 0.05 m vs 0.30 m」三口径的取舍说明 |
| U-6 | M04–M07 已落盘的实际可用 API（`PMPredictionContracts.cs` / PMNet 身份与 RPC 桩） | 不在本次委派的必读范围；本次仅确认 `Client/Assets/Scripts/PMProjectile/` **尚不存在**（`PMNet/`、`PMPrediction/`、`PMMover/`、`PMUnity/` 已存在） | R5 最小接口草案需与实际落地签名对齐，否则重复造轮子 |
| U-7 | 等待 A 超时清理的归属（Tick 驱动 vs 惰性） | R0_4：A 由 Driver Tick 驱动、C 惰性（组件不 Tick）、B 无 TTL。Unity 侧 M09/M10 未定 Tick 所有者 | 影响「谁在什么时候推动 TTL」 |

---

## 四、已检查范围

**读取的文档（全量读完）**
1. `D:/UGit/hyld-master/AGENTS.md`（全文，4 条路由）
2. `Client/Assets/AGENTS.md`（全文，含 §1.1 进程形态、§2 坐标系、§3 输入/命令/发送链路、§4 子弹系统、§5 文件索引、§7 参数表、§8 动态追帧、§9 已修复历史、§12 协议生成）
3. `Docs/plans/net-r0-contract.md`（全文 446 行；重点 §2.6 投射物 D-R0-30..40、§3.7 契约类型、§4 顺序域、§5 复制/生命周期、§7 投射物契约表、§9 待收敛参数、§10 抽查记录）
4. `D:/hyld-refactor-survey/R0_4_projectile.md`：**§0、§1、§A–§F、§I、§J、§K、§L、§M 已读**；**§G（客户端表现边界）、§H（位移校验工具）未读**（任务指定 A–F、I–J；§K–M 因落在同一连续读取区间内一并读到）
5. `Docs/plans/net-architecture-migration.md` 的 §3.9.3（M09 落点与硬边界）、§3.9.4（身份/可靠性/时间/投射物运行时契约）、§5 阶段表 R5 行与 T45 行（仅按 R5/T45 关键词定位，未读全文）
6. `Docs/plans/net-r0-contract.md` 之外的一份额外依赖：`Docs/plans/net-r4-prediction-contract.md`（全文 8.5 KB）——R5 行明确依赖「R4 帧锚契约」，且这是 Unity 米/整毫秒口径的**唯一冻结来源**
7. `Docs/plans/` 目录清单（确认 `net-r5-contract.md` **尚不存在**；同族命名 `_r4_mover_survey.md` / `_r4_prediction_survey.md` 为本报告格式依据）

**读取的源码（仅 2 处、均极小且为边界确认）**
- `Client/Assets/Scripts/ConstValue.cs`（37 行）：确认旧常量 `frameTime=0.016f`、`ReconciliationPositionThreshold=0.6f`、`PredictionHistoryWindowSize=40`、`inputBufferSize=4` 及其**单位未文档化**
- `Client/Assets/Scripts/` 目录清单：确认 `PMNet/`、`PMPrediction/`、`PMMover/`、`PMUnity/` 已存在，**`PMProjectile/` 不存在**（R5 为绿地）

**未做（尊重 explore 有界协议）**：未打开 `D:/UE_Project/ProjectMecury` 的任何 UE 源码；未读 `R0_1/R0_2/R0_3` 附录；未读 `R0_4` 的 §G/§H；未读 `Server/AGENTS.md`；未编译、未修改任何工程文件；未递归委派。
**阻塞**：无。任务指定的必读文档与 R0_4 §A–F/I–J 均可访问且信息自洽，未出现「附录缺关键事实」的情形；上表 U-1..U-7 属**边界外**事实，不构成本次调查的阻塞。

---

## 五、建议下一步

### 5.1 需在 R5 冻结前决策的 5 个契约缺口（按优先级）

| # | 缺口 | 冲突双方 | 建议处置 |
|---|---|---|---|
| **G1** | **无界 Spawn ↔ 有界内存** | 主计划 §3.9.4「三类等待**分别**有容量、TTL 和清理策略」＋D-R0-18「对象数/在途/重组缓冲/FastArray 全部有界」 **对** D-R0-35＋R0_4 §D.1「追加处无独立 cap，不能把 TTL 当容量上限」；§9 亦写「挂起 Spawn 无数量上限」；T45 要求「命中验证有界」 | **二选一并写入契约**：(i) 照抄 UE（A 无 cap），但必须补一份等价有界性论证（`速率 × 2000 ms` + Reliable 512 溢出即断连），并把「与 §3.9.4 的偏离」写进 §2.3 裁量表；(ii) 给 A 加 cap，并把「改变既有语义」显式记账。**同时**把 B 的「无独立 TTL、随 A」与 §9 的「TTL 均为 2.0 s」措辞改准。另建议补 `SourceBehaviorInstanceID` 号段一致性校验（R0_4 已标为信任面） |
| **G2** | **Frame ↔ 时间（四套时钟）** | D-R0-19/20/36（可变 dt、四命名空间隔离、帧锚用单调仿真时间） **对** R0_4 实际混用的四个时间基：墙钟（A 的 `PendingWallTime`）、比赛时间（`SavedPositions` 回溯基准）、Mover 仿真时间（锚新鲜度）、RTT 派生（`GetPredictionTime`）；R0 契约的 `PMTimeStep` 只有 SimTime/Session 两域，**无 MatchTime、无显式墙钟源**；R4 则给了 `TotalSimTimeMs`＋「断线 Freeze + 墙钟 2s」 | 冻结一张**时钟归属表**：锚新鲜度＝`TotalSimTimeMs`（与 R4 一致）；回溯历史＝需在 R4 `TotalSimTimeMs` 与 MatchTime 语义间二选一并说明；三类 TTL＝**墙钟**（D-R0-29：断线时帧号冻结，帧号超时必然失效）；**禁止跨基相减**。另：旧 `MaxClientAttackAge=8` / `MaxAcceptableAttackDelay=8` 帧计数窗口在可变 dt 下语义漂移，建议统一改为整毫秒窗口 |
| **G3** | **停止后迟到验证 ↔ 对象存活/有界内存/ID 世代** | D-R0-38「墓碑期允许迟到 Verify」＋D-R0-04「NetId 不复用」 **对** §2.3「Class Pool 首版可直接销毁」＋T45「ID世代复用」；且墓碑上界 1000 ms 无投递延迟上界支撑（I-2） | ①**墓碑记录与对象解耦**：改为有界 LRU 只存 `{Epoch, ProjectileID, 停止位置, 出生点, CachedDamageTargets, 起止时刻}`，既能覆盖超长迟到又能满足 D-R0-18；②注册表存 `(Epoch, ProjectileID) → NetId`，Verify 解析后**再校验该对象的生命周期世代**；③若坚持对象存活方案，则把「权威弹存活 ≥ tombstoneMs」写成销毁时刻契约 |
| **G4** | **§7 契约表述缺口** | §3.7 三队列是空壳；§7「对齐 6 项 + 停止态 4 项」与 U-07 实际 5 项不符；§7 未写 C 的「合并追加且不刷新时间戳」、「换 Pawn 清空」、B 的「回放无条件先清 pending 标记」「必须保序」、A 的「墙钟 `PendingWallTime`」与「待生成集合」 | 按本报告 §1.2/§1.3 补齐字段与硬语义（这些在 R0_4 §D 已定，不补会在实现期被重新发明）；计数改为 6+5 或明确合并口径 |
| **G5** | **旧链路三处口径必须淘汰** | 视觉子弹「禁用所有 Collider」铁律 vs 假弹需碰撞（I-1）；旧去重键（`attackId*100000+victimBattleId`、`_predictedBulletAttackIds`）vs `ProjectileID`+`CachedDamageTargets`（I-6）；三个「容差」（0.6f / 0.05 m / 0.30 m） | ①契约里同时写清「假弹启用碰撞、镜像禁用碰撞」两条互补约束，并显式声明 §4.3/§9-11 的旧结论在 R5 被收窄为「镜像/直创」适用；②给出 `AttackId ↔ ProjectileID ↔ NetId` 映射与去重层级（同弹 substep 命中同目标只结算一次）；③命名上区分「reconcile 容差（0.05 m）」与「命中几何容差（0.30 m）」 |

### 5.2 最小可分离实现接口建议（对齐 T45 的 6 条验收）

> 落点 `Client/Assets/Scripts/PMProjectile/`（namespace `PMNet.Projectile`，纯 C#、netstandard2.0 + C#7.3，零 UnityEngine 依赖）；数值常量放 `Client/Assets/Scripts/Shared/PMProjectileNumeric.cs`（**整毫秒 int + 米 float**），与 R4 的 `PMMoverNumeric` 同目录。

| 接口 | 最小方法集 | 对应 T45 条款 |
|---|---|---|
| `PMProjectileRegistry` | `TryRegister(uint projectileId, PMProjectileOrigin origin, uint epoch, PMNetId authority, ulong lifecycleGen, out string conflict)` / `TryResolve(uint epoch, uint projectileId, out AuthorityRef)` / `Unregister(...)` | ID 世代复用；DS 直创 |
| `IPMProjectileSpawnPolicy` | `Classify(bool locallyControlled, bool hasAuthority, uint taskNetRole) → { ClientPredicted, ServerDirect, Abort }` | DS 直创 |
| `IPMFakeProjectileSync` | `TryTakeover(uint projectileId, in MirrorState mirror, in FakeState fake) → { Overwrite, SilentStop, NoOp }`（**纯函数**，含幂等标志的入参/出参） | 预测弹接管 |
| `IPMPendingVerifyStash` | `Stash(id, arg)` / `Replay(id, int nowMs, Action<VerifyArg,int> emit)`（**先清标记再保序回放**）/ `Discard(id)`；caps 单 ID 5 / 总 128 | Verify 先于 Spawn 完成 |
| `IPMPendingSpawnQueue` | `Enqueue(entry, int wallClockMs)` / `TryResolveBySource(srcId, int nowMs, out entries, out int extraDeferMs)` / `DiscardBySource` / `PurgeExpired(int nowMs)`；**有界策略抽成 `IPMPendingBoundPolicy`**，使 G1 的两种处置都可配置且可测 | Pending 确认/拒绝 |
| `IPMPendingHitLedger` | `Stash(id, entry, int nowMs)`（合并追加、**不刷新时间戳**）/ `Resolve(srcId, result)` / `PurgeExpired(nowMs)` / `ClearAll()`；cap 128 满丢最旧；TTL 2000 ms | Pending 确认/拒绝 |
| `IPMProjectileTombstone` | `BeginStop(id, stopPosM, spawnOriginM, cachedTargets, int simMs, int graveMs)` / `TryResolve(id)` / `Release(id)`；记录与对象解耦（G3） | 停止后迟到命中 |
| `IPMHitVerificationPipeline` | `VerifyL0Activation` / `VerifyL1Gates` / `VerifyL2Budget` / `VerifyL3PerTarget` / `VerifyL4Settle` 五个**独立纯阶段**（L3 内再拆 `ResolveTargetPosition` / `SanitizeImpactPoint` / `ReplayFilters` / `SegmentDistance`） | 全部（逐层表驱动测试） |
| `IPMLagCompClock` | `NowWallMs` / `NowSimMs` / `RewindMs`（`NowMatchMs` 待 G2 决策）；四处显式取值、**禁止隐式跨基** | 全部（G2） |

**建议的首批实现顺序**（每步可独立验收、不依赖后续步的中间结论）：①`Registry` + `SpawnPolicy` + `Numeric`（纯查表/纯分类，无需网络）②`HitVerificationPipeline` 的 L1/L2（纯算术，可用表驱动 oracle 全覆盖）③`HitVerificationPipeline` 的 L3（依赖位置还原，需先定时钟归属）④`PendingVerifyStash` + `PendingSpawnQueue` + `PendingHitLedger`（需注入墙钟）⑤`Tombstone` + `FakeProjectileSync`（需镜像状态结构）⑥接 M04–M07 真实桩（依赖 U-6 对齐）。

### 5.3 最小补充查询 / 运行时验证

1. **最小补充查询（离线，低成本）**：核对 M04–M07 已落盘公开 API（`PMPrediction/PMPredictionContracts.cs`、`PMNet/` 身份与 RPC 桩）的实际签名，确认 5.2 的接口草案不与之重名/重叠；核对 `Client/Assets/Scripts/Shared/` 现有数值常量的单位口径。
2. **资产复核（若具备 UE 资产读取能力）**：`m_TaskNetRole`（决定双发）、`bSkipTrajectoryValidation`（决定 L2 强度）、`bAutoDestroyWhenStop`/`bHideWhenStop`/`bDetectOnStopRequiresDamage`（决定墓碑与停止表现）。
3. **运行时验证（联网，决定性）**：
   - **V1 墓碑 vs 迟到 Verify**：NetSim（50% 丢包 + 60–150 ms 延迟，旧链路已验证档）下，实测「停止 → Verify 到达」的延迟分布；确认 `tombstoneMs` 是否覆盖 P99。**这是 G3 的唯一可判定实验。**
   - **V2 A 队列有界性**：以伪造 `SourceBehaviorInstanceID` 洪水发送挂起 Spawn，测挂起项数、内存与 CPU 曲线；确认是否触发 Reliable 512 溢出断连（G1 的判定证据）。
   - **V3 时钟一致性**：同一局内对照 `TotalSimTimeMs` 与挂起墙钟的漂移；断线/暂停时确认帧号冻结但墙钟继续（D-R0-29）。
   - **V4 白名单残留**：复现池化复用后 `AllowedHitTargets` 继承上一次生命周期的误伤/误拒（I-7）。

---

*报告结束。全部结论来自上述文档的只读检查；未编译、未修改任何工程文件（本文件为唯一写入）。*
