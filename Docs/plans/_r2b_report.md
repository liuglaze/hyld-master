# R2-B 报告：运行时属性复制层（M06 最小）+ 离线门禁

- 任务类型：comprehensive（R2 的 B 块，`net-r2-codegen-contract.md` §7）
- 仓库：`D:/UGit/hyld-master`（注意：委派提示里的 `D:\UE_Project\ProjectMecury\...` 路径不存在，实际工程在 `D:/UGit/hyld-master`；写入边界按该仓库的相对路径解释）
- 结论：**门禁 129 项断言 0 失败；3 个注入缺陷全部被抓到；三条验收命令全部跑通**。未修改任何已存在文件，只新增 `Replication/` 目录 5 个源文件 + 5 个 `.meta` + 新门禁工程。

---

## 1. 已检查范围

### 1.1 必读文档（按委派指定顺序，全部完整读完）

| # | 文档 | 读到的关键内容 |
|---|---|---|
| 1 | `AGENTS.md` | 仓库入口：客户端/服务端/迁移计划三份文档的路由 |
| 2 | `Client/Assets/AGENTS.md` | 客户端全貌；§1.1 进程形态判定；§3 战斗主链路；§5 关键文件索引 |
| 3 | `Server/AGENTS.md` | 服务端全貌；§3.1 战斗系统 7 文件；§4.3 帧循环 |
| 4 | `Docs/plans/net-architecture-migration.md` | §3.9.3 的 M06 行（落点 `PMNet/Replication/`、职责=属性版本/脏位/每连接 baseline/量化/条件/OnRep/FastArray/相关性/休眠/频率/预算，**明确"只提供权威状态传递，不执行回滚"**）、§3.9.4「可靠性与复制」段（"每连接 baseline 只能前进到已确认版本。旧 ACK 不能清除新修改的脏位"）、§5 的 R2 行、§6.1 的 T41 行（不同基线最终一致 / 旧 ACK 不清新脏 / 条件跃迁补发 / Create 原子性 / 无幽灵对象） |
| 5 | `Docs/plans/net-r0-contract.md` | **§2.4 D-R0-12..18**（混合方案：Iris 成员级掩码 + legacy 每连接基线；标脏后仍要比较；8 项条件按 legacy 口径；条件跃迁全掩码；初始状态全量；复制队列有界）、**§5 复制与生命周期契约**（"每连接基线：每连接记录已确认版本；ACK 只能前进，旧 ACK 不得清除新脏位"等 13 行）、§3.5 复制描述符（`PMPropertyDescriptor` / `PMReplicationDescriptor`）、§8 T41 行、§9 待收敛参数（每帧调度 150、每对象在途 65535、MTU） |
| 6 | `Docs/plans/net-r2-codegen-contract.md` | §1 冻结接口（含两处已知偏差与 `PMCond` 的 8 项取值）、§3 协议摘要（`ProtocolHash` 与"不一致即不兼容"）、§7 B 块边界（写路径 = `Client/Assets/Scripts/PMNet/Replication/**` + `Tools/PMReplicationTest/**`；门禁必须用**手写描述符**）、§8 待收敛参数（单包属性数、字符串 1024 字节、每对象在途） |

### 1.2 实际读过的已落地代码（不是文档）

