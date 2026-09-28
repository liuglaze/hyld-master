# T-PLAY4 输入组：旧局内 UI / 摇杆的**后端输入 API** 落地报告

范围：只落地主计划冻结的**输入侧后端接口**（`PMClientSessionHost` 三个公开入口 + `PMUnityMoverInput`
的屏幕→世界变换与来源选择 + 对应可执行/静态测试）。**不做** UI prefab / Canvas / 美术 / sprite 抽取
—— 那是表现 UI 组（新建 `PMUnityBattleControls.cs`）的范围，本轮不改它、也不为编译它造任何假类。

本轮实际写入（**仅**这 5 个文件）：

| 文件 | 变更 |
|---|---|
| `Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs` | 新增公开值类型 `PMUiCombatSnapshot`；新增三个冻结入口 `TrySetUiMove` / `TryQueueUiAttack` / `TryGetUiCombatSnapshot`；`TryCombatAttack` 支持「UI 瞄准向量独立于 Mover yaw」并统一单点消费三个边沿；`ClearUiInput` 接入冻结/死亡/释放路径；新增主线程门与 UI 计数 |
| `Client/Assets/Scripts/PMUnity/PMUnityMoverInput.cs` | 新增纯变换 `TryScreenVectorToWorld` / `TryScreenAimDirection`；新增 UI 摇杆有状态入口 `SetUiMoveWorld` / `ClearUiMove` / `HasUiMove` / 计数；`ReadPlanar` 内做**来源选择**（UI 优先，释放回键盘）；`Reset` 清 UI 摇杆 |
| `Tools/PMR4UnityAdapterTest/Program.cs` | 新增 I 节（屏幕向量：±轴/组合/NaN/超限/限幅/归一化 + 81 点属性扫描）、J 节（来源选择，**先失败后修**）、K 节（宿主 UI 入口静态契约与负例）、辅助 `ExtractMember` / `StripComments` / `CountOccurrences` |
| `Tools/PMR6NetworkTest/Program.cs` | 新增 W 节：在**真实 R6 字节链**上验证「一次攻击的 planner 方向 == 上行方向 == DS 方向槽」，含「换成旧 yaw 方向必须被拒」的反例 |
| `Docs/plans/_play_ui_input.md` | 本报告 |

未改：任何资产（`HYLDGame.unity` / `Remake/Player.prefab` / `Resources/PMNet/**` / manifest）、任何 DS 代码、
任何协议/生成物（ID 锁与 `ProtocolHash` 未动）、`PMUnityBattleControls.cs`（并行组新文件）、任何 `*.csproj`、
任何桩件（`ClientStubs.cs` / `UnityStubs.cs`）、`PMUnityCombatHud.cs`。
未执行：Unity / 服务 / 打包 / SVN / git 写操作 / 递归委派。
中文源码 UTF-8 BOM + CRLF（4 个源码/测试文件逐个核对：`efbbbf` 起头、`bare_LF=0`）。

## 1. 必读文档（按委派顺序读完）与它们如何决定入口

`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md` → `Server/AGENTS.md` →
`Docs/plans/net-architecture-migration.md` 末尾「用户选型冻结（T-PLAY1..5）」/「T-PLAY 第一阶段接口与验收细化」/
**「T-PLAY4 UI与输入接口冻结」** → `Docs/plans/net-r4-network-contract.md` → `Docs/plans/net-r6-combat-contract.md` →
`Docs/plans/_play_camera_facing.md` → `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`。

可验收的文档 → 入口映射：

- 主计划 T-PLAY4 冻结原文给出**三个函数签名**（`TrySetUiMove(float,float) bool`、`TryQueueUiAttack(bool,float,float) bool`、
  `TryGetUiCombatSnapshot(out ...) bool`）、**屏幕轴约定**（相对用户选定相机世界 yaw −90，screen up → world −X、
  screen right → +Z）、**拒纳条件**（非 finite/非本局/冻结/死者拒、摇杆释放清移动）、**单点消费**（主线程 Pump 统一消费）、
  **瞄准独立**（攻击摇杆独立决定弹道，F/G 仍沿预测 Yaw 兜底）、**快照字段**
  （Hp/MaxHp/Mana/SuperEnergy/Dead/MatchEnded/NormalReady/SuperReady/LastReject）与
  **「UI 永不直接发 RPC/写 HP/Mana/Energy/本地伤害」「同一次攻击 planner/枪口/上行同一向量」「重复/间隔门与拒绝处理不弱化」
  「Stop/结束/换局不带出上局 UI 输入」「拒绝时不制造重复网络 RPC」**。
  ⇒ 本轮实现**逐条**按这段冻结文字落地（见 §3/§5），选型未改（未改成第三人称/正交/瞄准带转身）。
