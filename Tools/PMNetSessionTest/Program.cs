using System;
using System.Collections.Generic;
using PMNet;
using PMNet.Session;
using PMNet.Transport;

namespace PMNetSessionTest
{
    /// <summary>
    /// R3-A2 验收：已认证连接的网络适配（Session 层）。
    ///
    /// 事实来源：`Docs/plans/net-r3-control-contract.md` §2/§4/§5、
    /// `Docs/plans/net-r0-contract.md` §2.2/§2.7/§4/§5、`Docs/plans/_r2_fix_rpc.md` §2.6/§4、
    /// `Docs/plans/_r2_fix_replication.md` §4/§7。
    ///
    /// 覆盖 R3A3（会话接线：Create/RPC/属性/ACK/断连）与 R3A4（边界：摘要/世代错误、
    /// 未知来源、消息超限、弱网）。
    ///
    /// 所有投递都走 **OnDatagram / Update 的字节链**：真实 PMTransport（含分片、序号、
    /// ack/NAK、去重）→ 真实应用信封 → 真实 PMNetWorld / PMNetRpcReceive / PMReplicationChannel。
    /// 没有任何一处直接调用业务实现来冒充网络投递。
    /// </summary>
    internal static class Program
    {
        // ── 测试用的稳定 ID（手工描述符；不依赖生成器）──────────────────
        private const uint TestClassId = 0x7A11u;
        private const uint TestProtocolHash = 0x51D3A001u;

        // ── 预算 / 上限用例专用的第二个类 ──
        // 让每条 Create 记录带上固定长度的声明式初值，从而用**可控的对象个数**把生命周期批次
        // 精确推到目标量级（既不必真的登记上千个对象，也不用猜每条记录多大）。
        private const uint BigClassId = 0x7A12u;
        private const int BigActorStateBytes = 200;

        private const ushort RpcMark = 101;        // Server，可靠
        private const ushort RpcReward = 102;      // Client，可靠
        private const ushort RpcBroadcast = 103;   // Multicast，不可靠
        private const ushort RpcWatch = 104;       // Server，原生 _Validate

        private const ushort LayoutMark = 0x0111;
        private const ushort LayoutReward = 0x0222;
        private const ushort LayoutBroadcast = 0x0333;
        private const ushort LayoutWatch = 0x0444;

        private const ushort PropIdHealth = 1001;
        private const ushort PropIdArmor = 1002;

        private static int _passed;
        private static readonly List<string> _failures = new List<string>();

