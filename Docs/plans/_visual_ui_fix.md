# 首次双客户端画面修复：匹配面板 UI 覆盖（T-VIS1）

- 委派类型：comprehensive（允许写入仅三个文件）。
- 实际改动文件：
  - `Client/Assets/Scripts/Server/Panel/UIMatchingPanel.cs`（功能修复 + 引擎无关纯策略）
  - `Tools/PMLegacyRetirementTest/Program.cs`（新增 G9 静态门 + N7a..N7d 反例自证）
  - 本报告 `Docs/plans/_visual_ui_fix.md`
- 本轮**未**启动 Unity / 服务端 / 打包，**未**修改任何场景与资产，**未**提交、**未**暂存、**未**递归委派。
- 状态唯一源仍是 `Docs/plans/net-architecture-migration.md`（其末尾「首次双客户端画面修复开工」给出 T-VIS1..T-VIS5）。

---

## 1. 必读文档（先读完再搜业务，按委派顺序）

| # | 文档 | 读后锁定的口径 |
|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 工作副本是 Unity 2019 工程而非 UE 工程；入口文档与文档路由；实机集中后置。 |
| 2 | `Client/Assets/AGENTS.md` | §2 文档路由、§7 门禁清单（`PMClientCheck` / `PMLegacyRetirementTest`）、§6 中文源码 UTF-8 BOM+CRLF；§3「`UIMatchingPanel` 只接受 `PMDS1:` 通知，解析后进 `PMClientSessionHost.Enter`；非 PMDS1 不再加载旧战场」。 |
| 3 | `Server/AGENTS.md` | 局内权威在 Unity 打包的 HyldDS，Lobby 只编排/回收 ⇒ 本任务不涉及服务端改动。 |
| 4 | `Docs/plans/net-architecture-migration.md`（末尾「首次双客户端画面修复开工」） | T-VIS1 通过标准：入局 Canvas 不遮地图、宿主继续 Pump、退场/失败恢复原状态、匹配可再开；负例要防「只隐藏按钮/丢 UI 栈」；冻结安全：不改源场景/正式产物、不调旧 `ClearSence`、不启 Unity。 |
| 5 | `Docs/plans/net-r4c-content-contract.md` | C3「新相机控制自己对象并退局还原，不让旧主相机遮住；不能改旧场景永久设置」⇒ 与「临时改、退局还原」同一纪律，且失败一律 fail-closed 不回退。 |
| 6 | `Docs/plans/net-r6-combat-contract.md` | C 宿主接线冻结：「正常释放 session 与场景、回到原大厅（**原场景一直存在**）；终局 HUD 可保留至新入局/显式 Stop」⇒ 入局**不切场景**，所以大厅 Canvas 的可见性必须由本面板自己还回来。 |
| 7 | `Docs/plans/net-legacy-retirement-contract.md` | §B：`UIMatchingPanel` 只接受 PMDS1、非 PMDS1 显式报错；旧 UI 栈（`StartUIManger`）属保留的旧 UI 框架，不得改。 |
| 8 | `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md` | Pi 的 bash 工具一律按 Bash 语法执行；Windows 路径按 Windows 形式传入；本轮 `python` / `dotnet` 命令均以此为准。 |

---

## 2. 现状核对（冻结证据 × 实读源码/场景）

| 冻结证据 | 本次核对结果 |
|---|---|
| 截图 HUD HP960/960、Mana90、就绪；关掉 HYLDStart 根 Canvas 后能看到正式地图与 2 个角色 | 说明已真实入局；遮住画面的东西是 **UI Canvas**，不是「网络没进去」。 |
| `UIMatchingPanel` 收到 PMDS1 后仅隐藏 `ExitMathcing` 子按钮 | 实读确认（改动前 56–59 行）：`if (PMClientSessionHost.IsActive && ExitMathcing != null) ExitMathcing.SetActive(false);`——全屏匹配 Canvas 仍渲染，且该按钮**被永久隐藏**，第二局再也出不来「退出匹配」。 |
| `UIMatchingPanel` 位于 Canvas 的 workstation/recycle UI 树 | 实读 `Client/Assets/Scenes/HYLDStart.unity`：`UIMatchingPanel(GameObject 7419521581181128566)` → 父 `recyclePool(4485436064065661865)` → 父 `Canvas(7393250654863157133)`，其 Canvas 组件为 `7393250654863157120`，`m_Enabled: 1`。**该场景只有这一个 Canvas 组件（223）**，所以「所属 Canvas」＝用户实测关掉的那个根 Canvas。 |
| `ExitMathcing` 是子按钮 | `ExitMathcing` 序列化引用 `1823144908`（`m_Name: btnExitMatching`），其 RectTransform `1823144909` 正是 UIMatchingPanel 的 `m_Children` 之一；点击由 `UIInvatingFriendPanel.cs:36` 绑定（`btnExitMatching.onClick.AddListener(ExitMathching)`）。 |
| 旧 UI 栈管理 | `StartUIManger.cs`：`Open` → `recycleDic[panel].Open()` 成功才 `_panelStack.Push(panel)`（73–77）；`Close` → `Pop` 后立刻 `recycleDic[_panelStack.Peek()].OnRecovery()`（83–91）。`UIbasePanel.Open/Close` 在 `UIbasePanel.cs:104/115`。 |
| 新 `PMClientSessionHost` 不切旧 HYLDGame 场景、成功终局保留 HUD 回原大厅 | 实读确认：正常退场走 `EndSessionNormally`（本次读取时 `PMClientSessionHost.cs:2962`，该文件正被并行工作改动，行号以当时工作副本为准）→ `_active = null` ⇒ `IsActive`（530）变 false；终局后有界宽限 `TerminalExitGraceMs = ResultGraceMs + 2000`（2629）。故障路径 `Fail`（3134）置 `Faulted` ⇒ `IsActive` 同样变 false；显式 `Stop`（999）置 `_active = null`。 |

