using System;
using System.Globalization;
using System.Text;
using PMNet.Control;

namespace PMNet.Session
{
    // =====================================================================================
    //  R3-B：Lobby → 客户端的「入局通知」载荷（契约 §7.1）
    //
    //  契约原文要点（逐条对应实现）：
    //   - 使用**已认证 Lobby TCP** 的 StartEnterBattle 通知，`MainPack.Str` 承载
    //     `PMDS1:` + Base64(有界 PMNetWriter 编码的 PMDsEntryOffer)；
    //   - 它**只承载控制面入局信息**，不是业务状态同步；
    //   - 客户端识别前缀必须**严格解码**，解码失败报错、**不得偷偷退回旧链**；
    //   - 不会用 JSON 或分隔符拼接秘密；
    //   - codec 整条 <= 4096 字节，标识 <= 128 UTF-8 字节，票据按已有 codec 实际上限，
    //     端口 1..65535，严格尾部和范围；
    //   - **禁止把 offer / 完整 Str 写日志**。
    //
    //  为什么用 Base64 而不是往 MainPack 里加字段：契约明确「无需改 proto 生成物」。
    //  沿用 MainPack 只是借现有大厅载体，**不承诺旧客户端兼容**（契约 §7.1 原文）。
    //
    //  这个类型**不含任何秘密推导**：票据是 Lobby 签发的 bearer 凭据，客户端只是搬运它。
    // =====================================================================================

    /// <summary>
    /// 入局信息（控制面）：客户端据此连 DS 并做**关联校验**。
    ///
    /// 注意它**不是**入局状态的权威来源：<see cref="Ticket"/> 由 Lobby 签发并与对局/世代/摘要绑定，
    /// DS 侧会独立验票（见 <see cref="PMHandshakeServer"/>）；本结构只是「Lobby 告诉客户端去哪」。
    /// </summary>
    public sealed class PMDsEntryOffer
    {
        /// <summary>对局 ID（非空，UTF-8 &lt;= 128 字节）。</summary>
        public string MatchId;

        /// <summary>DS 实例标识（非空，UTF-8 &lt;= 128 字节）。</summary>
        public string DsId;

        /// <summary>DS 可达主机（非空，UTF-8 &lt;= 128 字节；首版为 loopback 字面量）。</summary>
        public string Host;

        /// <summary>会话世代（非 0）。</summary>
        public uint Epoch;

        /// <summary>协议摘要（非 0）。</summary>
        public uint ProtocolHash;

        /// <summary>碰撞配置摘要（0 表示「未声明」，客户端不校验该项）。</summary>
        public uint CollisionDigest;

        /// <summary>DS 监听端口（1..65535）。</summary>
        public int Port;

        /// <summary>本玩家在被认证名册里的身份。**所有权只能来自这里**（不从业务包自报 uid 采纳）。</summary>
        public PMDsRosterIdentity Identity;

        /// <summary>Lobby 签发的玩家票据（含 MAC）。**属于秘密材料，不得进日志。**</summary>
        public byte[] Ticket;

        /// <summary>
        /// T-LOOP1：该 offer 是**原局续玩**通知。它只决定客户端恢复路径/有界握手重试，
        /// 不授予权限；真正是否允许入局仍由 DS 独立验票、名册与连接账本裁决。
        /// 普通 PMDS1: 为 false，续局 PMDSR1: 为 true；二进制载荷完全相同。
        /// </summary>
        public bool IsResume;

        /// <summary>
        /// 诊断串：**只给公开字段与票据长度**，绝不打印票据字节
        /// （契约 §2：绝不日志输出密钥或完整票据；§7.1：禁止把 offer 写日志）。
        /// </summary>
        public override string ToString()
        {
            return "offer(match=" + (MatchId ?? string.Empty)
                + " ds=" + (DsId ?? string.Empty)
                + " host=" + (Host ?? string.Empty)
                + " port=" + Port
                + " epoch=" + Epoch
                + " hash=0x" + ProtocolHash.ToString("X8")
                + " digest=0x" + CollisionDigest.ToString("X8")
                + " resume=" + (IsResume ? 1 : 0)
                + " " + Identity.ToString()
                + " ticket=" + (Ticket == null ? 0 : Ticket.Length) + "B)";
        }
    }

