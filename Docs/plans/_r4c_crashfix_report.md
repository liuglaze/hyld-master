# R4-C / C1 烘焙崩溃修复实施报告（F1–F7）

> 范围：修 `Client/Assets/Editor/PMBattleContentBuild.cs` 在 Unity 2019.4 编辑期**真实 SIGSEGV** 的缺陷，
> 并把它能在 CLI 里钉死的部分做成门禁（新增 `Tools/PMBattleContentSceneFactsCheck`）。
> 状态唯一源仍是 `Docs/plans/net-architecture-migration.md`；**本报告不改主计划、不改契约、不改 manifest schema**。
>
> 本轮**未启动 Unity、未执行菜单、未生成任何 .prefab/.json 资产、未抢编辑器锁、未提交、未递归委派**。
> 因此**不声称崩溃已解决**：正确口径是「**用户下一次在 Unity 里执行菜单才能确认**」（见 §7）。
> 崩溃证据来自主侧锁定的 `C:\Users\luomingcong\AppData\Local\Unity\Editor\Editor.log`（本轮已逐行复核，
> 并核出**触发它的确切属性面与组件**，见 §2）。

---

## 0. 摘要（≤1500 字）

真机 Editor.log 报错原文见 §2.1：`CheckConsistency: GameObject does not reference component ... Fixing.`、
`Can't add component ... already added`、`Retrieving array size but no array was provided`、关临时场景
`... interests ... deregistered in Deactivate.`、关源场景 `Cannot unregister Collider ... not in the table.`（PhysicsManager.cpp:1450）；最后
`Got a SIGSEGV`。

根因一条：旧码 `tile.SetActive(template.activeSelf)` 在 `CopyComponentSet` **之前** ⇒ 向**非激活**对象
`AddComponent` MeshFilter/MeshRenderer/Collider。这类组件不走 Activate/Deactivate，transform change interests
永不注销、组件表与物理登记表被破坏，拆层级时崩溃。YAML 实证：`ScenseBuildLogic.floors` 指向的
`Plain02 (1)/Plain01 (1)` 就是 `m_IsActive: 0` 且无 Collider，2660 个地板落点全部命中；旧链 `MyInstantiate` 不
SetActive，故旧运行期这些地板同样不可见、无碰撞。

修复：F1 目标对象先 active 创建、拷完组件、最后才 SetActive（地面/tile/子物体同一纪律）；F2 模板 inactive 的
落点默认**跳过并计数**（摘要与 digest 都写），地面 inactive 显式失败；F3 AddComponent 前判重、返回 null 不再
继续；F4 只在 `isArray` 为真（窄 catch 兜底）时才读 `arraySize`；F5 销毁自建 root 前先 `SetActive(false)`、关临时
场景前确认已清空；F6 新增逐步落盘日志 `Client/Logs/PMBattleContentBuild.log`（FileShare.ReadWrite，每步 flush）；
F7 回读校验加 active/inactive 计数与 skippedInactive，并断言默认策略下无未激活 tile 根。
manifest 最后发布、digest 派生复用 C2，**未放宽**。

离线验证：V1 新增 `Tools/PMBattleContentSceneFactsCheck`（net8 纯 C#）**63 项全过**，9 个负向注入证明
非空转；V2 `PMBattleContentBuildCheck`（真实 Unity 2019.4）**0 错误**；V4 复跑 7 个关联门禁**全部退出码 0**
（ManifestTest 92 项、SessionTest 86 项）。未验证：Unity 编辑期 AddComponent/interests 行为、`isArray`/`arraySize`
语义、真实产物与 digest、崩溃是否消失——只能由用户点菜单确认。

## 1. 必读证据（按任务指定顺序完整读完）

| 序 | 文档 / 证据 | 对本轮的直接作用 |
|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 仓库入口（指向客户端/服务端/主计划三份下游文档） |
| 2 | `Client/Assets/AGENTS.md` | 客户端/DS 同二进制运行时判定、坐标系、§11 文档同步约束 |
| 3 | `Server/AGENTS.md` | 服务端「不含 Unity」的边界（确认本轮不该碰 Server） |
| 4 | `Docs/plans/net-r4c-content-contract.md` | 冻结契约：三产物路径、manifest schema、seed、digest 派生、C1 逐条要求 |
| 5 | `Docs/plans/_r4c_build_report.md` | 上一轮 C1 报告：**其 §5/§6 明确把「地板 tile 未激活」记成"刻意保留"**，正是本轮崩溃的成因 |
| 6 | `Docs/plans/_r4c_content_review_fix.md` | 上一轮对抗复核：F1–F5 的修复历史、`VerifyArraySizes` 的来历、三门槛现状 |
| 7 | `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md` | 本机 shell 纪律（本轮只用 bash + `dotnet`，未混用 PowerShell 语法） |

