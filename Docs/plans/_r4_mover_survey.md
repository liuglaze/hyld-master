# R4-A 首个 Mover 可回滚模型接口冻结调查（explore，只读）

> 边界：只读 hyld R0 Mover 条款、`Client/Assets/Scripts/Shared/PMBattleSim.cs`、PMR3 运行时边界；UE 侧只参考 `mover-quick-start` 技能 Mode/Mixer/Modifier 直接实现与 `D:/hyld-refactor-survey/R0_1_mover_np.md` 证据段。不实现、不编译、不扩 R5。首阶段只允许纯 C# 碰撞替身，**不得当成 Unity 物理通过**。

## 文档路由（必读顺序 → 它们决定的搜索入口）

| 顺序 | 文档 | 提供的首搜入口 |
|---|---|---|
| 1 | `D:/UGit/hyld-master/AGENTS.md` | 指向 `Client/Assets/AGENTS.md`、`Server/AGENTS.md`、`Docs/plans/net-architecture-migration.md` |
| 2 | `Client/Assets/AGENTS.md` | §1.1 进程形态（`IsDedicatedServer` 唯一判定）、§3 客户端预测链路、§7 参数表、坐标系（Y-up，Y 恒 1） |
| 3 | `Server/AGENTS.md` | §5 CMC-style ClientMove 时间轴、§3.1 战斗 7 文件、§1.2 无头保活 |
| 4 | 主计划 §3.9 / §5 R4 / T43-T44 | 模块硬边界（M07/M08/M11）、运行时契约、验收口径 |
| 5 | `net-r0-contract.md` §2.3/2.5/3.6/6/8/9 | D-R0-19..29 冻结决策、`IPMPredictedModel` 签名、Mover 契约表 |
| 6 | `D:/UE_Project/ProjectMecury/AI-Instruction.md` | 文档优先、中文、函数必须带类名与作用、不主动编译/提交 |
| 7 | `Docs/战斗模块架构地图.md` | §3 路由表把 Mover 问题指向 `战斗预测与权威.md`；§5 确认 Mover 属 `Pawn/Movement/` |
| 8 | `mover-quick-start/SKILL.md` | 红线 1/2/3/6/14、§5.3 九阶段数据流、§5.2 概念表 |
| 9 | `references/architecture-detail.md` / `CMC_vs_Mover_框架差异说明.md` / `network-prediction-checklist.md` | 17 步数据流 + IndependentTick Service 组合；`Auto` 四身份语义；NP 合规 6 检查项 |
| 10 | `R0_1_mover_np.md` | U1–U16 契约项 + A/B/C/D/E/F/G/H 源码证据段 |

## 一、已确认

### 1.1 模块与文件草案（M08 可与 M07 泛型核心独立实现，冻结后只读依赖）

- **M07 Prediction（新建，纯 C#）** `Client/Assets/Scripts/PMPrediction/`：`PMFrameTypes.cs`（`PMFrameId`/`PMTimeStep`）、`PMStateContracts.cs`（`IPMInputCmd`/`IPMSyncState`/`IPMAuxState`）、`PMPredictionHistory.cs`（每实例环形缓冲，存 `{InputCmd, SyncState, AuxState, DeltaMs}`）、`PMPredictedModel.cs`（唯一副作用落点 `FinalizeFrame`）、`PMPredictionReconciler.cs`（`ShouldReconcile` → `Restore` → `[authorityFrame, pendingFrame)` resim）。**不得**依赖 `BattleData`/`MainPack`/`UnityEngine`。
- **M08 Mover（新建，纯 C#）** `Client/Assets/Scripts/PMMover/`：`PMMoverModel.cs`、`PMMoverModes.cs`（Walking/Falling/Flying/Inactive）、`PMMoverState.cs`（Input/Sync/Aux 三结构）、`PMMoverMixer.cs`（两层）、`PMMoverEffects.cs`、`PMMoverLayeredMoves.cs`、`PMMoverModifiers.cs`、`IPMCollisionQuery.cs` + `PMMoverCollisionStub.cs`。**只读**引用 1.1 的 M07 契约。
- **共享数学**：`Client/Assets/Scripts/Shared/`（现由 `Tools/PMSharedConfigCheck` 以 netstandard2.0+C#7.3 零依赖编译），新增 `PMMoverNumeric.cs`；坐标/轴向转换函数只放这一处。
- **宿主适配**：Unity 侧 `Client/Assets/Scripts/PMUnity/`（当前不存在，需新建）；DS 宿主接线点已在 `PMDsSessionHost.Initialize`（§7 固定碰撞场景 + §8 UDP 端点）与 `PMDsSessionHost.Pump`（`PMDsHost.Update` 驱动）。
- 注意：`Tools/PMNetLangCheck` 通配符是 `PMNet/**`，新建的 `PMPrediction/`、`PMMover/` **不在**其覆盖内，需各自新增门禁（照抄 `PMSharedConfigCheck.csproj` 形态）。

