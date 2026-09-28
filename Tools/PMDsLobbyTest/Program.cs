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
    /// J 宿主 offer 字段 ⇄ 冻结 PMDsEntryCodec 往返（含续局前缀 PMDSR1）
    /// K 认证正常退出的优雅退出宽限
    /// L T-LOOP4 原局断线续玩：30s 窗口 / 新 Nonce 票 / episode 不滚票 / 失败面 / 只读结果
    /// M T-LOOP1 高危1：PMDS-END1 只读结果通知的共享严格解析（真实生产编码 ⇄ 字段往返 + 畸形样本）
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
                Section("L. T-LOOP4 原局断线续玩：新票签发 / episode 不滚票 / 失败面 / 只读结果",
                    TestResumeEntryLifecycle);
                Section("M. T-LOOP1 高危1：PMDS-END1 只读结果的共享严格解析（真实生产编码 ⇄ 字段往返 + 畸形样本）",
                    TestEndedNoticeStrictCodec);
                Section("N. T-LOOP 二次断线：连接代次区分重复通知与新episode", TestSecondReconnectEpisode);
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

                // T-LOOP二审：旧局结果120s缓存尚在，但同uid已进入**新的Running局**时，
                // 登录响应不能同时展示旧局胜负并推新局offer（新局优先）。
                string oldResult;
                Check(host.TryGetRecentMatchEndedNotice(81, out oldResult),
                    "F-new1 旧局结果在TTL内可供无局登录只读展示");
                CloseQuietly(controlSocket);
                controlSocket = null;
                gateway.ProcessAliveProbe = () => launcher.Processes.Count > 0
                    && launcher.Processes[launcher.Processes.Count - 1].IsRunning;
                Check(host.TryStartMatch(MakeRequest("m-result2", 81, 82)).IsQueued,
                    "F-new2 同uid新局已排队（原局资源已释放）");
                Check(PumpUntil(host, () => host.StartedSessions.Length >= 2
                    && host.StartedSessions[host.StartedSessions.Length - 1].MatchId == "m-result2"),
                    "F-new3 新局已落地（宿主历史已启动列表保留上一局记录）");
                if (host.StartedSessions.Length >= 2 && launcher.Requests.Count > 1)
                {
                    PMDsBootstrappedMatch nextBoot = ReadBootstrap(
                        ArgValue(launcher.Requests[1].Arguments, "-bootstrap"), out decodeError);
                    Check(nextBoot != null, "F-new4 新局引导文件可读：" + decodeError);
                    if (nextBoot != null)
                    {
                        controlSocket = Connect(host.ControlPort);
                        SendBytes(controlSocket, BuildReadyFrame(nextBoot,
                            host.StartedSessions[host.StartedSessions.Length - 1].Port,
                            nextBoot.CollisionDigest, true), 64);
                        Check(PumpUntil(host, () => host.GetCoordinator("m-result2").State == PMDsSessionState.Running),
                            "F-new5 新局Running");
                        CheckRejected(!host.TryGetRecentMatchEndedNotice(81, out oldResult),
                            "F-new6 正在新局时登录不附旧局结果（新局续玩offer优先）");
                    }
                }
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
                Check(!gateway.Offers[0].IsResume && !gateway.Offers[1].IsResume,
                    "初次入局 offer 不是续局（IsResume=false）");

                // T-LOOP4：**非终局 Running 局**下客户端断线不再立即中止，而是进入 30 秒原局续玩窗口
                // （离线角色仍留在权威战场，Lobby 不伪造胜负）；窗口到期仍未连回才走旧断线路径。
                gateway.Online.Remove(91);   // 真实断线：该 uid 的大厅 TCP 已经不在了
                host.NotifyClientDisconnected(91);
                Check(PumpUntil(host, () => host.ClientDisconnectsHeld == 1), "断线进入续玩窗口（不立即中止）");
                host.PumpOnce();
                CheckEq(host.ClientDisconnectAborts, 0L, "窗口内未中止该局");
                CheckEq(coordinator.State, PMDsSessionState.Running, "窗口内会话仍为 Running");
                CheckRejected(gateway.Results.Count == 0, "**没有**伪造正常胜利（无 Result 通知）");
                Check(host.PortPool.InUseCount == 1, "窗口内端口仍被占用");
                Check(host.PlayerLedger.IsOccupied(91) && host.PlayerLedger.IsOccupied(92),
                    "窗口内 uid 仍被占用");

                // 玩家 TCP 与控制通道是两条独立链路：窗口期内必须继续有 Lobby↔DS 心跳，
                // 否则会先撞 15 秒心跳超时（那是另一条失败路径）。
                AdvanceWithHeartbeats(host, controlSocket, boot, coordinator, clock, 30000);
                Check(PumpUntil(host, () => host.ReconnectWindowsExpired >= 1), "续玩窗口到期");
                Check(PumpUntil(host, () => host.ClientDisconnectAborts == 1), "到期仍未连回 ⇒ 走旧断线中止路径");
                host.PumpOnce();
                CheckRejected(gateway.Results.Count == 0, "**没有**伪造正常胜利（无 Result 通知）");
                Check(coordinator.State != PMDsSessionState.ResultPending
                    && coordinator.State != PMDsSessionState.ResultCommitted,
                    "断线局没有进入结果状态：" + coordinator.State);

                // T-LOOP4：已判定中止的局（DS 进程还没退出、State 仍是 Running）绝不允许靠
                // “再来一次断线 + 续局请求”重开 30 秒窗口 —— 否则等于跳过 Forfeit。
                host.NotifyClientDisconnected(91);
                host.PumpOnce();
                CheckEq(host.ClientDisconnectsHeld, 1L, "迟到的重复断线通知不重开窗口（不重复计数）");
                Check(host.TryRequestResumeEntry(91), "G 续局请求已入队（该局已判定中止）");
                Check(PumpUntil(host, () => host.ResumeRejectedSessionAborting == 1),
                    "已判定中止的局不再签发续局票");
                CheckEq(host.LastResumeOutcome, PMDsLobbyResumeOutcome.RejectedSessionAborting,
                    "拒绝原因=该局已被判定中止");
                CheckEq(gateway.Offers.Count, 2, "中止后不再产生 offer");

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

                // T-LOOP1：续局只换**文本前缀**，二进制字段/票据校验原样共用；
                // 实现前这里必须失败：旧 codec 根本不认识 PMDSR1。
                string resumeText = "PMDSR1:" + text.Substring(PMNet.Session.PMDsEntryCodec.Prefix.Length);
                Check(PMNet.Session.PMDsEntryCodec.HasPrefix(resumeText),
                    "J-resume 续局专用前缀被新链识别（不误当旧战斗链）");
                PMNet.Session.PMDsEntryOffer resumed;
                string resumeError;
                Check(PMNet.Session.PMDsEntryCodec.TryDecode(resumeText, out resumed, out resumeError)
                    && resumed != null && resumed.IsResume && BytesEqual(resumed.Ticket, notice.Ticket),
                    "J-resume 同一二进制载荷经续局前缀仍被严格解码并标记续局：" + resumeError);
                Check(!decoded.IsResume, "J-resume 普通 PMDS1 初始 offer 绝不误标续局");
                if (resumed != null)
                {
                    Check(PMNet.Session.PMDsEntryCodec.Encode(resumed) == resumeText,
                        "J-resume 续局编码往返逐字节稳定（不换二进制布局）");
                }
                Check(!PMNet.Session.PMDsEntryCodec.HasPrefix("PMDSR1"),
                    "J-resume 不完整前缀被拒");
            }
            finally
            {
                CloseQuietly(controlSocket);
                host.Dispose();
            }
        }

        private static void TestSecondReconnectEpisode()
        {
            FakeClock clock = new FakeClock();
            FakeLauncher launcher = new FakeLauncher();
            FakeGateway gateway = new FakeGateway();
            gateway.Online.Add(311);
            gateway.Online.Add(312);
            gateway.ProcessAliveProbe = () => launcher.Processes.Count > 0 && launcher.Processes[0].IsRunning;
            PMDsLobbyHost host = CreateHost(clock, launcher, gateway, null);
            Socket controlSocket = null;
            try
            {
                Check(host.TryStartMatch(MakeRequest("m-twice", 311, 312)).IsQueued, "N1 开局排队");
                Check(PumpUntil(host, () => host.StartedSessions.Length == 1), "N2 开局成功");
                PMDsLobbySessionRecord record = host.StartedSessions[0];
                string error;
                PMDsBootstrappedMatch boot = ReadBootstrap(
                    ArgValue(launcher.Requests[0].Arguments, "-bootstrap"), out error);
                if (boot == null) { Check(false, "N3 引导不可读：" + error); return; }
                controlSocket = Connect(host.ControlPort);
                SendBytes(controlSocket, BuildReadyFrame(boot, record.Port, boot.CollisionDigest, true), 64);
                Check(PumpUntil(host, () => host.GetCoordinator("m-twice").State == PMDsSessionState.Running),
                    "N3 进入Running");

                gateway.Online.Remove(311);
                host.NotifyClientDisconnected(311, 101L); // 首次TCP连接代次
                Check(PumpUntil(host, () => host.ClientDisconnectsHeld >= 1), "N4 第一断线开窗");
                host.NotifyClientDisconnected(311, 101L); // 同连接重复Close不得刷新
                host.PumpOnce();
                gateway.Online.Add(311);
                Check(host.TryRequestResumeEntry(311), "N5 首次自动续局请求入队");
                Check(PumpUntil(host, () => gateway.Offers.Count == 3), "N6 首次新票已发");
                if (gateway.Offers.Count < 3) { return; }
                byte[] first = gateway.Offers[2].Ticket;
                long reissues = host.GetCoordinator("m-twice").Counters.TicketsReissued;

                gateway.Online.Remove(311);
                host.NotifyClientDisconnected(311, 102L); // 新TCP连接真实第二次断线
                Check(PumpUntil(host, () => host.ClientDisconnectsHeld >= 3), "N7 第二次真实断线产生新episode");
                gateway.Online.Add(311);
                Check(host.TryRequestResumeEntry(311), "N8 第二次续局请求入队");
                Check(PumpUntil(host, () => gateway.Offers.Count == 4), "N9 第二张票发出");
                Check(gateway.Offers.Count >= 4 && !BytesEqual(first, gateway.Offers[3].Ticket),
                    "N10 二次断线必须是新票字节（旧票已被DS消费/留墓碑）");
                CheckEq(host.GetCoordinator("m-twice").Counters.TicketsReissued, reissues + 1,
                    "N11 新episode只轮转一次Nonce，不因同连接重复Close滚票");
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
                copy.IsResume = notice.IsResume;
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

        // ────────────────────────────────────────────────────────────────
        // L. T-LOOP4：原局断线续玩（新票签发 / episode 幂等 / 失败面 / 只读结果）
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// T-LOOP4 验收：Lobby 侧原局断线续玩 v1（`Docs/plans/net-architecture-migration.md`
        /// 「T-LOOP1 原局断线续玩 v1 冻结接口」）。
        ///
        /// 全程用注入时钟 + 真实 loopback 控制链路 + 受控进程替身，不用 sleep 等真实超时；
        /// 覆盖「正向签发 / 同 episode 不滚票 / 全部失败面 / 终局不可逆 / 只读结果有界」。
        /// </summary>
        private static void TestResumeEntryLifecycle()
        {
            FakeClock clock = new FakeClock();
            FakeLauncher launcher = new FakeLauncher();
            FakeGateway gateway = new FakeGateway();
            gateway.Online.Add(201);
            gateway.Online.Add(202);
            gateway.Online.Add(203);   // 在线但没有任何对局：用于「无局可续」
            gateway.ProcessAliveProbe = () => launcher.Processes.Count > 0 && launcher.Processes[0].IsRunning;

            PMDsLobbyHost host = CreateHost(clock, launcher, gateway, null);
            Socket controlSocket = null;

            try
            {
                Check(host.TryStartMatch(MakeRequest("m-resume", 201, 202)).IsQueued, "L1 开局已入队");
                Check(PumpUntil(host, () => host.StartedSessions.Length == 1), "L2 开局已落地");

                PMDsLobbySessionRecord record = host.StartedSessions[0];
                PMDsCoordinator coordinator = host.GetCoordinator("m-resume");
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
                Check(PumpUntil(host, () => coordinator.State == PMDsSessionState.Running), "L3 会话就绪并 Running");
                CheckEq(gateway.Offers.Count, 2, "L4 两名玩家收到初次入局 offer");
                if (gateway.Offers.Count < 2)
                {
                    return;
                }

                Check(!gateway.Offers[0].IsResume && !gateway.Offers[1].IsResume,
                    "L5 初次入局 offer 的 IsResume=false（PMDS1:）");
                byte[] originalTicket = (byte[])gateway.Offers[0].Ticket.Clone();
                PMDsRosterIdentity identity201 = gateway.Offers[0].Identity;
                CheckEq(identity201.Uid, 201, "L6 初次 offer 的身份来自名册 uid 201");

                // ── 真实断线 ⇒ 进入 30s 窗口，不立即中止 ──
                // 真实顺序：玩家的 TCP 已经掉了（所以此刻该 uid **没有**已认证连接），
                // 之后的重新登录才会让它重新变成已认证。
                gateway.Online.Remove(201);
                host.NotifyClientDisconnected(201);
                Check(PumpUntil(host, () => host.ClientDisconnectsHeld == 1), "L7 断线进入续玩窗口");
                host.PumpOnce();
                CheckEq(host.ClientDisconnectAborts, 0L, "L8 窗口内未中止该局");
                CheckEq(coordinator.State, PMDsSessionState.Running, "L9 窗口内会话仍为 Running");
                Check(host.PortPool.InUseCount == 1 && host.PlayerLedger.IsOccupied(201),
                    "L10 窗口内端口与 uid 仍被占用");

                // ── 未断线的在线 uid 不得取续局票（在线顶号必须 fail-closed）──
                Check(host.TryRequestResumeEntry(202), "L11 续局请求已入队（uid=202）");
                Check(PumpUntil(host, () => host.ResumeRejectedNotDisconnected == 1), "L12 未断线被拒");
                CheckEq(host.LastResumeOutcome, PMDsLobbyResumeOutcome.RejectedNotDisconnected, "L13 拒绝原因=未断线");
                CheckEq(gateway.Offers.Count, 2, "L14 被拒请求不产生任何 offer");

                // ── UID0 / 无局可续 ──
                CheckRejected(!host.TryRequestResumeEntry(0), "L15 uid=0 不入队");
                CheckEq(host.ResumeRejectedUidInvalid, 1L, "L16 UID0 被拒计数");
                Check(host.TryRequestResumeEntry(203), "L17 uid=203 请求已入队");
                Check(PumpUntil(host, () => host.ResumeRejectedNoSession >= 1), "L18 无局可续被拒");
                CheckEq(gateway.Offers.Count, 2, "L19 无局请求不产生 offer");

                // ── 没有已认证连接（密码校验/原子登记未真正落地）不得取票 ──
                Check(host.TryRequestResumeEntry(201), "L20 uid=201 请求已入队（当前无已认证连接）");
                Check(PumpUntil(host, () => host.ResumeRejectedNotAuthenticated == 1), "L21 无已认证连接被拒");
                CheckEq(gateway.Offers.Count, 2, "L22 未认证请求不产生 offer");
                gateway.Online.Add(201);   // 模拟玩家重新登录并原子登记成功

                // ── offer 投递失败 ⇒ 记失败、不中止该局，且已签发的票被缓存（不白轮转）──
                gateway.FailOffer = true;
                Check(host.TryRequestResumeEntry(201), "L23 uid=201 续局请求已入队（注入投递失败）");
                Check(PumpUntil(host, () => host.ResumeOfferSendFailures == 1), "L24 offer 投递失败被记录");
                CheckEq(host.ResumeTicketsIssued, 1L, "L25 首次请求确实重签了一张新票");
                CheckEq(coordinator.Counters.TicketsReissued, 1L, "L26 协调器层只轮转过一次 Nonce");
                CheckEq(coordinator.State, PMDsSessionState.Running, "L27 投递失败不中止该局");
                gateway.FailOffer = false;

                // ── 重试成功 ⇒ 用**同一张**票（不滚票）──
                Check(host.TryRequestResumeEntry(201), "L28 uid=201 重试续局请求已入队");
                Check(PumpUntil(host, () => gateway.Offers.Count == 3), "L29 续局 offer 已送达");
                if (gateway.Offers.Count < 3)
                {
                    // 新用例对旧行为要能干净地“红”，而不是抛下标越界把后面的断语全吞掉。
                    Check(false, "L29b 续局 offer 缺失（实现未按 T-LOOP4 签发），后续断语提前结束");
                    return;
                }

                CheckEq(host.ResumeTicketsReused, 1L, "L30 首次送达属于「复用本 episode 已签发的票」");
                CheckEq(coordinator.Counters.TicketsReissued, 1L, "L31 没有第二次轮转（不滚票）");

                PMDsLobbyEntryNotice resumeNotice = gateway.Offers[2];
                Check(resumeNotice.IsResume, "L32 续局 offer 的 IsResume=true（PMDSR1:）");
                Check(resumeNotice.Identity == identity201, "L33 续局身份与初次入局一致（只来自名册）");
                CheckEq(resumeNotice.MatchId, record.MatchId, "L34 MatchId 不变");
                CheckEq(resumeNotice.Port, record.Port, "L35 端口不变（仍指向同一局）");
                CheckEq(resumeNotice.Epoch, record.Epoch, "L36 Epoch 不变（原局同世代）");
                Check(!BytesEqual(resumeNotice.Ticket, originalTicket),
                    "L37 续局票**不是**旧票字节（换了新 Nonce）");
                CheckEq(resumeNotice.Ticket.Length, originalTicket.Length, "L38 票据长度与既有 codec 一致");

                // 新票必须仍是本局可信票据（DS 按密钥 + 名册验票，不按引导票字节比对）。
                PMDsTicketVerification verified = coordinator.ResolveTicket(resumeNotice.Ticket, clock.Ms / 1000L);
                Check(verified.IsValid, "L39 新票通过本局验签（Verdict=" + verified.Verdict + "）");
                Check(verified.Identity == identity201, "L40 新票绑定的身份与名册一致");

                // ── 同 episode 再请求一次 ⇒ 原样重发同一张票 ──
                Check(host.TryRequestResumeEntry(201), "L41 同 episode 第三次请求已入队");
                Check(PumpUntil(host, () => gateway.Offers.Count == 4), "L42 重复请求仍会重发 offer");
                Check(gateway.Offers.Count == 4 && BytesEqual(gateway.Offers[3].Ticket, resumeNotice.Ticket),
                    "L43 重复请求字节完全相同（不滚票）");
                CheckEq(host.ResumeTicketsReused, 2L, "L44 复用计数 +1");
                CheckEq(host.ResumeTicketsIssued, 1L, "L45 签发（轮转）计数仍为 1");
                CheckEq(coordinator.Counters.TicketsReissued, 1L, "L46 协调器只轮转过一次");

                // ── 冻结 codec 对续局 offer 的严格往返（与生产网关同字段映射）──
                string resumeText = PMNet.Session.PMDsEntryCodec.Encode(ToWireOffer(resumeNotice));
                Check(resumeText.StartsWith(PMNet.Session.PMDsEntryCodec.ResumePrefix, StringComparison.Ordinal),
                    "L47 续局 offer 编码后带 PMDSR1: 前缀（普通 PMDS1 无行为变化）");
                Check(!resumeText.StartsWith(PMNet.Session.PMDsEntryCodec.Prefix, StringComparison.Ordinal),
                    "L48 续局 offer 不再以初始 PMDS1: 开头");
                PMNet.Session.PMDsEntryOffer decodedResume;
                string resumeDecodeError;
                Check(PMNet.Session.PMDsEntryCodec.TryDecode(resumeText, out decodedResume, out resumeDecodeError)
                    && decodedResume != null && decodedResume.IsResume
                    && BytesEqual(decodedResume.Ticket, resumeNotice.Ticket),
                    "L49 续局 offer 被冻结 codec 严格解码并标记 IsResume：" + resumeDecodeError);

                // ── 窗口到期（该 uid 已重新在线）⇒ 不再签发新票，但**不中止**权威局 ──
                AdvanceWithHeartbeats(host, controlSocket, boot, coordinator, clock, 30000);
                Check(PumpUntil(host, () => host.ReconnectWindowsExpired >= 1), "L50 续玩窗口到期");
                CheckEq(host.ClientDisconnectAborts, 0L, "L51 到期时 uid 已在线 ⇒ 不中止该局");
                CheckEq(coordinator.State, PMDsSessionState.Running, "L52 会话仍为 Running");
                int offersBeforeExpiredRequest = gateway.Offers.Count;
                Check(host.TryRequestResumeEntry(201), "L53 过期后请求已入队");
                Check(PumpUntil(host, () => host.ResumeRejectedWindowExpired == 1), "L54 过期后请求被拒");
                CheckEq(host.LastResumeOutcome, PMDsLobbyResumeOutcome.RejectedWindowExpired, "L55 拒绝原因=窗口过期");
                CheckEq(gateway.Offers.Count, offersBeforeExpiredRequest, "L56 过期后不再产生 offer");

                // ── 终局 ⇒ 只读结果缓存 + 续局一律拒绝（不可逆）──
                SendBytes(controlSocket, BuildResultFrame(boot, 9001UL, 1, Encoding.UTF8.GetBytes("resume-smoke")), 32);
                Check(PumpUntil(host, () => coordinator.State == PMDsSessionState.ResultPending),
                    "L57 结果推进到 ResultPending");
                Check(PumpUntil(host, () => host.MatchEndedNoticesRecorded == 2), "L58 两名玩家各写一条只读结果");

                string endedText;
                Check(host.TryGetRecentMatchEndedNotice(201, out endedText), "L59 取得 uid=201 的只读结果通知");
                string expected201 = "PMDS-END1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("m-resume"))
                    + ":1:0";
                CheckEq(endedText, expected201, "L60 通知文本严格等于冻结格式（base64(matchId)/winner/localTeam）");
                string ended202;
                Check(host.TryGetRecentMatchEndedNotice(202, out ended202), "L61 uid=202 也有条目");
                CheckEq(ended202, "PMDS-END1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("m-resume"))
                    + ":1:1", "L62 localTeam 取该 uid 在名册里的真实 TeamId");
                CheckRejected(!host.TryGetRecentMatchEndedNotice(203, out ended202), "L63 非参战 uid 没有结果条目");
                CheckRejected(!host.TryGetRecentMatchEndedNotice(0, out ended202), "L64 uid=0 不返回结果条目");

                int offersBeforeTerminalRequest = gateway.Offers.Count;
                Check(host.TryRequestResumeEntry(201), "L65 终局后请求已入队");
                Check(PumpUntil(host, () => host.ResumeRejectedTerminal == 1), "L66 终局后续局被拒");
                CheckEq(host.LastResumeOutcome, PMDsLobbyResumeOutcome.RejectedTerminal, "L67 拒绝原因=已终局");
                CheckEq(gateway.Offers.Count, offersBeforeTerminalRequest, "L68 终局后不产生 offer（不恢复输入）");

                // ── 只读结果缓存 TTL（默认 120s）有界过期 ──
                AdvanceWithHeartbeats(host, controlSocket, boot, coordinator, clock, 120001);
                string expired;
                CheckRejected(!host.TryGetRecentMatchEndedNotice(201, out expired), "L69 超过 120s 后结果条目过期");
                CheckEq(host.MatchEndedNoticesServed, 2L, "L70 已服务计数只记两次成功返回");
            }
            finally
            {
                CloseQuietly(controlSocket);
                host.Dispose();
            }

            VerifyServerLoginWiring();
        }

        /// <summary>
        /// 与 <c>Server.ServerLobbyClientGateway.TrySendEntryOffer</c> **完全同字段**的映射（含 IsResume）。
        /// 本测试不编 Server 主工程（避免锁运行中的 dll），因此用同一映射验证字段能过冻结 codec。
        /// </summary>
        private static PMNet.Session.PMDsEntryOffer ToWireOffer(PMDsLobbyEntryNotice notice)
        {
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
            offer.IsResume = notice.IsResume;
            return offer;
        }

        // ────────────────────────────────────────────────────────────────
        //  M. T-LOOP1 高危1：PMDS-END1「上局已终局」只读结果通知的共享严格解析
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 红队高危1（主计划末尾「T-LOOP 跨端独立红队复核」第 1 条）的验收：
        /// **真实生产编码**（<c>PMDsLobbyEndedNotice.ToText</c> → Server 的 <c>PMDsLobbyEndedCodec</c>
        /// → 共享 <c>PMNet.Session.PMDsEndedNoticeCodec</c>）⇄ 共享严格解析的字段往返；
        /// 再逐条钉住畸形样本必须被拒。
        ///
        /// 为什么不是「源码正则」验收：这里断言的是**行为**（接受/拒绝 + 解析出的字段值 +
        /// 结论口径 + 只读文案），并且关键负例都与一条「最小修复版」配对
        /// （见 <see cref="CheckMutationRejected"/>），所以「解析器一律拒绝」这种假实现不可能全绿。
        /// </summary>
        private static void TestEndedNoticeStrictCodec()
        {
            // ── 1. 真实生产编码 → 共享解析：字段往返 + 冻结线格式 ──
            PMDsLobbyEndedNotice produced = new PMDsLobbyEndedNotice();
            produced.MatchId = "m-loop-end-1";
            produced.WinnerTeamId = 1;
            produced.LocalTeamId = 0;

            string text = produced.ToText();
            string frozenFormat = "PMDS-END1:"
                + Convert.ToBase64String(Encoding.UTF8.GetBytes("m-loop-end-1")) + ":1:0";
            CheckEq(text, frozenFormat, "M01 生产编码严格等于冻结线格式（base64(matchId):winner:localTeam）");
            CheckEq(PMDsLobbyEndedCodec.Prefix, "PMDS-END1:", "M02 前缀常量与冻结格式一致");

            PMNet.Session.PMDsEndedNotice notice;
            string error;
            bool decoded = PMNet.Session.PMDsEndedNoticeCodec.TryDecode(text, out notice, out error);
            Check(decoded && notice != null, "M03 生产编码能被共享严格解析（" + (error ?? "ok") + "）");
            if (!decoded || notice == null)
            {
                Check(false, "M03b 共享解析未接受生产编码，后续断语提前结束");
                return;
            }

            CheckEq(notice.MatchId, "m-loop-end-1", "M04 matchId 字段往返一致");
            CheckEq(notice.WinnerTeamId, 1, "M05 winnerTeamId 字段往返一致");
            CheckEq(notice.LocalTeamId, 0, "M06 localTeamId 字段往返一致");

            // 独立手写的冻结文本（**不经过本仓 Encode**）也必须能解析：排除「同源自证」。
            PMNet.Session.PMDsEndedNotice independent;
            Check(PMNet.Session.PMDsEndedNoticeCodec.TryDecode(frozenFormat, out independent, out error)
                && independent != null && independent.MatchId == "m-loop-end-1"
                && independent.WinnerTeamId == 1 && independent.LocalTeamId == 0,
                "M07 独立手写的冻结文本可解析且字段一致（" + (error ?? "ok") + "）");

            // ── 2. 前缀互不抢占：入局 codec 的 PMDS1:/PMDSR1: 必须原样保留（团队共享，不得丢）──
            CheckEq(PMNet.Session.PMDsEntryCodec.Prefix, "PMDS1:", "M08 PMDS1: 初始入局前缀未变");
            CheckEq(PMNet.Session.PMDsEntryCodec.ResumePrefix, "PMDSR1:", "M09 PMDSR1: 续局前缀未丢");
            Check(!PMNet.Session.PMDsEndedNoticeCodec.HasPrefix("PMDS1:AAAA:1:0"),
                "M10 本 codec 不认 PMDS1: 前缀（与入局 codec 不互相抢）");
            Check(!PMNet.Session.PMDsEndedNoticeCodec.HasPrefix("PMDSR1:AAAA:1:0"),
                "M11 本 codec 不认 PMDSR1: 前缀");
            Check(PMNet.Session.PMDsEndedNoticeCodec.HasPrefix(frozenFormat),
                "M12 HasPrefix 只认完整的 PMDS-END1: 前缀（含冒号）");
            CheckRejected(!PMNet.Session.PMDsEndedNoticeCodec.HasPrefix("PMDS-END1"),
                "M13 缺冒号的前缀不算命中（旧代码的裸前缀比较已被收紧）");

            // ── 3. 胜/负/平：结论口径与本局 HUD 完全一致（winner==0 → 平；localTeam==winner → 胜）──
            CheckVerdict(BuildEndedText("m-draw", 0, 1), PMNet.Session.PMDsEndedVerdict.Draw, "平局", "M14");
            CheckVerdict(BuildEndedText("m-win", 2, 2), PMNet.Session.PMDsEndedVerdict.Victory, "胜利", "M15");
            CheckVerdict(BuildEndedText("m-lose", 3, 1), PMNet.Session.PMDsEndedVerdict.Defeat, "失败", "M16");

            // ── 4. 畸形样本：一律必须被拒（负例优先）──
            //    覆盖契约要求的六类：非法分段 / 尾部 / 非规范 Base64 / 非法 UTF-8 / 负 team / 空 matchId。
            string[] malformed = new string[]
            {
                null,
                string.Empty,
                "PMDS-END1",                                       // 缺冒号
                "PMDS-END1:",                                      // 空载荷
                "PMDS-END1:AAAA",                                  // 缺字段
                "PMDS-END1:AAAA:1",                                // 缺 localTeam
                "PMDS-END1:AAAA:1:",                               // 空 localTeam
                "PMDS-END1:AAAA::0",                               // 空 winner
                "PMDS-END1:AAAA:1:0:9",                            // 尾部多余字段
                "PMDS-END1:AAAA:1:0:",                             // 尾部空字段（悬空冒号）
                "PMDS-END1::1:0",                                  // 空 matchId（空 base64）
                "pmds-end1:AAAA:1:0",                              // 前缀大小写不符
                " PMDS-END1:AAAA:1:0",                             // 前缀前有空白
                "XPMDS-END1:AAAA:1:0",                             // 前缀不在开头
                "PMDS1:AAAA:1:0",                                  // 用入局前缀冒充
                "PMDSR1:AAAA:1:0",                                 // 用续局前缀冒充
                "PMDS-END1:AAA:1:0",                               // base64 长度 %4 != 0
                "PMDS-END1:bS1wYQ:1:0",                            // 省略填充
                "PMDS-END1:AA=A:1:0",                              // 填充符出现在中间
                "PMDS-END1:AAA==:1:0",                             // 填充符过多
                "PMDS-END1:QR==:1:0",                              // 非规范 Base64（填充位非零）
                "PMDS-END1:AA A=:1:0",                             // 空白混入 Base64
                "PMDS-END1:wyg=:1:0",                              // 非法 UTF-8（0xC3 0x28）
                "PMDS-END1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('a', 129))) + ":1:0",
                "PMDS-END1:AAAA:-1:0",                             // 负 winner
                "PMDS-END1:AAAA:1:-2",                             // 负 localTeam
                "PMDS-END1:AAAA:+1:0",                             // 显式正号（非规范整数）
                "PMDS-END1:AAAA:1:+0",
                "PMDS-END1:AAAA: 1:0",                             // 字段内空白
                "PMDS-END1:AAAA:1: 0",
                "PMDS-END1:AAAA:01:0",                             // 前导零（非规范整数）
                "PMDS-END1:AAAA:1:00",
                "PMDS-END1:AAAA:abc:0",                            // 非数字
                "PMDS-END1:AAAA:1:x",
                "PMDS-END1:AAAA:1:0.0",                            // 小数
                "PMDS-END1:AAAA:0x1:0",                            // 十六进制写法
                "PMDS-END1:AAAA:2147483648:0",                     // int 溢出
                "PMDS-END1:AAAA:1:2147483648",
                "PMDS-END1:AAAA:1:0 ",                             // 尾随空白
                "PMDS-END1:AAAA:1:0\n",                            // 尾部换行
                "PMDS-END1:AAAA:\uFF11:0",                            // 全角数字（非 ASCII 数字）
                "PMDS-END1:" + new string('A', 600) + ":1:0",      // 文本超过 512 字节上限
            };

            for (int i = 0; i < malformed.Length; i++)
            {
                PMNet.Session.PMDsEndedNotice rejected;
                string reason;
                bool accepted = PMNet.Session.PMDsEndedNoticeCodec.TryDecode(malformed[i], out rejected, out reason);
                CheckRejected(!accepted && rejected == null,
                    "M20." + (i + 1).ToString("D2") + " 畸形样本被拒：" + DescribeText(malformed[i]) + "（"
                    + (accepted ? "被错误接受" : reason) + "）");
            }

            // ── 5. 配对反例：畸形必须被拒，而它的「最小修复版」必须被接受 ──
            //    没有这一对，一个「一律拒绝」的实现也能让上面所有负例全绿。
            string pairGood = BuildEndedText("m-pair", 1, 0);
            CheckMutationRejected(pairGood, pairGood + ":9", "M40 尾部多余字段被拒");
            CheckMutationRejected(pairGood, pairGood + ":", "M41 悬空尾部冒号被拒");
            CheckMutationRejected(pairGood, BuildEndedText("m-pair", 1, 0).Replace(":1:0", ":-1:0"),
                "M42 负 winner 被拒");
            CheckMutationRejected(pairGood, BuildEndedText("m-pair", 1, 0).Replace(":1:0", ":1:-2"),
                "M43 负 localTeam 被拒");
            CheckMutationRejected(pairGood, BuildEndedText("m-pair", 1, 0).Replace(":1:0", ":01:0"),
                "M44 前导零被拒（非规范整数）");
            CheckMutationRejected(pairGood, BuildEndedText("m-pair", 1, 0).Replace(":1:0", ":+1:0"),
                "M45 显式正号被拒");
            CheckMutationRejected("PMDS-END1:bS1wYQ==:1:0", "PMDS-END1:bS1wYQ:1:0",
                "M46 省略 Base64 填充被拒（配对样本的 base64 确实带填充）");
            CheckMutationRejected(pairGood, "PMDS-END1:wyg=:1:0", "M47 非法 UTF-8 被拒");
            CheckMutationRejected(pairGood, "PMDS-END1::1:0", "M48 空 matchId 被拒");
            CheckMutationRejected(pairGood, "PMDS1:"
                + Convert.ToBase64String(Encoding.UTF8.GetBytes("m-pair")) + ":1:0",
                "M49 用 PMDS1: 前缀冒充被拒");
            // 一个 1 字节的 matchId 是**合法**的，而且它的规范 Base64 以两个 '=' 结尾：
            // 必须被接受（这里刻意**不**照抄入局 codec 的「填充必须落在第 4 位以后」规则），
            // 而只把「填充位非零」的 QR== 拒掉。
            CheckMutationRejected("PMDS-END1:QQ==:1:0", "PMDS-END1:QR==:1:0",
                "M50 非规范 Base64（填充位非零）被拒，而规范的单字节编码被接受");

            // ── 6. 生产编码侧同样 fail-closed（远端字段可能非法：宁可让通知缺席，也不发必然被拒的文本）──
            PMNet.Session.PMDsEndedNotice badWinner = new PMNet.Session.PMDsEndedNotice();
            badWinner.MatchId = "m-bad";
            badWinner.WinnerTeamId = -1;
            badWinner.LocalTeamId = 0;
            string encodedText;
            string encodeError;
            CheckRejected(!PMNet.Session.PMDsEndedNoticeCodec.TryEncode(badWinner, out encodedText, out encodeError)
                && encodedText == null, "M60 负 winner 无法编码（" + (encodeError ?? "ok") + "）");
            ExpectThrow(delegate() { PMNet.Session.PMDsEndedNoticeCodec.Encode(badWinner); },
                "M61 Encode 对非法字段显式抛出（不静默发送）");

            PMNet.Session.PMDsEndedNotice badLocalTeam = new PMNet.Session.PMDsEndedNotice();
            badLocalTeam.MatchId = "m-bad";
            badLocalTeam.WinnerTeamId = 1;
            badLocalTeam.LocalTeamId = -2;
            CheckRejected(!PMNet.Session.PMDsEndedNoticeCodec.TryEncode(badLocalTeam, out encodedText, out encodeError),
                "M62 负 localTeam 无法编码（" + (encodeError ?? "ok") + "）");

            PMNet.Session.PMDsEndedNotice emptyMatch = new PMNet.Session.PMDsEndedNotice();
            emptyMatch.MatchId = string.Empty;
            CheckRejected(!PMNet.Session.PMDsEndedNoticeCodec.TryEncode(emptyMatch, out encodedText, out encodeError),
                "M63 空 matchId 无法编码（" + (encodeError ?? "ok") + "）");

            PMNet.Session.PMDsEndedNotice okNotice = new PMNet.Session.PMDsEndedNotice();
            okNotice.MatchId = "m-loop-end-1";
            okNotice.WinnerTeamId = 1;
            okNotice.LocalTeamId = 0;
            Check(PMNet.Session.PMDsEndedNoticeCodec.TryEncode(okNotice, out encodedText, out encodeError)
                && string.Equals(encodedText, frozenFormat, StringComparison.Ordinal),
                "M64 合法字段的 TryEncode 与冻结格式一致（" + (encodeError ?? "ok") + "）");

            // Lobby 侧的旧入口（PMDsLobbyEndedCodec）也必须走同一条 fail-closed 路径。
            PMDsLobbyEndedNotice badLobby = new PMDsLobbyEndedNotice();
            badLobby.MatchId = "m-bad";
            badLobby.WinnerTeamId = -1;
            badLobby.LocalTeamId = 0;
            string lobbyText;
            string lobbyError;
            CheckRejected(!PMDsLobbyEndedCodec.TryEncode(badLobby, out lobbyText, out lobbyError)
                && lobbyText == null, "M65 Lobby 侧负 winner 通知无法编码（" + (lobbyError ?? "ok") + "）");

            // ── 7. 宿主端到端负例：**已验证但不可信**的 winner（-1）不得变成客户端可见的「已结束」通知 ──
            //    控制协议的 Result.WinnerTeamId 只拒 < -1（-1 可上线），所以这条路径可达；
            //    若在此放行，客户端会拿一个负号去和「本队」比较，等于用乱值冒充可信结论。
            {
                FakeClock negativeClock = new FakeClock();
                FakeLauncher negativeLauncher = new FakeLauncher();
                FakeGateway negativeGateway = new FakeGateway();
                negativeGateway.Online.Add(301);
                negativeGateway.Online.Add(302);
                negativeGateway.ProcessAliveProbe =
                    () => negativeLauncher.Processes.Count > 0 && negativeLauncher.Processes[0].IsRunning;

                PMDsLobbyHost negativeHost = CreateHost(negativeClock, negativeLauncher, negativeGateway, null);
                Socket negativeSocket = null;
                try
                {
                    Check(negativeHost.TryStartMatch(MakeRequest("m-negative-winner", 301, 302)).IsQueued,
                        "M70 负 winner 场景：开局已入队");
                    Check(PumpUntil(negativeHost, () => negativeHost.StartedSessions.Length == 1),
                        "M71 负 winner 场景：开局已落地");

                    PMDsLobbySessionRecord negativeRecord = negativeHost.StartedSessions[0];
                    PMDsCoordinator negativeCoordinator = negativeHost.GetCoordinator("m-negative-winner");
                    string negativeDecodeError;
                    PMDsBootstrappedMatch negativeBoot = ReadBootstrap(
                        ArgValue(negativeLauncher.Requests[0].Arguments, "-bootstrap"), out negativeDecodeError);
                    if (negativeBoot == null)
                    {
                        Check(false, "M72 负 winner 场景：引导文件不可解码 " + negativeDecodeError);
                    }
                    else
                    {
                        negativeSocket = Connect(negativeHost.ControlPort);
                        SendBytes(negativeSocket,
                            BuildReadyFrame(negativeBoot, negativeRecord.Port, negativeBoot.CollisionDigest, true), 64);
                        Check(PumpUntil(negativeHost, () => negativeCoordinator.State == PMDsSessionState.Running),
                            "M72 负 winner 场景：会话 Running");

                        SendBytes(negativeSocket,
                            BuildResultFrame(negativeBoot, 8801UL, -1, Encoding.UTF8.GetBytes("negative-smoke")), 32);
                        Check(PumpUntil(negativeHost,
                                () => negativeCoordinator.State == PMDsSessionState.ResultPending),
                            "M73 负 winner 的 Result 被协调器受理（协议只拒 < -1）");
                        Check(PumpUntil(negativeHost, () => negativeHost.MatchEndedNoticesRecorded == 2L),
                            "M74 名册两条只读结果条目已写入（写入点未被本修改改变）");

                        string negativeText;
                        CheckRejected(!negativeHost.TryGetRecentMatchEndedNotice(301, out negativeText)
                            && negativeText == null,
                            "M75 负 winner 不产生客户端可见的结束通知（fail-closed，不冒充可信结果）");
                        CheckEq(negativeHost.MatchEndedNoticesEncodeRejected, 1L,
                            "M76 编码侧 fail-closed 分支确实走到且可观测");
                        CheckEq(negativeHost.MatchEndedNoticesServed, 0L, "M77 未服务任何不可信通知");
                    }
                }
                finally
                {
                    CloseQuietly(negativeSocket);
                    negativeHost.Dispose();
                }
            }
        }

        /// <summary>解码一段冻结文本并断言结论（胜/负/平）与只读文案。</summary>
        private static void CheckVerdict(string text, PMNet.Session.PMDsEndedVerdict expected,
            string expectedText, string tag)
        {
            PMNet.Session.PMDsEndedNotice notice;
            string error;
            if (!PMNet.Session.PMDsEndedNoticeCodec.TryDecode(text, out notice, out error) || notice == null)
            {
                Check(false, tag + " 合法样本应被接受：" + text + "（" + (error ?? "null") + "）");
                return;
            }

            PMNet.Session.PMDsEndedVerdict actual = PMNet.Session.PMDsEndedNoticeCodec.ResolveVerdict(notice);
            Check(actual == expected, tag + "a 结论=" + actual + "（期望 " + expected + "）");
            CheckEq(PMNet.Session.PMDsEndedNoticeCodec.VerdictText(actual), expectedText, tag + "b 结论文案");

            string message = PMNet.Session.PMDsEndedNoticeCodec.BuildReadOnlyMessage(notice);
            Check(message.IndexOf(expectedText, StringComparison.Ordinal) >= 0
                && message.IndexOf(notice.MatchId, StringComparison.Ordinal) >= 0,
                tag + "c 只读文案含结论与对局：" + message.Replace("\n", " / "));
        }

        /// <summary>
        /// 配对反例：<paramref name="good"/> 必须被接受、<paramref name="bad"/> 必须被拒。
        /// 没有这一对，一个「一律拒绝」的实现也能让所有负例全绿。
        /// </summary>
        private static void CheckMutationRejected(string good, string bad, string label)
        {
            PMNet.Session.PMDsEndedNotice decoded;
            string error;
            bool goodAccepted = PMNet.Session.PMDsEndedNoticeCodec.TryDecode(good, out decoded, out error);
            bool badAccepted = PMNet.Session.PMDsEndedNoticeCodec.TryDecode(bad, out decoded, out error);
            CheckRejected(goodAccepted && !badAccepted,
                label + "（合法样本被接受=" + goodAccepted + "，畸形样本被接受=" + badAccepted + "）");
        }

        /// <summary>
        /// 独立手写的冻结线格式（**不调用被测 codec**）：
        /// <c>base64(matchId UTF-8)</c> + <c>:</c> + winner + <c>:</c> + localTeam。
        /// 用它造合法样本，避免「用被测编码器造样本再喂给被测解析器」的同源自证。
        /// </summary>
        private static string BuildEndedText(string matchId, int winnerTeamId, int localTeamId)
        {
            return "PMDS-END1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(matchId))
                + ":" + winnerTeamId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ":" + localTeamId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>期望某段代码抛出（参数不合法是**调用方**的编程错误，必须显式炸）。</summary>
        private static void ExpectThrow(Action action, string label)
        {
            try
            {
                action();
                Check(false, label + "（未抛出）");
            }
            catch (Exception)
            {
                Check(true, label);
            }
        }

        /// <summary>把样本转成可读标签（截断 + 转义控制字符）。样本是测试数据，不含真实凭据。</summary>
        private static string DescribeText(string text)
        {
            if (text == null)
            {
                return "<null>";
            }

            string shown = text.Length > 48 ? text.Substring(0, 48) + "…" : text;
            return "\"" + shown.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\0", "\\0") + "\"";
        }

        /// <summary>
        /// 推进注入时钟并**保持 Lobby↔DS 控制面存活**：每步先发一条真实 Heartbeat 帧，
        /// 等到协调器确实收到并刷新了 liveness 时钟，再推进下一步。
        ///
        /// 为什么必须这样：玩家 TCP 断线与控制通道是两条独立链路，所以续玩窗口期内控制面仍需心跳；
        /// 否则会先撞 15 秒心跳超时，测试验的就不是续玩窗口而是另一条失败路径。
        /// </summary>
        private static void AdvanceWithHeartbeats(PMDsLobbyHost host, Socket controlSocket,
            PMDsBootstrappedMatch boot, PMDsCoordinator coordinator, FakeClock clock,
            long totalMilliseconds, long stepMilliseconds = 5000)
        {
            long remaining = totalMilliseconds;
            while (remaining > 0)
            {
                long step = remaining < stepMilliseconds ? remaining : stepMilliseconds;
                long before = coordinator.Counters.Heartbeats;
                clock.Ms += step;
                SendBytes(controlSocket, BuildHeartbeatFrame(boot), 64);
                if (!PumpUntil(host, () => coordinator.Counters.Heartbeats > before))
                {
                    // 不静默：心跳没被接受会让后续断言失去意义。
                    Check(false, "推进时钟时 Heartbeat 未被接受（state=" + coordinator.State + "）");
                    return;
                }

                remaining -= step;
            }
        }

        /// <summary>
        /// T-LOOP4 生产接线事实核对（Server 侧登录路径）。
        ///
        /// 本门禁不编 Server 主工程（避免锁住运行中的 Server.dll），所以对**真实源码**做文本事实断言：
        /// 续局与只读结果两条路径必须挂在「密码校验通过 + RegisterActiveClient 原子登记成功」之内，
        /// 且 T-LOOP2 的并发登记竞态修复（冲突回 Fail + 清身份）必须保留。
        /// 它与 `dotnet build Server/Server.csproj`（0 错误）一起构成 Server 侧接线证据；
        /// **真实 TCP 登录与真实两进程续局仍需实机验收**（T-LOOP8）。
        /// </summary>
        private static void VerifyServerLoginWiring()
        {
            string text;
            string error;
            if (!TryReadRepoFile("Server/Controller/Controllers.cs", out text, out error))
            {
                Check(false, "L71 无法定位 Server/Controller/Controllers.cs：" + error);
                return;
            }

            int loginIndex = text.IndexOf("public MainPack Login(Server server, Client client, MainPack pack)",
                StringComparison.Ordinal);
            Check(loginIndex > 0, "L71 找到 UserController.Login");

            int registeredIndex = text.IndexOf("if (registration == PMActiveRegistration.Registered)",
                StringComparison.Ordinal);
            Check(registeredIndex > loginIndex, "L72 Login 内存在原子登记成功分支");

            int resumeCallIndex = text.IndexOf("RequestResumeEntryFor(client.UID)", StringComparison.Ordinal);
            Check(resumeCallIndex > registeredIndex,
                "L73 续局请求只在原子登记成功分支内发出（密码错误/UID0/并发冲突都到不了）");

            int endedCallIndex = text.IndexOf("AttachEndedMatchNoticeIfAny(client, pack)", StringComparison.Ordinal);
            Check(endedCallIndex > registeredIndex, "L74 只读结果通知同样只在登记成功后附加");

            int findInfoIndex = text.IndexOf("public MainPack FindPlayerInfo(Server server, Client client, MainPack pack)",
                StringComparison.Ordinal);
            Check(findInfoIndex > resumeCallIndex,
                "L75 续局调用落在 Login 内部（不在 FindPlayerInfo 等其它入口）");

            int clearIdentityIndex = text.IndexOf("client.GetUserData.ClearLoginIdentity();", registeredIndex,
                StringComparison.Ordinal);
            Check(clearIdentityIndex > registeredIndex,
                "L76 登记冲突仍清登录身份并回 Fail（T-LOOP2 竞态修复保留）");

            Check(!ContainsPasswordLogging(text), "L77 Login 路径不打印密码字段（无凭据入日志）");

            int helperIndex = text.IndexOf("private static void RequestResumeEntryFor(int uid)", StringComparison.Ordinal);
            Check(helperIndex > 0, "L78 续局 helper 存在且为 login 专用封装");
            Check(text.IndexOf("TryRequestResumeEntry(uid)", helperIndex, StringComparison.Ordinal) > helperIndex,
                "L79 helper 只是转发到宿主 API（不在 Controller 里另写一套签发）");
        }

        /// <summary>是否在日志调用里出现密码字段（凭据不得进日志）。</summary>
        private static bool ContainsPasswordLogging(string text)
        {
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.IndexOf("Log", StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                if (line.IndexOf("Password", StringComparison.Ordinal) >= 0
                    || line.IndexOf("PassWord", StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 从「当前目录」与「AppContext.BaseDirectory」各自向上查找仓库相对文件。
        /// 找不到时**明确失败**（不静默跳过事实核对）。
        /// </summary>
        private static bool TryReadRepoFile(string relativePath, out string text, out string error)
        {
            text = null;
            error = null;

            string[] roots = new string[] { Environment.CurrentDirectory, AppContext.BaseDirectory };
            for (int i = 0; i < roots.Length; i++)
            {
                string directory = roots[i];
                for (int depth = 0; depth < 12 && !string.IsNullOrEmpty(directory); depth++)
                {
                    string candidate = Path.Combine(directory, relativePath);
                    if (File.Exists(candidate))
                    {
                        try
                        {
                            text = File.ReadAllText(candidate);
                            return true;
                        }
                        catch (Exception ex)
                        {
                            error = ex.GetType().Name + " " + ex.Message;
                            return false;
                        }
                    }

                    DirectoryInfo parent = Directory.GetParent(directory);
                    if (parent == null)
                    {
                        break;
                    }

                    directory = parent.FullName;
                }
            }

            error = "从当前目录与 AppContext.BaseDirectory 向上 12 层都没有找到 " + relativePath;
            return false;
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
