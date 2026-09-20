# R4-C 正式战斗场景接入调研（hyld 客户端 / HyldDS）

> 范围：只读调查「正式地图从哪里选择、唯一地图 ID/场景路径/BuildSettings/资源加载方式；DS 现只打空引导场景如何纳入同地图；直接加载正式场景会触发哪些 Awake/Start 旧网络/战斗/表现；哪些是 DS 必需 Collider 或运行期生成地图」。允许写入本报告一个文件。
> 未改任何源码/场景/资产，未启动 Unity、未编译、未提交、未递归委派。状态与验收仍以 `net-architecture-migration.md` 为准，本报告**不改主计划**。
> 资产类型说明：Unity 2019.4 资产是**文本 YAML**（`.unity`/`.prefab`/`.asset`），按文本解析，不适用 UE 资产 MCP 路由。

---

## 0. 结论速览

| 问题 | 结论（证据见 §1） |
|---|---|
| 正式地图从哪里选择？ | **没有任何"地图选择"逻辑**。旧链唯一入口 `UIMatchingPanel.cs:66` 用整数常量 `SceneConfig.battleScene = 2` 加载；地图内容不是资产，而是 `ScenseBuildLogic.InitData()` 在运行期按模式字符串选 `map1..map4` 程序化生成。 |
| 唯一地图 ID / 场景路径 | **场景**：`Assets/Scenes/HYLDGame.unity`（`EditorBuildSettings` index 2）。**地图 ID 不存在**；只有模式串 `HYLDStaticValue.ModenName`（网络模式恒 `HYLDBaoShiZhengBa` → `map2`）。 |
| BuildSettings | 仅 4 条 enabled：0 `HYLDLogon`、1 `HYLDStart`、2 `HYLDGame`、3 `HYLDAsyncScence`。DS 构建**不看**它（`PMDsBuild.cs:66` 用 `options.scenes = { PMDsBoot.unity }` 覆盖）。 |
| 资源加载方式 | 地图几何**不走** Resources/AssetBundle：场景内联模板 GameObject + 运行期 `Instantiate`。其它资源走 `Resources.Load("Remake/"+type)`。 |
| DS 现在怎么纳入同地图？ | **目前没有纳入**。DS/客户端新链都只在**运行期 `SceneManager.CreateScene` 出的本地物理场景**里 new 两块 BoxCollider（`PMR3TestScene`：地板 40×1×40 + 墙 1×2×8），且白名单容量**硬编码 2**。 |
| 直接加载正式场景会触发什么 | `HYLDStaticValue.Awake` → `AddComponent<HYLDBaoShiZhengBaManger>()` → `BattleManger.Init()` → `ScenseBuildLogic.InitData()`（这一步才生成地图）+ 表现层（相机/玩家/子弹/UI）；`BattleManger.Start()` 还会 `InitSocket()` **绑硬编码 UDP 7777**。 |
| DS 必需 Collider | 只有 (a) 地面 `HYLDGameTatal/MAP/Plane` 的 MeshCollider；(b) `InitData()` 实例化的墙/障碍/树副本 Collider。`floors`/`Grasses` 模板**无 Collider**。 |
| 运行期生成地图 | **是**。33×21 网格 + 边界墙 + 外围树全部由 `Instantiate` 生成；生成用随机数**未播种**（`rand_seed` 有协议字段、有服务端填值、有客户端存值，但**无任何消费者**）。 |

---

## 1. 已确认（含文件/行号证据）

### 1.1 场景选择：整数索引，唯一值 2

- `Client/Assets/HYLD2.0/Config/GameConfig.cs:15-20`：`class SceneConfig { loginScene=0; mainScene=1; battleScene=2; clearScene=3; }`（**整型常量**）。
- 旧链入口 `Client/Assets/Scripts/Server/Panel/UIMatchingPanel.cs:44`：新链优先——`PMDsEntryCodec.HasPrefix(pack.Str)` 命中即 `PMClientSessionHost.Enter(offer); return;`（解码失败只报错返回，**不回退旧链**）。
- `UIMatchingPanel.cs:65-66`（新链未命中时）：`BattleData.Instance.InitBattleInfo(pack.BattleInfo.RandSeed, pack.BattleInfo.BattleUsers);` + `Manger.ClearSenceManger.LoadScene(SceneConfig.battleScene);` ← **唯一的战前场景切换调用点**。
- `Client/Assets/Scripts/Manger/ClearSenceManger.cs:106-110`：`LoadScene(int)` 存 `nextScene` 并 `SceneManager.LoadScene(SceneConfig.clearScene)`（=3）；`:121` `SceneManager.LoadSceneAsync(scene)`（=2），`:126-141` 等 `UISliderPanel.IsCanEnterBattle` 后才 `allowSceneActivation = true`。
- 加载序列：**当前场景 →（3）`HYLDAsyncScence.unity`（清资源/进度条）→（2）`HYLDGame.unity`**；`clearScene` 与 `battleScene` 同源于一组整数常量。
- `HYLDManger.cs:226` 用 `SceneManager.GetActiveScene().name == "HYLDGame"` 判断"是否在战斗场景"，`:251` 回退 `LoadScene("HuangYeLuanDouStart")`（按名，且该场景 enabled=0 → 仅编辑器内可运行可达）。