### 1.2 首个模型所需 Input / Sync / Aux（字段级草案）

- **InputCmd（每帧重建、必须过网、可重放；dt 在帧信封里，不放 InputCmd）**：`moveX/moveY`（原始摇杆量，**不归一化**——归一化是派生）；`aimX/aimY`（意图）；按键位（jump）；`suggestedMode`；三条 Pending 队列：`QueuedEffects`、`QueuedLayeredMoves`+`RemoveLayeredMoveIds`、`QueuedModifiers`+`RemoveModifierIds`（对齐项目 `FPMCharacter*Inputs` 结构，但只保留本阶段用到的）。
- **SyncState（逐帧持久、参与比较、可重算）**：`location`+`rotation`、`linearVelocity`、`movementMode`、`activeLayeredMoves[]`（instanceId+参数+elapsed）、`activeModifiers[]`（networkId+业务键+参数）、运动参数组（`bHas*` 标志 + gravityScale/maxWalkSpeed/maxFlySpeed/accel/decel/friction/turningRate）、`bGrounded`+`groundNormal`（物理结果，**必须进 SyncState**）、基座引用 + **捕获时是否真用了平台的持久位**、`scale`。
- **不与 SyncState 混放的帧标签**：`serverFrame`/`simTimeMs` 放独立结构并**硬编码 `ShouldReconcile=false`**（对齐项目 `FPMMoverFrameSyncState`）。
- **AuxState（很少变、透传）**：世界重力向量、`collisionVersion`、运动设置版本。
- 模式差异：Walking 需要地面查询（向下 sweep）+ 摩擦/坡度；Falling 需重力积分 + 落地判定（复用同一地面查询）；Flying 需要垂直输入 + 碰撞 sweep；**Inactive 完全不加速度/加速度，但 NPP 继续跑**（禁止用 `EnableMoverSimulation(false)` / `SetMovementActiveState(false)` 代替"禁止移动"）。

### 1.3 已有 `PMBattleSim` 可复用面与不可复用面

- **可复用**：①"零依赖 + 标量进标量出"的工程范式（正是它逼出 netstandard2.0/C#7.3 门禁）；②`TryGetMoveDirection` 的轴互换/镜像约定（`moveX/moveY` → 世界方向 + 调用方自算 `sign`）；③`ZeroEpsilon`；④float32 全精度、不量化。
- **不可复用（禁止改写用途）**：`TryAdvancePosition` 是**旧模型**——按 `frameCount` 一次推进多帧、无重力/摩擦/加速度/碰撞、Y 恒 1.0，与 D-R0-19"一帧 = 一条输入 + 它自带的 dt"直接冲突，只能当退化参考/oracle；`TryGetVelocity`/`SpreadDirection`/`BulletStepDistance`/`IsBulletExpired`/`IsHit`/`RechargeSuperEnergy` 属投射物与伤害（R5），本阶段不得扩大。**没有竖直轴、没有碰撞、没有重力**，因此不能充当 Walk/Fall/Fly。
- 坐标系陷阱：该文件假设 Unity **Y-up 且 Y 恒为高度**；UE Mover 是 **Z-up**。任何从 UE 语义搬来的公式必须在这一处转换点做一次轴重命名，禁止双端混用。
- `sign`（队伍镜像）语义要保留（客户端自锚定 +15 / 服务端基锚定），但锚点规则必须**只冻结一处**，否则位置互操作会错。

