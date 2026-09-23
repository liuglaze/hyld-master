# R6-A1 独立对抗审查与返工报告（PMCombatSession / PMCombatWeaponPlanner）

> 任务类型：**comprehensive**（独立对抗 review + 兼容性返工；API 签名已给并行 driver 使用，只允许兼容性修复，不改共享 Contracts）
> 必读输入（按委派顺序，已全文阅读）：`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`
> → `Docs/plans/net-r6-combat-contract.md` → `Docs/plans/_r6_core_report.md`
> → `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`。
> 生产代码只读/写入范围：`Client/Assets/Scripts/PMCombat/PMCombatSession.cs`、`PMCombatWeaponPlanner.cs`、
> `Tools/PMCombatCoreTest/Program.cs`、本文件。
> 直接输入核对：`PMProjectileSettlement`（`PMProjectileCoordinator.cs:414` 定义、`:1853-1855` 唯一生产点）、
> `PMProjectileSpawnIntent`（`PMProjectileCodec.cs:88`）、`BattleNumericConfig`/`PMBattleSim`（Shared，只读）。
> **未启动 Unity；未提交（git/svn 均无写入）；未递归委派；未改主计划、未改共享 Contracts / Shared / 其它文件。**

**结论（一句话）**：A1 两份实现的主体记账是成立的（1257 条旧断言全部保留并通过），
本次独立审查**修掉 2 个真缺陷**（planner 整数域溢出导致 fail-closed 失效；`ApplySettlement`
在批次自身打出终局后仍继续写后续目标），另把 **10 项真实但「by design / 契约交给宿主」的边界**
钉成可执行断言并逐条点名（其中 R6C-3 重复 key 回显 / R6C-6 全局 512 记录表 / 雪莉大招角度
三项与 `_r6_core_report.md` 的自述口径存在冲突）。
断言计数 **1257 → 1500**（A–H 原样 1257，新增 I 段 243），build 0 警告 0 错误，运行 0 失败。

---

## 0. 验证命令与结果（本机实测）

```bash
# 语言面门禁（netstandard2.0 + C# 7.3，零 UnityEngine）：planner + session + 冻结契约 + PMProjectile 核心
dotnet build Tools/PMCombatCoreCheck/PMCombatCoreCheck.csproj -c Release   # 0 警告 / 0 错误
# 运行验收（net8.0，真实源码，无 mock）
dotnet build Tools/PMCombatCoreTest/PMCombatCoreTest.csproj  -c Release   # 0 警告 / 0 错误
dotnet Tools/PMCombatCoreTest/bin/Release/net8.0/PMCombatCoreTest.dll     # 1500 通过 / 0 失败，exit 0
python Tools/check_cs_braces.py <三份 .cs>                                 # BALANCED，深度非负、末尾归零，PASS
```

| 命令 | 结果 |
|---|---|
| `PMCombatCoreCheck`（netstandard2.0 + C#7.3 编译门禁） | **0 警告 / 0 错误** |
| `PMCombatCoreTest` 构建 | **0 警告 / 0 错误** |
| `PMCombatCoreTest` 运行 | **通过 1500 / 失败 0**，退出码 0 |
| `python Tools/check_cs_braces.py`（Session / Planner / Program） | BALANCED / PASS |
| 编码自检（三份 `.cs`） | 全部 `BOM=True` + `CRLF`（无 lone LF/CR） |

分段断言（本次实测）：A 783 / B 35 / C 49 / D 33 / E 91 / F 111 / G 106 / H 49 / **I 243**。
A–H 的 1257 条**原断言一字未改**且全部通过；I 是本次新增的对抗段。

改动后文件指纹（供对账）：

| 文件 | MD5 | BOM/换行 | 字节 |
|---|---|---|---|
| `PMCombatSession.cs` | `863d26a4d19d5792b0e558bfcead5a92` | BOM + CRLF | 47764 |
| `PMCombatWeaponPlanner.cs` | `40ff623c93d3eef18e6918682da2ecf2` | BOM + CRLF | 17122 |
| `Tools/PMCombatCoreTest/Program.cs` | `2643668a24807be6d42837d8ce19e837` | BOM + CRLF | 110806 |

`PMCombatContracts.cs` 与其 `.meta`、两份实现 `.meta` 的 mtime **未变**（未被本次改动触碰）。

---

## 1. 实际审查范围（诚实口径）

「审 PMCombatSession / Planner 及 R5Settlement 直接输入」= 以下**真实被消费的类型面**，不是旧 UE 全仓：

- `PMCombatSession` / `PMCombatWeaponPlanner` 全文（1163 行 / 347 行）。
- `PMCombatContracts.cs`（冻结，只读）、`PMMoverState.cs`（`PMVector3`）。
- `BattleNumericConfig`（20 英雄表 + `ResolveAttack` 的 normal=PerShot / super=Total + `-1` 继承语义）、
  `PMBattleSim.SpreadDirection` / `RechargeSuperEnergy` / `ZeroEpsilon`。
