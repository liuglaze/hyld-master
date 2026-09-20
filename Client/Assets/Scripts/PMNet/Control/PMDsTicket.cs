using System;
using System.Collections.Generic;
using System.Text;

namespace PMNet.Control
{
    /// <summary>
    /// R3-A1：每局控制密钥与玩家票据（HMAC-SHA256）。
    ///
    /// 事实来源：`Docs/plans/net-r3-control-contract.md` §2（身份及票据）。
    ///
    /// 契约要点（逐条对应实现）：
    /// - **每局 256 位随机控制密钥**，用加密随机数生成（<see cref="PMDsMatchKey.Create"/>），
    ///   不用 `Random`、不用时间戳当秘密。
    /// - 玩家票据用 HMAC-SHA256 **绑定** (MatchId, DsId, Epoch, ProtocolHash) +
    ///   玩家身份 (Uid, PlayerId, TeamId, HeroId) + 到期 UTC 秒 + 随机 Nonce。
    /// - **常量时间比对 MAC**（<see cref="PMDsCrypto.FixedTimeEquals"/>）。
    /// - 默认有效期 120 秒（可配置），且强制上限 <see cref="PMDsTicketPolicy.MaxLifetimeSeconds"/>。
    /// - **绝不打印密钥或完整票据**：`ToString()` 只输出身份与长度/指纹。
    ///
    /// ⚠ 必须明确的一条边界（契约 §2 原文）：本模块**只验证票据的真实性与字段**。
    /// 端点消费账本（同一张票据只能被唯一的真实端点消费一次、重连时由 Lobby 显式重发）
    /// 属于 R3-B 的连接接受器。**不得把 MAC 验证当作防重放已完成。**
    /// </summary>
    public static class PMDsTicketWire
    {
        /// <summary>票据格式版本。</summary>
        public const int FormatVersion = 1;

        /// <summary>随机 Nonce 字节数（128 位）。</summary>
        public const int NonceBytes = 16;

        /// <summary>MAC 字节数（HMAC-SHA256）。</summary>
        public const int MacBytes = PMDsControlWire.MacBytes;

        /// <summary>票据最大字节数（96 + 128 + 128 = 352，见文件头布局）。</summary>
        public const int MaxTicketBytes = 352;

        /// <summary>票据魔数（ASCII "PMDS" 的大端解释）。</summary>
        public const uint Magic = 0x504D4453u;

        internal static readonly byte[] MagicBytes = new byte[] { (byte)'P', (byte)'M', (byte)'D', (byte)'S' };
    }

    /// <summary>票据校验结论。**每一个取值都是「拒绝」的明确原因**，不合并成 bool。</summary>
    public enum PMDsTicketVerdict : byte
    {
        /// <summary>通过。</summary>
        Valid = 0,

        /// <summary>结构非法（长度/魔数/字段越界/字符串超限）。</summary>
        Malformed = 1,

        /// <summary>格式版本不受支持。</summary>
        BadVersion = 2,

        /// <summary>MAC 常量时间比对失败（篡改 / 错密钥）。</summary>
        MacMismatch = 3,

        /// <summary>有效期策略不合规（过期时间早于签发时间，或有效期超过上限）。</summary>
        LifetimePolicy = 4,

        /// <summary>签发时间在未来（超出允许的时钟偏差）。</summary>
        NotYetValid = 5,

        /// <summary>已过期。</summary>
        Expired = 6,

        /// <summary>不是本局的票据。</summary>
        WrongMatch = 7,

        /// <summary>世代（Epoch）不匹配。</summary>
        WrongEpoch = 8,

        /// <summary>协议摘要不匹配。</summary>
        WrongProtocolHash = 9,

        /// <summary>DsId 不匹配。</summary>
        WrongDsId = 10,

        /// <summary>身份字段与预期票据持有者不一致（或不在当前局名册内）。</summary>
        NotInRoster = 11,

        /// <summary>本协调器尚未分配任何会话（没有密钥可用）。</summary>
        NoSession = 12,
    }

    /// <summary>票据策略常量（契约 §2 的默认值 + 硬化上限）。</summary>
    public static class PMDsTicketPolicy
    {
        /// <summary>默认有效期：120 秒（契约 §2）。</summary>
        public const int DefaultLifetimeSeconds = 120;

        /// <summary>有效期硬上限：600 秒。签发方不得给出更长的票据（即使它持有密钥）。</summary>
        public const int MaxLifetimeSeconds = 600;

        /// <summary>允许的时钟偏差：30 秒（只用于「签发时间在未来」的判定）。</summary>
        public const int MaxClockSkewSeconds = 30;
    }

    /// <summary>校验票据时比对的「本局身份期望」。构造后不可变。</summary>
    public struct PMDsTicketExpectation
    {
        /// <summary>本局 MatchId（非空）。</summary>
        public readonly string MatchId;

        /// <summary>本局 DsId（非空时必须匹配）。</summary>
        public readonly string DsId;

        /// <summary>本局世代（非 0）。</summary>
        public readonly uint Epoch;

        /// <summary>本局协议摘要（非 0）。</summary>
        public readonly uint ProtocolHash;

