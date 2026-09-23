# R6-C 客户端宿主接线：正式直线攻击输入 / 只读结算 HUD / 结果正常退场

> 范围：`Docs/plans/net-r6-combat-contract.md`「C 宿主接线冻结」+ 验收 T6C 的**客户端侧**。
> 只读消费：`_r6_network_report.md`（R6 driver API）、`_r6_network_review.md`（复核后的语义变化）、
> `net-r6-combat-contract.md`（A2 声明 / B 驱动冻结补充 / C 宿主接线冻结）、`_r6_core_report.md` 口径。
> 状态唯一源仍是 `net-architecture-migration.md`。
> **未启动 Unity；未执行 SVN/git 写操作；未提交；未递归委派；未改主计划、未改 Shared/生成产物/锁文件；
> 未改 `PMDsSessionHost.cs`（并行 DS 改动，按任务要求不碰）。**

---

## 0. 结论摘要

| 项 | 结果 |
|---|---|
| 语言面门禁 `Tools/PMClientCheck`（netstandard2.0 + C# 7.3，含 `PMUnity/**` + 两个宿主） | **0 警告 / 0 错误**，产物 `Tools/PMClientCheck/bin/r6-client-host/PMClientCheck.dll` |
| 语言面门禁 `Tools/PMUnityGlueCheck`（同语言面，含 `PMClientSessionHost.cs`） | **0 警告 / 0 错误** |
| 既有 R6 网络门禁回归 `PMR6NetworkCheck` / `PMR6NetworkTest` | 0/0 编译；**通过 383 / 失败 0**，exit 0（R6 driver 本轮逐字未改） |
| 真实 Unity 2019.4 DLL 面 `Tools/PMR4UnityCheck` | **2 错误（唯一缺口）**：`GUI` 未解析 → 见 §7 P1（**写边界之外**，一行 csproj 引用即可，已用临时工程证明修好即 0/0） |
| `python Tools/check_cs_braces.py`（4 个 .cs） | **PASS**（配平、深度非负、末尾归零） |
| 编码自检 | `.cs` 全部 UTF-8 **BOM + CRLF**；`PMUnityCombatHud.cs.meta` ASCII + **LF**；GUID 全仓库唯一（1 次） |
| 诚实边界 | **未启动 Unity**；实机/UDP/PhysX 面继续 UNVERIFIED（T46/T47 仍 PENDING_USER）；DS 侧接线属并行任务，本轮不动 |

---

## 1. 交付文件与编码

| 文件 | 变更 | 编码 |
|---|---|---|
| `Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs` | 修改（R6-C 接线：攻击输入 / HUD / 结果退场） | UTF-8 BOM + CRLF |
| `Client/Assets/Scripts/PMUnity/PMUnityCombatHud.cs` | **新增**（薄只读 IMGUI HUD，334 行） | UTF-8 BOM + CRLF |
| `Client/Assets/Scripts/PMUnity/PMUnityCombatHud.cs.meta` | **新增**，GUID `b7c4e1d9a2f34d8e9c5b6a0f17e2d384`（全仓库 `.meta` 中出现 **1** 次，已 grep 复核） | ASCII + LF |
| `Tools/PMClientCheck/ClientStubs.cs` | 修改：新增 `UnityEngine.GUI` 桩（`Box(Rect,string)` / `Label(Rect,string)`，真实签名对应 IMGUIModule） | UTF-8 BOM + CRLF |
| `Tools/PMUnityGlueCheck/UnityStubs.cs` | 修改：`KeyCode` 补 `G = 103`；新增 `Rect` 结构 + `GUI` 桩 | UTF-8 BOM + CRLF |
| `Docs/plans/_r6_client_host_report.md` | 本报告 | UTF-8 BOM + CRLF |

**桩件纪律**：只按**真实 Unity 2019 API** 补形状（`GUI.Box(Rect,string)` / `GUI.Label(Rect,string)` 已用
`UnityEngine.IMGUIModule.xml` 的 `<member name="M:UnityEngine.GUI.Box(UnityEngine.Rect,System.String)">` 逐字核对）。
**没有**为了让桩件通过而改生产代码（除必要接线外，`PMClientSessionHost` 的既有诊断/运动/投射物路径逐字保留）。

---

## 2. 契约条款 → 实现落点（C 宿主接线冻结）