- `PMProjectileSettlement`（struct 定义 + **唯一**生产点 `SettleHits` 的 `HitCount`/`Hits` 同源构造）、
  `PMProjectileValidatedHit`、`PMProjectileSpawnIntent`（六个字段、无 spec/damage）、
  `PMProjectileLimits`（`MaxTargets=100`、`PendingTtlMs=2000`、`HitToleranceM=0.3`）、
  `PMProjectileKey.IsValid`。

未读取（也不属于 A1 依赖）：`PMProjectileDiagnosticConfig` / `PMProjectileCandidateCollector`（R5-C 诊断层）、
R6-B 的 `PMR6CombatDriver`、Unity/PhysX 路径。因此本报告的结论**不能**外推到「已能联机开枪」。

---

## 2. 真实缺陷（本次已修，均为兼容性修复：不改签名、不改共享 Contracts）

### R6C-1（真缺陷，fail-open，当前数值表下不可达）planner 用「先转 int 再钳位」实现 fail-closed，整数域溢出会把坏配置变成合法 1ms 计划

**位置**：`PMCombatWeaponPlanner.cs` 寿命/间隔段（现 `:184-209`）。

**缺陷**：`int lifetimeMs = (int)Math.Ceiling(distance / speed * 1000.0);`
C# 把**超出 `int` 范围**的 `double` 转 `int` 的结果是「未指定」；x64/RyuJIT 实际得到 `int.MinValue`。
于是当 `distance / speed * 1000` 巨大（例如配置误填 `ShootDistance=1e12, BulletSpeed=1`，或 `BulletSpeed` 取到
denormal 导致商溢出为 `Inf`）时：

1. `(int)Math.Ceiling(huge)` → `int.MinValue`（或某些运行时行为下的其它值）；
2. `if (lifetimeMs < 1) lifetimeMs = 1;` 把 `int.MinValue` **钳成 1ms**；
3. 修改前的 `if (lifetimeMs > MaxPlanLifetimeMs) → InvalidConfig` **永远不会命中**。

净效果：一个「寿命远超预算」的坏配置**不是被拒**，而是被静默降级成一张「合法」的 1ms 计划。
这与本文件头写的第 3 条硬边界（「fail closed，不偷偷降级」）以及报告 §5 第 4 条
（「`InvalidConfig` 是 fail-closed 的防御分支」）**直接矛盾**——防御分支只在当前冻结表上成立就不是防御。
`EachShotInterval` 有同构问题：`(int)Math.Ceiling(huge)` → `int.MinValue` → 被「下限 100ms」改写成 100ms，
把「几乎不能再攻击」静默变成「随时能攻击」。

**修法（已落地，`PMCombatWeaponPlanner.cs:186-209` + `:328-331`）**：把判定搬到 `double` 域，
新增私有 `IsFiniteDouble`：

```csharp
double lifetimeMsRaw = (double)distance / (double)speed * 1000.0;
if (!IsFiniteDouble(lifetimeMsRaw) || lifetimeMsRaw > (double)MaxPlanLifetimeMs)
{
    reason = PMCombatRejectReason.InvalidConfig;
    return false;
}
int lifetimeMs = (int)Math.Ceiling(lifetimeMsRaw);   // 到这里必有 0 <= raw <= 2500，转换安全
if (lifetimeMs < 1) { lifetimeMs = 1; }

double intervalMsRaw = ExpectedIntervalMs(eachShotInterval);
if (!IsFiniteDouble(intervalMsRaw) || intervalMsRaw > (double)int.MaxValue)
{
    intervalMsRaw = (double)int.MaxValue;            // 上界钳位，绝不溢出成 100ms
}
int fireIntervalMs = (int)Math.Ceiling(intervalMsRaw);
if (fireIntervalMs < PMCombatLimits.MinFireIntervalMs) { fireIntervalMs = PMCombatLimits.MinFireIntervalMs; }
```

**行为影响**：对当前冻结数值表**逐位不变**（寿命 1..1000ms，间隔 100/150ms；A 段 783 条断言原样通过）。

**测试覆盖（诚实）**：**不可由生产路径触发**。`TryBuild` 只从冻结 `BattleNumericConfig` 读数值，
本任务不允许改 Shared 表，因此**没有**加入运行断言；该修复只由「A 段全部 26 个支持组合仍在预算内」
（`InvalidConfig` 分支本身不可达）间接守着。这一点在 §6 明确列为「不可达/未直接覆盖」。

### R6C-2（真缺陷，终局后仍写入）`ApplySettlement` 整批里某目标打出首杀后，同批后续目标仍在掉血/被置 Dead

**位置**：`PMCombatSession.cs` 结算目标循环（现 `:842-900`；修改点是 `:891-898`）。

