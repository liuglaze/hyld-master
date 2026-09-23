# 旧网络脚本 / Unity 资产边界与删除风险调查

范围：`D:/UGit/hyld-master`（Unity 2019.4.8f1，`Client/Assets`）。目标类共 **7 个 / 12 个脚本文件**（`BattleData` 为 6 文件 partial）。
必读已读：根 `AGENTS.md` → `Client/Assets/AGENTS.md` → `Docs/plans/net-r4c-content-contract.md` → `Docs/plans/net-r6-combat-contract.md`。
调查对象（硬边界）：`BattleManger`、`BattleData`(6 个 partial 文件)、`HYLDPlayerManger`、`HYLDBulletManger`、`UDPSocketManger`、`CommandManger`、`BattleFrameHud`、旧 `Request/Panel`（`Scripts/Server/Request/BaseRequest.cs`、`Scripts/Server/Manger/RequestManger.cs`、`Scripts/Server/Panel/*.cs`）。
只读，未改任何资产，未运行 Unity，未提交。不调查无关场景 / 第三方（DouDiZhu、XLua、LZJ 等仅作为噪声排除项记录）。

---

## 1. 已确认

### 1.1 目标类 GUID 与其 .unity/.prefab 反向引用（唯一权威：对 `Client/Assets/**` 全文件类型实时 `rg -F <guid>`，排除 Library/Temp/obj）

| class | 脚本文件 | .cs.meta GUID | .unity/.prefab 反向引用 |
|---|---|---|---|
| `BattleManger` | `Scripts/Server/Manger/Battle/BattleManger.cs` | `7200a0eb9673b6e4f8cb8386cdde31db` | **`Assets/Scenes/HYLDGameTest.unity`：1 处**（`--- !u!114 &413438274`，`m_Script: {fileID: 11500000, guid: 7200a0eb..., type: 3}`，`m_Enabled: 1`，宿主 GameObject `fileID: 413438266`） |
| `BattleData`(主) | `.../BattleData.cs` | `21712db839cb13347881c6f1a9e56f73` | 无 |
| `BattleData`(partial) | `.../BattleData.Authority.cs` | `d31256a20867dde479f6428e010f24c7` | 无 |
| `BattleData`(partial) | `.../BattleData.Prediction.cs` | `82224bf11c60cef4bb5fd7ce677cf371` | 无 |
| `BattleData`(partial) | `.../BattleData.HitEvent.cs` | `fa1ccfe56e820c8458727be90788f765` | 无 |
| `BattleData`(partial) | `.../BattleData.Attack.cs` | `bd0479c8b0b623b449a06e2f8ea050e4` | 无 |
| `BattleData`(partial) | `.../BattleData.Rtt.cs` | `6aecda5c5238f07429fcd88a4fa5e717` | 无 |
| `HYLDPlayerManger` | `.../HYLDPlayerManger.cs` | `32b0d199e908c79498c9506a6e6fda1f` | 无 |
| `HYLDBulletManger` | `.../HYLDBulletManger.cs` | `e4868f000b5efd2409d48f15ababa8eb` | 无 |
| `UDPSocketManger` | `Scripts/Server/Manger/UDPSocketManger.cs` | `78c8df29e5cb9d94ca9bd705e42277e9` | 无 |
| `CommandManger` | `Scripts/Manger/CommandManger.cs` | `656f81efc04e6ce49801d1785e936ad9` | 无（且该类**不是 MonoBehaviour**，`public class CommandManger`，无 `AddComponent` 可能） |
| `BattleFrameHud` | `Scripts/Server/UI/BattleFrameHud.cs` | `6c74db1a9f7f4b84a8ec4a9b6f1e2d31` | 无（`public sealed class BattleFrameHud : MonoBehaviour`，只被运行期 `AddComponent`） |

**结论（硬事实）**：12 个目标 GUID 中**只有 `BattleManger` 被资产引用，且只被 `HYLDGameTest.unity` 引用 1 次**。其余 11 个在全部 `.unity`/`.prefab`（以及全文件类型）中**零反向引用**。

### 1.2 旧 Request/Panel 的资产挂载（同样全量实时扫描）

