# T-PLAY3 客户端专有光照（正式本局，独立且可精确释放）实现报告

范围：只在**正式模式客户端**的本局隔离物理场景里重建源场景 `HYLDGameTatal/MAP/lights` 的
1 蓝方向光 + 2 青聚光；**DS 零灯**；**不改**烘焙地图 prefab / 旧源场景 / manifest 与摘要 / DS 物理 / 协议 /
RenderSettings / LightmapSettings / 任何资产。本报告是 T-PLAY3、T-PLAY3a 的交付说明
（状态与验收唯一源仍是 `Docs/plans/net-architecture-migration.md` 末尾「T-PLAY 第一阶段接口与验收细化」B 组）。

---

## 1. 允许写入的文件（本轮实际改动）

| 文件 | 变更 |
|---|---|
| `Client/Assets/Scripts/PMUnity/PMUnityBattleLighting.cs`（**新增**）+ `.cs.meta` | 冻结参数区（纯数据、零 UnityEngine 依赖）+ 客户端专有光源容器实现（含 DS 门、隔离场景校验、幂等释放） |
| `Client/Assets/Scripts/PMUnity/PMUnityBattleMap.cs` | 新增 `ApplyClientLighting()`（DS 门 → 建独立根容器 → 失败不阻断）、可观察面 `ClientLightCount` / `LightingError`、`Dispose` 先销毁光源容器、`Describe()` 增两字段、头注释补 T-PLAY3 段 |
| `Tools/PMBattleContentSceneFactsCheck/Program.cs` | 新增 **§I（97 项）**：源 YAML 逐字段对照 + 6 条对照负例 + DS 门/隔离/物理/释放静态门 + 唯一引用者扫描 |
| `Docs/plans/_play_lighting.md` | 本报告 |

- 编码：改动/新增的 `.cs` 全部 **UTF-8 BOM + CRLF**（已用字节检查：lone LF = 0）；新增 `.cs.meta` **无 BOM + LF**，
  GUID `b95112cb02b04c98aed0fef9178aafc8`（全仓文本扫描出现 **1** 次 = 该 meta 自身，与既有 3147 个 meta GUID 无冲突）。
- **未改动**：`Client/Assets/Scenes/HYLDGame.unity`、`HYLDStart.unity`、`Resources/PMNet/BattleMapV1.prefab`、
  `BattleContentV1.json`、manifest/种子/digest、任何 DS 代码、`PMR3TestScene`、`PMUnityMoverPresentation`、
  旧相机/HYLDCameraManger、`Resources/**`、任何 `.unity`/`.prefab`/`.asset`。
- 本轮**未启动** Unity、未启动服务、未打包、未 `git add/commit`、未 SVN 操作、未递归委派。

---

## 2. 必读文档（按顺序读完）与它如何决定入口/范围

`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md` → `Server/AGENTS.md` →
`Docs/plans/net-architecture-migration.md` 末尾「实机视觉/操控还原：用户选型冻结（T-PLAY1..5）」与
「T-PLAY 第一阶段接口与验收细化（选型已确认，用户已退出 Play）」→ `Docs/plans/net-r4c-content-contract.md` →
`Docs/plans/_r4c_scene_survey.md` → `Docs/plans/_visual_camera_fix.md` →
`C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`。

