using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PMNet.Codegen;

namespace PMNetGen
{
    /// <summary>
    /// PMNet 序列化代码生成器（命令行）。
    ///
    /// 用法：
    ///   PMNetGen --proto &lt;proto 路径&gt; --out &lt;输出 .cs 路径&gt; [--namespace PMNet.Generated] [--source-path &lt;展示用路径&gt;]
    ///   PMNetGen --proto &lt;proto 路径&gt; --check &lt;已提交的 .cs 路径&gt;
    ///   PMNetGen --normalize-eol &lt;文件路径&gt;
    ///
    ///   PMNetGen --decl-scan &lt;源路径&gt;...
    ///   PMNetGen --decl-gen &lt;源路径&gt;... --out-dir &lt;目录&gt; --id-lock &lt;锁文件&gt;
    ///   PMNetGen --decl-check &lt;源路径&gt;... --id-lock &lt;锁文件&gt; --out-dir &lt;目录&gt;
    ///
    /// --check 模式：生成到内存并与磁盘上的文件比较，不一致则退出码 2。
    /// 供 build.bat 做「生成产物与权威 proto 是否同步」的构建期校验（计划 T4/T5）。
    ///
    /// R2 声明模式（契约 Docs/plans/net-r2-codegen-contract.md）：
    ///   --decl-scan  ：只扫描并打印声明摘要（不写盘）；有声明错误时退出码 1。
    ///   --decl-gen   ：扫描 → 校验 → 写生成物与 ID 锁文件。
    ///   --decl-check ：扫描 → 校验 → 与 --out-dir / --id-lock 的现有内容逐字节比对，只校验不写盘；
    ///                  不一致退出码 2（供 build.bat 做「产物与声明是否同步」的门禁）。
    ///   源路径可以是目录（递归取 *.cs，跳过生成产物与 bin/obj）或单个 .cs 文件。
    ///   --out-dir 就是生成物所在目录（不隐式追加 Generated/ 子目录；约定里的 Generated/ 是目录名习惯）。
    ///
    /// --normalize-eol 模式：把文件的换行统一为 CRLF。
    /// 背景：protoc 写出 LF，但本仓库追踪的文件是 CRLF，导致每次生成后 git 都会把产物报成「已修改」
    /// （内容其实完全一致，只是行尾）。这里复用已有的 .NET 依赖做归一，避免给 build.bat 引入
    /// Python / PowerShell 等新依赖。
    ///
    /// 退出码：0 成功；1 参数/解析/声明错误；2 --check / --decl-check 不一致。
    /// </summary>
    internal static class Program
    {
        private const string DefaultNamespace = "PMNet.Generated";

        private static int Main(string[] args)
        {
            string protoPath = null;
            string outPath = null;
            string checkPath = null;
            string normalizeEolPath = null;
            string ns = DefaultNamespace;
            string sourceDisplay = null;
            string declMode = null;
            string declOutDir = null;
            string idLockPath = null;
            List<string> declPaths = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "--proto":
                        protoPath = Next(args, ref i, a);
                        break;
                    case "--out":
                        outPath = Next(args, ref i, a);
                        break;
                    case "--check":
                        checkPath = Next(args, ref i, a);
                        break;
                    case "--normalize-eol":
                        normalizeEolPath = Next(args, ref i, a);
                        break;
                    case "--namespace":
                        ns = Next(args, ref i, a);
                        break;
                    case "--source-path":
                        sourceDisplay = Next(args, ref i, a);
                        break;
                    case "--decl-scan":
                    case "--decl-gen":
                    case "--decl-check":
                        declMode = a;
                        // 紧跟其后的连续非 `--` 参数都算源路径（目录或 .cs 文件）。
                        while (i + 1 < args.Length
                               && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        {
                            i++;
                            declPaths.Add(args[i]);
                        }

                        break;
                    case "--out-dir":
                        declOutDir = Next(args, ref i, a);
                        break;
                    case "--id-lock":
                        idLockPath = Next(args, ref i, a);
                        break;
                    case "-h":
                    case "--help":
                        PrintUsage();
                        return 0;
                    default:
                        Console.Error.WriteLine("[PMNetGen] 未知参数: " + a);
                        PrintUsage();
                        return 1;
                }
            }

