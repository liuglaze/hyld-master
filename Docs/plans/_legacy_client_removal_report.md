# 旧客户端网络战斗/预测链删除报告（B 组：客户端删除）

任务类型：comprehensive（B 组客户端删除批次）。范围：`D:/UGit/hyld-master`。
本报告只覆盖**本组实际执行**的删除/修改/保留与验证；不宣称 OldScripts 全部删除，也不覆盖并行的
A 组（Server 旧战斗）、C 组（DS 旧 bootstrap / PMUdpRouter）、D 组（资产解挂）与顺序 E（proto/generated）。

## 0. 裁决与口径（本组遵循）

- 主侧已裁决：旧试玩「按模式自动起 manager」一并退役；新链只认 PMDS1，不再询问单机是否保留。
- 退出契约：`Docs/plans/net-legacy-retirement-contract.md` §B（Client）；删除清单来源
  `Docs/plans/_legacy_client_removal_survey.md` §6.1 DELETE（15 个文件 + 各自 `.meta`）。
- 只删这 15 个 `.cs` 与其 `.meta`（共 30 个文件），无其它删除。`PMUdpRouter` / `PMSingleBattleRegistry`
  属 DS 新链（真实调用方只有 `PMDsHost`），**本组不碰**（已由 C 组删除）。
- 新链 PMR3/4/5/6 与 `Scripts/Shared`、`Scripts/Math/BattleFloatMath` 全部保留。
- 资产组同步移除 BattleManger / HYLDCameraManger 挂载；本组**不改任何资产**（GUID 已冻结）。

## 1. 实际删除（15 个 .cs + 15 个 .meta = 30 个文件）

`wc -l` 合计 **4404 行**（不含 meta）：

| # | 文件 | 行数 |
|---|---|---|
| 1 | `Scripts/Server/Manger/Battle/BattleManger.cs` | 782 |
| 2 | `Scripts/Server/Manger/Battle/BattleData.cs` | 284 |
| 3 | `Scripts/Server/Manger/Battle/BattleData.Attack.cs` | 249 |
| 4 | `Scripts/Server/Manger/Battle/BattleData.Authority.cs` | 660 |
| 5 | `Scripts/Server/Manger/Battle/BattleData.HitEvent.cs` | 264 |
| 6 | `Scripts/Server/Manger/Battle/BattleData.Prediction.cs` | 472 |
| 7 | `Scripts/Server/Manger/Battle/BattleData.Rtt.cs` | 74 |
| 8 | `Scripts/Server/Manger/Battle/HYLDPlayerManger.cs` | 257 |
| 9 | `Scripts/Server/Manger/Battle/HYLDBulletManger.cs` | 504 |
| 10 | `Scripts/Server/Manger/Battle/HYLDCameraManger.cs` | 77 |
| 11 | `Scripts/Server/Manger/Battle/HYLDBaoShiZhengBaManger.cs` | 38 |
| 12 | `Scripts/Server/Manger/Battle/GameManger.cs` | 29 |
| 13 | `Scripts/Server/Manger/UDPSocketManger.cs` | 296 |
| 14 | `Scripts/Manger/CommandManger.cs` | 129 |
| 15 | `Scripts/Server/UI/BattleFrameHud.cs` | 289 |
| | **合计** | **4404** |

删除方式为 `rm`（不用 `git restore`，不在 `Assets/**` 留任何旧实现 `.bak`）。删除前已按
`rg --no-ignore` 逐条核对集合外活引用（见 §4）。

## 2. 实际修改（14 个文件）

### 2.1 引用收口（契约 B / M1—M5、M8—M13）

