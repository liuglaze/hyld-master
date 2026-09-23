# R6-A1 纯核心交付：PMCombatWeaponPlanner / PMCombatSession

范围：`net-r6-combat-contract.md` 的「A1 纯核心」；D-R6-01 / D-R6-02 / D-R6-03。
本批**只做 planner 与 session 两个纯核心**，不接网络 host、不替换诊断策略、不动 Shared / 冻结 contracts / 主计划。
未启动 Unity、未提交、未做任何 SVN/git 写操作。

---

## 1. 交付文件与编码

| 文件 | 说明 | 编码 |
|---|---|---|
| `Client/Assets/Scripts/PMCombat/PMCombatWeaponPlanner.cs` | 攻击计划生成（两端共用） | UTF-8 **BOM** + CRLF |
| `Client/Assets/Scripts/PMCombat/PMCombatWeaponPlanner.cs.meta` | 唯一 GUID `f20a6ce573a74a0eacddd8cd2960d5e8` | ASCII + LF |
| `Client/Assets/Scripts/PMCombat/PMCombatSession.cs` | 权威会话（账本/资源/授权/结算/胜负） | UTF-8 **BOM** + CRLF |
| `Client/Assets/Scripts/PMCombat/PMCombatSession.cs.meta` | 唯一 GUID `544a981569a9432bb58acb057e2a5db9` | ASCII + LF |
| `Tools/PMCombatCoreCheck/PMCombatCoreCheck.csproj` | 语言面门禁（netstandard2.0 + C# 7.3，库） | UTF-8 无 BOM + LF（沿用 Tools 既有惯例） |
| `Tools/PMCombatCoreTest/PMCombatCoreTest.csproj` | 运行验收（net8.0，Exe，LangVersion 7.3） | UTF-8 无 BOM + LF（同上） |
| `Tools/PMCombatCoreTest/Program.cs` | 1257 条断言 | UTF-8 **BOM** + CRLF |
| `Docs/plans/_r6_core_report.md` | 本报告 | UTF-8 **BOM** + CRLF |

两个 GUID 在全仓库 `.meta` 中各只出现 1 次（已 `grep -rl` 复核），未复制既有资产的 GUID。
`.csproj` 保持 Tools 目录既有惯例（无 BOM + LF），与全部兄弟工程一致；中文明文文件（`.cs` / `.md`）为 BOM + CRLF。

**依赖面（`<Compile Include>` 逐文件给定，无通配符）**

- 本组：`PMCombatContracts.cs`（冻结，只读）+ `PMCombatWeaponPlanner.cs` + `PMCombatSession.cs`
- Shared（只读）：`BattleNumericConfig.cs`、`PMBattleSim.cs`
- PMProjectile 全核心：`PMProjectileContracts.cs`、`PMProjectileCodec.cs`（`PMProjectileSpawnIntent`）、
  `PMProjectileLifecycle.cs`、`PMProjectilePending.cs`、`PMProjectileHistory.cs`、`PMProjectileValidator.cs`、
  `PMProjectileCoordinator.cs`（`PMProjectileSettlement` / `PMProjectileValidatedHit`）
- 基础：`PMMover/PMMoverState.cs`（`PMVector3`）、`PMNet/{PMNetIdentity,PMNetRole,PMNetReader,PMNetWriter,PMWireType}.cs`

刻意**不**包含 `PMProjectileDiagnosticConfig.cs` / `PMProjectileCandidateCollector.cs`：它们是 R5-C 的
诊断策略与 DS 候选采集层（R6-B 会用正式授权策略替换诊断策略），不属于 A1 依赖。

零 `UnityEngine`、零 R3、零 `Google.Protobuf`。

---

## 2. PMCombatWeaponPlanner：精确 API

