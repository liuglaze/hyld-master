// PMNetWeaverTest —— P1 独立编织工具 + **真实编译/真实执行** 证明（T-W1 / T-W2）。
//
// 契约：Docs/plans/net-rpc-weaving-contract.md（唯一新冻结接口）
//
// 门禁链条（每一步都是“真实的”，不是对谓词的纯函数断言）：
//   [1] 现有生成器 PMNetGen 扫夹具声明 → 生成物文本（不改生成器）
//   [2] **断言生成物已经是冻结格式 v1**（private 发送 helper / 版本方法 / Require / 实例 gate /
//       BuildEntry 首句 gate）——P2 之后生成器会真的产出这些，门禁不再用临时字符串补，
//       而是直接拿**真实生成物**去编译（漏发任何一条都会在这里先变红）
//   [3] 未编织负例：--check 必须失败、new 与注册必须被 guard 拒绝
//   [4] --weave（真实 CLI 进程）：预检失败必须零写；成功后用**独立读取器**
//       （System.Reflection.Metadata）确认 PDB 完好，并核对稳定 ID 未变
//   [5] --check 零写；二次 --weave 幂等零 diff
//   [6] AssemblyLoadContext 加载编织后的夹具 → FixtureDriver.RunWoven 跑完整行为链
//   [7] 负例族：未知版本 / 假 stamp / 缺 guard / BuildEntry 缺 gate / 损坏收包 helper /
//       损坏发送 helper / 强名称 / 无 RPC / 无 PDB / 非 PE 输入 —— 全部必须明确失败且**零写**
//
// 负向注入的边界：所有注入都只改 **%TEMP% 沙箱副本**里的生成物文本（每个变体一个独立目录），
// 仓库里的生成器与夹具源码一行不动；注不进（锚点不在）就当场报失败，不静默跳过。
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

namespace PMNetWeaverTest
{
    internal static class Program
    {
        // ------------------------------------------------------------------ 断言框架

        private static int _passed;
        private static readonly List<string> _failures = new List<string>();
        private static int _weaverInvocations;
        private static int _fixtureBuilds;

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

        // ------------------------------------------------------------------ 入口

        private static Dictionary<string, string> _expectedIds = new Dictionary<string, string>(StringComparer.Ordinal);
        private static string _expectedProtocolHash = "<未解析>";

        private static int Main(string[] args)
        {
            Console.WriteLine("=== PMNetWeaver P1 门禁（真实编译 + 真实编织 + 真实执行） ===");

            string repoRoot = ResolveRepoRoot(args);
            Console.WriteLine("仓库根目录：" + repoRoot);

            string workDir = Path.Combine(Path.GetTempPath(), "pmweaver-test");
            if (Directory.Exists(workDir))
            {
                Directory.Delete(workDir, true);
            }

            Directory.CreateDirectory(workDir);
            Console.WriteLine("临时工作目录：" + workDir);

            string runtimeDir = Path.Combine(workDir, "runtime");
            string genDir = Path.Combine(workDir, "gen");
            string idLock = Path.Combine(workDir, "ids.json");

            string genDll = LocateToolDll(repoRoot, "PMNetGen");
            string weaverDll = LocateToolDll(repoRoot, "PMNetWeaver");
            Console.WriteLine("PMNetGen   : " + genDll);
            Console.WriteLine("PMNetWeaver: " + weaverDll);

            string fixtureSource = Path.Combine(repoRoot, "Tools", "PMNetWeaverTest", "Fixture.cs");

            SortedDictionary<string, string> generated = new SortedDictionary<string, string>(StringComparer.Ordinal);

            Section("0. 环境：夹具源码与工具就位", delegate ()
            {
                Check(File.Exists(fixtureSource), "夹具声明源存在：" + fixtureSource);
                Check(File.Exists(genDll), "PMNetGen.dll 存在：" + genDll);
                Check(File.Exists(weaverDll), "PMNetWeaver.dll 存在：" + weaverDll);
                Check(!File.Exists(Path.Combine(repoRoot, "Tools", "PMNetWeaverTest", "Fixture.GeneratedGuard.cs")),
                    "过时的手工 guard 夹具已删除（P2 之后版本/守卫由生成器产出，不再需要手写副本）");
            });

            if (_failures.Count > 0)
            {
                return Summarize();
            }

            Section("1. 现有 PMNetGen 扫夹具声明并生成（--decl-gen，不改生成器）", delegate ()
            {
                ProcessResult gen = RunProcess(
                    DotnetHost(),
                    new string[]
                    {
                        genDll, "--decl-gen", fixtureSource,
                        "--out-dir", genDir,
                        "--id-lock", idLock,
                    },
                    repoRoot);

                Check(gen.ExitCode == 0, "PMNetGen --decl-gen 退出码 = " + gen.ExitCode + "\n" + gen.Combined());
                Check(File.Exists(idLock), "ID 锁文件已写出：" + idLock);

                string[] files = Directory.Exists(genDir)
                    ? Directory.GetFiles(genDir, "*.cs")
                    : new string[0];
                Check(files.Length == 2, "生成物文件数 = " + files.Length + "（期望 2）");

                for (int i = 0; i < files.Length; i++)
                {
                    generated[Path.GetFileName(files[i])] = File.ReadAllText(files[i]);
                }

                string classFile = generated.Keys.FirstOrDefault(k => k.StartsWith("PMNet.PMWeave.Fixture.", StringComparison.Ordinal));
                Check(classFile != null, "找到类生成物（PMNet.PMWeave.Fixture.WeaveFixture.g.cs）");
                Check(generated.Keys.Any(k => k == "PMNetGeneratedRegistry.g.cs"), "找到程序集注册表生成物");

                // 生成物里的稳定 ID / 协议摘要（期望值来源：生成器输出文本）。
                _expectedIds = ParseConsts(generated);

                string registryText;
                if (!generated.TryGetValue("PMNetGeneratedRegistry.g.cs", out registryText))
                {
                    registryText = null;
                }

                _expectedProtocolHash = ParseHeaderHash(registryText);
                Check(_expectedIds.Count >= 9, "解析到 " + _expectedIds.Count + " 个稳定 ID 常量（期望 >= 9）");
                Check(_expectedProtocolHash.StartsWith("0x", StringComparison.Ordinal),
                    "解析到生成期协议摘要（实际 " + _expectedProtocolHash + "）");
            });

            if (_failures.Count > 0)
            {
                return Summarize();
            }

            Dictionary<string, string> expectedIds = _expectedIds;
            string expectedProtocolHash = _expectedProtocolHash;
            Console.WriteLine("生成期协议摘要：" + expectedProtocolHash);

            Section("2. 真实编译 PMNet 运行时（netstandard2.0 / C# 7.3）", delegate ()
            {
                BuildRuntime(repoRoot, workDir, runtimeDir);
                Check(File.Exists(Path.Combine(runtimeDir, "PMNet.Runtime.Temp.dll")),
                    "运行时程序集已生成");
            });

            if (_failures.Count > 0)
            {
                return Summarize();
            }

            // ------------------------------------------------------------------
            //  正向：Debug 与 Release 各走一遍完整链条
            // ------------------------------------------------------------------
            for (int c = 0; c < 2; c++)
            {
                string configuration = c == 0 ? "Debug" : "Release";
                RunPositiveChain(
                    repoRoot, workDir, runtimeDir, generated, expectedIds,
                    expectedProtocolHash, configuration);
            }

            // ------------------------------------------------------------------
            //  负例族（只在 Debug 上跑，省构建时间；失败语义与配置无关）
            // ------------------------------------------------------------------
            RunNegativeChains(repoRoot, workDir, runtimeDir, generated);

            // ------------------------------------------------------------------
            //  P3 加固链条：同程序集重载 / 同名多类 RPC / guard 与版本语义 / 符号 / 回滚 / 并发
            // ------------------------------------------------------------------
            RunHardeningChains(repoRoot, workDir, runtimeDir);

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
                return 0;
            }

            Console.WriteLine("结果：FAIL");
            return 1;
        }

        // =====================================================================================
        //  正向链条
        // =====================================================================================

        private static int _unwovenSequencePoints = -1;

        private static void RunPositiveChain(
            string repoRoot,
            string workDir,
            string runtimeDir,
            SortedDictionary<string, string> generated,
            Dictionary<string, string> expectedIds,
            string expectedProtocolHash,
            string configuration)
        {
            string variantDir = Path.Combine(workDir, "base-" + configuration);
            string outDir = Path.Combine(variantDir, "out");

            Section("3[" + configuration + "]. 断言真实生成物已是冻结格式 v1 + 真实编译夹具（含 PDB）", delegate ()
            {
                // P2 之前这一步是“把生成物文本补成冻结格式”；现在生成器自己就产出冻结格式，
                // 因此改为**只校验不补**：生成器一旦回退（漏发版本门 / 发送 helper 又变 public /
                // BuildEntry 第一句不是 guard），这里立刻变红，而不是被补丁掩盖。
                AssertFrozenFormatGenerated(generated);
                WriteVariant(variantDir, runtimeDir, repoRoot, generated, null);
                BuildFixture(variantDir, outDir, configuration);

                Check(File.Exists(Path.Combine(outDir, "PMWeaveFixture.dll")), "夹具 DLL 已生成");
                Check(File.Exists(Path.Combine(outDir, "PMWeaveFixture.pdb")), "夹具 PDB 已生成（" + configuration + "）");
            });

            string dll = Path.Combine(outDir, "PMWeaveFixture.dll");
            string pdb = Path.Combine(outDir, "PMWeaveFixture.pdb");

            if (!File.Exists(dll) || !File.Exists(pdb))
            {
                return;
            }

            Section("4[" + configuration + "]. 未编织必须被拒：--check 失败 + new/注册被 guard 拦住", delegate ()
            {
                string dllHash = Sha256(dll);
                string pdbHash = Sha256(pdb);

                // 先量一下“未编织时业务方法自己有多少条 sequence point”，编织后用来核对
                // 克隆业务体是否**逐条**搬移（不写死数字，避免夹具一改就假红）。
                PdbReport beforeReport = InspectPdb(dll);
                int beforeSequencePoints;
                if (!beforeReport.SequencePointCounts.TryGetValue("ServerAttack", out beforeSequencePoints))
                {
                    beforeSequencePoints = -1;
                }

                _unwovenSequencePoints = beforeSequencePoints;
                Check(beforeSequencePoints > 10,
                    "未编织 ServerAttack 的 sequence point 条数 = " + beforeSequencePoints + "（期望 > 10，说明夹具真的有调试信息）");

                ProcessResult check = RunWeaver(new string[] { "--check", dll });
                Check(check.ExitCode != 0, "未编织时 --check 必须失败（实际退出码 " + check.ExitCode + "）");
                Check(Sha256(dll) == dllHash && Sha256(pdb) == pdbHash, "--check 失败后 DLL/PDB 未被改写");

                string[] lines = RunDriver(dll, runtimeDir, "RunPreWeave", true);
                CheckDriver(lines, PreWeaveCases, "preweave[" + configuration + "]");

                // 稳定 ID：未编织时也要能读出常量（编织前后必须一致）
                Dictionary<string, string> beforeIds = ReadConstFields(dll, runtimeDir);
                Check(IdsEqual(expectedIds, beforeIds), "未编织 DLL 的稳定 ID 与生成物一致");

                Dictionary<string, string> afterIds = ReadConstFields(dll, runtimeDir);
                Check(IdsEqual(beforeIds, afterIds), "未编织 DLL 两次读取的稳定 ID 一致");
            });

            Section("5[" + configuration + "]. 真实编织（CLI 进程）+ 独立 PDB 校验 + 稳定 ID 不变", delegate ()
            {
                string beforeDll = Sha256(dll);
                string beforePdb = Sha256(pdb);
                Check(beforePdb.Length == 64, "编织前 PDB 存在（" + beforePdb.Substring(0, 12) + "…）");

                ProcessResult weave = RunWeaver(new string[] { "--weave", dll, "--reference-dir", runtimeDir });
                Check(weave.ExitCode == 0, "--weave 退出码 = " + weave.ExitCode + "\n" + weave.Combined());
                Check(weave.StdOut.Contains("是否实际改写：是"), "--weave 报告“实际改写=是”");
                Check(weave.StdOut.Contains("RPC 方法数：7"), "--weave 报告 7 条 RPC（实际输出：" + FirstLine(weave.StdOut) + "）");
                Check(weave.StdOut.Contains("PDB 独立校验通过"), "--weave 报告 PDB 独立校验通过");
                Check(Sha256(dll) != beforeDll, "编织后 DLL 内容确实改变");
                Check(Sha256(pdb) != beforePdb, "编织后 PDB 内容确实改变（未静默丢弃）");

                PdbReport report = InspectPdb(dll);
                Check(report.CorruptMethods == 0,
                    "独立读取器报告 PDB 损坏方法数 = " + report.CorruptMethods + "（应为 0）");
                Check(report.MethodCount >= 20, "PDB 里方法数 = " + report.MethodCount);

                // 克隆出来的业务体必须保留与源方法同样多的 sequence point（行号映射真的搬过去了）
                Check(report.SequencePointCounts.ContainsKey("PMNet_RpcBody_ServerAttack"),
                    "PDB 里有克隆业务体的调试信息");
                int cloneSequencePoints = report.SequencePointCounts.ContainsKey("PMNet_RpcBody_ServerAttack")
                    ? report.SequencePointCounts["PMNet_RpcBody_ServerAttack"]
                    : -1;
                Check(cloneSequencePoints == _unwovenSequencePoints,
                    "克隆业务体 sequence point 条数 = " + cloneSequencePoints
                    + "（未编织时为 " + _unwovenSequencePoints + "，必须逐条一致）");
                Check(report.SequencePointCounts.ContainsKey("ServerAttack") && report.SequencePointCounts["ServerAttack"] == 0,
                    "编织后的 wrapper 不保留失效行号（实际 " + (report.SequencePointCounts.ContainsKey("ServerAttack") ? report.SequencePointCounts["ServerAttack"] : -1) + " 条）");

                Dictionary<string, string> afterIds = ReadConstFields(dll, runtimeDir);
                Check(IdsEqual(expectedIds, afterIds), "编织后稳定 ID 与生成物一致（逐项）");
            });

            Section("6[" + configuration + "]. --check 零写 + 二次 weave 幂等零 diff", delegate ()
            {
                string dllHash = Sha256(dll);
                string pdbHash = Sha256(pdb);

                ProcessResult check = RunWeaver(new string[] { "--check", dll });
                Check(check.ExitCode == 0, "--check 退出码 = " + check.ExitCode + "\n" + check.Combined());
                Check(check.StdOut.Contains("--check 通过"), "--check 报告通过");
                Check(Sha256(dll) == dllHash && Sha256(pdb) == pdbHash, "--check 完全不写盘（DLL/PDB 哈希不变）");

                ProcessResult again = RunWeaver(new string[] { "--weave", dll });
                Check(again.ExitCode == 0, "二次 --weave 退出码 = " + again.ExitCode + "\n" + again.Combined());
                Check(again.StdOut.Contains("是否实际改写：否"), "二次 --weave 报告“未改写”（幂等）");
                Check(Sha256(dll) == dllHash && Sha256(pdb) == pdbHash, "二次 --weave 零 diff（DLL/PDB 逐字节不变）");
            });

            Section("7[" + configuration + "]. 真实执行：普通调用/收包/校验/数组/业务体形态", delegate ()
            {
                string[] lines = RunDriver(dll, runtimeDir, "RunWoven", true);
                CheckDriver(lines, WovenCases, "woven[" + configuration + "]");

                string hash = lines.FirstOrDefault(l => l.StartsWith("HASH ", StringComparison.Ordinal));
                Check(hash != null && hash.EndsWith(expectedProtocolHash, StringComparison.OrdinalIgnoreCase),
                    "运行期全局协议摘要与生成期一致（运行期：" + hash + "，生成期：" + expectedProtocolHash + "）");
            });

            // 非空断言：本配置下必须真的发生过 CLI 调用与驱动执行
            Check(_weaverInvocations >= 4, "本配置下 PMNetWeaver CLI 至少被调用 4 次");
        }

