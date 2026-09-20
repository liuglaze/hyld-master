# R4-A 预测核心（M07）实现报告

范围：`Docs/plans/net-r4-prediction-contract.md`（冻结契约，只读，**本轮未改**）+ `Docs/plans/_r4_prediction_survey.md`（草案，仅参考）
+ `Docs/plans/_r4a_integration_report.md`（P4A4 集成门禁的上游发现）。
本次只消费冻结契约与集成报告的上游发现；未改主计划、未改 Mover、未改 PMNet、未改宿主。

**本轮性质**：R4-A「真集成发现」修复的第 1、2 项（对应 `_r4a_integration_report.md` §7 发现 1 与发现 2）。
原 P4A1/P4A2 交付（§1～§6）保持有效；本轮在其上做了两处**兼容新增**的语义收紧，见 §3、§7。

## 1. 交付文件（含编码与摘要）

| 文件 | 行数 | 字节 | 编码 | sha256(前 16 位) | 本轮变化 |
|---|---|---|---|---|---|
| `Client/Assets/Scripts/PMPrediction/PMPredictionContracts.cs` | 81 | 3647 | BOM+CRLF | `eca271d078e555d2` | **改**（兼容新增证据载荷） |
| `Client/Assets/Scripts/PMPrediction/PMPredictionTimeline.cs` | 1559 | 64405 | BOM+CRLF | `33e55a679010f3f6` | **改**（Stall 门 + 事件证据） |
| `Tools/PMPredictionTest/Program.cs` | 1948 | 111781 | BOM+CRLF | `9ff413e63365dbe1` | **改**（D 节重写 + L 节新增） |
| `Docs/plans/_r4a_prediction_report.md` | 本报告 | — | 无BOM+LF | — | 改 |
| `Client/Assets/Scripts/PMPrediction/PMAuthorityInputBuffer.cs` | 511 | — | BOM+CRLF | — | 未改 |
| `Client/Assets/Scripts/PMPrediction/PMInterpolationBuffer.cs` | 360 | — | BOM+CRLF | — | 未改 |
| 三个同名 `.cs.meta` | — | — | 无BOM+LF | — | 未改（GUID 不变） |
| `Tools/PMPredictionCoreCheck/PMPredictionCoreCheck.csproj` | — | — | 无BOM+LF | — | 未改（仍只编契约 + 预测三源 + PMNet 两原语） |
| `Tools/PMPredictionTest/PMPredictionTest.csproj` | — | — | 无BOM+LF | — | 未改 |

未创建/修改其它仓库文件；`Tools/**/bin|obj` 为构建产物（已被 `.gitignore` 覆盖）。未提交 git、未执行任何 SVN 操作。
并行委派的 Mover 组（`Client/Assets/Scripts/PMMover/**`、`Tools/PMMover*`、`Docs/plans/_r4a_mover_report.md`）本次未读写其任何文件；
`Tools/PMMoverPredictionTest/**` 属集成子任务，本次未读写。

## 2. 实际公开 API（本轮新增部分；原有签名一律未删改）

### 2.1 兼容新增（`PMPredictionContracts.cs`）

```csharp
public struct PMPredictionEventFrame
{
    public PMFrameId OutputFrame;          // 必须是 PMFrameDomain.Input
    public PMPredictionEvent[] Events;      // 空数组 = 确定无事件；null = 未提供证据
    public PMPredictionEventFrame(PMFrameId outputFrame, PMPredictionEvent[] events);
}
```

`PMPredictionSnapshot<TSync,TAux>` 新增两个**可选**字段（默认 `null`，因此既有调用方零改动即可编译且语义不变）：

- `PMPredictionEvent[] ConfirmedEvents` —— 本输出边界（`OutputFrame`）产生的**权威**事件集合。
  `null` = 未提供事件证据；空数组 = 确定该边界没有不可逆事件。
- `PMPredictionEventFrame[] ConfirmedEventFrames` —— 按边界给出的权威事件证据批（首选承载）：
  一次确认跨过多个边界时，用「边界号 + 该边界事件」成对提供。`null`/空 = 本次只确认状态、不广播事件。

