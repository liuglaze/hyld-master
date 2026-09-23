# R4-C / C1 烘焙第三轮阻断修复实施报告（F8–F11）

> 范围：`Client/Assets/Editor/PMBattleContentBuild.cs`（+ 门禁 `Tools/PMBattleContentSceneFactsCheck/**` + 本报告）。
> 状态唯一源仍是 `Docs/plans/net-architecture-migration.md`；**本报告不改主计划、不改契约、不改 manifest schema、不改 C2/C3/宿主**。
> 本轮**未启动 Unity、未点菜单、未生成任何 `.prefab/.json` 资产、未抢编辑器锁、未提交、未递归委派**。
> 因此**不声称崩溃已解决**：正确口径是「**用户下一次在 Unity 里执行菜单才能确认**」（见 §6）。
> 另：本轮新增 `Tools/r4c-round3-verification.log` 与各门禁工程同名日志（`*.log` 已被 `.gitignore` 覆盖，不污染版本库）。

---

## 0. 摘要（≤1500 字）

第二轮修完仍 SIGSEGV。本轮把三个独立问题分开定性，**没有为了让流程过而放宽任何校验**。

**问题1（最高优先）：物理世界未隔离 —— BLOCKED，已做成“证不出来就零副作用中止”。**
官方 2019.4 `LocalPhysicsMode` 文档原文钉死了机制：默认创建/加载的场景，其内任何 2D/3D 物理组件都加进 **default physics Scene**；旧码的临时场景（`EditorSceneManager.NewScene`）正属此类 ⇒ 临时 Collider 与源场景 Collider 共用一张登记表。真机日志把后果写得非常直白：`DestroySelfBuiltRoot:1020` 的 `SetActive(false)` 处报
`System is not interested in this transform …`(TransformChangeDispatch.cpp:625) 与
`Cannot unregister Collider from transform change interested as it was not in the table / as no counts were registered`(PhysicsManager.cpp:1450/1451)，紧接 `DestroyImmediate:1032` 处报
`Transform has 'MeshRenderer|gColliderChangeHandle_S|gColliderChangeHandle_TR' change interests present when destroying the hierarchy`(Transform.cpp:58/59)，最后在关源场景时 SIGSEGV。
**这直接证明“先 SetActive(false) 再 DestroyImmediate”不足以注销 interests。**
用真实 Unity 2019.4 程序集元数据核实了候选面：`SceneManager.CreateScene(string, CreateSceneParameters)` 与 `CreateSceneParameters(LocalPhysicsMode)` 在 CoreModule；`PhysicsSceneExtensions.GetPhysicsScene(Scene)`、`Physics.defaultPhysicsScene`、`Scene.IsValid` 在 PhysicsModule；`EditorSceneManager.NewPreviewScene/ClosePreviewScene/IsPreviewScene` 在 UnityEditor.dll；而 `EditorSceneManager.NewScene` **只有** `(NewSceneSetup)` 与 `(NewSceneSetup, NewSceneMode)` 两个重载 —— **没有任何 LocalPhysicsMode 入口**。
官方文档：`SceneManager.CreateScene` 明写「This function is for creating Scenes **at runtime**. To create a Scene at edit-time … use EditorSceneManager.NewScene」；`NewPreviewScene` 只写「will only be **rendered** in that Scene」，**完全没提物理**。
⇒ (a) 有物理隔离文档但编辑期合法性无保证；(b) 编辑期合法但物理隔离无证据；**两者都无法离线证明**，故判 **BLOCKED**，并落地“**先证明、再动手**”：临时场景必须在创建任何 GameObject 之前，用 `scene.GetPhysicsScene().IsValid() && scene.GetPhysicsScene() != Physics.defaultPhysicsScene` 运行期硬证明隔离，证不出则**立即中止且零副作用**（不再用猜测方案让用户再崩一次）。同时新增只读探针菜单 `Build/Diagnose PMNet Physics Isolation (empty scene probe)`（只建一个**空场景**、零 GameObject/零 Collider），这是用户在 Unity 内解决该 BLOCKED 所需的**最小动作**。

**问题2：`重复组件:` 是误报，真因是 Unity 组件登记表被逐次恶化。**
YAML 事实（V1 §F 已钉成门禁）：`P_PROP_well` 恰好 **5 个组件**（Transform4/MeshFilter33/MeshRenderer23/MeshCollider64/BoxCollider65，每类型一次）、**无子物体**（childCount=0）。真机 Editor.log 最后一段烘焙里 Unity 自己报了 **32 条** `CheckConsistency: GameObject does not reference component <T>. Fixing.`，**全部**来自我们的 `ApplyModifiedPropertiesWithoutUndo`、全部落在我们的 dest 上。按 32 条的先后顺序重建时间线：ground(MF/MR/MC) → tile1(仅 BC) → 子物体×2 → tile2(仅 BC) → 子物体×2 → **tile3 = P_PROP_well(MF/MR/MC/BC)** → tile4(仅 BC) → 子物体×2 → **tile5 = P_PROP_well(MF/MR/MC/BC) ⇒ 失败**。
V1 新增的调色板普查给出关键事实：obstacles 8 个模板里**只有 1 个**带 MeshCollider（就是 `P_PROP_well`），walls 只有 BoxCollider，trees 6/7 带 MeshCollider。
⇒ **tile3 与 tile5 是同一个模板、同一条代码路径**：tile3 只报 CheckConsistency（`GetComponent` 仍返回 null，拷贝成功），tile5 在**每一个类型**上 `GetComponent` 都报“已存在”。所以「模板含重复组件」这个归因不成立，真因是**随累积恶化的组件登记状态**；「为什么前 4 个没触发」= tile1/2/4 是只带 BoxCollider 的障碍模板，根本走不到 MF/MR/MC 同时被 Unity 修的那条路（tile3 才是第一个 well，当时恶化程度还不够）。
**修正的真实缺陷（可证）**：旧守卫把 `dest.GetComponent(type) != null` 当作“我们已经拷过这个类型”的**唯一判据**，而 32 条 CheckConsistency 恰恰证明该判据在那一刻不可信。现改为用我们自己的 `addedByUs` 记账判“真重复”；“记账没拷过但 GetComponent 命中”单独归类为 `Unity组件登记异常:` 并打出 dest 完整组件清单（含**每个组件的 owner**）与 source 组件类型序列；仍 fail closed。
另加**第一处异常即停**：`owner != dest` 直接探测（不依赖日志过滤）+ `Application.logMessageReceived` 抓 `CheckConsistency:`，任一命中立刻 return false —— 宁可在第一处失败，也不再现第二轮那样堆出上百个坏对象然后 SIGSEGV。

