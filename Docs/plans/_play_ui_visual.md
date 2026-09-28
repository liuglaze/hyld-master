# T-PLAY4 局内操控 UI（视觉复用 + 新接口重接）：实现报告

状态唯一源：`Docs/plans/net-architecture-migration.md`（本文件是**交付报告**，不另立进度源）。
契约依据：「T-PLAY 第一阶段接口与验收细化」末段、「T-PLAY4 UI与输入接口冻结」整节；
旁证：`net-r6-combat-contract.md`「C 宿主接线冻结」末段、`net-legacy-retirement-contract.md`、
`Client/Assets/AGENTS.md` §1/§3/§6。

**总状态：代码侧完成；真实 Unity 实机（T-PLAY5）PENDING_USER。**
本轮不启动 Unity / 不启动服务 / 不改任何 `.asset` / `.prefab` / `.unity` / 不提交 / 不递归委派。

---

## 1. 交付摘要

| 项 | 结果 |
|---|---|
| 新增客户端专有 UI | `Client/Assets/Scripts/PMUnity/PMUnityBattleControls.cs`（+ 唯一 `.cs.meta`） |
| 旧资源复用方式 | **只读**读取 `Resources.Load<GameObject>("Prefabs/GameUI")` 的 `Image.sprite` 与 `RectTransform`/`CanvasScaler` 布局值；**绝不 Instantiate**、**绝不激活**、**绝不接触旧脚本** |
| UI 自身构成 | 本局专属 `Canvas`（ScreenSpaceOverlay）+ `GraphicRaycaster` + 一个指针转发组件；三根摇杆（`PlayerMove` / `FireNormal` / `FireSuper`）+ 只读 HP / Mana / 大招能量 + 提示行 |
| 权限链 | UI 只经三个**委托出口**（= 冻结入口 `TrySetUiMove` / `TryQueueUiAttack` / `TryGetUiCombatSnapshot`）转发输入与读取快照；**不发 RPC、不写复制/权威字段、不做伤害/判定** |
| 编译门 | `PMR4UnityCheck`（真实 Unity2019 API + 真实 `UnityEngine.UI.dll`）**0 错误**、编入完整实现；`PMClientCheck` 0 警告 0 错误；`PMUnityGlueCheck` 0 错误（两者走替身面） |
| 源真相门 | `PMBattleContentSceneFactsCheck`：**build 0 / run 0**，共 **373 项 / 失败 0**（其中 J 段 T-PLAY4 新增 **135 项**） |
| 未改动 | `PMClientSessionHost.cs`、`PMUnityMoverInput.cs`、任何资产、任何场景/Prefab/`.asset` |

---

## 2. 契约逐条落点（冻结文字 → 实现）

| 冻结要求 | 落点 |
|---|---|
| 「只读从 inactive 旧 GameUI prefab 抽**可验证**的 Sprite 引用」 | `TryResolveSprites`：节点路径优先、`sprite.name` 兜底；4 张必需 Sprite 缺失即**逐个点名失败** |
| 「构建本局安全 uGUI Canvas 和 PlayerMove/FireNormal/FireSuper 视觉及 HP/Mana/Energy」 | `BuildCanvas` + `BuildControls`；`PlayerMove` 左下、`FireNormal`/`FireSuper` 右下（= 旧 `joyAnchor` 7/9） |
| 「pointer down/drag/up 经上述冻结入口」 | 指针转发组件 → `ApplyPointer` / `ReleaseStick` → 委托出口 |
| 「瞄准摇杆**释放才提交**攻击」 | `ReleaseStick(submit:true)` 只在 `OnPointerUp` 的**边沿**调用 `QueueAttack` |
| 「不实例化或激活旧 GameUI 根/TouchLogic/EasyTouch/旧按钮持久监听」 | 源码去注释后 25 个禁用标记命中 0 处（J2）；旧 prefab 只作数据源 |
| 「不触碰旧 Scene/YAML」 | 全文件只有 `Resources.Load` 一处资源访问，无 `LoadScene`/`SceneManager`/`File` |
| 「允许 Prefab 缺席时受控失败，不伪造完整 UI」 | `Create` 返回 `null` + 明确 `error`（DS 进程 / prefab 缺失 / Sprite 缺失 / 布局节点缺失） |
| 「仅实际可用的普通/直线大招需有功能，Unsupported 提示不扣资源」 | 大招摇杆可用性完全由快照 `SuperReady` 决定；不可用时手柄换「未满」图标且按下被忽略；UI **不**做资源判断、不扣任何资源 |
| 「DS 禁创建」 | `Create` 首行按 `PMNetRuntime.IsDedicatedServer` 拒绝 |
| 「客户端生成独立 root 并在 Stop/换局/终局释放」 | 独立 `[PMUnityBattleControls]` root（`DontDestroyOnLoad`，不属于战斗场景）；`Dispose` 幂等 |
| 「UI 组不改 Host，由主侧在 batch 后将 Create/Update/Dispose 接到会话宿主」 | **未改** `PMClientSessionHost.cs`；接线代码见 §8 |
| 「需要 `UnityEngine.UI.dll` 真实 2019 API 编译」 | `PMR4UnityCheck.csproj` 增真实 `Client/Library/ScriptAssemblies/UnityEngine.UI.dll` + `UIModule` + `TextRenderingModule` |
| 「stubs 差异用真实编译门与编译边界说明，不在无证据处新增 `#if` 绕过生产代码」 | `#if UNITY_2019_1_OR_NEWER || UNITY_EDITOR` 内是**完整实现**（真实门里不跳过）；`#else` 只有一个显式失败的替身面；见 §7 |
| 「新增脚本 meta 唯一」 | `.meta` guid `d20d282d16237227b360296a88720a20`，J4 断言在 `Client/Assets` 内出现次数 == 1 |
| 「中文源码 BOM+CRLF；meta 无 BOM LF」 | J4 逐字节断言（源码 BOM + 全 CRLF；meta 无 BOM + 全 LF） |

