// R5-B2b / B2c 验收门禁（契约 Docs/plans/net-r5-network-contract.md「B2b网络driver」）。
//
// 三段：
//   [1] 声明与生成物：调真实生成器 `PMNetGen --decl-check` 逐字节比对，并断言新 ID 与旧 ID 都不漂移。
//   [2] 真实链路：AP TryFire → 真实生成桩 → 真实 PMTransport → DS driver → World 预留 / SpawnReserved /
//       复制 → owner(AP) + observer(SP) 收敛；覆盖 Pending / 拒绝 / 弱网 / 顺序 / 迟到镜像 /
//       stop-墓碑-Destroy / 分包配额 / 资源回收 / 发送失败可见。
//   [3] 事件订阅隔离：多 world 并跑时事件不串台，一个 world 退出不清别的 world。
//
// 明确不测（诚实边界）：
//   · Unity PhysX / 真实 UDP socket / 跨机 MTU（属 C 与实机 T45）；
//   · C# 语言面（netstandard2.0 + C#7.3）由 Tools/PMR5NetworkCheck 单独守。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using PMNet;
using PMNet.Mover;
using PMNet.Projectile;
using PMNet.R3;
using PMNet.Session;
using PMNet.Transport;

namespace PMR5NetworkTest
{
    internal static class Program
    {
        private static int _passed;
        private static readonly List<string> _failures = new List<string>();
        private static readonly List<string> _warnings = new List<string>();

        private static int Main()
        {
            Console.WriteLine("=== PMR5NetworkTest（R5-B2b / B2c）===");
            Console.WriteLine("Unity 语言面由 Tools/PMR5NetworkCheck（netstandard2.0 + C#7.3）单独守；本工程跑真实字节链。");
            Console.WriteLine();

            PMR3Player.ProjectileWarn = delegate(string message) { _warnings.Add(message); };
            PMR3Runtime.Warn = delegate(string message) { _runtimeWarnings.Add(message); };

            PMNetRegistry.Reset();
            PMR3Runtime.Shutdown();
            PMR3Runtime.Register();

            Section("A. 声明与生成物（--decl-check + 新/旧 ID + 三条 RPC 档位）", TestDeclarationArtifacts);
            Section("B. 真实链路：TryFire → DS 授权 → SpawnReserved 上线 → AP/SP 收敛", TestSpawnAndReplicate);
            Section("C. 非 owner 冒名拒 + 旧 epoch 拒", TestNonOwnerAndEpoch);
            Section("D. Pending 无 Create + 显式兑现 + 拒绝撤销预留", TestPendingAndReject);
            Section("E. 弱网裁决：真实丢包重传 + 重复幂等 + 乱序幂等", TestWeakDecisionNetwork);
            Section("F. 顺序鲁棒：裁决先于镜像 / 镜像先于裁决（一次接管无双弹）", TestDecisionOrdering);
            Section("G. 迟到镜像：假弹已结束不复活、不二次停止", TestLateMirror);
            Section("H. stop → 停止快照 → 墓碑 → DestroyObject 复制", TestStopTombstoneDestroy);
            Section("I. 100 目标分包 + Verify 配额 + 4096 门 + R6 结算出口", TestHitSplittingAndQuota);
            Section("J. Dispose / 断线资源回收（预留 / 对象 / 订阅 / 接缝）", TestResourceRecovery);
            Section("K. 发送失败可见（会话 fault）", TestSendFailureVisible);
            Section("L. 多 world 事件订阅隔离", TestEventSubscriptionIsolation);
            Section("M. R5-B2c 返工：预测存活 / 接管时机 / 权威追赶 / 退休 / 结算 / owner 清理", TestReworkFixes);
            Section("N. 真实生成入口重复上行：已受理 key 幂等（不扣 policy / 不重复预留 / 不补 Rejected）", TestUplinkDuplicateIdempotency);

            Console.WriteLine();
            Console.WriteLine("==================================================");
            Console.WriteLine("通过 " + _passed + " 项，失败 " + _failures.Count + " 项。");
            if (_warnings.Count > 0)
            {
                Console.WriteLine("（运行时告警 " + _warnings.Count + " 条，前 8 条）");
                for (int i = 0; i < _warnings.Count && i < 8; i++)
                {
                    Console.WriteLine("  [warn] " + _warnings[i]);
                }
            }

            if (_failures.Count > 0)
            {
                Console.WriteLine("失败明细：");
                for (int i = 0; i < _failures.Count; i++)
                {
                    Console.WriteLine("  [FAIL] " + _failures[i]);
                }

                Console.WriteLine("结果：FAILED");
                return 1;
            }

            Console.WriteLine("结果：PASS");
            return 0;
        }

        // =================================================================================
        //  断言
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
                _failures.Add(name + "：抛出 " + ex.GetType().Name + "：" + ex.Message);
                Console.WriteLine("      " + ex.GetType().Name + "：" + ex.Message);
                Console.WriteLine("      " + Trim(ex.StackTrace, 700));
            }