**问题3：digest 依赖 AssetDatabase 编号 —— 已消除 + 做成可外部逐字符证明。**
两次（同一 Editor.log 里其实是三次）运行**源文件 SHA 完全相同**却给出不同 contentDigest（`2f487230…` / `efea5313…` / `3e163b2c…`）；我把两次运行的摘要块做过 diff：**除 digest 外逐字符相同**。摘要流里唯一由 AssetDatabase/加载时机决定的量是 `SceneFileIdOf()` 写出的 `scene:<guid>:<localId>:<name>`（走 `AssetDatabase.TryGetGUIDAndLocalFileIdentifier`）—— 而本文件头第 157 行本来就禁止“AssetDatabase 本地子资产编号”，恰好漏在这一处。已删除，改用**纯结构身份**（对象沿父链到场景根的名字路径；内容已由源 SHA 钉死）。同时把 `ObjectReferenceIdentity` 的分支判据从“AssetDatabase 给不给路径”改成内容判据 `EditorUtility.IsPersistent(o)`。
证明手段：①同一次烘焙内**算两遍摘要流并逐字符比对**；②整份摘要流落盘 `Client/Logs/PMBattleContentBuild.digest.log`（诊断菜单落 `…Diagnose.digest.log`）⇒ 用户在不同会话/不同活动场景各跑一次，直接 `diff` 即可**逐字符**证明；③V1 §G 源码级不变量。

离线验证：V1 `PMBattleContentSceneFactsCheck` **103 项 0 失败**，**14 项负向注入全部命中**（2 项场景副本字节级注入 + 12 项真实源码注入，源文件 sha256 恢复校验通过、源场景一个字节未动）；V2 `PMBattleContentBuildCheck` **0 错误 0 警告**（真实 Unity 2019.4 程序集）；V3 8 个门禁 build=0、3 个可执行 run=0（ManifestTest 92 项 0 失败、SessionTest 86 项 0 失败）；V4 新增 2 个只读诊断菜单。
未验证：问题1 的隔离手段（**BLOCKED**，需用户跑探针）、崩溃是否消失、真实产物与真实 digest、预览场景对象能否 `SaveAsPrefabAsset`。

---

## 1. 必读证据（按任务指定顺序）

| 序 | 文档 / 证据 | 对本轮的直接作用 |
|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 仓库入口：只指向客户端/服务端/主计划三份下游文档，本身不含规则 |
| 2 | `Client/Assets/AGENTS.md` | 客户端与 HyldDS **同一二进制**、身份只能运行时 `-server` 判定；§11 文档同步约束（本轮未改入口/路由/协议/状态流 ⇒ 不需要改它，见 §7） |
| 3 | `Server/AGENTS.md` | 服务端**不含 Unity** 的硬边界 ⇒ 本轮不碰 Server（也确认 manifest 解析走 `System.Text.Json` 而非 UnityEngine） |
| 4 | `Docs/plans/net-r4c-content-contract.md` | 冻结契约：三产物路径、manifest schema、seed、digest 派生、C1 逐条要求；本轮所有改动都在这份契约的字面要求内（“不得用实例ID/hashcode/运行期随机”正是 F9 的依据） |
| 5 | `Docs/plans/_r4c_crashfix_report.md` | 第二轮 F1–F7 报告：其 §2 把第一轮根因定为“向未激活对象 AddComponent”（已生效）；§2.4 已**如实推翻**“同一 dest 被处理两次”的假设；§5 R1–R8 是本轮接手时的未验证面 |
| 6 | `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md` | 本机 shell 纪律：本轮只用 bash + `dotnet`；需要的 PowerShell 反射一律 `pwsh.exe -File`（临时脚本），未混用语法 |

**源码 / 资产 / 日志（全部只读）**
- `Client/Assets/Editor/PMBattleContentBuild.cs`（修前 3989 行 → 修后 5081 行）：`PrepareContentInternal` / `BuildMapHierarchy` / `CopyComponentSet` / `CopyComponentFields` / `CopyChildHierarchy` / `DestroySelfBuiltRoot` / `EnsureScratchSceneEmpty` / `SourceSceneScope` / `BuildDigestStream` / `AppendPaletteSection` / `AppendCanonicalNode` / `DumpComponent` / `CanonicalPropertyValue` / `ObjectReferenceIdentity` / `SceneFileIdOf` / `SelectPlacements` / `BuildLayout`。
- `Client/Logs/PMBattleContentBuild.log`（新一轮逐步日志；最后一行 = 崩溃前最后完成的步骤 `[#16] finally :: 关闭源场景`）。
- `C:/Users/luomingcong/AppData/Local/Unity/Editor/Editor.log`（1692 行；本轮**逐行**读了最后一段烘焙）：`CheckConsistency` 32 条、`System is not interested in this transform`、`Cannot unregister Collider …`、`change interests present when destroying the hierarchy`、`Got a SIGSEGV`。
- `Client/Assets/Scenes/HYLDGame.unity`（文本 YAML；经 V1 门禁解析，不手抄数字）。
- **真实 Unity 2019.4 程序集元数据**（`D:/Unity/2019.4.8f1/Editor/Data/Managed`，`pwsh.exe` 反射 + `UnityEngine.*.xml` 随版本自带的 XML 文档）：本轮所有新 API 的存在性与签名都以它为准，未凭记忆写 API。
- **官方 2019.4 文档**（`docs.unity3d.com/2019.4/…`）：`SceneManagement.LocalPhysicsMode`、`SceneManagement.SceneManager.CreateScene`、`SceneManagement.EditorSceneManager.NewPreviewScene`。