- `net-r4-network-contract.md` B2 冻结「`PMUnityMoverInput` 提供**纯输入转换入口**（用于可控测试）」⇒
  屏幕→世界变换刻意落在 `PMUnityMoverInput` 的**纯静态**函数里（net8 可逐条断言），而不是写在宿主的 UI 入口里。
- `net-r6-combat-contract.md`「B 整合与宿主」冻结「`TryAttack` 先共用 planner 建本地计划，再 `ServerCombatAttackV1`，
  按计划 N 方向 `R5.TryFire`（同一 activation）；位置=predicted pos + base aim × 0.6，同次所有弹同 muzzle」、
  「本地 planner gate 仅优化，不写 HP/Mana/Energy，服务器裁决唯一」⇒ 本轮只改**瞄准向量的来源**（UI 时取 UI 向量），
  planner/上行/枪口/间隔门/DS 裁决链**一字未改结构**。
- `_play_camera_facing.md`（T-PLAY1/2）冻结相机世界 yaw = −90 且由 `PMBattleCameraGeometry.DefaultYawDegrees` 单一来源给出
  ⇒ 轴约定与它**同源绑定**：门禁 I11 用该常量反推「screen up == 相机水平前向」「screen right == 相机右向」，
  而不是硬编码一对巧合格子。
- `Client/Assets/AGENTS.md` §4（局内主链归属：宿主在 `Scripts/Server/Boot/`、Unity 适配在 `Scripts/PMUnity/`）、
  §6（不改烘焙输入）、§7（`PMR4UnityCheck` 是真实 Unity2019 API 编译门、门禁必须本次 build0 后 run0）、
  §8（禁止无范围暂存/代提交）⇒ 写入面与验证方式。

## 2. 动手前的只读核对（不是回忆）

