# R4-C 显式组件拷贝重写（第四轮）—— 交付记录

> 状态唯一源：`Docs/plans/net-architecture-migration.md`（§「R4-C 显式组件拷贝重写（第四轮）」）。
> 本文只记录**本轮交付面**：支持与不支持的组件/字段、测试实际结果与日志、实机菜单名、剩余风险。
> 结论级别：T1/T2 已实测通过；**T3（Unity 实机）为 PENDING_USER**。
>
> **第五轮窄修正（追加在本文件中，见 §3.1）**：主 Agent 复核第四轮落地代码后发现两项**具体缺陷**并已定点修正：
> ① `CopyRendererCommonFields` 在**拷贝阶段**就查 `NodeMap` 解析 `probeAnchor`（后代节点还没建出来时**必误拒**合法源数据）；
> ② `ResolveLodGroupFixup` 用 `RecalculateBounds()` 覆写已按源拷贝的 `size` / `localReferencePoint`，
> 并在任一 Renderer 解析失败后**仍然** `SetLODs` 一个不完整数组。
> 第四轮的总体口径、支持矩阵、范围与 T3 状态**均不变**；本轮**同样未在 Unity 内执行**。

## 1. 本轮做了什么（一句话）

把 C1 烘焙（`Client/Assets/Editor/PMBattleContentBuild.cs`）里**地图层级的组件拷贝**从
「`SerializedObject` 遍历全部序列化字段 + `CopyFromSerializedProperty` + `ApplyModifiedPropertiesWithoutUndo`」
改成**明确组件类型的 Unity 公开 API 字段复制 + 拷贝后立即回读核对**；
未实现字段拷贝的类型在 `AddComponent` **之前**拒绝（fail closed，不静默丢字段）。

角色路径（`BuildPlayerHierarchy`）走 `Instantiate` + 删组件，**不调用**被改写的函数，本轮**未动**。
`CopyComponentSet` 的调用面只有地图三处（地面 / tile 根 / 子物体）。

**第五轮窄修正**（§3.1）修正了上面这条「引用类字段延后重绑定」实现里的两处具体缺陷：
`probeAnchor` 改为**拷贝阶段只排队、整树建完后才解析**；`LODGroup` 的 Renderer 数组重绑定
**不再 `RecalculateBounds()`**（它会覆写已按源拷贝的 `size`/`localReferencePoint`），
且在任一 Renderer 解析失败时**不调用** `SetLODs`。支持矩阵与不拷字段清单不变。

## 2. 支持矩阵

| 组件 | 拷贝字段（公开 API） | 源场景实测 |
|---|---|---|
| `MeshFilter` | `sharedMesh` | 出现 32 |
| `MeshRenderer` | `sharedMaterials`（不实例化）、`enabled`、`shadowCastingMode`、`receiveShadows`、`motionVectorGenerationMode`、`lightProbeUsage`、`reflectionProbeUsage`、`allowOcclusionWhenDynamic`、`sortingLayerID`、`sortingOrder`、`renderingLayerMask`、`rayTracingMode`、`rendererPriority`、`probeAnchor`（按映射重绑定） | 出现 32 |
| `BoxCollider` | `center`、`size` + 公共（`enabled`/`isTrigger`/`sharedMaterial`/`contactOffset`） | 出现 14 |
| `MeshCollider` | `sharedMesh`、`convex`、`cookingOptions` + 公共（顺序：先 `enabled=false`，最后恢复） | 出现 19 |
| `SphereCollider` / `CapsuleCollider` | `center`、`radius`（+`height`/`direction`）+ 公共 | **未出现（未验证）** |
| `LODGroup` | `enabled`、`fadeMode`、`animateCrossFading`、`localReferencePoint`、`size` + **Renderer 数组延后重绑定** | **未出现（未验证）** |
| `Rigidbody`（仅 kinematic） | `mass`/`drag`/`angularDrag`/`useGravity`/`isKinematic`/`constraints`/`collisionDetectionMode`/`interpolation`/`detectCollisions`/`maxAngularVelocity`/`centerOfMass`/`inertiaTensor`/`inertiaTensorRotation` | **未出现（未验证）** |
| **其它任何类型** | —— | **在 `AddComponent` 之前拒绝** |