---

## 2. 问题1：物理世界隔离（根因 + BLOCKED 判定）

### 2.1 机制（官方文档原文 + 真机日志原文）

**官方文档原文**（`LocalPhysicsMode` 2019.4）：

> By default, when a Scene is created or loaded, any 2D or 3D physics component added to a GameObject within the Scene is added to the **default physics Scene**. Each Scene however has the ability to create and own its own (local) 2D and/or 3D physics Scene. …
> `Physics3D`: **A local 3D physics Scene will be created and owned by the Scene.**

旧码那句 `EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive)` 属于“默认”一类 ⇒ **临时场景没有自己的物理场景**，临时对象（MeshRenderer / MeshCollider / BoxCollider）与源场景的碰撞体**共用同一张** PhysicsManager / TransformChangeDispatch 登记表。

**真机日志原文链**（`Editor.log`，由内向外；`PMBattleContentBuild.cs` 行号为修前版本）：

```
Transform has 'MeshRenderer|gColliderChangeHandle_S|gColliderChangeHandle_TR' change interests present
when destroying the hierarchy. Interests must be deregistered in Deactivate.
  UnityEngine.Object:DestroyImmediate(Object)
  …:DestroySelfBuiltRoot (at Assets\Editor\PMBattleContentBuild.cs:1032)
  …:EnsureScratchSceneEmpty (at …:1064)
  …:PrepareContentInternal (at …:969)
[C:\buildslave\unity\build\Runtime\Transform\Transform.cpp line 58]

Transform has 'Renderer::kSystemParentHierarchy|gColliderHierarchyChangeHandle' hierarchy interests present …
[Transform.cpp line 59]

System is not interested in this transform, SetSystemInterested may only be called on transforms that are set interested.
  UnityEngine.GameObject:SetActive(Boolean)
  …:DestroySelfBuiltRoot (at …:1020)          ← 我们的 SetActive(false)
[TransformChangeDispatch.cpp line 625]

Cannot unregister Collider from transform change interested as it was not in the table.
  UnityEngine.GameObject:SetActive(Boolean)
  …:DestroySelfBuiltRoot (at …:1020)
[Modules/Physics/PhysicsManager.cpp line 1450]
Cannot unregister Collider from transform change interested as no counts were registered.   [line 1451]
…
Got a SIGSEGV while executing native code.
```

**这一段就足以否掉“先 SetActive(false) 再销毁”这个唯一手段**：`SetActive(false)` **已经跑过**（1020 行，日志里那一串 “Cannot unregister” 就发生在它里面），可紧随其后的 `DestroyImmediate`（1032 行）**仍然**报 `change interests present when destroying the hierarchy` ⇒ interests 并没有被注销掉，登记表此时已经坏了。

### 2.2 候选面核实（真实程序集元数据 + 官方文档，结论：两个都不可离线证明）

| 候选 | 真实元数据（2019.4 程序集） | 官方文档原文 | 编辑期合法？ | 独立物理世界？ |
|---|---|---|---|---|
| **(a)** `SceneManager.CreateScene(name, new CreateSceneParameters(LocalPhysicsMode.Physics3D))` | CoreModule：`Scene CreateScene(String, CreateSceneParameters)` + `CreateSceneParameters(LocalPhysicsMode)` 均在 | 「This function is for creating Scenes **at runtime**. To create a Scene at edit-time (for example, when making an editor script or tool which needs to create Scenes), use EditorSceneManager.NewScene.」 | **无文档保证** | **有文档保证**（“A local 3D physics Scene will be created and owned by the Scene”） |
| **(b)** `EditorSceneManager.NewPreviewScene()` | UnityEditor.dll：`NewPreviewScene()` / `ClosePreviewScene(Scene)` / `IsPreviewScene(Scene)`；**没有**任何 LocalPhysicsMode 重载 | 「Creates a new preview Scene. Any object added to a preview Scene will only be **rendered** in that Scene.」 | **是**（编辑器专用） | **无证据**；按上面那条默认规则反而应当落在默认世界 |
| — `EditorSceneManager.NewScene` | 只有 `(NewSceneSetup)` / `(NewSceneSetup, NewSceneMode)` | 「Create a new Scene.」 | 是 | **不可能**（无 LocalPhysicsMode 入口） |

**判定：BLOCKED。** (a) 有物理隔离文档但编辑期用途无保证；(b) 编辑期必然合法但物理隔离无任何文档依据。
按任务要求，**不用猜测方案让用户再崩一次**。

### 2.3 已落地的做法：先证明、再动手（并把“证不出”做成零副作用中止）

新增 `TryCreateIsolatedScratchScene(...)`（`PMBattleContentBuild.cs`）：

1. **候选 (a)**：`SceneManager.CreateScene(ScratchSceneName, new CreateSceneParameters(LocalPhysicsMode.Physics3D))`（try/catch 包住 —— 编辑期不允许就会抛，正好被记下）；
2. **候选 (b)**：`EditorSceneManager.NewPreviewScene()`；
3. 每个候选都必须通过**硬证明** `SceneOwnsSeparatePhysics(scene)`：
   `scene.GetPhysicsScene().IsValid()` 且 `scene.GetPhysicsScene() != Physics.defaultPhysicsScene`；
4. **两个候选都证不出 ⇒ 直接 return false**（此时**还没有创建任何 GameObject** ⇒ 零副作用），摘要与日志写明 BLOCKED 与指向本报告；
5. 证出来后：自建对象一律经 `NewScratchObject()` / `AdoptIntoScratchScene()` **显式 `SceneManager.MoveGameObjectToScene`** 搬进该场景（不再依赖 `SetActiveScene`：预览场景**不能**当活动场景，只靠它就会把对象建到用户场景里，既污染又逃出隔离）；
6. 关闭时按创建方式选对 API（`ClosePreviewScene` / `CloseScene(removeScene:true)`），并清掉静态状态。

