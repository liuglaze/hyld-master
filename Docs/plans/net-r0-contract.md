# R0 — 详细契约与来源映射（Unity 网络框架移植）

> **定位**：本文件是 §5 中 **R0 阶段的交付物**，冻结 R1–R6 实施所依赖的契约：对象身份、RPC 调用 API、顺序与可靠性域、复制版本/ACK、每实例帧与状态、投射物裁决，以及逐项的**源实现 → Unity 适配差异**。
> **与主计划的关系**：`net-architecture-migration.md` 仍是**状态、决策与验收的唯一事实源**；本文件是该计划 §3.9/§5 的**细节展开**。两者冲突时以主计划的**状态与验收**为准，本文件的**数据布局与参数**在 R1 收敛后回写主计划。
> **证据附录**：`D:/hyld-refactor-survey/R0_1_mover_np.md`（Mover/NP，U1–U16）、`R0_2_ue_rpc.md`（RPC，U1–U22）、`R0_3_replication.md`（复制/生命周期，C-1–C-53）、`R0_4_projectile.md`（投射物，U-01–U-25）。本文件引用其编号，不复制正文。
> **事实等级**：第 1–2 节的**源侧语义**来自上述报告（均带 `文件:行号`，已抽查 3 项高风险结论）；**Unity 适配决策**（D-R0-*）是本次定稿的设计判断，尚未实现。第 9 节列出仍需 R1 实测收敛的参数。

---

## 0. 验收口径（对应 T37）

T37 通过条件：本文件覆盖 M01–M13 的契约、每项有源侧依据与 Unity 差异说明、无「全局帧号与输入帧号混用」、足以让 R1/R2 独立实现而不必重读 UE 源码。

| 检查 | 状态 |
|---|---|
| 13 个模块的职责/依赖/验收映射 | 主计划 §3.9.3 已列，本文件补**数据契约** |
| 每个契约项的源侧证据 | 见第 1 节映射表与 4 份附录报告 |
| Unity 适配差异显式记录 | 见 D-R0-02/03/05/06/08/09/10/11 |
| 有意裁剪清单 | 见 §2.3 |
| 待收敛参数 | 见 §9 |
| 高风险结论抽查 | 3/3 通过（见 §10） |

---

## 1. 来源映射总表

| Unity 模块 | 对应 UE / 项目机制 | 主要源证据（附录） | Unity 差异要点 |
|---|---|---|---|
| M01 Core | `FunctionCallspace` / `FNetRefHandle` / `FMoverTimeStep` 的**概念** | R0_2 §A.1；R0_3 C-21；R0_1 U1/U5 | 单会话命名空间，不做 60+4 位系统 ID |
| M02 CodeGen | UHT 生成 `exec` thunk + `_Validate`/`_ForceValidate` + `GetLifetimeReplicatedProps` | R0_2 U1/U3；R0_3 C-1 | 外部 Roslyn 生成 → C# 7.3 |
| M03 Transport | `UChannel` 可靠序号 / `FOutBunch` / NAK 重传 | R0_2 §C、U5–U8 | **不做 per-actor channel**，改固定顺序域 |
| M04 NetWorld | `UNetDriver` 对象登记 / `GetNetConnection` Owner 链 / `NetRefHandle` 分配 | R0_2 U4；R0_3 C-21/C-24 | **NetId 永不复用** |
| M05 RPC | `AActor::GetFunctionCallspace` 16 分支 / `ShouldCallRemoteFunction` | R0_2 §A.3、U2/U3 | 真值表照抄；**修**现有 PMNet 归属判断错误 |
| M06 Replication | 成员掩码 + per-connection baseline + `COND_*` + FastArray | R0_3 C-1–C-53 | 掩码学 Iris，基线学 legacy |
| M07 Prediction | `FMoverSyncState` / `ShouldReconcile` / `RestoreFrame` / `FinalizeFrame` | R0_1 U2–U9 | 帧模型按输入 dt，非固定 16ms |
| M08 Mover | MovementMode / LayeredMove / InstantEffect / Modifier | R0_1 U3/U4/U9/U15/U16 | **禁止 LayeredMove 副作用** |
| M09 Projectile | `APMNetProjectileBase` 双路径 + 假弹接管 + Layer 0–4 | R0_4 U-01–U-25 | ID 空间按连接隔离 + 服务器校验 |
| M10 BattleGameplay | GAS/Behavior 的**网络边界**（激活裁决、ID、Pending/Confirmed/Rejected） | R0_4 U-04/U-10/U-20；R0_2 §F.2 | 轻量激活账本替代 GAS |
| M11 UnityHost | `IPMMoverBridging` / 表现层 / SimProxy 平滑 | R0_1 U10/U11/U13 | 碰撞查询经接口注入 |
| M12 Lobby | Mos 逻辑服 + `ds_logic_mgr` + battleguard | 主计划 §3.1/§3.8 | C# 进程编排，非 Python |
| M13 Test | `net.*` CVar / NetSim / `PROJ-*` 日志前缀 | R0_4 U-25；R0_3 §L | 自建故障注入设施 |

---

## 2. 冻结决策

### 2.1 身份与生命周期