**不支持（AddComponent 之前拒绝）**：`TerrainCollider` / `WheelCollider` 等未实现的 Collider 子类；
`Animator` / `SkinnedMeshRenderer`（属角色表现白名单，但角色路径不经过本函数）；
以及任何白名单外类型（由 `IsAllowedComponent` 先挡）。

**有意不拷的序列化字段（穷举；源场景实测值）**：
`m_StaticBatchInfo`(firstSubMesh=0)、`m_StaticBatchRoot`({fileID:0})（批处理元数据，合批根会指源场景）；
`m_LightProbeVolumeOverride`({fileID:0})、`m_LightmapParameters`({fileID:0})（2019.4 无公开可写属性）；
`m_ScaleInLightmap`(1)、`m_ReceiveGI`(1，get-only)、`m_PreserveUVs`(0)、`m_IgnoreNormalsForChartDetection`(0)、
`m_ImportantGI`(0)、`m_StitchLightmapSeams`(1 或 0)、`m_SelectedEditorRenderState`(3)、`m_MinimumChartSize`(4)、
`m_AutoUVMaxDistance`(0.5)、`m_AutoUVMaxAngle`(89)（编辑器 GI/光照图**烘焙期**元数据）；
`m_SortingLayer`（字符串镜像，排序只由 `sortingLayerID` 决定）。
其中**对象引用那三个**由 `SceneFactsCheck §H` 的源数据门守住（必须始终 `{fileID: 0}`，否则门禁失败）。

## 3. 测试实际结果（本轮）

| 门禁 | 命令 | 实际结果 | 日志 |
|---|---|---|---|
| `PMBattleContentBuildCheck`（**真实 Unity 2019.4 程序集**，netstandard2.0 + C# 7.3） | `dotnet build Tools/PMBattleContentBuildCheck -c Release` | **0 错误 0 警告**（exit 0） | 见下「日志位置」 |
| `PMBattleContentRuntimeCheck`（真实 Unity 2019.4 程序集） | `dotnet build Tools/PMBattleContentRuntimeCheck -c Release` | **0 错误 0 警告**（exit 0） | 同上 |
| `PMBattleContentSceneFactsCheck` | `dotnet build Tools/PMBattleContentSceneFactsCheck -c Release && dotnet Tools/PMBattleContentSceneFactsCheck/bin/Release/net8.0/PMBattleContentSceneFactsCheck.dll` | **137 项 0 失败，exit 0** | `Tools/PMBattleContentSceneFactsCheck/r4c-explicit-copy-verify.log` |
| `PMBattleContentManifestTest` | build 后 `dotnet .../PMBattleContentManifestTest.dll` | **92 项 0 失败**，exit 0 | `Tools/PMBattleContentManifestTest/r4c-explicit-copy-verify.log` |
| `PMBattleContentSessionTest` | build 后 `dotnet .../PMBattleContentSessionTest.dll` | **86 项 0 失败**，exit 0 | `Tools/PMBattleContentSessionTest/r4c-explicit-copy-verify.log` |
| 结构检查 | `python Tools/check_cs_braces.py <两个改动 cs>` | PASS（顺序感知，末尾归零） | 命令输出 |

**负向验证（新增门禁非空转；用 `--scene <临时副本>` 注入，未改源场景）**

| 注入 | 期望 | 实测 |
|---|---|---|
| 地面 MeshCollider 文档头改 classID 154（支持集外）| §H 报不支持 | **FAIL 2 项**（含路径 + classID）|
| `m_LightProbeVolumeOverride: {fileID: 987654}` | §H 源数据门失败 | **FAIL 1 项** |
| `m_LightProbeProxyVolume: {fileID: 123456}` | §H 源数据门失败 | **FAIL 1 项** |

另有一条非空转证据：§E/§H 的"无 `ApplyModifiedProperties`"断言本轮**真实失败过一次**
（第 1715 行的**代码字符串**里含该词），改掉后转绿。

**源数据普查（§H 输出）**：模板 + 地面（含全部后代）去重后 **37 个 GameObject**；普查按“调色板条目 + 地面”逐次遍历，
同一对象被多个调色板引用时会重复计入，所以组件计数是：
`Transform×39 / MeshRenderer×33 / MeshFilter×33 / MeshCollider×20 / BoxCollider×14`，**全部落在支持集内**。