| 文档结论 | 本轮落点（可验收） |
|---|---|
| T-PLAY3：「以现有同 manifest/同摘要地图资产与源场景灯为边界恢复蓝/青氛围，不恢复含玩法脚本的旧场景」 | 只读源场景 YAML 取参数；**不** `LoadScene` 旧 HYLDGame，**不**引用 `ScenseBuildLogic`/`BattleManger`/`TouchLogic` |
| T-PLAY 阶段细化 B 组：「B 组仅 PMUnityBattleMap 与新增独立 PMUnityBattleLighting(+meta)/必要测试」「DS 不得创建 Light」「不修改地图 prefab/manifest/LightmapSettings/RenderSettings 或源场景」 | 写入面严格收敛到本文件组；DS 门在**创建任何 Unity 对象之前**；源码零 `RenderSettings`/`LightmapSettings` 写入（§I 静态门） |
| T-PLAY3a：「灯源颜色/位置/intensity/DS零灯/退局清理/灯不参与物理或地图源」 | §I 的 97 项里逐条钉住（对照 + 负例 + 静态门） |
| T-PLAY5：「用户同版两端实机」+「无实机不声称颜色已经准确」 | 本报告只声称"参数逐位一致 + 结构门"，颜色观感明确 **PENDING_USER** |
| `net-r4c-content-contract.md` C2/C3：`PMUnityBattleMap` 负责 manifest/隔离物理场景/白名单/出生位；C3 宿主只消费其 API | 光源**挂在同一隔离场景**但**不挂地图 prefab 根**（不进白名单、不进 `GetSceneDigest`）；宿主文件未改（不在写入边界） |
| `_r4c_scene_survey.md` §1.5：`MAP/lights` 是 `HYLDGameTatal/MAP` 的子节点；§1.3 父链是场景内联模板 | 只读核对父链 TRS 实测为单位值（§3.1），因此源 local TRS = 世界 TRS |
| `_visual_camera_fix.md` §7 登记项 1：「C1 的 `MapAllowedComponentNames` 只保留 MeshFilter/MeshRenderer/LODGroup ⇒ 产物不含任何 Light；RenderSettings/LightmapSettings 是场景级数据」 | 本轮的**唯一**修复路径就是"客户端运行期建灯"，而不是重烘焙 prefab；并且**不**动 C1/资产 |
| `Client/Assets/AGENTS.md` §6：不改原始烘焙输入、新 `.cs` 配唯一 meta | 只读源场景；新 `.cs` + 唯一 GUID meta（无 BOM/LF） |
| `windows-shell-compat` skill | 全部命令经 pi 的 bash（Git Bash）执行；`rg/find` 类检索改用 `fffind`/`ffgrep`，未混入 PowerShell 语法 |

> 资产类型说明（与既有调查一致）：Unity 2019.4 资产是**文本 YAML**，不适用 UE 资产 MCP 路由；
> 本轮因此用 `fffind`/`ffgrep` + 文本 YAML 解析 + `python` 字节级校验，未把 `bash` 当二进制资产语义读取器。
> `#if` 与 `.csproj` 属源码（非资产），文本读取正确。

---

## 3. 只读核对结论（先看清事实，再写代码）

### 3.1 源场景 `HYLDGameTatal/MAP/lights` 的真实序列化值

父链（全部实测为单位 TRS，`§I` 已把它钉成断言）：

| 节点 | Transform fileID | localPosition | localRotation | localScale |
|---|---|---|---|---|
| `HYLDGameTatal` | 1530457320478066174 | (0,0,0) | (0,0,0,1) | (1,1,1) |
| `HYLDGameTatal/MAP` | 525959233547396898 | (0,0,0) | (-0,-0,-0,1) | (1,1,1) |
| `HYLDGameTatal/MAP/lights` | 2140687510989074931 | **(0,-8.1,0)** | (0,0,0,1) | (1,1,1) |

⇒ 容器 local TRS 即**世界** TRS：容器世界位置 **(0,-8.1,0)**、单位旋转。三盏灯（`m_Children` 顺序）：

| # | GameObject / Light / Transform fileID | 类型 | 颜色 | intensity | range | spotAngle | innerSpotAngle | localPosition | localRotation(x,y,z,w) |
|---|---|---|---|---|---|---|---|---|---|
| 0 | 2140687511124058765 / …766 / …767 | Directional(1) | (0, 0.2460041, 1) | 3 | 10 | 30 | 21.80208 | (0, 22.85, 0) | (-0.33057782, 0.06202134, -0.010192308, -0.9416835) |
| 1 | 2140687511585649917 / …918 / …919 | Spot(0) | (0, 0.9638109, 1) | 10 | 17.867682 | 51.75067 | 3.960653 | (7.9, 17.1, -8.8) | (0.39040673, -0.3226305, 0.0016521374, 0.8622583) |
| 2 | 2140687512317984506 / …507 / …508 | Spot(0) | (0, 0.9638109, 1) | 3 | 14.529544 | 77.368996 | 2.1699755 | (0.3, 17.08, -15.9) | (0.4257336, -0.14970408, 0.09832193, 0.8869456) |

