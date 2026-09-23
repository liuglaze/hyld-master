// PMPropertyWeaverTest —— auto-property 复制属性编织的**独立**门禁（T-P2）。
//
// 契约：Docs/plans/net-property-authoring-contract.md（§2 冻结生成接口 / §5 T-P2）
//
// 门禁链条（每一步都是真实的，不是对谓词的纯函数断言）：
//   [1] 真实编译 PMNet 运行时（netstandard2.0 / C# 7.3）；
//   [2] 真实 PMNetGen --decl-gen 扫**整份夹具源码**（属性声明 + 业务 + 驱动 + RPC + 混合类），
//       产出全部生成物 —— 夹具里**没有任何手写生成物**（RawSet/PropertySet/槽位常量/Reader/
//       guard/BuildEntry/注册表/OnRep 分发表都由生成器产出）。缺 helper 即构建/weave 失败，
//       绝不用字符串模板补出来；
//   [3] 生成物 + 夹具源码编进**同一个** netstandard2.0 DLL（Debug / Release、均带 portable PDB）；
//   [4] 未编织负例：--check 必须失败、new 与注册入口（PMNet_BuildEntry）必须被 guard 拒绝；
//   [5] --weave（真实 CLI 进程）后：`Hp = x` / `Hp -= x` 这类**普通 C# 赋值**必须真的存值、
//       并在「变化 + 权威 + PushBased」时标脏；客户端不标脏；收包 Reader 只走 RawSet 不标脏；
//       原始 setter 的 `[MethodImpl]` 标志必须保留、**RawSet 必须继承同一批标志**（否则收包写丢锁），
//       并用「另一线程持锁 ⇒ 收包路径阻塞 / 无同步属性对照不阻塞」做行为证据；
//       同引用重赋 / 原地改元素不承诺标脏；初始化器原值保留；
//   [6] --check 零写；二次 --weave 幂等零 diff（DLL + PDB 逐字节不变）；
//   [7] **真实复制链**（PMNetWorld + PMReplicationChannel）：字节往返 / 初始全量 / ACK 推进基线 /
//       变化在 ACK 后仍重发 / OwnerOnly 只发给拥有者 / 收包应用不标脏 / 真复制层提交后 OnRep 恰好一次 /
//       无变化不发 / PushBased=false 在脏位为空时仍被轮询发出。OnRep 由真实通道的提交路径派发，
//       不靠反射直接调分发表冒充复制层（隔离低层用例仍保留）；
//   [8] **纯属性零 RPC** 场景：切出独立源码集，**真实生成独立集合**（不含任何 PMGeneratedRpcId_），
//       --weave 必须真的编织属性（不是 noop），--require-rpcs 语义不变（仍要求 RPC）；
//   [9] **同类 RPC + 属性**：同一个类在一次预检后用同一个 stamp 同时织好两侧（版本门 + 私有 RPC 业务体 +
//       属性赋值标脏），并且属性侧负例失败时**零写**（RPC 侧不会被留下半成品）；
//  [10] 负例族：损坏 setter / RawSet 不是 stub / RawSet 写错 backing field / helper 无权限 /
//       helper 错误槽位 / helper 不判变化 / Reader 走普通 setter / Reader 先普通 setter 再 RawSet /
//      缺 helper / 缺注册槽位 / PushBased 分歧 / **缺实例 gate** / **缺注册入口 guard** /
//       混合类属性槽位串位 / **收包 RawSet 丢 setter 实现标志（旧版产物形态）**
//       —— 全部必须明确失败且**文件 hash 不变**；
//  [11] 真实执行：AssemblyLoadContext 加载编织后的夹具 → 跑完整行为链（赋值 / 锁 / 复制链）。
//
// 负向注入的边界：属性区块的负例只注入 %TEMP% 沙箱里**真实生成产物副本**（生成物层）或
// **夹具源码副本**（仅"声明形态"类，如自定义访问器）；仓库里的夹具与生成器一行不动。
// 注不进（锚点不在）就当场报失败，不静默跳过。
//
// 退出码：0 = 全部通过；1 = 否则。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;

namespace PMPropertyWeaverTest
{
    internal static class Program
    {
        private static int _passed;
        private static readonly List<string> _failures = new List<string>();
        private static int _weaverInvocations;
        private static int _fixtureBuilds;
        private static int _loadSerial;

        private static string _weaverDllPath;
        private static string _genDllPath;
        private static string _workDir;

        private static void Check(bool condition, string what)
        {
            if (condition)
            {
                _passed++;
                return;
            }

            _failures.Add(what);
            Console.WriteLine("  [FAIL] " + what);
        }

        private static void Section(string title, Action body)
        {
            Console.WriteLine();
            Console.WriteLine("=== " + title + " ===");
            try
            {
                body();
            }
            catch (Exception ex)
            {
                _failures.Add(title + " 抛出未预期异常：" + ex.GetType().Name + "：" + ex.Message);
                Console.WriteLine("  [FAIL] " + title + " 抛出未预期异常：" + ex);
            }
        }

        // =====================================================================================
        //  用例清单（防止用例被静默删掉）
        // =====================================================================================

        private static readonly string[] PreWeaveCases = new string[]
        {
            "preweave-version-zero", "preweave-new-blocked", "preweave-buildentry-blocked",
        };

        private static readonly string[] WovenCases = new string[]
        {
            "stamp-is-one", "initializer-preserved",
            "authority-dirty", "authority-same-value-not-dirty", "compound-assignment-dirty",
            "client-not-dirty", "rawset-correct-field-and-slot", "pushbased-false-not-dirty",
            "array-whole-ref-dirty", "array-new-ref-same-content-dirty",
            "array-same-ref-not-dirty", "array-inplace-not-dirty",
            "reader-apply-not-dirty", "setter-and-reader-no-onrep", "onrep-dispatch-once",
            "legacy-setter-forwards-and-pushes", "registration-conditions",
            // 实现标志：原 setter 的 [MethodImpl] 必须保留，RawSet 必须继承
            "locked-setter-impl-flags-preserved", "rawset-inherits-setter-impl-flags",
            "plain-rawset-not-synchronized",
            // 行为证据：监视器锁确实被持有 / 释放，且收包路径与同步 setter 共用同一把锁
            "monitor-hold-observable", "monitor-released-after-probe",
            "rawset-locked-blocks-while-monitor-held", "locked-setter-shares-monitor",
            "rawset-unlocked-not-blocked",
        };

        private static readonly string[] ReplicationCases = new string[]
        {
            "rep-initial-full-state",
            "rep-owner-only-sent-to-owner",
            "rep-owner-only-not-sent-to-nonowner",
            "rep-reader-apply-not-dirty",
            "rep-onrep-dispatched-once",
            "rep-no-repeat-without-change",
            "rep-ack-advances-baseline",
            "rep-change-after-ack-resent",
            "rep-poll-after-ack-without-dirty",
        };

        private static readonly string[] MixedClassCases = new string[]
        {
            "mixed-stamp-is-one", "mixed-rpc-body-woven", "mixed-property-woven-same-pass",
        };

        // =====================================================================================
        //  入口
        // =====================================================================================

        private static int Main(string[] args)
        {
            Console.WriteLine("=== PMPropertyWeaver 门禁（真实生成 + 真实编译 + 真实编织 + 真实赋值/复制链执行） ===");

            string repoRoot = ResolveRepoRoot(args);
            Console.WriteLine("仓库根目录：" + repoRoot);

            // 每次运行一个**唯一**沙箱目录（不覆盖上一次的失败现场）。
            _workDir = Path.Combine(
                Path.GetTempPath(),
                "pmproperty-weaver-test-"
                + Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "-"
                + DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));

            Directory.CreateDirectory(_workDir);
            Console.WriteLine("临时工作目录：" + _workDir);

            string runtimeDir = Path.Combine(_workDir, "runtime");
            string mixedGenDir = Path.Combine(_workDir, "gen-mixed");
            string mixedIdLock = Path.Combine(_workDir, "ids-mixed.json");

            _genDllPath = LocateToolDll(repoRoot, "PMNetGen");
            _weaverDllPath = LocateToolDll(repoRoot, "PMNetWeaver");
            Console.WriteLine("PMNetGen   : " + _genDllPath);
            Console.WriteLine("PMNetWeaver: " + _weaverDllPath);

            string fixturePath = Path.Combine(repoRoot, "Tools", "PMPropertyWeaverTest", "Fixture.cs");
            string fixtureText = File.Exists(fixturePath) ? File.ReadAllText(fixturePath) : null;

