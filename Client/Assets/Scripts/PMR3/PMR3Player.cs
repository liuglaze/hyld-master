// R3-B：最小纯字段网络对象（契约 Docs/plans/net-r3-control-contract.md §7.4）。
//
// 本文件是**声明阶段**的输入：`Tools/PMNetGen --decl-gen` 拿它生成
//   Client/Assets/Scripts/PMR3/Generated/PMNet.PMNet.R3.PMR3Player.g.cs
//   Client/Assets/Scripts/PMR3/Generated/PMNetGeneratedRegistry.g.cs
// ID 锁文件专用：Docs/plans/pmnet-r3-ids.json（**不动** Docs/plans/pmnet-ids.json 那份共享锁）。
//
// 为什么对象放在 Client/Assets/Scripts/PMR3/ 而不是 PMNet/ 目录内：
//   Tools/PMNetE2E 之类的既有门禁以 `Client/Assets/Scripts/PMNet/**` 为编译通配符，
//   而 E2E 自己**也**生成一份 `PMNet.Generated.PMNetGeneratedRegistry`。
//   把 PMR3 放进 PMNet/ 会让两者编进同一个程序集 ⇒ 注册表类型重名。
//   放在 PMNet 目录之外，既有门禁的通配符天然不包含它（契约 §7.4 明确要求排除）。
//
// 本类型刻意只有「状态自动属性 + RPC声明」，不含任何业务状态机：
//   - 复制：`_uid` / `_probeCount`（Push 模型，普通赋值由编织器自动标脏）；
//   - 上行：`ServerProbe(int nonce)`（Reliable + ForceValidate，项目红线）；
//   - 下行：`ClientEcho(int nonce)`（Reliable）。
// 所有收发都经**声明层入口**（普通名 `ServerProbe` / `ClientEcho`）；
// 编译后由 Tools/PMNetWeaver 把业务体拆到私有 `PMNet_RpcBody_<M>`，普通调用即网络入口。
// **不手写业务状态包**（契约 §7.4 原文）。

using System;
using PMNet;

namespace PMNet.R3
{
    /// <summary>
    /// 运动网络驱动的**接缝**（声明层与实现层之间唯一约定）。
    ///
    /// 定义在声明文件里而不是 PMNet 目录下，是为了不新增共享文件；
    /// 实现者是 Client/Assets/Scripts/PMR3/PMR4MovementDriver.cs。
    /// 这五个入口全部由**声明承载**转发进来（RPC 业务实现 / RepNotify），
    /// 宿主不需要（也不应该）在别处再调它们。
    /// </summary>
    public interface IPMMovementNetworkDriver
    {
        /// <summary>复制层通知“运动快照属性更新了”（只通知/入队，不在这里解码）。</summary>
        void OnSnapshotReplicated();

        /// <summary>DS：收到一条上行输入载荷。</summary>
        void OnServerInputPayload(byte[] payload);

        /// <summary>DS：收到一条重同步请求。</summary>
        void OnServerResyncRequest(uint streamVersion);

        /// <summary>AP：收到一批权威事件。</summary>
        void OnClientEventsPayload(byte[] payload);

        /// <summary>AP：收到一张重同步完整快照。</summary>
        void OnClientResyncPayload(byte[] payload);
    }

    /// <summary>
    /// 投射物网络驱动的**接缝**（R5-B2a 契约 Docs/plans/net-r5-network-contract.md「声明层」冻结）。
    ///
    /// 与 <see cref="IPMMovementNetworkDriver"/> 同一条纪律：定义在**声明文件**里，
    /// 只依赖 `byte[]`，因此声明层既不引用 PMProjectile 纯核心、也不引用 Coordinator ——
    /// 只编「声明层 + PMNet 核心」的旧消费者工程（PMR3RuntimeTest / PMR3IntegrationTest 等）
    /// 不需要新增任何引用。
    ///
    /// 实现者是后续 B2b 的会话级驱动（PMR5ProjectileDriver 或等价物）：
    /// 字节解码、身份准入、有界队列、主线程 Pump 全部由它负责；
    /// 声明层只负责「把载荷可靠地送到这条接缝」与「参数域准入」。
    /// </summary>
    public interface IPMProjectileNetworkDriver
    {
        /// <summary>DS：收到一条上行投射物**生成**载荷（已过参数域准入）。</summary>
        void OnServerSpawnPayload(byte[] payload);

        /// <summary>DS：收到一条上行投射物**命中**载荷（已过参数域准入）。</summary>
        void OnServerHitPayload(byte[] payload);

        /// <summary>AP：收到一条下行投射物**裁决**载荷（本副本不是 owner 时由接入方在驱动里丢弃）。</summary>
        void OnClientDecisionPayload(byte[] payload);
    }

    /// <summary>
    /// 战斗网络驱动的**接缝**（R6-A2 契约 Docs/plans/net-r6-combat-contract.md「声明（与核心并行）」冻结）。
    ///
    /// 与 <see cref="IPMMovementNetworkDriver"/> / <see cref="IPMProjectileNetworkDriver"/> 同一条纪律：
    /// 定义在**声明文件**里，参数**只用原生类型**（uint / bool / float / int），
    /// 因此声明层既不引用 PMCombat 纯核心、也不引用 R5 的 Projectile/Coordinator ——
    /// 只编「声明层 + PMNet 核心」的旧消费者工程（PMR3RuntimeTest / PMR3IntegrationTest 等）
    /// 不需要新增任何引用。
    ///
    /// 实现者是 session 级的 <c>PMR6CombatDriver</c>（B 组）：
    /// 授权队列、身份准入、项目计划与 R5 投射物泵的全部职责在它那边；
    /// 声明层只负责「把载荷可靠地送到这条接缝」与「参数域准入」。
    ///
    /// **所有入口都只应入队**：复制回调与 RPC 接收都在入站 drain 栈上，
    /// 在这里直接发送会与本次帧的发送时机纠缠（契约 A2 原文：OnCombatStateReplicated() 只入队）。
    /// </summary>
    public interface IPMCombatNetworkDriver
    {
        /// <summary>复制层通知「战斗复制状态更新了」（只通知/入队，不在这里发 RPC / 不解码）。</summary>
        void OnCombatStateReplicated();

