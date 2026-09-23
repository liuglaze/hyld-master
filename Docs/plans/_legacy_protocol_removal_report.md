# E1 旧 proto 退役与真实重生成报告

范围：`Docs/plans/net-legacy-retirement-contract.md` §E 与「E 冻结补充」中的 E1 段。
即：权威 proto 收缩、非权威 proto 副本删除、真实 `build.bat` 重生成四份产物、`PMNetVerify` 改测剩余大厅 DTO。
A/B/C/D 段（Server/Client/DS/资产解挂）由其它组完成，本组只消费其结果，不改其它工具与主计划。

硬写入边界内实际改动的文件（8 条）：

| 文件 | 动作 |
|---|---|
| `ProtobufAndNotepad/Protobuf/SocketProto.proto` | 改（唯一权威源） |
| `ProtobufAndNotepad/Protobuf/CSharp/SocketProto.cs` | 重生成 |
| `Client/Assets/Scripts/Server/SocketProto.cs` | 重生成 |
| `Server/Server/SocketProto.cs` | 重生成 |
| `Client/Assets/Scripts/PMNet/Generated/SocketProto.PMNet.g.cs` | 重生成 |
| `Tools/PMNetVerify/Program.cs` | 确定性重写 |
| `Client/SocketProto.proto` | 删除（非权威副本） |
| `ProtobufAndNotepad/Protobuf/Proto/SocketProto.proto` | 删除（非权威副本） |

第 9 条路径 `Client/hyld-master/hyld-master/ProtobufAndNotepad/Protobuf/Proto/SocketProto.proto` **不存在**
（`Client/hyld-master` 目录本身不存在），按任务要求**记录而不编造**；仓库内 `find -name SocketProto.proto`
现在只剩权威源一份。

---

## 摘要（1658 字）

E1：把权威 proto 收缩为「仅剩大厅 DTO」，并用真实 build.bat 重生成四份产物。

改动：RequestCode 删 Battle=7/ClearSence=8 并 reserved 号码与名字；ActionCode 删 31..41（保 StartEnterBattle=30）并 reserved 号码与 11 个名字；MainPack 删 battleInfo=13/battle_net_sim_config=15 并 reserved 号码与名字；删 MoveType、AttackType 两个枚举、孤立 BattleRoomPack，以及从 message BattleInfo 起的 13 条旧战斗消息。幸存字段号与枚举数值一律未动：MainPack 仍为 1..12/14/16，RequestCode 0..6，ActionCode 0..30。

类型数：message 21→7，enum 9→7；proto 352→194 行（工作区 CRLF 6952→4175 字节）；protoc 产物 6085→1988 行，PMNet 产物 3674→1230 行。三份 protoc 产物字节完全相同（sha256 aabaa8c3…）。

非权威副本：删除 Client/SocketProto.proto 与 ProtobufAndNotepad/Protobuf/Proto/SocketProto.proto（普通文件系统删除，未暂存）；Client/hyld-master/... 路径不存在，已记录；未触碰 google/protobuf 第三方。

真实重生成：cmd /c build.bat（protoc 3.11.1 版本断言通过、生成 3 份 + PMNet 1 份、枚举 7/消息 7、同步校验通过）→ build.bat --check-only 通过（不写盘）。

PMNetVerify 确定性重写：删掉旧战斗热路径场景，改为「剩余 7 个大厅 DTO 的独立 protoc 字节 oracle」。消息/枚举集合、每个字段号/repeated/线格式、每个枚举成员数值都在运行时从权威 proto 解析并与 PMNet 生成产物反射对照（不预设数量），任一退役类型复活、号码重排、数值漂移都会失败。字节口径：protoc ToByteArray 与 PMNetWriter 必须逐字节相同，再用 protoc Parser 解析 PMNet 字节重编码、用 PMNet ParseFrom 解析 protoc 字节重编码，双向仍须字节相同——永远以 protoc 为 oracle，绝不 PMNet 自比较。覆盖面：全字段嵌套、repeated×0/1/64、UTF8 中文/空/非 BMP(1F600)/内嵌 NUL、负 int32 的 10 字节 varint 与 int64/int32 极值、未知字段跳过（含被冻结的 13/15 槽位）、59 个截断前缀的接受/拒绝判定与 protoc 完全一致（接受 8、拒绝 51）。覆盖边界诚实声明：旧战斗/浮点路径已删除，不再断言，剩余 DTO 无 float 字段，本轮没有 float 覆盖面。

