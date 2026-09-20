# R4-A 冻结纯预测核心接口（只读调查）

> 范围：hyld R0 预测条款 + M01 身份/帧 + Shared 数学 + `D:/hyld-refactor-survey/R0_1_mover_np.md` 预测段 + UE 技能指定的 IndependentTick 参考。
> 事实等级：**已确认**项均带来源；**推断**项标置信度；**未冻结**项交主 Agent 裁决，不当作 UE 事实。
> 入口链：主计划 §3.9.3/§3.9.4/§5 R4 行 → `net-r0-contract.md` D-R0-19..29/§6/§9 → `R0_1_mover_np.md`（证据附录）；UE 侧 SKILL §5.2.1 → `npp-independent-tick-frame.md` → 其直接引用 `confirmed-frame-alignment.md`、`localpredict-disconnect-recovery.md`。

## 一、已确认（冻结）

### 1. 帧单位 / 时间步
- **一帧 = 一条 InputCmd + 它自带的 `DeltaTimeMS`**；由输入 dt 驱动，**不是固定 16ms**、不是墙钟；撤回旧 P3'-3b 的 16ms 累加器（D-R0-19 / `R0_1` U1）。DS 可在同一引擎帧内连续消费多步，但**每步必须用原输入的 dt**，不得合并或放大 dt（`R0_1` §0-1、U1）；"降频"只允许**纯跳帧（丢时间）**（D-R0-28 / U14）。
- `PMTimeStep` 已落地：`BaseSimTimeMs / StepMs / ServerFrame / ClientInputFrame / IsResimulating` + `IsWellFormed()`（`PMNetIdentity.cs`）。

### 2. 帧号命名空间（严格隔离）
- 四命名空间互不相通：`Input(N)` / `AuthorityServer(K)` / `SimTime(T)` / `Session(W)`（D-R0-20 / `R0_1` C.1、U5）。
- `PMFrameId` 已实现：跨命名空间**相减与顺序比较均抛异常**；`None` = UE `INDEX_NONE`，**禁参与差值/比较**（`PMNetIdentity.cs`）。
- `ServerFrame` 只对该 Actor 有意义、不可跨 Actor 比较；`ClientInputFrame` 只有 AP↔DS 可比；`SimTime` 每 Actor 独立（`R0_1` C.1/C.4）。`ClientInputFrame == NONE` = 本端即权威（退回 ServerFrame）；`ConfirmedFrame == NONE` = **映射未建立**，禁算差值（`R0_1` C.3、U5）。

### 3. 输入 n → 输出 n+1（帧对应硬事实）
- DS 远程 tick：`InputFrame = ServerRecvData.PendingFrame++; OutputFrame = ServerRecvData.PendingFrame;` ⇒ **输出帧 = 输入帧 + 1**（`R0_1` C.2，`NetworkPredictionService_Ticking.inl:435-448`）。
- AP 复制：`ClientRecvState.ServerFrame = LastConsumedInputFrame + 1; TickState->ConfirmedFrame = ClientRecvState.ServerFrame;`（`R0_1` C.2，`NetworkPredictionSerialization.h:703-707`）。
- **ConfirmedFrame = `LastConsumedInputFrame + 1`（AP），不是 DS 的 K**；AP↔Server 帧号 1:1 无 Offset（`R0_1` §0-7、C.2；`npp-independent-tick-frame.md` §2）。项目 `GetConfirmedSimFrame()`：Authority / SP → `TickState.PendingFrame`；AP → `TickState.ConfirmedFrame`（`R0_1` C.2）。
- `PendingFrame` = **下一帧要 tick 的帧号**（刚 tick 完的是 `PendingFrame-1`）；取帧用 `FNetworkPredictionProxy::GetPendingFrame()`，**不要**用全局 `FVariableTickState::PendingFrame`（`npp-independent-tick-frame.md` §3）。

