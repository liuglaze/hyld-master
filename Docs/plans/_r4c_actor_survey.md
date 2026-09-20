# R4-C 为新 Mover 绑定「正式角色 / 输入」有界调查

> 任务类型：explore（只读调查）。本文是**调查证据报告**，不是实施计划，不改主计划/源码/场景。
> 状态与验收口径仍以 `Docs/plans/net-architecture-migration.md`（P4B6）与其后修复报告为准。
> 本轮**不重查网络核心**（R1–R4-B 的传输/复制/预测/驱动已有门禁），只回答「正式角色、英雄/名册、出生位、输入、相机、Animator 如何与 PMR4MovementDriver 接合」。

## 0. 必读文档与阅读次序（已按序完整阅读）

| 序 | 文档 | 对本轮的直接作用 |
|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 仓库入口，确认三份下游文档 |
| 2 | `Client/Assets/AGENTS.md` | 客户端现状：§2 坐标系（X 水平、Z 屏幕纵向、Y 固定高度=1）、出生点我方 `(15,1,-5/0/5)` 敌方 `(-15,1,5/0/-5)` 镜像、相机 Z 轴锁死、§3.1 输入层、§3.2 命令层、§1.1 客户端/DS 同二进制按运行时判定 |
| 3 | `Server/AGENTS.md` | 服务端「不含 Unity」的历史边界、UDP/战斗链路索引 |
| 4 | `net-architecture-migration.md`（卷首 / §3.9 / §5 阶段表 / 文末 R4-B） | §3.9.3 模块职责（M08 Mover / M10 BattleGameplay / M11 UnityHost）、§3.9.4 运行时契约（不得另造第二套时钟/身份）、§5 表 R4/R5/R6 边界、文末 R4-B 计划与主侧裁决 |
| 5 | `net-r4-network-contract.md` | §B1 承载冻结、§B2 Unity 适配器契约（输入不引用旧 CommandManger/TouchLogic、不双镜像）、§B3 主侧集成（出生点必须在墙外、WorldVersion 统一 1、Input 接受后 Consume） |
| 6 | `_r4b_host_integration_report.md` | B3 实际接线：DS/客户端调用次序、冻结 API 签名、113/0 证据、诚实边界（真实 PhysX/Unity 仍 PENDING_USER） |

**由文档决定的首搜入口**（非盲搜）：`Scripts/Server/Manger/Battle/HYLDPlayerManger.cs`、`HYLD1.0/Scripts/OldScripts/HYLDPlayerController.cs`、`HYLD1.0/Scripts/OldScripts/TouchLogic.cs`、`Scripts/Manger/CommandManger.cs`、`Scripts/Server/Manger/Battle/HYLDCameraManger.cs`、`Scripts/PlayerPrefabConstValue.cs`、`Scripts/Server/Boot/PMClientSessionHost.cs`、`Scripts/PMUnity/PMUnityMoverPresentation.cs`。

---

## 1. 已确认（文件:行 证据）

### 1.1 正式角色预制体链（旧链）

| 事实 | 证据 |
|---|---|
| 玩家预制体**只有一个**，路径由代码拼：`Resources.Load<GameObject>("Remake/" + Type)`，`Type.Player` → `Resources/Remake/Player.prefab` | `Scripts/Manger/ResourceManger.cs:30`（`string reamke = "Remake/"`）、`:40`（`Resources.Load`）；`Scripts/Server/Manger/Battle/HYLDPlayerManger.cs:37` |
| 预制体实体存在 | `Client/Assets/Resources/Remake/Player.prefab`（另有 `Bullet/Gem`） |
| 每个玩家实例由该预制体 `Instantiate` 得到；`transform.GetChild(0)` 是身体（名为 `Capsule`） | `HYLDPlayerManger.cs:101`；`Player.prefab` 根 `m_Name: Player`，child0 为 `Capsule` |
| 根节点挂 **HYLDPlayerController**（guid `6e67998e…`）+ **PlayerLogic**（guid `faeae29b…`）+ 第三个 MonoBehaviour（guid `66e3984f…`，字段 `selfTransform/isRunning/fightDistance/moveDistance/PlayerID`） | `Player.prefab` 根 `m_Component` 列表 |
| 身体 `Capsule` 的组件：MeshFilter(!u!33)、MeshRenderer(!u!23)、**Rigidbody(!u!54)**、**BoxCollider(!u!65)**、MonoBehaviour(!u!114，guid `e831e0f6…`=`HYLD1.0/Scripts/OldScripts/Bullet/s/Bullet/移动型大招.cs`)、**Animator(!u!95)** | `Player.prefab` 对应 `--- !u!` 段 |
| 根上第三个 MonoBehaviour 的 guid **在任何 `.cs.meta` 中都查不到**（仓库内仅出现在 `Player.prefab`、`Library/ArtifactDB`）→ 该脚本已被删除，预制体上是 **missing script** | `grep -rl 66e3984fb39329042ac11aabad97b6d8 --include=*.meta` = 0 命中 |
| 英雄**不决定**任何模型/预制体：`HYLDStaticValue.AddHero(...)` 只登记 `displayName/positioning/shell/大招实体/boom/isSuperMovingType`；`Hero` 类只有这些字段 + 只读数值属性，**无模型/prefab 字段** | `HYLDStaticValue.cs:88-105`（AddHero 定义）、`:160-180`（20 个 AddHero 调用，无模型参数）；`HYLD1.0/Scripts/OldScripts/HeroData.cs`（Hero 定义） |

