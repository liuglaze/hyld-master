# R4-C / C1+C2 内容构建与运行接口 —— 独立对抗复核与修复报告

> 范围：对 R4-C 的 C1（`Client/Assets/Editor/PMBattleContentBuild.cs`）与 C2
> （`Client/Assets/Scripts/PMUnity/PMBattleContent{Manifest,Map,Presentation}.cs`）做**独立对抗复核**，
> 只修**能证实的真实缺陷**，并把不可执行的部分明确记为 PENDING_USER。
>
> 状态唯一源仍是 `Docs/plans/net-architecture-migration.md`；本报告**不改主计划、不改契约、
> 不碰宿主/Server/另一组消费者 Tools**。本轮**未启动 Unity、未执行菜单、未生成任何资产、
> 未抢编辑器锁、未提交、未递归委派、未执行任何 svn/git 写操作**。
>
> 上一轮 C1/C2 报告（`_r4c_build_report.md` / `_r4c_runtime_report.md`）的结论**部分被本报告推翻**：
> 最关键的一条是「源场景编辑模式脚本审计通过」——实测该审计**永远失败**，因此**三个产物在上一轮
> 之后仍然不可能被生成出来**，`PMDsBuild` 也**必然**在中止构建（见 §2.1）。

---

## 0. 结论速览

| 项 | 结论 |
|---|---|
| **F1（阻断级）** | C1 的编辑模式脚本审计把 Unity 自带 UI 组件（`Image`/`Text`/`CanvasScaler`/`Button`/`Slider`，共 **55 个组件**）当成"未审计脚本" ⇒ 审计恒失败 ⇒ `PrepareContent()` **恒返回 false** ⇒ 三个 Resources 产物**永远生成不出来**，`PMDsBuild.BuildWindowsHeadlessDs` **必然中止**。**已修**（按脚本来源分桶）。 |
| **F2（高）** | C1 的逐字段拷贝里「数组长度」那段是**不可达代码**（`propertyType == ArraySize` 先被 `continue` 掉）⇒ `MeshRenderer.m_Materials` / `LODGroup.m_LODs` **永远拷不过去** ⇒ 地图"有 MeshFilter 没材质"，而上一轮的回读校验**不查材质** ⇒ 会静默出一张无材质地图。**已修**（数组先处理 + 拷后回读长度自证 + 回读查网格/材质）。 |
| **F3（中）** | 源场景**已打开且脏**时不做脏检查（脏检查只加在"由我们打开"的分支，恰好是永不命中的一侧）⇒ 用磁盘 SHA 烘焙内存里被改过的内容，digest 不可追溯。**已修**。 |
| **F4（中）** | 失败时清除 manifest 只调 `AssetDatabase.DeleteAsset` 且**不自证** ⇒ 可能留下"仍然有效的旧 manifest"（正是任务点名的风险）。**已修**（删除自证 + File.Delete 兜底 + 无效占位兜底）。 |
| **F5（中）** | 编辑模式脚本审计发生在 `OpenScene` **之后** ⇒ 违规脚本的 `Awake/OnEnable` 已经先执行过。**已修**（新增"打开之前"的文本预检，违规就**不打开场景**；打开后的运行时审计保留为第二道门）。 |
| Spawn 地面被当障碍 / 相机 transform / Animator 参数 / Plane+MeshFilter 引用 / 复制后 active / 全局随机 / digest 派生 | **复核未复现缺陷**，逐条给出推导与数据证据（§3）。 |
| 4096 白名单 vs 适配器 32 命中缓冲 | **真实残留风险**，适配器不在写入边界内 ⇒ 只登记 + 给出量化手段（§5）。 |
| 三门槛复验 | `PMBattleContentBuildCheck` / `PMBattleContentRuntimeCheck`（真实 Unity 2019.4 DLL）均 **0 错误**；`PMBattleContentManifestTest` **92 项 0 失败**（退出码 0）；顺带 `PMUnityGlueCheck` **0 错误**（无一风险回归）。 |
| 负向注入（3 例，含恢复 SHA 校验） | 全部按预期被门禁抓住 / 让测试变红，恢复后 SHA 一致（§4.3）。 |
| 资产烘焙 / 真机 | **仍未执行**（PENDING_USER）：本机不启动 Unity。 |

---

## 1. 必读证据（按任务指定顺序完整读完）

| 序 | 文档 | 对本轮的直接作用 |
|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 仓库入口（指向客户端/服务端/迁移计划三份下游文档） |
| 2 | `Client/Assets/AGENTS.md` | §1.1 客户端/DS 同二进制运行时判定（C2 表现层拒绝 DS 的依据）、§2 坐标系、§11 文档同步约束 |
| 3 | `Server/AGENTS.md` | 服务端"不含 Unity"的边界（确认本轮不该碰 Server） |
| 4 | `Docs/plans/net-r4c-content-contract.md`（全文） | **冻结契约**：三产物路径、manifest schema、seed=0x52444301、digest 派生、C1/C2 逐条要求 |
| 5 | `Docs/plans/_r4c_build_report.md`（全文） | 上一轮 C1 报告：本轮**推翻其 §3.1(a) 的普查结论**（见 §2.1） |
| 6 | `Docs/plans/_r4c_runtime_report.md`（全文） | 上一轮 C2 报告：本轮复核其"允许集对齐""出生位失败面"等论断 |
| 7 | `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md` | 本机 shell 纪律（本轮只用 bash 单引号 heredoc + `dotnet`，未用 PowerShell 语法） |

