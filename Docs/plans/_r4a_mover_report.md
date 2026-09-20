# R4-A Mover 纯模型实施报告（P4A3）

日期：2026-09-20 ｜ 范围：M08 纯 C# 可回滚运动模型 + 确定性碰撞测试环境 + 语言/行为双门禁
契约来源：`Docs/plans/net-r4-prediction-contract.md`（「Mover 冻结字段与机制」/「验收 P4A3」）；
主计划 `net-architecture-migration.md` 文末 R4-A 行；前置调研 `_r4_mover_survey.md`。

## 1. 交付物（全部为允许写入清单内文件）

| 文件 | 行数 | 编码 | sha256(前16) |
|---|---|---|---|
| `Client/Assets/Scripts/PMMover/PMMoverState.cs` | 706 | UTF-8 BOM + CRLF | `47af42f5a4faefeb` |
| `Client/Assets/Scripts/PMMover/IPMMoverCollisionQuery.cs` | 107 | UTF-8 BOM + CRLF | `922c2507cc98bb7c` |
| `Client/Assets/Scripts/PMMover/PMMoverModel.cs` | 1266 | UTF-8 BOM + CRLF | `8c5fbfdb18efef84` |
| `Client/Assets/Scripts/PMMover/PMMoverTestWorld.cs` | 393 | UTF-8 BOM + CRLF | `e4b19155dcb730ee` |
| `Client/Assets/Scripts/PMMover.meta`（目录） | 8 | 无 BOM + LF | `5db8b9f561f033ec` |
| 4 × `*.cs.meta` | 11 | 无 BOM + LF | `6c076915…` / `793e2be1…` / `706b38e3…` / `cc26651a…` |
| `Tools/PMMoverCoreCheck/PMMoverCoreCheck.csproj` | 45 | 无 BOM + LF | `9ac78e46b9205b48` |
| `Tools/PMMoverTest/PMMoverTest.csproj` + `Program.cs` | 49 + 1497 | csproj LF / Program.cs BOM+CRLF | `50077bf64c3285a7` / `a62ab740be16e5d2` |

meta GUID：`85c012820f6247b9bf6a170134d1da8f`（目录）、`ecb6895a67de4197bcb3fd4c20fa32a5`（State）、
`43c2377ffe504f19b037b29f36ca8c76`（Collision）、`419d1a91cdbd48a2a6b88037b3d009df`（Model）、
`11ad36afbdf145ecb8d104ffaed824f9`（TestWorld）。复核：全 `Client/**/*.meta` 共 6796 个 GUID，**0 重复**。

不改动：`PMPrediction/**`（除只读 `PMPredictionContracts.cs`）、`PMNetIdentity/PMNetRole`（只读）、
预测组 timeline/buffer、R3 宿主、旧 SavedMove、R5、主计划与契约文档。未执行 git 提交。

## 2. 公开 API（实际落盘，非草案）

`namespace PMNet.Mover`；只依赖 `PMNet`（PMFrameId/PMTimeStep）与 `PMNet.Prediction` 契约，**零 UnityEngine 依赖**。

- `PMVector3`（struct，Y-up 米）：`+ - *`、`Dot/Distance/Normalized/Length/LengthSquared/IsFinite/Epsilon`。
- `PMMoverMode : byte { Walking=0, Falling=1, Flying=2, Inactive=3 }` + `PMMoverModes.IsDefined/Name`。
- `PMMoverLayerKind : byte { AdditiveVelocity=0, OverrideVelocity=1 }` + `PMMoverLayerKinds`。
- `PMMoverEffectKind : byte { SetVelocity=0, Teleport=1, SetMode=2, SetParameters=3 }` + `PMMoverEffectKinds`。
- `PMMoverLayer`（Sync 内活跃层）：`InstanceId/Kind/Priority/Velocity/DurationMs/ElapsedMs`、
  `HasFiniteDuration/IsExpired/SameDefinition`（**不含 ElapsedMs**）。
