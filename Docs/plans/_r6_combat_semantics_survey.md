# R6 前置：共享战斗数值 / PMBattleSim / 服务端攻防语义 有界调查

范围：`Client/Assets/Scripts/Shared/BattleNumericConfig.cs`、`Client/Assets/Scripts/Shared/PMBattleSim.cs`、
`Server/Server/BattleController.Bullets.cs`、`Server/Server/BattleController.Network.cs`、
`Server/Server/Battle.cs`（资源初始化 / 胜负直接调用段）。
只读调查，未改任何代码。

## 阅读证据（按委派要求的顺序）

| 顺序 | 文档 / 文件 | 读到的可用结论 |
|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 入口三分：Client/Assets/AGENTS.md、Server/AGENTS.md、net-architecture-migration.md |
| 2 | `Client/Assets/AGENTS.md` | §4 子弹系统/延迟补偿 V2/HP 权威链；§7 参数表（其中「大招能量上限 500」已与代码不符，见下）；§14 新 PMNet R5 投射物接线优先于旧链 |
| 3 | `Server/AGENTS.md` | §3.1 拆分后 7 文件索引与函数行号；§4.3/§4.4/§4.5 帧循环与攻击资源门控；§13 注意（HP 为测试值 1/5） |
| 4 | `Docs/plans/net-r5-network-contract.md`（含尾 C 诊断范围） | C 批只做诊断射击（10m/s、0.1m、1500ms、200ms、8 子步、0.6m 枪口），明确「不称英雄普通攻击/大招/资源校验已完成」；`DrainSettlements` 无 R6 消费者不扣血；ServerFrame 必须 AuthorityServer 域 |

补充直引（用于交叉验证，未越出边界）：`Server/Server/Battle.cs`（资源初始化、帧循环、mana 回复、断线）、
`Client/Assets/HYLD1.0/Scripts/OldScripts/Bullet/s/Bullet/BulletLogic.cs`（`Shoot` 分发 + `SetBehaviorFlags`）、
`Client/Assets/Scripts/Server/Manger/Battle/HYLDBulletManger.cs:155 BuildVisualAttackSpec`（客户端表现侧字段映射）、
`Client/Assets/HYLD1.0/Scripts/OldScripts/HYLDStaticValue.cs`（`最大能量=200`、`playerManaValue=90`）。

---

## 一、已确认（有代码直引的事实）

### 1.1 数值单一事实源的实际内容（`BattleNumericConfig.cs`）

常量：
- `ManaMax = 90`、`ManaPerSegment = 30`、`DefaultAttackManaCost = 30`
- **`SuperEnergyMax = 200`**（不是客户端 AGENTS.md §7 写的 500；客户端 `HYLDStaticValue.cs:380 最大能量 = 200` 与之一致 → 文档 §7 是陈旧条目）
- `FrameTimeSec = 0.016f`、`FrameTimeMs = 16`、`MovementMaxPositionError = 0.6f`、`MaxAcceptableAttackDelay = 8`
- 注意 `Server/Server/Battle.cs:148` 另有一份私有 `MaxAcceptableAttackDelay = 8`（未收敛到共享常量，数值相同）。

API 面（两端共用）：
- `HeroNumeric Get(int heroId)`：越界→`_fallback` 且记入 `UnknownHeroes`，不抛异常（旧 `HeroConfig.GetReloadSeconds` 会抛 `KeyNotFoundException`，已消除）。
- `bool TryGetSuper(int heroId, out SuperNumeric)`：**只有 6 个英雄返回 true**。
- `ResolvedAttack ResolveAttack(int heroId, bool isSuper)`：普通/大招数值的**唯一合并点**；
  `SpawnBulletCount = 普通→BulletCountPerShot`，`大招→BulletCountTotal`；大招的 `-1` 字段回填普通值；
  `IsParabola` 直接取 super 值（不参与 -1 语义）。
- 字段语义纠偏：`ShootWidth` 是**表现宽度/爆炸半径**（值域 0~4），服务端判定半径是 `ServerHitRadius`（全英雄 0.8）。

### 1.2 20 英雄普通攻击数值全表（已确认）