        /// <summary>DS：收到一条上行攻击声明（已过参数域准入：非 0 激活 ID、方向有限非 0 且 ≤1.001）。</summary>
        void OnServerAttack(uint activationId, bool isSuper, float aimX, float aimZ);

        /// <summary>AP：收到一条攻击裁决（accepted=false 时 reason 给出拒绝码）。</summary>
        void OnClientAttackResult(uint activationId, bool accepted, int reason);

        /// <summary>AP：收到一局比赛结果（winnerTeamId 是名册真实队伍号，0 = 无唯一胜者）。</summary>
        void OnClientMatchResult(uint outcomeId, int winnerTeamId);

        /// <summary>DS：收到 owner 对该局结果的确认（幂等；驱动据此推进 ResultAck 闭环）。</summary>
        void OnServerResultAck(uint outcomeId);
    }

    /// <summary>
    /// R3-B 的最小可复制网络对象：一个玩家副本 + 一个「探针-回声」往返。
    ///
    /// 生命周期：由服务端 <see cref="PMR3Runtime.SpawnPlayer"/> 唯一创建（Authority + 唯一 owner），
    /// 客户端经生命周期消息收到副本后触发 <see cref="OnReplicatedCreate"/>，
    /// 由宿主（客户端会话宿主）据此发一次 <c>ServerProbe</c>。
    /// </summary>
    [PMNetworkObject]
    public partial class PMR3Player : PMNetObject
    {
        // ---------------------------------------------------------------- 复制属性（纯字段）

        /// <summary>该副本的账号 uid。**权威来源是 DS 侧票据验签后的名册身份**，不从业务包自报 uid 采纳。</summary>
        [PMReplicated]
        private int _uid { get; set; }

        /// <summary>该副本被上行探针确认执行的次数（服务端计数，复制回流给客户端观察收敛）。</summary>
        [PMReplicated]
        private int _probeCount { get; set; }

        // ---------------------------------------------------------------- R4-B 运动状态（复制）

        /// <summary>
        /// 运动权威快照载荷（R4-B / B1）。
        ///
        /// **这是"当前状态收敛"通道，不承载事件语义**（契约 §B1：快照只保证当前状态收敛；
        /// 不可逆事件走独立的可靠 RPC）。载荷结构由 <see cref="PMR4MovementCodec"/> 定义，
        /// 声明成 `byte[]` 是为了让**生成物**只负责"把这片字节可靠地搬到对端"，
        /// 而不在生成层重新发明运动字段（契约 §B1 原文：blob 内部由专门框架 codec 编码）。
        ///
        /// DS 侧只在**帧边界**写它（<see cref="PMR4MovementDriver.PublishInitialSnapshot"/> /
        /// 每次 Pump 有真实推进时）；客户端侧只读，且 `OnRep` 只做"通知/入队"，
        /// 真正解码与应用在 Driver 的 ApplyPending 里。
        /// </summary>
        [PMReplicated]
        private byte[] _movementSnapshotV1 { get; set; }

        // ---------------------------------------------------------------- 本地诊断字段（不参与复制）
        //
        // 这些计数器只用于「门禁与联调可观测」，不承载任何权威语义：
        // 复制只能保证当前状态最终收敛，用它来表达「事件发生了几次」是错的用法（契约 §3.9.4），
        // 所以往返次数一律记在各自进程的本地字段上。

        /// <summary>客户端：本副本上发起过多少次上行探针（本地记账，非复制）。</summary>
        public int ProbeSentCount;

        /// <summary>拥有者客户端：收到过多少次 <c>ClientEcho</c>（本地记账，非复制）。</summary>
        public int EchoCount;

        /// <summary>拥有者客户端：最近一次 <c>ClientEcho</c> 携带的 nonce；<see cref="int.MinValue"/> = 从未收到。</summary>
        public int LastEchoNonce = int.MinValue;

        /// <summary>本副本上 <see cref="OnReplicatedCreate"/> 被调用的次数（本地记账，非复制）。</summary>
        public int ReplicatedCreateCount;

        // ---------------------------------------------------------------- 只读访问器
        //
        // 复制成员在生成物里是 private，读回需要业务侧开一个口子（E2E 夹具同样做法：
        // 生成物只提供「赋值即标脏」的写入口，不提供读入口）。

        /// <summary>读回复制的 uid。</summary>
        public int Uid { get { return _uid; } }

        /// <summary>仅从认证名册初始化身份；普通属性赋值自动处理权威标脏。</summary>
        internal void InitializeIdentity(int uid) { _uid = uid; }

        /// <summary>读回复制的探针计数。</summary>
        public int ProbeCount { get { return _probeCount; } }

        // ---------------------------------------------------------------- R4-B 运动 Driver（委托出口）
        //
        // 契约 §B1：「PMR3Player 只含字段、声明与委托出口，算法放 Driver」。
        // 因此本类对运动只做三件事：持有 Driver 引用、把入站 RPC 转发给 Driver、
        // 把复制到的快照载荷借给 Driver 解码。**不含任何运动算法**。

        /// <summary>
        /// 本副本的运动驱动接缝（由宿主创建后自动绑上：<see cref="PMR4MovementDriver"/> 的构造函数写本字段）。
        ///
        /// **为什么是接口而不是具体类型**：本文件是**声明层**，它只应知道自己声明的接缝；
        /// 运动实现（codec / driver / 预测与 Mover 依赖）放在另外两个文件里。
        /// 这样附带一个真实的集成好处：只编译“声明层 + PMNet 核心”的消费者工程
        /// （如 Tools/PMR3RuntimeTest、Tools/PMR3IntegrationTest）**不需要任何 csproj 修改**，
        /// 否则它们会因为声明层引用具体实现而直接 CS0246。
        ///
        /// 为 null 时所有运动 RPC/属性一律**丢弃并计数**（不静默假装成功），
        /// 这样"宿主忘了建 Driver"是可见的，而不是表现成"网络偶尔不同步"。
        /// </summary>
        public IPMMovementNetworkDriver MovementDriver;

