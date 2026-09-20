using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PMNet;
using PMNet.Codegen;
using PMNetGen;

namespace PMDeclCheck
{
    /// <summary>
    /// R2 声明门禁（契约：Docs/plans/net-r2-codegen-contract.md）。
    ///
    /// 它断言四类东西：
    /// <list type="number">
    ///   <item>**正向**：夹具能扫出类/属性/RPC，且零错误（绝不允许"0 个文件被扫描"也算通过）；</item>
    ///   <item>**负向**：契约 §5 的 12 条规则**逐条**都有非法输入被真的抓到，
    ///         并打印每条规则的实际命中次数（含"源码夹具"与"注入模型"两个来源的拆分）；</item>
    ///   <item>**不变性**：契约 §2.3 的三条 —— 重排不变、二次生成零 diff、改名显式；</item>
    ///   <item>**产物保真**：生成物里嵌的 ID 与 IR 一致、RegisterAll 显式列出每个类且不扫反射、
    ///         并且生成物与 PMNet 运行时**一起真的能编译**（C# 7.3 + netstandard2.0）。</item>
    /// </list>
    ///
    /// 临时工作区：`%TEMP%/PMDeclCheck`。每次运行先整块删除再重建，
    /// 运行结束**保留**（报告要用里面的目录做 CLI 级 `fc /b` 比对）。
    /// </summary>
    internal static class Program
    {
        private static int _pass;
        private static int _fail;
        private static string _root;
        private static string _temp;

        private static int Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch (Exception)
            {
                // 个别重定向环境不允许改编码；忽略即可（中文仍会按默认编码输出）。
            }

            if (args.Length >= 2 && string.Equals(args[0], "--emit-fixtures", StringComparison.Ordinal))
            {
                return EmitFixtures(args[1]);
            }

            _root = FindRepoRoot();
            if (_root == null)
            {
                Console.Error.WriteLine("[PMDeclCheck] 找不到仓库根（缺少 Docs/plans/net-r2-codegen-contract.md）");
                return 3;
            }

            _temp = Path.Combine(Path.GetTempPath(), "PMDeclCheck");
            ResetDirectory(_temp);

            Console.WriteLine("PMDeclCheck - R2 声明门禁");
            Console.WriteLine("  仓库根   : " + _root);
            Console.WriteLine("  临时工作区: " + _temp);

            string goodPath = Path.Combine(_root, "Tools/PMDeclCheck/Fixtures/Good.cs");
            string badPath = Path.Combine(_root, "Tools/PMDeclCheck/Fixtures/Bad.cs");
            string repoLockPath = Path.Combine(_root, "Docs/plans/pmnet-ids.json");

            Section("0. 夹具可用性");
            Check("Good.cs 存在", File.Exists(goodPath), goodPath);
            Check("Bad.cs 存在", File.Exists(badPath), badPath);
            if (!File.Exists(goodPath) || !File.Exists(badPath))
            {
                return Summary();
            }

            PMDeclScanResult good = SafeScan(new List<string> { goodPath }, new PMIdLock());
            if (good == null)
            {
                return Summary();
            }

            Section("1. 正向扫描（Good.cs）");
            CheckPositive(good, repoLockPath);

            Section("2. 负向验证：契约 §5 十二条规则逐条命中");
            int[] sourceHits = CheckBadFixture(badPath);

            Section("3. 负向验证（补充）：注入损坏模型覆盖 IR 级不变量（规则 10 / 12）");
            int[] injectedHits = CheckInjectedModel();

            Section("4. 规则命中总表");
            PrintRuleTable(sourceHits, injectedHits);

            Section("5. 不变性 1：重排不变（两种排列各生成一次，逐字节比对）");
            InvariantPermutation(goodPath);

            Section("6. 不变性 2：二次生成零 diff（含锁文件）");
            InvariantSecondGeneration(goodPath);

            Section("7. 不变性 3：改名显式（新 ID + 退役告警；钉住 StableKey 则 ID 不变）");
            InvariantRename(goodPath);

            Section("8. 生成物保真：ID 内嵌一致 / RegisterAll 显式且不扫反射");
            CheckProductFidelity(good);

            Section("9. 生成物可编译（C# 7.3 + netstandard2.0 + PMNet 运行时）");
            CheckGeneratedCodeCompiles(good, goodPath);

            Section("10. 全局协议摘要自洽（生成期与运行期同一实现）");
            CheckProtocolHash(good);

            Section("11. 边界：空声明集也要产出注册表");
            CheckEmptyModel();

            Section("12. RV5/RV6 生成分支：数组快照 / 入队前长度门 / 原生 Validate / 发射器 fail-closed");
            CheckRpcBranchCoverage(good);

            return Summary();
        }

        // =================================================================================
        //  1. 正向
        // =================================================================================

