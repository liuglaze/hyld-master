# R5-B2 声明与网络接线契约

状态唯一源net-architecture-migration.md。纯核心/codec见net-r5-projectile-contract.md，用户要求代码优先、实机后置。本契约冻结跨组接口；阶段B2a两组并行World预留与声明对象，B2b驱动消费实际两组产物，不并行改同文件。

## World预留（新增兼容API）
在PMNetWorld：public bool TryReserveNetId(out PMNetSpawnReservation reservation, bool isStatic=false); public bool CancelReservedNetId(PMNetSpawnReservation reservation); public bool SpawnReserved(PMNetObject obj, PMNetSpawnReservation reservation, uint classId, uint archetypeId=0u); public int ReservedNetIdCount {get;}。
PMNetSpawnReservation为同文件public sealed能力令牌，internal构造，public PMNetId NetId只读，内部绑定创建World与消费状态；禁止只凭rawId跨world消费（两world可生成相同rawId）。只允许服务端，预留使用既有_allocator，ID永不复用；预留不登记网络对象、不发Create、不占已存活对象列表但参与_maxObjects容量（live+reserved），普通Spawn也必须计预留。Cancel只删预留不返还编号；SpawnReserved仅消费本World所持预留ID且完整isStatic匹配，成功前所有拒绝不丢预留；上线后一次性消费，不能重复/伪造/跨world/过期使用。Dispose/会话整体清理清掉预留，epoch变更不复活；既有Spawn行为不改。主线程归属按World既有线程规范遵守。SpawnReserved复用现有登记/生命周期入队逻辑，不复制出第二套语义。
Pending生成在RequestSpawn之前预留真实NetId，拒绝/TTL/断开Cancel，Confirmed/SpawnReady才SpawnReserved创建PMR5Projectile，初始snapshot已编码且写好后才上线，Pending期间无客户端幽灵对象。NetId为World-issued，不用伪常量冒充。

## 声明层（单一生成集合）
继续PMNetGen --decl-gen Client/Assets/Scripts/PMR3 --out-dir Client/Assets/Scripts/PMR3/Generated --id-lock Docs/plans/pmnet-r3-ids.json；已有所有Class/RPC/Prop ID不变。PMR3Player新增public IPMProjectileNetworkDriver ProjectileDriver;接口放PMR3Player.cs且只依赖byte[]：void OnServerSpawnPayload(byte[]);void OnServerHitPayload(byte[]);void OnClientDecisionPayload(byte[])。
新增3 RPC：ServerProjectileSpawnV1(byte[]) Reliable+ForceValidate，ServerProjectileHitV1(byte[]) Reliable+ForceValidate，ClientProjectileDecisionV1(byte[]) Reliable。生成方法PMNet_ServerProjectileSpawnV1等；声明ForceValidate拒null/empty/>4096，业务解码/身份准入由driver负责；driver==null上行ForceValidate拒绝而不是假成功。client下行无driver须计丢弃可观察。不要声明直接引用核心实现导致只编声明层的旧消费者全部引入核心。
新增PMR5Projectile.cs [PMNetworkObject] partial PMNetObject：唯一byte[] _projectileSnapshotV1 [PMReplicated]，public byte[] ProjectileSnapshotPayload，public void PublishProjectileSnapshot(byte[])走generated setter；RepNotify转PMR5ProjectileEvents.NotifyUpdated(this)。OnReplicatedCreate/OnReplicatedDestroy(依真实基类API适配)转全局PMR5ProjectileEvents.Created/Destroyed，Updated事件同理。事件须按obj.World筛选，各宿主Dispose取消自己的订阅，不因一个world退出清其它world事件。PMR5ProjectileEvents可以同文件实现，ClearAll仅进程全Shutdown清理。
PMR3Runtime.Attach同时RegisterClass及OnRepDispatcher PMR5Projectile；Shutdown清新事件。新增类静态PMR5DispatchOnRep包装生成方法，同PMR3Player现有PMR3DispatchOnRep模式。不强制新增Spawn辅助函数（driver会自己写初值再World.SpawnReserved+bridge.RegisterReplicatedObject）。

## B2b网络driver（后续顺序）
PMR5ProjectileDriver或等价会话级驱动消费World/Bridge/Coordinator/Codec，每player独立声明回调接缝；DS从player.NetId及认证owner获取身份，trusted配置/玩家位置/激活决定由宿主注入，不能采信上行spec。网络回调只入有界队列，主线程Pump处理。AP先假弹再Generated Spawn RPC，真正可视化由C适配；镜像创建/更新按world及key消费Lifecycle，与AP假弹接管一份表现。非ownerSP只接权威快照不碰伤害。
byte[]生成上限4096保持不变，Snapshot/Decision编码后再检查；Hit候选分成<=48条的RPC且每条都占5次Verify配额之一（不因分包重置配额，超限整次发送前明确拒绝），不能悄悄提高MaxArrayLength。真实Transport测试至少owner/AP+observer/SP两个连接，丢包/重复/错owner/错epoch/决策先于Create/晚镜像/停止与Destroy序。
出口Faulted/SendRpc失败/队列超限明确会话失败，不能仅log仍继续；周期Drain四出口，Settlements给未来R6唯一消费者（无R6不能假称已扣血）。网络状态更新通过声明复制，不新造socket或手工广播MainPack。B2b完成不代表C已自动接真实Unity宿主。


