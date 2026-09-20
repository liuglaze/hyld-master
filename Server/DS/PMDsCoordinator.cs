using System;
using System.Collections.Generic;
using System.Text;

namespace PMNet.Control
{
    /// <summary>
    /// R3-A1：Lobby 侧的「一局」协调器（M12 的进程编排与状态机）。
    ///
    /// 事实来源：`Docs/plans/net-r3-control-contract.md` §2/§3。
    ///
    /// 状态链（契约 §3 原文，末段状态名已按审查结论修正语义）：
    /// <code>
    /// Allocated -> Starting -> Ready -> Running -> ResultPending -> ResultCommitted -> Exited
    /// 任意非终态可 Failed / TimedOut
    /// </code>
    /// ⚠ 契约写的是 <c>ResultAcked</c>；本次审查确认该名字读起来像「对端已确认」，而协议里
    /// **没有** DS→Lobby 的 ResultAck 确认消息，进入该状态的实际条件只是「己方重发预算用尽」。
    /// 因此实现改用 <see cref="PMDsSessionState.ResultCommitted"/>（本地受理），对端收尾语义由
    /// <see cref="PMDsCoordinator.PeerExitObserved"/> 决定（Exited 消息 / 进程退出），不从重发次数推断。
    /// 只有 <see cref="PMDsSessionState.Idle"/> 是 A1 补充的初始态（尚未分配会话，契约从 Allocated 起算）。
    ///
    /// 设计上刻意做的几件事：
    /// 1. **时钟注入**：所有超时都走 <see cref="IPMDsClock"/> + <see cref="Tick"/>，
    ///    测试不用 sleep（契约 §3「注入时钟可配置，不用测试sleep」）。
    /// 2. **进程注入**：进程起停只经 <see cref="IPMDsProcessLauncher"/>，A1 测试用替身做故障注入，
    ///    因此「启动超时 / 崩溃 / 未就绪退出」都能在毫秒内被确定性验证。
    /// 3. **单入口验签**：唯一的入站入口是 <see cref="OnControlPayload"/>，它内部先解码再验 MAC，
    ///    验签失败**不会**返回任何业务字段，因此不存在「先改状态后验签」的调用姿势。
    /// 4. **就绪不靠日志**：<see cref="PMDsSessionState.Ready"/> 只能由一条通过全部校验的
    ///    Ready 控制帧推进。进程存在、stdout 里出现 "ready" 之类字符串**都不构成就绪证据**。
    /// 5. **结果幂等 + 有界墓碑**：以 (MatchId, Epoch, ResultId) 为幂等键，同 ID 同内容返回同一 Ack，
    ///    同 ID 不同内容拒绝。内容判定用 **winner + summary 的完整字节比对**（哈希只作诊断），
    ///    且墓碑的 Conflict 分支不覆盖已有条目。终态释放端口与名册，但保留短期墓碑（TTL + 条数上限）。
    /// 6. **释放以「进程已退出」为前提**：终态转移、强杀请求、资源释放是三件事，不能合并成
    ///    「调了 Kill 就算释放」。强杀失败会节流重试并写进事件 Reason；未确认退出时端口与名册保持占用
    ///    （由 <see cref="Tick"/> 继续收尾），因此“僵死进程占着共享端口却被新一局拿到”这种串用不会发生。
    ///
    /// A1 **不做**的事（明确交接给 R3-B）：
    /// - 不接匹配入口、不改旧 BattleManage/匹配流程、不碰旧开局路径。
    /// - 不实现端点消费账本（防重放）与「同一票据只能被唯一真实端点消费」——那是 R3-B 的连接接受器。
    /// - 不启动真实 HyldDS（真实启动器已提供，但需 R3-B 提供白名单与引导文件路径）。
    /// - 不承诺跨进程「恰好一次」：Lobby 重启即丢墓碑（当前无数据库，契约 §3 已声明）。
    /// </summary>
    public enum PMDsSessionState : byte
    {
        /// <summary>尚未分配会话（A1 补充的初始态）。</summary>
        Idle = 0,

        /// <summary>已分配端口/名册/密钥，进程尚未启动。</summary>
        Allocated = 1,

        /// <summary>已发起进程启动，等待合法 Ready。</summary>
        Starting = 2,

        /// <summary>DS 报告就绪且全部校验通过，地址尚未发布给客户端。</summary>
        Ready = 3,

        /// <summary>地址已发布给客户端（由宿主显式调用 MarkRunning）。</summary>
        Running = 4,

        /// <summary>
        /// 已接受结果，正在重发 ResultAck。
        ///
        /// 本状态下的「结束」有**两条必须区分**的路径（R3-B 集成冻结裁决）：
        /// ① 对端经 MAC/对局/世代认证的**显式** <c>Exited(0)</c> ⇒ 视为正常完成，终态
        ///    <see cref="PMDsSessionState.Exited"/>；这是 DS 收到匹配 ResultAck 后主动声明收尾的真实路径。
        /// ② 单纯进程提前退出（Tick 轮询发现 / 宿主报告，**没有**任何协议证据）⇒ 仍判
        ///    <see cref="PMDsSessionState.Failed"/>。
        /// 两条路径都**不从消息推断进程死亡**：端口与名册一律要等本地确认进程真的退出才释放。
        /// </summary>
        ResultPending = 5,

        /// <summary>
        /// 结果已在**本地受理**、ResultAck 重发预算用尽，进入收尾（等待进程退出）。
        ///
        /// ⚠ **语义修正（本次审查闭合，数值保持 6 与旧名对齐）**：契约 §3 的状态链把它写作
        /// <c>ResultAcked</c>，读起来像「对端已确认」。但 DS→Lobby 的上行消息清单只有
        /// Ready / Heartbeat / Result / Exited / Error，**不存在任何「DS 已收到 ResultAck」的确认消息**，
        /// 所以「对端确认」在当前协议下不可能由上行确认帧达成；进入本状态的唯一条件是自己数够重发次数，
        /// 记在 <see cref="PMDsSessionCounters.ResultAckAbandoned"/>（放弃重传）。
        /// 「对端已收尾」由后续观测决定：<see cref="PMDsCoordinator.PeerExitObserved"/>。
        /// </summary>
        ResultCommitted = 6,

        /// <summary>终态：正常退出。</summary>
        Exited = 7,

        /// <summary>终态：失败（启动失败/崩溃/未产生结果即退出/协议错误）。</summary>
        Failed = 8,

        /// <summary>终态：超时（启动超时/心跳超时）。</summary>
        TimedOut = 9,
    }

    /// <summary>可注入时钟（毫秒 UTC）。生产用 <see cref="PMDsSystemClock"/>，测试用虚拟时钟。</summary>
    public interface IPMDsClock
    {
        /// <summary>UTC 毫秒时间戳。</summary>
        long UtcNowUnixMilliseconds { get; }
    }

    /// <summary>系统时钟。</summary>
    public sealed class PMDsSystemClock : IPMDsClock
    {
        /// <summary>单例（无状态）。</summary>
        public static readonly PMDsSystemClock Instance = new PMDsSystemClock();

        public long UtcNowUnixMilliseconds
        {
            get { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
        }
    }

    /// <summary>协调器可配置参数。默认值即契约 §3 的默认值，只是把「测试用 sleep」换成了注入时钟。</summary>
    public sealed class PMDsCoordinatorOptions
    {
        /// <summary>启动超时：默认 30 秒（契约 §3）。</summary>
        public TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

        /// <summary>心跳超时：默认 15 秒（契约 §3）。</summary>
        public TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(15);

        /// <summary>
        /// 同一会话两次**心跳应答**之间的最小间隔：默认 1 秒。
        ///
        /// 为什么需要：本轮给控制面加了双向 liveness（合法 Ready / 合法 Heartbeat 之后回一条签名 Heartbeat），
        /// 如果没有节流，一个狂发 Ready/Heartbeat 的对端就能让本端按同样频率回帧，
        /// 形成「心跳回声风暴」（两端互相触发，无上限地占带宽）。
        /// 与锁仓无关：它只影响**应答节奏**，不影响任何超时判定。
        /// </summary>
        public TimeSpan HeartbeatReplyMinInterval = TimeSpan.FromMilliseconds(1000);

        /// <summary>结果确认重发间隔：默认 1 秒（契约 §3）。</summary>
        public TimeSpan ResultAckRetryInterval = TimeSpan.FromSeconds(1);

        /// <summary>结果确认最多重发次数（重发完即进入 ResultCommitted 并开始收尾）。</summary>
        public int MaxResultAckRetransmits = 3;

        /// <summary>
        /// 强杀重试间隔：一次强杀请求之后，至少间隔这么久才允许再次请求强杀。
        ///
        /// 为什么需要它：真实 <c>Process.Kill()</c> 只是「请求终止」，进程可能仍在运行并仍持有监听端口。
        /// 旧实现用一次性闩锁（<c>_killRequested</c>）挡住后续所有请求，于是「第一次强杀失败」就永久静默、
        /// 残留进程再也没人回收。现在改为**有节流的重试**：失败可见（写进事件 Reason 与计数器），
        /// 且不会在同一个 Tick 里刷爆。
        /// （A1 补充项，不在契约 §3 的默认超时清单里。）
        /// </summary>
        public TimeSpan KillRetryInterval = TimeSpan.FromSeconds(1);

        /// <summary>
        /// 每次「确认进程已退出」的有界等待上限。终态清理与收尾重试都只做**一次有界探测**：
        /// 探测不到已退出就**不释放端口与名册**，留给下一次 <see cref="PMDsCoordinator.Tick"/> 继续收尾。
        /// （A1 补充项，不在契约 §3 的默认超时清单里。）
        /// </summary>
        public TimeSpan ProcessExitConfirmWait = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// 认证正常退出后的**优雅退出宽限**：默认 5 秒（0 表示不宽限）。
        ///
        /// 为什么需要它（真实 DS 复现）：DS 收到匹配 ResultAck 后先发一条**经 MAC 认证的显式
        /// <c>Exited(0)</c>**，再走 Unity 的 <c>Application.Quit</c>；Unity 收尾不是瞬时的。
        /// 旧实现在进入终态时**无条件** <c>Process.Kill()</c>，于是「Exited(0) 已收到、进程还在收尾」
        /// 这一窗口里被强杀，OS 退出码变成 <c>-1</c>（.NET 强杀以 -1 终止），
        /// 而 DS 自己声明的却是 <c>exitCode=0</c> ——「成功局被写成异常退出」。
        /// 现在改成：认证正常退出后给一段**有界**宽限，宽限内只做非阻塞轮询、不请求强杀；
        /// 进程在宽限内自行退出即为正常收尾；宽限到期仍未退出才按异常收尾强杀（并记录）。
        /// 宽限必须有限：否则一个僵而不死的进程会让共享端口与 uid 永久无法回收。
        /// （R3-B 补充项，不在契约 §3 的默认超时清单里。）
        /// </summary>
        public TimeSpan GracefulExitGracePeriod = TimeSpan.FromSeconds(5);

        /// <summary>收尾宽限期：默认 10 秒（契约 §3）。到期未退出即请求强杀。</summary>
        public TimeSpan ShutdownGracePeriod = TimeSpan.FromSeconds(10);

        /// <summary>结果幂等墓碑 TTL：默认 5 分钟。</summary>
        public TimeSpan TombstoneTtl = TimeSpan.FromMinutes(5);

        /// <summary>墓碑条数上限（超出即淘汰最旧的一条）。</summary>
        public int MaxTombstones = 64;

        /// <summary>端口池起始端口（含）。</summary>
        public int PortRangeFirst = 7801;

        /// <summary>端口池结束端口（含）。</summary>
        public int PortRangeLast = 7899;

        /// <summary>单局名册人数上限（契约 §2：最多 6 人）。</summary>
        public int MaxRosterPlayers = PMDsControlWire.MaxRosterPlayers;

        /// <summary>默认票据有效期（契约 §2：120 秒）。</summary>
        public TimeSpan TicketLifetime = TimeSpan.FromSeconds(PMDsTicketPolicy.DefaultLifetimeSeconds);

        /// <summary>本进程声明的协议摘要；非 0 时要求分配请求的摘要与它一致（不一致即拒绝分配）。</summary>
        public uint ProtocolHash;

        /// <summary>校验参数；非法即抛（配置错误要在启动时就炸，不能等到局中）。</summary>
        public void Validate()
        {
            if (StartTimeout <= TimeSpan.Zero) { throw new ArgumentOutOfRangeException("StartTimeout"); }
            if (HeartbeatTimeout <= TimeSpan.Zero) { throw new ArgumentOutOfRangeException("HeartbeatTimeout"); }
            if (HeartbeatReplyMinInterval <= TimeSpan.Zero) { throw new ArgumentOutOfRangeException("HeartbeatReplyMinInterval"); }
            if (ResultAckRetryInterval <= TimeSpan.Zero) { throw new ArgumentOutOfRangeException("ResultAckRetryInterval"); }
            if (MaxResultAckRetransmits < 0) { throw new ArgumentOutOfRangeException("MaxResultAckRetransmits"); }
            if (KillRetryInterval <= TimeSpan.Zero) { throw new ArgumentOutOfRangeException("KillRetryInterval"); }
            if (ProcessExitConfirmWait <= TimeSpan.Zero) { throw new ArgumentOutOfRangeException("ProcessExitConfirmWait"); }
            if (GracefulExitGracePeriod < TimeSpan.Zero) { throw new ArgumentOutOfRangeException("GracefulExitGracePeriod"); }
            if (ShutdownGracePeriod <= TimeSpan.Zero) { throw new ArgumentOutOfRangeException("ShutdownGracePeriod"); }
            if (TombstoneTtl <= TimeSpan.Zero) { throw new ArgumentOutOfRangeException("TombstoneTtl"); }
            if (MaxTombstones < 1) { throw new ArgumentOutOfRangeException("MaxTombstones"); }
            if (PortRangeFirst < 1 || PortRangeLast > 65535 || PortRangeFirst > PortRangeLast)
            {
                throw new ArgumentOutOfRangeException("PortRangeFirst", "端口范围非法（1..65535 且 first <= last）");
            }

            if (MaxRosterPlayers < 1 || MaxRosterPlayers > PMDsControlWire.MaxRosterPlayers)
            {
                throw new ArgumentOutOfRangeException(
                    "MaxRosterPlayers", "名册上限必须在 1.." + PMDsControlWire.MaxRosterPlayers + " 之间");
            }

            if (TicketLifetime <= TimeSpan.Zero
                || TicketLifetime.TotalSeconds > PMDsTicketPolicy.MaxLifetimeSeconds)
            {
                throw new ArgumentOutOfRangeException("TicketLifetime", "票据有效期必须在 1..600 秒之间");
            }
        }
    }