---

## 3. 文件与接口

### 3.1 新增文件

- `Client/Assets/Scripts/PMUnity/PMUnityBattleControls.cs`（UTF-8 **BOM** + CRLF）
- `Client/Assets/Scripts/PMUnity/PMUnityBattleControls.cs.meta`（无 BOM + LF，guid 唯一）

命名空间 `PMNet.Unity`。类型：

| 类型 | 作用 |
|---|---|
| `PMUnityBattleUiStatus`（顶层 struct） | UI 消费的只读快照口径（Hp/MaxHp/Mana/SuperEnergy/Dead/MatchEnded/NormalReady/SuperReady/LastReject）+ `Offline()` |
| `PMUnityBattleMoveInputHandler` | `bool (float screenX, float screenY)`（= `TrySetUiMove`） |
| `PMUnityBattleAttackInputHandler` | `bool (bool isSuper, float screenX, float screenY)`（= `TryQueueUiAttack`） |
| `PMUnityBattleStatusQuery` | `bool (out PMUnityBattleUiStatus)`（= `TryGetUiCombatSnapshot` 的适配器） |
| `PMUnityBattleControlMath`（静态） | **零 Unity 依赖**的纯逻辑：有限性/钳制/摇杆归一化/半径钳制/瞄准长度/近等；三个编译门都会类型检查它 |
| `PMUnityBattleControls : MonoBehaviour, IDisposable` | UI 本体（真实实现） |
| `PMUnityBattleControlsPointerRelay : MonoBehaviour` | 单一指针事件转发器（挂在 Canvas 根，按命中对象路由三根摇杆） |
| `JoystickVisual` / `JoystickState`（internal） | 摇杆视觉件与输入状态（多指安全 + 事件去重） |

### 3.2 公开接口（主侧集成面）

```csharp
// 创建：失败一律返回 null + 原因（不抛、不静默）
public static PMUnityBattleControls Create(string matchId, out string error);

// 绑定三路宿主出口（可多次调用；传 null = 这一路暂时没有）
public void Bind(PMUnityBattleMoveInputHandler setMove,
                 PMUnityBattleAttackInputHandler queueAttack,
                 PMUnityBattleStatusQuery queryStatus);

// 每帧刷新（幂等）：查快照 → 翻译成可用性 → 刷新显示（全部去重）
public void UpdateStatus();

// 释放（幂等）：先清摇杆输入（**不提交攻击**）→ 收回本局专属事件系统 → 销毁自己的 root
public void Dispose();

// 诊断（门禁/心跳对账）
public bool IsDisposed { get; }
public string MatchId { get; }
public string Describe();
public bool StatusAvailable { get; }
public PMUnityBattleUiStatus LastStatus { get; }
public Canvas CanvasObject { get; }
public bool LegacyPrefabRootWasInactive { get; }
public bool LegacyCanvasScalerCopied { get; }
public bool FontAvailable { get; }
public const bool UguiImplementationCompiled = true;   // 替身面为 false
// 计数（只增）：StatusTicks / StatusUnavailableTicks / MovePushCount / MovePushRejectedCount /
//               AttackQueueCount / AttackQueueRejectedCount / AttackSuppressedCount / CancelCount
```