## B2b/C适配可并行边界
B2b新增PMR5ProjectileDriver.cs及PMR5NetworkTest/Check，会话级driver通过BindPlayer把声明接口适配到唯一session；权威配置/枪口/激活授权用public IPMR5ProjectileAuthorityPolicy注入（定义同driver文件，不能信payload），历史PMProjectileHistory由构造入参注入。公开Pump(double wallNowMs,int stepMs)、Dispose、BindPlayer/UnbindPlayer、TryFire本地预测、SubmitPredictedHits、只读view快照与DrainSettlements。具体精确签名由实现报告返回给后续宿主，C本批不依赖driver。DS Pending Request先World预留token，confirmed SpawnReady才编码初始snapshot并SpawnReserved+RegisterReplicatedObject；拒绝/TTL/dispose cancel token；Purge后的权威对象走DestroyObject+注销复制，不假造ID。每Pump按字节budget有界，fault后宿主终止。
C1独立PMUnityProjectileMotion.cs实现IPMProjectileHostMotion：构造(PhysicsScene physicsScene, Collider[] allowedColliders, int layerMask)，必须独立有效PhysicsScene，主线程；SphereCast/Overlap在指定physicsScene，忽略trigger与非白名单，饱和明确失败不是无障碍；半径取可信spec，移动受阻返回true+stop位置+零velocity，TryStop从该步命中记录返回true，出错返回false让core failclosed。运动不做角色伤害，不碰默认Physics world，不全局Physics.SphereCast。真实Unity2019 API编译门PMR5UnityCheck。查询同帧可对多颗弹调用，hit记账必须按Key且有界清理或TryStop即消费，不能共享单个mutable lastHit串弹。
C2独立PMUnityProjectilePresentation.cs为非MonoBehaviour可Dispose薄表现：构造或工厂创建不带Collider/Rigidbody/旧脚本的简单球形可视占位（不盗用原shell玩法Prefab）；Apply(PMProjectileState,PMProjectileSpec)只更新位置/yaw/尺寸/hidden，Stopped不二次触发副作用；DS禁止创建。运行时对象归一个自建根，Dispose清理，材料不反复实例化，不能动原资产。当前只是诊断表现，不称全部英雄子弹外观已移植。后续C宿主消费这些API实际接线。本批适配不修改SessionHost或driver。


## C宿主首个可执行入口（诊断射击，非R6完整玩法）
共享PMProjectileDiagnosticConfig已落盘只读：10m/s、0.1m、1500ms生命周期、200ms开火间隔、16ms步最多8步/Update、0.6m枪口偏移。双方只用于网络投射物探针，不能称英雄普通攻击/大招/资源校验已完成；不消费旧CommandManger/BattleManger，不复用旧shell。
DS宿主为每会话创建History/Motion/Driver（场景就绪后、连接接入前）；OnConnected玩家创建即BindPlayer。每Pump先Endpoint排队→PumpMovement→为所有driver记录本次host AuthorityServer帧+OutputBoundary+TotalSimTimeMs+world Stopwatch时间的胶囊样本（半径/半高PMMoverDefaults、按Sync.Scale有效缩放），再固定16ms累计最多8子步Pump投射物，零步也Pump(0)排裁决，history新stream由history.Record清旧。可信policy检查已认证绑定、来源activation单owner单调不重用、200ms墙钟间隔、已存在运动driver且未freeze，并使用共享诊断spec和DS真位置，客户端任何本地spec不影响。team/self filter从DS roster查，缺数据fail closed。DrainSettlements作为诊断命中计数/日志，不直接扣血，必须记录尚无R6消费者；正式伤害后续替换，不声称完整战斗。
Client会话创建同场景Motion+Driver，现有所有player绑定、后续OnPlayerReplicated绑定；新增F键单次（非每帧连发）诊断开火，只本地AP，按当前Yaw世界方向+枪口偏移，activation递增uint不回绕。输入在Update采样一次，和Mover Input边沿同纪律；UI文本可日志注明诊断。每帧固定16ms最多8步Pump后按CopyViews/DrainViewChanges维护presentation字典，删除/隐藏必须真正Dispose或Apply，不产生第二套权威。不能依据Hidden判断Destroyed（Stopped tombstone可隐藏但存在），视图移除通知需读真实driver约定。
Client候选收集不依赖目标Collider（PlayerVisual无Collider）：owner自己的ClientPredicted视图线段对其他player当前展示的Y轴胶囊做纯数学候选检测，按目标当前stream和其展示snapshot ServerFrame携带锚，不拿本地input frame冒充；TargetVisualOffset=0（首批不支持额外视觉偏移），impact取候选中心线附近点交DS消毒。只上报候选，DS几何/队伍/预算最终判定。每弹每target客户端去重有界100，字典随view移除清理；必须在目标本帧真实呈现状态后收集，且最终运动子步之前的轨迹不能丢（每子步收一次），禁止落到旧HP/伤害字段。
Fault→宿主原Fail清理，Driver→Motion→presentation→地图按合理顺序Dispose（退出不留事件/预留）；停用场景前清motion。正式/诊断地图选择继续用既有digest分流，不增加自动回退。C实机待统一验证，代码/真实API编译必须本轮完成。