### 1.4 Effect 两次消费 / LayeredMove 混合 / Modifier 快照与拒绝撤销（分阶段）

- **Effect 两次消费**：阶段 A（子步循环之前）消费 InputCmd 的 Pending Effect 队列并写入工作 SyncState；改模式者立即切模式并回拷工作起点。阶段 B（子步循环结束后）消费循环中**新入队**的 Effect。两次都在 `SimulationTick` 内（因此会被 resim 重放），且只许写工作状态。
- **Effect 同步铁律**：`Apply` 必须写 `OutputState`，否则等同于"没有同步"（项目 `FPMModifyCollisionResponseEffect` 的历史教训）。任何外部写入（碰撞通道、真实胶囊 Scale）不得在 `SimulationTick` 内做，只能作为 SyncState/Modifier 数据，在 `FinalizeFrame` 重建。
- **LayeredMove 混合**：两层——第一层 `MixLayeredMove`（Move↔Move），第二层 `MixProposedMoves`（Move↔Mode）；顺序**LayeredMove 先于 Mode**，Mode 基于 `RestorePreAdditiveVelocity` 的干净速度计算。Additive 门槛只排除 `OverrideAll`/`OverrideVelocity`。阶段 1 只实现 `AdditiveVelocity` + `OverrideVelocity`；枚举**数值位**先冻结（避免以后重排改变位协议），`OverrideVelocityAdditiveVertical`/`OverrideAllAdditiveAll` 后置；所有 switch/OR 链必须带 default（项目 `UPMClimbWallComponent` 漏 case 会穿墙）。
- **Modifier 完整快照与拒绝撤销（两阶段）**：
  - 阶段 1：只有**数据类** Modifier（不写 `OnStart`/`OnEnd`）。完整快照 = 整个活跃 Modifier 列表随 SyncState 逐帧 Clone；reconcile 用"双方 networkId 均非零则精确比 ID，否则退化业务键"。数据类必须**双重 opt-out**（id 保持 0 **且** 显式声明不参与 ID 追踪）。
  - 阶段 2：**行为类** Modifier 才允许副作用，且必须有完整的实例级回滚补偿（对应 `RollbackModifiers`）；`Matches()` 必须能区分实例（业务键 + 非零实例 ID）；SourceId 禁 `GetFName()`，用预留的跨端唯一 seed 拼。
  - **LayeredMove 禁止携带不可逆副作用**（引擎 `RollbackLayeredMoves` 是空实现，D-R0-25 照抄，不补）。
  - **拒绝撤销**两条通道：①AP 本地入队但 DS 未采纳 → 权威 SyncState 不含它 → reconcile 后 resim 自然丢弃（因为入队本身走 InputCmd 被重放）；②DS 主动拒绝 → 权威帧里就没有。外部可见事件靠 `predictionFrameHistory` 环形缓冲，只在 `confirmedFrame` 越过该帧后广播 `(LastConfirmed, New)` 区间——这就是"不重复触发"的机制。

### 1.5 供模型双端使用的 Unity 碰撞查询纯接口（冻结）