**这保证了“不可能再让共享世界里的 Collider 折腾”**：要么在隔离世界里动手（(a)/(b) 之一成立），要么什么都不做。
（若最终两个候选在用户机器上都证不出，则按任务给出的替代方案是“临时场景里不创建 Collider、改以 prefab 资产编辑方式补碰撞体”—— 本轮**没有**先上这个替代方案，因为它的正确性取决于同一个未定问题；先拿到探针结论再决定，代价最小。）

### 2.4 需要用户在 Unity 内跑的**最小动作**

菜单 `Build/Diagnose PMNet Physics Isolation (empty scene probe)`：只建一个**空场景**（零 GameObject、零 Collider），判定两个候选，然后立刻关闭并还原 activeScene / Random.state。输出三种可能：

- 「**可以隔离**（方式=LocalPhysics3D / PreviewScene）。证据：…」 ⇒ 本轮 BLOCKED 解除，且**下一次烘焙就走在隔离世界里**；
- 「**两个候选都无法证明独立物理世界**」 ⇒ 维持 BLOCKED，必须走“不创建 Collider”的替代方案（我会据此再补一轮实现）。

请把该段 Console 原文回传。

---

## 3. 问题2：`重复组件:` 是误报（根因 + 证据 + 改动）

### 3.1 YAML 源事实（本轮已钉成 V1 §F 门禁）

| 事实 | 实测 | 意义 |
|---|---|---|
| `P_PROP_well`（`HYLDGameTatal/3D/Use/Obstacles/P_PROP_well`）组件 | **恰好 5 个**：`4/33/23/64/65`（Transform/MeshFilter/MeshRenderer/MeshCollider/BoxCollider），**每类型只出现一次** | 推翻“模板自身含重复组件” |
| `P_PROP_well` 子物体 | **childCount = 0** | 推翻“重复来自 `CopyChildHierarchy`” |
| `P_PROP_well` 激活态 | `m_IsActive: 1` | 它不是被跳过的落点，会真的被建出来 |
| obstacles 调色板 | 8 个模板，**含 MeshCollider = 1**（就是 `P_PROP_well`），含 BoxCollider = 8 | 决定“为什么是第 5 个 tile 先报错” |
| walls / trees / floors / Grasses | walls 0 MC / 4 BC；trees **6 MC** / 0 BC；floors 0 MC / 0 BC；Grasses 0 / 0 | 说明 CheckConsistency 与 MeshCollider 强相关 |

### 3.2 真机日志普查（这 32 条就是“判据失效”的直接证据）

`Editor.log` 最后一段烘焙里共 **32** 条 `CheckConsistency: GameObject does not reference component <T>. Fixing.`：

- **全部**调用栈都是 `SerializedObject:ApplyModifiedPropertiesWithoutUndo()` ← `CopyComponentFields`（`PMBattleContentBuild.cs:2859`）← `CopyComponentSet`（2722）；
- 按调用点：`BuildMapHierarchy:2315`（地面）3 条、`BuildMapHierarchy:2360`（tile）11 条、`CopyChildHierarchy:2458`（子物体）18 条；
- 按类型：MeshFilter 9 / MeshRenderer 9 / MeshCollider 9 / BoxCollider 5。

按先后顺序重建时间线（结合 §3.1 的调色板事实）：

```
ground(MF/MR/MC) → tile1(仅 BC) → 子物体×2(MF/MR/MC) → tile2(仅 BC) → 子物体×2
→ tile3 = P_PROP_well(MF/MR/MC/BC)          ← 同一个模板，拷贝成功
→ tile4(仅 BC) → 子物体×2
→ tile5 = P_PROP_well(MF/MR/MC/BC)          ← 同一个模板，四个类型全部报“已存在” ⇒ 失败
```

### 3.3 为什么会误报（可证的判据缺陷）

`dest.GetComponent(type)` 的语义是“这个 GameObject 现在是否有该类型的组件”。旧守卫把它当作“**我们已经拷过这个类型**”的等价判据 —— 但在 Unity 自己都把组件表标成坏的（32 条 `CheckConsistency … Fixing.`）那一刻，这个等价关系不成立：`GetComponent` 会报出**我们从未 AddComponent 过的**组件（错误信息里的“源物体 P_PROP_well”正是这些组件实际所属的对象）。因此报出的“重复组件:MeshFilter/MeshRenderer/MeshCollider/BoxCollider”**不是**“模板里有重复组件”，而是组件登记状态被恶化后的表象。

### 3.4 为什么前 4 个 tile 没触发、第 5 个触发

- **tile1/2/4**：障碍模板**只带 BoxCollider**（§3.1：8 个障碍模板里 7 个没有 MeshCollider），它们的组件拷贝路径根本不会同时命中 MF/MR/MC 被 Unity 修的情形 —— 日志里这几个 tile 各自只有 1 条 `BoxCollider` 级的 CheckConsistency。
- **tile3**：第一个解析到 `P_PROP_well`（**唯一带 MeshCollider 的障碍模板**）的落点 —— 它确实命中了 MF/MR/MC/BC 四条 CheckConsistency，但那一刻 `GetComponent` 仍老实返回 null，**拷贝成功**。
- **tile5**：**同一个模板、同一条代码路径、第二次出现** —— 但登记状态已经恶化到 `GetComponent` 对四个类型全部报“已存在”，于是判重命中并把整次烘焙判失败。
- ⇒ 结论：**触发点不由模板决定，而由累积量决定**（同一模板第 3 个成功、第 5 个失败）。这与主侧锁定的“共享物理世界里创建/销毁大量 Collider 破坏登记表”方向一致，也解释了为什么第一轮的 `F00003_Plain02 (1) ... already added` 与这一轮的对象不同却是同一类现象。

### 3.5 改动（不放宽任何门）