### 2.2 `PMPredictionTimeline<TInput,TSync,TAux>` 新增

- 枚举：`PMAuthorityRejectReason.Stalled = 8`（兼容追加在 `HistoryMissing = 7` 之后）。
- 常量：`MaxEventsPerFrame = 256`、`MaxConfirmedEventFrames = 256`、`MaxConfirmedEventsTotal = 4096`。
- 只读：`ConfirmedEventFramesApplied`（确认时“有证据”的边界数，含证据为空数组的边界）、
  `ConfirmedEventFramesSkipped`（确认时“无证据”而被跳过广播的边界数）、`StalledAuthorities`（冻结/待重同步期间被拒的权威快照次数）。
- 读取：`bool TryGetBoundaryEvents(PMFrameId boundary, out PMPredictionEvent[] events, out bool authoritative)`
  （一律深克隆；`authoritative` 区分“已被权威证据替换”与“仍是本地预测集合”）。
- 既有：`IsStalled`（= `IsFrozen || NeedsResync`，本轮开始被 `ApplyAuthority` 真正使用）。

`IPMPredictionModel<,,>`、`PMSimulationResult<,>`、`PMPredictionEvent`、`PMTickResult`、`PMAuthorityApplyResult`、
`PMResyncResult`、`PMAuthorityInputBuffer<,>`、`PMInterpolationBuffer<,>` 的**公开形状一律未变**
（`PMInterpolationBuffer` 依旧没有 Reconcile/Restore/Rollback/ApplyAuthority/Resimulate——门禁反射钉死不变）。

## 3. 本轮两项修复的实际语义

### 3.1 修复 1：冻结 / 待重同步期间 `ApplyAuthority` 明确拒绝

契约原文（`net-r4-prediction-contract.md`「时间、参数与安全决策」）：
> 「Disconnected 只 Freeze，墙钟 2s 触发一次 ResyncRequested 通知(非每帧重复)；**状态恢复只接受显式可信 Resync**。」

修复前（`_r4a_integration_report.md` §7 发现 1、K4/K5/K7 复现）：`TickCore` 有 `_frozen`/`_needsResync` 两道门，
但 `ApplyAuthority` 在校验后**直接**恢复+重放并推进 `_acceptedBoundary`/`_confirmedFrame`，出现「已冻结但确认边界被推进、
确认事件被广播、状态被替换，且仍然冻结」的半冻结态。

修复后：`ApplyAuthority` 在 `null` 检查之后、**结构/epoch/instance/角色/未来/重复等一切校验之前**
先判 `_frozen || _needsResync`，命中即 `_stalledAuthorities++` 并返回 `PMAuthorityRejectReason.Stalled`：

- `Applied = false`、`Reconciled = false`、`AdvancedConfirmed = false`、`EventsConfirmed = 0`；
- 预测状态 / 历史槽位 / `ConfirmedFrame` / `PendingFrame` / 已确认事件**零改动**；
- 不触发 `ConfirmedFrameAdvanced` / `EventConfirmed`，也不计入 `RejectedAuthorities`
  （Stalled 有独立计数 `StalledAuthorities`，便于宿主区分“契约性停滞”与“帧内容被拒”）；
- 门在结构校验之前，因此冻结期间的**畸形快照也返回 `Stalled`**，不会顺手做任何结构判断；
- 恢复唯一入口仍是 `Resync`（`Resync` 内部 `ClearStallState()`），且 epoch 重绑与同绑定恢复都不受影响。

### 3.2 修复 2：权威校正边界的事件证据（闭合 `_r4a_integration_report.md` §7 发现 2）

原缺口：校正把边界 `b` 的状态整体替换成权威值，但产出 `b` 的输入是 `b-1`，**不在重放区间 `[b, PendingFrame)` 内**，
且快照不携带事件集合 ⇒ 帧 `b-1` 已广播的事件永远来自校正前模拟（集成门禁实测 3/3 帧不一致）。

修复后的判定规则（全部落在 `PMPredictionTimeline`）：