- `PMMoverLayerRequest`（Input 命令）→ `ToLayer()`。
- `PMMoverEffectRequest`：`Kind/Vector/Mode/MaxSpeed/Acceleration/Braking/GravityScale/JumpSpeed`
  + 静态工厂 `SetVelocity/Teleport/SetMode/SetParameters`。
- `PMMoverInput`：`MoveX/MoveZ/MoveY/YawDegrees/JumpPressed(边沿)` + `Effects[]/Layers[]/RemovedLayerIds[]`。
- `PMMoverSyncState`：`Position/Velocity/PreAdditiveVelocity/YawDegrees/Mode/Grounded/GroundNormal/Scale`
  + 5 个有效参数 + `ActiveLayers[]` + `CreateDefault()`。
- `PMMoverAuxState`：`Gravity/CollisionWorldVersion/ConfigVersion` + `CreateDefault()`。
- `PMMoverDefaults`：默认重力/参数/胶囊尺寸/探测量/skin/滑动迭代上限/dt 范围/全部 reconcile 阈值。
- `PMMoverHit{Blocking,Fraction,Normal}`、`PMMoverGround{Found,Distance,Normal}`、
  `IPMMoverCollisionQuery{WorldVersion,Sweep(position,delta,radius,halfHeight),QueryGround(position,radius,halfHeight,distance)}`。
- `PMMoverModel(IPMMoverCollisionQuery)` 实现 `IPMPredictionModel<PMMoverInput,PMMoverSyncState,PMMoverAuxState>`：
  `CloneInput/CloneSync/CloneAux/Simulate/ShouldReconcile/Interpolate` + `CollisionQuery`。
- `PMMoverTestWorld : IPMMoverCollisionQuery`（确定性替身，见 §7）。

## 3. 每帧管线（实现顺序即契约）

0. 校验 step/input/start/aux（**非法即抛**，不静默修正）→ 1. 深拷贝 start 到工作状态（start 之后只读）
→ 2. **阶段 A** 消费 InputCmd 的可信 Effect 队列（帧前）→ 3. 层命令：显式移除 → 新增/替换 → 帧首清扫到期 → 规范排序
→ 4. 层混合（Additive 求和 / Override 覆盖；存在 Override 即排除全部 Additive）
→ 5. 模式推进 + 扫掠滑动积分 → 6. **阶段 B** 消费**帧内**新入队的 Effect
→ 7. 活跃层 `ElapsedMs` 按**原 dt** 累加 → 8. 输出新 SyncState（新数组）+ Aux 拷贝 + **空事件集**。

## 4. 关键语义决策（已实现 + 已测；含对契约的解释）