| 文件 | 用它做了什么 |
|---|---|
| `Client/Assets/Scripts/PMNet/Declarations/PMNetDeclarations.cs` | 输入契约：`PMPropertyDescriptor`（`PropertyId/Condition/MaskOffset/MaskBitCount/QuantizerId/OnRepMethodId/Writer/Reader/PushBased/MemberName`）、`PMReplicationDescriptor`（`ClassId/Properties/ChangeMaskBitCount/HasConditionalMask/ProtocolHash`）、`PMNetRegistry`（`RegisterClass/RegisterRpc/Seal/TryGetClass/TryGetRpc`） |
| `Client/Assets/Scripts/PMNet/Declarations/PMStableHash.cs` | 描述符的 `ProtocolHash` 用 `ClassProtocolHash(classId, props)` 计算，与生成器同源 |
| `Client/Assets/Scripts/PMNet/PMNetObject.cs` | `Dirty`（`PMDirtyTracker`）、`GetLifetimeReplicatedProps()`、`ShouldReplicateProperty(propertyIndex, connection)`、`GetNetConnection()`、`OwnerConnection`、`Role`、`State`、`MarkPropertyDirty` |
| `Client/Assets/Scripts/PMNet/PMNetProperty.cs` | `PMDirtyTracker` **位宽固定 64**、`MarkAll()` 置满 `ulong.MaxValue`、`ClearBits(ulong)`；`PMRepList` / `PMLifetimeProperty` |
| `Client/Assets/Scripts/PMNet/PMWireType.cs` | `PMCond` 8 项与 UE `ELifetimeCondition` 同值（`None=0/OwnerOnly=2/SkipOwner=3/SimulatedOnly=4/AutonomousOnly=5/Custom=8/Dynamic=14/Never=15`） |
| `Client/Assets/Scripts/PMNet/World/PMNetWorld.cs` | M04 运行时：`Spawn/DestroyObject/AddConnection/BuildLifecycleBatch/OnLifecycleMessage/TryFind`；确认了「初始状态与创建同包」（`BuildLifecycleBatch` 把 `OnSerializeInitialState` 塞进 Create 记录） |
| `Client/Assets/Scripts/PMNet/PMNetIdentity.cs` | `PMNetId` / `PMSession` / `PMFrameId` / `PMTimeStep`（本层只用 `NetId.Value` 做键） |
| `Client/Assets/Scripts/PMNet/PMNetConnection.cs` | `ConnectionId` / `IsReady` / `Send(byte[], PMRpcReliability)` / `TryGetViewerLocation` |
| `Client/Assets/Scripts/PMNet/PMNetWriter.cs`、`PMNetReader.cs` | 线格式原语（varint/fixed32/fixed64/length-delimited/`ReadRawBytesCopy`） |
| `Client/Assets/Scripts/PMNet/World/PMNetObjectLifecycle.cs` | `PMObjectEventKind` / `PMNetObjectState`（对象是否 Active 决定是否参与复制） |
| `Client/Assets/Scripts/PMNet/PMRpc.cs` | `PMRpcReliability.Unreliable`（属性更新走不可靠域） |
| `Tools/PMNetWorldTest/PMNetWorldTest.csproj` | 门禁工程写法（`EnableDefaultCompileItems=false` + `<Compile Include="..\..\Client\Assets\Scripts\PMNet\**\*.cs">`） |
| `Tools/PMNetLangCheck/PMNetLangCheck.csproj`、`Tools/PMClientCheck/PMClientCheck.csproj` | 语言面/API 面天花板（netstandard2.0 + C# 7.3；PMClientCheck 含 Unity 桩 + Google.Protobuf 引用） |

### 1.3 证据附录

`D:/hyld-refactor-survey/R0_3_replication.md`：只读到 §I 的 C-1..C-20 与 C-29..C-38 清单，另加 §B（baseline 结构）、§C.1..C.5（条件族）、§E（Dormancy/Relevancy）、§F（频率调度）作为上下文。

### 1.4 哪一节决定了实现方案（逐条对应）

| 决定 | 依据 |
|---|---|
| 基线学 legacy（每连接已确认版本），掩码学 Iris（成员级位掩码） | D-R0-12 / §5「每连接基线」行 / C-8（legacy 有 per-connection baseline，Iris 默认没有；Iris 的 per-connection 只有"待发 changemask + 在途记录"） |
| 标脏 ≠ 发送；必须与基线比较，值未变 ⇒ 空掩码不下发 | D-R0-13 / C-5（"标脏但值未变 → 断言比较后掩码为空"）/ C-4（Iris 兼容层丢弃 RepIndex ⇒ 我们**不做**该退化） |
| 条件求值按 **legacy** 口径，不按 Iris | D-R0-14 明示"以 legacy 口径为准" / C-14（两套口径不等价，必须二选一） |
| 条件跃迁必须全掩码置脏 | D-R0-15 / C-17（`ApplyConditionalsToChangeMask`：条件由不满足变满足 ⇒ `SetBit` 该成员全部掩码位）/ C-18（条件不满足 **不丢值**） |
| `Custom` / `Dynamic` 的开关是**每对象共享**（非逐连接） | C-15（legacy `ActiveState` 是每对象）/ C-16（Iris `SetPropertyDynamicCondition(ObjectIndex, Owner, RepIndex, bool)`）/ C-16 的"改回 ⇒ 强制重发一次当前值" |
| 初始状态/基线缺失 ⇒ 全量 | D-R0-12 / D-R0-16 / C-12（创建头与初始状态同 batch） |
| 每帧每连接调度上限取 150 | C-37（项目 `net.Iris.MaxScheduledObjectsPerFrame = 150`）/ R0 §9 |
| 在途记录必须有界、解析必须无界输入安全 | D-R0-18 / C-9（UE `MaxReplicationRecordCount = 65535`）/ C-37 |
| 脏位清理必须"所有连接都追平"才能做 | §5「ACK 只能前进，旧 ACK 不得清除新脏位」+ C-8（legacy 的 ack 是**包级**、退休是逐条 changelist）+ C-11（"已被更新的在途记录覆盖的位要剔除"） |

