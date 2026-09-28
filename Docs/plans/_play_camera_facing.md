# T-PLAY1/2 第一阶段：旧透视俯视镜头 + 旧可见角色随移动方向转身

范围：客户端**表现层**（本地 owner 相机 + 可见角色朝向）。不改权威 Mover、不改协议、不改 DS、不改任何资产（场景 / prefab / manifest / 烘焙产物）。

本轮实际写入（**仅**这 5 个文件）：

| 文件 | 变更 |
|---|---|
| `Client/Assets/Scripts/PMUnity/PMBattleCameraGeometry.cs` | 新增旧预制体取景常量、`LegacyFollow*`、`FollowSmoothTimeSeconds`/`FollowSnapDistanceMeters`、`VisibleFacingYawDegrees`、`ComputeAtLookTarget`、`SmoothFollowPosition`（纯浮点、无 UnityEngine 依赖）；默认取景从诊断值改为旧预制体值 |
| `Client/Assets/Scripts/PMUnity/PMUnityBattlePresentation.cs` | 可见朝向改由旧链同一个 **Capsule** 节点承载（角色根只做位置/scale）；严格拒绝缺 Capsule 的正式内容；相机取景改为由宿主显式传入（新增 7 参 `CreateTestCamera` 重载） |
| `Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs` | 每帧相机：平滑观察目标（旧链 SmoothTime 0.08s）+ 旧预制体取景 + 遮挡探测按新 yaw/俯角更新；只给本地 owner 建相机并以纯几何常量为唯一取景来源 |
| `Tools/PMR4UnityAdapterTest/Program.cs` | 新增 B3（旧取景）、B4（平滑跟随）、D（可见朝向 + 资产/源码静态契约）三节共 40 条断言；B2-3/B2-4 按新取景口径改写（原断言编码的是"诊断期 yaw 0/俯角 12"，不改写就会永远把诊断取景当成正确值） |
| `Docs/plans/_play_camera_facing.md` | 本报告 |

新增测试源 / 项目改动路径（仅列出，便于主侧核对）：`Tools/PMR4UnityAdapterTest/Program.cs`（无新增工程、无新增文件；`PMR4UnityAdapterTest.csproj` 上一轮已包含 `PMBattleCameraGeometry.cs`，本轮**未**改它）。

未改：`HYLDGame.unity`、`Remake/Player.prefab`、`Resources/PMNet/**`（三个正式产物）、manifest/collisionDigest、任何 DS 代码、`PMUnityMoverPresentation`（诊断胶囊路径逐字未改）、任何桩件（`Tools/*/UnityStubs.cs` / `ClientStubs.cs`）、提交/暂存。
未执行：Unity / 服务 / 打包 / SVN / git 写操作 / 递归委派。中文源码 UTF-8 BOM + CRLF（已核对：4 个源码/测试文件 `efbbbf` 起头、`bare_LF=0`）。

## 1. 必读文档（本轮按顺序读完）与它们如何决定入口与范围

`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md` → `Server/AGENTS.md` →
`Docs/plans/net-architecture-migration.md` 末尾「实机视觉/操控还原：用户选型冻结（T-PLAY1..5）」与
「T-PLAY 第一阶段接口与验收细化（选型已确认，用户已退出 Play）」→ `Docs/plans/net-r4c-content-contract.md` →
`Docs/plans/net-r4-network-contract.md` → `Docs/plans/_visual_camera_fix.md` →
`C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`。

可验收的文档→入口映射：

- 主计划 T-PLAY 冻结「① 旧预制体透视相机（非正式源 Scene 中正交尺寸 17），FOV60/pitch 约 68/yaw−90；② 可见角色跟移动方向转身，攻击摇杆独立决定弹道；A 组 = `PMUnityBattlePresentation` + `PMBattleCameraGeometry` + `PMClientSessionHost` 相机调用/测试」，并写明「若需更改选型必须再问」。→ 本轮只做 A 组的相机 + 转身，**没有**改成第三人称/正交/瞄准带转身；UI/光照留给 T-PLAY3/4。
- `net-r4c-content-contract.md` C2/C3 冻结「`PMUnityBattlePresentation` 是可选的独立测试相机，只跟新根、不读旧静态，宿主负责可见性与恢复」「正式客户端表现用 `PMUnityBattlePresentation`，新相机控制自己对象并退局还原」。→ 相机归属与生命周期只能落在表现层 + 宿主；远端 SP 不建相机。
- `net-r4-network-contract.md` B2/B3 冻结「隔离 `PhysicsScene` + 白名单，查询不改全局物理」「表现层不做第二仿真」。→ 遮挡探测继续复用 `session.Query.Scene` 与同一 layer mask，本轮只更新它的方向/起点。
- `_visual_camera_fix.md` 提供上一轮已修好的三件事（独立顶层相机、极近遮挡安全优先、`ResolveProbeOcclusion` 归一化 + 饱和计数）。→ 本轮**保留**这些不变式与统计，只改取景口径与探测入参。
- `Client/Assets/AGENTS.md` §6「不改原始烘焙输入」、§7「`PMR4UnityCheck` 是真实 Unity2019 API 编译门」。→ 资产只读；门禁必须 build0 后 run0。