**结论：旧链「正式角色」= 单一 `Player.prefab`（胶囊体 + Animator + Rigidbody/BoxCollider + HYLDPlayerController/PlayerLogic/一个 missing script），英雄只改子弹/大招引用与共享数值表，不改外观。**

### 1.2 英雄选择 / 名册 / 关联方式（旧链 vs 新链）

**旧链（TCP 大厅 → 战斗场景）**

| 步骤 | 证据 |
|---|---|
| 玩家在 UI 选英雄 → `HYLDStaticValue.myheroName` / `_myheroName`，并写 `PlayerPrefs`（键 `HYLDPlayerHero`） | `HYLD1.0/Scripts/OldScripts/UI/ripts/UI/HYLDStartUILogic.cs:42-43,167-205`；`Scripts/PlayerPrefabConstValue.cs:15`（`HYLDPlayerHero = "HYLDPlayerHero"`） |
| 入局时把英雄名塞进 `playerPack.Hero`（proto 枚举 `SocketProto.Hero`）上行给大厅/服务端 | `Scripts/Server/Panel/UIStartMainPanel.cs:135`、`Scripts/Server/Panel/UIInvatingFriendPanel.cs:320`（`playerPack.Hero = (SocketProto.Hero)Enum.Parse(... HYLDStaticValue.myheroName)`） |
| 服务端广播战斗包 `pack.BattleInfo.BattleUsers`（`BattlePlayerPack`：`Id/Teamid/Roomid/Playername/Hero/Battleid`） | `Scripts/Server/SocketProto.cs:160`（字段列表） |
| 客户端 `UIMatchingPanel.OnResponse` → `BattleData.InitBattleInfo(RandSeed, BattleInfo.BattleUsers)` → `ClearSenceManger.LoadScene(SceneConfig.battleScene)` | `Scripts/Server/Panel/UIMatchingPanel.cs:63-67` |
| `BattleData.list_battleUsers` 就是这群 `BattlePlayerPack` | `Scripts/Server/Manger/Battle/BattleData.cs:111,234-242` |
| `HYLDPlayerManger.InitData()` 遍历名册：`player.Hero` 经 `(HeroName)((int)player.Hero)` 查 `HYLDStaticValue.Heros` 得到 `Hero`，与出生位、队伍、玩家类型一起装进 `PlayerInformation` | `HYLDPlayerManger.cs:60-84` |
| 自己由 `player.Battleid == battleid` 判定，队友由 `player.Teamid == teamID` 判定 | `HYLDPlayerManger.cs:62-80` |

**新链（PMNet / DS）**