---

## 2. 实现概要

### 2.1 新增文件（全部在硬边界内）

| 文件 | 行数 | 职责 |
|---|---|---|
| `Client/Assets/Scripts/PMNet/Replication/PMReplicationTypes.cs` | 612 | `PMRepViewRole`、`PMRepMessageKind`、`PMRepProtocol`、`PMRepOptions`、`PMRepStats`、`PMRepMask`、`PMRepConditions`、`PMRepUpdateRecord`、`PMRepAck`、`PMRepMessage`、`PMRepInflight` |
| `Client/Assets/Scripts/PMNet/Replication/PMRepConnectionState.cs` | 223 | `PMRepObjectEntry`（每对象共享：描述符/Custom/Dynamic）、`PMRepConnectionState`（每连接基线：`AckedVersion`/`Baseline[]`/`ConditionActive[]`/`ForceInclude[]`/`Inflight`） |
| `Client/Assets/Scripts/PMNet/Replication/PMReplicationWriter.cs` | 141 | 线格式编码（Update / Ack） |
| `Client/Assets/Scripts/PMNet/Replication/PMReplicationReader.cs` | 188 | 线格式解码（有界、整条丢弃语义） |
| `Client/Assets/Scripts/PMNet/Replication/PMReplicationChannel.cs` | 1387 | 调度核心：`AddConnection/RegisterObject/RegisterOnRepDispatcher/SetCustomConditionActive/SetDynamicCondition/Tick/BuildUpdate/OnAck/OnLoss/OnMessage/BuildAckMessage` + 门禁查询接口 |
| `Tools/PMReplicationTest/PMReplicationTest.csproj` | 42 | 门禁工程（net8.0；`Compile Include ..\..\Client\Assets\Scripts\PMNet\**\*.cs` 并 `Exclude` `Generated\**`，避开 Google.Protobuf） |
| `Tools/PMReplicationTest/Program.cs` | 1172 | 129 项断言 + 3 个缺陷注入的负向验证 |
| 5 个配套 `.meta` | 各 243 B | 无 BOM + LF；GUID 全局唯一（见 §3.4） |

**未修改任何已存在文件**（含 `PMNet/**` 既有文件、`Tools/PMDeclModel/**`、`Tools/PMNetGen/**`、`Docs/plans/*.md`）。

### 2.2 线格式（消息头带魔数 + 协议版本）

```
Update: 0xA7 | ver=1 | kind=1 | recordCount
        record*: netId | version | maskByteCount | mask[..]
                 ( propertyId | valueByteCount | value[..] )*
Ack:    0xA7 | ver=1 | kind=2 | ackCount
        ack*:    netId | version
```

- 掩码位序：第 i 位 = 描述符属性表下标 i（= `MaskOffset`），字节内小端。
- 每个值带一层 **varint 外长度 + 属性 ID**：前者让"切出该值的字节区间再交给描述符 Reader"成为可能（分层、可单测），后者让"两端描述符不一致"变成**当场可判定**的协议错误（D-R0-46 的运行期兜底），而不是一路错位。
- 解码侧的三条硬要求：不信任输入（异常 → `false` + 错误串，不穿透）、有界（记录数/属性数/掩码字节数都有上限）、整条丢弃（不部分采纳，对应 C-7 的"接收端停在发送端从未存在过的状态"）。

### 2.3 七条要求的落地位置

