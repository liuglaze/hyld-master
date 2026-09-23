# 旧链禁回归静态门禁报告（PMLegacyRetirementTest）

> 本文件由 `Tools/PMLegacyRetirementTest`（`--report`）生成；复跑命令：
> `dotnet run -c Release --project Tools/PMLegacyRetirementTest -- --report Docs/plans/_legacy_retirement_gate_report.md`
> 冻结契约：`Docs/plans/net-legacy-retirement-contract.md`。状态唯一源仍是 `Docs/plans/net-architecture-migration.md`。

## 1. 结论

| 项 | 值 |
|---|---|
| 检查根目录 | `D:/UGit/hyld-master` |
| 静态检查 | 通过 28 / 失败 0 |
| 负例自测 | 通过 5 / 失败 0 |
| 总体结论 | **PASS** |

## 2. 范围（Scope）

- 只读检查**真实磁盘源码**：`Client/Assets/**/*.cs`、`Server/**/*.cs`（扫描根见下），
  以及权威 proto、四份 generated 产物、`Client/Assets` 下的 `*.unity` / `*.prefab`、6 个冻结资产。
- 明确排除：`Library/`、`bin/`、`obj/`、`Docs/`、历史 `log/`、第三方 `Google*` 目录、`Generated/` 与 `*.g.cs`
  （generated 由 G7 单独检查，不混进 token 扫描）。
- 扫描根：`Client/Assets`、`Server`。工具自身位于 `Tools/PMLegacyRetirementTest/`，**不在**扫描根内，
  因此不存在「门禁把自己的字符串当命中」的问题。
- **GameManger 同名风险**：契约指的是已删的 `Manger.GameManger`。扫描根限定在 `Client/Assets` 与 `Server`
  的**本项目源码**（第三方/引擎目录已排除），不会误判第三方同名类型；当前活源码中 `GameManger` 出现 0 次。

## 3. 检查清单与结果（静态边界）