> **命名差异登记（供主侧集成）**：冻结文字允许「`UpdateStatus` 或 `instance.Update`」，本实现显式提供
> `UpdateStatus()`，**同时**保留 Unity 自动 `Update()` 作为安全网（两者等价且幂等）；宿主推荐显式调用
> `UpdateStatus()` 以获得确定的帧内顺序。

### 3.3 生命周期语义（与 HUD 的差异，务必注意）

`PMUnityCombatHud` 是「终局后**保留**只读结论直到下一 Enter/Stop」；**操控 UI 不是**：
操控 UI 在本局终局/死亡时**仍然存在但禁用**（不允许开火、不允许移动），并在
`Dispose`（Stop / 下一 Enter / 终局退场）时**销毁**。理由：终局后残留可点的攻击摇杆会让玩家误以为还能操作。

---

## 4. 旧 `GameUI.prefab` 的只读事实（J1 断言过的源真相）

源文件：`Client/Assets/HYLD1.0/Resources/Prefabs/GameUI.prefab`（guid `af998dab9af610e4986aa70670bf2cc6`），
ForceText YAML、CRLF、无 BOM；解析出 27 个 GameObject、27 个 Transform/RectTransform 文档。

### 4.1 根与关键节点

| 路径 | 事实 |
|---|---|
| `/GameUI` | 根 GameObject，**`m_IsActive: 0`（inactive）**；挂 `Canvas`(223)+`RectTransform`(224)+`CanvasScaler`+`GraphicRaycaster`+`GameUITeamGemLogic`+`HYLDHeropropertyUI` |
| `/GameUI/Android` | `TouchLogic`（旧触摸总控） |
| `/GameUI/Android/EasyTouch` | `EasyTouch` |
| `/GameUI/Android/PlayerMove` | `EasyJoystick`，`joyAnchor: 7`（LowerLeft），`zoneRadius: 100`，`deadZone: 20` |
| `/GameUI/Android/FireNormal` | `EasyJoystick`，`joyAnchor: 9`（LowerRight），同上 |
| `/GameUI/Android/FireSuper` | `EasyJoystick`，`joyAnchor: 9`，**`m_IsActive: 0`**，`touchTexture` 用 `Fulled.png` |
| `/GameUI/Android/FireNormalButton` | `EasyButton`（`receiverGameObject` → Android） |
| `/GameUI/Button` | uGUI `Button` + `Image`（`返回.png`），`UnityEvent.m_MethodName: backStart`（**旧持久监听**） |
| `/GameUI/GemSelfTeamUI` | 本队行：anchor `(0,1)`、`anchoredPosition (244, −85.2)`、`sizeDelta (439.11, 53.24)` |
| `/GameUI/GemEnemyTeamUI` | 对队行：anchor `(1,1)`、`anchoredPosition (−244.2, −85.2)`、`localScale.x = −1`（镜像） |
| `/GameUI/能量条` | 大招能量 `Slider`（`m_Direction: 0` 左→右、`m_FillRect` = Fill Area/Fill）；anchor 中心、`anchoredPosition (351.5, −296.6)`、`sizeDelta (218.78, 181.83)` |
| `/GameUI/能量条/Background` | `Image` Sprite = `FullBG.png` |
| `/GameUI/能量条/Fill Area/Fill` | `Image` Sprite = `FullPower.png` |
| `/GameUI/能量条/Image` | `Image` Sprite = `UnFullImage.png` |
| `/GameUI/GemSelfTeamUI/Image`、`/GameUI/GemEnemyTeamUI/Image` | `Image` Sprite = `Gem 1.png` |

`/GameUI` 的 CanvasScaler：`m_UiScaleMode: 0`（ConstantPixelSize）、`m_ScaleFactor: 1`、
`m_ReferencePixelsPerUnit: 100`、`m_ReferenceResolution (800, 600)`、`m_ScreenMatchMode: 0`。

### 4.2 UI 复用的 4 张 Sprite（唯一**可验证**的 uGUI Sprite 来源）