**由文档决定的首搜入口（非盲搜）**：`Client/Assets/Editor/PMBattleContentBuild.cs`、
`Client/Assets/Editor/PMDsBuild.cs`（只读）、`Client/Assets/Scripts/PMUnity/PMBattleContent{Manifest,Map,Presentation}.cs`、
`Tools/PMBattleContent{BuildCheck,RuntimeCheck,ManifestTest}/*`、`Client/Assets/Scripts/PMUnity/PMUnityMoverCollisionQuery.cs`
（只读，确认体检量语义与 32 容量）、`Client/Assets/Scripts/PMMover/PMMoverState.cs`（只读，`PMMoverDefaults` 胶囊尺寸）、
直接源证据 `Client/Assets/HYLD1.0/Scripts/OldScripts/ScenseBuildLogic.cs`、`Client/Assets/Scenes/HYLDGame.unity`、
`Client/Assets/Resources/Remake/Player.prefab`、`D:/Unity/2019.4.8f1/...`（真程序集与内置包）。

**工具与方法**：`fffind`/`ffgrep` 定位；文本 YAML 用脚本解析（不落仓内临时文件）；
真实程序集元数据用**临时 net8 程序**（放在系统临时目录，不在仓库内）加载
`Client/Library/ScriptAssemblies/UnityEngine.UI.dll` + `D:/Unity/2019.4.8f1/Editor/Data/Managed/UnityEngine/UnityEngine.CoreModule.dll`
实测属性继承；三个门禁用 `dotnet build` 实跑。

---

## 2. 严重发现（含可核验证据）

### 2.1 F1（阻断级）：编辑模式脚本审计判据错误 ⇒ **烘焙恒失败**、DS 构建恒中止

**代码位置（修前）**：`PMBattleContentBuild.SourceSceneScope.AuditEditModeScripts`

```csharp
bool editMode = Attribute.IsDefined(type, typeof(ExecuteInEditMode))
                || Attribute.IsDefined(type, typeof(ExecuteAlways));
if (!editMode) { continue; }
if (!Contains(EditModeToleratedScriptNames, type.Name))   // 白名单只有 {EasyJoystick, EasyButton}
{
    offenders.Add(...);          // ⇒ 任何其它"编辑模式类型"都判违规
}
```

**证据 A（真实程序集元数据，本机实测）**：`ExecuteAlways` / `ExecuteInEditMode` 的
`AttributeUsage.Inherited == true`，而 **ugui 2019.4 的 `UnityEngine.UI.Graphic` 带 `[ExecuteAlways]`**
（`com.unity.ugui/Runtime/UI/Core/Graphic.cs:19`），`CanvasScaler.cs:8`、`Selectable.cs:9`、`Slider.cs:8`
同样带。实测结果（读本工程编译产物 + 真实引擎程序集）：

```
ExecuteAlways.AttributeUsage.Inherited = True     (ValidOn = All)
ExecuteInEditMode.AttributeUsage.Inherited = True (ValidOn = All)

UnityEngine.UI.Image        -> C1判据(含继承)=True
UnityEngine.UI.Text         -> C1判据(含继承)=True
UnityEngine.UI.CanvasScaler -> C1判据(含继承)=True
UnityEngine.UI.Button       -> C1判据(含继承)=True
UnityEngine.UI.Slider       -> C1判据(含继承)=True
UnityEngine.UI.GraphicRaycaster -> C1判据=False（无该标记）
EasyJoystick / EasyButton   -> C1判据=True（工程内，白名单内）
ScenseBuildLogic / TouchLogic / HYLDPlayerController / Toolbox / ... -> False（工程内旧玩法脚本一律无标记）
```

**证据 B（源场景组件普查，按 `m_Script` guid × 真实类型）**：`HYLDGame.unity` 共 **89** 个
MonoBehaviour 组件，其中旧判据判为"编辑模式"且**不在白名单**内的组件数：

```
Image          32  (package: com.unity.ugui)   flagged=True
Text           14  (package: com.unity.ugui)   flagged=True
CanvasScaler    4  (package: com.unity.ugui)   flagged=True
Slider          3  (package: com.unity.ugui)   flagged=True
Button          2  (package: com.unity.ugui)   flagged=True
--------------------------------------------------------------
旧判据 offenders 合计 = 55  ⇒ 每次烘焙都必定失败
```