| 文件 | 改动 |
|---|---|
| `Scripts/Server/Panel/UIMatchingPanel.cs` | `OnResponse` 非 PMDS1 的 `ReturnCode.Succeed` 分支：删除旧回退 `Manger.BattleData.InitBattleInfo(...)` + `Manger.ClearSenceManger.LoadScene(battleScene)`，改为**显式报错** `[R3B] 收到旧链（非 PMDS1）入局成功通知…已明确拒绝`，绝不回退旧战场生成第二套入局状态。 |
| `Scripts/Server/Boot/PMClientSessionHost.cs` | 删除旧 socket 唯一触点 `global::Server.UDPSocketManger.CloseExisting();` 及其注释；`Enter` 日志里「已关闭旧战斗 UDP socket」改为「旧战斗 UDP socket 已随旧链删除」。 |
| `Scripts/Server/Manger/HYLDManger.cs` | `CloseClient` 去 `BattleManger`：改为读新宿主 `PMNet.Unity.PMClientSessionHost.IsActive` / `HasLastCombatResult`；在 `NetGlobal.AddAction`（主线程）里**仅在未拿到可信终局**时 `Stop()` 收尾，已可信终局则**不 Stop**（按新 host `EndSessionNormally` 正常结束语义保留只读终局 HUD），随后回 `HuangYeLuanDouStart`。 |
| `HYLD1.0/Scripts/OldScripts/HYLDStaticValue.cs` | `Awake()` 删除按模式 `AddComponent<HYLDBaoShiZhengBaManger>()` / `AddComponent<BattleManger>()` 两个旧宿主自动启动分支，替换为一行「旧战斗宿主自动启动已退役」日志。**Hero 表 / `AddHero` / 序列化数据全部保留**（`check_hero_table_shape` 仍 7/7）。旧场景只作素材输入，不再运行第二套权威。 |
| `HYLD1.0/Scripts/OldScripts/Toolbox.cs` | 删除 `Update()` 顶部 `BattleManger.Instance.IsGameOver` 守卫；宝石争霸 `Count==0` 分支删除 `IsGameOver` 轮询 + `BeginGameOver()`，改为 `yield return null`（保留帧让出、不再驱动旧管理器）；金库攻防删除 `BeginGameOver()` 调用（本地胜负标记保留）。 |
| `HYLD1.0/Scripts/OldScripts/TouchLogic.cs` | 删除 5 处 `CommandManger.Instance.AddCommad_*` 发送（3× Move、1× SuperAttack、1× Attack）；攻击/大招分支的日志由「ACCEPTED -> AddCommad_*」改为「VALIDATED（旧 CommandManger 发送已退役）」，**不伪造已接新链**，也**不引入空 CommandManger 替身**。摇杆/瞄准线/死区表现保留。 |
| `HYLD1.0/Scripts/OldScripts/PlayerLogic.cs` | `Manger.BattleData.LocalPositionJumpTraceThreshold` → 本文件 `private const float LocalPositionJumpTraceThreshold = 0.8f;`（仅日志门限）。 |
| `HYLD1.0/Scripts/OldScripts/HYLDPlayerController.cs` | 同上内联常量。 |
| `HYLD1.0/Scripts/OldScripts/Bullet/s/Bullet/移动型大招.cs` | 同上内联常量。 |
| `Scripts/Server/ConstValue.cs`（实际类 `Server.NetConfigValue`） | 瘦身：删 `ServiceUDPPort`(7777) 与只服务旧链预测/重发/弱网的字段（`PredictionHistoryWindowSize`、`ReconciliationPositionThreshold`、`EnablePredictionReconciliationPipeline`、`pingIntervalMs`、`maxCatchupPerUpdate`、`maxCatchupPerUpdateWhenBehind`、`inputBufferSize`、`targetFrameSafetyFrames`、`adjustRate`、`minSpeedFactor`、`maxSpeedFactor`、`smoothRate`、`jitterBufferRatio`、`maxJitterBufferFrames`、`severeLeadPauseFrames`、`pauseAccumulatorRetainFactor`、`moveMagnitudeThreshold`、`moveDotThreshold`）。保留 `RegexValue`（登录短信，`LoginUseSMS`）、`ServiceIP`/`ServiceTCPPort`（大厅 TCP，`TCPSocketManger`/`HYLDManger`）、`frameTime`（`shell.cs`/`HYLDStaticValue.cs` 源表现）、`canPlayerRestoreHealthTime`（非预测/重发/弱网字段，按「只瘦身该三类」口径保留）。 |
| `Scripts/Manger/ClearSenceManger.cs` | 删除注释掉的 `//Server.UDPSocketManger.Instance.Send(pack);`（纯注释残余）。`LoadScene` 通用入口保留（挂 `HYLDAsyncScence.unity`）。 |

