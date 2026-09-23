# 旧 UI / 回放协议消费删除报告（契约 E2 组）

范围：`D:/UGit/hyld-master`，只动契约 `Docs/plans/net-legacy-retirement-contract.md`「E冻结补充」中 E2 的客户端边界。
本报告是本次唯一的白名单外新增文件（任务指定的报告落盘点）；**未新建/删除其它任何文件**。
未做：`git add`/暂存/提交/回退、SVN 写入、启动或关闭 Unity、递归委派、修改 Unity 资产（`.unity/.prefab/.meta`）与 proto 生成物。

必读文档（已完整读，按此顺序）：
`D:/UGit/hyld-master/AGENTS.md` → `Client/Assets/AGENTS.md`（全文）
→ `Docs/plans/net-legacy-retirement-contract.md`（全文，尤其 §E 与「E冻结补充」）
→ `C:/Users/luomingcong/.pi/agent/skills/windows-shell-compat/SKILL.md`。

---

## 0. 摘要

E2 的客户端旧回放 / 旧清场景协议**活引用已归零**，五个文件按边界最小改动：

- `UIStartMainPanel`：删 `ActionCode.BattleReview` 的 Request 注册与 `OnResponse` 分支（`ChangeHero` 仍用 `Requests[4]`，索引不变）。
- `RequestManger.HandleRequest`：删 BattleReview「回主线程落盘」截获分支；**保留** `PmRpcClient.OnResponseReceived` 清账（仍在所有早退分支之前）、`PingPong`、`ActionNone` 与正常派发。
- `HYLDManger`：删 `AddBattleReview` / `GetBattleReview`（磁盘回放持久化）与仅其使用的 `using Google.Protobuf`；大厅 TCP / PingPong / 断线收尾不动（`using System.IO` 因 Awake 建日志目录而保留）。
- `UISliderPanel`：**保类名、保 GUID `12ce5b5195389c74caa57eaa1c9c8f7f`、继续挂在 `HYLDAsyncScence`**，退化为无任何 `BaseRequest` 的纯本地进度 UI；删 `SendLoadOver` / `IsCanEnterBattle` / 远端 `OnResponse` 战斗处理。
- `ClearSenceManger`：回归通用本地异步加载（`progress >= 0.9` 即 `allowSceneActivation`），删 `battleScene` 专属的 `SendLoadOver` 与 `WaitUntil(IsCanEnterBattle)` 等待（避免无对端响应时挂死）、删 `isAllPlayerClearOk`；保留场景序列化字段 `UISliderPanel` 以免改动资产语义。

验证：五文件 + `Client/Assets` 当前源码（排 `generated`/`Library`）对旧 Action / 旧 `GetReview` / `SendLoadOver` / `IsCanEnterBattle` / `battleScene` 扫描 **0 命中**；
`PMClientCheck`、`PMUnityGlueCheck` 均 **0 警告 0 错误**（proto 组已重生成，`SocketProto.cs` 与 `PMNet/Generated` 中 `ClearSence`/`BattleReview` 已消失，两端一致）。

---

## 1. 改前基线

- 基线 commit：`2084a67178eca1f3e9ae7c76eabfdf34ef9274e9`
- 改前工作区已有他组未提交改动（`Client/**`、`Docs/plans/**`、`Tools/**` 等），本次未触碰。

| 文件 | HEAD blob | 行数 | 本次后 blob | 行数 |
|---|---|---|---|---|
| `Client/Assets/Scripts/Server/Panel/UIStartMainPanel.cs` | `8b9b9772e52e26bd9ebce3ccbc6bf3f91a2b9387` | 238 | `3fd2da064b5a60c8b01b534229de1052a8ab2e18` | 230 |
| `Client/Assets/Scripts/Server/Manger/RequestManger.cs` | `df42705f389ed7fa0608de8dbdb6879afb7319d4` | 79 | `6c85095ae0e0c22c88000bd49ef06b17bc9e30ec` | 68 |
| `Client/Assets/Scripts/Server/Manger/HYLDManger.cs` | `75b572f940e0097d0e7ef9c09b1a119b6f459df5` | 267 | `91ed3a89fddd394afc49a387405f2ea51452a9e6` | 228 |
| `Client/Assets/Scripts/Server/Panel/UISliderPanel.cs` | `a02c66438220fe37766928d2e24daa177e7182bc` | 78 | `5548bd7fe665593a36b30f65f31cc7de94a296b7` | 20 |
| `Client/Assets/Scripts/Manger/ClearSenceManger.cs` | `2b3caf04b1b619d7d78ae40433dcf07032e575c6` | 170 | `4c22afd7aa40bc361aff7ced1ba6a3179e29b8f1` | 147 |