### 1.2 BuildSettings 与"唯一场景路径"

`Client/ProjectSettings/EditorBuildSettings.asset`（全文已读）：

| index | enabled | path |
|---|---|---|
| 0 | 1 | `Assets/Scenes/HYLDLogon.unity` |
| 1 | 1 | `Assets/Scenes/HYLDStart.unity` |
| 2 | **1** | **`Assets/Scenes/HYLDGame.unity`** |
| 3 | 1 | `Assets/Scenes/HYLDAsyncScence.unity` |
| 4…11 | 0 | `Assets/RemakeHYLD/Scenes/TestUDP.unity`、`Assets/HYLD1.0/Scenses/{HuangYeLuanDouStart,HYLDInit,HYLDAsyncScence,HYLDGame,HYLDTryGame}.unity` 等 |

⇒ 「正式战斗地图」= **`Assets/Scenes/HYLDGame.unity`**（guid `df98059cdf2e0fc46b19bc2ad1a5be45`，967,319 B）。

附带事实：`Assets/Scenes/` 下另有两支**不在** BuildSettings 的战斗变体——`HYLDGameTest.unity`（Sep 18 11:54 修改，**含序列化的 `BattleManger` 组件**）、`HYLDReGame.unity`；二者在 `.cs`/`.unity`/`.prefab` 全仓文本中**零引用** → 只能编辑器里手动打开，运行时按名/索引都不可达。

### 1.3 地图不是资产，是运行期程序化生成

- `Client/Assets/HYLD1.0/Scripts/OldScripts/ScenseBuildLogic.cs:24`（MonoBehaviour；`HYLDGame.unity` 内有 1 个实例，guid `3f7e7d14aee5b8d4e9f720c9e57bc36a`）。
- 该组件在场景中的序列化字段（直接读 `HYLDGame.unity` 的 MonoBehaviour 块）：`mapx: 33`、`mapy: 21`；`floors[4]`、`walls[4]`、`obstacles[8]`、`Grasses[1]`、`trees[7]`；`RedSaveBox`/`BlueSaveBox`；`MAP: {fileID: 3262547810056346214}`；`current_mode: BaoShiZhengBa`（代码未读该字段）。
  - **关键细节**：`floors/walls/obstacles/Grasses/trees` 的每一项都是**无 guid 的场景本地 fileID**（例：`- {fileID: 3262547811206782900}` = 场景内 GameObject `Plain02 (1)`）⇒ 它们是**场景内联模板对象**，不是 prefab 资产引用；只有 `RedSaveBox`（guid `4643236f3a459d040a402d79a116b582`）/`BlueSaveBox`（guid `cf4e46d63d767d44e8395be3a634c765`）是外链 prefab。
  - **对 `_r3b_host_survey.md` §1.4 的一处更正**：该文写作「floors/walls/… 是场景组件上序列化的 **prefab 数组引用**」。实测这些数组项**没有 guid**，指向场景内模板 GameObject；且 `HYLDGame.unity` 内 `^--- !u!1001 &`（真正的 PrefabInstance 文档）**计数为 0**，该文中的「1582 个 PrefabInstance」实为 `m_PrefabInstance:` 字段出现次数（1582）。这不改变"地图不是静态几何"的结论，但改变可实施性：**新建 DS 场景无法只靠"引用"复用这些模板**，必须在编辑器里把模板复制/重烘焙成真 prefab，或直接复制进新场景。
