using System;
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
            int total = Prefix.Length + base64.Length;
            if (total > MaxTextBytes)
            {
                // 显式失败：宁可在 Lobby 侧炸掉，也不发一条对端必然拒收的入局通知。
                throw new ArgumentException(
                    "编码后文本 " + total + " 字节超过上限 " + MaxTextBytes + " 字节", "offer");
            }

            return Prefix + base64;
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

            if (text.Length < Prefix.Length || string.CompareOrdinal(text, 0, Prefix, 0, Prefix.Length) != 0)
            {
                error = "入局通知缺少 '" + Prefix + "' 前缀（旧链载体不会被新链接受）";
                return false;
            }

            string payload = text.Substring(Prefix.Length);
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

            return TryDecodePayload(bytes, out offer, out error);
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
            return text != null && text.Length >= Prefix.Length
                && string.CompareOrdinal(text, 0, Prefix, 0, Prefix.Length) == 0;
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
}