> `HYLDManger.cs` 的工作区 diff 还包含**他组未提交**的 `CloseClient` 新链改写（`PMClientSessionHost.IsActive/HasLastCombatResult/Stop`，客户端 B 组契约）。本次在 `HYLDManger.cs` 只删了 `AddBattleReview`(20 行) + `GetBattleReview`(20 行) + `using Google.Protobuf`(1 行)，未触碰 `CloseClient`。

`git diff --numstat`（本次 5 文件，`HYLDManger` 含上述他组改动）：

```text
12      35      Client/Assets/Scripts/Manger/ClearSenceManger.cs
17      56      Client/Assets/Scripts/Server/Manger/HYLDManger.cs
0       11      Client/Assets/Scripts/Server/Manger/RequestManger.cs
12      71      Client/Assets/Scripts/Server/Panel/UISliderPanel.cs
1       9       Client/Assets/Scripts/Server/Panel/UIStartMainPanel.cs
```

### 1.1 冻结来源复核（改前，rg --no-ignore，排 `generated`/`Library`）

- `UIStartMainPanel`：注册 `(RequestCode.User, ActionCode.BattleReview)` + `OnResponse` 的 `BattleReview` 分支 → 唯一注册点。
- `RequestManger`：`else if (pack.Actioncode == ActionCode.BattleReview)` → `NetGlobal.AddAction(() => HYLDManger.Instance.AddBattleReview(pack))` → 唯一截获点。
- `HYLDManger.AddBattleReview/GetBattleReview`：写/读 `streamingAssetsPath` 下的 Review 文本；全仓库无其它调用点。
- `UISliderPanel`：清场景协议 35/36 两个 `BaseRequest`、`SendLoadOver()`、`IsCanEnterBattle`。
- `ClearSenceManger.AsyncLoadScene`：唯一 `SendLoadOver()` / `IsCanEnterBattle` 调用者；`battleScene` 分支已无 `LoadScene(battleScene)` 调用者（`UIMatchingPanel` 旧回退由前组删除，只剩注释）。
- 资产面复核：`rg -n -g '*.unity' -g '*.prefab' -g '*.asset'` 搜 `BattleReview|ClearSence|SendLoadOver|IsCanEnterBattle` → **0 命中**，确认无 UnityEvent 方法名 / 场景资产引用这些成员。

---

## 2. 删除范围（逐文件）

### 2.1 `Client/Assets/Scripts/Server/Panel/UIStartMainPanel.cs`
- 删 `Requests.Add(new BaseRequest(this, RequestCode.User, ActionCode.BattleReview));`
- `OnResponse` 的 `if (BattleReview) Debug.LogError else ResponseUser` 折叠为直接 `ResponseUser(pack)`；`RequestCode.Friend` 分支保留。
- 保留：改名/查好友/好友登录登出/换英雄 5 个 Request（`Requests[0..4]` 索引含义不变，`ChangeHero` 仍 `Requests[4]`）。

### 2.2 `Client/Assets/Scripts/Server/Manger/RequestManger.cs`
- 删 BattleReview 分支（含 `NetGlobal.Instance.AddAction` 包装与空判）。
- 保留：`PmRpcClient.OnResponseReceived(pack)` 必须在所有早退之前的清账语义、`RequestCode.PingPong → HYLDManger.Pong`、`ActionNone` 忽略、`_requestDic` 派发与「找不到处理」日志。

