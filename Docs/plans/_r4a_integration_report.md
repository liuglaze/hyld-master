# R4-A 集成门禁（P4A4）实施报告

范围：`Docs/plans/net-r4-prediction-contract.md`（冻结契约，只读）+ 主计划 `net-architecture-migration.md` 文末 R4-A 行 P4A4
+ 前置报告 `_r4a_prediction_report.md` / `_r4a_mover_report.md`（真实 API）。
本批只写两处：`Tools/PMMoverPredictionTest/`（门禁工程）与本报告；**未改任何上游实现**
（预测三源、Mover 四源、PMNet 原语均未动）。

**本轮性质**：R4-A「真集成发现」修复的第 1、2 项。原交付（§1～§4）保持有效；
本轮把 §7 的两项**上游发现**从「记录已知缺口（预期 FAIL）」改成「上游已修 + 门禁严格断言」，
并新增 L 节对权威事件证据做契约级断言。**当前门禁全绿（264/0，exit 0）**。

## 1. 交付文件

| 文件 | 行数 | 字节 | 编码 | sha256(16) |
|---|---|---|---|---|
| `Tools/PMMoverPredictionTest/PMMoverPredictionTest.csproj` | 61 | 3755 | 无 BOM + LF | `455957c46e2dde99`（未改） |
| `Tools/PMMoverPredictionTest/Program.cs` | 2630 | 152887 | BOM + CRLF | `e32fa4677907af64` |
| `Docs/plans/_r4a_integration_report.md` | 本报告 | — | 无 BOM + LF | — |

未新增 Unity 资产/meta；`Tools/**/bin|obj` 为构建产物（`.gitignore` 已覆盖）。未提交 git、未执行任何 SVN 操作。

## 2. csproj 形态：为什么"直接链接源码编一份"（实测，不是偏好）

先按要求试 `ProjectReference` 同时引 `PMMoverCoreCheck` + `PMPredictionCoreCheck`，**实测失败（17 个 CS0433）**：

```
Program.cs: error CS0433: 类型“IPMPredictionModel<TInput, TSync, TAux>”同时存在于
  “PMMoverCoreCheck, Version=1.0.0.0 ...” 和 “PMPredictionCoreCheck, Version=1.0.0.0 ...” 中
（同批还有 PMTimeStep / PMFrameId / PMSimulationResult<TSync,TAux> / PMPredictionSnapshot<TSync,TAux>）
```

根因：两个库门禁都把共享契约 `PMPredictionContracts.cs` + `PMNetIdentity.cs` + `PMNetRole.cs` 编进了自己的程序集
（这正是"零依赖自包含"的设计），所以引用两个库必然出现同一定义类型的两份实现。
**故本工程按契约要求改为直接链接源码编一份**（与 `Tools/PMMoverTest` 同风格）：

- `PMPrediction/{PMPredictionContracts,PMPredictionTimeline,PMAuthorityInputBuffer,PMInterpolationBuffer}.cs`（逐文件）
- `PMMover/**/*.cs`（state / collision query / model / test world）
- `PMNet/{PMNetIdentity,PMNetRole}.cs`
- 零 NuGet、零 UnityEngine、零 DLL。库侧 netstandard2.0 + C#7.3 语言门禁已由双方各自的 `*CoreCheck` 完成，
  本工程是 net8 可执行验收门禁。

## 3. 实际使用的真实 API（均为只读依赖，非草案）