- 生成算法 `ScenseBuildLogic.InitData():207-320`：
  - 模式分流（`:211-222`）：`HYLDTryGame → map2`；`HYLDBaoShiZhengBa → map2`（并把 ModenName 写回同名）；`HYLDJinKuGongFang → map4`。
  - 33×21 网格逐格：`0/1` 铺地板（从 `floors` 随机取），`2` 放障碍 + 地板，`5/6` 放红/蓝金库；随后沿 4 条边生成墙，再在外围生成树，最后把网格外圈铺地板补满。
  - 落点：`MyInstantiate` = `Instantiate(template, pos, q)` 后 `SetParent(MAP)`（`:312-317`）；网格坐标 `new Vector3(i - mapx/2, 0.01f, j - mapy/2)`，即玩法区 **x∈[-16.5,16.5]、z∈[-10.5,10.5]**。
  - 唯一调用点：`Scripts/Server/Manger/Battle/BattleManger.cs:133`（`BattleManger.Init()` 内）。
  - 四张地图数组均为 **35 行 × 21 列**（`mapx=33`/`mapy=21` 不会越界；实测行/列数）。
- 随机数：`InitData` 用 `UnityEngine.Random.Range(...)`，**无 `Random.InitState`**；`HYLDRandom.srand`（`BattleManger.cs:768-775`）**全工程 0 调用**（`grep -rn "srand("` 只命中定义处）。

### 1.4 资源加载方式

- 地图/场景几何：**无 Resources / AssetBundle / Addressables 加载路径**（全仓检索 `LoadScene`/`Resources.Load` 无地图相关命中）。
- 其它运行期资源：`Scripts/Manger/ResourceManger.cs:40` `Resources.Load<GameObject>(dic_typeMapToPath[type])`，路径为 `"Remake/" + Type`（`ResourceManger.cs:31-36`）；实际资产：`Client/Assets/Resources/Remake/{Player,Bullet,Gem}.prefab`。
- 子弹/大招/爆炸 prefab：`HYLDStaticValue` 上的序列化数组（`shells[18]`/`BetterShells[20]`/`大招实体[17]`/`Booms[4]`），全部是外链 prefab guid（读自 `HYLDGame.unity` 的该组件块）。
- `Assets/Scenes/OBJSence.unity` 是**唯一**引用 `MAP.prefab`（11,823,226 B，guid `6dedf814e48d78f4fa31f4d3a5ff6638`）的场景（PrefabInstance + `m_Name: MAP`）；该场景不在 BuildSettings、无代码引用 → 与正式战斗链无关（不要误当作"正式地图"）。

### 1.5 `HYLDGame.unity` 结构与其 Collider 构成

规模：403 GameObject / 89 MonoBehaviour / 291 MeshFilter + 291 MeshRenderer / 55 RectTransform / **43 Collider（21 MeshCollider + 22 BoxCollider，`m_IsTrigger` 全 0）** / 0 个 PrefabInstance 文档。

```
Main Camera(413438266)
HYLDGameTatal(517331440902661620)
├── MAP(1372061594275556908)
│   ├── Plane(7161483038451896960)   ← MeshCollider(convex=0), pos(0,0,0) scale(10,10,10)=100×100
│   └── lights(3)
├── Singleton of VirtualScreen
├── 狂暴圈完整[INACTIVE]
├── EventSystem
├── 开始游戏动画[INACTIVE]{GameBGM}
├── GemCreater
├── 3D(3262547811691410209)
│   ├── MAP(3262547810056346215)     ← ScenseBuildLogic.MAP：运行期生成物的容器（实测为空）
│   ├── Quad[INACTIVE]               ← MeshCollider(convex=0), scale 100
│   ├── Use                          ← 模板调色板
│   │   ├── floors   (Plain01/02, Plain_Trap, Plain_Poison)                 ← 无 Collider
│   │   ├── Obstacles(12 项：gravestone 系列 / well / cookingpot / Desert*) ← Box + Mesh Collider
│   │   ├── Grasses  (Tree Type6 03)                                       ← 无 Collider
│   │   ├── walls    (column_big/small、Plain01_4、Plain_Trap_4、Plain02_4、Plain_Poison_4) ← BoxCollider
│   │   └── trees    (tree_dead、coffin、dirtpile、Plain_Tree、Grave_Tree)  ← MeshCollider
│   ├── Box_Map[INACTIVE](8 子)
│   └── Rock Type3 01 (1)[INACTIVE]  ← BoxCollider
├── Directional Light
└── toolBox
```

