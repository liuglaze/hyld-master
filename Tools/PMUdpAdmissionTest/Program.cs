using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using PMNet;
using PMNet.Control;
using PMNet.Session;
using PMNet.Transport;

namespace PMUdpAdmissionTest
{
    /// <summary>
    /// R3-B2 门禁：UDP 入局接纳（票据端点绑定 / 重试 / 过期 / 第二端点 / 未激活不 ACK）。
    ///
    /// 事实来源：`Docs/plans/net-r3-control-contract.md` §2/§4/§5/§7.1/§7.2、
    /// `Docs/plans/_r3b_auth_survey.md`（只读调研，采纳入口证据；**不采纳**其 512B 错误估算——
    /// 契约 §7.2 已冻结握手帧上限为 1200 字节）、`Docs/plans/_r3a_session_report.md`。
    ///
    /// 全部走**真实 localhost UDP + 生产票据 + 生产 Bridge/Transport**；只有描述符是手工的。
    /// </summary>
    internal static class Program
    {
        // ── 手工描述符（不依赖生成器）─────────────────────────────────────
        private const uint TestClassId = 0x7B11u;
        private const uint TestProtocolHash = 0x51D3B001u;
        private const uint TestCollisionDigest = 0x52334201u;

        private const ushort RpcMark = 201;          // Server，可靠：客户端请求 → 权威执行
        private const ushort LayoutMark = 0x0A11;

        private const ushort PropertyIdHealth = 2001;

        private const string TestMatchId = "match-r3b-net";
        private const string TestDsId = "ds-r3b-net";
        private const uint TestEpoch = 77001u;
        private const int TestHeroId = 1;

        private const int UidA = 101;
        private const int UidB = 102;
        private const int UidC = 103;

        private static int _passed;
        private static readonly List<string> _failures = new List<string>();

        // 受控时钟：Pump 的两个时间参数都由测试推进（不用真实时钟做判定）。
        private static long _nowMs = 100000L;
        private static long _nowUnix = 1700000000L;

        private static int Main()
        {
            Console.WriteLine("=== R3-B2：UDP 入局接纳（票据/端点账本/握手）契约验证 ===");
            Console.WriteLine();

            SetupRegistry();

            Section("A. 入局通知 codec（PMDS1:）：严格解码 / 范围 / 边界", TestEntryOfferCodec);
            Section("B. 握手帧 codec：帧上限 / 严格尾部 / 填充与应答不大于请求", TestHandshakeCodec);
            Section("C. 端点消费账本：幂等 / 第二端点 / 墓碑 / uid / 容量", TestEndpointLedger);
            Section("D. 真实 localhost UDP：两玩家入局 + 双向数据（Create/RPC/ACK）", TestRealUdpTwoPlayers);
            Section("E. 未认证 / 未激活闸门：无回包、不进 Transport、不 ACK", TestAdmissionGates);
            Section("F. 票据负例：错 MAC / 过期 / 错局 / 错摘要 / 第二端点", TestTicketNegatives);
            Section("G. 断开墓碑 / 空闲超时 / 票据过期不影响已激活会话", TestLifecycleAndExpiry);
            Section("H. 最大报文与帧边界", TestBoundaries);
            Section("I. 主集成复核：墓碑淘汰 fail-closed / 摘要所有权 / 幂等 / 解析健壮性", TestReviewFindings);
            Section("J. 端点 IsDisposed 只读标志：生命周期前后 / 幂等 Dispose / 语义不变", TestEndpointDisposedFlag);

            Console.WriteLine();
            if (_failures.Count == 0)
            {
                Console.WriteLine("  全部通过：" + _passed + " 项检查，0 项失败");
                return 0;
            }

            Console.WriteLine("  " + _passed + " 项通过，" + _failures.Count + " 项失败：");
            for (int i = 0; i < _failures.Count; i++)
            {
                Console.WriteLine("    - " + _failures[i]);
            }

            return 1;
        }

        // =================================================================================
        //  断言
        // =================================================================================

        private static void Section(string name, Action body)
        {
            Console.WriteLine("── " + name);
            int passedBefore = _passed;
            int failedBefore = _failures.Count;
            body();
            Console.WriteLine("   [" + name.Substring(0, name.IndexOf('.')) + "] " + (_passed - passedBefore)
                              + " 项通过 / " + (_failures.Count - failedBefore) + " 项失败");
            Console.WriteLine();
        }

        private static void Check(bool ok, string label)
        {
            if (ok) { _passed++; } else { _failures.Add(label); }
            Console.WriteLine((ok ? "    OK   " : "    FAIL ") + label);
        }

        private static void CheckEq(long actual, long expected, string label)
        {
            Check(actual == expected, label + "（实际 " + actual + "，期望 " + expected + "）");
        }

        private static void CheckGt(long actual, long bound, string label)
        {
            Check(actual > bound, label + "（实际 " + actual + "，要求 > " + bound + "）");
        }

        private static void CheckStrEq(string actual, string expected, string label)
        {
            Check(actual == expected, label + "（实际 " + (actual ?? "<null>") + "，期望 " + (expected ?? "<null>") + "）");
        }

        // =================================================================================
        //  描述符 / 业务替身
        // =================================================================================

        private sealed class NetActor : PMNetObject
        {
            public int Health;
            public int MarkCount;
            public int LastMark;
            public int CreateCount;
            public int CreateHealthSeen = int.MinValue;

            public void SetHealth(int value)
            {
                Health = value;
                MarkPropertyDirty(0);
            }

            protected internal override void OnReplicatedCreate()
            {
                CreateCount++;
                CreateHealthSeen = Health;
            }
        }

        private static PMNetObject CreateActorInstance()
        {
            return new NetActor();
        }

        private static void WriteHealth(PMNetObject target, PMNetWriter writer)
        {
            writer.WriteInt32(((NetActor)target).Health);
        }

        private static void ReadHealth(PMNetObject target, PMNetReader reader)
        {
            ((NetActor)target).Health = reader.ReadInt32();
        }

        private static void InvokeMark(PMNetObject target, PMNetReader reader)
        {
            NetActor self = (NetActor)target;
            int value = reader.ReadInt32();
            if (!reader.IsAtEnd)
            {
                throw new FormatException("Mark 载荷存在尾随字节");
            }

            self.LastMark = value;
            self.MarkCount++;
            self.SetHealth(value);   // 顺带把值写进复制属性，用来验证回程收敛
        }

        private static PMNetRpcEntry MakeRpc(ushort rpcId, PMRpcKind direction, bool reliable, ushort layout,
                                             string methodName, PMRpcInvoker invoker)
        {
            PMNetRpcEntry entry = new PMNetRpcEntry();
            PMRpcDescriptor descriptor = new PMRpcDescriptor();
            descriptor.RpcId = rpcId;
            descriptor.Direction = direction;
            descriptor.IsReliable = reliable;
            descriptor.Validator = PMRpcValidator.None;
            descriptor.ParamLayoutId = layout;
            descriptor.MethodName = methodName;
            entry.Descriptor = descriptor;
            entry.OwningClassId = TestClassId;
            entry.Invoke = invoker;
            return entry;
        }

        private static PMPropertyDescriptor MakeProperty(int slot, ushort propertyId, string memberName,
                                                        PMPropertyWriter writer, PMPropertyReader reader)
        {
            PMPropertyDescriptor prop = new PMPropertyDescriptor();
            prop.PropertyId = propertyId;
            prop.Condition = PMCond.None;
            prop.MaskOffset = (ushort)slot;
            prop.MaskBitCount = 1;
            prop.QuantizerId = 0;
            prop.OnRepMethodId = 0;
            prop.Writer = writer;
            prop.Reader = reader;
            prop.PushBased = true;
            prop.MemberName = memberName;
            prop.SetterName = "PMNet_Set_" + memberName;
            return prop;
        }

        private static void SetupRegistry()
        {
            PMNetRegistry.Reset();

            PMReplicationDescriptor rep = new PMReplicationDescriptor();
            rep.ClassId = TestClassId;
            rep.TypeName = "NetActor";
            rep.ProtocolHash = TestProtocolHash;
            rep.HasConditionalMask = false;
            rep.ChangeMaskBitCount = 1;
            rep.Properties = new PMPropertyDescriptor[1];
            rep.Properties[0] = MakeProperty(0, PropertyIdHealth, "Health", WriteHealth, ReadHealth);

            PMNetClassEntry cls = new PMNetClassEntry();
            cls.ClassId = TestClassId;
            cls.TypeName = "NetActor";
            cls.Rep = rep;
            cls.Factory = CreateActorInstance;
            cls.Rpcs = new PMNetRpcEntry[1];
            cls.Rpcs[0] = MakeRpc(RpcMark, PMRpcKind.Server, true, LayoutMark, "Mark", InvokeMark);

            PMNetRegistry.RegisterClass(cls);
            PMNetRegistry.Seal(TestProtocolHash);

            PMNetRpcReceive.ResetStats();
            PMNetRpcReceive.Warn = null;
            PMNetRpcReceive.Observer = null;
        }

        // =================================================================================
        //  世界 / 票据 / 引导文件 / offer
        // =================================================================================

        private sealed class WorldFixture
        {
            public PMNetWorld World;
            public PMNetSessionBridge Bridge;
            public readonly List<string> Warnings = new List<string>();
            public readonly List<PMTransportConnection> ConnectedEvents = new List<PMTransportConnection>();
            public readonly List<string> FailedEvents = new List<string>();

            public bool HasWarning(string fragment)
            {
                for (int i = 0; i < Warnings.Count; i++)
                {
                    if (Warnings[i] != null && Warnings[i].IndexOf(fragment, StringComparison.Ordinal) >= 0)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private static WorldFixture CreateWorld(bool isServer)
        {
            WorldFixture fixture = new WorldFixture();
            fixture.World = new PMNetWorld(new PMSession(TestEpoch, isServer));
            fixture.World.Warn = delegate(string m) { fixture.Warnings.Add(m); };
            fixture.World.RegisterClass(TestClassId, CreateActorInstance);
            fixture.Bridge = new PMNetSessionBridge(fixture.World);
            fixture.Bridge.Warn = delegate(string m) { fixture.Warnings.Add(m); };
            return fixture;
        }

        private static PMDsRosterIdentity Identity(int uid, int playerId)
        {
            PMDsRosterIdentity identity = new PMDsRosterIdentity();
            identity.Uid = uid;
            identity.PlayerId = playerId;
            identity.TeamId = (playerId - 1) % 2;
            identity.HeroId = TestHeroId;
            return identity;
        }

        private sealed class TicketAuthority
        {
            public PMDsMatchKey Key;
            public PMDsTicketIssuer Issuer;
            public long IssuedAtUnix;
        }

        private static TicketAuthority CreateAuthority(string matchId, uint protocolHash, long issuedAtUnix)
        {
            TicketAuthority authority = new TicketAuthority();
            authority.Key = PMDsMatchKey.Create();
            authority.Issuer = authority.Key.CreateTicketIssuer(matchId, TestDsId, TestEpoch, protocolHash);
            authority.IssuedAtUnix = issuedAtUnix;
            return authority;
        }

        private static byte[] IssueTicket(TicketAuthority authority, PMDsRosterIdentity identity, int lifetimeSeconds)
        {
            PMDsTicket ticket = authority.Issuer.Issue(identity, authority.IssuedAtUnix, TimeSpan.FromSeconds(lifetimeSeconds));
            return ticket.ExportTicketBytes();
        }

        private static PMDsBootstrappedMatch BuildBoot(TicketAuthority authority, PMDsBootstrapPlayer[] roster)
        {
            PMDsBootstrapBody body = new PMDsBootstrapBody();
            body.CollisionDigest = TestCollisionDigest;
            body.Players = roster;

            PMDsControlMessage message = PMDsControlMessage.Create(PMDsControlMessageType.Bootstrap,
                TestMatchId, TestDsId, TestEpoch, TestProtocolHash, 0UL, body);

            byte[] document = PMDsBootstrapDocument.Encode(authority.Key, message);

            PMDsBootstrappedMatch boot;
            string error;
            if (!PMDsBootstrapDocument.TryDecode(document, out boot, out error))
            {
                throw new InvalidOperationException("引导文件解码失败：" + error);
            }

            return boot;
        }

        private static PMDsEntryOffer CreateOffer(byte[] ticket, PMDsRosterIdentity identity, string host, int port)
        {
            PMDsEntryOffer offer = new PMDsEntryOffer();
            offer.MatchId = TestMatchId;
            offer.DsId = TestDsId;
            offer.Host = host;
            offer.Epoch = TestEpoch;
            offer.ProtocolHash = TestProtocolHash;
            offer.CollisionDigest = TestCollisionDigest;
            offer.Port = port;
            offer.Identity = identity;
            offer.Ticket = ticket;
            return offer;
        }

        private static PMTransportConfig TestTransportConfig()
        {
            PMTransportConfig config = new PMTransportConfig();
            config.IdleTimeoutMs = 0L;   // 本门禁用受控时钟推进，不做墙钟空闲判定
            return config;
        }

        // =================================================================================
        //  驱动 / 时间
        // =================================================================================

        private static void PumpOnce(params PMUdpSessionEndpoint[] endpoints)
        {
            for (int i = 0; i < endpoints.Length; i++)
            {
                endpoints[i].Pump(_nowMs, _nowUnix);
            }
        }

        private static bool PumpUntil(Func<bool> condition, int maxRounds, params PMUdpSessionEndpoint[] endpoints)
        {
            for (int round = 0; round < maxRounds; round++)
            {
                PumpOnce(endpoints);
                if (condition != null && condition())
                {
                    return true;
                }

                Thread.Sleep(1);
                _nowMs += 20L;
            }

            return condition == null || condition();
        }

        private static void PumpFor(int rounds, params PMUdpSessionEndpoint[] endpoints)
        {
            for (int round = 0; round < rounds; round++)
            {
                PumpOnce(endpoints);
                Thread.Sleep(1);
                _nowMs += 20L;
            }
        }

        // =================================================================================
        //  原始 UDP 对端（用于「注入一条握手帧」这类负例）
        // =================================================================================

        private sealed class RawPeer : IDisposable
        {
            public readonly Socket Socket;
            private readonly IPEndPoint _target;

            public RawPeer(int targetPort)
            {
                Socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                Socket.Blocking = false;
                Socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                _target = new IPEndPoint(IPAddress.Loopback, targetPort);
            }

            public int LocalPort
            {
                get { return ((IPEndPoint)Socket.LocalEndPoint).Port; }
            }

            public void Send(byte[] frame)
            {
                Socket.SendTo(frame, 0, frame.Length, SocketFlags.None, _target);
            }

            public void SendRaw(byte[] frame, int offset, int count)
            {
                Socket.SendTo(frame, offset, count, SocketFlags.None, _target);
            }

            public void SendTo(byte[] frame, int port)
            {
                Socket.SendTo(frame, 0, frame.Length, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, port));
            }

            public bool TryReceive(int timeoutMs, out byte[] frame)
            {
                frame = null;

                if (!Socket.Poll(timeoutMs * 1000, SelectMode.SelectRead))
                {
                    return false;
                }

                byte[] buffer = new byte[2048];
                EndPoint any = new IPEndPoint(IPAddress.Any, 0);
                int received;

                try
                {
                    received = Socket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref any);
                }
                catch (SocketException)
                {
                    return false;
                }

                if (received <= 0)
                {
                    return false;
                }

                frame = new byte[received];
                Buffer.BlockCopy(buffer, 0, frame, 0, received);
                return true;
            }

            public void Dispose()
            {
                try
                {
                    Socket.Close();
                }
                catch (Exception)
                {
                    // 测试清理：忽略。
                }
            }
        }

