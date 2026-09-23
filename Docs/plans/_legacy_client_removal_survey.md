# 旧客户端战斗链删除前外部引用图调查（R6 旧链退役前置）

范围：`D:/UGit/hyld-master`，只读。目标对象 = 旧「联网 UDP 帧同步战斗链」：
`Client/Assets/Scripts/Server/Manger/Battle/{BattleManger,BattleData.*,HYLDPlayerManger,HYLDBulletManger,HYLDCameraManger,HYLDBaoShiZhengBaManger,GameManger}`、
`Server/Manger/UDPSocketManger.cs`、`Scripts/Manger/CommandManger.cs`、`Server/UI/BattleFrameHud.cs`，
以及旧房间进局回退（`UIMatchingPanel` 旧分支）与 `PMUdpRouter` 调用面。
非目标（未做）：服务端仿真、UE、整场景 YAML 内容解读（资产组负责）、按名字判死码、全项目无限扩散。

---

## 0. 必读文档如何决定本次入口

| 文档 | 读到的硬信息 | 对调查入口的裁决作用 |
|---|---|---|
| `AGENTS.md`（根） | 客户端文档指向 `Client/Assets/AGENTS.md`；迁移计划指向 `Docs/plans/net-architecture-migration.md` | 决定先读客户端速览与迁移计划，而不是直接扫目录 |
| `Client/Assets/AGENTS.md` §1.1 | 唯一进程身份来源 `PMNetRuntime.IsDedicatedServer`；判定链路 `PMNetBootstrap → PMNetRuntime → {Client 保持原流程(HYLDManger) / DedicatedServer → PMDsHost}` | 决定「路由先读 PMNetBootstrap + HYLDManger」 |
| 同 §3.1–3.3 | 旧链主链路就是 `TouchLogic → CommandManger → BattleData.selfOperation → BattleManger.BattleTick → UDPSocketManger.SendOperation` | 决定被删对象集合与它们之间的内部边 |
| 同 §5.1–5.5 | 函数级文件索引给出每个旧类的入口函数（`BattleManger.Init:44 / Update:81 / BattleTick:249 / HandleMessage:361`；`UDPSocketManger.InitSocket:43 / DrainAndDispatch:101 / SendOperation:114`） | 决定 grep 符号名清单（类名即包名 `Manger.*`） |
| 同 §14/§15 | 新链由 `PMClientSessionHost + PMDS1` 管理；`PMR6CombatDriver` 权威闭环；**「不走旧 CommandManger/BattleData/shell」**；HUD 只读 | 决定「先读 UIMatchingPanel/PMClientSessionHost 路由，再顺直接引用」（新链是否已断开旧链） |
| `Docs/plans/net-r6-combat-contract.md` A2/B/C/验收 | B 段「禁止旧 CommandManger/shell 并发」；C 段「移除常规 F 键 diagnostic，改 TryAttack」；T6C「不宣称旧链退役完成」 | 决定本批只出清单、不执行删除；旧链仍须保留可用 |
| `Docs/plans/net-architecture-migration.md` §决策11 / R6 行 / T46 | 「旧链退役…新链通过后删除旧路径，不长期保持双权威」「删除旧战斗 MainPack/state_mask/连发/旧 SavedMove 路径」；T46 PENDING | 决定把旧 SavedMove / BattleFrameHud / CommandManger 一并列入删除候选，但 gate 未到 |

---

## 1. 已确认（直接证据）

### 1.1 新链与旧链的接线现状（决定「入口」）

- `UIMatchingPanel.OnResponse`（`Scripts/Server/Panel/UIMatchingPanel.cs:36-67`）**双分支**：
  - `PMDsEntryCodec.HasPrefix(pack.Str)` → `PMClientSessionHost.Enter(offer)` 后 `return`（新链，第 47-56 行）；
  - 否则 `ReturnCode.Succeed` → `Manger.BattleData.Instance.InitBattleInfo(pack.BattleInfo.RandSeed, pack.BattleInfo.BattleUsers)` + `Manger.ClearSenceManger.LoadScene(SceneConfig.battleScene)`（旧回退，第 65-66 行）。
