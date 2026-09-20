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
// 本类型刻意只有「纯字段 + 两个 RPC」，不含任何业务状态机：
//   - 复制：`_uid` / `_probeCount`（Push 模型，赋值必须走生成物提供的 PMNet_Set_* 访问器）；
//   - 上行：`ServerProbe(int nonce)`（Reliable + ForceValidate，项目红线）；
//   - 下行：`ClientEcho(int nonce)`（Reliable）。
// 所有收发都经生成的调用桩（`PMNet_ServerProbe` / `PMNet_ClientEcho`），
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
    /// R3-B 的最小可复制网络对象：一个玩家副本 + 一个「探针-回声」往返。
    ///
    /// 生命周期：由服务端 <see cref="PMR3Runtime.SpawnPlayer"/> 唯一创建（Authority + 唯一 owner），
    /// 客户端经生命周期消息收到副本后触发 <see cref="OnReplicatedCreate"/>，
    /// 由宿主（客户端会话宿主）据此发一次 <c>PMNet_ServerProbe</c>。
    /// </summary>
    [PMNetworkObject]
    public partial class PMR3Player : PMNetObject
    {
        // ---------------------------------------------------------------- 复制属性（纯字段）

        /// <summary>该副本的账号 uid。**权威来源是 DS 侧票据验签后的名册身份**，不从业务包自报 uid 采纳。</summary>
        [PMReplicated]
        private int _uid;

        /// <summary>该副本被上行探针确认执行的次数（服务端计数，复制回流给客户端观察收敛）。</summary>
        [PMReplicated]
        private int _probeCount;

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
        private byte[] _movementSnapshotV1;

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
        /// 生成物的 `PMNet_Set_*` 访问器是唯一合法写入点，本方法只是给它一个**面向业务的名字**，
        /// 避免调用方直接依赖生成物的命名细节。
        /// </summary>
        public void PublishMovementSnapshot(byte[] payload)
        {
            PMNet_Set_movementSnapshotV1(payload);
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
        /// 实现体只做两件事：把 ProbeCount 加一（必须经生成的 <c>PMNet_Set_probeCount</c> 标脏，
        /// 否则复制层看不到变化），然后把 nonce 经生成的调用桩回声给拥有者。
        /// **不许手写业务状态包**：往返全部走生成桩与复制通道。
        /// </summary>
        [PMServerRpc(Reliability = PMRpcReliability.Reliable, Validator = PMRpcValidator.ForceValidate)]
        public void ServerProbe(int nonce)
        {
            PMNet_Set_probeCount(_probeCount + 1);

            // 经生成桩下行（`ClientEcho` 直接调用只会本地执行、不过网）。
            PMNet_ClientEcho(nonce);
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