**结论**：这三个信号（终局正常退场 / 故障 / 显式 Stop）在宿主公开只读面上是同一条判据「`IsActive == false`」，因此面板可以用**一条**策略覆盖三种结束路径。

---

## 3. 实现（`UIMatchingPanel.cs`）

### 3.1 引擎无关纯策略（唯一决策源）

```csharp
internal enum PMMatchingPanelRenderAction { None, Suppress, Restore }

internal static class PMMatchingPanelRenderPolicy
{
    // suppressed=false, hostActive=true  → Suppress
    // suppressed=true,  hostActive=false → Restore
    // 其它                                → None
    public static PMMatchingPanelRenderAction Decide(bool hostActive, bool suppressed);
    public static bool SnapshotMustBeRecorded(bool suppressed);   // 原值只记一次（重复入局不得覆盖）
    public static bool RestoreEnabledValue(bool recordedEnabled); // 按记录回放，绝不用字面量 true
}
```

三条规则都可被静态核对（G9），也是 N7c/N7d 两条回退注入的靶点。

### 3.2 生命周期

- **入局成功当帧**（`OnResponse` 里 `PMClientSessionHost.Enter(offer)` 之后）与**每帧 `Update`** 都调同一个 `PumpRenderLifecycle()`：
  - 作用：当帧抑制可以避免匹配界面在刚加载出来的正式地图上多闪一帧；每帧轮询负责发现「会话已结束」。
- **抑制**：`canvas.enabled = false`，只改**所属 Canvas 组件**。不改任何 `activeSelf`、不停用面板 GameObject、不动旧 UI 栈、不新建全局单例 ⇒ 本面板的 `Update/FixedUpdate` 与宿主（`PMClientSessionHostDriver`）的事件更新继续执行。
- **恢复**：`canvas.enabled = PMMatchingPanelRenderPolicy.RestoreEnabledValue(_owningCanvasWasEnabled)`——**回放原始值**（原值可能是 `false`，例如原本就被别的流程关过）。
- **兜底**：`OnDisable` / `OnDestroy` 也恢复。原因：面板一旦被停用（旧栈 `Close`、场景切换、宿主失联收尾）`Update` 即停止，若不在这里还，所属 Canvas 会永久停在地图可见状态（大厅 UI 整块消失且无法自愈）。
- **退出匹配按钮**：不再隐藏 `ExitMathcing`（字段保留，以不破坏场景序列化引用）。恢复后按钮本来就是可点状态，第二局 `Open(nameof(MVC.UIMatchingPanel))` 也自然重新显示它——这直接消灭「第二局没有退出按钮」的根因。
- **异常与对象销毁**：`ResolveOwningCanvas()` 用显式父链遍历（不依赖 `GetComponentInParent<T>(bool includeInactive)` 在 Unity 2019.4 的重载可用性，也不受父物体激活状态影响）；缓存引用被销毁（Unity 伪 null）时自动重解析；读写都 `try/catch`，失败**不伪造**已抑制状态（解析不到时只告警一次且保持可重试）。

未改动：PMDS1 识别/解码失败回退禁令、旧链成功通知显式报错、`Requests` 注册与 `FixedUpdate` 自转逻辑。

---

## 4. 门禁、构建与运行结果

### 4.1 新增门禁（只新增，未放宽任何原有断言）

`Tools/PMLegacyRetirementTest/Program.cs`：

- **G9（6 条）**：`owns-canvas-render-suppressed`、`preserves-original-canvas-enabled`、`no-gameobject-or-button-deactivation`、`restores-on-host-inactive`、`render-policy-single-source`、`no-new-global-singleton`；缺输入固定 FAIL（`G9.uimatching-present`），禁止空扫描绿。
- **N7a..N7d（负例自证，%TEMP% 沙盒注入，真实文件只读）**：
  - `N7a` 退役前旧实现夹具（只 `ExitMathcing.SetActive(false)`、无 Canvas 抑制、无生命周期恢复）→ G9 三项必须 FAIL（**这就是任务书要求的「旧实现会失败的反例」**）。
  - `N7b` 真实 `UIMatchingPanel.cs` 沙盒复跑 G9 必须全绿（证明 G9 不是恒 FAIL 的假门）。
  - `N7c` 在真实实现上注入「记录处写死 `true`」必须被检出。
  - `N7d` 在真实实现上注入「恢复处写死 `enabled = true`」必须被检出。
  - N7c/N7d 若注入串未命中真实源码，一律判 FAIL（不允许退化成「没检测到问题 = 绿」）。
- G1..G8 全部断言原样保留。报告模板只增补了 G9 的能力边界说明与负例覆盖行。

### 4.2 构建（build 0 之后才 run）

| 门禁 | 命令 | 结果 |
|---|---|---|
| 静态门工具 | `dotnet build Tools/PMLegacyRetirementTest/PMLegacyRetirementTest.csproj -c Release` | **0 警告 0 错误** |
| 客户端玩法层真编译 | `dotnet build Tools/PMClientCheck/PMClientCheck.csproj -c Release` | **0 警告 0 错误**（`Client/Assets/Scripts/Server/**` 全量真编，含本文件；RPC/属性编织 13/13、`--check` 通过） |
| 权威写入门 | `python Tools/check_client_authority_writes.py` | PASS（越界写入 **0**） |
| 括号门 | `python Tools/check_cs_braces.py <本文件>` | PASS（`{}` 37/37、`()` 66/66） |

> 说明：`PMR4UnityCheck`（真实 Unity2019 DLL）只编两个宿主 + HUD，**不含** `Server/Panel/**`，因此本文件的机械编译证据是 `PMClientCheck`；真实 Unity 编译仍是 T-VIS5 的用户侧动作。