| # | 决策 | 依据/影响 |
|---|---|---|
| D1 | 模式切换**无子步循环**：Walking→Falling 本帧立即按 Falling 积分整段 dt；Falling→Walking 落地从**下一帧**生效 | R4-A 未冻结子步尺寸策略。**未实现"剩余时间移交"**，见 §6 |
| D2 | `PreAdditiveVelocity` = **模式自驱干净速度**（不含层贡献），是每帧积分起点 | 契约「禁止上帧 additive 永久积累」「退出恢复干净 base」。宿主必须同时写 `Velocity` 与 `PreAdditiveVelocity` |
| D3 | 层混合：任意 `OverrideVelocity` 存在 → 本帧**全部** Additive 被排除；多个 Override 取 `(Priority,InstanceId)` 最大者 | 契约「Additive 门槛只排除 OverrideVelocity」+「按 priority/id 确定顺序」 |
| D4 | 层到期在**帧首**清扫（`Elapsed >= Duration`），进度在**帧末**累加 | 使"duration=dt 的层恰好生效 1 帧"可手算；`DurationMs==0` 为无时长，负数拒绝 |
| D5 | 同 `InstanceId` 再次请求 = **替换**（进度重置）；移除不存在的 id 为幂等空操作 | 唯一可解释的语义；避免"静默忽略" |
| D6 | `ShouldReconcile` 的层比较用**实例定义**（InstanceId/Kind/Priority/Velocity/DurationMs），**排除 ElapsedMs** | 进度是时间产物，两端差一帧属正常；把它算差异会导致每帧无谓回滚 |
| D7 | `ShouldReconcile` 顺序：Mode → Grounded → 精确参数(Scale/5 参数) → 层定义 → 位置 0.05m → 速度 0.01 → 干净基速 0.01 → 朝向 1° → 地面法线 1° → Aux(碰撞版本/配置版本/重力 1e-4) | 契约「mode 先、参数/层等完整」「AuxGravity/版本变化参与 reconcile」 |
| D8 | `SetVelocity` 同时写 `Velocity` 与 `PreAdditiveVelocity`；`SetMode` **不**改 Grounded/GroundNormal（由本帧模式自身刷新）；`Teleport` 保留速度 | 让"速度命令"语义自洽；避免凭猜重置地面位 |
| D9 | 朝向直接取 `Input.YawDegrees`（本批**无转向速率**），输出归一到 [0,360) 且 0 一律 +0.0f | 契约只冻结了 `YawDegrees` 字段；转向速率需实测（§7） |
| D10 | Falling **无空中操控**（只保留水平速度）；Flying 用三维方向 `normalize(MoveX,MoveY,MoveZ)`；Inactive 模式速度与层位移同时归零 | 契约「Flying 三维控制」「Inactive 零位移」；空中操控未在首批冻结 |
| D11 | Walking 的贴地判定用 `QueryGround(distance=0.05m)`（**不做地面吸附**，Y 不变）；Falling 落地复用同一查询（探测距离 = 本帧下落量 + 1e-4） | 契约「角色中心 Y=halfHeight 落地」；无斜坡/台阶故无需吸附 |
| D12 | 本批模型**不产生任何预测事件**（`Events` 为空数组） | Mode/Grounded 都是可回滚 SyncState，不是不可逆事件；事件去重属预测组 timeline（P4A1） |
| D13 | 未知 Effect 种类 / 未知模式值 / 未知层种类 / 0 实例 id / 负时长 / 非法 dt / NaN / Scale≤0 → **抛异常显式拒绝** | 契约「未知枚举明确拒绝不默认为 Walking」；fail-fast 强于静默默认 |

## 5. 验证（AI 可执行项，全部本次执行）

命令（全部在还原后的工作树上执行，`build exit=0` 才 `run`）：

```
dotnet build Tools/PMMoverCoreCheck/PMMoverCoreCheck.csproj -c Release   # exit 0，0 警告 0 错误
dotnet build Tools/PMMoverTest/PMMoverTest.csproj -c Release             # exit 0，0 警告 0 错误
dotnet Tools/PMMoverTest/bin/Release/net8.0/PMMoverTest.dll              # exit 0
```

`PMMoverTest`：**245 项检查，0 失败**。分节（节名 ↔ 覆盖点 ↔ 项数）：