**后果链**：`AuditEditModeScripts` 返回 false ⇒ `SourceSceneScope.Open` 返回 null ⇒
`PrepareContentInternal` 立即返回 false ⇒ `PrepareContent()` 恒 false ⇒
① 菜单 `Build/Prepare PMNet Battle Content` 永远弹"失败"、② `Resources/PMNet/*` 三个产物永远不存在、
③ `PMDsBuild.BuildWindowsHeadlessDs` 在 C1 就绪门处**必然** `Finish(batchMode, 1)` 中止。

**上一轮报告错在哪**：`_r4c_build_report.md` §3.1(a) 的普查只解析了 `Assets/` 下的 `.cs`，
把 8 个未解析的 guid 直接判为"标准 UI 组件（随包安装），**无编辑模式标记**"——**这一句是错的**，
而且它正好是"是否恒失败"的分水岭。这与任务点名的"代码虽可编但生成必失败"完全吻合：
`dotnet build` 全绿（本文件确实能编译），但**一条代码路径永远走不到成功**。

**为什么这不算"弱化拒绝门让生成绿"**（这一点必须说清，否则修法会被误读）：
①门的**目的**是"加载源场景时不执行旧游戏 Awake"，而旧判据并没有实现这个目的（它把 Unity 自带的
UI 组件当违规项），它只是**不可满足**；②修后**工程内脚本仍然一眼严判**（必须命中
`{EasyJoystick, EasyButton}` 白名单，否则失败），真正会被玩法代码污染的路径**没有放松**；
③新增了**更早、更强**的一道门（`PreflightProjectEditModeAudit`：违规就**不打开场景**），
比原来"打开后才报"更强；④来源无法判定的组件仍然 **fail closed**。
⇒ 拒绝门是**改正 + 收紧**，不是放开。

**修法（不改"拒绝门"语义，改的是"谁来当违规项"）**：审计按**脚本来源**分桶——
`Assets/**` 的脚本走严判（必须命中白名单 `{EasyJoystick, EasyButton}`，否则失败）；
Unity 自带程序集（`UnityEngine*` / `Unity.*`）与包路径（`Packages/`、`PackageCache`、`BuiltInPackages`）
只记入摘要不判失败；**来源无法判定仍然 fail closed**。修后本场景的判定是
"offenders = 0、包/引擎桶 = 55 个组件（5 个类型名）、工程内桶 = {EasyJoystick, EasyButton}"，
并且摘要会把这三桶都打印出来（不再有"相信今天的普查"的黑箱）。

### 2.2 F2（高）：`SerializedObject` 数组字段**永不拷贝**（地图材质静默丢失）

**代码位置（修前）**：`PMBattleContentBuild.CopyComponentFields`

```csharp
if (it.propertyType == SerializedPropertyType.ArraySize)
{
    continue;   // 由 isArray 分支统一设置长度   ← 这行先执行
}
...
if (it.isArray)
{
    target.arraySize = it.arraySize;           // ← 永远不可达（数组头本身就是 ArraySize）
    continue;
}
```

`SerializedProperty.propertyType` 对**数组属性本身**就是 `ArraySize`，所以上面"设长度"的分支是
**死代码**：目标数组长度恒为 0，随后 `m_Materials.Array.data[0]` 在目标上 `FindProperty` 返回 null
⇒ 被 `continue` 跳过。受影响的是所有数组字段，最要命的是 `MeshRenderer.m_Materials` 与
`LODGroup.m_LODs`（`m_Mesh` 不是数组 ⇒ **网格引用本身是正常拷贝的**，见 §3.4）。

**证据（源场景）**：291 个 MeshRenderer **全部**有 1 个材质引用（guid 可解析，0 个悬空）；
地面 `HYLDGameTatal/MAP/Plane` 的 MeshRenderer 材质 guid `9a4c9669...`。
**后果**：地图 tile 与地面会丢材质（渲染成无材质/默认外观），而上一轮的回读校验
`VerifyMapPrefabLoaded` **只查组件类型/Collider/active，不查材质** ⇒ 静默出图。

**修法**：把"数组长度"提到任何 skip **之前**（`propertyType == ArraySize` 一律先 `arraySize = 源长度`，
元素仍由后续迭代逐个拷贝），并在拷贝后新增 `VerifyArraySizes` **回读比对每个数组长度**（不一致即让烘焙失败）；
回读校验同时新增"每个 MeshFilter 必须有网格、每个已启用 MeshRenderer 必须有非 null 材质、
LODGroup 不能是 0 级"（这三条正是"引用/材质没落地"的探测点）。

### 2.3 F3（中）：源场景"已打开且脏"时不做脏检查 ⇒ digest 不可追溯

`SourceSceneScope.Open` 只在"由我们 OpenScene"的分支里检查 `opened.isDirty`；若用户**已经开着**这张场景
（看地图时最常见），则**跳过**该检查，直接用内存内容烘焙、却把**磁盘 SHA256** 写进 digest ⇒
摘要与实际被读取的内容不对应（用户未保存的改动也不会落盘，双重不一致）。

**修法**：已打开分支也判 `isDirty`，脏就明确失败并给出补救（先 Ctrl+S 或关闭该场景）。