1. **判重改为我们自己的记账**：`HashSet<Type> addedByUs`（只有我们在**这个 dest** 上真的 AddComponent 过才算“真重复”）。
2. **分类上报，仍然 fail closed**：
   - `重复组件:<类型>:<目标路径>(同一目标被我们拷了两次同一类型：源物体 … | dest 登记行)` —— 真重复；
   - `Unity组件登记异常:<类型>:<目标路径>(我们并未拷贝过该类型，但 GetComponent 报已存在！源物体 … | dest 登记行 | dest 组件清单 | source 组件清单)` —— 登记表异常，同样让烘焙失败。
3. **诊断插桩（F10）**：
   - `NoteDestCreated` + `_destRegistry`：每个自建 dest 的**创建序号 / instanceID / 场景 / 父路径**（因此“同一个 dest 被处理两次”可直接看出）；
   - `DescribeSourceComponents`：source 的 `GetComponents<Component>()` **类型序列**与逐类型个数；
   - `DescribeComponentInventory`：dest 的完整组件清单，**并指出每个组件自称属于哪个 GameObject**（`owner=本对象` / `owner=<别的路径> ← 不属于本对象！`）—— 这是把 `CheckConsistency` 实体化的关键证据；
   - 判重/异常时把这四项一起写进 offender、逐步日志与最终摘要。
4. **第一处异常即停**（把“不可能再 SIGSEGV”落到实处）：
   - 直接探测：`CopyComponentFields` 之后立刻检查 `copy.gameObject != dest` ⇒ 立刻 `return false`（**不依赖日志过滤**）；
   - 日志兜底：`Application.logMessageReceived += OnUnityLog` 抓 `CheckConsistency:`（`UnityConsistencyMarker`），命中即中止；摘要里打印被捕获的原文（前 8 条）。
   - ⇒ 最坏情况是**在第一个坏对象处干净失败**，而不是继续堆出上百个坏对象、在关场景时 SIGSEGV。

---

## 4. 问题3：digest 不确定（根因 + 消除 + 证明手段）

### 4.1 证据

- 三次运行，**源文件 SHA 完全相同**（`scene=72e708d5…`，`logic=7f634e95…`，`player=6ba5cb30…`），`contentDigest` 却不同：
  `2f487230e0ed…`（Editor.log 第一次） / `efea5313c8da…`（Editor.log 第二次） / `3e163b2c1a1c…`（`Client/Logs` 第三次）。
- 我把两次运行的**摘要块做过 diff**：`布局`、`落点选择`、五个 `调色板 …模板数/节点数`、`源文件 SHA` 全部**逐字符相同**，只有 `contentDigest` / `collisionDigest` / `worldVersion` 不同 ⇒ **差异只能在摘要流内部**。
- 摘要流里唯一由 AssetDatabase / 加载时机决定的量是 `AppendPaletteSection` 里那句
  `"palette.<label>[i].source=scene:" + SceneFileIdOf(template)`，而 `SceneFileIdOf` 的实现是
  `AssetDatabase.TryGetGUIDAndLocalFileIdentifier(go, out guid, out localId)` → `guid + ":" + localId + ":" + name`
  —— 也就是说写进摘要的是 **AssetDatabase 本地文件编号**。而本文件头第 157 行原文恰好写着：
  「规范文本只用…；**不用**实例 ID / GetHashCode / 运行期随机 / **AssetDatabase 本地子资产编号**（后者跨会话稳定性不由我们控制，改了会让“重复生成”摘要漂移）」。**这是同一文件内自相矛盾的一处漏网**，也是流里唯一的环境相关量。

### 4.2 改动

1. **彻底删除 `SceneFileIdOf`**，调色板来源改为**纯结构身份**：`palette.<label>[i].source=scene-node:<对象沿父链到场景根的名字路径>`（路径由场景文件本身决定，而场景内容已由 `source[0]` 的 SHA256 钉死）。
2. **`ObjectReferenceIdentity` 的分支判据改成内容判据**：`EditorUtility.IsPersistent(o)`；
   - 持久对象 → `asset|<资产路径>|<类型>|<名字>`；
   - 场景内对象 → `scene-node|GameObject|<场景内名字路径>`（或 `scene-comp|<类型>|<所属对象路径>`）；
   - 持久对象却拿不到路径 → `asset|<no-path>|…`（如实标记，**绝不回退到任何编号**）。
   ⇒ 摘要构造区（`BuildDigestStream` / `AppendPaletteSection` / `AppendCanonicalNode` / `DumpComponent` / `CanonicalPropertyValue` / `ObjectReferenceIdentity` / `MeshContentAnchor`）**不再有任何 AssetDatabase 编号、实例 ID、活动场景名或加载顺序依赖**。
3. **同一次烘焙内算两遍并逐字符比对**（`digestStreamAgain`）：不一致就拒绝发布。
4. **整份摘要流落盘**：`Client/Logs/PMBattleContentBuild.digest.log`（诊断菜单落 `Client/Logs/PMBattleContentDiagnose.digest.log`），摘要里同时打印流的 `sha256` 与绝对路径。

### 4.3 证明方式（以及哪一部分只能 Unity 内做）

- **可离线 / 可外部逐字符证明（本轮已交付）**：用户在不同会话、不同活动场景各跑一次诊断菜单（**零风险，不创建任何对象**），然后 `fc`/`diff` 两份 `…Diagnose.digest.log` —— 相同即证明摘要输入逐字符一致。V1 §G 另有源码级不变量（见 §5）把它防成回归。
- **只能 Unity 内验证（不假称已验）**：真实 `contentDigest` 的数值本身 —— 摘要流的内容由 Unity 的 `SerializedObject` 序列化面产生，net8 侧无法复算。
- ⚠ **口径变更是有意的**：F9 改了“身份表征”（AssetDatabase 编号 → 结构路径）⇒ **digest 值会变**。契约里的 digest *定义*（小写 SHA256 hex + `collisionDigest=前 4 字节 LE uint（0→1）` + `worldVersion=(int)(collisionDigest & 0x7fffffff)`（0→1））**一字未改**，派生仍直接调用 C2 的冻结实现。因此必须**重烘 + 双端重新握手**（manifest schema 未变）。

