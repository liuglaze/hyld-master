# R6-A1 契约缺口收口（terminal fix）：回显闸门 / 容量水位 / 开战门 / 结算身份

> 任务类型：**comprehensive**（只做兼容性修复：不改任何公开 API 签名，不改共享 Contracts / Planner / R6 驱动）
> 必读输入（按委派顺序全文阅读）：`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`
> → `Docs/plans/net-r6-combat-contract.md` → `Docs/plans/_r6_core_review.md`
> → `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`。
> 写入范围（仅此三处）：`Client/Assets/Scripts/PMCombat/PMCombatSession.cs`、
> `Tools/PMCombatCoreTest/Program.cs`、本文件。
> **未启动 Unity；未提交（git/svn 均无写入）；未递归委派；未改主计划、未改共享 Contracts / PMCombatWeaponPlanner /
> PMR6CombatDriver / 任何其他文件。**

**结论（一句话）**：把 `_r6_core_review.md` §3 里「标成 by design 保留」但其实是**契约缺口**的四项收口——
① `TryAuthorizeProjectile` 重复 key 不再是绕过比赛状态的纯回显；② `RequestAttack` 的 `Capacity` 拒绝也占
owner 单调水位；③ `StartMatch` 要求「人数一致 **且** 全名册 `Connected && !Dead`」；④ `ApplySettlement`
补 attacker 在线/存活、`AuthorityNetId != 0`、`Origin` 与 `Key.Origin` 双向一致——
并把原先固化这些旧行为的 I 段断言改成契约正确期望，另加带**前后资源快照**的反例段 J。
公开 API 签名零变化；A–H 的 1257 条断言**一条未删未改**；断言总数 **1257 → 1699**（I 296 + J 146），
build 0 警告 0 错误，运行 0 失败，exit 0。

---

## 0. 验证命令与结果（本机实测）

```bash
# 语言面门禁（netstandard2.0 + C# 7.3，零 UnityEngine / 零 R3）
dotnet build Tools/PMCombatCoreCheck/PMCombatCoreCheck.csproj -c Release   # 0 警告 / 0 错误
# 运行验收（net8.0，真实源码，无 mock）
dotnet build Tools/PMCombatCoreTest/PMCombatCoreTest.csproj -c Release    # 0 警告 / 0 错误
dotnet Tools/PMCombatCoreTest/bin/Release/net8.0/PMCombatCoreTest.dll     # 1699 通过 / 0 失败，exit 0
python Tools/check_cs_braces.py <两份 .cs>                                 # BALANCED，深度非负、末尾归零，PASS
```

| 命令 | 结果 |
|---|---|
| `PMCombatCoreCheck`（netstandard2.0 + C#7.3 编译门禁） | **0 警告 / 0 错误** |
| `PMCombatCoreTest` 构建 | **0 警告 / 0 错误** |
| `PMCombatCoreTest` 运行 | **通过 1699 / 失败 0**，退出码 **0** |
| `python Tools/check_cs_braces.py`（Session / Program） | BALANCED / PASS |
| 编码自检（两份 `.cs`） | 全部 `BOM=True` + 仅 CRLF（无 lone LF/CR） |

分段断言（本次实测）：

| 分段 | A | B | C | D | E | F | G | H | I | J | 合计 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 修改前 | 783 | 35 | 49 | 33 | 91 | 111 | 106 | 49 | 243 | — | **1500** |
| 修改后 | 783 | 35 | 49 | 33 | 91 | 111 | 106 | 49 | **296** | **146** | **1699** |

A–H 的 1257 条**原样保留**（数字未变即未删未改）。I 由 243 → 296（改 4 组期望 + 新增快照断言），
J 为本次新增 146 条。

改动后文件指纹（供对账）：

| 文件 | MD5 | BOM/换行 | 字节 |
|---|---|---|---|
| `Client/Assets/Scripts/PMCombat/PMCombatSession.cs` | `0ab0efe7d7ff6da0c756a862a9f9aa5d` | BOM + CRLF | 55894 |
| `Tools/PMCombatCoreTest/Program.cs` | `dd6750ab44d8c3065e334cdbe4a2f1dc` | BOM + CRLF | 131059 |

`PMCombatContracts.cs`、`PMCombatWeaponPlanner.cs`、`PMR6CombatDriver.cs` 及其 `.meta` 均**未被本次改动触碰**。

---

## 1. 四项修复（都在 `PMCombatSession.cs`，均不改签名）