| 要求 | 落地 |
|---|---|
| 1. 每连接基线；ACK 只能前进；旧 ACK 不得清除新脏位 | `PMRepConnectionState.AckedVersion` + `PMReplicationChannel.OnAck` + `IsAckStale`（默认 `incoming <= acked` 即过期）+ `TryClearSettledDirty`（**只有当所有连接都已追平该属性的当前值才清脏位**）。断言 B1–B9 |
| 2. 变更掩码（对象脏 + 成员级掩码比较；值未变不发） | `NeedsWork`（脏位/基线缺失/强制补发/条件跃迁 ⇒ 才进入比较）+ `ScanAndAppend`（逐槽位 `HasValueChangedSinceBaseline` 字节比较 ⇒ 置位）+ `PMRepMask`。断言 C0–C5 |
| 3. 8 项复制条件 × 角色组合（表驱动） | `PMRepConditions.Evaluate(condition, isOwner, isSimulated, customActive, dynamicResolved)`；角色由 `ResolveViewRole`（默认：无连接⇒Authority、拥有者⇒Autonomous、其余⇒Simulated），可用 `ViewRoleResolver` 覆盖。断言 D1–D33（11 行 × 3 角色）+ D-cross（与冻结的 `ShouldReplicateProperty` 交叉校验 16 项）+ D-int（OwnerOnly 端到端 4 项） |
| 4. 条件跃迁全掩码（D-R0-15） | `NeedsWork` 里"廉价前置"检测 `met && !ConditionActive[slot] && RequiresEvaluation(cond)`；`ScanAndAppend` 里 `ShouldForceIncludeOnTransition` ⇒ `state.RequireSend(slot)` ⇒ 该槽位**不比较**直接置位。断言 E1–E9 |
| 5. 初始状态（首次进入范围/基线缺失 ⇒ 全量） | `PMRepConnectionState.UnknownBaselineCount` / `Baseline[slot] == null` ⇒ 直接 include；新连接由 `AddConnection` 建状态即得全量。断言 F1–F6 |
| 6. OnRep 分发 | `ApplyRecord` 应用值后按 `PMPropertyDescriptor.OnRepMethodId` 调 `RegisterOnRepDispatcher(classId, Action<PMNetObject,ushort>)`；未注册 ⇒ `OnRepUnhandled` + 告警（不静默）。断言 G1–G6 |
| 7. 有界 | `PMRepOptions`：单载荷属性数 64、每对象在途 8、每连接每轮对象数 150、待发 Ack 1024；`PMRepMask.MaxBits = 256`（超限拒绝登记）；解析侧 `MaxUpdatesPerMessage/MaxAcksPerMessage`。断言 A14/A15 + H1–H3 + H2e–H2h |

### 2.4 与 M04（`PMNetWorld`）的衔接

- 发送侧：宿主 `AddConnection` + `RegisterObject`；`Tick()` 用 `PMNetConnection.Send(payload, Unreliable)`。未就绪的连接**跳过组装**（否则版本号与在途记录会被提前消耗，值却永远发不出去）。
- 接收侧：`OnMessage(conn, bytes, off, len)` 经 `World.TryFind(netId)` 定位对象、经 `PMNetRegistry.TryGetClass(ClassId).Rep` 取描述符；应用后 `QueueAck`；`BuildAckMessage(conn)` 取走上行 Ack，发送侧 `OnMessage`（Ack 类型）转 `OnAck`。
- 端到端门禁用**两个 `PMNetWorld`**（服务端权威 + 客户端副本）走真实生命周期（`BuildLifecycleBatch` → `OnLifecycleMessage`），不是造假对象。

### 2.5 编码与 `.meta`

- 5 个 `.cs`：**UTF-8 + BOM + CRLF**（与仓库里最近一批新文件 `World/**`、`Declarations/**` 一致）。
- 5 个 `.meta`：**无 BOM + LF**（与 R1 期新增的 `World.meta`、`Declarations/*.meta` 一致）。

---

## 3. 验收输出

### 3.1 `dotnet build Tools/PMReplicationTest -c Release`

```
  PMReplicationTest -> D:\UGit\hyld-master\Tools\PMReplicationTest\bin\Release\net8.0\PMReplicationTest.dll

已成功生成。
    0 个警告
    0 个错误
```

### 3.2 `dotnet Tools/PMReplicationTest/bin/Release/net8.0/PMReplicationTest.dll`（退出码 0）

