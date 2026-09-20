using System;
using System.Collections.Generic;
using System.Text;

namespace PMNet.Control
{
    /// <summary>
    /// R3-A1：Lobby ↔ DedicatedServer 的**手写有界控制协议**（M12）。
    ///
    /// 事实来源：`Docs/plans/net-r3-control-contract.md` §3（控制消息与状态）。
    ///
    /// 为什么单独一套 codec 而不用现有业务协议：
    /// 1. 控制面只有 8 种消息、字段全为 primitive，用 PMNetReader/Writer 手写比生成器更直接，
    ///    也避免把「进程编排」塞进 `SocketProto.MainPack` 那套已冻结的旧业务契约里。
    /// 2. 控制帧是**安全边界**：必须先验 MAC 再动状态。这里把「解码 + 验 MAC」做成
    ///    一个不可绕过的入口（<see cref="PMDsControlSigner.VerifyRaw"/>），
    ///    让调用方没有机会先解析业务字段再补验签。
    ///
    /// 严格性（全部是**明确失败**，没有「宽松兼容」分支）：
    /// - 载荷长度 &gt; 64KiB → 立即拒绝（<see cref="PMDsControlFrameDecoder"/> 会在读到长度前缀时抛）。
    /// - 字段号必须**严格递增**（唯一例外：Bootstrap 的信封 body 里名册字段可连续重复）。
    /// - 未知字段号 / 未知消息类型 / 未知格式版本 / 字段重复 / 缺失必填字段 / MAC 之后还有尾部字节 → 拒绝。
    /// - 字符串/字节字段按 UTF-8 **字节数**有界校验（不是字符数），且在**分配前**用长度前缀预检。
    ///
    /// 这层**不做**的事（有意留给 R3-B）：
    /// - 不做防重放：MAC 只证明「这条消息由持有本局密钥的一方产生」，
    ///   不代表「这条消息没被用过」。端点消费账本（哪条票据/哪次 Bootstrap 已被哪个真实端点消费）
    ///   由 R3-B 的连接接受器负责。**不得把 MAC 验证当成防重放已完成。**
    /// - 不做传输安全：首版 TCP 仅 loopback（契约 §3），密钥经由同机引导文件交付，不走网络。
    /// </summary>
    public static class PMDsControlWire
    {
        /// <summary>载荷格式版本。不匹配即拒绝（没有「向后兼容解析」）。</summary>
        public const int FormatVersion = 1;

        /// <summary>分帧长度前缀的字节数（小端 uint32）。</summary>
        public const int LengthPrefixBytes = 4;

        /// <summary>单帧载荷上限：64KiB（契约 §3）。长度前缀本身不计入。</summary>
        public const int MaxFramePayloadBytes = 64 * 1024;

        /// <summary>控制帧 MAC 长度（HMAC-SHA256 输出）。</summary>
        public const int MacBytes = 32;

        /// <summary>每局控制密钥长度：256 位（契约 §2）。</summary>
        public const int ControlKeyBytes = 32;

        /// <summary>MatchId 的 UTF-8 字节上限。</summary>
        public const int MaxMatchIdBytes = 128;

        /// <summary>DsId 的 UTF-8 字节上限。</summary>
        public const int MaxDsIdBytes = 128;

        /// <summary>名册人数上限（契约 §2：最多 6 人且 Uid/PlayerId 唯一）。</summary>
        public const int MaxRosterPlayers = 6;

        /// <summary>单张玩家票据的字节上限（真实长度 ≤ 348，见 PMDsTicket）。</summary>
        public const int MaxTicketBytes = 512;

        /// <summary>结算摘要字节上限。</summary>
        public const int MaxSummaryBytes = 512;

        /// <summary>错误文本 UTF-8 字节上限。</summary>
        public const int MaxErrorMessageBytes = 512;

        /// <summary>MAC 字段号：刻意放在高位，保证它是「最后一个字段」且不与业务字段挤在一起。</summary>
        public const int FieldMac = 250;

        /// <summary>控制通道默认监听端口（R3-B 接线用；A1 只做协议与状态机）。</summary>
        public const int DefaultControlPort = 7800;

        /// <summary>把 payload 长度格式化为带上下文的描述（诊断用）。</summary>
        public static string DescribePayloadLength(int length)
        {
            return length.ToString() + " 字节（上限 " + MaxFramePayloadBytes.ToString() + "）";
        }
    }

    /// <summary>控制消息类型（契约 §3 的消息清单）。</summary>
    public enum PMDsControlMessageType : byte
    {
        /// <summary>占位：0 永远非法（缺失字段会被当成 0，必须能被识别出来）。</summary>
        Invalid = 0,

        /// <summary>Lobby → DS：名册 + 票据。**A1 决定：生产路径走同机引导文件，不走控制通道**（见报告）。</summary>
        Bootstrap = 1,

        /// <summary>DS → Lobby：实际 boundPort + sceneReady + 碰撞配置摘要。</summary>
        Ready = 2,

        /// <summary>DS → Lobby：存活心跳。</summary>
        Heartbeat = 3,

        /// <summary>DS → Lobby：结算结果（ResultId + winnerTeamId + 摘要）。</summary>
        Result = 4,

        /// <summary>Lobby → DS：结果确认（同 ResultId）。</summary>
        ResultAck = 5,

        /// <summary>Lobby → DS：优雅关闭请求。</summary>
        Shutdown = 6,

        /// <summary>DS → Lobby：进程即将/已经退出（退出码）。</summary>
        Exited = 7,

        /// <summary>双向：协议级错误上报。Lobby 侧收到即视为该会话失败。</summary>
        Error = 8,
    }

    /// <summary>控制协议错误。**消息里不得包含密钥/MAC/票据原始字节**。</summary>
    public sealed class PMDsControlProtocolException : Exception
    {
        public PMDsControlProtocolException(string message)
            : base(message)
        {
        }

        public PMDsControlProtocolException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    /// <summary>分帧解码器的故障分类。一旦故障，解码器不可继续使用（必须重建连接）。</summary>
    public enum PMDsControlFrameFault : byte
    {
        None = 0,

        /// <summary>长度前缀声明的载荷超过 64KiB。</summary>
        OversizeLength = 1,

        /// <summary>长度前缀为 0（空帧没有合法语义）。</summary>
        ZeroLength = 2,
    }

    /// <summary>「解码 + 验签」这一步的失败分类，供状态机计数与诊断。</summary>
    public enum PMDsControlVerifyFault : byte
    {
        None = 0,

        /// <summary>载荷本身非法（字段号/版本/类型/尾部/长度越界等）。</summary>
        MalformedPayload = 1,

        /// <summary>载荷合法但没有 MAC 字段（未签名的控制帧一律拒绝）。</summary>
        MissingMac = 2,

        /// <summary>MAC 常量时间比对失败（篡改、错密钥、错局）。</summary>
        MacMismatch = 3,
    }

    /// <summary>
    /// 名册身份。**所有权只能来自已认证票据对应名册**，不从业务包自报 uid 采纳（契约 §2）。
    /// 因此这个结构体的实例只有两个合法来源：Lobby 分配的名册、或票据验签成功后的解析结果。
    /// </summary>
    public struct PMDsRosterIdentity : IEquatable<PMDsRosterIdentity>
    {
        /// <summary>账号 uid（&gt; 0）。</summary>
        public int Uid;

        /// <summary>局内玩家编号（&gt; 0）。</summary>
        public int PlayerId;

        /// <summary>队伍编号（≥ 0）。</summary>
        public int TeamId;

        /// <summary>英雄编号（≥ 0）。</summary>
        public int HeroId;

        public PMDsRosterIdentity(int uid, int playerId, int teamId, int heroId)
        {
            Uid = uid;
            PlayerId = playerId;
            TeamId = teamId;
            HeroId = heroId;
        }

        public bool Equals(PMDsRosterIdentity other)
        {
            return Uid == other.Uid && PlayerId == other.PlayerId
                && TeamId == other.TeamId && HeroId == other.HeroId;
        }

        public override bool Equals(object obj)
        {
            return obj is PMDsRosterIdentity && Equals((PMDsRosterIdentity)obj);
        }

        public override int GetHashCode()
        {
            int hash = Uid;
            hash = (hash * 397) ^ PlayerId;
            hash = (hash * 397) ^ TeamId;
            hash = (hash * 397) ^ HeroId;
            return hash;
        }

        public static bool operator ==(PMDsRosterIdentity a, PMDsRosterIdentity b) { return a.Equals(b); }
        public static bool operator !=(PMDsRosterIdentity a, PMDsRosterIdentity b) { return !a.Equals(b); }