### 4.3 运行结果（`--self-test`）

```
===== 静态门禁结果：通过 33 / 失败 1 =====
===== 负例自测：通过 10 / 失败 0 =====
总体结论：FAIL
```

- G9 六条全 OK；N1..N6 + **N7a..N7d 全 OK（10/0）**。
- 唯一 FAIL 是 `G8.frozen-anchors-unchanged`：
  - 不符者：`Client/Assets/Resources/PMNet/BattleMapV1.prefab`、`Client/ProjectSettings/EditorBuildSettings.asset` 的 SHA256 与冻结锚不同。
  - **非本次引入**：两文件在工作副本里早已是 `M`（`git diff --stat`：prefab 6071 insertions/6071 deletions，EditorBuildSettings 4 行），mtime `12:27:51` / `12:32:18`，而本次编辑发生在 `12:53`–`12:54`；本次从未读写这两个文件，也**不会**为让它变绿去改锚或改资产（任务要求「不为了绿色削弱原检测」）。
  - 结论：该 FAIL 属并行工作留下的既存状态差异，需由其归属方（重烘正式内容/改 BuildSettings 的动作）统一更新锚值或复原资产；与本轮 T-VIS1 范围正交。

---

## 5. 已知阻碍：旧 UI 栈的「恰当关闭」无法在本面板内安全完成（如实上报，未绕）

> **【本轮 T-VIS1b 已解除本条阻碍】** `StartUIManger` 现已新增显式栈安全 API `CloseIfTop`
> （栈顶就是本面板 + 栈里至少两层 + 注册表条目可用，三重校验全部通过才关闭弹栈），
> `UIMatchingPanel` 也已在「会话结束且本局确实入过局」时改走它。
> 本节以下内容是**上一轮的原始如实上报**，保留以便对照；最新证据见文末「T-VIS1b」章。

需求里的「恰当关闭匹配面板」在现有旧 UI 框架下**无法由本面板安全完成**，证据如下（均为实读）：

1. `StartUIManger.Close()`（`StartUIManger.cs:83-91`）弹的是**栈顶**，并不保证就是 `UIMatchingPanel`（其它面板可能在其后被 Push）；且 `Pop` 之后立刻 `_panelStack.Peek()`——**栈里只剩一项时会抛 `InvalidOperationException`**，异常点还在「已经 Pop 并关掉一个面板」之后，失败后果不可控。
2. 面板自身 `UIbasePanel.Close(false)`（`UIbasePanel.cs:115`）不走 `_panelStack`：它能正确「OnHide + SetActive(false) + 回 recyclePool」，但栈里那条 `UIMatchingPanel` 仍留着；下一次 `UIInvatingFriendPanel` 收到 `AddMatchingPlayer` 会 `Open(UIMatchingPanel)`（108/122 行），因为面板已被关闭所以 `Open()` 返回 true 并**再 Push 一条**⇒ 栈里按局数累积重复项，之后 `Close()` 弹到的是被我们关掉的那条（`OnRecovery` 落在一个已关闭面板上）。这是**真实的栈管理损坏**，不是理论顾虑。

因此本轮的选择是：**不做任何栈/面板关闭**（不改旧 UI 框架、不新增全局单例、不伪造「已归还」），只把可见性恢复成入局前的原状（匹配面板可见 + 「退出匹配」按钮可用）。可观察后果与走向：

- 战斗结束后回到大厅时看到的是「匹配中/已找到玩家 + 退出匹配」这一入局前状态；玩家点「退出匹配」仍走既有链路（`UIInvatingFriendPanel.cs:642` → `RemoveMatchingPlayer` → 服务端回 `"-1"` → `UIBaseManger.Close()` 正常弹栈回主面板）。
- 第二局 `Open(nameof(MVC.UIMatchingPanel))` 正常重新显示退出按钮（按钮不再被隐藏过）。
- 若产品要求「终局后由代码自动关闭匹配面板」，需要旧 UI 框架提供一个栈安全的显式 API（例如 `CloseIfTop(panel)`）；该文件不在本次允许写入范围，本轮不擅自改，也不声称已完成。

---

## 6. 实机待测（T-VIS5，PENDING_USER）

| # | 步骤 | 期望 |
|---|---|---|
| 1 | 同版本重建 Lobby/DS/Client 后，客户端 A 与 B 各自开始匹配 | 匹配中面板正常显示，「退出匹配」按钮可点 |
| 2 | A/B 各自收到 `PMDS1` 入局成功 | 匹配面板整块不再遮住正式地图与角色；日志出现 `[TVIS1] 入局成功：已只停用所属 Canvas 的渲染（原 enabled=true...）`；HUD（IMGUI）仍显示 |
| 3 | 局内移动/普攻/受击 | 输入与表现不受影响（Canvas 停用只影响渲染，MonoBehaviour 与宿主 Pump 继续） |
| 4 | 打完一局（正常终局） | 终局 HUD 保留；会话结束后日志出现 `[TVIS1] 已恢复所属 Canvas 渲染（新链会话已结束…；enabled=true…）`，并回到入局前画面 |
| 5 | 主动制造故障（关 DS / 断线，未收到可信终局） | 会话判失败后同样恢复 Canvas 渲染（不回退旧链） |
| 6 | 显式 `PMClientSessionHost.Stop()`（或断线收尾路径） | 同上恢复，且面板状态不被破坏 |
| 7 | **第二局**：再做一次匹配 → 入局 | 「退出匹配」按钮**再次可见可点**（本轮核心验收点）；再打一局后仍能恢复 |
| 8 | 关掉/销毁面板的边界（例如离场切场景） | 大厅 Canvas 不会停留在隐藏态（`OnDisable`/`OnDestroy` 兜底；若整块 Canvas 随场景销毁，日志给出「已找不到所属 Canvas」告警而不抛异常） |