## 2. 只读冻结证据（动手前的核对结果，不是回忆）

### 2.1 旧相机预制体（唯一取景数值来源）

`Client/Assets/HYLD1.0/Resources/Main Camera.prefab`（YAML 只读）：

- `Camera: field of view: 60`、`near clip plane: 0.3`、`far clip plane: 1000`、`orthographic: 0`；
- `Transform: m_LocalEulerAnglesHint: {x: 68.191, y: -90.00001, z: 0}`，`m_LocalPosition: {x: 21.36, y: 13.39, z: -0}`；
- `m_Father: {fileID: 0}` → 顶层对象（旧链是场景/管理器持有它，不是角色子节点）。

> 正交尺寸 17 出现在正式源场景 `HYLDGame.unity` 的相机上，**不是**用户选型；本轮不采用。

### 2.2 旧相机跟随脚本（已退役，从 git 历史只读取出核对）

`git show 41b0287^:Client/Assets/Scripts/Server/Manger/Battle/HYLDCameraManger.cs`（该文件已在退役提交里删除，工作区不存在；退役门 `PMLegacyRetirementTest` 明令不得复活）：

```csharp
tempx = Mathf.Min(6, transform.position.x - HYLDStaticValue.Players[...].playerPositon.x);
tempy = Mathf.Min(12, transform.position.y - HYLDStaticValue.Players[...].playerPositon.y);
...
endPos = selfBody.transform.GetChild(0).position;   // 取角色可见节点的世界位置
endPos.x += tempx; endPos.y += tempy; endPos.z = transform.position.z;
pos.x = Mathf.SmoothDamp(pos.x, endPos.x, ref _velocity.x, SmoothTime);
pos.y = Mathf.SmoothDamp(pos.y, endPos.y, ref _velocity.y, SmoothTime);
transform.position = pos;                            // 只写位置：rotation 从未被写
```

结论（逐条）：① 相机相对角色的**世界轴**偏移是 `(6, 12, 0)`；② `z` 被冻结在相机自身 z（不跟随）；③ 只对 x/y 做 `SmoothDamp`，`SmoothTime = 0.08f`；④ **rotation 从不写** —— 镜头姿态恒等于预制体姿态（yaw −90 / 俯角 68.191）。

### 2.3 旧"转身"到底写在哪颗节点上（本轮修复的关键事实）

`Client/Assets/Resources/Remake/Player.prefab`（源角色 prefab，只读）：

- 根 Transform `m_Father: 0`，其 GameObject 名 `Player`，根旋转为单位阵；
- 根下直系子节点 `Capsule`（Transform fileID `7820522290180825826`），`m_LocalEulerAnglesHint {x:0, y:270, z:0}`；
- `Capsule` 下 `Body` 挂 `Animator`（`m_ApplyRootMotion: 0`），可见骨骼/蒙皮都在 `Capsule` 之下；
- 根上的 `HYLDPlayerController`（脚本 guid `6e67998eff9636b46b49b3012fc6d288`）序列化字段
  **`selfTransform: {fileID: 7820522290180825826}`** —— 就是那颗 `Capsule`；
- `HYLDPlayerController.Update` 对 `selfTransform` 做 `MoveTowards(...)` 与
  `LookAt(position + moveDir.normalized)`（世界旋转），即"跟移动方向转身"是把**可见节点的世界朝向**写成移动方向。

`Client/Assets/Resources/PMNet/PlayerVisualV1.prefab`（C1 烘焙产物，只读）：

- 根 `PlayerVisualV1` 旋转为单位阵；直系子节点恰好一个 `Capsule`，其 `m_LocalRotation` 反解 yaw = 270°；
- `Animator`（`!u!95`）所在 GameObject 在 `Capsule` 子树内（门禁 D10 断言）。

