using System;
using System.Collections.Generic;
using System.Text;
using PMNet.Control;

namespace PMNet.Session
{
    // =====================================================================================
    //  R3-B：UDP 入局握手 + 端点消费账本（契约 §7.2）
    //
    //  为什么握手**不放在 Transport 里**（契约 §7.2 原文：「握手全部在 Transport 外」）：
    //   Transport 的数据报第一个字节是它的协议版本；握手帧第一个字节是 'P'，两者天然可分。
    //   更重要的是**语义**：Transport 收到任何合法数据报都会立刻记下 packetId 并把 ack 回给对端，
    //   对端据此退休可靠消息（生命周期/RPC）。如果未认证的字节先进了 Transport 再被上层丢弃，
    //   就出现「应用层什么都没收到、传输层却已经确认收到」的静默可靠性破洞 —— 没有 NAK、没有重传。
    //   因此次序被钉死为：
    //       IsHandshakeDatagram → PMHandshakeServer.TryHandle → 逐身份验票 → 账本消费
    //       → 建 PMSessionIdentity → 建 PMTransportConnection → TryActivate → **此后**才允许 OnDatagram。
    //   （已在 PMTransportConnection.OnDatagram 上加纵深闸门：未激活直接丢，不进 Transport。）
    //
    //  服务器身份确认的口径（契约 §7.2 原文，**必须连同限制一起理解**）：
    //   - 客户端只持票据（bearer），**没有密钥**，所以它**无法**验证 DS 的 HMAC；
    //     回一个 MAC 让客户端验，等于新开一条密钥分发路径 —— 本轮**不做**，也不自造 DTLS。
    //   - 首版做的是**关联性证明**（不是密码学证明）：ClientHello 带 16 字节新鲜 nonce，
    //     ServerHello 必须回带该 nonce + 票据 SHA256 + 对局/DS/世代/摘要，客户端与
    //     「Lobby 经已认证 TCP 下发的 offer」交叉比对，任一处不符即不采信该端点为 DS。
    //   - 能挡住：盲打/伪造地址、旧响应重放（nonce 新鲜）、错局/错代次/错摘要；
    //     挡不住：能读到票据的本机攻击者。这是**有意接受**的边界，不得宣传成「服务器认证完成」。
    //
    //  反放大与有界（契约 §7.2 原文）：
    //   - **失败无回包**：MAC 无效 / 未绑端点 / 账本拒绝 ⇒ 一个字节都不回（不做「告诉你为什么被拒」）。
    //   - **成功应答不大于请求**：客户端在 ClientHello 里按「已知的 ServerHello 精确长度」填充，
    //     服务端在回包前**再核一次** response.Length <= request.Length，不满足就拒发。
    //   - 单帧最多 <see cref="PMHandshakeCodec.MaxFrameBytes"/>（1200）字节，超限当场拒绝、不解析。
    //   - 账本键是**票据全量 SHA-256**（32 字节），不是 32 位指纹（R3-A 审查已判指纹只能做诊断）。
    //   - 未认证端点不分配无界状态：失败端点表有上限、有冷却窗口。
    // =====================================================================================

    /// <summary>握手帧类型（契约 §7.2：独立 magic/version 的三种帧）。</summary>
    public enum PMHandshakeKind : byte
    {
        /// <summary>客户端 → DS：携带票据 + 新鲜 nonce。</summary>
        ClientHello = 1,

        /// <summary>DS → 客户端：回带 nonce/票据摘要/对局身份。</summary>
        ServerHello = 2,

        /// <summary>
        /// 「显式拒绝」。**本实现（R3-B）从不发送它** —— 契约 §7.2 冻结的是「失败无回包」，
        /// 回一个拒绝帧等于向任意探测者确认「这里有一个 DS，而且你的票有点问题」。
        /// 解码能力保留，是为了让客户端能识别并拒绝一个**别版本/别实现**发来的拒绝帧
        /// （不把它当成功，也不无限重试）。
        /// </summary>
        ServerReject = 3,
    }

    /// <summary>客户端侧握手判定结果（「发什么 / 收到什么算通过」）。</summary>
    public enum PMHandshakeClientOutcome : byte
    {
        /// <summary>不是本端可用的握手帧。</summary>
        Malformed = 0,

        /// <summary>通过关联校验（唯一可以据以激活的结果）。</summary>
        Accepted = 1,

        /// <summary>看起来合法但与 offer 不符（错 nonce/摘要/对局/世代/身份）⇒ 丢弃并重新握手。</summary>
        Ignored = 2,

        /// <summary>对端明确拒绝（收到 ServerReject 帧）。</summary>
        Rejected = 3,
    }

    /// <summary>DS 侧处理一个入站帧后所处的阶段（诊断用；拒绝时给出「卡在哪一步」）。</summary>
    public enum PMHandshakeStage : byte
    {
        /// <summary>不是握手帧。</summary>
        None = 0,

        /// <summary>帧结构非法（长度/版本/尾部/字段越界）。</summary>
        Frame = 1,

        /// <summary>廉价预检失败（客户端声明的摘要/世代与 DS 不符）——在花钱验 MAC 之前就拒。</summary>
        Hash = 2,

        /// <summary>逐身份验票失败（MAC/字段/过期/不在名册）。</summary>
        Roster = 3,

        /// <summary>账本拒绝（第二端点/墓碑/uid 占用/容量）。</summary>
        Ledger = 4,

        /// <summary>通过（只有这一步才允许回包）。</summary>
        Accepted = 5,
    }

    /// <summary>一个 ClientHello 的解码结果。票据用「偏移 + 长度」指向入站缓冲，不复制（账本要按原字节算哈希）。</summary>
    public struct PMHandshakeClientHello
    {
        /// <summary>票据所在缓冲（通常是宿主收到的整条数据报）。</summary>
        public byte[] Ticket;

        /// <summary>票据在 <see cref="Ticket"/> 中的偏移。</summary>
        public int TicketOffset;

        /// <summary>票据字节数。</summary>
        public int TicketCount;

        /// <summary>客户端新鲜 nonce（16 字节）。</summary>
        public byte[] ClientNonce;

        /// <summary>客户端声明的协议摘要（与票据内字段重复是**故意的**：让 DS 在验 MAC 前就能廉价拒掉）。</summary>
        public uint ProtocolHash;

        /// <summary>客户端声明的会话世代（同上，用于廉价预检）。</summary>
        public uint Epoch;

        /// <summary>请求填充字节数（用于保证「应答不大于请求」）。</summary>
        public int PaddingBytes;

        /// <summary>诊断串（**不含票据字节**）。</summary>
        public override string ToString()
        {
            return "clientHello(ticket=" + TicketCount + "B nonce=" + NonceText(ClientNonce)
                + " hash=0x" + ProtocolHash.ToString("X8") + " epoch=" + Epoch
                + " pad=" + PaddingBytes + ")";
        }

        internal static string NonceText(byte[] nonce)
        {
            if (nonce == null || nonce.Length == 0)
            {
                return "<none>";
            }

            StringBuilder builder = new StringBuilder(nonce.Length * 2);
            for (int i = 0; i < nonce.Length; i++)
            {
                builder.Append(nonce[i].ToString("x2"));
            }

            return builder.ToString();
        }
    }

    /// <summary>ServerHello 的解码结果（客户端据此做关联校验）。</summary>
    public struct PMHandshakeServerHello
    {
        /// <summary>对局 ID。</summary>
        public string MatchId;

        /// <summary>DS 实例标识。</summary>
        public string DsId;

        /// <summary>会话世代。</summary>
        public uint Epoch;

        /// <summary>协议摘要。</summary>
        public uint ProtocolHash;

        /// <summary>碰撞配置摘要。</summary>
        public uint CollisionDigest;

        /// <summary>票据全量 SHA-256（32 字节）——客户端与自己所持票据的哈希交叉比对。</summary>
        public byte[] TicketDigest;

        /// <summary>回带的客户端 nonce（新鲜性；挡住旧响应重放）。</summary>
        public byte[] ClientNonce;

        /// <summary>服务端保留 nonce（首版未用于任何判定，仅作将来会话绑定材料的占位）。</summary>
        public byte[] ServerNonce;

        /// <summary>账号 ID。</summary>
        public int Uid;

        /// <summary>本局玩家 ID。</summary>
        public int PlayerId;

        /// <summary>队伍 ID。</summary>
        public int TeamId;

        /// <summary>英雄 ID。</summary>
        public int HeroId;

        /// <summary>诊断串。</summary>
        public override string ToString()
        {
            return "serverHello(match=" + (MatchId ?? string.Empty) + " ds=" + (DsId ?? string.Empty)
                + " epoch=" + Epoch + " hash=0x" + ProtocolHash.ToString("X8")
                + " digest=" + DigestText(TicketDigest)
                + " nonce=" + PMHandshakeClientHello.NonceText(ClientNonce)
                + " uid=" + Uid + " pid=" + PlayerId + " team=" + TeamId + " hero=" + HeroId + ")";
        }

        internal static string DigestText(byte[] digest)
        {
            if (digest == null || digest.Length < 4)
            {
                return "<none>";
            }

            // 只打前 4 字节：与 A1 的「指纹」口径一致（诊断用，不可逆、不是凭据）。
            return digest[0].ToString("x2") + digest[1].ToString("x2")
                 + digest[2].ToString("x2") + digest[3].ToString("x2");
        }
    }

    /// <summary>
    /// 握手帧编解码（纯函数，零 socket、零 Unity）。
    ///
    /// 线格式（**不进现有传输头**：首字节 'P' ≠ Transport 的 ProtocolVersion=1）：
    /// <code>
    /// 0  magic   'P','M','H','S'
    /// 4  version (1B raw) = 1
    /// 5  kind    (1B raw) = 1|2|3
    /// 6  body    PMNetReader/PMNetWriter（varint，与 PMApplicationEnvelope 同源，不引入第三套编码）
    /// </code>
    /// 正文一律**恰好消费**（<see cref="PMNetReader.IsAtEnd"/> 或等价的算术校验），严格拒绝尾部。
    /// </summary>
    public static class PMHandshakeCodec
    {
        /// <summary>握手帧格式版本。</summary>
        public const int FrameVersion = 1;