    /// <summary>
    /// 跨会话玩家占用账本的**契约**（R3-B）。
    ///
    /// 为什么把它声明在协调器文件里、而实现在 <c>Server/DS/PMDsPlayerLedger.cs</c>：
    /// 协调器是 A1 已交付且被 A1 门禁（只链接 <c>PMDsCoordinator.cs</c>+<c>PMDsProcess.cs</c>）
    /// 直接编译的源，而账本是 R3-B 新增的 Lobby 资源。把依赖收窄成一个接口，
    /// 就能让「协调器知道要预留 uid」与「账本怎么实现」解耦，且不把新文件强加给 A1 门禁。
    /// 实现语义（整局原子预留、按局整体释放、与端口同点）见 <c>PMDsPlayerLedger</c>。
    /// </summary>
    public interface IPMDsPlayerLedger
    {
        /// <summary>为一局原子预留整个名册；失败时不得留下任何部分占用。</summary>
        bool TryReserve(string matchId, PMDsRosterIdentity[] roster, out string reason);

        /// <summary>释放某局的全部占用（幂等）。</summary>
        bool Release(string matchId);

        /// <summary>该 uid 是否已被某个对局占用。</summary>
        bool IsOccupied(int uid);

        /// <summary>当前被占用的 uid 总数。</summary>
        int Count { get; }
    }

    /// <summary>会话分配请求（Lobby 匹配层产出，交由协调器落地）。</summary>
    public sealed class PMDsAllocationRequest
    {
        /// <summary>对局 ID（非空，≤128 字节）。</summary>
        public string MatchId = string.Empty;

        /// <summary>DS ID（非空，≤128 字节）。</summary>
        public string DsId = string.Empty;

        /// <summary>会话世代（非 0）。</summary>
        public uint Epoch;

        /// <summary>协议摘要（非 0）。</summary>
        public uint ProtocolHash;

        /// <summary>期望 DS 回带的碰撞配置摘要（非 0）。</summary>
        public uint CollisionDigest;

        /// <summary>名册（1..6 人，Uid/PlayerId 唯一）。</summary>
        public PMDsRosterIdentity[] Roster = new PMDsRosterIdentity[0];

        /// <summary>
        /// 同机引导文件的**绝对路径**（R3-B 宿主填写；A1 的原有调用可为空）。
        ///
        /// 用途只有一个：让 <see cref="PMDsCoordinator.SetLaunchRequest"/> 能证明「进程参数里的
        /// <c>-bootstrap</c> 指向的正是宿主真正发布的那份文件」，从而把「文件已安全发布」与
        /// 「进程可以启动」做成可校验的因果关系，而不是靠调用顺序纪律。
        /// 为空时跳过该项校验（保持 A1 既有调用不受影响）；R3-B 的宿主**始终**填写。
        /// </summary>
        public string BootstrapFilePath = string.Empty;

        /// <summary>进程启动请求（真实 exe 路径来自配置白名单）。</summary>
        public PMDsProcessLaunchRequest Process;

        /// <summary>该局票据有效期覆盖（null 用 options 默认）。</summary>
        public TimeSpan? TicketLifetime;
    }

    /// <summary>协调器对外副作用（宿主实现 <see cref="IPMDsCoordinatorSink"/> 消费）。</summary>
    public enum PMDsCoordinatorEffect : byte
    {
        /// <summary>已请求启动进程（真实启动已由注入的启动器完成）。</summary>
        ProcessStartRequested = 1,

        /// <summary>已发布就绪地址给客户端（**必须由宿主真正发地址**；本条是唯一一次发布）。</summary>
        ReadyAddressPublished = 2,

        /// <summary>需要把 <see cref="PMDsCoordinatorEvent.ControlPayload"/> 发给对端（已签名，无长度前缀）。</summary>
        ControlMessageOut = 3,

        /// <summary>已请求优雅关闭（对端应收 Shutdown 并自行退出）。</summary>
        GracefulShutdownRequested = 4,

        /// <summary>已请求强杀（宽限期超时 / 终态清理）。</summary>
        ProcessKillRequested = 5,

        /// <summary>已释放端口与名册（终态）。</summary>
        ResourcesReleased = 6,

        /// <summary>会话结束（终态唯一一次）。</summary>
        SessionEnded = 7,

        /// <summary>已接受权威结果（业务层在此把结果并入匹配系统）。</summary>
        ResultAccepted = 8,
    }

    /// <summary>协调器事件。**所有字段都不含密钥/票据**。</summary>
    public sealed class PMDsCoordinatorEvent
    {
        /// <summary>副作用种类。</summary>
        public PMDsCoordinatorEffect Effect;

        /// <summary>事件发生后的会话状态。</summary>
        public PMDsSessionState State;

        /// <summary>相关端口（发布/释放）。</summary>
        public int Port;

        /// <summary>相关进程 ID（启动/退出）。</summary>
        public int ProcessId;

        /// <summary>退出码（结束/退出）。</summary>
        public int ExitCode;

        /// <summary>原因描述（不含秘密）。</summary>
        public string Reason;

        /// <summary>对外控制消息：**已签名载荷**（不含 4 字节长度前缀）。</summary>
        public byte[] ControlPayload;

        /// <summary>对外控制消息类型。</summary>
        public PMDsControlMessageType ControlType;

        /// <summary>结果 ID（ResultAccepted）。</summary>
        public ulong ResultId;

        /// <summary>胜方队伍（ResultAccepted）。</summary>
        public int WinnerTeamId;

        /// <summary>结算摘要（ResultAccepted；业务层消费后可丢弃）。</summary>
        public byte[] ResultSummary;

        public override string ToString()
        {
            return Effect.ToString() + " state=" + State
                + (Port != 0 ? " port=" + Port : string.Empty)
                + (ProcessId != 0 ? " pid=" + ProcessId : string.Empty)
                + (Reason != null ? " reason=" + Reason : string.Empty);
        }
    }

    /// <summary>副作用接收者（宿主实现）。</summary>
    public interface IPMDsCoordinatorSink
    {
        /// <summary>收到一个副作用。实现**不应抛异常**（协调器会吞掉异常以保证状态机前进）。</summary>
        void OnEffect(PMDsCoordinatorEvent effect);
    }

    /// <summary>丢弃所有副作用的实现（只关心状态的测试/查询场景）。</summary>
    public sealed class PMDsNullCoordinatorSink : IPMDsCoordinatorSink
    {
        /// <summary>单例。</summary>
        public static readonly PMDsNullCoordinatorSink Instance = new PMDsNullCoordinatorSink();

        public void OnEffect(PMDsCoordinatorEvent effect)
        {
        }
    }

    /// <summary>入站消息处理结论。</summary>
    public enum PMDsCoordinatorOutcome : byte
    {
        /// <summary>已被接受并推进了状态（或完成了要求的动作）。</summary>
        Applied = 0,

        /// <summary>幂等重复（未改状态；需要重发的消息已重发）。</summary>
        Duplicate = 1,

        /// <summary>当前状态不接受该消息。</summary>
        RejectedState = 2,

        /// <summary>身份/世代/摘要不匹配。</summary>
        RejectedIdentity = 3,

        /// <summary>MAC 缺失或验签失败（**状态未动**）。</summary>
        RejectedMac = 4,

        /// <summary>载荷结构非法（**状态未动**）。</summary>
        RejectedMalformed = 5,

        /// <summary>与已接受的结果冲突（同 ID 不同内容，或终态后来新结果）。</summary>
        RejectedConflict = 6,

        /// <summary>Ready 校验失败（端口不一致 / 摘要不一致 / sceneReady=false）。</summary>
        RejectedNotReady = 7,

        /// <summary>无操作（例如 Tick 没有到期事项）。</summary>
        Ignored = 8,

        /// <summary>
        /// 玩家占用冲突（R3-B）：名册里的 uid 已被**另一个**对局通过共享
        /// <see cref="PMDsPlayerLedger"/> 占用。**状态与端口均未被触碰**。
        /// </summary>
        RejectedPlayerOccupied = 9,
    }

    /// <summary>处理结论 + 状态快照。</summary>
    public struct PMDsCoordinatorReply
    {
        /// <summary>结论。</summary>
        public PMDsCoordinatorOutcome Outcome;

        /// <summary>处理后的状态。</summary>
        public PMDsSessionState State;

        /// <summary>原因/诊断（不含秘密）。</summary>
        public string Detail;

        /// <summary>是否属于「被接受」（含幂等重复）。</summary>
        public bool IsAccepted
        {
            get
            {
                return Outcome == PMDsCoordinatorOutcome.Applied || Outcome == PMDsCoordinatorOutcome.Duplicate;
            }
        }

        public override string ToString()
        {
            return Outcome.ToString() + " state=" + State + (Detail != null ? " (" + Detail + ")" : string.Empty);
        }
    }

    /// <summary>会话计数器（全部可达；测试用它证明「负向输入确实走到了分支」）。</summary>
    public sealed class PMDsSessionCounters
    {
        /// <summary>被接受的入站控制帧数。</summary>
        public long FramesAccepted;

        /// <summary>幂等重复帧数。</summary>
        public long FramesDuplicate;

        /// <summary>状态不接受而被拒的帧数。</summary>
        public long FramesRejectedState;

        /// <summary>身份/世代/摘要不匹配被拒的帧数。</summary>
        public long FramesRejectedIdentity;

        /// <summary>结构非法被拒的帧数。</summary>
        public long FramesRejectedMalformed;

        /// <summary>MAC 缺失或验签失败被拒的帧数。</summary>
        public long FramesRejectedMac;

        /// <summary>Ready 校验失败被拒的帧数。</summary>
        public long FramesRejectedNotReady;

        /// <summary>结果冲突被拒的帧数。</summary>
        public long FramesRejectedConflict;

        /// <summary>已发布就绪地址的次数（成功路径应恰好 1）。</summary>
        public long ReadyPublications;

        /// <summary>重复 Ready 次数（不重复发布）。</summary>
        public long ReadyDuplicates;

        /// <summary>接受的心跳数。</summary>
        public long Heartbeats;

        /// <summary>
        /// 真正发出的**心跳应答**数（Lobby → DS，用于双向 liveness；按消息类型与控制帧出栈一并可查）。
        /// 与 <see cref="Heartbeats"/> 分开：前者是本端**发出**的，后者是本端**收到**的。
        /// </summary>
        public long HeartbeatReplies;

        /// <summary>因 <see cref="PMDsCoordinatorOptions.HeartbeatReplyMinInterval"/> 节流而被压掉的心跳应答次数（可观测「确实在限流」）。</summary>
        public long HeartbeatRepliesThrottled;

        /// <summary>心跳超时次数。</summary>
        public long HeartbeatTimeouts;

        /// <summary>首次接受结果的次数。</summary>
        public long ResultAccepted;

        /// <summary>发出的 ResultAck 次数（含首答与重发）。</summary>
        public long ResultAcksSent;

        /// <summary>ResultAck 定时重发次数（同一时钟触发的重发）。</summary>
        public long ResultAckRetransmits;

        /// <summary>
        /// 结果确认重发预算用尽（放弃重传）的次数。
        /// **这是进入 ResultCommitted 的唯一条件** —— 它记录的是「己方放弃」而不是「对端确认」。
        /// </summary>
        public long ResultAckAbandoned;

        /// <summary>结果幂等重复次数（含由墓碑服务的终态重复）。</summary>
        public long ResultDuplicates;

        /// <summary>结果冲突次数。</summary>
        public long ResultConflicts;

        /// <summary>启动超时次数。</summary>
        public long StartTimeouts;

        /// <summary>观测到的进程退出次数。</summary>
        public long ProcessExits;

        /// <summary>请求强杀次数（含节流后的重试）。</summary>
        public long KillRequests;

        /// <summary>强杀请求失败的次数（Kill 抛异常 / 无法请求）。失败不静默：同时写入事件 Reason。</summary>
        public long KillRequestFailures;

        /// <summary>
        /// 终态已确定但进程尚未确认退出、因此**推迟释放端口与名册**的 Tick 次数。
        /// 这个计数非零就说明共享端口池上的那个端口确实还被旧进程占着。
        /// </summary>
        public long DeferredCleanupTicks;

        /// <summary>
        /// 认证正常退出后，仍在**优雅退出宽限内**等待进程自行退出的 Tick 次数。
        /// 非零即证明「宽限确实存在」——那段时间里没有发出任何强杀请求。
        /// </summary>
        public long GracefulExitWaits;

        /// <summary>
        /// 认证正常退出后，进程在宽限内**自行退出**（全程未请求强杀）的次数。
        /// 这是「正常路径不靠强杀」的直接证据。
        /// </summary>
        public long GracefulExitObserved;

        /// <summary>
        /// 优雅退出宽限到期、进程仍未退出，因此转入异常收尾强杀（并记录）的次数。
        /// 非零表示这一局的收尾是异常收尾，不是干净退出。
        /// </summary>
        public long GracefulExitTimeouts;

        /// <summary>请求优雅关闭次数。</summary>
        public long GracefulShutdowns;

        /// <summary>写入墓碑次数。</summary>
        public long TombstonesWritten;

        /// <summary>墓碑因条数上限被淘汰次数。</summary>
        public long TombstonesEvicted;

        /// <summary>墓碑因 TTL 过期被清理次数。</summary>
        public long TombstonesExpired;

        /// <summary>释放资源次数（成功路径应恰好 1）。</summary>
        public long ResourcesReleased;

        /// <summary>发出的副作用总数。</summary>
        public long EffectsEmitted;

        /// <summary>签发的票据张数。</summary>
        public long TicketsIssued;

