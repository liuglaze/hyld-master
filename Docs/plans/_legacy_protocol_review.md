# 旧链 proto 退役 / 资产解挂 / 禁回归门禁 —— 独立只读复核

复核范围：`ProtobufAndNotepad/Protobuf/SocketProto.proto` + 4 份 generated 产物、`Tools/PMNetVerify`、`Tools/PMLegacyRetirementTest`、D 段 4 个资产改动与 2 个冻结 GUID、幸存大厅 DTO 消费者。
边界：只读；未运行 build / 测试 / Unity；未改任何源码、资产；未递归委派。实时 `rg --no-ignore` 仅用于「全量归零」完备性判定（FF 内容索引对刚被外部进程改写的文件与 gitignore 区为 partial 语义），下文均注明。

## 必读实际范围（实际读取）

1. `D:/UGit/hyld-master/AGENTS.md`（根入口，3 行，指向 Client/Server/计划）
2. `Client/Assets/AGENTS.md`（读到 §12 协议来源与生成约定、§14/§15 新链状态；含 7777 旧端口、proto 四产物约定）
3. `Docs/plans/net-legacy-retirement-contract.md`（§E / E 冻结补充 / 验收 T-L1..T-L6）
4. `Docs/plans/_legacy_protocol_removal_report.md`
5. `Docs/plans/_legacy_asset_detach_report.md`
6. `Docs/plans/_legacy_retirement_gate_report.md`
7. 实现与产物（逐段读）：`ProtobufAndNotepad/Protobuf/SocketProto.proto`、`ProtobufAndNotepad/Protobuf/build.bat`、4 份 generated 产物（含 protoc 内嵌 descriptor base64 解码）、`Tools/PMLegacyRetirementTest/Program.cs`（G1–G8 + 负例自测 + CodeStripper 调用）、`Tools/PMNetVerify/Program.cs`（A–K 段 + oracle 驱动）、两个 `.csproj`
8. 资产：`git diff HEAD` 4 个资产 + `git show HEAD:*.cs.meta` 两个冻结 GUID + 全仓 `*.unity/*.prefab` 实时扫描

动态首搜入口未使用 DataTable/资产 MCP 的原因：本任务无 `.uasset` 语义读取需求，Unity 文本资产（`.unity/.prefab`）走 git diff 与文本扫描即可，未降级。

---

## 一、已确认

### 1. reserved 字段/枚举：原号未复用，且产物确实重生成
- 权威 proto 明文：`RequestCode` = `reserved 7, 8;` + `reserved "Battle", "ClearSence";`；`ActionCode` = `reserved 31 to 41;` + 11 个旧名；`MainPack` = `reserved 13, 15;` + `reserved "battleInfo", "battle_net_sim_config";`。
- 更强证据（防「手改 generated」）：把三份 protoc 产物内嵌的 base64 `FileDescriptorProto` 解码后，MainPack 的 `reserved_range` = `{13..14, 15..16}`、`reserved_name` = battleInfo / battle_net_sim_config；RequestCode/ActionCode 的 reserved_range（7、8、31..42）与全部 reserved_name 都在。手工删类不可能产出这些 reserved 元数据，故三份 protoc 产物**确由当前 proto 重新生成**，且旧号只被 reserved、未被任何新字段/新枚举成员占用。
- 与改前对照（`git show HEAD`）：HEAD 里 ActionCode 31..41 的 11 个名字、RequestCode 7/8 名字与当前 reserved 名单逐一相同，无遗漏、无换名复用。

### 2. 大厅 DTO 保留完整
- `BattlePlayerPack`：`id=1 teamid=2 roomid=3 playername=4 hero=5 battleid=6` 全在；`MainPack.battleplayerpack=12` 保留。
- `FightPattern`：`BaoShiZhengBa=0, Sheji=1` 未漂移；`PlayerPack.fightpattern=6` 保留。
- `StartEnterBattle=30` 保留且**未被 reserved**（实测 `ReservedHasNumber(actionCode,30)==false`）。
- 幸存值域：RequestCode 0..6、ActionCode 0..30、Hero 0..19（20 项）全部原值；无重排。
- 消费者仍活：`Server/Server/Server.cs:416` 下发 `ActionCode.StartEnterBattle`，`UIMatchingPanel.cs:28` 发起 `RequestCode.Matching + StartEnterBattle`，`Server/Controller/Controllers.cs` 用 `FightPattern`/`PlayerPack.Fightpattern` 做匹配名册与房间分桶。