        /// <summary>
        /// 最近一次复制到的运动快照载荷（**只读语义**：调用方不得就地改写）。
        /// 每次复制更新都会换成新数组，因此借出期间不会被并发改写。
        /// </summary>
        public byte[] MovementSnapshotPayload { get { return _movementSnapshotV1; } }

        /// <summary>运动快照复制更新的通知出口（诊断/宿主观察用；Driver 走的是直接转发）。</summary>
        public event Action<PMR3Player> MovementSnapshotUpdated;

        /// <summary>运动相关告警的唯一出口（宿主接到项目日志；未接线时丢弃）。</summary>
        public static Action<string> MovementWarn;

        /// <summary>发出一条运动告警（Driver 用；不抛异常，避免把告警变成故障）。</summary>
        public void WarnMovement(string message)
        {
            Action<string> handler = MovementWarn;
            if (handler != null)
            {
                handler(message);
            }
        }

        /// <summary>
        /// DS 侧写快照的唯一出口："赋值即标脏"（Push 模型）。
        /// 自动属性由编织器跟踪变化，本方法提供**面向业务的发布入口**，
        /// 避免调用方直接依赖生成物的命名细节。
        /// </summary>
        public void PublishMovementSnapshot(byte[] payload)
        {
            _movementSnapshotV1 = payload;
        }

        /// <summary>
        /// 复制属性 `_movementSnapshotV1` 变化的 RepNotify。
        /// **只通知/入队**（契约 §B1 原文）：不在这里解码、不在这里改 Driver 状态。
        /// </summary>
        [PMRepNotify(nameof(_movementSnapshotV1))]
        private void OnRep_MovementSnapshot()
        {
            if (MovementDriver != null)
            {
                MovementDriver.OnSnapshotReplicated();
            }

            Action<PMR3Player> handler = MovementSnapshotUpdated;
            if (handler != null)
            {
                handler(this);
            }
        }

        /// <summary>
        /// 唯一 owner 来源：显式绑定的 <see cref="PMNetObject.OwnerConnection"/>。
        ///
        /// 契约 §7.4 要求「Authority 角色 + 唯一 owner + 登记复制，避免双身份」：
        /// 这里刻意**不**沿 `Owner` 链推导，也**不**另设 `NetConnection`，
        /// 否则复制层的 OwnerOnly 条件与 RPC 归属校验会各自看到一条不同的连接
        /// （<c>PMNetSessionBridge.ResolveOwnerConnection</c> 在两者冲突时直接拒绝发送）。
        /// </summary>
        public override PMNetConnection GetNetConnection()
        {
            return OwnerConnection;
        }

        /// <summary>
        /// 本端副本创建完成（初始状态已应用、对象已登记进世界）。
        ///
        /// 这里只做一件框架级的事：把对象交给 <see cref="PMR3Runtime"/> 的事件出口，
        /// 由宿主决定「这个副本是不是我的、要不要发一次探针」。
        /// **不用反射找类型**（契约 §7.4 原文），也从不在 DS 侧发生
        /// —— DS 的权威副本由 <see cref="PMNetWorld.Spawn"/> 创建，不经过本回调。
        /// </summary>
        protected internal override void OnReplicatedCreate()
        {
            ReplicatedCreateCount++;
            PMR3Runtime.NotifyPlayerReplicated(this);
        }

        // ---------------------------------------------------------------- RPC

        /// <summary>
        /// 上行探针：客户端 → DS（Reliable + ForceValidate）。
        ///
        /// 实现体只做两件事：把 ProbeCount 加一（普通赋值由编织器自动标脏），然后把 nonce 经**声明层入口**（普通名 ClientEcho）回声给拥有者。
        /// **不许手写业务状态包**：往返全部走声明层入口与复制通道。
        /// </summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void ServerProbe(int nonce)
        {
            _probeCount = _probeCount + 1;

            // 经声明层入口下行（普通名 `ClientEcho`；编织器已把业务体拆到私有体，普通调用不再是「只本地执行」）。
            ClientEcho(nonce);
        }

        /// <summary>
        /// <c>ServerProbe</c> 的三态校验同伴（规则 13：声明了 ForceValidate 就必须真的存在它）。
        ///
        /// 这里只做**参数域**校验：nonce 允许任意 int（测试要能构造负值 nonce 证明参数是原样往返的），
        /// 因此一律 Accept。真正的准入在别处：票据验签、端点绑定、以及
        /// <c>PMNetRpcDispatch.ShouldCallRemoteFunction</c> 的「服务端只接受该对象拥有者连接」。
        /// </summary>
        private PMRpcValidation ServerProbe_ForceValidate(int nonce)
        {
            return PMRpcValidation.Accept;
        }

        /// <summary>
        /// 下行回声：DS → 拥有者客户端（Reliable）。
        /// </summary>
        [PMClientRpc(Reliability = PMRpcReliability.Reliable)]
        public void ClientEcho(int nonce)
        {
            EchoCount++;
            LastEchoNonce = nonce;
        }

        // ---------------------------------------------------------------- R4-B 运动 RPC
        //
        // 承载（契约 §B1 冻结）：
        //   上行输入  ServerMovementInputV1(byte[])  Unreliable + ForceValidate
        //   上行重同步 ServerMovementResyncV1(uint)   Reliable   + ForceValidate
        //   下行事件  ClientMovementEventsV1(byte[])  Reliable（给 owner）
        //   下行重同步 ClientMovementResyncV1(byte[]) Reliable（给 owner）
        //
        // 为什么上行输入走**不可靠**：高频输入靠"原始输入有限重发窗口"自愈，
        // 不需要也不可能让可靠通道去排队每一次输入（契约 §3.9.4：不能为可靠把运动流堵在一个队列里）。
        // 归属校验由 PMNetRpcReceive 的 Owner 判定完成（**不绕过**），因此这些业务实现
        // 只会被"该对象的拥有者连接"调用到。