| 契约原文要点 | 落点（文件内成员） |
|---|---|
| 「Client 构造 R5 后创建 R6Driver」 | `Enter`：`new PMR6CombatDriver(world, bridge, epoch, ProjectileDriver, **model = null**)`；构造失败即 `ReleaseSession` + 报错，不回旧链 |
| 「bind 所有已知及新 player」 | `EnsureProjectileBinding`：**先**绑 R5（并校验结果）**再**绑 R6；`R6.BindPlayer` 内含 R5 绑定，重复调用幂等 |
| 「OnReplicatedCreate 绑定 CombatDriver」 | `OnPlayerReplicated` → `EnsureMovementRig` → `EnsureProjectileBinding`（含 R6），并对 AP 立即做一次身份核对 |
| 「已有初值 CombatHeroId/TeamId/MaxHp，与 offer hero/team 核对，不允许旧 UI 自选」 | `VerifyOwnerIdentity`：复制 `CombatTeamId > 0` 才判；逐项与 `offer.Identity.HeroId/TeamId` 比较，不等即 `Fail`（整局失败，显式原因） |
| 「不能默认 hero0」「状态未 ready 明确禁开火」 | `IsCombatFireReady`：身份已核对 **且** `CombatHeroId/TeamId == offer` **且** `CombatMaxHp > 0` **且** 未死/未终局；不满足即**不开火**、只显示（`AttackDriverRejected` + `LastAttackError`） |
| 「F 普攻 / G 大招输入每 Update 读一次」 | `SampleCombatInputEdges`（`Input.GetKeyDown(KeyCode.F/G)`，每 Update 一次，与 Mover 边沿同纪律） |
| 「替换常规 TryDiagnosticFire，不再 double-send 诊断弹」 | **删除** `TryDiagnosticFire` 与 `FireEdgeBuffered` / `NextActivationId` / `LastFireWallMs` / `DiagnosticFires*` / `ProjectileFiresRejectedByWrap` / `LastFireError`；唯一开火路径 = `TryCombatAttack` |
| 「调用 R6.TryAttack(player,isSuper,normalized aimX/Z,同次 muzzle,predictionMs,now,out activation,out error)」 | `TryCombatAttack` → `combat.TryAttack(owner, isSuper, forward.X, forward.Z, origin, CombatAttackPredictionMs(=100), wallNowMs, out activationId, out error)` |
| 「world aim 由 Mover 真实 predicted yaw」 | `rig.Driver.GetPredictedSync()` → `forward = (sin yaw, 0, cos yaw)`；非 finite 即不开火（不猜方向） |
| 「muzzle offset .6」 | `origin = predicted.Position + forward * PMProjectileDiagnosticConfig.MuzzleOffsetM`（= 0.6m，复用既有唯一常量） |
| 「同次所有弹同 muzzle」 | 同一 `origin` 交给 `R6.TryAttack`（driver 内部 N 颗弹全部用同一 `position`/同一 `activationId`） |
| 「支持范围 planner 同步 gate」 | `PMCombatWeaponPlanner.TryBuild(owner.CombatHeroId, isSuper, forward.X, forward.Z, ...)`（与 DS 同一份 planner；本地拒绝只显示） |
| 「原 200ms 诊断门替换 planner.FireIntervalMs 客户端门」 | `wallNowMs - LastAttackWallMs < plan.FireIntervalMs` → 拒绝并显示（`AttackGateBlocked`），不写任何资源 |
| 「错误如 Unsupported/Mana 拒只显示不整局 Fault」 | 三类分开：`AttackPlannerRejected`（本地 planner）、`AttackDriverRejected`（未就绪/枪口非法/TryAttack 返回 false 即含 DS 侧不足资源）、`AttackGateBlocked`；**都不 Fault** |
| 「driver.IsFaulted 才 Fail」 | 只有 `combat.IsFaulted/IsDisposed`（含 R5 fault 透传）与 `Pump/FlushState` 抛异常才 `Fail`；DS 裁决的 `ClientCombatAttackResultV1` 由 R6 驱动消费（Accepted 记录 / Rejected 真实撤假弹），宿主不再把它升级为整局失败 |
| 「不本地扣 HP/Mana/Energy 或额外加第三权威」 | 宿主不写任何 `_combat*`；HUD 只读复制字段；`PublishCombatState` 在 AP 上仍被权限拒绝（未触碰） |
| 「Pump endpoint→R6.Pump(now)→Movement→R5 每子步 candidate/presentation→R6.FlushState(now)」 | `PumpActive` 的固定次序，见 §3 |
| 「所有 driver/core 同一单调 clock，不得复用 now 前后错差导致倒退」 | 每帧 `nowMs = (long)(Time.realtimeSinceStartup*1000f)` **只算一次**，同时喂 `endpoint.Pump` / `R6.Pump` / `R6.FlushState` / `R5.Pump`；不存在第二根时钟 |
| 「CombatDead 冻结对应 Mover」 | `UpdateCombatReplicationState` → `FreezeRigOnce(rig)`（幂等，只冻一次）；`TickAutonomousProxies` / SP 插值循环显式跳过 `DeathFrozen` 的 rig（不依赖驱动侧 frozen 语义） |
| 「MatchEnded/receivedresult 冻结全部并停止输入/候选而继续协议 Pump 等待退出」 | `FreezeOnMatchEnded`（`MovementFrozen`）⇒ `PumpMovement` 退化为只 `Driver.Update`；`PumpProjectiles` 冻结分支只 `Pump(0)`、不推进/不开火/不收集候选；R6/R5/端点继续 Pump |
| 「dead target 不进候选」 | `BuildProjectileTargets`：`CombatDead` ⇒ 跳过（`TargetsSkippedDead`）；`target.Alive = !CombatDead` 保留协议字段 |
| 「team 可据复制提前滤但 DS 最终判」 | 仅在**两侧都是已知队伍**（`localTeam > 0 && target.Team == localTeam`）时跳过（`TargetsSkippedTeam`）；未知（0）不当作同队 |
| 「R5 表现保留 diagnostic sphere 美术」 | `PMUnityProjectilePresentation.Create(ProjectileLabel(key))` 与 `DiagnosticSpecTemplate.HideOnStop` 逐字保留；**不声称**英雄子弹美术已迁移 |
| 「新增 PMUnityCombatHud 薄只读」 | `Client/Assets/Scripts/PMUnity/PMUnityCombatHud.cs`（见 §5），`Enter` 创建、`ReleaseSession`/`Stop` 释放 |
| 「宿主接入并正确 Dispose，meta 唯一」 | `session.Hud` 单实例；`ReleaseSession` 销毁会话那一份；GUID 唯一（§1） |
| 「ClientMatchResult 收到 Pump 幂等存储后 ACK 由 R6.Flush 发」 | 结果落库在 `R6.Pump`（`ApplyInboundMatchResults`），ACK 在 `R6.FlushState`（AP 分支 `FlushClientResultAck`）；宿主只负责「端点可用才调用」（见 §4） |
| 「static LastCombatOutcome/Winner/MatchId + 只读结果 event」 | `PMClientSessionHost.HasLastCombatResult / LastCombatOutcome / LastCombatWinnerTeamId / LastCombatMatchId / TryGetLastCombatResult(...) / event CombatResultObserved`（逐订阅者异常隔离） |
| 「已知合法 terminal 结果后 DS 正常退出/Endpoint idle close 不应当作 Fail」 | `OnEndpointFailed` 先判「终局已到/已入队」⇒ 只记日志；`PumpActive` 的 `ClientFailed` / `IsDisposed` / `Faulted` 三条早退分支都对终局做正常退场分支 |
| 「正常 Release 回原大厅（隔离 scene 卸载），防 endpoint.Dispose 重入 OnEndpointFailed」 | `EndSessionNormally`：先 `_active = null` 再 `Endpoint.Dispose()`，因此 `OnEndpointFailed` 再进来只会走 `_active == null` 直接返回；释放走既有 `ReleaseSession`（隔离物理场景/相机恢复/桥/世界） |
| 「保留只读终局 HUD 到下一 Enter/显式 Stop 清，确保不是泄漏无限创建」 | 终局时把 HUD **摘出**到 `static _retainedHud`；`Stop()` 与 `Enter()`（换局/新局）都调 `ReleaseRetainedHud()`；`_retainedHud` 最多 1 个实例 |
| 「未收到可信 terminal 时断网仍 Fail，不能任何 disconnect 当胜利」 | 所有「不当 Fail」分支都要求 `TerminalResultObserved` 或「R6 已入队/已保存结果」；其余照旧 `Fail` |
| 「Stop/nextEnter 销毁保留 HUD、事件取消」 | `Stop()` 覆盖「无会话但保留 HUD」的情况；`EndSessionNormally`/`Stop` 都退订 `PMR3Runtime.PlayerReplicated`；R6 的 `PlayerDied` **未订阅**（客户端不产生结算，见 §6 说明） |
| 「R6Dispose 在 R5Dispose 前」 | `ReleaseProjectiles` 首段：`CombatDriver.UnbindPlayer × N` → `CombatDriver.Dispose()` → 再 R5（Unbind → Dispose）→ Motion → 表现 |
| 「无旧链 BattleReview 伪造」 | 未引用 `BattleReview` / `BattleData` / `HYLDManger`；结果通道只有 `ClientCombatMatchResultV1` |