**日志位置**：本轮命令输出已另存到各 Tools 工程的
`r4c-explicit-copy-verify.log`（与既有 `r4c-*-verification.log` 并列，均在 `.gitignore` 覆盖的 Tools 忽略区内）。

## 3.1 第五轮窄修正（两项缺陷）

主 Agent 复核第四轮落地代码后指出两项**具体缺陷**。本轮**只做定点修正**：未重开调查、未扩大范围、
未改 `Tools/**` 下任何门禁源码。

### 缺陷 1：`probeAnchor` 在"后代还没建出来"的那一瞬间就被解析 ⇒ 对合法源数据**必误拒**

- **现场（修正前）**：`CopyRendererCommonFields` 在登记 `Renderer.probeAnchor` 的延后修复**之前**，
  **先**去 `context.NodeMap` 里查 `source.probeAnchor`；查不到就立刻
  `failures.Add("字段不支持拷贝:Renderer.probeAnchor:…（probeAnchor 指向本层级之外的对象，无法重绑定到目标…）")`
  并 `return false`（随后才把 fixup 加进 `context.Fixups`）。
- **为什么是缺陷（顺序事实）**：`CopyComponentSet` 是在 `AddComponent` 之后**立刻**逐字段拷贝的；
  而 map 路径的调用顺序是**先拷 tile 根、再由 `CopyChildHierarchy` 建子层**
  （`BuildMapHierarchy` 中 `CopyComponentSet(template, tile, …)` 位于 `CopyChildHierarchy(template.transform, tile.transform, …)` **之前**）。
  因此"根对象的 `Renderer` 把 `probeAnchor` 指向自己某个**后代**渲染器"这一**完全合法**的源数据，
  在拷贝根的那一瞬 `NodeMap` 里必然还没有那个后代 ⇒ **无谓失败**。
  （源场景当前 `m_ProbeAnchor` 全为 `{fileID: 0}`，所以该误拒此前**没有被真实源数据触发过** —— 这也是它没在第四轮暴露的原因。）
- **修正**：拷贝阶段**只排队、不再查表**（只保留 `context == null` 这条显式失败）；
  解析统一交给整棵目标子树建完之后的 `ResolveProbeAnchorFixup` ——
  那时查不到，才真的是跨树 / 不存在的引用，才该失败（fail closed 口径不变，**仍绝不允许把引用指回源场景**）。
- **自测（合成源、走生产函数 `CopyComponentSet` + `ResolveDeferredReferences`）**：
  - 源根 `MeshRenderer.probeAnchor = sourceChildA.transform`（**后建的后代**）；
  - 前置条件**自证**（不是假设）：断言"拷贝根节点这一刻 `NodeMap` 里还没有 `sourceChildA` 的映射"；
  - 断言根拷贝**成功**；整棵树建完后解析**成功**，且 `destRenderer.probeAnchor` 引用**等于** `destChildA.transform`、
    **不等于** `sourceChildA.transform`（不得指源）；
  - **负例**：`probeAnchor` 指向**本层级之外**的 Transform（故意不为它登记映射）⇒
    拷贝阶段**只排队、不失败**；解析阶段**必须失败**；且目标上**不得**留下非空引用。

### 缺陷 2：`RecalculateBounds()` 覆写已按源拷贝的 `LODGroup.size` / `localReferencePoint`；解析失败仍写不完整 `SetLODs`

- **现场（修正前）**：`ResolveLodGroupFixup` 末尾是
  `dest.SetLODs(targetLods); dest.RecalculateBounds();`
  1. `RecalculateBounds()` 按 Renderer 的实际包围盒**覆写** `localReferencePoint` / `size`，
     而这两个值在 `CopyLodGroupFields` 里**已经按源逐字段拷过并回读核对过** ——
     等于"显式拷贝 + 回读核对"这条口径被 Unity 自己算的近似值悄悄毁掉；
  2. 某一级 Renderer 映射不到（或目标上没有对应 Renderer）时，旧码只 `continue` 记失败，
     **然后照样** `SetLODs` 一个含 `null` / 残缺的数组 ⇒ 写出半成品 LOD 配置
     （编辑器还会为此抛 `ArgumentException`）。这就是"看起来烘焙成功、内容却是错的"。