| 来源 | 用到的真实落点 |
|---|---|
| `PMPredictionTimeline<,>` | `Tick(int,TInput,PMFrameId)`、`ApplyAuthority(in snapshot)`、`Resync(in snapshot)`、`Freeze()`、`NotifyWallClock(double)`、`TryGetBoundarySnapshot(PMFrameId,out …)`、`GetSyncSnapshot()`、`PendingFrame/ConfirmedFrame/HasAcceptedAuthority/AcceptedBoundaryFrame/CurrentTotalSimTimeMs/IsFrozen/NeedsResync/IsStalled`、计数器（含本轮的 `StalledAuthorities/ConfirmedEventFramesApplied/ConfirmedEventFramesSkipped`）、事件（`ConfirmedFrameAdvanced`/`EventConfirmed`/`ResyncRequested`/`UnconfirmedInputsResetNotified`）、`PMTickRejectReason`/`PMAuthorityRejectReason`（含本轮新增 `Stalled`） |
| 本轮的**权威事件证据**面 | `PMPredictionSnapshot.ConfirmedEvents`、`PMPredictionSnapshot.ConfirmedEventFrames`、`PMPredictionEventFrame`、`PMPredictionTimeline.MaxConfirmedEventFrames`、`TryGetBoundaryEvents(boundary,out events,out authoritative)` |
| `PMAuthorityInputBuffer<TInput>` | `Admit(in PMAuthorityInput<TInput>)`、`Pump(double,IList<>)`、`Resync(epoch,instance,nextInputFrame)`、`CreditMs/NextInputFrame/QueuedCount/QueuedHeadFrame/HasGap/MissingFrameElapsedMs/NeedsResync`、`PMPumpDeferReason`/`PMInputRejectReason` |
| `PMInterpolationBuffer<TSync,TAux>` | `For<TInput>(model)`、`OnAuthority(in snapshot)`、`Advance(double)`、`AlignToLatest()`、`Sample()`、`Extract→PMInterpolatedState` |
| `PMMoverModel` | 实现 `IPMPredictionModel<PMMoverInput,PMMoverSyncState,PMMoverAuxState>` 全部 6 个方法 + `CollisionQuery`；`PMMoverDefaults`（含 1..50 dt 与 0.05m/0.01m/s/1° 阈值） |
| `PMMoverTestWorld` | 真 `IPMMoverCollisionQuery` 实现：`Sweep`/`QueryGround`/`WorldVersion`/`GetBox`/`FrozenCollisionWorldVersion` |
| `PMNet` | `PMFrameId`（`Input`/`AuthorityServer` 命名空间隔离）、`PMTimeStep`、`PMNetRole`（D-R0-22 只有 AutonomousProxy 回滚） |

门禁内部只有两个**装饰器**（不含任何位置/速度/层数学，不构成"第二套 reconcile oracle"）：
`InstrumentedModel` 委托真实 `PMMoverModel`，只做 (a) 记录 `Simulate` 的 `IsResimulating/StepMs/ClientInputFrame/BaseSimTimeMs/输入摘要`，(b) 按确定性公式注入预测事件；
`CountingWorld` 包装真实 `PMMoverTestWorld`，只计数并自证"同参数两次查询结果逐位相同"。reference 用的是**第三个独立世界实例**上的真实模型。

### 3.1 本轮新增：门禁如何提供"权威事件证据"（诚实口径）

- 预测事件与权威事件**同源不同据**：`InstrumentedModel` 按确定性公式为每个输出边界注入预测事件
  `key = KeyOf(inputFrame)`、`kind = (int)Sync.Mode`、`value = Sync.ValueOfPositionXMilli()`（即由**该步输出状态**决定）。
- 权威证据由 `AuthorityEventsOf(s, boundary, state)` 独立给出：key 由产出该边界的输入帧决定，
  **kind/value 由该边界的权威状态决定** —— 不读时间轴的预测事件集合。
  占位帧（`dt == 0`）确定无事件（送空数组）；边界 0 没有产出它的输入帧（空数组）。
- `EvidenceForRange(s, prevConfirmed, boundary, authoritySync)` 构造按边界证据批
  `ConfirmedEventFrames`（区间 `(ConfirmedFrame, boundary]`）：
  · 权威边界自身：证据取自**权威快照状态**（校正后该边界就是这个状态）；
  · 中间边界：本次调用不改动它们，证据取自调用前时间轴持有的边界状态。
  **明说**：本集成门禁的宿主没有“逐边界的服务器事件通道”，中间边界只能回放自己已确认的链
  （其数值与从同一起点出发的独立 reference 链逐位相同，见 B/D/E 节比对）。这一点不构成
  “用预测事件冒充权威”——权威边界的事件证据始终来自权威快照，而缺证据就一条都不广播（L 节钉死）。
- 因此 C/D/E/F 的每一次 `ApplyAuthority` 都通过 `WithEvidence(...)` 显式携带该区间证据；
  `F12` 断言本场景 82 个确认边界全部有证据（`applied=82 / skipped=0`）。

## 4. 测试场景（真 floor + 真 wall，不是空世界）

88 帧脚本，`dt` 取值实测 `{0,1,15,16,17,50}`（含下界 1、上界 50、缺帧占位 0）；四个阶段：
朝墙走(-X，第 22..25 帧用 `dt=50` 满速撞墙) → 起跳进 Falling → `SetMode(Flying)`+Additive/Override/移除/到期层 → `SetMode(Walking)`。
AP 用真实 timeline 逐帧 Tick；DS 用真实 `PMAuthorityInputBuffer` 按服务器墙钟 16ms 预算出步驱动**同一模型的第二个实例**
（`dt=50` 的帧会先被信用延后，故另有"追平"循环 —— 这正是预算语义）。