            Section("0. 环境：夹具源码（无手写生成物）与工具就位", delegate ()
            {
                Check(fixtureText != null, "夹具声明源存在：" + fixturePath);
                Check(File.Exists(_genDllPath), "PMNetGen.dll 存在：" + _genDllPath);
                Check(File.Exists(_weaverDllPath), "PMNetWeaver.dll 存在：" + _weaverDllPath);

                Check(Text(fixtureText).Contains(PropertyRegionBegin), "夹具带纯属性区块起始标记");
                Check(Text(fixtureText).Contains(PropertyRegionEnd), "夹具带纯属性区块结束标记");

                // ★ 最终验收：夹具里不得再有任何手写生成物。
                //   这一段是"回归到旧形态"的硬门 —— 手写生成物一旦回来，本组再跑绿也不算通过。
                Check(!Text(fixtureText).Contains("private void PMNet_PropertyRawSet_"),
                    "夹具不含手写的 PMNet_PropertyRawSet_ helper（必须由真实生成器产出）");
                Check(!Text(fixtureText).Contains("private void PMNet_PropertySet_"),
                    "夹具不含手写的 PMNet_PropertySet_ helper（必须由真实生成器产出）");
                Check(!Text(fixtureText).Contains("public const int PMGeneratedPropertyIndex_"),
                    "夹具不含手写槽位常量 PMGeneratedPropertyIndex_（必须由真实生成器产出）");
                Check(!Text(fixtureText).Contains("private static void PMNet_Read_"),
                    "夹具不含手写收包 Reader PMNet_Read_（必须由真实生成器产出）");
                Check(!Text(fixtureText).Contains("internal static PMNet.PMNetClassEntry PMNet_BuildEntry()"),
                    "夹具不含手写注册入口 PMNet_BuildEntry（必须由真实生成器产出）");
                Check(!Text(fixtureText).Contains("private static int PMNet_RequireRpcWeave"),
                    "夹具不含手写编织 guard PMNet_RequireRpcWeave（必须由真实生成器产出）");
                Check(!Text(fixtureText).Contains("protected override void CollectLifetimeReplicatedProps"),
                    "夹具不含手写复制属性注册 CollectLifetimeReplicatedProps（必须由真实生成器产出）");
                Check(!Text(fixtureText).Contains("internal static int PMNet_GetRpcWeaveVersion"),
                    "夹具不含手写版本门 PMNet_GetRpcWeaveVersion（必须由真实生成器产出）");
                Check(!Text(fixtureText).Contains("private static void PMNet_OnRepDispatch"),
                    "夹具不含手写 OnRep 分发表 PMNet_OnRepDispatch（必须由真实生成器产出）");
            });

            if (_failures.Count > 0)
            {
                return Summarize();
            }

            // 纯属性区块：只用于给"纯属性零 RPC"场景切出一份**独立的真实生成输入**。
            // 它同样只有声明 / 业务 / 驱动，没有生成物。
            string propertyRegion = ExtractRegion(fixtureText, PropertyRegionBegin, PropertyRegionEnd);

            Section("1. 真实 PMNetGen 生成（--decl-gen，整份夹具源码）", delegate ()
            {
                string genInputPath = Path.Combine(_workDir, "FixtureGenInput.cs");
                File.WriteAllText(genInputPath, fixtureText, new UTF8Encoding(true));

                ProcessResult gen = RunProcess(
                    DotnetHost(),
                    new string[] { _genDllPath, "--decl-gen", genInputPath, "--out-dir", mixedGenDir, "--id-lock", mixedIdLock },
                    _workDir);

                Check(gen.ExitCode == 0, "PMNetGen --decl-gen 退出码 = " + gen.ExitCode + "\n" + gen.Combined());
                Check(File.Exists(mixedIdLock), "ID 锁文件已写出：" + mixedIdLock);

                string[] files = Directory.Exists(mixedGenDir) ? Directory.GetFiles(mixedGenDir, "*.cs") : new string[0];
                Check(files.Length == 4, "生成物文件数 = " + files.Length + "（期望 4：3 个类 + 注册表）");

                string propFile = FindGenerated(files, "PMWeave.Property.PropFixture.g.cs");
                string mixedFile = FindGenerated(files, "PMWeave.Mixed.MixedFixture.g.cs");
                string registryFile = FindGenerated(files, "PMNetGeneratedRegistry.g.cs");
                Check(propFile != null, "找到属性夹具类生成物");
                Check(mixedFile != null, "找到混合类（RPC + 属性）生成物");
                Check(registryFile != null, "找到程序集注册表生成物");
                Check(files.Any(f => Path.GetFileName(f).EndsWith("PMWeave.Rpc.RpcFixture.g.cs", StringComparison.Ordinal)),
                    "找到 RPC 夹具类生成物");

                if (propFile == null || mixedFile == null || registryFile == null)
                {
                    return;
                }

                string propText = Text(File.ReadAllText(propFile));
                string mixedText = Text(File.ReadAllText(mixedFile));
                string registryText = Text(File.ReadAllText(registryFile));

                // ── 属性夹具：生成物必须自带契约 §2 的四样，且 Reader 只走 RawSet ──
                Check(propText.Contains("public const int PMGeneratedPropertyIndex_Hp"),
                    "属性夹具生成物含槽位常量 PMGeneratedPropertyIndex_Hp");
                Check(propText.Contains("private void PMNet_PropertyRawSet_Hp(int value)"),
                    "属性夹具生成物含 RawSet helper（编织前是抛异常 stub）");
                Check(propText.Contains("private void PMNet_PropertySet_Hp(int value)"),
                    "属性夹具生成物含赋值 helper PMNet_PropertySet_Hp");
                Check(propText.Contains("self.PMNet_PropertyRawSet_Hp(pmValue);"),
                    "属性夹具生成物的收包 Reader 直接调 RawSet");
                Check(!propText.Contains("self.Hp = pmValue;"),
                    "属性夹具生成物的收包 Reader 不写普通 setter");
                Check(!propText.Contains("PMGeneratedRpcId_"),
                    "属性夹具生成物不含任何 RPC 声明（该类没有 RPC）");
                Check(propText.Contains("PMNet_RequireRpcWeave"),
                    "生成物含编织 guard（真实生成物，不是手写补丁）");
                Check(registryText.Contains("PMWeave.Property.PropFixture.PMNet_BuildEntry()"),
                    "注册表显式列出属性夹具类（禁止反射扫描）");

                // ── 混合类：同一个生成物里既有一组属性 helper 又有 RPC 生成物 ──
                Check(mixedText.Contains("PMGeneratedRpcId_ServerMixed"),
                    "混合类生成物含 RPC 声明（PMGeneratedRpcId_ServerMixed）");
                Check(mixedText.Contains("private void PMNet_PropertyRawSet_Value(int value)"),
                    "混合类生成物含属性 helper（同一份生成物）");
            });

            if (_failures.Count > 0)
            {
                return Summarize();
            }

            Section("2. 真实编译 PMNet 运行时（netstandard2.0 / C# 7.3）", delegate ()
            {
                BuildRuntime(repoRoot, _workDir, runtimeDir);
                Check(File.Exists(Path.Combine(runtimeDir, "PMNet.Runtime.Temp.dll")), "运行时程序集已生成");
            });

            if (_failures.Count > 0)
            {
                return Summarize();
            }

            Dictionary<string, string> mixedGenerated = LoadGenerated(mixedGenDir);

            for (int c = 0; c < 2; c++)
            {
                string configuration = c == 0 ? "Debug" : "Release";
                RunMixedChain(_workDir, runtimeDir, fixtureText, mixedGenerated, configuration);
            }

            RunPropertyOnlyChain(_workDir, runtimeDir, propertyRegion);
            RunMixedClassChain(_workDir, runtimeDir, fixtureText, mixedGenerated);
            RunNegativeChains(_workDir, runtimeDir, fixtureText, mixedGenerated);
            RunRawSetWrongFieldChain(_workDir, runtimeDir, fixtureText, mixedGenerated);
            RunImplFlagTamperChain(_workDir, runtimeDir, fixtureText, mixedGenerated);

            return Summarize();
        }

        private static int Summarize()
        {
            Console.WriteLine();
            Console.WriteLine("=== 汇总 ===");
            Console.WriteLine("通过 " + _passed + " 项，失败 " + _failures.Count + " 项");
            Console.WriteLine("PMNetWeaver CLI 调用次数：" + _weaverInvocations + "；夹具真实编译次数：" + _fixtureBuilds);

            for (int i = 0; i < _failures.Count; i++)
            {
                Console.WriteLine("  - " + _failures[i]);
            }

            if (_passed > 0 && _failures.Count == 0)
            {
                Console.WriteLine("结果：PASS");
                Console.WriteLine("沙箱目录（通过后清理）：" + _workDir);
                TryDeleteWorkDir();
                return 0;
            }

            Console.WriteLine("结果：FAIL");
            Console.WriteLine("失败现场保留在：" + _workDir);
            return 1;
        }

        private static void TryDeleteWorkDir()
        {
            try
            {
                if (string.IsNullOrEmpty(_workDir) || !Directory.Exists(_workDir))
                {
                    return;
                }

                // AssemblyLoadContext 的卸载是**异步**的：不推一把终结器、不多试几次，
                // 已加载夹具 DLL 的文件句柄往往还没释放（删除会被拒）。
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        if (!Directory.Exists(_workDir))
                        {
                            return;
                        }

                        Directory.Delete(_workDir, true);
                        Console.WriteLine("沙箱已清理。");
                        return;
                    }
                    catch (IOException)
                    {
                        // 句柄未释放，再试
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // 句柄未释放，再试
                    }

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    System.Threading.Thread.Sleep(200);
                }