        private static byte[] BuildRawClientHello(byte[] ticket, uint protocolHash, uint epoch, int padding)
        {
            PMNetWriter writer = new PMNetWriter(600);
            byte[] nonce = PMDsCrypto.RandomBytes(PMHandshakeCodec.NonceBytes);
            PMHandshakeCodec.WriteClientHello(writer, ticket, 0, ticket.Length, nonce, protocolHash, epoch, padding);
            return writer.ToArray();
        }

        /// <summary>
        /// 手写一条 ClientHello，**绕过 codec 的填充上限检查**（只用于验证「读侧有界」）。
        /// 生产代码永远不会生成这种帧；这里造出来是为了证明读侧不会被「总长在上限内」骗过。
        /// </summary>
        private static byte[] BuildUncappedClientHello(byte[] ticket, uint protocolHash, uint epoch, int padding)
        {
            PMNetWriter writer = new PMNetWriter(1400);
            byte[] nonce = PMDsCrypto.RandomBytes(PMHandshakeCodec.NonceBytes);
            writer.WriteRawBytes(new byte[] { (byte)'P', (byte)'M', (byte)'H', (byte)'S' }, 0, 4);
            writer.WriteRawBytes(new byte[] { (byte)PMHandshakeCodec.FrameVersion }, 0, 1);
            writer.WriteRawBytes(new byte[] { (byte)PMHandshakeKind.ClientHello }, 0, 1);
            writer.WriteInt32(ticket.Length);
            writer.WriteRawBytes(ticket, 0, ticket.Length);
            writer.WriteRawBytes(nonce, 0, nonce.Length);
            writer.WriteUInt32(protocolHash);
            writer.WriteUInt32(epoch);
            writer.WriteInt32(padding);
            if (padding > 0)
            {
                writer.WriteRawBytes(new byte[padding], 0, padding);
            }

            return writer.ToArray();
        }

        /// <summary>手工拼一个合法的传输层数据报（只有固定头，0 条消息）。</summary>
        private static byte[] CraftTransportDatagram(uint epoch, ushort packetId)
        {
            byte[] buffer = new byte[18];
            int p = 0;
            buffer[p++] = 1;                       // 协议版本
            WriteU32(buffer, ref p, epoch);
            WriteU16(buffer, ref p, packetId);
            WriteU16(buffer, ref p, 0);            // ackId
            for (int i = 0; i < 8; i++) { buffer[p++] = 0; }
            buffer[p++] = 0;                       // messageCount = 0（只有 ack 回程）
            return buffer;
        }

        /// <summary>
        /// 拼一个**携带消息**的传输层数据报（控制 Ping，messageCount = 1，共 21 字节）。
        ///
        /// 为什么需要它：本轮 ackloop 修复后，「header-only 纯 ack 数据报」按设计**不再**触发
        /// 回程 ack（否则两端会每帧各回一个 18 字节纯 ack、互相追赶）。因此「已激活连接真的会 ack」
        /// 这条正例对照不能用纯 ack 包来做，否则会变成假绿/假红。
        ///
        /// 用控制 Ping 的理由：它在既有线格式里就是 messageCount=1 的消息帧，
        /// 且由传输层自己消费（`ReadControl`）——**不派发到应用层**，不会污染应用协议口径，
        /// 也不会把一条健康连接打成协议错误。
        /// </summary>
        private static byte[] CraftTransportPing(uint epoch, ushort packetId)
        {
            byte[] buffer = new byte[21];
            int p = 0;
            buffer[p++] = 1;                       // 协议版本
            WriteU32(buffer, ref p, epoch);
            WriteU16(buffer, ref p, packetId);
            WriteU16(buffer, ref p, 0);            // ackId
            for (int i = 0; i < 8; i++) { buffer[p++] = 0; }
            buffer[p++] = 1;                       // messageCount = 1
            buffer[p++] = 4;                       // FlagControl
            buffer[p++] = 0;                       // stream 占位
            buffer[p++] = 1;                       // ControlPing
            return buffer;
        }

        private static void WriteU16(byte[] b, ref int p, ushort v)
        {
            b[p++] = (byte)(v & 0xFF);
            b[p++] = (byte)((v >> 8) & 0xFF);
        }

        private static void WriteU32(byte[] b, ref int p, uint v)
        {
            b[p++] = (byte)(v & 0xFF);
            b[p++] = (byte)((v >> 8) & 0xFF);
            b[p++] = (byte)((v >> 16) & 0xFF);
            b[p++] = (byte)((v >> 24) & 0xFF);
        }

        /// <summary>捕获出站数据报的链路（用于「未激活不 ACK」这类断言）。</summary>
        private sealed class CaptureLink : IPMTransportLink
        {
            public readonly List<byte[]> Datagrams = new List<byte[]>();

            public bool Send(byte[] buffer, int offset, int count)
            {
                byte[] copy = new byte[count];
                Buffer.BlockCopy(buffer, offset, copy, 0, count);
                Datagrams.Add(copy);
                return true;
            }

            public string Describe()
            {
                return "capture";
            }
        }

        private static PMTransportConnection CreateServerConnection(WorldFixture server, int connectionId, int uid,
                                                                   CaptureLink link, out string error)
        {
            PMSessionIdentity identity = new PMSessionIdentity();
            identity.ConnectionId = connectionId;
            identity.PeerRole = PMSessionPeerRole.Client;
            identity.Uid = uid;
            identity.PlayerId = uid - 100;
            identity.TeamId = 0;
            identity.HeroId = TestHeroId;
            identity.Epoch = TestEpoch;
            identity.LocalProtocolHash = TestProtocolHash;
            identity.PeerProtocolHash = TestProtocolHash;
            identity.MatchId = TestMatchId;
            identity.DsId = TestDsId;

            error = null;
            return new PMTransportConnection(identity, server.World, server.Bridge, link, TestTransportConfig());
        }

        private static PMNetObject Find(WorldFixture fixture, PMNetId id)
        {
            PMNetObject obj;
            fixture.World.TryFind(id, out obj);
            return obj;
        }

        // =================================================================================
        //  A. 入局通知 codec
        // =================================================================================

        private static void TestEntryOfferCodec()
        {
            TicketAuthority authority = CreateAuthority(TestMatchId, TestProtocolHash, _nowUnix);
            PMDsRosterIdentity identity = Identity(UidA, 1);
            byte[] ticket = IssueTicket(authority, identity, 120);
            PMDsEntryOffer offer = CreateOffer(ticket, identity, "127.0.0.1", 7801);

            string text = PMDsEntryCodec.Encode(offer);

            CheckStrEq(text.Substring(0, 6), PMDsEntryCodec.Prefix, "编码结果以 PMDS1: 开头");
            Check(text.Length <= PMDsEntryCodec.MaxTextBytes, "编码结果不超过 " + PMDsEntryCodec.MaxTextBytes + " 字节");
            Check(PMDsEntryCodec.HasPrefix(text), "HasPrefix 识别本前缀");

            PMDsEntryOffer decoded;
            string error;
            Check(PMDsEntryCodec.TryDecode(text, out decoded, out error), "严格解码成功");
            Check(decoded != null, "解码得到对象");

            if (decoded != null)
            {
                CheckStrEq(decoded.MatchId, TestMatchId, "MatchId 往返一致");
                CheckStrEq(decoded.DsId, TestDsId, "DsId 往返一致");
                CheckStrEq(decoded.Host, "127.0.0.1", "Host 往返一致");
                CheckEq(decoded.Port, 7801, "Port 往返一致");
                CheckEq(decoded.Epoch, TestEpoch, "Epoch 往返一致");
                CheckEq(decoded.ProtocolHash, TestProtocolHash, "ProtocolHash 往返一致");
                CheckEq(decoded.CollisionDigest, TestCollisionDigest, "CollisionDigest 往返一致");
                CheckEq(decoded.Identity.Uid, UidA, "Uid 往返一致");
                CheckEq(decoded.Identity.PlayerId, 1, "PlayerId 往返一致");
                CheckEq(decoded.Identity.HeroId, TestHeroId, "HeroId 往返一致");
                CheckEq(decoded.Ticket.Length, ticket.Length, "票据长度往返一致");
                Check(decoded.Ticket[0] == ticket[0] && decoded.Ticket[ticket.Length - 1] == ticket[ticket.Length - 1],
                      "票据首尾字节一致（内容未被改写）");
            }

            // ── 严格负例 ────────────────────────────────────────────────
            Check(!PMDsEntryCodec.TryDecode("PMDS0:" + text.Substring(6), out decoded, out error),
                  "错误前缀 ⇒ 拒绝（不静默退回旧链）");
            Check(!PMDsEntryCodec.TryDecode(null, out decoded, out error), "空文本 ⇒ 拒绝");
            Check(!PMDsEntryCodec.TryDecode(PMDsEntryCodec.Prefix, out decoded, out error), "只有前缀 ⇒ 拒绝");
            Check(!PMDsEntryCodec.TryDecode(text.Substring(0, text.Length - 1), out decoded, out error),
                  "Base64 被截断 ⇒ 拒绝");
            Check(!PMDsEntryCodec.TryDecode(text + "=", out decoded, out error), "Base64 尾部多余 '=' ⇒ 拒绝");
            Check(!PMDsEntryCodec.TryDecode(text + "AAAA", out decoded, out error),
                  "Base64 尾部多余载荷 ⇒ 拒绝（严格尾部）");
            Check(!PMDsEntryCodec.TryDecode(text.Replace('A', '!'), out decoded, out error),
                  "Base64 字母表外字符 ⇒ 拒绝");
            Check(!PMDsEntryCodec.TryDecode(text + new string('A', PMDsEntryCodec.MaxTextBytes), out decoded, out error),
                  "超过 " + PMDsEntryCodec.MaxTextBytes + " 字节 ⇒ 拒绝");

            // ── 范围校验（编码侧显式抛）─────────────────────────────────
            PMDsEntryOffer badPort = CreateOffer(ticket, identity, "127.0.0.1", 0);
            Check(Throws(delegate { PMDsEntryCodec.Encode(badPort); }), "Port=0 ⇒ Encode 显式抛出");
            badPort.Port = 65536;
            Check(Throws(delegate { PMDsEntryCodec.Encode(badPort); }), "Port=65536 ⇒ Encode 显式抛出");
            badPort.Port = 7801;
            badPort.Ticket = null;
            Check(Throws(delegate { PMDsEntryCodec.Encode(badPort); }), "无票据 ⇒ Encode 显式抛出");
            badPort.Ticket = new byte[PMDsEntryCodec.MaxTicketBytes + 1];
            Check(Throws(delegate { PMDsEntryCodec.Encode(badPort); }), "票据超上限 ⇒ Encode 显式抛出");

            // ── 版本与魔数 ──────────────────────────────────────────────
            byte[] payload = Convert.FromBase64String(text.Substring(PMDsEntryCodec.Prefix.Length));
            byte[] badVersion = (byte[])payload.Clone();
            badVersion[0] = 2;
            string badVersionText = PMDsEntryCodec.Prefix + Convert.ToBase64String(badVersion);
            Check(!PMDsEntryCodec.TryDecode(badVersionText, out decoded, out error), "载荷版本不受支持 ⇒ 拒绝");

            byte[] badMagic = (byte[])payload.Clone();
            badMagic[badMagic.Length - ticket.Length] = (byte)'X';
            string badMagicText = PMDsEntryCodec.Prefix + Convert.ToBase64String(badMagic);
            Check(!PMDsEntryCodec.TryDecode(badMagicText, out decoded, out error), "票据魔数不符 ⇒ 拒绝");

            // ── 最大形态：标识与票据都取上限 ─────────────────────────────
            string longId = new string('m', PMDsEntryCodec.MaxIdBytes);
            string longDs = new string('d', PMDsEntryCodec.MaxIdBytes);
            TicketAuthority bigAuthority = CreateAuthority(longId, TestProtocolHash, _nowUnix);
            PMDsTicketIssuer bigIssuer = bigAuthority.Key.CreateTicketIssuer(
                longId, longDs, TestEpoch, TestProtocolHash);
            PMDsTicket bigTicket = bigIssuer.Issue(identity, _nowUnix, TimeSpan.FromSeconds(120));
            byte[] bigTicketBytes = bigTicket.ExportTicketBytes();

            PMDsEntryOffer bigOffer = CreateOffer(bigTicketBytes, identity, "127.0.0.1", 65535);
            bigOffer.MatchId = longId;
            bigOffer.DsId = longDs;
            bigOffer.Host = new string('h', PMDsEntryCodec.MaxIdBytes);

            string bigText = PMDsEntryCodec.Encode(bigOffer);
            CheckEq(bigTicketBytes.Length, PMDsEntryCodec.MaxTicketBytes, "上限形态：票据正好等于既有 codec 上限");
            Check(bigText.Length <= PMDsEntryCodec.MaxTextBytes, "上限形态：整条文本仍在上限内（" + bigText.Length + " 字节）");
            Check(PMDsEntryCodec.TryDecode(bigText, out decoded, out error), "上限形态：严格解码成功");
            Check(decoded != null && decoded.Port == 65535, "上限形态：端口 65535 往返一致");

            // 票据/标识都取上限时，ClientHello 与 ServerHello 的差距缩到最小 ——
            // 这正是「填充」机制真正生效的场景（否则无法保证「应答不大于请求」）。
            PMHandshakeClient maxClient = new PMHandshakeClient(bigOffer);
            byte[] maxHello = maxClient.BuildClientHello();
            int maxServerHello = PMHandshakeCodec.ComputeServerHelloBytes(longId, longDs, TestEpoch, TestProtocolHash,
                TestCollisionDigest, UidA, 1, 0, TestHeroId);
            CheckGt(maxClient.PaddingBytes, 0, "上限形态：客户端填充机制确实生效（PaddingBytes > 0）");
            Check(maxHello.Length >= maxServerHello,
                  "上限形态：ClientHello（" + maxHello.Length + "B）不小于 ServerHello（" + maxServerHello + "B）");
            Check(maxHello.Length <= PMHandshakeCodec.MaxFrameBytes, "上限形态：ClientHello 仍在上限内");

            Check(offer.ToString().IndexOf("127.0.0.1", StringComparison.Ordinal) >= 0, "诊断串含公开字段");
            Check(offer.ToString().IndexOf("ticket=", StringComparison.Ordinal) >= 0, "诊断串只给票据长度");
        }