### FIX-1 重复 key 回显闸门（`TryAuthorizeProjectile`，`PMCombatSession.cs:641-717`）

**修复前**：重复 key 分支位于 `_started` / `Ended` / 玩家存在 / `Connected` / `Dead` / 记录 TTL / 槽位判定
**之前**，因此一个已经授权过的 key 在任何状态变化之后都原样回显 `true` + spec。后果：宿主若据此再次
`R5.TryFire`，会在**终局后 / 记录 TTL 之后 / owner 已断线或已死后**真的生成一颗弹，而它的结算会被
session 静默丢弃（客户端看到弹、HP 不动 = **不可达伤害**）。

**修复后**：回显前逐项复核
`_started` → `!_outcome.Ended` → owner 在名册 → `Connected` → `!Dead` → 原 activation 记录仍在且 `Accepted`
→ `wallNow - record.WallTimeMs <= AttackRecordTtlMs`；任一不满足按当前状态拒绝
（`NotStarted` / `MatchEnded` / `UnknownPlayer` / `Disconnected` / `Dead` / `UnknownAttack` / `Expired`）。
失败**不占槽、不改资源、不撤销既有授权元数据**；成功才回既有 spec 的深拷贝，且依旧不占槽。

**为什么这不是「反向撤原弹」**：契约「重复 key 回既有授权」约束的是**不占槽 / 不改资源 / 不翻既有结论**，
不是「绕过当前战斗状态」。生产链路上已登记 key 的重发在 **R5 驱动策略之前**就被拦掉：
`PMR5ProjectileDriver.cs:1104` 的 `TryFindAdmittedKey`（已预留/已上线/协调器登记，命中即
`HandleDuplicateUplinkSpawn` 并 return，**不再**调 `_policy.TryAuthorizeSpawn`、不预留 NetId、
不发 Rejected），紧随其后 `:1112` 的 `Lifecycle.IsRetired` 终态 key 直接丢弃。因此核心这里返回 false
只可能发生在「key 从未真正登记成功」或「登记事实已被有界回收」的情形，不会撤销一颗已 Confirmed 的弹。

**未知 / 过期 key 无副作用**（契约原文要求）：未知 key 走主路径 → `StaleId` / `UnknownAttack`，
不写账本、不占授权、不改任何资源；过期 key 记录仍在但超 TTL → `Expired`，同样零副作用（且不删除既有
授权元数据）。

### FIX-2 `Capacity` 拒绝也占 owner 单调水位（`RequestAttack`，`PMCombatSession.cs:512-521`）

**修复前**：`EnsureCapacity` 失败直接 `return Capacity`，既 `RecordRejection` 也不写
`HighWaterActivationId`。于是同一个 `activationId` 在记录被 TTL 回收 + 资源恢复之后会被当成一笔
**全新攻击**接受。

**修复后**：容量拒绝先 `player.HighWaterActivationId = activationId;` 再返回 `Capacity`。
**不插入满表记录**（那正是容量拒绝要避免的），水位足够：同一个 ID 之后只会得到 `StaleId`。

**为什么必须这样**：R5 已因为这次 `Rejected` 真实撤销了该 activation 的假弹（
`PMR6CombatDriver` 收到 Rejected → `CancelPredictedActivation`），核心再把它当新攻击接受就是
「扣了资源却生成不出东西」——资源黑洞，且违反契约 A1「记录消失后旧 ID 不会被当新请求」。

**既有 decision 的幂等查询不受影响**：`_attacks` 查询（`:439-455`）在**水位判定之前**，
所以已记账 ID 的重复请求仍返回 `Duplicate = true` + 原结论（J.3 已固化）。

### FIX-3 `StartMatch` 要求全名册在线且存活（`PMCombatSession.cs:324-352`）

**修复前**：只要求 `expected == _players.Count`（1..6），不要求成员 `Connected`。

**修复后**：在人数一致之后再加一道循环——任一成员 `!Connected || Dead` 即拒绝。
`!Dead` 半边是**防御性**的（Dead 只能由结算写入，而结算要先过 `_started`，开战前不可达），
写进门槛是为了不让「Dead 也算可开战成员」成为将来新路径的隐式假设。