---

## 3. 每帧时序（实现后的真实次序，同一根 `now`）

```
PumpActive()                                   （Unity Update 唯一驱动点）
  ├─ session == null → return
  ├─ nowMs / nowUnixSeconds 各算一次（此后全帧复用）
  ├─ Faulted？      → 终局已落定 ⇒ EndSessionNormally；否则 FrozenWallClockPump（旧行为）
  ├─ Endpoint == null || IsDisposed？
  │                  → 终局已落定 ⇒ EndSessionNormally（DS 正常退场）；否则 Fail（旧行为）
  ├─ 终局已落定 且 now - TerminalResultWallMs ≥ 7000ms → EndSessionNormally（**有界退场**）
  ├─ Physics.SyncTransforms()                  （每帧一次，契约 §B3）
  ├─ endpoint.Pump(nowMs, nowUnixSeconds)      （入站 drain + bridge.Update + 客户端连接自检）
  ├─ Endpoint.ClientFailed？
  │     ├─ 终局已落定 ⇒ EndSessionNormally
  │     ├─ 终局**本帧**已到（已入队/已保存）⇒ 记告警并继续（不判失败）
  │     └─ 否则 ⇒ Fail（旧行为；未收可信终局时断网仍是失败）
  ├─ UpdateCombatReplicationState()
  │     ├─ VerifyOwnerIdentity()               （hero/team 与 offer 逐项核对，不一致 ⇒ Fail）
  │     ├─ CombatDead  ⇒ FreezeRigOnce(rig)     （冻结对应 Mover）
  │     └─ CombatMatchEnded ⇒ FreezeOnMatchEnded （冻结全部运动）
  ├─ SampleCombatInputEdges()                  （F / G 各采样一次）
  ├─ R6.Pump(nowMs)                            （① 固定次序第一步：上行攻击 / 裁决 / 结果 / ACK 入站 + core.Tick）
  ├─ 终局闭环检测：R6.TryGetStoredMatchResult 为真且未处置
  │     ├─ OnTerminalResult(...)                （推 HUD 结论 + static 快照 + 只读事件 + 冻结全部）
  │     ├─ R6.FlushState(nowMs)                 （补发 ACK；端点不可用则跳过）
  │     └─ 端点已关闭 ⇒ EndSessionNormally；否则 return（本帧不再推进子链）
  ├─ PumpMovement()
  │     ├─ MovementFrozen ⇒ FrozenWallClockPump（只 Driver.Update，不再 Tick/Advance/Apply）
  │     ├─ AP：PollHardware → 真实整毫秒子步（1..50，每帧 ≤8）→ 先 Sample 后 Tick、接受才 Consume →
  │     │      SendInputPayload（DeathFrozen 的 rig 跳过 Tick）
  │     └─ SP：Advance + SamplePresentation → ApplyInterpolated；AP：ApplyPredicted（DeathFrozen 跳过 Advance）
  ├─ PumpProjectiles(nowMs)
  │     ├─ 驱动 fault 检查 + 已有副本补绑（R5+R6）
  │     ├─ BuildProjectileTargets()            （死/同队提前滤；帧锚必须 AuthorityServer）
  │     ├─ MatchEndedFrozen || MovementFrozen ⇒ 累加器清零 + Pump(0) + 排空视图脏通道 + return
  │     ├─ TryCombatAttack(nowMs)              （F/G ⇒ 本地 planner 门 ⇒ R6.TryAttack）
  │     ├─ 固定 16ms 子步（≤8；零步也 Pump(0)）
  │     ├─ 每子步：R5.Pump(nowMs, step)
  │     │        → SyncProjectileViews（全量 CopyViews 差集）
  │     │        → CollectAndSubmitProjectileHits（冻结/终局时跳过）
  │     └─ DrainProjectileViewChanges（只排空，不用作真值）
  ├─ R6.FlushState(nowMs)                      （③ 固定次序末步：AP 只回 ServerCombatResultAckV1）
  ├─ SendPendingProbes()
  ├─ UpdateCombatHud()                        （只走 setter）
  └─ 心跳日志（含 DescribeCombat）
```