        private static bool Throws(Action action)
        {
            try
            {
                action();
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        // =================================================================================
        //  B. 握手 codec
        // =================================================================================

        private static void TestHandshakeCodec()
        {
            TicketAuthority authority = CreateAuthority(TestMatchId, TestProtocolHash, _nowUnix);
            PMDsRosterIdentity identity = Identity(UidA, 1);
            byte[] ticket = IssueTicket(authority, identity, 120);
            byte[] nonce = PMDsCrypto.RandomBytes(PMHandshakeCodec.NonceBytes);

            PMNetWriter writer = new PMNetWriter(600);
            PMHandshakeCodec.WriteClientHello(writer, ticket, 0, ticket.Length, nonce, TestProtocolHash, TestEpoch, 16);
            byte[] frame = writer.ToArray();

            PMHandshakeKind kind;
            Check(PMHandshakeCodec.IsHandshakeDatagram(frame, 0, frame.Length, out kind), "IsHandshakeDatagram 识别 ClientHello");
            Check(kind == PMHandshakeKind.ClientHello, "类型为 ClientHello");

            PMHandshakeClientHello hello;
            string error;
            Check(PMHandshakeCodec.TryReadClientHello(frame, 0, frame.Length, out hello, out error),
                  "ClientHello 严格解析成功：" + (error ?? "ok"));
            CheckEq(hello.TicketCount, ticket.Length, "票据长度一致");
            CheckEq(hello.TicketOffset, PMHandshakeCodec.HeaderBytes + PMHandshakeCodec.VarintSize(ticket.Length),
                    "票据偏移指向帧内票据（跳过长度前缀）");
            Check(hello.Ticket[hello.TicketOffset] == ticket[0], "票据首字节指向正确位置");
            CheckEq(hello.Epoch, TestEpoch, "Epoch 一致");
            CheckEq(hello.ProtocolHash, TestProtocolHash, "ProtocolHash 一致");
            CheckEq(hello.PaddingBytes, 16, "填充长度一致");
            Check(Equal(hello.ClientNonce, nonce), "nonce 一致");

            // ── 严格尾部 / 长度上限 ──────────────────────────────────────
            byte[] padded = new byte[frame.Length + 1];
            Buffer.BlockCopy(frame, 0, padded, 0, frame.Length);
            Check(!PMHandshakeCodec.TryReadClientHello(padded, 0, padded.Length, out hello, out error),
                  "尾部多一个字节 ⇒ 拒绝（严格尾部）");

            byte[] tooLarge = BuildRawClientHello(ticket, TestProtocolHash, TestEpoch,
                                                  PMHandshakeCodec.MaxPaddingBytes);
            Check(PMHandshakeCodec.TryReadClientHello(tooLarge, 0, tooLarge.Length, out hello, out error),
                  "最大填充（" + PMHandshakeCodec.MaxPaddingBytes + "）⇒ 仍可解析（" + (error ?? "ok") + "）");

            // ── 非握手帧不得被误判 ──────────────────────────────────────
            byte[] transportLike = CraftTransportDatagram(TestEpoch, 1);
            Check(!PMHandshakeCodec.IsHandshakeDatagram(transportLike, 0, transportLike.Length, out kind),
                  "传输层数据报不被判为握手帧（首字节 1 ≠ 'P'）");

            byte[] wrongVersion = (byte[])frame.Clone();
            wrongVersion[4] = 2;
            Check(!PMHandshakeCodec.IsHandshakeDatagram(wrongVersion, 0, wrongVersion.Length, out kind),
                  "握手版本不符 ⇒ 不是握手帧");

            byte[] wrongKind = (byte[])frame.Clone();
            wrongKind[5] = 9;
            Check(!PMHandshakeCodec.IsHandshakeDatagram(wrongKind, 0, wrongKind.Length, out kind),
                  "未知握手类型 ⇒ 不是握手帧");

            Check(!PMHandshakeCodec.IsHandshakeDatagram(frame, 0, 3, out kind), "短于固定头 ⇒ 不是握手帧");
            Check(!PMHandshakeCodec.IsHandshakeDatagram(null, 0, 0, out kind), "null ⇒ 不是握手帧");

            // ── ServerHello：长度公式必须与实际写出**逐字节一致** ───────
            PMHandshakeServerHello outHello = new PMHandshakeServerHello();
            outHello.MatchId = TestMatchId;
            outHello.DsId = TestDsId;
            outHello.Epoch = TestEpoch;
            outHello.ProtocolHash = TestProtocolHash;
            outHello.CollisionDigest = TestCollisionDigest;
            outHello.TicketDigest = PMDsCrypto.Sha256(ticket, 0, ticket.Length);
            outHello.ClientNonce = nonce;
            outHello.ServerNonce = PMDsCrypto.RandomBytes(PMHandshakeCodec.NonceBytes);
            outHello.Uid = UidA;
            outHello.PlayerId = 1;
            outHello.TeamId = 0;
            outHello.HeroId = TestHeroId;

            PMNetWriter serverWriter = new PMNetWriter(600);
            PMHandshakeCodec.WriteServerHello(serverWriter, outHello);
            byte[] serverFrame = serverWriter.ToArray();

            CheckEq(serverFrame.Length, PMHandshakeCodec.ComputeServerHelloBytes(
                        outHello.MatchId, outHello.DsId, outHello.Epoch, outHello.ProtocolHash,
                        outHello.CollisionDigest, outHello.Uid, outHello.PlayerId, outHello.TeamId, outHello.HeroId),
                    "ComputeServerHelloBytes 与实际写出长度一致");

            PMHandshakeServerHello readBack;
            Check(PMHandshakeCodec.TryReadServerHello(serverFrame, 0, serverFrame.Length, out readBack, out error),
                  "ServerHello 严格解析成功：" + (error ?? "ok"));
            CheckStrEq(readBack.MatchId, TestMatchId, "ServerHello MatchId 一致");
            CheckStrEq(readBack.DsId, TestDsId, "ServerHello DsId 一致");
            Check(Equal(readBack.TicketDigest, outHello.TicketDigest), "ServerHello 摘要一致");
            Check(Equal(readBack.ClientNonce, nonce), "ServerHello nonce 回带一致");
            CheckEq(readBack.Uid, UidA, "ServerHello uid 一致");
            CheckEq(readBack.HeroId, TestHeroId, "ServerHello heroId 一致");

            byte[] serverPadded = new byte[serverFrame.Length + 1];
            Buffer.BlockCopy(serverFrame, 0, serverPadded, 0, serverFrame.Length);
            Check(!PMHandshakeCodec.TryReadServerHello(serverPadded, 0, serverPadded.Length, out readBack, out error),
                  "ServerHello 尾部多余字节 ⇒ 拒绝");

            // ── ServerReject（本实现不发送，但必须能识别）────────────────
            PMNetWriter rejectWriter = new PMNetWriter(64);
            PMHandshakeCodec.WriteServerReject(rejectWriter, PMDsTicketVerdict.Expired, 3, 5000u);
            byte[] rejectFrame = rejectWriter.ToArray();
            PMDsTicketVerdict verdict;
            byte stage;
            uint retryAfterMs;
            Check(PMHandshakeCodec.TryReadServerReject(rejectFrame, 0, rejectFrame.Length,
                                                       out verdict, out stage, out retryAfterMs, out error),
                  "ServerReject 解析成功：" + (error ?? "ok"));
            Check(verdict == PMDsTicketVerdict.Expired, "ServerReject verdict 一致");
            CheckEq(stage, 3, "ServerReject stage 一致");
            CheckEq(retryAfterMs, 5000, "ServerReject retryAfter 一致");

            // ── 客户端填充必须保证「应答不大于请求」──────────────────────
            PMDsEntryOffer offer = CreateOffer(ticket, identity, "127.0.0.1", 7801);
            PMHandshakeClient client = new PMHandshakeClient(offer);
            byte[] clientHello = client.BuildClientHello();
            Check(clientHello.Length > serverFrame.Length,
                  "ClientHello 严格大于 ServerHello（应答不大于请求的基础：" + clientHello.Length + "B > "
                  + serverFrame.Length + "B）");
            Check(clientHello.Length >= client.ExpectedServerHelloBytes,
                  "ClientHello 不小于预期的 ServerHello 长度");

            // 之后的合成 ServerHello 一律以「客户端刚刚发出的 nonce」为准：
            // 这样每一个负例里只有被测字段不匹配（否则会退化成一个「总是 nonce 不符」的假阳性）。
            outHello.ClientNonce = client.LastNonce;
            serverWriter = new PMNetWriter(600);
            PMHandshakeCodec.WriteServerHello(serverWriter, outHello);
            serverFrame = serverWriter.ToArray();
            Check(clientHello.Length >= serverFrame.Length,
                  "ClientHello（" + clientHello.Length + "B）不小于 ServerHello（" + serverFrame.Length + "B）");
            Check(clientHello.Length <= PMHandshakeCodec.MaxFrameBytes, "ClientHello 不超过握手帧上限");

            // ── 客户端关联校验（合成 ServerHello：错 nonce / 错摘要 / 错局）──
            PMSessionBinding binding;
            PMHandshakeServerHello forged = outHello;
            forged.ClientNonce = PMDsCrypto.RandomBytes(PMHandshakeCodec.NonceBytes);
            byte[] forgedFrame = WriteServerHello(forged);
            Check(client.Handle(forgedFrame, 0, forgedFrame.Length, out binding, out error)
                  == PMHandshakeClientOutcome.Ignored, "错 nonce ⇒ 客户端不采信（Ignored）");

            forged = outHello;
            forged.TicketDigest = PMDsCrypto.RandomBytes(PMHandshakeCodec.DigestBytes);
            forgedFrame = WriteServerHello(forged);
            Check(client.Handle(forgedFrame, 0, forgedFrame.Length, out binding, out error)
                  == PMHandshakeClientOutcome.Ignored, "错票据摘要 ⇒ 客户端不采信（Ignored）");

            forged = outHello;
            forged.MatchId = "other-match";
            forgedFrame = WriteServerHello(forged);
            Check(client.Handle(forgedFrame, 0, forgedFrame.Length, out binding, out error)
                  == PMHandshakeClientOutcome.Ignored, "错对局 ⇒ 客户端不采信（Ignored）");

            forged = outHello;
            forged.ProtocolHash = TestProtocolHash ^ 0xFFu;
            forgedFrame = WriteServerHello(forged);
            Check(client.Handle(forgedFrame, 0, forgedFrame.Length, out binding, out error)
                  == PMHandshakeClientOutcome.Ignored, "错摘要 ⇒ 客户端不采信（Ignored）");

            forged = outHello;
            forged.Uid = UidB;
            forgedFrame = WriteServerHello(forged);
            Check(client.Handle(forgedFrame, 0, forgedFrame.Length, out binding, out error)
                  == PMHandshakeClientOutcome.Ignored, "错身份 ⇒ 客户端不采信（Ignored）");

            // 正例：完全匹配时必须接受
            Check(client.Handle(serverFrame, 0, serverFrame.Length, out binding, out error)
                  == PMHandshakeClientOutcome.Accepted, "完全匹配 ⇒ 客户端接受（" + (error ?? "ok") + "）");
            CheckEq(binding.Uid, UidA, "接受时绑定 uid 正确");
            CheckEq(binding.ProtocolHash, TestProtocolHash, "接受时绑定摘要正确");

            CheckGt(client.IdentityMismatches, 0, "客户端身份不符计数非空");

            // 连续不符达上限 ⇒ 明确失败（不无限重试）
            for (int i = 0; i < PMHandshakeClient.MaxIdentityMismatches; i++)
            {
                client.Handle(forgedFrame, 0, forgedFrame.Length, out binding, out error);
            }

            Check(client.Handle(forgedFrame, 0, forgedFrame.Length, out binding, out error)
                  == PMHandshakeClientOutcome.Rejected, "连续不符达上限 ⇒ 明确拒绝（不再无限重试）");
        }

        private static byte[] WriteServerHello(PMHandshakeServerHello hello)
        {
            PMNetWriter writer = new PMNetWriter(600);
            PMHandshakeCodec.WriteServerHello(writer, hello);
            return writer.ToArray();
        }

        private static bool Equal(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) { return false; }
            }

            return true;
        }

        // =================================================================================
        //  C. 端点消费账本
        // =================================================================================

