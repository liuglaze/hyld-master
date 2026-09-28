# 正式模式角色方向键引发镜头瞬转 / 贴墙黑遮挡 修复报告

范围：仅相机表现（本地 owner 视角）。不改权威 Mover、不改协议、不改 DS、不改原始场景与烘焙产物。

## 1. 允许写入的文件（本轮实际改动）

| 文件 | 变更 |
|---|---|
| `Client/Assets/Scripts/PMUnity/PMBattleCameraGeometry.cs`（新增）+ `.cs.meta` | 纯几何：固定方位角跟随 + 观察目标 + 后距/近裁面钳制 + 遮挡回退；无 UnityEngine 依赖；**T-VIS2b**：遮挡回退硬不变式（严格小于命中距离）+ 命中原始输出归一化（起点重叠/饱和）|
| `Client/Assets/Scripts/PMUnity/PMUnityBattlePresentation.cs` | 测试相机改为**独立顶层对象**（不再挂角色根下）；新增 `ApplyTestCameraPose`；Dispose 单独销毁相机 |
| `Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs` | 正式+本地 owner 每帧求解相机位姿；隔离物理场景只读球体扫掠做遮挡；rig 增加 `BattlePresentation` 引用；**T-VIS2b**：球扫结果先经 `ResolveProbeOcclusion` 归一化（命中起点重叠不再静默当无遮挡），缓冲饱和计数 + 首次警告 |
| `Tools/PMR4UnityAdapterTest/PMR4UnityAdapterTest.csproj`、`Program.cs` | 增编 `PMBattleCameraGeometry.cs`；新增 B2 节断言（含旧反例）；**T-VIS2b** 追加极近遮挡与归一化断言（严格小于命中距离、旧 1.6 钳位精确负例、起点重叠/饱和归一化、800 点属性扫描、极近近裁面与非法距离拒绝） |
| `Docs/plans/_visual_camera_fix.md` | 本报告 |

未改：HYLDGame.unity、Resources/Remake/Player.prefab、`Resources/PMNet/BattleMapV1.prefab`、`PlayerVisualV1.prefab`、`BattleContentV1.json`、manifest/collisionDigest、任何 DS 代码、任何旧相机/HYLDCameraManger。
未执行：Unity、服务、打包、SVN/Git 提交或暂存。中文源码 UTF-8 BOM + CRLF；新 `.cs.meta` 无 BOM + LF + 唯一 GUID（`4ae58eb9731a4ca6b83af196d59082f9`，与既有 3170 个 meta GUID 无冲突）。

## 2. 必读文档（本轮按顺序读完）

`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md` → `Server/AGENTS.md` →
`Docs/plans/net-architecture-migration.md` 末尾「首次双客户端画面修复开工（用户确认退出 Play）」→
`Docs/plans/net-r4c-content-contract.md` → `Docs/plans/net-r4-network-contract.md` → `Docs/plans/net-r6-combat-contract.md` →
`C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`。

文档如何决定入口与范围（可验收）：
- 主计划 T-VIS2 冻结了「本地 Yaw 左右瞬变不使视角跟转、位置跟随、近障碍不穿模、单本地相机、退出还原旧相机」，
  并冻结「相机不参与碰撞仿真、不改 DS、不改原资源」；
- `net-r4c-content-contract.md` C2/C3 冻结「`PMUnityBattlePresentation` 是可选的独立测试相机、只跟新根、不读旧静态、
  宿主负责可见性与恢复」，因此相机归属与生命周期只能落在这两个文件；
- `net-r4-network-contract.md` B2/B3 冻结「隔离 `PhysicsScene` + 白名单、查询不改全局物理、表现层不做第二仿真」，
  遮挡投射因此复用 `session.Query` 的场景与 layer mask，不新增第二物理框架；
- `Client/Assets/AGENTS.md` §6 冻结「不改原始烘焙输入、新 `.cs` 配唯一 meta」；§7 冻结 `PMR4UnityCheck` 是真实 Unity2019 API 编译门。

## 3. 根因（代码事实，非推测）

修复前：
- `PMUnityBattlePresentation.CreateTestCamera` 把相机建成**角色根节点的子对象**：
  `cameraObject.transform.parent = _root.transform; localPosition = (0, 1.5, -distance)`；