三盏灯共有的组件级字段（`§I` 逐盏断言，且实现里逐项复制）：
`m_CullingMask.m_Bits = 4294967295`（= `~0`）、`m_Lightmapping = 4`（Realtime）、`m_RenderMode = 0`（Auto）、
`m_BounceIntensity = 1`、`m_UseColorTemperature = 0`（`m_ColorTemperature = 6570` 因此**不生效**）。
阴影（`m_Shadows`）：三盏都是 `m_Type = 1`（Hard）——方向光 `strength 1 / bias 0.05 / normalBias 0.4 / nearPlane 0.2`，
聚光 #1 `1 / 0.02 / 0.1 / 0.1`，聚光 #2 `0.76 / 0.02 / 0.1 / 1.6`。

### 3.2 明确**未复制**的源字段（登记，不伪称逐字段等价）

`m_Cookie`/`m_Flare`/`m_DrawHalo`（均 0/None）、`m_Shape`（0=Cone）、`m_CookieSize`（10，仅 cookie 非空时有意义）、
`m_ShadowRadius`/`m_ShadowAngle`（0，仅软点光/面光有意义）、`m_ColorTemperature`（6570，`m_UseColorTemperature=0` ⇒ 惰性）。
理由：这些字段要么在源场景里就是"空/默认"，要么在本灯类型下不生效；复制它们只会增加 API 面而不改变画面。
**不伪称"逐字段等价"**——模块头注释里也写下了这份清单（`§I` 用 `未复制` 关键字把它钉住，防止后来者删掉说明）。

### 3.3 场景级光照事实（决定了"颜色不可能逐像素还原"）

- `HYLDGame.unity` **没有任何烘焙 lightmap**：全文无 `m_Lightmaps`、无 `m_Lightmap: {fileID: …}` 引用
  （`m_EnableBakedLightmaps: 1` 只是编辑器设置）。⇒ 原场景的"蓝地板"观感来自**实时光**（上面三盏）+ 该场景自己的 RenderSettings。
- 该场景 `RenderSettings`：`m_AmbientMode = 0`（Skybox）、`ambientSkyColor (0.212,0.227,0.259)`、
  `ambientEquatorColor (0.114,0.125,0.133)`、`ambientGroundColor (0.047,0.043,0.035)`，天空盒是自带材质；
  另外场上还有一个**场景级暖方向光**（`HYLDGameTatal/Directional Light`，颜色 `(1, 0.95686275, 0.8392157)`、intensity 1）。
- 正式对局时活动场景是 **`HYLDStart`**（不是 HYLDGame）：该场景有 1 盏 Directional Light（同暖色、intensity 1）与
  `m_AmbientMode = 0`、`ambientSkyColor (0.212,0.227,0.259)`。本实现**不修改**它。
- 烘焙产物 `Resources/PMNet/BattleMapV1.prefab` **含 0 个 `Light`**（`grep -c '^Light:'` = 0），
  与 C1 的 `MapAllowedComponentNames = {MeshFilter, MeshRenderer, LODGroup}`（+ 任意 Collider）一致
  ⇒ 运行期只能**新造**灯，这与 `_visual_camera_fix.md` §7 登记项 1 的结论相同。

### 3.4 落点与不破坏性（为什么"独立根节点"是必须的）

`PMUnityBattleMap.CollectAndValidate` 会遍历**地图实例子树**的**全部组件**并对白名单外类型 `fail closed`
（MonoBehaviour/Camera/Animator/AudioListener/动态 Rigidbody…）。若把灯容器挂到地图 prefab 根下，
加载会直接失败。⇒ 容器必须是**同场景内的独立根节点**，且不含 `Collider`/`Rigidbody` ⇒
既不进 Collider 白名单，也不进 `GetSceneDigest()`（DS/客户端摘要仍可比），也不参与 PhysX 查询。