```
=== R2 / T41：运行时属性复制层（M06 最小）契约验证 ===
覆盖：每连接基线 / ACK 只能前进 / 变更掩码比较 / 8 项复制条件 /
      条件跃迁全掩码 / 初始状态全量 / OnRep 分发 / 有界 / 端到端收敛

[1] 干净实现（无缺陷注入）：通过 129 项，失败 0 项
...
=== 汇总 ===
干净实现：通过 129 项，失败 0 项
缺陷注入：3 个缺陷中命中 3 个
结果：PASS
```

各段通过数（干净实现）：

| 段 | 通过 |
|---|---|
| A. 线格式字节级契约（编解码 + 协议不兼容判定） | 16 |
| B. 每连接基线：ACK 只能前进 / 旧 ACK 不清新脏位 | 10 |
| C. 变更掩码：值未变不发（D-R0-13） | 6 |
| D. 8 项复制条件 × 角色组合（表驱动 + 交叉校验 + 端到端） | 53 |
| E. 条件跃迁强制补发（D-R0-15） | 10 |
| F. 初始状态全量 / 新连接补齐（D-R0-12/16） | 6 |
| G. OnRep 分发 | 6 |
| H. 有界：单包属性数 / 每对象在途 / 每帧对象预算 | 14 |
| I. 端到端：两条不同基线的连接最终一致（T41） | 8 |
| **合计** | **129（0 失败）** |

关键断言原文摘录：

```
    OK   B5 旧 ACK 不回退已确认版本（仍为 2，实际 2）
    OK   B6 旧 ACK 被计入 stale（1）
    OK   B8 旧 ACK 不清除新脏位
    OK   B9 未确认的修改仍然被送达（客户端 A=30）
    OK   C2 标脏但值未变 ⇒ 不发数据（实际待发 0）
    OK   C4 追平后脏位被清（对象不再每轮空扫）
    OK   D11 Never（dyn=Dynamic, custom=True） × 模拟 = False
    OK   E6 条件跃迁后必须有载荷发出（实际 1）
    OK   E7 跃迁补偿的掩码包含该属性（实际 [1]）
    OK   E8 跃迁补发被计数（1）
    OK   F5 新连接首次进入范围 ⇒ 全量（实际 [0,1,2,3]）
    OK   H1b 单个载荷的属性数不超过上限（实际最大 2）
    OK   H2c 超限被顺延并计数（1）
    OK   H2d 被顺延的修改最终仍然送达（不丢），客户端 A=3
    OK   H3c 被顺延的第二个对象在后续帧送达（不丢）
    OK   I4 slow 最终收敛到 A=60
    OK   I6 两条连接最终一致（float/string 属性）
```

### 3.3 `dotnet build Tools/PMClientCheck -c Release`

```
已成功生成。
    0 个警告
    0 个错误
```

即：新增代码在 **Unity 2019.4 语言面（netstandard2.0 + C# 7.3）+ Unity 桩 + Google.Protobuf** 的编译面下可编。

### 3.4 回归（确认没有破坏既有门禁）

| 门禁 | 结果 |
|---|---|
| `Tools/PMNetLangCheck`（netstandard2.0 + C# 7.3，只编 PMNet 核心+生成物） | 0 错误 |
| `Tools/PMNetWorldTest` | **152 项 0 失败** |
| `Tools/PMTransportTest` | **47 项 0 失败** |
| `Tools/PMCallspaceCheck` | **93 项 0 失败** |
| `Tools/PMNetVerify` / `PMUdpRouterTest` / `PMNumericEquivalenceTest` | 0 错误 |
| `python Tools/check_cs_braces.py Client/Assets/Scripts/PMNet/Replication/*.cs` | PASS（5 个文件括号配平 + 顺序检查通过） |

### 3.5 `.meta` 与 GUID

```
CRLF BOM    PMRepConnectionState.cs        LF noBOM PMRepConnectionState.cs.meta
CRLF BOM    PMReplicationChannel.cs        LF noBOM PMReplicationChannel.cs.meta
CRLF BOM    PMReplicationReader.cs         LF noBOM PMReplicationReader.cs.meta
CRLF BOM    PMReplicationTypes.cs          LF noBOM PMReplicationTypes.cs.meta
CRLF BOM    PMReplicationWriter.cs         LF noBOM PMReplicationWriter.cs.meta

Client/ 下 .meta 总数 = 6769，重复 GUID = 0
新 GUID（5 个）全部存在且互不相同：
  828cd814b244451f997f270d9dd69001  PMReplicationTypes.cs.meta
  bdbac7a3b5b2414793eaa3cd23c4a027  PMRepConnectionState.cs.meta
  b4651175815743de814fbea7d1a96573  PMReplicationWriter.cs.meta
  078c1601342c427db5ad233de274476d  PMReplicationReader.cs.meta
  efdba40cb0504830bfb66e8050b8d179  PMReplicationChannel.cs.meta
```