### 2.4 F4（中）：失败时"清除 manifest"不自证 ⇒ 可能留下 valid manifest

修前两处只写 `AssetDatabase.DeleteAsset(ManifestAssetPath)`（删不掉时不抛异常），
失败兜底同样只依赖这一句 ⇒ 恰好会出现任务点名的"发布失败留下旧 valid manifest"：
消费者（C2/C3）会照旧接受它，而磁盘上的 prefab 可能已被本次烘焙覆盖（digest 与实际内容不再对应）。

**修法**：新增 `RemoveManifestOrInvalidate`，三步自证：
① `AssetDatabase.DeleteAsset` + `Refresh` → ② 文件仍在则 `File.Delete` + `Refresh` →
③ 仍在则写入**无效占位 manifest**（`formatVersion=0`）并**再解析一次确认它确实通不过 C2 强校验**；
连③都失败就返回 false 并以"严重"级别记进摘要（调用方必须中止）。进流程的前置清除与失败兜底都走它。

### 2.5 F5（中）：审计发生在 `OpenScene` **之后** ⇒ 违规脚本的 Awake 已先执行

契约允许"Editor 安全预览场景读取"**或**"另一经验证不会执行旧游戏 Awake 的编辑模式方案"。
修前只有"打开后再审计"，对**未预料到**的编辑模式脚本而言，它只能**事后报告**
（`Awake/OnEnable` 已经跑过）。修后新增 `PreflightProjectEditModeAudit`：**在 OpenScene 之前**
读场景文本里的 `m_Script` guid，只对**工程内** `.cs` 解析类型并查编辑模式标记，违规就**不打开场景**；
包/内置包脚本与未解析 guid 只计数（分别交"来源分桶"与"missing script 不可能执行"处理）；
场景不是 ForceText 时**明说预检不可用**（不假装成功），由打开后的运行时审计兜底。

---

## 3. 复核"未复现缺陷"的风险（逐条给推导，避免把"看着像"当结论）