        // =====================================================================================
        //  负例族
        // =====================================================================================

        private static void RunNegativeChains(
            string repoRoot,
            string workDir,
            string runtimeDir,
            SortedDictionary<string, string> generated)
        {
            // 所有注入都只改 **%TEMP% 沙箱副本**里的生成物文本（每个变体一个独立目录），
            // 仓库里的生成器与夹具源码一行不动。

            // (a) 未知版本：版本方法返回 2
            NegativeVariant(repoRoot, workDir, runtimeDir, generated, "noversion",
                "未知版本",
                delegate (string text) { return ReplaceOnce(text, "return 0;", "return 2;", "版本方法改为返回 2"); },
                "未知版本");

            // (b) 假 stamp：版本方法直接返回 1，但从未编织 ⇒ 结构校验必须抓住
            NegativeVariant(repoRoot, workDir, runtimeDir, generated, "falsestamp",
                "假 stamp（未编织却说 1）",
                delegate (string text) { return ReplaceOnce(text, "return 0;", "return 1;", "版本方法改为返回 1"); },
                "声称已编织");

            // (c) 缺 guard：删掉版本方法，并把守卫对它的引用断掉（否则夹具编不过，
            //     失败的就成了 “C# 编译错误” 而不是我们要验的那条预检）
            NegativeVariant(repoRoot, workDir, runtimeDir, generated, "noguard",
                "缺 guard（有 RPC 无生成版本方法）",
                delegate (string text)
                {
                    string versionMethod =
                        "        internal static int PMNet_GetRpcWeaveVersion()\r\n"
                        + "        {\r\n"
                        + "            return 0;\r\n"
                        + "        }\r\n\r\n";
                    string withoutVersion = ReplaceOnce(text, versionMethod, string.Empty, "删除版本方法");
                    return ReplaceOnce(withoutVersion, "if (PMNet_GetRpcWeaveVersion() != 1)", "if (false)",
                        "断开守卫对版本方法的引用");
                },
                "缺少生成物方法");

            // (d) BuildEntry 缺编织门：版本/守卫都在，但注册入口没调 ⇒ 未编织程序集仍能注册
            NegativeVariant(repoRoot, workDir, runtimeDir, generated, "nogate",
                "BuildEntry 缺 gate（注册入口没调 Require）",
                delegate (string text)
                {
                    return ReplaceOnce(text,
                        "            PMNet_RequireRpcWeave();\r\n\r\n            PMNet.PMPropertyDescriptor[] props",
                        "            PMNet.PMPropertyDescriptor[] props",
                        "删除 BuildEntry 首行的 guard 调用");
                },
                "没有调用 PMNet_RequireRpcWeave");

            // (e) 损坏收包 helper：把唯一业务调用删掉
            NegativeVariant(repoRoot, workDir, runtimeDir, generated, "brokenrecv",
                "损坏收包 helper（不调用业务方法）",
                delegate (string text)
                {
                    return ReplaceOnce(text, "            self.ServerAttack(p0);",
                        "            // 收包调用被故意删掉", "删除收包 helper 的业务调用");
                },
                "收包 helper");

            // (f) 损坏发送 helper：把唯一业务调用复制成两次
            NegativeVariant(repoRoot, workDir, runtimeDir, generated, "brokensend",
                "损坏发送 helper（调用业务方法两次）",
                delegate (string text)
                {
                    // 先用**精确缩进**的锚点确认恰好一处（旧版本的 12 空格锚点会因
                    // “12 空格是 16 空格的后缀”而命中错位置，导致注入点不对）
                    string marker = "                ServerAttack(p0);";
                    int count = CountOccurrences(text, marker);
                    if (count != 1)
                    {
                        throw new InvalidOperationException(
                            "发送 helper 的业务调用锤点命中 " + count + " 次（期望 1）：生成器格式已变化。");
                    }

                    int index = text.IndexOf(marker, StringComparison.Ordinal);
                    return text.Substring(0, index) + marker + "\r\n" + text.Substring(index);
                },
                "发送 helper");

            // (g) 强名称（公钥）程序集必须被拒
            StrongNameVariant(repoRoot, workDir, runtimeDir, generated);

            // (g) 无 RPC 的程序集：--weave 成功但不写盘；--require-rpcs 必须失败
            Section("8. 无 RPC 输入：--weave 成功且不写盘；--require-rpcs 必须失败", delegate ()
            {
                string runtimeDll = Path.Combine(runtimeDir, "PMNet.Runtime.Temp.dll");
                Check(File.Exists(runtimeDll), "运行时 DLL 存在（充当无 RPC 输入）");

                string before = Sha256(runtimeDll);
                ProcessResult weave = RunWeaver(new string[] { "--weave", runtimeDll });
                Check(weave.ExitCode == 0, "无 RPC 时 --weave 退出码 = " + weave.ExitCode + "\n" + weave.Combined());
                Check(weave.StdOut.Contains("是否实际改写：否"), "无 RPC 时报告未改写");
                Check(weave.StdOut.Contains("RPC 方法数：0"), "无 RPC 时报告 RPC 方法数 0");
                Check(Sha256(runtimeDll) == before, "无 RPC 时未写盘（哈希不变）");

                ProcessResult require = RunWeaver(new string[] { "--weave", runtimeDll, "--require-rpcs" });
                Check(require.ExitCode != 0, "--require-rpcs 在无 RPC 时必须失败（实际 " + require.ExitCode + "）");
                Check(Sha256(runtimeDll) == before, "--require-rpcs 失败后仍未写盘");
            });

            // (h) 无 PDB 的夹具：编织仍须成功，且不得凭空造 PDB
            Section("9. 无 PDB 夹具（DebugType=none）：编织成功且不造 PDB", delegate ()
            {
                string variantDir = Path.Combine(workDir, "nosymbols");
                string outDir = Path.Combine(variantDir, "out");
                WriteVariant(variantDir, runtimeDir, repoRoot, generated, "none");
                BuildFixture(variantDir, outDir, "Debug");

                string dll = Path.Combine(outDir, "PMWeaveFixture.dll");
                string pdb = Path.Combine(outDir, "PMWeaveFixture.pdb");
                Check(File.Exists(dll), "无符号夹具 DLL 已生成");
                Check(!File.Exists(pdb), "无符号夹具确实没有 PDB");

                string before = Sha256(dll);
                ProcessResult weave = RunWeaver(new string[] { "--weave", dll, "--reference-dir", runtimeDir });
                Check(weave.ExitCode == 0, "无 PDB 时 --weave 退出码 = " + weave.ExitCode + "\n" + weave.Combined());
                Check(Sha256(dll) != before, "无 PDB 时 DLL 确实被改写");
                Check(!File.Exists(pdb), "无 PDB 时不得凭空产生 PDB");

                string[] lines = RunDriver(dll, runtimeDir, "RunWoven", true);
                CheckDriver(lines, WovenCases, "woven[nosymbols]");
            });

            // (i) 非 PE 输入必须明确失败（不是未捕获异常崩溃）
            Section("10. 非 PE / 截断输入必须明确失败", delegate ()
            {
                string garbage = Path.Combine(workDir, "garbage.dll");
                File.WriteAllBytes(garbage, Encoding.ASCII.GetBytes("this is not a PE file, and it is not even close"));

                ProcessResult result = RunWeaver(new string[] { "--weave", garbage });
                Check(result.ExitCode != 0, "垃圾输入 --weave 必须失败（实际 " + result.ExitCode + "）");
                Check(result.StdErr.Contains("失败"), "垃圾输入给出明确失败信息");
                Check(Sha256(garbage) == Sha256(garbage), "垃圾输入未被改写");

                ProcessResult missing = RunWeaver(new string[] { "--weave", Path.Combine(workDir, "not-exist.dll") });
                Check(missing.ExitCode != 0, "不存在的输入必须失败");
            });

            // (j) --help 必须是成功退出
            Section("11. --help 成功退出并打印用法", delegate ()
            {
                ProcessResult help = RunWeaver(new string[] { "--help" });
                Check(help.ExitCode == 0, "--help 退出码 = " + help.ExitCode);
                Check(help.StdOut.Contains("--weave") && help.StdOut.Contains("--check")
                      && help.StdOut.Contains("--require-rpcs"),
                    "--help 打印冻结 CLI 契约");

                ProcessResult usage = RunWeaver(new string[] { "--weave" });
                Check(usage.ExitCode == 2, "--weave 缺路径时用法错误退出码 = " + usage.ExitCode + "（期望 2）");
            });
        }