| class | .cs.meta GUID | 挂载资产（`m_Script` 命中数，均为 1） |
|---|---|---|
| `UIAddFrindPanel` | `f1e0a4e536ae8b3488a235e3bc6540b1` | `Scenes/HYLDStart.unity` |
| `UIAplyInvateFriendPanel` | `2ae443bd58425ac41944f91527fb4283` | `Scenes/HYLDStart.unity` |
| `UIFriendPanel` | `01aba89264b23e14e8597ea46381d90e` | `Scenes/HYLDStart.unity` |
| `UIInvatingFriendPanel` | `51c4a070a5883fe4cbf56f4301e4ca97` | `Scenes/HYLDStart.unity` |
| `UILoginPanel` | `daf4c0689053f594c9c18ee392afc3ab` | `Scenes/HYLDLogon.unity`、`Scenes/Canvas.prefab`、`HYLD2.0/Prefabs/Canvas.prefab` |
| `UILogonPanel` | `31873ad782a9a9940b15bd0cee15173f` | `Scenes/HYLDLogon.unity`、`Scenes/Canvas.prefab`、`HYLD2.0/Prefabs/Canvas.prefab` |
| `UIMatchingPanel` | `620f6a0fe50dbf44ca05cd40db0ddd35` | `Scenes/HYLDStart.unity` |
| `UIMessagePanel` | `061ffb0ada2e2b04e9d481cfdde9f27b` | `Scenes/HYLDAsyncScence.unity`、`HYLD2.0/Prefabs/MessagePanel.prefab` |
| `UISettingPanel` | `05000b972239fe44e8199e96a75303d7` | `Scenes/HYLDStart.unity` |
| `UIShopPanel` | `22cca1b9910174e4bbc38373b7d31b96` | `Scenes/HYLDStart.unity` |
| `UISliderPanel` | `12ce5b5195389c74caa57eaa1c9c8f7f` | `Scenes/HYLDAsyncScence.unity` |
| `UIStartMainPanel` | `f3ce97cefabdad24c8c954235e7477a4` | `Scenes/HYLDStart.unity`、`HYLD2.0/Prefabs/UIStartMainPanel.prefab` |
| `UIStartPanel` | `8c1d82d7e558e304b9c044149da21cb6` | `Scenes/HYLDLogon.unity`、`Scenes/Canvas.prefab`、`HYLD2.0/Prefabs/Canvas.prefab` |
| `UIbasePanel` | `ae642ce7842dc3a4896d0cd88119fb8f` | 无（抽象/基类，非挂载组件） |
| `BaseRequest` | `c7a278484c8e4124397f370bbd7a0c46` | 无（普通类） |
| `RequestManger` | `5d7c2d803d0e0e541863e8b3050b614d` | 无 |

`Scenes/HYLDLogon.unity`、`Scenes/HYLDStart.unity`、`Scenes/HYLDGame.unity`、`Scenes/HYLDAsyncScence.unity` 在 `ProjectSettings/EditorBuildSettings.asset` 中 `enabled: 1`；`HYLDGameTest.unity` **不在** BuildSettings（`grep` 全仓仅 `Docs/plans/_r4c_scene_survey.md` 提到它，零代码/资产引用）。

### 1.3 已烘焙 `Resources/PMNet` 三产物不引用旧脚本（已确认）

| 产物 | .meta GUID | `MonoBehaviour:` 块 | `m_Script: {fileID: 0}` | 全部依赖 GUID 解析结果 |
|---|---|---|---|---|
| `Resources/PMNet/BattleMapV1.prefab` | `d0460de9f6cde0646817558d59666f96` | **0** | 0 | 33 个去重 GUID，**全部**指向 `HYLD1.0/HYLDResource/Fantastic Halloween Pack/` 下的 `.fbx`(22) 与 `.mat`(8) + 3 个引擎内置 GUID；**无一命中工程 `*.cs.meta`** |
| `Resources/PMNet/PlayerVisualV1.prefab` | `b80ce15df403b9348ad8af507689563b` | **0** | 0 | 10 个去重 GUID → `HYLD2.0/30kAnimatedCharacters/NPC_Models.FBX`、`NPC_Mecanims.FBX`、4 个 NPC 材质、`HYLD1.0/Shaders/body.mat`、`HYLD2.0/Animator/Player.controller` + 2 个引擎内置；**无一命中工程 `*.cs.meta`** |
| `Resources/PMNet/BattleContentV1.json` | `99dacf967d2e1584cb4e2bb05865d363` | —（TextAsset） | — | 内容 `{"formatVersion":1,"mapId":"hyld-map2-v1","seed":1380205313,"contentDigest":"694c469d...97ea","collisionDigest":2638629993,"worldVersion":491146345}` |

交叉验证方法：把 `Client/Assets/**/*.cs.meta` 的 362 个 GUID 建成集合，与两 prefab 的依赖 GUID 集合求交 → 空集。
`PlayerVisualV1.prefab` 间接依赖的 `HYLD2.0/Animator/Player.controller` 内 `m_Script` 出现 **0** 次（20 个 `m_StateMachineBehaviours` 全为空列表，唯一 GUID 是 `NPC_Mecanims.FBX`）→ 无 StateMachineBehaviour 旧脚本引用。

### 1.4 原始烘焙输入的 missing script 基线（不得变差）

- `Assets/Scenes/HYLDGame.unity`（`SourceScenePath`）：`m_Script` 引用 **89** 处，`m_Script: {fileID: 0}` **0** 处；工程脚本全部可解析（`HYLDStaticValue`、`TouchLogic`、`Toolbox`、`HYLDGameOver`、`ScenseBuildLogic`、`UITextEffect`、`HYLDHeropropertyUI`、`ScaleSelf`、`HYLDModenProp`、`GameUITeamGemLogic`、`CreatGemLogic`、EasyTouch 系）。其余不可解析 GUID 经 `BuiltInPackages/com.unity.ugui` 逐一核对，全部为 UGUI 内置组件（`Image`/`Text`/`Button`/`Slider`/`GraphicRaycaster`/`CanvasScaler`/`EventSystem`/`StandaloneInputModule` 等）。
  **基线 = 0 个 missing script。**