- 该旧分支的完整下游：`ClearSenceManger.LoadScene`（`Scripts/Manger/ClearSenceManger.cs:106-110`，内部 `SceneManager.LoadScene(SceneConfig.clearScene)`）→ `SceneConfig.clearScene`(=3, `HYLD2.0/Config/GameConfig.cs:20`) = `Assets/Scenes/HYLDAsyncScence.unity`（BuildSettings 索引 3，enabled）→ `AsyncLoadScene(SceneConfig.battleScene)`(=2) = `Assets/Scenes/HYLDGame.unity`（BuildSettings 索引 2，enabled）。
- 旧战斗场景 `Scenes/HYLDGame.unity` 内挂 `HYLDStaticValue`（guid `f425d8dc83f832340a6f80b7231893cf`，场景/预制体引用见 §1.4）；`HYLDStaticValue.Awake()` 结尾按 `ModenName` 用 `AddComponent` 拉起旧宿主（`HYLD1.0/Scripts/OldScripts/HYLDStaticValue.cs:214-235`）：
  - `HYLDBaoShiZhengBa` → `AddComponent<HYLDBaoShiZhengBaManger>()`；
  - `HYLDJinKuGongFang` → `AddComponent<BattleManger>()`。
  随后 `BattleManger.Init()`（`BattleManger.cs:126-142`）用 `AddComponent` 推导 `HYLDPlayerManger / HYLDCameraManger / HYLDBulletManger / BattleFrameHud`；`BattleManger.Start()`（`:143-150`）调 `UDPSocketManger.Instance.InitSocket()` 并挂 `Handle = HandleMessage`。
- 新链**不加载**旧战斗场景：`PMClientSessionHost` 全文件无 `LoadScene`；`PMDsSessionHost` 只 `SceneManager.CreateScene/UnloadSceneAsync` 自建隔离场景。新链 `Enter` 内显式关闭旧 socket（`PMClientSessionHost.cs:888-893`）。
- `PMUdpRouter` **属新 DS 链**，不是旧客户端链：真实代码调用方只有 `PMDsHost.cs:66,69,207-208`（DS 进程宿主 `new PMUdpRouter(...)` + `new PMSingleBattleRegistry()`）；`PMDsSessionHost.cs:446` 只是 `<see cref="PMDsHost"/>` 文档引用（非调用）；`PMSingleBattleRegistry` 实现 `PMUdpRouter.IBattleRegistry`。它**不含**任何对 `UDPSocketManger` / `BattleData` / `BattleManger` 的引用（全文件仅自身日志）。→ **结论：`PMUdpRouter` 不在删除集合内；旧 `UDPSocketManger` 与 `PMUdpRouter` 之间没有代码耦合**（旧链与新路由的唯一接触点是 `PMClientSessionHost.cs:893` 那句 `CloseExisting()`）。
- `Client/Assets/Editor/PMBattleContentBuild.cs:10-11` 明确记录：「直接加载 `HYLDGame.unity` 会拉起旧 `BattleManger`/`UDPSocketManger`（硬编码 UDP 7777）/UI/相机，两端必然不同图」——与新链自建场景的解法互为证据。

### 1.2 旧链外部真实引用（跨出旧链集合、非注释）

全量扫描 `Client/Assets/**/*.cs`（362 个 cs）后，旧链类型在集合外的**真实代码引用**只有下列 8 处：

| # | 位置 | 引用 | 性质 |
|---|---|---|---|
| E1 | `Scripts/Server/Panel/UIMatchingPanel.cs:65` | `Manger.BattleData.Instance.InitBattleInfo(...)` | 旧房间进局回退（分支起点） |
| E2 | `Scripts/Server/Panel/UIMatchingPanel.cs:66` | `Manger.ClearSenceManger.LoadScene(battleScene)` | 同上（`ClearSenceManger` 本身非旧战斗链） |
| E3 | `Scripts/Server/Boot/PMClientSessionHost.cs:893` | `global::Server.UDPSocketManger.CloseExisting()` | 新链关旧 socket（幂等静态入口） |
| E4 | `Scripts/Server/Manger/HYLDManger.cs:227-228` | `Manger.BattleManger.Instance != null` / `.IsGameOver` | `CloseClient` 判「是否战斗场景且未结束」 |
| E5 | `HYLD1.0/Scripts/OldScripts/HYLDStaticValue.cs:216,226` | `AddComponent<HYLDBaoShiZhengBaManger>()` / `AddComponent<BattleManger>()` | 旧局内承载宿主（单机模式 + 旧联机共用同一入口） |
| E6 | `HYLD1.0/Scripts/OldScripts/Toolbox.cs:110,111,211,263` | `Manger.BattleManger.Instance.IsGameOver` / `.BeginGameOver()` | 单机/旧战斗结算表现 |
| E7 | `HYLD1.0/Scripts/OldScripts/TouchLogic.cs:90,138,143,267,293` | `CommandManger.Instance.AddCommad_Move/SuperAttack/Attack` | 旧输入聚合（摇杆层） |
| E8 | `HYLD1.0/Scripts/OldScripts/HYLDPlayerController.cs:57`、`PlayerLogic.cs:235`、`Bullet/s/Bullet/移动型大招.cs:74` | `Manger.BattleData.LocalPositionJumpTraceThreshold` | 仅取常量 `const float = 0.8f`（`BattleData.cs:15`）做位置跳变日志门限 |