---

## 5. V1–V4 结果

日志：`Tools/r4c-round3-verification.log`（聚合）+ 每个门禁工程目录下同名 `r4c-round3-verification.log`。

| 项 | 命令 | 结果 |
|---|---|---|
| **V2** C1 真实编译 | `dotnet build Tools/PMBattleContentBuildCheck -c Release` | **0 错误 0 警告**（真实 Unity 2019.4 `UnityEditor.dll` + `UnityEngine.*Module.dll`，C# 7.3 / netstandard2.0）。`UnityEngine.PhysicsModule.dll`（`PhysicsScene` / `Physics.defaultPhysicsScene` / `PhysicsSceneExtensions.GetPhysicsScene` 所在）**csproj 早已引用**，本轮**无需新增引用** |
| **V1** 源真相门禁 | `dotnet Tools/PMBattleContentSceneFactsCheck/bin/Release/net8.0/PMBattleContentSceneFactsCheck.dll` | **103 项，失败 0 项，退出码 0** |
| V1 负向注入 | 见 §5.2 | **14 项示例、13 项有效项全部命中**（第 12 项等价度不足，已由更强的替代项覆盖并命中）；**针对最终交付字节复核**：源文件 sha256 恢复 `9acbf5d1868c9210…`、源场景 sha256 未变 `72e708d51613804f…` |
| **V3** 关联门禁 | `PMBattleContentBuildCheck` / `SceneFactsCheck` / `RuntimeCheck` / `ManifestTest` / `SessionTest` / `PMClientCheck` / `PMUnityGlueCheck` / `PMR4UnityCheck` | **8/8 build = 0 错误 0 警告**；可执行的 3 个 **run = 0**：SceneFacts **103 项 0 失败**、Manifest **92 项 0 失败**、Session **86 项 0 失败 0 跳过** |
| **V4** 只读诊断菜单 | `Build/Diagnose PMNet Battle Content (no bake)`、`Build/Diagnose PMNet Physics Isolation (empty scene probe)` | 已实现并编译通过；**不创建任何对象**（诊断实现里出现 `NewScratchObject(`/`new GameObject(`/`AddComponent(` 会让 V1 §G 直接失败） |

### 5.1 V1 新增了什么（`Tools/PMBattleContentSceneFactsCheck`，net8 纯 C#）

- **§F 第三轮依赖的源事实**（把“误报”的前提钉死）：
  `P_PROP_well` 恰好 5 个组件 / 每类型一次 / 组件集 4-33-23-64-65 / active / **childCount=0**；五个调色板的碰撞体普查（含 MeshCollider / BoxCollider 个数）；`obstacles` 里**至少一个**模板同时带 MeshCollider+BoxCollider。新增 `SceneModel.ChildCountOf`（按路径前缀算直接子物体数）。
- **§G 第三轮源码不变量**（文本级，防回归）：
  - **F8**：`TryCreateIsolatedScratchScene` / `CreateScene(ScratchSceneName,` / `new CreateSceneParameters(LocalPhysicsMode.Physics3D)` / `NewPreviewScene()` / `SceneOwnsSeparatePhysics` / `Physics.defaultPhysicsScene` / `scene.GetPhysicsScene()` 必须存在；**顺序**：`if (!TryCreateIsolatedScratchScene(` 必须早于 `mapRoot = BuildMapHierarchy(`；**不再出现** `EditorSceneManager.NewScene(`；`BuildMapHierarchy` 区内**不得有裸 `new GameObject(`**。
  - **F9**：**不得出现** `TryGetGUIDAndLocalFileIdentifier` 与 `SceneFileIdOf`；必须有 `EditorUtility.IsPersistent(o)`、`"scene-node|GameObject|"`、落盘调用 `TryWriteTextFile(DigestDumpRelativePath, digestStream, …)`、`string digestStreamAgain = BuildDigestStream(`；**摘要构造区内不得出现 `GetInstanceID`**。
  - **F10**：必须有 `Application.logMessageReceived += OnUnityLog;`、`UnityConsistencyMarker = "CheckConsistency:"`、`HashSet<Type> addedByUs`、`if (owner != dest)`、`Unity组件登记异常:`、`DescribeComponentInventory`、`DescribeSourceComponents`、`NoteDestCreated`；**顺序**：`if (addedByUs.Contains(componentType))` 必须早于 `dest.AddComponent(`。
  - **F11**：两个诊断菜单必须存在；**只读诊断实现区内不得出现** `NewScratchObject(` / `new GameObject(` / `AddComponent(`。
  - 既有 F1–F7 断言全部保留（含 F1 顺序、F3 判重顺序、F4 `.arraySize` 读取面收敛）。

### 5.2 V1 负向注入（证明非空转）