```csharp
namespace PMNet.Combat
{
    public static class PMCombatWeaponPlanner
    {
        public const int MaxPlanLifetimeMs = 2500;

        public static bool TryBuild(int heroId, bool isSuper, float aimX, float aimZ,
            out PMCombatAttackPlan plan, out PMCombatRejectReason reason);

        public static bool IsSameDirection(float ax, float az, float bx, float bz);
        public static bool TryNormalizeDirection(float x, float z, out float dirX, out float dirZ);
        public static bool IsFinite(float value);
    }
}
```

`TryBuild` 是**纯函数**：只读共享配置、只产出新对象，不扣资源、不记账、不认识玩家、不判断枪口/命中。
失败时 `plan == null`，`reason` 为下表之一；成功时 `reason == None` 且 `plan` 是新对象（`Directions`/`Spec` 均为新建）。

### 2.1 计划字段口径（与契约逐条对应）

| 字段 | 取值 | 依据 |
|---|---|---|
| `HeroId` | 入参（已校验 `0..19`） | D-R6-01「hero 必须 0..19，不能走共享 Get 兜底」 |
| `IsSuper` | 入参 | — |
| `Directions` | `N = ResolvedAttack.SpawnBulletCount` 条世界 XZ 单位方向（Y=0） | D-R6-01：normal=PerShot / super=Total |
| `Damage` | `ResolvedAttack.BulletDamage`，**0 原样保留** | 斯派克 `BulletDamage=0` 是配置事实，不擅自填值 |
| `ManaCost` | 普通 = `NormalAttackManaCost`；大招 = 0 | 大招只消耗能量（旧服语义） |
| `FireIntervalMs` | `max(100, ceil(EachShotInterval*1000))` | 契约「下限 100ms 安全预算」，且**明确是单次攻击间隔**，不是「一轮弹幕总时长」 |
| `Spec.SpeedMps` | `ResolvedAttack.BulletSpeed` | — |
| `Spec.LifetimeMs` | `ceil(ShootDistance / BulletSpeed * 1000)`，限 `[1, 2500]` | 记录 TTL 5000ms 必须覆盖 pending + 墓碑 |
| `Spec.RadiusM` | `PMCombatLimits.ProjectileRadiusM = 0.1` | R5 胶囊 0.4 + 容差 0.3 + 0.1 = 旧 0.8 侧向阈值；**不复用 `ShootWidth`** |
| `Spec.StopOnHit` | `true` | 第一批直线弹不穿透 |
| `Spec.HideOnStop` | `true`；`DelayDestroyMs=0`；`SkipFlyingTrajectoryValidation=false` | 显式给出，不依赖 `PMProjectileSpec` 默认值漂移 |

方向一律用 `PMBattleSim.SpreadDirection`（**复用**权威圆弧公式，不另写一份）：
`±LaunchAngle/2` 首尾包含；`N<=1` **或** `LaunchAngle==0` 时原样返回基准方向。
因此「柯尔特（0°，2 发）/ 格尔（0°，6 发）多弹完全同向重叠」的既有服务端语义被原样保留，
旧客户端那种「垂直于弹道平移排布」的表现编排**没有复活**（对应 D-R6-01 的取舍）。

### 2.2 拒绝原因表（planner）

| reason | 触发条件 |
|---|---|
| `InvalidHero` | `heroId < 0 \|\| heroId >= PMHeroId.Count(20)` |
| `InvalidAim` | `aimX/aimZ` 非 finite，或 `sqrt(x²+z²) <= PMBattleSim.ZeroEpsilon` |
| `UnsupportedAttack` | `isSuper && !TryGetSuper(heroId)`（14 个无子弹型大招的英雄）；或 `ResolvedAttack.IsParabola`（巴利/爆破麦克/迪克普通攻击、帕姆大招） |
| `InvalidConfig` | 弹数 `<=0` 或 `>64`；speed/dist `<=0` 或非 finite；`LaunchAngle<0` 或非 finite；`EachShotInterval<0` 或非 finite；`BulletDamage<0`；寿命 `>2500`；`SpreadDirection` 返回退化零方向；`ManaCost<0` |