| ID | 决策 | 依据 | 与 UE 的差异 |
|---|---|---|---|
| **D-R0-01** | 对象身份 = `(NetId: uint32, bStatic: bool)`，**每连接一个 `SessionEpoch`** | R0_3 C-21（UE 用 60+4 位以支持 PIE/多世界，我们不需要） | 去掉 `ReplicationSystemId`；简化跨世界比较 |
| **D-R0-02** | **NetId 单会话内单调分配、永不复用**；池化对象换新 NetId | **本项目自定的设计选择**（不是从 UE 推出的结论）。附录只指出 UE 的池化复用需要一整套世代复位（R0_3 C-28/C-24），**并未确立「UE 复用 NetGUID」这个前提**——`NetRefHandleManager` 的分配/释放/复用点被 R0_3 §L 列为未读，§J 仅有一句 `ActorNetGUID` 复用的记录 | 换来的收益：身份不复用 ⇒ 免去跨世代串扰判定；代价是 uint32 耗尽（42 亿，单局不可能）。**前提是静态与动态共用同一编号空间**（见 `PMNetIdAllocator` 注释） |
| **D-R0-03** | **Create / Destroy 走可靠流**，与 Server RPC 同一顺序域 ⇒ 「对象创建先于引用它的 RPC」由顺序域保证 | R0_2 U12/U13（UE 用 `GetOrCreateChannel` 强制先复制 + `net.DelayUnmappedRPCs` 排队） | **不做延迟 RPC 队列**（D-R0-11）；用顺序域替代 |
| **D-R0-04** | 对象销毁不依赖"关闭包被 ack"（UE legacy 需 ack 后才清理） | R0_3 C-24 | Destroy 是可靠事件 + NetId 不复用 ⇒ 丢失后重进范围也能自愈 |

### 2.2 传输、顺序与可靠性

| ID | 决策 | 依据 | 与 UE 的差异 |
|---|---|---|---|
| **D-R0-05** | **三个固定顺序域**，共用同一 UDP 连接：`Reliable` / `Unreliable` / `Replication` | R0_2 U6/U9（UE 是 per-actor channel + 属性 bunch） | **有意不做 per-actor channel**。后果：对象间可靠 RPC 相对有序（比 UE 更强）、且存在**队头阻塞**（一条丢包挡住该连接全部可靠流）。可接受：可靠流只承载低频事件；状态走 Replication 流（丢包由后续更新收敛，不排队） |
| **D-R0-06** | `Reliable`：连接级序号、保序、去重、**NAK 驱动重传（不做定时重传）** | R0_2 U6/U7 | 照抄；`RELIABLE_BUFFER=512`、`MAX_CHSEQUENCE=1024` 同值 |
| **D-R0-07** | 可靠缓冲溢出 = **断开连接**（不静默丢弃） | R0_2 U5（`ReliableBufferOverflow` / `MaxReliableExceeded`） | 照抄；DS 侧断连等价玩家掉线 |
| **D-R0-08** | `Unreliable`：无序号、无去重、无重传；连接未就绪则直接丢 | R0_2 U5/U9 | 照抄 |
| **D-R0-09** | **禁止跨顺序域假设顺序**；不可靠流不承载任何"必须执行"语义 | R0_2 U9（负向断言） | 照抄，写入评审清单 |
| **D-R0-10** | 不同可靠性**禁止合并**进同一包；大载荷走分片重组 | R0_2 U8 | 分片/MTU 预算在 R1 定（§9） |
| **D-R0-11** | **不做** `net.DelayUnmappedRPCs` 式延迟队列 | R0_2 U12 | 由 D-R0-03 的顺序域替代；未解析引用 = 丢弃 + 告警 |

### 2.3 有意裁剪（统一记录，避免被误认为遗漏）