        public PMDsTicketExpectation(string matchId, string dsId, uint epoch, uint protocolHash)
        {
            MatchId = matchId ?? string.Empty;
            DsId = dsId ?? string.Empty;
            Epoch = epoch;
            ProtocolHash = protocolHash;
        }

        /// <summary>结构是否可用（构造期失败会让「所有票据都通过」这种沉默故障变得不可能）。</summary>
        public bool IsUsable
        {
            get
            {
                return !string.IsNullOrEmpty(MatchId) && Epoch != 0u && ProtocolHash != 0u;
            }
        }

        /// <summary>诊断输出（无秘密）。</summary>
        public override string ToString()
        {
            return "expect(match=" + MatchId + " ds=" + DsId + " epoch=" + Epoch + " hash=" + ProtocolHash + ")";
        }
    }

    /// <summary>票据校验结果（只含公开字段，可安全进日志）。</summary>
    public struct PMDsTicketVerification
    {
        /// <summary>结论。</summary>
        public PMDsTicketVerdict Verdict;

        /// <summary>解析出的身份（结论非 Valid 时字段仍可能是「解析成功但不匹配」的值，不要使用）。</summary>
        public PMDsRosterIdentity Identity;

        /// <summary>票据里的 MatchId。</summary>
        public string MatchId;

        /// <summary>票据里的 DsId。</summary>
        public string DsId;

        /// <summary>票据里的世代。</summary>
        public uint Epoch;

        /// <summary>票据里的协议摘要。</summary>
        public uint ProtocolHash;

        /// <summary>签发时间（UTC 秒）。</summary>
        public long IssuedAtUnixSeconds;

        /// <summary>到期时间（UTC 秒，排他）。</summary>
        public long ExpiresAtUnixSeconds;

        /// <summary>是否通过。</summary>
        public bool IsValid { get { return Verdict == PMDsTicketVerdict.Valid; } }

        /// <summary>诊断输出（无秘密）。</summary>
        public override string ToString()
        {
            return Verdict.ToString() + " " + Identity.ToString()
                + " match=" + (MatchId ?? string.Empty)
                + " epoch=" + Epoch
                + " exp=" + ExpiresAtUnixSeconds;
        }
    }

    /// <summary>
    /// 每局控制密钥（256 位）。
    ///
    /// 用途：
    /// 1. 派生 <see cref="PMDsControlSigner"/> —— 控制帧 MAC；
    /// 2. 派生 <see cref="PMDsTicketIssuer"/> / <see cref="PMDsTicketVerifier"/> —— 玩家票据。
    ///
    /// Lobby 与 DS 必须持有**同一把**密钥：Lobby 用 <see cref="PMDsBootstrapDocument"/> 写同机引导文件，
    /// DS 读该文件。**没有任何入口接受来自对端的密钥**，因此不存在「客户自带密钥」的路径。
    /// </summary>
    public sealed class PMDsMatchKey
    {
        /// <summary>密钥字节数（256 位）。</summary>
        public const int KeyBytes = PMDsControlWire.ControlKeyBytes;

        private readonly byte[] _key;

        private PMDsMatchKey(byte[] key, int offset)
        {
            _key = new byte[KeyBytes];
            Buffer.BlockCopy(key, offset, _key, 0, KeyBytes);
            KeyFingerprint = PMDsCrypto.Fingerprint(_key);
        }

        /// <summary>
        /// 生成一把新的每局控制密钥。**调用方是 Lobby 的分配逻辑，一局一次。**
        /// 使用加密随机数（不是 `Random`、不是时间戳）。
        /// </summary>
        public static PMDsMatchKey Create()
        {
            return new PMDsMatchKey(PMDsCrypto.RandomBytes(KeyBytes), 0);
        }

        /// <summary>从已有 32 字节导入（DS 侧读引导文件、测试用）。入参必须正好 32 字节。</summary>
        public static PMDsMatchKey FromBytes(byte[] key, int offset, int count)
        {
            if (key == null)
            {
                throw new ArgumentNullException("key");
            }

            if (count != KeyBytes || offset < 0 || offset + count > key.Length)
            {
                throw new ArgumentException("控制密钥必须正好 " + KeyBytes + " 字节", "key");
            }

            return new PMDsMatchKey(key, offset);
        }

        /// <summary>整段重载。</summary>
        public static PMDsMatchKey FromBytes(byte[] key)
        {
            if (key == null)
            {
                throw new ArgumentNullException("key");
            }

            return FromBytes(key, 0, key.Length);
        }

        /// <summary>
        /// 导出密钥字节。**这是秘密材料**：只允许交给同机引导文件写入器或 DPAPI 之类的本地保护层，
        /// 不得进日志、不得经网络下发、不得拼接进命令行。
        /// </summary>
        public byte[] ExportKeyBytes()
        {
            byte[] copy = new byte[KeyBytes];
            Buffer.BlockCopy(_key, 0, copy, 0, KeyBytes);
            return copy;
        }

        /// <summary>密钥指纹（SHA-256 前 4 字节的十六进制）。用于两端核对「是不是同一把」，不可逆、不是凭据。</summary>
        public string KeyFingerprint { get; private set; }

        /// <summary>控制帧签名器。</summary>
        public PMDsControlSigner CreateControlSigner()
        {
            return new PMDsControlSigner(_key);
        }