1. **只有携带该输出边界权威事件证据的边界才广播该边界的不可逆事件。**
   证据来自 `ConfirmedEventFrames`（按边界成对，首选）或 `ConfirmedEvents`（本边界简写）。
   两者都缺 ⇒ 该边界**一条都不广播**，且**绝不用预测事件集合冒充权威**（`ConfirmedEventFramesSkipped++`）。
2. **`null` 与空数组语义不同**：`null` = 未提供证据（不广播、计入 skip）；
   空数组 = 证据成立且确定无事件（不广播、计入 applied）。`PMEventFrame.Events == null` 的条目等同“未提供证据”。
3. **确认事件不得先广播再 reconcile**：证据只做只读校验与深克隆（`TryCollectEvidence` 在 `ShouldReconcile` 之前，
   零状态改动）；广播统一发生在 `RestoreAndReplay` 完成之后（`BroadcastConfirmedRange`）。
4. **一次确认跨多个边界不伪造中间边界**：只广播证据表中出现的边界；
   中间边界按 `(ConfirmedFrame, OutputFrame]` 逐号检查，无证据即跳过。宿主可用可靠事件通道补。
5. **证据有界**：条目数 ≤ `MaxConfirmedEventFrames`、单帧事件数 ≤ `MaxEventsPerFrame`、
   总事件数 ≤ `MaxConfirmedEventsTotal`、`Key == 0` 拒绝、条目边界必须是 `Input` 域且落在
   `(ConfirmedFrame, OutputFrame]`、同一边界重复给证据拒绝、`frame.Events == null` 跳过；
   越界/畸形一律 `Malformed` 且**零状态改动**。去重只在单帧集合内做（HashSet 容量被该帧长度封死），
   **没有跨帧/跨调用的全局 HashSet**；模型单帧输出超过 `MaxEventsPerFrame` 时 fail-fast 抛异常。
6. **证据一律深克隆**：调用方事后改写自己持有的数组/条目不影响已确认历史（`TryGetBoundaryEvents` 可核验）。
7. **已确认边界不重播**：`boundary <= _acceptedBoundary` 依旧先判 `StaleOrDuplicate`（早于证据校验），
   因此「已确认后不同内容、甚至带新证据的同一边界包」既不推进确认也不广播。
8. **错 epoch / 未来边界不广播**：这两道判定同样早于证据校验，带证据也只会被拒。
9. **`Resync` 同款规则**：同绑定 Resync 若把确认边界推到 `boundary`，同样只广播有证据的边界；
   **epoch/instance 重绑**属于“换绑定重建”，旧绑定的事件证据无法归属，
   因此重绑快照一旦携带任何事件证据即 `Malformed` fail-closed（宁拒绝也不静默丢不可逆事件）。

被替换事件集合的所有权：确认时该边界槽位的 `Events` 被替换为证据集合（深克隆）并标记 `EventsAuthoritative = true`；
未确认边界保持本地预测集合（`authoritative = false`），回滚重放仍会替换同帧预测集合（不影响上述规则）。

## 4. 验证

| 步骤 | 命令 | 实际结果 |
|---|---|---|
| 清空 `bin/obj` 后语言/API 门禁（netstandard2.0 + C# 7.3） | `dotnet build Tools/PMPredictionCoreCheck/PMPredictionCoreCheck.csproj -c Release` | **exit 0**，0 警告 0 错误 |
| 清空 `bin/obj` 后门禁构建 | `dotnet build Tools/PMPredictionTest/PMPredictionTest.csproj -c Release` | **exit 0**，0 警告 0 错误 |
| 契约矩阵运行（先 build 0 再 run） | `dotnet Tools/PMPredictionTest/bin/Release/net8.0/PMPredictionTest.dll` | **exit 0，通过 417 / 失败 0** |

（本轮之前为 327/0；新增 90 项检查来自 D 节重写与新增 L 节。）

### 4.1 覆盖变化