```
public interface IPMCollisionQuery {          // 纯 C#，零 UnityEngine 依赖
    bool Sweep(PMCollisionShape shape, PMVec3 start, PMVec3 delta, out PMHit hit);
    bool GroundQuery(PMCollisionShape shape, PMVec3 position, float maxDistance, out PMGroundHit hit);
    bool Overlap(PMCollisionShape shape, PMVec3 position, out PMOverlapHit hit);
    int WorldVersion { get; }                 // 进 AuxState；对应 R3 的 CollisionDigest
}
```
- 全部用自建 `PMVec3`（float32），**不出现** `UnityEngine.Vector3` 或 UE `FVector`。
- 阶段 1 实现 = `PMMoverCollisionStub`：纯 C# 盒体求交，几何对齐 R3 已冻结的固定测试场景（地板中心 `(0,-0.5,0)` 尺寸 `(40,1,40)`、墙中心 `(0,1,0)` 尺寸 `(1,2,8)`，`PMR3Runtime.CollisionDigest = 0x52334201`）。**它只是替身**：测试只能断言手算期望值，不得断言与 Unity PhysX 等价；`PMUnityCollisionQuery`（包 `Physics.SweepTest`/`CapsuleCast` 并转 `PMVec3`）为 PENDING_USER。

### 1.6 坐标系冻结口径

- hyld Unity 世界 **Y-up**：X 水平、Z 屏幕纵向（战斗平面）、Y 高度（玩家 Y=1）。
- UE Mover **Z-up**：XY 为战斗平面、Z 为高度。
- 冻结：模型内部只声明一个 up 轴（建议 = Y-up，省掉 Unity 边界转换），所有跨来源向量只经 `Shared/PMMoverNumeric.cs` 的单一转换函数；UE 公式移植时必须在该点重命名轴向。重力轴 = up 轴的负向，不得两处各写一遍。

### 1.7 无外部副作用 / 预测不重复触发的最小验收

- **A1 帧**：N 条输入 dt={16,17,16,…} → 产生的步数与 `Σdt` 与输入一致；注入 `dt=0` 丢帧占位 → 不推进模拟。
- **A2 确定性**：同 `(startState, inputCmd, timeStep)` 连调 10 次输出逐位相等；禁止墙钟/随机/非 SyncState 可变数据。
- **A3 校正**：强制位置/速度/Mode/Modifier 差异 → AP 恢复完整状态后 resim 次数 == `pendingFrame - authorityFrame`，末态在容差内。
- **A4 副作用不放大**：强制每帧 rollback 下，"事件计数"（模式变化/落地/外部通知）== 业务发生次数。阶段 1 **不产生任何外部副作用**，故该计数必须恒为 0。
- **A5 混合**：Additive 叠乘顺序与 `Restore/SavePreAdditiveVelocity` 用数值断言验证；Override 不掉 Additive 之外的行为按冻结枚举校验。
- **A6 Modifier 快照**：数据类逐帧刷新 60 帧 → 回滚计数 0，且永不出现在活跃 ID 集合里。
- **A7 碰撞诚实性**：替身结果对手算盒体场景通过；**不得**声称 Unity 物理通过，适配层 `PENDING_USER`。
- **A8 Update 频率无关**：不同 Update 频率下每条输入产生的步序列一致（dt 驱动），而非"每墙钟状态一致"。

### 1.8 待主 Agent 先冻结的点（字段 / 时间 / 效果 ID）

1. **状态归属**：`bGrounded`/`groundNormal` 属 SyncState；"本帧落地"是**事件**（进 predictionFrameHistory）而不是状态。是否保留基座 `bHasMovementBaseData` 持久位？
2. **up 轴与转换点**：模型内部 Y-up（建议）还是 Z-up；`sign` 与锚点规则落在哪个函数。
3. **InputCmd 字段定稿**：moveX/moveY 保持原始量；aim 用轴还是角度；jump 是布尔还是边沿。
4. **容差量纲**：UE 是 5cm；hyld 旧阈值是 `MovementMaxPositionError=0.6`（unit）。R4 复用 0.6 还是另定，必须写死在一个常量里。
5. **帧号命名与跨端唯一锚**：候选为 `clientInputFrame`（AP↔DS 1:1）；DS 侧每对象 `serverFrame` 的映射规则；`NONE` 禁止参与差值。
6. **预测历史容量**：UE/项目 128 帧 vs 旧 hyld 40；需覆盖 RTT + 回滚深度。
7. **Effect/LayeredMove/Modifier 的 ID 分配**：Server/Client 分区间非零 ID；数据类双重 opt-out；Modifier `Matches()` 的业务键定义。
8. **Effect 身份策略**：哪些走 `LocalPredict`、哪些走 `ServerInitialize`（DS 主动击退等）；"专服玩家 + `Auto` → 拒绝"必须显式处理。
9. **阶段 1 测试世界几何与版本号**：沿用 `0x52334201` 还是新增 R4 digest。
10. **承载对象**：复用 R3 的 `PMR3Player` 还是新建声明对象（决定用哪份 ID 锁与生成注册表）。

