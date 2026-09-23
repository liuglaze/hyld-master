// R3-B 门禁：PMR3 声明对象 + 运行时接线 + 纯 C# 控制代理。
//
// 事实来源：`Docs/plans/net-r3-control-contract.md` §7.1/§7.2/§7.4；
// `Docs/plans/net-architecture-migration.md` §3.9.4（身份/所有权/可靠性）与文末 R3-B 行。
//
// 覆盖：
//   A. 声明 → 生成物（`--decl-check` 逐字节比对 + 生成常量/描述符断言）
//   B. 运行时接线（Attach 幂等 / 世界工厂 / SpawnPlayer 唯一 owner / 前置拒绝）
//   C. 复制与探针往返（真实 PMTransport 字节链：Create 初值、ProbeCount 收敛、ClientEcho 的 nonce）
//   D. 归属拒绝（非 owner 的 ServerProbe 必须被拒，且实现不被执行）
//   E. 清理（Detach/Shutdown 清 static RemoteSender 与事件订阅）
//   F. Lobby 控制代理（真实 loopback TCP：Ready/Heartbeat/Result/ResultAck→退出、坏 MAC、身份不符、非 loopback）
//   G. 冻结常量
//
// **不引 Unity**：PMR3Runtime / PMR3Player / PMDsLobbyAgent 都是纯 C#，本工程直接编真实源码。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PMNet;
using PMNet.Control;
using PMNet.R3;
using PMNet.Session;
using PMNet.Transport;
using PMNet.Unity;

namespace PMR3RuntimeTest
{
    internal static class Program
    {
        private static int _passed;
        private static readonly List<string> _failures = new List<string>();

        private static int Main()
        {
            Console.WriteLine("=== R3-B：PMR3 声明对象 / 运行时接线 / 控制代理 ===");
            Console.WriteLine();

            // 诊断出口：运行时的拒绝原因必须可见（否则失败时只能猜）。
            PMR3Runtime.Warn = delegate(string m) { Console.WriteLine("      [PMR3Runtime] " + m); };

            Section("A. 声明与生成物（--decl-check + 生成常量/描述符）", TestDeclarationArtifacts);
            Section("B. 运行时接线：Attach / SpawnPlayer 唯一 owner / 前置拒绝", TestAttachAndSpawn);
            Section("C. 复制与探针往返（真实 PMTransport 字节链）", TestReplicationAndProbeRoundTrip);
            Section("D. 归属拒绝：非 owner 的 ServerProbe", TestNonOwnerRejection);
            Section("E. 清理：Detach / Shutdown 清 static 状态", TestCleanup);
            Section("F. Lobby 控制代理（真实 loopback TCP）", TestLobbyAgent);
            Section("H. 控制面双向 liveness：长局保持 / 丢下行仍退出 / 坏 MAC 不续命", TestControlFaceLiveness);
            Section("G. 冻结常量与协议摘要", TestConstants);

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

        private static void CheckEq(ulong actual, ulong expected, string label)
        {
            Check(actual == expected, label + "（实际 " + actual + "，期望 " + expected + "）");
        }

        private static void CheckStrEq(string actual, string expected, string label)
        {
            Check(string.Equals(actual, expected, StringComparison.Ordinal),
                label + "（实际 '" + (actual ?? "<null>") + "'，期望 '" + (expected ?? "<null>") + "'）");
        }

        private static void CheckTrue(bool value, string label)
        {
            Check(value, label);
        }

        // =================================================================================
        //  A. 声明与生成物
        // =================================================================================

        private static void TestDeclarationArtifacts()
        {
            string root = ResolveRepoRoot();
            Check(root != null, "定位到仓库根目录" + (root != null ? "：" + root : ""));
            if (root == null) { return; }

            // A1：生成产物与声明逐字节一致（等价于 build.bat 的 decl-check 门禁）。
            string gen = Path.Combine(root, "Tools", "PMNetGen", "bin", "Release", "net8.0", "PMNetGen.dll");
            if (!File.Exists(gen))
            {
                Check(false, "PMNetGen.dll 不存在（先 dotnet build Tools/PMNetGen -c Release）：" + gen);
                return;
            }

            string player = Path.Combine(root, "Client", "Assets", "Scripts", "PMR3", "PMR3Player.cs");
            string projectile = Path.Combine(root, "Client", "Assets", "Scripts", "PMR3", "PMR5Projectile.cs");
            string outDir = Path.Combine(root, "Client", "Assets", "Scripts", "PMR3", "Generated");
            string idLock = Path.Combine(root, "Docs", "plans", "pmnet-r3-ids.json");

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = "dotnet";
            // R5-B2a：单一生成集合现在含**两个**网络类（PMR3Player + PMR5Projectile），
            // 因此声明路径必须把两者都传给 --decl-check，否则生成期注册表（2 个类）
            // 会与只扫到 1 个类的内存产物对不上 —— 那会把“本测试没跟上新建类”
            // 误报成“生成产物与声明不同步”。
            psi.Arguments = "\"" + gen + "\" --decl-check \"" + player + "\" \"" + projectile
                            + "\" --out-dir \"" + outDir
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

                Check(p.ExitCode == 0, "PMNetGen --decl-check 退出码 0（产物与声明/锁文件逐字节一致），实际 "
                                       + p.ExitCode);
                if (p.ExitCode != 0)
                {
                    Console.WriteLine("      stdout: " + Trim(stdout, 500));
                    Console.WriteLine("      stderr: " + Trim(stderr, 500));
                }
            }

            // A2：锁文件用**专用**锁，不动共享锁（否则会改掉别人的全局协议摘要）。
            CheckTrue(File.Exists(idLock), "专用 ID 锁存在：" + idLock);
            if (File.Exists(idLock))
            {
                string lockText = File.ReadAllText(idLock, Encoding.UTF8);
                CheckTrue(lockText.Contains("CLASS:PMNet.R3.PMR3Player"), "锁文件含 PMR3Player 的稳定键");
                CheckTrue(lockText.Contains("RPC:PMNet.R3.PMR3Player.ServerProbe"), "锁文件含 ServerProbe 的稳定 ID");
                CheckTrue(lockText.Contains("RPC:PMNet.R3.PMR3Player.ClientEcho"), "锁文件含 ClientEcho 的稳定 ID");
            }

            string sharedLock = Path.Combine(root, "Docs", "plans", "pmnet-ids.json");
            CheckTrue(File.Exists(sharedLock), "共享 ID 锁仍存在（未被本任务改写）：" + sharedLock);
            if (File.Exists(sharedLock))
            {
                string sharedText = File.ReadAllText(sharedLock, Encoding.UTF8);
                CheckTrue(!sharedText.Contains("PMNet.R3.PMR3Player"),
                    "共享锁里**没有** PMR3 条目（PMR3 用自己的锁）");
            }

            // A3：运行期注册表内容（零反射、显式列出）。
            PMNetRegistry.Reset();
            PMR3Runtime.Shutdown();
            PMR3Runtime.Register();

            CheckTrue(PMNetRegistry.IsSealed, "Register() 之后注册表已封板");
            CheckEq(PMNetRegistry.ClassCount, global::PMNet.Generated.PMNetGeneratedRegistry.GeneratedClassCount,
                "注册类数 == 生成期常量 GeneratedClassCount");
            // R5-B2a：单一生成集合里新增了投射物声明对象（PMR5Projectile），
            // 所以类数从 1 修正为 2。这里只改“新建类带来的计数”，
            // 下面的 ID/描述符断言（PMR3Player 的 ClassId、成员数、RPC 档位）一律不放宽。
            CheckEq(global::PMNet.Generated.PMNetGeneratedRegistry.GeneratedClassCount, 2,
                "本程序集注册 PMR3Player + PMR5Projectile 两个类（R5-B2a 新增投射物声明）");

            PMNetClassEntry entry;
            CheckTrue(PMNetRegistry.TryGetClass(PMR3Player.PMGeneratedClassId, out entry) && entry != null,
                "PMR3Player 的 ClassId 已注册：0x" + PMR3Player.PMGeneratedClassId.ToString("X8"));
            // R4-B（B1）：PMR3Player 新增第 3 个复制属性 `_movementSnapshotV1`（运动权威快照，byte[]）。
            // R6-A2：再追加 9 条战斗声明属性（7 公共 + 2 OwnerOnly），位宽从 3 变 12。
            // 这里只更新「新增属性带来的计数」；下面的成员名/写入口断言与 ID/RPC 档位断言一律不放宽。
            CheckEq(PMR3Player.PMGeneratedChangeMaskBitCount, 12,
                "复制属性位宽 == 12（Uid + ProbeCount + MovementSnapshotV1 + 9 战斗属性）");
            CheckEq(entry != null && entry.Rep != null ? entry.Rep.Properties.Length : -1, 12,
                "复制描述符里的属性数 == 12");

            PMNet.PMPropertyDescriptor snapshotProp = default(PMNet.PMPropertyDescriptor);
            bool hasSnapshotProp = false;
            if (entry != null && entry.Rep != null && entry.Rep.Properties != null)
            {
                for (int i = 0; i < entry.Rep.Properties.Length; i++)
                {
                    if (entry.Rep.Properties[i].MemberName == "_movementSnapshotV1")
                    {
                        snapshotProp = entry.Rep.Properties[i];
                        hasSnapshotProp = true;
                        break;
                    }
                }
            }

            CheckTrue(hasSnapshotProp,
                "复制描述符里真的注册了新字段 `_movementSnapshotV1`（R4-B 运动快照）");
            CheckTrue(hasSnapshotProp && snapshotProp.PushBased,
                "`_movementSnapshotV1` 为 Push 模型（赋值即标脏）");
            CheckTrue(hasSnapshotProp && snapshotProp.Condition == PMCond.None,
                "`_movementSnapshotV1` 条件 = None（当前状态收敛通道对全部相关连接）");
            CheckTrue(hasSnapshotProp && snapshotProp.OnRepMethodId != 0,
                "`_movementSnapshotV1` 已登记 OnRep 方法 ID（只通知/入队）");
            CheckTrue(hasSnapshotProp
                && snapshotProp.SetterName == "PMNet_Set_movementSnapshotV1",
                "`_movementSnapshotV1` 的写入口是生成的 Setter（不允许直接改私有字段）");

            PMNetRpcEntry probe;
            bool hasProbe = PMNetRegistry.TryGetRpc(PMR3Player.PMGeneratedClassId,
                PMR3Player.PMGeneratedRpcId_ServerProbe, out probe);
            CheckTrue(hasProbe, "ServerProbe 已注册为 RPC 条目");
            CheckTrue(hasProbe && probe.Descriptor.Direction == PMRpcKind.Server, "ServerProbe 方向 = Server");
            CheckTrue(hasProbe && probe.Descriptor.Validator == PMRpcValidator.ForceValidate,
                "ServerProbe 档位 = ForceValidate（项目红线）");
            CheckTrue(hasProbe && probe.Descriptor.IsReliable, "ServerProbe 为可靠 RPC");

            PMNetRpcEntry echo;
            bool hasEcho = PMNetRegistry.TryGetRpc(PMR3Player.PMGeneratedClassId,
                PMR3Player.PMGeneratedRpcId_ClientEcho, out echo);
            CheckTrue(hasEcho, "ClientEcho 已注册为 RPC 条目");
            CheckTrue(hasEcho && echo.Descriptor.Direction == PMRpcKind.Client, "ClientEcho 方向 = Client");
            CheckTrue(hasEcho && echo.Descriptor.IsReliable, "ClientEcho 为可靠 RPC");

            // R4-B（B1）新增的 4 条运动 RPC：方向/可靠性/校验档位按契约 §B1 冻结。
            // 它们只能出现在**声明层**（实现进 Driver），因此这里是消费者能看到的真实注册面。
            PMNetRpcEntry movement;
            bool hasInput = PMNetRegistry.TryGetRpc(PMR3Player.PMGeneratedClassId,
                PMR3Player.PMGeneratedRpcId_ServerMovementInputV1, out movement);
            CheckTrue(hasInput && movement.Descriptor.Direction == PMRpcKind.Server
                && !movement.Descriptor.IsReliable
                && movement.Descriptor.Validator == PMRpcValidator.ForceValidate,
                "ServerMovementInputV1 = Server/不可靠/ForceValidate（高频输入不占可靠队列）");

            PMNetRpcEntry resyncReq;
            bool hasResyncReq = PMNetRegistry.TryGetRpc(PMR3Player.PMGeneratedClassId,
                PMR3Player.PMGeneratedRpcId_ServerMovementResyncV1, out resyncReq);
            CheckTrue(hasResyncReq && resyncReq.Descriptor.Direction == PMRpcKind.Server
                && resyncReq.Descriptor.IsReliable
                && resyncReq.Descriptor.Validator == PMRpcValidator.ForceValidate,
                "ServerMovementResyncV1 = Server/可靠/ForceValidate");

            PMNetRpcEntry evt;
            bool hasEvt = PMNetRegistry.TryGetRpc(PMR3Player.PMGeneratedClassId,
                PMR3Player.PMGeneratedRpcId_ClientMovementEventsV1, out evt);
            CheckTrue(hasEvt && evt.Descriptor.Direction == PMRpcKind.Client && evt.Descriptor.IsReliable,
                "ClientMovementEventsV1 = Client/可靠（不可逆事件独立于快照游标）");

            PMNetRpcEntry resyncPayload;
            bool hasResyncPayload = PMNetRegistry.TryGetRpc(PMR3Player.PMGeneratedClassId,
                PMR3Player.PMGeneratedRpcId_ClientMovementResyncV1, out resyncPayload);
            CheckTrue(hasResyncPayload && resyncPayload.Descriptor.Direction == PMRpcKind.Client
                && resyncPayload.Descriptor.IsReliable,
                "ClientMovementResyncV1 = Client/可靠（显式重同步整体重绑）");

            CheckTrue(PMR3Runtime.ProtocolHash != 0u, "GlobalProtocolHash 非 0");
            CheckEq(global::PMNet.Generated.PMNetGeneratedRegistry.ProtocolHash, PMNetRegistry.ProtocolHash,
                "生成期摘要 == 运行期摘要");
        }

