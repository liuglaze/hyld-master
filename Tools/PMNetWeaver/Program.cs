// PMNetWeaver —— RPC 自然 C# 接口 / IL 编织工具的 CLI 入口（P1）。
//
// 契约：Docs/plans/net-rpc-weaving-contract.md（唯一新冻结接口）
//
// 用法：
//   PMNetWeaver --weave <assembly.dll> [--reference-dir <dir>]... [--require-rpcs]
//   PMNetWeaver --check <assembly.dll> [--reference-dir <dir>]... [--require-rpcs]
//   PMNetWeaver --help
//
// 语义：
//   --weave ：预检 → 克隆业务体 → 原方法改发包入口 → 两个 helper 改指私有业务体 → stamp=1
//             → 暂存 + 原子替换 DLL 与 PDB。**全部预检通过之前零写**。
//   --check ：只验证（stamp 必须为 1，且 wrapper 与两个 helper 真的都指向私有业务体），**不写盘**。
//   --require-rpcs：要求输入里至少有一条 RPC 标记方法（否则失败）。不写该开关时，无 RPC 属正常，退出 0。
//
// 退出码：0 = 成功（或 --check 通过 / 无 RPC 且未要求非空）；1 = 失败（预检/写入/校验失败）；
//         2 = 命令行用法错误。
//
// 设计要点（与契约 §2 对齐）：
//   - 只认**精确全类型名**的三个标记 Attribute，不碰第三方同名 Attribute；
//   - 发现缺生成物 / 错误签名 / 未知版本 / 同名冲突 / 强名称 / 无法处理的 PDB，一律**明确失败**，不静默跳过；
//   - 二次 weave 先做结构校验，幂等（不重复拆、不无条件信 stamp）；
//   - 不处理虚方法 / 网络继承 / 跨 assembly RPC 定义，明确拒绝。

using System;
using System.Collections.Generic;
using System.IO;

namespace PMNetWeaver
{
    /// <summary>命令行入口。</summary>
    public static class Program
    {
        private const int ExitOk = 0;
        private const int ExitFailure = 1;
        private const int ExitUsage = 2;

        /// <summary>进程入口。返回非 0 即失败（门禁据此判红）。</summary>
        public static int Main(string[] args)
        {
            string assemblyPath = null;
            WeaverMode mode = WeaverMode.Weave;
            bool hasMode = false;
            bool requireRpcs = false;
            List<string> referenceDirs = new List<string>();

            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    switch (a)
                    {
                        case "--weave":
                            if (hasMode) { return Usage("--weave 与 --check 只能给一个。"); }
                            hasMode = true;
                            mode = WeaverMode.Weave;
                            assemblyPath = Next(args, ref i, a);
                            break;
                        case "--check":
                            if (hasMode) { return Usage("--weave 与 --check 只能给一个。"); }
                            hasMode = true;
                            mode = WeaverMode.Check;
                            assemblyPath = Next(args, ref i, a);
                            break;
                        case "--reference-dir":
                        case "--reference-dirs":
                            referenceDirs.Add(Next(args, ref i, a));
                            break;
                        case "--require-rpcs":
                            requireRpcs = true;
                            break;
                        case "--help":
                        case "-h":
                        case "-?":
                        case "/?":
                            PrintUsage(Console.Out);
                            return ExitOk;
                        default:
                            if (a != null && a.Length > 0 && a[0] == '-')
                            {
                                return Usage("未知参数：" + a);
                            }

                            // 便利：允许裸路径（等价 --weave <path>）。
                            if (assemblyPath != null)
                            {
                                return Usage("给了多于一个程序集路径。");
                            }

                            assemblyPath = a;
                            break;
                    }
                }

                if (!hasMode && assemblyPath == null)
                {
                    PrintUsage(Console.Error);
                    return ExitUsage;
                }

                if (string.IsNullOrEmpty(assemblyPath))
                {
                    return Usage(mode == WeaverMode.Weave ? "--weave 必须指定程序集路径。" : "--check 必须指定程序集路径。");
                }