- **修正**：
  - **删除** `dest.RecalculateBounds()`；
  - 不假设 `SetLODs` 无副作用：写完 LOD 数组后**显式恢复**源的 `localReferencePoint` / `size`，并**回读核对**（不等即失败）；
  - 新增 `mappingComplete` 记账：**任一** Renderer 解析失败（空 Renderer / 不在映射内 / 目标无对应 Renderer）
    ⇒ 记失败并**整体放弃该 LODGroup 的 `SetLODs`**（不写不完整数组）。
- **自测**：源 `LODGroup` 取**明显非自动**的值（`localReferencePoint = (1,2,3)`、`size = 7.25f`）——
  自动值（默认 `size=1`、由包围盒算出的 referencePoint）会让"被重算"这件事**测不出来**；
  自测显式断言目标 `size` / `localReferencePoint` **等于源**（即"没被重算"），并断言源侧值本身未被改写；
  **负例**：LOD 的 Renderer 故意不登记映射 ⇒ 解析**必须失败**，且目标 `LODGroup` 上**不得**出现任何 Renderer
  （证明失败时确实没有继续 `SetLODs` 不完整数组）。

### 验证（T1/T2 级；T3 仍 PENDING_USER）

| 门禁 | 命令 | 实际结果 | 日志 |
|---|---|---|---|
| `PMBattleContentBuildCheck`（**真实 Unity 2019.4 程序集**，netstandard2.0 + C# 7.3）| `dotnet build Tools/PMBattleContentBuildCheck -c Release` | **0 错误 0 警告**（exit 0）| `Tools/r4c-narrow-fix-build.log` |
| `PMBattleContentRuntimeCheck`（真实 Unity 2019.4 程序集）| 同上 | **0 错误 0 警告** | 同上 |
| `PMBattleContentSceneFactsCheck` | build 后 `dotnet …/PMBattleContentSceneFactsCheck.dll` | **137 项 0 失败，exit 0**（与修正前**同数** ⇒ **无断言过时**）| `Tools/PMBattleContentSceneFactsCheck/r4c-narrow-fix-verify.log` |
| `PMBattleContentManifestTest` | build 后 run | **92 项 0 失败**，exit 0 | 命令输出 |
| `PMBattleContentSessionTest` | build 后 run | **86 项 0 失败**，exit 0 | 命令输出 |
| 结构检查 | `python Tools/check_cs_braces.py Client/Assets/Editor/PMBattleContentBuild.cs` | PASS（顺序感知，末尾归零）| `Tools/r4c-narrow-fix-build.log` |
| 文件编码 | — | UTF-8 **BOM** + **CRLF** 全程保持（0 裸 LF）| — |

**静态门未过时（逐条复核）**：`PMBattleContentSceneFactsCheck` 的既有断言 ——
`CheckContains("dest.probeAnchor = mapped;")`、`ResolveDeferredReferences` / `ResolveLodGroupFixup` 存在、
`CopyChildHierarchy(…)` → `ResolveDeferredReferences(tileContext, …)` 的顺序、`SetActive` 在组件拷贝之后、
"无 `CopyFromSerializedProperty` / `ApplyModifiedProperties*` / `.arraySize`" —— **逐条仍成立**，137 项全绿。
本轮**未修改** `Tools/**` 下任何文件，也**没有**需要报告的过时断言。

**自测的执行面＝PENDING_USER（诚实口径，不得扩大）**：本轮**未启动 Unity / 未抢编辑器锁**，
因此上面两处新增自测断言只在"生产函数代码 + 真实 Unity API 编译通过"这一层成立，**没有运行证据**。
"修正前会失败"是**逻辑论证**（修正前根拷贝会 `return false`；修正前目标 `size` 必被 `RecalculateBounds()`
覆写成由包围盒算出的值，与 7.25 / (1,2,3) 明显不同）—— 但**它没有被实机执行证实**，故不得写成"已验证"。