        private static void TestEndpointLedger()
        {
            TicketAuthority authority = CreateAuthority(TestMatchId, TestProtocolHash, _nowUnix);
            PMDsRosterIdentity idA = Identity(UidA, 1);
            PMDsRosterIdentity idB = Identity(UidB, 2);

            byte[] ticketA = IssueTicket(authority, idA, 120);
            byte[] ticketB = IssueTicket(authority, idB, 120);

            PMDsEndpointLedger ledger = new PMDsEndpointLedger(2, 4);

            PMSessionBinding bindingA;
            PMDsLedgerReject reject;
            Check(ledger.TryConsume(ticketA, 0, ticketA.Length, Verified(idA, _nowUnix + 120),
                                    "127.0.0.1:1001", _nowUnix, out bindingA, out reject),
                  "首次消费成功（" + reject + "）");
            CheckEq(bindingA.ConnectionId, 1, "首次分配的 ConnectionId 为 1");
            CheckEq(bindingA.TicketDigest.Length, PMHandshakeCodec.DigestBytes, "账本键是完整 SHA-256（32 字节）");

            PMSessionBinding retry;
            Check(ledger.TryConsume(ticketA, 0, ticketA.Length, Verified(idA, _nowUnix + 120),
                                    "127.0.0.1:1001", _nowUnix, out retry, out reject),
                  "同票同端点重试 ⇒ 幂等接受");
            CheckEq(retry.ConnectionId, bindingA.ConnectionId, "重试返回同一个 ConnectionId（不新建）");
            CheckEq(ledger.Stats.IdempotentRetries, 1, "幂等重试被记账");
            CheckEq(ledger.ActiveBindingCount, 1, "幂等重试没有新建绑定");

            PMSessionBinding other;
            Check(!ledger.TryConsume(ticketA, 0, ticketA.Length, Verified(idA, _nowUnix + 120),
                                     "127.0.0.1:2002", _nowUnix, out other, out reject),
                  "同票第二端点 ⇒ 拒绝");
            Check(reject == PMDsLedgerReject.DigestRebound, "第二端点拒绝原因为 DigestRebound（实际 " + reject + "）");
            CheckEq(ledger.ActiveBindingCount, 1, "第二端点没有新建绑定");

            Check(!ledger.TryConsume(ticketB, 0, ticketB.Length, Verified(idB, _nowUnix + 120),
                                     "127.0.0.1:1001", _nowUnix, out other, out reject),
                  "同端点另一张票 ⇒ 拒绝");
            Check(reject == PMDsLedgerReject.EndpointTaken, "端点占用拒绝原因为 EndpointTaken（实际 " + reject + "）");

            PMSessionBinding bindingB;
            Check(ledger.TryConsume(ticketB, 0, ticketB.Length, Verified(idB, _nowUnix + 120),
                                    "127.0.0.1:2002", _nowUnix, out bindingB, out reject),
                  "第二张票在另一个端点消费成功");
            CheckEq(bindingB.ConnectionId, 2, "ConnectionId 单调递增");
            CheckEq(ledger.ActiveBindingCount, 2, "两个绑定都活着");

            PMDsRosterIdentity idC = Identity(UidC, 3);
            byte[] ticketC = IssueTicket(authority, idC, 120);
            Check(!ledger.TryConsume(ticketC, 0, ticketC.Length, Verified(idC, _nowUnix + 120),
                                     "127.0.0.1:3003", _nowUnix, out other, out reject),
                  "超出绑定上限 ⇒ 拒绝");
            Check(reject == PMDsLedgerReject.Capacity, "容量拒绝原因为 Capacity（实际 " + reject + "）");

            // ── 断开 ⇒ 消费墓碑 ────────────────────────────────────────
            Check(ledger.ReleaseEndpoint("127.0.0.1:1001", _nowUnix), "释放端点 1001");
            CheckEq(ledger.ActiveBindingCount, 1, "释放后活跃绑定减少");
            CheckEq(ledger.Stats.Tombstones, 1, "释放产生一个消费墓碑");

            Check(!ledger.TryConsume(ticketA, 0, ticketA.Length, Verified(idA, _nowUnix + 120),
                                     "127.0.0.1:1001", _nowUnix, out other, out reject),
                  "墓碑未过期 ⇒ 同票同端点也不得再消费");
            Check(reject == PMDsLedgerReject.DigestTombstoned, "墓碑拒绝原因为 DigestTombstoned（实际 " + reject + "）");
            Check(!ledger.TryConsume(ticketA, 0, ticketA.Length, Verified(idA, _nowUnix + 120),
                                     "127.0.0.1:3003", _nowUnix, out other, out reject),
                  "墓碑未过期 ⇒ 换端点同样拒绝");
            Check(reject == PMDsLedgerReject.DigestTombstoned, "换端点墓碑拒绝原因为 DigestTombstoned（实际 " + reject + "）");

            // ── 新票（Lobby 显式重发）在同一端点可以入场 ─────────────────
            byte[] reissuedA = ReissueTicket(authority, idA, 120);
            Check(!Equal(reissuedA, ticketA), "重发票据字节确实不同（新 nonce）");

            PMSessionBinding rebind;
            Check(ledger.TryConsume(reissuedA, 0, reissuedA.Length, Verified(idA, _nowUnix + 120),
                                    "127.0.0.1:1001", _nowUnix, out rebind, out reject),
                  "新票在同一端点入场成功（释放后坑位归还）");
            CheckEq(rebind.ConnectionId, 3, "ConnectionId 单调且不复用");

            // uid 被另一张票占用 ⇒ UidBusy
            byte[] anotherA = ReissueTicket(authority, idA, 120);
            Check(!ledger.TryConsume(anotherA, 0, anotherA.Length, Verified(idA, _nowUnix + 120),
                                     "127.0.0.1:4004", _nowUnix, out other, out reject),
                  "同一 uid 的第二张票 ⇒ 拒绝");
            Check(reject == PMDsLedgerReject.UidBusy, "uid 占用拒绝原因为 UidBusy（实际 " + reject + "）");

            // ── 验证前置与端点键（防御性）────────────────────────────────
            Check(!ledger.TryConsume(ticketB, 0, ticketB.Length, NotVerified(), "127.0.0.1:2002", _nowUnix,
                                     out other, out reject), "未通过验票 ⇒ 拒绝");
            Check(reject == PMDsLedgerReject.NotVerified, "未验票拒绝原因为 NotVerified（实际 " + reject + "）");

            PMDsTicketVerification expired = Verified(idB, _nowUnix - 1);
            expired.Verdict = PMDsTicketVerdict.Expired;
            Check(!ledger.TryConsume(ticketB, 0, ticketB.Length, expired, "127.0.0.1:2002", _nowUnix,
                                     out other, out reject), "票据过期 ⇒ 拒绝");
            Check(reject == PMDsLedgerReject.Expired, "过期拒绝原因为 Expired（实际 " + reject + "）");

            Check(!ledger.TryConsume(ticketB, 0, ticketB.Length, Verified(idB, _nowUnix + 120), string.Empty,
                                     _nowUnix, out other, out reject), "空端点键 ⇒ 拒绝");
            Check(reject == PMDsLedgerReject.BadEndpoint, "端点键拒绝原因为 BadEndpoint（实际 " + reject + "）");

            Check(!ledger.TryConsume(ticketB, 0, ticketB.Length, Verified(idB, _nowUnix + 120),
                                     new string('e', PMDsEndpointLedger.MaxEndpointKeyChars + 1), _nowUnix,
                                     out other, out reject), "超长端点键 ⇒ 拒绝");
            Check(reject == PMDsLedgerReject.BadEndpoint, "超长端点键拒绝原因为 BadEndpoint（实际 " + reject + "）");

            Check(!ledger.TryConsume(null, 0, 0, Verified(idB, _nowUnix + 120), "127.0.0.1:2002", _nowUnix,
                                     out other, out reject), "空票据窗口 ⇒ 拒绝");

            // ── 墓碑容量（有界 + fail-closed）─────────────────────────────
            //
            // 语义（本轮复核修正）：墓碑超上限时**不许**让「仍有效的已消费旧票」失去重放保护。
            // 淘汰一个未过期墓碑 ⇒ 账本对该摘要出现盲区 ⇒ 在「被淘汰票据的到期时刻」之前，
            // 一切**未知摘要**的新消费一律拒绝（fail-closed，记 ReplayBlindRefusals）。
            // 票面过期后重放会被验票阶段直接拒（Expired），账本自动恢复接纳。
            PMDsEndpointLedger tiny = new PMDsEndpointLedger(2, 1);
            PMDsRosterIdentity[] tinyIds = new PMDsRosterIdentity[2];
            byte[][] tinyTickets = new byte[2][];
            string[] tinyKeys = new string[2];
            for (int i = 0; i < 2; i++)
            {
                tinyIds[i] = Identity(500 + i, 10 + i);
                tinyTickets[i] = IssueTicket(authority, tinyIds[i], 120);
                tinyKeys[i] = "127.0.0.1:" + (9000 + i);
            }

            PMSessionBinding tinyBin;
            for (int i = 0; i < 2; i++)
            {
                Check(tiny.TryConsume(tinyTickets[i], 0, tinyTickets[i].Length,
                                      Verified(tinyIds[i], _nowUnix + 120), tinyKeys[i], _nowUnix, out tinyBin, out reject),
                      "墓碑容量用例：第 " + (i + 1) + " 张票消费成功");
                tiny.ReleaseEndpoint(tinyKeys[i], _nowUnix);
            }

            CheckEq(tiny.Stats.Tombstones, 1, "墓碑数被限制在上限（1）");
            CheckGt(tiny.Stats.TombstoneEvictions, 0, "墓碑超上限被淘汰（计数非空）");

            // 被淘汰的正是**未过期**的 tinyTickets[0]：换端点重放必须被拒（不再是「全新票据」）。
            Check(!tiny.TryConsume(tinyTickets[0], 0, tinyTickets[0].Length,
                                   Verified(tinyIds[0], _nowUnix + 120), "127.0.0.1:9050", _nowUnix,
                                   out tinyBin, out reject),
                  "墓碑淘汰后，旧票换端点重放被拒（fail-closed）");
            Check(reject == PMDsLedgerReject.Capacity, "fail-closed 拒绝原因为 Capacity（实际 " + reject + "）");
            CheckGt(tiny.Stats.ReplayBlindRefusals, 0, "重放盲区拒绝被记账（ReplayBlindRefusals）");
            CheckEq(tiny.Stats.ConnectionsAllocated, 2, "重放没有被分配新 ConnectionId");

            // 盲区窗口结束（被淘汰票据已过期）⇒ 账本恢复接纳新票。
            TicketAuthority later = CreateAuthority(TestMatchId, TestProtocolHash, _nowUnix + 200);
            byte[] laterTicket = IssueTicket(later, Identity(510, 20), 120);
            Check(tiny.TryConsume(laterTicket, 0, laterTicket.Length, Verified(Identity(510, 20), _nowUnix + 320),
                                  "127.0.0.1:9060", _nowUnix + 121, out tinyBin, out reject),
                  "盲区窗口结束后账本恢复接纳新票（实际 " + reject + "）");

            // ── 摘要数组所有权：外部就地改写不得破坏账本键（重放保护）──────
            TicketAuthority aliasing = CreateAuthority(TestMatchId, TestProtocolHash, _nowUnix);
            PMDsEndpointLedger owned = new PMDsEndpointLedger(4, 4);
            byte[] aliasTicket = IssueTicket(aliasing, Identity(700, 30), 120);
            PMSessionBinding aliasBinding;
            Check(owned.TryConsume(aliasTicket, 0, aliasTicket.Length, Verified(Identity(700, 30), _nowUnix + 120),
                                   "127.0.0.1:9301", _nowUnix, out aliasBinding, out reject),
                  "摘要所有权用例：消费成功");
            CheckEq(aliasBinding.TicketDigest.Length, PMHandshakeCodec.DigestBytes, "绑定暴露的摘要是 32 字节");

            // 宿主做「卫生清零 / 复用缓冲」这类就地改写：不得影响账本自己的键。
            for (int i = 0; i < aliasBinding.TicketDigest.Length; i++)
            {
                aliasBinding.TicketDigest[i] = 0;
            }

            Check(owned.ReleaseEndpoint("127.0.0.1:9301", _nowUnix), "释放成功（账本仍保有该摘要）");
            CheckEq(owned.Stats.Tombstones, 1, "释放后产生墓碑（键未被外部改写破坏）");
            Check(!owned.TryConsume(aliasTicket, 0, aliasTicket.Length, Verified(Identity(700, 30), _nowUnix + 120),
                                    "127.0.0.1:9302", _nowUnix, out aliasBinding, out reject),
                  "绑定摘要被外部改写后，同票换端点重放**仍被拒**");
            Check(reject == PMDsLedgerReject.DigestTombstoned,
                  "摘要所有权用例的拒绝原因为 DigestTombstoned（实际 " + reject + "）");

            // ── 独立副本不得影响「按内容查找」的撤回消费（RenounceConsume）────
            //   撤回消费是传给账本的 `binding.TicketDigest`（现在是副本）：
            //   它必须仍能按内容命中内部键，否则「未建链就撤回消费」会静默失败。
            TicketAuthority renounceAuthority = CreateAuthority(TestMatchId, TestProtocolHash, _nowUnix);
            PMDsEndpointLedger renounceLedger = new PMDsEndpointLedger(4, 4);
            byte[] renounceTicket = IssueTicket(renounceAuthority, Identity(800, 40), 120);
            PMSessionBinding renounceBinding;
            Check(renounceLedger.TryConsume(renounceTicket, 0, renounceTicket.Length,
                                            Verified(Identity(800, 40), _nowUnix + 120), "127.0.0.1:9401",
                                            _nowUnix, out renounceBinding, out reject),
                  "撤回消费用例：先消费成功");
            Check(renounceLedger.RenounceConsume(renounceBinding.TicketDigest, "127.0.0.1:9401"),
                  "RenounceConsume 用绑定的摘要副本仍能按内容命中（返回 true）");
            CheckEq(renounceLedger.Stats.Renounced, 1, "撤回消费被记账");
            CheckEq(renounceLedger.Stats.Tombstones, 0, "撤回消费**不写墓碑**");
            CheckEq(renounceLedger.ActiveBindingCount, 0, "撤回后活跃绑定归零");
            Check(renounceLedger.TryConsume(renounceTicket, 0, renounceTicket.Length,
                                            Verified(Identity(800, 40), _nowUnix + 120), "127.0.0.1:9402",
                                            _nowUnix, out renounceBinding, out reject),
                  "撤回后同一张票仍可再次入场（环境失败不该把客户端永久挡在门外）");

            PMDsEndpointLedger expiring = new PMDsEndpointLedger(2, 4);
            byte[] tkExpire = IssueTicket(authority, idC, 120);
            PMSessionBinding tmp;
            string expireKey = "127.0.0.1:7777";
            Check(expiring.TryConsume(tkExpire, 0, tkExpire.Length, Verified(idC, _nowUnix + 120),
                                      expireKey, _nowUnix, out tmp, out reject), "过期清理用例：消费成功");
            Check(expiring.ReleaseEndpoint(expireKey, _nowUnix + 200), "过期后释放（票据已过期 ⇒ 不写墓碑）");
            CheckEq(expiring.Stats.Tombstones, 0, "票据已过期时释放不留墓碑");
            CheckGt(expiring.Stats.ExpiredPrunes, 0, "过期条目被清理（计数非空）");

            expiring.ReleaseSession();
            CheckEq(expiring.ActiveBindingCount, 0, "ReleaseSession 清空绑定");
            CheckEq(expiring.Stats.SessionResets, 1, "ReleaseSession 被记账");

            CheckGt(ledger.Stats.ConnectionsAllocated, 0, "账本分配计数非空");
            Check(ledger.Stats.ToString().IndexOf("ledger(", StringComparison.Ordinal) == 0, "账本诊断串可读");
        }

        private static PMDsTicketVerification Verified(PMDsRosterIdentity identity, long expiresAt)
        {
            PMDsTicketVerification verification = new PMDsTicketVerification();
            verification.Verdict = PMDsTicketVerdict.Valid;
            verification.Identity = identity;
            verification.MatchId = TestMatchId;
            verification.DsId = TestDsId;
            verification.Epoch = TestEpoch;
            verification.ProtocolHash = TestProtocolHash;
            verification.IssuedAtUnixSeconds = _nowUnix;
            verification.ExpiresAtUnixSeconds = expiresAt;
            return verification;
        }

        private static PMDsTicketVerification NotVerified()
        {
            PMDsTicketVerification verification = new PMDsTicketVerification();
            verification.Verdict = PMDsTicketVerdict.MacMismatch;
            return verification;
        }

        /// <summary>让 Lobby「显式重发」一张新票（撤销后再签发 ⇒ 新 nonce、新字节）。</summary>
        private static byte[] ReissueTicket(TicketAuthority authority, PMDsRosterIdentity identity, int lifetimeSeconds)
        {
            authority.Issuer.Revoke(identity.Uid);
            return IssueTicket(authority, identity, lifetimeSeconds);
        }

        // =================================================================================
        //  D. 真实 localhost UDP：两玩家
        // =================================================================================

        private sealed class TwoPlayerScenario
        {
            public TicketAuthority Authority;
            public PMDsBootstrappedMatch Boot;
            public WorldFixture Server;
            public WorldFixture ClientA;
            public WorldFixture ClientB;
            public PMUdpSessionEndpoint ServerEndpoint;
            public PMUdpSessionEndpoint ClientEndpointA;
            public PMUdpSessionEndpoint ClientEndpointB;
            public byte[] TicketA;
            public byte[] TicketB;
            public NetActor ActorA;
            public NetActor ActorB;
        }

