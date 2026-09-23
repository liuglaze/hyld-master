// PMNet 编织便利层 —— 纯 BCL 回归（**不启动 Unity、不调用任何 Unity native**）。
//
// 契约：Docs/plans/net-rpc-weaving-contract.md §4（Editor 与构建接口）。
//
// 为什么需要它：
//   `Client/Assets/Editor/PMNetWeaving/*.cs` 里有两类东西必须被真实执行验证，而它们都**不需要 Unity**：
//     1) `PMNetToolProcess.cs` —— 外部工具执行器。它的正确性是"超时/并发/分块有界读取/有界排空/
//        MSVCRT 引用"这些**可构造**的进程行为，正好可以在纯 C# 进程里用受控子进程反复验证；
//     2) `PMNetWeavingPolicy.cs` —— 纯决策策略（文件名匹配、Player 目标收集、工具输入内容指纹、
//        生成物契约探针、decl-check 退出码归类）。把这些判断留在"只有开编辑器才跑得到"的状态，
//        等于永远不验证。
//
// 本工程**链接同一份源码**（不是复制），因此它证明的就是编辑器里实际加载的那份实现。
//
// 边界（诚实口径）：
//   · 不编译 `PMNetWeavingEditor.cs`（它需要真实 Unity API，那是 Tools/PMNetWeavingEditorCheck 的职责）；
//     但会对其**源码做只读文本断言**，防止已冻结的修复（删门禁开关 / 删 Player 路径推测 /
//     preflight 只核对不生成）被静默回退。
//   · 不证明 Unity 回调时序、Player 管线位置、IL2CPP/Mono 运行 —— 那些仍是 PENDING_USER。
//
// 运行：dotnet Tools/PMNetWeavingEditorTest/bin/Release/net8.0/PMNetWeavingEditorTest.dll [--repo <仓库根>]
// 退出码：0 = 全部通过；1 = 有失败。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PMNet.Weaving.Editor;

namespace PMNetWeavingEditorTest
{
    internal static class Program
    {
        private static int _passed;
        private static int _failed;
        private static readonly List<string> FailureMessages = new List<string>();