| 事实 | 证据 |
|---|---|
| 名册结构 **携带 HeroId**：`PMDsRosterIdentity{Uid,PlayerId,TeamId,HeroId}` | `Scripts/PMNet/Control/PMDsControlProtocol.cs:155-203` |
| HeroId 一路进票据、握手、连接身份 | `PMNet/Control/PMDsTicket.cs:147`；`PMNet/Session/PMHandshake.cs:181,465,506,1113,1534`；`PMNet/Session/PMTransportConnection.cs:221`；`PMNet/Session/PMUdpSessionEndpoint.cs:668,832` |
| **但 DS 宿主与客户端宿主都不消费 HeroId**（`grep HeroId` 在两个宿主文件中 0 命中）；DS 只用 `TeamId` + 名册行号算出生位 | `PMDsSessionHost.cs:646-669`（只读 Uid/TeamId 建 `_rosterRowByUid/_rosterTeamByUid`）、`:1016-1049`（BuildSpawnSync 只用 row/team） |
| 新链客户端入口识别 `PMDS1:` 前缀即 `PMClientSessionHost.Enter(offer)` 并**直接 return**：不 `InitBattleInfo`、不 `LoadScene(battleScene)` | `Scripts/Server/Panel/UIMatchingPanel.cs:44-56` |
| 因此纯新链**不加载** `Scenes/HYLDGame.unity`，旧链的 HYLDManger/BattleManger/HYLDStaticValue/TouchLogic/旧相机/Player.prefab **都不存在** | 同上 `return`；`PMDsSessionHost.cs:7-9` 明确「不加载旧 HYLDGame 场景」 |

**结论：新链已把 HeroId 运到两端，但没有任何一处消费它；角色的「英雄/外观」在新链里目前是**空缺**。旧链的关联方式是「名册 `BattlePlayerPack.Hero` → `HYLDStaticValue.Heros` 字典 → 该玩家的子弹/大招/数值」，与服务端的 proto Hero 枚举逐值对齐。共享侧已有等价表 `PMNet.Shared.PMHeroId` 与 `BattleNumericConfig`（`Scripts/Shared/BattleNumericConfig.cs:25`）。**

### 1.3 出生位

| 事实 | 证据 |
|---|---|
| 旧链：两支出生位队列硬编码，我方 `(15,1,-5)(15,1,0)(15,1,5)`，敌方镜像 `(-15,1,5)(-15,1,0)(-15,1,-5)`；按名册遍历顺序 `Dequeue` | `HYLDPlayerManger.cs:44-53`、`:60-84`（`myteam.Dequeue()`/`otherTeam.Dequeue()`） |
| 复活位另有一份硬编码（我方 `(15,1,0)`、敌方 `(-15,1,0)`） | `HYLD1.0/Scripts/OldScripts/PlayerLogic.cs:200-215` |
| 新链：`BuildSpawnSync(uid)` → `x = ±3`（team 1→−，team 2→+，未知按行奇偶）、`z = 名册行居中 × 1.5`、`y = 胶囊半高` | `PMDsSessionHost.cs:1016-1049`；常量 `SpawnLateralOffsetMeters=3f`（`:428`）、`RosterRowSpacingMeters=1.5f`（`:425`） |
| 新链出生位刻意避开原点（原点落在测试墙盒 `x∈[-0.5,0.5]` 内） | `PMDsSessionHost.cs:1005-1014` 注释 |

**结论：新旧出生位**不同一空间**（±15 vs ±3），且新链出生位只与「名册行 + 队伍」关联，与英雄无关。**

### 1.4 现有组件可复用性判定（哪些写 Transform / 哪些启动旧 SavedMove）

**写世界 Transform（渲染层写者，绑定新驱动时是冲突源）**

| 组件 | 写什么 | 证据 |
|---|---|---|
| `HYLDPlayerController.Update()` | `selfTransform.position = Vector3.MoveTowards/Lerp(...)`；`selfTransform.LookAt(...)` | `HYLD1.0/Scripts/OldScripts/HYLDPlayerController.cs:56-88`（`selfTransform.position=` 出现 3 处） |
| `HYLDPlayerController.FixedUpdate()` | `Ani.SetFloat("Speed", player.playerMoveMagnitude)`（**Animator 唯一驱动点**） | 同文件 `:92-104` |
| `PlayerLogic.Update()` | `selfUITransform.position = selfBodyTransform.position` | `HYLD1.0/Scripts/OldScripts/PlayerLogic.cs:139` |
| `PlayerLogic.playerRevive()/playerDieLogic()` | 写 `selfBodyTransform.position`、开关 `BoxCollider`、`bodyAnimator.SetBool/SetTrigger` | 同文件 `:196-260` |
| `HYLDCameraManger.LateUpdate()` | 写**自己所在 GameObject** 的 `transform.position`（SmoothDamp，Z 锁死）；数据源是 `HYLDStaticValue.Players[playerSelfIDInServer].body.transform.GetChild(0).position` | `Scripts/Server/Manger/Battle/HYLDCameraManger.cs:57-77` |

**写逻辑位置（非 Transform）**