        private static TwoPlayerScenario OpenTwoPlayerScenario(bool withExpiredRosterPlayer)
        {
            TwoPlayerScenario scenario = new TwoPlayerScenario();
            scenario.Authority = CreateAuthority(TestMatchId, TestProtocolHash, _nowUnix);

            PMDsRosterIdentity idA = Identity(UidA, 1);
            PMDsRosterIdentity idB = Identity(UidB, 2);
            scenario.TicketA = IssueTicket(scenario.Authority, idA, 120);
            scenario.TicketB = IssueTicket(scenario.Authority, idB, 120);

            List<PMDsBootstrapPlayer> roster = new List<PMDsBootstrapPlayer>();
            roster.Add(new PMDsBootstrapPlayer(idA, scenario.TicketA));
            roster.Add(new PMDsBootstrapPlayer(idB, scenario.TicketB));

            if (withExpiredRosterPlayer)
            {
                PMDsRosterIdentity idC = Identity(UidC, 3);
                // 故意用「很久以前」签发 ⇒ 到 Pump 的时刻已经过期（测 Expired 分支）。
                byte[] expired = IssueTicket(scenario.Authority, idC, 120);
                // 直接改写签发基准不可行（票据已签），改为把它的有效期压到最短并在 Pump 时把 nowUnix 推进。
                roster.Add(new PMDsBootstrapPlayer(idC, expired));
            }

            scenario.Boot = BuildBoot(scenario.Authority, roster.ToArray());

            scenario.Server = CreateWorld(true);
            scenario.ClientA = CreateWorld(false);
            scenario.ClientB = CreateWorld(false);

            scenario.ServerEndpoint = PMUdpSessionEndpoint.OpenServer(scenario.Boot, scenario.Server.Bridge,
                "127.0.0.1", 0, TestTransportConfig(), 0);
            scenario.ServerEndpoint.Connected += delegate(PMTransportConnection c) { scenario.Server.ConnectedEvents.Add(c); };
            scenario.ServerEndpoint.Failed += delegate(string m) { scenario.Server.FailedEvents.Add(m); };

            return scenario;
        }

        private static void ConnectClient(TwoPlayerScenario scenario, bool isA)
        {
            WorldFixture fixture = isA ? scenario.ClientA : scenario.ClientB;
            byte[] ticket = isA ? scenario.TicketA : scenario.TicketB;
            PMDsRosterIdentity identity = isA ? Identity(UidA, 1) : Identity(UidB, 2);

            PMDsEntryOffer offer = CreateOffer(ticket, identity, "127.0.0.1", scenario.ServerEndpoint.BoundPort);

            // 走一遍真实文本编解码（客户端就是从 MainPack.Str 里读出这条文本的）。
            string text = PMDsEntryCodec.Encode(offer);
            PMDsEntryOffer parsed;
            string error;
            if (!PMDsEntryCodec.TryDecode(text, out parsed, out error))
            {
                throw new InvalidOperationException("offer 文本解码失败：" + error);
            }

            PMUdpSessionEndpoint endpoint = PMUdpSessionEndpoint.OpenClient(parsed, fixture.Bridge, TestTransportConfig());
            endpoint.Connected += delegate(PMTransportConnection c) { fixture.ConnectedEvents.Add(c); };
            endpoint.Failed += delegate(string m) { fixture.FailedEvents.Add(m); };

            if (isA) { scenario.ClientEndpointA = endpoint; } else { scenario.ClientEndpointB = endpoint; }
        }

        private static void TestRealUdpTwoPlayers()
        {
            TwoPlayerScenario scenario = OpenTwoPlayerScenario(false);
            CheckGt(scenario.ServerEndpoint.BoundPort, 0, "服务端绑定了临时端口（" + scenario.ServerEndpoint.BoundPort + "）");
            CheckGt(scenario.Boot.Epoch, 0, "引导文件解析出会话世代");

            ConnectClient(scenario, true);
            ConnectClient(scenario, false);

            bool connected = PumpUntil(
                delegate
                {
                    return scenario.ServerEndpoint.ConnectionCount == 2
                        && scenario.ClientEndpointA.ClientConnection != null
                        && scenario.ClientEndpointB.ClientConnection != null;
                },
                400,
                scenario.ServerEndpoint, scenario.ClientEndpointA, scenario.ClientEndpointB);

            Check(connected, "两个客户端在真实 UDP 上完成入局（服务端 2 条连接）");
            CheckEq(scenario.ServerEndpoint.ConnectionCount, 2, "服务端 ConnectionCount = 2");
            CheckEq(scenario.ClientEndpointA.ConnectionCount, 1, "客户端 A ConnectionCount = 1");
            CheckEq(scenario.ClientEndpointB.ConnectionCount, 1, "客户端 B ConnectionCount = 1");

            CheckEq(scenario.Server.ConnectedEvents.Count, 2, "服务端 Connected 事件触发 2 次");
            CheckEq(scenario.ClientA.ConnectedEvents.Count, 1, "客户端 A Connected 事件恰好 1 次");
            CheckEq(scenario.ClientB.ConnectedEvents.Count, 1, "客户端 B Connected 事件恰好 1 次");

            CheckEq(scenario.ServerEndpoint.Ledger.ActiveBindingCount, 2, "账本活跃绑定 2 个");
            CheckEq(scenario.ServerEndpoint.Ledger.Stats.ConnectionsAllocated, 2, "账本分配了 2 个 ConnectionId");
            CheckEq(scenario.ServerEndpoint.HandshakeServer.Accepted, 2, "握手处理器接受 2 次");
            CheckEq(scenario.ServerEndpoint.HandshakeServer.AmplificationRefusals, 0, "没有任何回包因放大被拒");
            CheckEq(scenario.ServerEndpoint.HandshakeRejections, 0, "服务端没有拒绝任何握手");
            CheckEq(scenario.ServerEndpoint.DroppedUnboundDatagrams, 0, "没有未认证数据报被丢（全走握手）");

            PMTransportConnection connA = scenario.Server.Bridge.FindConnectionByUid(UidA);
            PMTransportConnection connB = scenario.Server.Bridge.FindConnectionByUid(UidB);
            Check(connA != null && connB != null, "服务端按 uid 找到两条连接");
            Check(connA != null && connB != null && connA.ConnectionId != connB.ConnectionId,
                  "两个玩家的 ConnectionId 不同");

            if (connA != null && connB != null)
            {
                CheckEq(connA.Identity.Uid, UidA, "连接 A 的 uid 来自票据（不是包内自报）");
                CheckEq(connB.Identity.Uid, UidB, "连接 B 的 uid 来自票据");
                CheckEq(connA.Identity.Epoch, TestEpoch, "连接 A 的世代来自票据");
                Check(connA.IsReady && connB.IsReady, "两条服务端连接都已就绪");
            }

            CheckEq(scenario.ClientEndpointA.ClientConnection.Identity.Uid, UidA, "客户端 A 的身份来自 ServerHello 校验结果");
            CheckEq(scenario.ClientA.FailedEvents.Count, 0, "客户端 A 无失败事件");
            CheckEq(scenario.ClientB.FailedEvents.Count, 0, "客户端 B 无失败事件");

            // ── 权威创建 + 复制初值 ─────────────────────────────────────
            scenario.ActorA = new NetActor();
            scenario.ActorA.Health = 11;
            Check(scenario.Server.World.Spawn(scenario.ActorA, TestClassId), "服务端 Spawn actorA");
            scenario.ActorA.OwnerConnection = connA;
            scenario.Server.Bridge.RegisterReplicatedObject(scenario.ActorA);

            scenario.ActorB = new NetActor();
            scenario.ActorB.Health = 22;
            Check(scenario.Server.World.Spawn(scenario.ActorB, TestClassId), "服务端 Spawn actorB");
            scenario.ActorB.OwnerConnection = connB;
            scenario.Server.Bridge.RegisterReplicatedObject(scenario.ActorB);

            bool replicated = PumpUntil(
                delegate
                {
                    return Find(scenario.ClientA, scenario.ActorA.NetId) != null
                        && Find(scenario.ClientB, scenario.ActorB.NetId) != null;
                },
                400,
                scenario.ServerEndpoint, scenario.ClientEndpointA, scenario.ClientEndpointB);

            Check(replicated, "两个客户端都拿到了各自 actor 的副本");

            NetActor replicaA = Find(scenario.ClientA, scenario.ActorA.NetId) as NetActor;
            NetActor replicaB = Find(scenario.ClientB, scenario.ActorB.NetId) as NetActor;

            if (replicaA != null && replicaB != null)
            {
                CheckEq(replicaA.CreateCount, 1, "客户端 A 创建回调恰好一次");
                CheckEq(replicaA.CreateHealthSeen, 11, "客户端 A 在创建回调里就读到初值");
                CheckEq(replicaB.CreateHealthSeen, 22, "客户端 B 在创建回调里就读到初值");

                // ── 客户端 → 服务端 RPC → 属性回程 ──────────────────────
                Check(scenario.ClientA.Bridge.SendRpc(replicaA, RpcMark,
                        delegate(PMNetObject t, PMNetWriter w) { w.WriteInt32(77); }),
                      "客户端 A 发出 Server RPC（真实 UDP）");

                bool echoed = PumpUntil(
                    delegate { return scenario.ActorA.LastMark == 77 && replicaA.Health == 77; },
                    400,
                    scenario.ServerEndpoint, scenario.ClientEndpointA, scenario.ClientEndpointB);

                Check(echoed, "RPC 在权威侧执行且属性回程收敛（LastMark/Health = 77）");
                CheckEq(scenario.ActorA.MarkCount, 1, "权威侧 Mark 恰好执行一次");
                CheckEq(replicaA.Health, 77, "客户端 A 的属性收敛到权威值");
            }

            CheckGt(scenario.Server.Bridge.Replication.Stats.UpdateRecordsSent, 0, "服务端发过复制更新记录");
            CheckGt(scenario.Server.Bridge.Replication.Stats.AcksReceived, 0, "服务端收到过复制版本确认（客户端→服务端方向）");
            CheckGt(scenario.ClientA.Bridge.Replication.Stats.UpdateRecordsApplied, 0, "客户端 A 应用过复制更新记录");
            CheckGt(scenario.ClientA.Bridge.Replication.Stats.AcksSent, 0, "客户端 A 发送过复制版本确认");
            CheckGt(connA.Transport.Stats.DatagramsReceived, 0, "服务端连接 A 的 Transport 收到过数据报");
            CheckEq(connA.Transport.Stats.ParseErrors, 0, "服务端连接 A 没有解析错误");

            CheckEq(scenario.Server.World.Stats.ProtocolErrors, 0, "服务端世界没有协议错误");
            CheckEq(scenario.ClientA.World.Stats.ProtocolErrors, 0, "客户端 A 世界没有协议错误");

            // ── 幂等重试：真实客户端重发 ClientHello 不新建连接 ─────────
            long allocatedBefore = scenario.ServerEndpoint.Ledger.Stats.ConnectionsAllocated;
            PMUdpSessionEndpoint[] endpoints = { scenario.ServerEndpoint, scenario.ClientEndpointA, scenario.ClientEndpointB };
            PumpFor(6, endpoints);
            CheckEq(scenario.ServerEndpoint.Ledger.Stats.ConnectionsAllocated, allocatedBefore,
                    "后续帧里没有新建 ConnectionId");

            // ── 清理 ────────────────────────────────────────────────────
            scenario.ClientEndpointA.Dispose();
            scenario.ClientEndpointB.Dispose();
            scenario.ServerEndpoint.Dispose();
            CheckEq(scenario.ServerEndpoint.ConnectionCount, 0, "Dispose 后连接数归零");
            CheckEq(scenario.ServerEndpoint.Ledger.ActiveBindingCount, 0, "Dispose 后账本绑定清空");
        }

        // =================================================================================
        //  J. 端点 IsDisposed 只读标志（R4-B D5 最小修法）
        // =================================================================================

        /// <summary>
        /// J. <see cref="PMUdpSessionEndpoint.IsDisposed"/>：只读快照（返回现有 <c>_disposed</c>）。
        ///
        /// D5 缺陷：端点 <c>_disposed</c> 置位后 <c>Pump</c> 首行直接 return，既不置 ClientFailed、
        /// 也不走 CheckClientSessionAfterUpdate ⇒ 只挂 ClientFailed/Failed 的宿主会「端点不再 Pump、宿主继续预测」。
        /// 宿主组据本标志显式 Fail + Freeze；本 Section 只钉住标志本身的生命周期与语义不变性：
        ///   1) OpenServer/OpenClient 之后、Dispose 之前为 false；
        ///   2) Dispose 之后为 true，且只影响自身；
        ///   3) Dispose 幂等（重复调用不抛、标志保持 true）；
        ///   4) Dispose 后 Pump 仍是 no-op（不发送、不接收、不抛、不翻转标志）——
        ///      即本轮只新增了只读 getter，Dispose/Pump/ObjectDisposedException 路径语义未改。
        /// </summary>
        private static void TestEndpointDisposedFlag()
        {
            TwoPlayerScenario scenario = OpenTwoPlayerScenario(false);
            ConnectClient(scenario, true);
            ConnectClient(scenario, false);

            bool connected = PumpUntil(
                delegate
                {
                    return scenario.ServerEndpoint.ConnectionCount == 2
                        && scenario.ClientEndpointA.ClientConnection != null
                        && scenario.ClientEndpointB.ClientConnection != null;
                },
                400,
                scenario.ServerEndpoint, scenario.ClientEndpointA, scenario.ClientEndpointB);
            Check(connected, "J 前置：两客户端经真实 localhost UDP 建链成功");

            // ---- ① 生命周期前：Open 后未 Dispose ---- 
            Check(!scenario.ServerEndpoint.IsDisposed, "OpenServer 后未 Dispose：IsDisposed == false");
            Check(!scenario.ClientEndpointA.IsDisposed, "OpenClient(A) 后未 Dispose：IsDisposed == false");
            Check(!scenario.ClientEndpointB.IsDisposed, "OpenClient(B) 后未 Dispose：IsDisposed == false");

            // ---- ② 服务端 Dispose：只置位自身 ---- 
            bool serverDisposeThrew = false;
            try { scenario.ServerEndpoint.Dispose(); }
            catch (Exception) { serverDisposeThrew = true; }
            Check(!serverDisposeThrew, "服务端 Dispose 不抛异常");
            Check(scenario.ServerEndpoint.IsDisposed, "服务端 Dispose 后 IsDisposed == true");
            Check(!scenario.ClientEndpointA.IsDisposed, "服务端 Dispose 不影响客户端 A 的 IsDisposed");
            Check(!scenario.ClientEndpointB.IsDisposed, "服务端 Dispose 不影响客户端 B 的 IsDisposed");

            // ---- ③ 幂等 Dispose ---- 
            bool serverSecondDisposeThrew = false;
            try { scenario.ServerEndpoint.Dispose(); }
            catch (Exception) { serverSecondDisposeThrew = true; }
            Check(!serverSecondDisposeThrew, "服务端重复 Dispose 不抛异常（幂等）");
            Check(scenario.ServerEndpoint.IsDisposed, "服务端重复 Dispose 后 IsDisposed 仍为 true");

            // ---- ④ 语义不变：已释放端点的 Pump 仍是 no-op ---- 
            long sentBefore = scenario.ServerEndpoint.DatagramsSent;
            long receivedBefore = scenario.ServerEndpoint.DatagramsReceived;
            bool pumpThrew = false;
            try { scenario.ServerEndpoint.Pump(_nowMs, _nowUnix); }
            catch (Exception) { pumpThrew = true; }
            Check(!pumpThrew, "已释放端点 Pump 不抛异常（原 _disposed 早退路径未变）");
            Check(scenario.ServerEndpoint.IsDisposed, "已释放端点 Pump 不翻转 IsDisposed（保持 true）");
            CheckEq(scenario.ServerEndpoint.DatagramsSent, sentBefore, "已释放端点 Pump 不再发送数据报（语义不变）");
            CheckEq(scenario.ServerEndpoint.DatagramsReceived, receivedBefore, "已释放端点 Pump 不再接收数据报（语义不变）");

            // ---- ⑤ 客户端端点生命周期 + 幂等 ---- 
            bool clientADisposeThrew = false;
            try { scenario.ClientEndpointA.Dispose(); }
            catch (Exception) { clientADisposeThrew = true; }
            Check(!clientADisposeThrew, "客户端 A Dispose 不抛异常");
            Check(scenario.ClientEndpointA.IsDisposed, "客户端 A Dispose 后 IsDisposed == true");
            Check(!scenario.ClientEndpointB.IsDisposed, "客户端 A Dispose 不影响客户端 B 的 IsDisposed");

            bool clientASecondDisposeThrew = false;
            try { scenario.ClientEndpointA.Dispose(); }
            catch (Exception) { clientASecondDisposeThrew = true; }
            Check(!clientASecondDisposeThrew, "客户端 A 重复 Dispose 不抛异常（幂等）");
            Check(scenario.ClientEndpointA.IsDisposed, "客户端 A 重复 Dispose 后 IsDisposed 仍为 true");

            bool clientBDisposeThrew = false;
            try { scenario.ClientEndpointB.Dispose(); }
            catch (Exception) { clientBDisposeThrew = true; }
            Check(!clientBDisposeThrew, "客户端 B Dispose 不抛异常");
            Check(scenario.ClientEndpointB.IsDisposed, "客户端 B Dispose 后 IsDisposed == true");

            // 两边都 Dispose 后，服务端端点的连接数仍为 0（Dispose 清空会话的原有语义不变）。
            CheckEq(scenario.ServerEndpoint.ConnectionCount, 0, "全部 Dispose 后服务端端点连接数仍为 0");
        }