        /// <summary>票据签发器（一局一个）。</summary>
        public PMDsTicketIssuer CreateTicketIssuer(string matchId, string dsId, uint epoch, uint protocolHash)
        {
            return new PMDsTicketIssuer(this, matchId, dsId, epoch, protocolHash);
        }

        /// <summary>票据校验器（一局一个）。</summary>
        public PMDsTicketVerifier CreateTicketVerifier(PMDsTicketExpectation expectation)
        {
            return new PMDsTicketVerifier(this, expectation);
        }

        /// <summary>**不打印密钥**。</summary>
        public override string ToString()
        {
            return "PMDsMatchKey(fp=" + KeyFingerprint + ")";
        }

        internal byte[] PeekKeyForCodec()
        {
            return _key;
        }
    }

    /// <summary>一张已解析的玩家票据。</summary>
    public sealed class PMDsTicket
    {
        /// <summary>对局 ID。</summary>
        public string MatchId { get; private set; }

        /// <summary>DS ID。</summary>
        public string DsId { get; private set; }

        /// <summary>会话世代。</summary>
        public uint Epoch { get; private set; }

        /// <summary>协议摘要。</summary>
        public uint ProtocolHash { get; private set; }

        /// <summary>票据绑定的玩家身份。**这是「所有权」的唯一合法来源。**</summary>
        public PMDsRosterIdentity Identity { get; private set; }

        /// <summary>签发时间（UTC 秒）。</summary>
        public long IssuedAtUnixSeconds { get; private set; }

        /// <summary>到期时间（UTC 秒，排他：`now &gt;= ExpiresAt` 即过期）。</summary>
        public long ExpiresAtUnixSeconds { get; private set; }

        /// <summary>随机 Nonce（16 字节）。重连重发的依据之一，由 R3-B 的端点账本使用。</summary>
        public byte[] Nonce { get; private set; }

        /// <summary>票据总字节数。</summary>
        public int ByteLength { get; private set; }

        /// <summary>票据指纹（SHA-256 前 4 字节）。用于对账与日志，**不泄漏票据内容**。</summary>
        public string Fingerprint { get; private set; }

        private byte[] _withMac;

        private PMDsTicket()
        {
        }

        /// <summary>是否已过期（排他边界）。</summary>
        public bool IsExpiredAt(long nowUnixSeconds)
        {
            return nowUnixSeconds >= ExpiresAtUnixSeconds;
        }

        /// <summary>
        /// 导出完整票据字节（含 MAC）。**属于秘密材料**：只允许写进引导文件或直接发给对应客户端，
        /// 不得进日志。若只需要发放，请直接使用 <see cref="PMDsTicketIssuer.Issue(PMDsRosterIdentity, long)"/> 的返回值。
        /// </summary>
        public byte[] ExportTicketBytes()
        {
            byte[] copy = new byte[_withMac.Length];
            Buffer.BlockCopy(_withMac, 0, copy, 0, _withMac.Length);
            return copy;
        }

        /// <summary>**不打印票据内容**：只给身份、有效期与指纹。</summary>
        public override string ToString()
        {
            return "ticket(" + Identity.ToString()
                + " match=" + MatchId
                + " epoch=" + Epoch
                + " iat=" + IssuedAtUnixSeconds
                + " exp=" + ExpiresAtUnixSeconds
                + " bytes=" + ByteLength
                + " fp=" + Fingerprint + ")";
        }

        internal static PMDsTicket FromParsed(
            string matchId, string dsId, uint epoch, uint protocolHash,
            PMDsRosterIdentity identity, long issuedAt, long expiresAt,
            byte[] nonce, byte[] withMac)
        {
            PMDsTicket ticket = new PMDsTicket();
            ticket.MatchId = matchId;
            ticket.DsId = dsId;
            ticket.Epoch = epoch;
            ticket.ProtocolHash = protocolHash;
            ticket.Identity = identity;
            ticket.IssuedAtUnixSeconds = issuedAt;
            ticket.ExpiresAtUnixSeconds = expiresAt;
            ticket.Nonce = nonce;
            ticket._withMac = withMac;
            ticket.ByteLength = withMac.Length;
            ticket.Fingerprint = PMDsCrypto.Fingerprint(withMac, 0, withMac.Length);
            return ticket;
        }
    }

    /// <summary>
    /// 票据签发器（Lobby 侧，一局一个）。
    ///
    /// 幂等语义（契约 §2「票据重试只对同一已绑定会话幂等」）：
    /// 对同一个 (Uid, PlayerId) 在票据**未过期前**重发，返回**完全相同**的票据字节
    /// （同一个 Nonce / 同一个 MAC），不刷新有效期。这样客户端重试拿到的票据等价，
    /// R3-B 的端点账本才能按票据身份做去重。过期后重签会换新 Nonce（这是一次新的签发）。
    /// </summary>
    public sealed class PMDsTicketIssuer
    {
        private readonly PMDsMatchKey _key;
        private readonly string _matchId;
        private readonly string _dsId;
        private readonly uint _epoch;
        private readonly uint _protocolHash;
        private readonly Dictionary<long, PMDsTicket> _issued = new Dictionary<long, PMDsTicket>();
        private readonly object _gate = new object();

