# R3 控制编排与会话接线契约

本文展开主计划R3，不另存阶段状态。实现现状与验收以 net-architecture-migration.md 为准。

## 1. 范围与阶段

R3-A提供Lobby控制协议/进程协调器，以及已认证连接的Transport/World/RPC/Replication适配。R3-B负责匹配入口、真正的DS控制Agent与Unity场景、客户端切服。A阶段不能标T42通过，不得悄悄替换旧匹配流程或启动旧DS产物冒充新宿主。

保持Unity2019.4，纯共享代码C#7.3/netstandard2.0；共享源放Client/Assets/Scripts/PMNet，由Server链接。运行期无反射业务分发，protobuf字节编码使用已有PMNetReader/Writer。不新增JSON网络协议、不改变SocketProto旧业务契约。控制协议可以是独立的手写有界codec，不手写业务属性/RPC同步。

## 2. 身份及票据

首版Lobby直接拉本机Unity DS。身份字段：MatchId(string <=128 UTF8字节)、DsId(string <=128)、SessionEpoch(uint非0)、ProtocolHash(uint)、Uid(int>0)、PlayerId(int>0)、TeamId(int>=0)、HeroId(int>=0)。名册最多6人且Uid/PlayerId唯一。所有权只能来自已认证票据对应名册，不从业务包自报uid采纳。

每局256位随机控制密钥，玩家票据为HMAC-SHA256绑定上述对局/世代/摘要/玩家身份、到期UTC秒与随机Nonce。使用加密随机数，不用Random/时间戳当秘密；常量时间比对MAC。默认有效期120秒（允许配置），只允许当前局名册玩家。绝不日志输出密钥或完整票据。票据重试只对同一已绑定会话幂等；禁止第二来源端点拿同票据顶替，重连票据由Lobby后续显式重发。R3-A票据模块只验证真实性和字段，端点消费账本由R3-B连接接受器负责，必须注明不可把MAC验证当作防重放完成。

DS引导文件（R3-B）只传文件路径给进程，包含本局密钥和名册，受本机账户边界保护；不把完整票据/密钥写日志或拼接shell。文件用临时写后原子发布；端口范围与进程路径来自配置白名单，不信任客户端路径。A阶段进程启动使用接口，可测试失败、退出和超时；真实Process适配只允许固定exe+已转义参数，不能调用cmd /c执行拼接输入。

## 3. 控制消息和状态

控制帧为4字节LE长度+载荷，单帧上限64KiB。载荷使用PMNetReader/Writer，包含格式版本1、消息类型、MatchId/DsId/Epoch/ProtocolHash、RequestId或ResultId、业务字段和MAC；MAC验证前不得修改状态。首版TCP仅loopback；循环解帧需支持半包/粘包、超限立即拒绝。编解码不依赖Unity或第三方JSON。

消息：Bootstrap(名册/票据仅向受信DS发送)、Ready(实际boundPort、sceneReady、碰撞配置摘要)、Heartbeat、Result(ResultId、winnerTeamId、结算摘要)、ResultAck(同ResultId)、Shutdown、Exited、Error。未知类型/版本/尾部/长度越界明确失败。Bootstrap传输与同机文件选择由A1报告明确，但不能把控制密钥通过未认证远端接口下发。

Lobby状态：Allocated -> Starting -> Ready -> Running -> ResultPending -> ResultCommitted -> Exited；任意非终态可Failed/TimedOut。只在身份/摘要/启动代次匹配、端口与分配一致、sceneReady=true时对客户端发布Ready；不能靠进程存在/日志字符串即宣布就绪。默认启动30秒、心跳15秒、结果确认重试1秒、收尾10秒（注入时钟可配置，不用测试sleep）。重复Ready不重复通知；结果以(MatchId,Epoch,ResultId)幂等，同ID不同内容拒绝；确认丢失可重发同一结果获得同一Ack。内存去重账本有TTL和上限，终态释放玩家与端口占用，但保留短期幂等墓碑；Lobby重启不承诺跨进程恰好一次（当前无数据库）。失败/崩溃必须可观测并回收资源。

R3没有Mover/战斗结束逻辑，结果入口由权威玩法调用，不用任意定时器自动伪造正常胜利。A测试可主动调用完成入口。

## 4. 已认证连接的网络适配（R3-A2）

落点 Session/PMTransportConnection.cs、Session/PMNetSessionBridge.cs（namespace PMNet.Session）。适配器构造时接受已认证身份(连接ID、Uid/PlayerId、Epoch、双方ProtocolHash)、现有IPMTransportLink、共享PMNetWorld。未完成显式激活前IsReady=false且不派发/发送；双方Hash不一致拒绝激活并断开；Epoch必须与世界会话一致。A2不创建第二套票据机制，R3-B必须在调用Activate前验证A1票据并绑定真实端点；适配器本身不是对外握手服务器。