        // =================================================================================
        //  E. 未认证 / 未激活闸门
        // =================================================================================

        private static void TestAdmissionGates()
        {
            // ── ① 连接级纵深闸门：未激活时字节不进 Transport、不 ack ──────
            WorldFixture server = CreateWorld(true);
            CaptureLink capture = new CaptureLink();
            string error;
            PMTransportConnection connection = CreateServerConnection(server, 1, UidA, capture, out error);

            byte[] datagram = CraftTransportDatagram(TestEpoch, 1);
            connection.OnDatagram(datagram, 0, datagram.Length);
            connection.Update(_nowMs);

            Check(!connection.IsActivated, "前置：该连接尚未激活");
            CheckEq(connection.InboundDroppedNotActivated, 1, "未激活时数据报被丢弃（计数 1）");
            CheckEq(connection.MessagesReceived, 0, "未激活时没有任何应用消息到达");
            CheckEq(connection.Transport.Stats.DatagramsReceived, 0, "**Transport 入站计数为 0**（字节没进 Transport）");
            CheckEq(connection.Transport.Stats.AcksSent, 0, "**Transport 没有发出任何 ack**");
            CheckEq(capture.Datagrams.Count, 0, "链路上没有产生任何出站数据报");

            // ── ② 正例对照：已激活连接上「携带消息」的数据报会被正常接收并 ack ──
            //   口径变化（本轮 ackloop 修复）：header-only 纯 ack 数据报按设计**不再**触发回程 ack，
            //   所以「真的会 ack」这条对照必须用**携带消息**的数据报来做，
            //   否则它会因为口径变化而退化成假红（旧断言）或假绿（无条件放宽）。
            WorldFixture liveServer = CreateWorld(true);
            CaptureLink liveCapture = new CaptureLink();
            PMTransportConnection live = CreateServerConnection(liveServer, 1, UidA, liveCapture, out error);
            Check(live.TryActivate(out error), "对照连接激活成功（" + (error ?? "ok") + "）");

            // ②-a header-only 纯 ack：字节合法（进 Transport、无解析错误、不丢），但不回 ack
            byte[] datagram2 = CraftTransportDatagram(TestEpoch, 1);
            live.OnDatagram(datagram2, 0, datagram2.Length);
            live.Update(_nowMs);

            CheckEq(live.InboundDroppedNotActivated, 0, "对照：已激活连接不丢字节");
            CheckEq(live.Transport.Stats.DatagramsReceived, 1, "对照：Transport 入站计数为 1（说明字节本身合法）");
            CheckEq(live.Transport.Stats.ParseErrors, 0, "对照：纯 ack 数据报不产生解析错误");
            CheckEq(liveCapture.Datagrams.Count, 0,
                    "对照：header-only 纯 ack 不再触发回程 ack（ackloop 修复的口径，防两端互相追赶）");

            // ②-b 携带消息（控制 Ping）：必须产生回程 ack ⇒ 证明「不回 ack」只针对纯 ack，
            //      而不是把 ack 通路整个关掉（否则上面的断言会变成无法反证的假绿）。
            byte[] ping2 = CraftTransportPing(TestEpoch, 2);
            live.OnDatagram(ping2, 0, ping2.Length);
            live.Update(_nowMs);

            CheckEq(live.Transport.Stats.DatagramsReceived, 2, "对照：携带消息的数据报同样进 Transport");
            CheckEq(live.Transport.Stats.KeepAlivesReceived, 1, "对照：Ping 被识别为 keepalive");
            CheckGt(liveCapture.Datagrams.Count, 0, "对照：携带消息的数据报产生了回程数据报（含 ack）");
            CheckGt(live.Transport.Stats.AcksSent + live.Transport.Stats.DatagramsSent, 0, "对照：真的回过 ack 口径非空");

            // ── ③ 端点级：未认证端点的数据报被丢弃且**无回包** ────────────
            TwoPlayerScenario scenario = OpenTwoPlayerScenario(false);
            byte[] boundPortDatagram = CraftTransportDatagram(TestEpoch, 7);

            RawPeer stranger = new RawPeer(scenario.ServerEndpoint.BoundPort);
            stranger.Send(boundPortDatagram);
            PumpFor(6, scenario.ServerEndpoint);

            CheckEq(scenario.ServerEndpoint.DroppedUnboundDatagrams, 1, "服务端丢弃未认证端点的数据报");
            CheckEq(scenario.ServerEndpoint.ConnectionCount, 0, "未认证数据报没有建出任何连接");
            CheckEq(scenario.ServerEndpoint.Ledger.Stats.Consumed, 0, "未认证数据报没有消费任何票据");
            CheckEq(scenario.ServerEndpoint.HandshakeServer.FrameRejections, 0, "该数据报根本没被当成握手帧处理");

            byte[] reply;
            Check(!stranger.TryReceive(200, out reply), "未认证数据报**没有得到任何回包**");
            stranger.Dispose();

            // ── ④ 客户端端点：非预期来源/激活前的字节被丢弃 ─────────────
            RawPeer fakeDs = new RawPeer(scenario.ServerEndpoint.BoundPort);
            PMDsEntryOffer offer = CreateOffer(scenario.TicketA, Identity(UidA, 1), "127.0.0.1", fakeDs.LocalPort);
            PMUdpSessionEndpoint client = PMUdpSessionEndpoint.OpenClient(offer, scenario.ClientA.Bridge,
                                                                          TestTransportConfig());

            byte[] fakeData = CraftTransportDatagram(TestEpoch, 3);
            // 注意：必须发到**客户端自己的临时端口**，而来源端口必须等于客户端期待的 DS 端点端口
            // （offer.Port = fakeDs 的本地端口）。两者不是同一个端口，混用会测成「客户端什么都没收到」。
            fakeDs.SendTo(fakeData, client.BoundPort);
            PumpFor(4, client);

            CheckEq(client.DatagramsReceived, 1, "客户端确实收到了该数据报（前置）");
            CheckEq(client.DroppedPreActivationDatagrams, 1, "客户端在激活前丢弃非握手字节（**不进 Transport**）");
            Check(client.ClientConnection == null, "客户端仍未激活");
            CheckEq(client.ConnectionCount, 0, "客户端没有任何就绪连接");
            Check(!client.ClientFailed, "丢弃非握手字节不算失败（继续等 ServerHello）");

            long preActivationDrops = client.DroppedPreActivationDatagrams;
            CheckEq(preActivationDrops, 1, "计数保持可归因");

            client.Dispose();

            // 换一个来源端口：必须被判为「非预期来源」
            PMUdpSessionEndpoint client2 = PMUdpSessionEndpoint.OpenClient(offer, scenario.ClientB.Bridge,
                                                                           TestTransportConfig());
            RawPeer foreign = new RawPeer(scenario.ServerEndpoint.BoundPort);
            foreign.SendTo(fakeData, client2.BoundPort);
            PumpFor(4, client2);

            CheckEq(client2.DatagramsReceived, 1, "客户端 2 确实收到了该数据报（前置）");
            CheckEq(client2.DroppedForeignDatagrams, 1, "非预期来源的字节被丢弃（计数非空）");
            CheckEq(client2.DroppedPreActivationDatagrams, 0, "非预期来源不计入「激活前数据」");
            foreign.Dispose();
            client2.Dispose();

            scenario.ServerEndpoint.Dispose();
        }

        // =================================================================================
        //  F. 票据负例
        // =================================================================================

        private static void TestTicketNegatives()
        {
            TwoPlayerScenario scenario = OpenTwoPlayerScenario(false);
            int port = scenario.ServerEndpoint.BoundPort;

            // ── 正例对照（原始 UDP + 生产客户端状态机）：应答不大于请求 ───
            RawPeer good = new RawPeer(port);
            PMDsEntryOffer offer = CreateOffer(scenario.TicketA, Identity(UidA, 1), "127.0.0.1", port);
            PMHandshakeClient client = new PMHandshakeClient(offer);
            byte[] hello = client.BuildClientHello();
            good.Send(hello);
            PumpFor(6, scenario.ServerEndpoint);

            byte[] response;
            Check(good.TryReceive(500, out response), "正例：收到 ServerHello");
            if (response != null)
            {
                Check(response.Length <= hello.Length,
                      "**成功应答不大于请求**（响应 " + response.Length + "B ≤ 请求 " + hello.Length + "B）");

                PMHandshakeServerHello parsed;
                string parseError;
                Check(PMHandshakeCodec.TryReadServerHello(response, 0, response.Length, out parsed, out parseError),
                      "正例：ServerHello 严格可解析（" + (parseError ?? "ok") + "）");

                if (parsed.MatchId != null)
                {
                    Check(Equal(parsed.ClientNonce, client.LastNonce), "正例：nonce 回带一致");
                    Check(Equal(parsed.TicketDigest, client.TicketDigest), "正例：票据 SHA-256 一致");
                    CheckStrEq(parsed.MatchId, TestMatchId, "正例：对局一致");
                    CheckStrEq(parsed.DsId, TestDsId, "正例：DsId 一致");
                    CheckEq(parsed.Epoch, TestEpoch, "正例：世代一致");
                    CheckEq(parsed.ProtocolHash, TestProtocolHash, "正例：摘要一致");
                    CheckEq(parsed.CollisionDigest, TestCollisionDigest, "正例：碰撞摘要一致");
                    CheckEq(parsed.Uid, UidA, "正例：身份一致");
                }

                PMSessionBinding binding;
                string handleError;
                Check(client.Handle(response, 0, response.Length, out binding, out handleError)
                      == PMHandshakeClientOutcome.Accepted,
                      "正例：客户端关联校验通过（" + (handleError ?? "ok") + "）");
                CheckEq(binding.Uid, UidA, "正例：绑定 uid 正确");
            }

            CheckEq(scenario.ServerEndpoint.ConnectionCount, 1, "正例：服务端建立 1 条连接");
            CheckEq(scenario.ServerEndpoint.HandshakeServer.Accepted, 1, "正例：握手接受计数 1");

            // ── 幂等重试：同票同端点再发一次 ⇒ 再回一次、不新建连接 ───────
            long allocatedBefore = scenario.ServerEndpoint.Ledger.Stats.ConnectionsAllocated;
            byte[] hello2 = client.BuildClientHello();
            good.Send(hello2);
            PumpFor(6, scenario.ServerEndpoint);

            byte[] response2;
            Check(good.TryReceive(500, out response2), "重试：再次收到 ServerHello");
            CheckEq(scenario.ServerEndpoint.ConnectionCount, 1, "重试：连接数仍为 1（不新建）");
            CheckEq(scenario.ServerEndpoint.Ledger.Stats.ConnectionsAllocated, allocatedBefore, "重试：没有分配新 ConnectionId");
            CheckGt(scenario.ServerEndpoint.HandshakeRetries, 0, "重试：幂等重试计数非空");

            // ── ① 错 MAC ────────────────────────────────────────────────
            byte[] tamperedTicket = (byte[])scenario.TicketB.Clone();
            tamperedTicket[tamperedTicket.Length - 1] ^= 0xFF;      // 改 MAC 末字节
            CheckNoReply(scenario, "错 MAC",
                         BuildRawClientHello(tamperedTicket, TestProtocolHash, TestEpoch, 64));

            // ── ② 错客户端声明摘要（廉价预检）────────────────────────────
            long cheapBefore = scenario.ServerEndpoint.HandshakeServer.CheapRejections;
            CheckNoReply(scenario, "错声明摘要",
                         BuildRawClientHello(scenario.TicketB, TestProtocolHash ^ 0x5A5Au, TestEpoch, 64));
            CheckGt(scenario.ServerEndpoint.HandshakeServer.CheapRejections, cheapBefore,
                    "错声明摘要被廉价预检拒绝（未花 MAC 的钱）");

            // ── ③ 错声明世代（廉价预检）─────────────────────────────────
            CheckNoReply(scenario, "错声明世代",
                         BuildRawClientHello(scenario.TicketB, TestProtocolHash, TestEpoch + 1u, 64));

            // ── ④ 错局：同一把密钥签发的、但绑定另一个对局的票据 ──────────
            TicketAuthority otherMatch = CreateAuthority("other-match-r3b", TestProtocolHash, _nowUnix);
            byte[] otherMatchTicket = IssueTicket(otherMatch, Identity(UidB, 2), 120);
            CheckNoReply(scenario, "错局票据",
                         BuildRawClientHello(otherMatchTicket, TestProtocolHash, TestEpoch, 64));

            // ── ⑤ 错摘要：票据绑定另一个 ProtocolHash ────────────────────
            TicketAuthority otherHash = CreateAuthority(TestMatchId, TestProtocolHash ^ 0x1234u, _nowUnix);
            byte[] otherHashTicket = IssueTicket(otherHash, Identity(UidB, 2), 120);
            CheckNoReply(scenario, "错摘要票据",
                         BuildRawClientHello(otherHashTicket, TestProtocolHash, TestEpoch, 64));

            // ── ⑥ 名册外的身份（MAC 合法但不在本局名册）─────────────────
            TicketAuthority sameKey = scenario.Authority;
            byte[] outsiderTicket = IssueTicket(sameKey, Identity(999, 99), 120);
            CheckNoReply(scenario, "名册外身份",
                         BuildRawClientHello(outsiderTicket, TestProtocolHash, TestEpoch, 64));

            // ── ⑦ 第二端点：同一张**已绑定**票据换来源端口 ───────────────
            // 注意必须用已消费过的票（正例里绑定的 ticketA）：未绑定过的票从新端点入场是
            // **合法首次入场**，不是重放——那正是「同票换端点」与「新票新端点」的区别所在。
            CheckNoReply(scenario, "第二端点",
                         BuildRawClientHello(scenario.TicketA, TestProtocolHash, TestEpoch, 64));
            CheckGt(scenario.ServerEndpoint.Ledger.Stats.RejectedDigestRebound, 0, "第二端点被账本拒绝（DigestRebound）");

            // ── ⑧ 帧超限 / 垃圾帧 ───────────────────────────────────────
            byte[] oversized = new byte[PMHandshakeCodec.MaxFrameBytes + 1];
            oversized[0] = (byte)'P';
            oversized[1] = (byte)'M';
            oversized[2] = (byte)'H';
            oversized[3] = (byte)'S';
            oversized[4] = (byte)PMHandshakeCodec.FrameVersion;
            oversized[5] = (byte)PMHandshakeKind.ClientHello;
            CheckNoReply(scenario, "握手帧超限", oversized);

            byte[] garbage = new byte[32];
            for (int i = 0; i < garbage.Length; i++) { garbage[i] = (byte)(i * 7); }
            CheckNoReply(scenario, "无 magic 的垃圾帧", garbage);

            CheckEq(scenario.ServerEndpoint.ConnectionCount, 1, "负例期间连接数始终为 1（从未新建）");
            CheckGt(scenario.ServerEndpoint.HandshakeRejections, 0, "服务端握手拒绝计数非空");
            CheckGt(scenario.ServerEndpoint.HandshakeServer.VerifyFailures, 0, "逐身份验票失败计数非空");
            CheckGt(scenario.ServerEndpoint.HandshakeServer.FrameRejections, 0, "帧级拒绝计数非空");
            CheckEq(scenario.ServerEndpoint.HandshakeServer.AmplificationRefusals, 0, "没有回包因放大被拒（失败本就无回包）");
            Check(!scenario.Server.HasWarning("票据"), "服务端告警里没有票据字样");

            good.Dispose();
            scenario.ServerEndpoint.Dispose();
            _ = sameKey;
        }