        /// <summary>固定头字节数（magic 4 + version 1 + kind 1）。</summary>
        public const int HeaderBytes = 6;

        /// <summary>
        /// 单帧字节上限（契约 §7.2 冻结：1200 字节）。
        ///
        /// 调查稿里的 512 是**错误估算**（票据实际可达 352 字节，加上 nonce/摘要/身份/字符串就装不下），
        /// 契约已明确「不沿用」——这里以契约为准。
        /// </summary>
        public const int MaxFrameBytes = 1200;

        /// <summary>nonce / 摘要的字节数。</summary>
        public const int NonceBytes = 16;

        /// <summary>票据 SHA-256 摘要字节数。</summary>
        public const int DigestBytes = 32;

        /// <summary>票据字节上限（与既有票据 codec 同源）。</summary>
        public const int MaxTicketBytes = PMDsTicketWire.MaxTicketBytes;

        /// <summary>标识字段的 UTF-8 上限。</summary>
        public const int MaxIdBytes = PMDsControlWire.MaxMatchIdBytes;

        /// <summary>ClientHello 填充上限（有界；填充只用于「应答不大于请求」）。</summary>
        public const int MaxPaddingBytes = 512;

        internal static readonly byte[] MagicBytes = new byte[] { (byte)'P', (byte)'M', (byte)'H', (byte)'S' };

        private static readonly byte[] VersionBytes = new byte[] { (byte)FrameVersion };

        private static readonly byte[] ClientHelloKindBytes = new byte[] { (byte)PMHandshakeKind.ClientHello };

        private static readonly byte[] ServerHelloKindBytes = new byte[] { (byte)PMHandshakeKind.ServerHello };

        private static readonly byte[] ServerRejectKindBytes = new byte[] { (byte)PMHandshakeKind.ServerReject };

        private static readonly byte[] ZeroPadding = new byte[MaxPaddingBytes];

        /// <summary>
        /// 是否是握手帧（只看头 6 字节）。Host 用它把「握手」与「Transport 数据报」分流。
        ///
        /// 刻意**不**在这里判长度上限：分类与校验分开，超限由 <c>TryRead*</c> 明确拒绝并给出原因
        /// （否则「超限」会退化成「不是握手帧」，无法归因）。
        /// </summary>
        public static bool IsHandshakeDatagram(byte[] data, int offset, int count, out PMHandshakeKind kind)
        {
            kind = PMHandshakeKind.ClientHello;

            if (data == null || offset < 0 || count < HeaderBytes
                || offset > data.Length || count > data.Length - offset)
            {
                return false;
            }

            if (data[offset] != MagicBytes[0] || data[offset + 1] != MagicBytes[1]
                || data[offset + 2] != MagicBytes[2] || data[offset + 3] != MagicBytes[3])
            {
                return false;
            }

            if (data[offset + 4] != (byte)FrameVersion)
            {
                return false;
            }

            byte rawKind = data[offset + 5];
            if (rawKind < (byte)PMHandshakeKind.ClientHello || rawKind > (byte)PMHandshakeKind.ServerReject)
            {
                return false;
            }

            kind = (PMHandshakeKind)rawKind;
            return true;
        }

        /// <summary>写固定头（magic + version + kind）。</summary>
        public static void WriteHeader(PMNetWriter writer, PMHandshakeKind kind)
        {
            if (writer == null) { throw new ArgumentNullException("writer"); }

            byte[] kindBytes;
            switch (kind)
            {
                case PMHandshakeKind.ClientHello: kindBytes = ClientHelloKindBytes; break;
                case PMHandshakeKind.ServerHello: kindBytes = ServerHelloKindBytes; break;
                default: kindBytes = ServerRejectKindBytes; break;
            }

            writer.WriteRawBytes(MagicBytes, 0, MagicBytes.Length);
            writer.WriteRawBytes(VersionBytes, 0, VersionBytes.Length);
            writer.WriteRawBytes(kindBytes, 0, kindBytes.Length);
        }

        /// <summary>
        /// ServerHello 的**精确**字节数（客户端据此算填充，从而保证「应答不大于请求」）。
        ///
        /// 为什么可以「精确」：ServerHello 的每个变长字段都来自票据绑定的同一条身份 ——
        /// MatchId/DsId 与票据一致（DS 会验），uid/playerId/teamId/heroId 与 offer 一致（客户端会验）。
        /// 因此客户端算出来的长度就是 DS 将要写出的长度；两端各自还有一道兜底：
        /// 客户端多留 <c>PaddingSafetyMarginBytes</c>，DS 回包前再核一次 response.Length &lt;= request.Length。
        /// </summary>
        public static int ComputeServerHelloBytes(string matchId, string dsId, uint epoch, uint protocolHash,
                                                  uint collisionDigest, int uid, int playerId, int teamId, int heroId)
        {
            int matchBytes = Encoding.UTF8.GetByteCount(matchId ?? string.Empty);
            int dsBytes = Encoding.UTF8.GetByteCount(dsId ?? string.Empty);

            int size = HeaderBytes;
            size += VarintSize(matchBytes) + matchBytes;
            size += VarintSize(dsBytes) + dsBytes;
            size += VarintSize(epoch);
            size += VarintSize(protocolHash);
            size += VarintSize(collisionDigest);
            size += DigestBytes;                       // ticketDigest
            size += NonceBytes;                        // clientNonceEcho
            size += VarintSize(uid) + VarintSize(playerId) + VarintSize(teamId) + VarintSize(heroId);
            size += NonceBytes;                        // serverNonce
            return size;
        }

        /// <summary>写 ClientHello 正文字段（不含头）。</summary>
        public static void WriteClientHelloBody(PMNetWriter writer, byte[] ticket, int ticketOffset, int ticketCount,
                                                byte[] clientNonce16, uint protocolHash, uint epoch, int paddingBytes)
        {
            if (writer == null) { throw new ArgumentNullException("writer"); }

            if (ticket == null || ticketOffset < 0 || ticketCount <= 0
                || ticketOffset > ticket.Length || ticketCount > ticket.Length - ticketOffset)
            {
                throw new ArgumentException("票据窗口非法", "ticket");
            }

            if (ticketCount > MaxTicketBytes)
            {
                throw new ArgumentOutOfRangeException("ticket", "票据长度 " + ticketCount + " 超过上限 " + MaxTicketBytes);
            }

            if (clientNonce16 == null || clientNonce16.Length != NonceBytes)
            {
                throw new ArgumentException("客户端 nonce 必须正好 " + NonceBytes + " 字节", "clientNonce16");
            }

            if (paddingBytes < 0 || paddingBytes > MaxPaddingBytes)
            {
                throw new ArgumentOutOfRangeException("paddingBytes", "填充必须在 0.." + MaxPaddingBytes + " 之间");
            }

            writer.WriteInt32(ticketCount);
            writer.WriteRawBytes(ticket, ticketOffset, ticketCount);
            writer.WriteRawBytes(clientNonce16, 0, NonceBytes);
            writer.WriteUInt32(protocolHash);
            writer.WriteUInt32(epoch);
            writer.WriteInt32(paddingBytes);
            if (paddingBytes > 0)
            {
                writer.WriteRawBytes(ZeroPadding, 0, paddingBytes);
            }
        }

        /// <summary>
        /// 把一条 ClientHello 写进**已经写好头**的 writer（不重置 writer：调用方可能在外层已写入内容）。
        /// </summary>
        public static void WriteClientHello(PMNetWriter writer, byte[] ticket, int ticketOffset, int ticketCount,
                                            byte[] clientNonce16, uint protocolHash, uint epoch, int paddingBytes)
        {
            WriteHeader(writer, PMHandshakeKind.ClientHello);
            WriteClientHelloBody(writer, ticket, ticketOffset, ticketCount, clientNonce16, protocolHash, epoch, paddingBytes);
        }