门禁结果：PMNetLangCheck 编译 0 错误；PMNetVerify 编译 0 错误并运行 498 项检查 / 0 失败（exit 0）；Server/Server.csproj、Tools/PMClientCheck、Tools/PMUnityGlueCheck、Tools/PMServerSmokeTest 均 0 错误（后两者仅编译未运行）。

未验证：未启动 Unity，T-L4 GUID/资产、Unity 宿主编译与 R4/R5/R6 实机回归均未做。

纪律：无 git add/rm/staged/commit/reset/checkout；未改其它工具与主计划。

---

## 1. 权威 proto 改动明细

### 1.1 删除并显式 reserved（号码不可重用）

| 位置 | 删除的定义 | 冻结写法 |
|---|---|---|
| `enum RequestCode` | `Battle=7`、`ClearSence=8` | `reserved 7, 8;` + `reserved "Battle", "ClearSence";` |
| `enum ActionCode` | `BattleReady=31` … `BattleSetNetSimConfig=41`（共 11 项） | `reserved 31 to 41;` + 11 个名字的 `reserved "...";` |
| `message MainPack` | `BattleInfo battleInfo=13`、`BattleNetSimConfig battle_net_sim_config=15` | `reserved 13, 15;` + `reserved "battleInfo", "battle_net_sim_config";` |
| `ActionCode.StartEnterBattle=30` | **保留**（大厅匹配/新 offer 需要） | 未冻结，且断言 30 未被 reserved |

`protoc 3.11.1` 实际接受 enum 的 `reserved` 号码与名字（生成成功即证据），PMNetGen 的 `ProtoParser` 对
`reserved` 语句是「整条跳过、不报错」（`Tools/PMNetGen/ProtoParser.cs:257,297`），因此生成器无需改动。

### 1.2 整体删除的类型（13 条旧战斗消息 + 1 个孤立房间包 + 2 个旧枚举）

- 旧战斗消息（自 `message BattleInfo` 起，至文件末尾）：`BattleInfo`、`BattleNetSimConfig`、`BattleClientInput`、
  `BattleServerUpdate`、`BattleFrame`、`PlayerFrameInput`、`ClientAttack`、`ServerAttack`、`ClientMove`、
  `MoveAckResult`、`HitEvent`、`AttackAck`、`AuthoritativePlayerState`。
- 孤立消息：`BattleRoomPack`（活源码零消费者，仅生成物里有；E 冻结补充已核定）。
- 旧枚举：`MoveType`、`AttackType`。

### 1.3 幸存清单（号码/数值一律未重排）

- message（7）：`MainPack`、`ChatPack`、`LoginPack`、`RoomPack`、`BattlePlayerPack`、`FriendRoomPack`、`PlayerPack`。
- enum（7）：`RequestCode`、`ActionCode`、`ReturnCode`、`RoomState`、`PlayerState`、`Hero`、`FightPattern`。
- 幸存字段号：`MainPack` = 1 requestcode / 2 actioncode / 3 returncode / 4 loginpack / 5 str / 6 roompack /
  7 friendspack / 8 userInfopack / 9 friendroompack / 10 playerspack / 11 chatpack / 12 battleplayerpack /
  14 timestamp / 16 request_id；其余消息字段号见 §4.3 的机器对照（全部与改前一致）。