        /// <summary>注入一条握手帧，断言「无回包 + 无新连接」。</summary>
        private static void CheckNoReply(TwoPlayerScenario scenario, string what, byte[] frame)
        {
            int connectionsBefore = scenario.ServerEndpoint.ConnectionCount;
            long consumedBefore = scenario.ServerEndpoint.Ledger.Stats.Consumed;

            RawPeer peer = new RawPeer(scenario.ServerEndpoint.BoundPort);
            peer.Send(frame);
            PumpFor(5, scenario.ServerEndpoint);

            byte[] reply;
            bool gotReply = peer.TryReceive(150, out reply);

            Check(!gotReply, what + "：**无回包**（契约 §7.2）");
            CheckEq(scenario.ServerEndpoint.ConnectionCount, connectionsBefore, what + "：没有新建连接");
            CheckEq(scenario.ServerEndpoint.Ledger.Stats.Consumed, consumedBefore, what + "：没有消费任何票据");

            peer.Dispose();
        }

        // =================================================================================
        //  G. 断开墓碑 / 空闲超时 / 票据过期不影响已激活会话
        // =================================================================================

        private static void TestLifecycleAndExpiry()
        {
            // ── ① 断开 ⇒ 墓碑 ⇒ 旧票换端点也无法入场 ─────────────────────
            TwoPlayerScenario scenario = OpenTwoPlayerScenario(false);
            ConnectClient(scenario, true);
            ConnectClient(scenario, false);

            bool connected = PumpUntil(
                delegate
                {
                    return scenario.ServerEndpoint.ConnectionCount == 2
                        && scenario.ClientEndpointA.ClientConnection != null
                        && scenario.ClientEndpointB.ClientConnection != null;
                },
                400,
                scenario.ServerEndpoint, scenario.ClientEndpointA, scenario.ClientEndpointB);

            Check(connected, "断开用例：两名玩家先完成入局");

            // 断开玩家 A 的服务端连接（等价于「连接被协议错误/超时打断」）。
            PMTransportConnection connA = scenario.Server.Bridge.FindConnectionByUid(UidA);
            Check(connA != null, "断开用例：找到玩家 A 的连接");
            if (connA != null)
            {
                connA.Disconnect(PMDisconnectReason.ProtocolError);
            }

            PumpFor(6, scenario.ServerEndpoint, scenario.ClientEndpointA, scenario.ClientEndpointB);

            CheckEq(scenario.ServerEndpoint.ConnectionCount, 1, "断开后服务端只剩 1 条连接");
            CheckEq(scenario.ServerEndpoint.SessionsClosed, 1, "会话关闭计数 1");
            CheckEq(scenario.ServerEndpoint.Ledger.Stats.Tombstones, 1, "断开产生了消费墓碑");
            CheckGt(scenario.Server.FailedEvents.Count, 0, "断开触发 Failed 事件（非空）");

            // 旧票换端点 ⇒ 墓碑拒绝（无回包）
            CheckNoReply(scenario, "断开后旧票换端点",
                         BuildRawClientHello(scenario.TicketA, TestProtocolHash, TestEpoch, 64));
            CheckGt(scenario.ServerEndpoint.Ledger.Stats.RejectedTombstoned, 0, "墓碑拒绝计数非空（RejectedTombstoned）");

            // Lobby 显式重发新票 ⇒ 可以重新入场（uid 与端点坑位都已归还）
            byte[] reissuedA = ReissueTicket(scenario.Authority, Identity(UidA, 1), 120);
            RawPeer rejoin = new RawPeer(scenario.ServerEndpoint.BoundPort);
            PMDsEntryOffer rejoinOffer = CreateOffer(reissuedA, Identity(UidA, 1), "127.0.0.1",
                                                     scenario.ServerEndpoint.BoundPort);
            PMHandshakeClient rejoinClient = new PMHandshakeClient(rejoinOffer);
            rejoin.Send(rejoinClient.BuildClientHello());
            PumpFor(6, scenario.ServerEndpoint);

            byte[] rejoinReply;
            Check(rejoin.TryReceive(500, out rejoinReply), "新票重连：收到 ServerHello");
            CheckEq(scenario.ServerEndpoint.ConnectionCount, 2, "新票重连：连接数回到 2");
            rejoin.Dispose();

            // ── ② 端点级空闲超时 ⇒ 会话被回收并留下墓碑 ─────────────────
            TwoPlayerScenario idle = OpenTwoPlayerScenario(false);
            // 重新开一个「短空闲超时」的服务端端点（同一 boot，但独立端口）。
            idle.ServerEndpoint.Dispose();
            idle.ServerEndpoint = PMUdpSessionEndpoint.OpenServer(idle.Boot, idle.Server.Bridge, "127.0.0.1", 0,
                                                                  TestTransportConfig(), 200);
            ConnectClient(idle, true);

            bool idleConnected = PumpUntil(
                delegate { return idle.ServerEndpoint.ConnectionCount == 1; },
                400,
                idle.ServerEndpoint, idle.ClientEndpointA);

            Check(idleConnected, "空闲超时用例：先完成入局");
            long closedBefore = idle.ServerEndpoint.SessionsClosed;

            // 推进墙钟（nowMs）越过 200ms 空闲窗口；期间不产生任何业务流量。
            _nowMs += 500L;
            PumpFor(3, idle.ServerEndpoint);

            CheckGt(idle.ServerEndpoint.SessionsClosed, closedBefore, "空闲超时后会话被回收（计数增长）");
            CheckEq(idle.ServerEndpoint.ConnectionCount, 0, "空闲超时后连接数归零");
            CheckGt(idle.ServerEndpoint.Ledger.Stats.Tombstones, 0, "空闲超时同样留下消费墓碑");

            idle.ClientEndpointA.Dispose();
            idle.ServerEndpoint.Dispose();

            // ── ③ 「120 秒票据过期」不得影响已激活会话 ──────────────────
            TwoPlayerScenario expiry = OpenTwoPlayerScenario(false);
            ConnectClient(expiry, true);
            ConnectClient(expiry, false);

            bool expiryConnected = PumpUntil(
                delegate
                {
                    return expiry.ServerEndpoint.ConnectionCount == 2
                        && expiry.ClientEndpointA.ClientConnection != null
                        && expiry.ClientEndpointB.ClientConnection != null;
                },
                400,
                expiry.ServerEndpoint, expiry.ClientEndpointA, expiry.ClientEndpointB);

            Check(expiryConnected, "过期用例：两名玩家先完成入局");

            long savedUnix = _nowUnix;
            _nowUnix = savedUnix + 200L;    // 越过 120 秒票据有效期

            PumpFor(8, expiry.ServerEndpoint, expiry.ClientEndpointA, expiry.ClientEndpointB);

            CheckEq(expiry.ServerEndpoint.ConnectionCount, 2, "票据过期后**已激活会话仍保持**（2 条）");
            CheckEq(expiry.ServerEndpoint.Ledger.ActiveBindingCount, 2, "账本仍保有 2 个活跃绑定");
            Check(expiry.ServerEndpoint.ClientConnection == null, "服务端端点没有客户端连接槽位（对照）");

            PMTransportConnection stillA = expiry.Server.Bridge.FindConnectionByUid(UidA);
            Check(stillA != null && stillA.IsReady, "玩家 A 的连接在票据过期后仍就绪");
            Check(expiry.ClientEndpointA.ClientConnection.IsReady, "客户端 A 的连接在票据过期后仍就绪");

            // 到期只影响「新的入场/重试」：同一张已过期票据从新端点入场被拒。
            long verifyBefore = expiry.ServerEndpoint.HandshakeServer.VerifyFailures;
            CheckNoReply(expiry, "已过期票据的新入场",
                         BuildRawClientHello(expiry.TicketA, TestProtocolHash, TestEpoch, 64));
            CheckGt(expiry.ServerEndpoint.HandshakeServer.VerifyFailures, verifyBefore,
                    "过期票据在验票阶段被拒（新入场受影响）");

            // 数据仍然流动（会话没被打断）
            NetActor actor = new NetActor();
            actor.Health = 33;
            Check(expiry.Server.World.Spawn(actor, TestClassId), "过期用例：Spawn actor");
            actor.OwnerConnection = stillA;
            expiry.Server.Bridge.RegisterReplicatedObject(actor);

            bool stillFlows = PumpUntil(
                delegate { return Find(expiry.ClientA, actor.NetId) != null; },
                400,
                expiry.ServerEndpoint, expiry.ClientEndpointA, expiry.ClientEndpointB);

            Check(stillFlows, "票据过期后数据仍然流动（客户端拿到 Create）");

            expiry.ClientEndpointA.Dispose();
            expiry.ClientEndpointB.Dispose();
            expiry.ServerEndpoint.Dispose();

            scenario.ClientEndpointA.Dispose();
            scenario.ClientEndpointB.Dispose();
            scenario.ServerEndpoint.Dispose();
            _nowUnix = savedUnix;
        }

        // =================================================================================
        //  I. 主集成复核（R3-B 复核轮）：逐项风险的真实复现/回归
        // =================================================================================