**缺陷**：`_outcome.Ended` 只在 `ApplySettlement` **入口**检查一次。若一条 settlement 的 `Hits` 同时含两个
可致死目标，循环在处理第 1 个目标时 `EndMatch(...)` 锁死胜负，然后**继续**处理第 2 个目标：
`target.Hp` 被扣到 0、`target.Dead = true`。也就是说「首杀即终局」之后，同一次调用的后半段仍在改变
战斗状态，违反契约 A1「首杀锁定 `MatchEnded/WinnerTeamId/OutcomeId` 单次，不重复结算」与报告 §3.5
自述的「终局后所有 `RequestAttack` / `TryAuthorizeProjectile` / `ApplySettlement` 一律拒绝且无任何变化」，
也违反验收表 T6A3「终局后写入无变化」。

**修法（已落地，`PMCombatSession.cs:891-898`）**：在 `EndMatch` 之后 `break`，同批后续目标不再写入；
结构校验仍全部发生在任何写入之前，所以退出的是「已确定的终局」而不是「半提交」。

**行为影响边界**：只在「**同一条 settlement 自身**打出终局且该批还有后续可写目标」这一种情况改变行为；
胜负/`OutcomeId`/`Reason` 完全不变，只有同批后续目标的 HP/Dead 不再被改动。
落点与 PMProjectile 事实一致：`StopOnHit=true`（planner 固定）时一颗弹命中即停，真实 R5 生产点
（`PMProjectileCoordinator.SettleHits`）的 `accepted` 也按 key 去重，所以「一条 settlement 多致死目标」
在当前链路里本身不出现；此修复是**让边界与不变式自洽**，不是对现有输出的收紧。

**测试覆盖**：I.6（`Program.cs:1258-1259`）——先期手工 oracle 让 `[105,106]` 同批吃 1155 致死批，
断言 105 → HP 0/Dead/Ended/winner=1/OutcomeId=1，而 106 → **HP 仍 840、不 Dead**。

**变异证据**：临时删掉该 `break` 后 → 汇总 `1498 通过 / 2 失败`，失败项恰为
`I.6 同批第 2 目标不再写入（终局后无变化）（期望 840，实际 0）` 与 `I.6 同批第 2 目标不因同批后续命中被置 Dead`；
还原后两处恢复通过（md5 与变异前一致）。证明该断言有牙齿。

---

## 3. 已确认的边界语义（**by design / 契约交给宿主**；本次不改代码，但逐条点名并在 I 段钉死）

以下不是「改错」的对象（其中多项被 `_r6_core_report.md` 自述为有意取舍，且与**并行 driver 的既有期望**耦合），
所以本次**只报告 + 加断言固化**，把风险与必须由 B 阶段宿主/驱动补齐的动作写清楚。

### R6C-3 `TryAuthorizeProjectile` 的「重复 key 回显」会绕过 `Ended` / `Dead` / `Disconnected` / `Expired`

**事实**：重复 key 分支（`PMCombatSession.cs:594-609`）位于 `_started`/`Ended`/玩家存在/`Connected`/`Dead`/
记录 TTL/槽位/全局 key 上限**之前**。因此已授权 key 在任何状态变化后仍然原样回显 `true` + spec；
而**新** key 会得到 `MatchEnded` / `Expired` / `Disconnected`。
`RequestAttack` 的同 ID 幂等（`:427` 起）同样在比赛状态检查之前，终局后重传已批准 ID 返回原结论。

**为什么保留**：契约 A1 明确「重复 key 回既有授权但不能再次占槽」「任何 `Rejected` 不得影响既有批准攻击」
「同 ID 冲突不能把已 `Confirmed` 结果翻 `Rejected`」——把它改成 `MatchEnded` 等于**把既有授权翻成拒绝**，
正是契约禁止的方向；且并行 driver 的 `HandleDuplicateUplinkSpawn` 依赖这条回显。

**真实后果（必须由 B 阶段驱动兜住）**：回显本身不占槽、不加授权记录、不改资源，**但**宿主若据此再次
`R5.TryFire`，就会在**终局后**或**记录 TTL 之后**真的生成一颗弹；它的结算会被 session 判
`MatchEnded` / `Expired` 而**静默丢弃**（「不可达伤害」：客户端看到弹、HP 不动）。
契约 B / 报告 §6 已要求宿主「战斗结束禁止所有新攻击与推进」，这正是那条要求的**真实必要性来源**。
**建议**：driver 在 `TryAuthorizeProjectile` 前先判 `session.Ended` 与 owner `Connected/Dead`，
不要把「回显 `true`」当作「可以继续发射」。

断言固化：I.9（终局后重复 key 回显 `true`、新 key `MatchEnded`，授权数不增）、
I.9/dupTtl（TTL 过期后重复 key 回显 `true`、新 key `Expired`）、I.12（断线者重复 key `true`、新 key `Disconnected`）。

### R6C-4 共享时钟水位会被「合法时钟但被拒」的请求推进 → 更旧的合法事件被判 `InvalidClock`

**事实**：`AcceptClock`（`:990`）在 `RequestAttack` / `TryAuthorizeProjectile` / `ApplySettlement` / `Tick`
四个入口的**第一行**执行，只要时钟有限、非负、非递减就 `_lastWallNow = wallNow`，**与该请求后续是否被拒无关**。
因此一次「时钟合法但结果被拒」的调用（例：`UnknownPlayer`）会把全局水位推高，使**随后**以更旧时间戳
到达的合法结算被判 `InvalidClock` 并整条丢弃。