    /// <summary>
    /// <see cref="PMDsEntryOffer"/> 的文本编解码（前缀 + Base64）。
    ///
    /// **严格**是这里的核心性质，因为这条文本穿过了「大厅 TCP → 客户端」这条**不受本模块控制**的路径：
    ///   - 前缀必须是完整的 <see cref="Prefix"/>（不是「包含」，是「以它开头」）；
    ///   - 整条文本 &lt;= <see cref="MaxTextBytes"/>，且**逐字符 ASCII**（否则「字节上限」会被多字节字符钻空子）；
    ///   - Base64 必须是**规范**的（字母表内、长度 %4==0、`=` 只出现在末尾且最多两个）；
    ///   - 载荷必须**恰好消费完**（严格拒绝尾部），任何一段越界或多余字节都失败。
    ///
    /// 失败一律返回 false + 原因（**不抛**）：解码路径的输入来自网络，抛异常会把「对端乱写」变成
    /// 「自己崩溃」。编码路径相反——它由 Lobby 自己构造，参数不合法是**编程错误**，必须显式抛出。
    /// </summary>
    public static class PMDsEntryCodec
    {
        /// <summary>文本前缀（契约 §7.1 冻结）。</summary>
        public const string Prefix = "PMDS1:";

        /// <summary>原局续玩专用文本前缀；复用同一个严格二进制布局，不复用旧票。</summary>
        public const string ResumePrefix = "PMDSR1:";

        /// <summary>整条文本的字节上限（契约 §7.1：codec 整条 &lt;= 4096 字节）。</summary>
        public const int MaxTextBytes = 4096;

        /// <summary>标识类字段的 UTF-8 字节上限（契约 §7.1：标识 &lt;= 128）。</summary>
        public const int MaxIdBytes = 128;

        /// <summary>票据字节上限：与既有票据 codec 的实际上限同源（352 字节），不另立一套。</summary>
        public const int MaxTicketBytes = PMDsTicketWire.MaxTicketBytes;

        /// <summary>端口下界（契约 §7.1：端口 1..65535）。</summary>
        public const int MinPort = 1;

        /// <summary>端口上界。</summary>
        public const int MaxPort = 65535;

        /// <summary>载荷格式版本（自描述，便于将来演进时明确拒绝旧版本）。</summary>
        public const int FormatVersion = 1;

        private const string Base64Alphabet =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

        private static readonly byte[] TicketMagic = new byte[] { (byte)'P', (byte)'M', (byte)'D', (byte)'S' };

        /// <summary>
        /// 编码为 `PMDS1:` + Base64。**参数不合法即抛**（调用方是 Lobby，不是对端）。
        /// </summary>
        public static string Encode(PMDsEntryOffer offer)
        {
            if (offer == null)
            {
                throw new ArgumentNullException("offer");
            }

            RequireId(offer.MatchId, "MatchId");
            RequireId(offer.DsId, "DsId");
            RequireId(offer.Host, "Host");

            if (offer.Epoch == 0u)
            {
                throw new ArgumentException("Epoch 不得为 0", "offer");
            }

            if (offer.ProtocolHash == 0u)
            {
                throw new ArgumentException("ProtocolHash 不得为 0", "offer");
            }

            if (offer.Port < MinPort || offer.Port > MaxPort)
            {
                throw new ArgumentException("Port 必须在 " + MinPort + ".." + MaxPort + " 之间，收到 " + offer.Port, "offer");
            }

            if (offer.Identity.Uid <= 0 || offer.Identity.PlayerId <= 0)
            {
                throw new ArgumentException("Uid / PlayerId 必须 > 0：" + offer.Identity, "offer");
            }

            if (offer.Identity.TeamId < 0 || offer.Identity.HeroId < 0)
            {
                throw new ArgumentException("TeamId / HeroId 不得为负：" + offer.Identity, "offer");
            }

            if (offer.Ticket == null || offer.Ticket.Length == 0)
            {
                throw new ArgumentException("Ticket 不得为空", "offer");
            }

            if (offer.Ticket.Length > MaxTicketBytes)
            {
                throw new ArgumentException(
                    "Ticket 长度 " + offer.Ticket.Length + " 超过既有 codec 上限 " + MaxTicketBytes, "offer");
            }

            PMNetWriter writer = new PMNetWriter(512);
            writer.WriteInt32(FormatVersion);
            writer.WriteStringValue(offer.MatchId);
            writer.WriteStringValue(offer.DsId);
            writer.WriteStringValue(offer.Host);
            writer.WriteUInt32(offer.Epoch);
            writer.WriteUInt32(offer.ProtocolHash);
            writer.WriteUInt32(offer.CollisionDigest);
            writer.WriteInt32(offer.Port);
            writer.WriteInt32(offer.Identity.Uid);
            writer.WriteInt32(offer.Identity.PlayerId);
            writer.WriteInt32(offer.Identity.TeamId);
            writer.WriteInt32(offer.Identity.HeroId);
            writer.WriteInt32(offer.Ticket.Length);
            writer.WriteRawBytes(offer.Ticket, 0, offer.Ticket.Length);

            string base64 = Convert.ToBase64String(writer.GetBuffer(), 0, writer.Length);
            string prefix = offer.IsResume ? ResumePrefix : Prefix;
            int total = prefix.Length + base64.Length;
            if (total > MaxTextBytes)
            {
                // 显式失败：宁可在 Lobby 侧炸掉，也不发一条对端必然拒收的入局通知。
                throw new ArgumentException(
                    "编码后文本 " + total + " 字节超过上限 " + MaxTextBytes + " 字节", "offer");
            }

            return prefix + base64;
        }