| id | 英雄 | MaxHp | DesignHp | dmg | PerShot | Total | LaunchAngle | ShotInterval | Reload(s) | Parabola | High | ManaCost | 弹速 | 射程 | ShootWidth |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
|0|雪莉|960|4680|80|5|20|30|0.005|1|否|0|30|11|6|0.5|
|1|柯尔特|1180|3640|340|2|6|0|0.1|1|否|0|30|12|8|0.4|
|2|佩佩|840|3240|650|1|1|0|0|1|否|0|30|12|11|0.5|
|3|潘妮|1010|4160|400|1|1|0|0|1|否|0|30|11|6|0.01|
|4|巴利|840|2880|816|1|1|0|0.01|1|**是**|10|30|5|5|2|
|5|公牛|1400|5880|45|10|50|40|0.01|1|否|0|30|8|4|0|
|6|达里尔|960|5760|90|15|30|45|0.1|2|否|0|30|16|6|0|
|7|格尔|900|4420|448|6|6|0|0|1|否|0|30|11|7|3|
|8|布洛克|1120|2730|1155|1|1|0|0|1|否|0|30|10|10|0.5|
|9|爆破麦克|1060|2940|840|1|2|20|0.01|1|**是**|10|30|5|5|0.2|
|10|阿渤|1060|3600|520|1|3|15|0.1|1|否|0|30|10|6|0|
|11|迪克|1120|2200|680|4|4|20|0.01|1|**是**|10|30|5|5|0.4|
|12|贝亚|900|2400|800|1|1|0|0|**9**|否|0|**90**|10|10|0.5|
|13|塔拉|1120|3400|460|3|3|45|0.05|1|否|0|30|10|7|0|
|14|麦克斯|1060|3200|320|1|4|10|0.05|2|否|0|30|14|7|0|
|15|斯派克|840|2400|**0**|1|1|0|0|1|否|0|30|10|10|0.5|
|16|黑鸦|900|2400|320|3|3|30|0.08|2|否|0|30|12|7|0|
|17|里昂|840|4800|680|1|4|10|0.09|1|否|0|30|11|7|0|
|18|帕姆|1340|4800|260|2|9|50|0.15|1|否|**30**|30|14|9|0|
|19|瑞科|840|3250|400|1|5|0|0.1|1|否|0|30|13|10|0.04|

要点：
- **`MaxHp` ≈ 设计值 1/5**（设计值留在 `DesignHp`，降幅各英雄不统一，无法用单一系数还原；`Server/AGENTS.md §13` 与此一致）。
- `帕姆` 出现 `High=30` 但 `IsParabola=false` 的数据异常（服务端忽略 High，客户端 `ParadolaShoot` 才会用到）。
- `斯派克` `BulletDamage = 0` → 服务端子弹**永远打不掉血**、也**永远不给发射者回能**。
- `麦克斯` `NormalAttackManaRecover = 3` 在**全项目零调用**（Server/Client 均无引用）→ 该字段目前是死数据。
- `贝亚` `ReloadSeconds = 9`（其余多数 1，达里尔/麦克斯/黑鸦 2），`ManaCost = 90`（一次攻击吃满整条蓝）。

### 1.3 大招（`_hasSuper` 只有 6 个 true）

| id | 英雄 | dist | width | Total | dmg | angle | 弹速 | PerShot | Interval | Parabola | High |
|---|---|---|---|---|---|---|---|---|---|---|---|
|0|雪莉|6|0.5|40|-1→80|**40**|14|-1→5|-1→0.005|否|-1→0|
|1|柯尔特|12|0.2|12|-1→340|-1→**0**|18|-1→2|-1→0.1|否|-1→0|
|7|格尔|7|4|4|-1→448|-1→**0**|14|**4**|0|否|-1→0|
|12|贝亚|10|0.8|6|**60**|-1→**0**|10|**6**|-1→0|否|-1→0|
|18|帕姆|2|1|1|**300**|0|5|1|0|**是**|-1→30|
|19|瑞科|14|0.2|12|-1→400|-1→**0**|19|-1→1|-1→0.1|否|-1→0|