        /// <summary>
        /// 上行输入批：AP → DS（不可靠 + 参数域校验）。
        /// 只把原始载荷交给 Driver（解码/身份/流代次/上限都由 Driver 与 codec 校验）。
        /// </summary>
        [PMServerRpc(Reliability = PMRpcReliability.Unreliable, Validator = PMRpcValidator.ForceValidate)]
        public void ServerMovementInputV1(byte[] payload)
        {
            if (MovementDriver == null)
            {
                WarnMovement("[PMR3Player] 收到上行运动输入但本副本没有 Driver（已丢弃）：" + GetNameSafe());
                return;
            }

            MovementDriver.OnServerInputPayload(payload);
        }

        /// <summary>
        /// <c>ServerMovementInputV1</c> 的三态校验同伴（规则 13）：只做**参数域**准入。
        ///
        /// 真正的字节级校验在 codec 里（上限 4096、kind/version/身份/字段完整性/NaN/尾部……），
        /// 而且生成物的数组读入本身还有一道 4096 长度门。这里**不再重复**那个数字，
        /// 避免声明层反向依赖 codec 实现（否则只编译声明层的消费者工程会 CS0246）。
        /// </summary>
        private PMRpcValidation ServerMovementInputV1_ForceValidate(byte[] payload)
        {
            return payload == null || payload.Length == 0 ? PMRpcValidation.Reject : PMRpcValidation.Accept;
        }

        /// <summary>
        /// 上行重同步请求：AP → DS（可靠 + 参数域校验）。DS 收到后按"是否在当前流上"决定
        /// 升流并下发完整可信快照，或只补发当前流快照。
        /// </summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void ServerMovementResyncV1(uint streamVersion)
        {
            if (MovementDriver == null)
            {
                WarnMovement("[PMR3Player] 收到上行重同步请求但本副本没有 Driver（已丢弃）");
                return;
            }

            MovementDriver.OnServerResyncRequest(streamVersion);
        }

        /// <summary><c>ServerMovementResyncV1</c> 的三态校验同伴：流代次必须非 0（0 是无效哨兵）。</summary>
        private PMRpcValidation ServerMovementResyncV1_ForceValidate(uint streamVersion)
        {
            return streamVersion == 0u ? PMRpcValidation.Reject : PMRpcValidation.Accept;
        }

        /// <summary>
        /// 权威事件批：DS → owner（可靠）。**这是不可逆事件的唯一承载**，
        /// 因此它和快照通道是两条独立的链（事件不依赖快照确认游标到达）。
        /// </summary>
        [PMClientRpc(Reliability = PMRpcReliability.Reliable)]
        public void ClientMovementEventsV1(byte[] payload)
        {
            if (MovementDriver == null)
            {
                return;
            }

            MovementDriver.OnClientEventsPayload(payload);
        }

        /// <summary>显式重同步的完整快照：DS → owner（可靠）。接收侧据此整体重绑到新流。</summary>
        [PMClientRpc(Reliability = PMRpcReliability.Reliable)]
        public void ClientMovementResyncV1(byte[] payload)
        {
            if (MovementDriver == null)
            {
                return;
            }

            MovementDriver.OnClientResyncPayload(payload);
        }

        // ---------------------------------------------------------------- R5-B2a 投射物 RPC（声明承载）
        //
        // 承载（契约 Docs/plans/net-r5-network-contract.md「声明层」冻结）：
        //   上行生成  ServerProjectileSpawnV1(byte[])  Reliable + ForceValidate
        //   上行命中  ServerProjectileHitV1(byte[])    Reliable + ForceValidate
        //   下行裁决  ClientProjectileDecisionV1(byte[]) Reliable
        //
        // 声明层**只做参数域准入**（null / 空 / 超 4096 / 无 driver），
        // 业务解码、身份准入与字节语义一律由 driver 负责。因此本文件既不引用
        // PMProjectile 纯核心，也不引用 Coordinator（契约原文：不要声明直接引用核心实现
        // 导致只编声明层的旧消费者全部引入核心）。
        //
        // 上行两条走**可靠**：它们是不可逆事件（生成/命中）+ 后续裁决的输入，
        // 不允许把事件流塞进不可靠通道靠状态收敛自愈（契约 §3.9.4）。
        // 下行裁决也走可靠：它决定 owner 侧假弹的接管/拒绝结论，重复投递由 driver 幂等吸收。

        /// <summary>
        /// 声明层允许的投射物载荷字节上限（与生成物的数组读入门 `PMGeneratedMaxArrayLength` 同值）。
        ///
        /// 这里**显式写出 4096** 而不是从别处推导：契约要求「声明 ForceValidate 拒 null/empty/&gt;4096」，
        /// 因此它是声明层的准入常量；真正的**语义**上限（生成候选 ≤4321B 需按预算分批等）在 driver/codec，
        /// 声明层不重复那套规则，也不允许通过改这个常量偷偷放宽线上预算。
        /// </summary>
        public const int ProjectileMaxPayloadBytes = 4096;

        /// <summary>
        /// 本副本的投射物驱动接缝（由后续 B2b 的会话驱动创建后绑上）。
        ///
        /// 为 null 时：**上行**在 ForceValidate 里直接 Reject（不是假成功、也不是静默丢弃），
        /// **下行**计入 <see cref="ProjectileDecisionDiscardedCount"/> 作为可观察丢弃。
        /// 这样「宿主忘了建驱动」是可见的，而不是表现成「投射物偶尔不同步」。
        /// </summary>
        public IPMProjectileNetworkDriver ProjectileDriver;

        /// <summary>AP：本副本收到并交给 driver 的下行裁决条数（本地记账，非复制）。</summary>
        public long ProjectileDecisionAppliedCount;