其余全部命中都是**注释/文档**，已逐条核对上下文：
`PlayerLogic.cs:31,117,177`、`shell.cs:356`、`BulletLogic.cs:47`、`移动型大招.cs:90`、`BattleFloatMath.cs:27`、`PMBattleSim.cs:42,233`、`PMCombatWeaponPlanner.cs:15`、`PMUnityCombatHud.cs:7,13`、`PMUnityBattlePresentation.cs:329`、`PMUnityMoverInput.cs:7,55`、`PMDsSessionHost.cs:7,2717`、`PMClientSessionHost.cs:29,892,2975`、`PMBattleContentBuild.cs:11,45`、`ClearSenceManger.cs:150`、`BattleData.HitEvent.cs:19`、`BattleData.Attack.cs:19,21`、`HYLDStaticValue.cs:353`、`AGENTS.md`/`Docs/*.md`。

### 1.3 旧链内部边（自洽，随集合整体消失）

- `BattleManger` ↔ `BattleData.*`（含 `SavedMove` 定义于 `BattleData.cs`，被 `BattleData.Prediction.cs` / `BattleManger.cs` 使用，**无集合外引用**）。
- `BattleManger` → `HYLDPlayerManger` / `HYLDCameraManger` / `HYLDBulletManger` / `BattleFrameHud`（均 `AddComponent`，且这四个类在集合外**无真实引用**，`HYLDCameraManger` 资产挂载例外见 §1.4）。
- `BattleManger` → `UDPSocketManger.Instance.SendOperation/InitSocket`；`UDPSocketManger.SendOperation` → `BattleData.Instance`。
- `CommandManger` → `BattleData.Instance.{EnqueueAttack, selfOperation, FlushPendingAttacksToOperation}`；集合外入口只有 `TouchLogic`（E7）。
- `BattleData.HitEvent.cs` 的 `UDPSocketManger` 提及是注释（第 19 行）。

### 1.4 资产侧挂载（GUID 定位，仅列文件名，未解读 YAML）

| 组件 | 挂载资产 |
|---|---|
| `BattleManger` (`7200a0eb…`) | `Scenes/HYLDGameTest.unity`（**不在** BuildSettings） |
| `HYLDCameraManger` (`eec213bc…`) | `HYLD1.0/Scenses/HYLDTryGame.unity`（enabled:0）、`HYLD1.0/Scenses/HYLDGame.unity`（enabled:0）、`HYLD1.0/Resources/Main Camera.prefab` |
| `ClearSenceManger` (`92998943…`) | `Scenes/HYLDAsyncScence.unity`（BuildSettings 索引 3，enabled） |
| `HYLDStaticValue` (`f425d8dc…`) | `HYLD1.0/Scenses/HYLDTryGame.unity`、`HYLD1.0/Scenses/HYLDGame.unity`、`HYLD1.0/Resources/Main Camera.prefab`、`Scenes/HYLDGame.unity`、`Scenes/HYLDGameTest.unity`、`Scenes/HYLDReGame.unity` |
| `TouchLogic` (`aed5e2a9…`) | 上述 4 个场景 + `HYLD1.0/Resources/Prefabs/{Map/Android,HYLDGameTatal,GameUI}.prefab` |
| `Toolbox` (`a1fefb86…`) | `Scenes/HYLDReGame.unity`、`Scenes/HYLDGameTest.unity`、`Scenes/HYLDGame.unity`、`HYLD1.0/HYLDResource/模式素材/toolBox.prefab`、`HYLD1.0/Scenses/toolBox.prefab`、`HYLD1.0/Resources/Prefabs/HYLDGameTatal.prefab` |
| `PlayerLogic` / `HYLDPlayerController` / `移动型大招` | 各 `Player.prefab`（`HYLD2.0`、`Resources/Remake`、`HYLD1.0/Resources`、`HYLD1.0/other/other`） |
| `BulletLogic` (`5f553acb…`) | `Resources/Remake/Bullet.prefab`、`HYLD1.0/other/other/Bullet.prefab` |
| `shell` (`98b417fb…`) | `Resources/Remake/子弹适配unity/*.prefab`（全部英雄子弹）、`HYLD1.0/HYLDResource/子弹与拖尾/大招适配unity/大招5麦克斯.prefab` |
| `GameManger` (`a0dce0c8…`)、`HYLDPlayerManger`、`HYLDBulletManger`、`HYLDBaoShiZhengBaManger`、`BattleData`、`UDPSocketManger`、`CommandManger`、`BattleFrameHud` | **无任何场景/预制体 GUID 引用**（纯普通类，或由 `AddComponent` 运行时创建） |