**时钟一致性**：`endpoint.Pump` / `R6.Pump` / `R6.FlushState` / 每个 `R5.Pump` 全部收到**同一个**
`nowMs`；`R5` 的推进量由 `stepMs` 单独表达（`nowMs` 只做单调水位），因此不存在「前后 now 错差导致倒退」。

---

## 4. 结果退场状态机（「收到结果 → 等待 DS 退场 → 正常回归大厅」）

```
                                        ┌─────────────── 战斗进行中（identity 核对 + 正常 Pump）
                                        │
       复制到 CombatMatchEnded ────────▶ MatchEndedFrozen = true
                                        │   全部运动冻结；停输入/候选；**协议继续 Pump**
                                        │
       R6.Pump 取出 ClientCombatMatchResultV1 ─▶ _clientResultStored（终局真值）
                                        │
   ┌────────────────────────────────────┴────────────────────────────────────┐
   │ 宿主 OnTerminalResult：                                                   │
   │   · TerminalResultObserved = true, TerminalResultWallMs = now             │
   │   · Win/Lose/Draw = (winnerTeamId vs 本队 TeamId)（winner 0 ⇒ Draw）        │
   │   · HUD.SetOutcome(true, 结果, winnerTeamId 原始值, 本队)                   │
   │   · 发布 static LastCombatOutcome / LastCombatWinnerTeamId / MatchId + 事件 │
   │   · FreezeOnMatchEnded（冻结全部；幂等）                                   │
   └────────────────────────────────────┬────────────────────────────────────┘
                                        │
                 ACK：R6.FlushState（端点可用才发；不可用则跳过，绝不因此 fault）
                                        │
   ┌────────────────────────────────────┴────────────────────────────────────┐
   │ 退出触发（任一命中即 EndSessionNormally，全部**不是失败**）：                │
   │   a) Endpoint.ClientFailed / IsDisposed（DS 正常退场 / 端点 idle 关闭）      │
   │   b) now - TerminalResultWallMs ≥ 7000ms（有界退场；DS 侧宽限 5000ms + 余量）│
   │   c) 本帧后续出现 fault（终局已落定 ⇒ 不再「冻结挂着」）                      │
   └────────────────────────────────────┬────────────────────────────────────┘
                                        │
   EndSessionNormally：_active=null → 退订 PlayerReplicated → HUD 摘到 _retainedHud
                        → Endpoint.Dispose（OnEndpointFailed 因 _active==null 直接返回，防重入）
                        → ReleaseSession（R6→R5→Motion→表现→相机恢复→隔离物理场景卸载→桥/世界）
                        → PMClientSessionHostDriver.Release()
                                        │
   回到原大厅（原场景一直在；只读终局 HUD 继续显示） ──▶ 下一 Enter 或显式 Stop ⇒ HUD 销毁 + 结果快照清除
```

