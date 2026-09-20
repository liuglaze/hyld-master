# R4-A 预测与Mover核心冻结契约

状态和验收只记录主计划。本文是实现接口展开，依据R0 D-R0-19..29、_r4_prediction_survey、_r4_mover_survey及源证据R0_1。用户已授权连续实施，不要求重复确认。只写hyld，不改/编译UE。

## 首批范围和保留项

A：纯泛型预测时间轴(AP)、DS输入接纳预算、SP时间插值、完整Input/Sync/Aux快照与确认事件。
B：纯Mover Walking/Falling/Flying/Inactive、Y-up运动、参数与状态完整clone、确定碰撞查询接口及简单固定盒体测试环境；InstantEffect/LayeredMove最小纯状态机制。
R4-B随后接R3真实网络与Unity碰撞；行为类Modifier实例级补偿、所有模式/叠加混合族/网络激活裁决仍需后续，不因首批通过标T43/T44整体PASS。
不改旧SavedMove/业务权威入口，不扩R5。不声称测试几何等价Unity PhysX。禁止用新的PMFrameTypes复制已有PMFrameId/PMTimeStep。

## 时间、参数与安全决策

- 一个Input帧n在边界n起模拟，产出边界n+1；PendingFrame是下一条输入编号，ConfirmedFrame是最近接受的输出边界。两者在AP侧均用PMFrameDomain.Input；DS自身AuthorityServer帧单列元数据，不与AP帧做算术。
- 每实例绑定SessionEpoch(uint非0)+InstanceId(uint非0)。权威快照必须匹配两者；旧/重复确认边界丢弃，未来边界拒绝。边界0为初值。Snapshot含OutputFrame(Input域)、ServerFrame(AuthorityServer域)、TotalSimTimeMs(double)、Sync、Aux。
- 时间步DeltaTimeMs首批为整毫秒int，范围1..50；0仅DS缺帧占位（不调用模型/不累计时间，显式消耗该输入槽并推进输出边界），负数/NaN由接口类型或检查拒绝。此整毫秒限制是本项目首批适配决策，不是UE必然要求；不得合并或放大dt。PMTimeStep.StepMs赋该int。
- 历史默认128条输入+129边界状态；满且未确认的历史不能悄悄覆盖，冻结进入NeedsResync，宿主另发可信完整快照恢复。Epoch/实例不变时Resync不得回退确认边界，重置未确认输入必须通知宿主。Disconnected只Freeze，墙钟2s触发一次ResyncRequested通知(非每帧重复)；状态恢复只接受显式可信Resync。
- DS每Pump最多8步/100ms输入时间，token bucket信用按服务器墙钟流逝1:1补充、上限200ms，初始信用50ms；这些是项目首批保守可配置默认，非宣称UE默认。未来窗口128，重复输入不重复模拟，缺帧先等待，有界超时/缺历史请求Resync，不用最大帧直接跨过。正常输入保留原dt，预算不足延后不截断。时间倒退不增加信用。
- 位置单位Unity米，位置阈值0.05m(对应源5cm)、速度阈值0.01m/s、朝向阈值1度；Mode/有效参数/活跃运动实例的变化精确比较。参数仅初值待实测。
- SP首批只插值，超出最新快照停在最新权威值，外推上限0（明确暂不实现项目额外表现外推）；连续位置/速度/yaw最短弧插值，离散mode/grounded等取To，静止也推进已接收权威元数据；不调用AP回滚。
- 不可逆事件只随ConfirmedFrame一次通知，模型Simulate输出纯数据事件，回滚替换同帧事件集合，不在模拟调用业务副作用。Finalize由宿主在一批校正/模拟后调用一次，不在resim循环内部。

## 共享接口（主Agent先落盘，只读依赖）

文件Client/Assets/Scripts/PMPrediction/PMPredictionContracts.cs，namespace PMNet.Prediction：
IPMPredictionModel<TInput,TSync,TAux> 深clone三类，Simulate(PMTimeStep,TInput,TSync,TAux)返回PMSimulationResult<TSync,TAux>，ShouldReconcile(predictedSync,authoritativeSync,predictedAux,authoritativeAux)，Interpolate(from,to,alpha)。引用型入参按不可变对待，不得在Simulate改输入快照；所有调用方/模型输出都需脱离可变数组别名。
PMSimulationResult含Sync/Aux及PMPredictionEvent[]。Event为稳定Key(ulong非0)+Kind(int)+Value(int)，确认去重必须有界，不能全局无限HashSet；同帧同key重复只一次，跨回滚同key只在最终确认时发。