- 其余 **14 个英雄（2 佩佩、3 潘妮、4 巴利、5 公牛、6 达里尔、8 布洛克、9 爆破麦克、10 阿渤、11 迪克、13 塔拉、14 麦克斯、15 斯派克、16 黑鸦、17 里昂）没有子弹型大招配置**。
- 服务端对它们**双重拒绝**（均在 `Network.cs` / `Bullets.cs` 中）：
  - `BattleController.Network.cs:322` 收到 `AttackType.Super` 且 `TryGetSuper==false` → `RecordAttackAck(..., accepted:false, reject:"unsupported_super")`、**仍在能量扣减之前直接 continue**，且已把 `dic_lastProcessedAttackId` 推进（该 AttackId 不可再补发）。
  - `BattleController.Bullets.cs:24` 生成阶段再兜一层 `missing_super_config` 跳过。

### 1.4 服务端「一次攻击生成多少弹」

`BattleController.Bullets.cs:12 SpawnBulletsFromOperations` → `:70 SpawnServerBullets` → `:137 CreateServerBullet`：
- `int bulletCount = cfg.SpawnBulletCount;` → **普通攻击 = `BulletCountPerShot`，大招 = `BulletCountTotal`**。
- 所有子弹在**同一逻辑帧、同一 `spawnPos`** 一次性生成；`EachShotInterval` 与 `BulletCountTotal`（普通攻击语义）**服务端完全不用**。
- 由此推出**确认的对称性缺陷**：客户端 `HYLDBulletManger.BuildVisualAttackSpec` 把 `BulletCount = num.BulletCountTotal`、`BulletCountByEachTime = num.BulletCountPerShot` 交给 `BulletLogic`，即**一次点击播放 Total 发**（按 EachShotInterval 分轮）；服务端一次攻击只结算 PerShot 发。
  - 例：雪莉服务端最多 5×80=400，客户端画面 20 发；柯尔特 680 vs 2040；帕姆 520 vs 2340。
- `LaunchAngle == 0 且 SpawnBulletCount > 1` 的英雄（柯尔特 2 发、格尔 6 发）在服务端**方向完全相同且起点相同**（`PMBattleSim.SpreadDirection` 对 `bulletCount>1` 时 `step = 0/(n-1) = 0`），即多颗弹**完全重叠、同帧同时命中**；客户端 `StraightShoot` 用的是**垂直于弹道的平移排布**。这是两端几何不一致点。
- 单发（`SpawnBulletCount <= 1`）由 `PMBattleSim.SpreadDirection` **原样返回基准方向**（`Bullets.cs` 注释与 `PMBattleSim` 注释均明确这是修正过的历史 bug）。

### 1.5 服务端命中 / HP / 击杀 / 胜负

`BattleController.Bullets.cs:163 CheckBulletCollision`：
- 跳过条件：自己、队友、断线者（`disconnectedBattlePlayerIds`）、`playerIsDead==true`。
- 判定半径取**发射者**英雄的 `ServerHitRadius`（全英雄 0.8），不是受击者体型。
- 命中即 `playerHp[victim] -= bullet.Damage`（`PMBattleSim.IsHit` 球-点距离 ≤ 半径，每颗弹每帧**至多命中一个目标**后 return）。
- 击杀：`hp <= 0 && !playerIsDead` → `playerIsDead=true`、`isKill=true`、写 `_killerBattlePlayerId/_killerTeamId`、`hasAnyPlayerDied=true`、`dic_playerGameOver[victim]=true`。
- **HP 无任何回复路径**（服务端全文件无 `playerHp[x] +=`、无复活）；死亡即永久，`IsDead` 通过 `PackPlayerStates` 每帧下发。

HP 初始化（`Battle.cs:410`）：`playerHp[bpId] = BattleNumericConfig.Get(hero).MaxHp`；`playerIsDead=false`。

胜负（**旧实现的实际语义**）：
- `BattleLoop`（`Battle.cs:505`）：`hasAnyPlayerDied` 一为真 → **下一轮立即 `HandleBattleEnd()`**，不再推进帧。即**首杀即终局**，不是「团灭/计分」。
- `BattleController.Network.cs:898 SendFinishBattle`：`pack.Str = _killerTeamId > 0 ? _killerTeamId.ToString() : "1"`；`winnerTeamId` = **最后一击者的队伍**。
- **旧坑（确认）**：若因**断线**结束（`Battle.cs:238 HandlePlayerDisconnect` 置 `hasAnyPlayerDied=true`、`allClientsConfirmedGameOver=true`，而 `_killerTeamId` 仍为 0）→ 胜负被**硬编码为 "1"**，与战况无关。
- 客户端消费：`BattleManger.cs:699 HYLDStaticValue.玩家输了吗 = (winnerTeamId != BattleData.Instance.teamID)`；`BattleManger.cs:694-700`。