| 组件 | 写什么 | 证据 |
|---|---|---|
| `HYLDPlayerManger.AdvancePlayerPosition()` | `player.playerPositon = (outX, y, outZ)`，公式来自共享 `PMNet.Shared.PMBattleSim.TryAdvancePosition`（带 `sign` 镜像） | `HYLDPlayerManger.cs:175-205` |
| `HYLDPlayerManger.ApplyMovementInput()` | `player.playerMoveX/Y/Dir/Magnitude`（表现字段，停步显式清零） | 同文件 `:196-215` |

**启动旧 SavedMove / 旧预测的唯一路径**

| 组件 | 作用 | 证据 |
|---|---|---|
| `BattleManger.BattleTick()` | `RecordPredictedHistory(nextFrame, …)` → `CommitPredictedFrame(nextFrame)` → `SendOperation(...)`；即旧 SavedMove/pendingMove/MoveAck 链的唯一启动点，由 `BattleManger.Update()` 累加器驱动 | `Scripts/Server/Manger/Battle/BattleManger.cs:415`（BattleTick 定义）、`:496`（RecordPredictedHistory）、`:498`（CommitPredictedFrame）、`:539`（SendOperation）；`:158-200`（Update 驱动） |
| `BattleData.Prediction.cs` | `RecordPredictedHistory` 建 `SavedMove`、`BuildClientMovesForSend` 组 pending/DualMove | `Scripts/Server/Manger/Battle/BattleData.Prediction.cs:32-137` |
| `HYLDPlayerManger` | **不启动** SavedMove；只管逻辑位置推进与表现字段 | `HYLDPlayerManger.cs:151-215` |

**输入链（旧）**

| 组件 | 作用 | 证据 |
|---|---|---|
| `TouchLogic`（挂在摇杆 UI 根 `Android` 上，`能量条`/`大招遥感` 字段） | `EasyJoystick` 事件 `OnJoystickMove`/`JoystickMoveEnd`：`PlayerMove` → 写 `HYLDStaticValue.PlayerMoveX/Y` + `CommandManger.AddCommad_Move`；`FireNormal`/`FireSuper` 松手 → `AddCommad_Attack`/`AddCommad_SuperAttack` | `HYLD1.0/Scripts/OldScripts/TouchLogic.cs:80-145`（松手/攻击/大招）、`:167-300`（移动轴 + 死区 0.18/0.12 滞回） |
| `CommandManger` | 单例纯 C#（无 meta/非 Unity 组件）；`AddCommad_Move` 只记最新值；`Execute()` 写 `BattleData.selfOperation.MoveX/Y`、消费离散命令、`FlushPendingAttacksToOperation()` | `Scripts/Manger/CommandManger.cs:97-127` |
| `BattleData.EnqueueAttack` | 摇杆松手 → 入队 `pendingAttacks`（先按本地英雄蓝耗预测扣蓝） | `CommandManger.cs:75-83` |

**新链现状（作为「唯一运动源」的既有事实）**

| 事实 | 证据 |
|---|---|
| 每个副本（AP **与** SP）都建 `PMR4MovementDriver`；`OnPlayerReplicated` 不再跳过 SP | `PMClientSessionHost.cs:648-712`（EnsureMovementRig）、`:714-780`（OnPlayerReplicated） |
| 客户端每帧次序：`Physics.SyncTransforms()` → `endpoint.Pump`（入站/校正） → 子步 `Sample`（不消费）→`Tick`→**接受后**`Consume`→`SendInputPayload` → SP `Advance`+`SamplePresentation`+`ApplyInterpolated` → AP `ApplyPredicted` | `PMClientSessionHost.cs:505-640`、`:330-400`（PumpActive） |
| 表现层目前是 **primitive 胶囊**，不是正式预制体；DS 上构造**直接抛异常** | `Scripts/PMUnity/PMUnityMoverPresentation.cs:100-110`（DS 抛）、`:112-140`（`GameObject.CreatePrimitive(Capsule)`）、`:183-200`（Apply 只写 root position/rotation + body localScale） |
| `PMUnityMoverPresentation` **没有外部预制体注入点**（构造只有 `label/role/createTestCamera`） | 同文件构造签名 `:76-95` |
| DS 侧需要的东西：`LocalPhysicsMode.Physics3D` 隔离场景内的地板+墙两个 `BoxCollider`（白名单，缺失即拒绝启动）+ `PMUnityMoverCollisionQuery` + 每玩家 `PMR4MovementDriver` + `PMR3Player`（纯字段网络对象，无 prefab）+ `PMDsHost`（MonoBehaviour，唯一 Update 驱动） | `PMDsSessionHost.cs:45-57`（测试场景常量）、`:693-770`（BuildIsolated + allowlist 校验）、`:1097-1135`（AttachMovementDriver）、`:860`（Pump）、`Scripts/Server/Boot/PMDsHost.cs:36,132-134,380`（MonoBehaviour/Update） |
| DS **不需要**：Camera、Animator、输入采样、表现层、旧地图；宿主与表现层里 `Resources.Load`/`Animator`/`HYLDStaticValue` 均 0 命中 | `grep Resources.Load\|Animator\|HYLDStaticValue` 于两个宿主 + 表现层 = 0 |