        /// <summary>AP：本副本收到但**没有 driver** 而被丢弃的下行裁决条数（可观察丢弃，本地记账）。</summary>
        public long ProjectileDecisionDiscardedCount;

        /// <summary>投射物相关告警的唯一出口（宿主接到项目日志；未接线时丢弃）。</summary>
        public static Action<string> ProjectileWarn;

        /// <summary>发出一条投射物告警（不抛异常，避免把告警变成故障）。</summary>
        public void WarnProjectile(string message)
        {
            Action<string> handler = ProjectileWarn;
            if (handler != null)
            {
                handler(message);
            }
        }

        /// <summary>
        /// 上行投射物生成：AP → DS（可靠 + 参数域校验）。只把原始载荷交给 driver。
        /// </summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void ServerProjectileSpawnV1(byte[] payload)
        {
            if (ProjectileDriver == null)
            {
                // 正常情况下走不到这里：ForceValidate 同伴已经先把它拒了。
                // 保留这条守卫是为了「校验被绕过」时也不静默假装成功。
                WarnProjectile("[PMR3Player] 收到上行投射物生成载荷但本副本没有 ProjectileDriver（已丢弃）：" + GetNameSafe());
                return;
            }

            ProjectileDriver.OnServerSpawnPayload(payload);
        }

        /// <summary><c>ServerProjectileSpawnV1</c> 的三态校验同伴（规则 13）。</summary>
        private PMRpcValidation ServerProjectileSpawnV1_ForceValidate(byte[] payload)
        {
            return ValidateProjectileUpstreamPayload(payload, "ServerProjectileSpawnV1");
        }

        /// <summary>
        /// 上行投射物命中：AP → DS（可靠 + 参数域校验）。
        /// 候选点分成 ≤48 条的 RPC 是 driver 的职责（每条都占 5 次 Verify 配额之一），
        /// 声明层只保证单条载荷在 4096 内。
        /// </summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void ServerProjectileHitV1(byte[] payload)
        {
            if (ProjectileDriver == null)
            {
                WarnProjectile("[PMR3Player] 收到上行投射物命中载荷但本副本没有 ProjectileDriver（已丢弃）：" + GetNameSafe());
                return;
            }

            ProjectileDriver.OnServerHitPayload(payload);
        }

        /// <summary><c>ServerProjectileHitV1</c> 的三态校验同伴（规则 13）。</summary>
        private PMRpcValidation ServerProjectileHitV1_ForceValidate(byte[] payload)
        {
            return ValidateProjectileUpstreamPayload(payload, "ServerProjectileHitV1");
        }

        /// <summary>
        /// 两条上行投射物 RPC 共用的参数域准入（契约：拒 null / empty / &gt;4096 / driver==null）。
        ///
        /// 返回 <see cref="PMRpcValidation.Reject"/> 而不是抛异常：
        /// ForceValidate 的 Reject 语义是「上报后跳过实现、**不断连**」，这正是上行载荷域错误应有的处置。
        /// </summary>
        private PMRpcValidation ValidateProjectileUpstreamPayload(byte[] payload, string methodName)
        {
            if (ProjectileDriver == null)
            {
                WarnProjectile("[PMR3Player] " + methodName + " 被拒：本副本没有 ProjectileDriver（不假装成功）");
                return PMRpcValidation.Reject;
            }

            if (payload == null || payload.Length == 0)
            {
                return PMRpcValidation.Reject;
            }

            if (payload.Length > ProjectileMaxPayloadBytes)
            {
                WarnProjectile("[PMR3Player] " + methodName + " 被拒：载荷 " + payload.Length
                               + " 字节超过声明层上限 " + ProjectileMaxPayloadBytes);
                return PMRpcValidation.Reject;
            }

            return PMRpcValidation.Accept;
        }

        /// <summary>
        /// 下行投射物裁决：DS → owner（可靠）。
        ///
        /// 无 driver 时**计入可观察丢弃**（契约：client 下行无 driver 须计丢弃可观察），
        /// 而不是静默忽略。真正的身份/幂等判定在 driver 里。
        /// </summary>
        [PMClientRpc(Reliability = PMRpcReliability.Reliable)]
        public void ClientProjectileDecisionV1(byte[] payload)
        {
            if (ProjectileDriver == null)
            {
                ProjectileDecisionDiscardedCount++;
                WarnProjectile("[PMR3Player] 收到下行投射物裁决但本副本没有 ProjectileDriver（已丢弃）：" + GetNameSafe());
                return;
            }

            ProjectileDecisionAppliedCount++;
            ProjectileDriver.OnClientDecisionPayload(payload);
        }

        // ---------------------------------------------------------------- R6-A2 战斗声明（复制属性 + RPC）
        //
        // 承载（契约 Docs/plans/net-r6-combat-contract.md「A2 声明（与核心并行）」冻结）：
        //
        //   复制（公共）    _combatHeroId / _combatTeamId / _combatHp / _combatMaxHp
        //                   _combatDead / _combatMatchEnded / _combatWinnerTeamId
        //   复制（OwnerOnly）_combatMana / _combatSuperEnergy   ← 资源是**权限边界**，不是带宽优化
        //   上行            ServerCombatAttackV1(uint,bool,float,float)  可靠 + ForceValidate
        //                   ServerCombatResultAckV1(uint)                可靠 + ForceValidate
        //   下行            ClientCombatAttackResultV1(uint,bool,int)    可靠（给 owner）
        //                   ClientCombatMatchResultV1(uint,int)          可靠（给 owner）
        //
        // 三条不变量（本层只声明、不实现战斗规则）：
        //   1) 战斗**算法/授权/结算**全在 PMCombat 纯核心 + R6 driver；本文件不含任何规则；
        //   2) 复制只有 Push 一条写路：外部必须经 PublishCombatState / 生成的 Setter；
        //   3) RepNotify 与 RPC 入口一律**只通知/入队**，不在回调里发送。

