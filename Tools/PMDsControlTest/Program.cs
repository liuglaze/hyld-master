using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using PMNet;
using PMNet.Control;

namespace PMDsControlTest
{
    /// <summary>
    /// R3-A1 验收门禁：控制协议 / 票据 / Lobby 进程协调器。
    ///
    /// 事实来源：
    /// - `Docs/plans/net-r3-control-contract.md` §2（身份及票据）、§3（控制消息和状态）、§5（验收边界）
    /// - `Docs/plans/net-architecture-migration.md` 的 R3A1 / R3A2 行
    ///
    /// 门禁结构（与契约逐项对应）：
    /// A 分帧（4 字节 LE + 半包 / 粘包 / 超限 / 有界）
    /// B 编解码严格性（未知字段/版本/类型、重复字段、尾部字节、长度越界）
    /// C MAC（签名 / 篡改 / 错密钥 / 常量时间比较）
    /// D 票据（绑定字段、全部拒绝原因、幂等重发、不打印秘密）
    /// E 引导文件（同机交付：编码/解析/错魔数/错长度）
    /// F 协调器正常路径（Allocated → … → Exited，端口/名册/墓碑）
    /// G 故障注入（启动失败/启动超时/就绪前崩溃/心跳超时/结果冲突/强杀/日志≠就绪）
    /// H 安全与拒绝面（坏 MAC 不动状态、错身份、错状态、票据名册校验）
    /// I 真实 loopback TCP（真 socket 上跑分帧→解码→验签→状态推进）
    /// J 真实固定 exe 进程适配（有界输出、无 shell、kill）
    /// K 审查闭合回归（M1 强杀未退出不得释放端口 / M2 不得由重发次数宣布对端确认 /
    ///   M3 同 ID 内容完整比对（同 hash 不同 summary 必须冲突）/ M4 Exited/Error 状态门）
    ///
    /// 退出码：0 = 全部通过；1 = 有失败。
    /// </summary>
    internal static class Program
    {
        private static int _passed;
        private static int _negativePassed;
        private static readonly List<string> _failures = new List<string>();
        private static readonly Dictionary<string, int> _tagTotal = new Dictionary<string, int>();
        private static string _tag = "R3A1";

        private static int Main(string[] args)
        {
            if (args != null && args.Length > 0)
            {
                if (args[0] == "--child-emit") { return RunChildEmit(args); }
                if (args[0] == "--child-sleep") { Thread.Sleep(60000); return 0; }
            }

            Console.WriteLine("=== R3-A1：控制协议 / 票据 / Lobby 进程协调器 ===");
            Console.WriteLine();

            Section("A. 分帧：4 字节 LE + 半包 / 粘包 / 超限立即拒绝 / 有界", "R3A1", TestFraming);
            Section("B. 编解码严格性：未知字段/版本/类型、重复、尾部字节、长度越界", "R3A1", TestCodecStrictness);
            Section("C. MAC：签名 / 篡改 / 错密钥 / 常量时间比较", "R3A1", TestMac);
            Section("D. 票据：绑定与全部拒绝原因 / 幂等重发 / 不打印秘密", "R3A1", TestTicket);
            Section("E. 引导文件：同机交付格式（只传路径给进程）", "R3A1", TestBootstrapDocument);
            Section("F. 协调器正常路径：Allocated → … → Exited", "R3A2", TestCoordinatorHappyPath);
            Section("G. 故障注入：启动失败 / 超时 / 崩溃 / 心跳 / 结果冲突 / 强杀", "R3A2", TestCoordinatorFailures);
            Section("H. 拒绝面与安全：坏 MAC 不动状态 / 错身份 / 错状态 / 名册校验", "R3A2", TestCoordinatorSecurity);
            Section("I. 真实 loopback TCP：真 socket 分帧 → 解码 → 验签 → 推进", "R3A2", TestLoopbackTcp);
            Section("J. 真实固定 exe 进程适配：有界输出 / 无 shell / kill", "R3A2", TestRealProcessAdapter);
            Section("K. 审查闭合回归：M1 端口释放前提 / M2 不假称对端确认 / M3 内容完整比对 / M4 状态门",
                "R3A2", TestReviewClosureRegression);
            Section("L. ResultPending 退出语义：认证显式 Exited(0) = 正常完成 / 纯进程退出 = Failed",
                "R3A2", TestResultPendingExitSemantics);
            Section("M. 控制面双向 liveness：Ready/Heartbeat 应答 / 节流 / 被拒帧不续命",
                "R3A2", TestControlFaceLiveness);
            Section("N. 认证正常退出的优雅退出宽限：宽限内不强杀 / 到期才异常收尾强杀",
                "R3A2", TestGracefulExitGrace);

            Console.WriteLine();
            var tags = new List<string>(_tagTotal.Keys);
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
        // 测试骨架
        // ────────────────────────────────────────────────────────────────

        private static void Section(string name, string tag, Action body)
        {
            _tag = tag;
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
            }
            else
            {
                _failures.Add("[" + _tag + "] " + label);
            }

            Console.WriteLine((ok ? "    OK   " : "    FAIL ") + label);
        }

        /// <summary>负向输入断言（拒绝面）。单独计数，避免「全绿但其实没测到拒绝路径」。</summary>
        private static void CheckNeg(bool ok, string label)
        {
            _negativePassed++;
            Check(ok, "[负向] " + label);
        }

        private static void CheckEq(object actual, object expected, string label)
        {
            bool ok = Equals(actual, expected);
            Check(ok, label + "（实际 " + (actual == null ? "<null>" : actual.ToString())
                + "，期望 " + (expected == null ? "<null>" : expected.ToString()) + "）");
        }

        private static void CheckBytes(byte[] actual, byte[] expected, string label)
        {
            bool ok = actual != null && expected != null && actual.Length == expected.Length;
            if (ok)
            {
                for (int i = 0; i < actual.Length; i++)
                {
                    if (actual[i] != expected[i])
                    {
                        ok = false;
                        break;
                    }
                }
            }

            Check(ok, label + "（长度 " + (actual == null ? -1 : actual.Length) + " vs "
                + (expected == null ? -1 : expected.Length) + "）");
        }

        /// <summary>期望任意异常（参数校验类失败不是协议异常，但仍必须是「明确失败」）。</summary>
        private static void ExpectAnyThrow(Action action, string label)
        {
            try
            {
                action();
                CheckNeg(false, label + "（期望抛异常，实际未抛）");
            }
            catch (Exception ex)
            {
                CheckNeg(true, label + "（" + ex.GetType().Name + "）");
            }
        }

        private static void ExpectThrow(Action action, string label)
        {
            try
            {
                action();
                CheckNeg(false, label + "（期望抛异常，实际未抛）");
            }
            catch (PMDsControlProtocolException)
            {
                CheckNeg(true, label);
            }
            catch (Exception ex)
            {
                CheckNeg(false, label + "（期望 PMDsControlProtocolException，实际 " + ex.GetType().Name + "）");
            }
        }

        private static string Hex(byte[] data)
        {
            if (data == null)
            {
                return "<null>";
            }

            StringBuilder builder = new StringBuilder(data.Length * 2);
            for (int i = 0; i < data.Length; i++)
            {
                builder.Append(data[i].ToString("x2"));
            }

            return builder.ToString();
        }

        /// <summary>ASCII 摘要构造（测试里让「同 ID 同内容 / 不同内容」一眼可读）。</summary>
        private static byte[] Summary(string text)
        {
            return Encoding.ASCII.GetBytes(text);
        }

        // ────────────────────────────────────────────────────────────────
        // 测试替身
        // ────────────────────────────────────────────────────────────────

        private sealed class FakeClock : IPMDsClock
        {
            public long Now = 1700000000000L;

            public long UtcNowUnixMilliseconds { get { return Now; } }

            public void Advance(long milliseconds) { Now += milliseconds; }
        }

        private sealed class RecordingSink : IPMDsCoordinatorSink
        {
            public readonly List<PMDsCoordinatorEvent> Events = new List<PMDsCoordinatorEvent>();

            public void OnEffect(PMDsCoordinatorEvent effect) { Events.Add(effect); }

            public int Count(PMDsCoordinatorEffect kind)
            {
                int n = 0;
                for (int i = 0; i < Events.Count; i++)
                {
                    if (Events[i].Effect == kind) { n++; }
                }

                return n;
            }

            public PMDsCoordinatorEvent Last(PMDsCoordinatorEffect kind)
            {
                for (int i = Events.Count - 1; i >= 0; i--)
                {
                    if (Events[i].Effect == kind) { return Events[i]; }
                }

                return null;
            }

            public List<byte[]> Payloads(PMDsControlMessageType type)
            {
                List<byte[]> result = new List<byte[]>();
                for (int i = 0; i < Events.Count; i++)
                {
                    if (Events[i].Effect == PMDsCoordinatorEffect.ControlMessageOut && Events[i].ControlType == type)
                    {
                        result.Add(Events[i].ControlPayload);
                    }
                }

                return result;
            }
        }

        private sealed class FakeProcess : IPMDsProcess
        {
            private bool _running = true;
            private int _exitCode;
            private bool _exitedRaised;

            public int Id = 4242;
            public bool KillExitsProcess = true;

            /// <summary>模拟「Kill 请求发出但抛异常」（真实适配器里 Kill 可能抛）。</summary>
            public bool ThrowOnKill;

            /// <summary>WaitForExit 被调用的次数（证明协调器真的问过「你走了吗」）。</summary>
            public int WaitForExitCount;

            public bool Killed;
            public int KillCount;
            public long OutBytes;
            public long ErrBytes;
            public string OutText = string.Empty;
            public string ErrText = string.Empty;

            /// <summary>模拟 stdout 输出（累计字节 + 尾部文本）。</summary>
            public void EmitStdout(string text)
            {
                OutText = text;
                OutBytes += Encoding.UTF8.GetByteCount(text);
            }

            /// <summary>模拟 stderr 输出。</summary>
            public void EmitStderr(string text)
            {
                ErrText = text;
                ErrBytes += Encoding.UTF8.GetByteCount(text);
            }

            public int ProcessId { get { return Id; } }

            public bool IsRunning { get { return _running; } }

            public bool TryGetExitCode(out int exitCode)
            {
                exitCode = _exitCode;
                return !_running;
            }

            /// <summary>
            /// 替身是同步的：没有真实 I/O 要等，所以退化为一次状态查询（测试因此不需要 sleep）。
            /// 但接口语义保持不变：返回 false 就意味着「协作者不得释放端口」。
            /// </summary>
            public bool WaitForExit(int timeoutMilliseconds)
            {
                WaitForExitCount++;
                return !_running;
            }

            public long StdOutTotalBytes { get { return OutBytes; } }

            public long StdErrTotalBytes { get { return ErrBytes; } }

            public string StdOutTail { get { return OutText; } }

            public string StdErrTail { get { return ErrText; } }

            public event Action<int> Exited;

            /// <summary>模拟进程自行退出（崩溃/正常返回）。</summary>
            public void Exit(int code)
            {
                if (!_running)
                {
                    return;
                }

                _exitCode = code;
                _running = false;
                Raise();
            }

            public void RequestGracefulExit()
            {
            }

            public void Kill()
            {
                KillCount++;
                Killed = true;
                if (ThrowOnKill)
                {
                    throw new InvalidOperationException("替身注入的强杀失败");
                }

                if (KillExitsProcess)
                {
                    Exit(-1);
                }
            }

            public void Dispose()
            {
            }

            private void Raise()
            {
                if (_exitedRaised)
                {
                    return;
                }

                _exitedRaised = true;
                Action<int> handler = Exited;
                if (handler != null) { handler(_exitCode); }
            }
        }

        private sealed class FakeLauncher : IPMDsProcessLauncher
        {
            public bool FailStart;
            public PMDsProcessFault FailFault = PMDsProcessFault.LaunchFailed;
            public string FailDetail = "替身注入的启动失败";
            public FakeProcess Process = new FakeProcess();
            public int StartCount;
            public PMDsProcessLaunchRequest LastRequest;

            public bool TryStart(PMDsProcessLaunchRequest request, out IPMDsProcess process,
                out PMDsProcessFault fault, out string detail)
            {
                StartCount++;
                LastRequest = request;
                if (FailStart)
                {
                    process = null;
                    fault = FailFault;
                    detail = FailDetail;
                    return false;
                }

                process = Process;
                fault = PMDsProcessFault.None;
                detail = null;
                return true;
            }
        }

        /// <summary>测试侧的「DS 视图」：从引导文件拿到密钥，然后像真 DS 那样签名上行消息。</summary>
        private sealed class DsView
        {
            public string MatchId;
            public string DsId;
            public uint Epoch;
            public uint ProtocolHash;
            public uint CollisionDigest;
            public PMDsMatchKey Key;
            public PMDsControlSigner Signer;

            public static DsView FromCoordinator(PMDsCoordinator coordinator)
            {
                byte[] document = coordinator.ExportBootstrapDocument();
                PMDsBootstrappedMatch boot;
                string error;
                if (!PMDsBootstrapDocument.TryDecode(document, out boot, out error))
                {
                    throw new InvalidOperationException("引导文件解析失败：" + error);
                }

                DsView view = new DsView();
                view.MatchId = boot.MatchId;
                view.DsId = boot.DsId;
                view.Epoch = boot.Epoch;
                view.ProtocolHash = boot.ProtocolHash;
                view.CollisionDigest = boot.CollisionDigest;
                view.Key = boot.Key;
                view.Signer = boot.Key.CreateControlSigner();
                return view;
            }

            public byte[] Ready(int port, uint digest, bool sceneReady)
            {
                return Sign(PMDsControlMessageType.Ready, 11UL, new PMDsReadyBody
                {
                    BoundPort = port,
                    SceneReady = sceneReady,
                    CollisionDigest = digest,
                });
            }

            public byte[] Heartbeat(long uptimeMilliseconds, int players)
            {
                return Sign(PMDsControlMessageType.Heartbeat, 12UL, new PMDsHeartbeatBody
                {
                    UptimeMilliseconds = uptimeMilliseconds,
                    PlayerCount = players,
                });
            }

            public byte[] Result(ulong resultId, int winnerTeamId, byte[] summary)
            {
                return Sign(PMDsControlMessageType.Result, 0UL, new PMDsResultBody
                {
                    ResultId = resultId,
                    WinnerTeamId = winnerTeamId,
                    Summary = summary ?? new byte[0],
                });
            }

            public byte[] Exited(int exitCode)
            {
                return Sign(PMDsControlMessageType.Exited, 0UL, new PMDsExitedBody { ExitCode = exitCode });
            }

            public byte[] Error(uint code, string message)
            {
                return Sign(PMDsControlMessageType.Error, 0UL, new PMDsErrorBody { Code = code, Message = message });
            }

            public byte[] Shutdown(uint reasonCode)
            {
                return Sign(PMDsControlMessageType.Shutdown, 0UL, new PMDsShutdownBody { ReasonCode = reasonCode });
            }

            public byte[] Sign(PMDsControlMessageType type, ulong requestId, PMDsControlBody body)
            {
                PMDsControlMessage message = PMDsControlMessage.Create(
                    type, MatchId, DsId, Epoch, ProtocolHash, requestId, body);
                return Signer.Sign(message);
            }

            /// <summary>用另一组身份字段签名（用于「身份不匹配」的负向用例）。</summary>
            public byte[] SignWithIdentity(PMDsControlMessageType type, string matchId, string dsId,
                uint epoch, uint protocolHash, ulong requestId, PMDsControlBody body)
            {
                PMDsControlMessage message = PMDsControlMessage.Create(
                    type, matchId, dsId, epoch, protocolHash, requestId, body);
                return Signer.Sign(message);
            }
        }

        // ────────────────────────────────────────────────────────────────
        // 公共构造
        // ────────────────────────────────────────────────────────────────

        private static readonly PMDsRosterIdentity[] DefaultRoster = new PMDsRosterIdentity[]
        {
            new PMDsRosterIdentity(101, 1, 0, 3),
            new PMDsRosterIdentity(102, 2, 0, 5),
            new PMDsRosterIdentity(103, 3, 1, 7),
            new PMDsRosterIdentity(104, 4, 1, 9),
        };

        private static PMDsCoordinatorOptions MakeOptions()
        {
            PMDsCoordinatorOptions options = new PMDsCoordinatorOptions();
            options.StartTimeout = TimeSpan.FromSeconds(30);
            options.HeartbeatTimeout = TimeSpan.FromSeconds(15);
            options.ResultAckRetryInterval = TimeSpan.FromSeconds(1);
            options.MaxResultAckRetransmits = 3;
            options.ShutdownGracePeriod = TimeSpan.FromSeconds(10);
            options.TombstoneTtl = TimeSpan.FromMinutes(5);
            options.MaxTombstones = 64;
            options.PortRangeFirst = 7801;
            options.PortRangeLast = 7899;
            options.ProtocolHash = 0xAABBCCDDu;
            return options;
        }

        private static PMDsAllocationRequest MakeRequest(string matchId, string dsId, uint epoch, uint protocolHash,
            uint collisionDigest, PMDsRosterIdentity[] roster)
        {
            PMDsAllocationRequest request = new PMDsAllocationRequest();
            request.MatchId = matchId;
            request.DsId = dsId;
            request.Epoch = epoch;
            request.ProtocolHash = protocolHash;
            request.CollisionDigest = collisionDigest;
            request.Roster = roster ?? DefaultRoster;
            request.Process = new PMDsProcessLaunchRequest(
                @"C:\PMDsFake\HyldDS.exe", @"C:\PMDsFake",
                "-batchmode", "-nographics", "-server", "-port", "7801");
            return request;
        }

        private static void MyAllocate(PMDsCoordinator coordinator, string matchId, string dsId, uint epoch)
        {
            PMDsCoordinatorReply reply = coordinator.Allocate(
                MakeRequest(matchId, dsId, epoch, 0xAABBCCDDu, 1u, DefaultRoster));
            if (reply.Outcome != PMDsCoordinatorOutcome.Applied)
            {
                throw new InvalidOperationException("测试设施错误：Allocate 未成功：" + reply);
            }
        }

        /// <summary>建一个已经完成分配的协调器（返回替身与记录器便于后续断言）。</summary>
        private static PMDsCoordinator NewCoordinator(FakeClock clock, FakeLauncher launcher, RecordingSink sink,
            PMDsCoordinatorOptions options, PMDsAllocationRequest request)
        {
            PMDsCoordinator coordinator = new PMDsCoordinator(
                options, clock, launcher, sink ?? new RecordingSink());
            PMDsCoordinatorReply reply = coordinator.Allocate(request);
            if (reply.Outcome != PMDsCoordinatorOutcome.Applied)
            {
                throw new InvalidOperationException("测试设施错误：Allocate 未成功：" + reply);
            }

            return coordinator;
        }

        // ────────────────────────────────────────────────────────────────
        // A. 分帧
        // ────────────────────────────────────────────────────────────────

        private static void TestFraming()
        {
            byte[] payload = Encoding.ASCII.GetBytes("hello-control-frame");

            byte[] frame = PMDsControlFraming.Frame(payload);
            CheckEq(frame.Length, payload.Length + 4, "A1 分帧总长 = 4 + 载荷");
            CheckEq(PMDsControlFraming.ReadLengthPrefix(frame, 0), payload.Length, "A2 长度前缀可回读");

            PMDsControlFrameDecoder decoder = new PMDsControlFrameDecoder();
            decoder.Append(frame, 0, frame.Length);
            byte[] got;
            Check(decoder.TryDequeue(out got), "A3 完整帧可解出");
            CheckBytes(got, payload, "A4 解出的载荷逐字节一致");
            Check(!decoder.TryDequeue(out got), "A5 无第二个帧");
            CheckEq(decoder.PendingBytes, 0, "A6 无滞留字节");

            // 半包：逐字节喂入
            decoder = new PMDsControlFrameDecoder();
            for (int i = 0; i < frame.Length; i++)
            {
                decoder.Append(frame, i, 1);
                if (i < frame.Length - 1)
                {
                    CheckInnerNoFrame(decoder, i);
                }
            }

            Check(decoder.TryDequeue(out got), "A7 逐字节（半包）最终解出 1 帧");
            CheckBytes(got, payload, "A8 半包重组内容一致");
            CheckEq(decoder.DecodedFrameCount, 1L, "A9 半包不产生额外帧");

            // 粘包：一次喂两个帧
            byte[] frameA = PMDsControlFraming.Frame(Encoding.ASCII.GetBytes("AAA"));
            byte[] frameB = PMDsControlFraming.Frame(Encoding.ASCII.GetBytes("BBBB"));
            byte[] both = new byte[frameA.Length + frameB.Length];
            Buffer.BlockCopy(frameA, 0, both, 0, frameA.Length);
            Buffer.BlockCopy(frameB, 0, both, frameA.Length, frameB.Length);

            decoder = new PMDsControlFrameDecoder();
            decoder.Append(both, 0, both.Length);
            byte[] first;
            byte[] second;
            bool gotFirst = decoder.TryDequeue(out first);
            bool gotSecond = decoder.TryDequeue(out second);
            Check(gotFirst && gotSecond, "A10 粘包一次解出 2 帧");
            CheckEq(Encoding.ASCII.GetString(first), "AAA", "A11 第一帧顺序正确");
            CheckEq(Encoding.ASCII.GetString(second), "BBBB", "A12 第二帧顺序正确");
            Check(!decoder.TryDequeue(out first), "A13 粘包后无残留帧");

            // 半包 + 粘包混合：一个完整帧 + 第二个帧的前半
            decoder = new PMDsControlFrameDecoder();
            byte[] staged = new byte[frameA.Length + 2];
            Buffer.BlockCopy(frameA, 0, staged, 0, frameA.Length);
            Buffer.BlockCopy(frameB, 0, staged, frameA.Length, 2);
            decoder.Append(staged, 0, staged.Length);
            CheckEq(decoder.DecodedFrameCount, 1L, "A14 混合场景先解出 1 帧");
            CheckEq(decoder.PendingBytes, 2, "A15 剩余 2 字节滞留");
            decoder.Append(frameB, 2, frameB.Length - 2);
            CheckEq(decoder.DecodedFrameCount, 2L, "A16 补齐后解出第 2 帧");
            CheckEq(decoder.PendingBytes, 0, "A17 补齐后无滞留");

            // 超限：立即拒绝（不等待数据到达）
            decoder = new PMDsControlFrameDecoder();
            byte[] oversize = new byte[4];
            PMDsControlFraming.WriteLengthPrefix(oversize, 0, PMDsControlWire.MaxFramePayloadBytes + 1);
            ExpectThrow(delegate() { decoder.Append(oversize, 0, 4); },
                "A18 声明长度 > 64KiB 立即抛（不等待数据）");
            Check(decoder.IsFaulted, "A19 超限后进入故障态");
            CheckEq(decoder.Fault, PMDsControlFrameFault.OversizeLength, "A20 故障分类为 OversizeLength");
            CheckEq(decoder.RejectedFrameCount, 1L, "A21 被拒帧计数为 1");
            CheckEq(decoder.PendingBytes, 4, "A22 超限时不吞掉任何后续数据（只留前缀）");
            ExpectThrow(delegate() { decoder.Append(oversize, 0, 4); }, "A23 故障态再次喂入仍抛");

            // 巨长前缀 0x7FFFFFFF：不得被当成负数而绕过上限
            decoder = new PMDsControlFrameDecoder();
            byte[] huge = new byte[] { 0xFF, 0xFF, 0xFF, 0x7F };
            ExpectThrow(delegate() { decoder.Append(huge, 0, 4); }, "A24 0x7FFFFFFF 前缀被拒");

            // 零长
            decoder = new PMDsControlFrameDecoder();
            byte[] zero = new byte[] { 0, 0, 0, 0 };
            ExpectThrow(delegate() { decoder.Append(zero, 0, 4); }, "A25 零长帧被拒");
            CheckEq(decoder.Fault, PMDsControlFrameFault.ZeroLength, "A26 零长故障分类");

            // 恰好到上限：必须被接受
            byte[] maxPayload = new byte[PMDsControlWire.MaxFramePayloadBytes];
            for (int i = 0; i < maxPayload.Length; i++)
            {
                maxPayload[i] = (byte)(i & 0xFF);
            }

            byte[] maxFrame = PMDsControlFraming.Frame(maxPayload);
            decoder = new PMDsControlFrameDecoder();
            decoder.Append(maxFrame, 0, maxFrame.Length);
            Check(decoder.TryDequeue(out got), "A27 恰好 64KiB 载荷被接受");
            CheckEq(got.Length, PMDsControlWire.MaxFramePayloadBytes, "A28 上限载荷长度一致");

            // 有界：滞留字节始终 ≤ 一个最大帧 + 前缀
            decoder = new PMDsControlFrameDecoder();
            int chunk = 4096;
            bool exceeded = false;
            for (int written = 0; written < maxFrame.Length; written += chunk)
            {
                int n = Math.Min(chunk, maxFrame.Length - written);
                decoder.Append(maxFrame, written, n);
                if (decoder.PendingBytes > PMDsControlWire.MaxFramePayloadBytes + 4)
                {
                    exceeded = true;
                }
            }

            Check(!exceeded, "A29 滞留字节始终 ≤ 64KiB+4（有界）");
            Check(decoder.PeakPendingBytes <= PMDsControlWire.MaxFramePayloadBytes + 4,
                "A30 峰值滞留 " + decoder.PeakPendingBytes + " ≤ 上限");
            Check(decoder.TryDequeue(out got), "A31 分块喂入的大帧可解出");

            // 写侧：超限载荷不允许成帧
            byte[] tooBig = new byte[PMDsControlWire.MaxFramePayloadBytes + 1];
            ExpectThrow(delegate() { PMDsControlFraming.Frame(tooBig); },
                "A32 成帧函数拒绝超上限载荷（" + tooBig.Length + " 字节）");
            ExpectAnyThrow(delegate() { PMDsControlFraming.Frame(maxPayload, 0, maxPayload.Length + 1); },
                "A32b 成帧函数拒绝越界范围");
            ExpectThrow(delegate() { PMDsControlFraming.Frame(new byte[0]); }, "A33 成帧函数拒绝空载荷");
        }