- `Apply` 每帧按 `state.YawDegrees` 写根节点 `rotation`（`PMUnityBattlePresentation.Apply`）；
- 而 `PMUnityMoverInput` 的左右键/`A`/`D` 会由世界平面输入推导 Yaw
  （`DeriveYawDegrees(±1, 0) = ±90`，`(0, -1) = 180`）。

⇒ 子节点继承父节点旋转：**按键转向角色 = 顺带把镜头一起转**，表现为 90/180 度瞬转。
⇒ 相机后距恒为固定 6m、从不因墙收短：贴墙时相机进入几何体，出现黑遮挡/穿模。

## 4. 修复方案

### 4.1 纯几何模块 `PMBattleCameraGeometry`（新文件）

不引用 UnityEngine（只用 `System.Math` + `PMNet.Mover.PMVector3`），因此可在 net8 上独立执行：

- `Compute(ownerPosition, lookHeight, desiredDistance, configuredYaw, pitch, desiredNearClip, occlusionDistance, minDistance, margin)`
  → `PMBattleCameraPose { CameraPosition, LookTarget, YawDegrees, PitchDegrees, DistanceMeters, NearClipMeters, Occluded }`；
- **没有任何 owner Yaw 入参**：观察方位角只来自 `configuredYawDegrees`（`FixedCameraYawDegrees`），
  把「镜头不继承按键转向」做成**结构约束**而不是注释约定；
- 观察目标 = 角色世界位置 + `DefaultLookHeightMeters(1.5)`（固定目标，不是角色前方点）；
- 相机位置 = 观察目标 + `BackDirection(yaw, pitch) × distance`，`BackDirection` 为单位向量，
  与 `Quaternion.Euler(pitch, yaw, 0)` 的前向相反 ⇒ 相机始终看向观察目标；
- 默认参数：`DefaultDistanceMeters=6`、`DefaultPitchDegrees=12`、`DefaultYawDegrees=0`、
  `DefaultNearClipMeters=0.1`、`ProbeRadiusMeters=0.25`、`MinDistanceMeters=1.6`、`OcclusionMarginMeters=0.4`；
- `ResolveDistance(desired, occlusion, min, margin)`：遮挡值非有限或 ≤ 0 视为"没有可用的遮挡信息"，用想要的距离；否则
  **保证命中距离 > 0 时返回值严格小于它**（相机永远在障碍物之前）：先取上界 = `occlusion - margin`
  （远墙不缩短），边距为 0 或上界 ≤ 0（贴脸）时退化为 `occlusion × TightOcclusionFraction(0.5)`，
  两者都严格小于命中距离；再取下限 = `min(min, desired, occlusion × 0.5)`（构图期望，自身也被压在命中距离之内），
  结果 = `clamp(上界, 下限, desired)`。**安全（不穿墙）优先于构图（不贴进角色）**；
  旧实现把结果硬钳到 `MinDistanceMeters`(1.6)，命中 1.5/0.5/0.1m 时会把相机放到墙后（见 §4B）；
- `ResolveProbeOcclusion(hitCount, nearestHitDistance)`：把球扫原始输出归一成遮挡距离入参 ——
  **只要报告了至少一次命中，就绝不返回无遮挡哨兵**（起点重叠的 `distance ≈ 0` 归一为 1mm 贴脸距离；
  NaN/±Inf/负同样归一为贴脸距离）；只有 `hitCount ≤ 0` 才允许当无遮挡；
- `IsProbeSaturated(hitCount, capacity)`：`count ≥ capacity` 即饱和（批量重载不保证有序，
  被丢弃的命中理论上可能更近 —— 只计数告警，不静默当无遮挡，也不强称已防穿墙）；
- `ResolveNearClip(desired, distance)`：钳到 `[min(MinNearClipMeters, distance/2), distance/2]`，
  避免近裁面越过观察目标（**任何**距离下都成立，含 T-VIS2b 的亚 2cm 极近遮挡）；非正/非有限的距离显式抛异常；
- `ClampPitch`：±85 度；非法输入（NaN/Inf、非正距离、负边距）一律显式抛异常；
- `LegacyHierarchyChildCameraWorldYaw(ownerYaw, localYaw) = ownerYaw + localYaw`：
  **旧行为对照**，只用于回归反例，正式路径上无调用点。

### 4.2 表现层相机解耦（`PMUnityBattlePresentation`）