        // =================================================================================
        //  B. 运行时接线
        // =================================================================================

        private static void TestAttachAndSpawn()
        {
            // B1：服务端世界 + 一条已激活连接。
            Rig rig = Rig.BuildServerWithClient("attach", 4001u, 11);

            try
            {
                CheckTrue(PMR3Runtime.IsAttached(rig.Server.World), "Attach 之后服务端世界已接线");
                CheckTrue(ReferenceEquals(PMR3Runtime.GetBridge(rig.Server.World), rig.Server.Bridge),
                    "世界反查得到同一条桥");

                // B2：重复 Attach 幂等（不能因 RegisterClass 重复注册而抛）。
                bool threw = false;
                try
                {
                    PMR3Runtime.Attach(rig.Server.World, rig.Server.Bridge);
                }
                catch (Exception ex)
                {
                    threw = true;
                    Console.WriteLine("      " + ex.GetType().Name + ": " + ex.Message);
                }

                CheckTrue(!threw, "重复 Attach 不抛（RegisterClass 有先查再注册）");

                // B3：客户端世界调用 SpawnPlayer 必须被拒（只有权威侧能创建）。
                PMR3Player clientSpawn = PMR3Runtime.SpawnPlayer(rig.ClientWorld, rig.ClientBridge,
                    rig.ClientConnection);
                CheckTrue(clientSpawn == null, "客户端侧 SpawnPlayer 返回 null");

                // B4：未 Attach 的世界调用 SpawnPlayer 必须被拒。
                PMNetWorld rawWorld = new PMNetWorld(new PMSession(0x51u, true));
                PMNetSessionBridge rawBridge = new PMNetSessionBridge(rawWorld);
                PMR3Player un = PMR3Runtime.SpawnPlayer(rawWorld, rawBridge, rig.ServerConnection);
                CheckTrue(un == null, "未 Attach 的世界 SpawnPlayer 返回 null");

                // B5：唯一 owner —— 角色 Authority、OwnerConnection 唯一、GetNetConnection 直返它。
                PMR3Player player = PMR3Runtime.SpawnPlayer(rig.Server.World, rig.Server.Bridge,
                    rig.ServerConnection);
                CheckTrue(player != null, "权威侧 SpawnPlayer 成功");
                if (player == null) { return; }

                CheckTrue(player.Role == PMNetRole.Authority, "服务端副本角色 = Authority");
                CheckTrue(player.State == PMNetObjectState.Active, "服务端副本状态 = Active");
                CheckTrue(ReferenceEquals(player.OwnerConnection, rig.ServerConnection),
                    "OwnerConnection 指向唯一 owner 连接");
                CheckTrue(ReferenceEquals(player.GetNetConnection(), rig.ServerConnection),
                    "GetNetConnection() 直返 OwnerConnection（不沿 Owner 链，避免双身份）");
                CheckTrue(player.Owner == null, "没有设置 Owner（只有一个 owner 来源）");
                CheckEq(player.Uid, 11, "初值 Uid 来自已认证连接身份");
                CheckEq(player.ProbeCount, 0, "初值 ProbeCount == 0");

                // B6：重复 Spawn 同一连接是宿主的责任，但框架不会阻止 —— 这里只钉住
                //     「同一 uid 第二次 Spawn 也必须是新对象 + 新 NetId」（NetId 不复用）。
                PMR3Player second = PMR3Runtime.SpawnPlayer(rig.Server.World, rig.Server.Bridge,
                    rig.ServerConnection);
                CheckTrue(second != null && second.NetId.Value != player.NetId.Value,
                    "再次 Spawn 得到新对象 + 新 NetId（框架不复用 NetId；去重由宿主负责）");
            }
            finally
            {
                rig.Dispose();
            }
        }