        /// <summary>只打印身份字段（无秘密）。</summary>
        public override string ToString()
        {
            return "uid=" + Uid + " pid=" + PlayerId + " team=" + TeamId + " hero=" + HeroId;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Body DTO（每种消息一个 body；信封携带公共身份字段）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>控制消息 body 基类。body 的具体类型必须与信封的消息类型一致（否则拒绝）。</summary>
    public abstract class PMDsControlBody
    {
        /// <summary>本 body 对应的消息类型。</summary>
        public abstract PMDsControlMessageType MessageType { get; }
    }

    /// <summary>名册的一员 + 该玩家的票据（仅 Bootstrap 使用）。</summary>
    public sealed class PMDsBootstrapPlayer
    {
        public PMDsRosterIdentity Identity;

        /// <summary>该玩家的票据原始字节（含 MAC）。**属于秘密材料，不得进日志。**</summary>
        public byte[] Ticket = EmptyBytes;

        public PMDsBootstrapPlayer()
        {
        }

        public PMDsBootstrapPlayer(PMDsRosterIdentity identity, byte[] ticket)
        {
            Identity = identity;
            Ticket = ticket ?? EmptyBytes;
        }

        /// <summary>只打印身份与票据长度，不打印票据内容。</summary>
        public override string ToString()
        {
            return Identity.ToString() + " ticket=" + (Ticket == null ? 0 : Ticket.Length) + "B";
        }

        internal static readonly byte[] EmptyBytes = new byte[0];
    }

    /// <summary>Bootstrap：名册 + 票据 + 碰撞配置摘要（Lobby → DS）。</summary>
    public sealed class PMDsBootstrapBody : PMDsControlBody
    {
        public override PMDsControlMessageType MessageType { get { return PMDsControlMessageType.Bootstrap; } }

        /// <summary>Lobby 要求 DS 据此加载并回带的碰撞配置摘要（0 非法）。</summary>
        public uint CollisionDigest;

        /// <summary>名册（1..6 人）。</summary>
        public PMDsBootstrapPlayer[] Players = new PMDsBootstrapPlayer[0];
    }

    /// <summary>Ready：DS 就绪报告。**只有身份/摘要/启动代次全部匹配、端口与分配一致、sceneReady=true 才算就绪**。</summary>
    public sealed class PMDsReadyBody : PMDsControlBody
    {
        public override PMDsControlMessageType MessageType { get { return PMDsControlMessageType.Ready; } }

        /// <summary>进程**实际 bind 成功**的端口（必须等于 Lobby 分配的端口）。</summary>
        public int BoundPort;

        /// <summary>权威场景/碰撞环境是否就绪。≠ true 时 Lobby 不得发布地址。</summary>
        public bool SceneReady;

        /// <summary>DS 实际加载的碰撞配置摘要（必须等于 Bootstrap 里的值）。</summary>
        public uint CollisionDigest;
    }

    /// <summary>Heartbeat：存活心跳。</summary>
    public sealed class PMDsHeartbeatBody : PMDsControlBody
    {
        public override PMDsControlMessageType MessageType { get { return PMDsControlMessageType.Heartbeat; } }

        /// <summary>DS 自启动以来的毫秒数（诊断用）。</summary>
        public long UptimeMilliseconds;

        /// <summary>DS 当前认为已连接的玩家数（诊断用）。</summary>
        public int PlayerCount;
    }

    /// <summary>Result：权威玩法产出的结算结果。</summary>
    public sealed class PMDsResultBody : PMDsControlBody
    {
        public override PMDsControlMessageType MessageType { get { return PMDsControlMessageType.Result; } }

        /// <summary>业务幂等 ID（非 0）。幂等键是 (MatchId, Epoch, ResultId)。</summary>
        public ulong ResultId;

        /// <summary>胜方队伍；-1 表示平局。</summary>
        public int WinnerTeamId;

        /// <summary>结算摘要（可为空，≤ 512 字节）。</summary>
        public byte[] Summary = PMDsBootstrapPlayer.EmptyBytes;
    }

    /// <summary>ResultAck：结果确认（同 ResultId）。</summary>
    public sealed class PMDsResultAckBody : PMDsControlBody
    {
        public override PMDsControlMessageType MessageType { get { return PMDsControlMessageType.ResultAck; } }

        /// <summary>被确认的 ResultId（必须与 Result 中的一致）。</summary>
        public ulong ResultId;
    }

    /// <summary>Shutdown：优雅关闭请求。</summary>
    public sealed class PMDsShutdownBody : PMDsControlBody
    {
        public override PMDsControlMessageType MessageType { get { return PMDsControlMessageType.Shutdown; } }

        /// <summary>关闭原因码（由发起方定义，仅用于观测）。</summary>
        public uint ReasonCode;
    }

    /// <summary>Exited：进程退出报告。</summary>
    public sealed class PMDsExitedBody : PMDsControlBody
    {
        public override PMDsControlMessageType MessageType { get { return PMDsControlMessageType.Exited; } }

        /// <summary>进程退出码。</summary>
        public int ExitCode;
    }

    /// <summary>Error：协议级错误上报。</summary>
    public sealed class PMDsErrorBody : PMDsControlBody
    {
        public override PMDsControlMessageType MessageType { get { return PMDsControlMessageType.Error; } }

        /// <summary>错误码（由对端定义）。</summary>
        public uint Code;

        /// <summary>错误文本（≤ 512 字节，不得包含秘密）。</summary>
        public string Message = string.Empty;
    }

    /// <summary>控制消息（信封 + body）。</summary>
    public sealed class PMDsControlMessage
    {
        /// <summary>格式版本（解码后必须 = 1）。</summary>
        public int FormatVersion = PMDsControlWire.FormatVersion;

        /// <summary>消息类型。</summary>
        public PMDsControlMessageType Type;

        /// <summary>对局 ID。</summary>
        public string MatchId = string.Empty;

        /// <summary>DS ID。</summary>
        public string DsId = string.Empty;

        /// <summary>会话世代（非 0）。</summary>
        public uint Epoch;

        /// <summary>协议摘要（非 0）。</summary>
        public uint ProtocolHash;

        /// <summary>请求/响应关联 ID（0 表示不需要关联）。</summary>
        public ulong RequestId;

        /// <summary>业务字段。</summary>
        public PMDsControlBody Body;

        /// <summary>MAC（32 字节）；未签名时为长度 0 的数组。</summary>
        public byte[] Mac = PMDsBootstrapPlayer.EmptyBytes;

        /// <summary>签名/解码后的完整载荷副本（不含 4 字节长度前缀）。</summary>
        public byte[] RawPayload;

        /// <summary>RawPayload 中 MAC 字段（含标签）的起始偏移；-1 表示没有 MAC 字段。</summary>
        public int MacFieldOffset = -1;

        /// <summary>是否带 MAC 字段。</summary>
        public bool HasMac { get { return Mac != null && Mac.Length == PMDsControlWire.MacBytes && MacFieldOffset >= 0; } }

        /// <summary>Bootstrap body（类型不符时返回 null）。</summary>
        public PMDsBootstrapBody AsBootstrap { get { return Body as PMDsBootstrapBody; } }

        /// <summary>Ready body（类型不符时返回 null）。</summary>
        public PMDsReadyBody AsReady { get { return Body as PMDsReadyBody; } }

        /// <summary>Heartbeat body（类型不符时返回 null）。</summary>
        public PMDsHeartbeatBody AsHeartbeat { get { return Body as PMDsHeartbeatBody; } }

        /// <summary>Result body（类型不符时返回 null）。</summary>
        public PMDsResultBody AsResult { get { return Body as PMDsResultBody; } }

        /// <summary>ResultAck body（类型不符时返回 null）。</summary>
        public PMDsResultAckBody AsResultAck { get { return Body as PMDsResultAckBody; } }

        /// <summary>Shutdown body（类型不符时返回 null）。</summary>
        public PMDsShutdownBody AsShutdown { get { return Body as PMDsShutdownBody; } }

        /// <summary>Exited body（类型不符时返回 null）。</summary>
        public PMDsExitedBody AsExited { get { return Body as PMDsExitedBody; } }

        /// <summary>Error body（类型不符时返回 null）。</summary>
        public PMDsErrorBody AsError { get { return Body as PMDsErrorBody; } }

        /// <summary>构造一条消息（不做字段合法性校验，编码时才校验并抛）。</summary>
        public static PMDsControlMessage Create(
            PMDsControlMessageType type,
            string matchId,
            string dsId,
            uint epoch,
            uint protocolHash,
            ulong requestId,
            PMDsControlBody body)
        {
            if (body == null)
            {
                throw new ArgumentNullException("body");
            }

            if (body.MessageType != type)
            {
                throw new PMDsControlProtocolException(
                    "body 类型 " + body.MessageType + " 与信封类型 " + type + " 不一致");
            }

            PMDsControlMessage message = new PMDsControlMessage();
            message.Type = type;
            message.MatchId = matchId ?? string.Empty;
            message.DsId = dsId ?? string.Empty;
            message.Epoch = epoch;
            message.ProtocolHash = protocolHash;
            message.RequestId = requestId;
            message.Body = body;
            return message;
        }

        /// <summary>只打印信封字段与是否存在 MAC，**绝不打印 MAC / 票据字节**。</summary>
        public override string ToString()
        {
            return Type.ToString()
                + " match=" + MatchId
                + " ds=" + DsId
                + " epoch=" + Epoch
                + " hash=" + ProtocolHash
                + " req=" + RequestId
                + " mac=" + (HasMac ? "present" : "absent");
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 编解码
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 控制帧载荷编解码。
    ///
    /// 分两步：<see cref="EncodePrefix"/> 产出**不含 MAC** 的规范前缀；
    /// <see cref="PMDsControlSigner"/> 对这个前缀求 HMAC 并把 MAC 字段追加到最后。
    /// 验签时**不重新编码**，而是直接对「MAC 字段之前的原始字节」求 MAC —— 这样
    /// 「解码后再编码」的规范化差异不会成为验签绕过面。
    /// </summary>
    public static class PMDsControlCodec
    {
        // 信封字段号
        private const int EnvFormatVersion = 1;
        private const int EnvMessageType = 2;
        private const int EnvMatchId = 3;
        private const int EnvDsId = 4;
        private const int EnvEpoch = 5;
        private const int EnvProtocolHash = 6;
        private const int EnvRequestId = 7;
        private const int EnvBody = 8;

        // Bootstrap body 字段号
        private const int BootCollisionDigest = 1;
        private const int BootPlayer = 2;
        private const int BootPlayerUid = 1;
        private const int BootPlayerPlayerId = 2;
        private const int BootPlayerTeamId = 3;
        private const int BootPlayerHeroId = 4;
        private const int BootPlayerTicket = 5;

        // Ready body 字段号
        private const int ReadyBoundPort = 1;
        private const int ReadySceneReady = 2;
        private const int ReadyCollisionDigest = 3;

        // Heartbeat body 字段号
        private const int HeartbeatUptime = 1;
        private const int HeartbeatPlayerCount = 2;

        // Result body 字段号
        private const int ResultIdField = 1;
        private const int ResultWinnerTeam = 2;
        private const int ResultSummary = 3;

        // ResultAck body 字段号
        private const int AckResultIdField = 1;

        // Shutdown body 字段号
        private const int ShutdownReasonCode = 1;

        // Exited body 字段号
        private const int ExitedExitCode = 1;

        // Error body 字段号
        private const int ErrorCode = 1;
        private const int ErrorMessage = 2;

        /// <summary>
        /// 编码「不含 MAC」的规范前缀。写侧所有上限校验都在这里做（超限即抛，不静默截断）。
        /// </summary>
        public static byte[] EncodePrefix(PMDsControlMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException("message");
            }

            if (message.Body == null)
            {
                throw new PMDsControlProtocolException("控制消息缺少 body");
            }

            if (message.Body.MessageType != message.Type)
            {
                throw new PMDsControlProtocolException("body 与信封消息类型不一致");
            }

            RequireNonEmptyBounded(message.MatchId, PMDsControlWire.MaxMatchIdBytes, "MatchId");
            RequireNonEmptyBounded(message.DsId, PMDsControlWire.MaxDsIdBytes, "DsId");

            if (message.Epoch == 0u)
            {
                throw new PMDsControlProtocolException("Epoch 不得为 0");
            }

            if (message.ProtocolHash == 0u)
            {
                throw new PMDsControlProtocolException("ProtocolHash 不得为 0");
            }

            PMNetWriter writer = new PMNetWriter(256);
            writer.WriteTag(EnvFormatVersion, PMWireType.Varint);
            writer.WriteUInt32((uint)PMDsControlWire.FormatVersion);
            writer.WriteTag(EnvMessageType, PMWireType.Varint);
            writer.WriteUInt32((uint)message.Type);
            writer.WriteTag(EnvMatchId, PMWireType.LengthDelimited);
            writer.WriteStringValue(message.MatchId);
            writer.WriteTag(EnvDsId, PMWireType.LengthDelimited);
            writer.WriteStringValue(message.DsId);
            writer.WriteTag(EnvEpoch, PMWireType.Varint);
            writer.WriteUInt32(message.Epoch);
            writer.WriteTag(EnvProtocolHash, PMWireType.Varint);
            writer.WriteUInt32(message.ProtocolHash);
            writer.WriteTag(EnvRequestId, PMWireType.Varint);
            writer.WriteUInt64(message.RequestId);

            PMNetWriter bodyWriter = writer.RentSubWriter();
            EncodeBody(bodyWriter, message.Body);
            writer.WriteSubMessage(EnvBody, bodyWriter);

            return writer.ToArray();
        }

        /// <summary>把 MAC 字段追加到前缀之后，产出完整载荷。</summary>
        public static byte[] AppendMac(byte[] prefix, byte[] mac)
        {
            if (prefix == null)
            {
                throw new ArgumentNullException("prefix");
            }

            if (mac == null || mac.Length != PMDsControlWire.MacBytes)
            {
                throw new PMDsControlProtocolException("MAC 长度必须为 " + PMDsControlWire.MacBytes);
            }

            PMNetWriter writer = new PMNetWriter(prefix.Length + PMDsControlWire.MacBytes + 4);
            writer.WriteRawBytes(prefix, 0, prefix.Length);
            writer.WriteTag(PMDsControlWire.FieldMac, PMWireType.LengthDelimited);
            writer.WriteBytesValue(mac);
            return writer.ToArray();
        }

        /// <summary>MAC 字段（含标签与长度前缀）的字节偏移。</summary>
        public static int ComputeMacFieldOffset(byte[] prefix)
        {
            if (prefix == null)
            {
                throw new ArgumentNullException("prefix");
            }

            return prefix.Length;
        }

        /// <summary>
        /// 严格解码。MAC 可以缺席（<see cref="PMDsControlSigner"/> 会另行拒绝未签名帧），
        /// 其余任何结构问题都抛 <see cref="PMDsControlProtocolException"/>。
        /// </summary>
        public static PMDsControlMessage Decode(byte[] payload, int offset, int count)
        {
            if (payload == null)
            {
                throw new ArgumentNullException("payload");
            }

            if (offset < 0 || count < 0 || offset + count > payload.Length)
            {
                throw new ArgumentOutOfRangeException("count", "解码范围越界");
            }

            if (count == 0)
            {
                throw new PMDsControlProtocolException("控制帧载荷为空");
            }

            if (count > PMDsControlWire.MaxFramePayloadBytes)
            {
                throw new PMDsControlProtocolException(
                    "控制帧载荷超限：" + PMDsControlWire.DescribePayloadLength(count));
            }

            try
            {
                return DecodeCore(payload, offset, count);
            }
            catch (FormatException ex)
            {
                throw new PMDsControlProtocolException("控制帧载荷格式非法：" + ex.Message, ex);
            }
        }

        private static PMDsControlMessage DecodeCore(byte[] payload, int offset, int count)
        {
            PMNetReader reader = new PMNetReader(payload, offset, count);
            PMDsControlMessage message = new PMDsControlMessage();
            message.RawPayload = new byte[count];
            Buffer.BlockCopy(payload, offset, message.RawPayload, 0, count);

            int seenMask = 0;
            int lastField = 0;
            PMNetReader bodyReader = null;

            while (!reader.IsAtEnd)
            {
                int tagStart = reader.Position;
                int field;
                PMWireType wireType;
                if (!reader.ReadTag(out field, out wireType))
                {
                    break;
                }

                if (field < lastField)
                {
                    throw new PMDsControlProtocolException("信封字段号必须严格递增，收到乱序字段号 " + field);
                }

                if (field == lastField)
                {
                    throw new PMDsControlProtocolException("信封字段号重复：" + field);
                }

                lastField = field;

                switch (field)
                {
                    case EnvFormatVersion:
                        RequireWire(wireType, PMWireType.Varint, field, "信封");
                        uint rawVersion = reader.ReadUInt32();
                        if (rawVersion > int.MaxValue)
                        {
                            throw new PMDsControlProtocolException("控制帧格式版本非法的巨型取值：" + rawVersion);
                        }

                        message.FormatVersion = (int)rawVersion;
                        seenMask |= 1 << EnvFormatVersion;
                        break;
                    case EnvMessageType:
                        RequireWire(wireType, PMWireType.Varint, field, "信封");
                        uint rawType = reader.ReadUInt32();
                        message.Type = (PMDsControlMessageType)rawType;
                        seenMask |= 1 << EnvMessageType;
                        break;
                    case EnvMatchId:
                        RequireWire(wireType, PMWireType.LengthDelimited, field, "信封");
                        message.MatchId = ReadBoundedString(reader, PMDsControlWire.MaxMatchIdBytes, "MatchId");
                        seenMask |= 1 << EnvMatchId;
                        break;
                    case EnvDsId:
                        RequireWire(wireType, PMWireType.LengthDelimited, field, "信封");
                        message.DsId = ReadBoundedString(reader, PMDsControlWire.MaxDsIdBytes, "DsId");
                        seenMask |= 1 << EnvDsId;
                        break;
                    case EnvEpoch:
                        RequireWire(wireType, PMWireType.Varint, field, "信封");
                        message.Epoch = reader.ReadUInt32();
                        seenMask |= 1 << EnvEpoch;
                        break;
                    case EnvProtocolHash:
                        RequireWire(wireType, PMWireType.Varint, field, "信封");
                        message.ProtocolHash = reader.ReadUInt32();
                        seenMask |= 1 << EnvProtocolHash;
                        break;
                    case EnvRequestId:
                        RequireWire(wireType, PMWireType.Varint, field, "信封");
                        message.RequestId = reader.ReadUInt64();
                        seenMask |= 1 << EnvRequestId;
                        break;
                    case EnvBody:
                        RequireWire(wireType, PMWireType.LengthDelimited, field, "信封");
                        bodyReader = reader.ReadSubReader();
                        seenMask |= 1 << EnvBody;
                        break;
                    case PMDsControlWire.FieldMac:
                        RequireWire(wireType, PMWireType.LengthDelimited, field, "信封");
                        int declaredMac = reader.PeekVarintLength();
                        if (declaredMac != PMDsControlWire.MacBytes)
                        {
                            throw new PMDsControlProtocolException(
                                "MAC 字段长度必须为 " + PMDsControlWire.MacBytes + "，收到 " + declaredMac);
                        }

                        message.MacFieldOffset = tagStart - offset;
                        message.Mac = reader.ReadBytesValue();
                        if (!reader.IsAtEnd)
                        {
                            throw new PMDsControlProtocolException("MAC 字段之后仍有尾部字节");
                        }

                        break;
                    default:
                        throw new PMDsControlProtocolException("未知的信封字段号 " + field);
                }
            }

            int requiredMask = (1 << EnvFormatVersion) | (1 << EnvMessageType) | (1 << EnvMatchId)
                | (1 << EnvDsId) | (1 << EnvEpoch) | (1 << EnvProtocolHash)
                | (1 << EnvRequestId) | (1 << EnvBody);
            if ((seenMask & requiredMask) != requiredMask)
            {
                throw new PMDsControlProtocolException("信封缺少必填字段（位掩码 0x" + seenMask.ToString("X") + "）");
            }

            if (message.FormatVersion != PMDsControlWire.FormatVersion)
            {
                throw new PMDsControlProtocolException("控制帧格式版本不受支持：" + message.FormatVersion);
            }

            if (message.Type == PMDsControlMessageType.Invalid || (int)message.Type > (int)PMDsControlMessageType.Error)
            {
                throw new PMDsControlProtocolException("未知的控制消息类型：" + (int)message.Type);
            }

            if (message.MatchId.Length == 0 || message.DsId.Length == 0)
            {
                throw new PMDsControlProtocolException("MatchId / DsId 不得为空");
            }

            if (message.Epoch == 0u)
            {
                throw new PMDsControlProtocolException("Epoch 不得为 0");
            }

            if (message.ProtocolHash == 0u)
            {
                throw new PMDsControlProtocolException("ProtocolHash 不得为 0");
            }

            message.Body = DecodeBody(bodyReader, message.Type);
            return message;
        }

        private static PMDsControlBody DecodeBody(PMNetReader reader, PMDsControlMessageType type)
        {
            switch (type)
            {
                case PMDsControlMessageType.Bootstrap:
                    return DecodeBootstrap(reader);
                case PMDsControlMessageType.Ready:
                    return DecodeReady(reader);
                case PMDsControlMessageType.Heartbeat:
                    return DecodeHeartbeat(reader);
                case PMDsControlMessageType.Result:
                    return DecodeResult(reader);
                case PMDsControlMessageType.ResultAck:
                    return DecodeResultAck(reader);
                case PMDsControlMessageType.Shutdown:
                    return DecodeShutdown(reader);
                case PMDsControlMessageType.Exited:
                    return DecodeExited(reader);
                case PMDsControlMessageType.Error:
                    return DecodeError(reader);
                default:
                    throw new PMDsControlProtocolException("未知的控制消息类型：" + (int)type);
            }
        }

        private static PMDsBootstrapBody DecodeBootstrap(PMNetReader reader)
        {
            PMDsBootstrapBody body = new PMDsBootstrapBody();
            List<PMDsBootstrapPlayer> players = new List<PMDsBootstrapPlayer>();
            bool haveDigest = false;
            int lastField = 0;

            while (!reader.IsAtEnd)
            {
                int field;
                PMWireType wireType;
                if (!reader.ReadTag(out field, out wireType))
                {
                    break;
                }

                // Bootstrap 的名册字段（2）是可重复字段：允许与前一个字段号相同。
                if (field < lastField || (field == lastField && field != BootPlayer))
                {
                    throw new PMDsControlProtocolException("Bootstrap 字段号非法或重复：" + field);
                }

                lastField = field;

                if (field == BootCollisionDigest)
                {
                    RequireWire(wireType, PMWireType.Varint, field, "Bootstrap");
                    body.CollisionDigest = reader.ReadUInt32();
                    haveDigest = true;
                }
                else if (field == BootPlayer)
                {
                    RequireWire(wireType, PMWireType.LengthDelimited, field, "Bootstrap");
                    players.Add(DecodeBootstrapPlayer(reader.ReadSubReader()));
                }
                else
                {
                    throw new PMDsControlProtocolException("未知的 Bootstrap 字段号 " + field);
                }
            }

            if (!haveDigest)
            {
                throw new PMDsControlProtocolException("Bootstrap 缺少 CollisionDigest");
            }

            body.Players = players.ToArray();
            ValidatePlayers(body.Players, "Bootstrap 名册");
            return body;
        }

        private static PMDsBootstrapPlayer DecodeBootstrapPlayer(PMNetReader reader)
        {
            PMDsBootstrapPlayer player = new PMDsBootstrapPlayer();
            int seenMask = 0;
            int lastField = 0;

            while (!reader.IsAtEnd)
            {
                int field;
                PMWireType wireType;
                if (!reader.ReadTag(out field, out wireType))
                {
                    break;
                }

                if (field <= lastField)
                {
                    throw new PMDsControlProtocolException("名册条目字段号非法或重复：" + field);
                }

                lastField = field;

                switch (field)
                {
                    case BootPlayerUid:
                        RequireWire(wireType, PMWireType.Varint, field, "名册条目");
                        player.Identity.Uid = reader.ReadSInt32();
                        break;
                    case BootPlayerPlayerId:
                        RequireWire(wireType, PMWireType.Varint, field, "名册条目");
                        player.Identity.PlayerId = reader.ReadSInt32();
                        break;
                    case BootPlayerTeamId:
                        RequireWire(wireType, PMWireType.Varint, field, "名册条目");
                        player.Identity.TeamId = reader.ReadSInt32();
                        break;
                    case BootPlayerHeroId:
                        RequireWire(wireType, PMWireType.Varint, field, "名册条目");
                        player.Identity.HeroId = reader.ReadSInt32();
                        break;
                    case BootPlayerTicket:
                        RequireWire(wireType, PMWireType.LengthDelimited, field, "名册条目");
                        player.Ticket = ReadBoundedBytes(reader, PMDsControlWire.MaxTicketBytes, "票据");
                        break;
                    default:
                        throw new PMDsControlProtocolException("未知的名册条目字段号 " + field);
                }

                seenMask |= 1 << field;
            }

            int required = (1 << BootPlayerUid) | (1 << BootPlayerPlayerId) | (1 << BootPlayerTeamId)
                | (1 << BootPlayerHeroId) | (1 << BootPlayerTicket);
            if ((seenMask & required) != required)
            {
                throw new PMDsControlProtocolException("名册条目缺少必填字段（位掩码 0x" + seenMask.ToString("X") + "）");
            }

            return player;
        }

        private static PMDsReadyBody DecodeReady(PMNetReader reader)
        {
            PMDsReadyBody body = new PMDsReadyBody();
            int seenMask = 0;
            int lastField = 0;

            while (!reader.IsAtEnd)
            {
                int field;
                PMWireType wireType;
                if (!reader.ReadTag(out field, out wireType))
                {
                    break;
                }

                if (field <= lastField)
                {
                    throw new PMDsControlProtocolException("Ready 字段号非法或重复：" + field);
                }

                lastField = field;
                seenMask |= 1 << field;

                switch (field)
                {
                    case ReadyBoundPort:
                        RequireWire(wireType, PMWireType.Varint, field, "Ready");
                        body.BoundPort = reader.ReadSInt32();
                        break;
                    case ReadySceneReady:
                        RequireWire(wireType, PMWireType.Varint, field, "Ready");
                        body.SceneReady = reader.ReadBool();
                        break;
                    case ReadyCollisionDigest:
                        RequireWire(wireType, PMWireType.Varint, field, "Ready");
                        body.CollisionDigest = reader.ReadUInt32();
                        break;
                    default:
                        throw new PMDsControlProtocolException("未知的 Ready 字段号 " + field);
                }
            }

            int required = (1 << ReadyBoundPort) | (1 << ReadySceneReady) | (1 << ReadyCollisionDigest);
            if ((seenMask & required) != required)
            {
                throw new PMDsControlProtocolException("Ready 缺少必填字段（位掩码 0x" + seenMask.ToString("X") + "）");
            }

            if (body.BoundPort <= 0 || body.BoundPort > 65535)
            {
                throw new PMDsControlProtocolException("Ready.BoundPort 非法：" + body.BoundPort);
            }

            if (body.CollisionDigest == 0u)
            {
                throw new PMDsControlProtocolException("Ready.CollisionDigest 不得为 0");
            }

            return body;
        }

        private static PMDsHeartbeatBody DecodeHeartbeat(PMNetReader reader)
        {
            PMDsHeartbeatBody body = new PMDsHeartbeatBody();
            int seenMask = 0;
            int lastField = 0;

            while (!reader.IsAtEnd)
            {
                int field;
                PMWireType wireType;
                if (!reader.ReadTag(out field, out wireType))
                {
                    break;
                }

                if (field <= lastField)
                {
                    throw new PMDsControlProtocolException("Heartbeat 字段号非法或重复：" + field);
                }

                lastField = field;
                seenMask |= 1 << field;

                switch (field)
                {
                    case HeartbeatUptime:
                        RequireWire(wireType, PMWireType.Varint, field, "Heartbeat");
                        body.UptimeMilliseconds = reader.ReadSInt64();
                        break;
                    case HeartbeatPlayerCount:
                        RequireWire(wireType, PMWireType.Varint, field, "Heartbeat");
                        body.PlayerCount = reader.ReadSInt32();
                        break;
                    default:
                        throw new PMDsControlProtocolException("未知的 Heartbeat 字段号 " + field);
                }
            }

            int required = (1 << HeartbeatUptime) | (1 << HeartbeatPlayerCount);
            if ((seenMask & required) != required)
            {
                throw new PMDsControlProtocolException("Heartbeat 缺少必填字段（位掩码 0x" + seenMask.ToString("X") + "）");
            }

            if (body.UptimeMilliseconds < 0)
            {
                throw new PMDsControlProtocolException("Heartbeat.UptimeMilliseconds 不得为负");
            }

            if (body.PlayerCount < 0 || body.PlayerCount > PMDsControlWire.MaxRosterPlayers)
            {
                throw new PMDsControlProtocolException("Heartbeat.PlayerCount 非法：" + body.PlayerCount);
            }

            return body;
        }

        private static PMDsResultBody DecodeResult(PMNetReader reader)
        {
            PMDsResultBody body = new PMDsResultBody();
            int seenMask = 0;
            int lastField = 0;
            bool sawSummary = false;

            while (!reader.IsAtEnd)
            {
                int field;
                PMWireType wireType;
                if (!reader.ReadTag(out field, out wireType))
                {
                    break;
                }

                if (field <= lastField)
                {
                    throw new PMDsControlProtocolException("Result 字段号非法或重复：" + field);
                }

                lastField = field;
                seenMask |= 1 << field;

                switch (field)
                {
                    case ResultIdField:
                        RequireWire(wireType, PMWireType.Varint, field, "Result");
                        body.ResultId = reader.ReadUInt64();
                        break;
                    case ResultWinnerTeam:
                        RequireWire(wireType, PMWireType.Varint, field, "Result");
                        body.WinnerTeamId = reader.ReadSInt32();
                        break;
                    case ResultSummary:
                        RequireWire(wireType, PMWireType.LengthDelimited, field, "Result");
                        body.Summary = ReadBoundedBytes(reader, PMDsControlWire.MaxSummaryBytes, "结算摘要");
                        sawSummary = true;
                        break;
                    default:
                        throw new PMDsControlProtocolException("未知的 Result 字段号 " + field);
                }
            }

            int required = (1 << ResultIdField) | (1 << ResultWinnerTeam);
            if ((seenMask & required) != required)
            {
                throw new PMDsControlProtocolException("Result 缺少必填字段（位掩码 0x" + seenMask.ToString("X") + "）");
            }

            if (body.ResultId == 0UL)
            {
                throw new PMDsControlProtocolException("Result.ResultId 不得为 0");
            }

            if (body.WinnerTeamId < -1)
            {
                throw new PMDsControlProtocolException("Result.WinnerTeamId 非法：" + body.WinnerTeamId);
            }

            if (!sawSummary)
            {
                body.Summary = PMDsBootstrapPlayer.EmptyBytes;
            }

            return body;
        }

        private static PMDsResultAckBody DecodeResultAck(PMNetReader reader)
        {
            PMDsResultAckBody body = new PMDsResultAckBody();
            bool haveId = false;
            int lastField = 0;

            while (!reader.IsAtEnd)
            {
                int field;
                PMWireType wireType;
                if (!reader.ReadTag(out field, out wireType))
                {
                    break;
                }

                if (field <= lastField)
                {
                    throw new PMDsControlProtocolException("ResultAck 字段号非法或重复：" + field);
                }

                lastField = field;

                if (field == AckResultIdField)
                {
                    RequireWire(wireType, PMWireType.Varint, field, "ResultAck");
                    body.ResultId = reader.ReadUInt64();
                    haveId = true;
                }
                else
                {
                    throw new PMDsControlProtocolException("未知的 ResultAck 字段号 " + field);
                }
            }

            if (!haveId)
            {
                throw new PMDsControlProtocolException("ResultAck 缺少 ResultId");
            }

            if (body.ResultId == 0UL)
            {
                throw new PMDsControlProtocolException("ResultAck.ResultId 不得为 0");
            }

            return body;
        }

        private static PMDsShutdownBody DecodeShutdown(PMNetReader reader)
        {
            PMDsShutdownBody body = new PMDsShutdownBody();
            bool haveReason = false;
            int lastField = 0;

            while (!reader.IsAtEnd)
            {
                int field;
                PMWireType wireType;
                if (!reader.ReadTag(out field, out wireType))
                {
                    break;
                }

                if (field <= lastField)
                {
                    throw new PMDsControlProtocolException("Shutdown 字段号非法或重复：" + field);
                }

                lastField = field;

                if (field == ShutdownReasonCode)
                {
                    RequireWire(wireType, PMWireType.Varint, field, "Shutdown");
                    body.ReasonCode = reader.ReadUInt32();
                    haveReason = true;
                }
                else
                {
                    throw new PMDsControlProtocolException("未知的 Shutdown 字段号 " + field);
                }
            }

            if (!haveReason)
            {
                throw new PMDsControlProtocolException("Shutdown 缺少 ReasonCode");
            }

            return body;
        }

        private static PMDsExitedBody DecodeExited(PMNetReader reader)
        {
            PMDsExitedBody body = new PMDsExitedBody();
            bool haveCode = false;
            int lastField = 0;

            while (!reader.IsAtEnd)
            {
                int field;
                PMWireType wireType;
                if (!reader.ReadTag(out field, out wireType))
                {
                    break;
                }

                if (field <= lastField)
                {
                    throw new PMDsControlProtocolException("Exited 字段号非法或重复：" + field);
                }

                lastField = field;

                if (field == ExitedExitCode)
                {
                    RequireWire(wireType, PMWireType.Varint, field, "Exited");
                    body.ExitCode = reader.ReadSInt32();
                    haveCode = true;
                }
                else
                {
                    throw new PMDsControlProtocolException("未知的 Exited 字段号 " + field);
                }
            }

            if (!haveCode)
            {
                throw new PMDsControlProtocolException("Exited 缺少 ExitCode");
            }

            return body;
        }

        private static PMDsErrorBody DecodeError(PMNetReader reader)
        {
            PMDsErrorBody body = new PMDsErrorBody();
            int seenMask = 0;
            int lastField = 0;

            while (!reader.IsAtEnd)
            {
                int field;
                PMWireType wireType;
                if (!reader.ReadTag(out field, out wireType))
                {
                    break;
                }

                if (field <= lastField)
                {
                    throw new PMDsControlProtocolException("Error 字段号非法或重复：" + field);
                }

                lastField = field;
                seenMask |= 1 << field;

                switch (field)
                {
                    case ErrorCode:
                        RequireWire(wireType, PMWireType.Varint, field, "Error");
                        body.Code = reader.ReadUInt32();
                        break;
                    case ErrorMessage:
                        RequireWire(wireType, PMWireType.LengthDelimited, field, "Error");
                        body.Message = ReadBoundedString(reader, PMDsControlWire.MaxErrorMessageBytes, "错误文本");
                        break;
                    default:
                        throw new PMDsControlProtocolException("未知的 Error 字段号 " + field);
                }
            }

            int required = (1 << ErrorCode) | (1 << ErrorMessage);
            if ((seenMask & required) != required)
            {
                throw new PMDsControlProtocolException("Error 缺少必填字段（位掩码 0x" + seenMask.ToString("X") + "）");
            }

            return body;
        }

        private static void EncodeBody(PMNetWriter writer, PMDsControlBody body)
        {
            switch (body.MessageType)
            {
                case PMDsControlMessageType.Bootstrap:
                {
                    PMDsBootstrapBody typed = (PMDsBootstrapBody)body;
                    if (typed.CollisionDigest == 0u)
                    {
                        throw new PMDsControlProtocolException("Bootstrap.CollisionDigest 不得为 0");
                    }

                    ValidatePlayers(typed.Players, "Bootstrap 名册");

                    writer.WriteTag(BootCollisionDigest, PMWireType.Varint);
                    writer.WriteUInt32(typed.CollisionDigest);
                    for (int i = 0; i < typed.Players.Length; i++)
                    {
                        PMNetWriter sub = writer.RentSubWriter();
                        EncodeBootstrapPlayer(sub, typed.Players[i]);
                        writer.WriteSubMessage(BootPlayer, sub);
                    }

                    break;
                }

                case PMDsControlMessageType.Ready:
                {
                    PMDsReadyBody typed = (PMDsReadyBody)body;
                    if (typed.BoundPort <= 0 || typed.BoundPort > 65535)
                    {
                        throw new PMDsControlProtocolException("Ready.BoundPort 非法：" + typed.BoundPort);
                    }

                    if (typed.CollisionDigest == 0u)
                    {
                        throw new PMDsControlProtocolException("Ready.CollisionDigest 不得为 0");
                    }

                    writer.WriteTag(ReadyBoundPort, PMWireType.Varint);
                    writer.WriteSInt32(typed.BoundPort);
                    writer.WriteTag(ReadySceneReady, PMWireType.Varint);
                    writer.WriteBool(typed.SceneReady);
                    writer.WriteTag(ReadyCollisionDigest, PMWireType.Varint);
                    writer.WriteUInt32(typed.CollisionDigest);
                    break;
                }

                case PMDsControlMessageType.Heartbeat:
                {
                    PMDsHeartbeatBody typed = (PMDsHeartbeatBody)body;
                    writer.WriteTag(HeartbeatUptime, PMWireType.Varint);
                    writer.WriteSInt64(typed.UptimeMilliseconds);
                    writer.WriteTag(HeartbeatPlayerCount, PMWireType.Varint);
                    writer.WriteSInt32(typed.PlayerCount);
                    break;
                }

                case PMDsControlMessageType.Result:
                {
                    PMDsResultBody typed = (PMDsResultBody)body;
                    if (typed.ResultId == 0UL)
                    {
                        throw new PMDsControlProtocolException("Result.ResultId 不得为 0");
                    }

                    if (typed.WinnerTeamId < -1)
                    {
                        throw new PMDsControlProtocolException("Result.WinnerTeamId 非法：" + typed.WinnerTeamId);
                    }

                    byte[] summary = typed.Summary ?? PMDsBootstrapPlayer.EmptyBytes;
                    if (summary.Length > PMDsControlWire.MaxSummaryBytes)
                    {
                        throw new PMDsControlProtocolException(
                            "结算摘要超过上限：" + summary.Length + " > " + PMDsControlWire.MaxSummaryBytes);
                    }

                    writer.WriteTag(ResultIdField, PMWireType.Varint);
                    writer.WriteUInt64(typed.ResultId);
                    writer.WriteTag(ResultWinnerTeam, PMWireType.Varint);
                    writer.WriteSInt32(typed.WinnerTeamId);
                    writer.WriteTag(ResultSummary, PMWireType.LengthDelimited);
                    writer.WriteBytesValue(summary);
                    break;
                }

                case PMDsControlMessageType.ResultAck:
                {
                    PMDsResultAckBody typed = (PMDsResultAckBody)body;
                    if (typed.ResultId == 0UL)
                    {
                        throw new PMDsControlProtocolException("ResultAck.ResultId 不得为 0");
                    }

                    writer.WriteTag(AckResultIdField, PMWireType.Varint);
                    writer.WriteUInt64(typed.ResultId);
                    break;
                }

                case PMDsControlMessageType.Shutdown:
                {
                    PMDsShutdownBody typed = (PMDsShutdownBody)body;
                    writer.WriteTag(ShutdownReasonCode, PMWireType.Varint);
                    writer.WriteUInt32(typed.ReasonCode);
                    break;
                }

                case PMDsControlMessageType.Exited:
                {
                    PMDsExitedBody typed = (PMDsExitedBody)body;
                    writer.WriteTag(ExitedExitCode, PMWireType.Varint);
                    writer.WriteSInt32(typed.ExitCode);
                    break;
                }

                case PMDsControlMessageType.Error:
                {
                    PMDsErrorBody typed = (PMDsErrorBody)body;
                    string text = typed.Message ?? string.Empty;
                    EnsureWithinBytes(text, PMDsControlWire.MaxErrorMessageBytes, "错误文本");
                    writer.WriteTag(ErrorCode, PMWireType.Varint);
                    writer.WriteUInt32(typed.Code);
                    writer.WriteTag(ErrorMessage, PMWireType.LengthDelimited);
                    writer.WriteStringValue(text);
                    break;
                }

                default:
                    throw new PMDsControlProtocolException("无法编码的消息类型：" + body.MessageType);
            }
        }

        private static void EncodeBootstrapPlayer(PMNetWriter writer, PMDsBootstrapPlayer player)
        {
            if (player == null)
            {
                throw new PMDsControlProtocolException("名册条目为 null");
            }

            byte[] ticket = player.Ticket ?? PMDsBootstrapPlayer.EmptyBytes;
            if (ticket.Length > PMDsControlWire.MaxTicketBytes)
            {
                throw new PMDsControlProtocolException(
                    "票据超过上限：" + ticket.Length + " > " + PMDsControlWire.MaxTicketBytes);
            }

            writer.WriteTag(BootPlayerUid, PMWireType.Varint);
            writer.WriteSInt32(player.Identity.Uid);
            writer.WriteTag(BootPlayerPlayerId, PMWireType.Varint);
            writer.WriteSInt32(player.Identity.PlayerId);
            writer.WriteTag(BootPlayerTeamId, PMWireType.Varint);
            writer.WriteSInt32(player.Identity.TeamId);
            writer.WriteTag(BootPlayerHeroId, PMWireType.Varint);
            writer.WriteSInt32(player.Identity.HeroId);
            writer.WriteTag(BootPlayerTicket, PMWireType.LengthDelimited);
            writer.WriteBytesValue(ticket);
        }

        /// <summary>名册结构校验（读写两侧共用同一口径）。非法即抛。</summary>
        public static void ValidatePlayers(PMDsBootstrapPlayer[] players, string what)
        {
            if (players == null)
            {
                throw new PMDsControlProtocolException((what ?? "名册") + " 为 null");
            }

            if (players.Length == 0 || players.Length > PMDsControlWire.MaxRosterPlayers)
            {
                throw new PMDsControlProtocolException(
                    (what ?? "名册") + " 人数非法：" + players.Length + "（要求 1.." + PMDsControlWire.MaxRosterPlayers + "）");
            }

            for (int i = 0; i < players.Length; i++)
            {
                if (players[i] == null)
                {
                    throw new PMDsControlProtocolException((what ?? "名册") + " 第 " + i + " 项为 null");
                }

                for (int j = i + 1; j < players.Length; j++)
                {
                    if (players[j] != null && players[i].Identity.Uid == players[j].Identity.Uid)
                    {
                        throw new PMDsControlProtocolException((what ?? "名册") + " Uid 重复：" + players[i].Identity.Uid);
                    }

                    if (players[j] != null && players[i].Identity.PlayerId == players[j].Identity.PlayerId)
                    {
                        throw new PMDsControlProtocolException(
                            (what ?? "名册") + " PlayerId 重复：" + players[i].Identity.PlayerId);
                    }
                }
            }

            PMDsRosterIdentity[] identities = new PMDsRosterIdentity[players.Length];
            for (int i = 0; i < players.Length; i++)
            {
                identities[i] = players[i].Identity;
            }

            ValidateIdentities(identities, what);
        }

        /// <summary>名册身份字段范围校验（读写两侧共用同一口径）。非法即抛。</summary>
        public static void ValidateIdentities(PMDsRosterIdentity[] roster, string what)
        {
            if (roster == null)
            {
                throw new PMDsControlProtocolException((what ?? "名册") + " 为 null");
            }

            if (roster.Length == 0 || roster.Length > PMDsControlWire.MaxRosterPlayers)
            {
                throw new PMDsControlProtocolException(
                    (what ?? "名册") + " 人数非法：" + roster.Length + "（要求 1.." + PMDsControlWire.MaxRosterPlayers + "）");
            }

            for (int i = 0; i < roster.Length; i++)
            {
                PMDsRosterIdentity identity = roster[i];
                if (identity.Uid <= 0)
                {
                    throw new PMDsControlProtocolException((what ?? "名册") + " Uid 必须 > 0，收到 " + identity.Uid);
                }

                if (identity.PlayerId <= 0)
                {
                    throw new PMDsControlProtocolException((what ?? "名册") + " PlayerId 必须 > 0，收到 " + identity.PlayerId);
                }

                if (identity.TeamId < 0)
                {
                    throw new PMDsControlProtocolException((what ?? "名册") + " TeamId 不得为负：" + identity.TeamId);
                }

                if (identity.HeroId < 0)
                {
                    throw new PMDsControlProtocolException((what ?? "名册") + " HeroId 不得为负：" + identity.HeroId);
                }

                for (int j = i + 1; j < roster.Length; j++)
                {
                    if (roster[j].Uid == identity.Uid)
                    {
                        throw new PMDsControlProtocolException((what ?? "名册") + " Uid 重复：" + identity.Uid);
                    }

                    if (roster[j].PlayerId == identity.PlayerId)
                    {
                        throw new PMDsControlProtocolException((what ?? "名册") + " PlayerId 重复：" + identity.PlayerId);
                    }
                }
            }
        }

        internal static void RequireWire(PMWireType actual, PMWireType expected, int field, string what)
        {
            if (actual != expected)
            {
                throw new PMDsControlProtocolException(
                    what + " 字段 " + field + " 的 wire type 非法：期望 " + expected + "，收到 " + actual);
            }
        }

        internal static string ReadBoundedString(PMNetReader reader, int maxBytes, string what)
        {
            int declared = reader.PeekVarintLength();
            if (declared > maxBytes)
            {
                throw new PMDsControlProtocolException(
                    what + " 声明长度 " + declared + " 字节超过上限 " + maxBytes + " 字节");
            }

            return reader.ReadStringValue();
        }

        internal static byte[] ReadBoundedBytes(PMNetReader reader, int maxBytes, string what)
        {
            int declared = reader.PeekVarintLength();
            if (declared > maxBytes)
            {
                throw new PMDsControlProtocolException(
                    what + " 声明长度 " + declared + " 字节超过上限 " + maxBytes + " 字节");
            }

            return reader.ReadBytesValue();
        }

        private static void RequireNonEmptyBounded(string value, int maxBytes, string what)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new PMDsControlProtocolException(what + " 不得为空");
            }

            EnsureWithinBytes(value, maxBytes, what);
        }

        internal static void EnsureWithinBytes(string value, int maxBytes, string what)
        {
            int bytes = Encoding.UTF8.GetByteCount(value ?? string.Empty);
            if (bytes > maxBytes)
            {
                throw new PMDsControlProtocolException(what + " 超过上限：" + bytes + " > " + maxBytes + " 字节");
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 加密原语（只用 BCL：netstandard2.0 + C# 7.3 内）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 控制面用到的密码学原语。
    ///
    /// 约束：只用 .NET Standard 2.0 的 BCL（与 Unity 2019.4 的 .NET Standard 2.0 profile 一致），
    /// 不引第三方；<see cref="RandomBytes"/> 必须是**加密随机**（绝不用 Random / 时间戳当秘密）。
    ///
    /// 这个类里没有任何「打印秘密」的入口：<see cref="Fingerprint"/> 只用于**公开摘要**
    /// （碰撞配置摘要 / 结果内容摘要），它不是密钥的派生输出。
    /// </summary>
    public static class PMDsCrypto
    {
        /// <summary>加密随机字节。</summary>
        public static byte[] RandomBytes(int count)
        {
            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException("count", "随机字节数必须为正");
            }

            byte[] buffer = new byte[count];
            using (System.Security.Cryptography.RandomNumberGenerator rng =
                System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(buffer);
            }

            return buffer;
        }

        /// <summary>HMAC-SHA256。</summary>
        public static byte[] HmacSha256(byte[] key, byte[] data, int offset, int count)
        {
            if (key == null)
            {
                throw new ArgumentNullException("key");
            }

            if (data == null)
            {
                throw new ArgumentNullException("data");
            }

            if (offset < 0 || count < 0 || offset + count > data.Length)
            {
                throw new ArgumentOutOfRangeException("count", "HMAC 输入范围越界");
            }

            using (System.Security.Cryptography.HMACSHA256 hmac =
                new System.Security.Cryptography.HMACSHA256(key))
            {
                return hmac.ComputeHash(data, offset, count);
            }
        }

        /// <summary>HMAC-SHA256（整段）。</summary>
        public static byte[] HmacSha256(byte[] key, byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException("data");
            }

            return HmacSha256(key, data, 0, data.Length);
        }

        /// <summary>SHA-256。</summary>
        public static byte[] Sha256(byte[] data, int offset, int count)
        {
            if (data == null)
            {
                throw new ArgumentNullException("data");
            }

            if (offset < 0 || count < 0 || offset + count > data.Length)
            {
                throw new ArgumentOutOfRangeException("count", "SHA256 输入范围越界");
            }

            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            {
                return sha.ComputeHash(data, offset, count);
            }
        }

        /// <summary>
        /// **常量时间**比较（长度不同直接 false；长度相同则全字节异或累积，不提前返回）。
        ///
        /// 为什么不能用 `SequenceEqual` / 逐字节 `break`：那会把「前几个字节对不对」
        /// 通过耗时泄漏出去，配合可重放的验签入口就能逐字节爆破 MAC。
        /// </summary>
        public static bool FixedTimeEquals(byte[] a, int aOffset, byte[] b, int bOffset, int count)
        {
            if (a == null || b == null)
            {
                return false;
            }

            if (count < 0 || aOffset < 0 || bOffset < 0)
            {
                return false;
            }

            if (aOffset + count > a.Length || bOffset + count > b.Length)
            {
                return false;
            }

            int diff = 0;
            for (int i = 0; i < count; i++)
            {
                diff |= a[aOffset + i] ^ b[bOffset + i];
            }

            return diff == 0;
        }

        /// <summary>
        /// 8 位十六进制指纹（SHA-256 前 4 字节）。
        /// 用途：让两端**在不打印秘密的前提下**核对是不是同一份材料（例如同一张票据、同一把控制密钥）。
        /// 它不是可逆的，也不是 MAC，不得当作认证凭据。
        /// </summary>
        public static string Fingerprint(byte[] data, int offset, int count)
        {
            byte[] hash = Sha256(data, offset, count);
            StringBuilder builder = new StringBuilder(8);
            for (int i = 0; i < 4; i++)
            {
                builder.Append(hash[i].ToString("x2"));
            }

            return builder.ToString();
        }

        /// <summary>整段指纹。</summary>
        public static string Fingerprint(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException("data");
            }

            return Fingerprint(data, 0, data.Length);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 签名 / 验签
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 控制帧签名器/验签器。
    ///
    /// 密钥来源**只能是本进程自己的配置**：Lobby 侧的每局随机密钥、或 DS 侧从同机引导文件读到的
    /// 同一把密钥。**没有任何 API 接受来自对端的密钥**（契约 §2「不接受客户自己给密钥」）。
    ///
    /// 验签口径：MAC = HMAC-SHA256(key, RawPayload[0 .. MacFieldOffset))。
    /// 覆盖范围刻意是「MAC 字段之前的原始字节」，因此验签不依赖「重新编码得到同样字节」
    /// 这一假设：解码器只要准确记录 MAC 字段起点即可。
    /// </summary>
    public sealed class PMDsControlSigner
    {
        private readonly byte[] _key;

        /// <summary>用一把 256 位密钥构造。</summary>
        public PMDsControlSigner(byte[] key)
        {
            if (key == null)
            {
                throw new ArgumentNullException("key");
            }

            if (key.Length != PMDsControlWire.ControlKeyBytes)
            {
                throw new ArgumentException(
                    "控制密钥长度必须为 " + PMDsControlWire.ControlKeyBytes + " 字节，收到 " + key.Length, "key");
            }

            _key = new byte[key.Length];
            Buffer.BlockCopy(key, 0, _key, 0, key.Length);
        }

        /// <summary>密钥指纹（诊断用，不泄漏密钥）。</summary>
        public string KeyFingerprint { get { return PMDsCrypto.Fingerprint(_key); } }

        /// <summary>签名并返回完整载荷（同时回填 message 的 RawPayload/Mac/MacFieldOffset）。</summary>
        public byte[] Sign(PMDsControlMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException("message");
            }

            byte[] prefix = PMDsControlCodec.EncodePrefix(message);
            byte[] mac = PMDsCrypto.HmacSha256(_key, prefix);
            byte[] payload = PMDsControlCodec.AppendMac(prefix, mac);

            message.RawPayload = payload;
            message.Mac = mac;
            message.MacFieldOffset = prefix.Length;
            return payload;
        }

        /// <summary>对已解码消息验签（要求带 MAC）。</summary>
        public bool Verify(PMDsControlMessage message)
        {
            if (message == null || message.RawPayload == null || !message.HasMac)
            {
                return false;
            }

            byte[] expected = PMDsCrypto.HmacSha256(_key, message.RawPayload, 0, message.MacFieldOffset);
            return PMDsCrypto.FixedTimeEquals(expected, 0, message.Mac, 0, PMDsControlWire.MacBytes);
        }

        /// <summary>
        /// 「解码 + 验签」唯一入口。成功时 out 一条**已经验签通过**的消息。
        /// 失败时 out 为 null，并且**调用方未获得任何可用的业务字段**（避免先解析后补验签）。
        /// </summary>
        public bool VerifyRaw(byte[] payload, int offset, int count,
            out PMDsControlMessage message, out PMDsControlVerifyFault fault)
        {
            message = null;
            fault = PMDsControlVerifyFault.None;

            if (payload == null)
            {
                throw new ArgumentNullException("payload");
            }

            PMDsControlMessage decoded;
            try
            {
                decoded = PMDsControlCodec.Decode(payload, offset, count);
            }
            catch (PMDsControlProtocolException)
            {
                fault = PMDsControlVerifyFault.MalformedPayload;
                return false;
            }

            if (!decoded.HasMac)
            {
                fault = PMDsControlVerifyFault.MissingMac;
                return false;
            }

            if (!Verify(decoded))
            {
                fault = PMDsControlVerifyFault.MacMismatch;
                return false;
            }

            message = decoded;
            return true;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 分帧（4 字节小端长度前缀 + 载荷；支持半包与粘包）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>分帧工具。</summary>
    public static class PMDsControlFraming
    {
        /// <summary>把载荷包成完整帧（4 字节 LE 长度 + 载荷）。超限即抛。</summary>
        public static byte[] Frame(byte[] payload, int offset, int count)
        {
            if (payload == null)
            {
                throw new ArgumentNullException("payload");
            }

            if (offset < 0 || count < 0 || offset + count > payload.Length)
            {
                throw new ArgumentOutOfRangeException("count", "分帧范围越界");
            }

            if (!IsValidPayloadLength(count))
            {
                throw new PMDsControlProtocolException(
                    "控制帧载荷长度非法：" + PMDsControlWire.DescribePayloadLength(count));
            }

            byte[] frame = new byte[PMDsControlWire.LengthPrefixBytes + count];
            WriteLengthPrefix(frame, 0, count);
            Buffer.BlockCopy(payload, offset, frame, PMDsControlWire.LengthPrefixBytes, count);
            return frame;
        }

        /// <summary>整段重载。</summary>
        public static byte[] Frame(byte[] payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException("payload");
            }

            return Frame(payload, 0, payload.Length);
        }

        /// <summary>写入 4 字节小端长度前缀。</summary>
        public static void WriteLengthPrefix(byte[] buffer, int offset, int length)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException("buffer");
            }

            if (offset < 0 || offset + PMDsControlWire.LengthPrefixBytes > buffer.Length)
            {
                throw new ArgumentOutOfRangeException("offset", "长度前缀写入越界");
            }

            buffer[offset] = (byte)(length & 0xFF);
            buffer[offset + 1] = (byte)((length >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((length >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((length >> 24) & 0xFF);
        }

        /// <summary>读取 4 字节小端长度前缀（按无符号解释）。</summary>
        public static int ReadLengthPrefix(byte[] buffer, int offset)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException("buffer");
            }

            if (offset < 0 || offset + PMDsControlWire.LengthPrefixBytes > buffer.Length)
            {
                throw new ArgumentOutOfRangeException("offset", "长度前缀读取越界");
            }

            // 用 uint 解释再截断：0xFFFFFFFF 这类恶意前缀必须变成「明显超限」而不是负数。
            uint raw = (uint)(buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24));
            if (raw > int.MaxValue)
            {
                return int.MaxValue;
            }

            return (int)raw;
        }

        /// <summary>载荷长度是否合法（1 .. 64KiB）。</summary>
        public static bool IsValidPayloadLength(int length)
        {
            return length > 0 && length <= PMDsControlWire.MaxFramePayloadBytes;
        }
    }

    /// <summary>
    /// 控制帧流式解码器：处理**半包**（一个帧分多次到达）与**粘包**（一次到达多个帧）。
    ///
    /// 有界性：
    /// - 只保留「一个未完成帧」的字节（≤ 4 + 64KiB），容量超出即回落。
    /// - 长度前缀声明超限时**立即**抛 <see cref="PMDsControlFrameFault.OversizeLength"/> 语义的异常，
    ///   并进入故障态（不再等待那 64KiB+ 的数据到达，否则就是给对端一个免费的内存放大器）。
    /// - 故障态不可恢复：调用方必须断开并重建（<see cref="Reset"/> 只用于显式重建会话）。
    /// </summary>
    public sealed class PMDsControlFrameDecoder
    {
        private readonly int _maxPayloadBytes;
        private readonly int _maxBufferedBytes;
        private readonly Queue<byte[]> _frames = new Queue<byte[]>();

        private byte[] _buffer;
        private int _start;
        private int _count;
        private bool _faulted;

        public PMDsControlFrameDecoder()
            : this(PMDsControlWire.MaxFramePayloadBytes)
        {
        }

        public PMDsControlFrameDecoder(int maxPayloadBytes)
        {
            if (maxPayloadBytes <= 0 || maxPayloadBytes > PMDsControlWire.MaxFramePayloadBytes)
            {
                throw new ArgumentOutOfRangeException(
                    "maxPayloadBytes", "上限必须在 1.." + PMDsControlWire.MaxFramePayloadBytes + " 之间");
            }

            _maxPayloadBytes = maxPayloadBytes;
            int frameTotal = PMDsControlWire.LengthPrefixBytes + maxPayloadBytes;
            _maxBufferedBytes = frameTotal + PMDsControlWire.LengthPrefixBytes + maxPayloadBytes;
            _buffer = new byte[Math.Min(_maxBufferedBytes, 1024)];
        }

        /// <summary>本解码器允许的最大载荷字节数。</summary>
        public int MaxPayloadBytes { get { return _maxPayloadBytes; } }

        /// <summary>当前尚未组帧完成的字节数。</summary>
        public int PendingBytes { get { return _count; } }

        /// <summary>是否已进入故障态（必须重建连接）。</summary>
        public bool IsFaulted { get { return _faulted; } }

        /// <summary>已成功解出的帧数。</summary>
        public long DecodedFrameCount { get; private set; }

        /// <summary>被拒绝的帧数（超限/零长）。</summary>
        public long RejectedFrameCount { get; private set; }

        /// <summary>观测到的最大滞留字节数（用于证明「有界」不是空话）。</summary>
        public int PeakPendingBytes { get; private set; }

        /// <summary>最后一次故障分类。</summary>
        public PMDsControlFrameFault Fault { get; private set; }

        /// <summary>喂入字节。长度前缀超限/为 0 时抛异常并进入故障态。</summary>
        public void Append(byte[] data, int offset, int count)
        {
            if (data == null)
            {
                throw new ArgumentNullException("data");
            }

            if (offset < 0 || count < 0 || offset + count > data.Length)
            {
                throw new ArgumentOutOfRangeException("count", "喂入范围越界");
            }

            if (_faulted)
            {
                throw new PMDsControlProtocolException("控制帧解码器已进入故障态（" + Fault + "），必须重建连接");
            }

            if (count == 0)
            {
                return;
            }

            EnsureCapacity(_count + count);
            Buffer.BlockCopy(data, offset, _buffer, _start + _count, count);
            _count += count;
            if (_count > PeakPendingBytes)
            {
                PeakPendingBytes = _count;
            }

            Drain();
            Compact();
        }

        /// <summary>取出一个已完成的**载荷**（不含长度前缀）。没有完整帧时返回 false。</summary>
        public bool TryDequeue(out byte[] payload)
        {
            payload = null;
            if (_frames.Count == 0)
            {
                return false;
            }

            payload = _frames.Dequeue();
            return true;
        }

        /// <summary>取出一个完整帧（含长度前缀）。</summary>
        public bool TryDequeueFrame(out byte[] frame)
        {
            frame = null;
            byte[] payload;
            if (!TryDequeue(out payload))
            {
                return false;
            }

            frame = PMDsControlFraming.Frame(payload);
            return true;
        }

        /// <summary>显式重建（仅用于会话重建；故障态下也允许）。</summary>
        public void Reset()
        {
            _frames.Clear();
            _start = 0;
            _count = 0;
            _faulted = false;
            Fault = PMDsControlFrameFault.None;
            DecodedFrameCount = 0;
            RejectedFrameCount = 0;
            PeakPendingBytes = 0;
        }

        private void Drain()
        {
            while (true)
            {
                if (_count < PMDsControlWire.LengthPrefixBytes)
                {
                    return;
                }

                int declared = PMDsControlFraming.ReadLengthPrefix(_buffer, _start);
                if (declared <= 0)
                {
                    Fault = PMDsControlFrameFault.ZeroLength;
                    _faulted = true;
                    RejectedFrameCount++;
                    throw new PMDsControlProtocolException("控制帧长度前缀为 0（空帧非法）");
                }

                if (declared > _maxPayloadBytes)
                {
                    Fault = PMDsControlFrameFault.OversizeLength;
                    _faulted = true;
                    RejectedFrameCount++;
                    throw new PMDsControlProtocolException(
                        "控制帧声明长度 " + declared + " 超过上限 " + _maxPayloadBytes + "（立即拒绝，不等待数据到达）");
                }

                int frameTotal = PMDsControlWire.LengthPrefixBytes + declared;
                if (_count < frameTotal)
                {
                    return;
                }

                byte[] payload = new byte[declared];
                Buffer.BlockCopy(_buffer, _start + PMDsControlWire.LengthPrefixBytes, payload, 0, declared);
                _frames.Enqueue(payload);
                DecodedFrameCount++;

                _start += frameTotal;
                _count -= frameTotal;
            }
        }

        private void Compact()
        {
            if (_start == 0)
            {
                return;
            }

            if (_count > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _count);
            }

            _start = 0;
            if (_buffer.Length > _maxBufferedBytes)
            {
                _buffer = new byte[_maxBufferedBytes];
            }
        }

        private void EnsureCapacity(int required)
        {
            if (required <= _buffer.Length - _start)
            {
                return;
            }

            if (_start > 0)
            {
                Compact();
                if (required <= _buffer.Length)
                {
                    return;
                }
            }

            int needed = Math.Min(Math.Max(required, _buffer.Length * 2), _maxBufferedBytes);
            if (needed < required)
            {
                // 单次喂入本身就超过「一个最大帧」：仍必须容纳它，否则就无法解出其中第一个帧。
                needed = required;
            }

            byte[] grown = new byte[needed];
            Buffer.BlockCopy(_buffer, _start, grown, 0, _count);
            _buffer = grown;
            _start = 0;
        }
    }
}