| 核对对象 | 事实 | 影响 |
|---|---|---|
| `PMClientSessionHost.TryCombatAttack` | 瞄准方向 = `predicted.YawDegrees` 推的世界前向；枪口 = `predicted.Position + forward × 0.6`；上行 aim 与 planner 都用这个 `forward`；F/G 边沿在 `SampleCombatInputEdges` 采样、在 `TryCombatAttack` 单点消费；本地门 = `IsCombatFireReady` + `plan.FireIntervalMs` | 本轮只需把「`forward` 的来源」参数化，**不**改动门与 DS 链 |
| `PMClientSessionHost.Session` | 有 `OwnerPlayer` / `OwnerIdentityVerified` / `MovementFrozen` / `MatchEndedFrozen` / `TerminalResultObserved` / `LastAttackError` / `LastAttackWallMs` / `OwnerPlayer.CombatHp|MaxHp|Mana|SuperEnergy|Dead|MatchEnded` | 快照与就绪位可直接由既有**复制字段**合成，无需新真值 |
| `PMClientSessionHost` 清理路径 | `FreezeMovements`（Fail/终局共用）、`UpdateCombatReplicationState`（死亡冻结分支）、`ReleaseSession`（Stop/换局/构造失败共用） | 是 `ClearUiInput` 的四个接入点（不多也不少） |
| `PMUnityMoverInput` | `Sample`/`SampleAndConsume`/`Consume` 一套跳跃边沿状态机；`Build`→`ReadPlanar` 读键盘 + `Horizontal/Vertical` 虚拟轴，`PickLarger` 取较大者；`DeriveYawDegrees` 在 `DeriveYawFromMoveInput` 为真且平面输入非零时推 yaw | UI 摇杆**必须**走同一条 `ReadPlanar`（否则会出现第二条输入步进路径与两套边沿纪律） |
| `PMCombatWeaponPlanner.TryBuild` | 纯函数（不查资源）：拒未知英雄/非 finite/零方向/无子弹型大招/抛物线，`plan.ManaCost = isSuper ? 0 : hero.NormalAttackManaCost`，`plan.FireIntervalMs ≥ 100` | 就绪位可以复用它问「这种形态是否可打」，不必新造规则 |
| `BattleNumericConfig` | `ManaMax=90`、`SuperEnergyMax=200`、`DefaultAttackManaCost=30`；`PMHeroId.Count=20` | 大招就绪 = 能量满 200（与契约「满 200 清 0」一致） |
| `Tools/PMR4UnityAdapterTest.csproj` | 已编 `PMUnityMoverInput.cs`（+ A 组本轮加的 `PMBattleCameraGeometry.cs`），桩件只有 `Input`/`KeyCode` | 纯变换与来源选择**能真跑**；宿主不能（不假装） |
| 并行组 `PMUnityBattleControls.cs`（只读） | 用**委托**`PMUnityBattleMoveInputHandler(float,float)` / `PMUnityBattleAttackInputHandler(bool,float,float)` / `PMUnityBattleStatusQuery(out PMUnityBattleUiStatus)` 解耦，自带镜像结构 `PMUnityBattleUiStatus`（字段顺序 Hp/MaxHp/Mana/SuperEnergy/Dead/MatchEnded/NormalReady/SuperReady/LastReject），声明「uGUI 本地 +Y 就是向上，本文件**不取反 Y**」「换轴到世界方向是宿主的事」 | 与我的实现**口径一致**（见 §7 交界记录）；它不引用宿主类型，主侧只需写一个适配 lambda |

## 3. 公开冻结契约（本轮实现值）

namespace `PMNet.Unity`（`PMClientSessionHost` 为 `public static class`，快照为同一文件内的 `public struct`）：

| 入口 | 签名 | 返回 true | 返回 false（**显式拒**，不吞非法） |
|---|---|---|---|
| 移动摇杆 | `public static bool TrySetUiMove(float screenX, float screenY)` | 已生效：非零推杆写进输入层；**释放（零向量）清移动**并接受 | 不在局内/会话 fault；非主线程；`NaN/±Infinity` 或 `|分量| > 1+1e-4`；输入层缺失；冻结/终局/本人已死时的**非零**推杆（此时顺手清推杆） |
| 攻击 | `public static bool TryQueueUiAttack(bool isSuper, float screenX, float screenY)` | 已入队一个攻击边沿（带该次瞄准向量） | 不在局内/fault；非主线程；`NaN/±Infinity/超量程/零方向`；本人副本未复制或 `OwnerIdentityVerified` 未通过；本人 `CombatDead`/`CombatMatchEnded`；`MovementFrozen`/`MatchEndedFrozen`/`TerminalResultObserved`；**同一帧已有未消费边沿**（计 `UiAttackDuplicate`，防一次手势多打） |
| 只读快照 | `public static bool TryGetUiCombatSnapshot(out PMUiCombatSnapshot snapshot)` | 有会话且有本人副本 | 不在局内（已 Stop/换局/从未入局）或本人副本尚未复制 |

`PMUiCombatSnapshot`（**值类型**，只含值/字符串，**不泄出** `PMR3Player`/`Session`/`PMNetWorld`）：
`Hp`、`MaxHp`、`Mana`、`SuperEnergy`（复制值）、`Dead`、`MatchEnded`、`NormalReady`、`SuperReady`、
`LastReject`（最近一次本地 planner 或 DS 裁决拒绝的人读原因）、`MatchId`（可空，额外附上）。
全部成员为 `public readonly`，仅构造函数可写；无任何资源/复制的写入口。