### 1.6 蓝量 / 大招能量（实际公式）

初始化（`Battle.cs:412-414`）：`playerMana = 90`、`playerManaRegenTimerMs = 0`、`playerSuperEnergy = 0`。

普通攻击（`BattleController.Network.cs:341-352`）：
- `manaCost = BattleNumericConfig.Get(hero).NormalAttackManaCost`；
- 不足 → `reject:"mana"` 且**推进 `dic_lastProcessedAttackId`**（该 id 不能重试）；
- 充足 → `currentMana -= manaCost`、`playerMana[bpId] = currentMana`、**`playerManaRegenTimerMs[bpId] = 0f`**（攻击打断回蓝进度）。

大招（`BattleController.Network.cs:320-338`）：
- 门槛是 `currentSuperEnergy < BattleNumericConfig.SuperEnergyMax(200)` → `reject:"super_energy"`；即**必须满 200 才能放**（不是「≥ 某个消耗值」）；
- 通过 → `currentSuperEnergy = 0`，**不动蓝量**；随后走同一条 `ServerAttack` 入队路径。

回蓝（`Battle.cs:658 RegeneratePlayerMana`，每帧调用）：
```
if (mana >= 90) { timer = 0; continue; }              // 满蓝时计时器清零
reloadMs = ReloadSeconds * 1000
timer += frameIntervalMs(16)
while (timer >= reloadMs && mana < 90) { timer -= reloadMs; mana = min(90, mana + 30); }
```
- 即「每 `ReloadSeconds` 回 30」，最多 3 段回满；`贝亚` 每 9s 才回 30（回满 27s）。
- **不检查 `playerIsDead`** → 已死亡玩家仍在后台回蓝（旧坑，可复现但影响视觉/结算口径）。

大招能量获取（`BulletController.Bullets.cs:207-210` → `PMBattleSim.RechargeSuperEnergy`）：
```
gain = min(200, energy + damage / 2)   // damage/2 为整数除法（向下取整）
```
- 只有**命中**才回能（`CheckBulletCollision` 内），普通攻击与大招子弹都回给发射者；
- 例：雪莉 80 伤害 → +40；布洛克 1155 → 一次命中即封顶 200；**斯派克 dmg=0 → 永远 0 能量**（永远放不出大招，虽然其 `hasSuper=false` 本来也拒绝）。

### 1.7 `PMBattleSim` 的实际 API 面（R6 可直接复用）

纯静态、零依赖（无 UnityEngine / Protobuf），只收标量：
`ZeroEpsilon(1e-6f)`、
`TryGetMoveDirection(moveX,moveY,sign,out dirX,out dirZ)`、
`TryAdvancePosition(posX,posZ,moveX,moveY,sign,moveSpeed,frameTimeSec,frameCount,out,out)`、
`TryGetVelocity(...)`、
`TryGetAimDirection(towardX,towardY,sign,out dirX,out dirZ)`（含 proto 换轴 + 取反 + 归一化）、
`SpreadDirection(baseDirX,baseDirZ,totalAngleDeg,bulletCount,index,out,out)`（`±total/2` 首尾包含，`count<=1` 原样返回）、
`BulletStepDistance(speed,frameTimeSec)`、`IsBulletExpired(traveled,max)`、
`IsHit(bx,by,bz,tx,ty,tz,radius)`、`RechargeSuperEnergy(current,damage,max)`。

**其中没有任何**：重力/抛物线、子弹生命周期编排、AoE/爆炸、穿透、弹射、DoT、CC、位移、队伍/目标筛选策略。

### 1.8 服务端忽略抛物线的确认

`grep -rn "IsParabola|Parabola|\.High" Server/Server/*.cs` → **零命中**。
即 `ResolvedAttack.IsParabola / High` 只被客户端表现层（`HYLDBulletManger.BuildVisualAttackSpec` → `BulletLogic.IsParadola/high` → `ParadolaShoot`）使用；**服务端把巴利/爆破麦克/迪克/帕姆（大招）全部按直线弹模拟**。这是「不能把全部大招当直线」的反面证据：现有旧实现已经这么做了。