`GameManger.cs` 头部自述「[遗留空壳] 无任何逻辑…确认场景中无引用后可安全删除此文件」，且全项目仅自身文件命中 → 确认零引用空壳。

### 1.5 工具项目编译影响（已确认）

| 工具工程 | 对旧链的编译关系 | 删除后的影响 |
|---|---|---|
| `Tools/PMClientCheck` | `<Compile Include="Scripts/Server/**/*.cs"/>`、`Scripts/Manger/**/*.cs`、`HYLD1.0/Scripts/**/*.cs`（`PMClientCheck.csproj:79-82`）→ **编译全部旧链真文件**，且 `ClientStubs.cs` 中**没有** `UDPSocketManger` 替身 | 删文件后 glob 自动收窄，无残留；**必须同时**处理 `PMClientSessionHost.cs:893`，否则 `CS0103 UDPSocketManger`（该工程编真文件、无 stub 兜底） |
| `Tools/PMUnityGlueCheck` | 编 `PMClientSessionHost.cs`（`:97`）+ `Server/Net/**`（`:63`）+ `UnityStubs.cs`：其中 `:699-712` 是 `internal static class UDPSocketManger` **替身**（注释说明真实现会拖进整套旧链） | 删真文件**无影响**（走 stub）；若删掉 `PMClientSessionHost` 的调用，stub 变孤儿（可选清理） |
| `Tools/PMR4UnityCheck` | 编 `PMClientSessionHost.cs`/`PMDsSessionHost.cs`（`:85-86`）+ `HostDependencies.cs:31-34` 提供同款 `UDPSocketManger` 替身 | 同上 |
| `Tools/PMHeroDataCheck` | 只编 `Shared/**` + `HYLD1.0/Scripts/OldScripts/HeroData.cs`（`:34-35`） | 无影响 |
| `Tools/PMBattleContentBuildCheck` / `…SceneFactsCheck` / `…RuntimeCheck` | 编 `Editor/PMBattleContentBuild.cs`（仅注释提及旧链） | 无影响（**若要加「旧链不得回归」门禁，这里是最自然的新增点**） |
| `Tools/check_client_authority_writes.py` | `ALLOWED` 字典中 **10 条有 9 条**指向旧链文件（`:65-81`：`BattleData.HitEvent/Authority/Prediction/.cs/Attack`、`HYLDPlayerManger`、`HYLDBulletManger`、`BattleManger`、`BattleData.Rtt`） | 删除后必须同步删这 9 条白名单，否则门禁报「未知文件」或失去意义（`PlayerLogic.cs`/`HYLDStaticValue.cs`/`TouchLogic.cs` 三条保留） |

`Tools/PMClientCheck/PMClientCheck.csproj:91,105` 另记：`DouDiZhu/Scripts/rolateSelf.cs` 被旧链使用——实为 `BulletLogic.cs:196,197,215,216` 与 `HYLDBulletManger.cs:349,371` 使用；`BulletLogic` 保留，故 `rolateSelf` 编译条目**不可**随旧链删除而移除。

---

## 2. 高概率推断（依据 + 置信度）

| # | 推断 | 依据 | 置信度 |
|---|---|---|---|
| I1 | 删除旧链后，「Lobby 下发非 PMDS1 入局通知」将**没有任何接收者**，客户端停在匹配面板 | E1/E2 是唯一旧入局通路；R3-B 契约「选新后失败不回旧链」 | 高（95%） |
| I2 | `HYLDGameTest.unity`（挂 `BattleManger`）不在 BuildSettings，是编辑期测试场景，可随资产组决定改为移除组件 | BuildSettings 仅列出 4 个 enabled 场景（HYLDLogon/HYLDStart/HYLDGame/HYLDAsyncScence） | 高（85%） |
| I3 | 旧链的真正「保留理由」只剩**单机试玩**（`HYLDJinKuGongFang` / `HYLDBaoShiZhengBa` 两模式）+ 旧表现层（`PlayerLogic`/`Toolbox`/`TouchLogic`/`shell`/`BulletLogic`/`移动型大招`） | E5/E6/E7 + AGENTS §4.4「单机模式已决定全面剥离，将作为独立任务执行」 | 高（90%） |
| I4 | `HYLDBulletManger` 与 `HYLDPlayerManger` 只需随 `BattleManger` 一起删，无独立拆耦成本 | §1.2 全量扫描：集合外零真实引用 | 高（90%） |
| I5 | `BattleFrameHud` 只由 `BattleManger.cs:138` 运行时 `AddComponent` 创建，无资产挂载 | §1.4 GUID 无命中 + `BattleManger.cs:138` | 高（95%） |
| I6 | `GameManger.cs` 是纯死码 | §1.2/§1.4 零引用 + 文件自述 + 全项目 `\bGameManger\b` 只命中自身 | 高（95%） |
| I7 | `PMUdpRouter` / `PMSingleBattleRegistry` 是 DS 侧新链组件，不应进入旧链删除集合 | §1.1 调用方全为 DS 宿主；且 `PMR4UnityCheck`（排除旧链）能独立编译它们 | 高（95%） |
| I8 | 删除 `HYLDCameraManger.cs` 前须由资产组先解挂 `HYLD1.0/Resources/Main Camera.prefab`（否则 prefab 出现 missing script） | §1.4 GUID 命中该 prefab | 中高（80%） |
| I9 | `HYLD1.0/Scripts/OldScripts/*` 与 `Resources/Remake/*` 属「旧表现适配」，删旧链时**不能**顺带删，否则场景/预制体 missing script | §1.4 大量 GUID 命中 | 高（90%） |