### 2.4 修复前的当前源（问题形态）

- `PMUnityBattlePresentation.Apply`：`_root.transform.rotation = Quaternion.Euler(0f, state.YawDegrees, 0f)` → 可见朝向 = 权威 Yaw **+ 烘焙 270°**（表现层把 yaw 加在根上，Capsule 的 270 沿层级叠加）。这正是 `VisibleFacingYawDegrees` 的注释与门禁 D1 钉住的量值。
- `PMBattleCameraGeometry.DefaultYawDegrees = 0`、`DefaultPitchDegrees = 12`、`DefaultDistanceMeters = 6`、`DefaultNearClipMeters = 0.1`；`PMUnityBattlePresentation` 里相机 `fieldOfView = 60f`、`nearClipPlane = 0.1f`、`farClipPlane = 500f`、占位俯角 12。
- 宿主 `UpdateOwnerBattleCamera` 直接用 `state.Position` 求位姿（无平滑），探测方向取 `BackDirection(DefaultYaw, DefaultPitch)`。

## 3. 根因（代码事实，非推测）

1. **镜头根本不是旧俯视**：修复前那套 `yaw 0 / 俯角 12 / 后距 6 / 近裁面 0.1` 是 T-VIS2 当轮为了"先让画面可用"选的**诊断取景**（`_visual_camera_fix.md` §4.1 原文亦自述为默认值），不是旧游戏风格。它的水平后撤 5.87m 而抬升只有 1.25m（近地面平视），与旧预制体的 `yaw −90 / 俯角 68.191`（抬升 ≈12.5m、水平 ≈5.0m 的俯视）**不是同一取景**。上一轮修的是"镜头不要跟着按键急转"，**没有**修"镜头是不是旧俯视"——这两件事必须分开说。
2. **可见朝向错 270°**：表现层把权威 yaw 写在**角色根**上，而烘焙的 `Capsule` 自带 270° 本地 yaw，两者沿层级叠加 ⇒ 可见朝向 = 权威 Yaw + 270°。旧链把 yaw 写在 `Capsule` 上（`selfTransform`），所以旧链没有这个叠加。
3. 旧链还有"位置按 SmoothTime 0.08 跟随、rotation 恒定"的手感；修复前宿主每帧硬写位姿（无平滑），手感也就不是旧手感。

## 4. 实现

### 4.1 纯几何模块 `PMBattleCameraGeometry`（可 net8 断言）

新增/变更常量（全部带证据注释）：

| 常量 | 值 | 来源 |
|---|---|---|
| `LegacyPrefabFieldOfViewDegrees` | 60 | Main Camera.prefab `field of view` |
| `LegacyPrefabNearClipMeters` / `LegacyPrefabFarClipMeters` | 0.3 / 1000 | 同上 `near/far clip plane` |
| `LegacyPrefabPitchDegrees` / `LegacyPrefabYawDegrees` | 68.191 / −90 | 同上 `m_LocalEulerAnglesHint` |
| `LegacyFollowHorizontalMeters` / `LegacyFollowHeightMeters` | 6 / 12 | 旧 `HYLDCameraManger` `temp x/y` |
| `LegacyFollowOffsetDistanceMeters` | √(6²+12²) = 13.416408 | 由上面两者**推导**（不写死） |
| `FollowSmoothTimeSeconds` | 0.08 | 旧 `HYLDCameraManger.SmoothTime` |
| `FollowSnapDistanceMeters` | 8 | 本轮新增：出生/瞬移/重同步直接贴合的阈值 |
| `LegacyCapsuleBakedLocalYawDegrees` | 270 | 两个角色 prefab 的 `Capsule` 烘焙本地 yaw |
| `DefaultPitchDegrees` / `DefaultYawDegrees` / `DefaultNearClipMeters` | 68.191 / −90 / 0.3 | **改为**上面同名前缀常量（诊断期的 12 / 0 / 0.1 不再是默认） |
| `DefaultDistanceMeters` | 13.416408（`static readonly`） | = `LegacyFollowOffsetDistanceMeters` |

新增纯函数：