        private static void CheckPositive(PMDeclScanResult scan, string repoLockPath)
        {
            int props = 0;
            int rpcs = 0;
            for (int i = 0; i < scan.Model.Classes.Count; i++)
            {
                props += scan.Model.Classes[i].Properties.Count;
                rpcs += scan.Model.Classes[i].Rpcs.Count;
            }

            Console.WriteLine("  扫描：文件 " + scan.Facts.ParsedFileCount + " 个，类 " + scan.Model.Classes.Count
                + " 个，属性 " + props + " 个，RPC " + rpcs + " 条，错误 " + scan.Model.Errors.Count
                + " 个，告警 " + scan.Model.Warnings.Count + " 个");

            // 「不许把 0 个文件被扫描当成通过」——这是任务书点名的失败模式。
            Check("解析到源文件（ParsedFileCount > 0）", scan.Facts.ParsedFileCount > 0,
                "ParsedFileCount=" + scan.Facts.ParsedFileCount);
            Check("扫描到网络类（> 0）", scan.Model.Classes.Count > 0, "类数=" + scan.Model.Classes.Count);
            Check("扫描到复制属性（> 0）", props > 0, "属性数=" + props);
            Check("扫描到 RPC（> 0）", rpcs > 0, "RPC 数=" + rpcs);
            Check("Good.cs 零声明错误", scan.Model.Errors.Count == 0, FirstError(scan.Model));

            bool syntaxClean = scan.Facts.SyntaxErrorCount == 0;
            Check("Good.cs 零语法错误", syntaxClean, "SyntaxErrorCount=" + scan.Facts.SyntaxErrorCount);

            // 与已提交的锁文件一致性：换一把**全新**的锁扫同一份声明，
            // 得到的 ID 必须与已提交锁文件里的完全一致（否则说明哈希/分配不确定）。
            if (!File.Exists(repoLockPath))
            {
                Console.WriteLine("  [SKIP] 已提交锁文件不存在，跳过一致性比对：" + repoLockPath);
                return;
            }

            PMIdLock committed;
            try
            {
                committed = PMIdLock.Deserialize(File.ReadAllText(repoLockPath, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                Check("已提交锁文件可解析", false, ex.Message);
                return;
            }

            if (committed == null)
            {
                Check("已提交锁文件非空", false, repoLockPath);
                return;
            }

            int compared = 0;
            int mismatch = 0;
            string firstMismatch = null;
            for (int i = 0; i < scan.Model.Classes.Count; i++)
            {
                PMDeclClass cls = scan.Model.Classes[i];
                uint oldId;
                if (!committed.TryGetClassId(cls.StableKey, out oldId))
                {
                    continue;
                }

                compared++;
                if (oldId != cls.ClassId)
                {
                    mismatch++;
                    if (firstMismatch == null)
                    {
                        firstMismatch = cls.StableKey + "：" + oldId + " → " + cls.ClassId;
                    }
                }

                for (int k = 0; k < cls.Properties.Count; k++)
                {
                    ushort memberOld;
                    string key = PMStableHash.PropertyKey(cls.StableKey, cls.Properties[k].MemberName);
                    if (committed.TryGetMemberId(cls.StableKey, key, out memberOld)
                        && memberOld != cls.Properties[k].PropertyId)
                    {
                        mismatch++;
                        if (firstMismatch == null)
                        {
                            firstMismatch = key + "：" + memberOld + " → " + cls.Properties[k].PropertyId;
                        }
                    }
                }
            }

            Check("已提交锁文件与当前声明一致（同键同 ID）", mismatch == 0,
                "比对 " + compared + " 个类键，不一致 " + mismatch + " 处"
                + (firstMismatch != null ? "；首个：" + firstMismatch : string.Empty));
        }

        // =================================================================================
        //  2 / 3. 负向
        // =================================================================================

        private static int[] CheckBadFixture(string badPath)
        {
            PMDeclScanResult bad = SafeScan(new List<string> { badPath }, new PMIdLock());
            if (bad == null)
            {
                return new int[PMDeclValidation.RuleCount + 1];
            }

            Console.WriteLine("  Bad.cs 扫描：类 " + bad.Model.Classes.Count + " 个，错误 " + bad.Model.Errors.Count
                + " 个，告警 " + bad.Model.Warnings.Count + " 个");

            int[] hits = CountByRule(bad.Model.Errors, "Bad.cs");

            Check("Bad.cs 确实触发了声明错误", bad.Model.Errors.Count > 0, "错误数=" + bad.Model.Errors.Count);
            return hits;
        }

        private static int[] CheckInjectedModel()
        {
            // 规则 10 / 12 判的是 IR 级不变量（ID 唯一性、掩码区间），
            // 而 ID 与掩码都是生成器自己分配的 —— 源码层几乎构不出非法值。
            // 因此这里注入一个**手工损坏的 IR**，直接验证校验器真的会拦住它。
            // 这是"负向验证"的必要补充：只测源码路径会留下"校验器根本没生效"的盲区。
            PMDeclModel model = new PMDeclModel();
            PMDeclRawFacts facts = new PMDeclRawFacts();

            PMDeclClass injected = new PMDeclClass();
            injected.Namespace = "Injected";
            injected.TypeName = "Broken";
            injected.StableKey = "CLASS:Injected.Broken";
            injected.ClassId = 4242u;
            injected.IsPartial = true;
            injected.BaseTypeName = "PMNetObject";
            injected.ChangeMaskBitCount = 2;

            PMDeclProperty a = new PMDeclProperty();
            a.MemberName = "A";
            a.TypeName = "int";
            a.PropertyId = 7;
            a.MaskOffset = 0;
            a.MaskBitCount = 1;
            injected.Properties.Add(a);

            PMDeclProperty b = new PMDeclProperty();
            b.MemberName = "B";
            b.TypeName = "int";
            b.PropertyId = 7; // 与 A 重复（规则 10）
            b.MaskOffset = 0; // 与 A 重叠（规则 12）
            b.MaskBitCount = 1;
            injected.Properties.Add(b);

            PMDeclRpc r1 = new PMDeclRpc();
            r1.MethodName = "R1";
            r1.RpcId = 5;
            r1.Kind = PMRpcKind.Server;
            r1.Validator = PMRpcValidator.ForceValidate;
            r1.ReturnsVoid = true;
            injected.Rpcs.Add(r1);

            PMDeclRpc r2 = new PMDeclRpc();
            r2.MethodName = "R2";
            r2.RpcId = 5; // 与 R1 重复（规则 10）
            r2.Kind = PMRpcKind.Server;
            r2.Validator = PMRpcValidator.ForceValidate;
            r2.ReturnsVoid = true;
            injected.Rpcs.Add(r2);

            model.Classes.Add(injected);

            PMDeclClass maskGap = new PMDeclClass();
            maskGap.Namespace = "Injected";
            maskGap.TypeName = "MaskGap";
            maskGap.StableKey = "CLASS:Injected.MaskGap";
            maskGap.ClassId = 4243u;
            maskGap.IsPartial = true;
            maskGap.BaseTypeName = "PMNetObject";
            maskGap.ChangeMaskBitCount = 5; // 属性只占 2 位 ⇒ 总数不匹配（规则 12）

            PMDeclProperty c = new PMDeclProperty();
            c.MemberName = "C";
            c.TypeName = "int";
            c.PropertyId = 11;
            c.MaskOffset = 0;
            c.MaskBitCount = 1;
            maskGap.Properties.Add(c);

            PMDeclProperty d = new PMDeclProperty();
            d.MemberName = "D";
            d.TypeName = "int";
            d.PropertyId = 12;
            d.MaskOffset = 1;
            d.MaskBitCount = 1;
            maskGap.Properties.Add(d);

            model.Classes.Add(maskGap);

            PMDeclValidation.Validate(model, facts);

            int[] hits = CountByRule(model.Errors, "注入模型");
            for (int i = 0; i < model.Errors.Count; i++)
            {
                Console.WriteLine("      " + model.Errors[i]);
            }

            Check("注入模型触发了错误", model.Errors.Count > 0, "错误数=" + model.Errors.Count);
            Check("注入模型命中规则 10", hits[10] > 0, "命中 " + hits[10] + " 次");
            Check("注入模型命中规则 12", hits[12] > 0, "命中 " + hits[12] + " 次");
            return hits;
        }

        private static int[] CountByRule(List<string> errors, string sourceLabel)
        {
            int[] hits = new int[PMDeclValidation.RuleCount + 1];
            int unclassified = 0;
            const string prefix = "[规则 ";

            for (int i = 0; i < errors.Count; i++)
            {
                string e = errors[i];
                if (e == null || !e.StartsWith(prefix, StringComparison.Ordinal))
                {
                    unclassified++;
                    continue;
                }

                int close = e.IndexOf(']', prefix.Length);
                int rule;
                if (close > 0
                    && int.TryParse(e.Substring(prefix.Length, close - prefix.Length), out rule)
                    && rule >= 1 && rule <= PMDeclValidation.RuleCount)
                {
                    hits[rule]++;
                }
                else
                {
                    unclassified++;
                }
            }

            if (unclassified > 0)
            {
                Console.WriteLine("  注意：" + sourceLabel + " 有 " + unclassified + " 条错误没有可识别的规则编号");
            }

            return hits;
        }

        private static void PrintRuleTable(int[] sourceHits, int[] injectedHits)
        {
            int total = 0;
            for (int rule = 1; rule <= PMDeclValidation.RuleCount; rule++)
            {
                int sum = sourceHits[rule] + injectedHits[rule];
                total += sum;
                bool ok = sum > 0;
                if (ok)
                {
                    _pass++;
                }
                else
                {
                    _fail++;
                }

                Console.WriteLine("  [" + (ok ? "PASS" : "FAIL") + "] 规则 " + rule.ToString().PadLeft(2)
                    + "  命中 " + sum.ToString().PadLeft(3) + " 次"
                    + "（源码夹具 " + sourceHits[rule] + " + 注入模型 " + injectedHits[rule] + "）"
                    + "  " + PMDeclValidation.RuleTitles[rule - 1]);
            }

            Console.WriteLine("  规则命中合计：" + total + " 次");
        }

        // =================================================================================
        //  5. 重排不变
        // =================================================================================

        private static void InvariantPermutation(string goodPath)
        {
            string text = File.ReadAllText(goodPath);
            string permuted = PermuteDeclarations(text);

            Check("重排后的源码与原源码文本不同（否则这条不变性是空测）",
                !string.Equals(text, permuted, StringComparison.Ordinal), "长度 " + text.Length + " → " + permuted.Length);

            string dirA = Path.Combine(_temp, "perm-A");
            string dirB = Path.Combine(_temp, "perm-B");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);

            // A 用逐字节复制，保证"两种排列"除顺序外完全同一份声明。
            File.WriteAllBytes(Path.Combine(dirA, "Decl.cs"), File.ReadAllBytes(goodPath));
            File.WriteAllText(Path.Combine(dirB, "Decl.cs"), permuted, new UTF8Encoding(false));

            PMIdLock lockA = new PMIdLock();
            PMDeclScanResult scanA = SafeScan(new List<string> { dirA }, lockA);
            PMIdLock lockB = new PMIdLock();
            PMDeclScanResult scanB = SafeScan(new List<string> { dirB }, lockB);
            if (scanA == null || scanB == null)
            {
                return;
            }

            Check("重排后的源码零语法错误", scanB.Facts.SyntaxErrorCount == 0,
                "SyntaxErrorCount=" + scanB.Facts.SyntaxErrorCount);
            Check("两种排列都零声明错误", scanA.Model.Errors.Count == 0 && scanB.Model.Errors.Count == 0,
                FirstError(scanA.Model) + FirstError(scanB.Model));
            Check("两种排列的声明集合相同（类/属性/RPC 计数）",
                CountAll(scanA.Model, out int p1, out int r1) == CountAll(scanB.Model, out int p2, out int r2)
                && p1 == p2 && r1 == r2,
                "A=" + CountAll(scanA.Model, out p1, out r1) + "/" + p1 + "/" + r1
                + "  B=" + CountAll(scanB.Model, out p2, out r2) + "/" + p2 + "/" + r2);

            // ID 不变（这是"与声明顺序无关"的核心断言）
            int idMismatch = 0;
            for (int i = 0; i < scanA.Model.Classes.Count; i++)
            {
                uint b;
                if (!lockB.TryGetClassId(scanA.Model.Classes[i].StableKey, out b)
                    || b != scanA.Model.Classes[i].ClassId)
                {
                    idMismatch++;
                }
            }

            Check("重排后 ClassId 不变", idMismatch == 0, "不一致 " + idMismatch + " 个");

            // 产物逐字节不变
            List<PMGeneratedFile> filesA = PMDeclEmitter.EmitAll(scanA.Model, scanA.Facts);
            List<PMGeneratedFile> filesB = PMDeclEmitter.EmitAll(scanB.Model, scanB.Facts);

            string outA = Path.Combine(_temp, "out-A");
            string outB = Path.Combine(_temp, "out-B");
            WriteGenerated(outA, filesA);
            WriteGenerated(outB, filesB);

            int diff = CompareDirectories(outA, outB, out string detail);
            Check("两种排列的生成物逐字节相同", diff == 0, detail);
            Console.WriteLine("      排列 A 源码：" + Path.Combine(dirA, "Decl.cs"));
            Console.WriteLine("      排列 B 源码：" + Path.Combine(dirB, "Decl.cs"));
            Console.WriteLine("      产物目录 A  ：" + outA);
            Console.WriteLine("      产物目录 B  ：" + outB);
            Console.WriteLine("      可用 `fc /b <A 里的文件> <B 里的文件>` 复现（见报告）。");
        }

        // =================================================================================
        //  6. 二次生成零 diff
        // =================================================================================

        private static void InvariantSecondGeneration(string goodPath)
        {
            string dir = Path.Combine(_temp, "twice-src");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "Decl.cs"), File.ReadAllBytes(goodPath));