### 3. 四份产物确为真 generated（独立复核，非引用 PMNetVerify 结论）
- 三份 protoc 产物（`ProtobufAndNotepad/Protobuf/CSharp/SocketProto.cs`、`Client/Assets/Scripts/Server/SocketProto.cs`、`Server/Server/SocketProto.cs`）行数均 1988、sha256 均 `aabaa8c3…`、逐字节相同。
- 我自行解析 proto 文本后与产物机器对照（不依赖 PMNetVerify）：
  - protoc 产物声明恰为 7 message + 7 enum，字段号与 proto 全等；旧 14 消息 + MoveType/AttackType 标识符 0 次。
  - `SocketProto.PMNet.g.cs`：7 个 `PM*Serializer.Fields` 表、每个字段的（号/名/repeated）、`WriteTag`+`WriteSubMessage` 号、7 个 `PM*` 枚举成员值，全部与 proto 全等；多/少/错位一处即会打印不一致，实测全等。
  - `PMNetGenerated ProtoHash = 97219da6dabb…8ac1`，我重算 proto **LF 归一**后 sha256 = `97219da6…8ac1`，相等。

### 4. PMLegacyRetirementTest（禁回归门禁）主体是真门禁，非空扫描绿
- 反空洞三件套真实存在：`G2.scan-inputs-present`（.cs 计数 >0，实测 358）、`G2.stripper-anti-vacuity`（剥离 1:1 保长 + 全局代码占比 ≥0.30 + 3 处代码金丝雀）、`G2.tool-outside-scan-roots`；`N1` 缺输入必 FAIL，`N2/N3/N4/N5` 分别验「旧类复活被检出 / 注释字符串不误报 / 旧 Action 定义复活被检出 / 资产 GUID 残留被检出」。
- 扫描根 `Client/Assets` + `Server` 覆盖全部**生产** C#：我全仓枚举 `.cs`，除这两根外只有 `Client/Library/PackageCache`（Unity 包缓存）、`Tools/`（测试工具）、`ProtobufAndNotepad/`（生成器）、`HyldDS/`（构建产物）——均非生产源码。不存在「生产旧类在扫描根之外」的遗漏。
- 独立 `rg --no-ignore`（含 `Generated/`、`.g.cs`，仅排除 bin/obj）搜 19 个旧类型标识符，非注释命中仅 4 行且**全在日志字符串字面量内**（如 `"...旧 CommandManger 发送已退役"`、`"...不再拉起 BattleManger/HYLDBaoShiZhengBaManger"`）。剥离器去字符串后归零，与契约「注释/字符串不误报」一致，属设计预期，不是漏洞。
- `G6` 的 reserved 断言用自研 `ScanReserved`（独立词法），不依赖生成器；`G7` 用「本文件声明集合」反查 `global::SocketProto.*` 悬空引用；`G8` 强制资产文件数 >0 且冻结 SHA 固定。

### 5. PMNetVerify oracle 是独立 oracle，未自比较、未放宽
- 被校验对象 = PMNet 序列化器；对照基准 = **protoc 产物 `Server/Server/SocketProto.cs` + `Google.Protobuf 3.11.1`**（csproj `<Reference>`，非 PMNet 自身）。三向判定：① protoc `ToByteArray()` 与 PMNetWriter 逐字节相同；② protoc 解析 PMNet 字节重编码仍相同；③ PMNet 解析 protoc 字节重编码仍相同。`VerifyReflective` 首比即严格 `CheckBytes`，失败即 Dump，无「容错通过」。
- 唯一宽松断言在 **protoc 侧**未知字段行为（保留或丢弃二者皆可，版本无关），同段 PMNet 侧被强制「重编码精确等于已知字段字节」；截断段用 `TryGoogleSerialize/TryPmSerialize` 捕获异常做双方一致性判定，断言 `mismatch==0`，并额外断言「两种结果都出现过」防空洞。
- 反射只在测试进程（`Program.cs`）；`Client/Assets/Scripts/PMNet/**` 运行时源码无 proto 反射，只有 `ex.GetType().Name` 之类。`ProtoFileSchema` 为 PMNetVerify 自带解析器，不共用 PMNetGen 的解析器，独立性成立。