### 4. 三层状态边界（Input / Sync / Aux）
- `InputCmd`：每帧**清空重建**、必须过网、**可重放**；只放"玩家按键/摇杆能直接决定的值"，派生量（加速度、速度、命中结果、时间函数）禁止入内（D-R0-21 / `R0_1` A.2/A.3、U2）。
- `SyncState`：逐帧持久、**参与 `ShouldReconcile`**；含 `MovementMode + LayeredMoves + MovementModifiers + 自定义类型集合`，`NetSerialize` 显式写这 4 项（`R0_1` A.1/A.2、U2）。
- `AuxState`：**直接透传**（`SimOutput.AuxState = SimInput.AuxState`），语义是"很少变、改了会改变后续模拟走向"的输入型状态（`R0_1` A.2/A.3）。
- `bSkipReconcile` 是**非序列化瞬态标志**（外部接管用），不得序列化（D-R0-27 / U13）。M07 边界：纯 C# 核心，**不得直接依赖 BattleData / MainPack / Mover 具体玩法**（主计划 §3.9.3 M07）。

### 5. ACK / 确认边界（两套，不要混）
- **预测确认边界 = `ConfirmedFrame`**：不可逆副作用广播的唯一时机；`AdvanceConfirmedFrame` 只广播 `(LastConfirmedFrame, NewConfirmedFrame]` 区间事件（D-R0-24 / `R0_1` D.6、U8）。
- **复制 ACK** 是另一回事：每连接"已确认版本"，**ACK 只能前进，旧 ACK 不得清除新脏位**；`Replication` 流的包级 ack 只确认版本、不触发重传（`net-r0-contract.md` §4/§5）。禁止跨顺序域假设顺序；状态走复制流、一次性事件走 RPC（D-R0-05/09 / §5 分工）。

### 6. AP ShouldReconcile / Restore / Replay
- **只有客户端回滚**：`ReconcileSimulationsPostNetworkUpdate()` 首行 `if (NetMode != NM_Client) return;`；`TIndependentRollbackService::RegisterInstance` 有 `npEnsureSlow(NetRole == ROLE_AutonomousProxy)` ⇒ **SP 不注册回滚服务**、DS/Standalone 不回滚自己（D-R0-22 / `R0_1` D.1、U6/U7）。
- **判据是同帧 `ShouldReconcile(predicted, authority)`，不是帧号不等**。逐层：`FMoverSyncState`（**Mode 不等直接 true**）→ `bSkipReconcile` 短路 → `SyncStateCollection`（元素数不等 / 类型在权威缺失 → true）→ 各结构自定义（`R0_1` B.4、D.2、U6）。
- 默认容差：位置 `DistErrorTolerance = 5.f`(cm)；基座对齐用**持久位** `bHasMovementBaseData`（不是引用可解析）；旋转需独立角度阈值（`R0_1` B.4/U6）。
- **Restore 三件套**：① `OnSimulationPreRollback`（Modifier 完整补偿 / LayeredMove 空实现）② `SetFrameStateFromContext(bRebase=true)` ③ `OnSimulationRollback`（`ClearQueuedMode()` + 按 SyncState 的 Mode `SetModeImmediately` + 清空 LayeredMoves/InstantEffects/Modifiers 三组队列）（`R0_1` D.3、U7）。
- **重模拟区间 = `[权威帧, 本端 PendingFrame)`**，用本端已缓冲的**原始 InputCmd 与原始 dt**（`Step.dt` 取自 `TickState->Frames[InputFrame].DeltaMS`）（D-R0-23 / `R0_1` D.4、U7）。
- **会被重放**：PreSimulationTick/PreMovement 委托、Mode `Activate/Deactivate`、Modifier `OnStart/OnEnd`（补偿补发）+`OnPre/PostMovement`、LayeredMove `GenerateMove`（推进内部时钟）、InstantEffect（消费两次）。
  **不会被重放**：`FinalizeFrame` / `DispatchPostFinalize` / `NativeOnPostFinalize`（每帧一次、不在 resim 循环内）、`OnAuthorityStateAccepted`（`ShouldReconcile==false` 分支专用）、`RollbackLayeredMoves` 补偿（空实现）（`R0_1` D.5，`NetworkPredictionService_Finalize.inl:41/117`）。