---

## 4. 实现

### 4.1 新模块 `PMUnityBattleLighting.cs` 的结构

| 区域 | 内容 | 依赖 |
|---|---|---|
| `PMBattleLightDefinition`（struct） | 一条冻结定义：name + 20 个数值列（kind、rgb、intensity、range、spotAngle、innerSpotAngle、localPos3、localRot4、shadowType、strength、bias、normalBias、nearPlane） | 零 UnityEngine |
| `PMBattleLightingSource`（static） | 源场景路径/层级、容器名与容器 TRS 常量、`ExpectedLightCount = 3`、`BounceIntensity = 1`、**定义表**（3 行，机器可读锚点 `>>> PMLIGHT-DEFINITION-TABLE-BEGIN` / `<<< …END`）、`TryValidateDefinitions` 纯数据自检、`Describe()` | 零 UnityEngine |
| `PMUnityBattleLighting`（sealed, IDisposable） | `TryCreate(scene, out lighting, out error)`：DS 门 → 场景有效性 → 定义自检 → `new GameObject("[PMNetBattleLights]")` + 源容器世界 TRS → `SceneManager.MoveGameObjectToScene` + `scene.handle` 校验 → 3 盏子灯（原 local TRS + 逐字段 Light 属性）→ 失败即销毁半成品；`Dispose()` 幂等销毁容器 | `UnityEngine`（受 `#if` 圈定，见 §6.3） |

关键实现选择（都可核查）：

- **DS 门在最前**：`TryCreate` 第一句是 `PMNet.PMNetRuntime.IsDedicatedServer` 判定并直接返回失败，
  此时**尚未** `new` 任何对象（`§I` 有"判定必须先于 `new GameObject(`"的顺序断言）。
- **隔离场景**：与 `PMUnityBattleMap` 同一条纪律——`new GameObject` 落在活动场景，必须
  `MoveGameObjectToScene(container, scene)` 并校验 `container.scene.handle == scene.handle`，否则销毁 + 失败。
- **容器 = 源位置 + 单位旋转**；三盏子灯 = 源 **local** TRS ⇒ 叠加后世界变换与源场景逐位一致（§3.1）。
- **释放**：`Dispose()` 幂等；`PMUnityBattleMap.Dispose()` 在销毁地图根**之前**先 `lighting.Dispose()`。

### 4.2 地图侧接线（`PMUnityBattleMap.cs`）

```text
TryLoad: … → CollectAndValidate(instance, …) → layerMask → Physics.SyncTransforms()
        → new PMUnityBattleMap(…) → ValidateManifestConsistency()
        → created.ApplyClientLighting()          // ← T-PLAY3：本局客户端光
        → map = created; return true;
```

`ApplyClientLighting()` 是 **`private void`**（结构上不可能让 `TryLoad` 返回 false ⇒ 表现层不阻断权威对局）：

1. 复位 `ClientLightCount = 0`、`LightingError = null`；
2. `PMNetRuntime.IsDedicatedServer` ⇒ **直接 return**（DS 零灯，不算错误）；
3. `PMUnityBattleLighting.TryCreate(_scene, …)`：
   成功 ⇒ 保存引用 + `ClientLightCount = 3` + `HYLDDebug.Log` 一行可观察日志；
   失败 ⇒ `LightingError = error` + `HYLDDebug.LogWarning` 一行，**本局继续**（半成品容器已由光照类销毁）。

新增可观察面（供实机 T-PLAY5 核对，也是本轮"保守可观察口径"的载体）：
`ClientLightCount`（期望：正式客户端 3 / DS 0）、`LightingError`（成功与 DS 路径为 null）、
`Describe()` 末尾追加 `clientLights=N, lightingError=…`。

### 4.3 生命周期表