**未收可信终局的断网**：`Endpoint.ClientFailed` / `IsDisposed` / 其他 fault 一律照旧 `Fail`
（`Faulted = true` + 冻结运动 + `FrozenWallClockPump`），**不产生任何胜负结论**
（`_lastCombatResultValid` 保持 false）。

---

## 5. 只读 HUD：`PMNet.Unity.PMUnityCombatHud`

```csharp
public enum PMCombatHudOutcome { None = 0, Win = 1, Lose = 2, Draw = 3 }

public sealed class PMUnityCombatHud : MonoBehaviour, IDisposable
{
    // 创建（DS 直接拒绝；失败返回 null + 原因，不抛）
    public static PMUnityCombatHud Create(string matchId, out string error);

    public bool IsDisposed { get; }
    public string MatchId { get; }

    // 只读输入面：**只能读 setter 传入值**，不读旧 BattleData / 不写任何资源
    public void SetMatchId(string matchId);
    public void SetLocal(int hp, int maxHp, int mana, int superEnergy);
    public void SetReady(bool ready);                                  // 宿主判据，HUD 不自行比较数值
    public void SetNotice(string notice);                              // 本地 planner / DS 裁决的拒绝原因
    public void SetOutcome(bool hasOutcome, PMCombatHudOutcome outcome, int winnerTeamId, int localTeamId);

    public void Dispose();          // 销毁宿主 GameObject（幂等；只释放自己）
    public string Describe();       // 单行诊断（不含敏感数据）
}
```

**显示内容**（`OnGUI` + `GUI.Box` / `GUI.Label`，真实 Unity 2019 IMGUI）：

```
┌ R6 Combat  <matchId> ─────────────────────────┐
│ HP      1180 / 1180                            │
│ Mana    90                                     │
│ Energy  0                                      │
│ F = 普通攻击   G = 大招   [就绪]                 │
│ 最近拒绝：<无>                                   │
│ 结果：进行中（等待 DS 终局结果）                   │
└────────────────────────────────────────────────┘
```
* 零 Canvas、零 Material、零 Prefab、零资产编辑；面板尺寸由「行数 × 行高 + 标题」算出，文本只在 setter 里重建
  （`OnGUI` 一帧可能被调用多次，不在其中拼字符串）。
* `DontDestroyOnLoad` 宿主对象：[PMUnityCombatHud]——不属于任何业务场景，隔离战斗场景卸载不会带走它。
* 终局后由宿主继续只读显示（SetOutcome 后 `SetReady(false)`），直到下一 Enter / 显式 Stop。

---

## 6. 关键设计判定（与契约解释有关，显式记录）