就绪位口径（**本地建议，DS 仍是唯一裁决**，且刻意复用既有同一口径判据）：
`NormalReady/SuperReady` = `IsCombatFireReady`（身份已核对 + MaxHp 已复制 + 未死/未终局）
**且** 未冻结/未终局 **且** `PMCombatWeaponPlanner.TryBuild(hero, isSuper, …)` 成功
**且** `wallNow - LastAttackWallMs ≥ plan.FireIntervalMs`
**且** 复制资源够（普通：`Mana ≥ plan.ManaCost`；大招：`SuperEnergy ≥ SuperEnergyMax(200)`）。
它只用于 UI 置灰/提示，**不**阻止 UI 尝试，也不构成新的权威。

## 4. 屏幕系轴约定与证据（与 T-PLAY1 同源，不硬编码巧合）

- 约定：`screen up(+Y) → world −X`、`screen right(+X) → world +Z`（用户选定相机世界 yaw = −90）。
- 实现：`worldX = -screenY`、`worldZ = +screenX`（屏幕→世界是**旋转 90°**，不改变模长；只有模长 > 1 才缩到 1）。
- 证据（门禁 I11 实测）：`camYaw=-90`，相机水平前向 `(sin(-90),cos(-90)) = (-1, 0)` == screen up 的世界向量；
  相机右向 `(sin(0),cos(0)) = (0, 1)` == screen right 的世界向量。该断言用 `PMBattleCameraGeometry.DefaultYawDegrees`
  **反推**，所以将来若有人改了相机朝向，这条会先失败。
- 与并行 UI 组口径一致：它的注释写明「uGUI 本地空间的 +Y 就是向上，与冻结口径同向，因此本文件**不取反 Y**」，
  换轴明确由宿主做 —— 与本节实现一致。

## 5. 实现要点

### 5.1 `PMUnityMoverInput`（变换 + 来源选择，单一通路）

- 纯静态（不读硬件、不改状态，net8 可断言）：
  - `TryScreenVectorToWorld(screenX, screenY, out worldX, out worldZ)`：NaN/±Inf 或 `|分量|>1+1e-4` → **false**；
    接受范围内限幅单位向量；零向量是合法输入（返回 true 且 world=(0,0)）。
  - `TryScreenAimDirection(screenX, screenY, out worldX, out worldZ)`：同上校验 + 方向非零（长度 ≤ 1e-4 → false），
    返回**单位**向量。**没有方向就拒绝**，不替玩家默认一个弹道。
- 有状态（主线程；宿主写、`ReadPlanar` 读）：`SetUiMoveWorld`（非有限抛异常、模长 > 1 限幅、零向量等价 `ClearUiMove`）、
  `ClearUiMove`、`HasUiMove`、`UiMoveWorldX/Z`、`UiMoveAcceptCount`、`UiMoveClearCount`（诊断可对账）。
- **来源选择**：`ReadPlanar` 第一条就是 `if (_hasUiMove) { x = …; z = …; return; }`
  ⇒ UI 摇杆在时键盘物理键与 `Horizontal/Vertical` 虚拟轴**都不参与**平面输入（不叠加、不各来一份）；
  摇杆释放（零输入 ⇒ `HasUiMove=false`）后键盘**立刻**恢复，无需额外开关。竖直轴与跳跃边沿不受影响。
- `Reset()`（切场景/断线/换局/用例之间）一并清 UI 摇杆。

### 5.2 宿主：三个入口

- 全部为 `static`，并在会话建立时记下 `_mainThreadId`；非主线程调用**拒绝 + 警告 + 计数**（UI 与每帧 Pump 必须同线程，
  否则边沿位会被两个线程竞写）。
- `TrySetUiMove`：非法输入不改变已有状态（不静默钳制）；释放总是接受（清空不产生位移，因此冻结帧也能松开摇杆）；
  冻结/终局/死亡时的非零推杆拒绝**并顺手清推杆**（摇杆不可能粘住跨过冻结）。
- `TryQueueUiAttack`：只把「一次攻击手势 + 世界单位瞄准向量」入队；**本方法内没有任何发送/写入调用**
  （门禁 K7 静态负例钉住）。同一帧重复调用不重复排队（`UiAttackDuplicate`）。
- `TryGetUiCombatSnapshot`：只读复制字段 + `LastAttackError` + `MatchId`；不读旧 `HYLDStaticValue/BattleData/BattleReview`。