        public PMDsTicketIssuer(PMDsMatchKey key, string matchId, string dsId, uint epoch, uint protocolHash)
        {
            if (key == null)
            {
                throw new ArgumentNullException("key");
            }

            if (string.IsNullOrEmpty(matchId))
            {
                throw new ArgumentException("MatchId 不得为空", "matchId");
            }

            if (string.IsNullOrEmpty(dsId))
            {
                throw new ArgumentException("DsId 不得为空", "dsId");
            }

            if (epoch == 0u)
            {
                throw new ArgumentException("Epoch 不得为 0", "epoch");
            }

            if (protocolHash == 0u)
            {
                throw new ArgumentException("ProtocolHash 不得为 0", "protocolHash");
            }

            PMDsControlCodec.EnsureWithinBytes(matchId, PMDsControlWire.MaxMatchIdBytes, "MatchId");
            PMDsControlCodec.EnsureWithinBytes(dsId, PMDsControlWire.MaxDsIdBytes, "DsId");

            _key = key;
            _matchId = matchId;
            _dsId = dsId;
            _epoch = epoch;
            _protocolHash = protocolHash;
        }

        /// <summary>已签发的票据数量（含已过期未清理的）。</summary>
        public int IssuedCount
        {
            get
            {
                lock (_gate)
                {
                    return _issued.Count;
                }
            }
        }

        /// <summary>按默认有效期（120 秒）签发。</summary>
        public PMDsTicket Issue(PMDsRosterIdentity identity, long nowUnixSeconds)
        {
            return Issue(identity, nowUnixSeconds, TimeSpan.FromSeconds(PMDsTicketPolicy.DefaultLifetimeSeconds));
        }

        /// <summary>签发（同一身份在未过期前重复调用返回同一张票据）。</summary>
        public PMDsTicket Issue(PMDsRosterIdentity identity, long nowUnixSeconds, TimeSpan lifetime)
        {
            ValidateIdentity(identity);

            long lifetimeSeconds = (long)Math.Round(lifetime.TotalSeconds);
            if (lifetimeSeconds <= 0 || lifetimeSeconds > PMDsTicketPolicy.MaxLifetimeSeconds)
            {
                throw new ArgumentOutOfRangeException(
                    "lifetime", "有效期必须在 1.." + PMDsTicketPolicy.MaxLifetimeSeconds + " 秒之间");
            }

            long key = IdentityKey(identity);
            lock (_gate)
            {
                PMDsTicket existing;
                if (_issued.TryGetValue(key, out existing) && !existing.IsExpiredAt(nowUnixSeconds))
                {
                    // 同一已绑定会话的重试：字节完全相同。
                    return existing;
                }

                PMDsTicket ticket = PMDsTicketCodec.Issue(
                    _key, _matchId, _dsId, _epoch, _protocolHash,
                    identity, nowUnixSeconds, nowUnixSeconds + lifetimeSeconds);
                _issued[key] = ticket;
                return ticket;
            }
        }

        /// <summary>撤销某个玩家的票据（该玩家退出/被踢）。</summary>
        public bool Revoke(int uid)
        {
            lock (_gate)
            {
                List<long> doomed = new List<long>();
                foreach (KeyValuePair<long, PMDsTicket> pair in _issued)
                {
                    if (pair.Value.Identity.Uid == uid)
                    {
                        doomed.Add(pair.Key);
                    }
                }

                for (int i = 0; i < doomed.Count; i++)
                {
                    _issued.Remove(doomed[i]);
                }

                return doomed.Count > 0;
            }
        }

        /// <summary>本局结束：撤销全部票据（密钥本身由宿主决定何时丢弃）。</summary>
        public void RevokeAll()
        {
            lock (_gate)
            {
                _issued.Clear();
            }
        }

        private static void ValidateIdentity(PMDsRosterIdentity identity)
        {
            if (identity.Uid <= 0 || identity.PlayerId <= 0)
            {
                throw new ArgumentException("Uid / PlayerId 必须 > 0：" + identity, "identity");
            }

            if (identity.TeamId < 0 || identity.HeroId < 0)
            {
                throw new ArgumentException("TeamId / HeroId 不得为负：" + identity, "identity");
            }
        }

        private static long IdentityKey(PMDsRosterIdentity identity)
        {
            return ((long)identity.Uid << 32) | (uint)identity.PlayerId;
        }
    }

    /// <summary>票据校验器（DS 侧 / Lobby 侧都可以用；一局一个）。</summary>
    public sealed class PMDsTicketVerifier
    {
        private readonly PMDsMatchKey _key;
        private readonly PMDsTicketExpectation _expectation;

        public PMDsTicketVerifier(PMDsMatchKey key, PMDsTicketExpectation expectation)
        {
            if (key == null)
            {
                throw new ArgumentNullException("key");
            }

            if (!expectation.IsUsable)
            {
                throw new ArgumentException(
                    "票据期望不完整（MatchId 非空 / Epoch 非 0 / ProtocolHash 非 0）: " + expectation, "expectation");
            }

            _key = key;
            _expectation = expectation;
        }

        /// <summary>本校验器比对的身份期望。</summary>
        public PMDsTicketExpectation Expectation { get { return _expectation; } }