        private static void CheckInnerNoFrame(PMDsControlFrameDecoder decoder, int step)
        {
            byte[] none;
            if (decoder.TryDequeue(out none))
            {
                Check(false, "A-半包：第 " + step + " 步就解出了帧（数据不足），载荷长度 " + none.Length);
            }
        }

        // ────────────────────────────────────────────────────────────────
        // B. 编解码严格性
        // ────────────────────────────────────────────────────────────────

        private static void TestCodecStrictness()
        {
            byte[] keyBytes = PMDsCrypto.RandomBytes(PMDsControlWire.ControlKeyBytes);
            PMDsControlSigner signer = new PMDsControlSigner(keyBytes);
            const string match = "match-strict";
            const string ds = "ds-strict";
            const uint epoch = 9u;
            const uint hash = 0x01020304u;

            // B1 每种消息类型的往返
            RoundTrip(signer, match, ds, epoch, hash, new PMDsReadyBody
            {
                BoundPort = 7801,
                SceneReady = true,
                CollisionDigest = 0xDEADBEEFu,
            }, PMDsControlMessageType.Ready, "B1 Ready");

            RoundTrip(signer, match, ds, epoch, hash, new PMDsHeartbeatBody
            {
                UptimeMilliseconds = 123456,
                PlayerCount = 3,
            }, PMDsControlMessageType.Heartbeat, "B2 Heartbeat");

            RoundTrip(signer, match, ds, epoch, hash, new PMDsResultBody
            {
                ResultId = 77UL,
                WinnerTeamId = -1,
                Summary = Encoding.ASCII.GetBytes("draw"),
            }, PMDsControlMessageType.Result, "B3 Result");

            RoundTrip(signer, match, ds, epoch, hash, new PMDsResultAckBody { ResultId = 77UL },
                PMDsControlMessageType.ResultAck, "B4 ResultAck");

            RoundTrip(signer, match, ds, epoch, hash, new PMDsShutdownBody { ReasonCode = 5u },
                PMDsControlMessageType.Shutdown, "B5 Shutdown");

            RoundTrip(signer, match, ds, epoch, hash, new PMDsExitedBody { ExitCode = -9 },
                PMDsControlMessageType.Exited, "B6 Exited");

            RoundTrip(signer, match, ds, epoch, hash, new PMDsErrorBody { Code = 3u, Message = "bad input" },
                PMDsControlMessageType.Error, "B7 Error");

            PMDsBootstrapBody bootstrapBody = new PMDsBootstrapBody();
            bootstrapBody.CollisionDigest = 0x11223344u;
            bootstrapBody.Players = new PMDsBootstrapPlayer[]
            {
                new PMDsBootstrapPlayer(new PMDsRosterIdentity(101, 1, 0, 3), PMDsCrypto.RandomBytes(96)),
                new PMDsBootstrapPlayer(new PMDsRosterIdentity(102, 2, 1, 4), PMDsCrypto.RandomBytes(96)),
            };
            byte[] bootstrapPayload = signer.Sign(PMDsControlMessage.Create(
                PMDsControlMessageType.Bootstrap, match, ds, epoch, hash, 1UL, bootstrapBody));
            PMDsControlMessage decodedBootstrap = PMDsControlCodec.Decode(bootstrapPayload, 0, bootstrapPayload.Length);
            CheckEq(decodedBootstrap.AsBootstrap.Players.Length, 2, "B8 Bootstrap 名册人数往返");
            CheckEq(decodedBootstrap.AsBootstrap.Players[1].Identity.HeroId, 4, "B9 Bootstrap 名册字段往返");
            CheckEq(decodedBootstrap.AsBootstrap.Players[1].Ticket.Length, 96, "B10 Bootstrap 票据往返");

            // B11 未知消息类型
            byte[] unknownType = HandPayload(1u, 99u, match, ds, epoch, hash, 1UL, BuildReadyBody(7801, 0x1u, true));
            ExpectThrow(delegate() { PMDsControlCodec.Decode(unknownType, 0, unknownType.Length); },
                "B11 未知消息类型被拒");

            // B12 未知格式版本
            byte[] badVersion = HandPayload(2u, (uint)PMDsControlMessageType.Ready, match, ds, epoch, hash, 1UL,
                BuildReadyBody(7801, 0x1u, true));
            ExpectThrow(delegate() { PMDsControlCodec.Decode(badVersion, 0, badVersion.Length); },
                "B12 未知格式版本被拒");

            // B13 未知信封字段号（追加字段 9）
            byte[] withUnknownField = WithExtraField(
                HandPayload(1u, (uint)PMDsControlMessageType.Ready, match, ds, epoch, hash, 1UL,
                    BuildReadyBody(7801, 0x1u, true)), 9, 123UL);
            ExpectThrow(delegate() { PMDsControlCodec.Decode(withUnknownField, 0, withUnknownField.Length); },
                "B13 未知信封字段号被拒");

            // B14 字段重复
            byte[] duplicated = DuplicateField(
                HandPayload(1u, (uint)PMDsControlMessageType.Ready, match, ds, epoch, hash, 1UL,
                    BuildReadyBody(7801, 0x1u, true)), 5, 9u);
            ExpectThrow(delegate() { PMDsControlCodec.Decode(duplicated, 0, duplicated.Length); },
                "B14 信封字段重复被拒");

            // B15 字段乱序（把 epoch 挪到 matchId 之前）
            byte[] outOfOrder = OutOfOrderEpoch(hash, epoch);
            ExpectThrow(delegate() { PMDsControlCodec.Decode(outOfOrder, 0, outOfOrder.Length); },
                "B15 信封字段乱序被拒");

            // B16 MAC 之后还有尾部字节
            byte[] signed = signer.Sign(PMDsControlMessage.Create(
                PMDsControlMessageType.Heartbeat, match, ds, epoch, hash, 3UL,
                new PMDsHeartbeatBody { UptimeMilliseconds = 5, PlayerCount = 1 }));
            byte[] withTail = new byte[signed.Length + 1];
            Buffer.BlockCopy(signed, 0, withTail, 0, signed.Length);
            withTail[signed.Length] = 0xAB;
            ExpectThrow(delegate() { PMDsControlCodec.Decode(withTail, 0, withTail.Length); },
                "B16 MAC 字段之后有尾部字节被拒");

            // B17 缺失 MAC
            byte[] unsignedPrefix = PMDsControlCodec.EncodePrefix(PMDsControlMessage.Create(
                PMDsControlMessageType.Ready, match, ds, epoch, hash, 1UL,
                new PMDsReadyBody { BoundPort = 7801, SceneReady = true, CollisionDigest = 1u }));
            PMDsControlMessage decoded;
            PMDsControlVerifyFault fault;
            Check(!signer.VerifyRaw(unsignedPrefix, 0, unsignedPrefix.Length, out decoded, out fault),
                "B17 缺 MAC 的帧验签失败");
            CheckEq(fault, PMDsControlVerifyFault.MissingMac, "B18 分类为 MissingMac");

            // B19 载荷超限
            ExpectThrow(delegate()
            {
                PMDsControlCodec.Decode(new byte[PMDsControlWire.MaxFramePayloadBytes + 1], 0,
                    PMDsControlWire.MaxFramePayloadBytes + 1);
            }, "B19 超限载荷（> 64KiB）被拒");

            // B20 空载荷
            ExpectThrow(delegate() { PMDsControlCodec.Decode(new byte[0], 0, 0); }, "B20 空载荷被拒");

            // B21 超长 MatchId（读侧）
            string longMatch = new string('m', PMDsControlWire.MaxMatchIdBytes + 20);
            byte[] longMatchPayload = HandPayload(1u, (uint)PMDsControlMessageType.Ready, longMatch, ds, epoch, hash,
                1UL, BuildReadyBody(7801, 0x1u, true));
            ExpectThrow(delegate() { PMDsControlCodec.Decode(longMatchPayload, 0, longMatchPayload.Length); },
                "B21 超长 MatchId（" + (PMDsControlWire.MaxMatchIdBytes + 20) + " 字节）被拒");

            // B22 超长 MatchId（写侧）
            ExpectThrow(delegate()
            {
                PMDsControlCodec.EncodePrefix(PMDsControlMessage.Create(
                    PMDsControlMessageType.Ready, longMatch, ds, epoch, hash, 1UL,
                    new PMDsReadyBody { BoundPort = 7801, SceneReady = true, CollisionDigest = 1u }));
            }, "B22 写侧超长 MatchId 被拒");

            // B23 名册超过 6 人（写侧）
            PMDsBootstrapPlayer[] tooMany = new PMDsBootstrapPlayer[7];
            for (int i = 0; i < tooMany.Length; i++)
            {
                tooMany[i] = new PMDsBootstrapPlayer(new PMDsRosterIdentity(200 + i, 10 + i, 0, 1), new byte[96]);
            }

            PMDsBootstrapBody tooManyBody = new PMDsBootstrapBody();
            tooManyBody.CollisionDigest = 1u;
            tooManyBody.Players = tooMany;
            ExpectThrow(delegate()
            {
                PMDsControlCodec.EncodePrefix(PMDsControlMessage.Create(
                    PMDsControlMessageType.Bootstrap, match, ds, epoch, hash, 1UL, tooManyBody));
            }, "B23 名册 7 人（> 6）被拒");

            // B24 名册 Uid 重复
            PMDsBootstrapBody dupBody = new PMDsBootstrapBody();
            dupBody.CollisionDigest = 1u;
            dupBody.Players = new PMDsBootstrapPlayer[]
            {
                new PMDsBootstrapPlayer(new PMDsRosterIdentity(101, 1, 0, 1), new byte[96]),
                new PMDsBootstrapPlayer(new PMDsRosterIdentity(101, 2, 1, 1), new byte[96]),
            };
            ExpectThrow(delegate()
            {
                PMDsControlCodec.EncodePrefix(PMDsControlMessage.Create(
                    PMDsControlMessageType.Bootstrap, match, ds, epoch, hash, 1UL, dupBody));
            }, "B24 名册 Uid 重复被拒");

            // B25 名册 PlayerId 重复
            dupBody.Players = new PMDsBootstrapPlayer[]
            {
                new PMDsBootstrapPlayer(new PMDsRosterIdentity(101, 1, 0, 1), new byte[96]),
                new PMDsBootstrapPlayer(new PMDsRosterIdentity(102, 1, 1, 1), new byte[96]),
            };
            ExpectThrow(delegate()
            {
                PMDsControlCodec.EncodePrefix(PMDsControlMessage.Create(
                    PMDsControlMessageType.Bootstrap, match, ds, epoch, hash, 1UL, dupBody));
            }, "B25 名册 PlayerId 重复被拒");

            // B26 票据超长
            PMDsBootstrapBody ticketBody = new PMDsBootstrapBody();
            ticketBody.CollisionDigest = 1u;
            ticketBody.Players = new PMDsBootstrapPlayer[]
            {
                new PMDsBootstrapPlayer(new PMDsRosterIdentity(101, 1, 0, 1),
                    new byte[PMDsControlWire.MaxTicketBytes + 1]),
            };
            ExpectThrow(delegate()
            {
                PMDsControlCodec.EncodePrefix(PMDsControlMessage.Create(
                    PMDsControlMessageType.Bootstrap, match, ds, epoch, hash, 1UL, ticketBody));
            }, "B26 超长票据被拒");

            // B27 结算摘要超长
            ExpectThrow(delegate()
            {
                PMDsControlCodec.EncodePrefix(PMDsControlMessage.Create(
                    PMDsControlMessageType.Result, match, ds, epoch, hash, 0UL, new PMDsResultBody
                    {
                        ResultId = 1UL,
                        WinnerTeamId = 0,
                        Summary = new byte[PMDsControlWire.MaxSummaryBytes + 1],
                    }));
            }, "B27 超长结算摘要被拒");

            // B28 ResultId=0 被拒
            ExpectThrow(delegate()
            {
                PMDsControlCodec.EncodePrefix(PMDsControlMessage.Create(
                    PMDsControlMessageType.Result, match, ds, epoch, hash, 0UL, new PMDsResultBody
                    {
                        ResultId = 0UL,
                        WinnerTeamId = 0,
                    }));
            }, "B28 ResultId=0 被拒");

            // B29 Epoch=0 / ProtocolHash=0 被拒
            ExpectThrow(delegate()
            {
                PMDsControlCodec.EncodePrefix(PMDsControlMessage.Create(
                    PMDsControlMessageType.Heartbeat, match, ds, 0u, hash, 1UL,
                    new PMDsHeartbeatBody { UptimeMilliseconds = 1, PlayerCount = 1 }));
            }, "B29 Epoch=0 被拒");

            ExpectThrow(delegate()
            {
                PMDsControlCodec.EncodePrefix(PMDsControlMessage.Create(
                    PMDsControlMessageType.Heartbeat, match, ds, epoch, 0u, 1UL,
                    new PMDsHeartbeatBody { UptimeMilliseconds = 1, PlayerCount = 1 }));
            }, "B30 ProtocolHash=0 被拒");

            // B31 信封类型与 body 类型不一致
            ExpectThrow(delegate()
            {
                PMDsControlMessage.Create(PMDsControlMessageType.Ready, match, ds, epoch, hash, 1UL,
                    new PMDsExitedBody { ExitCode = 0 });
            }, "B31 信封/body 类型不一致被拒");

            // B32 Ready.BoundPort 越界（写侧）
            ExpectThrow(delegate()
            {
                PMDsControlCodec.EncodePrefix(PMDsControlMessage.Create(
                    PMDsControlMessageType.Ready, match, ds, epoch, hash, 1UL,
                    new PMDsReadyBody { BoundPort = 0, SceneReady = true, CollisionDigest = 1u }));
            }, "B32 Ready.BoundPort=0 被拒");

            // B33 未签名消息 ToString 不泄漏 MAC/票据
            string text = PMDsControlMessage.Create(PMDsControlMessageType.Ready, match, ds, epoch, hash, 1UL,
                new PMDsReadyBody { BoundPort = 7801, SceneReady = true, CollisionDigest = 1u }).ToString();
            Check(text.IndexOf(Hex(keyBytes), StringComparison.OrdinalIgnoreCase) < 0,
                "B33 消息 ToString 不含密钥");
        }

        private static void RoundTrip(PMDsControlSigner signer, string match, string ds, uint epoch, uint hash,
            PMDsControlBody body, PMDsControlMessageType type, string label)
        {
            byte[] payload = signer.Sign(PMDsControlMessage.Create(type, match, ds, epoch, hash, 5UL, body));
            PMDsControlMessage message;
            PMDsControlVerifyFault fault;
            bool ok = signer.VerifyRaw(payload, 0, payload.Length, out message, out fault);
            Check(ok && message != null && message.Type == type, label + " 签名往返 + 验签通过");
            if (ok)
            {
                CheckEq(message.MatchId, match, label + " MatchId 往返");
                CheckEq(message.RequestId, 5UL, label + " RequestId 往返");
                Check(message.HasMac, label + " 带 MAC");
            }
        }

        /// <summary>手工拼一个信封（可选带 MAC），用于构造畸形输入。</summary>
        private static byte[] HandPayload(uint version, uint type, string matchId, string dsId,
            uint epoch, uint hash, ulong requestId, byte[] bodyBytes)
        {
            PMNetWriter writer = new PMNetWriter(256);
            writer.WriteTag(1, PMWireType.Varint);
            writer.WriteUInt32(version);
            writer.WriteTag(2, PMWireType.Varint);
            writer.WriteUInt32(type);
            writer.WriteTag(3, PMWireType.LengthDelimited);
            writer.WriteStringValue(matchId);
            writer.WriteTag(4, PMWireType.LengthDelimited);
            writer.WriteStringValue(dsId);
            writer.WriteTag(5, PMWireType.Varint);
            writer.WriteUInt32(epoch);
            writer.WriteTag(6, PMWireType.Varint);
            writer.WriteUInt32(hash);
            writer.WriteTag(7, PMWireType.Varint);
            writer.WriteUInt64(requestId);
            writer.WriteTag(8, PMWireType.LengthDelimited);
            writer.WriteBytesValue(bodyBytes ?? new byte[0]);
            return writer.ToArray();
        }

        private static byte[] BuildReadyBody(int port, uint digest, bool sceneReady)
        {
            PMNetWriter writer = new PMNetWriter(32);
            writer.WriteTag(1, PMWireType.Varint);
            writer.WriteSInt32(port);
            writer.WriteTag(2, PMWireType.Varint);
            writer.WriteBool(sceneReady);
            writer.WriteTag(3, PMWireType.Varint);
            writer.WriteUInt32(digest);
            return writer.ToArray();
        }

        private static byte[] WithExtraField(byte[] prefix, int fieldNumber, ulong value)
        {
            PMNetWriter writer = new PMNetWriter(prefix.Length + 16);
            writer.WriteRawBytes(prefix, 0, prefix.Length);
            writer.WriteTag(fieldNumber, PMWireType.Varint);
            writer.WriteUInt64(value);
            return writer.ToArray();
        }

        private static byte[] DuplicateField(byte[] prefix, int fieldNumber, uint value)
        {
            PMNetWriter writer = new PMNetWriter(prefix.Length + 32);
            writer.WriteRawBytes(prefix, 0, prefix.Length);
            writer.WriteTag(fieldNumber, PMWireType.Varint);
            writer.WriteUInt32(value);
            return writer.ToArray();
        }

        /// <summary>把 epoch（字段 5）写到 matchId（字段 3）之前，构造乱序字段。</summary>
        private static byte[] OutOfOrderEpoch(uint hash, uint epoch)
        {
            PMNetWriter writer = new PMNetWriter(128);
            writer.WriteTag(1, PMWireType.Varint);
            writer.WriteUInt32(1u);
            writer.WriteTag(2, PMWireType.Varint);
            writer.WriteUInt32((uint)PMDsControlMessageType.Heartbeat);
            writer.WriteTag(5, PMWireType.Varint);
            writer.WriteUInt32(epoch);
            writer.WriteTag(6, PMWireType.Varint);
            writer.WriteUInt32(hash);
            return writer.ToArray();
        }

        // ────────────────────────────────────────────────────────────────
        // C. MAC
        // ────────────────────────────────────────────────────────────────

