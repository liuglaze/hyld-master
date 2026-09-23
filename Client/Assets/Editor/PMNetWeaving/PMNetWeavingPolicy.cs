// PMNet 编织便利层 —— 纯 BCL 决策策略（**不含任何 Unity API**）。
//
// 契约：Docs/plans/net-rpc-weaving-contract.md §4（Editor 与构建接口）。
//
// 为什么把判断抽到单独一个文件：
//   便利层里"哪些文件该处理"、"目标缺失该不该失败"、"工具产物还算不算新鲜"这三类判断，
//   本质上是**纯逻辑**，与 Unity 无关。把它们从 Unity 回调里抽出来有三个直接好处：
//     1) 可以在**不启动 Unity** 的纯 C# 进程里跑真实回归（Tools/PMNetWeavingEditorTest），
//        而不是把"必须开编辑器才能验证"当成永远不验证的借口；
//     2) 让 fail-closed 成为**代码里可读、可单测的单一判据**，不被散落在各处的小 if 稀释；
//     3) Unity 侧只剩"取值 → 调工具 → 记状态"，出错面更小。
//
// 本文件刻意只用 BCL（System.IO / System.Text），因此它同时被
//   · Unity 2019.4 的 PMNet.Weaving.Editor asmdef（编辑器里天天跑），和
//   · Tools/PMNetWeavingEditorTest（net8.0，纯 BCL 回归）编译，
// 两份编译面必须都能过 —— 这是"策略与宿主解耦"的可验证形式。
//
// 编码纪律：中文源码 UTF-8 BOM + CRLF（Client/Assets/AGENTS.md §6）。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PMNet.Weaving.Editor
{
    /// <summary>
    /// 便利层的决策策略。所有方法都是**纯函数式**的（除少数显式的读写辅助），
    /// 不读 SessionState、不碰 Editor 状态，因此可以在任意宿主里被直接断言。
    /// </summary>
    internal static class PMNetWeavingPolicy
    {
        // ------------------------------------------------------------------ 目标程序集
        /// <summary>需要处理的程序集文件名。**精确**相等，不做前缀匹配。</summary>
        public const string TargetAssemblyFileName = "Assembly-CSharp.dll";

        /// <summary>
        /// 是否是需要编织的目标程序集文件名。
        ///
        /// 必须是精确名：用 <c>StartsWith("Assembly-CSharp")</c> 会把
        /// <c>Assembly-CSharp-Editor.dll</c> / <c>Assembly-CSharp-firstpass.dll</c> 也卷进来，
        /// 那等于去改第三方/编辑器程序集（契约 §4 只允许处理唯一的 runtime 程序集）。
        /// </summary>
        public static bool IsTargetAssemblyFileName(string pathOrFileName)
        {
            if (string.IsNullOrEmpty(pathOrFileName))
            {
                return false;
            }

            return string.Equals(Path.GetFileName(pathOrFileName), TargetAssemblyFileName, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ 编织格式落地判据
        /// <summary>编织格式 v1 落地判据：生成物里必须出现这个签名。</summary>
        public const string WeaveFormatProbeToken = "int PMNet_GetRpcWeaveVersion(";

        /// <summary>生成物后缀（生成器的输入过滤规则也排除它，声明指纹必须同规则）。</summary>
        public const string GeneratedFileSuffix = ".g.cs";

        /// <summary>
        /// 文本里是否存在**代码行**形式的 token（跳过注释行、块注释体与 XML 文档注释行）。
        ///
        /// 为什么不能简单 <c>Contains</c>：本仓生成物头部有大量中文注释解释"为什么要有
        /// PMNet_GetRpcWeaveVersion"，只做 Contains 会让"注释里提过"被当成"格式已落地"，
        /// 于是 fail-closed 门禁在真正缺符号时静默失效。
        /// </summary>
        public static bool TextContainsCodeToken(string text, string token)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token))
            {
                return false;
            }

            string[] lines = text.Split('\n');
            bool inBlockComment = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                string trimmed = line.TrimStart();

                if (inBlockComment)
                {
                    int close = trimmed.IndexOf("*/", StringComparison.Ordinal);
                    if (close < 0)
                    {
                        continue;
                    }

                    inBlockComment = false;
                    trimmed = trimmed.Substring(close + 2).TrimStart();
                }

                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;   // 单行注释
                }

                if (trimmed.StartsWith("/*", StringComparison.Ordinal))
                {
                    int close = trimmed.IndexOf("*/", StringComparison.Ordinal);
                    if (close < 0)
                    {
                        inBlockComment = true;
                        continue;
                    }

                    trimmed = trimmed.Substring(close + 2).TrimStart();
                    if (trimmed.Length == 0)
                    {
                        continue;
                    }
                }

                if (trimmed.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;   // XML 文档注释 / 块注释延续行
                }

                if (trimmed.IndexOf(token, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 生成物集合的**内容**签名（用于缓存"格式是否已落地"的探测结果）。
        ///
        /// 为什么是内容而不是 (长度 + mtime)：同长度改写（例如把版本方法改成返回别的值、
        /// 或把 helper 改成 public）在长度相同的情况下不会改变长度签名，缓存就会继续返回旧结论。
        /// </summary>
        public static string ComputeGeneratedProbeSignature(IList<string> generatedFilePaths)
        {
            List<string> files = NormalizeAndSort(generatedFilePaths);
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < files.Count; i++)
            {
                hash = FnvAppend(hash, files[i]);
                hash = FnvAppend(hash, HashFileContentOrError(files[i]));
            }

            return files.Count.ToString(CultureInfo.InvariantCulture) + ":" + hash.ToString("x16", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ 声明源指纹
        /// <summary>
        /// 与 <c>Tools/PMNetGen/DeclScanner.IsScanCandidate</c> 保持一致的输入过滤：
        /// 排除生成物（*.g.cs）与 obj/bin 下的文件。
        /// </summary>
        public static bool IsDeclarationSourceCandidate(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                return false;
            }

            if (Path.GetFileName(fullPath).EndsWith(GeneratedFileSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string normalized = fullPath.Replace('\\', '/');
            if (normalized.IndexOf("/obj/", StringComparison.OrdinalIgnoreCase) >= 0
                || normalized.IndexOf("/bin/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// 声明源集合指纹：（相对路径、长度、mtime）的 FNV-1a 64 摘要。
        ///
        /// 关键性质：生成物（*.g.cs）**不在**指纹里 ⇒ 生成器写自己的产物不会改指纹，
        /// "生成 → 重编译 → 再生成"的自激循环在结构上被切断。
        /// </summary>
        public static string ComputeDeclarationFingerprint(string root, IList<string> allCandidateFiles)
        {
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            List<string> files = new List<string>();
            if (allCandidateFiles != null)
            {
                for (int i = 0; i < allCandidateFiles.Count; i++)
                {
                    if (IsDeclarationSourceCandidate(allCandidateFiles[i]))
                    {
                        files.Add(allCandidateFiles[i]);
                    }
                }
            }

            files.Sort(StringComparer.Ordinal);

            ulong hash = 14695981039346656037UL;
            int count = 0;
            for (int i = 0; i < files.Count; i++)
            {
                FileInfo info = new FileInfo(files[i]);
                if (!info.Exists)
                {
                    continue;   // 枚举与读取之间的竞态：按"不存在"处理，下一次指纹会再变
                }

                count++;
                hash = FnvAppend(hash, ToRelativePath(root, files[i]));
                hash = FnvAppend(hash, info.Length.ToString(CultureInfo.InvariantCulture));
                hash = FnvAppend(hash, info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            }

            return count.ToString(CultureInfo.InvariantCulture) + ":" + hash.ToString("x16", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ 元数据核对结果归类
        /// <summary><c>PMNetGen --decl-check</c> 退出码的语义归类。</summary>
        internal enum MetadataProbeOutcome
        {
            /// <summary>exit 0：生成物与声明逐字节一致。</summary>
            InSync = 0,

            /// <summary>exit 2：**唯一**的"该重新生成"证据（契约：不一致即 2）。</summary>
            NeedsGenerate = 1,

            /// <summary>其它退出码：工具/声明/环境错误，**不是**"需要生成"。</summary>
            ToolError = 2
        }

        /// <summary>
        /// 归类 decl-check 退出码。
        ///
        /// 为什么必须区分 <c>NeedsGenerate</c> 与 <c>ToolError</c>：把"工具根本没跑起来"
        /// 当成"需要生成"，就会在工具坏掉时反复 decl-gen（写盘）并掩盖真实故障。
        /// </summary>
        public static MetadataProbeOutcome ClassifyDeclCheck(int exitCode)
        {
            if (exitCode == 0)
            {
                return MetadataProbeOutcome.InSync;
            }

            if (exitCode == 2)
            {
                return MetadataProbeOutcome.NeedsGenerate;
            }

            return MetadataProbeOutcome.ToolError;
        }

        // ------------------------------------------------------------------ Player 目标 DLL
        /// <summary>
        /// 从 <c>BuildReport.files</c> 里挑出 Player 脚本 DLL 候选。
        ///
        /// 判据（只有一个，且刻意很窄）：
        ///   · 文件名**精确**等于 <c>Assembly-CSharp.dll</c>；
        ///   · 路径必须是**绝对路径**（相对路径无法解释其相对于谁，因此不认）。
        ///
        /// 刻意**不再**从 <c>summary.outputPath</c> 推导 <c>&lt;Exe&gt;_Data/Managed/Assembly-CSharp.dll</c>：
        /// 那条推导在"托管脚本已生成、IL2CPP 转换之前"这个窗口里指向的是**最终包目录**，
        /// 而此刻那里可能还是上一轮打包留下的旧 DLL —— 拿它去 weave 等于用"看起来很合理"的猜测
        /// 替换真实目标，并且会在无候选时把失败伪装成成功。任何平台都不猜（契约 §4：不能硬编码猜目录）。
        /// </summary>
        public static bool IsFullyQualifiedPath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) return false;
                // Windows的C:foo和\foo均IsPathRooted，但仍依赖当前目录/盘符。
                string root = Path.GetPathRoot(path).Replace((char)92, '/');
                string fullRoot = Path.GetPathRoot(Path.GetFullPath(path)).Replace((char)92, '/');
                return string.Equals(root, fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        public static string ResolveEditorAssemblyPath(string path, string unityProjectRoot)
        {
            if (!IsTargetAssemblyFileName(path) || !IsFullyQualifiedPath(unityProjectRoot))
                throw new ArgumentException("编辑器程序集路径或Unity项目根目录无效");
            if (Path.IsPathRooted(path))
            {
                if (!IsFullyQualifiedPath(path)) throw new ArgumentException("拒绝依赖当前盘符的程序集路径");
                return Path.GetFullPath(path);
            }
            // 编辑器API给的相对路径以Unity项目为基准，不能以外部工具的仓库cwd为基准。
            return Path.GetFullPath(Path.Combine(unityProjectRoot, path));
        }

        public static List<string> CollectPlayerScriptDllCandidates(IList<string> reportFilePaths)
        {
            List<string> candidates = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (reportFilePaths == null)
            {
                return candidates;
            }

            for (int i = 0; i < reportFilePaths.Count; i++)
            {
                string path = reportFilePaths[i];
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                if (!IsTargetAssemblyFileName(path))
                {
                    continue;
                }

                if (!IsFullyQualifiedPath(path))
                {
                    // 相对路径无从解释：既不猜 CWD，也不当候选（缺失 ⇒ 构建失败）。
                    continue;
                }

                if (seen.Add(path))
                {
                    candidates.Add(path);
                }
            }

            return candidates;
        }

        /// <summary>Player 目标缺失时的失败文案（构建必须失败，不产出未编织的 Player）。</summary>
        public static string DescribeMissingPlayerTarget(IList<string> reportFilePaths)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("BuildReport 未给出可用的 ").Append(TargetAssemblyFileName)
              .Append(" 绝对路径，拒绝产出未编织的 Player（不猜 Temp/最终包目录）。");
            if (reportFilePaths == null || reportFilePaths.Count == 0)
            {
                sb.Append(" BuildReport.files 为空。");
                return sb.ToString();
            }

            sb.Append(" BuildReport.files 实际内容：");
            for (int i = 0; i < reportFilePaths.Count; i++)
            {
                sb.Append("\n  ").Append(reportFilePaths[i]);
            }

            return sb.ToString();
        }

        // ------------------------------------------------------------------ 工具新鲜度（内容指纹）
        /// <summary>工具产物旁边记录"输入内容指纹"的文件（缺失即视为陈旧，强制重建一次）。</summary>
        public static string ToolFingerprintSidecarPath(string toolDll)
        {
            return string.IsNullOrEmpty(toolDll) ? null : toolDll + ".pmnet-inputs";
        }

        /// <summary>
        /// 收集一个工具工程的输入文件集合：工程目录下的 <c>*.cs</c> / <c>*.csproj</c> /
        /// <c>*.props</c> / <c>*.targets</c>，加上显式给出的额外依赖（文件或目录）。
        ///
        /// 为什么必须显式给出额外依赖：`Tools/PMNetGen` 的语义里包含
        /// <c>Tools/PMDeclModel</c>（IR + ID 锁）与被**链接**进来的 <c>Client/Assets/Scripts/PMNet</c> 核心，
        /// 以及仓库级 <c>Directory.Build.targets</c>。只看"工具工程自己的 mtime"会在这些依赖变化时
        /// 继续复用旧工具 DLL —— 表现是"改了生成器却跑旧行为"。
        /// </summary>
        public static List<string> CollectToolInputFiles(string projectPath, IList<string> extraRootsOrFiles, IList<string> excludedDirNames)
        {
            List<string> files = new List<string>();
            if (string.IsNullOrEmpty(projectPath))
            {
                return files;
            }

            string projectDir = Path.GetDirectoryName(projectPath);
            if (!string.IsNullOrEmpty(projectDir) && Directory.Exists(projectDir))
            {
                CollectInputFilesUnder(projectDir, excludedDirNames, files);
            }

            if (extraRootsOrFiles != null)
            {
                for (int i = 0; i < extraRootsOrFiles.Count; i++)
                {
                    string extra = extraRootsOrFiles[i];
                    if (string.IsNullOrEmpty(extra))
                    {
                        continue;
                    }

                    if (File.Exists(extra))
                    {
                        files.Add(extra);
                        continue;
                    }

                    if (Directory.Exists(extra))
                    {
                        CollectInputFilesUnder(extra, excludedDirNames, files);
                    }
                }
            }

            List<string> distinct = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Count; i++)
            {
                if (seen.Add(files[i]))
                {
                    distinct.Add(files[i]);
                }
            }

            distinct.Sort(StringComparer.Ordinal);
            return distinct;
        }

        /// <summary>
        /// 输入集合的**内容**指纹：每个文件的（相对路径、长度、内容 FNV 摘要）依次混入。
        ///
        /// 为什么用内容而不是 mtime：
        ///   · mtime 只比"自己旧产物"新，**删除**某个依赖文件不会让任何 mtime 变新 ⇒ 旧工具继续被用；
        ///   · 时钟回拨 / 从版本控制里恢复旧文件会把 mtime 变**旧** ⇒ 同样漏检；
        ///   · 内容指纹对这三种情况都成立（删文件改变计数与路径集合，改动内容改变摘要）。
        /// 代价是每次判定要读一遍输入文件（几十~几百 KB 源码），相对"跑错工具"的代价可以接受。
        /// </summary>
        public static string ComputeFileSetFingerprint(string baseDir, IList<string> files)
        {
            List<string> normalized = NormalizeAndSort(files);
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < normalized.Count; i++)
            {
                hash = FnvAppend(hash, ToRelativePath(baseDir, normalized[i]));
                hash = FnvAppend(hash, HashFileContentOrError(normalized[i]));
            }

            return normalized.Count.ToString(CultureInfo.InvariantCulture) + ":" + hash.ToString("x16", CultureInfo.InvariantCulture);
        }

        /// <summary>读回记录的指纹；文件不存在或不可读时返回 null（调用方按陈旧处理）。</summary>
        public static string TryReadToolFingerprint(string sidecarPath)
        {
            if (string.IsNullOrEmpty(sidecarPath) || !File.Exists(sidecarPath))
            {
                return null;
            }

            try
            {
                string text = File.ReadAllText(sidecarPath).Trim();
                return text.Length == 0 ? null : text;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>写入指纹记录（失败不抛：调用方按"下次仍陈旧"处理即可，不阻断当前构建）。</summary>
        public static bool TryWriteToolFingerprint(string sidecarPath, string fingerprint)
        {
            if (string.IsNullOrEmpty(sidecarPath) || string.IsNullOrEmpty(fingerprint))
            {
                return false;
            }

            try
            {
                File.WriteAllText(sidecarPath, fingerprint, new UTF8Encoding(false));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ 内部工具
        private static void CollectInputFilesUnder(string dir, IList<string> excludedDirNames, List<string> into)
        {
            string[] found;
            try
            {
                found = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories);
            }
            catch (Exception)
            {
                return;
            }

            for (int i = 0; i < found.Length; i++)
            {
                string normalized = found[i].Replace('\\', '/');
                if (IsExcludedPath(normalized, excludedDirNames))
                {
                    continue;
                }

                if (IsToolInputFile(found[i]))
                {
                    into.Add(found[i]);
                }
            }
        }

        private static bool IsToolInputFile(string path)
        {
            return path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsExcludedPath(string normalizedPath, IList<string> excludedDirNames)
        {
            if (excludedDirNames == null || excludedDirNames.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < excludedDirNames.Count; i++)
            {
                string name = excludedDirNames[i];
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (normalizedPath.IndexOf("/" + name + "/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static List<string> NormalizeAndSort(IList<string> files)
        {
            List<string> result = new List<string>();
            if (files != null)
            {
                for (int i = 0; i < files.Count; i++)
                {
                    if (!string.IsNullOrEmpty(files[i]))
                    {
                        result.Add(files[i]);
                    }
                }
            }

            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private static string ToRelativePath(string baseDir, string fullPath)
        {
            if (string.IsNullOrEmpty(baseDir) || string.IsNullOrEmpty(fullPath))
            {
                return fullPath ?? string.Empty;
            }

            string normalizedBase = baseDir.Replace('\\', '/').TrimEnd('/');
            string normalizedPath = fullPath.Replace('\\', '/');
            if (normalizedPath.Length > normalizedBase.Length
                && normalizedPath.StartsWith(normalizedBase + "/", StringComparison.OrdinalIgnoreCase))
            {
                return normalizedPath.Substring(normalizedBase.Length + 1);
            }

            return normalizedPath;
        }

        /// <summary>文件内容的 FNV-1a 64 摘要；不可读时返回可解释的错误标记（保证指纹会变化）。</summary>
        private static string HashFileContentOrError(string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                ulong hash = 14695981039346656037UL;
                for (int i = 0; i < bytes.Length; i++)
                {
                    hash ^= bytes[i];
                    hash *= 1099511628211UL;
                }

                return bytes.Length.ToString(CultureInfo.InvariantCulture) + ":" + hash.ToString("x16", CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                return "unreadable:" + ex.GetType().Name;
            }
        }

        private static ulong FnvAppend(ulong hash, string value)
        {
            if (value == null)
            {
                return hash;
            }

            for (int i = 0; i < value.Length; i++)
            {
                hash ^= value[i];
                hash *= 1099511628211UL;
            }

            hash ^= (ulong)'\n';
            hash *= 1099511628211UL;
            return hash;
        }
    }
}