        // =================================================================================
        //  C. 复制与探针往返
        // =================================================================================

        private static void TestReplicationAndProbeRoundTrip()
        {
            PMNetRpcReceive.ResetStats();

            Rig rig = Rig.BuildServerWithClient("roundtrip", 4002u, 21);
            try
            {
                PMR3Player serverPlayer = PMR3Runtime.SpawnPlayer(rig.Server.World, rig.Server.Bridge,
                    rig.ServerConnection);
                CheckTrue(serverPlayer != null, "权威侧创建玩家副本");
                if (serverPlayer == null) { return; }

                rig.Frame(4);

                PMNetObject clientObj;
                bool found = rig.ClientWorld.TryFind(serverPlayer.NetId, out clientObj) && clientObj != null;
                CheckTrue(found, "客户端经生命周期消息收到副本");
                if (!found) { return; }

                PMR3Player clientPlayer = clientObj as PMR3Player;
                CheckTrue(clientPlayer != null, "副本类型为 PMR3Player");
                if (clientPlayer == null) { return; }

                // C1：Create 与初值原子到达（OnReplicatedCreate 里必须已有 Uid）。
                CheckEq(clientPlayer.ReplicatedCreateCount, 1, "客户端副本创建回调恰好 1 次");
                CheckEq(clientPlayer.Uid, 21, "Create 初值里的 Uid 已就位（与创建原子到达）");
                CheckTrue(clientPlayer.Role == PMNetRole.AutonomousProxy,
                    "客户端副本角色 = AutonomousProxy（IsOwner 由 DS 现算）");

                // C2：本地 owner 经生成桩发一次探针（`ServerProbe`，不手写业务包）。
                int nonce = 0x1234;
                clientPlayer.ProbeSentCount++;
                clientPlayer.ServerProbe(nonce);
                CheckEq(global::PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount, 0,
                    "RemoteSender 已接线：生成桩没有落进待发队列");

                rig.Frame(4);

                CheckEq(serverPlayer.ProbeCount, 1, "服务端 ProbeCount 自增 1（ServerProbe 实现已执行）");
                CheckEq(rig.ServerConnection.RpcApplied, 1, "服务端连接应用了 1 条 RPC");
                CheckEq(rig.ServerConnection.RpcRejected, 0, "服务端连接没有拒绝 RPC");

                // C3：客户端收到回声与复制收敛。
                rig.Frame(4);
                CheckEq(clientPlayer.EchoCount, 1, "拥有者客户端收到 1 次 ClientEcho");
                CheckEq(clientPlayer.LastEchoNonce, nonce, "回声的 nonce 与上行一致（参数原样往返）");
                CheckEq(clientPlayer.ProbeCount, 1, "ProbeCount 复制收敛到客户端");

                // C4：再发一次，计数继续增长（不是一次性巧合）。
                clientPlayer.ProbeSentCount++;
                clientPlayer.ServerProbe(nonce + 1);
                rig.Frame(6);

                CheckEq(serverPlayer.ProbeCount, 2, "第二次探针后服务端 ProbeCount == 2");
                CheckEq(clientPlayer.EchoCount, 2, "第二次回声到达");
                CheckEq(clientPlayer.LastEchoNonce, nonce + 1, "第二次回声 nonce 正确");
                CheckEq(clientPlayer.ProbeCount, 2, "ProbeCount 再次收敛");
                CheckEq(rig.ServerConnection.RpcApplied, 2, "服务端连接累计应用 2 条 RPC");
            }
            finally
            {
                rig.Dispose();
            }
        }

        // =================================================================================
        //  D. 归属拒绝
        // =================================================================================

        private static void TestNonOwnerRejection()
        {
            List<PMNetRpcReceiveResult> observed = new List<PMNetRpcReceiveResult>();
            PMNetRpcReceive.Observer = delegate(PMNetRpcReceiveResult r) { observed.Add(r); };
            PMNetRpcReceive.ResetStats();

            // 服务端 + 两个客户端（uid 31 / 32）。
            Rig rig = Rig.BuildServerWithTwoClients("nonowner", 4003u, 31, 32);
            try
            {
                PMR3Player serverPlayer = PMR3Runtime.SpawnPlayer(rig.Server.World, rig.Server.Bridge,
                    rig.ServerConnection);
                CheckTrue(serverPlayer != null, "权威侧为 uid=31 创建副本");
                if (serverPlayer == null) { return; }

                rig.Frame(4);

                PMNetObject otherObj;
                bool found = rig.ClientEndpoints[1].World.TryFind(serverPlayer.NetId, out otherObj) && otherObj != null;
                CheckTrue(found, "第二个客户端也收到了该副本（SimulatedProxy）");
                if (!found) { return; }

                PMR3Player otherPlayer = otherObj as PMR3Player;
                CheckTrue(otherPlayer != null && otherPlayer.Role == PMNetRole.SimulatedProxy,
                    "第二个客户端上的副本角色 = SimulatedProxy");

                // 计数必须看**服务端对 client2 的那条连接视图**（RPC 是它收下的）。
                long rejectsBefore = rig.ServerViews[1].Connection.RpcRejected;
                int probeBefore = serverPlayer.ProbeCount;

                // 非 owner 冒名发探针：callspace 会判 Remote（客户端侧不额外判归属），
                // 但**服务端**必须按「来源连接是不是该对象 owner」拒绝（D-R0-42）。
                otherPlayer.ServerProbe(0x777);
                rig.Frame(6);

                CheckEq(serverPlayer.ProbeCount, probeBefore, "非 owner 的探针**没有**执行实现（ProbeCount 不变）");
                CheckTrue(rig.ServerViews[1].Connection.RpcRejected > rejectsBefore,
                    "服务端在第二条连接上拒绝了 RPC（RpcRejected 增长）");
                CheckEq(rig.ServerViews[1].Connection.RpcApplied, 0, "第二条连接应用过的 RPC 数保持 0");

                bool sawNotOwner = false;
                for (int i = 0; i < observed.Count; i++)
                {
                    if (observed[i].Status == PMRpcReceiveStatus.NotOwner) { sawNotOwner = true; break; }
                }

                CheckTrue(sawNotOwner, "接收入口给出了 NotOwner 状态（失败可归因）");

                // 对照：owner 自己的探针仍然能通过（证明上面的拒绝是归属原因，不是链路坏了）。
                PMNetObject ownObj;
                rig.ClientWorld.TryFind(serverPlayer.NetId, out ownObj);
                PMR3Player ownPlayer = ownObj as PMR3Player;
                CheckTrue(ownPlayer != null, "第一个客户端拿到自己的副本");
                if (ownPlayer != null)
                {
                    ownPlayer.ServerProbe(0x99);
                    rig.Frame(6);
                    CheckEq(serverPlayer.ProbeCount, probeBefore + 1, "owner 的探针仍然被接受（对照）");
                }
            }
            finally
            {
                PMNetRpcReceive.Observer = null;
                rig.Dispose();
            }
        }

        // =================================================================================
        //  E. 清理
        // =================================================================================