**源码/资产（只读，由文档指定入口，非盲搜）**：`Client/Assets/Editor/PMBattleContentBuild.cs`（全文 3307 行，修后 3989 行）、
`Client/Assets/HYLD1.0/Scripts/OldScripts/ScenseBuildLogic.cs`（`InitData` 循环 + `MyInstantiate` 逐行对齐）、
`Client/Assets/Scenes/HYLDGame.unity`（文本 YAML：`ScenseBuildLogic` 序列化块、五类模板子树、地面 `Plane`、层级）、
`Client/Assets/Resources/Remake/Player.prefab`（角色清理面核对）、`Tools/PMBattleContent{BuildCheck,ManifestTest,SessionTest,RuntimeCheck}/*`、
**真机日志** `C:\Users\luomingcong\AppData\Local\Unity\Editor\Editor.log`（≈39990–40500 行区间的 Bake 段）。

**工具与方法**：文本 YAML 用 Python 脚本解析（不落仓内临时文件）；负向注入一律「备份 → 注入 → 跑门禁 → 恢复 → `sha256sum` 校验」；
`dotnet build` 复验全部门禁；结果汇总落 `Tools/r4c-crashfix-verification.log`。

---

## 2. 根因（引用日志原文 + YAML 事实）

### 2.1 日志原文链（行号 = Editor.log 实际行号；`PMBattleContentBuild.cs` 行号 = **修复前**版本）

```
40139  CheckConsistency: GameObject does not reference component MeshFilter. Fixing.
       UnityEditor.SerializedObject:ApplyModifiedPropertiesWithoutUndo()
       ...:CopyComponentFields(...) (at Assets\Editor\PMBattleContentBuild.cs:2244)
       ...:CopyComponentSet(...)    (at Assets\Editor\PMBattleContentBuild.cs:2153)
       ...:BuildMapHierarchy(...)   (at Assets\Editor\PMBattleContentBuild.cs:1809 / 1846)
40151  Retrieving array size but no array was provided
       UnityEditor.SerializedProperty:GetArraySize()   ← get_arraySize
       ...:CopyComponentFields(...) (at ...:2231)      ← 旧码直接读 it.arraySize
40411  Can't add component 'MeshFilter' to F00003_Plain02 (1) because such a component is already added to the game object!
       UnityEngine.GameObject:AddComponent(Type)
       ...:CopyComponentSet(...) (at ...:2152)         ← 旧码 dest.AddComponent(component.GetType())
40423  Transform has 'MeshRenderer|gColliderChangeHandle_S|gColliderChangeHandle_TR' change interests present
       when destroying the hierarchy. Interests must be deregistered in Deactivate.
       UnityEditor.SceneManagement.EditorSceneManager:CloseScene(...)
       ...:PrepareContentInternal(StringBuilder) (at ...:663)   ← 关临时场景
40433  Transform has 'Renderer::kSystemParentHierarchy|gColliderHierarchyChangeHandle' hierarchy interests
       present when destroying the hierarchy. Interests must be deregistered in Deactivate.   (同上, 663)
40443  System is not interested in this transform, SetSystemInterested may only be called on transforms
       that are set interested.
       ...:SourceSceneScope:Dispose() (at ...:1175)             ← 关源场景
40454  Cannot unregister Collider from transform change interested as it was not in the table.
       [C:\buildslave\unity\build\Modules/Physics/PhysicsManager.cpp line 1450]   (同上, 1175)
40470  at PMNet.UnityEditor.PMBattleContentBuild/SourceSceneScope.Dispose () ...
       in D:\UGit\hyld-master\Client\Assets\Editor\PMBattleContentBuild.cs:1175
40477  Got a SIGSEGV while executing native code.
```

**三点必须说清的事实**：

1. 这些行号（663 / 1175 / 2152 / 2231 / 2244 / 2282）与**修复前**文件逐一对得上：
   `663` = finally 里 `EditorSceneManager.CloseScene(scratchScene, true)`；`1175` = `SourceSceneScope.Dispose()`
   里的 `CloseScene(_scene, true)`；`2152` = `dest.AddComponent(...)`；`2231` = `arrayTarget.arraySize = it.arraySize;`；
   `2244` = `dobj.ApplyModifiedPropertiesWithoutUndo()`；`2282` = `target.arraySize != it.arraySize`。
2. **没有留下任何产物**：Editor.log 全程只有一次 `Start importing Assets/Resources/PMNet`（那是
   `EnsureResourcesDirectory()` 建的空目录），**没有** `BattleMapV1.prefab` / `PlayerVisualV1.prefab` /
   `BattleContentV1.json` 的任何导入记录；盘上 `Client/Assets/Resources/PMNet/` 至今**是空目录**。