| 角色 | 节点路径 | Sprite guid | 源图 |
|---|---|---|---|
| 底盘/边框（`frame`） | `能量条/Background` | `1ecc8b15ae5d98c41a0ecf57a4f118ed` | `HYLDResource/SuperFireUI/FullBG.png`（1030×1004 圆盘） |
| 填充/手柄（`fill`） | `能量条/Fill Area/Fill` | `8e6b3e0a7f62eb044a849332e3c9a18b` | `HYLDResource/SuperFireUI/FullPower.png`（1030×1004） |
| 大招「未满」图标 | `能量条/Image` | `ab50dae53b97cce488f2b49b637fe5dc` | `HYLDResource/SuperFireUI/UnFullImage.png`（91×89） |
| 队伍宝石图标 | `GemSelfTeamUI/Image` | `6c515d343436ee4469a93989e49ec0a6` | `Images/Gem 1.png`（87×98） |

### 4.3 旧摇杆素材**不是** uGUI Sprite（重要的美术边界）

旧 `PlayerMove`/`FireNormal`/`FireSuper` 是 `EasyJoystick`，其外观来自 **Texture2D**：
`Images/joystick.png`（area）、`Images/joystick2.png`（touch）、
`EasyTouch/Plugins/Resources/RadialJoy_Dead.png`（dead zone）、`SuperFireUI/Fulled.png`（FireSuper touch）。
它们以 `fileID: 2800000`（Texture 主资产）序列化，**不是** `Image.m_Sprite`（`21300000`），
且只能经旧类型字段读到（本文件不得引用旧类型）。
因此：**摇杆/血条的视觉是用旧素材的近似复用**，不是旧像素级还原 —— 这一点在 §10 明确登记，不许当作「完整旧 UI 还原」。

---

## 5. 「零激活」的结构保证与静态负例

旧 prefab 里**确实**挂着会写玩法状态的旧脚本（`TouchLogic` / `EasyTouch` / `EasyJoystick` / `EasyButton` /
`GameUITeamGemLogic` / `HYLDHeropropertyUI`），并且 `/GameUI/Button` 挂着 `backStart` 持久监听。
所以「零激活」不能靠自觉，本批把它钉成三层：

1. **实现层**：全文件对旧 prefab 只有 `Resources.Load<GameObject>("Prefabs/GameUI")` 一次读取；
   没有 `Instantiate` / `SetActive` / `SendMessage` / `FindObjectOfType` / `LoadScene`；
   所有新建对象都是自己 `new GameObject(...)` + `AddComponent<自己的类型>`。
2. **门禁层（静态负例）**：J2 对**去注释后的源码**扫描 25 个禁用标记（旧脚本名 / 旧联网名 /
   `PMNet_` RPC 前缀 / 三条战斗 RPC / `LoadScene` / `SceneManager` 等），要求命中 **0 处**；
   并把每个标记**注入**源码文本后重扫，要求 **25/25 全部命中**（证明 0 命中不是扫描器空转）。
3. **资产层（可执行负例）**：J1 对旧 prefab 文本做**变异**——
   抹掉 `UnFullImage` 的 Sprite 引用 → Sprite 断言必须失败；
   把根的 `m_IsActive` 改成 1 → 「根 inactive」断言必须失败。
   即两条断言都不是恒真。

---

## 6. 输入语义（冻结口径的落地）

### 6.1 屏幕口径（**不换轴、不取反 Y**）

- UI 输出值域 `[-1, 1]`，`screenX` = 本地 dx / 半径，`screenY` = 本地 **+dy** / 半径；
  uGUI 本地空间 `+Y` 就是向上，与「screen up → world −X；screen right → world +Z」同向，**故不取反**。
- 换轴到世界方向由宿主完成（`PMUnityMoverInput.TryScreenVectorToWorld` / `TryScreenAimDirection`），
  UI 只负责屏幕向量；J3 用「向上拖 ⇒ screenY 为正」以及「取反口径会被抓住」的正负例钉住这条。

### 6.2 状态机（多指安全 + 事件去重）

| 事件 | 行为 |
|---|---|
| `down`（未按住且可用） | 激活该 `pointerId`；移动按当前值推送，攻击**只记瞄准** |
| `down`（已按住 / 不可用） | 忽略（去重；不重置、不重复激活） |
| `drag`（激活者） | 移动推送（按值去重），攻击只更新瞄准 |
| `drag`（非激活者） | 忽略（多指安全） |
| `up`（激活者） | 移动**清零**；攻击**提交一次**（`|aim| < MinAimLength` 时不提交，只记抑制） |
| `up`（非激活者 / 已释放） | 忽略（边沿去重） |
| `Cancel`（死亡/终局/无快照/`OnDisable`/`Dispose`） | 移动清零；攻击**不提交** |

