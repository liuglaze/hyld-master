# R5-A2 目标历史与命中验证纯核心 —— 独立对抗复核与修复报告

> 状态唯一源：`Docs/plans/net-architecture-migration.md`；本轮的**独立对抗复核 + 修复 + 证据**记录。
> 冻结契约：`Docs/plans/net-r5-projectile-contract.md`；共享只读类型：`Client/Assets/Scripts/PMProjectile/PMProjectileContracts.cs`（**本轮未改**）。
> 上一轮实施报告：`Docs/plans/_r5_validation_report.md`（本文对其中 2 处表述/口径提出更正，见 §5.1、§5.3）。
> 本轮只改边界内 4 个文件、不启动 Unity、不提交、不跑 SVN、不递归委派、不改共享类型、不改他组文件。

---

## 0. 摘要

复核范围＝任务点名的 15 项对抗点（历史单调/重复覆盖/Teleport/升流、Reset 倒退 epoch、OutputFrame 跨帧倒退、旧历史后退当前 fallback 的 Alive、未定义 enum 是否被当 confirmed、activation0 的 ServerDirect 信任、batch 身份与逐元素 finite 前置、5 次 Verify 计数口径、provider 返回错 epoch/netId/stream 的核对、超长线段与 VisualOffset 的非障碍验证边界、体型/时间/float 溢出、白名单/去重、filter 先于几何的消毒值、结果数组隔离与 Rejected 零 hits、目标上限/高水位）。

**发现 2 处真实缺陷（均已修 + 补回归）**：

1. **`PMProjectileHistory.Reset` 可以把 epoch 退回旧值（真 bug）**：`Reset(6)` 在 epoch=7 上成功执行 ⇒ 已关闭的旧 epoch 被重新打开（该 epoch 的记录/请求此前一律被拒，之后重新被接受），同时样本被清空。负向注入实测：epoch 变 6、样本 1→0、旧 epoch 记录被接受（4 项断言失败）。已改为 **epoch 只能单调前进**：更小的值抛 `ArgumentOutOfRangeException`、相同值等价 `Clear()`。
2. **非有限 `WorldNowMs` 会静默关闭墓碑窗口（真 fail-open）**：`NaN` 时钟下 `TombstoneUntilMs` 比较恒为 false ⇒ 「停止后超墓碑的迟到 Verify」被放行；负向注入实测：`WorldNowMs=NaN` 得到 **Confirmed + 1 命中**且消耗配额。已改为入口 **fail-closed**（`InvalidArgument`，整包拒、零 hits、不消耗配额）。

**另修 1 处可观测性缺口（非拒绝语义）**：`ExtraDeferMs` 超过挂起 Spawn TTL（2000ms）会同时放大 L2 偏差预算与帧锚新鲜度窗口，属异常输入却完全无迹可查；已加 `extra-defer-exceeds-pending-ttl:<n>` Report（**只报不拒**，不改变任何判定）。

**其余 12 项复核结论＝无缺陷，但补齐了 6 项"未覆盖/未言明"**：错域帧查询、provider 三态错配（epoch/netId/stream）、未定义枚举三处 fail-closed、Verify 配额四条口径、溢出 fail-closed 链、100/101 目标边界、batch 身份四元组、Rejected 零输出与无静态缓存、Pending 候选结算约定、批次上限与 `Clear()` 高水位限制。**新登记 8 项明确不做/待主侧裁定的边界**（§5）。

**验收（本次 build 后 run）**：`PMProjectileValidationCheck`（netstandard2.0 + C#7.3，零 UnityEngine）**0 警告 / 0 错误**；`PMProjectileValidationTest`（net8.0）**0 警告 / 0 错误**；运行 **通过 264 / 失败 0，退出码 0**（175 → +89）。负向注入（临时撤掉两处修复后逐字节还原并校验 sha256）精确抓到 **9 项失败**，全部命中对应判据。

---

## 1. 必读文档与已检查范围（按任务指定顺序）