### 2.2 工具/门禁（M5—M7）

| 文件 | 改动 |
|---|---|
| `Tools/check_client_authority_writes.py` | 删除 `ALLOWED` 中 9 条指向已删文件的条目（`BattleData.{HitEvent,Authority,Prediction,cs,Attack,Rtt}`、`HYLDPlayerManger`、`HYLDBulletManger`、`BattleManger`）；保留 `PlayerLogic` / `HYLDStaticValue` / `TouchLogic` 三条。 |
| `Tools/PMUnityGlueCheck/UnityStubs.cs` | 删除 `Server.UDPSocketManger` 旧链边界替身（新宿主已无该触点），改为「已随旧链退役删除」说明；其余真实 Unity 桩件不动。 |
| `Tools/PMR4UnityCheck/HostDependencies.cs` | 删除同款替身；文件保留（`PMR4UnityCheck.csproj` 仍 `<Compile Include>`，csproj 不在本批写入边界），头注释改写为「替身已退役」。**没有用新的 dummy 掩盖新 API**——新链 API 一律编真实源码。 |

## 3. 实际保留（关键项与理由）

- **新 DS 链**：`Scripts/Server/Net/PMUdpRouter.cs`、`PMSingleBattleRegistry.cs`——由 C 组删除，本组不碰。
- **共享核心**：`Scripts/Shared/{PMBattleSim,BattleNumericConfig}.cs`、`Scripts/Math/BattleFloatMath.cs`。
- **旧表现/美术源（挂资产，删掉会造成 missing script）**：`shell.cs`、`BulletLogic.cs`、`移动型大招.cs`、`PlayerLogic.cs`、`HYLDPlayerController.cs`、`TouchLogic.cs`、`Toolbox.cs`、`HYLDStaticValue.cs`、`rolateSelf.cs`（被 `BulletLogic` 使用）。
- **`ClearSenceManger.cs`**（挂 `Scenes/HYLDAsyncScence.unity`，通用切场景入口）、**`ConstValue.cs`**（瘦身后仍是大厅地址/帧时长来源）。
- **资产**：`Scenes/HYLDGameTest.unity`、`HYLD1.0/Scenses/*.unity`、`HYLD1.0/Resources/Main Camera.prefab`、`Resources/Remake/*` 等由 D 组处理；本组零改动。
- **OldScripts 其余文件未删**：本组只做「删除旧网引用」的必要改动，未批量删美术源资产/第三方，也**不称全部旧试玩算法已删除**。

## 4. 删除完备性与验证（实际执行结果）

### 4.1 引用终判

- 删除前/后均用 `rg --no-ignore` 扫描 `Client/Assets` + `Tools`（排除 `Library/Temp/obj/bin/Docs`、`SocketProto.cs` 生成物），
  命中项逐条核对：除**注释/文档/csproj 注释**外，无任何活代码引用。
- 终判用 `rg --no-ignore` 的原因：需要「删除后不留悬空引用」的完备性证据；pi-fff 内容索引对 gitignore 区域
  （`Client/Library` 等）为 partial 语义，故以真 `rg --no-ignore` 做终判。