---

## 4. 负向验证结果（逐条实际命中情况）

方式：把三处正确性关键点做成 `protected virtual` 决策点，门禁定义**缺陷子类**覆写它们，然后用同一个套件重跑一遍并断言"期望的断言确实失败了"。这一步是门禁的元验证：注入缺陷后若仍然全绿，说明断言是空的。

| 缺陷 | 注入点（覆写） | 注入前失败数 | 注入后失败数 | 期望断言 | 实际结果 |
|---|---|---|---|---|---|
| **F1 旧 ACK 不清新脏位 / 不回退版本** | `IsAckStale` 恒 `false`（旧 ACK 被当有效确认）+ `TryClearSettledDirty` 无条件 `obj.Dirty.Clear()` | 0 | **4**（通过 125 项） | B5 / B6 / B8 / B9 | **全部命中**（B5「仍为 2，实际 1」；B6「0」；B8；B9「客户端 A=30」） |
| **F2 条件跃迁全掩码（D-R0-15）** | `ShouldForceIncludeOnTransition` 恒 `false` | 0 | **3**（通过 126 项） | E6 / E7 / E8 | **全部命中**（E6「实际 0」；E7「实际 []」；E8「0」） |
| **F3 值未变不发（D-R0-13）** | `HasValueChangedSinceBaseline` 恒 `true`（标脏即发，不比较） | 0 | **5**（通过 124 项） | C2 / C3 | **全部命中**（C2「实际待发 1」；C3「0」），并连带 C4 / G1 / G3 一起失败 |

**未命中项：无。** 3 个缺陷全部被抓到，且每个缺陷的针对性断言都在失败清单里。

诚实说明两点：

1. **F1 的连带失败最少**：因为缺陷被限制在 ACK 路径上，只有 B 段（和个别依赖脏位清理的用例）受影响 —— 这说明 B5/B6/B8/B9 是**专门**为这条契约准备的，不是"被别的红色顺带染红"。
2. **F3 的连带失败最多**（C4/G1/G3）：因为"标脏即发"会改变很多用例的发送次数。这属于预期；但它也提示：若要更精确地定位，G 组断言应该改成"相对计数"而不是绝对次数（已按此写：G1 断言 == 1 是在"先建立基线再只改一个属性"的前提下）。

复现方式（三条命令，都是同一份源码 + 不同缺陷子类，无需额外编译开关）：

```
dotnet build Tools/PMReplicationTest -c Release
dotnet Tools/PMReplicationTest/bin/Release/net8.0/PMReplicationTest.dll
# 输出里 [2] 段即负向验证；退出码 0 当且仅当"干净实现 0 失败"且"3 个缺陷全命中"
```

---

## 5. 冻结接口问题

**没有任何冻结接口缺陷阻断本任务。** 但有 5 处"接口面不足/需知晓"的边界，按重要性列出（都不建议现在改接口，因为生成器 A 块与主 Agent 的 C 块可能依赖现状）：

1. **`PMPropertyDescriptor` 没有"值比较"委托**（只有 `Writer`/`Reader`）。
   → 本层的做法：用 `Writer` 把当前值编码到临时 `PMNetWriter`，再与**该连接基线的字节**做逐字节比较（`HasValueChangedSinceBaseline`）。这正好符合 D-R0-13 的口径（"标脏后仍要比较"），且不需要新增接口。
   代价：每轮比较都要重新编码一次脏属性；若 R2 收口时要优化，建议生成器额外产出 `PMPropertyComparer`（返回 bool），而不是改本层的比较方式。

2. **`PMReplicationDescriptor` 没有 OnRep 分发委托**（只有 `PMPropertyDescriptor.OnRepMethodId`）。
   → 本层补了一个 `PMReplicationChannel.RegisterOnRepDispatcher(uint classId, Action<PMNetObject, ushort> dispatcher)`，签名与 `net-r2-codegen-contract.md` §4.1 第 4 条生成物 `PMNet_OnRepDispatch(PMNetObject t, ushort onRepMethodId)` **一致**，A 块生成时直接挂上即可。未注册时计 `OnRepUnhandled` + 告警（不静默）。