3. **一行摘要都没有**：入口摘要只在 `finally` 末尾 `Debug.Log` 一次，而崩溃就发生在 finally 里
   ⇒ Console / Editor.log 里看不到 `[PMBattleContentBuild]` 的任何步骤，只能靠 Unity 内部报错反推
   —— 这正是 F6 存在的理由。

### 2.2 根因（一条，可解释上面全部症状）

旧码 `BuildMapHierarchy`（修复前 1844/1846 行）：

```csharp
tile.SetActive(template.activeSelf);            // ← 先把目标设成模板的（可能是 0）
if (!CopyComponentSet(template, tile, true, out stripped)) { ... }   // ← 再往"非激活对象"AddComponent
```

向**非激活** GameObject 加 `MeshFilter/MeshRenderer/*Collider` 时，Unity（2019.4）既不把组件挂进
GameObject 的组件表，也不走 `Activate/Deactivate`：

- 组件表没挂上 ⇒ `MeshRenderer` 的 `[RequireComponent(typeof(MeshFilter))]` 再补一个 MeshFilter，
  第二个就报 **`already added`**（40411）；随后的 `ApplyModifiedPropertiesWithoutUndo` 触发 Unity 的
  **`CheckConsistency ... Fixing`** 去补救（40139/40216/40281/40346）。
- 组件表/序列化状态错乱 ⇒ 旧码那句裸读 `it.arraySize` 进了 Unity 的
  **`Retrieving array size but no array was provided`**（40151…，栈 `get_arraySize`）。
- 组件注册的 transform change interests **永不注销**（没有 Deactivate 可走）⇒ 拆层级时报
  **`change interests present when destroying the hierarchy`**（40423/40433），物理侧登记表被破坏 ⇒
  关源场景时报 **`Cannot unregister Collider ... as it was not in the table`**（40454）⇒ **SIGSEGV**。

### 2.3 源场景 YAML 事实（本轮用 Python 解析 `HYLDGame.unity` 实证，并被 V1 钉成门禁）

| 事实 | 实测值 | 意义 |
|---|---|---|
| `ScenseBuildLogic` 组件 | 挂在 `HYLDGameTatal/3D`，`mapx: 33` / `mapy: 21` | 与契约冻结一致，未扩到表的 35 行 |
| `floors`（4 条，去重后 2 个） | `Plain02 (1)`、`Plain01 (1)`，**`m_IsActive: 0`**，组件只有 `Transform(4)/MeshFilter(33)/MeshRenderer(23)`，**无 Collider** | 2660 个地板落点全部是「未激活 + 无碰撞」的旧语义负担，且正是崩溃源 |
| `walls`(4) / `obstacles`(8) / `trees`(7) / `Grasses`(1) | 模板根**全部 `m_IsActive: 1`**；墙带 `BoxCollider(65)`，井/棺/土堆/枯树带 `MeshCollider(64)` | 「跳过未激活落点」不会误伤墙/障碍/树；非 trigger Collider > 0 由它们保证 |
| 地面 | `HYLDGameTatal/MAP/Plane`，`m_IsActive: 1`，组件 `Transform/MeshFilter/MeshRenderer/**MeshCollider(64)**`，scale 10 | floors 模板没有 Collider ⇒ **地面是唯一碰撞地板**，它 inactive 必须显式失败 |
| 运行期 tile 容器 | `HYLDGameTatal/3D/MAP`（scale 1）；`3D` 自身 scale 0.5 | 印证 `SetParent(MAP)`（worldPositionStays=true）抵消 0.5 的世界等价口径 |
| 旧链行为 | `ScenseBuildLogic.MyInstantiate` = `Instantiate(prefab, pos, rot)` + `SetParent(MAP)`，**不 SetActive** | `Instantiate` 继承模板 `activeSelf` ⇒ 旧运行期地板同样不可见、无碰撞 |

### 2.4 一处**推翻主侧假设**的发现（如实记录）

任务描述里把 2152 处报错列为「CopyComponentSet: Can't add component ... already added」，并提示
「若发现『同一 dest 被同一 source 处理两次』的根因，查清并如实报告」。**本轮查清了，结论是它并非
「同一 dest 被处理两次」**：`dest` 每次都是全新的 `new GameObject(...)`，`CopyComponentSet` 对同一
dest 只会被调用一次（地面一次、每个 tile 一次、每个子物体一次，代码上互不重叠）。
真正原因是 §2.2 的**非激活对象 AddComponent**：Unity 没把组件登记进 GameObject 的组件表，
`[RequireComponent]` 于是补出重复组件。证据是同一时间窗内成对出现的
`CheckConsistency ... Fixing`（补充登记）+ `already added`（因未登记而重复补），以及它们的调用栈
都落在 `CopyComponentSet → CopyComponentFields → ApplyModifiedProperties` 这一条上。

---

