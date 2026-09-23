// R6-A2 门禁：PMR3Player 的**战斗声明层**（9 条复制属性 + 4 条 RPC）+ 单一生成集合 + OwnerOnly 权限边界。
//
// 事实来源：
//   - `Docs/plans/net-r6-combat-contract.md` §A2（声明：字段/条件/接口/RPC 与参数域）；
//   - `Docs/plans/net-r2-codegen-contract.md` §2（稳定 ID）/§4（生成物 API 面）/§5 规则 13；
//   - `Docs/plans/net-r3-control-contract.md` §7.4（Runtime.Attach / SpawnPlayer 唯一 owner）；
//   - `Docs/plans/net-architecture-migration.md` 文末 T6A4。
//
// 覆盖：
//   A. 声明与单一生成集合（--decl-check 逐字节 + 旧 ID 冻结 + 新 ID 冻结 + 描述符条件 + 零反射）
//   B. OwnerOnly 权限边界（裸 World + 真实 PMReplicationChannel + 生命周期 codec，**逐连接掩码**）
//   C. 真实 Transport 双连接端到端（Create 初值/后续变更/公共 HP 死亡/OnRep 真派发）
//   D. RPC 归属与参数域（owner 通过 / observer 被拒 / ForceValidate 边界）
//   E. null driver 的失败关闭（上行 Reject 不断连 / 下行可观察丢弃）
//   F. PublishCombatState 的 World role 权限（DS 放行 / AP 拒绝且值不变）
//   G. 边界与清理（生成文件集合唯一 / Detach+Shutdown 幂等）
//
// **不引 Unity、不引 PMCombat 纯核心、不引 R5 Projectile/Coordinator**：
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

namespace PMR6DeclarationTest
{
    internal static class Program
    {
        private static int _passed;
        private static readonly List<string> _failures = new List<string>();

        // =================================================================================
        //  冻结的 ID（**外部 oracle**：字面量，不是「生成物和自己比」）
        // =================================================================================

        // ── R6-A2 之前就已存在的 ID（**不得漂移**；一旦漂移等于改了线协议）─────────────
        private const long FrozenPlayerClassId = 405815557L;
        private const long FrozenProjectileClassId = 227098277L;
        private const long FrozenPropUid = 18801L;
        private const long FrozenPropProbeCount = 12656L;
        private const long FrozenPropMovementSnapshot = 61580L;
        private const long FrozenPropProjectileSnapshot = 6683L;
        private const long FrozenRpcServerProbe = 34232L;
        private const long FrozenRpcClientEcho = 63853L;
        private const long FrozenRpcServerMovementInput = 25428L;
        private const long FrozenRpcServerMovementResync = 15021L;
        private const long FrozenRpcClientMovementEvents = 5180L;
        private const long FrozenRpcClientMovementResync = 28381L;
        private const long FrozenRpcServerProjectileSpawn = 21590L;
        private const long FrozenRpcServerProjectileHit = 33011L;
        private const long FrozenRpcClientProjectileDecision = 38620L;

        // ── R6-A2 由**真实生成器**新分配的 ID（从此冻结；后续不得被新键挤走）────────────
        private const long NewPropCombatDead = 4259L;
        private const long NewPropCombatSuperEnergy = 5878L;
        private const long NewPropCombatMatchEnded = 21371L;
        private const long NewPropCombatTeamId = 24395L;
        private const long NewPropCombatHp = 31999L;
        private const long NewPropCombatMana = 34826L;
        private const long NewPropCombatHeroId = 36554L;
        private const long NewPropCombatWinnerTeamId = 38154L;
        private const long NewPropCombatMaxHp = 64920L;
        private const long NewRpcClientCombatMatchResult = 39181L;
        private const long NewRpcServerCombatAttack = 42343L;
        private const long NewRpcClientCombatAttackResult = 44706L;
        private const long NewRpcServerCombatResultAck = 50908L;

        // ── 协议摘要（R6-A2 冻结；0 = 未声明哨兵，非 0 本身就是「摘要真的算过」）──────────
        private const long NewPlayerClassProtocolHash = 0xB09BCD1CL;
        private const long NewProjectileClassProtocolHash = 0xD4CB0B42L;
        private const long NewGlobalProtocolHash = 0xE6130FAAL;

        /// <summary>复制属性总数：3（uid/probe/movementSnapshot）+ 9（战斗）= 12。</summary>
        private const int PlayerPropertyCount = 12;

        private static readonly List<string> _combatWarnings = new List<string>();
        private static readonly Dictionary<PMNetWorld, CreateSnapshot> _createSnapshots =
            new Dictionary<PMNetWorld, CreateSnapshot>();