### 1.9 帧锚相关（仅记录，不展开）

- `ServerBullet.ClientFrameId` = 客户端 `AttackMoveFrame`（clamp 到 `frameid`）；`SpawnServerBullets` 另写 `atk.SpawnServerFrame = frameid`（服务端权威帧）。
- `SimulateBulletCatchUp` 的 `HitEvent.HitFrameId` 恒为**当前权威处理帧**，不是历史物理命中帧（代码注释已自述）。
- 追帧上界 `catchUpToFrame = frameid - 1`，避免与 `TickServerBullets` 同帧重复推进。

---

## 二、高概率推断（依据 + 置信度）

1. **服务端「一次攻击 = 一轮 PerShot」是设计缺陷而非刻意平衡**（置信度 中高）。
   依据：`ResolvedAttack.SpawnBulletCount` 的注释自称「与旧服务端行为一致」，但客户端一次点击播放 Total；两端伤害口径相差 2~6 倍。R6 若直接继承会把「雪莉贴脸 400」当权威。

2. **14 个 `hasSuper=false` 的英雄在真实玩法里是非子弹大招（位移/召唤/增益/区域）**（置信度 中；代码只证明"无子弹覆写"，具体形态来自玩法常识，未在本范围文件内取证）。
   服务端现状对它们是 `unsupported_super`，等于**这些英雄没有大招**。

3. **`LaunchAngle==0 且 count>1`（柯尔特/格尔）在服务端同帧重叠命中**是可直接观察到的非预期行为（置信度 高，由 `SpreadDirection` 公式直接推出，未运行验证）。

4. **断线导致胜方固定为 "1"** 是已知旧坑且现有代码仍然如此（置信度 高，`SendFinishBattle` 直引）。

5. **`斯派克` 全英雄 `BulletDamage=0`** 并非笔误而是「爆裂伤害由客户端/其他机制承担」的遗留（置信度 中低，属推断，无本范围证据）。

---

## 三、无法确定（缺少证据）

1. **R5 新投射物链（PMProjectile/PMR5ProjectileDriver）中是否已有 HP/资源/胜负接缝**：本批只读了 Shared 与服务端旧链；`net-r5-network-contract.md` 明确 C 批 `DrainSettlements` 只是诊断计数、**无 R6 消费者**，所以 R6 的接入面需要在 R6 代码阶段自行定义（本次未读 PMProjectile 核心源码）。
2. **旧客户端单机伤害路径（`shell.OnTriggerEnter` → `HYLDStaticValue.RedBP/BlueBP/playerBloodValue`）与联网链的最终取舍**：AGENTS.md 说单机模式「已决定全面剥离」，但剥离进度与残留引用未在本批范围内核查。
3. **`斯派克`/`潘妮` 的 `ExplodeOnHit`、`瑞科` 的 `Reflect`、`塔拉` 的 `Penetrate`、`黑鸦` 的 `Poison`、`贝亚` 的 `BeeCharge` 是否有服务端等价机制**：代码上服务端无任何对应字段；但这些行为的**策划预期效果**（范围/时长/伤害）无配置来源，无法确定。
4. **`DesignHp` 何时恢复、恢复后 MaxHp 口径**：注释只说「待对象池/复制优化后决定」，无时间点与验收标准。
5. **`EachShotInterval` 是否应进入权威结算**：现阶段服务端完全不用；是否需要在 R6 把「一轮多发」拆成多个权威事件，缺少设计结论。

---

## 四、可直接迁移 / 不支持 的边界清单（R6 用）

### 4.1 可在当前服务端语义下直接迁移为「直线弹」的普通攻击

**严格直线（`LaunchAngle==0`）**：佩佩(2)、潘妮(3)、布洛克(8)、贝亚(12)、瑞科(19)、斯派克(15，但 dmg=0)。
**直线但多弹同向重叠（需注意，不是客户端那种平行排布）**：柯尔特(1，2 发)、格尔(7，6 发)。
**扇形（服务端已支持，`LaunchAngle>0`）**：雪莉(0)、公牛(5)、达里尔(6)、阿渤(10)、塔拉(13)、黑鸦(16)、里昂(17)、麦克斯(14，10°)、帕姆(18，50°)、爆破麦克(9，20°)、迪克(11，20°)。