        private static void NegativeVariant(
            string repoRoot,
            string workDir,
            string runtimeDir,
            SortedDictionary<string, string> generated,
            string name,
            string what,
            Func<string, string> classTransform,
            string expectedKeyword)
        {
            Section("N. " + what + " ⇒ 必须明确失败且零写", delegate ()
            {
                string variantDir = Path.Combine(workDir, name);
                string outDir = Path.Combine(variantDir, "out");

                // 注入只发生在 **%TEMP% 沙箱副本**上（每个变体一个独立目录）。
                IDictionary<string, string> patched = classTransform == null
                    ? generated
                    : TransformClassFile(generated, classTransform);

                WriteVariant(variantDir, runtimeDir, repoRoot, patched, null);
                BuildFixture(variantDir, outDir, "Debug");

                string dll = Path.Combine(outDir, "PMWeaveFixture.dll");
                string pdb = Path.Combine(outDir, "PMWeaveFixture.pdb");
                if (!File.Exists(dll))
                {
                    Check(false, what + "：夹具未编译出来");
                    return;
                }

                string dllBefore = Sha256(dll);
                string pdbBefore = File.Exists(pdb) ? Sha256(pdb) : null;

                ProcessResult weave = RunWeaver(new string[] { "--weave", dll, "--reference-dir", runtimeDir });
                Check(weave.ExitCode != 0, what + "：--weave 必须失败（实际退出码 " + weave.ExitCode + "）");
                Check(weave.StdErr.Length > 0, what + "：失败信息非空");
                Console.WriteLine("  失败信息：" + FirstLine(weave.StdErr));

                // 断言“失败的是**预期的那条检查**”，而不是撞上了别的错误。
                if (!string.IsNullOrEmpty(expectedKeyword))
                {
                    Check(weave.StdErr.Contains(expectedKeyword),
                        what + "：失败必须由预期检查触发（期望关键词 [" + expectedKeyword + "]，实际："
                        + FirstLine(weave.StdErr) + "）");
                }

                Check(Sha256(dll) == dllBefore, what + "：预检失败后 DLL 哈希不变（零写）");
                if (pdbBefore != null)
                {
                    Check(Sha256(pdb) == pdbBefore, what + "：预检失败后 PDB 哈希不变（零写）");
                }

                ProcessResult check = RunWeaver(new string[] { "--check", dll });
                Check(check.ExitCode != 0, what + "：--check 也必须失败");
                Check(Sha256(dll) == dllBefore, what + "：--check 失败后 DLL 哈希仍不变");
            });
        }

        private static void StrongNameVariant(
            string repoRoot,
            string workDir,
            string runtimeDir,
            SortedDictionary<string, string> generated)
        {
            Section("N. 强名称（公钥）程序集 ⇒ 必须明确拒绝且零写", delegate ()
            {
                string variantDir = Path.Combine(workDir, "strongname");
                string outDir = Path.Combine(variantDir, "out");

                // 用运行期生成的一对 RSA 公钥做 PublicSign：产物带公钥、但不需要私钥签名，
                // 正好用来验证“带公钥的输入一律拒绝”这条预检。
                string keyPath = Path.Combine(variantDir, "pub.snk");
                Directory.CreateDirectory(variantDir);
                File.WriteAllBytes(keyPath, BuildCspPublicKeyBlob());

                IDictionary<string, string> frozen = generated;
                WriteVariant(variantDir, runtimeDir, repoRoot, frozen, null, keyPath);
                BuildFixture(variantDir, outDir, "Debug");

                string dll = Path.Combine(outDir, "PMWeaveFixture.dll");
                if (!File.Exists(dll))
                {
                    Check(false, "强名称变体：夹具未编译出来（PublicSign 失败？）");
                    return;
                }

                bool hasPublicKey = ReadHasPublicKey(dll);
                Check(hasPublicKey, "强名称变体确实带公钥（HasPublicKey=true）");

                string before = Sha256(dll);
                ProcessResult weave = RunWeaver(new string[] { "--weave", dll, "--reference-dir", runtimeDir });
                Check(weave.ExitCode != 0, "强名称输入 --weave 必须失败（实际 " + weave.ExitCode + "）");
                Console.WriteLine("  失败信息：" + FirstLine(weave.StdErr));
                Check(weave.StdErr.Contains("公钥") || weave.StdErr.Contains("强名称"),
                    "强名称失败信息提到公钥/强名称（实际：" + FirstLine(weave.StdErr) + "）");
                Check(Sha256(dll) == before, "强名称输入未被改写");
            });
        }

        // =====================================================================================
        //  P3 加固链条
        // =====================================================================================
        //
        // 五组，全部基于**真实编译 + 真编织 + 真执行**：
        //   A. 加固夹具（HardeningTests.cs）：运行时与 RPC **同程序集** + **同名多类 RPC** +
        //      **非 RPC 合法重载**，Debug 与 Release 各一份带 PDB。
        //      这是 P2 BLOCKER（PDB 核对按「类型名.方法名」做键 ⇒ 合法重载被误拒）的回归。
        //   B. guard / 版本语义反例：假 guard（读版本后恒 return 1）、guard 抛错类型不对、
        //      版本方法实际返回 ≠ 常量（`int v = 1; return -v;`）、版本方法含不认识 IL。
        //   C. 实例 gate 反例：字段初始化改成常量（weaver 必须拒 + 运行期 new 真的不再被拒）。
        //   D. --check 的符号核对：回灌旧 PDB / 截断 PDB 必须失败；无 PDB 必须仍然通过。
        //   E. 失败路径 / 原子性 / 并发：PDB 不可替换时两文件必须都回滚、不残留；
        //      锁文件使并发 weave 明确失败；两个真实进程并发后文件仍自洽。

        private static readonly string[] HardeningPreWeaveCases = new string[]
        {
            "hardening-preweave-alpha-version-zero", "hardening-preweave-beta-version-zero",
            "hardening-preweave-alpha-new-blocked", "hardening-preweave-beta-new-blocked",
            "hardening-preweave-register-blocked", "hardening-preweave-registry-empty",
        };

        private static readonly string[] HardeningWovenCases = new string[]
        {
            "hardening-alpha-version-one", "hardening-beta-version-one",
            "hardening-alpha-body", "hardening-beta-body",
            "hardening-register-two-classes",
            "hardening-alpha-same-name-rpc-registered", "hardening-beta-same-name-rpc-registered",
            "hardening-same-name-rpc-own-body",
            "hardening-overload-int", "hardening-overload-string", "hardening-overload-two-args",
            "hardening-woven-require-returns-one",
        };

        private static readonly string[] HardeningNoGateCases = new string[]
        {
            "hardening-nogate-alpha-new-succeeds", "hardening-nogate-beta-new-blocked",
            "hardening-nogate-register-still-blocked",
        };

        private const string HardeningDriverType = "PMWeave.Hardening.HardeningDriver";

        private static void RunHardeningChains(string repoRoot, string workDir, string runtimeDir)
        {
            string hardeningSource = Path.Combine(repoRoot, "Tools", "PMNetWeaverTest", "HardeningTests.cs");
            string genDir = Path.Combine(workDir, "hardening-gen");
            string idLock = Path.Combine(workDir, "hardening-ids.json");
            SortedDictionary<string, string> generated = new SortedDictionary<string, string>(StringComparer.Ordinal);

            Section("12. 加固夹具：现有 PMNetGen 真实生成（2 个含 RPC 的类 + 注册表）", delegate ()
            {
                Check(File.Exists(hardeningSource), "加固夹具源存在：" + hardeningSource);

                ProcessResult gen = RunProcess(
                    DotnetHost(),
                    new string[] { _genDllPath, "--decl-gen", hardeningSource, "--out-dir", genDir, "--id-lock", idLock },
                    repoRoot);

                Check(gen.ExitCode == 0, "PMNetGen --decl-gen（加固夹具）退出码 = " + gen.ExitCode + "\n" + gen.Combined());

                string[] files = Directory.Exists(genDir) ? Directory.GetFiles(genDir, "*.cs") : new string[0];
                Check(files.Length == 3, "加固生成物文件数 = " + files.Length + "（期望 3：两个类 + 注册表）");

                for (int i = 0; i < files.Length; i++)
                {
                    generated[Path.GetFileName(files[i])] = File.ReadAllText(files[i]);
                }

                int classFiles = 0;
                foreach (KeyValuePair<string, string> pair in generated)
                {
                    if (pair.Value.IndexOf("public const ushort PMGeneratedRpcId_", StringComparison.Ordinal) < 0)
                    {
                        continue;
                    }

                    classFiles++;
                    Check(CountOccurrences(pair.Value, "        internal static int PMNet_GetRpcWeaveVersion()") == 1,
                        pair.Key + " 里恰好一个编织版本方法");
                    Check(CountOccurrences(pair.Value, "        private static int PMNet_RequireRpcWeave()") == 1,
                        pair.Key + " 里恰好一个 private guard");
                    Check(CountOccurrences(pair.Value, "        private readonly int PMNet_rpcWeaveGate = PMNet_RequireRpcWeave();") == 1,
                        pair.Key + " 里恰好一个实例 gate 字段");
                }

                Check(classFiles == 2, "加固生成物里有 2 个含 RPC 的类产物（实际 " + classFiles + "）");
            });

            if (classFilesOf(generated) != 2)
            {
                return;
            }

            RunHardeningVariant(repoRoot, workDir, runtimeDir, generated, "hardening-sep-Debug", false, "Debug");
            RunHardeningVariant(repoRoot, workDir, runtimeDir, generated, "hardening-same-Debug", true, "Debug");
            RunHardeningVariant(repoRoot, workDir, runtimeDir, generated, "hardening-same-Release", true, "Release");

            RunHardeningNegativeChains(repoRoot, workDir, runtimeDir, generated);
            RunSymbolChecks(repoRoot, workDir, runtimeDir, generated);
            RunAtomicityChecks(repoRoot, workDir, runtimeDir, generated);

            Section("N. 失败路径不残留暂存/备份文件（全工作区扫描）", delegate ()
            {
                string[] leftovers = FindWeaverLeftovers(workDir);
                Check(leftovers.Length == 0,
                    "工作区内无 *.tmp / *.bak / *.pmweave-* / *.pmweave.lock 残留（实际 " + leftovers.Length
                    + " 个：" + JoinPaths(leftovers) + "）");
            });

            Section("N. 暂存 PDB 必须在 PDB 校验之前就登记进清理列表（源码顺序守卫）", delegate ()
            {
                // 为什么要有这条：旧实现里 `staged.Add(tmpPdb)` 在 VerifyPdbIntegrity **之后**，
                // 而那个校验在真实 E2E 程序集上恰好会抛 ⇒ 失败路径在目标目录留下了
                // `PMNetE2E.dll.pmweave-*.pdb`（本仓库里能直接看到这个残留）。
                // 这条失败在正常操作下已无法从外部触发（校验现在是按 RID 的、不会再误报），
                // 因此用「顺序守卫」把结构钉住，防止被改回去。
                string source = Path.Combine(repoRoot, "Tools", "PMNetWeaver", "RpcAssemblyWeaver.cs");
                Check(File.Exists(source), "weaver 源文件存在：" + source);
                if (!File.Exists(source))
                {
                    return;
                }

                string text = File.ReadAllText(source);
                int registerTmpPdb = text.IndexOf("staged.Add(tmpPdb)", StringComparison.Ordinal);
                int verifyPdbCall = text.IndexOf("VerifyPdb(tmpDll, expectations", StringComparison.Ordinal);
                Check(registerTmpPdb > 0 && verifyPdbCall > registerTmpPdb,
                    "写盘路径里 `staged.Add(tmpPdb)` 必须出现在 `VerifyPdb(tmpDll, …)` 之前（实际 "
                    + registerTmpPdb + " vs " + verifyPdbCall + "）");
            });
        }

        private static int classFilesOf(IDictionary<string, string> generated)
        {
            int count = 0;
            foreach (KeyValuePair<string, string> pair in generated)
            {
                if (pair.Value.IndexOf("public const ushort PMGeneratedRpcId_", StringComparison.Ordinal) >= 0)
                {
                    count++;
                }
            }

            return count;
        }