其余纯逻辑：半径 100（= 旧 `zoneRadius`）、死区比例 0.2（= 旧 `deadZone/zoneRadius`，死区内输出显式 `(0,0)`）、
超半径钳到 ±1、NaN/±Inf 一律拒绝、移动推送近等去重（`MovePushEpsilon = 0.0005`）、
攻击最小有效长度 `MinAimLength = 0.05`。

### 6.3 UI **不会**做的事

不发送 RPC、不写 `Hp/Mana/SuperEnergy`/复制字段、不做死亡/胜负/命中判定、不扣资源、
不读旧 `HYLDStaticValue`/`BattleData` 取「真值」、不采样网络状态、不推进任何仿真。

---

## 7. 编译边界（为什么这样切 `#if`）

本文件同时被三个门编译，三者的 Unity 面**不同**：

| 门 | 定义的宏 | Unity 面 | 编到的分支 |
|---|---|---|---|
| `Tools/PMR4UnityCheck` | `UNITY_EDITOR`（显式） | **真实** Unity2019 DLL + 真实 `UnityEngine.UI.dll` | **完整实现**（`UguiImplementationCompiled = true`） |
| `Tools/PMClientCheck` | 无 | 手写 `ClientStubs.cs`（含部分 `UnityEngine.UI`，**缺** uGUI 细节） | 替身面（`false`） |
| `Tools/PMUnityGlueCheck` | 无 | 手写 `UnityStubs.cs`（**没有** `UnityEngine.UI`） | 替身面（`false`） |

因此：

- **真实实现区**用 `#if UNITY_2019_1_OR_NEWER || UNITY_EDITOR` 划定；正式 Player 里
  `UNITY_2019_1_OR_NEWER` 存在，Editor 里 `UNITY_EDITOR` 存在，真实门里**不跳过实现**（这是契约的硬要求）。
- **`#else` 只有一个显式失败的替身面**：同样的公开面，`Create` 返回 `null` + 「当前编译面是替身」原因，
  其余是空操作；绝不试图在替身环境里「假装建出 UI」，也不静默成功。
- **纯逻辑（`PMUnityBattleControlMath`）放在 `#if` 之外**，因此三个门都会类型检查它。
- **替身面的验证强度诚实口径**：`PMClientCheck`/`PMUnityGlueCheck` 通过只证明「以替身面能被编译」，
  它**不**证明 uGUI 实现正确；uGUI 实现的编译证据来自 `PMR4UnityCheck`（真实 DLL）。

### 7.1 `PMR4UnityCheck.csproj` 的改动（+31 行，纯新增）

```xml
<UnityUiAssembly Condition="'$(UnityUiAssembly)' == ''">$(MSBuildThisFileDirectory)..\..\Client\Library\ScriptAssemblies\UnityEngine.UI.dll</UnityUiAssembly>
...
<Reference Include="UnityEngine.UIModule">            <!-- Canvas / RenderMode -->
  <HintPath>$(UnityManagedDir)\UnityEngine\UnityEngine.UIModule.dll</HintPath>
  <Private>false</Private>
</Reference>
<Reference Include="UnityEngine.TextRenderingModule"> <!-- Text / Font -->
  <HintPath>$(UnityManagedDir)\UnityEngine\UnityEngine.TextRenderingModule.dll</HintPath>
  <Private>false</Private>
</Reference>
<Reference Include="UnityEngine.UI">                  <!-- Image/CanvasScaler/GraphicRaycaster/EventSystems -->
  <HintPath>$(UnityUiAssembly)</HintPath>
  <Private>false</Private>
</Reference>
```

为什么是**工程 Library**而不是引擎目录：`com.unity.ugui`（`UnityEngine.UI.dll`，其中含 `UnityEngine.EventSystems`）
是**包**，不在 `Editor/Data/Managed/UnityEngine` 下；用户工程已导入该包后，真实编译产物在
`Client/Library/ScriptAssemblies/UnityEngine.UI.dll`（本机实测存在，258,560 字节）。本工程只**读**它。

---

## 8. 宿主接线（**本批未改 Host**，主侧 batch 后按此接）

冻结入口**已经落地**（输入组）：`PMClientSessionHost.TrySetUiMove`（L822）、
`TryQueueUiAttack`（L879）、`TryGetUiCombatSnapshot`（L931，`PMUiCombatSnapshot` 定义于 L204）。
两者签名与本文件的委托**完全一致**，因此可以**方法组直接绑定**。

### 8.1 需要主侧加的代码（建议放在 `PMClientSessionHost` 内）