### 5.3 单点消费 + 单向量（不弱化任何既有门）

`TryCombatAttack` 的唯一改动是**方向来源**与**边沿清位**：

```
bool uiEdge = session.UiAttackEdgeBuffered;
bool isSuper = uiEdge ? session.UiAttackIsSuper : session.SuperAttackEdgeBuffered;
float uiAimX = session.UiAimWorldX;  float uiAimZ = session.UiAimWorldZ;
session.NormalAttackEdgeBuffered = false;
session.SuperAttackEdgeBuffered  = false;
session.UiAttackEdgeBuffered     = false;          // ← 三个边沿在同一个消费点一起清
…
forward = uiEdge ? new PMVector3(uiAimX, 0f, uiAimZ)                 // UI：独立于 predictedYaw
                 : YawForward(predicted.YawDegrees);                // 键盘 F/G：predicted yaw 兜底
… PMCombatWeaponPlanner.TryBuild(hero, isSuper, forward.X, forward.Z, …)
… origin = predicted.Position + forward * MuzzleOffsetM
… combat.TryAttack(owner, isSuper, forward.X, forward.Z, origin, …)
```

⇒ planner 方向 / 枪口 / 上行声明**共用同一个 `forward`**（门禁 K11）；UI 与键盘两个源都不绕过
`IsCombatFireReady` 与 `plan.FireIntervalMs`（门禁 K14）；边沿消费纪律保持原样（先确认本帧能尝试才消费）。

### 5.4 清理路径（不带出上一局 UI 输入）

新增 `ClearUiInput(session)`（清摇杆 + 排队攻击边沿与瞄准向量，幂等），接入四处：
`FreezeMovements`（**Fail 与终局共用**）、`UpdateCombatReplicationState` 的本人死亡分支、
`ReleaseSession`（Stop/换局/构造失败共用），以及 `TryQueueUiAttack` 在冻结/终局/死亡时的拒绝分支（门禁 K15）。

### 5.5 可观测

心跳/`Describe()` 的 combat 段新增 `ui{move=接受/拒绝 attack=入队/拒绝 dup=重复}`，门禁与实机联调可对账。

## 6. 测试与证据（先失败后修，真实命令/真实输出）

### 6.1 第一轮：只加断言 + 只落「状态存储」，不接来源选择（RED）

先落 `TryScreenVectorToWorld` / `TryScreenAimDirection` / `SetUiMoveWorld` / `ClearUiMove` 与属性，
在 `ReadPlanar` 里**刻意先不接**来源选择，然后加上 I/J 两节断言并真跑：

```
dotnet build Tools/PMR4UnityAdapterTest -c Release      # 0 错误 0 警告
dotnet Tools/PMR4UnityAdapterTest/bin/Release/net8.0/PMR4UnityAdapterTest.dll
```

结果：**checks=163 failed=4**（exit=1）。I 节 16 条**全 PASS**（纯变换当时已实现），J 节 4 条失败：

| 断言 | 旧实现实际输出 | 判定 |
|---|---|---|
| J2 UI 摇杆存在时忽略键盘平面输入 | `MoveX=1 MoveZ=0`（键盘 D 生效、UI 被无视） | FAIL |
| J3 UI 摇杆存在时也忽略虚拟轴 | `MoveX=1 MoveZ=0` | FAIL |
| J5 `SetUiMoveWorld(3,4)` 限幅到单位向量 | `MoveX=0 MoveZ=0 len=0` | FAIL |
| J6 朝向跟随 UI 移动方向（+Z ⇒ yaw 0） | `yaw=90`（跟随了键盘 +X） | FAIL |

> 注：J4/J7/J8 当时就 PASS —— 它们只涉及「释放后键盘恢复」「Reset 清 UI」「计数」，
> 不依赖 `ReadPlanar` 的接入，属于**冻结证据**而非缺陷。

### 6.2 第二轮：接上来源选择（GREEN）

`ReadPlanar` 首行接入 `if (_hasUiMove) { … return; }` 后重跑（本次 build0 后 run0）：

```
结果：全部通过（checks=180 failed=0）   exit=0
```