        private static void TestMac()
        {
            byte[] keyA = PMDsCrypto.RandomBytes(32);
            byte[] keyB = PMDsCrypto.RandomBytes(32);
            Check(!Compare(keyA, keyB), "C1 两次加密随机密钥不同");

            PMDsControlSigner signer = new PMDsControlSigner(keyA);
            PMDsControlMessage message = PMDsControlMessage.Create(PMDsControlMessageType.Heartbeat,
                "match-mac", "ds-mac", 3u, 0x55u, 1UL,
                new PMDsHeartbeatBody { UptimeMilliseconds = 10, PlayerCount = 1 });
            byte[] payload = signer.Sign(message);

            PMDsControlMessage decoded;
            PMDsControlVerifyFault fault;
            Check(signer.VerifyRaw(payload, 0, payload.Length, out decoded, out fault), "C2 正确密钥验签通过");
            CheckEq(fault, PMDsControlVerifyFault.None, "C3 无故障分类");

            PMDsControlSigner other = new PMDsControlSigner(keyB);
            PMDsControlMessage unused;
            Check(!other.VerifyRaw(payload, 0, payload.Length, out unused, out fault), "C4 错密钥验签失败");
            CheckEq(fault, PMDsControlVerifyFault.MacMismatch, "C5 分类为 MacMismatch");

            // 篡改正文（保持结构合法）：翻掉 matchId 里的一个字节
            byte[] tampered = (byte[])payload.Clone();
            tampered[0] ^= 0x01;
            Check(!signer.VerifyRaw(tampered, 0, tampered.Length, out unused, out fault),
                "C6 篡改正文后验签失败");

            // 篡改 MAC 自身
            tampered = (byte[])payload.Clone();
            tampered[tampered.Length - 1] ^= 0xFF;
            Check(!signer.VerifyRaw(tampered, 0, tampered.Length, out unused, out fault),
                "C7 篡改 MAC 后验签失败");

            CheckEq(signer.KeyFingerprint.Length, 8, "C8 密钥指纹长度为 8 个十六进制字符");
            Check(signer.ToString().IndexOf(Hex(keyA), StringComparison.OrdinalIgnoreCase) < 0,
                "C9 签名器不泄漏密钥（签名器 ToString 来自默认实现，不含密钥）");

            // 常量时间比较
            byte[] x = new byte[] { 1, 2, 3, 4 };
            byte[] y = new byte[] { 1, 2, 3, 4 };
            byte[] z = new byte[] { 1, 2, 3, 5 };
            Check(PMDsCrypto.FixedTimeEquals(x, 0, y, 0, 4), "C10 相同内容判等");
            Check(!PMDsCrypto.FixedTimeEquals(x, 0, z, 0, 4), "C11 不同内容判不等");
            Check(!PMDsCrypto.FixedTimeEquals(x, 0, y, 0, 5), "C12 超出长度判不等（不抛）");
            Check(!PMDsCrypto.FixedTimeEquals(x, 0, null, 0, 4), "C13 null 判不等（不抛）");
            Check(PMDsCrypto.FixedTimeEquals(x, 1, y, 1, 3), "C14 偏移量正确生效");

            // HMAC 与已知测试向量对照（RFC 4231 test case 1）
            byte[] hmacKey = new byte[20];
            for (int i = 0; i < hmacKey.Length; i++) { hmacKey[i] = 0x0b; }
            byte[] hmacData = Encoding.ASCII.GetBytes("Hi There");
            byte[] expected = new byte[]
            {
                0xb0, 0x34, 0x4c, 0x61, 0xd8, 0xdb, 0x38, 0x53, 0x5c, 0xa8, 0xaf, 0xce, 0xaf, 0x0b, 0xf1, 0x2b,
                0x88, 0x1d, 0xc2, 0x00, 0xc9, 0x83, 0x3d, 0xa7, 0x26, 0xe9, 0x37, 0x6c, 0x2e, 0x32, 0xcf, 0xf7,
            };
            CheckBytes(PMDsCrypto.HmacSha256(hmacKey, hmacData), expected, "C15 HMAC-SHA256 对齐 RFC 4231 向量");
        }

        private static bool Compare(byte[] a, byte[] b)
        {
            return a.Length == b.Length && PMDsCrypto.FixedTimeEquals(a, 0, b, 0, a.Length);
        }

        // ────────────────────────────────────────────────────────────────
        // D. 票据
        // ────────────────────────────────────────────────────────────────

        private static void TestTicket()
        {
            const string match = "match-ticket";
            const string ds = "ds-ticket";
            const uint epoch = 7u;
            const uint hash = 0x0BADF00Du;
            const long now = 1700000000L;

            PMDsMatchKey key = PMDsMatchKey.Create();
            CheckEq(key.ExportKeyBytes().Length, 32, "D1 每局密钥 256 位");
            Check(!Compare(key.ExportKeyBytes(), PMDsMatchKey.Create().ExportKeyBytes()), "D2 两次生成不同密钥");
            ExpectAnyThrow(delegate() { PMDsMatchKey.FromBytes(new byte[16]); }, "D3 长度不是 32 字节被拒");

            PMDsTicketExpectation expectation = new PMDsTicketExpectation(match, ds, epoch, hash);
            PMDsTicketIssuer issuer = key.CreateTicketIssuer(match, ds, epoch, hash);
            PMDsTicketVerifier verifier = key.CreateTicketVerifier(expectation);

            PMDsRosterIdentity identity = new PMDsRosterIdentity(101, 1, 0, 3);
            PMDsTicket ticket = issuer.Issue(identity, now);
            CheckEq(ticket.ExpiresAtUnixSeconds, now + 120, "D4 默认有效期 120 秒");

            byte[] ticketBytes = ticket.ExportTicketBytes();
            Check(ticketBytes.Length <= PMDsTicketWire.MaxTicketBytes,
                "D5 票据长度 " + ticketBytes.Length + " ≤ " + PMDsTicketWire.MaxTicketBytes);

            PMDsTicketVerification ok = verifier.Verify(ticketBytes, 0, ticketBytes.Length, now, identity);
            CheckEq(ok.Verdict, PMDsTicketVerdict.Valid, "D6 正常票据通过");
            CheckEq(ok.Identity.PlayerId, 1, "D7 票据携带身份");

            // 边界：到期前一秒有效，到期时刻失效（排他）
            CheckEq(verifier.Verify(ticketBytes, 0, ticketBytes.Length, now + 119, identity).Verdict,
                PMDsTicketVerdict.Valid, "D8 到期前 1 秒有效");
            CheckEq(verifier.Verify(ticketBytes, 0, ticketBytes.Length, now + 120, identity).Verdict,
                PMDsTicketVerdict.Expired, "D9 到期时刻（排他）失效");

            // 错密钥
            PMDsTicketVerifier wrongKeyVerifier = PMDsMatchKey.Create().CreateTicketVerifier(expectation);
            CheckEq(wrongKeyVerifier.Verify(ticketBytes, 0, ticketBytes.Length, now, identity).Verdict,
                PMDsTicketVerdict.MacMismatch, "D10 错密钥 → MacMismatch");

            // 身份不符
            CheckEq(verifier.Verify(ticketBytes, 0, ticketBytes.Length, now, new PMDsRosterIdentity(101, 2, 0, 3)).Verdict,
                PMDsTicketVerdict.NotInRoster, "D11 身份不符 → NotInRoster");

            // 错局 / 错世代 / 错摘要 / 错 ds
            CheckEq(key.CreateTicketVerifier(new PMDsTicketExpectation("other-match", ds, epoch, hash))
                .Verify(ticketBytes, 0, ticketBytes.Length, now, identity).Verdict,
                PMDsTicketVerdict.WrongMatch, "D12 错局 → WrongMatch");
            CheckEq(key.CreateTicketVerifier(new PMDsTicketExpectation(match, ds, epoch + 1, hash))
                .Verify(ticketBytes, 0, ticketBytes.Length, now, identity).Verdict,
                PMDsTicketVerdict.WrongEpoch, "D13 错世代 → WrongEpoch");
            CheckEq(key.CreateTicketVerifier(new PMDsTicketExpectation(match, ds, epoch, hash + 1))
                .Verify(ticketBytes, 0, ticketBytes.Length, now, identity).Verdict,
                PMDsTicketVerdict.WrongProtocolHash, "D14 错摘要 → WrongProtocolHash");
            CheckEq(key.CreateTicketVerifier(new PMDsTicketExpectation(match, "other-ds", epoch, hash))
                .Verify(ticketBytes, 0, ticketBytes.Length, now, identity).Verdict,
                PMDsTicketVerdict.WrongDsId, "D15 错 DsId → WrongDsId");

            // 篡改
            byte[] tampered = (byte[])ticketBytes.Clone();
            tampered[tampered.Length - 40] ^= 0x01;
            CheckEq(verifier.Verify(tampered, 0, tampered.Length, now, identity).Verdict,
                PMDsTicketVerdict.MacMismatch, "D16 篡改票据 → MacMismatch");

            // 截断
            CheckEq(verifier.Verify(ticketBytes, 0, ticketBytes.Length - 1, now, identity).Verdict,
                PMDsTicketVerdict.Malformed, "D17 截断票据 → Malformed");

            // 魔数不符
            byte[] wrongMagic = (byte[])ticketBytes.Clone();
            wrongMagic[0] = (byte)'X';
            CheckEq(verifier.Verify(wrongMagic, 0, wrongMagic.Length, now, identity).Verdict,
                PMDsTicketVerdict.Malformed, "D18 魔数不符 → Malformed");

            // 版本不符
            byte[] badVersion = (byte[])ticketBytes.Clone();
            badVersion[4] = 9;
            CheckEq(verifier.Verify(badVersion, 0, badVersion.Length, now, identity).Verdict,
                PMDsTicketVerdict.BadVersion, "D19 版本不符 → BadVersion");

            // 有效期策略：重新签名一个 700 秒有效期的票据（超过 600 上限）
            byte[] longLived = ReissueWithExpiry(ticketBytes, now, now + 700, key.ExportKeyBytes());
            CheckEq(verifier.Verify(longLived, 0, longLived.Length, now, identity).Verdict,
                PMDsTicketVerdict.LifetimePolicy, "D20 有效期超上限 → LifetimePolicy");

            // 签发时间在未来（超时钟偏差）
            byte[] fromFuture = ReissueWithTimes(ticketBytes, now + 3600, now + 3700, key.ExportKeyBytes());
            CheckEq(verifier.Verify(fromFuture, 0, fromFuture.Length, now, identity).Verdict,
                PMDsTicketVerdict.NotYetValid, "D21 签发时间在未来 → NotYetValid");

            // 幂等重发：同一身份同一时刻 → 字节完全相同
            byte[] again = issuer.Issue(identity, now).ExportTicketBytes();
            CheckBytes(again, ticketBytes, "D22 同一会话重发返回完全相同的票据字节");
            CheckEq(issuer.IssuedCount, 1, "D23 幂等重发不新增票据");

            // 过期后重签：换新 Nonce（字节不同）
            byte[] renewed = issuer.Issue(identity, now + 200).ExportTicketBytes();
            Check(!Compare(renewed, ticketBytes), "D24 过期后重签产生新票据");

            // 撤销
            Check(issuer.Revoke(101), "D25 撤销返回命中");
            CheckEq(issuer.IssuedCount, 0, "D26 撤销后无票据");

            // 不打印秘密
            byte[] keyBytes = key.ExportKeyBytes();
            Check(key.ToString().IndexOf(Hex(keyBytes), StringComparison.OrdinalIgnoreCase) < 0,
                "D27 密钥 ToString 不含密钥字节");
            byte[] mac = new byte[32];
            Buffer.BlockCopy(ticketBytes, ticketBytes.Length - 32, mac, 0, 32);
            Check(ticket.ToString().IndexOf(Hex(mac), StringComparison.OrdinalIgnoreCase) < 0,
                "D28 票据 ToString 不含 MAC");
            Check(ticket.ToString().IndexOf(Hex(ticketBytes), StringComparison.OrdinalIgnoreCase) < 0,
                "D29 票据 ToString 不含完整票据");
            Check(key.ToString().IndexOf("fp=", StringComparison.Ordinal) >= 0, "D30 密钥 ToString 给出指纹");

            // 头尾偏移（避免 offset 被忽略）
            byte[] padded = new byte[ticketBytes.Length + 8];
            Buffer.BlockCopy(ticketBytes, 0, padded, 8, ticketBytes.Length);
            CheckEq(verifier.Verify(padded, 8, ticketBytes.Length, now, identity).Verdict,
                PMDsTicketVerdict.Valid, "D31 带偏移量校验等价");

            // 名册无关性：issuer 只绑身份字段，不校验名册（名册由协调器负责）
            PMDsTicket outsider = issuer.Issue(new PMDsRosterIdentity(999, 99, 0, 1), now);
            CheckEq(verifier.Verify(outsider.ExportTicketBytes(), 0, outsider.ExportTicketBytes().Length, now,
                outsider.Identity).Verdict, PMDsTicketVerdict.Valid, "D32 签发器不校验名册（由协调器校验）");
        }

        private static byte[] ReissueWithExpiry(byte[] ticket, long issuedAt, long expiresAt, byte[] keyBytes)
        {
            return ReissueWithTimes(ticket, issuedAt, expiresAt, keyBytes);
        }

        /// <summary>就地改写票据里的 issuedAt/expiresAt 并重算 MAC（用于构造策略类负向输入）。</summary>
        private static byte[] ReissueWithTimes(byte[] ticket, long issuedAt, long expiresAt, byte[] keyBytes)
        {
            byte[] copy = (byte[])ticket.Clone();
            int pos = 6;
            int matchLen = copy[pos];
            pos += 1 + matchLen;
            int dsLen = copy[pos];
            pos += 1 + dsLen;
            pos += 4 + 4 + 4 + 4 + 4 + 4;
            WriteI64(copy, pos, issuedAt);
            WriteI64(copy, pos + 8, expiresAt);

            int macStart = copy.Length - 32;
            byte[] mac = PMDsCrypto.HmacSha256(keyBytes, copy, 0, macStart);
            Buffer.BlockCopy(mac, 0, copy, macStart, 32);
            return copy;
        }

        private static void WriteI64(byte[] buffer, int offset, long value)
        {
            ulong raw = unchecked((ulong)value);
            for (int i = 0; i < 8; i++)
            {
                buffer[offset + i] = (byte)((raw >> (i * 8)) & 0xFF);
            }
        }

        // ────────────────────────────────────────────────────────────────
        // E. 引导文件
        // ────────────────────────────────────────────────────────────────

        private static void TestBootstrapDocument()
        {
            FakeClock clock = new FakeClock();
            FakeLauncher launcher = new FakeLauncher();
            RecordingSink sink = new RecordingSink();
            PMDsCoordinatorOptions options = MakeOptions();
            PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, options,
                MakeRequest("match-boot", "ds-boot", 5u, 0xAABBCCDDu, 0x12345678u, DefaultRoster));

            byte[] document = coordinator.ExportBootstrapDocument();
            Check(document.Length > PMDsBootstrapDocument.HeaderBytes + PMDsMatchKey.KeyBytes,
                "E1 引导文件非空且含密钥段");

            PMDsBootstrappedMatch boot;
            string error;
            Check(PMDsBootstrapDocument.TryDecode(document, out boot, out error), "E2 引导文件可解析");
            CheckEq(boot.MatchId, "match-boot", "E3 引导 MatchId");
            CheckEq(boot.DsId, "ds-boot", "E4 引导 DsId");
            CheckEq(boot.Epoch, 5u, "E5 引导 Epoch");
            CheckEq(boot.ProtocolHash, 0xAABBCCDDu, "E6 引导 ProtocolHash");
            CheckEq(boot.CollisionDigest, 0x12345678u, "E7 引导 CollisionDigest");
            CheckEq(boot.PlayerCount, DefaultRoster.Length, "E8 引导名册人数");

            // DS 侧验收路径：用文件里的密钥逐张验签名册票据
            PMDsTicketVerifier verifier = boot.Key.CreateTicketVerifier(
                new PMDsTicketExpectation(boot.MatchId, boot.DsId, boot.Epoch, boot.ProtocolHash));
            long nowSeconds = clock.Now / 1000L;
            int valid = 0;
            for (int i = 0; i < boot.Bootstrap.AsBootstrap.Players.Length; i++)
            {
                PMDsBootstrapPlayer player = boot.Bootstrap.AsBootstrap.Players[i];
                PMDsTicketVerification result = verifier.Verify(
                    player.Ticket, 0, player.Ticket.Length, nowSeconds, player.Identity);
                if (result.IsValid) { valid++; }
            }

            CheckEq(valid, DefaultRoster.Length, "E9 引导文件里的每张票据都能被该密钥验签");

            // 另一把密钥验不过
            PMDsTicketVerifier otherVerifier = PMDsMatchKey.Create().CreateTicketVerifier(
                new PMDsTicketExpectation(boot.MatchId, boot.DsId, boot.Epoch, boot.ProtocolHash));
            CheckEq(otherVerifier.Verify(boot.Bootstrap.AsBootstrap.Players[0].Ticket, 0,
                boot.Bootstrap.AsBootstrap.Players[0].Ticket.Length, nowSeconds,
                boot.Bootstrap.AsBootstrap.Players[0].Identity).Verdict,
                PMDsTicketVerdict.MacMismatch, "E10 换密钥验签失败");

            // 错魔数
            byte[] badMagic = (byte[])document.Clone();
            badMagic[0] = (byte)'Z';
            CheckNeg(!PMDsBootstrapDocument.TryDecode(badMagic, out boot, out error), "E11 错魔数被拒");

            // 截断
            CheckNeg(!PMDsBootstrapDocument.TryDecode(
                (byte[])Resize(document, document.Length - 1), out boot, out error), "E12 截断被拒");

            // 声明长度与实际不符
            byte[] badLength = (byte[])document.Clone();
            badLength[PMDsBootstrapDocument.HeaderBytes] ^= 0x01;
            CheckNeg(!PMDsBootstrapDocument.TryDecode(badLength, out boot, out error), "E13 长度不一致被拒");

            // 版本不符
            byte[] badVersion = (byte[])document.Clone();
            badVersion[8] = 0x7F;
            CheckNeg(!PMDsBootstrapDocument.TryDecode(badVersion, out boot, out error), "E14 版本不符被拒");

            coordinator.Dispose();
        }

        private static byte[] Resize(byte[] source, int length)
        {
            byte[] result = new byte[length];
            Buffer.BlockCopy(source, 0, result, 0, Math.Min(length, source.Length));
            return result;
        }

        // ────────────────────────────────────────────────────────────────
        // F. 协调器正常路径
        // ────────────────────────────────────────────────────────────────

        private static void TestCoordinatorHappyPath()
        {
            FakeClock clock = new FakeClock();
            FakeLauncher launcher = new FakeLauncher();
            RecordingSink sink = new RecordingSink();
            PMDsCoordinatorOptions options = MakeOptions();
            PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, options,
                MakeRequest("match-f", "ds-f", 7u, 0xAABBCCDDu, 0x0F0F0F0Fu, DefaultRoster));

            CheckEq(coordinator.State, PMDsSessionState.Allocated, "F1 分配后状态 Allocated");
            Check(coordinator.AllocatedPort >= options.PortRangeFirst && coordinator.AllocatedPort <= options.PortRangeLast,
                "F2 端口来自配置范围：" + coordinator.AllocatedPort);
            CheckEq(coordinator.PortsInUse, 1, "F3 端口池在用 1");
            CheckEq(coordinator.RosterCount, 4, "F4 名册 4 人");
            Check(!string.IsNullOrEmpty(coordinator.MatchKeyFingerprint), "F5 已生成每局密钥");
            CheckEq(coordinator.Allocate(MakeRequest("m2", "d2", 8u, 0xAABBCCDDu, 1u, DefaultRoster)).Outcome,
                PMDsCoordinatorOutcome.RejectedState, "F6 重复分配被拒");

            DsView ds = DsView.FromCoordinator(coordinator);
            int port = coordinator.AllocatedPort;

            // 启动
            CheckEq(coordinator.BeginStart().Outcome, PMDsCoordinatorOutcome.Applied, "F7 启动被接受");
            CheckEq(coordinator.State, PMDsSessionState.Starting, "F8 状态 Starting");
            CheckEq(sink.Count(PMDsCoordinatorEffect.ProcessStartRequested), 1, "F9 发出一次启动副作用");
            CheckEq(launcher.StartCount, 1, "F10 启动器被调用一次");
            CheckEq(launcher.LastRequest.Arguments.Length, 5, "F11 参数逐项传递（5 个）");
            CheckEq(coordinator.ProcessId, 4242, "F12 记录进程 ID");

            // Ready 负向：端口/摘要/sceneReady
            CheckEq(coordinator.OnControlPayload(ds.Ready(port + 1, ds.CollisionDigest, true), 0,
                ds.Ready(port + 1, ds.CollisionDigest, true).Length).Outcome,
                PMDsCoordinatorOutcome.RejectedNotReady, "F13 端口不一致被拒");
            byte[] wrongDigest = ds.Ready(port, ds.CollisionDigest + 1u, true);
            CheckEq(coordinator.OnControlPayload(wrongDigest, 0, wrongDigest.Length).Outcome,
                PMDsCoordinatorOutcome.RejectedNotReady, "F14 摘要不一致被拒");
            byte[] notReady = ds.Ready(port, ds.CollisionDigest, false);
            CheckEq(coordinator.OnControlPayload(notReady, 0, notReady.Length).Outcome,
                PMDsCoordinatorOutcome.RejectedNotReady, "F15 sceneReady=false 被拒");
            CheckEq(coordinator.State, PMDsSessionState.Starting, "F16 三次拒绝后状态未动");
            CheckEq(sink.Count(PMDsCoordinatorEffect.ReadyAddressPublished), 0, "F17 未发布地址");
            CheckEq(coordinator.Counters.FramesRejectedNotReady, 3L, "F18 拒绝计数为 3");

            // Ready 正向
            byte[] ready = ds.Ready(port, ds.CollisionDigest, true);
            CheckEq(coordinator.OnControlPayload(ready, 0, ready.Length).Outcome,
                PMDsCoordinatorOutcome.Applied, "F19 合法 Ready 被接受");
            CheckEq(coordinator.State, PMDsSessionState.Ready, "F20 状态 Ready");
            CheckEq(sink.Count(PMDsCoordinatorEffect.ReadyAddressPublished), 1, "F21 地址发布一次");
            CheckEq(sink.Last(PMDsCoordinatorEffect.ReadyAddressPublished).Port, port, "F22 发布端口等于分配端口");

            // 重复 Ready 不重复发布
            CheckEq(coordinator.OnControlPayload(ready, 0, ready.Length).Outcome,
                PMDsCoordinatorOutcome.Duplicate, "F23 重复 Ready 幂等");
            CheckEq(sink.Count(PMDsCoordinatorEffect.ReadyAddressPublished), 1, "F24 仍只发布一次");
            CheckEq(coordinator.Counters.ReadyDuplicates, 1L, "F25 重复 Ready 计数");

            // Running
            CheckEq(coordinator.MarkRunning().Outcome, PMDsCoordinatorOutcome.Applied, "F26 进入 Running");
            CheckEq(coordinator.State, PMDsSessionState.Running, "F27 状态 Running");
            CheckEq(coordinator.MarkRunning().Outcome, PMDsCoordinatorOutcome.Duplicate, "F28 重复 MarkRunning 幂等");

            // 心跳
            byte[] heartbeat = ds.Heartbeat(1000, 4);
            CheckEq(coordinator.OnControlPayload(heartbeat, 0, heartbeat.Length).Outcome,
                PMDsCoordinatorOutcome.Applied, "F29 心跳被接受");
            CheckEq(coordinator.Counters.Heartbeats, 1L, "F30 心跳计数");