### 2.3 `Client/Assets/Scripts/Server/Manger/HYLDManger.cs`
- 删 `AddBattleReview(MainPack)`、`GetBattleReview()`（`Application.streamingAssetsPath` 下的 Review 写入/解析）。
- 删 `using Google.Protobuf;`（仅 `ToByteArray()` / `MainPack.Descriptor.Parser.ParseFrom` 使用；已复核文件内无其它 Google.Protobuf 符号）。
- 保留：`using System.IO`（Awake 的 `Path.Combine`/`Directory.*` 日志目录）、DS 守卫、`Send`、`Pong`、`CloseClient`、`OnInit`。

### 2.4 `Client/Assets/Scripts/Server/Panel/UISliderPanel.cs`
- 删 `IsCanEnterBattle` 属性、`Init()` 中的两个清场景 Request 注册、`SendLoadOver()`、处理 `AllClearSenceReady` 的 `OnResponse` 覆写。
- 删仅被上述代码使用的 `using System.Collections/Generic/System/UnityEngine.UI/System.Linq/SocketProto/Server`。
- 结果：`public class UISliderPanel : UIbasePanel { }`，零 `BaseRequest`；类名与脚本 GUID 不变，`RegisterUIEvent` 走基类（`btnClose` 行为与改前一致）。

### 2.5 `Client/Assets/Scripts/Manger/ClearSenceManger.cs`
- `AsyncLoadScene` 改为纯本地：`while (async.progress < 0.9f)` 推进进度条 → `slider=1.0` / `100 %` → `allowSceneActivation = true`。
- 删 `scene != SceneConfig.battleScene → break`、`UISliderPanel.SendLoadOver()`、`WaitUntil(() => UISliderPanel.IsCanEnterBattle)`、`private bool isAllPlayerClearOk`。
- 删 `using SocketProto;`（未使用）、`using System.Collections.Generic;`、`using System.Linq;`；**保留 `using System;`**（`#else` 非编辑器分支用 `GC.Collect/WaitForPendingFinalizers`，由 PMClientCheck 编译实证）。
- 保留 `StartCoroutine(ClearResouces())` 的资源回收块（原样）、`LoadScene(int)` + `SceneConfig.clearScene` 跳转、`slider/progress` 字段。
- 本类不再引用 `SceneConfig.battleScene`：`LoadScene(int)` 只加载**请求的那个场景**，不会把任何入口静默路由到旧战场或新对局（`SceneConfig` 仍可被调用方传入）。

---

## 3. 仍保留的「纯 UI 壳」

| 保留项 | 位置 | 理由 |
|---|---|---|
| `class UISliderPanel : UIbasePanel` | `UISliderPanel.cs:17` | 场景 `HYLDAsyncScence.unity:882` 的 GameObject 上挂着该脚本组件（`m_Script` guid `12ce5b5195389c74caa57eaa1c9c8f7f`），删类即 Missing Script |
| 脚本 GUID / meta | `UISliderPanel.cs.meta`（`12ce5b51…`）、`ClearSenceManger.cs.meta`（`92998943…`） | 原 meta 未改（`git status` 无记录），场景 `m_Script` 解析保持有效 |
| `ClearSenceManger.UISliderPanel` 字段 | `ClearSenceManger.cs:21` | `HYLDAsyncScence.unity:640` 的 `UISliderPanel: {fileID: 4068297456744183870}` 是它的序列化值；保留字段=不改资产语义。该字段已不再参与任何网络等待 |
| `ClearSenceUIManger.Open(nameof(MVC.UISliderPanel))` | `ClearSenceUIManger.cs:60` | 非本次边界文件；面板 `Init()` 仍由 `ClearSenceUIManger.OnInit` 的 recyclePool 遍历调用，改后无任何 RPC |
| `Manger.ClearSenceManger.LoadScene(SceneConfig.mainScene)` | `HYLD1.0/Scripts/OldScripts/HYLDGameOver.cs:82` | 旧战场收尾回大厅路径，走本地异步加载，可用；新 PMDS1 入局不使用该 loader |

新链结算流程（`PMClientSessionHost` / `PMDsSessionHost` / R6 result HUD）本次**未触碰**。

---

## 4. 测试与门禁

### 4.1 静态扫描（任务要求的「首删后」核验）