---

## 3. 无法确定（缺少证据 / 超出本次边界）

1. **单机试玩模式的去留未获裁定**：`HYLDBaoShiZhengBaManger`/`Toolbox`/`TouchLogic`/`HYLDStaticValue` 的最终归属取决于「单机是否也一并退役」，本任务未给定；文档只写「已决定全面剥离，作为独立任务」，无排期。
2. **`Scenes/HYLDGame.unity`（battleScene=2）是否还有非 `UIMatchingPanel` 的加载方**：本次只核了 `UIMatchingPanel` 旧分支与 `HYLDGameOver → ClearSenceManger`。大厅「房主开始」「再来一局」等入口（`UIStartMainPanel` / `UIMessagePanel` 等）**未逐一核查**。
3. **`HYLD1.0/Resources/Main Camera.prefab` 运行时是否仍被加载**：无法从 GUID 命中判定其加载者（需资产组/运行时验证）。
4. **`Client/Assets/HYLD1.0/Scenses/*.unity`（enabled:0）是否仍被任何代码按名字加载**：只检查了 `SceneManager.LoadScene` 的字面量调用（`HYLDGame`/`HuangYeLuanDouStart` 两条），未穷举 `LoadScene(string变量)`。
5. **场景/预制体是否以「方法名字符串」引用旧链**（迁移计划 §1419 曾用此法证明 `setBulletInformation` 零调用）：本次未做 prefab/scene 内字符串扫描（资产组负责）。
6. **`BattleData.LocalPositionJumpTraceThreshold` 的最佳新家**（搬到 `Shared` / `Scripts/Math` / 内联）未定，属实现决策。
7. **`Client/Assets/HYLD1.0` 之外是否存在其他 `BattleData` 同名类型**：已确认无（`rg` 全局仅一处 `class BattleData`），但 `Server/` 与 `ProtobufAndNotepad/` 未查（非客户端交付物）。

---

## 4. 已检查范围

- 必读文档（全文）：`AGENTS.md`、`Client/Assets/AGENTS.md`（638 行）、`Docs/plans/net-r6-combat-contract.md`；定点核对 `Docs/plans/net-architecture-migration.md`（§决策 11、R6 行、T46/T47、R6 交付段、§1419 死码判定法）。
- 源码：`Client/Assets` 全部 362 个 `.cs`（`rg --no-ignore`，排除 `Client/Library`、二进制）。
- 文本非 cs：`Client/Assets/**/*.{md,lua,json,txt}`（旧链命中全在 md 文档）；`.asmdef`（无）。
- 资产（仅 GUID 级 `-l`，不解读 YAML）：`Client/Assets/**/*.{unity,prefab}`。
- 工程/Settings：`Client/ProjectSettings/EditorBuildSettings.asset`、`Client/.gitignore`。
- 工具：`Tools/**/*.{csproj,cs,py}` 中对旧链的编译与白名单引用。
- 逐文件读取：`PMNetBootstrap.cs`、`HYLDManger.cs`、`RequestManger.cs`、`UIMatchingPanel.cs`、`UDPSocketManger.cs`、`ClearSenceManger.cs`、`GameManger.cs`（头部）、`BattleManger.cs`（`Init/Start` 段）、`HYLDCameraManger.cs`（头部）、`HYLDBaoShiZhengBaManger.cs`（头部）；定点读取 `PMClientSessionHost.cs`（1-60、870-900、2965-2985）、`HYLDStaticValue.cs`（100-260）、`HYLDPlayerController/PlayerLogic/移动型大招`（命中处 ±20 行）、`check_client_authority_writes.py`（40-95）。
- 「全引用终判」使用 `rg --no-ignore` 的**原因**：需要「删除前不留悬空引用」的完备性证据，而 pi-fff 内容索引对 gitignore 区域（`Client/Library` 等）与非索引根为 partial 语义；本次仍以 `ffgrep`（绝对路径 `D:/UGit/hyld-master/Client/Assets`）做首轮排序定位，再以 `rg --no-ignore -g '*.cs'`（限定 `Client/Assets`，排除 `Library`/二进制）做终判。**未**对 `Server/`（服务端）与 `HyldDS/` 做扫描（非目标）。