| 顺序 | 文件 | 读法 | 它决定的入口 |
|---|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 全文 | 项目路由：客户端入口 `Client/Assets/AGENTS.md`、计划在 `Docs/plans/net-architecture-migration.md` |
| 2 | `Client/Assets/AGENTS.md` | 全文 | 战斗主链路、动态追帧、子弹/命中链路与参数表；确认「联网伤害判定在服务端、客户端纯表现」 |
| 3 | `Docs/plans/net-r5-projectile-contract.md` | 全文 | **冻结口径**：身份与安全、单位与时钟、有限资源、L0–L4、A 验收 T5A1–T5A6 |
| 4 | `Client/Assets/Scripts/PMProjectile/PMProjectileContracts.cs` | 全文（只读） | 类型与全部维度常量（`PMProjectileLimits`）、`IPMProjectileTargetHistory` / `IPMProjectileHitFilter` 签名 |
| 5 | `Docs/plans/_r5_validation_report.md` | 全文 | 上一轮自述口径（本次逐条对账，发现 2 处需更正） |
| 6 | `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md` | 全文 | Windows 下 bash 工具按 Bash 解析、`.ps1` 才用 pwsh；本轮只用 bash + dotnet，未混用语法 |
| 7（直接依赖，只读） | `Docs/plans/_r5_semantics_survey.md` §1.5/§1.6、§四/§五 | 定点读 | L0–L4 算法原文、米/整毫秒换算表、契约缺口 G1–G5 |
| 8（直接依赖，只读） | `Docs/plans/_r5_host_survey.md` §A/推断/下一步 | 定点读 | **七元组真实来源**（OutputFrame=Input 边界、AuthorityServer 帧、TotalSimTimeMs）、DS 侧历史"当前完全不存在" |

**文档如何决定本轮首搜入口**：契约第「命中验证」段给出 L0–L4 的判定顺序与数值（`speed*(2*clamp(rewind,0,500)+100+extraDefer)/1000`、`radius+5.0+0.3`、`600+extraDefer`、512/1000ms/500ms/20m/0.30m/100/5），因此**不猜数值、不游走目录**：先按契约逐条比对 `PMProjectileHistory.cs` / `PMProjectileValidator.cs` 的实现，再用 `_r5_semantics_survey` §1.5 的 UE 原文（这是两份实现文件的头部自述来源）判断哪些差异是有意偏离、哪些是缺陷；最后用 `_r5_host_survey` §A 确认 `OutputFrame`/帧锚的真实来源，以判定「跨帧 OutputFrame 倒退」的严重性。

未读（有界协议）：UE 工程 `D:/UE_Project/ProjectMecury` 任何源码、旧战斗链 `Server/Manger/Battle`、`R0_1/R0_2/R0_3` 附录。未读他组实现体（只按需 grep 了 A1 两文件的公开枚举名与是否存在对 A2 类型的引用，用于判定 A2↔A1 接缝，未读其算法）。

---

## 2. 复核结论清单（任务 15 点逐条）

证据一律给 `文件:行号`（行号为**本轮修改后**的当前状态；未改动的行号与上一轮一致）。

### 2.1 历史（`Client/Assets/Scripts/PMProjectile/PMProjectileHistory.cs`）

| # | 复核点 | 结论 | 证据 |
|---|---|---|---|
| H1 | ring 实际单调 / 重复覆盖 | **正确**。同 `AuthorityServer` 帧就地覆盖（每帧只留最后输出）；更高帧才追加；更低帧 `serverframe-regression` 拒 ⇒ ring 内 `ServerFrame` 严格递增，因此帧锚的倒序扫描 + `break` 是可靠的 | `Record` 同帧分支 `:335-348`、追加 `:357`；`TryResolve` 扫描 `:481-491` |
| H2 | Teleport | **正确**。区间上界样本 `Teleported` ⇒ 取近侧 `s0`（不外推）；下界 Teleport 不触发（两侧均已是 teleport 后位置） | `:526-533`（计数 `_teleportRefusals` 在 `:531`） |
| H3 | 升流 | **正确**。更高 `StreamVersion` ⇒ 清空该流再写入；更低 ⇒ `stale-stream` 拒；请求侧版本不等 ⇒ 直接 `false`（不退化到别世代） | `:287`（旧流）/`:295-306`（升代清旧）、`:413-418` |
| H4 | **Reset 倒退 epoch** | **缺陷（已修）** | 见 §3.1 |
| H5 | OutputFrame 跨 `AuthorityServer` 帧倒退 | **未拦，但不构成位置/时间口径缺陷** | 见 §5.4 |
| H6 | 查询旧历史后当前 fallback 的 `Alive` | **正确且符合延迟补偿语义**。回溯档 `Alive = a.Alive && b.Alive`（区间任一端已死即不可命中）；`Current` 档取最新样本的 `Alive`（已死 ⇒ `TargetNotAlive` skip） | `Interpolate` `:551-567`（`Alive` 在 `:565`）、`Current` `:473-479`、验证器 `:477-480` |
| H7 | 目标数上限 / 已销毁 key 高水位 | **上限正确、高水位不在本文件**。64 满拒新（不淘汰既有）；`Clear()` 会丢 stream 高水位（登记，见 §5.2）；key 高水位按契约属 A1 注册表 | `:52`（常量）、`:272`（满时拒新）、`:135-147`（`Clear` + 其限制注释） |

