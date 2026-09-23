# R5 投射物契约（首批实现）

状态唯一源 net-architecture-migration.md。用户授权代码优先，Unity双客户端后置；自动编译/纯核心测试仍执行。不启动Unity，不改源场景，不提交。

## 阶段
A 纯核心两组并行：生命周期/预测镜像/三类等待，与历史位置/几何/验证。B 顺序集成两者并接同一PMR3声明生成集合；C Unity宿主、输入与表现；D R6业务整合。A不能声称B/C或整阶段T45完成。共享只读类型 PMProjectile/PMProjectileContracts.cs 由主Agent提供，不得子组各定义一套。

## 身份与安全
PMProjectileKey=(Epoch,OwnerNetId,ProjectileId,Origin)。OwnerNetId必须取认证会话绑定网络玩家而非客户端自报；客户端只能ClientPredicted，ServerDirect只由权威入口产生。ProjectileId在同epoch/owner/origin单调增加永不复用，耗尽显式失败；重置需新epoch。权威NetId仍来自PMNetWorld，区别于预测关联键。A离线库不冒充完成网络Owner认证，B必须接生成接收校验。跨epoch/owner/stream请求不影响状态。

## 单位和时钟
复用PMNet.Mover.PMVector3，Y-up米，Yaw度。墙钟输入double毫秒单调用于TTL/墓碑，不用帧超时。目标历史含两种时间：WorldTimeMs是DS会话单调经过时间，用于各对象一致的回溯时刻；TotalSimTimeMs只用于同一对象同stream帧锚的新鲜度。AuthorityServer帧锚与Input边界明确分域，禁止做帧偏移猜转换。历史样本由DS真实运动结果提供：A支持Record，B/C在运动Pump后按宿主AuthorityServerFrame记录最后完成状态；同宿主帧可有多输入，选择最后输出，不宣称每子步精确回溯。升流清历史，旧流请求拒绝，不跨Teleport插值。时间回溯失败可退当前权威位置，但输出Resolution枚举暴露退化，不能称精确历史命中。

## 语义与有限资源（R0适配修订）
- 沿D-R0-30..40：预测先假弹；权威注册先于追赶；接管一次性消费完整运动/停止/命中集合；假弹存活时镜像位置不覆盖；假弹已经结束的迟到镜像必须隐藏且不再次停止通知。Rejected实际撤销预测实例；ServerDirect镜像不收集候选/不自行结算。
- 激活ID=0仅可信ServerDirect可用；客户端请求非0激活ID由权威账本裁决。不照搬UE客户端可伪造server-origin号段自动Confirmed的信任缺口。
- Pending Spawn上限128/owner（本项目有意偏离D-R0-35无cap源实现，拒新并产生明确结果），TTL2000ms；Verify-before-Spawn每key最多5、总128，FIFO、随spawn过期清理；Pending命中结论每key合并不刷新初始时间，最多128组，满时丢最旧未结算组并可观测，TTL2000ms；权威/预测活对象各上限1024，owner状态最多64，升epoch或Dispose整体清理。不能用TTL代替容量。每key目标去重有界100；整会话不会无限保留已销毁key，用每owner/origin高水位拒绝重用。终态不得被迟到Pending/Confirmed反转。
- 停止不立即Destroy，墓碑max(150,delayDestroyMs,clamp(2*predictionMs+100,0,1000))；只在墓碑内接受合法迟到Verify，超窗明确过期，不承诺可靠RPC可无限迟到。回收清白名单/命中集合/激活/运动/回调。队列出队和去重水位必须在调用外部回调之前提交，异常不能重复结算。

## 命中验证（纯验证不直接扣血）
L0 Rejected整包拒，Pending通过几何后仅暂存；L1生命周期/最多5次Verify/每批100目标/同弹同目标一次；L2飞行预算speed*(2*clamp(rewind,0,500)+100+extraDefer)/1000，停止回放锚spawn、停止常规锚stop，停止常规上限radius+5+0.3米，skipTrajectory只跳飞行；L3逐目标合法/白名单/历史→视觉偏移→ImpactPoint钳制→Filter→线段到Y轴胶囊中心线几何；L4输出权威结论给唯一结算者，Pending绝不调用伤害出口。目标尺寸由权威history provider提供，radius>0 halfheight>=radius；中心线半长halfheight-radius。
上行点finite、segment长度<=20m、visualOffset<=20m、rewind>=0；rewind>1000ms计Report但仍检查所有候选，不提前绕过Reject。锚窗口600+extraDefer毫秒以TotalSimTimeMs相减；rewind上限500ms，历史512样本/目标、保留1000ms，目标数最多64。impact只钳不因越界整批拒，过滤后再判几何。未接骨骼精细校验，BoneName仅诊断且不授予伤害部位倍数；本项目首批胶囊角色，不虚构UE怪物骨骼体系。完整可选骨骼校验后置并登记。