- `Assets/Resources/Remake/Player.prefab`（`SourcePlayerPath`）：`m_Script` 引用 **27** 处，`m_Script: {fileID: 0}` 0 处；工程脚本 3 个：`PlayerLogic`(`faeae29bd68cfa149a45dad97644ce15`)、`HYLDPlayerController`(`6e67998eff9636b46b49b3012fc6d288`)、`移动型大招`(`e831e0f6f524211499a867cffc79d212`)。
  **但存在 1 处既有 missing script：GUID `66e3984fb39329042ac11aabad97b6d8`**（`--- !u!114 &6562473155793414408`，序列化残留字段 `database / selfTransform / isRun`）。该 GUID 在 `Client/**` 全树、`Library/PackageCache`、`BuiltInPackages` 中均无 `.meta` 定义 → 脚本已被删除；同样出现在 `HYLD2.0/Prefabs/Player.prefab`、`HYLD1.0/Resources/Prefabs/Player.prefab`。
  **基线 = 1 个既有 missing script。**（C1 `BuildPlayerHierarchy` 会先 `GameObjectUtility.RemoveMonoBehavioursWithMissingScript` 再按类型清理，并输出「删除 missing script=N 个」，因此该既有空洞不阻塞烘焙。）
- `Assets/Scenes/HYLDGameTest.unity`：`m_Script` 引用 91 处，`m_Script: {fileID: 0}` 0 处 → **当前无 missing script**，删 `BattleManger` 会立刻制造 1 个。

### 1.5 代码级真实依赖（非注释；决定"能不能直接删"）

| 目标类 | 真实引用点（编译期硬依赖） |
|---|---|
| `BattleManger` | `HYLD1.0/Scripts/OldScripts/HYLDStaticValue.cs:216,226`（`AddComponent<HYLDBaoShiZhengBaManger>()` / `AddComponent<BattleManger>()` + 写 `ISNet/_StartGameAni/BG/ScenseBuildLogic/toolbox` + 调 `Init()`）；`HYLD1.0/Scripts/OldScripts/Toolbox.cs:110,111,211,263`（`Instance.IsGameOver` / `Instance.BeginGameOver()`）；`Scripts/Server/Manger/HYLDManger.cs:227,228`；`Scripts/Server/Manger/Battle/HYLDBaoShiZhengBaManger.cs:18`（**子类** `:BattleManger`，`override Init()`+`base.Init()`、`protected override OnBattleLogicTick`）；`Scripts/Server/Manger/Battle/BattleData.Authority.cs:630,635`（`Instance.bulletManger.SpawnVisualBullet`）；`Scripts/Server/UI/BattleFrameHud.cs`（读 `CurrentPredictedFrame/CurrentTargetFrame/CurrentSyncFrame/HasDynamicTarget/IsRttReady/SmoothedRttMs/RttVarianceMs/AuthorityAgeMs/PredictionHistoryCount/SendBattleNetSimConfig`） |
| `BattleData` | `Scripts/Manger/CommandManger.cs:78,113,114,127`（`Instance.EnqueueAttack/selfOperation/FlushPendingAttacksToOperation`）；`Scripts/Server/Panel/UIMatchingPanel.cs:65`（`Instance.InitBattleInfo(pack.BattleInfo.RandSeed, pack.BattleInfo.BattleUsers)`）；**`BattleData.cs:15` 的 `public const float LocalPositionJumpTraceThreshold = 0.8f` 被烘焙输入脚本使用**：`PlayerLogic.cs:235`、`HYLDPlayerController.cs:57`、`移动型大招.cs:74` |
| `HYLDPlayerManger` | `BattleManger.cs:130`（`AddComponent<HYLDPlayerManger>()`）、`BattleData.Prediction.cs`、`BattleData.Authority.cs` |
| `HYLDBulletManger` | `BattleManger.cs:132`（`AddComponent`）、`BattleData.Authority.cs:635`（`SpawnVisualBullet`）；其余均为注释 |
| `UDPSocketManger` | `BattleManger.cs:114,147,148,174,184,334,344,539,750`（`Instance.Send/InitSocket/Handle/DrainAndDispatch/SendOperation`）；**新链也依赖**：`Scripts/Server/Boot/PMClientSessionHost.cs:893` `global::Server.UDPSocketManger.CloseExisting()`（`UDPSocketManger.cs:140` 定义）；`ClearSenceManger.cs:150` 已注释 |
| `CommandManger` | `HYLD1.0/Scripts/OldScripts/TouchLogic.cs:90,138,143,267,293`（`Instance.AddCommad_Move/SuperAttack/Attack`，TouchLogic 是 `HYLDGame.unity` 的组件）；`BattleData.Attack.cs`、`BattleManger.cs` |
| `BattleFrameHud` | `BattleManger.cs:138`（`AddComponent<BattleFrameHud>()`）—— 仅此一处 |

**新 PMNet/PMR 代码中的 `BattleManger`/`BattleData`/`HYLDBulletManger`/`CommandManger` 出现全部是注释**（`PMBattleSim.cs:42,233`、`PMCombatWeaponPlanner.cs:15`、`PMUnityMoverInput.cs:7,55`、`PMUnityCombatHud.cs:13`、`BattleFloatMath.cs:27`、`PMDsSessionHost.cs:7,2717`、`PMClientSessionHost.cs:29,2975`），**唯一例外是 `PMClientSessionHost.cs:893` 的 `UDPSocketManger.CloseExisting()` 实调用**。

### 1.6 `SocketProto.proto` 战斗专用 message / enum 的客户端消费接口图

权威 proto 源：`ProtobufAndNotepad/Protobuf/SocketProto.proto`（352 行）；客户端生成产物 `Client/Assets/Scripts/Server/SocketProto.cs`。

**战斗专用 message**