**为什么保留**：契约 A1「Tick 单调墙钟，非法/倒退不改」要求拒绝倒退；水位是**全局共享**（跨 owner），
这正是「所有入口共用**同一根**单调墙钟」的强制性来源。把「拒绝时不推进水位」反而会允许重放旧时间戳。

**真实后果**：宿主若把「入队时刻 / R5 产生时刻」而不是「调用时刻」传给 session（例如结算传
`settlement.WallTimeMs`），只要此前处理过一条时钟更新的攻击，这条结算就会静默丢失。契约 B 已写
「宿主显式传单调时钟」，但**报告未点明「共享」这一点**。
**建议**：B 阶段四个入口都传同一处 `Stopwatch`/单调钟的**当前**读数（不是捕获到消息上的时间），
并在监控里统计 `InvalidClock`（它应当恒为 0；非 0 就是宿主时钟用错）。

断言固化：I.15（`UnknownPlayer` 合法时钟请求把水位推到 `t+2000` 后，`t+100` 的合法结算 → `InvalidClock`
且零写入；追平到 `t+2000` 的同一结算被正常受理并扣血 80）。

### R6C-5 `Capacity` 拒绝既不记账、也不推进水位 → 同一 activationId 可在回收+资源恢复后被当成全新攻击接受

**事实**：`EnsureCapacity` 失败时（`:983-986`）直接返回 `Capacity`，既**不** `RecordRejection`、也**不**写
`HighWaterActivationId`；与之相对，`Cooldown` / `InsufficientMana` / `InsufficientEnergy` / planner 拒绝
都会写记录并推进水位（`RecordRejection`，`:913`）。于是同一个 ID 在 TTL 回收后可以「复活」为一笔新攻击。

**判断**：**不构成双扣**（Capacity 时没有扣任何资源，之后只扣一次），也是 UDP 重传语义下可接受的「重试」；
但它与契约 A1「记录消失后旧 ID 不会被当新请求」的**字面**表述有出入，且与其它拒绝分支口径不一致。
报告 §3.3 的顺序表把这件事隐含在「第 8 步容量」里，**未明确说明「Capacity 分支不占水位」**。

**建议**：B 阶段若要求 activationId 全局单次使用，应选择「Capacity 也记一条拒绝记录（占水位）」或让
「被拒 ID 集合」独立记账；当前实现是「Capacity 不占水位」。**属于 A1 的明确取舍，本次不改**
（改它会与 E 段 512 满表压力的既有断言口径冲突）。

断言固化：I.7（填满 512 后第 513 个新 ID → `Capacity`、账本仍 512、蓝不变；`+5001ms` 后**同一个 513**
被接受并只扣一次蓝 → 30）。

### R6C-6 全局 512 记录表可被**单个 owner** 占满，在 ≤TTL（5000ms）窗口内影响**其他人**的 `RequestAttack`

**事实**：`_attacks` 是**会话级**字典（`MaxAttacks=512`），`RequestAttack` 的容量检查在 planner/资源之前，
所以任何 owner 只要在 5s 内送出 512 个**递增 ID**（哪怕全是 `Cooldown`/非法方向的拒绝，也会各占一格），
就能把表填满，使其他 owner 的合法新攻击在回收前得到 `Capacity`。回收只在**下一次** `RequestAttack` 触发
（`PurgeExpired` 仅被 `EnsureCapacity` 调用），因此最坏阻塞窗口约 5s。

**为什么保留**：这是契约「记录容量 session 512（含被拒绝结论）」的**直接**后果，且 E 段既有断言
**特意**用单个 owner 填到 512 并断言 `AttackRecordCount==512 / 第 513 个 → Capacity`——即该行为是被冻结的。
报告 §7 只把「全局 2048 key 上限是否按 owner 细分」列为欠账，**未提** 512 记录表的同类问题。

**建议**：B 阶段若要抗单点刷表，应引入「每 owner 记录配额」并同步调整 E 段断言（属跨批次口径变更，
必须主 Agent 决策，不在本次兼容性修复权限内）。

断言固化：I.7 的前半段（单 owner 填满 512）。

### R6C-7 `StartMatch` 只看名册人数、不看连接态；`StartMatch` 本身**不**判 forfeit

**事实**：`StartMatch(expected)` 只要求 `expected == _players.Count`（1..6），不要求所有玩家 `Connected`；
终局判定 `EvaluateForfeit`（`:1107`）只从 `Disconnect` 调用，所以「开战时只剩一支在线队伍」不会立即终局，
要等到**再一次** `Disconnect` 才判。

**为什么保留**：这不是遗漏而是**被旧断言钉死的语义**——H 段用单人局 `StartMatch(1)` + 断线。
断言 `winner == 0 / Draw`；若把 `EvaluateForfeit()` 挪进 `StartMatch`，单人局会在开战瞬间就因「剩余唯一队伍」
判该队获胜（winner=3），**直接打破旧 1257 断言**。契约「不得在全部到齐前因只一个已连接队早判终局」
也支持「开战门与终局判定解耦」。