### 6. 资产解挂正确、最小、无跨资产误删
- 4 个资质的 `git diff HEAD` 均为「1 条 `m_Component` 项 + 1 个 `!u!114` 文档」：HYLDGameTest 删 `&413438274`（m_Script guid `7200a0eb9673b6e4f8cb8386cdde31db`）、HYLDTryGame 删 `&413438271`、HYLDGame(HYLD1.0/Scenses) 删 `&413438271`、Main Camera.prefab 删 `&5705895439509941000`（后三者 m_Script guid `eec213bc5141674488046b737aede2bf`）。
- 两个 GUID 与 HEAD 中 `BattleManger.cs.meta` / `HYLDCameraManger.cs.meta` 的 guid 完全一致（从 HEAD blob 读原文，非报告转述）。
- 三个被删 anchor 在现文件中出现 0 次（无悬空引用）；HYLD1.0/Scenses/HYLDGame.unity 残留的 `&413438274` 是**该文件自身另一组件**（m_Script guid `91f8c6588526dd146a4d7f802dd8cc58`），非被删文档，处理正确。
- 两 GUID 在 `Client/Assets` 的 1037 个 `*.unity/*.prefab` 中实时扫描 0 命中。
- 禁改输入/输出未被改动：`Scenes/HYLDGame.unity`、`Remake/Player.prefab`、`EditorBuildSettings.asset` 的 `git hash-object` 等于 `HEAD` blob（报告表格里「SHA 与 HEAD 不同」的表象是 `git show` 的行尾归一所致，实际内容逐字节未变）；`Resources/PMNet/**` 整个目录 git 未跟踪（构建输出），内容由 D 段未触碰。

---

## 二、高概率推断（依据 + 置信度）

- **门禁与 PMNetVerify 的实际运行结论**（报告称 28+5 全绿、498 检查 0 失败）：本轮按交付约束**未运行**，只做静态复核。两个 DLL 已编译存在；oracle/门禁逻辑逐段读过且逻辑自洽。置信度 **高**（≈0.9）它们复跑仍为绿，但「498 / 28+5」这两个数字**未被本复核独立复现**。
- **PMNet 产物非手改**：除 ProtoHash 自洽外，字段表/线格式/写盘号/枚举值与 proto 全等，手工保持四者一致等同重写生成器。置信度 **高**；`build.bat --check-only` 是唯一 100% 判据。
- **旧 Python/C++/ProtoOut 生成物残留**：`ProtobufAndNotepad/Protobuf/{ProtoOut/SocketProto.cs, Python/SocketProto_pb2.py, SocketProto_pb2.py, Cplusplus/*}` 仍含旧战斗类型（ProtoOut 32 处、两份 pb2 各 15 处），但**全仓无任何脚本/工程引用**（实测 rg 0 命中），且 build.bat 不产出它们。判定为「未被消费的历史归档」，非生产复活面。置信度 **高**。

---

## 三、无法确定（缺少证据）

- Unity 实机加载是否 0 missing script、场景打开是否报错（T-L4）——需启动 Editor，属 PENDING_USER，报告也已如实声明。
- `Docs/plans/net-architecture-migration.md` 中旧数字（27 项）与新报告（498 项）的口径差异只表示覆盖重写，未逐项核对。
- `Resources/PMNet` 三输出的 `contentDigest` 是否仍与源指纹一致（需编辑器重烘校验）。

---

## 四、真 bug（精确证据 + 最小修复）

### BUG-1（唯一实质缺陷）：门禁冻结数据拼写错误，导致一个已删旧类实际未被守