| message | 消费者（`\b名\b` 词边界，排除生成文件） |
|---|---|
| `BattleInfo` | `Scripts/Server/Panel/UIMatchingPanel.cs:65`；`BattleData.Authority.cs`；`BattleManger.cs`；`UDPSocketManger.cs`；`Scripts/Server/Net/PMUdpRouter.cs` |
| `BattleNetSimConfig` | `BattleManger.cs`；`PMUdpRouter.cs` |
| `BattleClientInput` | `UDPSocketManger.cs` |
| `BattleServerUpdate` | `BattleData.Authority.cs` |
| `BattleFrame` | `BattleData.Authority.cs` / `.HitEvent.cs` / `.Prediction.cs` |
| `PlayerFrameInput` | `BattleData.Attack.cs` / `.Authority.cs` / `.Prediction.cs` / `.cs` |
| `ClientAttack` | `BattleData.Attack.cs` / `.cs`；`HYLDPlayerManger.cs`；`UDPSocketManger.cs` |
| `ServerAttack` | **无显式类型名消费者**（仅生成文件）；实际由 `BattleData.Authority.cs` 以 `var` 遍历 `frame.player_inputs[i].attacks` 隐式消费（`spawn_pos_x/y/z` 亦在该路径读取） |
| `ClientMove` | `BattleData.Authority.cs` / `.Prediction.cs`；`BattleManger.cs`；`UDPSocketManger.cs` |
| `MoveAckResult` | `BattleData.Authority.cs` |
| `HitEvent` | OldScripts：`Bullet/s/Bullet/shell.cs`、`HYLDStaticValue.cs`、`PlayerLogic.cs`；Scripts/Server：`BattleData.Authority.cs` / `.HitEvent.cs`；`BattleManger.cs` |
| `AttackAck` | `BattleData.Attack.cs` |
| `AuthoritativePlayerState` | `BattleData.Authority.cs` / `.HitEvent.cs` |
| `BattlePlayerPack` | `BattleData.cs`；`BattleManger.cs`；`HYLDPlayerManger.cs` |
| `BattleRoomPack` | 仅生成/PMNet 生成文件（**客户端零消费者**） |
| `PlayerPack` | OldScripts `HYLDStaticValue.cs`；UI `UIAddFrindPanel.cs`、`UIInvatingFriendPanel.cs`、`UIStartMainPanel.cs` |
| `ChatPack` | UI `UIFriendPanel.cs` |
| `LoginPack` | UI `UIInvatingFriendPanel.cs`、`UILoginPanel.cs`、`UILogonPanel.cs`、`UISliderPanel.cs`、`UIStartMainPanel.cs`、`UIStartPanel.cs` |
| `RoomPack` / `FriendRoomPack` | `FriendRoomPack`：OldScripts `HYLDStaticValue.cs`、UI `UIInvatingFriendPanel.cs`；`RoomPack` 仅生成文件 |

**战斗专用 enum / 枚举值**

| enum | 消费者 |
|---|---|
| `AttackType` | `BattleData.Attack.cs` / `.Authority.cs` / `.cs`；`BattleManger.cs`；`HYLDPlayerManger.cs`；`Scripts/Manger/CommandManger.cs` |
| `MoveType` | `BattleData.Prediction.cs` |
| `FightPattern` | UI `UIInvatingFriendPanel.cs` |
| `Hero` | OldScripts `BulletLogic.cs` / `HYLDStaticValue.cs` / `HeroData.cs`；UI `UIInvatingFriendPanel.cs` / `UIStartMainPanel.cs` / `InvateItem.cs`；Scripts/Server `BattleData.Authority.cs` / `HYLDBulletManger.cs` / `HYLDPlayerManger.cs` / `PMClientSessionHost.cs`；新链 `Scripts/Shared/BattleNumericConfig.cs` |
| `PlayerState` | UI `UIInvatingFriendPanel.cs` / `UIStartMainPanel.cs` / `InvateItem.cs`（第三方 `LZJ/ZYKTool` 同名枚举，非本 proto） |
| `ActionCode`（战斗值） | `BattleReady` / `BattleStart` / `BattlePushDowmAllFrameOpeartions` / `BattlePushDowmPlayerOpeartions` / `BattlePushDowmGameOver` / `BattlePushDownHitEvents` / `BattleSetNetSimConfig` / `ClientSendGameOver` / `ClientSendClearSenceReady` → **仅 `Scripts/Server`（BattleManger / UDPSocketManger / HYLDManger / PMUdpRouter）+ 生成文件**，UI/OldScripts **零消费**；例外两项：`StartEnterBattle` 被 `UIMatchingPanel.cs:28` 消费、`AllClearSenceReady` 被 `UISliderPanel.cs:55` 消费、`BattleReview` 被 `UIStartMainPanel.cs:67,79` + `RequestManger.cs:55` 消费；`Ping`/`Pong` 仅网络层 |
| `RequestCode` | 10 个 UI/Request + `BattleManger.cs` / `PingPongManger.cs` / `RequestManger.cs` / `UDPSocketManger.cs`（`RequestCode.Battle` 仅战斗网络层） |
| `ReturnCode` | 7 个 UI/Request 面板 |
| `RoomState` | 仅生成文件（**零消费者**，含 UI 与网络层） |

---

## 2. 高概率推断（依据与置信度）