**不降级**：抛物线、缺大招配置一律拒绝，绝不退回「用普通攻击值或兜底值继续开枪」。

### 2.3 20 英雄支持矩阵（测试实测口径）

- 普通攻击：**17 支持 / 3 拒绝**（拒绝 = 巴利 4、爆破麦克 9、迪克 11，均为 `IsParabola`）。
- 大招：**5 支持 / 15 拒绝**（拒绝 = 14 个 `TryGetSuper==false` + 帕姆 18 的 `IsParabola`）；
  支持者为雪莉 0（40 发，30°）、柯尔特 1（12）、格尔 7（4）、贝亚 12（6）、瑞科 19（12）。
- 计划寿命上界实测 **1000ms**（布洛克/贝亚/斯派克 `dist==speed`），远低于 2500ms 预算与 5000ms 记录 TTL。
- `FireIntervalMs` 实测：雪莉/柯尔特/…/瑞科 = 100ms，帕姆 = 150ms。

### 2.4 float32 精度处理（一处显式决策）

`EachShotInterval` 是 float32：`0.15f = 0.150000006`、`0.1f = 0.100000001`。
直接 `ceil(interval*1000)` 会得到 **151ms / 101ms**，与配置语义（150/100）不符。
实现先按 `1e-4 ms` 归整再取 `ceil`，因此帕姆得 150、柯尔特得 100；测试用**显式字面值**锁住这两点。
坏配置（NaN/负/0）不进这条路径（提前 `InvalidConfig`）。

---

## 3. PMCombatSession：精确 API 与记账

```csharp
namespace PMNet.Combat
{
    public sealed class PMCombatSession
    {
        public PMCombatSession(uint epoch);              // epoch==0 → ArgumentOutOfRangeException（沿 R5 核心先例）

        public bool AddPlayer(uint netId, int uid, int teamId, int heroId);
        public bool StartMatch(int expectedPlayerCount);
        public bool Disconnect(uint netId);              // 保留水位与死亡真值
        public bool Tick(double wallNow);                // 回蓝

        public PMCombatAttackDecision RequestAttack(uint ownerNetId, uint activationId, bool isSuper,
            float aimX, float aimZ, double wallNow);

        public bool TryAuthorizeProjectile(uint ownerNetId, PMProjectileSpawnIntent intent, double wallNow,
            out PMProjectileSpec spec, out PMCombatRejectReason reason);

        public bool ApplySettlement(PMProjectileSettlement settlement, double wallNow);
        public bool ApplySettlement(PMProjectileSettlement settlement, double wallNow,
            out PMCombatRejectReason reason);

        public PMCombatPlayerSnapshot GetPlayer(uint netId);        // 深拷贝；未知 → null
        public PMCombatPlayerSnapshot[] CapturePlayers();           // 登记顺序；每个都是深拷贝

        public uint Epoch { get; }
        public bool Started { get; }
        public bool Ended { get; }
        public PMCombatMatchOutcome Outcome { get; }                // 值拷贝
        public int PlayerCount { get; }
        public int IdConflictCount { get; }                         // 同 ID 内容冲突次数
        public int AuthorizedProjectileCount { get; }                // 当前已授权 key 数（≤2048）
        public int AttackRecordCount { get; }                       // 当前攻击记录数（≤512）
    }
}
```

`RequestAttack` 返回的 decision 是**深拷贝**（调用方改它不影响账本）；
`TryAuthorizeProjectile` 返回的 `spec` 同样是深拷贝（来自冻结计划）。
**不借出内部可变引用**：`Hits` 等入参数组只读、不留存；快照/计划全部 clone。

### 3.1 身份与名册