| 裁剪项 | UE 机制 | 不做的理由 | 替代 |
|---|---|---|---|
| per-actor channel | `UActorChannel` | 复杂度高；我们的状态已走独立 Replication 流 | 三个固定顺序域（D-R0-05） |
| Iris 63 位取模元素掩码 | `IrisFastArrayChangeMaskBits=63` | 取模会产生假脏，收益是位宽 | 元素掩码按元素数动态增长（R2 定） |
| Class Pool / Freeze 两级池 | `UActorClassPoolSubsystem` | 首版可直接销毁（DS 每局一个进程） | R6 按需接入，届时遵守 D-R0-02 |
| Delta Compression 4 槽基线 | `LastAcked/Pending/NewBaselineIndex` | 优化项，非语义必需 | 每连接单基线 |
| NetBlob / HugeObject 批导出 | `FHugeObjectSendQueue` | 我们无超大载荷（无 Behavior 上下文） | R1 定 MTU 上限并分片 |
| `COND_NetGroup` | 子对象分组 | 我们无子对象复制 | 不实现（R2 若需要再评估） |
| Iris 静态 Actor 解析重试 | `StaticActorResolveRetryCVars` | 绑定 WorldPartition 流送 | D-R0-04 自愈 |
| GAS/Behavior/StateTree 完整移植 | — | 只取网络边界 | M10 轻量激活账本 |
| `net.DelayUnmappedRPCs` 延迟队列 | `PendingLocalRPCs` 按序重放 | 我们无异步资源加载；顺序保证由 D-R0-03 的 Create 与 RPC 同域提供 | 未解析引用 = 丢弃 + 告警（D-R0-11） |
| 9 项未实现的生命周期条件 | UE `ELifetimeCondition` 共 18 项，未实现的 9 项为 `COND_InitialOnly / SimulatedOrPhysics / InitialOrOwner / ReplayOrOwner / ReplayOnly / SimulatedOnlyNoReplay / SimulatedOrPhysicsNoReplay / SkipReplay / NetGroup`（外加 `COND_Max` 这个哨兵值） | 首版只实现实际会用到的 8 项（D-R0-14）；其余需要时再补。`NetGroup` 在 UE 注释里明确「Not usable on properties」，我们不实现 | 未列入的条件在生成器声明阶段**直接报错**，不做静默降级 |
| LayeredMove 的回滚补偿 | 引擎 `RollbackLayeredMoves` 本身也是**空实现** | 补齐会与项目现有治理分叉（项目红线 14 就是禁止 LayeredMove 写副作用） | **禁止 LayeredMove 携带不可逆副作用**（D-R0-25）；补偿责任交给 Modifier |
| 服务器 / SimulatedProxy 的 reconcile | 引擎本身也是 `if (NetMode != NM_Client) return;` | 非“裁剪”而是照抄：只有客户端回滚 | 断言服务器与 SP 永不 reconcile（D-R0-22） |

### 2.4 复制模型

| ID | 决策 | 依据 | 说明 |
|---|---|---|---|
| **D-R0-12** | **变更掩码学 Iris（成员级 + 条件掩码），基线/ACK 学 legacy（每连接已确认版本）** | R0_3 C-1/C-3 与 C-8 | 混合方案，显式记录：Iris 的"无值基线 + 在途记录链"（C-9）实现成本高且优势在 delta compression（已裁剪） |
| **D-R0-13** | 脏粒度 = **对象脏 + 成员级掩码比较**（不做 Iris 的对象级退化） | R0_3 C-4/C-5（Iris 兼容层丢弃 RepIndex，是因为它有别的路径补全） | 标脏后**仍要比较**：Push 只保证"可能变了"，值未变则掩码为空、不发数据 |
| **D-R0-14** | 条件族**首版实现这 8 项**：`None(0) / OwnerOnly(2) / SkipOwner(3) / SimulatedOnly(4) / AutonomousOnly(5) / Custom(8) / Dynamic(14) / Never(15)`；**以 legacy 口径为准**。括号内是 UE `ELifetimeCondition` 的原始数值（`CoreNetTypes.h`），**我们的枚举用同一组数值**，以免两套编号在迁移期对不上 | R0_3 C-14（Iris 与 legacy **不等价**，必须二选一）；UE 数值取自 `Source/Runtime/CoreUObject/Public/UObject/CoreNetTypes.h` | 其余 9 项 + `COND_Max` 哨兵**明确不做**并列入 §2.3；表驱动测试覆盖范围为**首版这 8 项 × 角色组合**，不是 18 项 |
| **D-R0-15** | 条件由"不满足→满足"时必须**全掩码置脏**（新获得可见性的连接要拿到当前值） | R0_3 C-17 | 易漏项，单独列断言 |
| **D-R0-16** | 初始状态与创建在**同一包**内，接收侧无"已创建但值未到"的可见态 | R0_3 C-12/C-23 | 照抄，作为 Create 的原子性契约 |
| **D-R0-17** | FastArray：`ReplicationID`（稳定）+ `ReplicationKey`（版本）+ 数组 key；隐式删除 | R0_3 C-40/C-42/C-43 | 下标变化不触发重发；字段序以**源码**为准（项目文档旧口径有误，C-41） |
| **D-R0-18** | 复制队列与内存**必须有界**：对象数、每对象在途、分片重组缓冲、FastArray 单次上限 | R0_3 C-37/C-44 | 上限值 R1 定（§9） |

### 2.5 预测与帧模型