| # | 判定 | 理由 |
|---|---|---|
| D1 | 终局语义取「**冻结全部 + 继续协议 Pump + 等 DS 退场/有界超时**」而不是「收到结果立刻释放」 | 契约原文「MatchEnded/receivedresult 冻结全部并停止输入/候选而**继续协议 Pump 等待退出**」+「正常 Release 回原大厅」+「已知合法 terminal 结果后 DS 正常退出/Endpoint idle close 不应当作 Fail」。ACK 也必须先发出去 DS 才能结束自己的结果闭环 |
| D2 | `TerminalExitGraceMs = PMCombatLimits.ResultGraceMs + 2000 = 7000ms` | DS 侧「全 ACK 或 5000ms 宽限」一到就 SubmitResult/退出；而客户端传输层空闲看门狗默认 10s，只靠它会让玩家在终局画面白等 10s。超时只作**有界退场**，不宣称「客户端都观察到了」 |
| D3 | 端点不可用时**跳过** `R6.FlushState` 的 ACK，而不是让它抛 | 发给一个已经没人收的 socket 必然抛 → R6 `SendFailed` fault → 把「DS 正常退场」变成失败退场，正好违反契约要求 |
| D4 | 终局判决放在 `R6.Pump` 之后、**任何 fault 短路之前** | 「同帧发结果 + 关会话」与「R6 保存结果后自身某处 fault」两种次序都不能把一份已到达的合法终局降级为失败 |
| D5 | 客户端**不订阅** `R6.PlayerDied` | 该事件只在权威侧由结算产生（`PMR6CombatDriver` 只在 `_isServer` 时订阅 Settlement）；AP 永远收不到 ⇒ 订阅等于死接线。客户端死亡真值走复制字段 `CombatDead`（首次为真即 `Freeze` 对应 Mover） |
| D6 | `target.Alive` 由复制字段决定（恒为 true，因为死者已在上面被滤） | 保留该字段是因为命中协议本身要求它；DS 侧还会用 `model.Dead/Connected` 复核。客户端**不做**存活裁决 |
| D7 | `AttackDriverRejected` 同时统计「未就绪/枪口非法」与「`TryAttack` 返回 false」 | 两者都是「本次没打出去、只显示不 fault」；分开计数价值低、且 `TryAttack` 的 error 文本已进 HUD |
| D8 | 同队提前滤仅在「本队 > 0 且目标队 == 本队」时生效 | 未就位时 `CombatTeamId == 0` 表示「未知」；把未知当同队会误滤真实敌人 |
| D9 | `EndSessionNormally` **刻意不调** `PMClientSessionHostDriver.Release()` | Unity 的 `Object.Destroy` 是**帧末延迟**执行；它触发的 `OnDestroy → Stop()` 会在本方法返回、`_stopping` 已复位之后才跑，那时会走「无会话」分支，把刚保留的只读终局 HUD 与结果快照一起清掉 —— 正好违反契约。保留该 per-frame driver 无害：`_active == null` 时 `PumpActive()` 直接返回，HUD 的 `OnGUI` 由 Unity 自行驱动，下一 Enter 复用同一实例 |
| D10 | `PMClientSessionHostDriver.OnDestroy` 只在 `ReferenceEquals(_instance, this)` 时收尾 | 同一个延迟销毁竞态的反面：「先 Stop/Release，再在同一帧 Enter 新局」时，旧实例的延迟 `OnDestroy` 不得把**新**会话（与新 HUD）收掉。在既有流程上行为等价（正常退出的收尾已由 `Stop()` 当时同步做完），只消除竞态 |

---

## 7. 验证与回归（本批实测）

| 门禁 | 结果 |
|---|---|
| `dotnet build Tools/PMClientCheck/PMClientCheck.csproj -c Release -o Tools/PMClientCheck/bin/r6-client-host --no-incremental` | **0 警告 / 0 错误**；产物 `PMClientCheck.dll` + `pdb` |
| （产物自证）编译产物里同时含两端新代码 | `最近拒绝：`/`R6 Combat`（HUD 的 UTF-16 字符串）与 `开火间隔未到`（宿主的 UTF-16 字符串）各命中 1；`PMUnityCombatHud`/`TryCombatAttack`/`EndSessionNormally`/`DescribeCombat`/`CombatResultObserved` 均在元数据中 |
| `dotnet build Tools/PMUnityGlueCheck/PMUnityGlueCheck.csproj -c Release --no-incremental` | **0 警告 / 0 错误** |
| `dotnet build Tools/PMR6NetworkCheck/PMR6NetworkCheck.csproj -c Release` | **0 警告 / 0 错误** |
| `dotnet build Tools/PMR6NetworkTest/PMR6NetworkTest.csproj -c Release` + 运行 | **0 警告 / 0 错误**；**通过 383 / 失败 0**，exit 0 |
| `python Tools/check_cs_braces.py <4 个 .cs>` | **PASS**（453/453、46/46、1118/1118、325/325 花括号配平；深度全程非负、末尾归零） |
| `dotnet build Tools/PMR4UnityCheck/PMR4UnityCheck.csproj -c Release`（**真实 Unity 2019.4 DLL** 面） | **2 错误（唯一缺口）**：`PMUnityCombatHud.cs(327,13)` / `(328,13)` `error CS0103: 当前上下文中不存在名称"GUI"`；**其余全部通过**（含 `PMClientSessionHost.cs` 的全部改动与 `PMUnity/**` 其余文件） |

### 7.1 `PMR4UnityCheck` 的 `GUI` 缺口（P1，**写边界之外**，需主侧一行修复）

* 该门禁引用的是**真实 Unity DLL**（`D:\Unity\2019.4.8f1\Editor\Data\Managed\UnityEngine\*.dll`），
  但它**没有**引用 `UnityEngine.IMGUIModule.dll`；`UnityEngine.GUI` 正属于该模块
  （已核对 `UnityEngine.IMGUIModule.xml` 含 `<member name="M:UnityEngine.GUI.Box(UnityEngine.Rect,System.String)">`
  与 `...Label(UnityEngine.Rect,System.String)`），`UnityEngine.dll` 门面也**没有**转发 `GUI`（否则会报 CS1069/CS0012 而不是 CS0103）。