**真实后果**：宿主必须自己在「全名册接入后」调 `StartMatch`（契约 B 已要求）；若宿主在有人未连上时就
`StartMatch`，session 不会替你拦。这是**契约分配给宿主的职责**。

断言固化：I.16（开战前断掉整支 team2 → 仍 `!Ended`；`StartMatch(4)` 成功且仍 `!Ended`；直到开战后
再断一名 team1 才 `Ended`、winner=1）。

### R6C-8 `PMCombatRejectReason.Dead` 的两个分支（`RequestAttack` / `TryAuthorizeProjectile`）**不可达**

**事实**：HP 归 0 与首杀终局在**同一次结算**里同时发生（`:891-898`），所以「玩家 `Dead` 但比赛未 `Ended`」
这一状态**不存在**；`RequestAttack`/`TryAuthorizeProjectile` 都在 `Dead` 之前先判 `Ended` → `MatchEnded`。
结算里 `if (!target.Connected || target.Dead) continue;` 的 `Dead` 半边同理（Dead ⇒ Ended ⇒ 结算入口
`MatchEnded`）。

**判断**：这是**防御性不可达**分支（不是 bug），但意味着 `Dead` 这个拒绝原因在单测里**无法通过生产路径产生**，
报告 §4.4 却把「已 dead 无写」计入 T6A3 覆盖——属于**覆盖口径略宽松**。

断言固化：I.14（击杀后同时 `Dead==true` 且 `Ended==true`；死者自己的新攻击得到 `MatchEnded`，
证明 `Dead` 被 `Ended` 遮蔽）。

### R6C-9 结算不校验 attacker 的存活/连接；不校验 `AuthorityNetId`

**事实**：`ApplySettlement` 只校验 key 的 epoch/owner/projectileId/origin、`ActivationId`、授权记录存在且
`Accepted`、记录未过 TTL、attacker **在名册里**（`:803-840`），**不**检查 attacker 是否 `Connected`/`Dead`，
也**不**看 `settlement.AuthorityNetId`。

**判断**：契约 A1 把「attacker 和 target 当前存活/敌队/`TargetStreamVersion`」的复核明确交给 **host 先复核**，
所以 session 侧不是遗漏。物理上「已出膛的弹在发射者死后仍生效」也是正确语义。
断言固化：I.12（断线者的已授权弹仍正常扣血/回能）。

`AuthorityNetId` 在 `PMProjectileSettlement` 里存在但 session 完全不用；契约未要求 session 校验它。
若 B 阶段要让 session 校验「结算只能来自本局权威 DS」，需要**新增**构造参数/绑定 API → 属**接口变更**，
按委派要求**只报告不实施**。

### R6C-10 记录 / 授权 key 只在 `RequestAttack` 触发回收（有界，非「无限记忆」）

**事实**：`PurgeExpired`（`:948`）只被 `EnsureCapacity`（`:983`）调用，而 `EnsureCapacity` 只在 `RequestAttack` 里调用。
因此若一段时间没有新攻击请求，过期记录与已授权 key 会**驻留**在内存里直到下一次攻击请求。
上界是硬的：`_attacks ≤ 512`、`_authorized ≤ 2048`（`TryAuthorizeProjectile` 的全局上限），
所以是**有界留存**，不是无界增长。

**判断**：符合契约「满了就拒，不无界增长」，但要注意两点：
(a) 全局 2048 key 上限可能被**未回收**的过期 key 暂时占住（进而让新授权得到 `ProjectileLimit`），
直到下一次攻击触发回收；(b) 记录被回收时 `RemoveAttack`（`:967-979`）**确实**连带 `_authorized.Remove`，
且 `(key,target)` 去重账随 `AuthorizedKeyRecord` 一起消失——这部分是**正确的**（I.8 已固化）。

断言固化：I.8（回收后 `AuthorizedProjectileCount==0`；旧 key 的迟到结算 → `UnknownAttack` 且零写入；
同一 key 在新 activation 下可重新授权）。变异验证：删掉 `RemoveAttack` 里的 `_authorized.Remove` 循环后
→ `1498/2`，失败项为 `I.8 旧记录的已授权 key 一并回收（期望 0，实际 1）` 与
`I.8 同一 key 在新 activation 下可重新授权`。

### R6C-11 补蓝「最多 3 段」是**硬编码**，与 `ManaMax=90 / ManaPerSegment=30` 绑定

**事实**：`RechargeMana`（`:1035` 起）用 `if (timer >= reloadMs * 3.0) steps = 3;`。
若将来 `ManaMax`/`ManaPerSegment` 改变（例如每段 10、满蓝 90 需 9 段），这里的 `3` 会**少补蓝**。

**判断**：当前冻结值 90/30 下完全正确且有界（一次 Tick 最多 3 段），数值源是 Shared 的 `const`，
本次不改（改动属「与当前任务无关的逻辑」）。列为后续欠账。