- `ComputeAtLookTarget(lookTarget, …)`：与 `Compute(ownerPosition, lookHeight, …)` **同构同解**（`Compute` 现在委托给它），只是观察目标由调用方给定 —— 宿主才能把"平滑后的观察目标"喂进同一套几何（门禁 B4-1 断言两者逐位一致）。
- `SmoothFollowPosition(current, target, ref velocity, smoothTime, deltaTime, snapDistance)`：**临界阻尼平滑的纯浮点实现**（与 `Vector3.SmoothDamp` 同算法、`maxSpeed = +∞`、越过目标即停在目标上）。语义：`|target − current| ≥ snapDistance` → 立即贴合（速度归零）；否则逐轴追随；`deltaTime = 0` → 保持原位。非法输入（非有限、`smoothTime ≤ 0`、`deltaTime < 0`、`snapDistance ≤ 0`）显式抛异常。
- `VisibleFacingYawDegrees(authoritativeYaw)`：**故意不接受烘焙偏转入参** —— 与 `FixedCameraYawDegrees` 同一手法，把"烘焙的 270° 不可能再参与可见朝向"做成结构约束。`LegacyHierarchyChildCameraWorldYaw(ownerYaw, localYaw)` 继续作为**回归反例**（D1 证明旧组合偏 270°）。

保留不变（上一轮已修，本轮逐字未改）：`ResolveDistance` 的硬不变式（命中距离 > 0 时返回值**严格小于**它）、`TightOcclusionFraction`、`ResolveProbeOcclusion`（有命中就绝不返回无遮挡）、`IsProbeSaturated`、`ResolveNearClip`、`ClampPitch`。

### 4.2 表现层 `PMUnityBattlePresentation`

- **可见朝向**：`Apply` 写两个 Transform ——
  - 角色根：`position`（权威位置）+ `localScale`（权威 scale）+ `rotation = identity`（**不写 yaw**）；
  - 可见节点 `_facingNode`（名字恒为 `FacingNodeName = "Capsule"`）：`rotation = Quaternion.Euler(0, state.YawDegrees, 0)`（**世界** yaw，覆盖烘焙的本地 270°），并记录 `LastAppliedFacingYawDegrees` 诊断面。
  构造函数里同口径写初值；`ApplyPredicted`/`ApplyInterpolated` 共用同一写入面（AP 预测 yaw / SP 权威插值 yaw 都走这里，与"按旧 Capsule 转身方向应用预测与权威表现"一致）。
- **严格拒绝缺 Capsule**：`TryFindFacingNode` 只认**直系子节点**里名字恰为 `Capsule` 的**唯一**一颗；0 个或多个 → 销毁实例并抛异常（错误信息列出实际直系子节点名）。宿主 `EnsureMovementRig` 捕获后返回 null → 调用方 `Fail` 整局失败，**不会**静默退化成"把 yaw 写在根上"。
  - 查找用 `GetComponentsInChildren<Transform>(true)` + `parent` 过滤（不用 `Transform.GetChild/childCount`）：本文件也要在 `Tools/PMUnityGlueCheck` 的 UnityEngine 桩件下编译，而那个桩件只提供 name/parent/GetComponentsInChildren；桩件**不在**本轮可写清单内，因此不去改它。
- **相机取景参数化**：新增 7 参 `CreateTestCamera(distance, yaw, pitch, lookHeight, fov, nearClip, farClip)`；单参重载保留（C2 既有公开面不变）并把旧预制体值作为**占位取景**（值与常量同源，且注释说明生产路径由宿主显式传入）。相机仍是**独立顶层对象**、仍不碰 `Camera.main`、`Dispose` 仍先销毁相机对象再销毁根（本轮未改这部分语义）。
  - 该文件**不引用** `PMBattleCameraGeometry`：`Tools/PMBattleContentRuntimeCheck` 只编译本文件、不含几何模块，加引用会直接打断那个编译门（已在代码注释与本报告登记）。

### 4.3 宿主 `PMClientSessionHost`

- `MovementRig` 新增相机跟随状态：`CameraLookTarget` / `CameraLookVelocity` / `CameraFollowInitialized`。
- `UpdateOwnerBattleCamera`：
  1. `desiredLookTarget = ComputeLookTarget(state.Position, DefaultLookHeightMeters)`；
  2. 首帧（`CameraFollowInitialized == false`）**直接贴合**并清零速度；其后用
     `SmoothFollowPosition(..., FollowSmoothTimeSeconds=0.08, Δt=CurrentFrameElapsedMs()/1000, FollowSnapDistanceMeters=8)`；
  3. `occlusion = ProbeBattleCameraOcclusion(session, lookTarget)` —— 探测起点改为**平滑后的观察目标**，方向仍是 `BackDirection(DefaultYawDegrees, DefaultPitchDegrees)`（即**按新 yaw/俯角更新**），最大距离仍是 `DefaultDistanceMeters`；
  4. `pose = ComputeAtLookTarget(lookTarget, DefaultDistanceMeters, DefaultYawDegrees, DefaultPitchDegrees, DefaultNearClipMeters, occlusion, MinDistanceMeters, OcclusionMarginMeters)`；
  5. `battle.ApplyTestCameraPose(pose.CameraPosition, pose.YawDegrees, pose.PitchDegrees, pose.NearClipMeters)`。
