using System;
using System.Collections.Generic;
using PMNet.Transport;

namespace PMNet.Session
{
    // =====================================================================================
    //  一、应用信封（net-r3-control-contract.md §4）
    //
    //  「应用信封统一：版本1 + Kind(byte: Lifecycle=1,Rpc=2,Replication=3,ReplicationAck=4)
    //    + 长度限定正文，严格拒绝尾部。」
    //
    //  线格式（全部用 PMNetReader/PMNetWriter 的 varint，与 M04/M06 同源，不引入第二套编码）：
    //
    //      varint version     // 恒为 1；取值 1 时正好占 1 字节，与契约的「版本1」语义一致
    //      varint kind        // 1..4；越界即拒绝（不静默截断成某个已知类型）
    //      body...            // 长度 = 本条消息剩余字节（Transport 已按消息定界）
    //
    //  为什么没有显式的长度字段：M03 已经保证「一次 OnTransportMessage = 一条完整消息」
    //  （分片已重组、数据报已定界），再加一层长度字段只会多一个可被伪造的数字。
    //  「长度限定」由 MaxMessageBytes 上限承担，「严格拒绝尾部」由每个正文解码器自己
    //  消费到末尾（生命周期码与复制码内部都做了 IsAtEnd 检查，RPC 由生成执行器检查）。
    // =====================================================================================

    /// <summary>应用信封的消息类型（契约 §4 的四个 Kind）。</summary>
    public enum PMSessionMessageKind : byte
    {
        /// <summary>对象生命周期（Create / Destroy）。走可靠域。</summary>
        Lifecycle = 1,

        /// <summary>RPC 调用。可靠性按描述符选择顺序域。</summary>
        Rpc = 2,

        /// <summary>复制状态更新。走 Replication 域（不可靠，丢包由后续更新收敛）。</summary>
        Replication = 3,

        /// <summary>复制属性版本确认。走 Replication 域；**不是**底层包 ack。</summary>
        ReplicationAck = 4,
    }

    /// <summary>
    /// 应用层信封的编解码（有界、严格）。
    ///
    /// 与 A1 的控制帧编码（4 字节 LE 长度 + 载荷）**不是同一套**：控制帧属于 Lobby↔DS
    /// 的进程编排协议，本信封属于已认证连接上的世界/复制/RPC 协议。两者刻意不共享
    /// 编解码器，以免「控制面」与「数据面」的演进互相绊住。
    /// </summary>
    public static class PMApplicationEnvelope
    {
        /// <summary>信封版本。</summary>
        public const ulong Version = 1UL;

        /// <summary>单条应用消息的字节上限（契约 §4：64 KiB）。</summary>
        public const int MaxMessageBytes = 64 * 1024;

        /// <summary>
        /// M03 真正能搬运的单条消息上限（<c>ushort.MaxValue</c>）。
        ///
        /// **这是本轮实测出来的既有传输层限制，不是我们的设计选择**：
        /// `PMTransport` 的分片头把「整条消息总长」写成 `ushort`（`PMReliableSendBuffer.Entry.TotalLen`），
        /// 而 <c>PMTransport.Send</c> 切片时直接 `(ushort)count`：
        /// 超过 65535 字节的载荷会在**没有任何报错**的情况下被截断成 `count &amp; 0xFFFF`，
        /// 对端按这个错的总长重组 ⇒ 要么永远重组不出来、要么重组出一份长度对不上的脏数据。
        ///
        /// 于是「契约写 64 KiB（=65536）」与「传输上限 65535」**恰好差 1 字节**。
        /// 本轮不允许改核心，所以由适配层补一道前置闸门（见
        /// <see cref="PMTransportConnection.SendEnvelope"/> 与
        /// <see cref="PMNetSessionBridge.BuildRpcEnvelope"/>）：
        /// 超过本上限时**显式失败**，绝不送给传输层去静默截断。
        /// 已作为「既有 Transport 限制不足」登记到 R3-A 报告，交主 Agent 决定是否在 M03 上开口子。
        /// </summary>
        public const int MaxTransportableBytes = 65535;

        /// <summary>
        /// 适配层实际执行的单条消息上限 = min(契约上限, 传输实际上限)。
        /// 发送侧一律用它判超限（而不是只判 64 KiB），从而不会出现
        /// 「构建通过、传输静默截断」这一夹缝。
        /// </summary>
        public static int EnforceableMaxBytes
        {
            get { return MaxMessageBytes < MaxTransportableBytes ? MaxMessageBytes : MaxTransportableBytes; }
        }

        /// <summary>写出信封头（版本 + 类型）。正文由调用方接着写。</summary>
        public static void WriteHeader(PMNetWriter writer, PMSessionMessageKind kind)
        {
            if (writer == null) { throw new ArgumentNullException("writer"); }

            writer.WriteUInt64(Version);
            writer.WriteUInt64((ulong)kind);
        }

        /// <summary>把一个已有正文包成完整信封。失败（超限）返回 null，不抛。</summary>
        public static byte[] Wrap(PMNetWriter scratch, PMSessionMessageKind kind,
                                 byte[] body, int offset, int count)
        {
            if (scratch == null) { throw new ArgumentNullException("scratch"); }
            if (body == null || offset < 0 || count < 0 || offset + count > body.Length)
            {
                return null;
            }

            scratch.Reset();
            WriteHeader(scratch, kind);
            if (count > 0)
            {
                scratch.WriteRawBytes(body, offset, count);
            }

            if (scratch.Length > MaxMessageBytes)
            {
                // 超限**显式失败**（由调用方决定是断连还是丢弃），绝不静默切一份出来。
                return null;
            }

            return scratch.ToArray();
        }

        /// <summary>解析信封头。失败时 error 给出原因，调用方据此判协议错误。</summary>
        public static bool TryRead(byte[] payload, int offset, int count,
                                   out PMSessionMessageKind kind, out int bodyOffset, out int bodyCount,
                                   out string error)
        {
            kind = PMSessionMessageKind.Lifecycle;
            bodyOffset = offset;
            bodyCount = 0;
            error = null;

            if (payload == null)
            {
                error = "载荷为 null";
                return false;
            }

            if (offset < 0 || count < 0 || offset + count > payload.Length)
            {
                error = "载荷窗口越界（offset=" + offset + " count=" + count + " len=" + payload.Length + "）";
                return false;
            }

            if (count > MaxMessageBytes)
            {
                error = "应用消息长度 " + count + " 字节超过上限 " + MaxMessageBytes + " 字节";
                return false;
            }

            PMNetReader reader = new PMNetReader(payload, offset, count);

            ulong version;
            ulong kindRaw;
            try
            {
                version = reader.ReadVarint();
                kindRaw = reader.ReadVarint();
            }
            catch (Exception ex)
            {
                error = "信封头解码失败：" + ex.GetType().Name + " " + ex.Message;
                return false;
            }

            if (version != Version)
            {
                error = "信封版本不符：收到 " + version + "，本端 " + Version + "（D-R0-46：不一致即不兼容）";
                return false;
            }

            if (kindRaw < (ulong)PMSessionMessageKind.Lifecycle
                || kindRaw > (ulong)PMSessionMessageKind.ReplicationAck)
            {
                error = "未知的应用消息类型：" + kindRaw;
                return false;
            }

            kind = (PMSessionMessageKind)kindRaw;
            bodyOffset = offset + reader.Consumed;
            bodyCount = count - reader.Consumed;
            return true;
        }
    }