- **地面**：`HYLDGameTatal/MAP/Plane` 的 MeshCollider（convex=0），世界 100×100、y=0，完整覆盖玩法区。
- **模板本体**（`3D/Use/*` 下的 GameObject）自身带 Collider 且多为 `m_IsActive=1`，但位置在编辑器"调色板"区：**x∈[-48.25, 207.22]、z∈[-10.42, 28.5]**，与玩法区 x∈[-16.5,16.5]/z∈[-10.5,10.5] **不重叠**（`floors` 模板则为 inactive）。⇒ 场景一加载就存在的 43 个 Collider 中，真正落在玩法区内的只有 `MAP/Plane`。
- **真正的可玩地图几何**：`InitData()` 实例化出的 `3D/MAP` 子物体（墙/障碍/树副本各携带模板 Collider；地板副本**不带** Collider，靠 `MAP/Plane` 兜地面）。
- 模板细节：多数旋转为 identity，但 `walls[3]=Plain_Poison_4 (1)`、`Obstacles` 的 `Plain01_4 (1)`/`Desert*` 等 `localPosition.y=1.399…`；树类 `localScale=(2,2,2)`；`MeshCollider` 中 `m_Convex` 有 0 有 1（非凸的不能挂在运动刚体上，静态可用）。

### 1.6 DS 现状：只打空引导场景，且**不加载任何场景资产**

- `Client/Assets/Editor/PMDsBuild.cs:35` `BootScenePath = "Assets/Scenes/PMDsBoot.unity"`；`:66` `options.scenes = new string[] { bootScene };`（**覆盖** BuildSettings，与 `EditorBuildSettings.asset` 无关）；`:148-190` `EnsureBootScene()` 缺失时用 `EditorSceneManager.NewScene(EmptyScene, Additive)` 现场生成并保存。
- `Assets/Scenes/PMDsBoot.unity`（实测 123 行）**0 GameObject**，只有 OcclusionCullingSettings / RenderSettings / LightmapSettings / NavMeshSettings。
- DS 权威碰撞环境是**运行期程序化**的（`Scripts/Server/Boot/PMDsSessionHost.cs`）：
  - `:48-56` `PMR3TestScene`：`FloorCenter=(0,-0.5,0)` / `FloorSize=(40,1,40)`、`WallCenter=(0,1,0)` / `WallSize=(1,2,8)`（两侧均 BoxCollider）。
  - `:693` `PMR3TestScene.BuildIsolated("[PMDsAuthority]", false, _sceneObjects, out scene, out physicsScene, out err)`（`LocalPhysicsMode.Physics3D`；硬校验"物理场景 ≠ `Physics.defaultPhysicsScene`"）。
  - `:715` `Collider[] allowlist = new Collider[2];`（**容量硬编码 2**）→ `:718` `TryCollectColliders` → `:740` `movementLayerMask |= 1 << layer` → `:746` `new PMUnityMoverCollisionQuery(physicsScene, mask, MovementWorldVersion, allowlist)`。
  - `:422` `public const int MovementWorldVersion = 1;`；`:637` 启动期校验 `boot.CollisionDigest != 0 && != PMR3Runtime.CollisionDigest` 即失败。
- 客户端新链同构：`Scripts/Server/Boot/PMClientSessionHost.cs:257` `BuildIsolated("[PMClientScene]", true, …)`、`:285` `Collider[] allowlist = new Collider[2]`、`:307` `new PMUnityMoverCollisionQuery(...)`。
- ⇒ **当前 DS 与客户端用的是同一套"临时程序化测试碰撞场景"**；客户端新链也**不加载** `HYLDGame.unity`（在 `UIMatchingPanel` 就 return 了）。
- 现有冻结理由（改动需同步更新契约的依据）：`PMDsSessionHost.cs:7-10` 注释（"那片场景的 Awake 链会拉起 HYLDManger / BattleManger / UDPSocketManger（绑硬编码 UDP 7777）"）；`Docs/plans/net-r3-control-contract.md:123`（"不加载旧 HYLDGame…无需改 Unity 场景资产，PMDsBoot 中由宿主程序化创建验证场景"）；`net-architecture-migration.md` R3-B 段（"不加载随机旧地图；运行时程序化构建确定测试碰撞场景，无需手写 Unity 场景资产"）。
- `_r3b_host_survey.md` §1.4/§2 已就同一问题给过候选方案（B-3：新建只含 `ScenseBuildLogic` 的 DS 场景并入 `options.scenes`；F7 `CollisionDigest`；F8 地图播种；I5 未播种则两端不同图），并把目标资产名预写成 `Client/Assets/Scenes/PMDsAuthority.unity`。**本轮实测确认：该资产目前并不存在**（`Scenes/` 目录清单无此文件）。

### 1.7 直接加载 `HYLDGame.unity` 会触发的 Awake/Start 链（场景内 15 个脚本）