**为什么不是玩法选择**：下界仍是 1、上限仍是 `MaxPlayers`，**单队 / 单人局完整可用**（J.4 固化：
单队 2 人可开、单人（在线）可开）；`MaxTeams` 只是上限。契约 A1「不得在全部到齐前因只一个已连接队
早判终局」与契约 B「宿主在全名册接入后调 StartMatch」共同要求这道门；`StartMatch` 本身**仍不判 forfeit**
（forfeit 只在 `Disconnect` 触发），所以 H 段「未 start 不早终局 / winner 0」的语义一字未动。

### FIX-4 `ApplySettlement` 补 attacker 与结算身份校验（`PMCombatSession.cs:886-900`、`948-968`）

**修复前**：只校验 key 的 epoch/owner/origin、`ActivationId`、授权记录存在且 Accepted、记录未过 TTL、
attacker **在名册里**；不看 attacker 连接/存活，也不看 `settlement.AuthorityNetId`，也不校验
`settlement.Origin` 与 `Key.Origin` 是否一致。

**修复后**：
1. 身份块补 `settlement.Origin != settlement.Key.Origin` 与 `settlement.AuthorityNetId == 0u` →
   `IdentityMismatch`（在**任何写入之前**，整批拒绝零写入）；
2. 取到 attacker 后补 `!attacker.Connected` → `Disconnected`、`attacker.Dead` → `Dead`
   （同样在任何写入之前）。

**`AuthorityNetId` 的诚实边界（不新增假字典）**：`AuthorityNetId` 是「本地受信 DS」的**边界标记**，
不是「本局权威是谁」的证明。本对象是纯核心：不知道自己在哪个 World / 哪台 DS 上跑，也不持有权威 DS
名单，**因此只校验非 0**（0 = 没有权威身份的伪结算，与 `PMProjectileCodec` 对下行快照
`AuthorityNetId` 必须非 0 的口径一致，见 `PMProjectileCodec.cs:1102/1472`）。
「这份 settlement 真的来自本局权威 DS」由 host 保证：R5 只把 `PMProjectileCoordinator.SettleHits`
（唯一生产点，`:1853-1855`）产出的结算交给 `ApplySettlement`，**AP 永不调用**。
`attacker.Dead` 半边与 `PMCombatRejectReason.Dead` 同属**防御性不可达**（Dead ⇒ 首杀终局 ⇒ 入口的
`_outcome.Ended` 先返回 `MatchEnded`），与 `_r6_core_review.md` R6C-8 的结论一致。

**物理语义取舍说明（诚实）**：修复前 I.12 断言「已出膛的弹在发射者断线后仍生效」。本次按委派要求
改成契约正确期望——**断线 attacker 的结算一律拒绝**。理由：断线成员所在的权威会话已在其
`Disconnect` 时被判 forfeit 或失去参赛资格（`EvaluateForfeit`），继续让一个已离场身份的迟到结算
改血量/回能，等于让「已离场」在结算侧复活；而「弹在飞行中」这件事的正确位置是 **R5 协调器**（它自己
有 stop/retire/墓碑逻辑），不是 R6 权威账本。若后续产品确需「离场后弹仍生效」，应作为**显式契约变更**
由主 Agent 决策（会同时改变 I.12/J.5 与 driver 的 forfeit 时序），本次不擅自扩权。

---

## 2. 测试改动（`Tools/PMCombatCoreTest/Program.cs`）

### 2.1 旧「by design」断言改为契约正确期望（I 段，+53 条）

| 位置 | 修复前断言（旧口径） | 修复后断言（契约正确期望） |
|---|---|---|
| I.7 `:1269` | 第 513 个新 ID → `Capacity` 后，**回收+资源恢复后同一 513 被接受并扣一次蓝**（`Capacity` 不占水位） | `Capacity` 仍不写满表记录、`over.Accepted == false`、前后资源快照不变；`+5001ms` 回收后同一 **513 → `StaleId`**、零副作用、蓝仍 60、`StaleId` 不触发回收不记账（账本仍 512） |
| I.9 `:1321` | 终局后重复 key **仍回显 `true`**；TTL 过期后重复 key **仍回显 `true`** | 终局后重复 key → **`MatchEnded`**、`spec == null`、授权数不增不减、快照不变；TTL 过期后重复 key → **`Expired`**、`spec == null`、授权元数据不被删除；新 key 各自仍 `MatchEnded`/`Expired` |
| I.12 `:1397` | 断线者已授权 key **回显 `true`**；断线者已出膛弹**仍结算**（960-800=160、回能 200） | 断线者重复 key → **`Disconnected`**；断线 attacker 的结算 → **`Disconnected`**、目标 HP 无写（仍 960）、attacker 不回能（仍 0）、目标与 attacker 快照均不变、仍未终局 |
| I.16 `:1478` | 名册 4 人但 team2 两人已断线 → **`StartMatch(4)` 成功开局**（连接态不参与门槛） | 同样的名册 → **`StartMatch(4)` 被拒**、仍 `!Started`、快照不变；未开战攻击 → `NotStarted` 且不记账；降成在线人数 `StartMatch(2)` **同样拒**；对照组「全员在线 → `StartMatch(4)` 成功」保留，开战后逐人断线才判 forfeit（winner = 唯一在线队伍 1） |