手算几何 oracle（报告内可复核）：墙中心 X=0 尺寸 X=1 ⇒ 面 X=0.5；胶囊半径 0.4；skin 0.001
⇒ **接触平面 = 0.5+0.4-0.001 = 0.899**，推出 skin 后应停在 **0.899+0.001 = 0.900**。
实测：首次接触帧 **frame=22，X=0.900000**（精确落点）；墙前校正 frame=21；落地 frame=54（Y 精确 == 1.0 == halfHeight）；
模式切换 frame=60；真实碰撞查询 `Sweep/QueryGround = 310/234` 次。

## 5. 验证（先 build 0 再 run；清空 bin/obj 后重建）

| 步骤 | 命令 | 实际结果 |
|---|---|---|
| 语言/依赖面门禁（库） | `dotnet build Tools/PMPredictionCoreCheck/… -c Release`（清空 bin/obj） | **exit 0**，0 警告 0 错误 |
| 语言/依赖面门禁（库） | `dotnet build Tools/PMMoverCoreCheck/… -c Release`（清空 bin/obj） | **exit 0**，0 警告 0 错误 |
| 门禁构建（清空后） | `dotnet build Tools/PMPredictionTest/PMPredictionTest.csproj -c Release` | **exit 0**，0 警告 0 错误 |
| 门禁构建（清空后） | `dotnet build Tools/PMMoverPredictionTest/PMMoverPredictionTest.csproj -c Release` | **exit 0**，0 警告 0 错误 |
| 预测契约矩阵 | `dotnet Tools/PMPredictionTest/bin/Release/net8.0/PMPredictionTest.dll` | **exit 0：通过 417 / 失败 0** |
| 本门禁运行 | `dotnet Tools/PMMoverPredictionTest/bin/Release/net8.0/PMMoverPredictionTest.dll` | **exit 0：通过 264 / 失败 0**（本轮之前为 235 / 3） |

集成事实（程序自报）：确认边界 82；累计重放步数 193；确认事件 81；DS 出步覆盖 89 个边界（=88+1）；
两世界 `WorldVersion` 同为 `0x52334201` 且几何逐位相同。

### 5.1 缺陷注入（临时改生产分支，内存备份 + finally 恢复 + sha256 校验）

两次注入后 build + run 观察红，随后在 `finally` 中写回原始字节并校验 sha256 与注入前一致；
**未使用临时文件、未误截文件、未执行任何 git 操作**。涉及文件基线
`Client/Assets/Scripts/PMPrediction/PMPredictionTimeline.cs` sha256 =
`33e55a679010f3f6faf2d94686232b4e1738d9dbd0dd92a575e7aea69f63075e`。

| # | 注入点（生产分支） | 本门禁实测结果 | 恢复校验 |
|---|---|---|---|
| D1 | `ApplyAuthority` 的 `if (_frozen \|\| _needsResync)` → `if (false && …)` | **exit 1，8 项失败**：K4（Applied=True Reject=None）、K5（AdvancedConfirmed=True、帧推进回调 1、事件 1、confirmed=4）、K5b、K7（NeedsResync 下仍被接纳）、K8/K8b/K8c/K8d | sha256 一致 |
| D2 | `BroadcastConfirmedRange` 缺证据分支 `continue` → 回退用 `step.Events` | **exit 1，8 项失败**：L2（无证据却广播 4 条、applied/skip 计数错）、L3（值不再是证据值、重复包后累计 6 条）、L4（跳确认广播 5 条并伪造中间边界）等 | sha256 一致 |

注入均已在还原后重新 `rm -rf bin/obj` 全量重建，最终 264/0 可复现（见 §5 表）。

## 6. 覆盖矩阵（A–L，全部断言在真实实现上执行）