            // 结果
            byte[] summary = Encoding.ASCII.GetBytes("team0-wins");
            byte[] result = ds.Result(0xABCDEF01UL, 0, summary);
            CheckEq(coordinator.OnControlPayload(result, 0, result.Length).Outcome,
                PMDsCoordinatorOutcome.Applied, "F31 结果被接受");
            CheckEq(coordinator.State, PMDsSessionState.ResultPending, "F32 状态 ResultPending");
            CheckEq(sink.Count(PMDsCoordinatorEffect.ResultAccepted), 1, "F33 权威结果入口触发一次");
            CheckEq(sink.Last(PMDsCoordinatorEffect.ResultAccepted).ResultId, 0xABCDEF01UL, "F34 结果 ID 透传");
            CheckEq(sink.Last(PMDsCoordinatorEffect.ResultAccepted).WinnerTeamId, 0, "F35 胜方透传");
            List<byte[]> acks = sink.Payloads(PMDsControlMessageType.ResultAck);
            CheckEq(acks.Count, 1, "F36 发出一次 ResultAck");
            PMDsControlMessage ackMessage = PMDsControlCodec.Decode(acks[0], 0, acks[0].Length);
            CheckEq(ackMessage.AsResultAck.ResultId, 0xABCDEF01UL, "F37 ResultAck 携带同一 ResultId");
            Check(ds.Signer.Verify(ackMessage), "F38 ResultAck 由本局密钥签名");

            // 重复结果（Ack 丢失场景）：同一内容 → 同一 Ack 字节
            CheckEq(coordinator.OnControlPayload(result, 0, result.Length).Outcome,
                PMDsCoordinatorOutcome.Duplicate, "F39 重复结果幂等");
            acks = sink.Payloads(PMDsControlMessageType.ResultAck);
            CheckEq(acks.Count, 2, "F40 重复结果重发 Ack");
            CheckBytes(acks[1], acks[0], "F41 重发的 Ack 与首答字节一致");
            CheckEq(sink.Count(PMDsCoordinatorEffect.ResultAccepted), 1, "F42 结果只通知业务一次");

            // 冲突结果
            byte[] conflicting = ds.Result(0xABCDEF01UL, 1, summary);
            CheckEq(coordinator.OnControlPayload(conflicting, 0, conflicting.Length).Outcome,
                PMDsCoordinatorOutcome.RejectedConflict, "F43 同 ID 不同内容被拒");
            byte[] otherId = ds.Result(0xABCDEF02UL, 0, summary);
            CheckEq(coordinator.OnControlPayload(otherId, 0, otherId.Length).Outcome,
                PMDsCoordinatorOutcome.RejectedConflict, "F44 另一个 ResultId 被拒");
            CheckEq(coordinator.Counters.ResultConflicts, 2L, "F45 冲突计数");

            // ResultAck 定时重发（虚拟时钟）：3 次后进入 ResultCommitted
            // —— 注意：该状态只代表「**本地受理**并放弃重传」，它**不是**对端已确认：
            //    协议里没有 DS→Lobby 的 ResultAck 确认消息，本路径的唯一输入是本地发送计数。
            clock.Advance(1000);
            CheckEq(coordinator.Tick().State, PMDsSessionState.ResultPending, "F46 第一次重发后仍在 ResultPending");
            clock.Advance(1000);
            coordinator.Tick();
            clock.Advance(1000);
            coordinator.Tick();
            CheckEq(coordinator.Counters.ResultAckRetransmits, 3L, "F47 重发 3 次");
            acks = sink.Payloads(PMDsControlMessageType.ResultAck);
            CheckEq(acks.Count, 5, "F48 总 Ack 次数 = 首答 1 + 重复驱动 1 + 定时 3");
            CheckBytes(acks[4], acks[0], "F49 所有重发字节与首答一致");

            clock.Advance(1000);
            coordinator.Tick();
            CheckEq(coordinator.State, PMDsSessionState.ResultCommitted,
                "F50 重发预算用尽 → ResultCommitted（本地受理）");
            CheckEq(coordinator.Counters.ResultAckAbandoned, 1L, "F50b 放弃重传计数为 1");
            Check(!coordinator.PeerExitObserved, "F50c 未由重发次数推断出对端确认");
            CheckEq(sink.Count(PMDsCoordinatorEffect.GracefulShutdownRequested), 1, "F51 发出优雅关闭请求");
            PMDsControlMessage shutdown = PMDsControlCodec.Decode(
                sink.Last(PMDsCoordinatorEffect.GracefulShutdownRequested).ControlPayload, 0,
                sink.Last(PMDsCoordinatorEffect.GracefulShutdownRequested).ControlPayload.Length);
            CheckEq(shutdown.Type, PMDsControlMessageType.Shutdown, "F52 关闭消息类型正确");
            Check(ds.Signer.Verify(shutdown), "F53 关闭消息由本局密钥签名");

            // 收尾：进程退出
            launcher.Process.Exit(0);
            CheckEq(coordinator.Tick().Outcome, PMDsCoordinatorOutcome.Applied, "F54 Tick 发现进程退出");
            CheckEq(coordinator.State, PMDsSessionState.Exited, "F55 状态 Exited");
            CheckEq(sink.Count(PMDsCoordinatorEffect.ResourcesReleased), 1, "F56 释放资源一次");
            CheckEq(coordinator.PortsInUse, 0, "F57 端口池归零");
            CheckEq(coordinator.AllocatedPort, 0, "F58 分配端口清空");
            CheckEq(coordinator.RosterCount, 0, "F59 名册释放");
            CheckEq(sink.Count(PMDsCoordinatorEffect.SessionEnded), 1, "F60 会话结束事件一次");
            CheckEq(coordinator.Counters.ProcessExits, 1L, "F61 进程退出计数");

            // 墓碑
            PMDsResultTombstone tombstone;
            Check(coordinator.TryGetTombstone(0xABCDEF01UL, out tombstone), "F62 结果写入墓碑");
            CheckEq(tombstone.WinnerTeamId, 0, "F63 墓碑胜方");
            Check(coordinator.TombstoneCount >= 1, "F64 墓碑非空");

            // 终态后重复结果：由墓碑服务
            int ackCountBefore = sink.Payloads(PMDsControlMessageType.ResultAck).Count;
            CheckEq(coordinator.OnControlPayload(result, 0, result.Length).Outcome,
                PMDsCoordinatorOutcome.Duplicate, "F65 终态后重复结果由墓碑服务");
            acks = sink.Payloads(PMDsControlMessageType.ResultAck);
            CheckEq(acks.Count, ackCountBefore + 1, "F66 墓碑重发 Ack");
            CheckBytes(acks[acks.Count - 1], acks[0], "F67 墓碑 Ack 与首答一致");
            CheckEq(coordinator.OnControlPayload(conflicting, 0, conflicting.Length).Outcome,
                PMDsCoordinatorOutcome.RejectedConflict, "F68 终态后冲突内容仍被拒");
            CheckEq(coordinator.OnControlPayload(ds.Result(0xDEAD0001UL, 0, summary), 0,
                ds.Result(0xDEAD0001UL, 0, summary).Length).Outcome,
                PMDsCoordinatorOutcome.RejectedState, "F69 终态后无墓碑的新结果被拒");

            // 终态幂等
            CheckEq(coordinator.OnProcessExited(0).Outcome, PMDsCoordinatorOutcome.Duplicate, "F70 重复进程退出幂等");
            CheckEq(coordinator.Tick().Outcome, PMDsCoordinatorOutcome.Ignored, "F71 终态 Tick 无操作");
            CheckEq(sink.Count(PMDsCoordinatorEffect.ResourcesReleased), 1, "F72 释放只发生一次");
            coordinator.Dispose();
            coordinator.Dispose();
            Check(true, "F73 Dispose 幂等（不抛）");