- **副作用只允许落 `FinalizeFrame`**；不可逆事件等 `ConfirmedFrame` 推进后广播（D-R0-24 / U8）。
- 非对称性：**Modifier 有完整实例级回滚补偿（按 `Matches()` 补发 OnStart/OnEnd），LayeredMove 完全没有** ⇒ LayeredMove 不得带不可逆副作用（D-R0-25 / U9）。
- 项目三扩展 SyncState：`FPMMovementParamsSyncState` 参与回滚（逐字段比 `bHas*`+数值，刻意排除 `JumpHorizontalSpeedCap`）、`FPMActiveModifierIDsSyncState` 参与（`TArray<uint16>` 精确相等）、`FPMMoverFrameSyncState` **硬编码 `ShouldReconcile()=false`**（只当帧/时间标签，永不触发回滚）（`R0_1` F.1/F.2/F.3）。

### 7. SP 时间插值
- IndependentTick 下 **SP 只能插值**（`IndependentInterpolate` 唯一），**无等价帧号**，对齐量是 `SimTimeMS`/`TotalSimTimeMS`（`npp-independent-tick-frame.md` §2/§5；`R0_1` E.1、U10）。
- `TotalSimTimeMS`(W) 是**唯一**给所有 SP 共享展示时间轴的量；SP 包外层写共享 W（`R0_1` C.1/C.4、E.2）。
- 外推**必须有上限**（`GetSimProxyMaxExtrapolateMS()`），超限**冻结在上一权威态**；收到新包时外推量归零并刷新权威基准（`R0_1` E.3、H.4-7、U10）。**位置与旋转独立判定/收敛**：`HardSnapThreshold` 直接跳、`SnapThreshold` 内直接对齐、否则指数收敛并限速；`TurningRate < 0` = 旋转瞬转（`R0_1` U11）。
- `Interpolate` 对**离散量必须 Snap 取 To**（模式名、帧号、bool、时停系数），只有连续量才 Lerp（`R0_1` U2、F.2/F.3）。

### 8. 历史容量与溢出 / 断线重同步
- 项目 `UPMPredictionFrameHistory` 是**容量 128 帧**环形缓冲，记录 `{ServerFrame, MovementMode, bIsResimulating, bMovementModeChanged, bLanded, LandedHitResult}`；`RecordFrame` 被覆盖的帧不得再广播（`R0_1` D.6、U8）。
- **溢出要有安全网**：跳过并清理孤儿等待（`R0_1` U7）；不得把"当前值"当历史用（主计划 §3.9.4"历史溢出、权威陈旧、断线恢复必须定义重同步策略"）。
- **断线 = NPP 整段冻结**（`while (LastConsumedFrame < LastRecvFrame)` 不成立 ⇒ 不产 ServerFrame、SimTick/Finalize 全停）⇒ **一切帧号类超时/Grace 失效**，必须改**墙钟 + 非 NPP 驱动源**（D-R0-29 / `R0_1` E.4、U12；`localpredict-disconnect-recovery.md` §2）。
- 断线/重连重同步通道（已定稿）：`Server_RequestResyncMovementParams(ClientLastFinalizedSimFrame)` → `ResyncMovementParamsFromCurrentState()`，**仅 DS 执行**、走 `ServerInitialize` Effect 管线重新点火参数 SyncState、**禁止手写 DoubleBuffer**；`TrackReconcileFailureDensity` 默认关闭；**恢复 RPC 恒 `NetReport` 不 `NetReject`**（否则永久卡死）（`R0_1` E.4、A.5；该 reference §3.4/§4）。
- 恢复时必须**强制** `InitializeForNetworkRole(...)`（否则 ConfigFunc 不执行、Service 不订阅、SimTick 全不跑）；本端帧缓冲形态为每帧 `{inputCmd, syncState, auxState}`（`R0_1` E.4、U7）。