- `CreateTestCamera`：相机建在**独立顶层 GameObject**（`PMBattleTestCamera[label|role]`，**不设 parent**）
  ⇒ 结构上不可能继承角色根 Yaw。只给一个占位位姿（`CameraInitialHeightMeters=1.5`、`CameraInitialPitchDegrees=12`），
  并显式标注「权威每帧位姿由宿主写入」；
- 新增 `ApplyTestCameraPose(PMVector3 position, float yaw, float pitch, float nearClip)`：
  只写相机世界 position / `Quaternion.Euler(pitch, yaw, 0)` / `nearClipPlane`，**不写角色根**、不查物理、不做几何决策；
  非法输入显式抛异常；新增 `TestCameraObject`、`TestCameraPoseCount` 诊断面；
- `Dispose`：**先单独销毁相机对象**（相机不在角色根下，漏掉它就等于退局后留下一个活跃相机），再销毁角色根；幂等。

### 4.3 宿主接线（`PMClientSessionHost`）

- `MovementRig` 新增 `BattlePresentation`（仅正式模式非空；`IRigPresentation` 只有 Apply/Dispose 两面，故另持强类型引用）；
- `EnsureMovementRig`：正式路径把该引用存进 rig，并在建 rig 后**立即**摆一次相机（防止占位位姿被渲染）；
- `PumpMovement` 第 4 步（AP 表现）：同一个 `predicted` 位姿既喂角色表现也喂相机，避免两者相差一帧；
  随后 `UpdateOwnerBattleCamera` 求解并写入相机位姿；
- `ProbeBattleCameraOcclusion`：在 `session.Query.Scene`（**本局隔离 PhysicsScene**，非默认物理世界）里，
  用地图白名单 layer mask + `QueryTriggerInteraction.Ignore` 做一次只读 `SphereCast`（半径 `ProbeRadiusMeters=0.25`），
  批量重载返回不保证有序，故自己取最近命中；无命中/场景不可用 → 无遮挡哨兵；
  命中缓冲 `CameraOcclusionHits`（容量 8）静态复用，避免每帧分配；
- 失败路径沿用既有 `Fail`（幂等，自带冻结），不新增失败语义。

### 4.4 隔离性（明确不越界）

- 只对**正式模式（`ContentFormal`）+ 本地 owner** 生效；远端 SP 的 `TestCamera` 为 null，直接早返回；
- 诊断模式仍走 `PMUnityMoverPresentation`（胶囊 + 挂根节点的旧相机），代码逐字未改；
- 遮挡查询是**只读**：不移动 Transform、不调 `Physics.Simulate`、不改全局物理开关、不写权威碰撞；
  只读一个距离用于表现层后距，**DS 完全不需要参与**，协议/manifest/collisionDigest 未动；
- 相机仍由宿主按 C3 语义临时接管其它已启用相机（`SuppressOtherCameras`），退局 `RestoreSuppressedCameras` 原样恢复。

### 4.5 生命周期

| 时点 | 行为 |
|---|---|
| rig 建立（正式+owner） | `CreateTestCamera` 建独立相机（占位位姿）→ 立刻用第一帧 predicted 位姿摆正 |
| 每 Update | `PumpMovement` 第 4 步按 predicted 位姿重写相机（位置跟随世界坐标、方位角固定） |
| 死亡（`CombatDead` → 该 rig 不再 Tick） | predicted 位姿不再变化 ⇒ 相机静止 |
| 断线/终局（`MovementFrozen`） | `PumpMovement` 早返回 ⇒ 相机静止，只推墙钟 |
| `Stop`/换局/失败 | `ReleaseMovements` → `Presentation.Dispose()` → 先销毁相机对象、再销毁角色根；相机引用置 null |

## 4B. T-VIS2b：极近遮挡反例与窄修（本轮）

### 4B.1 确认的缺陷（先写反例并真跑出失败，再改代码）

主计划「T-VIS1/2 初版独立实现后的集成复核」已登记该风险。代码事实：
`ResolveDistance` 在 `occlusion - margin <= min(minDistance, desired)` 时返回 `effectiveMin = MinDistanceMeters`，
于是命中 1.5 / 0.5 / 0.1m 时相机被放到 **1.6m** —— 正好在障碍物**之后**（"贴墙黑遮挡/穿模"的成因之一）。

本轮**先只改测试**（`Tools/PMR4UnityAdapterTest/Program.cs`）写下反例，用真实运行拿失败证据：