| 脚本（场景内） | 触发点 | 后果（DS 视角） | 分类 |
|---|---|---|---|
| `HYLDStaticValue.cs` | `Awake():155` | 重建 Heros 表（`:185-207`）；`ModenName==HYLDBaoShiZhengBa` → `gameObject.AddComponent<HYLDBaoShiZhengBaManger>()`(`:216`)；赋 ISNet / `_StartGameAni` / BG / `ScenseBuildLogic` / toolbox → `Init()`。场景内序列化值 **`ISNet:1`、`testMOdel:1`**，因 `if(!ISNet)` 不成立，`ModenName` 保持静态默认 `"HYLDBaoShiZhengBa"`(`:51`) | 战斗/网络 |
| `HYLDBaoShiZhengBaManger.cs:22` | 由上一步 `Init()` | `base.Init()` → `GameObject.Find("HYLDGameTatal").Find("GemCreater")` → `creatGemLogic.InitData()` | 战斗 |
| `BattleManger.cs:119-141` | 由 `Init()` | `AddComponent<HYLDPlayerManger / HYLDCameraManger / HYLDBulletManger>()`；**`ScenseBuildLogic.InitData()`(:133) ← 地图在这里才生成**；`playerManger.InitData()`(:135，`HYLDResourceManger.Load(Player)` + 出生点 (15,1,-5/0/5)/(-15,1,5/0/-5))；`cameraManger.InitData()`；`bulletManger.InitData()`；`AddComponent<BattleFrameHud>()` + `Init(this)`；`NetGlobal.Instance.Init()` | 战斗 + 表现 |
| `BattleManger.cs:143-149` | `Start()` | `Server.UDPSocketManger.Instance.InitSocket()` → **硬编码 `NetConfigValue.ServiceUDPPort = 7777`**（`Scripts/Server/ConstValue.cs`）→ 与 DS 自身战斗端口/新链端点冲突；`Handle = HandleMessage`；`StartCoroutine(WaitInitData())` | 旧网络（**冲突**） |
| `BattleManger` `FixedUpdate`/`Update` | 每帧 | `FixedUpdate` → `cameraManger.OnLogicUpdate()`；`Update` 受 `_battleNetworkActive` 门控（首权威帧前不跑） | 表现 |
| `HYLDCameraManger.cs:30-45` | `InitData()` | `StartCoroutine(InitCamera())` 内 `WaitUntil(() => HYLDStaticValue.playerSelfIDInServer != -1)` → DS 上**永不满足**，协程永久挂起 | 表现（无效等待） |
| `HYLDPlayerManger.cs:32-45` | `InitData()` | `HYLDResourceManger.Load(Player)` 立即 Instantiate 玩家预制体（含 bodyAnimator）；`BattleData.Instance.list_battleUsers` 在**旧链未跑**时为空列表（`BattleData.Instance` 是纯 C# 单例 `new BattleData()`，`BattleData.cs:123-133`） | 表现 |
| `Toolbox.cs` | `Awake():269` / `Start():48`（协程） | UI `SetActive`、`模式开始台词` 文本、胜负循环 → `Manger.BattleManger.Instance.BeginGameOver()` | UI |
| `TouchLogic.cs` + `EasyTouch.cs` / `EasyJoystick.cs` / `EasyButton.cs` / `VirtualScreen.cs` | `OnEnable():72` / `Start():160`；插件 `Awake/OnEnable/Start` | 触摸输入、`CommandManger.Instance`；`VirtualScreen.Awake():38` | 输入/UI |
| `HYLDModenProp.cs` | `Start():18` | 狂暴瓶：客户端本地 +1 移速 / +30% 伤害血量（**S8 已知缺陷**）；`是搞服务器的` 分支可 `Instantiate(服务器)` | 表现/玩法 |
| `CreatGemLogic.cs:34-45` | `InitData()` | `HYLDResourceManger.Load(Gem)` + `Physics.IgnoreLayerCollision(8,8)`（**改全局物理设置**）；`OnLogicUpdate` 内 `NetGlobal.Instance.AddAction` 生成宝石 | 玩法/表现 |
| `HYLDGameOver.cs:17`、`GameUITeamGemLogic.cs:29`、`UITextEffect.cs:25`、`HYLDHeropropertyUI`、`ScaleSelf` | `Start` | 结束判定 UI、宝石 UI 计数、文本特效、英雄属性 UI、缩放动画 | UI |
| `ScenseBuildLogic` 自身 | 无 `Awake/Start` | 只被外部调 `InitData()` | 地图生成 |

补充：**`HYLDManger` 不在该场景内**（它挂在 `Assets/Scenes/HYLDLogon.unity`，其 `Awake` 有 `PMNetRuntime.IsDedicatedServer` 守卫）⇒ 加载 `HYLDGame.unity` 的 DS 上 `HYLDManger.Instance` 为 `null`，其守卫机制**不覆盖**上表任何脚本（`HYLDManger.cs` 的显式 DS 门在 `Awake`/`Update`/`Send` 三处）。

