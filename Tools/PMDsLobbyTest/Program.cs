using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using PMNet.Control;

namespace PMDsLobbyTest
{
    /// <summary>
    /// R3-B3 验收门禁：Lobby 控制 listener / 全局 uid 账本 / 宿主串行调度 / 真实回环控制链路。
    ///
    /// 事实来源：
    /// - `Docs/plans/net-r3-control-contract.md` §3（控制消息与状态）、§5（验收边界）、§7.1/§7.3（冻结接口）
    /// - `Docs/plans/net-architecture-migration.md` 的 R3B3 行
    ///
    /// 门禁结构：
    /// A 端口池 + 跨会话 uid 账本（两局不同端口 / 同 uid 拒绝 / 端口耗尽不泄漏 uid 预留）
    /// B 引导文件原子发布 + 启动参数一致性 + Allocated 态 SetLaunchRequest 校验
    /// C 引导文件发布失败 → 回滚（不启动进程、归还端口与 uid）
    /// D 真实 loopback 控制链路：坏 MAC 不占连接 / 半包 / 就绪才发 offer / 重复 Ready 幂等
    /// E 第二 TCP 连接不得冒用已绑定的控制连接（重放同一 Ready）
    /// F 结果幂等与冲突 + 结果通知（不伪造 BattleReview）+ 确认退出后释放/恢复在线
    /// G 客户端断线中止局（不伪造胜利）+ 延迟退出仍占用 + 退出后才恢复
    /// H listener 分帧（半包/粘包/超限立即断开）与三重有界 + 非 loopback 拒绝
    /// I 账本单元语义（原子预留 / 释放 / 幂等）
    ///
    /// 退出码：0 = 全部通过；1 = 有失败。
    /// </summary>
    internal static class Program
    {
        private static int _passed;
        private static int _negativePassed;
        private static readonly List<string> _failures = new List<string>();
        private static readonly Dictionary<string, int> _tagTotal = new Dictionary<string, int>();
        private static string _tag = "R3B3";
        private static string _root;