- `AddPlayer`：`netId!=0 && uid>0 && teamId>0 && heroId∈[0,20)`；uid 不重复、netId 不重复、
  最多 6 人、**最多 2 支不同队伍**、`StartMatch` 之后不得再登记。**不要求 team 只能是 1/2**（用真实名册 TeamId）。
- `StartMatch(expected)`：必须与名册人数**完全一致**，且 `1 <= expected <= 6`；重复调用返回 false。
  下界取 1 而不是 2，是为了让契约里「无唯一队伍 → winner 0」这条路径可达（见 §3.5）。
- **未 `StartMatch` 之前攻击一律 `NotStarted` 且不记账、不写水位**（不给开战前上行把账本塞满的机会）。

### 3.2 攻击账本：ID 空间、幂等、水位、有限资源

- **ID 空间 = 单 owner + 单 epoch 单调递增**（与 R5「activation 单 owner 单调不重用」一致）：
  账本按 `(ownerNetId, activationId)` 索引。**不同 owner 使用同一个 activationId 是两笔合法攻击，不是冲突**。
  水位也是 **per owner**。
- **幂等**：同一 `(owner, activationId)` 再次到达 → 返回**原始** decision 的拷贝并置 `Duplicate=true`：
  不双扣资源、不刷新记录 TTL、**不推翻**已 Accepted 的结论。请求内容不一致（isSuper 不同，或方向超出
  1° 容差）只把 `IdConflictCount` +1，结论仍以首次为准。
  幂等检查在「比赛状态检查」**之前**，所以终局后重传已批准 ID 仍返回原结论而不是 `MatchEnded`。
- **水位**：每个 owner 记录见过的最大 activationId（含被拒绝的请求）。低于水位且记录已被 TTL/容量回收
  → `StaleId`，**绝不会被当成新请求复活**（`id 非 0 不回绕`）。
- **有限资源**：账本 session 上限 **512**（含被拒绝结论 —— 幂等需要它）；已授权 key 上限 **2048**；
  每笔攻击方向槽 = 计划弹数（≤64）。满了就拒，不无界增长。
- **容量回收**：只在需要容量时按 TTL(5000ms) 回收过期记录，并**连带撤销它们的已授权 key**
  （被回收记录的结算会得到 `UnknownAttack`/`Expired`，不会复活）。

### 3.3 RequestAttack 的判定顺序（拒绝一律无副作用）

```
1  时钟合法（finite && >=0 && >= _lastWallNow）        → InvalidClock
2  activationId != 0                                   → InvalidId
3  幂等：账本命中 → 返回原结论（Duplicate=true）        （不做后续任何检查、不改任何状态）
4  玩家存在                                            → UnknownPlayer
5  Started / !Ended                                    → NotStarted / MatchEnded（此二者不记账、不写水位）
6  Connected / !Dead                                   → Disconnected / Dead
7  activationId > owner 水位                           → StaleId
8  容量（先按 TTL 回收）                                → Capacity
9  planner.TryBuild                                    → InvalidHero/InvalidAim/UnsupportedAttack/InvalidConfig
10 cooldown: wallNow - LastAttack < FireIntervalMs      → Cooldown
11 资源：normal Mana >= ManaCost / super SuperEnergy>=200 → InsufficientMana / InsufficientEnergy
12 扣费（normal: Mana-=cost 且 ReloadAccumMs=0；super: SuperEnergy=0，蓝不变）
13 记账 + 写水位 + 返回 Accepted
```

**先校验后扣费**：第 8~11 步的任何拒绝都发生在第 12 步之前，因此拒绝没有资源副作用。
第 4 步之前（`NotStarted`/`MatchEnded`）与时钟非法同样**不写账本**。

### 3.4 TryAuthorizeProjectile