### 9. 可复用既有冻结件
- **M01**：`PMNetId` / `PMNetIdAllocator`（单会话单调、**永不复用**、主线程、静态动态共用编号空间、耗尽抛异常）/ `PMSession(Epoch)` / `PMFrameDomain` / `PMFrameId` / `PMTimeStep`（`PMNetIdentity.cs`；D-R0-01/02/19/20）。
- **共享数学**：`Client/Assets/Scripts/Shared/PMBattleSim.cs`（零依赖、只收标量、逐位复刻服务端公式、`sign` 由调用方给；门禁 345715 项逐位比对 0 失败）⇒ R4 数值应复用它，不要另起第二套公式。**原语**：`PMNetWriter` / `PMNetReader`；**声明/生成**：`PMNet/Declarations/`（零反射注册表，R2 已落地）。

## 二、高概率推断（依据 + 置信度）

| # | 推断 | 依据 | 置信度 |
|---|---|---|---|
| I1 | R4 首版按"**每实例 IndependentTick + 仅 AP 回滚 + SP 纯插值**"，不做全局固定帧 Lockstep | 主计划 §3.9.4"默认移植 IndependentTick；固定帧简化须记录差异与验收" + D-R0-19/22 | 高 |
| I2 | 核心可先无 UE 物理落地：`PMBattleSim` 极简模型（无重力/摩擦/加速度/碰撞、Y 恒定、零输入即停）足以承载 T43"完整状态校正重模拟" | `PMBattleSim` 文件头"模型极简，且这是刻意的" + 主计划"R4 测试场景可先于 R3" | 中高 |
| I3 | `ShouldReconcile` 应"先比离散 mode 标签、再比容器"，容器比较须覆盖"元素数/类型缺失"两类结构性差异 | `FMoverSyncState::ShouldReconcile` 顺序 + `FMoverDataCollection` 三态判定（`R0_1` B.4/U6） | 高 |
| I4 | 历史容量应取 `≥ RTT + 回滚深度`，不照抄旧 hyld 的 40 | `net-r0-contract.md` §9 明示收敛判据；项目 128 帧为现状值 | 中 |
| I5 | `OnAuthorityStateAccepted`（PM MOD by ZhangHang）在 hyld 侧对应"**校验通过分支**"，用于统计而非状态回滚 | `R0_1` B.4 把该回调放在 `ShouldReconcile==false` 分支 | 中（未读实现） |
| I6 | hyld 需"帧缓存同时存 Input + Sync（+Aux）"，否则无法区分"输入丢"与"状态丢" | `R0_1` U7 字段 vs D-R0-23 只要求 input+dt | 中 |

## 三、无法确定 / 未冻结（需主 Agent 裁决，禁止自行猜值当 UE 事实）