## 二、高概率推断（依据与置信度）

- **P1（高）R4 是绿地**：全仓 `Client/Assets/Scripts` 与 `Server` 下 grep `SyncState|InputCmd|LayeredMove|ShouldReconcile` 零命中，且无 `PMPrediction/`、`PMMover/` 目录 → M07/M08 全新，无历史包袱。依据：`fffind`/`grep` 结果 + 主计划 §3.9.3 把两者列为拟定落点。
- **P2（高）DS 主线程 tick 已就位**：`PMDsHost.Update` 用累加器 + `MaxTicksPerUpdate=8` 驱动 `PMDsSessionHost.Pump`，R4 只需在 Pump 之外挂"排空输入队列 → 逐步 SimTick"，不需要新线程。依据：`PMDsHost.cs:380-429`、`PMDsSessionHost.Pump`。
- **P3（高）阶段 1 不能声称"Unity 物理通过"**：R3 的碰撞是运行时程序化 BoxCollider，`CollisionDigest` 明确"不声称旧地图摘要"；且门禁 `PMUnityGlueCheck` 自述"UnityStubs 是手写桩件，通过 ≠ 逻辑正确"。因此替身验收只能证明数学与管线，物理等价必须 PENDING_USER。
- **P4（中）Effect 两次消费在 Unity 模型里可保留**：其存在理由是"子步循环内新入队的效果本帧生效"；只要模型保留子步循环，两条消费点就有意义。依据：`architecture-detail.md §2` 步骤 5 与 14、`R0_1` B.2 步骤 4/18。
- **P5（中）`PMBattleSim` 的 `sign` 约定可原样继承，但锚点必须显式冻结**：客户端自锚定 +15 与服务端基锚定是等价两写法，R4 若引入新位置表示必须沿用同一套，否则与旧 MoveAck 对照会对不上。依据：`PMBattleSim.cs` 头注释。

## 三、无法确定（缺少证据）

1. **每引擎帧最多消费多少输入帧、累计 ms 上限**（UE `MaxRemoteClientStepsPerFrame`/`MaxRemoteClientTotalMSPerFrame` 的配置源与默认值）在 R0_1 已列为缺口，R4 的实现参数因此未定。
2. **AP 端本地输入帧环形推进的确切触发点**：R0_1 只由 `TIndependentTickReplicator_AP::NetRecv` 反推 `ConfirmedFrame`，`NetworkPredictionService_Input.inl` 未逐行读完 → A1/A3 的"pendingFrame 何时 ++"仍是推断。
3. **UE 5cm 容差在 hyld 单位下的对应值**：旧项目用 0.6 unit 阈值，两者量纲关系无证据 → 见 1.8#4。
4. **真实 Unity 物理适配行为**：本机无 Unity 可测（`Client/Assets/AGENTS.md` 与 `CMC_vs_Mover` 均记用户侧无 Unity），故 `IPMCollisionQuery` 的 Unity 实现与其在手感上的影响不可验证。
5. **DS 侧每对象 serverFrame 与 AP `clientInputFrame` 的 1:1 关系在"多玩家共享一个 Unity 进程"下是否仍成立**：UE 是每 Actor 独立 tick，hyld DS 是一进程多对象，映射规则无既有实现可参照。