原 I 段其余 12 组（I.1–I.6、I.8、I.10、I.11、I.13–I.15、I.17）**一字未改**。

### 2.2 新增 J 段：带前后资源快照的收口反例（146 条）

新增 `ResSnapshot`（仅玩家级 `Hp / Mana / SuperEnergy / Dead / Connected` 的深拷贝）+
`Snap()` / `CheckResUnchanged()`（每条快照 5 个断言）。**账本计数不进快照**：`PurgeExpired` 本来就会
合法改变记录数，把它混进「拒绝无副作用」会得到假失败——计数用独立 `CheckEq(AttackRecordCount, …)` 断言。

| 用例 | 固化内容 |
|---|---|
| J.1 终局后重复 key | `MatchEnded`、`spec == null`、`AuthorizedProjectileCount` 不增不减、快照不变 |
| J.1 原 activation 过 TTL 后重复 key | `Expired`、`spec == null`、既有授权元数据**不被删除**、快照不变 |
| J.1 owner 断线后重复 key（比赛未终局） | `Disconnected`、`spec == null`、快照不变 |
| J.2 未知 activation / 未知 key 的授权与结算 | `UnknownAttack`、不写账本、不占授权、快照不变（含未知 owner 的结算被拒） |
| J.3 `Capacity` 占水位 + 幂等查询 | 已有记录重复查询仍 `Duplicate + 原结论(Cooldown)`、`IdConflictCount == 0`；第 513 → `Capacity`；回收后同一 513 → `StaleId`、快照不变、蓝仍只扣过 30 |
| J.4 开战门与单队/单人 | 有人断线时 `StartMatch(3)/(2)/(4)` 全拒且零副作用；单队 2 人、单人（在线）仍可开战 |
| J.5 结算身份与 attacker | `AuthorityNetId = 0` → `IdentityMismatch` 零写入；`settlement.Origin` 与 `Key.Origin` 不一致 → `IdentityMismatch` 零写入；`ServerDirect` key 的结算 → `IdentityMismatch` 零写入；attacker 断线 → `Disconnected` 零写入；同队**在线**成员 105 的弹照常结算（960-650 = 310，证明只拦断线 owner） |

### 2.3 未删的原有正例（任务硬要求）

原 A–H 全部 1257 条断言（含所有**攻击批准 / 扣蓝回蓝 / 首杀 `OutcomeId` / `WinnerTeamId` 正例**）
保留；I 段被改的 4 组均**只改期望口径**，原有「先授权 → 再命中 → 扣血回能 → 首杀锁终局」的
正例步骤（I.9/I.12 的攻击、扣蓝、damage/2 回能、`MakeSettlement` 结构）全部留下。

---

## 3. 变异证据（证明新断言有牙齿）

对 `PMCombatSession.cs` 逐项做「改 → build → run → **逐字还原**」，还原后 MD5 与变异前一致
（`0ab0efe7d7ff6da0c756a862a9f9aa5d`），并重跑 1699/0。

| 变异 | 期望被捕获 | 实测 |
|---|---|---|
| M1 删掉重复 key 分支里的 `Ended` 闸门 | I.9 / J.1 | `1695 通过 / 4 失败`，含 `I.9 终局后重复 key → MatchEnded`、`J.1 终局后重复 key → MatchEnded`（另 2 条为同组 `spec == null` / 快照断言） |
| M2 删掉 `Capacity` 分支的 `HighWaterActivationId = activationId` | I.7 / J.3 | `1692 / 7 失败`，含两条 `回收后同一 ID 513 → StaleId` |
| M3 删掉 `StartMatch` 的 `Connected/Dead` 闸门循环 | I.16 / J.4 | `1693 / 6 失败`，含 `I.16 名册 4 人但有成员断线 → StartMatch(4) 拒`、`J.4 名册 3 人但有人断线 → 拒` |
| M4a 删掉身份块的 `settlement.AuthorityNetId == 0u` | J.5 | `1691 / 8 失败`，含 `J.5 AuthorityNetId=0（无权威身份的伪结算）→ IdentityMismatch` |
| M4b 删掉 `if (!attacker.Connected) → Disconnected` | I.12 / J.5 | `1689 / 10 失败`，含两条「断线 attacker 的结算 → Disconnected」 |