**新链输入适配器事实**

| 事实 | 证据 |
|---|---|
| `PMUnityMoverInput` 读 WASD/方向键 + 可选 `Horizontal/Vertical` 虚拟轴 + Space 边沿；**不做队伍镜像**；`Effects/Layers/RemovedLayerIds` 一律 null | `Scripts/PMUnity/PMUnityMoverInput.cs:60-100`（红线）、`:300-320`（`Convert`）、`:332-345`（`DeriveYawDegrees`，注释「不做队伍镜像」） |
| 跳跃边沿有显式状态表（PollHardware 幂等、0ms 不消费、补子步不重复跳） | 同文件 `:20-52`（状态表）、`:196-260`（PollHardware/Consume） |

### 1.5 「双镜像」的确切位置

| 事实 | 证据 |
|---|---|
| 旧链**是**镜像的：`sign = (tid != selfTeam) ? -1 : 1`；`dirX = -mx * sign; dirZ = mz * sign` | `HYLDPlayerManger.cs:169-172`（GetTeamRelativeSign）、`Scripts/Shared/PMBattleSim.cs:114-115` |
| 共享数学注释确认：客户端把**本地玩家锚定在 X=+15**，`sign` 由调用方算 | `PMBattleSim.cs:36-45` |
| 新链**不**镜像：`PMUnityMoverInput` 显式「不做队伍镜像」；`PMR4MovementDriver`/`PMMoverSyncState` 无 `sign` 参数（DS 的 `BuildSpawnSync` 用 ±3 分侧表达队伍） | `PMUnityMoverInput.cs:340-343`；`PMDsSessionHost.cs:1030-1049` |

**结论：新链的世界坐标就是网络世界坐标（Y-up、原始轴）。绑定正式角色时**不能**把旧 `sign` 叠加到新链上，否则就是双镜像。**

### 1.6 动作边界（本轮必须写明的红线）

| 事实 | 证据 |
|---|---|
| 旧摇杆松手即入队攻击/大招：`AddCommad_Attack`/`AddCommad_SuperAttack` → `BattleData.EnqueueAttack` | `TouchLogic.cs:138,143`；`CommandManger.cs:75-83,110-127` |
| 攻击后续走旧链的 pendingAttacks / 预测子弹 / ClientAttack 上行（R5 投射物与伤害裁决**尚未实现**，主计划 R5 = PENDING） | `net-architecture-migration.md` §5 表（R5 PENDING）、文末 P4B6 段（「完整行为 Modifier 与技能激活裁决、R5 投射物…不在本次接线里冒充完成」） |

**结论：若为「移动接线」把 `TouchLogic`/`CommandManger` 一并接活，摇杆松手就会打开**未实现**的攻击/伤害/子弹路径。移动接线必须只接移动，攻击/大招输入需显式屏蔽或另走新入口。**

### 1.7 场景/组件落位（供接线定位）