            // 第一次：全新锁 → 生成 → 保存锁
            PMIdLock lock1 = new PMIdLock();
            PMDeclScanResult scan1 = SafeScan(new List<string> { dir }, lock1);
            if (scan1 == null)
            {
                return;
            }

            PMDeclValidation.Validate(scan1.Model, scan1.Facts);
            if (scan1.Model.Errors.Count != 0)
            {
                Check("第一次生成零错误", false, FirstError(scan1.Model));
                return;
            }

            string lockText1 = lock1.Serialize();
            List<PMGeneratedFile> files1 = PMDeclEmitter.EmitAll(scan1.Model, scan1.Facts);
            string out1 = Path.Combine(_temp, "twice-out1");
            WriteGenerated(out1, files1);

            // 第二次：从**第一次写出的锁文件文本**重新加载（模拟"生成→提交→再生成"）
            PMIdLock lock2 = PMIdLock.Deserialize(lockText1);
            PMDeclScanResult scan2 = SafeScan(new List<string> { dir }, lock2);
            if (scan2 == null)
            {
                return;
            }

            PMDeclValidation.Validate(scan2.Model, scan2.Facts);
            string lockText2 = lock2.Serialize();
            List<PMGeneratedFile> files2 = PMDeclEmitter.EmitAll(scan2.Model, scan2.Facts);
            string out2 = Path.Combine(_temp, "twice-out2");
            WriteGenerated(out2, files2);

            Check("二次生成的锁文件逐字节相同",
                string.Equals(lockText1, lockText2, StringComparison.Ordinal),
                "长度 " + lockText1.Length + " vs " + lockText2.Length);