        /// <summary>票据校验通过次数。</summary>
        public long TicketAccepted;

        /// <summary>票据校验失败次数。</summary>
        public long TicketRejected;
    }

    /// <summary>结果幂等墓碑。</summary>
    public struct PMDsResultTombstone
    {
        /// <summary>业务结果 ID。</summary>
        public ulong ResultId;

        /// <summary>胜方队伍。</summary>
        public int WinnerTeamId;

        /// <summary>
        /// 结算摘要**原文**（逐字节保留，用于「同 ID 不同内容」的完整比对）。
        /// 有界：<see cref="PMDsControlWire.MaxSummaryBytes"/> ≤ 512 字节，乘上墓碑条数上限仍是有界的。
        /// </summary>
        public byte[] Summary;

        /// <summary>
        /// 结算摘要的内容摘要。**仅作诊断**（日志/报告里能一眼对照），
        /// **不参与冲突判定** —— 判定一律用 <see cref="Summary"/> + <see cref="WinnerTeamId"/> 的完整比对。
        /// </summary>
        public uint ContentHash;

        /// <summary>写入时刻（UTC 毫秒）。</summary>
        public long RecordedAtUnixMilliseconds;

        /// <summary>过期时刻（UTC 毫秒）。</summary>
        public long ExpiresAtUnixMilliseconds;

        /// <summary>该结果的 ResultAck 签名载荷（重发时字节完全一致）。</summary>
        public byte[] AckPayload;

        /// <summary>诊断输出（无秘密；只给摘要长度与诊断哈希，不展开摘要原文）。</summary>
        public override string ToString()
        {
            return "tombstone(result=" + ResultId + " winner=" + WinnerTeamId
                + " summaryLen=" + (Summary == null ? 0 : Summary.Length)
                + " diagHash=" + ContentHash + " exp=" + ExpiresAtUnixMilliseconds + ")";
        }
    }

    /// <summary>墓碑写入结论。</summary>
    public enum PMDsTombstoneWrite : byte
    {
        /// <summary>新增。</summary>
        Added = 0,

        /// <summary>同 ID 同内容（幂等刷新）。</summary>
        Updated = 1,

        /// <summary>同 ID 不同内容（必须拒绝）。</summary>
        Conflict = 2,
    }

    /// <summary>
    /// 结果幂等墓碑账本：**TTL + 条数上限**都有界。
    ///
    /// 为什么需要它：结果确认可能丢包，DS 会重发同一结果。Lobby 已经在终态释放了端口与名册，
    /// 如果此时无法识别「这条结果是刚结算过的那一条」，就会把它当成未知来源丢弃（业务上等于结算丢失）。
    /// 墓碑不是「永久去重」，只是覆盖确认丢失窗口；Lobby 重启即丢（当前无数据库，契约 §3 已声明）。
    /// </summary>
    public sealed class PMDsResultTombstoneLedger
    {
        private readonly int _maxEntries;
        private readonly long _ttlMilliseconds;
        private readonly Dictionary<ulong, PMDsResultTombstone> _entries = new Dictionary<ulong, PMDsResultTombstone>();

        public PMDsResultTombstoneLedger(int maxEntries, TimeSpan ttl)
        {
            if (maxEntries < 1)
            {
                throw new ArgumentOutOfRangeException("maxEntries");
            }

            if (ttl <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException("ttl");
            }

            _maxEntries = maxEntries;
            _ttlMilliseconds = (long)ttl.TotalMilliseconds;
            MaxEntries = maxEntries;
            TtlMilliseconds = _ttlMilliseconds;
        }

        /// <summary>条数上限。</summary>
        public int MaxEntries { get; private set; }

        /// <summary>TTL（毫秒）。</summary>
        public long TtlMilliseconds { get; private set; }

        /// <summary>当前条数。</summary>
        public int Count { get { return _entries.Count; } }

        /// <summary>累计写入次数。</summary>
        public long WrittenCount { get; private set; }

        /// <summary>累计因上限淘汰次数。</summary>
        public long EvictedCount { get; private set; }

        /// <summary>累计因 TTL 清理次数。</summary>
        public long ExpiredCount { get; private set; }

        /// <summary>构造一条墓碑（自动填 TTL）。<paramref name="summary"/> 会被逐字节复制，避免与调用方缓冲别名。</summary>
        public PMDsResultTombstone CreateEntry(ulong resultId, int winnerTeamId, byte[] summary, uint contentHash,
            byte[] ackPayload, long nowUnixMilliseconds)
        {
            PMDsResultTombstone entry = new PMDsResultTombstone();
            entry.ResultId = resultId;
            entry.WinnerTeamId = winnerTeamId;
            entry.Summary = CopySummary(summary);
            entry.ContentHash = contentHash;
            entry.AckPayload = ackPayload;
            entry.RecordedAtUnixMilliseconds = nowUnixMilliseconds;
            entry.ExpiresAtUnixMilliseconds = nowUnixMilliseconds + _ttlMilliseconds;
            return entry;
        }

        /// <summary>摘要复制（null 与空视为同一种内容）。</summary>
        public static byte[] CopySummary(byte[] summary)
        {
            if (summary == null || summary.Length == 0)
            {
                return new byte[0];
            }

            byte[] copy = new byte[summary.Length];
            Buffer.BlockCopy(summary, 0, copy, 0, summary.Length);
            return copy;
        }

        /// <summary>
        /// 写入（先清过期，再必要时淘汰最旧）。
        ///
        /// **冲突判定用完整内容**：胜方队伍相等 **且** 摘要逐字节相等才算同一条；
        /// <see cref="PMDsResultTombstone.ContentHash"/> 只是诊断字段，不参与判定。
        /// 判定为 <see cref="PMDsTombstoneWrite.Conflict"/> 时**不覆盖**也不刷新已有条目的 TTL。
        /// </summary>
        public PMDsTombstoneWrite Record(PMDsResultTombstone entry, long nowUnixMilliseconds)
        {
            CollectExpired(nowUnixMilliseconds);

            PMDsResultTombstone existing;
            if (_entries.TryGetValue(entry.ResultId, out existing))
            {
                if (existing.WinnerTeamId != entry.WinnerTeamId
                    || !SummaryEquals(existing.Summary, entry.Summary))
                {
                    return PMDsTombstoneWrite.Conflict;
                }

                _entries[entry.ResultId] = entry;
                WrittenCount++;
                return PMDsTombstoneWrite.Updated;
            }

            while (_entries.Count >= _maxEntries && _entries.Count > 0)
            {
                EvictOldest();
            }

            _entries.Add(entry.ResultId, entry);
            WrittenCount++;
            return PMDsTombstoneWrite.Added;
        }

        /// <summary>查询（过期即视为不存在）。</summary>
        public bool TryGet(ulong resultId, long nowUnixMilliseconds, out PMDsResultTombstone entry)
        {
            if (_entries.TryGetValue(resultId, out entry))
            {
                if (entry.ExpiresAtUnixMilliseconds > nowUnixMilliseconds)
                {
                    return true;
                }

                _entries.Remove(resultId);
                ExpiredCount++;
            }

            entry = default(PMDsResultTombstone);
            return false;
        }

        /// <summary>清理过期条目，返回清理数量。</summary>
        public int CollectExpired(long nowUnixMilliseconds)
        {
            if (_entries.Count == 0)
            {
                return 0;
            }

            List<ulong> doomed = null;
            foreach (KeyValuePair<ulong, PMDsResultTombstone> pair in _entries)
            {
                if (pair.Value.ExpiresAtUnixMilliseconds <= nowUnixMilliseconds)
                {
                    if (doomed == null)
                    {
                        doomed = new List<ulong>();
                    }

                    doomed.Add(pair.Key);
                }
            }

            if (doomed == null)
            {
                return 0;
            }

            for (int i = 0; i < doomed.Count; i++)
            {
                _entries.Remove(doomed[i]);
            }

            ExpiredCount += doomed.Count;
            return doomed.Count;
        }

        /// <summary>清空。</summary>
        public void Clear()
        {
            _entries.Clear();
        }

