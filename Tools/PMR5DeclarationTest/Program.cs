// R5-B2a 门禁：PMR5 投射物**声明层** + 单一生成集合。
//
// 事实来源：
//   - `Docs/plans/net-r5-network-contract.md`「声明层（单一生成集合）」；
//   - `Docs/plans/net-r2-codegen-contract.md` §2（稳定 ID）/§4（生成物 API 面）；
//   - `Docs/plans/net-architecture-migration.md` 文末「R5-B2代码续作」与 T5B2b。
//
// 对象模型（这一条最容易搞错，先写清楚）：
//   - **PMR3Player** 承载三条投射物 RPC 与 `ProjectileDriver` 接缝（契约原文：
//     「PMR3Player 新增 IPMProjectileNetworkDriver 及 ProjectileDriver 引用」）；
//   - **PMR5Projectile** 是投射物副本的**复制快照对象**（唯一属性 `_projectileSnapshotV1`
//     + 创建/更新/销毁事件），它本身没有 RPC。
//
// 覆盖：
//   A. 声明与单一生成集合（--decl-check 逐字节比对 + 旧 ID 不漂移 + 新条目真的新增）
//   B. 真实派发 / 初始化 / 通知（owner 路径，真实 world/bridge/生成 receive）
//   C. 销毁（Destroyed 事件）
//   D. 非 owner 拒（接收入口按归属拒绝，实现不执行）
//   E. driver 缺失的失败关闭（上行 ForceValidate Reject / 下行可观察丢弃）
//   F. 数组上限（发送侧抛异常 + 接收侧判畸形 + 边界内放行）
//   G. 多 world 事件筛选 + 独立 Dispose（不因一个 world 退出清别的 world）
//   H. 清理（Shutdown 走 ClearAll）
//
// **不引 Unity，也不引 PMProjectile 纯核心 / Coordinator**：
// 「只编声明层 + PMNet 核心」就能编译并跑通，本身就是契约要求被验收的属性。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using PMNet;
using PMNet.R3;
using PMNet.Session;
using PMNet.Transport;

namespace PMR5DeclarationTest
{
    internal static class Program
    {
        private static int _passed;
        private static readonly List<string> _failures = new List<string>();

        // ── R5-B2a 合并前已存在的冻结 ID（**不得漂移**；一旦漂移等于改了线协议）───────
        private const uint FrozenPlayerClassId = 405815557u;
        private const ushort FrozenPropUid = 18801;
        private const ushort FrozenPropProbeCount = 12656;
        private const ushort FrozenPropMovementSnapshot = 61580;
        private const ushort FrozenRpcServerProbe = 34232;
        private const ushort FrozenRpcClientEcho = 63853;
        private const ushort FrozenRpcServerMovementInput = 25428;
        private const ushort FrozenRpcServerMovementResync = 15021;
        private const ushort FrozenRpcClientMovementEvents = 5180;
        private const ushort FrozenRpcClientMovementResync = 28381;

        // ── 本批次由**真实生成器**新分配的 ID（从此冻结；后续不得被新键挤走）────────
        private const uint NewProjectileClassId = 227098277u;
        private const ushort NewPropProjectileSnapshot = 6683;
        private const ushort NewRpcServerProjectileSpawn = 21590;
        private const ushort NewRpcServerProjectileHit = 33011;
        private const ushort NewRpcClientProjectileDecision = 38620;