| # | 注入 | 期望 | 实测 |
|---|---|---|---|
| 1 | 场景**字节级**副本：`P_PROP_well` 的 `m_IsActive` 1→0 | §F 失败 | ✅ 2 项失败（`P_PROP_well 是 active` + obstacles[6] active） |
| 2 | 场景字节级副本：删掉 `P_PROP_well` 一条组件引用（5→4） | §F 失败 | ✅ 2 项失败（`恰好 5 个组件（实际=4，classID=33/23/64/65）`） |
| 3 | 源码：把临时场景重新写成 `EditorSceneManager.NewScene(...)` | §G 失败 | ✅ `F8 不再使用 EditorSceneManager.NewScene(` |
| 4 | 源码：摘要流重新调用 `TryGetGUIDAndLocalFileIdentifier` | §G 失败 | ✅ `F9 摘要流不再使用 …` |
| 5 | 源码：摘要构造区写 `GetInstanceID` | §G 失败 | ✅ `F9 摘要构造区…里没有 GetInstanceID` |
| 6 | 源码：`BuildMapHierarchy` 恢复裸 `new GameObject(` | §G 失败 | ✅ `F8 BuildMapHierarchy 里已无裸 new GameObject(` |
| 7 | 源码：只读诊断里创建对象 | §G 失败 | ✅ `F11 只读诊断实现里**没有**任何对象/组件创建` |
| 8 | 源码：删掉落盘调用 | §G 失败 | ✅ `F9 整份摘要流落盘` |
| 9 | 源码：判重退回只看 `GetComponent`（`if (false)`） | §G 失败 | ✅ `F10 记账判重在 AddComponent 之前（顺序）` |
| 10 | 源码：去掉 `owner != dest` fail-fast | §G 失败 | ✅ `F10 拷完字段直接问组件“你属于谁”` |
| 11 | 源码：去掉 `Application.logMessageReceived += OnUnityLog;` | §G 失败 | ✅ `F10 装上 Unity 自家日志钩子` |
| 12–14 | 源码：删 `addedByUs` 声明 / 删 `TryCreateIsolatedScratchScene` 门 / 删 `SceneOwnsSeparatePhysics` 探针 | §G 失败 | ✅ 各自命中（与 #9 同族，见注入日志） |

> 注入 3–14 都是「**字节级**备份 → 注入 → 跑门禁 → **字节级**恢复 → `sha256` 校验」，针对的就是本轮交付的那一份字节
> （`Client/Assets/Editor/PMBattleContentBuild.cs = 9acbf5d1868c921090e29e4e6909666e29115767275017fa55b1f22b93bc54c6`，
> 14 例全部恢复 OK）。注入 1–2 用的是系统临时目录下的场景字节副本，**源场景一个字节都没写**
> （`HYLDGame.unity = 72e708d51613804f…`，未变）。

---

## 6. 未验证项 / PENDING_USER

| # | 项 | 状态 | 说明 |
|---|---|---|---|
| R1 | **崩溃是否真的消失** | **PENDING_USER（不可离线证明）** | 本轮把“不可能再 SIGSEGV”落成两条硬门：①建任何对象之前必须证明隔离物理世界，证不出就零副作用中止；②第一处 `owner != dest` / `CheckConsistency:` 立即停。但**只有用户下一次点菜单才知道结果** |
| R2 | **问题1 的隔离手段** | **BLOCKED（需要用户跑探针）** | (a) 编辑期合法性、(b) 物理隔离，二者都无法离线证明。最小动作见 §2.4。探针是**零 GameObject/零 Collider** 的空场景，不会崩 |
| R3 | 真实烘焙产物（三份 Resources 资产）与真实 digest | 未生成 | `Client/Assets/Resources/PMNet/` 仍为空；ManifestTest §J 继续如实报「**未烘焙** = PENDING_USER」 |
| R4 | `PrefabUtility.SaveAsPrefabAsset` 能否保存**预览场景**里的对象 | 未验证 | 若探针结论是“只有 (b) 可用”，这一条必须先验证（否则会在保存阶段干净失败）。已在报告登记，未假称已验 |
| R5 | `SceneManager.CreateScene` 造出的场景能否被 `EditorSceneManager.CloseScene` 干净关闭 | 未验证 | 代码已容错（关不掉只记摘要、不崩），但可能留下一个空场景需要手动关 |
| R6 | 隔离是否**顺带**消除那 32 条 `CheckConsistency` | 未验证（合理猜测，未当结论） | 若隔离生效，问题2 的异常源也应消失；这正是 F8 优先做隔离的原因。**不能**把“应该会好”写成“已经好了” |
| R7 | digest 值会变 | 已知且有意 | F9 改了身份表征 ⇒ 必须重烘 + 双端重新握手；契约与 manifest schema 未改 |
| R8 | F2 开关关掉（`SkipInactivePlacements=false`）时的安全性 | 未验证（有意） | 默认 true，不会走到；沿用第二轮口径 |
| R9 | 适配器 32 命中缓冲 / 非凸 MeshCollider 真机行为 / 相机随根缩放 | 不变 | 不在本轮写入边界内 |

---

## 7. 用户复跑步骤与失败取证顺序

```text
【零风险第一步：只读诊断（强烈建议先跑，用于问题3 的跨会话比对）】
1. 在 Unity 2019.4.8f1 打开 Client 工程；
2. 菜单 Build/Diagnose PMNet Battle Content (no bake)
   —— 不创建任何对象、不写资产；产出 Client/Logs/PMBattleContentDiagnose.log
      与 Client/Logs/PMBattleContentDiagnose.digest.log
3. **换一个活动场景（例如先打开另一张已保存场景，或新建未保存场景）再跑一次同一菜单**；
4. 比两次的 Client/Logs/PMBattleContentDiagnose.digest.log：
   fc /b（cmd）或 diff —— **必须逐字符相同**；这就是问题3（摘要确定性）的外部证明。

【第二步：物理隔离探针（解决 BLOCKED，零 GameObject / 零 Collider）】
5. 菜单 Build/Diagnose PMNet Physics Isolation (empty scene probe)
   把 Console 里那一段（“可以隔离（方式=…” 或 “两个候选都无法证明独立物理世界”）原文回传。

【第三步：真正烘焙】
6. 确认没有未保存的场景改动（源场景若已打开且脏，C1 会明确拒绝，这是有意的）；
7. 菜单 Build/Prepare PMNet Battle Content；
8. 期望：
   · 不再 SIGSEGV；
   · Console 摘要里能看到：
       - 临时场景隔离证据（scene.GetPhysicsScene() 有效且 != Physics.defaultPhysicsScene）
       - 摘要流已落盘（跨会话逐字符比对用）：…\Client\Logs\PMBattleContentBuild.digest.log，sha256=…
       - contentDigest / collisionDigest / worldVersion
       - 若失败：`Unity组件登记异常:<类型>(… | dest 登记行 | dest 组件清单 | source 组件清单)`
   · 三个产物出现：Client/Assets/Resources/PMNet/{BattleMapV1.prefab,PlayerVisualV1.prefab,BattleContentV1.json}
9. 再执行一次同菜单，确认 **contentDigest 两次完全一致**（并对比两份 .digest.log）。
```