## 3. F1–F7 逐条改动（文件：`Client/Assets/Editor/PMBattleContentBuild.cs`）

| # | 要求 | 落地（修后行号） | 关键点 |
|---|---|---|---|
| **F1** | 先 active 创建 + 拷组件，最后才 `SetActive` | `BuildMapHierarchy`(2293)、`CopyChildHierarchy`(2441) | 地面/ tile 根 / 子物体三处都改为「new GameObject（默认 active）→ `CopyComponentSet` → 递归子树 → `SetActive(模板最终态)`」。这样 AddComponent 的那一刻**整条父链都 `activeInHierarchy`**（父 tile 也要等方法返回后才施加最终态）。同时删除原 `tile.SetActive(...)` / `destGo.SetActive(...)` / `groundClone.SetActive(...)` 的前置调用 |
| **F2** | 模板 inactive ⇒ 默认跳过并计数；地面 inactive ⇒ 显式失败；保留开关 | `SkipInactivePlacements`(254)、`PlacementSelection`(1888)、`SelectPlacements`(1921)、`AccumulateSelectionCounts`(1954)、地面门(≈786) | 判据是**模板自身的 `activeSelf`**（不是源场景 `activeInHierarchy`）：旧运行期等价于「克隆体继承模板 activeSelf + 父级 MAP 是激活的」。跳过数进**摘要**（`落点选择（skipInactive=…）：built=… skippedInactive=…（按类别…）`）**与 digest**（`layout.skipInactive` / `layout.built.count` / `layout.skippedInactive.count` / `layout.skippedInactive.<cat>`，用 `SortedDictionary` 保证顺序确定）。地面 `!groundPlane.activeSelf` ⇒ 明确失败并说明「floors 模板没有 Collider ⇒ 地面是唯一碰撞地板」 |
| **F3** | 判重 + null 保护 + 查清重复根因 | `CopyComponentSet`(2676) | `dest.GetComponent(componentType)` 命中 ⇒ 记 `重复组件:<类型>:<路径>(源物体 …)` 并 `continue`（不再让 Unity 打错误、也不在坏组件上拷字段）；`AddComponent` 返回 null ⇒ 记 `AddComponent 返回 null:` 并 `continue`（不再 `new SerializedObject(null)`）。Transform 跳过逻辑保持原样。根因见 §2.4（**不是**同一 dest 处理两次） |
| **F4** | 只在 `isArray` 为真时读 `arraySize` | `TryGetArraySize`(2741)、`CopyComponentFields`(2808)、`VerifyArraySizes`(2872) | 新增统一守卫：`!property.isArray` ⇒ 返回 false（调用方**跳过**，不报失败）；即使 `isArray` 为真也把这次读取包在**窄 catch** 里——真机日志证明「`propertyType == ArraySize` 但 Unity 拒绝当数组」确实存在；`CopyComponentFields` 的数组长度分支与 `VerifyArraySizes` 的所有 `arraySize` 读取一律改走该守卫，赋值改为已校验的 `sourceSize` |
| **F5** | 先 `SetActive(false)` 再销毁；关场景前确认自建对象已清空 | `DestroySelfBuiltRoot`(1007)、`EnsureScratchSceneEmpty`(1040)、finally(≈950–990) | finally 里 `mapRoot` / `playerClone` 都先 `SetActive(false)`（走正常 Deactivate 注销 interests）再 `DestroyImmediate`；关临时场景前枚举 `scratchScene.GetRootGameObjects()`，有残留就**报告 + 按同一纪律销毁**，否则只记「已无自建对象」；再关场景。关场景 / activeScene / Selection / `Random.state` 的还原顺序不变 |
| **F6** | 新增烘焙日志文件 + 每步标记 | `BakeLogRelativePath`(238)、`BakeLog`(512)、`Step(...)`、`PrepareContent`(643) | 日志落 **`Client/Logs/PMBattleContentBuild.log`**（相对 Unity 工程根，绝对路径写进摘要与 Console）；`FileMode.Create` + **`FileShare.ReadWrite`** + `AutoFlush`（外部可边跑边读），每次 WriteLine 后 `Flush`；失败时不影响烘焙（只写摘要）。步骤标记覆盖：开始 / 资源目录 / 清旧 manifest / 源文件 SHA / 开源场景 / 取模板+地面 / 生成布局 / 落点选择 / digest / 临时场景 / 搭地图 / 存地图 prefab / 克隆角色 / 存角色 prefab / 回读（地图、角色）/ 写 manifest / 销毁自建 root / 关临时场景 / 关源场景 / 结束。**结束时把整份摘要也写进日志**（崩溃时这段缺失，成功/失败时它就是完整现场） |
| **F7** | 回读校验增强 | `VerifyMapPrefab`(3106)、`VerifyMapPrefabLoaded`(3193) | 保留原「无 missing script / 组件全在 C2 允许集内（非白名单组件 ⇒ 失败）/ Collider 均 `enabled` 且 `activeInHierarchy` / 至少 1 个非 trigger Collider / 网格与材质非空」；**新增** tile 根 active/inactive 计数、`skippedInactive` 记录、以及「默认跳过策略下 `inactiveTiles > 0` ⇒ 失败」（未激活对象上的组件不会走 Activate/Deactivate，正是崩溃根因，也不满足 C2）；`VerifyMapPrefabLoaded` 另打印整棵 prefab 的 `节点 activeSelf / inactiveSelf` 计数 |