- `EnsureMovementRig`：正式内容改为 `createTestCamera = false` 构造，再在 `if (isOwner)` 内显式
  `battle.CreateTestCamera(DefaultDistanceMeters, DefaultYawDegrees, DefaultPitchDegrees, DefaultLookHeightMeters, LegacyPrefabFieldOfViewDegrees, LegacyPrefabNearClipMeters, LegacyPrefabFarClipMeters)`，随后才 `SuppressOtherCameras`。取景常量因此只有一处来源（纯几何模块），远端 SP 依旧没有相机。
- 冻结/死亡/终局语义不变：`PumpMovement` 早返回 ⇒ 相机停住；`Dispose` 仍先销毁相机对象。

### 4.4 明确不越界

- **不改权威 Mover.Yaw / 网络协议 / DS**：表现层只读 `PMMoverSyncState.YawDegrees`；`ServerMovementInputV1` 等声明、编解码、`ProtocolHash` 一字未动。
- **不改攻击朝向**：`TryCombatAttack` 仍按 `predicted.YawDegrees` 推世界前向（`PMCombatWeaponPlanner.TryBuild`）。本轮**没有**把它改成独立瞄准 —— 主计划明说下一 UI 阶段（T-PLAY4）才做"攻击摇杆独立决定弹道"。诚实登记：当前"瞄准 = Mover yaw = 移动方向"这一**既有**耦合不是本轮引入的，也不在本轮范围内解除；本轮只保证"可见角色跟移动方向转身"恢复旧形态，且**不因转身实现而改动攻击方向**。
- **不改资产**：未改 `HYLDGame.unity`、`Remake/Player.prefab`、`Resources/PMNet/**`、manifest/digest。可见朝向的修正是**运行期**写节点旋转，**不**在 Asset 上持久化任何偏转（`PlayerVisualV1.prefab` 的 Capsule 仍是 270）。
- **不给远端 SP 建相机**：`CreateTestCamera` 只在 `if (isOwner)` 内调用（门禁 D19 静态契约）。
- 诊断胶囊路径 `PMUnityMoverPresentation` 逐字未改。

## 5. 与旧链的诚实差异（不宣称"像素逐位还原"）

用户选型原文是"不一定像素逐位，优先源 prefab 确定值"，因此下面这些差异是**有意**的，逐条登记：

1. **球面后向 vs 旧轴对齐偏移**：本模块的位姿表达是"观察目标 + 固定后向 × 距离"（可复用上一轮几何），距离取旧偏移模长 13.4164，方向取旧预制体姿态。结果：水平后撤 **4.98m**（旧 6m）、抬升 **12.46m**（旧 12m）。取景尺度（相机到角色距离）与旧链一致，轴向偏移差约 ±1m。
2. **旧镜头并不精确看向角色**：旧链 rotation 固定为 `Euler(68.191, −90, 0)`，其前向与"相机→角色"方向相差约 4.76°，角色在旧画面里约在半屏高下方 ~11% 处；本轮相机精确看向观察目标（角色居中）。这是刻意选择（"居中构图"更稳、也更接近"俯视射击"直觉），但**不是**逐位一致。
3. **z 轴**：旧链 `endPos.z = transform.position.z`（z 冻结）。本轮按旧偏移的 z 分量（= 0）跟随，即相机 z 跟角色 z 走 —— 角色因此恒定留在画面水平中心。理由：新正式地图出生点 `x=±15, z=−5/0/5`，若照抄"z 冻结"，角色在 z 方向移动时会在画面上横向漂出；同时"出生/瞬移仍可见"要求相机在三个轴上都跟得住。本报告把这条当作**有意识偏离**登记，若主侧要求严格复刻"z 冻结"，改一处常量即可。
4. **相机 clearFlags/背景色未改**：旧预制体是 `m_ClearFlags: 1`（Skybox）+ 背景色 `(0.192, 0.302, 0.475)`；本轮仍保留 `SolidColor` + 深灰背景。原因：蓝色氛围/光照是 **T-PLAY3（B 组）**的明确范围，属跨组事项，不在本轮擅自并入；`farClipPlane` 已按预制体改为 1000。
5. **FOV/近裁面**：FOV 60（本就一致）与近裁面 0.3（由 0.1 改为预制体值）已按旧预制体对齐。