| 组 | 检查项 | 结果 | 说明 |
|---|---|---|---|
| G1 | `G1.client-files-and-meta-absent` | OK | 客户端 17 个旧 .cs 与其 .meta 均不存在 |
| G1 | `G1.server-files-absent` | OK | 服务端 8 个旧战斗/UDP 源码均不存在 |
| G1 | `G1.retired-tool-csproj-absent` | OK | PMUdpRouterCheck / PMUdpRouterTest / PMDsProbe 的 .csproj 均不存在 |
| G2 | `G2.scan-inputs-present` | OK | 扫描输入存在：.cs 文件数 = 358 |
| G2 | `G2.tool-outside-scan-roots` | OK | 门禁自身（D:/UGit/hyld-master/Tools/PMLegacyRetirementTest/bin/Release/net8.0/）不在扫描根内，不会把自身字符串当成命中 |
| G2 | `G2.stripper-anti-vacuity` | OK | 词法剥离 1:1 保长（0 个长度不一致文件），全局代码占比 0.694，三处代码金丝雀全部保留（最低单文件占比仅作参考=0.078 @Client/Assets/XLua/Editor/ExampleConfig.cs） |
| G2 | `G2.legacy-type-tokens-absent` | OK | 19 个旧类型标识符在活源码（去注释/去字符串）中出现 0 次 |
| G2 | `G2.hardcoded-udp-7777-absent` | OK | 活源码（去注释/去字符串）无硬编码战斗 UDP 端口 7777 |
| G3 | `G3.dshost-depends-on-sessionhost` | OK | PMDsHost 引用 PMDsSessionHost（新链唯一会话面） |
| G3 | `G3.dshost-no-legacy-router-or-socket` | OK | PMDsHost 去注释/去字符串后不含裸 socket / 旧 router / 旧诊断包 / 线程 |
| G3 | `G3.dshost-requires-bootstrap-path` | OK | PMDsHost 结构上读取 BootstrapPath（缺 -bootstrap 的**运行时行为**由 PMDsHostCheck 另测，本门不冒充） |
| G4 | `G4.controllers-no-legacy-selector` | OK | Controllers.cs 无旧选链开关/旧开局实现标识符 |
| G4 | `G4.startfighting-routes-only-dedicated` | OK | StartFighting 方法体只调用 StartFightingDedicatedServer（无旧分支） |
| G4 | `G4.repo-no-newchainenabled` | OK | 活源码中 NewChainEnabled 出现 0 次 |
| G5 | `G5.uimatching-no-legacy-entry-fallback` | OK | 去注释/去字符串后无 BattleData/旧 ClearSence LoadScene 回退 |
| G5 | `G5.uimatching-only-new-chain-entry` | OK | UIMatchingPanel 只识别 PMDS1（PMDsEntryCodec）入局通知 |
| G6 | `G6.proto-present` | OK | 权威 proto 存在：ProtobufAndNotepad/Protobuf/SocketProto.proto |
| G6 | `G6.proto-parse-and-type-refs` | OK | 解析通过，且无字段引用不存在的 message/enum（无类型复活） |
| G6 | `G6.proto-legacy-declarations-absent` | OK | 14 条旧战斗消息 + MoveType/AttackType 均未声明 |
| G6 | `G6.proto-legacy-numbers-absent` | OK | RequestCode 7/8、ActionCode 31..41、MainPack 13/15 均无定义 |
| G6 | `G6.proto-lobby-numbers-frozen` | OK | 厅相关既有号未漂移（7 枚举 + 7 消息；StartEnterBattle=30、Hero 全 20 项保留） |
| G6 | `G6.proto-legacy-numbers-reserved` | OK | 旧号已显式 reserved（RequestCode 7/8、ActionCode 31..41、MainPack 13/15），号不可重用 |
| G6 | `G6.non-authoritative-proto-copies-absent` | OK | 非权威 proto 副本均已删除 |
| G7 | `G7.generated-present` | OK | 四份 generated 产物均存在 |
| G7 | `G7.generated-no-legacy-types` | OK | 四份产物（去注释）无 14 旧消息 + MoveType/AttackType 的 class/enum/serializer |
| G7 | `G7.generated-no-dangling-type-refs` | OK | generated 内 global::SocketProto.* 类型引用全部有本文件声明兜底 |
| G8 | `G8.detached-guids-zero` | OK | 两个已解挂 GUID 在 1037 个 *.unity/*.prefab 中出现 0 次 |
| G8 | `G8.frozen-anchors-unchanged` | OK | 6 个冻结锚（HYLDGame.unity / Remake Player.prefab / 3 个 PMNet 输出 / EditorBuildSettings）SHA256 全部相符 |

### 3.1 各组断言的是什么（能力边界）

- **G1** 已删源码/`.meta`/退役工具 `.csproj` 是否以任何形式复活（存在性即可判定）。
- **G2** 活源码**词法剥离注释与字符串后**是否仍出现 19 个旧类型标识符与硬编码 UDP `7777`。
  剥离保留换行与偏移，所以报的是真实行号；注释/字符串里的说明文字不会误报。
- **G3** `PMDsHost` 的**结构事实**：依赖 `PMDsSessionHost`、无 `new Socket`/裸 socket/旧 router/旧诊断包/线程、
  结构上要求 `BootstrapPath`。它**不**验证「缺 bootstrap 时是否真的 Quit(1) 且不启动会话」——
  那是 `Tools/PMDsHostCheck` 的替身行为门禁的职责，本门禁不冒充。
- **G4** `Controllers.StartFighting` 方法体只调 `StartFightingDedicatedServer`，且全活源码无 `NewChainEnabled` 旧选链开关。
- **G5** `UIMatchingPanel` 去注释去字符串后无 `BattleData`/旧 `ClearSenceManger.LoadScene` 回退，且只认 `PMDsEntryCodec`。
- **G6** 权威 proto 的**声明名 + 字段号 + 枚举号**：14 条旧战斗消息与 `MoveType`/`AttackType` 不得声明；
  `RequestCode.Battle/ClearSence` 与号 7/8、`ActionCode` 31..41、`MainPack` 13/15 不得有定义，
  且必须被显式 `reserved`（reserved 语句不算定义）；厅相关既有号（7 枚举 + 7 消息）不得漂移；
  字段类型必须能在本文件内解析（禁止引用已删 message ⇒ 类型复活）。
- **G7** 四份 generated 产物（去注释）中不得出现 14 旧消息 + `MoveType`/`AttackType` 的
  `class`/`enum`/`Serializer`（含 `PM` 前缀形态），且 `global::SocketProto.*` 类型引用必须本文件有声明。
- **G8** 两个已解挂 GUID 在 `Client/Assets` 的 `*.unity`/`*.prefab` 中出现 0 次（并强制资产文件数 > 0，
  禁止「扫到 0 个文件就绿」）；6 个冻结锚的 SHA256 必须等于 `_legacy_asset_detach_report.md` §4.5 的固定值。

- 不做的事：不运行 Unity、不启动服务端、不提交、不 `git add`/暂存、不递归委派；
  不重做业务测试（本工具只做静态事实判定）。

## 4. 负例自测（临时沙盒注入，不修改真实生产文件）

沙盒根：`%TEMP%`（运行结束后清理）。失败计数：**0**。

| 结果 | 用例 | 说明 |
|---|---|---|
| PASS | N1 缺输入必须 FAIL | proto 缺失=True 扫描根缺失=True 资产根缺失=True（三组都必须报 FAIL，禁止空扫描绿） |
| PASS | N2 旧 class 复活必须被检出 | token 命中=True 旧文件复活=True |
| PASS | N3 注释/字符串同词不得误报 | 注释+字符串同词误报=False 注释里7777误报=False \| 正对照：代码里的7777被检出=True 且无旧类型误报=True |
| PASS | N4 旧 Action 定义复活必须被检出 | 干净基线全绿=True 注入 BattleReady=31 后被检出=True |
| PASS | N5 资产 GUID 残留必须被检出 | 注入 GUID 后被检出=True 干净沙盒不误报=True |

负例覆盖：缺输入必须 FAIL（不是空扫描绿）、旧 class 复活必须被检出、
注释/字符串里的同词**不得**误报（并带一组「代码里的 7777 必须被检出」的正对照，
防止「不误报」是靠整体失灵换来的）、旧 Action 定义复活必须被检出、资产 GUID 残留必须被检出。

## 5. 门禁检测力自证（可复现，不修改仓库文件）

负例自测只覆盖**合成夹具**。为证明 G6 对**真实历史内容**同样有检出力，
可用下面的配方把契约 §E 之前的 proto 放进临时沙盒复跑（仓库文件全程只读）：

```bash
SB="$TEMP/pm-gate-oldproto"
rm -rf "$SB"; mkdir -p "$SB/ProtobufAndNotepad/Protobuf" "$SB/Client"
git show HEAD:ProtobufAndNotepad/Protobuf/SocketProto.proto > "$SB/ProtobufAndNotepad/Protobuf/SocketProto.proto"
cp "$SB/ProtobufAndNotepad/Protobuf/SocketProto.proto" "$SB/Client/SocketProto.proto"
dotnet Tools/PMLegacyRetirementTest/bin/Release/net8.0/PMLegacyRetirementTest.dll --repo "$SB" --no-self-test
```

本门禁实现完成后已执行过一次该配方（当时 HEAD 仍为 §E 之前的旧 proto），记录到的结果：
G6 的 `legacy-declarations-absent` / `legacy-numbers-absent` / `legacy-numbers-reserved` /
`non-authoritative-proto-copies-absent` 与 G2/G3/G4/G5/G7/G8 的缺输入项共报 **10 个 FAIL**，
逐条给出了具体复活声明与占用号；把沙盒 proto 换回当前权威 proto 后即恢复全绿。
⇒ G6/G7 的「绿」不是空扫描或缺失输入造成的假绿。

## 6. 未覆盖 / 待主侧复跑

- 契约 §E 的 proto 收缩（旧消息删除、旧号 reserved、四份产物重生成、非权威副本删除）**已生效**，
  G6/G7 因此为绿；本门禁将长期看护这条边界，任何一处回退都会立刻变红。
- 冻结 SHA 锚是**固定值**：若后续**有意**重烘 `HYLDGame.unity` / `Remake/Player.prefab` / PMNet 输出，
  必须同步更新锚值与本报告说明，门禁不会写默认 PASS。
- 真实 Unity 实机加载、`PMDsHost` 运行时退出行为、服务端/Lobby 运行语义均不在本静态门禁范围内。

## 7. 写入边界

- 本工具只读仓库源码与资产；除 `--report <path>` 指定的报告文件外不写任何仓库文件（沙盒一律在 `%TEMP%`）。
- 未运行 Unity、未启动服务端、未提交、未 `git add`/暂存、未递归委派，未执行任何 SVN/git 写操作。