        private static int Main()
        {
            Console.WriteLine("=== R5-B2a：投射物声明层 / 单一生成集合 ===");
            Console.WriteLine();

            PMR3Runtime.Warn = delegate(string m) { Console.WriteLine("      [pmr3runtime] " + m); };
            PMR3Player.ProjectileWarn = delegate(string m) { Console.WriteLine("      [projectile] " + m); };
            PMR5ProjectileEvents.Warn = delegate(string m) { Console.WriteLine("      [projectile-events] " + m); };

            Section("A. 声明与单一生成集合（--decl-check + 旧 ID 不漂移 + 新条目）", TestDeclarationArtifacts);
            Section("B. 真实派发 / 初始化 / 通知", TestDispatchInitializationAndNotify);
            Section("C. 销毁", TestDestroy);
            Section("D. 非 owner 拒", TestNonOwnerRejection);
            Section("E. driver 缺失的失败关闭", TestDriverMissingFailClosed);
            Section("F. 数组上限", TestArrayLimit);
            Section("G. 多 world 事件筛选与独立 Dispose", TestMultiWorldEvents);
            Section("H. 清理：Shutdown 走 ClearAll", TestCleanup);

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

        private static void CheckTrue(bool value, string label)
        {
            Check(value, label);
        }

        // =================================================================================
        //  A. 声明与单一生成集合
        // =================================================================================

        private static void TestDeclarationArtifacts()
        {
            string root = ResolveRepoRoot();
            Check(root != null, "定位到仓库根目录" + (root != null ? "：" + root : ""));
            if (root == null) { return; }

            // A1：生成产物与声明/锁文件逐字节一致（等价于 build.bat 的 decl-check 门禁）。
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
            psi.Arguments = "\"" + gen + "\" --decl-check \"" + player + "\" \"" + projectile
                            + "\" --out-dir \"" + outDir + "\" --id-lock \"" + idLock + "\"";
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.WorkingDirectory = root;

            using (Process p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();

                Check(p.ExitCode == 0,
                    "PMNetGen --decl-check 退出码 0（两个类的产物与声明/锁文件逐字节一致），实际 " + p.ExitCode);
                if (p.ExitCode != 0)
                {
                    Console.WriteLine("      stdout: " + Trim(stdout, 600));
                    Console.WriteLine("      stderr: " + Trim(stderr, 600));
                }
            }

            // A2：锁文件是**专用**锁（不动共享锁），并且旧键与新增键都在。
            CheckTrue(File.Exists(idLock), "专用 ID 锁存在：" + idLock);
            if (File.Exists(idLock))
            {
                string lockText = File.ReadAllText(idLock, Encoding.UTF8);
                CheckTrue(lockText.Contains("CLASS:PMNet.R3.PMR3Player"), "锁文件含 PMR3Player 的类稳定键");
                CheckTrue(lockText.Contains("CLASS:PMNet.R3.PMR5Projectile"), "锁文件含 PMR5Projectile 的类稳定键");
                CheckTrue(lockText.Contains("PROP:PMNet.R3.PMR5Projectile._projectileSnapshotV1"),
                    "锁文件含投射物快照属性的稳定键");
                CheckTrue(lockText.Contains("RPC:PMNet.R3.PMR3Player.ServerProjectileSpawnV1")
                          && lockText.Contains("RPC:PMNet.R3.PMR3Player.ServerProjectileHitV1")
                          && lockText.Contains("RPC:PMNet.R3.PMR3Player.ClientProjectileDecisionV1"),
                    "锁文件含三条新增投射物 RPC 的稳定键");

                // 旧 ID 逐条按字面量核对：这是「没有漂移」的最强断言（不是只看键还在）。
                CheckTrue(lockText.Contains("\"CLASS:PMNet.R3.PMR3Player\": " + FrozenPlayerClassId),
                    "锁文件里 PMR3Player 的 ClassId 仍为 " + FrozenPlayerClassId);
                CheckTrue(lockText.Contains("\"PROP:PMNet.R3.PMR3Player._uid\": " + FrozenPropUid)
                          && lockText.Contains("\"PROP:PMNet.R3.PMR3Player._probeCount\": " + FrozenPropProbeCount)
                          && lockText.Contains("\"PROP:PMNet.R3.PMR3Player._movementSnapshotV1\": " + FrozenPropMovementSnapshot),
                    "锁文件里 PMR3Player 的三个属性 ID 逐条未漂移");
                CheckTrue(lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ServerProbe\": " + FrozenRpcServerProbe)
                          && lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ClientEcho\": " + FrozenRpcClientEcho)
                          && lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ServerMovementInputV1\": " + FrozenRpcServerMovementInput)
                          && lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ServerMovementResyncV1\": " + FrozenRpcServerMovementResync)
                          && lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ClientMovementEventsV1\": " + FrozenRpcClientMovementEvents)
                          && lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ClientMovementResyncV1\": " + FrozenRpcClientMovementResync),
                    "锁文件里 PMR3Player 的六条旧 RPC ID 逐条未漂移");
            }

            string sharedLock = Path.Combine(root, "Docs", "plans", "pmnet-ids.json");
            CheckTrue(File.Exists(sharedLock), "共享 ID 锁仍存在（未被本任务改写）：" + sharedLock);
            if (File.Exists(sharedLock))
            {
                string sharedText = File.ReadAllText(sharedLock, Encoding.UTF8);
                CheckTrue(!sharedText.Contains("PMNet.R3."), "共享锁里没有 PMR3/R5 条目（这两个类用专用锁）");
            }

            // A3：运行期注册表（零反射、显式列出）。
            PMNetRegistry.Reset();
            PMR3Runtime.Shutdown();
            PMR3Runtime.Register();

            CheckTrue(PMNetRegistry.IsSealed, "Register() 之后注册表已封板");
            CheckEq(PMNetRegistry.ClassCount, global::PMNet.Generated.PMNetGeneratedRegistry.GeneratedClassCount,
                "注册类数 == 生成期常量 GeneratedClassCount");
            CheckEq(global::PMNet.Generated.PMNetGeneratedRegistry.GeneratedClassCount, 2,
                "单一生成集合里恰好两个网络类（PMR3Player + PMR5Projectile）");

            PMNetClassEntry playerEntry;
            CheckTrue(PMNetRegistry.TryGetClass(PMR3Player.PMGeneratedClassId, out playerEntry) && playerEntry != null,
                "PMR3Player 的 ClassId 仍注册：" + PMR3Player.PMGeneratedClassId);
            CheckEq(PMR3Player.PMGeneratedClassId, FrozenPlayerClassId,
                "PMR3Player 的 ClassId 与冻结值一致（未被新类挤走）");

            PMNetClassEntry projectileEntry;
            CheckTrue(PMNetRegistry.TryGetClass(PMR5Projectile.PMGeneratedClassId, out projectileEntry)
                      && projectileEntry != null,
                "PMR5Projectile 的 ClassId 已注册：" + PMR5Projectile.PMGeneratedClassId);
            CheckEq(PMR5Projectile.PMGeneratedClassId, NewProjectileClassId,
                "PMR5Projectile 的 ClassId 与本批次生成器分配的冻结值一致");
            CheckTrue(PMR5Projectile.PMGeneratedClassId != PMR3Player.PMGeneratedClassId,
                "两个类的 ClassId 不同（新增而不是复用）");

            // 新属性真的进了描述符（只改数字会退化成「数字对不对」，这里断言成员名与写入口）。
            CheckEq(PMR5Projectile.PMGeneratedChangeMaskBitCount, 1, "投射物类复制属性位宽 == 1");
            CheckEq(projectileEntry != null && projectileEntry.Rep != null
                    ? projectileEntry.Rep.Properties.Length : -1, 1, "投射物复制描述符里的属性数 == 1");

            PMPropertyDescriptor snapshotProp = default(PMPropertyDescriptor);
            bool hasSnapshotProp = false;
            if (projectileEntry != null && projectileEntry.Rep != null && projectileEntry.Rep.Properties != null)
            {
                for (int i = 0; i < projectileEntry.Rep.Properties.Length; i++)
                {
                    if (projectileEntry.Rep.Properties[i].MemberName == "_projectileSnapshotV1")
                    {
                        snapshotProp = projectileEntry.Rep.Properties[i];
                        hasSnapshotProp = true;
                        break;
                    }
                }
            }

            CheckTrue(hasSnapshotProp, "投射物描述符里注册了 `_projectileSnapshotV1`");
            CheckEq(hasSnapshotProp ? snapshotProp.PropertyId : -1, NewPropProjectileSnapshot,
                "`_projectileSnapshotV1` 的 PropertyId 等于生成器分配的冻结值");
            CheckTrue(hasSnapshotProp && snapshotProp.PushBased, "`_projectileSnapshotV1` 为 Push 模型（赋值即标脏）");
            CheckTrue(hasSnapshotProp && snapshotProp.Condition == PMCond.None,
                "`_projectileSnapshotV1` 条件 = None（当前状态收敛通道）");
            CheckTrue(hasSnapshotProp && snapshotProp.OnRepMethodId != 0,
                "`_projectileSnapshotV1` 已登记 OnRep 方法 ID（只通知/入队）");
            CheckTrue(hasSnapshotProp && snapshotProp.SetterName == "PMNet_Set_projectileSnapshotV1",
                "`_projectileSnapshotV1` 的写入口是生成的 Setter");

            // ── 旧条目一条都不能少（新增不能替换旧注册）──────────────────────────
            CheckTrue(HasRpc(PMR3Player.PMGeneratedClassId, FrozenRpcServerProbe, PMRpcKind.Server, true, PMRpcValidator.ForceValidate),
                "旧 RPC ServerProbe 仍在注册且档位未变");
            CheckTrue(HasRpc(PMR3Player.PMGeneratedClassId, FrozenRpcClientEcho, PMRpcKind.Client, true, PMRpcValidator.None),
                "旧 RPC ClientEcho 仍在注册且档位未变");
            CheckTrue(HasRpc(PMR3Player.PMGeneratedClassId, FrozenRpcServerMovementInput, PMRpcKind.Server, false, PMRpcValidator.ForceValidate),
                "旧 RPC ServerMovementInputV1 仍在注册且档位未变");
            CheckTrue(HasRpc(PMR3Player.PMGeneratedClassId, FrozenRpcServerMovementResync, PMRpcKind.Server, true, PMRpcValidator.ForceValidate),
                "旧 RPC ServerMovementResyncV1 仍在注册且档位未变");
            CheckTrue(HasRpc(PMR3Player.PMGeneratedClassId, FrozenRpcClientMovementEvents, PMRpcKind.Client, true, PMRpcValidator.None),
                "旧 RPC ClientMovementEventsV1 仍在注册且档位未变");
            CheckTrue(HasRpc(PMR3Player.PMGeneratedClassId, FrozenRpcClientMovementResync, PMRpcKind.Client, true, PMRpcValidator.None),
                "旧 RPC ClientMovementResyncV1 仍在注册且档位未变");

            // ── 新增三条：方向/可靠性/校验档位按契约冻结 ──────────────────────────
            CheckTrue(HasRpc(PMR3Player.PMGeneratedClassId, NewRpcServerProjectileSpawn,
                    PMRpcKind.Server, true, PMRpcValidator.ForceValidate),
                "新增 ServerProjectileSpawnV1 = Server/可靠/ForceValidate");
            CheckTrue(HasRpc(PMR3Player.PMGeneratedClassId, NewRpcServerProjectileHit,
                    PMRpcKind.Server, true, PMRpcValidator.ForceValidate),
                "新增 ServerProjectileHitV1 = Server/可靠/ForceValidate");
            CheckTrue(HasRpc(PMR3Player.PMGeneratedClassId, NewRpcClientProjectileDecision,
                    PMRpcKind.Client, true, PMRpcValidator.None),
                "新增 ClientProjectileDecisionV1 = Client/可靠（无校验档位）");

            // 生成桩常量与注册表必须**同源**（避免手写常量与生成物漂移）。
            CheckEq(PMR3Player.PMGeneratedRpcId_ServerProjectileSpawnV1, NewRpcServerProjectileSpawn,
                "生成的 ServerProjectileSpawnV1 RpcId 常量 == 冻结值");
            CheckEq(PMR3Player.PMGeneratedRpcId_ServerProjectileHitV1, NewRpcServerProjectileHit,
                "生成的 ServerProjectileHitV1 RpcId 常量 == 冻结值");
            CheckEq(PMR3Player.PMGeneratedRpcId_ClientProjectileDecisionV1, NewRpcClientProjectileDecision,
                "生成的 ClientProjectileDecisionV1 RpcId 常量 == 冻结值");

            CheckTrue(PMR3Runtime.ProtocolHash != 0u, "GlobalProtocolHash 非 0（新增类已混入摘要）");
            CheckEq(global::PMNet.Generated.PMNetGeneratedRegistry.ProtocolHash, PMNetRegistry.ProtocolHash,
                "生成期摘要 == 运行期摘要");
        }

        private static bool HasRpc(uint classId, ushort rpcId, PMRpcKind direction, bool reliable,
                                   PMRpcValidator validator)
        {
            PMNetRpcEntry entry;
            if (!PMNetRegistry.TryGetRpc(classId, rpcId, out entry) || entry == null)
            {
                return false;
            }

            return entry.Descriptor.Direction == direction
                   && entry.Descriptor.IsReliable == reliable
                   && entry.Descriptor.Validator == validator;
        }

        // =================================================================================
        //  B. 真实派发 / 初始化 / 通知
        // =================================================================================

        private static void TestDispatchInitializationAndNotify()
        {
            PMNetRpcReceive.ResetStats();

            Rig rig = Rig.BuildServerWithClient("decl-dispatch", 5001u, 71);
            List<PMR5Projectile> created = new List<PMR5Projectile>();
            List<PMR5Projectile> updated = new List<PMR5Projectile>();

            PMR5ProjectileEvents.Subscription events = PMR5ProjectileEvents.Subscribe(
                rig.ClientWorld,
                delegate(PMR5Projectile p) { created.Add(p); },
                delegate(PMR5Projectile p) { updated.Add(p); },
                null);

            try
            {
                PMR3Player serverPlayer = SpawnPlayer(rig);
                CheckTrue(serverPlayer != null, "权威侧创建玩家副本（承载投射物 RPC）");
                if (serverPlayer == null) { return; }

                byte[] initial = new byte[] { 0x11, 0x22, 0x33 };
                PMR5Projectile serverProj = SpawnProjectile(rig, initial);
                CheckTrue(serverProj != null, "权威侧创建投射物副本（初值在 Spawn 之前写好）");
                if (serverProj == null) { return; }

                rig.Frame(4);

                PMR3Player clientPlayer = FindClientPlayer(rig, serverPlayer);
                CheckTrue(clientPlayer != null, "客户端收到玩家副本");
                if (clientPlayer == null) { return; }

                PMNetObject clientObj;
                bool found = rig.ClientWorld.TryFind(serverProj.NetId, out clientObj) && clientObj != null;
                CheckTrue(found, "客户端经生命周期消息收到投射物副本");
                if (!found) { return; }

                PMR5Projectile clientProj = clientObj as PMR5Projectile;
                CheckTrue(clientProj != null, "副本类型为 PMR5Projectile");
                if (clientProj == null) { return; }

                // B1：Create 回调恰好一次，且**初始 snapshot 与创建原子到达**。
                CheckEq(clientProj.ReplicatedCreateCount, 1, "客户端副本创建回调恰好 1 次");
                CheckTrue(SameBytes(clientProj.ProjectileSnapshotPayload, initial),
                    "Create 回调里初始 snapshot 已经到位（初值与创建原子到达）");

                // B2：全局 Created 事件（按 world 筛选）也恰好一次，并且是同一个对象。
                CheckEq(created.Count, 1, "Created 事件恰好 1 次");
                CheckTrue(created.Count == 1 && ReferenceEquals(created[0], clientProj),
                    "Created 事件携带的是本 world 的那个副本");

                // B3：owner 经**生成桩**发两条上行（不手写业务包）。
                FakeProjectileDriver serverDriver = new FakeProjectileDriver();
                serverPlayer.ProjectileDriver = serverDriver;

                byte[] spawnPayload = new byte[] { 0x0A, 0x0B };
                byte[] hitPayload = new byte[] { 0x0C, 0x0D, 0x0E };

                clientPlayer.ServerProjectileSpawnV1(spawnPayload);
                CheckEq(global::PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount, 0,
                    "RemoteSender 已接线：生成桩没有落进待发队列");
                rig.Frame(4);

                CheckEq(serverDriver.SpawnPayloads.Count, 1, "DS 的 driver 收到 1 条生成载荷");
                CheckTrue(serverDriver.SpawnPayloads.Count == 1 && SameBytes(serverDriver.SpawnPayloads[0], spawnPayload),
                    "生成载荷字节原样到达 driver（生成桩编码/接收侧解码一致）");
                CheckEq(rig.ServerConnection.RpcApplied, 1, "服务端连接应用了 1 条 RPC");
                CheckEq(rig.ServerConnection.RpcRejected, 0, "服务端连接没有拒绝 RPC");

                clientPlayer.ServerProjectileHitV1(hitPayload);
                rig.Frame(4);

                CheckEq(serverDriver.HitPayloads.Count, 1, "DS 的 driver 收到 1 条命中载荷");
                CheckTrue(serverDriver.HitPayloads.Count == 1 && SameBytes(serverDriver.HitPayloads[0], hitPayload),
                    "命中载荷字节原样到达 driver");

                // B4：通知（RepNotify）——服务端改快照 ⇒ 客户端 Updated 事件 + 载荷收敛。
                //
                // 注意断言口径：复制层对**每一条送达的 Update 记录**都派发 OnRep（不做值比较），
                // 而未确认的属性会跨帧重传，因此同一次业务变更可能被通知多次。
                // OnRep 的语义是「通知/入队」而不是「事件计数」（契约 §3.9.4），
                // 所以这里断言的是「**至少**通知一次 + 每次都是本对象 + 载荷最终收敛」，
                // 而不是钉死某个次数（钉死次数会变成在验收复制层的重传节奏）。
                byte[] next = new byte[] { 0x44, 0x55 };
                serverProj.PublishProjectileSnapshot(next);
                rig.Frame(4);

                CheckTrue(updated.Count >= 1, "客户端 Updated 事件至少 1 次（RepNotify 只通知/入队），实际 "
                                              + updated.Count + " 次");
                bool allSameObject = true;
                for (int i = 0; i < updated.Count; i++)
                {
                    if (!ReferenceEquals(updated[i], clientProj)) { allSameObject = false; break; }
                }

                CheckTrue(allSameObject, "Updated 事件的每个参数都是本 world 的那个副本（无串 world/串对象）");
                CheckTrue(SameBytes(clientProj.ProjectileSnapshotPayload, next),
                    "投射物快照复制收敛到客户端");
            }
            finally
            {
                events.Dispose();
                rig.Dispose();
            }
        }

        // =================================================================================
        //  C. 销毁
        // =================================================================================

        private static void TestDestroy()
        {
            Rig rig = Rig.BuildServerWithClient("decl-destroy", 5002u, 72);
            List<PMObjectDestroyReason> destroyed = new List<PMObjectDestroyReason>();

            PMR5ProjectileEvents.Subscription events = PMR5ProjectileEvents.Subscribe(
                rig.ClientWorld, null, null,
                delegate(PMR5Projectile p, PMObjectDestroyReason r) { destroyed.Add(r); });

            try
            {
                PMR5Projectile serverProj = SpawnProjectile(rig, new byte[] { 0x01 });
                CheckTrue(serverProj != null, "权威侧创建投射物副本");
                if (serverProj == null) { return; }

                rig.Frame(4);

                PMNetObject clientObj;
                bool found = rig.ClientWorld.TryFind(serverProj.NetId, out clientObj) && clientObj != null;
                CheckTrue(found, "客户端收到投射物副本（销毁前置）");
                if (!found) { return; }

                PMR5Projectile clientProj = clientObj as PMR5Projectile;
                if (clientProj == null) { CheckTrue(false, "副本类型为 PMR5Projectile"); return; }

                bool ok = rig.Server.Bridge.DestroyObject(serverProj, PMObjectDestroyReason.Destroyed);
                CheckTrue(ok, "权威侧销毁投射物成功");
                rig.Frame(4);

                CheckEq(clientProj.ReplicatedDestroyCount, 1, "客户端副本销毁回调恰好 1 次");
                CheckEq(destroyed.Count, 1, "Destroyed 事件恰好 1 次");
                CheckTrue(destroyed.Count == 1 && destroyed[0] == PMObjectDestroyReason.Destroyed,
                    "Destroyed 事件携带真实销毁原因（Destroyed）");

                PMNetObject gone;
                CheckTrue(!rig.ClientWorld.TryFind(serverProj.NetId, out gone),
                    "销毁后客户端世界已找不到该对象（不是只发了个事件）");
            }
            finally
            {
                events.Dispose();
                rig.Dispose();
            }
        }

        // =================================================================================
        //  D. 非 owner 拒
        // =================================================================================

        private static void TestNonOwnerRejection()
        {
            List<PMNetRpcReceiveResult> observed = new List<PMNetRpcReceiveResult>();
            PMNetRpcReceive.Observer = delegate(PMNetRpcReceiveResult r) { observed.Add(r); };
            PMNetRpcReceive.ResetStats();

            Rig rig = Rig.BuildServerWithTwoClients("decl-nonowner", 5003u, 81, 82);
            try
            {
                PMR3Player serverPlayer = SpawnPlayer(rig, 0);
                CheckTrue(serverPlayer != null, "权威侧为 uid=81 创建玩家副本");
                if (serverPlayer == null) { return; }

                FakeProjectileDriver serverDriver = new FakeProjectileDriver();
                serverPlayer.ProjectileDriver = serverDriver;

                rig.Frame(4);

                PMR3Player otherPlayer = FindClientPlayer(rig, serverPlayer, 1);
                CheckTrue(otherPlayer != null, "第二个客户端也收到了该玩家副本（SimulatedProxy）");
                if (otherPlayer == null) { return; }

                // 非 owner 冒名发上行生成载荷：客户端侧不额外判归属，
                // 但**服务端**必须按「来源连接是不是该对象 owner」拒绝（D-R0-42）。
                long rejectsBefore = rig.ServerViews[1].Connection.RpcRejected;
                otherPlayer.ServerProjectileSpawnV1(new byte[] { 0x0F });
                rig.Frame(6);

                CheckEq(serverDriver.SpawnPayloads.Count, 0, "非 owner 的载荷没有到达 driver（实现未执行）");
                CheckTrue(rig.ServerViews[1].Connection.RpcRejected > rejectsBefore,
                    "服务端在第二条连接上拒绝了 RPC（RpcRejected 增长）");
                CheckEq(rig.ServerViews[1].Connection.RpcApplied, 0, "第二条连接应用过的 RPC 数保持 0");

                bool sawNotOwner = false;
                for (int i = 0; i < observed.Count; i++)
                {
                    if (observed[i].Status == PMRpcReceiveStatus.NotOwner) { sawNotOwner = true; break; }
                }

                CheckTrue(sawNotOwner, "接收入口给出了 NotOwner 状态（失败可归因）");

                // 对照：owner 自己的载荷仍然能通过（证明拒绝是归属原因，不是链路坏了）。
                PMR3Player ownerPlayer = FindClientPlayer(rig, serverPlayer, 0);
                CheckTrue(ownerPlayer != null, "第一个客户端拿到自己的副本");
                if (ownerPlayer != null)
                {
                    ownerPlayer.ServerProjectileSpawnV1(new byte[] { 0x10 });
                    rig.Frame(6);
                    CheckEq(serverDriver.SpawnPayloads.Count, 1, "owner 的载荷仍然被接受（对照）");
                }
            }
            finally
            {
                PMNetRpcReceive.Observer = null;
                rig.Dispose();
            }
        }

        // =================================================================================
        //  E. driver 缺失的失败关闭
        // =================================================================================

        private static void TestDriverMissingFailClosed()
        {
            List<string> rejections = new List<string>();
            PMRpcValidationSink.OnReported = delegate(PMNetObject t, ushort rpcId, PMRpcValidation verdict, string method)
            {
                if (verdict == PMRpcValidation.Reject) { rejections.Add(method); }
            };

            PMNetRpcReceive.ResetStats();

            Rig rig = Rig.BuildServerWithClient("decl-driver-missing", 5004u, 91);
            try
            {
                // 服务端玩家副本**故意不接 driver**。
                PMR3Player serverPlayer = SpawnPlayer(rig);
                CheckTrue(serverPlayer != null, "权威侧创建玩家副本（未接 driver）");
                if (serverPlayer == null) { return; }
                CheckTrue(serverPlayer.ProjectileDriver == null, "前置：服务端副本没有 ProjectileDriver");

                SpawnProjectile(rig, new byte[] { 0x03 });
                rig.Frame(4);

                PMR3Player clientPlayer = FindClientPlayer(rig, serverPlayer);
                CheckTrue(clientPlayer != null, "客户端收到玩家副本");
                if (clientPlayer == null) { return; }

                // E1：上行在 ForceValidate 里被拒（**不是**假成功，也不是静默丢弃）。
                long appliedBefore = rig.ServerConnection.RpcApplied;
                clientPlayer.ServerProjectileSpawnV1(new byte[] { 0x21, 0x22 });
                rig.Frame(4);

                bool sawReject = false;
                for (int i = 0; i < rejections.Count; i++)
                {
                    if (rejections[i] == "ServerProjectileSpawnV1") { sawReject = true; break; }
                }

                CheckTrue(sawReject, "无 driver 时上行被 ForceValidate 判 Reject（三态上报可观测）");
                CheckTrue(rig.ServerConnection.RpcApplied > appliedBefore,
                    "Reject 仍算「已投递并被处置」（ForceValidate 的 Reject 语义：不断连）");
                CheckEq(rig.ServerConnection.RpcDisconnectRequests, 0,
                    "ForceValidate Reject 没有请求断连（它是参数域拒绝）");

                // E2：下行无 driver ⇒ 可观察丢弃（计数增长，不静默吞掉）。
                Rig rig2 = Rig.BuildServerWithClient("decl-decision-discard", 5005u, 92);
                try
                {
                    PMR3Player serverPlayer2 = SpawnPlayer(rig2);
                    CheckTrue(serverPlayer2 != null, "权威侧创建第二个玩家副本（下行裁决发送方）");
                    if (serverPlayer2 == null) { return; }

                    PMR5Projectile proj2 = SpawnProjectile(rig2, new byte[] { 0x04 });
                    CheckTrue(proj2 != null, "权威侧创建投射物副本（裁决关联对象）");
                    if (proj2 == null) { return; }

                    rig2.Frame(4);

                    PMR3Player ownerPlayer = FindClientPlayer(rig2, serverPlayer2);
                    CheckTrue(ownerPlayer != null, "owner 收到自己的玩家副本");
                    if (ownerPlayer == null) { return; }

                    CheckTrue(ownerPlayer.ProjectileDriver == null, "前置：owner 副本没有 ProjectileDriver");

                    serverPlayer2.ClientProjectileDecisionV1(new byte[] { 0x31 });
                    rig2.Frame(4);

                    CheckEq(ownerPlayer.ProjectileDecisionAppliedCount, 0, "无 driver 时下行没有被应用");
                    CheckEq(ownerPlayer.ProjectileDecisionDiscardedCount, 1,
                        "无 driver 时下行计入可观察丢弃（不是静默忽略）");

                    // 对照：接上 driver 后同一条下行正常投递。
                    FakeProjectileDriver clientDriver = new FakeProjectileDriver();
                    ownerPlayer.ProjectileDriver = clientDriver;
                    serverPlayer2.ClientProjectileDecisionV1(new byte[] { 0x32 });
                    rig2.Frame(4);

                    CheckEq(clientDriver.DecisionPayloads.Count, 1, "接上 driver 后下行裁决到达 driver");
                    CheckEq(ownerPlayer.ProjectileDecisionAppliedCount, 1, "接上 driver 后 applied 计数增长");
                    CheckEq(ownerPlayer.ProjectileDecisionDiscardedCount, 1, "丢弃计数不再增长");
                }
                finally
                {
                    rig2.Dispose();
                }
            }
            finally
            {
                PMRpcValidationSink.OnReported = null;
                rig.Dispose();
            }
        }

        // =================================================================================
        //  F. 数组上限
        // =================================================================================

        private static void TestArrayLimit()
        {
            PMNetRpcReceive.ResetStats();

            Rig rig = Rig.BuildServerWithClient("decl-array-limit", 5006u, 93);
            try
            {
                PMR3Player serverPlayer = SpawnPlayer(rig);
                CheckTrue(serverPlayer != null, "权威侧创建玩家副本");
                if (serverPlayer == null) { return; }

                FakeProjectileDriver serverDriver = new FakeProjectileDriver();
                serverPlayer.ProjectileDriver = serverDriver;

                SpawnProjectile(rig, new byte[] { 0x05 });
                rig.Frame(4);

                PMR3Player clientPlayer = FindClientPlayer(rig, serverPlayer);
                CheckTrue(clientPlayer != null, "客户端收到玩家副本");
                if (clientPlayer == null) { return; }

                // F1：发送侧——超过 4096 的实参在**入队之前**就抛（不悄悄截断，也不发出去）。
                bool threw = false;
                try
                {
                    clientPlayer.ServerProjectileSpawnV1(new byte[PMR3Player.ProjectileMaxPayloadBytes + 1]);
                }
                catch (FormatException)
                {
                    threw = true;
                }

                CheckTrue(threw, "发送侧：4097 字节载荷在生成桩里抛出 FormatException");
                rig.Frame(4);
                CheckEq(serverDriver.SpawnPayloads.Count, 0, "超长载荷没有到达 DS 的 driver");
                CheckEq(global::PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount, 0,
                    "超长载荷没有落进待发队列");

                // F2：接收侧——伪造超出生成物读入门的长度前缀，必须在**业务实现之前**被拒（畸形）。
                PMNetWriter w = new PMNetWriter();
                w.WriteSInt32(PMR3Player.ProjectileMaxPayloadBytes + 1);
                byte[] malformed = w.ToArray();

                PMNetRpcReceiveResult result = PMNetRpcReceive.Deliver(
                    rig.Server.World, rig.ServerConnection,
                    PMR3Player.PMGeneratedRpcId_ServerProjectileSpawnV1,
                    serverPlayer.NetId.Value, malformed, 0, malformed.Length);

                CheckTrue(result.Status == PMRpcReceiveStatus.Malformed,
                    "接收侧：超长长度前缀判畸形（实际 " + result.Status + "）");
                CheckEq(serverDriver.SpawnPayloads.Count, 0, "畸形载荷没有到达 DS 的 driver");

                // F3：边界内（恰好 4096）必须正常通过，证明上面拒绝的是超限而不是「大包一律拒」。
                byte[] maxPayload = new byte[PMR3Player.ProjectileMaxPayloadBytes];
                for (int i = 0; i < maxPayload.Length; i++) { maxPayload[i] = (byte)(i & 0xFF); }
                clientPlayer.ServerProjectileSpawnV1(maxPayload);
                rig.Frame(4);

                CheckEq(serverDriver.SpawnPayloads.Count, 1, "恰好 4096 字节的载荷被接受（边界内）");
                CheckTrue(serverDriver.SpawnPayloads.Count == 1 && SameBytes(serverDriver.SpawnPayloads[0], maxPayload),
                    "4096 字节载荷字节逐字节一致（没有被截断）");
            }
            finally
            {
                rig.Dispose();
            }
        }

        // =================================================================================
        //  G. 多 world 事件筛选与独立 Dispose
        // =================================================================================

        private static void TestMultiWorldEvents()
        {
            Rig rig = Rig.BuildServerWithTwoClients("decl-multiworld", 5007u, 95, 96);

            PMNetWorld worldA = rig.ClientEndpoints[0].World;
            PMNetWorld worldB = rig.ClientEndpoints[1].World;

            int createdA = 0;
            int createdB = 0;
            int destroyedA = 0;
            int destroyedB = 0;

            PMR5ProjectileEvents.Subscription subA = PMR5ProjectileEvents.Subscribe(
                worldA,
                delegate(PMR5Projectile p) { createdA++; },
                null,
                delegate(PMR5Projectile p, PMObjectDestroyReason r) { destroyedA++; });

            PMR5ProjectileEvents.Subscription subB = PMR5ProjectileEvents.Subscribe(
                worldB,
                delegate(PMR5Projectile p) { createdB++; },
                null,
                delegate(PMR5Projectile p, PMObjectDestroyReason r) { destroyedB++; });

            try
            {
                CheckEq(PMR5ProjectileEvents.SubscriptionCount, 2, "两张订阅都在（前置）");

                // G1：不同世界的订阅各看各的，事件不乱投。
                PMR5Projectile first = SpawnProjectile(rig, new byte[] { 0x06 });
                CheckTrue(first != null, "创建投射物（两个客户端各收一份副本）");
                if (first == null) { return; }

                rig.Frame(4);

                CheckEq(createdA, 1, "worldA 的订阅看到自己 world 的 Created");
                CheckEq(createdB, 1, "worldB 的订阅看到自己 world 的 Created（同一对象在各自 world 各一份）");

                // G2：Dispose 一张订阅**只**摘掉自己。
                subA.Dispose();
                CheckTrue(subA.IsDisposed, "subA 已取消");
                CheckEq(PMR5ProjectileEvents.SubscriptionCount, 1, "取消后只剩 subB 一张订阅");
                CheckTrue(!subB.IsDisposed, "subB 未被 subA 的 Dispose 影响");

                // G3：**一个 world 退出（Detach）不得清掉别的 world 的事件订阅。**
                PMR3Runtime.Detach(worldA);
                CheckEq(PMR5ProjectileEvents.SubscriptionCount, 1, "Detach 一个 world 后，别的 world 的订阅仍在");

                PMR5Projectile second = SpawnProjectile(rig, new byte[] { 0x07 });
                CheckTrue(second != null, "再创建一个投射物");
                if (second == null) { return; }

                rig.Frame(4);

                CheckEq(createdA, 1, "已取消的 subA 不再收到事件");
                CheckEq(createdB, 2, "subB 仍然收到事件（Detach 别的 world 不影响它）");

                // G4：销毁路径同样按 world 筛选。
                rig.Server.Bridge.DestroyObject(second, PMObjectDestroyReason.OutOfRange);
                rig.Frame(4);

                CheckEq(destroyedA, 0, "已取消的 subA 收不到 Destroyed");
                CheckEq(destroyedB, 1, "subB 收到 Destroyed 及其原因");

                subB.Dispose();
                CheckEq(PMR5ProjectileEvents.SubscriptionCount, 0, "两张订阅都取消后订阅数为 0");
            }
            finally
            {
                if (!subA.IsDisposed) { subA.Dispose(); }
                if (!subB.IsDisposed) { subB.Dispose(); }
                rig.Dispose();
            }
        }

        // =================================================================================
        //  H. 清理
        // =================================================================================

        private static void TestCleanup()
        {
            Rig rig = Rig.BuildServerWithClient("decl-cleanup", 5008u, 97);
            PMR5ProjectileEvents.Subscription sub = PMR5ProjectileEvents.Subscribe(rig.ClientWorld, null, null, null);

            try
            {
                CheckEq(PMR5ProjectileEvents.SubscriptionCount, 1, "前置：有一张存活订阅");
            }
            finally
            {
                rig.Dispose();
            }

            // 契约：ClearAll **只在进程整体 Shutdown** 清理（不是某个 world 退出时）。
            CheckEq(PMR5ProjectileEvents.SubscriptionCount, 1, "world 退出（Detach）不清事件订阅");
            PMR3Runtime.Shutdown();
            CheckEq(PMR5ProjectileEvents.SubscriptionCount, 0, "Shutdown 走 ClearAll，残留订阅被清空");
            CheckTrue(sub.IsDisposed, "ClearAll 后旧凭证进入已取消状态（幂等 Dispose 安全）");
            sub.Dispose();
            CheckEq(PMR5ProjectileEvents.SubscriptionCount, 0, "ClearAll 之后再 Dispose 幂等（不会负增长/异常）");
        }

        // =================================================================================
        //  测试替身与夹具
        // =================================================================================

        /// <summary>
        /// 假的投射物驱动：只记录收到的载荷（契约允许测试用 fake interface handler）。
        /// 它**不**做任何解码/身份判定——那是 B2b driver 的职责，本批次不验收那部分。
        /// </summary>
        private sealed class FakeProjectileDriver : IPMProjectileNetworkDriver
        {
            public readonly List<byte[]> SpawnPayloads = new List<byte[]>();
            public readonly List<byte[]> HitPayloads = new List<byte[]>();
            public readonly List<byte[]> DecisionPayloads = new List<byte[]>();

            public void OnServerSpawnPayload(byte[] payload) { SpawnPayloads.Add(payload); }

            public void OnServerHitPayload(byte[] payload) { HitPayloads.Add(payload); }

            public void OnClientDecisionPayload(byte[] payload) { DecisionPayloads.Add(payload); }
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b)) { return true; }
            if (a == null || b == null || a.Length != b.Length) { return false; }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) { return false; }
            }

            return true;
        }

        private static byte[] Copy(byte[] source)
        {
            if (source == null) { return null; }
            byte[] copy = new byte[source.Length];
            Buffer.BlockCopy(source, 0, copy, 0, source.Length);
            return copy;
        }

        /// <summary>权威侧创建一个玩家副本（投射物三条 RPC 的承载对象）。</summary>
        private static PMR3Player SpawnPlayer(Rig rig)
        {
            return SpawnPlayer(rig, 0);
        }

        private static PMR3Player SpawnPlayer(Rig rig, int clientIndex)
        {
            return PMR3Runtime.SpawnPlayer(rig.Server.World, rig.Server.Bridge,
                rig.ServerViews[clientIndex].Connection);
        }

        /// <summary>
        /// 权威侧创建一个投射物副本：**先把初始 snapshot 写好，再 Spawn 并登记复制**
        /// （契约：初始 snapshot 已写好后才上线，Pending 期间无客户端幽灵对象）。
        /// </summary>
        private static PMR5Projectile SpawnProjectile(Rig rig, byte[] initialSnapshot)
        {
            PMR5Projectile proj = new PMR5Projectile();
            proj.PublishProjectileSnapshot(Copy(initialSnapshot));
            if (!rig.Server.World.Spawn(proj, PMR5Projectile.PMGeneratedClassId))
            {
                return null;
            }

            rig.Server.Bridge.RegisterReplicatedObject(proj);
            return proj;
        }

        /// <summary>在客户端世界里按 NetId 找回该玩家副本。</summary>
        private static PMR3Player FindClientPlayer(Rig rig, PMR3Player serverPlayer)
        {
            return FindClientPlayer(rig, serverPlayer, 0);
        }

        private static PMR3Player FindClientPlayer(Rig rig, PMR3Player serverPlayer, int clientIndex)
        {
            PMNetObject obj;
            if (!rig.ClientEndpoints[clientIndex].World.TryFind(serverPlayer.NetId, out obj) || obj == null)
            {
                return null;
            }

            return obj as PMR3Player;
        }

        // =================================================================================
        //  手工链路 / 端点 / 场景（与 PMR3RuntimeTest 同一手法：真实 PMTransport 字节链）
        // =================================================================================

        private sealed class TestLink : IPMTransportLink
        {
            public string Name;
            public LinkHub Hub;
            public PMTransportConnection Connection;
            public readonly List<TestLink> Peers = new List<TestLink>(1);

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

        /// <summary>
        /// 手工链路 + 真实 PMTransport 的双端场景（与 PMR3RuntimeTest 的 Rig 同一模型）：
        /// 一条逻辑链路有**两个**连接对象（服务端一份视图、客户端一份），
        /// 因此「非 owner 被拒」才有意义。
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

            public PMTransportConnection ServerConnection { get { return ServerViews[0].Connection; } }

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
                identity.MatchId = "match-r5-declaration-test";
                identity.DsId = "ds-r5-declaration-test";

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
                string candidate = Path.Combine(dir.FullName, "Client", "Assets", "Scripts", "PMR3", "PMR5Projectile.cs");
                if (File.Exists(candidate)) { return dir.FullName; }
                dir = dir.Parent;
            }

            return null;
        }
    }
}