```
1  时钟合法                                   → InvalidClock
2  intent != null / owner!=0 / activationId!=0 / projectileId!=0 → InvalidId
3  key.OwnerNetId==owner && key.Epoch==session.Epoch && Origin==ClientPredicted
                                              → IdentityMismatch（含 ServerDirect 上行）
4  重复 key（先于一切状态判断）：原样返回既有 spec，不占槽；
   同一 key 绑定到另一 activation → IdentityMismatch
5  方向 normalize（finite 且非零）              → InvalidAim
6  Started / !Ended                           → NotStarted / MatchEnded
7  玩家存在 / Connected / !Dead                → UnknownPlayer / Disconnected / Dead
8  activation 记录存在（否则按水位判定）         → StaleId（低于水位）/ UnknownAttack（高于水位）
9  记录 owner 一致 / 已 Accepted                → IdentityMismatch / UnknownAttack
10 记录未过期（wallNow - WallTime <= 5000ms）    → Expired
11 全局 key 数 < 2048                          → ProjectileLimit
12 本攻击已用槽 < N                            → ProjectileLimit
13 找第一个「未占用且与入参方向夹角 <= 1°」的槽   → DirectionMismatch
14 占槽 + 记录 key 元数据（Damage/ActivationId/Owner/Epoch/IsSuper + spec 快照）
15 spec = 冻结计划 spec 的深拷贝，返回 true
```

- **同方向多槽逐个取**：`LaunchAngle==0` 的多弹（柯尔特/格尔）方向完全相同，因此会按索引 0,1,… 依次占用，
  而不是一颗弹占满全部槽；重复 key 则完全不占槽。
- **本方法不知道枪口位置**：`intent.Position`/`Yaw`/`PredictionMs` 一律不参与判定，也不替代 R5 的
  枪口/历史/胶囊/L0–L4 检查。上行 payload 里本来就没有 spec/damage 字段，其值也一律不信。
- **弹只有被批准过才有 slot**：被拒绝的 activation 或从未见过的高位 ID 一律 `UnknownAttack`。
- **资源不退款**：已授权但 R5 真实生成失败时不回滚账本（一次攻击已授权成本，不能靠回滚制造重复资源）。

### 3.5 ApplySettlement：整批结构先验 + 每 key-target 一次 + 首杀单次锁

```
1  时钟合法                                   → InvalidClock
2  整批结构先验（任一不合格 → 整批拒绝，绝不部分扣血）：Hits!=null 且非空、
   HitCount==Hits.Length、长度<=100、无 targetNetId==0、同批无重复 target   → InvalidId
3  ids/identity：ActivationId!=0、ProjectileId!=0、key.Epoch==epoch、
   key.Owner==owner、Origin==ClientPredicted                              → InvalidId / IdentityMismatch
4  Started / !Ended                                                     → NotStarted / MatchEnded
5  授权 key 元数据存在且与 settlement 的 activation/owner/epoch 一致        → UnknownAttack / IdentityMismatch
6  记录仍 Accepted 且未过期（TTL）                                        → UnknownAttack / Expired
7  owner 玩家存在                                                        → UnknownPlayer
8  逐目标（顺序应用，互不影响）：已结算过 → 跳过；未知/self/友军/未连接/已 dead → 跳过；
   否则标记 (key,target) 已结算 → HP = clamp(Hp - damage, [0, MaxHp])
9  damage>0 时：attacker.SuperEnergy = PMBattleSim.RechargeSuperEnergy(...,damage,200)
10 damage>0 且目标 HP 归 0 → Dead=true 且「若未终局」锁 Outcome(OutcomeId++, WinnerTeamId,
   FirstKill)。damage==0 永远不首杀。
```

关键取舍（与契约逐条对应）：

- **伤害只来自冻结计划**：`damage` 取授权时写入的元数据，之后配置变化不影响已授权弹。
  `PMProjectileSettlement` 本身也没有伤害字段，结构上排除了「信客户端 damage」。
- **整批 vs 单目标**：结构问题（空批/长度不符/目标 0/同批重复）→ **整批拒绝**，合法目标也不先扣血；
  语义不合格（未知/self/友军/已 dead）→ **只跳过该目标**，不丢弃同批其它合法目标（否则会静默丢伤害）。