> 口径提醒：**Canvas 不可见 ≠ 业务正常**。本轮只把「谁负责可见性、何时还回去」钉死在静态门 + 纯策略上；运行期是否真的不再遮挡、退出后是否真的恢复，一律以 T-VIS5 实机为准，本轮不冒称已通过。

---

## 7. 写入边界与声明

- 只写了三个文件：`Client/Assets/Scripts/Server/Panel/UIMatchingPanel.cs`、`Tools/PMLegacyRetirementTest/Program.cs`、本报告；未新增/删除其它文件，未改任何 `.unity` / `.prefab` / `.json` 资产，未新增客户端脚本（因此不需要新 `.meta`）。
- 中文源码保持 **UTF-8 BOM + CRLF**（改后已核对：本文件 326 行全 CRLF、无裸 LF；`Program.cs` 2561 CRLF、无裸 LF）。
- 未修改网络代码、旧 UI 框架、DS/协议/manifest；未启动 Unity/服务端/打包；未 `git add`/提交/暂存/回退。
- 未递归委派；未削弱 G1..G8 任何原有断言（G9 与 N7 为纯新增）。

---

# T-VIS1b：匹配面板「栈安全退场」收口（本轮增补）

- 委派类型：comprehensive（写入边界 = 下方 4 个文件）。
- 实际改动文件：
  - `Client/Assets/Scripts/Server/Manger/Start/StartUIManger.cs`（**新增**显式栈安全 API `CloseIfTop`；既有 `Close()` 语义一个字都没改）
  - `Client/Assets/Scripts/Server/Panel/UIMatchingPanel.cs`（退场纯策略 + 一次性登记 + 只走新 API 的退场调用）
  - `Tools/PMLegacyRetirementTest/Program.cs`（新增 **G10** 静态门 + **N8a..N8g** 反例自证 + 报告模板 G10/N8 说明）
  - 本报告 `Docs/plans/_visual_ui_fix.md`
- 本轮**未**启动 Unity / 未启动服务端 / 未打包，**未**新增或修改任何 `.unity` / `.prefab` / `.json` 资产，**未**提交、**未**暂存、**未**递归委派。
- 本轮**未**放宽 G1..G9 / N1..N7 的任何断言（G10 与 N8 都是纯新增）。
- 上级文档本轮新增的 T-VIS1b 要求（`net-architecture-migration.md` §「T-VIS1/2 初版独立实现后的集成复核」）：
  「面板留在旧 UI 栈里，结束仍见『已找到玩家 2/2』，需新增 `StartUIManger` 显式 `CloseIfTop`，
  严格验证栈顶才关闭/回主界面，不能直接 `UIbasePanel.Close` 导致栈重复。」

---

## 1. 必读文档（按委派顺序读完后锁定的口径）

| # | 文档 | 读后锁定的口径 |
|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 工作副本是 Unity 2019 工程（不是 UE 工程）；文档路由；状态唯一源为主计划；不代提交、不主动 SVN。 |
| 2 | `Client/Assets/AGENTS.md` | §2 文档路由、§3「`UIMatchingPanel` 只接受 `PMDS1:` 通知」、§6「含中文源码 UTF-8 BOM + CRLF」、§7 门禁清单（`PMClientCheck` / `PMLegacyRetirementTest`）。 |
| 3 | `Server/AGENTS.md` | 局内权威在 Unity 打包的 HyldDS，Lobby 只编排/回收 ⇒ 本任务**不涉及服务端改动**。 |
| 4 | `Docs/plans/net-architecture-migration.md`（末尾「首次双客户端画面修复开工」+「T-VIS1/2 初版独立实现后的集成复核」） | T-VIS1 通过标准；新增 **T-VIS1b**：安全匹配栈退出、失败/重复局/单元素栈负例；G8 冻结钳是独立阻断，不得为绿灯改错值或回退用户资产。 |
| 5 | `Docs/plans/net-r6-combat-contract.md`（§C 宿主接线冻结） | 「正常释放 session 与场景、回到原大厅（原场景一直存在）；终局 HUD 可保留至新入局/显式 Stop」⇒ 入局**不切场景**，大厅 UI 栈必须由面板自己还回去。 |
| 6 | `Docs/plans/_visual_ui_fix.md`（上一轮 T-VIS1 报告） | §5 已如实上报「三件事实」：`Close()` 弹的是栈顶且单元素栈 `Peek` 会抛；`UIbasePanel.Close()` 不动栈会积压重复条目 ⇒ 上一轮**故意不关面板**。本轮就是解除这条阻碍。 |
| 7 | `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md` | Pi 的 `bash` 工具一律按 Bash 语法执行；Windows 路径按 Windows 形式传入；本轮 `python` / `dotnet` 命令以此为准。 |

---

## 2. 现状核对（冻结证据 × 本轮实读源码/场景）