| 断言 | 旧实现实际输出 | 判定 |
|---|---|---|
| B2-11 命中过近 → 严格小于命中距离（原断言是『等于 1.6』，本轮按修复后的正确语义改写）| `resolved=1.6` | **FAIL** |
| B2-20 命中 0.1/0.5/1.5m 后距严格小于命中距离 | `occ=0.1->d=1.6` / `occ=0.5->d=1.6` / `occ=1.5->d=1.6` | **FAIL** |
| B2-23 相机水平后撤量也严格小于命中距离 | `hz=1.5650363`（> 0.1/0.5/1.5） | **FAIL** |
| B2-24 旧公式命中 0.5m → 1.6m（墙后） | `old=1.6 new=1.6` | **FAIL** |

命令：`dotnet build Tools/PMR4UnityAdapterTest -c Release` → `dotnet Tools/PMR4UnityAdapterTest/bin/Release/net8.0/PMR4UnityAdapterTest.dll`
修复前结果：**checks=90 failed=4**（B2-11 / B2-20 / B2-23 / B2-24）；其余 86 条全 PASS。
（失败日志只在会话临时目录，未写入仓库 —— 本轮可写文件只有本报告与另三个代码/测试文件。）

### 4B.2 修法：安全优先于构图，且不做"距离归零"的伪修复

`ResolveDistance` 新增**硬不变式**：`occlusion > 0` 时返回值**严格小于** `occlusion`。

| 命中距离 | 旧结果 | 新结果 | 说明 |
|---|---|---|---|
| 无命中（inf）/0/NaN/负 | 6.0 | 6.0 | 不变（"无可用遮挡信息"唯一合法路径）|
| 6.5m（> 想要 + 边距）| 6.0 | 6.0 | 不变（远墙不缩短）|
| 3.0m | 2.6 | 2.6 | 不变（边距路径）|
| 1.5m | **1.6（墙后）** | 1.1 | 修正：`occlusion - margin` |
| 0.5m | **1.6（墙后）** | 0.25 | 修正：边距挤成非正，退到 `occlusion × 0.5` |
| 0.1m | **1.6（墙后）** | 0.05 | 修正：同上 |

下限（构图期望）也被压在命中距离之内（`min(minDistance, desired, occlusion × 0.5)`），
所以"为了不贴进角色"不可能再把相机推过墙；"不贴进角色"改由 **观察高度 1.5m > 角色权威胶囊半高 1.0m**
在结构上保证（已断言：极近遮挡下相对中心 Y > 1.5m，始终在胶囊顶之上）。

### 4B.3 遮挡探测原始输出的归一化（本轮新增，堵静默失败）

| 情形 | 旧行为 | 新行为 |
|---|---|---|
| 一次都没命中 | 无遮挡哨兵 | 无遮挡哨兵（**唯一**允许的"当无遮挡"路径）|
| 命中但 `distance ≈ 0`（PhysX 起点重叠/接触）| 传 0 → 被当成"无可用信息" → **退回到想要距离 6m = 穿墙** | `ResolveProbeOcclusion` 归一为 1mm 贴脸距离 → 后距 0.5mm（相机贴到观察点，不退到 6m）|
| 命中但距离 NaN/±Inf/负 | 旧代码 `IsInfinity(nearest)` → **无遮挡哨兵（退回 6m）** | 同样归一为贴脸距离（有命中就不允许退回无遮挡）|
| `count >= 容量`（缓冲饱和）| 静默取前 8 条里最近的 | 同上取最近，但 `IsProbeSaturated` 计数 + 首次 `LogWarning`；**不**静默当无遮挡，也**不**强称已防穿墙 |

### 4B.4 已核查但**刻意不改**的两处（附理由，而不是"忘了"）

1. **lookTarget 不调整**：极近遮挡下相机位置 = 观察目标 + 后向 × 0.05~0.25m，`相对中心 Y = 1.51~1.73m`
   已高于角色权威胶囊顶（半高 1.0m）0.5m 以上；角色**表现体**的头部位置本轮未在 Unity 内量实际包围盒
   （按胶囊中心/1.8m 身高估约 +0.7~0.8m 且在下方），因此本轮不声称"绝不自遮挡"，
   只声称"不在权威胶囊内部且高出胶囊顶 0.5m"（B2-25 已钉住）。
   若改成"贴墙时抬高观察目标"，抬高量沿后向（含 +Y 分量）反而会向障碍平面靠近，
   必须额外加一层投影预算才能保持"严格在墙前"——属于无收益的额外几何耦合，本轮不做。
   实机（T-VIS5）若发现相机仍贴进角色表现体，再单独评估该退路。