| # | 任务点名的风险 | 复核结论 | 证据/推导 |
|---|---|---|---|
| 3.1 | Spawn 脚下"碰撞厚度"/Overlap 把**地面当障碍**导致 **6 个出生位全拒** | **未复现** | ①`supportTopY = SpawnProbeStartY - halfHeight - ground.Distance`，而 `ground.Distance` 自**胶囊足底**起算（适配器 `QueryGround` 分支 2 的 CapsuleCast 距离）⇒ 相减恰好回到地面顶面 Y；②PhysX TOI 扫掠**保守偏早**（最多早约一个 contactOffset≈0.01 m）⇒ `supportTopY ≥ 地面顶面` ⇒ `bounds.max.y <= supportTopY + 1e-3` 必成立 ⇒ 地面被正确排除；③排除条件只作用于**非 trigger 硬障碍白名单**内、且 6 个出生位与最近非地面 Collider 的间距远大于胶囊半径 0.4 m（`PMMoverDefaults.CapsuleRadiusMeters=0.4`、`CapsuleHalfHeightMeters=1`）。该推导已写入 `SpawnSupportToleranceMeters` 的 `<remarks>` 供 C4 实机复核。 |
| 3.2 | Spawn 位置是否真的没被障碍占住 | **数据核对通过** | `map2` 35×21（实际用 33×21=693 格：658×`0` + 35×`2`，与上一轮报告一致）；出生格 `(i,j)=(31,5)/(31,10)/(31,15)/(1,5)/(1,10)/(1,15)` **全为 0**；±2 格邻域内无任何障碍格。 |
| 3.3 | 相机 transform 错 | **未复现** | 测试相机是角色根的**子物体**：`localPosition=(0,1.5,-6)`、`localRotation=Euler(12,0,0)`；Unity 相机沿自身 +Z 观察，根朝向 `Euler(0,yaw,0)`（出生 yaw=0）⇒ 相机在角色后方 6 m、俯 12° 看向角色（相机 y=2.5、6 m 处视线落点 y≈1.22 ≈ 角色胸高），"跟随"由层级关系提供、不读任何旧静态。已知边界：根 `Scale≠1` 时相机**偏移量**会随缩放（不影响视口朝向），已登记。 |
| 3.4 | Plane / MeshFilter 引用（"mesh 引用没拷过去"） | **未复现**（`m_Mesh` 是 ObjectReference，非数组） | `MeshFilter.m_Mesh`、`MeshCollider.m_Mesh`、`MeshRenderer.m_Materials` 的**数组**部分才是 F2 的受害面；本轮修复后回读校验会显式验证"每个 MeshFilter 有网格"。 |
| 3.5 | 复制后物体 active | **符合预期** | tile 的 `activeSelf` 逐字段取自模板：floors 模板在源场景就是 `m_IsActive: 0`（且**没有** Collider）⇒ 产物同样 inactive（与旧链运行期行为一致，不渲染、不参与物理）；墙/障碍/树/地面均 active，C2 的"Collider 必须 enabled 且 activeInHierarchy"因此不会误拒。**283 个 inactive 地板 tile 属"语义等价的纯体积"**，本轮**不擅自裁剪**（契约：不得用简化替代实际地图）。 |
| 3.6 | 全局随机被改动 | **未复现** | 全文件对 `UnityEngine.Random` 只有"读 state / finally 写回 / 比对是否变化并写进摘要"三处；生成用自带 `xorshift32(0x52444301)`；摘要里有一行 `UnityEngine.Random.state 未被改动=是/否`。 |
| 3.7 | 摘要随机实例 ID / 不稳定路径 | **基本未复现，但有一处与上一轮报告口径不符（已记录）** | digest 用资产路径、对象名、mesh 顶点数/bounds、`"R"`+InvariantCulture ⇒ 可复现。**但** `AppendPaletteSection` 里确实用了 `AssetDatabase.TryGetGUIDAndLocalFileIdentifier` 的 **localFileID**（`palette.X[i].source=scene:<guid>:<localId>:<name>`），而上一轮报告 §3.7 声称"刻意不用本地子资产编号"。localFileID 是场景 YAML 里**内容锚定**的持久标识（与 `GetInstanceID` 不同），同一份场景重复烘焙稳定；若将来用其它工具重写场景导致 fileID 变化，只会让**已发布的握手 digest 失效并触发重烘**，不会造成两端静默不一致。本轮**保留**该行为并如实登记（不额外改 digest 口径，以免无谓地改冻结协议）。 |
| 3.8 | Manifest 派生与"保留 digest" | **未复现缺陷** | C1 直接调用 C2 的 `TryDeriveCollisionDigestAndWorldVersion`（无第二份实现），写盘前先删 manifest、写盘后回读 `TryParseJson` 强校验；`ManifestTest` 的 C/G 组已把"手写常量/大端/0 值"全部钉成失败用例。 |
| 3.9 | prefab `Start` 旧脚本 | **未复现** | 地图侧白名单外组件一律 fail-closed（源模板子树只有 Transform/MeshFilter/MeshRenderer/Collider，实测 0 个 MonoBehaviour）；角色侧 `Object.Instantiate` 后逐类型删除白名单外组件（含 `HYLDPlayerController`/`PlayerLogic`/missing script/`移动型大招`），并在回读校验里拒绝任何残留 MonoBehaviour。两份产物都不含任何脚本 ⇒ 不存在 `Start`。 |
| 3.10 | 角色清理后"骨骼/材质/Renderer 仍可用" | **构造上成立，且已加自检** | 角色走 `Instantiate`（组件/骨骼/材质由 Unity 整体克隆），本文件只**删组件、不删物体** ⇒ `SkinnedMeshRenderer.m_Bones`/`m_Mesh`/`m_Materials` 不受影响；实测源 prefab：SkinnedMeshRenderer 有 **30 根骨骼 + 网格 + 2 个材质**，5 个 MeshRenderer 全部有材质（其中 2 个 `m_Enabled: 0`）。C1 回读新增"缺网格/骨骼的 SkinnedMeshRenderer"与"已启用但无材质的渲染器"两条失败判定。 |
| 3.11 | 世界变换口径（把地图做小一半） | **上一轮结论正确** | 实测 `3D` localScale=0.5、`3D/MAP`=1、`HYLDGameTatal`/`HYLDGameTatal/MAP`=1、地面 `Plane`=10；源码 `SetParent(MAP)`（worldPositionStays=true）抵消 0.5 ⇒ tile 世界变换 = 落点值/identity/模板自身缩放 ⇒ 按世界等价存放（容器 identity）是对的。 |
| 3.12 | 组件白名单 C1↔C2 对齐 | **一致**（且 C1 更严） | C2 地图允许集 = Transform/MeshFilter/MeshRenderer/Collider/LODGroup/kinematic Rigidbody；C1 地图 fail-closed 于此集合（`SkinnedMeshRenderer`/`Light`/`Camera`/`Animator`/`AudioListener`/动态刚体都失败）。C2 角色允许 `Renderer` 基类（比 C1 保留集更宽），但 C1 会删掉其它 Renderer 子类 ⇒ 产物必然通过 C2。 |
| 3.13 | Animator 参数 | **存在且已校验** | 源 prefab：`Animator` 在 `Body` 上（Avatar `6ccf12d5…`、Controller `58bf525e…`、`m_ApplyRootMotion: 0`）；C1 显式再写 `applyRootMotion=false` 并要求控制器参数表里有 **Float 型 "Speed"**，否则失败（C2 侧再次 `HasFloatParameter` 判定，不存在就完全不设）。 |

---

## 4. 修复与测试

### 4.1 改动清单（严格限定在授权文件内，**无 public API 变更**）