连接继承PMNetConnection，实现IPMTransportSink和IPMNetRpcDisconnectTarget，持有PMTransport；对端字节交OnDatagram，业务线程Update驱动；不得接收线程执行回调。连接进入/离开World与Replication成对，断开清理；客户端仅接受当前受信服务器连接，服务端仅接受已认证玩家连接。不得用连接IsServerSide混淆world.IsServer。

应用信封统一：版本1+Kind(byte: Lifecycle=1,Rpc=2,Replication=3,ReplicationAck=4)+长度限定正文，严格拒绝尾部。RPC正文target NetId+bStatic、ClassId、RpcId、ParamLayoutId、参数字节；ClassId与当前目标一致、LayoutId与描述符一致，否则ProtocolError断连。可靠RPC与Create/Destroy在PMStream.Reliable同一顺序域；普通RPC按描述符可靠性选择域。复制Update/ACK用PMStream.Replication，不把底层包ACK当属性版本ACK。禁止跨顺序域推导对象创建已到达；先于Create的Update依既有策略丢弃并靠后续收敛。

PMNetSessionBridge是每世界协调者，提供SendRpc(target,rpcId,PMRpcWriter)供生成Registry.RemoteSender绑定（核心不得直接依赖尚不存在的生产生成类）。按目标ClassId查描述符：Server只向本客户端已认证服务器连接；Client向权威Owner链连接；Multicast向已拥有该副本且相关连接。Create必须先入同一可靠流，再入RPC，Destroy后RPC拒绝。不把OwnerConnection与Owner链两个冲突连接都视为拥有者，宿主只能指定一个权威来源。

主线程次序：drain transport -> 生命周期排队 -> 复制Tick和版本ACK发送 -> transport.Update flush；断开/Dispose幂等。MaxDatagramBytes=1200、MaxFragmentsPerMessage=64保持既有初值；应用消息最大64KiB且不能超过Transport实际可发送上限，超限显式失败/断连，不静默丢可靠消息。每帧数据报预算32、入站队列/连接数有限；有界与实际行为均需测试。A2如发现现有Transport限制不足必须报告，禁止在边界外悄悄修。

## 5. 验收边界

A1门禁PMDsControlTest：真实codec、非法/过期/错局/错摘要票据、半包粘包与超限、可控时钟状态机、重复Ready/结果、失败启动/崩溃/退出回收。进程用替身即明确仅状态机验收，不宣传真实Unity进程。
A2门禁PMNetSessionTest：两个端点实际PMTransport+World+Replication+RPC接收入口，生命周期初值、双向RPC、Owner拒绝、Layout/Hash/Epoch错误、Validate失败真实Transport断连、丢包重排最终收敛、连接清理。使用手工描述符只为适配器独立测试，不伪称验证生成器；生成链已由R2覆盖，R3-B需再集成。
所有build退出0后才run；增加负向输入与非空可达计数。A阶段全部PASS仅表示R3-A可用于后续接线。T42保留PENDING直到真实Lobby启动UnityDS、加载权威场景并完成两客户端闭环。


## 6. R3-A实施审查修订

- ResultCommitted不表示DS确认收到ResultAck。当前协议无Ack-of-Ack；PeerExitObserved记录对端Exited或实际进程退出。终态不得仅凭Kill请求归还端口，必须实际确认退出；失败节流重试并保持占用。
- 同ResultId冲突判定比较winner与完整summary，不能只比较截短hash。
- M03分片长度字段为ushort，应用信封含头总长最多65535字节，另受实际分片容量约束；原64KiB为概念上限，不允许把65536传入Transport。Transport.Send已显式拒绝越界，不能静默截断。
- Transport新增Update(now,sendBudget)；桥的帧首/帧尾共用同一发送预算，严格不超过MaxDatagramsPerUpdate。入站解析次数仍有界但两次Update会处理两批，尚未独立拆drain/flush。
- 复制方向严格：DS只收ReplicationAck，客户端只收Replication Update；违反ProtocolError断连，不应用也不ACK。
- R3-A采用长度由Transport消息边界限定的正文（version+kind之后剩余字节），未另存正文长度字段；RPC/生命周期/复制内层必须自行恰好消费。
- R3-B在票据验证/端点绑定完成前不得把字节交给Transport，以免传输ACK已发生但应用仍未激活。票据MAC真实性不代表消费防重放；需票据哈希账本与全局uid占用表。
- 大生命周期批次超限当前以显式断连兜底，字节切批留待宿主集成前扩展，不声称可服务任意大世界。