| 节 | 覆盖 | 项数 |
|---|---|---|
| A 冻结面 | 模式/层/Effect 数值、默认值、PMVector3 数学、NaN/Inf 判定 | 32 |
| B clone 隔离 | Input 三列表/ Sync 层数组 / Aux 深拷贝、输出不别名 start、删改不回写 | 14 |
| C Walking | 静止、`v=a·dt`、位移 `v·dt`、Y 恒定、**8ms×2 vs 16ms×1 结果不同**、13 步钳制到 3.9、制动 3.58、斜向归一化 | 13 |
| D Falling | `vy=-9.81·dt`、连续叠加重力、`GravityScale` 相乘、Aux 重力反向、无空中操控 | 8 |
| E 落地 | 吸附到中心 Y=halfHeight、Grounded/Normal、**阶段 B 精确清零 vy**、水平位移仍走满 | 11 |
| F 失去地面 | 走出地板足迹 → 本帧转 Falling 并即刻施加重力 | 5 |
| G 墙 | 正撞停在膨胀面外 1mm、斜撞滑满切向、绕墙角后恢复自由、连续 10 帧不穿墙、起点重叠不隧穿 | 16 |
| H Inactive | 63 帧零位移、速度精确 0、层进度照常累加、第 64 帧到期清扫、SetMode 恢复 | 10 |
| I Layer | 单帧不累积（第 3 帧仍 =1）、有限时长到期恢复 base、priority/id 顺序、Override 排除 Additive、求和、移除、替换重置、无时长不过期 | 23 |
| J Effect | 阶段 A 先于移动（`5-1.6=3.4`）、多条 SetMode 取最后、Teleport 内/外、Flying 无重力、未知枚举与非法值 11 类显式拒绝、拒绝后 start 未被改写 | 28 |
| K ShouldReconcile | 完全相同/mode/grounded/6 个精确参数/层 7 类差异/位置速度朝向阈值边界/359°↔1°/法线/Aux 3 项 | 35 |
| L Interpolate | 位置速度线性、参数与离散取 To、**yaw 最短弧 350→10**、alpha 钳制、法线归一化、层数组无别名 | 18 |
| M 确定性/纯度 | 同输入 10 次**逐位**相同、start 与 input 均未被改写、事后改输入数组不影响输出、`IsResimulating` 不改变结果、空事件集、注入式碰撞查询 | 9 |
| N dt 驱动 | 16ms/50ms 各自一步、5 条输入 = 5 步、不存在墙钟合并 | 5 |
| O 替身几何 | 两个盒的精确 AABB、版本号、地面查询、**贴墙站立时墙不算地面**、贴地水平移动不被地板挡、起点重叠给出有限法线 | 18 |

## 6. 生产分支故障注入（先备份 → 注入 → 期望失败 → finally 还原 → 哈希核对）

备份：`PMMoverModel.cs` sha256 = `8c5fbfdb18efef8469a1b63c45980a410b45017ca02e010194f57d12f2f2b638`（55428 B）。
每次注入后**先确认注入版本能编译**（否则"失败"来自编译错误，不构成证据），再跑测试：

| 注入 | 真实生产分支改动 | build | run | 命中断言 | 判定 |
|---|---|---|---|---|---|
| INJ-1 | 删除落地阶段 B 的帧内 Effect 入队（Falling→Walking 不再清零 vy） | 0 | 1 | E5, E6 | **CAUGHT** |
| INJ-2 | 删除 `ShouldReconcile` 的"模式优先"比较块 | 0 | 1 | K2, K29 | **CAUGHT** |
| INJ-3 | `result.PreAdditiveVelocity = work.FinalVelocity`（干净基速被污染 → Additive 跨帧累积） | 0 | 1 | I2, I3, I4, I6, I7, I15（6 项） | **CAUGHT** |
| INJ-4 | 未知 SetMode 值静默回退到 Walking（校验与执行两处同时移除，模拟"从未校验"） | 0 | 1 | J13（"**没有**抛出：非法输入被静默接受"） | **CAUGHT** |

还原核对：还原后 sha256 = `8c5fbfdb18efef8469a1b63c45980a410b45017ca02e010194f57d12f2f2b638`，
与备份**逐字节一致**。还原后再次 build=0 / run=0（§5 即该次结果）。

## 7. 未实现项（**明说**，不注释假装有）

1. **子步循环 / 剩余时间移交**：一帧一次积分。模式切换不拆分子步，落地切 Walking 从下一帧生效（D1）。
2. **斜坡、台阶 step-up、移动平台 BaseCarry**：契约首批明确不做；碰撞面只有 `Sweep` + `QueryGround`。
3. **空中操控（Falling 水平输入）**：未实现（D10）。
4. **完整去穿透（depenetration）**：起点已重叠时只沿最小穿透轴推出 1mm skin/帧，不做整深度顶出
   （UE 对应 `TryMoveToResolvePenetration`）。原因：冻结的 `PMMoverHit` **不携带穿透深度**。
   已测性质：不崩溃、不挂死、**绝不隧穿到墙的另一侧**。若要完整去穿透，需在 R4-B 的 Unity 适配器上另加。