| 时点 | 行为 |
|---|---|
| 正式客户端 `TryLoad` 成功（校验/自检之后、发布 map 之前） | 建 `[PMNetBattleLights]`（独立根，进本局隔离场景）+ 3 盏灯；`ClientLightCount = 3` |
| DS `TryLoad` 成功 | **不建任何对象**；`ClientLightCount = 0`、`LightingError = null` |
| 诊断模式（`PMR3TestScene` 胶囊路径） | **根本不经过** `PMUnityBattleMap` ⇒ 不触碰本模块（`§I` 扫描"唯一引用者"） |
| 创建过程中任一步抛异常 | 立即销毁已建容器并返回失败 ⇒ 地图模块只记 `LightingError`，本局继续（零残留） |
| 每帧 | 灯不参与驱动：无脚本、无 Animator、无刚体、无 Transform 写入（建好即静态） |
| `Stop` / 换局 / 入局失败 / 正常退场 | `PMUnityBattleMap.Dispose()` → 先 `lighting.Dispose()`（销毁容器，3 盏灯随之销毁）→ 再销毁地图根 → 卸载隔离场景；幂等 |
| 重新入局 | 新建 `PMUnityBattleMap` + 新容器（上一局的容器已在 Dispose 中销毁，不累积） |

---

## 5. 诚实口径（明确**不**声称的内容）

1. **不是逐像素还原**：源场景没有烘焙 lightmap，`RenderSettings`/`LightmapSettings` 是场景级数据、本实现不写它们；
   同时 `HYLDStart` 的暖方向光（(1, 0.95686275, 0.8392157)、intensity 1）与它的天光/环境仍**叠加**在本局之上。
   因此最终画面 = `HYLDStart` 暖方向光 + 本轮三盏灯 + `HYLDStart` 环境/天空盒，
   **不等于**原 `HYLDGame` 场景的逐像素结果（原场景还有它自己的场景级暖方向光与 RenderSettings）。
2. 本轮可证的是：**参数与源 YAML 逐位一致**（`§I` 3 盏 × 20 列）、**正式客户端恰好 3 盏 / DS 恰好 0 盏**（结构门 + 身份门）、
   **退局精确销毁**、**灯与物理/地图源/全局光照无关**。
3. **颜色观感必须由用户实机（T-PLAY5）确认**；本轮**没有**实机截图证据，因此不声称"颜色已经准确"，
   也不声称"已经和旧场景一致"。没有加 postprocessing、没有复制整个旧场景、没有改任何全局渲染设置。

---

## 6. 测试与验证（全部本次 build 成功后才 run）

### 6.1 源真实 YAML 参数静态对照 + 负例（`PMBattleContentSceneFactsCheck` §I）

```bat
dotnet build Tools/PMBattleContentSceneFactsCheck -c Release --no-incremental
dotnet Tools/PMBattleContentSceneFactsCheck/bin/Release/net8.0/PMBattleContentSceneFactsCheck.dll
```

结果：**共 238 项，失败 0 项**（其中 §A–§H 141 项为既有基线，**§I 新增 97 项全绿**）。

§I 的 97 项覆盖：

- **A 源真相（29 项）**：`HYLDGameTatal` 与 `HYLDGameTatal/MAP` 是单位 TRS；`lights` 容器 localPosition.y = −8.1、
  单位旋转；`m_Children` 恰好 3 个；每盏灯有 GameObject/Transform/Light 文档、**恰好 1 个** Light 组件、
  `m_Bits = 4294967295`、`m_Lightmapping = 4`、`m_RenderMode = 0`、`m_UseColorTemperature = 0`。
- **B 表 ↔ 源逐字段对照（13 项）**：锚点存在、表恰好 3 行、容器 7 个常量存在且**等于**源 localPosition/localRotation、
  `ExpectedLightCount = 3`、`BounceIntensity = 1f`、来源路径写在代码里、
  **「模块定义表逐字段等于源场景 MAP/lights 的真实序列化值（3 盏 × 20 列）」**、
  以及"未复制字段登记""HYLDStart 叠加""不等于逐像素"三条诚实边界文本门。