3. **`PMNetObject.ShouldReplicateProperty(propertyIndex, connection)` 覆盖不了 8 项全集**：
   - 它只按 `connection == OwnerConnection` 判定（等价于客户端视角的自治/模拟两行），**没有"权威视角"这一行**；
   - `Custom` / `Dynamic` 恒返回 true（没有运行期覆盖入口）。
   → 本层自带 `PMRepConditions.Evaluate`，并用 **D-cross 16 项断言**证明在客户端两行上与冻结实现逐格一致；权威行与 Custom/Dynamic 的运行期覆盖由本层补足（C-15/C-16）。建议 A 块**不要**改 `ShouldReplicateProperty`，把它当作"客户端两行"的权威参考实现即可。

4. **`PMNetWorld` 没有"枚举已登记连接"的公开入口**（只有 `ConnectionCount` / `PendingEventCount(conn)`）。
   → 本层自持连接列表（`AddConnection`）。宿主接线时需要在同一处同时调用 `PMNetWorld.AddConnection` 与本层 `AddConnection`；建议 R2 收口时由主 Agent 决定是否给 M04 补一个只读枚举（那属于 M04，不在本任务边界内）。

5. **`PMDirtyTracker` 位宽固定 64**（`Mark(index>=64)` 会 `MarkAll()` 退化为整对象脏）。
   → 影响：类属性数 > 64 时，槽位 ≥ 64 的脏位**无法按位清除**（`TryClearSettledDirty` 里 `limit = min(SlotCount, 64)`）。后果被限制为"该对象持续参与比较"（正确性不受影响，因为比较才是判据），但会削弱 Push 的收益。已在代码注释与本报告 §6 记录，未擅自扩展冻结类型。

6. **`PMReplicationDescriptor` 的 `HasConditionalMask` 由生成器填写**，本层不盲信：自行按属性条件重算，不一致时计 `DescriptorFlagMismatch` + 告警（生成器缺陷信号）。这也是一处对 A 块的软约束：该标志必须等于"存在 `Condition != None` 的属性"。

---

## 6. 诚实记录的缺口

### 6.1 按任务边界明确不做（非缺陷）

- 距离裁剪 / 相关性调度 / 频率 / 休眠（M06 后半）—— `CullDistanceSquared` / `AlwaysRelevant` / `Dormancy` / `NetUpdateFrequency` 全部未接；对象只要 `Active` 且登记就参与复制。
- FastArray（D-R0-17）未实现。
- 预测 / 回滚 / reconcile（M07/M08，R4）未实现；本层**不做**任何回滚，也不读客户端预测帧。
- 量化（`QuantizerId`）只透传不实现 —— 描述符里有字段，本层的 Writer/Reader 直接由生成器决定是否做定点化。

### 6.2 已实现但口径受限（需要主 Agent 裁决或后续收口）

1. **丢包恢复策略**：属性更新走 `Unreliable`。本层**没有**做"同一值已在途就不再发"的抑制，而是"**未确认就继续重发**"，用每对象在途上限（默认 8）做背压。理由：M03 的丢包通知尚未接线，而抑制一旦开在丢包通道上，丢一包就会让该值停滞到下次变化。另提供 `OnLoss(conn, netId, version)` 供传输层上报丢包（提前回收容量），已用 H2e–H2h 4 项断言覆盖。
   **未实现**：Iris 的 `LostStatePriorityBump`（丢包后提优先级）、`LostChangeMask &= ~NewerRecordChangeMask`（位级精简）、legacy 的 NAK 语义。本层以"持续重发 + 基线比较"达到最终收敛，但没有位级精简。
2. **在途上限的粒度是"价值批"而不是"位"**：重复的未确认值会各占一条在途记录（上限 8）。持续丢包时对象会被顺延（**有界、不丢**，但吞吐下降）。R0 §9 的"每对象在途 65535"未采用（本任务按 §8 的待收敛口径取初值 8）。
3. **D-R0-15 在本设计下的可观测差异**：因为本层是"每连接基线 + 比较"，条件由不满足变满足时，**"值已变"的情形本来就会被比较发现**；跃迁规则的独立可观测行为是"**值未变也补发一次当前值**"（即契约明文要求的"全掩码置脏"）。门禁 E6–E8 正是按这一点断言的。
   → **需主 Agent 裁决**：若认为"值未变时的补发"应省略（纯冗余），则 D-R0-15 在本设计下等价于比较结果，可以考虑撤掉该规则并在 R0 契约里注明；若保持契约（我的选择：保留并断言），则现状即为契约实现。