| # | 项 | 现状证据 | 需裁决内容 |
|---|---|---|---|
| U-1 | **DS 输入预算** | 只见使用点（`R0_1` §0-1、U1"单引擎帧最大步数 + 最大累计 ms"）；`R0_1` 证据缺口 #3 明确 `MaxRemoteClientStepsPerFrame` / `MinRemoteClientStepMS` / `MaxRemoteClientTotalMSPerFrame` 的**来源与默认值未读**；hyld 现状**无实现**（grep `Client/Assets/Scripts`、`Tools/`、`Server/` 全无 `MaxRemoteClient*`/`NetResilience*`） | 三上限取值 + 来源（照抄 NetResilience or 自定）+ "积压加速排空"阈值与是否允许 |
| U-2 | **DeltaTimeMS 合法范围** | 契约只说"来自输入、不得合并/放大"（D-R0-19）；`PMTimeStep.IsWellFormed()` 只校验非 NaN/非负，**无 min/max** | dt 上下限与钳制；dt=0 占位语义（跳过 Tick）；客户端 dt 漂移容忍度 |
| U-3 | **预测历史容量与溢出策略** | `net-r0-contract.md` §9 明确列为**不冻结**（"UE/项目 128；旧 hyld 40"，R1/R4 收敛）；只有"要有安全网"的定性要求 | 容量取值 + 溢出动作（丢最旧/整段清空/断线重同步）+ 是否同存 Input 与 Sync |
| U-4 | **ShouldReconcile 具体阈值** | 只有位置 5cm 与"旋转独立阈值"定性（`R0_1` B.4/U6） | 朝向角度阈值、速度阈值、容器容差（是否照抄 `UE_KINDA_SMALL_NUMBER`） |
| U-5 | **SP 外推上限与收敛参数** | 工程有 `GetSimProxyMaxExtrapolateMS()`/`HardSnapThreshold`/`SnapThreshold`/`RotationSnapThreshold`/`ConvergeSpeed`/`MaxCatchUpSpeed`，但**运行值未验证**（`R0_1` H.4-7、缺口 #7）；项目表现层是**引擎外自建**，`bEnableSimProxyPredictiveInterpolation` 当前值未验证 | 首版用引擎原生 `TickInterpolatedSimProxy` 还是项目式"预测旁路+自建表现层"；各阈值取值 |
| U-6 | **断线墙钟超时值** | 通用默认 2s（该 reference §2）；`ModifyCollision` 独立 3s 特例（§5）；恢复账本 key = 业务实例 ID | R4 首版统一值 + 是否引入"按需启停 Timer" |
| U-7 | **帧缓存是否含 Aux / 裁剪边界** | D-R0-23 只要求 InputCmd+dt；`R0_1` U7 写 `{inputCmd, syncState, auxState}` | 存储形态与裁剪时机（ConfirmedFrame 前 / 重模拟后） |
| U-8 | **M07 与 M05/M06 接口切面** | 主计划只说"经接口接 M05/M06" | 权威帧从复制流进预测核心的封装点（谁触发 `ShouldReconcile`、谁持历史） |
| U-9 | **AP 本地输入帧推进触发点** | `R0_1` 缺口 #1：只由 `LastConsumedInputFrame+1` 反推 `ConfirmedFrame`，未逐行读 `NetworkPredictionService_Input.inl` | 是否需复刻"本地输入环形缓冲 + PendingFrame++"精确时序 |

## 四、泛型预测核心接口草案（C# 7.3，只依赖 PMNetIdentity）

> 约束：`netstandard2.0` + C# 7.3；命名空间 `PMNet.Prediction`；**只用 `PMNet` 的 `PMFrameId`/`PMTimeStep`/`PMNetWriter`**；模型只实现 Clone/Simulate/Compare(+NetSerialize/Interpolate)。不含玩法类型/MonoBehaviour/protobuf 生成类型。帧语义固定"**输入帧 n → 输出帧 n+1**"，`ConfirmedFrame = LastConsumedInputFrame + 1`。

