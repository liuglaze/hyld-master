namespace PMNet.Transport
{
    /// <summary>
    /// 顺序域（决策 D-R0-05）。
    ///
    /// 为什么是"少量固定域"而不是 UE 的 per-actor channel：
    /// per-actor channel 的复杂度主要服务于"每个 Actor 独立的属性复制流"；
    /// 本项目把状态集中在 Replication 域，事件集中在 Reliable 域，
    /// 因此少量固定域即可表达全部语义，而实现与排查成本低一个量级。
    ///
    /// 已知代价（有意接受，见 net-r0-contract.md §4）：同一连接上，
    /// 一个丢包会阻塞该域中它后面的全部可靠消息（队头阻塞）。
    /// 缓解方式是把高频状态放进 Replication 域——它不做重传，丢包由后续更新收敛。
    /// </summary>
    public enum PMStream : byte
    {
        /// <summary>可靠事件与对象生命周期（Create/Destroy/RPC）。保序、去重、NAK 重传。</summary>
        Reliable = 0,

        /// <summary>不可靠的一次性数据（无序号、不去重、不重传）。</summary>
        Unreliable = 1,

        /// <summary>复制状态（携带版本，丢包由后续更新收敛，**不重传旧值**）。</summary>
        Replication = 2,

        /// <summary>域数量（内部使用）。</summary>
        Count = 3,
    }

    /// <summary>断开原因（对应 UE 的 ENetCloseResult 的部分语义）。</summary>
    public enum PMDisconnectReason : byte
    {
        None = 0,

        /// <summary>对端主动断开。</summary>
        PeerClosed = 1,

        /// <summary>本地主动断开。</summary>
        LocalClosed = 2,

        /// <summary>可靠**发送**缓冲溢出 → 断连（D-R0-07，对应 ReliableBufferOverflow）。</summary>
        ReliableBufferOverflow = 3,

        /// <summary>可靠**接收**乱序缓冲溢出 → 断连（D-R0-07，对应 MaxReliableExceeded）。</summary>
        MaxReliableExceeded = 4,

        /// <summary>会话世代不符（旧连接的迟到包）。</summary>
        SessionMismatch = 5,

        /// <summary>握手/心跳超时。</summary>
        Timeout = 6,

        /// <summary>报文协议损坏（版本不符、长度越界等）。</summary>
        ProtocolError = 7,

        /// <summary>
        /// 发送侧：单条消息超过**线格式**可表达的上限（分片头用 `ushort` 写总长 ⇒ 65535），
        /// 由 <see cref="PMTransport.Send"/> 显式拒绝。
        ///
        /// 它与 <see cref="ReliableBufferOverflow"/> 是**两件不同的事**：后者是可靠窗口满了，
        /// 前者是这条消息**根本没法被这套线格式表达**。历史实现把两者混为一谈，
        /// 于是「分片数超限」被误标成「可靠窗溢出」，真实原因被掩盖。
        /// </summary>
        MessageTooLarge = 8,

        /// <summary>发送侧：分片数超过 <see cref="PMTransportConfig.MaxFragmentsPerMessage"/>（显式拒绝，不无界切分）。</summary>
        FragmentLimitExceeded = 9,

        /// <summary>
        /// 应用层：一条**生命周期批次**（Create/Destroy）无法发出。
        ///
        /// 世界的 `BuildLifecycleBatch` 在返回批次前就已清空待发表，所以这类批次发不出去
        /// 就没有第二次机会 —— 对端永远拿不到 Create，后续复制 Update 会在那边被当成未知对象
        /// 丢弃、指向这些对象的 RPC 会被 UnknownTarget 拒，且**没有自愈路径**。
        /// 因此本层选择显式断连（而不是记一条告警后继续跑），把「永久不一致」换成
        /// 「显式失败 + 重连后由世界的 AddConnection 重新排队全量 Create」。
        /// </summary>
        LifecycleBatchOversize = 10,
    }

    /// <summary>
    /// <see cref="PMTransport.Send"/> 最近一次被拒绝的原因（诊断；成功或未调用过时为 None）。
    ///
    /// 为什么必须有它："发送失败"至少有四种互不相同的原因（参数窗口非法 / 超过线格式上限 /
    /// 分片数超限 / 可靠发送窗溢出），而它们的**处置**完全不同 —— 只有最后一种按 D-R0-07 必须断连，
    /// 前三种是调用方的错。没有这个出口，上层只能瞎猜，历史上就把分片超限误标成了可靠窗溢出。
    /// </summary>
    public enum PMSendRejectReason : byte
    {
        /// <summary>没有失败（或尚未调用过 Send）。</summary>
        None = 0,

        /// <summary>连接已断开。</summary>
        Disconnected = 1,

        /// <summary>参数窗口非法（payload 为 null / offset 或 count 为负 / 越出 payload 长度）。</summary>
        InvalidWindow = 2,

        /// <summary>消息超过 <see cref="PMTransport.MaxTransportableMessageBytes"/>（超过即显式拒绝，绝不 ushort 截断）。</summary>
        MessageTooLarge = 3,

        /// <summary>分片数超过 <see cref="PMTransportConfig.MaxFragmentsPerMessage"/>。</summary>
        FragmentLimitExceeded = 4,

        /// <summary>可靠发送缓冲溢出（此时传输层已按 D-R0-07 断连）。</summary>
        ReliableBufferOverflow = 5,
    }

    /// <summary>
    /// 传输层配置。所有上限都必须有界（R0 契约 D-R0-18）。
    /// 具体取值在 R1 收敛后回写契约 §9。
    /// </summary>
    public sealed class PMTransportConfig
    {
        /// <summary>单个数据报的最大字节数（含头）。默认 1200，避开常见路径 MTU。</summary>
        public int MaxDatagramBytes = 1200;

        /// <summary>单条消息分片后的最大片数。超过即拒绝发送（宁可失败也不无界切分）。</summary>
        public int MaxFragmentsPerMessage = 64;

        /// <summary>可靠发送侧未确认消息上限。超限 → 断连（D-R0-07）。</summary>
        public int ReliableSendWindow = 512;

        /// <summary>可靠接收侧乱序等待上限。超限 → 断连（D-R0-07）。</summary>
        public int ReliableRecvWindow = 512;

        /// <summary>重组中的消息上限（按条）。超限丢弃最旧（不崩、不无界）。</summary>
        public int ReassemblyCapacity = 64;

        /// <summary>重组条目的存活上限（毫秒），避免对端永久不发剩余片导致泄漏。</summary>
        public long ReassemblyTtlMs = 5000L;

        /// <summary>
        /// 无入站流量多久判定超时（毫秒）。0 = 不做超时判定（测试用）。
        ///
        /// 判据是「距最后一次**收到任何数据报**」与「距本连接**第一次被 Update 驱动**」
        /// （后者覆盖「一个字节都还没收到」的初始静默期 —— 否则对端只建链不发任何东西时
        /// 这条连接会永生，既占资源也让对端的上行丢失无从察觉）。
        /// </summary>
        public long IdleTimeoutMs = 10000L;

        /// <summary>
        /// 周期性 keepalive（控制 <c>ControlPing</c> 帧）的发送间隔（毫秒）。0 = 禁用（测试用）。
        ///
        /// 为什么必须有它（本轮修的真实缺口，见 `Docs/plans/_r3b_transport_liveness.md`）：
        ///   1. 重传完全由 ack/NAK 驱动，而 ack/NAK 又只能搭车在本端的出站流量上。
        ///      若双方都没有业务要发（静默期），本端**第一个**可靠数据报一旦丢失，
        ///      接收端看不到缺口（它把收到的第一个包当基线）、发送端也拿不到 ack 水位，
        ///      于是既没有 NAK 也没有推断 —— 这条可靠消息只能等到空闲超时断连才被发现。
        ///   2. 接收侧的 <see cref="IdleTimeoutMs"/> 是「没收到任何数据报」的看门狗；
        ///      业务停摆时若没有 keepalive，一条**完全健康**的连接会被自己判成超时。
        ///
        /// 语义边界（有意为之，不要改成「收到 ping 就回 ping」）：
        ///   - 只在 <see cref="PMTransport.Update(long, int)"/> 里按周期判定，不是定时器线程；
        ///   - 计入同一份发送预算（`MaxDatagramsPerUpdate`），**不额外绕过**背压；
        ///   - 到期时优先与当轮已有的业务包**并进同一个数据报**，不额外多占一个包；
        ///   - 收到 ping **不**回 ping：只走既有的 ack 反馈（`_ackDirty`），因此不存在 ping 风暴。
        ///
        /// 线格式未变：<c>ControlPing</c> 早就在既有协议里（此前只有解析分支，没有发送方）。
        /// </summary>
        public long KeepAliveIntervalMs = 1000L;

        /// <summary>
        /// 单个 Update 内最多发出的数据报数（背压）。
        ///
        /// 语义边界：这是**传输层自己的**单次调用上限。调用方可以用
        /// <see cref="PMTransport.Update(long, int)"/> 传一个**更小**的预算（例如协调者要在
        /// 一帧内两次驱动同一条连接、两次共享同一个帧预算时）；传更大的值不会突破本上限。
        /// </summary>
        public int MaxDatagramsPerUpdate = 32;
    }

    /// <summary>运行期计数（对应 UE 的 NetDriver 统计；诊断与测试断言用）。</summary>
    public struct PMTransportStats
    {
        public long DatagramsSent;
        public long DatagramsReceived;
        public long BytesSent;
        public long BytesReceived;

        public long ReliableSent;
        public long ReliableResent;
        public long ReliableDelivered;
        public long ReliableDuplicates;
        public long UnreliableSent;
        public long UnreliableReceived;

        public long NaksSent;
        public long NaksReceived;
        public long AcksSent;

        /// <summary>发出的周期 keepalive（控制 Ping）数据报条数。</summary>
        public long KeepAlivesSent;

        /// <summary>收到的控制 Ping 帧条数（对端 keepalive；只用于诊断，不触发回 ping）。</summary>
        public long KeepAlivesReceived;

        public long FragmentsSent;
        public long FragmentsReceived;
        public long Reassembled;
        public long ReassemblyDropped;

        public long ParseErrors;
        public long DroppedStaleSession;
        public long DroppedUnknownStream;

        /// <summary>发送侧因**参数窗口非法**被拒绝的次数（调用方的错，不是网络问题）。</summary>
        public long SendRejectedWindow;

        /// <summary>发送侧因**超过线格式上限**被拒绝的次数（见 <see cref="PMTransport.MaxTransportableMessageBytes"/>）。</summary>
        public long SendRejectedOversize;

        /// <summary>发送侧因**分片数超限**被拒绝的次数（与可靠窗溢出区分开）。</summary>
        public long SendRejectedFragmentLimit;

        public override string ToString()
        {
            return "tx=" + DatagramsSent + " rx=" + DatagramsReceived
                   + " relSent=" + ReliableSent + " relResent=" + ReliableResent
                   + " relDelivered=" + ReliableDelivered + " relDup=" + ReliableDuplicates
                   + " nakTx=" + NaksSent + " nakRx=" + NaksReceived
                   + " kaTx=" + KeepAlivesSent + " kaRx=" + KeepAlivesReceived
                   + " fragSent=" + FragmentsSent + " fragRx=" + FragmentsReceived
                   + " reassembled=" + Reassembled + " reassemblyDropped=" + ReassemblyDropped
                   + " parseErr=" + ParseErrors + " staleSession=" + DroppedStaleSession
                   + " sendRej=" + (SendRejectedWindow + SendRejectedOversize + SendRejectedFragmentLimit)
                   + "(win=" + SendRejectedWindow + ",over=" + SendRejectedOversize
                   + ",frag=" + SendRejectedFragmentLimit + ")";
        }
    }

    /// <summary>
    /// 字节层链路（UDP socket 或测试用的可控链路）。
    ///
    /// 把 socket 抽出来是为了：① 传输层不依赖 UnityEngine/Socket，可离线测试；
    /// ② 测试可以注入丢包、乱序、重复、延迟。
    /// </summary>
    public interface IPMTransportLink
    {
        /// <summary>发送一个数据报。失败返回 false（不得抛出——发送失败不该废掉一局）。</summary>
        bool Send(byte[] buffer, int offset, int count);

        /// <summary>本地端点描述（诊断用，可为空）。</summary>
        string Describe();
    }

    /// <summary>
    /// 上层的入站回调。
    ///
    /// 线程约束：**只在主线程被调用**。传输层的接收侧只做解析与入队，
    /// 真正的分发由主线程的 Update 驱动（P3'-1 已确立的纪律）。
    /// </summary>
    public interface IPMTransportSink
    {
        /// <summary>收到一条完整消息（分片已重组）。payload 的所有权归调用方本次调用，不得缓存引用。</summary>
        void OnTransportMessage(PMStream stream, byte[] payload, int offset, int count);

        /// <summary>连接建立（握手完成）。</summary>
        void OnTransportConnected();

        /// <summary>连接断开。reason 给出原因，便于上层决定是否重连。</summary>
        void OnTransportDisconnected(PMDisconnectReason reason);

        /// <summary>
        /// 一批数据报被对端确认到达。
        ///
        /// 复制层（M06）用它推进「每连接已确认版本」；传输层本身不使用这个信息。
        /// </summary>
        void OnTransportPacketsAcked(ushort fromPacketId, ushort toPacketId);
    }
}