| 文件 | 改动 | public API |
|---|---|---|
| `Client/Assets/Editor/PMBattleContentBuild.cs` | F1 来源分桶审计（新增 `EditModeScriptOrigin` / `ClassifyEditModeScriptOrigin`）；F5 新增 `PreflightProjectEditModeAudit` + `IsHex32` + 两个私有常量；F3 已打开分支补脏检查；F4 新增 `RemoveManifestOrInvalidate` 并替换两处清除点；F2 数组分支重排 + 新增 `VerifyArraySizes`（`CopyComponentFields` 增加 `List<string> failures` 参数，私有）；回读新增网格/材质/LOD 自检（新增私有 `HasUsableMaterial`）；`EnsureResourcesDirectory` 补父目录并显式确认；`AddOnce` 工具 | **不变**（`PrepareContentMenu` / `PrepareContent` / `ValidateContent` / `PrepareForBuild` 与全部公开常量签名保持不变） |
| `Client/Assets/Scripts/PMUnity/PMUnityBattleMap.cs` | 加载期新增"MeshFilter 必须有网格"失败判定（只用已桩化 API）；文档补充：`Physics.SyncTransforms` 在 2019.4 的**本地物理场景口径**限制、出生位容差的推导、`MaxColliderCount` 与适配器 32 命中缓冲的残留风险 | **不变**（属性/静态方法签名与释放后语义全部保持） |
| `Client/Assets/Scripts/PMUnity/PMUnityBattlePresentation.cs` | 组件校验新增"MeshFilter 必须有网格"；类头补充"骨骼/材质可用性在 C1 产出侧自检、本文件不引用未桩化类型"的边界说明 | **不变** |
| `Client/Assets/Scripts/PMUnity/PMBattleContentManifest.cs` | **未改**（复核未发现缺陷；且它是 C2 的冻结规则文件） | 不变 |
| `Tools/PMBattleContentManifestTest/Program.cs` | 新增 J 组 2 项：冻结资源键目录前缀；C1 三产物在磁盘上的**齐备性/可解析性**（缺任一份即失败；全都不存在时明确标注"未烘焙=PENDING_USER，不是通过"；存在时用 C2 规则真实解析） | 测试程序，无库 API |
| `Docs/plans/_r4c_content_review_fix.md` | 本报告 | — |

**未改动（边界声明）**：`PMDsBuild.cs`（只读 hook，未动一字）、`Tools/PMUnityGlueCheck/*`、
`Tools/PMBattleContent{BuildCheck,RuntimeCheck}/*`（既有门禁，未改）、`PMUnityMoverCollisionQuery.cs`、
任何宿主 / Server / 旧链 / 资产 / 场景 / 主计划 / 契约 / 另一组文件。

**编码**：`PMBattleContentBuild.cs`、三个 C2 `.cs`、`Program.cs` 改动后仍保持原有
（BOM + CRLF；`Program.cs` 为无 BOM + CRLF），`.meta` 未新增也未改（本轮没有新文件），
本报告为 BOM + CRLF（与 `Docs/plans/` 既有报告一致）。**没有新增 `.cs` 文件**（因此不需要新 meta）。

### 4.2 复现命令（本机实跑）

以下命令按 C1 报告的既有约定**把 `bin/obj` 重定向到系统临时目录**（`<TEMP>` 自选一个空目录），
以避免在仓库内留下构建产物；MSBuild 在 Windows 上也接受正斜杠路径，所以这里统一用 `/`：

```bat
set REDIR=-p:BaseIntermediateOutputPath=%TEMP%/pmr4c/obj/ -p:BaseOutputPath=%TEMP%/pmr4c/bin/

REM ① C1 门禁：真实 Unity 2019.4 程序集（UnityEditor.dll + UnityEngine.*Module.dll），netstandard2.0 + C#7.3
dotnet build Tools/PMBattleContentBuildCheck -c Release %REDIR%
REM    → 0 警告 / 0 错误

REM ② C2 门禁：真实 Unity 2019.4 程序集（不引用 UnityEditor）
dotnet build Tools/PMBattleContentRuntimeCheck -c Release %REDIR%
REM    → 0 警告 / 0 错误

REM ③ manifest 纯规则门禁（net8 + 仅 JsonUtility 替身）
dotnet build Tools/PMBattleContentManifestTest -c Release %REDIR%
dotnet %TEMP%/pmr4c/bin/Release/net8.0/PMBattleContentManifestTest.dll
REM    → PMBattleContentManifestTest: 92 项，失败 0 项。退出码 0

REM ④ 顺带确认没有把既有桩件门禁弄红（它此前因缺 Animator 桩件而红，见上一轮 C1 报告 §4c）
dotnet build Tools/PMUnityGlueCheck -c Release %REDIR%
REM    → 0 警告 / 0 错误
```