## A验收
T5A1 两种origin、同owner重号拒/跨owner合法、跨epoch拒、TTL/容量/升流/断连清理。
T5A2 假弹运动接管、停止接管不二次事件、先结束后镜像不复活、拒绝撤销。
T5A3 三类Pending乱序/确认/拒绝/超时、Verify-before-Spawn按序、Pending无结算、重复确认不重复结果。
T5A4 同serverframe多输入/variable dt/错域帧/未来锚/缺历史/Teleport/Stream隔离。
T5A5 L0-L4正负/NaN/Inf/超长/超配额/多目标独立尺寸/平行及退化线段/Impact消毒顺序。
T5A6 netstandard2.0+C#7.3无Unity依赖编译、net8真实核心测试与负向样本；A整合测试必须调用两组真实实现；实机T45保留PENDING_USER。


## A3/B1集成冻结
A3 PMProjectileCoordinator负责可信owner/muzzle admission、权威注册与三队列生命周期联动、Validate后目标去重/StopOnHit/Pending结论唯一消费、整毫秒<=50直线运动与墓碑清理；不直接扣血，DrainSettlements是R6唯一接收点。RequestSpawn拿authenticatedOwner与authorityNetId/ownerPosition的可信宿主入参，不能信request.State的Owner/权威ID/命中集合/停止状态。client请求spec不得被当权威配置，须独立trustedSpec入参；可信ServerDirect走独立入口。每spawn克隆只允许位置/velocity方向/yaw意图，速度由trustedSpec决定。Pending多个弹可同activation，ID应在收请求时预留，乱序解挂不能再按ID高水位拒已受理项。Reject/TTL/capacity不结算；命中组只存PMProjectileValidatedHitSet，不保存未经校验的hit batch。最终结算前确认目标epoch/stream/alive仍有效，callback外置，先消费再返回独占结果。全部对外队列有界、溢出显式失败，不能静默丢确认导致永久假弹。

B1 PMProjectileCodec纯编解码，namespace PMNet.Projectile，不引用Coordinator。上行SpawnIntent专用DTO：Key、ActivationId、Position、Direction、Yaw、PredictionMs；禁止上行Spec/AuthorityNetId/HitTargets/Stopped等权威字段。HitBatch用冻结类型；下行Snapshot=State+Spec（权威ID非0）；下行Decision=Key+ActivationId+Confirmed/Rejected(+reason固定枚举或有界字符串)。wire protobuf字段头kind=1/version=2/epoch=3/owner=4/projectileId=5/origin=6；载荷10起按单调field顺序，未知/重复/错wire/缺字段/尾部/超长全拒，不兼容旧schema。上行origin只ClientPredicted，direction finite且非零（权威再归一化）；batch/candidate上限100；payload<=16384（独立codec上限，最终PMR3 byte[]生成RPC上限还要接线验证，不宣称当前能直发16KB）。读端分配前长度上限，对外数组克隆；固定Kind Spawn=1 Hit=2 Snapshot=3 Decision=4，Version=1。Snapshot运动完整字段/HitTargets/AllowedTargets有界；不能把client SpawnRequest序列化带入trusted Spec。ID锁在B2统一生成集合再分配，B1不改PMR3Player或generated。


## A3复核后的公开边界
ReportHits(batch,authenticatedOwnerNetId,wallNowMs)必须绑定认证owner；B2 codec拒任何上行ServerDirect（权威内部命中可由可信调用进入）。RequestSpawn预留通过后只在Confirmed用TryPromoteReservedToAuthority原地升级；权威NetId必须由World提供，B2不得伪造。输出队列容量512，满则永久Faulted并抛异常，已排队结果保留；宿主必须每TickDrain并将Faulted作为会话失败处理，AdvanceEpoch为唯一恢复。运动hook false/非finite/异常就地停止，禁止回退直线。当Pending几何通过时立即StopOnHit并预留去重，Confirm最后校验目标Alive/stream后才产出一次Settlement。可信ServerDirect的Decision允许ActivationId=0，ClientPredicted仍要求非0。

实现证据见主计划R5-A/B1交付段，纯C#门禁不代表已接Unity宿主或网络声明。Next B2仍需冻结World预留NetId API与byte[]4096分包计次语义。