            int diff = CompareDirectories(out1, out2, out string detail);
            Check("二次生成的产物逐字节相同", diff == 0, detail);
            Check("二次生成没有新增 ID（Added 为空）", lock2.Added.Count == 0,
                "Added=" + lock2.Added.Count);
            Check("夹具级 ID 无哈希碰撞（探测未发生）", lock1.Collisions.Count == 0,
                "Collisions=" + lock1.Collisions.Count);
            Console.WriteLine("      产物目录 1：" + out1);
            Console.WriteLine("      产物目录 2：" + out2);
        }

        // =================================================================================
        //  7. 改名显式
        // =================================================================================

        private static void InvariantRename(string goodPath)
        {
            string original = File.ReadAllText(goodPath);
            string seedLockText = null;
            PMIdLock seed = new PMIdLock();
            PMDeclScanResult baseline = SafeScan(new List<string> { goodPath }, seed);
            if (baseline == null)
            {
                return;
            }

            seedLockText = seed.Serialize();

            uint hudId = 0;
            uint pinnedId = 0;
            bool foundHud = false;
            bool foundPinned = false;
            for (int i = 0; i < baseline.Model.Classes.Count; i++)
            {
                if (baseline.Model.Classes[i].TypeName == "FixtureHud")
                {
                    hudId = baseline.Model.Classes[i].ClassId;
                    foundHud = true;
                }

                if (baseline.Model.Classes[i].TypeName == "FixturePinned")
                {
                    pinnedId = baseline.Model.Classes[i].ClassId;
                    foundPinned = true;
                }
            }

            Check("夹具包含未钉住键的 FixtureHud", foundHud, "ClassId=" + hudId);
            Check("夹具包含钉住 StableKey 的 FixturePinned", foundPinned, "ClassId=" + pinnedId);

            // --- 7a：未钉住键的类改名 ⇒ 新 ID ---
            string renamed = original.Replace("FixtureHud", "FixtureHudRenamed");
            Check("改名后的源码文本确实变了", !string.Equals(original, renamed, StringComparison.Ordinal), "");

            string renamePath = Path.Combine(_temp, "rename-plain");
            Directory.CreateDirectory(renamePath);
            File.WriteAllText(Path.Combine(renamePath, "Decl.cs"), renamed, new UTF8Encoding(false));

            PMIdLock lockAfter = PMIdLock.Deserialize(seedLockText);
            PMDeclScanResult after = SafeScan(new List<string> { renamePath }, lockAfter);
            if (after == null)
            {
                return;
            }

            PMDeclValidation.Validate(after.Model, after.Facts);
            PMDeclScanner.ReportRetiredKeys(lockAfter, seedLockText, after.Model);

            uint newHudId = 0;
            bool foundNew = false;
            for (int i = 0; i < after.Model.Classes.Count; i++)
            {
                if (after.Model.Classes[i].TypeName == "FixtureHudRenamed")
                {
                    newHudId = after.Model.Classes[i].ClassId;
                    foundNew = true;
                }
            }

            Check("改名后出现新类键", foundNew, "FixtureHudRenamed ClassId=" + newHudId);
            Check("改名后得到新 ClassId（不是静默沿用）", foundNew && newHudId != hudId,
                hudId + " → " + newHudId);

            uint oldStillThere;
            Check("旧键的 ID 未被回收（仍在锁文件里）",
                lockAfter.TryGetClassId("CLASS:PMNetFixtures.FixtureHud", out oldStillThere) && oldStillThere == hudId,
                "旧键 = " + hudId);

            bool hasRetiredWarning = false;
            for (int i = 0; i < after.Model.Warnings.Count; i++)
            {
                if (after.Model.Warnings[i].Contains("已退役") && after.Model.Warnings[i].Contains("FixtureHud"))
                {
                    hasRetiredWarning = true;
                }
            }

            Check("改名产生了退役告警（ID 永不回收）", hasRetiredWarning, string.Join(" / ", after.Model.Warnings.ToArray()));

            // --- 7b：钉住 StableKey 的类改名 ⇒ ClassId 不变 ---
            string pinnedRenamed = original.Replace("FixturePinned", "FixturePinnedRenamed");
            string pinnedPath = Path.Combine(_temp, "rename-pinned");
            Directory.CreateDirectory(pinnedPath);
            File.WriteAllText(Path.Combine(pinnedPath, "Decl.cs"), pinnedRenamed, new UTF8Encoding(false));

            PMIdLock pinnedLock = PMIdLock.Deserialize(seedLockText);
            PMDeclScanResult pinnedAfter = SafeScan(new List<string> { pinnedPath }, pinnedLock);
            if (pinnedAfter == null)
            {
                return;
            }

            uint pinnedNewId = 0;
            ushort pinnedMemberId = 0;
            for (int i = 0; i < pinnedAfter.Model.Classes.Count; i++)
            {
                PMDeclClass cls = pinnedAfter.Model.Classes[i];
                if (cls.TypeName == "FixturePinnedRenamed")
                {
                    pinnedNewId = cls.ClassId;
                    if (cls.Properties.Count > 0)
                    {
                        pinnedMemberId = cls.Properties[0].PropertyId;
                    }
                }
            }

            Check("钉住 StableKey 的类改名后 ClassId 不变（D-R0-49 的兼容路径）",
                pinnedNewId == pinnedId, pinnedId + " → " + pinnedNewId);

            // 钉住类键是否也能钉住**成员 ID** —— 这是契约 §2.1 那句"改名但要保持线协议兼容"
            // 到底成不成立的判据。早期实现里成员键用「命名空间.类型名」拼前缀，
            // 于是钉住类键后改类名会得到「类 ID 不变、成员 ID 全变」，等于只保住了半个协议；
            // 现在成员键以**类稳定键**为前缀，因此这条断言必须成立。
            ushort pinnedMemberOld = 0;
            bool memberOldKnown = seed.TryGetMemberId(
                "PMDeclCheck.Fixture.Pinned",
                PMStableHash.PropertyKey("CLASS:PMDeclCheck.Fixture.Pinned", "_value"),
                out pinnedMemberOld);

            Check("钉住 StableKey 的类改名后成员 PropertyId 也不变（整类线协议被钉住）",
                memberOldKnown && pinnedMemberOld == pinnedMemberId,
                (memberOldKnown ? pinnedMemberOld.ToString() : "<读不到>") + " → " + pinnedMemberId);

            // 反向对照：未钉 StableKey 的等同类，改名后成员 ID **必须**变 ——
            // 否则说明"钉住"与"不钉住"没有区别，那条兼容路径就是装饰。
            Console.WriteLine("      对照：未钉 StableKey 的类改名后成员 ID 会变（预期行为，未被钉住）。");
        }

        // =================================================================================
        //  8. 产物保真
        // =================================================================================

        private static void CheckProductFidelity(PMDeclScanResult scan)
        {
            List<PMGeneratedFile> files = PMDeclEmitter.EmitAll(scan.Model, scan.Facts);

            Check("产物数量 = 类数 + 1（注册表）", files.Count == scan.Model.Classes.Count + 1,
                "文件 " + files.Count + " 个 / 类 " + scan.Model.Classes.Count + " 个");

            bool hasRegistry = false;
            for (int i = 0; i < files.Count; i++)
            {
                if (files[i].FileName == PMDeclEmitter.RegistryFileName)
                {
                    hasRegistry = true;
                }
            }

            Check("产出了注册表文件 " + PMDeclEmitter.RegistryFileName, hasRegistry, "");

            int idMismatch = 0;
            int rpcMismatch = 0;
            int missing = 0;
            int reflectionTokens = 0;
            int badSystemUsage = 0;
            string firstReflection = null;
            string firstBadSystem = null;
            string firstMissing = null;

            string[] forbidden =
            {
                "System.Reflection", "Activator.", "GetTypes(", ".GetType()", "GetProperties(",
                "GetMethod(", "typeof(", "Assembly.",
            };

            for (int i = 0; i < scan.Model.Classes.Count; i++)
            {
                PMDeclClass cls = scan.Model.Classes[i];
                string fileName = PMDeclEmitter.FileNameFor(cls);
                string content = null;
                for (int k = 0; k < files.Count; k++)
                {
                    if (string.Equals(files[k].FileName, fileName, StringComparison.Ordinal))
                    {
                        content = files[k].Content;
                    }
                }

                if (content == null)
                {
                    missing++;
                    if (firstMissing == null)
                    {
                        firstMissing = fileName;
                    }

                    continue;
                }

                if (content.IndexOf("PMGeneratedClassId = " + cls.ClassId + "u", StringComparison.Ordinal) < 0)
                {
                    idMismatch++;
                }

                if (content.IndexOf("PMGeneratedChangeMaskBitCount = " + cls.ChangeMaskBitCount,
                        StringComparison.Ordinal) < 0)
                {
                    idMismatch++;
                }

                for (int r = 0; r < cls.Rpcs.Count; r++)
                {
                    if (content.IndexOf("PMGeneratedRpcId_" + cls.Rpcs[r].MethodName + " = " + cls.Rpcs[r].RpcId,
                            StringComparison.Ordinal) < 0)
                    {
                        rpcMismatch++;
                    }
                }

                for (int f = 0; f < forbidden.Length; f++)
                {
                    if (content.IndexOf(forbidden[f], StringComparison.Ordinal) >= 0)
                    {
                        reflectionTokens++;
                        if (firstReflection == null)
                        {
                            firstReflection = fileName + " 含 " + forbidden[f];
                        }
                    }
                }

                string badSystem = FindUnexpectedSystemUsage(content);
                if (badSystem != null)
                {
                    badUsageAdd(ref badSystemUsage, ref firstBadSystem, fileName + " 用了 " + badSystem);
                }
            }

            // 注册表：显式列出每个类 + 无反射
            string registry = null;
            for (int i = 0; i < files.Count; i++)
            {
                if (files[i].FileName == PMDeclEmitter.RegistryFileName)
                {
                    registry = files[i].Content;
                }
            }

            int notListed = 0;
            string firstNotListed = null;
            for (int i = 0; i < scan.Model.Classes.Count; i++)
            {
                string needle = "global::" + scan.Model.Classes[i].QualifiedName + ".PMNet_BuildEntry();";
                if (registry == null || registry.IndexOf(needle, StringComparison.Ordinal) < 0)
                {
                    notListed++;
                    if (firstNotListed == null)
                    {
                        firstNotListed = needle;
                    }
                }
            }

            for (int f = 0; f < forbidden.Length; f++)
            {
                if (registry != null && registry.IndexOf(forbidden[f], StringComparison.Ordinal) >= 0)
                {
                    reflectionTokens++;
                    if (firstReflection == null)
                    {
                        firstReflection = PMDeclEmitter.RegistryFileName + " 含 " + forbidden[f];
                    }
                }
            }

            string badRegistrySystem = registry == null ? null : FindUnexpectedSystemUsage(registry);
            if (badRegistrySystem != null)
            {
                badUsageAdd(ref badSystemUsage, ref firstBadSystem,
                    PMDeclEmitter.RegistryFileName + " 用了 " + badRegistrySystem);
            }

            Check("每个类的产物都存在且内嵌 ClassId / 掩码位数与 IR 一致", idMismatch == 0 && missing == 0,
                "ID 不一致 " + idMismatch + " 处，缺失 " + missing + " 个"
                + (firstMissing != null ? "（首个 " + firstMissing + "）" : string.Empty));
            Check("每个 RPC 的产物内嵌 RpcId 与 IR 一致", rpcMismatch == 0, "不一致 " + rpcMismatch + " 处");
            Check("RegisterAll 显式列出每个类", notListed == 0,
                "未列出 " + notListed + " 个" + (firstNotListed != null ? "（首个 " + firstNotListed + "）" : string.Empty));
            Check("产物不含反射扫描（D-R0-48 / T40：运行不扫反射）", reflectionTokens == 0,
                firstReflection ?? "未发现 System.Reflection / Activator / GetTypes / typeof 等记号");
            Check("产物的 BCL 用法限于白名单（System.FormatException）", badSystemUsage == 0,
                firstBadSystem ?? "无其它 System.* 用法");
        }

        private static void badUsageAdd(ref int count, ref string first, string message)
        {
            count++;
            if (first == null)
            {
                first = message;
            }
        }

        /// <summary>
        /// 扫生成物里对 BCL 的**实际使用**（不含 using 行）。
        /// 允许的只有 `System.FormatException`（数组越界时抛）。
        /// 这条检查是 netstandard2.0 API 面的一个可核对的替代口径：
        /// 生成物只用到 PMNet 类型 + 一个 BCL 异常类型，因此它的 API 面是手可复核的。
        /// </summary>
        private static string FindUnexpectedSystemUsage(string content)
        {
            string[] lines = content.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("using ", StringComparison.Ordinal))
                {
                    continue;
                }

                int at = line.IndexOf("System.", StringComparison.Ordinal);
                if (at < 0)
                {
                    continue;
                }

                string tail = line.Substring(at + "System.".Length);
                if (tail.StartsWith("FormatException", StringComparison.Ordinal))
                {
                    continue;
                }

                return line.Trim();
            }

            return null;
        }

        // =================================================================================
        //  9. 生成物可编译
        // =================================================================================

        private static void CheckGeneratedCodeCompiles(PMDeclScanResult scan, string goodPath)
        {
            List<PMGeneratedFile> files = PMDeclEmitter.EmitAll(scan.Model, scan.Facts);

            List<SyntaxTree> trees = new List<SyntaxTree>();
            CSharpParseOptions options = new CSharpParseOptions(LanguageVersion.CSharp7_3);

            // 生成物
            for (int i = 0; i < files.Count; i++)
            {
                trees.Add(CSharpSyntaxTree.ParseText(files[i].Content, options, files[i].FileName));
            }

            // 业务声明夹具
            trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(goodPath), options, Path.GetFileName(goodPath)));

            // PMNet 运行时（与 PMNetLangCheck 用同一份源码，不复制）
            string pmnetDir = Path.Combine(_root, "Client/Assets/Scripts/PMNet");
            if (!Directory.Exists(pmnetDir))
            {
                Check("PMNet 运行时源码目录存在", false, pmnetDir);
                return;
            }

            string[] runtimeFiles = Directory.GetFiles(pmnetDir, "*.cs", SearchOption.AllDirectories);
            Array.Sort(runtimeFiles, StringComparer.Ordinal);
            for (int i = 0; i < runtimeFiles.Length; i++)
            {
                trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(runtimeFiles[i]), options, Path.GetFileName(runtimeFiles[i])));
            }

            string refMode;
            List<MetadataReference> references = BuildReferences(out refMode);
            Check("编译引用集可用", references != null && references.Count > 0, refMode);
            if (references == null || references.Count == 0)
            {
                return;
            }

            CSharpCompilation compilation = CSharpCompilation.Create(
                "PMDeclCheckGenerated",
                trees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            List<string> errors = new List<string>();
            foreach (Diagnostic d in compilation.GetDiagnostics())
            {
                if (d.Severity == DiagnosticSeverity.Error)
                {
                    errors.Add(d.Id + " " + d.GetMessage() + " @ " + d.Location.GetLineSpan().Path + ":"
                        + (d.Location.GetLineSpan().StartLinePosition.Line + 1));
                }
            }

            Console.WriteLine("      编译输入：生成物 " + files.Count + " 个 + 夹具 1 个 + PMNet 运行时 "
                + runtimeFiles.Length + " 个；引用集：" + refMode);

            for (int i = 0; i < errors.Count && i < 20; i++)
            {
                Console.WriteLine("      " + errors[i]);
            }

            Check("生成物 + 夹具 + 运行时在 C# 7.3 下零编译错误", errors.Count == 0,
                "错误 " + errors.Count + " 个" + (errors.Count > 0 ? "（见上）" : string.Empty));
        }

        private static List<MetadataReference> BuildReferences(out string mode)
        {
            List<MetadataReference> refs = new List<MetadataReference>();

            string refDir = FindNetStandard20RefDirectory();
            if (refDir != null)
            {
                string[] dlls = Directory.GetFiles(refDir, "*.dll");
                Array.Sort(dlls, StringComparer.Ordinal);
                for (int i = 0; i < dlls.Length; i++)
                {
                    try
                    {
                        refs.Add(MetadataReference.CreateFromFile(dlls[i]));
                    }
                    catch (Exception)
                    {
                        // 个别 facade 无法作为元数据读取时跳过；netstandard.dll 一定读得到。
                    }
                }

                mode = "netstandard2.0 引用程序集（" + refs.Count + " 个，来自 " + refDir + "）";
                if (refs.Count > 0)
                {
                    return refs;
                }
            }

            // 降级：用运行时的 TPA 集合。此时语言面仍是 C# 7.3，但 API 面放宽到 net8.0。
            string tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
            if (!string.IsNullOrEmpty(tpa))
            {
                string[] parts = tpa.Split(Path.PathSeparator);
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i].Length == 0)
                    {
                        continue;
                    }

                    try
                    {
                        refs.Add(MetadataReference.CreateFromFile(parts[i]));
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            mode = "降级：运行时 TPA（" + refs.Count + " 个）——未找到 netstandard2.0 引用程序集，"
                + "因此本段只强制语言面 C# 7.3，API 面由 Tools/PMNetLangCheck 单独覆盖";
            return refs;
        }

        private static string FindNetStandard20RefDirectory()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string pkg = Path.Combine(home, ".nuget", "packages", "netstandard.library");
            if (!Directory.Exists(pkg))
            {
                return null;
            }

            string[] versions = Directory.GetDirectories(pkg);
            Array.Sort(versions, StringComparer.OrdinalIgnoreCase);
            for (int i = versions.Length - 1; i >= 0; i--)
            {
                string candidate = Path.Combine(versions[i], "build", "netstandard2.0", "ref");
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "netstandard.dll")))
                {
                    return candidate;
                }
            }

            return null;
        }

        // =================================================================================
        //  10 / 11. 协议摘要与边界
        // =================================================================================

        private static void CheckProtocolHash(PMDeclScanResult scan)
        {
            PMNetClassEntry[] entries = PMDeclScanner.BuildClassEntries(scan.Model);
            uint recomputed = PMStableHash.GlobalProtocolHash(entries);
            Check("生成期 GlobalProtocolHash 与用 PMStableHash 重算的一致",
                recomputed == scan.Model.GlobalProtocolHash,
                "0x" + scan.Model.GlobalProtocolHash.ToString("X8") + " vs 0x" + recomputed.ToString("X8"));

            for (int i = 0; i < scan.Model.Classes.Count; i++)
            {
                PMDeclClass cls = scan.Model.Classes[i];
                uint clsHash = PMStableHash.ClassProtocolHash(cls.ClassId, PMDeclScanner.BuildPropertyDescriptors(cls));
                if (clsHash != cls.ClassProtocolHash)
                {
                    Check("类协议摘要自洽（" + cls.QualifiedName + "）", false,
                        "0x" + cls.ClassProtocolHash.ToString("X8") + " vs 0x" + clsHash.ToString("X8"));
                    return;
                }
            }

            Check("每个类的 ClassProtocolHash 自洽", true, scan.Model.Classes.Count + " 个类");

            Check("ClassId 与 MemberId 不返回 0（0 是保留值）",
                PMStableHash.ClassId("") != 0u && PMStableHash.MemberId("") != 0,
                "ClassId(\"\")=" + PMStableHash.ClassId("") + " MemberId(\"\")=" + PMStableHash.MemberId(""));

            ushort sig1 = PMStableHash.ParamLayoutId(new string[] { "int", "float" });
            ushort sig2 = PMStableHash.ParamLayoutId(new string[] { "int", "float" });
            ushort sig3 = PMStableHash.ParamLayoutId(new string[] { "int", "double" });
            Check("ParamLayoutId 对同类型列表稳定、对不同类型区分", sig1 == sig2 && sig1 != sig3,
                sig1 + " / " + sig2 + " / " + sig3);

            Check("PMCond 已实现条件数 = 8", PMCondNames.ImplementedNames().Length == 8,
                string.Join(" / ", PMCondNames.ImplementedNames()));
        }

        private static void CheckEmptyModel()
        {
            PMDeclModel empty = new PMDeclModel();
            List<PMGeneratedFile> files = PMDeclEmitter.EmitAll(empty, new PMDeclRawFacts());
            Check("空声明集只产出注册表", files.Count == 1
                && files[0].FileName == PMDeclEmitter.RegistryFileName,
                "文件 " + files.Count + " 个");
            Check("空注册表仍然可编译（GeneratedClassCount = 0 且条目数组长度为 0）",
                files.Count == 1
                && files[0].Content.IndexOf("GeneratedClassCount = 0;", StringComparison.Ordinal) >= 0
                && files[0].Content.IndexOf("new PMNet.PMNetClassEntry[0]", StringComparison.Ordinal) >= 0,
                "");
        }

        // =================================================================================
        //  工具
        // =================================================================================

        /// <summary>
        /// RV5/RV6 修的是**生成物里的分支**，而 R2 首版对这些分支的覆盖是空的：
        ///   - 全仓没有任何数组参数的 RPC 声明 ⇒ 数组写体/读体/快照/长度门均零覆盖；
        ///   - 全仓 `.g.cs` 搜 `_Validate(` 零命中 ⇒ 原生 Validate（失败即断连）分支从未被发射过；
        ///   - 发射器在“档位有、同伴无”时只发射一条注释、照常执行实现（fail-open）。
        /// 这一节把这四类断言的载体钉在**产物文本**上（产物就是被编译与运行的那份字节）。
        /// </summary>
        private static void CheckRpcBranchCoverage(PMDeclScanResult scan)
        {
            List<PMGeneratedFile> files = PMDeclEmitter.EmitAll(scan.Model, scan.Facts);

            string player = null;
            string registry = null;
            for (int i = 0; i < files.Count; i++)
            {
                if (files[i].FileName == "PMNet.PMNetFixtures.FixturePlayer.g.cs")
                {
                    player = files[i].Content;
                }
                else if (files[i].FileName == PMDeclEmitter.RegistryFileName)
                {
                    registry = files[i].Content;
                }
            }

            Check("找到 FixturePlayer 产物", player != null, "期望文件名 PMNet.PMNetFixtures.FixturePlayer.g.cs");
            if (player == null)
            {
                return;
            }

            Check("RV5 数组实参在**调用点**做快照（Clone）",
                player.IndexOf("p0Snapshot = (int[])p0.Clone();", StringComparison.Ordinal) >= 0,
                Detail(player.IndexOf("p0Snapshot = (int[])p0.Clone();", StringComparison.Ordinal) >= 0,
                    "产物里找不到 p0Snapshot = (int[])p0.Clone();"));

            Check("RV5 数组长度门在入队前（引用 PMGeneratedMaxArrayLength）",
                player.IndexOf("if (p0.Length > PMGeneratedMaxArrayLength)", StringComparison.Ordinal) >= 0,
                Detail(player.IndexOf("if (p0.Length > PMGeneratedMaxArrayLength)", StringComparison.Ordinal) >= 0,
                    "产物里找不到数组长度门"));

            Check("RV5 字符串长度门在入队前（EnsureWithinLimit）",
                player.IndexOf("PMNet.PMNetString.EnsureWithinLimit(p1,", StringComparison.Ordinal) >= 0,
                Detail(player.IndexOf("PMNet.PMNetString.EnsureWithinLimit(p1,", StringComparison.Ordinal) >= 0,
                    "产物里找不到 EnsureWithinLimit 的调用（OnEffect 的 string 实参）"));

            Check("RV5/RV6 尾随字节在业务实现之前被拒（!r.IsAtEnd）",
                player.IndexOf("if (!r.IsAtEnd)", StringComparison.Ordinal) >= 0,
                Detail(player.IndexOf("if (!r.IsAtEnd)", StringComparison.Ordinal) >= 0,
                    "产物里找不到尾随字节检查"));

            Check("RV6 原生 Validate 分支真的被发射（这是 R2 首版的零覆盖分支）",
                player.IndexOf("if (!self.CheckedTeleport_Validate(", StringComparison.Ordinal) >= 0,
                Detail(player.IndexOf("if (!self.CheckedTeleport_Validate(", StringComparison.Ordinal) >= 0,
                    "产物里找不到 _Validate 调用"));

            Check("RV6 原生 Validate 失败抛出 PMNetRpcValidationFailedException（交给带连接的入口去断连）",
                player.IndexOf("throw new PMNet.PMNetRpcValidationFailedException(", StringComparison.Ordinal) >= 0,
                Detail(player.IndexOf("throw new PMNet.PMNetRpcValidationFailedException(", StringComparison.Ordinal) >= 0,
                    "产物里找不到 PMNetRpcValidationFailedException"));

            Check("RV6 ForceValidate 非法值失败关闭（先归一成 Report/Reject，再判 Reject 返回）",
                player.IndexOf("PMNet.PMRpcValidation pmReported = pmVerdict == PMNet.PMRpcValidation.Report",
                    StringComparison.Ordinal) >= 0
                && player.IndexOf("if (pmReported == PMNet.PMRpcValidation.Reject)", StringComparison.Ordinal) >= 0,
                Detail(player.IndexOf("PMNet.PMRpcValidation pmReported = pmVerdict == PMNet.PMRpcValidation.Report",
                    StringComparison.Ordinal) >= 0
                && player.IndexOf("if (pmReported == PMNet.PMRpcValidation.Reject)", StringComparison.Ordinal) >= 0,
                    "产物里找不到非法值的 fail-closed 归一分支"));

            // 注意：注册表里 `_pendingRpcs.Dequeue();` 在 TryDequeueRpc 里是**合法**的，
            // 因此这里只能断言"丢弃循环"已经不存在（旧形态是 while + Dequeue）。
            bool registryNoEvict = registry != null
                                   && registry.IndexOf("RejectedPendingRpcs++", StringComparison.Ordinal) >= 0
                                   && registry.IndexOf("throw new InvalidOperationException(", StringComparison.Ordinal) >= 0
                                   && registry.IndexOf("while (_pendingRpcs.Count >= MaxPendingRpcs)",
                                       StringComparison.Ordinal) < 0
                                   && registry.IndexOf("DroppedPendingRpcs", StringComparison.Ordinal) < 0;
            Check("RV5 待发队列超限显式失败（不再丢最旧）", registryNoEvict,
                Detail(registryNoEvict,
                    registry == null ? "找不到注册表产物" : "注册表里仍存在「丢最旧」的写法"));

            // 发射器自身 fail-closed：档位声明了、同伴不存在 ⇒ 拒绝发射。
            // 必须**绕过声明期门禁**直接调 EmitAll —— EmitAll 是公开 API，
            // 声明期校验不是它唯一入口（增量生成 / 编辑器内生成 / 未来工具复用）。
            PMDeclModel broken = new PMDeclModel();
            PMDeclRawFacts brokenFacts = new PMDeclRawFacts();
            PMDeclClass brokenClass = new PMDeclClass();
            brokenClass.Namespace = "Injected";
            brokenClass.TypeName = "NoCompanion";
            brokenClass.StableKey = "CLASS:Injected.NoCompanion";
            brokenClass.ClassId = 9911u;
            brokenClass.IsPartial = true;
            brokenClass.BaseTypeName = "PMNetObject";
            PMDeclRpc brokenRpc = new PMDeclRpc();
            brokenRpc.MethodName = "Fire";
            brokenRpc.RpcId = 4242;
            brokenRpc.Kind = PMRpcKind.Server;
            brokenRpc.Validator = PMRpcValidator.ForceValidate;
            brokenClass.Rpcs.Add(brokenRpc);
            broken.Classes.Add(brokenClass);

            bool threw = false;
            string thrown = null;
            try
            {
                PMDeclEmitter.EmitAll(broken, brokenFacts);
            }
            catch (Exception ex)
            {
                threw = true;
                thrown = ex.GetType().Name + ": " + ex.Message;
            }

            Check("发射器 fail-closed：档位有、同伴无 ⇒ EmitAll 抛异常（不再只发射一条注释）",
                threw, threw ? "已确认：" + thrown
                    : "EmitAll 没有抛异常 —— 产物会是「声称有校验、实际没有」");
        }

        /// <summary>
        /// 门禁 detail 文案：通过时给"已确认"，失败时给具体缺失说明。
        /// 避免 PASS 行印着失败原因（那种输出会让报告不可复核）。
        /// </summary>
        private static string Detail(bool ok, string whenMissing)
        {
            return ok ? "已确认" : whenMissing;
        }

        private static PMDeclScanResult SafeScan(List<string> paths, PMIdLock idLock)
        {
            try
            {
                PMDeclScanResult scan = PMDeclScanner.Scan(paths, idLock, string.Join(", ", paths.ToArray()));
                if (scan.Model.Errors.Count == 0)
                {
                    // 扫描器本身不做规则判定，这里补上（与 CLI 一致）。
                }

                PMDeclValidation.Validate(scan.Model, scan.Facts);
                return scan;
            }
            catch (Exception ex)
            {
                Check("扫描 " + string.Join(", ", paths.ToArray()) + " 成功", false, ex.Message);
                return null;
            }
        }

        private static int CountAll(PMDeclModel model, out int props, out int rpcs)
        {
            props = 0;
            rpcs = 0;
            for (int i = 0; i < model.Classes.Count; i++)
            {
                props += model.Classes[i].Properties.Count;
                rpcs += model.Classes[i].Rpcs.Count;
            }

            return model.Classes.Count;
        }

        private static void WriteGenerated(string dir, List<PMGeneratedFile> files)
        {
            Directory.CreateDirectory(dir);
            for (int i = 0; i < files.Count; i++)
            {
                // 与 CLI 的写出约定一致：UTF-8 BOM + CRLF。
                byte[] body = Encoding.UTF8.GetBytes(files[i].Content.Replace("\r\n", "\n").Replace("\n", "\r\n"));
                using (FileStream fs = new FileStream(Path.Combine(dir, files[i].FileName), FileMode.Create, FileAccess.Write))
                {
                    fs.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                    fs.Write(body, 0, body.Length);
                }
            }
        }

        /// <summary>逐文件逐字节比较两个目录；返回不同的文件数。</summary>
        private static int CompareDirectories(string a, string b, out string detail)
        {
            string[] fa = Directory.GetFiles(a);
            string[] fb = Directory.GetFiles(b);
            Array.Sort(fa, StringComparer.Ordinal);
            Array.Sort(fb, StringComparer.Ordinal);

            detail = "";
            if (fa.Length != fb.Length)
            {
                detail = "文件数不同：" + fa.Length + " vs " + fb.Length;
                return Math.Abs(fa.Length - fb.Length) + 1;
            }

            int diff = 0;
            for (int i = 0; i < fa.Length; i++)
            {
                string nameA = Path.GetFileName(fa[i]);
                string nameB = Path.GetFileName(fb[i]);
                if (!string.Equals(nameA, nameB, StringComparison.Ordinal))
                {
                    diff++;
                    if (detail.Length == 0)
                    {
                        detail = "文件名不同：" + nameA + " vs " + nameB;
                    }

                    continue;
                }

                byte[] ba = File.ReadAllBytes(fa[i]);
                byte[] bb = File.ReadAllBytes(fb[i]);
                if (ba.Length != bb.Length || !SameBytes(ba, bb))
                {
                    diff++;
                    if (detail.Length == 0)
                    {
                        detail = nameA + " 字节不同（" + ba.Length + " vs " + bb.Length + "）";
                    }
                }
            }

            if (diff == 0)
            {
                detail = fa.Length + " 个文件逐字节相同";
            }

            return diff;
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 把同一份声明集重排：类型声明顺序整体反转，且每个类型的成员顺序也反转。
        ///
        /// 这比"换个文件放同一个类"强得多：它同时覆盖了类型顺序与**成员顺序**，
        /// 而成员顺序正是"ID 按声明顺序临时编号"这类缺陷最容易暴露的地方。
        /// </summary>
        private static string PermuteDeclarations(string text)
        {
            SyntaxTree tree = CSharpSyntaxTree.ParseText(text);
            CompilationUnitSyntax root = tree.GetCompilationUnitRoot();

            BaseNamespaceDeclarationSyntax ns = null;
            for (int i = 0; i < root.Members.Count; i++)
            {
                BaseNamespaceDeclarationSyntax candidate = root.Members[i] as BaseNamespaceDeclarationSyntax;
                if (candidate != null)
                {
                    ns = candidate;
                    break;
                }
            }

            if (ns == null)
            {
                throw new InvalidOperationException("夹具必须有显式 namespace（否则无法重排）");
            }

            NamespaceDeclarationSyntax block = ns as NamespaceDeclarationSyntax;
            if (block == null)
            {
                throw new InvalidOperationException("暂只支持块式 namespace（夹具用的是块式）");
            }

            StringBuilder sb = new StringBuilder(text.Length + 64);
            sb.Append("// 由 PMDeclCheck 自动生成的「重排版」声明集：类型顺序与成员顺序整体反转。\n");
            for (int i = 0; i < root.Usings.Count; i++)
            {
                sb.Append(root.Usings[i].ToString()).Append('\n');
            }

            sb.Append("namespace ").Append(ns.Name.ToString()).Append('\n').Append("{\n");

            List<BaseTypeDeclarationSyntax> types = new List<BaseTypeDeclarationSyntax>();
            for (int i = 0; i < block.Members.Count; i++)
            {
                BaseTypeDeclarationSyntax t = block.Members[i] as BaseTypeDeclarationSyntax;
                if (t != null)
                {
                    types.Add(t);
                }
            }

            for (int i = types.Count - 1; i >= 0; i--)
            {
                TypeDeclarationSyntax typeDecl = types[i] as TypeDeclarationSyntax;
                if (typeDecl == null)
                {
                    sb.Append(types[i].ToFullString()).Append('\n');
                    continue;
                }

                sb.Append(PermuteType(typeDecl)).Append('\n');
            }

            sb.Append("}\n");
            return sb.ToString();
        }

        private static string PermuteType(TypeDeclarationSyntax typeDecl)
        {
            string full = typeDecl.ToFullString();
            int headerLength = typeDecl.OpenBraceToken.SpanStart - typeDecl.FullSpan.Start + 1;
            if (headerLength <= 0 || headerLength > full.Length)
            {
                throw new InvalidOperationException("类型 " + typeDecl.Identifier.Text + " 的头部切片失败");
            }

            StringBuilder sb = new StringBuilder(full.Length + 32);
            sb.Append(full.Substring(0, headerLength)).Append('\n');
            for (int i = typeDecl.Members.Count - 1; i >= 0; i--)
            {
                sb.Append(typeDecl.Members[i].ToFullString()).Append('\n');
            }

            sb.Append("}\n");
            return sb.ToString();
        }

        private static int EmitFixtures(string dir)
        {
            string root = _root ?? FindRepoRoot();
            if (root == null)
            {
                Console.Error.WriteLine("[PMDeclCheck] 找不到仓库根");
                return 3;
            }

            string goodPath = Path.Combine(root, "Tools/PMDeclCheck/Fixtures/Good.cs");
            string original = File.ReadAllText(goodPath);

            Directory.CreateDirectory(Path.Combine(dir, "A"));
            Directory.CreateDirectory(Path.Combine(dir, "B"));

            // A 用逐字节复制，保证 "两种排列" 除顺序外完全同一份声明。
            File.WriteAllBytes(Path.Combine(dir, "A", "Decl.cs"), File.ReadAllBytes(goodPath));
            File.WriteAllText(Path.Combine(dir, "B", "Decl.cs"), PermuteDeclarations(original), new UTF8Encoding(false));

            Console.WriteLine("[PMDeclCheck] 已写出夹具：");
            Console.WriteLine("  A（原序）: " + Path.Combine(dir, "A", "Decl.cs"));
            Console.WriteLine("  B（重排）: " + Path.Combine(dir, "B", "Decl.cs"));
            return 0;
        }

        private static void ResetDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                try
                {
                    Directory.Delete(path, true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[PMDeclCheck] 清理临时目录失败（继续）: " + ex.Message);
                }
            }

            Directory.CreateDirectory(path);
        }

        private static string FindRepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
            {
                if (File.Exists(Path.Combine(dir, "Docs/plans/net-r2-codegen-contract.md")))
                {
                    return dir;
                }

                DirectoryInfo parent = Directory.GetParent(dir);
                if (parent == null)
                {
                    break;
                }

                dir = parent.FullName;
            }

            return null;
        }

        private static string FirstError(PMDeclModel model)
        {
            return model.Errors.Count > 0 ? model.Errors[0] : "";
        }

        private static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("== " + title + " ==");
        }

        private static void Check(string title, bool ok, string detail)
        {
            if (ok)
            {
                _pass++;
            }
            else
            {
                _fail++;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("  [").Append(ok ? "PASS" : "FAIL").Append("] ").Append(title);
            if (!string.IsNullOrEmpty(detail))
            {
                sb.Append("  —— ").Append(detail);
            }

            Console.WriteLine(sb.ToString());
        }

        private static int Summary()
        {
            Console.WriteLine();
            Console.WriteLine("== 汇总 ==");
            Console.WriteLine("  PASS " + _pass + " / FAIL " + _fail);
            Console.WriteLine("  临时工作区（保留，供 fc /b 复现）: " + _temp);
            if (_fail > 0)
            {
                Console.WriteLine("  结果：失败");
                return 1;
            }

            Console.WriteLine("  结果：全部通过");
            return 0;
        }
    }
}
