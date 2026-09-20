# R4-C / C1 正式内容烘焙实施报告

> 范围：实施 `Docs/plans/net-r4c-content-contract.md` 的 **C1 Editor 内容烘焙**（新增 Editor 构建脚本、
> 其真实 API 编译门禁、`PMDsBuild` 构建前接线），并给出 API / 流程 / 真实编译 / **未执行资源烘焙** /
> 限定边界。
>
> 状态口径仍以 `Docs/plans/net-architecture-migration.md`（R4-C 段）为唯一事实源；本报告**不改主计划**。
> 本轮**未启动 Unity、未执行菜单、未生成任何 .prefab/.json 资产、未抢编辑器锁、未提交、未递归委派**。
>
> **因此 P4C1 不能标 PASS**：契约的 T-C1 验收是「真实 API 编译 + Editor 重复生成 + 无旧脚本检查」，
> 其中"重复生成"与"无旧脚本检查"必须由用户在 Unity 内点菜单后才产生证据（见 §5）。

---

## 0. 结论速览

| 项 | 结论 |
|---|---|
| C1 代码是否落盘 | ✅ `Client/Assets/Editor/PMBattleContentBuild.cs`(+`.meta`)，`namespace PMNet.UnityEditor` |
| 真实 Unity 2019.4 API 编译 | ✅ `Tools/PMBattleContentBuildCheck`：**0 错误 0 警告**（真实 `UnityEditor.dll` + `UnityEngine.*Module.dll`） |
| `PMDsBuild` 是否接线 | ✅ 构建前调 `PrepareForBuild()`，失败即**中止构建**（缺资源不允许假成功） |
| 源场景读取方案 | ✅ Additive 预览读 + **编辑模式脚本审计门**（旧玩法脚本 0 个编辑模式标记，见 §3.1） |
| 地图来源 | ✅ 从 `ScenseBuildLogic` **真实字段**取模板 + `map2` 表；**不调用** `InitData()` |
| 随机数 | ✅ 局部 `xorshift32(0x52444301)`；**不读**全局随机源（只保存/还原 + 断言未变） |
| 世界变换口径 | ✅ 已核清 `SetParent(MAP)`（`worldPositionStays=true`）抵消 `3D` 的 0.5 缩放（§3.3，本轮最易做错的一点） |
| 组件白名单 | ✅ 与 C2 的 `PMUnityBattleMap` / `PMUnityBattlePresentation` 允许集**逐条对齐**（§3.4） |
| digest / 派生 | ✅ 复用 C2 的 `PMBattleContentManifest.TryDeriveCollisionDigestAndWorldVersion`（不重写第二份口径） |
| manifest 发布时序 | ✅ 先生成两个 prefab 并**回读校验**，全部通过后才写 manifest；失败不留 manifest |
| **实际资产烘焙** | ❌ **未执行**（本机不启动 Unity、不抢锁）→ 三个 Resources 产物目前**尚不存在** |
| P4C1 状态 | **PENDING_USER**（等待用户在编辑器点 `Build/Prepare PMNet Battle Content`） |

---

## 1. 交付物与改动文件

### 1.1 新增

| 文件 | 作用 |
|---|---|
| `Client/Assets/Editor/PMBattleContentBuild.cs` | C1 烘焙实现（约 2630 行，UTF-8 **BOM** + CRLF，含中文） |
| `Client/Assets/Editor/PMBattleContentBuild.cs.meta` | 新脚本 meta（guid `2103621d01ec4fc1846f76e3bead77e7`，工程内唯一） |
| `Tools/PMBattleContentBuildCheck/PMBattleContentBuildCheck.csproj` | 真实 Unity 2019.4 程序集编译门禁（netstandard2.0 + C# 7.3） |
| `Tools/PMBattleContentBuildCheck/HostDependencies.cs` | **仅** `ScenseBuildLogic` 类型/字段边界替身（见 §1.3） |
| `Docs/plans/_r4c_build_report.md` | 本报告 |

### 1.2 修改

| 文件 | 改动 |
|---|---|
| `Client/Assets/Editor/PMDsBuild.cs` | ① 类头补 R4-C/C1 说明；② `BuildWindowsHeadlessDs` 的 `try` 首行插入 C1 就绪门（失败 `Finish(batchMode,1)` 并 return）。**其余逻辑（含 `options.scenes` 只含 `PMDsBoot`）一字未动** —— server-smoke 默认场景保持原样。 |

`PMDsBuild.cs` 保持原有 **无 BOM + LF**（它是既有文件，不为了本轮改动重排整文件编码）。新增的
`PMBattleContentBuild.cs` 按项目规范写 **UTF-8 + BOM + CRLF**；`.meta` 按 Unity 自身产物与全仓既有
meta 的**唯一口径**写 **无 BOM + LF**（`PMDsBuild.cs.meta` / C2 本轮新增的 `PMBattleContentManifest.cs.meta`
同为无 BOM + LF，字节长度与格式逐行一致，仅 guid 不同）。

### 1.3 为什么要给 `ScenseBuildLogic` 做替身

真实类在旧链里（`Assets/HYLD1.0/Scripts/OldScripts/ScenseBuildLogic.cs`），`using LongZhiJie;`
`using UnityEngine.UI;` 并引用 `HYLDStaticValue`。把它编进门禁等于把整套旧玩法拖进来，而门禁要证明的是
「C1 的编辑期烘焙代码能在**真实 Unity 2019.4 API** 上编译」。替身只保留 C1 读取的字段
（`mapx/mapy/floors/walls/obstacles/Grasses/trees/map2` + `RedSaveBox/BlueSaveBox/MAP`），并**刻意不声明**
`InitData()` / `maps` / `mapDictionary` / `current_mode` / `InitFinish`：