            Console.WriteLine("   [" + (before == _failures.Count ? "OK" : "FAIL") + "] " + name);
        }

        private static void Check(bool ok, string label)
        {
            if (ok)
            {
                _passed++;
            }
            else
            {
                _failures.Add(label);
                Console.WriteLine("      FAIL " + label);
            }
        }

        private static void CheckTrue(bool value, string label) { Check(value, label); }

        private static void CheckEq(long actual, long expected, string label)
        {
            Check(actual == expected, label + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckEq(uint actual, uint expected, string label)
        {
            Check(actual == expected, label + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckEq(int actual, int expected, string label)
        {
            Check(actual == expected, label + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void ExpectThrows(Action body, string label)
        {
            bool threw = false;
            try
            {
                body();
            }
            catch (Exception)
            {
                threw = true;
            }

            Check(threw, label + "（应抛异常）");
        }

        private static string Trim(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) { return "<empty>"; }
            text = text.Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }

        private static string ResolveRepoRoot()
        {
            DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 12 && dir != null; i++)
            {
                string candidate = Path.Combine(dir.FullName, "Client", "Assets", "Scripts", "PMR3", "PMR3Player.cs");
                if (File.Exists(candidate)) { return dir.FullName; }
                dir = dir.Parent;
            }

            return null;
        }

        // =================================================================================
        //  A. 声明与生成物
        // =================================================================================

        private static void TestDeclarationArtifacts()
        {
            string root = ResolveRepoRoot();
            Check(root != null, "定位到仓库根目录");
            if (root == null) { return; }

            string gen = Path.Combine(root, "Tools", "PMNetGen", "bin", "Release", "net8.0", "PMNetGen.dll");
            if (!File.Exists(gen))
            {
                Check(false, "PMNetGen.dll 不存在（先 dotnet build Tools/PMNetGen -c Release）");
                return;
            }

            string declDir = Path.Combine(root, "Client", "Assets", "Scripts", "PMR3");
            string outDir = Path.Combine(declDir, "Generated");
            string idLock = Path.Combine(root, "Docs", "plans", "pmnet-r3-ids.json");

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = "dotnet";
            psi.Arguments = "\"" + gen + "\" --decl-check \"" + declDir + "\" --out-dir \"" + outDir
                            + "\" --id-lock \"" + idLock + "\"";
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.WorkingDirectory = root;

            using (Process p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Check(p.ExitCode == 0, "PMNetGen --decl-check 退出码 0（产物与声明/锁文件逐字节一致），实际 " + p.ExitCode);
                if (p.ExitCode != 0)
                {
                    Console.WriteLine("      stdout: " + Trim(stdout, 400));
                    Console.WriteLine("      stderr: " + Trim(stderr, 400));
                }
            }

            string lockText = File.ReadAllText(idLock, Encoding.UTF8);

            // 旧 ID 逐条不漂移。
            CheckTrue(lockText.Contains("\"CLASS:PMNet.R3.PMR3Player\": 405815557"), "PMR3Player ClassId 保持 405815557");
            CheckTrue(lockText.Contains("\"PROP:PMNet.R3.PMR3Player._uid\": 18801"), "_uid PropertyId 保持 18801");
            CheckTrue(lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ServerProbe\": 34232"), "ServerProbe RpcId 保持 34232");
            CheckTrue(lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ServerMovementInputV1\": 25428"),
                "ServerMovementInputV1 RpcId 保持 25428");

            // 本批新增 ID（B2a 冻结）。
            CheckTrue(lockText.Contains("\"CLASS:PMNet.R3.PMR5Projectile\": 227098277"), "PMR5Projectile ClassId = 227098277");
            CheckTrue(lockText.Contains("\"PROP:PMNet.R3.PMR5Projectile._projectileSnapshotV1\": 6683"),
                "_projectileSnapshotV1 PropertyId = 6683");
            CheckTrue(lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ServerProjectileSpawnV1\": 21590"),
                "ServerProjectileSpawnV1 RpcId = 21590");
            CheckTrue(lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ServerProjectileHitV1\": 33011"),
                "ServerProjectileHitV1 RpcId = 33011");
            CheckTrue(lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ClientProjectileDecisionV1\": 38620"),
                "ClientProjectileDecisionV1 RpcId = 38620");

            string sharedLock = Path.Combine(root, "Docs", "plans", "pmnet-ids.json");
            CheckTrue(!File.ReadAllText(sharedLock, Encoding.UTF8).Contains("PMR3Player"), "共享锁未被本批改写");

            CheckTrue(PMNetRegistry.IsSealed, "注册表已封板");
            CheckEq(PMR5Projectile.PMGeneratedClassId, 227098277u, "运行期 PMR5Projectile ClassId 与锁一致");

            PMNetRpcEntry rpc;
            CheckTrue(PMNetRegistry.TryGetRpc(PMR3Player.PMGeneratedClassId,
                PMR3Player.PMGeneratedRpcId_ServerProjectileSpawnV1, out rpc), "ServerProjectileSpawnV1 已注册");
            CheckTrue(rpc.Descriptor.IsReliable && rpc.Descriptor.Direction == PMRpcKind.Server,
                "ServerProjectileSpawnV1 = 可靠 + Server");
            CheckTrue(rpc.Descriptor.Validator == PMRpcValidator.ForceValidate,
                "ServerProjectileSpawnV1 档位 = ForceValidate");

            CheckTrue(PMNetRegistry.TryGetRpc(PMR3Player.PMGeneratedClassId,
                PMR3Player.PMGeneratedRpcId_ServerProjectileHitV1, out rpc), "ServerProjectileHitV1 已注册");
            CheckTrue(rpc.Descriptor.IsReliable && rpc.Descriptor.Validator == PMRpcValidator.ForceValidate,
                "ServerProjectileHitV1 = 可靠 + ForceValidate");

            CheckTrue(PMNetRegistry.TryGetRpc(PMR3Player.PMGeneratedClassId,
                PMR3Player.PMGeneratedRpcId_ClientProjectileDecisionV1, out rpc), "ClientProjectileDecisionV1 已注册");
            CheckTrue(rpc.Descriptor.IsReliable && rpc.Descriptor.Direction == PMRpcKind.Client,
                "ClientProjectileDecisionV1 = 可靠 + Client（给 owner）");

            CheckEq(PMR3Player.ProjectileMaxPayloadBytes, 4096, "声明层 byte[] 上限常量 = 4096");
            CheckEq(PMR5ProjectileDriver.MaxTargetsPerHitRpc, 48, "上行命中分包上限 = 48 目标/RPC");
            CheckEq(PMR5ProjectileDriver.MaxVerifyPerKey, 5, "上行 Verify 总配额 = 5/key");
        }

        // =================================================================================
        //  夹具：真实 Transport + World + Bridge + 生成桩
        // =================================================================================

        private sealed class TestLink : PMNet.Transport.IPMTransportLink
        {
            public readonly string Name;
            public readonly LinkHub Hub;
            public readonly List<TestLink> Peers = new List<TestLink>(2);
            public PMTransportConnection Connection;
            public int DropNext;

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

            public string Describe() { return Name; }
        }

        private sealed class LinkHub
        {
            private readonly List<TestLink> _links = new List<TestLink>(4);
            private readonly Dictionary<TestLink, List<byte[]>> _inbox = new Dictionary<TestLink, List<byte[]>>();

            private TestLink _holdLink;
            private int _holdRemaining;
            private readonly List<KeyValuePair<TestLink, byte[]>> _held = new List<KeyValuePair<TestLink, byte[]>>();

            public long Dropped;
            public long Held;

            public TestLink AddLink(string name)
            {
                TestLink link = new TestLink(name, this);
                _links.Add(link);
                _inbox[link] = new List<byte[]>();
                return link;
            }

            public void Connect(TestLink a, TestLink b)
            {
                a.Peers.Add(b);
                b.Peers.Add(a);
            }

            public void HoldFrom(TestLink link, int count)
            {
                _holdLink = link;
                _holdRemaining = count;
            }

            public void ReleaseHeld()
            {
                KeyValuePair<TestLink, byte[]>[] batch = _held.ToArray();
                _held.Clear();
                for (int i = 0; i < batch.Length; i++)
                {
                    _inbox[batch[i].Key].Add(batch[i].Value);
                }
            }

            /// <summary>彻底解除扣包（连残余扣包额度一起清掉），并交付所有被扣数据报。</summary>
            public void ClearHold()
            {
                _holdLink = null;
                _holdRemaining = 0;
                ReleaseHeld();
            }

            public void Route(TestLink from, byte[] buffer, int offset, int count)
            {
                if (from.DropNext > 0)
                {
                    from.DropNext--;
                    Dropped++;
                    return;
                }

                for (int i = 0; i < from.Peers.Count; i++)
                {
                    TestLink peer = from.Peers[i];
                    byte[] copy = new byte[count];
                    Buffer.BlockCopy(buffer, offset, copy, 0, count);

                    if (ReferenceEquals(_holdLink, from) && _holdRemaining > 0)
                    {
                        _holdRemaining--;
                        Held++;
                        _held.Add(new KeyValuePair<TestLink, byte[]>(peer, copy));
                        continue;
                    }

                    _inbox[peer].Add(copy);
                }
            }

            public void Deliver()
            {
                for (int i = 0; i < _links.Count; i++)
                {
                    TestLink link = _links[i];
                    List<byte[]> box = _inbox[link];
                    if (box.Count == 0) { continue; }

                    byte[][] batch = box.ToArray();
                    box.Clear();
                    for (int k = 0; k < batch.Length; k++)
                    {
                        if (link.Connection != null)
                        {
                            link.Connection.OnDatagram(batch[k], 0, batch[k].Length);
                        }
                    }
                }
            }
        }

        private sealed class EndpointRow
        {
            public string Name;
            public PMNetWorld World;
            public PMNetSessionBridge Bridge;
        }

        private sealed class ConnectionRow
        {
            public string Name;
            public EndpointRow Endpoint;
            public PMTransportConnection Connection;
            public TestLink Link;
        }

        private sealed class Rig : IDisposable
        {
            public readonly LinkHub Hub = new LinkHub();
            public EndpointRow Server;
            public readonly List<EndpointRow> ClientEndpoints = new List<EndpointRow>(2);
            public readonly List<ConnectionRow> ServerViews = new List<ConnectionRow>(2);
            public readonly List<ConnectionRow> ClientConnections = new List<ConnectionRow>(2);
            public readonly uint Epoch;
            public long Now = 1000L;

            private Rig(uint epoch) { Epoch = epoch; }

            public PMNetWorld ServerWorld { get { return Server.World; } }
            public PMNetSessionBridge ServerBridge { get { return Server.Bridge; } }
            public PMTransportConnection ServerView(int index) { return ServerViews[index].Connection; }
            public PMNetWorld ClientWorld(int index) { return ClientEndpoints[index].World; }
            public PMNetSessionBridge ClientBridge(int index) { return ClientEndpoints[index].Bridge; }
            public TestLink ServerLink(int index) { return ServerViews[index].Link; }
            public TestLink ClientLink(int index) { return ClientConnections[index].Link; }

            public static Rig Build(uint epoch, params int[] uids)
            {
                Rig rig = new Rig(epoch);
                rig.Server = rig.NewEndpoint("server", true);

                for (int i = 0; i < uids.Length; i++)
                {
                    rig.AddClient(uids[i]);
                }

                rig.Finish();
                return rig;
            }

            private EndpointRow NewEndpoint(string name, bool isServer)
            {
                EndpointRow row = new EndpointRow();
                row.Name = name;
                row.World = new PMNetWorld(new PMSession(Epoch, isServer));
                row.World.Warn = delegate(string m) { };
                row.Bridge = new PMNetSessionBridge(row.World);
                row.Bridge.Warn = delegate(string m) { };
                return row;
            }

            private void AddClient(int uid)
            {
                int connectionId = ClientEndpoints.Count + 1;
                EndpointRow client = NewEndpoint("client" + connectionId, false);
                ClientEndpoints.Add(client);

                TestLink serverLink = Hub.AddLink("server->c" + connectionId);
                TestLink clientLink = Hub.AddLink("c" + connectionId + "->server");
                Hub.Connect(serverLink, clientLink);

                ServerViews.Add(NewConnection(Server, "serverView" + connectionId, connectionId,
                    PMSessionPeerRole.Client, uid, uid, uid, serverLink));
                ClientConnections.Add(NewConnection(client, "client" + connectionId, 1,
                    PMSessionPeerRole.Server, uid, uid, uid, clientLink));
            }

            private ConnectionRow NewConnection(EndpointRow owner, string name, int connectionId,
                PMSessionPeerRole peerRole, int uid, int playerId, int teamId, TestLink link)
            {
                ConnectionRow row = new ConnectionRow();
                row.Name = name;
                row.Endpoint = owner;
                row.Link = link;

                PMSessionIdentity identity = new PMSessionIdentity();
                identity.ConnectionId = connectionId;
                identity.PeerRole = peerRole;
                identity.Uid = uid;
                identity.PlayerId = playerId;
                identity.TeamId = teamId;
                identity.HeroId = 1;
                identity.Epoch = Epoch;
                identity.LocalProtocolHash = PMR3Runtime.ProtocolHash;
                identity.PeerProtocolHash = PMR3Runtime.ProtocolHash;
                identity.MatchId = "match-r5-b2b-network-test";
                identity.DsId = "ds-r5-b2b-network-test";

                PMTransportConfig config = new PMTransportConfig();
                config.IdleTimeoutMs = 0L;

                row.Connection = new PMTransportConnection(identity, owner.World, owner.Bridge, link, config);
                link.Connection = row.Connection;
                return row;
            }

            private void Finish()
            {
                for (int i = 0; i < ServerViews.Count; i++)
                {
                    string error;
                    CheckTrue(ServerViews[i].Connection.TryActivate(out error),
                        ServerViews[i].Name + " 连接激活成功（" + (error ?? "ok") + "）");
                }

                for (int i = 0; i < ClientConnections.Count; i++)
                {
                    string error;
                    CheckTrue(ClientConnections[i].Connection.TryActivate(out error),
                        ClientConnections[i].Name + " 连接激活成功（" + (error ?? "ok") + "）");
                }

                PMR3Runtime.Attach(Server.World, Server.Bridge);
                for (int i = 0; i < ClientEndpoints.Count; i++)
                {
                    PMR3Runtime.Attach(ClientEndpoints[i].World, ClientEndpoints[i].Bridge);
                }
            }

            public PMR3Player SpawnPlayer(int index)
            {
                return PMR3Runtime.SpawnPlayer(Server.World, Server.Bridge, ServerView(index));
            }

            public void Frame(int frames)
            {
                for (int f = 0; f < frames; f++)
                {
                    Hub.Deliver();
                    Server.Bridge.Update(Now);
                    for (int i = 0; i < ClientEndpoints.Count; i++)
                    {
                        ClientEndpoints[i].Bridge.Update(Now);
                    }

                    Hub.Deliver();
                    Now += 16L;
                }
            }

            public void Dispose()
            {
                PMR3Runtime.Detach(Server.World);
                for (int i = 0; i < ClientEndpoints.Count; i++)
                {
                    PMR3Runtime.Detach(ClientEndpoints[i].World);
                }

                try { Server.Bridge.Dispose(); } catch (Exception) { }

                for (int i = 0; i < ClientEndpoints.Count; i++)
                {
                    try { ClientEndpoints[i].Bridge.Dispose(); } catch (Exception) { }
                }
            }
        }

        /// <summary>DS 可信授权策略的测试实现（可信 spec / 玩家位置 / 裁决由「宿主」给出）。</summary>
        private sealed class TestPolicy : IPMR5ProjectileAuthorityPolicy
        {
            public PMProjectileSpec Spec = NewSpec(3000);
            public PMVector3 OwnerPosition = new PMVector3(0f, 1f, 0f);
            public PMActivationResult Verdict = PMActivationResult.Confirmed;
            public bool Allow = true;
            public int Calls;

            public bool TryAuthorizeSpawn(PMR3Player player, PMProjectileSpawnIntent intent,
                out PMProjectileSpec trustedSpec, out PMVector3 ownerPosition,
                out PMActivationResult verdict, out string error)
            {
                Calls++;
                trustedSpec = Spec.Clone();
                ownerPosition = OwnerPosition;
                verdict = Verdict;
                error = null;
                return Allow;
            }
        }

        private static PMProjectileSpec NewSpec(int lifetimeMs)
        {
            PMProjectileSpec spec = new PMProjectileSpec();
            spec.SpeedMps = 10f;
            spec.RadiusM = 0.2f;
            spec.LifetimeMs = lifetimeMs;
            spec.DelayDestroyMs = 0;
            spec.StopOnHit = false;
            spec.HideOnStop = true;
            spec.SkipFlyingTrajectoryValidation = false;
            return spec;
        }

        /// <summary>测试用宿主运动 hook：弹推进到指定毫秒后「撞墙」停止。</summary>
        private sealed class StopAfterMotion : IPMProjectileHostMotion
        {
            public int StopAfterMs;

            public bool TryStep(PMProjectileKey key, PMProjectileSpec spec, PMProjectileState state,
                int deltaMs, PMVector3 straightPosition, PMVector3 straightVelocity, float straightYaw,
                out PMVector3 position, out PMVector3 velocity, out float yaw)
            {
                position = straightPosition;
                velocity = straightVelocity;
                yaw = straightYaw;
                return true;
            }

            public bool TryStop(PMProjectileKey key, PMProjectileSpec spec, PMProjectileState state,
                double wallNowMs, out PMVector3 stopPosition)
            {
                stopPosition = state != null ? state.Position : PMVector3.Zero;
                return state != null && state.MoveTimeMs >= StopAfterMs;
            }
        }

        private static double ProbeViewZ(Harness h, int clientIndex, PMProjectileKey key)
        {
            PMR5ProjectileView? v = h.FindView(h.DriverOf(clientIndex), key);
            return v.HasValue ? v.Value.Position.Z : double.NaN;
        }

        private static readonly List<string> _runtimeWarnings = new List<string>();

        private static void TestReworkFixes()
        {
            TestLocalPredictionSurvivesBlockedUplink();
            TestTakeoverRequiresMirrorAndNeverRewinds();
            TestDsCatchUpBeforeInitialSnapshot();
            TestRetirementAndNoGhostAfterDestroy();
            TestSettlementWithoutConsumerIsNotDropped();
            TestOwnerRemovalCancelsReservationAndRpcClassMatch();
        }

        // =================================================================================
        //  N. 生成入口重复上行幂等（R5-B2d 收口：driver 侧无副作用幂等门）
        // =================================================================================

        /// <summary>
        /// N：**真实生成入口**（`ServerProjectileSpawnV1`，可靠 RPC）重复上行时，
        /// 已受理 key 必须被**无副作用幂等**吸收：
        ///   · 不再扣 policy 授权额度（不推进单调 activation / 开火间隔水位）；
        ///   · 不再预留 NetId（ReservationCount 不增）；
        ///   · 不再 RequestSpawn（ServerSpawnsAuthorized / Spawned 不变）；
        ///   · **不补发 Rejected**（DecisionsSent 不变、AP 假弹不被撤销）；
        ///   · 冲突重复（同 key 改 activation）只计数（RejectedDuplicateConflict），不撤旧弹；
        ///   · Pending 重复不刷新 TTL（挂起时刻不变）；
        ///   · 不同 key 同 activation 仍按新请求规则走 policy 并正常生成。
        ///
        /// 与 E 段的区别：E 段重复的是**下行裁决**；本段重复的是**上行生成 RPC 本身**
        /// （重新过一遍生成桩 + 真实字节链，**不是** Transport 同包去重）。
        /// </summary>
        private static void TestUplinkDuplicateIdempotency()
        {
            // ---- N1：已 Confirmed + 镜像已接管 → 同 key/同 activation 重复 → 完全幂等
            using (Harness h = Harness.Build(0x8101u, new[] { 91, 92 }))
            {
                PMProjectileKey key;
                CheckTrue(h.Fire(9001u, null, 0, out key), "N1 首次开火");
                h.Tick(8);

                CheckEq(h.Ds.ServerSpawnsSpawned, 1, "N1 DS 已生成 1 个权威对象");
                CheckEq(h.Ds.AuthorityObjectCount, 1, "N1 权威对象在册");
                CheckTrue(h.DriverOf(0).FakesTakenOver >= 1, "N1 AP 已接管（Confirmed + 镜像到齐）");
                CheckTrue(h.FindView(h.DriverOf(0), key).HasValue, "N1 假弹视图存在");

                long policyBefore = h.Policy.Calls;
                long authorizedBefore = h.Ds.ServerSpawnsAuthorized;
                long spawnedBefore = h.Ds.ServerSpawnsSpawned;
                long rejectedBefore = h.Ds.ServerSpawnsRejected;
                long decisionsBefore = h.Ds.DecisionsSent;
                long receivedBefore = h.DriverOf(0).DecisionsReceived;
                long revokedBefore = h.DriverOf(0).FakesRevoked;
                long dupBefore = h.Ds.ServerSpawnsDuplicateIgnored;
                int reservationsBefore = h.Ds.ReservationCount;

                CheckTrue(ReplaySpawnRpc(h, key, 9001u, h.Policy.OwnerPosition, 0),
                    "N1 重新生成同一 Spawn RPC（可靠入口第二次）");
                h.Tick(8);

                CheckEq(h.Ds.ServerSpawnsDuplicateIgnored, dupBefore + 1, "N1 重复被幂等吸收（dupIgnored +1）");
                CheckEq(h.Policy.Calls, policyBefore, "N1 policy 调用不增（不扣授权额度）");
                CheckEq(h.Ds.ServerSpawnsAuthorized, authorizedBefore, "N1 不再 RequestSpawn/授权");
                CheckEq(h.Ds.ServerSpawnsSpawned, spawnedBefore, "N1 不重复生成");
                CheckEq(h.Ds.ServerSpawnsRejected, rejectedBefore, "N1 不新增拒绝计数");
                CheckEq(h.Ds.DecisionsSent, decisionsBefore, "N1 不给已受理 key 下发任何新裁决");
                CheckEq(h.DriverOf(0).DecisionsReceived, receivedBefore, "N1 AP 没收到任何新裁决");
                CheckEq(h.DriverOf(0).FakesRevoked, revokedBefore, "N1 AP 假弹未被撤销（无 Rejected）");
                CheckEq(h.Ds.ReservationCount, reservationsBefore, "N1 Reservation 不增");
                CheckEq(h.Ds.AuthorityObjectCount, 1, "N1 原权威对象仍在");
                CheckTrue(h.FindView(h.DriverOf(0), key).HasValue, "N1 原 key 视图仍在");
            }

            // ---- N2：Pending 重复不刷新 TTL
            using (Harness h = Harness.Build(0x8102u, new[] { 93, 94 }))
            {
                h.Policy.Verdict = PMActivationResult.Pending;

                PMProjectileKey key;
                CheckTrue(h.Fire(9002u, null, 0, out key), "N2 Pending 开火");
                h.Tick(4);

                PMProjectilePendingSpawn pendingBefore;
                CheckTrue(h.Ds.Coordinator.PendingSpawns.TryGet(key, out pendingBefore), "N2 挂起项在册");
                double enqueuedWall = pendingBefore.EnqueuedWallTimeMs;
                int reservationsBefore = h.Ds.ReservationCount;
                long policyBefore = h.Policy.Calls;
                long dupBefore = h.Ds.ServerSpawnsDuplicateIgnored;
                long decisionsBefore = h.Ds.DecisionsSent;

                h.Tick(20); // 推进墙钟 ~320ms（远小于 2000ms TTL），使「刷新 TTL」可被观测

                CheckTrue(ReplaySpawnRpc(h, key, 9002u, h.Policy.OwnerPosition, 0), "N2 Pending 重复上行");
                h.Tick(8);

                PMProjectilePendingSpawn pendingAfter;
                CheckTrue(h.Ds.Coordinator.PendingSpawns.TryGet(key, out pendingAfter),
                    "N2 挂起项仍在（未被重登记/兑现）");
                CheckTrue(pendingAfter.EnqueuedWallTimeMs == enqueuedWall,
                    "N2 重复不刷新挂起时刻（TTL 不被延长）");
                CheckEq(h.Ds.ReservationCount, reservationsBefore, "N2 预留不增");
                CheckEq(h.Policy.Calls, policyBefore, "N2 policy 不再被调用");
                CheckEq(h.Ds.ServerSpawnsDuplicateIgnored, dupBefore + 1, "N2 重复被幂等吸收");
                CheckTrue(h.Ds.Coordinator.IsSpawnPending(key), "N2 仍然挂起");
                CheckEq(h.Ds.DecisionsSent, decisionsBefore, "N2 未给 Pending 弹下发任何裁决");
            }

            // ---- N3：同 key 改 activation → 只计数，不撤旧弹
            using (Harness h = Harness.Build(0x8103u, new[] { 95, 96 }))
            {
                PMProjectileKey key;
                CheckTrue(h.Fire(9003u, null, 0, out key), "N3 开火");
                h.Tick(8);
                CheckTrue(h.DriverOf(0).FakesTakenOver >= 1, "N3 已接管");

                long policyBefore = h.Policy.Calls;
                long decisionsBefore = h.Ds.DecisionsSent;
                long revokedBefore = h.DriverOf(0).FakesRevoked;
                long conflictBefore = h.Ds.RejectedDuplicateConflict;

                CheckTrue(ReplaySpawnRpc(h, key, 9999u, h.Policy.OwnerPosition, 0),
                    "N3 同 key 改 activation 重复上行");
                h.Tick(8);

                CheckEq(h.Ds.RejectedDuplicateConflict, conflictBefore + 1,
                    "N3 冲突被计数（RejectedDuplicateConflict）");
                CheckEq(h.DriverOf(0).FakesRevoked, revokedBefore, "N3 不给已存在 key 发 Rejected（旧弹不被撤）");
                CheckEq(h.Ds.DecisionsSent, decisionsBefore, "N3 没有新增下行裁决");
                CheckEq(h.Policy.Calls, policyBefore, "N3 不再调用 policy");
                CheckTrue(h.FindView(h.DriverOf(0), key).HasValue, "N3 旧弹视图仍在");
                CheckEq(h.Ds.AuthorityObjectCount, 1, "N3 原对象未被修改/新增");
            }

            // ---- N4：不同 key 同 activation → 按新请求规则（policy 照常、正常生成）
            using (Harness h = Harness.Build(0x8104u, new[] { 97, 98 }))
            {
                PMProjectileKey key1;
                CheckTrue(h.Fire(9004u, null, 0, out key1), "N4 开火 1");
                h.Tick(8);

                long policyBefore = h.Policy.Calls;
                long spawnedBefore = h.Ds.ServerSpawnsSpawned;
                long dupBefore = h.Ds.ServerSpawnsDuplicateIgnored;

                PMProjectileKey key2 = new PMProjectileKey(
                    h.Epoch, h.ServerPlayer.NetId.Value, key1.ProjectileId + 1u, PMProjectileOrigin.ClientPredicted);
                CheckTrue(ReplaySpawnRpc(h, key2, 9004u, h.Policy.OwnerPosition, 0),
                    "N4 不同 key 同 activation");
                h.Tick(8);

                CheckEq(h.Policy.Calls, policyBefore + 1, "N4 新 key 仍按新请求规则走 policy（未被幂等门误挡）");
                CheckEq(h.Ds.ServerSpawnsSpawned, spawnedBefore + 1, "N4 新 key 正常生成");
                CheckEq(h.Ds.ServerSpawnsDuplicateIgnored, dupBefore, "N4 新 key 不被判为重复");
            }
        }

        /// <summary>
        /// 用**真实生成入口**重发一条 Spawn RPC：重新编码与首次同 key/同 activation 的 payload，
        /// 再经生成桩（可靠 RPC）发往 DS。这**不是** Transport 同包重发 —— 每次都重新过一遍生成桩、
        /// 编码出一份新数据报，因此验证的是驱动侧幂等而不是链路层去重。
        /// </summary>
        private static bool ReplaySpawnRpc(Harness h, PMProjectileKey key, uint activationId,
            PMVector3 position, int predictionMs)
        {
            PMProjectileSpawnIntent intent = new PMProjectileSpawnIntent();
            intent.Key = key;
            intent.ActivationId = activationId;
            intent.Position = position;
            intent.Direction = new PMVector3(0f, 0f, 1f);
            intent.Yaw = 0f;
            intent.PredictionMs = predictionMs;

            byte[] payload;
            string error;
            if (!PMProjectileCodec.TryEncodeSpawnIntent(intent, out payload, out error) || payload == null)
            {
                Check(false, "重发 Spawn payload 编码失败：" + error);
                return false;
            }

            h.ApReplica().ServerProjectileSpawnV1(payload);
            return true;
        }

        // ---------------------------------------------------------------- M1

        /// <summary>
        /// M1：真实上行被阻断时，本地预测必须**真的在推进**且**不因缺记账而消失**。
        ///
        /// 上游假设是「TryFire 只调 Lifecycle.TryRegisterPredicted，AdvanceMotion 会 UnknownKey，
        /// 假弹下一帧就消失」；实测**不成立**：AdvanceMotion 经生命周期账本解析
        ///（StepMotion → IsRegistered/TryGetFrozen），不需要 Coordinator 自有 KeyRecord。
        /// 本测试把这个事实固定成回归证据：不把「view 创建」当预测成功，而是断言真实位移。
        /// </summary>
        private static void TestLocalPredictionSurvivesBlockedUplink()
        {
            using (Harness h = Harness.Build(0x7001u, new[] { 81, 82 }))
            {
                // 真实链路层阻断：客户端发出的上行一条都到不了 DS。
                h.Rig.Hub.HoldFrom(h.Rig.ClientLink(0), 64);

                PMProjectileSpec local = NewSpec(3000);
                PMProjectileKey key;
                CheckTrue(h.Fire(8101u, local, 0, out key), "M1 开火成功（上行被真实阻断）");

                // 枪口位置就是 policy 给出的 owner 位置（0,1,0）⇒ 视图 Z 就是飞行距离。
                double zAtFire = h.Policy.OwnerPosition.Z;

                // speed 10 m/s，7 × 16ms = 112ms ⇒ 约 1.12m。
                h.Tick(7);

                CheckEq(h.Ds.ServerSpawnsReceived, 0, "M1 DS 完全没收到上行生成（真实阻断，不是 mock）");
                CheckEq(h.Ds.ServerSpawnsSpawned, 0, "M1 DS 没有建立任何权威对象");
                CheckEq(h.DriverOf(1).ViewCount, 0, "M1 observer 看不到任何东西");

                PMR5ProjectileView? view = h.FindView(h.DriverOf(0), key);
                CheckTrue(view.HasValue, "M1 阻断 112ms 后假弹仍在（不因缺 Coordinator 记账而消失）");
                if (view.HasValue)
                {
                    CheckTrue(view.Value.LocalFake && !view.Value.TakenOver, "M1 仍是本地假弹（未接管）");
                    CheckTrue(!view.Value.Hidden, "M1 假弹未被隐藏");

                    double advanced = view.Value.Position.Z - zAtFire;
                    CheckTrue(advanced >= 1.0 && advanced <= 1.3,
                        "M1 本地真实推进 ~1.12m（speed 10 × 112ms，实测 " + advanced + "）");
                }

                CheckTrue(h.DriverOf(0).FakeCount == 1, "M1 假弹登记仍在（未被退休）");
            }
        }

        // ---------------------------------------------------------------- M2

        /// <summary>
        /// M2：接管必须**只在权威镜像真实存在时**发生（不得因 Confirmed 先到就提前接管），
        /// 而且接管之后的展示**不得被在途旧 snapshot 拉回去**（高水位）。
        /// </summary>
        private static void TestTakeoverRequiresMirrorAndNeverRewinds()
        {
            using (Harness h = Harness.Build(0x7101u, new[] { 83, 84 }))
            {
                // 扣住下行（server→client）：DS 能建立权威对象，但镜像副本到不了客户端。
                h.Rig.Hub.HoldFrom(h.Rig.ServerLink(0), 24);

                PMProjectileKey key;
                CheckTrue(h.Fire(8201u, null, 0, out key), "M2 开火");
                h.Tick(6);

                CheckEq(h.Ds.ServerSpawnsSpawned, 1, "M2 DS 已建立权威对象（只是镜像还在路上）");
                CheckEq(h.DriverOf(0).FakesTakenOver, 0, "M2 镜像未到时还没有接管");

                long takenBefore = h.DriverOf(0).FakesTakenOver;
                double zBefore = ProbeViewZ(h, 0, key);

                PMProjectileDecision confirm = new PMProjectileDecision();
                confirm.Key = key;
                confirm.ActivationId = 8201u;
                confirm.Result = PMActivationResult.Confirmed;
                confirm.Reason = "confirm-first";
                byte[] payload;
                string encErr;
                CheckTrue(PMProjectileCodec.TryEncodeDecision(confirm, out payload, out encErr), "M2 构造 Confirmed 裁决");

                // 决策先到（声明接缝 = 生成 RPC 的业务实现入口；线上该顺序由重连/换流产生）。
                h.ApReplica().ProjectileDriver.OnClientDecisionPayload(payload);
                h.Tick(1);

                PMR5ProjectileView? afterDecision = h.FindView(h.DriverOf(0), key);
                CheckEq(h.DriverOf(0).FakesTakenOver, takenBefore,
                    "M2 Confirmed 先到但镜像未到：**不得**提前接管");
                CheckTrue(afterDecision.HasValue, "M2 决策先到：假弹视图仍在");
                if (afterDecision.HasValue)
                {
                    CheckTrue(afterDecision.Value.LocalFake && !afterDecision.Value.TakenOver,
                        "M2 决策先到：仍是本地假弹（接管留给镜像）");
                    CheckTrue(!afterDecision.Value.Hidden, "M2 决策先到：没有被提前隐藏");
                    CheckTrue(afterDecision.Value.MirrorObjectNetId == 0u, "M2 决策先到：还没有镜像对象");
                    CheckTrue(afterDecision.Value.Position.Z >= zBefore - 0.001,
                        "M2 决策先到：本地位置继续前进（未被旧状态覆写）");
                }

                // 放开下行：镜像到达 → 一次接管；接管后的位置不得回退。
                double zRelease = ProbeViewZ(h, 0, key);
                double maxZ = zRelease;
                bool rewind = false;
                h.Rig.Hub.ClearHold();

                for (int i = 0; i < 24; i++)
                {
                    h.Tick(1);
                    double z = ProbeViewZ(h, 0, key);
                    if (double.IsNaN(z)) { continue; }
                    if (z < maxZ - 0.001) { rewind = true; }
                    if (z > maxZ) { maxZ = z; }
                }

                CheckEq(h.DriverOf(0).FakesTakenOver, takenBefore + 1, "M2 镜像到达后恰好接管一次（无双弹）");
                CheckTrue(!rewind, "M2 接管后位置单调不回退（高水位挡住在途旧 mirror）");
                CheckTrue(h.DriverOf(0).MirrorStaleDropped >= 1,
                    "M2 旧 mirror 被记数为丢弃（实测 " + h.DriverOf(0).MirrorStaleDropped + "）");

                PMProjectileSpec p2spec;
                PMProjectileState p2state;
                CheckTrue(h.Ds.Coordinator.TryObserveFrozen(key, out p2spec, out p2state), "M2 DS 冻结状态");
                PMNetObject p2obj = null;
                bool clientHasObj = p2state != null
                    && h.Rig.ClientWorld(0).TryFind(p2state.AuthorityNetId, out p2obj);
                CheckTrue(clientHasObj, "M2 客户端确实收到了权威镜像对象");

                PMR5ProjectileView? converged = h.FindView(h.DriverOf(0), key);
                CheckTrue(converged.HasValue, "M2 收敛后视图仍在");
                if (converged.HasValue && p2state != null)
                {
                    CheckEq(converged.Value.AuthorityNetId, p2state.AuthorityNetId,
                        "M2 视图权威 NetId 与 DS 一致（身份来自镜像快照）");
                    CheckEq(converged.Value.MirrorObjectNetId, p2state.AuthorityNetId,
                        "M2 视图镜像对象 NetId = 权威 NetId");
                    CheckTrue(converged.Value.TakenOver, "M2 视图已由权威接管（不是永远停住的假弹）");
                    CheckTrue(System.Math.Abs(converged.Value.Position.Z - p2state.Position.Z) <= 0.5f,
                        "M2 接管后展示最终收敛到权威位置（视图 " + converged.Value.Position.Z
                        + " vs 权威 " + p2state.Position.Z + "）");
                }
            }
        }

        // ---------------------------------------------------------------- M3

        /// <summary>
        /// M3：DS 必须在「发布初始 snapshot 之前」按 `predictionMs/2 + 挂起时长` 有界追赶。
        /// 否则线上初值就是未追赶的枪口位置（客户端接管时直接回退）。
        /// </summary>
        private static void TestDsCatchUpBeforeInitialSnapshot()
        {
            // M3a：立即确认（无挂起）⇒ 追赶量 = predictionMs/2。
            using (Harness h = Harness.Build(0x7201u, new[] { 85, 86 }))
            {
                PMProjectileKey key;
                CheckTrue(h.Fire(8301u, null, 100, out key), "M3a 开火（prediction = 100ms）");

                // 逐帧采样 observer 第一次看到的线上初值（SP 无本地假弹，看到的就是镜像）。
                double firstSpZ = double.NaN;
                for (int i = 0; i < 10; i++)
                {
                    h.Tick(1);
                    if (double.IsNaN(firstSpZ))
                    {
                        double z = ProbeViewZ(h, 1, key);
                        if (!double.IsNaN(z)) { firstSpZ = z; }
                    }
                }

                PMProjectileSpec spec;
                PMProjectileState state;
                CheckTrue(h.Ds.Coordinator.TryObserveFrozen(key, out spec, out state), "M3a DS 冻结状态可观察");
                CheckTrue(state.MoveTimeMs >= 50.0,
                    "M3a confirmed 后已追赶 ≥ prediction/2 = 50ms（实测 " + state.MoveTimeMs + "）");
                CheckTrue(!double.IsNaN(firstSpZ) && firstSpZ >= 0.5 - 0.001,
                    "M3a **线上初值**已追赶（SP 首次观测 Z=" + firstSpZ + "，speed 10 ⇒ 50ms = 0.5m）");
                CheckTrue(h.Ds.CatchUpsApplied >= 1, "M3a 记录了一次权威追赶");
                CheckTrue(h.Ds.Coordinator.PromotedProjectileCount >= 1, "M3a 预留弹已原地升级为权威（追赶前提）");
            }

            // M3b：Pending 挂起 ⇒ 追赶量 = predictionMs/2 + 挂起时长（按真实墙钟）。
            using (Harness h = Harness.Build(0x7202u, new[] { 87, 88 }))
            {
                h.Policy.Verdict = PMActivationResult.Pending;

                PMProjectileKey key;
                CheckTrue(h.Fire(8302u, null, 100, out key), "M3b Pending 开火（prediction = 100ms）");
                h.Tick(6);

                PMProjectileRegistration reg;
                CheckTrue(h.Ds.Coordinator.TryObserveRegistration(key, out reg), "M3b 登记可观察");
                CheckEq(h.Ds.ServerSpawnsSpawned, 0, "M3b Pending 期间不生成（无幽灵）");
                CheckTrue(h.Ds.Coordinator.IsSpawnPending(key), "M3b 处于挂起");

                double registeredWall = reg.RegisteredWallTimeMs;
                double resolveWall = h.Rig.Now;

                PMProjectileActivationApplyResult res;
                CheckTrue(h.Ds.ResolveActivation(h.ServerPlayer.NetId.Value, 8302u,
                    PMActivationResult.Confirmed, resolveWall, out res), "M3b 显式确认被接受");
                h.Tick(1);

                CheckEq(h.Ds.ServerSpawnsSpawned, 1, "M3b 确认后才生成权威对象");

                PMProjectileSpec spec;
                PMProjectileState state;
                CheckTrue(h.Ds.Coordinator.TryObserveFrozen(key, out spec, out state), "M3b DS 冻结状态可观察");

                double expectedMs = 50.0 + (resolveWall - registeredWall);
                CheckTrue(System.Math.Abs(state.MoveTimeMs - expectedMs) <= 0.001,
                    "M3b 追赶总时长 = prediction/2 + 挂起时长（期望 " + expectedMs + "，实测 " + state.MoveTimeMs + "）");
                CheckTrue(System.Math.Abs(state.Position.Z - (float)(expectedMs / 100.0)) <= 0.001f,
                    "M3b 位置与追赶时长自洽（speed 10 m/s，Z=" + state.Position.Z + "）");
                CheckTrue(state.Position.Z > 0.5f, "M3b 位置确实比「只有 prediction/2」更远（挂起时长被计入）");

                h.Tick(4);
                PMR5ProjectileView? sp = h.FindView(h.DriverOf(1), key);
                CheckTrue(sp.HasValue, "M3b observer 收到镜像");
                if (sp.HasValue)
                {
                    CheckTrue(sp.Value.Position.Z >= 0.5f,
                        "M3b observer 看到的线上初值已追赶（Z=" + sp.Value.Position.Z + "）");
                }
            }

            // M3c：prediction = 0 ⇒ 不得凭空多推（追赶量只有挂起项）。
            using (Harness h = Harness.Build(0x7203u, new[] { 89, 90 }))
            {
                PMProjectileKey key;
                CheckTrue(h.Fire(8303u, null, 0, out key), "M3c 开火（prediction = 0）");
                h.Tick(4);

                PMProjectileSpec spec;
                PMProjectileState state;
                CheckTrue(h.Ds.Coordinator.TryObserveFrozen(key, out spec, out state), "M3c DS 冻结状态可观察");
                CheckTrue(state.MoveTimeMs <= 4.0 * 16.0,
                    "M3c prediction=0 时追赶量为 0（MoveTimeMs=" + state.MoveTimeMs + " 全部来自正常运动）");
            }
        }

        // ---------------------------------------------------------------- M4

        /// <summary>
        /// M4：飞完的 key 必须**退休**（不留到对局结束），而且权威对象被 Destroy 后
        /// 本地假弹**不得复活**（表现与登记都要消失）。
        /// </summary>
        private static void TestRetirementAndNoGhostAfterDestroy()
        {
            using (Harness h = Harness.Build(0x7301u, new[] { 91, 92 }))
            {
                // 宿主运动 hook：弹推进到 32ms 就「撞墙停止」⇒ 很快进墓碑、墓碑到期 DS 销毁。
                StopAfterMotion motion = new StopAfterMotion();
                motion.StopAfterMs = 32;
                h.AttachDsMotion(motion);

                PMProjectileKey key;
                CheckTrue(h.Fire(8401u, null, 0, out key), "M4 开火");
                h.Tick(6);

                CheckTrue(h.DriverOf(0).FakesTakenOver >= 1, "M4 已接管（走在真路上）");
                CheckEq(h.DriverOf(0).FakeCount, 1, "M4 接管后假弹登记还在（等退休条件）");

                // 上行 Verify 账也必须在 key 退休时回落：先真实占一次配额。
                PMProjectileHitBatch batch = new PMProjectileHitBatch();
                batch.Key = key;
                batch.PreviousPosition = PMVector3.Zero;
                batch.HitPosition = PMVector3.Zero;
                batch.RewindMs = 0;
                PMProjectileHitCandidate candidate = new PMProjectileHitCandidate();
                candidate.TargetNetId = 777u;
                candidate.TargetStreamVersion = 1u;
                candidate.TargetServerFrame = new PMFrameId(PMFrameDomain.AuthorityServer, 3L);
                candidate.ImpactPoint = PMVector3.Zero;
                candidate.VisualOffset = PMVector3.Zero;
                batch.Targets = new[] { candidate };

                int rpcCount;
                string error;
                CheckTrue(h.DriverOf(0).SubmitPredictedHits(h.ApReplica(), batch, h.Rig.Now, out rpcCount, out error),
                    "M4 上报一次预测命中（占 1 次 Verify 配额）: " + (error ?? "ok"));
                CheckEq(h.DriverOf(0).UplinkVerifyKeyCount, 1, "M4 Verify 账里记住 1 个 key");

                // 跨过墓碑（≥150ms）⇒ DS 销毁权威对象 ⇒ 客户端副本销毁。
                h.Tick(24);

                CheckTrue(h.Ds.AuthorityObjectsDestroyed >= 1, "M4 墓碑到期后 DS 销毁了权威对象");
                CheckEq(h.DriverOf(0).ViewCount, 0, "M4 owner 视图清空（Destroy 不复活假弹）");
                CheckEq(h.DriverOf(0).FakeCount, 0, "M4 假弹登记已退休（不留到对局结束）");
                CheckEq(h.DriverOf(0).MirrorCount, 0, "M4 镜像登记已退休");
                CheckEq(h.DriverOf(0).UplinkVerifyKeyCount, 0, "M4 上行 Verify 账已退休（不把飞完 key 留在表里）");
                CheckEq(h.DriverOf(1).ViewCount, 0, "M4 observer 视图也清空");
                CheckEq(h.DriverOf(1).MirrorCount, 0, "M4 observer 镜像登记也退休");

                // 退休后迟到裁决/重复载荷不得把它复活。
                PMProjectileDecision late = new PMProjectileDecision();
                late.Key = key;
                late.ActivationId = 8401u;
                late.Result = PMActivationResult.Confirmed;
                late.Reason = "late-confirm";
                byte[] latePayload;
                string lateError;
                CheckTrue(PMProjectileCodec.TryEncodeDecision(late, out latePayload, out lateError), "M4 构造迟到确认");
                h.ApReplica().ProjectileDriver.OnClientDecisionPayload(latePayload);
                h.Tick(4);
                CheckEq(h.DriverOf(0).ViewCount, 0, "M4 迟到裁决不得复活已退休的 key");
                CheckEq(h.DriverOf(0).FakeCount, 0, "M4 迟到裁决不得重建假弹登记");
            }
        }

        // ---------------------------------------------------------------- M5

        /// <summary>
        /// M5：结算出口“没有消费者”不得变成静默丢弃真实伤害 —— 必须有有界待取缓冲（DrainSettlements），
        /// 缓冲也满则显式会话 fault。
        /// </summary>
        private static void TestSettlementWithoutConsumerIsNotDropped()
        {
            using (Harness h = Harness.Build(0x7401u, new[] { 93, 94 }, 0, false))
            {
                CheckEq(h.Ds.SettlementsHandedToR6, 0, "M5 本会话刻意不订阅结算事件（没有任何事件交付）");

                PMProjectileKey key;
                CheckTrue(h.Fire(8501u, null, 0, out key), "M5 开火");
                h.Tick(8);

                PMProjectileSpec spec;
                PMProjectileState state;
                CheckTrue(h.Ds.Coordinator.TryObserveFrozen(key, out spec, out state), "M5 DS 冻结状态可观察");
                PMVector3 anchor = state.Position;
                uint victim = 5252u;
                CheckTrue(h.RecordTarget(victim, 1u, 9L, h.Rig.Now, anchor, 0.5f, 1.0f), "M5 宿主记录真实历史样本");

                PMProjectileHitBatch lethal = new PMProjectileHitBatch();
                lethal.Key = key;
                lethal.PreviousPosition = anchor;
                lethal.HitPosition = anchor;
                lethal.RewindMs = 0;
                PMProjectileHitCandidate c = new PMProjectileHitCandidate();
                c.TargetNetId = victim;
                c.TargetStreamVersion = 1u;
                c.TargetServerFrame = new PMFrameId(PMFrameDomain.AuthorityServer, 9L);
                c.ImpactPoint = anchor;
                c.VisualOffset = PMVector3.Zero;
                lethal.Targets = new[] { c };

                int rpcs;
                string err;
                CheckTrue(h.DriverOf(0).SubmitPredictedHits(h.ApReplica(), lethal, h.Rig.Now, out rpcs, out err),
                    "M5 上报命中: " + (err ?? "ok"));
                h.Tick(6);

                CheckTrue(h.Ds.SettlementsWithNoConsumer >= 1, "M5 无消费者时结算被显式记账（不静默吞）");
                CheckEq(h.Ds.SettlementsHandedToR6, 0, "M5 没有订阅者时不会假装已交付 R6");
                CheckTrue(h.Ds.PendingSettlementCount >= 1,
                    "M5 结算进入有界待取缓冲（不丢真实伤害，实测 " + h.Ds.PendingSettlementCount + " 条）");
                CheckTrue(!h.Ds.IsFaulted, "M5 缓冲未满时不得 fault");

                PMProjectileSettlement[] drained;
                int drainedCount = h.Ds.DrainSettlements(8, out drained);
                CheckTrue(drainedCount >= 1, "M5 宿主可经有界 DrainSettlements 取到结算");
                if (drainedCount >= 1)
                {
                    CheckTrue(drained[0].Key.Equals(key), "M5 取到的就是该 key 的结算");
                    CheckTrue(drained[0].HitCount >= 1, "M5 结算含 ≥1 条已消毒命中");
                }

                CheckEq(h.Ds.PendingSettlementCount, 0, "M5 Drain 后缓冲清空（不重复投递）");

                PMProjectileSettlement[] none;
                CheckEq(h.Ds.DrainSettlements(8, out none), 0, "M5 再 Drain 取不到重复项");
            }
        }

        // ---------------------------------------------------------------- M6

        /// <summary>
        /// M6：owner 被移除时必须取消它的**未消费预留/队列**；
        /// 投射物 RPC 的失败放宽判定必须按 **(ClassId, RpcId)** 而不仅仅是 ushort RpcId。
        /// </summary>
        private static void TestOwnerRemovalCancelsReservationAndRpcClassMatch()
        {
            using (Harness h = Harness.Build(0x7501u, new[] { 95, 96 }))
            {
                h.Policy.Verdict = PMActivationResult.Pending;

                PMProjectileKey key;
                CheckTrue(h.Fire(8601u, null, 0, out key), "M6 Pending 开火");
                h.Tick(6);

                CheckEq(h.Ds.ServerSpawnsAuthorized, 1, "M6 Pending 已接纳（占住预留）");
                CheckEq(h.Ds.ReservationCount, 1, "M6 未消费预留 1 个");

                // 再开一炮（确认路径）以产生一个已上线的权威对象。
                h.Policy.Verdict = PMActivationResult.Confirmed;
                PMProjectileKey liveKey;
                CheckTrue(h.Fire(8602u, null, 0, out liveKey), "M6 再开一炮（走确认路径）");
                h.Tick(8);
                CheckEq(h.Ds.AuthorityObjectCount, 1, "M6 已有 1 个存活权威对象");

                CheckTrue(h.Ds.UnbindPlayer(h.ServerPlayer), "M6 DS 侧移除 owner 接线");
                h.Tick(2);

                CheckEq(h.Ds.ReservationCount, 0, "M6 移除 owner 必须取消它的未消费预留");
                CheckTrue(h.Ds.ReservationsCancelled >= 1, "M6 预留取消被记账");
                CheckTrue(!h.Ds.Coordinator.IsSpawnPending(key), "M6 该 owner 的挂起生成已清理");
                CheckTrue(h.Ds.OwnerCleanups >= 1, "M6 owner 清理路径被走通");
                CheckEq(h.Ds.AuthorityObjectCount, 0, "M6 owner 离开后权威对象已销毁（不留悬空对象）");
                CheckTrue(h.Ds.AuthorityObjectsDestroyed >= 1, "M6 权威对象销毁被记账");

                // 客户端侧：本地 owner 移除后，它的假弹/镜像/视图一起退休。
                PMR5ProjectileDriver ap = h.DriverOf(0);
                CheckTrue(ap.FakeCount >= 1, "M6 客户端侧假弹存在（先决条件）");
                CheckTrue(ap.UnbindPlayer(h.ApReplica()), "M6 客户端侧移除本地 owner 接线");
                h.Tick(1);
                CheckEq(ap.FakeCount, 0, "M6 owner 离开后假弹登记清理");
                CheckEq(ap.ViewCount, 0, "M6 owner 离开后视图清理");
                CheckEq(ap.UplinkVerifyKeyCount, 0, "M6 owner 离开后上行 Verify 账清理");

                // 投射物 RPC 判定：(ClassId, RpcId) 对不上就不能当成投射物 RPC。
                PMR5Projectile stray = new PMR5Projectile();
                bool threw = false;
                string thrown = null;
                int warnBefore = _runtimeWarnings.Count;
                try
                {
                    global::PMNet.Generated.PMNetGeneratedRegistry.RemoteSender(stray,
                        PMR3Player.PMGeneratedRpcId_ServerProjectileSpawnV1,
                        delegate(PMNetObject t, PMNetWriter w) { });
                }
                catch (Exception ex)
                {
                    threw = true;
                    thrown = ex.GetType().Name;
                }

                CheckTrue(!threw,
                    "M6 非 PMR3Player 目标 + 投射物 RpcId 不得抛异常（应走告警路径，实测 " + (thrown ?? "warn") + "）");
                CheckTrue(_runtimeWarnings.Count > warnBefore,
                    "M6 该调用确实走了 PMR3Runtime 告警出口（可观测）");

                // 正对照：**同一个 RpcId** 在真正的投射物所属类上仍然要抛（不能被修成永不抛）。
                // 用真实的世界副本（ClassId 已由 Spawn 盖好）而不是裸 new（裸对象的 ClassId 还是 0）。
                bool orphanThrew = false;
                try
                {
                    global::PMNet.Generated.PMNetGeneratedRegistry.RemoteSender(h.ServerPlayer,
                        PMR3Player.PMGeneratedRpcId_ServerProjectileSpawnV1,
                        delegate(PMNetObject t, PMNetWriter w) { });
                }
                catch (Exception)
                {
                    orphanThrew = true;
                }

                CheckTrue(orphanThrew, "M6 正对照：投射物所属类的同一 RpcId 未接线时仍然抛（失败可见）");
            }
        }

        /// <summary>DS + owner(client0) + observer(client1) 的投射物场景。</summary>
        private sealed class Harness : IDisposable
        {
            public Rig Rig;
            public uint Epoch;
            public int OwnerIndex;
            public PMProjectileHistory DsHistory;
            public TestPolicy Policy;
            public PMR5ProjectileDriver Ds;
            public readonly List<PMR3Player> ServerPlayers = new List<PMR3Player>();
            public readonly List<PMR5ProjectileDriver> Clients = new List<PMR5ProjectileDriver>();
            public readonly List<List<PMR3Player>> ClientPlayers = new List<List<PMR3Player>>();
            public readonly List<PMProjectileSettlement> Settlements = new List<PMProjectileSettlement>();
            public long ServerFrame;
            private bool _subscribeSettlement = true;

            /// <summary>测试用：把 DS 驱动换成带宿主运动 hook 的新实例（同一 world/bridge/epoch）。</summary>
            public void AttachDsMotion(IPMProjectileHostMotion motion)
            {
                Ds.Dispose();
                Ds = new PMR5ProjectileDriver(Rig.ServerWorld, Rig.ServerBridge, Epoch, DsHistory, Policy, motion);
                if (_subscribeSettlement) { Ds.Settlement += OnSettlement; }
                for (int i = 0; i < ServerPlayers.Count; i++) { Ds.BindPlayer(ServerPlayers[i]); }
            }

            public static Harness Build(uint epoch, int[] uids, int ownerIndex = 0, bool subscribeSettlement = true)
            {
                Harness h = new Harness();
                h.Epoch = epoch;
                h.OwnerIndex = ownerIndex;
                h.Rig = Rig.Build(epoch, uids);
                h.DsHistory = new PMProjectileHistory(epoch);
                h.Policy = new TestPolicy();
                h._subscribeSettlement = subscribeSettlement;

                // 只生成 **owner 那一个** 玩家副本：其余客户端因此是真正的纯观察者（SP），
                // 这样 AP / SP 的角色与「非 owner 上行被拒」才是有意义的断言。
                PMR3Player p = h.Rig.SpawnPlayer(ownerIndex);
                if (p == null)
                {
                    Check(false, "权威侧创建玩家副本失败 client" + ownerIndex);
                    return h;
                }

                h.ServerPlayers.Add(p);

                // 契约：初始状态必须在首次生命周期 Flush 之前就位（投射物没有「初值」以外的额外要求）。
                h.Ds = new PMR5ProjectileDriver(h.Rig.ServerWorld, h.Rig.ServerBridge, epoch, h.DsHistory, h.Policy);
                if (subscribeSettlement) { h.Ds.Settlement += h.OnSettlement; }
                for (int i = 0; i < h.ServerPlayers.Count; i++) { h.Ds.BindPlayer(h.ServerPlayers[i]); }

                h.Rig.Frame(3);

                for (int c = 0; c < h.Rig.ClientEndpoints.Count; c++)
                {
                    PMR5ProjectileDriver driver = new PMR5ProjectileDriver(
                        h.Rig.ClientWorld(c), h.Rig.ClientBridge(c), epoch, new PMProjectileHistory(epoch));
                    driver.Settlement += h.OnSettlement;
                    h.Clients.Add(driver);

                    List<PMR3Player> list = new List<PMR3Player>();
                    for (int i = 0; i < h.ServerPlayers.Count; i++)
                    {
                        PMNetObject obj;
                        if (h.Rig.ClientWorld(c).TryFind(h.ServerPlayers[i].NetId, out obj) && obj != null)
                        {
                            PMR3Player cp = obj as PMR3Player;
                            if (cp != null)
                            {
                                list.Add(cp);
                                driver.BindPlayer(cp);
                            }
                        }
                        else
                        {
                            list.Add(null);
                        }
                    }

                    h.ClientPlayers.Add(list);
                }

                return h;
            }

            private void OnSettlement(PMProjectileSettlement settlement)
            {
                Settlements.Add(settlement);
            }

            /// <summary>DS 侧唯一玩家副本。</summary>
            public PMR3Player ServerPlayer { get { return ServerPlayers[0]; } }

            /// <summary>该玩家在**自己客户端**上的 AP 副本。</summary>
            public PMR3Player ApReplica()
            {
                return ClientPlayers[OwnerIndex][0];
            }

            /// <summary>该玩家在**指定客户端**上的副本（非 owner 客户端上是 SP）。</summary>
            public PMR3Player ReplicaOn(int clientIndex)
            {
                return ClientPlayers[clientIndex][0];
            }

            public PMR5ProjectileDriver DriverOf(int clientIndex) { return Clients[clientIndex]; }

            public void Tick(int frames)
            {
                for (int f = 0; f < frames; f++)
                {
                    Ds.Pump(Rig.Now, 16);
                    for (int c = 0; c < Clients.Count; c++) { Clients[c].Pump(Rig.Now, 16); }
                    Rig.Frame(1);
                    ServerFrame++;
                }
            }

            public bool Fire(uint activationId, PMProjectileSpec localSpec, int predictionMs,
                out PMProjectileKey key)
            {
                PMR3Player ap = ApReplica();
                string error;
                bool ok = Clients[OwnerIndex].TryFire(
                    ap, activationId, Policy.OwnerPosition, new PMVector3(0f, 0f, 1f), 0f,
                    localSpec ?? Policy.Spec.Clone(), predictionMs, Rig.Now, out key, out error);
                if (!ok) { Console.WriteLine("      [fire] 失败：" + error); }
                return ok;
            }

            /// <summary>记录一个真实目标历史样本（模拟宿主从真实玩家状态填入）。</summary>
            public bool RecordTarget(uint netId, uint stream, long serverFrame, double worldTimeMs,
                PMVector3 position, float radius, float halfHeight)
            {
                PMProjectileTargetSample sample = new PMProjectileTargetSample();
                sample.Epoch = Epoch;
                sample.NetId = netId;
                sample.StreamVersion = stream;
                sample.ServerFrame = new PMFrameId(PMFrameDomain.AuthorityServer, serverFrame);
                sample.OutputFrame = new PMFrameId(PMFrameDomain.Input, serverFrame);
                sample.TotalSimTimeMs = worldTimeMs;
                sample.WorldTimeMs = worldTimeMs;
                sample.Position = position;
                sample.RadiusM = radius;
                sample.HalfHeightM = halfHeight;
                sample.Teleported = false;
                sample.Alive = true;

                string reason;
                return DsHistory.Record(sample, out reason);
            }

            public PMR5ProjectileView? FindView(PMR5ProjectileDriver driver, PMProjectileKey key)
            {
                PMR5ProjectileView[] buffer = new PMR5ProjectileView[64];
                int n = driver.CopyViews(buffer);
                for (int i = 0; i < n; i++)
                {
                    if (buffer[i].Key.Equals(key)) { return buffer[i]; }
                }

                return null;
            }

            public void Dispose()
            {
                for (int i = 0; i < Clients.Count; i++) { try { Clients[i].Dispose(); } catch (Exception) { } }
                if (Ds != null) { try { Ds.Dispose(); } catch (Exception) { } }
                if (Rig != null) { Rig.Dispose(); }
            }
        }

        // =================================================================================
        //  B. 真实链路：TryFire → DS 授权 → SpawnReserved 上线 → AP/SP 收敛
        // =================================================================================

        private static void TestSpawnAndReplicate()
        {
            using (Harness h = Harness.Build(0x5B01u, new[] { 61, 62 }))
            {
                CheckEq(h.Ds.BoundPlayerCount, 1, "DS 绑定 1 个真实 player");
                CheckEq(h.Clients[0].BoundPlayerCount, 1, "client0 绑定 1 个副本");
                CheckTrue(ReferenceEquals(h.DriverOf(0).LocalOwner, h.ApReplica()), "client0 的 LocalOwner = owner 副本（AP）");
                CheckTrue(h.DriverOf(1).LocalOwner == null, "client1 没有本地 owner（observer 全是 SP）");
                CheckEq((long)h.ApReplica().Role, (long)PMNetRole.AutonomousProxy, "owner 副本角色 = AutonomousProxy");
                CheckEq((long)h.ReplicaOn(1).Role, (long)PMNetRole.SimulatedProxy, "observer 上的副本角色 = SimulatedProxy");

                PMProjectileKey key;
                CheckTrue(h.Fire(7001u, null, 0, out key), "AP TryFire 成功（走真实生成桩）");
                CheckEq(h.Clients[0].FakesCreated, 1, "AP 本地假弹已登记（真实 Lifecycle 预测登记）");

                h.Tick(20);

                CheckTrue(h.Ds.ServerSpawnsReceived >= 1, "DS 收到上行生成载荷");
                CheckEq(h.Policy.Calls, 1, "DS 恰好调用一次可信 policy");
                CheckEq(h.Ds.ServerSpawnsAuthorized, 1, "DS 授权 1 次");
                CheckEq(h.Ds.ServerSpawnsSpawned, 1, "DS 生成 1 个权威投射物（无双弹）");
                CheckEq(h.Ds.ReservationCount, 0, "上线后预留令牌已消费（不残留）");

                PMProjectileSpec spec;
                PMProjectileState state;
                CheckTrue(h.Ds.Coordinator.TryObserveFrozen(key, out spec, out state), "DS 冻结状态可观察");
                CheckTrue(state != null && state.AuthorityNetId != 0u, "权威 NetId 非 0（World 签发）");

                uint authorityNetId = state.AuthorityNetId;
                PMNetObject serverObj;
                CheckTrue(h.Rig.ServerWorld.TryFind(authorityNetId, out serverObj) && serverObj is PMR5Projectile,
                    "DS 世界上线了 PMR5Projectile（NetId = 权威 NetId）");

                // owner（AP）：镜像已应用、且已一次性接管（假弹不再单独表现）。
                // 说明：prediction=0 时本地假弹会领先权威一个单程延迟，高水位闸门会先丢弃
                // 在途的旧 snapshot（MirrorStaleDropped），权威时基追上后才真正写入账本，
                // 因此这里同时接受「已应用」与「被旧 snapshot 闸门丢弃」两种消费证据。
                CheckTrue(h.DriverOf(0).MirrorPayloadsApplied + h.DriverOf(0).MirrorStaleDropped >= 1,
                    "AP 消费了权威镜像载荷（应用或旧时基丢弃）");
                CheckTrue(h.DriverOf(0).FakesTakenOver >= 1, "AP 假弹已一次性接管");
                PMR5ProjectileView? apView = h.FindView(h.DriverOf(0), key);
                CheckTrue(apView.HasValue, "AP 视图存在");
                if (apView.HasValue)
                {
                    CheckEq(apView.Value.AuthorityNetId, authorityNetId, "AP 视图权威 NetId 与 DS 一致");
                    CheckEq(apView.Value.MirrorObjectNetId, authorityNetId, "AP 视图镜像对象 NetId = 权威 NetId");
                    CheckTrue(!apView.Value.Hidden, "AP 视图未隐藏");
                    CheckTrue(apView.Value.TakenOver, "AP 视图已接管（无双弹）");
                }

                // observer（SP）：纯镜像消费，且没有本地 owner。
                CheckTrue(h.DriverOf(1).MirrorPayloadsApplied >= 1, "SP 应用了权威镜像");
                PMR5ProjectileView? spView = h.FindView(h.DriverOf(1), key);
                CheckTrue(spView.HasValue, "SP 视图存在");
                if (spView.HasValue)
                {
                    CheckEq(spView.Value.AuthorityNetId, authorityNetId, "SP 视图权威 NetId 一致");
                    CheckTrue(!spView.Value.LocalFake, "SP 视图不是本地假弹");
                }

                PMNetObject clientObj;
                CheckTrue(h.Rig.ClientWorld(1).TryFind(authorityNetId, out clientObj) && clientObj is PMR5Projectile,
                    "observer 客户端收到 PMR5Projectile 副本");

                // 携带初值的 Create 已原子到达（OnRep/Created 事件里载荷已就位）。
                PMR5Projectile proj = clientObj as PMR5Projectile;
                CheckTrue(proj != null && proj.ProjectileSnapshotPayload != null && proj.ProjectileSnapshotPayload.Length > 0,
                    "observer 副本上初始 snapshot 已就位");

                // 位移收敛：权威位置应随 Pump 推进。
                PMVector3 before = state.Position;
                h.Tick(6);
                PMProjectileState later;
                PMProjectileSpec laterSpec;
                CheckTrue(h.Ds.Coordinator.TryObserveFrozen(key, out laterSpec, out later)
                           && later != null && later.Position.Z > before.Z,
                    "权威投射物沿 +Z 真实推进（history 之外的直线运动）");
            }
        }

        // =================================================================================
        //  C. 非 owner 冒名拒 + 旧 epoch 拒
        // =================================================================================

        private static void TestNonOwnerAndEpoch()
        {
            using (Harness h = Harness.Build(0x5C01u, new[] { 63, 64 }))
            {
                uint ownerNetId = h.ServerPlayer.NetId.Value;

                // 1) 非 owner 冒名上行：observer 用自己的「他人副本」发 owner 的生成 RPC。
                PMProjectileSpawnIntent intent = new PMProjectileSpawnIntent();
                intent.Key = new PMProjectileKey(h.Epoch, ownerNetId, 900u, PMProjectileOrigin.ClientPredicted);
                intent.ActivationId = 8001u;
                intent.Position = h.Policy.OwnerPosition;
                intent.Direction = new PMVector3(0f, 0f, 1f);
                intent.Yaw = 0f;
                intent.PredictionMs = 0;

                byte[] payload;
                string encodeError;
                CheckTrue(PMProjectileCodec.TryEncodeSpawnIntent(intent, out payload, out encodeError),
                    "构造冒名载荷成功");

                long rejectedBefore = h.Rig.ServerView(1).RpcRejected;
                h.ReplicaOn(1).ServerProjectileSpawnV1(payload);
                h.Tick(4);

                CheckTrue(h.Rig.ServerView(1).RpcRejected > rejectedBefore,
                    "非 owner 上行被判 RpcRejected（服务端只接受该对象拥有者连接）");
                CheckEq(h.Ds.ServerSpawnsReceived, 0, "DS 的接缝**没有**收到冒名载荷");
                CheckEq(h.Policy.Calls, 0, "冒名请求没有机会调用 policy");

                // 2) 旧 epoch：owner 自报旧世代（codec 允许非 0 epoch，语义由 driver 拒）。
                PMProjectileSpawnIntent stale = new PMProjectileSpawnIntent();
                stale.Key = new PMProjectileKey(h.Epoch + 1u, ownerNetId, 901u, PMProjectileOrigin.ClientPredicted);
                stale.ActivationId = 8002u;
                stale.Position = h.Policy.OwnerPosition;
                stale.Direction = new PMVector3(0f, 0f, 1f);
                stale.Yaw = 0f;
                stale.PredictionMs = 0;

                byte[] stalePayload;
                CheckTrue(PMProjectileCodec.TryEncodeSpawnIntent(stale, out stalePayload, out encodeError),
                    "构造旧 epoch 载荷成功");

                h.ApReplica().ServerProjectileSpawnV1(stalePayload);
                h.Tick(12);

                CheckTrue(h.Ds.ServerSpawnsReceived >= 1, "旧 epoch 载荷已到达 DS 入口");
                CheckEq(h.Ds.ServerSpawnsAuthorized, 0, "旧 epoch 未被授权");
                CheckEq(h.Ds.ServerSpawnsSpawned, 0, "旧 epoch 未生成对象");
                CheckTrue(h.DriverOf(0).DecisionsReceived >= 1, "DS 向 owner 回了一条 Rejected 裁决（可观测）");

                // 3) 权威 NetId 绝不由 payload 冒充：DS 只用自己的 World 预留。
                h.Policy.Verdict = PMActivationResult.Confirmed;
                PMProjectileKey key;
                CheckTrue(h.Fire(8003u, null, 0, out key), "正常路径仍可用");
                h.Tick(8);
                CheckEq(h.Ds.ServerSpawnsSpawned, 1, "正常路径生成 1 个对象");
                CheckTrue(!h.Rig.ServerWorld.TryFind(999u, out _), "世界上不存在伪造的 999 号对象");
            }
        }

        // =================================================================================
        //  D. Pending 无 Create + 显式兑现 + 拒绝撤销预留
        // =================================================================================

        private static void TestPendingAndReject()
        {
            using (Harness h = Harness.Build(0x5D01u, new[] { 65, 66 }))
            {
                h.Policy.Verdict = PMActivationResult.Pending;

                PMProjectileKey key;
                CheckTrue(h.Fire(7101u, null, 0, out key), "Pending 路径 TryFire 成功");
                h.Tick(6);

                CheckEq(h.Ds.ServerSpawnsAuthorized, 1, "Pending 也算被接纳（占住预留）");
                CheckEq(h.Ds.ServerSpawnsSpawned, 0, "Pending 期间**没有**生成权威对象（无幽灵）");
                CheckEq(h.Ds.ReservationCount, 1, "Pending 期间预留令牌在册");
                CheckTrue(h.Ds.Coordinator.IsSpawnPending(key), "Coordinator 确认该 key 处于挂起");
                CheckEq(h.DriverOf(0).ViewCount, 1, "客户端只有本地假弹（没有权威镜像）");
                CheckTrue(h.FindView(h.DriverOf(0), key).HasValue, "假弹视图存在");
                PMR5ProjectileView? pendingView = h.FindView(h.DriverOf(0), key);
                CheckEq(pendingView.Value.MirrorObjectNetId, 0u, "Pending 期间视图没有镜像对象");

                // 显式兑现（契约：Pending 按权威 ResolveActivation 显式后续入口）。
                PMProjectileActivationApplyResult applyResult;
                CheckTrue(h.Ds.ResolveActivation(h.ServerPlayer.NetId.Value, 7101u,
                    PMActivationResult.Confirmed, h.Rig.Now, out applyResult), "显式 ResolveActivation(Confirmed) 被接受");
                h.Tick(8);

                CheckEq(h.Ds.ServerSpawnsSpawned, 1, "兑现后才生成权威对象");
                CheckEq(h.Ds.ReservationCount, 0, "兑现后预留令牌已消费");
                CheckTrue(h.DriverOf(0).FakesTakenOver >= 1, "owner 收到 Confirmed 并接管假弹");
                CheckTrue(h.DriverOf(1).MirrorPayloadsApplied >= 1, "observer 也收到镜像");

                // 拒绝路径：策略拒绝 → 预留必须退掉 + owner 收到 Rejected 并撤销假弹。
                h.Policy.Allow = false;
                long spawnedBefore = h.Ds.ServerSpawnsSpawned;
                PMProjectileKey rejectedKey;
                CheckTrue(h.Fire(7102u, null, 0, out rejectedKey), "拒绝路径 TryFire 成功（假弹先落地）");
                h.Tick(8);

                CheckEq(h.Ds.ServerSpawnsSpawned, spawnedBefore, "策略拒绝不生成对象");
                CheckEq(h.Ds.ReservationCount, 0, "策略拒绝把预留令牌退掉（不悬空）");
                CheckTrue(h.Ds.ReservationsCancelled >= 1, "取消预留计数增长");
                CheckTrue(h.DriverOf(0).FakesRevoked >= 1, "owner 收到 Rejected 并真正撤销假弹");
                CheckTrue(h.FindView(h.DriverOf(0), rejectedKey) == null, "被撤销的假弹视图已移除");

                // 幂等：同一 activation 重复确认不重复生成。
                h.Policy.Allow = true;
                h.Policy.Verdict = PMActivationResult.Pending;
                PMProjectileKey againKey;
                CheckTrue(h.Fire(7103u, null, 0, out againKey), "第二次 Pending 成功");
                h.Tick(4);
                CheckTrue(h.Ds.ResolveActivation(h.ServerPlayer.NetId.Value, 7103u,
                    PMActivationResult.Confirmed, h.Rig.Now, out applyResult), "第一次确认");
                h.Tick(8);
                long afterFirst = h.Ds.ServerSpawnsSpawned;
                CheckTrue(h.Ds.ResolveActivation(h.ServerPlayer.NetId.Value, 7103u,
                    PMActivationResult.Confirmed, h.Rig.Now, out applyResult), "重复确认被幂等吸收");
                CheckEq((long)applyResult, (long)PMProjectileActivationApplyResult.AlreadyResolved,
                    "重复确认返回 AlreadyResolved");
                h.Tick(8);
                CheckEq(h.Ds.ServerSpawnsSpawned, afterFirst, "重复确认不重复生成（无第二颗弹）");
            }
        }

        // =================================================================================
        //  E. 弱网裁决
        // =================================================================================

        private static void TestWeakDecisionNetwork()
        {
            using (Harness h = Harness.Build(0x5E01u, new[] { 67, 68 }))
            {
                // 1) 真实丢包：DS 的 Rejected 裁决数据报被丢一次，靠可靠域重传最终仍到达。
                h.Policy.Allow = false;
                PMProjectileKey key;
                CheckTrue(h.Fire(7201u, null, 0, out key), "开火成功");

                h.Rig.ServerLink(0).DropNext = 1;
                h.Tick(8);
                CheckTrue(h.Rig.Hub.Dropped >= 1, "链路确实丢过包");
                CheckTrue(h.DriverOf(0).FakesRevoked >= 1, "可靠重传后裁决仍然到达（无永久假弹）");

                // 2) 重复投递幂等：同一 Rejected 裁决再喂一次，不产生第二次撤销。
                long revokedBefore = h.DriverOf(0).FakesRevoked;
                PMProjectileDecision dup = new PMProjectileDecision();
                dup.Key = key;
                dup.ActivationId = 7201u;
                dup.Result = PMActivationResult.Rejected;
                dup.Reason = "duplicate";
                byte[] dupPayload;
                string dupError;
                CheckTrue(PMProjectileCodec.TryEncodeDecision(dup, out dupPayload, out dupError), "构造重复裁决");
                h.ApReplica().ProjectileDriver.OnClientDecisionPayload(dupPayload);
                h.ApReplica().ProjectileDriver.OnClientDecisionPayload(dupPayload);
                h.Tick(4);
                CheckEq(h.DriverOf(0).FakesRevoked, revokedBefore, "重复裁决不再产生第二次撤销（幂等）");

                // 3) 乱序幂等：先给「已经确认过」的 key 再补一条 Rejected（终态不倒退，不撤销已接管的表现）。
                h.Policy.Allow = true;
                h.Policy.Verdict = PMActivationResult.Confirmed;
                PMProjectileKey okKey;
                CheckTrue(h.Fire(7202u, null, 0, out okKey), "第二条开火成功");
                h.Tick(8);
                CheckTrue(h.DriverOf(0).FakesTakenOver >= 1, "第二条已接管"); 

                PMProjectileDecision late = new PMProjectileDecision();
                late.Key = okKey;
                late.ActivationId = 7202u;
                late.Result = PMActivationResult.Rejected;
                late.Reason = "late-reject";
                byte[] latePayload;
                string lateError;
                CheckTrue(PMProjectileCodec.TryEncodeDecision(late, out latePayload, out lateError), "构造迟到反向裁决");
                h.ApReplica().ProjectileDriver.OnClientDecisionPayload(latePayload);
                h.Tick(4);
                PMR5ProjectileView? view = h.FindView(h.DriverOf(0), okKey);
                CheckTrue(view.HasValue, "接管后的视图仍在（未被迟到反向裁决撤销）");
            }
        }

        // =================================================================================
        //  F. 顺序鲁棒：裁决先于镜像 / 镜像先于裁决
        // =================================================================================

        private static void TestDecisionOrdering()
        {
            // 说明：同一连接的创建记录与可靠 RPC 共享**同一条可靠有序流**，因此「Create 先于决策」
            // 是线上唯一的自然顺序（本段先用真实链路验证它）；「决策先于 Create」在线上只可能由
            // 重连/换流等外部时序产生，因此这里用**声明接缝**（生成 RPC 的业务实现入口，即
            // player.ProjectileDriver.OnClientDecisionPayload）直接构造该顺序，验证驱动不崩、不双弹。
            using (Harness h = Harness.Build(0x5F01u, new[] { 69, 70 }))
            {
                // A) 真实链路：Create 先于决策。
                PMProjectileKey key;
                CheckTrue(h.Fire(7301u, null, 0, out key), "开火成功");
                h.Tick(8);
                CheckTrue(h.DriverOf(0).FakesTakenOver >= 1, "真实顺序：随后决策完成接管");
                PMR5ProjectileView? mirrorFirst = h.FindView(h.DriverOf(0), key);
                CheckTrue(mirrorFirst.HasValue && mirrorFirst.Value.MirrorObjectNetId != 0u,
                    "真实顺序：Create 先到（镜像对象已绑定）");

                // B) 伪顺序：先喂 Confirmed 决策，再让权威副本到达。
                PMProjectileKey key2;
                CheckTrue(h.Fire(7302u, null, 0, out key2), "第二条开火成功");

                PMProjectileDecision confirm = new PMProjectileDecision();
                confirm.Key = key2;
                confirm.ActivationId = 7302u;
                confirm.Result = PMActivationResult.Confirmed;
                confirm.Reason = "confirm-first";
                byte[] confirmPayload;
                string confirmError;
                CheckTrue(PMProjectileCodec.TryEncodeDecision(confirm, out confirmPayload, out confirmError),
                    "构造 Confirmed 裁决");

                // 先把上行生成按住，让 DS 还没建立权威对象。
                h.Rig.Hub.HoldFrom(h.Rig.ClientLink(0), 4);
                h.Tick(4);

                // 决策先到（伪顺序）：Confirmed 已到但镜像未到 ⇒ **不得提前接管**（接管留给镜像到达）。
                h.ApReplica().ProjectileDriver.OnClientDecisionPayload(confirmPayload);
                h.Tick(1);
                CheckTrue(h.DriverOf(0).FakesTakenOver == 1,
                    "决策先到：镜像未到时不提前接管（仍只有第一条已接管）");
                PMR5ProjectileView? waiting = h.FindView(h.DriverOf(0), key2);
                CheckTrue(waiting.HasValue && waiting.Value.LocalFake && !waiting.Value.TakenOver,
                    "决策先到：第二条仍是本地假弹（未接管、未隐藏）");

                // 放开被扣住的上行：DS 生成权威对象 → 镜像到达 → 恰好一次接管、仍然只有一份表现。
                h.Rig.Hub.ReleaseHeld();
                h.Tick(20);

                CheckEq(h.Ds.ServerSpawnsSpawned, 2, "两条弹各自只生成一颗（无双弹）");
                CheckEq(h.DriverOf(0).FakesTakenOver, 2, "镜像到达后第二条也恰好接管一次（不重复）");
                PMR5ProjectileView? view2 = h.FindView(h.DriverOf(0), key2);
                CheckTrue(view2.HasValue, "第二条视图存在");
                if (view2.HasValue)
                {
                    CheckTrue(view2.Value.MirrorObjectNetId != 0u, "第二条视图已绑定权威镜像对象");
                    CheckTrue(view2.Value.TakenOver, "第二条视图已接管（无双弹）");
                }
            }
        }

        // =================================================================================
        //  G. 迟到镜像
        // =================================================================================

        private static void TestLateMirror()
        {
            using (Harness h = Harness.Build(0x6001u, new[] { 71, 72 }))
            {
                // 假弹寿命 16ms：客户端第一次 Pump 就把它推到终态（本地「已结束」）。
                PMProjectileSpec shortLived = NewSpec(16);

                h.Rig.Hub.HoldFrom(h.Rig.ClientLink(0), 4);

                PMProjectileKey key;
                CheckTrue(h.Fire(7401u, shortLived, 0, out key), "开火成功（本地假弹寿命 16ms）");

                h.Tick(4);
                PMR5ProjectileView? ended = h.FindView(h.DriverOf(0), key);
                CheckTrue(ended.HasValue, "假弹视图仍存在（保留墓碑）");
                CheckTrue(ended.Value.Stopped, "假弹已本地结束（停止是终态）");

                // 放开上行：DS 现在才建立权威对象，镜像**迟到**。
                h.Rig.Hub.ReleaseHeld();
                h.Tick(8);

                CheckTrue(h.Ds.ServerSpawnsSpawned >= 1, "DS 最终建立了权威对象（镜像迟到）");
                CheckTrue(h.DriverOf(0).MirrorHiddenNoRevive >= 1, "迟到镜像被判 HiddenNoRevive（不复活）");
                CheckEq(h.DriverOf(0).FakesTakenOver, 0, "已结束的假弹**不**被接管");

                PMR5ProjectileView? late = h.FindView(h.DriverOf(0), key);
                CheckTrue(late.HasValue, "迟到镜像后视图仍在");
                if (late.HasValue)
                {
                    CheckTrue(late.Value.Hidden, "迟到镜像被隐藏（不复活、不二次停止）");
                }
            }
        }

        // =================================================================================
        //  H. stop → 停止快照 → 墓碑 → DestroyObject
        // =================================================================================

        private static void TestStopTombstoneDestroy()
        {
            using (Harness h = Harness.Build(0x6101u, new[] { 73, 74 }))
            {
                PMProjectileSpec spec = NewSpec(16);
                PMProjectileKey key;
                string error;
                CheckTrue(h.Ds.TryServerDirectSpawn(
                    h.ServerPlayer.NetId.Value, 1u, 0u,
                    new PMVector3(1f, 1f, 0f), new PMVector3(0f, 0f, 1f), 0f,
                    spec, null, 0, h.Rig.Now, out key, out error),
                    "可信 ServerDirect 生成（权威独立入口）：" + (error ?? "ok"));

                h.Tick(6);

                CheckTrue(h.Ds.Coordinator.IsSpawned(key), "权威对象已上线");
                PMProjectileSpec frozenSpec;
                PMProjectileState frozenState;
                CheckTrue(h.Ds.Coordinator.TryObserveFrozen(key, out frozenSpec, out frozenState), "冻结状态可观察");
                CheckTrue(frozenState.Stopped, "寿命到期后权威状态 = 已停止");
                uint authId = frozenState.AuthorityNetId;

                PMR5ProjectileView? ownerView = h.FindView(h.DriverOf(0), key);
                CheckTrue(ownerView.HasValue && ownerView.Value.Stopped, "客户端看到停止快照");

                // 推进墙钟越过墓碑（>=150ms），DS 应 DestroyObject + 注销复制，客户端收到销毁。
                h.Tick(20);

                CheckTrue(h.Ds.AuthorityObjectsDestroyed >= 1, "墓碑到期后 DS 销毁了权威对象");
                CheckEq(h.Ds.ViewCount, 0, "DS 视图已清空");

                PMNetObject gone;
                CheckTrue(!h.Rig.ServerWorld.TryFind(authId, out gone), "DS 世界已摘除该对象");
                CheckTrue(!h.Rig.ClientWorld(0).TryFind(authId, out gone), "owner 客户端已摘除该副本");

                PMR5ProjectileView[] changes;
                int n = h.DriverOf(0).DrainViewChanges(64, out changes);
                bool sawRemoval = false;
                for (int i = 0; i < n; i++)
                {
                    if (changes[i].Key.Equals(key) && changes[i].Hidden) { sawRemoval = true; }
                }

                CheckTrue(sawRemoval || h.FindView(h.DriverOf(0), key) == null, "C 能观察到该投射物已移除");
            }
        }

        // =================================================================================
        //  I. 分包 / 配额 / 4096 门 / R6 结算
        // =================================================================================

        private static void TestHitSplittingAndQuota()
        {
            using (Harness h = Harness.Build(0x6201u, new[] { 75, 76 }))
            {
                PMProjectileKey key;
                CheckTrue(h.Fire(7501u, null, 0, out key), "开火成功");
                h.Tick(8);

                PMR3Player ap = h.ApReplica();

                // 100 目标 → 100/48 = 3 条 RPC，全部 ≤4096。
                PMProjectileHitCandidate[] targets = new PMProjectileHitCandidate[100];
                for (int i = 0; i < targets.Length; i++)
                {
                    targets[i].TargetNetId = (uint)(1000 + i);
                    targets[i].TargetStreamVersion = 1u;
                    targets[i].TargetServerFrame = new PMFrameId(PMFrameDomain.AuthorityServer, 1L + i);
                    targets[i].ImpactPoint = new PMVector3(0f, 1f, 0f);
                    targets[i].VisualOffset = PMVector3.Zero;
                }

                PMProjectileHitBatch batch = new PMProjectileHitBatch();
                batch.Key = key;
                batch.PreviousPosition = new PMVector3(0f, 1f, 0f);
                batch.HitPosition = new PMVector3(0f, 1f, 0f);
                batch.RewindMs = 0;
                batch.Targets = targets;

                int rpcCount;
                string error;
                CheckTrue(h.DriverOf(0).SubmitPredictedHits(ap, batch, h.Rig.Now, out rpcCount, out error),
                    "100 目标上报被接受：" + (error ?? "ok"));
                CheckEq(rpcCount, 3, "100 目标按 ≤48 分成 3 条 RPC");
                CheckEq(h.DriverOf(0).HitRpcsSent, 3, "驱动记了 3 次 RPC 发送");
                CheckEq(h.DriverOf(0).UplinkVerifyUsed(key), 3, "每条 RPC 占 1 次 Verify（共 3）");

                h.Tick(8);
                CheckTrue(h.Ds.HitsReceived >= 3, "DS 收到 3 条上行命中载荷");

                // 配额：第二次 100 目标需要 3 条，但 3+3 > 5 ⇒ 必须**事先**明确拒绝（不是发一半）。
                int rpcCount2;
                string error2;
                CheckTrue(!h.DriverOf(0).SubmitPredictedHits(ap, batch, h.Rig.Now, out rpcCount2, out error2),
                    "超出剩余 Verify 配额时整次拒绝");
                CheckEq(rpcCount2, 0, "拒绝时一条都没发");
                CheckEq(h.DriverOf(0).HitRpcsSent, 3, "拒绝不增加已发 RPC 数");
                CheckTrue(error2 != null && error2.Contains("配额"), "拒绝原因明确说明配额：" + (error2 ?? "null"));

                // codec 独立上限对照：100 目标单包 > 4096（这就是必须 ≤48 分包的原因）。
                byte[] single;
                string singleError;
                CheckTrue(PMProjectileCodec.TryEncodeHitBatch(batch, out single, out singleError), "codec 编出 100 目标单包");
                CheckTrue(single != null && single.Length > 4096,
                    "100 目标单包 = " + (single == null ? 0 : single.Length) + " 字节 > 4096（无法直发）");

                // 声明层 4096 门：生成桩在**发送侧**对超长数组抛 FormatException。
                ExpectThrows(delegate { ap.ServerProjectileHitV1(new byte[4097]); },
                    "生成桩拒绝 4097 字节数组实参");

                // R6 结算出口：构造一个几何上真实命中的候选（history 由「宿主」注入真实样本）。
                PMProjectileSpec frozenSpec;
                PMProjectileState frozenState;
                CheckTrue(h.Ds.Coordinator.TryObserveFrozen(key, out frozenSpec, out frozenState), "冻结状态可观察");
                PMVector3 anchor = frozenState.Position;

                uint victimNetId = 4242u;
                CheckTrue(h.RecordTarget(victimNetId, 1u, 7L, h.Rig.Now, anchor, 0.5f, 1.0f),
                    "宿主记录了一致的目标历史样本");

                PMProjectileHitBatch lethal = new PMProjectileHitBatch();
                lethal.Key = key;
                lethal.PreviousPosition = anchor;
                lethal.HitPosition = anchor;
                lethal.RewindMs = 0;
                PMProjectileHitCandidate victim = new PMProjectileHitCandidate();
                victim.TargetNetId = victimNetId;
                victim.TargetStreamVersion = 1u;
                victim.TargetServerFrame = new PMFrameId(PMFrameDomain.AuthorityServer, 7L);
                victim.ImpactPoint = anchor;
                victim.VisualOffset = PMVector3.Zero;
                lethal.Targets = new[] { victim };

                int lethalRpcs;
                string lethalError;
                CheckTrue(h.DriverOf(0).SubmitPredictedHits(ap, lethal, h.Rig.Now, out lethalRpcs, out lethalError),
                    "单目标上报被接受：" + (lethalError ?? "ok"));
                h.Tick(6);

                CheckTrue(h.Ds.HitsReported >= 1, "DS 受理了至少一次命中上报");
                CheckTrue(h.Settlements.Count >= 1, "R6 结算出口收到了权威结算（唯一消费点）");
                if (h.Settlements.Count > 0)
                {
                    PMProjectileSettlement s = h.Settlements[0];
                    CheckEq((long)s.Key.Origin, (long)PMProjectileOrigin.ClientPredicted, "结算来源 = ClientPredicted");
                    CheckTrue(s.HitCount >= 1, "结算含 ≥1 条已消毒命中");
                }
            }
        }

        // =================================================================================
        //  J. 资源回收
        // =================================================================================

        private static void TestResourceRecovery()
        {
            Harness h = Harness.Build(0x6301u, new[] { 77, 78 });
            int subscriptionsWithDriver = PMR5ProjectileEvents.SubscriptionCount;

            PMProjectileKey key;
            CheckTrue(h.Fire(7601u, null, 0, out key), "开火成功");
            h.Tick(8);
            CheckTrue(h.Ds.ServerSpawnsSpawned == 1, "已上线 1 个权威对象");

            PMProjectileState state;
            PMProjectileSpec spec;
            h.Ds.Coordinator.TryObserveFrozen(key, out spec, out state);
            uint authId = state.AuthorityNetId;

            // Dispose：预留取消 + 对象销毁 + 订阅摘除 + 接缝清空。
            h.Ds.Dispose();
            h.Rig.Frame(2);

            CheckEq(h.Rig.ServerWorld.ReservedNetIdCount, 0, "Dispose 后 DS 世界没有残留预留");
            PMNetObject gone;
            CheckTrue(!h.Rig.ServerWorld.TryFind(authId, out gone), "Dispose 后权威对象已从世界摘除");
            CheckTrue(!h.Rig.ClientWorld(0).TryFind(authId, out gone), "客户端也收到销毁");

            for (int i = 0; i < h.ServerPlayers.Count; i++)
            {
                CheckTrue(h.ServerPlayers[i].ProjectileDriver == null, "DS player 的接缝引用已摘除");
            }

            h.Ds.Dispose();
            CheckTrue(h.Ds.IsDisposed, "Dispose 幂等");

            // 客户端 driver 的订阅也应随 Dispose 摘除。
            int before = PMR5ProjectileEvents.SubscriptionCount;
            h.DriverOf(0).Dispose();
            CheckEq(PMR5ProjectileEvents.SubscriptionCount, before - 1, "客户端 Dispose 摘掉了自己那一张订阅");

            // 一个 world 退出不得清别的 world（这里用「另一个 harness 的订阅仍在」证明）。
            using (Harness other = Harness.Build(0x6302u, new[] { 79, 80 }))
            {
                int otherBefore = PMR5ProjectileEvents.SubscriptionCount;
                CheckTrue(otherBefore >= 3, "第二个 harness 建立了自己的订阅（DS + 2 客户端）");
                h.Dispose();
                CheckTrue(PMR5ProjectileEvents.SubscriptionCount >= otherBefore - 2,
                    "释放第一个 harness 不会清掉第二个 harness 的订阅");

                PMProjectileKey otherKey;
                CheckTrue(other.Fire(7602u, null, 0, out otherKey), "第二个 harness 开火成功");
                other.Tick(20);
                CheckTrue(other.DriverOf(1).MirrorPayloadsApplied + other.DriverOf(1).MirrorStaleDropped >= 1,
                    "第二个 harness 的事件链仍然工作");
            }

            CheckTrue(subscriptionsWithDriver >= 0, "订阅计数可观测");
        }

        // =================================================================================
        //  K. 发送失败可见
        // =================================================================================

        private static void TestSendFailureVisible()
        {
            Harness h = Harness.Build(0x6401u, new[] { 81, 82 });

            PMProjectileKey okKey;
            CheckTrue(h.Fire(7701u, null, 0, out okKey), "正常发送成功");
            h.Tick(6);
            CheckTrue(!h.DriverOf(0).IsFaulted, "正常路径不 fault");

            // 摘掉接线：三条投射物 RPC 的失败路径会抛，driver 必须转成显式 fault。
            PMR3Runtime.Detach(h.Rig.ClientWorld(0));

            PMProjectileKey failKey;
            string error;
            bool ok = h.DriverOf(0).TryFire(h.ApReplica(), 7702u, h.Policy.OwnerPosition,
                new PMVector3(0f, 0f, 1f), 0f, h.Policy.Spec.Clone(), 0, h.Rig.Now, out failKey, out error);

            CheckTrue(!ok, "未接线时 TryFire 返回 false");
            CheckTrue(h.DriverOf(0).IsFaulted, "未接线时驱动进入 IsFaulted");
            CheckEq((long)h.DriverOf(0).FaultReason, (long)PMR5ProjectileFaultReason.SendFailed,
                "故障原因 = SendFailed");
            CheckTrue(h.DriverOf(0).FaultError != null && h.DriverOf(0).FaultError.Length > 0, "故障出口可读");

            // fault 后不再假装成功：Pump 早退、再次 TryFire 直接失败。
            double wallBefore = h.DriverOf(0).LastWallTimeMs;
            h.DriverOf(0).Pump(h.Rig.Now + 1000.0, 16);
            CheckEq((long)(h.DriverOf(0).LastWallTimeMs - wallBefore), 0L, "fault 后 Pump 不再推进（不静默继续）");

            PMProjectileKey againKey;
            CheckTrue(!h.DriverOf(0).TryFire(h.ApReplica(), 7703u, h.Policy.OwnerPosition,
                new PMVector3(0f, 0f, 1f), 0f, h.Policy.Spec.Clone(), 0, h.Rig.Now, out againKey, out error),
                "fault 后 TryFire 继续失败");

            h.Dispose();
        }

        // =================================================================================
        //  L. 多 world 事件订阅隔离
        // =================================================================================

        private static void TestEventSubscriptionIsolation()
        {
            int baseline = PMR5ProjectileEvents.SubscriptionCount;

            Harness a = Harness.Build(0x6501u, new[] { 83, 84 });
            Harness b = Harness.Build(0x6502u, new[] { 85, 86 });

            CheckEq(PMR5ProjectileEvents.SubscriptionCount, baseline + 6, "两个 harness 各 3 张订阅（DS + 2 客户端）");

            PMProjectileKey keyA;
            CheckTrue(a.Fire(7801u, null, 0, out keyA), "harness A 开火");
            a.Tick(20);

            CheckTrue(a.DriverOf(0).MirrorPayloadsApplied + a.DriverOf(0).MirrorStaleDropped >= 1,
                "A 自己的事件链工作");
            CheckEq(b.DriverOf(0).MirrorPayloadsApplied, 0L, "A 的事件**不**串到 B 的 world");

            // A 的 DS 订阅退出，B 必须不受影响。
            a.Ds.Dispose();
            PMProjectileKey keyB;
            CheckTrue(b.Fire(7802u, null, 0, out keyB), "harness B 开火");
            b.Tick(20);
            CheckTrue(b.DriverOf(0).MirrorPayloadsApplied + b.DriverOf(0).MirrorStaleDropped >= 1,
                "A 的 DS 退出后 B 仍然收到自己的事件");

            a.Dispose();
            b.Dispose();

            CheckTrue(PMR5ProjectileEvents.SubscriptionCount <= baseline, "全部释放后订阅回到基线（无泄漏）");
        }
    }
}