2. **不改 PMUnityBattlePresentation 的相机生命周期**：它不在本轮可写清单内；
   本轮只**只读核查**：`Dispose()` 先单独销毁独立相机对象再销毁角色根（幂等），
   且相机只在 `createTestCamera == isOwner` 时创建 ⇒ SP 无相机、退出不残留。

### 4B.5 本轮验证（build 0 后才 run）

| 门 | 命令 | 结果 |
|---|---|---|
| 纯几何（含反例与属性扫描）| `dotnet build Tools/PMR4UnityAdapterTest -c Release` + 运行 DLL | **PASS：checks=99 / failed=0**（上一轮 84 条中 83 条逐字保留并通过；B2-11 按修复后的正确语义改写（见 §4B.1，不是弱化覆盖）；新增 15 条：B2-20…B2-34）|
| 真实 Unity2019 API 编译（两个宿主 + PMUnity 全目录）| `dotnet build Tools/PMR4UnityCheck -c Release` | **PASS：0 错误**（3 条既有 CS2002 重复源文件警告，与本轮无关）|
| 全层替身编译 | `dotnet build Tools/PMClientCheck -c Release` | **PASS：0 错误 0 警告** |
| Unity 桩件胶水编译 | `dotnet build Tools/PMUnityGlueCheck -c Release` | **PASS：0 错误**（3 条既有 CS2002 重复源文件警告，与本轮无关）|
| C2 正式内容真实 API 编译面 | `dotnet build Tools/PMBattleContentRuntimeCheck -c Release` | **PASS：0 错误** |
| 客户端权威写入 | `python Tools/check_client_authority_writes.py` | **PASS：越界写入 0** |
| 括号配对 | `python Tools/check_cs_braces.py` 三个改动文件 | **PASS** |
| PMUnity 目录其他消费者（避免几何改动打断同目录编译/回归）| `PMBattleContentSessionTest` / `PMR4CollisionAdapterTest` / `PMR5UnityAdapterTest` / `PMR5UnityCheck` 均 `-c Release --no-incremental` build + 运行 | **PASS**：4 项均 **0 错误**（PMR5UnityCheck 0 警告，其余亦 0 警告）；运行：ContentSession **85/0**、R4CollisionAdapter **38/0**、R5UnityAdapter **74/0** |

关键新断言（逐条可重跑）：B2-20 严格小于命中距离、B2-21 非零正距、B2-22 相机到观察目标真实距离自洽、
B2-23 水平后撤量也严格小于命中距离（非循环）、B2-24 旧公式 1.6 精确负例、B2-25 相机仍在角色权威胶囊之外、
B2-26…B2-29 起点重叠/不可用距离不退回无遮挡、B2-30 饱和判定、B2-31/B2-32 命中 0.01…8m 共 800 点扫描
（严格小于 + 单调不抖）、B2-33 极近近裁面严格小于实际距离、B2-34 近裁面拒绝非正/非有限实际距离。

### 4B.6 残留风险（本轮只登记，**不声明完全防穿墙**）

1. **比探测球（半径 0.25m）更薄的墙/低矮天花板盲区**：从头部观察点沿后向的球扫可能**一次都不命中**
   （球从薄墙旁掠过而没碰到它）⇒ 只能返回无遮挡哨兵，无法强保证不穿墙。
   要鲁棒需要多次投射/体积裁剪（或从墙的另一侧重投、抬高起点再投），本轮不做。
2. **命中缓冲饱和（count ≥ 8）**：批量重载不保证有序，被丢弃的命中里理论上可能有更近的一面墙。
   本宿主不静默当无遮挡且计数告警（`CameraOcclusionSaturationCount`），但**不能强保证**取到的就是最近命中。
3. **真实遮挡距离为 0 的起点重叠**：归一后是 1mm 贴脸距离，但任何正距离都不可能严格小于 0，
   因此该退化情形下"严格在障碍之前"在数学上不可满足，只能保证"不退到想要距离（不穿墙到 6m）"。
4. 以上三条都需在 Unity 实机（T-VIS5）里用真实地图几何复核；本报告不把纯函数与 API 编译当成实机证据。