### 2.2 验证器（`Client/Assets/Scripts/PMProjectile/PMProjectileValidator.cs`）

| # | 复核点 | 结论 | 证据 |
|---|---|---|---|
| V1 | 未定义 enum 是否被当 Confirmed | **无洞（三处均 fail-closed）**。`PMActivationResult` 未定义值 ⇒ 既非 `Rejected` 也非 `Confirmed` ⇒ `Pending`（不可结算）；`PMProjectileOrigin` 未定义 ⇒ `Key.IsValid` 假 ⇒ `StateKeyInvalid`；`PMFrameDomain` 未定义 ⇒ `IsValid` 为真但 `!=` 目标域 ⇒ 记录期拒、查询期退回溯 | `:342-361`（未定义 activation ⇒ `Pending`）、共享契约 `Key.IsValid`（未定义 origin ⇒ 无效键）、历史 `:218`（OutputFrame 域拒）+ `:428-430`（帧锚域判定）；测试 R3.1–R3.10 |
| V2 | 激活 0 的 ServerDirect 信任 | **符合冻结契约，但存在「显式 Rejected 被忽略」的信任排序**：`ActivationId==0 && ServerDirect` ⇒ 直接 `Confirmed`，**不看账本**（与上一轮测试一致、被刻意固定） | `:342-351`（`ActivationId==0 && ServerDirect` ⇒ `l0Confirmed = true`）；测试 R4.6/R4.7；风险与建议见 §5.5 |
| V3 | activation0 是否只对 ServerDirect | **正确**。`ClientPredicted + 0` ⇒ `ActivationIdZeroNotServerDirect` 整包拒；非 0 激活恒由账本裁决（哪怕 origin 是 ServerDirect） | `:344-348`、`:352-360`；测试 R4.8 |
| V4 | batch 身份 | **正确**。`batch.Key == state.Key` 四元组（Epoch/Owner/ProjectileId/Origin）全等才继续；其余情况不可能落到 L1+ | `:237-240`；测试 R14.1–R14.3 |
| V5 | finite 输入"所有元素先检" | **正确**。逐元素 `TargetNetId / ImpactPoint / VisualOffset` finite 与 20m 上限在 spec/L0/L1/L2/L3 **之前**一趟查完，因此包级拒与元素顺序无关 | `:286-313` |
| V6 | 5 次 Verify 计数口径 | **上限正确（绝无第 6 次），但上一轮"与 UE 先++再查目标数同序"的表述与实现不符** | 口径真相见 §5.1；测试 R6.1–R6.7 |
| V7 | provider 返回错 epoch/netId/stream 是否核对 | **正确**。三者任一不等 ⇒ `StreamMismatch` skip，且在 filter/几何**之前** | `:451-456`；测试 R5.1–R5.9 |
| V8 | filter 先于几何、收到消毒值 | **正确**。顺序＝历史还原 → 尺寸 → Alive → `+= VisualOffset` → `ImpactPoint` 钳制 → `Filter.Accept(sanitized)` → 线段几何 | `:508-541` |
| V9 | 纯验证结果隔离 / Rejected 零 hits | **正确**。全部输出为 `ToArray()` 新数组，与输入无别名；`Reject()` 显式给零长度 hits/skip；无静态可变缓存 | `:554-556`（`ToArray()`）、`:829-841`（`Reject` 零输出）；测试 R15.1–R15.5 |
| V10 | 白名单 / 去重 | **正确但顺序值得注意**：先 `HitTargets` 已命中 → 再批内去重 → 再白名单；**空 `AllowedTargets` 数组＝未设置白名单（放行）**，无法表达"全拒" | `:417-434`；测试 R11.1–R11.4；说明见 §5.6 |
| V11 | 超长线段 / VisualOffset 的穿墙语义 | **正确表述边界**：两者都是"抗伪造"预算，不是障碍/LOS 校验；本层无墙体输入 ⇒ 隔墙命中不会被拒 | `:253-262`（线段）、`:300-311`（VisualOffset）；测试 R10.1/R10.2；说明见 §5.7 |
| V12 | 体型 / 时间 / float 溢出 | **正确（fail-closed 链完整）**。逐层：样本 finite → `center` finite → `threshold` finite → 钳制结果 finite → 距离 finite；任一非有限 ⇒ 目标级 `NumericOverflow` skip（绝不"当作通过"） | `:458-541`（样本 finite → 尺寸 → `center` → `threshold` → 消毒 → 距离）；测试 R9.1–R9.2 |
| V13 | 时钟维度（任务"时间溢出"的另一半） | **缺陷（已修）**：非有限 `WorldNowMs` 使墓碑窗口静默失效 | 见 §3.2 |
| V14 | Rejected 时 Report 仍保留 | **正确**。`Reject()` 保留已累积 reports（`rewind>1000` 的 Report 不会因后续 Reject 丢失） | `:829-841`、`:269-273` |