4. **规则"条件不满足 ⇒ 抹掉脏位"**：本层不按连接逐条清共享脏位（那会让另一条条件已满足的连接看不到修改）。实际做法是"条件不满足时过滤该槽位，并把该连接视为**不阻塞**脏位清理"，由 D-R0-15 的跃迁补发兜底。这与 Iris 的 `ClearBits`/legacy 的 `InactiveChangelist` 殊途同归（都不丢值），但**机制不同**，值得在 R0 契约里补一句口径说明。
5. **`PMRepMask.MaxBits = 256`**：超过即拒绝登记该对象（`RejectedTooManyProperties` + 告警），不做静默截断。未实现分段掩码。
6. **无 MTU/字节预算**：单载荷只按"属性总数 ≤ 64"切分，没有按字节数切分（R0 §9 的 MTU 结论尚未冻结）。
7. **无字符串长度上限**：`net-r2-codegen-contract.md` §8 的"字符串复制长度上限 1024 字节"**未实现**；解析侧只受 `int` 上限与整体载荷长度约束。**这是 D-R0-18 的一个未闭合点**，建议 R2 收口时补。
8. **无载荷校验和**：UDP 下损坏的包只能靠魔数 / 协议版本 / 长度 / 属性 ID 部分发现（首字节魔数不符会被抓住，中途位翻转可能读出一串合法值）。建议由 M03 层加校验或依赖 UDP 校验和。
9. **值级解析异常无法回滚**：`ApplyRecord` 中某属性 Reader 抛异常时计 `ProtocolErrors` 并继续后续属性/记录，已写入的属性不回滚。D-R0-16 的原子性只对 M04 的 Create 路径成立（本层不碰）。
10. **乱序 Ack 只做单调丢弃**：迟到的旧 Ack 直接忽略，未实现"按版本窗口合并 + 在途链 AND NOT"（C-11）的精简。
11. **休眠交互未接**：`MarkPropertyDirty` 会 `FlushNetDormancy`（冻结实现里），但本层不消费休眠状态，因此"休眠 + ForceNetUpdate"这条耦合（C-29/C-30）在 R2 只算半个。

### 6.3 未验证项（证据缺口）

- **未在真实 Unity 2019.4 里编译**（本机无法开 Unity，且任务明令不要用 Unity 打开工程）。已用 `PMNetLangCheck`（netstandard2.0 + C# 7.3，无任何依赖）与 `PMClientCheck`（含 Unity 桩 + Google.Protobuf）双面兜住语言面/API 面，但运行时行为在 Unity 的 Mono/IL2CPP 下未实测。
- **未接真实传输（M03）**：门禁是单进程内"手动投递 + 手工 Ack/OnLoss"，没有真实丢包/乱序/重传注入。因此"与 M03 的丢包通知接线""真实 MTU 下的分包"都还没有端到端证据。
- **未对 > 64 个属性的类做端到端用例**：`PMDirtyTracker` 的 64 位边界只有代码路径保证，没有断言。
- **未做弱网/多对局压力验证**：`MaxObjectsPerConnectionPerTick = 150` 只在 2 个对象上验证了"顺延不丢"。

### 6.4 顺带发现（供主 Agent 参考，未在本任务范围内修改）

- `PMNetWorld.BuildLifecycleBatch` 的 `viewer` 参数与 `IsNetRelevantFor` 已经有"相关性"的雏形，但 `PMNetObject.CullDistanceSquared` 默认 -1（不裁剪）。R2 后半接距离裁剪时，M06 与 M04 的这条交界点需要一起设计（谁负责"进入/离开范围 ⇒ 全量/停止"）。
- `PMReplicationChannel` 的门禁查询接口（`TryGetAckedVersion` / `GetInflightCount` / `GetInflightVersions` / `TryGetBaselineValue` / `TryGetConditionActive` / `SerializeSlot`）是刻意保留的：主 Agent 在 C 块做端到端（T40/T41 收口）时可以直接用它们做断言，不必再引入测试专用后门。