| ID | 决策 | 依据 | 说明 |
|---|---|---|---|
| **D-R0-19** | 帧单位 = **一条 InputCmd + 它自带的 DeltaTimeMS**；sim step 由输入 dt 驱动，**非固定 16ms** | R0_1 U1 | 撤回主计划旧 P3'-3b 的「沿用 16ms 累加器」；DS 的 tick 只是"排空输入队列"的时机 |
| **D-R0-20** | 帧号 4 个命名空间**严格隔离**：`InputFrame` / `AuthorityServerFrame` / `SimTimeMs` / `SessionTimeMs` | R0_1 U5 | 禁止混算；`None` 表示映射未建立，禁止参与差值 |
| **D-R0-21** | 三层状态：`InputCmd`（每帧重建、必须过网、可重放）/ `SyncState`（逐帧持久、参与比较）/ `AuxState`（很少变、透传） | R0_1 U2 | 字段归属判定口诀见 R0_1 §A.3 |
| **D-R0-22** | **只有客户端回滚**；判据 = 同帧 `ShouldReconcile(predicted, authority)`，**不是帧号不等** | R0_1 U6 | 照抄；服务器与 SP 永不 reconcile |
| **D-R0-23** | 重模拟区间 `[authorityFrame, pendingFrame)`，复用**原始 InputCmd 与原始 dt** | R0_1 U7 | 照抄 |
| **D-R0-24** | 副作用只允许落在 `FinalizeFrame`；不可逆业务事件等确认帧推进后广播 | R0_1 U8 | 照抄；Unity 侧对应"提交帧"，不进重模拟 |
| **D-R0-25** | **禁止 LayeredMove 携带不可逆副作用**；Modifier 必须实现完整实例级补偿 | R0_1 U9（UE `RollbackLayeredMoves` 是空实现，两者不对称） | 对齐项目红线 14；**不**去补 LayeredMove 补偿（会与项目语义分叉） |
| **D-R0-26** | Modifier 身份可区分实例：业务键 + 非零实例 ID；数据类 Modifier 双重 opt-out（不分配 ID + 显式声明不参与追踪） | R0_1 U16 | 照抄；数据类漏做后者会在未来重构时静默升级进回滚 |
| **D-R0-27** | 外部接管模式（过场动画等）用**非序列化的瞬态标志**短路 reconcile | R0_1 U13 | 照抄 |
| **D-R0-28** | 降频只允许**纯跳帧**（丢时间），禁止放大 dt 的大跨步 | R0_1 U14 | 照抄（项目实测过 stretch 会破坏寻路/刹车区） |
| **D-R0-29** | 断线兜底必须**墙钟 + 非预测框架驱动** | R0_1 U12 | 断线时帧号冻结，任何基于帧号的超时都失效 |

### 2.6 投射物

| ID | 决策 | 依据 | 说明 |
|---|---|---|---|
| **D-R0-30** | 双路径：`ClientPredicted`（假弹 + Spawn RPC）/ `ServerDirect`（仅权威弹） | R0_4 U-01 | 判定条件照抄（`IsLocallyControlled` / `HasAuthority`） |
| **D-R0-31** | `ProjectileID` 按**连接隔离**（每玩家一份 ID 空间），**服务器校验同连接内重号** | R0_4 U-02（UE 客户端自分配且**未校验**重号） | 显式差异：UE 留了这个缺口，我们补校验 + 告警 |
| **D-R0-32** | 假弹→权威镜像：**一次性消费**、幂等标志、迟到分支（假弹已结束则静默停止并隐藏）、不二次广播停止 | R0_4 U-07 | 照抄全部对齐字段（位置/朝向/运动时基/已命中目标/停止态/停止后时长） |
| **D-R0-33** | 假弹存活期间**镜像位置不写入** | R0_4 U-08 | 照抄 |
| **D-R0-34** | Verify 五层：L0 激活裁决 → L1 资源/次数 → L2 轨迹预算 → L3 逐目标历史位置 → L4 结算 | R0_4 U-10–U-20 | 阈值与公式见 R0_4 §C/§I |
| **D-R0-35** | 三类等待（挂起 Spawn / 待生成 Verify / 命中结论）**各有 TTL + 清理**；容量上限**逐类不同**：待生成 Verify 有「单 ID 上限 + 总量上限」，命中结论「容量满丢最旧」，**挂起 Spawn 没有独立数量上限**（只有 TTL） | R0_4 U-04/U-20/U-21 与 §D.1/§D.4（§D.1 明确「追加处无独立数量 cap，不能把 TTL 当容量上限」） | **Pending 期间绝不扣血**（U-20 断言）。不要给挂起 Spawn 加未经设计的 cap——那会改变既有语义 |
| **D-R0-36** | 命中位置还原顺序：帧锚 → 时间回溯 → 当前位置；帧锚新鲜度用**单调仿真时间**而非帧号差 | R0_4 U-13/U-14 | 易错项，单独断言 |
| **D-R0-37** | `ImpactPoint` **只消毒不拒绝**；消毒 → 重放 Filter → 几何判定，顺序不可换 | R0_4 U-16/U-17 | 照抄 |
| **D-R0-38** | 停止与销毁分离：墓碑期允许迟到 Verify；回池后走告警丢弃 | R0_4 U-22 | 照抄 |
| **D-R0-39** | 回池清理清单必须含**运行时白名单** `AllowedHitTargets` | R0_4 U-23（UE 当前遗漏） | 显式差异：UE 有缺陷，我们补上 |
| **D-R0-40** | 客户端本地可做：运动积分、候选收集、受击表现、停止表现、显隐、追赶；**不可**做任何伤害/属性/死亡结算 | R0_4 U-24 | 照抄，写入 M10 边界 |

### 2.7 RPC

