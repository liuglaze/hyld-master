// ============================================================================
//  PMBattleContentBuild.cs —— R4-C / C1：正式战斗内容的**构建期烘焙**
// ============================================================================
//
//  契约来源：Docs/plans/net-r4c-content-contract.md（唯一冻结源）与
//            Docs/plans/net-architecture-migration.md 的 R4-C 段（状态唯一源）。
//
//  本文件回答一个问题：**正式地图与角色表现从哪里来？**
//    · 旧链的地图不是资产，而是 `ScenseBuildLogic.InitData()` 在运行期用
//      `UnityEngine.Random`（**未播种**）逐格生成的；直接加载 HYLDGame.unity 会拉起
//      旧 BattleManger/UDPSocketManger（硬编码 UDP 7777）/UI/相机，两端必然不同图。
//    · 契约的解法：**构建期用固定种子烘焙一次**，DS 与客户端加载同一份产物
//      （Resources/PMNet/BattleMapV1.prefab），运行期不各自随机。
//
//  产物（三个，全部由本文件生成；运行期绝不回退旧内容）：
//    Assets/Resources/PMNet/BattleMapV1.prefab     Resources 键 "PMNet/BattleMapV1"
//    Assets/Resources/PMNet/PlayerVisualV1.prefab  Resources 键 "PMNet/PlayerVisualV1"
//    Assets/Resources/PMNet/BattleContentV1.json   Resources 键 "PMNet/BattleContentV1"
//
//  manifest 的 schema（formatVersion/mapId/seed/mapResource/playerResource/
//  contentDigest/collisionDigest/worldVersion）**由 C2 的 PMNet.Unity.PMBattleContentManifest
//  定义**，本文件直接复用它的类型与冻结派生规则（TryDeriveCollisionDigestAndWorldVersion），
//  不重写第二份口径 —— 两端各写一遍派生规则正是最容易在握手时才暴露的漂移。
//
//  ---------------------------------------------------------------------------
//  本文件**不做**什么（读的人最容易误解的地方）
//  ---------------------------------------------------------------------------
//    · 不调用 `ScenseBuildLogic.InitData()`（依赖全局 Random 与旧玩法数据）；
//    · 不使用 `UnityEngine.Random`（不用全局随机态；自带的 DeterministicRng 是可复现的
//      局部 PRNG，种子固定 0x52444301）。**不使用**意味着任何代码路径都不读写它 ——
//      若发现全局随机态被改动，摘要里会显式列出（见 RandomStateNote）；
//    · 不改动源资产：`Assets/Scenes/HYLDGame.unity` 与 `Resources/Remake/Player.prefab`
//      只读；角色是"克隆后清理"，绝不对原件 Destroy/Apply；
//    · 不进入 Play 模式（`Application.isPlaying` 时直接拒绝执行）；
//    · 不抢编辑器锁、不自动运行：菜单要用户点，或由 PMDsBuild 在构建前调用；
//    · **不做**UE 资产那套 MCP 读取：Unity 场景/prefab 是文本 YAML + 真实 Editor 对象，
//      按 Unity 方式读（见下）。
//
//  ---------------------------------------------------------------------------
//  源场景怎么读（这是"安全预览"的落点，也是本文件最需要解释的一处）
//  ---------------------------------------------------------------------------
//  契约要求：用 Editor 安全预览场景读取，或另一**经验证不会执行旧游戏 Awake**的编辑模式方案。
//  实测核验（本次 C1 调查的结论，见 report；其中「包内脚本无编辑模式标记」这句原先是错的，
//  复核已改正为按**脚本来源**分桶）：
//    · **工程内玩法脚本**（`Assets/**`：HYLDStaticValue / BattleManger / TouchLogic / Toolbox /
//      ScenseBuildLogic …）**一律没有**编辑模式标记 ⇒
//      **没有任何旧游戏 Awake/OnEnable 会因本文件加载场景而执行**；
//    · 工程内**带**编辑模式标记的只有第三方摇杆插件的 EasyJoystick / EasyButton：它们只动
//      自身 UI 状态与静态 EasyTouch 事件（OnDisable 会反注册），不创建/销毁场景对象、不碰游戏状态；
//    · 源场景里还有**大量 Unity 包/引擎自带的编辑模式组件**：ugui 的 `Graphic`（`Image`/`Text`
//      的基类）与 `CanvasScaler`/`Selectable`(→`Button`)/`Slider` 都带 `[ExecuteAlways]`，而
//      `ExecuteAlways.AttributeUsage.Inherited == true` ⇒ `Attribute.IsDefined(typeof(Image),
//      typeof(ExecuteAlways))` 为 true（实测读本工程编译产物 UnityEngine.UI.dll 与真实
//      UnityEngine.CoreModule.dll 的元数据）。它们**不是**未审计脚本：原实现把它们当违规项，
//      会让源场景永远过不了审计 ⇒ 烘焙恒失败（这正是复核改掉的那处缺陷）。
//  因此本文件用**两道门** + 「EditorSceneManager.OpenScene(..., Additive) 预览读取 + finally 关闭」：
//    · **打开之前**的预检 PreflightProjectEditModeAudit：读场景文本里的脚本 guid，只对
//      **工程内** `.cs` 解析类型并查编辑模式标记 ⇒ 违规时**根本不打开场景**
//      （即"audit 在 Awake 之前"是**真的**，而不是"打开后才发现"）；
//    · 打开之后的运行时复核 AuditEditModeScripts：按脚本来源（工程内 / 包与引擎 / 无法判定）分桶；
//      包与引擎的编辑模式组件只记入摘要（Unity 自带；本文件不 Save 场景，其组件写入不会被保留），
//      工程内违规与无法判定都显式失败。
//  同时保证：不改 activeScene（finally 还原）、不动用户选择（finally 还原）、
//  不改源场景脏状态（不 Save、只 Close 自己开的那一份）。
//
//  ---------------------------------------------------------------------------
//  地图几何怎么复刻（逐字段，不简化）
//  ---------------------------------------------------------------------------
//  完全按 `ScenseBuildLogic.InitData()` 的**循环结构与随机数消费顺序**复刻，只把
//  `UnityEngine.Random.Range` 换成局部确定 PRNG：
//    1) 33×21 网格（mapx/mapy 取自场景序列化值，不擅自扩到表的 35 行）；
//       `maps[i,j]` 直接读**真实类**的 `map2` 数组（不手抄 735 个数字）；
//    2) 边界墙（4 条边）；3) 外围树；4) 边界外补地板。
//  随机数消费顺序逐处对齐（**包括源码里被注释掉实例化、但仍然消费一次随机数的草丛分支**），
//  详见 BuildLayout 内注释。
//
//  世界变换的一处关键事实（很容易做错，故写在这里）：
//    源码 `MyInstantiate` = `Instantiate(t, worldPos, rot)` 后 `SetParent(MAP)`；
//    `SetParent(Transform)` 单参重载 = `worldPositionStays: true`，于是父级 `3D` 的
//    0.5 缩放被**抵消**（子物体局部值被反向放大 2 倍），tile 的**世界**变换就等于
//    `(i-16, y, j-10) / identity / 模板自身缩放`。因此烘焙产物按**世界等价**存放：
//    容器用 identity，tile 局部值 = 世界值（不做 0.5 × 2 往返，避免把地图做小一半）。
//
//  ---------------------------------------------------------------------------
//  组件白名单（与 C2 运行期校验逐条对齐，写死在这里）
//  ---------------------------------------------------------------------------
//    地图（PMUnityBattleMap.CollectAndValidate 的允许集）：
//      Transform 系 / MeshFilter / MeshRenderer / **任意 Collider** / LODGroup /
//      **kinematic** Rigidbody；其它任何类型（含 MonoBehaviour / Camera / Animator /
//      AudioListener / 动态 Rigidbody / SkinnedMeshRenderer）在 C2 侧是**致命**的 ⇒
//      本文件对地图模板采取 **fail-closed**：出现白名单外组件直接**让生成失败**，
//      而不是"报告一下继续"。源场景实测 5 类模板子树里只有
//      Transform/MeshFilter/MeshRenderer/MeshCollider/BoxCollider，白名单外为 0。
//    **两道门（第四轮明确）**：
//      · `IsAllowedComponent` = “这个类型允许出现在产物里吗”（与 C2 允许集对齐）；
//      · `IsExplicitCopySupported` = “我们真的实现了它的字段拷贝吗”。
//      只过第一道门就 AddComponent、然后静默丢字段，正是本轮要消除的形态；
//      未实现的类型在 **AddComponent 之前**就被拒绝（在目标上不会留下空壳组件）。
//    角色（PMUnityBattlePresentation.ValidateComponents 的允许集）：
//      Transform 系 / MeshFilter / MeshRenderer / SkinnedMeshRenderer / Animator / LODGroup；
//      其余（旧 PlayerLogic/HYLDPlayerController/missing script/Rigidbody/BoxCollider/
//      AudioSource/Canvas 系 UI/LineRenderer/SpriteRenderer/CanvasRenderer…）**删除**，
//      并逐类型计数上报（"报告"而不是"静默残留"）。
//
//  ---------------------------------------------------------------------------
//  崩溃现场（观测链，保留原样；**不要**把下面的“假说”读成结论）
//  ---------------------------------------------------------------------------
//  真机实测（Editor.log，2026-09 修复前）：菜单 Bake 走到搭地图层级后，关**临时场景**与关
//  **源场景**各报出一串 Unity 内部登记表错误，最后在 `SourceSceneScope.Dispose()` 的
//  `EditorSceneManager.CloseScene` 里 native 崩溃（SIGSEGV）。日志原文链：
//    · `CheckConsistency: GameObject does not reference component MeshFilter. Fixing.`
//      （栈 = `SerializedObject.ApplyModifiedPropertiesWithoutUndo` ← CopyComponentFields）；
//    · `Can't add component 'MeshFilter' to F00003_Plain02 (1) because such a component is
//      already added to the game object!`（栈 = `GameObject.AddComponent` ← CopyComponentSet）；
//    · `Retrieving array size but no array was provided`（栈 = `SerializedProperty.get_arraySize`）；
//    · 关临时场景：`Transform has 'MeshRenderer|gColliderChangeHandle_S|gColliderChangeHandle_TR'
//      change interests present when destroying the hierarchy. Interests must be deregistered
//      in Deactivate.`；
//    · 关源场景：`Cannot unregister Collider from transform change interested as it was not in
//      the table.`（PhysicsManager.cpp:1450）⇒ SIGSEGV。
//
//  **已观测的事实（可引用）**：那 32 条 `CheckConsistency … Fixing.` 的调用栈**全部**落在本文件旧码的
//  `SerializedObject.ApplyModifiedPropertiesWithoutUndo()` 上；SIGSEGV 发生在关源场景时。
//
//  **未被证实的假说（不得再当结论写）**：
//    （假说 A）“向非激活对象 AddComponent”是崩溃根因；
//    （假说 B）“临时场景与源场景共用默认物理世界、大批量 Collider 建销破坏了登记表”是崩溃根因；
//    （假说 C）“通用序列化字段写入”是崩溃根因。
//  三者都只是当时的归因方向，没有被任何实验证实（第三轮报告也只给出“调用点 + 现象”，不是因果实验）。
//  YAML 实据（可引用）：`ScenseBuildLogic.floors` 指向的 `Plain02 (1)`/`Plain01 (1)` 在源场景就是
//  `m_IsActive: 0`，而旧码把该状态前置到 AddComponent 之前 ⇒ 2660 个地板 tile 全部命中该路径。
//  本轮（第四轮）不再依赖上述任一假说：直接把通用序列化写路径删掉（见下节），
//  并把 F1“先 active 建、拷完再施加最终激活态”作为**安全纪律**保留（不再称为已证实的根因）。
//
//  修复（七条，逐条见各处注释）：
//    F1 生成纪律：目标对象一律**先以 active 创建并完成组件拷贝**，最后才 `SetActive(模板最终态)`
//       （地面 / tile / 子物体同一纪律，保证 AddComponent 时整条父链都 activeInHierarchy）；
//    F2 跳过无意义落点：模板 inactive ⇒ 旧运行期该实例不可见且无碰撞，默认**跳过并计数**
//       （`SkipInactivePlacements`，默认 true；摘要与 digest 都写 skippedInactive）；
//       地面若 inactive 则**显式失败**（否则地图没有可站立面）；
//    F3 CopyComponentSet 健壮：AddComponent 前 `dest.GetComponent(type)` 判重；AddComponent 返回
//       null 时不再 `new SerializedObject(null)`，而是记为 offender 让烘焙失败；
//    F4 数组字段不再经 `SerializedObject` 读写（旧码的 `isArray`/`arraySize` 守卫已随显式拷贝整体删除）：
//       数组现在就是普通字段赋值（`MeshRenderer.sharedMaterials` / `LODGroup.GetLODs/SetLODs`），
//       而“数组长度对不对”由**回读核对**保证（长度或元素不等就烘焙失败）。
//       真机日志里的 `Retrieving array size but no array was provided` **只是**旧码那条路径的现象，
//       它**不是**任何崩溃的已证实根因（见上节）。
//    F5 释放纪律：销毁自建 root 前先 `SetActive(false)`（走正常 Deactivate 注销 interests），
//       并确认临时场景里已无自建对象再关场景；
//    F6 可诊断性：新增逐步落盘日志 `Logs/PMBattleContentBuild.log`（FileShare.ReadWrite，可边跑边读），
//       崩溃后最后一行就是崩溃前完成的步骤；
//    F7 回读校验增强：不存在非白名单组件、Collider 均 enabled 且 activeInHierarchy、
//       记录 active/inactive 计数与 skippedInactive。
//
//  ---------------------------------------------------------------------------
//  第三轮（F8–F11）：物理世界隔离门 / 摘要确定性 / 诊断插桩 / 只读诊断菜单
//  ---------------------------------------------------------------------------
//  第二轮修完后真机**仍然崩溃**（Editor.log 第二次 SIGSEGV）。可引用的现场：
//    · 本轮烘焙日志最后一步是 `[#16] finally :: 关闭源场景`；Editor.log 里在
//      `DestroySelfBuiltRoot`（我们的 SetActive(false) + DestroyImmediate）处报
//      `Transform has 'MeshRenderer|gColliderChangeHandle_S|gColliderChangeHandle_TR' change
//      interests present when destroying the hierarchy.` 与
//      `Cannot unregister Collider from transform change interested as it was not in the table.`
//      （PhysicsManager.cpp:1450/1451），随后 SIGSEGV。
//      ⇒ **可引用的事实**：SIGSEGV 发生在关源场景时，且之前有 interests 未注销的报错。
//      ⇒ **未证实的假说**：`SetActive(false)` 之后 DestroyImmediate 不足以致销 interests、
//        且其根因是“临时场景与源场景共用同一张默认物理世界登记表”。下面的官方文档引文是**真的**，
//        但“它就是这次崩溃的原因”从未被实验证实。
//  物理世界事实（官方文档，LocalPhysicsMode 页原文，仅作为背景）：
//    「By default, when a Scene is created or loaded, any 2D or 3D physics component added to a
//      GameObject within the Scene is added to the default physics Scene.」
//  ⇒ 旧码用 `EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive)` 造的
//   临时场景**没有**自己的物理场景 —— 这是文档事实；但“因此就破坏了 PhysicsManager /
//   TransformChangeDispatch 的表”是**推断**，本轮不把它当成结论。
//
//  F8 物理世界隔离门（**先证明，再动手**）：临时场景必须在创建**任何** GameObject 之前就证明
//     “自己拥有独立的 3D 物理场景”，否则**立刻失败退出**（零副作用），绝不再用猜测的方案跑一遍。
//     证据强度（真实 2019.4 程序集元数据 + 官方文档）：
//       (a) `SceneManager.CreateScene(name, new CreateSceneParameters(LocalPhysicsMode.Physics3D))`
//           —— 唯一有**明确文档**“A local 3D physics Scene will be created and owned by the Scene”
//           的 API（元数据已核：CoreModule 里两个 CreateScene 重载 + CreateSceneParameters
//           ctor(LocalPhysicsMode) 都在）；但 2019.4 文档同时写明它是**运行时**用途、编辑期应改用
//           `EditorSceneManager.NewScene` ⇒ 编辑模式（非 Play）合法性**无文档保证**；
//       (b) `EditorSceneManager.NewPreviewScene()` —— 编辑器专用、编辑期必然合法，但 2019.4 文档
//           只说“Any object added to a preview Scene will only be rendered in that Scene”，
//           **完全没提物理**，且不接受 LocalPhysicsMode ⇒ “独立物理世界”**无证据**，反而按上面
//           那句默认规则应当落在默认世界。
//     ⇒ 二者都无法在离线条件下被证明“编辑期合法 **且** 独立物理世界”。因此本文件**不选**任何一个
//       当既定方案，而是两个都试、并且用 `Scene.GetPhysicsScene()`（extension，PhysicsModule）
//       与 `Physics.defaultPhysicsScene` 做**运行期硬证明**：证明通过才继续，证明不过就中止。
//       这样无论哪一个候选在用户的 Unity 上成立，都不可能再让“共享世界里的 Collider 折腾”发生。
//       判定结论与仍需用户在 Unity 内跑的最小动作：见 Docs/plans/_r4c_round3_report.md。
//  F9 摘要确定性（契约级）：两份**源文件 SHA 完全相同**的真实运行给出了**两个不同**的
//     contentDigest（efea5313… / 3e163b2c…，同一 Editor.log 里还能看到第三个 2f487230…）。
//     本文件头原先写“**不用** AssetDatabase 本地子资产编号”，但 `SceneFileIdOf()` 恰恰用了
//     `AssetDatabase.TryGetGUIDAndLocalFileIdentifier()` 把 `guid:localId:name` 写进摘要
//     —— 那是流里**唯一**由 AssetDatabase/加载时机决定的量。已删除，改成**纯结构**身份：
//     从对象沿父链到场景根的名字路径（源文件 SHA 已经钉死了内容，路径只用于人工核对哪里来的）。
//     同时把 `ObjectReferenceIdentity` 的分支判据从“AssetDatabase 是否给路径”改成
//     `EditorUtility.IsPersistent`（内容判据，而不是 AssetDatabase 状态判据）；非持久对象用
//     纯结构身份（类型 + 场景内名字路径），彻底不再向 AssetDatabase 要“编号”。
//     并要求同一次烘焙内**算两遍摘要流并逐字符比对**；整份摘要流另落盘到
//     `Logs/PMBattleContentBuild.digest.txt`，使“不同会话/不同活动场景两次生成摘要逐字符相同”
//     这件事可以被外部 `diff` 直接验证（CSL 只能自证的边界见报告）。
//  F10 诊断插桩 + 第一处异常即停：`CheckConsistency: GameObject does not reference component
//     <类型>. Fixing.` 是 Unity 在**修复它自己**的组件表（真机日志 32 条，调用栈全部落在旧码的
//     `ApplyModifiedPropertiesWithoutUndo` 上）。它一出现，`dest.GetComponent(type)` 就不再是
//     “我们已经拷过这个类型了吗”的可靠判据 —— 第二轮的 `重复组件:` 误报正是这么来的。
//     **第四轮**：产生那些日志的通用序列化写路径已经整体删除（改走公开 API），
//     但本钩子**保留**作为兜底（它抓的是 Unity 自己的组件登记异常，与哪条写路径无关）。
//     因此：① 挂 `Application.logMessageReceived`，见到这条 Unity 自家一致性日志就**立刻中止**
//     烘焙（零后续破坏，避免第三轮再 SIGSEGV）；② 判重改用**我们自己**的 `addedByUs` 记账
//     （只有我们真的拷过才算重复，白名单/失败门一律不放宽）；③ 命中时把 dest 的创建序号 /
//     instanceID / 父路径 / 场景 / 完整组件清单 + source 的组件类型序列一起打进日志与摘要。
//  F11 只读诊断菜单：`Build/Diagnose PMNet Battle Content (no bake)` —— 打开源场景只读、
//     **不创建任何对象**、不写任何资产，输出模板/组件/落点/摘要流事实。这样即使再失败，
//     用户也能用零风险方式取回数据（尤其可用于 F9 的跨会话摘要比对）。
//
//  ---------------------------------------------------------------------------
//  组件字段怎么拷（第四轮：显式公开 API，不再是通用序列化搬运）
//  ---------------------------------------------------------------------------
//  旧做法：`SerializedObject.GetIterator()` 遍历**全部**序列化属性（含隐藏项），逐个
//  `CopyFromSerializedProperty` 后 `ApplyModifiedPropertiesWithoutUndo()`。它的两个问题：
//    · “拷了哪些字段”不可枚举（隐藏字段也写、源侧新增字段会被静默带走）；
//    · 真机 32 条 `CheckConsistency … Fixing.` 的调用栈**全部**落在它上面（已观测的调用点）。
//  新做法：**只对明确支持的组件类型逐字段调 Unity 公开 API**，并在同一处**立即回读核对**
//  （长度或值不等就记失败、让烘焙失败）。支持矩阵（源场景实测标记“出现”/“未出现”）：
//
//    | 组件 | 拷贝字段 | 源实测 |
//    |---|---|---|
//    | MeshFilter | sharedMesh | 出现 32 |
//    | MeshRenderer | sharedMaterials / enabled / shadowCastingMode / receiveShadows /
//      motionVectorGenerationMode / lightProbeUsage / reflectionProbeUsage /
//      allowOcclusionWhenDynamic / sortingLayerID / sortingOrder / renderingLayerMask /
//      rayTracingMode / rendererPriority / probeAnchor（按源→目标映射重绑定） | 出现 32 |
//    | BoxCollider | center / size + 公共（enabled / isTrigger / sharedMaterial / contactOffset）| 出现 14 |
//    | MeshCollider | sharedMesh / convex / cookingOptions + 公共 | 出现 19 |
//    | SphereCollider / CapsuleCollider | center / radius（+height/direction）+ 公共 | **未出现（未验证）** |
//    | LODGroup | fadeMode / animateCrossFading / localReferencePoint / size / enabled
//      + Renderer 数组（延后重绑定）| **未出现（未验证）** |
//    | Rigidbody（仅 kinematic）| mass/drag/angularDrag/useGravity/isKinematic/constraints/
//      collisionDetectionMode/interpolation/detectCollisions/maxAngularVelocity/centerOfMass/
//      inertiaTensor/inertiaTensorRotation | **未出现（未验证）** |
//    | 其它任何类型（含 TerrainCollider/WheelCollider 等 Collider 子类、Animator、SkinnedMeshRenderer）|
//      —— | **AddComponent 之前拒绝** |
//
//  三条硬性质（本轮的验收点）：
//    1. **不允许通用隐藏字段写入**：本文件不再出现 `CopyFromSerializedProperty` /
//       `ApplyModifiedProperties*`；`SerializedObject` 只剩**只读摘要**（`DumpComponent`）在用。
//    2. **数据或归属引用不得指源**：`LODGroup` 的 Renderer 数组与 `Renderer.probeAnchor` 在
//       **整棵目标子树建完之后**按源→目标映射重绑定；映射不到就失败（绝不指回源场景）。
//       （第五轮窄修正：probeAnchor 在拷贝阶段**只排队、不解析** —— 那一刻后代节点可能还没建出来，
//        在此查表会把"构建顺序"误判成"引用在层级之外"，对合法源数据必误拒。
//        另：`ResolveLodGroupFixup` 不再 `RecalculateBounds()`（它会覆写已按源拷贝的
//        `size`/`localReferencePoint`），且在 `SetLODs` 之后显式恢复这两个值并回读；
//        任一 Renderer 解析失败时**不调用** `SetLODs`，不写不完整的 LOD 数组。）
//    3. **不支持就不静默丢**：未实现的类型在 AddComponent 之前拒绝；已实现类型里
//       “无法搬运的字段”（如 `Renderer.lightProbeProxyVolume`、
//       `MeshRenderer.additionalVertexStreams`）非空即失败。
//  场景级“地址”信息（`m_StaticBatchInfo`/`m_StaticBatchRoot` 等批处理元数据）故意不拷：
//  它们不是外观、没有公开 API、而且合批根会指向源场景；新建组件保持默认（未合批）才是产物该有的状态。
//
//  **有意不拷的序列化字段（穷举，源场景实测值已列出）**——它们的共同点是“不是外观”或“不可搬运”：
//    · 批处理元数据：`m_StaticBatchInfo`(firstSubMesh=0)/`m_StaticBatchRoot`({fileID:0})
//      → 不得拷（合批根会指源场景）；新组件默认未合批。
//    · 不可写引用的对象引用：`m_LightProbeVolumeOverride`({fileID:0}) / `m_LightmapParameters`({fileID:0})
//      → 2019.4 无公开可写属性，由 SceneFactsCheck §H 的**源数据门**要求它们始终为 {fileID: 0}。
//      （`m_LightProbeProxyVolume` 在源场景根本不存在；`m_ProbeAnchor` 同样全为 {fileID: 0}，
//        因此 probeAnchor 的重绑定路径**有实现但未被源数据执行到**。）
//    · 编辑器 GI/光照图烘焙元数据：`m_ScaleInLightmap`(1) / `m_ReceiveGI`(1，get-only 无 setter)
//      / `m_PreserveUVs`(0) / `m_IgnoreNormalsForChartDetection`(0) / `m_ImportantGI`(0)
//      / `m_StitchLightmapSeams`(1 或 0) / `m_SelectedEditorRenderState`(3) / `m_MinimumChartSize`(4)
//      / `m_AutoUVMaxDistance`(0.5) / `m_AutoUVMaxAngle`(89) → 全为**烘焙期编辑器设置**（不参与运行期
//      外观）；产物是一张不带光照图数据的干净 prefab，保留这些值反而会把源场景的 bake 配置带进来。
//    · `m_SortingLayer`（字符串镜像）：排序由 `sortingLayerID` 唯一决定，无需同步两个镜像。
//  口径：上述字段里**任何一个是“外观相关且非默认”**的情况本轮都**没有被覆盖** —— 这是一个
//  已登记的边界，而不是“已保证不丢”。当前源场景的取值已逐项列在上面（CI/门禁只守住了对象引用那三个）。
//
//  最小自测：菜单 `Build/Self-test PMNet Content Copy (no bake)`（只由用户手动执行，不自动跑）；
//  它用合成对象调用**生产拷贝函数**，核对 owner/类型集合/数值/材质数组/LOD 重绑定/清理，
//  并验证未支持类型确实在 AddComponent 之前被拒绝。自测**不是**完整烘焙的替代。
//
//  ---------------------------------------------------------------------------
//  摘要（digest）与 manifest 的发布时序
//  ---------------------------------------------------------------------------
//    contentDigest = SHA256(规范化文本流) 的小写 hex，流的内容：
//      域标签 + 冻结常量（formatVersion/mapId/seed/mapx/mapy）
//      + **三个源文件的 SHA256**（HYLDGame.unity / ScenseBuildLogic.cs / Remake/Player.prefab；
//        Player 那条就是契约要求的"源 Player 指纹"）
//      + 模板调色板逐个的规范化签名（名字/激活/层/局部变换/mesh 引用与内容锚/材质/碰撞体字段）
//      + 地面 Plane 的规范化签名
//      + 每个实例化的规范行（类别/模板序号/局部位置/旋转）
//      + 落点总数（placements.count）
//      + 未激活落点的跳过开关与计数（layout.skipInactive / layout.skippedInactive.*）
//        —— 它决定产物到底包含了哪些落点，因此必须进摘要；否则两次不同策略的烘焙会得到同一个 digest
//    角色表现的"清理后内容"不进 digest：它的输入就是源 prefab（已由 SHA 钉死），
//    清理规则是同一次构建里的代码常量，因此 digest 无需重复表征它。
//    规范化文本只用：源文件 SHA、对象**名字**、资产路径、mesh 顶点数/bounds、组件序列化字段值；
//    **不用**实例 ID / GetHashCode / 运行期随机 / AssetDatabase 本地子资产编号
//    （后者跨会话稳定性不由我们控制，改了会让"重复生成"摘要漂移）。
//    collisionDigest / worldVersion 由 C2 的 PMBattleContentManifest 冻结规则派生。
//
//    发布时序（"部分失败不 valid"）：先生成两个 prefab 并**回读校验**，全部通过后才写
//    manifest；开始前先删掉旧 manifest，任何失败路径都不留下 manifest。
//
//  ---------------------------------------------------------------------------
//  API（谁调用什么）
//  ---------------------------------------------------------------------------
//    [MenuItem("Build/Prepare PMNet Battle Content")] PrepareContentMenu()  —— 用户显式烘焙
//    PrepareContent()   强制完整烘焙（失败不留 manifest）；返回 bool
//    ValidateContent()  只读校验既有产物（不写任何资源），给构建 hook 用；返回 bool
//    PrepareForBuild()  PMDsBuild 构建前调用：缺产物/源较新则烘焙，然后校验；返回 bool
//
//  语言面：Unity 2019.4 / C# 7.3 / .NET Standard 2.0。不要在 Unity API 签名上有任何"想当然"：
//  本文件同时被 Tools/PMBattleContentBuildCheck 用**真实 Unity 2019.4 程序集**(C#7.3) 编译，
//  编译不过就不会交付。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PMNet.Unity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PMNet.UnityEditor
{
    /// <summary>
    /// R4-C / C1：把「正式地图 + 干净角色表现 + 内容 manifest」在**编辑期**烘焙成 Resources 产物。
    ///
    /// 设计原则（与契约逐条对应）：
    ///   1) **确定性**：固定种子 + 局部 PRNG + 规范化摘要，重复生成摘要稳定；
    ///   2) **fail closed**：任何不确定/越界/白名单外内容都显式失败，不回退旧内容、不静默丢弃；
    ///   3) **不改源**：源场景/源 prefab 只读；临时对象全部在临时场景里，finally 清理；
    ///   4) **不假成功**：只有两个 prefab 回读校验通过后才发布 manifest。
    /// </summary>
    public static class PMBattleContentBuild
    {
        // ==================================================================== 冻结常量

        /// <summary>正式战斗场景（唯一联机入口；BuildSettings index 2）。</summary>
        public const string SourceScenePath = "Assets/Scenes/HYLDGame.unity";

        /// <summary>旧链唯一角色预制体（本文件只读它，不改它）。</summary>
        public const string SourcePlayerPath = "Assets/Resources/Remake/Player.prefab";

        /// <summary>地图生成算法与 map2 表的来源文件（进摘要；它就是"布局来源"）。</summary>
        public const string SourceLogicPath = "Assets/HYLD1.0/Scripts/OldScripts/ScenseBuildLogic.cs";

        /// <summary>三个产物必须落在的目录（与 C2 的资源键目录前缀一致）。</summary>
        public const string ResourcesDir = "Assets/Resources/PMNet";

        public const string MapPrefabPath = ResourcesDir + "/BattleMapV1.prefab";
        public const string PlayerPrefabPath = ResourcesDir + "/PlayerVisualV1.prefab";
        public const string ManifestAssetPath = ResourcesDir + "/BattleContentV1.json";

        /// <summary>固定烘焙种子（契约冻结 0x52444301 = 1380205313）。</summary>
        public const int BakeSeed = unchecked((int)0x52444301);

        /// <summary>地面来源（唯一的"地板碰撞"；floors 模板自身没有 Collider）。</summary>
        public const string GroundSourcePath = "HYLDGameTatal/MAP/Plane";

        private const string MapRootName = "BattleMapV1";
        private const string GroundContainerName = "Ground";
        private const string MapContainerName = "MAP";
        private const string PlayerRootName = "PlayerVisualV1";

        /// <summary>摘要域标签（改它就是改摘要口径，必须与主计划同步）。</summary>
        private const string DigestDomain = "PMNetBattleContentDigest/v1";

        /// <summary>
        /// 烘焙日志的相对路径（相对 Unity 工程根，即 <c>Application.dataPath</c> 的父目录 = Client/）。
        /// 绝对路径会被写进摘要；崩溃后直接打开该文件即可看到“崩在哪一步”。
        /// </summary>
        public const string BakeLogRelativePath = "Logs/PMBattleContentBuild.log";

        /// <summary>
        /// F9：整份摘要流（digest 的**输入**）落盘路径。
        /// 两个不同会话 / 不同活动场景各烘焙一次，然后直接 `diff` 这两份文件即可证明
        /// “摘要输入逐字符相同”——这是本机能给出的最强证据，且不依赖任何 Unity API。
        /// </summary>
        public const string DigestDumpRelativePath = "Logs/PMBattleContentBuild.digest.log";

        /// <summary>F11：只读诊断日志路径（诊断菜单专用，与烘焙日志分开）。</summary>
        public const string DiagnoseLogRelativePath = "Logs/PMBattleContentDiagnose.log";

        /// <summary>F11：只读诊断的摘要流落盘路径（用于跨会话逐字符比对）。</summary>
        public const string DiagnoseDigestRelativePath = "Logs/PMBattleContentDiagnose.digest.log";

        /// <summary>F8：临时场景名（`SceneManager.CreateScene` 要求名字与已加载场景不重名）。</summary>
        private const string ScratchSceneName = "PMBattleContentScratch";

        /// <summary>
        /// F10：Unity 自家“组件表不一致、我正在修”的日志标记。
        /// 见到它 = 组件登记表已不可信，必须立刻停（否则就是第二轮的 `重复组件:` 误报与 SIGSEGV）。
        /// </summary>
        private const string UnityConsistencyMarker = "CheckConsistency:";

        /// <summary>F8：临时场景隔离方式的枚举（只用于日志/摘要，便于用户回传现场）。</summary>
        private enum ScratchSceneKind
        {
            /// <summary>还没造出来。</summary>
            None,

            /// <summary>`SceneManager.CreateScene(..., LocalPhysicsMode.Physics3D)`：文档指明场景自有 3D 物理场景。</summary>
            LocalPhysics3D,

            /// <summary>`EditorSceneManager.NewPreviewScene()`：编辑器预览场景（物理隔离需运行期证明）。</summary>
            PreviewScene,
        }

        /// <summary>
        /// 是否跳过“模板本身未激活”的落点（F2），**默认跳**。
        ///
        /// 为什么默认跳（它不是可选的优化，而是崩溃修复的一半）：
        ///   · 旧链 <c>MyInstantiate</c> = <c>Instantiate(prefab, pos, rot)</c> 后 <c>SetParent(MAP)</c>，
        ///     并不 SetActive，因此 `Instantiate` 会**继承模板自身的 activeSelf**；
        ///   · 源场景 <c>ScenseBuildLogic.floors</c> 指向的 `Plain02 (1)`/`Plain01 (1)` 就是 `m_IsActive: 0`
        ///     （只有 Transform/MeshFilter/MeshRenderer，**无** Collider）⇒ 旧运行期这些地板实例
        ///     同样不可见、也无碰撞，产物纯属节点重量；
        ///   · 而把“不激活”状态前置到 AddComponent 之前，正是真机 SIGSEGV 的根因（见文件头）。
        /// 开关保留是为了“将来确实需要复原全部落点”时有路可走：把本字段改成 false 即可
        /// （例如先确认“未激活落点的组件添加”在本地 Unity 版本上不再触发 interests 泄漏）。
        /// 无论开关取何值，跳过数都会写进摘要与 digest（不得静默丢弃）。
        /// </summary>
        public static bool SkipInactivePlacements = true;

        /// <summary>地图模板允许的组件类型名（= C2 允许集；白名单外**失败**）。</summary>
        private static readonly string[] MapAllowedComponentNames =
        {
            "MeshFilter", "MeshRenderer", "LODGroup",
        };

        /// <summary>角色表现保留的组件类型名（= C2 允许集；不在列表内的一律删除并上报）。</summary>
        private static readonly string[] PlayerKeptComponentNames =
        {
            "MeshFilter", "MeshRenderer", "SkinnedMeshRenderer", "Animator", "LODGroup",
        };

        /// <summary>
        /// 允许在编辑模式下执行的**工程内**脚本类型名（白名单，超出即**失败**）。
        ///
        /// 判据是脚本**来源**，不是"有没有编辑模式标记"（这一处是复核时改正的，原判据会让烘焙必失败）：
        ///   · Unity 自带/包内的编辑模式组件在源场景里大量存在：ugui 的 `Graphic`（`Image`/`Text`
        ///     的基类）与 `CanvasScaler`/`Selectable`（`Button` 的基类）/`Slider` 都带
        ///     `[ExecuteAlways]`，而 `ExecuteAlways` 的 `AttributeUsage.Inherited == true`
        ///     ⇒ `Attribute.IsDefined(typeof(Image), typeof(ExecuteAlways))` 为 **true**
        ///     （实测：读本工程编译产物 `Library/ScriptAssemblies/UnityEngine.UI.dll` 与
        ///     真实 `UnityEngine.CoreModule.dll` 的元数据）。把它们当"未审计脚本"，
        ///     源场景永远过不了审计 ⇒ `PrepareContent()` 恒为 false ⇒ 三个产物永远生成不出来；
        ///   · 真正要挡的是**工程内玩法脚本**：它们一旦带上编辑模式标记，其 Awake/OnEnable
        ///     就会在加载源场景时执行旧玩法逻辑。
        /// 因此审计按脚本资产来源分桶（见 <see cref="ClassifyEditModeScriptOrigin"/>）：
        /// 工程内（`Assets/**`）必须命中本白名单；包/引擎组件记录进摘要但不判失败；
        /// 来源无法判定的仍然 fail closed。
        ///
        /// 表里的两个都是工程内的第三方摇杆插件：其编辑模式回调只操作自身 UI 状态与
        /// 静态 EasyTouch 事件（OnDisable 反注册），不创建/销毁场景对象、不碰游戏状态。
        /// </summary>
        private static readonly string[] EditModeToleratedScriptNames =
        {
            "EasyJoystick", "EasyButton",
        };

        /// <summary>
        /// 单个带编辑模式标记的组件属于哪个来源。判定结果直接决定
        /// "是否必须命中 <see cref="EditModeToleratedScriptNames"/>"。
        /// </summary>
        private enum EditModeScriptOrigin
        {
            /// <summary>工程内脚本（`Assets/**`）：必须命中白名单，否则失败。</summary>
            Project,

            /// <summary>Unity 包/引擎自带组件：记入摘要，不判失败。</summary>
            PackageOrEngine,

            /// <summary>来源无法判定：fail closed。</summary>
            Unknown,
        }

        /// <summary>
        /// 判定一个带编辑模式标记的组件是"工程内脚本"还是"Unity 包/引擎组件"。
        ///
        /// 判据顺序（先严后兜底，确保"工程内脚本"永远走严判）：
        ///   1) MonoScript 的资产路径以 `Assets/` 开头 ⇒ **Project**（唯一需要人工审计的一类）；
        ///   2) 否则看**程序集名**前缀 `UnityEngine` / `Unity.`（`UnityEngine.UI`、`Unity.TextMeshPro`…）
        ///      ⇒ **PackageOrEngine**。这一步是必要的兜底：内置包脚本的 `GetAssetPath` 形态并不唯一
        ///      （可能是 `Packages/...`、`Library/PackageCache/...`，也可能是编辑器安装目录下的
        ///      `.../BuiltInPackages/...`），而程序集名是稳定的；
        ///   3) 路径含 `Packages/` / `PackageCache` / `BuiltInPackages` ⇒ **PackageOrEngine**
        ///      （覆盖"程序集名不是 Unity 前缀、但确实来自包"的情况）；
        ///   4) 其它 ⇒ **Unknown**（调用方 fail closed，绝不静默放过）。
        ///
        /// 为何不能只看程序集名：工程内脚本与包内脚本在运行期都落在
        /// `Library/ScriptAssemblies/*.dll`（`Assembly-CSharp.dll` 与 `UnityEngine.UI.dll` 同目录），
        /// 光看程序集名分不开"工程内玩法脚本"与"Unity 自带组件" —— 所以"路径以 Assets/ 开头"
        /// 必须是第一判据。
        /// </summary>
        private static EditModeScriptOrigin ClassifyEditModeScriptOrigin(Type type, Component component,
                                                                       out string detail)
        {
            string assetPath = null;

            MonoBehaviour behaviour = component as MonoBehaviour;
            if (behaviour != null)
            {
                MonoScript script = null;
                try
                {
                    script = MonoScript.FromMonoBehaviour(behaviour);
                }
                catch (Exception)
                {
                    script = null;
                }

                if (script != null)
                {
                    assetPath = AssetDatabase.GetAssetPath(script);
                }
            }

            string assemblyName = null;
            try
            {
                if (type.Assembly != null && type.Assembly.GetName() != null)
                {
                    assemblyName = type.Assembly.GetName().Name;
                }
            }
            catch (Exception)
            {
                assemblyName = null;
            }

            detail = "path=" + (string.IsNullOrEmpty(assetPath) ? "<无 MonoScript>" : assetPath)
                     + " assembly=" + (assemblyName == null ? "<未知>" : assemblyName);

            // 1) 工程内脚本（最严，且优先级最高）。
            if (!string.IsNullOrEmpty(assetPath)
                && assetPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return EditModeScriptOrigin.Project;
            }

            // 2) Unity 自带程序集名（内置包/模块的稳定标识）。
            if (!string.IsNullOrEmpty(assemblyName)
                && (assemblyName.StartsWith("UnityEngine", StringComparison.Ordinal)
                    || assemblyName.StartsWith("Unity.", StringComparison.Ordinal)))
            {
                return EditModeScriptOrigin.PackageOrEngine;
            }

            // 3) 包路径形态（含内置包在编辑器安装目录下的形态）。
            if (!string.IsNullOrEmpty(assetPath)
                && (assetPath.StartsWith("Packages/", StringComparison.Ordinal)
                    || assetPath.IndexOf("PackageCache", StringComparison.Ordinal) >= 0
                    || assetPath.IndexOf("BuiltInPackages", StringComparison.Ordinal) >= 0))
            {
                return EditModeScriptOrigin.PackageOrEngine;
            }

            return EditModeScriptOrigin.Unknown;
        }

        // ==================================================================== 入口（菜单 / hook）

        /// <summary>
        /// 用户显式烘焙入口：Build/Prepare PMNet Battle Content。
        ///
        /// 只有在用户点菜单（或构建 hook）时才执行 —— 本文件**不会**自己启动 Unity，
        /// 也不在任何静态构造/InitializeOnLoad 里偷偷生成。
        /// </summary>
        [MenuItem("Build/Prepare PMNet Battle Content")]
        public static void PrepareContentMenu()
        {
            bool ok = PrepareContent();

            string title = ok ? "PMNet 内容烘焙完成" : "PMNet 内容烘焙失败";
            string body = ok
                ? "已生成：\n  " + MapPrefabPath + "\n  " + PlayerPrefabPath + "\n  " + ManifestAssetPath
                  + "\n\n详见 Console 的 [PMBattleContentBuild] 摘要。"
                : "生成失败，**未**发布 manifest（不会留下可被当成有效的半成品）。\n详见 Console 的 [PMBattleContentBuild] 错误。";

            if (ok)
            {
                Debug.Log("[PMBattleContentBuild] " + body.Replace("\n", " | "));
            }

            // batchmode 下弹窗会挂住进程：只在交互模式下提示。
            if (!Application.isBatchMode)
            {
                EditorUtility.DisplayDialog(title, body, "确定");
            }
        }

        /// <summary>
        /// F11（第三轮）：**只读诊断**菜单 —— 不创建任何对象、不写任何 Unity 资产、不抢锁，
        /// 只把“模板/组件/落点/摘要流”事实输出到日志。
        ///
        /// 为什么需要它（这是本轮最实用的交付之一）：烘焙再失败时，用户可以用**零崩溃风险**的方式
        /// 取回我们要的全部现场；尤其可以在**不同活动场景/不同会话**各跑一次，然后直接对比两份
        /// `Logs/PMBattleContentDiagnose.digest.log` —— 那就是 F9（摘要确定性）的外部证明。
        /// </summary>
        [MenuItem("Build/Diagnose PMNet Battle Content (no bake)")]
        public static void DiagnoseContentMenu()
        {
            bool ok = DiagnoseContent();

            if (!Application.isBatchMode)
            {
                EditorUtility.DisplayDialog(ok ? "PMNet 内容诊断完成" : "PMNet 内容诊断失败",
                                            (ok ? "只读诊断已完成（未创建任何对象、未写任何资产）。\n"
                                                : "诊断失败。\n")
                                            + "详见 Console 与 " + DiagnoseLogRelativePath,
                                            "确定");
            }
        }

        // ==================================================================== 最小编辑器自测（组件显式拷贝）

        /// <summary>自测日志的相对路径（与烘焙日志分开；`Logs/*.log` 已被 .gitignore 覆盖）。</summary>
        public const string SelfTestLogRelativePath = "Logs/PMBattleContentSelfTest.log";

        /// <summary>
        /// 最小编辑器自测菜单：**只验证本轮改写的组件显式拷贝语义**。它不打开源场景、不读正式资源、
        /// 不生成任何持久化资产 —— 因此它与“完整 Prepare 实机烘焙”是两件事。
        ///
        /// 构造的合成层级（全部 `HideFlags.HideAndDontSave`，不落盘）：
        ///   source：MeshFilter + MeshRenderer（**两个材质**）+ BoxCollider + MeshCollider
        ///           + 两个子物体（各 MeshFilter/MeshRenderer）+ LODGroup（两级各引用一个子渲染器）
        ///   dest  ：空对象 —— 组件全部由**生产函数** <see cref="CopyComponentSet"/> 拷过去；
        ///           子层级手工建好并登记进同一个 <see cref="MapCopyContext"/>，随后统一
        ///           <see cref="ResolveDeferredReferences"/>。
        /// 另外单独验证 **fail closed**：只带 `WheelCollider`（允许出现的 Collider 子类，但没有显式字段
        /// 拷贝实现）的源对象必须被拒绝，且目标上**不得**被 AddComponent 出该类型。
        ///
        /// 核对项：源/目标各自 owner 不变、目标组件类型集合与源一致、数值与材质数组一致、
        /// LOD 的 Renderer 指向**目标**子节点、源对象未被改写、Unity 自家一致性日志未出现、自建对象清理干净。
        ///
        /// **风险口径（不得称为“零风险”）**：自测会在当前活动场景里临时建几个隐藏对象，并在 finally
        /// 销毁；其中三个 Collider 会短暂进入**默认物理世界**（与源场景同一个世界）。自测规模远小于
        /// 完整烘焙（数千 tile），但**不是零风险**。它只由用户手动执行；本文件不会自动运行它。
        /// 完整 `Prepare` 实机烘焙仍属 PENDING_USER，本菜单不替代那一项。
        /// </summary>
        [MenuItem("Build/Self-test PMNet Content Copy (no bake)")]
        public static void SelfTestCopyMenu()
        {
            bool ok = SelfTestComponentCopy();

            if (!Application.isBatchMode)
            {
                EditorUtility.DisplayDialog(ok ? "PMNet 组件拷贝自测通过" : "PMNet 组件拷贝自测失败",
                                            (ok ? "复制/重绑定/清理核对全部通过。\n" : "存在失败项。\n")
                                            + "详见 Console 与 " + SelfTestLogRelativePath,
                                            "确定");
            }
        }

        /// <summary>自测实现（菜单与批处理共用；不自动运行）。</summary>
        public static bool SelfTestComponentCopy()
        {
            if (Application.isPlaying)
            {
                Debug.LogError("[PMBattleContentBuild] 自测在 Play 模式下被调用：拒绝执行（它会创建临时对象）。");
                return false;
            }

            StringBuilder summary = new StringBuilder();
            summary.AppendLine("[PMBattleContentBuild] 组件显式拷贝最小自测（不打开源场景 / 不读正式资源 / 不写资产）");
            summary.AppendLine("  风险：在当前活动场景临时建隐藏对象（HideAndDontSave）并在 finally 销毁；");
            summary.AppendLine("        其中三个 Collider 会短暂进入默认物理世界。**不是零风险**。");

            List<string> failures = new List<string>();
            List<UnityEngine.Object> owned = new List<UnityEngine.Object>();

            bool anomalyBefore = _unityConsistencyAnomaly;
            _unityConsistencyAnomaly = false;
            _unityConsistencyMessages.Clear();
            Application.logMessageReceived += OnUnityLog;

            try
            {
                Mesh mesh = CreateSelfTestMesh();
                owned.Add(mesh);

                Material materialA;
                Material materialB;
                if (!TryCreateSelfTestMaterials(out materialA, out materialB, failures))
                {
                    return FinishSelfTest(summary, failures);
                }

                owned.Add(materialA);
                owned.Add(materialB);

                global::UnityEditor.Animations.AnimatorController testController = new global::UnityEditor.Animations.AnimatorController();
                testController.hideFlags = HideFlags.HideAndDontSave;
                owned.Add(testController);
                SelfTestCheck(!ControllerDefinesFloatParameter(null, "Speed"), "控制器空引用必须拒绝", failures);
                SelfTestCheck(!ControllerDefinesFloatParameter(testController, "Speed"), "控制器缺Speed必须拒绝", failures);
                testController.parameters = new AnimatorControllerParameter[] {
                    new AnimatorControllerParameter { name = "Speed", type = AnimatorControllerParameterType.Int }
                };
                SelfTestCheck(!ControllerDefinesFloatParameter(testController, "Speed"), "Speed类型错误必须拒绝", failures);
                testController.parameters = new AnimatorControllerParameter[] {
                    new AnimatorControllerParameter { name = "Speed", type = AnimatorControllerParameterType.Float }
                };
                SelfTestCheck(ControllerDefinesFloatParameter(testController, "Speed"), "控制器Float Speed定义通过", failures);

                // ---------------- 源层级
                GameObject sourceRoot = NewSelfTestObject("PMSelfTestSource", null, owned);

                MeshFilter sourceFilter = sourceRoot.AddComponent<MeshFilter>();
                sourceFilter.sharedMesh = mesh;

                MeshRenderer sourceRenderer = sourceRoot.AddComponent<MeshRenderer>();
                sourceRenderer.sharedMaterials = new Material[] { materialA, materialB };
                sourceRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                sourceRenderer.receiveShadows = false;
                sourceRenderer.sortingOrder = 7;

                BoxCollider sourceBox = sourceRoot.AddComponent<BoxCollider>();
                sourceBox.center = new Vector3(0.25f, 0.5f, -0.75f);
                sourceBox.size = new Vector3(1.5f, 2.5f, 3.5f);

                BoxCollider sourceBoxSecond = sourceRoot.AddComponent<BoxCollider>();
                sourceBoxSecond.center = new Vector3(-2f, 1f, 0.5f);
                sourceBoxSecond.size = new Vector3(4f, 0.5f, 2f);
                sourceBoxSecond.isTrigger = true;

                MeshCollider sourceMeshCollider = sourceRoot.AddComponent<MeshCollider>();
                sourceMeshCollider.sharedMesh = mesh;
                sourceMeshCollider.convex = true;

                GameObject sourceChildA = NewSelfTestObject("PMSelfTestSourceChildA", sourceRoot.transform, owned);
                MeshRenderer sourceChildRendererA = AddSelfTestChildRenderer(sourceChildA, mesh, materialA);

                GameObject sourceChildB = NewSelfTestObject("PMSelfTestSourceChildB", sourceRoot.transform, owned);
                MeshRenderer sourceChildRendererB = AddSelfTestChildRenderer(sourceChildB, mesh, materialB);

                // 第五轮窄修正：probeAnchor 指向**后建的后代节点** —— 这是合法用法，也正是修正前
                // 必误拒的形态（拷贝根节点时 `NodeMap` 里还没有这个 child，见下方前置断言）。
                sourceRenderer.probeAnchor = sourceChildA.transform;

                LODGroup sourceLod = sourceRoot.AddComponent<LODGroup>();
                LOD[] sourceLods = new LOD[2];
                sourceLods[0] = new LOD(0.6f, new Renderer[] { sourceChildRendererA });
                sourceLods[1] = new LOD(0.05f, new Renderer[] { sourceChildRendererB });
                sourceLod.SetLODs(sourceLods);
                // 第五轮窄修正：取**明显非自动**的值。自动值（size 默认 1、localReferencePoint 由
                // 包围盒算出）会让"被 RecalculateBounds 覆写"这件事测不出来。
                sourceLod.localReferencePoint = new Vector3(1f, 2f, 3f);
                sourceLod.size = 7.25f;
                sourceLod.animateCrossFading = true;

                // ---------------- 目标层级：组件全部走生产函数
                GameObject destRoot = NewSelfTestObject("PMSelfTestDest", null, owned);

                MapCopyContext context = new MapCopyContext();
                context.NodeMap[sourceRoot.transform] = destRoot.transform;

                // 第五轮窄修正的前置条件（自证，不是假设）：拷根节点这一刻，后代 child 的源→目标
                // 映射**还不存在**。修正前 `CopyRendererCommonFields` 正是在这一瞬间查表并失败。
                SelfTestCheck(!context.NodeMap.ContainsKey(sourceChildA.transform),
                              "自测前置失效：拷贝根节点时 NodeMap 里已经存在后代 child 映射", failures);

                List<string> rootOffenders;
                if (!CopyComponentSet(sourceRoot, destRoot, true, context, out rootOffenders))
                {
                    failures.Add("CopyComponentSet(源根) 失败：" + Join(rootOffenders));
                }

                GameObject destChildA = NewSelfTestObject("PMSelfTestDestChildA", destRoot.transform, owned);
                GameObject destChildB = NewSelfTestObject("PMSelfTestDestChildB", destRoot.transform, owned);
                context.NodeMap[sourceChildA.transform] = destChildA.transform;
                context.NodeMap[sourceChildB.transform] = destChildB.transform;

                List<string> childOffendersA;
                if (!CopyComponentSet(sourceChildA, destChildA, true, context, out childOffendersA))
                {
                    failures.Add("CopyComponentSet(子 A) 失败：" + Join(childOffendersA));
                }

                List<string> childOffendersB;
                if (!CopyComponentSet(sourceChildB, destChildB, true, context, out childOffendersB))
                {
                    failures.Add("CopyComponentSet(子 B) 失败：" + Join(childOffendersB));
                }

                List<string> fixupFailures = new List<string>();
                if (!ResolveDeferredReferences(context, fixupFailures))
                {
                    failures.Add("ResolveDeferredReferences 失败：" + Join(fixupFailures));
                }

                // ---------------- 断言 1：源 / 目标各自 owner 不变
                SelfTestCheck(ReferenceEquals(SafeOwner(sourceFilter), sourceRoot),
                              "源 MeshFilter 的 owner 被改写了", failures);
                SelfTestCheck(ReferenceEquals(SafeOwner(sourceRenderer), sourceRoot),
                              "源 MeshRenderer 的 owner 被改写了", failures);
                SelfTestCheck(ReferenceEquals(SafeOwner(sourceBox), sourceRoot),
                              "源 BoxCollider 的 owner 被改写了", failures);
                SelfTestCheck(ReferenceEquals(SafeOwner(sourceMeshCollider), sourceRoot),
                              "源 MeshCollider 的 owner 被改写了", failures);
                SelfTestCheck(ReferenceEquals(SafeOwner(sourceLod), sourceRoot),
                              "源 LODGroup 的 owner 被改写了", failures);

                SelfTestCheck(ReferenceEquals(SafeOwner(destRoot.GetComponent<MeshFilter>()), destRoot),
                              "目标 MeshFilter 的 owner 不是目标对象", failures);
                SelfTestCheck(ReferenceEquals(SafeOwner(destRoot.GetComponent<MeshRenderer>()), destRoot),
                              "目标 MeshRenderer 的 owner 不是目标对象", failures);
                SelfTestCheck(ReferenceEquals(SafeOwner(destRoot.GetComponent<BoxCollider>()), destRoot),
                              "目标 BoxCollider 的 owner 不是目标对象", failures);
                SelfTestCheck(ReferenceEquals(SafeOwner(destRoot.GetComponent<MeshCollider>()), destRoot),
                              "目标 MeshCollider 的 owner 不是目标对象", failures);
                SelfTestCheck(ReferenceEquals(SafeOwner(destRoot.GetComponent<LODGroup>()), destRoot),
                              "目标 LODGroup 的 owner 不是目标对象", failures);

                // ---------------- 断言 2：组件类型集合一致
                string sourceKey = SelfTestComponentTypeKey(sourceRoot);
                string destKey = SelfTestComponentTypeKey(destRoot);
                SelfTestCheck(string.Equals(sourceKey, destKey, StringComparison.Ordinal),
                              "目标根对象的组件类型集合与源不一致：源={" + sourceKey + "} 目标={" + destKey + "}",
                              failures);

                // ---------------- 断言 3：数值 / 材质数组
                MeshFilter destFilter = destRoot.GetComponent<MeshFilter>();
                SelfTestCheck(destFilter != null && ReferenceEquals(destFilter.sharedMesh, mesh),
                              "目标 MeshFilter.sharedMesh 不等于源网格", failures);

                MeshRenderer destRenderer = destRoot.GetComponent<MeshRenderer>();
                bool materialsOk = false;
                if (destRenderer != null)
                {
                    Material[] destMaterials = destRenderer.sharedMaterials;
                    materialsOk = destMaterials.Length == 2
                                  && ReferenceEquals(destMaterials[0], materialA)
                                  && ReferenceEquals(destMaterials[1], materialB);
                }

                SelfTestCheck(materialsOk, "目标 MeshRenderer.sharedMaterials 与源（两个材质）不一致", failures);
                SelfTestCheck(destRenderer != null
                              && destRenderer.sortingOrder == sourceRenderer.sortingOrder,
                              "目标 MeshRenderer.sortingOrder 与源不一致", failures);
                SelfTestCheck(destRenderer != null
                              && destRenderer.shadowCastingMode == sourceRenderer.shadowCastingMode,
                              "目标 MeshRenderer.shadowCastingMode 与源不一致", failures);
                SelfTestCheck(destRenderer != null && destRenderer.receiveShadows == sourceRenderer.receiveShadows,
                              "目标 MeshRenderer.receiveShadows 与源不一致", failures);

                // 第五轮窄修正：probeAnchor 必须在**整树建完后**重绑到目标后代节点（且不得指源）。
                SelfTestCheck(destRenderer != null
                              && ReferenceEquals(destRenderer.probeAnchor, destChildA.transform),
                              "目标 MeshRenderer.probeAnchor 没有被重绑定到目标后代节点（应指向 destChildA）",
                              failures);
                SelfTestCheck(destRenderer != null
                              && !ReferenceEquals(destRenderer.probeAnchor, sourceChildA.transform),
                              "目标 MeshRenderer.probeAnchor 仍指向源对象（不得指源）", failures);

                BoxCollider destBox = destRoot.GetComponent<BoxCollider>();
                SelfTestCheck(destBox != null
                              && destBox.center == sourceBox.center
                              && destBox.size == sourceBox.size,
                              "目标 BoxCollider 的 center/size 与源不一致", failures);

                MeshCollider destMeshCollider = destRoot.GetComponent<MeshCollider>();
                SelfTestCheck(destMeshCollider != null
                              && ReferenceEquals(destMeshCollider.sharedMesh, mesh)
                              && destMeshCollider.convex == sourceMeshCollider.convex,
                              "目标 MeshCollider 的 sharedMesh/convex 与源不一致", failures);
                SelfTestCheck(destMeshCollider != null && destMeshCollider.enabled == sourceMeshCollider.enabled,
                              "目标 MeshCollider.enabled 与源不一致", failures);

                // ---------------- 断言 4：LOD Renderer 重绑定到**目标**子节点
                LODGroup destLod = destRoot.GetComponent<LODGroup>();
                if (destLod == null)
                {
                    failures.Add("目标上缺少 LODGroup");
                }
                else
                {
                    LOD[] destLods = destLod.GetLODs();
                    SelfTestCheck(destLods.Length == 2,
                                  "目标 LODGroup 的级别数不是 2（实际="
                                  + destLods.Length.ToString(CultureInfo.InvariantCulture) + "）", failures);

                    MeshRenderer destChildRendererA = destChildA.GetComponent<MeshRenderer>();
                    MeshRenderer destChildRendererB = destChildB.GetComponent<MeshRenderer>();

                    if (destLods.Length == 2)
                    {
                        SelfTestCheck(destLods[0].renderers.Length == 1
                                      && ReferenceEquals(destLods[0].renderers[0], destChildRendererA),
                                      "LOD 第 0 级的 Renderer 未指向目标子物体 A", failures);
                        SelfTestCheck(destLods[1].renderers.Length == 1
                                      && ReferenceEquals(destLods[1].renderers[0], destChildRendererB),
                                      "LOD 第 1 级的 Renderer 未指向目标子物体 B", failures);
                        SelfTestCheck(destLods[0].renderers.Length == 1
                                      && !ReferenceEquals(destLods[0].renderers[0], sourceChildRendererA),
                                      "LOD 第 0 级的 Renderer 仍指向源物体（不得指源）", failures);
                        SelfTestCheck(
                            destLods[0].screenRelativeTransitionHeight == sourceLods[0].screenRelativeTransitionHeight,
                            "LOD 第 0 级的 screenRelativeTransitionHeight 与源不一致", failures);

                        if (destLods[0].renderers.Length == 1 && destLods[0].renderers[0] != null)
                        {
                            SelfTestCheck(destLods[0].renderers[0].gameObject.scene == destRoot.scene,
                                          "LOD 第 0 级的 Renderer 不在目标场景（指向了别的场景）", failures);
                        }
                    }

                    // 第五轮窄修正：size / localReferencePoint 必须逐字保持源的非自动值 ——
                    // 它们曾被 `RecalculateBounds()` 覆写（那就是修正前的缺陷）。
                    SelfTestCheck(sourceLod.size == 7.25f
                                  && sourceLod.localReferencePoint == new Vector3(1f, 2f, 3f),
                                  "自测前置失效：源 LODGroup 的 size/localReferencePoint 不是预期的非自动值",
                                  failures);
                    SelfTestCheck(destLod.size == sourceLod.size,
                                  "目标 LODGroup.size 被改写了（疑似 SetLODs 之后的重算）：目标="
                                  + destLod.size.ToString(CultureInfo.InvariantCulture)
                                  + " 源=" + sourceLod.size.ToString(CultureInfo.InvariantCulture), failures);
                    SelfTestCheck(destLod.localReferencePoint == sourceLod.localReferencePoint,
                                  "目标 LODGroup.localReferencePoint 被改写了（疑似 SetLODs 之后的重算）：目标="
                                  + destLod.localReferencePoint.ToString() + " 源="
                                  + sourceLod.localReferencePoint.ToString(), failures);
                }

                // ---------------- 断言 5：源对象未被改写（“源只读”的可检查形式）
                LOD[] sourceLodsAfter = sourceLod.GetLODs();
                SelfTestCheck(sourceLodsAfter.Length == 2
                              && ReferenceEquals(sourceLodsAfter[0].renderers[0], sourceChildRendererA),
                              "源 LODGroup 在拷贝后发生了变化（源对象被改写）", failures);
                SelfTestCheck(sourceRenderer.sharedMaterials.Length == 2
                              && ReferenceEquals(sourceRenderer.sharedMaterials[0], materialA),
                              "源 MeshRenderer 的材质数组在拷贝后发生了变化", failures);

                // ---------------- 断言 6：fail closed —— 允许出现但没有实现的类型，必须在创建组件前拒绝
                BoxCollider[] copiedBoxes = destRoot.GetComponents<BoxCollider>();
                SelfTestCheck(copiedBoxes.Length == 2, "组合碰撞体：必须保留两个 BoxCollider", failures);
                if (copiedBoxes.Length == 2)
                {
                    SelfTestCheck(copiedBoxes[0] != copiedBoxes[1]
                        && copiedBoxes[0].gameObject == destRoot && copiedBoxes[1].gameObject == destRoot,
                        "组合碰撞体：独立实例及目标归属", failures);
                    SelfTestCheck(copiedBoxes[0].center == sourceBox.center && copiedBoxes[0].size == sourceBox.size
                        && copiedBoxes[1].center == sourceBoxSecond.center && copiedBoxes[1].size == sourceBoxSecond.size
                        && copiedBoxes[1].isTrigger == sourceBoxSecond.isTrigger,
                        "组合碰撞体：分别保留各自形状及 trigger", failures);
                }
                SelfTestCheck(sourceRoot.GetComponents<BoxCollider>().Length == 2
                    && sourceBoxSecond.gameObject == sourceRoot,
                    "组合碰撞体：源实例数量和归属不变", failures);

                SelfTestCheck(IsActiveWithinPrefabRoot(sourceChildA.transform, sourceRoot.transform),
                    "资产激活链：激活的根与子节点通过", failures);
                sourceRoot.SetActive(false);
                SelfTestCheck(!IsActiveWithinPrefabRoot(sourceChildA.transform, sourceRoot.transform),
                    "资产激活链：禁用祖先必须拒绝", failures);
                sourceRoot.SetActive(true);
                sourceChildA.SetActive(false);
                SelfTestCheck(!IsActiveWithinPrefabRoot(sourceChildA.transform, sourceRoot.transform),
                    "资产激活链：禁用自身必须拒绝", failures);
                sourceChildA.SetActive(true);
                SelfTestCheck(!IsActiveWithinPrefabRoot(sourceChildA.transform, destRoot.transform),
                    "资产激活链：不属于指定根必须拒绝", failures);

                GameObject unsupportedSource = NewSelfTestObject("PMSelfTestUnsupportedSource", null, owned);
                unsupportedSource.AddComponent<WheelCollider>();
                GameObject unsupportedDest = NewSelfTestObject("PMSelfTestUnsupportedDest", null, owned);

                string unsupportedReason;
                SelfTestCheck(!IsExplicitCopySupported(typeof(WheelCollider), out unsupportedReason),
                              "WheelCollider 被判为支持（fail closed 门失效）：" + unsupportedReason, failures);

                MapCopyContext unsupportedContext = new MapCopyContext();
                unsupportedContext.NodeMap[unsupportedSource.transform] = unsupportedDest.transform;
                List<string> unsupportedOffenders;
                bool unsupportedCopied = CopyComponentSet(unsupportedSource, unsupportedDest, true,
                                                          unsupportedContext, out unsupportedOffenders);
                SelfTestCheck(!unsupportedCopied && unsupportedOffenders.Count > 0,
                              "未实现字段拷贝的类型没有被拒绝（fail closed 门失效）", failures);
                SelfTestCheck(unsupportedDest.GetComponent<WheelCollider>() == null,
                              "未支持的类型竟然在目标上被 AddComponent 出来了（违反“创建之前拒绝”）", failures);
                SelfTestCheck(unsupportedDest.GetComponents<Component>().Length == 1,
                              "未支持类型的源对象在目标上留下了额外组件（目标组件数应为 1 = Transform）",
                              failures);
                SelfTestCheck(ReferenceEquals(SafeOwner(unsupportedSource.GetComponent<WheelCollider>()),
                                              unsupportedSource),
                              "fail closed 用例改写了源对象的 owner", failures);

                // ---------------- 断言 6b（第五轮负例）：树外 probeAnchor 必须在**整树建完后**被拒绝
                // 生产语义 = 拷贝阶段只排队（不解析），解析阶段查不到就失败；绝不把引用指回源场景。
                GameObject outsideAnchor = NewSelfTestObject("PMSelfTestOutsideAnchor", null, owned);
                // anchor 对象自身也带 Renderer：避免任何"锚点无效"的语义歧义（这是 probeAnchor 的常规用法）。
                AddSelfTestChildRenderer(outsideAnchor, mesh, materialA);
                GameObject outsideSource = NewSelfTestObject("PMSelfTestProbeOutsideSource", null, owned);
                MeshRenderer outsideSourceRenderer = outsideSource.AddComponent<MeshRenderer>();
                outsideSourceRenderer.sharedMaterials = new Material[] { materialA };
                outsideSourceRenderer.probeAnchor = outsideAnchor.transform;

                GameObject outsideDest = NewSelfTestObject("PMSelfTestProbeOutsideDest", null, owned);
                MapCopyContext outsideContext = new MapCopyContext();
                outsideContext.NodeMap[outsideSource.transform] = outsideDest.transform;
                // 注意：**故意不登记** outsideAnchor（它在本层级之外）⇒ 解析阶段必须失败。

                List<string> outsideOffenders;
                bool outsideCopied = CopyComponentSet(outsideSource, outsideDest, true,
                                                     outsideContext, out outsideOffenders);
                SelfTestCheck(outsideCopied,
                              "负例前置失效：树外 probeAnchor 在拷贝阶段就被拒了（应只排队、留到解析阶段判）："
                              + Join(outsideOffenders), failures);

                List<string> outsideFixupFailures = new List<string>();
                bool outsideResolved = ResolveDeferredReferences(outsideContext, outsideFixupFailures);
                SelfTestCheck(!outsideResolved && outsideFixupFailures.Count > 0,
                              "树外 probeAnchor 在整树建完后没有被拒绝（跨树引用必须失败）", failures);

                MeshRenderer outsideDestRenderer = outsideDest.GetComponent<MeshRenderer>();
                SelfTestCheck(outsideDestRenderer != null && outsideDestRenderer.probeAnchor == null,
                              "树外 probeAnchor 解析失败后目标上仍留下了非空引用（不得指源）", failures);

                // ---------------- 断言 6c（第五轮负例）：LOD 有任一 Renderer 解析不了时**不得** SetLODs
                GameObject lodNegSource = NewSelfTestObject("PMSelfTestLodNegSource", null, owned);
                GameObject lodNegSourceChild = NewSelfTestObject("PMSelfTestLodNegSourceChild",
                                                                 lodNegSource.transform, owned);
                MeshRenderer lodNegSourceRenderer = AddSelfTestChildRenderer(lodNegSourceChild, mesh, materialA);
                LODGroup lodNegSourceLod = lodNegSource.AddComponent<LODGroup>();
                lodNegSourceLod.SetLODs(new LOD[] { new LOD(0.5f, new Renderer[] { lodNegSourceRenderer }) });
                lodNegSourceLod.size = 3f;

                GameObject lodNegDest = NewSelfTestObject("PMSelfTestLodNegDest", null, owned);
                MapCopyContext lodNegContext = new MapCopyContext();
                lodNegContext.NodeMap[lodNegSource.transform] = lodNegDest.transform;
                // 注意：**故意不登记**子节点映射 ⇒ LOD Renderer 解析必然失败。

                List<string> lodNegOffenders;
                bool lodNegCopied = CopyComponentSet(lodNegSource, lodNegDest, true,
                                                     lodNegContext, out lodNegOffenders);
                SelfTestCheck(lodNegCopied,
                              "负例前置失效：LODGroup 在拷贝阶段就解析失败（应只排队）：" + Join(lodNegOffenders),
                              failures);

                List<string> lodNegFixupFailures = new List<string>();
                bool lodNegResolved = ResolveDeferredReferences(lodNegContext, lodNegFixupFailures);
                SelfTestCheck(!lodNegResolved && lodNegFixupFailures.Count > 0,
                              "LODGroup 有无法重绑定的 Renderer 时没有被整体拒绝（会写出不完整 LOD 配置）",
                              failures);

                LODGroup lodNegDestGroup = lodNegDest.GetComponent<LODGroup>();
                bool lodNegDestHasRenderer = false;
                if (lodNegDestGroup != null)
                {
                    LOD[] lodNegDestLevels = lodNegDestGroup.GetLODs();
                    for (int i = 0; i < lodNegDestLevels.Length; i++)
                    {
                        if (lodNegDestLevels[i].renderers != null && lodNegDestLevels[i].renderers.Length > 0)
                        {
                            lodNegDestHasRenderer = true;
                            break;
                        }
                    }
                }

                SelfTestCheck(lodNegDestGroup != null && !lodNegDestHasRenderer,
                              "LOD Renderer 解析失败，目标 LODGroup 却仍被写入了 Renderer"
                              + "（不得 SetLODs 不完整数组）", failures);

                // ---------------- 断言 7：Unity 自家一致性日志
                SelfTestCheck(!_unityConsistencyAnomaly,
                              "自测期间出现 Unity 自家 CheckConsistency 日志（组件登记表异常）", failures);
            }
            catch (Exception ex)
            {
                failures.Add("自测抛出异常：" + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                Application.logMessageReceived -= OnUnityLog;

                // F5 同一纪律：先 SetActive(false)、再 DestroyImmediate。
                for (int i = 0; i < owned.Count; i++)
                {
                    GameObject go = owned[i] as GameObject;
                    if (go == null)
                    {
                        continue;
                    }

                    try
                    {
                        go.SetActive(false);
                    }
                    catch (Exception)
                    {
                    }
                }

                for (int i = owned.Count - 1; i >= 0; i--)
                {
                    if (owned[i] != null)
                    {
                        UnityEngine.Object.DestroyImmediate(owned[i]);
                    }
                }

                owned.Clear();
                _unityConsistencyAnomaly = anomalyBefore;
                _unityConsistencyMessages.Clear();
            }

            return FinishSelfTest(summary, failures);
        }

        private static GameObject NewSelfTestObject(string name, Transform parent, List<UnityEngine.Object> owned)
        {
            GameObject go = new GameObject(name);
            go.hideFlags = HideFlags.HideAndDontSave;

            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }

            owned.Add(go);
            return go;
        }

        private static MeshRenderer AddSelfTestChildRenderer(GameObject go, Mesh mesh, Material material)
        {
            MeshFilter filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;

            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = new Material[] { material };
            return renderer;
        }

        /// <summary>自测用网格：一个四点四面体（可读、凸），既能当 MeshFilter 网格也能当 convex MeshCollider。</summary>
        private static Mesh CreateSelfTestMesh()
        {
            Mesh mesh = new Mesh();
            mesh.name = "PMSelfTestMesh";
            mesh.hideFlags = HideFlags.HideAndDontSave;
            mesh.vertices = new Vector3[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 0f, 1f),
                new Vector3(0f, 1f, 0f),
            };
            mesh.triangles = new int[] { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static bool TryCreateSelfTestMaterials(out Material materialA, out Material materialB,
                                                       List<string> failures)
        {
            materialA = null;
            materialB = null;

            Shader shader = Shader.Find("Standard");
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Color");
            }

            if (shader == null)
            {
                failures.Add("自测找不到可用 Shader（Standard / Unlit/Color 都为 null）：无法构造材质数组用例。");
                return false;
            }

            materialA = new Material(shader);
            materialA.name = "PMSelfTestMaterialA";
            materialA.hideFlags = HideFlags.HideAndDontSave;
            materialA.color = Color.red;

            materialB = new Material(shader);
            materialB.name = "PMSelfTestMaterialB";
            materialB.hideFlags = HideFlags.HideAndDontSave;
            materialB.color = Color.blue;
            return true;
        }

        private static void SelfTestCheck(bool condition, string label, List<string> failures)
        {
            if (!condition)
            {
                failures.Add(label);
            }
        }

        /// <summary>对象上（不含 Transform）的组件类型名集合，排序后拼成一个可比对的字符串。</summary>
        private static string SelfTestComponentTypeKey(GameObject go)
        {
            Component[] components = go.GetComponents<Component>();
            List<string> names = new List<string>(components.Length);
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] == null)
                {
                    names.Add("<null>");
                    continue;
                }

                if (components[i] is Transform)
                {
                    continue;
                }

                names.Add(components[i].GetType().Name);
            }

            names.Sort(StringComparer.Ordinal);
            return Join(names);
        }

        private static bool FinishSelfTest(StringBuilder summary, List<string> failures)
        {
            summary.AppendLine("  失败项=" + failures.Count.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < failures.Count; i++)
            {
                summary.AppendLine("    · " + failures[i]);
            }

            string text = summary.ToString();
            if (failures.Count == 0)
            {
                Debug.Log("[PMBattleContentBuild] 组件显式拷贝自测通过（日志：" + SelfTestLogRelativePath + "）。");
            }
            else
            {
                Debug.LogError("[PMBattleContentBuild] 组件显式拷贝自测失败（"
                               + failures.Count.ToString(CultureInfo.InvariantCulture) + " 项）：\n" + text);
            }

            string writeError;
            if (!TryWriteTextFile(SelfTestLogRelativePath, text, out writeError))
            {
                Debug.LogWarning("[PMBattleContentBuild] 自测日志无法落盘：" + writeError);
            }

            return failures.Count == 0;
        }

        /// <summary>
        /// F11：只读诊断实现。
        ///
        /// 硬约束（与烘焙的差别就在这里）：
        ///   · **不创建任何 GameObject**（因此一个 Collider 都不会进任何物理世界，零崩溃风险）；
        ///   · 不写任何 Unity 资产（只写 `Logs/*.log`，已被 .gitignore 覆盖）；
        ///   · 不改 activeScene / Selection / Random.state（finally 还原）。
        /// </summary>
        public static bool DiagnoseContent()
        {
            if (Application.isPlaying)
            {
                Debug.LogError("[PMBattleContentBuild] 诊断在 Play 模式下被调用：拒绝执行。");
                return false;
            }

            StringBuilder summary = new StringBuilder();
            summary.AppendLine("[PMBattleContentBuild] 只读诊断（no bake）：不创建对象、不写资产、不改活动场景。");

            string logPath = AbsolutePath(DiagnoseLogRelativePath);
            _bakeLog = BakeLog.Open(logPath, summary);
            if (_bakeLog != null)
            {
                summary.AppendLine("  诊断日志（每步 flush）：" + logPath);
            }

            Scene activeBefore = SceneManager.GetActiveScene();
            UnityEngine.Object[] selectionBefore = Selection.objects;
            UnityEngine.Random.State randomBefore = UnityEngine.Random.state;

            SourceSceneScope scope = null;
            bool ok = false;
            try
            {
                Step("diagnose", "开始只读诊断（不创建对象）");
                DumpEnvironmentFacts(summary);

                Step("diagnose", "打开源场景（只读 Additive 预览）");
                string sceneError;
                scope = SourceSceneScope.Open(SourceScenePath, summary, out sceneError);
                if (scope == null)
                {
                    summary.AppendLine("  打开源场景失败：" + sceneError);
                    Step("diagnose", "失败：打开源场景失败");
                    return false;
                }

                ScenseBuildLogic logic = FindLogic(scope.Scene);
                if (logic == null)
                {
                    summary.AppendLine("  源场景里找不到 ScenseBuildLogic：不能凭空猜模板来源。");
                    Step("diagnose", "失败：找不到 ScenseBuildLogic");
                    return false;
                }

                summary.AppendLine("  布局参数：mapx=" + logic.mapx.ToString(CultureInfo.InvariantCulture)
                                   + " mapy=" + logic.mapy.ToString(CultureInfo.InvariantCulture));

                // ---- 模板事实（每个模板的激活态 / 子物体数 / 组件类型序列 / 碰撞体分类）
                DumpPaletteFacts("floors", logic.floors, summary);
                DumpPaletteFacts("walls", logic.walls, summary);
                DumpPaletteFacts("obstacles", logic.obstacles, summary);
                DumpPaletteFacts("Grasses", logic.Grasses, summary);
                DumpPaletteFacts("trees", logic.trees, summary);

                GameObject groundPlane = FindByPath(scope.Scene, GroundSourcePath);
                if (groundPlane == null)
                {
                    summary.AppendLine("  找不到地面 \"" + GroundSourcePath + "\"。");
                    Step("diagnose", "失败：找不到地面");
                    return false;
                }

                Step("diagnose", "地面事实：activeSelf=" + (groundPlane.activeSelf ? 1 : 0));
                summary.AppendLine("  地面 \"" + GroundSourcePath + "\"：activeSelf="
                                   + (groundPlane.activeSelf ? 1 : 0)
                                   + " childCount=" + groundPlane.transform.childCount.ToString(CultureInfo.InvariantCulture)
                                   + " 组件=" + DescribeSourceComponents(groundPlane.GetComponents<Component>())
                                   + " 非TriggerCollider=" + CountSolidColliders(groundPlane).ToString(CultureInfo.InvariantCulture));

                // ---- 落点事实（同种子两次生成必须一致）
                List<Placement> placements;
                string layoutError;
                if (!BuildLayout(logic, out placements, out layoutError))
                {
                    summary.AppendLine("  生成布局失败：" + layoutError);
                    Step("diagnose", "失败：生成布局失败");
                    return false;
                }

                List<Placement> placementsAgain;
                string layoutErrorAgain;
                bool layoutReproducible = BuildLayout(logic, out placementsAgain, out layoutErrorAgain)
                                          && SamePlacements(placements, placementsAgain);
                AccumulateLayoutCounts(summary, placements);
                summary.AppendLine("  **重点**：布局同种子两次一致=" + (layoutReproducible ? "是" : "否")
                                   + "（源 SHA 已钉死输入；否就该查 PRNG）");

                PlacementSelection selection;
                string selectError;
                if (!SelectPlacements(logic, placements, SkipInactivePlacements, out selection, out selectError))
                {
                    summary.AppendLine("  落点选择失败：" + selectError);
                    Step("diagnose", "失败：落点选择失败");
                    return false;
                }

                AccumulateSelectionCounts(summary, selection);

                // ---- 摘要流（与烘焙**同一份构造逻辑**）
                string srcSceneSha = Sha256File(AbsolutePath(SourceScenePath));
                string srcLogicSha = Sha256File(AbsolutePath(SourceLogicPath));
                string srcPlayerSha = Sha256File(AbsolutePath(SourcePlayerPath));
                string stream = BuildDigestStream(logic, placements, selection, groundPlane,
                                                 srcSceneSha, srcLogicSha, srcPlayerSha, summary);
                string streamAgain = BuildDigestStream(logic, placements, selection, groundPlane,
                                                      srcSceneSha, srcLogicSha, srcPlayerSha, summary);

                string contentDigest = Sha256Hex(Encoding.UTF8.GetBytes(stream));
                summary.AppendLine("  摘要流：长度=" + stream.Length.ToString(CultureInfo.InvariantCulture)
                                   + " 同一次内两次逐字符相同="
                                   + (string.Equals(stream, streamAgain, StringComparison.Ordinal) ? "是" : "否")
                                   + " sha256=" + Sha256Hex(Encoding.UTF8.GetBytes(stream)));
                summary.AppendLine("  contentDigest=" + contentDigest);

                string dumpError;
                if (!TryWriteTextFile(DiagnoseDigestRelativePath, stream, out dumpError))
                {
                    summary.AppendLine("  摘要流落盘失败：" + dumpError
                                       + "（不影响诊断；但跨会话逐字符比对就没得比了）");
                }
                else
                {
                    summary.AppendLine("  摘要流已落盘（**跨会话比对就比它**）："
                                       + AbsolutePath(DiagnoseDigestRelativePath));
                }

                Step("diagnose", "摘要流落盘完成；contentDigest=" + contentDigest);
                ok = true;
            }
            catch (Exception ex)
            {
                summary.AppendLine("  异常：" + ex.GetType().Name + " " + ex.Message);
                summary.AppendLine(ex.StackTrace);
                Step("diagnose", "异常：" + ex.GetType().Name + " " + ex.Message);
                ok = false;
            }
            finally
            {
                if (activeBefore.IsValid())
                {
                    SceneManager.SetActiveScene(activeBefore);
                }

                Selection.objects = selectionBefore;
                UnityEngine.Random.state = randomBefore;

                if (scope != null)
                {
                    Step("finally", "关闭源场景（只关我们自己开的那一份）");
                    scope.Dispose();
                }

                Step("diagnose", ok ? "结束：诊断成功" : "结束：诊断失败");

                BakeLog log = _bakeLog;
                _bakeLog = null;
                if (log != null)
                {
                    log.Raw("---------- 诊断摘要 ----------");
                    log.Raw(summary.ToString());
                    log.Dispose();
                }

                summary.AppendLine(ok ? "  结果：诊断成功" : "  结果：诊断失败");
                if (ok)
                {
                    Debug.Log(summary.ToString());
                }
                else
                {
                    Debug.LogError(summary.ToString());
                }

                Debug.Log("[PMBattleContentBuild] 诊断日志：" + logPath);
            }

            return ok;
        }

        /// <summary>
        /// F11：物理世界隔离探针 —— 解决“问题1 BLOCKED”所需的**最小 Unity 内动作**。
        ///
        /// 它只创建一个**空场景**（零 GameObject、零 Collider），用
        /// `Scene.GetPhysicsScene()` vs `Physics.defaultPhysicsScene` 判定两个候选有没有独立物理世界，
        /// 然后立刻关掉并还原 activeScene / Random.state。
        /// **不碰源场景、不创建任何 GameObject** —— 因此不可能触发第二轮那类崩溃。
        /// </summary>
        [MenuItem("Build/Diagnose PMNet Physics Isolation (empty scene probe)")]
        public static void DiagnosePhysicsIsolationMenu()
        {
            if (Application.isPlaying)
            {
                Debug.LogError("[PMBattleContentBuild] 隔离探针在 Play 模式下被调用：拒绝执行。");
                return;
            }

            StringBuilder summary = new StringBuilder();
            summary.AppendLine("[PMBattleContentBuild] 物理世界隔离探针（只建一个**空场景**，零 GameObject / 零 Collider）。");

            Scene activeBefore = SceneManager.GetActiveScene();
            UnityEngine.Random.State randomBefore = UnityEngine.Random.state;

            Scene probe;
            ScratchSceneKind kind;
            string error;
            bool isolated = TryCreateIsolatedScratchScene(summary, out probe, out kind, out error);

            try
            {
                if (isolated)
                {
                    summary.AppendLine("  结论：**可以隔离**（方式=" + kind + "）。证据：" + _scratchIsolationDetail);
                    summary.AppendLine("  ⇒ 报告《_r4c_round3_report.md》§问题1 的 BLOCKED 可以用该方式解除；"
                                       + "请把本段原文回传。");
                }
                else
                {
                    summary.AppendLine("  结论：**两个候选都无法证明独立物理世界**。");
                    summary.AppendLine("  " + error);
                    summary.AppendLine("  ⇒ 在你这台 Unity 2019.4 上，临时对象无法脱离默认物理世界；"
                                       + "问题1 维持 BLOCKED，必须走“临时场景里不创建 Collider”的替代方案。");
                }
            }
            finally
            {
                if (isolated && probe.IsValid())
                {
                    CloseScratchScene(probe, kind, summary);
                }

                if (activeBefore.IsValid())
                {
                    SceneManager.SetActiveScene(activeBefore);
                }

                UnityEngine.Random.state = randomBefore;
                summary.AppendLine("  探针结束（空场景已关闭，activeScene / Random.state 已还原；"
                                   + "全程未创建任何 GameObject）");

                if (isolated)
                {
                    Debug.Log(summary.ToString());
                }
                else
                {
                    Debug.LogError(summary.ToString());
                }
            }
        }

        /// <summary>F11：环境事实（诊断用：Unity 版本、默认物理场景、当前已打开场景）。</summary>
        private static void DumpEnvironmentFacts(StringBuilder summary)
        {
            summary.AppendLine("  环境：Unity " + Application.unityVersion);
            summary.AppendLine("  Physics.defaultPhysicsScene.IsValid()="
                               + (Physics.defaultPhysicsScene.IsValid() ? "true" : "false"));

            int sceneCount = SceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                summary.AppendLine("    已打开场景[" + i.ToString(CultureInfo.InvariantCulture) + "] name=\""
                                   + s.name + "\" path=\"" + s.path + "\" isLoaded=" + s.isLoaded
                                   + " isDirty=" + s.isDirty
                                   + " isPreview=" + (s.IsValid() ? EditorSceneManager.IsPreviewScene(s).ToString() : "<invalid>"));
            }
        }

        /// <summary>F11：一个模板数组的事实（激活态 / 节点数 / 组件类型序列 / 碰撞体分类）。</summary>
        private static void DumpPaletteFacts(string label, GameObject[] templates, StringBuilder summary)
        {
            if (templates == null)
            {
                summary.AppendLine("  调色板 " + label + "：**null**（源类字段初始化失败）");
                return;
            }

            summary.AppendLine("  调色板 " + label + "：模板数=" + templates.Length.ToString(CultureInfo.InvariantCulture)
                               + "（含子物体共 " + CountHierarchy(templates).ToString(CultureInfo.InvariantCulture)
                               + " 个节点）");
            for (int i = 0; i < templates.Length; i++)
            {
                GameObject t = templates[i];
                if (t == null)
                {
                    summary.AppendLine("    [" + i.ToString(CultureInfo.InvariantCulture) + "]=<null>");
                    continue;
                }

                Component[] components = t.GetComponents<Component>();
                summary.AppendLine("    [" + i.ToString(CultureInfo.InvariantCulture) + "] name=\"" + t.name
                                   + "\" activeSelf=" + (t.activeSelf ? 1 : 0)
                                   + " childCount=" + t.transform.childCount.ToString(CultureInfo.InvariantCulture)
                                   + " hierarchyCount=" + t.transform.hierarchyCount.ToString(CultureInfo.InvariantCulture)
                                   + " 非TriggerCollider=" + CountSolidColliders(t).ToString(CultureInfo.InvariantCulture)
                                   + " 组件=" + DescribeSourceComponents(components));
            }
        }

        /// <summary>F11：一个对象子树里有几个非 trigger Collider（源事实用）。</summary>
        private static int CountSolidColliders(GameObject root)
        {
            if (root == null)
            {
                return 0;
            }

            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            int count = 0;
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null && !colliders[i].isTrigger)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// 构建前 hook（由 <c>PMDsBuild.BuildWindowsHeadlessDs</c> 调用）。
        ///
        /// 语义：
        ///   · 产物缺失或源资产比产物新 ⇒ 先完整烘焙；
        ///   · 然后**只读校验**（manifest 内部一致 + 资源组件约束）；
        ///   · 任一步失败返回 false —— 调用方必须让构建失败，
        ///     绝不允许"缺资源的包"被当成功（契约：缺资源不能假成功）。
        ///
        /// 为什么先判"是否需要烘焙"而不是每次都重烘：正常构建不需要重打开源场景，
        /// 少一次编辑模式场景加载就少一份副作用面；而一旦源确实变了，会显式重烘。
        /// </summary>
        public static bool PrepareForBuild()
        {
            if (Application.isPlaying)
            {
                Debug.LogError("[PMBattleContentBuild] PrepareForBuild 在 Play 模式下被调用：拒绝执行"
                               + "（本文件只在编辑期烘焙，不参与运行）。");
                return false;
            }

            bool allExist = File.Exists(AbsolutePath(MapPrefabPath))
                            && File.Exists(AbsolutePath(PlayerPrefabPath))
                            && File.Exists(AbsolutePath(ManifestAssetPath));

            bool stale;
            string staleReason;
            if (!allExist)
            {
                stale = true;
                staleReason = "有产物缺失";
            }
            else
            {
                stale = IsStale(out staleReason);
            }

            if (stale)
            {
                Debug.Log("[PMBattleContentBuild] 正式内容需要重新烘焙（" + staleReason + "）。");
                if (!PrepareContent())
                {
                    return false;
                }
            }
            else
            {
                Debug.Log("[PMBattleContentBuild] 正式内容已就绪且不比源资产旧，跳过烘焙，直接校验。");
            }

            return ValidateContent();
        }

        // ==================================================================== 烘焙日志（F6）

        /// <summary>
        /// 当前烘焙会话的日志（一次烘焙一个；<c>PrepareContent</c> 负责开/关）。
        ///
        /// 用静态字段而不是参数透传：本类全是静态方法，而需要打标记的地方跨越
        /// PrepareContentInternal / BuildMapHierarchy / SavePrefab / Verify* 多层；
        /// 逐层加参数会把签名改得到处都是（且这些方法都是私有的）。
        /// 编辑器主线程单跑，不存在并发烘焙。
        /// </summary>
        private static BakeLog _bakeLog;

        // ---------------------------------------------------------------- F10 诊断状态（第三轮）

        /// <summary>Unity 是否在我们拷贝组件期间报告过“组件表不一致、已修复”。</summary>
        private static bool _unityConsistencyAnomaly;

        /// <summary>Unity 一致性日志原文（有界，最多 64 条），用于把根因写进摘要与日志。</summary>
        private static readonly List<string> _unityConsistencyMessages = new List<string>(64);

        /// <summary>
        /// 目标对象的创建序号（F10）：每次 `new GameObject` 后 +1。
        ///
        /// 为什么需要它：`GetInstanceID()` 每次加载都不同，单看它无法判断
        /// “同一个 dest 被处理了两次”。有了**单次烘焙内单调递增**的序号，再把
        /// `instanceID → 序号 + 父路径` 记进表，就能直接看出“这个 dest 之前出现过”。
        /// </summary>
        private static int _destSequence;

        /// <summary>dest.instanceID → 诊断行（序号 / 父路径 / 场景）。只读用途，随烘焙重建。</summary>
        private static readonly Dictionary<int, string> _destRegistry = new Dictionary<int, string>(512);

        /// <summary>写一条步骤标记（没有日志会话时静默）。</summary>
        private static void Step(string phase, string detail)
        {
            BakeLog log = _bakeLog;
            if (log != null)
            {
                log.Step(phase, detail);
            }
        }

        /// <summary>
        /// 逐步落盘烘焙日志。
        ///
        /// 存在的理由（真机教训）：入口摘要只在 <c>finally</c> 里 <c>Debug.Log</c> 一次，而本次崩溃
        /// 就发生在 <c>finally</c> 结束之前（关场景时 SIGSEGV）⇒ Console 与 Editor.log 里
        /// **一行摘要都没有**，只能靠零散的 Unity 内部报错反推步骤。
        ///
        /// 两条纪律：
        ///   · **每步 flush**：崩溃后文件里最后一条就是最后完成的步骤；
        ///   · **FileShare.ReadWrite**：Unity 进程占着它的时候，外部（记事本/type）仍能读到当前进度。
        /// 日志自身失败（目录不可写等）绝不把烘焙弄失败：只写进摘要并继续。
        /// </summary>
        private sealed class BakeLog : IDisposable
        {
            private readonly string _absolutePath;
            private readonly FileStream _stream;
            private readonly StreamWriter _writer;
            private int _step;
            private bool _disposed;

            private BakeLog(string absolutePath, FileStream stream, StreamWriter writer)
            {
                _absolutePath = absolutePath;
                _stream = stream;
                _writer = writer;
            }

            public string AbsolutePath { get { return _absolutePath; } }

            public static BakeLog Open(string absolutePath, StringBuilder summary)
            {
                try
                {
                    string directory = Path.GetDirectoryName(absolutePath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // 每次烘焙重建（描述“本次尝试”）；FileShare.ReadWrite ⇒ 会话进行中也能被外部读取。
                    FileStream stream = new FileStream(absolutePath, FileMode.Create, FileAccess.Write,
                                                       FileShare.ReadWrite);
                    StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false));
                    writer.AutoFlush = true;

                    BakeLog log = new BakeLog(absolutePath, stream, writer);
                    log.Step("bake", "日志已打开（每次烘焙重建；FileShare.ReadWrite ⇒ 边跑边可读）");
                    return log;
                }
                catch (Exception ex)
                {
                    if (summary != null)
                    {
                        summary.AppendLine("  注意：无法打开烘焙日志 " + absolutePath + "（"
                                           + ex.GetType().Name + " " + ex.Message + "）：继续烘焙，"
                                           + "但崩溃时只能靠 Console / Editor.log。");
                    }

                    return null;
                }
            }

            public void Step(string phase, string detail)
            {
                if (_disposed)
                {
                    return;
                }

                _step++;
                try
                {
                    _writer.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                                      + " [#" + _step.ToString(CultureInfo.InvariantCulture) + "] "
                                      + phase + " :: " + (detail == null ? string.Empty : detail));
                    _writer.Flush();
                }
                catch (Exception)
                {
                    // 日志写失败不拖垮烘焙（磁盘满/被杀软锁住等）。
                }
            }

            /// <summary>原样写一段文本（用于把最终摘要也收进日志文件，便于事后对照）。</summary>
            public void Raw(string text)
            {
                if (_disposed || text == null)
                {
                    return;
                }

                try
                {
                    _writer.WriteLine(text);
                    _writer.Flush();
                }
                catch (Exception)
                {
                }
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                try
                {
                    _writer.Flush();
                    _writer.Dispose();
                }
                catch (Exception)
                {
                    // 忽略：关闭失败不影响烘焙结果。
                }

                try
                {
                    _stream.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }

        // ==================================================================== 烘焙主流程

        /// <summary>
        /// 强制完整烘焙（菜单 / 构建 hook 的公共实现）。
        ///
        /// 顺序（失败即停止，且**不留下 manifest**）：
        ///   0) 拒绝 Play 模式；确保输出目录存在；先删除旧 manifest（发布信号最后才写）；
        ///   1) 打开源场景（Additive 预览读）→ 编辑模式脚本审计 → 取 ScenseBuildLogic；
        ///   2) 生成布局（局部 PRNG）+ 逐个模板规范化签名 → 算 contentDigest；
        ///   3) 在临时空场景里搭地图层级 → 深拷贝模板 → 真 API 存 prefab → 回读校验；
        ///   4) 克隆源角色 prefab → 清理到白名单 → 真 API 存 prefab → 回读校验；
        ///   5) 写 manifest（JsonUtility 序列化 C2 的类型）→ 再解析一次确认可读；
        ///   6) finally：关场景、还原 activeScene / 选择 / 全局随机态。
        /// </summary>
        public static bool PrepareContent()
        {
            if (Application.isPlaying)
            {
                Debug.LogError("[PMBattleContentBuild] 处于 Play 模式：拒绝烘焙（本文件只在编辑期生成资产）。");
                return false;
            }

            StringBuilder summary = new StringBuilder();
            summary.AppendLine("[PMBattleContentBuild] 开始烘焙正式内容（C1）");

            // F6：逐步落盘日志先开（崩溃前最后完成的步骤就靠它定位）。
            string logPath = AbsolutePath(BakeLogRelativePath);
            _bakeLog = BakeLog.Open(logPath, summary);
            if (_bakeLog != null)
            {
                summary.AppendLine("  烘焙日志（每步 flush，FileShare.ReadWrite）：" + logPath);
            }

            // F10（第三轮）：装 Unity 自家日志钩子。
            // `CheckConsistency: GameObject does not reference component <类型>. Fixing.` 是 Unity
            // 在**修复它自己**的组件表 —— 它出现的时刻就是组件登记表已经不可信的起点（真机日志 32 条，
            // 全部来自我们的 `ApplyModifiedPropertiesWithoutUndo`）。一旦出现就立刻中止本次烘焙：
            // 宁可在第一处异常停住，也不要继续堆出上百个坏对象再 SIGSEGV。
            _unityConsistencyAnomaly = false;
            _unityConsistencyMessages.Clear();
            _destSequence = 0;
            _destRegistry.Clear();

            // F8：清掉可能由上一次运行（尤其是崩溃的那一次）遗留的临时场景引用。
            // 不清的话，下一次建对象时 AdoptIntoScratchScene 会往一个已经关掉的场景里搬。
            _scratchScene = default(Scene);
            _scratchSceneIsPreview = false;
            _scratchIsolationDetail = null;

            Application.logMessageReceived += OnUnityLog;

            bool ok = false;
            try
            {
                Step("prepare", "开始烘焙（application.isPlaying=false）");
                EnsureResourcesDirectory();
                Step("prepare", "资源目录已就绪：" + ResourcesDir);

                // 发布信号 = manifest：进流程先删掉它，任何失败路径都不会留下"有效 manifest"。
                string clearError;
                if (!RemoveManifestOrInvalidate(summary, out clearError))
                {
                    summary.AppendLine("  无法清除旧 manifest（" + clearError + "）：拒绝开始烘焙，"
                                       + "以免留下\"新 prefab + 旧 manifest\"的组合（digest 与实际内容不再对应）。");
                    return false;
                }

                summary.AppendLine("  已清除旧 manifest（发布信号最后才写）");
                Step("prepare", "已清除旧 manifest（发布信号最后才写）");

                ok = PrepareContentInternal(summary);
            }
            catch (Exception ex)
            {
                summary.AppendLine("  异常：" + ex.GetType().Name + " " + ex.Message);
                summary.AppendLine(ex.StackTrace);
                Step("prepare", "异常：" + ex.GetType().Name + " " + ex.Message);
                ok = false;
            }
            finally
            {
                Application.logMessageReceived -= OnUnityLog;

                if (_unityConsistencyAnomaly)
                {
                    summary.AppendLine("  **Unity 组件表一致性异常**（第三轮定位到的关键现场）：Unity 在我们拷组件期间报了 "
                                       + _unityConsistencyMessages.Count.ToString(CultureInfo.InvariantCulture)
                                       + " 条 `CheckConsistency: GameObject does not reference component ...`，"
                                       + "说明组件与 GameObject 的登记表不一致（也就是第二轮 `重复组件:` 报错的上游现场）。"
                                       + "第四轮已把产生该现场的那条**通用序列化写入**路径整体删除（字段拷贝改走公开 API），"
                                       + "本钩子保留为兜底：它抓的是 Unity 自己的登记异常，与具体哪条写路径无关。"
                                       + "原文（前 8 条，先拿这几条去定位）：");
                    for (int i = 0; i < _unityConsistencyMessages.Count && i < 8; i++)
                    {
                        summary.AppendLine("    · " + _unityConsistencyMessages[i]);
                    }
                }

                if (!ok)
                {
                    // 失败兜底：绝不留"有效 manifest"（含 AssetDatabase 删不掉时的 File.Delete/作废兜底）。
                    string clearError;
                    if (!RemoveManifestOrInvalidate(summary, out clearError))
                    {
                        summary.AppendLine("  **严重**：无法清除也无法作废 manifest —— " + clearError);
                    }
                }

                Step("prepare", ok ? "结束：成功" : "结束：失败（未发布 manifest）");

                BakeLog log = _bakeLog;
                _bakeLog = null;
                if (log != null)
                {
                    // 把最终摘要也收进日志文件：崩溃时没有这段，成功/失败时它就是完整现场。
                    log.Raw("---------- 最终摘要（与 Console 一致） ----------");
                    log.Raw(summary.ToString());
                    log.Dispose();
                }

                summary.AppendLine(ok ? "  结果：成功" : "  结果：失败（未发布 manifest）");
                if (ok)
                {
                    Debug.Log(summary.ToString());
                }
                else
                {
                    Debug.LogError(summary.ToString());
                }

                if (logPath != null)
                {
                    Debug.Log("[PMBattleContentBuild] 烘焙日志（最后一步就是成功/失败/崩溃前完成的步骤）："
                              + logPath);
                }
            }

            return ok;
        }

        private static bool PrepareContentInternal(StringBuilder summary)
        {
            string srcSceneSha = Sha256File(AbsolutePath(SourceScenePath));
            string srcLogicSha = Sha256File(AbsolutePath(SourceLogicPath));
            string srcPlayerSha = Sha256File(AbsolutePath(SourcePlayerPath));
            summary.AppendLine("  源文件 SHA256：scene=" + Short(srcSceneSha)
                               + " logic=" + Short(srcLogicSha) + " player=" + Short(srcPlayerSha));
            Step("sources", "源文件 SHA：scene=" + Short(srcSceneSha) + " logic=" + Short(srcLogicSha)
                            + " player=" + Short(srcPlayerSha));

            Scene activeBefore = SceneManager.GetActiveScene();
            UnityEngine.Object[] selectionBefore = Selection.objects;
            UnityEngine.Random.State randomBefore = UnityEngine.Random.state;

            SourceSceneScope scope = null;
            Scene scratchScene = default(Scene);
            ScratchSceneKind scratchKind = ScratchSceneKind.None;
            bool scratchCreated = false;
            GameObject mapRoot = null;
            GameObject playerClone = null;

            try
            {
                // ---------------- 1) 打开源场景（只读）＋ 编辑模式脚本审计
                Step("source-scene", "打开源场景（Additive 只读预览）开始：" + SourceScenePath);
                string sceneError;
                scope = SourceSceneScope.Open(SourceScenePath, summary, out sceneError);
                if (scope == null)
                {
                    summary.AppendLine("  打开源场景失败：" + sceneError);
                    Step("source-scene", "失败：" + sceneError);
                    return false;
                }

                ScenseBuildLogic logic = FindLogic(scope.Scene);
                if (logic == null)
                {
                    summary.AppendLine("  源场景里找不到 ScenseBuildLogic 实例（模板调色板的载体）："
                                       + "不能凭空猜模板来源，显式失败。");
                    return false;
                }

                GameObject groundPlane = FindByPath(scope.Scene, GroundSourcePath);
                if (groundPlane == null)
                {
                    summary.AppendLine("  源场景里找不到地面对象 \"" + GroundSourcePath + "\"："
                                       + "正式地图没有地面碰撞等于不可用，显式失败。");
                    return false;
                }

                // F2 的地面半边：地面是**唯一**的真实地板碰撞来源（floors 模板自身没有 Collider），
                // 因此它未激活时不能像普通落点那样"跳过" —— 那等于出一张没有可站立面的地图。
                if (!groundPlane.activeSelf)
                {
                    summary.AppendLine("  地面对象 \"" + GroundSourcePath + "\" 在源场景里处于**未激活**状态"
                                       + "（m_IsActive: 0）：它是正式地图**唯一**的碰撞地板（floors 模板自身"
                                       + "没有 Collider），跳过它等于地图没有可站立面 ⇒ 显式失败（不静默出图）。");
                    return false;
                }

                Step("templates", "已取到 ScenseBuildLogic + 地面（" + GroundSourcePath + "，active=1）");

                // ---------------- 2) 布局（局部确定 PRNG）
                List<Placement> placements;
                string layoutError;
                if (!BuildLayout(logic, out placements, out layoutError))
                {
                    summary.AppendLine("  生成布局失败：" + layoutError);
                    return false;
                }

                // 重复生成一次，证明"同种子同输入 ⇒ 同布局"（摘要稳定的第一道证据）。
                List<Placement> placementsAgain;
                string layoutErrorAgain;
                if (!BuildLayout(logic, out placementsAgain, out layoutErrorAgain)
                    || !SamePlacements(placements, placementsAgain))
                {
                    summary.AppendLine("  布局不可复现（两次生成结果不同）：PRNG 或模板读取不确定，"
                                       + "拒绝发布。原因：" + layoutErrorAgain);
                    return false;
                }

                AccumulateLayoutCounts(summary, placements);

                // ---------------- 2b) F2：落点选择（跳过未激活模板，计数并写进摘要与 digest）
                PlacementSelection selection;
                string selectError;
                if (!SelectPlacements(logic, placements, SkipInactivePlacements, out selection, out selectError))
                {
                    summary.AppendLine("  落点选择失败：" + selectError);
                    return false;
                }

                AccumulateSelectionCounts(summary, selection);
                Step("layout", "落点选择：built=" + selection.Built.Count.ToString(CultureInfo.InvariantCulture)
                               + " skippedInactive=" + selection.SkippedInactive.ToString(CultureInfo.InvariantCulture)
                               + "（skipInactive=" + (selection.SkipEnabled ? "true" : "false") + "）");

                // ---------------- 3) 摘要（模板源指纹 + 布局 + 源文件 SHA）
                Step("digest", "构造摘要流并算 contentDigest");
                string digestStream = BuildDigestStream(logic, placements, selection, groundPlane,
                                                       srcSceneSha, srcLogicSha, srcPlayerSha,
                                                       summary);

                // F9（第三轮）：同一次烘焙内**算两遍、逐字符比对**。
                // 任何“随访问次序 / AssetDatabase 缓存状态 / 加载时机变化”的量都会让两次不同。
                // 它不能证明跨会话稳定（那只能靠落盘后 diff），但能挡住最阴的一类：
                // 同一份输入在同一次运行里就给出两个摘要。
                string digestStreamAgain = BuildDigestStream(logic, placements, selection, groundPlane,
                                                            srcSceneSha, srcLogicSha, srcPlayerSha,
                                                            summary);
                if (!string.Equals(digestStream, digestStreamAgain, StringComparison.Ordinal))
                {
                    summary.AppendLine("  摘要流不可复现（同一次烘焙内两次生成不同）：拒绝发布。长度 "
                                       + digestStream.Length.ToString(CultureInfo.InvariantCulture) + " vs "
                                       + digestStreamAgain.Length.ToString(CultureInfo.InvariantCulture));
                    Step("digest", "失败：同一次烘焙内摘要流两次生成不同");
                    return false;
                }

                string contentDigest = Sha256Hex(Encoding.UTF8.GetBytes(digestStream));

                // F9：整份摘要流落盘（日志目录；`*.log` 已被 .gitignore 覆盖，不污染版本库）。
                // 用途：两个不同会话 / 不同活动场景各烘焙一次，然后直接 diff 这两份文件，
                // 就能**逐字符**验证“同一份源 ⇒ 同一个摘要输入”。没有它，跨会话确定性无法被检查。
                string digestDumpError;
                string digestStreamSha = Sha256Hex(Encoding.UTF8.GetBytes(digestStream));
                if (!TryWriteTextFile(DigestDumpRelativePath, digestStream, out digestDumpError))
                {
                    summary.AppendLine("  注意：摘要流无法落盘（" + digestDumpError + "）：不影响烘焙，"
                                       + "但“跨会话两次生成逐字符相同”就只能靠 Console 对比。");
                }
                else
                {
                    summary.AppendLine("  摘要流已落盘（跨会话逐字符比对用）："
                                       + AbsolutePath(DigestDumpRelativePath)
                                       + "，sha256=" + Short(digestStreamSha));
                }

                Step("digest", "摘要流 sha256=" + Short(digestStreamSha)
                               + " → " + DigestDumpRelativePath);

                uint collisionDigest;
                int worldVersion;
                string deriveError;
                if (!PMBattleContentManifest.TryDeriveCollisionDigestAndWorldVersion(
                        contentDigest, out collisionDigest, out worldVersion, out deriveError))
                {
                    summary.AppendLine("  由 contentDigest 派生 collisionDigest/worldVersion 失败："
                                       + deriveError);
                    return false;
                }

                summary.AppendLine("  contentDigest=" + contentDigest);
                summary.AppendLine("  collisionDigest=" + collisionDigest.ToString(CultureInfo.InvariantCulture)
                                   + " worldVersion=" + worldVersion.ToString(CultureInfo.InvariantCulture));
                Step("digest", "contentDigest=" + contentDigest
                               + " collisionDigest=" + collisionDigest.ToString(CultureInfo.InvariantCulture));

                // ---------------- 4) 临时场景（F8：必须自带独立物理世界，**先证明再动手**）
                string scratchError;
                if (!TryCreateIsolatedScratchScene(summary, out scratchScene, out scratchKind,
                                                  out scratchError))
                {
                    summary.AppendLine("  临时场景无法获得独立物理世界：**中止烘焙**（零副作用，不会碰源场景的重物），"
                                       + "原因：" + scratchError);
                    Step("scratch-scene", "失败：临时场景无法获得独立物理世界（BLOCKED，见报告 §问题1）");
                    return false;
                }

                scratchCreated = true;
                Step("scratch-scene", "已就绪：kind=" + scratchKind
                                      + " name=" + scratchScene.name
                                      + "（物理世界已运行期证明非默认世界）");

                // ---------------- 5) 地图 prefab
                Step("map", "搭建地图层级开始（tiles="
                             + selection.Built.Count.ToString(CultureInfo.InvariantCulture) + "）");
                string mapError;
                mapRoot = BuildMapHierarchy(logic, selection.Built, groundPlane, summary, out mapError);
                if (mapRoot == null)
                {
                    summary.AppendLine("  搭建地图层级失败：" + mapError);
                    Step("map", "失败：" + mapError);
                    return false;
                }

                Step("map", "地图层级已搭好：节点="
                             + mapRoot.transform.hierarchyCount.ToString(CultureInfo.InvariantCulture));

                bool mapSaved;
                if (!SavePrefab(mapRoot, MapPrefabPath, out mapSaved, summary))
                {
                    return false;
                }

                Step("map", "已保存地图 prefab：" + MapPrefabPath);

                // ---------------- 6) 角色 prefab
                GameObject playerSource = AssetDatabase.LoadAssetAtPath<GameObject>(SourcePlayerPath);
                if (playerSource == null)
                {
                    summary.AppendLine("  读不到源角色 prefab：" + SourcePlayerPath + "（契约资源缺失，显式失败）");
                    return false;
                }

                Step("player", "克隆并清理角色表现开始：" + SourcePlayerPath);
                string playerError;
                playerClone = BuildPlayerHierarchy(playerSource, summary, out playerError);
                if (playerClone == null)
                {
                    summary.AppendLine("  搭建角色层级失败：" + playerError);
                    Step("player", "失败：" + playerError);
                    return false;
                }

                bool playerSaved;
                if (!SavePrefab(playerClone, PlayerPrefabPath, out playerSaved, summary))
                {
                    return false;
                }

                Step("player", "已保存角色 prefab：" + PlayerPrefabPath);

                // ---------------- 7) 回读校验（生成后必须读回来检查，不能只看内存）
                Step("verify", "回读校验地图 prefab 开始");
                string verifyError;
                if (!VerifyMapPrefab(MapPrefabPath, selection.Built, selection, summary, out verifyError))
                {
                    summary.AppendLine("  地图 prefab 回读校验失败：" + verifyError);
                    Step("verify", "地图回读失败：" + verifyError);
                    return false;
                }

                Step("verify", "回读校验角色 prefab 开始");
                if (!VerifyPlayerPrefab(PlayerPrefabPath, summary, out verifyError))
                {
                    summary.AppendLine("  角色 prefab 回读校验失败：" + verifyError);
                    Step("verify", "角色回读失败：" + verifyError);
                    return false;
                }

                // ---------------- 8) 发布 manifest（最后一步 = 发布信号）
                Step("manifest", "写 manifest（发布信号，最后一步）");
                if (!WriteManifest(contentDigest, collisionDigest, worldVersion, summary))
                {
                    Step("manifest", "写 manifest 失败");
                    return false;
                }

                Step("manifest", "已发布 manifest：" + ManifestAssetPath);

                // 内存里的临时对象全部丢弃（临时场景 finally 里整体关闭）。
                scope.AppendRandomStateNote(summary, randomBefore);
                return true;
            }
            finally
            {
                // F5：销毁自建对象之前先 SetActive(false)（走一遍 Deactivate，注销 renderer/collider
                // 注册的 transform change interests），然后才 DestroyImmediate。
                // 旧码直接 DestroyImmediate，于是"从未 activated 的组件"带着 interests 被拆掉 ——
                // 正是真机 SIGSEGV 那串报错的上游。
                if (mapRoot != null)
                {
                    DestroySelfBuiltRoot(mapRoot, "地图 root", summary);
                    mapRoot = null;
                }

                if (playerClone != null)
                {
                    DestroySelfBuiltRoot(playerClone, "角色 root", summary);
                    playerClone = null;
                }

                if (scratchCreated && scratchScene.IsValid())
                {
                    // F5：关场景前确认自建对象已全部销毁（残留一个 root 就会重演老问题）。
                    EnsureScratchSceneEmpty(scratchScene, summary);
                    CloseScratchScene(scratchScene, scratchKind, summary);
                }

                if (activeBefore.IsValid())
                {
                    SceneManager.SetActiveScene(activeBefore);
                }

                Selection.objects = selectionBefore;
                UnityEngine.Random.state = randomBefore;

                if (scope != null)
                {
                    Step("finally", "关闭源场景（只关闭我们自己打开的那一份）");
                    scope.Dispose();
                    Step("finally", "源场景已关闭");
                }

                Step("finally", "清理完成（临时对象/临时场景/activeScene/Selection/Random.state 已还原）");
            }
        }

        // ==================================================================== F8：临时场景 + 物理世界隔离门

        /// <summary>
        /// F8：本次烘焙的临时场景。**所有自建对象都必须落在它里面** —— 不再依赖
        /// `SceneManager.SetActiveScene`（预览场景不能当活动场景，依赖它就会把对象建到用户场景里去）。
        /// </summary>
        private static Scene _scratchScene;

        /// <summary>F8：临时场景是不是 preview scene（决定关闭 API）。</summary>
        private static bool _scratchSceneIsPreview;

        /// <summary>F8：本次隔离尝试的证据说明（写进摘要，便于用户回传）。</summary>
        private static string _scratchIsolationDetail;

        /// <summary>
        /// F10：Unity 自家日志钩子。
        ///
        /// 只关心 <c>CheckConsistency:</c> —— 那是 Unity 在**修复它自己**的组件表，也就是
        /// “`dest.GetComponent(type)` 不再可信”的那一刻。真机日志里它一次烘焙出现 32 条，
        /// 全部来自我们的 `ApplyModifiedPropertiesWithoutUndo`。
        /// </summary>
        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            if (string.IsNullOrEmpty(condition)
                || condition.IndexOf(UnityConsistencyMarker, StringComparison.Ordinal) < 0)
            {
                return;
            }

            _unityConsistencyAnomaly = true;
            if (_unityConsistencyMessages.Count < 64)
            {
                _unityConsistencyMessages.Add(condition);
            }
        }

        /// <summary>
        /// F8：创建一个**自带独立物理世界**的临时场景，并在创建**任何 GameObject 之前**完成证明。
        ///
        /// 为什么是“先证明再动手”（这是第三轮最重要的设计决定）：
        ///   · 官方 LocalPhysicsMode 文档原文：默认创建的/加载的场景，其内任何 2D/3D 物理组件都加进
        ///     “default physics Scene”。旧码的临时场景（`EditorSceneManager.NewScene`）就属于
        ///     “默认”一类 ⇒ 临时 Collider 与源场景 Collider 共用一张登记表 ⇒ 第二轮 SIGSEGV。
        ///   · 能给出“场景自有 3D 物理场景”的只有 `SceneManager.CreateScene` +
        ///     `CreateSceneParameters(LocalPhysicsMode.Physics3D)`（已核真实程序集元数据），
        ///     但 2019.4 文档把它归为**运行时**用途（编辑期应改用 `EditorSceneManager.NewScene`）
        ///     ⇒ 编辑模式合法性**无文档保证**；
        ///   · `EditorSceneManager.NewPreviewScene()` 编辑期必然合法，但文档只说“只在该场景里渲染”，
        ///     **未提物理**，也不接受 LocalPhysicsMode ⇒ 也无法从文档推出隔离。
        ///   因此两个候选都试，并用 `Scene.GetPhysicsScene()` vs `Physics.defaultPhysicsScene` 在
        ///   运行期**硬证明**隔离；两个都证不出来就**中止且零副作用**（宁可不烘焙，不再撞一次 SIGSEGV）。
        ///
        /// 证明项（全部通过才算成功）：
        ///   1) 场景 valid + isLoaded；
        ///   2) `scene.GetPhysicsScene().IsValid()`；
        ///   3) `scene.GetPhysicsScene() != Physics.defaultPhysicsScene`。
        /// 不做的事：不在此处创建任何 GameObject（否则就已经在共享世界上添碰撞体了）。
        /// </summary>
        private static bool TryCreateIsolatedScratchScene(StringBuilder summary, out Scene scene,
                                                         out ScratchSceneKind kind, out string error)
        {
            scene = default(Scene);
            kind = ScratchSceneKind.None;
            error = null;
            _scratchIsolationDetail = null;

            List<string> attempts = new List<string>(2);

            // ---- 候选 (a)：文档唯一明确“场景自有 3D 物理场景”的 API（但归为运行时用途）
            Scene created = default(Scene);
            try
            {
                created = SceneManager.CreateScene(ScratchSceneName,
                                                   new CreateSceneParameters(LocalPhysicsMode.Physics3D));
                string detail = null;
                if (created.IsValid() && created.isLoaded && SceneOwnsSeparatePhysics(created, out detail))
                {
                    scene = created;
                    kind = ScratchSceneKind.LocalPhysics3D;
                    _scratchSceneIsPreview = false;
                    _scratchIsolationDetail = "(a) SceneManager.CreateScene(LocalPhysicsMode.Physics3D)：“"
                                               + detail + "”";
                    AdoptScratchScene(scene, summary);
                    return true;
                }

                attempts.Add("(a) SceneManager.CreateScene(LocalPhysicsMode.Physics3D) 不可用："
                             + (created.IsValid() ? detail : "返回了无效场景"));
            }
            catch (Exception ex)
            {
                attempts.Add("(a) SceneManager.CreateScene 抛异常（很可能就是“编辑期不允许运行时建场景”）："
                             + ex.GetType().Name + " " + ex.Message);
            }

            if (created.IsValid())
            {
                try
                {
                    EditorSceneManager.CloseScene(created, true);
                }
                catch (Exception)
                {
                    attempts.Add("(a) 清理失败尝试建立的场景时又抛异常（已忽略）");
                }
            }

            // ---- 候选 (b)：编辑器专用预览场景（编辑期必然合法，但物理隔离仍需运行期证明）
            Scene preview = default(Scene);
            try
            {
                preview = EditorSceneManager.NewPreviewScene();
                string detail = null;
                if (preview.IsValid() && preview.isLoaded && SceneOwnsSeparatePhysics(preview, out detail))
                {
                    scene = preview;
                    kind = ScratchSceneKind.PreviewScene;
                    _scratchSceneIsPreview = true;
                    _scratchIsolationDetail = "(b) EditorSceneManager.NewPreviewScene()：“" + detail + "”";
                    _scratchScene = scene;
                    if (summary != null)
                    {
                        summary.AppendLine("  临时场景隔离证据：" + _scratchIsolationDetail
                                           + "；预览场景不能设为活动场景，因此每个自建对象都显式"
                                           + "`SceneManager.MoveGameObjectToScene` 搬进来。");
                    }

                    return true;
                }

                attempts.Add("(b) EditorSceneManager.NewPreviewScene() 不能证明物理隔离："
                             + (preview.IsValid() ? detail : "返回了无效场景"));
            }
            catch (Exception ex)
            {
                attempts.Add("(b) EditorSceneManager.NewPreviewScene 抛异常："
                             + ex.GetType().Name + " " + ex.Message);
            }

            if (preview.IsValid())
            {
                try
                {
                    EditorSceneManager.ClosePreviewScene(preview);
                }
                catch (Exception)
                {
                    attempts.Add("(b) 清理预览场景时又抛异常（已忽略）");
                }
            }

            error = "两个候选都无法证明“自带独立 3D 物理世界”：（1）" + attempts[0]
                    + "；（2）" + (attempts.Count > 1 ? attempts[1] : "（未尝试）")
                    + "。按官方 LocalPhysicsMode 文档，没有自建物理场景的场景其物理组件都会加进默认物理场景"
                    + "——那正是第二轮 SIGSEGV 的机制，因此本函数**拒绝**在共享世界上建临时层级、直接让烘焙失败"
                    + "（零副作用），而不是用猜测方案再跑一次。需要用户在 Unity 内确认的最小动作见"
                    + "Docs/plans/_r4c_round3_report.md（只读诊断菜单 Build/Diagnose PMNet Battle Content）。";
            return false;
        }

        /// <summary>候选 (a) 成功后的善后：尽量把临时场景设为活动场景（失败不影响正确性，因为对象会被显式搬过去）。</summary>
        private static void AdoptScratchScene(Scene scene, StringBuilder summary)
        {
            _scratchScene = scene;

            bool activated = false;
            try
            {
                activated = SceneManager.SetActiveScene(scene);
            }
            catch (Exception ex)
            {
                if (summary != null)
                {
                    summary.AppendLine("  注意：临时场景无法设为活动场景（" + ex.GetType().Name + " "
                                       + ex.Message + "）：改由逐个 `MoveGameObjectToScene` 保证对象落点。");
                }
            }

            if (summary != null)
            {
                summary.AppendLine("  临时场景隔离证据：" + _scratchIsolationDetail
                                   + "；设为活动场景=" + (activated ? "是" : "否（已改用逐个 MoveGameObjectToScene）"));
            }
        }

        /// <summary>
        /// F8 的核心证明：这个场景是不是**自己拥有**一个 3D 物理场景（而不是挂在默认物理世界上）。
        ///
        /// `Scene.GetPhysicsScene()` 是 `UnityEngine.PhysicsSceneExtensions` 的扩展方法
        /// （已核 UnityEngine.PhysicsModule.dll 元数据）；`Physics.defaultPhysicsScene` 也在同一程序集。
        /// </summary>
        private static bool SceneOwnsSeparatePhysics(Scene scene, out string detail)
        {
            detail = null;

            try
            {
                PhysicsScene scenePhysics = scene.GetPhysicsScene();
                PhysicsScene defaultPhysics = Physics.defaultPhysicsScene;

                if (!scenePhysics.IsValid())
                {
                    detail = "scene.GetPhysicsScene().IsValid()=false（该场景根本没有自己的 3D 物理场景）";
                    return false;
                }

                if (scenePhysics.Equals(defaultPhysics))
                {
                    detail = "scene.GetPhysicsScene() == Physics.defaultPhysicsScene"
                             + "（与源场景共用默认物理世界 —— 正是第二轮 SIGSEGV 的机制）";
                    return false;
                }

                detail = "scene.GetPhysicsScene() 有效且 != Physics.defaultPhysicsScene";
                return true;
            }
            catch (Exception ex)
            {
                detail = "探测 PhysicsScene 时抛异常：" + ex.GetType().Name + " " + ex.Message;
                return false;
            }
        }

        /// <summary>F8：关闭临时场景（按创建方式选对 API），并清掉烘焙期静态状态。</summary>
        private static void CloseScratchScene(Scene scene, ScratchSceneKind kind, StringBuilder summary)
        {
            Step("finally", "关闭临时场景（kind=" + kind + "，name=" + scene.name + "）");

            try
            {
                if (kind == ScratchSceneKind.PreviewScene || _scratchSceneIsPreview)
                {
                    EditorSceneManager.ClosePreviewScene(scene);
                }
                else
                {
                    // removeScene=true：丢弃临时场景，不提示保存、不污染用户工程。
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
            catch (Exception ex)
            {
                if (summary != null)
                {
                    summary.AppendLine("  注意：关闭临时场景失败（" + ex.GetType().Name + " " + ex.Message
                                       + "）：临时场景可能留在编辑器里（不是崩溃，但请手动关掉）。");
                }
            }
            finally
            {
                _scratchScene = default(Scene);
                _scratchSceneIsPreview = false;
            }
        }

        /// <summary>
        /// F8：建一个自建对象，并**显式搬进**临时场景。
        ///
        /// 不能只靠“设活动场景”：预览场景根本不能当活动场景，而 `new GameObject()` 永远落在活动场景
        /// ——只靠 SetActiveScene 就会把对象建到用户场景里（污染 + 崩溃面）。因此这里强制搬迁。
        /// </summary>
        private static GameObject NewScratchObject(string name)
        {
            GameObject go = new GameObject(name);
            AdoptIntoScratchScene(go);
            return go;
        }

        /// <summary>把对象搬进临时场景（已在其中就不动）。</summary>
        private static void AdoptIntoScratchScene(GameObject go)
        {
            if (go == null || !_scratchScene.IsValid() || !_scratchScene.isLoaded)
            {
                return;
            }

            if (go.scene == _scratchScene)
            {
                return;
            }

            SceneManager.MoveGameObjectToScene(go, _scratchScene);
        }

        // ==================================================================== F10：目标对象诊断

        /// <summary>F10：登记一个自建目标对象（单调序号 + instanceID → 父路径 / 所属场景）。</summary>
        private static void NoteDestCreated(GameObject go)
        {
            if (go == null)
            {
                return;
            }

            _destSequence++;
            _destRegistry[go.GetInstanceID()] =
                "seq=" + _destSequence.ToString(CultureInfo.InvariantCulture)
                + " instanceID=" + go.GetInstanceID().ToString(CultureInfo.InvariantCulture)
                + " scene=" + (go.scene.IsValid() ? go.scene.name : "<invalid>")
                + " path=" + PathOf(go);
        }

        /// <summary>F10：取目标对象的登记行（没登记过就说明不是我们建的）。</summary>
        private static string DestRegistryLine(GameObject go)
        {
            if (go == null)
            {
                return "<null>";
            }

            string line;
            if (_destRegistry.TryGetValue(go.GetInstanceID(), out line))
            {
                return line;
            }

            return "<未登记（不是本次烘焙自建的对象？）> instanceID="
                   + go.GetInstanceID().ToString(CultureInfo.InvariantCulture)
                   + " path=" + PathOf(go);
        }

        /// <summary>
        /// F10：把对象的**完整组件清单**打成一行，**并且逐个组件指出它自己声称属于哪个 GameObject**。
        ///
        /// 为什么要指 owner：`CheckConsistency: GameObject does not reference component X` 的语义就是
        /// “有个组件自称属于它，但它的组件表里没有” —— 如果这里打出 `owner=别的对象`，
        /// 就**直接证明**这是 Unity 侧组件登记错乱（而不是“模板里有重复组件”）。
        /// </summary>
        private static string DescribeComponentInventory(GameObject go)
        {
            if (go == null)
            {
                return "<null>";
            }

            Component[] components = go.GetComponents<Component>();
            StringBuilder sb = new StringBuilder(256);
            sb.Append("组件数=").Append(components.Length.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < components.Length; i++)
            {
                Component c = components[i];
                if (c == null)
                {
                    sb.Append(" [").Append(i.ToString(CultureInfo.InvariantCulture)).Append("]=<null>");
                    continue;
                }

                GameObject owner = null;
                try
                {
                    owner = c.gameObject;
                }
                catch (Exception)
                {
                    // 拿不到 owner（组件已经坏了）：如实标出来。
                }

                sb.Append(" [").Append(i.ToString(CultureInfo.InvariantCulture)).Append("]=")
                  .Append(c.GetType().Name).Append('#').Append(c.GetInstanceID().ToString(CultureInfo.InvariantCulture));

                if (owner == null)
                {
                    sb.Append("(owner=<null>)");
                }
                else if (owner == go)
                {
                    sb.Append("(owner=本对象)");
                }
                else
                {
                    sb.Append("(owner=").Append(PathOf(owner)).Append(" ← 不属于本对象！)");
                }
            }

            return sb.ToString();
        }

        /// <summary>F10：源物体组件类型序列 + 每类型个数（用于证明“模板每个类型只出现一次”）。</summary>
        private static string DescribeSourceComponents(Component[] components)
        {
            if (components == null)
            {
                return "<null>";
            }

            StringBuilder sequence = new StringBuilder(128);
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < components.Length; i++)
            {
                Component c = components[i];
                string name = c == null ? "<missing-script>" : c.GetType().Name;
                if (i > 0)
                {
                    sequence.Append(',');
                }

                sequence.Append(name);

                int count;
                counts.TryGetValue(name, out count);
                counts[name] = count + 1;
            }

            return "个数=" + components.Length.ToString(CultureInfo.InvariantCulture)
                   + " 序列=[" + sequence + "] 按类型=" + DescribeCounts(counts);
        }

        /// <summary>写一个文本文件（目录自动补；失败返回 false 而不抛）。</summary>
        private static bool TryWriteTextFile(string relativePath, string text, out string error)
        {
            error = null;

            try
            {
                string absolute = AbsolutePath(relativePath);
                string directory = Path.GetDirectoryName(absolute);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(absolute, text ?? string.Empty, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + " " + ex.Message;
                return false;
            }
        }

        // ==================================================================== 自建对象释放（F5）

        /// <summary>
        /// 销毁自建 root：**先 <c>SetActive(false)</c>**（让 Deactivate 正常跑一遍，注销 renderer/collider
        /// 在 transform 上注册的 change/hierarchy interests），再 <c>DestroyImmediate</c>。
        ///
        /// 真机证据（Editor.log）：直接销毁这类层级时 Unity 报
        /// <c>Transform has '...' change interests present when destroying the hierarchy.
        /// Interests must be deregistered in Deactivate.</c>，随后关源场景时报
        /// <c>Cannot unregister Collider from transform change interested as it was not in the table.</c>
        /// 并 native 崩溃。本方法把"先注销再销毁"变成代码里固定的一步。
        /// </summary>
        private static void DestroySelfBuiltRoot(GameObject root, string label, StringBuilder summary)
        {
            if (root == null)
            {
                return;
            }

            Step("cleanup", "销毁" + label + "：\"" + root.name + "\"（先 SetActive(false) 注销 interests）");

            try
            {
                if (root.activeSelf)
                {
                    root.SetActive(false);
                }
            }
            catch (Exception ex)
            {
                if (summary != null)
                {
                    summary.AppendLine("  注意：销毁前 SetActive(false) 失败（" + label + "）："
                                       + ex.GetType().Name + " " + ex.Message);
                }
            }

            UnityEngine.Object.DestroyImmediate(root);
        }

        /// <summary>
        /// 关临时场景之前的硬门：临时场景是本次烘焙新建的空场景，里面只可能是我们建的东西，
        /// 因此"还有 root 残留"就说明清理漏了 —— 残留对象带着未注销的 interests 被关场景拆掉，
        /// 正是崩溃路径。这里先报告、再按同一纪律销毁，最后才允许关场景。
        /// </summary>
        private static void EnsureScratchSceneEmpty(Scene scratchScene, StringBuilder summary)
        {
            if (!scratchScene.IsValid() || !scratchScene.isLoaded)
            {
                return;
            }

            GameObject[] leftovers = scratchScene.GetRootGameObjects();
            if (leftovers == null || leftovers.Length == 0)
            {
                Step("cleanup", "关场景前检查：临时场景已无自建对象");
                return;
            }

            List<string> names = new List<string>();
            for (int i = 0; i < leftovers.Length; i++)
            {
                GameObject go = leftovers[i];
                if (go == null)
                {
                    continue;
                }

                names.Add(go.name);
                DestroySelfBuiltRoot(go, "临时场景残留 root", summary);
            }

            if (summary != null)
            {
                summary.AppendLine("  **注意**：关临时场景前发现 " + names.Count.ToString(CultureInfo.InvariantCulture)
                                   + " 个残留 root（已按 SetActive(false)→DestroyImmediate 销毁）：" + Join(names));
            }
        }

        // ==================================================================== 只读校验（构建 hook）

        /// <summary>
        /// 只读校验既有产物：**不写任何资源、不打开源场景、不烘焙**。
        ///
        /// 校验面（= 运行期 C2 会做的同一批约束，提前在构建期挡住）：
        ///   1) 三个产物存在且可加载；
        ///   2) manifest 能被 C2 的 TryParseJson 解析并**强校验通过**
        ///      （版本/固定值/资源键白名单/digest 格式/派生值自洽）；
        ///   3) 地图 prefab：根激活、无 missing script、组件全在 C2 允许集内、
        ///      Collider 全部 enabled 且所在物体 active、至少 1 个非 trigger Collider、
       ///      硬障碍数量有界；
        ///   4) 角色 prefab：根激活、无 missing script、无 MonoBehaviour/Collider/Rigidbody、
        ///      组件全在 C2 允许集内、Animator 关闭 root motion 且有 Float 型 "Speed" 参数。
        ///
        /// 不做的事（诚实边界）：不重算"编辑器侧内容指纹"（源场景可能不在磁盘上/已变更），
        /// 因此这里只证 **资源与 manifest 内部一致 + 组件约束**；
        /// "两端同一份内容"由 C3 的同构建 + digest 握手守护。
        /// </summary>
        public static bool ValidateContent()
        {
            StringBuilder summary = new StringBuilder();
            summary.AppendLine("[PMBattleContentBuild] 校验正式内容（只读，不生成、不修改资源）");

            bool ok = false;
            try
            {
                ok = ValidateContentInternal(summary);
            }
            catch (Exception ex)
            {
                summary.AppendLine("  异常：" + ex.GetType().Name + " " + ex.Message);
                ok = false;
            }

            summary.AppendLine(ok ? "  结果：通过" : "  结果：失败");
            if (ok)
            {
                Debug.Log(summary.ToString());
            }
            else
            {
                Debug.LogError(summary.ToString());
            }

            return ok;
        }

        private static bool ValidateContentInternal(StringBuilder summary)
        {
            // ---- 1) 产物存在性
            GameObject mapPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(MapPrefabPath);
            if (mapPrefab == null)
            {
                summary.AppendLine("  缺少地图 prefab：" + MapPrefabPath
                                   + "（先执行菜单 Build/Prepare PMNet Battle Content）");
                return false;
            }

            GameObject playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            if (playerPrefab == null)
            {
                summary.AppendLine("  缺少角色 prefab：" + PlayerPrefabPath
                                   + "（先执行菜单 Build/Prepare PMNet Battle Content）");
                return false;
            }

            TextAsset manifestAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(ManifestAssetPath);
            if (manifestAsset == null)
            {
                summary.AppendLine("  缺少 manifest：" + ManifestAssetPath
                                   + "（manifest 是唯一的内容已就绪发布信号，缺它就等于没就绪）");
                return false;
            }

            // ---- 2) manifest 强校验（复用 C2 的规则，绝不各写一份）
            PMBattleContentManifest manifest;
            string manifestError;
            if (!PMBattleContentManifest.TryParseJson(manifestAsset.text, out manifest, out manifestError))
            {
                summary.AppendLine("  manifest 校验失败：" + manifestError);
                return false;
            }

            summary.AppendLine("  " + manifest.Describe());

            // 资源键必须与实际文件一一对应（manifest 说 key，磁盘就得有那个 key）。
            if (!string.Equals(manifest.mapResource, PMBattleContentManifest.MapResourceKey,
                               StringComparison.Ordinal)
                || !string.Equals(manifest.playerResource, PMBattleContentManifest.PlayerResourceKey,
                                  StringComparison.Ordinal))
            {
                summary.AppendLine("  manifest 资源键与冻结常量不一致（应当已被 C2 强校验挡住）。");
                return false;
            }

            // ---- 3) 地图 prefab 结构与组件约束
            string error;
            if (!VerifyMapPrefabLoaded(mapPrefab, summary, out error))
            {
                summary.AppendLine("  地图 prefab 校验失败：" + error);
                return false;
            }

            // ---- 4) 角色 prefab 结构与组件约束
            if (!VerifyPlayerPrefabLoaded(playerPrefab, summary, out error))
            {
                summary.AppendLine("  角色 prefab 校验失败：" + error);
                return false;
            }

            return true;
        }

        // ==================================================================== 源场景读取（安全预览）

        /// <summary>预检最多解析多少个脚本 guid（场景里组件上千，但这里只关心脚本引用）。</summary>
        private const int PreflightMaxScriptReferences = 512;

        /// <summary>场景 YAML 里脚本引用的前缀（ForceText 序列化下 MonoBehaviour 的 m_Script）。</summary>
        private const string PreflightScriptReferenceMarker = "m_Script: {fileID: 11500000, guid: ";

        /// <summary>
        /// **打开源场景之前**的脚本预检：从场景文本取所有 `m_Script` 的 guid，只对**工程内**
        /// （`Assets/**.cs`）的脚本解析类型并检查 `[ExecuteInEditMode]`/`[ExecuteAlways]`。
        /// 违规时直接失败 ⇒ **根本不会 OpenScene**，于是"审计发生在旧脚本 Awake 之前"是真的，
        /// 而不是"打开之后才发现它已经跑过了"（这正是契约允许的"另一经验证不会执行旧游戏 Awake
        /// 的编辑模式方案"里"先验证后执行"的那一半）。
        ///
        /// 边界（诚实说明）：
        ///   · 只对工程内 `.cs` 下结论；包/内置包脚本与 Assets 下的非 `.cs` 资产只计数，
        ///     由打开后的运行时审计按来源分桶做权威判定；
        ///   · 若场景不是 ForceText 序列化（读不到脚本引用），预检**不假装成功**：明说不可用，
        ///     由打开后的运行时审计兜底；
        ///   · 解析不到 guid 的脚本引用（脚本已删除 ⇒ missing script）不可能执行任何东西，只计数。
        /// </summary>
        private static bool PreflightProjectEditModeAudit(string assetPath, StringBuilder summary,
                                                          out string error)
        {
            error = null;

            string absolute = AbsolutePath(assetPath);
            string text;
            try
            {
                if (!File.Exists(absolute))
                {
                    error = "源场景文件不存在：" + assetPath + "（磁盘路径 " + absolute + "）";
                    return false;
                }

                text = File.ReadAllText(absolute, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                summary.AppendLine("  预检跳过（读取源场景文本失败：" + ex.GetType().Name
                                   + "）：改由打开场景后的运行时审计把关。");
                return true;
            }

            List<string> guids = new List<string>();
            int cursor = 0;
            while (cursor < text.Length && guids.Count < PreflightMaxScriptReferences)
            {
                int hit = text.IndexOf(PreflightScriptReferenceMarker, cursor, StringComparison.Ordinal);
                if (hit < 0)
                {
                    break;
                }

                int start = hit + PreflightScriptReferenceMarker.Length;
                int end = text.IndexOf('}', start);
                if (end < 0)
                {
                    break;
                }

                string guid = text.Substring(start, end - start).Trim();
                if (IsHex32(guid))
                {
                    AddOnce(guids, guid);
                }

                cursor = end;
            }

            if (guids.Count == 0)
            {
                summary.AppendLine("  预检（打开场景之前）：未从场景文本读到脚本引用（可能不是 ForceText"
                                   + " 序列化，或场景里没有 MonoBehaviour）：打开前的脚本门不可用，"
                                   + "改由打开后的运行时审计把关。");
                return true;
            }

            List<string> offenders = new List<string>();
            List<string> projectFlagged = new List<string>();
            int projectReferences = 0;
            int packageReferences = 0;
            int undecidableReferences = 0;
            int unresolvedReferences = 0;

            for (int i = 0; i < guids.Count; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(path))
                {
                    unresolvedReferences++;     // 脚本已删除 ⇒ missing script，不可能执行任何东西
                    continue;
                }

                if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                {
                    packageReferences++;        // 包/内置包脚本：交给运行时审计按来源分桶
                    continue;
                }

                if (!path.EndsWith(".cs", StringComparison.Ordinal))
                {
                    undecidableReferences++;    // Assets 下的 dll 之类：预检不下结论
                    continue;
                }

                projectReferences++;

                MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                Type type = script == null ? null : script.GetClass();
                if (type == null)
                {
                    offenders.Add(path + "（工程内脚本无法解析为类型：未编译？）");
                    continue;
                }

                bool editMode = Attribute.IsDefined(type, typeof(ExecuteInEditMode))
                                || Attribute.IsDefined(type, typeof(ExecuteAlways));
                if (!editMode)
                {
                    continue;
                }

                if (!Contains(EditModeToleratedScriptNames, type.Name))
                {
                    offenders.Add(type.Name + " <- " + path);
                    continue;
                }

                AddOnce(projectFlagged, type.Name);
            }

            if (offenders.Count > 0)
            {
                error = "**打开源场景之前**的脚本预检失败：工程内脚本带编辑模式标记且不在容忍白名单："
                        + Join(offenders)
                        + "。加载这张场景会让它们的 Awake/OnEnable 在编辑期执行（可能带旧玩法副作用）。"
                        + "请先确认这些脚本的编辑模式行为，再决定是否加入 EditModeToleratedScriptNames；"
                        + "本文件不会在未确认的情况下打开场景。";
                return false;
            }

            summary.AppendLine("  预检（**打开场景之前**）：脚本引用="
                               + guids.Count.ToString(CultureInfo.InvariantCulture)
                               + "（工程内 .cs=" + projectReferences.ToString(CultureInfo.InvariantCulture)
                               + "，包/内置包=" + packageReferences.ToString(CultureInfo.InvariantCulture)
                               + "，工程内非 .cs=" + undecidableReferences.ToString(CultureInfo.InvariantCulture)
                               + "，未解析/missing script="
                               + unresolvedReferences.ToString(CultureInfo.InvariantCulture)
                               + "）；工程内带编辑模式标记且命中白名单={"
                               + (projectFlagged.Count == 0 ? "空" : Join(projectFlagged))
                               + "}（白名单={" + Join(new List<string>(EditModeToleratedScriptNames)) + "}）");
            return true;
        }

        /// <summary>是否是 32 位十六进制的 guid（避免把 YAML 里的其它 token 当成 guid）。</summary>
        private static bool IsHex32(string value)
        {
            if (value == null || value.Length != 32)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 源场景的读取作用域：Additive 打开（或用已打开的那一份）→ 审计编辑模式脚本 →
        /// Dispose 时关闭自己打开的那一份。
        ///
        /// 三条不许破的边界：
        ///   · 只 Close **自己打开的**场景（用户本来开着的场景绝不动，其脏状态因此不受影响）；
        ///   · 不 Save（源场景一个字节都不写）；
        ///   · activeScene / Selection 由调用方 finally 还原（这里只负责开/关）。
        /// </summary>
        private sealed class SourceSceneScope : IDisposable
        {
            private readonly Scene _scene;
            private readonly bool _openedByUs;
            private bool _disposed;

            private SourceSceneScope(Scene scene, bool openedByUs)
            {
                _scene = scene;
                _openedByUs = openedByUs;
            }

            public Scene Scene { get { return _scene; } }

            public static SourceSceneScope Open(string assetPath, StringBuilder summary, out string error)
            {
                error = null;

                Scene existing = SceneManager.GetSceneByPath(assetPath);
                if (existing.IsValid() && existing.isLoaded)
                {
                    // 用户已经开着这张场景：直接用，不重复打开（重复 Additive 打开会报错）。
                    summary.AppendLine("  源场景已打开，直接读取（不重复加载、不关闭、不保存）");

                    // 脏场景必须拒绝：此时磁盘 SHA256 代表不了内存里被读取到的内容，用它算 digest
                    // 会产出"不可追溯"的指纹。这条检查原先只加在"由我们打开"的分支上 —— 恰好加在
                    // 不会失败的一侧（漏掉了最可能命中它的路径），故补到这里。
                    if (existing.isDirty)
                    {
                        error = "源场景当前已打开且有**未保存改动**：磁盘 SHA256 无法代表实际被读取的内容，"
                                + "用它算出的 digest 不可追溯。请先保存（Ctrl+S）或关闭该场景，再重新执行。";
                        return null;
                    }

                    if (!AuditEditModeScripts(existing, summary, out error))
                    {
                        return null;
                    }

                    return new SourceSceneScope(existing, false);
                }

                // 打开之前的脚本预检：违规时**根本不打开场景**（避免其编辑模式回调先跑起来）。
                if (!PreflightProjectEditModeAudit(assetPath, summary, out error))
                {
                    return null;
                }

                Scene opened;
                try
                {
                    opened = EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Additive);
                }
                catch (Exception ex)
                {
                    error = "OpenScene 异常：" + ex.GetType().Name + " " + ex.Message;
                    return null;
                }

                if (!opened.IsValid() || !opened.isLoaded)
                {
                    error = "OpenScene 返回了无效场景（路径=" + assetPath + "）";
                    return null;
                }

                summary.AppendLine("  已 Additive 打开源场景用于只读预览：" + assetPath);

                if (!AuditEditModeScripts(opened, summary, out error))
                {
                    EditorSceneManager.CloseScene(opened, true);
                    return null;
                }

                if (opened.isDirty)
                {
                    // 不该发生（刚打开的编辑期场景不会是脏的），但一旦发生说明"磁盘 SHA"不能代表内容。
                    error = "刚打开的源场景处于脏状态：磁盘 SHA 无法代表实际内容，拒绝用不一致的指纹烘焙。";
                    EditorSceneManager.CloseScene(opened, true);
                    return null;
                }

                return new SourceSceneScope(opened, true);
            }

            /// <summary>
            /// 编辑模式脚本审计：源场景里带 `[ExecuteInEditMode]`/`[ExecuteAlways]` 的组件按
            /// **脚本来源**分桶：
            ///   · 工程内（`Assets/**`）：必须命中容忍白名单，否则**显式失败**；
            ///   · 包 / 引擎自带（`Packages/**`、`Library/PackageCache/**`、`UnityEngine.*` 程序集）：
            ///     记入摘要但不判失败 —— 它们是 Unity 自带组件的编辑模式行为（ugui 的 `Image`/
            ///     `Text`/`CanvasScaler`/`Button`/`Slider` 等），不执行玩法逻辑；
            ///   · 来源无法判定：fail closed。
            ///
            /// 为何不能只看"是否带编辑模式标记"：源场景里有 32×`Image` / 14×`Text` /
            /// 4×`CanvasScaler` / 3×`Slider` / 2×`Button`，而 ugui 的 `Graphic`/`CanvasScaler`/
            /// `Selectable` 带 `[ExecuteAlways]`（`Inherited=true`）⇒ 旧实现会把它们全当违规项，
            /// 源场景永远过不了审计 ⇒ 烘焙恒失败。
            /// </summary>
            private static bool AuditEditModeScripts(Scene scene, StringBuilder summary, out string error)
            {
                error = null;

                List<string> projectFound = new List<string>();
                List<string> packageFound = new List<string>();
                List<string> offenders = new List<string>();
                int inspected = 0;

                GameObject[] roots = scene.GetRootGameObjects();
                for (int i = 0; i < roots.Length; i++)
                {
                    Component[] components = roots[i].GetComponentsInChildren<Component>(true);
                    for (int c = 0; c < components.Length; c++)
                    {
                        Component component = components[c];
                        if (component == null)
                        {
                            continue;   // missing script：本场景读取不依赖它们
                        }

                        inspected++;

                        Type type = component.GetType();
                        bool editMode = Attribute.IsDefined(type, typeof(ExecuteInEditMode))
                                        || Attribute.IsDefined(type, typeof(ExecuteAlways));
                        if (!editMode)
                        {
                            continue;
                        }

                        string detail;
                        EditModeScriptOrigin origin = ClassifyEditModeScriptOrigin(type, component, out detail);
                        string describe = type.Name + "(" + type.Namespace + ") <- " + detail;

                        if (origin == EditModeScriptOrigin.PackageOrEngine)
                        {
                            AddOnce(packageFound, type.Name);
                            continue;
                        }

                        if (origin == EditModeScriptOrigin.Project)
                        {
                            if (!Contains(EditModeToleratedScriptNames, type.Name))
                            {
                                offenders.Add(describe);
                                continue;
                            }

                            AddOnce(projectFound, type.Name);
                            continue;
                        }

                        offenders.Add(describe + " [来源无法判定：既不是工程内脚本，也不是 Unity 包/引擎程序集]");
                    }
                }

                if (offenders.Count > 0)
                {
                    error = "源场景含**未审计**的编辑模式脚本：" + Join(offenders)
                            + "。这会违反「加载源场景不执行旧游戏 Awake」的前提 —— "
                            + "请人工确认后再把它加入容忍白名单（工程内脚本，见 EditModeToleratedScriptNames）。";
                    return false;
                }

                summary.AppendLine("  编辑模式脚本审计（打开后运行时复核）：检查组件 "
                                   + inspected.ToString(CultureInfo.InvariantCulture) + " 个；"
                                   + "工程内带编辑模式标记 = {"
                                   + (projectFound.Count == 0 ? "空" : Join(projectFound)) + "}"
                                   + "（白名单 = {" + Join(new List<string>(EditModeToleratedScriptNames)) + "}）；"
                                   + "Unity 包/引擎自带 = {"
                                   + (packageFound.Count == 0 ? "空" : Join(packageFound)) + "}"
                                   + "（Unity 自带组件的编辑模式行为，不执行玩法逻辑；本文件从不 Save 场景）；"
                                   + "旧玩法脚本无编辑模式标记 ⇒ 不会执行其 Awake/OnEnable");
                return true;
            }

            /// <summary>全局随机态是否被改动（"不用全局 Random"的运行时证据，进摘要）。</summary>
            public void AppendRandomStateNote(StringBuilder summary, UnityEngine.Random.State before)
            {
                bool changed = !before.Equals(UnityEngine.Random.state);
                summary.AppendLine("  UnityEngine.Random.state 未被改动=" + (changed ? "否（异常，请核查）" : "是"));
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                if (_openedByUs && _scene.IsValid() && _scene.isLoaded)
                {
                    EditorSceneManager.CloseScene(_scene, true);
                }
            }
        }

        // ==================================================================== 布局（确定性复刻）

        /// <summary>一个实例化落点（= 源码循环里的每次 MyInstantiate 调用）。</summary>
        private sealed class Placement
        {
            public string Category;      // "floor"/"wall"/"obstacle"/"grass"/"tree"
            public int TemplateIndex;    // 在对应模板数组里的下标
            public float LocalX;
            public float LocalY;
            public float LocalZ;
            public float RotX;
            public float RotY;
            public float RotZ;
            public float RotW;

            public string CanonicalLine(int ordinal)
            {
                return "placement[" + ordinal.ToString(CultureInfo.InvariantCulture) + "]"
                       + " cat=" + Category
                       + " idx=" + TemplateIndex.ToString(CultureInfo.InvariantCulture)
                       + " pos=(" + Num(LocalX) + "," + Num(LocalY) + "," + Num(LocalZ) + ")"
                       + " rot=(" + Num(RotX) + "," + Num(RotY) + "," + Num(RotZ) + "," + Num(RotW) + ")";
            }
        }

        /// <summary>
        /// 局部确定性 PRNG（xorshift32）。
        ///
        /// 为什么不用 UnityEngine.Random：契约要求"不用全局随机"（旧链地图不可复现的根因就是
        /// 全局未播种随机）；也不允许改全局态。这里用自带状态，种子固定 0x52444301。
        /// 语义对齐 Unity 的整数重载：<c>Range(min, max)</c> 取 [min, max)，max&lt;=min 返回 min。
        /// </summary>
        private struct DeterministicRng
        {
            private uint _state;

            public DeterministicRng(int seed)
            {
                // xorshift 状态不能为 0；契约种子非 0，这里再兜一层。
                _state = seed == 0 ? 0x9E3779B9u : unchecked((uint)seed);
            }

            public int Range(int minInclusive, int maxExclusive)
            {
                if (maxExclusive <= minInclusive)
                {
                    return minInclusive;
                }

                uint span = unchecked((uint)(maxExclusive - minInclusive));
                return minInclusive + (int)(NextUInt() % span);
            }

            private uint NextUInt()
            {
                uint x = _state;
                x ^= x << 13;
                x ^= x >> 17;
                x ^= x << 5;
                _state = x;
                return x;
            }
        }

        /// <summary>
        /// 按 <c>ScenseBuildLogic.InitData()</c> 的循环结构与随机数消费顺序复刻布局。
        ///
        /// 与源码逐处对齐的地方（读代码时最容易改坏的三处）：
        ///   · `maps[i,j]==1`（草丛）分支里实例化被注释掉，但 `Random.Range(0,Grasses.Length)`
        ///     **仍然被消费一次** —— 必须照消费，否则后续所有落点整体错位；
        ///   · 墙壁 `if (i % 3 == 0) random = walll - 1`（C# 负数取模：只有 -15..15 的 3 倍数命中）；
        ///   · 树的两处循环边界不同：i 循环用 `-mapx/2-1`，j 循环用 `-mapx/2`（源码原样，不改）。
        /// </summary>
        private static bool BuildLayout(ScenseBuildLogic logic, out List<Placement> placements,
                                        out string error)
        {
            placements = null;
            error = null;

            int mapx = logic.mapx;
            int mapy = logic.mapy;

            if (logic.map2 == null)
            {
                error = "ScenseBuildLogic.map2 为 null（源类字段初始化失败）";
                return false;
            }

            int rows = logic.map2.GetLength(0);
            int cols = logic.map2.GetLength(1);
            if (rows < mapx || cols < mapy)
            {
                error = "map2 维度 " + rows.ToString(CultureInfo.InvariantCulture) + "×"
                        + cols.ToString(CultureInfo.InvariantCulture) + " 小于 mapx/mapy "
                        + mapx.ToString(CultureInfo.InvariantCulture) + "×"
                        + mapy.ToString(CultureInfo.InvariantCulture)
                        + "（契约要求维持真实的 33×21 循环范围，不擅自扩大）";
                return false;
            }

            if (logic.floors == null || logic.floors.Length == 0
                || logic.walls == null || logic.walls.Length < 2
                || logic.obstacles == null || logic.obstacles.Length == 0
                || logic.Grasses == null
                || logic.trees == null || logic.trees.Length < 3)
            {
                error = "模板数组为空或长度不足以支撑源码的随机上界"
                        + "（floors>0 / walls>=2 / obstacles>0 / Grasses 非 null / trees>=3）";
                return false;
            }

            int floorCount = logic.floors.Length;
            int wallCount = logic.walls.Length;
            int obstacleCount = logic.obstacles.Length;
            int grassCount = logic.Grasses.Length;
            int treeCount = logic.trees.Length;

            for (int i = 0; i < logic.floors.Length; i++)
            {
                if (logic.floors[i] == null) { error = "floors[" + i + "] 为 null"; return false; }
            }
            for (int i = 0; i < logic.walls.Length; i++)
            {
                if (logic.walls[i] == null) { error = "walls[" + i + "] 为 null"; return false; }
            }
            for (int i = 0; i < logic.obstacles.Length; i++)
            {
                if (logic.obstacles[i] == null) { error = "obstacles[" + i + "] 为 null"; return false; }
            }
            for (int i = 0; i < logic.trees.Length; i++)
            {
                if (logic.trees[i] == null) { error = "trees[" + i + "] 为 null"; return false; }
            }

            DeterministicRng rng = new DeterministicRng(BakeSeed);
            List<Placement> list = new List<Placement>(8192);

            // ---- 1) 33×21 网格（含障碍 + 其下地板）
            for (int i = 0; i < mapx; i++)
            {
                for (int j = 0; j < mapy; j++)
                {
                    int cell = logic.map2[i, j];

                    if (cell == 0 || cell == 1)
                    {
                        int r = rng.Range(0, floorCount);
                        list.Add(Make("floor", r, i - mapx / 2, 0.01f, j - mapy / 2));
                    }

                    if (cell == 1)
                    {
                        // 源码：Grasses 的实例化被注释掉，但随机数照样消费一次（顺序不能省）。
                        rng.Range(0, grassCount);
                    }

                    if (cell == 2)
                    {
                        int r = rng.Range(0, obstacleCount);
                        list.Add(Make("obstacle", r, i - mapx / 2, 0f, j - mapy / 2));

                        int r2 = rng.Range(0, floorCount);
                        list.Add(Make("floor", r2, i - mapx / 2, 0.01f, j - mapy / 2));
                    }

                    if (cell == 5 || cell == 6)
                    {
                        // 金库格：map2 里不存在（实测 0 处）。RedSaveBox/BlueSaveBox 是**外部 prefab**，
                        // 其内部组件未经审计，直接实例化会把未知内容带进"纯地图"。
                        // 首图不含金库 ⇒ 显式失败，而不是静默丢掉这两个格子。
                        error = "map2 在第 (" + i.ToString(CultureInfo.InvariantCulture) + ","
                                + j.ToString(CultureInfo.InvariantCulture) + ") 格出现金库值 " + cell
                                + "：金库（RedSaveBox/BlueSaveBox）是外部 prefab，其组件未纳入本轮的"
                                + "纯几何审计，故 C1 明确失败而不是静默丢弃。";
                        return false;
                    }
                }
            }

            // ---- 2) 边界墙（四条边；源码按 i、j 两个循环，随机数各自消费）
            for (int i = -mapx / 2 - 1; i <= mapx / 2 + 1; i++)
            {
                int r = rng.Range(0, wallCount - 1);
                if (i % 3 == 0)
                {
                    r = wallCount - 1;
                }

                list.Add(Make("wall", r, i, 0f, -mapy / 2 - 1));
                list.Add(Make("wall", r, i, 0f, mapy / 2 + 1));
            }

            for (int j = -mapy / 2; j < mapy / 2 + 1; j++)
            {
                int r = rng.Range(0, wallCount - 1);
                list.Add(Make("wall", r, -mapx / 2 - 1, 0f, j));
                list.Add(Make("wall", r, mapx / 2 + 1, 0f, j));
            }

            // ---- 3) 外围树（两处循环边界与源码逐字一致）
            for (int i = -mapx / 2 - 1; i <= mapx / 2 + 1; i++)
            {
                int r = rng.Range(0, treeCount - 2);
                if (i % 3 == 0)
                {
                    r = treeCount - 2 + rng.Range(0, 2);
                }

                list.Add(Make("tree", r, i, 0f, -mapy / 2 - rng.Range(3, 10)));
                list.Add(Make("tree", r, i, 0f, mapy / 2 + rng.Range(3, 10)));
            }

            for (int j = -mapy / 2; j < mapy / 2 + 1; j++)
            {
                int r = rng.Range(0, treeCount - 2);
                list.Add(Make("tree", r, -mapx / 2 - rng.Range(3, 10), 0f, j));
                list.Add(Make("tree", r, mapx / 2 + rng.Range(3, 10), 0f, j));
            }

            // ---- 4) 边界外补地板
            for (int i = 0; i < mapx * 2; i++)
            {
                for (int j = 0; j < mapy * 2; j++)
                {
                    bool outside = i - mapx < -mapx / 2 - 1
                                   || i - mapx > mapx / 2 + 1
                                   || j - mapy < -mapy / 2 - 1
                                   || j - mapy > mapy / 2 + 1;
                    if (!outside)
                    {
                        continue;
                    }

                    int r = rng.Range(0, floorCount);
                    list.Add(Make("floor", r, i - mapx, 0.01f, j - mapy));
                }
            }

            placements = list;
            return true;
        }

        private static Placement Make(string category, int templateIndex, float x, float y, float z)
        {
            Placement p = new Placement();
            p.Category = category;
            p.TemplateIndex = templateIndex;
            p.LocalX = x;
            p.LocalY = y;
            p.LocalZ = z;
            p.RotX = 0f;
            p.RotY = 0f;
            p.RotZ = 0f;
            p.RotW = 1f;
            return p;
        }

        private static bool SamePlacements(List<Placement> a, List<Placement> b)
        {
            if (a == null || b == null || a.Count != b.Count)
            {
                return false;
            }

            for (int i = 0; i < a.Count; i++)
            {
                Placement x = a[i];
                Placement y = b[i];
                if (!string.Equals(x.Category, y.Category, StringComparison.Ordinal)
                    || x.TemplateIndex != y.TemplateIndex
                    || !x.LocalX.Equals(y.LocalX) || !x.LocalY.Equals(y.LocalY) || !x.LocalZ.Equals(y.LocalZ)
                    || !x.RotX.Equals(y.RotX) || !x.RotY.Equals(y.RotY)
                    || !x.RotZ.Equals(y.RotZ) || !x.RotW.Equals(y.RotW))
                {
                    return false;
                }
            }

            return true;
        }

        private static void AccumulateLayoutCounts(StringBuilder summary, List<Placement> placements)
        {
            int floor = 0, wall = 0, obstacle = 0, grass = 0, tree = 0, other = 0;
            for (int i = 0; i < placements.Count; i++)
            {
                switch (placements[i].Category)
                {
                    case "floor": floor++; break;
                    case "wall": wall++; break;
                    case "obstacle": obstacle++; break;
                    case "grass": grass++; break;
                    case "tree": tree++; break;
                    default: other++; break;
                }
            }

            summary.AppendLine("  布局（同种子两次生成一致）：total=" + placements.Count.ToString(CultureInfo.InvariantCulture)
                               + " floor=" + floor.ToString(CultureInfo.InvariantCulture)
                               + " wall=" + wall.ToString(CultureInfo.InvariantCulture)
                               + " obstacle=" + obstacle.ToString(CultureInfo.InvariantCulture)
                               + " grass=" + grass.ToString(CultureInfo.InvariantCulture)
                               + " tree=" + tree.ToString(CultureInfo.InvariantCulture)
                               + " other=" + other.ToString(CultureInfo.InvariantCulture));
        }

        // ==================================================================== 落点选择（F2）

        /// <summary>
        /// F2 的落点选择结果：真正要建出来的落点 + 被跳过的未激活落点计数。
        ///
        /// 把“跳过”显式建模（而不是在循环里 `continue`）是为了让计数能同时进
        /// **摘要**与 **digest** —— 否则“产物里少了一部分落点”这件事没有任何痕迹，
        /// 且两次不同策略的烘焙会得到同一个 digest（不可接受）。
        /// </summary>
        private sealed class PlacementSelection
        {
            public readonly List<Placement> Built = new List<Placement>(8192);
            public readonly SortedDictionary<string, int> SkippedByCategory =
                new SortedDictionary<string, int>(StringComparer.Ordinal);
            public bool SkipEnabled;
            public int SkippedInactive;

            public void Skip(string category)
            {
                SkippedInactive++;

                int count;
                if (!SkippedByCategory.TryGetValue(category, out count))
                {
                    count = 0;
                }

                SkippedByCategory[category] = count + 1;
            }
        }

        /// <summary>
        /// 把“全量布局”选成“实际要建出来的落点”（F2）。
        ///
        /// 判据是**模板自身的 <c>activeSelf</c>**，不是它在源场景里的 <c>activeInHierarchy</c>：
        /// 旧链的运行期是 `Instantiate(模板, pos, rot)` 再 `SetParent(MAP)` —— 克隆体继承模板的
        /// `activeSelf`，而 `MAP` 自己是激活的，因此“旧运行期该实例可见/有碰撞吗”就等价于
        /// 模板的 `activeSelf`（源场景里模板的父链状态不参与）。
        ///
        /// 关掉开关（<see cref="SkipInactivePlacements"/> = false）时仍会把未激活落点建出来，
        /// 但**不会**改变 F1 的纪律：创建时一律 active 先拷组件，最后才 SetActive。
        /// </summary>
        private static bool SelectPlacements(ScenseBuildLogic logic, List<Placement> placements,
                                            bool skipInactive, out PlacementSelection selection,
                                            out string error)
        {
            selection = new PlacementSelection();
            selection.SkipEnabled = skipInactive;
            error = null;

            for (int i = 0; i < placements.Count; i++)
            {
                Placement placement = placements[i];

                GameObject template = ResolveTemplate(logic, placement.Category, placement.TemplateIndex);
                if (template == null)
                {
                    error = "落点 " + (i + 1).ToString(CultureInfo.InvariantCulture) + " 解析模板失败："
                            + placement.Category + "["
                            + placement.TemplateIndex.ToString(CultureInfo.InvariantCulture) + "]";
                    return false;
                }

                if (skipInactive && !template.activeSelf)
                {
                    selection.Skip(placement.Category);
                    continue;
                }

                selection.Built.Add(placement);
            }

            return true;
        }

        private static void AccumulateSelectionCounts(StringBuilder summary, PlacementSelection selection)
        {
            summary.AppendLine("  落点选择（skipInactive=" + (selection.SkipEnabled ? "true" : "false")
                               + "）：built=" + selection.Built.Count.ToString(CultureInfo.InvariantCulture)
                               + "，skippedInactive=" + selection.SkippedInactive.ToString(CultureInfo.InvariantCulture)
                               + "（按类别：" + DescribeCounts(selection.SkippedByCategory) + "）");

            if (selection.SkippedInactive > 0)
            {
                summary.AppendLine("    —— 被跳过的都是**模板在源场景就未激活**的落点（`m_IsActive: 0`）。"
                                   + "旧链 `MyInstantiate` 不 SetActive ⇒ `Instantiate` 继承模板状态 ⇒ 这些实例在旧"
                                   + "运行期也不可见、无碰撞；而把该状态前置到 AddComponent 之前正是真机 SIGSEGV 的根因。");
            }
        }

        // ==================================================================== 摘要流

        /// <summary>
        /// 构造 contentDigest 的输入流（纯文本、确定性、可人工复核）。
        /// 结构见文件头"摘要（digest）"段。
        /// </summary>
        private static string BuildDigestStream(ScenseBuildLogic logic, List<Placement> placements,
                                               PlacementSelection selection, GameObject groundPlane,
                                               string sceneSha, string logicSha, string playerSha,
                                               StringBuilder summary)
        {
            StringBuilder sb = new StringBuilder(1 << 20);

            sb.Append("domain=").Append(DigestDomain).Append('\n');
            sb.Append("formatVersion=").Append(PMBattleContentManifest.ExpectedFormatVersion
                      .ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("mapId=").Append(PMBattleContentManifest.ExpectedMapId).Append('\n');
            sb.Append("seed=").Append(PMBattleContentManifest.ExpectedSeed
                      .ToString(CultureInfo.InvariantCulture)).Append('\n');

            // 源文件 SHA：把"引用关系"钉死（模板/材质/mesh 的指向只要变了，SHA 就变）。
            sb.Append("source[0]=").Append(SourceScenePath).Append("|sha256=").Append(sceneSha).Append('\n');
            sb.Append("source[1]=").Append(SourceLogicPath).Append("|sha256=").Append(logicSha).Append('\n');
            sb.Append("source[2]=").Append(SourcePlayerPath).Append("|sha256=").Append(playerSha).Append('\n');

            // 布局参数 + 容器口径（世界等价存放：tile 局部值就是世界值）。
            sb.Append("layout.mapx=").Append(logic.mapx.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("layout.mapy=").Append(logic.mapy.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("layout.tileSpace=world|container=identity|tile.local==tile.world\n");

            // F2：跳过策略与计数必须进摘要 —— 它决定产物到底包含哪些落点。
            // 不写它，“同一份源 + 两种不同跳过策略”会得到同一个 digest（不可接受）。
            sb.Append("layout.skipInactive=").Append(selection.SkipEnabled ? "true" : "false").Append('\n');
            sb.Append("layout.built.count=")
              .Append(selection.Built.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("layout.skippedInactive.count=")
              .Append(selection.SkippedInactive.ToString(CultureInfo.InvariantCulture)).Append('\n');

            // SortedDictionary ⇒ 类别顺序确定，不受插入顺序影响。
            foreach (KeyValuePair<string, int> pair in selection.SkippedByCategory)
            {
                sb.Append("layout.skippedInactive.").Append(pair.Key).Append('=')
                  .Append(pair.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }

            // 模板调色板：逐个模板的规范化签名（结构 + 变换 + mesh/材质引用 + 碰撞体字段）。
            AppendPaletteSection(sb, "floors", logic.floors, summary);
            AppendPaletteSection(sb, "walls", logic.walls, summary);
            AppendPaletteSection(sb, "obstacles", logic.obstacles, summary);
            AppendPaletteSection(sb, "Grasses", logic.Grasses, summary);
            AppendPaletteSection(sb, "trees", logic.trees, summary);

            // 地面（唯一的真实"地板碰撞"来源）。
            sb.Append("ground.source=").Append(GroundSourcePath).Append('\n');
            AppendCanonicalNode(sb, groundPlane, "ground", 0);

            // 落点列表（顺序即源码调用顺序，位置不排序）。
            sb.Append("placements.count=").Append(placements.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            for (int i = 0; i < placements.Count; i++)
            {
                sb.Append(placements[i].CanonicalLine(i)).Append('\n');
            }

            return sb.ToString();
        }

        private static void AppendPaletteSection(StringBuilder sb, string label, GameObject[] templates,
                                                StringBuilder summary)
        {
            sb.Append("palette.").Append(label).Append(".count=")
              .Append(templates.Length.ToString(CultureInfo.InvariantCulture)).Append('\n');

            for (int i = 0; i < templates.Length; i++)
            {
                GameObject template = templates[i];
                // F9（第三轮）：模板“来自场景哪一块”改用**纯结构路径**（名字路径），
                // 不再用 `AssetDatabase.TryGetGUIDAndLocalFileIdentifier` 的 `guid:localId`：
                // 那是 AssetDatabase 本地编号，正是三轮不同摘要（同一源 SHA）里唯一
                // 由加载时机/资产库状态决定的量。名字路径由场景文件本身决定（源 SHA 已钉死）。
                sb.Append("palette.").Append(label).Append('[')
                  .Append(i.ToString(CultureInfo.InvariantCulture)).Append("].source=scene-node:"
                  + PathOf(template)).Append('\n');
                AppendCanonicalNode(sb, template, label + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", 0);
            }

            if (summary != null)
            {
                summary.AppendLine("  调色板 " + label + "：模板数="
                                   + templates.Length.ToString(CultureInfo.InvariantCulture)
                                   + "（含子物体共 "
                                   + CountHierarchy(templates).ToString(CultureInfo.InvariantCulture) + " 个节点）");
            }
        }

        private static int CountHierarchy(GameObject[] templates)
        {
            int total = 0;
            for (int i = 0; i < templates.Length; i++)
            {
                total += templates[i].transform.hierarchyCount;
            }

            return total;
        }

        /// <summary>
        /// 规范化节点签名（唯一一份"逐字段"序列化口径）：
        ///   节点行（名字/激活/层/tag）→ 局部变换 → 组件字段（按类型名排序）→ 子节点（兄弟序）。
        /// 三个用途共用它：摘要输入、深拷贝正确性比对、回读校验比对。
        /// </summary>
        private static void AppendCanonicalNode(StringBuilder sb, GameObject go, string label, int depth)
        {
            sb.Append("node ").Append(label)
              .Append(" name=").Append(Quote(go.name))
              .Append(" active=").Append(go.activeSelf ? "1" : "0")
              .Append(" layer=").Append(go.layer.ToString(CultureInfo.InvariantCulture))
              .Append(" tag=").Append(Quote(go.tag))
              .Append('\n');

            Transform t = go.transform;
            sb.Append("  tf pos=").Append(Vec(t.localPosition))
              .Append(" rot=").Append(Quat(t.localRotation))
              .Append(" scale=").Append(Vec(t.localScale))
              .Append('\n');

            Component[] components = go.GetComponents<Component>();
            List<string> lines = new List<string>(components.Length);
            for (int i = 0; i < components.Length; i++)
            {
                Component c = components[i];
                if (c == null)
                {
                    lines.Add("comp <missing-script>");
                    continue;
                }

                if (c is Transform)
                {
                    continue;   // 变换已单独输出
                }

                lines.Add("comp " + c.GetType().Name + " " + DumpComponent(c));
            }

            lines.Sort(StringComparer.Ordinal);
            for (int i = 0; i < lines.Count; i++)
            {
                sb.Append("  ").Append(lines[i]).Append('\n');
            }

            for (int i = 0; i < t.childCount; i++)
            {
                AppendCanonicalNode(sb, t.GetChild(i).gameObject,
                                    label + "/" + i.ToString(CultureInfo.InvariantCulture), depth + 1);
            }
        }

        /// <summary>
        /// 把一个组件的**全部序列化字段**规范化成一行（按字段路径排序 ⇒ 与迭代顺序无关）。
        /// 对象引用只输出"资产路径/名字/类型"（**绝不**输出实例 ID：那会跨会话漂移）。
        /// Mesh 字段额外补顶点数与 bounds 作为内容锚（防止 mesh 被换但路径名字没变）。
        /// </summary>
        private static string DumpComponent(Component component)
        {
            SerializedObject so = new SerializedObject(component);
            SerializedProperty it = so.GetIterator();

            List<string> fields = new List<string>(32);
            while (it.Next(true))
            {
                string path = it.propertyPath;
                if (IsNonDeterministicPath(path))
                {
                    continue;
                }

                if (it.propertyType == SerializedPropertyType.ArraySize)
                {
                    continue;
                }

                string value = CanonicalPropertyValue(it);

                if (string.Equals(path, "m_Mesh", StringComparison.Ordinal)
                    && it.propertyType == SerializedPropertyType.ObjectReference)
                {
                    value += MeshContentAnchor(it.objectReferenceValue as Mesh);
                }

                fields.Add(path + "=" + value);
            }

            fields.Sort(StringComparer.Ordinal);

            StringBuilder sb = new StringBuilder(512);
            for (int i = 0; i < fields.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(';');
                }

                sb.Append(fields[i]);
            }

            return sb.ToString();
        }

        private static string CanonicalPropertyValue(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean:
                    return property.boolValue ? "1" : "0";
                case SerializedPropertyType.Float:
                    return Num(property.floatValue);
                case SerializedPropertyType.String:
                    return Quote(property.stringValue);
                case SerializedPropertyType.Color:
                    Color color = property.colorValue;
                    return "(" + Num(color.r) + "," + Num(color.g) + "," + Num(color.b) + "," + Num(color.a) + ")";
                case SerializedPropertyType.ObjectReference:
                    return ObjectReferenceIdentity(property.objectReferenceValue);
                case SerializedPropertyType.LayerMask:
                    return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Enum:
                    return property.enumValueIndex.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Vector2:
                    return Vec(property.vector2Value);
                case SerializedPropertyType.Vector3:
                    return Vec(property.vector3Value);
                case SerializedPropertyType.Vector4:
                    return Vec(property.vector4Value);
                case SerializedPropertyType.Rect:
                    Rect rect = property.rectValue;
                    return "(" + Num(rect.x) + "," + Num(rect.y) + "," + Num(rect.width) + "," + Num(rect.height) + ")";
                case SerializedPropertyType.ArraySize:
                    return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Character:
                    return ((int)property.intValue).ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.AnimationCurve:
                    return "<animation-curve>";
                case SerializedPropertyType.Bounds:
                    Bounds bounds = property.boundsValue;
                    return "(" + Vec(bounds.center) + "," + Vec(bounds.extents) + ")";
                case SerializedPropertyType.Gradient:
                    return "<gradient>";
                case SerializedPropertyType.Quaternion:
                    return Quat(property.quaternionValue);
                default:
                    return "<" + property.propertyType.ToString() + ">";
            }
        }

        /// <summary>
        /// 对象引用的**确定性**身份：资产用"资产路径|类型|名字"，场景内对象用"local|类型|名字"。
        /// 刻意不使用 GetInstanceID（每次加载都不同）也不使用 AssetDatabase 的本地子资产编号
        /// （其跨会话稳定性不由我们控制，会破坏"重复生成摘要稳定"）。
        /// </summary>
        private static string ObjectReferenceIdentity(UnityEngine.Object o)
        {
            if (o == null)
            {
                return "<null>";
            }

            // F9（第三轮）：分支判据改用**内容**（对象是否持久化），而不是“AssetDatabase 给不给路径”。
            // 旧写法把 `GetAssetPath(o)` 的返回值当分支条件 —— 而它的返回值本身依赖 AssetDatabase
            // 状态（同一个对象在不同加载时机可能拿到 "" 或路径），等于把环境状态写进了摘要。
            bool persistent;
            try
            {
                persistent = EditorUtility.IsPersistent(o);
            }
            catch (Exception)
            {
                // 判据本身拿不到时**不猜资产身份**，按场景内对象处理（仍然完全确定）。
                persistent = false;
            }

            if (persistent)
            {
                string assetPath = AssetDatabase.GetAssetPath(o);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    return "asset|" + assetPath + "|" + o.GetType().Name + "|" + Quote(o.name);
                }

                // 持久化对象却拿不到路径：如实标记，但**绝不**回退到任何“编号”（instanceID/localId）。
                return "asset|<no-path>|" + o.GetType().Name + "|" + Quote(o.name);
            }

            // 场景内对象：**纯结构身份**（类型 + 场景内名字路径），不碰 AssetDatabase、不用编号。
            GameObject go = o as GameObject;
            if (go != null)
            {
                return "scene-node|GameObject|" + PathOf(go);
            }

            Component component = o as Component;
            if (component != null)
            {
                GameObject owner = null;
                try
                {
                    owner = component.gameObject;
                }
                catch (Exception)
                {
                }

                return "scene-comp|" + component.GetType().Name + "|"
                       + (owner == null ? "<no-go>" : PathOf(owner));
            }

            return "scene|" + o.GetType().Name + "|" + Quote(o.name);
        }

        /// <summary>mesh 内容锚：顶点数 + bounds（不用 localID，避免跨会话漂移）。</summary>
        private static string MeshContentAnchor(Mesh mesh)
        {
            if (mesh == null)
            {
                return "";
            }

            Bounds b;
            try
            {
                b = mesh.bounds;
            }
            catch (Exception)
            {
                return "|mesh=unreadable";
            }

            return "|mesh.verts=" + mesh.vertexCount.ToString(CultureInfo.InvariantCulture)
                   + "|mesh.bounds=" + Vec(b.center) + "/" + Vec(b.extents);
        }

        /// <summary>
        /// 跨会话不稳定/与内容无关的字段路径 —— **只用于只读摘要**（<see cref="DumpComponent"/>）。
        /// 本轮起字段拷贝不再经 `SerializedObject`（改走公开 API），因此这个黑名单**只影响摘要**，
        /// 不再影响“哪些字段被拷过去”。
        ///
        /// **子树语义**（本轮修正）：这些都可能是**带子字段的节点**（例如 `m_PrefabInstance` 在
        /// 某些组件上是结构体），只做字符串全等匹配会让 `m_PrefabInstance.xxx` 这类子路径漏进摘要 ——
        /// 而它们恰恰是跨会话不稳定的量。因此改成“路径本身**或**它的某个子节点”命中即跳过。
        ///
        /// 口径提醒：这条黑名单**不是**任何崩溃的根因（现场只证明 32 条 `CheckConsistency` 的调用栈
        /// 落在旧码的 `ApplyModifiedProperties` 上）；它在这里的唯一作用是让摘要不含跨会话漂移的量。
        /// 数组的“长度”不单独进摘要（`DumpComponent` 跳过 `ArraySize` 类型的节点），元素本身
        /// （`xxx.Array.data[n]`）逐个进摘要，长度由元素条数隐式表征。
        /// </summary>
        private static bool IsNonDeterministicPath(string path)
        {
            return MatchesPathOrSubtree(path, "m_GameObject")
                   || MatchesPathOrSubtree(path, "m_Script")
                   || MatchesPathOrSubtree(path, "m_PrefabInstance")
                   || MatchesPathOrSubtree(path, "m_PrefabAsset")
                   || MatchesPathOrSubtree(path, "m_CorrespondingSourceObject")
                   || MatchesPathOrSubtree(path, "m_PrefabInternal");
        }

        /// <summary>
        /// `path == name`，或 `path` 落在 `name` 的**子路径**里（`name.` 前缀）都算命中。
        /// 只用字符串前缀判断（字段路径就是 `a.b.c` 形式的标识符序列，足够），不引入正则。
        /// </summary>
        private static bool MatchesPathOrSubtree(string path, string name)
        {
            if (path == null || name == null)
            {
                return false;
            }

            if (string.Equals(path, name, StringComparison.Ordinal))
            {
                return true;
            }

            return path.Length > name.Length
                   && path.StartsWith(name, StringComparison.Ordinal)
                   && path[name.Length] == '.';
        }

        // ==================================================================== 地图层级搭建

        /// <summary>
        /// 搭建"纯地图"层级：
        ///   BattleMapV1(identity)
        ///     ├── Ground(identity) → Plane(scale 10)   ← 源场景 HYLDGameTatal/MAP/Plane（地面 MeshCollider）
        ///     └── MAP(identity)    → tile_00001 …      ← 按布局逐格深拷贝模板
        ///
        /// 为什么容器用 identity：源码 `SetParent(MAP)`（worldPositionStays=true）把 `3D` 的 0.5
        /// 缩放抵消掉了，tile 的世界变换就是 `(gridPos, identity, templateScale)`；
        /// 这里按世界等价直接存放，不做 0.5×2 往返（做错就会把地图缩成一半）。
        ///
        /// **F1 生成纪律**（本方法里最容易再犯的错）：目标对象先以 **active** 建出来、把组件都拷完，
        /// **最后**才 `SetActive(模板的最终态)`。旧码先 `SetActive(template.activeSelf)` 再
        /// CopyComponentSet，于是“向未激活对象 AddComponent” —— 真机 SIGSEGV 的直接原因：
        /// 非激活对象不会走 `Activate/Deactivate`，组件注册的 transform change interests 永不注销，
        /// 物理登记表被破坏，关场景时 native 崩溃。
        /// </summary>
        private static GameObject BuildMapHierarchy(ScenseBuildLogic logic, List<Placement> placements,
                                                   GameObject groundPlane, StringBuilder summary,
                                                   out string error)
        {
            error = null;

            // F8：所有自建对象走 `NewScratchObject`（建出来 → **显式**搬进已证明隔离物理世界的临时场景）。
            // 不再依赖“临时场景是活动场景”：预览场景不能当活动场景，靠它就等于把对象建到用户场景里。
            GameObject root = NewScratchObject(MapRootName);
            root.layer = 0;
            NoteDestCreated(root);

            GameObject groundContainer = NewScratchObject(GroundContainerName);
            groundContainer.transform.SetParent(root.transform, false);
            NoteDestCreated(groundContainer);

            // F1：地面也走同一条纪律 —— new GameObject 默认 active，拷完组件后才施加最终激活态。
            // （调用方已保证 groundPlane.activeSelf == true，否则它根本不会走到这里：
            //   地面是唯一碰撞地板，“跳过地面” = 出一张不可用地图，见 PrepareContentInternal。）
            GameObject groundClone = NewScratchObject(groundPlane.name);
            groundClone.transform.SetParent(groundContainer.transform, false);
            NoteDestCreated(groundClone);
            CopyTransformValues(groundPlane.transform, groundClone.transform);
            groundClone.layer = groundPlane.layer;
            TrySetTag(groundClone, groundPlane.tag, summary);

            // 地面也需要映射上下文：Renderer.probeAnchor（以及将来若地面带 LODGroup）都要重绑定到
            // **目标**对象，绝不能把引用指回源场景。
            MapCopyContext groundContext = new MapCopyContext();
            groundContext.NodeMap[groundPlane.transform] = groundClone.transform;

            List<string> stripped;
            if (!CopyComponentSet(groundPlane, groundClone, true, groundContext, out stripped))
            {
                error = "地面对象含白名单外组件或字段拷贝失败：" + Join(stripped)
                        + "（对象=\"" + groundPlane.name + "\"）；纯地图必须只含几何/渲染/碰撞。";
                return null;
            }

            // 延后引用重绑定：引用类字段只能在"整棵目标层级建完之后"写。
            List<string> groundFixupFailures = new List<string>();
            if (!ResolveDeferredReferences(groundContext, groundFixupFailures))
            {
                error = "地面对象的延后引用重绑定失败：" + Join(groundFixupFailures);
                return null;
            }

            // 拷贝全部完成，现在才施加模板的最终激活态（本场景地面是 active，留着这行是为了纪律一致）。
            if (groundClone.activeSelf != groundPlane.activeSelf)
            {
                groundClone.SetActive(groundPlane.activeSelf);
            }

            GameObject mapContainer = NewScratchObject(MapContainerName);
            mapContainer.transform.SetParent(root.transform, false);
            NoteDestCreated(mapContainer);

            int ordinal = 0;
            int copiedNodes = 0;
            int inactiveTiles = 0;
            for (int i = 0; i < placements.Count; i++)
            {
                Placement placement = placements[i];
                ordinal++;

                GameObject template = ResolveTemplate(logic, placement.Category, placement.TemplateIndex);
                if (template == null)
                {
                    error = "落点 " + ordinal.ToString(CultureInfo.InvariantCulture) + " 解析模板失败："
                            + placement.Category + "[" + placement.TemplateIndex.ToString(CultureInfo.InvariantCulture) + "]";
                    return null;
                }

                string tileName = BuildTileName(placement, ordinal, template);

                // F1：新建的 GameObject 默认 active；**不在这里 SetActive**，先把组件拷完、
                // 子层也搭完，最后才施加模板的最终激活态（下方循环末尾）。
                GameObject tile = NewScratchObject(tileName);
                tile.transform.SetParent(mapContainer.transform, false);
                NoteDestCreated(tile);
                tile.transform.localPosition = new Vector3(placement.LocalX, placement.LocalY, placement.LocalZ);
                tile.transform.localRotation = new Quaternion(placement.RotX, placement.RotY, placement.RotZ,
                                                             placement.RotW);
                tile.transform.localScale = template.transform.localScale;
                tile.layer = template.layer;
                TrySetTag(tile, template.tag, summary);

                // 每个 tile 各一个映射上下文：LODGroup 的 Renderer 数组 / Renderer.probeAnchor
                // 都限定在本 tile 子树内重绑定，不会跨 tile 串味。
                MapCopyContext tileContext = new MapCopyContext();
                tileContext.NodeMap[template.transform] = tile.transform;

                if (!CopyComponentSet(template, tile, true, tileContext, out stripped))
                {
                    error = "模板 \"" + template.name + "\" 含白名单外组件或字段拷贝失败：" + Join(stripped)
                            + "（C2 侧会显式拒绝加载，故这里直接失败）。";
                    return null;
                }

                if (!CopyChildHierarchy(template.transform, tile.transform, true, tileContext, summary,
                                        ref copiedNodes, out error))
                {
                    return null;
                }

                // 契约要求：LODGroup 的 Renderer 引用必须在**完整目标层级建完以后**按源→目标映射重绑定。
                // 因此这里（子层都搭完之后、施加最终激活态之前）统一兑现。
                List<string> tileFixupFailures = new List<string>();
                if (!ResolveDeferredReferences(tileContext, tileFixupFailures))
                {
                    error = "模板 \"" + template.name + "\" 的延后引用重绑定失败："
                            + Join(tileFixupFailures);
                    return null;
                }

                // 组件/子层都就绪后才施加模板的最终激活态（F1 的最后一步）。
                if (tile.activeSelf != template.activeSelf)
                {
                    tile.SetActive(template.activeSelf);
                }

                if (!tile.activeSelf)
                {
                    inactiveTiles++;
                }

                copiedNodes++;
            }

            summary.AppendLine("  地图层级：tile 根=" + ordinal.ToString(CultureInfo.InvariantCulture)
                               + "，含子物体共 " + copiedNodes.ToString(CultureInfo.InvariantCulture) + " 个节点"
                               + "，未激活 tile 根=" + inactiveTiles.ToString(CultureInfo.InvariantCulture)
                               + "（默认跳过策略下应为 0：未激活模板的落点已在选择阶段跳过）");
            return root;
        }

        private static GameObject ResolveTemplate(ScenseBuildLogic logic, string category, int index)
        {
            GameObject[] array;
            switch (category)
            {
                case "floor": array = logic.floors; break;
                case "wall": array = logic.walls; break;
                case "obstacle": array = logic.obstacles; break;
                case "grass": array = logic.Grasses; break;
                case "tree": array = logic.trees; break;
                default: return null;
            }

            if (array == null || index < 0 || index >= array.Length)
            {
                return null;
            }

            return array[index];
        }

        private static string BuildTileName(Placement placement, int ordinal, GameObject template)
        {
            string prefix;
            switch (placement.Category)
            {
                case "floor": prefix = "F"; break;
                case "wall": prefix = "W"; break;
                case "obstacle": prefix = "O"; break;
                case "grass": prefix = "G"; break;
                case "tree": prefix = "T"; break;
                default: prefix = "X"; break;
            }

            return prefix + ordinal.ToString("D5", CultureInfo.InvariantCulture) + "_" + template.name;
        }

        /// <summary>
        /// 深拷贝子层（模板子物体 → tile 子物体）：局部变换逐字段复制，组件走白名单。
        /// vector3/quaternion/scale 一律直接复制，不做"统一缩放/统一朝向"之类的简化。
        ///
        /// **F1**：目标子物体先以 active 建出来 → 拷组件 → 递归搭它的子树 → **最后**才
        /// `SetActive(源子物体的 activeSelf)`。这样“AddComponent 的那一刻”整条父链都是
        /// `activeInHierarchy`（父 tile 也要等到本方法返回后才会施加最终激活态）。
        /// 旧码在这里同样先把 `SetActive(sourceGo.activeSelf)` 放在 CopyComponentSet 之前 ——
        /// 同一个“向非激活对象 AddComponent”的根因，在子层一样会命中。
        /// </summary>
        private static bool CopyChildHierarchy(Transform sourceParent, Transform destParent, bool forMap,
                                              MapCopyContext context, StringBuilder summary,
                                              ref int copiedNodes, out string error)
        {
            error = null;

            for (int i = 0; i < sourceParent.childCount; i++)
            {
                Transform sourceChild = sourceParent.GetChild(i);
                GameObject sourceGo = sourceChild.gameObject;

                GameObject destGo = NewScratchObject(sourceGo.name);
                destGo.transform.SetParent(destParent, false);
                NoteDestCreated(destGo);
                CopyTransformValues(sourceChild, destGo.transform);
                destGo.layer = sourceGo.layer;
                TrySetTag(destGo, sourceGo.tag, summary);

                // 源→目标节点映射：延后重绑定（LODGroup 的 Renderer / probeAnchor）靠它换算。
                context.NodeMap[sourceChild] = destGo.transform;

                List<string> stripped;
                if (!CopyComponentSet(sourceGo, destGo, forMap, context, out stripped))
                {
                    error = "子物体 \"" + sourceGo.name + "\" 含白名单外组件或字段拷贝失败：" + Join(stripped);
                    return false;
                }

                copiedNodes++;
                if (!CopyChildHierarchy(sourceChild, destGo.transform, forMap, context, summary,
                                       ref copiedNodes, out error))
                {
                    return false;
                }

                // F1 的最后一步：子树全部就绪后才施加源子物体的最终激活态。
                if (destGo.activeSelf != sourceGo.activeSelf)
                {
                    destGo.SetActive(sourceGo.activeSelf);
                }
            }

            return true;
        }

        private static void CopyTransformValues(Transform source, Transform dest)
        {
            dest.localPosition = source.localPosition;
            dest.localRotation = source.localRotation;
            dest.localScale = source.localScale;
        }

        // ==================================================================== 角色表现烘焙

        /// <summary>
        /// 把源角色 prefab 清理成"干净表现"：
        ///   · 克隆（**不用** PrefabUtility.InstantiatePrefab，避免把改动回写到原件）；
        ///   · 移除 missing script（GameObjectUtility.RemoveMonoBehavioursWithMissingScript）；
        ///   · 只保留 Transform 系 / MeshFilter / MeshRenderer / SkinnedMeshRenderer / Animator / LODGroup；
        ///     其余（旧脚本 / Collider / Rigidbody / AudioSource / Canvas 系 UI / LineRenderer /
        ///     SpriteRenderer / CanvasRenderer …）逐个删除并**按类型计数上报**；
        ///   · 删除旧世界空间 UI 子树 Canvas（它的驱动器就是被删掉的旧脚本）；
        ///   · Animator.applyRootMotion = false（并且校验控制器里确实有 Float 型 "Speed"）。
        /// </summary>
        private static GameObject BuildPlayerHierarchy(GameObject sourcePrefab, StringBuilder summary,
                                                      out string error)
        {
            error = null;

            GameObject clone = UnityEngine.Object.Instantiate(sourcePrefab);
            clone.name = PlayerRootName;

            // F8：`Instantiate` 把克隆体放在**活动场景**里；临时场景是隔离物理世界的那个，
            // 因此必须显式搬过去（否则角色会被建到用户场景里，既污染又逃出隔离）。
            AdoptIntoScratchScene(clone);
            NoteDestCreated(clone);

            // ---- 1) missing script（先删，避免后面 Destroy 空脚本组件报错）
            // 注意：GameObject 不是 Component，GetComponentsInChildren<GameObject>() 编不过；
            // 用 Transform 枚举全部节点（含未激活）再取 gameObject。
            int missingRemoved = 0;
            Transform[] allNodes = clone.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < allNodes.Length; i++)
            {
                missingRemoved += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(allNodes[i].gameObject);
            }

            // ---- 2) 删掉旧世界空间 UI 子树（Canvas）：驱动器是被删掉的旧脚本，留着就是死 UI
            int canvasSubtreeRemoved = 0;
            List<string> canvasNotes = new List<string>();
            for (int i = clone.transform.childCount - 1; i >= 0; i--)
            {
                Transform child = clone.transform.GetChild(i);
                if (!string.Equals(child.gameObject.name, "Canvas", StringComparison.Ordinal))
                {
                    continue;
                }

                canvasSubtreeRemoved = child.hierarchyCount;
                canvasNotes.Add("子物体 \"" + child.gameObject.name + "\"（" 
                                + canvasSubtreeRemoved.ToString(CultureInfo.InvariantCulture)
                                + " 个节点，含旧血条/名字/宝石计数等世界空间 UI）");
                UnityEngine.Object.DestroyImmediate(child.gameObject);
            }

            // ---- 3) 白名单外组件逐类型删除 + 计数
            Dictionary<string, int> removedByType = new Dictionary<string, int>(StringComparer.Ordinal);
            List<string> keptNotes = new List<string>();
            int keptComponents = 0;

            for (int i = 0; i < allNodes.Length; i++)
            {
                GameObject node = allNodes[i] == null ? null : allNodes[i].gameObject;

                if (node == null)
                {
                    continue;
                }

                Component[] components = node.GetComponents<Component>();
                for (int c = components.Length - 1; c >= 0; c--)
                {
                    Component component = components[c];
                    if (component == null)
                    {
                        continue;   // 已由 RemoveMonoBehavioursWithMissingScript 处理
                    }

                    if (component is Transform)
                    {
                        continue;   // Transform/RectTransform 是物体的固有组件，不能删
                    }

                    string typeName = component.GetType().Name;
                    if (Contains(PlayerKeptComponentNames, typeName))
                    {
                        keptComponents++;

                        if (!Contains(keptNotes.ToArray(), typeName))
                        {
                            keptNotes.Add(typeName);
                        }

                        Animator animator = component as Animator;
                        if (animator != null)
                        {
                            // 契约：root motion 关闭。源资产本来就是 0，这里显式再写一次并校验。
                            animator.applyRootMotion = false;
                        }

                        continue;
                    }

                    int count;
                    removedByType.TryGetValue(typeName, out count);
                    removedByType[typeName] = count + 1;
                    UnityEngine.Object.DestroyImmediate(component);
                }
            }

            // ---- 4) 校验：确实没有残留（Animator 必须存在且 Speed 参数存在）
            Animator[] animators = clone.GetComponentsInChildren<Animator>(true);
            if (animators.Length == 0)
            {
                error = "清理后的角色表现没有 Animator：C2 的 PMUnityBattlePresentation 需要它驱动 Speed 参数。";
                return null;
            }

            for (int i = 0; i < animators.Length; i++)
            {
                if (animators[i].applyRootMotion)
                {
                    error = "清理后的角色 Animator.applyRootMotion 仍为 true（契约要求关闭）。";
                    return null;
                }

                if (animators[i].runtimeAnimatorController == null)
                {
                    error = "清理后的角色 Animator 没有 runtimeAnimatorController（Speed 参数无从校验）。";
                    return null;
                }
            }

            bool hasSpeed = ControllerDefinesFloatParameter(animators[0].runtimeAnimatorController, "Speed");

            if (!hasSpeed)
            {
                error = "角色 Animator 控制器里没有 Float 型 \"Speed\" 参数："
                        + "C2 会因此完全不驱动动画（契约要求该参数供 C2 使用）。";
                return null;
            }

            // ---- 5) 摘要（"报告"而不是"静默"）
            summary.AppendLine("  角色表现清理：保留组件类型={" + Join(keptNotes) + "}（共 "
                               + keptComponents.ToString(CultureInfo.InvariantCulture) + " 个）");
            summary.AppendLine("  角色表现清理：删除 missing script="
                               + missingRemoved.ToString(CultureInfo.InvariantCulture)
                               + " 个；删除旧 UI 子树="
                               + (canvasSubtreeRemoved == 0 ? "无" : Join(canvasNotes)));
            summary.AppendLine("  角色表现清理：按类型删除组件 = " + DescribeCounts(removedByType));
            summary.AppendLine("  角色表现清理：Animator="
                               + animators.Length.ToString(CultureInfo.InvariantCulture)
                               + "，applyRootMotion=false，Speed(Float) 参数存在，节点数="
                               + clone.transform.hierarchyCount.ToString(CultureInfo.InvariantCulture));

            return clone;
        }

        // ==================================================================== 组件拷贝（显式公开 API）

        /// <summary>
        /// 地图层级拷贝期间需要的两样东西（每个 tile 一个实例；地面单独一个）：
        ///   1) **源 → 目标节点映射**（源 `Transform` → 目标 `Transform`）。拷贝是逐节点进行的，
        ///      但有一类字段只能在**整棵子树建完之后**才能写：`LODGroup` 的 Renderer 数组与
        ///      `Renderer.probeAnchor`。它们都是"指向本层级里另一个对象"的引用，必须按**目标**对象
        ///      重绑定 —— 否则引用会指回源场景，存成 prefab 时就是跨场景脏引用（或干脆丢失）。
        ///   2) **延后修复请求**：拷贝时就地登记"待重绑定"，整棵子树搭完后统一
        ///      <see cref="ResolveDeferredReferences"/> 兑现。
        /// 映射与修复都限定在该 tile 的子树内，不会跨 tile 串味。
        /// </summary>
        private sealed class MapCopyContext
        {
            /// <summary>源 Transform → 目标 Transform（只收录本次拷贝真正建出来的节点）。</summary>
            public readonly Dictionary<Transform, Transform> NodeMap = new Dictionary<Transform, Transform>();

            /// <summary>待重绑定的引用（顺序即登记顺序，便于日志复现）。</summary>
            public readonly List<DeferredReferenceFixup> Fixups = new List<DeferredReferenceFixup>();
        }

        /// <summary>延后重绑定的引用（目前两种：`LODGroup` 的 Renderer 数组、`Renderer.probeAnchor`）。</summary>
        private sealed class DeferredReferenceFixup
        {
            /// <summary>"LODGroup" / "Renderer.probeAnchor"。</summary>
            public string Kind;

            public Component Source;
            public Component Dest;
            public string TargetPath;
        }

        /// <summary>
        /// 把源物体上的组件按白名单拷到目标物体（Transform 不在此列：由调用方单独处理）。
        ///
        /// **本轮（R4-C 显式拷贝）的口径变更**：字段拷贝不再走 `SerializedObject` +
        /// `CopyFromSerializedProperty` / `ApplyModifiedProperties`（通用隐藏字段搬运），而是对
        /// **明确支持的组件类型**逐字段调用 Unity 公开 API（见 <see cref="TryCopyComponentFields"/>）。
        ///
        /// 证据边界（必须分清"现场"与"假说"，不要再把假说写成结论）：
        ///   · **已观测的现场**：真机 `Editor.log` 里 32 条
        ///     `CheckConsistency: GameObject does not reference component <类型>. Fixing.`，
        ///     调用栈**全部**落在 `SerializedObject.ApplyModifiedPropertiesWithoutUndo()` ← 本方法；
        ///   · **未被证实的当时假说**：通用序列化字段写入就是 SIGSEGV 根因、共享物理世界破坏登记表、
        ///     向非激活对象 AddComponent 必崩。本轮**不依赖**这些假说 —— 直接删掉那条写路径，并让
        ///     "拷了哪些字段"变成可枚举、可回读核对的事实。
        ///   · F1（先以 active 建、拷完组件、最后才施加模板最终激活态）作为**安全纪律保留**，
        ///     但它不再被描述为已证实的根因。
        ///
        /// 白名单判定与"是否允许出现"的语义差异：
        ///   · forMap=true（纯地图）：白名单外**返回 false**（fail closed，C2 侧同样会拒载）；
        ///   · forMap=false（角色）：白名单外**已在 BuildPlayerHierarchy 删掉**；且角色路径
        ///     **根本不调用本方法**（它用 `Instantiate` + 删组件），因此 `forMap=false` 目前不可达。
        ///
        /// **两道门，缺一不可**：
        ///   · `IsAllowedComponent` = "这个类型允许出现在产物里吗"（与 C2 的允许集对齐）；
        ///   · `IsExplicitCopySupported` = "我们真的实现了它的字段拷贝吗"。
        ///   只过第一道门就 AddComponent、然后静默丢字段，正是本轮要消除的形态。
        ///
        /// **F3 健壮性**（真机实证的两条坑，都不是理论担心）：
        ///   · **AddComponent 前先 `dest.GetComponent(type)` 判重**：真机 Editor.log 有
        ///     `Can't add component 'MeshFilter' to F00003_Plain02 (1) because such a component
        ///     is already added to the game object!`。判重是第二道防线：重复就记入 offenders
        ///     让烘焙失败，**不再让 Unity 自己报错**；
        ///   · **AddComponent 返回 null 不得继续**：直接当失败（旧码会把它交给
        ///     `new SerializedObject(null)`）。
        ///   · **不支持的类型必须在 AddComponent 之前拒绝**：宁可"在创建组件之前明确失败"，
        ///     也不要"先建出来、再静默丢字段"。
        ///
        /// `offenders` 承载的失败类别：
        ///   · 白名单外的组件类型名；
        ///   · **未实现显式字段拷贝**的类型（fail closed，不静默丢字段）；
        ///   · 目标已有同类型组件（判重命中）/ Unity 组件登记异常；
        ///   · AddComponent 返回 null / 逐字段拷贝或回读核对失败 / 源或目标 owner 归属变化。
        /// </summary>
        private static bool CopyComponentSet(GameObject source, GameObject dest, bool forMap,
                                            MapCopyContext context, out List<string> offenders)
        {
            offenders = new List<string>();

            // F10（第三轮）：`addedByUs` = **我们自己**在这个 dest 上真的 AddComponent 过的类型。
            //
            // 为什么不能用 `dest.GetComponent(type)` 当唯一判据（这是第二轮的教训）：真机日志里
            // Unity 自己报了 32 条 `CheckConsistency: GameObject does not reference component
            // <类型>. Fixing.`（调用栈就是旧码的 `ApplyModifiedProperties`）。那一刻起 GameObject 的
            // 组件登记表就不可信了：`GetComponent` 会报出**我们根本没拷过**的组件，于是"模板含重复
            // 组件"的归因就是错的（误报）。
            // 因此分两类报：
            //   · 非 Collider 类型记账命中 ⇒ 不支持的重复——失败；Collider 允许同类型多个实例。
            //   · 记账未命中但 GetComponent 命中 ⇒ **Unity 组件登记异常**——同样是失败（fail closed，
            //     不放宽任何门），但归因与证据完全不同，且把 dest/source 的完整清单打出来。
            HashSet<Type> addedByUs = new HashSet<Type>();

            // "保留源场景/源对象只读"的可检查形式：整个拷贝过程中源的 owner 必须一直是同一个对象。
            GameObject sourceOwner = null;
            try
            {
                sourceOwner = source.gameObject;
            }
            catch (Exception)
            {
                sourceOwner = null;
            }

            if (sourceOwner == null)
            {
                offenders.Add("源组件取不到 owner（源对象已损坏，拒绝继续）：源物体 \""
                              + PathOf(source) + "\"");
                return false;
            }

            Component[] components = source.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null)
                {
                    offenders.Add("<missing-script>");
                    continue;
                }

                if (component is Transform)
                {
                    continue;
                }

                if (!IsAllowedComponent(component, forMap))
                {
                    offenders.Add(component.GetType().Name);
                    continue;
                }

                Type componentType = component.GetType();

                // **本轮核心**：没有显式字段拷贝实现的类型，在 AddComponent **之前**就拒绝。
                // 旧写法（SerializedObject 通用搬运）会把"没实现"变成"静默丢字段"，产物看起来
                // 生成成功、内容却是错的。
                string unsupportedReason;
                if (!IsExplicitCopySupported(componentType, out unsupportedReason))
                {
                    offenders.Add("不支持的组件类型(未实现显式字段拷贝，AddComponent 之前拒绝):"
                                  + componentType.Name + ":" + PathOf(dest)
                                  + "（源物体 \"" + PathOf(source) + "\"；原因：" + unsupportedReason + "）");
                    continue;
                }

                // 同一物体可合法包含多个同类型 Collider；逐个 AddComponent 并复制各自形状。
                // 只允许本次已创建的 Collider 共存，首次遇到预存组件仍拒绝。
                // 其它组件保持原有单实例守卫。
                if (!(component is Collider) && addedByUs.Contains(componentType))
                {
                    offenders.Add("重复组件:" + componentType.Name + ":" + PathOf(dest)
                                  + "(同一目标被我们拷了两次同一类型：源物体 \"" + PathOf(source)
                                  + "\" | " + DestRegistryLine(dest) + ")");
                    continue;
                }

                Component existing = dest.GetComponent(componentType);
                if (existing != null && !(component is Collider && addedByUs.Contains(componentType)))
                {
                    offenders.Add("Unity组件登记异常:" + componentType.Name + ":" + PathOf(dest)
                                  + "(我们并未拷贝过该类型，但 GetComponent 报已存在！源物体 \"" + PathOf(source)
                                  + "\" | " + DestRegistryLine(dest)
                                  + " | dest 组件清单：" + DescribeComponentInventory(dest)
                                  + " | source 组件清单：" + DescribeSourceComponents(components) + ")");
                    continue;
                }

                Component copy = dest.AddComponent(componentType);
                if (copy == null)
                {
                    // 旧码在这里会把 null 交给 CopyComponentFields（new SerializedObject(null)）。
                    offenders.Add("AddComponent 返回 null:" + componentType.Name + ":" + PathOf(dest)
                                  + " | " + DestRegistryLine(dest));
                    continue;
                }

                addedByUs.Add(componentType);

                // 逐字段显式拷贝 + 立即回读核对（拷了什么 = 可核对的事实）。
                TryCopyComponentFields(component, copy, context, offenders, PathOf(dest));

                // F10 fail-fast（**不依赖日志过滤**）：直接问刚拷完的组件"你属于谁"。
                // 真机异常路径下组件会自称不属于 dest —— 那就是
                // `CheckConsistency: GameObject does not reference component <类型>` 的实体。
                // 一旦出现就**立即停**：宁可在第一处失败，也不要继续堆出上百个坏对象。
                GameObject owner = null;
                try
                {
                    owner = copy.gameObject;
                }
                catch (Exception)
                {
                    // 已坏到连 owner 都拿不到：也算异常。
                }

                if (owner != dest)
                {
                    offenders.Add("Unity组件登记异常(owner 不是 dest):" + componentType.Name
                                  + ":" + PathOf(dest)
                                  + "(拷完字段后组件自称属于 \""
                                  + (owner == null ? "<null>" : PathOf(owner))
                                  + "\"；源物体 \"" + PathOf(source)
                                  + "\" | " + DestRegistryLine(dest)
                                  + " | dest 组件清单：" + DescribeComponentInventory(dest)
                                  + " | source 组件清单：" + DescribeSourceComponents(components)
                                  + " | Unity 一致性日志条数="
                                  + _unityConsistencyMessages.Count.ToString(CultureInfo.InvariantCulture) + ")");
                    return false;
                }

                // 源侧的 owner 必须一字未变（"源场景/源对象只读"的可检查形式）。
                GameObject sourceOwnerAfter = null;
                try
                {
                    sourceOwnerAfter = source.gameObject;
                }
                catch (Exception)
                {
                    sourceOwnerAfter = null;
                }

                if (sourceOwnerAfter != sourceOwner)
                {
                    offenders.Add("源对象 owner 在拷贝后发生变化:" + componentType.Name
                                  + ":(拷贝前 \"" + PathOf(sourceOwner) + "\" 拷贝后 \""
                                  + (sourceOwnerAfter == null ? "<null>" : PathOf(sourceOwnerAfter))
                                  + "\"）：源对象被改写，拒绝继续。");
                    return false;
                }

                if (_unityConsistencyAnomaly)
                {
                    offenders.Add("Unity组件登记异常(CheckConsistency 日志):" + componentType.Name
                                  + ":" + PathOf(dest)
                                  + "(Unity 报 \""
                                  + (_unityConsistencyMessages.Count == 0
                                      ? "<无原文>"
                                      : _unityConsistencyMessages[_unityConsistencyMessages.Count - 1])
                                  + "\"；源物体 \"" + PathOf(source)
                                  + "\" | " + DestRegistryLine(dest)
                                  + " | dest 组件清单：" + DescribeComponentInventory(dest)
                                  + " | source 组件清单：" + DescribeSourceComponents(components) + ")");
                    return false;
                }
            }

            return offenders.Count == 0;
        }

        /// <summary>
        /// 组件是否**允许出现**在产物里（与 C2 的允许集对齐）。注意它**不等于**"我们实现了字段拷贝" ——
        /// 后者是 <see cref="IsExplicitCopySupported"/>。两道门都必须过。
        /// </summary>
        private static bool IsAllowedComponent(Component component, bool forMap)
        {
            string typeName = component.GetType().Name;

            if (forMap)
            {
                if (Contains(MapAllowedComponentNames, typeName))
                {
                    return true;
                }

                if (component is Collider)
                {
                    return true;    // 任意 Collider（保留真实形状；C2 会区分 trigger/非 trigger）
                }

                Rigidbody rigidbody = component as Rigidbody;
                if (rigidbody != null)
                {
                    return rigidbody.isKinematic;    // 动态刚体一律不允许
                }

                return false;
            }

            return Contains(PlayerKeptComponentNames, typeName);
        }

        /// <summary>
        /// **显式字段拷贝的唯一支持集**。不在这个集合里的类型一律 fail closed（调用方在
        /// AddComponent **之前**拒绝）—— 这是本轮的核心不变量：不允许"建出来但没拷字段"。
        ///
        /// 支持集与文件头「支持矩阵」、以及 `Tools/PMBattleContentSceneFactsCheck` §H 的
        /// 源数据检查必须一致。当前源场景模板层级（38 个节点）实测只出现
        /// Transform / MeshFilter / MeshRenderer / MeshCollider / BoxCollider，其余类型
        /// **有实现但未被源数据执行到**（不得称已验证）。
        /// </summary>
        private static bool IsExplicitCopySupported(Type componentType, out string reason)
        {
            if (componentType == typeof(MeshFilter)
                || componentType == typeof(MeshRenderer)
                || componentType == typeof(LODGroup)
                || componentType == typeof(BoxCollider)
                || componentType == typeof(SphereCollider)
                || componentType == typeof(CapsuleCollider)
                || componentType == typeof(MeshCollider)
                || componentType == typeof(Rigidbody))
            {
                reason = null;
                return true;
            }

            if (typeof(Collider).IsAssignableFrom(componentType))
            {
                reason = "是 Collider 子类，但只实现了 Box/Sphere/Capsule/Mesh 四种形状的字段拷贝";
                return false;
            }

            if (componentType == typeof(SkinnedMeshRenderer) || componentType == typeof(Animator))
            {
                reason = "属于角色表现白名单，但角色路径走 Instantiate + 删组件，不经过本函数";
                return false;
            }

            reason = "不在显式字段拷贝支持集内";
            return false;
        }

        /// <summary>取组件所属 GameObject（永不抛）。</summary>
        private static GameObject SafeOwner(Component component)
        {
            if (component == null)
            {
                return null;
            }

            try
            {
                return component.gameObject;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>组件所属对象的路径（组件为 null / owner 取不到时给明确占位，不抛）。</summary>
        private static string PathOf(Component component)
        {
            GameObject go = SafeOwner(component);
            return go == null ? "<no-owner>" : PathOf(go);
        }

        /// <summary>回读核对失败的统一措辞（"拷了但目标值不等于源值"）。</summary>
        private static string FieldFailure(string typeName, string field, string destPath)
        {
            return "字段回读核对失败:" + typeName + "." + field + ":" + destPath + "（目标值与源不一致）";
        }

        /// <summary>把目标组件取成具体类型；类型不符/为 null 时记失败而不是抛。</summary>
        private static bool RequireDest<T>(Component dest, string typeName, string destPath,
                                          List<string> failures, out T typed) where T : Component
        {
            typed = dest as T;
            if (typed == null)
            {
                failures.Add("字段拷贝失败(目标组件类型不符或为 null):" + typeName + ":" + destPath);
                return false;
            }

            return true;
        }

        /// <summary>
        /// **显式字段拷贝的分发点**：只认 <see cref="IsExplicitCopySupported"/> 里的类型，
        /// 每种类型一个独立函数（字段清单可枚举、可核对）。未匹配到实现时记失败（fail closed）。
        /// </summary>
        private static bool TryCopyComponentFields(Component source, Component dest,
                                                  MapCopyContext context, List<string> failures,
                                                  string destPath)
        {
            int before = failures.Count;

            MeshFilter meshFilter = source as MeshFilter;
            if (meshFilter != null)
            {
                MeshFilter target;
                if (!RequireDest(dest, "MeshFilter", destPath, failures, out target))
                {
                    return false;
                }

                target.sharedMesh = meshFilter.sharedMesh;
                if (!ReferenceEquals(target.sharedMesh, meshFilter.sharedMesh))
                {
                    failures.Add(FieldFailure("MeshFilter", "sharedMesh", destPath));
                }

                return failures.Count == before;
            }

            MeshRenderer meshRenderer = source as MeshRenderer;
            if (meshRenderer != null)
            {
                return CopyMeshRendererFields(meshRenderer, dest, context, failures, destPath);
            }

            LODGroup lodGroup = source as LODGroup;
            if (lodGroup != null)
            {
                return CopyLodGroupFields(lodGroup, dest, context, failures, destPath);
            }

            BoxCollider boxCollider = source as BoxCollider;
            if (boxCollider != null)
            {
                return CopyBoxColliderFields(boxCollider, dest, failures, destPath);
            }

            SphereCollider sphereCollider = source as SphereCollider;
            if (sphereCollider != null)
            {
                return CopySphereColliderFields(sphereCollider, dest, failures, destPath);
            }

            CapsuleCollider capsuleCollider = source as CapsuleCollider;
            if (capsuleCollider != null)
            {
                return CopyCapsuleColliderFields(capsuleCollider, dest, failures, destPath);
            }

            MeshCollider meshCollider = source as MeshCollider;
            if (meshCollider != null)
            {
                return CopyMeshColliderFields(meshCollider, dest, failures, destPath);
            }

            Rigidbody rigidbody = source as Rigidbody;
            if (rigidbody != null)
            {
                return CopyRigidbodyFields(rigidbody, dest, failures, destPath);
            }

            failures.Add("字段拷贝失败(没有匹配的实现):" + source.GetType().Name + ":" + destPath
                         + "（调用方本应在 AddComponent 之前用 IsExplicitCopySupported 拒绝它）");
            return false;
        }

        // -------------------------------------------------------------------- Renderer / MeshRenderer

        private static bool CopyMeshRendererFields(MeshRenderer source, Component dest,
                                                   MapCopyContext context, List<string> failures,
                                                   string destPath)
        {
            MeshRenderer target;
            if (!RequireDest(dest, "MeshRenderer", destPath, failures, out target))
            {
                return false;
            }

            if (!CopyRendererCommonFields(source, target, context, failures, destPath))
            {
                return false;
            }

            // 历史动态批处理注入的"额外顶点流"：没有可搬运的公开语义，地图侧也从不设置它。
            // 源上真有值时**显式失败**，而不是静默丢掉（"不静默丢字段"是硬要求）。
            if (source.additionalVertexStreams != null)
            {
                failures.Add("字段不支持拷贝:MeshRenderer.additionalVertexStreams:" + destPath
                             + "（源上非空；无法通过公开 API 复制该内容）");
                return false;
            }

            return true;
        }

        /// <summary>
        /// `Renderer` 的公共表现字段（`MeshRenderer`/`SkinnedMeshRenderer` 共用）。字段清单是**穷举**的：
        /// 刻意不拷的是"批处理元数据"（`m_StaticBatchInfo`/`m_StaticBatchRoot`/`m_SubsetIndices` 之类）
        /// —— 它们不是外观、没有公开 API、而且静态合批根会指向源场景；新建组件保持默认（未合批）
        /// 才是烘焙产物该有的状态。
        ///
        /// `probeAnchor` / `lightProbeProxyVolume` 是**对象引用**：前者按映射重绑定到目标 Transform，
        /// 后者不在允许集内因而非空即失败 —— 两者都**绝不**指回源场景。
        /// </summary>
        private static bool CopyRendererCommonFields(Renderer source, Renderer dest,
                                                     MapCopyContext context, List<string> failures,
                                                     string destPath)
        {
            Material[] sourceMaterials = source.sharedMaterials;
            if (sourceMaterials == null)
            {
                failures.Add("字段拷贝失败:Renderer.sharedMaterials 源为 null:" + destPath);
                return false;
            }

            Material[] materialsCopy = new Material[sourceMaterials.Length];
            for (int i = 0; i < sourceMaterials.Length; i++)
            {
                materialsCopy[i] = sourceMaterials[i];
            }

            dest.sharedMaterials = materialsCopy;

            dest.enabled = source.enabled;
            dest.shadowCastingMode = source.shadowCastingMode;
            dest.receiveShadows = source.receiveShadows;
            dest.motionVectorGenerationMode = source.motionVectorGenerationMode;
            dest.lightProbeUsage = source.lightProbeUsage;
            dest.reflectionProbeUsage = source.reflectionProbeUsage;
            dest.allowOcclusionWhenDynamic = source.allowOcclusionWhenDynamic;
            dest.sortingLayerID = source.sortingLayerID;
            dest.sortingOrder = source.sortingOrder;
            dest.renderingLayerMask = source.renderingLayerMask;
            dest.rayTracingMode = source.rayTracingMode;
            dest.rendererPriority = source.rendererPriority;

            // 说明边界：`Renderer` 还有一个“指向 LightProbeProxyVolume 组件”的序列化引用，
            // 但它在 Unity 2019.4 的 CoreModule 里**没有可用的公开读写属性**（实测 `Renderer`
            // 上找不到该成员，编译不过），本轮又明令禁止用通用序列化写入去搬它。
            // 因此这条改成**源数据门**：`Tools/PMBattleContentSceneFactsCheck` §H 断言源场景里
            // `m_LightProbeProxyVolume` 要么不出现、要么是 `{fileID: 0}`（当前实测为 0 处）。
            // 一旦源上真出现非空值，门禁会失败，而不是静默丢掉那个引用。
            if (source.probeAnchor != null)
            {
                if (context == null)
                {
                    failures.Add("字段不支持拷贝:Renderer.probeAnchor:" + destPath
                                 + "（缺少源→目标映射上下文）");
                    return false;
                }

                // **只排队，绝不在此刻解析**（第五轮窄修正）：本函数是在 AddComponent 之后**立刻**
                // 被调用的，此刻同一棵目标子树**可能还没搭完**。源里 probeAnchor 指向一个"后建的"
                // 后代节点是完全合法的用法，那一瞬间 `NodeMap` 里当然还没有它。
                // 修正前在这里查表，于是"构建顺序"被误判成"引用在本层级之外"，对合法源数据**必误拒**。
                // 解析统一交给整棵目标子树建完之后的 `ResolveProbeAnchorFixup`：那时查不到，
                // 才真的是跨树 / 不存在的引用，才该失败 —— fail closed 的口径不变（仍不放宽成"指回源"）。
                context.Fixups.Add(new DeferredReferenceFixup
                {
                    Kind = "Renderer.probeAnchor",
                    Source = source,
                    Dest = dest,
                    TargetPath = destPath,
                });
            }

            // ---- 回读核对：拷了哪些字段 = 可核对的事实
            bool ok = true;

            Material[] destMaterials = dest.sharedMaterials;
            if (destMaterials.Length != sourceMaterials.Length)
            {
                failures.Add(FieldFailure("Renderer",
                                          "sharedMaterials.Length（目标="
                                          + destMaterials.Length.ToString(CultureInfo.InvariantCulture)
                                          + " 源=" + sourceMaterials.Length.ToString(CultureInfo.InvariantCulture) + "）",
                                          destPath));
                ok = false;
            }
            else
            {
                for (int i = 0; i < sourceMaterials.Length; i++)
                {
                    if (!ReferenceEquals(destMaterials[i], sourceMaterials[i]))
                    {
                        failures.Add(FieldFailure("Renderer",
                                                  "sharedMaterials[" + i.ToString(CultureInfo.InvariantCulture) + "]",
                                                  destPath));
                        ok = false;
                        break;
                    }
                }
            }

            if (dest.enabled != source.enabled)
            {
                failures.Add(FieldFailure("Renderer", "enabled", destPath));
                ok = false;
            }

            if (dest.shadowCastingMode != source.shadowCastingMode)
            {
                failures.Add(FieldFailure("Renderer", "shadowCastingMode", destPath));
                ok = false;
            }

            if (dest.receiveShadows != source.receiveShadows)
            {
                failures.Add(FieldFailure("Renderer", "receiveShadows", destPath));
                ok = false;
            }

            if (dest.motionVectorGenerationMode != source.motionVectorGenerationMode)
            {
                failures.Add(FieldFailure("Renderer", "motionVectorGenerationMode", destPath));
                ok = false;
            }

            if (dest.lightProbeUsage != source.lightProbeUsage)
            {
                failures.Add(FieldFailure("Renderer", "lightProbeUsage", destPath));
                ok = false;
            }

            if (dest.reflectionProbeUsage != source.reflectionProbeUsage)
            {
                failures.Add(FieldFailure("Renderer", "reflectionProbeUsage", destPath));
                ok = false;
            }

            if (dest.allowOcclusionWhenDynamic != source.allowOcclusionWhenDynamic)
            {
                failures.Add(FieldFailure("Renderer", "allowOcclusionWhenDynamic", destPath));
                ok = false;
            }

            if (dest.sortingLayerID != source.sortingLayerID)
            {
                failures.Add(FieldFailure("Renderer", "sortingLayerID", destPath));
                ok = false;
            }

            if (dest.sortingOrder != source.sortingOrder)
            {
                failures.Add(FieldFailure("Renderer", "sortingOrder", destPath));
                ok = false;
            }

            if (dest.renderingLayerMask != source.renderingLayerMask)
            {
                failures.Add(FieldFailure("Renderer", "renderingLayerMask", destPath));
                ok = false;
            }

            if (dest.rayTracingMode != source.rayTracingMode)
            {
                failures.Add(FieldFailure("Renderer", "rayTracingMode", destPath));
                ok = false;
            }

            if (dest.rendererPriority != source.rendererPriority)
            {
                failures.Add(FieldFailure("Renderer", "rendererPriority", destPath));
                ok = false;
            }

            return ok;
        }

        // -------------------------------------------------------------------- LODGroup

        /// <summary>
        /// `LODGroup` 的非引用字段就地拷贝；**Renderer 数组延后**（见
        /// <see cref="ResolveDeferredReferences"/>）—— 那些 Renderer 属于本层级的子节点，
        /// 此刻还没建出来。契约要求"在完整目标层级建完以后按源→目标映射重绑定"，就是这件事。
        /// </summary>
        private static bool CopyLodGroupFields(LODGroup source, Component dest, MapCopyContext context,
                                              List<string> failures, string destPath)
        {
            LODGroup target;
            if (!RequireDest(dest, "LODGroup", destPath, failures, out target))
            {
                return false;
            }

            if (context == null)
            {
                failures.Add("字段不支持拷贝:LODGroup:" + destPath
                             + "（缺少源→目标映射上下文，Renderer 引用无法重绑定）");
                return false;
            }

            target.enabled = source.enabled;
            target.fadeMode = source.fadeMode;
            target.animateCrossFading = source.animateCrossFading;
            target.localReferencePoint = source.localReferencePoint;
            target.size = source.size;

            if (source.lodCount <= 0)
            {
                failures.Add("字段拷贝失败:LODGroup.lodCount=0:" + destPath
                             + "（C1 回读校验要求 LODGroup 至少有一个 LOD 级别）");
                return false;
            }

            context.Fixups.Add(new DeferredReferenceFixup
            {
                Kind = "LODGroup",
                Source = source,
                Dest = target,
                TargetPath = destPath,
            });

            bool ok = true;

            if (target.fadeMode != source.fadeMode)
            {
                failures.Add(FieldFailure("LODGroup", "fadeMode", destPath));
                ok = false;
            }

            if (target.animateCrossFading != source.animateCrossFading)
            {
                failures.Add(FieldFailure("LODGroup", "animateCrossFading", destPath));
                ok = false;
            }

            if (target.localReferencePoint != source.localReferencePoint)
            {
                failures.Add(FieldFailure("LODGroup", "localReferencePoint", destPath));
                ok = false;
            }

            if (target.size != source.size)
            {
                failures.Add(FieldFailure("LODGroup", "size", destPath));
                ok = false;
            }

            return ok;
        }

        // -------------------------------------------------------------------- Collider 家族

        /// <summary>
        /// `Collider` 公共字段。**放在形状字段之后调用**：本函数会写 `enabled`，
        /// 而形状字段的写入刻意在 disabled 状态下进行（避免 transient invalid 配置）。
        /// </summary>
        private static bool CopyColliderCommonFields(Collider source, Collider dest,
                                                     List<string> failures, string destPath)
        {
            dest.enabled = source.enabled;
            dest.isTrigger = source.isTrigger;
            dest.sharedMaterial = source.sharedMaterial;
            dest.contactOffset = source.contactOffset;

            bool ok = true;

            if (dest.enabled != source.enabled)
            {
                failures.Add(FieldFailure("Collider", "enabled", destPath));
                ok = false;
            }

            if (dest.isTrigger != source.isTrigger)
            {
                failures.Add(FieldFailure("Collider", "isTrigger", destPath));
                ok = false;
            }

            if (!ReferenceEquals(dest.sharedMaterial, source.sharedMaterial))
            {
                failures.Add(FieldFailure("Collider", "sharedMaterial", destPath));
                ok = false;
            }

            if (dest.contactOffset != source.contactOffset)
            {
                failures.Add(FieldFailure("Collider", "contactOffset", destPath));
                ok = false;
            }

            return ok;
        }

        private static bool CopyBoxColliderFields(BoxCollider source, Component dest,
                                                  List<string> failures, string destPath)
        {
            BoxCollider target;
            if (!RequireDest(dest, "BoxCollider", destPath, failures, out target))
            {
                return false;
            }

            target.enabled = false;   // 配置期间保持 disabled，避免中间态
            target.center = source.center;
            target.size = source.size;

            if (target.center != source.center)
            {
                failures.Add(FieldFailure("BoxCollider", "center", destPath));
                return false;
            }

            if (target.size != source.size)
            {
                failures.Add(FieldFailure("BoxCollider", "size", destPath));
                return false;
            }

            return CopyColliderCommonFields(source, target, failures, destPath);
        }

        private static bool CopySphereColliderFields(SphereCollider source, Component dest,
                                                     List<string> failures, string destPath)
        {
            SphereCollider target;
            if (!RequireDest(dest, "SphereCollider", destPath, failures, out target))
            {
                return false;
            }

            target.enabled = false;
            target.center = source.center;
            target.radius = source.radius;

            if (target.center != source.center)
            {
                failures.Add(FieldFailure("SphereCollider", "center", destPath));
                return false;
            }

            if (target.radius != source.radius)
            {
                failures.Add(FieldFailure("SphereCollider", "radius", destPath));
                return false;
            }

            return CopyColliderCommonFields(source, target, failures, destPath);
        }

        private static bool CopyCapsuleColliderFields(CapsuleCollider source, Component dest,
                                                      List<string> failures, string destPath)
        {
            CapsuleCollider target;
            if (!RequireDest(dest, "CapsuleCollider", destPath, failures, out target))
            {
                return false;
            }

            target.enabled = false;
            target.center = source.center;
            target.radius = source.radius;
            target.height = source.height;
            target.direction = source.direction;

            if (target.center != source.center)
            {
                failures.Add(FieldFailure("CapsuleCollider", "center", destPath));
                return false;
            }

            if (target.radius != source.radius)
            {
                failures.Add(FieldFailure("CapsuleCollider", "radius", destPath));
                return false;
            }

            if (target.height != source.height)
            {
                failures.Add(FieldFailure("CapsuleCollider", "height", destPath));
                return false;
            }

            if (target.direction != source.direction)
            {
                failures.Add(FieldFailure("CapsuleCollider", "direction", destPath));
                return false;
            }

            return CopyColliderCommonFields(source, target, failures, destPath);
        }

        /// <summary>
        /// `MeshCollider` 的赋值顺序是**刻意的**（避免 transient invalid 配置）：
        /// 先 `enabled = false` → `sharedMesh` → `convex` → `cookingOptions` → 公共字段（最后恢复 enabled）。
        /// 这样"启用的 collider 处于非法 mesh/convex 组合"这个中间态从来不存在
        /// （真机日志里出现过这类瞬时配置引发的 Unity 内部报错）。
        /// </summary>
        private static bool CopyMeshColliderFields(MeshCollider source, Component dest,
                                                   List<string> failures, string destPath)
        {
            MeshCollider target;
            if (!RequireDest(dest, "MeshCollider", destPath, failures, out target))
            {
                return false;
            }

            if (source.sharedMesh == null)
            {
                failures.Add("字段拷贝失败:MeshCollider.sharedMesh 源为 null:" + destPath
                             + "（C2 会把缺网格的碰撞体当几何缺失）");
                return false;
            }

            target.enabled = false;
            target.sharedMesh = source.sharedMesh;
            target.convex = source.convex;
            target.cookingOptions = source.cookingOptions;

            if (!ReferenceEquals(target.sharedMesh, source.sharedMesh))
            {
                failures.Add(FieldFailure("MeshCollider", "sharedMesh", destPath));
                return false;
            }

            if (target.convex != source.convex)
            {
                failures.Add(FieldFailure("MeshCollider", "convex", destPath));
                return false;
            }

            if (target.cookingOptions != source.cookingOptions)
            {
                failures.Add(FieldFailure("MeshCollider", "cookingOptions", destPath));
                return false;
            }

            return CopyColliderCommonFields(source, target, failures, destPath);
        }

        // -------------------------------------------------------------------- Rigidbody（仅 kinematic）

        private static bool CopyRigidbodyFields(Rigidbody source, Component dest,
                                                List<string> failures, string destPath)
        {
            Rigidbody target;
            if (!RequireDest(dest, "Rigidbody", destPath, failures, out target))
            {
                return false;
            }

            if (!source.isKinematic)
            {
                failures.Add("字段拷贝失败:Rigidbody 源不是 kinematic:" + destPath
                             + "（C1 白名单与 C2 允许集都只允许 kinematic 刚体）");
                return false;
            }

            target.mass = source.mass;
            target.drag = source.drag;
            target.angularDrag = source.angularDrag;
            target.useGravity = source.useGravity;
            target.isKinematic = source.isKinematic;
            target.constraints = source.constraints;
            target.collisionDetectionMode = source.collisionDetectionMode;
            target.interpolation = source.interpolation;
            target.detectCollisions = source.detectCollisions;
            target.maxAngularVelocity = source.maxAngularVelocity;
            target.centerOfMass = source.centerOfMass;
            target.inertiaTensor = source.inertiaTensor;
            target.inertiaTensorRotation = source.inertiaTensorRotation;

            bool ok = true;

            if (!target.isKinematic)
            {
                failures.Add(FieldFailure("Rigidbody", "isKinematic", destPath));
                ok = false;
            }

            if (target.mass != source.mass)
            {
                failures.Add(FieldFailure("Rigidbody", "mass", destPath));
                ok = false;
            }

            if (target.drag != source.drag)
            {
                failures.Add(FieldFailure("Rigidbody", "drag", destPath));
                ok = false;
            }

            if (target.angularDrag != source.angularDrag)
            {
                failures.Add(FieldFailure("Rigidbody", "angularDrag", destPath));
                ok = false;
            }

            if (target.useGravity != source.useGravity)
            {
                failures.Add(FieldFailure("Rigidbody", "useGravity", destPath));
                ok = false;
            }

            if (target.constraints != source.constraints)
            {
                failures.Add(FieldFailure("Rigidbody", "constraints", destPath));
                ok = false;
            }

            if (target.collisionDetectionMode != source.collisionDetectionMode)
            {
                failures.Add(FieldFailure("Rigidbody", "collisionDetectionMode", destPath));
                ok = false;
            }

            if (target.interpolation != source.interpolation)
            {
                failures.Add(FieldFailure("Rigidbody", "interpolation", destPath));
                ok = false;
            }

            if (target.detectCollisions != source.detectCollisions)
            {
                failures.Add(FieldFailure("Rigidbody", "detectCollisions", destPath));
                ok = false;
            }

            if (target.maxAngularVelocity != source.maxAngularVelocity)
            {
                failures.Add(FieldFailure("Rigidbody", "maxAngularVelocity", destPath));
                ok = false;
            }

            if (target.centerOfMass != source.centerOfMass)
            {
                failures.Add(FieldFailure("Rigidbody", "centerOfMass", destPath));
                ok = false;
            }

            if (target.inertiaTensor != source.inertiaTensor)
            {
                failures.Add(FieldFailure("Rigidbody", "inertiaTensor", destPath));
                ok = false;
            }

            if (target.inertiaTensorRotation != source.inertiaTensorRotation)
            {
                failures.Add(FieldFailure("Rigidbody", "inertiaTensorRotation", destPath));
                ok = false;
            }

            return ok;
        }

        // -------------------------------------------------------------------- 延后重绑定

        /// <summary>
        /// 兑现延后重绑定（在**整棵目标子树建完之后**调用一次）：
        ///   · `LODGroup`：按源→目标映射把每一级的 Renderer 数组重写成**目标**对象上的渲染器；
        ///   · `Renderer.probeAnchor`：确认最终值指向目标层级内的 Transform。
        /// 任何映射不到的引用都让烘焙失败 —— **绝不**把引用指回源场景。
        /// </summary>
        private static bool ResolveDeferredReferences(MapCopyContext context, List<string> failures)
        {
            if (context == null)
            {
                return true;
            }

            int before = failures.Count;
            for (int i = 0; i < context.Fixups.Count; i++)
            {
                DeferredReferenceFixup fixup = context.Fixups[i];
                if (string.Equals(fixup.Kind, "LODGroup", StringComparison.Ordinal))
                {
                    ResolveLodGroupFixup(fixup, context, failures);
                }
                else if (string.Equals(fixup.Kind, "Renderer.probeAnchor", StringComparison.Ordinal))
                {
                    ResolveProbeAnchorFixup(fixup, context, failures);
                }
                else
                {
                    failures.Add("延后重绑定失败(未知 Kind):" + (fixup.Kind == null ? "<null>" : fixup.Kind)
                                 + ":" + fixup.TargetPath);
                }
            }

            return failures.Count == before;
        }

        private static void ResolveLodGroupFixup(DeferredReferenceFixup fixup, MapCopyContext context,
                                                List<string> failures)
        {
            LODGroup source = fixup.Source as LODGroup;
            LODGroup dest = fixup.Dest as LODGroup;
            if (source == null || dest == null)
            {
                failures.Add("延后重绑定失败(LODGroup 类型不符):" + fixup.TargetPath);
                return;
            }

            LOD[] sourceLods = source.GetLODs();
            if (sourceLods == null || sourceLods.Length == 0)
            {
                failures.Add("延后重绑定失败(LODGroup 源没有 LOD 级别):" + fixup.TargetPath);
                return;
            }

            LOD[] targetLods = new LOD[sourceLods.Length];

            // **任一 Renderer 解析失败就绝不 SetLODs**（第五轮窄修正）：带着 null / 残缺的数组去
            // `SetLODs` 等于把半成品 LOD 配置写进产物（编辑器还会为此抛 ArgumentException），
            // 那正是"看起来烘焙成功、内容却是错的"。这里只负责记账 + 整体放弃这个 LODGroup。
            bool mappingComplete = true;

            for (int level = 0; level < sourceLods.Length; level++)
            {
                Renderer[] sourceRenderers = sourceLods[level].renderers;
                Renderer[] targetRenderers = new Renderer[sourceRenderers == null ? 0 : sourceRenderers.Length];

                for (int r = 0; r < targetRenderers.Length; r++)
                {
                    Renderer sourceRenderer = sourceRenderers[r];
                    if (sourceRenderer == null)
                    {
                        failures.Add("延后重绑定失败(LODGroup 第 "
                                     + level.ToString(CultureInfo.InvariantCulture)
                                     + " 级存在空 Renderer):" + fixup.TargetPath);
                        mappingComplete = false;
                        continue;
                    }

                    Transform targetTransform;
                    if (!context.NodeMap.TryGetValue(sourceRenderer.transform, out targetTransform)
                        || targetTransform == null)
                    {
                        failures.Add("延后重绑定失败(LODGroup Renderer 不在本层级映射内):" + fixup.TargetPath
                                     + " renderer=\"" + PathOf(sourceRenderer.gameObject) + "\"");
                        mappingComplete = false;
                        continue;
                    }

                    Renderer mapped = targetTransform.GetComponent(sourceRenderer.GetType()) as Renderer;
                    if (mapped == null)
                    {
                        failures.Add("延后重绑定失败(目标对象上没有对应 Renderer):" + fixup.TargetPath
                                     + " renderer=\"" + PathOf(sourceRenderer.gameObject)
                                     + "\" 类型=" + sourceRenderer.GetType().Name);
                        mappingComplete = false;
                        continue;
                    }

                    targetRenderers[r] = mapped;
                }

                targetLods[level] = sourceLods[level];
                targetLods[level].renderers = targetRenderers;
            }

            if (!mappingComplete)
            {
                failures.Add("延后重绑定失败(LODGroup 存在无法重绑定的 Renderer，已整体放弃该 LODGroup 的 SetLODs):"
                             + fixup.TargetPath);
                return;
            }

            // `localReferencePoint` / `size` 在 `CopyLodGroupFields` 里已按源逐字段拷过，是产物必须
            // 保持的内容。**第五轮窄修正：这里不再 RecalculateBounds()** —— 那会用 Renderer 的
            // 实际包围盒把这两个值覆写掉，等于用 Unity 自己算的近似值悄悄毁掉"显式拷贝 + 回读核对"。
            // 但 `SetLODs` 自身是否有副作用没有文档保证，因此不假设它不动这两个字段：
            // 写完 LOD 数组后**显式恢复**源的 size / localReferencePoint，再回读核对。
            Vector3 sourceReferencePoint = source.localReferencePoint;
            float sourceSize = source.size;

            dest.SetLODs(targetLods);
            dest.localReferencePoint = sourceReferencePoint;
            dest.size = sourceSize;

            if (dest.localReferencePoint != sourceReferencePoint)
            {
                failures.Add(FieldFailure("LODGroup", "localReferencePoint（SetLODs 之后恢复失败）",
                                          fixup.TargetPath));
            }

            if (dest.size != sourceSize)
            {
                failures.Add(FieldFailure("LODGroup", "size（SetLODs 之后恢复失败）", fixup.TargetPath));
            }

            // ---- 回读核对：每级的 Renderer 必须与"按映射换算过的源序列"逐一对应，
            //      而且**绝不能**还留在源场景里。
            LOD[] verify = dest.GetLODs();
            if (verify.Length != sourceLods.Length)
            {
                failures.Add("延后重绑定失败(LODGroup 级别数不一致):" + fixup.TargetPath
                             + "（目标=" + verify.Length.ToString(CultureInfo.InvariantCulture)
                             + " 源=" + sourceLods.Length.ToString(CultureInfo.InvariantCulture) + "）");
                return;
            }

            Scene destScene = dest.gameObject.scene;
            for (int level = 0; level < sourceLods.Length; level++)
            {
                Renderer[] expected = targetLods[level].renderers;
                Renderer[] actual = verify[level].renderers;

                if (actual == null || actual.Length != expected.Length)
                {
                    failures.Add("延后重绑定失败(LODGroup 第 "
                                 + level.ToString(CultureInfo.InvariantCulture)
                                 + " 级 Renderer 数量不一致):" + fixup.TargetPath);
                    continue;
                }

                for (int r = 0; r < expected.Length; r++)
                {
                    if (!ReferenceEquals(actual[r], expected[r]))
                    {
                        failures.Add("延后重绑定失败(LODGroup 第 "
                                     + level.ToString(CultureInfo.InvariantCulture)
                                     + " 级 Renderer[" + r.ToString(CultureInfo.InvariantCulture)
                                     + "] 未指向目标对象):" + fixup.TargetPath);
                        break;
                    }

                    if (actual[r] != null && actual[r].gameObject.scene != destScene)
                    {
                        failures.Add("延后重绑定失败(LODGroup 第 "
                                     + level.ToString(CultureInfo.InvariantCulture)
                                     + " 级 Renderer[" + r.ToString(CultureInfo.InvariantCulture)
                                     + "] 仍指向源场景):" + fixup.TargetPath);
                        break;
                    }
                }
            }
        }

        private static void ResolveProbeAnchorFixup(DeferredReferenceFixup fixup, MapCopyContext context,
                                                   List<string> failures)
        {
            Renderer source = fixup.Source as Renderer;
            Renderer dest = fixup.Dest as Renderer;
            if (source == null || dest == null)
            {
                failures.Add("延后重绑定失败(Renderer 类型不符):" + fixup.TargetPath);
                return;
            }

            if (source.probeAnchor == null)
            {
                return;
            }

            Transform mapped;
            if (!context.NodeMap.TryGetValue(source.probeAnchor, out mapped) || mapped == null)
            {
                failures.Add("延后重绑定失败(probeAnchor 映射不到目标):" + fixup.TargetPath);
                return;
            }

            dest.probeAnchor = mapped;
            if (dest.probeAnchor != mapped)
            {
                failures.Add(FieldFailure("Renderer", "probeAnchor", fixup.TargetPath));
            }
        }

        // ==================================================================== 保存 prefab（真实 API）

        private static bool SavePrefab(GameObject root, string assetPath, out bool saved,
                                       StringBuilder summary)
        {
            saved = false;

            bool success;
            GameObject result;
            try
            {
                result = PrefabUtility.SaveAsPrefabAsset(root, assetPath, out success);
            }
            catch (Exception ex)
            {
                summary.AppendLine("  保存 prefab 异常（" + assetPath + "）："
                                   + ex.GetType().Name + " " + ex.Message);
                return false;
            }

            if (!success || result == null)
            {
                summary.AppendLine("  PrefabUtility.SaveAsPrefabAsset 报告失败：" + assetPath);
                return false;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

            if (AssetDatabase.LoadAssetAtPath<GameObject>(assetPath) == null)
            {
                summary.AppendLine("  保存后立刻回读不到 prefab（说明导入失败）：" + assetPath);
                return false;
            }

            saved = true;
            summary.AppendLine("  已保存 prefab：" + assetPath);
            return true;
        }

        // ==================================================================== manifest（发布信号）

        /// <summary>
        /// 清除 manifest（发布信号），并且**自证确实清掉了**。
        ///
        /// 为什么不能只写一句 `AssetDatabase.DeleteAsset`（这是复核修掉的一处发布缺陷）：
        /// 那一步失败时它不一定会抛异常，于是"烘焙失败"可能留下一个**仍然有效的旧 manifest** ——
        /// 消费者（C2/C3）会照旧接受它，而磁盘上的 prefab 已经被本次烘焙覆盖过（digest 与实际内容
        /// 不再对应）。契约要求"失败不能留下 valid manifest"，所以这里必须自证：
        ///   1) `AssetDatabase.DeleteAsset` + `Refresh`；
        ///   2) 文件还在 ⇒ `File.Delete` + `Refresh`；
        ///   3) 还在 ⇒ 写入**无效占位 manifest**（formatVersion=0，不可能通过 C2 的强校验），
        ///      并**再解析一次确认它确实不合法**；连这一步都失败就返回 false（调用方必须中止）。
        /// 注意：本方法只处理 manifest（发布信号）；两个 prefab 不在这里删 —— 没有 manifest 的
        /// prefab 不可消费（C2 会以"缺资源"失败），所以它们残留无害。
        /// </summary>
        private static bool RemoveManifestOrInvalidate(StringBuilder summary, out string error)
        {
            error = null;

            string absolute = AbsolutePath(ManifestAssetPath);
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(ManifestAssetPath) != null
                || File.Exists(absolute))
            {
                AssetDatabase.DeleteAsset(ManifestAssetPath);
                AssetDatabase.Refresh();
            }

            if (!File.Exists(absolute))
            {
                return true;
            }

            try
            {
                File.Delete(absolute);
                AssetDatabase.Refresh();
            }
            catch (Exception ex)
            {
                if (summary != null)
                {
                    summary.AppendLine("  File.Delete 失败（" + ManifestAssetPath + "）："
                                       + ex.GetType().Name + " " + ex.Message);
                }
            }

            if (!File.Exists(absolute))
            {
                return true;
            }

            // 最后一道：让它**不再是一个有效发布信号**（格式非法 ⇒ C2 的 Validate 必拒）。
            const string poisoned =
                "{\"formatVersion\":0,\"mapId\":\"invalid\",\"seed\":0,\"mapResource\":\"\","
                + "\"playerResource\":\"\",\"contentDigest\":\"\",\"collisionDigest\":0,"
                + "\"worldVersion\":0}";

            try
            {
                File.WriteAllText(absolute, poisoned, new UTF8Encoding(false));
                AssetDatabase.ImportAsset(ManifestAssetPath, ImportAssetOptions.ForceSynchronousImport);
            }
            catch (Exception ex)
            {
                error = "无法删除、也无法作废 manifest（" + ManifestAssetPath + "）："
                        + ex.GetType().Name + " " + ex.Message
                        + "。请手工删除该文件后重试，否则它会被后来者当成有效内容。";
                return false;
            }

            PMBattleContentManifest poisonedManifest;
            string parseError;
            if (PMBattleContentManifest.TryParseJson(poisoned, out poisonedManifest, out parseError))
            {
                error = "无法删除 manifest，且写入的无效占位竟通过了 C2 强校验（" + ManifestAssetPath
                        + "）：拒绝继续，请手工删除该文件。";
                return false;
            }

            if (summary != null)
            {
                summary.AppendLine("  **警告**：" + ManifestAssetPath
                                   + " 无法删除，已写入**无效占位**（formatVersion=0）：它不会再被当成"
                                   + "有效发布信号（C2 强校验必拒），但请手工删除该文件。");
            }

            return true;
        }

        private static bool WriteManifest(string contentDigest, uint collisionDigest, int worldVersion,
                                         StringBuilder summary)
        {
            // 直接复用 C2 的类型：字段名/类型/顺序就是协议，自己另写一个类就是制造第二份口径。
            PMBattleContentManifest manifest = new PMBattleContentManifest();
            manifest.formatVersion = PMBattleContentManifest.ExpectedFormatVersion;
            manifest.mapId = PMBattleContentManifest.ExpectedMapId;
            manifest.seed = PMBattleContentManifest.ExpectedSeed;
            manifest.mapResource = PMBattleContentManifest.MapResourceKey;
            manifest.playerResource = PMBattleContentManifest.PlayerResourceKey;
            manifest.contentDigest = contentDigest;
            manifest.collisionDigest = collisionDigest;
            manifest.worldVersion = worldVersion;

            string json = JsonUtility.ToJson(manifest);

            string absolute = AbsolutePath(ManifestAssetPath);
            try
            {
                // UTF-8 **不带 BOM**（JsonUtility 读的是纯文本；BOM 会被当成内容首字符的风险不值得冒）。
                File.WriteAllText(absolute, json, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                summary.AppendLine("  写 manifest 失败：" + ex.GetType().Name + " " + ex.Message);
                return false;
            }

            AssetDatabase.ImportAsset(ManifestAssetPath, ImportAssetOptions.ForceSynchronousImport);

            TextAsset written = AssetDatabase.LoadAssetAtPath<TextAsset>(ManifestAssetPath);
            if (written == null)
            {
                summary.AppendLine("  manifest 写盘后不是 TextAsset（扩展名/导入设置问题）：" + ManifestAssetPath);
                return false;
            }

            PMBattleContentManifest reparsed;
            string parseError;
            if (!PMBattleContentManifest.TryParseJson(written.text, out reparsed, out parseError))
            {
                summary.AppendLine("  写盘后的 manifest 无法被 C2 规则解析/校验：" + parseError);
                return false;
            }

            summary.AppendLine("  已发布 manifest（发布信号，最后一步）：" + json);
            return true;
        }

        // ==================================================================== 回读校验

        /// <summary>
        /// 地图 prefab 回读校验：把**磁盘上的产物**重新走一遍结构检查 + 落点逐一比对。
        /// 之所以要回读：内存里的层级正确 != 保存后的资产正确（保存会走 Unity 的序列化）。
        ///
        /// F7 新增（与 C2 加载要求对齐，并在构建期就把它钉成失败）：
        ///   · tile 根**全部**处于激活状态（默认跳过策略下这是不变量：未激活模板的落点已在选择阶段
        ///     跳过）；开关关掉时只计数不失败；
        ///   · 记录 active/inactive 节点计数与 skippedInactive（进摘要，不静默）；
        ///   · “无非白名单组件 / Collider 均 enabled 且 activeInHierarchy”在 VerifyMapPrefabLoaded。
        /// </summary>
        private static bool VerifyMapPrefab(string assetPath, List<Placement> placements,
                                           PlacementSelection selection, StringBuilder summary,
                                           out string error)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
            {
                error = "回读 prefab 为 null";
                return false;
            }

            Transform container = prefab.transform.Find(MapContainerName);
            if (container == null)
            {
                error = "回读的 prefab 里找不到容器 \"" + MapContainerName + "\"";
                return false;
            }

            if (container.childCount != placements.Count)
            {
                error = "tile 数量不一致：资产=" + container.childCount.ToString(CultureInfo.InvariantCulture)
                        + " 期望=" + placements.Count.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            int activeTiles = 0;
            int inactiveTiles = 0;
            string firstInactiveTile = null;
            for (int i = 0; i < placements.Count; i++)
            {
                Transform tile = container.GetChild(i);
                Placement p = placements[i];
                Vector3 expectedPos = new Vector3(p.LocalX, p.LocalY, p.LocalZ);
                Quaternion expectedRot = new Quaternion(p.RotX, p.RotY, p.RotZ, p.RotW);

                if (tile.localPosition != expectedPos || tile.localRotation != expectedRot)
                {
                    error = "tile[" + i.ToString(CultureInfo.InvariantCulture) + "] 变换不一致（资产=\""
                            + tile.name + "\" pos=" + Vec(tile.localPosition) + " rot="
                            + Quat(tile.localRotation) + "；期望 pos=" + Vec(expectedPos) + " rot="
                            + Quat(expectedRot) + "）";
                    return false;
                }

                if (tile.gameObject.activeSelf)
                {
                    activeTiles++;
                }
                else
                {
                    inactiveTiles++;
                    if (firstInactiveTile == null)
                    {
                        firstInactiveTile = tile.name;
                    }
                }
            }

            // F7：默认跳过策略下“tile 根未激活”是不变量（模板 inactive 的落点根本不会被建）。
            // 它一旦出现，说明跳过策略没生效（或有人在别处把未激活状态又引回来了），必须显式失败。
            if (selection != null && selection.SkipEnabled && inactiveTiles > 0)
            {
                error = "默认跳过策略下不应存在未激活的 tile 根：未激活="
                        + inactiveTiles.ToString(CultureInfo.InvariantCulture) + "（首个=\""
                        + (firstInactiveTile == null ? "<未知>" : firstInactiveTile)
                        + "\"）：未激活对象上的组件不会走 Activate/Deactivate，正是崩溃根因，也不符合"
                        + "C2 对 Collider activeInHierarchy 的要求。";
                return false;
            }

            if (!VerifyMapPrefabLoaded(prefab, summary, out error))
            {
                return false;
            }

            summary.AppendLine("  地图 prefab 回读校验通过：tile="
                               + placements.Count.ToString(CultureInfo.InvariantCulture)
                               + "（active=" + activeTiles.ToString(CultureInfo.InvariantCulture)
                               + " inactive=" + inactiveTiles.ToString(CultureInfo.InvariantCulture) + "）"
                               + "，skippedInactive=" + (selection == null ? "<未提供>"
                                   : selection.SkippedInactive.ToString(CultureInfo.InvariantCulture))
                               + "，节点=" + prefab.transform.hierarchyCount.ToString(CultureInfo.InvariantCulture)
                               + "，Collider=" + CountColliders(prefab).ToString(CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>检查资产内部激活链；必须到达指定根，不能使用场景实例 activeInHierarchy。</summary>
        private static bool IsActiveWithinPrefabRoot(Transform node, Transform root)
        {
            if (root == null) return false;
            for (Transform current = node; current != null; current = current.parent)
            {
                if (!current.gameObject.activeSelf) return false;
                if (current == root) return true;
            }
            return false;
        }

        /// <summary>地图 prefab 的组件/碰撞体约束（与 C2 的 CollectAndValidate 同口径）。</summary>
        private static bool VerifyMapPrefabLoaded(GameObject prefab, StringBuilder summary, out string error)
        {
            error = null;

            // 这里读取的是 prefab 资产而非场景实例；只能按资产内部 activeSelf 链判断。
            if (!prefab.activeSelf)
            {
                error = "地图 prefab 根节点未激活（C2 会拒绝加载）。";
                return false;
            }

            // F7：把 active/inactive 节点计数记进摘要 —— “产物里有多少未激活节点”是可诊断信息，
            // 不能只有“Collider 都 active”这一条黑箱结论。
            Transform[] nodes = prefab.GetComponentsInChildren<Transform>(true);
            int activeNodes = 0;
            int inactiveSelfNodes = 0;
            for (int i = 0; i < nodes.Length; i++)
            {
                if (nodes[i] != null && nodes[i].gameObject.activeSelf)
                {
                    activeNodes++;
                }
                else
                {
                    inactiveSelfNodes++;
                }
            }

            Component[] components = prefab.GetComponentsInChildren<Component>(true);
            int colliders = 0;
            int triggers = 0;
            int meshFilters = 0;
            int meshRenderers = 0;
            int lodGroups = 0;
            int kinematicRigidbodies = 0;
            int renderersWithoutMaterial = 0;
            string firstRendererWithoutMaterial = null;
            int lodGroupsWithoutLevels = 0;
            string firstLodGroupWithoutLevels = null;
            Dictionary<string, int> census = new Dictionary<string, int>(StringComparer.Ordinal);

            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null)
                {
                    error = "地图 prefab 含 missing script（组件数组里的 null 项）"
                            + "：C1 必须彻底删除旧脚本，而不是禁用。";
                    return false;
                }

                string typeName = component.GetType().Name;
                int count;
                census.TryGetValue(typeName, out count);
                census[typeName] = count + 1;

                if (component is Transform)
                {
                    continue;
                }

                if (component is MeshFilter)
                {
                    meshFilters++;

                    Mesh mesh = ((MeshFilter)component).sharedMesh;
                    if (mesh == null)
                    {
                        error = "地图 MeshFilter 没有网格（对象=\"" + PathOf(component.gameObject)
                                + "\"）：MeshFilter 的 m_Mesh 引用没有拷过来，产物会不可见。";
                        return false;
                    }

                    continue;
                }

                if (component is MeshRenderer)
                {
                    meshRenderers++;

                    // 材质数组是**数组字段**，正是"数组没拷过去"最先暴露的地方：这里把
                    // "渲染器一个材质都没有"变成烘焙失败，而不是静默出一张没有材质的图。
                    Material[] materials = ((MeshRenderer)component).sharedMaterials;
                    if (!HasUsableMaterial(materials))
                    {
                        renderersWithoutMaterial++;
                        if (firstRendererWithoutMaterial == null)
                        {
                            firstRendererWithoutMaterial = PathOf(component.gameObject);
                        }
                    }

                    continue;
                }

                if (component is LODGroup)
                {
                    lodGroups++;

                    if (((LODGroup)component).lodCount <= 0)
                    {
                        lodGroupsWithoutLevels++;
                        if (firstLodGroupWithoutLevels == null)
                        {
                            firstLodGroupWithoutLevels = PathOf(component.gameObject);
                        }
                    }

                    continue;
                }

                Collider collider = component as Collider;
                if (collider != null)
                {
                    if (!collider.enabled)
                    {
                        error = "地图 Collider 未启用（对象=\"" + PathOf(collider.gameObject) + "\"）";
                        return false;
                    }

                    if (!IsActiveWithinPrefabRoot(collider.transform, prefab.transform))
                    {
                        error = "地图 Collider 所在物体未激活（对象=\"" + PathOf(collider.gameObject)
                                + "\"）：C2 要求所有 Collider enabled/active。";
                        return false;
                    }

                    if (collider.isTrigger)
                    {
                        triggers++;
                    }
                    else
                    {
                        colliders++;
                    }

                    continue;
                }

                Rigidbody rigidbody = component as Rigidbody;
                if (rigidbody != null)
                {
                    if (!rigidbody.isKinematic)
                    {
                        error = "地图含动态 Rigidbody（对象=\"" + PathOf(rigidbody.gameObject) + "\"）";
                        return false;
                    }

                    kinematicRigidbodies++;
                    continue;
                }

                error = "地图 prefab 含未允许组件类型 " + component.GetType().FullName
                        + "（对象=\"" + PathOf(component.gameObject)
                        + "\"）：C2 允许集 = Transform/MeshFilter/MeshRenderer/Collider/LODGroup/kinematic Rigidbody。";
                return false;
            }

            if (colliders == 0)
            {
                error = "地图没有任何非 trigger Collider（正式地图必须有地面+墙/障碍的真实碰撞）。";
                return false;
            }

            if (renderersWithoutMaterial > 0)
            {
                error = "地图有 " + renderersWithoutMaterial.ToString(CultureInfo.InvariantCulture)
                        + " 个 MeshRenderer 没有任何可用材质（首个对象=\""
                        + (firstRendererWithoutMaterial == null ? "<未知>" : firstRendererWithoutMaterial)
                        + "\"）：材质数组（m_Materials）没有被逐字段拷贝过去，地图会渲染成无材质/默认外观。";
                return false;
            }

            if (lodGroupsWithoutLevels > 0)
            {
                error = "地图有 " + lodGroupsWithoutLevels.ToString(CultureInfo.InvariantCulture)
                        + " 个 LODGroup 没有任何 LOD 级别（首个对象=\""
                        + (firstLodGroupWithoutLevels == null ? "<未知>" : firstLodGroupWithoutLevels)
                        + "\"）：LODGroup 的 m_LODs 数组没有被拷贝过去。";
                return false;
            }

            if (summary != null)
            {
                summary.AppendLine("  地图组件清单：无材质渲染器=" + renderersWithoutMaterial.ToString(CultureInfo.InvariantCulture)
                                   + " 无 LOD 级别的 LODGroup=" + lodGroupsWithoutLevels.ToString(CultureInfo.InvariantCulture)
                                   + " | colliders=" + colliders.ToString(CultureInfo.InvariantCulture)
                                   + " triggers=" + triggers.ToString(CultureInfo.InvariantCulture)
                                   + " meshFilters=" + meshFilters.ToString(CultureInfo.InvariantCulture)
                                   + " meshRenderers=" + meshRenderers.ToString(CultureInfo.InvariantCulture)
                                   + " lodGroups=" + lodGroups.ToString(CultureInfo.InvariantCulture)
                                   + " kinematicRigidbodies=" + kinematicRigidbodies.ToString(CultureInfo.InvariantCulture)
                                   + " | 节点 activeSelf=" + activeNodes.ToString(CultureInfo.InvariantCulture)
                                   + " inactiveSelf=" + inactiveSelfNodes.ToString(CultureInfo.InvariantCulture)
                                   + " | 类型分布=" + DescribeCounts(census));
            }

            return true;
        }

        /// <summary>角色 prefab 回读校验。</summary>
        private static bool VerifyPlayerPrefab(string assetPath, StringBuilder summary, out string error)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
            {
                error = "回读 prefab 为 null";
                return false;
            }

            return VerifyPlayerPrefabLoaded(prefab, summary, out error);
        }

        private static bool VerifyPlayerPrefabLoaded(GameObject prefab, StringBuilder summary, out string error)
        {
            error = null;

            if (!prefab.activeSelf)
            {
                error = "角色 prefab 根节点未激活（C2 会因 activeInHierarchy=false 直接抛异常）。";
                return false;
            }

            Component[] components = prefab.GetComponentsInChildren<Component>(true);
            int animators = 0;
            int renderers = 0;
            int meshFilters = 0;
            int enabledRenderersWithoutMaterial = 0;
            string firstRendererWithoutMaterial = null;
            int skinnedWithoutMeshOrBones = 0;
            string firstSkinnedProblem = null;
            Dictionary<string, int> census = new Dictionary<string, int>(StringComparer.Ordinal);

            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null)
                {
                    error = "角色 prefab 含 missing script（组件数组里的 null 项）："
                            + "C2 的 PMUnityBattlePresentation 会直接拒绝。";
                    return false;
                }

                string typeName = component.GetType().Name;
                int count;
                census.TryGetValue(typeName, out count);
                census[typeName] = count + 1;

                if (component is Transform)
                {
                    continue;
                }

                if (component is Collider)
                {
                    error = "角色 prefab 仍含 Collider（对象=\"" + PathOf(component.gameObject)
                            + "\"）：碰撞属于 DS 权威运动，表现 prefab 必须不含。";
                    return false;
                }

                if (component is Rigidbody)
                {
                    error = "角色 prefab 仍含 Rigidbody（对象=\"" + PathOf(component.gameObject) + "\"）。";
                    return false;
                }

                if (component is MonoBehaviour)
                {
                    error = "角色 prefab 仍含 MonoBehaviour（类型=" + component.GetType().FullName
                            + "，对象=\"" + PathOf(component.gameObject)
                            + "\"）：旧玩法/驱动脚本必须删除，而不是禁用。";
                    return false;
                }

                if (component is MeshFilter)
                {
                    meshFilters++;

                    // 网格引用必须真的在（否则角色只是个空壳）。
                    Mesh mesh = ((MeshFilter)component).sharedMesh;
                    if (mesh == null)
                    {
                        error = "角色 MeshFilter 没有网格（对象=\"" + PathOf(component.gameObject)
                                + "\"）：角色表现不可见。";
                        return false;
                    }

                    continue;
                }

                if (component is MeshRenderer)
                {
                    renderers++;

                    MeshRenderer meshRenderer = (MeshRenderer)component;
                    if (meshRenderer.enabled && !HasUsableMaterial(meshRenderer.sharedMaterials))
                    {
                        enabledRenderersWithoutMaterial++;
                        if (firstRendererWithoutMaterial == null)
                        {
                            firstRendererWithoutMaterial = PathOf(component.gameObject);
                        }
                    }

                    continue;
                }

                if (component is SkinnedMeshRenderer)
                {
                    renderers++;

                    // 骨骼网格是"角色可用"的另一半：网格与骨骼数组都必须真的在
                    //（骨骼对象不会被本文件删除，但字段级损坏必须在这里暴露）。
                    SkinnedMeshRenderer skinned = (SkinnedMeshRenderer)component;
                    if (skinned.sharedMesh == null || skinned.bones == null || skinned.bones.Length == 0)
                    {
                        skinnedWithoutMeshOrBones++;
                        if (firstSkinnedProblem == null)
                        {
                            firstSkinnedProblem = PathOf(component.gameObject);
                        }
                    }
                    else if (skinned.enabled && !HasUsableMaterial(skinned.sharedMaterials))
                    {
                        enabledRenderersWithoutMaterial++;
                        if (firstRendererWithoutMaterial == null)
                        {
                            firstRendererWithoutMaterial = PathOf(component.gameObject);
                        }
                    }

                    continue;
                }

                if (component is LODGroup)
                {
                    continue;
                }

                Animator animator = component as Animator;
                if (animator != null)
                {
                    animators++;
                    if (animator.applyRootMotion)
                    {
                        error = "角色 Animator.applyRootMotion=true（契约要求关闭，否则会与唯一 Transform 写者冲突）。";
                        return false;
                    }

                    continue;
                }

                error = "角色 prefab 含未允许组件类型 " + component.GetType().FullName
                        + "（对象=\"" + PathOf(component.gameObject)
                        + "\"）：C2 允许集 = Transform/MeshFilter/MeshRenderer/SkinnedMeshRenderer/Animator/LODGroup。";
                return false;
            }

            if (animators == 0)
            {
                error = "角色 prefab 没有 Animator（C2 需要它驱动 Speed）。";
                return false;
            }

            if (renderers == 0 || meshFilters == 0)
            {
                error = "角色 prefab 没有任何网格渲染（renderers=" + renderers.ToString(CultureInfo.InvariantCulture)
                        + " meshFilters=" + meshFilters.ToString(CultureInfo.InvariantCulture)
                        + "）：那不是一个可见角色。";
                return false;
            }

            if (skinnedWithoutMeshOrBones > 0)
            {
                error = "角色有 " + skinnedWithoutMeshOrBones.ToString(CultureInfo.InvariantCulture)
                        + " 个 SkinnedMeshRenderer 缺少网格或骨骼（首个对象=\""
                        + (firstSkinnedProblem == null ? "<未知>" : firstSkinnedProblem)
                        + "\"）：蒙皮网格不会跟着骨骼动，角色表现不可用。";
                return false;
            }

            if (enabledRenderersWithoutMaterial > 0)
            {
                error = "角色有 " + enabledRenderersWithoutMaterial.ToString(CultureInfo.InvariantCulture)
                        + " 个**已启用**的渲染器没有任何可用材质（首个对象=\""
                        + (firstRendererWithoutMaterial == null ? "<未知>" : firstRendererWithoutMaterial)
                        + "\"）：角色会渲染成无材质/默认外观。";
                return false;
            }

            bool hasSpeed = false;
            Animator[] allAnimators = prefab.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < allAnimators.Length && !hasSpeed; i++)
            {
                hasSpeed = ControllerDefinesFloatParameter(allAnimators[i].runtimeAnimatorController, "Speed");
            }

            if (!hasSpeed)
            {
                error = "角色 Animator 控制器里没有 Float 型 \"Speed\" 参数（C2 靠它驱动移动动画）。";
                return false;
            }

            if (summary != null)
            {
                summary.AppendLine("  角色组件清单：无材质启用渲染器=" + enabledRenderersWithoutMaterial.ToString(CultureInfo.InvariantCulture)
                                   + " 缺网格/骨骼的 SkinnedMeshRenderer=" + skinnedWithoutMeshOrBones.ToString(CultureInfo.InvariantCulture)
                                   + " | animators=" + animators.ToString(CultureInfo.InvariantCulture)
                                   + " renderers=" + renderers.ToString(CultureInfo.InvariantCulture)
                                   + " meshFilters=" + meshFilters.ToString(CultureInfo.InvariantCulture)
                                   + " 节点=" + prefab.transform.hierarchyCount.ToString(CultureInfo.InvariantCulture)
                                   + " | 类型分布=" + DescribeCounts(census));
            }

            return true;
        }

        /// <summary>Editor校验读取控制器资产定义，不依赖Animator实例是否已经初始化。</summary>
        private static bool ControllerDefinesFloatParameter(RuntimeAnimatorController runtimeController, string parameterName)
        {
            HashSet<RuntimeAnimatorController> visited = new HashSet<RuntimeAnimatorController>();
            while (runtimeController != null && visited.Add(runtimeController))
            {
                AnimatorOverrideController overrides = runtimeController as AnimatorOverrideController;
                if (overrides != null)
                {
                    runtimeController = overrides.runtimeAnimatorController;
                    continue;
                }

                global::UnityEditor.Animations.AnimatorController controller =
                    runtimeController as global::UnityEditor.Animations.AnimatorController;
                if (controller == null) return false;
                AnimatorControllerParameter[] definitions = controller.parameters;
                for (int i = 0; i < definitions.Length; i++)
                {
                    if (definitions[i].type == AnimatorControllerParameterType.Float
                        && string.Equals(definitions[i].name, parameterName, StringComparison.Ordinal))
                        return true;
                }
                return false;
            }
            return false;
        }

        // ==================================================================== 场景内查找

        /// <summary>
        /// 按路径在场景里找一个对象；0 个或多个匹配都返回 null（调用方给明确错误）。
        /// 刻意不用 GameObject.Find（它受激活状态影响且会命中别的已打开场景）。
        /// </summary>
        private static GameObject FindByPath(Scene scene, string path)
        {
            string[] segments = path.Split('/');
            if (segments.Length == 0)
            {
                return null;
            }

            GameObject[] roots = scene.GetRootGameObjects();
            GameObject current = null;

            for (int i = 0; i < roots.Length; i++)
            {
                if (!string.Equals(roots[i].name, segments[0], StringComparison.Ordinal))
                {
                    continue;
                }

                if (current != null)
                {
                    return null;    // 同名根：歧义 ⇒ 不猜
                }

                current = roots[i];
            }

            for (int s = 1; s < segments.Length && current != null; s++)
            {
                Transform next = null;
                Transform parent = current.transform;
                for (int i = 0; i < parent.childCount; i++)
                {
                    if (!string.Equals(parent.GetChild(i).name, segments[s], StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (next != null)
                    {
                        return null;
                    }

                    next = parent.GetChild(i);
                }

                current = next == null ? null : next.gameObject;
            }

            return current;
        }

        private static ScenseBuildLogic FindLogic(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                ScenseBuildLogic logic = roots[i].GetComponentInChildren<ScenseBuildLogic>(true);
                if (logic != null)
                {
                    return logic;
                }
            }

            return null;
        }

        // ==================================================================== 杂项

        /// <summary>
        /// 确保 <see cref="ResourcesDir"/> 存在。`Assets/Resources` 本身也可能不存在，因此先补父目录、
        /// 再补 `PMNet`，最后**显式确认**一次：不确认就会把失败推迟到"存 prefab 时抛一个与目录无关的错"。
        /// </summary>
        private static void EnsureResourcesDirectory()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Resources"))
            {
                AssetDatabase.CreateFolder("Assets", "Resources");
                AssetDatabase.Refresh();
            }

            if (!AssetDatabase.IsValidFolder(ResourcesDir))
            {
                AssetDatabase.CreateFolder("Assets/Resources", "PMNet");
                AssetDatabase.Refresh();
            }

            if (!AssetDatabase.IsValidFolder(ResourcesDir))
            {
                throw new InvalidOperationException("无法创建资源目录：" + ResourcesDir
                                                    + "（AssetDatabase.CreateFolder 未生效）");
            }
        }

        /// <summary>源资产是否比产物新（用于构建 hook 决定要不要重烘；不做内容比对）。</summary>
        private static bool IsStale(out string reason)
        {
            reason = null;

            string[] sources = { SourceScenePath, SourceLogicPath, SourcePlayerPath };
            string[] outputs = { MapPrefabPath, PlayerPrefabPath, ManifestAssetPath };

            DateTime oldestOutput = DateTime.MaxValue;
            for (int i = 0; i < outputs.Length; i++)
            {
                string path = AbsolutePath(outputs[i]);
                if (!File.Exists(path))
                {
                    reason = "产物缺失：" + outputs[i];
                    return true;
                }

                DateTime written = File.GetLastWriteTimeUtc(path);
                if (written < oldestOutput)
                {
                    oldestOutput = written;
                }
            }

            for (int i = 0; i < sources.Length; i++)
            {
                string path = AbsolutePath(sources[i]);
                if (!File.Exists(path))
                {
                    reason = "源资产缺失：" + sources[i];
                    return true;
                }

                if (File.GetLastWriteTimeUtc(path) > oldestOutput)
                {
                    reason = "源资产较新：" + sources[i];
                    return true;
                }
            }

            return false;
        }

        /// <summary>Assets 相对路径 → 磁盘绝对路径（Unity 的 Application.dataPath 一定是 Client/Assets）。</summary>
        private static string AbsolutePath(string assetPath)
        {
            DirectoryInfo clientDir = Directory.GetParent(Application.dataPath);
            string projectRoot = clientDir == null ? Application.dataPath : clientDir.FullName;
            return Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>渲染器是否至少有一个非 null 材质（"渲染器可用"的最小判据）。</summary>
        private static bool HasUsableMaterial(Material[] materials)
        {
            if (materials == null || materials.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] != null)
                {
                    return true;
                }
            }

            return false;
        }

        private static int CountColliders(GameObject root)
        {
            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            return colliders.Length;
        }

        private static string PathOf(GameObject go)
        {
            if (go == null)
            {
                return "<null>";
            }

            string path = go.name;
            Transform parent = go.transform.parent;
            int guard = 0;
            while (parent != null && guard++ < 64)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }

        private static void TrySetTag(GameObject go, string tag, StringBuilder summary)
        {
            if (string.IsNullOrEmpty(tag))
            {
                return;
            }

            try
            {
                go.tag = tag;
            }
            catch (Exception)
            {
                // tag 未在工程里定义时 Unity 会抛异常；tag 只影响物理层之外的分组语义，
                // 不值得让整次烘焙失败，但必须让用户看见。
                if (summary != null)
                {
                    summary.AppendLine("  注意：tag \"" + tag + "\" 在本工程未定义，已保持 Untagged（对象 \""
                                       + go.name + "\"）");
                }
            }
        }

        private static bool Contains(string[] array, string value)
        {
            for (int i = 0; i < array.Length; i++)
            {
                if (string.Equals(array[i], value, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>往列表里加一个去重项（摘要里的"集合"语义；顺序 = 首次出现顺序）。</summary>
        private static void AddOnce(List<string> values, string value)
        {
            for (int i = 0; i < values.Count; i++)
            {
                if (string.Equals(values[i], value, StringComparison.Ordinal))
                {
                    return;
                }
            }

            values.Add(value);
        }

        private static string Join(List<string> values)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(values[i]);
            }

            return sb.ToString();
        }

        private static string DescribeCounts(IDictionary<string, int> counts)
        {
            List<string> keys = new List<string>(counts.Keys);
            keys.Sort(StringComparer.Ordinal);

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(keys[i]).Append('=').Append(counts[keys[i]].ToString(CultureInfo.InvariantCulture));
            }

            return sb.Length == 0 ? "空" : sb.ToString();
        }

        private static string Num(float value)
        {
            // "R" + 不变文化：同一 float 在任何区域设置下都得到同一个字符串。
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Vec(Vector3 v)
        {
            return "(" + Num(v.x) + "," + Num(v.y) + "," + Num(v.z) + ")";
        }

        private static string Vec(Vector2 v)
        {
            return "(" + Num(v.x) + "," + Num(v.y) + ")";
        }

        private static string Vec(Vector4 v)
        {
            return "(" + Num(v.x) + "," + Num(v.y) + "," + Num(v.z) + "," + Num(v.w) + ")";
        }

        private static string Quat(Quaternion q)
        {
            return "(" + Num(q.x) + "," + Num(q.y) + "," + Num(q.z) + "," + Num(q.w) + ")";
        }

        private static string Quote(string value)
        {
            return value == null ? "<null>" : "\"" + value + "\"";
        }

        private static string Short(string sha)
        {
            if (string.IsNullOrEmpty(sha) || sha.Length < 12)
            {
                return sha;
            }

            return sha.Substring(0, 12);
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return sb.ToString();
            }
        }

        private static string Sha256File(string absolutePath)
        {
            if (!File.Exists(absolutePath))
            {
                throw new FileNotFoundException("源文件不存在（无法计算 SHA256）：" + absolutePath, absolutePath);
            }

            using (FileStream stream = File.OpenRead(absolutePath))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(stream);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return sb.ToString();
            }
        }
    }
}