| 项 | 本轮核对结果（均为实读，命令行只读） |
|---|---|
| `StartUIManger.Close()`（`StartUIManger.cs:83-91` 附近） | `Pop()` 之后立刻 `_panelStack.Peek()`：单元素栈会 `InvalidOperationException`；且弹的是**栈顶**，不保证是 `UIMatchingPanel`。**本轮未改它**（它被多处旧调用点共用，改语义超出「非重写 UI 系统」的范围）。 |
| `StartUIManger.Open(panel)` | `if (recycleDic[panel].Open()) _panelStack.Push(panel);` ⇒ 只在「关→开」跃迁时 push 一次（重复通知不会重复入栈），所以退场只要**正确弹掉一条**，重复局再 `Open` 就恰好又是一条。 |
| `UIbasePanel.Close(bool)` | `OnHide + SetActive(false) + 回 recyclePool`，**不动 `_panelStack`** ⇒ 上一轮 §5 认定的「脏栈条目」来源。 |
| `UIMatchingPanel` 的注册键 | `StartUIManger.StartInit()` 用 `Type.GetType("MVC." + view.name).Name` 建键，而场景里该 GameObject 的 `m_Name: UIMatchingPanel`（`HYLDStart.unity:97175`）与类型名相同 ⇒ 键 == GameObject 名 == `nameof(MVC.UIMatchingPanel)`。 |
| `StartInit()` 何时跑 | 唯一调用点 `Client/Assets/HYLD1.0/Scripts/OldScripts/UI/ripts/UI/HYLDStartUILogic.cs:86`（`isDo==false` 时调一次）⇒ 入局前 `recycleDic` 已含 `UIMatchingPanel`。 |
| `UIRoot.UIManger` 指向谁 | `UIRoot.Init(transform, uIManger)` 由 `UIBaseManger.OnInit` 调用；`HYLDStart.unity` 里根 Canvas 对象 `7393250654863157133` 的 `m_TagString: UIManger`（`:36550`）且其组件 `m_Script.guid = ac15603cdc1c5bf449e9b48c255233db`（= `StartUIManger.cs.meta`）⇒ `UIRoot.UIManger` 就是 `StartUIManger` 实例，`as StartUIManger` 可用且**不需要新增公共入口**。 |
| 运行期栈形态（正常局） | `OnInit` push `UIStartMainPanel` → `UIInvatingFriendPanel` → `UIInvatingFriendPanel.OnResponse`（AddMatchingPlayer / 其它）`Open(nameof(MVC.UIMatchingPanel))` push 一条 ⇒ 栈 = `[UIMatchingPanel, UIInvatingFriendPanel, UIStartMainPanel]`，**Count = 3**，栈顶就是匹配面板。 |
| 会话结束信号 | 正常退场 / 故障 / 显式 `Stop` 在宿主只读面上同为 `PMClientSessionHost.IsActive == false`（上一轮证据，本轮未改宿主）。 |

---

## 3. 实现

### 3.1 新 API：`StartUIManger.CloseIfTop(string panel)`（唯一新增入口）

四个条件**全部成立**才关闭并弹一条栈；任何一条不成立都 `return false` 且**不动任何栈状态**：

1. `panel` 非空，且 `recycleDic.TryGetValue(panel, out target)` 成功、`target != null`（Unity 伪 null / 已销毁按不可用）；
2. 注册条目确实是「当前面板」：`target.gameObject.name == panel`（本框架的注册口径就是 GameObject 名），访问 `gameObject` 抛异常也算不可用；
3. `_panelStack.Count >= 2`（弹出后必须还剩一层可 `OnRecovery`，**绝不 `Peek` 空栈**）；
4. `_panelStack.Peek() == panel`（**只关栈顶**，绝不误关别的面板）。

顺序保证（本轮新增 G10 专门盯这条）：`Close()` 与弹出后要 `OnRecovery` 的下一层**全部校验完**才 `_panelStack.Pop()`；
`target.Close()` 抛异常时栈还没被动过，因此不会出现「已经弹掉却没关成」的半成品状态；`next.OnRecovery()` 的异常被单独捕获（此时弹栈是**已经被校验通过的正当操作**，不会破坏栈的良构性）。

`Close()`（无参数弹栈顶）与 `UIbasePanel.Close()`（不动栈）**都没有被改**：前者被 `UIbasePanel.RegisterUIEvent` 的 `btnClose` 等多处调用点共用，
后者是面板基类语义；改它们属于重写旧 UI 框架，超出本轮「非重写 UI 系统」的边界。

### 3.2 面板侧（`UIMatchingPanel.cs`）

新增引擎无关纯策略 `PMMatchingPanelExitPolicy`（四条规则，与渲染策略同风格、同样是唯一决策源）：

| 规则 | 语义 |
|---|---|
| `ShouldEnterReturnToMain(canvasRestoredThisFrame)` | 只有**本帧确实按原值恢复了本面板压制过的 Canvas**（= 本局真的由本面板入过局）才登记退场。 |
| `ShouldAttemptReturnToMain(hostActive, pendingReturnToMain)` | `!hostActive && pendingReturnToMain`：会话仍在进行中一律不碰 UI 栈。 |
| `ShouldGiveUpReturnToMain(stackManagerAvailable)` | 拿不到可用 `StartUIManger` 时放弃登记（结构不可用，重试无意义），只告警一次。 |
| `ShouldKeepWaitingForReturnToMain(closed)` | 被 `CloseIfTop` 拒绝时保留登记、后续帧再试，但本帧不改栈。 |

生命周期接线（`PumpRenderLifecycle`，`Update` 与入局当帧都调它）：

```
hostActive = PMClientSessionHost.IsActive
Decide(hostActive, _renderSuppressed)
  Suppress → SuppressOwnCanvasRender()；return（不改栈）
  Restore  → canvasRestored = RestoreOwnCanvasRender(...)          ← ① 先还 Canvas 渲染
             ShouldEnterReturnToMain(canvasRestored) → _pendingReturnToMain = true
ShouldAttemptReturnToMain(hostActive, _pendingReturnToMain) → ReturnToMainMenuIfSafe()   ← ② 再退场
```

- `RestoreOwnCanvasRender` 现在返回 `bool`，**只在确实按记录恢复了渲染时为 true**：未抑制过 / Canvas 已随场景销毁 / 赋值抛异常都返回 false
  ⇒ 这些情况不会被误判成「本局入过局」，不会去动 UI 栈。`OnDisable` / `OnDestroy` 兜底调用**忽略返回值**，因此**没有任何退场副作用**。