                WeaveReport report = RpcAssemblyWeaver.Process(assemblyPath, mode, requireRpcs, referenceDirs);
                PrintReport(report, mode);
                return ExitOk;
            }
            catch (WeaverUsageException ex)
            {
                return Usage(ex.Message);
            }
            catch (WeaverException ex)
            {
                Console.Error.WriteLine("[PMNetWeaver] 失败：" + ex.Message);
                return ExitFailure;
            }
            catch (Exception ex)
            {
                // 未预期异常也要明确失败（不允许"没输出=没事"）。
                Console.Error.WriteLine("[PMNetWeaver] 未预期异常：" + ex.GetType().Name + "：" + ex.Message);
                Console.Error.WriteLine(ex.StackTrace);
                return ExitFailure;
            }
        }

        private static string Next(string[] args, ref int i, string option)
        {
            if (i + 1 >= args.Length)
            {
                throw new WeaverUsageException(option + " 缺少参数值。");
            }

            i++;
            return args[i];
        }

        private static int Usage(string message)
        {
            Console.Error.WriteLine("[PMNetWeaver] 用法错误：" + message);
            PrintUsage(Console.Error);
            return ExitUsage;
        }

        private static void PrintUsage(TextWriter w)
        {
            w.WriteLine("PMNetWeaver —— RPC 自然 C# 接口 / IL 编织工具");
            w.WriteLine();
            w.WriteLine("用法：");
            w.WriteLine("  PMNetWeaver --weave <assembly.dll> [--reference-dir <dir>]... [--require-rpcs]");
            w.WriteLine("  PMNetWeaver --check <assembly.dll> [--reference-dir <dir>]... [--require-rpcs]");
            w.WriteLine("  PMNetWeaver --help");
            w.WriteLine();
            w.WriteLine("  --weave         预检全部通过后编织并原子替换 DLL（+ 有 PDB 则一并替换）。");
            w.WriteLine("  --check         只验证已编织结构（stamp=1 且 wrapper/两个 helper 都指向私有业务体），不写盘。");
            w.WriteLine("  --reference-dir 解析引用程序集时可选的搜索目录（可重复）。");
            w.WriteLine("  --require-rpcs  要求输入至少含一条 PMNet RPC 标记方法，否则失败。");
            w.WriteLine("                  该开关**不把 auto-property 计入**：纯属性程序集仍会被编织（不加此开关时），");
            w.WriteLine("                  但传了它就必须至少 1 条 RPC（--require-rpcs 语义未变）。");
            w.WriteLine();
            w.WriteLine("退出码：0 成功 / 无 RPC（未要求非空）；1 失败；2 用法错误。");
        }

        private static void PrintReport(WeaveReport report, WeaverMode mode)
        {
            Console.WriteLine("[PMNetWeaver] 程序集：" + report.AssemblyPath);
            Console.WriteLine("[PMNetWeaver] 模式：" + (mode == WeaverMode.Weave ? "--weave" : "--check"));
            Console.WriteLine("[PMNetWeaver] RPC 方法数：" + report.RpcMethodCount
                              + "；含 RPC 的类数：" + report.RpcClassCount
                              + "；本次实际编织的类数：" + report.WovenClassCount);
            Console.WriteLine("[PMNetWeaver] auto-property 复制属性数：" + report.PropertyCount
                              + "；本次实际编织的属性数：" + report.WovenPropertyCount
                              + "（[PMReplicated] 字段旧模式不参与，需业务显式 PMNet_Set_<P> 标脏）");
            Console.WriteLine("[PMNetWeaver] 是否实际改写：" + (report.Rewritten ? "是" : "否"));
            Console.WriteLine("[PMNetWeaver] 读入符号：" + (report.SymbolsIn ? "有" : "无")
                              + "；PDB 在磁盘上：" + (report.PdbOnDisk ? "是" : "否")
                              + "；写出符号：" + (report.SymbolsOut ? "有" : "无"));

            for (int i = 0; i < report.Notes.Count; i++)
            {
                Console.WriteLine("[PMNetWeaver] 说明：" + report.Notes[i]);
            }
        }
    }
}
