using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace PMNet.Control
{
    /// <summary>
    /// Lobby 宿主的可注入配置（R3-B 契约 §7.3）。
    ///
    /// 全部字段都允许由配置/环境变量注入：**不猜**固定用户目录、不硬编码端口。
    /// 其中 <see cref="DsExecutablePath"/> / <see cref="DsWorkingDirectory"/> /
    /// <see cref="BootstrapRootDirectory"/> 为空时，新链**不会**被启用（避免拿一个猜出来的路径
    /// 去启动真实进程）。
    /// </summary>
    public sealed class PMDsLobbyHostOptions
    {
        /// <summary>
        /// 是否启用 DS 宿主。
        /// 旧战斗链退役后本字段在 production 恒为 true（<see cref="FromEnvironment"/> 不再读
        /// <c>HYLD_PMNET_DS</c>）；保留它只为测试可以显式禁用宿主，
        /// 此时匹配会**显式失败**，不回退旧链。
        /// </summary>
        public bool Enabled;

        /// <summary>
        /// 旧选链开关 <c>HYLD_PMNET_DS</c> 是否被显式设置过。
        /// 它**不再参与选链**（Enabled 恒为 true，旧链已退役）；只为启动时打一条明确的忽略日志。
        /// </summary>
        public bool DeprecatedLegacySwitchIgnored;

        /// <summary>被忽略的旧开关原始值（仅用于日志）。</summary>
        public string DeprecatedLegacySwitchValue = string.Empty;

        /// <summary>控制通道监听地址（首版只允许 loopback）。</summary>
        public string ListenAddress = "127.0.0.1";

        /// <summary>控制通道监听端口；0 表示由系统分配（测试用）。</summary>
        public int ControlPort = PMDsControlWire.DefaultControlPort;

        /// <summary>DS 可执行文件绝对路径（配置白名单；不从客户端输入取）。</summary>
        public string DsExecutablePath = string.Empty;

        /// <summary>DS 工作目录绝对路径。</summary>
        public string DsWorkingDirectory = string.Empty;

        /// <summary>每局引导文件的父目录（引导文件写在该目录下的每局子目录里）。</summary>
        public string BootstrapRootDirectory = string.Empty;

        /// <summary>全局共享端口池下界。</summary>
        public int PortRangeFirst = 7801;

        /// <summary>全局共享端口池上界。</summary>
        public int PortRangeLast = 7899;

        /// <summary>协议摘要（由 <c>PMR3Runtime.ProtocolHash</c> 提供，宿主只透传）。</summary>
        public uint ProtocolHash;

        /// <summary>碰撞配置摘要（由 <c>PMR3Runtime.CollisionDigest</c> 提供，宿主只透传）。</summary>
        public uint CollisionDigest;

        /// <summary>名册人数上限（契约 §2：最多 6 人）。</summary>
        public int MaxRosterPlayers = PMDsControlWire.MaxRosterPlayers;

        /// <summary>泵循环间隔（毫秒）。宿主线程按此节拍 drain + Tick，不阻塞等 socket。</summary>
        public int PumpIntervalMilliseconds = 50;

        /// <summary>未通过身份/MAC 校验的连接最多允许存活多久（毫秒），超时由泵关闭。</summary>
        public int UnboundConnectionTimeoutMilliseconds = 20000;

        /// <summary>是否启动后台泵线程（测试可关掉，改为手工 <c>PumpOnce</c>）。</summary>
        public bool RunBackgroundPumpThread = true;

        /// <summary>启动超时（透传给协调器，测试可注入更短的值）。</summary>
        public TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

        /// <summary>心跳超时（透传给协调器）。</summary>
        public TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(15);

        /// <summary>结果确认重发间隔（透传给协调器）。</summary>
        public TimeSpan ResultAckRetryInterval = TimeSpan.FromSeconds(1);

        /// <summary>收尾宽限期（透传给协调器）。</summary>
        public TimeSpan ShutdownGracePeriod = TimeSpan.FromSeconds(10);

        /// <summary>进程退出确认等待（透传给协调器）。</summary>
        public TimeSpan ProcessExitConfirmWait = TimeSpan.FromMilliseconds(500);

        /// <summary>强杀重试间隔（透传给协调器）。</summary>
        public TimeSpan KillRetryInterval = TimeSpan.FromSeconds(1);

        /// <summary>新链是否具备启动一局的最小配置（不满足时拒绝开局，而不是猜路径）。</summary>
        public bool IsLaunchConfigured
        {
            get
            {
                return !string.IsNullOrEmpty(DsExecutablePath)
                    && !string.IsNullOrEmpty(DsWorkingDirectory)
                    && !string.IsNullOrEmpty(BootstrapRootDirectory)
                    && PortRangeFirst > 0
                    && PortRangeLast >= PortRangeFirst;
            }
        }

        /// <summary>
        /// T-LOOP1/T-LOOP4：原局断线续玩的 **Lobby 侧宽限**（默认 30 秒）。
        ///
        /// 从「该 uid 在大厅 TCP 上真实断开」那一刻起算；窗口内才允许为它重签**新 Nonce**票。
        /// 窗口到期仍未连回则走旧断线路径（请求 DS 收尾，不伪造胜负），并拒后续续局请求。
        /// 两端（Lobby TCP 与 DS UDP）**各自独立计时**，取保守并集：任一端过期即可拒。
        /// </summary>
        public TimeSpan ReconnectWindow = TimeSpan.FromSeconds(30);

        /// <summary>
        /// T-LOOP1/T-LOOP4：已终局对局**只读结果摘要**的缓存 TTL（默认 120 秒）。
        ///
        /// 只由**已验证**的 DS <c>ResultAccepted</c> 产生，登录时以 <c>PMDS-END1</c> 通知同步返回。
        /// 它是**进程内内存缓存**，Lobby 重启即丢（明确非持久化承诺，当前无数据库）；
        /// 它只承载 matchId/winner/localTeam 三个公开字段，不含任何帧历史。
        /// </summary>
        public TimeSpan MatchEndedNoticeTtl = TimeSpan.FromSeconds(120);

        /// <summary>已终局结果摘要缓存的条数上限（有界，超出即淘汰最旧的一条）。</summary>
        public int MaxMatchEndedNotices = 64;

        /// <summary>
        /// 从环境变量装载配置。**不提供任何猜测出来的默认路径**：
        /// 未显式配置 exe/工作目录/引导目录时，宿主不会启动（匹配显式失败，而不是猜路径）。
        ///
        /// 变量清单：
        /// - <c>HYLD_PMNET_DS_EXE</c> / <c>HYLD_PMNET_DS_WORKDIR</c> / <c>HYLD_PMNET_DS_BOOTSTRAP_DIR</c>；
        /// - <c>HYLD_PMNET_DS_PORT_FIRST</c> / <c>HYLD_PMNET_DS_PORT_LAST</c>；
        /// - <c>HYLD_PMNET_DS_CONTROL_ADDR</c> / <c>HYLD_PMNET_DS_CONTROL_PORT</c>。
        ///
        /// 注意（旧链退役）：<c>HYLD_PMNET_DS</c> 已**不再是选链开关**。旧战斗链已删除，
        /// 因此 Enabled 恒为 true；显式写 0 也不会恢复旧链，只会记录一条被忽略的日志。
        /// </summary>
        public static PMDsLobbyHostOptions FromEnvironment()
        {
            PMDsLobbyHostOptions options = new PMDsLobbyHostOptions();

            // 新链是唯一链路：不再有任何开关能选回旧链。
            options.Enabled = true;
            string legacySwitch = Environment.GetEnvironmentVariable("HYLD_PMNET_DS");
            if (!string.IsNullOrEmpty(legacySwitch))
            {
                options.DeprecatedLegacySwitchIgnored = true;
                options.DeprecatedLegacySwitchValue = legacySwitch;
            }

            options.DsExecutablePath = ReadString("HYLD_PMNET_DS_EXE", string.Empty);
            options.DsWorkingDirectory = ReadString("HYLD_PMNET_DS_WORKDIR", string.Empty);
            options.BootstrapRootDirectory = ReadString("HYLD_PMNET_DS_BOOTSTRAP_DIR", string.Empty);
            options.ListenAddress = ReadString("HYLD_PMNET_DS_CONTROL_ADDR", options.ListenAddress);
            options.ControlPort = ReadInt("HYLD_PMNET_DS_CONTROL_PORT", options.ControlPort);
            options.PortRangeFirst = ReadInt("HYLD_PMNET_DS_PORT_FIRST", options.PortRangeFirst);
            options.PortRangeLast = ReadInt("HYLD_PMNET_DS_PORT_LAST", options.PortRangeLast);
            return options;
        }

        private static string ReadString(string name, string fallback)
        {
            string value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        private static int ReadInt(string name, int fallback)
        {
            string value = Environment.GetEnvironmentVariable(name);
            int parsed;
            if (!string.IsNullOrEmpty(value) && int.TryParse(value, out parsed))
            {
                return parsed;
            }

            return fallback;
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // 客户端网关（Lobby → 已认证客户端）
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 入局通知的字段集合。
    ///
    /// ⚠ **它不是 <c>PMDsEntryOffer</c> 的替身**：本类型只承载宿主产出的字段，
    /// 生产实现必须用冻结的 <c>PMNet.Session.PMDsEntryCodec.Encode</c> 把这些字段编码成
    /// <c>MainPack.Str</c>。把它做成「另一个 offer 实现」会绕过共享契约，本类刻意只做透传。
    /// </summary>
    public sealed class PMDsLobbyEntryNotice
    {
        /// <summary>对局 ID。</summary>
        public string MatchId = string.Empty;

        /// <summary>DS ID。</summary>
        public string DsId = string.Empty;

        /// <summary>DS 主机（首版 loopback）。</summary>
        public string Host = string.Empty;

        /// <summary>DS 实际监听端口（= Allocate 后的真实端口）。</summary>
        public int Port;

        /// <summary>会话世代。</summary>
        public uint Epoch;

        /// <summary>协议摘要。</summary>
        public uint ProtocolHash;

        /// <summary>碰撞配置摘要。</summary>
        public uint CollisionDigest;

        /// <summary>该玩家在名册中的身份。</summary>
        public PMDsRosterIdentity Identity;

        /// <summary>该玩家的入场票据原始字节（**秘密材料**，生产实现不得写日志）。</summary>
        public byte[] Ticket = new byte[0];

        /// <summary>
        /// T-LOOP1/T-LOOP4：这条通知是**原局续玩**（<c>PMDSR1:</c>）而不是初次入局（<c>PMDS1:</c>）。
        /// 它只决定客户端的恢复路径/有界握手重试，**不授予权限**：是否允许入局仍由 DS 独立验票、
        /// 名册与端点账本裁决。字段与初次入局完全相同，只是换了文本前缀。
        /// </summary>
        public bool IsResume;
    }

    /// <summary>结算结果通知的字段集合（**不含**帧历史，不伪造 BattleReview）。</summary>
    public sealed class PMDsLobbyResultNotice
    {
        /// <summary>对局 ID。</summary>
        public string MatchId = string.Empty;

        /// <summary>DS ID。</summary>
        public string DsId = string.Empty;

        /// <summary>会话世代。</summary>
        public uint Epoch;

        /// <summary>结果 ID。</summary>
        public ulong ResultId;

        /// <summary>胜方队伍。</summary>
        public int WinnerTeamId;

        /// <summary>结算摘要字节（可为空）。</summary>
        public byte[] Summary = new byte[0];

        /// <summary>名册快照（宿主自己的副本）。</summary>
        public PMDsRosterIdentity[] Roster = new PMDsRosterIdentity[0];
    }

    /// <summary>
    /// T-LOOP1/T-LOOP4：已终局对局的**只读**结果通知（无秘密：matchId / winner / localTeam）。
    ///
    /// 它**不是**战斗回放，也**不得**据此伪造旧 <c>BattleReview</c> 帧历史（回放属 R6）。
    /// 它只有一个用途：让「打完一局后重新登录」的客户端能看见该局已结束的可信胜负并留在大厅，
    /// **不**创建 DS 会话、**不**恢复输入/战斗。
    /// </summary>
    public sealed class PMDsLobbyEndedNotice
    {
        /// <summary>对局 ID（非空；UTF-8 后 base64 进线格式）。</summary>
        public string MatchId = string.Empty;

        /// <summary>冻结的胜方队伍（来自已验证的 DS ResultAccepted；未改判）。</summary>
        public int WinnerTeamId;

        /// <summary>本 uid 在该局名册里的队伍（客户端据此判「胜/负/平」）。</summary>
        public int LocalTeamId;

        /// <summary>本缓存条目的过期时刻（UTC 毫秒；诊断用，不参与线格式）。</summary>
        public long ExpiresAtUnixMilliseconds;

        /// <summary>线格式文本（无秘密，可放进登录响应的 <c>MainPack.Str</c>）。</summary>
        public string ToText()
        {
            return PMDsLobbyEndedCodec.Encode(this);
        }

        /// <summary>诊断串：只给公开字段，不含票据/密钥（本类型本身也不携带它们）。</summary>
        public override string ToString()
        {
            return "ended(match=" + (MatchId ?? string.Empty)
                + " winner=" + WinnerTeamId + " localTeam=" + LocalTeamId
                + " exp=" + ExpiresAtUnixMilliseconds + ")";
        }
    }

    /// <summary>
    /// T-LOOP1 冻结的「已结束对局」通知文本格式：
    /// <code>PMDS-END1:&lt;base64(matchId UTF8)&gt;:&lt;winner&gt;:&lt;localTeam&gt;</code>
    ///
    /// **编码委托给两端共享的 <c>PMNet.Session.PMDsEndedNoticeCodec</c>**（不再另写一份）：
    /// 这条文本的**解码端在客户端**（大厅 TCP → 登录响应），如果生产端自己写一套边界口径
    /// （标识上限 / 非负 team / 文本上限 / 规范 Base64），两侧就会各自漂移，
    /// 而漂移的表现是「Lobby 发了客户端必然拒收的文本」。
    /// 本类只保留 Lobby 侧的字段集合与「参数不合法即抛」的旧语义。
    /// </summary>
    public static class PMDsLobbyEndedCodec
    {
        /// <summary>文本前缀（与共享 codec 同源，避免两处各写一份常量）。</summary>
        public const string Prefix = PMNet.Session.PMDsEndedNoticeCodec.Prefix;

        /// <summary>
        /// 编码为冻结线格式。**参数不合法即抛**（调用方是 Lobby，不是对端）。
        /// 只编码公开结论；绝不编码票据/密钥/帧历史。
        /// </summary>
        public static string Encode(PMDsLobbyEndedNotice notice)
        {
            string text;
            string error;
            if (!TryEncode(notice, out text, out error))
            {
                throw new ArgumentException(error, "notice");
            }

            return text;
        }

        /// <summary>
        /// 非抛出编码（登录响应路径用）。
        ///
        /// 为什么需要它：本类的字段来自**远端结果**（DS 的 Result 只拒 WinnerTeamId &lt; -1），
        /// 一个不可信字段（例：伪造的负 winner）只应让这条只读通知**缺席**，
        /// 绝不能把异常抛进登录响应，也绝不能发一条客户端必然拒收的文本。
        /// </summary>
        public static bool TryEncode(PMDsLobbyEndedNotice notice, out string text, out string error)
        {
            text = null;
            error = null;

            if (notice == null)
            {
                error = "已结束对局通知为空";
                return false;
            }

            PMNet.Session.PMDsEndedNotice shared = new PMNet.Session.PMDsEndedNotice();
            shared.MatchId = notice.MatchId;
            shared.WinnerTeamId = notice.WinnerTeamId;
            shared.LocalTeamId = notice.LocalTeamId;
            return PMNet.Session.PMDsEndedNoticeCodec.TryEncode(shared, out text, out error);
        }
    }

    /// <summary>
    /// T-LOOP4：「原局续玩」请求的处理结论（逐 uid，诊断/门禁用）。
    /// 每个取值都对应一个**可达的负向分支**，测试靠专有计数器证明它确实走到了。
    /// </summary>
    public enum PMDsLobbyResumeOutcome : byte
    {
        /// <summary>已**重签新 Nonce 票**并推送（本断线 episode 的首次签发）。</summary>
        Issued = 0,

        /// <summary>同一 episode 的重复请求：复用已签发的同一张票，**不滚票**。</summary>
        Reused = 1,

        /// <summary>uid &lt;= 0：无效身份（UID0 不得取新票）。</summary>
        RejectedUidInvalid = 2,

        /// <summary>该 uid 没有被任何未终局会话占用（没局可续，属正常情况）。</summary>
        RejectedNoSession = 3,

        /// <summary>该局已终局/已受理结果：终局不可逆，不发票也不恢复输入。</summary>
        RejectedTerminal = 4,

        /// <summary>会话不是非终局 Running（启动中/就绪中/结果中）：没有可续玩的权威会话。</summary>
        RejectedNotRunning = 5,

        /// <summary>uid 不在该局**可信名册**内（不按请求自报身份签发）。</summary>
        RejectedNotInRoster = 6,

        /// <summary>没有该 uid 的**真实断线**记录：未断线的在线会话不得借续局顶号。</summary>
        RejectedNotDisconnected = 7,

        /// <summary>断线续玩窗口已过期（任一端过期即可拒，不允许借另一端宽限延长）。</summary>
        RejectedWindowExpired = 8,

        /// <summary>当前没有该 uid 的已认证连接（登录未真正落地 / 密码校验未通过）。</summary>
        RejectedNotAuthenticated = 9,

        /// <summary>重签票据本身失败（会话已不在可签状态等）。</summary>
        RejectedReissueFailed = 10,

        /// <summary>
        /// 已签发但 offer 投递失败（不中止该局，等下一次登录重试）。
        /// </summary>
        RejectedOfferUndeliverable = 11,

        /// <summary>
        /// 该局已被 Lobby 判定中止（断线中止/窗口到期未连回）。
        ///
        /// 为什么需要这个单独分支：<c>RequestShutdown</c> 只是「已请求收尾」，会话状态在 DS 进程
        /// **真正退出之前仍然是 Running**。若不把「已判定中止」记下来，一段迟到的断线通知就能在
        /// 被弃局上再开一个 30 秒窗口，让已过期局重新拿到新票（等于跳过 Forfeit）。
        /// </summary>
        RejectedSessionAborting = 12,
    }

    /// <summary>
    /// 客户端网关：宿主与大厅 TCP 之间的**唯一**缝。
    ///
    /// 为什么要有这个接口：<see cref="PMDsLobbyHost"/> 必须能脱离完整 Server 类型被测试
    /// （契约 §5「有界测试宿主（非Unity）」），而「找到当前已认证的 Client 并发 StartEnterBattle」、
    /// 「结果通知」、「恢复 PlayerOnline」都是大厅侧的事。生产实现放在 Server 侧，
    /// **必须**用冻结的 <c>PMDsEntryCodec.Encode</c> 编码。
    /// </summary>
    public interface IPMDsLobbyClientGateway
    {
        /// <summary>该 uid 当前是否存在**已认证**的活跃连接。</summary>
        bool IsClientAuthenticated(int uid);

        /// <summary>
        /// 把入局通知发给该 uid 当前已认证的连接。返回 false 时必须给出原因（不含秘密）。
        /// 实现不得把 offer / MainPack.Str / 完整票据写入日志。
        /// </summary>
        bool TrySendEntryOffer(PMDsLobbyEntryNotice notice, out string error);

        /// <summary>
        /// 明确的对局结束通知（可选）。**不得**在此伪造 BattleReview 帧历史（回放属 R6）。
        /// 允许是空实现。
        /// </summary>
        void NotifyMatchEnded(PMDsLobbyResultNotice notice);

        /// <summary>确认 DS 进程退出、资源已释放之后，把参战玩家恢复为在线状态。</summary>
        void RestorePlayerOnline(int uid);
    }

    // ────────────────────────────────────────────────────────────────────
    // 匹配请求 / 返回
    // ────────────────────────────────────────────────────────────────────

    /// <summary>一次新链开局请求（由匹配层构造成员名册之后交给宿主）。</summary>
    public sealed class PMDsLobbyMatchRequest
    {
        /// <summary>对局 ID（非空，≤128 字节）。</summary>
        public string MatchId = string.Empty;

        /// <summary>名册（1..6 人；必须与**已认证活跃客户端**一致，缺人整局拒绝）。</summary>
        public PMDsRosterIdentity[] Roster = new PMDsRosterIdentity[0];

        /// <summary>赛制描述（诊断用，不参与协议）。</summary>
        public string FightPattern = string.Empty;

        /// <summary>碰撞摘要覆盖（0 = 用宿主配置）。</summary>
        public uint CollisionDigest;

        /// <summary>业务请求 ID（诊断用）。</summary>
        public long RequestId;
    }

    /// <summary>开局请求的处理结论。</summary>
    public enum PMDsLobbyStartOutcome : byte
    {
        /// <summary>已接受并入队，将在泵循环里落地（分配 → 写引导文件 → 启动）。</summary>
        Queued = 0,

        /// <summary>宿主未运行 / 新链未启用。</summary>
        RejectedNotRunning = 1,

        /// <summary>请求结构非法（空 matchId / 空名册 / 超上限 / uid 重复）。</summary>
        RejectedMalformed = 2,

        /// <summary>名册里有人不在线/未认证 —— **整局拒绝，不缩编**。</summary>
        RejectedOffline = 3,

        /// <summary>名册里有人已被其它对局占用。</summary>
        RejectedPlayerOccupied = 4,

        /// <summary>启动所需配置缺失（exe / 工作目录 / 引导目录 / 端口范围）。</summary>
        RejectedNotConfigured = 5,
    }

    /// <summary>开局请求的即时回执。</summary>
    public struct PMDsLobbyStartReply
    {
        /// <summary>结论。</summary>
        public PMDsLobbyStartOutcome Outcome;

        /// <summary>原因（不含秘密）。</summary>
        public string Detail;

        /// <summary>是否已入队（不代表已经启动成功）。</summary>
        public bool IsQueued { get { return Outcome == PMDsLobbyStartOutcome.Queued; } }

        public override string ToString()
        {
            return Outcome + (string.IsNullOrEmpty(Detail) ? string.Empty : "（" + Detail + "）");
        }
    }

    /// <summary>一局真正启动成功后的公开记录（供匹配层对账/日志）。</summary>
    public struct PMDsLobbySessionRecord
    {
        /// <summary>对局 ID。</summary>
        public string MatchId;

        /// <summary>DS ID。</summary>
        public string DsId;

        /// <summary>会话世代。</summary>
        public uint Epoch;

        /// <summary>真实分配端口。</summary>
        public int Port;

        /// <summary>进程 ID。</summary>
        public int ProcessId;

        public override string ToString()
        {
            return "match=" + MatchId + " ds=" + DsId + " epoch=" + Epoch
                + " port=" + Port + " pid=" + ProcessId;
        }
    }

    /// <summary>
    /// Lobby 宿主（R3-B，契约 §7.3）：**唯一**串行调度所有 <see cref="PMDsCoordinator"/> 的所有者。
    ///
    /// ## 线程模型（关键约束）
    /// - socket 线程（<see cref="PMDsControlListener"/> 的连接 worker）**只入队**，绝不触碰会话状态；
    /// - 匹配层（大厅 TCP 接收回调线程）只调用 <see cref="TryStartMatch"/> / <see cref="NotifyClientDisconnected"/>，
    ///   二者都是「校验 + 入队」，不直接进协调器；
    /// - 泵线程（或测试里的 <see cref="PumpOnce"/>）是**唯一**会调用
    ///   <see cref="PMDsCoordinator"/> 的线程：drain 控制入站 → drain 命令 → 应用后置动作 → 对所有会话 Tick。
    ///
    /// 因此「不要阻塞整个 host 等待外部 socket 读」是结构性的：socket 读在各自 worker 上，
    /// 泵只做有界 drain，绝不等待任何 socket。
    ///
    /// ## 资源所有权
    /// - <see cref="PMDsPortPool"/>：**全局唯一**，注入给每个协调器（否则两局撞端口）；
    /// - <see cref="PMDsPlayerLedger"/>：**全局唯一**，跨会话 uid 占用；
    ///   与端口**同点释放**（确认 DS 进程退出后由协调器释放）。
    ///
    /// ## 引导文件
    /// 路径 = <c>&lt;BootstrapRootDirectory&gt;/&lt;matchId&gt;/bootstrap-&lt;epoch&gt;.bin</c>；
    /// 先写临时文件再原子改名（<c>File.Replace</c>/<c>File.Move</c>），**只有发布成功才 BeginStart**，
    /// 失败即回滚（分配态直接收尾并归还端口/uid）。文件受本机账户边界保护（不额外做 DPAPI）。
    ///
    /// ## 刻意不做
    /// - 不调用 <c>BattleManage</c>、不写 <c>_uidToBattleIds</c>（新链不借用旧路由）；
    /// - 不伪造 BattleReview；不回退旧链（调用方选链一次，失败即失败）；
    /// - 不把密钥/票据写日志。
    /// </summary>
    public sealed class PMDsLobbyHost : IDisposable
    {
        /// <summary>控制通道与 <c>-control</c> 参数的地址分隔符。</summary>
        private const char KeySeparator = '\u0001';

        /// <summary>每次 Pump 最多处理的控制载荷条数（有界，避免单次长饿死 Tick）。</summary>
        private const int InboundBudgetPerPump = 64;

        /// <summary>每次 Pump 最多处理的命令条数。</summary>
        private const int CommandBudgetPerPump = 16;

        /// <summary>未绑定会话的连接最多可堆积的出站帧数。</summary>
        private const int PendingOutboundLimit = 16;

        private readonly PMDsLobbyHostOptions _options;
        private readonly IPMDsClock _clock;
        private readonly IPMDsProcessLauncher _launcher;
        private readonly IPMDsLobbyClientGateway _gateway;
        private readonly PMDsControlListener _listener;
        private readonly PMDsPortPool _ports;
        private readonly PMDsPlayerLedger _ledger = new PMDsPlayerLedger();

        private readonly Dictionary<string, LobbySession> _sessions = new Dictionary<string, LobbySession>(StringComparer.Ordinal);
        private readonly Dictionary<int, long> _connectionOpenedAt = new Dictionary<int, long>();
        private readonly Dictionary<int, LobbySession> _connectionToSession = new Dictionary<int, LobbySession>();
        private readonly ConcurrentQueue<LobbyCommand> _commands = new ConcurrentQueue<LobbyCommand>();
        private readonly List<PMDsLobbyResultNotice> _pendingResultNotices = new List<PMDsLobbyResultNotice>();
        private readonly List<PMDsLobbySessionRecord> _startedSessions = new List<PMDsLobbySessionRecord>();

        /// <summary>
        /// T-LOOP4：按 uid 的「上局已终局只读结果」有界缓存（uid -&gt; 条目）。
        /// 只由已验证的 DS <c>ResultAccepted</c> 写入，带 TTL（默认 120 秒）+ 条数上限。
        /// 与端口池/结果墓碑一样是**进程内**存储：Lobby 重启即丢（不承诺持久化）。
        /// </summary>
        private readonly Dictionary<int, PMDsEndedResultEntry> _endedResults =
            new Dictionary<int, PMDsEndedResultEntry>();

        private readonly object _statusGate = new object();

        private Thread _pumpThread;
        private volatile bool _running;
        private volatile bool _disposed;
        private uint _epochSequence;

        /// <summary>日志出口。默认丢弃；生产由 Server 接到 <c>Logging.Debug.Log</c>。</summary>
        public Action<string> Log;

        /// <summary>一局真正启动成功（含真实端口与 pid）时触发。</summary>
        public event Action<PMDsLobbySessionRecord> SessionStarted;

        /// <summary>一局被拒绝/失败（含原因）时触发。</summary>
        public event Action<string, string> SessionFailed;

        /// <summary>会话终态且资源已释放时触发。</summary>
        public event Action<string, PMDsSessionState, string> SessionReleased;

        /// <summary>
        /// 收到权威结果时触发（控制面结果：ResultId / 胜方 / 摘要）。
        /// **不含**回放帧历史，也不得据此伪造 BattleReview（回放属 R6）。
        /// </summary>
        public event Action<PMDsLobbyResultNotice> ResultAccepted;

        // ── 计数（诊断/门禁证据） ────────────────────────────────────────
        public long InboundFramesDispatched;
        public long InboundFramesDroppedUnknownSession;
        public long InboundFramesDroppedMacFailed;
        public long InboundFramesDroppedMalformed;
        public long HijackAttempts;
        public long SessionStartsAccepted;
        public long SessionStartsRejected;
        public long BootstrapFilesPublished;
        public long BootstrapPublishFailures;
        public long EntryOffersSent;
        public long EntryOfferFailures;
        public long ClientDisconnectAborts;
        public long ControlFramesUndeliverable;
        public long UnboundConnectionsClosedByTimeout;
        public long PumpExceptions;

        // ── T-LOOP4：原局断线续玩（逐分支可达，负向用例靠它们证明「确实走到了该分支」） ──

        /// <summary>断线后**进入续玩窗口**（而非立即中止）的玩家数。</summary>
        public long ClientDisconnectsHeld;

        /// <summary>续玩窗口到期（不论随后是否中止该局）的次数。</summary>
        public long ReconnectWindowsExpired;

        /// <summary>真正轮转 Nonce 重签的新票张数（本 episode 首次签发）。</summary>
        public long ResumeTicketsIssued;

        /// <summary>同一 episode 重复请求而**复用**已签发票的次数（证明「不滚票」）。</summary>
        public long ResumeTicketsReused;

        /// <summary>uid &lt;= 0 被拒（UID0 不得取新票）。</summary>
        public long ResumeRejectedUidInvalid;

        /// <summary>没有可续的原局（正常，不影响匹配）。</summary>
        public long ResumeRejectedNoSession;

        /// <summary>该局已终局/已受理结果而被拒（终局不可逆）。</summary>
        public long ResumeRejectedTerminal;

        /// <summary>会话状态不是非终局 Running 而被拒。</summary>
        public long ResumeRejectedNotRunning;

        /// <summary>uid 不在可信名册内而被拒。</summary>
        public long ResumeRejectedNotInRoster;

        /// <summary>没有该 uid 的真实断线记录而被拒（未断线在线不得顶号）。</summary>
        public long ResumeRejectedNotDisconnected;

        /// <summary>续玩窗口已过期而被拒。</summary>
        public long ResumeRejectedWindowExpired;

        /// <summary>该局已被判定中止（已请求收尾）而被拒。</summary>
        public long ResumeRejectedSessionAborting;

        /// <summary>当前没有已认证连接而被拒。</summary>
        public long ResumeRejectedNotAuthenticated;

        /// <summary>重签票据失败而被拒。</summary>
        public long ResumeRejectedReissueFailed;

        /// <summary>已签发但 offer 投递失败。</summary>
        public long ResumeOfferSendFailures;

        /// <summary>写入只读结果缓存的条目数（按 uid 计）。</summary>
        public long MatchEndedNoticesRecorded;

        /// <summary>被登录路径取走（返回给客户端）的只读结果通知次数。</summary>
        public long MatchEndedNoticesServed;

        /// <summary>
        /// 因为字段不可信（例：伪造的负 winner）而**无法编码**、被 fail-closed 丢弃的只读结果通知次数。
        /// 它必须可观测：这条分支意味着「本可以显示的胜负被安全地藏起来了」，不是静默无处理。
        /// </summary>
        public long MatchEndedNoticesEncodeRejected;

        /// <summary>最近一次续局请求的处理结论（诊断；无请求时为 <see cref="PMDsLobbyResumeOutcome.RejectedNoSession"/> 之外的值不可采信）。</summary>
        public PMDsLobbyResumeOutcome LastResumeOutcome;

        /// <summary>最近一次续局请求的 uid（诊断；与 <see cref="LastResumeOutcome"/> 同批有效）。</summary>
        public int LastResumeOutcomeUid;

        /// <summary>已处理过的续局请求总数（含被拒）。</summary>
        public long ResumeRequestsHandled;

        /// <summary>进程内唯一实例（未启动时为 null）。</summary>
        public static PMDsLobbyHost Instance { get; internal set; }

        public PMDsLobbyHost(PMDsLobbyHostOptions options, IPMDsClock clock,
            IPMDsProcessLauncher launcher, IPMDsLobbyClientGateway gateway)
        {
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }

            if (gateway == null)
            {
                throw new ArgumentNullException("gateway");
            }

            _options = options;
            _clock = clock ?? PMDsSystemClock.Instance;
            _launcher = launcher;
            _gateway = gateway;
            _ports = new PMDsPortPool(options.PortRangeFirst, options.PortRangeLast);
            _listener = new PMDsControlListener(options.ListenAddress, options.ControlPort);

            // 配置错误要在启动时炸，不能等到局中（与 PMDsCoordinatorOptions.Validate 同口径）。
            if (options.ReconnectWindow <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException("ReconnectWindow", "续玩窗口必须为正");
            }

            if (options.MatchEndedNoticeTtl <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException("MatchEndedNoticeTtl", "只读结果缓存 TTL 必须为正");
            }

            if (options.MaxMatchEndedNotices < 1)
            {
                throw new ArgumentOutOfRangeException("MaxMatchEndedNotices", "只读结果缓存条数上限必须 >= 1");
            }
        }

        /// <summary>实际控制端口（监听启动后有效）。</summary>
        public int ControlPort { get { return _listener.BoundPort; } }

        /// <summary>全局端口池（测试/诊断用）。</summary>
        public PMDsPortPool PortPool { get { return _ports; } }

        /// <summary>跨会话 uid 占用账本（测试/诊断用）。</summary>
        public PMDsPlayerLedger PlayerLedger { get { return _ledger; } }

        /// <summary>是否已启动。</summary>
        public bool IsRunning { get { return _running; } }

        /// <summary>当前会话数（含延迟收尾中的会话）。</summary>
        public int SessionCount
        {
            get
            {
                lock (_statusGate)
                {
                    return _sessions.Count;
                }
            }
        }

        /// <summary>已成功启动过的会话记录（有界快照）。</summary>
        public PMDsLobbySessionRecord[] StartedSessions
        {
            get
            {
                lock (_statusGate)
                {
                    return _startedSessions.ToArray();
                }
            }
        }

        // ────────────────────────────────────────────────────────────────
        // 生命周期
        // ────────────────────────────────────────────────────────────────

        /// <summary>启动监听与泵线程。</summary>
        public bool Start(out string error)
        {
            error = null;
            if (_running)
            {
                error = "宿主已启动";
                return false;
            }

            if (!_options.IsLaunchConfigured)
            {
                error = "新链启动配置不完整（需要 DS exe / 工作目录 / 引导目录 / 合法端口范围）；"
                    + "本实现不猜路径，拒绝启动";
                return false;
            }

            if (!_listener.Start(out error))
            {
                return false;
            }

            _running = true;
            if (_options.RunBackgroundPumpThread)
            {
                _pumpThread = new Thread(PumpLoop);
                _pumpThread.IsBackground = true;
                _pumpThread.Name = "PMDsLobbyPump";
                _pumpThread.Start();
            }

            Info("PMDsLobbyHost 已启动：control=" + _options.ListenAddress + ":" + _listener.BoundPort
                + " ports=" + _options.PortRangeFirst + "-" + _options.PortRangeLast
                + " bootstrapRoot=" + _options.BootstrapRootDirectory);
            return true;
        }

        /// <summary>停止泵与监听；未终结的会话交给各自的协调器收尾（可能保持资源占用）。</summary>
        public void Stop()
        {
            if (!_running && _pumpThread == null)
            {
                return;
            }

            _running = false;

            Thread pump = _pumpThread;
            if (pump != null)
            {
                try
                {
                    pump.Join(2000);
                }
                catch (Exception)
                {
                }

                _pumpThread = null;
            }

            _listener.Dispose();
            Info("PMDsLobbyHost 已停止");
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _running = false;

            Thread pump = _pumpThread;
            if (pump != null)
            {
                try
                {
                    pump.Join(2000);
                }
                catch (Exception)
                {
                }

                _pumpThread = null;
            }

            _listener.Dispose();

            List<LobbySession> sessions;
            lock (_statusGate)
            {
                sessions = new List<LobbySession>(_sessions.Values);
                _sessions.Clear();
            }

            for (int i = 0; i < sessions.Count; i++)
            {
                try
                {
                    sessions[i].Coordinator.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }

        private void PumpLoop()
        {
            while (_running)
            {
                try
                {
                    PumpOnce();
                }
                catch (Exception ex)
                {
                    PumpExceptions++;
                    Warn("泵循环异常（已隔离，不影响其他会话）：" + ex.GetType().Name + " " + ex.Message);
                }

                int interval = _options.PumpIntervalMilliseconds;
                if (interval < 1)
                {
                    interval = 1;
                }

                Thread.Sleep(interval);
            }
        }

        /// <summary>
        /// 单趟泵：drain 连接事件 → drain 控制入站 → drain 命令 → 应用后置动作 → Tick 所有会话 → 回收。
        /// **不阻塞**在任何 socket 上。测试可直接手工调用它获得确定性。
        /// </summary>
        public void PumpOnce()
        {
            DrainConnectionEvents();
            DrainInbound();
            DrainCommands();
            ApplyDeferredWork();
            TickSessions();
            ExpireReconnectWindows();
            ExpireEndedResults();
            ReapSessions();
        }

        // ────────────────────────────────────────────────────────────────
        // 对外入口（线程安全：只校验 + 入队）
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 提交一次新链开局（匹配层调用，线程安全）。
        ///
        /// 这里做的是「**整局**准入」的快速判定：名册与已认证活跃客户端一致、无人被占用。
        /// 缺任何一人即整局拒绝（**不缩编**），因为「悄悄少一个人」会让名册与客户端认知不一致。
        /// 真正的分配/启动在泵线程上落地。
        /// </summary>
        public PMDsLobbyStartReply TryStartMatch(PMDsLobbyMatchRequest request)
        {
            PMDsLobbyStartReply reply = new PMDsLobbyStartReply();

            if (!_running)
            {
                reply.Outcome = PMDsLobbyStartOutcome.RejectedNotRunning;
                reply.Detail = "宿主未运行";
                SessionStartsRejected++;
                return reply;
            }

            if (!_options.IsLaunchConfigured)
            {
                reply.Outcome = PMDsLobbyStartOutcome.RejectedNotConfigured;
                reply.Detail = "新链启动配置不完整（exe/工作目录/引导目录/端口范围）";
                SessionStartsRejected++;
                return reply;
            }

            if (request == null || string.IsNullOrEmpty(request.MatchId))
            {
                reply.Outcome = PMDsLobbyStartOutcome.RejectedMalformed;
                reply.Detail = "MatchId 不得为空";
                SessionStartsRejected++;
                return reply;
            }

            PMDsRosterIdentity[] roster = request.Roster;
            if (roster == null || roster.Length == 0)
            {
                reply.Outcome = PMDsLobbyStartOutcome.RejectedMalformed;
                reply.Detail = "名册不得为空";
                SessionStartsRejected++;
                return reply;
            }

            if (roster.Length > _options.MaxRosterPlayers)
            {
                reply.Outcome = PMDsLobbyStartOutcome.RejectedMalformed;
                reply.Detail = "名册超过上限：" + roster.Length + " > " + _options.MaxRosterPlayers;
                SessionStartsRejected++;
                return reply;
            }

            try
            {
                PMDsControlCodec.ValidateIdentities(roster, "新链名册");
                // MatchId 的 UTF-8 字节上限在分配前先查，避免「开了局才发现名字太长」。
                PMDsControlCodec.EnsureWithinBytes(request.MatchId, PMDsControlWire.MaxMatchIdBytes, "MatchId");
            }
            catch (PMDsControlProtocolException ex)
            {
                reply.Outcome = PMDsLobbyStartOutcome.RejectedMalformed;
                reply.Detail = ex.Message;
                SessionStartsRejected++;
                return reply;
            }

            for (int i = 0; i < roster.Length; i++)
            {
                int uid = roster[i].Uid;
                if (_ledger.IsOccupied(uid))
                {
                    string owner;
                    _ledger.TryGetMatchId(uid, out owner);
                    reply.Outcome = PMDsLobbyStartOutcome.RejectedPlayerOccupied;
                    reply.Detail = "uid " + uid + " 已被对局占用：" + owner;
                    SessionStartsRejected++;
                    return reply;
                }

                if (!_gateway.IsClientAuthenticated(uid))
                {
                    reply.Outcome = PMDsLobbyStartOutcome.RejectedOffline;
                    reply.Detail = "uid " + uid + " 不在线或未认证（整局拒绝，不缩编）";
                    SessionStartsRejected++;
                    return reply;
                }
            }

            LobbyCommand command = new LobbyCommand();
            command.Kind = LobbyCommandKind.StartMatch;
            command.MatchId = request.MatchId;
            command.FightPattern = request.FightPattern;
            command.CollisionDigest = request.CollisionDigest;
            command.RequestId = request.RequestId;
            command.Roster = (PMDsRosterIdentity[])roster.Clone();
            _commands.Enqueue(command);

            reply.Outcome = PMDsLobbyStartOutcome.Queued;
            reply.Detail = "已入队";
            return reply;
        }

        /// <summary>
        /// 大厅侧检测到客户端断线时调用（线程安全）。
        ///
        /// T-LOOP4 语义（原局断线续玩 v1 冻结接口）：
        /// - 该 uid 在一个**非终局 Running 局**里 ⇒ **不**立即 <c>RequestShutdown</c>，而是从本次
        ///   真实断开起开一个 <see cref="PMDsLobbyHostOptions.ReconnectWindow"/>（默认 30 秒）窗口；
        /// - 其它情形（启动中/就绪中/已受理结果/已终局）沿用旧语义：立即请求 DS 收尾，不伪造正常胜利。
        /// </summary>
        public void NotifyClientDisconnected(int uid)
        {
            NotifyClientDisconnected(uid, 0L);
        }

        /// <summary>携带大厅TCP连接代次；0仅供既有调用/门禁，生产必须给非零代次。</summary>
        public void NotifyClientDisconnected(int uid, long connectionGeneration)
        {
            if (uid <= 0)
            {
                return;
            }

            LobbyCommand command = new LobbyCommand();
            command.Kind = LobbyCommandKind.ClientDisconnected;
            command.Uid = uid;
            command.ConnectionGeneration = connectionGeneration;
            _commands.Enqueue(command);
        }

        /// <summary>
        /// T-LOOP4：同 uid 重新登录（**已通过密码校验并已由 Server.RegisterActiveClient 原子登记**）
        /// 后请求「原局续玩」。
        ///
        /// 调用方只负责「本次登录确实成功且已登记」；其余全部判定在泵线程上 fail-closed 完成：
        /// 非终局 Running 局 + 本局可信名册 + 该 uid **真实断线**且仍在 30 秒窗口内 + 当前有已认证连接。
        ///
        /// 为什么是**异步**：重签票据与 offer 推送必须发生在单线程宿主上（不阻塞大厅接收线程），
        /// 且 offer 与初始入局走完全相同的 <c>StartEnterBattle</c> 网关。调用方不需要等结果：
        /// 没有可续的局时是 no-op（正常匹配流程不受影响），失败分支只记计数器与日志。
        ///
        /// 返回 false 只表示 uid 非法或宿主未运行（即**没有入队**），不代表一定能签发。
        /// </summary>
        public bool TryRequestResumeEntry(int uid)
        {
            if (uid <= 0)
            {
                ResumeRejectedUidInvalid++;
                ResumeRequestsHandled++;
                LastResumeOutcomeUid = uid;
                LastResumeOutcome = PMDsLobbyResumeOutcome.RejectedUidInvalid;
                Warn("续局请求被拒：uid 非法（" + uid + "，UID0 不得取新票）");
                return false;
            }

            if (!_running)
            {
                ResumeRejectedNoSession++;
                ResumeRequestsHandled++;
                LastResumeOutcomeUid = uid;
                LastResumeOutcome = PMDsLobbyResumeOutcome.RejectedNoSession;
                return false;
            }

            LobbyCommand command = new LobbyCommand();
            command.Kind = LobbyCommandKind.ResumeEntry;
            command.Uid = uid;
            _commands.Enqueue(command);
            return true;
        }

        /// <summary>
        /// T-LOOP4：取该 uid **最近一局已终局**的只读结果文本（冻结格式
        /// <c>PMDS-END1:&lt;base64(matchId UTF8)&gt;:&lt;winner&gt;:&lt;localTeam&gt;</c>），
        /// 供登录响应的 <c>MainPack.Str</c> 返回。
        ///
        /// 线程安全、**只读**：不创建任何 DS 会话、不恢复输入/战斗、不碰端口与 uid 占用，
        /// 也**绝不**伪造 <c>BattleReview</c> 帧历史（回放属 R6）。
        /// 缓存只有已验证的 DS ResultAccepted 会写入，且有界（TTL 默认 120 秒 + 条数上限）；
        /// 没有可返回条目（含已过期）时返回 false。
        /// </summary>
        public bool TryGetRecentMatchEndedNotice(int uid, out string text)
        {
            text = null;
            if (uid <= 0)
            {
                return false;
            }

            // 旧结果仍在120s缓存中时，玩家可能已进入一场**新的未终局对局**。
            // 不能在登录响应先显示旧局胜负、同时又自动推送新局续玩票；
            // 当前占用局若仍Running，优先返回新局，不附上局结论。终局/结果待收尾不受此门影响。
            string occupiedMatchId;
            if (_ledger.TryGetMatchId(uid, out occupiedMatchId))
            {
                LobbySession occupied = FindSession(occupiedMatchId);
                if (occupied != null && !occupied.ResultAccepted
                    && occupied.Coordinator != null && occupied.Coordinator.State == PMDsSessionState.Running)
                {
                    return false;
                }
            }

            long now = _clock.UtcNowUnixMilliseconds;
            PMDsEndedResultEntry entry;
            lock (_statusGate)
            {
                if (!_endedResults.TryGetValue(uid, out entry))
                {
                    return false;
                }

                if (entry.ExpiresAtUnixMilliseconds <= now)
                {
                    _endedResults.Remove(uid);
                    return false;
                }
            }

            PMDsLobbyEndedNotice notice = new PMDsLobbyEndedNotice();
            notice.MatchId = entry.MatchId;
            notice.WinnerTeamId = entry.WinnerTeamId;
            notice.LocalTeamId = entry.LocalTeamId;
            notice.ExpiresAtUnixMilliseconds = entry.ExpiresAtUnixMilliseconds;

            // 用**共享 codec 的非抛出版本**编码：字段来自远端结果，不可信字段只能让这条只读通知缺席，
            // 不能把异常抛进登录响应路径（也绝不发一条客户端必然拒收的文本）。
            string encodeError;
            if (!PMDsLobbyEndedCodec.TryEncode(notice, out text, out encodeError))
            {
                MatchEndedNoticesEncodeRejected++;
                Warn("上局只读结果无法编码，已放弃该条通知（uid=" + uid + "）：" + encodeError);
                return false;
            }

            MatchEndedNoticesServed++;
            return true;
        }

        /// <summary>该 uid 是否正被新链的某个会话占用（大厅侧只读查询：已入局的 uid 不应再走任何大厅局内路由）。</summary>
        public bool IsUidInMatch(int uid)
        {
            return _ledger.IsOccupied(uid);
        }

        /// <summary>按对局 ID 查询会话当前状态。</summary>
        public bool TryGetSessionState(string matchId, out PMDsSessionState state)
        {
            state = PMDsSessionState.Idle;
            if (string.IsNullOrEmpty(matchId))
            {
                return false;
            }

            lock (_statusGate)
            {
                foreach (KeyValuePair<string, LobbySession> pair in _sessions)
                {
                    if (string.Equals(pair.Value.MatchId, matchId, StringComparison.Ordinal))
                    {
                        state = pair.Value.Coordinator.State;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>该对局当前是否已绑定控制连接（诊断/门禁用，只读）。</summary>
        public bool HasBoundControlConnection(string matchId)
        {
            if (string.IsNullOrEmpty(matchId))
            {
                return false;
            }

            lock (_statusGate)
            {
                foreach (KeyValuePair<string, LobbySession> pair in _sessions)
                {
                    if (string.Equals(pair.Value.MatchId, matchId, StringComparison.Ordinal))
                    {
                        return pair.Value.ControlConnectionId != 0;
                    }
                }
            }

            return false;
        }

        /// <summary>按对局 ID 查询协调器实例（**只读**用途：诊断/门禁断言）。</summary>
        public PMDsCoordinator GetCoordinator(string matchId)
        {
            if (string.IsNullOrEmpty(matchId))
            {
                return null;
            }

            lock (_statusGate)
            {
                foreach (KeyValuePair<string, LobbySession> pair in _sessions)
                {
                    if (string.Equals(pair.Value.MatchId, matchId, StringComparison.Ordinal))
                    {
                        return pair.Value.Coordinator;
                    }
                }
            }

            return null;
        }

        /// <summary>按对局 ID 找宿主侧会话视图（泵线程内部使用；未找到返回 null）。</summary>
        private LobbySession FindSession(string matchId)
        {
            if (string.IsNullOrEmpty(matchId))
            {
                return null;
            }

            lock (_statusGate)
            {
                foreach (KeyValuePair<string, LobbySession> pair in _sessions)
                {
                    if (string.Equals(pair.Value.MatchId, matchId, StringComparison.Ordinal))
                    {
                        return pair.Value;
                    }
                }
            }

            return null;
        }

        // ────────────────────────────────────────────────────────────────
        // 泵的内部步骤
        // ────────────────────────────────────────────────────────────────

        private void DrainConnectionEvents()
        {
            PMDsControlConnectionEvent evt;
            while (_listener.TryDequeueConnectionEvent(out evt))
            {
                if (evt.Kind == PMDsControlConnectionEventKind.Opened)
                {
                    lock (_statusGate)
                    {
                        _connectionOpenedAt[evt.ConnectionId] = _clock.UtcNowUnixMilliseconds;
                    }

                    continue;
                }

                LobbySession session;
                lock (_statusGate)
                {
                    _connectionOpenedAt.Remove(evt.ConnectionId);
                    if (_connectionToSession.TryGetValue(evt.ConnectionId, out session))
                    {
                        _connectionToSession.Remove(evt.ConnectionId);
                        if (session != null && session.ControlConnectionId == evt.ConnectionId)
                        {
                            // 绑定的控制连接消失 ⇒ 解绑。重连策略：只有在**这一步之后**
                            // 新的连接才允许通过身份/MAC 校验占据该会话的控制连接
                            // （断开期间发生的第二条连接一律按冒用处理，见 HandleInbound）。
                            session.ControlConnectionId = 0;
                            Info("会话 " + session.MatchId + " 的控制连接已断开（id=" + evt.ConnectionId
                                + "，reason=" + (evt.Reason ?? "unknown") + "），等待重连");
                        }
                    }
                }
            }

            // 未认证连接超时：不给对端无限挂起的机会（有界）。
            List<int> idle = null;
            long now = _clock.UtcNowUnixMilliseconds;
            lock (_statusGate)
            {
                foreach (KeyValuePair<int, long> pair in _connectionOpenedAt)
                {
                    if (_connectionToSession.ContainsKey(pair.Key))
                    {
                        continue;
                    }

                    if (now - pair.Value >= _options.UnboundConnectionTimeoutMilliseconds)
                    {
                        if (idle == null)
                        {
                            idle = new List<int>();
                        }

                        idle.Add(pair.Key);
                    }
                }
            }

            if (idle != null)
            {
                for (int i = 0; i < idle.Count; i++)
                {
                    UnboundConnectionsClosedByTimeout++;
                    _listener.CloseConnection(idle[i], "未在超时内通过身份/MAC 校验");
                }
            }
        }

        private void DrainInbound()
        {
            for (int i = 0; i < InboundBudgetPerPump; i++)
            {
                PMDsControlInbound inbound;
                if (!_listener.TryDequeueInbound(out inbound))
                {
                    return;
                }

                HandleInbound(inbound);
            }
        }

        /// <summary>
        /// 处理一条已解帧的控制载荷。
        ///
        /// 选路分两段，且**刻意区分「选路」与「授权」**：
        /// 1. 解出信封身份（MatchId/DsId/Epoch）用来找候选会话 —— 这一步**未认证**，不能据此改状态；
        /// 2. 交给候选协调器 <c>OnControlPayload</c> 做 MAC 校验与状态推进；
        ///    只有在 MAC 通过之后，连接才被允许**占据**该会话的控制连接。
        /// </summary>
        private void HandleInbound(PMDsControlInbound inbound)
        {
            PMDsControlMessage envelope;
            try
            {
                envelope = PMDsControlCodec.Decode(inbound.Payload, 0, inbound.Length);
            }
            catch (Exception)
            {
                // 载荷结构非法：连接不获任何信任。
                _listener.CloseConnection(inbound.ConnectionId, "控制载荷结构非法");
                return;
            }

            string key = SessionKey(envelope.MatchId, envelope.DsId, envelope.Epoch);

            LobbySession session;
            lock (_statusGate)
            {
                _sessions.TryGetValue(key, out session);
            }

            if (session == null)
            {
                InboundFramesDroppedUnknownSession++;
                return;
            }

            // 已经绑定过的连接只能服务于它绑定的会话（防止一条连接改身份顶替别的会话）。
            if (session.ControlConnectionId != 0 && session.ControlConnectionId != inbound.ConnectionId)
            {
                HijackAttempts++;
                Warn("拒绝第二控制连接冒用会话 " + session.MatchId + "：已绑定 id="
                    + session.ControlConnectionId + "，新来 id=" + inbound.ConnectionId);
                _listener.CloseConnection(inbound.ConnectionId, "该会话已有控制连接");
                return;
            }

            PMDsCoordinatorReply reply = session.Coordinator.OnControlPayload(
                inbound.Payload, 0, inbound.Length);

            if (reply.Outcome == PMDsCoordinatorOutcome.RejectedMac)
            {
                InboundFramesDroppedMacFailed++;
                if (session.ControlConnectionId == 0)
                {
                    // 首条消息就没通过 MAC：这条连接不获得会话控制权，直接关闭（fail-closed）。
                    _listener.CloseConnection(inbound.ConnectionId, "首个控制消息未通过 MAC 校验");
                }

                return;
            }

            if (reply.Outcome == PMDsCoordinatorOutcome.RejectedMalformed)
            {
                InboundFramesDroppedMalformed++;
                if (session.ControlConnectionId == 0)
                {
                    // 结构非法（含验签之后的未知消息类型）：同样不允许占据控制连接。
                    _listener.CloseConnection(inbound.ConnectionId, "控制消息结构非法");
                }

                return;
            }

            InboundFramesDispatched++;

            if (session.ControlConnectionId == 0)
            {
                // 身份/MAC 校验通过才允许占据控制连接（契约 §7.3）。
                session.ControlConnectionId = inbound.ConnectionId;
                lock (_statusGate)
                {
                    _connectionToSession[inbound.ConnectionId] = session;
                }

                FlushPendingOutbound(session);
                Info("会话 " + session.MatchId + " 的控制连接已绑定：id=" + inbound.ConnectionId);
            }
        }

        private void DrainCommands()
        {
            for (int i = 0; i < CommandBudgetPerPump; i++)
            {
                LobbyCommand command;
                if (!_commands.TryDequeue(out command))
                {
                    return;
                }

                switch (command.Kind)
                {
                    case LobbyCommandKind.StartMatch:
                        ProcessStartMatch(command);
                        break;

                    case LobbyCommandKind.ClientDisconnected:
                        ProcessClientDisconnected(command.Uid, command.ConnectionGeneration);
                        break;

                    case LobbyCommandKind.ResumeEntry:
                        try
                        {
                            ProcessResumeEntry(command.Uid);
                        }
                        catch (Exception ex)
                        {
                            // 续局是外部登录触发的新路径：单个会话的异常不得把泵剩余步骤吃掉。
                            ResumeRejectedReissueFailed++;
                            LastResumeOutcomeUid = command.Uid;
                            LastResumeOutcome = PMDsLobbyResumeOutcome.RejectedReissueFailed;
                            Warn("续局请求处理异常（uid=" + command.Uid + "）：" + ex.GetType().Name);
                        }

                        break;
                }
            }
        }

        private void ProcessStartMatch(LobbyCommand command)
        {
            PMDsLobbyMatchRequest request = new PMDsLobbyMatchRequest();
            request.MatchId = command.MatchId;
            request.Roster = command.Roster;
            request.FightPattern = command.FightPattern;
            request.CollisionDigest = command.CollisionDigest;
            request.RequestId = command.RequestId;

            uint epoch = _epochSequence + 1u;
            if (epoch == 0u)
            {
                epoch = 1u;
            }

            _epochSequence = epoch;

            string dsId = BuildDsId(request.MatchId, epoch);
            string key = SessionKey(request.MatchId, dsId, epoch);
            string bootstrapPath = BuildBootstrapPath(request.MatchId, epoch);
            uint collisionDigest = request.CollisionDigest != 0u
                ? request.CollisionDigest : _options.CollisionDigest;
            if (collisionDigest == 0u)
            {
                RejectUnregistered(request.MatchId, "碰撞摘要为 0（必须由 PMR3Runtime.CollisionDigest 或调用方提供）");
                return;
            }

            LobbySession session = new LobbySession();
            session.MatchId = request.MatchId;
            session.DsId = dsId;
            session.Epoch = epoch;
            session.Key = key;
            session.Roster = (PMDsRosterIdentity[])request.Roster.Clone();
            session.BootstrapFilePath = bootstrapPath;
            session.CollisionDigest = collisionDigest;
            PMDsCoordinatorOptions coordinatorOptions = new PMDsCoordinatorOptions();
            coordinatorOptions.PortRangeFirst = _options.PortRangeFirst;
            coordinatorOptions.PortRangeLast = _options.PortRangeLast;
            coordinatorOptions.ProtocolHash = _options.ProtocolHash;
            coordinatorOptions.MaxRosterPlayers = _options.MaxRosterPlayers;
            coordinatorOptions.StartTimeout = _options.StartTimeout;
            coordinatorOptions.HeartbeatTimeout = _options.HeartbeatTimeout;
            coordinatorOptions.ResultAckRetryInterval = _options.ResultAckRetryInterval;
            coordinatorOptions.ShutdownGracePeriod = _options.ShutdownGracePeriod;
            coordinatorOptions.ProcessExitConfirmWait = _options.ProcessExitConfirmWait;
            coordinatorOptions.KillRetryInterval = _options.KillRetryInterval;

            PMDsCoordinator coordinator = new PMDsCoordinator(coordinatorOptions, _clock, _launcher,
                new SessionSink(this, session), _ports, _ledger);
            session.Coordinator = coordinator;

            PMDsAllocationRequest allocation = new PMDsAllocationRequest();
            allocation.MatchId = request.MatchId;
            allocation.DsId = dsId;
            allocation.Epoch = epoch;
            allocation.ProtocolHash = _options.ProtocolHash;
            allocation.CollisionDigest = collisionDigest;
            allocation.Roster = (PMDsRosterIdentity[])request.Roster.Clone();
            allocation.BootstrapFilePath = bootstrapPath;
            allocation.Process = new PMDsProcessLaunchRequest(
                _options.DsExecutablePath, _options.DsWorkingDirectory, BuildBaseArguments(dsId, request.MatchId));

            PMDsCoordinatorReply allocated = coordinator.Allocate(allocation);
            if (!allocated.IsAccepted || coordinator.State != PMDsSessionState.Allocated)
            {
                // 注意：协调器的失败路径也会返回 Accepted（它把状态推到终态后返回 Applied），
                // 因此判定必须看**状态**而不是只看 outcome，否则会把「启动失败」误当成成功开局。
                RejectUnregistered(request.MatchId, "分配失败：" + allocated);
                return;
            }

            // 分配成功（端口 + uid 账本已预留）之后才把会话登记进宿主视图。
            // 登记早了会在分配失败时留下一个永远不会被回收的条目。
            lock (_statusGate)
            {
                _sessions[key] = session;
            }

            // 引导文件必须先**安全发布**，然后才能启动进程（因果，而不是调用顺序纪律）。
            byte[] document;
            try
            {
                document = coordinator.ExportBootstrapDocument();
            }
            catch (Exception ex)
            {
                RejectSession(session, "引导内容导出失败：" + ex.GetType().Name);
                return;
            }

            string publishError;
            if (!TryPublishBootstrapFile(bootstrapPath, document, out publishError))
            {
                BootstrapPublishFailures++;
                RejectSession(session, "引导文件发布失败：" + publishError);
                return;
            }

            BootstrapFilesPublished++;

            string controlEndpoint = _options.ListenAddress + ":" + _listener.BoundPort;
            PMDsProcessLaunchRequest finalLaunch = new PMDsProcessLaunchRequest(
                _options.DsExecutablePath, _options.DsWorkingDirectory,
                BuildFinalArguments(dsId, request.MatchId, bootstrapPath, controlEndpoint, coordinator.AllocatedPort));

            PMDsCoordinatorReply launchReply = coordinator.SetLaunchRequest(finalLaunch);
            if (!launchReply.IsAccepted || coordinator.State != PMDsSessionState.Allocated)
            {
                RejectSession(session, "启动参数校验失败：" + launchReply);
                return;
            }

            PMDsCoordinatorReply started = coordinator.BeginStart();
            if (!started.IsAccepted || coordinator.State != PMDsSessionState.Starting)
            {
                // 同上：BeginStart 失败时协调器会把状态推到 Failed 并归还资源，但 outcome 仍是 Applied。
                RejectSession(session, "进程启动失败：" + started);
                return;
            }

            SessionStartsAccepted++;

            PMDsLobbySessionRecord record = new PMDsLobbySessionRecord();
            record.MatchId = request.MatchId;
            record.DsId = dsId;
            record.Epoch = epoch;
            record.Port = coordinator.AllocatedPort;
            record.ProcessId = coordinator.ProcessId;

            lock (_statusGate)
            {
                _startedSessions.Add(record);
                if (_startedSessions.Count > 64)
                {
                    _startedSessions.RemoveAt(0);
                }
            }

            Info("新链开局已落地：" + record + " pattern=" + request.FightPattern);

            Action<PMDsLobbySessionRecord> handler = SessionStarted;
            if (handler != null)
            {
                try
                {
                    handler(record);
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>分配前就失败的拒绝（会话尚未登记，无需回滚）。</summary>
        private void RejectUnregistered(string matchId, string reason)
        {
            SessionStartsRejected++;
            Warn("新链开局失败（match=" + matchId + "）：" + reason);

            Action<string, string> handler = SessionFailed;
            if (handler != null)
            {
                try
                {
                    handler(matchId, reason);
                }
                catch (Exception)
                {
                }
            }
        }

        private void RejectSession(LobbySession session, string reason)
        {
            SessionStartsRejected++;
            Warn("新链开局失败（match=" + session.MatchId + "）：" + reason);

            // 分配态/启动态失败都由协调器自行收尾：Allocated 无进程会立即归还端口与 uid；
            // Starting 之后的失败会经 Kill + 确认退出再释放。
            try
            {
                session.Coordinator.RequestShutdown(9u);
            }
            catch (Exception ex)
            {
                Warn("失败回滚时请求关闭异常（match=" + session.MatchId + "）：" + ex.GetType().Name);
            }

            TryDeleteBootstrapFile(session.BootstrapFilePath);

            Action<string, string> handler = SessionFailed;
            if (handler != null)
            {
                try
                {
                    handler(session.MatchId, reason);
                }
                catch (Exception)
                {
                }
            }
        }

        private void ProcessClientDisconnected(int uid, long connectionGeneration)
        {
            if (uid <= 0)
            {
                return;
            }

            string matchId;
            if (!_ledger.TryGetMatchId(uid, out matchId))
            {
                return;
            }

            LobbySession session = FindSession(matchId);
            if (session == null || session.Coordinator.IsTerminal)
            {
                return;
            }

            if (session.ResultAccepted || session.HostShutdownRequested)
            {
                // 终局不可逆 / 已被判定中止：迟到的断线通知不得改变任何事（不重复计数、不重开窗口）。
                return;
            }

            long now = _clock.UtcNowUnixMilliseconds;

            // T-LOOP4：**非终局 Running 局**不再一见断线就中止，而是开一个 30 秒原局续玩窗口。
            // 窗口只从第一次真实断开起算，重复通知不续期（不能借重复通知放大宽限）。
            if (CanHoldForReconnect(session))
            {
                OpenReconnectWindow(session, uid, now, connectionGeneration);
                ClientDisconnectsHeld++;
                Info("大厅 TCP 断线（uid=" + uid + "，match=" + matchId + "）：进入 "
                    + (long)_options.ReconnectWindow.TotalSeconds
                    + " 秒原局续玩窗口（不立即中止、不伪造胜负；离线角色仍留在权威战场）");
                return;
            }

            AbortForClientDisconnect(session, uid, matchId,
                "该局不是可续玩的非终局 Running 状态（" + session.Coordinator.State + "）");
        }

        /// <summary>
        /// T-LOOP4：该局是否处于「可以等玩家 30 秒回来」的状态。
        /// 只有**非终局 Running**：启动中/就绪中还没发 offer（客户端不可能已在局内），
        /// 已受理结果/终态则不可逆 —— 那些情形都走旧的中止路径。
        /// </summary>
        private static bool CanHoldForReconnect(LobbySession session)
        {
            return !session.ResultAccepted
                && !session.HostShutdownRequested
                && !session.Coordinator.IsTerminal
                && session.Coordinator.State == PMDsSessionState.Running;
        }

        /// <summary>
        /// 开/延续该 uid 的续玩窗口。已在同一 episode 的有效窗口内则**不刷新到期时刻**：
        /// 否则重复断线通知就能把 30 秒宽限无限延长（契约要求“只在真实断线时启动”）。
        /// </summary>
        private void OpenReconnectWindow(LobbySession session, int uid, long now, long connectionGeneration)
        {
            ReconnectWindowEntry existing;
            if (session.ReconnectWindows.TryGetValue(uid, out existing)
                && existing.Active
                && now < existing.ExpiresAtUnixMilliseconds
                && existing.SourceConnectionGeneration == connectionGeneration)
            {
                // 同一条TCP连接的重复断线事件不得重开窗口/反复滚票；
                // 但新的TCP连接续局后再断开，是**新episode**，已消费的上张票必在DS墓碑里。
                existing.DuplicateNotifications++;
                return;
            }

            ReconnectWindowEntry entry = new ReconnectWindowEntry();
            entry.Episode = existing == null ? 1 : existing.Episode + 1;
            entry.SourceConnectionGeneration = connectionGeneration;
            entry.DisconnectedAtUnixMilliseconds = now;
            entry.ExpiresAtUnixMilliseconds = now + (long)_options.ReconnectWindow.TotalMilliseconds;
            entry.Active = true;
            session.ReconnectWindows[uid] = entry;
        }

        /// <summary>
        /// 旧的断线语义：请求 DS 收尾（不伪造正常胜利）。
        /// 由两条路径进入：不可续玩的会话遇见断线；或续玩窗口到期而该 uid 仍未连回。
        /// 进入时立即丢掉该会话的全部待用续玩票，防止“已过期却还能被领走”。
        /// </summary>
        private void AbortForClientDisconnect(LobbySession session, int uid, string matchId, string reason)
        {
            ClientDisconnectAborts++;
            DropReconnectWindows(session);

            // 「已判定中止」必须在 RequestShutdown **之前**记下：
            // RequestShutdown 不改变 State，若不在宿主侧留痕，稍后的断线通知就能在这局上重开窗口。
            session.HostShutdownRequested = true;
            Warn("客户端断线（uid=" + uid + "，match=" + matchId + "）：中止该局，不伪造正常胜利（" + reason + "）");

            // 不伪造胜利：直接请 DS 收尾，没有 Result 就没有 Result。
            session.Coordinator.RequestShutdown(7u);
        }

        /// <summary>让该会话所有待用续玩票失效（保留 episode 序号，便于诊断）。</summary>
        private static void DropReconnectWindows(LobbySession session)
        {
            foreach (KeyValuePair<int, ReconnectWindowEntry> pair in session.ReconnectWindows)
            {
                pair.Value.Active = false;
                pair.Value.TicketCached = false;
                pair.Value.TicketBytes = null;
            }
        }

        /// <summary>
        /// T-LOOP4：处理一次续局请求（泵线程）。
        ///
        /// fail-closed 判定顺序（任何一项不满足即拒绝且**不改任何状态**）：
        /// uid 合法 → 该 uid 确实被某个会话占用 → 该局**未终局** → 会话处于
        /// <see cref="PMDsSessionState.Running"/> → uid 在**本局可信名册**内 →
        /// 该 uid 有**真实断线**记录且窗口未过期 → 当前存在已认证连接。
        /// 全部通过后才重签**新 Nonce**票（同一 episode 重复请求复用已签发的同一张，不滚票），
        /// 并以 <c>IsResume = true</c> 走既有 offer 网关推送。
        /// </summary>
        private void ProcessResumeEntry(int uid)
        {
            long now = _clock.UtcNowUnixMilliseconds;

            if (uid <= 0)
            {
                CountResumeOutcome(uid, PMDsLobbyResumeOutcome.RejectedUidInvalid);
                Warn("续局请求被拒：uid 非法（" + uid + "，UID0 不得取新票）");
                return;
            }

            string matchId;
            if (!_ledger.TryGetMatchId(uid, out matchId))
            {
                CountResumeOutcome(uid, PMDsLobbyResumeOutcome.RejectedNoSession);
                Info("续局请求无对应原局（uid=" + uid + "）：按无局处理，不影响正常匹配流程");
                return;
            }

            LobbySession session = FindSession(matchId);
            if (session == null)
            {
                CountResumeOutcome(uid, PMDsLobbyResumeOutcome.RejectedNoSession);
                return;
            }

            if (session.ResultAccepted || session.Coordinator.IsTerminal)
            {
                CountResumeOutcome(uid, PMDsLobbyResumeOutcome.RejectedTerminal);
                Warn("续局请求被拒（uid=" + uid + "，match=" + session.MatchId
                    + "）：该局已终局，终局不可逆（不发票也不恢复输入）");
                return;
            }

            if (session.HostShutdownRequested)
            {
                // 该局已被判定中止（供断线 Forfeit）但 DS 进程还没退出：绝不重新开窗发新票。
                CountResumeOutcome(uid, PMDsLobbyResumeOutcome.RejectedSessionAborting);
                Warn("续局请求被拒（uid=" + uid + "，match=" + session.MatchId
                    + "）：该局已被判定中止（已请求收尾），不再签发新票");
                return;
            }

            if (session.Coordinator.State != PMDsSessionState.Running)
            {
                CountResumeOutcome(uid, PMDsLobbyResumeOutcome.RejectedNotRunning);
                Info("续局请求被拒（uid=" + uid + "，match=" + session.MatchId + "）：会话状态 "
                    + session.Coordinator.State + " 不接受续局（只对非终局 Running 局重签票）");
                return;
            }

            PMDsRosterIdentity identity;
            if (!session.Coordinator.TryGetRosterIdentity(uid, out identity))
            {
                CountResumeOutcome(uid, PMDsLobbyResumeOutcome.RejectedNotInRoster);
                Warn("续局请求被拒（uid=" + uid + "）：不在该局可信名册内（不按请求自报身份签发）");
                return;
            }

            ReconnectWindowEntry window;
            if (!session.ReconnectWindows.TryGetValue(uid, out window))
            {
                CountResumeOutcome(uid, PMDsLobbyResumeOutcome.RejectedNotDisconnected);
                Warn("续局请求被拒（uid=" + uid + "）：没有该 uid 的真实断线记录"
                    + "（未断线的在线会话不得借续局顶号）");
                return;
            }

            if (!window.Active || now >= window.ExpiresAtUnixMilliseconds)
            {
                CountResumeOutcome(uid, PMDsLobbyResumeOutcome.RejectedWindowExpired);
                Warn("续局请求被拒（uid=" + uid + "）：断线续玩窗口已过期"
                    + "（断开于 " + window.DisconnectedAtUnixMilliseconds
                    + "，到期 " + window.ExpiresAtUnixMilliseconds + "，现 " + now + "）");
                return;
            }

            if (!_gateway.IsClientAuthenticated(uid))
            {
                CountResumeOutcome(uid, PMDsLobbyResumeOutcome.RejectedNotAuthenticated);
                Warn("续局请求被拒（uid=" + uid + "）：当前没有该 uid 的已认证连接"
                    + "（密码校验/原子登记未真正落地）");
                return;
            }

            byte[] ticket;
            bool rotated;
            if (window.TicketCached && window.TicketEpisode == window.Episode && window.TicketBytes != null)
            {
                // 同一断线 episode 的重复请求：**不滚票**，原样重发同一张已签发的票。
                ticket = window.TicketBytes;
                rotated = false;
                ResumeTicketsReused++;
            }
            else
            {
                try
                {
                    ticket = session.Coordinator.ReissueTicket(uid, now / 1000L).ExportTicketBytes();
                }
                catch (Exception ex)
                {
                    CountResumeOutcome(uid, PMDsLobbyResumeOutcome.RejectedReissueFailed);
                    Warn("续局票据重签失败（uid=" + uid + "，match=" + session.MatchId + "）："
                        + ex.GetType().Name);
                    return;
                }

                window.TicketCached = true;
                window.TicketEpisode = window.Episode;
                window.TicketBytes = ticket;
                rotated = true;
                ResumeTicketsIssued++;
            }

            PMDsLobbyEntryNotice notice = new PMDsLobbyEntryNotice();
            notice.MatchId = session.MatchId;
            notice.DsId = session.DsId;
            notice.Host = _options.ListenAddress;
            notice.Port = session.Coordinator.AllocatedPort;
            notice.Epoch = session.Epoch;
            notice.ProtocolHash = session.Coordinator.ProtocolHash;
            notice.CollisionDigest = session.CollisionDigest;
            notice.Identity = identity;
            notice.Ticket = ticket;
            notice.IsResume = true;

            string error;
            if (!_gateway.TrySendEntryOffer(notice, out error))
            {
                // 计数由 CountResumeOutcome 统一落（避免同一失败被算两次）。
                CountResumeOutcome(uid, PMDsLobbyResumeOutcome.RejectedOfferUndeliverable);
                Warn("续局通知发送失败（uid=" + uid + "，match=" + session.MatchId + "）：" + error
                    + "（不中止该局，等下一次登录重试）");
                return;
            }

            CountResumeOutcome(uid, rotated ? PMDsLobbyResumeOutcome.Issued : PMDsLobbyResumeOutcome.Reused);
            Info("续局通知已发送（uid=" + uid + "，match=" + session.MatchId
                + "，episode=" + window.Episode
                + (rotated ? "，已重签新 Nonce 票" : "，沿用本 episode 已签发的票（不滚票）")
                + "，剩余宽限 " + (window.ExpiresAtUnixMilliseconds - now) + "ms）");
        }

        /// <summary>记一个续局结论（专有计数器 + 最近一次诊断字段；不得记录票据/密码）。</summary>
        private void CountResumeOutcome(int uid, PMDsLobbyResumeOutcome outcome)
        {
            ResumeRequestsHandled++;
            LastResumeOutcomeUid = uid;
            LastResumeOutcome = outcome;

            switch (outcome)
            {
                case PMDsLobbyResumeOutcome.RejectedUidInvalid:
                    ResumeRejectedUidInvalid++;
                    return;
                case PMDsLobbyResumeOutcome.RejectedNoSession:
                    ResumeRejectedNoSession++;
                    return;
                case PMDsLobbyResumeOutcome.RejectedTerminal:
                    ResumeRejectedTerminal++;
                    return;
                case PMDsLobbyResumeOutcome.RejectedNotRunning:
                    ResumeRejectedNotRunning++;
                    return;
                case PMDsLobbyResumeOutcome.RejectedNotInRoster:
                    ResumeRejectedNotInRoster++;
                    return;
                case PMDsLobbyResumeOutcome.RejectedNotDisconnected:
                    ResumeRejectedNotDisconnected++;
                    return;
                case PMDsLobbyResumeOutcome.RejectedWindowExpired:
                    ResumeRejectedWindowExpired++;
                    return;
                case PMDsLobbyResumeOutcome.RejectedSessionAborting:
                    ResumeRejectedSessionAborting++;
                    return;
                case PMDsLobbyResumeOutcome.RejectedNotAuthenticated:
                    ResumeRejectedNotAuthenticated++;
                    return;
                case PMDsLobbyResumeOutcome.RejectedReissueFailed:
                    ResumeRejectedReissueFailed++;
                    return;
                case PMDsLobbyResumeOutcome.RejectedOfferUndeliverable:
                    ResumeOfferSendFailures++;
                    return;
            }
        }

        /// <summary>
        /// T-LOOP4：续玩窗口到期处理。
        ///
        /// 到期时**重新确认**「确实还没连回」：若该 uid 已在线（例如刚刚续局成功），
        /// 则不中止这个权威局（由 DS 自己的 30 秒窗口裁决）；只有仍未连回才走旧断线路径。
        /// 这样写而不是在开窗时看在线状态，是因为开窗那一刻正处于「旧连接刚移除、新连接可能正在登录」
        /// 的窗口期，在那儿读活跃表会把合法续局误判成未断线。
        /// </summary>
        private void ExpireReconnectWindows()
        {
            List<LobbySession> snapshot;
            lock (_statusGate)
            {
                if (_sessions.Count == 0)
                {
                    return;
                }

                snapshot = new List<LobbySession>(_sessions.Values);
            }

            long now = _clock.UtcNowUnixMilliseconds;

            for (int i = 0; i < snapshot.Count; i++)
            {
                LobbySession session = snapshot[i];
                if (session.ReconnectWindows.Count == 0)
                {
                    continue;
                }

                if (session.Coordinator.IsTerminal)
                {
                    DropReconnectWindows(session);
                    continue;
                }

                List<int> expired = null;
                foreach (KeyValuePair<int, ReconnectWindowEntry> pair in session.ReconnectWindows)
                {
                    if (pair.Value.Active && now >= pair.Value.ExpiresAtUnixMilliseconds)
                    {
                        if (expired == null)
                        {
                            expired = new List<int>();
                        }

                        expired.Add(pair.Key);
                    }
                }

                if (expired == null)
                {
                    continue;
                }

                for (int j = 0; j < expired.Count; j++)
                {
                    int uid = expired[j];
                    ReconnectWindowEntry entry = session.ReconnectWindows[uid];
                    entry.Active = false;
                    entry.TicketCached = false;
                    entry.TicketBytes = null;
                    ReconnectWindowsExpired++;

                    if (_gateway.IsClientAuthenticated(uid))
                    {
                        Info("原局续玩窗口到期但 uid=" + uid + "（match=" + session.MatchId
                            + "）已重新在线：不中止该局，交由 DS 权威侧按自己的窗口裁决；本端不再签发新票");
                        continue;
                    }

                    AbortForClientDisconnect(session, uid, session.MatchId,
                        "续玩窗口到期仍未连回（" + (long)_options.ReconnectWindow.TotalSeconds + " 秒）");
                    // 该局已请求收尾，同一会话剩余窗口失去意义。
                    break;
                }
            }
        }

        /// <summary>应用「后置动作」——在协调器回调里只置位，统一在这一步落地，避免深层重入。</summary>
        private void ApplyDeferredWork()
        {
            List<LobbySession> snapshot;
            lock (_statusGate)
            {
                snapshot = new List<LobbySession>(_sessions.Values);
            }

            for (int i = 0; i < snapshot.Count; i++)
            {
                LobbySession session = snapshot[i];
                if (session.EntryPublishPending && !session.EntryPublished)
                {
                    PublishEntryOffers(session);
                }
            }

            if (_pendingResultNotices.Count > 0)
            {
                PMDsLobbyResultNotice[] notices = _pendingResultNotices.ToArray();
                _pendingResultNotices.Clear();
                for (int i = 0; i < notices.Length; i++)
                {
                    Action<PMDsLobbyResultNotice> handler = ResultAccepted;
                    if (handler != null)
                    {
                        try
                        {
                            handler(notices[i]);
                        }
                        catch (Exception ex)
                        {
                            Warn("结果事件处理异常（match=" + notices[i].MatchId + "）：" + ex.GetType().Name);
                        }
                    }

                    try
                    {
                        _gateway.NotifyMatchEnded(notices[i]);
                    }
                    catch (Exception ex)
                    {
                        Warn("结果通知失败（match=" + notices[i].MatchId + "）：" + ex.GetType().Name);
                    }
                }
            }
        }

        /// <summary>
        /// Ready 之后把入局通知发给**当前同一认证客户端**（每人一张票据）。
        /// 任何一人发不出去即整局判失败（不缩编、不伪装成功）。
        /// </summary>
        private void PublishEntryOffers(LobbySession session)
        {
            session.EntryPublishPending = false;

            long nowSeconds = _clock.UtcNowUnixMilliseconds / 1000L;

            for (int i = 0; i < session.Roster.Length; i++)
            {
                PMDsRosterIdentity identity = session.Roster[i];
                if (!_gateway.IsClientAuthenticated(identity.Uid))
                {
                    EntryOfferFailures++;
                    RejectSession(session, "uid " + identity.Uid + " 在就绪前掉线，整局失败（不缩编）");
                    return;
                }

                byte[] ticket;
                try
                {
                    ticket = session.Coordinator.IssueTicket(identity.Uid, nowSeconds).ExportTicketBytes();
                }
                catch (Exception ex)
                {
                    EntryOfferFailures++;
                    RejectSession(session, "签发入场票据失败（uid=" + identity.Uid + "）：" + ex.GetType().Name);
                    return;
                }

                PMDsLobbyEntryNotice notice = new PMDsLobbyEntryNotice();
                notice.MatchId = session.MatchId;
                notice.DsId = session.DsId;
                notice.Host = _options.ListenAddress;
                notice.Port = session.Coordinator.AllocatedPort;
                notice.Epoch = session.Epoch;
                notice.ProtocolHash = session.Coordinator.ProtocolHash;
                notice.CollisionDigest = session.CollisionDigest;
                notice.Identity = identity;
                notice.Ticket = ticket;

                string error;
                if (!_gateway.TrySendEntryOffer(notice, out error))
                {
                    EntryOfferFailures++;
                    // 注意：此处可能已经给前面的玩家发过 offer（部分投递）。
                    // 我们**不**把这种局面包装成成功：直接中止该局，让已通知的客户端在连 DS 时失败，
                    // 而不是假装 6 个人都能进。
                    RejectSession(session, "入局通知发送失败（uid=" + identity.Uid + "）：" + error);
                    return;
                }

                EntryOffersSent++;
            }

            session.EntryPublished = true;
            session.Coordinator.MarkRunning();
            Info("会话 " + session.MatchId + " 已发布入局通知给 " + session.Roster.Length
                + " 名玩家并进入 Running（port=" + session.Coordinator.AllocatedPort + "）");
        }

        private void TickSessions()
        {
            List<LobbySession> snapshot;
            lock (_statusGate)
            {
                snapshot = new List<LobbySession>(_sessions.Values);
            }

            for (int i = 0; i < snapshot.Count; i++)
            {
                try
                {
                    snapshot[i].Coordinator.Tick();
                }
                catch (Exception ex)
                {
                    Warn("会话 Tick 异常（match=" + snapshot[i].MatchId + "）：" + ex.GetType().Name);
                }
            }
        }

        private void ReapSessions()
        {
            List<LobbySession> doomed = null;
            lock (_statusGate)
            {
                foreach (KeyValuePair<string, LobbySession> pair in _sessions)
                {
                    if (pair.Value.Released)
                    {
                        if (doomed == null)
                        {
                            doomed = new List<LobbySession>();
                        }

                        doomed.Add(pair.Value);
                    }
                }

                if (doomed != null)
                {
                    for (int i = 0; i < doomed.Count; i++)
                    {
                        _sessions.Remove(doomed[i].Key);
                    }
                }
            }

            if (doomed == null)
            {
                return;
            }

            for (int i = 0; i < doomed.Count; i++)
            {
                LobbySession session = doomed[i];

                // 「确认退出再恢复 PlayerOnline / 释放 uid」——uid 已由协调器在同点释放，
                // 这里只负责恢复大厅在线状态（与断线/顶号无关的第三件事）。
                for (int j = 0; j < session.Roster.Length; j++)
                {
                    try
                    {
                        _gateway.RestorePlayerOnline(session.Roster[j].Uid);
                    }
                    catch (Exception ex)
                    {
                        Warn("恢复 PlayerOnline 失败（uid=" + session.Roster[j].Uid + "）：" + ex.GetType().Name);
                    }
                }

                TryDeleteBootstrapFile(session.BootstrapFilePath);

                Action<string, PMDsSessionState, string> handler = SessionReleased;
                if (handler != null)
                {
                    try
                    {
                        handler(session.MatchId, session.LastState, session.LastReason);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        // ────────────────────────────────────────────────────────────────
        // 协调器副作用（SessionSink 转发；只置位 + 路由控制消息，不做深层重入）
        // ────────────────────────────────────────────────────────────────

        internal void HandleEffect(LobbySession session, PMDsCoordinatorEvent effect)
        {
            if (session == null)
            {
                return;
            }

            switch (effect.Effect)
            {
                case PMDsCoordinatorEffect.ReadyAddressPublished:
                    // 就绪只是「可以发地址了」；真正发出去由 ApplyDeferredWork 完成。
                    session.EntryPublishPending = true;
                    break;

                case PMDsCoordinatorEffect.ControlMessageOut:
                case PMDsCoordinatorEffect.GracefulShutdownRequested:
                    SendControlPayload(session, effect.ControlPayload);
                    break;

                case PMDsCoordinatorEffect.ResultAccepted:
                    session.ResultAccepted = true;
                    RecordResult(session, effect);
                    RecordEndedResults(session, effect);
                    break;

                case PMDsCoordinatorEffect.SessionEnded:
                    session.LastState = effect.State;
                    session.LastReason = effect.Reason;
                    break;

                case PMDsCoordinatorEffect.ResourcesReleased:
                    session.Released = true;
                    break;
            }
        }

        private void SendControlPayload(LobbySession session, byte[] payload)
        {
            if (payload == null || payload.Length == 0)
            {
                return;
            }

            int connectionId = session.ControlConnectionId;
            if (connectionId != 0 && _listener.TrySendFrame(connectionId, payload, 0, payload.Length))
            {
                return;
            }

            if (session.PendingOutbound.Count < PendingOutboundLimit)
            {
                session.PendingOutbound.Enqueue(payload);
                return;
            }

            ControlFramesUndeliverable++;
            Warn("控制消息无法投递（match=" + session.MatchId + "）：无绑定连接或队列已满");
        }

        private void FlushPendingOutbound(LobbySession session)
        {
            while (session.PendingOutbound.Count > 0)
            {
                byte[] payload = session.PendingOutbound.Dequeue();
                if (!_listener.TrySendFrame(session.ControlConnectionId, payload, 0, payload.Length))
                {
                    ControlFramesUndeliverable++;
                    return;
                }
            }
        }

        private void RecordResult(LobbySession session, PMDsCoordinatorEvent effect)
        {
            PMDsLobbyResultNotice notice = new PMDsLobbyResultNotice();
            notice.MatchId = session.MatchId;
            notice.DsId = session.DsId;
            notice.Epoch = session.Epoch;
            notice.ResultId = effect.ResultId;
            notice.WinnerTeamId = effect.WinnerTeamId;
            notice.Summary = effect.ResultSummary ?? new byte[0];
            notice.Roster = session.Roster;

            lock (_statusGate)
            {
                _pendingResultNotices.Add(notice);
            }

            Info("会话 " + session.MatchId + " 收到权威结果：resultId=" + effect.ResultId
                + " winner=" + effect.WinnerTeamId + "（只做控制结果通知，不伪造 BattleReview）");
        }

        /// <summary>
        /// T-LOOP4：把**已验证**的 DS 结果按 uid 记入有界只读缓存（TTL 默认 120 秒）。
        ///
        /// 只写 matchId / winner / localTeam 三个公开字段（后者的 localTeam 取该 uid 在名册里的真实 TeamId），
        /// 不含票据/密钥，也没有任何帧历史 —— 所以它**不可能**被当成 BattleReview 回放。
        /// 写入点唯一：<see cref="PMDsCoordinatorEffect.ResultAccepted"/>（即已经过 MAC/身份/摘要校验的结果）。
        /// </summary>
        private void RecordEndedResults(LobbySession session, PMDsCoordinatorEvent effect)
        {
            long now = _clock.UtcNowUnixMilliseconds;
            long expires = now + (long)_options.MatchEndedNoticeTtl.TotalMilliseconds;

            lock (_statusGate)
            {
                for (int i = 0; i < session.Roster.Length; i++)
                {
                    PMDsRosterIdentity identity = session.Roster[i];
                    PMDsEndedResultEntry entry = new PMDsEndedResultEntry();
                    entry.MatchId = session.MatchId;
                    entry.WinnerTeamId = effect.WinnerTeamId;
                    entry.LocalTeamId = identity.TeamId;
                    entry.RecordedAtUnixMilliseconds = now;
                    entry.ExpiresAtUnixMilliseconds = expires;
                    _endedResults[identity.Uid] = entry;

                    // 同 uid 只保留最近一局：旧局条目被覆盖（绝不把两局的胜负混在一起）。
                    MatchEndedNoticesRecorded++;
                }

                CollectExpiredEndedResults(now);
                while (_endedResults.Count > _options.MaxMatchEndedNotices)
                {
                    EvictOldestEndedResult();
                }
            }

            Info("已记录上局只读结果（match=" + session.MatchId + " winner=" + effect.WinnerTeamId
                + " 名册=" + session.Roster.Length + " 人，TTL="
                + (long)_options.MatchEndedNoticeTtl.TotalSeconds + " 秒；不携带帧历史）");
        }

        /// <summary>定期清理过期的只读结果条目（内存有界，不依赖是否有新结果到达）。</summary>
        private void ExpireEndedResults()
        {
            if (_endedResults.Count == 0)
            {
                return;
            }

            lock (_statusGate)
            {
                CollectExpiredEndedResults(_clock.UtcNowUnixMilliseconds);
            }
        }

        private void CollectExpiredEndedResults(long nowUnixMilliseconds)
        {
            List<int> doomed = null;
            foreach (KeyValuePair<int, PMDsEndedResultEntry> pair in _endedResults)
            {
                if (pair.Value.ExpiresAtUnixMilliseconds <= nowUnixMilliseconds)
                {
                    if (doomed == null)
                    {
                        doomed = new List<int>();
                    }

                    doomed.Add(pair.Key);
                }
            }

            if (doomed == null)
            {
                return;
            }

            for (int i = 0; i < doomed.Count; i++)
            {
                _endedResults.Remove(doomed[i]);
            }
        }

        private void EvictOldestEndedResult()
        {
            int oldestKey = 0;
            bool found = false;
            long oldest = long.MaxValue;
            foreach (KeyValuePair<int, PMDsEndedResultEntry> pair in _endedResults)
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
                _endedResults.Remove(oldestKey);
            }
        }

        // ────────────────────────────────────────────────────────────────
        // 启动参数与引导文件
        // ────────────────────────────────────────────────────────────────

        private static string[] BuildBaseArguments(string dsId, string matchId)
        {
            return new string[]
            {
                "-batchmode",
                "-nographics",
                "-server",
                "-dsid", dsId,
                "-matchid", matchId,
            };
        }

        /// <summary>
        /// 组装真实启动参数：**固定 exe + 逐个参数**，端口来自 Allocate 后的真实值（不硬编码），
        /// 密钥/票据一律不进命令行（只给引导文件路径）。
        /// </summary>
        private static string[] BuildFinalArguments(string dsId, string matchId, string bootstrapPath,
            string controlEndpoint, int port)
        {
            return new string[]
            {
                "-batchmode",
                "-nographics",
                "-server",
                "-dsid", dsId,
                "-matchid", matchId,
                "-bootstrap", bootstrapPath,
                "-control", controlEndpoint,
                "-port", port.ToString(),
            };
        }

        private string BuildDsId(string matchId, uint epoch)
        {
            // DsId 同样受 128 字节上限约束；截断时保留 epoch 后缀（它是会话身份的一部分）。
            string sanitized = SanitizeSegment(matchId);
            string suffix = "-ds-" + epoch.ToString();
            int maxBody = PMDsControlWire.MaxDsIdBytes - suffix.Length;
            if (maxBody < 1)
            {
                maxBody = 1;
            }

            if (sanitized.Length > maxBody)
            {
                sanitized = sanitized.Substring(0, maxBody);
            }

            return sanitized + suffix;
        }

        private string BuildBootstrapPath(string matchId, uint epoch)
        {
            string directory = Path.Combine(_options.BootstrapRootDirectory, SanitizeSegment(matchId));
            return Path.Combine(directory, "bootstrap-" + epoch + ".bin");
        }

        /// <summary>
        /// 路径段净化：只保留字母/数字/下划线/减号/点，其余替换为 <c>_</c>。
        /// 这样 matchId 再脏也不会逃逸出配置的每局目录（不做路径推断）。
        /// </summary>
        private static string SanitizeSegment(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "match";
            }

            StringBuilder builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool ok = (c >= '0' && c <= '9')
                    || (c >= 'a' && c <= 'z')
                    || (c >= 'A' && c <= 'Z')
                    || c == '_' || c == '-' || c == '.';
                builder.Append(ok ? c : '_');
            }

            string sanitized = builder.ToString();
            if (sanitized.Length == 0 || sanitized == "." || sanitized == "..")
            {
                return "match";
            }

            return sanitized;
        }

        /// <summary>
        /// 临时文件 + 原子改名发布引导文件。任一步失败都清掉临时文件并返回 false（调用方据此回滚）。
        ///
        /// **本机信任边界（有意）**：文件落在 <see cref="PMDsLobbyHostOptions.BootstrapRootDirectory"/> 下的
        /// 每局子目录，依靠同机同账户的文件系统权限保护（Windows 下继承父目录 ACL）。
        /// 本实现不做 DPAPI/自加密 —— 因为密钥就是给同机 DS 进程读的，
        /// 「同账户可读」与「必须可读」是同一件事；换成别的账户跑 DS 需自行收紧目录 ACL。
        /// 写入用 <c>Flush(true)</c> 落盘后再改名，保证 DS 只会看到「不存在」或「完整」两种状态。
        /// </summary>
        internal static bool TryPublishBootstrapFile(string path, byte[] document, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path))
            {
                error = "引导文件路径为空";
                return false;
            }

            if (document == null || document.Length == 0)
            {
                error = "引导内容为空";
                return false;
            }

            string directory = Path.GetDirectoryName(path);
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");

            try
            {
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(document, 0, document.Length);
                    stream.Flush(true);
                }

                if (File.Exists(path))
                {
                    File.Replace(temporary, path, null);
                }
                else
                {
                    File.Move(temporary, path);
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + " " + ex.Message;
                try
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
                catch (Exception)
                {
                }

                return false;
            }
        }

        private static void TryDeleteBootstrapFile(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
            }
        }

        private static string SessionKey(string matchId, string dsId, uint epoch)
        {
            return (matchId ?? string.Empty) + KeySeparator + (dsId ?? string.Empty) + KeySeparator
                + epoch.ToString();
        }

        private void Info(string message)
        {
            Action<string> log = Log;
            if (log != null)
            {
                try
                {
                    log("[PMDsLobbyHost] " + message);
                }
                catch (Exception)
                {
                }
            }
        }

        private void Warn(string message)
        {
            Info("WARN " + message);
        }

        // ────────────────────────────────────────────────────────────────
        // 内部类型
        // ────────────────────────────────────────────────────────────────

        private enum LobbyCommandKind : byte
        {
            StartMatch = 1,
            ClientDisconnected = 2,

            /// <summary>T-LOOP4：同 uid 重新登录后的「原局续玩」请求。</summary>
            ResumeEntry = 3,
        }

        private sealed class LobbyCommand
        {
            public LobbyCommandKind Kind;
            public string MatchId;
            public PMDsRosterIdentity[] Roster;
            public string FightPattern;
            public uint CollisionDigest;
            public long RequestId;
            public int Uid;
            public long ConnectionGeneration;
        }

        /// <summary>宿主侧的一局视图（协调器状态的影子 + 控制连接绑定）。</summary>
        internal sealed class LobbySession
        {
            public string MatchId = string.Empty;
            public string DsId = string.Empty;
            public uint Epoch;
            public string Key = string.Empty;
            public PMDsRosterIdentity[] Roster = new PMDsRosterIdentity[0];
            public PMDsCoordinator Coordinator;
            public string BootstrapFilePath = string.Empty;

            public int ControlConnectionId;
            public readonly Queue<byte[]> PendingOutbound = new Queue<byte[]>();

            public bool EntryPublishPending;
            public bool EntryPublished;
            public bool Released;
            public PMDsSessionState LastState;
            public string LastReason;

            public uint CollisionDigest;

            /// <summary>
            /// 已受理权威结果（终局不可逆）：续局/新票从此一律拒绝，只允许登录响应返回只读结论。
            /// </summary>
            public bool ResultAccepted;

            /// <summary>
            /// T-LOOP4：该局已被宿主判定中止（断线 Forfeit：立即中止或续玩窗口到期未连回）。
            ///
            /// 单独记一个标志而不是只看 State，是因为 `RequestShutdown` 只发出收尾请求，
            /// **State 在 DS 进程真正退出前仍是 Running**；没有这个标志，一段迟到的断线通知
            /// 就能在被弃局上再开一个 30 秒窗口，让已过期局重新拿到新票。
            /// </summary>
            public bool HostShutdownRequested;

            /// <summary>
            /// T-LOOP4：本会话内每个 uid 的「断线 → 续玩窗口」记录（uid -&gt; 记录）。
            /// 只有**真实断线**才会写入；同一 episode 的重复通知不刷新到期时刻。
            /// </summary>
            public readonly Dictionary<int, ReconnectWindowEntry> ReconnectWindows =
                new Dictionary<int, ReconnectWindowEntry>();
        }

        /// <summary>
        /// T-LOOP4：单个 uid 的一次「断线 episode」及其续玩窗口。
        ///
        /// 为什么要按 episode 而不是只存一个到期时刻：契约要求「同一断线 episode 内重复请求**不滚票**」，
        /// 所以本 episode 首次重签出来的票必须被缓存下来，后续请求原样重发。
        /// 新一次真实断线（玩家续局后又掉线）会开出新 episode，那时才允许再轮转一次 Nonce。
        /// </summary>
        internal sealed class ReconnectWindowEntry
        {
            /// <summary>本会话内该 uid 的断线序号（从 1 开始递增）。</summary>
            public int Episode = 1;

            /// <summary>发起本轮断线的大厅TCP连接代次；相同代次通知幂等，新连接再断线则开新轮。</summary>
            public long SourceConnectionGeneration;

            /// <summary>本 episode 的断开时刻（UTC 毫秒）。</summary>
            public long DisconnectedAtUnixMilliseconds;

            /// <summary>窗口到期时刻（UTC 毫秒）。</summary>
            public long ExpiresAtUnixMilliseconds;

            /// <summary>窗口是否仍有效（到期/会话收尾后置 false）。</summary>
            public bool Active;

            /// <summary>同一 episode 内重复的断线通知次数（诊断：证明窗口未被反复续期）。</summary>
            public int DuplicateNotifications;

            /// <summary>是否已为本 episode 重签并存下票。</summary>
            public bool TicketCached;

            /// <summary>缓存票对应的 episode（不等于当前 episode 时视为无效缓存）。</summary>
            public int TicketEpisode;

            /// <summary>缓存的新票字节（**秘密材料**，绝不进日志）。</summary>
            public byte[] TicketBytes;
        }

        /// <summary>
        /// T-LOOP4：按 uid 的「上局已终局只读结果」缓存条目。
        /// 只携带三个公开字段（matchId/winner/localTeam）+ 过期时刻，不含票据/密钥/帧历史。
        /// </summary>
        internal sealed class PMDsEndedResultEntry
        {
            public string MatchId = string.Empty;
            public int WinnerTeamId;
            public int LocalTeamId;
            public long RecordedAtUnixMilliseconds;
            public long ExpiresAtUnixMilliseconds;
        }

        /// <summary>每个会话一个的副作用接收器：把协调器事件绑定回具体会话，再交给宿主。</summary>
        private sealed class SessionSink : IPMDsCoordinatorSink
        {
            private readonly PMDsLobbyHost _host;
            private readonly LobbySession _session;

            public SessionSink(PMDsLobbyHost host, LobbySession session)
            {
                _host = host;
                _session = session;
            }

            public void OnEffect(PMDsCoordinatorEvent effect)
            {
                _host.HandleEffect(_session, effect);
            }
        }
    }
}