## 6. 验收：负例先失败后修（真实运行证据，两轮）

### 6.1 第一轮：只加断言、不修代码（先在旧实现上失败）

先把 B3（旧取景）/D（可见朝向 + 静态契约）写进 `Tools/PMR4UnityAdapterTest/Program.cs`，并在几何模块里**只**补新函数、把四个默认取景常量暂时保持诊断值，真实跑：

```
dotnet build Tools/PMR4UnityAdapterTest -c Release
dotnet Tools/PMR4UnityAdapterTest/bin/Release/net8.0/PMR4UnityAdapterTest.dll
```

结果：**checks=130 failed=13**（exit=1），失败明细（逐条可重跑）：

| 断言 | 旧实现实际输出 | 判定 |
|---|---|---|
| B3-1 世界 yaw 取 −90 | `yaw=0` | FAIL |
| B3-2 俯角取 68.191 | `pitch=12` | FAIL |
| B3-3 近裁面取 0.3 | `nearClip=0.1` | FAIL |
| B3-4 后距 = \|(6,12,0)\| ≈ 13.416 | `distance=6 旧偏移模长=13.416408` | FAIL |
| B3-6 后向落回旧相对轴（+X、+Y、\|Z\|≈0） | `back=(-0, 0.20791, -0.97815)` | FAIL |
| B3-7 水平/竖直落在旧 (6,12) 量级 | `horizontal=5.8689 vertical=1.2475` | FAIL |
| B3-8 相机 z 不漂移 | `camZ=-10.8689 lookZ=-5` | FAIL |
| B3-9 抬升远大于水平后撤（俯视而非近地面） | `vertical=1.2475 < horizontal*2` | FAIL |
| D15 角色根不再承载 yaw | 源里仍是 `_root.transform.rotation = Quaternion.Euler(0f, state.YawDegrees, 0f)` | FAIL |
| D16 可见 yaw 写在 Capsule 节点上 | 源里无 `_facingNode.rotation` | FAIL |
| D17 缺 Capsule 显式失败 + 根仍写位置/scale | 源里无 `FacingNodeName`/`"Capsule"` | FAIL |
| D19 相机只给本地 owner 建 | `EnsureMovementRig` 内当时无显式 `CreateTestCamera(` 调用点 | FAIL |
| D20 取景常量单一来源（宿主引用几何常量） | 宿主未引用 `LegacyPrefabFieldOfViewDegrees` | FAIL |

同时 D1（旧组合偏 270°）与 D6–D13（资产事实：烘焙 Capsule yaw=270、Animator 在 Capsule 之下、旧 prefab 的 `selfTransform` 指向 Capsule）在**修复前后都 PASS** —— 它们是"冻结证据"，不是缺陷，写进门的目的是**钉住基准值**（防止将来有人改了资产或把 270 当成别的东西）。

### 6.2 第二轮：修复后（本次 build0 后 run0）

```
dotnet build Tools/PMR4UnityAdapterTest -c Release   # 0 错误 0 警告
dotnet Tools/PMR4UnityAdapterTest/bin/Release/net8.0/PMR4UnityAdapterTest.dll
```

结果：**checks=139 failed=0**（exit=0）。新增/改写的关键断言与实测值：