| ID | 决策 | 依据 | 说明 |
|---|---|---|---|
| **D-R0-41** | 发送侧必须实现 `GetFunctionCallspace` 的**16 分支真值表，且分支顺序本身是契约** | R0_2 U2 / §A.3 | 顺序错会得到不同结果（如 pending-kill 必须在 NetRequest 之前） |
| **D-R0-42** | 接收侧归属校验 = `(!IsServer \|\| isObjectOwner) && !ignoreRpcs` | R0_2 U3 / §B.1 | **必须修正现有 `PMRpcDispatch.ShouldCallRemoteFunction`**（当前服务端分支无条件放行，与 UE 相反） |
| **D-R0-43** | 方向非法 = 确定行为，非未定义：服务端调 Server RPC → 本地执行不回环；客户端调 Client/Multicast → 本地执行不外发；服务端收到 Client/Multicast → 拒绝 | R0_2 U3 / §H | 照抄，全部给断言 |
| **D-R0-44** | Multicast = 「相关连接集合」，逐连接判相关性；**不是空间广播** | R0_2 U10 | 照抄 |
| **D-R0-45** | 校验分两层：编译期强制 + 运行期三态（`Accept` / `Report` / `Reject`）；`Reject` 只跳过实现，**不断连** | R0_2 §F.1/§F.2 | 与原生 `_Validate`（失败即断连）**互斥**，必须二选一 |
| **D-R0-46** | 参数布局不匹配 = **协议不兼容**（服务端断连 / 客户端标记不兼容），不是"忽略该 RPC" | R0_2 U11 | 照抄；生成器需产出协议摘要供 handshake 比对 |

### 2.8 编码与生成

| ID | 决策 | 依据 | 说明 |
|---|---|---|---|
| **D-R0-47** | protobuf 仅作**底层字节编码**；不保留旧 `MainPack` schema | 主计划 §3.9.5 | 生成器产出独立于 protobuf 的 RPC/复制描述符 |
| **D-R0-48** | 生成产物必须 C# 7.3 + .NET Standard 2.0；业务用 `_Implementation` + 标记字段 | 主计划 A9；R0_2 U1 | Attribute 不拦截调用，靠生成入口 |
| **D-R0-49** | 生成器必须输出**稳定 ID**（类型/RPC/属性）与协议摘要；**禁止按声明顺序临时编号** | 主计划 §3.9.5 | 否则重排源文件即破坏线协议 |
| **D-R0-50** | 声明错误（缺 ForceValidate 的 Server RPC、重复 ID、非法签名）**编译期失败** | R0_2 U1 | 对齐 UHT 的强制要求 |

---

## 3. M01 核心类型契约（拟定 API）

> C# 7.3 约束；纯 C#、零 Unity 依赖；命名空间 `PMNet.Core`。以下为**签名契约**，实现属 R1/R2。

### 3.1 身份与句柄

```csharp
public readonly struct PMNetId : IEquatable<PMNetId>   // D-R0-01/02
{
    public readonly uint Value;      // 单会话单调；0 = 无效；永不复用
    public readonly bool IsStatic;
    public bool IsValid { get; }
}

public sealed class PMSession          // 每连接一个
{
    public readonly uint Epoch;        // 连接建立时分配，用于拒收旧世代包
    public readonly bool IsServerSide;
}
```

**不变量**
- `PMNetId.Value == 0` ⇒ 无效；任何 API 收到无效 ID 必须拒绝而非静默处理。
- 同一 `Epoch` 内 `Value` 单调递增且不复用；`Epoch` 变化 ⇒ 所有旧 ID 立即失效。

### 3.2 帧与时间步

```csharp
public readonly struct PMFrameId       // 四个命名空间互不相等（D-R0-20）
{
    public readonly PMFrameDomain Domain;   // Input | AuthorityServer | SimTime | Session
    public readonly long Value;
}

public readonly struct PMNone { }      // 显式"未建立"，禁止参与运算

public struct PMTimeStep               // 对齐 FMoverTimeStep 概念
{
    public long BaseSimTimeMs;
    public float StepMs;               // 来自输入 dt（D-R0-19）
    public PMFrameId ServerFrame;
    public PMFrameId ClientInputFrame;  // None ⇒ 本地权威
    public bool IsResimulating;
}
```

**不变量**
- 不同 `Domain` 的 `PMFrameId` 相减是**编程错误**（R1 用 Debug 断言或类型包装拦截）。
- `BaseSimTimeMs == None` / `ClientInputFrame == None` 时禁止差值（R0_1 U5 断言）。

### 3.3 三层状态

```csharp
public struct PMInputCmd                     // 每帧重建，过网，可重放
{
    public PMFrameId InputFrame;
    public float DeltaTimeMs;
    public IPMInputPayload Payload;          // 类型化输入
    public bool IsPlaceholder;               // dt==0 的丢包占位，不执行 Tick
}

public interface IPMSyncState                // 逐帧持久，参与 ShouldReconcile
{
    bool ShouldReconcile(IPMSyncState authority);
    void Interpolate(IPMSyncState from, IPMSyncState to, float alpha);
    void NetSerialize(PMNetWriter w);
}

public interface IPMAuxState                 // 很少变，透传
{
    void NetSerialize(PMNetWriter w);
}
```

### 3.4 RPC 描述与分发