    /// <summary>对端在本连接上的角色（用于「客户端只接受受信服务器连接 / 服务端只接受已认证玩家连接」）。</summary>
    public enum PMSessionPeerRole : byte
    {
        /// <summary>对端是局内服务器（DS）。客户端侧的连接必须是这一种。</summary>
        Server = 0,

        /// <summary>对端是玩家客户端。服务端侧的连接必须是这一种。</summary>
        Client = 1,
    }

    /// <summary>
    /// 一条已认证连接的身份（**数据**，不是票据）。
    ///
    /// 为什么这里只有字段、没有任何签名/密钥逻辑（契约 §4 明确要求）：
    /// R3-A2 的适配器**不是对外握手服务器**。票据的签发与验证属于 A1 的会话/名册模块，
    /// 端点消费账本属于 R3-B 的连接接受器。这里只承载「A1 验证完成后」的身份结果，
    /// 并由 <see cref="PMTransportConnection.TryActivate"/> 做**一致性**校验
    /// （Epoch 与世界会话一致、双方摘要一致）。绝不在这里再造第二套票据/密钥/防重放。
    ///
    /// 所有权只能来自这里的已认证身份：本适配器**从不**从业务包内自报的 uid 采纳归属。
    /// </summary>
    public sealed class PMSessionIdentity
    {
        /// <summary>连接序号（本进程内唯一，由宿主分配）。</summary>
        public int ConnectionId;

        /// <summary>对端在本连接上的角色。</summary>
        public PMSessionPeerRole PeerRole;

        /// <summary>账号 ID（必须 &gt; 0）。</summary>
        public int Uid;

        /// <summary>本局玩家 ID（必须 &gt; 0）。</summary>
        public int PlayerId;

        /// <summary>队伍 ID（必须 &gt;= 0）。</summary>
        public int TeamId;

        /// <summary>英雄 ID（必须 &gt;= 0）。</summary>
        public int HeroId;

        /// <summary>会话世代（必须非 0，且必须等于世界会话的 Epoch）。</summary>
        public uint Epoch;

        /// <summary>本端协议摘要（应当来自 <see cref="PMNetRegistry.ProtocolHash"/>）。</summary>
        public uint LocalProtocolHash;

        /// <summary>对端声明的协议摘要。与 <see cref="LocalProtocolHash"/> 不一致即拒绝激活。</summary>
        public uint PeerProtocolHash;

        /// <summary>对局标识（诊断/对账用；UTF-8 不超过 128 字节）。</summary>
        public string MatchId;

        /// <summary>DS 实例标识（诊断/对账用；UTF-8 不超过 128 字节）。</summary>
        public string DsId;

        /// <summary>契约 §2 的字段合法性检查。失败给出具体原因。</summary>
        public bool IsWellFormed(out string error)
        {
            error = null;

            if (ConnectionId <= 0) { error = "ConnectionId 必须大于 0"; return false; }
            if (Epoch == 0u) { error = "SessionEpoch 必须非 0"; return false; }
            if (Uid <= 0) { error = "Uid 必须大于 0"; return false; }
            if (PlayerId <= 0) { error = "PlayerId 必须大于 0"; return false; }
            if (TeamId < 0) { error = "TeamId 必须大于等于 0"; return false; }
            if (HeroId < 0) { error = "HeroId 必须大于等于 0"; return false; }

            string idError;
            if (!IsBoundedId(MatchId, "MatchId", out idError)) { error = idError; return false; }
            if (!IsBoundedId(DsId, "DsId", out idError)) { error = idError; return false; }

            return true;
        }

        private static bool IsBoundedId(string value, string what, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(value))
            {
                return true;
            }

            int bytes = System.Text.Encoding.UTF8.GetByteCount(value);
            if (bytes > 128)
            {
                error = what + " 的 UTF-8 长度 " + bytes + " 字节超过 128 字节上限";
                return false;
            }

            return true;
        }