* 契约明令 C1 不得调用 `InitData()`。若将来有人写了 `logic.InitData()`，**门禁会直接编译失败**
  —— 这是刻意的结构性保证，比注释约定可靠。

---

## 2. C1 对外 API

| 签名 | 语义 |
|---|---|
| `[MenuItem("Build/Prepare PMNet Battle Content")] PrepareContentMenu()` | 用户显式烘焙 + 摘要 + （非 batchmode 时）弹窗提示 |
| `bool PrepareContent()` | **强制**完整烘焙；任何失败都不留 manifest |
| `bool ValidateContent()` | **只读**校验既有产物（不写资源、不开源场景）；给构建 hook 用 |
| `bool PrepareForBuild()` | 构建前 hook：产物缺失/源较新则先烘焙，然后校验；返回 false ⇒ 调用方必须让构建失败 |

---

## 3. 实施细节

### 3.1 源场景怎么读：Additive 预览读 + 编辑模式脚本审计（"旧游戏 Awake 不执行"的证据）

契约允许「Editor 安全预览场景读取」或「另一**经验证不会执行旧游戏 Awake**的编辑模式方案」。本轮实测核验：

**（a）源场景 23 个脚本 GUID 的编辑模式标记普查**

| 结果 | 证据 |
|---|---|
| 带 `[ExecuteInEditMode]` 的**只有**第三方摇杆插件的 `EasyJoystick` / `EasyButton` | `Assets/HYLD1.0/EasyTouch/Plugins/{EasyJoystick,EasyButton}.cs` 文件头属性 |
| 旧玩法脚本**一律没有**编辑模式标记 | `ScenseBuildLogic` / `HYLDStaticValue` / `BattleManger` / `TouchLogic` / `Toolbox` / `HYLDGameOver` / `CreatGemLogic` / `HYLDModenProp` / `VirtualScreen` / `ScaleSelf` / `HYLDHeropropertyUI` / `UITextEffect` / `GameUITeamGemLogic` / `EasyTouch` 全为 `-` |
| 其余 8 个 guid 未在 `Assets/` 下解析出 `.cs` | 它们是 com.unity.ugui 包内脚本（`Image/Text/CanvasScaler/GraphicRaycaster/Button` 等，随包安装），均为标准 UI 组件，无编辑模式标记 |

⇒ **没有任何旧游戏 `Awake`/`OnEnable` 会因本文件加载场景而执行**。

**（b）被容忍的两个插件的编辑模式副作用面（已逐行读过）**

* `EasyJoystick`：`OnEnable/OnDisable` 只订阅/反注册静态 `EasyTouch.On_*` 事件；
  `Update → UpdateJoystick()` 首行即 `if (Application.isPlaying)`，编辑模式下不做任何事；
  `Start()` 只做自身摇杆锚点/虚拟屏计算。
* `EasyButton`：同上（`OnGUI` 里有 `Application.isPlaying` 分支，编辑模式只画调试框）。
* 二者都**不创建/销毁场景对象、不改游戏状态**；`OnDisable` 会反注册静态事件，因此关场景后不留残余订阅。

**（c）把结论做成运行时门（不是"相信今天的普查"）**

`SourceSceneScope.AuditEditModeScripts` 在场景打开后遍历全部组件，凡 `Attribute.IsDefined(type, ExecuteInEditMode/ExecuteAlways)`
为真的类型若**不在**容忍白名单 `{EasyJoystick, EasyButton}` 内 ⇒ **显式失败**（列出类型名），拒绝烘焙。
将来谁给旧玩法脚本加了编辑模式标记，这里会立刻挡住，而不是静默执行其 Awake。

**（d）三条边界**

* 只 `CloseScene` **自己打开的**那一份；用户本来开着的场景不动（其脏状态因此不受影响）；
* 从不 `SaveScene`（源场景一个字节都不写）；
* `finally` 还原 `activeScene` / `Selection.objects` / `UnityEngine.Random.state`。

补充防御：若源场景**已打开且在内存里是脏的**（用户有未保存改动），`Open` 会拒绝继续 ——
因为此时"磁盘文件 SHA"不能代表实际读取到的内容，用不一致的指纹烘焙会产出不可追溯的 digest。

### 3.2 地图复刻：循环结构 + 随机数消费顺序逐处对齐

模板与格子表**全部取自真实类字段**（`logic.floors/walls/obstacles/Grasses/trees/map2`），
**不手抄** 735 个格子数字，也不把表扩到 35 行（契约：维持真实 `mapx=33 / mapy=21` 循环范围）。

三处最容易改坏的地方（源码里都是"看着像冗余、删了就错位"）：

1. `maps[i,j]==1`（草丛）分支的实例化被**注释掉**，但 `Random.Range(0,Grasses.Length)` **仍消费一次**
   —— 必须照消费，否则其后所有落点整体错位；
2. 墙循环 `if (i % 3 == 0) random = walll - 1`，C# 负数取模下只有 `{-15,-12,…,15}` 命中（含 0）；
3. 树的第二处循环边界与第一处**不同**（i 循环用 `-mapx/2-1`，j 循环用 `-mapx/2`），源码即如此，未"修正"。

**局部确定 PRNG**：`xorshift32`，状态种子 = 契约冻结的 `0x52444301`（非 0，满足 xorshift 前置条件），
`Range(min,max)` 语义对齐 Unity 的整数重载（`[min,max)`，`max<=min` 返回 `min`）。
实现内注释说明了"为什么不用 `UnityEngine.Random`"（旧链地图不可复现的根因就是全局未播种随机）。