        private static int Main(string[] args)
        {
            _root = Path.Combine(Path.GetTempPath(), "pmds-lobby-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            Console.WriteLine("=== R3-B3：Lobby 控制 listener / 全局账本 / 宿主串行调度 ===");
            Console.WriteLine("    临时工作目录：" + _root);
            Console.WriteLine();

            try
            {
                Section("A. 端口池 + 跨会话 uid 账本：两局不同端口 / 同 uid 拒绝 / 预留回滚", TestPortsAndLedger);
                Section("B. 引导文件原子发布 + 启动参数一致性 + SetLaunchRequest 校验", TestBootstrapAndLaunchArgs);
                Section("C. 失败回滚：引导发布 / 进程启动 / offer 投递（端口与 uid 归还）", TestBootstrapRollback);
                Section("D. 真实 loopback 控制链路：坏 MAC / 半包 / 就绪才发 offer / 重复 Ready", TestControlLinkHappyPath);
                Section("E. 第二 TCP 连接不得冒用已绑定的控制连接", TestSecondConnectionHijack);
                Section("F. 结果幂等与冲突 / 结果通知 / 确认退出后释放与恢复", TestResultIdempotencyAndRelease);
                Section("G. 客户端断线中止局（不伪造胜利）+ 延迟退出仍占用", TestClientDisconnectAndDelayedExit);
                Section("H. listener 分帧与有界：半包 / 粘包 / 超限立即断开 / 连接上限 / 非 loopback", TestListenerFramingAndBounds);
                Section("I. 账本单元语义：原子预留 / 释放 / 幂等 / 非法输入", TestLedgerUnitSemantics);
                Section("J. 宿主 offer 字段 ⇄ 冻结 PMDsEntryCodec 往返（不另写替身 codec）",
                    TestFrozenEntryOfferRoundTrip);
                Section("K. 认证正常退出的优雅退出宽限（真实宿主链路 + 真实 loopback TCP）",
                    TestGracefulExitGraceThroughHost);
            }
            finally
            {
                try
                {
                    Directory.Delete(_root, true);
                }
                catch (Exception)
                {
                }
            }

            Console.WriteLine();
            List<string> tags = new List<string>(_tagTotal.Keys);
            tags.Sort(StringComparer.Ordinal);
            for (int i = 0; i < tags.Count; i++)
            {
                Console.WriteLine("  " + tags[i] + "：" + _tagTotal[tags[i]] + " 项检查");
            }

            Console.WriteLine();
            if (_failures.Count == 0)
            {
                Console.WriteLine("  全部通过：" + _passed + " 项检查（含负向输入 " + _negativePassed + " 项），0 项失败");
                return 0;
            }

            Console.WriteLine("  " + _passed + " 项通过，" + _failures.Count + " 项失败：");
            for (int i = 0; i < _failures.Count; i++)
            {
                Console.WriteLine("    - " + _failures[i]);
            }

            return 1;
        }

        // ────────────────────────────────────────────────────────────────
        // A. 端口池 + 跨会话 uid 账本
        // ────────────────────────────────────────────────────────────────

        private static void TestPortsAndLedger()
        {
            FakeClock clock = new FakeClock();
            FakeLauncher launcher = new FakeLauncher();
            FakeGateway gateway = new FakeGateway();
            gateway.Online.Add(1);
            gateway.Online.Add(2);
            gateway.Online.Add(3);
            gateway.Online.Add(4);
            gateway.Online.Add(5);
            gateway.Online.Add(9);

            PMDsLobbyHost host = CreateHost(clock, launcher, gateway, null);
            try
            {
                Check(host.TryStartMatch(MakeRequest("m-two-1", 1, 2)).IsQueued, "第一局已入队");
                Check(PumpUntil(host, () => host.StartedSessions.Length == 1), "第一局已落地启动");
                Check(host.TryStartMatch(MakeRequest("m-two-2", 3, 4)).IsQueued, "第二局已入队");
                Check(PumpUntil(host, () => host.StartedSessions.Length == 2), "第二局已落地启动");

                PMDsLobbySessionRecord[] records = host.StartedSessions;
                Check(records.Length == 2, "确有两条启动记录");
                Check(records[0].Port != records[1].Port, "两局拿到**不同**端口（共享端口池生效）");
                Check(host.PortPool.InUseCount == 2, "端口池占用 = 2");
                Check(host.PlayerLedger.Count == 4, "跨会话 uid 账本占用 = 4");
                Check(host.PlayerLedger.IsOccupied(1) && host.PlayerLedger.IsOccupied(4), "四个 uid 均被占用");
                Check(launcher.Processes.Count == 2, "两个受控进程均已启动");
                Check(launcher.Processes[0].IsRunning && launcher.Processes[1].IsRunning, "两个进程均在运行");

                // 同一 uid 试图再进一局：宿主预检阶段即整局拒绝。
                int sessionsBefore = host.SessionCount;
                PMDsLobbyStartReply repeat = host.TryStartMatch(MakeRequest("m-two-3", 1, 5));
                CheckRejected(repeat.Outcome == PMDsLobbyStartOutcome.RejectedPlayerOccupied,
                    "同 uid 二次入局被拒：" + repeat);
                Check(host.SessionCount == sessionsBefore, "拒绝后未创建多余会话");
                Check(host.PortPool.InUseCount == 2, "拒绝后端口占用不变");
                Check(host.PlayerLedger.Count == 4, "拒绝后 uid 占用不变");

                // 离线缺人：整局拒绝，不缩编。
                PMDsLobbyStartReply offline = host.TryStartMatch(MakeRequest("m-two-4", 9, 77));
                CheckRejected(offline.Outcome == PMDsLobbyStartOutcome.RejectedOffline,
                    "名册缺人整局拒绝（不缩编）：" + offline);
                Check(host.SessionCount == sessionsBefore, "离线拒绝未创建会话");
                Check(host.PlayerLedger.Count == 4, "离线拒绝未留下部分 uid 预留");
                Check(!host.PlayerLedger.IsOccupied(77), "未入局的 uid 未被写入账本");

                // 协调器层（绕过宿主预检）：共享账本的 uid 冲突必须由协调器再挡一次。
                PMDsPortPool pool = new PMDsPortPool(7901, 7905);
                PMDsPlayerLedger ledger = new PMDsPlayerLedger();
                PMDsCoordinator first = new PMDsCoordinator(MakeCoordinatorOptions(), clock, launcher,
                    PMDsNullCoordinatorSink.Instance, pool, ledger);
                PMDsCoordinator second = new PMDsCoordinator(MakeCoordinatorOptions(), clock, launcher,
                    PMDsNullCoordinatorSink.Instance, pool, ledger);

                PMDsCoordinatorReply firstReply = first.Allocate(MakeAllocation("m-c1", "ds-c1", 1u, 11, 12));
                Check(firstReply.IsAccepted, "协调器层第一局分配成功：" + firstReply);
                PMDsCoordinatorReply secondReply = second.Allocate(MakeAllocation("m-c2", "ds-c2", 2u, 12, 13));
                CheckRejected(secondReply.Outcome == PMDsCoordinatorOutcome.RejectedPlayerOccupied,
                    "协调器层同 uid 冲突被拒：" + secondReply);
                Check(second.AllocatedPort == 0, "被拒会话没有拿到端口");
                Check(pool.InUseCount == 1, "被拒会话没有占用端口");
                Check(ledger.IsOccupied(11) && ledger.IsOccupied(12), "被拒会话没有污染已有占用");

                // 端口耗尽：不得留下「占着 uid 却没有端口」的泄漏。
                PMDsPortPool single = new PMDsPortPool(7910, 7910);
                PMDsPlayerLedger ledger2 = new PMDsPlayerLedger();
                PMDsCoordinator holder = new PMDsCoordinator(MakeCoordinatorOptions(), clock, launcher,
                    PMDsNullCoordinatorSink.Instance, single, ledger2);
                PMDsCoordinator starved = new PMDsCoordinator(MakeCoordinatorOptions(), clock, launcher,
                    PMDsNullCoordinatorSink.Instance, single, ledger2);
                Check(holder.Allocate(MakeAllocation("m-d1", "ds-d1", 3u, 21)).IsAccepted, "占满唯一端口");
                PMDsCoordinatorReply starvedReply = starved.Allocate(MakeAllocation("m-d2", "ds-d2", 4u, 22));
                CheckRejected(starvedReply.Outcome == PMDsCoordinatorOutcome.RejectedState,
                    "端口耗尽被拒：" + starvedReply);
                CheckRejected(!ledger2.IsOccupied(22), "端口耗尽时 uid 预留被回滚（不泄漏）");
                Check(ledger2.Count == 1, "端口耗尽后账本只剩已成功的一局");
            }
            finally
            {
                host.Dispose();
            }
        }

        // ────────────────────────────────────────────────────────────────
        // B. 引导文件 + 启动参数 + SetLaunchRequest 校验
        // ────────────────────────────────────────────────────────────────

        private static void TestBootstrapAndLaunchArgs()
        {
            FakeClock clock = new FakeClock();
            FakeLauncher launcher = new FakeLauncher();
            FakeGateway gateway = new FakeGateway();
            gateway.Online.Add(31);
            gateway.Online.Add(32);

            PMDsLobbyHost host = CreateHost(clock, launcher, gateway, null);
            try
            {
                Check(host.TryStartMatch(MakeRequest("m-args", 31, 32)).IsQueued, "开局已入队");
                Check(PumpUntil(host, () => host.StartedSessions.Length == 1), "开局已落地");

                PMDsLobbySessionRecord record = host.StartedSessions[0];
                Check(launcher.Requests.Count == 1, "启动请求已交给启动器");
                PMDsProcessLaunchRequest request = launcher.Requests[0];

                Check(string.Equals(request.ExecutablePath, Path.Combine(_root, "HyldDS.exe"), StringComparison.OrdinalIgnoreCase),
                    "启动参数使用配置里的固定 exe（未从客户端输入取）");

                string bootstrapPath = ArgValue(request.Arguments, "-bootstrap");
                Check(!string.IsNullOrEmpty(bootstrapPath), "参数含 -bootstrap");
                Check(Path.IsPathRooted(bootstrapPath), "-bootstrap 是绝对路径");
                Check(bootstrapPath.StartsWith(Path.Combine(_root, "boot"), StringComparison.OrdinalIgnoreCase),
                    "-bootstrap 落在配置的引导目录下：" + bootstrapPath);
                Check(File.Exists(bootstrapPath), "引导文件在 BeginStart 之前已发布到磁盘");

                string portText = ArgValue(request.Arguments, "-port");
                Check(portText == record.Port.ToString(), "-port 等于 Allocate 后的真实端口（不硬编码 7801）");
                Check(record.Port != 7801 || portText == "7801", "端口值就是真实分配值");
                Check(ArgValue(request.Arguments, "-matchid") == "m-args", "-matchid 与本局一致");
                Check(ArgValue(request.Arguments, "-dsid") == record.DsId, "-dsid 与本局 DsId 一致");
                Check(ArgValue(request.Arguments, "-control") == "127.0.0.1:" + host.ControlPort,
                    "-control 指向宿主真实控制端口：" + ArgValue(request.Arguments, "-control"));
                Check(CountKey(request.Arguments, "-port") == 1, "-port 恰好出现一次");
                Check(CountKey(request.Arguments, "-bootstrap") == 1, "-bootstrap 恰好出现一次");
                Check(request.Arguments[0] == "-batchmode" && request.Arguments[1] == "-nographics",
                    "沿用 DS 无头启动惯例（-batchmode -nographics）");
                Check(Array.IndexOf(request.Arguments, "-server") >= 0, "含 -server 身份标识");

                // 引导文件内容：可解码、身份一致、票据可验签且与名册一致。
                string decodeError;
                PMDsBootstrappedMatch boot = ReadBootstrap(bootstrapPath, out decodeError);
                Check(boot != null, "引导文件可解码：" + decodeError);
                if (boot != null)
                {
                    Check(boot.MatchId == "m-args" && boot.DsId == record.DsId && boot.Epoch == record.Epoch,
                        "引导文件身份字段与分配一致");
                    Check(boot.ProtocolHash == hostOptionsProtocolHash, "引导文件协议摘要来自注入配置");
                    Check(boot.CollisionDigest != 0u, "引导文件碰撞摘要非 0");
                    Check(boot.Bootstrap.AsBootstrap.Players.Length == 2, "引导文件名册 2 人");

                    PMDsTicketVerifier verifier = boot.Key.CreateTicketVerifier(
                        new PMDsTicketExpectation(boot.MatchId, boot.DsId, boot.Epoch, boot.ProtocolHash));
                    long nowSeconds = clock.UtcNowUnixMilliseconds / 1000L;
                    PMDsBootstrapPlayer player0 = boot.Bootstrap.AsBootstrap.Players[0];
                    PMDsTicketVerification verification = verifier.Verify(
                        player0.Ticket, 0, player0.Ticket.Length, nowSeconds);
                    Check(verification.IsValid, "引导文件里的票据可通过验签：" + verification);
                    Check(verification.Identity.Uid == 31, "票据绑定的 uid 与名册一致");
                }

                // Allocated 态 SetLaunchRequest 的 fail-closed 校验（协调器层直接验证）。
                PMDsPortPool pool = new PMDsPortPool(7920, 7925);
                PMDsPlayerLedger ledger = new PMDsPlayerLedger();
                PMDsCoordinator coordinator = new PMDsCoordinator(MakeCoordinatorOptions(), clock, launcher,
                    PMDsNullCoordinatorSink.Instance, pool, ledger);
                string fakeBootstrap = Path.Combine(_root, "expected-bootstrap.bin");
                PMDsCoordinatorReply allocated = coordinator.Allocate(
                    MakeAllocation("m-launch", "ds-launch", 5u, fakeBootstrap, 41, 42));
                Check(allocated.IsAccepted, "分配成功（带期望引导文件路径）");

                PMDsCoordinatorReply wrongPort = coordinator.SetLaunchRequest(
                    MakeLaunch("m-launch", "ds-launch", coordinator.AllocatedPort + 1, fakeBootstrap, "127.0.0.1:7800"));
                CheckRejected(wrongPort.Outcome == PMDsCoordinatorOutcome.RejectedMalformed, "错误 -port 被拒：" + wrongPort);
                Check(coordinator.State == PMDsSessionState.Allocated, "被拒后状态未变");
                Check(coordinator.AllocatedPort != 0, "被拒后端口仍被本会话持有（未静默释放）");

                PMDsCoordinatorReply missingBootstrap = coordinator.SetLaunchRequest(
                    MakeLaunchWithoutBootstrap("m-launch", "ds-launch", coordinator.AllocatedPort, "127.0.0.1:7800"));
                CheckRejected(missingBootstrap.Outcome == PMDsCoordinatorOutcome.RejectedMalformed,
                    "缺少 -bootstrap 被拒：" + missingBootstrap);

                PMDsCoordinatorReply duplicatePort = coordinator.SetLaunchRequest(
                    MakeLaunchWithDuplicatePort("m-launch", "ds-launch", coordinator.AllocatedPort, fakeBootstrap));
                CheckRejected(duplicatePort.Outcome == PMDsCoordinatorOutcome.RejectedMalformed,
                    "重复 -port 被拒（fail-closed）：" + duplicatePort);

                PMDsCoordinatorReply wrongDsId = coordinator.SetLaunchRequest(
                    MakeLaunch("m-launch", "ds-other", coordinator.AllocatedPort, fakeBootstrap, "127.0.0.1:7800"));
                CheckRejected(wrongDsId.Outcome == PMDsCoordinatorOutcome.RejectedMalformed, "错 -dsid 被拒：" + wrongDsId);

                PMDsCoordinatorReply wrongMatch = coordinator.SetLaunchRequest(
                    MakeLaunch("m-other", "ds-launch", coordinator.AllocatedPort, fakeBootstrap, "127.0.0.1:7800"));
                CheckRejected(wrongMatch.Outcome == PMDsCoordinatorOutcome.RejectedMalformed, "错 -matchid 被拒：" + wrongMatch);

                PMDsCoordinatorReply ok = coordinator.SetLaunchRequest(
                    MakeLaunch("m-launch", "ds-launch", coordinator.AllocatedPort, fakeBootstrap, "127.0.0.1:7800"));
                Check(ok.IsAccepted, "全部一致时接受：" + ok);
                coordinator.RequestShutdown(9u);
            }
            finally
            {
                host.Dispose();
            }
        }

        // ────────────────────────────────────────────────────────────────
        // C. 引导文件发布失败 → 回滚
        // ────────────────────────────────────────────────────────────────

        private static void TestBootstrapRollback()
        {
            FakeClock clock = new FakeClock();
            FakeLauncher launcher = new FakeLauncher();
            FakeGateway gateway = new FakeGateway();
            gateway.Online.Add(51);
            gateway.Online.Add(52);

            // 用一个「已存在的普通文件」当父目录，让 Directory.CreateDirectory 必然失败。
            string blocker = Path.Combine(_root, "blocker-file");
            File.WriteAllText(blocker, "not a directory");

            PMDsLobbyHost host = CreateHost(clock, launcher, gateway,
                delegate(PMDsLobbyHostOptions options)
                {
                    options.BootstrapRootDirectory = Path.Combine(blocker, "boot");
                });

            bool failed = false;
            string failedMatch = null;
            host.SessionFailed += delegate(string matchId, string reason)
            {
                failed = true;
                failedMatch = matchId;
            };

            try
            {
                Check(host.TryStartMatch(MakeRequest("m-boot-fail", 51, 52)).IsQueued, "开局已入队");
                Check(PumpUntil(host, () => failed), "发布失败被观测到");
                Check(failedMatch == "m-boot-fail", "失败事件带的 matchId 正确");
                Check(host.BootstrapPublishFailures == 1, "引导发布失败计数 = 1");
                CheckRejected(launcher.Requests.Count == 0, "**进程未被启动**（BeginStart 未被调用）");
                CheckRejected(host.PortPool.InUseCount == 0, "端口已归还");
                CheckRejected(host.PlayerLedger.Count == 0, "uid 占用已释放");
                Check(PumpUntil(host, () => host.SessionCount == 0), "失败会话已从宿主视图移除");
                Check(host.GetCoordinator("m-boot-fail") == null, "失败会话不再可查");
            }
            finally
            {
                host.Dispose();
            }

            // C2：进程启动失败（结构层／启动器层）⇒ 回滚。
            FakeClock clock2 = new FakeClock();
            FakeLauncher launcher2 = new FakeLauncher();
            launcher2.FailStart = true;
            FakeGateway gateway2 = new FakeGateway();
            gateway2.Online.Add(53);

            PMDsLobbyHost host2 = CreateHost(clock2, launcher2, gateway2, null);
            bool startFailed = false;
            host2.SessionFailed += delegate(string matchId, string reason) { startFailed = true; };
            try
            {
                Check(host2.TryStartMatch(MakeRequest("m-start-fail", 53)).IsQueued, "启动失败用例已入队");
                Check(PumpUntil(host2, () => startFailed), "启动失败被观测到");
                Check(launcher2.Requests.Count == 1, "启动器确实被调用过一次（失败来自启动层）");
                CheckRejected(host2.PortPool.InUseCount == 0, "启动失败后端口已归还");
                CheckRejected(host2.PlayerLedger.Count == 0, "启动失败后 uid 已释放");
                Check(PumpUntil(host2, () => host2.SessionCount == 0), "启动失败会话已移除");
            }
            finally
            {
                host2.Dispose();
            }

            // C3：就绪后 offer 投递失败 ⇒ 整局失败（不伪装成功），进程死后才归还资源。
            FakeClock clock3 = new FakeClock();
            FakeLauncher launcher3 = new FakeLauncher();
            FakeGateway gateway3 = new FakeGateway();
            gateway3.Online.Add(54);
            gateway3.FailOffer = true;

            PMDsLobbyHost host3 = CreateHost(clock3, launcher3, gateway3, null);
            Socket controlSocket3 = null;
            try
            {
                Check(host3.TryStartMatch(MakeRequest("m-offer-fail", 54)).IsQueued, "offer 失败用例已入队");
                Check(PumpUntil(host3, () => host3.StartedSessions.Length == 1), "offer 失败用例已启动进程");

                string decodeError3;
                PMDsBootstrappedMatch boot3 = ReadBootstrap(
                    ArgValue(launcher3.Requests[0].Arguments, "-bootstrap"), out decodeError3);
                if (boot3 == null)
                {
                    Check(false, "引导文件不可解码：" + decodeError3);
                    return;
                }

                PMDsCoordinator coordinator3 = host3.GetCoordinator("m-offer-fail");
                controlSocket3 = Connect(host3.ControlPort);
                SendBytes(controlSocket3, BuildReadyFrame(boot3, host3.StartedSessions[0].Port,
                    boot3.CollisionDigest, true), 64);

                Check(PumpUntil(host3, () => host3.EntryOfferFailures >= 1), "offer 投递失败被计数");
                CheckRejected(gateway3.Offers.Count == 0, "没有任何 offer 落地");
                CheckRejected(coordinator3.State != PMDsSessionState.Running, "offer 失败不得进入 Running");
                Check(host3.PortPool.InUseCount == 1 && host3.PlayerLedger.Count == 1,
                    "进程仍在 ⇒ 资源保持占用（不提前归还）");

                launcher3.Processes[0].Stop(0);
                Check(PumpUntil(host3, () => host3.SessionCount == 0), "确认退出后失败会话被回收");
                Check(host3.PortPool.InUseCount == 0 && host3.PlayerLedger.Count == 0, "端口与 uid 归还");
                Check(gateway3.Restored.Count == 1, "参战玩家恢复在线");
            }
            finally
            {
                CloseQuietly(controlSocket3);
                host3.Dispose();
            }
        }

        // ────────────────────────────────────────────────────────────────
        // D. 真实 loopback 控制链路
        // ────────────────────────────────────────────────────────────────

        private static void TestControlLinkHappyPath()
        {
            FakeClock clock = new FakeClock();
            FakeLauncher launcher = new FakeLauncher();
            FakeGateway gateway = new FakeGateway();
            gateway.Online.Add(61);
            gateway.Online.Add(62);

            PMDsLobbyHost host = CreateHost(clock, launcher, gateway, null);
            Socket badSocket = null;
            Socket controlSocket = null;

            try
            {
                Check(host.TryStartMatch(MakeRequest("m-ctl", 61, 62)).IsQueued, "开局已入队");
                Check(PumpUntil(host, () => host.StartedSessions.Length == 1), "开局已落地");

                PMDsLobbySessionRecord record = host.StartedSessions[0];
                PMDsCoordinator coordinator = host.GetCoordinator("m-ctl");
                string bootstrapPath = ArgValue(launcher.Requests[0].Arguments, "-bootstrap");
                string decodeError;
                PMDsBootstrappedMatch boot = ReadBootstrap(bootstrapPath, out decodeError);
                if (boot == null)
                {
                    Check(false, "引导文件不可解码：" + decodeError);
                    return;
                }

                byte[] readyFrame = BuildReadyFrame(boot, record.Port, boot.CollisionDigest, true);

                // D1 坏 MAC：不得占据会话控制连接、不得改状态、不得发 offer。
                badSocket = Connect(host.ControlPort);
                byte[] tampered = (byte[])readyFrame.Clone();
                tampered[tampered.Length - 1] ^= 0xFF;
                SendBytes(badSocket, tampered, tampered.Length);
                CheckRejected(PumpUntil(host, () => host.InboundFramesDroppedMacFailed > 0), "坏 MAC 帧被拒并计数");
                CheckRejected(coordinator.State == PMDsSessionState.Starting, "坏 MAC 不推进状态");
                CheckRejected(gateway.Offers.Count == 0, "坏 MAC 不发布 offer");
                CheckRejected(!TryReadPayload(badSocket, 400, out byte[] _), "坏 MAC 的连接被关闭（读取失败）");

                // D2 正确 Ready，故意拆成多次写入验证半包。
                controlSocket = Connect(host.ControlPort);
                SendBytes(controlSocket, readyFrame, 0, 5, 5);
                host.PumpOnce();
                Thread.Sleep(10);
                SendBytes(controlSocket, readyFrame, 5, 11, 11);
                host.PumpOnce();
                Thread.Sleep(10);
                SendBytes(controlSocket, readyFrame, 16, readyFrame.Length - 16, readyFrame.Length - 16);

                Check(PumpUntil(host, () => coordinator.State == PMDsSessionState.Running), "合法 Ready 推进到 Running");
                Check(PumpUntil(host, () => gateway.Offers.Count == 2), "就绪后向两名玩家各发一次 offer");
                Check(coordinator.Counters.ReadyPublications == 1, "地址只发布一次");
                Check(coordinator.AllocatedPort == record.Port, "会话端口未被改写");

                PMDsLobbyEntryNotice offer0 = gateway.Offers[0];
                Check(offer0.MatchId == "m-ctl" && offer0.DsId == record.DsId, "offer 携带本局身份");
                Check(offer0.Port == record.Port, "offer 携带真实 DS 端口");
                Check(offer0.Epoch == record.Epoch && offer0.ProtocolHash == boot.ProtocolHash,
                    "offer 携带世代与协议摘要");
                Check(offer0.CollisionDigest == boot.CollisionDigest, "offer 携带碰撞摘要");
                Check(offer0.Ticket.Length > 0, "offer 携带该玩家票据");
                Check(offer0.Identity.Uid == 61 && gateway.Offers[1].Identity.Uid == 62,
                    "offer 按名册逐人发送（身份来自名册而非业务包自报）");

                PMDsTicketVerifier verifier = boot.Key.CreateTicketVerifier(
                    new PMDsTicketExpectation(boot.MatchId, boot.DsId, boot.Epoch, boot.ProtocolHash));
                PMDsTicketVerification verification = verifier.Verify(
                    offer0.Ticket, 0, offer0.Ticket.Length, clock.UtcNowUnixMilliseconds / 1000L);
                Check(verification.IsValid && verification.Identity.Uid == 61, "offer 票据可验签且身份正确");

                // D3 重复 Ready：幂等，不重复发 offer。
                SendBytes(controlSocket, readyFrame, readyFrame.Length);
                host.PumpOnce();
                Thread.Sleep(20);
                host.PumpOnce();
                Check(gateway.Offers.Count == 2, "重复 Ready 不重复发布 offer");
                Check(coordinator.Counters.ReadyDuplicates >= 1, "重复 Ready 被计为幂等重复");
                Check(coordinator.State == PMDsSessionState.Running, "重复 Ready 后仍是 Running");

                // D4 已绑定的连接继续可用：心跳被接受。
                SendBytes(controlSocket, BuildHeartbeatFrame(boot), 9);
                Check(PumpUntil(host, () => coordinator.Counters.Heartbeats >= 1), "已绑定连接的心跳被接受");
                SendBytes(controlSocket, BuildHeartbeatFrame(boot), 9);
                Check(PumpUntil(host, () => coordinator.Counters.Heartbeats >= 2), "同一连接可继续发送控制帧");
            }
            finally
            {
                CloseQuietly(badSocket);
                CloseQuietly(controlSocket);
                host.Dispose();
            }
        }

        // ────────────────────────────────────────────────────────────────
        // E. 第二 TCP 连接不得冒用
        // ────────────────────────────────────────────────────────────────

        private static void TestSecondConnectionHijack()
        {
            FakeClock clock = new FakeClock();
            FakeLauncher launcher = new FakeLauncher();
            FakeGateway gateway = new FakeGateway();
            gateway.Online.Add(71);
            gateway.Online.Add(72);

            PMDsLobbyHost host = CreateHost(clock, launcher, gateway, null);
            Socket first = null;
            Socket second = null;

            try
            {
                Check(host.TryStartMatch(MakeRequest("m-hijack", 71, 72)).IsQueued, "开局已入队");
                Check(PumpUntil(host, () => host.StartedSessions.Length == 1), "开局已落地");

                PMDsLobbySessionRecord record = host.StartedSessions[0];
                PMDsCoordinator coordinator = host.GetCoordinator("m-hijack");
                string decodeError;
                PMDsBootstrappedMatch boot = ReadBootstrap(
                    ArgValue(launcher.Requests[0].Arguments, "-bootstrap"), out decodeError);
                if (boot == null)
                {
                    Check(false, "引导文件不可解码：" + decodeError);
                    return;
                }

                byte[] readyFrame = BuildReadyFrame(boot, record.Port, boot.CollisionDigest, true);

                first = Connect(host.ControlPort);
                SendBytes(first, readyFrame, readyFrame.Length);
                Check(PumpUntil(host, () => coordinator.State == PMDsSessionState.Running), "第一条连接完成就绪");
                Check(gateway.Offers.Count == 2, "第一条连接拿到 offer");

                int offersBefore = gateway.Offers.Count;

                // 第二条连接重放完全合法的 Ready（MAC 也是对的）：只可能来自持票重放/顶替，
                // 此时该会话已有活着的控制连接 ⇒ 必须拒绝第二条。
                second = Connect(host.ControlPort);
                SendBytes(second, readyFrame, readyFrame.Length);
                Check(PumpUntil(host, () => host.HijackAttempts > 0), "第二连接被识别为冒用");
                Check(!TryReadPayload(second, 400, out byte[] _), "第二连接已被关闭");
                CheckRejected(gateway.Offers.Count == offersBefore, "冒用连接没有触发额外 offer");
                Check(coordinator.State == PMDsSessionState.Running, "冒用连接没有改变会话状态");
                CheckRejected(coordinator.Counters.ReadyDuplicates == 0, "冒用帧未进入协调器（未计入重复 Ready）");

                // 原连接仍然有效。
                SendBytes(first, BuildHeartbeatFrame(boot), 7);
                Check(PumpUntil(host, () => coordinator.Counters.Heartbeats >= 1), "原控制连接未被误伤");

                // 原连接关闭后允许重连（重连策略：只有在观测到断开之后）。
                CloseQuietly(first);
                first = null;
                Check(PumpUntil(host, () => host.HasBoundControlConnection("m-hijack") == false),
                    "断开后控制连接被解绑");

                Socket reconnect = Connect(host.ControlPort);
                try
                {
                    SendBytes(reconnect, BuildHeartbeatFrame(boot), 6);
                    Check(PumpUntil(host, () => coordinator.Counters.Heartbeats >= 2), "断开后允许新连接重新绑定");
                }
                finally
                {
                    CloseQuietly(reconnect);
                }
            }
            finally
            {
                CloseQuietly(first);
                CloseQuietly(second);
                host.Dispose();
            }
        }

        // ────────────────────────────────────────────────────────────────
        // F. 结果幂等与冲突 / 结果通知 / 释放与恢复
        // ────────────────────────────────────────────────────────────────

        private static void TestResultIdempotencyAndRelease()
        {
            FakeClock clock = new FakeClock();
            FakeLauncher launcher = new FakeLauncher();
            FakeGateway gateway = new FakeGateway();
            gateway.Online.Add(81);
            gateway.Online.Add(82);
            gateway.ProcessAliveProbe = () => launcher.Processes.Count > 0 && launcher.Processes[0].IsRunning;

            PMDsLobbyHost host = CreateHost(clock, launcher, gateway, null);
            Socket controlSocket = null;

            int resultEvents = 0;
            int lastWinner = -1;
            host.ResultAccepted += delegate(PMDsLobbyResultNotice notice)
            {
                resultEvents++;
                lastWinner = notice.WinnerTeamId;
            };

            try
            {
                Check(host.TryStartMatch(MakeRequest("m-result", 81, 82)).IsQueued, "开局已入队");
                Check(PumpUntil(host, () => host.StartedSessions.Length == 1), "开局已落地");

                PMDsLobbySessionRecord record = host.StartedSessions[0];
                PMDsCoordinator coordinator = host.GetCoordinator("m-result");
                string decodeError;
                PMDsBootstrappedMatch boot = ReadBootstrap(
                    ArgValue(launcher.Requests[0].Arguments, "-bootstrap"), out decodeError);
                if (boot == null)
                {
                    Check(false, "引导文件不可解码：" + decodeError);
                    return;
                }

                controlSocket = Connect(host.ControlPort);
                SendBytes(controlSocket, BuildReadyFrame(boot, record.Port, boot.CollisionDigest, true), 64);
                Check(PumpUntil(host, () => coordinator.State == PMDsSessionState.Running), "会话就绪并 Running");

                byte[] resultFrame = BuildResultFrame(boot, 4242UL, 1, Encoding.UTF8.GetBytes("summary-bytes"));
                SendBytes(controlSocket, resultFrame, 32);
                Check(PumpUntil(host, () => gateway.Results.Count == 1 && resultEvents == 1),
                    "结果被接受并产生控制通知与公开事件");
                Check(resultEvents == 1 && lastWinner == 1, "公开 ResultAccepted 事件只触发一次且胜方正确");
                Check(coordinator.State == PMDsSessionState.ResultPending, "结果推进到 ResultPending");

                byte[] ackPayload;
                // 注意：R3-B 双向 liveness 修复后，Lobby 在合法 Ready 之后会补一条签名 Heartbeat，
                // 「Ready 之后的下一条下行帧」因此不再必然是 ResultAck ⇒ 这里改成**按消息类型读**
                // （断言面一条不减：类型 / ResultId / 字节一致性仍逐项验）。
                Check(TryReadPayloadOfType(controlSocket, 2000, boot.Key.CreateControlSigner(),
                        PMDsControlMessageType.ResultAck, out ackPayload), "收到 ResultAck 帧");
                if (ackPayload != null)
                {
                    PMDsControlMessage ackMessage;
                    PMDsControlVerifyFault fault;
                    PMDsControlSigner signer = boot.Key.CreateControlSigner();
                    Check(signer.VerifyRaw(ackPayload, 0, ackPayload.Length, out ackMessage, out fault),
                        "ResultAck 验签通过（fault=" + fault + "）");
                    if (ackMessage != null)
                    {
                        Check(ackMessage.Type == PMDsControlMessageType.ResultAck, "aсk 类型是 ResultAck");
                        Check(ackMessage.AsResultAck != null && ackMessage.AsResultAck.ResultId == 4242UL,
                            "ResultAck 回带同一 ResultId");
                    }
                }

                // 重复同一结果：幂等，不产生第二条业务通知。
                SendBytes(controlSocket, resultFrame, resultFrame.Length);
                Check(PumpUntil(host, () => coordinator.Counters.ResultDuplicates >= 1), "重复结果被计为幂等重复");
                byte[] secondAck;
                Check(TryReadPayloadOfType(controlSocket, 2000, boot.Key.CreateControlSigner(),
                        PMDsControlMessageType.ResultAck, out secondAck), "重复结果仍重发同一 ResultAck");
                Check(secondAck != null && ackPayload != null && BytesEqual(secondAck, ackPayload),
                    "重复结果的 Ack 字节完全一致");
                Check(gateway.Results.Count == 1, "重复结果不重复通知业务层");

                // 冲突结果：同 ResultId、不同胜方 ⇒ 必须被拒，且不覆盖已有结果。
                byte[] conflictFrame = BuildResultFrame(boot, 4242UL, 2, Encoding.UTF8.GetBytes("summary-bytes"));
                SendBytes(controlSocket, conflictFrame, conflictFrame.Length);
                Check(PumpUntil(host, () => coordinator.Counters.ResultConflicts >= 1), "同 ID 不同内容被拒（冲突）");
                Check(gateway.Results.Count == 1, "冲突结果不产生第二条通知");
                CheckRejected(resultEvents == 1, "冲突结果不触发新的公开事件");
                Check(gateway.Results[0].WinnerTeamId == 1, "已接受结果未被覆盖");
                Check(gateway.Results[0].Summary.Length == "summary-bytes".Length, "结果摘要按字节保留");

                // 收尾：把 Ack 重发预算走完 → ResultCommitted，再让进程退出 → 释放。
                for (int i = 0; i < 8; i++)
                {
                    clock.Ms += 1500;
                    host.PumpOnce();
                    if (coordinator.State == PMDsSessionState.ResultCommitted)
                    {
                        break;
                    }
                }

                Check(coordinator.State == PMDsSessionState.ResultCommitted,
                    "Ack 重发预算用尽 → ResultCommitted（本地受理，不代表对端确认）");

                launcher.Processes[0].Stop(0);
                Check(PumpUntil(host, () => host.SessionCount == 0), "确认退出后会话被回收");
                Check(host.PortPool.InUseCount == 0, "端口已归还");
                Check(host.PlayerLedger.Count == 0, "uid 占用已释放");
                Check(gateway.Restored.Count == 2, "参战玩家恢复在线（两人）");
                Check(gateway.RestoredWhileProcessAlive == false, "恢复在线发生在确认进程退出**之后**");
                Check(host.GetCoordinator("m-result") == null, "会话已从宿主视图移除");
            }
            finally
            {
                CloseQuietly(controlSocket);
                host.Dispose();
            }
        }

        // ────────────────────────────────────────────────────────────────
        // G. 客户端断线中止 + 延迟退出占用
        // ────────────────────────────────────────────────────────────────

        private static void TestClientDisconnectAndDelayedExit()
        {
            FakeClock clock = new FakeClock();
            FakeLauncher launcher = new FakeLauncher();
            launcher.KillStopsProcess = false; // 模拟「强杀失败/进程赖着不走」
            FakeGateway gateway = new FakeGateway();
            gateway.Online.Add(91);
            gateway.Online.Add(92);
            gateway.ProcessAliveProbe = () => launcher.Processes.Count > 0 && launcher.Processes[0].IsRunning;

            PMDsLobbyHost host = CreateHost(clock, launcher, gateway, null);
            Socket controlSocket = null;

            try
            {
                Check(host.TryStartMatch(MakeRequest("m-abort", 91, 92)).IsQueued, "开局已入队");
                Check(PumpUntil(host, () => host.StartedSessions.Length == 1), "开局已落地");

                PMDsLobbySessionRecord record = host.StartedSessions[0];
                PMDsCoordinator coordinator = host.GetCoordinator("m-abort");
                string decodeError;
                PMDsBootstrappedMatch boot = ReadBootstrap(
                    ArgValue(launcher.Requests[0].Arguments, "-bootstrap"), out decodeError);
                if (boot == null)
                {
                    Check(false, "引导文件不可解码：" + decodeError);
                    return;
                }

                controlSocket = Connect(host.ControlPort);
                SendBytes(controlSocket, BuildReadyFrame(boot, record.Port, boot.CollisionDigest, true), 48);
                Check(PumpUntil(host, () => coordinator.State == PMDsSessionState.Running), "会话就绪并 Running");
                Check(gateway.Offers.Count == 2, "两名玩家已收到 offer");

                // 客户端断线 ⇒ 中止该测试局，不伪造胜利。
                host.NotifyClientDisconnected(91);
                Check(PumpUntil(host, () => host.ClientDisconnectAborts == 1), "断线中止被处理");
                host.PumpOnce();
                CheckRejected(gateway.Results.Count == 0, "**没有**伪造正常胜利（无 Result 通知）");
                Check(coordinator.State != PMDsSessionState.ResultPending
                    && coordinator.State != PMDsSessionState.ResultCommitted,
                    "断线局没有进入结果状态：" + coordinator.State);

                // 宽限期到期 → 请求强杀，但进程故意不退出。
                clock.Ms += 11000;
                Check(PumpUntil(host, () => launcher.Processes[0].KillCount >= 1), "宽限期到期请求强杀");
                Check(launcher.Processes[0].IsRunning, "受控进程仍在运行（模拟僵死）");
                Check(host.PortPool.InUseCount == 1, "进程未退出时端口仍被占用（延迟收尾）");
                Check(host.PlayerLedger.IsOccupied(91) && host.PlayerLedger.IsOccupied(92),
                    "进程未退出时 uid 仍被占用");
                Check(host.SessionCount == 1, "会话尚未回收");
                CheckRejected(gateway.Restored.Count == 0, "进程未退出时不恢复 PlayerOnline");

                // 进程真正退出后才能释放。
                launcher.Processes[0].Stop(0);
                Check(PumpUntil(host, () => host.SessionCount == 0), "确认退出后会话回收");
                Check(host.PortPool.InUseCount == 0, "端口归还");
                Check(host.PlayerLedger.Count == 0, "uid 释放");
                Check(gateway.Restored.Count == 2, "恢复两名玩家在线");
                Check(gateway.RestoredWhileProcessAlive == false, "恢复只在确认退出之后发生");
                Check(coordinator.IsTerminal, "会话进入终态");
                Check(coordinator.LastReason != null && coordinator.LastReason.IndexOf("关闭",
                    StringComparison.Ordinal) >= 0, "终态原因记录了关闭语义");
            }
            finally
            {
                CloseQuietly(controlSocket);
                host.Dispose();
            }

            // 直接验证协调器层：终态但进程未退出 ⇒ 端口与 uid 都不得释放。
            FakeClock clock2 = new FakeClock();
            FakeLauncher launcher2 = new FakeLauncher();
            launcher2.KillStopsProcess = false;
            PMDsPortPool pool = new PMDsPortPool(7970, 7975);
            PMDsPlayerLedger ledger = new PMDsPlayerLedger();
            PMDsCoordinator stuck = new PMDsCoordinator(MakeCoordinatorOptions(), clock2, launcher2,
                PMDsNullCoordinatorSink.Instance, pool, ledger);
            PMDsCoordinatorReply allocated = stuck.Allocate(MakeAllocation("m-stuck", "ds-stuck", 9u, 99));
            Check(allocated.IsAccepted, "僵死用例分配成功");
            Check(stuck.SetLaunchRequest(MakeLaunch("m-stuck", "ds-stuck", stuck.AllocatedPort,
                Path.Combine(_root, "x.bin"), "127.0.0.1:7800")).IsAccepted, "僵死用例参数校验通过");
            Check(stuck.BeginStart().IsAccepted, "僵死用例已启动");
            stuck.Dispose();
            Check(stuck.State == PMDsSessionState.Exited, "终态已确定");
            Check(stuck.IsAwaitingProcessExit, "终态但进程未退出 ⇒ 等待收尾");
            Check(pool.InUseCount == 1 && ledger.IsOccupied(99), "端口与 uid 均**未**提前释放");
            launcher2.Processes[0].Stop(0);
            stuck.Tick();
            Check(pool.InUseCount == 0 && ledger.Count == 0, "确认退出后才释放端口与 uid");
            Check(!stuck.ResourcesHeld, "资源已释放");
        }

        // ────────────────────────────────────────────────────────────────
        // H. listener 分帧与有界
        // ────────────────────────────────────────────────────────────────

        private static void TestListenerFramingAndBounds()
        {
            PMDsControlListener listener = new PMDsControlListener("127.0.0.1", 0);
            string error;
            Check(listener.Start(out error), "loopback listener 启动：" + error);
            Check(listener.BoundPort > 0, "拿到真实绑定端口");

            try
            {
                // H1 半包：一条载荷拆成 3 次写入。
                byte[] payload = new byte[40];
                for (int i = 0; i < payload.Length; i++)
                {
                    payload[i] = (byte)i;
                }

                Socket halfSocket = Connect(listener.BoundPort);
                try
                {
                    SendBytes(halfSocket, PMDsControlFraming.Frame(payload), 13);
                    PMDsControlInbound inbound;
                    Check(TryDequeueInbound(listener, 2000, out inbound), "半包最终被解出");
                    Check(inbound.Length == payload.Length && BytesEqual(inbound.Payload, payload),
                        "半包还原出的载荷逐字节一致");
                }
                finally
                {
                    CloseQuietly(halfSocket);
                }

                // H2 粘包：两条帧一次写入。
                byte[] first = Encoding.UTF8.GetBytes("first-frame");
                byte[] second = Encoding.UTF8.GetBytes("second-frame-longer");
                byte[] glued = Concat(PMDsControlFraming.Frame(first), PMDsControlFraming.Frame(second));
                Socket gluedSocket = Connect(listener.BoundPort);
                try
                {
                    SendBytes(gluedSocket, glued, glued.Length);
                    PMDsControlInbound a;
                    PMDsControlInbound b;
                    Check(TryDequeueInbound(listener, 2000, out a), "粘包第一帧解出");
                    Check(TryDequeueInbound(listener, 2000, out b), "粘包第二帧解出");
                    Check(a.Length == first.Length && BytesEqual(a.Payload, first), "第一帧内容正确");
                    Check(b.Length == second.Length && BytesEqual(b.Payload, second), "第二帧内容正确且保序");
                }
                finally
                {
                    CloseQuietly(gluedSocket);
                }

                // H3 超限：长度前缀 0x7FFFFFFF → 立即断开。
                long framingBefore = listener.RejectedFramingFault;
                Socket oversizeSocket = Connect(listener.BoundPort);
                try
                {
                    byte[] evil = new byte[] { 0xFF, 0xFF, 0xFF, 0x7F, 0x41 };
                    SendBytes(oversizeSocket, evil, evil.Length);
                    Check(WaitFor(delegate { return listener.RejectedFramingFault > framingBefore; }, 2000),
                        "超限长度前缀导致连接被立即拒绝");
                    Check(!TryReadPayload(oversizeSocket, 400, out byte[] _), "超限连接已关闭（不等 64KiB+ 到达）");
                }
                finally
                {
                    CloseQuietly(oversizeSocket);
                }

                // H4 有界：连接上限。
                PMDsControlListener tiny = new PMDsControlListener("127.0.0.1", 0, 1, 16, 4096, 4, 4096);
                Check(tiny.Start(out error), "限量 listener 启动：" + error);
                Socket keep = null;
                Socket extra = null;
                try
                {
                    keep = Connect(tiny.BoundPort);
                    Check(WaitFor(delegate { return tiny.ConnectionCount == 1; }, 2000), "第一条连接被接受");
                    extra = Connect(tiny.BoundPort);
                    Check(WaitFor(delegate { return tiny.RejectedOverConnectionLimit >= 1; }, 2000),
                        "超过连接上限被拒绝");
                }
                finally
                {
                    CloseQuietly(keep);
                    CloseQuietly(extra);
                    tiny.Dispose();
                }

                // H5 有界：入站队列条数上限（不 drain，故意灌 3 帧）。
                PMDsControlListener bounded = new PMDsControlListener("127.0.0.1", 0, 4, 1, 65536, 4, 65536);
                Check(bounded.Start(out error), "有界 listener 启动：" + error);
                Socket flood = null;
                try
                {
                    flood = Connect(bounded.BoundPort);
                    byte[] frame = PMDsControlFraming.Frame(Encoding.UTF8.GetBytes("x"));
                    SendBytes(flood, Concat(Concat(frame, frame), frame), frame.Length * 3);
                    Check(WaitFor(delegate { return bounded.RejectedOverInboundQueue >= 1; }, 2000),
                        "入站队列超限时显式失败（不是静默丢帧）");
                    Check(!TryReadPayload(flood, 800, out byte[] _), "入站队列溢出的连接被关闭");
                    Check(bounded.QueuedInboundCount <= 1, "队列占用仍在界内");
                }
                finally
                {
                    CloseQuietly(flood);
                    bounded.Dispose();
                }

                // H6 非 loopback 地址必须拒绝启动。
                PMDsControlListener nonLoopback = new PMDsControlListener("0.0.0.0", 0);
                string nonLoopbackError;
                CheckRejected(!nonLoopback.Start(out nonLoopbackError), "非 loopback 地址被拒绝启动");
                Check(nonLoopbackError != null && nonLoopbackError.IndexOf("loopback",
                    StringComparison.OrdinalIgnoreCase) >= 0, "拒绝原因明确提到 loopback：" + nonLoopbackError);
                nonLoopback.Dispose();

                // H7 未知连接 ID 的发送必须显式失败。
                CheckRejected(!listener.TrySendFrame(999999, payload, 0, payload.Length), "未知连接 ID 的发送返回 false");
                Check(listener.PeakQueuedInboundBytes <= PMDsControlListener.DefaultMaxQueuedInboundBytes,
                    "入站队列字节峰值在界内");
            }
            finally
            {
                listener.Dispose();
            }
        }

        // ────────────────────────────────────────────────────────────────
        // I. 账本单元语义
        // ────────────────────────────────────────────────────────────────

        private static void TestLedgerUnitSemantics()
        {
            PMDsPlayerLedger ledger = new PMDsPlayerLedger();
            string reason;

            Check(ledger.TryReserve("m-1", Roster(1, 2, 3), out reason), "整局原子预留成功");
            Check(ledger.Count == 3 && ledger.MatchCount == 1, "账本计数正确");
            Check(ledger.IsOccupied(2), "成员 uid 被占用");
            Check(!ledger.TryReserve("m-1", Roster(4), out reason), "同 matchId 重复预留被拒：" + reason);
            Check(!ledger.TryReserve("m-2", Roster(3, 4), out reason), "跨局 uid 冲突被拒：" + reason);
            Check(!ledger.IsOccupied(4), "被拒预留未写入任何 uid");
            Check(!ledger.TryReserve("m-3", Roster(5, 5), out reason), "名册内 uid 重复被拒：" + reason);
            Check(!ledger.TryReserve("m-4", Roster(0), out reason), "uid <= 0 被拒：" + reason);
            Check(!ledger.TryReserve("m-5", Roster(6, 7, 8, 9, 10, 11, 12), out reason),
                "超过 6 人被拒：" + reason);
            Check(!ledger.TryReserve(string.Empty, Roster(13), out reason), "空 matchId 被拒");
            Check(!ledger.TryReserve("m-6", new PMDsRosterIdentity[0], out reason), "空名册被拒");

            string owner;
            Check(ledger.TryGetMatchId(1, out owner) && owner == "m-1", "可按 uid 查到占用者");
            Check(ledger.Release("m-1"), "释放成功");
            Check(ledger.Count == 0 && ledger.MatchCount == 0, "释放后账本清空");
            Check(!ledger.Release("m-1"), "重复释放返回 false（幂等）");
            Check(!ledger.IsOccupied(1), "释放后可再次占用");
            Check(ledger.TryReserve("m-7", Roster(1), out reason), "释放后重新预留成功");

            ledger.ReleaseAll();
            Check(ledger.Count == 0, "ReleaseAll 清空");
        }

        // ────────────────────────────────────────────────────────────────
        // J. 宿主 offer 字段 ⇄ 冻结 PMNet.Session.PMDsEntryCodec 往返
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 用网络组交付的**真实** <c>PMDsEntryOffer</c>/<c>PMDsEntryCodec</c>（不在测试里另写替身）
        /// 把宿主产出的 offer 字段编码并严格解码回来，逐字段比对。
        /// 这一段复刻的是 Server 侧生产网关的字段映射，因此它同时钉住了：
        /// 「Lobby 产出的 Port/Epoch/Hash/Digest/Identity/Ticket 确实能被冻结 codec 接受」。
        /// </summary>
        private static void TestFrozenEntryOfferRoundTrip()
        {
            FakeClock clock = new FakeClock();
            FakeLauncher launcher = new FakeLauncher();
            FakeGateway gateway = new FakeGateway();
            gateway.Online.Add(101);
            gateway.Online.Add(102);

            PMDsLobbyHost host = CreateHost(clock, launcher, gateway, null);
            Socket controlSocket = null;

            try
            {
                Check(host.TryStartMatch(MakeRequest("m-offer", 101, 102)).IsQueued, "开局已入队");
                Check(PumpUntil(host, () => host.StartedSessions.Length == 1), "开局已落地");

                PMDsLobbySessionRecord record = host.StartedSessions[0];
                PMDsCoordinator coordinator = host.GetCoordinator("m-offer");
                string decodeError;
                PMDsBootstrappedMatch boot = ReadBootstrap(
                    ArgValue(launcher.Requests[0].Arguments, "-bootstrap"), out decodeError);
                if (boot == null)
                {
                    Check(false, "引导文件不可解码：" + decodeError);
                    return;
                }

                controlSocket = Connect(host.ControlPort);
                SendBytes(controlSocket, BuildReadyFrame(boot, record.Port, boot.CollisionDigest, true), 64);
                Check(PumpUntil(host, () => coordinator.State == PMDsSessionState.Running), "会话就绪并 Running");
                Check(gateway.Offers.Count == 2, "拿到两条 offer 通知");
                if (gateway.Offers.Count == 0)
                {
                    return;
                }

                PMDsLobbyEntryNotice notice = gateway.Offers[0];
                Check(notice.Ticket.Length <= PMNet.Session.PMDsEntryCodec.MaxTicketBytes,
                    "宿主签发的票据长度在冻结 wire 上限（" + PMNet.Session.PMDsEntryCodec.MaxTicketBytes + "）之内");

                // 与 Server 侧生产网关完全一致的字段映射。
                PMNet.Session.PMDsEntryOffer offer = new PMNet.Session.PMDsEntryOffer();
                offer.MatchId = notice.MatchId;
                offer.DsId = notice.DsId;
                offer.Host = notice.Host;
                offer.Epoch = notice.Epoch;
                offer.ProtocolHash = notice.ProtocolHash;
                offer.CollisionDigest = notice.CollisionDigest;
                offer.Port = notice.Port;
                offer.Identity = notice.Identity;
                offer.Ticket = notice.Ticket;

                string text;
                try
                {
                    text = PMNet.Session.PMDsEntryCodec.Encode(offer);
                }
                catch (Exception ex)
                {
                    Check(false, "冻结 codec 拒绝宿主字段：" + ex.GetType().Name + " " + ex.Message);
                    return;
                }

                Check(PMNet.Session.PMDsEntryCodec.HasPrefix(text), "编码结果带 PMDS1: 前缀");
                Check(text.Length <= PMNet.Session.PMDsEntryCodec.MaxTextBytes,
                    "整条文本 <= " + PMNet.Session.PMDsEntryCodec.MaxTextBytes + " 字节");
                Check(text.StartsWith(PMNet.Session.PMDsEntryCodec.Prefix, StringComparison.Ordinal),
                    "前缀严格匹配（客户端识别用）");

                PMNet.Session.PMDsEntryOffer decoded;
                string error;
                Check(PMNet.Session.PMDsEntryCodec.TryDecode(text, out decoded, out error),
                    "可被冻结 codec 严格解码：" + error);
                if (decoded == null)
                {
                    return;
                }

                Check(decoded.MatchId == notice.MatchId, "往返 MatchId 一致");
                Check(decoded.DsId == notice.DsId, "往返 DsId 一致");
                Check(decoded.Host == notice.Host, "往返 Host 一致");
                Check(decoded.Port == notice.Port && decoded.Port == record.Port,
                    "往返 Port 一致且等于真实分配端口");
                Check(decoded.Epoch == notice.Epoch, "往返 Epoch 一致");
                Check(decoded.ProtocolHash == notice.ProtocolHash, "往返 ProtocolHash 一致");
                Check(decoded.CollisionDigest == notice.CollisionDigest, "往返 CollisionDigest 一致");
                Check(decoded.Identity == notice.Identity, "往返 Identity 一致（所有权只来自票据名册）");
                Check(decoded.Ticket != null && BytesEqual(decoded.Ticket, notice.Ticket),
                    "往返票据字节逐字节保真（客户端据此连 DS）");
            }
            finally
            {
                CloseQuietly(controlSocket);
                host.Dispose();
            }
        }

        // ────────────────────────────────────────────────────────────────
        // 测试骨架
        // ────────────────────────────────────────────────────────────────

        private static void Section(string name, Action body)
        {
            _tag = name.Substring(0, name.IndexOf('.'));
            Console.WriteLine("── " + name);
            try
            {
                body();
            }
            catch (Exception ex)
            {
                Check(false, "该节抛出未捕获异常：" + ex.GetType().Name + " " + ex.Message);
            }

            Console.WriteLine();
        }

        private static void Check(bool ok, string label)
        {
            int total;
            _tagTotal.TryGetValue(_tag, out total);
            _tagTotal[_tag] = total + 1;

            if (ok)
            {
                _passed++;
                Console.WriteLine("    [ok]  " + label);
                return;
            }

            _failures.Add(_tag + " " + label);
            Console.WriteLine("    [FAIL] " + label);
        }

        /// <summary>负向断言：期望输入被拒绝/不产生副作用。</summary>
        private static void CheckRejected(bool rejected, string label)
        {
            if (rejected)
            {
                _negativePassed++;
            }

            Check(rejected, label);
        }

        /// <summary>等值断言（带实际/期望回显，便于失败时定位）。与 PMDsControlTest 同口径。</summary>
        private static void CheckEq(object actual, object expected, string label)
        {
            Check(Equals(actual, expected),
                label + "（实际 " + (actual == null ? "<null>" : actual.ToString())
                + "，期望 " + (expected == null ? "<null>" : expected.ToString()) + "）");
        }

        // ────────────────────────────────────────────────────────────────
        // 测试替身
        // ────────────────────────────────────────────────────────────────

        private sealed class FakeClock : IPMDsClock
        {
            public long Ms = 1700000000000L;

            public long UtcNowUnixMilliseconds { get { return Ms; } }
        }

        private sealed class FakeProcess : IPMDsProcess
        {
            private volatile bool _running = true;

            public int ProcessId { get; set; }
            public bool KillStopsProcess;
            public int KillCount;
            public int WaitForExitCount;
            public int ExitCodeValue;

            public event Action<int> Exited;

            public bool IsRunning { get { return _running; } }

            public bool TryGetExitCode(out int exitCode)
            {
                exitCode = ExitCodeValue;
                return !_running;
            }

            public bool WaitForExit(int timeoutMilliseconds)
            {
                WaitForExitCount++;
                return !_running;
            }

            public long StdOutTotalBytes { get { return 0; } }
            public long StdErrTotalBytes { get { return 0; } }
            public string StdOutTail { get { return string.Empty; } }
            public string StdErrTail { get { return string.Empty; } }

            public void RequestGracefulExit()
            {
            }

            public void Kill()
            {
                KillCount++;
                if (KillStopsProcess)
                {
                    Stop(ExitCodeValue);
                }
            }

            public void Dispose()
            {
            }

            public void Stop(int exitCode)
            {
                if (!_running)
                {
                    return;
                }

                _running = false;
                ExitCodeValue = exitCode;
                Action<int> handler = Exited;
                if (handler != null)
                {
                    handler(exitCode);
                }
            }
        }

        private sealed class FakeLauncher : IPMDsProcessLauncher
        {
            public readonly List<PMDsProcessLaunchRequest> Requests = new List<PMDsProcessLaunchRequest>();
            public readonly List<FakeProcess> Processes = new List<FakeProcess>();
            public bool FailStart;
            public bool KillStopsProcess;

            public bool TryStart(PMDsProcessLaunchRequest request, out IPMDsProcess process,
                out PMDsProcessFault fault, out string detail)
            {
                Requests.Add(request == null ? null : request.Clone());
                if (FailStart)
                {
                    process = null;
                    fault = PMDsProcessFault.LaunchFailed;
                    detail = "注入的启动失败";
                    return false;
                }

                FakeProcess created = new FakeProcess();
                created.ProcessId = 4000 + Processes.Count;
                created.KillStopsProcess = KillStopsProcess;
                Processes.Add(created);
                process = created;
                fault = PMDsProcessFault.None;
                detail = null;
                return true;
            }
        }

        private sealed class FakeGateway : IPMDsLobbyClientGateway
        {
            public readonly HashSet<int> Online = new HashSet<int>();
            public readonly List<PMDsLobbyEntryNotice> Offers = new List<PMDsLobbyEntryNotice>();
            public readonly List<PMDsLobbyResultNotice> Results = new List<PMDsLobbyResultNotice>();
            public readonly List<int> Restored = new List<int>();
            public bool FailOffer;
            public bool RestoredWhileProcessAlive;
            public Func<bool> ProcessAliveProbe;

            public bool IsClientAuthenticated(int uid)
            {
                return Online.Contains(uid);
            }

            public bool TrySendEntryOffer(PMDsLobbyEntryNotice notice, out string error)
            {
                if (FailOffer)
                {
                    error = "注入的 offer 发送失败";
                    return false;
                }

                if (!Online.Contains(notice.Identity.Uid))
                {
                    error = "客户端不在线";
                    return false;
                }

                PMDsLobbyEntryNotice copy = new PMDsLobbyEntryNotice();
                copy.MatchId = notice.MatchId;
                copy.DsId = notice.DsId;
                copy.Host = notice.Host;
                copy.Port = notice.Port;
                copy.Epoch = notice.Epoch;
                copy.ProtocolHash = notice.ProtocolHash;
                copy.CollisionDigest = notice.CollisionDigest;
                copy.Identity = notice.Identity;
                copy.Ticket = (byte[])notice.Ticket.Clone();
                Offers.Add(copy);
                error = null;
                return true;
            }

            public void NotifyMatchEnded(PMDsLobbyResultNotice notice)
            {
                Results.Add(notice);
            }

            public void RestorePlayerOnline(int uid)
            {
                if (ProcessAliveProbe != null && ProcessAliveProbe())
                {
                    RestoredWhileProcessAlive = true;
                }

                Restored.Add(uid);
            }
        }

        // ────────────────────────────────────────────────────────────────
        // 构造与工具
        // ────────────────────────────────────────────────────────────────

        private const uint hostOptionsProtocolHash = 0x5233AA01u;
        private const uint hostOptionsCollisionDigest = 0x52334201u;

        private static PMDsLobbyHost CreateHost(FakeClock clock, FakeLauncher launcher, FakeGateway gateway,
            Action<PMDsLobbyHostOptions> tweak)
        {
            PMDsLobbyHostOptions options = new PMDsLobbyHostOptions();
            options.Enabled = true;
            options.ListenAddress = "127.0.0.1";
            options.ControlPort = 0;
            options.DsExecutablePath = Path.Combine(_root, "HyldDS.exe");
            options.DsWorkingDirectory = _root;
            options.BootstrapRootDirectory = Path.Combine(_root, "boot");
            options.PortRangeFirst = 7801;
            options.PortRangeLast = 7899;
            options.ProtocolHash = hostOptionsProtocolHash;
            options.CollisionDigest = hostOptionsCollisionDigest;
            options.RunBackgroundPumpThread = false;
            if (tweak != null)
            {
                tweak(options);
            }

            PMDsLobbyHost host = new PMDsLobbyHost(options, clock, launcher, gateway);
            string error;
            if (!host.Start(out error))
            {
                throw new InvalidOperationException("宿主启动失败：" + error);
            }

            return host;
        }

        private static PMDsCoordinatorOptions MakeCoordinatorOptions()
        {
            PMDsCoordinatorOptions options = new PMDsCoordinatorOptions();
            options.PortRangeFirst = 7801;
            options.PortRangeLast = 7899;
            options.ProtocolHash = hostOptionsProtocolHash;
            return options;
        }

        private static PMDsLobbyMatchRequest MakeRequest(string matchId, params int[] uids)
        {
            PMDsLobbyMatchRequest request = new PMDsLobbyMatchRequest();
            request.MatchId = matchId;
            request.Roster = Roster(uids);
            request.FightPattern = "test";
            request.CollisionDigest = hostOptionsCollisionDigest;
            return request;
        }

        private static PMDsRosterIdentity[] Roster(params int[] uids)
        {
            PMDsRosterIdentity[] roster = new PMDsRosterIdentity[uids.Length];
            for (int i = 0; i < uids.Length; i++)
            {
                roster[i] = new PMDsRosterIdentity(uids[i], i + 1, i % 2, 3);
            }

            return roster;
        }

        private static PMDsAllocationRequest MakeAllocation(string matchId, string dsId, uint epoch,
            params int[] uids)
        {
            return MakeAllocation(matchId, dsId, epoch, string.Empty, uids);
        }

        private static PMDsAllocationRequest MakeAllocation(string matchId, string dsId, uint epoch,
            string bootstrapPath, params int[] uids)
        {
            PMDsAllocationRequest request = new PMDsAllocationRequest();
            request.MatchId = matchId;
            request.DsId = dsId;
            request.Epoch = epoch;
            request.ProtocolHash = hostOptionsProtocolHash;
            request.CollisionDigest = hostOptionsCollisionDigest;
            request.Roster = Roster(uids);
            request.BootstrapFilePath = bootstrapPath;
            request.Process = new PMDsProcessLaunchRequest(
                Path.Combine(_root, "HyldDS.exe"), _root, "-server", "-dsid", dsId, "-matchid", matchId);
            return request;
        }

        private static PMDsProcessLaunchRequest MakeLaunch(string matchId, string dsId, int port,
            string bootstrapPath, string control)
        {
            return new PMDsProcessLaunchRequest(Path.Combine(_root, "HyldDS.exe"), _root,
                "-server", "-dsid", dsId, "-matchid", matchId,
                "-bootstrap", bootstrapPath, "-control", control, "-port", port.ToString());
        }

        private static PMDsProcessLaunchRequest MakeLaunchWithoutBootstrap(string matchId, string dsId,
            int port, string control)
        {
            return new PMDsProcessLaunchRequest(Path.Combine(_root, "HyldDS.exe"), _root,
                "-server", "-dsid", dsId, "-matchid", matchId, "-control", control, "-port", port.ToString());
        }

        private static PMDsProcessLaunchRequest MakeLaunchWithDuplicatePort(string matchId, string dsId,
            int port, string bootstrapPath)
        {
            return new PMDsProcessLaunchRequest(Path.Combine(_root, "HyldDS.exe"), _root,
                "-server", "-dsid", dsId, "-matchid", matchId, "-bootstrap", bootstrapPath,
                "-control", "127.0.0.1:7800", "-port", port.ToString(), "-port", port.ToString());
        }

        private static string ArgValue(string[] args, string key)
        {
            if (args == null)
            {
                return null;
            }

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], key, StringComparison.Ordinal) && i + 1 < args.Length)
                {
                    return args[i + 1];
                }