        /// <summary>摘要逐字节比对（null 与空等价）。</summary>
        public static bool SummaryEquals(byte[] left, byte[] right)
        {
            int leftLength = left == null ? 0 : left.Length;
            int rightLength = right == null ? 0 : right.Length;
            if (leftLength != rightLength)
            {
                return false;
            }

            for (int i = 0; i < leftLength; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        private void EvictOldest()
        {
            ulong oldestKey = 0UL;
            bool found = false;
            long oldest = long.MaxValue;
            foreach (KeyValuePair<ulong, PMDsResultTombstone> pair in _entries)
            {
                if (!found || pair.Value.RecordedAtUnixMilliseconds < oldest)
                {
                    found = true;
                    oldest = pair.Value.RecordedAtUnixMilliseconds;
                    oldestKey = pair.Key;
                }
            }

            if (found)
            {
                _entries.Remove(oldestKey);
                EvictedCount++;
            }
        }
    }

    /// <summary>
    /// 端口池：**确定性分配（从小到大）+ 显式回收**。
    /// 「终态释放端口」这件事必须可被测试证明，所以回收是一个显式动作而不是 GC 副作用。
    /// </summary>
    public sealed class PMDsPortPool
    {
        private readonly int _first;
        private readonly int _last;
        private readonly bool[] _used;

        public PMDsPortPool(int first, int last)
        {
            if (first < 1 || last > 65535 || first > last)
            {
                throw new ArgumentOutOfRangeException("first", "端口范围非法");
            }

            _first = first;
            _last = last;
            _used = new bool[last - first + 1];
        }

        /// <summary>容量。</summary>
        public int Capacity { get { return _used.Length; } }

        /// <summary>在用数量。</summary>
        public int InUseCount { get; private set; }

        /// <summary>分配一个端口（按从小到大）。</summary>
        public bool TryAcquire(out int port)
        {
            for (int i = 0; i < _used.Length; i++)
            {
                if (!_used[i])
                {
                    _used[i] = true;
                    InUseCount++;
                    port = _first + i;
                    return true;
                }
            }

            port = 0;
            return false;
        }

        /// <summary>回收端口；返回它之前是否在用。</summary>
        public bool Release(int port)
        {
            int index = port - _first;
            if (index < 0 || index >= _used.Length || !_used[index])
            {
                return false;
            }

            _used[index] = false;
            InUseCount--;
            return true;
        }

        /// <summary>端口是否在用。</summary>
        public bool IsInUse(int port)
        {
            int index = port - _first;
            return index >= 0 && index < _used.Length && _used[index];
        }

        /// <summary>全部回收（进程退出时的兜底）。</summary>
        public void ReleaseAll()
        {
            for (int i = 0; i < _used.Length; i++)
            {
                _used[i] = false;
            }

            InUseCount = 0;
        }
    }

    /// <summary>一局（一个 DS 进程）的 Lobby 侧协调器。单实例只服务一局。</summary>
    public sealed class PMDsCoordinator : IDisposable
    {
        private readonly PMDsCoordinatorOptions _options;
        private readonly IPMDsClock _clock;
        private readonly IPMDsProcessLauncher _launcher;
        private readonly IPMDsCoordinatorSink _sink;
        private readonly PMDsPortPool _ports;
        private readonly IPMDsPlayerLedger _playerLedger;
        private readonly PMDsResultTombstoneLedger _tombstones;
        private string _expectedBootstrapFilePath = string.Empty;
        private readonly Dictionary<int, int> _uidToRosterIndex = new Dictionary<int, int>();

        private PMDsSessionState _state = PMDsSessionState.Idle;
        private string _matchId = string.Empty;
        private string _dsId = string.Empty;
        private uint _epoch;
        private uint _protocolHash;
        private uint _collisionDigest;
        private PMDsRosterIdentity[] _roster = new PMDsRosterIdentity[0];
        private PMDsProcessLaunchRequest _launch;
        private PMDsMatchKey _key;
        private PMDsControlSigner _signer;
        private PMDsTicketIssuer _issuer;
        private PMDsTicketVerifier _verifier;
        private int _port;
        private IPMDsProcess _process;
        private int _processId;

        private long _startDeadlineMilliseconds;
        private long _lastHeartbeatMilliseconds;
        private long _allocatedMilliseconds;

        /// <summary>是否已经回过心跳应答（用作「无上次应答时刻」的安全哨兵，避免 long.MinValue 相减溢出）。</summary>
        private bool _hasHeartbeatReply;
        private long _lastHeartbeatReplyMilliseconds;
        private long _shutdownDeadlineMilliseconds;
        private long _nextAckRetryMilliseconds;

        private bool _resourcesReleased;
        private bool _awaitingExit;
        private bool _awaitingProcessExit;

        /// <summary>
        /// 正在「认证正常退出」的优雅退出宽限内：宽限内**不请求强杀**，只做非阻塞轮询。
        /// 与 <see cref="_awaitingProcessExit"/> 同时为真时，说明终态已确定、但为「不把 Unity 收尾
        /// 打成非 0 退出」而故意等一段有界时间；<see cref="PMDsCoordinatorOptions.GracefulExitGracePeriod"/>
        /// 到期仍未退出才转入强杀。
        /// </summary>
        private bool _awaitingGracefulExit;

        /// <summary>优雅退出宽限的到期时刻（UTC 毫秒）；仅在 <see cref="_awaitingGracefulExit"/> 为真时有意义。</summary>
        private long _gracefulExitDeadlineMilliseconds;

        private bool _peerExitObserved;
        private long _nextKillRetryMilliseconds = long.MinValue;
        private bool _peerRequestedShutdown;
        private bool _hostRequestedShutdown;
        private bool _hasResult;
        private ulong _resultId;
        private int _winnerTeamId;
        private uint _resultContentHash;
        private byte[] _resultSummary = new byte[0];
        private byte[] _resultAckPayload;
        private int _ackRetransmits;
        private bool _disposed;

        public PMDsCoordinator(PMDsCoordinatorOptions options, IPMDsClock clock,
            IPMDsProcessLauncher launcher, IPMDsCoordinatorSink sink)
            : this(options, clock, launcher, sink, null, null)
        {
        }

        /// <summary>
        /// 带**共享端口池**的构造。
        ///
        /// 为什么需要它：本类是一局一个实例，而端口资源是**跨局共享**的。
        /// 若每个会话各自 <c>new PMDsPortPool</c>，所有会话都会从同一个最小端口开始分配，
        /// 于是不同局拿到同一端口（本测试 `G50` 就是把这个后果钉住的证据）。
        /// 因此宿主（R3-B 的 Lobby）**必须**持有唯一的 <see cref="PMDsPortPool"/> 并注入到每个会话。
        /// 传入 null 则退化为「本实例自己的池」（只适合单会话测试）。
        /// </summary>
        public PMDsCoordinator(PMDsCoordinatorOptions options, IPMDsClock clock,
            IPMDsProcessLauncher launcher, IPMDsCoordinatorSink sink, PMDsPortPool sharedPortPool)
            : this(options, clock, launcher, sink, sharedPortPool, null)
        {
        }

        /// <summary>
        /// 带**共享端口池 + 共享玩家占用账本**的构造（R3-B 宿主使用）。
        ///
        /// 为什么玩家占用也必须是共享的：<see cref="PMDsPortPool"/> 挡住了「两局抢同一端口」，
        /// 但名册唯一性原先只做**本局内**校验，同一个 uid 可以同时出现在两个会话里 ——
        /// 也就是「两局抢同一个人」。<see cref="PMDsPlayerLedger"/> 补上这一半，
        /// 并且**与端口同点释放**（<c>ReleaseResources</c>），因此不会出现
        /// 「进程还活着但 uid 已经放出去」的中间态。
        /// 传入 null 则不做跨会话 uid 守卫（只适合单会话测试）。
        /// </summary>
        public PMDsCoordinator(PMDsCoordinatorOptions options, IPMDsClock clock,
            IPMDsProcessLauncher launcher, IPMDsCoordinatorSink sink, PMDsPortPool sharedPortPool,
            IPMDsPlayerLedger sharedPlayerLedger)
        {
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }

            options.Validate();
            _options = options;
            _clock = clock ?? PMDsSystemClock.Instance;
            _launcher = launcher ?? new PMDsSystemProcessLauncher();
            _sink = sink ?? PMDsNullCoordinatorSink.Instance;
            _ports = sharedPortPool ?? new PMDsPortPool(options.PortRangeFirst, options.PortRangeLast);
            _playerLedger = sharedPlayerLedger;
            _tombstones = new PMDsResultTombstoneLedger(options.MaxTombstones, options.TombstoneTtl);
        }

        /// <summary>当前状态。</summary>
        public PMDsSessionState State { get { return _state; } }

        /// <summary>是否终态（Exited / Failed / TimedOut）。</summary>
        public bool IsTerminal
        {
            get
            {
                return _state == PMDsSessionState.Exited
                    || _state == PMDsSessionState.Failed
                    || _state == PMDsSessionState.TimedOut;
            }
        }

        /// <summary>对局 ID。</summary>
        public string MatchId { get { return _matchId; } }

        /// <summary>DS ID。</summary>
        public string DsId { get { return _dsId; } }

        /// <summary>会话世代。</summary>
        public uint Epoch { get { return _epoch; } }

        /// <summary>协议摘要。</summary>
        public uint ProtocolHash { get { return _protocolHash; } }

        /// <summary>已分配端口（终态释放后为 0）。</summary>
        public int AllocatedPort { get { return _port; } }

        /// <summary>本协调器端口池的在用端口数（分配后 1，终态释放后 0 —— 释放证据靠它直读）。</summary>
        public int PortsInUse { get { return _ports.InUseCount; } }

        /// <summary>
        /// 终态已确定、但进程尚未**确认退出**：此时端口与名册**仍被占用**（不得提前释放）。
        /// 由 <see cref="Tick"/> 继续收尾/重试强杀，直到确认退出或收到对端显式 Exited。
        /// </summary>
        public bool IsAwaitingProcessExit { get { return _awaitingProcessExit; } }

        /// <summary>
        /// 是否正在**认证正常退出**的优雅退出宽限内等待进程自行退出。
        /// 为真时：终态已确定、资源仍被占用、未发出强杀请求。
        /// </summary>
        public bool IsAwaitingGracefulExit { get { return _awaitingGracefulExit; } }

        /// <summary>端口/名册等资源是否仍被本会话占用（终态前、或延迟收尾中为 true）。</summary>
        public bool ResourcesHeld { get { return !_resourcesReleased; } }

        /// <summary>
        /// 是否已观测到对端（DS）收尾：来自 DS 的 <c>Exited</c> 消息或进程退出。
        /// **这是当前协议下唯一可用的「对端已结束」信号**；
        /// <see cref="PMDsSessionState.ResultCommitted"/> 本身**不代表**对端确认（也永不由重发次数置位）。
        /// </summary>
        public bool PeerExitObserved { get { return _peerExitObserved; } }

        /// <summary>指定端口是否仍被本协调器占用。</summary>
        public bool IsPortInUse(int port) { return _ports.IsInUse(port); }

        /// <summary>名册人数（终态释放后为 0）。</summary>
        public int RosterCount { get { return _roster.Length; } }

        /// <summary>进程 ID（未启动为 0）。</summary>
        public int ProcessId { get { return _processId; } }

        /// <summary>计数器。</summary>
        public PMDsSessionCounters Counters { get { return _counters; } }

        private readonly PMDsSessionCounters _counters = new PMDsSessionCounters();

        /// <summary>墓碑条数。</summary>
        public int TombstoneCount { get { return _tombstones.Count; } }

        /// <summary>控制密钥指纹（便于两端核对；不含密钥）。</summary>
        public string MatchKeyFingerprint { get { return _key == null ? null : _key.KeyFingerprint; } }

        /// <summary>最近一次结束原因（不含秘密）。</summary>
        public string LastReason { get; private set; }

        /// <summary>当前 UTC 毫秒（来自注入时钟）。</summary>
        public long NowUnixMilliseconds { get { return _clock.UtcNowUnixMilliseconds; } }

        // ────────────────────────────────────────────────────────────────
        // 生命周期（宿主调用）
        // ────────────────────────────────────────────────────────────────

        /// <summary>分配会话：取端口、生成每局密钥、派生票据签发器/校验器，状态 → Allocated。</summary>
        public PMDsCoordinatorReply Allocate(PMDsAllocationRequest request)
        {
            if (_state != PMDsSessionState.Idle)
            {
                return Reply(PMDsCoordinatorOutcome.RejectedState, "会话已分配（当前 " + _state + "）");
            }

            if (request == null)
            {
                return Reply(PMDsCoordinatorOutcome.RejectedMalformed, "分配请求为 null");
            }

            if (string.IsNullOrEmpty(request.MatchId) || string.IsNullOrEmpty(request.DsId))
            {
                return Reply(PMDsCoordinatorOutcome.RejectedMalformed, "MatchId / DsId 不得为空");
            }

            try
            {
                PMDsControlCodec.EnsureWithinBytes(request.MatchId, PMDsControlWire.MaxMatchIdBytes, "MatchId");
                PMDsControlCodec.EnsureWithinBytes(request.DsId, PMDsControlWire.MaxDsIdBytes, "DsId");
            }
            catch (PMDsControlProtocolException ex)
            {
                return Reply(PMDsCoordinatorOutcome.RejectedMalformed, ex.Message);
            }

            if (request.Epoch == 0u)
            {
                return Reply(PMDsCoordinatorOutcome.RejectedMalformed, "Epoch 不得为 0");
            }

            if (request.ProtocolHash == 0u)
            {
                return Reply(PMDsCoordinatorOutcome.RejectedMalformed, "ProtocolHash 不得为 0");
            }

            if (_options.ProtocolHash != 0u && request.ProtocolHash != _options.ProtocolHash)
            {
                return Reply(PMDsCoordinatorOutcome.RejectedIdentity,
                    "协议摘要与本进程不一致：请求 0x" + request.ProtocolHash.ToString("X8")
                    + "，本地 0x" + _options.ProtocolHash.ToString("X8"));
            }

            if (request.CollisionDigest == 0u)
            {
                return Reply(PMDsCoordinatorOutcome.RejectedMalformed, "CollisionDigest 不得为 0");
            }

            if (request.Roster == null || request.Roster.Length == 0)
            {
                return Reply(PMDsCoordinatorOutcome.RejectedMalformed, "名册不得为空");
            }

            if (request.Roster.Length > _options.MaxRosterPlayers)
            {
                return Reply(PMDsCoordinatorOutcome.RejectedMalformed,
                    "名册超过上限：" + request.Roster.Length + " > " + _options.MaxRosterPlayers);
            }

            try
            {
                PMDsControlCodec.ValidateIdentities(request.Roster, "分配名册");
            }
            catch (PMDsControlProtocolException ex)
            {
                return Reply(PMDsCoordinatorOutcome.RejectedMalformed, ex.Message);
            }

            PMDsProcessFault fault;
            string detail;
            if (!PMDsProcessSafety.ValidateStructure(request.Process, out fault, out detail))
            {
                return Reply(PMDsCoordinatorOutcome.RejectedMalformed, "进程请求非法（" + fault + "）：" + detail);
            }

            // R3-B：跨会话 uid 占用必须先于端口获取。
            // 顺序很关键：如果先拿端口再发现 uid 冲突，就必须把端口还回去，
            // 而「还回去」在共享池里意味着次序被打乱（确定性分配从小到大），徒增测试噪声。
            // 预留失败时状态、端口、名册都未被触碰。
            if (_playerLedger != null && !_playerLedger.TryReserve(request.MatchId, request.Roster, out detail))
            {
                return Reply(PMDsCoordinatorOutcome.RejectedPlayerOccupied, "玩家占用冲突：" + detail);
            }

            int port;
            if (!_ports.TryAcquire(out port))
            {
                // 端口拿不到 → 回滚刚才的 uid 预留，否则会出现「占着玩家、没有端口」的泄漏。
                if (_playerLedger != null)
                {
                    _playerLedger.Release(request.MatchId);
                }

                return Reply(PMDsCoordinatorOutcome.RejectedState,
                    "端口池已耗尽（" + _options.PortRangeFirst + "-" + _options.PortRangeLast + "）");
            }

            _port = port;
            _matchId = request.MatchId;
            _dsId = request.DsId;
            _epoch = request.Epoch;
            _protocolHash = request.ProtocolHash;
            _collisionDigest = request.CollisionDigest;
            _expectedBootstrapFilePath = request.BootstrapFilePath ?? string.Empty;
            _roster = new PMDsRosterIdentity[request.Roster.Length];
            for (int i = 0; i < request.Roster.Length; i++)
            {
                _roster[i] = request.Roster[i];
                _uidToRosterIndex[_roster[i].Uid] = i;
            }

            _launch = request.Process.Clone();
            _key = PMDsMatchKey.Create();
            _signer = _key.CreateControlSigner();
            _issuer = _key.CreateTicketIssuer(_matchId, _dsId, _epoch, _protocolHash);
            _verifier = _key.CreateTicketVerifier(new PMDsTicketExpectation(_matchId, _dsId, _epoch, _protocolHash));
            _allocatedMilliseconds = NowUnixMilliseconds;
            _state = PMDsSessionState.Allocated;
            return Reply(PMDsCoordinatorOutcome.Applied, null);
        }

        /// <summary>
        /// 在 <see cref="PMDsSessionState.Allocated"/> 阶段注入/替换启动参数，并对参数做
        /// **fail-closed** 校验（R3-B，契约 §7.3「启动参数必须从 Allocate 后的真实 Port 生成」）。
        ///
        /// 为什么需要这个接口：端口只在 <see cref="Allocate"/> 成功之后才知道，
        /// 所以宿主无法在分配前就把 <c>-port &lt;真实端口&gt;</c> 写进 args。
        /// 如果没有这个接口，宿主只能把 <c>-port</c> 交给「调用顺序纪律」，
        /// 而那正是 R3-A1 示例里硬编码 <c>7801</c> 能悄悄活下来的原因。
        ///
        /// 校验项（任一不满足即 <see cref="PMDsCoordinatorOutcome.RejectedMalformed"/>，
        /// **不改状态、不释放资源**，由宿主自行决定取消）：
        /// 1. 结构合法（<see cref="PMDsProcessSafety.ValidateStructure"/>，含「无 shell / 绝对路径 / 无 NUL 换行」）；
        /// 2. 可执行文件与工作目录与分配时**一致**（不允许分配后换 exe）；
        /// 3. 恰好一个 <c>-port</c>，且值等于本次分配的真实端口（支持 <c>-k v</c> 与 <c>-k=v</c>）；
        /// 4. 恰好一个 <c>-dsid</c>，值等于本次 DsId；
        /// 5. 恰好一个 <c>-matchid</c>，值等于本次 MatchId；
        /// 6. 若分配请求给出了 <see cref="PMDsAllocationRequest.BootstrapFilePath"/>，
        ///    则恰好一个 <c>-bootstrap</c> 且值逐字符等于该绝对路径。
        ///
        /// 注意：命令行参数里**不得**出现密钥/完整票据（引导内容一律走文件路径），
        /// 这是契约 §2 的硬要求；本方法只校验结构，不做「参数里有没有秘密」的字符串猜测。
        /// </summary>
        public PMDsCoordinatorReply SetLaunchRequest(PMDsProcessLaunchRequest request)
        {
            if (_state != PMDsSessionState.Allocated)
            {
                return Reply(PMDsCoordinatorOutcome.RejectedState,
                    "只有 Allocated 可以设置启动参数（当前 " + _state + "）");
            }

            PMDsProcessFault fault;
            string detail;
            if (!PMDsProcessSafety.ValidateStructure(request, out fault, out detail))
            {
                return Reply(PMDsCoordinatorOutcome.RejectedMalformed,
                    "启动请求非法（" + fault + "）：" + detail);
            }

            if (_launch != null)
            {
                if (!string.Equals(_launch.ExecutablePath, request.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                {
                    return Reply(PMDsCoordinatorOutcome.RejectedMalformed,
                        "不允许在分配后更换可执行文件：分配=" + _launch.ExecutablePath
                        + "，请求=" + request.ExecutablePath);
                }

                if (!string.Equals(_launch.WorkingDirectory, request.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    return Reply(PMDsCoordinatorOutcome.RejectedMalformed,
                        "不允许在分配后更换工作目录：分配=" + _launch.WorkingDirectory
                        + "，请求=" + request.WorkingDirectory);
                }
            }

            string[] args = request.Arguments ?? new string[0];

            if (!HasExactlyOneValue(args, "-port", _port.ToString(), out detail))
            {
                return Reply(PMDsCoordinatorOutcome.RejectedMalformed, "-port 校验失败：" + detail);
            }

            if (!HasExactlyOneValue(args, "-dsid", _dsId, out detail))
            {
                return Reply(PMDsCoordinatorOutcome.RejectedMalformed, "-dsid 校验失败：" + detail);
            }

            if (!HasExactlyOneValue(args, "-matchid", _matchId, out detail))
            {
                return Reply(PMDsCoordinatorOutcome.RejectedMalformed, "-matchid 校验失败：" + detail);
            }

            if (!string.IsNullOrEmpty(_expectedBootstrapFilePath))
            {
                if (!HasExactlyOneValue(args, "-bootstrap", _expectedBootstrapFilePath, out detail))
                {
                    return Reply(PMDsCoordinatorOutcome.RejectedMalformed, "-bootstrap 校验失败：" + detail);
                }
            }

            _launch = request.Clone();
            return Reply(PMDsCoordinatorOutcome.Applied, null);
        }

        /// <summary>
        /// 参数数组中是否恰好出现一个键，且其值（大小写敏感、逐字符）等于期望值。
        /// 支持 <c>-k v</c> 与 <c>-k=v</c> 两种写法；重复键视为失败（fail-closed）。
        /// </summary>
        private static bool HasExactlyOneValue(string[] args, string key, string expected, out string detail)
        {
            detail = null;
            int found = 0;
            string value = null;
            string prefix = key + "=";

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == null)
                {
                    continue;
                }

                if (string.Equals(arg, key, StringComparison.Ordinal))
                {
                    found++;
                    value = i + 1 < args.Length ? args[i + 1] : null;
                    i++;
                    continue;
                }

                if (arg.StartsWith(prefix, StringComparison.Ordinal))
                {
                    found++;
                    value = arg.Substring(prefix.Length);
                }
            }

            if (found == 0)
            {
                detail = "缺少 " + key;
                return false;
            }

            if (found > 1)
            {
                detail = "重复出现 " + found + " 次 " + key + "（fail-closed）";
                return false;
            }

            if (value == null)
            {
                detail = key + " 没有取值";
                return false;
            }

            if (!string.Equals(value, expected, StringComparison.Ordinal))
            {
                detail = key + " 取值与本会话不一致（拒绝把参数交给调用顺序纪律）";
                return false;
            }

            return true;
        }

        /// <summary>发起进程启动，状态 → Starting（失败即 Failed 并释放资源）。</summary>
        public PMDsCoordinatorReply BeginStart()
        {
            if (_state != PMDsSessionState.Allocated)
            {
                return Reply(PMDsCoordinatorOutcome.RejectedState, "只有 Allocated 可以启动（当前 " + _state + "）");
            }

            IPMDsProcess process;
            PMDsProcessFault fault;
            string detail;
            if (!_launcher.TryStart(_launch, out process, out fault, out detail) || process == null)
            {
                return FailAndRelease("进程启动失败（" + fault + "）：" + detail);
            }

            _process = process;
            _processId = process.ProcessId;
            _state = PMDsSessionState.Starting;
            _startDeadlineMilliseconds = NowUnixMilliseconds + (long)_options.StartTimeout.TotalMilliseconds;
            _lastHeartbeatMilliseconds = NowUnixMilliseconds;

            Emit(new PMDsCoordinatorEvent
            {
                Effect = PMDsCoordinatorEffect.ProcessStartRequested,
                State = _state,
                Port = _port,
                ProcessId = _processId,
                Reason = "进程已启动",
            });
            return Reply(PMDsCoordinatorOutcome.Applied, null);
        }

        /// <summary>宿主已把就绪地址真正发给客户端 → Running。</summary>
        public PMDsCoordinatorReply MarkRunning()
        {
            if (_state == PMDsSessionState.Running)
            {
                return Reply(PMDsCoordinatorOutcome.Duplicate, "已经处于 Running");
            }

            if (_state != PMDsSessionState.Ready)
            {
                return Reply(PMDsCoordinatorOutcome.RejectedState, "只有 Ready 可以进入 Running（当前 " + _state + "）");
            }

            _state = PMDsSessionState.Running;
            return Reply(PMDsCoordinatorOutcome.Applied, null);
        }

        /// <summary>宿主侧取消/关闭请求。</summary>
        public PMDsCoordinatorReply RequestShutdown(uint reasonCode)
        {
            if (IsTerminal)
            {
                return Reply(PMDsCoordinatorOutcome.Duplicate, "会话已结束（" + _state + "）");
            }

            if (_state == PMDsSessionState.Idle)
            {
                return Reply(PMDsCoordinatorOutcome.RejectedState, "尚未分配会话");
            }

            if (_hostRequestedShutdown)
            {
                return Reply(PMDsCoordinatorOutcome.Duplicate, "已请求过关闭");
            }

            _hostRequestedShutdown = true;

            if (_state == PMDsSessionState.Allocated)
            {
                // 进程都没起来：直接收尾，不留端口占用。
                return FailAndRelease("宿主在启动前取消（reason=" + reasonCode + "）");
            }

            byte[] payload = SignControl(PMDsControlMessageType.Shutdown, 0u, new PMDsShutdownBody
            {
                ReasonCode = reasonCode,
            });
            _counters.GracefulShutdowns++;
            _awaitingExit = true;
            _shutdownDeadlineMilliseconds = NowUnixMilliseconds + (long)_options.ShutdownGracePeriod.TotalMilliseconds;
            EmitControl(PMDsCoordinatorEffect.GracefulShutdownRequested, payload, PMDsControlMessageType.Shutdown,
                "宿主请求关闭");
            return Reply(PMDsCoordinatorOutcome.Applied, null);
        }

        /// <summary>
        /// 时钟推进。宿主按固定节拍调用（例如每 200ms 一次）。
        /// 处理：终态延迟收尾、进程退出轮询、启动/心跳超时、ResultAck 重发、收尾强杀、墓碑过期。
        /// </summary>
        public PMDsCoordinatorReply Tick()
        {
            long now = NowUnixMilliseconds;

            int expired = _tombstones.CollectExpired(now);
            if (expired > 0)
            {
                _counters.TombstonesExpired += expired;
            }

            if (_state == PMDsSessionState.Idle)
            {
                return Reply(PMDsCoordinatorOutcome.Ignored, null);
            }

            // 0) 终态但进程尚未确认退出：端口与名册**仍被占用**，这里继续收尾/重试。
            //    这条路径必须排在终态早退之前，否则「终态 ⇒ 端口已释放」就是假的。
            if (IsTerminal)
            {
                if (_awaitingProcessExit)
                {
                    return ContinueTerminalCleanup(now);
                }

                return Reply(PMDsCoordinatorOutcome.Ignored, null);
            }

            // 1) 进程存活轮询（**不**依赖后台事件回调，状态只在这一条主线程路径上变化）
            if (_process != null && !_process.IsRunning)
            {
                int exitCode;
                if (_process.TryGetExitCode(out exitCode))
                {
                    ApplyProcessExit(exitCode, "轮询发现进程已退出");
                    return Reply(PMDsCoordinatorOutcome.Applied, null);
                }
            }

            // 2) 启动超时（强杀与「确认已退出」都在 Finalize 内，不在确认前释放端口）
            if (_state == PMDsSessionState.Starting && now >= _startDeadlineMilliseconds)
            {
                _counters.StartTimeouts++;
                return Finalize(PMDsSessionState.TimedOut,
                    "启动超时（未在 " + _options.StartTimeout.TotalSeconds + " 秒内收到合法 Ready）", 0);
            }

            // 3) 心跳超时（只在「期望 DS 活着」的状态成立）
            if (!_awaitingExit && (_state == PMDsSessionState.Ready
                || _state == PMDsSessionState.Running
                || _state == PMDsSessionState.ResultPending))
            {
                long silent = now - _lastHeartbeatMilliseconds;
                if (silent >= (long)_options.HeartbeatTimeout.TotalMilliseconds)
                {
                    _counters.HeartbeatTimeouts++;
                    return Finalize(PMDsSessionState.TimedOut, "心跳超时（" + silent + "ms 无心跳）", 0);
                }
            }

            // 4) ResultAck 定时重发（有界）
            if (_state == PMDsSessionState.ResultPending && _resultAckPayload != null && now >= _nextAckRetryMilliseconds)
            {
                if (_ackRetransmits < _options.MaxResultAckRetransmits)
                {
                    _ackRetransmits++;
                    _counters.ResultAcksSent++;
                    _counters.ResultAckRetransmits++;
                    _nextAckRetryMilliseconds = now + (long)_options.ResultAckRetryInterval.TotalMilliseconds;
                    EmitControl(PMDsCoordinatorEffect.ControlMessageOut, _resultAckPayload,
                        PMDsControlMessageType.ResultAck, "ResultAck 定时重发 #" + _ackRetransmits);
                    return Reply(PMDsCoordinatorOutcome.Applied, "ResultAck 重发 #" + _ackRetransmits);
                }

                // 重发预算用尽：进入 ResultCommitted（**本地受理，不是对端确认**）并开始收尾。
                _state = PMDsSessionState.ResultCommitted;
                _counters.ResultAckAbandoned++;
                BeginShutdownAfterResult(now,
                    "ResultAck 重发预算用尽（" + _options.MaxResultAckRetransmits + " 次）：本地受理完成，未获得对端确认");
                return Reply(PMDsCoordinatorOutcome.Applied,
                    "进入 ResultCommitted（本地受理；未由重发次数推断对端确认）并开始收尾");
            }

            // 5) 收尾宽限期到期 → 请求强杀（首次 + 节流重试；强杀后仍以进程退出为终态依据）
            if (_awaitingExit && now >= _shutdownDeadlineMilliseconds)
            {
                if (now >= _nextKillRetryMilliseconds)
                {
                    RequestKill("收尾宽限期到期（" + _options.ShutdownGracePeriod.TotalSeconds + " 秒）");
                    return Reply(PMDsCoordinatorOutcome.Applied, "已请求强杀");
                }

                return Reply(PMDsCoordinatorOutcome.Ignored,
                    "强杀已请求，等进程退出或重试间隔到期（端口与名册未释放）");
            }

            return Reply(PMDsCoordinatorOutcome.Ignored, null);
        }

        /// <summary>
        /// 宿主显式报告进程退出（正常路径由 <see cref="Tick"/> 轮询发现）。
        /// 没有可归属的进程（尚未启动）时拒绝，避免「未启动就被宣告终态」。
        /// </summary>
        public PMDsCoordinatorReply OnProcessExited(int exitCode)
        {
            if (IsTerminal)
            {
                return Reply(PMDsCoordinatorOutcome.Duplicate, "会话已结束（" + _state + "）");
            }

            if (_state == PMDsSessionState.Idle)
            {
                return Reply(PMDsCoordinatorOutcome.RejectedState, "尚未分配会话");
            }

            if (_process == null)
            {
                // 代次/归属校验：Allocated 阶段根本没有进程，不能被一条外部退出通知推到终态。
                return Reply(PMDsCoordinatorOutcome.RejectedState,
                    "没有可归属的进程（当前 " + _state + "），忽略进程退出通知");
            }

            ApplyProcessExit(exitCode, "宿主报告进程退出");
            return Reply(PMDsCoordinatorOutcome.Applied, null);
        }

        // ────────────────────────────────────────────────────────────────
        // 入站控制帧（唯一入口：解码 + 验签 + 身份校验 + 状态推进）
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 处理一条控制帧载荷（**不含** 4 字节长度前缀）。
        /// 顺序固定：结构解码 → MAC 验签 → 身份/世代/摘要 → 状态机。
        /// **任何一步失败都不会修改状态**。
        /// </summary>
        public PMDsCoordinatorReply OnControlPayload(byte[] payload, int offset, int count)
        {
            if (_state == PMDsSessionState.Idle)
            {
                return Reply(PMDsCoordinatorOutcome.RejectedState, "尚未分配会话，没有可用于验签的密钥");
            }

            PMDsControlMessage message;
            PMDsControlVerifyFault fault;
            if (!_signer.VerifyRaw(payload, offset, count, out message, out fault))
            {
                switch (fault)
                {
                    case PMDsControlVerifyFault.MacMismatch:
                        _counters.FramesRejectedMac++;
                        return Reply(PMDsCoordinatorOutcome.RejectedMac, "控制帧 MAC 校验失败");
                    case PMDsControlVerifyFault.MissingMac:
                        _counters.FramesRejectedMac++;
                        return Reply(PMDsCoordinatorOutcome.RejectedMac, "控制帧缺少 MAC");
                    default:
                        _counters.FramesRejectedMalformed++;
                        return Reply(PMDsCoordinatorOutcome.RejectedMalformed, "控制帧载荷非法");
                }
            }

            return Dispatch(message);
        }

        private PMDsCoordinatorReply Dispatch(PMDsControlMessage message)
        {
            if (!string.Equals(message.MatchId, _matchId, StringComparison.Ordinal)
                || !string.Equals(message.DsId, _dsId, StringComparison.Ordinal)
                || message.Epoch != _epoch
                || message.ProtocolHash != _protocolHash)
            {
                _counters.FramesRejectedIdentity++;
                return Reply(PMDsCoordinatorOutcome.RejectedIdentity,
                    "控制帧身份与本次分配不匹配（" + message.ToString() + "）");
            }

            switch (message.Type)
            {
                case PMDsControlMessageType.Bootstrap:
                    // Bootstrap 由 Lobby 发出（同机引导文件），Lobby 侧不接受对端 Bootstrap。
                    _counters.FramesRejectedState++;
                    return Reply(PMDsCoordinatorOutcome.RejectedState,
                        "Lobby 不接受 Bootstrap（引导走同机文件，避免把密钥/票据经通道下发）");

                case PMDsControlMessageType.Ready:
                    return HandleReady(message);

                case PMDsControlMessageType.Heartbeat:
                    return HandleHeartbeat(message);

                case PMDsControlMessageType.Result:
                    return HandleResult(message);

                case PMDsControlMessageType.ResultAck:
                    _counters.FramesRejectedState++;
                    return Reply(PMDsCoordinatorOutcome.RejectedState, "Lobby 不接受 ResultAck（Ack 由 Lobby 发出）");

                case PMDsControlMessageType.Shutdown:
                    return HandlePeerShutdown(message);

                case PMDsControlMessageType.Exited:
                    return HandlePeerExited(message);

                case PMDsControlMessageType.Error:
                    return HandlePeerError(message);

                default:
                    _counters.FramesRejectedMalformed++;
                    return Reply(PMDsCoordinatorOutcome.RejectedMalformed, "未知控制消息类型");
            }
        }

        private PMDsCoordinatorReply HandleReady(PMDsControlMessage message)
        {
            PMDsReadyBody body = message.AsReady;
            if (body == null)
            {
                _counters.FramesRejectedMalformed++;
                return Reply(PMDsCoordinatorOutcome.RejectedMalformed, "Ready 缺少 body");
            }

            string mismatch = DescribeReadyMismatch(body);
            if (mismatch != null)
            {
                _counters.FramesRejectedNotReady++;
                return Reply(PMDsCoordinatorOutcome.RejectedNotReady, mismatch);
            }

            if (_state == PMDsSessionState.Ready || _state == PMDsSessionState.Running
                || _state == PMDsSessionState.ResultPending || _state == PMDsSessionState.ResultCommitted)
            {
                // 重复 Ready：不重复通知、不重复发布地址。
                _counters.ReadyDuplicates++;
                _counters.FramesDuplicate++;
                return Reply(PMDsCoordinatorOutcome.Duplicate, "重复 Ready（不重复发布地址）");
            }

            if (_state != PMDsSessionState.Starting)
            {
                _counters.FramesRejectedState++;
                return Reply(PMDsCoordinatorOutcome.RejectedState, "当前状态不接受 Ready：" + _state);
            }

            _state = PMDsSessionState.Ready;
            _lastHeartbeatMilliseconds = NowUnixMilliseconds;
            _counters.ReadyPublications++;
            _counters.FramesAccepted++;

            Emit(new PMDsCoordinatorEvent
            {
                Effect = PMDsCoordinatorEffect.ReadyAddressPublished,
                State = _state,
                Port = _port,
                ProcessId = _processId,
                Reason = "DS 报告就绪（port=" + body.BoundPort + "，digest=0x" + body.CollisionDigest.ToString("X8") + "）",
            });

            // 双向 liveness：就绪之后立即给 DS 一条可信下行。
            // 为什么必须回：当前协议下 Lobby 只在**结果受理后**才下行 ResultAck，
            // 于是「收到过一次可信下行」这个事实在正常对局里可能永远不成立，
            // DS 侧的运行期看门狗只能退化成启动期看门狗 —— 那正是长局误退出的根因。
            // 同一句也保证了 DS 的 Ready 不再无限重发（它有「首个可信响应」就停）。
            ReplyHeartbeat("Ready 应答（双向 liveness；不改变就绪语义）");

            return Reply(PMDsCoordinatorOutcome.Applied, null);
        }

        private string DescribeReadyMismatch(PMDsReadyBody body)
        {
            if (body.BoundPort != _port)
            {
                return "Ready 端口与分配不一致：报告 " + body.BoundPort + "，分配 " + _port;
            }

            if (body.CollisionDigest != _collisionDigest)
            {
                return "Ready 碰撞摘要与分配不一致：报告 0x" + body.CollisionDigest.ToString("X8")
                    + "，期望 0x" + _collisionDigest.ToString("X8");
            }

            if (!body.SceneReady)
            {
                return "Ready.sceneReady=false：权威场景/碰撞环境尚未就绪";
            }

            return null;
        }

        private PMDsCoordinatorReply HandleHeartbeat(PMDsControlMessage message)
        {
            PMDsHeartbeatBody body = message.AsHeartbeat;
            if (body == null)
            {
                _counters.FramesRejectedMalformed++;
                return Reply(PMDsCoordinatorOutcome.RejectedMalformed, "Heartbeat 缺少 body");
            }

            if (_state != PMDsSessionState.Ready && _state != PMDsSessionState.Running
                && _state != PMDsSessionState.ResultPending && _state != PMDsSessionState.ResultCommitted)
            {
                _counters.FramesRejectedState++;
                return Reply(PMDsCoordinatorOutcome.RejectedState, "当前状态不接受 Heartbeat：" + _state);
            }

            _lastHeartbeatMilliseconds = NowUnixMilliseconds;
            _counters.Heartbeats++;
            _counters.FramesAccepted++;

            // 收到了才会回（且要过 MAC + 身份 + 状态三道关），并再叠一层时间节流：
            // 对端狂发心跳时本端不会同频回，DS 侧也不会因收到心跳立即回，因此不存在回声风暴。
            ReplyHeartbeat("Heartbeat 应答（双向 liveness；不改变状态）");
            return Reply(PMDsCoordinatorOutcome.Applied, null);
        }

        /// <summary>
        /// 向 DS 回一条**本局密钥签名**的 Heartbeat（控制面活性应答，不是业务状态）。
        ///
        /// 只允许在「已经通过 MAC + 身份 + 状态」三道校验之后调用：
        /// 本方法自身不改任何状态、不改任何 liveness 时钟（<see cref="_lastHeartbeatMilliseconds"/>），
        /// 也不参与结果/退出的任何判定 —— 它只是给对端的「我还活着」信号。
        /// </summary>
        private void ReplyHeartbeat(string reason)
        {
            long now = NowUnixMilliseconds;
            if (_hasHeartbeatReply)
            {
                long minInterval = (long)_options.HeartbeatReplyMinInterval.TotalMilliseconds;
                if (now - _lastHeartbeatReplyMilliseconds < minInterval)
                {
                    _counters.HeartbeatRepliesThrottled++;
                    return;
                }
            }

            PMDsHeartbeatBody body = new PMDsHeartbeatBody();
            long uptime = now - _allocatedMilliseconds;
            body.UptimeMilliseconds = uptime > 0L ? uptime : 0L;
            body.PlayerCount = _roster.Length;

            byte[] payload = SignControl(PMDsControlMessageType.Heartbeat, 0UL, body);

            _hasHeartbeatReply = true;
            _lastHeartbeatReplyMilliseconds = now;
            _counters.HeartbeatReplies++;
            EmitControl(PMDsCoordinatorEffect.ControlMessageOut, payload,
                PMDsControlMessageType.Heartbeat, reason);
        }

        private PMDsCoordinatorReply HandleResult(PMDsControlMessage message)
        {
            PMDsResultBody body = message.AsResult;
            if (body == null)
            {
                _counters.FramesRejectedMalformed++;
                return Reply(PMDsCoordinatorOutcome.RejectedMalformed, "Result 缺少 body");
            }

            // contentHash 只用于诊断/日志；**冲突判定一律用 winner + summary 的完整比对**。
            uint contentHash = ComputeResultContentHash(body);
            long now = NowUnixMilliseconds;

            // 终态之后的迟到结果：只允许由墓碑服务（Lobby 重启即丢，契约已声明）。
            if (IsTerminal)
            {
                PMDsResultTombstone tombstone;
                if (_tombstones.TryGet(body.ResultId, now, out tombstone))
                {
                    if (tombstone.WinnerTeamId == body.WinnerTeamId
                        && PMDsResultTombstoneLedger.SummaryEquals(tombstone.Summary, body.Summary))
                    {
                        _counters.ResultDuplicates++;
                        _counters.FramesDuplicate++;
                        EmitControl(PMDsCoordinatorEffect.ControlMessageOut, tombstone.AckPayload,
                            PMDsControlMessageType.ResultAck, "由墓碑重发同一 ResultAck");
                        return Reply(PMDsCoordinatorOutcome.Duplicate, "终态后重复结果：由墓碑重发同一 ResultAck");
                    }

                    _counters.ResultConflicts++;
                    _counters.FramesRejectedConflict++;
                    return Reply(PMDsCoordinatorOutcome.RejectedConflict,
                        "同一 ResultId 的内容与墓碑不一致（胜方或摘要字节不同；墓碑不被覆盖）");
                }

                _counters.FramesRejectedState++;
                return Reply(PMDsCoordinatorOutcome.RejectedState, "会话已结束（" + _state + "），且无对应墓碑");
            }

            if (_hasResult)
            {
                if (body.ResultId == _resultId)
                {
                    if (body.WinnerTeamId == _winnerTeamId
                        && PMDsResultTombstoneLedger.SummaryEquals(body.Summary, _resultSummary))
                    {
                        _counters.ResultDuplicates++;
                        _counters.FramesDuplicate++;
                        EmitControl(PMDsCoordinatorEffect.ControlMessageOut, _resultAckPayload,
                            PMDsControlMessageType.ResultAck, "重复结果：重发同一 ResultAck");
                        return Reply(PMDsCoordinatorOutcome.Duplicate, "重复结果：重发同一 ResultAck");
                    }

                    _counters.ResultConflicts++;
                    _counters.FramesRejectedConflict++;
                    return Reply(PMDsCoordinatorOutcome.RejectedConflict,
                        "同一 ResultId 的结果内容不一致（胜方或摘要字节不同）");
                }

                _counters.ResultConflicts++;
                _counters.FramesRejectedConflict++;
                return Reply(PMDsCoordinatorOutcome.RejectedConflict,
                    "已接受 ResultId=" + _resultId + "，拒绝另一个 ResultId=" + body.ResultId);
            }

            if (_state != PMDsSessionState.Ready && _state != PMDsSessionState.Running)
            {
                _counters.FramesRejectedState++;
                return Reply(PMDsCoordinatorOutcome.RejectedState, "当前状态不接受结果：" + _state);
            }

            _hasResult = true;
            _resultId = body.ResultId;
            _winnerTeamId = body.WinnerTeamId;
            _resultContentHash = contentHash;
            _resultSummary = PMDsResultTombstoneLedger.CopySummary(body.Summary);
            _resultAckPayload = SignControl(PMDsControlMessageType.ResultAck, 0u, new PMDsResultAckBody
            {
                ResultId = body.ResultId,
            });
            _state = PMDsSessionState.ResultPending;
            _ackRetransmits = 0;
            _nextAckRetryMilliseconds = now + (long)_options.ResultAckRetryInterval.TotalMilliseconds;
            _counters.ResultAccepted++;
            _counters.ResultAcksSent++;
            _counters.FramesAccepted++;

            // 权威结果入口：业务层在此把结果并入匹配系统。
            Emit(new PMDsCoordinatorEvent
            {
                Effect = PMDsCoordinatorEffect.ResultAccepted,
                State = _state,
                Port = _port,
                ProcessId = _processId,
                ResultId = body.ResultId,
                WinnerTeamId = body.WinnerTeamId,
                ResultSummary = _resultSummary,
                Reason = "权威结果已接受（winner=" + body.WinnerTeamId + "）",
            });

            EmitControl(PMDsCoordinatorEffect.ControlMessageOut, _resultAckPayload,
                PMDsControlMessageType.ResultAck, "首次 ResultAck");
            return Reply(PMDsCoordinatorOutcome.Applied, null);
        }

        private PMDsCoordinatorReply HandlePeerShutdown(PMDsControlMessage message)
        {
            if (message.AsShutdown == null)
            {
                _counters.FramesRejectedMalformed++;
                return Reply(PMDsCoordinatorOutcome.RejectedMalformed, "Shutdown 缺少 body");
            }

            if (_state != PMDsSessionState.Ready && _state != PMDsSessionState.Running
                && _state != PMDsSessionState.ResultPending && _state != PMDsSessionState.ResultCommitted)
            {
                _counters.FramesRejectedState++;
                return Reply(PMDsCoordinatorOutcome.RejectedState, "当前状态不接受 Shutdown：" + _state);
            }

            if (_peerRequestedShutdown)
            {
                _counters.FramesDuplicate++;
                return Reply(PMDsCoordinatorOutcome.Duplicate, "重复 Shutdown");
            }

            _peerRequestedShutdown = true;
            _awaitingExit = true;
            _shutdownDeadlineMilliseconds = NowUnixMilliseconds + (long)_options.ShutdownGracePeriod.TotalMilliseconds;
            _counters.FramesAccepted++;
            return Reply(PMDsCoordinatorOutcome.Applied, "对端声明正在关闭，等待进程退出");
        }

        private PMDsCoordinatorReply HandlePeerExited(PMDsControlMessage message)
        {
            PMDsExitedBody body = message.AsExited;
            if (body == null)
            {
                _counters.FramesRejectedMalformed++;
                return Reply(PMDsCoordinatorOutcome.RejectedMalformed, "Exited 缺少 body");
            }

            // 终态 + 延迟收尾：对端显式声明已退出 = 当前协议下可用的「对端已收尾」信号，
            // 用它完成收尾（这正是「确认语义由后续 DS 退出/显式消息决定」的落点）。
            // 注意：这里只释放资源，**不**伪造状态转移（状态已经是终态）。
            if (IsTerminal)
            {
                if (_awaitingProcessExit)
                {
                    _counters.FramesAccepted++;
                    _peerExitObserved = true;
                    // 宽限内一律不允许强杀（两个标志同为 false）：宿主 pump 是单线程的，
                    // 也不能因为重复 Exited 就把 Unity 的收尾打成非 0 退出。
                    if (TryFinishTerminalCleanup("收到对端 Exited（exit=" + body.ExitCode + "）",
                        !_awaitingGracefulExit, !_awaitingGracefulExit))
                    {
                        return Reply(PMDsCoordinatorOutcome.Applied,
                            "终态收尾完成：对端显式 Exited 且进程已确认退出");
                    }

                    return Reply(PMDsCoordinatorOutcome.Applied,
                        "已收到对端显式 Exited，但本地仍观测到进程占用端口，继续收尾（不提前释放）");
                }

                _counters.FramesDuplicate++;
                return Reply(PMDsCoordinatorOutcome.Duplicate, "会话已结束（" + _state + "）");
            }

            // 状态门：Exited 只能属于一个**确实启动过进程**的会话。
            // 没有这道门，一条迟到/重复/乱序的自签名 Exited 就能把 Allocated（尚未启动）判死并归还端口。
            if (!CanAcceptRunningSessionFrame())
            {
                _counters.FramesRejectedState++;
                return Reply(PMDsCoordinatorOutcome.RejectedState,
                    "当前状态不接受 Exited（" + _state + "，进程="
                    + (_process == null ? "未启动" : "已启动") + "）");
            }

            _counters.FramesAccepted++;
            ApplyProcessExit(body.ExitCode, "收到 Exited 消息", true);
            return Reply(PMDsCoordinatorOutcome.Applied, null);
        }

        private PMDsCoordinatorReply HandlePeerError(PMDsControlMessage message)
        {
            PMDsErrorBody body = message.AsError;
            if (body == null)
            {
                _counters.FramesRejectedMalformed++;
                return Reply(PMDsCoordinatorOutcome.RejectedMalformed, "Error 缺少 body");
            }

            if (IsTerminal)
            {
                _counters.FramesDuplicate++;
                return Reply(PMDsCoordinatorOutcome.Duplicate, "会话已结束（" + _state + "）");
            }

            // 与 Exited 同口径的状态门（Error 在 Starting 阶段是合法的：DS 可以在就绪前报致命错误；
            // 但在 Allocated 阶段没有进程能上报 Error）。
            if (!CanAcceptRunningSessionFrame())
            {
                _counters.FramesRejectedState++;
                return Reply(PMDsCoordinatorOutcome.RejectedState,
                    "当前状态不接受 Error（" + _state + "，进程="
                    + (_process == null ? "未启动" : "已启动") + "）");
            }

            _counters.FramesAccepted++;
            // 终态转移与「确认已退出后释放资源」都在 Finalize 内（不在确认前归还端口）。
            return Finalize(PMDsSessionState.Failed,
                "DS 上报错误 code=" + body.Code + "：" + body.Message, 0);
        }

        /// <summary>
        /// <c>Exited</c> / <c>Error</c> 这类「来自已启动 DS 的事件」的状态门。
        /// 代次/身份已由 <see cref="Dispatch"/> 的 MatchId/DsId/Epoch/ProtocolHash 全等校验把住，
        /// 这里补上「必须已经启动过进程」这个归属校验：
        /// <c>Idle</c> / <c>Allocated</c> / 终态都不接受。
        /// </summary>
        private bool CanAcceptRunningSessionFrame()
        {
            if (_process == null)
            {
                return false;
            }

            return _state == PMDsSessionState.Starting
                || _state == PMDsSessionState.Ready
                || _state == PMDsSessionState.Running
                || _state == PMDsSessionState.ResultPending
                || _state == PMDsSessionState.ResultCommitted;
        }

        // ────────────────────────────────────────────────────────────────
        // 票据 / 查询
        // ────────────────────────────────────────────────────────────────

        /// <summary>导出同机引导文件字节（R3-B 负责原子写文件并把路径作为参数交给 DS）。</summary>
        public byte[] ExportBootstrapDocument()
        {
            if (_state == PMDsSessionState.Idle)
            {
                throw new InvalidOperationException("尚未分配会话，没有可导出的引导内容");
            }

            PMDsBootstrapPlayer[] players = new PMDsBootstrapPlayer[_roster.Length];
            long nowSeconds = NowUnixMilliseconds / 1000L;
            for (int i = 0; i < _roster.Length; i++)
            {
                PMDsTicket ticket = _issuer.Issue(_roster[i], nowSeconds);
                players[i] = new PMDsBootstrapPlayer(_roster[i], ticket.ExportTicketBytes());
                _counters.TicketsIssued++;
            }

            PMDsBootstrapBody body = new PMDsBootstrapBody();
            body.CollisionDigest = _collisionDigest;
            body.Players = players;

            PMDsControlMessage message = PMDsControlMessage.Create(
                PMDsControlMessageType.Bootstrap, _matchId, _dsId, _epoch, _protocolHash, 0UL, body);
            return PMDsBootstrapDocument.Encode(_key, message);
        }

        /// <summary>按 uid 查名册身份（终态释放后返回 false）。</summary>
        public bool TryGetRosterIdentity(int uid, out PMDsRosterIdentity identity)
        {
            identity = default(PMDsRosterIdentity);
            int index;
            if (!_uidToRosterIndex.TryGetValue(uid, out index))
            {
                return false;
            }

            if (index < 0 || index >= _roster.Length)
            {
                return false;
            }

            identity = _roster[index];
            return true;
        }

        /// <summary>
        /// 校验票据并把它解析成**名册身份**。这是「所有权只来自已认证票据」的落点：
        /// 调用方拿到 identity 之后不应再从业务包里采纳自报 uid。
        ///
        /// ⚠ 它**不是**防重放：同一张票据被校验两次都会通过。端点消费账本属 R3-B。
        /// </summary>
        public PMDsTicketVerification ResolveTicket(byte[] ticket, int offset, int count, long nowUnixSeconds)
        {
            PMDsTicketVerification result = new PMDsTicketVerification();
            if (_state == PMDsSessionState.Idle || _verifier == null)
            {
                result.Verdict = PMDsTicketVerdict.NoSession;
                _counters.TicketRejected++;
                return result;
            }

            result = _verifier.Verify(ticket, offset, count, nowUnixSeconds);
            if (!result.IsValid)
            {
                _counters.TicketRejected++;
                return result;
            }

            PMDsRosterIdentity rosterIdentity;
            if (!TryGetRosterIdentity(result.Identity.Uid, out rosterIdentity)
                || !rosterIdentity.Equals(result.Identity))
            {
                result.Verdict = PMDsTicketVerdict.NotInRoster;
                _counters.TicketRejected++;
                return result;
            }

            _counters.TicketAccepted++;
            return result;
        }

        /// <summary>整段重载。</summary>
        public PMDsTicketVerification ResolveTicket(byte[] ticket, long nowUnixSeconds)
        {
            if (ticket == null)
            {
                PMDsTicketVerification empty = new PMDsTicketVerification();
                empty.Verdict = PMDsTicketVerdict.Malformed;
                _counters.TicketRejected++;
                return empty;
            }

            return ResolveTicket(ticket, 0, ticket.Length, nowUnixSeconds);
        }

        /// <summary>为名册内某 uid 签发/取回票据（同一会话内幂等）。</summary>
        public PMDsTicket IssueTicket(int uid, long nowUnixSeconds)
        {
            if (_state == PMDsSessionState.Idle || _issuer == null)
            {
                throw new InvalidOperationException("尚未分配会话");
            }

            PMDsRosterIdentity identity;
            if (!TryGetRosterIdentity(uid, out identity))
            {
                throw new ArgumentException("uid " + uid + " 不在本局名册内", "uid");
            }

            PMDsTicket ticket = _issuer.Issue(identity, nowUnixSeconds);
            _counters.TicketsIssued++;
            return ticket;
        }

        /// <summary>查询结果墓碑。</summary>
        public bool TryGetTombstone(ulong resultId, out PMDsResultTombstone tombstone)
        {
            return _tombstones.TryGet(resultId, NowUnixMilliseconds, out tombstone);
        }

        /// <summary>
        /// 释放：进入终态（内部会请求强杀，并在**确认进程已退出**后释放端口/名册），幂等。
        /// 若 Dispose 时进程还没退出，端口与名册会保持占用，直到 <see cref="Tick"/> 确认退出
        /// （或不达预期地永远不退出 —— 但那是真实的，不再用「已释放」掩盖）。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Finalize(PMDsSessionState.Exited, "宿主 Dispose", 0);
            _tombstones.Clear();
            _key = null;
            _signer = null;
            _verifier = null;
            _issuer = null;
        }

        // ────────────────────────────────────────────────────────────────
        // 内部
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 进程退出的统一落点（轮询发现 / 宿主报告 / 对端显式 <c>Exited</c> 三条入口）。
        ///
        /// <param name="authenticatedPeerExited">
        /// 本次退出是否来自**对端经认证的显式 <c>Exited</c> 消息**。
        /// 该消息必须先通过 MAC 校验，并通过 <see cref="Dispatch"/> 的 MatchId/DsId/Epoch/ProtocolHash 全等校验，
        /// 才会走到这里（见 <see cref="HandlePeerExited"/>）。
        ///
        /// 它只影响 <see cref="PMDsSessionState.ResultPending"/> 这一处的终态标签：
        /// 已受理结果 + 显式 <c>Exited(0)</c> = 正常完成（终态 <see cref="PMDsSessionState.Exited"/>）；
        /// 单纯进程提前退出（无协议证据）仍判 <see cref="PMDsSessionState.Failed"/>。
        /// 这**不是**「由发送次数/消息推断进程已死」——资源释放仍然只认「本地确认进程已退出」。
        /// </param>
        /// </summary>
        private void ApplyProcessExit(int exitCode, string why, bool authenticatedPeerExited = false)
        {
            if (IsTerminal)
            {
                return;
            }

            _counters.ProcessExits++;
            _peerExitObserved = true;
            _processId = _process == null ? 0 : _process.ProcessId;

            switch (_state)
            {
                case PMDsSessionState.ResultCommitted:
                    Finalize(PMDsSessionState.Exited,
                        "结果已在本地受理，且进程已退出（" + why + "）", exitCode);
                    return;

                case PMDsSessionState.Starting:
                    Finalize(PMDsSessionState.Failed, "就绪前进程退出（" + why + "，exit=" + exitCode + "）", exitCode);
                    return;

                case PMDsSessionState.ResultPending:
                    // 已受理结果（_hasResult 必为真）时，**经认证的显式 Exited(0)** 表示对端正常收尾：
                    // 这正是 DS 在收到匹配 ResultAck 后 SendExited(0) 的真实路径，不能误判成失败。
                    // 只有「单纯进程提前退出」（轮询/宿主报告，无协议证据）或显式非 0 退出码才判失败。
                    // 无论哪条路径，端口/名册都仍要等 TryFinishTerminalCleanup 确认进程已退出。
                    if (authenticatedPeerExited && exitCode == 0 && _hasResult)
                    {
                        Finalize(PMDsSessionState.Exited,
                            "结果已本地受理，且对端经认证显式 Exited(0)（" + why + "）", exitCode, true);
                        return;
                    }

                    Finalize(PMDsSessionState.Failed, "结果确认前进程退出（" + why + "，exit=" + exitCode + "）", exitCode);
                    return;

                case PMDsSessionState.Ready:
                case PMDsSessionState.Running:
                    if (_hostRequestedShutdown)
                    {
                        Finalize(PMDsSessionState.Failed, "宿主请求关闭但未产生结果（exit=" + exitCode + "）", exitCode);
                    }
                    else if (_peerRequestedShutdown)
                    {
                        Finalize(PMDsSessionState.Failed, "DS 主动关闭但未产生结果（exit=" + exitCode + "）", exitCode);
                    }
                    else
                    {
                        Finalize(PMDsSessionState.Failed, "运行中进程异常退出（exit=" + exitCode + "）", exitCode);
                    }

                    return;

                default:
                    Finalize(PMDsSessionState.Failed, "进程在 " + _state + " 状态退出（exit=" + exitCode + "）", exitCode);
                    return;
            }
        }

        private void BeginShutdownAfterResult(long now, string why)
        {
            if (_hostRequestedShutdown)
            {
                return;
            }

            byte[] payload = SignControl(PMDsControlMessageType.Shutdown, 0u, new PMDsShutdownBody
            {
                ReasonCode = 1u,
            });
            _counters.GracefulShutdowns++;
            _awaitingExit = true;
            _shutdownDeadlineMilliseconds = now + (long)_options.ShutdownGracePeriod.TotalMilliseconds;
            EmitControl(PMDsCoordinatorEffect.GracefulShutdownRequested, payload,
                PMDsControlMessageType.Shutdown, why);
        }

        private PMDsCoordinatorReply FailAndRelease(string reason)
        {
            // 强杀与「确认已退出」都在 Finalize 内部：确认不到就不释放端口/名册。
            return Finalize(PMDsSessionState.Failed, reason, 0);
        }

        /// <summary>
        /// 进入终态（默认**非「认证正常退出」**路径）：无宽限，立即请求强杀，再按需确认退出后释放资源。
        /// 宽限语义与完整释放顺序见 <c>Finalize(PMDsSessionState, string, int, bool)</c>。
        /// </summary>
        private PMDsCoordinatorReply Finalize(PMDsSessionState terminalState, string reason, int exitCode)
        {
            return Finalize(terminalState, reason, exitCode, false);
        }

        /// <summary>
        /// 进入终态（带「对端是否经认证声明正常退出」标志）。**释放顺序是关键**：先处理进程（正常退出走宽限、
        /// 其余路径请求强杀），再**确认进程已退出**，最后才归还端口与名册。
        ///
        /// 旧实现在 Kill 与 Release 之间没有任何「确认已退出」的等待，于是「强杀失败/进程还在」时
        /// 共享端口池里的那个端口已经被还回去了（确定性分配从小到大 ⇒ 很可能被下一局立刻拿走），
        /// 而旧进程还占着它。现在改成分两段：
        /// 1. 本轮能确认退出 → 立即释放，结果与旧行为一致；
        /// 2. 确认不到 → 置 <see cref="IsAwaitingProcessExit"/>，**保留端口与名册**，
        ///    在后续 <see cref="Tick"/> 里节流重试强杀，直到确认退出或收到对端显式 Exited。
        ///
        /// <paramref name="gracefulPeerExit"/> = true 表示「**经认证的显式 Exited(0)**」：
        /// 这是正常完成路径，进程此刻往往还在 Unity 收尾。此时**不立即强杀**，而是进入
        /// <see cref="PMDsCoordinatorOptions.GracefulExitGracePeriod"/> 的有界宽限，宽限内只做非阻塞轮询。
        /// 这不会放宽任何安全约束：端口与名册仍然只在**本地确认进程退出**后才释放。
        /// </summary>
        private PMDsCoordinatorReply Finalize(PMDsSessionState terminalState, string reason, int exitCode,
            bool gracefulPeerExit)
        {
            if (IsTerminal)
            {
                return Reply(PMDsCoordinatorOutcome.Duplicate, "会话已结束（" + _state + "）");
            }

            LastReason = reason;

            if (_hasResult && _state != PMDsSessionState.Exited)
            {
                // 墓碑写入用完整内容（winner + summary 字节）；ContentHash 仅供诊断。
                if (_tombstones.Record(_tombstones.CreateEntry(
                    _resultId, _winnerTeamId, _resultSummary, _resultContentHash, _resultAckPayload,
                    NowUnixMilliseconds),
                    NowUnixMilliseconds) == PMDsTombstoneWrite.Conflict)
                {
                    _counters.ResultConflicts++;
                }
                else
                {
                    _counters.TombstonesWritten++;
                }
            }

            _state = terminalState;

            bool graceStarted = false;
            if (_process != null && _process.IsRunning)
            {
                if (gracefulPeerExit)
                {
                    // 正常退出：不立刻强杀，改为有界宽限内等它自己走完（见 BeginGracefulExitWait）。
                    BeginGracefulExitWait();
                    graceStarted = true;
                }
                else
                {
                    RequestKill("终态清理（" + terminalState + "）");
                }
            }

            Emit(new PMDsCoordinatorEvent
            {
                Effect = PMDsCoordinatorEffect.SessionEnded,
                State = _state,
                Port = _port,
                ProcessId = _processId,
                ExitCode = exitCode,
                Reason = reason,
                ResultId = _resultId,
                WinnerTeamId = _winnerTeamId,
            });

            if (!TryFinishTerminalCleanup(reason, false, !graceStarted))
            {
                // 进程还没退出：**不归还端口、不释放名册**，交给 Tick 继续收尾。
                // （非宽限路径刚在上一行请求过强杀，`RequestKill` 已把下一次重试时刻推到 now + KillRetryInterval；
                //   宽限路径则**一次强杀请求都没发**。）
                _awaitingProcessExit = true;
                return Reply(PMDsCoordinatorOutcome.Applied,
                    reason + "；进程尚未确认退出，端口与名册暂不释放（Tick 将继续收尾"
                    + (graceStarted ? "；宽限内不请求强杀" : "/重试强杀") + "）");
            }

            return Reply(PMDsCoordinatorOutcome.Applied, reason);
        }

        /// <summary>
        /// 开始「认证正常退出」的优雅退出宽限：只记截止时刻，**不请求强杀**。
        ///
        /// 宽限内 <see cref="Tick"/> 只做非阻塞的 <see cref="IPMDsProcess.IsRunning"/> 查询（不调
        /// <see cref="IPMDsProcess.WaitForExit"/>），因此有真实进程的宿主也不会被阻塞住主线程 pump。
        /// </summary>
        private void BeginGracefulExitWait()
        {
            _awaitingGracefulExit = true;
            _gracefulExitDeadlineMilliseconds = NowUnixMilliseconds
                + (long)_options.GracefulExitGracePeriod.TotalMilliseconds;

            // 纵深防御：把「下一次允许强杀的时刻」推到宽限到期。这样即使有别的入口路径
            // 在宽限内尝试重试强杀（例如重复 Exited、宿主主动收尾），也会被节流挡到宽限结束，
            // 而不是因为「从未强杀过」而立即放行。
            _nextKillRetryMilliseconds = _gracefulExitDeadlineMilliseconds;
        }

        /// <summary>
        /// 终态但进程未退出的收尾：继续重试强杀 + 确认退出，**不伪造进程退出、不提前释放资源**。
        ///
        /// 认证正常退出的宽限路径单独先走：宽限内不请求强杀；到期仍未退出才转入异常收尾强杀（并记录）。
        /// </summary>
        private PMDsCoordinatorReply ContinueTerminalCleanup(long now)
        {
            if (_awaitingGracefulExit)
            {
                // 宽限内只看状态，**不阻塞**（不调 WaitForExit）。
                if (_process == null || !_process.IsRunning)
                {
                    _awaitingGracefulExit = false;
                    _awaitingProcessExit = false;
                    _counters.GracefulExitObserved++;
                    ReleaseResources("终态收尾：优雅退出宽限内进程已自行退出");
                    return Reply(PMDsCoordinatorOutcome.Applied,
                        "终态收尾：认证正常退出的进程在宽限内自行退出（全程未请求强杀），已释放端口与名册");
                }

                if (now < _gracefulExitDeadlineMilliseconds)
                {
                    _counters.GracefulExitWaits++;
                    return Reply(PMDsCoordinatorOutcome.Ignored,
                        "终态收尾：等待认证正常退出的优雅退出宽限（剩余 "
                        + Math.Max(0L, _gracefulExitDeadlineMilliseconds - now)
                        + "ms；宽限内不请求强杀，端口与名册未释放）");
                }

                // 宽限到期：转入异常收尾强杀（可观测，并记入结束原因）。
                _awaitingGracefulExit = false;
                _counters.GracefulExitTimeouts++;
                LastReason = (LastReason == null ? string.Empty : LastReason + "；")
                    + "异常收尾：优雅退出宽限（" + _options.GracefulExitGracePeriod.TotalSeconds
                    + " 秒）到期进程仍未退出，已请求强杀";
                RequestKill("优雅退出宽限到期（" + _options.GracefulExitGracePeriod.TotalSeconds
                    + " 秒）：认证正常退出后进程仍未退出，按异常收尾强杀");
                _counters.DeferredCleanupTicks++;
                return Reply(PMDsCoordinatorOutcome.Applied,
                    "优雅退出宽限到期，已请求异常收尾强杀（端口与名册仍未释放，等待本地确认退出）");
            }

            if (TryFinishTerminalCleanup("终态收尾：进程已确认退出", true, true))
            {
                return _process == null
                    ? Reply(PMDsCoordinatorOutcome.Applied, "终态收尾：无进程视图，已释放端口与名册")
                    : Reply(PMDsCoordinatorOutcome.Applied, "终态收尾：确认进程退出后释放端口与名册");
            }

            _counters.DeferredCleanupTicks++;
            return Reply(PMDsCoordinatorOutcome.Ignored,
                "终态收尾：进程仍在运行（已发 " + _counters.KillRequests + " 次强杀请求），等待退出或重试间隔（端口与名册未释放）");
        }

        /// <summary>
        /// 尝试完成终态收尾：**只有确认（或无）进程已退出才释放**端口/名册，返回是否已完成。
        /// 尚未退出时按需节流重试强杀，并保持资源占用。
        ///
        /// 注意：对端的显式 <c>Exited</c> 消息也走这里 —— 它把「对端已收尾」这一事实置位
        /// （<see cref="PeerExitObserved"/>），但**不能替代**本地确认：对端说走了而本地仍观测到它占着端口时，
        /// 我们宁可继续收尾，也不把端口还给共享池。
        /// </summary>
        /// <param name="allowBlockingWait">
        /// 是否允许调用一次有界的 <see cref="IPMDsProcess.WaitForExit"/>（默认 true）。
        /// 认证正常退出的宽限路径传 false：宿主 pump 是单线程的，宽限内不能因 500ms 等待而卡住。
        /// </param>
        private bool TryFinishTerminalCleanup(string reason, bool allowKillRetry, bool allowBlockingWait)
        {
            bool exited = _process == null || !_process.IsRunning;
            if (!exited && allowBlockingWait)
            {
                exited = WaitForProcessExit(_options.ProcessExitConfirmWait);
            }

            if (exited)
            {
                _awaitingGracefulExit = false;
                _awaitingProcessExit = false;
                ReleaseResources(reason);
                return true;
            }

            if (allowKillRetry && NowUnixMilliseconds >= _nextKillRetryMilliseconds)
            {
                RequestKill(reason + "：进程未退出，节流重试强杀");
            }

            return false;
        }

        /// <summary>
        /// 有界等待「进程已退出」。返回 true 才允许释放端口/名册。
        /// 优先用 <see cref="IPMDsProcess.WaitForExit"/>（真实适配器会真的等）；
        /// 替身/无进程场景退化为一次状态查询，因此测试不需要 sleep。
        /// </summary>
        private bool WaitForProcessExit(TimeSpan wait)
        {
            if (_process == null)
            {
                return true;
            }

            if (!_process.IsRunning)
            {
                return true;
            }

            try
            {
                if (_process.WaitForExit((int)Math.Max(0d, wait.TotalMilliseconds)))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // 等待本身失败也不静默：下面用一次状态查询兜底，并保持「不确认就不释放」。
            }

            return !_process.IsRunning;
        }

        private void ReleaseResources(string reason)
        {
            if (_resourcesReleased)
            {
                return;
            }

            _resourcesReleased = true;
            _awaitingProcessExit = false;
            _awaitingGracefulExit = false;
            int port = _port;
            if (port != 0 && _ports.Release(port))
            {
                _counters.ResourcesReleased++;
            }

            _port = 0;

            // 「终态释放玩家占用」：跨会话 uid 账本与端口**同点**释放（R3-B，契约 §3/§7.3）。
            // 只有确认进程已退出才会走到这里（见 TryFinishTerminalCleanup），
            // 因此不存在「uid 已经能重新入局、而旧 DS 还占着它」的窗口。
            if (_playerLedger != null && !string.IsNullOrEmpty(_matchId))
            {
                _playerLedger.Release(_matchId);
            }

            // 「终态释放玩家占用」：名册与 uid 映射一并清掉。
            _uidToRosterIndex.Clear();
            _roster = new PMDsRosterIdentity[0];
            _expectedBootstrapFilePath = string.Empty;

            if (_issuer != null)
            {
                _issuer.RevokeAll();
            }

            if (_process != null)
            {
                try
                {
                    _process.Dispose();
                }
                catch (Exception)
                {
                }

                _process = null;
            }

            Emit(new PMDsCoordinatorEvent
            {
                Effect = PMDsCoordinatorEffect.ResourcesReleased,
                State = _state,
                Port = port,
                Reason = reason == null ? "端口与名册已释放" : "端口与名册已释放（" + reason + "）",
            });
        }

        /// <summary>
        /// 请求强杀。**不再是一次性闩锁**：每次调用都真实尝试，失败会被计数并写进事件 Reason，
        /// 同时把下一次允许请求的时刻推到 <c>now + KillRetryInterval</c>（由调用方按需重试）。
        /// </summary>
        /// <returns>是否成功发出强杀请求（进程已不存在也算成功）。</returns>
        private bool RequestKill(string reason)
        {
            _counters.KillRequests++;
            _nextKillRetryMilliseconds = NowUnixMilliseconds
                + (long)_options.KillRetryInterval.TotalMilliseconds;

            bool killed = true;
            string failure = null;
            if (_process != null)
            {
                try
                {
                    _process.Kill();
                }
                catch (Exception ex)
                {
                    killed = false;
                    failure = ex.GetType().Name + ": " + ex.Message;
                }
            }

            if (!killed)
            {
                _counters.KillRequestFailures++;
            }

            Emit(new PMDsCoordinatorEvent
            {
                Effect = PMDsCoordinatorEffect.ProcessKillRequested,
                State = _state,
                Port = _port,
                ProcessId = _processId,
                Reason = killed
                    ? reason
                    : reason + "（强杀失败，将节流重试：" + failure + "）",
            });

            return killed;
        }

        private byte[] SignControl(PMDsControlMessageType type, ulong requestId, PMDsControlBody body)
        {
            PMDsControlMessage message = PMDsControlMessage.Create(
                type, _matchId, _dsId, _epoch, _protocolHash, requestId, body);
            return _signer.Sign(message);
        }

        /// <summary>
        /// 计算结算摘要的**诊断**指纹（胜方 + 摘要的 SHA-256 前 4 字节）。
        ///
        /// ⚠ 它**不参与幂等/冲突判定**：4 字节摘要存在可构造碰撞（2^32 生日界），
        /// 拿它当「同 ID 不同内容」的唯一判据会把不同内容静默当成重复。
        /// 现在的判定一律用 winner + summary 的完整字节比对（<see cref="PMDsResultTombstoneLedger.SummaryEquals"/>）；
        /// 本方法只给日志/报告/墓碑诊断字段用。
        /// </summary>
        private uint ComputeResultContentHash(PMDsResultBody body)
        {
            byte[] summary = body.Summary ?? new byte[0];
            byte[] buffer = new byte[4 + summary.Length];
            buffer[0] = (byte)(body.WinnerTeamId & 0xFF);
            buffer[1] = (byte)((body.WinnerTeamId >> 8) & 0xFF);
            buffer[2] = (byte)((body.WinnerTeamId >> 16) & 0xFF);
            buffer[3] = (byte)((body.WinnerTeamId >> 24) & 0xFF);
            if (summary.Length > 0)
            {
                Buffer.BlockCopy(summary, 0, buffer, 4, summary.Length);
            }

            byte[] hash = PMDsCrypto.Sha256(buffer, 0, buffer.Length);
            return (uint)(hash[0] | (hash[1] << 8) | (hash[2] << 16) | (hash[3] << 24));
        }

        private PMDsCoordinatorReply Reply(PMDsCoordinatorOutcome outcome, string detail)
        {
            PMDsCoordinatorReply reply = new PMDsCoordinatorReply();
            reply.Outcome = outcome;
            reply.State = _state;
            reply.Detail = detail;
            return reply;
        }

        private void EmitControl(PMDsCoordinatorEffect effect, byte[] payload, PMDsControlMessageType type, string reason)
        {
            Emit(new PMDsCoordinatorEvent
            {
                Effect = effect,
                State = _state,
                Port = _port,
                ProcessId = _processId,
                ControlPayload = payload,
                ControlType = type,
                Reason = reason,
            });
        }

        private void Emit(PMDsCoordinatorEvent effect)
        {
            _counters.EffectsEmitted++;
            try
            {
                _sink.OnEffect(effect);
            }
            catch (Exception)
            {
                // 宿主回调异常不得让状态机停摆（否则一个日志 bug 就能卡住整局回收）。
            }
        }
    }
}