**明确没有改动（不得放宽的既有校验）**：`VerifyMapPrefabLoaded` 的「非白名单组件 ⇒ 失败」「Collider 必须
enabled + activeInHierarchy」、`ValidateContent` 的 manifest 强校验与资源键白名单、`RemoveManifestOrInvalidate`
的三步自证、`WriteManifest` 的「manifest 最后发布」、`TryDeriveCollisionDigestAndWorldVersion`
（仍直接调用 C2 的冻结实现，无第二份派生）。**manifest schema / 字段 / 派生规则一字未改。**

---

## 4. V1–V4 结果（日志：`Tools/r4c-crashfix-verification.log`）

| 项 | 命令 | 结果 |
|---|---|---|
| V1 新增门禁 | `dotnet build Tools/PMBattleContentSceneFactsCheck -c Release` + 运行 | **0 错误 0 警告；63 项，失败 0 项，退出码 0** |
| V2 C1 真实编译 | `dotnet build Tools/PMBattleContentBuildCheck -c Release` | **0 错误 0 警告**（真实 Unity 2019.4 `UnityEditor.dll` + `UnityEngine.*Module.dll`） |
| V3 纯逻辑用例 | 见 §4.3 | **只能 Unity 内验证**（不假称已验） |
| V4 回归 | `PMBattleContentRuntimeCheck` / `PMBattleContentManifestTest` / `PMBattleContentSessionTest` / `PMClientCheck` / `PMUnityGlueCheck` / `PMR4UnityCheck` | **全部 0 错误、退出码 0**；ManifestTest **92 项 0 失败**；SessionTest **86 项 0 失败 0 跳过** |

### 4.1 V1 门禁做了什么（`Tools/PMBattleContentSceneFactsCheck`，net8，纯 C#，零外部依赖）

- **§A 解析面自证**：文档数 1586 / GameObject 403 / Transform 403 / MonoBehaviour 89 / 根对象 2
  —— 先证明「按 `--- !u!<classID> &<fileID>` 切分真的读到了东西」，避免空转。
- **§B**：从 `ScenseBuildLogic.cs.meta` 取 guid（不把 guid 抄死）→ 在场景里找到引用它的 MonoBehaviour
  → `mapx==33`、`mapy==21`、五个模板数组非空、组件确实挂在 `HYLDGameTatal/3D`。
- **§C（核心）**：`floors` 的每一条都必须 `m_IsActive == 0` **且**组件 classID 里没有任何 Collider
  （打印实际 classID，实测 `4/33/23`）。**这就是「默认跳过」的源真相。**
- **§D**：`walls`/`obstacles`/`trees`/`Grasses` 每条模板都 `m_IsActive == 1`；
  地面 `HYLDGameTatal/MAP/Plane` 存在 + active + 含 `MeshCollider(64)`（打印实际 classID `4/33/23/64`）；
  运行期容器 `HYLDGameTatal/3D/MAP` 存在。
- **§E（口径必须说清）**：对 C1 源码做**文本级静态结构断言**——F2 开关默认 true、`SelectPlacements`/
  `TryGetArraySize`/`DestroySelfBuiltRoot`/`EnsureScratchSceneEmpty`/`BakeLogRelativePath`/`FileShare.ReadWrite`
  存在、**F1 三处顺序**（`CopyComponentSet` 必须在 `SetActive` 之前）、**F3 判重必须在 `AddComponent` 之前**、
  以及**F4 的所有 `.arraySize` 读写都必须在 `TryGetArraySize` 守卫内或那个已知安全的赋值上**。
  断言前先**剥掉注释**（否则文档注释里解释旧码写法的文字会被当成"还没修"）。
  **它是防回归的文本级门禁，不是行为验证**。

### 4.2 V1 负向注入（证明它非空转；每次注入后都 `sha256sum` 校验已恢复）