- **D 节（重写，不删覆盖）**：广播的必须是**测试独立给出的权威证据**，不再是预测事件。
  · D1：确认 0→2 广播 2 条；key100 证据**故意给两次** ⇒ 帧内去重成一条；
  证据 value 取 11/22（模型预测值是 1）⇒ 广播值 11/22 才能证明“用的是证据而不是预测集合”；
  区间记录 `0->2`；证据采用 2 个边界、跳过 0 个。
  · D1b：确认 2→4 不带证据 ⇒ 即使这两帧都有预测事件也**零广播**，skip 增 2。
  · D1c：已确认边界再送「不同内容 + 新证据」⇒ `StaleOrDuplicate`，累计仍 2 条、applied 不变、区间记录不增。
  · D2：校正边界 2（Mode 2/Layers[2]）触发重放；断言
  `TryGetBoundaryEvents(边界3)` 从「校正前空集合」被替换成「重放后的 {4002, value=2}」且**仍非权威**、
  此时 4002 **尚未广播**；确认 3 时广播该边界的权威证据（一条），300 不重播、4002 只出现一次，
  确认后边界 3 的事件集合被证据替换并标记权威。**「确认去重 / 升序 / 回滚替换」三项覆盖全部保留。**
- **L 节（新增）**：
  · L1 `null` 证据 ⇒ 不广播 + skip++ + applied 不变 + 事件仍非权威（预测事件没被冒充）；
  · L2 空数组证据 ⇒ 不广播但 applied++（与 null 可区分）、集合被替换为权威空集合；
  · L3 跳确认 0→4 只给边界 4 证据 ⇒ 只广播 1 条、中间 3 个边界 skip、中间预测 key 一个都没广播；
  · L4 事后改写证据数组/条目 ⇒ 历史 key/value 不变（深克隆）；
  · L5 有界校验 7 例（条目超上限 / 单帧超上限 / 总量超上限 / key=0 / 非 Input 域 / 边界 ≤ 已确认 / 边界 > 本次边界 /
  同一边界重复）全部 `Malformed` 且确认边界、广播数、applied/skipped 零改动；`frame.Events == null` 计入 skip 不畸形；
  · L6 错 epoch、未来边界带证据 ⇒ 各自拒绝码且零广播；
  · L7 `Freeze()` 后 `ApplyAuthority` ⇒ `Stalled` 且 `Applied/Reconciled/AdvancedConfirmed` 全 false、
  confirmed/pending 不变、零帧推进回调、零事件、`IsFrozen` 仍 true、`StalledAuthorities` 可观测；
  连畸形快照也是 `Stalled` 且不混入 `RejectedAuthorities`；反复调用仍 `Stalled`；
  只有 `Resync` 能解冻，且 Resync 只广播有证据的边界；
  · L8 重绑快照带事件证据 ⇒ `Malformed` 且未重绑；不带证据的重绑照常 `EpochRebound`。

### 4.2 缺陷注入（临时改生产分支，内存备份 + finally 恢复 + sha256 校验）

每次注入后 build + run 观察红，随后在 `finally` 中写回原始字节并校验 sha256 与注入前一致；
**未使用临时文件、未误截文件、未执行任何 git 操作**。本轮涉及文件基线 `PMPredictionTimeline.cs`
sha256 = `33e55a679010f3f6faf2d94686232b4e1738d9dbd0dd92a575e7aea69f63075e`。

| # | 注入点（生产分支） | 实测结果 | 恢复校验 |
|---|---|---|---|
| D1 | `ApplyAuthority` 的 `if (_frozen \|\| _needsResync)` → `if (false && …)`（关闭 Stall 门） | `PMMoverPredictionTest` **exit 1，8 项失败**（K4/K5/K5b/K7 + K8/K8b/K8c/K8d：Applied=True、AdvancedConfirmed=True、confirmed 被推到 4、NeedsResync 下仍被接纳） | sha256 一致 |
| D2 | `BroadcastConfirmedRange` 缺证据分支 `continue` → 回退用 `step.Events`（预测事件冒充权威） | `PMMoverPredictionTest` **exit 1，8 项失败**（L2 实测广播 4 条、L3 值不再是证据值、L3 重复包后累计 6 条、L4 跳确认广播 5 条并伪造中间边界等） | sha256 一致 |