- `ReturnToMainMenuIfSafe()`：`UIRoot.UIManger as LongZhiJie.StartUIManger`（只读既有公开静态、**不新建 manager**），
  然后 `stackManager.CloseIfTop(nameof(UIMatchingPanel))`。面板**不调用** `UIBaseManger.Close()`，也**不调用**面板自己的 `Close()`。
- 一次性登记：`_pendingReturnToMain` 在整个文件里只有**一处**赋值 `false`（在 `ClearReturnToMain()` 内），
  且 `ReturnToMainMenuIfSafe` 里**只有在「不处于被拒分支」之后**才会走到最后一次 `ClearReturnToMain()`
  ⇒ 被拒时不消费登记、关成功才消费，重复局不会继承上一轮的登记。

### 3.3 本轮确立、可静态核对的关键不变量

| 不变量 | 对应门禁 |
|---|---|
| 只关栈顶、且栈里至少两层（不误关别的面板 / 不 `Peek` 空栈） | `G10.exit-api-strict-top-and-depth` |
| 注册表条目（目标 + 弹出后要恢复的下一层）都必须可用 | `G10.exit-api-registry-validated` |
| 任何校验失败前不得改栈（`Pop` 正好一次且排在全部校验之后） | `G10.exit-api-no-mutation-before-validation` |
| 面板只走新 API，不裸 `Close()`、不拿「恢复退出按钮」充数 | `G10.panel-exit-uses-safe-api-not-button` |
| 先恢复 Canvas 渲染、再谈退场 | `G10.panel-restores-canvas-before-exit` |
| 会话进行中绝不尝试关闭；只有恢复成功才登记 | `G10.panel-exit-gated-by-host-inactive` |
| 登记只消费一次（关成功才清） | `G10.panel-exit-registration-one-shot` |
| `OnDisable`/`OnDestroy` 兜底不新建 manager、不动栈 | `G10.panel-teardown-touches-no-manager-or-stack` |
| 四条退场决策全部走同一纯策略 + 只读既有 `UIRoot.UIManger` | `G10.exit-policy-single-source` |

---

## 4. 门禁、构建与运行结果（build 0 之后才 run）

| 门禁 | 命令 | 结果 |
|---|---|---|
| 客户端玩法层真编译（含本轮两个客户端文件） | `dotnet build Tools/PMClientCheck/PMClientCheck.csproj -c Release` | **0 警告 0 错误**；RPC 编织 13/13、auto-property 13/13，`--check` 通过（52 个方法逐 RID 符号核对） |
| 静态门工具 | `dotnet build Tools/PMLegacyRetirementTest/PMLegacyRetirementTest.csproj -c Release` | **0 警告 0 错误**（本轮修掉一处自身缺陷：teardown 断言原来用非短路的 `&=`，缺 `OnDisable` 时会把 `null` 喂给 `Regex` ⇒ 改为短路并显式判 FAIL） |
| 旧链禁回归门 + 反例自测 | `dotnet Tools/PMLegacyRetirementTest/bin/Release/net8.0/PMLegacyRetirementTest.dll` | `静态门禁结果：通过 43 / 失败 1`；`负例自测：通过 17 / 失败 0`；`总体结论：FAIL`（唯一 FAIL = 既存 `G8.frozen-anchors-unchanged`，见 §8） |
| 权威写入门 | `python Tools/check_client_authority_writes.py` | PASS（越界写入 **0**；命中许可路径 9 处、已登记例外 13 处，与上一轮同口径） |
| 括号门 | `python Tools/check_cs_braces.py`（三个文件） | PASS：`UIMatchingPanel.cs` `{}`56/56 `()`99/99；`StartUIManger.cs` `{}`33/33 `()`85/85；`Program.cs` `{}`375/375 `()`1412/1412；均「深度全程非负且末尾归零」 |

> 机械编译证据仍只用 `PMClientCheck`：`PMR4UnityCheck` 只编两个宿主 + HUD，`PMUnityGlueCheck` 的 `Compile Include` 也不含 `Server/Panel/**` 与 `Server/Manger/**`（已实读 csproj 确认），
> 所以那两个门禁对本轮两个文件**不产生证据**，不拿来充数。真实 Unity 编译仍是 T-VIS5 的用户侧动作。

G10 逐条实测输出（本轮真实运行结果，非模板）：

```
[OK  ] G10.inputs-present  输入就位：.../UIMatchingPanel.cs + .../StartUIManger.cs
[OK  ] G10.exit-api-strict-top-and-depth  CloseIfTop=True 签名=True 栈里至少两层=True 栈顶必须等于目标面板=True
[OK  ] G10.exit-api-registry-validated  注册表校验目标面板=True 注册表校验下一层=True 空/已销毁条目判定数=2（>=2）
[OK  ] G10.exit-api-no-mutation-before-validation  Pop 次数=1（必须为 1）Pop 偏移=1911 在两层校验=798 / 栈顶校验=931 / 注册表下一层校验=1327 之后=True
[OK  ] G10.panel-exit-uses-safe-api-not-button  调用 CloseIfTop=True 无裸 Close()=True 退场路径不碰退出按钮=True
[OK  ] G10.panel-restores-canvas-before-exit  恢复 Canvas 偏移=518 退场调用偏移=1092 恢复在前=True
[OK  ] G10.panel-exit-gated-by-host-inactive  泵按 hostActive 传参=True 纯策略要求 !hostActive=True 恢复成功才登记退场=True
[OK  ] G10.panel-exit-registration-one-shot  清登记赋值次数=1（必须为 1）被拒先 return 的判定偏移=1088 最后一次清登记偏移=1579 关成功才清=True
[OK  ] G10.panel-teardown-touches-no-manager-or-stack  OnDisable/OnDestroy 只调 RestoreOwnCanvasRender，不碰 UIRoot/StartUIManger/CloseIfTop/Pop/new/退场登记=True
[OK  ] G10.exit-policy-single-source  四条纯策略规则都在用=True 四条规则都有定义=True 只读既有 UIRoot.UIManger=True
```