1. **"可直接删、不丢资产引用"的目标 = 11 个脚本文件**（共 7 个类：`BattleData` 占 6 个 partial 文件，另加 `HYLDPlayerManger`、`HYLDBulletManger`、`UDPSocketManger`、`CommandManger`、`BattleFrameHud`）。
   依据：1.1 全量实时 GUID 扫描零命中。置信度 **高（≈0.97）**——残留不确定性来自"GUID 引用可能存在于被 gitignore 的 `Library/` 或 `.unitypackage` 导入缓存"，但按任务约定 `Library/` 属排除区，且这些类若是资产组件，其 GUID 必然出现在工程 `.unity/.prefab` 文本里。
2. **`BattleManger` 是唯一被资产挂载的目标类**，`HYLDGameTest.unity` 是唯一挂载点，且该场景不在 BuildSettings、全仓零引用 → 属"编辑器临时/历史测试场景"。置信度 **高（≈0.9）**，与既有 `Docs/plans/_r4c_scene_survey.md` §49/§157（置信 0.8）一致。
   注意：该组件的 YAML 里 `playerManger: {fileID: 0}`、`cameraManger: {fileID: 0}` 为 null，另有当前类里**已不存在**的陈旧字段 `id`/`opt` → 是历史版本序列化残留；这意味着**即使保留壳，字段集也不必与历史完全一致**，但保留原字段名最安全。
3. **真正的删除阻塞点不是资产 GUID，而是编译链**：删掉目标类 → `PlayerLogic.cs` / `HYLDPlayerController.cs` / `移动型大招.cs`（`Remake/Player.prefab` 的 3 个组件）与 `HYLDStaticValue.cs` / `TouchLogic.cs` / `Toolbox.cs`（`HYLDGame.unity` 的组件）编译失败 → 这两个烘焙输入立刻新增 missing script（Player.prefab +3、HYLDGame.unity +6 级别）。置信度 **高（≈0.95）**（依赖点逐条 grep 到实调用）。
4. **`UDPSocketManger` 不能被"纯删"**：新链 `PMClientSessionHost.cs:893` 仍在调 `CloseExisting()`，删类会破坏新链编译。置信度 **高（≈0.98）**（`CloseExisting` 全仓仅此一处调用 + 定义处）。
5. **`BattleData` 虽然无资产引用，但 `LocalPositionJumpTraceThreshold` 这个 `const` 被烘焙输入脚本使用** → 即使把 `BattleData` 剥成壳，也必须保留该常量（或改这三处引用）。置信度 **高（≈0.95）**。
6. **`PlayerVisualV1.prefab` 依赖的 `Player.controller` 无脚本引用**（20 个空 StateMachineBehaviour 列表）→ 烘焙产物确实"零旧脚本"，与 R4-C 契约的"生成后回读检查脚本数 0"一致。置信度 **高（≈0.95）**。
7. `Resources/PMNet` 三产物是**可重建产物**且当前**未纳入**旧脚本 → 删旧脚本不会让三产物失效；反之，**只有把 `HYLDGame.unity` / `ScenseBuildLogic.cs` / `Remake/Player.prefab` 三个烘焙输入改坏才会**。置信度 **中高（≈0.85）**（依据 `PMBattleContentBuild.cs:288,347,350,353` 的源指纹声明；未实测重跑烘焙）。
8. **`HYLDGameTest.unity` 里的 `BattleManger` 组件在运行时并不启动战斗**（依赖 `ScenseBuildLogic`/UID 等字段，且该场景无引用）→ 即使保留空壳也不会产生网络行为。置信度 **中（≈0.7）**（未在编辑器验证该场景 Awake 链）。

---

## 3. 无法确定（缺少证据）

1. **`HYLDGameTest.unity` 的实际用途与"能否直接删掉该组件"**：无文档、无 `.cs` 引用、不在 BuildSettings；`_r4c_scene_survey.md` 也只能给 0.8 置信的猜测。未在 Unity 中打开验证谁依赖它。
2. **`66e3984f...`（`Remake/Player.prefab` 的既有 missing script）是什么脚本**：全树无 `.meta`、无源码残留；仅能从序列化字段名 `database / selfTransform / isRun` 猜测与"数据库/自身 Transform/运行开关"有关。是否属于旧网络链**无法判定**，因此它是否算"应当清理的旧网络残留"不能下结论。
3. **`Resources/PMNet` 三产物与当前源资产的 digest 是否仍然一致**：本次只校验了"无脚本引用"与 manifest 内部字段，未重算 `contentDigest`/源文件 SHA256（需要跑 Unity 编辑器菜单，被"只读不运行 Unity"边界禁止）。
4. **`Library/` 内的导入缓存是否残留旧类引用**：按任务约定排除，且 `Library/` 为可再生目录，未扫。
5. **旧 Request/Panel 里哪些是"纯网络遗留"**：只做到"资产挂载位置 + proto 消费关系"这一步（任务要求的接口图）；各面板的业务页面语义（是否仍是正式大厅 UI）未逐个深挖。
6. **`ServerAttack` 是否真的无显式消费者**：已确认无显式类型名，但 `var` 隐式消费路径无法用文本搜索完全枚举；只能给出"通过 `frame.player_inputs[].attacks` 隐式消费"这一条已读到的证据。

---

## 4. 已检查范围