        private static string _repoRoot;
        private static string _editorSourcePath;

        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0].StartsWith("--child-", StringComparison.Ordinal))
            {
                return ChildMain(args);
            }

            _repoRoot = ResolveRepoRoot(args);
            _editorSourcePath = _repoRoot == null
                ? null
                : Path.Combine(_repoRoot, "Client", "Assets", "Editor", "PMNetWeaving", "PMNetWeavingEditor.cs");

            Console.WriteLine("[PMNetWeavingEditorTest] 仓库根=" + (_repoRoot ?? "(未解析)"));
            Console.WriteLine("[PMNetWeavingEditorTest] 子进程宿主=" + DescribeChildHost());
            Console.WriteLine();

            // ---------------------------------------------------------------- 引用规则（纯函数）
            Run("QuoteArgument: 无空白不加引号", Test_QuoteArgument_Bare);
            Run("QuoteArgument: 空白/引号/结尾反斜杠", Test_QuoteArgument_EdgeCases);

            // ---------------------------------------------------------------- 真实子进程往返
            Run("进程: 参数往返（空格/引号/反斜杠/中文/cmd 元字符/空串）", Test_Process_ArgumentRoundTrip);
            Run("进程: stdout 无换行超 64KiB 有界且不挂", Test_Process_StdoutFloodNoNewline);
            Run("进程: stdout+stderr 同时超 64KiB 有界", Test_Process_StdoutAndStderrFlood);
            Run("进程: 非 0 退出不算成功", Test_Process_NonZeroExit);
            Run("进程: 不存在的可执行文件不抛异常", Test_Process_MissingExecutable);
            Run("进程: 并发第二次调用被显式拒绝", Test_Process_ConcurrencyRejected);
            Run("进程: 超时被终止且之后可复用", Test_Process_TimeoutThenReusable);
            Run("进程: 孙进程继承管道不无限挂且按失败处理", Test_Process_InheritedPipeDoesNotHang);

            // ---------------------------------------------------------------- Player 目标策略
            Run("策略: 只认 report.files 里精确名 + 绝对路径", Test_Policy_PlayerCandidates);
            Run("策略: Editor相对路径以Unity项目而非仓库为基准", Test_EditorRelativePath);
            Run("策略: Player 候选 API 不接受 outputPath（无推测回退）", Test_Policy_PlayerCandidateApiShape);
            Run("策略: 目标缺失文案明确拒绝产出 Player", Test_Policy_PlayerMissingDescription);
            Run("策略: 目标文件名精确匹配（不吞 Editor/firstpass）", Test_Policy_TargetFileNameExact);

            // ---------------------------------------------------------------- 工具输入内容指纹
            Run("策略: 工具输入集合排除 bin/obj/editor-tool", Test_Policy_ToolInputCollection);
            Run("策略: 内容指纹对改内容/删文件/加文件敏感，对纯 mtime 变化不敏感", Test_Policy_ToolFingerprintContentBased);
            Run("策略: 指纹包含 root.targets 等显式额外依赖", Test_Policy_FingerprintIncludesExtraInputs);
            Run("策略: 指纹旁车文件读写往返", Test_Policy_FingerprintSidecar);

            // ---------------------------------------------------------------- 生成物契约探针 / 声明指纹
            Run("策略: 代码 token 探针跳过注释行", Test_Policy_CodeTokenSkipsComments);
            Run("策略: 生成物探针签名对同长度改写敏感", Test_Policy_GeneratedProbeSignature);
            Run("策略: 声明源候选排除 .g.cs 与 obj/bin", Test_Policy_DeclarationCandidateFilter);
            Run("策略: 声明指纹忽略生成物、对源改动敏感", Test_Policy_DeclarationFingerprint);
            Run("策略: decl-check 退出码归类", Test_Policy_ClassifyDeclCheck);
            Run("对照: 真实生成物确实含有编织契约符号", Test_RealGeneratedArtifactHasContract);

            // ---------------------------------------------------------------- Unity 侧接线（源码级回归）
            Run("接线: Editor 源码没有门禁开关/路径推测/假新鲜", Test_EditorSourceFrozenFixes);

            Console.WriteLine();
            Console.WriteLine("[PMNetWeavingEditorTest] 通过 " + _passed.ToString(CultureInfo.InvariantCulture)
                + " / 失败 " + _failed.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < FailureMessages.Count; i++)
            {
                Console.WriteLine("  FAIL: " + FailureMessages[i]);
            }

            return _failed == 0 ? 0 : 1;
        }

        // ============================================================================================
        //  测试宿主
        // ============================================================================================
        private static void Run(string name, Action body)
        {
            try
            {
                body();
                _passed++;
                Console.WriteLine("  [PASS] " + name);
            }
            catch (Exception ex)
            {
                _failed++;
                string message = name + " -> " + ex.GetType().Name + ": " + ex.Message;
                FailureMessages.Add(message);
                Console.WriteLine("  [FAIL] " + message);
            }
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception(message);
            }
        }

        private static void AssertEqual(string expected, string actual, string message)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new Exception(message + "（expected=[" + expected + "] actual=[" + actual + "]）");
            }
        }

        private static void AssertEqualInt(int expected, int actual, string message)
        {
            if (expected != actual)
            {
                throw new Exception(message + "（expected=" + expected.ToString(CultureInfo.InvariantCulture)
                    + " actual=" + actual.ToString(CultureInfo.InvariantCulture) + "）");
            }
        }

        private static string NewTempDir(string label)
        {
            string dir = Path.Combine(Path.GetTempPath(), "pmnet-editor-test-" + label + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void WriteText(string path, string text)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        private static int CountOccurrences(string text, string needle)
        {
            int count = 0;
            int index = 0;
            while (true)
            {
                int found = text.IndexOf(needle, index, StringComparison.Ordinal);
                if (found < 0)
                {
                    return count;
                }

                count++;
                index = found + needle.Length;
            }
        }

        private static string ResolveRepoRoot(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--repo", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetFullPath(args[i + 1]);
                }
            }

            DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string probe = Path.Combine(dir.FullName, "Client", "Assets", "Editor", "PMNetWeaving", "PMNetWeavingPolicy.cs");
                if (File.Exists(probe))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            return null;
        }

        // ============================================================================================
        //  子进程宿主构造（自己启动自己，作为**受控**被测子进程）
        // ============================================================================================
        private static bool HostIsDotnet()
        {
            string path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            return string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase);
        }

        private static string SelfAssemblyPath()
        {
            Assembly entry = Assembly.GetEntryAssembly();
            if (entry != null && !string.IsNullOrEmpty(entry.Location))
            {
                return entry.Location;
            }

            return Path.Combine(AppContext.BaseDirectory, "PMNetWeavingEditorTest.dll");
        }

        private static string DescribeChildHost()
        {
            return (HostIsDotnet() ? "dotnet " + SelfAssemblyPath() : Environment.ProcessPath);
        }

        private static void BuildChildCommand(List<string> childArgs, out string fileName, out List<string> arguments)
        {
            fileName = Environment.ProcessPath;
            arguments = new List<string>();
            if (HostIsDotnet())
            {
                arguments.Add(SelfAssemblyPath());
            }

            arguments.AddRange(childArgs);
        }

        private static PMNetToolResult RunChild(int timeoutMs, params string[] childArgs)
        {
            string fileName;
            List<string> arguments;
            BuildChildCommand(new List<string>(childArgs), out fileName, out arguments);
            return PMNetToolProcess.Run(fileName, arguments, AppContext.BaseDirectory, timeoutMs);
        }

        private static void WaitUntil(Func<bool> condition, int timeoutMs, string what)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (condition())
                {
                    return;
                }

                Thread.Sleep(20);
            }

            throw new Exception("等待超时：" + what);
        }

        // ============================================================================================
        //  引用规则
        // ============================================================================================
        private static void Test_QuoteArgument_Bare()
        {
            AssertEqual("plain", PMNetToolProcess.QuoteArgument("plain"), "无空白参数不应加引号");
            AssertEqual(@"C:\dir\file.dll", PMNetToolProcess.QuoteArgument(@"C:\dir\file.dll"), "普通路径不应加引号");
        }

        private static void Test_QuoteArgument_EdgeCases()
        {
            AssertEqual("\"with space\"", PMNetToolProcess.QuoteArgument("with space"), "空白参数必须加引号");
            AssertEqual("\"a\\\"b\"", PMNetToolProcess.QuoteArgument("a\"b"), "引号前必须是奇数个反斜杠");
            // 无空白/引号的结尾反斜杠**无需**加倍（MSVCRT 只在引号前处理反斜杠）；
            // 反斜杠加倍只在参数被引号包起来时才有意义，因此下面两类要分开断言。
            AssertEqual("trail\\", PMNetToolProcess.QuoteArgument("trail\\"), "无空白/引号时不该加引号");
            AssertEqual("\\", PMNetToolProcess.QuoteArgument("\\"), "单个反斜杠不带引号触发条件");
            AssertEqual("\"a b\\\\\"", PMNetToolProcess.QuoteArgument("a b\\"), "有空白时结尾反斜杠必须翻倍");
            AssertEqual("\"\"", PMNetToolProcess.QuoteArgument(string.Empty), "空串要写成空引号对");
            AssertEqual("\"\"", PMNetToolProcess.QuoteArgument(null), "null 要写成空引号对");
            AssertEqual("\"a \\\" b\"", PMNetToolProcess.QuoteArgument("a \" b"), "引号+空格混合");
            AssertEqual("a\\\\b", PMNetToolProcess.QuoteArgument("a\\\\b"), "中间反斜杠不需要处理（无引号/空白）");
            AssertEqual("\"a b\\\\c\\\"d\"", PMNetToolProcess.QuoteArgument("a b\\\\c\"d"), "反斜杠+引号混合");
        }

        // ============================================================================================
        //  真实子进程往返
        // ============================================================================================
        private static void Test_Process_ArgumentRoundTrip()
        {
            string[] exotic = new string[]
            {
                "plain",
                "with space",
                "with\ttab",
                "quote\"inside",
                "trail\\",
                "back\\slash",
                "back\\\"quote",
                "中文 参数【引号】",
                "\"fully quoted\"",
                string.Empty,
                "50%|caret^&<>|pipe>semi;colon"
            };

            List<string> childArgs = new List<string>();
            childArgs.Add("--child-echo-args");
            childArgs.AddRange(exotic);

            PMNetToolResult result = RunChild(30000, childArgs.ToArray());
            AssertTrue(result.Success, "往返子进程必须成功：" + result.ShortReason() + "\n" + result.TailOfStdErr(2000));

            string[] lines = result.StdOut.Replace("\r\n", "\n").Split('\n');
            List<string> received = new List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                if (i == lines.Length - 1 && lines[i].Length == 0)
                {
                    continue;   // 结尾换行产生的空行
                }

                received.Add(lines[i]);
            }

            AssertEqualInt(exotic.Length, received.Count, "参数个数必须一致");
            for (int i = 0; i < exotic.Length; i++)
            {
                AssertEqual(exotic[i], received[i], "第 " + i.ToString(CultureInfo.InvariantCulture) + " 个参数必须逐字节一致");
            }
        }

        private static void Test_Process_StdoutFloodNoNewline()
        {
            const int count = 200000;
            PMNetToolResult result = RunChild(60000, "--child-flood-stdout", count.ToString(CultureInfo.InvariantCulture));

            AssertTrue(result.Success, "flood 子进程必须成功退出（只是输出被截断）：" + result.ShortReason());
            AssertTrue(result.OutputTruncated, "超过 64KiB 必须标记截断");
            AssertTrue(result.StdOut.Length <= PMNetToolProcess.MaxCapturedCharsPerStream + 64,
                "截断后长度必须受预算约束，实际=" + result.StdOut.Length.ToString(CultureInfo.InvariantCulture));

            int xCount = CountOccurrences(result.StdOut, "x");
            AssertEqualInt(PMNetToolProcess.MaxCapturedCharsPerStream, xCount,
                "无换行输出必须恰好保留预算内字符数（说明读取是分块的，不是按行缓冲）");
        }

        private static void Test_Process_StdoutAndStderrFlood()
        {
            const int count = 200000;
            PMNetToolResult result = RunChild(60000, "--child-flood-both", count.ToString(CultureInfo.InvariantCulture));

            AssertTrue(result.Success, "双流 flood 必须成功退出且不死锁：" + result.ShortReason());
            AssertTrue(result.OutputTruncated, "两路都必须标记截断");
            AssertEqualInt(PMNetToolProcess.MaxCapturedCharsPerStream, CountOccurrences(result.StdOut, "O"), "stdout 预算内字符数");
            AssertEqualInt(PMNetToolProcess.MaxCapturedCharsPerStream, CountOccurrences(result.StdErr, "E"), "stderr 预算内字符数");
        }

        private static void Test_Process_NonZeroExit()
        {
            PMNetToolResult result = RunChild(30000, "--child-exit", "3");
            AssertTrue(result.Started, "进程应被启动");
            AssertTrue(!result.Success, "退出码 3 不能算成功");
            AssertEqualInt(3, result.ExitCode, "退出码必须被如实记录");
            AssertTrue(result.ShortReason().IndexOf("3", StringComparison.Ordinal) >= 0, "失败原因必须包含退出码");
        }

        private static void Test_Process_MissingExecutable()
        {
            string missing = Path.Combine(Path.GetTempPath(), "pmnet-no-such-exe-" + Guid.NewGuid().ToString("N") + ".exe");
            List<string> args = new List<string>();
            args.Add("--help");

            PMNetToolResult result = PMNetToolProcess.Run(missing, args, Path.GetTempPath(), 5000);
            AssertTrue(!result.Success, "不存在的可执行文件不能算成功");
            AssertTrue(!string.IsNullOrEmpty(result.Failure), "必须给出显式失败原因（不能静默）");
            AssertTrue(!result.Started, "未启动的进程 Started 必须为 false");
        }

        private static void Test_Process_ConcurrencyRejected()
        {
            PMNetToolResult holder = null;
            Task holderTask = Task.Run(delegate { holder = RunChild(60000, "--child-sleep", "1500"); });

            try
            {
                WaitUntil(delegate { return PMNetToolProcess.IsRunning; }, 10000, "第一个工具进入运行中");

                PMNetToolResult second = RunChild(30000, "--child-echo-args", "x");
                AssertTrue(!second.Started, "第二次并发调用必须被**拒绝**而不是排队执行");
                AssertTrue(second.Failure.IndexOf("并发", StringComparison.Ordinal) >= 0,
                    "拒绝原因必须点明并发，实际=[" + second.Failure + "]");
            }
            finally
            {
                holderTask.Wait(60000);
            }

            AssertTrue(holder != null && holder.Success, "第一个调用本身必须正常完成");
            AssertTrue(!PMNetToolProcess.IsRunning, "并发闸门必须在结束后复位");
        }

        private static void Test_Process_TimeoutThenReusable()
        {
            Stopwatch watch = Stopwatch.StartNew();
            PMNetToolResult timedOut = RunChild(600, "--child-sleep", "30000");
            watch.Stop();

            AssertTrue(timedOut.TimedOut, "必须标记超时");
            AssertTrue(!timedOut.Success, "超时不能算成功");
            AssertTrue(watch.ElapsedMilliseconds < 15000,
                "超时必须真的有界（实际 " + watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms）");
            AssertTrue(!PMNetToolProcess.IsRunning, "超时后并发闸门必须复位（否则编辑器再也跑不了工具）");

            PMNetToolResult after = RunChild(30000, "--child-echo-args", "after-timeout");
            AssertTrue(after.Success, "超时被终止后必须能再次运行工具：" + after.ShortReason() + "\n" + after.TailOfStdErr(500));
            AssertTrue(after.StdOut.IndexOf("after-timeout", StringComparison.Ordinal) >= 0, "后续调用必须真的执行");
        }

        private static void Test_Process_InheritedPipeDoesNotHang()
        {
            // 子进程立刻退出（exit 0），但它的孙进程继承了 stdout/stderr 管道并继续存活。
            // 旧实现（无参 WaitForExit / ReadToEnd）在这种输入下会永久挂住；新实现必须有界排空，
            // 并且**按失败处理**（"进程 exit 0 但流没关闭"不能算成功）。
            Stopwatch watch = Stopwatch.StartNew();
            PMNetToolResult result = RunChild(60000, "--child-leak-grandchild", "6000");
            watch.Stop();

            AssertTrue(watch.ElapsedMilliseconds < 30000,
                "有界排空不允许无限等待（实际 " + watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms）");
            AssertTrue(!result.Success, "进程退出 0 但输出流未关闭必须按失败处理");
            AssertTrue(result.Failure.IndexOf("输出流", StringComparison.Ordinal) >= 0,
                "失败原因必须点明输出流未关闭，实际=[" + result.Failure + "]");
            AssertTrue(!PMNetToolProcess.IsRunning, "放弃排空后并发闸门必须复位");
        }

        // ============================================================================================
        //  Player 目标策略
        // ============================================================================================
        private static void Test_Policy_PlayerCandidates()
        {
            string sep = Path.DirectorySeparatorChar.ToString();
            List<string> reported = new List<string>();
            reported.Add(@"C:\out\Game_Data\Managed\Assembly-CSharp.dll");     // 命中
            reported.Add("C:Assembly-CSharp.dll");
            reported.Add(((char)92) + "Assembly-CSharp.dll");
            reported.Add("Assembly-CSharp.dll");                              // 相对路径：不认
            reported.Add(@"C:\out\Managed\Assembly-CSharp-Editor.dll");        // 前缀相同但不同名：不认
            reported.Add(@"C:\out\Managed\Assembly-CSharp-firstpass.dll");     // 同上
            reported.Add(@"C:\out2\Managed" + sep + "assembly-csharp.dll");  // 不同目录 + 不同大小写：命中（Windows 语义）
            reported.Add(@"C:\out\Game_Data\Managed\Assembly-CSharp.dll");     // 与第 1 项重复：去重
            reported.Add(@"C:\out\Game_Data\Managed\Other.dll");              // 无关
            reported.Add(string.Empty);                                       // 空项

            List<string> candidates = PMNetWeavingPolicy.CollectPlayerScriptDllCandidates(reported);
            AssertEqualInt(2, candidates.Count, "只应有 2 个绝对路径候选");
            AssertEqual(@"C:\out\Game_Data\Managed\Assembly-CSharp.dll", candidates[0], "第一个候选");
            AssertEqual(@"C:\out2\Managed" + sep + "assembly-csharp.dll", candidates[1], "第二个候选");
        }

        private static void Test_EditorRelativePath()
        {
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pm-editor-project"));
            string relative = "Library/ScriptAssemblies/Assembly-CSharp.dll";
            string expected = Path.GetFullPath(Path.Combine(root, relative));
            AssertEqual(expected, PMNetWeavingPolicy.ResolveEditorAssemblyPath(relative, root), "相对路径按Unity项目根定位");
            AssertEqual(expected, PMNetWeavingPolicy.ResolveEditorAssemblyPath(expected, root), "绝对路径不二次拼接");
            AssertTrue(!PMNetWeavingPolicy.IsFullyQualifiedPath("C:Assembly-CSharp.dll"), "盘符相对路径必须拒绝");
            AssertTrue(!PMNetWeavingPolicy.IsFullyQualifiedPath(((char)92) + "Assembly-CSharp.dll"), "当前盘根相对路径必须拒绝");
        }

        private static void Test_Policy_PlayerCandidateApiShape()
        {
            MethodInfo method = typeof(PMNetWeavingPolicy).GetMethod(
                "CollectPlayerScriptDllCandidates",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            AssertTrue(method != null, "CollectPlayerScriptDllCandidates 必须存在");
            AssertEqualInt(1, method.GetParameters().Length,
                "只允许一个参数（report.files 的路径列表）：任何额外的 outputPath/platform 参数都意味着"
                + "恢复了\"从最终包目录猜旧 DLL\"的回退，那是被明确删除的行为");
        }

        private static void Test_Policy_PlayerMissingDescription()
        {
            List<string> reported = new List<string>();
            reported.Add(@"C:\out\Game_Data\Managed\Other.dll");

            List<string> candidates = PMNetWeavingPolicy.CollectPlayerScriptDllCandidates(reported);
            AssertEqualInt(0, candidates.Count, "没有精确命名的绝对路径时不允许有候选（也就不会去织错文件）");

            string text = PMNetWeavingPolicy.DescribeMissingPlayerTarget(reported);
            AssertTrue(text.IndexOf("拒绝产出未编织的 Player", StringComparison.Ordinal) >= 0, "文案必须明确构建失败");
            AssertTrue(text.IndexOf(@"C:\out\Game_Data\Managed\Other.dll", StringComparison.Ordinal) >= 0, "文案要带上实际清单");

            string empty = PMNetWeavingPolicy.DescribeMissingPlayerTarget(new List<string>());
            AssertTrue(empty.IndexOf("BuildReport.files 为空", StringComparison.Ordinal) >= 0, "空清单要有专门说明");
        }

        private static void Test_Policy_TargetFileNameExact()
        {
            AssertTrue(PMNetWeavingPolicy.IsTargetAssemblyFileName("Assembly-CSharp.dll"), "精确名必须命中");
            AssertTrue(PMNetWeavingPolicy.IsTargetAssemblyFileName(@"C:\lib\Assembly-CSharp.dll"), "带路径的精确名必须命中");
            AssertTrue(PMNetWeavingPolicy.IsTargetAssemblyFileName(@"C:\lib\assembly-csharp.DLL"), "大小写不敏感");
            AssertTrue(!PMNetWeavingPolicy.IsTargetAssemblyFileName("Assembly-CSharp-Editor.dll"), "Editor 程序集不能命中");
            AssertTrue(!PMNetWeavingPolicy.IsTargetAssemblyFileName("Assembly-CSharp-firstpass.dll"), "firstpass 不能命中");
            AssertTrue(!PMNetWeavingPolicy.IsTargetAssemblyFileName("Assembly-CSharp.dll.bak"), "后缀变体不能命中");
            AssertTrue(!PMNetWeavingPolicy.IsTargetAssemblyFileName(null), "null 不能命中");
            AssertTrue(!PMNetWeavingPolicy.IsTargetAssemblyFileName(string.Empty), "空串不能命中");
        }

        // ============================================================================================
        //  工具输入内容指纹
        // ============================================================================================
        private static void Test_Policy_ToolInputCollection()
        {
            string root = NewTempDir("tool-inputs");
            try
            {
                string proj = Path.Combine(root, "proj");
                WriteText(Path.Combine(proj, "tool.csproj"), "<Project />");
                WriteText(Path.Combine(proj, "a.cs"), "// a");
                WriteText(Path.Combine(proj, "bin", "x.cs"), "// must be excluded");
                WriteText(Path.Combine(proj, "obj", "y.cs"), "// must be excluded");
                WriteText(Path.Combine(proj, "editor-tool", "z.cs"), "// must be excluded");
                WriteText(Path.Combine(proj, "notes.txt"), "not an input");

                string dep = Path.Combine(root, "dep");
                WriteText(Path.Combine(dep, "dep.cs"), "// dep");
                string rootTargets = Path.Combine(root, "Directory.Build.targets");
                WriteText(rootTargets, "<Project />");

                List<string> extras = new List<string>();
                extras.Add(dep);
                extras.Add(rootTargets);
                List<string> excluded = new List<string>();
                excluded.Add("bin");
                excluded.Add("obj");
                excluded.Add("editor-tool");

                List<string> files = PMNetWeavingPolicy.CollectToolInputFiles(Path.Combine(proj, "tool.csproj"), extras, excluded);

                AssertTrue(files.Contains(Path.Combine(proj, "a.cs")), "工程内 .cs 必须包含");
                AssertTrue(files.Contains(Path.Combine(proj, "tool.csproj")), "工程文件必须包含");
                AssertTrue(files.Contains(Path.Combine(dep, "dep.cs")), "显式额外依赖目录必须包含");
                AssertTrue(files.Contains(rootTargets), "显式额外依赖文件（root.targets）必须包含");
                AssertTrue(!files.Contains(Path.Combine(proj, "bin", "x.cs")), "bin 必须排除");
                AssertTrue(!files.Contains(Path.Combine(proj, "obj", "y.cs")), "obj 必须排除");
                AssertTrue(!files.Contains(Path.Combine(proj, "editor-tool", "z.cs")), "editor-tool 必须排除");
                AssertTrue(!files.Contains(Path.Combine(proj, "notes.txt")), "非输入后缀必须排除");
                AssertEqualInt(4, files.Count, "输入集合大小");
            }
            finally
            {
                TryDelete(root);
            }
        }

        private static void Test_Policy_ToolFingerprintContentBased()
        {
            string root = NewTempDir("tool-fingerprint");
            try
            {
                string proj = Path.Combine(root, "proj");
                WriteText(Path.Combine(proj, "tool.csproj"), "<Project />");
                WriteText(Path.Combine(proj, "a.cs"), "class A { }");
                string dep = Path.Combine(root, "dep");
                WriteText(Path.Combine(dep, "dep.cs"), "class D { }");
                string rootTargets = Path.Combine(root, "Directory.Build.targets");
                WriteText(rootTargets, "<Project />");

                List<string> extras = new List<string>();
                extras.Add(dep);
                extras.Add(rootTargets);
                List<string> excluded = new List<string>();
                excluded.Add("bin");
                excluded.Add("obj");
                excluded.Add("editor-tool");

                Func<string> fingerprint = delegate
                {
                    List<string> files = PMNetWeavingPolicy.CollectToolInputFiles(Path.Combine(proj, "tool.csproj"), extras, excluded);
                    return PMNetWeavingPolicy.ComputeFileSetFingerprint(root, files);
                };

                string f1 = fingerprint();

                // 1) 纯 mtime 变化（内容不变）：内容指纹**不应**改变（这是它相对"比 mtime"的改进）。
                DateTime stamp = DateTime.UtcNow.AddMinutes(-30);
                File.SetLastWriteTimeUtc(Path.Combine(proj, "a.cs"), stamp);
                string f2 = fingerprint();
                AssertEqual(f1, f2, "只改 mtime 不应触发重建（内容指纹按内容判定）");

                // 2) 内容变化（**长度相同**）：必须改变（mtime 比较在这里可能因为时间精度而漏检）。
                WriteText(Path.Combine(dep, "dep.cs"), "class E { }");
                File.SetLastWriteTimeUtc(Path.Combine(dep, "dep.cs"), stamp);
                string f3 = fingerprint();
                AssertTrue(!string.Equals(f1, f3, StringComparison.Ordinal), "同长度内容改写必须改变指纹");

                // 3) 删除依赖文件：必须改变（旧实现的 mtime 比较对删除完全不敏感）。
                WriteText(Path.Combine(dep, "dep.cs"), "class D { }");
                string f4 = fingerprint();
                AssertEqual(f1, f4, "恢复内容后指纹应回到原值");
                File.Delete(Path.Combine(dep, "dep.cs"));
                string f5 = fingerprint();
                AssertTrue(!string.Equals(f1, f5, StringComparison.Ordinal), "删除依赖文件必须改变指纹");

                // 4) 新增文件：必须改变。
                WriteText(Path.Combine(dep, "dep.cs"), "class D { }");
                WriteText(Path.Combine(proj, "b.cs"), "class B { }");
                string f6 = fingerprint();
                AssertTrue(!string.Equals(f1, f6, StringComparison.Ordinal), "新增输入文件必须改变指纹");

                // 5) 重命名：必须改变（相对路径进入摘要）。
                File.Move(Path.Combine(proj, "b.cs"), Path.Combine(proj, "c.cs"));
                string f7 = fingerprint();
                AssertTrue(!string.Equals(f6, f7, StringComparison.Ordinal), "重命名输入文件必须改变指纹");
            }
            finally
            {
                TryDelete(root);
            }
        }

        private static void Test_Policy_FingerprintIncludesExtraInputs()
        {
            string root = NewTempDir("tool-extras");
            try
            {
                string proj = Path.Combine(root, "proj");
                WriteText(Path.Combine(proj, "tool.csproj"), "<Project />");
                string rootTargets = Path.Combine(root, "Directory.Build.targets");
                WriteText(rootTargets, "<Project><!-- v1 --></Project>");

                List<string> extras = new List<string>();
                extras.Add(rootTargets);
                List<string> excluded = new List<string>();

                List<string> files = PMNetWeavingPolicy.CollectToolInputFiles(Path.Combine(proj, "tool.csproj"), extras, excluded);
                string before = PMNetWeavingPolicy.ComputeFileSetFingerprint(root, files);

                // 仓库级 Directory.Build.targets 是**真实输入**（RPC 编织目标就写在那里）：
                // 它变化必须让工具重建，否则会出现"改了构建接线却继续跑旧工具"。
                WriteText(rootTargets, "<Project><!-- v2 --></Project>");
                List<string> files2 = PMNetWeavingPolicy.CollectToolInputFiles(Path.Combine(proj, "tool.csproj"), extras, excluded);
                string after = PMNetWeavingPolicy.ComputeFileSetFingerprint(root, files2);

                AssertTrue(!string.Equals(before, after, StringComparison.Ordinal),
                    "root.targets 内容变化必须改变工具输入指纹");
            }
            finally
            {
                TryDelete(root);
            }
        }

        private static void Test_Policy_FingerprintSidecar()
        {
            string root = NewTempDir("tool-sidecar");
            try
            {
                string toolDll = Path.Combine(root, "editor-tool", "PMNetWeaver.dll");
                WriteText(toolDll, "not a real dll");
                string sidecar = PMNetWeavingPolicy.ToolFingerprintSidecarPath(toolDll);
                AssertEqual(toolDll + ".pmnet-inputs", sidecar, "旁车文件命名");

                AssertTrue(PMNetWeavingPolicy.TryReadToolFingerprint(sidecar) == null, "缺失时应返回 null（按陈旧处理）");
                AssertTrue(PMNetWeavingPolicy.TryWriteToolFingerprint(sidecar, "3:abcdef0123456789"), "写入应成功");
                AssertEqual("3:abcdef0123456789", PMNetWeavingPolicy.TryReadToolFingerprint(sidecar), "读回必须一致");
                AssertTrue(!PMNetWeavingPolicy.TryWriteToolFingerprint(null, "x"), "空路径写入必须失败而不是抛异常");
                AssertTrue(!PMNetWeavingPolicy.TryWriteToolFingerprint(sidecar, null), "空指纹写入必须失败而不是抛异常");
            }
            finally
            {
                TryDelete(root);
            }
        }

        // ============================================================================================
        //  生成物契约探针 / 声明指纹
        // ============================================================================================
        private static void Test_Policy_CodeTokenSkipsComments()
        {
            const string token = "int PMNet_GetRpcWeaveVersion(";
            AssertTrue(!PMNetWeavingPolicy.TextContainsCodeToken("// " + token + "\nint x;\n", token), "行注释不算落地");
            AssertTrue(!PMNetWeavingPolicy.TextContainsCodeToken("/* " + token + " */\nint x;\n", token), "单行块注释不算落地");
            AssertTrue(!PMNetWeavingPolicy.TextContainsCodeToken("/*\n * " + token + "\n */\nint x;\n", token), "块注释体不算落地");
            AssertTrue(!PMNetWeavingPolicy.TextContainsCodeToken(" * " + token + "\n", token), "文档注释行不算落地");
            AssertTrue(PMNetWeavingPolicy.TextContainsCodeToken("        internal static " + token + ")\n", token), "代码行必须命中");
            AssertTrue(PMNetWeavingPolicy.TextContainsCodeToken("/* c */ internal static " + token + ")\n", token),
                "同行块注释之后仍是代码");
            AssertTrue(!PMNetWeavingPolicy.TextContainsCodeToken(null, token), "null 文本不能命中");
            AssertTrue(!PMNetWeavingPolicy.TextContainsCodeToken("int x;", null), "null token 不能命中");
        }

        private static void Test_Policy_GeneratedProbeSignature()
        {
            string root = NewTempDir("probe-signature");
            try
            {
                string a = Path.Combine(root, "PMNet.A.g.cs");
                string b = Path.Combine(root, "PMNet.B.g.cs");
                WriteText(a, "internal static int PMNet_GetRpcWeaveVersion() { return 0; }");
                WriteText(b, "class B { }");

                List<string> one = new List<string>();
                one.Add(a);
                string s1 = PMNetWeavingPolicy.ComputeGeneratedProbeSignature(one);

                List<string> two = new List<string>();
                two.Add(a);
                two.Add(b);
                string s2 = PMNetWeavingPolicy.ComputeGeneratedProbeSignature(two);
                AssertTrue(!string.Equals(s1, s2, StringComparison.Ordinal), "文件集合变化必须改变签名");

                // 同长度改写：长度签名会漏检，内容签名必须抓到。
                WriteText(a, "internal static int PMNet_GetRpcWeaveVersion() { return 1; }");
                string s3 = PMNetWeavingPolicy.ComputeGeneratedProbeSignature(one);
                AssertTrue(!string.Equals(s1, s3, StringComparison.Ordinal), "同长度内容改写必须改变签名");

                string s4 = PMNetWeavingPolicy.ComputeGeneratedProbeSignature(one);
                AssertEqual(s3, s4, "内容不变时签名必须稳定");
            }
            finally
            {
                TryDelete(root);
            }
        }

        private static void Test_Policy_DeclarationCandidateFilter()
        {
            AssertTrue(PMNetWeavingPolicy.IsDeclarationSourceCandidate(@"D:\r\a.cs"), "普通 .cs 是候选");
            AssertTrue(!PMNetWeavingPolicy.IsDeclarationSourceCandidate(@"D:\r\X.g.cs"), ".g.cs 不是候选");
            AssertTrue(!PMNetWeavingPolicy.IsDeclarationSourceCandidate(@"D:\r\obj\a.cs"), "obj 下不是候选");
            AssertTrue(!PMNetWeavingPolicy.IsDeclarationSourceCandidate(@"D:\r\bin\a.cs"), "bin 下不是候选");
            AssertTrue(!PMNetWeavingPolicy.IsDeclarationSourceCandidate(null), "null 不是候选");
        }

        private static void Test_Policy_DeclarationFingerprint()
        {
            string root = NewTempDir("decl-fingerprint");
            try
            {
                string source = Path.Combine(root, "Player.cs");
                WriteText(source, "class Player { }");
                string generatedDir = Path.Combine(root, "Generated");
                WriteText(Path.Combine(generatedDir, "Player.g.cs"), "class PlayerGen { }");

                Func<string> fingerprint = delegate
                {
                    string[] all = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
                    return PMNetWeavingPolicy.ComputeDeclarationFingerprint(root, all);
                };

                string f1 = fingerprint();

                // 生成物变化**不能**改变声明指纹（否则"生成 → 重编译 → 再生成"会变成自激循环）。
                WriteText(Path.Combine(generatedDir, "Player.g.cs"), "class PlayerGen { /* rewritten */ }");
                string f2 = fingerprint();
                AssertEqual(f1, f2, "生成物内容变化不得影响声明指纹");

                WriteText(Path.Combine(generatedDir, "New.g.cs"), "class NewGen { }");
                string f3 = fingerprint();
                AssertEqual(f1, f3, "新增生成物不得影响声明指纹");

                // 声明源变化必须改变指纹。
                WriteText(source, "class Player { int hp; }");
                string f4 = fingerprint();
                AssertTrue(!string.Equals(f1, f4, StringComparison.Ordinal), "声明源内容变化必须改变指纹");

                AssertTrue(PMNetWeavingPolicy.ComputeDeclarationFingerprint(null, new string[] { source }) == null,
                    "root 为空时必须返回 null（调用方按不可解析处理）");
            }
            finally
            {
                TryDelete(root);
            }
        }

        private static void Test_Policy_ClassifyDeclCheck()
        {
            AssertTrue(PMNetWeavingPolicy.ClassifyDeclCheck(0) == PMNetWeavingPolicy.MetadataProbeOutcome.InSync, "0 = 一致");
            AssertTrue(PMNetWeavingPolicy.ClassifyDeclCheck(2) == PMNetWeavingPolicy.MetadataProbeOutcome.NeedsGenerate,
                "2 = 逐字节不一致（唯一的该生成证据）");
            AssertTrue(PMNetWeavingPolicy.ClassifyDeclCheck(1) == PMNetWeavingPolicy.MetadataProbeOutcome.ToolError,
                "1 = 工具/声明错误，不能当成需要生成");
            AssertTrue(PMNetWeavingPolicy.ClassifyDeclCheck(255) == PMNetWeavingPolicy.MetadataProbeOutcome.ToolError,
                "255 = 工具/声明错误");
            AssertTrue(PMNetWeavingPolicy.ClassifyDeclCheck(-1) == PMNetWeavingPolicy.MetadataProbeOutcome.ToolError,
                "-1（未启动/超时）= 工具错误");
        }

        private static void Test_RealGeneratedArtifactHasContract()
        {
            AssertTrue(_repoRoot != null, "必须能解析仓库根（用 --repo 或从可执行文件向上查找）");
            string generated = Path.Combine(_repoRoot, "Client", "Assets", "Scripts", "PMR3", "Generated",
                "PMNet.PMNet.R3.PMR3Player.g.cs");
            AssertTrue(File.Exists(generated), "真实生成物必须存在：" + generated);

            string text = File.ReadAllText(generated);
            AssertTrue(PMNetWeavingPolicy.TextContainsCodeToken(text, PMNetWeavingPolicy.WeaveFormatProbeToken),
                "真实生成物必须含有编织契约符号，否则 fail-closed 门禁会在正常工程里误拦");
            AssertTrue(text.IndexOf("PMNet_RequireRpcWeave", StringComparison.Ordinal) >= 0,
                "真实生成物必须含有运行期 guard（未编织程序集在 new/注册处被拒绝）");
        }

        // ============================================================================================
        //  Unity 侧接线（源码级回归，防已冻结修复被静默回退）
        // ============================================================================================
        private static void Test_EditorSourceFrozenFixes()
        {
            AssertTrue(_editorSourcePath != null && File.Exists(_editorSourcePath),
                "必须能读到 Editor 源码：" + (_editorSourcePath ?? "(未解析)"));
            string source = File.ReadAllText(_editorSourcePath);

            // (1) 没有可以关掉硬门的开关。
            AssertTrue(source.IndexOf("Tools/PMNet/Weaving/Enforce Gate Before Play & Build", StringComparison.Ordinal) < 0,
                "不允许存在可关闭 Play/Build 硬门的菜单项");
            AssertTrue(source.IndexOf("GateEnabledPrefKey", StringComparison.Ordinal) < 0,
                "不允许存在门禁开关偏好键");
            AssertTrue(source.IndexOf("GateEnabled()", StringComparison.Ordinal) < 0,
                "不允许存在 GateEnabled() 判定（门禁必须始终生效）");

            // (2) Player 目标不再从 summary.outputPath 推测旧包目录。
            AssertTrue(source.IndexOf("summary.platform == BuildTarget.StandaloneWindows", StringComparison.Ordinal) < 0,
                "不允许按平台从 summary.outputPath 推导 Player 脚本 DLL（那是猜测旧包目录）");
            AssertTrue(source.IndexOf("CollectPlayerScriptDllCandidates(report)", StringComparison.Ordinal) < 0,
                "Player 候选必须走 policy（只认 BuildReport.files 的绝对精确名）");
            AssertTrue(source.IndexOf("PMNetWeavingPolicy.CollectPlayerScriptDllCandidates", StringComparison.Ordinal) >= 0,
                "Player 候选必须走 policy");

            // (3) 构建 preflight 只核对不生成（否则会拿"刚生成过"冒充"当前程序集新鲜"）。
            AssertTrue(source.IndexOf("EnsureMetadataVerified(false)", StringComparison.Ordinal) >= 0,
                "构建 preflight 必须使用 check-only（不允许在 preflight 里改写生成源码）");
            AssertTrue(source.IndexOf("本次构建会自行编译这些产物", StringComparison.Ordinal) < 0,
                "不允许 preflight 在生成后声称本次构建自动新鲜");

            // (4) 源生成写入后必须标 PendingReload（至少：Repair 一处 + 重新生成一处）。
            int pendingReloadWrites = CountOccurrences(source, "SessionState.SetBool(PendingReloadKey, true);");
            AssertTrue(pendingReloadWrites >= 2,
                "源生成/手工编织后都必须标 PendingReload，实际出现 " + pendingReloadWrites.ToString(CultureInfo.InvariantCulture) + " 次");

            // (5) 契约缺失必须 fail closed（不能再有缺失即跳过的旁路，构建/Player/preflight 都算）。
            AssertTrue(source.IndexOf("尚未落地，跳过", StringComparison.Ordinal) < 0,
                "不允许保留 [契约缺失⇒跳过] 的旁路（preflight / Player / 编译回调都不允许）");
            AssertTrue(source.IndexOf("跳过编织门禁", StringComparison.Ordinal) < 0,
                "构建 preflight 不允许跳过编织门禁");
            AssertTrue(source.IndexOf("契约缺失（fail closed）", StringComparison.Ordinal) >= 0,
                "契约缺失必须按 fail closed 记录");
            AssertTrue(source.IndexOf("\"；Play/Build 门禁=\" + (GateEnabled", StringComparison.Ordinal) < 0,
                "状态输出不得再宣称门禁可关");

            // (6) 契约缺失的所有入口都必须真的失败（不是只写日志）。
            int failBuildCalls = CountOccurrences(source, "FailBuild(");
            AssertTrue(failBuildCalls >= 6,
                "FailBuild 调用点过少（preflight/Player 缺失与失败都必须 FailBuild），实际 "
                + failBuildCalls.ToString(CultureInfo.InvariantCulture) + " 次");
        }

        // ============================================================================================
        //  子进程模式（受控被测对象）
        // ============================================================================================
        private static int ChildMain(string[] args)
        {
            string mode = args[0];

            if (string.Equals(mode, "--child-echo-args", StringComparison.Ordinal))
            {
                using (Stream stdout = Console.OpenStandardOutput())
                {
                    for (int i = 1; i < args.Length; i++)
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(args[i]);
                        stdout.Write(bytes, 0, bytes.Length);
                        stdout.WriteByte((byte)'\n');
                    }

                    stdout.Flush();
                }

                return 0;
            }

            if (string.Equals(mode, "--child-flood-stdout", StringComparison.Ordinal))
            {
                int count = int.Parse(args[1], CultureInfo.InvariantCulture);
                using (Stream stdout = Console.OpenStandardOutput())
                {
                    FloodRaw(stdout, count, (byte)'x');   // 刻意**不写换行**
                }

                return 0;
            }

            if (string.Equals(mode, "--child-flood-both", StringComparison.Ordinal))
            {
                int count = int.Parse(args[1], CultureInfo.InvariantCulture);
                Task a = Task.Run(delegate
                {
                    using (Stream stdout = Console.OpenStandardOutput())
                    {
                        FloodRaw(stdout, count, (byte)'O');
                    }
                });
                Task b = Task.Run(delegate
                {
                    using (Stream stderr = Console.OpenStandardError())
                    {
                        FloodRaw(stderr, count, (byte)'E');
                    }
                });
                Task.WaitAll(a, b);
                return 0;
            }

            if (string.Equals(mode, "--child-exit", StringComparison.Ordinal))
            {
                return int.Parse(args[1], CultureInfo.InvariantCulture);
            }

            if (string.Equals(mode, "--child-sleep", StringComparison.Ordinal))
            {
                Thread.Sleep(int.Parse(args[1], CultureInfo.InvariantCulture));
                return 0;
            }

            if (string.Equals(mode, "--child-leak-grandchild", StringComparison.Ordinal))
            {
                int ms = int.Parse(args[1], CultureInfo.InvariantCulture);

                // 孙进程**继承**本进程的 stdout/stderr 管道（不重定向），随后本进程立刻退出：
                // 这正是"进程 exit 0 但管道不关闭"的受控复现。
                List<string> grandChildArgs = new List<string>();
                grandChildArgs.Add("--child-sleep");
                grandChildArgs.Add(ms.ToString(CultureInfo.InvariantCulture));

                string fileName;
                List<string> arguments;
                BuildChildCommand(grandChildArgs, out fileName, out arguments);

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = fileName;
                psi.Arguments = PMNetToolProcess.BuildArgumentString(arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = false;
                psi.RedirectStandardError = false;
                Process.Start(psi);
                return 0;
            }

            Console.Error.WriteLine("unknown child mode: " + mode);
            return 2;
        }

        private static void FloodRaw(Stream stream, int count, byte value)
        {
            byte[] chunk = new byte[8192];
            for (int i = 0; i < chunk.Length; i++)
            {
                chunk[i] = value;
            }

            int remaining = count;
            while (remaining > 0)
            {
                int take = Math.Min(chunk.Length, remaining);
                stream.Write(chunk, 0, take);
                remaining -= take;
            }

            stream.Flush();
        }

        private static void TryDelete(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