（静态门禁总数从上一轮的 33/1 变成 43/1：新增的 10 条全是 G10，G1..G9 一条都没改、仍是上一轮的判定逻辑。）

---

## 5. 反例自证（N8a..N8g，%TEMP% 沙盒注入；真实生产文件只读）

`N8a..N8g` 全部 `[OK]`（`负例自测：通过 17 / 失败 0`，其中 N1..N7 共 10 条沿用上一轮，一条未放宽）：

| 用例 | 注入/夹具 | 判定 |
|---|---|---|
| **N8a 旧只有恢复不关闭** | 夹具：上一轮状态（只按原值恢复 Canvas、无任何栈退场接线）+ 旧 `StartUIManger`（无 `CloseIfTop`，`Close()` 无脑弹栈顶） | `G10.exit-api-strict-top-and-depth`、`G10.panel-exit-uses-safe-api-not-button`、`G10.panel-restores-canvas-before-exit`、`G10.panel-exit-registration-one-shot` 同时 FAIL |
| **N8b 基线全绿** | 真实 `UIMatchingPanel.cs` + `StartUIManger.cs` 写进沙盒复跑 G10 | 全绿（证明 G10 不是恒 FAIL 的假门） |
| **N8c 错误栈顶** | 真实管理器里 `if (_panelStack.Peek() != panel)` → `if (false)` | 命中 + `exit-api-strict-top-and-depth` 与 `exit-api-no-mutation-before-validation` 均 FAIL |
| **N8d 单元素栈** | 真实管理器里 `if (_panelStack.Count < 2)` → `if (_panelStack.Count < 1)` | 命中 + `exit-api-strict-top-and-depth` FAIL |
| **N8e 按钮恢复（假退场）** | 真实面板里 `ReturnToMainMenuIfSafe();` → `ExitMathcing.SetActive(true);` | 命中 + `panel-restores-canvas-before-exit` FAIL |
| **N8f 重复局（不清登记）** | 真实面板里唯一一处 `_pendingReturnToMain = false;` → `_pendingReturnToMain = _pendingReturnToMain;` | 命中 + `panel-exit-registration-one-shot` FAIL |
| **N8g 先 Pop 再校验** | 真实管理器里栈顶校验行 → `_panelStack.Pop();` | 命中 + `exit-api-no-mutation-before-validation` FAIL（出现两次 `Pop`） |

注入纪律与 N7c/N7d 一致：**注入字符串没有命中真实源码就一律判 FAIL**（`注入命中=False` 也会红），不允许静默退化成「没检测到问题=绿」。

---

## 6. 关键补充证据：「已找到玩家 X/Y」到底属于谁（决定「回主菜单」是否真的成立）

本轮为这一条**另行解析了 `HYLDStart.unity` 的层级**（只读脚本，未改资产）：

| 事实 | 证据 |
|---|---|
| `UIMatchingPanel` 是 `recyclePool` 的直接子物体 | GameObject `7419521581181128566` / RectTransform `7419521581181128567`，`m_Father` = `recyclePool` 的 RectTransform `4485436064065661870` |
| 它的子物体 = `BG / Star / Text / PlayerCntText / btnExitMatching / AddAIButton` | 逐层遍历结果；`btnExitMatching` 确实在它下面（与上一轮 §2 的结论一致） |
| **`MatchingInfo`（「已找到玩家 X/Y」那行文字）就在 `UIMatchingPanel` 里面** | `UIInvatingFriendPanel` 组件（`:1873978795`）序列化 `MatchingInfo: {fileID: 7419521580779613612}` → 该 Text 组件属于 GameObject `7419521580779613601`（`m_Name: PlayerCntText`）；父链 `PlayerCntText -> UIMatchingPanel -> recyclePool -> Canvas` |
| 退场后剩下的就是大厅主菜单面板 | `UIInvatingFriendPanel` 父链 `UIInvatingFriendPanel -> recyclePool -> Canvas`（与匹配面板同级），它是「开始匹配 / 离开房间 / 好友列表 / 房间ID」那块主菜单 |

⇒ 两条结论：
1. 集成复核里「结束仍见『已找到玩家 2/2』」的**唯一正确修法就是真的关掉匹配面板**（该文字随面板 `SetActive(false)` 一起消失），
   「只恢复 Canvas / 只恢复退出按钮」都改不掉它；本轮实现正是前者。
2. 关掉一条栈后露出的 `UIInvatingFriendPanel` 就是主菜单，符合「回主菜单」，且**不需要**再弹第二条栈
   （`TryCloseIfTop` 只弹一条，也刻意只弹一条：多弹一条就会把主菜单面板一起关掉）。

---

## 7. 能力边界与诚实口径（本轮没有做的事）

- **G10 是源接线静态事实，不是运行期行为验证。** 与 G9 同口径：它只证明「代码里确实只有这条路径、且三重校验确实排在 `Pop` 之前」，
  检出力由 N8a..N8g 注入自证。真实运行期是否真的不再遮挡、是否真的回到主菜单，一律以 T-VIS5 实机为准。
- **本轮没有「纯策略执行」式测试。** 委派允许「静态 + 纯策略执行」，但本仓库的 `Tools/PMLegacyRetirementTest` 是**单文件静态分析器**
  （无 Roslyn、无 Unity、不加载任何客户端程序集），无法真正执行 C# 决策函数；而写入边界禁止新建测试工程/文件。
  因此本轮的可执行性保证是：① 四条退场规则写成**引擎无关** `internal static` 纯函数（不引 UnityEngine，未来可由任何执行宿主直接调用）；
  ② 本轮用静态接线 + 7 条注入反例证明判定不是空的。**不把静态绿冒充成运行期通过**。