## 5. 验证（上一轮基线，本次 build 0 后才 run）

> 本节是 T-VIS2 上一轮的基线记录；T-VIS2b 本轮的最新计数与新增门见 §4B.5（99/0）。

| 门 | 命令 | 结果 |
|---|---|---|
| 纯几何算法（可执行反例） | `dotnet build Tools/PMR4UnityAdapterTest -c Release` + 运行 DLL | **PASS：checks=84 / failed=0**（新增 B2 节 20 条：含「角色 Yaw 0/±90/180/359 都不改变相机方位角」与「旧挂根节点在同样序列下跳到 90/180」） |
| 真实 Unity2019 API 编译（宿主 + PMUnity 全目录） | `dotnet build Tools/PMR4UnityCheck -c Release` | **PASS：0 error**（3 条既有 CS2002 重复源文件警告，与本轮无关） |
| C2 正式内容真实 API 编译面（含 `PMUnityBattlePresentation`） | `dotnet build Tools/PMBattleContentRuntimeCheck -c Release` | **PASS：0 error** |
| 全层替身编译（含两个宿主 + PMUnity） | `dotnet build Tools/PMClientCheck -c Release` | **PASS：0 error** |
| Unity 桩件胶水编译 | `dotnet build Tools/PMUnityGlueCheck -c Release` | **PASS：0 error** |
| 客户端权威写入检查 | `python Tools/check_client_authority_writes.py` | **PASS：越界写入 0** |
| 旧链退役门 | `dotnet build Tools/PMLegacyRetirementTest -c Release` + 运行 | T-VIS2b 本轮复跑：**静态门禁 43 通过 / 1 失败**，唯一失败项 = `G8.frozen-anchors-unchanged`：`Resources/PMNet/BattleMapV1.prefab` 与 `Client/ProjectSettings/EditorBuildSettings.asset` 的 SHA 与冻结锚不符。**与本轮无关且非本范围**：这两份文件在本轮开工前的 `git status` 里已是他人未提交修改（内容/构建组），而 G8 的 6 个冻结锚不含本轮任何文件；本轮未触碰任何资产。（计数从上一轮的 33 升到 43，是因为 T-VIS1 已并入新增的 G9/N7 静态门，不属本轮改动。）|
| T-VIS1 顺带确认 | 同上 | `G9.uimatching-owns-canvas-render-suppressed` = **OK**（Canvas 引用 + 原始 `enabled` 记录），与本轮相机改动不冲突 |

关于「为什么用 `SphereCast` 而不是 `Raycast`」（可核查的实现选择）：
两支桩件（`Tools/PMClientCheck/ClientStubs.cs`、`Tools/PMUnityGlueCheck/UnityStubs.cs`）与真实
Unity2019 `UnityEngine.PhysicsModule.PhysicsScene` 都提供
`SphereCast(Vector3 origin, float radius, Vector3 direction, RaycastHit[] results, float maxDistance, int layerMask, QueryTriggerInteraction)`
（已用反射读真实 DLL 核实存在单命中与批量两种重载）；桩件的 `PhysicsScene` **没有** `Raycast`。
选批量 `SphereCast` 同时满足「能在桩件门禁编译」与「比无体积射线更保守」，且不修改任何桩件文件。

## 6. 剩余实机（PENDING_USER）

以下只能在 Unity + 真实双客户端/DS/Lobby 同版本下确认，本报告不宣称已通过：

1. 进场视觉：正式地图可见、角色可见、镜头在后上方 1.5m/6m 的合理视角；
2. **左右键**（`A/D`、`Left/Right`）与后退：镜头方位角**完全不随按键/角色朝向变化**，只平移跟随；
3. 贴墙：把角色走到墙边，镜头应收短且不穿墙/不出现黑遮挡（`TestCameraPoseCount` 继续增长、`DistanceMeters` 变小）；
4. 死亡与终局：相机静止；退局后旧相机恢复、**不残留第二个活跃相机**（`Camera.allCameras` 核对）；
5. 第二局：重复入局不累积相机、共享同一隔离物理世界版本；
6. 远端 SP 与诊断（`PMR3TestScene` 胶囊）路径逐字未变的行为确认。

