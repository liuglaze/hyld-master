// ============================================================================
//  R4-B / B3 集成门禁：真实认证 + 回环 UDP + 两客户端 AP/SP + 运动 Driver
// ============================================================================
//
//  契约依据：Docs/plans/net-r4-network-contract.md 的 §B3（主侧集成）；
//            状态与验收口径以 Docs/plans/net-architecture-migration.md 的 P4B4 行为准；
//            冻结 API 见 _r4b_network_report.md（B1）与 _r4b_unity_report.md（B2）。
//
//  为什么需要这道门禁：
//    B1 用手工 link（内存投递）证明了 Driver 本身；B2 证明了 Unity 适配器能在真实 Unity
//    程序集上编译。两者都没有证明「真 UDP 上两个客户端各自 AP、互相看到 SP、跳跃事件经可靠
//    通道到达、权威差异能收敛、重同步能升流并挡住旧流、断线能冻结并释放」。这些只能把真实
//    组件接起来、在真实回环 UDP 上跑才能证明。
//
//  本门禁用的是**真实实现**（不替本项目任何类型）：
//    · 真实 PMNet 核心（Transport / Session / World / Replication / Generated / Control）；
//    · 真实认证材料：PMDsMatchKey / PMDsTicketIssuer / PMDsBootstrapDocument / PMDsEntryCodec；
//      DS 侧逐身份验票走真实 PMHandshakeServer（与真实 DS 同一条代码路径）；
//    · 真实 PMUdpSessionEndpoint.OpenServer / OpenClient（真实 UDP socket、真实握手）；
//    · 真实 PMR3 声明对象与生成桩（ServerMovementInputV1 / ClientMovementEventsV1 …）；
//    · 真实 PMR4MovementCodec / PMR4MovementDriver（上游输入批与旧流快照都用真实 codec 编码）；
//    · 可控确定性碰撞环境 PMMoverTestWorld（**同一套**给 DS 与两个客户端）。
//
//  与真实 Unity 宿主的关系（诚实口径）：
//    · 真实宿主（PMDsSessionHost / PMClientSessionHost）用 Unity PhysX 适配器
//      （PMUnityMoverCollisionQuery）与 Physics.SyncTransforms；本机无 Unity，net8 里不可能
//      运行 PhysX。因此这里把碰撞查询换成**同一套确定性 AABB 替身**（PMMoverTestWorld）：
//      两侧用它时 WorldVersion 仍然相等，所以驱动的一致性校验照样生效，
//      “快照碰撞版本不符即拒绝并请求重同步”这条也被真实走到。
//    · 真实宿主的接触点（固定场景 + allowlist 白名单 / Query 构造 / 墙外出生点规则 /
//      每帧 Physics.SyncTransforms 一次 / endpoint.Pump 之后逐实例 Pump / 断线先 Freeze 再 Dispose）
//      由本门禁的测试宿主按同一次序**复刻**（见 DsSide / ClientSide 的注释）。
//    · 因此：**这不是** PhysX 运行验证，也**不是**真实 DS 进程验证。真实 Unity 物理与真实
//      进程的最终验收是 P4B6（PENDING_USER）。
//
//  真实运行：dotnet Tools/PMR4IntegrationTest/bin/Release/net8.0/PMR4IntegrationTest.dll
//  退出码：0 = 全部通过；1 = 存在失败
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using PMNet;
using PMNet.Control;
using PMNet.Mover;
using PMNet.Prediction;
using PMNet.R3;
using PMNet.Session;

namespace PMR4IntegrationTest
{
    internal static class Program
    {
        private static int _passed;
        private static readonly List<string> _failures = new List<string>();

        // ---------------------------------------------------------------- 冻结布局常量

        private const string MatchId = "r4b-integration";
        private const string DsId = "ds-r4b-integration";
        private const uint Epoch = 0x4C01u;
        private const int UidA = 11;
        private const int UidB = 12;

        /// <summary>出生点横向偏移（米）。墙盒 x∈[-0.5,0.5]，±3 保证在墙外。</summary>
        private const float SpawnLateralOffsetMeters = 3f;

        /// <summary>名册行间距（米）。</summary>
        private const float RosterRowSpacingMeters = 1.5f;

        /// <summary>offer 编解码往返用的占位端口（DS 端点尚未打开时的合法值）。</summary>
        private const int OfferCodecProbePort = 7777;

        private static DsSide _ds;
        private static ClientSide _clientA;
        private static ClientSide _clientB;

        private static readonly Dictionary<PMNetWorld, ClientSide> ClientsByWorld =
            new Dictionary<PMNetWorld, ClientSide>();

        private static long _nowMs = 10000L;
        private static long _nowUnixSeconds;