- **没有改旧 UI 框架的两处已知缺陷**（都不在本轮写入意图内，且新路径不经过它们）：
  1. `StartUIManger.Close()`（无参数）在单元素栈上 `Pop()` 后 `Peek()` 会抛 `InvalidOperationException`；
  2. `UIbasePanel.Close()` 不动 `_panelStack`，仍会留下脏条目。
  这两条是**既有**结构问题，`Close()` 被 `btnClose` 等多处调用点共用，改语义等于重写旧 UI 框架；
  本轮只保证「匹配面板自动退场」这条路径**不经过**它们（面板侧不调 `UIBaseManger.Close()`、也不调自身 `Close()`）。
- **没有改服务端 / 协议 / 宿主 / manifest / 场景 / 资产**；没有改 `HYLDManger`、`UIRoot`、`UIBaseManger`。
- **没有新增任何 `.cs` 文件**（因此不存在新 `.meta` 需求）。
- `StartUIManger.cs` 原本**没有 BOM**（只有 CRLF）。本轮按 `Client/Assets/AGENTS.md` §6「含中文源码 UTF-8 BOM + CRLF」
  把它补成 BOM，因此该文件的 `git diff` 第 1 行会显示一个纯编码差异（**不是逻辑改动**）。

---

## 8. 已知阻断（G8 冻结资产钳，非本轮引入、本轮未处理）

`G10` 全绿，但完整门禁仍是 `总体结论：FAIL`，唯一 FAIL 与上一轮**完全相同**：

```
[FAIL] G8.frozen-anchors-unchanged  Client/Assets/Resources/PMNet/BattleMapV1.prefab SHA 不符
                                   Client/ProjectSettings/EditorBuildSettings.asset SHA 不符
```

- 这两个文件属于**并行工作（重烘正式内容 / 改 BuildSettings）**改写的用户资产，**不在本轮允许写入的 4 个文件内**；
- 本轮从未读写它们，也**不会**为了绿灯去改错值或 `git` 回退/覆盖用户资源（`net-architecture-migration.md` 已明确要求）；
- 需由其归属方统一更新修正值或复原资产。该阻断与本轮 T-VIS1b 正交，**不掩盖**。

---

## 9. T-VIS5 实机待测（本轮新增可观测点，全部 PENDING_USER）

| # | 步骤 | 期望日志 / 现象 |
|---|---|---|
| 1 | 双客户端各自匹配 → 收到 `PMDS1` 入局 | `[TVIS1] 入局成功：已只停用所属 Canvas 的渲染（原 enabled=true...）`；匹配界面不再遮挡地图与角色 |
| 2 | 打完一局（正常终局） | ① `[TVIS1] 已恢复所属 Canvas 渲染（新链会话已结束...）`；② **`[TVIS1b] 已按栈安全条件关闭匹配面板并回到主菜单`**；③ 屏幕上「已找到玩家 X/Y」与匹配底色**整块消失**，剩下主菜单（开始匹配/离开房间/好友列表） |
| 3 | 主动制造故障（关 DS / 断线，未收到可信终局） | 同上两条日志 + 同样回到主菜单（**不回退旧链**） |
| 4 | 显式 `PMClientSessionHost.Stop()` | 同上 |
| 5 | **第二局**：再匹配 → 入局 → 再打完 | 匹配面板正常再出一次（「退出匹配」按钮仍可点）；退场后仍能回主菜单；**不得**出现「面板已被关掉但栈里还有一条」导致第二次弹栈错位 |
| 6 | 边界：会话结束时栈顶**不是**匹配面板（例如玩家在匹配中又开了别的面板） | 只出现 `[TVIS1b] 旧 UI 栈拒绝关闭匹配面板（栈顶校验 / 两层校验 / 注册表校验未通过）：本轮不改栈，等条件成立再试`（最多一次），**不得**关掉别的面板、**不得**抛异常 |
| 7 | 单元素栈边界（若能在实机复现） | 同上拒绝日志，**不得**出现 `InvalidOperationException`（本轮 API 不走无参 `Close()`） |
| 8 | 退场/进场次序 | 日志里 `[TVIS1] 已恢复所属 Canvas 渲染` 必须早于 `[TVIS1b] 已按栈安全条件关闭匹配面板`（先还渲染、再动栈） |

> 口径提醒：**静态门全绿 ≠ 实机通过**。本轮只把「谁在什么时候以什么条件动旧 UI 栈」钉在 G10 + 四条纯策略上；
> 运行期是否真的不遮挡、真的回主菜单、第二局是否真的恰好一条，一律以 T-VIS5 为准，本轮不冒称已通过。

---

## 10. 写入边界与编码声明（本轮）

- 只写了 4 个文件：`Client/Assets/Scripts/Server/Manger/Start/StartUIManger.cs`、
  `Client/Assets/Scripts/Server/Panel/UIMatchingPanel.cs`、`Tools/PMLegacyRetirementTest/Program.cs`、本报告。
- 未新增/删除其它文件；未改任何 `.unity` / `.prefab` / `.json`；未改 `HYLDManger` / `UIRoot` / `UIBaseManger` / 宿主 / 服务端 / 协议。
- 三个源码文件的编码本轮实测：均为 **UTF-8 BOM + 纯 CRLF、无裸 LF**：
  `UIMatchingPanel.cs` 495 CRLF、`StartUIManger.cs` 208 CRLF、`Program.cs` 2932 CRLF（`loneLF = 0`）。
- 未启动 Unity / 服务端 / 打包；未 `git add` / 提交 / 暂存 / 回退 / SVN 写；未递归委派。
- `git status` 中另有 `PMClientSessionHost.cs`、`BattleMapV1.prefab`、`EditorBuildSettings.asset` 等**并行工作**留下的改动，
  **不是本轮产生**，本轮未触碰。