| 事实 | 证据 |
|---|---|
| 战斗场景 = `SceneConfig.battleScene`，构建索引 2 = `Assets/Scenes/HYLDGame.unity`（`enabled: 1`；`HYLD1.0/Scenses/HYLDGame.unity` 为 `enabled: 0`） | `Client/Assets/HYLD2.0/Config/GameConfig.cs:19`；`Client/ProjectSettings/EditorBuildSettings.asset:8-15`；`ClearSenceManger.cs:106-107`（`LoadScene(SceneConfig.clearScene)` 后 `LoadSceneAsync(2)`） |
| 该场景里 Main Camera GameObject（`!u!1 &413438266`）同时挂 **Camera** + **HYLDStaticValue** | `Client/Assets/Scenes/HYLDGame.unity:212`（m_Name: Main Camera）、`:292`（guid `f425d8dc…`=HYLDStaticValue） |
| **HYLDCameraManger 不在该场景里**（`grep eec213bc Scenes/HYLDGame.unity` = 0 命中）；它是运行时由 `BattleManger.Init()` 用 `gameObject.AddComponent<HYLDCameraManger>()` 加到**同一个 Main Camera GameObject** 上的 | `Scripts/Server/Manger/Battle/BattleManger.cs:130-131`；`HYLDStaticValue.cs:198-205`（`gameObject.AddComponent<BattleManger>()`，gameObject 即 Main Camera） |
| `TouchLogic` 在该场景中挂在摇杆 UI 根（名为 `Android` 的 GameObject，`能量条`/`大招遥感` 已绑定） | `Scenes/HYLDGame.unity:5345`（guid `aed5e2a9…`）、`:5313`（m_Name: Android）；摇杆对象 `FireSuper/FireNormal/PlayerMove` 在 `:2898/:3280/:27208` |

---

## 2. 高概率推断（依据与置信度）

| # | 推断 | 依据 | 置信度 |
|---|---|---|---|
| P1 | 全部 20 个英雄共用同一 body 预制体，切换英雄不换外观 | `HYLDStaticValue.AddHero` 无模型参数；`Hero` 无模型字段；`HYLDPlayerManger.CreatePlayers` 对所有玩家 `Instantiate(PlayerClone)` 同一个 `PlayerClone` | 高 |
| P2 | 旧相机在新链里会**静默失效**：`LateUpdate` 有 `if (!initFinish) return;`，而 `initFinish` 等待 `HYLDStaticValue.playerSelfIDInServer != -1`，新链从不设置该静态量 | `HYLDCameraManger.cs:57-60`、`:34-45`；新链宿主 0 命中 `HYLDStaticValue` | 高 |
| P3 | `PMUnityMoverPresentation` 无法直接承载正式预制体（无注入点、硬编码 CreatePrimitive、Apply 只写胶囊子节点 localScale），需新增表现适配或改构造 | `PMUnityMoverPresentation.cs:76-95,112-140,183-200` | 高 |
| P4 | 直接 `Instantiate(Player.prefab)` 会带一个 **missing script**（根上第三个 MonoBehaviour）；且 `HYLDPlayerController`/`PlayerLogic` 会与表现层争抢 Transform 写入 | 1.1 缺 meta 证据；`HYLDPlayerController.cs:56-88`、`PlayerLogic.cs:139` | 中高（缺 meta 是硬证据；「争抢」需运行时确认哪一方先写） |
| P5 | 把正式角色搬进新链必须**重定出生位与碰撞环境**：新链目前只有 floor+wall 两个 BoxCollider，且明确「不声称是任何旧地图的摘要」；旧地图不可直接复用 | `PMR3TestScene`（`PMDsSessionHost.cs:45-57`）、`PMR3Runtime.CollisionDigest` 注释（`PMR3Runtime.cs:52-57`） | 高 |

---

## 3. 无法确定（缺少证据）

1. **Lobby 侧如何在 PMDS1 名册里填 `HeroId`**：本轮不研究 DS 编排，未查 Lobby 组装 `PMDsBootstrapPlayer/PMDsRosterIdentity` 的代码；因此「英雄选择如何从大厅传进新链」只确认了**管道存在**，未确认**取值来源**。
2. **`EasyJoystick` 属于哪个脚本**：未解析 `PlayerMove/FireNormal/FireSuper` 上的第三方组件 GUID，未确认其回调能否在不改 UI 的情况下复用为 `PMMoverInput` 来源。
3. **`Player.prefab` 的 Rigidbody 是否仍参与物理**（`isKinematic/useGravity/Constraints` 未逐字段读取）；这决定实例化后是否必须禁用。
4. **是否存在按英雄换模型的代码**：仅确认 `HYLDStaticValue`/`Hero`/`HYLDPlayerManger` 三个直接引用点无模型字段，未扫其余资产；「全英雄共用 body」是推断（P1）而非全项目证明。
5. **正式关卡的碰撞数据在新链的落点**：主计划要求 DS「必须加载权威逻辑需要的场景、障碍和碰撞数据」，但当前实现只有测试 floor+wall；旧地图如何进 DS 未查。
6. **`Player.prefab` 根上 missing script 的确切类名**：`Library/ArtifactDB` 里有该 GUID 记录，但本轮未反查（也不应把 Library 当权威）。