---

## 3. 修正清单（含前后对比与负向注入证据）

### 3.1 修复 1（真 bug）：`PMProjectileHistory.Reset` 允许 epoch 倒退

**缺陷**：`Reset(uint newEpoch)` 只挡 `0`，不挡 `< _epoch`。epoch 从 7 退回 6 后，该实例进入"已关闭 epoch 重新开放"状态：所有 epoch==6 的记录/请求从此前一律被拒变成被接受（契约「重置需新epoch」「跨epoch/owner/stream请求不影响状态」被破坏），且 `_streams` 已被清空 ⇒ 高水位一并丢失。触发条件极普通：任何把"上一个 epoch 值"或"回退的会话代次"传进 `Reset` 的调用方。

**修改**（`PMProjectileHistory.cs:121-133`）：

```csharp
if (newEpoch < _epoch)
{
    throw new ArgumentOutOfRangeException("newEpoch",
        "[PMProjectileHistory] epoch 只能单调前进：当前 " + _epoch + "，收到 " + newEpoch
        + "（倒退会重新打开已关闭的旧 epoch）。");
}
```

同 epoch 仍允许（语义＝`Clear()`），因此不阻断"同一会话内重新起算历史"的合法用法；只有**倒退**被拒。同时补齐 `Reset`/`Clear` 的 XML 文档（含 `Clear` 高水位限制，见 §5.2）。

**负向注入证据**（临时撤掉该 guard，其余代码不变）：

```
通过: 255 / 失败: 9
[FAIL] R1.1 Reset 到更小 epoch → 抛 ArgumentOutOfRangeException（拒倒退）
[FAIL] R1.2 抛异常后 epoch 未被改写（期望 7，实际 6）
[FAIL] R1.3 抛异常后样本仍在（未清空）（期望 1，实际 0）
[FAIL] R1.4 倒退未生效：原 epoch 记录仍可用
```

即注入态下 epoch 真的变成 6、样本真的被清空 —— 缺陷可复现、回归可判定。

### 3.2 修复 2（真 fail-open）：非有限 `WorldNowMs` 静默关闭墓碑窗口

**缺陷**：墓碑判定是 `state.TombstoneUntilMs > 0.0 && request.WorldNowMs > state.TombstoneUntilMs`。`WorldNowMs = NaN` 时比较恒 false ⇒ **过期永不成立**，"停止后超墓碑的迟到 Verify"被当合法包继续走 L2/L3，在几何命中时给出 Confirmed 命中（负向注入实测`Confirmed + 1 hit`）。这直接违反契约「只在墓碑内接受合法迟到Verify，超窗明确过期」，也与本文件头部自述"数值溢出 fail closed"矛盾。`+Inf` 同理会绕过上游裁剪与时间基准。

**修改**（`PMProjectileValidator.cs:225-230`）：入参结构前置处整包 fail-closed：

```csharp
if (IsNonFinite(request.WorldNowMs))
{
    return Reject(PMProjectileRejectReason.InvalidArgument, reports, false);
}
```

不新增枚举值（共享类型不动），复用 `InvalidArgument`；`VerifyConsumed=false`（尚未进入验证，不消耗 5 次配额）。

**负向注入证据**：

```
[FAIL] R7.2 WorldNowMs=NaN → Rejected（否则墓碑比较恒 false、窗口静默失效）（期望 Rejected，实际 Confirmed）
[FAIL] R7.3 拒因 InvalidArgument（期望 InvalidArgument，实际 None）
[FAIL] R7.4 非有限时钟不消耗配额
[FAIL] R7.5 非有限时钟零 hits（期望 0，实际 1）
```

**对照断言（修复后仍成立）**：窗口内（`worldNow < until`）受理；有限极大时钟（`double.MaxValue`）⇒ `TombstoneExpired`（fail-closed，而非静默放行）。

### 3.3 修复 3（可观测性，不改变判定）：`ExtraDeferMs` 超 TTL 只 Report

`extraDefer` 同时放大 L2 偏差预算（`speed*(2*rewind+100+extraDefer)/1000`）与帧锚新鲜度窗口（`600+extraDefer`）。正常值由 DS 按挂起 Spawn 实测，必然 ≤ `PMProjectileLimits.PendingTtlMs`（2000ms）；一旦传入更大的值，"预算被放大到接近无约束"这件事在输出里完全不可见。