> 本次实际执行时用的是默认输出路径（`Tools/<proj>/bin|obj`，被 `.gitignore:77-78` 覆盖），
> **执行后已删除这三个工程新建的 `bin/obj`**，仓库内**不留下**本轮产生的文件。
> 复核者若要用默认路径重跑，请自行清理或照上面的 `REDIR` 重定向。
>
> **口径提醒**：`PMBattleContentManifestTest` 的 J 组依赖"从可执行文件目录向上找到同时含
> `Client` 与 `Tools` 的那一层"来定位仓库根。在仓库内（默认输出路径）运行 ⇒ **92 项 0 失败**；
> 若按上面的 `REDIR` 把输出重定向到系统临时目录，J 组会像既有的 I 组一样**降级为"非仓库内运行：
> 跳过磁盘一致性检查"** ⇒ 显示 **90 项 0 失败**（两者都不失败，只是少两条磁盘断言，这是刻意的
> 可降级设计，不是漏测）。
> 本机 Unity 托管程序集路径为 `D:/Unity/2019.4.8f1/Editor/Data/Managed`（与主计划"环境现状"一致）。

### 4.3 负向注入（证明门禁真的在看这些新代码，且恢复后字节一致）

| # | 注入 | 期望 | 实测 |
|---|---|---|---|
| 1 | 在 `PreflightProjectEditModeAudit` 内插入 `PMBattleContentManifest.__NEGATIVE_TEST__();` | `PMBattleContentBuildCheck` 编译失败，且报在 PMBattleContentBuild.cs 该行 | ✅ `PMBattleContentBuild.cs(837,37): error CS0117: "PMBattleContentManifest"未包含"__NEGATIVE_TEST__"的定义`（⇒ 新代码在编译区内、且解析到真实类型） |
| 2 | 在 `PMUnityBattleMap.CollectAndValidate` 新判定前插入同一句 | `PMBattleContentRuntimeCheck` 编译失败 | ✅ `PMUnityBattleMap.cs(649,37): error CS0117 ...` |
| 3 | 把 `PMBattleContentManifest.ResourceDirectory` 临时改成 `"PMNet2/"`（制造契约漂移） | `PMBattleContentManifestTest` 变红且退出码非 0 | ✅ 88 项 **6 项失败**，`EXIT=1` |

三次注入都用**临时备份 + 恢复后 `sha256sum -c` 校验**：`Client/Assets/Editor/PMBattleContentBuild.cs`
（`2b1098…`）、`Client/Assets/Scripts/PMUnity/PMUnityBattleMap.cs`（`b7e637…`）、
`Client/Assets/Scripts/PMUnity/PMBattleContentManifest.cs`（`69253c…`）恢复后均 **OK**（字节一致）。

### 4.4 关键发现的性质说明（避免误读）

- **F1 的性质**：它不是"编译错误"，而是"可编译但**唯一成功路径不可达**"——`dotnet build` 全绿、
  `PMDsBuild` 也会正常进入构建流程，然后在 C1 就绪门处中止。上一轮的三个 CLI 门禁**都无法**发现它
  （它们不执行编辑期逻辑），这正是本轮"对抗复核"能拿到的最高价值结论。
- **F2 的性质**：它是**静默内容损坏**（不是崩溃），且上一轮的回读校验恰好不覆盖材质 ⇒
  若无本轮修复，很可能表现为"地图能加载但看起来不对"，并在 C4 实机才暴露。
- 本轮**没有**把任何"看起来更准"的局部改动塞进物理适配器：`PMUnityMoverCollisionQuery` 一字未动。

---

## 5. 剩余风险与 PENDING（明确不可在 CLI 证实）