5. **Modifier**（数据类与行为类全部后置）、行为类实例级补偿、`RollbackModifiers` 对接。
6. **Effect 网络策略与拒绝裁决**：本批把 Effect/Layer 当**已鉴权的宿主可信命令**直接执行；
   "谁有权下发、专服玩家 + Auto 如何拒绝、如何撤销已完成效果"均属 R4-B，**未接线、未验证**。
7. **Unity 物理等价性**：`PMMoverTestWorld` 是确定性 AABB 替身。**不声称与 Unity PhysX 等价**，
   也不声称代表任何真实地图。Unity 适配器（`Physics.CapsuleCast` 等）PENDING_USER（本机无 Unity）。
8. **帧号/时间元数据**：模型不消费 `PMTimeStep.ServerFrame/ClientInputFrame/BaseSimTimeMs`，
   也不生成 Snapshot（帧标签归预测组 timeline）。`BaseSimTimeMs` 未被用于层计时（用 dt 累加）。

## 8. 未决项（需后续实测/裁决，当前取占位值）

| 项 | 当前值/口径 | 说明 |
|---|---|---|
| `Acceleration` / `Braking` | 20 / 20 m/s² | **占位**，无实测依据 |
| `JumpSpeed` | 4 m/s | **占位**；旧项目无竖直轴，无历史值 |
| `MaxSpeed` | 3.9 m/s | 有据：项目冻结移速（`BattleNumericConfig.MoveSpeed` 多数英雄 3.9） |
| 胶囊 `Radius`/`HalfHeight` | 0.4 / 1.0 m | HalfHeight 对齐 AGENTS「玩家 Y=1」；Radius 占位 |
| `GroundProbeMeters` | 0.05 m | 占位；与位置容差同量级 |
| 地面法线阈值 / 重力阈值 | 1° / 1e-4 | 契约只列了位置/速度/朝向三项，这两项为**本批自定** |
| 层时长 0 的语义 | `0` = 无时长（直到显式移除），负数拒绝 | UE 语义未定，本批显式冻结 |
| `Inactive` 与层的关系 | Inactive 期间层**不参与位移**（保证"零位移"字面成立），但进度照常推进 | 需产品确认：是否允许 Inactive 时被层推动 |
| 朝向 | 直接取输入，无转向速率 | 需实测/配置 |
| `Scale` 的改动通道 | 本批**无** SetScale Effect，只能由权威快照写入 | 契约的 SetParameters 未含 Scale |

## 9. 诚实口径汇总

- 单位：位置/距离=米，速度=米/秒，时间=毫秒，角度=度，重力/加速度=米/秒²；Y-up，世界坐标**不做二次镜像**。
- 测试几何边界：地板中心 `(0,-0.5,0)` 尺寸 `(40,1,40)`；墙中心 `(0,1,0)` 尺寸 `(1,2,8)`；
  版本号 `0x52334201` 与 `PMR3Runtime.CollisionDigest` **同值但不复用其常量**（避免 M08 依赖 PMNet.R3/Session），
  一致性由 R4-B 接线守护。**仅测试替身，不声称 PhysX**。
- 全部测试对手算 oracle 比对；**未使用实现自身当预期**；注入式负向验证 4/4 命中。
- 事件：本批模型 0 事件；不可逆事件去重与"只随 ConfirmedFrame 通知一次"由预测组 P4A1 覆盖。
- 真实网络角色（专服玩家/AP/SP）、真实 Unity 碰撞、T43/T44 整体仍 **PENDING**，本批不据此声明其通过。

## 10. P4A3 判定

| ID | 场景 | 状态 | 证据 |
|---|---|---|---|
| P4A3 | Mover 模式/碰撞/效果/层：纯状态可恢复、手算 oracle、无墙钟外部副作用 | **PASS（限定范围）** | PMMoverCoreCheck build0；PMMoverTest 245/0；故障注入 4/4 CAUGHT 且哈希还原一致 |
| T43/T44 | 真网络角色 / Unity 物理 | **PENDING** | 属 R4-B，本批未接线（§7.6/7.7） |