- 契约 §B 要删的类名：`HYLDBaoShiZhengBaManger`（单 D，`net-legacy-retirement-contract.md` 唯一出现 1 次）。
- `Tools/PMLegacyRetirementTest/Program.cs`：
  - `:95` `LegacyTypeTokens` 写的是 `"HYLDDBaoShiZhengBaManger"`（**双 D**）；
  - `:124` `LegacyClientFiles` 写的是 `"Client/Assets/Scripts/Server/Manger/Battle/HYLDDBaoShiZhengBaManger.cs"`（**双 D**）。
- 而实际被删文件/类（`git ls-tree HEAD` + `git show HEAD:…` 原文）：
  - `Client/Assets/Scripts/Server/Manger/Battle/HYLDBaoShiZhengBaManger.cs`（单 D，现为 ` D` 删除）；
  - 文件内声明 `public class HYLDBaoShiZhengBaManger :BattleManger`（`namespace Manger`）。
- 后果（已机器验证）：
  - `G1.client-files-and-meta-absent` 检查的是一条**在 HEAD 中从不存在的路径**（`git ls-tree HEAD` 判定 NOT-IN-HEAD），该断言无论工作区如何都恒为真 → 对这类**完全没有守**；
  - `G2` 的正则 `(?<![A-Za-z0-9_])HYLDDBaoShiZhengBaManger(?![A-Za-z0-9_])` 对真实标识符 `HYLDBaoShiZhengBaManger` 匹配结果为 **False**（已用 Python 复现），故 G2 也漏掉该类名本身。
  - 唯一残留保护：该类原继承 `BattleManger`，若「原样复活」会被 G2 的 `BattleManger` 命中；但只要复活版本不写 `:BattleManger`，门禁保持全绿。
  - 报告的「客户端 17 个旧 .cs 与其 .meta 均不存在」实际是「16 个真实路径 + 1 条幽灵路径」，属轻度误导性表述。
- 最小修复（2 行，不涉及逻辑）：
  - `Program.cs:95` `"HYLDDBaoShiZhengBaManger"` → `"HYLDBaoShiZhengBaManger"`
  - `Program.cs:124` `…/HYLDDBaoShiZhengBaManger.cs` → `…/HYLDBaoShiZhengBaManger.cs`
- 加固建议（非必须）：新增负例 `N6` 注入一个「名字不可由 `BattleManger` 推导」的旧类（如 `HYLDBaoShiZhengBaManger`），使守备非空洞；并给 `LegacyClientFiles/LegacyTypeTokens` 增加「对照 HEAD/夹具存在性」自检，杜绝同类拼写错导致真空通过。此前 `N2` 只注入 `BattleManger`，天然覆盖不到该 typo。

### 观察项（非缺陷，不建议扩大删除）
- `ClientUdp`（契约 §A 删 `Server/Server/ClientUdp.cs`）不在 `LegacyTypeTokens` 中，仅由 G1 文件路径守；当前活源码 0 引用，风险低。
- `G6.proto-lobby-numbers-frozen` 只校验「期望成员存在且号一致」，不断言「无额外成员」；`ActionCode` 的 11 个 reserved **名字**未做「不得再声明」定义级断言（protoc 会拒绝 reserved 名复用，故仍有生成期兜底）。属加固空间。
- `ProtobufAndNotepad/Protobuf/{ProtoOut,Python,Cplusplus}` 旧生成物残留（见二、高概率推断），未消费；契约只冻结「四份产物」，是否清理由主侧决定，本复核不建议顺手扩大删除。

---

## 五、建议下一步（最小）

1. 修 BUG-1 两行，并补 N6 负例（+ 可选：文件/ token 清单与 HEAD 存在性自检）。
2. 在 CI 或提交前脚本里真正复跑 `build.bat --check-only`、`PMNetVerify`、`PMLegacyRetirementTest --report`，把「报告声称的 498/28+5 绿」升级为「可复现绿」。
3. 对 `ProtobufAndNotepad/Protobuf/{ProtoOut,Python,Cplusplus}` 残留做一次显式决策（保留归档 or 删除），只需一次 grep 证明无消费者。
4. T-L4 的 Unity 实机加载与 missing-script 计数仍须实机补验（本轮与三份报告一致地未验）。