        /// <summary>只校验真实性与本局字段（不校验具体持有者）。</summary>
        public PMDsTicketVerification Verify(byte[] ticket, int offset, int count, long nowUnixSeconds)
        {
            return PMDsTicketCodec.Verify(_key, _expectation, false, default(PMDsRosterIdentity),
                ticket, offset, count, nowUnixSeconds);
        }

        /// <summary>额外要求票据持有者身份与 <paramref name="expectedIdentity"/> 完全一致。</summary>
        public PMDsTicketVerification Verify(
            byte[] ticket, int offset, int count, long nowUnixSeconds, PMDsRosterIdentity expectedIdentity)
        {
            return PMDsTicketCodec.Verify(_key, _expectation, true, expectedIdentity,
                ticket, offset, count, nowUnixSeconds);
        }
    }

    /// <summary>
    /// 票据线格式编解码与校验（内部实现，对外只通过 Issuer/Verifier 使用）。
    ///
    /// 布局（显式小端，无 varint —— 避免「同一语义多种字节表示」的规范化面）：
    /// <code>
    /// 0   magic 'P','M','D','S'
    /// 4   version (1B)
    /// 5   flags   (1B, 必须 0)
    /// 6   matchIdLen (1B) + matchId (UTF-8)
    ///     dsIdLen    (1B) + dsId    (UTF-8)
    ///     epoch (4B LE) / protocolHash (4B LE)
    ///     uid (4B LE) / playerId (4B LE) / teamId (4B LE) / heroId (4B LE)
    ///     issuedAt  (8B LE, UTC 秒)
    ///     expiresAt (8B LE, UTC 秒)
    ///     nonce (16B)
    ///     mac   (32B = HMAC-SHA256(key, 前面所有字节))
    /// </code>
    /// </summary>
    internal static class PMDsTicketCodec
    {
        internal static PMDsTicket Issue(
            PMDsMatchKey key, string matchId, string dsId, uint epoch, uint protocolHash,
            PMDsRosterIdentity identity, long issuedAtUnixSeconds, long expiresAtUnixSeconds)
        {
            byte[] matchBytes = Encoding.UTF8.GetBytes(matchId);
            byte[] dsBytes = Encoding.UTF8.GetBytes(dsId);
            if (matchBytes.Length == 0 || matchBytes.Length > PMDsControlWire.MaxMatchIdBytes)
            {
                throw new ArgumentException("MatchId 字节长度非法：" + matchBytes.Length, "matchId");
            }

            if (dsBytes.Length == 0 || dsBytes.Length > PMDsControlWire.MaxDsIdBytes)
            {
                throw new ArgumentException("DsId 字节长度非法：" + dsBytes.Length, "dsId");
            }

            byte[] nonce = PMDsCrypto.RandomBytes(PMDsTicketWire.NonceBytes);
            int macStart = 6 + 1 + matchBytes.Length + 1 + dsBytes.Length + 4 + 4 + 16 + 8 + 8
                + PMDsTicketWire.NonceBytes;
            int total = macStart + PMDsTicketWire.MacBytes;
            if (total > PMDsTicketWire.MaxTicketBytes)
            {
                throw new ArgumentException("票据超出上限：" + total, "matchId");
            }

            byte[] buffer = new byte[total];
            int pos = 0;
            Buffer.BlockCopy(PMDsTicketWire.MagicBytes, 0, buffer, pos, 4);
            pos += 4;
            buffer[pos++] = (byte)PMDsTicketWire.FormatVersion;
            buffer[pos++] = 0;
            buffer[pos++] = (byte)matchBytes.Length;
            Buffer.BlockCopy(matchBytes, 0, buffer, pos, matchBytes.Length);
            pos += matchBytes.Length;
            buffer[pos++] = (byte)dsBytes.Length;
            Buffer.BlockCopy(dsBytes, 0, buffer, pos, dsBytes.Length);
            pos += dsBytes.Length;
            WriteU32(buffer, ref pos, epoch);
            WriteU32(buffer, ref pos, protocolHash);
            WriteI32(buffer, ref pos, identity.Uid);
            WriteI32(buffer, ref pos, identity.PlayerId);
            WriteI32(buffer, ref pos, identity.TeamId);
            WriteI32(buffer, ref pos, identity.HeroId);
            WriteI64(buffer, ref pos, issuedAtUnixSeconds);
            WriteI64(buffer, ref pos, expiresAtUnixSeconds);
            Buffer.BlockCopy(nonce, 0, buffer, pos, nonce.Length);
            pos += nonce.Length;
            if (pos != macStart)
            {
                throw new InvalidOperationException("票据布局与 macStart 不一致（实现缺陷）");
            }

            byte[] mac = PMDsCrypto.HmacSha256(key.PeekKeyForCodec(), buffer, 0, macStart);
            Buffer.BlockCopy(mac, 0, buffer, macStart, PMDsTicketWire.MacBytes);

            return PMDsTicket.FromParsed(matchId, dsId, epoch, protocolHash, identity,
                issuedAtUnixSeconds, expiresAtUnixSeconds, nonce, buffer);
        }