                Console.WriteLine("  [info] 沙箱未能立即删除（卸载与句柄释放是异步的）——不影响结论；"
                                  + "保留为本次证据，可手动删除：" + _workDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [info] 沙箱未能立即删除（卸载是异步的，句柄可能仍在）——不影响结论；"
                                  + "可手动删除：" + _workDir + "（" + ex.GetType().Name + "）");
            }
        }

        // =====================================================================================
        //  正向链条：RPC + auto-property 同程序集（真实生成物）
        // =====================================================================================

        private static void RunMixedChain(
            string workDir,
            string runtimeDir,
            string fixtureText,
            Dictionary<string, string> generated,
            string configuration)
        {
            string variantDir = Path.Combine(workDir, "mixed-" + configuration);
            string outDir = Path.Combine(variantDir, "out");

            Section("3[" + configuration + "]. 真实编译（真实生成物 + 夹具源码）", delegate ()
            {
                WriteVariant(variantDir, runtimeDir, fixtureText, generated);
                BuildProject(Path.Combine(variantDir, "fixture.csproj"), outDir, configuration, variantDir);

                Check(File.Exists(Path.Combine(outDir, "PMPropFixture.dll")), "夹具 DLL 已生成");
                Check(File.Exists(Path.Combine(outDir, "PMPropFixture.pdb")), "夹具 PDB 已生成（portable）");
            });

            if (_failures.Count > 0)
            {
                return;
            }

            string dll = Path.Combine(outDir, "PMPropFixture.dll");
            string pdb = Path.Combine(outDir, "PMPropFixture.pdb");

            Section("4[" + configuration + "]. 未编织负例：stamp=0、new / 注册入口必须被拒", delegate ()
            {
                string[] lines = RunDriver(dll, runtimeDir, "PMWeave.Property.PropertyDriver", "RunPreWeave");
                CheckDriver(lines, PreWeaveCases, "preweave[" + configuration + "]");

                ProcessResult check = RunWeaver(new string[] { "--check", dll });
                Check(check.ExitCode != 0, "--check 在未编织程序集上必须失败（实际 " + check.ExitCode + "）");
            });

            Section("5[" + configuration + "]. 真实编织：--weave 必须真的改写属性与 RPC", delegate ()
            {
                string beforeDll = Sha256(dll);
                string beforePdb = Sha256(pdb);

                ProcessResult weave = RunWeaver(new string[] { "--weave", dll, "--require-rpcs" });
                Check(weave.ExitCode == 0, "--weave 退出码 = " + weave.ExitCode + "\n" + weave.Combined());
                Check(weave.Combined().Contains("auto-property 复制属性数：8"),
                    "报告里出现属性计数 8（6 个属性夹具 + 2 个混合类；实际输出见下）\n" + weave.Combined());
                Check(weave.Combined().Contains("本次实际编织的属性数：8"),
                    "报告里出现「实际编织的属性数：8」");
                Check(weave.Combined().Contains("RPC 方法数：2"),
                    "报告里 RPC 数为 2（属性夹具 0 + RPC 夹具 1 + 混合类 1；语义未变）");
                Check(weave.Combined().Contains("本次实际编织的类数：3"),
                    "报告里「实际编织的类数：3」（属性类与 RPC 类在同一轮被编织）");

                Check(Sha256(dll) != beforeDll, "编织后 DLL 内容确实变了");
                Check(Sha256(pdb) != beforePdb, "编织后 PDB 内容确实变了");
                Check(HasNoWeaverLeftovers(variantDir), "编织后没有暂存/备份残留文件");
            });

            Section("6[" + configuration + "]. --check 只验证不写盘 + 二次 --weave 幂等零 diff", delegate ()
            {
                string beforeDll = Sha256(dll);
                string beforePdb = Sha256(pdb);

                ProcessResult check = RunWeaver(new string[] { "--check", dll, "--require-rpcs" });
                Check(check.ExitCode == 0, "--check 退出码 = " + check.ExitCode + "\n" + check.Combined());
                Check(check.Combined().Contains("是否实际改写：否"), "--check 报告「是否实际改写：否」");
                Check(Sha256(dll) == beforeDll && Sha256(pdb) == beforePdb, "--check 零写（DLL/PDB hash 不变）");

                ProcessResult again = RunWeaver(new string[] { "--weave", dll, "--require-rpcs" });
                Check(again.ExitCode == 0, "二次 --weave 退出码 = " + again.ExitCode + "\n" + again.Combined());
                Check(again.Combined().Contains("是否实际改写：否"), "二次 --weave 报告「是否实际改写：否」");
                Check(Sha256(dll) == beforeDll && Sha256(pdb) == beforePdb,
                    "二次 --weave 幂等：DLL/PDB hash 逐字节不变");
            });

            Section("7[" + configuration + "]. 真实执行：普通赋值 + 实现标志 + 监视器锁行为", delegate ()
            {
                string[] lines = RunDriver(dll, runtimeDir, "PMWeave.Property.PropertyDriver", "RunWoven");
                CheckDriver(lines, WovenCases, "woven[" + configuration + "]", true);
            });

            Section("8[" + configuration + "]. 真实复制链：PMNetWorld + PMReplicationChannel 字节/ACK/条件", delegate ()
            {
                string[] lines = RunDriver(dll, runtimeDir, "PMWeave.Property.PropertyDriver", "RunReplication");
                CheckDriver(lines, ReplicationCases, "replication[" + configuration + "]", true);
            });
        }

        // =====================================================================================
        //  纯属性零 RPC：独立真实生成集合
        // =====================================================================================

        private static void RunPropertyOnlyChain(string workDir, string runtimeDir, string propertyRegion)
        {
            string variantDir = Path.Combine(workDir, "property-only");
            string outDir = Path.Combine(variantDir, "out");
            string genDir = Path.Combine(workDir, "gen-property-only");
            string idLock = Path.Combine(workDir, "ids-property-only.json");

            Section("9. 纯属性零 RPC：独立真实生成集合 → --weave 不能是 noop，--require-rpcs 语义不变", delegate ()
            {
                string sourcePath = Path.Combine(workDir, "PropertyOnlySource.cs");
                File.WriteAllText(sourcePath, propertyRegion, new UTF8Encoding(true));

                ProcessResult gen = RunProcess(
                    DotnetHost(),
                    new string[] { _genDllPath, "--decl-gen", sourcePath, "--out-dir", genDir, "--id-lock", idLock },
                    workDir);
                Check(gen.ExitCode == 0, "纯属性 --decl-gen 退出码 = " + gen.ExitCode + "\n" + gen.Combined());

                string[] files = Directory.Exists(genDir) ? Directory.GetFiles(genDir, "*.cs") : new string[0];
                Check(files.Length == 2, "纯属性生成物文件数 = " + files.Length + "（期望 2：类 + 注册表）");

                string classFile = FindGenerated(files, "PMWeave.Property.PropFixture.g.cs");
                string registryFile = FindGenerated(files, "PMNetGeneratedRegistry.g.cs");
                Check(classFile != null, "找到纯属性类生成物");
                if (classFile == null || registryFile == null)
                {
                    return;
                }

                string classText = Text(File.ReadAllText(classFile));
                string registryText = Text(File.ReadAllText(registryFile));
                Check(classText.Contains("public const int PMGeneratedPropertyIndex_Hp"),
                    "纯属性生成物含槽位常量（真实生成，不是手写）");
                Check(classText.Contains("private void PMNet_PropertyRawSet_Hp(int value)"),
                    "纯属性生成物含 RawSet helper（真实生成）");
                Check(classText.Contains("private void PMNet_PropertySet_Hp(int value)"),
                    "纯属性生成物含赋值 helper（真实生成）");
                Check(!classText.Contains("PMGeneratedRpcId_"), "纯属性生成物不含任何 RPC 声明（真正零 RPC）");
                Check(registryText.Contains("GeneratedClassCount = 1"),
                    "纯属性注册表只登记 1 个类（独立集合，不含 RPC 夹具）");

                WriteVariant(variantDir, runtimeDir, propertyRegion, LoadGenerated(genDir));
                BuildProject(Path.Combine(variantDir, "fixture.csproj"), outDir, "Debug", variantDir);

                string dll = Path.Combine(outDir, "PMPropFixture.dll");
                Check(File.Exists(dll), "纯属性夹具 DLL 已生成");
                if (!File.Exists(dll))
                {
                    return;
                }

                string before = Sha256(dll);
                ProcessResult weave = RunWeaver(new string[] { "--weave", dll });
                Check(weave.ExitCode == 0, "纯属性 --weave 退出码 = " + weave.ExitCode + "\n" + weave.Combined());
                Check(weave.Combined().Contains("RPC 方法数：0"), "纯属性程序集 RPC 数 = 0");
                Check(weave.Combined().Contains("auto-property 复制属性数：6"), "纯属性程序集属性数 = 6");
                Check(weave.Combined().Contains("本次实际编织的属性数：6"),
                    "纯属性程序集**真的**编织了 6 个属性（不是 noop）");
                Check(Sha256(dll) != before, "纯属性程序集 DLL 确实被改写");

                ProcessResult requireRpcs = RunWeaver(new string[] { "--weave", dll, "--require-rpcs" });
                Check(requireRpcs.ExitCode != 0,
                    "--require-rpcs 在无 RPC 的纯属性程序集上仍必须失败（语义未变），实际退出码 "
                    + requireRpcs.ExitCode);

                ProcessResult check = RunWeaver(new string[] { "--check", dll });
                Check(check.ExitCode == 0, "纯属性程序集 --check 通过（实际 " + check.ExitCode + "）");
            });
        }

        // =====================================================================================
        //  同类 RPC + 属性：一次 stamp
        // =====================================================================================

        private static void RunMixedClassChain(
            string workDir,
            string runtimeDir,
            string fixtureText,
            Dictionary<string, string> generated)
        {
            string variantDir = Path.Combine(workDir, "mixed-class");
            string outDir = Path.Combine(variantDir, "out");

            Section("10. 同类 RPC + 属性：同一个类一次 stamp 织好两侧", delegate ()
            {
                WriteVariant(variantDir, runtimeDir, fixtureText, generated);
                BuildProject(Path.Combine(variantDir, "fixture.csproj"), outDir, "Release", variantDir);

                string dll = Path.Combine(outDir, "PMPropFixture.dll");
                Check(File.Exists(dll), "混合类夹具 DLL 已生成");
                if (!File.Exists(dll))
                {
                    return;
                }

                ProcessResult weave = RunWeaver(new string[] { "--weave", dll, "--require-rpcs" });
                Check(weave.ExitCode == 0, "混合类 --weave 退出码 = " + weave.ExitCode + "\n" + weave.Combined());

                string[] lines = RunDriver(dll, runtimeDir, "PMWeave.Mixed.MixedDriver", "RunMixedClassProbe");
                CheckDriver(lines, MixedClassCases, "mixed-class", true);
            });
        }

        // =====================================================================================
        //  负例族：注入**真实生成产物副本**（生成物层）或夹具源码副本（声明形态层）
        // =====================================================================================

        private sealed class NegativeCase
        {
            public string Name;

            /// <summary>期望错误信息里出现的关键字（用于证明拒绝的是**这条**缺陷，而不是别的）。</summary>
            public string ExpectedKeyword;

            /// <summary>
            /// 改 %TEMP% 里**夹具源码副本**。只允许用于"声明形态"类负例
            /// （例如把 auto-property 换成自定义访问器）——这类缺陷本来就不该由生成器产出。
            /// </summary>
            public Func<string, string> SourcePatch;

            /// <summary>改 %TEMP% 里**真实生成产物副本**（文件名 → 文本）。生成物层缺陷只能这样注入。</summary>
            public Action<Dictionary<string, string>> GeneratedPatch;
        }

        private static List<NegativeCase> BuildNegativeCases()
        {
            List<NegativeCase> cases = new List<NegativeCase>();

            // ---- 声明形态层（只能改源码；生成器本身会硬拒这种形态，因此不能拿生成产物做）----
            cases.Add(new NegativeCase
            {
                Name = "corrupt-setter",
                ExpectedKeyword = "backing field",
                SourcePatch = text => ReplaceOnce(
                    text,
                    "[PMReplicated]\r\n        public int Hp { get; private set; }",
                    "[PMReplicated]\r\n        public int Hp { get { return _hpStore; } private set { _hpStore = value; } }\r\n\r\n        private int _hpStore;",
                    "corrupt-setter"),
            });

            // ---- 生成物层（全部打在真实生成产物副本上）----
            cases.Add(new NegativeCase
            {
                Name = "rawset-not-stub",
                ExpectedKeyword = "占位 stub",
                GeneratedPatch = gen => PatchFile(gen, "PropFixture.g.cs", text => ReplaceOnce(
                    text,
                    "        private void PMNet_PropertyRawSet_Hp(int value)\n        {\n            throw new System.InvalidOperationException(\"PMNet property has not been woven\");\n        }",
                    "        private void PMNet_PropertyRawSet_Hp(int value)\n        {\n        }",
                    "rawset-not-stub")),
            });

            cases.Add(new NegativeCase
            {
                Name = "helper-wrong-index",
                ExpectedKeyword = "槽位",
                GeneratedPatch = gen => PatchFile(gen, "PropFixture.g.cs", text => ReplaceOnce(
                    text,
                    "MarkPropertyDirty(PMGeneratedPropertyIndex_Hp);",
                    "MarkPropertyDirty(PMGeneratedPropertyIndex_Mana);",
                    "helper-wrong-index")),
            });

            cases.Add(new NegativeCase
            {
                Name = "helper-no-authority",
                ExpectedKeyword = "没有权威",
                GeneratedPatch = gen => PatchFile(gen, "PropFixture.g.cs", text => ReplaceOnce(
                    text,
                    "            if (pmChanged && HasAuthority)\n            {\n                MarkPropertyDirty(PMGeneratedPropertyIndex_Hp);\n            }",
                    "            if (pmChanged)\n            {\n                MarkPropertyDirty(PMGeneratedPropertyIndex_Hp);\n            }",
                    "helper-no-authority")),
            });

            cases.Add(new NegativeCase
            {
                Name = "helper-ignores-change",
                ExpectedKeyword = "值没有变化",
                GeneratedPatch = gen => PatchFile(gen, "PropFixture.g.cs", text => ReplaceOnce(
                    text,
                    "            if (pmChanged && HasAuthority)\n            {\n                MarkPropertyDirty(PMGeneratedPropertyIndex_Hp);\n            }",
                    "            if (HasAuthority)\n            {\n                MarkPropertyDirty(PMGeneratedPropertyIndex_Hp);\n            }",
                    "helper-ignores-change")),
            });

            cases.Add(new NegativeCase
            {
                Name = "reader-uses-setter",
                ExpectedKeyword = "普通 setter",
                GeneratedPatch = gen => PatchFile(gen, "PropFixture.g.cs", text => ReplaceOnce(
                    text,
                    "            self.PMNet_PropertyRawSet_Hp(pmValue);",
                    "            self.Hp = pmValue;",
                    "reader-uses-setter")),
            });

            // 「先普通 setter、再 RawSet」也必须被拒：RawSet 调了不代表没有反向标脏路径。
            cases.Add(new NegativeCase
            {
                Name = "reader-setter-then-rawset",
                ExpectedKeyword = "普通 setter",
                GeneratedPatch = gen => PatchFile(gen, "PropFixture.g.cs", text => ReplaceOnce(
                    text,
                    "            self.PMNet_PropertyRawSet_Hp(pmValue);",
                    "            self.Hp = pmValue;\n            self.PMNet_PropertyRawSet_Hp(pmValue);",
                    "reader-setter-then-rawset")),
            });

            cases.Add(new NegativeCase
            {
                Name = "missing-rawset-helper",
                ExpectedKeyword = "缺少生成物 helper",
                GeneratedPatch = gen => PatchFile(gen, "PropFixture.g.cs",
                    text => text.Replace("PMNet_PropertyRawSet_Hp", "PMNet_PropertyRawSetHp")),
            });

            cases.Add(new NegativeCase
            {
                Name = "missing-property-set-helper",
                ExpectedKeyword = "缺少生成物 helper",
                GeneratedPatch = gen => PatchFile(gen, "PropFixture.g.cs",
                    text => text.Replace("PMNet_PropertySet_Blob", "PMNet_PropertySetBlob")),
            });

            cases.Add(new NegativeCase
            {
                Name = "missing-registration-slot",
                ExpectedKeyword = "注册",
                GeneratedPatch = gen => PatchFile(gen, "PropFixture.g.cs",
                    text => RemoveLineContaining(text, "outProps.Add(PMGeneratedPropertyIndex_Blob,")),
            });

            cases.Add(new NegativeCase
            {
                Name = "pushbased-mismatch",
                ExpectedKeyword = "PushBased",
                GeneratedPatch = gen => PatchFile(gen, "PropFixture.g.cs", text => ReplaceOnce(
                    text,
                    "outProps.Add(PMGeneratedPropertyIndex_Polled, PMNet.PMCond.None, false);",
                    "outProps.Add(PMGeneratedPropertyIndex_Polled, PMNet.PMCond.None, true);",
                    "pushbased-mismatch")),
            });

            // 缺实例 gate：注册入口仍然会拒，但 `new` 不再被拒 —— 门只挂了一半。
            cases.Add(new NegativeCase
            {
                Name = "missing-instance-gate",
                ExpectedKeyword = "实例 gate",
                GeneratedPatch = gen => PatchFile(gen, "PropFixture.g.cs",
                    text => RemoveLineContaining(text, "private readonly int PMNet_rpcWeaveGate = PMNet_RequireRpcWeave();")),
            });

            // 缺注册入口 guard：实例侧仍然会拒，但注册（BuildEntry）不再被拒 —— 也是半个门。
            cases.Add(new NegativeCase
            {
                Name = "missing-buildentry-guard",
                ExpectedKeyword = "没有调用",
                GeneratedPatch = gen => PatchFile(gen, "PropFixture.g.cs",
                    text => RemoveLineContaining(text, "            PMNet_RequireRpcWeave();")),
            });

            // 混合类（同一个类里 RPC + 属性）：属性侧坏掉必须整轮失败、**零写**，
            // 从而证明 RPC 侧不会被留下"已织一半"的产物。
            cases.Add(new NegativeCase
            {
                Name = "mixed-property-wrong-index",
                ExpectedKeyword = "槽位",
                GeneratedPatch = gen => PatchFile(gen, "MixedFixture.g.cs", text => ReplaceOnce(
                    text,
                    "MarkPropertyDirty(PMGeneratedPropertyIndex_Value);",
                    "MarkPropertyDirty(PMGeneratedPropertyIndex_OwnerValue);",
                    "mixed-property-wrong-index")),
            });

            return cases;
        }

        private static void RunNegativeChains(
            string workDir,
            string runtimeDir,
            string fixtureText,
            Dictionary<string, string> generated)
        {
            List<NegativeCase> cases = BuildNegativeCases();

            Section("11. 负例族（生成物/声明层）：必须拒绝且文件 hash 不变", delegate ()
            {
                for (int i = 0; i < cases.Count; i++)
                {
                    NegativeCase item = cases[i];
                    string variantDir = Path.Combine(workDir, "neg-" + item.Name);
                    string outDir = Path.Combine(variantDir, "out");

                    string source = fixtureText;
                    Dictionary<string, string> patchedGenerated = CloneGenerated(generated);

                    try
                    {
                        if (item.SourcePatch != null)
                        {
                            source = item.SourcePatch(source);
                        }

                        if (item.GeneratedPatch != null)
                        {
                            item.GeneratedPatch(patchedGenerated);
                        }
                    }
                    catch (Exception ex)
                    {
                        Check(false, "负例 " + item.Name + " 注入失败（锚点不在）：" + ex.Message);
                        continue;
                    }

                    bool sourceChanged = !string.Equals(source, fixtureText, StringComparison.Ordinal);
                    bool generatedChanged = !SameGenerated(generated, patchedGenerated);
                    Check(sourceChanged || generatedChanged, "负例 " + item.Name + " 的输入确实被改动");

                    WriteVariant(variantDir, runtimeDir, source, patchedGenerated);
                    try
                    {
                        BuildProject(Path.Combine(variantDir, "fixture.csproj"), outDir, "Debug", variantDir);
                    }
                    catch (Exception ex)
                    {
                        Check(false, "负例 " + item.Name + " 夹具编译失败（注入本身不合法）：" + ex.Message);
                        continue;
                    }

                    string dll = Path.Combine(outDir, "PMPropFixture.dll");
                    string pdb = Path.Combine(outDir, "PMPropFixture.pdb");
                    string beforeDll = Sha256(dll);
                    string beforePdb = Sha256(pdb);
                    long beforeLen = new FileInfo(dll).Length;

                    ProcessResult weave = RunWeaver(new string[] { "--weave", dll });
                    Check(weave.ExitCode != 0,
                        "负例 " + item.Name + " 的 --weave 必须失败（实际退出码 " + weave.ExitCode + "）");
                    if (item.ExpectedKeyword != null)
                    {
                        Check(weave.Combined().Contains(item.ExpectedKeyword),
                            "负例 " + item.Name + " 的失败原因指向该缺陷（期望关键字「" + item.ExpectedKeyword
                            + "」）；实际输出：\n" + weave.Combined());
                    }

                    Check(Sha256(dll) == beforeDll, "负例 " + item.Name + " 失败后 DLL 未被改写（hash 不变）");
                    Check(Sha256(pdb) == beforePdb, "负例 " + item.Name + " 失败后 PDB 未被改写（hash 不变）");
                    Check(new FileInfo(dll).Length == beforeLen, "负例 " + item.Name + " 失败后 DLL 长度不变");
                    Check(HasNoWeaverLeftovers(variantDir), "负例 " + item.Name + " 失败后没有暂存/备份残留");

                    ProcessResult check = RunWeaver(new string[] { "--check", dll });
                    Check(check.ExitCode != 0, "负例 " + item.Name + " 的 --check 也必须失败");
                }
            });
        }

        // =====================================================================================
        //  负例族：RawSet 写错 backing field（改已编织 DLL 的 IL 字段 token）
        // =====================================================================================

        private static void RunRawSetWrongFieldChain(
            string workDir,
            string runtimeDir,
            string fixtureText,
            Dictionary<string, string> generated)
        {
            Section("12. 负例：RawSet 写错 backing field ⇒ weave/check 都必须拒绝", delegate ()
            {
                string variantDir = Path.Combine(workDir, "neg-rawset-wrong-field");
                string outDir = Path.Combine(variantDir, "out");

                WriteVariant(variantDir, runtimeDir, fixtureText, generated);
                BuildProject(Path.Combine(variantDir, "fixture.csproj"), outDir, "Debug", variantDir);

                string dll = Path.Combine(outDir, "PMPropFixture.dll");
                string pdb = Path.Combine(outDir, "PMPropFixture.pdb");

                ProcessResult weave = RunWeaver(new string[] { "--weave", dll });
                Check(weave.ExitCode == 0, "前置编织成功（退出码 " + weave.ExitCode + "）\n" + weave.Combined());

                // 把 RawSet_Hp 的 `stfld <Hp>k__BackingField` 的字段 token 换成 Mana 的 backing field：
                // 这就是「RawSet 写错字段」的真实形态（会静默把 Hp 的赋值写进 Mana）。
                PatchRawSetFieldToken(dll, "PMWeave.Property.PropFixture", "PMNet_PropertyRawSet_Hp",
                    "<Hp>k__BackingField", "<Mana>k__BackingField");

                string beforeDll = Sha256(dll);
                string beforePdb = Sha256(pdb);

                ProcessResult check = RunWeaver(new string[] { "--check", dll });
                Check(check.ExitCode != 0, "--check 必须拒绝写错字段的 RawSet（实际 " + check.ExitCode + "）");
                Check(check.Combined().Contains("backing field"),
                    "--check 的失败原因指向 backing field；实际输出：\n" + check.Combined());

                ProcessResult reweave = RunWeaver(new string[] { "--weave", dll });
                Check(reweave.ExitCode != 0, "对写错字段的已编织程序集 --weave 也必须失败");
                Check(Sha256(dll) == beforeDll && Sha256(pdb) == beforePdb,
                    "写错字段的负例：两次失败都没有改写文件（hash 不变）");
                Check(HasNoWeaverLeftovers(variantDir), "写错字段的负例：没有暂存/备份残留");
            });
        }

        // =====================================================================================
        //  负例族：收包 RawSet 丢掉 setter 的实现标志（旧版编织产物形态）
        // =====================================================================================

        /// <summary>
        /// 复现「旧版编织产物把 setter 的 MethodImpl 标志丢了」这一真实缺陷形态，并证明
        /// `<c>--check</c>` / <c>--weave</c> 都明确拒绝它（不是只靠反射探针发现）。
        ///
        /// 为何必须做元数据篡改而不用源码反例：编织器**每次都会**把 setter 的实现标志写齐
        /// （<c>PropertyWeaver.BuildRawSet</c> 直接赋值），因此源码级反例不存在；
        /// 能造出这种程序集的只有「旧版工具产物」或「手工篡改」。
        /// 篡改用工具自身的依赖 Mono.Cecil（仅工具依赖，不进运行时）：把
        /// <c>PMNet_PropertyRawSet_Locked</c> 的 ImplAttributes 改回 IL（= 丢锁）。
        /// 篡改不带符号，因此同时删掉 PDB，避免绕到无关的符号分支上。
        /// </summary>
        private static void RunImplFlagTamperChain(
            string workDir,
            string runtimeDir,
            string fixtureText,
            Dictionary<string, string> generated)
        {
            Section("13. 负例：收包 RawSet 丢 setter 实现标志（旧版产物形态）⇒ weave/check 都必须拒绝", delegate ()
            {
                string variantDir = Path.Combine(workDir, "neg-rawset-lost-impl-flags");
                string outDir = Path.Combine(variantDir, "out");

                WriteVariant(variantDir, runtimeDir, fixtureText, generated);
                BuildProject(Path.Combine(variantDir, "fixture.csproj"), outDir, "Release", variantDir);

                string dll = Path.Combine(outDir, "PMPropFixture.dll");
                string pdb = Path.Combine(outDir, "PMPropFixture.pdb");

                ProcessResult weave = RunWeaver(new string[] { "--weave", dll });
                Check(weave.ExitCode == 0, "前置编织成功（退出码 " + weave.ExitCode + "）\n" + weave.Combined());

                RunImplFlagTamper(workDir, dll, "PMWeave.Property.PropFixture", "PMNet_PropertyRawSet_Locked");
                if (File.Exists(pdb))
                {
                    File.Delete(pdb);
                }

                string beforeDll = Sha256(dll);

                ProcessResult check = RunWeaver(new string[] { "--check", dll });
                Check(check.ExitCode != 0,
                    "--check 必须拒绝丢了实现标志的 RawSet（实际 " + check.ExitCode + "）");
                Check(check.Combined().Contains("实现标志与 setter 不一致"),
                    "--check 的失败原因指向实现标志；实际输出：\n" + check.Combined());

                ProcessResult reweave = RunWeaver(new string[] { "--weave", dll });
                Check(reweave.ExitCode != 0,
                    "对丢了实现标志的已编织程序集 --weave 也必须失败（实际 " + reweave.ExitCode + "）");
                Check(reweave.Combined().Contains("实现标志与 setter 不一致"),
                    "--weave 的失败原因也指向实现标志；实际输出：\n" + reweave.Combined());

                Check(Sha256(dll) == beforeDll, "拒绝后 DLL 未被改写（hash 不变）");
                Check(HasNoWeaverLeftovers(variantDir), "拒绝后没有暂存/备份残留");
            });
        }

        /// <summary>
        /// 在沙箱里生成一个最小的 Mono.Cecil 篡改工程（引用工具输出目录里的 Mono.Cecil.dll），
        /// 把指定方法的 ImplAttributes 改成 IL，并写回原路径。
        /// </summary>
        private static void RunImplFlagTamper(
            string workDir,
            string targetDll,
            string typeFullName,
            string methodName)
        {
            string cecil = Path.Combine(Path.GetDirectoryName(_weaverDllPath), "Mono.Cecil.dll");
            if (!File.Exists(cecil))
            {
                throw new InvalidOperationException("找不到工具依赖 Mono.Cecil.dll：" + cecil);
            }

            string projectDir = Path.Combine(workDir, "impl-flag-tamper");
            Directory.CreateDirectory(projectDir);

            string program =
                "using System;\r\n" +
                "using System.IO;\r\n" +
                "using System.Linq;\r\n" +
                "using Mono.Cecil;\r\n" +
                "\r\n" +
                "public static class Tamper\r\n" +
                "{\r\n" +
                "    public static int Main(string[] args)\r\n" +
                "    {\r\n" +
                "        string path = args[0];\r\n" +
                "        string typeName = args[1];\r\n" +
                "        string methodName = args[2];\r\n" +
                "\r\n" +
                "        // 写到同目录的新文件名，读完/写完都释放句柄后再覆盖原文件（避免同文件读写互锁）。\r\n" +
                "        string dir = Path.GetDirectoryName(Path.GetFullPath(path));\r\n" +
                "        string outPath = Path.Combine(dir, \"tampered.dll\");\r\n" +
                "        string outPdb = Path.ChangeExtension(outPath, \".pdb\");\r\n" +
                "\r\n" +
                "        using (ModuleDefinition module = ModuleDefinition.ReadModule(path))\r\n" +
                "        {\r\n" +
                "            TypeDefinition type = module.GetTypes().First(t => t.FullName == typeName);\r\n" +
                "            MethodDefinition method = type.Methods.First(m => m.Name == methodName);\r\n" +
                "            Console.WriteLine(\"before impl=\" + (int)method.ImplAttributes);\r\n" +
                "            method.ImplAttributes = MethodImplAttributes.IL;\r\n" +
                "            Console.WriteLine(\"after  impl=\" + (int)method.ImplAttributes);\r\n" +
                "            module.Write(outPath);\r\n" +
                "        }\r\n" +
                "\r\n" +
                "        if (File.Exists(path)) { File.Delete(path); }\r\n" +
                "        File.Move(outPath, path);\r\n" +
                "        if (File.Exists(outPdb))\r\n" +
                "        {\r\n" +
                "            string targetPdb = Path.ChangeExtension(path, \".pdb\");\r\n" +
                "            if (File.Exists(targetPdb)) { File.Delete(targetPdb); }\r\n" +
                "            File.Move(outPdb, targetPdb);\r\n" +
                "        }\r\n" +
                "\r\n" +
                "        return 0;\r\n" +
                "    }\r\n" +
                "}\r\n";

            string csproj =
                "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n" +
                "  <PropertyGroup>\r\n" +
                "    <OutputType>Exe</OutputType>\r\n" +
                "    <TargetFramework>net8.0</TargetFramework>\r\n" +
                "    <AssemblyName>PmPropImplFlagTamper</AssemblyName>\r\n" +
                "    <RootNamespace>PmPropImplFlagTamper</RootNamespace>\r\n" +
                "    <LangVersion>latest</LangVersion>\r\n" +
                "    <Nullable>disable</Nullable>\r\n" +
                "    <ImplicitUsings>disable</ImplicitUsings>\r\n" +
                "    <GenerateDocumentationFile>false</GenerateDocumentationFile>\r\n" +
                "  </PropertyGroup>\r\n" +
                "  <ItemGroup>\r\n" +
                "    <Reference Include=\"Mono.Cecil\">\r\n" +
                "      <HintPath>" + cecil + "</HintPath>\r\n" +
                "    </Reference>\r\n" +
                "  </ItemGroup>\r\n" +
                "</Project>\r\n";

            File.WriteAllText(Path.Combine(projectDir, "Tamper.cs"), program, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(projectDir, "tamper.csproj"), csproj, new UTF8Encoding(false));

            string tamperOut = Path.Combine(projectDir, "out");
            BuildProject(Path.Combine(projectDir, "tamper.csproj"), tamperOut, "Release", projectDir);

            ProcessResult run = RunProcess(
                DotnetHost(),
                new string[]
                {
                    Path.Combine(tamperOut, "PmPropImplFlagTamper.dll"),
                    targetDll,
                    typeFullName,
                    methodName,
                },
                projectDir);

            if (run.ExitCode != 0)
            {
                throw new InvalidOperationException("实现标志篡改进程失败：\n" + run.Combined());
            }

            Console.WriteLine("  [info] " + run.Combined());
        }

        // =====================================================================================
        //  夹具工程 / 构建 / 进程
        // =====================================================================================

        private const string PropertyRegionBegin = "// >>>PROPERTY-ONLY-REGION>>>";
        private const string PropertyRegionEnd = "// <<<PROPERTY-ONLY-REGION<<<";

        private static string ExtractRegion(string text, string begin, string end)
        {
            int start = text.IndexOf(begin, StringComparison.Ordinal);
            int stop = text.IndexOf(end, StringComparison.Ordinal);
            if (start < 0 || stop <= start)
            {
                throw new InvalidOperationException("夹具缺少区域标记");
            }

            return text.Substring(start + begin.Length, stop - (start + begin.Length));
        }

        private static string Text(string value)
        {
            return value == null ? string.Empty : value;
        }

        private static string FindGenerated(string[] files, string suffix)
        {
            for (int i = 0; i < files.Length; i++)
            {
                if (Path.GetFileName(files[i]).EndsWith(suffix, StringComparison.Ordinal))
                {
                    return files[i];
                }
            }

            return null;
        }

        /// <summary>
        /// 读取一份真实生成产物集合（文件名 → 文本，统一成 LF，便于负例注入与比对）。
        /// </summary>
        private static Dictionary<string, string> LoadGenerated(string genDir)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(genDir) || !Directory.Exists(genDir))
            {
                return result;
            }

            string[] files = Directory.GetFiles(genDir, "*.cs");
            for (int i = 0; i < files.Length; i++)
            {
                result[Path.GetFileName(files[i])] = File.ReadAllText(files[i]).Replace("\r\n", "\n");
            }

            return result;
        }

        private static Dictionary<string, string> CloneGenerated(Dictionary<string, string> source)
        {
            Dictionary<string, string> clone = new Dictionary<string, string>(StringComparer.Ordinal);
            if (source == null)
            {
                return clone;
            }

            foreach (KeyValuePair<string, string> pair in source)
            {
                clone[pair.Key] = pair.Value;
            }

            return clone;
        }

        private static bool SameGenerated(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            if (a == null || b == null)
            {
                return a == b;
            }

            if (a.Count != b.Count)
            {
                return false;
            }

            foreach (KeyValuePair<string, string> pair in a)
            {
                string other;
                if (!b.TryGetValue(pair.Key, out other) || !string.Equals(pair.Value, other, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>改一份真实生成产物副本（按文件名后缀定位）。改不到就让检查项失败。</summary>
        private static void PatchFile(
            Dictionary<string, string> generated,
            string fileSuffix,
            Func<string, string> patch)
        {
            string key = null;
            foreach (KeyValuePair<string, string> pair in generated)
            {
                if (pair.Key.EndsWith(fileSuffix, StringComparison.Ordinal))
                {
                    key = pair.Key;
                    break;
                }
            }

            if (key == null)
            {
                throw new InvalidOperationException("生成产物里找不到 " + fileSuffix);
            }

            string patched = patch(generated[key]);
            if (string.Equals(patched, generated[key], StringComparison.Ordinal))
            {
                throw new InvalidOperationException("生成产物注入未生效（锚点不在）：" + fileSuffix);
            }

            generated[key] = patched;
        }

        private static string ReplaceOnce(string text, string oldValue, string newValue, string what)
        {
            int first = text.IndexOf(oldValue, StringComparison.Ordinal);
            if (first < 0)
            {
                throw new InvalidOperationException("注入锚点不存在：" + what);
            }

            if (text.IndexOf(oldValue, first + 1, StringComparison.Ordinal) >= 0)
            {
                throw new InvalidOperationException("注入锚点不唯一：" + what);
            }

            return text.Substring(0, first) + newValue + text.Substring(first + oldValue.Length);
        }

        /// <summary>删掉命中指定片段的整行（含行尾换行）。片段必须唯一命中一行。</summary>
        private static string RemoveLineContaining(string text, string fragment)
        {
            int at = text.IndexOf(fragment, StringComparison.Ordinal);
            if (at < 0)
            {
                throw new InvalidOperationException("注入锚点不存在：" + fragment);
            }

            if (text.IndexOf(fragment, at + 1, StringComparison.Ordinal) >= 0)
            {
                throw new InvalidOperationException("注入锚点不唯一：" + fragment);
            }

            // 行首 = 上一个换行符**之后**；行尾 = 下一个换行符（含）。
            // 这里不能用上一个换行符本身作为起点：那会把上一行的换行也删掉，
            // 导致上一行与下一行被拼在一起（生成物里就是「方法体与 } 粘连」的编译错误）。
            int previousNewline = text.LastIndexOf('\n', at);
            int lineStart = previousNewline < 0 ? 0 : previousNewline + 1;
            int nextNewline = text.IndexOf('\n', at);
            int removeCount = (nextNewline < 0 ? text.Length : nextNewline + 1) - lineStart;
            return text.Remove(lineStart, removeCount);
        }

        /// <summary>把夹具源码（以及真实生成产物副本）写进沙箱，并生成一个临时 netstandard2.0 工程。</summary>
        private static void WriteVariant(
            string variantDir,
            string runtimeDir,
            string fixtureSource,
            Dictionary<string, string> generated)
        {
            Directory.CreateDirectory(variantDir);
            File.WriteAllText(Path.Combine(variantDir, "Fixture.cs"), fixtureSource, new UTF8Encoding(true));

            if (generated != null && generated.Count > 0)
            {
                string generatedDir = Path.Combine(variantDir, "Generated");
                Directory.CreateDirectory(generatedDir);
                foreach (KeyValuePair<string, string> pair in generated)
                {
                    File.WriteAllText(
                        Path.Combine(generatedDir, pair.Key), pair.Value, new UTF8Encoding(false));
                }
            }

            string csproj =
                "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n" +
                "  <PropertyGroup>\r\n" +
                "    <TargetFramework>netstandard2.0</TargetFramework>\r\n" +
                "    <LangVersion>7.3</LangVersion>\r\n" +
                "    <AssemblyName>PMPropFixture</AssemblyName>\r\n" +
                "    <RootNamespace>PMWeave.Property</RootNamespace>\r\n" +
                "    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>\r\n" +
                "    <DebugType>portable</DebugType>\r\n" +
                "    <GenerateDocumentationFile>false</GenerateDocumentationFile>\r\n" +
                "    <NoWarn>$(NoWarn);CS1591;CS0414;CS0219;CS0105;CS0169;CS0649;CS0067;CS0162</NoWarn>\r\n" +
                "  </PropertyGroup>\r\n" +
                "  <ItemGroup>\r\n" +
                "    <Compile Include=\"Fixture.cs\" />\r\n" +
                "    <Compile Include=\"Generated/**/*.cs\" />\r\n" +
                "  </ItemGroup>\r\n" +
                "  <ItemGroup>\r\n" +
                "    <Reference Include=\"PMNet.Runtime.Temp\">\r\n" +
                "      <HintPath>" + ToMsBuildPath(Path.Combine(runtimeDir, "PMNet.Runtime.Temp.dll")) + "</HintPath>\r\n" +
                "    </Reference>\r\n" +
                "  </ItemGroup>\r\n" +
                "</Project>\r\n";

            File.WriteAllText(Path.Combine(variantDir, "fixture.csproj"), csproj, new UTF8Encoding(false));
        }

        private static void BuildRuntime(string repoRoot, string workDir, string runtimeDir)
        {
            string projectDir = Path.Combine(workDir, "runtime-src");
            Directory.CreateDirectory(projectDir);

            string repo = ToMsBuildPath(repoRoot);
            string csproj =
                "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n" +
                "  <PropertyGroup>\r\n" +
                "    <TargetFramework>netstandard2.0</TargetFramework>\r\n" +
                "    <LangVersion>7.3</LangVersion>\r\n" +
                "    <AssemblyName>PMNet.Runtime.Temp</AssemblyName>\r\n" +
                "    <RootNamespace>PMNet</RootNamespace>\r\n" +
                "    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>\r\n" +
                "    <DebugType>portable</DebugType>\r\n" +
                "    <GenerateDocumentationFile>false</GenerateDocumentationFile>\r\n" +
                "    <NoWarn>$(NoWarn);CS1591</NoWarn>\r\n" +
                "  </PropertyGroup>\r\n" +
                "  <ItemGroup>\r\n" +
                "    <Compile Include=\"" + repo + "/Client/Assets/Scripts/PMNet/**/*.cs\" " +
                "Exclude=\"" + repo + "/Client/Assets/Scripts/PMNet/Generated/**/*.cs\" />\r\n" +
                "  </ItemGroup>\r\n" +
                "</Project>\r\n";

            File.WriteAllText(Path.Combine(projectDir, "runtime.csproj"), csproj, new UTF8Encoding(false));
            BuildProject(Path.Combine(projectDir, "runtime.csproj"), runtimeDir, "Release", repoRoot);
        }

        private static void BuildProject(string projectPath, string outDir, string configuration, string workingDir)
        {
            _fixtureBuilds++;
            ProcessResult result = RunProcess(
                DotnetHost(),
                new string[] { "build", projectPath, "-c", configuration, "-o", outDir, "-v:q", "--nologo" },
                workingDir);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "dotnet build 失败（" + configuration + "）：" + projectPath + "\n" + result.Combined());
            }
        }

        private static ProcessResult RunWeaver(string[] args)
        {
            _weaverInvocations++;
            List<string> all = new List<string>();
            all.Add(_weaverDllPath);
            all.AddRange(args);
            return RunProcess(DotnetHost(), all.ToArray(), Path.GetDirectoryName(_weaverDllPath));
        }

        // =====================================================================================
        //  加载执行
        // =====================================================================================

        private static string[] RunDriver(string dllPath, string runtimeDir, string typeName, string entryName)
        {
            string loadDir = Path.Combine(Path.GetDirectoryName(dllPath), "load");
            Directory.CreateDirectory(loadDir);
            string loadPath = Path.Combine(loadDir, entryName + "-" + (++_loadSerial) + ".dll");
            File.Copy(dllPath, loadPath, true);

            string sourcePdb = Path.ChangeExtension(dllPath, ".pdb");
            if (File.Exists(sourcePdb))
            {
                File.Copy(sourcePdb, Path.ChangeExtension(loadPath, ".pdb"), true);
            }

            FixtureLoadContext context = new FixtureLoadContext(runtimeDir);
            try
            {
                Assembly assembly = context.LoadFromAssemblyPath(Path.GetFullPath(loadPath));
                Type driver = assembly.GetType(typeName, true);
                MethodInfo method = driver.GetMethod(entryName, BindingFlags.Public | BindingFlags.Static);
                if (method == null)
                {
                    return new string[] { "FAIL fatal :: 夹具缺少入口 " + entryName };
                }

                object result;
                try
                {
                    result = method.Invoke(null, null);
                }
                catch (TargetInvocationException ex)
                {
                    Exception inner = ex.InnerException ?? ex;
                    return new string[]
                    {
                        "FAIL fatal :: 夹具入口 " + entryName + " 抛出 " + inner.GetType().Name + "：" + inner.Message,
                    };
                }

                string[] lines = result as string[];
                if (lines == null)
                {
                    return new string[] { "FAIL fatal :: 夹具入口 " + entryName + " 返回了非 string[]" };
                }

                return lines;
            }
            finally
            {
                context.Unload();
            }
        }

        private sealed class FixtureLoadContext : AssemblyLoadContext
        {
            private readonly string _runtimeDir;

            public FixtureLoadContext(string runtimeDir)
                : base("pmproperty-fixture", true)
            {
                _runtimeDir = runtimeDir;
            }

            protected override Assembly Load(AssemblyName assemblyName)
            {
                string candidate = Path.Combine(_runtimeDir, assemblyName.Name + ".dll");
                return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
            }
        }

        private static void CheckDriver(string[] lines, string[] expectedCases, string tag) 
        {
            CheckDriver(lines, expectedCases, tag, false);
        }

        /// <summary>
        /// 校验夹具驱动的用例集合。
        /// <param name="echo">
        /// true = 把每条用例（含 PASS）原样打印出来。
        /// 本组新增的"实现标志 / 监听器锁 / 真实复制链 / 混合类"四个入口全部开此开关：
        /// 这些用例就是本次要交的**证据本身**，不应只靠末尾的"通过 N 项"字。
        /// </param>
        /// </summary>
        private static void CheckDriver(string[] lines, string[] expectedCases, string tag, bool echo)
        {
            Dictionary<string, string> results = new Dictionary<string, string>(StringComparer.Ordinal);
            int fail = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.StartsWith("PASS ", StringComparison.Ordinal))
                {
                    // 继续
                }
                else if (line.StartsWith("FAIL ", StringComparison.Ordinal))
                {
                    fail++;
                    Check(false, tag + "：" + line);
                }
                else
                {
                    continue;
                }

                if (echo)
                {
                    Console.WriteLine("  [" + tag + "] " + line);
                }

                int space = line.IndexOf(' ', 5);
                string name = space < 0 ? line.Substring(5) : line.Substring(5, space - 5);
                if (results.ContainsKey(name))
                {
                    Check(false, tag + "：用例 " + name + " 出现了多次");
                }

                results[name] = line;
            }

            Check(fail == 0, tag + "：夹具没有 FAIL 用例（FAIL 数 = " + fail + "）");

            for (int i = 0; i < expectedCases.Length; i++)
            {
                if (!results.ContainsKey(expectedCases[i]))
                {
                    Check(false, tag + "：缺少用例 " + expectedCases[i]);
                }
            }

            Check(results.Count == expectedCases.Length,
                tag + "：用例数 = " + results.Count + "（期望恰好 " + expectedCases.Length + "，防止用例被静默删掉）");
        }

        // =====================================================================================
        //  IL 字段 token 注入
        // =====================================================================================

        private static TypeDefinitionHandle FindType(MetadataReader reader, string fullName)
        {
            foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
            {
                TypeDefinition type = reader.GetTypeDefinition(handle);
                string ns = reader.GetString(type.Namespace);
                string name = reader.GetString(type.Name);
                string candidate = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                if (string.Equals(candidate, fullName, StringComparison.Ordinal))
                {
                    return handle;
                }
            }

            throw new InvalidOperationException("找不到类型 " + fullName);
        }

        /// <summary>
        /// 把 `PMNet_PropertyRawSet_&lt;P&gt;` 里的 `stfld &lt;P&gt;k__BackingField` 的字段 token
        /// 换成另一个 backing field —— 这是「RawSet 写错字段」最真实的形态。
        /// 只改 4 个 token 字节，debug 目录 / PDB 不受影响（所以 DLL 与 PDB 仍“匹配”）。
        /// </summary>
        private static void PatchRawSetFieldToken(
            string dllPath,
            string typeFullName,
            string methodName,
            string fromFieldName,
            string toFieldName)
        {
            int ilFileOffset;
            int fromToken;
            int toToken;

            using (FileStream stream = File.OpenRead(dllPath))
            {
                using (PEReader pe = new PEReader(stream))
                {
                    MetadataReader reader = pe.GetMetadataReader();
                    TypeDefinitionHandle typeHandle = FindType(reader, typeFullName);
                    TypeDefinition type = reader.GetTypeDefinition(typeHandle);

                    fromToken = 0;
                    toToken = 0;
                    foreach (FieldDefinitionHandle fieldHandle in type.GetFields())
                    {
                        string name = reader.GetString(reader.GetFieldDefinition(fieldHandle).Name);
                        if (string.Equals(name, fromFieldName, StringComparison.Ordinal))
                        {
                            fromToken = MetadataTokens.GetToken(fieldHandle);
                        }

                        if (string.Equals(name, toFieldName, StringComparison.Ordinal))
                        {
                            toToken = MetadataTokens.GetToken(fieldHandle);
                        }
                    }

                    int rva = 0;
                    foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                    {
                        if (string.Equals(
                            reader.GetString(reader.GetMethodDefinition(methodHandle).Name),
                            methodName,
                            StringComparison.Ordinal))
                        {
                            rva = reader.GetMethodDefinition(methodHandle).RelativeVirtualAddress;
                        }
                    }

                    if (fromToken == 0 || toToken == 0 || rva == 0)
                    {
                        throw new InvalidOperationException(
                            "字段 token 注入前置条件不满足（from=" + fromToken + " to=" + toToken + " rva=" + rva + "）");
                    }

                    byte[] il = pe.GetMethodBody(rva).GetILBytes();
                    if (il.Length != 8 || il[0] != 0x02 || il[1] != 0x03 || il[2] != 0x7D || il[7] != 0x2A)
                    {
                        throw new InvalidOperationException(
                            "RawSet 的 IL 不是预期的 4 条指令形态：" + BitConverter.ToString(il));
                    }

                    int tokenInIl = BitConverter.ToInt32(il, 3);
                    if (tokenInIl != fromToken)
                    {
                        throw new InvalidOperationException(
                            "RawSet 写的是 token " + tokenInIl + "，期望 " + fromToken);
                    }

                    ilFileOffset = RvaToFileOffset(pe, rva);
                }
            }

            byte[] patched = File.ReadAllBytes(dllPath);
            BitConverter.GetBytes(toToken).CopyTo(patched, ilFileOffset + 3);
            File.WriteAllBytes(dllPath, patched);

            Console.WriteLine("  [info] 已把 " + methodName + " 的 stfld token " + fromToken + " 改成 " + toToken
                              + "（文件偏移 IL+" + ilFileOffset + "）");
        }

        private static int RvaToFileOffset(PEReader pe, int rva)
        {
            foreach (SectionHeader section in pe.PEHeaders.SectionHeaders)
            {
                int size = Math.Max(section.VirtualSize, section.SizeOfRawData);
                if (rva >= section.VirtualAddress && rva < section.VirtualAddress + size)
                {
                    return rva - section.VirtualAddress + section.PointerToRawData;
                }
            }

            throw new InvalidOperationException("RVA " + rva + " 不在任何 PE 段内");
        }

        // =====================================================================================
        //  通用工具
        // =====================================================================================

        private static bool HasNoWeaverLeftovers(string directory)
        {
            string[] leftovers = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                            || p.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                            || p.Contains(".pmweave"))
                .ToArray();

            if (leftovers.Length == 0)
            {
                return true;
            }

            Console.WriteLine("  [info] 残留文件：" + string.Join(", ", leftovers));
            return false;
        }

        private static string Sha256(string path)
        {
            if (!File.Exists(path))
            {
                return "<缺失>";
            }

            using (SHA256 sha = SHA256.Create())
            {
                using (FileStream stream = File.OpenRead(path))
                {
                    byte[] hash = sha.ComputeHash(stream);
                    StringBuilder builder = new StringBuilder(hash.Length * 2);
                    for (int i = 0; i < hash.Length; i++)
                    {
                        builder.Append(hash[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                    }

                    return builder.ToString();
                }
            }
        }

        private static ProcessResult RunProcess(string fileName, string[] arguments, string workingDirectory)
        {
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = fileName;
            info.WorkingDirectory = workingDirectory;
            info.UseShellExecute = false;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.CreateNoWindow = true;
            for (int i = 0; i < arguments.Length; i++)
            {
                info.ArgumentList.Add(arguments[i]);
            }

            StringBuilder stdout = new StringBuilder();
            StringBuilder stderr = new StringBuilder();

            using (Process process = new Process())
            {
                process.StartInfo = info;
                process.OutputDataReceived += delegate (object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        stdout.AppendLine(e.Data);
                    }
                };
                process.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        stderr.AppendLine(e.Data);
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                ProcessResult result = new ProcessResult();
                result.ExitCode = process.ExitCode;
                result.StdOut = stdout.ToString();
                result.StdErr = stderr.ToString();
                return result;
            }
        }

        private static string DotnetHost()
        {
            string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            return string.IsNullOrEmpty(host) ? "dotnet" : host;
        }

        private static string ToMsBuildPath(string path)
        {
            return path.Replace('\\', '/');
        }

        private static string LocateToolDll(string repoRoot, string toolName)
        {
            string[] candidates = new string[]
            {
                Path.Combine(repoRoot, "Tools", toolName, "bin", "Release", "net8.0", toolName + ".dll"),
                Path.Combine(repoRoot, "Tools", toolName, "bin", "Debug", "net8.0", toolName + ".dll"),
                Path.Combine(repoRoot, "Tools", toolName, "bin", "Release", "net8.0", toolName + ".exe"),
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                if (File.Exists(candidates[i]))
                {
                    return candidates[i];
                }
            }

            throw new InvalidOperationException(
                "找不到工具 " + toolName + " 的输出（先构建 " + toolName + " 工程）："
                + string.Join(" / ", candidates));
        }

        private static string ResolveRepoRoot(string[] args)
        {
            if (args != null && args.Length > 0 && !string.IsNullOrEmpty(args[0]))
            {
                return Path.GetFullPath(args[0]);
            }

            DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))
                    && Directory.Exists(Path.Combine(dir.FullName, "Tools"))
                    && Directory.Exists(Path.Combine(dir.FullName, "Docs", "plans")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException("无法定位仓库根目录（请把仓库根作为第一个参数传入）。");
        }

        private sealed class ProcessResult
        {
            public int ExitCode;
            public string StdOut = string.Empty;
            public string StdErr = string.Empty;

            public string Combined()
            {
                return (StdOut + StdErr).Trim();
            }
        }
    }
}