```csharp
public enum PMRpcDirection : byte { Server, Client, Multicast }
public enum PMRpcValidator : byte { None, Validate /*失败断连*/, ForceValidate /*三态*/ }

public struct PMRpcDescriptor
{
    public ushort RpcId;                 // 生成器分配的稳定 ID（D-R0-49）
    public PMRpcDirection Direction;
    public bool IsReliable;
    public PMRpcValidator Validator;
    public ushort ParamLayoutId;         // 参数布局哈希，用于协议比对（D-R0-46）
}

public enum PMCallspace : byte { Absorbed = 0, Remote = 1, Local = 2 }   // 对齐 FunctionCallspace

public static class PMCallspaceEvaluator      // D-R0-41：16 分支，顺序即契约
{
    public static PMCallspace Evaluate(PMCallspaceContext ctx, PMRpcDescriptor rpc);
}

public static class PMRpcReception            // D-R0-42
{
    public static bool ShouldCallRemoteFunction(bool receiverIsServer, bool isObjectOwner, bool ignoreRpcs)
        => (!receiverIsServer || isObjectOwner) && !ignoreRpcs;   // ★ 修现有 PMNet 的反向实现
}
```

### 3.5 复制描述符

```csharp
public struct PMPropertyDescriptor            // D-R0-12/13
{
    public ushort PropertyId;
    public PMCondition Condition;
    public byte[] ChangeMaskBits;             // 该属性占用的位区间
    public ushort QuantizerId;                // 0 = 不量化
    public ushort OnRepMethodId;              // 0 = 无 RepNotify
}

public sealed class PMReplicationDescriptor
{
    public PMPropertyDescriptor[] Properties;
    public int ChangeMaskBitCount;
    public bool HasConditionalMask;           // 含条件属性时 D-R0-14
    public uint ProtocolHash;                 // 与 PMRpcDescriptor.ParamLayoutId 同源
}
```

### 3.6 预测接口

```csharp
public interface IPMPredictedModel
{
    void ProduceInput(ref PMInputCmd cmd, float deltaMs);              // 采集
    void SimulationTick(in PMTimeStep step, ref IPMSyncState sync, ref IPMAuxState aux);
    void FinalizeFrame(in PMTimeStep step, IPMSyncState sync);         // ★ 唯一副作用落点（D-R0-24）
}

public interface IPMPredictionHistory           // D-R0-23
{
    bool TryGetFrame(PMFrameId inputFrame, out PMInputCmd cmd, out IPMSyncState sync, out IPMAuxState aux);
    void Record(PMFrameId inputFrame, in PMInputCmd cmd, IPMSyncState sync, IPMAuxState aux);
    int Capacity { get; }                       // 需覆盖 RTT + 回滚深度（§9）
}
```

### 3.7 投射物契约类型

```csharp
public enum PMProjectileOrigin : byte { ClientPredicted, ServerDirect }
public enum PMActivationResult : byte { Confirmed, Pending, Rejected }   // D-R0-34

public sealed class PMProjectileRegistry      // D-R0-31：按连接隔离
{
    // 每个连接一份；同连接内 ID 冲突 → 告警 + 拒绝（UE 缺口，我们补）
    public bool TryRegister(uint projectileId, PMProjectileOrigin origin, out string conflict);
}

// 三类等待（D-R0-35）：各有 TTL；容量逐类不同（挂起 Spawn 无数量上限），Pending 期间不结算
public sealed class PMPendingSpawnQueue { }        // U-04
public sealed class PMPendingVerifyStash { }       // U-21
public sealed class PMPendingHitEntries { }        // U-20
```

---

## 4. 顺序域与可靠性契约（M03）

| 项 | 契约 |
|---|---|
| 流 | `Reliable` / `Unreliable` / `Replication`，**共用同一 UDP 连接**（D-R0-05） |
| Replication 流的包级机制 | 与 `Reliable` **不同**：状态不做可靠重传（重传旧值无意义）。每次更新携带**发送版本/基版本**；接收侧按版本判定陈旧并丢弃；丢包由后续更新自然收敛（见 §5 的每连接基线）。**但要有一层包级 ack** 来推进「每连接已确认版本」——否则 §5 的 baseline 无法前进。ack 只确认版本，不触发重传 |
| 帧头 | `{ SessionEpoch, StreamId, ... }`；`Epoch` 不符 ⇒ 丢弃（D-R0-01） |
| Reliable 序号 | 每连接独立 `OutReliable`/`InReliable`；`MAX_CHSEQUENCE=1024` 环绕；`!= inReliable+1` ⇒ 入等待队列按序释放 |
| Reliable 重传 | NAK 驱动；不发定时重传；重传不做发送侧去重，靠接收侧按序号丢弃重复 |
| Reliable 溢出 | 发送缓冲 / 接收等待队列超上限 ⇒ **断连**（D-R0-07） |
| Unreliable | 无序号、无去重、无重传；未就绪直接丢（D-R0-08） |
| 合并 | 仅同流且尾部对齐可合并；**不同流禁止合并**（D-R0-10） |
| 分片 | 超过 MTU 的载荷切片并重组；重组缓冲有界（D-R0-18） |
| 队头阻塞 | **已知代价**：全局可靠流的丢包会阻塞该连接全部可靠消息。缓解：可靠流只承载低频事件；**禁止**把高频状态放进可靠流（D-R0-05/D-R0-09） |

---

## 5. 复制与生命周期契约（M06 / M04）