        private static void RunHardeningVariant(
            string repoRoot,
            string workDir,
            string runtimeDir,
            SortedDictionary<string, string> generated,
            string name,
            bool runtimeInAssembly,
            string configuration)
        {
            string variantDir = Path.Combine(workDir, name);
            string outDir = Path.Combine(variantDir, "out");
            string tag = name + "[" + configuration + "]";

            Section("13. 加固变体 " + tag + "：真实编译（runtimeInAssembly=" + runtimeInAssembly + "，带 PDB）", delegate ()
            {
                WriteHardeningVariant(variantDir, runtimeDir, repoRoot, generated, runtimeInAssembly, configuration);
                BuildFixture(variantDir, outDir, configuration);
                Check(File.Exists(Path.Combine(outDir, "PMHardeningFixture.dll")), tag + " 夹具 DLL 已生成");
                Check(File.Exists(Path.Combine(outDir, "PMHardeningFixture.pdb")), tag + " 夹具 PDB 已生成");
            });

            string dll = Path.Combine(outDir, "PMHardeningFixture.dll");
            string pdb = Path.Combine(outDir, "PMHardeningFixture.pdb");
            if (!File.Exists(dll) || !File.Exists(pdb))
            {
                return;
            }

            Section("14. 加固变体 " + tag + "：同名重载面 + 未编织被拒 + 真编织 + 真执行", delegate ()
            {
                // 先用独立读取器把「旧实现的键」（类型名.方法名，无签名）真的算一遍：
                // 碰撞数必须 > 0，否则这个用例就是空跑（夹具没有真的覆盖那条缺陷的形态）。
                PdbCollisionReport collisions = InspectPdbNameCollisions(dll);
                Check(collisions.CollidingNameKeys > 0,
                    tag + " 夹具确实含同名合法重载（旧 PDB 核对键的碰撞数 = " + collisions.CollidingNameKeys
                    + "，例：" + (collisions.Samples.Count == 0 ? "<无>" : collisions.Samples[0]) + "）");
                Console.WriteLine("  " + tag + " 旧核对键碰撞面：方法行 " + collisions.MethodRows + "，不同名键 "
                                  + collisions.DistinctNameKeys + "，同名不同 SP 条数的键 " + collisions.CollidingNameKeys);
                for (int si = 0; si < collisions.Samples.Count; si++)
                {
                    Console.WriteLine("    collision: " + collisions.Samples[si]);
                }

                string[] pre = RunDriver(dll, runtimeDir, "RunPreWeave", true, HardeningDriverType);
                CheckDriver(pre, HardeningPreWeaveCases, tag + " preweave");

                string dllBefore = Sha256(dll);
                string pdbBefore = Sha256(pdb);

                ProcessResult weave = RunWeaver(new string[]
                {
                    "--weave", dll, "--reference-dir", runtimeDir, "--reference-dir", outDir, "--require-rpcs",
                });
                Check(weave.ExitCode == 0, tag + " --weave 退出码 = " + weave.ExitCode + "\n" + weave.Combined());
                Check(weave.StdOut.Contains("是否实际改写：是"), tag + " 报告实际改写");
                Check(Sha256(dll) != dllBefore && Sha256(pdb) != pdbBefore, tag + " DLL 与 PDB 都被改写");
                Check(FindWeaverLeftovers(outDir).Length == 0,
                    tag + " 成功后无 .tmp/.bak/.lock 残留：" + JoinPaths(FindWeaverLeftovers(outDir)));

                ProcessResult check = RunWeaver(new string[]
                {
                    "--check", dll, "--reference-dir", runtimeDir, "--reference-dir", outDir,
                });
                Check(check.ExitCode == 0, tag + " --check 退出码 = " + check.ExitCode + "\n" + check.Combined());

                string[] woven = RunDriver(dll, runtimeDir, "RunWoven", true, HardeningDriverType);
                CheckDriver(woven, HardeningWovenCases, tag + " woven");
                Check(DriverIdsEqual(pre, woven), tag + " 加固夹具稳定 ID 编织前后逐项一致");

                string afterDll = Sha256(dll);
                ProcessResult again = RunWeaver(new string[]
                {
                    "--weave", dll, "--reference-dir", runtimeDir, "--reference-dir", outDir,
                });
                Check(again.ExitCode == 0 && again.StdOut.Contains("是否实际改写：否"), tag + " 二次 weave 幂等");
                Check(Sha256(dll) == afterDll, tag + " 二次 weave 零 diff");
            });
        }

        private static void RunHardeningNegativeChains(
            string repoRoot,
            string workDir,
            string runtimeDir,
            SortedDictionary<string, string> generated)
        {
            // (a) 假 guard：**读了**版本但忽略结果、恒 return 1。
            //     旧实现只数「有没有调用版本方法」⇒ 会直接放行（运行期未编织程序集不会被拒）。
            HardeningNegativeVariant(
                repoRoot, workDir, runtimeDir, generated,
                "hard-guard-fake",
                "假 guard（读版本后恒 return 1）",
                delegate (string fileName, string text)
                {
                    return ReplaceOnce(text, "if (PMNet_GetRpcWeaveVersion() != 1)",
                        "PMNet_GetRpcWeaveVersion();\r\n            if (false)",
                        "把 guard 改成读版本但恒不抛（" + fileName + "）");
                },
                "假 guard");

            // (b) guard 抛的是别的异常类型（契约指定 System.InvalidOperationException）。
            HardeningNegativeVariant(
                repoRoot, workDir, runtimeDir, generated,
                "hard-guard-wrong-exception",
                "guard 抛错类型不对（System.Exception）",
                delegate (string fileName, string text)
                {
                    return ReplaceOnce(text, "throw new System.InvalidOperationException(",
                        "throw new System.Exception(", "把 guard 的异常类型换成 System.Exception（" + fileName + "）");
                },
                "InvalidOperationException");

            // (c) 版本方法**实际返回 ≠ 常量**：`int v = 1; return -v;` 满足旧实现的
            //     「1 个 ret / 无调用 / 1 个整型常量」不变式，实际返回 -1。
            HardeningNegativeVariant(
                repoRoot, workDir, runtimeDir, generated,
                "hard-version-computed",
                "版本方法实际返回 ≠ 常量（int v = 1; return -v;）",
                delegate (string fileName, string text)
                {
                    string versionMethod =
                        "        internal static int PMNet_GetRpcWeaveVersion()\r\n"
                        + "        {\r\n"
                        + "            return 0;\r\n"
                        + "        }";
                    string replaced =
                        "        internal static int PMNet_GetRpcWeaveVersion()\r\n"
                        + "        {\r\n"
                        + "            int v = 1;\r\n"
                        + "            return -v;\r\n"
                        + "        }";
                    return ReplaceOnce(text, versionMethod, replaced, "版本方法改成计算值（" + fileName + "）");
                },
                "未知版本");

            // (d) 版本方法含求值器不认识的指令（newarr）⇒ 必须明确拒绝，不能猜一个值。
            HardeningNegativeVariant(
                repoRoot, workDir, runtimeDir, generated,
                "hard-version-unknown-il",
                "版本方法含未支持的 IL（newarr）",
                delegate (string fileName, string text)
                {
                    string versionMethod =
                        "        internal static int PMNet_GetRpcWeaveVersion()\r\n"
                        + "        {\r\n"
                        + "            return 0;\r\n"
                        + "        }";
                    string replaced =
                        "        internal static int PMNet_GetRpcWeaveVersion()\r\n"
                        + "        {\r\n"
                        + "            int[] probe = new int[1];\r\n"
                        + "            return probe.Length;\r\n"
                        + "        }";
                    return ReplaceOnce(text, versionMethod, replaced, "版本方法改成 newarr（" + fileName + "）");
                },
                "不支持的指令");

            // (e) 实例 gate 失效：字段初始化改成常量。
            //     这里额外用**真实运行**证明这就是被放过的反例：new 不再被拒（注册仍被拒）。
            Section("N. 实例 gate 失效（字段初始化改成常量） ⇒ weaver 必须拒 + 运行期 new 真的不再被拒", delegate ()
            {
                string variantDir = Path.Combine(workDir, "hard-gate-disabled");
                string outDir = Path.Combine(variantDir, "out");

                IDictionary<string, string> patched = HardeningTransform(generated, delegate (string fileName, string text)
                {
                    if (!string.Equals(fileName, "PMNet.PMWeave.Hardening.HardeningAlpha.g.cs", StringComparison.Ordinal))
                    {
                        return text;
                    }

                    return ReplaceOnce(text, "private readonly int PMNet_rpcWeaveGate = PMNet_RequireRpcWeave();",
                        "private readonly int PMNet_rpcWeaveGate = 1;", "把 HardeningAlpha 的 gate 字段初始化改成常量");
                });

                WriteHardeningVariant(variantDir, runtimeDir, repoRoot, patched, false, "Debug");
                BuildFixture(variantDir, outDir, "Debug");

                string dll = Path.Combine(outDir, "PMHardeningFixture.dll");
                if (!File.Exists(dll))
                {
                    Check(false, "实例 gate 反例：夹具未编译出来");
                    return;
                }

                // 运行期证据：字段初始化被改掉后，new HardeningAlpha() 不再被拒；
                // 注册入口的 guard 仍在 ⇒ 注册仍被拒（半防御）。
                string[] lines = RunDriver(dll, runtimeDir, "RunPreWeaveGateDisabled", true, HardeningDriverType);
                CheckDriver(lines, HardeningNoGateCases, "hard-gate-runtime");

                string dllBefore = Sha256(dll);
                ProcessResult weave = RunWeaver(new string[]
                {
                    "--weave", dll, "--reference-dir", runtimeDir, "--reference-dir", outDir,
                });
                Check(weave.ExitCode != 0, "实例 gate 失效：--weave 必须失败（实际 " + weave.ExitCode + "）");
                Console.WriteLine("  失败信息：" + FirstLine(weave.StdErr));
                Check(weave.StdErr.Contains("实例 gate"),
                    "失败必须由实例 gate 检查触发（实际：" + FirstLine(weave.StdErr) + "）");
                Check(Sha256(dll) == dllBefore, "实例 gate 失效：预检失败后 DLL 哈希不变（零写）");
                Check(FindWeaverLeftovers(outDir).Length == 0,
                    "实例 gate 失效：失败后无残留：" + JoinPaths(FindWeaverLeftovers(outDir)));
            });
        }

        private static void HardeningNegativeVariant(
            string repoRoot,
            string workDir,
            string runtimeDir,
            SortedDictionary<string, string> generated,
            string name,
            string what,
            Func<string, string, string> transform,
            string expectedKeyword)
        {
            Section("N. " + what + " ⇒ 必须明确失败且零写", delegate ()
            {
                string variantDir = Path.Combine(workDir, name);
                string outDir = Path.Combine(variantDir, "out");
                IDictionary<string, string> patched = HardeningTransform(generated, transform);

                WriteHardeningVariant(variantDir, runtimeDir, repoRoot, patched, false, "Debug");
                BuildFixture(variantDir, outDir, "Debug");

                string dll = Path.Combine(outDir, "PMHardeningFixture.dll");
                string pdb = Path.Combine(outDir, "PMHardeningFixture.pdb");
                if (!File.Exists(dll))
                {
                    Check(false, what + "：夹具未编译出来");
                    return;
                }

                string dllBefore = Sha256(dll);
                string pdbBefore = File.Exists(pdb) ? Sha256(pdb) : null;

                ProcessResult weave = RunWeaver(new string[]
                {
                    "--weave", dll, "--reference-dir", runtimeDir, "--reference-dir", outDir,
                });
                Check(weave.ExitCode != 0, what + "：--weave 必须失败（实际退出码 " + weave.ExitCode + "）");
                Console.WriteLine("  失败信息：" + FirstLine(weave.StdErr));

                if (!string.IsNullOrEmpty(expectedKeyword))
                {
                    Check(weave.StdErr.Contains(expectedKeyword),
                        what + "：失败必须由预期检查触发（期望关键词 [" + expectedKeyword + "]，实际："
                        + FirstLine(weave.StdErr) + "）");
                }

                Check(Sha256(dll) == dllBefore, what + "：预检失败后 DLL 哈希不变（零写）");
                if (pdbBefore != null)
                {
                    Check(Sha256(pdb) == pdbBefore, what + "：预检失败后 PDB 哈希不变（零写）");
                }

                Check(FindWeaverLeftovers(outDir).Length == 0,
                    what + "：失败后无残留：" + JoinPaths(FindWeaverLeftovers(outDir)));

                ProcessResult check = RunWeaver(new string[] { "--check", dll });
                Check(check.ExitCode != 0, what + "：--check 也必须失败");
            });
        }