```csharp
private static PMUnityBattleControls _ui;

// ① 快照适配器（PMUiCombatSnapshot 的字段是 readonly，因此逐字段赋值，不能用对象初始化器）
private static bool QueryUiStatus(out PMUnityBattleUiStatus status)
{
    PMUiCombatSnapshot snapshot;
    if (!TryGetUiCombatSnapshot(out snapshot))
    {
        status = PMUnityBattleUiStatus.Offline();
        return false;
    }

    status.Hp = snapshot.Hp;
    status.MaxHp = snapshot.MaxHp;
    status.Mana = snapshot.Mana;
    status.SuperEnergy = snapshot.SuperEnergy;
    status.Dead = snapshot.Dead;
    status.MatchEnded = snapshot.MatchEnded;
    status.NormalReady = snapshot.NormalReady;
    status.SuperReady = snapshot.SuperReady;
    status.LastReject = snapshot.LastReject;
    return true;
}
```

### 8.2 五个接线点（行号为本报告写作时的实测值，后续会漂移，按方法名定位）

| # | 位置 | 现在做什么 | 需要加什么 |
|---|---|---|---|
| 1 | `Enter(...)`，紧接 `session.Hud = PMUnityCombatHud.Create(...)`（**L1399**）之后 | 建只读 HUD，失败即整局失败 | `string uiError; _ui = PMUnityBattleControls.Create(offer.MatchId, out uiError); if (_ui == null) { ReleaseSession(session); HYLDDebug.LogError("[PMClientSessionHost] 局内操控 UI 创建失败：" + uiError + "（不回旧链）"); return; } _ui.Bind(TrySetUiMove, TryQueueUiAttack, QueryUiStatus);`（**失败即整局失败**，与 HUD 同纪律：静默「没有 UI」会把「没显示」误读成「没数据」） |
| 2 | `Enter` 前置清理 `ReleaseRetainedHud();`（**L1124**）之后 | 清上一局保留 HUD | `if (_ui != null) { _ui.Dispose(); _ui = null; }` |
| 3 | `PumpActive()` 中三处 `UpdateCombatHud(session)`（**L1588 / L1646 / L1689**） | 推 HUD | 在每个 `UpdateCombatHud(session);` 之后加 `if (_ui != null) { _ui.UpdateStatus(); }`（幂等，重复调用无害） |
| 4 | `Stop()`：`ReleaseRetainedHud(); ClearLastCombatResult();`（**L1472**）之后 | 释放保留 HUD 与结果快照 | `if (_ui != null) { _ui.Dispose(); _ui = null; }`（**操控 UI 不保留**） |
| 5 | 终局退场路径（**L3538** 附近的 `ReleaseRetainedHud(); _retainedHud = session.Hud;`）与 `ReleaseSession`（**L3781**） | 摘出/释放会话 HUD | 在摘出 HUD 的同一处 `if (_ui != null) { _ui.Dispose(); _ui = null; }`；`ReleaseSession` 的会话级清理里也放一份（幂等） |

补充说明：
- 接线后，`Tools/PMBattleContentSceneFactsCheck` 的 J4 断言
  「宿主源码里暂不出现 `PMUnityBattleControls`」会**自然失败**——那正是模块作者留下的**提醒**：
  该断言需要与接线一起更新（改成断言存在绑定与 Dispose 即可）。
- UI 的 `UpdateStatus()` 自身不依赖 Unity 回调时序；若主侧更愿意让 Unity 驱动，
  本类已实现 `Update()` 安全网（但**推荐**显式调用，顺序确定）。

---

## 9. 测试与结果

### 9.1 本次实际执行的命令与结果（全部本机 CLI，未启动 Unity / 未启动服务）

| # | 命令 | 结果 |
|---|---|---|
| 1 | `dotnet build Tools/PMR4UnityCheck -c Release` | **0 错误**，3 警告（既有 CS2002「同一源文件被包含两次」，与本次改动无关）；DLL 内含 `PMUnityBattleControlsPointerRelay` ⇒ **完整实现被编入** |
| 2 | `dotnet build Tools/PMClientCheck -c Release` | **0 警告 0 错误**（替身面） |
| 3 | `dotnet build Tools/PMUnityGlueCheck -c Release` | **0 错误**（替身面，3 个既有警告） |
| 4 | `dotnet build Tools/PMBattleContentSceneFactsCheck -c Release` | **0 警告 0 错误** |
| 5 | `dotnet Tools/PMBattleContentSceneFactsCheck/bin/Release/net8.0/PMBattleContentSceneFactsCheck.dll` | **共 373 项，失败 0 项，退出码 0** |