> 说明：`里昂/麦克斯/阿渤/爆破麦克` 的 `LaunchAngle` 非 0 但 `SpawnBulletCount==1` → 走「原样返回基准方向」分支，实际仍是单发直线，**不产生扇形**。

### 4.2 不能在 R6 当直线弹处理的边界

| 边界 | 英雄/大类 | 依据 |
|---|---|---|
| **抛物线** | 巴利(4)、爆破麦克(9)、迪克(11) 普通攻击；帕姆(18) 大招 | `IsParabola=true`；服务端当前忽略，客户端 `ParadolaShoot` 才走落点 |
| **无子弹大招（服务端一律 `unsupported_super` 拒绝）** | 佩佩、潘妮、巴利、公牛、达里尔、布洛克、爆破麦克、阿渤、迪克、塔拉、麦克斯、斯派克、黑鸦、里昂（14 个） | `_hasSuper[..]=false` + `Network.cs:322`/`Bullets.cs:24` |
| **位移/冲刺/跳跃型大招** | 公牛、达里尔、布洛克、佩佩、黑鸦、里昂（推断，见 §二.2） | 同上一行；无位移同步实现 |
| **召唤/增益/区域型大招** | 潘妮（炮台）、阿渤（图腾）、塔拉（引力）、麦克斯（加速）、斯派克（减速场）（推断） | 同上 |
| **特殊效果（客户端 behavior 标志，服务端全无）** | 普通：塔拉穿透、瑞科反射、黑鸦毒、贝亚蜜蜂充能、潘妮爆裂、斯派克爆裂；大招：雪莉 穿透+破墙+CC0.6s、柯尔特 穿透+破墙、格尔 穿透+CC0.5s、贝亚 减速、瑞科 穿透+反射 | `BulletLogic.SetBehaviorFlags`；服务端 `ServerBullet` 仅 9 个标量字段，无状态机 |
| **贝亚大招** | 客户端走 `Shoot()` 的特例 `蜜蜂大招()` 协程 | `BulletLogic.cs:66`；即其表现不是直线路径 |

### 4.3 目前**是**直线、但含特殊语义需显式决策的

- **雪莉大招**：`LaunchAngle=40` 的 40 发扇形，服务端一次全生成（同帧 40 颗，位置相同）。
- **柯尔特/瑞科大招**：`LaunchAngle` 继承 0，`SpawnBulletCount = Total = 12` → **12 颗完全重叠的直线弹**，最大单帧伤害 12×340=4080 / 12×400=4800。
- **格尔大招**：4 颗重叠 + 客户端有穿透/CC。
- **帕姆大招**：`IsParabola=true` → 必须走落点结算，不能当直线。

---

## 五、建议下一步（最小补充查询 / 运行时验证）

1. **确认 R6 的「一次攻击 = 几发」口径**（最高优先级）：现状服务端 PerShot、客户端 Total。建议在 R6 契约里冻结为「每次上行攻击 = 客户端一次点击 = Total 发」或明确保留 PerShot，并同步 `ResolvedAttack` 注释与 `HYLDBulletManger.BuildVisualAttackSpec`。
2. **补查 PMProjectile 核心（`Client/Assets/Scripts/PMProjectile/`、`PMR5ProjectileDriver`）现有 settlement 载荷字段**，确认是否已携带 `damage/attackId/victimId`，以决定 R6 是复用还是新增字段（本次未读，属真实缺口）。
3. **运行时验证 1（5 分钟）**：单人开枪打空子弹，读服务端 `[HP]`/`[HitEvent]` 日志，核对 雪莉 5 发 vs 20 发、柯尔特 680 vs 2040 的实际伤害口径。
4. **运行时验证 2**：断线一局，确认 `SendFinishBattle` 日志 `winnerTeamId=1` 与战况无关（验证 §1.5 旧坑）。
5. **确认 `斯派克 dmg=0` 与 `麦克斯 ManaRecover=3` 是生产预期还是数据待修**，再决定是否在 R6 保留。
6. **确认 14 个 `hasSuper=false` 英雄在 R6 的产品口径**（无大招 / 后续补 / 永久非子弹），否则 R6 会默认把它们做成「没有大招」。