        private static void TestCleanup()
        {
            Rig rig = Rig.BuildServerWithClient("cleanup", 4004u, 41);
            try
            {
                CheckTrue(!(global::PMNet.Generated.PMNetGeneratedRegistry.RemoteSender == null),
                    "Attach 后 RemoteSender 非 null");

                PMR3Runtime.Detach(rig.Server.World);
                CheckTrue(!PMR3Runtime.IsAttached(rig.Server.World), "Detach 之后该世界不再登记");
                CheckTrue(!(global::PMNet.Generated.PMNetGeneratedRegistry.RemoteSender == null),
                    "仍有其他世界接线时 RemoteSender 不应归 null（否则别的世界发不出去）");

                PMR3Runtime.Detach(rig.ClientWorld);
                CheckTrue(global::PMNet.Generated.PMNetGeneratedRegistry.RemoteSender == null,
                    "最后一个世界 Detach 后 RemoteSender 归 null（不留下已释放世界的分发器）");

                PMR3Runtime.Shutdown();
                CheckTrue(global::PMNet.Generated.PMNetGeneratedRegistry.RemoteSender == null,
                    "Shutdown 之后 RemoteSender 仍为 null");
                CheckTrue(!PMR3Runtime.IsAttached(rig.Server.World), "Shutdown 之后世界登记已清空");
                CheckTrue(!PMR3Runtime.HasPlayerReplicatedHandlers, "Shutdown 清掉了 static 事件订阅");
                CheckTrue(!PMR3Runtime.HasPlayerSpawnedHandlers, "Shutdown 清掉了 PlayerSpawned 订阅");
            }
            finally
            {
                rig.Dispose();
            }
        }

        // =================================================================================
        //  F. Lobby 控制代理
        // =================================================================================

        private static void TestLobbyAgent()
        {
            // 准备一局的密钥 / 名册 / 引导文件（与 Lobby 侧同一条路径）。
            PMDsMatchKey key = PMDsMatchKey.Create();
            const string matchId = "match-r3b-test";
            const string dsId = "ds-r3b-test";
            const uint epoch = 0x7B01u;
            const uint hash = 0x5C0B6B49u;
            const int boundPort = 7811;

            PMDsBootstrappedMatch boot = MakeBoot(key, matchId, dsId, epoch, hash,
                new PMDsRosterIdentity(101, 1, 0, 1));
            CheckTrue(boot != null, "构造并解析引导文件成功");
            if (boot == null) { return; }

            // F1：非 loopback 控制地址直接拒绝（契约 §3：首版 TCP 仅 loopback）。
            bool loopbackRejected = false;
            try
            {
                new PMDsLobbyAgent(boot, "10.0.0.5", 7800, boundPort, PMR3Runtime.CollisionDigest);
            }
            catch (ArgumentException)
            {
                loopbackRejected = true;
            }

            CheckTrue(loopbackRejected, "非 loopback 的 -control 被构造期拒绝");

            // F2：到没人监听的端口 → Start 返回 false（不假装连上了）。
            PMDsLobbyAgent noListener = new PMDsLobbyAgent(boot, "127.0.0.1", FindFreePort(),
                boundPort, PMR3Runtime.CollisionDigest);
            string startError;
            bool started = noListener.Start(out startError);
            CheckTrue(!started, "无 listener 时 Start 返回 false（" + (startError ?? "<null>") + "）");
            noListener.Dispose();

            // F3：正常往返 —— Ready / Heartbeat / Result / ResultAck → 请求退出 0。
            using (FakeLobby lobby = new FakeLobby(key.CreateControlSigner()))
            {
                int exitCode = int.MinValue;
                List<string> logs = new List<string>();

                PMDsLobbyAgent agent = new PMDsLobbyAgent(boot, "127.0.0.1", lobby.Port, boundPort,
                    PMR3Runtime.CollisionDigest);
                agent.SceneReady = true;
                agent.PlayerCountProvider = delegate { return 3; };
                agent.Log = delegate(string m) { logs.Add(m); };
                agent.Warn = delegate(string m) { logs.Add("WARN: " + m); };
                agent.ExitRequested = delegate(int code) { exitCode = code; };

                try
                {
                    bool ok = agent.Start(out startError);
                    CheckTrue(ok, "控制通道连接成功（" + (startError ?? "ok") + "）");
                    if (!ok) { return; }

                    lobby.Accept();

                    long now = 1000L;
                    PumpUntil(agent, lobby, ref now, delegate { return lobby.CountOf(PMDsControlMessageType.Ready) > 0; }, 40);

                    PMDsControlMessage ready = lobby.First(PMDsControlMessageType.Ready);
                    CheckTrue(ready != null, "Lobby 收到 Ready（帧/MAC 都通过）");
                    CheckTrue(agent.ReadySent, "代理进入 ReadySent 状态");
                    if (ready != null)
                    {
                        CheckEq(ready.AsReady.BoundPort, boundPort, "Ready.BoundPort = 实际绑定端口");
                        CheckTrue(ready.AsReady.SceneReady, "Ready.SceneReady = true（场景已校验）");
                        CheckEq(ready.AsReady.CollisionDigest, PMR3Runtime.CollisionDigest, "Ready.CollisionDigest 一致");
                        CheckStrEq(ready.MatchId, matchId, "Ready.MatchId 正确");
                        CheckStrEq(ready.DsId, dsId, "Ready.DsId 正确");
                        CheckEq(ready.Epoch, epoch, "Ready.Epoch 正确");
                        CheckEq(ready.ProtocolHash, hash, "Ready.ProtocolHash 正确");
                    }

                    // 心跳：把注入时钟推过 15s。
                    int hbBefore = lobby.CountOf(PMDsControlMessageType.Heartbeat);
                    PumpUntil(agent, lobby, ref now, delegate { return lobby.CountOf(PMDsControlMessageType.Heartbeat) > hbBefore; }, 40, 1000L);
                    PMDsControlMessage hb = lobby.Last(PMDsControlMessageType.Heartbeat);
                    CheckTrue(hb != null, "Lobby 收到 Heartbeat");
                    if (hb != null)
                    {
                        CheckEq(hb.AsHeartbeat.PlayerCount, 3, "Heartbeat.PlayerCount 来自宿主提供者");
                        CheckTrue(hb.AsHeartbeat.UptimeMilliseconds > 0L, "Heartbeat.UptimeMilliseconds > 0");
                    }

                    // 结果：提交 → 重发直到匹配的 ResultAck。
                    byte[] summary = new byte[] { 1, 2, 3, 4, 5 };
                    string submitError;
                    CheckTrue(agent.SubmitResult(0, summary, out submitError),
                        "SubmitResult 成功（" + (submitError ?? "ok") + "）");
                    CheckTrue(agent.HasResult, "代理进入 HasResult 状态");

                    // 同 ID 同内容幂等；同 ID 不同内容拒绝。
                    string secondError;
                    CheckTrue(agent.SubmitResult(0, summary, out secondError), "同内容重复提交幂等（返回 true）");
                    string conflictError;
                    CheckTrue(!agent.SubmitResult(1, summary, out conflictError), "同 ID 不同内容被拒");
                    CheckTrue(conflictError != null && conflictError.Contains("内容不同"), "冲突原因可读：" + conflictError);

                    PumpUntil(agent, lobby, ref now, delegate { return lobby.CountOf(PMDsControlMessageType.Result) > 0; }, 40);
                    PMDsControlMessage result = lobby.First(PMDsControlMessageType.Result);
                    CheckTrue(result != null, "Lobby 收到 Result");
                    if (result != null)
                    {
                        CheckEq(result.RequestId, agent.ResultId, "Result.RequestId == ResultId");
                        CheckEq(result.AsResult.ResultId, agent.ResultId, "Result.ResultId 与 RequestId 一致");
                        CheckEq(result.AsResult.WinnerTeamId, 0, "Result.WinnerTeamId 正确");
                        CheckEq(result.AsResult.Summary.Length, summary.Length, "Result.Summary 长度正确");
                    }

                    // 不匹配的 ResultAck 必须被忽略（不能当成确认）。
                    PMDsResultAckBody wrongAck = new PMDsResultAckBody();
                    wrongAck.ResultId = agent.ResultId + 7UL;
                    lobby.Send(matchId, dsId, epoch, hash, PMDsControlMessageType.ResultAck, wrongAck, wrongAck.ResultId);
                    PumpUntil(agent, lobby, ref now, delegate { return false; }, 4);

                    CheckTrue(!agent.ResultAcknowledged, "不匹配的 ResultAck 不被当成确认");
                    CheckEq(exitCode, int.MinValue, "不匹配的 ResultAck 不请求退出");

                    // 匹配的 ResultAck → 请求退出 0。
                    PMDsResultAckBody ack = new PMDsResultAckBody();
                    ack.ResultId = agent.ResultId;
                    lobby.Send(matchId, dsId, epoch, hash, PMDsControlMessageType.ResultAck, ack, ack.ResultId);
                    PumpUntil(agent, lobby, ref now, delegate { return exitCode != int.MinValue; }, 40);

                    CheckTrue(agent.ResultAcknowledged, "匹配的 ResultAck 被接受");
                    CheckEq(exitCode, 0, "收到匹配 ResultAck 后请求退出码 0");
                    CheckEq(agent.ExitCode, 0, "代理记录的退出码为 0");
                    CheckTrue(lobby.CountOf(PMDsControlMessageType.Exited) > 0, "退出前发出了 Exited");
                }
                finally
                {
                    agent.Dispose();
                }
            }

            // F4：坏 MAC → 明确失败 + 退出码 1（不假装成功）。
            using (FakeLobby lobby = new FakeLobby(key.CreateControlSigner()))
            {
                int exitCode = int.MinValue;
                PMDsLobbyAgent agent = new PMDsLobbyAgent(boot, "127.0.0.1", lobby.Port, boundPort,
                    PMR3Runtime.CollisionDigest);
                agent.SceneReady = true;
                agent.ExitRequested = delegate(int code) { exitCode = code; };

                try
                {
                    CheckTrue(agent.Start(out startError), "第二条控制通道连接成功");
                    lobby.Accept();

                    long now = 1000L;
                    PumpUntil(agent, lobby, ref now, delegate { return lobby.CountOf(PMDsControlMessageType.Ready) > 0; }, 40);

                    // 构造一条**合法签名**的帧再篡改 MAC 区：验签必须失败，且不得修改任何状态。
                    PMDsResultAckBody badAckBody = new PMDsResultAckBody();
                    badAckBody.ResultId = 1UL;
                    byte[] tampered = lobby.BuildFrame(matchId, dsId, epoch, hash,
                        PMDsControlMessageType.ResultAck, badAckBody, badAckBody.ResultId);
                    CheckTrue(tampered != null && tampered.Length > 0, "构造出一条待篡改的合法帧");
                    if (tampered != null && tampered.Length > 0)
                    {
                        tampered[tampered.Length - 1] = (byte)(tampered[tampered.Length - 1] ^ 0xFF);
                        lobby.SendRaw(tampered);
                    }

                    PumpUntil(agent, lobby, ref now, delegate { return agent.IsFaulted; }, 40);
                    CheckTrue(agent.IsFaulted, "坏 MAC 使代理进入失败态");
                    CheckTrue(exitCode == 1, "坏 MAC 请求退出码 1（实际 " + exitCode + "）");
                    CheckEq(agent.FramesReceived, 0, "坏 MAC 在验签阶段被拒：没有任何帧进入已受理计数");
                    CheckEq(agent.MacFailures, 1, "坏 MAC 记入 MacFailures（与身份/方向拒绝分开）");
                    CheckEq(agent.FramesRejected, 0, "坏 MAC 不计入「已验签但身份/方向不符」计数（两类失败分开）");
                }
                finally
                {
                    agent.Dispose();
                }
            }

            // F5：身份不符（错 MatchId，但 MAC 合法）→ 明确失败。
            using (FakeLobby lobby = new FakeLobby(key.CreateControlSigner()))
            {
                int exitCode = int.MinValue;
                PMDsLobbyAgent agent = new PMDsLobbyAgent(boot, "127.0.0.1", lobby.Port, boundPort,
                    PMR3Runtime.CollisionDigest);
                agent.SceneReady = true;
                agent.ExitRequested = delegate(int code) { exitCode = code; };

                try
                {
                    CheckTrue(agent.Start(out startError), "第三条控制通道连接成功");
                    lobby.Accept();

                    long now = 1000L;
                    PumpUntil(agent, lobby, ref now, delegate { return lobby.CountOf(PMDsControlMessageType.Ready) > 0; }, 40);

                    PMDsResultAckBody ack = new PMDsResultAckBody();
                    ack.ResultId = 1UL;
                    // 合法 MAC、但 MatchId 指向别的局（用同一把密钥签，因此验签会通过）。
                    lobby.Send("match-other", dsId, epoch, hash, PMDsControlMessageType.ResultAck, ack, ack.ResultId);

                    PumpUntil(agent, lobby, ref now, delegate { return agent.IsFaulted; }, 40);
                    CheckTrue(agent.IsFaulted, "身份不符（错 MatchId）使代理失败");
                    CheckTrue(exitCode == 1, "身份不符请求退出码 1（实际 " + exitCode + "）");
                    CheckTrue(agent.FaultReason != null && agent.FaultReason.Contains("MatchId"),
                        "失败原因指向 MatchId：" + (agent.FaultReason ?? "<null>"));
                }
                finally
                {
                    agent.Dispose();
                }
            }

            // F6：场景未就绪 → 不发 Ready；启动期超时后明确失败。
            using (FakeLobby lobby = new FakeLobby(key.CreateControlSigner()))
            {
                int exitCode = int.MinValue;
                PMDsLobbyAgent agent = new PMDsLobbyAgent(boot, "127.0.0.1", lobby.Port, boundPort,
                    PMR3Runtime.CollisionDigest);
                agent.SceneReady = false;   // 故意不置就绪
                agent.ExitRequested = delegate(int code) { exitCode = code; };

                try
                {
                    CheckTrue(agent.Start(out startError), "第四条控制通道连接成功");
                    lobby.Accept();

                    long now = 1000L;
                    PumpUntil(agent, lobby, ref now, delegate { return agent.IsFaulted; }, 60, 1000L);

                    CheckEq(lobby.CountOf(PMDsControlMessageType.Ready), 0, "场景未就绪时**不发** Ready");
                    CheckTrue(agent.IsFaulted, "超过启动期限后代理失败（不静默挂着）");
                    CheckEq(exitCode, 1, "场景不就绪请求退出码 1");
                }
                finally
                {
                    agent.Dispose();
                }
            }
        }