---

## 5. 建议下一步（最小补充查询或运行时验证）

1. **裁定单机去留**（阻塞 §3.1，也直接决定 `HYLDBaoShiZhengBaManger`/`Toolbox`/`TouchLogic` 是删是改）。最小问题：单机试玩是否与旧联机同步退役？
2. **补一次「谁加载 HYLDGame/旧场景」的穷举**：`rg -n "LoadScene" -g '*.cs' Client/Assets` 全量核对每个调用点（本次只看了两条），并确认 `Scenes/HYLDGame.unity` 与 `HYLD1.0/Scenses/*` 的加载者。
3. **让资产组处理 3 个挂载点**：`Scenes/HYLDGameTest.unity`(BattleManger)、`HYLD1.0/Resources/Main Camera.prefab`(HYLDCameraManger/HYLDStaticValue/TouchLogic)、`Scenes/HYLDGame.unity`(HYLDStaticValue/TouchLogic/Toolbox)——在删类前解挂或确认随场景退役。
4. **门禁与工具同步项**（可与删除同批）：`Tools/check_client_authority_writes.py:65-81`（删 9 条白名单）、`Tools/PMUnityGlueCheck/UnityStubs.cs:699-712` 与 `Tools/PMR4UnityCheck/HostDependencies.cs:31-34`（stub 变孤儿，可选清理）、`Tools/PMClientCheck/PMClientCheck.csproj`（glob 无需改；**不可**删 `rolateSelf` 条目）。
5. **删前的编译验证顺序**（本任务不执行）：改 `PMClientSessionHost.cs` 去掉 `CloseExisting()` → 跑 `PMClientCheck` + `PMUnityGlueCheck` + `PMR4UnityCheck`（证明无悬空引用）→ 再删文件。
6. **文档同步**：`Client/Assets/AGENTS.md` §3/§5/§7/§8、`Client/Assets/Docs/ForServer.md`、`Client/Assets/Docs/BattlePipelineGuide.md` 全部以旧链为主干描述，删除后需整体重写或标注「已退役」。

---

## 6. 文件清单

### 6.1 DELETE（旧链主体，含 `.meta` 一并删除）

> 前置条件：§5 第 1、2、3 项完成；`PMClientSessionHost.cs` 已去掉 `CloseExisting()`。下列 15 个文件 + 各自 `.meta`。

| # | 文件 | 外部阻塞引用 | 处置前置 |
|---|---|---|---|
| D1 | `Client/Assets/Scripts/Server/Manger/Battle/BattleManger.cs` | E4(`HYLDManger:227-228`)、E5(`HYLDStaticValue:226`)、E6(`Toolbox`)、资产：`Scenes/HYLDGameTest.unity` | 改 E4/E5/E6 + 资产解挂 |
| D2 | `Client/Assets/Scripts/Server/Manger/Battle/BattleData.cs` | E1(`UIMatchingPanel:65`)、E8（常量 3 处） | 改 E1；E8 常量搬迁 |
| D3 | `Client/Assets/Scripts/Server/Manger/Battle/BattleData.Attack.cs` | 无（集合内） | — |
| D4 | `Client/Assets/Scripts/Server/Manger/Battle/BattleData.Authority.cs` | 无（集合内） | — |
| D5 | `Client/Assets/Scripts/Server/Manger/Battle/BattleData.HitEvent.cs` | 无（集合内；`UDPSocketManger` 仅注释） | — |
| D6 | `Client/Assets/Scripts/Server/Manger/Battle/BattleData.Prediction.cs` | 无（集合内；旧 `SavedMove` 使用方） | — |
| D7 | `Client/Assets/Scripts/Server/Manger/Battle/BattleData.Rtt.cs` | 无（集合内） | — |
| D8 | `Client/Assets/Scripts/Server/Manger/Battle/HYLDPlayerManger.cs` | 无 | — |
| D9 | `Client/Assets/Scripts/Server/Manger/Battle/HYLDBulletManger.cs` | 无（`rolateSelf` 依赖方，但 `BulletLogic` 仍在用，`rolateSelf` 不删） | — |
| D10 | `Client/Assets/Scripts/Server/Manger/Battle/HYLDCameraManger.cs` | 资产：`HYLD1.0/Resources/Main Camera.prefab`、`HYLDTryGame.unity`、`HYLD1.0/Scenses/HYLDGame.unity` | **必须**先资产解挂 |
| D11 | `Client/Assets/Scripts/Server/Manger/Battle/GameManger.cs` | 无（自述空壳，零引用） | — |
| D12 | `Client/Assets/Scripts/Server/Manger/Battle/HYLDBaoShiZhengBaManger.cs` | E5(`HYLDStaticValue:216`) | 取决于单机裁定 |
| D13 | `Client/Assets/Scripts/Server/Manger/UDPSocketManger.cs` | E3(`PMClientSessionHost:893`) | 改 E3 |
| D14 | `Client/Assets/Scripts/Manger/CommandManger.cs` | E7(`TouchLogic`×5) | 改 E7（或随单机裁定） |
| D15 | `Client/Assets/Scripts/Server/UI/BattleFrameHud.cs` | 仅 `BattleManger.cs:138` | 随 D1 |