        /// <summary>本副本的战斗驱动接缝（由 B 组的 <c>PMR6CombatDriver</c> 创建后绑上）。</summary>
        public IPMCombatNetworkDriver CombatDriver;

        /// <summary>
        /// 上行攻击方向的**逐分量**字面范围上限（契约 A2 原文：<=1.001，最终由 core 归一化）。
        ///
        /// 刻意**不在这里做归一化**：归一化是 core 的职责（声明层不得复制战斗算法），
        /// 声明层只拒绝明显越界/非有限/零向量的参数域错误。
        /// </summary>
        public const float CombatAimComponentLimit = 1.001f;

        /// <summary>本副本上战斗复制通知（RepNotify）被派发的次数（本地记账，非复制；同一批可多次）。</summary>
        public long CombatStateNotifyCount;

        /// <summary>战斗 RepNotify 期间被隔离的异常次数（驱动或订阅者抛异常；本地记账，非复制）。</summary>
        public long CombatNotifyFailureCount;

        /// <summary>AP：本副本收到并交给驱动**应用**的攻击裁决条数（本地记账，非复制）。</summary>
        public long CombatAttackResultAppliedCount;

        /// <summary>AP：本副本收到但**没有驱动**而被丢弃的攻击裁决条数（可观察丢弃，本地记账）。</summary>
        public long CombatAttackResultDiscardedCount;

        /// <summary>AP：本副本收到并交给驱动**应用**的比赛结果条数（本地记账，非复制）。</summary>
        public long CombatMatchResultAppliedCount;

        /// <summary>AP：本副本收到但**没有驱动**而被丢弃的比赛结果条数（可观察丢弃，本地记账）。</summary>
        public long CombatMatchResultDiscardedCount;

        /// <summary>本副本上 <see cref="PublishCombatState"/> 成功写入复制字段的次数（本地记账）。</summary>
        public long CombatPublishCount;

        /// <summary>本副本上 <see cref="PublishCombatState"/> 因**非权威世界**被拒的次数（可观察拒绝，本地记账）。</summary>
        public long CombatPublishRejectedCount;

        /// <summary>战斗相关告警的唯一出口（宿主接到项目日志；未接线时丢弃）。</summary>
        public static Action<string> CombatWarn;

        /// <summary>发出一条战斗告警（不抛异常，避免把告警变成故障）。</summary>
        public void WarnCombat(string message)
        {
            Action<string> handler = CombatWarn;
            if (handler != null)
            {
                handler(message);
            }
        }

        /// <summary>复制到的英雄号（DS 名册真值；只读）。</summary>
        public int CombatHeroId { get { return _combatHeroId; } }

        /// <summary>复制到的真实队伍号（名册 TeamId；只读）。</summary>
        public int CombatTeamId { get { return _combatTeamId; } }

        /// <summary>复制到的当前 HP（只读）。</summary>
        public int CombatHp { get { return _combatHp; } }

        /// <summary>复制到的 HP 上限（只读）。</summary>
        public int CombatMaxHp { get { return _combatMaxHp; } }

        /// <summary>复制到的死亡标记（只读）。</summary>
        public bool CombatDead { get { return _combatDead; } }

        /// <summary>复制到的一局比赛结果（只读）。</summary>
        public bool CombatMatchEnded { get { return _combatMatchEnded; } }

        /// <summary>复制到的胜利队伍号（0 = 无唯一胜者；只读）。</summary>
        public int CombatWinnerTeamId { get { return _combatWinnerTeamId; } }

        /// <summary>复制到的普通攻击蓝量（OwnerOnly；非 owner 永远读不到，只读）。</summary>
        public int CombatMana { get { return _combatMana; } }

        /// <summary>复制到的大招能量（OwnerOnly；非 owner 永远读不到，只读）。</summary>
        public int CombatSuperEnergy { get { return _combatSuperEnergy; } }

        /// <summary>
        /// R6-A2 战斗复制状态（DS 权威 → 各客户端；资源两条仅 owner）。
        ///
        /// 全部写在**公共复制字段**上；资源两条是 OwnerOnly 条件属性 ——
        /// 复制层按连接过滤，非 owner 连接在 Create 初值与后续 Update 里都拿不到它们
        /// （契约 A2 原文：OwnerOnly 不得泄露）。
        /// </summary>
        [PMReplicated]
        private int _combatHeroId { get; set; }

        /// <inheritdoc cref="_combatHeroId"/>
        [PMReplicated]
        private int _combatTeamId { get; set; }

        /// <inheritdoc cref="_combatHeroId"/>
        [PMReplicated]
        private int _combatHp { get; set; }

        /// <inheritdoc cref="_combatHeroId"/>
        [PMReplicated]
        private int _combatMaxHp { get; set; }

        /// <inheritdoc cref="_combatHeroId"/>
        [PMReplicated]
        private bool _combatDead { get; set; }

        /// <inheritdoc cref="_combatHeroId"/>
        [PMReplicated]
        private bool _combatMatchEnded { get; set; }

        /// <inheritdoc cref="_combatHeroId"/>
        [PMReplicated]
        private int _combatWinnerTeamId { get; set; }

        /// <summary>普通攻击蓝量：**仅 owner 可见**（OwnerOnly 条件，权限边界）。</summary>
        [PMReplicated(PMCond.OwnerOnly)]
        private int _combatMana { get; set; }

        /// <summary>大招能量：**仅 owner 可见**（OwnerOnly 条件，权限边界）。</summary>
        [PMReplicated(PMCond.OwnerOnly)]
        private int _combatSuperEnergy { get; set; }

        /// <summary>
        /// 战斗复制状态更新的**唯一通知出口**（每个属性的 RepNotify 都汇到这里）。
        ///
        /// 契约允许同一批多次触发（复制层对每条送达的 Update 记录派发 OnRep，不做值比较），
        /// 因此消费方必须按「**至少**通知一次 + 读当前值」使用，不得把它当事件计数。
        /// **不在回调里发任何 RPC**（契约 A2 原文）。
        /// </summary>
        public event Action<PMR3Player> CombatStateUpdated;