| 节 | 覆盖点 | 关键结果 |
|---|---|---|
| A | 真实类型装配 / 双世界 / 角色 / 无 `Finalize` / SP 无回滚 API / 无静态可变状态 / 跨模块 dt 范围一致 | 全 PASS（反射钉死"无 Finalize 入口""SP 无 Reconcile/Restore/Rollback/ApplyAuthority/Resimulate"） |
| B | AP 预测 == DS 权威**逐位**（89 个边界，两个独立世界/实例）；DS 原 dt 无合并无放大；接触面/落地/模式切换三类真实帧存在 | 全 PASS |
| C | 校正起点（边界 0）：六类差异 → 完整恢复+重放 88 步；**不推进确认边界、零事件、零外部回调**；累计时间被权威值接管 | 全 PASS（证据批为空，`Applied` 但 `EventsConfirmed=0`） |
| D | 墙前校正：位置/速度/Mode/参数/ActiveLayers/aux-版本**逐项差异均触发 reconcile**；组合校正后逐边界 == 独立 reference；重放用的就是**原输入原 dt**；事后改写权威对象不污染 timeline | 全 PASS |
| E | 落地帧校正后**逐位收敛**到 DS 权威；模式切换帧 `ShouldReconcile==false` → 接受但不回滚、重放 0 步、确认照常推进且事件按证据发一次 | 全 PASS |
| F | 晚帧强制校正 → 重放；确认事件 81 条**逐 key 只发一次**、区间严格等于 (prev,new]；**F10 每条已广播事件的 kind/value 都等于该边界最终状态导出的值（0 不一致）**；**F11a 三个原载荷缺口帧全部闭合**；**F11b 广播事件逐字段等于该次校正携带的权威证据**；**F11c 两次被扰动权威（墙前/晚帧）的广播事件 ≠ 校正前预测事件（2/2）**；**F11d 落地校正的权威就是真值，与预测一致（1/1）**；**F12 applied=82 / skipped=0** | 全 PASS |
| G | 拒绝矩阵：epoch=0/instance=0/错 epoch/错 instance/帧域错/未来/超窗/重复 → 全部 fail-closed 且计数可观测；SP/Authority 角色 → `RoleNotPredictive`；历史满 → `HistoryExhausted`+`NeedsResync`+只请求一次 Resync；Resync 丢未确认输入并通知、不回退确认边界；断线墙钟 1900ms 不触发 / 跨 2000ms 只一次 / 倒退不累计 / Resync 解冻 | 全 PASS |
| H | DS 预算：重复/迟到/错 epoch/instance/畸形 dt/超窗/队列满（零副作用）；缺帧**先等待不跨越**、超 500ms 才 Resync 且水位从不跳到 2；信用 50 只够 3 步(48ms)、上限 200、倒退不补；每 Pump 8 步 / 100ms 上限；0 占位不花信用但占步数；Resync 返回值 | 全 PASS |
| I | SP 只时间插值：alpha 0.5 位置手算 (2,1.25,-1)；**yaw 350°→10° 最短弧得 0°**；Mode/Grounded/参数/层/Aux 取 To；外推上限 0 停在最新权威值；迟到/错绑定 fail-closed；采样结果无别名；**SP 期间真实 AP timeline 计数零变化（不调用 AP 回滚）** | 全 PASS |
| J | clone 三类无别名 / `Simulate` 不改写入参 / 真实 Mover 本批零预测事件 / 快照深克隆隔离 / 全序列输入与初始状态逐位未变 / Ticked·Simulated·Placeholder·Replayed 计数不变式 / AP 每步只调一次模型 / **整段场景两个全新实例重跑摘要逐位相同** | 全 PASS |
| K | **Stall 门回归（本轮从"上游偏离复现"改为严格正确断言）**：K1–K3 前置；**K4 冻结期间 `ApplyAuthority` → `Stalled`**；**K5 confirmed/pending/帧推进回调/事件全零改动**；K5b `IsFrozen` 仍 true 且 `StalledAuthorities=1`（无半冻结态）；K6 前置；**K7 NeedsResync 期间 → `Stalled`**；K8/K8b 显式 `Resync` 是唯一恢复入口并把确认边界落到可信快照、只广播携带证据的两个边界（升序）；K8c/K8d Resync 后 Tick 与 ApplyAuthority 恢复 | 全 PASS |
| L | **权威事件证据（真实 PMMoverModel）**：L1 未确认前零广播；L2 无证据 → 确认边界照常推进但**零广播**、4 个边界计入 skip、预测事件一条都没被冒充；L3 携带证据 4→6 → 按边界升序广播 2 条且值来自证据（404/505）；L3b 事后改写证据数组/条目 **不污染历史**（深克隆）；L3c 重复包（含新证据）→ `StaleOrDuplicate` 且不重播；L4 跳确认 0→5 只广播边界 5 的证据、中间 4 个边界 skip（**不伪造中间事件**）；L5 证据条目超上限 → `Malformed` 且确认边界与已广播事件零改动；未来边界 + 证据 → `Future` 且不广播 | 全 PASS |