基线对照：改动前 SceneFacts 为「238 项 / 失败 0」；本次 J 段新增 **135 项**断言。
真实/替身分支的选择用 DLL 内容反查确认（真实门 DLL 有 `PointerRelay`，两个替身门 DLL 没有）。

### 9.2 J 段（T-PLAY4）断言清单（135 项，摘要）

- **J1 旧 prefab 只读事实**：根名/inactive；16 条关键节点路径；`FireSuper` inactive；
  8 条旧脚本 guid 命中（EasyJoystick×3 / EasyButton / TouchLogic / EasyTouch / GameUITeamGemLogic / HYLDHeropropertyUI）；
  `Button` 的 `backStart` 持久监听与 `m_TargetGraphic`；4 张 Sprite 的路径→guid→源图三者一致；
  `zoneRadius=100` / `deadZone=20` / `deadZone×5==zoneRadius` / `joyAnchor 7 与 9`；
  `CanvasScaler` 的 `m_UiScaleMode=0`、`scaleFactor=1`、`referenceResolution.x=800`。
- **J1 负例（可执行）**：抹掉 Sprite 引用后断言必须失败；根改成 active 后断言必须失败。
- **J2 源码零激活/零权威**：24 个必需标记存在；25 个禁用标记在**去注释源码**里命中 0 处；
  变异注入后 25/25 全部命中；`DS 拒绝` 早于 `Resources.Load`；`Resources.Load` 早于 `AddComponent<自己>`；
  `Canvas` 早于指针转发器；射线命中控制（装饰件/文本/填充均 `raycastTarget=false`）；
  `Dispose` 区域顺序（先清摇杆 → 摘委托 → 销毁对象）与幂等；只收回本局专属事件系统。
- **J3 摇杆纯逻辑反例（可执行镜像）**：down 去重、非激活指针 drag/up 忽略、移动推送去重、
  **Y 不取反**（含「取反口径会被抓住」的负例）、松手清零只推一次、攻击松手提交一次且重复 up 不重复提交、
  空方向抑制、死区 `|local|≤20 ⇒ (0,0)`、超半径钳到 +1、不可用时 down 被忽略、NaN 拒绝、
  `Cancel` 两路（移动清零 / 攻击不提交）、未按住 Cancel 幂等、Dispose 后事件全部无效；
  以及四个常量与源码字面值的交叉核对（0.05 / 0.0005 / 0.2 / 100）。
- **J4 交付面**：源码 BOM + CRLF、meta 无 BOM + LF、meta guid 在 `Client/Assets` 内唯一、
  旧 prefab guid 与登记一致、`PMR4UnityCheck` 真实 `UnityEngine.UI.dll` + `UIModule` + `TextRenderingModule` + `UNITY_EDITOR`、
  真实 `UnityEngine.UI.dll` 存在于工程 Library、宿主本批未被改动。
- **镜像口径（必须写清）**：J3 是**规则镜像**（在 Program.cs 里按同一张语义表独立实现的可执行模型），
  不是「跑同一个实现」——该工具只编 `Program.cs`，无法引用 UI 源码。常量一致性由 J3 的源码常量交叉核对守住，
  结构一致性由 J2 的标记断言守住；**等价性不构成机器证明**，这是本工具能做到的上限。

---

## 10. 已知边界与**未验证**项（不许当成已完成）

1. **未实机**：本轮没有启动 Unity、没有进 Play、没有打包 Player。UI 的真实渲染/触摸命中/EventSystem 交互
   仍属 **T-PLAY5 PENDING_USER**。代码编译通过 ≠ 实机通过。
2. **美术是近似复用，不是旧 UI 还原**：
   - 摇杆底盘/手柄来自 `FullBG`/`FullPower`（1030×1004 圆盘），旧摇杆真实外观是 `joystick.png`/`joystick2.png`/
     `RadialJoy_Dead.png`（**Texture2D，非 uGUI Sprite**，本文件不可引用旧类型去取）；
   - HP/Mana 条：旧 prefab 的 `RedValue`/`bluebgImage` 本来就是**纯色 Image**（无 Sprite），
     因此新 UI 用「旧几何 + 旧素材边框 + 纯色填充」；
   - 蓝条（Mana）在旧 prefab 里没有对应行，复用本队行几何并向下平移一行（派生布局，已在源码注释登记）；
   - 摇杆中心距屏幕角的 40px 边距、手柄直径比例 0.45、宝石图标 52×58 都是本实现的选择（旧值不含这些量）。