### R6C-12 `ResolveAttack` 的「无大招配置则回退普通攻击」依赖 planner 的 `TryGetSuper` **前置守卫**

**事实**：`BattleNumericConfig.ResolveAttack(heroId, isSuper:true)` 对**没有**大招配置的英雄会**回退到普通攻击值**
（Shared 的注释也写明了）。`PMCombatWeaponPlanner.TryBuild` 先用 `TryGetSuper` 拒绝（`:122-126`），
因此这条回退不会被武器计划走到。

**判断**：守卫是**承重**的——任何新调用方直接调 `ResolveAttack(hero, true)` 都会拿到普通攻击值而不自知。
`PMCombatSession` 只经 planner，所以安全。建议后续给 `ResolveAttack` 的 doc 补一句「调用方必须先 `TryGetSuper`」
（属 Shared 文档改动，不在本次范围）。

---

## 4. 任务给定审查清单的逐项回答

| 检查项（委派原文） | 结论 | 证据 |
|---|---|---|
| TTL/capacity 与已授权 projectile 的重复查是否绕过 `Expiry`/`Ended`/`Dead` | **是，回显分支绕过**（by design，契约要求不翻既有授权） | R6C-3；I.9 / I.12；回显不占槽/不加授权（`AuthorizedProjectileCount` 不变） |
| 攻击记录满的拒绝 ID 是否可在资源变化后重用 | **可以**（`Capacity` 不记账、不占水位） | R6C-5；I.7（513 → Capacity → 回收后同一 513 被接受，仅扣一次蓝） |
| 非法请求是否更新资源/clock/Highwater 导致别人正常事件失效 | **资源：否**（全部拒绝在扣费前）。**clock：是**（全局水位被合法时钟的拒绝调用推进 → 更旧的合法结算被判 `InvalidClock`）。**Highwater：仅限被记账的拒绝（per-owner，不跨玩家）** | I.1 / I.17 / I.15；`RecordRejection` 写水位是**有意**的（幂等） |
| duplicate `Accepted` 不能翻 `Rejected`，且 changed aim 不重新扣/占槽 | **成立**。同 ID 不同内容只 `IdConflictCount++`，返回**首次**结论的深拷贝；不重扣、不刷新 TTL、不重新占槽 | E 段原断言 + I.17（非法方向拒绝后同 ID 换合法方向仍返回 `InvalidAim`、蓝不变、冲突+1） |
| 有限记录回收是否连带 projectile 元数据/去重，避免不可达伤害或无限记忆 | **是**（`RemoveAttack` 连带撤销 key 与 `(key,target)` 去重；上界 512/2048 有界） | R6C-10；I.8 + 变异验证（删 `_authorized.Remove` → 2 条断言失败） |
| `StartMatch` 要求所有 `expected` 且 2 team？一人规则当前留但不可未齐开 | **人数必须精确相等（1..6）**；**不要求**连接态；**≤2 队**在 `AddPlayer` 强制。一人规则保留（用于 `winner 0` 可达）；「未齐不开」由人数精确相等保证 | B 段原断言；I.16；`StartMatch` 先判 `_started`/范围/计数 |
| `Disconnect` outcome 及重复不改 winner | **成立**：`EvaluateForfeit` 只在 `!Ended && _started` 时动作，`EndMatch` 单次锁；重复 `Disconnect` 返回 false 不改结论 | H 段原断言 + I.13（重复断线后 `OutcomeId/Winner/Reason` 全不变） |
| `ApplySettlement` 整批先结构校验，不得先扣前目标遇坏后目标才 false | **成立**：`Hits==null`/空/`HitCount` 不符/长度>100/目标 0/同批重复 → **整批拒绝且零写入**（校验在一切写入之前，含 O(n²) 同批去重扫描） | G 段原断言 + I.5（101/负 `HitCount`/`projectileId=0`/`activationId=0` 全部 `InvalidId` 且零写入） |
| Key/Origin/AuthorityID/Activation 可信绑定 | **Key/Origin/Activation/Owner/Epoch 已双向校验**；**`AuthorityNetId` 未校验**（契约交给 host） | `:585-590`（上行 identity）、`:781-787`（结算 identity）；R6C-9；I.9/I.12 |
| `hitCount` vs `Hits.Length` 不一致 / 重复 target / 友军 / Dead 后不回能 | **全部成立**：不一致 → 整批 `InvalidId`；同批重复 → 整批 `InvalidId`；友军/self/未知/Dead → 只跳过该目标、**不回能并且不消耗该 key 的 `(key,target)` 额度** | G 段 + I.3（友军/self/未知三次跳过后该 key 仍能命中敌队并回能 66）+ I.5 |
| first kill 抛异常不要半提交 | **结构先验在所有写入之前**；首杀写入是纯算术 + 单次锁，无抛异常路径。**且本次修掉**「首杀后同批后续目标仍被写」 | R6C-2；I.6 + 变异验证 |
| 巨大 double delta 回蓝 overflow | **无溢出**：`steps` 恒 ≤ 3、`timer/reloadMs` 只在 `< 3` 分支转 int、`Math.Ceiling` 后钳 `[1,2500]`；`Tick(double.MaxValue)` 后满蓝且再次 Tick 不增长 | I.11（`1e18` / `double.MaxValue` / `+Inf` / `NaN` / 倒退）= 90 封顶、非法拒绝 |
| planner 真实 config 值 zeroDamage / unsupportedPara / 1deg 方向余弦 / zero angular 大招 | **zeroDamage 保留**（斯派克 0 → 不扣血/不回能/不首杀）；**抛物线拒绝**（巴利/爆破麦克/迪克普通、帕姆大招）；**容差=cos(1°)** 且 0.9°/1.1° 分界正确；**零角多弹同向逐槽占用** | A 段原断言 + I.2（抛物线/无大招不扣资源）+ I.3（45/2=22）+ I.10（0.9°/1.1°、`1e-6` epsilon 边界） |
| 雪莉 40deg 别按报告 30 | **报告错、配置与测试对**：`_supers[0].LaunchAngle = 40f`，测试 oracle `SuperAngle[0]=40f` 并逐方向校验 ±20°；报告 §2.3 的「雪莉 0（40 发，**30°**）」是错值 | 独立复算：`super angle = 40`；`Program.cs:199` `CheckDirections(plan, SuperAngle[hero], …)` |