        private static void PumpUntil(PMDsLobbyAgent agent, FakeLobby lobby, ref long now,
                                      Func<bool> done, int maxIterations)
        {
            PumpUntil(agent, lobby, ref now, done, maxIterations, 100L);
        }

        private static void PumpUntil(PMDsLobbyAgent agent, FakeLobby lobby, ref long now,
                                      Func<bool> done, int maxIterations, long stepMs)
        {
            for (int i = 0; i < maxIterations; i++)
            {
                agent.Pump(now, 1700000000L + i);
                lobby.Pump();
                if (done()) { return; }

                now += stepMs;
            }
        }

        // =================================================================================
        //  H. 控制面双向 liveness（长局不再误退出 / 丢下行仍退出 / 坏 MAC 不续命）
        //
        //  背景：DS 在 Ready 之后需要一个「对端还活着」的**可信**信号。旧实现把「收到过任何下行帧」
        //  当活性判据，而 Lobby 只在结果受理后才下行 ResultAck ⇒ 长局必然被 DS 自判失败
        //  （真实用户：新构建 smoke 122/0 通过，但 30 秒后 exit 1）。
        //  现在：Lobby 在合法 Ready / 合法 Heartbeat 之后回一条签名 Heartbeat；
        //  「首个 Ready 响应」仍用 StartupReadyTimeoutMs，运行期只看 RuntimeLivenessTimeoutMs，
        //  而**持续上行发送成功不算对端存活**。
        // =================================================================================