关键实测值：`I1 world=(-0,1)`、`I3 world=(-1,0)`、`I6 world=(-0.70710677,0.70710677) len=1`、
`I11 camYaw=-90 up=(-1,0) fwd=(-1,6.1e-17) right=(-0,1) camRight=(0,1)`、
`I15 81 个网格点全部接受、|world| == min(|screen|,1)、换轴正交`、
`J2 MoveX=-0.5 MoveZ=0（键盘 D 被忽略）`、`J5 MoveX=0.6 MoveZ=0.8 len=1`、`J6 yaw=0`。

### 6.3 K 节：宿主接线的静态契约与负例（宿主需要 Unity，故不假装运行）

`PMClientSessionHost` 的三个入口与消费点都需要真实 Unity 会话/物理场景，本工程（net8 纯逻辑）**不运行宿主**；
因此用「源码形状 + 负例」把**路由**钉住（先 `StripComments` 再看代码，避免注释让断言假绿/假红）：

| 断言 | 钉住的契约 |
|---|---|
| K2/K3/K4 | 三个冻结入口的**精确签名**（含 `out PMUiCombatSnapshot`） |
| K5/K6 | 快照是 `public struct` 且含全部冻结字段；**不得出现** `PMR3Player`/`PMNetWorld`/`Session` |
| K7/K8 | UI 入口内**无** `ServerCombatAttackV1`/`PMNet_`/`MarkPropertyDirty`/`TryAttack(`/权威赋值 |
| K9/K10 | 轴变换只有一份（委托给 `PMUnityMoverInput` 的纯入口），宿主内不得再写一份换轴 |
| K11 | planner / 枪口 / 上行三处共用同一个 `forward` |
| K12 | UI 分支取 UI 向量；键盘分支仍用 `predicted.YawDegrees` |
| K13 | F/G/UI 三个边沿在**同一个消费点**一次性清空 |
| K14 | UI 攻击仍先过 `IsCombatFireReady` 与 `plan.FireIntervalMs`（不弱化） |
| K15 | `ClearUiInput` 至少接在冻结/释放/复制死亡三条路径（实测调用点 4 处） |
| K16 | 主线程门存在（记 `_mainThreadId`、拒异线程） |
| K17 | 来源选择在输入层：`ReadPlanar` 里 UI 优先、键盘兜底，且宿主是唯一写入面 |

### 6.4 R6 真实字节链：UI 瞄准向量的「同源」与反例（`Tools/PMR6NetworkTest` W 节）

宿主不能被 net8 运行，但**契约「一次攻击的方向/枪口/planner/上行必须是同一个向量」可以在真实字节链上验证**：
把 UI 摇杆换出来的世界方向（screen up ⇒ world −X）当成宿主要用的方向，构造真实会话（柯尔特 vs 雪莉）：

| 断言 | 内容 |
|---|---|
| W1/W2/W3 | UI 方向能生成计划；**每颗弹**方向都在该向量的 1° 容差内；方向确实由 UI 瞄准（−X）决定，而非旧 yaw 兜底（+Z） |
| W4/W5 | 上行成功且 **DS 按同一方向授权了全部预测弹**（方向槽匹配） |
| W6/W7/W8 | **反例**：把上行方向换成正交的旧 yaw 方向（+Z）⇒ 被 DS 拒、且不生成任何权威对象（“只改 UI 显示”过不了方向槽） |
| W9/W10/W11 | 同一向量的命中链照常被真实结算（HP 下降）、无会话 fault |

实测：`通过 400 / 失败 0`（exit=0），W 节 `ok`。

## 7. 门禁表（本次 build0 后 run0）