                string prefix = key + "=";
                if (args[i] != null && args[i].StartsWith(prefix, StringComparison.Ordinal))
                {
                    return args[i].Substring(prefix.Length);
                }
            }

            return null;
        }

        private static int CountKey(string[] args, string key)
        {
            int count = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], key, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        // ────────────────────────────────────────────────────────────────
        // K. 认证正常退出的优雅退出宽限（宿主链路 + 真实 loopback TCP）
        //
        // 背景与 PMDsControlTest 的 N 节同一根因：真实 DS 先发经 MAC 认证的显式 Exited(0)，
        // 再走 Unity Application.Quit（收尾不是瞬时的）。旧实现在进入终态时无条件强杀，
        // 把这段收尾窗口打成 OS exit -1（而 DS 自己声明的是 exitCode=0）。
        // 这里在真实 PMDsLobbyHost + 真实 loopback TCP 上验证：
        //   ① 宽限内不强杀、不释放端口/uid；
        //   ② 进程在宽限内自行退出后才回收（全程零强杀）；
        //   ③ 宽限到期才转异常收尾强杀；强杀失败（进程赖着不走）继续占用资源。
        // ────────────────────────────────────────────────────────────────

        private static void TestGracefulExitGraceThroughHost()
        {
            // ── K1：宽限内不请求强杀，进程自行退出后回收 ──
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();   // 默认 KillStopsProcess=false：强杀不会让替身退出
                FakeGateway gateway = new FakeGateway();
                gateway.Online.Add(71);
                gateway.Online.Add(72);
                gateway.ProcessAliveProbe = () => launcher.Processes.Count > 0 && launcher.Processes[0].IsRunning;

                PMDsLobbyHost host = CreateHost(clock, launcher, gateway, null);
                Socket controlSocket = null;
                int released = 0;
                host.SessionReleased += delegate(string matchId, PMDsSessionState state, string reason)
                {
                    released++;
                };

                try
                {
                    Check(host.TryStartMatch(MakeRequest("m-grace", 71, 72)).IsQueued, "K1 开局已入队");
                    Check(PumpUntil(host, () => host.StartedSessions.Length == 1), "K2 开局已落地");

                    PMDsLobbySessionRecord record = host.StartedSessions[0];
                    PMDsCoordinator coordinator = host.GetCoordinator("m-grace");
                    string decodeError;
                    PMDsBootstrappedMatch boot = ReadBootstrap(
                        ArgValue(launcher.Requests[0].Arguments, "-bootstrap"), out decodeError);
                    if (boot == null)
                    {
                        Check(false, "引导文件不可解码：" + decodeError);
                        return;
                    }

                    controlSocket = Connect(host.ControlPort);
                    SendBytes(controlSocket, BuildReadyFrame(boot, record.Port, boot.CollisionDigest, true), 64);
                    Check(PumpUntil(host, () => coordinator.State == PMDsSessionState.Running), "K3 会话就绪并 Running");

                    SendBytes(controlSocket,
                        BuildResultFrame(boot, 7777UL, 1, Encoding.UTF8.GetBytes("grace-smoke")), 32);
                    Check(PumpUntil(host, () => coordinator.State == PMDsSessionState.ResultPending),
                        "K4 结果推进到 ResultPending");

                    // DS 收到 ResultAck 后声明 Exited(0)（真实：Unity 还在收尾）。
                    SendBytes(controlSocket, BuildExitedFrame(boot, 0), 16);
                    Check(PumpUntil(host, () => coordinator.State == PMDsSessionState.Exited),
                        "K5 认证显式 Exited(0) ⇒ 终态 Exited");
                    Check(coordinator.IsAwaitingGracefulExit, "K6 处于优雅退出宽限内");
                    Check(coordinator.ResourcesHeld, "K7 宽限内资源仍占用");

                    // 宽限内持续推进宿主（含接近到期）：不得强杀、不得释放。
                    clock.Ms += 4999;
                    for (int i = 0; i < 5; i++)
                    {
                        host.PumpOnce();
                    }

                    CheckEq(launcher.Processes[0].KillCount, 0, "K8 宽限内零强杀请求");
                    CheckEq(host.PortPool.InUseCount, 1, "K9 宽限内端口仍占用");
                    Check(host.PlayerLedger.IsOccupied(71) && host.PlayerLedger.IsOccupied(72),
                        "K10 宽限内 uid 仍占用");
                    CheckEq(host.SessionCount, 1, "K11 宽限内会话未回收");
                    CheckEq(released, 0, "K12 宽限内不触发 SessionReleased");

                    // 进程在宽限内自行退出 ⇒ 正常回收，全程零强杀。
                    launcher.Processes[0].Stop(0);
                    Check(PumpUntil(host, () => host.SessionCount == 0), "K13 宽限内自行退出后会话回收");
                    CheckEq(launcher.Processes[0].KillCount, 0, "K14 整条正常路径零强杀");
                    CheckEq(coordinator.Counters.GracefulExitObserved, 1L, "K15 记录「宽限内自行退出」");
                    CheckEq(coordinator.Counters.GracefulExitTimeouts, 0L, "K16 无宽限超时（不是异常收尾）");
                    CheckEq(host.PortPool.InUseCount, 0, "K17 端口归还");
                    CheckEq(host.PlayerLedger.Count, 0, "K18 uid 释放");
                    CheckEq(released, 1, "K19 SessionReleased 恰好一次");
                    Check(gateway.Restored.Count == 2, "K20 参战玩家恢复在线");
                    Check(gateway.RestoredWhileProcessAlive == false, "K21 恢复在线发生在确认进程退出之后");
                }
                finally
                {
                    CloseQuietly(controlSocket);
                    host.Dispose();
                }
            }

            // ── K2：宽限到期 ⇒ 记录异常收尾并节流强杀；强杀失败（进程赖着不走）继续占用 ──
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();   // KillStopsProcess=false ⇒ 强杀请求发出但进程不退出
                FakeGateway gateway = new FakeGateway();
                gateway.Online.Add(73);
                gateway.Online.Add(74);
                gateway.ProcessAliveProbe = () => launcher.Processes.Count > 0 && launcher.Processes[0].IsRunning;

                PMDsLobbyHost host = CreateHost(clock, launcher, gateway, null);
                Socket controlSocket = null;

                try
                {
                    Check(host.TryStartMatch(MakeRequest("m-grace-timeout", 73, 74)).IsQueued, "K22 开局已入队");
                    Check(PumpUntil(host, () => host.StartedSessions.Length == 1), "K23 开局已落地");

                    PMDsLobbySessionRecord record = host.StartedSessions[0];
                    PMDsCoordinator coordinator = host.GetCoordinator("m-grace-timeout");
                    string decodeError;
                    PMDsBootstrappedMatch boot = ReadBootstrap(
                        ArgValue(launcher.Requests[0].Arguments, "-bootstrap"), out decodeError);
                    if (boot == null)
                    {
                        Check(false, "引导文件不可解码：" + decodeError);
                        return;
                    }

                    controlSocket = Connect(host.ControlPort);
                    SendBytes(controlSocket, BuildReadyFrame(boot, record.Port, boot.CollisionDigest, true), 64);
                    Check(PumpUntil(host, () => coordinator.State == PMDsSessionState.Running), "K24 会话就绪并 Running");

                    SendBytes(controlSocket,
                        BuildResultFrame(boot, 7778UL, 0, Encoding.UTF8.GetBytes("grace-timeout")), 32);
                    Check(PumpUntil(host, () => coordinator.State == PMDsSessionState.ResultPending),
                        "K25 结果推进到 ResultPending");
                    SendBytes(controlSocket, BuildExitedFrame(boot, 0), 16);
                    Check(PumpUntil(host, () => coordinator.State == PMDsSessionState.Exited), "K26 终态 Exited");
                    CheckEq(launcher.Processes[0].KillCount, 0, "K27 前置：宽限内零强杀");

                    clock.Ms += 5001;   // 超过默认 5 秒宽限
                    Check(PumpUntil(host, () => coordinator.Counters.GracefulExitTimeouts >= 1L),
                        "K28 宽限到期被记录为异常收尾");
                    CheckEq(coordinator.Counters.KillRequests, 1L, "K29 到期后才请求强杀");
                    Check(launcher.Processes[0].KillCount >= 1, "K30 替身确实收到了强杀请求");
                    Check(launcher.Processes[0].IsRunning, "K31 强杀未生效：替身进程仍在运行");
                    Check(coordinator.LastReason != null
                        && coordinator.LastReason.IndexOf("异常收尾", StringComparison.Ordinal) >= 0,
                        "K32 结束原因记录异常收尾：" + coordinator.LastReason);
                    CheckEq(host.PortPool.InUseCount, 1, "K33 强杀失败 ⇒ 端口继续占用");
                    Check(host.PlayerLedger.IsOccupied(73) && host.PlayerLedger.IsOccupied(74),
                        "K34 强杀失败 ⇒ uid 继续占用");
                    CheckEq(host.SessionCount, 1, "K35 会话未回收");

                    launcher.Processes[0].Stop(0);
                    Check(PumpUntil(host, () => host.SessionCount == 0), "K36 进程真正退出后才回收");
                    CheckEq(host.PortPool.InUseCount, 0, "K37 端口归还");
                    CheckEq(host.PlayerLedger.Count, 0, "K38 uid 释放");
                }
                finally
                {
                    CloseQuietly(controlSocket);
                    host.Dispose();
                }
            }
        }

        private static byte[] BuildExitedFrame(PMDsBootstrappedMatch boot, int exitCode)
        {
            PMDsExitedBody body = new PMDsExitedBody();
            body.ExitCode = exitCode;
            return SignFrame(boot, PMDsControlMessageType.Exited, body);
        }

        private static byte[] BuildReadyFrame(PMDsBootstrappedMatch boot, int boundPort, uint digest,
            bool sceneReady)
        {
            PMDsReadyBody body = new PMDsReadyBody();
            body.BoundPort = boundPort;
            body.CollisionDigest = digest;
            body.SceneReady = sceneReady;
            return SignFrame(boot, PMDsControlMessageType.Ready, body);
        }

        private static byte[] BuildHeartbeatFrame(PMDsBootstrappedMatch boot)
        {
            PMDsHeartbeatBody body = new PMDsHeartbeatBody();
            body.PlayerCount = boot.PlayerCount;
            return SignFrame(boot, PMDsControlMessageType.Heartbeat, body);
        }

        private static byte[] BuildResultFrame(PMDsBootstrappedMatch boot, ulong resultId, int winner,
            byte[] summary)
        {
            PMDsResultBody body = new PMDsResultBody();
            body.ResultId = resultId;
            body.WinnerTeamId = winner;
            body.Summary = summary;
            return SignFrame(boot, PMDsControlMessageType.Result, body);
        }

        private static byte[] SignFrame(PMDsBootstrappedMatch boot, PMDsControlMessageType type,
            PMDsControlBody body)
        {
            PMDsControlMessage message = PMDsControlMessage.Create(
                type, boot.MatchId, boot.DsId, boot.Epoch, boot.ProtocolHash, 0UL, body);
            PMDsControlSigner signer = boot.Key.CreateControlSigner();
            return PMDsControlFraming.Frame(signer.Sign(message));
        }

        private static PMDsBootstrappedMatch ReadBootstrap(string path, out string error)
        {
            error = null;
            PMDsBootstrappedMatch match;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                if (!PMDsBootstrapDocument.TryDecode(bytes, out match, out error))
                {
                    return null;
                }

                return match;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + " " + ex.Message;
                return null;
            }
        }

        private static Socket Connect(int port)
        {
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(new IPEndPoint(IPAddress.Loopback, port));
            return socket;
        }

        private static void SendBytes(Socket socket, byte[] data, int chunkSize)
        {
            SendBytes(socket, data, 0, data.Length, chunkSize);
        }

        private static void SendBytes(Socket socket, byte[] data, int offset, int count, int chunkSize)
        {
            if (socket == null)
            {
                return;
            }

            if (chunkSize <= 0)
            {
                chunkSize = count;
            }

            int sent = 0;
            while (sent < count)
            {
                int take = Math.Min(chunkSize, count - sent);
                socket.Send(data, offset + sent, take, SocketFlags.None);
                sent += take;
                if (take < count - sent)
                {
                    Thread.Sleep(5);
                }
            }
        }

        /// <summary>
        /// 读到**指定类型**的控制帧为止，其余类型按类型丢弃（有界）。
        ///
        /// 为什么需要：R3-B 双向 liveness 修复之后，Lobby 在合法 Ready 之后会补一条签名 Heartbeat，
        /// 于是「Ready 之后的下一条下行帧」不再必然是 ResultAck。用本方法按消息类型读，
        /// 既不改超时也不放宽任何断言（调用方仍然验签、仍然比类型、仍然比 ResultId 与字节）。
        /// </summary>
        private static bool TryReadPayloadOfType(Socket socket, int timeoutMs, PMDsControlSigner signer,
                                                 PMDsControlMessageType expected, out byte[] payload)
        {
            payload = null;
            for (int i = 0; i < 8; i++)
            {
                byte[] candidate;
                if (!TryReadPayload(socket, timeoutMs, out candidate))
                {
                    return false;
                }

                PMDsControlMessage message;
                PMDsControlVerifyFault fault;
                if (!signer.VerifyRaw(candidate, 0, candidate.Length, out message, out fault))
                {
                    return false;
                }

                if (message.Type == expected)
                {
                    payload = candidate;
                    return true;
                }
            }

            return false;
        }

        private static bool TryReadPayload(Socket socket, int timeoutMs, out byte[] payload)
        {
            payload = null;
            if (socket == null)
            {
                return false;
            }

            int previous = socket.ReceiveTimeout;
            socket.ReceiveTimeout = timeoutMs;
            try
            {
                byte[] head = new byte[4];
                if (!ReadExact(socket, head, 0, 4))
                {
                    return false;
                }

                int length = PMDsControlFraming.ReadLengthPrefix(head, 0);
                if (length <= 0 || length > PMDsControlWire.MaxFramePayloadBytes)
                {
                    return false;
                }

                payload = new byte[length];
                return ReadExact(socket, payload, 0, length);
            }
            finally
            {
                try
                {
                    socket.ReceiveTimeout = previous;
                }
                catch (Exception)
                {
                }
            }
        }

        private static bool ReadExact(Socket socket, byte[] buffer, int offset, int count)
        {
            int got = 0;
            while (got < count)
            {
                int read;
                try
                {
                    read = socket.Receive(buffer, offset + got, count - got, SocketFlags.None);
                }
                catch (SocketException)
                {
                    return false;
                }

                if (read <= 0)
                {
                    return false;
                }

                got += read;
            }

            return true;
        }

        private static bool TryDequeueInbound(PMDsControlListener listener, int timeoutMs,
            out PMDsControlInbound inbound)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (listener.TryDequeueInbound(out inbound))
                {
                    return true;
                }

                Thread.Sleep(2);
            }

            return listener.TryDequeueInbound(out inbound);
        }

        private static bool PumpUntil(PMDsLobbyHost host, Func<bool> condition, int timeoutMs = 3000)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                host.PumpOnce();
                if (condition())
                {
                    return true;
                }

                Thread.Sleep(2);
            }

            host.PumpOnce();
            return condition();
        }

        private static bool WaitFor(Func<bool> condition, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (condition())
                {
                    return true;
                }

                Thread.Sleep(2);
            }

            return condition();
        }

        private static void CloseQuietly(Socket socket)
        {
            if (socket == null)
            {
                return;
            }

            try
            {
                socket.Close();
            }
            catch (Exception)
            {
            }
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static byte[] Concat(byte[] first, byte[] second)
        {
            byte[] result = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, result, 0, first.Length);
            Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
            return result;
        }
    }
}