```csharp
namespace PMNet.Prediction
{
    public interface IPMPredictedModel
    {
        IPMPredictedModel Clone();                                    // 深拷贝快照，禁共享引用
        void Simulate(in PMTimeStep step, IPMPredictionInput input);  // 纯函数；禁墙钟/随机/外部状态
        bool ShouldReconcile(IPMPredictedModel authority);            // 同帧比较；离散量优先
        void NetSerialize(PMNetWriter w);                             // SyncState 线格式
        void Interpolate(IPMPredictedModel from, IPMPredictedModel to, float alpha); // SP：离散量 Snap
    }
    public interface IPMPredictionInput
    {
        IPMPredictionInput Clone();
        void NetSerialize(PMNetWriter w);
        float DeltaTimeMs { get; }   // 来自输入本身（范围见 U-2）
        bool IsPlaceholder { get; }  // dt==0 丢包占位：不得执行 Tick
    }
    public interface IPMPredictionInputProducer    // 宿主注入：Unity 采样 / DS 上行 / 重放
    { void ProduceInput(IPMPredictionInput into, float deltaMs); }

    public struct PMPredictionRecord               // 历史一条（形态见 U-7）
    {
        public PMFrameId InputFrame;               // Input (n)
        public PMFrameId ServerFrame;              // AuthorityServer (n+1)
        public IPMPredictionInput Input;           // 原始输入（重放用，禁重算 dt）
        public IPMPredictedModel State;            // Clone 产物
    }
    public interface IPMPredictionHistoryStore
    {
        int Capacity { get; }                                   // 未冻结（U-3）：需 ≥ RTT + 回滚深度
        void Record(in PMPredictionRecord record);
        bool TryGet(PMFrameId serverFrame, out PMPredictionRecord record);
        void DropOlderThan(PMFrameId serverFrame);              // 确认/溢出裁剪
        void Clear();                                           // 溢出安全网：必须伴随重同步标记
    }
    public struct PMAuthorityFrame
    {
        public PMFrameId ServerFrame;        // 权威 K（AuthorityServer）
        public PMFrameId ClientInputFrame;   // 对应 N（Input）；None = 本端即权威
        public IPMPredictedModel State;
    }
    public struct PMReconcileOutcome
    {
        public bool Reconciled;
        public int ResimulatedFrames;   // == PendingFrame - authorityFrame
        public bool HistoryMissing;     // true ⇒ 必须重同步，禁用当前值冒充
        public bool StaleDropped;       // 旧/重复权威帧被丢弃
    }

    public sealed class PMPredictionTimeline           // 每实例时间轴（纯 C# 核心）
    {
        public PMPredictionTimeline(IPMPredictedModel initial, IPMPredictionHistoryStore history);
        public PMFrameId PendingFrame { get; }         // 下一帧要 tick 的帧号
        public PMFrameId ConfirmedFrame { get; }       // None ⇒ 禁参与运算
        public IPMPredictedModel Predicted { get; }
        public bool IsFrozen { get; }                  // 断线冻结：帧号停止推进（超时由宿主墙钟）
        public void SetInputProducer(IPMPredictionInputProducer producer);
        public void Tick(in PMTimeStep step);                                  // 采样 → Simulate → Record(n→n+1)
        public void TickReplay(in PMTimeStep step, IPMPredictionInput replayed); // 重放：不采样
        public PMReconcileOutcome ApplyAuthority(in PMAuthorityFrame authority);
        // 旧/重复帧 → StaleDropped 且不改本地；ShouldReconcile==false → 不 Restore/不 Replay；
        // 否则 Restore(authority) → 按原始 Input 重放 [auth, PendingFrame)。
        public void Freeze();                                                  // 断线：停止推进（D-R0-29）
        public void Resync(IPMPredictedModel authorityState, PMFrameId authorityServerFrame);
        public event System.Action<PMFrameId> OnConfirmedFrameAdvanced;        // 不可逆副作用唯一广播点
    }

    public sealed class PMInterpolationTimeline         // SP：无帧号语义，按仿真时间插值 + 有上限外推
    {
        public PMInterpolationTimeline(IPMPredictedModel initial, long maxExtrapolateMs);
        public void OnAuthority(in PMAuthorityFrame authority, long totalSimTimeMs); // 刷新权威 + 外推归零
        public void Tick(float deltaSeconds, IPMPredictedModel outState);           // 超限后冻结在上一权威态
    }
}
```

**边界声明**：上为**接口草案**，`Client/Assets/Scripts/PMPrediction/` 当前不存在。U-1..U-9 裁决前，不得把预算/容量/阈值具体数值写进代码。

## 五、最小断言集（T43 骨架，可离线跑）