### 1.8 种子缺口（DS/客户端同图的前置）

- `BattleInfo.rand_seed = 2` 存在（`Client/Assets/Scripts/Server/SocketProto.cs:2435-2442`）。
- 服务端填值：`Server/Server/Battle.cs:179` `battleInfo.RandSeed = randSeed;`。
- 客户端存值：`Manger/BattleData.cs:121` `public int randSeed { get; private set; }`，`:239` `randSeed = _randSeed;`，**唯一入口** `UIMatchingPanel.cs:65`（旧链）。
- **无消费者**：`HYLDRandom.srand`（`BattleManger.cs:768`）0 调用；`ScenseBuildLogic.InitData` 用全局 `UnityEngine.Random`；全仓无 `Random.InitState`。⇒ 若两端各自生成地图，**必然不同图**（正是 `_r3b_host_survey.md` 的 F8/I5）。

---

## 2. 高概率推断（依据 + 置信度）

1. **直接加载 `HYLDGame.unity` 到 DS 不能作为最小接入方案**（置信 0.95）。依据：`BattleManger.Start()` 绑 7777（§1.7）与 DS 新链端点独占同端口；表现层对象/UI/相机协程副作用；且地图生成依赖 `BattleData`/`ScenseBuildLogic` 序列化字段与未播种随机。
2. **"新建 DS 专用权威场景"无法只靠"引用"复用地图**（0.9）。依据：`floors/walls/obstacles/trees` 数组项**无 guid**、指向场景内模板对象（§1.3 更正）；且 `HYLDGame.unity` 无 PrefabInstance 文档。必须先决定"模板复制进新场景"还是"提升为真 prefab / 静态表"。
3. **真实地图接入必然要改 `MovementWorldVersion` 与 `CollisionDigest` 口径**（0.85）。依据：`PMDsSessionHost.cs:422/637` 把版本与摘要当启动期硬校验；R4-B 报告 §9.7 已登记 `0x52334201 / 1` 双常量口径未统一。
4. **真实地图会落到碰撞适配器的"bounds 近似"分支**（0.9）。依据：`Scripts/PMUnity/PMUnityMoverCollisionQuery.cs` 头注释"精确性边界"——非 identity 旋转的 Box / Mesh 上 `Collider.bounds` 只是超集，行为是**保守早挡、绝不漏墙**（并有 `NonBoxBoundsClassifications` 计数）；真实地图 21 个 MeshCollider 多为 `convex=0` 且多数非轴对齐。
5. **白名单机制能隔离掉模板调色板 Collider，但容量必须改**（0.9）。依据：`:715` / `PMClientSessionHost.cs:285` 硬编码 `new Collider[2]`；模板 Collider 虽不在玩法区（§1.5），但若改成"场景内全量 Collider"就会把它们一并收进来。
6. **`HYLDGameTest.unity` 很可能是当时的临时测试场景**（0.8）。依据：它是唯一含序列化 `BattleManger` 组件的场景（正式场景靠 `AddComponent`），修改时间较新，且不在 BuildSettings、无引用——与"从临时测试场景推进真实地图加载"相符（但"临时测试场景"也可能指 `PMR3TestScene` 程序化场景，见 §3.2）。

---

## 3. 无法确定（缺证据 / 需产品与主侧裁定）

1. **目标地图未定**：网络模式恒 `map2`（源码注释即"map2测试地图"），另一张是 `map4`（金库攻防）；`HYLDGameTest.unity` / `TestUDP` / `rand_seed` 都暗示尚未定版。产品要哪张、是否需要多图选择，无证据。
2. **DS 的接入形态未定**：加载 Unity 场景资产 vs 运行期生成（R3-B 冻结了后者；用户新授权未指明路径）。
3. **`HYLDGame.unity` / `HYLDGameTest.unity` / `HYLDReGame.unity` 三者语义**无文档；谁是最新主干无法从仓库事实判定。
4. **地图种子契约未冻结**：`rand_seed` → 哪一端以何种确定顺序驱动生成、是否需要跨端一致的 `Random.InitState` 时机，未定义。
5. **无头环境实跑缺失**：未验证 DS 无头包里 `Resources.Load("Remake/Player")`、`AddComponent<BattleFrameHud>`（会创建运行时 HUD 面板）、`Camera.main` 相关路径会不会报错或产生不可见副作用（本轮只读，未启动 Unity、未跑 DS）。
6. **权威地图与表现地图的分工未定**：是否允许客户端继续走旧 `HYLDGame.unity`（旧链已加载），还是两端都改走新生成器——这决定"同图"是"同一份数据"还是"同一套生成器 + 同一种子"。