        private static int Main()
        {
            Console.WriteLine("=== R6-A2：战斗声明层（9 属性 + 4 RPC）/ OwnerOnly 权限边界 / 单一生成集合 ===");
            Console.WriteLine();

            PMR3Runtime.Warn = delegate(string m) { Console.WriteLine("      [pmr3runtime] " + m); };
            PMR3Player.CombatWarn = delegate(string m) { _combatWarnings.Add(m); Console.WriteLine("      [combat] " + m); };
            PMR3Player.ProjectileWarn = delegate(string m) { Console.WriteLine("      [projectile] " + m); };

            Section("A. 声明与单一生成集合（--decl-check + 旧/新 ID + 描述符条件）", TestDeclarationArtifacts);
            Section("B. OwnerOnly 权限边界（逐连接掩码）", TestOwnerOnlyPermissionBoundary);
            Section("C. 真实 Transport 双连接端到端", TestReplicationOverRealTransport);
            Section("D. RPC 归属与参数域", TestRpcOwnershipAndParamDomain);
            Section("E. null driver 的失败关闭", TestNullDriverFailClosed);
            Section("F. PublishCombatState 的 World role 权限", TestPublishPermissionByWorldRole);
            Section("G. 边界与清理", TestBoundariesAndCleanup);

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

            // A2：锁文件是**同一份专用锁**（不动共享锁），旧键与新键都在，且逐条按字面量核对。
            Check(File.Exists(idLock), "专用 ID 锁存在：" + idLock);
            if (File.Exists(idLock))
            {
                string lockText = File.ReadAllText(idLock, Encoding.UTF8);

                Check(lockText.Contains("\"CLASS:PMNet.R3.PMR3Player\": " + FrozenPlayerClassId),
                    "锁文件里 PMR3Player 的 ClassId 仍为 " + FrozenPlayerClassId + "（旧 ID 未漂移）");
                Check(lockText.Contains("\"PROP:PMNet.R3.PMR3Player._uid\": " + FrozenPropUid)
                      && lockText.Contains("\"PROP:PMNet.R3.PMR3Player._probeCount\": " + FrozenPropProbeCount)
                      && lockText.Contains("\"PROP:PMNet.R3.PMR3Player._movementSnapshotV1\": " + FrozenPropMovementSnapshot),
                    "锁文件里 PMR3Player 的三个旧属性 ID 逐条未漂移");
                Check(lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ServerProbe\": " + FrozenRpcServerProbe)
                      && lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ClientEcho\": " + FrozenRpcClientEcho)
                      && lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ServerMovementInputV1\": " + FrozenRpcServerMovementInput)
                      && lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ServerMovementResyncV1\": " + FrozenRpcServerMovementResync)
                      && lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ClientMovementEventsV1\": " + FrozenRpcClientMovementEvents)
                      && lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ClientMovementResyncV1\": " + FrozenRpcClientMovementResync)
                      && lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ServerProjectileSpawnV1\": " + FrozenRpcServerProjectileSpawn)
                      && lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ServerProjectileHitV1\": " + FrozenRpcServerProjectileHit)
                      && lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ClientProjectileDecisionV1\": " + FrozenRpcClientProjectileDecision),
                    "锁文件里 PMR3Player 的九条旧 RPC ID 逐条未漂移");

                Check(lockText.Contains("\"PROP:PMNet.R3.PMR3Player._combatHeroId\": " + NewPropCombatHeroId)
                      && lockText.Contains("\"PROP:PMNet.R3.PMR3Player._combatTeamId\": " + NewPropCombatTeamId)
                      && lockText.Contains("\"PROP:PMNet.R3.PMR3Player._combatHp\": " + NewPropCombatHp)
                      && lockText.Contains("\"PROP:PMNet.R3.PMR3Player._combatMaxHp\": " + NewPropCombatMaxHp)
                      && lockText.Contains("\"PROP:PMNet.R3.PMR3Player._combatDead\": " + NewPropCombatDead)
                      && lockText.Contains("\"PROP:PMNet.R3.PMR3Player._combatMatchEnded\": " + NewPropCombatMatchEnded)
                      && lockText.Contains("\"PROP:PMNet.R3.PMR3Player._combatWinnerTeamId\": " + NewPropCombatWinnerTeamId)
                      && lockText.Contains("\"PROP:PMNet.R3.PMR3Player._combatMana\": " + NewPropCombatMana)
                      && lockText.Contains("\"PROP:PMNet.R3.PMR3Player._combatSuperEnergy\": " + NewPropCombatSuperEnergy),
                    "锁文件含 R6-A2 九条战斗复制属性的稳定键与冻结 ID");

                Check(lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ServerCombatAttackV1\": " + NewRpcServerCombatAttack)
                      && lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ServerCombatResultAckV1\": " + NewRpcServerCombatResultAck)
                      && lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ClientCombatAttackResultV1\": " + NewRpcClientCombatAttackResult)
                      && lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ClientCombatMatchResultV1\": " + NewRpcClientCombatMatchResult),
                    "锁文件含 R6-A2 四条战斗 RPC 的稳定键与冻结 ID");
            }

            string sharedLock = Path.Combine(root, "Docs", "plans", "pmnet-ids.json");
            Check(File.Exists(sharedLock), "共享 ID 锁仍存在（未被本任务改写）：" + sharedLock);
            if (File.Exists(sharedLock))
            {
                Check(!File.ReadAllText(sharedLock, Encoding.UTF8).Contains("PMNet.R3."),
                    "共享锁里没有 PMR3/R5 条目（这两个类用专用锁，未另造 registry/锁）");
            }

            // A3：运行期注册表（零反射、显式列出）。
            PMNetRegistry.Reset();
            PMR3Runtime.Shutdown();
            PMR3Runtime.Register();

            Check(PMNetRegistry.IsSealed, "Register() 之后注册表已封板");
            CheckEq(PMNetRegistry.ClassCount, global::PMNet.Generated.PMNetGeneratedRegistry.GeneratedClassCount,
                "注册类数 == 生成期常量 GeneratedClassCount");
            CheckEq(global::PMNet.Generated.PMNetGeneratedRegistry.GeneratedClassCount, 2,
                "单一生成集合里恰好两个网络类（PMR3Player + PMR5Projectile，R6-A2 未新增类）");
            CheckEq(global::PMNet.Generated.PMNetGeneratedRegistry.ProtocolHash, NewGlobalProtocolHash,
                "生成期整体协议摘要 == R6-A2 冻结值");
            CheckEq(PMNetRegistry.ProtocolHash, NewGlobalProtocolHash, "运行期整体协议摘要 == R6-A2 冻结值");

            PMNetClassEntry entry;
            Check(PMNetRegistry.TryGetClass(PMR3Player.PMGeneratedClassId, out entry) && entry != null,
                "PMR3Player 的 ClassId 仍注册：" + PMR3Player.PMGeneratedClassId);
            CheckEq(PMR3Player.PMGeneratedClassId, FrozenPlayerClassId,
                "PMR3Player 的 ClassId 与冻结值一致（未被新成员挤走 + 未新增类）");
            CheckEq(entry != null && entry.Rep != null ? entry.Rep.ProtocolHash : 0, NewPlayerClassProtocolHash,
                "PMR3Player 的类协议摘要 == R6-A2 冻结值（掩码区间/条件/成员名参与计算）");

            // 位宽与属性数：3 旧 + 9 新。
            CheckEq(PMR3Player.PMGeneratedChangeMaskBitCount, PlayerPropertyCount,
                "复制属性位宽 == 12（Uid + ProbeCount + MovementSnapshot + 9 战斗）");
            CheckEq(entry != null && entry.Rep != null ? entry.Rep.Properties.Length : -1, PlayerPropertyCount,
                "复制描述符里的属性数 == 12");
            Check(entry != null && entry.Rep != null && entry.Rep.HasConditionalMask,
                "描述符标记为含条件属性（OwnerOnly 真的进了条件掩码）");

            // 九条战斗属性逐条断言：ID / 条件 / Push / Setter / OnRep 方法 ID。
            CheckCombatProperty(entry, "_combatHeroId", NewPropCombatHeroId, PMCond.None);
            CheckCombatProperty(entry, "_combatTeamId", NewPropCombatTeamId, PMCond.None);
            CheckCombatProperty(entry, "_combatHp", NewPropCombatHp, PMCond.None);
            CheckCombatProperty(entry, "_combatMaxHp", NewPropCombatMaxHp, PMCond.None);
            CheckCombatProperty(entry, "_combatDead", NewPropCombatDead, PMCond.None);
            CheckCombatProperty(entry, "_combatMatchEnded", NewPropCombatMatchEnded, PMCond.None);
            CheckCombatProperty(entry, "_combatWinnerTeamId", NewPropCombatWinnerTeamId, PMCond.None);
            CheckCombatProperty(entry, "_combatMana", NewPropCombatMana, PMCond.OwnerOnly);
            CheckCombatProperty(entry, "_combatSuperEnergy", NewPropCombatSuperEnergy, PMCond.OwnerOnly);

            // 旧属性一条都不能少（新增不能替换旧注册）。
            Check(PropertyOf(entry, "_uid").PropertyId == FrozenPropUid
                  && PropertyOf(entry, "_probeCount").PropertyId == FrozenPropProbeCount
                  && PropertyOf(entry, "_movementSnapshotV1").PropertyId == FrozenPropMovementSnapshot,
                "三个旧属性的 PropertyId 逐条未漂移（新增只是追加）");
            Check(PropertyOf(entry, "_movementSnapshotV1").SetterName == "PMNet_Set_movementSnapshotV1",
                "旧属性 _movementSnapshotV1 的写入口仍是生成的 Setter");

            // ── 旧 RPC 逐条按档位核对（不因本批次而弱化）────────────────────────────
            Check(HasRpc(PMR3Player.PMGeneratedClassId, FrozenRpcServerProbe, PMRpcKind.Server, true, PMRpcValidator.ForceValidate),
                "旧 RPC ServerProbe = Server/可靠/ForceValidate（未变）");
            Check(HasRpc(PMR3Player.PMGeneratedClassId, FrozenRpcClientEcho, PMRpcKind.Client, true, PMRpcValidator.None),
                "旧 RPC ClientEcho = Client/可靠（未变）");
            Check(HasRpc(PMR3Player.PMGeneratedClassId, FrozenRpcServerMovementInput, PMRpcKind.Server, false, PMRpcValidator.ForceValidate),
                "旧 RPC ServerMovementInputV1 = Server/不可靠/ForceValidate（未变）");
            Check(HasRpc(PMR3Player.PMGeneratedClassId, FrozenRpcServerMovementResync, PMRpcKind.Server, true, PMRpcValidator.ForceValidate),
                "旧 RPC ServerMovementResyncV1 = Server/可靠/ForceValidate（未变）");
            Check(HasRpc(PMR3Player.PMGeneratedClassId, FrozenRpcClientMovementEvents, PMRpcKind.Client, true, PMRpcValidator.None),
                "旧 RPC ClientMovementEventsV1 = Client/可靠（未变）");
            Check(HasRpc(PMR3Player.PMGeneratedClassId, FrozenRpcClientMovementResync, PMRpcKind.Client, true, PMRpcValidator.None),
                "旧 RPC ClientMovementResyncV1 = Client/可靠（未变）");
            Check(HasRpc(PMR3Player.PMGeneratedClassId, FrozenRpcServerProjectileSpawn, PMRpcKind.Server, true, PMRpcValidator.ForceValidate),
                "旧 RPC ServerProjectileSpawnV1 = Server/可靠/ForceValidate（未变）");
            Check(HasRpc(PMR3Player.PMGeneratedClassId, FrozenRpcServerProjectileHit, PMRpcKind.Server, true, PMRpcValidator.ForceValidate),
                "旧 RPC ServerProjectileHitV1 = Server/可靠/ForceValidate（未变）");
            Check(HasRpc(PMR3Player.PMGeneratedClassId, FrozenRpcClientProjectileDecision, PMRpcKind.Client, true, PMRpcValidator.None),
                "旧 RPC ClientProjectileDecisionV1 = Client/可靠（未变）");

            // ── 新增四条：方向/可靠性/校验档位按契约 A2 冻结 ────────────────────────
            Check(HasRpc(PMR3Player.PMGeneratedClassId, NewRpcServerCombatAttack,
                    PMRpcKind.Server, true, PMRpcValidator.ForceValidate),
                "新增 ServerCombatAttackV1 = Server/可靠/ForceValidate");
            Check(HasRpc(PMR3Player.PMGeneratedClassId, NewRpcServerCombatResultAck,
                    PMRpcKind.Server, true, PMRpcValidator.ForceValidate),
                "新增 ServerCombatResultAckV1 = Server/可靠/ForceValidate");
            Check(HasRpc(PMR3Player.PMGeneratedClassId, NewRpcClientCombatAttackResult,
                    PMRpcKind.Client, true, PMRpcValidator.None),
                "新增 ClientCombatAttackResultV1 = Client/可靠（无校验档位）");
            Check(HasRpc(PMR3Player.PMGeneratedClassId, NewRpcClientCombatMatchResult,
                    PMRpcKind.Client, true, PMRpcValidator.None),
                "新增 ClientCombatMatchResultV1 = Client/可靠（无校验档位）");

            // 生成桩常量与注册表必须同源，且都等于**冻结字面量**（不是生成物自比较）。
            CheckEq(PMR3Player.PMGeneratedRpcId_ServerCombatAttackV1, NewRpcServerCombatAttack,
                "生成的 ServerCombatAttackV1 RpcId 常量 == 冻结值");
            CheckEq(PMR3Player.PMGeneratedRpcId_ServerCombatResultAckV1, NewRpcServerCombatResultAck,
                "生成的 ServerCombatResultAckV1 RpcId 常量 == 冻结值");
            CheckEq(PMR3Player.PMGeneratedRpcId_ClientCombatAttackResultV1, NewRpcClientCombatAttackResult,
                "生成的 ClientCombatAttackResultV1 RpcId 常量 == 冻结值");
            CheckEq(PMR3Player.PMGeneratedRpcId_ClientCombatMatchResultV1, NewRpcClientCombatMatchResult,
                "生成的 ClientCombatMatchResultV1 RpcId 常量 == 冻结值");

            // 参数布局哈希必须**不同**（两条 server RPC 的签名不同）且非 0 —— 证明签名真的进了摘要。
            PMNetRpcEntry attackRpc;
            PMNetRpcEntry ackRpc;
            bool hasAttack = PMNetRegistry.TryGetRpc(PMR3Player.PMGeneratedClassId,
                PMR3Player.PMGeneratedRpcId_ServerCombatAttackV1, out attackRpc);
            bool hasAck = PMNetRegistry.TryGetRpc(PMR3Player.PMGeneratedClassId,
                PMR3Player.PMGeneratedRpcId_ServerCombatResultAckV1, out ackRpc);
            Check(hasAttack && attackRpc.Descriptor.ParamLayoutId != 0
                  && hasAck && ackRpc.Descriptor.ParamLayoutId != 0
                  && attackRpc.Descriptor.ParamLayoutId != ackRpc.Descriptor.ParamLayoutId,
                "两条上行战斗 RPC 的参数布局哈希非 0 且互不相同（签名参与协议摘要）");

            // A4：零反射 + 单一 registry（R0-48 / T40）。
            string registryFile = Path.Combine(outDir, "PMNetGeneratedRegistry.g.cs");
            Check(File.Exists(registryFile), "生成集合的 registry 存在：" + registryFile);
            if (File.Exists(registryFile))
            {
                string registryText = File.ReadAllText(registryFile, Encoding.UTF8);
                Check(registryText.IndexOf("Reflection", StringComparison.Ordinal) < 0
                      && registryText.IndexOf("GetTypes(", StringComparison.Ordinal) < 0,
                    "生成集合的 RegisterAll 不含任何运行期反射扫描（D-R0-48）");
                Check(registryText.Contains("global::PMNet.R3.PMR3Player.PMNet_BuildEntry()")
                      && registryText.Contains("global::PMNet.R3.PMR5Projectile.PMNet_BuildEntry()"),
                    "RegisterAll 显式列出两个类（零反射、编译期可知）");
            }

            CheckEq(PMR3Runtime.ProtocolHash, NewGlobalProtocolHash,
                "PMR3Runtime.ProtocolHash == R6-A2 冻结值（握手口径唯一来源）");
        }

        private static void CheckCombatProperty(PMNetClassEntry entry, string memberName,
                                                long propertyId, PMCond condition)
        {
            PMPropertyDescriptor p = PropertyOf(entry, memberName);
            bool found = p.MemberName == memberName;
            Check(found, "复制描述符里注册了 `" + memberName + "`");
            if (!found) { return; }

            CheckEq(p.PropertyId, propertyId, "`" + memberName + "` 的 PropertyId == 冻结值");
            Check(p.Condition == condition, "`" + memberName + "` 的条件 == " + condition
                                            + "（实际 " + p.Condition + "）");
            Check(p.PushBased, "`" + memberName + "` 为 Push 模型（赋值即标脏）");
            Check(p.SetterName == "PMNet_Set_" + memberName.Substring(1),
                "`" + memberName + "` 的写入口是生成的 Setter：" + p.SetterName);
            Check(p.OnRepMethodId != 0, "`" + memberName + "` 已登记 OnRep 方法 ID（只通知/入队）");
        }

        private static PMPropertyDescriptor PropertyOf(PMNetClassEntry entry, string memberName)
        {
            if (entry == null || entry.Rep == null || entry.Rep.Properties == null)
            {
                return default(PMPropertyDescriptor);
            }

            for (int i = 0; i < entry.Rep.Properties.Length; i++)
            {
                if (entry.Rep.Properties[i].MemberName == memberName)
                {
                    return entry.Rep.Properties[i];
                }
            }

            return default(PMPropertyDescriptor);
        }

        private static bool HasRpc(uint classId, long rpcId, PMRpcKind direction, bool reliable,
                                   PMRpcValidator validator)
        {
            PMNetRpcEntry entry;
            if (!PMNetRegistry.TryGetRpc(classId, (ushort)rpcId, out entry) || entry == null)
            {
                return false;
            }

            return entry.Descriptor.Direction == direction
                   && entry.Descriptor.IsReliable == reliable
                   && entry.Descriptor.Validator == validator;
        }

        // =================================================================================
        //  B. OwnerOnly 权限边界（逐连接掩码：真实 World + 真实复制通道 + 真实生命周期 codec）
        // =================================================================================

        private static void TestOwnerOnlyPermissionBoundary()
        {
            const uint epoch = 0x6A1u;

            PMNetWorld world = new PMNetWorld(new PMSession(epoch, true));
            world.Warn = delegate(string m) { Console.WriteLine("      [ds-world] " + m); };
            PMNetSessionBridge bridge = new PMNetSessionBridge(world);
            bridge.Warn = delegate(string m) { Console.WriteLine("      [ds-bridge] " + m); };
            PMR3Runtime.Attach(world, bridge);

            TestConn ownerConn = new TestConn(601, true);
            TestConn otherConn = new TestConn(602, true);
            world.AddConnection(ownerConn);
            world.AddConnection(otherConn);

            PMR3Player ds = new PMR3Player();

            // 权限：**尚未上线**（World == null）时放行 —— DS 允许「先写好初值再上线」（与 R5 投射物同一手法）。
            bool earlyPublish = ds.PublishCombatState(5, 2, 100, 100, false, 42, 17, false, 0);
            Check(earlyPublish, "DS 侧未上线（World==null）时 PublishCombatState 放行（先写初值再 Spawn）");
            CheckEq(ds.CombatPublishCount, 1, "未上线写入计入 CombatPublishCount");

            // 唯一 owner 来源：OwnerConnection（本对象不使用 Owner 链）。
            ds.OwnerConnection = ownerConn;
            Check(world.Spawn(ds, PMR3Player.PMGeneratedClassId), "DS 权威副本 Spawn 成功");
            bridge.RegisterReplicatedObject(ds);

            PMNetClassEntry entry;
            PMNetRegistry.TryGetClass(PMR3Player.PMGeneratedClassId, out entry);
            int manaSlot = PropertyOf(entry, "_combatMana").MaskOffset;
            int energySlot = PropertyOf(entry, "_combatSuperEnergy").MaskOffset;
            ushort manaId = PropertyOf(entry, "_combatMana").PropertyId;
            ushort energyId = PropertyOf(entry, "_combatSuperEnergy").PropertyId;
            ushort hpId = PropertyOf(entry, "_combatHp").PropertyId;
            int hpSlot = PropertyOf(entry, "_combatHp").MaskOffset;

            // ── B1：Create 初值的逐连接槽位（初始状态与创建原子到达，且按条件过滤）──────
            byte[] ownerBatch = world.BuildLifecycleBatch(ownerConn);
            byte[] otherBatch = world.BuildLifecycleBatch(otherConn);

            List<int> ownerSlots = DecodeInitialSlots(ownerBatch);
            List<int> otherSlots = DecodeInitialSlots(otherBatch);

            Check(ownerSlots.Count == PlayerPropertyCount,
                "owner 的 Create 初值携带全部 12 个槽位（实际 " + SlotsToText(ownerSlots) + "）");
            Check(otherSlots.Count == PlayerPropertyCount - 2,
                "observer 的 Create 初值只携带 10 个槽位（少了 OwnerOnly 的两条，实际 " + SlotsToText(otherSlots) + "）");
            Check(ownerSlots.Contains(manaSlot) && ownerSlots.Contains(energySlot),
                "★owner 的 Create 初值含 mana/energy 槽位（" + manaSlot + "/" + energySlot + "）");
            Check(!otherSlots.Contains(manaSlot) && !otherSlots.Contains(energySlot),
                "★observer 的 Create 初值**不含** mana/energy 槽位（初始 Create 不泄资源）");

            // ── B2：后续资源变更的逐连接属性 ID（Update 通道）─────────────────────────
            PMReplicationChannel channel = new PMReplicationChannel(new PMRepOptions());
            channel.World = world;
            channel.Warn = delegate(string m) { Console.WriteLine("      [ds-rep] " + m); };
            channel.RegisterObject(ds);
            channel.RegisterOnRepDispatcher(PMR3Player.PMGeneratedClassId, PMR3Player.PMR3DispatchOnRep);
            channel.AddConnection(ownerConn);
            channel.AddConnection(otherConn);

            channel.Tick();

            Check(NoPropertyId(otherConn, manaId) && NoPropertyId(otherConn, energyId),
                "★首次 Update（全量基线）里 observer 也没有 mana/energy 属性（" + manaId + "/" + energyId + "）");
            Check(HasPropertyId(ownerConn, manaId) && HasPropertyId(ownerConn, energyId),
                "首次 Update 里 owner 拿到 mana/energy 属性");

            ownerConn.Sent.Clear();
            otherConn.Sent.Clear();

            // 资源变更 + 一条公共变更（HP/死亡）。
            Check(ds.PublishCombatState(5, 2, 55, 100, true, 99, 5, false, 0),
                "DS 侧资源变更写入成功（hp 55 / dead / mana 99 / energy 5）");
            channel.Tick();

            Check(NoPropertyId(otherConn, manaId) && NoPropertyId(otherConn, energyId),
                "★后续资源变更里 observer 依然拿不到 mana/energy（不泄）");
            Check(HasPropertyId(otherConn, hpId), "后续的公共 HP 变更 observer 拿到了（排除「整体漏发」）");
            Check(HasPropertyId(ownerConn, manaId) && HasPropertyId(ownerConn, energyId)
                  && HasPropertyId(ownerConn, hpId),
                "owner 同时拿到 mana/energy 与公共 HP");
            Check(HasSlot(otherConn, hpSlot), "observer 的 Update 记录里出现 HP 槽位（条件排除是定向的）");

            // 资源变更**不能**因为 owner-only 就把公共属性的脏位一起卡住（反向对照）。
            Check(channel.Stats.ConditionFiltered > 0,
                "复制层统计到条件过滤（ConditionFiltered = " + channel.Stats.ConditionFiltered + "）");

            bridge.Dispose();
        }

        /// <summary>把 Create 记录的声明式初值解成槽位表（格式：varint(slot)|varint(propId)|varint(len)|bytes…）。</summary>
        private static List<int> DecodeInitialSlots(byte[] lifecycleBatch)
        {
            List<int> slots = new List<int>();
            if (lifecycleBatch == null || lifecycleBatch.Length == 0)
            {
                Check(false, "生命周期批次为空（Create 初值不可解）");
                return slots;
            }

            List<PMNetLifecycleRecord> records = new List<PMNetLifecycleRecord>();
            string error;
            if (!PMLifecycleCodec.TryRead(lifecycleBatch, 0, lifecycleBatch.Length, records, out error)
                || records.Count != 1)
            {
                Check(false, "生命周期批次解析失败或记录数不为 1（" + (error ?? "count=" + records.Count) + "）");
                return slots;
            }

            byte[] repState = records[0].RepInitialState;
            if (repState == null || repState.Length == 0)
            {
                Check(false, "Create 记录没有声明式初值（RepInitialState 为空）");
                return slots;
            }

            PMNetReader r = new PMNetReader(repState);
            while (!r.IsAtEnd)
            {
                int slot = checked((int)r.ReadVarint());
                int propertyId = checked((int)r.ReadVarint());
                int len = checked((int)r.ReadVarint());
                r.ReadRawBytesCopy(len);

                if (propertyId == 0)
                {
                    Check(false, "初值槽位 " + slot + " 的属性 ID 为 0（无效）");
                    slots.Clear();
                    return slots;
                }

                slots.Add(slot);
            }

            return slots;
        }

        private static bool AnyPayloadHasPropertyId(TestConn conn, ushort propertyId)
        {
            for (int i = 0; i < conn.Sent.Count; i++)
            {
                PMRepMessage msg = DecodeReplication(conn.Sent[i]);
                if (msg == null) { continue; }

                for (int u = 0; u < msg.Updates.Count; u++)
                {
                    ushort[] ids = msg.Updates[u].PropertyIds;
                    if (ids == null) { continue; }

                    for (int k = 0; k < ids.Length; k++)
                    {
                        if (ids[k] == propertyId) { return true; }
                    }
                }
            }

            return false;
        }

        private static bool AnyPayloadHasSlot(TestConn conn, int slot)
        {
            for (int i = 0; i < conn.Sent.Count; i++)
            {
                PMRepMessage msg = DecodeReplication(conn.Sent[i]);
                if (msg == null) { continue; }

                for (int u = 0; u < msg.Updates.Count; u++)
                {
                    int[] slots = msg.Updates[u].Slots;
                    if (slots == null) { continue; }

                    for (int k = 0; k < slots.Length; k++)
                    {
                        if (slots[k] == slot) { return true; }
                    }
                }
            }

            return false;
        }

        private static bool HasPropertyId(TestConn conn, ushort propertyId)
        {
            return AnyPayloadHasPropertyId(conn, propertyId);
        }

        private static bool NoPropertyId(TestConn conn, ushort propertyId)
        {
            return !AnyPayloadHasPropertyId(conn, propertyId);
        }

        private static bool HasSlot(TestConn conn, int slot)
        {
            return AnyPayloadHasSlot(conn, slot);
        }

        private static PMRepMessage DecodeReplication(byte[] payload)
        {
            PMRepMessage msg = new PMRepMessage();
            string error;
            if (!PMReplicationReader.TryRead(new PMNetReader(payload), msg, out error))
            {
                Check(false, "复制载荷解码失败：" + error);
                return null;
            }

            return msg;
        }

        // =================================================================================
        //  C. 真实 Transport 双连接端到端（owner + observer）
        // =================================================================================

        private static void TestReplicationOverRealTransport()
        {
            PMNetRpcReceive.ResetStats();
            _createSnapshots.Clear();

            Rig rig = Rig.BuildServerWithTwoClients("r6-transport", 0x6B1u, 811, 812);
            Action<PMR3Player> onReplicated = OnPlayerReplicated;
            PMR3Runtime.PlayerReplicated += onReplicated;

            try
            {
                PMR3Player serverPlayer = PMR3Runtime.SpawnPlayer(rig.Server.World, rig.Server.Bridge,
                    rig.ServerViews[0].Connection);
                Check(serverPlayer != null, "DS 权威副本创建成功（owner = 连接 1）");
                if (serverPlayer == null) { return; }

                // 初值在**上线之前**就位：Create 记录现取，因此两条连接看到的初值不同（权限边界）。
                Check(serverPlayer.PublishCombatState(5, 2, 100, 100, false, 42, 17, false, 0),
                    "DS 侧写入战斗初值（mana 42 / energy 17 / hp 100）");
                CheckEq(serverPlayer.CombatMana, 42, "DS 侧自己的 CombatMana 已写入（只读 getter 反映真值）");

                rig.Frame(4);

                PMR3Player ownerCopy = FindClientPlayer(rig, serverPlayer, 0);
                PMR3Player observerCopy = FindClientPlayer(rig, serverPlayer, 1);
                Check(ownerCopy != null, "owner 客户端收到副本");
                Check(observerCopy != null, "observer 客户端也收到副本（两连接真实传输）");
                if (ownerCopy == null || observerCopy == null) { return; }

                // ── C1：Create 的初值（在 OnReplicatedCreate 当场读取的快照）───────────
                CreateSnapshot ownerSnapshot;
                CreateSnapshot otherSnapshot;
                Check(_createSnapshots.TryGetValue(rig.ClientEndpoints[0].World, out ownerSnapshot)
                      && ownerSnapshot.Count == 1, "owner 客户端副本创建回调恰好 1 次");
                Check(_createSnapshots.TryGetValue(rig.ClientEndpoints[1].World, out otherSnapshot)
                      && otherSnapshot.Count == 1, "observer 客户端副本创建回调恰好 1 次");

                if (ownerSnapshot != null && otherSnapshot != null)
                {
                    Check(ownerSnapshot.Mana == 42 && ownerSnapshot.Energy == 17,
                        "★owner 在 Create 回调里就读到 mana=42/energy=17（初值与创建原子到达）");
                    Check(otherSnapshot.Mana == 0 && otherSnapshot.Energy == 0,
                        "★observer 在 Create 回调里 mana=0/energy=0（OwnerOnly 初始 Create 未泄露）");
                    Check(ownerSnapshot.Hp == 100 && otherSnapshot.Hp == 100
                          && ownerSnapshot.HeroId == 5 && otherSnapshot.HeroId == 5
                          && ownerSnapshot.TeamId == 2 && otherSnapshot.TeamId == 2
                          && ownerSnapshot.MaxHp == 100 && otherSnapshot.MaxHp == 100,
                        "公共字段（hero/team/hp/maxHp）两条连接都拿到真值");
                }

                // ── C2：后续资源变更不泄 + 公共 HP/死亡全到 + OnRep 真派发 ──────────────
                FakeCombatDriver ownerDriver = new FakeCombatDriver();
                FakeCombatDriver otherDriver = new FakeCombatDriver();
                ownerCopy.CombatDriver = ownerDriver;
                observerCopy.CombatDriver = otherDriver;

                long ownerNotifyBefore = ownerCopy.CombatStateNotifyCount;
                long otherNotifyBefore = observerCopy.CombatStateNotifyCount;
                long onRepBefore = rig.ClientEndpoints[0].Bridge.Replication.Stats.OnRepDispatched;

                Check(serverPlayer.PublishCombatState(5, 2, 55, 100, true, 99, 5, false, 0),
                    "DS 侧写入资源变更 + 公共死亡（hp 55 / dead / mana 99 / energy 5）");
                rig.Frame(4);

                CheckEq(ownerCopy.CombatMana, 99, "★owner 副本收到 mana=99（资源变更到达 owner）");
                CheckEq(ownerCopy.CombatSuperEnergy, 5, "★owner 副本收到 energy=5");
                CheckEq(ownerCopy.CombatHp, 55, "owner 副本收到公共 HP=55");
                Check(ownerCopy.CombatDead, "owner 副本收到死亡标记");

                CheckEq(observerCopy.CombatMana, 0, "★observer 副本的 mana 仍为默认 0（后续资源变更不泄）");
                CheckEq(observerCopy.CombatSuperEnergy, 0, "★observer 副本的 energy 仍为默认 0");
                CheckEq(observerCopy.CombatHp, 55, "★observer 副本收到公共 HP=55（公共死亡全到）");
                Check(observerCopy.CombatDead, "★observer 副本收到死亡标记（不止 owner 能看到死亡）");

                Check(observerCopy.CombatHeroId == 5 && observerCopy.CombatTeamId == 2
                      && observerCopy.CombatMaxHp == 100,
                    "observer 副本的其它公共字段同步收敛");

                Check(ownerCopy.CombatStateNotifyCount > ownerNotifyBefore,
                    "owner 副本的 RepNotify 真的派发（+" + (ownerCopy.CombatStateNotifyCount - ownerNotifyBefore) + "）");
                Check(observerCopy.CombatStateNotifyCount > otherNotifyBefore,
                    "observer 副本的 RepNotify 也派发（公共属性，+" + (observerCopy.CombatStateNotifyCount - otherNotifyBefore) + "）");
                Check(ownerDriver.StateNotifyCount > 0,
                    "★owner 的 CombatDriver.OnCombatStateReplicated 被真的调用");
                Check(otherDriver.StateNotifyCount > 0,
                    "★observer 的 CombatDriver.OnCombatStateReplicated 也被调用（公共通道）");
                CheckEq(ownerCopy.CombatNotifyFailureCount, 0, "通知期间没有被隔离的异常（owner）");
                CheckEq(observerCopy.CombatNotifyFailureCount, 0, "通知期间没有被隔离的异常（observer）");
                Check(rig.ClientEndpoints[0].Bridge.Replication.Stats.OnRepDispatched > onRepBefore,
                    "复制层统计确认 OnRep 经真实 Transport 派发（OnRepDispatched 增长）");
                CheckEq(rig.ClientEndpoints[0].Bridge.Replication.Stats.OnRepUnhandled, 0,
                    "没有「声明了 OnRep 却没有分发器」的情况（接线完整）");
            }
            finally
            {
                PMR3Runtime.PlayerReplicated -= onReplicated;
                rig.Dispose();
            }
        }

        private static void OnPlayerReplicated(PMR3Player player)
        {
            if (player == null || player.World == null) { return; }

            CreateSnapshot snapshot;
            if (!_createSnapshots.TryGetValue(player.World, out snapshot))
            {
                snapshot = new CreateSnapshot();
                _createSnapshots[player.World] = snapshot;
            }

            snapshot.Count++;
            snapshot.HeroId = player.CombatHeroId;
            snapshot.TeamId = player.CombatTeamId;
            snapshot.Hp = player.CombatHp;
            snapshot.MaxHp = player.CombatMaxHp;
            snapshot.Dead = player.CombatDead;
            snapshot.Mana = player.CombatMana;
            snapshot.Energy = player.CombatSuperEnergy;
            snapshot.Ended = player.CombatMatchEnded;
            snapshot.Winner = player.CombatWinnerTeamId;
        }

        private sealed class CreateSnapshot
        {
            public int Count;
            public int HeroId;
            public int TeamId;
            public int Hp;
            public int MaxHp;
            public bool Dead;
            public int Mana;
            public int Energy;
            public bool Ended;
            public int Winner;
        }

        // =================================================================================
        //  D. RPC 归属与参数域
        // =================================================================================

        private static void TestRpcOwnershipAndParamDomain()
        {
            List<PMNetRpcReceiveResult> observed = new List<PMNetRpcReceiveResult>();
            PMNetRpcReceive.Observer = delegate(PMNetRpcReceiveResult r) { observed.Add(r); };
            PMNetRpcReceive.ResetStats();

            List<string> reported = new List<string>();
            PMRpcValidationSink.OnReported = delegate(PMNetObject t, ushort rpcId, PMRpcValidation verdict, string method)
            {
                if (verdict == PMRpcValidation.Reject) { reported.Add(method); }
            };

            Rig rig = Rig.BuildServerWithTwoClients("r6-rpc", 0x6C1u, 821, 822);
            try
            {
                PMR3Player serverPlayer = PMR3Runtime.SpawnPlayer(rig.Server.World, rig.Server.Bridge,
                    rig.ServerViews[0].Connection);
                Check(serverPlayer != null, "DS 权威副本创建成功");
                if (serverPlayer == null) { return; }

                FakeCombatDriver serverDriver = new FakeCombatDriver();
                serverPlayer.CombatDriver = serverDriver;

                rig.Frame(4);

                PMR3Player ownerCopy = FindClientPlayer(rig, serverPlayer, 0);
                PMR3Player observerCopy = FindClientPlayer(rig, serverPlayer, 1);
                Check(ownerCopy != null && observerCopy != null, "两条连接都收到副本");
                if (ownerCopy == null || observerCopy == null) { return; }

                // ── D1：owner 的上行攻击：参数原样到达 DS 驱动 ────────────────────────
                ownerCopy.ServerCombatAttackV1(0xA1u, true, 0.6f, -0.8f);
                rig.Frame(4);

                CheckEq(serverDriver.AttackCount, 1, "DS 驱动的 OnServerAttack 收到 1 条（owner）");
                Check(serverDriver.LastActivationId == 0xA1u && serverDriver.LastIsSuper
                      && Math.Abs(serverDriver.LastAimX - 0.6f) < 1e-6f
                      && Math.Abs(serverDriver.LastAimZ + 0.8f) < 1e-6f,
                    "攻击实参（activationId/isSuper/aimX/aimZ）逐字段原样到达（含 float 位域）");
                CheckEq(rig.ServerViews[0].Connection.RpcApplied, 1, "第一条连接应用了 1 条 RPC");
                CheckEq(rig.ServerViews[0].Connection.RpcRejected, 0, "第一条连接没有拒绝 RPC");

                // ── D2：observer 冒名上行 → 服务端按归属拒绝（实现不执行）──────────────
                long rejectsBefore = rig.ServerViews[1].Connection.RpcRejected;
                observerCopy.ServerCombatAttackV1(0xA2u, false, 0.1f, 0.1f);
                rig.Frame(6);

                CheckEq(serverDriver.AttackCount, 1, "★observer 的载荷没有到达驱动（实现未执行）");
                Check(rig.ServerViews[1].Connection.RpcRejected > rejectsBefore,
                    "服务端在第二条连接上拒绝了 RPC（RpcRejected 增长）");
                CheckEq(rig.ServerViews[1].Connection.RpcApplied, 0, "第二条连接应用过的 RPC 数保持 0");

                bool sawNotOwner = false;
                for (int i = 0; i < observed.Count; i++)
                {
                    if (observed[i].Status == PMRpcReceiveStatus.NotOwner) { sawNotOwner = true; break; }
                }

                Check(sawNotOwner, "接收入口给出 NotOwner 状态（失败可归因）");

                // ── D3：结果确认同样按归属（PMServerRpc + ForceValidate）───────────────
                observerCopy.ServerCombatResultAckV1(77u);
                rig.Frame(6);
                CheckEq(serverDriver.ResultAckCount, 0, "★observer 的结果确认没有到达驱动");
                CheckEq(serverDriver.LastOutcomeId, 0, "驱动未记录任何 outcomeId");

                ownerCopy.ServerCombatResultAckV1(77u);
                rig.Frame(6);
                CheckEq(serverDriver.ResultAckCount, 1, "owner 的结果确认到达驱动（对照）");
                CheckEq(serverDriver.LastOutcomeId, 77, "结果确认的 outcomeId 原样到达");

                // ── D4：参数域（ForceValidate 三态，在**业务实现之前**判定）────────────
                int driverBefore = serverDriver.AttackCount;
                long appliedBefore = rig.ServerViews[0].Connection.RpcApplied;

                ownerCopy.ServerCombatAttackV1(0u, false, 0.5f, 0.5f);
                rig.Frame(4);
                CheckEq(serverDriver.AttackCount, driverBefore, "activationId=0 被拒：实现未执行");

                ownerCopy.ServerCombatAttackV1(0xB1u, false, 0f, 0f);
                rig.Frame(4);
                CheckEq(serverDriver.AttackCount, driverBefore, "零方向被拒：实现未执行");

                ownerCopy.ServerCombatAttackV1(0xB2u, false, float.NaN, 0.5f);
                rig.Frame(4);
                CheckEq(serverDriver.AttackCount, driverBefore, "NaN 方向被拒：实现未执行");

                ownerCopy.ServerCombatAttackV1(0xB3u, false, 1.5f, 0f);
                rig.Frame(4);
                CheckEq(serverDriver.AttackCount, driverBefore, "越界方向（1.5 > 1.001）被拒：实现未执行");

                Check(reported.Contains("ServerCombatAttackV1"),
                    "参数域拒绝经 PMRpcValidationSink 上报（三态可观测，不是静默丢弃）");
                Check(rig.ServerViews[0].Connection.RpcApplied > appliedBefore,
                    "被拒的载荷仍算「已投递并被处置」（ForceValidate Reject 不断连）");
                CheckEq(rig.ServerViews[0].Connection.RpcDisconnectRequests, 0,
                    "ForceValidate Reject 没有请求断连（它是参数域拒绝，不是原生 Validate）");

                // 边界内：恰好 1.001 与非零小方向必须**放行**（证明上面拒的是越界而不是「一律拒」）。
                ownerCopy.ServerCombatAttackV1(0xC1u, false, 1.001f, -1.001f);
                rig.Frame(4);
                CheckEq(serverDriver.AttackCount, driverBefore + 1,
                    "边界 1.001 逐分量放行（Reject 是范围判定，不是一律拒）");
                Check(serverDriver.LastActivationId == 0xC1u, "边界放行的激活 ID 正确");

                ownerCopy.ServerCombatAttackV1(0xC2u, false, 1e-6f, 0f);
                rig.Frame(4);
                CheckEq(serverDriver.AttackCount, driverBefore + 2,
                    "非零极小方向放行（只拒零向量，不拒小值）");

                // ResultAck 的 0 哨兵。
                int ackBefore = serverDriver.ResultAckCount;
                ownerCopy.ServerCombatResultAckV1(0u);
                rig.Frame(4);
                CheckEq(serverDriver.ResultAckCount, ackBefore, "outcomeId=0 被拒：实现未执行");

                // ── D5：接收侧尾随字节必须在业务实现之前被判畸形 ──────────────────────
                PMNetWriter w = new PMNetWriter();
                w.WriteUInt32(0xD1u);
                w.WriteBool(false);
                w.WriteFloat(0.5f);
                w.WriteFloat(0.5f);
                w.WriteVarint(0UL); // 尾随字节
                byte[] malformed = w.ToArray();

                PMNetRpcReceiveResult result = PMNetRpcReceive.Deliver(
                    rig.Server.World, rig.ServerViews[0].Connection,
                    PMR3Player.PMGeneratedRpcId_ServerCombatAttackV1,
                    serverPlayer.NetId.Value, malformed, 0, malformed.Length);

                Check(result.Status == PMRpcReceiveStatus.Malformed,
                    "带尾随字节的攻击载荷判畸形（实际 " + result.Status + "）");
                CheckEq(serverDriver.AttackCount, driverBefore + 2, "畸形载荷没有到达驱动");
            }
            finally
            {
                PMNetRpcReceive.Observer = null;
                PMRpcValidationSink.OnReported = null;
                rig.Dispose();
            }
        }

        // =================================================================================
        //  E. null driver 的失败关闭
        // =================================================================================

        private static void TestNullDriverFailClosed()
        {
            List<string> reported = new List<string>();
            PMRpcValidationSink.OnReported = delegate(PMNetObject t, ushort rpcId, PMRpcValidation verdict, string method)
            {
                if (verdict == PMRpcValidation.Reject) { reported.Add(method); }
            };

            PMNetRpcReceive.ResetStats();

            Rig rig = Rig.BuildServerWithOneClient("r6-null-driver", 0x6D1u, 831);
            try
            {
                // 服务端玩家副本**故意不接驱动**。
                PMR3Player serverPlayer = PMR3Runtime.SpawnPlayer(rig.Server.World, rig.Server.Bridge,
                    rig.ServerViews[0].Connection);
                Check(serverPlayer != null, "DS 权威副本创建成功（未接 CombatDriver）");
                if (serverPlayer == null) { return; }
                Check(serverPlayer.CombatDriver == null, "前置：服务端副本没有 CombatDriver");

                rig.Frame(4);

                PMR3Player ownerCopy = FindClientPlayer(rig, serverPlayer, 0);
                Check(ownerCopy != null, "owner 客户端收到副本");
                if (ownerCopy == null) { return; }

                // E1：两条上行在 ForceValidate 里被拒（**不是**假成功、也不是静默丢弃）。
                long appliedBefore = rig.ServerViews[0].Connection.RpcApplied;
                ownerCopy.ServerCombatAttackV1(0xE1u, false, 0.5f, 0.5f);
                ownerCopy.ServerCombatResultAckV1(9u);
                rig.Frame(6);

                Check(reported.Contains("ServerCombatAttackV1"),
                    "无 driver 时上行攻击被 ForceValidate 判 Reject（三态上报可观测）");
                Check(reported.Contains("ServerCombatResultAckV1"),
                    "无 driver 时上行结果确认同样被 Reject");
                Check(rig.ServerViews[0].Connection.RpcApplied > appliedBefore,
                    "Reject 仍算「已投递并被处置」（ForceValidate 的 Reject 语义：不断连）");
                CheckEq(rig.ServerViews[0].Connection.RpcDisconnectRequests, 0,
                    "ForceValidate Reject 没有请求断连");

                // E2：下行无 driver ⇒ 可观察丢弃（计数增长，不静默吞掉）。
                Check(ownerCopy.CombatDriver == null, "前置：owner 副本没有 CombatDriver");
                serverPlayer.ClientCombatAttackResultV1(0xE2u, false, 7);
                serverPlayer.ClientCombatMatchResultV1(0xE3u, 2);
                rig.Frame(4);

                CheckEq(ownerCopy.CombatAttackResultAppliedCount, 0, "无 driver 时下行攻击裁决没有被应用");
                CheckEq(ownerCopy.CombatAttackResultDiscardedCount, 1,
                    "无 driver 时下行攻击裁决计入可观察丢弃（不是静默忽略）");
                CheckEq(ownerCopy.CombatMatchResultAppliedCount, 0, "无 driver 时下行比赛结果没有被应用");
                CheckEq(ownerCopy.CombatMatchResultDiscardedCount, 1,
                    "无 driver 时下行比赛结果计入可观察丢弃");

                // 对照：接上 driver 后同两条下行正常投递。
                FakeCombatDriver clientDriver = new FakeCombatDriver();
                ownerCopy.CombatDriver = clientDriver;

                serverPlayer.ClientCombatAttackResultV1(0xE4u, true, 0);
                serverPlayer.ClientCombatMatchResultV1(0xE5u, 1);
                rig.Frame(4);

                CheckEq(clientDriver.AttackResultCount, 1, "接上 driver 后下行攻击裁决到达驱动");
                CheckEq(clientDriver.MatchResultCount, 1, "接上 driver 后下行比赛结果到达驱动");
                CheckEq(clientDriver.LastActivationId, 0xE4u, "攻击裁决的 activationId 原样到达");
                Check(clientDriver.LastAccepted, "攻击裁决的 accepted 原样到达");
                CheckEq(clientDriver.LastOutcomeId, 0xE5u, "比赛结果的 outcomeId 原样到达");
                CheckEq(clientDriver.LastWinnerTeamId, 1, "比赛结果的 winnerTeamId 原样到达（名册真实队伍号）");
                CheckEq(ownerCopy.CombatAttackResultAppliedCount, 1, "接上 driver 后 applied 计数增长");
                CheckEq(ownerCopy.CombatAttackResultDiscardedCount, 1, "丢弃计数不再增长");
                CheckEq(ownerCopy.CombatMatchResultAppliedCount, 1, "比赛结果 applied 计数增长");
                CheckEq(ownerCopy.CombatMatchResultDiscardedCount, 1, "比赛结果丢弃计数不再增长");

                // E3：DS 侧驱动只做「入队」，声明层不代替它发送任何东西 ——
                // 断言 DS 侧没有被下行 RPC 反灌（CombatAttackResult* 只应发生在客户端副本上）。
                CheckEq(serverPlayer.CombatAttackResultAppliedCount, 0, "DS 权威副本没有应用下行裁决（方向正确）");
                CheckEq(serverPlayer.CombatMatchResultAppliedCount, 0, "DS 权威副本没有应用下行比赛结果");
            }
            finally
            {
                PMRpcValidationSink.OnReported = null;
                rig.Dispose();
            }
        }

        // =================================================================================
        //  F. PublishCombatState 的 World role 权限
        // =================================================================================

        private static void TestPublishPermissionByWorldRole()
        {
            _combatWarnings.Clear();

            Rig rig = Rig.BuildServerWithTwoClients("r6-publish", 0x6E1u, 841, 842);
            try
            {
                Check(rig.Server.World.IsServer, "DS 世界角色 = 权威（IsServer）");
                Check(!rig.ClientEndpoints[0].World.IsServer, "客户端 0 世界角色 = 非权威（AP）");

                PMR3Player serverPlayer = PMR3Runtime.SpawnPlayer(rig.Server.World, rig.Server.Bridge,
                    rig.ServerViews[0].Connection);
                Check(serverPlayer != null, "DS 权威副本创建成功");
                if (serverPlayer == null) { return; }

                rig.Frame(4);

                PMR3Player ownerCopy = FindClientPlayer(rig, serverPlayer, 0);
                Check(ownerCopy != null, "owner 客户端收到副本");
                if (ownerCopy == null) { return; }

                // F1：DS 世界放行（唯一合法写入方）。
                bool dsOk = serverPlayer.PublishCombatState(7, 3, 120, 120, false, 60, 30, false, 0);
                Check(dsOk, "★DS（权威世界）侧 PublishCombatState 放行");
                Check(serverPlayer.CombatHeroId == 7 && serverPlayer.CombatTeamId == 3
                      && serverPlayer.CombatHp == 120 && serverPlayer.CombatMaxHp == 120
                      && serverPlayer.CombatMana == 60 && serverPlayer.CombatSuperEnergy == 30,
                    "DS 侧的九条复制字段都写入了（经生成的 Setter 标脏）");
                CheckEq(serverPlayer.CombatPublishCount, 1, "DS 侧写入计数 +1");
                CheckEq(serverPlayer.CombatPublishRejectedCount, 0, "DS 侧没有被拒");

                // F2：AP（非权威世界）副本必须被拒，且值保持不变（AP 不写复制字段）。
                long ownerManaBefore = ownerCopy.CombatMana;
                long ownerHpBefore = ownerCopy.CombatHp;
                int rejectBefore = _combatWarnings.Count;

                bool apOk = ownerCopy.PublishCombatState(19, 19, 1, 1, true, 999, 999, true, 19);

                Check(!apOk, "★AP（非权威世界）副本的 PublishCombatState 被拒（返回 false）");
                CheckEq(ownerCopy.CombatPublishRejectedCount, 1, "AP 侧拒绝计数 +1（可观察）");
                CheckEq(ownerCopy.CombatPublishCount, 0, "AP 侧成功写入计数保持 0");
                CheckEq(ownerCopy.CombatMana, ownerManaBefore, "AP 侧 mana 未被改写");
                CheckEq(ownerCopy.CombatHp, ownerHpBefore, "AP 侧 HP 未被改写");
                Check(_combatWarnings.Count > rejectBefore, "AP 侧被拒时发出了可观察告警");

                // F3：AP 的越权写入不会传播回 DS / 其它连接。
                rig.Frame(4);
                Check(serverPlayer.CombatHeroId == 7 && serverPlayer.CombatMana == 60,
                    "DS 权威状态未被 AP 的尝试影响");
                CheckEq(ownerCopy.CombatHeroId, 7, "owner 副本仍为 DS 真值（AP 尝试没有落到线上）");

                PMR3Player observerCopy = FindClientPlayer(rig, serverPlayer, 1);
                Check(observerCopy != null && observerCopy.CombatHeroId == 7,
                    "observer 副本同样只看到 DS 真值");
                Check(observerCopy != null && observerCopy.CombatMana == 0,
                    "observer 副本的 mana 始终未被写入（owner-only + 只有 DS 能写）");
            }
            finally
            {
                rig.Dispose();
            }
        }

        // =================================================================================
        //  G. 边界与清理
        // =================================================================================

        private static void TestBoundariesAndCleanup()
        {
            string root = ResolveRepoRoot();
            if (root == null)
            {
                Check(false, "定位仓库根目录失败（生成文件集合无法核对）");
                return;
            }

            // G1：生成集合唯一 —— 同一目录、同一份锁、恰好三个产物（不另造 registry）。
            string outDir = Path.Combine(root, "Client", "Assets", "Scripts", "PMR3", "Generated");
            string[] files = Directory.GetFiles(outDir, "*.cs");
            Array.Sort(files);
            CheckEq(files.Length, 3, "生成目录里恰好三个 .cs 产物（2 个类 + 1 个 registry）");
            string joined = string.Join("|", files);
            Check(joined.Contains("PMNet.PMNet.R3.PMR3Player.g.cs")
                  && joined.Contains("PMNet.PMNet.R3.PMR5Projectile.g.cs")
                  && joined.Contains("PMNetGeneratedRegistry.g.cs"),
                "产物集合与 R5 相同（R6-A2 只追加 PMR3Player 声明，未新增 runtime .cs / meta）");
            Check(Directory.GetFiles(outDir, "*.meta").Length == 3,
                "生成产物的 .meta 仍是三份（没有多出未纳管的文件）");

            // G2：Detach / Shutdown 幂等，且不留静态订阅。
            Rig rig = Rig.BuildServerWithOneClient("r6-cleanup", 0x6F1u, 851);
            PMR3Runtime.Attach(rig.Server.World, rig.Server.Bridge);
            Check(PMR3Runtime.IsAttached(rig.Server.World), "重复 Attach 幂等且世界仍接线");

            rig.Dispose();
            Check(!PMR3Runtime.IsAttached(rig.Server.World), "Detach 之后世界已解除接线");

            PMR3Runtime.Shutdown();
            Check(!PMR3Runtime.HasPlayerReplicatedHandlers && !PMR3Runtime.HasPlayerSpawnedHandlers,
                "Shutdown 清掉静态事件订阅（不留上一次宿主的强引用）");
            PMR3Runtime.Shutdown();
            Check(true, "Shutdown 重复调用幂等（不抛异常）");

            // 复位成后续门禁可用的状态。
            PMNetRegistry.Reset();
            PMR3Runtime.Register();
            CheckEq(PMR3Runtime.ProtocolHash, NewGlobalProtocolHash, "复位后协议摘要仍为 R6-A2 冻结值");
        }

        // =================================================================================
        //  测试替身
        // =================================================================================

        /// <summary>
        /// 假的战斗驱动：只记录收到的调用（契约允许测试用 fake interface handler）。
        /// 它**不**做任何授权/结算/解码 —— 那是 PMCombat 核心与 R6 driver 的职责，本批次不验收那部分。
        /// </summary>
        private sealed class FakeCombatDriver : IPMCombatNetworkDriver
        {
            public int StateNotifyCount;
            public int AttackCount;
            public int AttackResultCount;
            public int MatchResultCount;
            public int ResultAckCount;

            public uint LastActivationId;
            public bool LastIsSuper;
            public float LastAimX;
            public float LastAimZ;
            public bool LastAccepted;
            public int LastReason;
            public uint LastOutcomeId;
            public int LastWinnerTeamId;

            public void OnCombatStateReplicated() { StateNotifyCount++; }

            public void OnServerAttack(uint activationId, bool isSuper, float aimX, float aimZ)
            {
                AttackCount++;
                LastActivationId = activationId;
                LastIsSuper = isSuper;
                LastAimX = aimX;
                LastAimZ = aimZ;
            }

            public void OnClientAttackResult(uint activationId, bool accepted, int reason)
            {
                AttackResultCount++;
                LastActivationId = activationId;
                LastAccepted = accepted;
                LastReason = reason;
            }

            public void OnClientMatchResult(uint outcomeId, int winnerTeamId)
            {
                MatchResultCount++;
                LastOutcomeId = outcomeId;
                LastWinnerTeamId = winnerTeamId;
            }

            public void OnServerResultAck(uint outcomeId)
            {
                ResultAckCount++;
                LastOutcomeId = outcomeId;
            }
        }

        /// <summary>
        /// 测试连接：记录发出去的**复制/RPC 载荷**并实现断连接口。
        /// 它的存在让「OwnerOnly 到底发了哪几个槽位」可以被逐字节判定，而不是靠读对象字段猜。
        /// </summary>
        private sealed class TestConn : PMNetConnection, IPMNetRpcDisconnectTarget
        {
            public readonly List<byte[]> Sent = new List<byte[]>();
            public readonly List<string> DisconnectRequests = new List<string>();

            public TestConn(int id, bool serverSide)
                : base(id, serverSide)
            {
            }

            public override bool IsReady { get { return true; } }

            public override void Send(byte[] payload, PMRpcReliability reliability)
            {
                Sent.Add(payload);
            }

            public void RequestRpcDisconnect(PMNetObject target, ushort rpcId, string methodName)
            {
                DisconnectRequests.Add(methodName);
            }
        }

        // =================================================================================
        //  手工链路 / 端点 / 场景（与 PMR3RuntimeTest / PMR5DeclarationTest 同一手法：真实 PMTransport 字节链）
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
        /// 因此「非 owner 被拒」「OwnerOnly 只发给 owner」才有意义。
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

            public static Rig BuildServerWithOneClient(string tag, uint epoch, int uid)
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
                identity.MatchId = "match-r6-declaration-test";
                identity.DsId = "ds-r6-declaration-test";

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
                    Check(ServerViews[i].Connection.TryActivate(out error),
                        ServerViews[i].Name + " 连接激活成功（" + (error ?? "ok") + "）");
                }

                for (int i = 0; i < ClientConnections.Count; i++)
                {
                    string error;
                    Check(ClientConnections[i].Connection.TryActivate(out error),
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

        private static PMR3Player FindClientPlayer(Rig rig, PMR3Player serverPlayer, int clientIndex)
        {
            PMNetObject obj;
            if (!rig.ClientEndpoints[clientIndex].World.TryFind(serverPlayer.NetId, out obj) || obj == null)
            {
                return null;
            }

            return obj as PMR3Player;
        }

        private static string SlotsToText(List<int> slots)
        {
            if (slots == null || slots.Count == 0) { return "[]"; }

            StringBuilder sb = new StringBuilder("[");
            for (int i = 0; i < slots.Count; i++)
            {
                if (i > 0) { sb.Append(','); }
                sb.Append(slots[i]);
            }

            return sb.Append(']').ToString();
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
    }
}