- 幸存枚举数值：`RequestCode` 0..6、`ActionCode` 0..30、`ReturnCode` 0..4、`RoomState` 0..2、`PlayerState` 0..4、
  `Hero` 0..19、`FightPattern` 0..1，全部原值。
- 未改「新 PMR3 声明」（`Client/Assets/Scripts/PMR3/**` 与 PMNet 声明文件本组只读未动）。

编辑方式：Python 按锚点逐条替换（每条断言命中次数为 1），保留原 UTF-8 无 BOM + CRLF 与既有中文注释；
未整文件重写，未改任何保留段的字节。

---

## 2. 非权威 proto 副本处置

真实命令与结果：

```text
$ find . -name "SocketProto.proto" -not -path "./.git/*"
./Client/SocketProto.proto
./ProtobufAndNotepad/Protobuf/Proto/SocketProto.proto
./ProtobufAndNotepad/Protobuf/SocketProto.proto

$ grep -rn "Protobuf/Proto/SocketProto|Client/SocketProto.proto" --include=*.bat --include=*.py --include=*.csproj --include=*.cs --include=*.sh --include=*.ps1 --include=*.json .
(无输出：没有任何工具/脚本引用这两个副本)

$ rm -v Client/SocketProto.proto ProtobufAndNotepad/Protobuf/Proto/SocketProto.proto
removed 'Client/SocketProto.proto'
removed 'ProtobufAndNotepad/Protobuf/Proto/SocketProto.proto'
```

删除后 `find` 只剩权威源一份；`ProtobufAndNotepad/Protobuf/Proto/` 变为空目录（未删目录本身，不影响构建）。
`ProtobufAndNotepad/Protobuf/google/**`（第三方 protobuf 模板）**未触碰**。
`Client/hyld-master/hyld-master/ProtobufAndNotepad/Protobuf/Proto/SocketProto.proto` **不存在**（已记录）。

---

## 3. 真实重生成（build.bat）

### 3.1 生成

```text
$ cd ProtobufAndNotepad/Protobuf && cmd.exe //c build.bat
[build] repo root : D:\UGit\hyld-master\ProtobufAndNotepad\Protobuf\..\..
[build] mode      : generate
[build] protoc 版本校验通过: libprotoc 3.11.1
[build] 编译 PMNetGen...
已成功生成。 0 个警告 0 个错误
[build] protoc 生成中...
[PMNetGen] 行尾已归一为 CRLF（72440 -> 74428 字节）: ...\CSharp\SocketProto.cs
[PMNetGen] 行尾已归一为 CRLF（72440 -> 74428 字节）: ...\Client\Assets\Scripts\Server\SocketProto.cs
[PMNetGen] 行尾已归一为 CRLF（72440 -> 74428 字节）: ...\Server\Server\SocketProto.cs
[build] protoc 产物同步校验通过
[build] PMNet 生成中...
[PMNetGen] 已生成 ...\Client\Assets\Scripts\PMNet\Generated\SocketProto.PMNet.g.cs
[PMNetGen] 枚举 7 个，消息 7 个，产物 37634 字符
[PMNetGen] 同步校验通过: ...SocketProto.PMNet.g.cs
[build] OK
（退出码 0）
```

要点：protoc 版本断言（3.11.1）通过 —— **未换 protoc 版本**；三份 protoc 产物与一份 PMNet 产物都是
真实重新生成，没有手改 generated；PMNetGen 自报「枚举 7 个，消息 7 个」。

### 3.2 生成确定性校验（不写盘）

```text
$ cmd.exe //c build.bat --check-only
[build] mode      : check-only (no files will be written)
[build] protoc 版本校验通过: libprotoc 3.11.1
[build] protoc 产物同步校验通过
[PMNetGen] 同步校验通过: ...\SocketProto.PMNet.g.cs
[PMNetGen] 枚举 7 个，消息 7 个，产物 37634 字符
[build] OK
（退出码 0）
```

### 3.3 产物一致性与指纹