        /// <summary>
        /// 严格解码。失败给出**不含秘密**的原因（不得把原文本回显进 error：它含票据）。
        /// </summary>
        public static bool TryDecode(string text, out PMDsEntryOffer offer, out string error)
        {
            offer = null;
            error = null;

            if (text == null || text.Length == 0)
            {
                error = "入局通知文本为空";
                return false;
            }

            if (text.Length > MaxTextBytes)
            {
                error = "入局通知文本长度 " + text.Length + " 超过上限 " + MaxTextBytes + " 字节";
                return false;
            }

            bool isResume = text.Length >= ResumePrefix.Length
                && string.CompareOrdinal(text, 0, ResumePrefix, 0, ResumePrefix.Length) == 0;
            bool isInitial = text.Length >= Prefix.Length
                && string.CompareOrdinal(text, 0, Prefix, 0, Prefix.Length) == 0;
            if (!isResume && !isInitial)
            {
                error = "入局通知缺少 PMDS1:/PMDSR1: 完整前缀（旧链载体不会被新链接受）";
                return false;
            }

            string payload = text.Substring(isResume ? ResumePrefix.Length : Prefix.Length);
            if (!IsStrictBase64(payload))
            {
                error = "入局通知的 Base64 载荷非法（非规范编码）";
                return false;
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(payload);
            }
            catch (FormatException)
            {
                error = "入局通知的 Base64 载荷无法解码";
                return false;
            }

            if (!TryDecodePayload(bytes, out offer, out error)) { return false; }
            // 前缀在严格二进制校验成功**之后**才落到返回对象：伪造一个字符串标签
            // 本身没有任何认证能力，DS 仍会独立校验票据并绑定名册。
            offer.IsResume = isResume;
            return true;
        }

        /// <summary>
        /// 整段字节重载（门禁/工具用；语义与文本版一致）。
        /// **不抛**：畸形 varint 之类的越界输入一律翻成 false + 原因。
        /// </summary>
        public static bool TryDecodePayload(byte[] bytes, out PMDsEntryOffer offer, out string error)
        {
            try
            {
                return TryDecodePayloadStrict(bytes, out offer, out error);
            }
            catch (Exception ex)
            {
                // 解码路径绝不抛出：任何未被显式判定的异常都归为「载荷非法」。
                offer = null;
                error = "入局通知载荷解析异常：" + ex.GetType().Name;
                return false;
            }
        }