        private static void TestControlFaceLiveness()
        {
            PMDsMatchKey key = PMDsMatchKey.Create();
            const string matchId = "match-r3b-liveness";
            const string dsId = "ds-r3b-liveness";
            const uint epoch = 0x7B02u;
            const uint hash = 0x5C0B6B49u;
            const int boundPort = 7812;

            PMDsBootstrappedMatch boot = MakeBoot(key, matchId, dsId, epoch, hash,
                new PMDsRosterIdentity(201, 1, 0, 1));
            CheckTrue(boot != null, "H1 构造并解析引导文件成功");
            if (boot == null) { return; }

            // ── H2–H18：虚拟 65 秒长局，在 Lobby 双向应答下保持存活；心跳不回声、发送有界；
            //            合法心跳不影响 ResultAck 与退出 ──
            using (FakeLobby lobby = new FakeLobby(key.CreateControlSigner()))
            {
                lobby.AutoReplyHeartbeats = true;

                int exitCode = int.MinValue;
                List<string> logs = new List<string>();
                PMDsLobbyAgent agent = new PMDsLobbyAgent(boot, "127.0.0.1", lobby.Port, boundPort,
                    PMR3Runtime.CollisionDigest);
                agent.SceneReady = true;
                agent.Log = delegate(string m) { logs.Add(m); };
                agent.Warn = delegate(string m) { logs.Add("WARN: " + m); };
                agent.ExitRequested = delegate(int code) { exitCode = code; };

                try
                {
                    string startError;
                    CheckTrue(agent.Start(out startError), "H2 控制通道连接成功（" + (startError ?? "ok") + "）");
                    lobby.Accept();

                    long now = 1000L;
                    PumpUntil(agent, lobby, ref now,
                        delegate { return lobby.CountOf(PMDsControlMessageType.Ready) > 0; }, 40);
                    long readySentAt = now;

                    // 65 个 1s 步：虚拟时间跨过旧的 30 秒看门狗两次以上。
                    PumpUntil(agent, lobby, ref now, delegate { return false; }, 65, 1000L);

                    long virtualMs = now - readySentAt;
                    CheckTrue(virtualMs > 60000L,
                        "H3 虚拟时间已跨过 60 秒（实际 " + virtualMs + "ms；旧实现会在 30s 处误退出）");
                    CheckTrue(!agent.IsFaulted,
                        "H4 长局不再误退出（IsFaulted=false，原因=" + (agent.FaultReason ?? "<none>") + "）");
                    CheckTrue(exitCode == int.MinValue, "H5 没有请求退出（实际 " + exitCode + "）");
                    CheckTrue(agent.LastTrustedReceiveMs > 0L,
                        "H6 记录了最后可信接收时刻（" + agent.LastTrustedReceiveMs + "）");
                    CheckTrue(agent.HeartbeatsReceived >= 8L,
                        "H7 收到 Lobby 下行心跳（实际 " + agent.HeartbeatsReceived + "，期望 ≥ 8）");
                    CheckTrue(agent.HeartbeatsReceived <= 20L,
                        "H8 下行心跳有界（实际 " + agent.HeartbeatsReceived + " ≤ 20 = Ready 应答 1 + DS 心跳数）");

                    long sentHeartbeats = lobby.CountOf(PMDsControlMessageType.Heartbeat);
                    CheckTrue(sentHeartbeats <= 14L,
                        "H9 DS 上行心跳有界（实际 " + sentHeartbeats + " ≤ 14 = 65s/5s + 2；若回声会 ≥ 24）");
                    CheckTrue(lobby.CountOf(PMDsControlMessageType.Ready) <= 5L,
                        "H10 拿到首个可信回应后 Ready 重发有界（实际 "
                        + lobby.CountOf(PMDsControlMessageType.Ready) + "；旧行为会发 ~30 条）");
                    CheckTrue(lobby.AutoRepliesSent >= 8,
                        "H11 替身 Lobby 确实一直在应答（" + lobby.AutoRepliesSent + "）");

                    // 合法心跳不影响 ResultAck 与退出。
                    byte[] summary = new byte[] { 9, 9, 9 };
                    string submitError;
                    CheckTrue(agent.SubmitResult(0, summary, out submitError),
                        "H12 结果提交成功（" + (submitError ?? "ok") + "）");
                    PumpUntil(agent, lobby, ref now,
                        delegate { return lobby.CountOf(PMDsControlMessageType.Result) > 0; }, 20);
                    CheckTrue(lobby.CountOf(PMDsControlMessageType.Result) > 0, "H13 Result 已发出");
                    CheckTrue(!agent.ResultAcknowledged, "H14 心跳流不会冒充 ResultAck");

                    PMDsResultAckBody ack = new PMDsResultAckBody();
                    ack.ResultId = agent.ResultId;
                    if (agent.HasResult)
                    {
                        // 前置未成立（例如注入故障让代理提前失败）时**不发**这条 Ack：
                        // 下面的断言会如实变红，但不会把「测试构造非法帧」变成未捕获异常。
                        lobby.Send(matchId, dsId, epoch, hash, PMDsControlMessageType.ResultAck, ack, ack.ResultId);
                        PumpUntil(agent, lobby, ref now, delegate { return exitCode != int.MinValue; }, 20);
                    }

                    CheckTrue(agent.ResultAcknowledged, "H15 匹配的 ResultAck 在心跳流中仍被接受");
                    CheckEq(exitCode, 0, "H16 退出码 0（心跳不破坏退出语义）");
                    CheckTrue(lobby.CountOf(PMDsControlMessageType.Exited) > 0, "H17 退出前发出 Exited");
                    CheckTrue(!agent.IsFaulted, "H18 心跳 + 结果路径全程未失败");
                }
                finally
                {
                    agent.Dispose();
                }
            }

            // ── H19–H26：坏 MAC **不续命**：既不建立/刷新可信接收时间，也不进入已受理计数，
            //            而是立即明确失败（本组刻意全程关掉自动应答，保证无任何合法帧干扰）──
            using (FakeLobby lobby = new FakeLobby(key.CreateControlSigner()))
            {
                int exitCode = int.MinValue;
                PMDsLobbyAgent agent = new PMDsLobbyAgent(boot, "127.0.0.1", lobby.Port, boundPort,
                    PMR3Runtime.CollisionDigest);
                agent.SceneReady = true;
                agent.ExitRequested = delegate(int code) { exitCode = code; };

                try
                {
                    string startError;
                    CheckTrue(agent.Start(out startError), "H19 控制通道连接成功");
                    lobby.Accept();

                    long now = 1000L;
                    PumpUntil(agent, lobby, ref now,
                        delegate { return lobby.CountOf(PMDsControlMessageType.Ready) > 0; }, 40);
                    PumpUntil(agent, lobby, ref now, delegate { return false; }, 3, 1000L);

                    CheckEq(agent.LastTrustedReceiveMs, 0L, "H20 前置：尚无任何可信下行（Lobby 完全不回应）");
                    long macBefore = agent.MacFailures;

                    PMDsHeartbeatBody tamperBody = new PMDsHeartbeatBody();
                    tamperBody.UptimeMilliseconds = 1L;
                    tamperBody.PlayerCount = 1;
                    byte[] tampered = lobby.BuildFrame(matchId, dsId, epoch, hash,
                        PMDsControlMessageType.Heartbeat, tamperBody, 0UL);
                    CheckTrue(tampered != null && tampered.Length > 0, "H21 构造出一条待篡改的合法心跳帧");
                    if (tampered != null && tampered.Length > 0)
                    {
                        tampered[tampered.Length - 1] = (byte)(tampered[tampered.Length - 1] ^ 0xFF);
                        lobby.SendRaw(tampered);
                    }

                    PumpUntil(agent, lobby, ref now, delegate { return agent.IsFaulted; }, 20);

                    CheckTrue(agent.IsFaulted, "H22 坏 MAC 使代理立即明确失败");
                    CheckEq(exitCode, 1, "H23 坏 MAC 退出码 1");
                    CheckEq(agent.LastTrustedReceiveMs, 0L, "H24 坏 MAC 没有建立/刷新可信接收时间（不续命）");
                    CheckEq(agent.FramesReceived, 0L, "H25 坏 MAC 未进入已受理计数");
                    CheckEq(agent.MacFailures, macBefore + 1L, "H26 坏 MAC 计入 MacFailures（与身份/方向拒绝分开）");
                    CheckTrue(agent.FaultReason != null && agent.FaultReason.IndexOf("验签", StringComparison.Ordinal) >= 0,
                        "H27 失败原因指向验签：" + (agent.FaultReason ?? "<null>"));
                }
                finally
                {
                    agent.Dispose();
                }
            }

            // ── H28–H33：丢下行可信流量仍退出；而「上行一直发得出去」不能续命 ──
            using (FakeLobby lobby = new FakeLobby(key.CreateControlSigner()))
            {
                lobby.AutoReplyHeartbeats = true;

                int exitCode = int.MinValue;
                PMDsLobbyAgent agent = new PMDsLobbyAgent(boot, "127.0.0.1", lobby.Port, boundPort,
                    PMR3Runtime.CollisionDigest);
                agent.SceneReady = true;
                agent.ExitRequested = delegate(int code) { exitCode = code; };

                try
                {
                    string startError;
                    CheckTrue(agent.Start(out startError), "H28 控制通道连接成功");
                    lobby.Accept();

                    long now = 1000L;
                    PumpUntil(agent, lobby, ref now,
                        delegate { return agent.LastTrustedReceiveMs > 0L; }, 40);
                    CheckTrue(agent.LastTrustedReceiveMs > 0L, "H29 已建立可信下行（前置）");

                    long trustedAt = agent.LastTrustedReceiveMs;
                    long sentBefore = agent.FramesSent;
                    lobby.AutoReplyHeartbeats = false;   // 此后不再下行任何可信帧

                    PumpUntil(agent, lobby, ref now, delegate { return agent.IsFaulted; }, 90, 500L);

                    long elapsed = now - trustedAt;
                    CheckTrue(agent.IsFaulted, "H30 丢下行可信流量后仍明确失败退出");
                    CheckEq(exitCode, 1, "H31 退出码 1");
                    CheckTrue(agent.FaultReason != null
                        && agent.FaultReason.IndexOf("运行期", StringComparison.Ordinal) >= 0,
                        "H32 失败原因是运行期 liveness：“" + (agent.FaultReason ?? "<null>") + "”");
                    CheckTrue(elapsed >= PMDsLobbyAgent.RuntimeLivenessTimeoutMs,
                        "H33 至少经过运行期超时（实际 " + elapsed + "ms）");
                    CheckTrue(elapsed <= PMDsLobbyAgent.RuntimeLivenessTimeoutMs + 2500L,
                        "H34 超时检测不过度滞后（实际 " + elapsed + "ms，上界 "
                        + (PMDsLobbyAgent.RuntimeLivenessTimeoutMs + 2500L) + "ms）");
                    CheckTrue(agent.FramesSent > sentBefore,
                        "H35 期间上行一直在成功发送（发送成功并不代表对端存活）");
                    CheckTrue(agent.IsConnected, "H36 失败时 TCP 仍连着（失败源于对端无响应，不是断链）");
                }
                finally
                {
                    agent.Dispose();
                }
            }
        }