        /// <summary>严格解析 ClientHello（含长度上限、字段范围、严格尾部）。</summary>
        public static bool TryReadClientHello(byte[] data, int offset, int count,
                                              out PMHandshakeClientHello hello, out string error)
        {
            hello = default(PMHandshakeClientHello);
            error = null;

            if (!CheckFrame(data, offset, count, PMHandshakeKind.ClientHello, out error))
            {
                return false;
            }

            int bodyLength = count - HeaderBytes;
            PMNetReader reader = new PMNetReader(data, offset + HeaderBytes, bodyLength);

            try
            {
                int ticketLength = reader.ReadInt32();
                if (ticketLength <= 0 || ticketLength > MaxTicketBytes)
                {
                    error = "票据长度 " + ticketLength + " 越界（允许 1.." + MaxTicketBytes + "）";
                    return false;
                }

                hello.Ticket = data;
                hello.TicketOffset = offset + HeaderBytes + reader.Consumed;
                hello.TicketCount = ticketLength;
                reader.ReadRawBytesCopy(ticketLength);   // 只推进位置：票据仍指向入站缓冲（账本要按原字节算哈希）

                hello.ClientNonce = reader.ReadRawBytesCopy(NonceBytes);
                hello.ProtocolHash = reader.ReadUInt32();
                hello.Epoch = reader.ReadUInt32();

                int padding = reader.ReadInt32();
                if (padding < 0 || padding > MaxPaddingBytes)
                {
                    error = "填充长度 " + padding + " 越界（允许 0.." + MaxPaddingBytes + "）";
                    return false;
                }

                hello.PaddingBytes = padding;

                // 严格尾部：读到这里之后必须**恰好**剩下 padding 字节（不做任何分配）。
                if (reader.Consumed + padding != bodyLength)
                {
                    error = "ClientHello 长度与字段/填充不符（严格拒绝尾部）";
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = "ClientHello 字段解码失败：" + ex.GetType().Name;
                return false;
            }

            return true;
        }

        /// <summary>写 ServerHello 正文字段（不含头）。字段会做范围检查（宁可抛也不写出畸形帧）。</summary>
        public static void WriteServerHelloBody(PMNetWriter writer, PMHandshakeServerHello hello)
        {
            if (writer == null) { throw new ArgumentNullException("writer"); }

            WriteId(writer, hello.MatchId, "MatchId");
            WriteId(writer, hello.DsId, "DsId");
            writer.WriteUInt32(hello.Epoch);
            writer.WriteUInt32(hello.ProtocolHash);
            writer.WriteUInt32(hello.CollisionDigest);
            WriteFixed(writer, hello.TicketDigest, DigestBytes, "TicketDigest");
            WriteFixed(writer, hello.ClientNonce, NonceBytes, "ClientNonce");
            writer.WriteInt32(hello.Uid);
            writer.WriteInt32(hello.PlayerId);
            writer.WriteInt32(hello.TeamId);
            writer.WriteInt32(hello.HeroId);
            WriteFixed(writer, hello.ServerNonce, NonceBytes, "ServerNonce");
        }

        /// <summary>写一条完整 ServerHello。</summary>
        public static void WriteServerHello(PMNetWriter writer, PMHandshakeServerHello hello)
        {
            WriteHeader(writer, PMHandshakeKind.ServerHello);
            WriteServerHelloBody(writer, hello);
        }

        /// <summary>严格解析 ServerHello。</summary>
        public static bool TryReadServerHello(byte[] data, int offset, int count,
                                              out PMHandshakeServerHello hello, out string error)
        {
            hello = default(PMHandshakeServerHello);
            error = null;

            if (!CheckFrame(data, offset, count, PMHandshakeKind.ServerHello, out error))
            {
                return false;
            }

            PMNetReader reader = new PMNetReader(data, offset + HeaderBytes, count - HeaderBytes);

            try
            {
                hello.MatchId = ReadId(reader, "MatchId", out error);
                if (error != null) { return false; }

                hello.DsId = ReadId(reader, "DsId", out error);
                if (error != null) { return false; }

                hello.Epoch = reader.ReadUInt32();
                hello.ProtocolHash = reader.ReadUInt32();
                hello.CollisionDigest = reader.ReadUInt32();
                hello.TicketDigest = reader.ReadRawBytesCopy(DigestBytes);
                hello.ClientNonce = reader.ReadRawBytesCopy(NonceBytes);
                hello.Uid = reader.ReadInt32();
                hello.PlayerId = reader.ReadInt32();
                hello.TeamId = reader.ReadInt32();
                hello.HeroId = reader.ReadInt32();
                hello.ServerNonce = reader.ReadRawBytesCopy(NonceBytes);

                if (!reader.IsAtEnd)
                {
                    error = "ServerHello 存在多余尾部字节（严格拒绝）";
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = "ServerHello 字段解码失败：" + ex.GetType().Name;
                return false;
            }

            return true;
        }

        /// <summary>写一条 ServerReject（**本实现不发送它**，仅为协议完整性保留）。</summary>
        public static void WriteServerReject(PMNetWriter writer, PMDsTicketVerdict verdict, byte stage, uint retryAfterMs)
        {
            if (writer == null) { throw new ArgumentNullException("writer"); }

            WriteHeader(writer, PMHandshakeKind.ServerReject);
            writer.WriteInt32((int)verdict);
            writer.WriteInt32(stage);
            writer.WriteUInt32(retryAfterMs);
        }

        /// <summary>解析 ServerReject（客户端用它识别「对端明确拒绝」，不当作成功）。</summary>
        public static bool TryReadServerReject(byte[] data, int offset, int count,
                                               out PMDsTicketVerdict verdict, out byte stage, out uint retryAfterMs,
                                               out string error)
        {
            verdict = PMDsTicketVerdict.Malformed;
            stage = 0;
            retryAfterMs = 0u;
            error = null;

            if (!CheckFrame(data, offset, count, PMHandshakeKind.ServerReject, out error))
            {
                return false;
            }

            PMNetReader reader = new PMNetReader(data, offset + HeaderBytes, count - HeaderBytes);
            try
            {
                verdict = (PMDsTicketVerdict)reader.ReadInt32();
                stage = (byte)reader.ReadInt32();
                retryAfterMs = reader.ReadUInt32();

                if (!reader.IsAtEnd)
                {
                    error = "ServerReject 存在多余尾部字节（严格拒绝）";
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = "ServerReject 字段解码失败：" + ex.GetType().Name;
                return false;
            }

            return true;
        }

        internal static int VarintSize(long value)
        {
            ulong raw = unchecked((ulong)value);
            int size = 1;
            while (raw >= 0x80UL)
            {
                raw >>= 7;
                size++;
            }

            return size;
        }

        private static bool CheckFrame(byte[] data, int offset, int count, PMHandshakeKind expected, out string error)
        {
            error = null;

            if (data == null)
            {
                error = "握手帧缓冲为 null";
                return false;
            }

            if (offset < 0 || count < 0 || offset > data.Length || count > data.Length - offset)
            {
                error = "握手帧窗口越界";
                return false;
            }

            if (count > MaxFrameBytes)
            {
                // 「超限当场拒绝」：不解析、不回包（回包本身就是放大）。
                error = "握手帧长度 " + count + " 字节超过上限 " + MaxFrameBytes + " 字节";
                return false;
            }

            if (count < HeaderBytes)
            {
                error = "握手帧长度 " + count + " 字节不足以容纳固定头";
                return false;
            }

            if (data[offset] != MagicBytes[0] || data[offset + 1] != MagicBytes[1]
                || data[offset + 2] != MagicBytes[2] || data[offset + 3] != MagicBytes[3])
            {
                error = "握手帧魔数不匹配";
                return false;
            }

            if (data[offset + 4] != (byte)FrameVersion)
            {
                error = "握手帧版本不受支持：" + data[offset + 4];
                return false;
            }

            if (data[offset + 5] != (byte)expected)
            {
                error = "握手帧类型不符：收到 " + data[offset + 5] + "，期望 " + (byte)expected;
                return false;
            }

            return true;
        }

        private static void WriteId(PMNetWriter writer, string value, string what)
        {
            int bytes = Encoding.UTF8.GetByteCount(value ?? string.Empty);
            if (bytes > MaxIdBytes)
            {
                throw new ArgumentException(what + " 的 UTF-8 长度 " + bytes + " 字节超过上限 " + MaxIdBytes, "hello");
            }

            if (bytes == 0)
            {
                writer.WriteInt32(0);
                return;
            }

            writer.WriteStringValue(value);
        }

        private static void WriteFixed(PMNetWriter writer, byte[] value, int expected, string what)
        {
            if (value == null || value.Length != expected)
            {
                throw new ArgumentException(what + " 必须正好 " + expected + " 字节", "hello");
            }

            writer.WriteRawBytes(value, 0, expected);
        }

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

            try
            {
                return reader.ReadStringValue();
            }
            catch (Exception)
            {
                error = what + " 越出帧范围";
                return null;
            }
        }
    }

    /// <summary>账本拒绝原因（**每一种都是明确拒绝**，不合并成 bool）。</summary>
    public enum PMDsLedgerReject : byte
    {
        /// <summary>接受。</summary>
        None = 0,

        /// <summary>票据已过期（只影响**新的入场/重试**，不影响已激活会话）。</summary>
        Expired = 1,

        /// <summary>该来源端点已被**另一张**票据占用。</summary>
        EndpointTaken = 2,

        /// <summary>同一张票据来自**第二个**端点（契约 §7.2：拒绝且不回包）。</summary>
        DigestRebound = 3,

        /// <summary>该票据已消费过、其连接已断开 ⇒ 墓碑未过期（不允许同票重连）。</summary>
        DigestTombstoned = 4,

        /// <summary>该 uid 已被**另一张**票据占用（早于桥的 uid 查重，避免白烧一次激活）。</summary>
        UidBusy = 5,

        /// <summary>绑定数已达上限。</summary>
        Capacity = 6,

        /// <summary>端点键非法或过长（不为未认证端点分配无界状态）。</summary>
        BadEndpoint = 7,

        /// <summary>没有通过验票（防御性前置；正常路径不会走到）。</summary>
        NotVerified = 8,
    }

    /// <summary>一次成功消费得到的会话绑定（**连接ID 在本账本生命周期内不复用**）。</summary>
    public struct PMSessionBinding
    {
        /// <summary>连接序号（> 0，由账本单调分配）。</summary>
        public int ConnectionId;

        /// <summary>账号 ID。</summary>
        public int Uid;

        /// <summary>本局玩家 ID。</summary>
        public int PlayerId;

        /// <summary>队伍 ID。</summary>
        public int TeamId;

        /// <summary>英雄 ID。</summary>
        public int HeroId;

        /// <summary>会话世代。</summary>
        public uint Epoch;

        /// <summary>票据绑定的协议摘要（用于构造 <see cref="PMSessionIdentity"/> 的对端摘要）。</summary>
        public uint ProtocolHash;

        /// <summary>对局 ID。</summary>
        public string MatchId;

        /// <summary>DS ID。</summary>
        public string DsId;

        /// <summary>消费该票据的来源端点键。</summary>
        public string EndpointKey;

        /// <summary>
        /// 票据全量 SHA-256（32 字节）。**与账本内部的键数组是两个独立数组**：
        /// 外部就地改写这个副本（卫生清零 / 复用缓冲等）不会影响账本的键与重放保护；
        /// 反之账本也不会把内部键暴露出去。内容比较（如撤回消费）仍然等价。
        /// </summary>
        public byte[] TicketDigest;

        /// <summary>诊断串（不含票据字节）。</summary>
        public override string ToString()
        {
            return "binding#conn" + ConnectionId + " uid=" + Uid + " pid=" + PlayerId
                + " team=" + TeamId + " hero=" + HeroId + " epoch=" + Epoch
                + " hash=0x" + ProtocolHash.ToString("X8")
                + " digest=" + PMHandshakeServerHello.DigestText(TicketDigest)
                + " endpoint=" + (EndpointKey ?? string.Empty);
        }
    }

    /// <summary>账本计数（诊断/门禁断言；非 0 才算「真的跑到了」）。</summary>
    public struct PMDsEndpointLedgerStats
    {
        /// <summary>当前存活绑定数。</summary>
        public int ActiveBindings;

        /// <summary>当前未过期墓碑数。</summary>
        public int Tombstones;

        /// <summary>分配过的连接序号总数（单调，不复用）。</summary>
        public long ConnectionsAllocated;

        /// <summary>成功消费（含幂等重试）次数。</summary>
        public long Consumed;

        /// <summary>同端点同票的幂等重试次数（**没有**新建绑定）。</summary>
        public long IdempotentRetries;

        /// <summary>票据过期被拒次数。</summary>
        public long RejectedExpired;

        /// <summary>端点已被别的票据占用被拒次数。</summary>
        public long RejectedEndpointTaken;

        /// <summary>同票第二端点被拒次数。</summary>
        public long RejectedDigestRebound;

        /// <summary>墓碑未过期被拒次数（同票换端点/重连）。</summary>
        public long RejectedTombstoned;

        /// <summary>uid 被别的票据占用被拒次数。</summary>
        public long RejectedUidBusy;

        /// <summary>容量耗尽被拒次数。</summary>
        public long RejectedCapacity;

        /// <summary>端点键非法被拒次数。</summary>
        public long RejectedBadEndpoint;

        /// <summary>未通过验票（防御性前置）被拒次数。</summary>
        public long RejectedNotVerified;

        /// <summary>主动释放（断开）次数。</summary>
        public long Released;

        /// <summary>已消费但未能建链/回包而撤回的次数（不写墓碑）。</summary>
        public long Renounced;

        /// <summary>过期条目（含墓碑）被清理的次数。</summary>
        public long ExpiredPrunes;

        /// <summary>墓碑超容量被淘汰的次数。</summary>
        public long TombstoneEvictions;

        /// <summary>
        /// 因「墓碑容量淘汰导致重放保护出现盲区」而**拒绝新入场**的次数（fail-closed）。
        ///
        /// 被淘汰的墓碑对应的是**尚未过期**的已消费票据；账本已经不再记得它的摘要，
        /// 因此在该票到期之前，一切**未知摘要**的新消费一律被拒（已有绑定/墓碑的幂等重试不受影响）。
        /// 票面过期后重放会被验票阶段直接拒（Expired），账本自动恢复接纳。
        /// </summary>
        public long ReplayBlindRefusals;

        /// <summary>整体释放会话次数。</summary>
        public long SessionResets;

        /// <summary>诊断串。</summary>
        public override string ToString()
        {
            return "ledger(active=" + ActiveBindings + " tombstones=" + Tombstones
                + " alloc=" + ConnectionsAllocated + " consumed=" + Consumed + " idem=" + IdempotentRetries
                + " rej[exp=" + RejectedExpired + ",ep=" + RejectedEndpointTaken
                + ",rebound=" + RejectedDigestRebound + ",tomb=" + RejectedTombstoned
                + ",uid=" + RejectedUidBusy + ",cap=" + RejectedCapacity
                + ",badEp=" + RejectedBadEndpoint + ",unver=" + RejectedNotVerified + "]"
                + " released=" + Released + " renounced=" + Renounced
                + " pruned=" + ExpiredPrunes + " evicted=" + TombstoneEvictions
                + " blind=" + ReplayBlindRefusals + ")";
        }
    }

    /// <summary>票据全量 SHA-256 的结构化字典键（**不是** 32 位指纹）。</summary>
    internal struct PMTicketDigestKey : IEquatable<PMTicketDigestKey>
    {
        public readonly byte[] Bytes;

        public PMTicketDigestKey(byte[] bytes)
        {
            Bytes = bytes;
        }

        public bool Equals(PMTicketDigestKey other)
        {
            if (ReferenceEquals(Bytes, other.Bytes)) { return true; }
            if (Bytes == null || other.Bytes == null || Bytes.Length != other.Bytes.Length) { return false; }

            for (int i = 0; i < Bytes.Length; i++)
            {
                if (Bytes[i] != other.Bytes[i]) { return false; }
            }

            return true;
        }

        public override bool Equals(object obj)
        {
            return obj is PMTicketDigestKey && Equals((PMTicketDigestKey)obj);
        }

        public override int GetHashCode()
        {
            if (Bytes == null || Bytes.Length < 4) { return 0; }
            return (Bytes[0] << 24) | (Bytes[1] << 16) | (Bytes[2] << 8) | Bytes[3];
        }
    }

    /// <summary>
    /// 端点消费账本（契约 §7.2：**DS 侧每局一个**）。
    ///
    /// 它存在的唯一理由：**MAC 真实性不等于防重放**（A1 的 <see cref="PMDsTicketVerifier"/> 明确只验证
    /// 真实性与字段）。票据是 bearer 凭据，任何拿到它的人都能通过 MAC 校验；能挡住「换一个来源地址再用一次」
    /// 的只有这张账本。
    ///
    /// 三条可测判据（契约审查原文）：
    ///   ① 同一票据来自**第二个端点** ⇒ 被拒且**不返回任何业务字节**（本类只回 false，回不回包由上层决定）；
    ///   ② 同一票据**同端点**重发 ⇒ 无新 ConnectionId、无新绑定（幂等）；
    ///   ③ 票据过期后绑定被回收且重放被拒（过期票据在验票阶段就会失败）。
    ///
    /// 另外两条纪律：
    ///   - **断开即墓碑**：已激活连接断开后，旧票在过期前不得再被消费（不允许同票换端点重连）。
    ///   - **墓碑上限饱和时 fail-closed**：墓碑表有上限（默认 64），超出时淘汰最旧的墓碑；
    ///     而被淘汰的票据**可能尚未过期**，账本从此不再记得它 ⇒ 同票换端点重放会检测不到。
    ///     因此淘汰时记下该票的到期时刻，在该时刻之前**拒绝一切未知摘要的新消费**
    ///     （`Capacity`，计入 `ReplayBlindRefusals`），票面过期后自动恢复接纳。
    ///     宁可在异常抖动时拒绝新入场，也不允许「已消费旧票换端点复用」。
    ///   - **账本自持其键字节**：<see cref="PMSessionBinding.TicketDigest"/> 是内部键的**独立副本**，
    ///     外部就地改写不影响账本（否则“清空摘要数组做卫生处理”这类写法会直接抹掉重放保护）。
    ///   - **票据过期不影响已激活会话**：本类**不**因为到期而摘除 Active 绑定（契约 §7.2 原文：
    ///     「入场票据到期只影响新的入场/重试，不能把已激活正常对局在 120 秒票据过期时踢掉」）。
    ///     到期只驱动两类清理：新入场的验票拒绝，以及墓碑的回收。
    /// </summary>
    public sealed class PMDsEndpointLedger
    {
        /// <summary>默认绑定上限（契约 §2：名册最多 6 人，这里不额外留重连余量——重连必须换新票）。</summary>
        public const int DefaultMaxBindings = PMDsControlWire.MaxRosterPlayers;

        /// <summary>墓碑上限（有界；超出按最旧的淘汰）。</summary>
        public const int DefaultMaxTombstones = 64;

        /// <summary>端点键的字符上限（IPv6 + scope + 端口远小于它）。</summary>
        public const int MaxEndpointKeyChars = 64;

        private sealed class Entry
        {
            public byte[] Digest;
            public string EndpointKey;
            public PMSessionBinding Binding;
            public long ExpiresAtUnixSeconds;
            public bool Tombstoned;
            public long TombstoneSequence;
        }

        private readonly Dictionary<PMTicketDigestKey, Entry> _byDigest =
            new Dictionary<PMTicketDigestKey, Entry>();

        private readonly Dictionary<string, Entry> _byEndpoint =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        private readonly Dictionary<int, Entry> _byUid = new Dictionary<int, Entry>();

        private readonly int _maxBindings;
        private readonly int _maxTombstones;

        private int _nextConnectionId;
        private long _tombstoneSequence;
        private PMDsEndpointLedgerStats _stats;

        /// <summary>
        /// 重放保护盲区窗口的右端（Unix 秒；0 表示无盲区）。
        ///
        /// 成因：墓碑超上限时只能淘汰最旧的墓碑，而被淘汰的票据**可能尚未过期** ——
        /// 账本从此不再记得它的摘要，同票换端点重放就检测不到了。
        /// 处置：记下被淘汰票据的到期时刻；在该时刻之前，一切**未知摘要**的新消费一律拒绝
        /// （fail-closed）。票面过期后重放会被验票阶段直接拒（Expired），窗口自动失效。
        /// 这样既保住「墓碑 ≤ 上限」的有界性，也不会出现「旧票换端点复用」。
        /// </summary>
        private long _replayBlindUntilUnixSeconds;

        public PMDsEndpointLedger()
            : this(DefaultMaxBindings, DefaultMaxTombstones)
        {
        }

        public PMDsEndpointLedger(int maxBindings, int maxTombstones)
        {
            _maxBindings = maxBindings < 0 ? 0 : maxBindings;
            _maxTombstones = maxTombstones < 0 ? 0 : maxTombstones;
        }

        /// <summary>绑定上限。</summary>
        public int MaxBindings { get { return _maxBindings; } }

        /// <summary>当前存活绑定数。</summary>
        public int ActiveBindingCount { get { return _byEndpoint.Count; } }

        /// <summary>重放保护盲区窗口的右端（Unix 秒；0 表示无盲区）。诊断用。</summary>
        public long ReplayBlindUntilUnixSeconds { get { return _replayBlindUntilUnixSeconds; } }

        /// <summary>诊断计数（含实时字段）。</summary>
        public PMDsEndpointLedgerStats Stats
        {
            get
            {
                PMDsEndpointLedgerStats snapshot = _stats;
                snapshot.ActiveBindings = _byEndpoint.Count;
                snapshot.Tombstones = CountTombstones();
                return snapshot;
            }
        }

        /// <summary>
        /// 尝试消费一张**已验证**的票据。
        ///
        /// 前置：<paramref name="verification"/> 必须来自 A1 的验票（本类**不验 MAC**）。
        /// 入参与状态：<paramref name="ticket"/> 是原始票据字节（账本自行算全量 SHA-256，不依赖 A1）。
        /// </summary>
        public bool TryConsume(byte[] ticket, int offset, int count, in PMDsTicketVerification verification,
                               string endpointKey, long nowUnixSeconds,
                               out PMSessionBinding binding, out PMDsLedgerReject reject)
        {
            binding = default(PMSessionBinding);
            reject = PMDsLedgerReject.None;

            if (ticket == null || offset < 0 || count <= 0
                || offset > ticket.Length || count > ticket.Length - offset)
            {
                _stats.RejectedNotVerified++;
                reject = PMDsLedgerReject.NotVerified;
                return false;
            }

            if (verification.Verdict != PMDsTicketVerdict.Valid)
            {
                if (verification.Verdict == PMDsTicketVerdict.Expired)
                {
                    _stats.RejectedExpired++;
                    reject = PMDsLedgerReject.Expired;
                    return false;
                }

                _stats.RejectedNotVerified++;
                reject = PMDsLedgerReject.NotVerified;
                return false;
            }

            if (endpointKey == null || endpointKey.Length == 0 || endpointKey.Length > MaxEndpointKeyChars)
            {
                _stats.RejectedBadEndpoint++;
                reject = PMDsLedgerReject.BadEndpoint;
                return false;
            }

            PruneExpired(nowUnixSeconds);

            byte[] digest = PMDsCrypto.Sha256(ticket, offset, count);
            PMTicketDigestKey key = new PMTicketDigestKey(digest);

            Entry existing;
            if (_byDigest.TryGetValue(key, out existing))
            {
                if (!string.Equals(existing.EndpointKey, endpointKey, StringComparison.Ordinal))
                {
                    // 同一张票据换端点：无论原绑定是活的还是墓碑，都拒绝。
                    if (existing.Tombstoned) { _stats.RejectedTombstoned++; reject = PMDsLedgerReject.DigestTombstoned; }
                    else { _stats.RejectedDigestRebound++; reject = PMDsLedgerReject.DigestRebound; }
                    return false;
                }

                if (existing.Tombstoned)
                {
                    // 同端点但该票已断开消费过：墓碑未过期 ⇒ 不允许重连（Lobby 必须显式重发新票）。
                    _stats.RejectedTombstoned++;
                    reject = PMDsLedgerReject.DigestTombstoned;
                    return false;
                }

                // 同票同端点：幂等 —— 返回**同一**绑定，不新建、不分配新 ConnectionId。
                _stats.Consumed++;
                _stats.IdempotentRetries++;
                binding = existing.Binding;
                return true;
            }

            Entry occupant;
            if (_byEndpoint.TryGetValue(endpointKey, out occupant))
            {
                _stats.RejectedEndpointTaken++;
                reject = PMDsLedgerReject.EndpointTaken;
                return false;
            }

            // 墓碑容量淘汰过、且被淘汰票据尚未到期 ⇒ 本端已失去对「那些摘要」的重放保护，
            // 因此**拒绝一切未知摘要的新消费**（fail-closed），而不是把旧票换端点当真新票接纳。
            // 已有绑定/墓碑（上面的分支）不受影响：那些摘要账本仍然记得。
            if (nowUnixSeconds < _replayBlindUntilUnixSeconds)
            {
                _stats.ReplayBlindRefusals++;
                reject = PMDsLedgerReject.Capacity;
                return false;
            }

            Entry uidHolder;
            if (_byUid.TryGetValue(verification.Identity.Uid, out uidHolder))
            {
                _stats.RejectedUidBusy++;
                reject = PMDsLedgerReject.UidBusy;
                return false;
            }

            if (_byEndpoint.Count >= _maxBindings)
            {
                _stats.RejectedCapacity++;
                reject = PMDsLedgerReject.Capacity;
                return false;
            }

            // ConnectionId 单调、**不复用**（防「旧连接的迟到包命中新连接」）。
            _nextConnectionId++;
            _stats.ConnectionsAllocated++;

            PMSessionBinding created = new PMSessionBinding();
            created.ConnectionId = _nextConnectionId;
            created.Uid = verification.Identity.Uid;
            created.PlayerId = verification.Identity.PlayerId;
            created.TeamId = verification.Identity.TeamId;
            created.HeroId = verification.Identity.HeroId;
            created.Epoch = verification.Epoch;
            created.ProtocolHash = verification.ProtocolHash;
            created.MatchId = verification.MatchId;
            created.DsId = verification.DsId;
            created.EndpointKey = endpointKey;
            // 账本键自持：`entry.Digest`（= 字典键用的字节）是内部数组，
            // 暴露给宿主的是**独立副本**，外部就地改写不会破坏账本键与重放保护。
            created.TicketDigest = (byte[])digest.Clone();

            Entry entry = new Entry();
            entry.Digest = digest;
            entry.EndpointKey = endpointKey;
            entry.Binding = created;
            entry.ExpiresAtUnixSeconds = verification.ExpiresAtUnixSeconds;
            entry.Tombstoned = false;

            _byDigest[key] = entry;
            _byEndpoint[endpointKey] = entry;
            _byUid[created.Uid] = entry;

            _stats.Consumed++;
            binding = created;
            return true;
        }

        /// <summary>
        /// 已消费但**没能建链/回包**（激活失败、端点容量已满、回包会放大等）时撤回消费。
        ///
        /// 与 <see cref="ReleaseEndpoint"/> 的关键区别：**不写墓碑**。墓碑的语义是
        /// 「这张票曾经建立过一个会话，现在它不能再用」，而这里从来没有建立过任何东西——
        /// 写墓碑会让一个因为环境问题（摘要不符）而失败的客户端**永远**无法重试。
        /// </summary>
        public bool RenounceConsume(byte[] ticketDigest, string endpointKey)
        {
            if (ticketDigest == null || ticketDigest.Length != PMHandshakeCodec.DigestBytes)
            {
                return false;
            }

            PMTicketDigestKey key = new PMTicketDigestKey(ticketDigest);
            Entry entry;
            if (!_byDigest.TryGetValue(key, out entry) || entry.Tombstoned)
            {
                return false;
            }

            RemoveEntry(key, entry);
            _stats.Renounced++;
            return true;
        }

        /// <summary>
        /// 释放某个端点的绑定（连接断开 / 端点被丢弃）。
        ///
        /// 票据**未过期**时留下墓碑：旧票在过期前不能被再次消费（含换端点重连）。
        /// 已过期时直接丢弃条目（重放会在验票阶段被 Expired 拒掉）。
        /// </summary>
        public bool ReleaseEndpoint(string endpointKey, long nowUnixSeconds)
        {
            if (endpointKey == null) { return false; }

            PruneExpired(nowUnixSeconds);

            Entry entry;
            if (!_byEndpoint.TryGetValue(endpointKey, out entry))
            {
                return false;
            }

            _byEndpoint.Remove(endpointKey);

            Entry uidEntry;
            if (_byUid.TryGetValue(entry.Binding.Uid, out uidEntry) && ReferenceEquals(uidEntry, entry))
            {
                _byUid.Remove(entry.Binding.Uid);
            }

            PMTicketDigestKey key = new PMTicketDigestKey(entry.Digest);

            if (entry.ExpiresAtUnixSeconds <= nowUnixSeconds)
            {
                _byDigest.Remove(key);
                _stats.ExpiredPrunes++;
            }
            else
            {
                entry.Tombstoned = true;
                entry.TombstoneSequence = ++_tombstoneSequence;
                EnforceTombstoneCapacity();
            }

            _stats.Released++;
            return true;
        }

        /// <summary>整局释放：清空全部绑定与墓碑（终态调用）。</summary>
        public void ReleaseSession()
        {
            _byDigest.Clear();
            _byEndpoint.Clear();
            _byUid.Clear();

            // 该局的标签已全部丢弃，盲区窗口也就没有保护对象了。
            // （注意：`ReleaseSession` 本身就已经放弃整局的重放保护，这是终态语义。）
            _replayBlindUntilUnixSeconds = 0L;

            _stats.SessionResets++;
        }

        /// <summary>清理已过期的墓碑（由 Pump 定期调用，保证状态有界）。</summary>
        public int PruneExpired(long nowUnixSeconds)
        {
            int removed = 0;

            if (_byDigest.Count == 0)
            {
                return 0;
            }

            List<PMTicketDigestKey> doomed = null;
            foreach (KeyValuePair<PMTicketDigestKey, Entry> pair in _byDigest)
            {
                Entry entry = pair.Value;
                if (!entry.Tombstoned || entry.ExpiresAtUnixSeconds > nowUnixSeconds)
                {
                    continue;
                }

                if (doomed == null) { doomed = new List<PMTicketDigestKey>(); }
                doomed.Add(pair.Key);
            }

            if (doomed == null)
            {
                return 0;
            }

            for (int i = 0; i < doomed.Count; i++)
            {
                _byDigest.Remove(doomed[i]);
                removed++;
            }

            _stats.ExpiredPrunes += removed;
            return removed;
        }

        private void EnforceTombstoneCapacity()
        {
            int tombstones = CountTombstones();
            while (tombstones > _maxTombstones && tombstones > 0)
            {
                PMTicketDigestKey oldestKey = default(PMTicketDigestKey);
                Entry oldest = null;

                foreach (KeyValuePair<PMTicketDigestKey, Entry> pair in _byDigest)
                {
                    if (!pair.Value.Tombstoned)
                    {
                        continue;
                    }

                    if (oldest == null || pair.Value.TombstoneSequence < oldest.TombstoneSequence)
                    {
                        oldest = pair.Value;
                        oldestKey = pair.Key;
                    }
                }

                if (oldest == null)
                {
                    return;
                }

                // 被淘汰的墓碑在 `ReleaseEndpoint` 里只可能「票据尚未过期」时才被写出来
                // （过期条目当场回收）。因此这里淘汰的一定是一张**仍可能被重放**的票：
                // 把它的到期时刻记为盲区右端，期间拒绝新消费（fail-closed），
                // 到期后重放会被验票阶段直接拒（Expired），无需账本再记住它。
                if (oldest.ExpiresAtUnixSeconds > _replayBlindUntilUnixSeconds)
                {
                    _replayBlindUntilUnixSeconds = oldest.ExpiresAtUnixSeconds;
                }

                _byDigest.Remove(oldestKey);
                _stats.TombstoneEvictions++;
                tombstones--;
            }
        }

        private int CountTombstones()
        {
            int count = 0;
            foreach (KeyValuePair<PMTicketDigestKey, Entry> pair in _byDigest)
            {
                if (pair.Value.Tombstoned)
                {
                    count++;
                }
            }

            return count;
        }

        private void RemoveEntry(PMTicketDigestKey key, Entry entry)
        {
            _byDigest.Remove(key);

            Entry endpointEntry;
            if (_byEndpoint.TryGetValue(entry.EndpointKey, out endpointEntry) && ReferenceEquals(endpointEntry, entry))
            {
                _byEndpoint.Remove(entry.EndpointKey);
            }

            Entry uidEntry;
            if (_byUid.TryGetValue(entry.Binding.Uid, out uidEntry) && ReferenceEquals(uidEntry, entry))
            {
                _byUid.Remove(entry.Binding.Uid);
            }
        }
    }

    // =====================================================================================
    //  客户端握手状态机（只回答「发什么 / 收到什么算通过」；不碰 socket、不碰 Unity）
    // =====================================================================================

    /// <summary>
    /// 客户端握手状态机。
    ///
    /// 它做三件事：
    ///   1. 生成 ClientHello（每次**新 nonce**；重试由调用方限速）；
    ///   2. 解析 ServerHello 并做**关联校验**：nonce 新鲜性 + 票据 SHA-256 + 对局/DS/世代/摘要/身份；
    ///   3. 明确**不做**什么：不验证 DS 的 MAC（客户端没有密钥，见文件头），因此这是
    ///      「与 Lobby 通知的关联校验」，**不是密码学服务器认证**。
    /// </summary>
    public sealed class PMHandshakeClient
    {
        /// <summary>连续多少次「与 offer 不符」就断定该端点不是本局的 DS（不再无限重试）。</summary>
        public const int MaxIdentityMismatches = 8;

        /// <summary>
        /// 「应答不大于请求」的**显式**安全余量（字节）。
        ///
        /// 为什么要显式留余量，而不是指望「票据天生比 ServerHello 大」：
        /// 票据长度 = 96 + MatchId + DsId，ServerHello 也随 MatchId/DsId 增长，
        /// 因此两者只差一个与内容无关的常数（实测约 33 字节）。如果把保障建立在这个巧合上，
        /// 将来一旦票据/ServerHello 布局变动（例如新增字段），它会在**没有任何编译错误**的情况下失效，
        /// 而当时的失效形式是“DS 回包被自己的反放大检查拒发”——很难定位。
        /// 因此客户端主动填充到这个余量，DS 侧还有一道 response &lt;= request 的兜底检查。
        /// </summary>
        private const int PaddingSafetyMarginBytes = 64;

        private readonly PMDsEntryOffer _offer;
        private readonly byte[] _ticket;
        private readonly byte[] _ticketDigest;
        private readonly int _expectedServerHelloBytes;
        private readonly int _paddingBytes;

        private byte[] _lastNonce;

        public PMHandshakeClient(PMDsEntryOffer offer)
        {
            if (offer == null) { throw new ArgumentNullException("offer"); }
            if (offer.Ticket == null || offer.Ticket.Length == 0)
            {
                throw new ArgumentException("offer 缺少票据", "offer");
            }

            if (offer.Ticket.Length > PMHandshakeCodec.MaxTicketBytes)
            {
                throw new ArgumentException("offer 票据长度超出上限", "offer");
            }

            if (offer.Epoch == 0u || offer.ProtocolHash == 0u)
            {
                throw new ArgumentException("offer 的 Epoch / ProtocolHash 不得为 0", "offer");
            }

            _offer = offer;
            _ticket = offer.Ticket;
            _ticketDigest = PMDsCrypto.Sha256(_ticket, 0, _ticket.Length);
            _expectedServerHelloBytes = PMHandshakeCodec.ComputeServerHelloBytes(
                offer.MatchId, offer.DsId, offer.Epoch, offer.ProtocolHash, offer.CollisionDigest,
                offer.Identity.Uid, offer.Identity.PlayerId, offer.Identity.TeamId, offer.Identity.HeroId);
            _paddingBytes = ComputePadding(_expectedServerHelloBytes);
        }

        /// <summary>本端票据的全量 SHA-256（用于与 ServerHello 回带的摘要比对）。</summary>
        public byte[] TicketDigest { get { return _ticketDigest; } }

        /// <summary>最近一次 <see cref="BuildClientHello"/> 使用的 nonce。</summary>
        public byte[] LastNonce { get { return _lastNonce; } }

        /// <summary>已生成过的 ClientHello 次数（调用方据此限速/超时）。</summary>
        public int HellosSent { get; private set; }

        /// <summary>与 offer 不符的响应次数（错 nonce/摘要/对局/世代/身份）。</summary>
        public int IdentityMismatches { get; private set; }

        /// <summary>收到对端明确拒绝（ServerReject）的次数。</summary>
        public int RejectedByServer { get; private set; }

        /// <summary>收到的帧非法次数。</summary>
        public int MalformedResponses { get; private set; }

        /// <summary>预期 ServerHello 字节数（诊断；填充就是按它算的）。</summary>
        public int ExpectedServerHelloBytes { get { return _expectedServerHelloBytes; } }

        /// <summary>本端 ClientHello 的固定填充字节数（保证「应答不大于请求」）。</summary>
        public int PaddingBytes { get { return _paddingBytes; } }

        /// <summary>生成一条 ClientHello（**每次都是新 nonce**；重试限速由调用方负责）。</summary>
        public byte[] BuildClientHello()
        {
            byte[] nonce = PMDsCrypto.RandomBytes(PMHandshakeCodec.NonceBytes);
            _lastNonce = nonce;
            HellosSent++;

            PMNetWriter writer = new PMNetWriter(512);
            PMHandshakeCodec.WriteClientHello(writer, _ticket, 0, _ticket.Length, nonce,
                                              _offer.ProtocolHash, _offer.Epoch, _paddingBytes);
            byte[] frame = writer.ToArray();

            if (frame.Length > PMHandshakeCodec.MaxFrameBytes)
            {
                // 显式失败：宁可不发，也不发一条对端必然拒收的帧。
                throw new InvalidOperationException(
                    "ClientHello " + frame.Length + " 字节超过握手帧上限 " + PMHandshakeCodec.MaxFrameBytes);
            }

            return frame;
        }

        /// <summary>
        /// 处理一条来自预期 DS 端点的入站帧。
        ///
        /// <paramref name="error"/> 只用于诊断，**不得包含票据字节**。
        /// 返回 <see cref="PMHandshakeClientOutcome.Accepted"/> 之外的任何结果都**不得**据以激活。
        /// </summary>
        public PMHandshakeClientOutcome Handle(byte[] data, int offset, int count,
                                               out PMSessionBinding binding, out string error)
        {
            binding = default(PMSessionBinding);
            error = null;

            PMHandshakeKind kind;
            if (!PMHandshakeCodec.IsHandshakeDatagram(data, offset, count, out kind))
            {
                MalformedResponses++;
                error = "不是握手帧（magic/version/kind 不符）";
                return PMHandshakeClientOutcome.Malformed;
            }

            if (kind == PMHandshakeKind.ServerReject)
            {
                PMDsTicketVerdict verdict;
                byte stage;
                uint retryAfterMs;
                if (!PMHandshakeCodec.TryReadServerReject(data, offset, count, out verdict, out stage, out retryAfterMs, out error))
                {
                    MalformedResponses++;
                    return PMHandshakeClientOutcome.Malformed;
                }

                RejectedByServer++;
                error = "DS 明确拒绝（verdict=" + verdict + " stage=" + stage + " retryAfter=" + retryAfterMs + "ms）";
                return PMHandshakeClientOutcome.Rejected;
            }

            if (kind != PMHandshakeKind.ServerHello)
            {
                MalformedResponses++;
                error = "对端发来了 " + kind + "（客户端只接受 ServerHello）";
                return PMHandshakeClientOutcome.Malformed;
            }

            PMHandshakeServerHello hello;
            if (!PMHandshakeCodec.TryReadServerHello(data, offset, count, out hello, out error))
            {
                MalformedResponses++;
                return PMHandshakeClientOutcome.Malformed;
            }

            if (hello.ClientNonce == null || _lastNonce == null
                || !FixedTimeEquals(hello.ClientNonce, 0, _lastNonce, 0, PMHandshakeCodec.NonceBytes))
            {
                return Mismatch(ref binding, out error, "ServerHello 回带的 nonce 与本端发出的不符（旧响应重放？）");
            }

            if (hello.TicketDigest == null
                || !FixedTimeEquals(hello.TicketDigest, 0, _ticketDigest, 0, PMHandshakeCodec.DigestBytes))
            {
                return Mismatch(ref binding, out error, "ServerHello 回带的票据摘要与本端所持票据不符");
            }

            if (!string.Equals(hello.MatchId, _offer.MatchId, StringComparison.Ordinal))
            {
                return Mismatch(ref binding, out error, "ServerHello 的 MatchId 与 Lobby 下发的 offer 不符");
            }

            if (!string.Equals(hello.DsId, _offer.DsId, StringComparison.Ordinal))
            {
                return Mismatch(ref binding, out error, "ServerHello 的 DsId 与 Lobby 下发的 offer 不符");
            }

            if (hello.Epoch != _offer.Epoch)
            {
                return Mismatch(ref binding, out error, "ServerHello 的 Epoch 与 offer 不符");
            }

            if (hello.ProtocolHash != _offer.ProtocolHash)
            {
                return Mismatch(ref binding, out error, "ServerHello 的 ProtocolHash 与 offer 不符");
            }

            if (_offer.CollisionDigest != 0u && hello.CollisionDigest != _offer.CollisionDigest)
            {
                return Mismatch(ref binding, out error, "ServerHello 的 CollisionDigest 与 offer 不符");
            }

            if (hello.Uid != _offer.Identity.Uid || hello.PlayerId != _offer.Identity.PlayerId
                || hello.TeamId != _offer.Identity.TeamId || hello.HeroId != _offer.Identity.HeroId)
            {
                return Mismatch(ref binding, out error, "ServerHello 声明的身份与 offer 名册身份不符");
            }

            binding.ConnectionId = 0;                 // 客户端侧连接序号由宿主分配（本端只有一条服务器连接）
            binding.Uid = hello.Uid;
            binding.PlayerId = hello.PlayerId;
            binding.TeamId = hello.TeamId;
            binding.HeroId = hello.HeroId;
            binding.Epoch = hello.Epoch;
            binding.ProtocolHash = hello.ProtocolHash;
            binding.MatchId = hello.MatchId;
            binding.DsId = hello.DsId;
            binding.EndpointKey = null;
            binding.TicketDigest = _ticketDigest;
            return PMHandshakeClientOutcome.Accepted;
        }

        private PMHandshakeClientOutcome Mismatch(ref PMSessionBinding binding, out string error, string reason)
        {
            IdentityMismatches++;
            error = reason + "（第 " + IdentityMismatches + " 次）";

            if (IdentityMismatches >= MaxIdentityMismatches)
            {
                error = reason + "；已达 " + MaxIdentityMismatches + " 次上限，不再采信该端点为 DS";
                return PMHandshakeClientOutcome.Rejected;
            }

            return PMHandshakeClientOutcome.Ignored;
        }

        private int ComputePadding(int expectedServerHelloBytes)
        {
            int target = expectedServerHelloBytes + PaddingSafetyMarginBytes;
            if (target > PMHandshakeCodec.MaxFrameBytes)
            {
                // offer 本身大到无法保证「应答不大于请求」：直接失败，不做半吊子尝试。
                throw new ArgumentException(
                    "offer 过大：预期 ServerHello " + expectedServerHelloBytes
                    + " 字节，无法在 " + PMHandshakeCodec.MaxFrameBytes + " 字节内保证应答不大于请求", "offer");
            }

            // ClientHello 的固定部分（不含填充本身）。
            int baseSize = PMHandshakeCodec.HeaderBytes
                + PMHandshakeCodec.VarintSize(_ticket.Length) + _ticket.Length
                + PMHandshakeCodec.NonceBytes
                + PMHandshakeCodec.VarintSize(_offer.ProtocolHash)
                + PMHandshakeCodec.VarintSize(_offer.Epoch);

            int padding = target > baseSize ? target - baseSize : 0;

            // 填充长度自身的 varint 会占字节：收敛到「加上填充头之后仍 >= target」。
            while (baseSize + PMHandshakeCodec.VarintSize(padding) + padding < target
                   && padding < PMHandshakeCodec.MaxPaddingBytes)
            {
                padding++;
            }

            if (padding > PMHandshakeCodec.MaxPaddingBytes)
            {
                throw new ArgumentException("offer 过大：所需填充超过上限", "offer");
            }

            if (baseSize + PMHandshakeCodec.VarintSize(padding) + padding > PMHandshakeCodec.MaxFrameBytes)
            {
                throw new ArgumentException("offer 过大：ClientHello 会超过握手帧上限", "offer");
            }

            return padding;
        }

        private static bool FixedTimeEquals(byte[] a, int aOffset, byte[] b, int bOffset, int count)
        {
            return PMDsCrypto.FixedTimeEquals(a, aOffset, b, bOffset, count);
        }
    }

    // =====================================================================================
    //  DS 侧握手处理（socket 由宿主给，本类只做「帧 → 判定 → 响应字节」）
    // =====================================================================================

    /// <summary>DS 侧对一条入站帧的处理结果。</summary>
    public struct PMHandshakeServerResult
    {
        /// <summary>该帧是否看起来是握手帧。</summary>
        public bool IsHandshake;

        /// <summary>是否通过（**只有 true 才允许回包**）。</summary>
        public bool Accepted;

        /// <summary>帧类型。</summary>
        public PMHandshakeKind Kind;

        /// <summary>处理到哪一步（拒绝时用于归因）。</summary>
        public PMHandshakeStage Stage;

        /// <summary>验票结论（Stage=Roster 时有意义）。</summary>
        public PMDsTicketVerdict Verdict;

        /// <summary>账本结论（Stage=Ledger 时有意义）。</summary>
        public PMDsLedgerReject Reject;

        /// <summary>成功时的会话绑定。</summary>
        public PMSessionBinding Binding;

        /// <summary>成功时的回包字节（**失败时恒为 null**：契约 §7.2 的「失败无回包」）。</summary>
        public byte[] Response;

        /// <summary>诊断串。</summary>
        public override string ToString()
        {
            return "hsServer(handshake=" + IsHandshake + " kind=" + Kind + " accepted=" + Accepted
                + " stage=" + Stage + " verdict=" + Verdict + " reject=" + Reject
                + " response=" + (Response == null ? 0 : Response.Length) + "B)";
        }
    }

    /// <summary>
    /// DS 侧握手处理器。
    ///
    /// 逐条落实契约 §7.2：
    ///   - **从 boot 名册逐身份验票**，不只验证 MAC：对名册里每个身份调一次
    ///     <see cref="PMDsTicketVerifier.Verify(byte[],int,int,long,PMDsRosterIdentity)"/>，
    ///     只有身份**完全匹配**名册某一项才算通过（所有权只来自名册，不从包内自报 uid 采纳）。
    ///   - 先做**廉价预检**（客户端声明的摘要/世代），再花 MAC 的钱。
    ///   - 账本消费在验票之后、任何状态变更之前。
    ///   - 失败一律**不产生响应字节**（调用方据此不回包）。
    ///   - 未认证端点状态有界：失败端点表 ≤ <see cref="MaxTrackedFailedEndpoints"/>，
    ///     单端点失败达 <see cref="MaxFailuresPerEndpoint"/> 后进入 <see cref="FailureCooldownSeconds"/> 冷却。
    /// </summary>
    public sealed class PMHandshakeServer
    {
        /// <summary>跟踪的失败端点上限（有界；超出淘汰最旧的）。</summary>
        public const int MaxTrackedFailedEndpoints = 64;

        /// <summary>单端点失败次数上限（达到后进入冷却）。</summary>
        public const int MaxFailuresPerEndpoint = 8;

        /// <summary>失败端点冷却窗口（秒）。</summary>
        public const long FailureCooldownSeconds = 30L;

        /// <summary>serverNonce 保留上限（有界；保留它只为「幂等重试返回同一 ServerHello」）。</summary>
        public const int MaxKeptServerNonces = 96;

        private sealed class FailureRecord
        {
            public int Count;
            public long WindowStartUnixSeconds;
            public long Sequence;
        }

        private sealed class NonceRecord
        {
            public byte[] Nonce;
            public long Sequence;
        }

        private readonly PMDsTicketVerifier _verifier;
        private readonly PMDsEndpointLedger _ledger;
        private readonly PMDsBootstrapPlayer[] _roster;
        private readonly string _matchId;
        private readonly string _dsId;
        private readonly uint _epoch;
        private readonly uint _protocolHash;
        private readonly uint _collisionDigest;

        private readonly Dictionary<string, FailureRecord> _failures =
            new Dictionary<string, FailureRecord>(StringComparer.Ordinal);

        private readonly Dictionary<PMTicketDigestKey, NonceRecord> _serverNonces =
            new Dictionary<PMTicketDigestKey, NonceRecord>();

        private long _sequence;

        public PMHandshakeServer(PMDsBootstrappedMatch boot, PMDsEndpointLedger ledger)
        {
            if (boot == null) { throw new ArgumentNullException("boot"); }
            if (boot.Key == null) { throw new ArgumentException("boot 缺少控制密钥", "boot"); }
            if (boot.Bootstrap == null) { throw new ArgumentException("boot 缺少 Bootstrap", "boot"); }
            if (ledger == null) { throw new ArgumentNullException("ledger"); }

            PMDsBootstrapBody body = boot.Bootstrap.AsBootstrap;
            if (body == null || body.Players == null || body.Players.Length == 0)
            {
                throw new ArgumentException("boot 名册为空（逐身份验票无从下手）", "boot");
            }

            PMDsTicketExpectation expectation = new PMDsTicketExpectation(
                boot.MatchId, boot.DsId, boot.Epoch, boot.ProtocolHash);
            if (!expectation.IsUsable)
            {
                throw new ArgumentException("boot 的对局身份不完整：" + expectation, "boot");
            }

            _verifier = boot.Key.CreateTicketVerifier(expectation);
            _ledger = ledger;
            _roster = body.Players;
            _matchId = boot.MatchId;
            _dsId = boot.DsId;
            _epoch = boot.Epoch;
            _protocolHash = boot.ProtocolHash;
            _collisionDigest = boot.CollisionDigest;
        }

        /// <summary>名册人数。</summary>
        public int RosterCount { get { return _roster.Length; } }

        /// <summary>通过并回包次数。</summary>
        public long Accepted { get; private set; }

        /// <summary>帧结构非法/类型不符次数。</summary>
        public long FrameRejections { get; private set; }

        /// <summary>廉价预检（摘要/世代）拒绝次数。</summary>
        public long CheapRejections { get; private set; }

        /// <summary>逐身份验票失败次数。</summary>
        public long VerifyFailures { get; private set; }

        /// <summary>账本拒绝次数。</summary>
        public long LedgerRejections { get; private set; }

        /// <summary>因端点失败达上限而被冷却丢弃的次数（**未做任何验票**）。</summary>
        public long RateLimited { get; private set; }

        /// <summary>因「回包会大于请求」而拒发次数（反放大兜底）。</summary>
        public long AmplificationRefusals { get; private set; }

        /// <summary>幂等重试（同票同端点，未新建绑定）次数。</summary>
        public long IdempotentRetries { get { return _ledger.Stats.IdempotentRetries; } }

        /// <summary>账本（宿主在连接断开时用它释放端点）。</summary>
        public PMDsEndpointLedger Ledger { get { return _ledger; } }

        /// <summary>
        /// 处理一条入站帧。
        ///
        /// 返回值 = 「是否通过」（等价于 <c>result.Accepted</c>）；调用方一律只在通过时才发送
        /// <c>result.Response</c>。
        /// </summary>
        public bool TryHandle(byte[] data, int offset, int count, string endpointKey, long nowUnixSeconds,
                              out PMHandshakeServerResult result)
        {
            result = default(PMHandshakeServerResult);

            PMHandshakeKind kind;
            if (!PMHandshakeCodec.IsHandshakeDatagram(data, offset, count, out kind))
            {
                result.Stage = PMHandshakeStage.None;
                return false;
            }

            result.IsHandshake = true;
            result.Kind = kind;

            if (kind != PMHandshakeKind.ClientHello)
            {
                // DS 只处理 ClientHello：对端发 ServerHello/ServerReject 只可能是接线错或伪造。
                result.Stage = PMHandshakeStage.Frame;
                FrameRejections++;
                return false;
            }

            if (IsRateLimited(endpointKey, nowUnixSeconds))
            {
                result.Stage = PMHandshakeStage.Frame;
                RateLimited++;
                return false;
            }

            PMHandshakeClientHello hello;
            string error;
            if (!PMHandshakeCodec.TryReadClientHello(data, offset, count, out hello, out error))
            {
                result.Stage = PMHandshakeStage.Frame;
                FrameRejections++;
                RecordFailure(endpointKey, nowUnixSeconds);
                return false;
            }

            if (hello.ProtocolHash != _protocolHash || hello.Epoch != _epoch)
            {
                // 廉价预检：**在花 MAC 的钱之前**拒掉摘要/世代不符的包。
                result.Stage = PMHandshakeStage.Hash;
                CheapRejections++;
                RecordFailure(endpointKey, nowUnixSeconds);
                return false;
            }

            // 逐身份验票：只有名册里某个身份的票据完全匹配（含 MAC、对局、世代、摘要、DS、身份、有效期）。
            PMDsTicketVerification verification = default(PMDsTicketVerification);
            verification.Verdict = PMDsTicketVerdict.NotInRoster;
            bool verified = false;

            for (int i = 0; i < _roster.Length; i++)
            {
                PMDsRosterIdentity identity = _roster[i].Identity;
                PMDsTicketVerification candidate = _verifier.Verify(
                    hello.Ticket, hello.TicketOffset, hello.TicketCount, nowUnixSeconds, identity);

                if (candidate.Verdict == PMDsTicketVerdict.Valid)
                {
                    verification = candidate;
                    verified = true;
                    break;
                }

                // 保留「更具体」的结论（NotInRoster 是「这张票不是这名玩家的」这种最弱的信息）。
                if (verification.Verdict == PMDsTicketVerdict.NotInRoster
                    && candidate.Verdict != PMDsTicketVerdict.NotInRoster)
                {
                    verification = candidate;
                }
            }

            result.Verdict = verification.Verdict;

            if (!verified)
            {
                result.Stage = PMHandshakeStage.Roster;
                VerifyFailures++;
                RecordFailure(endpointKey, nowUnixSeconds);
                return false;
            }

            PMSessionBinding binding;
            PMDsLedgerReject reject;
            if (!_ledger.TryConsume(hello.Ticket, hello.TicketOffset, hello.TicketCount, verification,
                                    endpointKey, nowUnixSeconds, out binding, out reject))
            {
                result.Stage = PMHandshakeStage.Ledger;
                result.Reject = reject;
                LedgerRejections++;
                return false;
            }

            result.Binding = binding;
            result.Stage = PMHandshakeStage.Accepted;

            byte[] response = BuildServerHello(hello, binding);
            if (response.Length > count)
            {
                // 反放大兜底：回调不出去就撤回消费（客户端会重试，不写墓碑）。
                _ledger.RenounceConsume(binding.TicketDigest, endpointKey);
                result.Accepted = false;
                result.Stage = PMHandshakeStage.Frame;
                result.Response = null;
                AmplificationRefusals++;
                return false;
            }

            result.Response = response;
            result.Accepted = true;
            Accepted++;

            // 成功会重置该端点的失败计数（避免「先失败几次、成功一次后被冷却」的错杀）。
            _failures.Remove(endpointKey);
            return true;
        }

        private byte[] BuildServerHello(PMHandshakeClientHello hello, PMSessionBinding binding)
        {
            PMHandshakeServerHello outHello = new PMHandshakeServerHello();
            outHello.MatchId = _matchId;
            outHello.DsId = _dsId;
            outHello.Epoch = binding.Epoch;
            outHello.ProtocolHash = _protocolHash;
            outHello.CollisionDigest = _collisionDigest;
            outHello.TicketDigest = binding.TicketDigest;
            outHello.ClientNonce = hello.ClientNonce;
            outHello.ServerNonce = GetOrCreateServerNonce(binding.TicketDigest);
            outHello.Uid = binding.Uid;
            outHello.PlayerId = binding.PlayerId;
            outHello.TeamId = binding.TeamId;
            outHello.HeroId = binding.HeroId;

            PMNetWriter writer = new PMNetWriter(512);
            PMHandshakeCodec.WriteServerHello(writer, outHello);
            return writer.ToArray();
        }

        /// <summary>
        /// 取（或首次生成）该票据绑定的 serverNonce。
        ///
        /// 为什么要保留：契约对重试的语义是「幂等重试返回同一 ServerHello 内容（nonce 回带每次都新）」。
        /// 保留表有上限（<see cref="MaxKeptServerNonces"/>），超出按最旧淘汰——丢掉一个 nonce
        /// 只影响该字段的连续性，不影响任何判定（该字段首版未被使用）。
        /// </summary>
        private byte[] GetOrCreateServerNonce(byte[] digest)
        {
            PMTicketDigestKey key = new PMTicketDigestKey(digest);

            NonceRecord record;
            if (_serverNonces.TryGetValue(key, out record))
            {
                return record.Nonce;
            }

            byte[] nonce = PMDsCrypto.RandomBytes(PMHandshakeCodec.NonceBytes);
            NonceRecord created = new NonceRecord();
            created.Nonce = nonce;
            created.Sequence = ++_sequence;
            _serverNonces[key] = created;

            EnforceNonceCapacity();
            return nonce;
        }

        private void EnforceNonceCapacity()
        {
            while (_serverNonces.Count > MaxKeptServerNonces)
            {
                PMTicketDigestKey oldestKey = default(PMTicketDigestKey);
                NonceRecord oldest = null;
                bool found = false;

                foreach (KeyValuePair<PMTicketDigestKey, NonceRecord> pair in _serverNonces)
                {
                    if (!found || pair.Value.Sequence < oldest.Sequence)
                    {
                        oldest = pair.Value;
                        oldestKey = pair.Key;
                        found = true;
                    }
                }

                if (!found)
                {
                    return;
                }

                _serverNonces.Remove(oldestKey);
            }
        }

        private bool IsRateLimited(string endpointKey, long nowUnixSeconds)
        {
            if (endpointKey == null)
            {
                return true;
            }

            FailureRecord record;
            if (!_failures.TryGetValue(endpointKey, out record))
            {
                return false;
            }

            if (nowUnixSeconds - record.WindowStartUnixSeconds >= FailureCooldownSeconds)
            {
                _failures.Remove(endpointKey);
                return false;
            }

            return record.Count >= MaxFailuresPerEndpoint;
        }

        private void RecordFailure(string endpointKey, long nowUnixSeconds)
        {
            if (endpointKey == null)
            {
                return;
            }

            FailureRecord record;
            if (_failures.TryGetValue(endpointKey, out record)
                && nowUnixSeconds - record.WindowStartUnixSeconds < FailureCooldownSeconds)
            {
                record.Count++;
                return;
            }

            FailureRecord created = new FailureRecord();
            created.Count = 1;
            created.WindowStartUnixSeconds = nowUnixSeconds;
            created.Sequence = ++_sequence;
            _failures[endpointKey] = created;

            EnforceFailureCapacity();
        }

        private void EnforceFailureCapacity()
        {
            while (_failures.Count > MaxTrackedFailedEndpoints)
            {
                string oldestKey = null;
                FailureRecord oldest = null;
                bool found = false;

                foreach (KeyValuePair<string, FailureRecord> pair in _failures)
                {
                    if (!found || pair.Value.Sequence < oldest.Sequence)
                    {
                        oldest = pair.Value;
                        oldestKey = pair.Key;
                        found = true;
                    }
                }

                if (!found)
                {
                    return;
                }

                _failures.Remove(oldestKey);
            }
        }
    }
}