**"不用全局 Random"的证据**：全文件对 `UnityEngine.Random` 只有 3 处接触 —— 读 `Random.state`、
`finally` 写回、以及比较是否被改动并写进摘要（`UnityEngine.Random.state 未被改动=是`）。
生成过程**没有任何一处**调用 `Random.Range/InitState`。若将来某处引入全局随机，摘要会显式写"否（异常，请核查）"。

**金库格（值 5/6）显式失败**：`map2` 实测 0 处，但契约的另一张图（map4/金库攻防）会用到。
金库是**外部 prefab**（`RedSaveBox`/`BlueSaveBox`，guid 外链），其内部组件未经"纯几何"审计，
直接实例化会把未知内容带进地图资源 ⇒ 本版遇到 5/6 **明确失败并报告**（不静默丢弃格子、不擅自把外部 prefab 拉进来）。

**地面**：源场景 `HYLDGameTatal/MAP/Plane`（唯一真实"地板碰撞"）被整体拷进产物。
它不是可选项：`floors` 模板**自身没有 Collider**（见 §3.3 的实测），去掉地面 = 地图没有可站立面。

### 3.3 世界变换口径（本轮最容易被做错的一处，已核清）

源码落点：

```csharp
GameObject go = Instantiate(mapPresfab, MapPrefabvector3, MapPrefabquaternion);
go.transform.SetParent(MAP);          // ← 单参重载 == SetParent(parent, worldPositionStays: true)
```

`SetParent(Transform)` 的默认是 **worldPositionStays = true**，因此父级 `3D`（`localScale = (0.5,0.5,0.5)`）
的缩放被**反向抵消**：tile 的**局部**值被放大 2 倍，而**世界**变换保持

```
world position = (i - 16, y, j - 10)         // 不含 0.5
world rotation = quaternion 传入值            // map2 全为 identity
world scale    = 模板自身 localScale          // 不含 0.5
```

⇒ 「玩法区 x∈[-16,16]、z∈[-10,10]」这一既有调查结论是**对的**；若按"父级 0.5 缩放会传到 tile"
去烘焙，会把整张地图做成一半大（而且碰撞/出生点全部错位）。

烘焙产物按**世界等价**存放：容器 `MAP` 用 identity，tile 的 `localPosition/localScale` 直接等于世界值，
**不做 0.5 × 2 往返**（往返本身容易在浮点/四元数上引入差异）。digest 里写入
`layout.tileSpace=world|container=identity|tile.local==tile.world` 固定这一含义。

### 3.4 组件白名单：与 C2 运行期校验**逐条**对齐

C2 的两个运行期校验器已经在仓库里（本轮已读）：

| 消费方 | 允许集（原文） | 致命项 |
|---|---|---|
| `PMUnityBattleMap.CollectAndValidate` | Transform / MeshFilter / MeshRenderer / **Collider** / LODGroup / **kinematic** Rigidbody | missing script(null 项)、任何 MonoBehaviour、Camera、Animator、AudioListener、**非 kinematic** Rigidbody、任何其它类型；**Collider 必须 enabled 且所在物体 activeInHierarchy** |
| `PMUnityBattlePresentation.ValidateComponents` | Transform / MeshFilter / MeshRenderer / SkinnedMeshRenderer / Animator / LODGroup | missing script、任何 MonoBehaviour、Collider、Rigidbody、Camera、AudioListener、任何其它类型 |

C1 的对齐策略：

* **地图 = fail-closed**：模板里出现白名单外组件**直接让生成失败**（不"报告一下继续"）。
  源场景实测 5 类模板子树内组件只有 `Transform/MeshFilter/MeshRenderer/MeshCollider/BoxCollider`
  （合计 36 Transform / 30 MeshFilter / 30 MeshRenderer / 19 MeshCollider / 14 BoxCollider），
  白名单外为 **0**，且全场景 `Rigidbody` = 0、`LODGroup` = 0、`MonoBehaviour` 不在模板子树内。
* **角色 = 删除 + 逐类型计数上报**（契约要求的"报告而非静默"）：保留集
  `{Transform, MeshFilter, MeshRenderer, SkinnedMeshRenderer, Animator, LODGroup}`，其余全部
  `DestroyImmediate` 并按类型名计数进摘要。
* **inactive 的 Collider 是硬约束**：因为 C2 会把"未激活物体上的 Collider"判为致命，
  C1 的回读校验对每个 Collider 检查 `enabled` + `activeInHierarchy`，不满足即失败。
  （本轮实测：所有带 Collider 的模板（墙/障碍/树）在源场景都是 `m_IsActive: 1`，地板模板是 `0` 但地板**没有** Collider。）

### 3.5 组件逐字段拷贝（不简化、不近似）

* **Transform**：`localPosition/localRotation/localScale` + `name/layer/tag/activeSelf` 直接复制；
  tile 根的 `localPosition/Rotation` 由落点决定、`localScale` 取模板自身。
* **其它白名单组件**：`AddComponent(源组件类型)` 后走 **SerializedObject 全字段拷贝**
  （`GetIterator() + Next(true)`，含隐藏字段；数组先同步 `arraySize` 再逐元素
  `CopyFromSerializedProperty`），跳过 `m_GameObject / m_Script / m_Prefab* / m_CorrespondingSourceObject`
  （这些是跨对象引用与 prefab 链接内部字段，复制它们会把引用接到错误的物体上）。
* 于是 `m_Mesh`、`m_Materials[]`、`m_Convex`、`m_Size`、`m_Center`、`m_IsTrigger`、`m_CookingOptions`、
  `m_CastShadows`、`m_LightProbeUsage` … **逐字段**落到产物，没有任何"用盒子替代 mesh"的简化。