        private static bool TryDecodePayloadStrict(byte[] bytes, out PMDsEntryOffer offer, out string error)
        {
            offer = null;
            error = null;

            if (bytes == null || bytes.Length == 0)
            {
                error = "入局通知载荷为空";
                return false;
            }

            PMNetReader reader = new PMNetReader(bytes, 0, bytes.Length);

            int version = reader.ReadInt32();
            if (version != FormatVersion)
            {
                error = "入局通知载荷版本不受支持：" + version;
                return false;
            }

            PMDsEntryOffer parsed = new PMDsEntryOffer();
            parsed.MatchId = ReadId(reader, "MatchId", out error);
            if (error != null) { return false; }

            parsed.DsId = ReadId(reader, "DsId", out error);
            if (error != null) { return false; }

            parsed.Host = ReadId(reader, "Host", out error);
            if (error != null) { return false; }

            parsed.Epoch = reader.ReadUInt32();
            parsed.ProtocolHash = reader.ReadUInt32();
            parsed.CollisionDigest = reader.ReadUInt32();
            parsed.Port = reader.ReadInt32();
            parsed.Identity.Uid = reader.ReadInt32();
            parsed.Identity.PlayerId = reader.ReadInt32();
            parsed.Identity.TeamId = reader.ReadInt32();
            parsed.Identity.HeroId = reader.ReadInt32();

            int ticketLength = reader.ReadInt32();
            if (ticketLength <= 0 || ticketLength > MaxTicketBytes)
            {
                error = "票据长度 " + ticketLength + " 越界（允许 1.." + MaxTicketBytes + "）";
                return false;
            }

            parsed.Ticket = reader.ReadRawBytesCopy(ticketLength);
            if (!HasTicketMagic(parsed.Ticket))
            {
                error = "票据缺少既有票据 codec 的魔数（结构与既有 codec 不同源）";
                return false;
            }

            if (!reader.IsAtEnd)
            {
                error = "入局通知载荷存在多余尾部字节（严格拒绝）";
                return false;
            }

            if (parsed.Epoch == 0u)
            {
                error = "Epoch 不得为 0";
                return false;
            }

            if (parsed.ProtocolHash == 0u)
            {
                error = "ProtocolHash 不得为 0";
                return false;
            }

            if (parsed.Port < MinPort || parsed.Port > MaxPort)
            {
                error = "端口 " + parsed.Port + " 越界（允许 " + MinPort + ".." + MaxPort + "）";
                return false;
            }

            if (parsed.Identity.Uid <= 0 || parsed.Identity.PlayerId <= 0)
            {
                error = "Uid / PlayerId 必须 > 0：" + parsed.Identity;
                return false;
            }

            if (parsed.Identity.TeamId < 0 || parsed.Identity.HeroId < 0)
            {
                error = "TeamId / HeroId 不得为负：" + parsed.Identity;
                return false;
            }

            if (string.IsNullOrEmpty(parsed.MatchId) || string.IsNullOrEmpty(parsed.DsId)
                || string.IsNullOrEmpty(parsed.Host))
            {
                error = "MatchId / DsId / Host 不得为空";
                return false;
            }

            offer = parsed;
            return true;
        }

        /// <summary>是否以此前缀开头（宿主用它分流新链 / 旧链，不承担完整校验）。</summary>
        public static bool HasPrefix(string text)
        {
            return text != null
                && ((text.Length >= Prefix.Length
                     && string.CompareOrdinal(text, 0, Prefix, 0, Prefix.Length) == 0)
                    || (text.Length >= ResumePrefix.Length
                     && string.CompareOrdinal(text, 0, ResumePrefix, 0, ResumePrefix.Length) == 0));
        }

        private static void RequireId(string value, string what)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException(what + " 不得为空", "offer");
            }