| # | 注入 | 期望 | 实测 |
|---|---|---|---|
| 1 | 临时副本里把 `Plain02 (1)` 的 `m_IsActive` 改成 1（源场景文件本身不动） | §C 失败 | ✅ 2 项失败，打印 `实际=1` |
| 2 | 临时副本里把地面 `Plane` 的 `MeshCollider` 改成 `BoxCollider` | §D 失败 | ✅ 1 项失败，打印 `实际 classID=4/33/23/65` |
| 3 | 临时副本里把地面 `m_IsActive` 改成 0 | §D 失败 | ✅ 1 项失败，打印 `实际=0` |
| 4 | 临时副本里把 `mapx` 改成 35 | §B 失败 | ✅ `mapx == 33（实际=35）` |
| 5 | 真实 C1 源码里插一行提早的 `tile.SetActive(template.activeSelf);` | §E 顺序失败 | ✅ `实际位置 75927 / 639`（恢复后 SHA 一致） |
| 6 | 真实 C1 源码里 `SkipInactivePlacements = true` → `false` | §E 失败 | ✅ 1 项失败 |
| 7 | 真实 C1 源码里把 `TryGetArraySize(` 改名 | §E 失败 | ✅ 2 项失败（标记缺失 + F4 守卫无从定位） |
| 8 | 真实 C1 源码里插入裸读 `int sourceSize = it.arraySize;` | §E F4 不变量失败 | ✅ `共 3 处；违规=… int sourceSize = it.arraySize;` |
| 9 | 真实 C1 源码里所有 `FileShare.ReadWrite` → `FileShare.None` | §E 失败 | ✅ 1 项失败 |

> 注入 5–9 都是「备份 → 注入 → 跑门禁 → 恢复 → `sha256sum` 校验」，且注入/恢复针对的就是**本次门禁全绿
> 的那一份字节**：`Client/Assets/Editor/PMBattleContentBuild.cs =
> f5113f903209c89fffc22b629acd5192a1bf8ae2c39f1142dfe03d8bb030f48d`（9 例全部恢复 OK）。
> 注入 1–4 用的是系统临时目录下的场景副本，**源场景一个字节都没写**
> （`HYLDGame.unity = 72e708d51613804f…`，未变）。另附「场景文件不存在」用例：退出码 1、输出明确。

### 4.3 V3：判重与数组分支的纯逻辑用例 → **只能 Unity 内验证**（不假称已验）

`CopyComponentSet` 的判重与 `TryGetArraySize` 的语义**依赖 Unity 运行时**（`GameObject.AddComponent`
是否登记组件表、`SerializedProperty.isArray` 与 `arraySize` 的真实行为），而这两个方法所在文件是
Editor-only（引用 `UnityEditor.dll`），**无法在 net8 里编译执行**。可行的替代方案都被判为更差：

- 在 `Tools/PMBattleContentSceneFactsCheck` 里**复制**一份同样的判据逻辑 → 那是**第二份实现**，
  正是上一轮报告警告过的「两端各写一遍口径」的翻版，只会在两边漂移时给人虚假安全感；
- 为此新加一个共享纯逻辑文件 → 超出本轮写入边界（只允许动 C1 与其门禁/新工具/报告）。

因此本轮**只做**：①（V1 §E）把「判重在 AddComponent 之前」「所有 arraySize 读取必须过守卫」钉成
静态不变量；②在报告里如实标注这两处**只能由用户在 Unity 内验证**。**不声称已验。**

---

## 5. 仍未验证项 / PENDING_USER

| # | 项 | 状态 | 说明 |
|---|---|---|---|
| R1 | **崩溃是否真的消失** | **PENDING_USER（不可离线证明）** | 本轮把「把未激活状态前置到 AddComponent 之前」这条路径从根上去掉了（F1 顺序 + F2 默认跳过 2660 个 inactive 地板落点），但**只有用户下一次点菜单才知道结果** |
| R2 | 真实烘焙产物（三个 Resources 资产）与真实 digest | 未生成 | `Client/Assets/Resources/PMNet/` 至今为空；`PMBattleContentManifestTest` §J 如实报「**未烘焙** = PENDING_USER」 |
| R3 | `SerializedProperty.isArray` 对那批「`propertyType==ArraySize` 但不是数组」属性到底返回什么 | **未验证** | 本轮按任务要求加了 `isArray` 守卫，**并额外加了窄 catch**：即使 `isArray` 为 true 而 Unity 仍拒绝，也不会再产生 `Retrieving array size but no array was provided`。触发它的**具体字段路径无从离线确定**（Unity 不在日志里打印路径） |
| R4 | 重复确认「非激活对象 AddComponent」是 Unity 2019.4 的普遍行为 | 由日志推断 | 三条独立症状（组件表未登记 / require-component 补重复 / interests 未注销）都指向它；但本轮无法在 Unity 内做受控实验 |
| R5 | F1 纪律在「开关关掉」时是否也安全 | **未验证（有意）** | `SkipInactivePlacements=false` 会把未激活落点再建出来；F1 只保证「AddComponent 时 active」，最终仍会把 tile 设为非激活 —— 若 Unity 的行为与推断不符，这条路径可能仍不安全。**默认开关是 true，不会走到它**；报告如实登记 |
| R6 | `Logs/` 目录在 Unity 工程内的可写性 / 是否被纳入版本库 | 已知 | 路径 `<Client>/Logs/PMBattleContentBuild.log`；`.gitignore` 里有 `*.log`，因此**不会污染版本库**；若某些环境该目录不可写，日志会降级（只写摘要），烘焙不受影响 |
| R7 | 上一轮遗留：适配器 32 命中缓冲 / 非凸 MeshCollider 真机行为、相机随根缩放 | 不变 | 不在本轮写入边界内，维持上一轮报告的口径 |
| R8 | 跳过 2660 个地板落点对产物体积/表现的影响 | 语义等价 | 旧运行期这些实例同样不可见、无碰撞；地图可见地板与唯一地面碰撞来自 `Ground/Plane`。**若主侧要求恢复这些落点，把 `SkipInactivePlacements` 置 false 并重新握手 digest**（digest 会变，因为摘要流里记了跳过策略与计数） |