        /// <summary>
        /// DS 侧写战斗复制状态的唯一出口（「赋值即标脏」，Push 模型）：
        /// 九条属性全部经生成的 Setter 写入，外部不得直接改私有字段。
        ///
        /// **权限**：只允许权威（DS）侧写。本副本若已经挂在**非权威**世界上
        /// （即 AP 客户端收到的副本），调用会被拒并计入 <see cref="CombatPublishRejectedCount"/>，
        /// 复制字段保持原值 —— 契约 A2 原文「AP 不写复制字段」。
        /// 尚未上线（`World == null`）时放行：DS 侧允许「先写好初值再 Spawn」（与 R5 投射物同一手法）。
        /// </summary>
        /// <returns>是否真的写入（false = 被权限拒绝，值未被改动）。</returns>
        public bool PublishCombatState(int heroId, int teamId, int hp, int maxHp, bool dead,
                                       int mana, int energy, bool ended, int winner)
        {
            PMNetWorld world = World;
            if (world != null && !world.IsServer)
            {
                CombatPublishRejectedCount++;
                WarnCombat("[PMR3Player] PublishCombatState 被拒：本副本在非权威世界上（AP 不写复制字段）："
                           + GetNameSafe());
                return false;
            }

            _combatHeroId = heroId;
            _combatTeamId = teamId;
            _combatHp = hp;
            _combatMaxHp = maxHp;
            _combatDead = dead;
            _combatMana = mana;
            _combatSuperEnergy = energy;
            _combatMatchEnded = ended;
            _combatWinnerTeamId = winner;

            CombatPublishCount++;
            return true;
        }

        /// <summary>
        /// 战斗复制状态通知的统一实现（九条属性的 RepNotify 全部转发到这里）。
        ///
        /// 只做两件事：**通知驱动入队** + **广播本地事件**；两者都逐订阅者隔离异常
        /// （驱动/宿主的 bug 不该让整条复制链炸掉），并计数到
        /// <see cref="CombatNotifyFailureCount"/> 让「通知被吞掉」保持可观测。
        /// </summary>
        private void NotifyCombatStateReplicated()
        {
            CombatStateNotifyCount++;

            IPMCombatNetworkDriver driver = CombatDriver;
            if (driver != null)
            {
                try
                {
                    driver.OnCombatStateReplicated();
                }
                catch (Exception ex)
                {
                    CombatNotifyFailureCount++;
                    WarnCombat("[PMR3Player] 战斗驱动 OnCombatStateReplicated 抛异常（已隔离）："
                               + ex.GetType().Name + " " + ex.Message);
                }
            }

            Action<PMR3Player> handler = CombatStateUpdated;
            if (handler == null)
            {
                return;
            }

            Delegate[] list = handler.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                try
                {
                    ((Action<PMR3Player>)list[i])(this);
                }
                catch (Exception ex)
                {
                    CombatNotifyFailureCount++;
                    WarnCombat("[PMR3Player] CombatStateUpdated 订阅者异常（已隔离）："
                               + ex.GetType().Name + " " + ex.Message);
                }
            }
        }

        /// <summary>复制属性 <c>_combatHeroId</c> 的 RepNotify（只通知/入队）。</summary>
        [PMRepNotify(nameof(_combatHeroId))]
        private void OnRep_CombatHeroId() { NotifyCombatStateReplicated(); }

        /// <summary>复制属性 <c>_combatTeamId</c> 的 RepNotify（只通知/入队）。</summary>
        [PMRepNotify(nameof(_combatTeamId))]
        private void OnRep_CombatTeamId() { NotifyCombatStateReplicated(); }

        /// <summary>复制属性 <c>_combatHp</c> 的 RepNotify（只通知/入队）。</summary>
        [PMRepNotify(nameof(_combatHp))]
        private void OnRep_CombatHp() { NotifyCombatStateReplicated(); }

        /// <summary>复制属性 <c>_combatMaxHp</c> 的 RepNotify（只通知/入队）。</summary>
        [PMRepNotify(nameof(_combatMaxHp))]
        private void OnRep_CombatMaxHp() { NotifyCombatStateReplicated(); }

        /// <summary>复制属性 <c>_combatDead</c> 的 RepNotify（只通知/入队）。</summary>
        [PMRepNotify(nameof(_combatDead))]
        private void OnRep_CombatDead() { NotifyCombatStateReplicated(); }

        /// <summary>复制属性 <c>_combatMatchEnded</c> 的 RepNotify（只通知/入队）。</summary>
        [PMRepNotify(nameof(_combatMatchEnded))]
        private void OnRep_CombatMatchEnded() { NotifyCombatStateReplicated(); }

        /// <summary>复制属性 <c>_combatWinnerTeamId</c> 的 RepNotify（只通知/入队）。</summary>
        [PMRepNotify(nameof(_combatWinnerTeamId))]
        private void OnRep_CombatWinnerTeamId() { NotifyCombatStateReplicated(); }

        /// <summary>复制属性 <c>_combatMana</c> 的 RepNotify（只通知/入队；OwnerOnly 通道不会对非 owner 触发）。</summary>
        [PMRepNotify(nameof(_combatMana))]
        private void OnRep_CombatMana() { NotifyCombatStateReplicated(); }

        /// <summary>复制属性 <c>_combatSuperEnergy</c> 的 RepNotify（只通知/入队；OwnerOnly 通道不会对非 owner 触发）。</summary>
        [PMRepNotify(nameof(_combatSuperEnergy))]
        private void OnRep_CombatSuperEnergy() { NotifyCombatStateReplicated(); }