## 7. 上游发现（本轮已修，交主 Agent 归档）

**发现 1（已修）：`ApplyAuthority` 缺少 Stall 门，冻结/待重同步期间仍被接纳并推进确认边界。**
契约原文（`net-r4-prediction-contract.md`「时间、参数与安全决策」）：
> "Disconnected 只 Freeze，墙钟 2s 触发一次 ResyncRequested 通知(非每帧重复)；**状态恢复只接受显式可信 Resync**。"

修复落点：`PMPredictionTimeline.cs` 的 `ApplyAuthority` 在结构/epoch/instance/角色/未来/重复校验**之前**
先判 `_frozen || _needsResync`，命中即返回 `PMAuthorityRejectReason.Stalled`（兼容新增枚举值 8）并立即返回。
预测状态 / 历史槽位 / `ConfirmedFrame` / `PendingFrame` / 已确认事件零改动，不触发任何回调，
也不计入 `RejectedAuthorities`（另有独立计数 `StalledAuthorities`）。
K4/K5/K5b/K7 现在是**严格正确断言**（不再"预期 FAIL"），且 D1 注入证明这道门是负载承载的（去掉即 8 项红）。

**发现 2（已修）：`PMPredictionSnapshot` 不携带事件集合 ⇒ 校正边界 `b` 的"前一帧 `b-1`"事件无法随状态替换。**
校正把边界 `b` 的状态整体替换为权威值，但产出 `b` 的输入是 `b-1`，不在重放区间 `[b, PendingFrame)` 内，
故帧 `b-1` 已广播的事件只能来自校正前模拟（原门禁 F11 实测 3/3 帧不一致）。

修复落点（兼容新增，未改任何既有签名）：
`PMPredictionSnapshot.ConfirmedEvents`（本边界权威事件；`null`=未提供证据、空数组=确定无事件）
与 `PMPredictionSnapshot.ConfirmedEventFrames`（**按边界成对**的证据批，首选承载）；
`PMPredictionTimeline` 只在**携带该边界证据**时广播该边界的不可逆事件，缺证据一律不广播、
**绝不用预测事件冒充权威**；广播仍在 `RestoreAndReplay` 之后（不先广播再 reconcile）；
一次确认跨多个边界时中间边界不伪造；证据有界（条目/单帧/总量三档上限 + `Malformed` fail-closed 且零状态改动），
去重只在单帧集合内（无跨帧全局 HashSet），证据一律深克隆。
本门禁 F10/F11a–F11d/F12 与新增 L 节把这条规则钉死；D2 注入证明缺证据回退到预测事件即 8 项红。

**契约正文更新由主 Agent 负责**：本轮为闭合上述两项发现，**修改了共享文件
`Client/Assets/Scripts/PMPrediction/PMPredictionContracts.cs` 与 `PMPredictionTimeline.cs`**（纯兼容新增 + 语义收紧）。
`Docs/plans/net-r4-prediction-contract.md` 正文本次**未改**（不在本批文件边界内），
需由主 Agent 把「冻结/NeedsResync 期间 ApplyAuthority 必须返回 Stalled」与
「不可逆事件只广播携带权威事件证据的边界，缺证据不得用预测事件冒充」两条写回契约正文。
在此之前，`_r4a_prediction_report.md` §3 与本节即为这两条语义的当前事实源。

## 8. 未实现 / 未声称（不因本批全绿而标 T43/T44）