### 3.6 角色表现清理（源 prefab 不动）

源：`Assets/Resources/Remake/Player.prefab`（**只读**）。实测其组件构成：

```
Player(根: 3 个 MonoBehaviour = HYLDPlayerController + PlayerLogic + **missing script**)
└── Capsule(tag=Player): MeshFilter(builtin Capsule) + MeshRenderer + **Rigidbody** + **BoxCollider**
    + MonoBehaviour(移动型大招)
    ├── Body: **Animator**(Avatar 6ccf12d5…, Controller 58bf525e…, m_ApplyRootMotion=0)
    │     └── 骨架(纯 Transform) + NPC_Man_Fat(**SkinnedMeshRenderer**, mesh f9f5c1bd…)
    │           + NPC_Beard_006 / NPC_Hair_004 / NPC_Tools_Hammer_01(MeshFilter+MeshRenderer)
    ├── BulletCreater: **AudioSource**
    └── Gun: MeshFilter + MeshRenderer + **BoxCollider(trigger)** + **LineRenderer**
└── Canvas(Canvas + CanvasScaler + GraphicRaycaster) + 24 RectTransform/CanvasRenderer/Image/Text
      （旧世界空间血条/名字/宝石计数/保护罩 SpriteRenderer…）
```

处理（**克隆后清理**，用 `Object.Instantiate` 而不是 `PrefabUtility.InstantiatePrefab`，
以避免任何回写原件的可能）：

1. `GameObjectUtility.RemoveMonoBehavioursWithMissingScript` 清 missing script；
2. 删除根下的 `Canvas` 子树（旧世界空间 UI；其驱动器就是被删掉的旧脚本）；
3. 逐节点把白名单外组件 `DestroyImmediate`（含 `Rigidbody/BoxCollider/AudioSource/LineRenderer/SpriteRenderer/CanvasRenderer/Canvas/CanvasScaler/GraphicRaycaster/Image/Text/PlayerLogic/HYLDPlayerController/移动型大招`）；
4. `Animator.applyRootMotion = false`（源资产本来就是 0，**显式再写一次并校验**）；
5. **校验**存在 Animator、其 `runtimeAnimatorController != null`、控制器参数表里**确实有 Float 型 `Speed`**
   （已核实 `Assets/HYLD2.0/Animator/Player.controller` 的 `m_AnimatorParameters` 首项 = `Speed (m_Type: 1 = Float)`）；
   没有就直接失败 —— 因为 C2 靠它驱动移动动画，缺了它表现层会"完全不驱动"而不报错。

`Speed` 供 C2 使用这一点是**已验证**的（不是"假设存在"）。根节点按 C2 约定重命名为 `PlayerVisualV1`
（C2 构造后仍会自行改名 `BuildRootName(label, role)`）。骨架节点**全部保留**（不裁剪），
原因：`SkinnedMeshRenderer.m_Bones` 与 Avatar 的人形骨骼映射都是按层级/名字解析的，
裁掉"看起来没用"的空 Transform（如 IK 目标 `CATRigLArmIKTarget`）会静默破坏绑定。

### 3.7 digest 设计

```
contentDigest = SHA256(UTF-8 规范化文本流)，小写 hex（64 字符）
流 = 域标签 PMNetBattleContentDigest/v1
   + formatVersion / mapId / seed（取 C2 的冻结常量）
   + mapx / mapy / layout.tileSpace=world|container=identity|tile.local==tile.world
   + source[0..2]：HYLDGame.unity / ScenseBuildLogic.cs / Remake/Player.prefab 的 **SHA256**
   + palette.{floors,walls,obstacles,Grasses,trees}[i]：每个模板的规范化节点签名
   + ground：HYLDGameTatal/MAP/Plane 的规范化节点签名
   + placements.count + 每个落点的规范行（类别 / 模板序号 / 局部位置 / 旋转）
```

规范化节点签名 = 节点行（名字 / activeSelf / layer / tag）+ 局部变换 + **组件全字段**（按字段路径排序）
+ 子节点（兄弟序，递归）。其中：

* **对象引用只输出身份**：资产 → `asset|资产路径|类型|名字`；场景内对象 → `local|类型|名字`。
  **刻意不用** `GetInstanceID`（每次加载都不同）、**也不用** `AssetDatabase` 的本地子资产编号
  （其跨会话稳定性不由我们控制，会把"重复生成摘要稳定"搞坏）；
* `m_Mesh` 追加**内容锚**：`mesh.verts=顶点数` + `mesh.bounds=center/extents`（mesh 被换但路径名字没变时能识别）；
* 浮点一律 `"R"` + `InvariantCulture`（同值在任何区域设置下得到同一字符串）；
* 覆盖了契约要求的：确定布局 ✓ / 模板源指纹 ✓ / 实际 Collider 类型与几何 transform ✓ / mesh 引用 ✓ / **源 Player 指纹**（=源 prefab SHA256）✓；
  不使用实例 ID / hashcode / 运行期随机 ✓。

**派生（冻结，不各写常量）**：直接调用 C2 的
`PMBattleContentManifest.TryDeriveCollisionDigestAndWorldVersion(contentDigest, out collisionDigest, out worldVersion, out error)`
⇒ `collisionDigest = ` 前 4 个摘要字节按 LE 的 uint（0→1）、`worldVersion = (int)(collisionDigest & 0x7fffffff)`（0→1）。
本文件**没有**第二份派生实现；且 `PMBattleContentBuildCheck` 把 C2 的
`PMBattleContentManifest.cs` 编进同一门禁 ⇒ 两端口径在**编译期**就是同一份代码。

**摘要稳定的两道自证**（在烘焙过程中执行）：