- **每 key-target 一次**：`(key, target)` 记在授权 key 元数据内，随 key 一起被回收。
- **首杀即终局**（旧服语义，调查 §1.5）：只锁一次 `OutcomeId/WinnerTeamId/Reason`；
  终局后所有 `RequestAttack` / `TryAuthorizeProjectile` / `ApplySettlement` 一律拒绝**且无任何变化**。
- **实际 R5 生产者的结构性事实**（已核对源码，作为本检查的相容性依据）：
  `PMProjectileCoordinator.cs:1842-1855` 是**唯一**的 settlement 生产者，`s.HitCount = accepted.Count`
  与 `s.Hits = accepted.ToArray()` 同源；`accepted` 已过滤 `TargetNetId==0` 并对每 key 做
  `TryAddHitTarget` 去重（`SettleHitSets` 也汇流到 `SettleHits`）。因此上面的「整批结构拒绝」分支
  不会被真实 R5 结论触发，它们是**边界防御**（核心不无条件信任调用方），而不是对现有输出的收紧。

### 3.6 资源与回蓝

- 初值：`Mana = ManaMax(90)`、`SuperEnergy = 0`、`Hp = MaxHp = BattleNumericConfig.Get(hero).MaxHp`。
- 普通攻击：扣 `NormalAttackManaCost`（贝亚 90、其余 30）并把回蓝进度**清零**（打断）。
- 回蓝（`Tick`）：每 `ReloadSeconds` 回 `ManaPerSegment(30)`，封顶 90；**最多结算 3 段**，
  时钟跳变再大也不会退化成巨大 while（`steps` 有上界，且满蓝时进度清零）。
- **不回蓝**：未 `StartMatch`、已终局、已死亡、已断线。
- 大招：门槛 `SuperEnergy >= 200`，通过后清 0，**蓝量不变**。
- 有效伤害回能：`damage/2`（整数除法，向下取整），封顶 200；多次命中会重复调用但被 clamp 封顶。
  斯派克 `damage=0` → 永远 0 能量、永远不首杀。
- HP 无任何回复路径（与旧服一致）。

### 3.7 时钟

`Tick` / `RequestAttack` / `TryAuthorizeProjectile` / `ApplySettlement` 统一使用同一根 double 墙钟：
`NaN` / `±Inf` / 负数 / **倒退**一律 `InvalidClock` 且**不修改任何状态**（时钟水位也不推进）。
非递减（相等）允许。**宿主必须只从一处单调时钟喂入**（B 阶段建议同一个 Stopwatch）。

### 3.8 终局与断线

- `Disconnect`：保留水位与死亡真值；`StartMatch` 之后若在线的**不同队伍恰好 1 支** → 该队 `Forfeit` 获胜；
  若**一支都不剩** → winner **0**（`Draw`）—— 这正是契约「无唯一队伍则 winner 0、不硬编码 1」的可达路径；
  仍有 2 支在线 → 不终局。未 `StartMatch` 一律不判终局。
- 首杀锁定的结论**不会被后续断线改写**（`EndMatch` 只写一次）。
- 计分口径：`MaxTeams=2` 让「剩余队伍是否唯一」总有确定答案。

---

## 4. 测试与证据

### 4.1 运行结果（本机实测，本次构建产物）

```
dotnet build Tools/PMCombatCoreCheck/PMCombatCoreCheck.csproj -c Release   → 0 警告 0 错误
dotnet build Tools/PMCombatCoreTest/PMCombatCoreTest.csproj  -c Release   → 0 警告 0 错误
dotnet Tools/PMCombatCoreTest/bin/Release/net8.0/PMCombatCoreTest.dll     → exit 0，通过 1257 / 失败 0
python Tools/check_cs_braces.py <两份新实现>                              → PASS
  （planner 322 行 {}=30/30 ()=118/118；session 1156 行 {}=152/152 ()=236/236）
```