```text
$ wc -l
 1988 ProtobufAndNotepad/Protobuf/CSharp/SocketProto.cs
 1988 Client/Assets/Scripts/Server/SocketProto.cs
 1988 Server/Server/SocketProto.cs
 1230 Client/Assets/Scripts/PMNet/Generated/SocketProto.PMNet.g.cs

$ cmp CSharp/SocketProto.cs Server/Server/SocketProto.cs && cmp Client/.../Server/SocketProto.cs Server/Server/SocketProto.cs
IDENTICAL   （三份 protoc 产物逐字节相同）

$ sha256sum（工作区 CRLF 文件）
5ca84d78934e5da262d03bbe7f88f063a30673e24dbf78b94cf1fd8585e7bfb7  ProtobufAndNotepad/Protobuf/SocketProto.proto
aabaa8c3ecee0affbe01829aad7ebba82dc7fb780f833e203ca8186ca0fd39cb  CSharp / Client / Server 三份 SocketProto.cs（同一值）
43064803d746e8c7fc352086494ad79f85c487c5a88af6b87030bf12fa5339ba  Client/.../PMNet/Generated/SocketProto.PMNet.g.cs
2db23be9d535cf35f320fef0a48d1253321dc1d1c6fd3dd81111371a6506fd91  Tools/PMNetVerify/Program.cs
```

`PMNet` 生成物内嵌的 `PMProtocolInfo.ProtoHash = "97219da6dabb691f7dc363ebf5bc87ee9125b374e942e156c385b53e00cd8ac1"`，
等于权威 proto **LF 归一后**的 SHA-256（与 `sha256sum` 的 5ca84d… 不等，是因为后者按工作区 CRLF 字节计算）；
该等式已由 §6 的 J 段门禁在运行时重算并断言通过，可用于判定「proto 改了但 PMNet 产物忘了重新生成」。

---

## 4. schema 前后对照与稳定性证明

### 4.1 规模

字节口径说明：下表"LF 字节"= 把行尾归一到 LF 后的 UTF-8 字节数（`git show HEAD:path` 的口径，
可与改前直接对比）；"工作区 CRLF 字节"= 磁盘上实际文件的字节数。两者差额恰为 CR 数量。

| 指标 | 改前（HEAD blob，LF） | 改后（LF 字节） | 改后（工作区 CRLF 字节） |
|---|---|---|---|
| 权威 proto 行数 / 字节 | 352 / 6600 | 194 / 3981 | 4175 |
| `message` 数 | 21 | 7 | — |
| `enum` 数 | 9 | 7 | — |
| 一份 protoc 产物行数 / 字节 | 6085 / 217755 | 1988 / 72440 | 74428 |
| PMNet 产物行数 / 字节 | 3674 / 115495 | 1230 / 38290 | 39520 |
| `Tools/PMNetVerify/Program.cs` 行数 / 字节 | 869 / 33144 | 1670 / 72145 | 73815 |

改后行数用 `wc -l` 实测；LF 字节由去掉 CR 后重新计数得到；三份 protoc 产物字节完全相同（见 §3.3）。

### 4.2 幸存字段号未重排（改前 = 改后）

`MainPack`：1,2,3,4,5,6,7,8,9,10,11,12,14,16（13、15 从「有定义」变为「reserved」）。
`ChatPack`：1,2,3；`LoginPack`：1,2；`RoomPack`：1,2,3,4；`BattlePlayerPack`：1..6；`FriendRoomPack`：1,2,3,4；`PlayerPack`：1..6。

### 4.3 机器判据（不是人肉比对）

`PMNetVerify` 的 A 段在运行时做三件事：

1. 从权威 proto 文本解析出 message / enum 集合、每个字段的（号、名、repeated、类型）、每个枚举成员的（名、值）；
2. 用反射取出 PMNet 生成产物的 `PM<X>Serializer.Fields`（`PMNetFieldDesc[]`）与 `PM<X>` 枚举成员，做**双向**对照：
   集合相等、字段数量相等、逐个字段号↔名字↔repeated↔线格式相等、逐个枚举成员名↔数值相等；