---

## 6. 预期数值（供用户首次烘焙时对照 Console / 日志）

| 项 | 预期 | 出处 |
|---|---|---|
| 落点总数 | 2919（floor 2660 / wall 112 / obstacle 35 / tree 112 / grass 0） | 上一轮 C1 报告 §8 用**同一算法**独立 Python 复算（非本次 C# 执行结果） |
| 默认跳过策略 | **skippedInactive = 2660（全部 floor）**，built = **259** | floors 数组 4 条模板全部 `m_IsActive: 0`（本轮 YAML 实证，V1 §C 守住） |
| tile 根未激活数 | **0** | F7 会把 `inactiveTiles > 0` 判成失败 |
| 非 trigger Collider | > 0（墙 112 个 `BoxCollider` + 障碍/树若干） | C2 的「至少一个非 trigger Collider」由墙保证 |
| 地图节点数 | 比上一轮预测的 ≈3005 显著下降（少了 2660 个 inactive 地板及其子节点） | 跳过策略的直接后果 |
| 日志文件 | `<Client>/Logs/PMBattleContentBuild.log`（每次烘焙重建；外部可边跑边读） | 本轮新增 |

---

## 7. 用户复跑步骤（本机不能跑 Unity，这一步只能由用户执行）

```text
1. 在 Unity 2019.4.8f1（不要用 Unity 6）打开 Client 工程；
2. 确认没有未保存的场景改动（源场景若已打开且脏，C1 会明确拒绝，这是有意的）；
3. 菜单 Build / Prepare PMNet Battle Content；
4. 期望：
   · 不再崩溃；弹窗提示“PMNet 内容烘焙完成”；
   · Console 的 [PMBattleContentBuild] 摘要里能看到：
       - 烘焙日志（每步 flush，FileShare.ReadWrite）：<...>\Client\Logs\PMBattleContentBuild.log
       - 布局（同种子两次生成一致）：total=2919 floor=2660 ...
       - 落点选择（skipInactive=true）：built=259，skippedInactive=2660（按类别：floor=2660）
       - 地图层级：tile 根=259，… 未激活 tile 根=0（默认跳过策略下应为 0…）
       - 地图组件清单：… | 节点 activeSelf=… inactiveSelf=0 | 类型分布=…
       - contentDigest / collisionDigest / worldVersion
       - 已发布 manifest（发布信号，最后一步）
       - UnityEngine.Random.state 未被改动=是
   · 三个产物出现：Client/Assets/Resources/PMNet/{BattleMapV1.prefab,PlayerVisualV1.prefab,BattleContentV1.json}
5. 再执行一次同菜单（或直接 Build / Build HyldDS (Windows Headless)，它会先走 PrepareForBuild() 校验），
   确认 **contentDigest 两次完全一致**（重复生成稳定性）。
6. 之后 dotnet Tools/PMBattleContentManifestTest/bin/Release/net8.0/PMBattleContentManifestTest.dll
   —— §J 会自动从“未烘焙”转为对三份产物的强校验（92 项仍应 0 失败）。
```

**如果仍然崩溃/失败，请抓这些证据（顺序即排障顺序）**：

1. **`<Client>/Logs/PMBattleContentBuild.log` 的最后一行** —— F6 的设计就是「最后一行 = 崩溃前最后完成的
   步骤」。它直接告诉你崩在 `map/verify/manifest/cleanup/finally` 的哪一步，不必再从 Unity 内部报错反推。
2. **Editor.log**（`%LOCALAPPDATA%\Unity\Editor\Editor.log`）+ 本工具日志的步骤时间戳对照：
   若 Editor.log 里出现 `change interests present when destroying the hierarchy` 或
   `Cannot unregister Collider ... as it was not in the table`，说明**仍有组件被加到了非激活对象上** ——
   请把该次日志里紧随其后的 `CheckConsistency: GameObject does not reference component <类型>` 与
   `Can't add component '<类型>' to <对象名>` 两行原文贴回来（对象名会直接指向是哪个模板/落点）。