| 门 | 命令 | 结果 |
|---|---|---|
| 纯变换 + 来源选择 + 宿主静态契约 | `dotnet build Tools/PMR4UnityAdapterTest -c Release` + run | **PASS：checks=180 failed=0**（exit=0） |
| R6 真实字节链（含 UI 瞄准同源 + 反例） | `dotnet build Tools/PMR6NetworkTest -c Release` + run | **PASS：通过 400 / 失败 0**（exit=0） |
| 全层替身编译（含宿主 + PMUnity 全目录） | `dotnet build Tools/PMClientCheck -c Release --no-incremental` | **PASS：0 错误 0 警告** |
| Unity 桩件胶水编译 | `dotnet build Tools/PMUnityGlueCheck -c Release --no-incremental` | **PASS：0 错误**（3 条既有 CS2002 重复源文件警告） |
| 真实 Unity2019 DLL 编译（两宿主 + PMUnity 全目录 + Editor 验证） | `dotnet build Tools/PMR4UnityCheck -c Release --no-incremental` | **PASS：0 错误**（同 3 条既有 CS2002 警告） |
| 客户端权威写入 | `python Tools/check_client_authority_writes.py` | **PASS：越界写入 0**（UI 入口不新增任何权威写） |
| 括号配对 | `python Tools/check_cs_braces.py` 四个改动文件 | **PASS**（宿主 4079 行、输入 667 行、R4Adapter 2206 行、R6 2337 行，括号平衡且深度末尾归零） |
| 编码 | 逐文件 `BOM/CRLF/bare_LF` | **PASS**：均为 `efbbbf` 起头、`bare_LF=0` |

### 关于 `PMR4UnityCheck` 的一次**跨组瞬时红**（客观记录，非我的代码）

第一次跑该门时它是**红**的：所有 `error CS` 都落在**并行 UI 组刚新建**的
`Client/Assets/Scripts/PMUnity/PMUnityBattleControls.cs`（`UnityEngine.UI` / `UnityEngine.EventSystems` /
`UnityEngine.TextRenderingModule` 未被那个 csproj 引用），**我的两个文件错误条数为 0**（逐条 grep 核对）。
我是这样定性的、而不是猜：

1. `grep -c 'error CS.*(PMClientSessionHost|PMUnityMoverInput)\.cs'` → **0**；
2. 在**仓库外的临时目录**（`%TEMP%`，验证后已删除、不进仓库）用一个临时 csproj 镜像
   `PMR4UnityCheck.csproj` 的同一份文件集，只额外补 `UnityEngine.UIModule.dll`、
   `UnityEngine.TextRenderingModule.dll`、`Client/Library/ScriptAssemblies/UnityEngine.UI.dll` ⇒
   **整份文件集（含并行组文件）0 错误 0 警告**，即缺的只是引用；
3. 随后并行组自己给 `Tools/PMR4UnityCheck/PMR4UnityCheck.csproj` 补上了
   `UnityEngine.UIModule` / `UnityEngine.TextRenderingModule` / `UnityEngine.UI`（`$(UnityUiAssembly)`），
   我重跑该门 ⇒ **0 错误**（仅 3 条既有 CS2002 警告）。
   ⇒ 该门现为**绿**，且包含我的改动。这也说明当时那次红是并行写入的中间态 + csproj 引用缺口，不是本轮源码问题。

> 另注：`Tools/PMR4UnityAdapterTest/PMR4UnityAdapterTest.csproj` 在 `git status` 里也是 M，但那是
> T-PLAY1/2（A 组）加 `PMBattleCameraGeometry.cs` 的改动，**不是本轮**（本轮只改该工程的 `Program.cs`）。
> 本轮**没有**改任何 csproj。

## 8. 与并行表现 UI 组的交界（只读核对，不改它）

- 它的 `PMUnityBattleControls.cs` **不引用宿主类型**，用三个委托解耦：
  `PMUnityBattleMoveInputHandler(float,float) → bool` 可直接绑 `TrySetUiMove`（方法组）；
  `PMUnityBattleAttackInputHandler(bool,float,float) → bool` 可直接绑 `TryQueueUiAttack`；
  `PMUnityBattleStatusQuery(out PMUnityBattleUiStatus) → bool` 需要一个**适配 lambda**：
  调 `PMClientSessionHost.TryGetUiCombatSnapshot(out PMUiCombatSnapshot s)` 后把
  `Hp/MaxHp/Mana/SuperEnergy/Dead/MatchEnded/NormalReady/SuperReady/LastReject` 逐字段搬到它的镜像结构
  （字段名与顺序一致；我的快照多一个可选的 `MatchId`，适配时忽略即可）。