        internal static PMDsTicketVerification Verify(
            PMDsMatchKey key, PMDsTicketExpectation expectation, bool checkIdentity,
            PMDsRosterIdentity expectedIdentity, byte[] ticket, int offset, int count, long nowUnixSeconds)
        {
            PMDsTicketVerification result = new PMDsTicketVerification();
            result.Verdict = PMDsTicketVerdict.Malformed;

            if (ticket == null || offset < 0 || count < 0 || offset + count > ticket.Length)
            {
                return result;
            }

            if (count < 6 + 2 + 24 + 16 + 16 + PMDsTicketWire.MacBytes || count > PMDsTicketWire.MaxTicketBytes)
            {
                return result;
            }

            if (ticket[offset] != PMDsTicketWire.MagicBytes[0]
                || ticket[offset + 1] != PMDsTicketWire.MagicBytes[1]
                || ticket[offset + 2] != PMDsTicketWire.MagicBytes[2]
                || ticket[offset + 3] != PMDsTicketWire.MagicBytes[3])
            {
                return result;
            }

            if (ticket[offset + 4] != (byte)PMDsTicketWire.FormatVersion)
            {
                result.Verdict = PMDsTicketVerdict.BadVersion;
                return result;
            }

            if (ticket[offset + 5] != 0)
            {
                return result;
            }

            int pos = offset + 6;
            int limit = offset + count;
            int matchLen = ticket[pos++];
            if (matchLen == 0 || matchLen > PMDsControlWire.MaxMatchIdBytes || pos + matchLen > limit)
            {
                return result;
            }

            string matchId = Encoding.UTF8.GetString(ticket, pos, matchLen);
            pos += matchLen;

            int dsLen = ticket[pos++];
            if (dsLen == 0 || dsLen > PMDsControlWire.MaxDsIdBytes || pos + dsLen > limit)
            {
                return result;
            }

            string dsId = Encoding.UTF8.GetString(ticket, pos, dsLen);
            pos += dsLen;

            if (pos + 4 + 4 + 16 + 8 + 8 + PMDsTicketWire.NonceBytes + PMDsTicketWire.MacBytes > limit)
            {
                return result;
            }

            uint epoch = ReadU32(ticket, ref pos);
            uint protocolHash = ReadU32(ticket, ref pos);
            PMDsRosterIdentity identity = default(PMDsRosterIdentity);
            identity.Uid = ReadI32(ticket, ref pos);
            identity.PlayerId = ReadI32(ticket, ref pos);
            identity.TeamId = ReadI32(ticket, ref pos);
            identity.HeroId = ReadI32(ticket, ref pos);
            long issuedAt = ReadI64(ticket, ref pos);
            long expiresAt = ReadI64(ticket, ref pos);
            pos += PMDsTicketWire.NonceBytes;

            int macStart = limit - PMDsTicketWire.MacBytes;
            if (pos != macStart)
            {
                // 尾部长度与布局不符（多塞了字节或少了字节）→ 结构非法。
                return result;
            }

            result.MatchId = matchId;
            result.DsId = dsId;
            result.Epoch = epoch;
            result.ProtocolHash = protocolHash;
            result.Identity = identity;
            result.IssuedAtUnixSeconds = issuedAt;
            result.ExpiresAtUnixSeconds = expiresAt;

            byte[] expectedMac = PMDsCrypto.HmacSha256(key.PeekKeyForCodec(), ticket, offset, count - PMDsTicketWire.MacBytes);
            if (!PMDsCrypto.FixedTimeEquals(expectedMac, 0, ticket, macStart, PMDsTicketWire.MacBytes))
            {
                result.Verdict = PMDsTicketVerdict.MacMismatch;
                return result;
            }

            // --- 以下检查全部在 MAC 通过之后（不可信输入不得影响策略判定顺序） ---

            if (expiresAt <= issuedAt || expiresAt - issuedAt > PMDsTicketPolicy.MaxLifetimeSeconds)
            {
                result.Verdict = PMDsTicketVerdict.LifetimePolicy;
                return result;
            }

            if (issuedAt > nowUnixSeconds + PMDsTicketPolicy.MaxClockSkewSeconds)
            {
                result.Verdict = PMDsTicketVerdict.NotYetValid;
                return result;
            }

            if (!string.Equals(matchId, expectation.MatchId, StringComparison.Ordinal))
            {
                result.Verdict = PMDsTicketVerdict.WrongMatch;
                return result;
            }

            if (epoch != expectation.Epoch)
            {
                result.Verdict = PMDsTicketVerdict.WrongEpoch;
                return result;
            }

            if (protocolHash != expectation.ProtocolHash)
            {
                result.Verdict = PMDsTicketVerdict.WrongProtocolHash;
                return result;
            }

            if (!string.IsNullOrEmpty(expectation.DsId) && !string.Equals(dsId, expectation.DsId, StringComparison.Ordinal))
            {
                result.Verdict = PMDsTicketVerdict.WrongDsId;
                return result;
            }

            if (checkIdentity && !identity.Equals(expectedIdentity))
            {
                result.Verdict = PMDsTicketVerdict.NotInRoster;
                return result;
            }

            if (nowUnixSeconds >= expiresAt)
            {
                result.Verdict = PMDsTicketVerdict.Expired;
                return result;
            }

            result.Verdict = PMDsTicketVerdict.Valid;
            return result;
        }