**读过的项目文档（按顺序）**：`AGENTS.md`（根，312B，仅做路由）；`Client/Assets/AGENTS.md`（52KB，读至 §15 结束）；`Docs/plans/net-r4c-content-contract.md`（9.4KB，全读）；`Docs/plans/net-r6-combat-contract.md`（12KB，全读）。文档如何决定入口：`Client/Assets/AGENTS.md` §5「关键文件索引（函数级）」直接给出 12 个目标类的**权威相对路径与成员清单**，§12 给出 proto 唯一权威源路径，§3/§4 给出战斗链调用关系；`net-r4c-content-contract.md` 给出烘焙输入三路径与产物三路径（`Resources/PMNet/BattleMapV1.prefab`/`PlayerVisualV1.prefab`/`BattleContentV1.json`）以及"生成资产零脚本"的验收口径；`net-r6-combat-contract.md` 确认新链不消费 `CommandManger/BattleData/旧 shell`（据此把新链中的同类名出现判定为注释）。**未脱离这三份文档盲搜。**

**文件级检查**：
- 12 个目标 `.cs` + `.cs.meta`（GUID 提取），16 个 Request/Panel `.cs.meta`（含 `BaseRequest`、`RequestManger`）。
- GUID 全量实时扫描：对 `Client/Assets/**`（排除 `Library/`、`Temp/`）逐 GUID `rg -F`，覆盖**所有文件类型**（不限于 `.unity`/`.prefab`）；另对 `.unity`/`.prefab` 做了 `m_Script + guid` 精确计数。
- `Client/Assets/**/*.cs.meta` 共 **362** 个 GUID 建集合；`Client/Assets/**/*.meta` 共 **3158** 个 GUID 建映射（用于把资产依赖解析到真实文件路径）。
- 逐 GUID 解析的资产：`Scenes/HYLDGame.unity`、`Scenes/HYLDGameTest.unity`、`Scenes/HYLDStart.unity`、`Scenes/HYLDLogon.unity`、`Scenes/HYLDAsyncScence.unity`、`Scenes/Canvas.prefab`、`Resources/Remake/Player.prefab`、`Resources/PMNet/BattleMapV1.prefab`、`Resources/PMNet/PlayerVisualV1.prefab`、`HYLD2.0/Prefabs/{Canvas,MessagePanel,UIStartMainPanel,Player}.prefab`、`HYLD1.0/Resources/Prefabs/Player.prefab`、`HYLD2.0/Animator/Player.controller`。
- UGUI 内置 GUID 判定：对 `C:/Program Files/Unity/Hub/Editor/6000.6.1f1/.../BuiltInPackages/com.unity.ugui/**/*.meta` 核对 `Image/Text/Button/Slider/Mask/GridLayoutGroup/ScrollRect/InputField/GraphicRaycaster/CanvasScaler/EventSystem/StandaloneInputModule`；项目目标编辑器为 `2019.4.8f1`（`ProjectSettings/ProjectVersion.txt`）。
- 代码依赖：逐目标类 `rg -l --pcre2 '\bClass\b'` 全量列文件，再对命中文件逐条看调用行（区分实调用 vs 注释），重点覆盖烘焙输入脚本与 `Scripts/Server/Boot/*`。
- proto：读 `ProtobufAndNotepad/Protobuf/SocketProto.proto` 全文（352 行），对 30 个 message/enum 符号做词边界消费方分桶（OldScripts / UI-Request / legacy-net / 新链 / 生成文件 / 第三方）。

**未检查（明确排除项）**：`Client/Library`、`Temp`、`obj`、`bin`、`HyldDS/`、`Client/Assets/{XLua,LZJ,DouDiZhu,AssetStoreTools,Plugins/Editor/JetBrains*}` 等第三方/生成目录的**内容**（仅作为噪声出现在分桶统计里）；`Server/`、`HyldDS/` 服务端实现；`Assets/HYLD1.0/He|other` 场景样例；`Assets/Scenes/HYLDReGame.unity`、`PMDsBoot.unity`、`OBJSence.unity`（与目标类无 GUID 交集）；未运行 Unity、未跑烘焙、未做运行时验证。

---

## 5. 建议下一步（最小补充查询 / 运行时验证）

1. **人工决策点（唯一开放问题）**：`Assets/Scenes/HYLDGameTest.unity` 是否仍有价值。若有 → 走"保留 `BattleManger` 薄壳"路线；若无 → 可在同一次提交里删掉该场景，从而**完全消除资产级 GUID 阻塞**（需用户确认，属场景删除，超出本调查边界）。
2. **有界补充查询**：在 Unity 编辑器里打开 `HYLDGameTest.unity` 看 Console 是否已有报错、该 `BattleManger` 组件的 Inspector 是否已显示 `id`/`opt` 为"未知字段丢弃"（验证 §2.2 的陈旧字段推断），一次操作即可闭环。
3. **编译级回归（改代码前必须）**：按下面的最小拆耦清单改完后，用一次真实编译 + 在编辑器里打开 `HYLDGame.unity` 与 `Remake/Player.prefab` 核对 missing script 数（期望：`HYLDGame.unity` 仍为 0、`Remake/Player.prefab` 仍只为那 1 个既有的 `66e3984f`）。
4. **烘焙回归**：跑一次菜单 `Build/Prepare PMNet Battle Content`，核对 summary 里"角色表现清理：删除 missing script=N 个"与"脚本数 0"仍成立，且 manifest 的 `contentDigest` 未因本次改动变化（若变化，说明碰到了烘焙输入指纹）。
5. **`UDPSocketManger.CloseExisting()` 迁移**：若最终要删 `UDPSocketManger`，先把 `CloseExisting`（`UDPSocketManger.cs:140`）挪到新链自有位置，改 `PMClientSessionHost.cs:893` 的调用，再删类——这是删 `UDPSocketManger` 的前置条件。