1. **两次独立生成布局**（重跑 PRNG + 重读模板）逐字段比较，不同即失败；
2. 保存前后：**回读磁盘产物**做结构校验（见 §3.9）。

### 3.8 manifest 发布时序（"部分失败不 valid"）

```
进流程 → 先删旧 manifest（发布信号）
       → 打开源场景（只读）→ 审计 → 取模板/地面/map2
       → 生成布局（并复算一次比对）→ 算 contentDigest → 派生 collisionDigest/worldVersion
       → 建临时空场景（Additive）→ 搭地图层级 → SaveAsPrefabAsset → 回读校验
       → 克隆角色 → 清理 → SaveAsPrefabAsset → 回读校验
       → 最后写 Assets/Resources/PMNet/BattleContentV1.json（JsonUtility 序列化 **C2 的类型**）
       → 立刻再解析一次确认可被 C2 规则接受
finally → 销毁临时对象、关闭临时场景、还原 activeScene/Selection/Random.state、关源场景
失败路径 → 删除 manifest（绝不留"有效 manifest"）
```

manifest 直接用 C2 的 `PMBattleContentManifest` 类序列化 ⇒ 字段名/类型/顺序**不可能**漂移；
没有第二个 manifest 类。JSON 写 UTF-8 **不带 BOM**（避免 BOM 被当内容的风险）。

### 3.9 回读校验（生成后必须读回来查，不能只看内存）

| 对象 | 检查 |
|---|---|
| 地图 prefab | 回读成功；根激活；`MAP` 容器 tile 数与布局一致且**逐个**比对 `localPosition/localRotation`；无 missing script（null 组件项）；组件全在 C2 允许集内；Collider 全部 `enabled` 且 `activeInHierarchy`；非 trigger Collider > 0；同时打印类型分布清点 |
| 角色 prefab | 回读成功；根激活；**无** missing script / MonoBehaviour / Collider / Rigidbody；组件全在 C2 允许集内；Animator ≥1 且 `applyRootMotion=false` 且存在 Float `Speed`；网格渲染 > 0 |
| manifest | 写盘后能加载为 `TextAsset`，并能被 C2 的 `TryParseJson` **强校验通过** |
| `ValidateContent()`（构建 hook 用） | 上述全部 + 资源键与冻结常量一致；**不写任何资源、不打开源场景** |

`ValidateContent()` 的诚实边界：它**不重算**编辑器侧内容指纹（源场景可能已被改动/不在磁盘），
因此只证"资源与 manifest 内部一致 + 组件约束"。两端"同一份内容"由 C3 的同构建 + digest 握手守护
（与契约对 C2 的同一口径）。

### 3.10 `PMDsBuild` 接线与那处 `#if UNITY_EDITOR`

```csharp
try
{
    // R4-C / C1：正式内容先就绪（缺资源不允许假成功）。
#if UNITY_EDITOR
    if (!PMNet.UnityEditor.PMBattleContentBuild.PrepareForBuild())
    {
        Debug.LogError("[PMDsBuild] 正式内容未就绪或校验失败，构建中止（缺资源不允许假成功）。");
        Finish(batchMode, 1);
        return;
    }
#endif
    string bootScene = EnsureBootScene();
    ...
```

为什么包 `#if UNITY_EDITOR`（而不是裸写）：

* `PMDsBuild.cs` 同时被 **`Tools/PMUnityGlueCheck`**（手写 UnityEngine/UnityEditor **桩件**）编译，
  该工程的编译集**不含** `Assets/Editor/PMBattleContentBuild.cs`，裸写会直接编不过、把既有门禁弄红；
* `UNITY_EDITOR` 在 Unity 的 Editor 程序集里**恒定义**，因此对真实编辑器零影响；
* **它并没有躲开验证**：`Tools/PMBattleContentBuildCheck` 定义了 `UNITY_EDITOR` 并用**真实 DLL**
  编译 `PMDsBuild.cs`，因此这段调用被静态编译校验过（见 §4 的负向测试）。
* `Resources/` 下的内容由 Unity 自动随包纳入，所以这里不必改 `options.scenes`；
  server-smoke 的默认场景仍只有 `PMDsBoot`（本轮未改变它）。

---

## 4. 真实编译验证（实际执行过，附命令与结果）

```bat
REM ① C1 门禁（**真实** Unity 2019.4 程序集：UnityEditor.dll + UnityEngine{Core,Physics,Animation,JSONSerialize,SharedInternals}Module.dll）
dotnet build Tools/PMBattleContentBuildCheck -c Release
REM    → 已成功生成。0 个警告 / 0 个错误
```

编译集 = `HostDependencies.cs`（`ScenseBuildLogic` 替身）+ **`Client/Assets/Editor/PMBattleContentBuild.cs`**
+ **`Client/Assets/Editor/PMDsBuild.cs`** + **`Client/Assets/Scripts/PMUnity/PMBattleContentManifest.cs`**（C2 真实源码）。

**（a）门禁真实抓到了 API 差异（证明它真在编真实程序集，不是走过场）**

首次编译报 2 个错：

```
PMBattleContentBuild.cs(1326,45): error CS0117: “SerializedPropertyType”未包含“Long”的定义
PMBattleContentBuild.cs(1332,45): error CS0117: “SerializedPropertyType”未包含“Double”的定义
```

Unity **2019.4** 的 `SerializedPropertyType` 还没有 `Long/Double`（后续版本才加）⇒ 已删除这两个分支
（落到 `default` 分支输出确定的类型标记）。这正是"先自验真实编译"要抓的东西。

**（b）负向测试：证明 `PMDsBuild` 里那段调用**确实**在编译区内**