5/5 变异均被捕获，且每次都多捕获到同组的配套断言（快照 / `spec == null` / `Accepted == false`），
说明「零副作用」不是靠单条断言兜着。

---

## 4. 诚实口径：未覆盖与不可达

1. **`attacker.Dead` 分支不可达**（防御性，与 R6C-8 同类）：Dead ⇒ 首杀终局 ⇒ 入口 `_outcome.Ended`
   先返回 `MatchEnded`。本次只加了断言证明「终局后结算 → `MatchEnded`」，**没有**也无法构造
   「attacker Dead 且比赛未终局」。
2. **`StartMatch` 的 `!Dead` 半边同样不可达**（Dead 只能由结算写入，结算要先过 `_started`）。
3. **`AuthorityNetId` 只校验非 0**：核心无法独立知道「本局权威是谁」。要校验「结算只能来自本局权威 DS」
   需要新增构造参数 / 绑定 API → 属**接口变更**，按委派要求只报告不实施。
4. **`Capacity` 水位只挡「同 owner 同 ID 复活」**，不改 `_r6_core_review.md` R6C-6 的
   「单个 owner 可占满全局 512 记录表」口径——那是**契约冻结的 session 级容量**，改它需要动 E 段断言，
   属跨批次口径变更，本次不碰。
5. **R6C-4（共享时钟水位）未改**：本次按契约保留「合法时钟的拒绝调用也推进全局水位」，
   I.15 原断言未动；B 阶段仍须「四个入口共用同一处单调时钟的当前读数」。
6. **R6C-11（补蓝硬编码 3 段）未改**：与 `ManaMax/ManaPerSegment = 90/30` 绑定，本次不扩权。
7. **不覆盖真实 R5 字节链与 Unity**：本次仍只编纯核心真实源码，不产生真实弹、不接 PhysX；
   T6B / T6C / T46 / T47 仍为 `PENDING_USER`，**不能**声称「已能联机开枪」。

### 4.1 观察到的外部阻塞（与本次改动无关，未处理）

`Tools/PMR6NetworkTest/PMR6NetworkTest.csproj` 当前**无法构建**，报 6 处
`error CS0103: 当前上下文中不存在名称"_bufferedSettlements"`（`PMR6CombatDriver.cs:588/1163/1165/1166/1945/1964`）。
该字段在仓库里**没有任何声明**，且 `PMR6CombatDriver.cs` 的 mtime（`20:38:05`）落在本次构建（`20:38:2x`）
**之前数十秒**，即它是**并行 R6-B 任务正在编辑的中间态**；该文件在本次写入边界之外，故**只报告不修改**。
因此「重复 key 回显闸门对 driver 无副作用」这条结论**不是**靠跑 `PMR6NetworkTest` 得到的，而是靠读
R5 真实闸门代码（§1 FIX-1）与该 driver 的拒绝路径（policy false → `CancelReservedNetId` + 下行 Rejected，
**不 fault**；`ApplySettlement false` → 仅 `SettlementsRejected++`，**不 fault**）得到的。待并行驱动收敛后，
建议用 `PMR6NetworkTest` 复跑一次作为端到端复核。

---

## 5. 本次明确**没有**做的事

- 未改 `PMCombatContracts.cs`、`PMCombatWeaponPlanner.cs`、`Shared/**`、`PMProjectile/**`、
  `PMR6CombatDriver.cs`、`net-architecture-migration.md`、`net-r6-combat-contract.md`、`_r6_core_review.md`。
- 未新增 / 修改任何**公开** API 签名（`PMCombatSession` 全部公开成员与 `PMCombatWeaponPlanner` 公开静态方法
  保持不变）→ 并行 driver 不会被本批改动打断。新增成员全部是 `private`（`ResSnapshot`、`Snap`、
  `CheckResUnchanged`、`TestTerminalFix`）。
- 未启动 Unity、未跑实机、未做任何 git/SVN 写操作、未提交、未递归委派。