---

## 附录 A：GUID 与资产路径证据（原始行）

```
BattleManger  7200a0eb9673b6e4f8cb8386cdde31db
  Client/Assets/Scripts/Server/Manger/Battle/BattleManger.cs.meta:guid
  Client/Assets/Scenes/HYLDGameTest.unity:
    --- !u!114 &413438274
    MonoBehaviour:
      m_GameObject: {fileID: 413438266}
      m_Enabled: 1
      m_Script: {fileID: 11500000, guid: 7200a0eb9673b6e4f8cb8386cdde31db, type: 3}
      ISNet: 0
      playerManger: {fileID: 0}
      cameraManger: {fileID: 0}
      _StartGameAni: {fileID: 3262547810649915196}
      id: {fileID: 2041129036}      # 当前 BattleManger 无此字段（历史残留）
      opt: {fileID: 48908444}       # 同上

Remake/Player.prefab 既有 missing script
  --- !u!114 &6562473155793414408
  m_Script: {fileID: 11500000, guid: 66e3984fb39329042ac11aabad97b6d8, type: 3}
  database: {fileID: 0}
  selfTransform: {fileID: 7820522290180825826}
  isRun: ...                      # 该 guid 在 Client/**、PackageCache、BuiltInPackages 均无 .meta

BattleMapV1.prefab (d0460de9f6cde0646817558d59666f96) 依赖
  0bb2b73d.. SM_PROP_well.fbx / 13c0f630.. M_PROP_gravestone.mat / 9a4c9669.. M_PROP_dirtpile.mat
  ab029e9c.. M_wood_planks_06.mat / ce3f7dcd.. SM_PROP_coffin_02.fbx / 1d0e4881.. SM_PROP_column_small_v01_01.fbx
  ... (共 33 个，全部 .fbx/.mat + 引擎内置；MonoBehaviour 块 0)
PlayerVisualV1.prefab (b80ce15df403b9348ad8af507689563b) 依赖
  f9f5c1bd.. HYLD2.0/30kAnimatedCharacters/NPC_Models.FBX
  6ccf12d5.. HYLD2.0/30kAnimatedCharacters/NPC_Mecanims.FBX
  58bf525e.. HYLD2.0/Animator/Player.controller   (m_Script 出现 0 次)
  153903a7.. NPC_Man_12.mat / 2b5872a6.. NPC_Beard_04.mat / a1910f6f.. NPC_Man_13.mat / ad2e3ffe.. NPC_Tools_03.mat
  5f8e39f9.. HYLD1.0/Shaders/body.mat
```

## 附录 B：删除风险评级

| class | 资产引用 | 编译依赖 | 删除风险 | 判定 |
|---|---|---|---|---|
| `BattleManger` | **1（`HYLDGameTest.unity`）** | OldScripts×3 + `HYLDManger` + **子类** `HYLDBaoShiZhengBaManger` + `BattleData.Authority` + `BattleFrameHud` + `PMClientSessionHost/PMDsSessionHost`(注释) | **高**：直接把资产变成 missing script，且子类编译断裂 | **不可直接删**，须壳保 GUID |
| `BattleData`(6 文件) | 0 | `CommandManger`、`UIMatchingPanel`、**烘焙输入脚本用其 const**、`HYLDPlayerManger`、`BattleManger`、`UDPSocketManger` | 中高 | 不可直接删；须留 `Instance` + 被调成员 + `LocalPositionJumpTraceThreshold` |
| `HYLDPlayerManger` | 0 | `BattleManger`(AddComponent + `public HYLDPlayerManger playerManger` 字段) | 中 | 可删，但需同步改 `BattleManger` 的字段类型 |
| `HYLDBulletManger` | 0 | `BattleManger`(字段 + AddComponent)、`BattleData.Authority.SpawnVisualBullet` | 中 | 同上；`SpawnVisualBullet` 需保留符号或改调用方 |
| `UDPSocketManger` | 0 | `BattleManger`(9 处)、**新链 `PMClientSessionHost:893`** | 中高 | 删前必须先迁 `CloseExisting` |
| `CommandManger` | 0（非 MonoBehaviour） | `TouchLogic`（烘焙输入组件） | 中 | 可删，但须保 `Instance.AddCommad_Move/Attack/SuperAttack` 或改 `TouchLogic` |
| `BattleFrameHud` | 0 | `BattleManger.cs:138` | 低 | 可删，但会连带删掉 `BattleManger` 里一堆只读属性 |
| 旧 Request/Panel | 13 个面板有挂载（3 个 build-enabled 场景 + 4 个 prefab） | 面板间互引 | **高** | 面板不得直接删（会破坏大厅场景）；只有 `BaseRequest`/`RequestManger`/`UIbasePanel` 无资产引用 |

## 附录 C：最小拆耦清单（薄壳保 GUID 路线）

**可行性结论：可行，且是唯一能同时满足"保资产引用"与"删算法"的路线。** 依据 1.1（只有 `BattleManger` 需要保 GUID）、1.4（烘焙输入当前 missing script 基线 0 / 1）、1.5（编译依赖清单）。

**必须满足的 5 条硬约束**