3. **字体**：提示行/数值文本用 `Resources.GetBuiltinResource<Font>("Arial.ttf")`；取不到时**只少文字**
   （`FontAvailable = false` 可观测），摇杆与血条不受影响 —— 这是刻意选择（不因字体缺失整局失败）。
4. **缩放**：CanvasScaler 直接copy旧 prefab 的 `ConstantPixelSize` + `scaleFactor 1`（保持与旧 UI 同源），
   因此与旧 UI 一样是按 ~1920×1080 调的；极小分辨率下被 copy 的量表可能部分出屏（旧 UI 同样如此，未新增缩放策略）。
5. **只做「本人」状态**：快照只有本人 HP/Mana/Energy，故只建本队 HP/Mana/大招能量；
   旧 prefab 的对队行（`GemEnemyTeamUI`）本批不重建（未伪造对队数值）。
6. **EventSystem 归属**：既有（HYLDStart 大厅）EventSystem 可用时**借用**；确实没有才建本局专属，
   `Dispose` 时只收回自己创建的那一个（绝不销毁别人的）。
7. **大招可用性完全依赖快照 `SuperReady`**：UI 不判断英雄形态；抛物线/无子弹型大招在 DS/planner 侧被拒，
   UI 侧表现为「未满图标 + 按下无效」，不会扣资源、也不会伪装成功。
8. **未运行的门**：本轮**没有**运行 `PMLegacyRetirementTest` 与各网络回归（`PMR3RuntimeTest` /
   `PMR4NetworkTest` / `PMR5NetworkTest` / `PMR6NetworkTest` 等）。理由：本批只新增一个**客户端表现层**文件，
   且它的编译面只被 `PMR4UnityCheck` / `PMClientCheck` / `PMUnityGlueCheck` 收录（不编进 Server/Lobby 与网络测试工程），
   因此对网络回归无影响；`PMLegacyRetirementTest` 的 G8 资产冻结冲突是**用户工作区既有状态**
   （主计划已登记为独立 BLOCKED），与本文件无因果关系，故未用旧锚/未回退资源去凑绿灯。
   → 若主侧要求完整回归，应在接线后按 `net-runtime-acceptance.md` 统一跑一遍。
9. **接线后才算完成**：本文件**没有**修改 `PMClientSessionHost.cs`；在 §8 的五个点接上之前，
   实机不会出现本 UI（这是冻结文字要求的「UI 组不改 Host，由主侧接线」）。

---

## 11. 剩余问题 / 待主侧动作

| 项 | 说明 |
|---|---|
| **① 接入会话宿主（必做）** | 按 §8：新建/绑定/逐帧刷新/Stop/终局/ReleaseSession 六处；接线后同步更新 SceneFacts J4 的「宿主暂未引用」断言 |
| ② 屏幕缩放策略是否跟随旧 `ConstantPixelSize` | 本批按「只读复用旧布局」选择了保持同源；若实机觉得摇杆过小，需要主侧/用户决定是否改 `ScaleWithScreenSize`（**会偏离旧值**，属选型变更） |
| ③ 摇杆美术是否要更接近旧观感 | 若要，必须用户同意「从 Texture2D 运行期 `Sprite.Create`」或另出 Sprite 资产（本批刻意不做，以免伪造） |
| ④ 对队血条/技能栏/头像 | 快照不含对队数值，且旧 prefab 的对应节点带旧脚本；本批只做本人状态，完整旧 HUD 属后续 |
| ⑤ 实机验收 | **T-PLAY5 PENDING_USER**：双端同版本入局、两摇杆独立方向、攻击资源/结算/终局退场、第二局、断线与死亡后摇杆清理 |

---

## 12. 未触碰清单（边界证明）

- 未修改：`Client/Assets/Scripts/Server/Boot/PMClientSessionHost.cs`、`PMUnityMoverInput.cs`、
  任何 `.prefab` / `.scene` / `.asset` / `.unity` / 任何旧脚本 / 任何烘焙产物。
- 未创建：除 `PMUnityBattleControls.cs` 与其 `.meta`、本报告之外的任何文件。
- 未执行：`Instantiate` 旧 prefab、`LoadScene`、服务/DS/Lobby 启动、Unity 启动、SVN/git 写操作、递归委派。
- 未改：`PMR4UnityCheck` 除「新增 3 个引用 + 1 个属性」（+31 行纯新增）之外的内容；
  `PMBattleContentSceneFactsCheck` 除新增 J 段（含其常量/模型/镜像）之外的内容。