## 7. R3-B冻结接口（本轮实施）

R3-B是基础网络对象闭环，不抢先实现R4–R6完整战斗。新链由Lobby配置`HYLD_PMNET_DS=1`显式启用；缺省保留旧开局，选新链后失败只能报失败，禁止静默回退生成第二权威。旧UDP诊断只允许不带bootstrap的历史启动，新链带bootstrap时独占分配-port，不再创建PMUdpRouter。

### 7.1 Lobby到客户端通知（冻结，无需改proto生成物）

使用已认证Lobby TCP的StartEnterBattle通知，MainPack.Str承载 `PMDS1:` + Base64(有界PMNetWriter编码的PMDsEntryOffer)，它只承载控制面入局信息，不是业务状态同步；BattleInfo可以继续放名册，但新路径不得进入旧BattleManger。客户端识别前缀必须严格解码，解码失败报错不得退旧链。沿用MainPack仅是现有大厅载体，不承诺旧客户端兼容。不会用JSON或分隔符拼接秘密。

共享类型由网络组实现，namespace PMNet.Session：
```
public sealed class PMDsEntryOffer {
 public string MatchId, DsId, Host;
 public uint Epoch, ProtocolHash, CollisionDigest;
 public int Port;
 public PMNet.Control.PMDsRosterIdentity Identity;
 public byte[] Ticket;
}
public static class PMDsEntryCodec {
 public const string Prefix = "PMDS1:";
 public static string Encode(PMDsEntryOffer offer);
 public static bool TryDecode(string text, out PMDsEntryOffer offer, out string error);
}
```
codec整条<=4096字节，标识<=128UTF8，票据按已有codec实际上限，端口1..65535，严格尾部和范围。禁止把offer/完整Str写日志。

### 7.2 UDP宿主接口（网络组实现，Unity组只消费）

所有状态和socket收发由调用方主线程Pump驱动，非阻塞UDP无需后台执行Unity。握手在Transport之外走独立magic/version，单帧最多1200字节（512B不足以覆盖现有票据最大值，不沿用调查中的错误估算）。失败无回包；成功应答不大于请求，必要时Hello请求填充，填充受上限约束。客户端检查预期DS源端点+随机nonce+ticket SHA256+Match/Ds/Epoch/Hash。此为与Lobby通知的关联校验，不宣称抵御持票中间人，也不自造DTLS。
```
public sealed class PMUdpSessionEndpoint : IDisposable {
 public static PMUdpSessionEndpoint OpenServer(PMNet.Control.PMDsBootstrappedMatch boot,
     PMNetSessionBridge bridge, string listenAddress, int port);
 public static PMUdpSessionEndpoint OpenClient(PMDsEntryOffer offer, PMNetSessionBridge bridge);
 public int BoundPort {get;}
 public int ConnectionCount {get;}
 public event Action<PMTransportConnection> Connected;
 public event Action<string> Failed;
 public void Pump(long nowMs, long nowUnixSeconds); // 排空有限UDP，握手重试，再bridge.Update，宿主不要二次Update
 public void Dispose();
}
```
OpenServer必须从boot名册逐身份验票，不只验证MAC。账本key完整SHA256，成功绑定同票同端点同ConnectionId幂等；不同端点/不同票同uid拒绝。入场票据到期只影响新的入场/重试，不能把已激活正常对局在120秒票据过期时踢掉。断开后旧票保留消费墓碑直到过期，不允许同票换端点重连。连接数<=6，未认证不分配无界状态，socket每Pump收包有预算。PMTransportConnection.OnDatagram纵深前置IsReady，未激活绝不交Transport以免先ACK后丢。

### 7.3 Lobby宿主（Lobby组实现）

`Server/DS/PMDsLobbyHost.cs`单线程所有者：线程安全命令队列/控制TCP入站队列 -> drain -> Coordinator.Tick；真正TCP listener只loopback。全局共享端口池+uid占用，启动参数必须从Allocate后的真实Port生成。可给Coordinator增加Allocated态SetLaunchRequest校验接口。引导文件写临时再rename、路径在配置的每局目录下，失败回滚；BeginStart只在发布成功后。启动参数固定exe，-bootstrap绝对文件 -control 127.0.0.1:端口 -port实际值 -dsid/-matchid。
匹配StartFighting分流，一次只选一条。新链请求名册与已认证活跃Client一致，离线缺人整局拒绝，不悄悄缩编。uid占用与资源释放同点；不要借用旧BattleManage字典给新DS提供伪旧路由。退出/断线回收通知新宿主；玩家掉线首版中止该测试局，不伪造正常胜利。Ready才发offer，ResultAccepted可给参与者大厅控制结果通知，不伪造BattleReview帧历史（回放属R6）。
注册hash来源由Unity组PMR3Runtime提供；Server.csproj链接PMR3纯C#声明及生成物，排除PMR3 Unity适配(后者放Server/Boot)。Lobby初始化PMR3Runtime.Register()再分配局，不接受外部客户端提供hash为权威。可测试时注入hash。