        private static void WriteU32(byte[] buffer, ref int pos, uint value)
        {
            buffer[pos++] = (byte)(value & 0xFF);
            buffer[pos++] = (byte)((value >> 8) & 0xFF);
            buffer[pos++] = (byte)((value >> 16) & 0xFF);
            buffer[pos++] = (byte)((value >> 24) & 0xFF);
        }

        private static void WriteI32(byte[] buffer, ref int pos, int value)
        {
            WriteU32(buffer, ref pos, unchecked((uint)value));
        }

        private static void WriteI64(byte[] buffer, ref int pos, long value)
        {
            ulong raw = unchecked((ulong)value);
            for (int i = 0; i < 8; i++)
            {
                buffer[pos++] = (byte)((raw >> (i * 8)) & 0xFF);
            }
        }

        private static uint ReadU32(byte[] buffer, ref int pos)
        {
            uint value = (uint)(buffer[pos] | (buffer[pos + 1] << 8)
                | (buffer[pos + 2] << 16) | (buffer[pos + 3] << 24));
            pos += 4;
            return value;
        }

        private static int ReadI32(byte[] buffer, ref int pos)
        {
            return unchecked((int)ReadU32(buffer, ref pos));
        }

        private static long ReadI64(byte[] buffer, ref int pos)
        {
            ulong value = 0UL;
            for (int i = 0; i < 8; i++)
            {
                value |= (ulong)buffer[pos + i] << (i * 8);
            }

            pos += 8;
            return unchecked((long)value);
        }
    }

    /// <summary>
    /// 同机引导文件（A1 决定的 Bootstrap 交付方式）。
    ///
    /// **A1 决策（必须在报告里讲清楚，R3-B 照此接线）**：
    /// 名册与票据**不经控制通道下发**，而是由 Lobby 写成同机引导文件、只把**文件路径**作为
    /// 命令行参数交给 DS。理由：
    /// 1. 控制通道在 R3-A 阶段只有 loopback、且请求方尚未被认证到「就是本机那一局的 DS」；
    ///    把票据（bearer 凭据）交给一个未认证端点等于把入场券发给了任意本机进程。
    /// 2. 契约 §2 明确「DS引导文件只传文件路径给进程…受本机账户边界保护」。
    ///
    /// 因此本类只做**字节层编解码**（纯函数，不碰文件系统）：
    /// 原子发布（临时文件 + rename）与文件权限由 R3-B 的宿主实现。
    ///
    /// 布局：`magic(8) "PMDSBOOT" | version(1) | payloadLen(4 LE) | payload | key(32)`，
    /// 其中 payload 就是**不含 MAC 的规范 Bootstrap 信封**（与 <see cref="PMDsControlCodec.EncodePrefix"/> 同源），
    /// 因此 DS 侧可以直接 <see cref="PMDsControlCodec.Decode"/> 后逐张校验票据。
    ///
    /// 注意：引导文件**不含对文件自身的 MAC**。它受本机账户边界保护；
    /// 「文件里的密钥」同时是验签材料，用它给自己签名不构成认证。这一点是有意的，不是遗漏。
    /// </summary>
    public static class PMDsBootstrapDocument
    {
        /// <summary>引导文件格式版本。</summary>
        public const int FormatVersion = 1;

        /// <summary>魔数 "PMDSBOOT"。</summary>
        internal static readonly byte[] MagicBytes = new byte[]
        {
            (byte)'P', (byte)'M', (byte)'D', (byte)'S', (byte)'B', (byte)'O', (byte)'O', (byte)'T',
        };

        /// <summary>固定头部字节数（magic + version）。</summary>
        public const int HeaderBytes = 9;

        /// <summary>文件总字节上限（信封上限 + 密钥 + 头）。</summary>
        public const int MaxDocumentBytes = HeaderBytes + 4 + PMDsControlWire.MaxFramePayloadBytes + PMDsMatchKey.KeyBytes;

        /// <summary>把「密钥 + 无 MAC 的 Bootstrap 信封」编码为引导文件字节。</summary>
        public static byte[] Encode(PMDsMatchKey key, PMDsControlMessage bootstrap)
        {
            if (key == null)
            {
                throw new ArgumentNullException("key");
            }

            if (bootstrap == null)
            {
                throw new ArgumentNullException("bootstrap");
            }

            if (bootstrap.Type != PMDsControlMessageType.Bootstrap)
            {
                throw new PMDsControlProtocolException("引导文件只能承载 Bootstrap，收到 " + bootstrap.Type);
            }

            byte[] payload = PMDsControlCodec.EncodePrefix(bootstrap);
            byte[] keyBytes = key.ExportKeyBytes();
            byte[] document = new byte[HeaderBytes + 4 + payload.Length + keyBytes.Length];

            int pos = 0;
            Buffer.BlockCopy(MagicBytes, 0, document, pos, MagicBytes.Length);
            pos += MagicBytes.Length;
            document[pos++] = (byte)FormatVersion;
            WriteU32(document, ref pos, (uint)payload.Length);
            Buffer.BlockCopy(payload, 0, document, pos, payload.Length);
            pos += payload.Length;
            Buffer.BlockCopy(keyBytes, 0, document, pos, keyBytes.Length);
            return document;
        }