            int bytes = Encoding.UTF8.GetByteCount(value);
            if (bytes > MaxIdBytes)
            {
                throw new ArgumentException(
                    what + " 的 UTF-8 长度 " + bytes + " 字节超过上限 " + MaxIdBytes, "offer");
            }
        }

        /// <summary>读一个有界标识：先预读长度**再**分配（不做事后校验）。</summary>
        private static string ReadId(PMNetReader reader, string what, out string error)
        {
            error = null;

            int length;
            try
            {
                length = reader.PeekVarintLength();
            }
            catch (Exception)
            {
                error = what + " 的长度前缀非法";
                return null;
            }

            if (length < 0 || length > MaxIdBytes)
            {
                error = what + " 的 UTF-8 长度 " + length + " 字节超过上限 " + MaxIdBytes;
                return null;
            }

            if (length == 0)
            {
                error = what + " 不得为空";
                return null;
            }

            try
            {
                return reader.ReadStringValue();
            }
            catch (Exception)
            {
                error = what + " 越出载荷范围";
                return null;
            }
        }

        /// <summary>
        /// 规范 Base64 校验：字母表内、长度 %4==0、`=` 只出现在末尾且最多两个。
        /// 不依赖 <see cref="Convert.FromBase64String"/> 的宽松行为（它会忽略空白）。
        /// </summary>
        private static bool IsStrictBase64(string value)
        {
            if (value.Length == 0 || (value.Length % 4) != 0)
            {
                return false;
            }

            int padding = 0;
            int firstPadding = value.Length;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '=')
                {
                    if (padding == 0)
                    {
                        firstPadding = i;
                    }

                    padding++;
                    if (padding > 2)
                    {
                        return false;
                    }

                    continue;
                }

                if (padding > 0)
                {
                    // `=` 之后不得再出现数据字符（非规范编码）。
                    return false;
                }

                if (Base64Alphabet.IndexOf(c) < 0)
                {
                    return false;
                }
            }

            if (padding > 0 && firstPadding < 4)
            {
                return false;
            }

            return true;
        }

        private static bool HasTicketMagic(byte[] ticket)
        {
            if (ticket == null || ticket.Length < TicketMagic.Length)
            {
                return false;
            }

            for (int i = 0; i < TicketMagic.Length; i++)
            {
                if (ticket[i] != TicketMagic[i])
                {
                    return false;
                }
            }

            return true;
        }
    }

    // =====================================================================================
    //  T-LOOP1：Lobby → 客户端的「上局已终局」只读结果通知（`PMDS-END1:`）
    //
    //  契约要点（主计划「T-LOOP1 原局断线续玩 v1 冻结接口」+ 末尾红队复核第 1 条）：
    //   - 线格式 `PMDS-END1:<base64(matchId UTF-8)>:<winner>:<localTeam>`，**无秘密**
    //     （不含票据/密钥，也不含任何帧历史 —— 回放属 R6）；
    //   - 它只由「已验证的 DS ResultAccepted」产生，登录成功时随 `MainPack.Str` 同步返回；
    //     客户端只读显示胜/负/平并**留在大厅**：不创建 DS 会话、不恢复输入、不启战斗，
    //     也**不得**冒充客户端进程内那份「DS 实机终局」快照；
    //   - 这条文本穿过的路径（大厅 TCP → 登录响应）不受本模块控制，所以解码必须**严格**：
    //     完整前缀、恰好三段、规范 Base64、合法 UTF-8、非空 matchId、非负 team、拒绝任何尾部。
    //
    //  为什么与 `PMDsEntryCodec` 同文件：两者都是「Lobby 产出、客户端严格消费」的控制面文本，
    //  而本文件是**两端共享源**（Server.csproj 直接链接 PMNet/**）。把编码与解码放进同一个
    //  事实源，服务端就不可能悄悄发出一条客户端必然拒收的文本。
    // =====================================================================================

    /// <summary>
    /// 上局已终局的**只读**结果（无秘密）：对局 ID / 胜方队伍 / 本队。
    /// 它只是「让重新登录的客户端看见可信胜负」的载体，不含票据、密钥或帧历史。
    /// </summary>
    public sealed class PMDsEndedNotice
    {
        /// <summary>对局 ID（非空；线格式里先 UTF-8 再 Base64）。</summary>
        public string MatchId;

        /// <summary>冻结的胜方队伍（来自已验证的 DS ResultAccepted；0 = 无唯一胜者/平）。</summary>
        public int WinnerTeamId;

        /// <summary>本玩家在该局可信名册里的真实队伍（客户端据此判胜/负/平）。</summary>
        public int LocalTeamId;

        /// <summary>诊断串：只有公开字段，不含文本/凭据。</summary>
        public override string ToString()
        {
            return "ended(match=" + (MatchId ?? string.Empty)
                + " winner=" + WinnerTeamId + " localTeam=" + LocalTeamId + ")";
        }
    }

    /// <summary>
    /// 客户端对「上局已终局」的只读结论。口径与 <c>PMUnityCombatHud</c> **完全一致**：
    /// winnerTeamId==0 → 平；否则本队 == winnerTeamId → 胜，其余 → 负。
    /// </summary>
    public enum PMDsEndedVerdict : byte
    {
        /// <summary>无唯一胜者（winnerTeamId == 0）。</summary>
        Draw = 0,

        /// <summary>本队即胜方。</summary>
        Victory = 1,

        /// <summary>本队不是胜方。</summary>
        Defeat = 2,
    }

    /// <summary>
    /// `PMDS-END1:` 通知的**严格**编解码。
    ///
    /// 解码失败一律 false + 原因（**不抛**）：输入来自网络，抛异常会把「对端乱写」变成
    /// 「自己崩溃」。编码侧相反 —— 参数不合法是**调用方**的编程错误：
    /// <see cref="Encode"/> 显式抛出，<see cref="TryEncode"/> 给宿主一条
    /// 「宁可让通知缺席，也不发非法文本」的非抛出路径。
    /// </summary>
    public static class PMDsEndedNoticeCodec
    {
        /// <summary>文本前缀（冻结：带冒号；裸 `PMDS-END1` 不算命中）。</summary>
        public const string Prefix = "PMDS-END1:";

        /// <summary>
        /// 整条文本的字符上限。它比入局 codec 的 4096 小得多：载荷只有
        /// 「一个 &lt;= 128 字节的 matchId」+ 两个整数，正常长度 &lt; 200。
        /// </summary>
        public const int MaxTextBytes = 512;

        /// <summary>matchId 的 UTF-8 字节上限：与 <see cref="PMDsEntryCodec.MaxIdBytes"/> 同源。</summary>
        public const int MaxMatchIdBytes = PMDsEntryCodec.MaxIdBytes;

        private const string Base64Alphabet =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

        /// <summary>
        /// 严格 UTF-8（拒绝非法序列 / 过长编码 / 孤悬代理）。
        /// 默认的 <c>Encoding.UTF8</c> 会把非法字节换成 U+FFFD 而**不报错**，
        /// 那等于把畸形 matchId 当成合法文本显示出来。
        /// </summary>
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>
        /// 编码为冻结线格式。**参数不合法即抛**（调用方是 Lobby/宿主，不是对端）。
        /// 只编码公开结论；绝不编码票据/密钥/帧历史。
        /// </summary>
        public static string Encode(PMDsEndedNotice notice)
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
        /// 非抛出编码：给「字段来自远端结果」的宿主路径用 —— 一个不可信字段只让这条只读通知缺席，
        /// 绝不能把异常抛进登录响应路径，也绝不能发一条客户端必然拒收的文本。
        /// </summary>
        public static bool TryEncode(PMDsEndedNotice notice, out string text, out string error)
        {
            text = null;
            error = null;

            if (notice == null)
            {
                error = "已结束对局通知对象为空";
                return false;
            }

            if (string.IsNullOrEmpty(notice.MatchId))
            {
                error = "MatchId 不得为空";
                return false;
            }

            int matchIdBytes = Encoding.UTF8.GetByteCount(notice.MatchId);
            if (matchIdBytes > MaxMatchIdBytes)
            {
                error = "MatchId 的 UTF-8 长度 " + matchIdBytes + " 字节超过上限 " + MaxMatchIdBytes;
                return false;
            }

            if (notice.WinnerTeamId < 0)
            {
                error = "胜方队伍不得为负（收到 " + notice.WinnerTeamId + "）";
                return false;
            }

            if (notice.LocalTeamId < 0)
            {
                error = "本队不得为负（收到 " + notice.LocalTeamId + "）";
                return false;
            }

            string encoded = Prefix
                + Convert.ToBase64String(Encoding.UTF8.GetBytes(notice.MatchId))
                + ":" + notice.WinnerTeamId.ToString(CultureInfo.InvariantCulture)
                + ":" + notice.LocalTeamId.ToString(CultureInfo.InvariantCulture);
            if (encoded.Length > MaxTextBytes)
            {
                error = "编码后文本 " + encoded.Length + " 字节超过上限 " + MaxTextBytes + " 字节";
                return false;
            }

            text = encoded;
            return true;
        }

        /// <summary>是否以**完整**前缀（含冒号）开头（宿主分流用；不承担完整校验）。</summary>
        public static bool HasPrefix(string text)
        {
            return text != null
                && text.Length >= Prefix.Length
                && string.CompareOrdinal(text, 0, Prefix, 0, Prefix.Length) == 0;
        }

        /// <summary>
        /// 严格解码。失败给出**不含秘密**的原因：不回显原文本（错误原因只需说明结构问题）。
        /// </summary>
        public static bool TryDecode(string text, out PMDsEndedNotice notice, out string error)
        {
            notice = null;
            error = null;

            if (string.IsNullOrEmpty(text))
            {
                error = "已结束对局通知文本为空";
                return false;
            }

            if (text.Length > MaxTextBytes)
            {
                error = "已结束对局通知文本长度 " + text.Length + " 超过上限 " + MaxTextBytes + " 字节";
                return false;
            }

            if (!HasPrefix(text))
            {
                error = "已结束对局通知缺少完整的 PMDS-END1: 前缀";
                return false;
            }

            string body = text.Substring(Prefix.Length);
            int firstColon = body.IndexOf(':');
            if (firstColon < 0)
            {
                error = "已结束对局通知缺少胜方/本队字段（分段不足）";
                return false;
            }

            int secondColon = body.IndexOf(':', firstColon + 1);
            if (secondColon < 0)
            {
                error = "已结束对局通知缺少本队字段（分段不足）";
                return false;
            }

            if (body.IndexOf(':', secondColon + 1) >= 0)
            {
                error = "已结束对局通知存在多余字段（严格拒绝尾部）";
                return false;
            }

            string matchIdPart = body.Substring(0, firstColon);
            string winnerPart = body.Substring(firstColon + 1, secondColon - firstColon - 1);
            string localTeamPart = body.Substring(secondColon + 1);

            if (!IsCanonicalBase64(matchIdPart))
            {
                error = "matchId 的 Base64 不是规范编码";
                return false;
            }

            byte[] matchIdBytes;
            try
            {
                matchIdBytes = Convert.FromBase64String(matchIdPart);
            }
            catch (FormatException)
            {
                error = "matchId 的 Base64 无法解码";
                return false;
            }

            if (matchIdBytes.Length == 0)
            {
                error = "matchId 不得为空";
                return false;
            }

            if (matchIdBytes.Length > MaxMatchIdBytes)
            {
                error = "matchId 的 UTF-8 长度 " + matchIdBytes.Length + " 字节超过上限 " + MaxMatchIdBytes;
                return false;
            }

            string matchId;
            try
            {
                matchId = StrictUtf8.GetString(matchIdBytes);
            }
            catch (Exception)
            {
                error = "matchId 不是合法的 UTF-8 文本";
                return false;
            }

            if (string.IsNullOrEmpty(matchId))
            {
                error = "matchId 不得为空";
                return false;
            }

            int winnerTeamId;
            if (!TryParseTeam(winnerPart, out winnerTeamId, out error))
            {
                error = "胜方队伍字段非法（" + error + "）";
                return false;
            }

            int localTeamId;
            if (!TryParseTeam(localTeamPart, out localTeamId, out error))
            {
                error = "本队字段非法（" + error + "）";
                return false;
            }

            PMDsEndedNotice parsed = new PMDsEndedNotice();
            parsed.MatchId = matchId;
            parsed.WinnerTeamId = winnerTeamId;
            parsed.LocalTeamId = localTeamId;
            notice = parsed;
            return true;
        }

        /// <summary>
        /// 客户端只读结论。**与本局 HUD 同一口径**：winnerTeamId==0 → 平；
        /// 否则本队 == winnerTeamId → 胜，其余 → 负。
        /// </summary>
        public static PMDsEndedVerdict ResolveVerdict(PMDsEndedNotice notice)
        {
            if (notice == null)
            {
                throw new ArgumentNullException("notice");
            }

            if (notice.WinnerTeamId == 0)
            {
                return PMDsEndedVerdict.Draw;
            }

            return notice.LocalTeamId > 0 && notice.LocalTeamId == notice.WinnerTeamId
                ? PMDsEndedVerdict.Victory
                : PMDsEndedVerdict.Defeat;
        }

        /// <summary>结论的展示文案（与 HUD 的「胜利/失败/平局」逐字一致）。</summary>
        public static string VerdictText(PMDsEndedVerdict verdict)
        {
            if (verdict == PMDsEndedVerdict.Victory) { return "胜利"; }
            if (verdict == PMDsEndedVerdict.Defeat) { return "失败"; }
            return "平局";
        }

        /// <summary>
        /// 登录成功后展示的**只读**文案（纯文本，无任何 Unity 依赖）。
        ///
        /// 为什么放在共享 codec 而不是 UI 面板里：这样「解析 → 结论 → 文案」整条逻辑都能被
        /// **可执行门禁**逐条断言，而不是只看 UI 源码里出现过哪些字符串；
        /// UI 面板只剩一句 <c>ShowMessage</c>，不含任何判定分支。
        /// </summary>
        public static string BuildReadOnlyMessage(PMDsEndedNotice notice)
        {
            if (notice == null)
            {
                throw new ArgumentNullException("notice");
            }

            return "上一局已结束（" + VerdictText(ResolveVerdict(notice)) + "）\n"
                + "对局：" + (notice.MatchId ?? "<未知>") + "\n"
                + "胜方队伍：" + notice.WinnerTeamId.ToString(CultureInfo.InvariantCulture) + "\n"
                + "本队：" + notice.LocalTeamId.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 规范 Base64：字母表内、长度 %4==0、`=` 只出现在末尾且最多两个，并且**往返一致**
        /// （因此拒绝「填充位非零」这类被 .NET 宽松接受的变体）。
        ///
        /// 为什么另写一份而不是复用 <see cref="PMDsEntryCodec"/> 的私有校验：
        ///   · 那份是**已冻结**的入局契约（本轮不得扰动）；
        ///   · 它按「二进制块」语义还额外要求 `=` 不能出现在第 4 位之前，那会**误杀**
        ///     合法的单字节 matchId（如 `QQ==`）；
        ///   · 本 codec 的载荷是**文本**，规范性由「解出来再编回去必须一模一样」定义最直接。
        /// </summary>
        private static bool IsCanonicalBase64(string value)
        {
            if (string.IsNullOrEmpty(value) || (value.Length % 4) != 0)
            {
                return false;
            }

            int padding = 0;
            bool sawPadding = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '=')
                {
                    sawPadding = true;
                    padding++;
                    if (padding > 2)
                    {
                        return false;
                    }

                    continue;
                }

                if (sawPadding)
                {
                    // `=` 之后不得再出现数据字符（非规范编码）。
                    return false;
                }

                if (Base64Alphabet.IndexOf(c) < 0)
                {
                    return false;
                }
            }

            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(value);
            }
            catch (FormatException)
            {
                return false;
            }

            // 往返一致 ⇒ 尾部填充位必须为 0（拒绝 "QR==" 这类非规范编码）。
            return string.Equals(Convert.ToBase64String(decoded), value, StringComparison.Ordinal);
        }

        /// <summary>
        /// 严格整数解析（队伍号）：不允许正号/空白/非 ASCII 数字，不允许前导零，
        /// 任何负值都判非法（**负 team 一律拒绝**），越界用有界算术在溢出前拒绝。
        /// </summary>
        private static bool TryParseTeam(string value, out int parsed, out string error)
        {
            parsed = 0;
            error = null;

            if (string.IsNullOrEmpty(value))
            {
                error = "字段为空";
                return false;
            }

            if (value[0] == '-')
            {
                error = "队伍号不得为负";
                return false;
            }

            // 先逐字符确认全是 ASCII 数字（并把溢出判定融进同一次扫描），
            // 最后才判「前导零」——这样 "0.0"/"0 "/"0x1" 报的是「非数字字符」而不是误导性的「前导零」。
            int result = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c < '0' || c > '9')
                {
                    error = "含有非数字字符";
                    return false;
                }

                int digit = c - '0';
                if (result > (int.MaxValue - digit) / 10)
                {
                    error = "整数越界";
                    return false;
                }

                result = result * 10 + digit;
            }

            if (value.Length > 1 && value[0] == '0')
            {
                error = "存在前导零（非规范整数）";
                return false;
            }

            parsed = result;
            return true;
        }
    }
}