分节断言数：A 783 / B 35 / C 49 / D 33 / E 91 / F 111 / G 106 / H 49 = **1257**。

### 4.2 独立 oracle（不是拿实现自比）

- 20 英雄数值表（弹数/伤害/速度/射程/扇形角/间隔/蓝耗/抛物线/Reload）**照调查报告 §1.2/§1.3 逐值誊写**
  成测试常量，再在测试内用显式算术独立推导寿命 `ceil(dist/speed*1000)` 与间隔 `max(100,ceil(i*1000))`；
  另有若干**字面值**硬校验（布洛克 1000ms、佩佩 917ms、瑞科大招 737ms、帕姆 150ms、柯尔特 100ms、雪莉 546ms）。
- 扇形几何用**角度差**判定（-15/-7.5/0/+7.5/+15 度、±0.05° 容差），不是「和实现算出的数比」。
- 负向样本是真边界值：容量 512/513、TTL 5000/5001、方向 0.5°/1.5°/3.75°/11.25°、
  时钟 NaN/±Inf/-1/倒退、同批重复目标、终局后写入、断线后重复 ID、单人局 winner 0。
- 回蓝/能量用**手算序列**：`0→(999ms)0→(1ms)30→(1000ms)60→(1000ms)90→(5000ms)90`；
  贝亚 `0→(8999)0→(1)30→(9000)60`；布洛克 1155 伤害 → 能量一次性封顶 200。

### 4.3 变异测试（证明门禁有牙齿，4/4 被捕获，已全部还原）

| 临时变异 | 被捕获的失败断言 |
|---|---|
| 结算不再过滤队友（友军也吃伤害） | 3 条：`友军不受伤`、`友军不回能`、`再回能 40 → 80` |
| Forfeit 胜方硬编码为 1 | 2 条：`胜方 = 剩余队伍 2`、`胜方 = 2` |
| 去掉 per-owner 水位检查 | 2 条：`乱序旧 ID（5 < 水位 20）→ StaleId`、`被回收的旧 ID（2）仍被水位拦住` |
| planner 普通攻击改用 `BulletCountTotal` | 16 条（弹数/方向/槽位相关） |

变异后已用备份逐字还原，两份实现文件 MD5 与变异前完全一致
（`PMCombatSession.cs = 76919e6b2bfea39bf28bf262dba79409`、
`PMCombatWeaponPlanner.cs = b3a8af7d5d8f8179c58d532de31cb65c`），并在还原后重新 build(0/0)+run(1257/0)。

### 4.4 覆盖矩阵（对应验收表）

| 验收 | 覆盖点 |
|---|---|
| T6A1 | 20 英雄 normal/super 支持矩阵；弹数/伤害/速度/射程/寿命/扇形/间隔与共享配置逐值一致；单发不旋转；零角同向不平移；抛物线/无大招配置拒绝；非法 hero/aim；damage 0 保留；深 clone |
| T6A2 | 扣蓝/cooldown/逐拍回蓝/攻击打断/封顶；能量不足不扣蓝、满 200 清 0、回能 damage/2 封顶；同 ID 幂等不双扣、冲突不推翻、TTL 不刷新、旧 ID 不复活、乱序 StaleId、满表 512/513、TTL 回收后可再用、时钟非法/倒退无副作用；方向槽逐个占用/1° 容差内外/重复 key 不占槽/N 上限/多 owner 独立/未知与被拒 activation/ServerDirect 拒绝/spec 冻结 |
| T6A3 | 名册与 StartMatch 门；去重（每 key-target 一次）；未知/self/友军/已 dead 无写；整批结构先验；HP clamp；damage 0 不首杀；首杀单次锁；终局后攻击与结算无变化；未 start 不早终局；Forfeit 剩余唯一队伍；无唯一队伍 winner 0；winner 非固定 1；断线保留水位与重复幂等 |