3. 断言退役类型名不在 proto 中，reserved 覆盖 7/8、31..41、13/15 与全部 11 个旧 Action 名，且 `MainPack`
   的描述符表里**不存在** 13/15（即 reserved 号码没有被复用）。

因此「号码不许重排 / 枚举数值不许漂移 / 旧类型不许复活」在本组是可复跑的机器判据，而不是报告里的一句声明。

---

## 5. PMNetVerify 确定性重写说明

### 5.1 为什么重写

旧版（原 869 行）的场景全部围绕旧战斗热路径（`BattleInfo.server_update.frames[].player_states`、`ClientMove` 的
float 三元组、`MoveAckResult` 的修正位置/速度、`HitEvent` 命中坐标、`BattleNetSimConfig` 等）。
这些消息已随 E1 删除，旧测试必然编译失败；即使勉强保留，也等于对「已经不存在的覆盖」说谎。
本次按任务要求**确定性重写为剩余大厅 DTO 的 protoc 字节 oracle**，并保留原有「退出码 0/1 + 计数汇总」的用法。

### 5.2 oracle 口径（不与 PMNet 自比较）

每个场景都以 `Server/Server/SocketProto.cs`（protoc 生成）为唯一基准：

1. 同一组取值，`protoc.ToByteArray()` 与 `PMNetWriter` 产物必须**逐字节相同**；
2. 用 protoc 的 `MessageParser.ParseFrom` 解析 PMNet 写出的字节，再编码必须仍与 PMNet 字节相同；
3. 用 PMNet 的 `ParseFrom` 解析 protoc 写出的字节，再编码必须仍与 protoc 字节相同；
4. 失败时打印期望/实际十六进制与首个差异字节偏移。

未知字段与截断场景额外做了版本无关的稳健处理（见 §5.4 / §5.5）。

### 5.3 覆盖分组（运行输出 11 段，合计 498 项检查）

| 段 | 内容 |
|---|---|
| A | proto 结构门禁：16 个退役类型缺席、reserved 号码/名字冻结、幸存值域（RequestCode 0..6、ActionCode 0..30）、生成集合与字段号/枚举值反射对照 |
| B | 空消息：proto3 全默认必须 0 字节，**消息集合运行时来自 proto（本次 7 个），不写死数量** |
| C | `MainPack` 全字段嵌套（12 个幸存字段 + 全部子消息，含 6 条战斗名册 `BattlePlayerPack`） |
| D | 6 个子消息直测（`ChatPack` / `LoginPack` / `RoomPack` / `BattlePlayerPack` / `FriendRoomPack` / `PlayerPack`） |
| E | 字符串边界 8 组：中文、空串、非 BMP `U+1F600`、内嵌 NUL、纯 NUL、混合串、300 字节 ASCII、384 字节中文 |
| F | 标量边界：负 int32 的 10 字节 varint、`int.MinValue/MaxValue`、`long.MinValue/MaxValue`、`-1`、未知枚举值 1000、负枚举值 |
| G | repeated 规模 0 / 1 / 64 × 5 个 repeated 字段（`RoomPack`、`BattlePlayerPack`、`FriendRoomPack`、`PlayerPack`、`Friendspack`） |
| H | 未知字段跳过：varint / fixed32 / fixed64 / length-delimited 四种未知字段 + **被冻结的 MainPack 13/15 槽位**（内容刻意做成旧战斗包形状） |
| I | 截断：59 个前缀的接受/拒绝判定必须与 protoc 完全一致 |
| J | 生成产物门禁：4 份产物存在、不含 16 个退役标识符（词法边界匹配，不会把 `BattlePlayerPack` 误判成 `BattleInfo`）、`ProtoHash` 与 proto 内容哈希自洽 |
| K | 覆盖门禁：proto 里 7 个 message + 7 个 enum 必须全部被断言过，否则失败 |