**修改**（`PMProjectileValidator.cs:332-338`）：`deferMs > PendingTtlMs` 时追加 `extra-defer-exceeds-pending-ttl:<n>`，**只 Report 不 Reject**（不引入新语义、不改判定）。负向注入证据：撤掉该分支时 `R17.3` 失败。

### 3.4 文档级修正（无行为改动）

- `PMProjectileHistory` 类注释补「**非线程安全**：只在 DS 主线程使用」（`PMProjectileHistory.cs:44-48`）——契约 D13 要求局内权威在主线程，但原文档未言明，而内部 `Dictionary` + 环形缓冲无锁。
- `PMProjectileValidateResult.IsSettleable` 注释补三条**调用方约定**（`PMProjectileValidator.cs:164-176`）：`Status==Confirmed` 只表示激活可信、结算前必须看 `IsSettleable`；`Pending` 的 `Hits` 是候选、**确认后不得重跑 `Validate`**；结算后调用方必须把命中并入 `state.HitTargets`。这三条是"纯验证 + 无状态"设计的必然推论，但原文档未写，属 B/A1 集成最容易踩空的地方（测试 R12.1–R12.4 已把"重跑会再消耗一次配额"固化为可判定事实）。
- `PMProjectileValidator` 头部 L1 段落改写为「0) 时钟必须有限；配额在 0)①② 之后消耗；结构性非法不消耗」，与实现一致（原表述见 §5.1 更正）。

---

## 4. 测试与验收

### 4.1 命令与结果

```bash
dotnet build Tools/PMProjectileValidationCheck -c Release   # netstandard2.0 + C#7.3，零 UnityEngine
dotnet build Tools/PMProjectileValidationTest  -c Release   # net8.0
dotnet Tools/PMProjectileValidationTest/bin/Release/net8.0/PMProjectileValidationTest.dll
```

| 命令 | 结果 |
|---|---|
| `PMProjectileValidationCheck` 构建 | **0 警告 / 0 错误** |
| `PMProjectileValidationTest` 构建 | **0 警告 / 0 错误** |
| 运行 | **通过 264 / 失败 0，退出码 0**（上一轮 175 ⇒ 本轮 +89） |

### 4.2 新增回归分组 `T5A2-R`（89 项；`Tools/PMProjectileValidationTest/Program.cs`）

| 组 | 覆盖 | 关键断言 |
|---|---|---|
| R1 (9) | `Reset` epoch 单调 | 倒退抛异常且状态不被改写；同 epoch 允许；升 epoch 后旧 epoch 记录/请求仍拒 |
| R2 (6) | `Clear()` 高水位（限制锁定） | Clear 后低版本 stream 被当新流接受（1.4→1.1）；对照未 Clear 时 `stale-stream` 拒 |
| R3 (10) | 未定义 enum / 错域帧 | 未定义 `PMActivationResult`→`Pending`；未定义 Origin→`StateKeyInvalid`；未定义帧域作 SF/OF→记录期拒；**错域锚不抛异常且退 `Rewind`**（补齐契约 T5A4「错域帧」） |
| R4 (8) | L0 三态 + activation0 边界 | 非 0 激活三态；`ServerDirect+0` 恒 Confirmed（含账本 Rejected）；`ServerDirect` 但非 0 仍由账本裁决 |
| R5 (9) | provider 三态错配 | 错 epoch/netId/stream 全部 `StreamMismatch` 且 **filter 未被调用**；真实历史对错 stream 只能报 `HistoryUnavailable`（诊断粒度限制） |
| R6 (7) | Verify 计数口径 | `-3→0` 消耗、`4` 消耗、`5` 拒且不消耗、`>100 目标`**不消耗**、`L2 超预算`**消耗** |
| R7 (7) | 时钟有限（修复 2） | `NaN/+Inf`→`Rejected/InvalidArgument` 且零 hits 不消耗；`MaxValue`→`TombstoneExpired` |
| R8 (3) | 墓碑语义边界 | `TombstoneUntilMs=0` ⇒ 极晚时刻仍受理且产出命中（锁定「0=未设置」）；非有限墓碑只 Report |
| R9 (3) | 溢出 fail-closed | 极端半高致中心线端点溢出 ⇒ 0 命中 + `NumericOverflow`；极大 `spec.Radius` ⇒ 仍命中（登记 Spec 无上限） |
| R10 (2) | 非障碍验证边界 | 10m 线段穿中心线即命中（无遮挡输入）；>20m 整包拒（代价如实登记） |
| R11 (4) | 白名单/去重 | 空白名单放行、非空不含目标 `NotInWhitelist`、已命中 `DuplicateAlreadyHit` 且不走 filter |
| R12 (4) | 无状态 + Pending 约定 | 同输入两次结论一致、各自消耗配额（⇒不得重跑）、候选带 resolution、结果无共享实例 |
| R13 (3) | 批量边界 | 100 通过（100 条 skip、0 命中）、101 触发 `TargetCountExceeded` |
| R14 (3) | batch 身份四元组 | origin / epoch / projectileId 各自不同 ⇒ `BatchKeyMismatch` |
| R15 (5) | Rejected 零输出 | 零 hits/零 skip/不可结算；两次 Rejected 不共享数组实例 |
| R16 (2) | provider 未定义 `Resolution` | 不崩、按命中处理、不误报 `history-degraded-to-current` |
| R17 (4) | extraDefer 可观测（修复 3） | `2000` 不报、`2001` 报且仍受理、负值报 |