---

## 4. 已检查范围

**文档（6 份，按序完整阅读）**：`AGENTS.md`(hyld-master)、`Client/Assets/AGENTS.md`、`Server/AGENTS.md`、`Docs/plans/net-architecture-migration.md`（卷首 / §3.9.1–3.9.6 / §5 阶段表 / 文末 R3-R4B 全部节点）、`Docs/plans/net-r4-network-contract.md`、`Docs/plans/_r4b_host_integration_report.md`。

**源码（逐文件读取）**
- 旧角色/输入/相机：`HYLDPlayerManger.cs`、`HYLDPlayerController.cs`、`PlayerLogic.cs`、`TouchLogic.cs`、`CommandManger.cs`、`HYLDCameraManger.cs`、`HYLDStaticValue.cs`、`HeroData.cs`、`BattleFloatMath.cs`、`ResourceManger.cs`、`PlayerPrefabConstValue.cs`、`BattleManger.cs`（Init/Update/BattleTick/ApplyLocalPredictedInput 段）、`BattleData.cs`（SavedMove 字段段）、`BattleData.Prediction.cs`（Record/Build 段）、`ClearSenceManger.cs`、`UIMatchingPanel.cs`、`HYLDStartUILogic.cs`（英雄选择段）、`UIStartMainPanel.cs`/`UIInvatingFriendPanel.cs`（Hero 上行）、`GameConfig.cs`（battleScene=2）、`HYLDBaoShiZhengBaManger.cs`（Init 覆写）
- 新链：`PMClientSessionHost.cs`（全文）、`PMDsSessionHost.cs`（头部/常量/roster/Spawn/Attach/Pump）、`PMR3Player.cs`、`PMR3Runtime.cs`、`PMUnityMoverInput.cs`、`PMUnityMoverPresentation.cs`、`PMR4MovementDriver.cs`（头部契约段）、`PMDsHost.cs`（结构）、`Scripts/Shared/PMBattleSim.cs`（sign 语义）、`Scripts/Shared/BattleNumericConfig.cs`（PMHeroId 段）、`Scripts/PMNet/Control/PMDsControlProtocol.cs`（PMDsRosterIdentity）、`Scripts/PMNet/Session/PMDsEntryOffer.cs`、`Scripts/PMNet/Control/PMDsTicket.cs`（Identity 段）、`Scripts/PMNet/Session/PMHandshake.cs`/`PMTransportConnection.cs`/`PMUdpSessionEndpoint.cs`（HeroId 传递点）
- 资产（只读必要元数据）：`Resources/Remake/Player.prefab`（组件清单与 GUID）、`HYLD1.0/Resources/Prefabs/MainGameController.prefab`、`HYLD1.0/Resources/Main Camera.prefab`、`HYLD1.0/Resources/Prefabs/HYLDGameTatal.prefab`、`Client/Assets/Scenes/HYLDGame.unity`（Main Camera/TouchLogic/摇杆对象定位）、`Client/ProjectSettings/EditorBuildSettings.asset`（场景索引）、`.meta` GUID 反查

**未做（符合硬边界）**：未扫全项目资产、未做 R5 攻击迁移、未研究 DS 编排实现、未改任何源码/场景/主计划、未运行 Unity、未执行 svn/git 写操作、未递归委派。

---

## 5. 建议下一步（最小补充查询 / 运行时验证）

1. **补一条窄查询**（把 §3.1 缺口补上）：查 Lobby 侧组装 `PMDsRosterIdentity.HeroId` 的那一处赋值（预期在 `Server/` 的大厅匹配→入场快照路径），确认英雄选择从 UI 到新链名册的取值来源；这是「正式角色绑定」唯一缺失的上游证据。
2. **定接口再动手**：给「角色表现」增加预制体注入点（见 §6 接口 S1），而不是改 `PMUnityMoverPresentation` 的语义——后者已被 B2 契约与门禁覆盖，改它要同步 `PMR4UnityCheck`/`PMR4IntegrationTest`。
3. **运行时验证（Play 模式，需用户点菜单/进局）**：把 AP 表现从胶囊换成 `Resources/Remake/Player.prefab` 实例，只保留**一个** Transform 写者（禁用/剥离 `HYLDPlayerController` 与 `PlayerLogic` 的 Transform 写入），确认角色仍随预测移动、DS 不创建任何表现对象。
4. **相机数据源切换**：让相机读新链状态（`Driver.GetPredictedSync()` / SP 的 `SamplePresentation()` 根 Transform），否则本地看不到角色（P2）。
5. **输入来源切换后必须回归攻击屏蔽**：确认摇杆松手不再触达 `BattleData.EnqueueAttack`。