1. **Unity 物理等价性 PENDING**：碰撞是确定性 AABB 替身（`PMMoverTestWorld`），本批只声称"在该替身几何下"的墙/落地/滑动行为与手算契合；PhysX 适配器属 R4-B。
2. **真实网络承载未接线**：谁调用 `ApplyAuthority/Resync/Freeze/NotifyWallClock/AlignToLatest`、权威帧与**权威事件证据**如何从 R3 复制流进入预测核心、DS 如何把 `Pump` 结果送上网 —— 均未实现、未验证（T43/T44 仍 PENDING）。
3. **完整效果/行为类机制未实现**：行为类 Modifier 实例级补偿、Effect/Layer 的网络策略与**拒绝裁决**（谁有权下发/如何撤销）全部后置；本批只把 Effect/Layer 当作"已鉴权的宿主可信命令"消费（P4A3 已登记，本批沿用）。
4. **"无证据不广播"的宿主补发通道未实现**：本批只保证缺证据边界一条都不广播（`ConfirmedEventFramesSkipped` 可观测）；谁经可靠事件通道补、如何与已确认区间对齐（是否引入“事件确认水位”）属 R4-B 设计。
5. **中间边界证据的来源限制（本章程诚实口径）**：`EvidenceForRange` 对中间边界只能回放本宿主已确认的链（没有逐边界服务器事件通道），因此本门禁**不能**证明“中间边界事件一定来自服务器”；能证明的是：权威边界事件必来自权威快照、缺证据必不广播、跳确认不伪造（L 节）。
6. **不声称等价于 UE 语义**：dt 整毫秒 1..50、置信窗口 128、DS 信用 50/200、SP 外推 0 都是本项目首批适配决策；单帧一次积分（无子步循环）、无斜坡/台阶/移动平台/空中操控仍是本批缺口。
7. **阈值仍是初值**：位置 0.05m / 速度 0.01m/s / 朝向 1°、胶囊 0.4×1.0、`Acceleration/Braking/JumpSpeed` 均为占位（测试里 `JumpSpeed` 设为 1.2 仅为把落地帧压进 88 帧内）。
8. **P4A4 的验收口径**：B/C/D/E/F/J/K/L 全绿 + 无已知 FAIL ⇒ 本轮"强制全状态差异后收敛 + 事件证据边界"目标已取得证据；
   但 T43/T44（真网络角色 / Unity 物理）仍 PENDING，**不代表整个 R4-A 已通过验收**。

## 9. 已检查范围（文档如何决定入口）

- `D:/UGit/hyld-master/AGENTS.md` → 客户端 `Client/Assets/AGENTS.md`（帧时长/坐标系/输入与发送链路）、服务端 `Server/AGENTS.md`（DS 帧号语义/输入处理现状）、迁移计划 `Docs/plans/net-architecture-migration.md`。
- 主计划文末 **R4-A 行**（§2045–2059）：P4A4 = 「真实预测核心+Mover模型 | PMMoverPredictionTest | 强制全状态差异后收敛」→ 决定"必须装进同一进程""必须强制差异后收敛"。
- `Docs/plans/net-r4-prediction-contract.md` 全文：帧语义 n→n+1、深 clone、**重放必须用原始输入与原 dt**、
  确认事件只随 `ConfirmedFrame` 一次且回滚替换同帧集合、**「状态恢复只接受显式可信 Resync」**、
  DS 8 步/100ms + 信用 50..200、缺帧不跨越、SP 首批只插值且外推 0、Mover 冻结字段/四模式/两层混合/四个 InstantEffect、
  首批验收 P4A1..P4A4 —— 逐条映射成本批 A..L 的断言。
- `_r4a_prediction_report.md`（P4A1/P4A2 实际 API 与本轮修复 1/2 的语义、`HistoryMissing 不可达`等已知边界）、
  `_r4a_mover_report.md`（P4A3 实际 API、D1..D13 语义决策、未实现项）→ 据此**只用已落盘的公开 API**，并沿用其"诚实口径"。
- 本轮上游发现来自本报告 §7 的自有复现（K4/K5/K7、F11），修复后已由 §5.1 的两次注入与全绿结果闭环。
- 只读源码：`Client/Assets/Scripts/PMPrediction/*.cs`（含本轮新增的 `TryCollectEvidence`/`BroadcastConfirmedRange` 语义、
  `Stalled` 门位置）、`Client/Assets/Scripts/PMMover/*.cs`、`Client/Assets/Scripts/PMNet/{PMNetIdentity,PMNetRole}.cs`。
- 门禁工程风格参考：`Tools/PMMoverTest`、`Tools/PMPredictionTest`、`Tools/PMPredictionCoreCheck`、`Tools/PMMoverCoreCheck`（netstandard2.0+C#7.3 函数库门禁 + net8 断言门禁、退出码 0/1、直接编真实源码）。
- Windows 命令按 `windows-shell-compat`：Pi bash 工具按 Bash 解析，未混用 PowerShell 语法；
  文本改写统一用 Python 以 `utf-8-sig` + `newline='\r\n'` 读写，保证 BOM+CRLF 不变（已用 `od`/`file` 复核）。