            if (!string.IsNullOrEmpty(declMode))
            {
                return RunDecl(declMode, declPaths, declOutDir, idLockPath);
            }

            if (!string.IsNullOrEmpty(normalizeEolPath))
            {
                return RunNormalizeEol(normalizeEolPath);
            }

            if (string.IsNullOrEmpty(protoPath))
            {
                Console.Error.WriteLine("[PMNetGen] 缺少 --proto");
                PrintUsage();
                return 1;
            }

            if (string.IsNullOrEmpty(outPath) && string.IsNullOrEmpty(checkPath))
            {
                Console.Error.WriteLine("[PMNetGen] 必须指定 --out 或 --check");
                PrintUsage();
                return 1;
            }

            if (!string.IsNullOrEmpty(outPath) && !string.IsNullOrEmpty(checkPath))
            {
                Console.Error.WriteLine("[PMNetGen] --out 与 --check 不能同时使用");
                return 1;
            }

            if (!File.Exists(protoPath))
            {
                Console.Error.WriteLine("[PMNetGen] proto 文件不存在: " + protoPath);
                return 1;
            }

            string protoText;
            try
            {
                protoText = File.ReadAllText(protoPath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[PMNetGen] 读取 proto 失败: " + ex.Message);
                return 1;
            }

            string display = string.IsNullOrEmpty(sourceDisplay) ? protoPath : sourceDisplay;
            string hash = ComputeNormalizedHash(protoText);

            ProtoFile file;
            try
            {
                file = ProtoParser.Parse(protoText, display);
            }
            catch (ProtoParseException ex)
            {
                Console.Error.WriteLine("[PMNetGen] proto 解析失败: " + ex.Message);
                return 1;
            }

            if (file.Syntax != "proto3")
            {
                Console.Error.WriteLine("[PMNetGen] 仅支持 proto3，实际为: " + file.Syntax);
                return 1;
            }

            string generated;
            try
            {
                CSharpEmitter emitter = new CSharpEmitter(file, ns, display, hash);
                generated = emitter.Generate();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[PMNetGen] 生成失败: " + ex.Message);
                return 1;
            }

            // 统一行尾为 CRLF，与本仓库既有生成产物（SocketProto.cs）保持一致。
            generated = ToCrlf(generated);

            if (!string.IsNullOrEmpty(checkPath))
            {
                return RunCheck(checkPath, generated, file);
            }

            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                WriteUtf8NoBom(outPath, generated);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[PMNetGen] 写出失败: " + ex.Message);
                return 1;
            }

            Console.WriteLine("[PMNetGen] 已生成 " + outPath);
            PrintSummary(file, generated);
            return 0;
        }

        private static int RunCheck(string checkPath, string generated, ProtoFile file)
        {
            if (!File.Exists(checkPath))
            {
                Console.Error.WriteLine("[PMNetGen] 校验目标不存在: " + checkPath);
                Console.Error.WriteLine("[PMNetGen] 该产物尚未生成，请先执行 build.bat");
                return 2;
            }

            string existing;
            try
            {
                existing = File.ReadAllText(checkPath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[PMNetGen] 读取校验目标失败: " + ex.Message);
                return 1;
            }

            // 行尾归一后再比较，避免 CRLF/LF 差异造成误报。
            string a = NormalizeNewlines(existing);
            string b = NormalizeNewlines(generated);

            if (string.Equals(a, b, StringComparison.Ordinal))
            {
                Console.WriteLine("[PMNetGen] 同步校验通过: " + checkPath);
                PrintSummary(file, generated);
                return 0;
            }

            Console.Error.WriteLine("[PMNetGen] 生成产物与权威 proto 不同步: " + checkPath);
            Console.Error.WriteLine("[PMNetGen] 原因通常是 proto 已改动但未重新生成运行时产物。");
            Console.Error.WriteLine("[PMNetGen] 处理方式：执行 ProtobufAndNotepad/Protobuf/build.bat 重新生成。");
            Console.Error.WriteLine("[PMNetGen] 差异定位：" + FirstDifference(a, b));
            return 2;
        }

        /// <summary>
        /// 把文件的换行统一为 CRLF。
        /// 按字节处理：UTF-8 下 0x0D/0x0A 不会出现在多字节序列内部，因此字节级改写是安全的。
        /// </summary>
        private static int RunNormalizeEol(string path)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine("[PMNetGen] 行尾归一的文件不存在: " + path);
                return 1;
            }