预测组拥有PMPredictionTimeline.cs、PMAuthorityInputBuffer.cs、PMInterpolationBuffer.cs，具体公开API可自行设计但报告明确，用泛型接口不依赖Mover。Mover组只依赖共享Contracts+PMNetIdentity，不依赖预测组尚未写的实现，独立测试自己模型。

## Mover冻结字段与机制

namespace PMNet.Mover。PMVector3纯float XYZ(Y-up)。PMMoverInput原始MoveX/MoveZ/MoveY、YawDegrees、JumpPressed边沿；不含派生速度/命中/墙钟。Effect请求单独由宿主鉴权后的输入命令携带，必须标明本阶段可信命令，不把客户端请求直接当ServerInitialize。Input clone复制所有列表。
Sync：Position、Velocity、YawDegrees、Mode、Grounded、GroundNormal、Scale、PreAdditiveVelocity及有效运动参数(MaxSpeed/Acceleration/Braking/GravityScale/JumpSpeed)，ActiveLayers完整(instanceId、kind、priority、velocity、durationMs、elapsedMs)。Aux：Gravity(Y=-9.81默认)、CollisionWorldVersion与配置Version。所有影响未来模拟的值参与clone/恢复/比较，时帧元数据放Snapshot。
Modes数值Walking=0 Falling=1 Flying=2 Inactive=3。Walking地面查询+平面速度，Jump边沿切Falling，Falling重力/扫掠/落地切Walking，Flying三维控制，Inactive零位移但时间轴不停。首批不做斜坡步阶/移动平台，输入到新模型为权威世界坐标，旧队伍镜像只在未来输入/表现适配边界转换，不在模型里双重镜像。
碰撞接口IPMMoverCollisionQuery { int WorldVersion{get;} PMMoverHit Sweep(PMVector3 position, PMVector3 delta, float radius, float halfHeight); PMMoverGround QueryGround(PMVector3 position,float radius,float halfHeight,float distance); }。hit含Blocking/Fraction/Normal；Ground含Found/Distance/Normal；足底与中心明确，角色中心Y=halfHeight落地。模型含有限迭代滑动，不能穿墙/除0/无限循环。测试环境实现确定floor与wall(AABB膨胀相交)，仅测试，不声称PhysX。
InstantEffect最小SetVelocity/Teleport/SetMode/SetParameters；帧前、帧内排队后各消费一遍，输出工作Sync；不得直接改Unity物体。Layers AdditiveVelocity=0/OverrideVelocity=1，稳定priority/ID排序，禁止上帧additive永久积累；时长按原dt，退出恢复干净base。数据类Modifier本批可后置，行为类全部后置；不要写一个空壳冒充完整补偿。拒绝裁决R4-B接线，首批纯模型只消费明确给定的可信命令，不宣称自动撤销已完成。

## 验收

P4A1预测：可变dt/帧n+1、同帧全状态校正、深clone、旧/未来/错Epoch拒绝、重放dt不变、历史满/缺失resync、断线墙钟、确认事件一次、SP离散/旋转/无回滚。
P4A2权威输入：重复乱序、缺帧不跨越、credit防加速、Pump步数/ms上限、超大dt/超窗口/队列容量失败与无副作用。
P4A3Mover：Walk/Fall/Fly/Inactive、地面/墙/滑动、dt多频、加速度重力、层不累积/到期、效果次序/clone/Mode阈值；独立手算oracle，不能用实现自身当预期。
P4A4集成：主Agent用真实Mover模型装进真实预测timeline，强制位置/速度/Mode/参数/层差异恢复重放，模拟期间无外部副作用。
库源码用netstandard2.0+LangVersion7.3独立编译，门禁net8可执行。必须先build0后run；Unity源码新增meta，GUID唯一。报告范围与未实现明说。


## 集成后的权威事件与冻结门修订

`ApplyAuthority`在Frozen或NeedsResync期间返回Stalled，状态/历史/确认边界/事件不变；恢复只走显式可信Resync。
Snapshot兼容新增ConfirmedEvents(仅本输出边界)与ConfirmedEventFrames(按OutputFrame边界的集合)。null=未提供证据，空数组=确定无事件。缺证据不得用预测事件补充广播；先完成restore/replay再按确认区间广播权威事件，一次跳过多边界时只广播有证据的边界。每帧256事件、批最多256边界、总4096事件，结构/身份/窗口检查在任何状态改动之前；数组深clone，同帧同key去重，重复/旧快照不重播。新Epoch重绑Resync不接受携带前Epoch事件证据。
这项为C#适配的显式安全契约，不宣称UE必需相同wire字段。R4-B必须实现权威事件证据可靠补发/确认边界策略，当前只验证内存注入证据，不承诺网络中间事件必达。