注入后均已还原并重新 `rm -rf bin/obj` 全量重建，最终 `PMPredictionTest` 417/0 与
`PMMoverPredictionTest` 264/0 可复现（见 §5 与 `_r4a_integration_report.md` §5）。

### 4.3 冻结契约变更裁决（需主 Agent 知悉）

本轮为闭合集成门禁的两项上游发现，**修改了共享冻结契约的两个文件**：

- `PMPredictionContracts.cs`：新增 `PMPredictionEventFrame` 结构 + `PMPredictionSnapshot` 的两个可选字段。
  **纯兼容新增**（默认 `null`，既有构造/读写方无需改动，语义与旧行为一致：旧快照不带证据就只确认状态、不广播）。
- `PMPredictionTimeline.cs`：新增 `PMAuthorityRejectReason.Stalled = 8`、三个上限常量、三个只读计数、
  `TryGetBoundaryEvents`；并收紧 `ApplyAuthority`（Stall 门）与事件广播判据（证据制）。

**裁决归属**：这是本任务（委派方）明确要求的两项修复，属主 Agent 授权范围内的本轮裁决；
`Docs/plans/net-r4-prediction-contract.md`（冻结契约正文）**本次未改（越界）**，
需要由主 Agent 把上述两条语义写回契约正文（建议补充：「冻结/NeedsResync 期间 ApplyAuthority 必须返回 Stalled」
与「不可逆事件只广播携带权威事件证据的边界，缺证据不得用预测事件冒充」）。
在契约正文更新之前，本报告 §3 即为这两条语义的当前事实源。

## 5. 实际退出码汇总

| 命令 | exit |
|---|---|
| `dotnet build Tools/PMPredictionCoreCheck/PMPredictionCoreCheck.csproj -c Release`（清空 bin/obj 后） | 0 |
| `dotnet build Tools/PMMoverCoreCheck/PMMoverCoreCheck.csproj -c Release`（清空 bin/obj 后） | 0 |
| `dotnet build Tools/PMPredictionTest/PMPredictionTest.csproj -c Release`（清空 bin/obj 后） | 0 |
| `dotnet build Tools/PMMoverPredictionTest/PMMoverPredictionTest.csproj -c Release`（清空 bin/obj 后） | 0 |
| `dotnet Tools/PMPredictionTest/bin/Release/net8.0/PMPredictionTest.dll` | **0**（417 通过 / 0 失败） |
| `dotnet Tools/PMMoverPredictionTest/bin/Release/net8.0/PMMoverPredictionTest.dll` | **0**（264 通过 / 0 失败） |

## 6. 未实现 / 残留（R4-B 及以后）

1. **缺历史（HistoryMissing）在冻结契约下仍不可达**：裁剪只丢弃已确认记录，因此
   “边界 ∈ (已确认, PendingFrame]”必然仍在窗口内（E3 用 200 步不变式钉死）。该分支保留为防御性 fail-closed；
   缺历史的**可达** fail-closed 形态是 `HistoryExhausted → NeedsResync`。
2. **SP 恢复不做自动阈值跳变**：没有实测阈值就不写进首批；由宿主在恢复时调 `AlignToLatest()`。
   `MaxExtrapolateMs` 首批固定 0（只插值，不做表现外推）。
3. **`Resync` 的 `_acceptedBoundary` 只增不减**：若宿主先接受边界 5 再 `Resync` 到边界 4，
   `_confirmedFrame` 落到 4 而 `_acceptedBoundary` 仍是 5，随后边界 5 的权威会被判 `StaleOrDuplicate`。
   该行为本轮未改（既有语义），也未新增实测覆盖。
4. **`TryGetBoundarySnapshot` 不携带事件集合**（`ConfirmedEvents`/`ConfirmedEventFrames` 保持 `null`）：
   时间轴自身产出的快照不是“权威事件声明”，因此不做假设性填充；需要事件时用 `TryGetBoundaryEvents`。
   这也意味着宿主把时间轴快照原样回灌成 Resync 时不会带事件证据（本轮的 `Resync` 不广播，符合 fail-safe）。