### 5.4 未知字段

protoc 侧实测行为是「**保留并原序写回**」（门禁以 `[INFO]` 记录该事实，并同时接受「保留或丢弃」两种结果，
避免绑定 Google.Protobuf 的具体版本行为），且断言「已知字段的文本投影不变」；
PMNet 侧重编码必须**精确回到已知字段字节**（未知字段被跳过，而不是被合并进任何已知字段）。
被冻结的 13/15 槽位用「像旧 BattleInfo 一样的字节」注入，PMNet 仍必须跳过 —— 这直接证明旧槽位没有被暗中复用。

### 5.5 截断

对同一 `MainPack` 载荷的全部 59 个前缀逐字节截断，逐个比较「双方是否抛异常」：
实测**双方接受 8 个、双方拒绝 51 个，判定完全一致**（另断言「字段中途截断（末尾少 1 字节）双方都拒绝」，
并断言两种结果都出现过，避免空洞通过）。这比只测「某一个截断样本被拒」更强。

### 5.6 覆盖边界：不虚构还覆盖旧 float / 战斗

旧 `ClientMove` 的 `float move_x/y`、`MoveAckResult` 的 `correct_pos_*` / `correct_vel_*`、`HitEvent` 的
`hit_pos_*`、`BattleNetSimConfig.drop_rate` 等浮点场景**已随 E1 删除**，本程序**不再对其做任何断言**，
并在输出中显式打印该覆盖边界。剩余 7 个大厅 DTO 不含任何 float/double 字段，因此**本轮不存在 float 覆盖面**，
这是删除的直接后果而非遗漏。任务明确要求「不虚构还覆盖」，此处按事实陈述。

---

## 6. 自动门禁清单（真实命令与退出码）

| # | 命令（工作目录 `D:/UGit/hyld-master`） | 结果 |
|---|---|---|
| 1 | `cd ProtobufAndNotepad/Protobuf && cmd.exe //c build.bat` | 退出码 0；protoc 3.11.1 断言通过；4 份产物真实重生成；同步校验通过 |
| 2 | `cmd.exe //c build.bat --check-only` | 退出码 0；不写盘；protoc 与 PMNet 产物均与权威 proto 同步 |
| 3 | `dotnet build Tools/PMNetLangCheck/PMNetLangCheck.csproj -c Release -v q --nologo` | **0 错误**（netstandard2.0 + C# 7.3 语言面天花板） |
| 4 | `dotnet build Tools/PMNetVerify/PMNetVerify.csproj -c Debug -v q --nologo` | **0 错误**；4 个 `CS8981` 警告来自 protoc 产物的 `pb/pbc/pbr/scg` 别名（既有，与本次改动无关） |
| 5 | `dotnet Tools/PMNetVerify/bin/Debug/net8.0/PMNetVerify.dll` | 退出码 0；**498 项检查 / 0 项失败**；对照基准 `Google.Protobuf 3.11.1.0` |
| 6 | `dotnet build Server/Server.csproj -c Debug -v q --nologo` | **0 错误** / 8 警告 |
| 7 | `dotnet build Tools/PMClientCheck/PMClientCheck.csproj -c Debug -v q --nologo` | **0 错误**（仅编译，未运行其运行期场景） |
| 8 | `dotnet build Tools/PMUnityGlueCheck/PMUnityGlueCheck.csproj -c Debug -v q --nologo` | **0 错误**（Unity 胶水可编译性门禁，含 `Server/SocketProto.cs`） |
| 9 | `dotnet build Tools/PMServerSmokeTest/PMServerSmokeTest.csproj -c Release -v q --nologo` | **0 错误**（仅编译，未运行：其运行需先起服务端） |