已知实现边界（诚实声明，未在实机验证）：遮挡是**单次球体扫掠 + 安全边距**的保守近似；
起点（角色头部观察点）若嵌在极薄墙体内或低矮天花板下，扫掠可能不命中背面。
T-VIS2b 后"靠最小距离兜底"已不再成立（那正是把相机放到墙后的旧缺陷）：现在靠的是
**遮蔽回退的硬不变式（命中距离 > 0 时后距严格小于它）** 与 **命中输出归一化（有命中就绝不退回无遮挡）**，
但比探测球还薄的墙 / 缓冲饱和 / 真实距离为 0 的起点重叠仍**无法强保证**——见 §4B.6。
真正鲁棒的解法需要多次投射/体积裁剪，本轮不做。

## 7. 登记项（本轮只登记，不修改）

1. **地图偏暗另有根因（非相机、非材质丢失）**：`Client/Assets/Editor/PMBattleContentBuild.cs` 的
   `MapAllowedComponentNames = { MeshFilter, MeshRenderer, LODGroup }` ⇒ 烘焙产物**不含任何 Light 组件**；
   而 `RenderSettings`/`LightmapSettings`（环境光、已烘焙 GI、光照图纹理）是**场景级**数据，
   prefab 资产无法携带。于是运行时隔离场景里既无光源也无环境光/烘焙 GI ⇒ 偏暗。
   烘焙代码确实保留了每个 Renderer 的 lightmap 元数据（`m_ScaleInLightmap`/`m_ReceiveGI` 等），
   但那只在“把 prefab 放回具备该光照数据的场景”时才有意义。
   本项属**光照/内容管线**主题，且原始资产与烘焙产物在冻结的禁改清单内，本轮不擅自加灯或改资源。
2. `PMLegacyRetirementTest` 的 `G8.frozen-anchors-unchanged` 当前失败（见 §5），
   根因是他人对 `BattleMapV1.prefab` / `EditorBuildSettings.asset` 的未提交改动与冻结 SHA 不一致；
   需要内容/构建组决定是重烘焙后更新锚，还是回退该改动。本轮不越界处理。
3. 跨组 UI（`UIMatchingPanel`）本轮的 T-VIS1 已由 UI 组完成：`PMLegacyRetirementTest` 的
   `G9.uimatching-owns-canvas-render-suppressed` 显示 **OK**（Canvas 引用 + 原始 `enabled` 记录均存在），
   与本轮相机改动互不冲突。
4. **T-VIS2b 新增登记（已量化、已告警，但不强保证）**：
   · 比探测球（半径 0.25m）更薄的墙 / 低矮天花板盲区：球扫可能一次都不命中 ⇒ 退回无遮挡哨兵（理论穿墙可能）；
   · 命中缓冲饱和（`count >= 8`）：`PMBattleCameraGeometry.IsProbeSaturated` + 宿主
     `CameraOcclusionSaturationCount` 计数与首次 `LogWarning` 可观测，但批量重载不保证有序，
     被丢弃的命中里理论上可能更近；
   · 真实遮蔽距离为 0 的起点重叠：归一为 1mm 贴脸距离（不再退回 6m），但不可能严格小于 0。
   三项的详细口径与修法见 §4B.6；需在 T-VIS5 实机复核，本报告**不**声明"已完全防穿墙"。

## 8. 未做 / 禁止项确认

未启动 Unity/服务/打包；未提交、未暂存、未 SVN 操作；未递归委派；未改 HYLDGame 源场景、Remake 角色 prefab、
三个 `Resources/PMNet` 正式产物、manifest/种子/digest；未改 DS、协议、`PMLegacyRetirementTest`/桩件等本轮允许清单外文件。

T-VIS2b 本轮的**实际写入范围**仅限于：`Client/Assets/Scripts/PMUnity/PMBattleCameraGeometry.cs`、
`Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs`、`Tools/PMR4UnityAdapterTest/Program.cs`、本报告。
**未**改 `PMUnityBattlePresentation.cs`（只读核查其 Dispose 与 AP-only 建相机语义）、
**未**改权威 Mover / `PMMoverDefaults` / 隔离物理世界构建 / 原地图与烘焙产物 / DS 代码；
**未**改固定 Yaw（`DefaultYawDegrees` 仍 0）、**未**改 SP 路径（`UpdateOwnerBattleCamera` 仍限本地 owner，
SP 的 `BattlePresentation` 为 null 直接早返回）、**未**新增/泄露相机对象。
G8 冻结资产哈希冲突仍非本范围（未触碰、未改锚值）。长驻实测与真实双客户端仍 PENDING_USER。