- B3-1..B3-4：`yaw=-90`、`pitch=68.191`、`nearClip=0.3`、`distance=13.416408`（= 旧偏移模长）。
- B3-6/B3-7/B3-9：`back=(0.37151363, 0.9284275, ≈0)`、`horizontal=4.9843783`、`vertical=12.456162`、`cos(pitch)=0.37151365` → 俯视成立（`vertical > horizontal × 2`）。
- B3-8：`camZ == lookZ`。
- B3-10（初次跳变负例）：同一状态三次求解**逐位一致**（首帧摆位 == 稳定帧，进场不会先跳一下）。
- B3-11：角色 Yaw 0/±90/180/359 下整帧位姿（位置 + 朝向）逐点一致。
- B3-5：FOV 常量 = 60。
- B4-1：`Compute` 与 `ComputeAtLookTarget` 同解（`6.9843783/13.956162` 两侧一致）。
- B4-2：第一帧只走 `0.06613892`（不平移直送、也不卡住）。
- B4-3/B4-4：60 帧内单调逼近、不过冲、收敛到 1m 目标（`finalX=0.99999994`）。
- B4-5：跳变 ≥ 阈值（20m）→ 直接到位 `snapped=20`、`velocity=0`。
- B4-6：`Δt=0` 保持原位 `0.3`。
- B4-7/B4-8：非正平滑时间/负 Δt/非正阈值 → 最值异常；NaN/Inf 位置 → 参数异常。
- B4-9：`FollowSmoothTimeSeconds == 0.08`。
- D1..D5：旧组合偏 270°（`90+270=0`）vs 新组合逐点等于权威 yaw（0/±45/±90/135/180/−135/359）。
- D6..D13：资产静态契约（`root=PlayerVisualV1` 单位旋转、唯一 `Capsule`、烘焙 `yaw=270`、Animator 在 Capsule 子树内、旧 prefab 根名 `Player`、`selfTransform=7820522290180825826 == Capsule`）。
- D14..D20：源码静态契约（根不写 yaw、可见节点写 yaw、缺 Capsule 严格失败 + 根写位置/scale、相机只在 owner 分支建、宿主引用几何模块取景常量）。
- B2 节既有 99 条中，仅 B2-3/B2-4 按新取景口径改写（它们原本检查"相机在世界 −Z 侧、X 与观察目标相等"，只有 yaw=0 才成立）；改写后语义更强：逐分量断言"相机精确落在固定后向 × 距离处"、"水平投影 = 距离 × cos(pitch)、抬升 = 距离 × sin(pitch)"，换回诊断取景仍会 FAIL。

### 6.3 门禁表（本次 build0 后 run0）

| 门 | 命令 | 结果 |
|---|---|---|
| 纯几何 + T-PLAY1/2 断言 | `dotnet build Tools/PMR4UnityAdapterTest -c Release` + 运行 DLL | **PASS：checks=139 / failed=0**（exit=0） |
| 真实 Unity2019 API 编译（两宿主 + PMUnity 全目录） | `dotnet build Tools/PMR4UnityCheck -c Release --no-incremental` | **PASS：0 错误**（3 条既有 CS2002 重复源文件警告，与本轮无关） |
| C2 正式内容真实 API 编译面（含 `PMUnityBattlePresentation`） | `dotnet build Tools/PMBattleContentRuntimeCheck -c Release --no-incremental` | **PASS：0 错误 0 警告** |
| 全层替身编译 | `dotnet build Tools/PMClientCheck -c Release --no-incremental` | **PASS：0 错误 0 警告** |
| Unity 桩件胶水编译（宿主 + PMUnity，桩件无 `GetChild`） | `dotnet build Tools/PMUnityGlueCheck -c Release --no-incremental` | **PASS：0 错误**（3 条既有 CS2002 警告） |
| R4 网络回归（真实核心/Transport 字节链） | `dotnet build Tools/PMR4NetworkTest -c Release` + 运行 | **PASS（结果：PASS）** |
| R4 集成回归（两客户端 loopback + 宿主语义） | `dotnet build Tools/PMR4IntegrationTest -c Release` + 运行 | **PASS：113 项检查 / 0 失败** |
| 同目录消费者（避免几何/表现改动打断邻近回归） | `PMBattleContentSessionTest` / `PMR4CollisionAdapterTest` / `PMR5UnityAdapterTest` build + run | **PASS**：ContentSession `失败 0 项`、R4Collision `38/0`、R5UnityAdapter `74/0` |
| 客户端权威写入 | `python Tools/check_client_authority_writes.py` | **PASS：越界写入 0** |
| 括号配对 | `python Tools/check_cs_braces.py` 四个改动文件 | **PASS**（Program.cs 1662 行，`{ }` 130/130、`( )` 890/890、深度归零） |
| 旧链退役静态门 | `dotnet build Tools/PMLegacyRetirementTest -c Release` + 运行 | 静态门禁 **43 通过 / 1 失败**、负例自测 **17/17**；唯一失败 = `G8.frozen-anchors-unchanged`（`Resources/PMNet/BattleMapV1.prefab` 与 `Client/ProjectSettings/EditorBuildSettings.asset` 的 SHA 与冻结锚不符）—— 与本轮无关、非本范围：这两份文件在本轮开工前的 `git status` 里已是他人未提交改动，G8 的冻结锚不含本轮任何文件，本轮未触碰任何资产。 |