既有 T5A4-1..5 / T5A5-1..8 共 175 项**全部保持通过**（修复只影响 `Reset` 倒退与 `WorldNowMs` 非有限两个此前无测试覆盖的入口）。

### 4.3 负向注入（测试有效性证明）

流程：`cp` 备份两源文件到临时目录 → 用脚本撤掉 §3.1/§3.2/§3.3 的改动 → build + run → `cp` 逐字节还原 → `sha256sum -c` 校验（3 个文件全部 OK）→ 复跑得 264/0。注入态结果：**通过 255 / 失败 9**，失败项全部落在 R1.1–R1.4、R7.2–R7.5、R17.3，即两处修复与一处诊断各自**都有可判定的回归**，不存在"测试随实现一起漂移"。

---

## 5. 明确不做 / 已登记的限制（诚实边界）

### 5.1 更正上一轮报告：Verify 配额与目标数检查的**顺序**并非"先++再查目标数"
上一轮 §4.3 写「配额在通过 ①② 之后消耗（与 UE 的「先 ++ 再查目标数」同序）」；§5 表格也据此表述。按 UE 原文（`_r5_semantics_survey` §1.5 L1：「①生命周期；②`++VerifyHitRPCCount > 5`；③`HitTargets.Num() > 100`」）确实是先 ++ 后查目标数，但**本实现不是**：目标数（及全部结构性校验）在**入口硬门**阶段、配额检查之前完成，因此"目标数 > 100"的包**不消耗**配额（测试 R6.5 固定）。
口径真相（本轮已写入文件头与测试）：**结构性非法（入参/元素/identity/activation/墓碑）不消耗；进入验证（L2/L3）才消耗，上限 5，绝无第 6 次**。该行为比 UE 更保守（畸形包不烧玩家配额），但**不是**同序，原表述需更正。总 128 的 pending verify 上限属 A1 挂起账本职责（契约「Verify-before-Spawn每key最多5、总128」），本层只实现每 key ≤ 5。

### 5.2 `Clear()` 丢弃 stream 版本高水位（同 epoch 内旧流回归的可能）
`Clear()` 清空 `_streams` 后，同一 netId 若再收到**低于**此前版本的记录，会被当作新流接受（R2 已锁定该行为）。按 D-R0-02，netId 在单会话内永不复用，正常链路不会产生这种记录；需要硬防时请用 `Reset(更新 epoch)` 升代，并把 key/stream 高水位交给 A1 注册表。**本轮按要求只登记、不机械扩**（不引入无界高水位字典，也不改有界性论证）。`Reset(相同 epoch)` 与 `Clear()` 等效，同样具备该性质。

### 5.3 更正上一轮 §7 一致性表的两处表述
- 「维度全部从 `PMProjectileLimits` 取，无重复字面量」：`MaxTargetStreams = 64` 在**共享契约里没有对应常量**，只能在 `PMProjectileHistory` 内以自有常量表达（`PMProjectileHistory.cs:52`）。这是共享契约的字段缺口，不是本组重复字面量；建议后续把 64 升进 `PMProjectileLimits`（需主侧改共享类型，本轮不动）。
- **`PMProjectileLimits.MuzzleToleranceM = 10f`（枪口约束，契约常量）在全仓库无任何使用者**（`grep -rn MuzzleToleranceM --include=*.cs` 只有定义行；A1 两文件、A2 两文件均未用）。它既未写进 `net-r5-projectile-contract.md` 的 L0–L4 条文，也未在上一轮报告中出现 ⇒ **既无实现也无归属**。请主侧裁定：是 L2 的子检查（如「客户端 `P_prev` 与权威出生点距离 ≤ 10m」）、A1 生成侧检查、还是后置；**本轮不臆造该检查**（臆造可能误拒合法命中）。