### 6.2 MODIFY（删除前必须先做的最小改动）

| # | 文件 | 改动 | 说明 |
|---|---|---|---|
| M1 | `Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs:888-893` | 删除 `global::Server.UDPSocketManger.CloseExisting();` 与注释；保留「旧链已退役」说明 | 硬阻塞（否则 Unity 与 `PMClientCheck` 编译失败） |
| M2 | `Client/Assets/Scripts/Server/Panel/UIMatchingPanel.cs:60-67` | 删除旧回退分支（`InitBattleInfo` + `ClearSenceManger.LoadScene`）；非 PMDS1 时改为显式报错/忽略 | 旧房间进局回退的唯一落点 |
| M3 | `Client/Assets/Scripts/Server/Manger/HYLDManger.cs:226-238` | 去掉 `BattleManger.Instance` 依赖（`CloseClient` 的战斗场景判定改为不依赖旧链，或整段删除） | 大厅 TCP 看护逻辑需重新表述 |
| M4 | `Client/Assets/HYLD1.0/Scripts/OldScripts/HYLDStaticValue.cs:214-235` | 删除 `AddComponent<HYLDBaoShiZhengBaManger>()` / `AddComponent<BattleManger>()` 两个分支（**保留** Hero 表与 `PoisonDamagePerTick` 等常量） | 取决于单机裁定 |
| M5 | `Tools/check_client_authority_writes.py:65-81` | 删除 9 条指向旧链的 `ALLOWED` 条目（保留 `PlayerLogic`/`HYLDStaticValue`/`TouchLogic` 三条） | 门禁同步 |
| M6 | `Tools/PMUnityGlueCheck/UnityStubs.cs:699-712` | （可选）删除 `UDPSocketManger` 替身 | 孤儿 stub |
| M7 | `Tools/PMR4UnityCheck/HostDependencies.cs:9-35` | （可选）同上 | 孤儿 stub |
| M8 | `Client/Assets/HYLD1.0/Scripts/OldScripts/HYLDPlayerController.cs:57` | `Manger.BattleData.LocalPositionJumpTraceThreshold` → 中立常量/内联 | 仅当保留旧表现层（单机） |
| M9 | `Client/Assets/HYLD1.0/Scripts/OldScripts/PlayerLogic.cs:235` | 同上 | 同上 |
| M10 | `Client/Assets/HYLD1.0/Scripts/OldScripts/Bullet/s/Bullet/移动型大招.cs:74` | 同上 | 同上 |
| M11 | `Client/Assets/HYLD1.0/Scripts/OldScripts/TouchLogic.cs:90,138,143,267,293` | 去掉 `CommandManger` 调用（仅保留摇杆/瞄准表现） | 仅当保留单机 |
| M12 | `Client/Assets/HYLD1.0/Scripts/OldScripts/Toolbox.cs:110,111,211,263` | 去掉 `BattleManger.Instance` 分支 | 仅当保留单机 |
| M13 | `Client/Assets/Scripts/Manger/ClearSenceManger.cs:150` | （可选）删除注释掉的 `Server.UDPSocketManger.Instance.Send` | 纯注释 |
| M14 | `Client/Assets/AGENTS.md` §3/§5/§7/§8、`Client/Assets/Docs/ForServer.md`、`Client/Assets/Docs/BattlePipelineGuide.md` | 旧链退役后重写/标注 | 文档，属必做收尾 |
| M15 | `Docs/plans/net-architecture-migration.md`（T46/T47、R6 行、决策 11） | 删除完成后更新状态 | 唯一进度源 |

### 6.3 KEEP（**不得**因删旧链而删除；删了会造成 missing script 或破坏新链）