        /// <summary>诊断串。**不包含任何密钥/票据**（契约 §2：绝不日志输出密钥或完整票据）。</summary>
        public string Describe()
        {
            return "conn#" + ConnectionId + " peer=" + PeerRole
                   + " uid=" + Uid + " pid=" + PlayerId + " team=" + TeamId + " hero=" + HeroId
                   + " epoch=" + Epoch + " hash=0x" + LocalProtocolHash.ToString("X8")
                   + "/0x" + PeerProtocolHash.ToString("X8")
                   + (string.IsNullOrEmpty(MatchId) ? "" : " match=" + MatchId);
        }
    }

    /// <summary>
    /// <see cref="PMNet.Transport.PMTransport"/> 到 <see cref="PMNetConnection"/> 的适配器
    /// （R3-A2，契约 §4）。
    ///
    /// ## 它负责什么
    ///   - 把「已认证身份」变成一条可用的 <see cref="PMNetConnection"/>：显式
    ///     <see cref="TryActivate"/> 之前 <see cref="IsReady"/> 恒为 false，且不派发、不发送；
    ///   - 把对端字节交给 <see cref="PMNet.Transport.PMTransport.OnDatagram"/>（只入队）；
    ///   - 把 <see cref="PMNet.Transport.PMTransport"/> 的入站回调（主线程）翻成
    ///     世界 / RPC / 复制三条入口；
    ///   - 出站方向：把三类消息封进应用信封、按可靠性选顺序域；
    ///   - 实现 <see cref="IPMNetRpcDisconnectTarget"/>，让 R2 的「原生校验失败」真的落到
    ///     <see cref="PMNet.Transport.PMTransport.Disconnect(PMNet.Transport.PMDisconnectReason)"/>。
    ///
    /// ## 它**不**负责什么（边界）
    ///   - 票据签发/验证/防重放（A1），端点消费账本（R3-B）；
    ///   - 属性筛选/优先级/带宽（复制层）；
    ///   - RPC 归属与方向的**判定**（M05 的 `PMRpcDispatch` / `PMNetRpcReceive`）。
    ///     本层只做两件 M05 做不到的事：① 把 `ParamLayoutId` 与描述符比对（不一致 ⇒ 协议错误断连）；
    ///     ② 把「ClassId 必须等于当前目标的 ClassId」落到实处。
    ///
    /// ## 线程
    ///   - <see cref="OnDatagram"/> 可从接收线程调用（只入队）；
    ///   - 其余（<see cref="Update"/>、<see cref="TryActivate"/>、出站、断开）全部在主线程。
    ///
    /// ## 与 <see cref="PMNetConnection.IsServerSide"/> 的关系
    ///   该字段按世界角色派生，但**本类内部一律用 `World.IsServer` 判定**，
    ///   禁止用 `IsServerSide` 去推断「世界里我们是不是权威」（契约 §4 的明确要求）。
    /// </summary>
    public sealed class PMTransportConnection : PMNetConnection, IPMTransportSink, IPMNetRpcDisconnectTarget
    {
        // ── 组成 ────────────────────────────────────────────────────────
        private readonly PMNetWorld _world;
        private readonly PMNetSessionBridge _bridge;
        private readonly PMNetWriter _envelopeScratch = new PMNetWriter(1024);
        private readonly PMRepMessage _repInboundCheck = new PMRepMessage();

        /// <summary>是否收到过传输层的「连接建立」回调（诊断；**不等于**已认证）。</summary>
        public bool TransportConnectedNotified { get { return _transportConnectedNotified; } }

        // ── 状态 ────────────────────────────────────────────────────────
        private bool _activated;
        private bool _disconnectHandled;
        private bool _disposed;
        private bool _transportConnectedNotified;

        /// <summary>本连接会话的传输层（公开是为了让宿主/门禁能直接观察它的统计与状态）。</summary>
        public readonly PMTransport Transport;

        /// <summary>已认证身份（数据，由 R3-B 在验证 A1 票据后填入）。</summary>
        public readonly PMSessionIdentity Identity;

        /// <summary>断开原因（本端观察到的那一个）。</summary>
        public PMDisconnectReason DisconnectReason = PMDisconnectReason.None;

        // ── 计数（诊断/门禁断言；非 0 才算「真的跑到了」）────────────────
        /// <summary>信封消息出站成功次数。</summary>
        public long MessagesSent;

        /// <summary>因未就绪（未激活/已断开）被拒绝的出站次数。</summary>
        public long SendRejectedNotReady;

        /// <summary>
        /// 因超过传输层实际可搬运上限（<see cref="PMApplicationEnvelope.MaxTransportableBytes"/>）
        /// 而被拒绝的出站次数。
        ///
        /// 为什么必须在本层拦：M03 的分片头用 `ushort` 写总长，超限载荷会被**静默截断**
        /// （见 <see cref="PMApplicationEnvelope.MaxTransportableBytes"/> 的说明）。
        /// 宁可在边界上显式失败，也不能让一条可靠消息变成对端永远重组不出来的碎片。
        /// </summary>
        public long SendRejectedTransportCeiling;

        /// <summary>传输层拒绝发送（缓冲溢出/超限/已断开）的次数。</summary>
        public long SendFailed;

        /// <summary>最近一条入站应用消息的字节数（诊断；用来暴露「传输层静默截断」这类既有上限）。</summary>
        public int LastInboundMessageBytes;

        /// <summary>入站消息总数（含被丢弃的）。</summary>
        public long MessagesReceived;

        /// <summary>
        /// 未就绪（未激活/已断开）就收到入站数据报而被丢弃的次数。
        ///
        /// 两个丢弃点共用一个计数：
        ///   ① <see cref="OnDatagram"/> 的纵深闸门（R3-B）：**未进 Transport**，因此不会建序号、不会 ack；
        ///   ② <see cref="HandleInboundMessage"/> 的「未激活不派发」（R3-A 原有语义）。
        /// 区分二者靠传输层统计：① 时 <c>Transport.Stats.DatagramsReceived</c> 与 <c>AcksSent</c> **都不变**。
        /// </summary>
        public long InboundDroppedNotActivated;

        /// <summary>应用层协议错误次数（信封非法/ClassId 不符/布局不符/内部类型不符/超限）。</summary>
        public long ProtocolErrors;

        /// <summary>业务实现抛出的非协议异常次数（**不吞**：向上传播，只计数）。</summary>
        public long BusinessExceptions;

        /// <summary>入站生命周期消息次数。</summary>
        public long LifecycleInbound;

        /// <summary>入站 RPC 被接收入口接收（Applied）的次数。</summary>
        public long RpcApplied;

        /// <summary>入站 RPC 被拒（含 Malformed / ValidationFailed）的次数。</summary>
        public long RpcRejected;

        /// <summary>入站复制消息（Update / Ack）交给复制层的次数。</summary>
        public long ReplicationInbound;

        /// <summary>
        /// 因**方向非法**被拒的入站复制消息次数（服务端收到 Update / 客户端收到 Ack）。
        ///
        /// 为什么必须有这道闸门：复制层的 `OnMessage` 本身**没有任何角色判断** ——
        /// 只要双方 ProtocolHash 一致（激活的前提），客户端就能用同名描述符伪造一条格式完全合法的
        /// Update，命中服务端世界里同 NetId 的权威对象并写入它的属性（还会被服务端当成已确认回 ACK）。
        /// 「连接级白名单」拦不住这个：它只区分连接是谁，不区分消息该往哪个方向走。
        /// </summary>
        public long ReplicationDirectionRejected;

        /// <summary>出站生命周期消息次数。</summary>
        public long LifecycleSent;

        /// <summary>出站复制属性确认消息次数。</summary>
        public long ReplicationAckSent;

        /// <summary>本端主动发起断开的次数。</summary>
        public long DisconnectsInitiated;

        /// <summary>因 RPC 原生校验失败而请求断连的次数（契约 §4 / D-R0-45）。</summary>
        public long RpcDisconnectRequests;

        /// <summary>
        /// 传输层入站解析抛出异常（非业务异常）而被收敛的次数。
        ///
        /// 为什么要计：`PMTransport.ProcessDatagram` 的消息循环对部分读写**没有长度守卫**
        /// （例如读完 `flags` 就直接读 `streamRaw`），一条长度处于特定区间的畸形数据报会在其中
        /// 抛 `IndexOutOfRangeException`。`DrainInbound` 不捕异常，所以它会沿
        /// `Transport.Update → 本类 Update → 桥 Update → 宿主 Pump` 一路冒泡，
        /// **中断同一帧所有端点的收包/握手/flush 与复制 Tick**。
        /// 本层把它收敛成「本连接协议错误 ⇒ 断连」，使故障域严格限制在一条连接内
        /// （核心 M03 的长度守卫另属其写边界，本层是纵深兜底，不是替代）。
        /// </summary>
        public long TransportUpdateFaults;

        /// <summary>因**传输层已经断开**而拒绝激活的次数（防止登记出无法自动摘除的僵尸连接）。</summary>
        public long RejectedInactiveTransport;

        /// <summary>传输层上报「数据报已被对端 ack」的通知次数（**不用于推进复制基线**，见回调注释）。</summary>
        public long TransportAckNotifications;

        /// <summary>最后被确认的数据报号（诊断）。</summary>
        public ushort LastAckedPacketId;

        /// <summary>本连接累计收到的未激活入站丢弃（转发 <c>Transport.DroppedInbound</c> 的口径见测试）。</summary>
        public long InboundQueueDropped { get { return Transport != null ? Transport.DroppedInbound : 0L; } }

        /// <summary>
        /// 本帧的数据报预算快照（由 <see cref="PMNetSessionBridge.Update"/> 在帧首写入）。
        ///
        /// 为什么要它：桥每帧要调用两次 <c>PMTransport.Update</c>（一次 drain、一次 flush），
        /// 而 M03 的 `MaxDatagramsPerUpdate` 只约束**单次** FlushOutbound。
        /// 两次各自按自己的上限发，帧内总量就是 2×上限 − 1（实测到过 63 个数据报）。
        /// 所以帧首记下起点与**本帧总预算**，两次调用都把
        /// <see cref="RemainingFrameDatagramBudget"/> 传给传输层，共享同一个余量。
        /// </summary>
        public long FrameDatagramStart;

        /// <summary>本帧允许发出的数据报总数（帧首由桥写入）。</summary>
        public int FrameDatagramBudget;

        /// <summary>帧首重置本帧预算（起点 + 总量）。</summary>
        internal void BeginFrameDatagramBudget(int budget)
        {
            FrameDatagramStart = Transport.Stats.DatagramsSent;
            FrameDatagramBudget = budget < 0 ? 0 : budget;
        }

        /// <summary>
        /// 本帧还没用掉的发送预算 = 帧总预算 − 本帧已发数据报数。
        ///
        /// 帧首与帧尾两次 <c>Update</c> 都用它：帧首天然得到完整预算，帧尾得到扣除后的**余量**，
        /// 因此「帧首 s + 帧尾 x」严格满足 s + x <= 帧总预算，而不是各自 32。
        /// 超预算时把 flush 顺延到下一帧：消息留在传输层的（有界）队列里，**可靠消息不丢**。
        /// 未在本帧帧首登记过的连接（帧中途新建）返回 0：宁肯晚一帧发，也不越预算。
        /// </summary>
        public int RemainingFrameDatagramBudget
        {
            get
            {
                long sent = Transport.Stats.DatagramsSent - FrameDatagramStart;
                if (sent < 0L) { sent = 0L; }

                long remaining = (long)FrameDatagramBudget - sent;
                if (remaining <= 0L) { return 0; }
                return remaining > int.MaxValue ? int.MaxValue : (int)remaining;
            }
        }

        public PMTransportConnection(PMSessionIdentity identity, PMNetWorld world, PMNetSessionBridge bridge,
                                     IPMTransportLink link, PMTransportConfig config = null)
            : base(identity != null ? identity.ConnectionId : 0,
                   world != null && world.IsServer)
        {
            if (identity == null) { throw new ArgumentNullException("identity"); }
            if (world == null) { throw new ArgumentNullException("world"); }
            if (bridge == null) { throw new ArgumentNullException("bridge"); }
            if (link == null) { throw new ArgumentNullException("link"); }

            if (world != bridge.World)
            {
                throw new ArgumentException("连接的世界与桥的世界不是同一个实例", "world");
            }

            Identity = identity;
            _world = world;
            _bridge = bridge;

            // 保持既有初值：MaxDatagramBytes=1200 / MaxFragmentsPerMessage=64（契约 §4）。
            Transport = new PMTransport(identity.Epoch, config ?? new PMTransportConfig(), link, this);
        }

        /// <summary>是否已完成显式激活（且传输层仍然连通）。</summary>
        public override bool IsReady { get { return _activated && Transport.IsConnected; } }

        /// <summary>是否已完成显式激活（不含传输层连通性）。</summary>
        public bool IsActivated { get { return _activated; } }

        /// <summary>本连接归属的桥（每世界一个协调者）。</summary>
        public PMNetSessionBridge Bridge { get { return _bridge; } }

        /// <summary>本连接所属世界。</summary>
        public PMNetWorld World { get { return _world; } }

        /// <summary>诊断名。</summary>
        public string Name { get { return "conn#" + ConnectionId; } }

        // =================================================================================
        //  激活
        // =================================================================================

        /// <summary>
        /// 显式激活（契约 §4）。**R3-B 必须在验证 A1 票据并绑定真实端点之后调用它**。
        ///
        /// 校验（任一不通过即拒绝激活并断开，幂等）：
        ///   1. 身份字段合法（Epoch 非 0、Uid/PlayerId &gt; 0…）；
        ///   2. `Identity.Epoch == World.Session.Epoch`（世代必须与世界会话一致）；
        ///   3. `Identity.PeerProtocolHash == Identity.LocalProtocolHash`（双方摘要一致）；
        ///   4. 本地摘要与 `PMNetRegistry.ProtocolHash` 一致（防止出现「第二套摘要来源」）；
        ///   5. 世界/桥侧的连接接受规则（客户端只接受受信服务器连接、服务端只接受
        ///      已认证且 uid 不重复的玩家连接）。
        ///
        /// 注意：本方法**不是**对外安全握手，也不做票据验证与防重放 —— 它只保证
        /// 「进入世界/复制的连接，其身份已经过一致性校验且已被宿主认证过」。
        /// </summary>
        public bool TryActivate(out string error)
        {
            error = null;

            if (_disposed) { error = "连接已释放"; return false; }

            // 「激活成功 ⇒ IsReady 为真」是本类必须自己守住的不变量。
            // IsReady = 已激活 && 传输连通，所以**死链路上绝不能激活**：
            // 一旦登记进世界/复制，而断开回调（_disconnectHandled）早已跑完（或者根本不会再触发，
            // 因为传输层的 Disconnect 是幂等的），这条连接就没有任何对手能把它摘除 ——
            //   客户端侧：它会把唯一受信服务器槽位占死，后续合法服务器连接全被拒；
            //   服务端侧：它会把 uid 永久占用（同一玩家的重连被拒，本意是防顶号）；
            //   世界侧：它还长期挂一份永远发不出去的 Create 待发队列（FlushLifecycle 的前置是 IsReady）。
            // 宁可在入口拒绝，并让宿主自行决定是否重连。
            if (Transport == null || !Transport.IsConnected)
            {
                RejectedInactiveTransport++;
                error = "传输层已断开，拒绝在死链路上激活（激活成功必须满足 IsReady）";
                return false;
            }

            if (_activated) { return true; }

            string identityError;
            if (!Identity.IsWellFormed(out identityError))
            {
                RejectActivation(PMDisconnectReason.ProtocolError);
                error = "身份字段非法：" + identityError;
                return false;
            }

            if (_world.Session == null)
            {
                RejectActivation(PMDisconnectReason.ProtocolError);
                error = "世界没有会话（无法校验世代）";
                return false;
            }

            if (Identity.Epoch != _world.Session.Epoch)
            {
                RejectActivation(PMDisconnectReason.SessionMismatch);
                error = "会话世代不符：身份 " + Identity.Epoch + "，世界 " + _world.Session.Epoch;
                return false;
            }

            if (Identity.PeerProtocolHash != Identity.LocalProtocolHash)
            {
                RejectActivation(PMDisconnectReason.ProtocolError);
                error = "双方协议摘要不一致：本端 0x" + Identity.LocalProtocolHash.ToString("X8")
                        + "，对端 0x" + Identity.PeerProtocolHash.ToString("X8");
                return false;
            }

            if (PMNetRegistry.IsSealed && PMNetRegistry.ProtocolHash != Identity.LocalProtocolHash)
            {
                // 摘要必须来自真实注册表；否则「摘要一致」只是一句自说自话。
                RejectActivation(PMDisconnectReason.ProtocolError);
                error = "本端摘要 0x" + Identity.LocalProtocolHash.ToString("X8")
                        + " 与注册表的 0x" + PMNetRegistry.ProtocolHash.ToString("X8")
                        + " 不一致（不接受第二套摘要来源）";
                return false;
            }

            string registerError;

            // 先把本连接标为已激活，再交给桥做世界/复制的成对登记：
            // 桥的接受规则里明确要求「只接受已激活的连接」（否则任何未认证连接都能进世界），
            // 因此这里必须让标记先于登记；登记失败则立刻回退，不留「半激活」态。
            _activated = true;
            if (!_bridge.RegisterConnection(this, out registerError))
            {
                _activated = false;
                RejectActivation(PMDisconnectReason.ProtocolError);
                error = "连接未被世界/桥接受：" + registerError;
                return false;
            }

            return true;
        }

        private void RejectActivation(PMDisconnectReason reason)
        {
            Warn("[" + Name + "] 激活被拒绝（" + reason + "）：" + Identity.Describe());
            Disconnect(reason);
        }

        // =================================================================================
        //  出站
        // =================================================================================

        /// <summary>
        /// <see cref="PMNetConnection.Send"/> 的唯一核心调用方是
        /// `PMReplicationChannel.Tick()`（复制状态更新，永远不可靠、不重传）。
        ///
        /// 因此本适配器把「裸 Send」解释为复制域消息；可靠 RPC / 生命周期 / 复制确认
        /// 都有专用入口——**因为可靠性一个维度不足以区分顺序域**（RPC 的不可靠调用与
        /// 复制更新都写 Unreliable，但必须落在不同的域上）。
        /// </summary>
        public override void Send(byte[] payload, PMRpcReliability reliability)
        {
            byte[] envelope = PMApplicationEnvelope.Wrap(_envelopeScratch, PMSessionMessageKind.Replication,
                                                         payload, 0, payload != null ? payload.Length : 0);
            if (envelope == null)
            {
                SendFailed++;
                Warn("[" + Name + "] 复制更新超限，被拒绝发送（不静默丢弃）");
                return;
            }

            SendEnvelope(envelope, PMStream.Replication, false);
        }

        /// <summary>发送一条已经封好信封的 RPC 消息（由 <see cref="PMNetSessionBridge.SendRpc"/> 调用）。</summary>
        internal bool SendRpcEnvelope(byte[] envelope, bool reliable)
        {
            return SendEnvelope(envelope, reliable ? PMStream.Reliable : PMStream.Unreliable, reliable);
        }

        /// <summary>发送一条已经封好信封的生命周期消息（Create/Destroy，与可靠 RPC 同域）。</summary>
        internal bool SendLifecycleEnvelope(byte[] envelope)
        {
            return SendEnvelope(envelope, PMStream.Reliable, true);
        }

        /// <summary>发送一条已经封好信封的复制属性确认（Replication 域；与底层包 ack 无关）。</summary>
        internal bool SendReplicationAckEnvelope(byte[] envelope)
        {
            return SendEnvelope(envelope, PMStream.Replication, false);
        }

        private bool SendEnvelope(byte[] envelope, PMStream stream, bool reliable)
        {
            if (envelope == null)
            {
                return false;
            }

            if (!IsReady)
            {
                // 「未完成显式激活前不发送」：这条闸门就是它的实现点。
                SendRejectedNotReady++;
                return false;
            }

            if (envelope.Length > PMApplicationEnvelope.EnforceableMaxBytes)
            {
                // 两道上限合一：契约的 64 KiB 与 M03 的 ushort 总长上限（65535）。
                // 只有**两者都过**才交给传输层；否则显式失败（不允许静默截断）。
                if (envelope.Length > PMApplicationEnvelope.MaxTransportableBytes)
                {
                    SendRejectedTransportCeiling++;
                    Warn("[" + Name + "] 应用消息 " + envelope.Length + " 字节超过传输层实际上限 "
                         + PMApplicationEnvelope.MaxTransportableBytes + " 字节（M03 用 ushort 写总长），"
                         + "拒绝发送以免被静默截断");
                }
                else
                {
                    SendFailed++;
                    Warn("[" + Name + "] 应用消息 " + envelope.Length + " 字节超过契约上限 "
                         + PMApplicationEnvelope.MaxMessageBytes + " 字节");
                }

                if (reliable) { Disconnect(PMDisconnectReason.ProtocolError); }
                return false;
            }

            bool ok = Transport.Send(stream, reliable, envelope, 0, envelope.Length);
            if (ok)
            {
                MessagesSent++;
                return true;
            }

            // 传输层拒绝：原因由传输层给出（参数窗口/超上限/分片超限/可靠窗溢出）。
            // 区分原因很重要：只有**可靠窗溢出**才是 D-R0-07 的“必须断连”，其余是调用方/上限问题，
            // 历史实现把它们一律标成 ReliableBufferOverflow，把真实原因盖掉了。
            SendFailed++;
            PMSendRejectReason reject = Transport.LastSendRejectReason;
            Warn("[" + Name + "] 传输层拒绝发送（stream=" + stream + " reliable=" + reliable
                 + " bytes=" + envelope.Length + " reason=" + reject + "）");

            if (reliable && Transport.IsConnected)
            {
                // 可靠消息不允许静默丢失（契约 §4）：发不出去就断连，由上层重连重来。
                PMDisconnectReason why;
                if (reject == PMSendRejectReason.FragmentLimitExceeded) { why = PMDisconnectReason.FragmentLimitExceeded; }
                else if (reject == PMSendRejectReason.MessageTooLarge) { why = PMDisconnectReason.MessageTooLarge; }
                else { why = PMDisconnectReason.ReliableBufferOverflow; }
                Disconnect(why);
            }

            return false;
        }

        // =================================================================================
        //  入站（接收线程只入队；解析/派发都在主线程的 Update 里）
        // =================================================================================

        /// <summary>
        /// 接收线程调用：只把字节交给传输层入队，不做任何解析与派发。
        ///
        /// **纵深闸门（R3-B）**：未就绪（未激活/已断开）时**一个字节都不进 Transport**。
        ///
        /// 为什么必须在传输层之前拦：Transport 收到任何合法数据报都会立刻
        /// `TrackIncomingPacketId` 并把 ack 回给对端，对端据此**退休可靠消息**
        /// （生命周期/RPC/可靠复制），之后才 `DeliverMessage` → 本适配器再因未激活丢弃。
        /// 结果是「应用层什么都没收到、传输层却已经确认收到」—— 没有 NAK、没有重传，
        /// 一条可靠消息就这样静默消失。宿主次序（先握手验票、激活，再放行数据报）是主防线，
        /// 这里是与它**各自充分**的第二道：即使宿主接线出错也不会出现这个可靠性破洞。
        /// </summary>
        public void OnDatagram(byte[] data, int offset, int count)
        {
            if (_disposed)
            {
                return;
            }

            if (!IsReady)
            {
                // 不计数传输层统计、不建序号、不 ack：
                // 等价证据是 Transport.Stats.DatagramsReceived 与 AcksSent 都不变。
                InboundDroppedNotActivated++;
                return;
            }

            Transport.OnDatagram(data, offset, count);
        }

        /// <summary>
        /// 主线程驱动一次：<c>drain 入站 + flush 出站</c>，发送上限用传输层自己的
        /// <see cref="PMNet.Transport.PMTransportConfig.MaxDatagramsPerUpdate"/>。
        ///
        /// 它对应「一次完整驱动」，语义保持原样（宿主手动驱动、门禁直接用）。
        /// 桥走的是带预算的重载 <see cref="Update(long, int)"/>：它一帧要驱动两次，
        /// 两次必须共享同一个帧预算，否则帧内总量会变成 2×上限 − 1。
        /// </summary>
        public void Update(long nowMs)
        {
            if (_disposed)
            {
                return;
            }

            RunTransportUpdate(nowMs, UseTransportOwnBudget);
        }

        /// <summary>
        /// 由 <see cref="PMNetSessionBridge.Update"/> 调用的帧内驱动入口：把「本帧还剩多少数据报预算」
        /// 交给传输层，从而让一帧内的两次驱动（帧首 drain、帧尾 flush）**共享**同一个预算。
        /// 传 0 表示"这次只 drain、不发"。
        /// </summary>
        internal void Update(long nowMs, int maxDatagramsThisFlush)
        {
            if (_disposed)
            {
                return;
            }

            RunTransportUpdate(nowMs, maxDatagramsThisFlush);
        }

        /// <summary>「使用传输层自己的数据报预算」的哨兵值（等价于 <c>Transport.Update(nowMs)</c>）。</summary>
        private const int UseTransportOwnBudget = -1;

        /// <summary>
        /// 传输层驱动的**唯一入口**：把「入站解析抛异常」收敛成本连接的协议错误，
        /// 不让它冒泡出去中断宿主一帧里其它端点的进度。
        ///
        /// 与 R2 既定口径的关系（关键）：业务实现抛出的异常**仍然照旧向上传播**，
        /// 本方法不吞任何业务异常。区分办法是业已存在的计数器：
        /// `HandleInboundMessage` 在向上抛之前一定先 `BusinessExceptions++`，
        /// 因此「异常前后该计数没变」= 异常来自解析路径（而非业务实现）。
        /// </summary>
        private void RunTransportUpdate(long nowMs, int maxDatagramsThisFlush)
        {
            long businessBefore = BusinessExceptions;

            try
            {
                if (maxDatagramsThisFlush < 0)
                {
                    Transport.Update(nowMs);
                }
                else
                {
                    Transport.Update(nowMs, maxDatagramsThisFlush);
                }
            }
            catch (Exception ex)
            {
                if (BusinessExceptions != businessBefore)
                {
                    // 业务异常：不吞，维持 R2 口径（计数已在上层累加）。
                    throw;
                }

                TransportUpdateFaults++;
                ProtocolError("传输层入站解析抛出 " + ex.GetType().Name + "：" + ex.Message
                              + "（单条畸形数据报不得中断宿主 Pump）");
            }
        }

        /// <summary>被 <see cref="PMNetSessionBridge"/> 调用：断开后不再参与任何阶段。</summary>
        internal bool IsOpenForPump { get { return !_disposed && Transport.IsConnected; } }

        // =================================================================================
        //  IPMTransportSink
        // =================================================================================

        void IPMTransportSink.OnTransportConnected()
        {
            // 传输层认为「对端活着」不等于「本连接已认证」。真正的就绪判据是 TryActivate。
            _transportConnectedNotified = true;
        }

        void IPMTransportSink.OnTransportDisconnected(PMDisconnectReason reason)
        {
            HandleDisconnected(reason);
        }

        /// <summary>
        /// 传输层的数据报级 ack（用来退休**可靠消息**）。
        ///
        /// 刻意**不**用它推进复制的「每连接已确认版本」：那是属性版本 ACK 的职责
        /// （契约 §4：「不把底层包ACK当属性版本ACK」）。这里只记账，供诊断与断言。
        /// </summary>
        void IPMTransportSink.OnTransportPacketsAcked(ushort fromPacketId, ushort toPacketId)
        {
            TransportAckNotifications++;
            LastAckedPacketId = toPacketId;
        }

        void IPMTransportSink.OnTransportMessage(PMStream stream, byte[] payload, int offset, int count)
        {
            HandleInboundMessage(stream, payload, offset, count);
        }

        private void HandleInboundMessage(PMStream stream, byte[] payload, int offset, int count)
        {
            MessagesReceived++;
            LastInboundMessageBytes = count;

            if (!IsReady)
            {
                // 「未完成显式激活前不派发」的正向证据：字节进来了也一律丢弃。
                InboundDroppedNotActivated++;
                return;
            }

            if (count > PMApplicationEnvelope.MaxMessageBytes)
            {
                ProtocolError("应用消息长度 " + count + " 字节超过上限 "
                              + PMApplicationEnvelope.MaxMessageBytes + " 字节");
                return;
            }

            PMSessionMessageKind kind;
            int bodyOffset;
            int bodyCount;
            string envelopeError;
            if (!PMApplicationEnvelope.TryRead(payload, offset, count, out kind, out bodyOffset, out bodyCount,
                                               out envelopeError))
            {
                ProtocolError("应用信封非法：" + envelopeError);
                return;
            }

            try
            {
                switch (kind)
                {
                    case PMSessionMessageKind.Lifecycle:
                        HandleLifecycleMessage(payload, bodyOffset, bodyCount);
                        break;

                    case PMSessionMessageKind.Rpc:
                        HandleRpcMessage(payload, bodyOffset, bodyCount);
                        break;

                    case PMSessionMessageKind.Replication:
                        HandleReplicationMessage(PMRepMessageKind.Update, payload, bodyOffset, bodyCount);
                        break;

                    case PMSessionMessageKind.ReplicationAck:
                        HandleReplicationMessage(PMRepMessageKind.Ack, payload, bodyOffset, bodyCount);
                        break;

                    default:
                        ProtocolError("未处理的应用消息类型：" + kind);
                        break;
                }
            }
            catch (FormatException ex)
            {
                ProtocolError("载荷格式非法：" + ex.Message);
            }
            catch (InvalidCastException ex)
            {
                ProtocolError("载荷与目标类型不匹配：" + ex.Message);
            }
            catch (Exception ex)
            {
                // 业务实现自身抛出的异常**不吞**（R2 的既定口径）：把它记成协议错误会让
                // 业务 bug 变成「网络在抽风」。计数后向上传播，由宿主兜底。
                BusinessExceptions++;
                Warn("[" + Name + "] 处理入站消息时抛出非协议异常：" + ex.GetType().Name + " " + ex.Message);
                throw;
            }
        }

        private void HandleLifecycleMessage(byte[] payload, int offset, int count)
        {
            if (_world.IsServer)
            {
                // 客户端专属下行：服务端收到生命周期消息说明对端在伪造方向（或接线错）。
                ProtocolError("服务端收到了生命周期消息（生命周期只允许服务端 → 客户端）");
                return;
            }

            LifecycleInbound++;
            _world.OnLifecycleMessage(payload, offset, count);
        }

        private void HandleRpcMessage(byte[] payload, int offset, int count)
        {
            PMNetReader reader = new PMNetReader(payload, offset, count);

            ulong netIdRaw;
            bool isStatic;
            ulong classIdRaw;
            ulong rpcIdRaw;
            ulong layoutRaw;

            try
            {
                // 用 ReadUInt64 + 范围检查，而不是 ReadUInt32/ReadInt32：
                // 后者会把超范围 varint 静默截断，等于把「畸形包」变成「另一个合法 ID」。
                netIdRaw = reader.ReadUInt64();
                isStatic = reader.ReadBool();
                classIdRaw = reader.ReadUInt64();
                rpcIdRaw = reader.ReadUInt64();
                layoutRaw = reader.ReadUInt64();
            }
            catch (Exception ex)
            {
                ProtocolError("RPC 头解码失败：" + ex.GetType().Name + " " + ex.Message);
                return;
            }

            if (netIdRaw == 0UL || netIdRaw > uint.MaxValue)
            {
                ProtocolError("RPC 目标的 NetId 越界：" + netIdRaw);
                return;
            }

            if (classIdRaw == 0UL || classIdRaw > uint.MaxValue)
            {
                ProtocolError("RPC 的 ClassId 越界：" + classIdRaw);
                return;
            }

            if (rpcIdRaw == 0UL || rpcIdRaw > ushort.MaxValue)
            {
                ProtocolError("RPC 的 RpcId 越界：" + rpcIdRaw);
                return;
            }

            if (layoutRaw > ushort.MaxValue)
            {
                ProtocolError("RPC 的 ParamLayoutId 越界：" + layoutRaw);
                return;
            }

            uint classId = (uint)classIdRaw;
            ushort rpcId = (ushort)rpcIdRaw;
            ushort layoutId = (ushort)layoutRaw;
            PMNetId targetId = new PMNetId((uint)netIdRaw, isStatic);

            // ── ① ClassId 必须与**当前目标**一致（契约 §4）───────────────────
            PMNetObject target;
            if (_world.TryFind(targetId, out target) && target != null)
            {
                if (target.ClassId != classId)
                {
                    ProtocolError("RPC 的 ClassId 与目标对象不一致：包里 " + classId
                                  + "，目标 " + target.ClassId + "（NetId " + targetId + "）");
                    return;
                }

                // ── ② ParamLayoutId 必须与描述符一致（否则协议不兼容，断连）────
                PMNetRpcEntry entry;
                if (PMNetRegistry.TryGetRpc(classId, rpcId, out entry) && entry != null)
                {
                    if (entry.Descriptor.ParamLayoutId != layoutId)
                    {
                        ProtocolError("RPC 参数布局不一致：方法 " + entry.Descriptor.MethodName
                                      + " 本端 0x" + entry.Descriptor.ParamLayoutId.ToString("X4")
                                      + "，远端 0x" + layoutId.ToString("X4") + "（D-R0-46 判定为不兼容）");
                        return;
                    }
                }
            }

            // 目标不存在 / RpcId 未注册 / 方向或归属非法，都由 M05 的入口判定并给出可验收状态。
            PMNetRpcReceiveResult result = PMNetRpcReceive.Deliver(_world, this, rpcId, targetId, reader);

            if (result.Applied)
            {
                RpcApplied++;
            }
            else
            {
                RpcRejected++;
            }
        }

        private void HandleReplicationMessage(PMRepMessageKind expectedKind, byte[] payload, int offset, int count)
        {
            // ── 方向闸门（先判，且只看方向，不看载荷）───────────────────────────
            //
            // 复制层的接收路径（PMReplicationChannel.OnMessage → TryApplyRecord）**不做任何角色判断**：
            // 它只按 NetId 在世界里找到对象，校验“结构 + 值可解码”，然后直接写活对象并回 ACK。
            // 换句话说，只要两端 ProtocolHash 一致（这是激活的前提），一个已认证客户端就能用
            // 同名描述符伪造一条**格式完全合法**的 Update，命中服务端世界里同 NetId 的**权威对象**
            // 并改写它的属性，服务端还会把该版本当成已确认回 ACK、并在服务端派发 OnRep。
            // 「连接级白名单」（客户端只接受受信服务器 / 服务端只接受已认证玩家）拦不住这个 ——
            // 它区分的是「连接是谁」，不是「这条消息该往哪个方向走」。
            //
            // 因此方向在本层钉死，与生命周期方向（服务端收到 Lifecycle 即协议错误）同一纪律：
            //   服务端 = 复制的唯一权威 ⇒ 只接受 ReplicationAck（客户端回传的属性版本确认）；
            //   客户端 = 被同步方     ⇒ 只接受 Replication Update（服务端下发的状态）。
            // 注意：本设计下不存在「客户端自治上行」，所以合法流量永远不会撞上这道闸门
            // （客户端的复制通道没有登记任何对象，Tick 不产生 Update）。
            if (_world.IsServer)
            {
                if (expectedKind != PMRepMessageKind.Ack)
                {
                    ReplicationDirectionRejected++;
                    ProtocolError("服务端只接受复制版本确认（ReplicationAck），收到 " + expectedKind
                                  + "（复制 Update 只允许服务端 → 客户端）");
                    return;
                }
            }
            else
            {
                if (expectedKind != PMRepMessageKind.Update)
                {
                    ReplicationDirectionRejected++;
                    ProtocolError("客户端只接受复制更新（Replication），收到 " + expectedKind
                                  + "（复制版本确认只允许客户端 → 服务端）");
                    return;
                }
            }

            // 只花三次 varint 读头部做「信封类型 ↔ 内部消息类型」一致性检查，
            // 不重复整条解码（真正的解码仍然只发生在复制层里一次）。
            PMNetReader probe = new PMNetReader(payload, offset, count);
            ulong magic;
            ulong version;
            ulong kind;

            try
            {
                magic = probe.ReadVarint();
                version = probe.ReadVarint();
                kind = probe.ReadVarint();
            }
            catch (Exception ex)
            {
                ProtocolError("复制消息头解码失败：" + ex.GetType().Name + " " + ex.Message);
                return;
            }

            if (magic != PMRepProtocol.Magic || version != PMRepProtocol.Version)
            {
                ProtocolError("复制消息魔数/版本不符：magic=" + magic + " version=" + version);
                return;
            }

            if (kind != (ulong)expectedKind)
            {
                ProtocolError("复制信封类型与内部消息类型不一致：信封 " + expectedKind + "，内部 " + kind);
                return;
            }

            ReplicationInbound++;
            _bridge.Replication.OnMessage(this, payload, offset, count);
        }

        // =================================================================================
        //  断开 / 释放
        // =================================================================================

        /// <summary>请求断开（幂等）。这是连接唯一的下行出口。</summary>
        public void Disconnect(PMDisconnectReason reason)
        {
            if (_disposed || Transport == null)
            {
                return;
            }

            if (!Transport.IsConnected)
            {
                return;
            }

            DisconnectsInitiated++;
            Transport.Disconnect(reason);
        }

        /// <summary>
        /// RPC 的原生校验（`_Validate` 返回 false）失败 ⇒ 宿主连接断连
        /// （契约 §4 / D-R0-45 / `_r2_fix_rpc.md` §2.6）。
        ///
        /// 直接委托既有的 <see cref="PMNet.Transport.PMTransport.Disconnect"/>：
        /// 「怎么断、通知谁、按什么次序清场」由传输层决定，RPC 层不另造一套。
        /// 注意 <see cref="PMDisconnectReason.ProtocolError"/> 在既有传输层里**不**发对端通知
        /// （只有 LocalClosed/PeerClosed 会尽力通知一次），因此对端是在自己的空闲超时后才察觉。
        /// </summary>
        public void RequestRpcDisconnect(PMNetObject target, ushort rpcId, string methodName)
        {
            RpcDisconnectRequests++;
            Warn("[" + Name + "] RPC " + methodName + "（id=" + rpcId + "）原生校验失败 ⇒ 断开本连接");
            Disconnect(PMDisconnectReason.ProtocolError);
        }

        private void HandleDisconnected(PMDisconnectReason reason)
        {
            if (_disconnectHandled)
            {
                return;
            }

            _disconnectHandled = true;
            DisconnectReason = reason;

            bool wasActivated = _activated;
            _activated = false;

            if (wasActivated && _bridge != null)
            {
                // 成对退出世界与复制（契约 §4：进入/离开成对、断开清理）。
                _bridge.OnConnectionClosed(this);
            }
        }

        /// <summary>
        /// 释放本连接（幂等，可重复调用）。断开会尽力通知对端一次，再本地清场。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (Transport != null && Transport.IsConnected)
            {
                // LocalClosed 会经传输层回调进 HandleDisconnected → 成对摘除世界/复制登记。
                Transport.Disconnect(PMDisconnectReason.LocalClosed);
            }

            if (_activated)
            {
                // 传输层已经断开却没走过回调（例如 Transport 为 null 的理论情形）时的兜底。
                _activated = false;
                if (_bridge != null)
                {
                    _bridge.OnConnectionClosed(this);
                }
            }
        }

        // =================================================================================
        //  诊断
        // =================================================================================

        /// <summary>日志出口。宿主接到项目日志（客户端 `Logging.Debug.Log`）。</summary>
        private void Warn(string message)
        {
            Action<string> handler = _bridge != null ? _bridge.Warn : null;
            if (handler != null)
            {
                handler(message);
            }
        }

        private void ProtocolError(string detail)
        {
            ProtocolErrors++;
            Warn("[" + Name + "] 应用层协议错误 ⇒ 断开本连接：" + detail);
            Disconnect(PMDisconnectReason.ProtocolError);
        }

        public override string ToString()
        {
            return "PMTransportConnection(" + Name + " ready=" + IsReady + " activated=" + _activated
                   + " tx=" + MessagesSent + " rx=" + MessagesReceived + ")"
                   + (Identity != null ? " " + Identity.Describe() : string.Empty);
        }
    }
}