---

## 4. 已检查范围（必读证据）

**按必读顺序读完**：
1. `D:/UGit/hyld-master/AGENTS.md`（三份下游文档入口）
2. `Client/Assets/AGENTS.md`（进程形态判定、坐标系、`frameTime`、主链路、§5 关键文件索引）
3. `Server/AGENTS.md`（Lobby/战斗现状、"服务端不含 Unity"的历史边界）
4. `Docs/plans/net-architecture-migration.md`：卷首"快速接手 / 环境现状 / 本机环境的坑"、§3.9 全节（A1–A9、M01–M13、运行时契约、完成判据）、§5 阶段表、§5.1–5.6、§6（T23/T27/T34）、文末 R3-B 与 R4-B 段（含 P4B6 状态）
5. `Docs/plans/net-r4-network-contract.md`（B1/B2/B3 与"最终场景隔离实现"）
6. `Docs/plans/_r4b_real_unity_movement.md`（真实 DS 运动验收、碰撞替身口径、§9 未覆盖清单）

**另读（同题既有调查，用于对齐而非重复）**：`Docs/plans/net-r3-control-contract.md`（:123 场景冻结）、`Docs/plans/_r3b_host_survey.md`（§1.4 场景调查、§2 风险 I4/I5、§4 F7/F8、§5 B-3/B-4、预写的 `PMDsAuthority.unity`）。

**源码/资产（只读）**：
`Client/ProjectSettings/EditorBuildSettings.asset`；`Client/Assets/Editor/PMDsBuild.cs`；`Client/Assets/Scenes/{PMDsBoot,OBJSence,HYLDGame}.unity`（YAML 结构全量解析：层级、组件计数、Collider 清点、模板/调色板坐标、序列化字段）；`Scenes/*.unity.meta`、`MAP.prefab.meta`；
`Scripts/Server/Panel/UIMatchingPanel.cs`；`Scripts/Manger/ClearSenceManger.cs`；`HYLD2.0/Config/GameConfig.cs`；`HYLD1.0/Scripts/OldScripts/ScenseBuildLogic.cs`；`HYLD1.0/Scripts/OldScripts/HYLDStaticValue.cs`；`HYLD1.0/Scripts/OldScripts/Toolbox.cs`；`HYLD1.0/Scripts/OldScripts/TouchLogic.cs`；`HYLD1.0/Scripts/OldScripts/HYLDGameOver.cs`；`HYLD1.0/Scripts/OldScripts/Moden/ts/Moden/HYLDModenProp.cs`；`…/BaoShiZhengBa/{CreatGemLogic,GameUITeamGemLogic}.cs`；`Scripts/Server/Manger/Battle/{BattleManger,BattleData,HYLDBaoShiZhengBaManger,HYLDPlayerManger,HYLDCameraManger}.cs`；`Scripts/Server/Manger/HYLDManger.cs`；`Scripts/Server/ConstValue.cs`；`Scripts/Manger/ResourceManger.cs`；`Scripts/Server/Boot/{PMDsHost,PMDsSessionHost,PMClientSessionHost}.cs`；`Scripts/PMUnity/PMUnityMoverCollisionQuery.cs`；`Scripts/Server/SocketProto.cs` 与 `Server/Server/Battle.cs`（`rand_seed` 链路）；`Client/Assets/Resources/` 目录清单。

**工具与方法**：`fffind` / `ffgrep`（含工作区外绝对路径索引 `D:/UGit/hyld-master`）+ 文本 YAML 解析 + 脚本统计。未使用二进制资产读取器（Unity 资产为文本 YAML，适用）。未启动 Unity、未编译、未改动源码/场景/资产、未提交、未递归委派。

**未纳入（硬边界）**：角色/表现行为研究（另组负责）；未做全仓资产扫描（只沿直接引用追踪）；未复核历史报告中的测试数字。

---

## 5. 建议下一步

### 5.1 最小补充查询（只读）
1. 请产品/主侧裁定 **目标地图**（`map2` vs `map4`，是否需要多图选择）。
2. 请主侧裁定 **DS 接入形态**：允许 `SceneManager.LoadScene`（需并入 `options.scenes`）还是坚持"运行期生成"（需把模板提升为可引用资产）。
3. 只读核对 `_r3b_host_survey.md` 的 F7/F8 冻结结论是否已落地（本轮未见实现：`PMDsAuthority.unity` 不存在，`srand` 无调用，`CollisionDigest` 仍为测试布局摘要）。