```text
# A) 五个白名单文件（期望 0）
rg --no-ignore -n -e 'ActionCode\.BattleReview' -e 'RequestCode\.ClearSence'
   -e 'ClientSendClearSenceReady' -e 'AllClearSenceReady' -e 'AddBattleReview'
   -e 'GetBattleReview' -e 'SendLoadOver' -e 'IsCanEnterBattle' -e 'battleScene' <5 files>
→ exit=1（无命中）

# B) Client/Assets 当前源码（排 generated/Library/bin/obj）
rg --no-ignore -n --glob '!Library/**' --glob '!**/Generated/**' --glob '!**/obj/**' --glob '!**/bin/**'
   <同上 8 个模式> Assets
→ exit=1（无命中）

# C) proto 生成物（proto 组 E1 已重生成）
rg --no-ignore -n -e 'ClearSence|BattleReview' Assets/Scripts/Server/SocketProto.cs Assets/Scripts/PMNet/Generated/
→ exit=1（无命中；ActionCode 现止于 StartEnterBattle=30，RequestCode 现止于 Matching=6）
```

- 结构检查：`python Tools/check_cs_braces.py <5 files>` → `结果: PASS`（5 文件花括号/圆括号均配平，顺序感知深度归零）。
- 编码：5 文件逐一复核 `BOM/CRLF`；`HYLDManger.cs` 保留 BOM，其余原为 noBOM，全部保持 CRLF（`crlf == lf_total`）。

### 4.2 编译门禁（构建产物写到 `%TEMP%`，仓库内未产生 bin/obj 改动）

```text
cd Tools/PMClientCheck
dotnet build PMClientCheck.csproj -v m -p:MSBuildProjectExtensionsPath=<temp>/obj/
  -p:BaseIntermediateOutputPath=<temp>/obj/ -p:BaseOutputPath=<temp>/bin/
→ 已成功生成。0 个警告 0 个错误（netstandard2.0 / C# 7.3，含 Scripts/Server/**、Scripts/Manger/** 真代码）

cd Tools/PMUnityGlueCheck
dotnet build PMUnityGlueCheck.csproj -v m -p:MSBuildProjectExtensionsPath=<temp>/obj/
  -p:BaseIntermediateOutputPath=<temp>/obj/ -p:BaseOutputPath=<temp>/bin/
→ 已成功生成。0 个警告 0 个错误
```

过程记录（供主侧统一）：首次编译暴露两处**我引入的**问题并已修复 ——
① `UIStartMainPanel.OnResponse` 删分支时误删 `if` 块闭合花括号（CS8641/CS1513）；
② `ClearSenceManger` 误删 `using System;` 导致 `#else` 分支 `GC` 未定义（CS0103）。
修复后两轮编译均 0/0。**proto 组期间重生成 `SocketProto.cs` / `PMNet/Generated`，最终一轮门禁是在重生成之后跑的**，故该绿灯同时证明「我方删除」与「proto 删除」在客户端侧一致，不依赖两组输出齐备。

真实 Unity 2019 宿主编译（T-L2）与实机仍按契约后置，本次**未启动 Unity**。

---

## 5. 残留与移交主侧

1. **文档**：`Client/Assets/AGENTS.md:10`、`Client/Assets/Docs/ForServer.md:128` 仍在描述「战斗结束服务端 TCP 下发 BattleReview 完整回放、客户端切主线程保存」——该能力已删，需文档组（E6）同步；不在本次写边界内。
2. **他组文件**：`Tools/PMNetVerify`（旧 DTO/协议门禁改写）、proto 非权威副本清理、`Client/Assets/HYLD2.0/Config/GameConfig.cs` 的 `battleScene` 常量清理均属 E 的其它子组；本次只保证客户端**活引用**为零。
3. **命名残留（非活引用）**：类名 `ClearSenceManger`、`SceneConfig.clearScene` 语义仍是「清资源中转场景」，与已退役的清场景**协议**无关；`UISliderPanel` 内保留的说明性注释不含被删枚举字面量，机械扫描可保持 0 命中。
4. **资产**：`Client/Assets/Scenes/HYLDAsyncScence.unity` 未改（本次 diff 中 `HYLDGameTest.unity` 的修改是他组 D 的既有未提交改动，非本次）。
5. **未做**：`git add/rm` 暂存、提交、回退、Unity 启动、递归委派。