* 我在 `%TEMP%` 里建了一个**仓库外**的最小工程（引用同一批真实 Unity DLL，只编 `PMUnityCombatHud.cs`）复现了完全相同的 2 条错误；
  仅在其中加一条 `<Reference Include="UnityEngine.IMGUIModule">` 后 → **0 警告 / 0 错误**。
* 需要主侧在 `Tools/PMR4UnityCheck/PMR4UnityCheck.csproj` 的「真实 Unity 汇编」那个 `ItemGroup` 里补：

```xml
    <Reference Include="UnityEngine.IMGUIModule">
      <HintPath>$(UnityManagedDir)\UnityEngine\UnityEngine.IMGUIModule.dll</HintPath>
      <Private>false</Private>
    </Reference>
```
* 该文件**不在**本任务的写入边界内，故保持只读；**没有**为了让门禁变绿而把 HUD 改成绕过真实 IMGUI 的写法。

### 7.2 变异/反证（说明这次改动确实被门禁覆盖）

| 反证 | 结果 |
|---|---|
| 两条错误全部指向 HUD 的**两个** `GUI` 调用点（`PMUnityCombatHud.cs:327` = `GUI.Box`、`:328` = `GUI.Label`），`PMR4UnityCheck` 未给出任何其它错误 | 证明缺口**只**来自本批新增的 HUD 绘制面，与宿主接线无关 |
| 把 `PMR4UnityCheck` 的输出与 `PMClientCheck` 对比 | `PMR4UnityCheck` 除这 2 条外**无任何**关于 `PMClientSessionHost.cs` 的错误 ⇒ 宿主的新 API 用法（`KeyCode.G`、`PMTransportConnection.IsReady`、`PMR6CombatDriver` 全量成员、`PMCombatWeaponPlanner`、`PMCombatHudOutcome`）在**真实 Unity API 面**上成立 |

### 7.3 自审中发现并修掉的两个真实缺陷（延迟销毁竞态）

| # | 缺陷 | 处置 |
|---|---|---|
| F1 | `EndSessionNormally` 最初调了 `PMClientSessionHostDriver.Release()`；Unity 的 `Destroy` 帧末才触发 `OnDestroy → Stop()`，而那时 `_stopping` 已复位 ⇒ `Stop()` 会**销毁刚保留的终局 HUD 并清空结果快照**（直接违反「保留到下一 Enter / 显式 Stop」） | 移除该调用并写明理由（D9）；per-frame driver 保留至下一 Enter / 显式 Stop，`_active == null` 时它每帧只是空转返回 |
| F2 | 同类竞态的反面：「Stop（或 Release）后**同帧** Enter 新局」时，旧实例的延迟 `OnDestroy` 会 `Stop()` 掉**新**会话 | `OnDestroy` 加 `ReferenceEquals(_instance, this)` 门（D10）；既有流程行为不变 |

两条都是**只能靠读源码发现**的时序问题（编译面/门禁都看不出来），已在代码注释与本节分别标注。

---

## 8. 未验证 / 诚实边界（PENDING_USER）

| # | 项 | 说明 |
|---|---|---|
| U1 | **实机（Unity 编辑器/真机）** | 本任务禁止启动 Unity。HUD 的绘制观感、`DontDestroyOnLoad` 宿主在退局后的存活、隔离物理场景卸载后的相机恢复，均只在**编译面**成立，未在实机观察 |
| U2 | **真 UDP / 跨机** | 未做端到端实机联调：`ClientCombatMatchResultV1` → ACK → DS `ResultReadyForLobby` → DS 退场 → 客户端正常回归大厅 的整链只有**读源码 + 门禁**支撑 |
| U3 | **DS 侧接线** | `PMDsSessionHost` 的 R6 接线（`CreateAuthorityPolicy` / 移除诊断 Settlement 订阅 / `PlayerDied` → freeze+`history.Alive=false` / `ResultReadyForLobby` → `Lobby.SubmitResult`）属并行任务，本任务**按指示不碰该文件**，也未验证 |
| U4 | **R6 完整玩法** | 特殊技能 / 爆炸 / 弹射 / 穿透 / 位移 / 道具 / 完整 UI / 回放仍后置；摇杆输入后置（本批只 F/G）。T46/T47 继续 PENDING_USER |
| U5 | `TerminalExitGraceMs` 的实测取值 | 7000ms 由「DS 5000ms 宽限 + 余量」推得，**未经实机计时**；若实机发现 DS 退场更慢，应调大 |
| U6 | 同队提前滤的收益 | 未量化（只减少上行配额消耗）；DS 仍是最终裁决 |
| U7 | `PMR4UnityCheck` 的 GUI 引用 | 见 §7.1（P1，主侧一行修复） |

---

## 9. 复现命令