- 它的最小有效瞄准长度是 `0.05f`，我的是 `1e-4f`（我这边更宽松）⇒ 不冲突。
- 它明确「不取反 Y」「换轴到世界方向是宿主的事」，与 §4 的口径一致。
- 该适配 lambda 属于**主侧接线**（委派边界写明「UI组不改Host，由主侧在batch后将 Create/Update/Dispose 接到会话宿主」），
  本轮**不**代做、也不改它的文件。宿主侧不需要为它再做任何改动。

## 9. 明确未做 / 未越界

- **不做** UI：无 Canvas、无 Prefab 抽取、无 Sprite、无 uGUI 组件、不激活 `Resources/Prefabs/GameUI.prefab`、
  不留旧 `TouchLogic/EasyTouch/旧按钮持久监听`；`PMUnityBattleControls.cs` 与 `_play_ui_visual.md` 属并行组。
- **不改**权威链：不改 Mover 权威 Yaw/协议/生成物/`ProtocolHash`/ID 锁；不改 DS 审批、资源、间隔门、拒绝处理；
  UI 不直接发 RPC、不写 HP/Mana/Energy/复制字段/本地伤害（门禁 K7/K8 + 权威写入检查为证）。
- **不改**相机/光照/表现层/资产：取景常量与 `PMBattleCameraGeometry` 未动；`PMUnityMoverPresentation` 逐字未动。
- 不建任何临时假类来「凑编译」；未启动 Unity/服务/打包；未提交/暂存/SVN/回滚/递归委派。
- 临时验证工程建在**仓库外的系统临时目录**并在用后删除（未进入仓库、未进入交付）；除此之外没有创建任何文件。

## 10. 残留风险 / 实机待验（T-PLAY5，PENDING_USER）

以下只能在 Unity 2019 + 同版本双端/Lobby/DS 下确认，**本报告不宣称已通过**：

1. **摇杆手感**：UI 摇杆的屏幕→世界换轴在实机上是否与画面方向一致（门禁只能证明「轴约定与相机 yaw −90 同源」，
   不能证明「玩家按屏幕右边角色就往世界 +Z 走」这一**观感**）；斜向推杆限幅到单位向量是否手感合适（不加速）。
2. **UI 独立瞄准 vs 移动转身**：实机确认「移动方向转身」与「攻击摇杆决定弹道」确实是两个独立输入
   （本轮在协议层已证明方向槽只认 UI 向量；画面层需要实机确认瞄准反馈不误导）。
3. **就绪位显示**：`NormalReady/SuperReady` 与 DS 的真实裁决是否一致（例如极短间隔内连按、资源刚好临界时的下一帧刷新）。
4. **拒绝提示**：`LastReject` 的文案是否覆盖实机常见拒因（抛物线/能量不足/间隔未到/DS 拒绝）。
5. **停局/换局/二局**：实机确认退局后摇杆不再推、再进局不残留上一局推杆（代码侧已接四处清理，但只有实机能看 UI 侧表现）。
6. **多手指/断触**：uGUI 摇杆的多点触控与 pointer up 丢失时，`TrySetUiMove(0,0)` 是否一定被调用到
   （宿主侧对「冻结/死亡时的非零推杆」会强制清，但正常局内的丢手指事件仍依赖 UI 组正确上报）。
7. 美术：本批**不**包含旧 UI 的美术还原，`PMUnityBattleControls.cs` 的说明也已声明「不是完整旧 UI 还原」。

## 11. 未验实机的诚实口径

- 本轮所有结论都是**代码侧 + 真实字节链 + 编译面**结论：`PMR4UnityAdapterTest` 180/0、`PMR6NetworkTest` 400/0、
  三个编译门 0 错误、权威写入 0、括号/编码核对通过。
- **没有任何一条**等于「Unity 实机双端通过」；`PMR4UnityCheck` 只证明「能在真实 Unity 2019 API 上编译」，
  `PMR6NetworkTest` 只证明「真实字节链上的方向/裁决闭环」。
- 明确未验：实机画面/摇杆手感/多指触控/退局二局/真机帧率，以及旧 UI 美术的还原度（PENDING_USER）。