**本轮明确不主张**：native crash 已消除；`probeAnchor` / `LODGroup` 路径已在 Unity 内运行通过；
完整 `Prepare` 已可用（F8 隔离硬门保留，仍可能在自证失败时阻断烘焙）。

**必读清单偏差（如实记录）**：委托任务列出的第 3 项 `D:/UGit/hyld-master/.github/copilot-instructions.md`
**在本仓库中不存在**（`git`/文件系统均无 `.github` 目录，全仓库 `find -iname "*copilot-instructions*"` 为空）。
其余 5 项（根 `AGENTS.md`、`Client/Assets/AGENTS.md`、`Docs/plans/net-r4c-content-contract.md`、
`Docs/plans/_r4c_explicit_copy_fix.md`、`windows-shell-compat/SKILL.md`）已按序完整阅读。

## 4. 实机菜单名（T3，PENDING_USER）

| 菜单 | 作用 | 口径 |
|---|---|---|
| `Build/Self-test PMNet Content Copy (no bake)` | **最小自测**：不打开源场景、不读正式资源、不生成持久化资产；用合成 MeshFilter/MeshRenderer(两材质)/Box+MeshCollider/LODGroup 调用**生产拷贝函数**，核对 owner、组件类型集合、数值、材质数组、LOD 重绑定、源未被改写、清理；并验证未支持类型（`WheelCollider`）确实在 `AddComponent` 之前被拒绝 | 只由用户手动执行；会写 `Logs/PMBattleContentSelfTest.log`；**不是零风险**（见 §5）|
| `Build/Prepare PMNet Battle Content` | **完整烘焙**（既有菜单）| 仍 PENDING_USER；F8 隔离硬门保留 |

## 5. 剩余风险与边界（诚实记录）

1. **native crash 未被本轮消除证明**：本轮未在 Unity 内执行菜单，因此不能声称"crash 已消除"。
   本轮做的是"把产生那 32 条 `CheckConsistency` 的**写路径**删掉"，并保留 F1 纪律与 `CheckConsistency` 兜底钩子。
2. **F8 隔离硬门仍可能阻断完整烘焙**：临时场景必须自证拥有独立物理世界，证不出来就零副作用中止。
   本轮**没有**新增证据表明它在用户机器上成立。
3. **自测不是零风险**：它会在当前活动场景临时建隐藏对象（`HideAndDontSave`，不落盘）并在 `finally` 销毁；
   3 个 Collider 会短暂进入**默认物理世界**（与源场景同一个世界）。规模远小于完整烘焙，但风险不为零。
4. **未被执行到的实现**：`LODGroup`、`SphereCollider`、`CapsuleCollider`、kinematic `Rigidbody`、
   以及 `probeAnchor` 重绑定路径——**源场景当前都不出现**，只有实现与静态门禁，**没有运行证据**。
   （第五轮为 `probeAnchor` 与 `LODGroup` 这两条**补了自测断言与负例**（§3.1），口径不变：
    仍然是「代码 + 编译」级证据，**未在 Unity 内执行**。）
5. **有意不拷字段的覆盖边界**：非对象引用的 GI/光照图烘焙元数据（10 个标量）**没有**门禁守着；
   若将来源场景把它们改成"外观相关且非默认"，本轮实现会静默保持新组件默认值。
   （对象引用的三个已由 §H 源数据门守住。）
6. **每 tile 一次 `MapCopyContext`**：`NodeMap`/`Fixups` 是每 tile 新建的小字典/列表；tile 数约数千，
   属可接受的编辑器开销，但**未做性能测量**。
7. **`CheckConsistency` 兜底钩子在自测里也生效**：自测会临时接管该静态标志（结束后还原）。
   若自测与烘焙并发（不可能：都是同步菜单），会互相干扰。
8. **Unity 内尚未验证的 API 语义**：`LODGroup.SetLODs` 对"非子物体渲染器"的处理、`Renderer.probeAnchor`
   在临时场景里的赋值行为，都只在"编译通过"层面被验证。
   （第五轮进一步**不再依赖** `SetLODs` 的副作用假设：`size`/`localReferencePoint` 在 `SetLODs` 之后被显式恢复并回读；
    同时**移除了** `RecalculateBounds()` 这条会被 Unity 用来覆写已拷字段的调用。）