| # | 项 | 状态 | 说明 / 后续动作 |
|---|---|---|---|
| R1 | **资产真烘焙** | **PENDING_USER** | 需要一个真实 Unity 2019.4 会话点 `Build/Prepare PMNet Battle Content`。本轮只把"必然失败"改成"可以成功"，**没有**产出 `Resources/PMNet/*`（因此 `ManifestTest` 的 J 组此刻仍报"未烘焙"）。首次烘焙请重点看摘要里的：`预检（打开场景之前）`、`编辑模式脚本审计（打开后运行时复核）`、`数组长度不一致`（应为 0 条）、`地图组件清单：无材质渲染器=0`、`角色组件清单：无材质启用渲染器=0 缺网格/骨骼的 SkinnedMeshRenderer=0`、`contentDigest/collisionDigest/worldVersion`，以及第二次烘焙的 digest 是否**完全一致**。 |
| R2 | **4096 白名单 vs 适配器 32 命中缓冲** | **真实残留风险（不可在 C2 内修）** | `PMUnityBattleMap.MaxColliderCount = 4096`，而 `PMUnityMoverCollisionQuery.HitBufferCapacity = 32` 且**饱和即抛异常**。若大量 Collider 挤在同一查询体附近，表现是"出生位解析失败/驱动报错"（**fail-closed，不会静默给错值**）。适配器不在本轮写入边界内 ⇒ 只登记。量化手段：C4 读 `Query.GetStats().SaturationFailures` / `NonBoxBoundsClassifications` 与 `NonBoxColliderCount`。正式的 map2 白名单约 300 个几何（远低于 4096），风险概率低但未实测。 |
| R3 | **正式地图的非凸 `MeshCollider` 未经真机验证** | **PENDING（C4）** | 正式地图的地面是 **非凸 MeshCollider**（内置 Plane mesh），另有 well/coffin/dirtpile/tree 的 MeshCollider；而 B2/R4-B 的 PhysX 实证只覆盖**轴对齐 BoxCollider**（`PMR4UnityValidation`）。适配器对非 Box 用 `Collider.bounds` 超集近似（保守早挡、绝不漏墙、方向有效但非最小 MTD）。"场景查询对非凸三角网有效"是本项目的**假设**，需 C4 实测；本轮不声称任意形状精确。 |
| R4 | **PhysX 扫掠对"地面顶面"的偏差方向** | **推导正确但需实测一次** | §3.1 的结论依赖"TOI 扫掠保守偏早"。若某平台/版本出现"偏晚"（历史未见），`supportTopY` 会略低于地面顶，理论上可能让地面进入障碍判定 ⇒ 6 个出生位一起失败。C4 请打印 `map.DescribeSpawns()`：正常应 6/6 成功。失败时第一时间看是否**全部**失败（=该推导被推翻）而不是个别槽位。 |
| R5 | **相机偏移随根缩放** | **已知边界（低危）** | `CreateTestCamera` 的 `localPosition` 挂在角色根下 ⇒ 根 `Scale≠1` 时后撤距离会等比变化（视口朝向不受影响）。出生/默认 Scale=1 时精确。仅影响"可选测试相机"，不影响正式表现。 |
| R6 | **inactive 地板 tile（283/2919 落点中的地板）** | **有意保留** | 与源场景行为一致（源模板 `m_IsActive: 0`、无 Collider）。若主侧要裁剪以减小 prefab 体积，属**语义等价的体积优化**，必须改 C1 并重新握手 digest；本轮不擅自简化。 |
| R7 | **摘要里使用 scene localFileID** | **已登记（§3.7）** | 与上一轮报告"不用本地子资产编号"的措辞不一致；实际影响仅限"场景被其它工具重写后需重烘"，不会造成两端静默不一致。若要彻底落实原措辞，应作为**独立批次**改 digest 口径（会改冻结协议，需主侧决定）。 |
| R8 | **`uint collisionDigest` 的真实 JsonUtility 往返** | **PENDING（C1 首跑即验）** | 由 C1 写盘后回读 + `ManifestTest` J 组在产物出现后自动转为真实校验；本轮**未**在 Unity 内执行过。 |
| R9 | **LODGroup / 其它数组字段** | 已随 F2 一并修好，但**本场景无 LODGroup** | `m_LODs` 现在会正确拷贝；回读新增"LODGroup 必须 ≥1 级"的判定。首图用不到，留给后续换图。 |

---

## 6. 已检查范围 / 未做事项（合规声明）

**已读（只读）**：本报告 §1 列出的 7 份文档全文；`PMBattleContentBuild.cs`（全文 2630 行 → 修后 3307 行）、
`PMDsBuild.cs`（全文，未改）、C2 三个 `.cs`（全文）、`PMUnityMoverCollisionQuery.cs`（全文，用于判 32/非 Box 语义）、
`PMMoverState.cs`（`PMMoverDefaults` 尺寸）、`ScenseBuildLogic.cs`（`map2` 表 + `InitData` 循环逐行对齐）、
`HYLDGame.unity`（文本 YAML：根层级、模板调色板序列化数组、地面 `Plane` 组件、变换与激活位、
`m_Script` guid 普查）、`Resources/Remake/Player.prefab`（层级/组件/Animator/骨骼/材质）、
`ProjectSettings/EditorSettings.asset`（`m_SerializationMode: 2` = ForceText，预检前提）、
真程序集元数据（`UnityEngine.CoreModule.dll`、`UnityEngine.PhysicsModule.dll`、`UnityEngine.UI.dll`、`UnityEditor.dll`）、
ugui 内置包源码（`Graphic/CanvasScaler/Selectable/Slider`）与 `Tools/PMUnityGlueCheck/UnityStubs.cs`（用于避免给别组增负担）。

**未做（符合硬边界）**：未启动 Unity / 未点菜单 / 未生成或改写任何资产或场景 / 未抢编辑器锁 /
未改主计划与契约 / 未改 `PMDsBuild.cs` / 未改 `Tools/PMUnityGlueCheck`、`Tools/PMBattleContent{BuildCheck,RuntimeCheck}` /
未改 `PMUnityMoverCollisionQuery` / 未碰宿主与 Server / 未碰另一组消费者 Tools / 未 git-svn 写操作（无 add/commit/checkout/reset）/
未递归委派。仓库内**未新增**任何文件，除本报告外**未创建**任何文件。

**一处必须如实标注的自我限制**：F2 的修复（数组先处理）**无法在本机执行验证**——它需要 Unity 编辑期上下文；
本轮能证明的是：①修前那段"设长度"在 `SerializedPropertyType` 语义下**不可达**（静态可判定）；
②修后新增的 `VerifyArraySizes` 回读自证会让"没拷过去"变成**烘焙失败**而不是静默出图；
③两个回读自检（网格/材质）也会独立挡住同一类损坏。因此首跑要么成功、要么报出可归因的失败，**不会**再有静默坏内容。