        private static void RunSymbolChecks(
            string repoRoot,
            string workDir,
            string runtimeDir,
            SortedDictionary<string, string> generated)
        {
            string variantDir = Path.Combine(workDir, "symbolcheck");
            string outDir = Path.Combine(variantDir, "out");
            string dll = Path.Combine(outDir, "PMHardeningFixture.dll");
            string pdb = Path.ChangeExtension(dll, ".pdb");
            string unwovenPdb = Path.Combine(variantDir, "unwoven.pdb");
            string wovenPdb = Path.Combine(variantDir, "woven.pdb");

            Section("15. --check 的符号核对（旧 PDB 回灌 / 截断 PDB / 无 PDB）", delegate ()
            {
                WriteHardeningVariant(variantDir, runtimeDir, repoRoot, generated, false, "Debug");
                BuildFixture(variantDir, outDir, "Debug");

                if (!File.Exists(dll) || !File.Exists(pdb))
                {
                    Check(false, "符号核对：夹具未编译出来");
                    return;
                }

                File.Copy(pdb, unwovenPdb, true);

                ProcessResult weave = RunWeaver(new string[]
                {
                    "--weave", dll, "--reference-dir", runtimeDir, "--reference-dir", outDir,
                });
                Check(weave.ExitCode == 0, "符号核对：先织好（退出码 " + weave.ExitCode + "）\n" + weave.Combined());

                ProcessResult wovenCheck = RunWeaver(new string[] { "--check", dll });
                Check(wovenCheck.ExitCode == 0,
                    "织好的 DLL + 写出的 PDB：--check 通过（旧实现这里也通过）\n" + wovenCheck.Combined());

                // 1) 无 PDB：按契约允许（无符号可工作），--check 必须仍然通过。
                File.Move(pdb, wovenPdb);
                ProcessResult noPdb = RunWeaver(new string[] { "--check", dll });
                Check(noPdb.ExitCode == 0, "无 PDB 时 --check 必须通过（无符号可工作）\n" + noPdb.Combined());

                // 2) 回灌**编织前**的 PDB：DLL 与 PDB 不对应，--check 必须失败。
                //    注：这一例在 Cecil 读符号时就会被「符号与程序集不匹配」拦住（旧实现也拦得住），
                //    所以关键字只要求提到 符号/PDB —— 它证明的是「没被改弱」。
                File.Copy(unwovenPdb, pdb, true);
                ProcessResult stale = RunWeaver(new string[] { "--check", dll });
                Check(stale.ExitCode != 0, "回灌编织前 PDB 后 --check 必须失败（实际 " + stale.ExitCode + "）");
                Console.WriteLine("  失败信息：" + FirstLine(stale.StdErr));
                Check(stale.StdErr.Contains("符号") || stale.StdErr.Contains("PDB"),
                    "失败信息提到 符号/PDB（实际：" + FirstLine(stale.StdErr) + "）");

                // 3) ★ 关键：把 DLL 里的 PDB GUID **改成编织前那份 PDB 的 GUID**，
                //    让 Cecil 的「符号与程序集匹配」检查通过 —— 于是「读得进、但方法级完全不对应」
                //    的 PDB 不再被读入阶段拦住。这正是旧实现 --check 的假绿（实测旧二进制 exit 0），
                //    也是新增的内容核对层要拦的形态。
                byte[] wovenId = ReadPortablePdbId(wovenPdb);
                byte[] unwovenId = ReadPortablePdbId(unwovenPdb);
                Check(wovenId != null && unwovenId != null && wovenId.Length == 20 && unwovenId.Length == 20,
                    "能独立读出两份 PDB 的 20 字节标识（woven=" + (wovenId == null ? "<null>" : wovenId.Length + "B")
                    + "，unwoven=" + (unwovenId == null ? "<null>" : unwovenId.Length + "B") + "）");

                string patchedDll = Path.Combine(variantDir, "patched.dll");
                File.Copy(dll, patchedDll, true);
                File.Copy(unwovenPdb, Path.ChangeExtension(patchedDll, ".pdb"), true);

                // Cecil 只比 20 字节标识里的前 16 字节（GUID）；年龄/戳不参与比较。
                byte[] fromGuid = Take16(wovenId);
                byte[] toGuid = Take16(unwovenId);
                int patched = PatchDllCodeViewGuid(patchedDll, fromGuid, toGuid);
                Check(patched >= 1, "DLL 的 CodeView GUID 被改成编织前那份（命中 " + patched + " 处）");

                ProcessResult guidPatched = RunWeaver(new string[] { "--check", patchedDll });
                Check(guidPatched.ExitCode != 0,
                    "GUID 改为编织前 PDB 后 --check 必须失败（旧实现会 exit 0 假绿；实际 " + guidPatched.ExitCode + "）");
                Console.WriteLine("  失败信息：" + FirstLine(guidPatched.StdErr));
                Check(guidPatched.StdErr.Contains("逐方法对应"),
                    "失败必须由**新增的 DLL/PDB 内容核对**触发（实际：" + FirstLine(guidPatched.StdErr) + "）");

                // 4) 截断 PDB：--check 必须失败（不能当“符号完好”放行）。
                byte[] wovenPdbBytes = File.ReadAllBytes(wovenPdb);
                File.WriteAllBytes(pdb, wovenPdbBytes.Take(wovenPdbBytes.Length / 3).ToArray());
                ProcessResult truncated = RunWeaver(new string[] { "--check", dll });
                Check(truncated.ExitCode != 0, "截断 PDB 后 --check 必须失败（实际 " + truncated.ExitCode + "）");
                Console.WriteLine("  失败信息：" + FirstLine(truncated.StdErr));
            });
        }

        /// <summary>取 20 字节 PDB 标识的前 16 字节（Cecil 比较用的 GUID）。</summary>
        private static byte[] Take16(byte[] value)
        {
            if (value == null || value.Length < 16)
            {
                return null;
            }

            byte[] result = new byte[16];
            Array.Copy(value, result, 16);
            return result;
        }

        /// <summary>独立读出 portable PDB 的 20 字节标识（System.Reflection.Metadata）。</summary>
        private static byte[] ReadPortablePdbId(string pdbPath)
        {
            using (FileStream stream = File.OpenRead(pdbPath))
            {
                using (MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(stream))
                {
                    MetadataReader reader = provider.GetMetadataReader();
                    DebugMetadataHeader header = reader.DebugMetadataHeader;
                    return header == null ? null : header.Id.ToArray();
                }
            }
        }

        /// <summary>
        /// 把 DLL 里出现的 fromId（20 字节 = GUID16 + 4 字节年龄/戳）换成 toId。
        /// 用途：让另一份 PDB 骗过 Cecil 的「符号与程序集匹配」检查（它只比 GUID），
        /// 从而构造出「读得进但内容不对应」的 PDB（新增内容核对的真实反例）。
        /// 返回实际替换的处数。
        /// </summary>
        private static int PatchDllCodeViewGuid(string dllPath, byte[] fromId, byte[] toId)
        {
            if (fromId == null || toId == null || fromId.Length != toId.Length)
            {
                return 0;
            }

            byte[] bytes = File.ReadAllBytes(dllPath);
            int patched = 0;
            for (int i = 0; i <= bytes.Length - fromId.Length; i++)
            {
                bool equal = true;
                for (int k = 0; k < fromId.Length; k++)
                {
                    if (bytes[i + k] != fromId[k])
                    {
                        equal = false;
                        break;
                    }
                }

                if (!equal)
                {
                    continue;
                }

                for (int k = 0; k < toId.Length; k++)
                {
                    bytes[i + k] = toId[k];
                }

                patched++;
                i += fromId.Length - 1;
            }

            if (patched > 0)
            {
                File.WriteAllBytes(dllPath, bytes);
            }

            return patched;
        }

        private static void RunAtomicityChecks(
            string repoRoot,
            string workDir,
            string runtimeDir,
            SortedDictionary<string, string> generated)
        {
            string variantDir = Path.Combine(workDir, "atomicity");
            string outDir = Path.Combine(variantDir, "out");
            string dll = Path.Combine(outDir, "PMHardeningFixture.dll");
            string pdb = Path.ChangeExtension(dll, ".pdb");

            Section("N. PDB 不可替换（第二个文件提交失败） ⇒ 两文件都回滚、不残留", delegate ()
            {
                WriteHardeningVariant(variantDir, runtimeDir, repoRoot, generated, false, "Debug");
                BuildFixture(variantDir, outDir, "Debug");

                if (!File.Exists(dll) || !File.Exists(pdb))
                {
                    Check(false, "原子性：夹具未编译出来");
                    return;
                }

                string dllBefore = Sha256(dll);
                string pdbBefore = Sha256(pdb);

                // 独占读共享：别人可以**读**（Cecil 能读入符号、备份复制也能成功），
                // 但不能删除/替换（Move 覆盖需要 delete 访问）⇒ 正好命中「第二个文件提交失败」。
                using (FileStream hold = new FileStream(pdb, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    ProcessResult weave = RunWeaver(new string[]
                    {
                        "--weave", dll, "--reference-dir", runtimeDir, "--reference-dir", outDir,
                    });
                    Check(weave.ExitCode != 0, "PDB 不可替换时 --weave 必须失败（实际 " + weave.ExitCode + "）");
                    Console.WriteLine("  失败信息：" + FirstLine(weave.StdErr));
                    Check(weave.StdErr.Contains("回滚"),
                        "失败信息给出回滚结论（实际：" + FirstLine(weave.StdErr) + "）");
                }

                Check(Sha256(dll) == dllBefore, "失败后 DLL 与编织前一致（已回滚，未留下半 DLL）");
                Check(Sha256(pdb) == pdbBefore, "失败后 PDB 与编织前一致（未留下半 PDB）");
                Check(FindWeaverLeftovers(outDir).Length == 0,
                    "失败后无残留：" + JoinPaths(FindWeaverLeftovers(outDir)));

                ProcessResult check = RunWeaver(new string[] { "--check", dll });
                Check(check.ExitCode != 0, "失败后仍是未编织状态（--check 失败）");
            });

            Section("N. 并发 weave 锁 ⇒ 后到者明确失败，而不是交错替换", delegate ()
            {
                string lockPath = dll + ".pmweave.lock";
                string dllBefore = Sha256(dll);

                try
                {
                    using (FileStream hold = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                        ProcessResult weave = RunWeaver(new string[]
                        {
                            "--weave", dll, "--reference-dir", runtimeDir, "--reference-dir", outDir,
                        });
                        Check(weave.ExitCode != 0, "锁被占用时 --weave 必须失败（实际 " + weave.ExitCode + "）");
                        Check(weave.StdErr.Contains("正在处理同一个程序集"),
                            "失败信息点明并发（实际：" + FirstLine(weave.StdErr) + "）");
                    }
                }
                finally
                {
                    try
                    {
                        if (File.Exists(lockPath))
                        {
                            File.Delete(lockPath);
                        }
                    }
                    catch (Exception)
                    {
                        // 清理失败不影响结论（下一次运行仍可拿锁）。
                    }
                }

                Check(Sha256(dll) == dllBefore, "锁冲突失败后 DLL 未被触碰");

                ProcessResult weave2 = RunWeaver(new string[]
                {
                    "--weave", dll, "--reference-dir", runtimeDir, "--reference-dir", outDir,
                });
                Check(weave2.ExitCode == 0, "释放锁后 --weave 成功（实际 " + weave2.ExitCode + "）\n" + weave2.Combined());
                Check(FindWeaverLeftovers(outDir).Length == 0,
                    "成功后无残留：" + JoinPaths(FindWeaverLeftovers(outDir)));
            });

            Section("N. 两个真实进程并发 weave 同一程序集 ⇒ 文件仍自洽、无残留", delegate ()
            {
                string concurrentDir = Path.Combine(workDir, "concurrent");
                string concurrentOut = Path.Combine(concurrentDir, "out");
                WriteHardeningVariant(concurrentDir, runtimeDir, repoRoot, generated, false, "Debug");
                BuildFixture(concurrentDir, concurrentOut, "Debug");

                string target = Path.Combine(concurrentOut, "PMHardeningFixture.dll");
                if (!File.Exists(target))
                {
                    Check(false, "并发：夹具未编译出来");
                    return;
                }

                string before = Sha256(target);
                string beforePdb = Sha256(Path.ChangeExtension(target, ".pdb"));

                string[] args = new string[]
                {
                    _weaverDllPath, "--weave", target, "--reference-dir", runtimeDir, "--reference-dir", concurrentOut,
                };

                // 两个进程**同时**启动（不靠 sleep 猜时序），再一起等结束。
                ProcessResult[] pair = RunConcurrently(args, concurrentDir, 2);
                ProcessResult first = pair[0];
                ProcessResult second = pair[1];

                Check(first.ExitCode == 0 || first.ExitCode == 1,
                    "并发 A 退出码 ∈ {0,1}（实际 " + first.ExitCode + "）");
                Check(second.ExitCode == 0 || second.ExitCode == 1,
                    "并发 B 退出码 ∈ {0,1}（实际 " + second.ExitCode + "）");
                Check(first.ExitCode == 0 || second.ExitCode == 0,
                    "并发后至少一个进程成功（A=" + first.ExitCode + "，B=" + second.ExitCode + "）");
                Check(!first.Combined().Contains("未预期异常") && !second.Combined().Contains("未预期异常"),
                    "并发时不得出现未预期异常");

                Check(FindWeaverLeftovers(concurrentOut).Length == 0,
                    "并发后无残留：" + JoinPaths(FindWeaverLeftovers(concurrentOut)));

                ProcessResult check = RunWeaver(new string[] { "--check", target });
                string after = Sha256(target);
                string afterPdb = Sha256(Path.ChangeExtension(target, ".pdb"));
                bool untouched = after == before && afterPdb == beforePdb;
                Check(check.ExitCode == 0 || untouched,
                    "并发后文件自洽：要么两文件都未变（untouched=" + untouched + "），要么 --check 通过（实际 "
                    + check.ExitCode + "）\n" + check.Combined());
            });
        }

        /// <summary>
        /// 加固生成物里「只对某个类文件」做文本注入（文件名参与判断）。
        /// 每个类文件的锚点都必须**恰好命中一次**，否则当场抛错（不允许静默跳过）。
        /// </summary>
        private static IDictionary<string, string> HardeningTransform(
            IDictionary<string, string> generated,
            Func<string, string, string> transform)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> pair in generated)
            {
                bool isClassFile = pair.Value.IndexOf("public const ushort PMGeneratedRpcId_", StringComparison.Ordinal) >= 0;
                result[pair.Key] = isClassFile ? transform(pair.Key, pair.Value) : pair.Value;
            }