- **C 对照负例（8 项）**：先证明"未改动时对照返回一致"（不是恒真），再逐条改字段必须被判不一致：
  方向光 `intensity`、聚光#1 `spotAngle` / `colorG`、方向光 `localRotation.x`（改成单位值）、
  聚光#2 `shadowNormalBias` / `localPosition.y`、以及**名称**改动。
- **D 结构门（47 项）**：模块实现区带 `#if UNITY_2019_1_OR_NEWER || UNITY_EDITOR` 且注明桩件理由；
  `IsDedicatedServer` 出现在 `new GameObject(` **之前**；`MoveGameObjectToScene(container, scene)` +
  `scene.handle` 校验；容器**没有** `container.transform.parent =`（独立根）；
  实现区（剥注释后）**不含** `Collider/Rigidbody/Physics./PhysicsScene/RenderSettings/LightmapSettings/
  QualitySettings/Skybox/Material/PostProcess/Volume/AssetDatabase/PrefabUtility/SceneManager.LoadScene/UnityEditor`；
  复制 `cullingMask/renderMode/lightmapBakeType/bounceIntensity/useColorTemperature` 的语句存在；
  `Dispose` 存在 + 幂等 + 销毁自持容器；地图侧 `created.ApplyClientLighting();` 的位置（自检之后、发布之前）、
  `private void ApplyClientLighting()`、DS 门先于 `TryCreate`、`lighting.Dispose()` 先于 `DestroyObject(root)`、
  容器不进 `CollectAndValidate`；以及"`PMUnityBattleLighting` 只被 `PMUnityBattleMap.cs` 引用"的扫描
  （⇒ 诊断模式不经本模块）。

### 6.2 真实 Unity2019 API 编译 + 必要 RuntimeCheck

| 门 | 命令 | 结果 |
|---|---|---|
| **真实 Unity2019 API 编译（PMUnity 全目录，含新模块）** | `dotnet build Tools/PMR4UnityCheck -c Release --no-incremental` | **0 错误**（3 条既有 CS2002 警告）——新模块的 `Light.type/color/intensity/range/spotAngle/innerSpotAngle/shadows/shadowStrength/shadowBias/shadowNormalBias/shadowNearPlane/cullingMask/renderMode/lightmapBakeType/bounceIntensity/useColorTemperature`、`LightType/LightShadows/LightRenderMode/LightmapBakeType`、`SceneManager.MoveGameObjectToScene`、`GameObject.scene` 全部对真实 DLL 编译通过（本工程定义 `UNITY_EDITOR` ⇒ 实现区真的被编了；改前曾因缺 `using UnityEngine;` 报 CS0246，正是"实现区确实参与编译"的反证） |
| C2 正式内容真实 API 面（含 `PMUnityBattleMap`） | `dotnet build Tools/PMBattleContentRuntimeCheck -c Release --no-incremental` | **0 错误 0 警告** |
| 全层替身编译 | `dotnet build Tools/PMClientCheck -c Release --no-incremental` | **0 错误 0 警告** |
| 正式内容会话（manifest/摘要/会话）运行 | `dotnet Tools/PMBattleContentSessionTest/bin/Release/net8.0/PMBattleContentSessionTest.dll` | **85 / 0**，跳过 0 |
| 碰撞适配器运行 | `dotnet Tools/PMR4CollisionAdapterTest/bin/Release/net8.0/PMR4CollisionAdapterTest.dll` | **38 / 0** |
| 投射物 Unity 适配运行 | `dotnet Tools/PMR5UnityAdapterTest/bin/Release/net8.0/PMR5UnityAdapterTest.dll` | **74 / 0** |
| 投射物真实 API 编译 | `dotnet build Tools/PMR5UnityCheck -c Release --no-incremental` | **0 错误** |
| 客户端权威写入检查 | `python Tools/check_client_authority_writes.py` | **PASS：越界写入 0**（许可 9 / 已登记例外 13，未新增例外） |
| 括号配对 | `python Tools/check_cs_braces.py <3 个文件>` | **PASS**（三个文件花括号/圆括号均 BALANCED） |
| 旧链退役门（含 §I 之外的资产/GUID 冻结） | `dotnet build Tools/PMLegacyRetirementTest -c Release --no-incremental` + 运行 | 构建 0 错误；运行 **43 通过 / 1 失败**，唯一失败 = **G8.frozen-anchors-unchanged**（见 §7.3，与本轮无关、预先存在） |