        // =================================================================================
        //  G. 常量
        // =================================================================================

        private static void TestConstants()
        {
            CheckEq(PMR3Runtime.CollisionDigest, 0x52334201u,
                "CollisionDigest 与契约 §7.4 冻结值一致（0x52334201）");
            CheckTrue(PMDsLobbyAgent.DefaultControlPort == PMDsControlWire.DefaultControlPort,
                "控制通道默认端口与 PMDsControlWire 同源（" + PMDsLobbyAgent.DefaultControlPort + "）");
            CheckEq(PMDsLobbyAgent.StartupReadyTimeoutMs, 30000, "就绪期限 30s（只用于「首个 Ready 响应」等待）");
            CheckEq(PMDsLobbyAgent.RuntimeLivenessTimeoutMs, 15000, "运行期 liveness 超时 15s（= 3 × 心跳间隔）");
            CheckEq(PMDsLobbyAgent.HeartbeatIntervalMs, 5000,
                "心跳发送间隔 5s（Lobby 侧心跳超时仍为 15s ⇒ 3:1 余量，消除 1:1 竞态）");
            CheckEq(PMDsLobbyAgent.ResultRetransmitMs, 1000, "结果确认重试 1s（与契约 §3 一致）");
        }

        // =================================================================================
        //  引导文件构造（与 Lobby 侧同一条路径：Encode → TryDecode）
        // =================================================================================

        private static PMDsBootstrappedMatch MakeBoot(PMDsMatchKey key, string matchId, string dsId,
                                                     uint epoch, uint hash, params PMDsRosterIdentity[] roster)
        {
            PMDsTicketIssuer issuer = key.CreateTicketIssuer(matchId, dsId, epoch, hash);

            PMDsBootstrapPlayer[] players = new PMDsBootstrapPlayer[roster.Length];
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            for (int i = 0; i < roster.Length; i++)
            {
                PMDsTicket ticket = issuer.Issue(roster[i], now);
                players[i] = new PMDsBootstrapPlayer(roster[i], ticket.ExportTicketBytes());
            }

            PMDsBootstrapBody body = new PMDsBootstrapBody();
            body.CollisionDigest = PMR3Runtime.CollisionDigest;
            body.Players = players;

            PMDsControlMessage message = PMDsControlMessage.Create(
                PMDsControlMessageType.Bootstrap, matchId, dsId, epoch, hash, 0UL, body);

            byte[] document = PMDsBootstrapDocument.Encode(key, message);

            PMDsBootstrappedMatch boot;
            string error;
            if (!PMDsBootstrapDocument.TryDecode(document, out boot, out error))
            {
                Console.WriteLine("      TryDecode 失败：" + error);
                return null;
            }

            return boot;
        }

        // =================================================================================
        //  替身 Lobby：真实 loopback TCP + 真实帧/MAC
        // =================================================================================

        private sealed class FakeLobby : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly PMDsControlSigner _signer;
            private readonly PMDsControlFrameDecoder _decoder = new PMDsControlFrameDecoder();
            private readonly byte[] _buffer = new byte[8192];
            private readonly Dictionary<PMDsControlMessageType, List<PMDsControlMessage>> _received =
                new Dictionary<PMDsControlMessageType, List<PMDsControlMessage>>();
            private Socket _client;

            public FakeLobby(PMDsControlSigner signer)
            {
                _signer = signer;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            }

            public int Port { get; private set; }

            /// <summary>
            /// 自动应答开关：收到合法 Ready / Heartbeat 时回一条**同一身份、同一密钥签名**的 Heartbeat。
            ///
            /// 它就是真实 Lobby（<c>PMDsCoordinator</c>）双向 liveness 的替身，用来复现「长局不再误退出」。
            /// 默认关闭，因此既有用例（F3–F6）的「Lobby 从不回应」口径一字不变。
            /// </summary>
            public bool AutoReplyHeartbeats;

            /// <summary>自动应答实际发出的条数（有界性可观测）。</summary>
            public int AutoRepliesSent;

            public void Accept()
            {
                Socket server = _listener.Server;
                for (int i = 0; i < 100; i++)
                {
                    if (server.Poll(50000, SelectMode.SelectRead)) { break; }
                }

                _client = _listener.AcceptSocket();
                _client.NoDelay = true;
            }

            public void Pump()
            {
                if (_client == null) { return; }

                while (true)
                {
                    int available = _client.Available;
                    if (available <= 0) { return; }

                    int read = _client.Receive(_buffer, 0, Math.Min(_buffer.Length, available), SocketFlags.None);
                    if (read <= 0) { return; }

                    _decoder.Append(_buffer, 0, read);

                    byte[] payload;
                    while (_decoder.TryDequeue(out payload))
                    {
                        PMDsControlMessage message;
                        PMDsControlVerifyFault fault;
                        if (!_signer.VerifyRaw(payload, 0, payload.Length, out message, out fault))
                        {
                            continue;
                        }

                        List<PMDsControlMessage> list;
                        if (!_received.TryGetValue(message.Type, out list))
                        {
                            list = new List<PMDsControlMessage>();
                            _received[message.Type] = list;
                        }

                        list.Add(message);

                        // ── 双向 liveness 替身：合法 Ready / 合法 Heartbeat ⇒ 回一条签名 Heartbeat ──
                        // 注意：只对**已收到**的两类帧回答，而且回答的是 Heartbeat，并不是回声原始帧。
                        if (AutoReplyHeartbeats
                            && (message.Type == PMDsControlMessageType.Ready
                                || message.Type == PMDsControlMessageType.Heartbeat))
                        {
                            PMDsHeartbeatBody replyBody = new PMDsHeartbeatBody();
                            replyBody.UptimeMilliseconds = 1L;
                            replyBody.PlayerCount = 1;
                            PMDsControlMessage replyMessage = PMDsControlMessage.Create(
                                PMDsControlMessageType.Heartbeat,
                                message.MatchId, message.DsId, message.Epoch, message.ProtocolHash,
                                0UL, replyBody);
                            SendRaw(PMDsControlFraming.Frame(_signer.Sign(replyMessage)));
                            AutoRepliesSent++;
                        }
                    }
                }
            }

            public void Send(string matchId, string dsId, uint epoch, uint hash,
                             PMDsControlMessageType type, PMDsControlBody body, ulong requestId)
            {
                PMDsControlMessage message = PMDsControlMessage.Create(type, matchId, dsId, epoch, hash, requestId, body);
                byte[] payload = _signer.Sign(message);
                byte[] frame = PMDsControlFraming.Frame(payload);
                SendRaw(frame);
            }

            public void SendRaw(byte[] frame)
            {
                if (_client == null) { return; }

                // 半包：分两段发，验证 DS 侧的解码器确实支持半包。
                int half = frame.Length > 1 ? frame.Length / 2 : frame.Length;
                _client.Send(frame, 0, half, SocketFlags.None);
                if (frame.Length - half > 0)
                {
                    _client.Send(frame, half, frame.Length - half, SocketFlags.None);
                }
            }