        /// <summary>
        /// 本轮独立风险复核的可执行证据集（与 `Docs/plans/_r3b_network_review.md` 对应）：
        ///   ① 墓碑容量淘汰不得让仍有效的已消费旧票换端点复用（已在账本段回归）；
        ///   ② 摘要数组所有权（已在账本段回归）；
        ///   ③ 重复 ClientHello ⇒ Connected/Spawn 只一次；
        ///   ④ 同端点第二张有效票据不得扰动已认证活连接；
        ///   ⑤ 超长/垃圾数据报不得中断端点进度；已认证连接收到畸形数据报不得让 Update 抛出。
        /// </summary>
        private static void TestReviewFindings()
        {
            // ── ① 重复 ClientHello（ServerHello 尚未处理时客户端重试）──────
            //   断言口径：只建立 1 条连接、只分配 1 个 ConnectionId、Connected/Spawn 只一次。
            TwoPlayerScenario dup = OpenTwoPlayerScenario(false);
            int spawned = 0;
            dup.ServerEndpoint.Connected += delegate(PMTransportConnection c)
            {
                NetActor actor = new NetActor();
                actor.Health = 5;
                if (dup.Server.World.Spawn(actor, TestClassId))
                {
                    spawned++;
                }

                actor.OwnerConnection = c;
                dup.Server.Bridge.RegisterReplicatedObject(actor);
            };

            RawPeer retry = new RawPeer(dup.ServerEndpoint.BoundPort);
            PMHandshakeClient retryClient = new PMHandshakeClient(
                CreateOffer(dup.TicketA, Identity(UidA, 1), "127.0.0.1", dup.ServerEndpoint.BoundPort));
            retry.Send(retryClient.BuildClientHello());
            retry.Send(retryClient.BuildClientHello());
            PumpFor(8, dup.ServerEndpoint);

            CheckEq(dup.ServerEndpoint.ConnectionCount, 1, "重复 ClientHello：只建立 1 条连接");
            CheckEq(dup.ServerEndpoint.Ledger.Stats.ConnectionsAllocated, 1, "重复 ClientHello：只分配 1 个 ConnectionId");
            CheckEq(dup.Server.ConnectedEvents.Count, 1, "重复 ClientHello：Connected 事件只触发一次");
            CheckEq(spawned, 1, "重复 ClientHello：权威对象只 Spawn 一次（不重复建立）");
            CheckGt(dup.ServerEndpoint.HandshakeRetries, 0, "第二次 ClientHello 被判为幂等重试");
            CheckEq(dup.ServerEndpoint.HandshakeServer.AmplificationRefusals, 0, "幂等重试路径没有放大拒发");

            // 清空上文留下的回包（两次 Hello ⇒ 两条 ServerHello），否则②的「无回包」会读到它们。
            byte[] leftover;
            while (retry.TryReceive(1, out leftover))
            {
                leftover = null;
            }

            // ── ② 同端点第二张有效票据：不得扰动已认证活连接 ──────────────
            //   同一 source endpoint 先入局 uidA，再用**另一张名册内的有效票**（uidB）
            //   从同一端点发 ClientHello：必须无回包、不新建、活连接不受影响
            //   （UidBusy/EndpointTaken 只驱动拒绝，绝不摘除既有绑定）。
            PMTransportConnection liveA = dup.Server.Bridge.FindConnectionByUid(UidA);
            Check(liveA != null && liveA.IsReady, "同端点用例：活连接已就绪");

            long inboundBefore = liveA != null ? liveA.Transport.Stats.DatagramsReceived : 0L;
            long rejectedBefore = dup.ServerEndpoint.Ledger.Stats.RejectedEndpointTaken
                                + dup.ServerEndpoint.Ledger.Stats.RejectedUidBusy;

            PMHandshakeClient second = new PMHandshakeClient(
                CreateOffer(dup.TicketB, Identity(UidB, 2), "127.0.0.1", dup.ServerEndpoint.BoundPort));
            // 注意：必须来自**同一个** source endpoint 才叫「同端点」；因此复用 retry 这个 socket。
            retry.Send(second.BuildClientHello());            PumpFor(6, dup.ServerEndpoint);

            byte[] sameEndpointReply;
            Check(!retry.TryReceive(200, out sameEndpointReply), "同端点第二张有效票：**无回包**");
            CheckEq(dup.ServerEndpoint.ConnectionCount, 1, "同端点第二张有效票：没有新建连接");
            CheckEq(dup.ServerEndpoint.Ledger.ActiveBindingCount, 1, "同端点第二张有效票：账本仍只有 1 个活跃绑定");
            CheckGt(dup.ServerEndpoint.Ledger.Stats.RejectedEndpointTaken
                    + dup.ServerEndpoint.Ledger.Stats.RejectedUidBusy, rejectedBefore,
                    "同端点第二张有效票被账本拒绝（EndpointTaken/UidBusy）");
            Check(liveA != null && liveA.IsReady, "活连接在恶意 Hello 之后仍就绪");
            CheckEq(dup.ServerEndpoint.Ledger.ActiveBindingCount, 1, "活连接的账本绑定没有被摘除");

            // 活连接上数据仍能流动（不是「连接还在但已死」）。
            retry.Send(CraftTransportDatagram(TestEpoch, 11));
            PumpFor(4, dup.ServerEndpoint);
            CheckGt(liveA != null ? liveA.Transport.Stats.DatagramsReceived : 0L, inboundBefore,
                    "恶意 Hello 之后活连接仍能收数据（未被摘除/未被封死）");
            Check(liveA != null && liveA.Transport.IsConnected, "活连接的传输层仍连通");

            retry.Dispose();
            dup.ServerEndpoint.Dispose();

            // ── ③ 超长/垃圾数据报不得中断端点进度 ────────────────────────
            TwoPlayerScenario noisy = OpenTwoPlayerScenario(false);
            RawPeer noise = new RawPeer(noisy.ServerEndpoint.BoundPort);

            byte[] huge = new byte[3000];
            huge[0] = 1;                       // 传输层版本，但不是握手帧；且远超接收缓冲
            noise.Send(huge);

            byte[] junk = new byte[64];
            junk[0] = 1;                       // 版本对、世代/序号错 ⇒ 被当未认证字节丢弃
            noise.Send(junk);
            PumpFor(3, noisy.ServerEndpoint);

            // 紧接着一条**合法**握手：必须仍被接纳（单个畸形/超长包不得中断端点进度）。
            PMHandshakeClient noisyClient = new PMHandshakeClient(
                CreateOffer(noisy.TicketA, Identity(UidA, 1), "127.0.0.1", noisy.ServerEndpoint.BoundPort));
            byte[] noisyHello = noisyClient.BuildClientHello();
            noise.Send(noisyHello);
            PumpFor(8, noisy.ServerEndpoint);

            byte[] noisyReply;
            Check(noise.TryReceive(500, out noisyReply), "超长/垃圾数据报之后，合法握手仍被接纳（端点进度未中断）");
            CheckEq(noisy.ServerEndpoint.ConnectionCount, 1, "超长/垃圾数据报之后仍能建立 1 条连接");
            CheckEq(noisy.ServerEndpoint.UnexpectedErrors, 0, "超长/垃圾数据报没有产生未预期异常");

            noise.Dispose();
            noisy.ServerEndpoint.Dispose();

            // ── ④ 已认证连接收到畸形数据报：核心侧长度守卫，不得让 Transport.Update 抛出 ──
            //   现实可达路径：已认证玩家发一条 19 字节数据报（固定头 18 字节 + messageCount=1，
            //   但消息体缺失）。核心 Transport 的消息循环在读完 flags 后就直接读 stream 字节，
            //   旧实现会在 pkt[19] 抛 IndexOutOfRangeException；若它冒泡出 Pump，同一帧**所有**端点的
            //   收包/握手/flush 都会被中断。
            //   现口径：核心自己补齐长度守卫，把这种数据报当成**解析错误**有界拒掉，不抛、不断连、
            //   不误执行（会话层那把「异常 → 本连接协议错误」的兜底仍然保留，但不再由这条已知读越界触发——
            //   见 Tools/PMNetSessionTest 的 M4）。
            WorldFixture parseServer = CreateWorld(true);
            CaptureLink parseCapture = new CaptureLink();
            string parseError;
            PMTransportConnection parseConn = CreateServerConnection(parseServer, 1, UidA, parseCapture, out parseError);
            Check(parseConn.TryActivate(out parseError), "解析健壮性用例：连接激活（" + (parseError ?? "ok") + "）");

            byte[] truncated = new byte[19];
            int tp = 0;
            truncated[tp++] = 1;                      // 协议版本
            WriteU32(truncated, ref tp, TestEpoch);   // 世代正确（通过世代门）
            WriteU16(truncated, ref tp, 1);           // packetId
            WriteU16(truncated, ref tp, 0);           // ackId
            for (int i = 0; i < 8; i++) { truncated[tp++] = 0; }
            truncated[tp++] = 1;                      // messageCount = 1（其后再无字节）
            CheckEq(tp, 18, "构造出的数据报 = 固定头 18 字节（messageCount 在最后一字节）");
            CheckEq(truncated.Length, 19, "只剩 1 字节 flags、缺 stream 字节 ⇒ 19 字节");

            bool threw = false;
            try
            {
                parseConn.OnDatagram(truncated, 0, truncated.Length);
                parseConn.Update(_nowMs);
            }
            catch (Exception)
            {
                threw = true;
            }

            Check(!threw, "单条畸形数据报不得让 Transport.Update 抛出（否则宿主 Pump 被中断）");
            CheckGt(parseConn.Transport.Stats.ParseErrors, 0L,
                    "核心侧把畸形数据报记为解析错误（有界拒绝，不静默吞掉）");
            CheckEq(parseConn.TransportUpdateFaults, 0L,
                    "核心自己判长度 ⇒ 不再走「解析异常 → 会话层兜底」");
            CheckEq(parseConn.ProtocolErrors, 0L, "那条畸形数据报不算应用层协议错误");
            CheckEq(parseConn.MessagesReceived, 0L, "畸形数据报没有被当成消息派发（不误执行）");
            Check(parseConn.IsReady, "本机噪声不断开健康连接（只记解析错误）");
            CheckEq(parseServer.World.ConnectionCount, 1,
                    "世界侧登记保持不变（没有再被成对摘除）");

            // 对照：同一条连接上补一个**完整合法**的数据报，必须仍能继续进展
            //   （证明上一条畸形包的处置不是「静默吞掉后把连接卡死」）。
            byte[] followUp = CraftTransportDatagram(TestEpoch, 2);
            long recvBeforeFollow = parseConn.Transport.Stats.DatagramsReceived;
            long parseBeforeFollow = parseConn.Transport.Stats.ParseErrors;
            long faultsBeforeFollow = parseConn.TransportUpdateFaults;
            bool followThrew = false;
            try
            {
                parseConn.OnDatagram(followUp, 0, followUp.Length);
                parseConn.Update(_nowMs);
            }
            catch (Exception)
            {
                followThrew = true;
            }

            Check(!followThrew, "对照：紧随其后的合法数据报不抛");
            CheckEq(parseConn.Transport.Stats.DatagramsReceived, recvBeforeFollow + 1,
                    "对照：合法数据报进入 Transport（入站计数 +1，连接还能进展）");
            CheckEq(parseConn.Transport.Stats.ParseErrors, parseBeforeFollow,
                    "对照：合法数据报不计解析错误");
            CheckEq(parseConn.TransportUpdateFaults, faultsBeforeFollow,
                    "对照：合法数据报不触发会话层兜底");
            Check(parseConn.IsReady, "对照：补正常包后连接仍就绪");

            // 对照：同一条畸形数据报不会让**另一条**已认证连接的进度受影响。
            WorldFixture peerServer = CreateWorld(true);
            CaptureLink peerCapture = new CaptureLink();
            PMTransportConnection peerConn = CreateServerConnection(peerServer, 2, UidB, peerCapture, out parseError);
            Check(peerConn.TryActivate(out parseError), "解析健壮性用例：第二条连接激活");
            byte[] wellFormed = CraftTransportDatagram(TestEpoch, 3);
            bool peerThrew = false;
            try
            {
                peerConn.OnDatagram(wellFormed, 0, wellFormed.Length);
                peerConn.Update(_nowMs);
            }
            catch (Exception)
            {
                peerThrew = true;
            }

            Check(!peerThrew, "对照：正常数据报仍可正常处理");
            CheckEq(peerConn.Transport.Stats.DatagramsReceived, 1, "对照：正常数据报进入 Transport");
            Check(peerConn.IsReady, "对照：另一条连接仍就绪");
        }

        // =================================================================================
        //  H. 边界
        // =================================================================================

        private static void TestBoundaries()
        {
            TwoPlayerScenario scenario = OpenTwoPlayerScenario(false);
            int port = scenario.ServerEndpoint.BoundPort;

            // ── 握手帧上限：契约 §7.2 冻结 1200 字节 ─────────────────────
            CheckEq(PMHandshakeCodec.MaxFrameBytes, 1200, "握手帧上限为契约冻结的 1200 字节");

            // 合法 ClientHello 的「最大形态」：票据 + 填充都取上限 ⇒ 可解析、可被接受
            byte[] maxValid = BuildRawClientHello(scenario.TicketA, TestProtocolHash, TestEpoch,
                                                  PMHandshakeCodec.MaxPaddingBytes);
            PMHandshakeClientHello parsedHello;
            string parseError;
            Check(PMHandshakeCodec.TryReadClientHello(maxValid, 0, maxValid.Length, out parsedHello, out parseError),
                  "最大合法握手帧可解析（" + maxValid.Length + "B）：" + (parseError ?? "ok"));
            Check(maxValid.Length <= PMHandshakeCodec.MaxFrameBytes, "最大合法握手帧仍在上限内");
            CheckEq(parsedHello.PaddingBytes, PMHandshakeCodec.MaxPaddingBytes, "最大填充被完整读回");

            RawPeer peer = new RawPeer(port);
            peer.Send(maxValid);
            PumpFor(6, scenario.ServerEndpoint);

            byte[] reply;
            Check(peer.TryReceive(500, out reply), "最大合法握手帧被接受并回包");
            if (reply != null)
            {
                Check(reply.Length <= maxValid.Length,
                      "应答不大于请求（" + reply.Length + " ≤ " + maxValid.Length + "）");
                CheckEq(scenario.ServerEndpoint.ConnectionCount, 1, "最大合法握手帧建立了 1 条连接");
            }

            // ── 正好等于帧上限、但填充超限 ⇒ 拒绝且无回包（有界输入）───
            int baseSize = PMHandshakeCodec.HeaderBytes
                + PMHandshakeCodec.VarintSize(scenario.TicketA.Length) + scenario.TicketA.Length
                + PMHandshakeCodec.NonceBytes
                + PMHandshakeCodec.VarintSize(TestProtocolHash)
                + PMHandshakeCodec.VarintSize(TestEpoch);

            int padding = PMHandshakeCodec.MaxFrameBytes - baseSize;
            while (baseSize + PMHandshakeCodec.VarintSize(padding) + padding > PMHandshakeCodec.MaxFrameBytes)
            {
                padding--;
            }

            byte[] atLimit = BuildUncappedClientHello(scenario.TicketA, TestProtocolHash, TestEpoch, padding);
            CheckEq(atLimit.Length, PMHandshakeCodec.MaxFrameBytes, "构造出的握手帧正好等于上限（" + atLimit.Length + "B）");
            Check(padding > PMHandshakeCodec.MaxPaddingBytes, "该帧的填充确实超过填充上限（用于验证有界拒绝）");
            Check(!PMHandshakeCodec.TryReadClientHello(atLimit, 0, atLimit.Length, out parsedHello, out parseError),
                  "填充超限的「等于上限」帧被拒（不因为总长在上限内就放行）");

            // ── 上限 +1 ⇒ 拒绝且无回包 ──────────────────────────────────
            byte[] aboveLimit = new byte[PMHandshakeCodec.MaxFrameBytes + 1];
            Buffer.BlockCopy(maxValid, 0, aboveLimit, 0, maxValid.Length);
            aboveLimit[PMHandshakeCodec.MaxFrameBytes] = 0;

            RawPeer tooBig = new RawPeer(port);
            long framesBefore = scenario.ServerEndpoint.HandshakeServer.FrameRejections;
            tooBig.Send(aboveLimit);
            PumpFor(5, scenario.ServerEndpoint);

            byte[] bigReply;
            Check(!tooBig.TryReceive(150, out bigReply), "上限 +1 的握手帧**无回包**");
            CheckGt(scenario.ServerEndpoint.HandshakeServer.FrameRejections, framesBefore, "上限 +1 被记为帧级拒绝");
            CheckEq(scenario.ServerEndpoint.ConnectionCount, 1, "上限 +1 没有新建连接");

            // ── 传输层数据报上限：>1200 的数据报不会进入握手路径 ─────────
            long dataBefore = scenario.ServerEndpoint.DroppedUnboundDatagrams;
            RawPeer bigData = new RawPeer(port);
            byte[] bigDatagram = new byte[1300];
            bigDatagram[0] = 1;                       // 传输层版本（不是握手 magic）
            bigData.Send(bigDatagram);
            PumpFor(5, scenario.ServerEndpoint);

            CheckGt(scenario.ServerEndpoint.DroppedUnboundDatagrams, dataBefore,
                    "超限数据报被当作未认证字节丢弃（不进 Transport）");
            byte[] dataReply;
            Check(!bigData.TryReceive(150, out dataReply), "超限数据报没有任何回包");

            // ── offer 文本上限 ──────────────────────────────────────────
            string oversize = PMDsEntryCodec.Prefix + new string('A', PMDsEntryCodec.MaxTextBytes);
            PMDsEntryOffer parsedOffer;
            string error;
            Check(!PMDsEntryCodec.TryDecode(oversize, out parsedOffer, out error), "超长 offer 文本被拒（严格上限）");

            peer.Dispose();
            tooBig.Dispose();
            bigData.Dispose();
            scenario.ServerEndpoint.Dispose();
        }
    }
}