临时在 `#if UNITY_EDITOR` 块内插入一个不存在的成员再编译：

```
PMDsBuild.cs(64,52): error CS0117: “PMBattleContentBuild”未包含“__NEGATIVE_TEST__”的定义
```

⇒ ① 该行位于 `#if UNITY_EDITOR` 内被**真实编译**；② 类型是从**真实** `PMBattleContentBuild` 解析到的
（不是漏编成 0 行）。随后已从备份恢复，`cmp` 确认**字节一致**。

**（c）不回归既有门禁**

```bat
dotnet build Tools/PMUnityGlueCheck -c Release
```

改动前后错误集**完全一致**：仍是 4 条 `PMUnityBattlePresentation.cs(...): error CS0246 未能找到类型…“Animator”`
（这是 **C2 本轮新增文件**与 `UnityStubs.cs` 缺 `Animator` 桩件造成的**既有**红，**不是本轮引入**；
本轮无权改 `Tools/PMUnityGlueCheck/*`，故只记录不处理）。

**（d）语言面**

门禁与 Unity 一致使用 `netstandard2.0` + `LangVersion 7.3`，`EnableDefaultCompileItems=false`，
引用 `Private=false`（只做编译校验，不拷 DLL）。

---

## 5. **未执行**的步骤（明确声明，不得标 PASS）

| 步骤 | 状态 | 原因/去向 |
|---|---|---|
| 启动 Unity / 执行 `Build/Prepare PMNet Battle Content` 菜单 | ❌ 未执行 | 硬边界：不启动 Unity、不抢编辑器锁；由用户执行 |
| 真正生成 `Resources/PMNet/BattleMapV1.prefab` | ❌ 未生成 | 同上（因此此刻三个产物都还不存在） |
| 真正生成 `Resources/PMNet/PlayerVisualV1.prefab` | ❌ 未生成 | 同上 |
| 真正生成 `Resources/PMNet/BattleContentV1.json` | ❌ 未生成 | 同上 |
| digest 的真实计算与"重复生成摘要一致" | ❌ 未执行 | 需要真实 Editor 会话 |
| prefab 回读校验 / `ValidateContent()` 的真实运行 | ❌ 未执行 | 同上 |
| 真实 PhysX 下地图碰撞可用性、`TryGetSpawn` 在真实地图上的行为 | ❌ 未执行 | 属 C2/C3/C4（C2 已有独立门禁） |
| `PMDsBuild` 真机打包（含 Resources 随包） | ❌ 未执行 | 需用户点菜单 |

**因此**：`P4C1` 维持在 **PENDING_USER**；本报告只声明"代码落盘 + 真实 API 编译通过"，
**不声明**"Editor 重复生成通过 / 无旧脚本检查通过"。

---

## 6. 限定边界与已知缺口

1. **首图固定 map2**：`map1/map3/map4` 未接入（契约冻结"不擅自把 map4 纳入"）。
   遇到金库格（5/6）显式失败而不是静默丢弃（§3.2）。
2. **地板 tile 是 inactive**（重要发现，**刻意保留**）：
   源场景两个地板模板 `Plain02 (1)` / `Plain01 (1)` 的 `m_IsActive: 0`，而
   `Instantiate` 会复制源物体的激活状态 ⇒ 旧链运行期生成的地板 tile **也是未激活的**
   （不渲染、且本来就没有 Collider）。本轮按"逐字段保留 + 不改变原渲染"**原样保留**。
   后果与代价（如实记录）：
   * 这 2660 个地板 tile 不渲染、不参与物理，纯属节点重量（约占产物 GameObject 的 88%）；
   * 地图的**唯一可见地板与唯一地面碰撞**都来自 `Ground/Plane`（内置 Plane mesh × scale 10 = 100×100、y=0）；
   * 若主侧希望裁剪这些 inactive tile（**语义等价、纯体积优化**），需要改本文件并重新握手 digest
     —— 本轮**不**擅自简化（契约：不得用简化替代实际地图）。
3. **`Grasses` 在源码里被注释掉**：本图草丛实例数 = **0**（随机数仍被消费一次以保持序列对齐）。
   这是复刻源码行为，不是漏生成。
4. **`3D` 的 0.5 缩放与产物容器**：产物按**世界等价**存放（容器 identity），因此产物层级与源场景
   不完全同构（源是 `HYLDGameTatal/3D(0.5)/MAP`）。选择理由见 §3.3；digest 里用
   `layout.tileSpace=world|...` 固定这一含义。
5. **`Plane` 的可见性**：地面 `Ground/Plane` 保留了源 MeshRenderer（材质 guid `9a4c9669…`）与
   MeshCollider；**源场景 `HYLDGameTatal/MAP` 下的 `lights`（3 个 Light 子物体）按许可组件清单未纳入**
   （Light 不在地图允许集内），因此产物**自身不含光源**。运行期（C2/C3）需要自行解决照明/环境光。
6. **非 mesh 渲染器不进产物**：`LineRenderer`（`Gun` 的瞄准线）、`SpriteRenderer`（`保护罩`）、
   `CanvasRenderer`（旧 UI）都随"旧脚本已删"一并删除（理由：它们由被删除的旧脚本驱动，
   保留只会留下无驱动、无意义的渲染器）。C2 的允许集本身也不包含它们。
7. **动画剪辑绑定路径未静态核验**：`Player.controller` 的 19 个 state 全部引用
   `NPC_Mecanims.FBX`（guid `6ccf12d5…`，**二进制 FBX**，与 Avatar 同源）内的剪辑；
   本轮**无法**在不开 Unity 的前提下核验剪辑是否绑定到被删除的 Canvas 子树。已知的有利事实：
   Animator 挂在子物体 `Body` 上，clip 路径以 `Body` 为根，而 C2 只写 prefab 根 ⇒ 不构成争用。
   若运行期出现 "transform not found" 警告，来源就是这里。