| # | 文件/资产 | 保留理由 |
|---|---|---|
| K1 | `Client/Assets/HYLD1.0/Scripts/OldScripts/Bullet/s/Bullet/shell.cs` | 挂在 `Resources/Remake/子弹适配unity/*.prefab` 全部英雄子弹 + 麦克斯大招 prefab |
| K2 | `Client/Assets/HYLD1.0/Scripts/OldScripts/Bullet/s/Bullet/BulletLogic.cs` | 挂 `Resources/Remake/Bullet.prefab`、`HYLD1.0/other/other/Bullet.prefab`；迁移计划 §1419 已裁定「类保留、只删死方法」 |
| K3 | `Client/Assets/HYLD1.0/Scripts/OldScripts/Bullet/s/Bullet/移动型大招.cs` | 挂 `Player.prefab`(×3) 与 `大招5麦克斯.prefab` |
| K4 | `Client/Assets/HYLD1.0/Scripts/OldScripts/PlayerLogic.cs`、`HYLDPlayerController.cs` | 挂 `Player.prefab`(×4) |
| K5 | `Client/Assets/HYLD1.0/Scripts/OldScripts/TouchLogic.cs`、`Toolbox.cs`、`HYLDGameOver.cs` | 挂多个场景/预制体；单机试玩输入与结算表现 |
| K6 | `Client/Assets/HYLD1.0/Scripts/OldScripts/HYLDStaticValue.cs` | 挂 6 个场景/预制体；全局静态数据 + Hero 表（新链不读，但资产与单机读） |
| K7 | `Client/Assets/Scripts/Manger/ClearSenceManger.cs` | 挂 `Scenes/HYLDAsyncScence.unity`（BuildSettings enabled）；场景切换通用件 |
| K8 | `Client/Assets/Scripts/Server/Net/PMUdpRouter.cs`、`PMSingleBattleRegistry.cs` | 新 DS 链：真实调用方只有 `PMDsHost.cs:66,69,207-208`；`PMSingleBattleRegistry` 实现 `PMUdpRouter.IBattleRegistry`；`PMDsSessionHost.cs:446` 仅 `<see cref>` 文档引用 |
| K9 | `Client/Assets/Scripts/Shared/{PMBattleSim,BattleNumericConfig}.cs`、`Scripts/Math/BattleFloatMath.cs` | 新链共享数值/仿真核心 |
| K9b | `Client/Assets/Scripts/Server/ConstValue.cs`（实际类名 `Server.NetConfigValue`） | **不能删**：`frameTime` 被保留的 `shell.cs:181,191` 与 `HYLDStaticValue.cs:47` 读取，`ServiceIP` 被 `HYLDManger.cs:51` 读取；但其中 `PredictionHistoryWindowSize` / `EnablePredictionReconciliationPipeline` / `pingIntervalMs` / `moveMagnitudeThreshold` / `moveDotThreshold` 只服务旧链 → 删旧链后本文件变「半死文件」（可后续瘦身，不可整删） |
| K10 | `Client/Assets/Scripts/Server/{Boot,Net,Panel,Manger(HYLDManger/PingPong/Request/PmRpc/TCPSocket)}`、`Scripts/PMNet/**`、`PMR3/**`、`PMUnity/**`、`PMCombat/**`、`PMProjectile/**`、`PMPrediction/**`、`PMMover/**` | 新链主体 |
| K11 | `Client/Assets/HYLD1.0/other/Sources/DouDiZhu/Scripts/rolateSelf.cs` | 被 `BulletLogic`（K2）使用 → 不可随旧链删除（`PMClientCheck.csproj:97` 编译条目同理保留） |
| K12 | `Client/Assets/Scenes/{HYLDLogon,HYLDStart,HYLDAsyncScence}.unity`、`Client/Assets/HYLD1.0/Resources/Main Camera.prefab` | 已启用场景/被引用预制体；其**组件**处置属资产组（见 §5.3），文件本身不删 |

---

## 7. 工具项目编译影响汇总（结论）

- **会因删除而报错、必须先改的来源**：`Tools/PMClientCheck`（编真 `Scripts/Server/**`，无 `UDPSocketManger` stub）——唯一硬阻塞是 `PMClientSessionHost.cs:893`。
- **使用替身、不受影响**：`Tools/PMUnityGlueCheck`（`UnityStubs.cs:699-712`）、`Tools/PMR4UnityCheck`（`HostDependencies.cs:31-34`）。
- **完全无关**：`Tools/PMHeroDataCheck`（只编 `Shared/**` + `HeroData.cs`）、`PMBattleContent*Check` 三件套（只编 `Editor/PMBattleContentBuild.cs` 等，旧链仅出现在注释）。
- **非编译但必须同步**：`Tools/check_client_authority_writes.py` 的 `ALLOWED` 白名单（9/10 条指向旧链）。
- **Unity 侧**：`Client/Assets` 无 `.asmdef`（单一 Assembly-CSharp），所以删文件不需要改程序集定义；但**场景/预制体挂载**是真正的风险点（§1.4），必须由资产组先行处理。