            /// <summary>构造一条已签名分帧但不发送（负向用例要先篡改再发）。</summary>
            public byte[] BuildFrame(string matchId, string dsId, uint epoch, uint hash,
                                     PMDsControlMessageType type, PMDsControlBody body, ulong requestId)
            {
                PMDsControlMessage message = PMDsControlMessage.Create(type, matchId, dsId, epoch, hash, requestId, body);
                byte[] payload = _signer.Sign(message);
                return PMDsControlFraming.Frame(payload);
            }

            public int CountOf(PMDsControlMessageType type)
            {
                List<PMDsControlMessage> list;
                return _received.TryGetValue(type, out list) ? list.Count : 0;
            }

            public PMDsControlMessage First(PMDsControlMessageType type)
            {
                List<PMDsControlMessage> list;
                if (!_received.TryGetValue(type, out list) || list.Count == 0) { return null; }
                return list[0];
            }

            public PMDsControlMessage Last(PMDsControlMessageType type)
            {
                List<PMDsControlMessage> list;
                if (!_received.TryGetValue(type, out list) || list.Count == 0) { return null; }
                return list[list.Count - 1];
            }

            public void Dispose()
            {
                if (_client != null)
                {
                    try { _client.Close(); } catch (Exception) { }
                    _client = null;
                }

                try { _listener.Stop(); } catch (Exception) { }
            }
        }

        private static int FindFreePort()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        // =================================================================================
        //  手工链路 / 端点 / 场景（与 PMNetSessionTest 同一手法：真实 PMTransport 字节链）
        // =================================================================================

        private sealed class TestLink : IPMTransportLink
        {
            public readonly string Name;
            public readonly LinkHub Hub;
            public readonly List<TestLink> Peers = new List<TestLink>(2);
            public PMTransportConnection Connection;

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

        private sealed class LinkHub
        {
            private readonly List<TestLink> _links = new List<TestLink>(4);
            private readonly Dictionary<TestLink, List<byte[]>> _inbox = new Dictionary<TestLink, List<byte[]>>();

            public TestLink AddLink(string name)
            {
                TestLink link = new TestLink(name, this);
                _links.Add(link);
                _inbox[link] = new List<byte[]>();
                return link;
            }

            /// <summary>把两条链路登记为**互为对端**（投递是逐链路的，两个方向都要）。</summary>
            public void Connect(TestLink a, TestLink b)
            {
                a.Peers.Add(b);
                b.Peers.Add(a);
            }

            public void Route(TestLink from, byte[] buffer, int offset, int count)
            {
                for (int i = 0; i < from.Peers.Count; i++)
                {
                    TestLink peer = from.Peers[i];
                    byte[] copy = new byte[count];
                    Buffer.BlockCopy(buffer, offset, copy, 0, count);
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

        /// <summary>一个世界 + 它的桥（每一端一份：服务端一份，每个客户端一份）。</summary>
        private sealed class EndpointRow
        {
            public string Name;
            public PMNetWorld World;
            public PMNetSessionBridge Bridge;
        }

        /// <summary>一条真实 PMTransportConnection（服务端侧每个客户端一条，客户端侧各一条）。</summary>
        private sealed class ConnectionRow
        {
            public string Name;
            public EndpointRow Endpoint;
            public PMTransportConnection Connection;
            public TestLink Link;
        }

        /// <summary>
        /// 手工链路 + 真实 PMTransport 的双端场景。
        ///
        /// **连接模型（这里最容易搞错）**：一个连接对象是「某一端对另一端的视图」，
        /// 因此一条逻辑链路有**两个**连接对象，分属两端的世界：
        ///   - 服务端世界：每个客户端一条（PeerRole=Client，uid=该玩家）；
        ///   - 客户端世界：只有一条到服务端的连接（PeerRole=Server）。
        /// 两端各持一个 TestLink，互为对端：A.Send 的字节落到 B.OnDatagram。
        /// 若把它们合成一个对象，服务端就只有一个连接 —— 归属判定会退化成「谁看都是 owner」，
        /// 「非 owner 被拒」根本无从测起。
        /// </summary>
        private sealed class Rig : IDisposable
        {
            public LinkHub Hub = new LinkHub();
            public EndpointRow Server;
            public readonly List<EndpointRow> Endpoints = new List<EndpointRow>(3);
            public readonly List<EndpointRow> ClientEndpoints = new List<EndpointRow>(2);
            public readonly List<ConnectionRow> ServerViews = new List<ConnectionRow>(2);
            public readonly List<ConnectionRow> ClientConnections = new List<ConnectionRow>(2);
            public long Now = 1000L;

            /// <summary>服务端对第一个客户端的连接视图（SpawnPlayer 的 owner）。</summary>
            public PMTransportConnection ServerConnection { get { return ServerViews[0].Connection; } }

            /// <summary>第一个客户端自己的连接。</summary>
            public PMTransportConnection ClientConnection { get { return ClientConnections[0].Connection; } }

            public PMNetWorld ClientWorld { get { return ClientEndpoints[0].World; } }

            public PMNetSessionBridge ClientBridge { get { return ClientEndpoints[0].Bridge; } }

            public static Rig BuildServerWithClient(string tag, uint epoch, int uid)
            {
                Rig rig = new Rig();
                rig.Server = rig.NewEndpoint("server", true, epoch);
                rig.AddClient(epoch, uid, uid, uid);
                rig.Finish();
                return rig;
            }

            public static Rig BuildServerWithTwoClients(string tag, uint epoch, int uid1, int uid2)
            {
                Rig rig = new Rig();
                rig.Server = rig.NewEndpoint("server", true, epoch);
                rig.AddClient(epoch, uid1, uid1, uid1);
                rig.AddClient(epoch, uid2, uid2, uid2);
                rig.Finish();
                return rig;
            }

            private EndpointRow NewEndpoint(string name, bool isServer, uint epoch)
            {
                EndpointRow row = new EndpointRow();
                row.Name = name;
                row.World = new PMNetWorld(new PMSession(epoch, isServer));
                row.World.Warn = delegate(string m) { Console.WriteLine("      [" + name + "] world: " + m); };
                row.Bridge = new PMNetSessionBridge(row.World);
                row.Bridge.Warn = delegate(string m) { Console.WriteLine("      [" + name + "] bridge: " + m); };
                Endpoints.Add(row);
                return row;
            }

            private void AddClient(uint epoch, int uid, int playerId, int teamId)
            {
                int connectionId = ClientEndpoints.Count + 1;

                EndpointRow client = NewEndpoint("client" + connectionId, false, epoch);
                ClientEndpoints.Add(client);

                TestLink serverLink = Hub.AddLink("server->c" + connectionId);
                TestLink clientLink = Hub.AddLink("c" + connectionId + "->server");
                Hub.Connect(serverLink, clientLink);

                ServerViews.Add(NewConnection(Server, "serverView" + connectionId, connectionId,
                    PMSessionPeerRole.Client, uid, playerId, teamId, epoch, serverLink));
                ClientConnections.Add(NewConnection(client, "client" + connectionId, 1,
                    PMSessionPeerRole.Server, uid, playerId, teamId, epoch, clientLink));
            }

            private ConnectionRow NewConnection(EndpointRow owner, string name, int connectionId,
                                                PMSessionPeerRole peerRole, int uid, int playerId, int teamId,
                                                uint epoch, TestLink link)
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
                identity.Epoch = epoch;
                identity.LocalProtocolHash = PMR3Runtime.ProtocolHash;
                identity.PeerProtocolHash = PMR3Runtime.ProtocolHash;
                identity.MatchId = "match-r3-runtime-test";
                identity.DsId = "ds-r3-runtime-test";

                PMTransportConfig config = new PMTransportConfig();
                config.IdleTimeoutMs = 0L;

                row.Connection = new PMTransportConnection(identity, owner.World, owner.Bridge, link, config);
                link.Connection = row.Connection;
                return row;
            }

            private void Finish()
            {
                // 先激活每一条连接（TryActivate 同时完成世界/桥的成对登记）。
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

                for (int i = 0; i < Endpoints.Count; i++)
                {
                    PMR3Runtime.Attach(Endpoints[i].World, Endpoints[i].Bridge);
                }
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

            public void Dispose()
            {
                for (int i = 0; i < Endpoints.Count; i++)
                {
                    PMR3Runtime.Detach(Endpoints[i].World);
                }

                for (int i = 0; i < Endpoints.Count; i++)
                {
                    try { Endpoints[i].Bridge.Dispose(); }
                    catch (Exception) { }
                }
            }
        }

        // =================================================================================
        //  小工具
        // =================================================================================

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
    }
}