```bash
# 语言面（本任务指定产物路径；与 DS 侧 PMR4UnityCheck 互不影响）
dotnet build Tools/PMClientCheck/PMClientCheck.csproj -c Release -o Tools/PMClientCheck/bin/r6-client-host
dotnet build Tools/PMUnityGlueCheck/PMUnityGlueCheck.csproj -c Release

# R6 网络门禁回归（本轮未改 R6 driver）
dotnet build Tools/PMR6NetworkCheck/PMR6NetworkCheck.csproj -c Release
dotnet build Tools/PMR6NetworkTest/PMR6NetworkTest.csproj -c Release
dotnet Tools/PMR6NetworkTest/bin/Release/net8.0/PMR6NetworkTest.dll     # 通过 383 / 失败 0

# 真实 Unity 2019.4 API 面（**需主侧先补 IMGUIModule 引用**）
dotnet build Tools/PMR4UnityCheck/PMR4UnityCheck.csproj -c Release

# 括号/编码
python Tools/check_cs_braces.py \
  Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs \
  Client/Assets/Scripts/PMUnity/PMUnityCombatHud.cs \
  Tools/PMClientCheck/ClientStubs.cs \
  Tools/PMUnityGlueCheck/UnityStubs.cs
```

---

## 10. 已检查范围

**完整读取的文档（按委派顺序）**：`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`（全文）
→ `Docs/plans/net-r6-combat-contract.md`（全文，含尾「B 驱动冻结补充」「C 宿主接线冻结」）
→ `Docs/plans/_r6_network_report.md`（全文，含 §2 公开 host API、§4 宿主固定次序）
→ `Docs/plans/_r6_network_review.md`（全文，含 §3 host API 变化 / 语义变化）
→ `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`（全文）。

**为落点而对读的真实实现**：`PMClientSessionHost.cs`（全文，2263→3246 行）、`PMR6CombatDriver.cs`
（ctor/只读视图/`BindPlayer`/`UnbindPlayer`/`AddPlayer`/`TryAttack`/`IsAliveForAttack`/`Pump`/`TickModel`/
`HandleServerAttack`/`HandleAttackResult`/`FlushState`/`PublishStateIfDirty`/`UpdateOutcome`/`SendMatchResult`/
`FlushClientResultAck`/`UpdateClock`/`CreateAuthorityPolicy`/`Dispose`/`Describe`）、`PMR5ProjectileDriver.cs`
（公开面：`BindPlayer`/`LocalOwner`/`LocalUid`/`TryFire`/`ResolveActivation`/`Pump`/`CopyViews`/`DrainViews`/
`CancelPredictedActivation`/`Dispose` 与容错口径）、`PMR3Player.cs`（战斗声明段、`PublishCombatState`、
`CombatStateUpdated`、四条 RPC 桩）、`PMCombatContracts.cs`（`PMCombatAttackPlan.FireIntervalMs`、
`PMCombatLimits`、`PMCombatEndReason`、`PMCombatPlayerSnapshot`）、`PMCombatWeaponPlanner.cs`（文档头 + `TryBuild`）、
`PMCombatSession.cs`（`Started`/`Ended`/`Outcome`/`Tick` 签名）、`PMDsEntryOffer.cs`（`Identity.HeroId/TeamId`）、
`PMUdpSessionEndpoint.cs`（`Pump`/`Dispose`/`ClientFailed`/`IsDisposed`/`CheckClientSessionAfterUpdate`/`RaiseFailed`）、
`PMTransportConnection.cs`（`IsReady` = `_activated && Transport.IsConnected`）、`PMTransportTypes.cs`
（`IdleTimeoutMs = 10000`）、`PMPredictionTimeline.cs`（`TickCore` 的 frozen 分支）、`PMR4MovementDriver.cs`
（`Tick`/`Advance`/`SamplePresentation`/`Freeze` 的 frozen 语义）、`PMProjectileCandidateCollector.cs`
（`TryCollect` 的 `TargetNotAlive` 判据）、`PMProjectileDiagnosticConfig.cs`（`MuzzleOffsetM = 0.6f`）、
`PMUnityProjectilePresentation.cs`、`Tools/PMClientCheck/*.csproj`、`Tools/PMUnityGlueCheck/*.csproj`、
`Tools/PMR4UnityCheck/*.csproj` 与 `HostDependencies.cs`、`UnityEngine.IMGUIModule.xml`（真实 API 签名核对）。

**本次写入的文件**：§1 表中 6 个（3 源 + 1 meta + 1 报告 + 2 stub 文件中的 2 个改动）。

**本次未做**：未启动 Unity；未做 SVN/git 写操作；未提交；未递归委派；未改 `PMDsSessionHost.cs`、
`PMR6CombatDriver.cs`、`PMR5ProjectileDriver.cs`、`PMR3Runtime.cs`、`PMR3Player.cs` + 生成产物、
`PMCombat/**`、`Shared/**`、主计划、主计划之外的任何 `Docs/` 或 `Tools/*.csproj`。