3. 若崩在 `map` 之前的步骤，日志里会有明确的失败文本（例如「地面对象 … 处于未激活状态」、
   「落点选择失败」、「模板 … 含白名单外组件」）——按该文本处理，不需要看 native 栈。
4. 若只是**报错但不崩**：错误文本本身已经带对象路径（C1 的 offender 列表带
   `重复组件:<类型>:<目标路径>(源物体 <源路径>)`），直接据此定位。

---

## 8. 改动文件与编码纪律

| 文件 | 状态 | 编码 |
|---|---|---|
| `Client/Assets/Editor/PMBattleContentBuild.cs` | 修改（3307 → 3989 行） | **UTF-8 BOM + CRLF**（修后 `sha256 = f5113f903209c89f…`；`.meta` 未改动） |
| `Client/Assets/Editor/PMBattleContentBuild.cs.meta` | **未改**（未新增 Unity 资产，故本轮不需要新 guid / 新 meta） | — |
| `Tools/PMBattleContentSceneFactsCheck/Program.cs` | **新增** | 无 BOM + CRLF（与 `Tools/PMBattleContentManifestTest/Program.cs` 一致） |
| `Tools/PMBattleContentSceneFactsCheck/PMBattleContentSceneFactsCheck.csproj` | **新增** | 无 BOM + LF（与 `Tools/PMBattleContentBuildCheck/PMBattleContentBuildCheck.csproj` 一致） |
| `Tools/r4c-crashfix-verification.log` | **新增**（V1–V4 聚合日志：各门禁输出 + 退出码 + 被验证字节的 sha256 + 真机日志行号 + 9 条负向注入结果） | 纯文本；`*.log` 已在 `.gitignore` 内 |
| `Tools/<各门禁工程>/r4c-crashfix-verification.log` | **新增**（每个被跑过的门禁工程各一份，与既有 `r4c-main-verification.log` 同一约定）：`PMBattleContentSceneFactsCheck` / `PMBattleContentBuildCheck` / `PMBattleContentRuntimeCheck` / `PMBattleContentManifestTest` / `PMBattleContentSessionTest` / `PMClientCheck` / `PMUnityGlueCheck` / `PMR4UnityCheck` | 纯文本；同样被 `*.log` 覆盖 |
| `Docs/plans/_r4c_crashfix_report.md` | 本报告 | BOM + CRLF（与 `Docs/plans/` 既有报告一致） |

**未改动**：C2 运行类（`PMUnityBattleMap` / `PMUnityBattlePresentation` / `PMBattleContentManifest`）、
宿主 / `PMDsBuild.cs`、Server 侧、`net-r4c-content-contract.md` 与主计划、`Tools/PMBattleContent{BuildCheck,RuntimeCheck}/*`、
`Tools/PMUnityGlueCheck/*`、`PMUnityMoverCollisionQuery`、源场景与源 prefab、
`Tools/PMBattleContentManifestTest/Program.cs` 与 `PMBattleContentSessionTest/Program.cs`。
未执行 svn/git 写操作，未提交，未启动 Unity，未抢编辑器锁，未递归委派。

---

## 9. 已检查范围 / 未做事项（合规声明）

**已读完**：§1 表的 7 份文档全文；`PMBattleContentBuild.cs` 全文（含修后复核）；`ScenseBuildLogic.cs`
的 `InitData` 循环与 `MyInstantiate`；`HYLDGame.unity`（`ScenseBuildLogic` 序列化块、五类模板子树、
地面 `Plane`、`3D`/`3D/MAP`/`HYLDGameTatal/MAP` 层级与缩放、`m_IsActive` 与组件 classID 普查）；
`Remake/Player.prefab`（root 激活状态与 5 个 inactive 子树节点，均无 Collider，只做删除不做新增）；
`Tools/PMBattleContent{BuildCheck,ManifestTest,SessionTest,RuntimeCheck}` 的写法与输出；
真机 `Editor.log` 的 Bake 段（39990–40500：解析失败点、`CheckConsistency`、`already added`、
interests、`PhysicsManager.cpp:1450`、SIGSEGV 与托管栈）。

**未做（符合硬边界）**：未启动 Unity / 未点菜单 / 未生成或改写任何 Unity 资产或场景 / 未抢编辑器锁 /
未改 C2 与宿主与 Server / 未改契约与主计划 / 未改 manifest schema / 未跑 UE 编译 / 未 svn、git 写操作 /
未递归委派。**本报告不声称崩溃已解决**——见 §7 的复跑与取证步骤。