5. **证据批不含中间边界时的宿主补发通道未实现**：本轮只保证“无证据不广播”，
   `EventConfirmed` 仍是唯一广播点；可靠事件通道（谁补、如何对齐已确认区间）属 R4-B 宿主设计。
6. **未接线**：谁调用 `ApplyAuthority`/`Resync`/`Freeze`/`NotifyWallClock`/`AlignToLatest`、
   权威帧与事件证据如何从 R3 复制流进入预测核心，均不在本次范围。
7. **未实现**：行为类 Modifier 的实例级补偿、InstantEffect/LayeredMove 的网络策略与**拒绝裁决**、
   Mover 纯模型的 Unity PhysX 等价性（P4A3/P4A4 归集成子任务与 R4-B；本轮只有集成门禁侧证据）。
8. **阈值仍是初值**：位置 0.05m / 速度 0.01m/s / 朝向 1°（写在测试模型里），需按实测收敛。
9. **dt 整毫秒 1..50 是本项目首批限制**：若 R4-B 出现亚毫秒或多频 dt，需要重新裁决（本层会 fail-fast 而不是静默拆分）。
10. 未改：`PMAuthorityInputBuffer.cs`、`PMInterpolationBuffer.cs`、`PMNet/**`、Mover、旧玩法 SavedMove/权威入口、
    主计划、R4 契约正文、Unity 宿主；未编译/未修改任何 UE 工程。

## 7. 已检查范围（文档如何决定入口）

- `D:/UGit/hyld-master/AGENTS.md` → 客户端 `Client/Assets/AGENTS.md`、服务端 `Server/AGENTS.md`、
  迁移计划 `Docs/plans/net-architecture-migration.md`。
- 主计划文末 **R4-A 行**（§2045–2059）：P4A1/P4A2 的验收项与「预测组只改泛型 timeline/authority budget/SP 及其测试」边界
  → 决定本轮只动 `PMPrediction/**` 与 `Tools/PMPredictionTest/**`。
- `Docs/plans/net-r4-prediction-contract.md`（全文，冻结裁决优先）→ 帧语义 n/n+1、历史 128、2s 墙钟、
  DS 8 步/100ms + 信用 50..200、SP 只插值且外推 0、事件只随 ConfirmedFrame 一次、
  **「状态恢复只接受显式可信 Resync」**（本轮修复 1 的直接依据）；
  「不可逆事件只随 ConfirmedFrame 一次通知，模型 Simulate 输出纯数据事件，回滚替换同帧事件集合」
  （本轮修复 2 的语义基础）。
- `Docs/plans/_r4a_integration_report.md` §7（**本轮两项修复的来源**）：发现 1 给 K4/K5/K7 最小复现与后果；
  发现 2 给「快照不带事件集合 ⇒ 校正边界前一帧事件无法替换」的可复现计数（3/3）。
  本轮把这两条从“记录已知缺口”变成“修复 + 严格断言”。
- `Docs/plans/_r4_prediction_survey.md`（草案）：UE 侧来源与未冻结项清单（首批取值见 §3）。
- `Client/Assets/Scripts/PMPrediction/*.cs`（只读源码）→ 实际落点与 `TryCollectEvidence`/`BroadcastConfirmedRange` 位置；
  `Client/Assets/Scripts/PMNet/PMNetIdentity.cs`/`PMNetRole.cs`（只读）→ `PMFrameId` 命名空间隔离、
  `PMTimeStep`、`PMNetRole`（D-R0-22）。
- `Tools/PMPredictionCoreCheck`、`Tools/PMMoverCoreCheck`、`Tools/PMMoverPredictionTest`、`Tools/PMPredictionTest`
  （只读）→ 门禁工程风格（netstandard2.0 + C#7.3 语言门禁 + net8 断言门禁、退出码 0/1、直接编真实源码不引 DLL）。
- Windows 命令按 `windows-shell-compat`：Pi bash 工具按 Bash 解析，未混用 PowerShell 语法；
  文本改写统一用 Python 以 `utf-8-sig` + `newline='\r\n'` 读写，保证 BOM+CRLF 不变（已用 `od`/`file` 复核）。