> 口径：以上都是 **CODE_ONLY** 证据。`PMR4UnityCheck` 用的是 `D:/Unity/2019.4.8f1` 的**真实托管 DLL**，
> 但仍**不等于** Unity 内实跑（不加载场景、不跑 PhysX、不渲染）。本报告不拿编译当实机。

### 6.3 编译面（受写入边界限制的妥协，已登记）

新模块的 Unity 实现区用 `#if UNITY_2019_1_OR_NEWER || UNITY_EDITOR` 圈定，原因与后果都写在模块头注释里：

- **真实 Unity 构建（Editor 与所有 Player 目标）都定义 `UNITY_2019_1_OR_NEWER`** ⇒ 实现区真实存在于客户端/DS 二进制，
  **不是**编辑器专用、也不是被裁掉的死码；
- `Tools/PMR4UnityCheck` 定义 `UNITY_EDITOR` 且引用真实 Unity2019 DLL ⇒ 实现区的 Light API **仍被真实 API 编译门覆盖**（§6.2 第一行）；
- `Tools/PMClientCheck` / `Tools/PMUnityGlueCheck` 不定义任何 `UNITY_*` ⇒ 只编纯数据区（保持它们原有的状态，不因新文件新增错误）。

为什么必须这样做（见 §7.1/§7.2）：这两个桩件门禁用 `PMUnity\**\*.cs` 通配符把新文件编进来，而它们的
`Light` 面不足以编过真实实现；`Tools/PMBattleContentRuntimeCheck` 则用**显式** `Compile Include`（不通配目录）。
三处需要改的文件都**不在**本轮写入边界内。

---

## 7. 登记项 / 阻塞（不掩盖、不越界修）

### 7.1 `Tools/PMUnityGlueCheck` 当前**预先存在**的红色（非本轮引入，已实证）

现象：5 条 `CS1061`，全部在 `Client/Assets/Scripts/PMUnity/PMUnityBattlePresentation.cs`
（`root.childCount` / `root.GetChild(i)`，即上一批相机/转身改动），而 `Tools/PMUnityGlueCheck/UnityStubs.cs`
的 `Transform` 没有这两个成员。

**实证方法（本轮真的做了"移除自己改动再复现"的对照）**：把新文件 `PMUnityBattleLighting.cs(.meta)` 临时移出、
并把 `PMUnityBattleMap.cs` 还原成基线后再 build ⇒ **同样 5 条错误、完全相同**；恢复后错误集合不变，
且全输出里 **0 条**错误提到 `PMUnityBattleLighting.cs`。⇒ 这是**既有的**、与本轮无关的红色，
两个涉及文件（`PMUnityBattlePresentation.cs`、`UnityStubs.cs`）都不在本轮写入边界内，**不代改、不擅自放宽**。

### 7.2 §6.3 的 `#if` 要彻底去掉，需要（**均不在本轮写入清单内**，故只登记）

1. `Tools/PMClientCheck/ClientStubs.cs`：给 `Light` 补 `type/range/spotAngle/innerSpotAngle/shadows/shadowStrength/
   shadowBias/shadowNormalBias/shadowNearPlane/cullingMask/renderMode/lightmapBakeType/bounceIntensity/useColorTemperature`，
   并补 `LightType/LightShadows/LightRenderMode/LightmapBakeType`；