| ID | 场景 | 断言 |
|---|---|---|
| A1 | 输入 dt 16/17/16ms | 输出每步 dt 与输入一致；`Σdt == Σ输入` |
| A2 | 注入 dt=0 占位（丢帧） | 不执行 Tick、不推进状态；后续帧 dt 不变 |
| A3 | 帧对应 | DS `OutputFrame == InputFrame+1`；AP `ConfirmedFrame == LastConsumedInputFrame+1` |
| A4 | `ConfirmedFrame == None` / 跨命名空间 `PMFrameId` | 不广播任何"已确认"事件；差值/比较（含 `<`/`>`/`<=`/`>=`）抛异常，不静默 |
| B1 | 重复权威帧（同帧连发 3 次）/ 旧权威帧 | 重复只 restore/replay 一次、后两次 no-op；旧帧丢弃且 `ConfirmedFrame` **不回退**、不触发回滚 |
| B3 | 强制位置/速度/Mode/Modifier 差异 | 恢复**同帧完整状态**后重模拟并收敛（位置容差内、Mode 精确、Modifier 集合一致） |
| B4 | 重模拟次数 | `== PendingFrame - authorityFrame`；每步用**原始 input 与原始 dt** |
| B5 | `ShouldReconcile == false` | 不 Restore、不 Replay、本地状态零改动 |
| B6 | 阈值边界 | 4cm 不和解 / 6cm 和解；仅 Mode 名不同也和解；`bAllowExternalPositionChanges` 时 100cm 不和解 |
| C1 | 历史缺失（清空后收权威帧） | `HistoryMissing == true` + 触发重同步；**不得**用当前值冒充；不崩溃 |
| C2 | 历史溢出 | 明确重同步动作；帧号不回退；不静默假装校验成功 |
| C3 | 溢出后到达旧 ACK / 旧权威 | 找不到记录 → 忽略，不误配邻近帧 |
| D1 | 每帧强制回滚 300 帧 | 音效/对象生成/扣血次数 == 实际业务次数（**不回滚放大**） |
| D2 | 模式变化委托 | 仅在 `ConfirmedFrame` 越过该帧后触发**一次** |
| D3 | LayeredMove 集合变化触发和解 | 不出现任何 `OnStart/OnEnd`（空实现；出现即错误接线） |
| D4 | Modifier 集合变化（换实例） | `OnEnd(旧)` / `OnStart(新)` 各一次（实例级 `Matches`） |
| E1 | 不同 Update 频率（30/60/120Hz 宿主） | 一帧一条输入；`Σdt == Σ输入 dt`；dt 不被放大 |
| E2 | TickLOD 纯跳帧 N=4 | 每 4 帧 1 次模拟；跳帧 `StepMs` 与正常帧一致；100 帧 `Σdt ≈ 1/4`（丢时间非压缩） |
| F1 | SP 停发 5s / 恢复发包 | 停止后在 `maxExtrapolateMs` 冻结；恢复首帧直接对齐权威（无衰减回弹）、外推量归零 |
| F3 | 位置误差 0 / 旋转误差 90° | 旋转仍独立收敛（不因位置已对齐而跳过） |
| F4 | `Interpolate` 离散量（Mode、SimulationTimeScale 1→0） | 结果 Snap 取 `To`（不是 0.5） |
| G1 | 断线后注入未确认变更 | 墙钟超时触发兜底（**非**无限等待帧号 Grace） |
| G2 | 断线期 + 恢复 RPC | 帧号冻结（`PendingFrame` 不推进）；恢复 RPC 在 ForceValidate 下不返回 Reject |

## 六、已检查范围（文档如何决定入口）