---

## 5. 未接网络 / 诚实边界

1. **没有接任何网络 host**：R6-B（`PMR6CombatDriver`、可靠 RPC、`ServerCombatAttackV1` / 结果 ACK）、
   A2 声明、C 宿主接线都不在本批；本批**不替换** DS 的 `DiagnosticProjectileAuthorityPolicy`。
2. **不产生真实投射物**：测试里的 `PMProjectileSpawnIntent` / `PMProjectileSettlement` 是直接构造的
   真实类型值，不经过 R5 codec 字节链，也不接 Unity/PhysX。**不能声称「已能联机开枪」**。
3. **不启动 Unity、不改资产、不提交、不做 SVN/git 写操作**；未修改 `Shared/**`、`PMCombatContracts.cs`、主计划。
4. `InvalidConfig` 在当前冻结数值表下**不可达**（最大寿命 1000ms << 2500ms 预算；所有字段合法），
   它是 fail-closed 的防御分支，测试通过「全 26 种支持组合都在预算内」间接覆盖该上界。
5. **全局 2048 key 上限**在单局内实际不可达（按 5 槽/次攻击需要约 410 次获批攻击且时钟不越过 5s TTL），
   属有界防御；测试用常量断言 + **每攻击额度**（柯尔特 2 槽 / 雪莉 5 槽）实测其行为。
6. **ServerDirect 结算不在 A1 范围**：`ApplySettlement` 只接受被授权过的 `ClientPredicted` key；
   DS 自身产生的 `ServerDirect`（activation 0）路径留待 B 阶段明确后再开。
7. `NotStarted` / `MatchEnded` 的拒绝**不写账本**（有意）：既满足「结束后所有 Attack 无变化」，
   也避免开战前上行把 512 槽位塞满。
8. 本对象**无锁**、单线程宿主所有（与 PMProjectile 核心一致）；宿主必须在同一条泵线程调用。

---

## 6. 给 B 阶段的消费顺序建议

1. 先 `AddPlayer`（可信名册：真实 uid/team/hero）→ 全名册接入后 `StartMatch(count)`；此前不得开门。
2. 客户端 `TryAttack`：先本地 `PMCombatWeaponPlanner.TryBuild` 得到同一份计划（预测弹），
   再上行 `ServerCombatAttackV1`；DS 侧 `RequestAttack` 得到冻结 decision 后回 `ClientCombatAttackResultV1`。
3. 每颗真实弹：用同 `activationId`、递增 `projectileId` 调 `TryAuthorizeProjectile` 拿 `spec`，
   交给 R5 `TryFire`；Rejected 必须通过 R5 的真实撤销路径回收该 activation 的假弹（不能只改 UI）。
4. 每泵顺序：**先**战斗命令队列（`RequestAttack`/授权）**再** `R5.Pump`；`DrainSettlements`
   唯一消费者是 `ApplySettlement`，且先于/独立于表现层。
5. `GetPlayer`/`CapturePlayers` 用于复制纯字段（A2 声明）；死亡/终局后立即停攻与停推进。
6. 所有入口共用**同一个**单调墙钟。

---

## 7. 剩余问题（不在 A1 承诺内）

- 特殊技能/爆炸/弹射/穿透/位移/道具/完整 UI 仍未实现，R6 整体未完成（T46/T47 继续 PENDING_USER）。
- 14 个英雄没有子弹型大招配置 → 本批口径是「明确拒绝」，产品口径（无大招 / 后续补 / 永久非子弹）待定。
- `DesignHp` 恢复、`ShootWidth` 表现宽度与判定半径的最终收敛仍按主计划欠账。
- R6-B 需要决定 ServerDirect（DS 自产弹）是否也走 session 结算，以及全局 2048 key 上限是否需要按 owner 细分。