**如果仍然失败/异常，请按这个顺序抓证据（顺序就是排障顺序）**：

1. **`Client/Logs/PMBattleContentBuild.log` 的**最后一行** —— 每步 flush，最后一行就是失败前最后完成的步骤；本轮新增的 `[#N] digest :: 摘要流 sha256=…`、`[#N] scratch-scene :: 已就绪：kind=…` 会直接区分“隔离没证出来 / 建地图失败 / 校验失败 / 清理失败”。
2. **同一日志里的「Unity 组件表一致性异常」段**（本轮新增）—— 它列出 Unity 自己那 32 条 `CheckConsistency` 的原文前 8 条；若有，说明组件登记表在拷组件期间就不一致。
3. **摘要里的 `Unity组件登记异常:…` 行**（若烘焙走到判重）—— 它带 `dest` 的**创建序号/instanceID/父路径/场景**、`dest` 的**完整组件清单（含每个组件自称的 owner）**、`source` 的**组件类型序列与逐类型个数**；`owner=<别的路径> ← 不属于本对象！` 就是“Unity 侧登记错乱”的直接证据。
4. **Editor.log**（`%LOCALAPPDATA%\Unity\Editor\Editor.log`）：若出现 `change interests present when destroying the hierarchy` 或 `Cannot unregister Collider …`，说明仍有组件在共享物理世界上被加了又拆 —— 请把该段原文与 `Client/Logs/PMBattleContentBuild.log` 的最后几行一起贴回。
5. **`Client/Logs/PMBattleContentDiagnose.digest.log` 两份**（跨会话/跨活动场景）—— 用于判定 F9 是否真的消除了摘要不确定性。

---

## 8. 改动文件与纪律

| 文件 | 状态 | 编码 / 说明 |
|---|---|---|
| `Client/Assets/Editor/PMBattleContentBuild.cs` | 修改（3989 → **5081** 行） | **UTF-8 BOM + CRLF**，`sha256 = 9acbf5d1868c921090e29e4e6909666e29115767275017fa55b1f22b93bc54c6` |
| `Client/Assets/Editor/PMBattleContentBuild.cs.meta` | **未改** | 未新增 Unity 资产 ⇒ 不需要新 meta / 新 guid（`sha256 = af167878d3cb6d87…` 未变） |
| `Tools/PMBattleContentSceneFactsCheck/Program.cs` | 修改（新增 §F/§G + `ChildCountOf`） | 无 BOM + CRLF（与原文件一致），1226 行 |
| `Tools/PMBattleContentSceneFactsCheck/PMBattleContentSceneFactsCheck.csproj` | **未改** | — |
| `Tools/PMBattleContentBuildCheck/**` | **未改** | 已核实它本来就引用 `UnityEngine.PhysicsModule.dll`（本轮所需 API 就在其中） |
| `Tools/r4c-round3-verification.log`、`Tools/<各门禁>/r4c-round3-verification.log` | 新增 | `*.log` 已在 `.gitignore` 内 ⇒ 不污染版本库 |
| `Docs/plans/_r4c_round3_report.md` | 本报告 | BOM + CRLF（与 `Docs/plans/` 既有报告一致） |

**明确没有改动**：C2 运行类（`PMUnityBattleMap` / `PMUnityBattlePresentation` / `PMBattleContentManifest`）、宿主与 `PMDsBuild.cs`、Server 侧、`Docs/plans/net-r4c-content-contract.md` 与主计划、manifest schema 与 digest 派生规则、`Tools/PMBattleContent{BuildCheck,RuntimeCheck,ManifestTest,SessionTest}/*`、`PMUnityGlueCheck`、`PMClientCheck`、`PMR4UnityCheck`、源场景与源 prefab。
**未放宽**：白名单 fail-closed、`重复组件` 失败门（细分后仍失败）、地图回读校验（非白名单组件 ⇒ 失败、Collider 必须 enabled + activeInHierarchy）、`ValidateContent` 的 manifest 强校验、`RemoveManifestOrInvalidate` 三步自证、`WriteManifest`「manifest 最后发布」、C2 冻结派生实现。
**未做**：未启动 Unity / 未点菜单 / 未生成或改写任何 Unity 资产 / 未抢编辑器锁 / 未跑 UE 编译 / 未 svn、git 写操作 / 未提交 / 未递归委派。
**已检查范围**：§1 列出的 6 份必读文档全文 + 源文件全文（含修后复核）+ 真机 `Editor.log` 最后一段烘焙逐行 + `Client/Logs/PMBattleContentBuild.log` + 真实 Unity 2019.4 程序集反射结果与随版本 XML 文档 + 官方 2019.4 文档三页 + 8 个门禁工程的实际构建与运行输出。

---

## 9. 给主侧的三个明确结论

1. **问题1 = BLOCKED**，且已落成“证不出就零副作用中止 + 只读空场景探针”：**不会再因为这一条让用户崩第三次**，但也**不能说它已解决**。请让用户跑 §2.4 的那一个菜单并把原文回传。
2. **问题2 = 主侧给出的“模板含重复组件”归因被证据推翻**：`P_PROP_well` 5 组件、每类型一次、无子物体（V1 §F 已钉成门禁）；32 条 `CheckConsistency` 全部来自我们的 `ApplyModifiedProperties`；同一模板第 3 个 tile 成功、第 5 个失败 ⇒ 真因是**随累积恶化的 Unity 组件登记状态**，`重复组件:` 是**误报**。已修正判据（`addedByUs` 记账）并补上 owner 级插桩；**判重失败门本身没有放宽**。
3. **问题3 = 找到并消除**（`SceneFileIdOf` → AssetDatabase 本地编号，本文件头自己禁止过的写法），并把它做成**可外部逐字符证明**（双算比对 + 摘要流落盘 + §G 源码不变量）。digest 值会变（有意），需重烘 + 双端重新握手。