8. **`Player.prefab` 根的 authored `localPosition=(0,2,0)` 保留**：C2 的 `Apply` 写的是
   `transform.position`（世界坐标）并在构造期就会覆盖它，因此不影响正确性；但若将来有人改成
   写 `localPosition`，这 2 个单位会变成偏移 —— 记录在此。
9. **组件拷贝用 SerializedObject 全字段遍历**：`Next(true)` 会包含隐藏字段与数组元素，
   一次性覆盖 `m_Mesh/m_Materials/m_Convex/m_Size/...`；但**不**包含 Unity 未序列化的运行时状态
   （如 MeshCollider 的 cooking 结果缓存，加载时由 Unity 依据 `m_Mesh` 重新 cook）。
10. **staleness 判断用文件时间戳**（`PrepareForBuild` 里）：源文件比产物新就重烘。
    它是"保守触发"而非内容比对；`svn update` 触碰时间戳会触发一次不必要的重烘（可接受，且会打日志）。
11. **`ValidateContent()` 不校验"内容与源一致"**（理由见 §3.9）；它也不会打开源场景，
    因此**不会**触发 §3.1 的编辑模式脚本审计 —— 构建 hook 在产物齐备时完全不碰用户场景，这是有意的。
12. **`Application.isPlaying` 时拒绝**：所有入口（`PrepareContent`/`PrepareForBuild`）在 Play 模式下
    直接失败并报错，不进入生成；本文件**从不**设置 `EditorApplication.isPlaying`、也不调用任何运行
    播放的 API。
13. **`EasyJoystick/EasyButton` 会在预览读期间执行其编辑模式 `OnEnable`/`Start`**（§3.1b 已逐行核验
    其副作用面：只动自身 UI 状态与静态 EasyTouch 事件，`OnDisable` 反注册）。这是"加载源场景"的
    固有代价，已通过审计门（白名单 + 失败关闭）与 finally 还原把风险限定在可解释范围内。
14. **本报告不覆盖 C2/C3**：C1 不代表 C3 已接线；本次改动**没有**触碰 C2 的任何文件。
15. **未改动 `Tools/PMUnityGlueCheck`**（无权）：它当前因 C2 缺 `Animator` 桩件而红，属既有状态。

---

## 7. 跨组契约缺口记录（**未**自行改变任何 JSON 字段）

契约冻结的 manifest schema 是 8 个字段，本轮**一个字段都没加、没删、没改名**，两个派生值也走 C2 的
共享实现。以下是本轮观察到、需要主侧知悉但**不应由 C1 单方面处置**的点：

| # | 观察 | 影响 | 建议处置方 |
|---|---|---|---|
| G1 | `Player.prefab` 根上有 **missing script**（第三个 MonoBehaviour，guid `66e3984f…` 在仓库里查不到任何 `.cs.meta`，属已删除脚本）。C1 已按契约删除。 | C2 把"missing script"判为致命，因此这是**必须**删的；但源资产本身是脏的，未来若有人"修"源 prefab 会改变源文件 SHA ⇒ digest 变 ⇒ 需要重烘。 | 主侧/资产所有者 |
| G2 | `map2` 表**不参与序列化**（`int[,]` 不可序列化），只在 `ScenseBuildLogic` 的字段初始化器里。因此摘要必须把 `ScenseBuildLogic.cs` 的 SHA 也纳入（已纳入）。 | 若将来把地图表挪到配置资产，digest 的输入集合要同步调整。 | 主侧 |
| G3 | C2 的允许集**不包含** `SkinnedMeshRenderer`（地图侧）与 `Light`（两侧）。C1 因此在地图侧对 `SkinnedMeshRenderer` 采取**失败**策略。 | 若将来地图模板引入骨骼网格，会直接烘焙失败（需先与 C2 对齐允许集）。 | 主侧/C2 |
| G4 | 金库（5/6 格）在首图不存在；`RedSaveBox/BlueSaveBox` 是外部 prefab。 | 换图（map4/金库攻防）前必须先决定金库的"纯几何表现"与是否属允许集；C1 目前 fail-closed。 | 产品 + 主侧 |
| G5 | 地图自身不含 `Camera`/`Light`/音频（按允许集删除/未纳入）。 | 运行期画面亮度/可见性由宿主（C2/C3）决定，不由地图负责。 | C2/C3 |
| G6 | 出生点 `x=±15, z=-5/0/5`（契约冻结）与地图实际范围的一致性**已核清、无缺口**：墙在 `|x|≤17, |z|≤11`，树木外环到 `|x|≤25, |z|≤19`，地面 `Plane` 为 100×100（`x,z∈[-50,50]`）。⇒ 出生位在墙**内**、在地面**上**，`TryGetSpawn` 的向下查询能找到真实地面。 | 无 | — |
| G7 | C2 已有文件（`PMBattleContentManifest/PMUnityBattleMap/PMUnityBattlePresentation`）与本轮 C1 的**资源键、manifest 字段、派生规则、允许集**均已对齐；**无字段级缺口**。 | 无 | — |

---

## 8. 期望数值（供用户首次烘焙时对照 Console 摘要）

用**同一算法**（含同一 `xorshift32(0x52444301)`）在 Python 里独立复算的结果（**不是** C# 执行结果，
仅用于给用户一个对照基线；真实数值以烘焙时打印的摘要为准）：