### 7.4 Unity与声明对象（Unity组实现）

纯源码落 `Client/Assets/Scripts/PMR3/PMR3Player.cs`、`PMR3Runtime.cs`、`Generated/PMNet.PMNet.R3.PMR3Player.g.cs`、`Generated/PMNetGeneratedRegistry.g.cs`。namespace PMNet.R3。外部PMNetGen生成，专用锁Docs/plans/pmnet-r3-ids.json，不动旧共享锁。工具中含自己生成registry的E2E必须排除PMR3(现有PMNet glob不包括它)。
```
public static class PMR3Runtime {
 public static void Register(); // 生成Registry.RegisterAll()
 public static uint ProtocolHash {get;}
 public const uint CollisionDigest = 0x52334201u;
 public static void Attach(PMNetWorld world, PMNetSessionBridge bridge); // 世界工厂/OnRep/RemoteSender
 public static PMR3Player SpawnPlayer(PMNetWorld world, PMNetSessionBridge bridge,
     PMTransportConnection owner); // Authority角色+唯一owner+登记复制，避免双身份
}
```
最小纯字段复制Uid/ProbeCount，ServerProbe(int nonce)带ForceValidate、执行后增ProbeCount并ClientEcho(nonce)；通过生成桩发，不能手写业务状态包。客户端接收对象后调用一次ServerProbe并观察Echo与属性收敛，主线程触发。生命周期回调发布对象可通过Runtime事件，不反射找类型。
DS `PMDsHost`在-bootstrap非空时转交新`PMDsSessionHost`，旧诊断不启动。新host读bootstrap并校验options局/端口，建立世界/注册/UDP端点，初始化固定测试碰撞场景，再与Lobby控制TCP交互Ready/Heartbeat/ResultAck。共享布局规格：平面地板中心(0,-0.5,0)尺寸(40,1,40)，墙中心(0,1,0)尺寸(1,2,8)，均BoxCollider；CollisionDigest标识此版本布局，不声称旧地图摘要。运行时创建实际Collider且确认enabled/active才SceneReady；不加载旧HYLDGame。客户端PMClientSessionHost按同布局建测试场景，保留Lobby TCP，关闭旧UDP后OpenClient。UIMatchingPanel先检查PMDS1前缀，交新host，不执行旧BattleData/ClearSence。无需改Unity场景资产，PMDsBoot中由宿主程序化创建验证场景。
默认不自动胜利；新增-server-smoke选项可仅在显式自动验收时，全部名册玩家完成Probe后提交测试结果，摘要明确smoke。正常路径提供SubmitResult由未来权威玩法调用。只有收到匹配ResultAck才请求退出，超时进入失败退出并留日志，不冒充确认成功。
新Unity脚本+生成产物都有唯一meta；不在编辑器打开状态下启动batchmode/关闭编辑器。先桩编译+纯C#真实UDP/控制测试，再保留真实Unity验收PENDING_USER。


## 8. R3-B集成修订

- ResultPending已经受理结果时，经MAC/Match/Ds/Epoch/Hash校验的显式Exited(0)可标正常Exited；没有协议证据的进程提前退出仍Failed。两者均等本地确认进程死后释放端口和uid。
- Transport默认KeepAliveIntervalMs=1000，IdleTimeoutMs=10000，包括从未收到首包的看门狗；Ping沿既有ControlPing，不新增消息格式或RTO，计入同一发送预算。纯ACK不触发再次ACK，但仍跟踪包号并消费确认信息。生产宿主不得禁用keepalive后仍假定完全静默丢包会主动恢复。
- 墓碑容量淘汰未过期记录时，至其过期前拒绝未知摘要的新消费，避免有界缓存忘记重放保护；摘要对外返回独立副本。
- 当前实际结果与命令以主计划R3-B末段为准。真实TCP+UDP整链测试143项通过仍不等于真实Unity进程T42通过。


## 9. 真实DS验收后的控制活性修订

控制Heartbeat使用既有消息类型双向：DS发送间隔5000ms；Lobby只在身份/MAC/状态通过的Ready或Heartbeat后应答，最小间隔1000ms，不按收到应答立即回声。DS Ready首个可信响应等待30000ms，收到后运行期15000ms无可信下行则失败退出；本端发送成功不续命。结果等待同样受运行期看门狗约束。两端须同版，2026-09-20 12:12用户构建不包含此修复，需再build。