        /// <summary>
        /// 上行攻击声明：AP → DS（可靠 + 参数域校验）。
        ///
        /// 只把**已过参数域准入**的四个实参交给驱动（授权、资源、项目计划全在 core 侧）；
        /// 归属校验由 `PMNetRpcReceive` 的 Owner 判定完成（**不绕过**），因此本实现
        /// 只会被「该对象的拥有者连接」调用到。
        /// </summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void ServerCombatAttackV1(uint activationId, bool isSuper, float aimX, float aimZ)
        {
            if (CombatDriver == null)
            {
                // 正常情况下走不到这里：ForceValidate 同伴已经先把它拒了。
                // 保留这条守卫是为了「校验被绕过」时也不静默假装成功。
                WarnCombat("[PMR3Player] 收到上行攻击声明但本副本没有 CombatDriver（已丢弃）：" + GetNameSafe());
                return;
            }

            CombatDriver.OnServerAttack(activationId, isSuper, aimX, aimZ);
        }

        /// <summary>
        /// <c>ServerCombatAttackV1</c> 的三态校验同伴（规则 13：声明了 ForceValidate 就必须真的存在它）。
        ///
        /// 参数域（契约 A2 原文）：driver 非 null、激活 ID 非 0、方向**有限**且**非零**、
        /// 逐分量字面范围 &lt;= 1.001（归一化留 core）。Reject 只跳过实现、**不断连**。
        /// </summary>
        private PMRpcValidation ServerCombatAttackV1_ForceValidate(uint activationId, bool isSuper,
                                                                   float aimX, float aimZ)
        {
            if (CombatDriver == null)
            {
                WarnCombat("[PMR3Player] ServerCombatAttackV1 被拒：本副本没有 CombatDriver（不假装成功）");
                return PMRpcValidation.Reject;
            }

            if (activationId == 0u)
            {
                return PMRpcValidation.Reject;
            }

            if (float.IsNaN(aimX) || float.IsInfinity(aimX) || float.IsNaN(aimZ) || float.IsInfinity(aimZ))
            {
                return PMRpcValidation.Reject;
            }

            if (aimX == 0f && aimZ == 0f)
            {
                return PMRpcValidation.Reject;
            }

            if (Math.Abs(aimX) > CombatAimComponentLimit || Math.Abs(aimZ) > CombatAimComponentLimit)
            {
                WarnCombat("[PMR3Player] ServerCombatAttackV1 被拒：方向越界 (" + aimX + ", " + aimZ
                           + ")，逐分量上限 " + CombatAimComponentLimit);
                return PMRpcValidation.Reject;
            }

            return PMRpcValidation.Accept;
        }

        /// <summary>
        /// 上行结果确认：AP → DS（可靠 + 参数域校验）。
        /// 客户端对 <c>ClientCombatMatchResultV1</c> 幂等显示后回这一条，DS 据此推进结算闭环。
        /// </summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void ServerCombatResultAckV1(uint outcomeId)
        {
            if (CombatDriver == null)
            {
                WarnCombat("[PMR3Player] 收到上行结果确认但本副本没有 CombatDriver（已丢弃）：" + GetNameSafe());
                return;
            }

            CombatDriver.OnServerResultAck(outcomeId);
        }

        /// <summary><c>ServerCombatResultAckV1</c> 的三态校验同伴：driver 非 null 且 outcomeId 非 0（0 是无效哨兵）。</summary>
        private PMRpcValidation ServerCombatResultAckV1_ForceValidate(uint outcomeId)
        {
            if (CombatDriver == null)
            {
                WarnCombat("[PMR3Player] ServerCombatResultAckV1 被拒：本副本没有 CombatDriver（不假装成功）");
                return PMRpcValidation.Reject;
            }

            return outcomeId == 0u ? PMRpcValidation.Reject : PMRpcValidation.Accept;
        }

        /// <summary>
        /// 下行攻击裁决：DS → owner（可靠）。
        ///
        /// 无 driver 时**计入可观察丢弃**（契约 A2：ClientRPC null driver 有可观察计数），
        /// 而不是静默忽略。真正的幂等与假弹撤销在 driver 里。
        /// </summary>
        [PMClientRpc(Reliability = PMRpcReliability.Reliable)]
        public void ClientCombatAttackResultV1(uint activationId, bool accepted, int reason)
        {
            if (CombatDriver == null)
            {
                CombatAttackResultDiscardedCount++;
                WarnCombat("[PMR3Player] 收到下行攻击裁决但本副本没有 CombatDriver（已丢弃）：" + GetNameSafe());
                return;
            }

            CombatAttackResultAppliedCount++;
            CombatDriver.OnClientAttackResult(activationId, accepted, reason);
        }

        /// <summary>
        /// 下行比赛结果：DS → owner（可靠）。客户端幂等显示后必须回 <c>ServerCombatResultAckV1</c>。
        /// 无 driver 时计入可观察丢弃（同上）。
        /// </summary>
        [PMClientRpc(Reliability = PMRpcReliability.Reliable)]
        public void ClientCombatMatchResultV1(uint outcomeId, int winnerTeamId)
        {
            if (CombatDriver == null)
            {
                CombatMatchResultDiscardedCount++;
                WarnCombat("[PMR3Player] 收到下行比赛结果但本副本没有 CombatDriver（已丢弃）：" + GetNameSafe());
                return;
            }

            CombatMatchResultAppliedCount++;
            CombatDriver.OnClientMatchResult(outcomeId, winnerTeamId);
        }

        /// <summary>单行身份摘要（告警里避免对临时 FString 取地址这类问题）。</summary>
        private string GetNameSafe()
        {
            return "PMR3Player(NetId=" + NetId + " uid=" + _uid + ")";
        }

        /// <summary>
        /// 把生成物的私有 OnRep 分发表转发成 public。
        ///
        /// 与 E2E 夹具同一做法：`PMNet_OnRepDispatch` 是生成物里的 `private static`，
        /// 只有本类的 partial 能访问；而 <see cref="PMR3Runtime.Attach"/> 需要把它注册进
        /// 复制通道的 OnRep 分发表。转发本身不改变任何行为。
        /// </summary>
        public static void PMR3DispatchOnRep(PMNetObject target, ushort onRepMethodId)
        {
            PMNet_OnRepDispatch(target, onRepMethodId);
        }
    }
}