---

## 5. 数值与计数核对（含 `_r6_core_report.md` 的自相矛盾）

用独立复算（不改仓库）逐值核对报告 §2.3 / §2.4 / §4.2 的数值断言：

| 报告断言 | 独立复算 | 结论 |
|---|---|---|
| 普通攻击 17 支持 / 3 拒绝（巴利 4、爆破麦克 9、迪克 11 为 `IsParabola`） | 17 / 3，拒者恰为 {4,9,11} | ✅ |
| 大招 5 支持 / 15 拒绝 | 5 / 15；支持者 {0,1,7,12,19}；帕姆 18 因 `IsParabola` 拒 | ✅ |
| 计划寿命上界 1000ms（布洛克/贝亚/斯派克 `dist==speed` 为 10/10/10） | 1000ms | ✅ |
| `FireIntervalMs`：雪莉…瑞科 = 100ms，帕姆 = 150ms | 100 / 150（float32 0.1f=100.000001、0.15f=150.000006，先 `Round(…,4)` 再 `ceil`） | ✅ |
| 雪莉普通寿命 546ms / 佩佩 917 / 瑞科大招 737 | `ceil(6/11*1000)=546`、`ceil(11/12*1000)=917`、`ceil(14/19*1000)=737` | ✅ |
| 「雪莉 0（40 发，**30°**）」 | 冻结配置 `LaunchAngle=40f`；测试 oracle 也是 40 | ❌ **报告数值自相矛盾**（40 发对、30° 错）。`_r6_core_report.md` **不在**本次可写范围，故只在此报告点名 |
| 「终局后所有 `RequestAttack`/`TryAuthorizeProjectile`/`ApplySettlement` 一律拒绝且无任何变化」 | `RequestAttack` 幂等回显与 `TryAuthorizeProjectile` 重复 key 回显都会在终局后返回原结论（**有意**，契约要求不翻既有授权）；且修改前 `ApplySettlement` 批次内终局后仍写后续目标 | ❌ **报告自述过强**：前两者是取舍、后者本次已修 |
| 「`InvalidConfig` 在当前冻结数值表下不可达」 | 成立（最大寿命 1000 << 2500；所有字段合法） | ✅ 但正因如此，R6C-1 的 fail-open 在原实现里**不会**被任何测试/现网触发——它只在**将来**改数值表或接入非冻结配置时爆 |

断言计数（本次实测）：

| 分段 | A | B | C | D | E | F | G | H | I | 合计 |
|---|---|---|---|---|---|---|---|---|---|---|
| 修改前 | 783 | 35 | 49 | 33 | 91 | 111 | 106 | 49 | — | **1257** |
| 修改后 | 783 | 35 | 49 | 33 | 91 | 111 | 106 | 49 | **243** | **1500** |

A–H 的 1257 条**一条未删未改**；新增 I 段 243 条（对抗审查）。

---

## 6. 未覆盖 / 不可达（诚实口径，逐条说明为什么）

1. **R6C-1 的溢出路径没有运行断言**：`TryBuild` 只能从冻结 `BattleNumericConfig` 取数，本任务不允许改 Shared，
   因此无法在生产路径上构造「巨大 distance/speed」。修的是**守卫的正确性**（先验后转），验证方式是
   阅读 + `InvalidConfig` 分支仍对全部 26 个支持组合保持 fail-closed（A 段）——**不是**「实测触发过溢出」。
2. **`Dead` 拒绝原因不可达**（R6C-8）：HP 归 0 与 `Ended` 同一次发生。I.14 只能证明它被 `Ended` 遮蔽，
   不能产生 `Dead`。