第 6/7/8/9 项说明：任务预期「client/server 编译因另组尚未完成而暂留错误」，实测**四项全部 0 错误**。
原因不是本组放宽了口径，而是 E2 组已把 `UISliderPanel` / `UIStartMainPanel` / `RequestManger` 的旧
`BattleReview` / `ClearSence` 引用删掉：本组复扫
`ActionCode.Battle*|ActionCode.ClearSence*|RequestCode.Battle|RequestCode.ClearSence` 在 `Client` / `Server` / `Tools`
的 C# 源码里，只剩 1 条**注释**（`Server/Controller/ControllerManger.cs:31` 的说明文字）与另一组新门禁的**报告字符串**。
因此本组不需要「创建旧 stub 修表面」，也不存在本组引入的、需要主侧留意的编译错误。

---

## 7. 未验证 / 遗留

- **未启动 Unity**：没有打开编辑器、没有跑 Unity 宿主 Player。以下均未验证，本报告不作任何「已验证」表述：
  - T-L4：已删类对应资产的 GUID 实时反查与场景加载（`HYLDGameTest.unity` / `HYLDTryGame.unity` /
    `HYLDGame.unity` / `Main Camera.prefab`）；
  - T-L5 的 Unity 侧：Unity 2019.4 宿主编译（`Client` 工程）、R4/R5/R6 实机回归；
  - T-L2 中「真实 Unity 2019 宿主编译」一项（本组只覆盖了非 Unity 的 `PMClientCheck` / `PMUnityGlueCheck` 编译）。
- 两份非权威副本的**历史内容**（含旧战斗消息）已随删除消失；主计划
  `Docs/plans/net-architecture-migration.md` 里「非权威副本已漂移」的表述**本次未同步修改**（任务明令不改主计划），
  如实记录该文档口径已与现实不同。
- `Tools/PMLegacyRetirementTest`（另一组在本次窗口内新增的静态禁回归门）**未由本组运行或修改**。
  仅读代码核对其 G6 期望与本权威 proto 一致：`RequestCode 7/8`、`ActionCode 31..41`、`MainPack 13/15` 与
  `battleInfo` / `battle_net_sim_config` 两个名字必须 reserved —— 与 §1 的实现完全对齐。
- 全仓**源码级**词法扫描（「活源码里不存在旧类型/协议/7777 监听」）属该工具与主侧的职责；
  本组只在 `PMNetVerify` 内做了 **proto 与生成产物层面**的禁回归断言（A / J 段），不冒充全仓扫描。

---

## 8. 纪律

- 全程**未执行**任何 `git add` / `git rm` / 暂存 / `git commit` / `git reset` / `git checkout`：
  实测 `git diff --cached --name-only` 为空。
- 两处副本删除使用普通文件系统 `rm`（`git status` 显示为未暂存的 ` D`），不使用 `git rm`。
- 编码：权威 proto 保持原 UTF-8 **无 BOM** + CRLF；`Program.cs` 由工具写入 LF 后统一归一为 CRLF
  （归一后再编译，确保与仓库追踪口径一致）；生成的 `SocketProto.cs` 系列由 build.bat 自行归一到 CRLF
  （日志可见「72440 -> 74428 字节」）。
- 未修改 `Docs/plans/net-architecture-migration.md`、其它工具、其它组的报告与资产文件。

---

## 9. 与其它组的关系（并行窗口）

- 本组只改 E1 边界内的 8 个文件；工作区里其它 M/D（`Client/Assets/**`、`Server/Controller/**`、
  `Server/DS/**`、`Docs/plans/_legacy_*`）来自 A/B/C/D 与 E2 组的并行工作，本组未触碰。
- E 冻结补充记录的子代理误用 `git rm` 暂存 8 文件一事，本组未重复：本组所有删除都是工作区普通删除，
  未暂存任何文件；报告亦不采纳「用暂存记录删除」的建议。
- 另一组新增的 `Tools/PMLegacyRetirementTest` 与本组 `Tools/PMNetVerify` 分工不同、互不重复：
  前者做全仓静态事实（文件 / 词法 / GUID / SHA / 声明），后者做运行时字节等价与 proto 结构一致性。