| 项 | 契约 | 源 |
|---|---|---|
| 对象创建 | Create 与初始状态**同包**；接收侧无"已创建空值"可见态 | D-R0-16 |
| 对象销毁 | 走可靠流；不依赖 ack；NetId 不复用 ⇒ 迟到包自然失效 | D-R0-04 |
| 每连接基线 | 每连接记录"已确认版本"；ACK 只能前进，**旧 ACK 不得清除新脏位** | R0_3 C-8 |
| 脏 | Push 标脏 = 对象进"待比较"集合；比较后得成员掩码；值未变 ⇒ 掩码空、不发 | D-R0-13 |
| 条件 | `COND_*` 按 legacy 口径；**表驱动测试**；条件跃迁需全掩码置脏 | D-R0-14/15 |
| 初始状态 | 首次进入范围 / 基线缺失 ⇒ 全量 | D-R0-12 |
| 相关性 | 距离裁剪 + AlwaysRelevant + 附着继承；**带迟滞**避免临界抖动 | R0_3 C-32/C-33 |
| 休眠 | `Dorm_Initial` 一次性 → 自动转 `Dorm_Always`；`ForceNetUpdate` 隐含唤醒 | R0_3 C-29/C-30 |
| 频率 | 主值与 Min 成对；脏对象**绕过频率**立即发 | R0_3 C-35/C-38 |
| 调度预算 | 每帧调度对象数上限；超限顺延且**不丢** | R0_3 C-37 |
| FastArray | `ReplicationID` 稳定；下标变化不触发；隐式删除靠 key 区间；删除移位必须正确 | D-R0-17 / C-45 |
| 上限 | 对象数 / 在途 / 重组缓冲 / FastArray 单次变更 全部有界 | D-R0-18 |
| 分工 | 状态走复制（新进入者能补齐）；一次性事件走 RPC | R0_3 C-48 |

---

## 6. 预测与 Mover 契约（M07 / M08）

| 项 | 契约 |
|---|---|
| 模拟单位 | 一条 InputCmd + 其 dt；服务器有步数/累计 ms 上限；**不合并、不放大 dt** |
| 子步循环 | Transition 与 Mode.SimulationTick 互斥；剩余时间让给下一模式；连续满额退还需上限保护；InstantEffect 消费两次 |
| 确定性 | `GenerateMove(startState, inputCmd, timeStep)` 纯函数；禁止墙钟/随机 |
| reconcile | 逐层比较（模式差异优先）；位置容差 5cm；朝向独立阈值；基座用"捕获时是否真用平台"的持久位；外部接管短路 |
| 重模拟 | `[authorityFrame, pendingFrame)`；复用原始输入与 dt；溢出有安全网 |
| 副作用 | 只在 `FinalizeFrame`；不可逆事件等确认帧推进后广播 |
| LayeredMove | **禁止不可逆副作用**（D-R0-25） |
| Modifier | 实例级补偿；数据类双重 opt-out（D-R0-26） |
| AP/SP | AP 预测+回滚；SP 只插值（Unity 可加受控外推，须有上限并冻结）；位置与旋转**独立**收敛 |
| 降频 | 只允许纯跳帧（D-R0-28） |
| 断线 | 墙钟 + 非预测框架驱动兜底（D-R0-29） |
| 身份路由 | 每个派发点必须明确身份；专服玩家 + `Auto` ⇒ 必须显式初始化；SimProxy 静默拒绝；AP 缓存上行 |

---

## 7. 投射物契约（M09）

| 项 | 契约 |
|---|---|
| 分流 | `IsLocallyControlled` ⇒ 预测路径；`!predicted && HasAuthority` ⇒ 直创；否则不生成 |
| ID | 按连接隔离；服务端校验重号（**修正 UE 缺口**，D-R0-31） |
| 生成顺序 | 写来源 ID → 绑裁决 → 钳制预测时间 → **注册** → 追赶 → 按需隐藏（注册必须先于追赶） |
| 枪口约束 | 出生点与权威位置距离 ≤ 上限（默认 1000cm），超出拒绝；挂起恢复不重检 |
| 假弹 | 本地模拟 + 本地碰撞 + 受击表现；**永不产生伤害**；RPC 由 Controller 侧代发 |
| 接管 | 一次性消费 + 幂等标志；用**假弹状态**覆盖过时复制值；对齐 6 项 + 停止态 4 项；不二次广播停止 |
| 迟到 | 假弹已结束 ⇒ 静默停止 + 隐藏 + 置幂等标志 |
| 位置闸门 | 假弹存活期间镜像位置不写入 |
| 直创镜像 | 关碰撞、不收集候选、停止由服务器信号驱动 |
| L0 | 激活裁决：`Rejected` 丢弃整包；`Pending` 先校验后结算；来源 ID 为 0 恒放行 |
| L1 | 生命周期 / 单弹次数上限 / 单批目标上限；任一失败整包丢弃 |
| L2 | 三分支预算（飞行 / 停止回放 / 停止正常）；`skipTrajectoryValidation` **只跳飞行分支** |
| L3 | 帧锚（新鲜度用**单调仿真时间**）→ 时间回溯 → 当前位置；再叠加视觉偏移；`ImpactPoint` 只消毒不拒绝；重放 Filter 须补齐骨骼上下文；线段-线段几何判定 |
| L4 | 结算；`Pending` 时**只暂存不结算** |
| 三类等待 | 挂起 Spawn（**仅 TTL，无数量上限**）/ 待生成 Verify（保序 + 单 ID 与总量上限）/ 命中结论（TTL + 容量满丢最旧） |
| 停止/墓碑 | 停止 ≠ 销毁；墓碑期允许迟到 Verify；墓碑长度取公式与最小值的 max |
| 回池 | 清空清单 + **运行时白名单**（D-R0-39） |
| 客户端边界 | 只做表现，不做任何结算（D-R0-40） |
| 诊断 | 保留分级日志前缀（R0_4 U-25） |