            // 端口释放的可复核证据：独立端口池上确认同一端口已空闲
            PMDsPortPool pool = new PMDsPortPool(options.PortRangeFirst, options.PortRangeLast);
            int bumped;
            Check(pool.TryAcquire(out bumped), "F74 独立端口池可分配");
            CheckEq(bumped, options.PortRangeFirst, "F75 端口池分配从最小开始（顺序确定）");
        }

        // ────────────────────────────────────────────────────────────────
        // G. 故障注入
        // ────────────────────────────────────────────────────────────────

        private static void TestCoordinatorFailures()
        {
            // G1 启动失败
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();
                launcher.FailStart = true;
                launcher.FailFault = PMDsProcessFault.ExecutableNotFound;
                RecordingSink sink = new RecordingSink();
                PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, MakeOptions(),
                    MakeRequest("match-g1", "ds-g1", 1u, 0xAABBCCDDu, 1u, DefaultRoster));

                PMDsCoordinatorReply reply = coordinator.BeginStart();
                CheckEq(reply.Outcome, PMDsCoordinatorOutcome.Applied, "G1 启动失败也是一次有效终态转移");
                CheckEq(coordinator.State, PMDsSessionState.Failed, "G2 启动失败 → Failed");
                CheckEq(sink.Count(PMDsCoordinatorEffect.ResourcesReleased), 1, "G3 启动失败释放资源");
                CheckEq(coordinator.PortsInUse, 0, "G4 启动失败不留端口占用");
                Check(sink.Count(PMDsCoordinatorEffect.ReadyAddressPublished) == 0, "G5 启动失败绝不发布地址");
                CheckEq(sink.Count(PMDsCoordinatorEffect.SessionEnded), 1, "G6 结束事件一次");
                coordinator.Dispose();
            }

            // G7 启动超时（虚拟时钟）
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();
                RecordingSink sink = new RecordingSink();
                PMDsCoordinatorOptions options = MakeOptions();
                PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, options,
                    MakeRequest("match-g7", "ds-g7", 1u, 0xAABBCCDDu, 1u, DefaultRoster));
                coordinator.BeginStart();

                clock.Advance(29999);
                coordinator.Tick();
                CheckEq(coordinator.State, PMDsSessionState.Starting, "G7 29.999 秒时仍在等待");

                clock.Advance(1);
                coordinator.Tick();
                CheckEq(coordinator.State, PMDsSessionState.TimedOut, "G8 30 秒到期 → TimedOut");
                CheckEq(coordinator.Counters.StartTimeouts, 1L, "G9 启动超时计数");
                Check(launcher.Process.Killed, "G10 超时进程被强杀");
                CheckEq(coordinator.PortsInUse, 0, "G11 超时释放端口");
                coordinator.Dispose();
            }

            // G12 就绪前崩溃
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();
                RecordingSink sink = new RecordingSink();
                PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, MakeOptions(),
                    MakeRequest("match-g12", "ds-g12", 1u, 0xAABBCCDDu, 1u, DefaultRoster));
                coordinator.BeginStart();
                launcher.Process.Exit(3);
                coordinator.Tick();
                CheckEq(coordinator.State, PMDsSessionState.Failed, "G12 就绪前崩溃 → Failed");
                Check(coordinator.LastReason != null && coordinator.LastReason.IndexOf("就绪前", StringComparison.Ordinal) >= 0,
                    "G13 结束原因标明「就绪前」：" + coordinator.LastReason);
                CheckEq(coordinator.PortsInUse, 0, "G14 崩溃释放端口");
                coordinator.Dispose();
            }

            // G15 运行中崩溃 + 日志不是就绪证据
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();
                RecordingSink sink = new RecordingSink();
                PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, MakeOptions(),
                    MakeRequest("match-g15", "ds-g15", 1u, 0xAABBCCDDu, 1u, DefaultRoster));

                // 进程「打印」了就绪日志，但没有任何合法控制帧
                launcher.Process.EmitStdout("scene loaded, READY, listening on port 7801\r\n");
                launcher.Process.EmitStderr("warmup done\r\n");
                coordinator.BeginStart();
                coordinator.Tick();
                CheckEq(coordinator.State, PMDsSessionState.Starting, "G15 日志字符串不构成就绪");
                CheckEq(sink.Count(PMDsCoordinatorEffect.ReadyAddressPublished), 0, "G16 未因日志发布地址");

                DsView ds = DsView.FromCoordinator(coordinator);
                byte[] ready = ds.Ready(coordinator.AllocatedPort, ds.CollisionDigest, true);
                coordinator.OnControlPayload(ready, 0, ready.Length);
                coordinator.MarkRunning();
                CheckEq(coordinator.State, PMDsSessionState.Running, "G17 合法 Ready 后才进入 Running");

                launcher.Process.Exit(9);
                coordinator.Tick();
                CheckEq(coordinator.State, PMDsSessionState.Failed, "G18 运行中崩溃 → Failed");
                Check(coordinator.LastReason.IndexOf("异常退出", StringComparison.Ordinal) >= 0,
                    "G19 原因标明异常退出：" + coordinator.LastReason);
                coordinator.Dispose();
            }

            // G20 心跳超时
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();
                RecordingSink sink = new RecordingSink();
                PMDsCoordinatorOptions options = MakeOptions();
                PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, options,
                    MakeRequest("match-g20", "ds-g20", 1u, 0xAABBCCDDu, 1u, DefaultRoster));
                coordinator.BeginStart();
                DsView ds = DsView.FromCoordinator(coordinator);
                byte[] ready = ds.Ready(coordinator.AllocatedPort, ds.CollisionDigest, true);
                coordinator.OnControlPayload(ready, 0, ready.Length);
                coordinator.MarkRunning();

                clock.Advance(14999);
                coordinator.Tick();
                CheckEq(coordinator.State, PMDsSessionState.Running, "G20 14.999 秒无心跳仍存活");

                clock.Advance(1);
                coordinator.Tick();
                CheckEq(coordinator.State, PMDsSessionState.TimedOut, "G21 心跳超时 → TimedOut");
                CheckEq(coordinator.Counters.HeartbeatTimeouts, 1L, "G22 心跳超时计数");
                Check(launcher.Process.Killed, "G23 心跳超时强杀");
                coordinator.Dispose();
            }

            // G24 心跳续命（每次心跳重置窗口）
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();
                RecordingSink sink = new RecordingSink();
                PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, MakeOptions(),
                    MakeRequest("match-g24", "ds-g24", 1u, 0xAABBCCDDu, 1u, DefaultRoster));
                coordinator.BeginStart();
                DsView ds = DsView.FromCoordinator(coordinator);
                byte[] ready = ds.Ready(coordinator.AllocatedPort, ds.CollisionDigest, true);
                coordinator.OnControlPayload(ready, 0, ready.Length);
                coordinator.MarkRunning();

                for (int i = 0; i < 5; i++)
                {
                    clock.Advance(10000);
                    byte[] heartbeat = ds.Heartbeat(1000 * (i + 1), 4);
                    coordinator.OnControlPayload(heartbeat, 0, heartbeat.Length);
                    coordinator.Tick();
                }

                CheckEq(coordinator.State, PMDsSessionState.Running, "G24 持续心跳下不会误判超时");
                CheckEq(coordinator.Counters.Heartbeats, 5L, "G25 心跳累计");
                coordinator.Dispose();
            }

            // G26 DS 上报协议错误
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();
                RecordingSink sink = new RecordingSink();
                PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, MakeOptions(),
                    MakeRequest("match-g26", "ds-g26", 1u, 0xAABBCCDDu, 1u, DefaultRoster));
                coordinator.BeginStart();
                DsView ds = DsView.FromCoordinator(coordinator);
                byte[] ready = ds.Ready(coordinator.AllocatedPort, ds.CollisionDigest, true);
                coordinator.OnControlPayload(ready, 0, ready.Length);
                coordinator.MarkRunning();

                byte[] error = ds.Error(7u, "collision digest mismatch");
                CheckEq(coordinator.OnControlPayload(error, 0, error.Length).Outcome,
                    PMDsCoordinatorOutcome.Applied, "G26 Error 消息被处理");
                CheckEq(coordinator.State, PMDsSessionState.Failed, "G27 Error → Failed");
                Check(coordinator.LastReason.IndexOf("collision digest", StringComparison.Ordinal) >= 0,
                    "G28 错误文本透传到结束原因");
                coordinator.Dispose();
            }

            // G29 宿主请求关闭（无结果）
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();
                RecordingSink sink = new RecordingSink();
                PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, MakeOptions(),
                    MakeRequest("match-g29", "ds-g29", 1u, 0xAABBCCDDu, 1u, DefaultRoster));
                coordinator.BeginStart();
                DsView ds = DsView.FromCoordinator(coordinator);
                byte[] ready = ds.Ready(coordinator.AllocatedPort, ds.CollisionDigest, true);
                coordinator.OnControlPayload(ready, 0, ready.Length);
                coordinator.MarkRunning();

                CheckEq(coordinator.RequestShutdown(42u).Outcome, PMDsCoordinatorOutcome.Applied,
                    "G29 宿主关闭请求被接受");
                CheckEq(sink.Count(PMDsCoordinatorEffect.GracefulShutdownRequested), 1, "G30 发出优雅关闭");
                CheckEq(coordinator.RequestShutdown(42u).Outcome, PMDsCoordinatorOutcome.Duplicate,
                    "G31 重复关闭请求幂等");
                CheckEq(coordinator.State, PMDsSessionState.Running, "G32 关闭请求不立即改状态（等进程退出）");

                // 未退出 → 宽限期到期后强杀；**强杀请求会节流重试**（旧实现是一次性闩锁，失败即永久静默）
                launcher.Process.KillExitsProcess = false;
                clock.Advance(10000);
                coordinator.Tick();
                Check(launcher.Process.Killed, "G33 宽限期到期请求强杀");
                CheckEq(coordinator.Counters.KillRequests, 1L, "G34 首次强杀请求计 1 次");
                CheckEq(coordinator.State, PMDsSessionState.Running, "G35 强杀后仍等进程退出（不伪造退出）");

                coordinator.Tick();
                CheckEq(coordinator.Counters.KillRequests, 1L, "G36 同一时刻不重复强杀（重试受 KillRetryInterval 节流）");

                clock.Advance(1000);
                coordinator.Tick();
                CheckEq(coordinator.Counters.KillRequests, 2L, "G36b 节流间隔到期后重试强杀（失败不静默）");
                CheckEq(launcher.Process.KillCount, 2, "G36c 替身确实收到了第二次强杀请求");
                CheckEq(coordinator.State, PMDsSessionState.Running, "G36d 仍未退出，状态不变");

                launcher.Process.KillExitsProcess = true;
                launcher.Process.Exit(1);
                coordinator.Tick();
                CheckEq(coordinator.State, PMDsSessionState.Failed, "G37 宿主关闭且无结果 → Failed");
                Check(coordinator.LastReason.IndexOf("宿主请求关闭但未产生结果", StringComparison.Ordinal) >= 0,
                    "G38 原因可区分宿主关闭：" + coordinator.LastReason);
                coordinator.Dispose();
            }

            // G39 Allocated 阶段取消（进程未起）
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();
                RecordingSink sink = new RecordingSink();
                PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, MakeOptions(),
                    MakeRequest("match-g39", "ds-g39", 1u, 0xAABBCCDDu, 1u, DefaultRoster));
                coordinator.RequestShutdown(1u);
                CheckEq(coordinator.State, PMDsSessionState.Failed, "G39 启动前取消 → Failed");
                CheckEq(coordinator.PortsInUse, 0, "G40 启动前取消释放端口");
                CheckEq(launcher.StartCount, 0, "G41 未启动进程");
                coordinator.Dispose();
            }

            // G42 DS 主动关闭（无结果）
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();
                RecordingSink sink = new RecordingSink();
                PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, MakeOptions(),
                    MakeRequest("match-g42", "ds-g42", 1u, 0xAABBCCDDu, 1u, DefaultRoster));
                coordinator.BeginStart();
                DsView ds = DsView.FromCoordinator(coordinator);
                byte[] ready = ds.Ready(coordinator.AllocatedPort, ds.CollisionDigest, true);
                coordinator.OnControlPayload(ready, 0, ready.Length);
                coordinator.MarkRunning();

                byte[] shutdown = ds.Shutdown(2u);
                CheckEq(coordinator.OnControlPayload(shutdown, 0, shutdown.Length).Outcome,
                    PMDsCoordinatorOutcome.Applied, "G42 DS 主动关闭被接受");
                launcher.Process.Exit(0);
                coordinator.Tick();
                CheckEq(coordinator.State, PMDsSessionState.Failed, "G43 DS 主动关闭且无结果 → Failed");
                Check(coordinator.LastReason.IndexOf("DS 主动关闭", StringComparison.Ordinal) >= 0,
                    "G44 原因可区分 DS 主动关闭");
                coordinator.Dispose();
            }

            // G45 共享端口池耗尽 / 释放后可再分配（Lobby 级分配器在多个会话间共享一个池）
            {
                PMDsCoordinatorOptions options = MakeOptions();
                options.PortRangeFirst = 7900;
                options.PortRangeLast = 7900;
                PMDsPortPool sharedPool = new PMDsPortPool(options.PortRangeFirst, options.PortRangeLast);
                FakeClock clock = new FakeClock();
                PMDsCoordinator first = new PMDsCoordinator(options, clock, new FakeLauncher(),
                    new RecordingSink(), sharedPool);
                MyAllocate(first, "match-g45a", "ds-g45a", 1u);
                CheckEq(first.AllocatedPort, 7900, "G45a 共享池分配 7900");

                PMDsCoordinator second = new PMDsCoordinator(options, clock, new FakeLauncher(),
                    new RecordingSink(), sharedPool);
                CheckEq(second.Allocate(MakeRequest("match-g45b", "ds-g45b", 2u, 0xAABBCCDDu, 1u, DefaultRoster)).Outcome,
                    PMDsCoordinatorOutcome.RejectedState, "G45 共享端口池耗尽 → 拒绝分配");
                CheckEq(sharedPool.InUseCount, 1, "G46 池仍只占 1 个端口");

                first.Dispose();
                CheckEq(sharedPool.InUseCount, 0, "G47 一局结束后共享池释放");
                CheckEq(second.Allocate(MakeRequest("match-g45b", "ds-g45b", 2u, 0xAABBCCDDu, 1u, DefaultRoster)).Outcome,
                    PMDsCoordinatorOutcome.Applied, "G48 释放后可再分配");
                CheckEq(second.AllocatedPort, 7900, "G49 复用同一端口");
                second.Dispose();

                // 说明：若每个会话各自 new 一个池，则所有会话都会选到同一个端口。
                // 因此「跨局端口唯一」必须由 Lobby 级共享池（或等价全局分配器）保证 —— 这是 R3-B 的接线前提。
                PMDsCoordinatorOptions own = MakeOptions();
                own.PortRangeFirst = 7900;
                own.PortRangeLast = 7900;
                PMDsCoordinator ownA = new PMDsCoordinator(own, clock, new FakeLauncher(), new RecordingSink());
                PMDsCoordinator ownB = new PMDsCoordinator(own, clock, new FakeLauncher(), new RecordingSink());
                MyAllocate(ownA, "match-ownA", "ds-ownA", 1u);
                MyAllocate(ownB, "match-ownB", "ds-ownB", 1u);
                CheckEq(ownA.AllocatedPort, ownB.AllocatedPort,
                    "G50 独立池会撞同一端口（因此 R3-B 必须用共享池）");
                ownA.Dispose();
                ownB.Dispose();
            }

            // G47 分配参数校验
            {
                FakeClock clock = new FakeClock();
                PMDsCoordinator coordinator = new PMDsCoordinator(MakeOptions(), clock, new FakeLauncher(),
                    new RecordingSink());
                CheckEq(coordinator.Allocate(null).Outcome, PMDsCoordinatorOutcome.RejectedMalformed,
                    "G47 null 分配请求被拒");
                CheckEq(coordinator.Allocate(MakeRequest("", "ds", 1u, 0xAABBCCDDu, 1u, DefaultRoster)).Outcome,
                    PMDsCoordinatorOutcome.RejectedMalformed, "G48 空 MatchId 被拒");
                CheckEq(coordinator.Allocate(MakeRequest("m", "ds", 0u, 0xAABBCCDDu, 1u, DefaultRoster)).Outcome,
                    PMDsCoordinatorOutcome.RejectedMalformed, "G49 Epoch=0 被拒");
                CheckEq(coordinator.Allocate(MakeRequest("m", "ds", 1u, 0x11111111u, 1u, DefaultRoster)).Outcome,
                    PMDsCoordinatorOutcome.RejectedIdentity, "G50 摘要与本进程不一致被拒");
                CheckEq(coordinator.Allocate(MakeRequest("m", "ds", 1u, 0xAABBCCDDu, 0u, DefaultRoster)).Outcome,
                    PMDsCoordinatorOutcome.RejectedMalformed, "G51 CollisionDigest=0 被拒");
                CheckEq(coordinator.Allocate(MakeRequest("m", "ds", 1u, 0xAABBCCDDu, 1u,
                    new PMDsRosterIdentity[] { new PMDsRosterIdentity(1, 1, 0, 1) })).Outcome,
                    PMDsCoordinatorOutcome.Applied, "G52 单人局可分配");
                coordinator.Dispose();
            }

            // G53 非法进程请求（shell / 相对路径 / 换行参数）
            {
                FakeClock clock = new FakeClock();
                PMDsCoordinator coordinator = new PMDsCoordinator(MakeOptions(), clock, new FakeLauncher(),
                    new RecordingSink());
                PMDsAllocationRequest request = MakeRequest("m-shell", "ds-shell", 1u, 0xAABBCCDDu, 1u, DefaultRoster);
                request.Process = new PMDsProcessLaunchRequest(@"C:\Windows\System32\cmd.exe", @"C:\", "/c", "echo hi");
                CheckEq(coordinator.Allocate(request).Outcome, PMDsCoordinatorOutcome.RejectedMalformed,
                    "G53 shell 启动请求被拒");

                request = MakeRequest("m-rel", "ds-rel", 1u, 0xAABBCCDDu, 1u, DefaultRoster);
                request.Process = new PMDsProcessLaunchRequest("HyldDS.exe", @"C:\", "-server");
                CheckEq(coordinator.Allocate(request).Outcome, PMDsCoordinatorOutcome.RejectedMalformed,
                    "G54 相对路径启动请求被拒");

                request = MakeRequest("m-nl", "ds-nl", 1u, 0xAABBCCDDu, 1u, DefaultRoster);
                request.Process = new PMDsProcessLaunchRequest(@"C:\a\b.exe", @"C:\", "-x\r\n-injected");
                CheckEq(coordinator.Allocate(request).Outcome, PMDsCoordinatorOutcome.RejectedMalformed,
                    "G55 含换行的参数被拒");
                CheckEq(coordinator.State, PMDsSessionState.Idle, "G56 非法请求后仍为 Idle（可重试）");
                coordinator.Dispose();
            }

            // G57 墓碑账本：TTL + 条数上限（冲突判定用 winner + summary 完整比对）
            {
                PMDsResultTombstoneLedger ledger = new PMDsResultTombstoneLedger(2, TimeSpan.FromSeconds(10));
                long now = 1000000L;
                CheckEq(ledger.Record(ledger.CreateEntry(1UL, 0, Summary("s1"), 11u, new byte[] { 1 }, now), now),
                    PMDsTombstoneWrite.Added, "G57 墓碑写入 1");
                CheckEq(ledger.Record(ledger.CreateEntry(2UL, 0, Summary("s2"), 22u, new byte[] { 2 }, now + 1), now + 1),
                    PMDsTombstoneWrite.Added, "G58 墓碑写入 2");
                CheckEq(ledger.Record(ledger.CreateEntry(3UL, 0, Summary("s3"), 33u, new byte[] { 3 }, now + 2), now + 2),
                    PMDsTombstoneWrite.Added, "G59 墓碑写入 3（超上限）");
                CheckEq(ledger.Count, 2, "G60 墓碑条数被上限截住");
                CheckEq(ledger.EvictedCount, 1L, "G61 淘汰计数为 1");

                PMDsResultTombstone entry;
                Check(!ledger.TryGet(1UL, now + 2, out entry), "G62 最旧墓碑被淘汰");
                Check(ledger.TryGet(3UL, now + 2, out entry), "G63 最新墓碑可查");

                CheckEq(ledger.Record(ledger.CreateEntry(3UL, 1, Summary("s3"), 33u, new byte[] { 3 }, now + 3), now + 3),
                    PMDsTombstoneWrite.Conflict, "G64 同 ID 不同胜方 → Conflict");
                CheckEq(ledger.Record(ledger.CreateEntry(3UL, 0, Summary("s3-modified"), 33u, new byte[] { 3 }, now + 3),
                    now + 3), PMDsTombstoneWrite.Conflict, "G64b 同 ID 同胜方但摘要字节不同 → Conflict");
                CheckEq(ledger.Record(ledger.CreateEntry(3UL, 0, Summary("s3"), 33u, new byte[] { 3 }, now + 3), now + 3),
                    PMDsTombstoneWrite.Updated, "G65 同 ID 同内容 → Updated");

                CheckEq(ledger.CollectExpired(now + 20000), 2, "G66 TTL 到期清理 2 条");
                CheckEq(ledger.Count, 0, "G67 清理后为空");
                Check(!ledger.TryGet(3UL, now + 20000, out entry), "G68 过期墓碑不可查");
            }

            // G69 端口池语义
            {
                PMDsPortPool pool = new PMDsPortPool(8000, 8002);
                int a;
                int b;
                int c;
                int d;
                bool gotA = pool.TryAcquire(out a);
                bool gotB = pool.TryAcquire(out b);
                bool gotC = pool.TryAcquire(out c);
                Check(gotA && gotB && gotC, "G69 端口池可分配 3 个");
                CheckEq(a, 8000, "G70 顺序分配 8000");
                CheckEq(c, 8002, "G71 顺序分配 8002");
                CheckNeg(!pool.TryAcquire(out d), "G72 池空时分配失败");
                Check(pool.Release(8001), "G73 回收 8001");
                Check(pool.TryAcquire(out d), "G74 回收后可再分配");
                CheckEq(d, 8001, "G75 复用回收的端口");
                Check(!pool.Release(9999), "G76 回收范围外端口返回 false");
                pool.ReleaseAll();
                CheckEq(pool.InUseCount, 0, "G77 ReleaseAll 归零");
            }
        }

        // ────────────────────────────────────────────────────────────────
        // H. 拒绝面与安全
        // ────────────────────────────────────────────────────────────────

        private static void TestCoordinatorSecurity()
        {
            FakeClock clock = new FakeClock();
            FakeLauncher launcher = new FakeLauncher();
            RecordingSink sink = new RecordingSink();
            PMDsCoordinatorOptions options = MakeOptions();
            PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, options,
                MakeRequest("match-h", "ds-h", 3u, 0xAABBCCDDu, 0xCAFEBABEu, DefaultRoster));
            coordinator.BeginStart();
            DsView ds = DsView.FromCoordinator(coordinator);

            // H1 坏 MAC 不动状态
            byte[] ready = ds.Ready(coordinator.AllocatedPort, ds.CollisionDigest, true);
            byte[] tampered = (byte[])ready.Clone();
            tampered[tampered.Length - 1] ^= 0xFF;
            CheckEq(coordinator.OnControlPayload(tampered, 0, tampered.Length).Outcome,
                PMDsCoordinatorOutcome.RejectedMac, "H1 坏 MAC 被拒");
            CheckEq(coordinator.State, PMDsSessionState.Starting, "H2 坏 MAC 不改状态");
            CheckEq(sink.Count(PMDsCoordinatorEffect.ReadyAddressPublished), 0, "H3 坏 MAC 不发布地址");
            CheckEq(coordinator.Counters.FramesRejectedMac, 1L, "H4 坏 MAC 计数");

            // H5 错密钥（另一个局）
            byte[] otherKey = PMDsMatchKey.Create().ExportKeyBytes();
            PMDsControlSigner foreignSigner = new PMDsControlSigner(otherKey);
            byte[] foreign = foreignSigner.Sign(PMDsControlMessage.Create(PMDsControlMessageType.Ready,
                "match-h", "ds-h", 3u, 0xAABBCCDDu, 1UL, new PMDsReadyBody
                {
                    BoundPort = coordinator.AllocatedPort,
                    SceneReady = true,
                    CollisionDigest = ds.CollisionDigest,
                }));
            CheckEq(coordinator.OnControlPayload(foreign, 0, foreign.Length).Outcome,
                PMDsCoordinatorOutcome.RejectedMac, "H5 错密钥被拒（不接受客户自报密钥）");
            CheckEq(coordinator.State, PMDsSessionState.Starting, "H6 错密钥不改状态");

            // H7 结构性非法载荷
            CheckEq(coordinator.OnControlPayload(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, 0, 4).Outcome,
                PMDsCoordinatorOutcome.RejectedMalformed, "H7 结构非法载荷被拒");
            CheckEq(coordinator.Counters.FramesRejectedMalformed, 1L, "H8 结构非法计数");

            // H9 错身份：用真实密钥签，但 MatchId 不同
            byte[] wrongMatch = ds.SignWithIdentity(PMDsControlMessageType.Ready, "other-match", "ds-h", 3u,
                0xAABBCCDDu, 1UL, new PMDsReadyBody
                {
                    BoundPort = coordinator.AllocatedPort,
                    SceneReady = true,
                    CollisionDigest = ds.CollisionDigest,
                });
            CheckEq(coordinator.OnControlPayload(wrongMatch, 0, wrongMatch.Length).Outcome,
                PMDsCoordinatorOutcome.RejectedIdentity, "H9 错 MatchId 被拒");

            byte[] wrongEpoch = ds.SignWithIdentity(PMDsControlMessageType.Ready, "match-h", "ds-h", 4u,
                0xAABBCCDDu, 1UL, new PMDsReadyBody
                {
                    BoundPort = coordinator.AllocatedPort,
                    SceneReady = true,
                    CollisionDigest = ds.CollisionDigest,
                });
            CheckEq(coordinator.OnControlPayload(wrongEpoch, 0, wrongEpoch.Length).Outcome,
                PMDsCoordinatorOutcome.RejectedIdentity, "H10 错世代被拒");

            byte[] wrongHash = ds.SignWithIdentity(PMDsControlMessageType.Ready, "match-h", "ds-h", 3u,
                0x11223344u, 1UL, new PMDsReadyBody
                {
                    BoundPort = coordinator.AllocatedPort,
                    SceneReady = true,
                    CollisionDigest = ds.CollisionDigest,
                });
            CheckEq(coordinator.OnControlPayload(wrongHash, 0, wrongHash.Length).Outcome,
                PMDsCoordinatorOutcome.RejectedIdentity, "H11 错摘要被拒");

            byte[] wrongDs = ds.SignWithIdentity(PMDsControlMessageType.Ready, "match-h", "other-ds", 3u,
                0xAABBCCDDu, 1UL, new PMDsReadyBody
                {
                    BoundPort = coordinator.AllocatedPort,
                    SceneReady = true,
                    CollisionDigest = ds.CollisionDigest,
                });
            CheckEq(coordinator.OnControlPayload(wrongDs, 0, wrongDs.Length).Outcome,
                PMDsCoordinatorOutcome.RejectedIdentity, "H12 错 DsId 被拒");
            CheckEq(coordinator.Counters.FramesRejectedIdentity, 4L, "H13 身份拒绝计数");
            CheckEq(coordinator.State, PMDsSessionState.Starting, "H14 身份拒绝不改状态");

            // H15 Starting 阶段拒绝心跳/结果
            byte[] heartbeat = ds.Heartbeat(1, 1);
            CheckEq(coordinator.OnControlPayload(heartbeat, 0, heartbeat.Length).Outcome,
                PMDsCoordinatorOutcome.RejectedState, "H15 Starting 阶段心跳被拒");
            byte[] result = ds.Result(1UL, 0, new byte[0]);
            CheckEq(coordinator.OnControlPayload(result, 0, result.Length).Outcome,
                PMDsCoordinatorOutcome.RejectedState, "H16 Starting 阶段结果被拒");

            // H17 Lobby 不收 Bootstrap / ResultAck
            byte[] bootstrap = ds.Sign(PMDsControlMessageType.Bootstrap, 1UL, new PMDsBootstrapBody
            {
                CollisionDigest = 1u,
                Players = new PMDsBootstrapPlayer[]
                {
                    new PMDsBootstrapPlayer(new PMDsRosterIdentity(101, 1, 0, 1), new byte[96]),
                },
            });
            CheckEq(coordinator.OnControlPayload(bootstrap, 0, bootstrap.Length).Outcome,
                PMDsCoordinatorOutcome.RejectedState, "H17 Lobby 不接受 Bootstrap（密钥不走通道）");
            byte[] ack = ds.Sign(PMDsControlMessageType.ResultAck, 1UL,
                new PMDsResultAckBody { ResultId = 5UL });
            CheckEq(coordinator.OnControlPayload(ack, 0, ack.Length).Outcome,
                PMDsCoordinatorOutcome.RejectedState, "H18 Lobby 不接受 ResultAck");

            // H19 正常 Ready 后进入 Running，结果可被接受
            CheckEq(coordinator.OnControlPayload(ready, 0, ready.Length).Outcome,
                PMDsCoordinatorOutcome.Applied, "H19 合法 Ready 被接受");
            coordinator.MarkRunning();
            CheckEq(coordinator.OnControlPayload(result, 0, result.Length).Outcome,
                PMDsCoordinatorOutcome.Applied, "H20 Running 后结果被接受");

            // H21 票据名册校验
            long nowSeconds = clock.Now / 1000L;
            PMDsTicket ticket;
            byte[] document = coordinator.ExportBootstrapDocument();
            PMDsBootstrappedMatch boot;
            string error;
            PMDsBootstrapDocument.TryDecode(document, out boot, out error);

            // 用本局密钥另签一张「不在名册里的人」的票据
            PMDsTicketIssuer rogueIssuer = boot.Key.CreateTicketIssuer(boot.MatchId, boot.DsId, boot.Epoch, boot.ProtocolHash);
            PMDsTicket rogue = rogueIssuer.Issue(new PMDsRosterIdentity(999, 99, 0, 1), nowSeconds);
            byte[] rogueBytes = rogue.ExportTicketBytes();
            CheckEq(coordinator.ResolveTicket(rogueBytes, 0, rogueBytes.Length, nowSeconds).Verdict,
                PMDsTicketVerdict.NotInRoster, "H21 不在名册的票据 → NotInRoster");

            // 名册内票据：真实验签 + 名册命中
            PMDsBootstrapPlayer firstPlayer = boot.Bootstrap.AsBootstrap.Players[0];
            PMDsTicketVerification accept = coordinator.ResolveTicket(
                firstPlayer.Ticket, 0, firstPlayer.Ticket.Length, nowSeconds);
            CheckEq(accept.Verdict, PMDsTicketVerdict.Valid, "H22 名册内票据通过");
            CheckEq(accept.Identity.Uid, 101, "H23 身份来自票据（不是业务包自报）");

            // 错密钥的票据（另一个局）
            PMDsMatchKey foreignKey = PMDsMatchKey.Create();
            PMDsTicketIssuer foreignIssuer = foreignKey.CreateTicketIssuer(boot.MatchId, boot.DsId, boot.Epoch, boot.ProtocolHash);
            byte[] foreignTicket = foreignIssuer.Issue(DefaultRoster[0], nowSeconds).ExportTicketBytes();
            CheckEq(coordinator.ResolveTicket(foreignTicket, 0, foreignTicket.Length, nowSeconds).Verdict,
                PMDsTicketVerdict.MacMismatch, "H24 他局票据 → MacMismatch");

            // 过期票据
            CheckEq(coordinator.ResolveTicket(firstPlayer.Ticket, 0, firstPlayer.Ticket.Length, nowSeconds + 1000).Verdict,
                PMDsTicketVerdict.Expired, "H25 过期票据 → Expired");

            // 名册查询（uid 越界）
            PMDsRosterIdentity identity;
            Check(coordinator.TryGetRosterIdentity(101, out identity), "H26 名册查询命中");
            Check(!coordinator.TryGetRosterIdentity(999, out identity), "H27 名册查询未命中");

            // 票据签发：非名册 uid 抛异常
            ExpectAnyThrow(delegate() { coordinator.IssueTicket(999, nowSeconds); }, "H28 非名册 uid 签发被拒");
            ticket = coordinator.IssueTicket(102, nowSeconds);
            Check(ticket != null, "H29 名册内 uid 可签发");

            // 终态后名册释放 → 票据解析不再通过
            launcher.Process.Exit(0);
            coordinator.Tick();
            coordinator.OnProcessExited(0);
            CheckEq(coordinator.State, PMDsSessionState.Failed, "H30 有结果但进程异常退出 → Failed");
            CheckEq(coordinator.RosterCount, 0, "H31 终态释放名册");
            CheckEq(coordinator.ResolveTicket(firstPlayer.Ticket, 0, firstPlayer.Ticket.Length, nowSeconds).Verdict,
                PMDsTicketVerdict.NotInRoster, "H32 终态后票据不再解析成名册身份");
            coordinator.Dispose();
        }

        // ────────────────────────────────────────────────────────────────
        // I. 真实 loopback TCP
        // ────────────────────────────────────────────────────────────────

        private static void TestLoopbackTcp()
        {
            FakeClock clock = new FakeClock();
            FakeLauncher launcher = new FakeLauncher();
            RecordingSink sink = new RecordingSink();
            PMDsCoordinatorOptions options = MakeOptions();
            PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, options,
                MakeRequest("match-tcp", "ds-tcp", 11u, 0xAABBCCDDu, 0x5A5A5A5Au, DefaultRoster));
            coordinator.BeginStart();
            DsView ds = DsView.FromCoordinator(coordinator);
            int port = coordinator.AllocatedPort;

            byte[] ready = ds.Ready(port, ds.CollisionDigest, true);
            byte[] heartbeat = ds.Heartbeat(500, 4);
            byte[] result = ds.Result(0x1234UL, 1, Encoding.ASCII.GetBytes("tcp"));
            byte[] tampered = (byte[])heartbeat.Clone();
            tampered[tampered.Length - 1] ^= 0x01;

            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int listenPort = ((IPEndPoint)listener.LocalEndpoint).Port;

            Exception senderError = null;
            Thread sender = new Thread(delegate()
            {
                try
                {
                    using (TcpClient client = new TcpClient())
                    {
                        client.NoDelay = true;
                        client.Connect(IPAddress.Loopback, listenPort);
                        NetworkStream stream = client.GetStream();

                        // 阶段 1：Ready 逐字节发送（真实分片，制造半包）
                        byte[] frame = PMDsControlFraming.Frame(ready);
                        for (int i = 0; i < frame.Length; i++)
                        {
                            stream.Write(frame, i, 1);
                            stream.Flush();
                            Thread.Sleep(1);
                        }

                        Thread.Sleep(40);

                        // 阶段 2：Heartbeat + Result 一次写入（粘包）
                        byte[] hb = PMDsControlFraming.Frame(heartbeat);
                        byte[] rs = PMDsControlFraming.Frame(result);
                        byte[] both = new byte[hb.Length + rs.Length];
                        Buffer.BlockCopy(hb, 0, both, 0, hb.Length);
                        Buffer.BlockCopy(rs, 0, both, hb.Length, rs.Length);
                        stream.Write(both, 0, both.Length);
                        stream.Flush();
                        Thread.Sleep(40);

                        // 阶段 3：篡改 MAC 的帧
                        byte[] bad = PMDsControlFraming.Frame(tampered);
                        stream.Write(bad, 0, bad.Length);
                        stream.Flush();
                        Thread.Sleep(40);

                        // 阶段 4：超限长度前缀
                        stream.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F }, 0, 4);
                        stream.Flush();
                        Thread.Sleep(40);
                        client.Client.Shutdown(SocketShutdown.Send);
                    }
                }
                catch (Exception ex)
                {
                    senderError = ex;
                }
            });
            sender.IsBackground = true;
            sender.Start();

            int framesDecoded = 0;
            int rejectedMac = 0;
            bool oversizeRejected = false;
            PMDsControlFrameDecoder decoder = new PMDsControlFrameDecoder();

            using (TcpClient server = listener.AcceptTcpClient())
            {
                server.NoDelay = true;
                server.ReceiveTimeout = 8000;
                NetworkStream stream = server.GetStream();
                byte[] buffer = new byte[512];

                while (true)
                {
                    int read;
                    try
                    {
                        read = stream.Read(buffer, 0, buffer.Length);
                    }
                    catch (IOException)
                    {
                        break;
                    }

                    if (read <= 0)
                    {
                        break;
                    }

                    try
                    {
                        decoder.Append(buffer, 0, read);
                    }
                    catch (PMDsControlProtocolException)
                    {
                        oversizeRejected = decoder.Fault == PMDsControlFrameFault.OversizeLength;
                        break;
                    }

                    byte[] payload;
                    while (decoder.TryDequeue(out payload))
                    {
                        framesDecoded++;
                        PMDsCoordinatorReply reply = coordinator.OnControlPayload(payload, 0, payload.Length);
                        if (reply.Outcome == PMDsCoordinatorOutcome.RejectedMac)
                        {
                            rejectedMac++;
                        }
                    }
                }
            }

            listener.Stop();
            sender.Join(3000);

            Check(senderError == null, "I1 真实 TCP 发送端无异常"
                + (senderError == null ? string.Empty : "： " + senderError.Message));
            CheckEq(framesDecoded, 4, "I2 真实 socket 上解出 4 帧");
            CheckEq(coordinator.Counters.FramesRejectedMac, 1L, "I3 篡改帧在真实链路上被拒 1 次");
            CheckEq(rejectedMac, 1, "I4 拒绝分类为 MAC 失败");
            Check(oversizeRejected, "I5 超限长度前缀在真实链路上立即拒绝");
            CheckEq(decoder.Fault, PMDsControlFrameFault.OversizeLength, "I6 故障分类 OversizeLength");
            CheckEq(coordinator.State, PMDsSessionState.ResultPending, "I7 真实链路推进到 ResultPending");
            CheckEq(sink.Count(PMDsCoordinatorEffect.ReadyAddressPublished), 1, "I8 真实链路只发布一次地址");
            CheckEq(sink.Count(PMDsCoordinatorEffect.ResultAccepted), 1, "I9 真实链路接受了一次结果");
            Check(coordinator.Counters.FramesAccepted >= 3L, "I10 至少 3 帧被接受（Ready+Heartbeat+Result）");

            coordinator.Dispose();
        }

        // ────────────────────────────────────────────────────────────────
        // J. 真实固定 exe 进程适配
        // ────────────────────────────────────────────────────────────────

        private static void TestRealProcessAdapter()
        {
            // J1 有界输出缓冲
            PMDsBoundedTextLog log = new PMDsBoundedTextLog(1024);
            byte[] chunk = new byte[300];
            for (int i = 0; i < 10; i++)
            {
                log.Append(chunk, 0, chunk.Length);
            }

            CheckEq(log.TotalBytes, 3000L, "J1 累计字节数不受上限影响");
            CheckEq(log.RetainedBytes, 1024, "J2 保留字节数被上限截住");
            Check(log.GetTail().Length <= 1024, "J3 尾部文本长度有界");
            log.Append(new byte[5000], 0, 5000);
            CheckEq(log.TotalBytes, 8000L, "J4 单次超限写入仍累计");
            CheckEq(log.RetainedBytes, 1024, "J5 单次超限写入保留上限");

            // J6 启动请求安全校验（不需要真实文件）
            PMDsProcessFault fault;
            string detail;
            Check(PMDsProcessSafety.ValidateStructure(
                new PMDsProcessLaunchRequest(@"C:\x\y.exe", @"C:\x", "-server"), out fault, out detail),
                "J6 正常请求通过结构校验");
            CheckNeg(!PMDsProcessSafety.ValidateStructure(
                new PMDsProcessLaunchRequest(@"C:\Windows\System32\cmd.exe", @"C:\", "/c", "whoami"), out fault, out detail),
                "J7 cmd.exe 被拒绝（不允许经 shell 启动）");
            CheckEq(fault, PMDsProcessFault.ShellRejected, "J8 拒绝分类 ShellRejected");
            CheckNeg(!PMDsProcessSafety.ValidateStructure(
                new PMDsProcessLaunchRequest("HyldDS.exe", @"C:\", "-server"), out fault, out detail),
                "J9 相对路径被拒绝");
            CheckNeg(!PMDsProcessSafety.ValidateStructure(
                new PMDsProcessLaunchRequest(@"C:\x\y.exe", string.Empty, "-server"), out fault, out detail),
                "J10 缺工作目录被拒绝");
            CheckNeg(!PMDsProcessSafety.ValidateStructure(
                new PMDsProcessLaunchRequest(@"C:\x\y.exe", @"C:\", "-a\n-b"), out fault, out detail),
                "J11 含换行参数被拒绝");
            CheckNeg(!PMDsProcessSafety.ValidateStructure(
                new PMDsProcessLaunchRequest(@"C:\x\y.exe", @"C:\", new string[] { null }), out fault, out detail),
                "J12 null 参数被拒绝");
            CheckNeg(!PMDsProcessSafety.ValidateStructure(null, out fault, out detail), "J13 null 请求被拒绝");

            // J14 白名单
            PMDsProcessWhitelist whitelist = new PMDsProcessWhitelist();
            whitelist.Add(@"C:\PMDsOnly\HyldDS.exe");
            Check(whitelist.Contains(@"C:\PMDsOnly\HyldDS.exe"), "J14 白名单命中");
            Check(!whitelist.Contains(@"C:\Other\HyldDS.exe"), "J15 白名单未命中");
            CheckEq(whitelist.Count, 1, "J16 重复加入幂等");

            // J17 名单外路径被拒（不要求文件存在：白名单先于存在性判定）
            PMDsSystemProcessLauncher strictLauncher = new PMDsSystemProcessLauncher(whitelist);
            IPMDsProcess process;
            CheckNeg(!strictLauncher.TryStart(
                new PMDsProcessLaunchRequest(@"C:\NotWhitelisted\HyldDS.exe", @"C:\", "-server"),
                out process, out fault, out detail), "J17 名单外 exe 被拒");
            CheckEq(fault, PMDsProcessFault.NotWhitelisted, "J18 拒绝分类 NotWhitelisted");

            // J19 不存在的 exe 被拒
            PMDsSystemProcessLauncher launcher = new PMDsSystemProcessLauncher();
            CheckNeg(!launcher.TryStart(
                new PMDsProcessLaunchRequest(@"C:\PMDsDoesNotExist\HyldDS.exe", @"C:\", "-server"),
                out process, out fault, out detail), "J19 不存在的 exe 被拒");
            CheckEq(fault, PMDsProcessFault.ExecutableNotFound, "J20 拒绝分类 ExecutableNotFound");

            // J21 真实子进程：有界 stdout/stderr + 退出码
            if (!TryRunRealChild(launcher))
            {
                Console.WriteLine("    SKIP 真实子进程用例（无法确定可用宿主 exe，跳过并如实记录）");
            }
        }

        /// <summary>真实启动本测试自身作为子进程，验证「有界输出 + 退出码 + 强杀」。</summary>
        private static bool TryRunRealChild(PMDsSystemProcessLauncher launcher)
        {
            string host = Environment.ProcessPath;
            string entry = Assembly.GetEntryAssembly() == null ? null : Assembly.GetEntryAssembly().Location;
            if (string.IsNullOrEmpty(host))
            {
                return false;
            }

            List<string> args = new List<string>();
            string hostName = Path.GetFileNameWithoutExtension(host);
            bool hostIsSelf = string.Equals(hostName, "PMDsControlTest", StringComparison.OrdinalIgnoreCase);
            if (!hostIsSelf)
            {
                if (string.IsNullOrEmpty(entry) || !File.Exists(entry))
                {
                    return false;
                }

                args.Add(entry);
            }

            args.Add("--child-emit");
            args.Add("4000");
            args.Add("2000");
            args.Add("7");

            PMDsProcessLaunchRequest request = new PMDsProcessLaunchRequest(host, AppContext.BaseDirectory,
                args.ToArray());
            request.MaxCapturedOutputBytes = 8192;

            IPMDsProcess process;
            PMDsProcessFault fault;
            string detail;
            if (!launcher.TryStart(request, out process, out fault, out detail))
            {
                Check(false, "J21 真实子进程启动失败：" + fault + " " + detail);
                return true;
            }

            using (process)
            {
                int exitCode = 0;
                bool finished = WaitForExit(process, 15000, out exitCode);
                Check(finished, "J22 真实子进程在 15 秒内退出");
                CheckEq(exitCode, 7, "J23 真实退出码被读取");
                Check(process.StdOutTotalBytes > 8192, "J24 stdout 累计字节数 " + process.StdOutTotalBytes + " 超过保留上限");
                Check(process.StdErrTotalBytes > 0, "J25 stderr 有输出");
                Check(Encoding.UTF8.GetByteCount(process.StdOutTail) <= 8192,
                    "J26 stdout 尾部保留 ≤ 8192 字节（实测 " + Encoding.UTF8.GetByteCount(process.StdOutTail) + "）");
                Check(Encoding.UTF8.GetByteCount(process.StdErrTail) <= 8192,
                    "J27 stderr 尾部保留 ≤ 8192 字节");
                Check(process.StdOutTail.IndexOf("line-", StringComparison.Ordinal) >= 0,
                    "J28 stdout 尾部确实是日志尾部（可诊断）");
            }

            // J29 强杀长驻子进程
            List<string> sleepArgs = new List<string>();
            if (!hostIsSelf)
            {
                sleepArgs.Add(entry);
            }

            sleepArgs.Add("--child-sleep");

            IPMDsProcess sleeper;
            if (launcher.TryStart(new PMDsProcessLaunchRequest(host, AppContext.BaseDirectory, sleepArgs.ToArray()),
                out sleeper, out fault, out detail))
            {
                using (sleeper)
                {
                    Check(sleeper.IsRunning, "J29 长驻子进程在运行");
                    sleeper.Kill();
                    int code;
                    bool gone = WaitForExit(sleeper, 10000, out code);
                    Check(gone, "J30 强杀后进程确实退出");
                    Check(!sleeper.IsRunning, "J31 强杀后 IsRunning=false");
                }
            }
            else
            {
                Check(false, "J32 长驻子进程启动失败：" + fault + " " + detail);
            }

            // J33 输出不是就绪信号：协调器从不读 stdout
            FakeClock clock = new FakeClock();
            FakeLauncher fakeLauncher = new FakeLauncher();
            fakeLauncher.Process.EmitStdout("Ready! scene ready! bound port 7801");
            RecordingSink sink = new RecordingSink();
            PMDsCoordinator coordinator = NewCoordinator(clock, fakeLauncher, sink, MakeOptions(),
                MakeRequest("match-j33", "ds-j33", 1u, 0xAABBCCDDu, 1u, DefaultRoster));
            coordinator.BeginStart();
            for (int i = 0; i < 5; i++)
            {
                clock.Advance(1000);
                coordinator.Tick();
            }

            CheckEq(coordinator.State, PMDsSessionState.Starting, "J33 日志内容不推进状态（5 秒后仍 Starting）");
            CheckEq(sink.Count(PMDsCoordinatorEffect.ReadyAddressPublished), 0, "J34 日志从不触发地址发布");
            coordinator.Dispose();

            return true;
        }

        private static bool WaitForExit(IPMDsProcess process, int timeoutMilliseconds, out int exitCode)
        {
            long deadline = Environment.TickCount64 + timeoutMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                if (process.TryGetExitCode(out exitCode))
                {
                    return true;
                }

                Thread.Sleep(25);
            }

            exitCode = 0;
            return process.TryGetExitCode(out exitCode);
        }

        // ────────────────────────────────────────────────────────────────
        // K. 审查闭合回归（M1–M4 最小复现）
        //
        // 对应 `Docs/plans/_r3a_control_review.md` 的 4 项必须修：
        //   M1 强杀失败/未退出时不得释放端口与玩家占用（Tick 继续收尾并节流重试，不伪造退出）
        //   M2 ResultCommitted 只是「本地受理」，不得依据发送次数宣布对端确认
        //   M3 同 ResultId 的内容判定用 winner + summary 完整比对（hash 仅诊断，墓碑不被覆盖）
        //   M4 Exited / Error 严格状态门（Allocated 阶段不得被推到终态）
        // ────────────────────────────────────────────────────────────────

        private static void TestReviewClosureRegression()
        {
            // ── K1（M1 最小复现）：强杀成功但进程尚未退出时不得归还端口/名册 ──
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();
                RecordingSink sink = new RecordingSink();
                PMDsCoordinatorOptions options = MakeOptions();
                PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, options,
                    MakeRequest("match-k1", "ds-k1", 1u, 0xAABBCCDDu, 1u, DefaultRoster));
                int port = coordinator.AllocatedPort;
                coordinator.BeginStart();

                // 关键注入：Kill 请求发出去了，但进程没走（真实进程完全可能这样）。
                launcher.Process.KillExitsProcess = false;
                clock.Advance(30000);
                PMDsCoordinatorReply expired = coordinator.Tick();

                CheckEq(expired.Outcome, PMDsCoordinatorOutcome.Applied, "K1 启动超时被处理");
                CheckEq(coordinator.State, PMDsSessionState.TimedOut, "K2 状态 TimedOut");
                Check(launcher.Process.Killed, "K3 已请求强杀");
                CheckEq(launcher.Process.KillCount, 1, "K4 强杀请求 1 次");
                Check(launcher.Process.IsRunning, "K5 进程仍在运行（没有伪造退出）");
                Check(coordinator.IsAwaitingProcessExit, "K6 标记为等待进程退出");
                CheckNeg(coordinator.PortsInUse == 1, "K7 端口未归还共享池（旧实现在这里已提前归还）");
                CheckNeg(coordinator.IsPortInUse(port), "K8 该端口仍被本会话占用");
                CheckEq(coordinator.RosterCount, 4, "K9 玩家/名册占用未释放");
                CheckEq(sink.Count(PMDsCoordinatorEffect.ResourcesReleased), 0, "K10 未发资源释放事件");
                CheckEq(coordinator.Counters.KillRequestFailures, 0L, "K11 本次强杀请求本身没有失败");

                coordinator.Tick();
                CheckEq(launcher.Process.KillCount, 1, "K12 同一时刻不重复强杀（节流）");
                CheckEq(coordinator.PortsInUse, 1, "K13 仍未释放端口");
                CheckEq(coordinator.Counters.DeferredCleanupTicks, 1L, "K14 已记录一次延迟收尾");

                launcher.Process.KillExitsProcess = true;
                launcher.Process.Exit(0);
                PMDsCoordinatorReply done = coordinator.Tick();
                CheckEq(done.Outcome, PMDsCoordinatorOutcome.Applied, "K15 确认退出后收尾完成");
                CheckEq(coordinator.State, PMDsSessionState.TimedOut, "K16 终态不因收尾改变");
                Check(!coordinator.IsAwaitingProcessExit, "K17 已不在等待退出");
                CheckEq(coordinator.PortsInUse, 0, "K18 确认退出后才释放端口");
                CheckNeg(!coordinator.IsPortInUse(port), "K19 端口已真正释放");
                CheckEq(coordinator.RosterCount, 0, "K20 名册随之释放");
                CheckEq(sink.Count(PMDsCoordinatorEffect.ResourcesReleased), 1, "K21 资源释放恰好一次");
                Check(launcher.Process.WaitForExitCount > 0, "K22 协调器确实向进程问过「你走了吗」");
                coordinator.Dispose();
            }

            // ── K2（M1）：Kill 抛异常不得静默吞掉，也不得释放端口；重试可见 ──
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();
                RecordingSink sink = new RecordingSink();
                PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, MakeOptions(),
                    MakeRequest("match-k2", "ds-k2", 1u, 0xAABBCCDDu, 1u, DefaultRoster));
                coordinator.BeginStart();

                launcher.Process.ThrowOnKill = true;
                clock.Advance(30000);
                coordinator.Tick();

                CheckEq(coordinator.State, PMDsSessionState.TimedOut, "K23 强杀抛异常不影响终态判定");
                CheckEq(coordinator.Counters.KillRequests, 1L, "K24 强杀请求 1 次");
                CheckEq(coordinator.Counters.KillRequestFailures, 1L, "K25 强杀失败被计数（不静默）");
                PMDsCoordinatorEvent killEvent = sink.Last(PMDsCoordinatorEffect.ProcessKillRequested);
                Check(killEvent != null && killEvent.Reason != null
                    && killEvent.Reason.IndexOf("强杀失败", StringComparison.Ordinal) >= 0,
                    "K26 失败原因写进事件：" + (killEvent == null ? "<null>" : killEvent.Reason));
                Check(coordinator.IsAwaitingProcessExit, "K27 仍在等待进程退出");
                CheckNeg(coordinator.PortsInUse == 1, "K28 强杀失败时端口不释放");

                clock.Advance(1000);
                coordinator.Tick();
                CheckEq(coordinator.Counters.KillRequests, 2L, "K29 节流到期后重试强杀");
                CheckEq(coordinator.Counters.KillRequestFailures, 2L, "K30 第二次仍然失败并被记录");

                launcher.Process.ThrowOnKill = false;
                launcher.Process.KillExitsProcess = true;
                launcher.Process.Exit(0);
                coordinator.Tick();
                CheckEq(coordinator.PortsInUse, 0, "K31 进程真正退出后端口才释放");
                CheckEq(coordinator.Counters.KillRequestFailures, 2L, "K32 失败计数不被回退");
                coordinator.Dispose();
            }

            // ── K3（M2）：Result 无任何对端入站时不得宣称「对端已确认」 ──
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();
                RecordingSink sink = new RecordingSink();
                PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, MakeOptions(),
                    MakeRequest("match-k3", "ds-k3", 1u, 0xAABBCCDDu, 1u, DefaultRoster));
                coordinator.BeginStart();
                DsView ds = DsView.FromCoordinator(coordinator);
                byte[] ready = ds.Ready(coordinator.AllocatedPort, ds.CollisionDigest, true);
                coordinator.OnControlPayload(ready, 0, ready.Length);
                coordinator.MarkRunning();
                byte[] result = ds.Result(0x1122334455667788UL, 0, Summary("k3-summary"));
                CheckEq(coordinator.OnControlPayload(result, 0, result.Length).Outcome,
                    PMDsCoordinatorOutcome.Applied, "K33 结果被接受");

                for (int i = 0; i < 4; i++)
                {
                    clock.Advance(1000);
                    coordinator.Tick();
                }

                CheckEq(coordinator.State, PMDsSessionState.ResultCommitted, "K34 重发预算用尽 → ResultCommitted");
                CheckEq(coordinator.Counters.ResultAckAbandoned, 1L, "K35 放弃重传计数 1");
                Check(!coordinator.PeerExitObserved, "K36 无对端入站 ⇒ 不得声称对端已确认");
                Check(coordinator.State != PMDsSessionState.Exited, "K37 不得自行进入 Exited");

                // 时钟继续推进但不对端给任何输入：状态与资源占用都不变
                launcher.Process.KillExitsProcess = false;
                clock.Advance(20000);
                coordinator.Tick();
                CheckEq(coordinator.State, PMDsSessionState.ResultCommitted, "K38 无对端信号时状态不变");
                Check(!coordinator.PeerExitObserved, "K39 仍未观测到对端收尾");
                CheckEq(coordinator.PortsInUse, 1, "K40 未确认收尾前端口仍占用");
                Check(launcher.Process.IsRunning, "K41 未伪造进程退出");

                // 显式消息/真实退出才是确认来源
                launcher.Process.KillExitsProcess = true;
                byte[] exited = ds.Exited(0);
                CheckEq(coordinator.OnControlPayload(exited, 0, exited.Length).Outcome,
                    PMDsCoordinatorOutcome.Applied, "K42 对端显式 Exited 被接受");
                Check(coordinator.PeerExitObserved, "K43 对端收尾已观测（由显式消息决定）");
                CheckEq(coordinator.State, PMDsSessionState.Exited, "K44 对端退出后进入 Exited");
                CheckEq(coordinator.PortsInUse, 0, "K45 端口释放");
                coordinator.Dispose();
            }

            // ── K4（M3）：同 ResultId 的「同 hash 不同 summary」必须判冲突 ──
            {
                // 4a 账本层：判定不看 hash，只看 winner + summary 字节
                PMDsResultTombstoneLedger ledger = new PMDsResultTombstoneLedger(8, TimeSpan.FromMinutes(5));
                long now = 2000000L;
                byte[] summaryA = Summary("ledger-A");
                byte[] summaryB = Summary("ledger-B");
                CheckEq(ledger.Record(ledger.CreateEntry(9UL, 0, summaryA, 0x12345678u, new byte[] { 9 }, now), now),
                    PMDsTombstoneWrite.Added, "K46 账本写入 A");
                CheckEq(ledger.Record(
                    ledger.CreateEntry(9UL, 0, summaryB, 0x12345678u, new byte[] { 9 }, now + 1), now + 1),
                    PMDsTombstoneWrite.Conflict, "K47 同 hash 同胜方但 summary 不同 → Conflict");
                PMDsResultTombstone stored;
                Check(ledger.TryGet(9UL, now + 1, out stored), "K48 原墓碑仍在");
                CheckNeg(!PMDsResultTombstoneLedger.SummaryEquals(stored.Summary, summaryB),
                    "K49 冲突不覆盖原内容（墓碑仍是 A）");
                Check(PMDsResultTombstoneLedger.SummaryEquals(stored.Summary, summaryA), "K50 原内容字节完好");
                CheckEq(stored.ContentHash, 0x12345678u, "K51 诊断 hash 原样保留（仅诊断）");

                // 4b 协调器层：真造一对 4 字节诊断 hash 相同的不同摘要，第二条必须被判冲突
                byte[] collisionA;
                byte[] collisionB;
                uint collisionHash;
                if (TryFindDiagnosticHashCollision(out collisionA, out collisionB, out collisionHash))
                {
                    FakeClock clock = new FakeClock();
                    FakeLauncher launcher = new FakeLauncher();
                    RecordingSink sink = new RecordingSink();
                    PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, MakeOptions(),
                        MakeRequest("match-k4", "ds-k4", 1u, 0xAABBCCDDu, 1u, DefaultRoster));
                    coordinator.BeginStart();
                    DsView ds = DsView.FromCoordinator(coordinator);
                    byte[] ready = ds.Ready(coordinator.AllocatedPort, ds.CollisionDigest, true);
                    coordinator.OnControlPayload(ready, 0, ready.Length);
                    coordinator.MarkRunning();

                    byte[] first = ds.Result(0xABCDABCDUL, 0, collisionA);
                    CheckEq(coordinator.OnControlPayload(first, 0, first.Length).Outcome,
                        PMDsCoordinatorOutcome.Applied,
                        "K52 第一条结果被接受（诊断 hash 0x" + collisionHash.ToString("X8") + "）");
                    byte[] second = ds.Result(0xABCDABCDUL, 0, collisionB);
                    CheckEq(coordinator.OnControlPayload(second, 0, second.Length).Outcome,
                        PMDsCoordinatorOutcome.RejectedConflict,
                        "K53 同 hash 不同 summary → 冲突（旧实现会误判为重复并静默吞掉）");
                    CheckEq(coordinator.Counters.ResultDuplicates, 0L, "K54 不得被计成重复");
                    CheckEq(coordinator.Counters.ResultConflicts, 1L, "K55 冲突计数 1");
                    coordinator.Dispose();
                }
                else
                {
                    Check(false, "K52 未能构造 4 字节诊断 hash 碰撞（碰撞搜索预算用尽，属测试设施问题）");
                }
            }

            // ── K5（M4）：Exited / Error 严格状态门（未启动不得被推到终态） ──
            {
                // 5a Allocated：没有进程，Exited / Error 都不得改状态、不得释放端口
                {
                    FakeClock clock = new FakeClock();
                    FakeLauncher launcher = new FakeLauncher();
                    RecordingSink sink = new RecordingSink();
                    PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, MakeOptions(),
                        MakeRequest("match-k5a", "ds-k5a", 1u, 0xAABBCCDDu, 1u, DefaultRoster));
                    int port = coordinator.AllocatedPort;
                    DsView ds = DsView.FromCoordinator(coordinator);

                    byte[] exited = ds.Exited(0);
                    CheckEq(coordinator.OnControlPayload(exited, 0, exited.Length).Outcome,
                        PMDsCoordinatorOutcome.RejectedState,
                        "K56 Allocated 阶段 Exited 被拒（未启动就终态是旧缺陷）");
                    CheckEq(coordinator.State, PMDsSessionState.Allocated, "K57 状态仍为 Allocated");
                    CheckNeg(coordinator.PortsInUse == 1, "K58 端口仍被占用");
                    CheckNeg(coordinator.IsPortInUse(port), "K59 端口未被误归还");
                    CheckEq(coordinator.RosterCount, 4, "K60 名册未被误释放");
                    CheckEq(coordinator.Counters.FramesAccepted, 0L, "K61 被拒帧不计入已接受");

                    byte[] error = ds.Error(1u, "not started yet");
                    CheckEq(coordinator.OnControlPayload(error, 0, error.Length).Outcome,
                        PMDsCoordinatorOutcome.RejectedState, "K62 Allocated 阶段 Error 被拒");
                    CheckEq(coordinator.State, PMDsSessionState.Allocated, "K63 状态仍为 Allocated");
                    CheckEq(coordinator.Counters.FramesRejectedState, 2L, "K64 状态拒绝计数 2");

                    // 没有可归属进程时的宿主退出通知同样被拒
                    CheckEq(coordinator.OnProcessExited(0).Outcome, PMDsCoordinatorOutcome.RejectedState,
                        "K65 未启动时宿主退出通知被拒");
                    CheckEq(coordinator.State, PMDsSessionState.Allocated, "K66 状态仍为 Allocated");
                    CheckEq(coordinator.BeginStart().Outcome, PMDsCoordinatorOutcome.Applied,
                        "K67 会话未被「宣告死亡」，仍可正常启动");
                    coordinator.Dispose();
                }

                // 5b Starting：「就绪前退出」是合法的失败路径
                {
                    FakeClock clock = new FakeClock();
                    FakeLauncher launcher = new FakeLauncher();
                    RecordingSink sink = new RecordingSink();
                    PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, MakeOptions(),
                        MakeRequest("match-k5b", "ds-k5b", 1u, 0xAABBCCDDu, 1u, DefaultRoster));
                    coordinator.BeginStart();
                    DsView ds = DsView.FromCoordinator(coordinator);

                    byte[] exited = ds.Exited(3);
                    CheckEq(coordinator.OnControlPayload(exited, 0, exited.Length).Outcome,
                        PMDsCoordinatorOutcome.Applied, "K68 Starting 阶段 Exited 被接受");
                    CheckEq(coordinator.State, PMDsSessionState.Failed, "K69 就绪前退出 → Failed");
                    Check(coordinator.PeerExitObserved, "K70 对端退出已被观测");
                    CheckEq(coordinator.PortsInUse, 0, "K71 就绪前退出后释放端口");
                    CheckEq(coordinator.Counters.FramesAccepted, 1L, "K72 生效的 Exited 计入已接受");
                    coordinator.Dispose();
                }

                // 5c Running 阶段的 Exited → 运行中异常退出
                {
                    FakeClock clock = new FakeClock();
                    FakeLauncher launcher = new FakeLauncher();
                    RecordingSink sink = new RecordingSink();
                    PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, MakeOptions(),
                        MakeRequest("match-k5c", "ds-k5c", 1u, 0xAABBCCDDu, 1u, DefaultRoster));
                    coordinator.BeginStart();
                    DsView ds = DsView.FromCoordinator(coordinator);
                    byte[] ready = ds.Ready(coordinator.AllocatedPort, ds.CollisionDigest, true);
                    coordinator.OnControlPayload(ready, 0, ready.Length);
                    coordinator.MarkRunning();

                    byte[] exited = ds.Exited(5);
                    CheckEq(coordinator.OnControlPayload(exited, 0, exited.Length).Outcome,
                        PMDsCoordinatorOutcome.Applied, "K73 Running 阶段 Exited 被接受");
                    CheckEq(coordinator.State, PMDsSessionState.Failed, "K74 运行中退出 → Failed");
                    Check(coordinator.LastReason != null
                        && coordinator.LastReason.IndexOf("异常退出", StringComparison.Ordinal) >= 0,
                        "K75 原因标明异常退出：" + coordinator.LastReason);
                    coordinator.Dispose();
                }
            }

            // ── K6（M1×M4）：终态延迟收尾中，对端显式 Exited 仍不能代替本地确认 ──
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();
                RecordingSink sink = new RecordingSink();
                PMDsCoordinatorOptions options = MakeOptions();
                PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, options,
                    MakeRequest("match-k6", "ds-k6", 1u, 0xAABBCCDDu, 1u, DefaultRoster));
                coordinator.BeginStart();
                DsView ds = DsView.FromCoordinator(coordinator);
                byte[] ready = ds.Ready(coordinator.AllocatedPort, ds.CollisionDigest, true);
                coordinator.OnControlPayload(ready, 0, ready.Length);
                coordinator.MarkRunning();

                // 心跳超时 → 终态；进程杀不走（Kill 请求发出但没退出）→ 延迟收尾
                launcher.Process.KillExitsProcess = false;
                clock.Advance(15000);
                coordinator.Tick();
                CheckEq(coordinator.State, PMDsSessionState.TimedOut, "K76 心跳超时 → TimedOut");
                Check(coordinator.IsAwaitingProcessExit, "K77 进入延迟收尾");
                CheckEq(coordinator.PortsInUse, 1, "K78 端口仍占用");

                clock.Advance(1000);
                byte[] exited = ds.Exited(0);
                CheckEq(coordinator.OnControlPayload(exited, 0, exited.Length).Outcome,
                    PMDsCoordinatorOutcome.Applied, "K79 终态后显式 Exited 被接受（不伪造状态转移）");
                Check(coordinator.PeerExitObserved, "K80 对端收尾已观测");
                CheckNeg(coordinator.PortsInUse == 1, "K81 本地仍观测到进程活着 ⇒ 端口不得释放");
                Check(coordinator.IsAwaitingProcessExit, "K82 仍在延迟收尾");
                CheckEq(coordinator.State, PMDsSessionState.TimedOut, "K83 终态不变");

                launcher.Process.KillExitsProcess = true;
                launcher.Process.Exit(0);
                CheckEq(coordinator.Tick().Outcome, PMDsCoordinatorOutcome.Applied, "K84 进程真退出后收尾完成");
                CheckEq(coordinator.PortsInUse, 0, "K85 端口才释放");
                Check(!coordinator.IsAwaitingProcessExit, "K86 收尾结束");
                CheckEq(sink.Count(PMDsCoordinatorEffect.ResourcesReleased), 1, "K87 释放恰好一次");
                coordinator.Dispose();
            }
        }

        // ────────────────────────────────────────────────────────────────
        // L. ResultPending 退出语义区分（R3-B 主集成冻结裁决）
        //
        // 背景：真实链路上 DS 收到匹配 ResultAck 后会 SendExited(0) 再退进程（见 PMDsLobbyAgent）。
        // 旧实现把「ResultPending 期间的对端 Exited」一律判 Failed，于是**成功局**的终态标签是 Failed，
        // 而 ResultCommitted 在真实流程里通常根本到不了。冻结裁决补齐两者区别：
        //   ① 已受理结果 + **经 MAC/对局/世代认证的显式 Exited(0)** ⇒ 正常完成，终态 Exited；
        //   ② 单纯进程提前退出（Tick 轮询 / 宿主报告，无协议证据）⇒ 仍判 Failed；
        //   ③ 显式 Exited(非 0) 仍判 Failed（只有 exit=0 才算正常完成）；
        //   ④ ResultCommitted 语义不变；两条路径的资源释放都要求**本地确认进程已退出**，
        //      不从控制消息推断进程死亡。
        // ────────────────────────────────────────────────────────────────

        private static void TestResultPendingExitSemantics()
        {
            // ── L1：ResultPending + 认证显式 Exited(0) ⇒ Exited；进程未退出前不得释放端口 ──
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();
                RecordingSink sink = new RecordingSink();
                PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, MakeOptions(),
                    MakeRequest("match-l1", "ds-l1", 1u, 0xAABBCCDDu, 1u, DefaultRoster));
                int port = coordinator.AllocatedPort;
                coordinator.BeginStart();
                DsView ds = DsView.FromCoordinator(coordinator);
                byte[] ready = ds.Ready(coordinator.AllocatedPort, ds.CollisionDigest, true);
                coordinator.OnControlPayload(ready, 0, ready.Length);
                coordinator.MarkRunning();

                byte[] result = ds.Result(0x100000001UL, 0, Summary("l1-smoke"));
                CheckEq(coordinator.OnControlPayload(result, 0, result.Length).Outcome,
                    PMDsCoordinatorOutcome.Applied, "L1 结果被接受");
                CheckEq(coordinator.State, PMDsSessionState.ResultPending, "L2 状态 ResultPending");

                // DS 收到 ResultAck 后先声明 Exited(0)，进程此刻**还没退**（真实：RequestExit 是异步的）。
                launcher.Process.KillExitsProcess = false;
                byte[] exited = ds.Exited(0);
                CheckEq(coordinator.OnControlPayload(exited, 0, exited.Length).Outcome,
                    PMDsCoordinatorOutcome.Applied, "L3 认证显式 Exited(0) 被接受");
                CheckEq(coordinator.State, PMDsSessionState.Exited, "L4 终态为 Exited（旧实现误判 Failed）");
                Check(coordinator.PeerExitObserved, "L5 对端收尾已观测");
                Check(coordinator.LastReason != null
                    && coordinator.LastReason.IndexOf("对端经认证显式 Exited", StringComparison.Ordinal) >= 0,
                    "L6 原因标明是认证显式退出：" + coordinator.LastReason);
                Check(coordinator.IsAwaitingProcessExit, "L7 进程未退出 ⇒ 仍在延迟收尾");
                CheckNeg(coordinator.PortsInUse == 1, "L8 端口仍占用（不从消息推断进程已死）");
                CheckNeg(coordinator.IsPortInUse(port), "L9 该端口仍被本会话占用");
                CheckEq(coordinator.RosterCount, 4, "L10 名册仍占用");
                CheckEq(sink.Count(PMDsCoordinatorEffect.ResourcesReleased), 0, "L11 未释放资源");
                Check(coordinator.IsAwaitingGracefulExit, "L11b 认证正常退出进入优雅退出宽限");
                CheckNeg(coordinator.Counters.KillRequests == 0L, "L11c 宽限内一次强杀请求都没有（正常退出不是故障收尾）");
                CheckNeg(launcher.Process.KillCount == 0, "L11d 替身进程没被 Kill 过");
                CheckNeg(launcher.Process.WaitForExitCount == 0, "L11e 宽限路径不调 WaitForExit（不阻塞宿主 pump）");

                launcher.Process.KillExitsProcess = true;
                launcher.Process.Exit(0);
                CheckEq(coordinator.Tick().Outcome, PMDsCoordinatorOutcome.Applied, "L12 进程退出后收尾完成");
                CheckEq(coordinator.State, PMDsSessionState.Exited, "L13 终态不变");
                Check(!coordinator.IsAwaitingProcessExit, "L14 收尾结束");
                CheckEq(coordinator.PortsInUse, 0, "L15 确认退出后才释放端口");
                CheckEq(coordinator.RosterCount, 0, "L16 名册释放");
                CheckNeg(!coordinator.IsPortInUse(port), "L17 端口已真正释放");
                CheckEq(sink.Count(PMDsCoordinatorEffect.ResourcesReleased), 1, "L18 资源释放恰好一次");
                CheckEq(coordinator.Counters.GracefulExitObserved, 1L, "L18b 宽限内自行退出被记录");
                CheckNeg(coordinator.Counters.KillRequests == 0L, "L18c 整条正常路径零强杀请求（真实 DS 的 OS exit 0 前提）");
                coordinator.Dispose();
            }

            // ── L2：ResultPending + 纯进程退出（无协议证据，exit=0） ⇒ Failed ──
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();
                RecordingSink sink = new RecordingSink();
                PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, MakeOptions(),
                    MakeRequest("match-l2", "ds-l2", 1u, 0xAABBCCDDu, 1u, DefaultRoster));
                coordinator.BeginStart();
                DsView ds = DsView.FromCoordinator(coordinator);
                byte[] ready = ds.Ready(coordinator.AllocatedPort, ds.CollisionDigest, true);
                coordinator.OnControlPayload(ready, 0, ready.Length);
                coordinator.MarkRunning();
                byte[] result = ds.Result(0x200000001UL, 0, Summary("l2-smoke"));
                coordinator.OnControlPayload(result, 0, result.Length);
                CheckEq(coordinator.State, PMDsSessionState.ResultPending, "L19 前置：ResultPending");

                // 进程提前退出：没有任何 Exited 协议证据，退出码甚至是 0 —— 仍不得当成正常完成。
                launcher.Process.Exit(0);
                CheckEq(coordinator.Tick().Outcome, PMDsCoordinatorOutcome.Applied, "L20 Tick 发现进程退出");
                CheckEq(coordinator.State, PMDsSessionState.Failed,
                    "L21 纯进程退出仍判 Failed（不得把「进程没了」当成「结果已确认」）");
                Check(coordinator.PeerExitObserved, "L22 进程退出也算对端收尾观测");
                Check(coordinator.LastReason != null
                    && coordinator.LastReason.IndexOf("结果确认前进程退出", StringComparison.Ordinal) >= 0,
                    "L23 原因标明未确认结果即退出：" + coordinator.LastReason);
                CheckEq(coordinator.PortsInUse, 0, "L24 进程确实退出后释放端口");
                coordinator.Dispose();
            }

            // ── L3：ResultPending + 认证显式 Exited(非 0) ⇒ 仍 Failed ──
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();
                RecordingSink sink = new RecordingSink();
                PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, MakeOptions(),
                    MakeRequest("match-l3", "ds-l3", 1u, 0xAABBCCDDu, 1u, DefaultRoster));
                coordinator.BeginStart();
                DsView ds = DsView.FromCoordinator(coordinator);
                byte[] ready = ds.Ready(coordinator.AllocatedPort, ds.CollisionDigest, true);
                coordinator.OnControlPayload(ready, 0, ready.Length);
                coordinator.MarkRunning();
                byte[] result = ds.Result(0x300000001UL, 0, Summary("l3-smoke"));
                coordinator.OnControlPayload(result, 0, result.Length);
                CheckEq(coordinator.State, PMDsSessionState.ResultPending, "L25 前置：ResultPending");

                launcher.Process.KillExitsProcess = false;
                byte[] exited = ds.Exited(7);
                CheckEq(coordinator.OnControlPayload(exited, 0, exited.Length).Outcome,
                    PMDsCoordinatorOutcome.Applied, "L26 显式 Exited(7) 被接受");
                CheckEq(coordinator.State, PMDsSessionState.Failed, "L27 非 0 显式退出 ⇒ Failed");
                Check(coordinator.LastReason != null
                    && coordinator.LastReason.IndexOf("结果确认前进程退出", StringComparison.Ordinal) >= 0,
                    "L28 原因按「未确认结果即退出」记录：" + coordinator.LastReason);
                CheckNeg(!coordinator.IsAwaitingGracefulExit, "L28b 非 0 显式退出不走优雅退出宽限");
                CheckEq(coordinator.Counters.KillRequests, 1L, "L28c 故障收尾立即请求强杀（与正常路径明确区分）");
                CheckEq(coordinator.Counters.GracefulExitWaits, 0L, "L28d 故障路径没有宽限等待");
                CheckEq(coordinator.Counters.GracefulExitTimeouts, 0L, "L28e 故障路径不记宽限超时");
                coordinator.Dispose();
            }

            // ── L4：ResultPending + **未认证**的 Exited ⇒ RejectedMac，不得改状态 ──
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();
                RecordingSink sink = new RecordingSink();
                PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, MakeOptions(),
                    MakeRequest("match-l4", "ds-l4", 1u, 0xAABBCCDDu, 1u, DefaultRoster));
                coordinator.BeginStart();
                DsView ds = DsView.FromCoordinator(coordinator);
                byte[] ready = ds.Ready(coordinator.AllocatedPort, ds.CollisionDigest, true);
                coordinator.OnControlPayload(ready, 0, ready.Length);
                coordinator.MarkRunning();
                byte[] result = ds.Result(0x400000001UL, 0, Summary("l4-smoke"));
                coordinator.OnControlPayload(result, 0, result.Length);
                CheckEq(coordinator.State, PMDsSessionState.ResultPending, "L29 前置：ResultPending");

                byte[] foreignKey = PMDsMatchKey.Create().ExportKeyBytes();
                PMDsControlSigner foreignSigner = new PMDsControlSigner(foreignKey);
                byte[] forged = foreignSigner.Sign(PMDsControlMessage.Create(
                    PMDsControlMessageType.Exited, "match-l4", "ds-l4", 1u, 0xAABBCCDDu, 0UL,
                    new PMDsExitedBody { ExitCode = 0 }));
                CheckEq(coordinator.OnControlPayload(forged, 0, forged.Length).Outcome,
                    PMDsCoordinatorOutcome.RejectedMac, "L30 未认证 Exited 被拒（MAC）");
                CheckEq(coordinator.State, PMDsSessionState.ResultPending, "L31 状态不变（正常完成必须有认证证据）");
                CheckNeg(!coordinator.PeerExitObserved, "L32 未认证消息不构成对端收尾观测");

                launcher.Process.Exit(0);
                coordinator.Tick();
                CheckEq(coordinator.State, PMDsSessionState.Failed, "L33 随后进程真退出仍按 Failed 收尾");
                coordinator.Dispose();
            }
        }

        // ────────────────────────────────────────────────────────────────
        // M. 控制面双向 liveness（R3-B 长局误退出修复）
        //
        // 背景：DS（PMDsLobbyAgent）在 Ready 之后需要一个「对端还活着」的可信信号，
        // 否则它只能用「收到过任何下行帧」当活性判据 —— 而当前协议下 Lobby 只在结果受理后
        // 才下行 ResultAck，于是**长局必然被 DS 自判失败**（真实用户 30 秒误退出）。
        // 冻结做法：Lobby 在合法 Ready / 合法 Heartbeat 之后回一条**本局密钥签名**的 Heartbeat
        // （按消息类型读，与 ResultAck 分开），并叠一层时间节流；被拒的帧（状态/身份/MAC）
        // 一律**不回、不刷新任何 liveness 时钟**，因此不可能用坏帧续命。
        // ────────────────────────────────────────────────────────────────

        private static void TestControlFaceLiveness()
        {
            // ── M1：合法 Ready → 立即回一条签名心跳；重复 Ready 不重复回；合法心跳跨窗回；
            //         同刻连发只回一条；结果路径不受污染 ──
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();
                RecordingSink sink = new RecordingSink();
                PMDsCoordinatorOptions options = MakeOptions();
                PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, options,
                    MakeRequest("match-m1", "ds-m1", 11u, 0xAABBCCDDu, 0x0F0F0F0Fu, DefaultRoster));
                coordinator.BeginStart();
                DsView ds = DsView.FromCoordinator(coordinator);
                int port = coordinator.AllocatedPort;

                byte[] ready = ds.Ready(port, ds.CollisionDigest, true);
                CheckEq(coordinator.OnControlPayload(ready, 0, ready.Length).Outcome,
                    PMDsCoordinatorOutcome.Applied, "M1 合法 Ready 被接受");

                List<byte[]> replies = sink.Payloads(PMDsControlMessageType.Heartbeat);
                CheckEq(replies.Count, 1, "M2 Ready 之后立即回一条心跳（双向 liveness）");

                PMDsControlMessage reply = replies.Count > 0
                    ? PMDsControlCodec.Decode(replies[0], 0, replies[0].Length) : null;
                Check(reply != null && reply.Type == PMDsControlMessageType.Heartbeat,
                    "M3 应答消息类型就是既有 Heartbeat（不新增 wire 类型）");
                if (reply != null)
                {
                    Check(ds.Signer.Verify(reply), "M4 应答由本局密钥签名（DS 侧可验）");
                    CheckEq(reply.MatchId, "match-m1", "M5 应答 MatchId 与会话一致");
                    CheckEq(reply.DsId, "ds-m1", "M6 应答 DsId 与会话一致");
                    CheckEq(reply.Epoch, 11u, "M7 应答 Epoch 与会话一致");
                    CheckEq(reply.ProtocolHash, 0xAABBCCDDu, "M8 应答 ProtocolHash 与会话一致");
                    CheckEq(reply.AsHeartbeat.PlayerCount, DefaultRoster.Length, "M9 应答 PlayerCount = 名册人数");
                }

                CheckEq(coordinator.Counters.HeartbeatReplies, 1L, "M10 HeartbeatReplies = 1（出站计数）");
                CheckEq(coordinator.State, PMDsSessionState.Ready, "M11 应答不改变状态（仍 Ready）");
                CheckEq(coordinator.Counters.ReadyPublications, 1L, "M12 就绪发布仍只一次");
                Check(sink.Count(PMDsCoordinatorEffect.ControlMessageOut) >= 1,
                    "M13 心跳应答经 ControlMessageOut 出栈（宿主按既有路径投递）");

                CheckEq(coordinator.OnControlPayload(ready, 0, ready.Length).Outcome,
                    PMDsCoordinatorOutcome.Duplicate, "M14 重复 Ready 仍幂等");
                CheckEq(sink.Payloads(PMDsControlMessageType.Heartbeat).Count, 1,
                    "M15 重复 Ready 不重复应答（幂等语义不变）");

                clock.Advance(1000);
                byte[] heartbeat = ds.Heartbeat(1000, DefaultRoster.Length);
                CheckEq(coordinator.OnControlPayload(heartbeat, 0, heartbeat.Length).Outcome,
                    PMDsCoordinatorOutcome.Applied, "M16 合法心跳被接受");
                CheckEq(sink.Payloads(PMDsControlMessageType.Heartbeat).Count, 2,
                    "M17 合法心跳得到一条应答");
                CheckEq(coordinator.Counters.Heartbeats, 1L, "M18 入站心跳计数与出站应答分开");

                int replyBefore = sink.Payloads(PMDsControlMessageType.Heartbeat).Count;
                long throttledBefore = coordinator.Counters.HeartbeatRepliesThrottled;
                for (int i = 0; i < 8; i++)
                {
                    coordinator.OnControlPayload(heartbeat, 0, heartbeat.Length);
                }

                CheckEq(sink.Payloads(PMDsControlMessageType.Heartbeat).Count, replyBefore,
                    "M19 同一时刻连发 8 条心跳不产生回声风暴（0 条新应答）");
                CheckEq(coordinator.Counters.HeartbeatRepliesThrottled, throttledBefore + 8L,
                    "M20 被节流的次数可观测（确实在限流）");

                clock.Advance(1000);
                coordinator.OnControlPayload(heartbeat, 0, heartbeat.Length);
                CheckEq(sink.Payloads(PMDsControlMessageType.Heartbeat).Count, replyBefore + 1,
                    "M21 跨过节流窗口后恢复应答");

                byte[] result = ds.Result(0x900000001UL, 0, Summary("m1-smoke"));
                CheckEq(coordinator.OnControlPayload(result, 0, result.Length).Outcome,
                    PMDsCoordinatorOutcome.Applied, "M22 结果仍被接受（心跳应答不污染结果路径）");
                CheckEq(sink.Payloads(PMDsControlMessageType.ResultAck).Count, 1,
                    "M23 ResultAck 仍恰好 1 条（按消息类型读）");
                CheckEq(sink.Count(PMDsCoordinatorEffect.ResultAccepted), 1, "M24 权威结果入口仍只触发一次");
                CheckEq(coordinator.Counters.ResultAcksSent, 1L, "M25 ResultAck 计数 = 1");
                CheckEq(coordinator.State, PMDsSessionState.ResultPending, "M26 状态 ResultPending");
                coordinator.Dispose();
            }

            // ── M2：发送有界（60 秒 @1Hz 心跳 ⇒ 应答 ≤ elapsed/interval + 1） ──
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();
                RecordingSink sink = new RecordingSink();
                PMDsCoordinatorOptions options = MakeOptions();
                PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, options,
                    MakeRequest("match-m2", "ds-m2", 12u, 0xAABBCCDDu, 0x0F0F0F0Fu, DefaultRoster));
                coordinator.BeginStart();
                DsView ds = DsView.FromCoordinator(coordinator);
                byte[] ready = ds.Ready(coordinator.AllocatedPort, ds.CollisionDigest, true);
                coordinator.OnControlPayload(ready, 0, ready.Length);
                coordinator.MarkRunning();

                byte[] heartbeat = ds.Heartbeat(2000, DefaultRoster.Length);
                for (int i = 0; i < 60; i++)
                {
                    clock.Advance(1000);
                    coordinator.OnControlPayload(heartbeat, 0, heartbeat.Length);
                }

                const long elapsed = 60000L;
                long interval = (long)options.HeartbeatReplyMinInterval.TotalMilliseconds;
                long upperBound = elapsed / interval + 1L;
                CheckEq(coordinator.Counters.HeartbeatReplies, 61L,
                    "M27 60 条 1Hz 心跳得到 61 条应答（Ready 1 + 心跳 60）");
                Check(coordinator.Counters.HeartbeatReplies <= upperBound,
                    "M28 应答数不超过 elapsed/interval + 1 = " + upperBound
                    + "（实际 " + coordinator.Counters.HeartbeatReplies + "）");
                CheckEq(coordinator.Counters.HeartbeatRepliesThrottled, 0L,
                    "M29 频率恰好等于节流窗口时不应被压（上界来自节流而非丢帧）");
                CheckEq(coordinator.Counters.Heartbeats, 60L, "M30 入站心跳计数 = 60（未吞帧）");
                CheckEq(coordinator.State, PMDsSessionState.Running, "M31 高频心跳不改状态");
                coordinator.Dispose();
            }

            // ── M3：未启动（Allocated）心跳不能改状态、不回、不发布地址 ──
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();
                RecordingSink sink = new RecordingSink();
                PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, MakeOptions(),
                    MakeRequest("match-m3", "ds-m3", 13u, 0xAABBCCDDu, 0x0F0F0F0Fu, DefaultRoster));
                DsView ds = DsView.FromCoordinator(coordinator);

                byte[] heartbeat = ds.Heartbeat(10, DefaultRoster.Length);
                CheckEq(coordinator.OnControlPayload(heartbeat, 0, heartbeat.Length).Outcome,
                    PMDsCoordinatorOutcome.RejectedState, "M32 未启动（Allocated）不接受 Heartbeat");
                CheckEq(coordinator.State, PMDsSessionState.Allocated, "M33 状态未被改变");
                CheckEq(coordinator.Counters.Heartbeats, 0L, "M34 未计入心跳");
                CheckEq(coordinator.Counters.HeartbeatReplies, 0L, "M35 未产生任何应答");
                CheckEq(sink.Count(PMDsCoordinatorEffect.ReadyAddressPublished), 0, "M36 未启动时绝不发布地址");
                coordinator.Dispose();
            }

            // ── M4：错身份（MAC 合法）心跳不续命、不回 ──
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();
                RecordingSink sink = new RecordingSink();
                PMDsCoordinatorOptions options = MakeOptions();
                PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, options,
                    MakeRequest("match-m4", "ds-m4", 21u, 0xAABBCCDDu, 0x0F0F0F0Fu, DefaultRoster));
                coordinator.BeginStart();
                DsView ds = DsView.FromCoordinator(coordinator);
                byte[] ready = ds.Ready(coordinator.AllocatedPort, ds.CollisionDigest, true);
                coordinator.OnControlPayload(ready, 0, ready.Length);
                coordinator.MarkRunning();

                clock.Advance(14999);
                coordinator.Tick();
                CheckEq(coordinator.State, PMDsSessionState.Running,
                    "M37 14.999 秒无合法心跳仍存活");

                byte[] wrongIdentity = ds.SignWithIdentity(PMDsControlMessageType.Heartbeat,
                    "match-other", "ds-m4", 21u, 0xAABBCCDDu, 12UL,
                    new PMDsHeartbeatBody { UptimeMilliseconds = 1L, PlayerCount = 1 });
                CheckEq(coordinator.OnControlPayload(wrongIdentity, 0, wrongIdentity.Length).Outcome,
                    PMDsCoordinatorOutcome.RejectedIdentity, "M38 错身份心跳被拒（校验在回复之前）");
                CheckEq(coordinator.Counters.FramesRejectedIdentity, 1L, "M39 记入身份拒绝");
                CheckEq(coordinator.Counters.Heartbeats, 0L, "M40 错身份未计入心跳");
                CheckEq(coordinator.Counters.HeartbeatReplies, 1L,
                    "M41 只有 Ready 那一条应答（被拒帧不回）");

                clock.Advance(1);
                coordinator.Tick();
                CheckEq(coordinator.State, PMDsSessionState.TimedOut,
                    "M42 错身份心跳没有续命：15 秒到期仍 TimedOut");
                CheckEq(coordinator.Counters.HeartbeatTimeouts, 1L, "M43 心跳超时计数");
                coordinator.Dispose();
            }

            // ── M5：坏 MAC 心跳不续命、不回 ──
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();
                RecordingSink sink = new RecordingSink();
                PMDsCoordinatorOptions options = MakeOptions();
                PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, options,
                    MakeRequest("match-m5", "ds-m5", 31u, 0xAABBCCDDu, 0x0F0F0F0Fu, DefaultRoster));
                coordinator.BeginStart();
                DsView ds = DsView.FromCoordinator(coordinator);
                byte[] ready = ds.Ready(coordinator.AllocatedPort, ds.CollisionDigest, true);
                coordinator.OnControlPayload(ready, 0, ready.Length);
                coordinator.MarkRunning();

                clock.Advance(14999);
                coordinator.Tick();
                CheckEq(coordinator.State, PMDsSessionState.Running, "M44 14.999 秒仍存活（前置）");

                PMDsControlSigner foreign = new PMDsControlSigner(PMDsMatchKey.Create().ExportKeyBytes());
                byte[] forged = foreign.Sign(PMDsControlMessage.Create(
                    PMDsControlMessageType.Heartbeat, "match-m5", "ds-m5", 31u, 0xAABBCCDDu, 0UL,
                    new PMDsHeartbeatBody { UptimeMilliseconds = 1L, PlayerCount = 1 }));
                CheckEq(coordinator.OnControlPayload(forged, 0, forged.Length).Outcome,
                    PMDsCoordinatorOutcome.RejectedMac, "M45 坏 MAC 心跳被拒");
                CheckEq(coordinator.Counters.FramesRejectedMac, 1L, "M46 记入 MAC 拒绝");
                CheckEq(coordinator.Counters.Heartbeats, 0L, "M47 坏 MAC 未计入心跳");
                CheckEq(coordinator.Counters.HeartbeatReplies, 1L, "M48 坏 MAC 不产生应答");

                clock.Advance(1);
                coordinator.Tick();
                CheckEq(coordinator.State, PMDsSessionState.TimedOut,
                    "M49 坏 MAC 没有续命：15 秒到期仍 TimedOut");
                CheckEq(coordinator.Counters.HeartbeatTimeouts, 1L, "M50 心跳超时计数");
                coordinator.Dispose();
            }
        }

        // ────────────────────────────────────────────────────────────────
        // N. 认证正常退出的优雅退出宽限（真实 DS 复现：Exited(0) 后 Unity 仍在收尾）
        //
        // 背景：真实长局 smoke 里 DS 先发**经 MAC 认证的显式 Exited(0)**，再走 Application.Quit；
        // Unity 收尾不是瞬时的。旧实现在进入终态时无条件 Process.Kill()，把这段收尾窗口打成
        // OS exit -1（而 DS 自己声明的是 exitCode=0）。冻结做法：
        //   ① 认证正常退出给一段**有界可配置**宽限（默认 5 秒）；宽限内**不请求强杀**，
        //      只用非阻塞状态查询（不调 WaitForExit 的 500ms 阻塞等待，不占住宿主 pump）；
        //   ② 宽限内进程自行退出 ⇒ 正常收尾，全程 KillRequests=0；
        //   ③ 宽限到期仍未退出 ⇒ 记录异常收尾并转入**节流**强杀；强杀失败继续占用资源；
        //   ④ 无论哪条路径，端口与名册都只在**本地确认进程退出**后释放。
        // ────────────────────────────────────────────────────────────────

        private static void TestGracefulExitGrace()
        {
            // ── N1：默认宽限 5 秒且可配置；宽限内绝不强杀 / 不阻塞 / 不释放资源 ──
            {
                PMDsCoordinatorOptions defaults = new PMDsCoordinatorOptions();
                CheckEq((long)defaults.GracefulExitGracePeriod.TotalMilliseconds, 5000L,
                    "N1 默认优雅退出宽限 = 5000ms（可配置项，不是硬编码在这里）");

                bool negativeRejected = false;
                try
                {
                    PMDsCoordinatorOptions bad = new PMDsCoordinatorOptions();
                    bad.GracefulExitGracePeriod = TimeSpan.FromMilliseconds(-1);
                    bad.Validate();
                }
                catch (ArgumentOutOfRangeException)
                {
                    negativeRejected = true;
                }

                CheckNeg(negativeRejected, "N2 负的优雅退出宽限被 Validate 拒绝（配置错误启动即炸）");

                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();
                RecordingSink sink = new RecordingSink();
                // 替身「强杀不会退出」：因此任何强杀请求都会留在 KillRequests/KillCount 里，无法被自然退出掩盖。
                launcher.Process.KillExitsProcess = false;
                PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, MakeOptions(),
                    MakeRequest("match-n1", "ds-n1", 1u, 0xAABBCCDDu, 1u, DefaultRoster));
                int port = coordinator.AllocatedPort;
                coordinator.BeginStart();
                DsView ds = DsView.FromCoordinator(coordinator);
                byte[] ready = ds.Ready(port, ds.CollisionDigest, true);
                coordinator.OnControlPayload(ready, 0, ready.Length);
                coordinator.MarkRunning();
                byte[] result = ds.Result(0xA00000001UL, 0, Summary("n1-smoke"));
                coordinator.OnControlPayload(result, 0, result.Length);
                CheckEq(coordinator.State, PMDsSessionState.ResultPending, "N3 前置：ResultPending");

                byte[] exited = ds.Exited(0);
                CheckEq(coordinator.OnControlPayload(exited, 0, exited.Length).Outcome,
                    PMDsCoordinatorOutcome.Applied, "N4 认证显式 Exited(0) 被接受");
                CheckEq(coordinator.State, PMDsSessionState.Exited, "N5 终态 = Exited（正常完成）");
                Check(coordinator.IsAwaitingProcessExit, "N6 进程未退出 ⇒ 仍在延迟收尾");
                Check(coordinator.IsAwaitingGracefulExit, "N7 且正处于优雅退出宽限内");
                CheckNeg(coordinator.Counters.KillRequests == 0L, "N8 宽限内一次强杀请求都没有（旧实现此处无条件强杀）");
                CheckNeg(launcher.Process.KillCount == 0, "N9 替身进程没被 Kill");
                CheckNeg(launcher.Process.WaitForExitCount == 0, "N10 宽限路径未调 WaitForExit（不阻塞宿主 pump）");
                Check(coordinator.ResourcesHeld, "N11 资源仍被占用");
                CheckEq(coordinator.PortsInUse, 1, "N12 端口仍占用");
                CheckEq(coordinator.RosterCount, 4, "N13 名册仍占用");
                CheckEq(sink.Count(PMDsCoordinatorEffect.ResourcesReleased), 0, "N14 未释放资源");

                // 宽限内多次 Tick（含 4999ms 接近到期）都不得强杀；宽限内重复 Exited 也不得强杀。
                clock.Advance(4999);
                coordinator.Tick();
                coordinator.Tick();
                Check(coordinator.IsAwaitingGracefulExit, "N15 4999ms 时仍在宽限内");
                CheckNeg(coordinator.Counters.KillRequests == 0L, "N16 宽限内重复 Tick 仍不强杀");
                Check(coordinator.Counters.GracefulExitWaits >= 2L,
                    "N17 宽限内等待 Tick 被计数（实际 " + coordinator.Counters.GracefulExitWaits + "）");
                CheckNeg(launcher.Process.WaitForExitCount == 0, "N18 宽限内也没调 WaitForExit");
                CheckEq(coordinator.PortsInUse, 1, "N19 宽限内端口未释放");

                CheckEq(coordinator.OnControlPayload(exited, 0, exited.Length).Outcome,
                    PMDsCoordinatorOutcome.Applied, "N20 宽限内重复 Exited 仍被受理（终态收尾）");
                CheckNeg(coordinator.Counters.KillRequests == 0L, "N21 重复 Exited 在宽限内也不强杀");

                // 宽限内进程自行退出 ⇒ 正常收尾，全程零强杀。
                launcher.Process.Exit(0);
                CheckEq(coordinator.Tick().Outcome, PMDsCoordinatorOutcome.Applied, "N22 宽限内自行退出后收尾完成");
                CheckNeg(coordinator.Counters.KillRequests == 0L, "N23 正常收尾全程零强杀请求");
                CheckEq(coordinator.Counters.GracefulExitObserved, 1L, "N24 记录「宽限内自行退出」");
                CheckEq(coordinator.Counters.GracefulExitTimeouts, 0L, "N25 未出现宽限超时（不是异常收尾）");
                Check(!coordinator.IsAwaitingProcessExit, "N26 收尾已结束");
                Check(!coordinator.IsAwaitingGracefulExit, "N27 宽限标记已清");
                CheckEq(coordinator.PortsInUse, 0, "N28 确认退出后才释放端口");
                CheckEq(coordinator.RosterCount, 0, "N29 名册释放");
                CheckNeg(!coordinator.IsPortInUse(port), "N30 端口已真正释放");
                CheckEq(sink.Count(PMDsCoordinatorEffect.ResourcesReleased), 1, "N31 资源释放恰好一次");
                coordinator.Dispose();
            }

            // ── N2：宽限**可配置**；到期仍未退出 ⇒ 记录异常收尾并节流强杀；强杀失败继续占用 ──
            {
                FakeClock clock = new FakeClock();
                FakeLauncher launcher = new FakeLauncher();
                RecordingSink sink = new RecordingSink();
                launcher.Process.KillExitsProcess = false;
                launcher.Process.ThrowOnKill = true;   // 模拟真实适配器里 Kill 抛异常（强杀失败）
                PMDsCoordinatorOptions options = MakeOptions();
                options.GracefulExitGracePeriod = TimeSpan.FromMilliseconds(1200);   // 可配置：这一局只给 1200ms
                PMDsCoordinator coordinator = NewCoordinator(clock, launcher, sink, options,
                    MakeRequest("match-n2", "ds-n2", 1u, 0xAABBCCDDu, 1u, DefaultRoster));
                int port = coordinator.AllocatedPort;
                coordinator.BeginStart();
                DsView ds = DsView.FromCoordinator(coordinator);
                byte[] ready = ds.Ready(port, ds.CollisionDigest, true);
                coordinator.OnControlPayload(ready, 0, ready.Length);
                coordinator.MarkRunning();
                byte[] result = ds.Result(0xB00000001UL, 0, Summary("n2-smoke"));
                coordinator.OnControlPayload(result, 0, result.Length);
                byte[] exited = ds.Exited(0);
                coordinator.OnControlPayload(exited, 0, exited.Length);
                CheckEq(coordinator.Counters.KillRequests, 0L, "N32 前置：自定义宽限内零强杀");

                clock.Advance(1199);
                coordinator.Tick();
                Check(coordinator.IsAwaitingGracefulExit, "N33 自定宽限 1199ms 时仍在宽限内");
                CheckEq(coordinator.Counters.KillRequests, 0L, "N34 自定义宽限内仍不强杀（配置真的生效）");

                clock.Advance(2);   // 1201ms ⇒ 超过自定义宽限
                CheckEq(coordinator.Tick().Outcome, PMDsCoordinatorOutcome.Applied,
                    "N35 宽限到期 Tick 触发异常收尾强杀");
                Check(!coordinator.IsAwaitingGracefulExit, "N36 宽限已结束");
                Check(coordinator.IsAwaitingProcessExit, "N37 进程未退出 ⇒ 仍在延迟收尾");
                CheckEq(coordinator.Counters.GracefulExitTimeouts, 1L, "N38 记录一次宽限超时（异常收尾）");
                CheckEq(coordinator.Counters.KillRequests, 1L, "N39 到期才第一次请求强杀");
                CheckEq(coordinator.Counters.KillRequestFailures, 1L, "N40 强杀失败被计数（不静默）");
                Check(coordinator.LastReason != null
                    && coordinator.LastReason.IndexOf("异常收尾", StringComparison.Ordinal) >= 0,
                    "N41 结束原因记录异常收尾：" + coordinator.LastReason);
                Check(coordinator.LastReason != null
                    && coordinator.LastReason.IndexOf("对端经认证显式 Exited", StringComparison.Ordinal) >= 0,
                    "N42 异常收尾不覆盖「认证正常退出」事实（旧断言不被改写）");
                Check(coordinator.ResourcesHeld, "N43 强杀失败 ⇒ 资源继续占用");
                CheckEq(coordinator.PortsInUse, 1, "N44 强杀失败后端口仍占用");
                CheckEq(coordinator.RosterCount, 4, "N45 强杀失败后名册仍占用");
                CheckEq(sink.Count(PMDsCoordinatorEffect.ResourcesReleased), 0, "N46 强杀失败不释放资源");

                coordinator.Tick();
                CheckEq(coordinator.Counters.KillRequests, 1L, "N47 同一时刻不重复强杀（KillRetryInterval 节流）");
                clock.Advance(1000);
                coordinator.Tick();
                CheckEq(coordinator.Counters.KillRequests, 2L, "N48 节流到期后重试强杀");
                CheckEq(coordinator.Counters.KillRequestFailures, 2L, "N49 第二次仍失败并被记录");
                CheckEq(coordinator.PortsInUse, 1, "N50 重试失败期间端口仍占用");

                // 强杀恢复可用并且进程真的退出 ⇒ 只有这时才释放（释放本身仍需下一次 Tick 确认）。
                launcher.Process.ThrowOnKill = false;
                launcher.Process.KillExitsProcess = true;
                clock.Advance(1000);
                coordinator.Tick();
                CheckEq(coordinator.Counters.KillRequests, 3L, "N51 节流到期后第三次强杀请求已发出");
                Check(coordinator.IsAwaitingProcessExit, "N52 强杀请求后仍需本地确认退出（不凭空释放）");
                CheckEq(coordinator.PortsInUse, 1, "N53 未确认退出 ⇒ 端口仍占用");
                coordinator.Tick();
                Check(!coordinator.IsAwaitingProcessExit, "N54 确认退出后收尾完成");
                CheckEq(coordinator.PortsInUse, 0, "N55 确认退出后释放端口");
                CheckEq(coordinator.RosterCount, 0, "N56 名册释放");
                CheckEq(coordinator.Counters.GracefulExitObserved, 0L, "N57 异常收尾不记「宽限内自行退出」");
                coordinator.Dispose();
            }
        }

        /// <summary>
        /// 复刻协调器的**诊断**指纹算法（winner 小端 4 字节 + summary → SHA-256 前 4 字节）。
        /// 只用来构造「同 32 位摘要、不同内容」的样本，证明判定确实不靠它。
        /// </summary>
        private static uint DiagnosticContentHash(int winnerTeamId, byte[] summary)
        {
            byte[] s = summary ?? new byte[0];
            byte[] buffer = new byte[4 + s.Length];
            buffer[0] = (byte)(winnerTeamId & 0xFF);
            buffer[1] = (byte)((winnerTeamId >> 8) & 0xFF);
            buffer[2] = (byte)((winnerTeamId >> 16) & 0xFF);
            buffer[3] = (byte)((winnerTeamId >> 24) & 0xFF);
            if (s.Length > 0)
            {
                Buffer.BlockCopy(s, 0, buffer, 4, s.Length);
            }

            byte[] hash = PMDsCrypto.Sha256(buffer, 0, buffer.Length);
            return (uint)(hash[0] | (hash[1] << 8) | (hash[2] << 16) | (hash[3] << 24));
        }

        /// <summary>
        /// 找一对内容不同、但 32 位诊断指纹相同的摘要（生日界，期望 ~6.5 万次）。
        /// 这正是旧判定「只比 4 字节摘要」会漏判的那类输入。
        /// </summary>
        private static bool TryFindDiagnosticHashCollision(out byte[] first, out byte[] second, out uint hash)
        {
            Dictionary<uint, byte[]> seen = new Dictionary<uint, byte[]>();
            for (int i = 0; i < 4000000; i++)
            {
                byte[] summary = BitConverter.GetBytes((long)i);
                uint candidate = DiagnosticContentHash(0, summary);
                byte[] other;
                if (seen.TryGetValue(candidate, out other))
                {
                    first = other;
                    second = summary;
                    hash = candidate;
                    return true;
                }

                seen[candidate] = summary;
            }

            first = null;
            second = null;
            hash = 0u;
            return false;
        }

        // ────────────────────────────────────────────────────────────────
        // 子进程模式（只用于 J 节验证真实进程适配器）
        // ────────────────────────────────────────────────────────────────

        private static int RunChildEmit(string[] args)
        {
            int stdoutLines = args.Length > 1 ? int.Parse(args[1]) : 10;
            int stderrLines = args.Length > 2 ? int.Parse(args[2]) : 2;
            int exitCode = args.Length > 3 ? int.Parse(args[3]) : 0;

            for (int i = 0; i < stdoutLines; i++)
            {
                Console.Out.WriteLine("line-" + i + " padding-padding-padding-padding-padding");
            }

            Console.Out.Flush();
            for (int i = 0; i < stderrLines; i++)
            {
                Console.Error.WriteLine("err-" + i + " padding-padding-padding-padding-padding");
            }

            Console.Error.Flush();
            return exitCode;
        }
    }
}