- 残留命中（**有意保留，非活调用**）：
  - 文档：`Client/Assets/AGENTS.md` §3/§5/§7/§8、`Client/Assets/Docs/{ForServer.md,BattlePipelineGuide.md}`、`BothSide.md`、
    `Docs/plans/*`（均为旧链描述，属收尾批次，本组无写入边界）。
  - csproj 注释：`Tools/PMClientCheck.csproj`、`Tools/PMUnityGlueCheck.csproj`、`Tools/PMR4UnityCheck.csproj`、`Tools/PMHeroDataCheck.csproj`
    （csproj 不在写入边界）。
  - 历史生成脚本：`Client/Assets/update_{prediction,attack,hit_event}.py`（只含被删文件的旧头注释，非编译单元）。
  - 资产里 `Scenes/HYLDLogon.unity:511` 的 `m_Name: GameManger` 只是 GameObject 名字字符串，不是脚本 GUID 引用。
  - 保留源里的说明性注释（如 `PlayerLogic.cs` 的 `BattleManger -> playerManger…` 历史说明）。

### 4.2 编译与门禁

| 项 | 结果 |
|---|---|
| `Tools/PMClientCheck`（编真 `Scripts/Server/**`、`Scripts/Manger/**`、`HYLD1.0/Scripts/**`，无 `UDPSocketManger` stub） | **成功 0 警告 0 错误** |
| `Tools/PMUnityGlueCheck` | **成功 0 警告 0 错误** |
| `Tools/PMR4UnityCheck` | **成功 0 警告 0 错误** |
| `Tools/PMHeroDataCheck` | **成功 0 警告 0 错误**（数据/资源映射未动） |
| `python Tools/check_cs_braces.py <11 个改动 .cs>` | **PASS**（全部花括号/圆括号 BALANCED） |
| `python Tools/check_client_authority_writes.py` | **PASS**（越界写入 0；许可路径 3 个文件） |
| `python Tools/check_hero_table_shape.py` | **7 项 0 失败**（20 行 AddHero，表现引用与冻结基准一致） |
| `python Tools/check_hero_id_alignment.py` | **6 项 0 失败** |
| `python Tools/scan_non_utf8_text.py` | 疑似 7 个**既有**文件（protobuf 生成物 / XLua 示例），本组改动文件**均不在其中** |
| `Tools/PMDsHostCheck`（另组范围，仅报告） | 成功 0/0（本次未改其文件） |
| `Tools/PMUdpRouterCheck`（C 组范围，仅报告） | 目录已被 C 组清空（仅剩 `bin/obj`），`dotnet build` 报 `MSB1003 未找到项目文件`；**本组未修** |

编码/行尾：本组所有改动文件保持原有 BOM 与 CRLF（`.cs` 中文源为 BOM+CRLF；`UIMatchingPanel.cs`/`ConstValue.cs`/`ClearSenceManger.cs`
原为无 BOM，保持不变），逐文件用字节级核对（`bom=` / `crlf==lf`）。

## 5. 边界外/未做（交主侧或后续批次）

- **proto / generated**：`SocketProto.proto`、四份 `SocketProto.cs`、PMNet 产物**完全未动**（顺序 E 统一处理）。
  本组未发现需要上抛的「旧 protocol 类型活调用」：删除后活代码里已无旧战斗消息消费。
- **资产**：本组零改动（D 组负责 GUID 解挂；本组已验证编译无悬空脚本引用）。
- **文档收尾**：`Client/Assets/AGENTS.md`（§3/§5/§7/§8）、`Client/Assets/Docs/{ForServer.md,BattlePipelineGuide.md}`、
  `BothSide.md`、`Docs/plans/net-architecture-migration.md` 未改（不在写入边界）。
- **Unity 实机**：未启动/未关闭 Editor，未跑 R4/R5/R6 实机回归（用户后置）。
- **未提交**：未 `git add`/`commit`/`push`，未 `svn` 写入。

## 6. 结论

B 组客户端旧战斗链的**运行入口与实现**已按契约删除：15 个旧链 `.cs`（4404 行）+ 30 个文件（含 meta）清除，
14 个文件完成引用收口与常量内联，工具白名单/stub 同步；四个门禁工程编译 0/0，四个 python 门禁全绿。
旧链不再有活调用；保留的 OldScripts/资产仅为素材与表现，**不宣称全部旧试玩算法已删除**。