---

## 8. 验收映射

R0 自身：**T37**（本文件即交付物）。

R0 冻结的契约将被以下测试验证（均为 PENDING，不得用本文档替代）：

| 契约簇 | 验证测试 | 关键断言（取自附录报告，已去重） |
|---|---|---|
| 顺序域与可靠性 | T38 | 同流保序去重；NAK 重传不重复执行；可靠溢出断连；不可靠不重传；不同流不合并；分片重组有界 |
| RPC 身份 | T39 | callspace 16 分支真值表；归属校验拒绝非 Owner；方向非法为确定行为；旧世代包无副作用 |
| 生成与漂移 | T40 | 新增标记类即可跨端工作；非法声明编译失败；二次生成零 diff；运行不扫反射 |
| 复制收敛 | T41 | 不同基线最终一致；旧 ACK 不清新脏；条件跃迁补发；Create 原子性；无幽灵对象 |
| 帧模型 | T43 | 每步 dt 与输入一致；丢帧占位不推进；重模拟区间 = pending - authority；`FinalizeFrame` 副作用不放大 |
| 主线程与表现 | T44 | SP 插值/外推有上限并冻结；位置与旋转独立收敛；确认元数据持续刷新 |
| 投射物 | T45 | 无双弹/幽灵弹/重复伤害；接管对齐全字段；Pending 不扣血；拒绝能撤销；墓碑期迟到合法 |

---

## 9. 待 R1/R2 实测收敛的参数（本文件不冻结）

| 参数 | 现状依据 | 收敛时机 |
|---|---|---|
| MTU / 单包上限 / 分片阈值 | 旧实现 1024B 收包上限（主计划 B10） | R1 |
| 可靠缓冲与等待队列上限 | UE `RELIABLE_BUFFER=512` | R1（照抄为初值） |
| 预测历史容量 | UE/项目 128 帧；旧 hyld 40 | R1/R4（覆盖 RTT + 回滚深度） |
| 对象在途记录上限 / 每帧调度上限 | UE 65535 / 项目 150 | R1/R2 |
| 复制频率分档取值 | 项目 30/60Hz、怪物 5/10/30Hz | R2 |
| FastArray 单次变更上限 | UE 2048 | R2 |
| 相关性迟滞阈值 | UE `ObjectScopeHysteresisUpdater` 未读实现 | R2 |
| 投射物各层阈值 | R0_4 §I 已列全部现状值 | R5（照抄为初值） |
| 三类等待 TTL/容量 | TTL 均为 2.0s；待生成 Verify 单 ID 5 条 / 总量 128；命中结论容量 128；**挂起 Spawn 无数量上限** | R5（照抄为初值） |

---

## 10. 高风险抽查记录（主 Agent 复核）

| # | 抽查对象 | 报告声称 | 实测 | 结论 |
|---|---|---|---|---|
| 1 | `UNetDriver::ShouldCallRemoteFunction` | `(!IsServer \|\| bNetOwner) && !bIgnoreRPCs` @`NetDriver.cpp:8041-8044` | 逐字一致 | ✅ 并**证实现有 `PMRpcDispatch` 服务端分支写反**（D-R0-42） |
| 2 | Iris 兼容层 Push 是否退化 | `MarkPropertyOwnerDirty` 丢弃 `RepIndex`，只标对象脏 @`LegacyPushModel.cpp:59-88` | 实现确实忽略 `RepIndex`，仅调 `MarkNetObjectStateDirty` | ✅ 支撑 D-R0-13（我们**不做**该退化） |
| 3 | 投射物 ID 分配 | 进程级 `static` 计数器，`MAX_int32→1`，跳过 `-1` | `PMBehaviorUtilLibrary.cpp:823-827` 逐字一致（报告写 809-815，**行号漂移 ~14 行**，语义一致） | ✅ 支撑 D-R0-31 |

**行号漂移提示**：附录报告的 `文件:行号` 存在 ≤20 行漂移（引擎带 PM MOD、项目持续改动）。引用时以**函数/符号名**为准，行号仅作定位辅助。

---

## 11. 本文件未覆盖（留给后续阶段）

- M12 Lobby ↔ DS 的进程编排契约（拉起/凭据/快照/结算/回收）→ **R3 开工前冻结**。
- M10 玩法层的属性表与 RPC 清单 → **R6**（按业务逐个迁移）。
- 生成器的模板与 ID 分配算法细节 → **R2**。
- 网络模拟/故障注入设施的具体接口 → **R1**（M13）。