### 5.4 `OutputFrame` 跨 `AuthorityServer` 帧倒退**未拦**（结论：不改，登记）
同帧分支会比较 `OutputFrame`（更旧输出＝迟到重复 ⇒ 忽略并计数）；**跨帧**只由 `ServerFrame` 与 `WorldTimeMs/TotalSimTimeMs` 单调性把守，`OutputFrame` 倒退还带更高 `ServerFrame` 的记录会被接受（R2/帧锚测试未覆盖此形，本轮用 R3/新断言定性）。判定**不构成位置/时间口径缺陷**：位置与时间由 WorldTimeMs/TotalSimTimeMs/ServerFrame 三重单调保证，`OutputFrame` 只用于同帧"取最后输出"的并列裁决。风险等级低；`_r5_host_survey` §A 显示 DS 侧历史 recorder **尚不存在**，其取值来源 `OutputBoundary` 在单一 stream 内单调。**建议**：B/C 写 recorder 时把"OutputBoundary 同 stream 内单调"写进 recorder 契约并加断言，届时再决定是否在 `Record` 内硬拒倒退（现在硬拒有误拒合法样本的风险，故不做）。

### 5.5 L0 信任排序：`ActivationId==0 && ServerDirect` 忽略**显式** `Rejected`
现状被上一轮测试与契约「激活ID=0仅可信ServerDirect可用」固定，本轮**保持不变**。残留风险＝若 B 对 0 号激活传入 `PMActivationResult.Rejected`（语义上自相矛盾），本层仍给 `Confirmed`（R4.6 固定该行为）。**这是刻意的"0=无账本行"哨兵语义**，不是号段免校验（与 UE 的 `<=32767 恒 Confirmed` 缺口不同：非 0 激活一律要账本裁决，R4.8）。给 B 的硬前置：**不得对 `ActivationId==0` 传 `Rejected`**；若主侧希望"显式 Rejected 优先于 0 号信任"，只需把 `:354-357` 的 Rejected 判断上移到 `:342` 之前（一行改动），并同步改 R4.6/上一轮 T5A5-7 的期望值 —— 属语义决策，本轮不擅自变更。

### 5.6 白名单空数组语义 / 去重顺序
- `AllowedTargets` 为空数组（含 `null`）＝**未设置白名单 ⇒ 放行**（`FlyingState` 默认值就是空数组，若改成"全拒"则默认状态永不命中）。**无法表达"白名单为空、谁也不许打"**；如需该语义需扩展契约（例如加 `bool HasWhitelist`），本轮登记不动。
- 顺序为 `HitTargets 已命中 → 批内重复 → 白名单`；因此"已命中过且不在白名单"的目标报 `DuplicateAlreadyHit` 而非 `NotInWhitelist`。两者都不命中，仅诊断标签差异。
- 契约 I-7「白名单残留（池化复用后 `AllowedHitTargets` 继承上一生命周期）」是 **A1 职责**：本层只读 `state.AllowedTargets`，无法判断它是否属于本世代。同理 `HitTargets` 若在复用/接管时未清空，会造成**永久去重抑制**。给 A1/B 的硬前置：复用或接管时清 `HitTargets`/`AllowedTargets`/`ActivationId`，并在结算后把命中并入 `HitTargets`。

### 5.7 本层**不是**障碍/LOS 校验（不要按"穿墙防护"宣传）
L2 只是"与权威锚的距离 ≤ 预算"，L3 只是"子弹线段 ↔ 目标竖直中心线最短距离 ≤ threshold"。环境里没有墙体/遮挡输入 ⇒ **隔墙命中在本层既无法检出也不会被拒**（R10.1 如实锁定）。`P_prev↔P_cur` 的 20m 上限与 `VisualOffset` 的 20m 上限都是**抗伪造**预算，不是穿墙防护；代价是高帧位移 >20m 的合法高速弹会被整包拒（R10.2 固定该代价）。真正的 LOS/骨骼细校验与"完整可选骨骼校验后置"同属契约明列的未做项。