            return result;
        }

        private static string JoinPaths(string[] paths)
        {
            if (paths == null || paths.Length == 0)
            {
                return "<无>";
            }

            return string.Join("; ", paths);
        }

        /// <summary>找出目录下 weaver 可能遗留的暂存/备份/锁文件（成功与失败路径都必须是 0）。</summary>
        private static string[] FindWeaverLeftovers(string directory)
        {
            List<string> hits = new List<string>();
            if (!Directory.Exists(directory))
            {
                return hits.ToArray();
            }

            string[] files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
            for (int i = 0; i < files.Length; i++)
            {
                string file = Path.GetFileName(files[i]);
                if (file.EndsWith(".tmp", StringComparison.Ordinal)
                    || file.EndsWith(".bak", StringComparison.Ordinal)
                    || file.EndsWith(".pmweave.lock", StringComparison.Ordinal)
                    || file.IndexOf(".pmweave-", StringComparison.Ordinal) >= 0)
                {
                    hits.Add(files[i]);
                }
            }

            return hits.ToArray();
        }

        private static Dictionary<string, string> CollectIdLines(string[] lines)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("ID ", StringComparison.Ordinal))
                {
                    continue;
                }

                int space = lines[i].IndexOf(' ', 3);
                if (space < 0)
                {
                    continue;
                }

                values[lines[i].Substring(3, space - 3)] = lines[i].Substring(space + 1);
            }

            return values;
        }

        private static bool DriverIdsEqual(string[] before, string[] after)
        {
            Dictionary<string, string> a = CollectIdLines(before);
            Dictionary<string, string> b = CollectIdLines(after);
            if (a.Count == 0 || a.Count != b.Count)
            {
                return false;
            }

            foreach (KeyValuePair<string, string> pair in a)
            {
                string value;
                if (!b.TryGetValue(pair.Key, out value) || value != pair.Value)
                {
                    return false;
                }
            }

            return true;
        }

        // =====================================================================================
        //  冻结格式补丁（模拟生成器未来会产出的 v1 形态）
        // =====================================================================================

        private static readonly string[] RpcMethodNames = new string[]
        {
            "ServerAttack", "ServerProbe", "ServerWarp", "ClientNotify", "MulticastPush", "ServerBranch", "ServerShout",
        };

        /// <summary>
        /// 断言**真实生成物**已经是冻结格式 v1（契约 net-rpc-weaving-contract.md §2）：
        ///   - 发送 helper 是 private（业务改调普通名 M，编织后 M 就是网络入口）；
        ///   - 版本方法返回 0；
        ///   - 守卫真的读版本并抛 System.InvalidOperationException；
        ///   - 实例 readonly gate 字段初始化调用守卫（未编织时 new 也被拒）；
        ///   - PMNet_BuildEntry 的**第一条语句**是 PMNet_RequireRpcWeave()。
        ///
        /// P2 之前这里是把产物文本“补”成冻结格式（用临时字符串插入 guard），
        /// 那会**掩盖生成器本身的回退**：生成器不产出 guard 时补丁把它补上，门禁仍然全绿。
        /// 现在改为“只校验不补”，且每一条锚点都断言恰好命中一次。
        /// 注：Tests 与 PMDeclCheck 第 13 节看的是**同一组文本形状**，
        /// 但这里必须在真实生成物上再验一次：本门禁是拿它去真编译+真编织的那一方。
        /// </summary>
        private static void AssertFrozenFormatGenerated(IDictionary<string, string> generated)
        {
            string classKey = SelectClassFileKey(generated);
            string text = generated[classKey];

            // 1) 发送 helper 必须是 private（且不得还有一个 public 版本）
            // 注：不能用 “public void PMNet_” 的宽匹配 —— 复制属性访问器 `public void PMNet_Set<成员>`
            //     也命中这个前缀（本夹具没有属性，但夹具一改就会变成假红）。逐条 RPC 名字判才准确。
            for (int i = 0; i < RpcMethodNames.Length; i++)
            {
                string marker = "        private void PMNet_" + RpcMethodNames[i] + "(";
                Check(CountOccurrences(text, marker) == 1,
                    "生成物里恰好一个 private 发送 helper：" + RpcMethodNames[i]
                    + "（实际 " + CountOccurrences(text, marker) + " 个）");

                string publicMarker = "        public void PMNet_" + RpcMethodNames[i] + "(";
                Check(CountOccurrences(text, publicMarker) == 0,
                    "发送 helper 不是 public：" + RpcMethodNames[i]);
            }

            // 2) 版本方法：编译前常量 0（恰好一处）
            Check(CountOccurrences(text, "        internal static int PMNet_GetRpcWeaveVersion()\r\n        {\r\n            return 0;\r\n        }") == 1,
                "生成物里恰好一个版本方法且返回 0");
            Check(CountOccurrences(text, "return 0;") == 1,
                "类生成物里 `return 0;` 恰好一处（负例注入靠这条锚点，多一处就会注错位置）");

            // 3) 守卫读版本 + 抛明确异常
            Check(CountOccurrences(text, "        private static int PMNet_RequireRpcWeave()") == 1,
                "生成物里恰好一个 private 守卫方法");
            Check(CountOccurrences(text, "if (PMNet_GetRpcWeaveVersion() != 1)") == 1,
                "守卫真的读取版本方法（不是「假 guard 恒返回 1」）");
            Check(CountOccurrences(text, "throw new System.InvalidOperationException(") == 1,
                "守卫抛 System.InvalidOperationException（未编织时有明确失败信息）");

            // 4) 实例 gate 字段
            Check(CountOccurrences(text, "        private readonly int PMNet_rpcWeaveGate = PMNet_RequireRpcWeave();") == 1,
                "生成物里恰好一个实例 gate 字段，且字段初始化调用守卫");

            // 5) BuildEntry 首句是 guard
            string anchor = "        internal static PMNet.PMNetClassEntry PMNet_BuildEntry()\r\n"
                + "        {\r\n"
                + "            PMNet_RequireRpcWeave();\r\n";
            Check(CountOccurrences(text, anchor) == 1,
                "PMNet_BuildEntry 的第一条语句是 PMNet_RequireRpcWeave()（实际 "
                + CountOccurrences(text, anchor) + " 处）");

            // 6) 注册表侧：所有 BuildEntry 求值都在第一个 RegisterClass 之前（预检 → 登记）
            string registry = generated["PMNetGeneratedRegistry.g.cs"];
            int lastBuildEntry = registry.LastIndexOf(".PMNet_BuildEntry();", StringComparison.Ordinal);
            int firstRegister = registry.IndexOf("RegisterClass(", StringComparison.Ordinal);
            Check(lastBuildEntry >= 0 && firstRegister > lastBuildEntry,
                "RegisterAll 先求值全部 BuildEntry（含 RPC 类的 guard），再统一 RegisterClass");
        }

        /// <summary>
        /// 找出**含 RPC 常量**的那一个类生成物（注册表里没有 PMGeneratedRpcId_）。
        /// 找不到 / 找到多个都硬失败：夹具与生成器契约不一致时不能静默跳过。
        /// </summary>
        private static string SelectClassFileKey(IDictionary<string, string> generated)
        {
            string classKey = null;
            foreach (KeyValuePair<string, string> pair in generated)
            {
                if (pair.Value.IndexOf("public const ushort PMGeneratedRpcId_", StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                if (classKey != null)
                {
                    throw new InvalidOperationException("有多个含 RPC 常量的生成物：" + classKey + " / " + pair.Key);
                }

                classKey = pair.Key;
            }

            if (classKey == null)
            {
                throw new InvalidOperationException("找不到含 PMGeneratedRpcId_ 常量的类生成物（夹具与生成器契约不一致）");
            }

            return classKey;
        }

        /// <summary>只对「网络类」的那一个生成物应用文本变换（其余文件原样返回）。</summary>
        private static Dictionary<string, string> TransformClassFile(
            IDictionary<string, string> generated,
            Func<string, string> transform)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            string classKey = null;
            foreach (KeyValuePair<string, string> pair in generated)
            {
                if (pair.Value.IndexOf("public const ushort PMGeneratedRpcId_", StringComparison.Ordinal) >= 0)
                {
                    classKey = pair.Key;
                }
            }

            if (classKey == null)
            {
                throw new InvalidOperationException("找不到含 PMGeneratedRpcId_ 常量的类生成物");
            }

            foreach (KeyValuePair<string, string> pair in generated)
            {
                result[pair.Key] = pair.Key == classKey ? transform(pair.Value) : pair.Value;
            }

            return result;
        }

        private static string ReplaceOnce(string text, string oldValue, string newValue, string what)        {
            int count = CountOccurrences(text, oldValue);
            if (count != 1)
            {
                throw new InvalidOperationException(
                    what + "：锚点命中 " + count + " 次（期望 1）。锚点=[" + oldValue + "]");
            }

            return text.Replace(oldValue, newValue);
        }

        private static int CountOccurrences(string text, string value)
        {
            int count = 0;
            int index = 0;
            while (true)
            {
                index = text.IndexOf(value, index, StringComparison.Ordinal);
                if (index < 0)
                {
                    return count;
                }

                count++;
                index += value.Length;
            }
        }

        // =====================================================================================
        //  临时工程
        // =====================================================================================

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

        /// <summary>
        /// 把一个变体的夹具工程写到**%TEMP% 沙箱目录**里：
        ///   变体目录/
        ///     fixture.csproj        （现场拼出的临时工程）
        ///     Generated/*.g.cs      （生成物；P2 之后版本/守卫已由生成器产出，不再有手工 guard 文件）
        ///   夹具源文件（Fixture.cs）直接引用仓库里的那份（只读，不复制）。
        /// 仓库里的任何文件都不会被本方法改动。
        /// </summary>
        private static void WriteVariant(
            string variantDir,
            string runtimeDir,
            string repoRoot,
            IDictionary<string, string> generated,
            string debugType,
            string publicKeyPath = null)
        {
            Directory.CreateDirectory(variantDir);
            string genDir = Path.Combine(variantDir, "Generated");
            Directory.CreateDirectory(genDir);

            foreach (KeyValuePair<string, string> pair in generated)
            {
                File.WriteAllText(Path.Combine(genDir, pair.Key), pair.Value, new UTF8Encoding(true));
            }

            string repo = ToMsBuildPath(repoRoot);
            string runtime = ToMsBuildPath(Path.Combine(runtimeDir, "PMNet.Runtime.Temp.dll"));

            StringBuilder item = new StringBuilder();
            item.Append("  <ItemGroup>\r\n");
            item.Append("    <Compile Include=\"").Append(repo).Append("/Tools/PMNetWeaverTest/Fixture.cs\" />\r\n");
            item.Append("    <Compile Include=\"Generated/**/*.cs\" />\r\n");
            item.Append("  </ItemGroup>\r\n");
            item.Append("  <ItemGroup>\r\n");
            item.Append("    <Reference Include=\"PMNet.Runtime.Temp\">\r\n");
            item.Append("      <HintPath>").Append(runtime).Append("</HintPath>\r\n");
            item.Append("    </Reference>\r\n");
            item.Append("  </ItemGroup>\r\n");

            StringBuilder properties = new StringBuilder();
            properties.Append("    <TargetFramework>netstandard2.0</TargetFramework>\r\n");
            properties.Append("    <LangVersion>7.3</LangVersion>\r\n");
            properties.Append("    <AssemblyName>PMWeaveFixture</AssemblyName>\r\n");
            properties.Append("    <RootNamespace>PMWeave.Fixture</RootNamespace>\r\n");
            properties.Append("    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>\r\n");
            properties.Append("    <GenerateDocumentationFile>false</GenerateDocumentationFile>\r\n");
            properties.Append("    <NoWarn>$(NoWarn);CS1591;CS0414</NoWarn>\r\n");
            properties.Append("    <DebugType>").Append(debugType ?? "portable").Append("</DebugType>\r\n");

            if (!string.IsNullOrEmpty(publicKeyPath))
            {
                properties.Append("    <SignAssembly>true</SignAssembly>\r\n");
                properties.Append("    <PublicSign>true</PublicSign>\r\n");
                properties.Append("    <AssemblyOriginatorKeyFile>")
                    .Append(ToMsBuildPath(publicKeyPath)).Append("</AssemblyOriginatorKeyFile>\r\n");
            }

            string csproj =
                "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n" +
                "  <PropertyGroup>\r\n" + properties + "  </PropertyGroup>\r\n" +
                item +
                "</Project>\r\n";

            File.WriteAllText(Path.Combine(variantDir, "fixture.csproj"), csproj, new UTF8Encoding(false));
        }

        private static void BuildFixture(string variantDir, string outDir, string configuration)
        {
            BuildProject(Path.Combine(variantDir, "fixture.csproj"), outDir, configuration, variantDir);
        }

        /// <summary>
        /// 写一个**加固夹具**工程到 %TEMP% 沙箱目录：
        ///   variantDir/
        ///     hardening.csproj
        ///     Generated/*.g.cs            （加固夹具的生成物）
        ///   源文件直接引用仓库里的 Tools/PMNetWeaverTest/HardeningTests.cs（只读，不复制）。
        ///
        /// runtimeInAssembly = true 时，把 Client/Assets/Scripts/PMNet/**/*.cs 也编进**同一个**程序集
        /// ——这就是 Unity / Server 的真实形态（运行时与业务同 DLL，DLL 里天然有同名合法重载），
        /// 也是 P2 报出的 BLOCKER 第一次被触发的形态。false 时改为引用独立的 PMNet.Runtime.Temp。
        /// 仓库里的任何文件都不会被本方法改动。
        /// </summary>
        private static void WriteHardeningVariant(
            string variantDir,
            string runtimeDir,
            string repoRoot,
            IDictionary<string, string> generated,
            bool runtimeInAssembly,
            string configuration)
        {
            Directory.CreateDirectory(variantDir);
            string genDir = Path.Combine(variantDir, "Generated");
            Directory.CreateDirectory(genDir);

            foreach (KeyValuePair<string, string> pair in generated)
            {
                File.WriteAllText(Path.Combine(genDir, pair.Key), pair.Value, new UTF8Encoding(true));
            }

            string repo = ToMsBuildPath(repoRoot);

            StringBuilder properties = new StringBuilder();
            properties.Append("    <TargetFramework>netstandard2.0</TargetFramework>\r\n");
            properties.Append("    <LangVersion>7.3</LangVersion>\r\n");
            properties.Append("    <AssemblyName>PMHardeningFixture</AssemblyName>\r\n");
            properties.Append("    <RootNamespace>PMWeave.Hardening</RootNamespace>\r\n");
            properties.Append("    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>\r\n");
            properties.Append("    <GenerateDocumentationFile>false</GenerateDocumentationFile>\r\n");
            properties.Append("    <NoWarn>$(NoWarn);CS1591;CS0414;CS0162;CS0219</NoWarn>\r\n");
            // 加固夹具 Debug / Release 都必须带 portable PDB（本门禁要的就是「带符号的真实行为」）。
            properties.Append("    <DebugType>portable</DebugType>\r\n");
            properties.Append("    <Configuration>").Append(configuration ?? "Debug").Append("</Configuration>\r\n");

            StringBuilder item = new StringBuilder();
            item.Append("  <ItemGroup>\r\n");
            item.Append("    <Compile Include=\"").Append(repo)
                .Append("/Tools/PMNetWeaverTest/HardeningTests.cs\" />\r\n");
            item.Append("    <Compile Include=\"Generated/**/*.cs\" />\r\n");
            if (runtimeInAssembly)
            {
                item.Append("    <Compile Include=\"").Append(repo).Append("/Client/Assets/Scripts/PMNet/**/*.cs\" ")
                    .Append("Exclude=\"").Append(repo).Append("/Client/Assets/Scripts/PMNet/Generated/**/*.cs\" />\r\n");
            }

            item.Append("  </ItemGroup>\r\n");

            if (!runtimeInAssembly)
            {
                item.Append("  <ItemGroup>\r\n    <Reference Include=\"PMNet.Runtime.Temp\">\r\n      <HintPath>")
                    .Append(ToMsBuildPath(Path.Combine(runtimeDir, "PMNet.Runtime.Temp.dll")))
                    .Append("</HintPath>\r\n    </Reference>\r\n  </ItemGroup>\r\n");
            }

            string csproj =
                "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n"
                + "  <PropertyGroup>\r\n" + properties + "  </PropertyGroup>\r\n"
                + item
                + "</Project>\r\n";

            File.WriteAllText(Path.Combine(variantDir, "fixture.csproj"), csproj, new UTF8Encoding(false));
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

        // =====================================================================================
        //  CLI 调用与加载执行
        // =====================================================================================

        private static ProcessResult RunWeaver(string[] args)
        {
            _weaverInvocations++;
            string weaverDll = _weaverDllPath;
            List<string> all = new List<string>();
            all.Add(weaverDll);
            all.AddRange(args);
            return RunProcess(DotnetHost(), all.ToArray(), Path.GetDirectoryName(weaverDll));
        }

        private static string _weaverDllPath;

        private static string _genDllPath;

        private static int _loadSerial;

        /// <summary>
        /// 把要加载的 DLL（+PDB）拷到一个**全新路径**再交给 AssemblyLoadContext。
        ///
        /// 为什么不能直接从被替换过的路径加载：实测（可用最小实验复现）在本机上，
        /// “先加载旧 DLL → 把同一路径换成新 DLL → 再从新 ALC 按同一路径加载”会拿到**旧内容**。
        /// 因为 Windows 的 ReplaceFile 语义会让目标路径保留原来的文件身份，
        /// 而宿主可能按文件身份缓存已加载的程序集。
        /// 门禁每次用独立路径加载，就与“宿主缓存”完全无关，结论才可信。
        /// </summary>
        private static string MaterializeForLoad(string sourceDll, string loadDir, string stateName)
        {
            Directory.CreateDirectory(loadDir);
            string dest = Path.Combine(loadDir, stateName + "-" + (++_loadSerial) + ".dll");
            File.Copy(sourceDll, dest, true);

            string sourcePdb = Path.ChangeExtension(sourceDll, ".pdb");
            if (File.Exists(sourcePdb))
            {
                File.Copy(sourcePdb, Path.ChangeExtension(dest, ".pdb"), true);
            }

            return dest;
        }

        private static string[] RunDriver(
            string dllPath,
            string runtimeDir,
            string entryName,
            bool copyFirst,
            string driverTypeName = "PMWeave.Fixture.FixtureDriver")
        {
            string loadPath = copyFirst
                ? MaterializeForLoad(dllPath, Path.Combine(Path.GetDirectoryName(dllPath), "load"), "state")
                : dllPath;

            FixtureLoadContext context = new FixtureLoadContext(runtimeDir);
            try
            {
                Assembly assembly = context.LoadFromAssemblyPath(Path.GetFullPath(loadPath));
                Type driver = assembly.GetType(driverTypeName, true);
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
                : base("pmweaver-fixture", true)
            {
                _runtimeDir = runtimeDir;
            }

            protected override Assembly Load(AssemblyName assemblyName)
            {
                string candidate = Path.Combine(_runtimeDir, assemblyName.Name + ".dll");
                return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
            }
        }

        // =====================================================================================
        //  驱动结果核对
        // =====================================================================================

        private static void CheckDriver(string[] lines, string[] expectedCases, string tag)
        {
            Dictionary<string, string> results = new Dictionary<string, string>(StringComparer.Ordinal);
            int pass = 0;
            int fail = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.StartsWith("ID ", StringComparison.Ordinal) || line.StartsWith("HASH ", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("PASS ", StringComparison.Ordinal))
                {
                    pass++;
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

                int space = line.IndexOf(' ', 5);
                string name = space < 0 ? line.Substring(5) : line.Substring(5, space - 5);
                if (results.ContainsKey(name))
                {
                    Check(false, tag + "：用例 " + name + " 出现了多次");
                }

                results[name] = line;
            }

            Check(pass > 0, tag + "：夹具真的跑了用例（PASS 数 = " + pass + "）");
            Check(fail == 0, tag + "：夹具没有 FAIL 用例（FAIL 数 = " + fail + "）");

            for (int i = 0; i < expectedCases.Length; i++)
            {
                string name = expectedCases[i];
                if (!results.ContainsKey(name))
                {
                    Check(false, tag + "：缺少用例 " + name);
                }
            }

            Check(results.Count == expectedCases.Length,
                tag + "：用例数 = " + results.Count + "（期望恰好 " + expectedCases.Length + "，防止用例被静默删掉）");
        }

        private static readonly string[] PreWeaveCases = new string[]
        {
            "preweave-version-zero", "preweave-new-blocked", "preweave-register-blocked",
            "preweave-registry-empty", "preweave-no-body-method", "preweave-send-helper-not-public",
        };

        private static readonly string[] WovenCases = new string[]
        {
            "stamp-is-one", "require-returns-one", "body-method-private", "send-helper-private", "body-attribute-free",
            "register-all", "register-validator-forcevalidate", "id-constants",
            "local-once", "local-args", "local-no-remote-send", "local-once-repeat",
            "remote-not-local", "remote-enqueued", "remote-payload-encoded", "remote-bytes-match",
            "remote-sync-encode-not-local", "remote-sync-bytes-match",
            "receive-status-applied", "receive-validator-business", "receive-args",
            "receive-delivered-counter", "no-recursion", "receive-not-owner-rejected",
            "force-reject-skip", "force-reject-no-disconnect", "force-reject-reported",
            "force-report-executes", "force-report-reported", "force-accept-silent",
            "force-invalid-fail-closed",
            "native-validate-false-skips", "native-validate-false-disconnect", "native-validate-true-executes",
            "array-body-sees-calltime-value", "array-caller-mutated-locally",
            "array-remote-snapshot", "array-remote-peer-receives", "array-null-reject-receive",
            "body-loop-switch-tryfinally", "body-lambda", "body-catch-not-taken", "body-catch-taken",
            "body-shapes-remote-match", "string-param-receive", "client-rpc-receive",
            "delegate-entry-still-networked",
        };

        // =====================================================================================
        //  反射 / 元数据读取
        // =====================================================================================

        private static Dictionary<string, string> ReadConstFields(string dllPath, string runtimeDir)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            string loadPath = MaterializeForLoad(dllPath,
                Path.Combine(Path.GetDirectoryName(dllPath), "load"), "consts");
            FixtureLoadContext context = new FixtureLoadContext(runtimeDir);
            try
            {
                Assembly assembly = context.LoadFromAssemblyPath(Path.GetFullPath(loadPath));
                Type type = assembly.GetType("PMWeave.Fixture.WeaveFixture", true);
                FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo field = fields[i];
                    if (!field.IsLiteral || field.Name == null)
                    {
                        continue;
                    }

                    if (field.Name.IndexOf("PMGenerated", StringComparison.Ordinal) != 0)
                    {
                        continue;
                    }

                    object value = field.GetRawConstantValue();
                    values[field.Name] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                }

                return values;
            }
            finally
            {
                context.Unload();
            }
        }

        private static bool ReadHasPublicKey(string dllPath)
        {
            using (FileStream stream = File.OpenRead(dllPath))
            {
                using (PEReader pe = new PEReader(stream))
                {
                    MetadataReader reader = pe.GetMetadataReader();
                    AssemblyDefinition definition = reader.GetAssemblyDefinition();
                    return !definition.PublicKey.IsNil && reader.GetBlobBytes(definition.PublicKey).Length > 0;
                }
            }
        }

        private static Dictionary<string, string> ParseConsts(SortedDictionary<string, string> generated)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> pair in generated)
            {
                string[] lines = pair.Value.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (!line.StartsWith("public const ", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // public const ushort PMGeneratedRpcId_X = 123;
                    int equals = line.IndexOf(" = ", StringComparison.Ordinal);
                    if (equals < 0)
                    {
                        continue;
                    }

                    string left = line.Substring(0, equals).Trim();
                    string[] parts = left.Split(' ');
                    string name = parts[parts.Length - 1];
                    string value = line.Substring(equals + 3).Trim().TrimEnd(';').TrimEnd('u', 'U').Trim();
                    values[name] = value;
                }
            }

            return values;
        }

        private static string ParseHeaderHash(string registryText)
        {
            if (registryText == null)
            {
                return "<无注册表生成物>";
            }

            string[] lines = registryText.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                int index = lines[i].IndexOf("协议摘要（生成期）：", StringComparison.Ordinal);
                if (index >= 0)
                {
                    return lines[i].Substring(index + "协议摘要（生成期）：".Length).Trim();
                }
            }

            return "<未找到>";
        }

        private static bool IdsEqual(Dictionary<string, string> expected, Dictionary<string, string> actual)
        {
            foreach (KeyValuePair<string, string> pair in expected)
            {
                if (!pair.Key.StartsWith("PMGenerated", StringComparison.Ordinal))
                {
                    continue;
                }

                string value;
                if (!actual.TryGetValue(pair.Key, out value))
                {
                    return false;
                }

                if (!string.Equals(value, pair.Value, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        // =====================================================================================
        //  独立 PDB 读取（System.Reflection.Metadata）
        // =====================================================================================

        private sealed class PdbReport
        {
            public int MethodCount;
            public int CorruptMethods;
            public readonly Dictionary<string, int> SequencePointCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        private sealed class PdbCollisionReport
        {
            public int MethodRows;
            public int DistinctNameKeys;
            public int CollidingNameKeys;
            public readonly List<string> Samples = new List<string>();
        }

        /// <summary>
        /// 用独立读取器复现**旧实现的核对键**（“声明类型名.方法名”，无签名）并量化：
        /// 有多少个键对应「同名但 sequence point 条数不同」的多个方法。
        /// 这就是 P2 BLOCKER 的碰撞面；夹具里必须 > 0，否则用例是空跑。
        /// </summary>
        private static PdbCollisionReport InspectPdbNameCollisions(string dllPath)
        {
            PdbCollisionReport report = new PdbCollisionReport();
            string pdbPath = Path.ChangeExtension(dllPath, ".pdb");
            if (!File.Exists(pdbPath))
            {
                return report;
            }

            Dictionary<int, string> names = new Dictionary<int, string>();
            using (FileStream stream = File.OpenRead(dllPath))
            {
                using (PEReader pe = new PEReader(stream))
                {
                    MetadataReader reader = pe.GetMetadataReader();
                    foreach (MethodDefinitionHandle handle in reader.MethodDefinitions)
                    {
                        int rid = MetadataTokens.GetRowNumber(handle);
                        MethodDefinition definition = reader.GetMethodDefinition(handle);
                        string methodName = reader.GetString(definition.Name);

                        string typeName = "?";
                        TypeDefinitionHandle typeHandle = definition.GetDeclaringType();
                        if (!typeHandle.IsNil)
                        {
                            TypeDefinition typeDefinition = reader.GetTypeDefinition(typeHandle);
                            string ns = reader.GetString(typeDefinition.Namespace);
                            string name = reader.GetString(typeDefinition.Name);
                            typeName = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                        }

                        names[rid] = typeName + "." + methodName;
                    }
                }
            }

            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.Ordinal);
            HashSet<string> colliding = new HashSet<string>(StringComparer.Ordinal);

            using (FileStream stream = File.OpenRead(pdbPath))
            {
                using (MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(stream))
                {
                    MetadataReader pdb = provider.GetMetadataReader();
                    foreach (MethodDebugInformationHandle handle in pdb.MethodDebugInformation)
                    {
                        int rid = MetadataTokens.GetRowNumber(handle);
                        string name;
                        if (!names.TryGetValue(rid, out name))
                        {
                            continue;
                        }

                        int count = 0;
                        foreach (SequencePoint unused in pdb.GetMethodDebugInformation(handle).GetSequencePoints())
                        {
                            count++;
                        }

                        report.MethodRows++;
                        int existing;
                        if (seen.TryGetValue(name, out existing))
                        {
                            if (existing != count)
                            {
                                colliding.Add(name);
                                if (report.Samples.Count < 5)
                                {
                                    report.Samples.Add(name + " (" + existing + " vs " + count + ")");
                                }
                            }
                        }
                        else
                        {
                            seen[name] = count;
                        }
                    }
                }
            }

            report.DistinctNameKeys = seen.Count;
            report.CollidingNameKeys = colliding.Count;
            return report;
        }

        private static PdbReport InspectPdb(string dllPath)
        {
            PdbReport report = new PdbReport();
            string pdbPath = Path.ChangeExtension(dllPath, ".pdb");

            Dictionary<int, string> names = new Dictionary<int, string>();
            using (FileStream stream = File.OpenRead(dllPath))
            {
                using (PEReader pe = new PEReader(stream))
                {
                    MetadataReader reader = pe.GetMetadataReader();
                    foreach (MethodDefinitionHandle handle in reader.MethodDefinitions)
                    {
                        names[MetadataTokens.GetRowNumber(handle)] = reader.GetString(reader.GetMethodDefinition(handle).Name);
                    }
                }
            }

            using (FileStream stream = File.OpenRead(pdbPath))
            {
                using (MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(stream))
                {
                    MetadataReader pdb = provider.GetMetadataReader();
                    foreach (MethodDebugInformationHandle handle in pdb.MethodDebugInformation)
                    {
                        int rid = MetadataTokens.GetRowNumber(handle);
                        string name;
                        if (!names.TryGetValue(rid, out name))
                        {
                            name = "<unknown:" + rid + ">";
                        }

                        MethodDebugInformation info = pdb.GetMethodDebugInformation(handle);
                        int count = 0;
                        bool corrupt = false;
                        try
                        {
                            foreach (SequencePoint unused in info.GetSequencePoints())
                            {
                                count++;
                            }

                            foreach (LocalScopeHandle scopeHandle in pdb.GetLocalScopes(handle))
                            {
                                LocalScope scope = pdb.GetLocalScope(scopeHandle);
                                foreach (LocalVariableHandle variableHandle in scope.GetLocalVariables())
                                {
                                    LocalVariable variable = pdb.GetLocalVariable(variableHandle);
                                    string unusedName = pdb.GetString(variable.Name);
                                }
                            }
                        }
                        catch (Exception)
                        {
                            corrupt = true;
                        }

                        report.MethodCount++;
                        if (corrupt)
                        {
                            report.CorruptMethods++;
                        }

                        report.SequencePointCounts[name] = count;
                    }
                }
            }

            return report;
        }

        // =====================================================================================
        //  进程与环境
        // =====================================================================================

        private sealed class ProcessResult
        {
            public int ExitCode;
            public string StdOut = string.Empty;
            public string StdErr = string.Empty;

            public string Combined()
            {
                return "--- stdout ---\n" + StdOut + "\n--- stderr ---\n" + StdErr;
            }
        }

        private static ProcessResult RunProcess(string fileName, string[] args, string workingDirectory)
        {
            ProcessStartInfo info = new ProcessStartInfo(fileName);
            for (int i = 0; i < args.Length; i++)
            {
                info.ArgumentList.Add(args[i]);
            }

            info.WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.UseShellExecute = false;

            using (Process process = new Process())
            {
                process.StartInfo = info;
                process.Start();
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();

                ProcessResult result = new ProcessResult();
                result.ExitCode = process.ExitCode;
                result.StdOut = stdout;
                result.StdErr = stderr;
                return result;
            }
        }

        /// <summary>
        /// 同时启动 count 个相同命令的进程，然后一起等它们结束（真正的并发，不靠 sleep 猜时序）。
        /// 输出量很小（几百字节），远小于管道缓冲区，因此“先全部启动、再逐个读”不会互锁。
        /// </summary>
        private static ProcessResult[] RunConcurrently(string[] args, string workingDirectory, int count)
        {
            string fileName = DotnetHost();
            Process[] processes = new Process[count];

            for (int i = 0; i < count; i++)
            {
                ProcessStartInfo info = new ProcessStartInfo(fileName);
                for (int k = 0; k < args.Length; k++)
                {
                    info.ArgumentList.Add(args[k]);
                }

                info.WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                info.UseShellExecute = false;

                Process process = new Process();
                process.StartInfo = info;
                process.Start();
                processes[i] = process;
            }

            ProcessResult[] results = new ProcessResult[count];
            try
            {
                for (int i = 0; i < count; i++)
                {
                    ProcessResult result = new ProcessResult();
                    result.StdOut = processes[i].StandardOutput.ReadToEnd();
                    result.StdErr = processes[i].StandardError.ReadToEnd();
                    processes[i].WaitForExit();
                    result.ExitCode = processes[i].ExitCode;
                    results[i] = result;
                }
            }
            finally
            {
                for (int i = 0; i < count; i++)
                {
                    if (processes[i] != null)
                    {
                        processes[i].Dispose();
                    }
                }
            }

            return results;
        }

        private static string DotnetHost()
        {
            string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            return string.IsNullOrEmpty(host) ? "dotnet" : host;
        }

        private static string ToMsBuildPath(string path)
        {
            string full = Path.GetFullPath(path);
            return full.Replace('\\', '/');
        }

        private static string Sha256(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            {
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(stream);
                    StringBuilder text = new StringBuilder(hash.Length * 2);
                    for (int i = 0; i < hash.Length; i++)
                    {
                        text.Append(hash[i].ToString("x2"));
                    }

                    return text.ToString();
                }
            }
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "<空>";
            }

            int index = text.IndexOf('\n');
            return index < 0 ? text : text.Substring(0, index);
        }

        /// <summary>
        /// 定位工具 DLL：
        ///   1) 测试程序集所在目录（`dotnet build -o &lt;dir&gt;` 会把 ProjectReference 的产物放到那里）；
        ///   2) 工具自己的 `bin/Release|Debug/net8.0`。
        /// 多个候选都存在时取**最新写入**的那个，避开“跑到旧 DLL 打假绿”。
        /// </summary>
        private static string LocateToolDll(string repoRoot, string toolName)
        {
            string[] candidates = new string[]
            {
                Path.Combine(AppContext.BaseDirectory, toolName + ".dll"),
                Path.Combine(repoRoot, "Tools", toolName, "bin", "Release", "net8.0", toolName + ".dll"),
                Path.Combine(repoRoot, "Tools", toolName, "bin", "Debug", "net8.0", toolName + ".dll"),
            };

            string best = null;
            DateTime bestTime = DateTime.MinValue;
            for (int i = 0; i < candidates.Length; i++)
            {
                if (!File.Exists(candidates[i]))
                {
                    continue;
                }

                DateTime time = File.GetLastWriteTimeUtc(candidates[i]);
                if (best == null || time > bestTime)
                {
                    best = candidates[i];
                    bestTime = time;
                }
            }

            if (best == null)
            {
                throw new InvalidOperationException(
                    "找不到 " + toolName + ".dll。期望其一存在：\n  " + string.Join("\n  ", candidates)
                    + "\n请先 dotnet build Tools/" + toolName);
            }

            Console.WriteLine("选用 " + toolName + "：" + best);

            if (toolName == "PMNetWeaver")
            {
                _weaverDllPath = best;
            }
            else if (toolName == "PMNetGen")
            {
                _genDllPath = best;
            }

            return best;
        }

        private static string ResolveRepoRoot(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--repo")
                {
                    return Path.GetFullPath(args[i + 1]).Replace('\\', '/');
                }
            }

            string env = Environment.GetEnvironmentVariable("PMWEAVE_REPO_ROOT");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
            {
                return Path.GetFullPath(env).Replace('\\', '/');
            }

            DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "Client"))
                    && Directory.Exists(Path.Combine(dir.FullName, "Tools"))
                    && File.Exists(Path.Combine(dir.FullName, "Tools", "PMNetWeaverTest", "Fixture.cs")))
                {
                    return dir.FullName.Replace('\\', '/');
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException(
                "找不到同时含 Client/Tools 与 Tools/PMNetWeaverTest/Fixture.cs 的仓库根目录，起点："
                + AppContext.BaseDirectory + "（可用 --repo <路径> 或 PMWEAVE_REPO_ROOT 指定）");
        }

        /// <summary>
        /// 构造一个 CSP 公钥 blob（BLOBHEADER + RSAPUBKEY + 模数），供 PublicSign 使用。
        /// 只导出公钥 ⇒ 不需要私钥就能产出「带公钥」的程序集，正好用来测强名称拒绝分支。
        /// </summary>
        private static byte[] BuildCspPublicKeyBlob()
        {
            using (RSA rsa = RSA.Create(1024))
            {
                RSAParameters parameters = rsa.ExportParameters(false);
                byte[] modulus = parameters.Modulus;

                List<byte> blob = new List<byte>();
                blob.Add(0x06); // PUBLICKEYBLOB
                blob.Add(0x02); // 版本
                blob.Add(0x00);
                blob.Add(0x00);
                blob.AddRange(BitConverter.GetBytes(0x00002400)); // CALG_RSA_SIGN

                blob.AddRange(BitConverter.GetBytes(0x31415352)); // "RSA1"
                blob.AddRange(BitConverter.GetBytes(modulus.Length * 8));
                blob.AddRange(BitConverter.GetBytes(65537));
                blob.AddRange(modulus);

                return blob.ToArray();
            }
        }
    }
}