- `D:/UGit/hyld-master/AGENTS.md` → 指向 `Client/Assets/AGENTS.md`、`Server/AGENTS.md`、`net-architecture-migration.md`；`D:/UE_Project/ProjectMecury/AI-Instruction.md` → 文档优先、不编译、不提交，战斗必读架构地图。
- `Client/Assets/AGENTS.md` / `Server/AGENTS.md` → 旧链 SavedMove/MoveAck 事实（仅作对照；D-R0 已撤销 16ms 累加器与旧预测）与旧参数量级（frameTime 16ms、PredictionHistoryWindowSize 40）。
- 主计划 §3.9.3/§3.9.4/§5 R4 行 → 冻结 M07 职责与"纯 C#、不依赖 BattleData/MainPack/Mover 玩法"边界并给 T43/T44；§6.1 T43/T44 → 验收场景（强制差异、重复/延迟权威帧、输入丢失、历史溢出；不同 Update 频率、断线恢复、历史不混帧）。
- 主计划交接记录末段（R3-B / 真实 DS 验收）→ 确认 R4 未开工，当前仅测试碰撞场景与 Probe/Echo；"历史容量/误差阈值在 R0/R1/R4 按实测收敛并记录来源"。
- `net-r0-contract.md` D-R0-19..29、§6、§8、§9 → 预测/帧冻结决策 + 待收敛参数清单 + T43/T44 关键断言；`R0_1_mover_np.md` 预测段（A/B/C/D/E/F + U1–U16 + 证据缺口）→ 唯一带 `文件:行号` 的 UE 侧预测证据源。
- `.agents/skills/mover-quick-start/SKILL.md` §5.2.1 与红线 → IndependentTick 结论摘要与红线（1/2/3/6/14/19/20）；`references/npp-independent-tick-frame.md` → 帧号体系 + "唯一精确锚 = ConfirmedFrame"；`references/network-prediction-checklist.md` → 合规 6 项 + 回滚流程 + 自定义 SyncState 扩展模板。
- `references/confirmed-frame-alignment.md`（npp 直接引用）→ ConfirmedFrame 取值、Snap key 用 `ClientInputFrame`、禁 ServerFrame 做跨端 key；`references/localpredict-disconnect-recovery.md`（npp 直接引用）→ 断线 = NPP 冻结、墙钟兜底、恢复 RPC 恒 NetReport。
- `Docs/战斗模块架构地图.md` §2.1/§3 → 路由表把"全局帧同步/ServerFrame/ConfirmedFrame"指向战斗预测专项；确认 Mover 归 `Pawn/Movement`，Combat 侧不在本次硬边界。
- `Client/Assets/Scripts/PMNet/PMNetIdentity.cs` → M01 实际签名与不变量；`Shared/PMBattleSim.cs`（+`BattleNumericConfig.cs` 存在性）→ 零依赖/标量/sign 契约；`net-r3-control-contract.md`（局部 grep）→ 确认现有"预算 32"是**发送数据报预算**，与 DS 输入仿真预算无关（防混用）。

**未读（有界排除）**：UE NetResilience 头文件、`NetworkPredictionService_Input.inl` 全文、项目 `Modifier/`/`FinalizeOverride/`/`Snap/`/`Rollback/`/`VisualSync/` 实现、`战斗预测与权威.md` 正文、GAS/Behavior/StateTree、hyld 业务代码与全部资产。

## 七、建议下一步（最小补充查询 / 运行时验证）

1. **补 U-1**：只读 UE `NetworkPredictionService_Ticking.inl:340-400` 与 `GetNetResilienceServerCatchupMaxSteps*`/`MaxRemoteClientStepsPerFrame`/`MinRemoteClientStepMS`/`MaxRemoteClientTotalMSPerFrame` 定义与 CVar 默认值（`R0_1` 缺口 #2，定点查询）。
2. **补 U-9**：只读 `NetworkPredictionService_Input.inl`（约 192 行）+ `Ticking.inl:73-220`，确认 `PendingFrame++` 与本地输入环形缓存的写入顺序（`R0_1` 缺口 #1）。
3. **裁决 U-2/U-3/U-4/U-6**（dt 范围、历史容量与溢出动作、ShouldReconcile 阈值、墙钟超时），并把取值与来源写入 `net-r0-contract.md` §9（该表要求"记录参数来源"）。
4. **裁决 U-5**：R4 首版选"引擎原生 SP 插值"还是"项目式自建表现层"；后者阈值需实测收敛（`R0_1` 缺口 #7）。
5. **落地顺序**：先 `IPMPredictedModel` + `PMPredictionTimeline` + 内存 `IPMPredictionHistoryStore`，把 A/B/C/D/E 做成离线门禁工具（沿用 `Tools/*Test` 风格，`dotnet run` 无 Unity）；SP(F) 与断线(G) 随后接宿主墙钟与 `PMInterpolationTimeline`。
6. **运行时验证（R4 有 Unity 后）**：`np.ForceReconcile`/`np.SkipReconcile` 对照 D1/D2；`bEnableSimProxyPredictiveInterpolation` 开/关对照 F1/F3；断网 3s 恢复对照 G1/G2。