1. **文件路径 + `class` 名 + `.cs.meta` GUID 三者原样保留**（Unity 的脚本引用是 `m_Script` = {fileID 11500000, guid}，只有 GUID 不变资产引用才不丢）。禁止把 `BattleManger` 拆分/改名/换目录；`BattleData` 若合并 6 个 partial 文件，请**保留 6 个文件与各自 GUID 不变**（虽无资产引用，但可避免 `.meta` 抖动）。
2. **`BattleManger` 必须继续是 `MonoBehaviour`**，并保留：
   - `public static BattleManger Instance { get; private set; }`（`Toolbox`/`HYLDManger`/`BattleData.Authority` 引用）
   - `public bool ISNet`（`HYLDGameTest.unity` 序列化 + `HYLDStaticValue` 赋值）
   - `public HYLDPlayerManger playerManger / HYLDCameraManger cameraManger / HYLDBulletManger bulletManger / ScenseBuildLogic ScenseBuildLogic / GameObject _StartGameAni / GameObject BG / Toolbox toolbox`（YAML 字段 + `HYLDStaticValue` 赋值；若同时删 `HYLDPlayerManger`/`HYLDBulletManger`，这两个字段类型必须同步替换或改为 `MonoBehaviour`/删字段——**改字段类型等于改序列化布局，`HYLDGameTest.unity` 上该字段会丢值（当前为 null，损失为 0）**）
   - `public virtual void Init()`、`protected virtual void OnBattleLogicTick(int frameid)`（子类 `HYLDBaoShiZhengBaManger` 的 `override`/`base.Init()` 依赖）
   - `public bool IsGameOver { get; private set; }` + `public void BeginGameOver()`（`Toolbox.cs` 4 处、`HYLDManger.cs`）
   - `BattleFrameHud` 读的那组只读属性（`CurrentPredictedFrame/CurrentTargetFrame/CurrentSyncFrame/HasDynamicTarget/IsRttReady/SmoothedRttMs/RttVarianceMs/AuthorityAgeMs/PredictionHistoryCount/SendBattleNetSimConfig`）——保留为返回 0/false 的桩，或同时删 `BattleFrameHud` 并清掉 `BattleManger.cs:138`
   - **`Update()` 必须不再 tick**（删除算法时最容易漏的副作用点：`BattleTick`/`SendOperation`/`DrainAndDispatch`/`InvokeRepeating` 全部停掉，且不要留空 `Update()` 每帧空转）
3. **`BattleData` 保壳**：`public static BattleData Instance`（无资产引用，但被 5 个类调用）、`public const float LocalPositionJumpTraceThreshold = 0.8f`（**烘焙输入脚本在用**）、`selfOperation`、`predicted_frameID`/`sync_frameID`/`smoothedRTT`/`rttVariance`/`IsRttInitialized`、`InitBattleInfo`、`EnqueueAttack`、`FlushPendingAttacksToOperation`（`UIMatchingPanel.cs:65` + `CommandManger`）。
4. **`CommandManger` 保 API**：`Instance`、`AddCommad_Move(float,float)`、`AddCommad_Attack(float,float)`、`AddCommad_SuperAttack(float,float)`、`Execute()`（`TouchLogic.cs` 5 处调用），内部改空实现/只写本地静态值即"无网络壳"。
5. **`UDPSocketManger` 顺序**：先迁 `CloseExisting()`（`UDPSocketManger.cs:140`）到新链位置并改 `PMClientSessionHost.cs:893`，再决定是否删类；在迁移完成前 `UDPSocketManger` 不可删。
6. **`HYLDBulletManger.SpawnVisualBullet(...)`**：`BattleData.Authority.cs:635` 直接调用，若删算法须保留同名同参"空表现"方法（或把该调用点一并清零）。

**应删除"算法"时的具体清单（保持不变项之外的全部网络/战斗算法）**
`BattleManger`：`BattleTick` 内 `DrainAndDispatch` / `AdjustTickInterval` / `CalcTargetFrame` / `SendOperation` / `EstimateServerFrameNow` / 攻击重发 burst / `HandleMessage` 的 UDP 分发 / Ping 调度（约 `BattleManger.cs:81-560`）。
`BattleData.Authority/.Prediction/.HitEvent/.Attack/.Rtt`：权威帧合并、SavedMove 历史、Replay、MoveAck 校正、HitEvent 去重、HP 覆写、攻击重发窗口、EWMA RTT。
`HYLDPlayerManger`：`OnLogicUpdate` 的位置推进（保留类名与 `AddComponent` 兼容性，方法体清空）。
`HYLDBulletManger`：`AllShells` 推进、`Attack`、`ResetForReconciliation`（保留 `SpawnVisualBullet` 桩或删除并清调用点）。
`UDPSocketManger`：`ReceiveLoop` 线程、`InitSocket`、`SendOperation`、`DrainAndDispatch`；**保留 `CloseExisting`**（先迁移）。
`BattleFrameHud`：整个文件可删（无法资产引用），只需清 `BattleManger.cs:138` 的 `AddComponent`。
`CommandManger`：`Execute()` 内 `BattleData` 写入 → 置空。

**验收口径（改动后必须核对）**
`Assets/Scenes/HYLDGame.unity` missing script 仍为 **0**；`Assets/Resources/Remake/Player.prefab` missing script 仍只有既有的 **1** 个（`66e3984f...`）；`Assets/Scenes/HYLDGameTest.unity` **不新增** missing script（即 `BattleManger` 壳仍在）；`Resources/PMNet` 三产物 `MonoBehaviour` 块数与依赖 GUID 集合**不变**（仍为 0 个脚本 GUID）。