## 四、已检查范围

- **必读项目文档（按任务顺序）**：`D:/UGit/hyld-master/AGENTS.md`、`Client/Assets/AGENTS.md`（§1.1/§3/§7/§12）、`Server/AGENTS.md`（§1.1/§3.1/§5/§12）、`Docs/plans/net-architecture-migration.md`（§3.9 全节、§5 R4 行、T43/T44 行、D14）、`Docs/plans/net-r0-contract.md`（§1 映射表、§2.3/2.5/2.6/3.6/6/8/9/11）、`D:/UE_Project/ProjectMecury/AI-Instruction.md`、`Docs/战斗模块架构地图.md`（§3 路由表、§5）、`mover-quick-start/SKILL.md`（全文）、`references/architecture-detail.md`、`references/CMC_vs_Mover_框架差异说明.md`、`references/network-prediction-checklist.md`、`references/move-mix-mode.md`、`references/Modifier使用规范.md`（全文十一节）、`D:/hyld-refactor-survey/R0_1_mover_np.md`（§0–§H + U1–U16 + 缺口 + 已检查范围）。
- **实际读取的源码**：`Client/Assets/Scripts/Shared/PMBattleSim.cs`（全文）、`Shared/BattleNumericConfig.cs`（头与 MoveSpeed 段）、`PMR3/PMR3Runtime.cs`（全文）、`PMR3/PMR3Player.cs`（全文）、`Server/Boot/PMDsSessionHost.cs`（`PMR3TestScene` + `Initialize` + `Pump`，共 520 行）、`Server/Boot/PMDsHost.cs`（Update/tick 段）、`Docs/plans/net-r3-control-contract.md`（§7.3/§7.4/§8/§9）、`Tools/PMSharedConfigCheck/PMSharedConfigCheck.csproj`、`Tools/PMNetLangCheck/PMNetLangCheck.csproj`、`Tools/PMUnityGlueCheck/PMUnityGlueCheck.csproj`、`Tools/PMBattleSimTest/PMBattleSimTest.csproj`。
- **主动排除（有界边界）**：`R0_2/R0_3/R0_4` 与 S1–S8 调查报告（与本问题无直接关系）、`Server/Server/*`（旧战斗）、`Modifier使用规范.md` 引用的 `CollisionResponseModifier问题分析与修复方案.md`、UE 引擎源码本体（只在 R0_1 证据段内引用）、任何 R5 投射物内容。

## 五、建议下一步（最小补充查询 / 运行时验证）

1. **补齐三个时间参数**（解除 §三.1/§三.2）：读 `NetworkPredictionService_Input.inl` 全文 + `Ticking.inl:73-220`（AP 本地输入推进与 `PendingFrame++`）+ grep `GetNetResilienceServerCatchupMaxSteps`，冻结"每帧最多消费输入帧数/累计 ms 上限"。
2. **冻结 1.8 的十个决策点**（主 Agent 一次给全），再据此写 M07/M08 的接口文件骨架（只写签名 + 注释，不含实现），作为 R4 实施冻结契约。
3. **纯 C# 门禁先行**：新增 `Tools/PMMoverContractTest`（照抄 `PMSharedConfigCheck.csproj` 的 netstandard2.0+C#7.3 零依赖形态）承载 A1/A2/A5/A6，并用 `PMMoverCollisionStub` 承载 A7 的手算断言；**A3/A4 需要能注入"每帧强制 rollback"的测试开关**。
4. **运行时验证（Unity 侧，PENDING_USER）**：多端 PIE，`np.PrintReconciles 1` 对照 `pendingFrame/confirmedFrame`；断线→墙钟兜底；`PMUnityCollisionQuery` 打开后与替身结果做差异对照，明确记录差异来源（`IPMCollisionQuery` 契约是否够用）。
5. **不扩到 R5**：投射物/命中裁决相关公式与结论本次一律不带入实现。