        /// <summary>解析引导文件。失败时 <paramref name="error"/> 给出**不含秘密**的原因。</summary>
        public static bool TryDecode(
            byte[] data, int offset, int count, out PMDsBootstrappedMatch match, out string error)
        {
            match = null;
            error = null;

            if (data == null)
            {
                error = "引导文件字节为 null";
                return false;
            }

            if (offset < 0 || count < 0 || offset + count > data.Length)
            {
                error = "引导文件解析范围越界";
                return false;
            }

            if (count < HeaderBytes + 4 + PMDsMatchKey.KeyBytes + 1)
            {
                error = "引导文件过短：" + count;
                return false;
            }

            if (count > MaxDocumentBytes)
            {
                error = "引导文件过长：" + count;
                return false;
            }

            for (int i = 0; i < MagicBytes.Length; i++)
            {
                if (data[offset + i] != MagicBytes[i])
                {
                    error = "引导文件魔数不匹配（不是 PMDSBOOT 文件）";
                    return false;
                }
            }

            if (data[offset + 8] != (byte)FormatVersion)
            {
                error = "引导文件格式版本不受支持：" + data[offset + 8];
                return false;
            }

            int pos = offset + HeaderBytes;
            uint payloadLength = (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24));
            pos += 4;

            if (payloadLength == 0u || payloadLength > PMDsControlWire.MaxFramePayloadBytes)
            {
                error = "引导文件声明的载荷长度非法：" + payloadLength;
                return false;
            }

            int expectedTotal = HeaderBytes + 4 + (int)payloadLength + PMDsMatchKey.KeyBytes;
            if (count != expectedTotal)
            {
                error = "引导文件长度与声明不一致：实际 " + count + "，声明 " + expectedTotal;
                return false;
            }

            // 密钥在末尾：先切出来，再用它校验票据（信封本身不签名）。
            byte[] keyBytes = new byte[PMDsMatchKey.KeyBytes];
            Buffer.BlockCopy(data, offset + HeaderBytes + 4 + (int)payloadLength, keyBytes, 0, PMDsMatchKey.KeyBytes);

            PMDsControlMessage message;
            try
            {
                message = PMDsControlCodec.Decode(data, pos, (int)payloadLength);
            }
            catch (PMDsControlProtocolException ex)
            {
                error = "引导文件载荷非法：" + ex.Message;
                return false;
            }

            if (message.Type != PMDsControlMessageType.Bootstrap)
            {
                error = "引导文件载荷不是 Bootstrap：" + message.Type;
                return false;
            }

            PMDsBootstrapBody body = message.AsBootstrap;
            if (body == null)
            {
                error = "引导文件载荷缺少 Bootstrap body";
                return false;
            }

            PMDsBootstrappedMatch parsed = new PMDsBootstrappedMatch();
            parsed.Key = PMDsMatchKey.FromBytes(keyBytes, 0, keyBytes.Length);
            parsed.Bootstrap = message;
            parsed.MatchId = message.MatchId;
            parsed.DsId = message.DsId;
            parsed.Epoch = message.Epoch;
            parsed.ProtocolHash = message.ProtocolHash;
            parsed.CollisionDigest = body.CollisionDigest;
            parsed.PlayerCount = body.Players.Length;
            match = parsed;
            return true;
        }

        /// <summary>整段重载。</summary>
        public static bool TryDecode(byte[] data, out PMDsBootstrappedMatch match, out string error)
        {
            if (data == null)
            {
                match = null;
                error = "引导文件字节为 null";
                return false;
            }

            return TryDecode(data, 0, data.Length, out match, out error);
        }

        private static void WriteU32(byte[] buffer, ref int pos, uint value)
        {
            buffer[pos++] = (byte)(value & 0xFF);
            buffer[pos++] = (byte)((value >> 8) & 0xFF);
            buffer[pos++] = (byte)((value >> 16) & 0xFF);
            buffer[pos++] = (byte)((value >> 24) & 0xFF);
        }
    }

    /// <summary>解析出来的引导内容（**含密钥，不得整体进日志**）。</summary>
    public sealed class PMDsBootstrappedMatch
    {
        /// <summary>本局控制密钥。</summary>
        public PMDsMatchKey Key;

        /// <summary>无 MAC 的 Bootstrap 信封（名册 + 票据）。</summary>
        public PMDsControlMessage Bootstrap;

        /// <summary>对局 ID。</summary>
        public string MatchId;

        /// <summary>DS ID。</summary>
        public string DsId;

        /// <summary>会话世代。</summary>
        public uint Epoch;

        /// <summary>协议摘要。</summary>
        public uint ProtocolHash;

        /// <summary>碰撞配置摘要。</summary>
        public uint CollisionDigest;

        /// <summary>名册人数。</summary>
        public int PlayerCount;

        /// <summary>**不打印密钥与票据**。</summary>
        public override string ToString()
        {
            return "boot(match=" + MatchId + " ds=" + DsId + " epoch=" + Epoch
                + " hash=" + ProtocolHash + " digest=" + CollisionDigest
                + " players=" + PlayerCount + " keyfp=" + (Key == null ? "?" : Key.KeyFingerprint) + ")";
        }
    }
}