            byte[] original;
            try
            {
                original = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[PMNetGen] 读取失败: " + ex.Message);
                return 1;
            }

            byte[] normalized = ToCrlfBytes(original);

            if (BytesEqual(original, normalized))
            {
                Console.WriteLine("[PMNetGen] 行尾已是 CRLF，无需改写: " + path);
                return 0;
            }

            try
            {
                File.WriteAllBytes(path, normalized);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[PMNetGen] 写出失败: " + ex.Message);
                return 1;
            }

            Console.WriteLine("[PMNetGen] 行尾已归一为 CRLF（" + original.Length + " -> " + normalized.Length + " 字节）: " + path);
            return 0;
        }

        /// <summary>把任意换行组合归一为 CRLF。</summary>
        private static byte[] ToCrlfBytes(byte[] data)
        {
            const byte Cr = (byte)'\r';
            const byte Lf = (byte)'\n';

            // 第一遍：去掉紧邻 LF 之前的 CR，把 CRLF 与 LF 都变成单个 LF。
            List<byte> stage1 = new List<byte>(data.Length);
            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];
                if (b == Cr && i + 1 < data.Length && data[i + 1] == Lf)
                {
                    continue;
                }

                stage1.Add(b);
            }

            // 第二遍：把每个 LF 展开为 CRLF。
            List<byte> stage2 = new List<byte>(stage1.Count + stage1.Count / 16 + 16);
            for (int i = 0; i < stage1.Count; i++)
            {
                byte b = stage1[i];
                if (b == Lf)
                {
                    stage2.Add(Cr);
                }

                stage2.Add(b);
            }

            return stage2.ToArray();
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static void PrintSummary(ProtoFile file, string generated)
        {
            Console.WriteLine("[PMNetGen] 枚举 " + file.Enums.Count
                + " 个，消息 " + file.Messages.Count
                + " 个，产物 " + generated.Length + " 字符");
        }

        /// <summary>
        /// 计算 proto 内容哈希。先把行尾归一为 LF，使哈希不受 core.autocrlf/checkout 平台影响，
        /// 否则同一份 proto 在不同工作副本上会得到不同哈希，导致生成产物出现无意义差异。
        /// </summary>
        private static string ComputeNormalizedHash(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(NormalizeNewlines(text));
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(bytes);
                StringBuilder sb = new StringBuilder(digest.Length * 2);
                for (int i = 0; i < digest.Length; i++)
                {
                    sb.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return sb.ToString();
            }
        }

        private static string NormalizeNewlines(string text)
        {
            return text.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static string ToCrlf(string text)
        {
            string normalized = NormalizeNewlines(text);
            return normalized.Replace("\n", "\r\n");
        }

        private static void WriteUtf8NoBom(string path, string content)
        {
            UTF8Encoding encoding = new UTF8Encoding(false);
            File.WriteAllText(path, content, encoding);
        }

        private static string FirstDifference(string a, string b)
        {
            int limit = Math.Min(a.Length, b.Length);
            for (int i = 0; i < limit; i++)
            {
                if (a[i] != b[i])
                {
                    int line = 1;
                    for (int j = 0; j < i; j++)
                    {
                        if (a[j] == '\n')
                        {
                            line++;
                        }
                    }

                    return "第 " + line + " 行附近";
                }
            }

            return a.Length == b.Length ? "(内容相同)" : "长度不同（磁盘 " + a.Length + " 字符 / 生成 " + b.Length + " 字符）";
        }

        // =========================================================================================
        //  R2 声明模式（契约 Docs/plans/net-r2-codegen-contract.md）
        // =========================================================================================

        private static int RunDecl(string mode, List<string> paths, string outDir, string idLockPath)
        {
            if (paths.Count == 0)
            {
                Console.Error.WriteLine("[PMNetGen] " + mode + " 缺少源路径（目录或 .cs 文件）");
                return 1;
            }

            if (string.Equals(mode, "--decl-gen", StringComparison.Ordinal)
                && string.IsNullOrEmpty(outDir))
            {
                Console.Error.WriteLine("[PMNetGen] --decl-gen 必须指定 --out-dir");
                return 1;
            }

            if (string.Equals(mode, "--decl-check", StringComparison.Ordinal)
                && (string.IsNullOrEmpty(outDir) || string.IsNullOrEmpty(idLockPath)))
            {
                Console.Error.WriteLine("[PMNetGen] --decl-check 必须指定 --out-dir 与 --id-lock");
                return 1;
            }

            // 1) 读锁文件（权威）。文件不存在 = 首次生成，按全新分配处理。
            PMIdLock idLock = new PMIdLock();
            string diskLockText = null;
            if (!string.IsNullOrEmpty(idLockPath) && File.Exists(idLockPath))
            {
                try
                {
                    diskLockText = File.ReadAllText(idLockPath, Encoding.UTF8);
                    PMIdLock loaded = PMIdLock.Deserialize(diskLockText);
                    if (loaded != null)
                    {
                        idLock = loaded;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[PMNetGen] 锁文件不可用（宁可停下也不用半个锁文件去分配 ID）: " + ex.Message);
                    return 1;
                }
            }

            // 分配前先记下旧值：分配后若某个已存在键的 ID 变了，就是「悄悄改了线协议」。
            string lockBefore = idLock.Serialize();

            // 2) 扫描 + 分配 ID
            PMDeclScanResult scan;
            try
            {
                scan = PMDeclScanner.Scan(paths, idLock, string.Join(", ", paths.ToArray()));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[PMNetGen] 声明扫描失败: " + ex.Message);
                return 1;
            }

            // 3) 校验（契约 §5 的 12 条规则 + §6 类型集）
            PMDeclValidation.Validate(scan.Model, scan.Facts);
            PMDeclScanner.ReportRetiredKeys(idLock, lockBefore, scan.Model);

            PrintDeclSummary(scan);

            if (string.Equals(mode, "--decl-scan", StringComparison.Ordinal))
            {
                if (scan.Model.Ok)
                {
                    Console.WriteLine("[PMNetGen] 声明扫描通过（--decl-scan 不写盘）");
                    return 0;
                }

                PrintErrors(scan.Model);
                return 1;
            }

            if (!scan.Model.Ok)
            {
                PrintErrors(scan.Model);
                Console.Error.WriteLine("[PMNetGen] 声明存在 " + scan.Model.Errors.Count + " 个错误，已拒绝生成（D-R0-50：编译期失败）");
                return 1;
            }

            // 4) 发射
            List<PMGeneratedFile> files;
            try
            {
                files = PMDeclEmitter.EmitAll(scan.Model, scan.Facts);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[PMNetGen] 代码发射失败: " + ex.Message);
                return 1;
            }

            string newLockText = idLock.Serialize();

            if (string.Equals(mode, "--decl-gen", StringComparison.Ordinal))
            {
                return RunDeclGen(files, outDir, idLockPath, newLockText, scan);
            }

            return RunDeclCheck(files, outDir, idLockPath, diskLockText, newLockText, scan);
        }

        private static int RunDeclGen(
            List<PMGeneratedFile> files,
            string outDir,
            string idLockPath,
            string newLockText,
            PMDeclScanResult scan)
        {
            try
            {
                Directory.CreateDirectory(outDir);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[PMNetGen] 无法创建输出目录: " + ex.Message);
                return 1;
            }

            // 清理「上次生成、这次不再产出」的旧产物，避免陈旧的 .g.cs 一直参与编译。
            HashSet<string> expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Count; i++)
            {
                expected.Add(files[i].FileName);
            }

            string[] existing;
            try
            {
                existing = Directory.GetFiles(outDir, "PMNet*.g.cs", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[PMNetGen] 无法枚举输出目录: " + ex.Message);
                return 1;
            }

            for (int i = 0; i < existing.Length; i++)
            {
                string leaf = Path.GetFileName(existing[i]);
                if (!expected.Contains(leaf))
                {
                    try
                    {
                        File.Delete(existing[i]);
                        Console.WriteLine("[PMNetGen] 已删除过期产物 " + leaf);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("[PMNetGen] 删除过期产物失败: " + ex.Message);
                        return 1;
                    }
                }
            }

            try
            {
                for (int i = 0; i < files.Count; i++)
                {
                    WriteUtf8BomCrlf(Path.Combine(outDir, files[i].FileName), files[i].Content);
                }

                if (!string.IsNullOrEmpty(idLockPath))
                {
                    string lockDir = Path.GetDirectoryName(Path.GetFullPath(idLockPath));
                    if (!string.IsNullOrEmpty(lockDir) && !Directory.Exists(lockDir))
                    {
                        Directory.CreateDirectory(lockDir);
                    }

                    // 锁文件是 JSON 数据，不带 BOM、LF 行尾（与仓库既有 JSON 一致）。
                    File.WriteAllText(idLockPath, newLockText, new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[PMNetGen] 写出失败: " + ex.Message);
                return 1;
            }

            Console.WriteLine("[PMNetGen] 已生成 " + files.Count + " 个文件到 " + outDir);
            if (!string.IsNullOrEmpty(idLockPath))
            {
                Console.WriteLine("[PMNetGen] 已写出 ID 锁文件 " + idLockPath);
            }

            Console.WriteLine("[PMNetGen] 生成期整体协议摘要 GlobalProtocolHash = 0x"
                + scan.Model.GlobalProtocolHash.ToString("X8", CultureInfo.InvariantCulture));
            return 0;
        }

        private static int RunDeclCheck(
            List<PMGeneratedFile> files,
            string outDir,
            string idLockPath,
            string diskLockText,
            string newLockText,
            PMDeclScanResult scan)
        {
            int mismatches = 0;

            if (!Directory.Exists(outDir))
            {
                Console.Error.WriteLine("[PMNetGen] --decl-check：输出目录不存在（产物尚未生成？）: " + outDir);
                return 2;
            }

            for (int i = 0; i < files.Count; i++)
            {
                string path = Path.Combine(outDir, files[i].FileName);
                if (!File.Exists(path))
                {
                    Console.Error.WriteLine("[PMNetGen] 缺少生成产物: " + files[i].FileName);
                    mismatches++;
                    continue;
                }

                string onDisk;
                try
                {
                    onDisk = File.ReadAllText(path, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[PMNetGen] 读取产物失败 " + files[i].FileName + ": " + ex.Message);
                    return 1;
                }

                // BOM 与行尾归一后再比内容，避免只差一个 BOM/CRLF 就误报。
                if (!string.Equals(Strip(onDisk), Strip(files[i].Content), StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("[PMNetGen] 产物与声明不同步: " + files[i].FileName
                        + "（" + FirstDifference(Strip(onDisk), Strip(files[i].Content)) + "）");
                    mismatches++;
                }
            }

            // 也要检查「磁盘上多出来的 PMNet*.g.cs」——那是已删除声明的残留。
            HashSet<string> expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Count; i++)
            {
                expected.Add(files[i].FileName);
            }

            string[] existing = Directory.GetFiles(outDir, "PMNet*.g.cs", SearchOption.TopDirectoryOnly);
            for (int i = 0; i < existing.Length; i++)
            {
                string leaf = Path.GetFileName(existing[i]);
                if (!expected.Contains(leaf))
                {
                    Console.Error.WriteLine("[PMNetGen] 残留的过期产物（声明已不存在）: " + leaf);
                    mismatches++;
                }
            }

            if (!string.IsNullOrEmpty(idLockPath))
            {
                if (diskLockText == null)
                {
                    Console.Error.WriteLine("[PMNetGen] --decl-check：锁文件不存在: " + idLockPath);
                    mismatches++;
                }
                else if (!string.Equals(NormalizeNewlines(diskLockText), NormalizeNewlines(newLockText), StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("[PMNetGen] ID 锁文件与声明不同步: " + idLockPath);
                    mismatches++;
                }
            }

            if (mismatches == 0)
            {
                Console.WriteLine("[PMNetGen] 同步校验通过: " + files.Count + " 个产物 + 锁文件");
                return 0;
            }

            Console.Error.WriteLine("[PMNetGen] --decl-check 失败：" + mismatches + " 处不一致");
            Console.Error.WriteLine("[PMNetGen] 处理方式：执行 --decl-gen 重新生成并提交产物与锁文件");
            return 2;
        }

        private static void PrintDeclSummary(PMDeclScanResult scan)
        {
            PMDeclModel model = scan.Model;
            int props = 0;
            int rpcs = 0;
            for (int i = 0; i < model.Classes.Count; i++)
            {
                props += model.Classes[i].Properties.Count;
                rpcs += model.Classes[i].Rpcs.Count;
            }

            Console.WriteLine("[PMNetGen] 声明扫描：文件 " + scan.Facts.ParsedFileCount
                + " 个（语法错误 " + scan.Facts.SyntaxErrorCount + " 处），网络类 " + model.Classes.Count
                + " 个，复制属性 " + props + " 个，RPC " + rpcs + " 条");

            for (int i = 0; i < model.Classes.Count; i++)
            {
                PMDeclClass cls = model.Classes[i];
                Console.WriteLine("    ClassId=" + cls.ClassId + "  " + cls.QualifiedName
                    + "  props=" + cls.Properties.Count + "  rpcs=" + cls.Rpcs.Count
                    + "  maskBits=" + cls.ChangeMaskBitCount
                    + "  hash=0x" + cls.ClassProtocolHash.ToString("X8", CultureInfo.InvariantCulture)
                    + (cls.IsPartial ? "" : "  [非 partial]"));
            }

            for (int i = 0; i < model.Warnings.Count; i++)
            {
                Console.WriteLine("    " + model.Warnings[i]);
            }
        }

        private static void PrintErrors(PMDeclModel model)
        {
            for (int i = 0; i < model.Errors.Count; i++)
            {
                Console.Error.WriteLine("[PMNetGen] " + model.Errors[i]);
            }
        }

        /// <summary>写出 UTF-8 + BOM + CRLF 的文本（含中文的源文件约定）。</summary>
        private static void WriteUtf8BomCrlf(string path, string content)
        {
            byte[] body = Encoding.UTF8.GetBytes(ToCrlf(content));
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                fs.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                fs.Write(body, 0, body.Length);
            }
        }

        /// <summary>去掉 BOM 并把行尾归一为 LF（用于内容比较）。</summary>
        private static string Strip(string text)
        {
            if (text == null)
            {
                return string.Empty;
            }

            if (text.Length > 0 && text[0] == '\uFEFF')
            {
                text = text.Substring(1);
            }

            return NormalizeNewlines(text);
        }

        private static string Next(string[] args, ref int i, string option)
        {
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException("参数 " + option + " 缺少取值");
            }

            i++;
            return args[i];
        }

        private static void PrintUsage()
        {
            Console.WriteLine("PMNetGen - PMNet 序列化代码生成器 + R2 声明扫描/发射/门禁");
            Console.WriteLine();
            Console.WriteLine("用法:");
            Console.WriteLine("  PMNetGen --proto <proto> --out <out.cs> [--namespace <ns>] [--source-path <display>]");
            Console.WriteLine("  PMNetGen --proto <proto> --check <existing.cs>");
            Console.WriteLine("  PMNetGen --normalize-eol <file>");
            Console.WriteLine();
            Console.WriteLine("  PMNetGen --decl-scan <源路径>...");
            Console.WriteLine("  PMNetGen --decl-gen <源路径>... --out-dir <目录> --id-lock <锁文件>");
            Console.WriteLine("  PMNetGen --decl-check <源路径>... --id-lock <锁文件> --out-dir <目录>");
            Console.WriteLine();
            Console.WriteLine("  <源路径> 可以是目录（递归 *.cs）或单个 .cs 文件。");
            Console.WriteLine("  --out-dir 就是生成物目录（不隐式追加 Generated/ 子目录）。");
            Console.WriteLine();
            Console.WriteLine("退出码: 0 成功 / 1 参数或声明错误 / 2 --check 或 --decl-check 不一致");
        }
    }
}