        private static int Main()
        {
            Console.WriteLine("=== R4-B / B3：真实认证 + 回环 UDP + 两客户端 AP/SP + 运动 Driver ===");
            Console.WriteLine();
            Console.WriteLine("口径：真实 UDP / 真实票据与握手 / 真实生成桩与复制；");
            Console.WriteLine("      碰撞环境是确定性 AABB 替身（PMMoverTestWorld）——**不是 Unity PhysX**。");
            Console.WriteLine();

            _nowUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            PMR3Runtime.Warn = delegate(string m) { Console.WriteLine("      [PMR3Runtime] " + m); };
            PMR3Runtime.PlayerReplicated += OnPlayerReplicatedGlobal;

            int exitCode = 1;
            try
            {
                PMR3Runtime.Register();

                Section("A. 真实认证材料与入局（offer/ticket/codec/真实握手）", TestAuthAndEntry);
                Section("B. 副本与角色：两客户端各 AP + 对方 SP 都有 Driver", TestRolesAndRigs);
                Section("C. 移动收敛与 SP 插值（真实 UDP 往返）", TestMovementConvergence);
                Section("D. 跳跃与权威事件（可靠通道、独立于快照游标）", TestJumpEvents);
                Section("E. 权威差异：强制差异 → 回滚重放 → 再收敛", TestAuthorityDivergence);
                Section("F. 重同步：升流重绑 / 旧流输入被拒 / 旧流快照不回写", TestResyncAndOldStream);
                Section("G. 断线冻结与资源释放（宿主接线语义）", TestDisconnectAndCleanup);

                exitCode = _failures.Count == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("  未捕获异常：" + ex.GetType().Name + " " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                exitCode = 1;
            }
            finally
            {
                TeardownAll();
            }

            Console.WriteLine();
            if (_failures.Count == 0)
            {
                Console.WriteLine("  全部通过：" + _passed + " 项检查，0 项失败");
            }
            else
            {
                Console.WriteLine("  失败 " + _failures.Count + " 项 / 通过 " + _passed + " 项：");
                for (int i = 0; i < _failures.Count; i++)
                {
                    Console.WriteLine("    - " + _failures[i]);
                }
            }

            return exitCode;
        }

        // =================================================================================
        //  断言语义（与其它门禁同一风格：计数 + 失败明细 + 退出码）
        // =================================================================================

        private static void Section(string name, Action body)
        {
            Console.WriteLine("── " + name);
            int before = _failures.Count;
            try
            {
                body();
            }
            catch (Exception ex)
            {
                Fail("小节抛出异常：" + ex.GetType().Name + " " + ex.Message);
            }

            Console.WriteLine("   [" + name.Substring(0, 1) + "] "
                              + (_failures.Count == before ? "通过" : "失败 " + (_failures.Count - before) + " 项"));
            Console.WriteLine();
        }

        private static void Check(bool ok, string label)
        {
            if (ok)
            {
                _passed++;
            }
            else
            {
                Fail(label);
            }
        }

        private static void CheckTrue(bool value, string label) { Check(value, label); }

        private static void CheckEq(long actual, long expected, string label)
        {
            Check(actual == expected, label + "（实际 " + actual.ToString(CultureInfo.InvariantCulture)
                                      + "，期望 " + expected.ToString(CultureInfo.InvariantCulture) + "）");
        }

        private static void CheckEq(uint actual, uint expected, string label)
        {
            Check(actual == expected, label + "（实际 " + actual.ToString(CultureInfo.InvariantCulture)
                                      + "，期望 " + expected.ToString(CultureInfo.InvariantCulture) + "）");
        }

        private static void CheckEq(int actual, int expected, string label)
        {
            Check(actual == expected, label + "（实际 " + actual.ToString(CultureInfo.InvariantCulture)
                                      + "，期望 " + expected.ToString(CultureInfo.InvariantCulture) + "）");
        }

        private static void CheckStr(string actual, string expected, string label)
        {
            Check(string.Equals(actual, expected, StringComparison.Ordinal),
                label + "（实际 '" + (actual ?? "<null>") + "'，期望 '" + (expected ?? "<null>") + "'）");
        }

        private static void CheckNear(float actual, float expected, float tolerance, string label)
        {
            Check(Math.Abs(actual - expected) <= tolerance,
                label + "（实际 " + Num(actual) + "，期望 " + Num(expected) + "±" + Num(tolerance) + "）");
        }

        private static void CheckVec(PMVector3 actual, PMVector3 expected, float tolerance, string label)
        {
            Check(Math.Abs(actual.X - expected.X) <= tolerance
                  && Math.Abs(actual.Y - expected.Y) <= tolerance
                  && Math.Abs(actual.Z - expected.Z) <= tolerance,
                label + "（实际 " + actual + "，期望 " + expected + "±" + Num(tolerance) + "）");
        }

        private static string Num(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static void Fail(string label)
        {
            _failures.Add(label);
            Console.WriteLine("      FAIL " + label);
        }

        // =================================================================================
        //  A. 真实认证材料与入局
        // =================================================================================

        private static void TestAuthAndEntry()
        {
            // A1：真实签发材料。DS 与 offer 用**同一把局密钥**与**同一批票据**，
            //     因此 DS 的逐身份验票是真实验证，不是走过场。
            PMDsMatchKey key = PMDsMatchKey.Create();
            CheckTrue(key != null && key.KeyFingerprint != null, "A1 局控制密钥已创建（PMDsMatchKey.Create）");

            PMDsRosterIdentity identityA = new PMDsRosterIdentity(UidA, 1, 1, 1);
            PMDsRosterIdentity identityB = new PMDsRosterIdentity(UidB, 2, 2, 2);

            PMDsTicketIssuer issuer = key.CreateTicketIssuer(MatchId, DsId, Epoch, PMR3Runtime.ProtocolHash);
            PMDsTicket ticketA = issuer.Issue(identityA, _nowUnixSeconds);
            PMDsTicket ticketB = issuer.Issue(identityB, _nowUnixSeconds);

            CheckTrue(ticketA != null && ticketA.ByteLength > 0, "A2 uid=11 票据已签发（真实 MAC）");
            CheckTrue(ticketB != null && ticketB.ByteLength > 0, "A3 uid=12 票据已签发（真实 MAC）");
            CheckTrue(ticketA != null && !ticketA.IsExpiredAt(_nowUnixSeconds), "A4 票据未过期");

            // A5：真实引导文件（DS 侧唯一可信名册来源）。
            PMDsBootstrapPlayer[] roster = new PMDsBootstrapPlayer[2];
            roster[0] = new PMDsBootstrapPlayer(identityA, ticketA.ExportTicketBytes());
            roster[1] = new PMDsBootstrapPlayer(identityB, ticketB.ExportTicketBytes());

            PMDsBootstrapBody body = new PMDsBootstrapBody();
            body.CollisionDigest = PMR3Runtime.CollisionDigest;
            body.Players = roster;

            PMDsControlMessage message = PMDsControlMessage.Create(
                PMDsControlMessageType.Bootstrap, MatchId, DsId, Epoch, PMR3Runtime.ProtocolHash, 0UL, body);

            byte[] document = PMDsBootstrapDocument.Encode(key, message);
            CheckTrue(document != null && document.Length > 0, "A5 引导文件已编码（PMDsBootstrapDocument.Encode）");

            PMDsBootstrappedMatch boot;
            string decodeError;
            CheckTrue(PMDsBootstrapDocument.TryDecode(document, out boot, out decodeError),
                "A6 引导文件可被真实解码（" + (decodeError ?? "ok") + "）");
            if (boot == null) { return; }

            CheckStr(boot.MatchId, MatchId, "A7 引导文件 MatchId 一致");
            CheckStr(boot.DsId, DsId, "A8 引导文件 DsId 一致");
            CheckEq(boot.Epoch, Epoch, "A9 引导文件 Epoch 一致");
            CheckEq(boot.ProtocolHash, PMR3Runtime.ProtocolHash, "A10 引导文件协议摘要 == 本程序集摘要");
            CheckEq(boot.CollisionDigest, PMR3Runtime.CollisionDigest, "A11 引导文件碰撞摘要一致");
            CheckEq(boot.PlayerCount, 2, "A12 名册人数 == 2");

            // A13：真实 offer 编解码往返（PMDS1: 文本）。
            //      注意：这里用占位端口（编解码本身要求 1..65535，且 DS 端点还没开）；
            //      真正的端口在 A19 用 OpenServer 实际绑定的 BoundPort 重建。
            PMDsEntryOffer offerA = BuildOffer(boot, identityA, ticketA, OfferCodecProbePort);
            string text = PMDsEntryCodec.Encode(offerA);
            CheckTrue(text != null && text.StartsWith(PMDsEntryCodec.Prefix, StringComparison.Ordinal),
                "A13 offer 编码前缀 = '" + PMDsEntryCodec.Prefix + "'");

            PMDsEntryOffer parsed;
            string parseError;
            CheckTrue(PMDsEntryCodec.TryDecode(text, out parsed, out parseError),
                "A14 offer 可被真实解码（" + (parseError ?? "ok") + "）");
            CheckTrue(parsed != null && parsed.Ticket != null
                      && parsed.Ticket.Length == ticketA.ExportTicketBytes().Length,
                "A15 offer 携带票据字节（长度一致）");

            // A16：真实 DS 端点（真实 UDP 绑定，端口由系统分配）。
            _ds = DsSide.Start(boot);
            CheckTrue(_ds != null, "A16 DS 端点已打开（PMUdpSessionEndpoint.OpenServer）");
            if (_ds == null) { return; }

            CheckTrue(_ds.Endpoint.BoundPort > 0, "A17 DS 实际绑定端口有效：" + _ds.Endpoint.BoundPort);
            CheckTrue(_ds.Endpoint.IsServerSide, "A18 端点为服务端侧");

            // A19：两个客户端（真实 OpenClient + 真实票据关联校验）。
            offerA = BuildOffer(boot, identityA, ticketA, _ds.Endpoint.BoundPort);
            PMDsEntryOffer offerB = BuildOffer(boot, identityB, ticketB, _ds.Endpoint.BoundPort);

            _clientA = ClientSide.Enter(offerA);
            _clientB = ClientSide.Enter(offerB);
            CheckTrue(_clientA != null && _clientB != null, "A19 两个客户端端点已打开（真实 OpenClient）");
            if (_clientA == null || _clientB == null) { return; }

            // A20：真实握手（hello/accept）在真实 UDP 上完成。
            int frames = 0;
            while (frames < 200
                   && (_ds.Endpoint.ConnectionCount < 2
                       || _clientA.Endpoint.ClientConnection == null
                       || _clientB.Endpoint.ClientConnection == null))
            {
                Frames(1);
                frames++;
            }

            CheckEq(_ds.Endpoint.ConnectionCount, 2, "A20 DS 侧两条连接均就绪（真实握手 + 逐身份验票）");
            CheckEq(_ds.Endpoint.HandshakeServer.Accepted, 2L, "A21 握手服务器接受 2 次");
            CheckEq(_ds.Endpoint.HandshakeServer.VerifyFailures, 0L, "A22 验票失败 0 次（票据由真实 key 签发）");
            CheckTrue(_clientA.Endpoint.ClientConnection != null && _clientA.Endpoint.ClientConnection.IsReady,
                "A23 客户端 A 连接就绪");
            CheckTrue(_clientB.Endpoint.ClientConnection != null && _clientB.Endpoint.ClientConnection.IsReady,
                "A24 客户端 B 连接就绪");
        }

        private static PMDsEntryOffer BuildOffer(PMDsBootstrappedMatch boot, PMDsRosterIdentity identity,
                                                 PMDsTicket ticket, int port)
        {
            PMDsEntryOffer offer = new PMDsEntryOffer();
            offer.MatchId = boot.MatchId;
            offer.DsId = boot.DsId;
            offer.Host = "127.0.0.1";
            offer.Epoch = boot.Epoch;
            offer.ProtocolHash = boot.ProtocolHash;
            offer.CollisionDigest = boot.CollisionDigest;
            offer.Port = port;
            offer.Identity = identity;
            offer.Ticket = ticket.ExportTicketBytes();
            return offer;
        }

        // =================================================================================
        //  B. 副本与角色
        // =================================================================================

        private static void TestRolesAndRigs()
        {
            if (_ds == null || _clientA == null || _clientB == null) { Fail("B 前置：链路未建立"); return; }

            Frames(3);

            CheckEq(_ds.PlayersByUid.Count, 2, "B1 DS 侧两个权威副本已创建（Connected → SpawnPlayer）");
            CheckEq(_ds.DriversByUid.Count, 2, "B2 DS 侧两个权威运动 Driver 已建立");
            CheckEq(_ds.InitialSnapshotPublishedCount, 2, "B3 两个 Driver 都发布了初始快照（首次 Flush 之前）");

            CheckEq(_clientA.Rigs.Count, 2, "B4 客户端 A 复制到 2 个副本");
            CheckEq(_clientB.Rigs.Count, 2, "B5 客户端 B 复制到 2 个副本");

            CheckEq(_clientA.CountRole(PMNetRole.AutonomousProxy), 1, "B6 客户端 A 恰好 1 个 AutonomousProxy");
            CheckEq(_clientA.CountRole(PMNetRole.SimulatedProxy), 1, "B7 客户端 A 恰好 1 个 SimulatedProxy");
            CheckEq(_clientB.CountRole(PMNetRole.AutonomousProxy), 1, "B8 客户端 B 恰好 1 个 AutonomousProxy");
            CheckEq(_clientB.CountRole(PMNetRole.SimulatedProxy), 1, "B9 客户端 B 恰好 1 个 SimulatedProxy");

            // B10/B11：**旧实现会在这里失败** —— 它 `if (Role != AutonomousProxy) return;`，
            // 于是 SP 根本没有 Driver，别人的角色在本地永远不动。
            CheckEq(_clientA.Rigs.Count, 2, "B10 客户端 A 为全部副本（AP 与 SP）都建了 Driver");
            CheckEq(_clientB.Rigs.Count, 2, "B11 客户端 B 为全部副本（AP 与 SP）都建了 Driver");
            CheckTrue(AllRigsHaveDrivers(_clientA), "B12 客户端 A 的每个副本都有非空 Driver");
            CheckTrue(AllRigsHaveDrivers(_clientB), "B13 客户端 B 的每个副本都有非空 Driver");

            CheckTrue(_clientA.ApDriver != null && _clientA.ApPlayer != null
                      && _clientA.ApPlayer.Uid == UidA, "B14 客户端 A 的 AP 是 uid=11");
            CheckTrue(_clientB.ApDriver != null && _clientB.ApPlayer != null
                      && _clientB.ApPlayer.Uid == UidB, "B15 客户端 B 的 AP 是 uid=12");

            // B16：SP 没有预测时间轴（结构上不可能预测）。
            PMR4MovementDriver spA = _clientA.FindDriverByRole(PMNetRole.SimulatedProxy);
            CheckTrue(spA != null && spA.Timeline == null && spA.Interpolation != null,
                "B16 SP 只有插值缓冲、没有预测时间轴");

            // B17：出生点必须在墙外（默认原点 (0,1,0) 在测试墙盒内部）。
            PMMoverSyncState dsA = _ds.DriversByUid[UidA].GetAuthoritativeSync();
            PMMoverSyncState dsB = _ds.DriversByUid[UidB].GetAuthoritativeSync();

            CheckNear(Math.Abs(dsA.Position.X), SpawnLateralOffsetMeters, 1e-3f,
                "B17 uid=11 出生 |x| = " + Num(SpawnLateralOffsetMeters));
            CheckNear(dsA.Position.Y, PMMoverDefaults.CapsuleHalfHeightMeters, 1e-3f, "B18 uid=11 出生 y = 半高（站在地板上）");
            CheckNear(dsA.Position.Z, -(RosterRowSpacingMeters * 0.5f), 1e-3f, "B19 uid=11 出生 z = 名册行居中");
            CheckNear(Math.Abs(dsB.Position.X), SpawnLateralOffsetMeters, 1e-3f,
                "B20 uid=12 出生 |x| = " + Num(SpawnLateralOffsetMeters));
            CheckTrue(Math.Abs(dsA.Position.X - dsB.Position.X) > 1f, "B21 两人出生在墙的两侧（不重叠）");
            CheckTrue(Math.Abs(dsA.Position.Z - dsB.Position.Z) > 1f, "B22 两人出生行不同（不重叠）");
            CheckTrue(Math.Abs(dsA.Position.X) > 0.5f + PMMoverDefaults.CapsuleRadiusMeters
                      && Math.Abs(dsB.Position.X) > 0.5f + PMMoverDefaults.CapsuleRadiusMeters,
                "B23 两人都在墙的碰撞范围之外（|x| > 半墙宽 + 胶囊半径）");

            CheckEq(_clientA.ApDriver.Epoch, Epoch, "B24 AP epoch 绑定会话");
            CheckEq(_clientA.ApDriver.StreamVersion, 1u, "B25 初始 streamVersion = 1");
            CheckEq((long)_clientA.ApDriver.OutputBoundary.Value, 0L, "B26 AP 初始输出边界 = 0");
            CheckTrue(_clientA.ApPlayer.MovementSnapshotPayload != null
                      && _clientA.ApPlayer.MovementSnapshotPayload.Length > 0,
                "B27 Create 记录已带上运动初值快照（与 Create 原子到达）");
            CheckTrue(_ds.Query.WorldVersion > 0, "B28 确定性碰撞世界版本可读（" + _ds.Query.WorldVersion + "）");
        }

        private static bool AllRigsHaveDrivers(ClientSide side)
        {
            for (int i = 0; i < side.Rigs.Count; i++)
            {
                if (side.Rigs[i].Driver == null) { return false; }
            }

            return true;
        }

        // =================================================================================
        //  C. 移动收敛 + SP 插值
        // =================================================================================

        private static void TestMovementConvergence()
        {
            if (_ds == null || _clientA == null || _clientB == null) { Fail("C 前置：链路未建立"); return; }

            const int steps = 24;
            for (int i = 0; i < steps; i++)
            {
                _clientA.StepAp(16, Move(0f, 1f, 0f, false));
                _clientA.SendApInput();

                _clientB.StepAp(16, Move(0f, -1f, 0f, false));
                _clientB.SendApInput();

                Frames(1);
            }

            Frames(6);   // 把最后一包推到位（真实链路，不绕过）

            // 诊断证据：DS/AP 的内部进度必须可见（否则“为何只走了一小段”只能猜）。
            Console.WriteLine("      [diag] DS uidA : " + _ds.DriversByUid[UidA].Describe());
            Console.WriteLine("      [diag] AP(A)   : " + _clientA.ApDriver.Describe());
            {
                PMMoverSyncState d = _ds.DriversByUid[UidA].GetAuthoritativeSync();
                Console.WriteLine("      [diag] DS sync : pos=" + d.Position + " vel=" + d.Velocity
                                  + " pre=" + d.PreAdditiveVelocity + " mode=" + d.Mode
                                  + " maxSpeed=" + Num(d.MaxSpeed) + " accel=" + Num(d.Acceleration));
            }

            CheckEq(_ds.DriversByUid[UidA].AuthorityNextInputFrame, steps, "C1 DS 消费了客户端 A 的全部 " + steps + " 条输入");
            CheckEq(_ds.DriversByUid[UidB].AuthorityNextInputFrame, steps, "C2 DS 消费了客户端 B 的全部 " + steps + " 条输入");
            CheckEq((long)_ds.DriversByUid[UidA].OutputBoundary.Value, steps, "C3 DS 权威输出边界 == " + steps);

            CheckEq((long)_clientA.ApDriver.OutputBoundary.Value, steps, "C4 AP 输出边界跟进 == " + steps);
            CheckEq((long)_clientA.ApDriver.ConfirmedBoundary.Value, steps, "C5 AP 已确认边界 == " + steps);
            CheckTrue(_clientA.ApDriver.SnapshotPayloadsApplied > 0, "C6 AP 应用过快照（复制通道真的在用）");

            PMMoverSyncState spawnA = _ds.BuildSpawnSync(UidA);
            PMMoverSyncState spawnB = _ds.BuildSpawnSync(UidB);

            PMMoverSyncState authA = _ds.DriversByUid[UidA].GetAuthoritativeSync();
            PMMoverSyncState predA = _clientA.ApDriver.GetPredictedSync();
            CheckVec(predA.Position, authA.Position, 0.05f, "C7 AP 预测与 DS 权威位置收敛（同模型同输入）");
            // 注意：断言用**相对出生点的位移**，不用绝对 Z —— 出生点本身是墙外的 ±3/居中行，
            CheckNear(authA.Position.Z - spawnA.Position.Z, 1.148f, 0.15f,
                "C8 DS 权威位置按输入方向推进（Δz，实际 " + Num(authA.Position.Z - spawnA.Position.Z) + "）");
            CheckTrue(authA.Velocity.Length > 3f, "C9 DS 权威速度已达上限附近（|v|=" + Num(authA.Velocity.Length) + "）");

            PMMoverSyncState authB = _ds.DriversByUid[UidB].GetAuthoritativeSync();
            CheckNear(spawnB.Position.Z - authB.Position.Z, 1.148f, 0.15f,
                "C10 客户端 B 的权威位置向相反方向推进（Δz，实际 " + Num(spawnB.Position.Z - authB.Position.Z) + "）");

            // C11：客户端 A 上由**复制**得到的 SP（就是 B 的角色）也要收敛。
            PMR4MovementDriver spB = _clientA.FindDriverByRole(PMNetRole.SimulatedProxy);
            CheckTrue(spB != null, "C11 客户端 A 上的 SP Driver 存在");
            if (spB == null) { return; }

            CheckTrue(spB.Interpolation.AuthorityAccepted > 0, "C12 SP 插值缓冲接受了权威快照（真实复制）");
            PMInterpolatedState<PMMoverSyncState, PMMoverAuxState> sample = spB.SamplePresentation();
            CheckTrue(sample.HasValue, "C13 SP 可采样到权威状态");
            if (sample.HasValue)
            {
                CheckVec(sample.Sync.Position, authB.Position, 2.0f,
                    "C14 SP 采样位置接近 DS 权威（插值滞后有界）");
            }

            CheckEq(_clientA.ApDriver.Timeline.ConfirmedEventsEmitted, 0L,
                "C15 预测时间轴从不广播事件（唯一派发者是独立日志，避免双重派发）");
            CheckEq(_clientA.ApDriver.UnackedInputCount, 0, "C16 已确认输入全部从重发窗口退役");
        }

        // =================================================================================
        //  D. 跳跃与权威事件
        // =================================================================================

        private static void TestJumpEvents()
        {
            if (_ds == null || _clientA == null) { Fail("D 前置：链路未建立"); return; }

            long batchesBefore = _clientA.ApDriver.EventBatchesAccepted;
            int dispatchedBefore = _clientA.Events.Count;

            // D1：Walking 下按一次跳跃边沿 → DS 权威模型切 Falling → 落回地面。
            //     边沿只按一次，且只在 Tick 被接受后才算消费（对照契约 §B3 的“不先丢边沿”）。
            bool consumed = false;
            for (int i = 0; i < 110; i++)
            {
                bool jumpEdge = !consumed;
                PMTickRejectReason reject = _clientA.StepAp(16, Move(0f, 0.6f, 0f, jumpEdge));
                if (reject == PMTickRejectReason.None)
                {
                    consumed = true;
                    _clientA.SendApInput();
                }

                Frames(1);
            }

            Frames(6);

            CheckTrue(consumed, "D1 跳跃边沿在 Tick 被接受后被消费（未因冻结/超限丢失）");

            long batches = _clientA.ApDriver.EventBatchesAccepted;
            CheckTrue(batches > batchesBefore,
                "D2 AP 通过可靠通道收到权威事件批（" + batchesBefore + " → " + batches + "）");
            CheckEq(_clientA.ApDriver.EventDecodeFailed, 0L, "D3 事件载荷解码失败 0 次");
            CheckTrue(_clientA.Events.Count > dispatchedBefore,
                "D4 AP 真的派发了事件（" + dispatchedBefore + " → " + _clientA.Events.Count + " 条）");

            bool sawMode = false;
            bool sawLanded = false;
            for (int i = 0; i < _clientA.Events.Count; i++)
            {
                if (_clientA.Events[i].Kind == PMR4MovementDriver.EventKindModeChanged) { sawMode = true; }
                if (_clientA.Events[i].Kind == PMR4MovementDriver.EventKindLanded) { sawLanded = true; }
            }

            CheckTrue(sawMode, "D5 收到 Mode 变化事件（kind=" + PMR4MovementDriver.EventKindModeChanged + "）");
            CheckTrue(sawLanded, "D6 收到落地事件（kind=" + PMR4MovementDriver.EventKindLanded + "）");

            HashSet<ulong> keys = new HashSet<ulong>();
            bool duplicate = false;
            for (int i = 0; i < _clientA.Events.Count; i++)
            {
                if (!keys.Add(_clientA.Events[i].Key)) { duplicate = true; }
            }

            CheckTrue(!duplicate, "D7 同一事件 key 没有被重复派发");
            CheckEq(_clientA.ApDriver.Journal.SequenceGaps, 0L, "D8 事件序号无缺口");
            CheckTrue(!_clientA.ApDriver.Journal.Failed, "D9 事件日志未进入失败态");

            CheckTrue(_clientA.ApDriver.SnapshotPayloadsApplied > 0 && batches > 0,
                "D10 快照与事件两条链都在工作（事件独立于快照确认游标）");
        }

        // =================================================================================
        //  E. 权威差异：强制差异 → 回滚重放 → 再收敛
        // =================================================================================

        private static void TestAuthorityDivergence()
        {
            if (_ds == null || _clientA == null) { Fail("E 前置：链路未建立"); return; }

            PMR4MovementDriver dsDriver = _ds.DriversByUid[UidA];
            PMMoverSyncState before = dsDriver.GetAuthoritativeSync();
            long replaysBefore = _clientA.ApDriver.Timeline.ReplayedSteps;

            // E1：DS 在帧边界提交**可信** Teleport（上行不开放该权限；这是契约允许的
            //     “强制差异”通道，也正是本次要验的东西）。
            PMVector3 target = new PMVector3(before.Position.X, before.Position.Y, before.Position.Z + 4f);
            CheckTrue(dsDriver.ServerSubmitTrustedEffect(PMMoverEffectRequest.Teleport(target)),
                "E1 DS 接受可信 Teleport 效果（ServerSubmitTrustedEffect）");

            for (int i = 0; i < 12; i++)
            {
                _clientA.StepAp(16, Move(0f, 0f, 0f, false));
                _clientA.SendApInput();
                Frames(1);
            }

            Frames(6);

            PMMoverSyncState after = dsDriver.GetAuthoritativeSync();
            CheckTrue(Math.Abs(after.Position.Z - before.Position.Z) > 3f,
                "E2 DS 权威位置确实被可信效果改变了（Δz=" + Num(after.Position.Z - before.Position.Z) + "）");

            CheckTrue(_clientA.ApDriver.Timeline.ReplayedSteps > replaysBefore,
                "E3 AP 检测到权威差异并重放未确认输入（ReplayedSteps "
                + replaysBefore + " → " + _clientA.ApDriver.Timeline.ReplayedSteps + "）");

            PMMoverSyncState pred = _clientA.ApDriver.GetPredictedSync();
            CheckVec(pred.Position, after.Position, 0.05f, "E4 重放后 AP 预测与 DS 权威重新收敛");
        }

        // =================================================================================
        //  F. 重同步：升流重绑 / 旧流输入被拒 / 旧流快照不回写
        // =================================================================================

        private static void TestResyncAndOldStream()
        {
            if (_ds == null || _clientA == null) { Fail("F 前置：链路未建立"); return; }

            PMR4MovementDriver dsDriver = _ds.DriversByUid[UidA];
            PMR4MovementDriver apDriver = _clientA.ApDriver;

            uint streamBefore = dsDriver.StreamVersion;
            CheckEq(streamBefore, 1u, "F1 重同步前 DS 流代次 = 1");

            long servedBefore = dsDriver.ResyncServed;
            long appliedBefore = apDriver.ResyncApplied;
            long rejectedNewerBefore = apDriver.SnapshotRejectedStreamNewer;

            CheckTrue(dsDriver.BeginServerResync("integration-test"), "F2 DS 主动开启新流（BeginServerResync）");
            CheckEq(dsDriver.StreamVersion, streamBefore + 1u, "F3 DS 流代次已递增");

            for (int i = 0; i < 8; i++)
            {
                _clientA.StepAp(16, Move(0f, 0f, 0f, false));
                _clientA.SendApInput();
                Frames(1);
            }

            Frames(6);

            CheckTrue(dsDriver.ResyncServed > servedBefore, "F4 DS 记为服务过一次重同步");
            CheckTrue(apDriver.ResyncApplied > appliedBefore, "F5 AP 应用了显式重同步并整体重绑");
            CheckEq(apDriver.StreamVersion, dsDriver.StreamVersion, "F6 重绑后 AP 流代次 == DS 流代次");
            CheckEq(apDriver.Epoch, dsDriver.Epoch, "F7 重绑后 epoch 未变");

            // F8：未拿到重同步载荷之前，**新流**的普通快照会被拒绝（拒绝而不是回写）。
            CheckTrue(apDriver.SnapshotRejectedStreamNewer > rejectedNewerBefore,
                "F8 重绑前到达的新流普通快照被拒（SnapshotRejectedStreamNewer "
                + rejectedNewerBefore + " → " + apDriver.SnapshotRejectedStreamNewer + "）");

            // F9/F10：旧流**输入**被 DS 丢弃 —— 用真实 codec 编码 + 真实生成桩 + 真实 UDP 发送。
            PMR4MovementInputEntry[] entries = new PMR4MovementInputEntry[1];
            entries[0].InputFrame = apDriver.OutputBoundary.Value + 1L;
            entries[0].StepMs = 16;
            entries[0].Input = Move(0f, 0f, 0f, false);

            byte[] stalePayload = PMR4MovementCodec.EncodeInputs(
                Epoch, _clientA.ApPlayer.NetId.Value, streamBefore, entries);
            CheckTrue(stalePayload != null && stalePayload.Length > 0, "F9 旧流输入批已用真实 codec 编码");

            long rejectedStreamBefore = dsDriver.InputRejectedStream;
            _clientA.ApPlayer.ServerMovementInputV1(stalePayload);
            for (int i = 0; i < 6; i++) { Frames(1); }

            CheckTrue(dsDriver.InputRejectedStream > rejectedStreamBefore,
                "F10 DS 丢弃了旧流输入（InputRejectedStream "
                + rejectedStreamBefore + " → " + dsDriver.InputRejectedStream + "）");

            // F11/F12：旧流**快照**不回写。DS 侧用真实写入口（PublishMovementSnapshot，
            //          就是 Driver 自己用的那个）写一张 stream=旧流 的快照，经真实复制通道发出。
            //          先让 DS 把待处理输入吃完（不再有新输入 ⇒ 不会被新快照覆盖）。
            for (int i = 0; i < 10; i++) { Frames(1); }

            long rejectedIdentityBefore = apDriver.SnapshotRejectedIdentity;

            PMR4MovementSnapshotBlob staleBlob = new PMR4MovementSnapshotBlob();
            staleBlob.Epoch = Epoch;
            staleBlob.InstanceId = _ds.PlayersByUid[UidA].NetId.Value;
            staleBlob.StreamVersion = streamBefore;
            staleBlob.OutputFrame = dsDriver.OutputBoundary.Value;
            staleBlob.ServerFrame = 0L;
            staleBlob.TotalSimTimeMs = dsDriver.AuthorityTotalSimTimeMs;
            staleBlob.Sync = dsDriver.GetAuthoritativeSync();
            staleBlob.Aux = dsDriver.GetAuthoritativeAux();

            byte[] staleSnapshot = PMR4MovementCodec.EncodeSnapshot(false, staleBlob);
            CheckTrue(staleSnapshot != null && staleSnapshot.Length > 0, "F11 旧流快照已用真实 codec 编码");

            _ds.PlayersByUid[UidA].PublishMovementSnapshot(staleSnapshot);
            for (int i = 0; i < 8; i++) { Frames(1); }

            CheckTrue(apDriver.SnapshotRejectedIdentity > rejectedIdentityBefore,
                "F12 旧流快照被拒、状态未回写（SnapshotRejectedIdentity "
                + rejectedIdentityBefore + " → " + apDriver.SnapshotRejectedIdentity + "）");

            // F13/F14/F15：重绑后仍能继续推进并再次收敛（没卡死）。
            long ticksBefore = apDriver.Ticks;
            long boundaryBefore = apDriver.OutputBoundary.Value;

            for (int i = 0; i < 8; i++)
            {
                _clientA.StepAp(16, Move(0f, 1f, 0f, false));
                _clientA.SendApInput();
                Frames(1);
            }

            Frames(6);

            CheckTrue(apDriver.Ticks > ticksBefore, "F13 重绑后 AP 仍在推进（Ticks "
                      + ticksBefore + " → " + apDriver.Ticks + "）");
            CheckTrue(apDriver.OutputBoundary.Value > boundaryBefore, "F14 重绑后输出边界继续增长");

            PMMoverSyncState dsSync = dsDriver.GetAuthoritativeSync();
            PMMoverSyncState apSync = apDriver.GetPredictedSync();
            CheckVec(apSync.Position, dsSync.Position, 0.05f, "F15 重绑后 AP 与 DS 权威再次收敛");
        }

        // =================================================================================
        //  G. 断线冻结与资源释放
        // =================================================================================

        private static void TestDisconnectAndCleanup()
        {
            if (_ds == null || _clientB == null) { Fail("G 前置：链路未建立"); return; }

            CheckEq(_ds.DriversByUid.Count, 2, "G1 断线前 DS 持有 2 个 Driver");

            // G2：客户端 B 停止一切活动，然后**逐步**推进时钟（而不是一次跳 32 秒）。
            //     为什么不能一次跳：DS 的剪除发生在每次 Pump 的**收包之前**，
            //     一次跳到 32 秒会把两个会话都判成空闲（包括还在发前的一个）；
            //     逐步推进时 A 每帧都有入站（新鲜度紧随 nowMs），只有 B 会超过 30 秒空闲。
            //     这与真实 DS 的判据同源：端点按 IdleTimeout 剪除会话，连接 IsReady 变 false。
            _clientB.Silent = true;
            for (int i = 0; i < 40; i++)
            {
                _nowMs += 1000L;
                Frames(1);
            }

            CheckEq(_ds.Endpoint.ConnectionCount, 1, "G2 DS 侧只剩 1 条就绪连接（空闲超时剪除，真实机制）");

            // G3：宿主语义 —— 连接不再就绪的副本必须**先 Freeze 再 Dispose**。
            int pruned = _ds.PruneDisconnectedDrivers();

            CheckEq(pruned, 1, "G3 DS 剪除了 1 个断线玩家的运动 Driver");
            CheckEq(_ds.DriversByUid.Count, 1, "G4 DS 只保留存活玩家的 Driver");
            CheckTrue(_ds.FrozenUids.Contains(UidB), "G5 断线玩家的 Driver 先被 Freeze 再 Dispose");
            CheckTrue(_ds.PlayersByUid[UidB].MovementDriver == null, "G6 Dispose 后 player 上不再持有 Driver 引用");

            // G7/G8：客户端侧释放语义。
            PMR4MovementDriver leftover = _clientB.ApDriver;
            leftover.Freeze();
            CheckTrue(leftover.IsFrozen, "G7 客户端 AP 可被 Freeze（断线冻结）");
            leftover.Dispose();
            leftover.Dispose();
            CheckTrue(leftover.IsDisposed && _clientB.ApPlayer.MovementDriver == null,
                "G8 Driver Dispose 幂等且摘除 player 引用");

            // G9/G10：客户端 Stop 语义 —— 释放全部运动链。
            _clientB.Stop();
            CheckEq(_clientB.Rigs.Count, 0, "G9 客户端 Stop 释放了全部运动链");
            CheckEq(_clientB.Rigs.Count, 0, "G10 客户端 Stop 后无残留 Driver");

            PMNetWorld worldA = _clientA.World;
            CheckTrue(PMR3Runtime.GetBridge(worldA) != null, "G11 停止前客户端 A 的世界仍接线");
            _clientA.Stop();
            CheckTrue(PMR3Runtime.GetBridge(worldA) == null, "G12 停止后世界已从 PMR3Runtime 摘除（GetBridge 返回 null）");
            CheckEq(_clientA.Rigs.Count, 0, "G13 客户端 A 的运动链已全部释放");

            _ds.Dispose();

            // 复刻真实宿主的 static 退订（PMClientSessionHost.Stop / PMDsSessionHost.Dispose 各自
            // 退掉自己装的那一个静态出口）：订阅会强引用宿主，不退会在“退出再入局”时拿到旧宿主。
            PMR3Runtime.PlayerReplicated -= OnPlayerReplicatedGlobal;

            CheckTrue(!PMR3Runtime.HasPlayerReplicatedHandlers, "G14 宿主 Stop 退订后 static PlayerReplicated 订阅已清空");
            CheckTrue(!PMR3Runtime.HasPlayerSpawnedHandlers, "G15 DS 释放后 static PlayerSpawned 订阅已清空");

            // G16：重复释放不抛（幂等）。
            bool threw = false;
            try
            {
                _ds.Dispose();
                _clientA.Stop();
                leftover.Dispose();
            }
            catch (Exception)
            {
                threw = true;
            }

            CheckTrue(!threw, "G16 重复 Dispose/Stop 不抛异常（幂等）");
        }

        private static void TeardownAll()
        {
            try { if (_clientA != null) { _clientA.Stop(); } }
            catch (Exception) { }

            try { if (_clientB != null) { _clientB.Stop(); } }
            catch (Exception) { }

            try { if (_ds != null) { _ds.Dispose(); } }
            catch (Exception) { }

            PMR3Runtime.PlayerReplicated -= OnPlayerReplicatedGlobal;
            ClientsByWorld.Clear();
            PMR3Runtime.Shutdown();
        }

        // =================================================================================
        //  帧驱动（真实 socket，不含任何内存投递捷径）
        // =================================================================================

        /// <summary>
        /// 一个宿主帧：次序与两个真实宿主一致
        /// （DS：endpoint.Pump → 逐实例 Driver Pump；客户端：endpoint.Pump → Driver Update/Advance）。
        /// 第二轮 pump 是为了把本帧产生的上行输入/下行快照真正投递并应用
        /// —— 真实 UDP 上包要到下一次 Pump 才落地，这是链路事实，不是绕过。
        /// </summary>
        private static void Frames(int count)
        {
            for (int i = 0; i < count; i++)
            {
                _nowMs += 16L;
                long unix = _nowUnixSeconds;

                if (_ds != null && !_ds.Disposed) { _ds.Endpoint.Pump(_nowMs, unix); }
                if (_clientA != null && !_clientA.Disposed) { _clientA.Endpoint.Pump(_nowMs, unix); }
                if (_clientB != null && !_clientB.Disposed && !_clientB.Silent) { _clientB.Endpoint.Pump(_nowMs, unix); }

                if (_ds != null && !_ds.Disposed) { _ds.PumpMovement(16.0); }
                if (_clientA != null && !_clientA.Disposed) { _clientA.PumpMovement(16.0); }
                if (_clientB != null && !_clientB.Disposed && !_clientB.Silent) { _clientB.PumpMovement(16.0); }

                if (_ds != null && !_ds.Disposed) { _ds.Endpoint.Pump(_nowMs, unix); }
                if (_clientA != null && !_clientA.Disposed) { _clientA.Endpoint.Pump(_nowMs, unix); }
                if (_clientB != null && !_clientB.Disposed && !_clientB.Silent) { _clientB.Endpoint.Pump(_nowMs, unix); }
            }
        }

        private static PMMoverInput Move(float x, float z, float y, bool jump)
        {
            PMMoverInput input = new PMMoverInput();
            input.MoveX = x;
            input.MoveZ = z;
            input.MoveY = y;
            input.YawDegrees = 0f;
            input.JumpPressed = jump;
            input.Effects = null;
            input.Layers = null;
            input.RemovedLayerIds = null;
            return input;
        }

        private static void OnPlayerReplicatedGlobal(PMR3Player player)
        {
            if (player == null || player.World == null) { return; }

            ClientSide side;
            if (ClientsByWorld.TryGetValue(player.World, out side) && side != null)
            {
                side.OnReplicated(player);
            }
        }

        // =================================================================================
        //  DS 侧测试宿主（复刻 PMDsSessionHost 的**非 Unity** 次序）
        // =================================================================================

        /// <summary>
        /// DS 侧测试宿主。装配次序与真实 <c>PMDsSessionHost</c> 逐条对应：
        ///   校验局/摘要 → Register → 世界/桥/Attach → 建固定碰撞环境 → OpenServer（真实 UDP）
        ///   → Connected 时 Spawn + 建 Driver + **首次 Flush 前**发布初值
        ///   → 每帧 endpoint.Pump → 逐实例 Driver Pump（实际墙钟 + 显式 hostTickId + 独立 ServerFrame）。
        ///
        /// 与真实宿主的唯一差异：碰撞环境是确定性 AABB 替身（本机无 Unity，无法跑 PhysX）；
        /// 真实宿主用 PMR3TestScene + PMUnityMoverCollisionQuery(allowlist=地板/墙, worldVersion=1)。
        /// </summary>
        private sealed class DsSide : IDisposable
        {
            public PMDsBootstrappedMatch Boot;
            public PMSession Session;
            public PMNetWorld World;
            public PMNetSessionBridge Bridge;
            public PMUdpSessionEndpoint Endpoint;

            /// <summary>本局碰撞环境（**与客户端同一套**确定性地形）。</summary>
            public readonly PMMoverTestWorld Query = new PMMoverTestWorld();

            public readonly Dictionary<int, PMR3Player> PlayersByUid = new Dictionary<int, PMR3Player>();
            public readonly Dictionary<int, PMR4MovementDriver> DriversByUid = new Dictionary<int, PMR4MovementDriver>();
            public readonly Dictionary<int, int> RosterRowByUid = new Dictionary<int, int>();
            public readonly Dictionary<int, int> RosterTeamByUid = new Dictionary<int, int>();
            public readonly HashSet<int> FrozenUids = new HashSet<int>();

            public long HostTickId;
            public long ServerFrame;
            public int InitialSnapshotPublishedCount;
            public int RosterCount;
            public bool Disposed;

            public static DsSide Start(PMDsBootstrappedMatch boot)
            {
                DsSide side = new DsSide();
                side.Boot = boot;

                PMDsBootstrapBody body = boot.Bootstrap.AsBootstrap;
                side.RosterCount = body.Players.Length;
                for (int i = 0; i < body.Players.Length; i++)
                {
                    side.RosterRowByUid[body.Players[i].Identity.Uid] = i;
                    side.RosterTeamByUid[body.Players[i].Identity.Uid] = body.Players[i].Identity.TeamId;
                }

                side.Session = new PMSession(boot.Epoch, true);
                side.World = new PMNetWorld(side.Session);
                side.Bridge = new PMNetSessionBridge(side.World);
                PMR3Runtime.Attach(side.World, side.Bridge);

                side.Endpoint = PMUdpSessionEndpoint.OpenServer(boot, side.Bridge, "127.0.0.1", 0);
                side.Endpoint.Connected += side.OnConnected;
                return side;
            }

            private void OnConnected(PMTransportConnection connection)
            {
                if (connection == null) { return; }

                int uid = connection.Identity.Uid;
                if (PlayersByUid.ContainsKey(uid)) { return; }

                PMR3Player player = PMR3Runtime.SpawnPlayer(World, Bridge, connection);
                if (player == null) { return; }

                PlayersByUid.Add(uid, player);

                // 契约 §B1：初值必须**在首次生命周期 Flush 之前**写好，否则 Create 不带初值。
                PMR4MovementDriver driver = new PMR4MovementDriver(
                    player, Query, PMNetRole.Authority, Boot.Epoch, BuildSpawnSync(uid), PMMoverAuxState.CreateDefault());

                if (driver.PublishInitialSnapshot())
                {
                    InitialSnapshotPublishedCount++;
                }

                DriversByUid.Add(uid, driver);
            }

            /// <summary>
            /// 出生状态：x = ±3（按队伍取侧）、z = 名册行居中、y = 半高。
            /// **刻意避开默认原点**：PMMoverTestWorld 的墙盒正是 x∈[-0.5,0.5]、y∈[0,2]、z∈[-4,4]，
            /// 原点在墙内，直接用 CreateDefault 会一出生就被墙包住（位移测不出来）。
            /// </summary>
            public PMMoverSyncState BuildSpawnSync(int uid)
            {
                PMMoverSyncState state = PMMoverSyncState.CreateDefault();

                int row;
                if (!RosterRowByUid.TryGetValue(uid, out row)) { row = 0; }

                int team;
                if (!RosterTeamByUid.TryGetValue(uid, out team)) { team = 0; }

                float sign;
                if (team == 1) { sign = -1f; }
                else if (team == 2) { sign = 1f; }
                else { sign = (row % 2 == 0) ? -1f : 1f; }

                int count = RosterCount > 0 ? RosterCount : 1;
                float centeredRow = row - (count - 1) * 0.5f;

                state.Position = new PMVector3(sign * SpawnLateralOffsetMeters,
                                               PMMoverDefaults.CapsuleHalfHeightMeters,
                                               centeredRow * RosterRowSpacingMeters);
                state.Velocity = PMVector3.Zero;
                state.PreAdditiveVelocity = PMVector3.Zero;
                state.YawDegrees = 0f;
                state.Mode = PMMoverMode.Walking;
                state.Grounded = true;
                state.GroundNormal = PMVector3.Up;
                state.ActiveLayers = null;
                return state;
            }

            /// <summary>每宿主帧驱动全部权威副本（实际墙钟 + 显式 hostTickId + 独立 ServerFrame）。</summary>
            public void PumpMovement(double elapsedMs)
            {
                if (Disposed || DriversByUid.Count == 0) { return; }

                HostTickId++;
                ServerFrame++;
                PMFrameId serverFrame = new PMFrameId(PMFrameDomain.AuthorityServer, ServerFrame);

                List<int> uids = new List<int>(DriversByUid.Keys);
                for (int i = 0; i < uids.Count; i++)
                {
                    PMR4MovementDriver driver;
                    if (!DriversByUid.TryGetValue(uids[i], out driver) || driver == null || driver.IsDisposed)
                    {
                        continue;
                    }

                    driver.Update(elapsedMs);
                    driver.Pump(elapsedMs, serverFrame, HostTickId);
                }
            }

            /// <summary>
            /// 断线收尾（复刻 PMDsSessionHost.PruneDisconnectedDrivers）：
            /// 连接不再就绪的副本**先 Freeze 再 Dispose**。返回剪除数量。
            /// </summary>
            public int PruneDisconnectedDrivers()
            {
                if (Disposed || DriversByUid.Count == 0) { return 0; }

                int pruned = 0;
                List<int> uids = new List<int>(DriversByUid.Keys);
                for (int i = 0; i < uids.Count; i++)
                {
                    int uid = uids[i];

                    PMR3Player player;
                    if (!PlayersByUid.TryGetValue(uid, out player) || player == null) { continue; }

                    if (player.OwnerConnection != null && player.OwnerConnection.IsReady) { continue; }

                    PMR4MovementDriver driver;
                    if (DriversByUid.TryGetValue(uid, out driver) && driver != null)
                    {
                        driver.Freeze();
                        if (driver.IsFrozen) { FrozenUids.Add(uid); }
                        driver.Dispose();
                    }

                    DriversByUid.Remove(uid);
                    pruned++;
                }

                return pruned;
            }

            public void Dispose()
            {
                if (Disposed) { return; }
                Disposed = true;

                List<int> uids = new List<int>(DriversByUid.Keys);
                for (int i = 0; i < uids.Count; i++)
                {
                    PMR4MovementDriver driver;
                    if (!DriversByUid.TryGetValue(uids[i], out driver) || driver == null) { continue; }
                    try { driver.Freeze(); driver.Dispose(); }
                    catch (Exception) { }
                }

                DriversByUid.Clear();

                if (Endpoint != null)
                {
                    try { Endpoint.Connected -= OnConnected; } catch (Exception) { }
                    try { Endpoint.Dispose(); } catch (Exception) { }
                    Endpoint = null;
                }

                if (World != null) { PMR3Runtime.Detach(World); }

                if (Bridge != null)
                {
                    try { Bridge.Dispose(); } catch (Exception) { }
                    Bridge = null;
                }

                World = null;
            }
        }

        // =================================================================================
        //  客户端侧测试宿主（复刻 PMClientSessionHost 的**非 Unity** 次序）
        // =================================================================================

        private sealed class Rig
        {
            public PMR3Player Player;
            public PMR4MovementDriver Driver;
            public bool IsOwner;
        }

        /// <summary>
        /// 客户端侧测试宿主。与真实 <c>PMClientSessionHost</c> 的对应关系：
        ///   · 世界/桥/Attach → 建固定碰撞环境 → OpenClient（真实 UDP）；
        ///   · **所有** AP/SP 副本都建 Driver（旧实现跳过 SP 是缺陷）；
        ///   · AP：Tick + SendInputPayload；SP：Advance + SamplePresentation。
        /// 差异：没有表现层（Unity）与相机；输入由测试显式给出（真实宿主用 PMUnityMoverInput 采样，
        /// 并做“接受后才消费跳跃边沿”）。这里把边沿语义放在测试的 StepAp 里由调用方控制。
        /// </summary>
        private sealed class ClientSide : IDisposable
        {
            public PMDsEntryOffer Offer;
            public PMSession Session;
            public PMNetWorld World;
            public PMNetSessionBridge Bridge;
            public PMUdpSessionEndpoint Endpoint;

            /// <summary>本端碰撞环境（与 DS 同一套确定性地形）。</summary>
            public readonly PMMoverTestWorld Query = new PMMoverTestWorld();

            public readonly List<Rig> Rigs = new List<Rig>();
            public readonly List<PMPredictionEvent> Events = new List<PMPredictionEvent>();
            public PMR3Player ApPlayer;
            public PMR4MovementDriver ApDriver;
            public long LocalFrame;
            public bool Disposed;
            public bool Silent;

            public static ClientSide Enter(PMDsEntryOffer offer)
            {
                ClientSide side = new ClientSide();
                side.Offer = offer;

                side.Session = new PMSession(offer.Epoch, false);
                side.World = new PMNetWorld(side.Session);
                side.Bridge = new PMNetSessionBridge(side.World);
                PMR3Runtime.Attach(side.World, side.Bridge);
                ClientsByWorld[side.World] = side;

                side.Endpoint = PMUdpSessionEndpoint.OpenClient(offer, side.Bridge);
                return side;
            }

            /// <summary>复刻宿主的 PlayerReplicated 处理：**全部**副本都建 Driver，不只 AP。</summary>
            public void OnReplicated(PMR3Player player)
            {
                if (Disposed || player == null) { return; }

                for (int i = 0; i < Rigs.Count; i++)
                {
                    if (ReferenceEquals(Rigs[i].Player, player)) { return; }
                }

                bool isOwner = player.Role == PMNetRole.AutonomousProxy
                               && player.Uid == Offer.Identity.Uid;

                string error;
                PMR4MovementDriver driver = PMR4MovementDriver.CreateFromPlayerInitialSnapshot(
                    player, Query, Offer.Epoch, out error);

                if (driver == null)
                {
                    Fail("客户端 Driver 建立失败（netId=" + player.NetId.Value + " role=" + player.Role + "）：" + error);
                    return;
                }

                Rig rig = new Rig();
                rig.Player = player;
                rig.Driver = driver;
                rig.IsOwner = isOwner;
                Rigs.Add(rig);

                if (isOwner)
                {
                    ApPlayer = player;
                    ApDriver = driver;
                    driver.EventDispatched += OnEvent;
                }
            }

            private void OnEvent(PMPredictionEvent evt)
            {
                Events.Add(evt);
            }

            public int CountRole(PMNetRole role)
            {
                int count = 0;
                for (int i = 0; i < Rigs.Count; i++)
                {
                    if (Rigs[i].Player != null && Rigs[i].Player.Role == role) { count++; }
                }

                return count;
            }

            public PMR4MovementDriver FindDriverByRole(PMNetRole role)
            {
                for (int i = 0; i < Rigs.Count; i++)
                {
                    if (Rigs[i].Player != null && Rigs[i].Player.Role == role) { return Rigs[i].Driver; }
                }

                return null;
            }

            /// <summary>
            /// AP 预测一步。返回 <see cref="PMTickRejectReason.None"/> 表示**被接受**；
            /// 其它值表示被拒（冻结/历史耗尽/未确认窗口满）——此时调用方**不得**认为边沿已消费。
            /// </summary>
            public PMTickRejectReason StepAp(int stepMs, PMMoverInput input)
            {
                if (ApDriver == null || ApDriver.IsDisposed) { return PMTickRejectReason.Frozen; }

                LocalFrame++;
                PMFrameId serverFrame = new PMFrameId(PMFrameDomain.AuthorityServer, LocalFrame);
                PMTickResult result = ApDriver.Tick(stepMs, input, serverFrame);
                return result.Accepted ? PMTickRejectReason.None : result.Reject;
            }

            public void SendApInput()
            {
                if (ApDriver == null || ApDriver.IsDisposed) { return; }
                ApDriver.SendInputPayload();
            }

            /// <summary>每帧：全部 Driver 消费入站；SP 只插值。</summary>
            public void PumpMovement(double elapsedMs)
            {
                if (Disposed) { return; }

                for (int i = 0; i < Rigs.Count; i++)
                {
                    Rig rig = Rigs[i];
                    if (rig.Driver == null || rig.Driver.IsDisposed) { continue; }

                    rig.Driver.Update(elapsedMs);

                    if (!rig.IsOwner)
                    {
                        rig.Driver.Advance(elapsedMs);
                        rig.Driver.SamplePresentation();
                    }
                }
            }

            /// <summary>Stop 语义（复刻宿主的 ReleaseMovements）：释放全部 Driver 与世界接线。</summary>
            public void Stop()
            {
                if (Disposed) { return; }
                Disposed = true;

                for (int i = 0; i < Rigs.Count; i++)
                {
                    Rig rig = Rigs[i];
                    if (rig.Driver == null) { continue; }
                    try { rig.Driver.Freeze(); rig.Driver.Dispose(); }
                    catch (Exception) { }
                    rig.Driver = null;
                }

                Rigs.Clear();

                if (Endpoint != null)
                {
                    try { Endpoint.Dispose(); } catch (Exception) { }
                    Endpoint = null;
                }

                if (World != null)
                {
                    PMR3Runtime.Detach(World);
                    ClientsByWorld.Remove(World);
                }

                if (Bridge != null)
                {
                    try { Bridge.Dispose(); } catch (Exception) { }
                    Bridge = null;
                }

                World = null;
            }

            public void Dispose() { Stop(); }
        }
    }
}