2. `Tools/PMUnityGlueCheck/UnityStubs.cs`：新增同样的 `Light` 面（顺便补 `Transform.childCount/GetChild` 以解 §7.1）；
3. `Tools/PMBattleContentRuntimeCheck/PMBattleContentRuntimeCheck.csproj`：补一行
   `<Compile Include="..\..\Client\Assets\Scripts\PMUnity\PMUnityBattleLighting.cs" LinkBase="PMUnity" />`。

做完这三处即可删掉 `#if`（届时新模块在**全部**客户端门禁里都参与真实编译）。

### 7.3 其它预先存在的阻断（本轮未触碰、未改锚）

- `PMLegacyRetirementTest` 的 **G8.frozen-anchors-unchanged**：`Resources/PMNet/BattleMapV1.prefab` 与
  `Client/ProjectSettings/EditorBuildSettings.asset` 的 SHA 与冻结锚不符（用户/Unity 现有工作区状态）。
  这与 `_visual_camera_fix.md` §7.2 登记的是同一条，需要内容/构建组决定"重烘焙后更新锚"还是"回退该改动"。
- `Tools/PMR4UnityAdapterTest` 运行结果 **130 / 2**，两条失败为 `B2-3`、`B2-4`（相机俯仰/后距几何，属在飞的相机批次）。
  **与本轮无关**且可证：该工程的 csproj 只编 `PMUnityMoverInput.cs` 与 `PMBattleCameraGeometry.cs`，
  **不编** `PMUnityBattleMap.cs`，也不编本模块。

### 7.4 模块内已声明的运行期边界（诚实声明）

- 灯只做"参数复制 + 建对象"，**不做**任何与渲染质量相关的调优（不加 postprocessing、不动阴影距离/质量设置）。
- 若宿主将来把 `ClientLightCount` 用于逻辑判断会被视为误用：它只是**可观察口径**，DS/客户端权威对局不依赖它。
- 创建失败时本局**没有**蓝/青氛围（退回现状"偏暗"），不会崩、不会重试、不会静默改别的设置；失败原因在 `LightingError` + 日志。

---

## 8. 剩余实机（PENDING_USER，本轮不做）

以下只能在 Unity2019 + 真实双客户端/DS/Lobby 同版本下确认，**本报告不声称已通过**：

1. 正式客户端进场后：地面/摆件呈蓝-青氛围（`ClientLightCount == 3`、日志出现"客户端专有光照已创建"）；
   与旧 HYLDGame 的画面对照记录（色彩差异属预期：`HYLDStart` 暖方向光仍叠加、无烘焙 GI）。
2. DS 端：`ClientLightCount == 0`、场景内不存在 `[PMNetBattleLights]`、无 Light 组件（DS 日志 + 场景核对）。
3. 退局/换局/第二局：`Camera.allCameras` 之外还应核对**不残留第二个 `[PMNetBattleLights]`**（
   可用 `Resources.FindObjectsOfTypeAll<Light>()` 或场景层级核对），且共享同一隔离物理世界版本。
4. 灯与物理互不干扰：贴墙/出生位/移动碰撞行为与改动前一致（本模块无 Collider/Rigidbody）。
5. 阴影性能观感：3 盏实时阴影（其中 1 盏方向光）在目标机型上的开销；若不合适由用户决定是否降为无阴影/单盏。
6. 诊断模式（`PMR3TestScene` 胶囊）行为逐字未变。

---

## 9. 未做 / 禁止项确认

未启动 Unity、未启动/停止任何用户服务、未打包、未 `git add`/`commit`/`push`、未 SVN 写操作、未递归委派；
未改 `HYLDGame.unity`/`HYLDStart.unity`/`BattleMapV1.prefab`/`PlayerVisualV1.prefab`/`BattleContentV1.json`/
manifest/种子/digest；未改 DS 代码、协议、DS 物理、`PMR3TestScene`、旧相机/Light 资源；
未改 `Resources/**`；未新增 postprocessing 或复制整个旧场景；
未改本轮写入清单外的任何文件（含三个被发现需要补充的桩件/csproj —— 只登记，不代改）。