---

## 6. 实现拆分与共享接口建议（供后续 comprehensive 阶段使用）

### 6.1 建议的 4 个可并行子任务

| # | 子任务 | 边界（只动什么） | 验收要点 |
|---|---|---|---|
| T-A | **角色表现适配**：新增 `PMUnityMoverPrefabPresentation`（或给现有表现层加「预制体工厂」注入），承载正式预制体的 Renderer/Animator，只由 SyncState 写**根** Transform + 朝向 | 新增 `Scripts/PMUnity/**`；宿主 `PMClientSessionHost.EnsureMovementRig` 选择建哪种表现 | 同一角色只有一个 Transform 写者；DS 仍抛/不建；Dispose 幂等；旧 prefab 的 missing script 不刷错误（用干净 prefab 或显式过滤） |
| T-B | **输入来源适配**：`IMoverInputSource`（`Poll/Sample/Consume/Reset`）抽象，新增「旧摇杆源」（读 `HYLDStaticValue.PlayerMoveX/Y` 或订阅 `EasyJoystick` 事件）与既有 WASD 源；**只产出移动轴 + 跳跃边沿，不含攻击** | 新增 `Scripts/PMUnity/**`；宿主改 `session.Input` 的类型 | 摇杆 → `PMMoverInput.MoveX/MoveZ`（保持原始轴、不镜像、不归一化）；边沿语义与 `PMUnityMoverInput` 状态表一致；攻击/大招入口显式不可达（可加断言/日志） |
| T-C | **英雄/名册消费**：从 `PMDsEntryOffer.Identity.HeroId`（客户端）与连接身份 HeroId（DS）取 HeroId，客户端选择角色预制体/表现变体，DS 只用 `PMHeroId`+`BattleNumericConfig` 取数值 | 宿主 + 一处共享取值函数；不新增第二份英雄表 | 两端同一 HeroId → 同一数值；DS 不建表现；HeroId 越界显式失败 |
| T-D | **出生位与碰撞环境**：把名册→出生位从「±3 + 行居中」升级为显式名册给点（x/y/z/yaw），并明确正式关卡的碰撞白名单来源 | `PMDsSessionHost.BuildSpawnSync` + 场景装配；不改 `PMR4MovementDriver` | 出生位在墙/障碍外（沿用既有「原点在墙内」教训）；`MovementWorldVersion` 两端同值；allowlist 仍是显式白名单，不用 layer 掩码当隔离 |

### 6.2 建议共享接口（最小面，避免各造第二套）

```csharp
// S1 角色表现：由宿主注入「怎么建一个角色的可见体」，运动数据仍只从 SyncState 来。
public interface IPMMoverPresentation : IDisposable {
    string Label { get; }
    PMNetRole Role { get; }
    void ApplyPredicted(PMMoverSyncState state);     // AP
    void ApplyInterpolated(PMMoverSyncState state);  // SP
}

// S2 输入来源：只产出「玩家能直接决定的值」，与 PMUnityMoverInput 的状态表同语义。
public interface IMoverInputSource {
    void Poll();                       // 每宿主帧/每子步幂等
    PMMoverInput Sample(int stepMs);   // 不消费边沿
    void Consume(int stepMs);          // 仅 1..50 真实步消费
    void Reset();
}

// S3 名册身份（已有，勿另造）：HeroId 的唯一上游
//   PMDsRosterIdentity{Uid,PlayerId,TeamId,HeroId}  （Control 层）
//   PMConnectionIdentity.HeroId / PMTransportConnection.HeroId（Session 层）
//   下游唯一数值表：PMNet.Shared.PMHeroId + BattleNumericConfig
```

**共享接口纪律（与 §3.9.4 一致）**：S1/S2 只为「表现与输入」各开一条缝，**不得**引入第二套时钟、第二套身份或第二套同步状态；运动真值仍只有 `PMR4MovementDriver` + `PMNet` 复制/RPC。攻击与伤害（R5）不在本组接口里预留任何旁路。