### 5.8 其它已核实但未修改的边界
- **`Spec` 无上限**：`spec.SpeedMps/RadiusM` 只挡非有限与负值、不设上界 ⇒ 极大半径/极高速度会把 L2/L3 的 threshold 与预算放大到近乎无约束（R9.3 锁定）。**前置条件：`Spec` 必须由权威提供**（客户端不得参与）。是否加上界属新语义，建议主侧裁定。
- **`TombstoneUntilMs == 0` ＝"未设置墓碑 ⇒ 不施加窗口"**（R8.1/R8.2 在极晚时刻仍产出命中）。给 A1 的硬前置：停止时必须写入真实墓碑值（`PMProjectileTombstone.ComputeTombstoneMs`）。
- **墓碑判定双实现**：A1 有 `PMProjectileVerifyAdmission.TombstoneExpired/Retired/UnknownKey/StaleEpoch` 预门，A2 L1 又按 `state.TombstoneUntilMs` 复核一次。两者必须以**同一** `TombstoneUntilMs` 为准，否则会出现"一个说过期、一个说有效"。建议 B：A1 预门为准，A2 的检查作为防御性复核，并把不一致计入诊断。
- **now-clamped rewind 在验证输出里不可观测**：`rewind` 请求时刻早于保留窗口时，历史钳到最旧样本并计 `ClampedRewinds`，但返回的 `Resolution` 仍是 `Rewind`（UE 同口径；契约只要求 `Current` 档暴露退化）⇒ 验证器侧只能靠 `Current` 判定"退化"。若需在验证输出区分 clamped-rewind，需扩展 `IPMProjectileTargetHistory`（共享接口冻结，本轮登记）。
- **`Memory/分配`**：每个首次出现的目标会分配 `512 × sizeof(PMProjectileTargetSample)`（约 45KB）的环形数组，64 目标上限约 2.9MB；首录即在 DS 主线程分配。够用但有优化空间（分块/预分配），本轮不改。
- **`Reports` 的 rewind 位置**：`rewind>1000` 的 Report 在元素校验**之前**写入且不提前 return（与 UE"Report 必须在元素校验之后"的字面顺序不同，效果等价）；上一轮已登记，本轮保持并继续由 R/T5A5-2 固定。
- 本轮未涉及：RPC 声明/复制承载（B）、Unity 宿主与表现（C）、R6 业务切换、L4 结算（属 A1/B）、实机双端（T45 仍 `PENDING_USER`）。

---

## 6. 与冻结契约的一致性自检

| 契约条款 | 本轮状态 |
|---|---|
| 只改边界内文件；共享 `PMProjectileContracts.cs` 与他组文件未改 | ✅ 仅 `PMProjectileHistory.cs` / `PMProjectileValidator.cs` / `Tools/PMProjectileValidationTest/Program.cs` + 本报告；`sha256sum -c` 校验过还原后的两个源文件 |
| 复用 `PMNet.Mover.PMVector3`（Y-up 米）与 `PMFrameId` 域隔离，不混算 | ✅ 未新增单位/帧类型；帧域只做等值判断（不用会抛异常的运算符），未定义域 fail-closed |
| `HistoryCapacity=512` / `HistoryAgeMs=1000` / `MaxRewindMs=500` / `AnchorAgeMs=600` / `TickBufferMs=100` / `HitToleranceM=0.30` / `MaxSegmentM=20` / `MaxVisualOffsetM=20` / `MaxVerifyCalls=5` / `MaxTargets=100` / `PendingTtlMs=2000` | ✅ 全部从 `PMProjectileLimits` 取（`PendingTtlMs` 为本轮新增引用，仅用于 Report 阈值） |
| 目标数 64 / 目标级去重 / 白名单 / alive / 墓碑截止 / ring 512 / 拒绝新目标 | ✅ 未改；64 的常量缺口见 §5.3 |
| 停止三分支、skip 只跳飞行、半胶囊中心线（halfheight−radius） | ✅ 未改 |
| `ImpactPoint` 只钳不拒 → 消毒后 Filter → 再几何；线段–线段双精度覆盖平行/退化 | ✅ 未改 |
| 数值溢出 fail closed（包级 Reject / 目标级 skip）；Pending 不结算；整包被拒不部分输出 | ✅ 溢出链复核通过（R9）；**新增时钟维度同样 fail-closed**（§3.2） |
| 重置需新 epoch、跨 epoch/stream 不串扰 | ✅ **本轮修复了唯一的反例**（Reset 倒退） |
| `netstandard2.0 + C#7.3` 零 Unity 依赖编译 | ✅ `PMProjectileValidationCheck` 0/0 |
| 「T45 不因本组改 PASS；实机 PENDING_USER」 | ✅ 本文不主张任何实机/跨端结论 |

---

*报告结束。全部结论来自上述文档与源码的只读检查 + 两个真实工程的 build/run；未启动 Unity、未提交、未跑 SVN、未递归委派。*