### 5.2 运行时验证（用户在 Unity 内，AI 不抢锁）
- 在编辑器内新建/打开权威场景，确认 `ScenseBuildLogic` 外**无** `HYLDStaticValue`/UI/相机/Toolbox，然后打一个测试 DS 包跑 `run_ds.bat`，确认 DS 日志无 `HYLDStaticValue`/`BattleManger` 痕迹、无 7777 绑定。
- 用同一 `mapSeed` 在 DS 与客户端各生成一次，比对碰撞体集合摘要（digest）与关键几何 AABB。

### 5.3 可独立实施文件组（互不重叠，可并行）

| 组 | 文件边界 | 内容 | 依赖 |
|---|---|---|---|
| **D（构建/场景资产，必须编辑器内、串行）** | `Client/Assets/Editor/PMDsBuild.cs` + 新场景资产 `Client/Assets/Scenes/PMDsAuthority.unity`(+`.meta`) | 把权威场景并入 `options.scenes`（`PMDsBuild.cs:66`）；场景内只放地图几何/模板 + `ScenseBuildLogic`（或纯几何容器），**不放** `HYLDStaticValue`/UI/相机/Toolbox/BattleManger；沿用 `EnsureBootScene` 风格保证幂等生成 | 依赖 5.1-2 裁定 |
| **M（地图构建核心，纯 C#，可离线门禁）** | 新增 `Client/Assets/Scripts/PMR4/PMR4MapBuilder.cs`（namespace `PMNet.R4`，配 `.meta`）；若必须改旧脚本则只碰 `ScenseBuildLogic.cs` 的**显式种子入口** | 显式 `mapSeed` + 模式→地图表；产出碰撞体集合与稳定摘要（digest）；不依赖 `UnityEngine.Random` 全局态 | 依赖 5.1-3 / §3.4 种子契约 |
| **H-DS（宿主）** | `Client/Assets/Scripts/Server/Boot/PMDsSessionHost.cs` | 把 `PMR3TestScene.BuildIsolated` + `new Collider[2]` 换成"地图碰撞体集合"（容量参数化/有界，饱和显式失败）；`MovementWorldVersion` 升版与 digest 计算 | 依赖 M 组接口 |
| **H-CL（客户端宿主）** | `Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs` | 与 DS 同构改造，保证两端同一生成器 + 同一种子 | 依赖 M 组接口 |
| **T（测试/门禁，可选）** | `Tools/PMR3UnitySmoke/`、`Tools/PMR4UnityCheck/`（仅在需要时新增断言） | 地图等价性（digest / 关键 AABB）、白名单容量与饱和路径、`SceneReady` 只在真实碰撞就绪后置位 | 依赖 H 组 |

`PMDsSessionHost.cs` 与 `PMClientSessionHost.cs` 是**两份文件、两个组**，接口相同但可在冻结接口后并行。

### 5.4 必须冻结的接口（实施前）

1. **WorldVersion / CollisionDigest**：地图版本常量与摘要的**单一计算口径**（输入、编码、字节序）；DS 启动期 `boot.CollisionDigest` 校验同步升版。
2. **地图标识与种子**：`mapId`（模式→地图表）与 `mapSeed` 的取值来源、传递路径（谁生成、谁下发）、进入生成器的**确定顺序**；明确禁止依赖 `UnityEngine.Random` 全局态。
3. **碰撞体允许清单契约**：由"固定 2 项 `Collider[]`"改为"由地图生成器产出的碰撞体集合"，含**容量上限**、**饱和行为**（显式失败，不允许静默忽略墙）与是否包含调色板/非玩法区 Collider 的判据。
4. **场景加载契约**：DS 是否允许 `LoadScene`；权威场景路径常量；**DS 路径禁止使用 `SceneConfig` 整数索引**（DS 构建场景表与编辑器不同，见 `_r3b_host_survey.md` §1.4"索引陷阱"）。
5. **旧脚本抑制策略**：权威场景内"禁用组件" vs "不放入场景"二选一，并显式禁止 `BattleManger` / `UDPSocketManger` / `Toolbox` / `TouchLogic` / `HYLDModenProp` 被 `AddComponent` 或 `Find` 到。
6. **权威/表现分工**：客户端是否仍走旧 `HYLDGame.unity` 加载；若同图靠"同生成器 + 同种子"，需明确客户端生成时机（在 `UIMatchingPanel` 新链 return 之后由 `PMClientSessionHost` 负责）。
7. **`SceneReady` 判据**：必须由"真实地图碰撞体 enabled/active 且摘要可比"决定；空场景/临时地板不得报 ready（沿用 §3.9.2 与 R3 契约 §7.4）。