> 注：`PMR4IntegrationTest`、`PMR4NetworkTest`、`PMBattleContentSessionTest`、`PMR4CollisionAdapterTest`、`PMR5UnityAdapterTest`、`PMR5UnityCheck` 的 csproj 都不包含 `PMUnityBattlePresentation.cs`/`PMBattleCameraGeometry.cs`，因此它们的结论不受本轮改动影响（仍按 build0 后 run0 执行以留证据）。

> 并行工作树披露：执行上述门禁时，工作区里另有**并行 B 组（光照）**未提交改动（`Client/Assets/Scripts/PMUnity/PMUnityBattleMap.cs`、新增 `Client/Assets/Scripts/PMUnity/PMUnityBattleLighting.cs` 与 `Docs/plans/_play_lighting.md` 等）。`PMClientCheck`/`PMR4UnityCheck`/`PMUnityGlueCheck` 会一并编译 `PMUnity/**`，因此那三个门的绿也包含 B 组当时的代码状态；本轮自己的文件与 B 组不重叠（本轮未改 `PMUnityBattleMap.cs`、未新增光照文件）。这解释了本轮中途曾观察到一次 `PMClientCheck` 的瞬时失败（同一命令串行重跑与 `--no-incremental` 重跑均为 0 错误 0 警告），归因为并行写入期间的中间态而非本轮源码问题。

## 7. 残留风险 / 实机待验（T-PLAY5，PENDING_USER）

以下只能在 Unity 2019 + 真实双客户端/DS/Lobby 同版本下确认，**本报告不宣称已通过**：

1. **旧俯视到底像不像**：`yaw −90 / 俯角 68.191 / 后距 13.416 / FOV 60` 的实机取景是否与旧游戏观感一致（含第 5 节的 ±1m 轴向差与"角色居中 vs 旧画面略偏下"）。若用户觉得"太高/太陡/太远"，调的是同一组常量（一处改、全链生效）。
2. **可见转身**：模型转身方向是否与移动方向一致（本轮把 yaw 写在 Capsule 上、烘焙 270 不叠加，门禁只能钉住"写在哪颗节点 + 资产层次未变"，**不能**证明实机看到的方向对）；同时要确认 Animator 的动画不会自己写 Capsule/Body 的旋转而把结果盖掉。
3. **贴墙 / 出生 / 瞬移**：贴墙时后距是否收短且不穿模（`TestCameraPoseCount` 持续增长、`DistanceMeters` 变小）；出生点与重同步瞬移时相机是否**直接到位**（不滑行）；极近遮挡下相机是否仍不在角色可见体内。
4. **退局/第二局**：退局后旧相机是否原样恢复、`Camera.allCameras` 里**不残留**第二个活跃相机；第二局不累积。
5. **远端 SP**：远端角色仍然只有表现、没有相机；其转身用的是权威插值 yaw。
6. **攻击朝向（既有耦合，未解除）**：当前瞄准方向仍取自 Mover predicted yaw（= 移动方向）。"攻击摇杆独立决定弹道"属 T-PLAY4（UI 阶段），本轮明确未做，也不宣称"已解耦"。
7. **z 轴冻结 vs 跟随**（第 5.3 条）与 **clearFlags/背景色/光照**（T-PLAY3）两处仍是显式登记的偏离项。

## 8. 未做 / 禁止项确认

未启动 Unity / 服务 / 打包；未提交、未暂存、未 SVN 操作；未递归委派；未改 UE 工程。
未改 `HYLDGame.unity`、`Remake/Player.prefab`、`Resources/PMNet/**`（含 `PlayerVisualV1.prefab` 的 Capsule 仍是 270，运行期不持久化任何偏转）、manifest/collisionDigest、任何 DS 代码、任何桩件（`UnityStubs.cs`/`ClientStubs.cs`）、`PMUnityMoverPresentation`（诊断胶囊+挂根相机逐字未改）、权威 Mover/协议/攻击朝向。
`G8` 冻结资产哈希冲突仍非本范围（未触碰、未改锚值）。中文源码 UTF-8 BOM + CRLF，已逐文件核对（BOM `efbbbf`、`bare_LF=0`）。
真实双客户端实机与长局仍 PENDING_USER。