| 项 | 期望值 |
|---|---|
| `map2` 表维度 / 实际使用 | 35×21 表 / 33×21 = **693** 格（实测 658 个 `0` + 35 个 `2`；**无** `1/5/6`） |
| 落点总数 | **2919** = floor **2660** + obstacle **35** + wall **112** + tree **112**（grass **0**） |
| 其中"边界外补地板" | 1967（占总节点大头，且**全部 inactive**） |
| 模板使用分布 | floor[0]×683 / [1]×641 / [2]×686 / [3]×650；wall[0]×36 [1]×26 [2]×28 [3]×22；obstacle[0]×5 [1]×2 [2]×4 [3]×2 [4]×1 [5]×10 [6]×6 [7]×5；tree[0]×20 [1]×24 [2]×8 [3]×22 [4]×16 [5]×10 [6]×12 |
| 子节点（障碍 base+v、棺盖） | 82 |
| 预期 GameObject 总数 | ≈ **3005**（根 + Ground + Plane + MAP + 2919 + 82） |
| 预期非 trigger Collider（含地面） | ≈ **300**（量级；远低于 C2 的 4096 上限） |
| 世界范围 | 网格 `x∈[-16,16] z∈[-10,10]`；墙 `|x|≤17, |z|≤11`；树外环到 `|x|≤25, |z|≤19`；地面 100×100 |
| 玩家出生点 | `x=±15, z=-5/0/5`（在墙内、在地面上） |

> 注：`placements` 的**具体数量**由本轮选定的局部 PRNG 算法决定（契约只要求"局部固定种子、确定、可复现"，
> 不要求复现旧链未播种的运行期随机）。同一构建内 DS 与客户端加载的是**同一份 prefab**，因此数量差异
> 不影响两端一致性。

---

## 9. 用户下一步（本轮交付后只剩这一步）

```
1. 在 Unity 2019.4.8f1 打开 Client 工程（不要用 Unity 6）；
2. 菜单 Build / Prepare PMNet Battle Content  ← 首次真正生成三个资源；
3. 对照 Console 的 [PMBattleContentBuild] 摘要检查：
   · "布局（同种子两次生成一致）" 的 total=2919 一类的数字（见 §8）；
   · "地图组件清单"：colliders>0、无其它类型；
   · "角色表现清理"：保留组件类型 = {Animator, MeshFilter, MeshRenderer, SkinnedMeshRenderer} 量级、
     删除 missing script=1、删除旧 UI 子树 1 个；
   · "contentDigest=…"、"collisionDigest=… worldVersion=…"；
   · "已发布 manifest（发布信号，最后一步）"；
   · "UnityEngine.Random.state 未被改动=是"。
4. 再用菜单跑一次（或直接 `PMDsBuild` 打包）确认 digest **完全一致**（重复生成稳定性）；
5. 之后 Build / Build HyldDS (Windows Headless) 会自动先走 `PrepareForBuild()` 校验。
```

---

## 10. 已检查范围 / 未做事项

**按必读顺序读完**：`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md` → `Server/AGENTS.md`
→ `Docs/plans/net-architecture-migration.md`（卷首"快速接手/环境现状/本机环境的坑/禁止事项" + 文末
R4-B/R4-C 段与 P4C1–P4C4 表）→ `Docs/plans/net-r4c-content-contract.md`（全文）
→ `Docs/plans/_r4c_scene_survey.md` → `Docs/plans/_r4c_actor_survey.md`
→ `~/.pi/agent/skills/windows-shell-compat/SKILL.md`。

**源码/资产（只读）**：`HYLD1.0/Scripts/OldScripts/ScenseBuildLogic.cs`（全文，含 map1–map4 表）；
`Scenes/HYLDGame.unity`（文本 YAML：ScenseBuildLogic 序列化块、5 类模板子树全量组件/变换、
`HYLDGameTatal/MAP/Plane`、`3D`、`3D/MAP` 的变换、场景根对象、`ExecuteInEditMode` 脚本普查）；
`Resources/Remake/Player.prefab`（全量组件/层级/引用）；`HYLD2.0/Animator/Player.controller`（参数表）；
`HYLD1.0/EasyTouch/Plugins/{EasyJoystick,EasyButton}.cs`（OnEnable/Start/Update/OnGUI 读法）；
`Scripts/PMUnity/{PMBattleContentManifest,PMUnityBattleMap,PMUnityBattlePresentation}.cs`（**C2 真实实现**，
用于对齐 manifest 字段/派生规则/允许集/出生点/相机约定）；`Scripts/PMNet/Control/PMDsControlProtocol.cs`
（`PMDsRosterIdentity.TeamId` 与 C2 的 teamIndex 映射一致性核对）；`Editor/PMDsBuild.cs`；
`Tools/{PMR4UnityCheck,PMUnityGlueCheck}/*`（门禁写法与 `UNITY_EDITOR` 惯例）。

**工具与方法**：`fffind`/`ffgrep`（含工作区外绝对路径）+ 文本 YAML 解析（Python，经 stdin，不落临时文件）
+ 独立 Python 复算布局 + `dotnet build`（真实 Unity 2019.4 程序集引用；`bin/obj` 经
`-p:BaseIntermediateOutputPath`/`-p:BaseOutputPath` **重定向到系统临时目录**，不在仓库内产生新文件）。

**未做（符合硬边界）**：未启动 Unity / 未执行任何菜单 / 未生成或改写任何 Unity 资产 / 未抢编辑器锁 /
未改动源场景与源 prefab / 未改 `Tools/PMUnityGlueCheck` / 未改 C2 的任何文件 / 未动
`net-r4c-content-contract.md` 与 `net-architecture-migration.md`（本报告不改主计划）/ 未 svn、git 写操作 /
未递归委派。