3. **`PMCombatLimits.MaxAuthorizedProjectiles=2048` 的全局上限在不改时钟的情况下不可达**：
   按每次攻击最多 64 key、单局 5s TTL 内还要过 cooldown/资源，实际需要数十次满弹攻击；
   报告 §5 第 5 条已自述，本次只做常量断言（A 段）+ 每攻击额度（F 段/I 段）。
4. **不覆盖真实 R5 字节链与 Unity**：本测试直接构造 `PMProjectileSpawnIntent` / `PMProjectileSettlement`
   真实类型值，**不经过** codec 字节链、不产生真实弹、不接 PhysX。T6B / T6C / T46 / T47 仍为
   `PENDING_USER`，**不能**声称「已能联机开枪」。
5. **`ServerDirect`（DS 自产弹）不在 A1**：`ApplySettlement` 只收 `ClientPredicted` key；`AuthorityNetId` 未用。
   若 B 阶段要开 ServerDirect 或要 session 校验权威来源，需**接口变更** → 按委派要求**只报告不实施**。
6. **`ApplySettlement` 不检查 attacker 存活/连接**：契约把该职责交给 host（R6C-9），故视为已覆盖但**非 session 职责**。
7. **多 team / 单 team 名册**：`MaxTeams=2` 已强制，但 session 不要求「两队都有人」；同队多人合法。
   这在「仅剩唯一在线队伍 → 该队 Forfeit 获胜」语义下有一处反直觉：单队名册里某人断线会让**剩下的人所属队**
   （即同一队）获胜。契约原文支持这一读法，A1 未定义单队产品的具体语义。
8. **`RechargeMana` 的 `steps=3`** 隐含绑定 `ManaMax/ManaPerSegment=90/30`（R6C-11），未加护栏断言。

---

## 7. 测试与变异证据（证明新增断言有牙齿）

| 变异 | 期望被捕获 | 实测 |
|---|---|---|
| 删掉 `ApplySettlement` 里终局后的 `break`（R6C-2 回退） | I.6 两条 | `1498 通过 / 2 失败`：`I.6 同批第 2 目标不再写入（期望 840，实际 0）`、`I.6 同批第 2 目标不因同批后续命中被置 Dead` |
| 删掉 `RemoveAttack` 里的 `_authorized.Remove` 循环（R6C-10 回退） | I.8 两条 | `1498 通过 / 2 失败`：`I.8 旧记录的已授权 key 一并回收（期望 0，实际 1）`、`I.8 同一 key 在新 activation 下可重新授权（旧元数据已消失）` |

两次变异均由同一脚本「改 → build → run → 逐字还原」，还原后 `PMCombatSession.cs` MD5
与变异前**完全一致**（`863d26a4d19d5792b0e558bfcead5a92`），并且还原后重跑 **1500 / 0**。

I 段的期望值全部手工推导：HP 算术（960−650=310、840−45=795→750→705、840−1155→clamp 0）、
整数除法回能（45/2=22、再 22=44、三次=66、650/2→200、800/2→200、1155/2→200）、
容差分界（0.9° 收 / 1.1° 拒）、`ZeroEpsilon=1e-6` 含边界、TTL 5000/5001ms、容量 512/513、
时钟 `1e18`/`double.MaxValue`/`+Inf`/`NaN`/`1e18-1e6`；全部经生产入口
（`RequestAttack` / `TryAuthorizeProjectile` / `ApplySettlement` / `Tick` / `Disconnect` / `StartMatch`）。

---

## 8. 本次明确**没有**做的事

- 未改 `PMCombatContracts.cs`、`Shared/**`、`PMProjectile/**`、主计划 `net-architecture-migration.md`、
  `net-r6-combat-contract.md`、`_r6_core_report.md`（后两者不在可写范围，故其数值错误只在本报告点名）。
- 未新增/修改任何**公开** API 签名（`PMCombatWeaponPlanner.TryBuild/IsSameDirection/TryNormalizeDirection/IsFinite`
  与 `PMCombatSession` 的全部公开成员保持不变）→ 并行 driver 不会被本批改动打断。
- 未新增 public 类型/方法：`IsFiniteDouble` 是 `private`。
- 未启动 Unity、未跑实机、未做任何 git/SVN 写操作、未提交、未递归委派。

## 9. 给 B 阶段的最小行动清单（都是「宿主/驱动」而非 session 改动）

1. **四个入口共用同一处单调钟的当前读数**（不要把消息捕获时刻传进 session），并监控 `InvalidClock` 应为 0（R6C-4）。
2. `TryAuthorizeProjectile` / R5 发射前判 `session.Ended` 与 owner `Connected/Dead`，**不把回显 `true` 当发射许可**（R6C-3）。
3. 保持「先战斗命令队列 → 再 `R5.Pump`」的顺序，Settlement 只走 `ApplySettlement` 唯一消费者（契约 B）。
4. 若要 activationId 全局单次使用 / 抗单点刷表 / 校验 `AuthorityNetId` / 开 ServerDirect，
   这些是**口径或接口变更**（需要改 E 段断言或 session 构造签名），必须先由主 Agent 决策，勿在并行期直接改。