        private static int Main()
        {
            Console.WriteLine("=== R3-A2 / R3A3+R3A4：已认证连接的会话适配契约验证 ===");
            Console.WriteLine();

            SetupRegistry();

            Section("A. 应用信封：版本 / Kind / 有界 / 严格拒绝", TestEnvelopeCodec);
            Section("B. 激活闸门：未激活不派发不发送、摘要/世代/角色规则", TestActivationGate);
            Section("C. 双端连接与真实 PMTransport：Create 初值", TestCreateInitialState);
            Section("D. 双向 RPC 与方向/归属", TestRpcDirections);
            Section("E. 摘要 / 世代 / 布局 / ClassId 不符 ⇒ 拒绝或断连", TestProtocolMismatch);
            Section("F. 原生校验失败 ⇒ 真实 Transport 断连", TestValidateDisconnect);
            Section("G. 未知来源 / 超限 / 入站有界 / 每帧数据报预算", TestBoundsAndOversize);
            Section("H. 丢包重排：属性收敛、可靠 RPC 一次交付、Destroy 后拒绝", TestLossAndConvergence);
            Section("I. Dispose/断开幂等、成对摘除、计数非空", TestLifecycleAndCounters);
            Section("J. 每帧数据报预算严格上界（帧首 31 + 帧尾 32 场景）", TestFrameDatagramBudgetStrict);
            Section("K. 生命周期批次超限：显式断连而非静默丢弃", TestLifecycleBatchOversize);
            Section("L. 未激活期丢弃不得误 ACK ⇒ 激活后可靠流自动补齐", TestPreActivationReliableRecovery);
            Section("M. 静默期活性：真实桥驱动的周期 keepalive / 丢包恢复 / 核心长度守卫", TestSilentLiveness);

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
            int before = _passed;
            int failedBefore = _failures.Count;
            body();
            Console.WriteLine("   [" + name.Substring(0, name.IndexOf('.')) + "] " + (_passed - before)
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
        //  手工描述符 / 业务替身
        // =================================================================================

        /// <summary>业务替身：两个复制属性 + 四个 RPC 实现。计数器挂在实例上（不用 static 跨世界串味）。</summary>
        private sealed class SessionActor : PMNetObject
        {
            public int Health;
            public int Armor;

            public int CreateCount;
            public int CreateHealthSeen = int.MinValue;
            public int DestroyCount;

            public int MarkCount;
            public int LastMark;
            public int RewardCount;
            public int LastReward;
            public int BroadcastCount;
            public int LastBroadcast;
            public int WatchCount;
            public int LastWatch;

            public void SetHealth(int value)
            {
                Health = value;
                MarkPropertyDirty(0);
            }

            public void SetArmor(int value)
            {
                Armor = value;
                MarkPropertyDirty(1);
            }

            protected internal override void OnReplicatedCreate()
            {
                CreateCount++;
                CreateHealthSeen = Health;
            }

            protected internal override void OnReplicatedDestroy(PMObjectDestroyReason reason)
            {
                DestroyCount++;
            }
        }

        private static PMNetObject CreateActorInstance()
        {
            return new SessionActor();
        }

        /// <summary>
        /// 预算/上限用例专用对象：`OnSerializeInitialState` 固定写 <see cref="BigActorStateBytes"/> 字节，
        /// 于是每条 Create 记录的大小几乎全由这个固定长度决定（~200 + 十几字节头）。
        /// </summary>
        private sealed class BigActor : PMNetObject
        {
            protected internal override void OnSerializeInitialState(PMNetWriter writer)
            {
                byte[] blob = new byte[BigActorStateBytes];
                for (int i = 0; i < blob.Length; i++) { blob[i] = (byte)(i & 0xFF); }
                writer.WriteRawBytes(blob, 0, blob.Length);
            }
        }

        private static PMNetObject CreateBigActorInstance()
        {
            return new BigActor();
        }

        private static void WriteHealth(PMNetObject target, PMNetWriter writer)
        {
            writer.WriteInt32(((SessionActor)target).Health);
        }

        private static void ReadHealth(PMNetObject target, PMNetReader reader)
        {
            ((SessionActor)target).Health = reader.ReadInt32();
        }

        private static void WriteArmor(PMNetObject target, PMNetWriter writer)
        {
            writer.WriteInt32(((SessionActor)target).Armor);
        }

        private static void ReadArmor(PMNetObject target, PMNetReader reader)
        {
            ((SessionActor)target).Armor = reader.ReadInt32();
        }

        private static void InvokeMark(PMNetObject target, PMNetReader reader)
        {
            SessionActor self = (SessionActor)target;
            int value = reader.ReadInt32();
            if (!reader.IsAtEnd)
            {
                // 与生成物一致：调实现之前先拒绝尾随字节（严格拒绝尾部）。
                throw new FormatException("RPC Mark 载荷存在尾随字节");
            }

            self.MarkCount++;
            self.LastMark = value;
        }

        private static void InvokeReward(PMNetObject target, PMNetReader reader)
        {
            SessionActor self = (SessionActor)target;
            int value = reader.ReadInt32();
            if (!reader.IsAtEnd)
            {
                throw new FormatException("RPC Reward 载荷存在尾随字节");
            }

            self.RewardCount++;
            self.LastReward = value;
        }

        private static void InvokeBroadcast(PMNetObject target, PMNetReader reader)
        {
            SessionActor self = (SessionActor)target;
            int value = reader.ReadInt32();
            if (!reader.IsAtEnd)
            {
                throw new FormatException("RPC Broadcast 载荷存在尾随字节");
            }

            self.BroadcastCount++;
            self.LastBroadcast = value;
        }

        /// <summary>
        /// 模拟生成物对「原生 _Validate 返回 false」的发射形态：先留痕，再抛信号，
        /// 由接收入口（<see cref="PMNetRpcReceive"/>）向来源连接请求断连（D-R0-45）。
        /// </summary>
        private static void InvokeWatch(PMNetObject target, PMNetReader reader)
        {
            SessionActor self = (SessionActor)target;
            int value = reader.ReadInt32();
            if (!reader.IsAtEnd)
            {
                throw new FormatException("RPC Watch 载荷存在尾随字节");
            }

            self.LastWatch = value;

            if (value != 0)
            {
                PMRpcValidationSink.NotifyValidateFailed(target, RpcWatch, "Watch");
                throw new PMNetRpcValidationFailedException(RpcWatch, "Watch");
            }

            self.WatchCount++;
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

        /// <summary>注册一次全局只读描述符表（静态表只作描述符来源，不承载每世界状态）。</summary>
        private static void SetupRegistry()
        {
            PMNetRegistry.Reset();

            PMReplicationDescriptor rep = new PMReplicationDescriptor();
            rep.ClassId = TestClassId;
            rep.TypeName = "SessionActor";
            rep.ProtocolHash = TestProtocolHash;
            rep.HasConditionalMask = false;
            rep.ChangeMaskBitCount = 2;
            rep.Properties = new PMPropertyDescriptor[2];
            rep.Properties[0] = MakeProperty(0, PropIdHealth, "Health", WriteHealth, ReadHealth);
            rep.Properties[1] = MakeProperty(1, PropIdArmor, "Armor", WriteArmor, ReadArmor);

            PMNetClassEntry cls = new PMNetClassEntry();
            cls.ClassId = TestClassId;
            cls.TypeName = "SessionActor";
            cls.Rep = rep;
            cls.Factory = CreateActorInstance;
            cls.Rpcs = new PMNetRpcEntry[4];
            cls.Rpcs[0] = MakeRpc(RpcMark, PMRpcKind.Server, true, LayoutMark, "Mark", InvokeMark);
            cls.Rpcs[1] = MakeRpc(RpcReward, PMRpcKind.Client, true, LayoutReward, "Reward", InvokeReward);
            cls.Rpcs[2] = MakeRpc(RpcBroadcast, PMRpcKind.Multicast, false, LayoutBroadcast, "Broadcast", InvokeBroadcast);
            cls.Rpcs[3] = MakeRpc(RpcWatch, PMRpcKind.Server, true, LayoutWatch, "Watch", InvokeWatch);

            PMNetRegistry.RegisterClass(cls);

            // 第二个类：空属性表 + 固定长度初值（预算 / 上限用例用；不参与 RPC）。
            PMReplicationDescriptor bigRep = new PMReplicationDescriptor();
            bigRep.ClassId = BigClassId;
            bigRep.TypeName = "BigActor";
            bigRep.ProtocolHash = TestProtocolHash;
            bigRep.HasConditionalMask = false;
            bigRep.ChangeMaskBitCount = 0;
            bigRep.Properties = new PMPropertyDescriptor[0];

            PMNetClassEntry bigCls = new PMNetClassEntry();
            bigCls.ClassId = BigClassId;
            bigCls.TypeName = "BigActor";
            bigCls.Rep = bigRep;
            bigCls.Factory = CreateBigActorInstance;
            bigCls.Rpcs = new PMNetRpcEntry[0];
            PMNetRegistry.RegisterClass(bigCls);

            PMNetRegistry.Seal(TestProtocolHash);

            PMNetRpcReceive.ResetStats();
            PMNetRpcReceive.Warn = null;
            PMNetRpcReceive.Observer = null;
        }

        // =================================================================================
        //  可控链路 / 端点 / 场景
        // =================================================================================

        private sealed class TestLink : IPMTransportLink
        {
            public readonly string Name;
            public readonly LinkHub Hub;
            public readonly List<TestLink> Peers = new List<TestLink>(2);
            public PMTransportConnection Conn;

            public TestLink(string name, LinkHub hub)
            {
                Name = name;
                Hub = hub;
            }

            public bool Send(byte[] buffer, int offset, int count)
            {
                Hub.Route(this, buffer, offset, count);
                return true;
            }

            public string Describe()
            {
                return Name;
            }
        }

        /// <summary>可控链路中枢：按「来源链路」施加丢包 / 重复 / 乱序，并把在途数据报投递给目标连接。</summary>
        private sealed class LinkHub
        {
            private readonly List<TestLink> _links = new List<TestLink>(4);
            private readonly Dictionary<TestLink, List<byte[]>> _inbox = new Dictionary<TestLink, List<byte[]>>();
            private readonly Dictionary<string, int> _dropFrom = new Dictionary<string, int>();
            private readonly Dictionary<string, int> _dupFrom = new Dictionary<string, int>();
            private readonly Dictionary<string, int> _reorderFrom = new Dictionary<string, int>();

            public long Enqueued;
            public long Dropped;
            public long Duplicated;

            public TestLink AddLink(string name)
            {
                TestLink link = new TestLink(name, this);
                _links.Add(link);
                _inbox[link] = new List<byte[]>();
                return link;
            }

            public void DropNextFrom(string sourceName, int count)
            {
                _dropFrom[sourceName] = Get(_dropFrom, sourceName) + count;
            }

            public void DuplicateNextFrom(string sourceName, int count)
            {
                _dupFrom[sourceName] = Get(_dupFrom, sourceName) + count;
            }

            public void ReorderNextFrom(string sourceName, int count)
            {
                _reorderFrom[sourceName] = Get(_reorderFrom, sourceName) + count;
            }

            private static int Get(Dictionary<string, int> table, string key)
            {
                int value;
                return table.TryGetValue(key, out value) ? value : 0;
            }

            public void Route(TestLink from, byte[] buffer, int offset, int count)
            {
                int drop;
                if (_dropFrom.TryGetValue(from.Name, out drop) && drop > 0)
                {
                    _dropFrom[from.Name] = drop - 1;
                    Dropped++;
                    return;
                }

                for (int i = 0; i < from.Peers.Count; i++)
                {
                    TestLink peer = from.Peers[i];
                    byte[] copy = new byte[count];
                    Buffer.BlockCopy(buffer, offset, copy, 0, count);
                    List<byte[]> box = _inbox[peer];
                    box.Add(copy);
                    Enqueued++;

                    int dup;
                    if (_dupFrom.TryGetValue(from.Name, out dup) && dup > 0)
                    {
                        _dupFrom[from.Name] = dup - 1;
                        box.Add((byte[])copy.Clone());
                        Duplicated++;
                    }

                    int reorder;
                    if (_reorderFrom.TryGetValue(from.Name, out reorder) && reorder > 0 && box.Count >= 2)
                    {
                        _reorderFrom[from.Name] = reorder - 1;
                        byte[] last = box[box.Count - 1];
                        box[box.Count - 1] = box[box.Count - 2];
                        box[box.Count - 2] = last;
                    }
                }
            }

            /// <summary>把在途数据报投递到目标连接（等价于接收线程调用 OnDatagram）。</summary>
            public void Deliver()
            {
                for (int i = 0; i < _links.Count; i++)
                {
                    TestLink link = _links[i];
                    List<byte[]> box = _inbox[link];
                    if (box.Count == 0)
                    {
                        continue;
                    }

                    byte[][] batch = box.ToArray();
                    box.Clear();
                    for (int k = 0; k < batch.Length; k++)
                    {
                        if (link.Conn == null)
                        {
                            continue;
                        }

                        link.Conn.OnDatagram(batch[k], 0, batch[k].Length);
                    }
                }
            }
        }

        /// <summary>一个世界 + 一条桥 + 1..N 条连接（服务端可以有 N 条）。</summary>
        private sealed class Endpoint
        {
            public string Name;
            public uint Epoch;
            public PMNetWorld World;
            public PMNetSessionBridge Bridge;
            public readonly List<PMTransportConnection> Connections = new List<PMTransportConnection>();
            public readonly List<TestLink> Links = new List<TestLink>();
            public readonly List<string> Warnings = new List<string>();

            public PMTransportConnection Conn { get { return Connections[0]; } }
            public TestLink Link { get { return Links[0]; } }

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

        private sealed class Scenario
        {
            public readonly LinkHub Hub = new LinkHub();
            public readonly List<Endpoint> Endpoints = new List<Endpoint>();
            public long Now = 1000L;

            /// <summary>
            /// 连接传输层配置的调参钩子（必须在 <see cref="Add"/> 之前设置：传输层在连接构造时就建好了）。
            ///
            /// 存在的理由（本轮）：<see cref="TestSilentLiveness"/> 需要用**生产路径**验证
            /// keepalive 生效与不生效两种口径，而 A–L 组全部统计绝对报文/入站计数，
            /// 必须保持「keepalive 关闭」的旧行为。
            /// </summary>
            public Action<PMTransportConfig> TuneTransportConfig;

            public Endpoint Add(string name, bool isServer, uint epoch, int connectionId,
                                PMSessionPeerRole peerRole, int uid, int playerId,
                                uint localHash, uint peerHash)
            {
                return Add(name, isServer, epoch, connectionId, peerRole, uid, playerId, localHash, peerHash, 0u);
            }

            /// <summary>identityEpoch != 0 时，连接上的票面世代与世界世代**故意不同**（测世代校验）。</summary>
            public Endpoint Add(string name, bool isServer, uint epoch, int connectionId,
                                PMSessionPeerRole peerRole, int uid, int playerId,
                                uint localHash, uint peerHash, uint identityEpoch)
            {
                Endpoint ep = new Endpoint();
                ep.Name = name;
                ep.Epoch = epoch;
                ep.World = new PMNetWorld(new PMSession(epoch, isServer));
                ep.World.Warn = delegate(string m) { ep.Warnings.Add(m); };
                ep.World.RegisterClass(TestClassId, CreateActorInstance);
                ep.World.RegisterClass(BigClassId, CreateBigActorInstance);

                ep.Bridge = new PMNetSessionBridge(ep.World);
                ep.Bridge.Warn = delegate(string m) { ep.Warnings.Add(m); };

                uint ticketEpoch = identityEpoch != 0u ? identityEpoch : epoch;
                AddConnectionTo(ep, name, connectionId, peerRole, uid, playerId, ticketEpoch, localHash, peerHash);
                Endpoints.Add(ep);
                return ep;
            }

            /// <summary>在同一个世界/桥上再加一条连接（服务端多客户端、客户端第二条服务器连接等场景）。</summary>
            public PMTransportConnection AddConnection(Endpoint ep, string name, int connectionId,
                                                       PMSessionPeerRole peerRole, int uid, int playerId)
            {
                return AddConnectionTo(ep, name, connectionId, peerRole, uid, playerId, ep.Epoch,
                                       TestProtocolHash, TestProtocolHash);
            }

            private PMTransportConnection AddConnectionTo(Endpoint ep, string name, int connectionId,
                                                          PMSessionPeerRole peerRole, int uid, int playerId,
                                                          uint epoch, uint localHash, uint peerHash)
            {
                TestLink link = Hub.AddLink(name);

                PMSessionIdentity identity = new PMSessionIdentity();
                identity.ConnectionId = connectionId;
                identity.PeerRole = peerRole;
                identity.Uid = uid;
                identity.PlayerId = playerId;
                identity.TeamId = uid % 2;
                identity.HeroId = 1;
                identity.Epoch = epoch;
                identity.LocalProtocolHash = localHash;
                identity.PeerProtocolHash = peerHash;
                identity.MatchId = "match-session-test";
                identity.DsId = "ds-session-test";

                PMTransportConfig config = new PMTransportConfig();
                config.IdleTimeoutMs = 0L;         // 旧用例不用墙钟推进做超时判定
                config.KeepAliveIntervalMs = 0L;   // 旧用例统计绝对报文数 ⇒ 显式禁用 keepalive（见 §M）
                if (TuneTransportConfig != null) { TuneTransportConfig(config); }

                PMTransportConnection connection = new PMTransportConnection(identity, ep.World, ep.Bridge, link, config);
                link.Conn = connection;
                ep.Connections.Add(connection);
                ep.Links.Add(link);
                return connection;
            }

            public void Connect(TestLink from, TestLink to)
            {
                from.Peers.Add(to);
            }

            public void Frame(int frames)
            {
                for (int f = 0; f < frames; f++)
                {
                    Hub.Deliver();
                    for (int i = 0; i < Endpoints.Count; i++)
                    {
                        Endpoints[i].Bridge.Update(Now);
                    }

                    Hub.Deliver();
                    Now += 16L;
                }
            }
        }

        /// <summary>建立一个已激活的双端会话：server + client，epoch/摘要一致。</summary>
        private static void MakeActivatedPair(Scenario scene, uint epoch, out Endpoint server, out Endpoint client)
        {
            server = scene.Add("server", true, epoch, 1, PMSessionPeerRole.Client, 900, 900,
                               TestProtocolHash, TestProtocolHash);
            client = scene.Add("client", false, epoch, 1, PMSessionPeerRole.Server, 7, 7,
                               TestProtocolHash, TestProtocolHash);
            scene.Connect(server.Link, client.Link);
            scene.Connect(client.Link, server.Link);

            string error;
            Check(server.Conn.TryActivate(out error), "服务端连接激活成功（" + (error ?? "ok") + "）");
            Check(client.Conn.TryActivate(out error), "客户端连接激活成功（" + (error ?? "ok") + "）");
            Check(server.Conn.IsReady && client.Conn.IsReady, "双端 IsReady 均为 true");
        }

        // =================================================================================
        //  工程辅助：手工构造应用消息（模拟「对端发来的字节」）
        // =================================================================================

        private static byte[] BuildApplicationMessage(PMSessionMessageKind kind, byte[] body)
        {
            PMNetWriter writer = new PMNetWriter(64);
            return PMApplicationEnvelope.Wrap(writer, kind, body, 0, body != null ? body.Length : 0);
        }

        private static byte[] BuildRpcBody(uint netId, bool isStatic, uint classId, ushort rpcId, ushort layout,
                                           int paramValue)
        {
            PMNetWriter writer = new PMNetWriter(32);
            writer.WriteUInt64(netId);
            writer.WriteBool(isStatic);
            writer.WriteUInt64(classId);
            writer.WriteUInt64(rpcId);
            writer.WriteUInt64(layout);
            writer.WriteInt32(paramValue);
            return writer.ToArray();
        }

        /// <summary>
        /// 用**真实的** PMReplicationWriter 造一条复制 Update 正文。
        ///
        /// 为什么必须用它而不是手拼几个字节：伪造复制 Update 的用例需要一条**格式完全合法**
        /// （魔数 0xA7 / 版本 1 / kind=Update / 记录结构合法）的载荷，且指向真实存在的对象。
        /// 用坏 magic 会在「魔数/版本不符」分支提前失败，那条路径测到的是「解析兑底」
        /// 而不是「方向拒绝」—— 断言名与代码路径会错位（上一轮正是这么漏检的）。
        /// </summary>
        private static byte[] BuildReplicationUpdateBody(uint netId, long version, int slot,
                                                          ushort propertyId, int value)
        {
            PMNetWriter valueWriter = new PMNetWriter(8);
            valueWriter.WriteInt32(value);

            PMRepUpdateRecord record = new PMRepUpdateRecord(netId, version,
                new int[] { slot }, new ushort[] { propertyId }, new byte[][] { valueWriter.ToArray() });

            List<PMRepUpdateRecord> records = new List<PMRepUpdateRecord>(1);
            records.Add(record);

            PMNetWriter writer = new PMNetWriter(64);
            PMReplicationWriter.WriteUpdate(writer, records);
            return writer.ToArray();
        }

        /// <summary>手工拼一个合法的传输层数据报（用于「未认证 / 未激活就喂字节」这类闸门测试）。</summary>
        private static byte[] CraftTransportDatagram(uint epoch, byte[] applicationMessage)
        {
            int total = 18 + 2 + 2 + applicationMessage.Length;
            byte[] buffer = new byte[total];
            int p = 0;
            buffer[p++] = 1;                       // 协议版本
            WriteU32(buffer, ref p, epoch);
            WriteU16(buffer, ref p, 1);            // packetId
            WriteU16(buffer, ref p, 0);            // ackId
            for (int i = 0; i < 8; i++) { buffer[p++] = 0; }
            buffer[p++] = 1;                       // messageCount
            buffer[p++] = 0;                       // flags：不可靠、非分片
            buffer[p++] = (byte)PMStream.Unreliable;
            WriteU16(buffer, ref p, (ushort)applicationMessage.Length);
            Buffer.BlockCopy(applicationMessage, 0, buffer, p, applicationMessage.Length);
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

        private static PMNetObject Find(Endpoint ep, PMNetId id)
        {
            PMNetObject obj;
            ep.World.TryFind(id, out obj);
            return obj;
        }

        // =================================================================================
        //  A. 应用信封
        // =================================================================================

        private static void TestEnvelopeCodec()
        {
            byte[] body = new byte[] { 9, 8, 7, 6, 5 };

            for (int k = 1; k <= 4; k++)
            {
                PMSessionMessageKind kind = (PMSessionMessageKind)k;
                byte[] message = BuildApplicationMessage(kind, body);
                CheckEq(message.Length, body.Length + 2, "Kind=" + kind + " 的信封长度 = 正文 + 2");

                PMSessionMessageKind parsedKind;
                int bodyOffset;
                int bodyCount;
                string error;
                bool ok = PMApplicationEnvelope.TryRead(message, 0, message.Length, out parsedKind,
                                                        out bodyOffset, out bodyCount, out error);
                Check(ok, "Kind=" + kind + " 信封可解析（" + (error ?? "ok") + "）");
                Check(parsedKind == kind, "Kind=" + kind + " 往返一致");
                CheckEq(bodyCount, body.Length, "Kind=" + kind + " 正文长度正确");
                CheckEq(message[bodyOffset], 9, "Kind=" + kind + " 正文首字节正确");
            }

            PMSessionMessageKind tmpKind;
            int tmpOffset;
            int tmpCount;
            string err;

            byte[] badVersion = BuildApplicationMessage(PMSessionMessageKind.Rpc, body);
            badVersion[0] = 2;
            Check(!PMApplicationEnvelope.TryRead(badVersion, 0, badVersion.Length, out tmpKind, out tmpOffset,
                                                 out tmpCount, out err), "版本不符被拒绝");
            Check(err != null && err.IndexOf("版本", StringComparison.Ordinal) >= 0, "版本不符给出可归因原因");

            PMNetWriter kindWriter = new PMNetWriter(8);
            kindWriter.WriteUInt64(1UL);
            kindWriter.WriteUInt64(5UL);
            byte[] badKind = kindWriter.ToArray();
            Check(!PMApplicationEnvelope.TryRead(badKind, 0, badKind.Length, out tmpKind, out tmpOffset,
                                                 out tmpCount, out err), "未知消息类型（5）被拒绝");
            Check(err != null && err.IndexOf("类型", StringComparison.Ordinal) >= 0, "未知类型给出可归因原因");

            byte[] truncated = new byte[] { 1 };
            Check(!PMApplicationEnvelope.TryRead(truncated, 0, truncated.Length, out tmpKind, out tmpOffset,
                                                 out tmpCount, out err), "截断的信封被拒绝");

            byte[] oversize = new byte[PMApplicationEnvelope.MaxMessageBytes + 1];
            Check(!PMApplicationEnvelope.TryRead(oversize, 0, oversize.Length, out tmpKind, out tmpOffset,
                                                 out tmpCount, out err), "超过 64KiB 的应用消息被拒绝");

            byte[] windowOut = BuildApplicationMessage(PMSessionMessageKind.Rpc, body);
            Check(!PMApplicationEnvelope.TryRead(windowOut, 0, windowOut.Length + 8, out tmpKind, out tmpOffset,
                                                 out tmpCount, out err), "载荷窗口越界被拒绝");

            Check(PMApplicationEnvelope.Wrap(new PMNetWriter(8), PMSessionMessageKind.Rpc,
                                             new byte[PMApplicationEnvelope.MaxMessageBytes], 0,
                                             PMApplicationEnvelope.MaxMessageBytes) == null,
                  "Wrap 在超限时返回 null（显式失败，不静默截断）");
        }

        // =================================================================================
        //  B. 激活闸门
        // =================================================================================

        private static void TestActivationGate()
        {
            // ── ① 未激活：不派发、不发送 ────────────────────────────────
            Scenario scene = new Scenario();
            Endpoint lone = scene.Add("lone", true, 5001u, 1, PMSessionPeerRole.Client, 1, 1,
                                      TestProtocolHash, TestProtocolHash);

            Check(!lone.Conn.IsReady && !lone.Conn.IsActivated, "未激活：IsReady=false 且 IsActivated=false");

            byte[] probe = BuildApplicationMessage(PMSessionMessageKind.Rpc,
                                                   BuildRpcBody(1u, false, TestClassId, RpcMark, LayoutMark, 5));
            byte[] datagram = CraftTransportDatagram(5001u, probe);
            lone.Conn.OnDatagram(datagram, 0, datagram.Length);
            lone.Conn.Update(scene.Now);

            // R3-B 语义（本轮复核确认，旧断言已按此改写）：
            //   未就绪时 `OnDatagram` 的纵深闸门直接丢弃字节 ⇒ **不进 Transport**、
            //   不建序号、**不 ack**（否则对端会据此退休可靠消息，事后丢弃就是静默的可靠性破洞）。
            CheckEq(lone.Conn.MessagesReceived, 0, "未激活时没有应用消息到达（不派发）");
            CheckEq(lone.Conn.InboundDroppedNotActivated, 1, "未激活时数据报被纵深闸门丢弃（计数 1）");
            CheckEq(lone.Conn.Transport.Stats.DatagramsReceived, 0, "未激活时 **Transport 入站计数为 0**（字节没进 Transport）");
            CheckEq(lone.Conn.Transport.Stats.AcksSent, 0, "未激活时 **没有发出任何 ack**");
            CheckEq(lone.Conn.Transport.Stats.DatagramsSent, 0, "未激活时没有任何出站数据报");

            Check(!lone.Bridge.SendRpc(CreateActorInstance(), RpcMark,
                                       delegate(PMNetObject t, PMNetWriter w) { w.WriteInt32(1); }),
                  "未激活（无受信连接）：SendRpc 返回 false");
            CheckEq(scene.Hub.Enqueued, 0, "未激活时没有任何数据报被发出");

            // ── ①-b 正例对照：**同一份字节**在已激活连接上会被接收并 ack ──────
            //   没有这条对照，“未激活不 ack”可能只是因为字节本身非法（假绿）。
            Scenario gateScene2 = new Scenario();
            Endpoint gateSink = gateScene2.Add("gatesink", true, 5001u, 1, PMSessionPeerRole.Client, 1, 1,
                                              TestProtocolHash, TestProtocolHash);
            string liveError;
            Check(gateSink.Conn.TryActivate(out liveError), "对照连接激活成功（" + (liveError ?? "ok") + "）");

            byte[] sameDatagram = CraftTransportDatagram(5001u, probe);
            gateSink.Conn.OnDatagram(sameDatagram, 0, sameDatagram.Length);
            gateSink.Conn.Update(gateScene2.Now);

            CheckEq(gateSink.Conn.MessagesReceived, 1, "对照：同一份字节在已激活连接上到达应用层");
            CheckEq(gateSink.Conn.Transport.Stats.DatagramsReceived, 1, "对照：Transport 入站计数为 1（字节本身合法）");
            CheckEq(gateSink.Conn.InboundDroppedNotActivated, 0, "对照：已激活连接不丢字节");
            CheckGt(gateSink.Conn.Transport.Stats.AcksSent + gateSink.Conn.Transport.Stats.DatagramsSent, 0,
                    "对照：已激活连接真的回了 ack 口径（非空）");

            // ── ② 摘要不一致 ⇒ 拒绝激活并断开 ───────────────────────────
            Scenario mismatched = new Scenario();
            Endpoint badHash = mismatched.Add("badhash", true, 5002u, 1, PMSessionPeerRole.Client, 1, 1,
                                              TestProtocolHash, TestProtocolHash ^ 0xFFFFu);
            string error;
            Check(!badHash.Conn.TryActivate(out error), "双方摘要不一致 ⇒ 拒绝激活");
            Check(error != null && error.IndexOf("摘要", StringComparison.Ordinal) >= 0,
                  "摘要不一致的拒绝原因可归因：" + error);
            Check(!badHash.Conn.Transport.IsConnected, "摘要不一致 ⇒ 真实 Transport 已断开");
            CheckStrEq(badHash.Conn.DisconnectReason.ToString(), "ProtocolError", "断开原因为 ProtocolError");
            CheckEq(badHash.World.ConnectionCount, 0, "被拒绝的连接没有进入世界");
            CheckEq(badHash.Bridge.Replication.ConnectionCount, 0, "被拒绝的连接没有进入复制层");

            // ── ③ 本地摘要与注册表不一致 ⇒ 拒绝（不接受第二套摘要来源）──
            Scenario forged = new Scenario();
            Endpoint secondHash = forged.Add("secondhash", true, 5003u, 1, PMSessionPeerRole.Client, 1, 1,
                                             0xDEADBEEFu, 0xDEADBEEFu);
            Check(!secondHash.Conn.TryActivate(out error), "本地摘要与注册表不一致 ⇒ 拒绝激活");
            Check(error != null && error.IndexOf("注册表", StringComparison.Ordinal) >= 0,
                  "第二套摘要来源的拒绝原因可归因：" + error);

            // ── ④ 票面世代与世界会话不一致 ⇒ 拒绝激活 ──────────────────
            Scenario epochScene = new Scenario();
            Endpoint epochOk = epochScene.Add("epochok", true, 5004u, 1, PMSessionPeerRole.Client, 1, 1,
                                              TestProtocolHash, TestProtocolHash);
            Endpoint epochBad = epochScene.Add("epochbad", true, 5004u, 2, PMSessionPeerRole.Client, 2, 2,
                                               TestProtocolHash, TestProtocolHash, 999u);
            Check(epochOk.Conn.TryActivate(out error), "票面世代 = 世界世代 ⇒ 激活成功（对照）");
            Check(!epochBad.Conn.TryActivate(out error), "票面世代（999）≠ 世界会话（5004）⇒ 拒绝激活");
            Check(error != null && error.IndexOf("世代", StringComparison.Ordinal) >= 0,
                  "世代不一致的拒绝原因可归因：" + error);
            CheckStrEq(epochBad.Conn.DisconnectReason.ToString(), "SessionMismatch", "断开原因为 SessionMismatch");
            CheckEq(epochBad.World.ConnectionCount, 0, "世代不符的连接没有进入世界");

            // ── ⑤ 客户端只接受一条受信服务器连接 ───────────────────────
            Scenario clientScene = new Scenario();
            Endpoint cli = clientScene.Add("cli", false, 5005u, 1, PMSessionPeerRole.Server, 7, 7,
                                           TestProtocolHash, TestProtocolHash);
            // 同一条桥（同一个客户端世界）上的第二条/第三条连接
            PMTransportConnection decoy = clientScene.AddConnection(cli, "decoy", 2, PMSessionPeerRole.Server, 8, 8);
            PMTransportConnection impostor = clientScene.AddConnection(cli, "impostor", 3, PMSessionPeerRole.Client, 9, 9);

            Check(cli.Conn.TryActivate(out error), "客户端激活受信服务器连接成功");
            Check(!decoy.TryActivate(out error), "客户端拒绝第二条服务器连接");
            Check(!impostor.TryActivate(out error), "客户端拒绝「对端声明为客户端」的连接");
            CheckEq(cli.World.ConnectionCount, 1, "客户端世界里只有 1 条连接");
            CheckGt(cli.Bridge.RejectedConnections, 0, "客户端拒绝计数非空（RejectedConnections）");

            // ── ⑥ 服务端只接受已认证且 uid 唯一的玩家连接 ──────────────
            Scenario serverScene = new Scenario();
            Endpoint srv = serverScene.Add("srv", true, 5006u, 1, PMSessionPeerRole.Client, 1, 1,
                                           TestProtocolHash, TestProtocolHash);
            PMTransportConnection p1 = serverScene.AddConnection(srv, "p1", 2, PMSessionPeerRole.Client, 7, 7);
            PMTransportConnection p2 = serverScene.AddConnection(srv, "p2", 3, PMSessionPeerRole.Client, 7, 7);
            PMTransportConnection p3 = serverScene.AddConnection(srv, "p3", 4, PMSessionPeerRole.Client, 8, 8);
            PMTransportConnection asServerPeer = serverScene.AddConnection(srv, "srvrole", 5, PMSessionPeerRole.Server, 9, 9);

            Check(srv.Conn.TryActivate(out error), "服务端连接自己激活成功");
            Check(p1.TryActivate(out error), "服务端接受已认证玩家连接（uid=7）");
            Check(!p2.TryActivate(out error), "服务端拒绝重复 uid（uid=7 第二条）");
            Check(!p2.TryActivate(out error), "重复 uid 的拒绝是幂等判定（再次仍为 false）");
            Check(!asServerPeer.TryActivate(out error), "服务端拒绝「对端声明为服务器」的连接");
            CheckGt(srv.Bridge.RejectedConnections, 0, "服务端拒绝计数非空（RejectedConnections）");
            CheckEq(srv.World.ConnectionCount, 2, "服务端世界里有 2 条连接（srv + uid7）");

            // ── ⑦ 激活幂等 ──────────────────────────────────────────────
            Check(p3.TryActivate(out error), "服务端接受另一 uid（uid=8）");
            Check(p3.TryActivate(out error), "重复激活同一连接是幂等的 true");
            CheckEq(srv.World.ConnectionCount, 3, "服务端世界里有 3 条连接");

            // ── ⑧ 身份字段非法 ⇒ 拒绝 ──────────────────────────────────
            Scenario badIdentityScene = new Scenario();
            Endpoint badIdentity = badIdentityScene.Add("badidentity", true, 5007u, 1, PMSessionPeerRole.Client,
                                                        0, 1, TestProtocolHash, TestProtocolHash);
            Check(!badIdentity.Conn.TryActivate(out error), "Uid=0 的身份字段非法 ⇒ 拒绝激活");
            Check(error != null && error.IndexOf("身份字段", StringComparison.Ordinal) >= 0,
                  "身份字段非法的拒绝原因可归因：" + error);

            // ── ⑨ Epoch 非 0 的契约（PMTransport 构造期就要求）──────────
            Scenario zeroEpochScene = new Scenario();
            Endpoint zeroEpoch = zeroEpochScene.Add("zeroepoch", true, 0u, 1, PMSessionPeerRole.Client, 1, 1,
                                                    TestProtocolHash, TestProtocolHash);
            Check(!zeroEpoch.Conn.TryActivate(out error), "SessionEpoch=0 的身份非法 ⇒ 拒绝激活");

            // ── ⑩ 传输层已断开时不得激活（防止登记出无法自动摘除的僵尸连接）──
            //
            // 为什么必须拦：断开回调（HandleDisconnected → Bridge.OnConnectionClosed）在断开那一刻就执行完了，
            // 而传输层的 Disconnect 是幂等的（不会再回调）。若此时还能激活并登记，这条连接就再没有
            // 任何对手能摘除它：客户端侧它把唯一受信服务器槽位占死（后续合法服务器连接被拒）、
            // 服务端侧它把 uid 永久占用（同一玩家重连被拒）、世界侧它还长期挂一份永远发不出去的
            // Create 待发队列（FlushLifecycle 的前置是 IsReady）。
            Scenario deadScene = new Scenario();
            Endpoint dead = deadScene.Add("dead", true, 5008u, 1, PMSessionPeerRole.Client, 1, 1,
                                           TestProtocolHash, TestProtocolHash);
            dead.Conn.Transport.Disconnect(PMDisconnectReason.PeerClosed);
            Check(!dead.Conn.Transport.IsConnected, "前置：该连接的传输层确已断开");

            Check(!dead.Conn.TryActivate(out error), "传输已断开 ⇒ TryActivate 返回 false");
            Check(error != null && error.IndexOf("传输", StringComparison.Ordinal) >= 0,
                  "死链路激活的拒绝原因可归因：" + error);
            CheckEq(dead.Conn.RejectedInactiveTransport, 1, "死链路激活被记账（RejectedInactiveTransport）");
            Check(!dead.Conn.IsActivated, "死链路上的连接没有被标为已激活");
            Check(!dead.Conn.IsReady, "不变量：未激活 ⇒ IsReady=false");
            CheckEq(dead.World.ConnectionCount, 0, "死链路激活没有登记进世界");
            CheckEq(dead.Bridge.Replication.ConnectionCount, 0, "死链路激活没有登记进复制层");
            CheckEq(dead.Bridge.ConnectionCount, 0, "死链路激活没有进入桥的连接表");
            CheckEq(dead.Bridge.RejectedConnections, 0, "拒绝发生在桥的接受规则之前（不是因为角色/uid 冲突）");

            // 不变量正例：真激活成功时 IsReady 必须为真（否则上面的拒绝断言可能是空过）
            Scenario liveScene = new Scenario();
            Endpoint live = liveScene.Add("live", true, 5009u, 1, PMSessionPeerRole.Client, 1, 1,
                                          TestProtocolHash, TestProtocolHash);
            Check(live.Conn.TryActivate(out error), "活链路激活成功（对照）");
            Check(live.Conn.IsReady, "不变量：TryActivate 成功 ⇒ IsReady 为真");
        }

        // =================================================================================
        //  C. Create 初值
        // =================================================================================

        private static void TestCreateInitialState()
        {
            Scenario scene = new Scenario();
            Endpoint server;
            Endpoint client;
            MakeActivatedPair(scene, 6001u, out server, out client);

            CheckEq(server.World.ObjectCount, 0, "激活后世界里还没有对象");
            CheckEq(client.World.ConnectionCount, 1, "客户端世界登记了 1 条连接");

            SessionActor actor = new SessionActor();
            actor.Health = 100;
            actor.Armor = 25;
            Check(server.World.Spawn(actor, TestClassId), "服务端 Spawn 成功");
            actor.OwnerConnection = server.Conn;   // 归属来自已认证会话（服务端侧连接对象）
            server.Bridge.RegisterReplicatedObject(actor);

            scene.Frame(6);

            PMNetObject replicated = Find(client, actor.NetId);
            Check(replicated != null, "客户端通过了身份查到副本");
            SessionActor clientActor = replicated as SessionActor;
            Check(clientActor != null, "客户端副本类型正确（SessionActor）");

            if (clientActor == null)
            {
                return;
            }

            CheckEq(clientActor.CreateCount, 1, "客户端创建回调恰好一次");
            CheckEq(clientActor.CreateHealthSeen, 100, "创建回调里就读到声明式初值（D-R0-16：创建与初值同包）");
            CheckEq(clientActor.Health, 100, "客户端 Health 与初值一致");
            CheckEq(clientActor.Armor, 25, "客户端 Armor 与初值一致");
            CheckEq(client.World.Stats.CreatesApplied, 1, "客户端 CreatesApplied = 1");
            CheckEq(client.World.Stats.ProtocolErrors, 0, "客户端没有协议错误");

            CheckGt(server.Bridge.LifecycleMessagesSent, 0, "服务端发出了生命周期消息（计数非空）");
            CheckGt(server.World.Stats.CreatesSent, 0, "服务端构造过 Create 记录（CreatesSent 非空）");
            CheckGt(server.Conn.Transport.Stats.DatagramsSent, 0, "服务端 Transport 发送计数非空");
            CheckGt(client.Conn.Transport.Stats.DatagramsReceived, 0, "客户端 Transport 接收计数非空");
            CheckGt(client.Bridge.ReplicationAckMessagesSent, 0, "客户端回传了复制版本确认（非空）");
            CheckEq(scene.Hub.Dropped, 0, "本次没有任何数据报被链路丢弃");
            Check(!server.HasWarning("票据"), "告警里不含票据字样（不泄露凭据）");

            // ── Create 与可靠 RPC 同流：同一 tick 内先排 Create 再排 RPC ──
            // 本用例中间**不跑任何帧**：对象刚 Spawn、生命周期事件还在世界待发队列里，
            // 立刻调 SendRpc。如果 SendRpc 不先把该连接的生命周期批次排进可靠域，
            // 对端会先收到一条指向「还不存在的对象」的 RPC（UnknownTarget 被丢弃）。
            SessionActor ordered = new SessionActor();
            ordered.Health = 7;
            Check(server.World.Spawn(ordered, TestClassId), "同 tick 用例：Spawn 成功");
            ordered.OwnerConnection = server.Conn;
            server.Bridge.RegisterReplicatedObject(ordered);

            Check(server.Bridge.SendRpc(ordered, RpcReward,
                                        delegate(PMNetObject t, PMNetWriter w) { w.WriteInt32(123); }),
                  "同 tick 用例：Spawn 之后立刻发 Client RPC（中间不跑帧）");

            scene.Frame(12);

            SessionActor orderedCopy = Find(client, ordered.NetId) as SessionActor;
            Check(orderedCopy != null, "同 tick 用例：客户端先拿到了 Create 副本");
            if (orderedCopy != null)
            {
                CheckEq(orderedCopy.Health, 7, "同 tick 用例：Create 初值已到位");
                CheckEq(orderedCopy.RewardCount, 1, "同 tick 用例：RPC 仍然执行（Create 先于 RPC 同一可靠流）");
                CheckEq(orderedCopy.LastReward, 123, "同 tick 用例：RPC 参数正确");
            }
        }

        // =================================================================================
        //  D. RPC 方向与归属
        // =================================================================================

        private static void TestRpcDirections()
        {
            Scenario scene = new Scenario();
            Endpoint server = scene.Add("server", true, 7001u, 1, PMSessionPeerRole.Client, 900, 900,
                                        TestProtocolHash, TestProtocolHash);
            Endpoint c1 = scene.Add("c1", false, 7001u, 1, PMSessionPeerRole.Server, 7, 7,
                                    TestProtocolHash, TestProtocolHash);
            Endpoint c2 = scene.Add("c2", false, 7001u, 2, PMSessionPeerRole.Server, 8, 8,
                                    TestProtocolHash, TestProtocolHash);

            // 服务端的第 2 条连接（代表 c2）；第 1 条（server.Conn）代表 c1。
            PMTransportConnection serverConnForC2 = scene.AddConnection(server, "server-c2", 2,
                                                                       PMSessionPeerRole.Client, 8, 8);

            scene.Connect(server.Link, c1.Link);
            scene.Connect(c1.Link, server.Link);
            scene.Connect(server.Links[1], c2.Link);
            scene.Connect(c2.Link, server.Links[1]);

            string error;
            Check(server.Conn.TryActivate(out error), "服务端连接 1（对应 c1）激活");
            Check(serverConnForC2.TryActivate(out error), "服务端连接 2（对应 c2）激活");
            Check(c1.Conn.TryActivate(out error), "c1 激活");
            Check(c2.Conn.TryActivate(out error), "c2 激活");
            CheckEq(server.Bridge.ConnectionCount, 2, "服务端桥登记了 2 条玩家连接");

            SessionActor actor = new SessionActor();
            actor.Health = 10;
            actor.Armor = 1;
            Check(server.World.Spawn(actor, TestClassId), "服务端 Spawn actor");
            actor.OwnerConnection = server.Conn;          // c1 的连接才是拥有者
            server.Bridge.RegisterReplicatedObject(actor);

            scene.Frame(8);

            PMNetObject c1Replica = Find(c1, actor.NetId);
            PMNetObject c2Replica = Find(c2, actor.NetId);
            Check(c1Replica != null, "c1 拿到了 actor 副本");
            Check(c2Replica != null, "c2 拿到了 actor 副本");
            if (c1Replica == null || c2Replica == null)
            {
                return;
            }

            SessionActor c1Copy = (SessionActor)c1Replica;
            SessionActor c2Copy = (SessionActor)c2Replica;

            // ── ① 客户端 → 服务端：拥有者发的 Server RPC 被放行 ─────────
            Check(c1.Bridge.SendRpc(c1Copy, RpcMark,
                                    delegate(PMNetObject t, PMNetWriter w) { w.WriteInt32(42); }),
                  "c1 发出 Server RPC（SendRpc 返回 true）");
            scene.Frame(4);
            CheckEq(actor.MarkCount, 1, "服务端执行了 Server RPC（拥有者连接）");
            CheckEq(actor.LastMark, 42, "Server RPC 参数正确送达");
            CheckEq(c1Copy.MarkCount, 0, "客户端本地没有越权执行（发送路径不本地执行）");

            // ── ② 客户端 → 服务端：非拥有者发 Server RPC 被拒 ───────────
            Check(c2.Bridge.SendRpc(c2Copy, RpcMark,
                                    delegate(PMNetObject t, PMNetWriter w) { w.WriteInt32(99); }),
                  "c2 成功发出（发送侧合法）");
            scene.Frame(4);
            CheckEq(actor.MarkCount, 1, "非拥有者的 Server RPC 未被执行（仍是 1 次）");
            CheckGt(serverConnForC2.RpcRejected, 0, "服务端对 c2 连接的入站 RPC 拒绝计数非空");
            CheckGt(PMNetRpcReceive.Rejected, 0, "接收入口 Rejected 计数非空");

            // ── ③ 服务端 → 客户端：Client RPC 只发给拥有者 ──────────────
            Check(server.Bridge.SendRpc(actor, RpcReward,
                                        delegate(PMNetObject t, PMNetWriter w) { w.WriteInt32(7); }),
                  "服务端发出 Client RPC");
            scene.Frame(4);
            CheckEq(c1Copy.RewardCount, 1, "拥有者客户端执行了 Client RPC");
            CheckEq(c1Copy.LastReward, 7, "Client RPC 参数正确");
            CheckEq(c2Copy.RewardCount, 0, "非拥有者客户端没有收到 Client RPC");

            // ── ④ 服务端 → 全部相关连接：Multicast ──────────────────────
            Check(server.Bridge.SendRpc(actor, RpcBroadcast,
                                        delegate(PMNetObject t, PMNetWriter w) { w.WriteInt32(5); }),
                  "服务端发出 Multicast RPC");
            scene.Frame(4);
            CheckEq(c1Copy.BroadcastCount, 1, "c1 执行了 Multicast RPC");
            CheckEq(c2Copy.BroadcastCount, 1, "c2 执行了 Multicast RPC");
            CheckEq(c1Copy.LastBroadcast, 5, "Multicast 参数正确（c1）");
            CheckEq(c2Copy.LastBroadcast, 5, "Multicast 参数正确（c2）");

            // ── ⑤ 方向非法：客户端手工发一条 Client 方向 RPC 上去 ───────
            byte[] wrongDirection = BuildApplicationMessage(PMSessionMessageKind.Rpc,
                BuildRpcBody(c1Copy.NetId.Value, false, TestClassId, RpcReward, LayoutReward, 3));
            long rejectedBefore = server.Conn.RpcRejected;
            Check(c1.Conn.Transport.Send(PMStream.Unreliable, false, wrongDirection, 0, wrongDirection.Length),
                  "手工构造的 Client 方向 RPC 已进入传输层");
            scene.Frame(4);
            CheckEq(c1Copy.RewardCount, 1, "服务端收到的 Client 方向 RPC 未被执行");
            CheckGt(server.Conn.RpcRejected, rejectedBefore, "服务端把该包判为方向非法（RpcRejected 增长）");
            CheckStrEq(server.Conn.DisconnectReason.ToString(), "None",
                       "方向非法只拒绝、不断连（D-R0-43 的确定行为）");
            CheckEq(server.World.ConnectionCount, 2, "方向非法没有摘除任何连接");

            CheckGt(PMNetRpcReceive.Delivered, 0, "接收入口 Delivered 计数非空");
            CheckGt(server.Bridge.RpcSent, 0, "服务端 RpcSent 计数非空");
            CheckGt(c1.Bridge.RpcSent, 0, "客户端 RpcSent 计数非空");
        }

        // =================================================================================
        //  E. 协议不符
        // =================================================================================

        private static void TestProtocolMismatch()
        {
            // ── ① ParamLayoutId 不符 ⇒ 协议错误断连，且实现不执行 ───────
            Scenario scene = new Scenario();
            Endpoint server;
            Endpoint client;
            MakeActivatedPair(scene, 8001u, out server, out client);

            SessionActor actor = new SessionActor();
            actor.Health = 1;
            actor.Armor = 1;
            server.World.Spawn(actor, TestClassId);
            actor.OwnerConnection = server.Conn;
            server.Bridge.RegisterReplicatedObject(actor);
            scene.Frame(6);

            byte[] badLayout = BuildApplicationMessage(PMSessionMessageKind.Rpc,
                BuildRpcBody(actor.NetId.Value, false, TestClassId, RpcMark, (ushort)(LayoutMark ^ 0x0001), 11));
            client.Conn.Transport.Send(PMStream.Unreliable, false, badLayout, 0, badLayout.Length);
            scene.Frame(4);

            CheckEq(actor.MarkCount, 0, "布局不符的 RPC 没有被执行");
            CheckGt(server.Conn.ProtocolErrors, 0, "布局不符被计为应用层协议错误");
            Check(!server.Conn.Transport.IsConnected, "布局不符 ⇒ 真实 Transport 已断开");
            CheckStrEq(server.Conn.DisconnectReason.ToString(), "ProtocolError", "布局不符的断开原因为 ProtocolError");

            // ── ② ClassId 与目标不一致 ⇒ 断连 ──────────────────────────
            Scenario scene2 = new Scenario();
            Endpoint server2;
            Endpoint client2;
            MakeActivatedPair(scene2, 8002u, out server2, out client2);

            SessionActor actor2 = new SessionActor();
            server2.World.Spawn(actor2, TestClassId);
            actor2.OwnerConnection = server2.Conn;
            server2.Bridge.RegisterReplicatedObject(actor2);
            scene2.Frame(6);

            byte[] badClass = BuildApplicationMessage(PMSessionMessageKind.Rpc,
                BuildRpcBody(actor2.NetId.Value, false, TestClassId + 1u, RpcMark, LayoutMark, 12));
            client2.Conn.Transport.Send(PMStream.Unreliable, false, badClass, 0, badClass.Length);
            scene2.Frame(4);

            CheckEq(actor2.MarkCount, 0, "ClassId 不符的 RPC 没有被执行");
            CheckGt(server2.Conn.ProtocolErrors, 0, "ClassId 不符被计为协议错误");
            Check(!server2.Conn.Transport.IsConnected, "ClassId 不符 ⇒ 断开");

            // ── ③ 信封版本不符 ⇒ 断连 ──────────────────────────────────
            Scenario scene3 = new Scenario();
            Endpoint server3;
            Endpoint client3;
            MakeActivatedPair(scene3, 8003u, out server3, out client3);

            byte[] badVersion = BuildApplicationMessage(PMSessionMessageKind.Rpc,
                BuildRpcBody(1u, false, TestClassId, RpcMark, LayoutMark, 1));
            badVersion[0] = 9;
            client3.Conn.Transport.Send(PMStream.Unreliable, false, badVersion, 0, badVersion.Length);
            scene3.Frame(4);

            CheckGt(server3.Conn.ProtocolErrors, 0, "信封版本不符被计为协议错误");
            Check(!server3.Conn.Transport.IsConnected, "信封版本不符 ⇒ 断开");
            CheckEq(server3.Conn.InboundDroppedNotActivated, 0, "已激活连接不会走「未激活丢弃」路径");

            // ── ④ 复制域方向闸门：客户端上行**格式完全合法**的 Update ⇒ 服务端拒绝、不 ACK、不改写权威 ──
            //
            // 为什么必须用合法正文：复制层的接收路径（OnMessage → TryApplyRecord）**不做任何角色判断**，
            // 也不校验来源与版本；只要双方 ProtocolHash 一致，一条合法 Update 就会直接改写活对象。
            // 用坏 magic 会在「魔数/版本不符」分支提前失败，于是测到的是「解析兑底」而不是「方向拒绝」
            // —— 断言名与代码路径错位（上一轮正是这么漏检的）。这里的正文由真实 PMReplicationWriter 产出。
            Scenario scene4 = new Scenario();
            Endpoint server4;
            Endpoint client4;
            MakeActivatedPair(scene4, 8004u, out server4, out client4);

            SessionActor actor4 = new SessionActor();
            actor4.Health = 11;
            actor4.Armor = 22;
            server4.World.Spawn(actor4, TestClassId);
            actor4.OwnerConnection = server4.Conn;
            server4.Bridge.RegisterReplicatedObject(actor4);
            scene4.Frame(6);

            byte[] forgedUpdate = BuildApplicationMessage(PMSessionMessageKind.Replication,
                BuildReplicationUpdateBody(actor4.NetId.Value, 9001L, 0, PropIdHealth, 7777));

            long dirBefore = server4.Conn.ReplicationDirectionRejected;
            long appliedBefore = server4.Bridge.Replication.Stats.UpdateRecordsApplied;
            long acksBefore = server4.Bridge.Replication.Stats.AcksSent;

            Check(client4.Conn.Transport.Send(PMStream.Replication, false, forgedUpdate, 0, forgedUpdate.Length),
                  "伪造的复制 Update 已进入传输层（正文由真实 PMReplicationWriter 产出、魔数与版本合法）");
            scene4.Frame(2);

            CheckGt(server4.Conn.ReplicationDirectionRejected, dirBefore,
                    "服务端把客户端上行的复制 Update 判为方向非法（ReplicationDirectionRejected 增长）");
            CheckEq(actor4.Health, 11, "服务端权威对象的属性**未被改写**（闸门在写之前就拦住）");
            CheckEq(actor4.Armor, 22, "同对象的其他属性也未被改写");
            CheckEq(server4.Bridge.Replication.Stats.UpdateRecordsApplied, appliedBefore,
                    "服务端没有应用任何复制更新记录（UpdateRecordsApplied 未增长）");
            CheckEq(server4.Bridge.Replication.Stats.AcksSent, acksBefore, "服务端**没有回 ACK**（不确认伪造版本）");
            CheckEq(server4.Conn.ReplicationAckSent, 0, "服务端连接层没有发出任何复制版本确认");
            Check(!server4.Conn.Transport.IsConnected, "复制方向非法 ⇒ 真实 Transport 已断开");
            CheckStrEq(server4.Conn.DisconnectReason.ToString(), "ProtocolError",
                       "复制方向非法的断开原因为 ProtocolError");

            // ── ④′ 对照组：同一份字节走合法方向（服务端 → 客户端）⇒ 真的被应用 ──
            // 没有这条对照，“上面没生效”可能只是因为“载荷本来就不合法”，
            // 而不是“方向被拦住”。这里用**逐字节相同**的正文证明载荷本身是可应用的。
            Scenario scene4b = new Scenario();
            Endpoint server4b;
            Endpoint client4b;
            MakeActivatedPair(scene4b, 8006u, out server4b, out client4b);

            SessionActor actor4b = new SessionActor();
            actor4b.Health = 11;
            server4b.World.Spawn(actor4b, TestClassId);
            actor4b.OwnerConnection = server4b.Conn;
            server4b.Bridge.RegisterReplicatedObject(actor4b);
            scene4b.Frame(6);

            SessionActor clientCopy4b = Find(client4b, actor4b.NetId) as SessionActor;
            Check(clientCopy4b != null, "对照组前置：客户端拿到了副本");
            if (clientCopy4b != null)
            {
                byte[] sameBody = BuildReplicationUpdateBody(actor4b.NetId.Value, 9001L, 0, PropIdHealth, 7777);
                byte[] sameEnvelope = BuildApplicationMessage(PMSessionMessageKind.Replication, sameBody);

                Check(server4b.Conn.Transport.Send(PMStream.Replication, false, sameEnvelope, 0, sameEnvelope.Length),
                      "对照组：同一份正文（服务端 → 客户端）已进入传输层");
                scene4b.Frame(2);

                CheckEq(clientCopy4b.Health, 7777,
                        "对照组：同一份字节在合法方向上真的被应用（证明上面的拒绝是方向闸门，不是载荷非法）");
                CheckGt(client4b.Bridge.Replication.Stats.UpdateRecordsApplied, 0,
                        "对照组：客户端确实应用了这条更新记录");
                CheckGt(client4b.Bridge.Replication.Stats.AcksSent, 0,
                        "对照组：客户端还回传了版本确认（与“服务端不 ACK 上行 Update”形成对照）");
                Check(client4b.Conn.Transport.IsConnected, "对照组：客户端连接保持连通");
            }

            // ── ④″ 信封类型 ↔ 内部消息类型不一致（在**方向合法**的信封里造假）⇒ 断连 ──
            // 服务端只允许 ReplicationAck 这个信封，所以这条路径必须落在
            // 「一致性检查」而不是「方向闸门」上。
            Scenario scene4c = new Scenario();
            Endpoint server4c;
            Endpoint client4c;
            MakeActivatedPair(scene4c, 8007u, out server4c, out client4c);

            byte[] mismatched = BuildApplicationMessage(PMSessionMessageKind.ReplicationAck,
                BuildReplicationUpdateBody(1u, 1L, 0, PropIdHealth, 1));
            client4c.Conn.Transport.Send(PMStream.Replication, false, mismatched, 0, mismatched.Length);
            scene4c.Frame(2);

            CheckGt(server4c.Conn.ProtocolErrors, 0, "复制信封类型与内部消息类型不一致被计为协议错误");
            CheckEq(server4c.Conn.ReplicationDirectionRejected, 0,
                    "该路径走的是信封/内部类型一致性检查，而不是方向闸门");
            Check(!server4c.Conn.Transport.IsConnected, "复制信封类型不符 ⇒ 断开");

            // ── ④‴ 客户端侧方向：服务端下行的 ReplicationAck ⇒ 客户端拒绝 ──
            Scenario scene4d = new Scenario();
            Endpoint server4d;
            Endpoint client4d;
            MakeActivatedPair(scene4d, 8008u, out server4d, out client4d);

            PMNetWriter ackWriter = new PMNetWriter(32);
            List<PMRepAck> acks = new List<PMRepAck>(1);
            acks.Add(new PMRepAck(1u, 1L));
            PMReplicationWriter.WriteAck(ackWriter, acks);
            byte[] legalAck = BuildApplicationMessage(PMSessionMessageKind.ReplicationAck, ackWriter.ToArray());

            long clientDirBefore = client4d.Conn.ReplicationDirectionRejected;
            server4d.Conn.Transport.Send(PMStream.Replication, false, legalAck, 0, legalAck.Length);
            scene4d.Frame(2);

            CheckGt(client4d.Conn.ReplicationDirectionRejected, clientDirBefore,
                    "客户端把服务端下行的复制版本确认判为方向非法（客户端只接受 Replication Update）");
            Check(!client4d.Conn.Transport.IsConnected, "客户端方向非法 ⇒ 真实 Transport 已断开");

            // ── ⑤ 未知 RpcId ⇒ 拒绝但不断连（对照断言）────────────────
            Scenario scene5 = new Scenario();
            Endpoint server5;
            Endpoint client5;
            MakeActivatedPair(scene5, 8005u, out server5, out client5);

            SessionActor actor5 = new SessionActor();
            actor5.Health = 1;
            server5.World.Spawn(actor5, TestClassId);
            actor5.OwnerConnection = server5.Conn;
            server5.Bridge.RegisterReplicatedObject(actor5);
            scene5.Frame(6);

            byte[] unknownRpc = BuildApplicationMessage(PMSessionMessageKind.Rpc,
                BuildRpcBody(actor5.NetId.Value, false, TestClassId, (ushort)9999, LayoutMark, 1));
            client5.Conn.Transport.Send(PMStream.Unreliable, false, unknownRpc, 0, unknownRpc.Length);
            scene5.Frame(4);

            CheckGt(server5.Conn.RpcRejected, 0, "未知 RpcId 被拒（入站拒绝计数非空）");
            Check(server5.Conn.Transport.IsConnected, "未知 RpcId 只拒绝、不断连（与布局不符相反）");
            CheckEq(actor5.MarkCount, 0, "未知 RpcId 没有执行任何实现");

            // ── ⑥ 对照组：正确布局 ⇒ 同一个世界真的能执行 ───────────────
            PMNetObject clientReplica = Find(client5, actor5.NetId);
            Check(clientReplica != null, "对照组前置：客户端拿到了副本");
            if (clientReplica == null)
            {
                return;
            }

            Check(client5.Bridge.SendRpc(clientReplica, RpcMark,
                                         delegate(PMNetObject t, PMNetWriter w) { w.WriteInt32(4); }),
                  "对照组：正确布局的 RPC 发送成功");
            scene5.Frame(4);
            CheckEq(actor5.MarkCount, 1, "对照组：正确布局的 RPC 真的执行了（证明上面的拒绝不是空过）");
        }

        // =================================================================================
        //  F. 原生校验失败
        // =================================================================================

        private static void TestValidateDisconnect()
        {
            Scenario scene = new Scenario();
            Endpoint server;
            Endpoint client;
            MakeActivatedPair(scene, 9001u, out server, out client);

            SessionActor actor = new SessionActor();
            actor.Health = 1;
            server.World.Spawn(actor, TestClassId);
            actor.OwnerConnection = server.Conn;
            server.Bridge.RegisterReplicatedObject(actor);
            scene.Frame(6);

            PMNetObject clientReplica = Find(client, actor.NetId);
            Check(clientReplica != null, "客户端拿到了 actor 副本");
            if (clientReplica == null)
            {
                return;
            }

            // ── 对照：校验通过 ⇒ 正常执行 ──────────────────────────────
            Check(client.Bridge.SendRpc(clientReplica, RpcWatch,
                                        delegate(PMNetObject t, PMNetWriter w) { w.WriteInt32(0); }),
                  "对照组：校验参数合法的 Watch RPC 已发出");
            scene.Frame(4);
            CheckEq(actor.WatchCount, 1, "对照组：_Validate 通过 ⇒ 实现被执行");
            CheckEq(PMNetRpcReceive.DisconnectRequests, 0, "对照组没有请求断连");
            Check(server.Conn.Transport.IsConnected, "对照组连接仍然连通");

            // ── 正例：_Validate 返回 false ⇒ 请求断连并落到真实 Transport ──
            long disconnectBefore = PMNetRpcReceive.DisconnectRequests;
            Check(client.Bridge.SendRpc(clientReplica, RpcWatch,
                                        delegate(PMNetObject t, PMNetWriter w) { w.WriteInt32(1); }),
                  "校验参数非法的 Watch RPC 已发出");
            scene.Frame(4);

            CheckEq(actor.WatchCount, 1, "校验失败的 RPC 没有执行实现（仍是 1 次）");
            CheckGt(PMNetRpcReceive.DisconnectRequests, disconnectBefore, "接收入口发出了断连请求");
            CheckEq(server.Conn.RpcDisconnectRequests, 1, "连接层收到断连请求恰好 1 次");
            Check(!server.Conn.Transport.IsConnected, "真实 Transport 已断开（Validate 失败 ⇒ 断连）");
            CheckStrEq(server.Conn.DisconnectReason.ToString(), "ProtocolError",
                       "Validate 失败的断开原因为 ProtocolError");
            CheckEq(server.World.ConnectionCount, 0, "断开后世界侧连接已摘除");
            CheckEq(server.Bridge.Replication.ConnectionCount, 0, "断开后复制侧连接已摘除");
            CheckGt(server.Conn.RpcRejected, 0, "服务端入站 RPC 拒绝计数非空");
            CheckGt(server.Bridge.ClosedConnections, 0, "断开被桥记账（ClosedConnections 非空）");
        }

        // =================================================================================
        //  G. 边界
        // =================================================================================

        private static void TestBoundsAndOversize()
        {
            // ── ① 略过适配器、直接把**超过线格式上限**的消息交给传输层 ⇒ 显式拒绝（不截断）──
            Scenario scene = new Scenario();
            Endpoint server;
            Endpoint client;
            MakeActivatedPair(scene, 10001u, out server, out client);

            CheckEq(PMApplicationEnvelope.MaxTransportableBytes, 65535,
                    "适配层声明的可搬运上限 = ushort.MaxValue");
            CheckEq(PMApplicationEnvelope.EnforceableMaxBytes, 65535,
                    "适配层执行的上限 = min(契约 64KiB, 传输 65535) = 65535");
            CheckEq(PMTransport.MaxTransportableMessageBytes, 65535,
                    "传输层自己声明的上限与适配层同源（65535）");

            PMNetWriter writer = new PMNetWriter(1024);
            PMApplicationEnvelope.WriteHeader(writer, PMSessionMessageKind.Replication);
            byte[] fill = new byte[70000];
            writer.WriteRawBytes(fill, 0, fill.Length);
            byte[] oversize = writer.ToArray();
            CheckEq(oversize.Length, 70002, "构造出的消息长度 = 70002（超上限）");

            long oversizeDatagramsBefore = server.Conn.Transport.Stats.DatagramsSent;
            Check(!server.Conn.Transport.Send(PMStream.Replication, false, oversize, 0, oversize.Length),
                  "超过线格式上限的消息被传输层**显式拒绝**（旧实现是 (ushort) 静默截断）");
            CheckEq(server.Conn.Transport.Stats.DatagramsSent, oversizeDatagramsBefore,
                    "被拒绝的超限消息没有产生任何数据报");
            CheckStrEq(server.Conn.Transport.LastSendRejectReason.ToString(), "MessageTooLarge",
                       "拒绝原因被明确标注为 MessageTooLarge（不是可靠窗溢出）");
            CheckGt(server.Conn.Transport.Stats.SendRejectedOversize, 0, "超限拒绝被记账（SendRejectedOversize）");
            CheckEq(server.Conn.Transport.ReliableOutstanding, 0, "可靠缓冲里没有残留半个消息");
            Check(server.Conn.Transport.IsConnected, "超限是调用方的错，不因此断开连接");

            // ── ①′ 上限内的**大**消息：跨帧分片发完，且每帧严格不超过预算 ──
            // 用一条合法 RPC 信封（目标 NetId 不存在 ⇒ 对端只拒绝、不断连）把载荷推到 60000 字节级：
            // 它必须仍在 65535 以内（否则就是①的情形），却需要 >32 个分片才能测出跨帧与预算。
            PMNetWriter bigWriter = new PMNetWriter(1024);
            PMApplicationEnvelope.WriteHeader(bigWriter, PMSessionMessageKind.Rpc);
            bigWriter.WriteUInt64(1UL);            // 不存在的 NetId
            bigWriter.WriteBool(false);
            bigWriter.WriteUInt64(TestClassId);
            bigWriter.WriteUInt64(RpcMark);
            bigWriter.WriteUInt64(LayoutMark);
            byte[] bigFill = new byte[60000];
            for (int i = 0; i < bigFill.Length; i++) { bigFill[i] = (byte)(i % 251); }
            bigWriter.WriteRawBytes(bigFill, 0, bigFill.Length);
            byte[] big = bigWriter.ToArray();
            Check(big.Length <= PMTransport.MaxTransportableMessageBytes && big.Length > 32 * 1166,
                  "①′ 的载荷在 65535 以内、但需要 >32 个分片（实际 " + big.Length + " 字节）");

            Check(server.Conn.Transport.Send(PMStream.Reliable, true, big, 0, big.Length),
                  "上限内的大消息被接受（自动分片）");

            long maxPerFrame = 0;
            int framesUsed = 0;
            for (int f = 0; f < 12; f++)
            {
                long before = server.Conn.Transport.Stats.DatagramsSent;
                scene.Frame(1);
                long delta = server.Conn.Transport.Stats.DatagramsSent - before;
                if (delta > 0) { framesUsed++; }
                if (delta > maxPerFrame) { maxPerFrame = delta; }
            }

            CheckGt(framesUsed, 1, "超过单帧预算的消息需要跨帧发送（实际用了 " + framesUsed + " 帧）");
            Check(maxPerFrame <= PMNetSessionBridge.DefaultFrameDatagramBudget,
                  "单帧发送数据报数严格不超过预算（实际峰值 " + maxPerFrame + " ≤ "
                  + PMNetSessionBridge.DefaultFrameDatagramBudget + "）");
            CheckGt(maxPerFrame, 0, "确实发过数据报（用例不是空过）");
            CheckEq(PMNetSessionBridge.DefaultFrameDatagramBudget, 32, "每帧数据报预算默认 32（与 Transport 初值同源）");
            CheckEq(client.Conn.LastInboundMessageBytes, big.Length,
                    "上限内的 60000 字节级消息被对端**完整**收到（分片路径正常）");
            Check(client.Conn.Transport.IsConnected, "未知目标 RPC 只拒绝、不断连（对照条件成立）");
            CheckGt(client.Conn.RpcRejected, 0, "该大消息被接收入口拒绝（证明它真的走到了应用层）");

            // ── ② 适配层的超限闸门：显式失败，且不交给传输层 ────────────
            Scenario gateScene = new Scenario();
            Endpoint gateServer;
            Endpoint gateClient;
            MakeActivatedPair(gateScene, 10004u, out gateServer, out gateClient);

            SessionActor gateActor = new SessionActor();
            gateActor.Health = 1;
            gateServer.World.Spawn(gateActor, TestClassId);
            gateActor.OwnerConnection = gateServer.Conn;
            gateServer.Bridge.RegisterReplicatedObject(gateActor);
            gateScene.Frame(6);

            long oversizeBefore = gateServer.Bridge.RpcOversize;
            long datagramsBefore = gateServer.Conn.Transport.Stats.DatagramsSent;
            Check(!gateServer.Bridge.SendRpc(gateActor, RpcReward,
                                             delegate(PMNetObject t, PMNetWriter w)
                                             {
                                                 w.WriteBytesValue(new byte[70000]);
                                             }),
                  "携带 70000 字节参数的 RPC 被适配层拒绝");
            CheckGt(gateServer.Bridge.RpcOversize, oversizeBefore, "RpcOversize 计数增长（显式失败）");
            CheckEq(gateServer.Conn.Transport.Stats.DatagramsSent, datagramsBefore,
                    "被拒绝的超限 RPC 没有交给传输层（不会被静默截断）");
            CheckEq(gateServer.Conn.Transport.ReliableOutstanding, 0, "可靠缓冲里没有残留半个消息");

            // ── ③ 入站队列有界且可观测（不静默无限排队）──────────────────
            Scenario queueScene = new Scenario();
            Endpoint solo = queueScene.Add("solo", true, 10002u, 1, PMSessionPeerRole.Client, 1, 1,
                                           TestProtocolHash, TestProtocolHash);
            string error;
            solo.Conn.TryActivate(out error);

            byte[] junk = new byte[64];
            junk[0] = 1;
            for (int i = 0; i < 5000; i++)
            {
                solo.Conn.OnDatagram(junk, 0, junk.Length);
            }

            CheckGt(solo.Conn.InboundQueueDropped, 0, "入站队列上限生效并计入 DroppedInbound（有界且可观测）");
            CheckEq(solo.Conn.Transport.Stats.DatagramsReceived, 0, "未 Update 时没有任何数据报被解析（只入队）");
            CheckEq(solo.Conn.MessagesReceived, 0, "未 Update 时没有消息被派发");

            // ── ④ 未知来源：未激活连接的入站一律丢弃 ────────────────────
            Scenario unknownScene = new Scenario();
            Endpoint unauthenticated = unknownScene.Add("unauth", true, 10003u, 1, PMSessionPeerRole.Client, 1, 1,
                                                        TestProtocolHash, TestProtocolHash);
            byte[] something = BuildApplicationMessage(PMSessionMessageKind.Rpc,
                BuildRpcBody(1u, false, TestClassId, RpcMark, LayoutMark, 1));
            byte[] unknownDatagram = CraftTransportDatagram(10003u, something);
            unauthenticated.Conn.OnDatagram(unknownDatagram, 0, unknownDatagram.Length);
            unauthenticated.Conn.Update(unknownScene.Now);
            CheckEq(unauthenticated.Conn.InboundDroppedNotActivated, 1, "未认证来源的消息被丢弃（不派发）");
            CheckEq(unauthenticated.Conn.RpcApplied, 0, "未认证来源没有触发任何 RPC 执行");

            // ── ⑤ 旧世代数据报被传输层丢弃（不作用到本会话）────────────
            //
            // 注意：未激活连接上的字节已经**进不了 Transport**（见 §B①），
            // 因此本覆盖必须用**已激活连接**才能真实走到 `DroppedStaleSession`。
            Scenario staleScene = new Scenario();
            Endpoint staleServer;
            Endpoint staleClient;
            MakeActivatedPair(staleScene, 11003u, out staleServer, out staleClient);

            long staleMessagesBefore = staleClient.Conn.MessagesReceived;
            long staleDroppedBefore = staleClient.Conn.Transport.Stats.DroppedStaleSession;
            long staleInboundBefore = staleClient.Conn.Transport.Stats.DatagramsReceived;

            byte[] staleDatagram = CraftTransportDatagram(11003u + 1u, something);
            staleClient.Conn.OnDatagram(staleDatagram, 0, staleDatagram.Length);
            staleClient.Conn.Update(staleScene.Now);

            CheckEq(staleClient.Conn.MessagesReceived, staleMessagesBefore,
                    "旧世代数据报没有到达应用层（仍是最初的 " + staleMessagesBefore + " 条）");
            CheckGt(staleClient.Conn.Transport.Stats.DroppedStaleSession, staleDroppedBefore,
                    "旧世代丢弃被传输层记账（已激活连接上的真实路径）");
            CheckGt(staleClient.Conn.Transport.Stats.DatagramsReceived, staleInboundBefore,
                    "对照：该数据报确实进了 Transport（否则 DroppedStaleSession 不会是真实路径）");
            Check(staleClient.Conn.IsReady, "旧世代数据报不导致断连");
        }

        // =================================================================================
        //  H. 丢包 / 重排 / 收敛
        // =================================================================================

        private static void TestLossAndConvergence()
        {
            // ── ① Create 在丢包下仍收敛（可靠域重传 + 去重）─────────────
            // 使用生产默认保活；不能依赖旧 ACK 循环提供后续丢包探测流量。
            Scenario scene = new Scenario();
            scene.TuneTransportConfig = delegate(PMTransportConfig c)
            {
                c.KeepAliveIntervalMs = 1000L;
                c.IdleTimeoutMs = 10000L;
            };
            Endpoint server;
            Endpoint client;
            MakeActivatedPair(scene, 11001u, out server, out client);

            scene.Hub.DropNextFrom("server", 3);

            SessionActor actor = new SessionActor();
            actor.Health = 100;
            actor.Armor = 3;
            server.World.Spawn(actor, TestClassId);
            actor.OwnerConnection = server.Conn;
            server.Bridge.RegisterReplicatedObject(actor);

            scene.Frame(40);

            PMNetObject replicatedObject = Find(client, actor.NetId);
            Check(replicatedObject != null, "丢包后 Create 最终到达（可靠域重传）");
            CheckGt(server.Conn.Transport.Stats.ReliableResent, 0, "服务端发生过可靠重传（丢包被恢复）");
            CheckGt(scene.Hub.Dropped, 0, "链路确实丢掉过数据报");

            SessionActor clientActor = replicatedObject as SessionActor;
            if (clientActor == null)
            {
                return;
            }

            CheckEq(clientActor.CreateCount, 1, "Create 恰好交付一次（重传没有重复创建）");
            CheckEq(clientActor.Health, 100, "丢包后初值仍然正确");

            // ── ② 可靠 RPC 在丢包 + 重复数据报下恰好交付一次 ────────────
            scene.Frame(10);
            long reliableSentBefore = client.Conn.Transport.Stats.ReliableSent;
            Check(client.Bridge.SendRpc(clientActor, RpcMark,
                                        delegate(PMNetObject t, PMNetWriter w) { w.WriteInt32(777); }),
                  "客户端发出可靠的 Server RPC");
            CheckGt(client.Conn.Transport.Stats.ReliableSent, reliableSentBefore, "RPC 进入了可靠发送缓冲");

            scene.Hub.DropNextFrom("client", 1);      // 丢掉承载该 RPC 的数据报
            scene.Hub.DuplicateNextFrom("client", 1); // 随后重复投递一次
            scene.Frame(180); // 覆盖至少两个生产保活周期，等待ACK/NAK驱动恢复。

            CheckEq(actor.MarkCount, 1, "可靠 RPC 恰好交付一次（丢包重传 + 数据报去重）");
            CheckEq(actor.LastMark, 777, "可靠 RPC 参数正确");
            CheckGt(server.Conn.Transport.Stats.ReliableResent, 0, "服务端侧观测到可靠重传");
            CheckGt(scene.Hub.Duplicated, 0, "链路确实重复投递过数据报");

            // ── ③ 复制属性在丢包 + 乱序下最终收敛 ──────────────────────
            actor.SetHealth(42);
            actor.SetArmor(9);
            scene.Hub.DropNextFrom("server", 4);
            scene.Hub.ReorderNextFrom("server", 3);
            scene.Frame(30);
            scene.Frame(40);

            CheckEq(clientActor.Health, 42, "丢包 + 乱序后 Health 最终收敛到 42");
            CheckEq(clientActor.Armor, 9, "丢包 + 乱序后 Armor 最终收敛到 9");
            CheckGt(server.Bridge.Replication.Stats.UpdateRecordsSent, 0, "服务端发过复制更新记录");
            CheckGt(server.Bridge.Replication.Stats.AcksReceived, 0, "服务端收到过复制版本确认");
            CheckGt(client.Bridge.Replication.Stats.UpdateRecordsApplied, 0, "客户端应用过复制更新记录");
            CheckGt(client.Bridge.Replication.Stats.AcksSent, 0, "客户端发送过复制版本确认");
            CheckGt(server.Bridge.Replication.Stats.InitialFullSends, 0, "基线缺失时做过全量初始同步");

            // ── ④ Destroy 之后 RPC 被拒绝，且客户端不再持有副本 ────────
            Check(server.Bridge.DestroyObject(actor), "服务端销毁对象成功");
            scene.Frame(20);

            Check(Find(client, actor.NetId) == null, "客户端已摘除该对象");
            CheckEq(clientActor.DestroyCount, 1, "客户端销毁回调恰好一次");

            long inactiveBefore = server.Bridge.RpcRejectedInactive;
            Check(!server.Bridge.SendRpc(actor, RpcReward,
                                         delegate(PMNetObject t, PMNetWriter w) { w.WriteInt32(1); }),
                  "销毁后服务端 SendRpc 被拒绝");
            CheckGt(server.Bridge.RpcRejectedInactive, inactiveBefore, "销毁后拒绝计数增长（RpcRejectedInactive）");
            Check(!client.Bridge.SendRpc(clientActor, RpcMark,
                                         delegate(PMNetObject t, PMNetWriter w) { w.WriteInt32(1); }),
                  "销毁后客户端 SendRpc 也被拒绝");
            CheckEq(actor.MarkCount, 1, "销毁后的 RPC 没有执行任何实现");

            // ── ⑤ 计数器可达性（非空断言）──────────────────────────────
            CheckGt(client.Conn.MessagesReceived, 0, "客户端入站消息计数非空");
            CheckGt(server.Conn.MessagesSent, 0, "服务端出站消息计数非空");
            CheckGt(server.Bridge.Frames, 0, "桥驱动帧计数非空");
            CheckGt(PMNetRpcReceive.Delivered, 0, "接收入口 Delivered 非空");
        }

        // =================================================================================
        //  I. 生命周期与计数
        // =================================================================================

        private static void TestLifecycleAndCounters()
        {
            Scenario scene = new Scenario();
            Endpoint server;
            Endpoint client;
            MakeActivatedPair(scene, 12001u, out server, out client);

            SessionActor actor = new SessionActor();
            actor.Health = 5;
            server.World.Spawn(actor, TestClassId);
            actor.OwnerConnection = server.Conn;
            server.Bridge.RegisterReplicatedObject(actor);
            scene.Frame(6);

            CheckEq(server.World.ConnectionCount, 1, "断开前世界侧有 1 条连接");
            CheckEq(server.Bridge.Replication.ConnectionCount, 1, "断开前复制侧有 1 条连接");
            CheckEq(server.Bridge.ConnectionCount, 1, "断开前桥内有 1 条连接");

            // ── ① 断开摘除世界与复制登记（成对）────────────────────────
            server.Conn.Disconnect(PMDisconnectReason.LocalClosed);
            CheckEq(server.World.ConnectionCount, 0, "断开后世界侧连接已摘除");
            CheckEq(server.Bridge.Replication.ConnectionCount, 0, "断开后复制侧连接已摘除");
            CheckEq(server.Bridge.ConnectionCount, 0, "断开后桥内连接已摘除");
            CheckGt(server.Bridge.ClosedConnections, 0, "ClosedConnections 计数非空");

            // ── ② Disconnect 幂等 ───────────────────────────────────────
            long initiated = server.Conn.DisconnectsInitiated;
            server.Conn.Disconnect(PMDisconnectReason.LocalClosed);
            server.Conn.Disconnect(PMDisconnectReason.ProtocolError);
            CheckEq(server.Conn.DisconnectsInitiated, initiated, "重复 Disconnect 不再累加（幂等）");

            // ── ③ Dispose 幂等 + 释放后不再参与 ────────────────────────
            server.Conn.Dispose();
            server.Conn.Dispose();
            CheckEq(server.World.ConnectionCount, 0, "Dispose 两次后世界侧仍为 0（幂等）");
            Check(!server.Conn.IsReady, "Dispose 后 IsReady=false");
            Check(!server.Bridge.SendRpc(actor, RpcMark, delegate(PMNetObject t, PMNetWriter w) { w.WriteInt32(1); }),
                  "Dispose 后 SendRpc 被拒绝");

            long frames = server.Bridge.Frames;
            server.Bridge.Update(scene.Now);
            CheckEq(server.Bridge.Frames, frames + 1, "Dispose 后 Update 仍可安全驱动（不抛异常）");
            CheckEq(server.Bridge.ConnectionCount, 0, "Dispose 后桥内连接仍为 0");

            // ── ④ 桥 Dispose 幂等 ──────────────────────────────────────
            client.Bridge.Dispose();
            client.Bridge.Dispose();
            CheckEq(client.Bridge.ConnectionCount, 0, "桥 Dispose 两次后连接数为 0（幂等）");
            CheckEq(client.World.ConnectionCount, 0, "桥 Dispose 后世界侧连接已摘除");
            CheckEq(client.Bridge.Replication.ConnectionCount, 0, "桥 Dispose 后复制侧连接已摘除");
            Check(!client.Conn.IsReady, "桥 Dispose 后连接不再就绪");

            scene.Frame(2);
            Check(true, "端点在释放后继续驱动帧不抛异常");

            // ── ⑤ 全局计数器可达性（非空断言，防止「零路径」空过）──────
            CheckGt(server.Conn.MessagesSent + client.Conn.MessagesSent, 0, "出站消息计数非空");
            CheckGt(server.Conn.MessagesReceived + client.Conn.MessagesReceived, 0, "入站消息计数非空");
            CheckGt(server.Conn.Transport.Stats.DatagramsSent + client.Conn.Transport.Stats.DatagramsSent, 0,
                    "传输层发送计数非空");
            CheckGt(server.Conn.TransportAckNotifications + client.Conn.TransportAckNotifications, 0,
                    "传输层 ack 通知计数非空（底层包 ack 被记账但不推进复制基线）");
            CheckGt(PMNetRpcReceive.Delivered + PMNetRpcReceive.Rejected + PMNetRpcReceive.MalformedCount, 0,
                    "接收入口各判定计数非空");
        }

        // =================================================================================
        //  J. 每帧数据报预算：严格上界（帧首 31 + 帧尾 32 场景）
        // =================================================================================

        /// <summary>
        /// 复现「帧首 31 + 帧尾 32 = 63」这个真实上界，并证明它已被收口到 &lt;= 预算。
        ///
        /// 场景形状（缺一不可，这正是上一轮漏检的原因）：
        ///   ① 帧首还剩**一些但不满额**的积压（31 个数据报）⇒ 旧实现的帧尾闸门（已发 >= 32）不触发；
        ///   ② 同一帧内又有**本帧新产出**（一条 ~55KB 的生命周期批次，分片后约 46 个数据报）
        ///      ⇒ 帧尾会再发满 32 个。
        /// 旧实现的帧内总量 = 31 + 32 = 63；新实现 = 帧首 31 + 帧尾 1 = 32，其余顺延到后续帧。
        /// </summary>
        private static void TestFrameDatagramBudgetStrict()
        {
            Scenario scene = new Scenario();

            // 单端场景：这条连接的链路没有对端（TestLink.Peers 为空），
            // 因此这里只观察「本端一帧发了多少个数据报」，不受对端处理影响。
            Endpoint server = scene.Add("budget", true, 13001u, 1, PMSessionPeerRole.Client, 1, 1,
                                        TestProtocolHash, TestProtocolHash);

            string error;
            Check(server.Conn.TryActivate(out error), "预算用例：服务端连接激活成功（" + (error ?? "ok") + "）");
            CheckEq(server.Bridge.FrameDatagramBudget, 32, "每帧数据报预算默认 32");
            CheckEq(server.Conn.Transport.MaxDatagramsPerUpdate, 32, "传输层单次发送上限同为 32");

            // 帧首积压：31 条 1166 字节的不可靠消息，各自独占一个数据报
            int maxPayload = 1200 - 18 - 16;
            byte[] fill = new byte[maxPayload];
            for (int i = 0; i < 31; i++)
            {
                server.Conn.Transport.Send(PMStream.Replication, false, fill, 0, maxPayload);
            }

            // 同帧新产出：250 个带 200 字节初值的对象 ⇒ 批次约 250*214 ≈ 53KB ⇒ 约 46 个分片
            for (int i = 0; i < 250; i++)
            {
                BigActor big = new BigActor();
                server.World.Spawn(big, BigClassId);
            }

            CheckEq(server.World.PendingEventCount(server.Conn), 250, "生命周期待发表里有 250 条 Create");
            CheckEq(server.Conn.Transport.Stats.DatagramsSent, 0, "驱动之前一个数据报都没发");

            long deferredBefore = server.Bridge.DatagramsDeferredByBudget;
            long before = server.Conn.Transport.Stats.DatagramsSent;

            server.Bridge.Update(scene.Now);

            long firstFrame = server.Conn.Transport.Stats.DatagramsSent - before;
            CheckEq(firstFrame, 32, "帧内总发送量**严格**等于预算（旧实现此处是 63）");
            CheckEq(server.Bridge.LifecycleOversize, 0, "该批次未超应用消息上限（否则会走 K 的断连路径）");
            CheckEq(server.Bridge.LifecycleMessagesSent, 1, "生命周期批次已交给传输层（不是被丢掉）");
            CheckGt(server.Conn.Transport.ReliableOutstanding, 32,
                    "帧内新产出的可靠分片数 > 32（旧实现的帧尾会再发满 32 个 ⇒ 31+32=63）");
            CheckEq(server.Bridge.DatagramsDeferredByBudget, deferredBefore,
                    "帧首只发了 31 ⇒ 帧尾仍有余量：不是跳过，而是**减量**发出（合计仍是 32）");

            // 后续帧逐帧检查：任何一帧都不得超过预算
            long peak = firstFrame;
            long laterTotal = 0;
            long lastDelta = -1;
            for (int f = 0; f < 10; f++)
            {
                long b = server.Conn.Transport.Stats.DatagramsSent;
                scene.Now += 16L;
                server.Bridge.Update(scene.Now);
                long delta = server.Conn.Transport.Stats.DatagramsSent - b;
                laterTotal += delta;
                lastDelta = delta;
                if (delta > peak) { peak = delta; }
            }

            Check(peak <= server.Bridge.FrameDatagramBudget,
                  "多帧峰值严格不超过预算（峰值 " + peak + " ≤ " + server.Bridge.FrameDatagramBudget + "）");
            CheckGt(laterTotal, 0, "后续帧继续把顺延的数据报发出去（不是卡死）");
            CheckEq(lastDelta, 0, "积压最终被排空（末帧不再产生数据报）");

            // ── 第二种形状：帧首就把预算发满 ⇒ 帧尾的 flush 必须被**跳过**（而不是再发 32 个）──
            Scenario saturated = new Scenario();
            Endpoint satServer = saturated.Add("budget-sat", true, 13002u, 1, PMSessionPeerRole.Client, 1, 1,
                                               TestProtocolHash, TestProtocolHash);
            Check(satServer.Conn.TryActivate(out error),
                  "饱和形状：服务端连接激活成功（" + (error ?? "ok") + "）");

            for (int i = 0; i < 40; i++)
            {
                satServer.Conn.Transport.Send(PMStream.Replication, false, fill, 0, maxPayload);
            }

            // 同帧新产出，保证帧尾确实有东西“想发”（对比“本帧已经没东西可发”）
            BigActor satBig = new BigActor();
            satServer.World.Spawn(satBig, BigClassId);

            long satDeferred = satServer.Bridge.DatagramsDeferredByBudget;
            long satBefore = satServer.Conn.Transport.Stats.DatagramsSent;
            satServer.Bridge.Update(saturated.Now);
            long satFrame = satServer.Conn.Transport.Stats.DatagramsSent - satBefore;

            CheckEq(satFrame, 32, "帧首发满预算 ⇒ 本帧仍然只发 32 个（旧实现会再补 32 个 ⇒ 64）");
            CheckGt(satServer.Bridge.DatagramsDeferredByBudget, satDeferred,
                    "帧尾因预算归零被跳过并记账（DatagramsDeferredByBudget）");
            CheckEq(satServer.Bridge.LifecycleMessagesSent, 1,
                    "被跳过的是 flush，消息本身仍在传输层队列里（可靠消息不丢）");
        }

        // =================================================================================
        //  K. 生命周期批次超限：显式断连，而不是静默丢弃
        // =================================================================================

        /// <summary>
        /// 世界的 `BuildLifecycleBatch` 在返回批次前就已清空待发表，因此一批发不出去的
        /// Create/Destroy 没有任何重发机会 —— 对端会永远缺这些对象（后续复制 Update 被当作未知对象丢弃、
        /// 指向它们的 RPC 被 UnknownTarget 拒），且不可自愈。
        /// 本层必须把它变成**显式失败**：计数 + 专用断开原因 + 成对摘除；重连由世界重新排队全量 Create。
        /// </summary>
        private static void TestLifecycleBatchOversize()
        {
            Scenario scene = new Scenario();
            Endpoint server;
            Endpoint client;
            MakeActivatedPair(scene, 14001u, out server, out client);

            // 对照：小批次正常送达
            BigActor small = new BigActor();
            Check(server.World.Spawn(small, BigClassId), "小批次：Spawn 成功");
            scene.Frame(8);
            Check(Find(client, small.NetId) != null, "小批次：对端拿到了 Create（对照路径可用）");
            CheckEq(server.Bridge.LifecycleDisconnects, 0, "小批次没有触发任何断连");
            CheckEq(server.Bridge.LifecycleOversize, 0, "小批次没有超限");

            // 超限：一个批次里 400 条带 200 字节初值的 Create ⇒ 约 86KB > 64KiB 应用消息上限
            for (int i = 0; i < 400; i++)
            {
                BigActor big = new BigActor();
                server.World.Spawn(big, BigClassId);
            }

            long oversizeBefore = server.Bridge.LifecycleOversize;
            long discBefore = server.Bridge.LifecycleDisconnects;
            scene.Frame(1);

            CheckGt(server.Bridge.LifecycleOversize, oversizeBefore, "超限批次被显式记为失败（LifecycleOversize）");
            CheckGt(server.Bridge.LifecycleDisconnects, discBefore, "超限批次触发显式断连（不再只是记一条告警）");
            Check(!server.Conn.Transport.IsConnected, "该连接的真实 Transport 已断开");
            CheckStrEq(server.Conn.DisconnectReason.ToString(), "LifecycleBatchOversize",
                       "断开原因是生命周期批次超限（专用原因，不与分片/窗溢出混淆）");
            Check(server.HasWarning("生命周期批次"), "告警明确指向生命周期批次超限");

            // 成对摘除：不得留下「世界有对象、对端永远没有」却还挂着连接的状态
            CheckEq(server.World.ConnectionCount, 0, "断开后世界侧连接已摘除");
            CheckEq(server.Bridge.Replication.ConnectionCount, 0, "断开后复制侧连接已摘除");
            CheckEq(server.Bridge.ConnectionCount, 0, "断开后桥内连接已摘除");
            CheckEq(server.World.PendingEventCount(server.Conn), 0, "旧连接的待发表已随之丢弃");

            // 自愈路径存在：新连接进世界时，世界会为所有存活对象重新排队 Create
            PMTransportConnection retry = scene.AddConnection(server, "server-retry", 2,
                                                              PMSessionPeerRole.Client, 2, 2);
            string error;
            Check(retry.TryActivate(out error), "重连（新连接）激活成功（" + (error ?? "ok") + "）");
            CheckEq(server.World.PendingEventCount(retry), server.World.ObjectCount,
                    "新连接被重新排队了全部存活对象的 Create（重连可自愈，不是永久失同步）");
        }

        // =================================================================================
        //  L. 未激活期丢弃不得误 ACK ⇒ 就绪后可靠流最终补齐
        // =================================================================================

        /// <summary>
        /// 契约 §7.2 的后果链（R3-B 复核新增）：
        ///
        /// 「未认证数据报必须在进入 Transport 前拒绝以免先 ACK 后丢业务」——要验证的正是后半句：
        /// 未激活期的可靠消息（Create）在接收侧被闸门丢掉时，
        /// **绝不能把 ack 回给发送方**；否则发送方会退休该可靠消息（无 NAK、无重传），
        /// 一条可靠消息就静默消失。没被 ACK ⇒ 发送方仍视为“在途” ⇒ 一旦有真实流量把确认水位推过它，
        /// 发送端就能从“已确认到 X、而这条只进过更早的包”推断丢包并重传（M03 既有机制）。
        ///
        /// 诚实边界（本轮实测确认，不修）：M03 的可靠重传**只由 ack 水位/NAK 驱动，没有定时器**。
        /// 因此「接收方就绪后发送方完全静默、双方再无任何流量」时，该消息要等到下一次真实流量
        /// （实战中 DS 每帧都有复制/生命周期流量）或传输层空闲超时重连才会补齐。
        /// 加定时重传等于新增传输层行为/协议，不在本轮边界内。
        /// </summary>
        private static void TestPreActivationReliableRecovery()
        {
            Scenario scene = new Scenario();
            Endpoint server = scene.Add("server", true, 15001u, 1, PMSessionPeerRole.Client, 900, 900,
                                        TestProtocolHash, TestProtocolHash);
            Endpoint client = scene.Add("client", false, 15001u, 1, PMSessionPeerRole.Server, 7, 7,
                                        TestProtocolHash, TestProtocolHash);
            scene.Connect(server.Link, client.Link);
            scene.Connect(client.Link, server.Link);

            string error;
            Check(server.Conn.TryActivate(out error), "服务端连接激活成功（" + (error ?? "ok") + "）");
            Check(!client.Conn.IsReady && !client.Conn.IsActivated,
                  "客户端**尚未激活**（模拟 ServerHello 丢失 / 未及处理）");

            SessionActor actor = new SessionActor();
            actor.Health = 42;
            actor.Armor = 2;
            Check(server.World.Spawn(actor, TestClassId), "服务端 Spawn 权威对象（Create 走可靠域）");
            actor.OwnerConnection = server.Conn;
            server.Bridge.RegisterReplicatedObject(actor);

            scene.Frame(10);

            // ── ① 未激活期：字节被闸门丢弃，且**绝不误 ACK** ──────────────
            Check(Find(client, actor.NetId) == null, "未激活期：创建消息没有到达客户端应用层");
            CheckEq(client.Conn.MessagesReceived, 0, "未激活期：客户端应用层没有收到任何消息");
            CheckGt(client.Conn.InboundDroppedNotActivated, 0, "未激活期：服务端发来的字节确实来过并被闸门丢弃");
            CheckEq(client.Conn.Transport.Stats.DatagramsReceived, 0,
                    "未激活期：字节根本没进 Transport（因此不可能 ack）");
            CheckEq(client.Conn.Transport.Stats.AcksSent, 0, "未激活期：没有发出过任何 ack");
            CheckGt(server.Conn.Transport.ReliableOutstanding, 0,
                    "未激活期：服务端仍持有未确认的可靠消息（没被静默退休）");
            CheckEq(server.Conn.Transport.Stats.ReliableResent, 0,
                    "未激活期：服务端从未收到过任何 ack/NAK，因此不会也无法重传（前置事实）");

            // ── ② 就绪后：真实流量把确认水位推过丢包点 ⇒ 发送端推断丢包并重传 ──
            Check(client.Conn.TryActivate(out error), "随后激活客户端连接（" + (error ?? "ok") + "）");
            scene.Frame(4);

            // 就绪后让权威侧再发一条新对象（实战中每帧都有生命周期/复制流量）：
            // 客户端收到它就能回 ack，该 ack 的水位越过包 1 ⇒ 服务端推断包 1 丢失并重传 Create。
            SessionActor late = new SessionActor();
            late.Health = 7;
            Check(server.World.Spawn(late, TestClassId), "就绪后 Spawn 第二个权威对象（产生新的服务端流量）");
            late.OwnerConnection = server.Conn;
            server.Bridge.RegisterReplicatedObject(late);

            scene.Frame(80);

            Check(Find(client, actor.NetId) != null,
                  "就绪后可靠流最终补齐（未激活期被丢的 Create 自动恢复）");
            CheckGt(server.Conn.Transport.Stats.ReliableResent, 0,
                    "发生可靠重传（发送端从确认水位推断出丢包，不是静默丢失）");
            CheckEq(server.Conn.Transport.ReliableOutstanding, 0, "可靠消息被真实确认后退休（无残留）");

            SessionActor replica = Find(client, actor.NetId) as SessionActor;
            if (replica != null)
            {
                CheckEq(replica.Health, 42, "补齐的 Create 带着声明式初值（首帧状态原子到达）");
            }
        }

        // =================================================================================
        //  M. 静默期活性（真实桥驱动）
        // =================================================================================

        /// <summary>
        /// 本轮新增：把「周期 keepalive」放进**真实生产路径**（<c>PMTransportConnection</c> →
        /// <c>PMNetSessionBridge.Update</c> 的帧内两次驱动）验证，而不是只在传输层单测里自证。
        ///
        /// 三个必须同时成立的结论：
        ///   ① 两端无业务超过 10s，连接不被自己的空闲看门狗断开；
        ///   ② 静默期首个可靠数据报丢失后，**再无任何业务**也能最终补齐且恰好一次；
        ///   ③ 核心侧的长度守卫让一条畸形数据报不再走「解析抛异常 → 会话层兜底断连」那条路。
        ///
        /// 口径对照（M2）是关键：关闭 keepalive 后同样的静默区间会超时断连，
        /// 证明上面的「保持」确实由 keepalive 承担，而不是新链路本来就不超时。
        /// </summary>
        private static void TestSilentLiveness()
        {
            // ── M1 两端静默 >10s（真实桥 + 真实 Transport）──────────────
            Scenario quiet = new Scenario();
            quiet.TuneTransportConfig = delegate (PMTransportConfig c)
            {
                c.IdleTimeoutMs = 10000L;
                c.KeepAliveIntervalMs = 1000L;
            };

            Endpoint qServer;
            Endpoint qClient;
            MakeActivatedPair(quiet, 16001u, out qServer, out qClient);

            quiet.Frame(760);   // 12.16s 全程零业务（无对象、无 RPC、无复制 Update）

            Check(qServer.Conn.IsReady && qClient.Conn.IsReady,
                  "M1 静默 12.16s 后两端 IsReady 仍为真（周期 keepalive 抵消空闲看门狗）");
            CheckGt(qServer.Conn.Transport.Stats.KeepAlivesSent + qClient.Conn.Transport.Stats.KeepAlivesSent,
                    0, "M1 真实桥路径上确实产生了周期 keepalive");
            Check(qServer.Conn.Transport.Stats.KeepAlivesSent <= 14L,
                  "M1 服务端 keepalive 数量有界（12.16s 内每 1000ms 至多一个，实际 "
                  + qServer.Conn.Transport.Stats.KeepAlivesSent + "）");
            CheckGt(qClient.Conn.Transport.Stats.KeepAlivesReceived, 0,
                    "M1 客户端收到并记账了对端 keepalive");

            // 每帧发送量有界：不得因 keepalive 放大（预算 32/帧，观察值应为个位数）
            long frameTxBefore = qServer.Conn.Transport.Stats.DatagramsSent;
            quiet.Frame(50);
            long frameTx = qServer.Conn.Transport.Stats.DatagramsSent - frameTxBefore;
            Check(frameTx <= 50L * qServer.Bridge.FrameDatagramBudget,
                  "M1 服务端每帧发送量受预算约束（50 帧共 " + frameTx + " 个数据报，预算 "
                  + qServer.Bridge.FrameDatagramBudget + "/帧）");

            // ── M2 对照：关闭 keepalive，同样静默必被看门狗断开 ──
            Scenario dead = new Scenario();
            dead.TuneTransportConfig = delegate (PMTransportConfig c)
            {
                c.IdleTimeoutMs = 10000L;
                c.KeepAliveIntervalMs = 0L;
            };

            Endpoint dServer;
            Endpoint dClient;
            MakeActivatedPair(dead, 16002u, out dServer, out dClient);

            dead.Frame(624);    // 9.98s
            Check(dServer.Conn.IsReady && dClient.Conn.IsReady,
                  "M2 对照：keepalive 关闭时 10s 之前连接仍就绪（看门狗没提前开火）");
            dead.Frame(20);     // 累计 10.3s
            Check(!dServer.Conn.Transport.IsConnected && !dClient.Conn.Transport.IsConnected,
                  "M2 对照：纯静默超过 10s 后被空闲看门狗断开（keepalive 是唯一活性来源）");
            CheckStrEq(dServer.Conn.DisconnectReason.ToString(), "Timeout", "M2 服务端断开原因 = Timeout");
            CheckEq(dServer.Conn.Transport.Stats.DatagramsSent, 0,
                    "M2 对照：全程零发送（没有任何业务，也没有 keepalive）");

            // ── M3 静默期首个可靠数据报丢失 ⇒ keepalive 推动补齐，恰好一次 ──
            Scenario heal = new Scenario();
            heal.TuneTransportConfig = delegate (PMTransportConfig c)
            {
                c.IdleTimeoutMs = 10000L;
                c.KeepAliveIntervalMs = 1000L;
            };

            Endpoint hServer;
            Endpoint hClient;
            MakeActivatedPair(heal, 16003u, out hServer, out hClient);

            SessionActor quietActor = new SessionActor();
            quietActor.Health = 42;
            quietActor.Armor = 3;
            Check(hServer.World.Spawn(quietActor, TestClassId), "M3 服务端 Spawn 权威对象（Create 走可靠域）");
            quietActor.OwnerConnection = hServer.Conn;
            hServer.Bridge.RegisterReplicatedObject(quietActor);

            // 丢掉服务端**第一个**数据报（承载 Create）；此后两端都没有任何业务
            heal.Hub.DropNextFrom("server", 1);
            heal.Frame(100);    // 1.6s：足够让 keepalive 到期并把缺口暴露给客户端

            Check(Find(hClient, quietActor.NetId) != null,
                  "M3 静默期首个可靠数据报丢失后仍最终交付（keepalive 推动 NAK）");
            CheckEq(hClient.Conn.LifecycleInbound, 1, "M3 生命周期恰好交付一次（不重复）");
            CheckGt(hServer.Conn.Transport.Stats.ReliableResent, 0,
                    "M3 服务端发生了可靠重传（不是静默丢失）");
            CheckEq(hServer.Conn.Transport.ReliableOutstanding, 0, "M3 可靠消息已确认退休（无残留）");
            Check(hServer.Conn.IsReady && hClient.Conn.IsReady, "M3 恢复期间两端保持就绪");

            SessionActor quietReplica = Find(hClient, quietActor.NetId) as SessionActor;
            if (quietReplica != null)
            {
                CheckEq(quietReplica.Health, 42, "M3 补齐的 Create 带着声明式初值");
            }

            // ── M4 19 字节畸形数据报：核心长度守卫，不再走「异常 → 会话层兜底」──
            Scenario malformed = new Scenario();
            malformed.TuneTransportConfig = delegate (PMTransportConfig c)
            {
                c.IdleTimeoutMs = 10000L;
                c.KeepAliveIntervalMs = 0L;
            };

            Endpoint mServer;
            Endpoint mClient;
            MakeActivatedPair(malformed, 16004u, out mServer, out mClient);

            byte[] bad = new byte[19];
            bad[0] = 1;
            bad[1] = (byte)(mClient.Epoch & 0xFF);
            bad[2] = (byte)((mClient.Epoch >> 8) & 0xFF);
            bad[3] = (byte)((mClient.Epoch >> 16) & 0xFF);
            bad[4] = (byte)((mClient.Epoch >> 24) & 0xFF);
            bad[5] = 0xF4;   // packetId = 500（低字节）
            bad[6] = 0x01;
            bad[17] = 1;     // messageCount = 1
            bad[18] = 0;     // flags（读完即断：没有 stream 字节）

            long faultsBefore = mClient.Conn.TransportUpdateFaults;
            long parseBefore = mClient.Conn.Transport.Stats.ParseErrors;
            bool threw = false;
            try
            {
                mClient.Conn.OnDatagram(bad, 0, bad.Length);
                malformed.Frame(3);
            }
            catch (Exception)
            {
                threw = true;
            }

            Check(!threw, "M4 19 字节畸形数据报不得让 Transport.Update（进而整个帧）抛出");
            CheckGt(mClient.Conn.Transport.Stats.ParseErrors, parseBefore,
                    "M4 畸形数据报被核心计为解析错误");
            CheckEq(mClient.Conn.TransportUpdateFaults, faultsBefore,
                    "M4 不再是「解析异常 → 会话层兜底」：核心自己就不抛");
            CheckEq(mClient.Conn.ProtocolErrors, 0, "M4 畸形数据报不触发应用层协议错误");
            Check(mClient.Conn.IsReady, "M4 连接保持就绪（本机噪声不废掉健康连接）");
        }
    }
}
