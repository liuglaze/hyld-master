// PMNetWeaver 核心：RPC 程序集预检 + IL 编织 + 结构校验 + 原子落盘。
//
// 契约：Docs/plans/net-rpc-weaving-contract.md（唯一新冻结接口）
//
// 冻结格式 v1（契约 §2）：
//   - 业务方法 M（带 PMNet.PMServerRpcAttribute / PMClientRpcAttribute / PMNetMulticastAttribute）
//     直接写业务体，普通名调用；
//   - 生成物提供 `private void PMNet_<M>(<与 M 相同的参数>)`（发送 helper，业务不可直接调用）
//     与 `private static void PMNet_RpcInvoke_<M>(PMNet.PMNetObject, PMNet.PMNetReader)`（收包 helper）；
//     编织前这两个 helper 各**恰一次**调用原始方法 M；
//   - 每个含 RPC 的生成 partial 类提供 `internal static int PMNet_GetRpcWeaveVersion()`
//     （编译前 0、成功编织后 1）与 `private static int PMNet_RequireRpcWeave()`（版本 != 1 抛异常），
//     且 `PMNet_BuildEntry()` 开头调用 Require（正常 new / 注册都拒绝未编织程序集）。
//
// 编织后的不变式（--check 必须逐条验证，不能只看 stamp）：
//   1. M 的 IL == `ldarg.0 + 各参数 → call PMNet_<M> → ret`（参数名与 Attribute 仍在 M 上）；
//   2. 私有业务体 `PMNet_RpcBody_<M>` 存在，参数与 M 一致，且**不带 RPC Attribute**；
//   3. 发送 helper 的唯一业务调用指向业务体（不是 M），收包 helper 的唯一业务调用也指向业务体
//      ⇒ 本地执行一次、收包不递归；
//   4. 稳定 ID / 描述符 / 协议摘要**不被本工具触碰**（只改方法体与版本方法）。
//
// 明确拒绝（不静默跳过）：缺生成物 / wrapper / guard、未知版本、同名重载、static/virtual/abstract/
// extern/async/generic/无体/非 void、ref-out-in-params-default 参数、强名称程序集、无法处理的 PDB 格式。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// System.Reflection.Metadata（net8.0 内置，不引入新依赖）用于**独立**校验写出的 PDB。
// 为什么不用 Cecil 自己读回来校验：Cecil 的读写器对同一处缺陷可能“错得一致”，
// 独立读取器才能把“写坏了”变成可观察的失败。
using SrmMetadataReader = System.Reflection.Metadata.MetadataReader;
using SrmMetadataReaderProvider = System.Reflection.Metadata.MetadataReaderProvider;
using SrmMetadataTokens = System.Reflection.Metadata.Ecma335.MetadataTokens;
using SrmMethodDebugInformationHandle = System.Reflection.Metadata.MethodDebugInformationHandle;
using SrmMethodDefinitionHandle = System.Reflection.Metadata.MethodDefinitionHandle;
using SrmPEReader = System.Reflection.PortableExecutable.PEReader;

namespace PMNetWeaver
{
    /// <summary>编织器可预期的失败（消息面向修复者，含修复方向）。</summary>
    public sealed class WeaverException : Exception
    {
        public WeaverException(string message)
            : base(message)
        {
        }
    }

    /// <summary>命令行用法错误（缺参数值 / 未知开关互斥等）：CLI 据此返回退出码 2。</summary>
    public sealed class WeaverUsageException : Exception
    {
        public WeaverUsageException(string message)
            : base(message)
        {
        }
    }

    /// <summary>运行模式。</summary>
    public enum WeaverMode
    {
        /// <summary>预检 + 编织 + 原子落盘。</summary>
        Weave,

        /// <summary>只验证已编织结构，绝不写盘。</summary>
        Check,
    }

    /// <summary>一次处理的结构化结果（供 CLI 打印与门禁断言）。</summary>
    public sealed class WeaveReport
    {
        public string AssemblyPath = string.Empty;
        public int RpcMethodCount;
        public int RpcClassCount;
        public int WovenClassCount;

        /// <summary>[PMReplicated] auto-property 总数（字段旧模式不计入）。</summary>
        public int PropertyCount;

        /// <summary>本次实际编织的 auto-property 数。</summary>
        public int WovenPropertyCount;

        public bool Rewritten;
        public bool AlreadyWoven;
        public bool SymbolsIn;
        public bool SymbolsOut;
        public bool PdbOnDisk;
        public readonly List<string> Notes = new List<string>();

        public void Note(string text)
        {
            Notes.Add(text);
        }
    }

    /// <summary>程序集级的 RPC 编织器。</summary>
    public static class RpcAssemblyWeaver
    {
        /// <summary>生成物提供的编织版本方法名（契约 §2，冻结）。</summary>
        public const string VersionMethodName = "PMNet_GetRpcWeaveVersion";

        /// <summary>生成物提供的注册/构造 guard 方法名（契约 §2，冻结）。</summary>
        public const string RequireMethodName = "PMNet_RequireRpcWeave";

        /// <summary>私有业务体方法名前缀（契约 §2，冻结）。</summary>
        public const string BodyPrefix = "PMNet_RpcBody_";

        /// <summary>发送 helper 前缀（契约 §2，冻结）。</summary>
        public const string SendPrefix = "PMNet_";

        /// <summary>收包 helper 前缀（契约 §2，冻结）。</summary>
        public const string InvokePrefix = "PMNet_RpcInvoke_";

        /// <summary>类注册入口方法名（契约 §2，冻结）。</summary>
        public const string BuildEntryName = "PMNet_BuildEntry";

        /// <summary>已成功编织的版本号（契约 §2，冻结）。</summary>
        public const int WovenVersion = 1;

        /// <summary>生成物提供的实例 guard 字段名（契约 §2 的「实例 readonly gate 字段」）。</summary>
        public const string GateFieldName = "PMNet_rpcWeaveGate";

        /// <summary>guard 必须抛出的异常类型（契约 §2 冻结；不能用别的异常代替）。</summary>
        private const string InvalidOperationExceptionFullName = "System.InvalidOperationException";

        private const string ObjectTypeFullName = "PMNet.PMNetObject";
        private const string ReaderTypeFullName = "PMNet.PMNetReader";

        /// <summary>只认这三个**精确全类型名**，不碰第三方同名 Attribute。</summary>
        private static readonly string[] MarkerAttributeFullNames = new string[]
        {
            "PMNet.PMServerRpcAttribute",
            "PMNet.PMClientRpcAttribute",
            "PMNet.PMNetMulticastAttribute",
        };

        /// <summary>
        /// 处理一个程序集。
        /// </summary>
        /// <param name="path">目标 DLL 路径。</param>
        /// <param name="mode">weave / check。</param>
        /// <param name="requireRpcs">是否要求至少一条 RPC。</param>
        /// <param name="referenceDirs">可选的引用搜索目录。</param>
        public static WeaveReport Process(
            string path,
            WeaverMode mode,
            bool requireRpcs,
            IReadOnlyList<string> referenceDirs)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new WeaverException("必须指定程序集路径。");
            }

            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                throw new WeaverException("找不到程序集文件：" + fullPath);
            }

            // 并发保护（只对会写盘的 weave 有意义；--check 只读，不参与）：
            // 两个 weave 同时处理同一程序集时，会同时做「备份 → 改名替换」——
            // 旧实现的备份名写死 `<目标>.pmweave.bak`，两个进程会互相覆盖/删除对方的备份，
            // 甚至把对方回滚所需的原始内容删掉（错乱的结果是「DLL 是 A 的、PDB 是 B 的」）。
            // 这里用独占锁文件把并发 weave 变成「后到者明确失败」，而不是静默交错；
            // 进程崩溃时 OS 会释放句柄，残留的锁文件不会阻塞下一次运行。
            string lockPath = fullPath + ".pmweave.lock";
            FileStream lockStream = null;
            try
            {
                if (mode == WeaverMode.Weave)
                {
                    lockStream = AcquireWeaveLock(lockPath);
                }

                return ProcessCore(fullPath, mode, requireRpcs, referenceDirs);
            }
            finally
            {
                if (lockStream != null)
                {
                    try
                    {
                        lockStream.Dispose();
                    }
                    catch
                    {
                        // 释放失败只影响锁的及时性，不影响本次结论。
                    }

                    // 别人正持有锁时 File.Delete 会失败（共享冲突）⇒ TryDelete 静默跳过，不会删掉别人的锁。
                    TryDelete(lockPath);
                }
            }
        }

        /// <summary>
        /// 为「同一程序集的并发 weave」取一把进程间独占锁（锁文件写在目标旁边）。
        /// 拿不到锁就明确失败：并发交错替换 DLL/PDB 的后果（两文件内容来自不同次编织）
        /// 比“后到者失败重试”严重得多。
        /// </summary>
        private static FileStream AcquireWeaveLock(string lockPath)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                throw new WeaverException(
                    "另一个 PMNetWeaver 正在处理同一个程序集（锁文件 " + lockPath
                    + " 被占用）：并发 weave 会交错替换 DLL/PDB，因此明确失败而不是交错写入。"
                    + "等它结束后重试即可（若确认没有其它 weave 在跑，删除该锁文件）。原始错误：" + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new WeaverException("无法创建 weave 锁文件 " + lockPath + "：" + ex.Message);
            }
        }

        private static WeaveReport ProcessCore(
            string fullPath,
            WeaverMode mode,
            bool requireRpcs,
            IReadOnlyList<string> referenceDirs)
        {
            string pdbPath = Path.ChangeExtension(fullPath, ".pdb");
            bool pdbOnDisk = File.Exists(pdbPath);

            DefaultAssemblyResolver resolver = new DefaultAssemblyResolver();
            if (referenceDirs != null)
            {
                for (int i = 0; i < referenceDirs.Count; i++)
                {
                    string dir = referenceDirs[i];
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        resolver.AddSearchDirectory(dir);
                    }
                }
            }

            string assemblyDir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(assemblyDir))
            {
                resolver.AddSearchDirectory(assemblyDir);
            }

            ReaderParameters readerParameters = new ReaderParameters();
            readerParameters.AssemblyResolver = resolver;
            // InMemory=true：Cecil 绝不回写输入文件，落盘完全由本类控制（原子替换的前提）。
            readerParameters.InMemory = true;
            // 有符号就读；没有符号由 provider 容忍（throwIfNoSymbols=false）；符号存在但损坏会抛 ⇒ 明确失败。
            readerParameters.ReadSymbols = true;
            readerParameters.SymbolReaderProvider = new DefaultSymbolReaderProvider(false);

            AssemblyDefinition assembly;
            try
            {
                assembly = AssemblyDefinition.ReadAssembly(fullPath, readerParameters);
            }
            catch (BadImageFormatException ex)
            {
                throw new WeaverException("输入不是可处理的托管 PE 程序集：" + ex.Message);
            }
            catch (Exception ex)
            {
                throw new WeaverException("读取程序集/符号失败（不静默忽略调试信息）：" + ex.Message);
            }

            try
            {
                return ProcessLoaded(assembly, fullPath, pdbOnDisk, mode, requireRpcs);
            }
            finally
            {
                assembly.Dispose();
            }
        }

        private static WeaveReport ProcessLoaded(
            AssemblyDefinition assembly,
            string fullPath,
            bool pdbOnDisk,
            WeaverMode mode,
            bool requireRpcs)
        {
            WeaveReport report = new WeaveReport();
            report.AssemblyPath = fullPath;
            report.PdbOnDisk = pdbOnDisk;
            report.SymbolsIn = assembly.MainModule.HasSymbols;

            RejectUnsupportedAssembly(assembly, pdbOnDisk, report);

            List<ClassBucket> buckets = CollectBuckets(assembly, report);

            int rpcCount = 0;
            int propertyCount = 0;
            for (int i = 0; i < buckets.Count; i++)
            {
                rpcCount += buckets[i].Methods.Count;
                propertyCount += buckets[i].Properties.Count;
            }

            report.RpcClassCount = buckets.Count;
            report.RpcMethodCount = rpcCount;
            report.PropertyCount = propertyCount;

            // ★ --require-rpcs 的语义**不变**：它仍然表示「至少 1 条 RPC」，不能改成「至少 1 个属性」。
            //   纯 auto-property 程序集在本开关下依旧失败（构建接线只在生成源码里真的出现
            //   PMGeneratedRpcId_ 时才传它，所以正常构建不会把纯属性程序集判红）。
            //   顺序很关键：必须在「无 RPC 且无属性 ⇒ noop」早退**之前**判定。
            if (rpcCount == 0 && requireRpcs)
            {
                throw new WeaverException(
                    "--require-rpcs：输入程序集没有任何 PMNet RPC 标记方法（" + fullPath + "；"
                    + "auto-property " + propertyCount + " 个）。该开关要求至少 1 条 RPC，属性不计入。");
            }

            if (rpcCount == 0 && propertyCount == 0)
            {
                report.Note("程序集内没有 PMNet RPC 标记方法，也没有 [PMReplicated] auto-property："
                            + "无需编织（退出 0，不写盘）。");
                return report;
            }

            // ── 预检阶段：全部通过之前**零写** ───────────────────────────────
            int unwovenClasses = 0;
            string unwovenNames = string.Empty;
            for (int i = 0; i < buckets.Count; i++)
            {
                ClassBucket bucket = buckets[i];
                ValidateClassGuard(bucket);

                int version = ReadWeaveVersion(bucket.Type);
                bucket.Version = version;

                for (int k = 0; k < bucket.Methods.Count; k++)
                {
                    ValidateRpcSignature(bucket);
                }

                if (version == 0)
                {
                    ValidateUnwovenClass(bucket);
                    bucket.PropertyPlans = PropertyWeaver.Analyze(bucket.Type, false);
                    unwovenClasses++;
                    if (unwovenNames.Length < 512)
                    {
                        unwovenNames += (unwovenNames.Length == 0 ? string.Empty : ", ") + bucket.Type.FullName;
                    }
                }
                else if (version == WovenVersion)
                {
                    ValidateWovenClass(bucket);
                    bucket.PropertyPlans = PropertyWeaver.Analyze(bucket.Type, true);
                }
                else
                {
                    throw new WeaverException(
                        "类型 " + bucket.Type.FullName + " 的 " + VersionMethodName + " 返回未知版本 " + version
                        + "：只支持 0（未编织）/ 1（已编织）。未知版本必须明确失败，不能用 define 跳过。");
                }
            }

            if (mode == WeaverMode.Check)
            {
                if (unwovenClasses > 0)
                {
                    throw new WeaverException(
                        "--check：有 " + unwovenClasses + " 个含 RPC / 复制属性的类尚未编织（" + unwovenNames
                        + "）。--check 只验证、不写盘；请先运行 --weave。");
                }

                // ★ P3 加固：旧实现的 --check 完全不看符号，于是「已编织但 PDB 是旧的/损坏的」
                //   也能报绿（wrapper 的失效行号、克隆业务体在 PDB 里没有对应行都发现不了）。
                //   这里在只读模式下把 DLL 与 PDB 的对应关系真的核一遍。
                VerifyWovenSymbols(fullPath, pdbOnDisk, buckets, report);

                report.AlreadyWoven = true;
                report.Note("--check 通过：全部 RPC 类的 stamp=1、wrapper 与两个 helper 都指向私有业务体；"
                            + "全部 auto-property 的 setter 都转发到赋值 helper、RawSet 都精确 stfld 自己的 backing field、"
                            + "helper 语义与槽位正确、收包 Reader 只走 RawSet；且（有 PDB 时）符号与 DLL 逐方法对应（未写盘）。");
                return report;
            }

            if (unwovenClasses == 0)
            {
                report.AlreadyWoven = true;
                report.Note("全部含 RPC / auto-property 的类已是编织后结构（stamp=1 且结构一致）：幂等，不重复拆、不写盘。");
                return report;
            }

            // ── 编织阶段（只改内存模型，不碰磁盘）────────────────────────────
            List<PdbProbe> expectations = new List<PdbProbe>();
            for (int i = 0; i < buckets.Count; i++)
            {
                ClassBucket bucket = buckets[i];
                if (bucket.Version != 0)
                {
                    continue;
                }

                for (int k = 0; k < bucket.Methods.Count; k++)
                {
                    WeaveMethod(bucket, bucket.Methods[k], expectations);
                }

                // ★ auto-property 与 RPC 在同一次预检之后统一编织（同一个 stamp、同一份 PDB 探针、
                //   同一次原子落盘）——不新增独立写盘器，也不允许「属性织了、RPC 没织」。
                if (bucket.PropertyPlans != null && bucket.PropertyPlans.Length > 0)
                {
                    PropertyWeaver.Weave(bucket.PropertyPlans, expectations);
                    report.WovenPropertyCount += bucket.PropertyPlans.Length;
                }

                SetWeaveVersion(bucket.Type, WovenVersion);

                // 编织后自检：写盘前必须已经是合法的编织结构。
                ValidateWovenClass(bucket);
                if (bucket.PropertyPlans != null && bucket.PropertyPlans.Length > 0)
                {
                    PropertyWeaver.Analyze(bucket.Type, true);
                }

                report.WovenClassCount++;
            }

            report.Rewritten = true;

            // ── 暂存 + 独立校验 PDB + 原子替换 ─────────────────────────────
            WriteAtomically(assembly, fullPath, pdbOnDisk, report, expectations);
            return report;
        }

        // =================================================================================
        //  预检
        // =================================================================================

        private static void RejectUnsupportedAssembly(AssemblyDefinition assembly, bool pdbOnDisk, WeaveReport report)
        {
            if (assembly.Name != null && assembly.Name.HasPublicKey)
            {
                throw new WeaverException(
                    "输入程序集带公钥（强名称）：编织会破坏签名，契约明确不支持。"
                    + "请在编织后重新签名，或对本工具输入未签名程序集。");
            }

            if ((assembly.MainModule.Attributes & ModuleAttributes.StrongNameSigned) != 0)
            {
                throw new WeaverException("输入程序集标记为强名称签名（StrongNameSigned）：契约明确拒绝。");
            }

            if (pdbOnDisk && !assembly.MainModule.HasSymbols)
            {
                throw new WeaverException(
                    "PDB 存在但未能读入符号（" + Path.ChangeExtension(assembly.MainModule.FileName, ".pdb")
                    + "）：不得静默丢弃调试信息。请确认 PDB 与 DLL 匹配且为 portable 格式。");
            }

            if (assembly.MainModule.HasSymbols)
            {
                string readerName = assembly.MainModule.SymbolReader == null
                    ? null
                    : assembly.MainModule.SymbolReader.GetType().Name;
                bool portable = readerName != null
                                && readerName.IndexOf("Portable", StringComparison.Ordinal) >= 0;
                if (!portable)
                {
                    throw new WeaverException(
                        "不支持的符号格式（" + (readerName ?? "<无>") + "）：只支持 portable PDB（外部或 embedded）。"
                        + "非 portable（例如 Windows native PDB）必须明确失败，不能静默丢调试信息或偷偷换格式。");
                }

                report.Note("符号格式：" + readerName + "。");
            }
        }

        private static List<ClassBucket> CollectBuckets(AssemblyDefinition assembly, WeaveReport report)
        {
            SortedDictionary<string, ClassBucket> byName =
                new SortedDictionary<string, ClassBucket>(StringComparer.Ordinal);

            for (int m = 0; m < assembly.Modules.Count; m++)
            {
                ModuleDefinition module = assembly.Modules[m];
                List<TypeDefinition> types = new List<TypeDefinition>();
                CollectTypes(module.Types, types);

                for (int t = 0; t < types.Count; t++)
                {
                    TypeDefinition type = types[t];
                    List<MethodDefinition> hits = null;

                    for (int i = 0; i < type.Methods.Count; i++)
                    {
                        MethodDefinition method = type.Methods[i];
                        List<string> markers = FindRpcMarkers(method);
                        if (markers.Count == 0)
                        {
                            continue;
                        }

                        if (markers.Count > 1)
                        {
                            throw new WeaverException(
                                "方法 " + Loc(method) + " 同时带多个 RPC 标记（"
                                + string.Join(" / ", markers.ToArray()) + "）：无法确定方向，明确失败。");
                        }

                        if (hits == null)
                        {
                            hits = new List<MethodDefinition>();
                        }

                        hits.Add(method);
                    }

                    // ★ auto-property：与 RPC 共用同一套 bucket / 预检 / stamp / 落盘。
                    //   契约要求「存在 RPC **或** auto-property 的类均发射 version/Require/实例 gate/BuildEntry 门」，
                    //   因此纯属性零 RPC 的类也是参与编织的对象（不能被当成 noop 跳过）。
                    List<PropertyDefinition> properties = PropertyWeaver.Collect(type);

                    if (hits == null && properties.Count == 0)
                    {
                        continue;
                    }

                    if (hits != null)
                    {
                        hits.Sort(delegate (MethodDefinition a, MethodDefinition b)
                        {
                            return string.CompareOrdinal(a.Name, b.Name);
                        });
                    }

                    ClassBucket bucket = new ClassBucket();
                    bucket.Type = type;
                    bucket.Methods = hits ?? new List<MethodDefinition>();
                    bucket.Properties = properties;
                    byName[type.FullName] = bucket;
                }
            }

            List<ClassBucket> result = new List<ClassBucket>(byName.Values);
            if (result.Count > 0)
            {
                report.Note("参与编织的类型（按全名序）：" + string.Join(", ", result.Select(b => b.Type.FullName).ToArray()) + "。");
            }

            return result;
        }

        private static void CollectTypes(IEnumerable<TypeDefinition> source, List<TypeDefinition> sink)
        {
            foreach (TypeDefinition type in source)
            {
                sink.Add(type);
                if (type.HasNestedTypes)
                {
                    CollectTypes(type.NestedTypes, sink);
                }
            }
        }

        private static void ValidateClassGuard(ClassBucket bucket)
        {
            TypeDefinition type = bucket.Type;

            MethodDefinition version = FindMethod(type, VersionMethodName);
            if (version == null)
            {
                throw new WeaverException(
                    "类型 " + type.FullName + " 声明了 PMNet RPC，但缺少生成物方法 " + VersionMethodName + "()："
                    + "说明生成物与该程序集不同步（有 RPC 无生成/无 guard ⇒ 明确失败，不静默跳过）。"
                    + "修复：先运行 PMNetGen 的 --decl-gen 重新生成（且生成器必须已包含编织版本门）。");
            }

            RequireStaticInt32NoArg(version, "生成物版本方法");

            MethodDefinition require = FindMethod(type, RequireMethodName);
            if (require == null)
            {
                throw new WeaverException(
                    "类型 " + type.FullName + " 缺少 " + RequireMethodName + "()："
                    + "未编织程序集必须在 new 实例与注册处被拒（guard 缺失 ⇒ 明确失败）。"
                    + "修复：确认生成器已产出该 guard，并重新生成。");
            }

            RequireStaticInt32NoArg(require, "生成物 guard 方法");

            if (CountCallsTo(require, version) == 0)
            {
                throw new WeaverException(
                    "类型 " + type.FullName + " 的 " + RequireMethodName + " 没有读取 " + VersionMethodName + "："
                    + "这是「假 guard」（无法在未编织时拒绝）。明确失败。");
            }

            MethodDefinition entry = FindMethod(type, BuildEntryName);
            if (entry == null)
            {
                throw new WeaverException(
                    "类型 " + type.FullName + " 缺少 " + BuildEntryName + "()：注册入口缺失，"
                    + "无法保证「未编织 ⇒ 注册被拒」。明确失败。");
            }

            if (!entry.IsStatic || entry.Parameters.Count != 0)
            {
                throw new WeaverException(
                    "类型 " + type.FullName + " 的 " + BuildEntryName + " 签名不符（应为 static 且无参）。");
            }

            if (CountCallsTo(entry, require) == 0)
            {
                throw new WeaverException(
                    "类型 " + type.FullName + " 的 " + BuildEntryName + " 没有调用 " + RequireMethodName + "()："
                    + "契约要求注册入口开头调用 guard（否则未编织程序集也能注册）。明确失败。");
            }

            // ── 上面只证明了「guard 读了版本」「注册入口调了 guard」，**没有**证明 guard 真会抛：
            //    `private static int PMNet_RequireRpcWeave() { PMNet_GetRpcWeaveVersion(); return 1; }`
            //    同样满足 CountCallsTo>0（P3 已用真实变体证明旧实现会放行这种「假 guard」）。
            //    这里对冻结形态做两条**语义**断言（有界抽象求值，不认识就明确失败）：
            //      · 版本 == 1 ⇒ guard 正常返回整型 1（否则已编织程序集会被自己的 guard 拒绝）；
            //      · 版本 != 1 ⇒ guard **真的抛** System.InvalidOperationException。
            VerifyGuardThrowsWhenUnwoven(type, require, version);

            // 未编织程序集的实例侧门：生成物的实例 readonly gate 字段在**每个**
            // 非 `: this(...)` 链式构造函数里调用 Require（字段初始化），否则未编织时 new 不会被拒。
            ValidateInstanceGate(bucket, require);
        }

        /// <summary>
        /// 证明 guard 的**行为**（不只是「调了版本方法」）：版本为 1 时返回 1；
        /// 版本为 0 / 2 / -1 时抛 System.InvalidOperationException。
        ///
        /// 为什么不能只做语法检查："读版本 + 恒 return 1" 的假 guard 完全满足「调用了版本方法」，
        /// 但它永远不抛 ⇒ 未编织程序集能被直接 new。只有把两条路径真的求值出来才能证伪。
        /// </summary>
        private static void VerifyGuardThrowsWhenUnwoven(
            TypeDefinition type,
            MethodDefinition require,
            MethodDefinition version)
        {
            string what = "类型 " + type.FullName + " 的 " + RequireMethodName + "()";

            // (1) 版本 == 1：必须正常返回 1
            SimResult woven = SimulateBody(require, version, WovenVersion, what);
            if (woven.Outcome == SimOutcome.Threw)
            {
                throw new WeaverException(
                    what + " 在版本为 " + WovenVersion + " 时抛出 " + woven.ExceptionType
                    + "：已编织程序集会被自己的 guard 拒绝，形态不符。");
            }

            if (woven.Outcome != SimOutcome.ReturnedInt || woven.IntValue != WovenVersion)
            {
                throw new WeaverException(
                    what + " 在版本为 " + WovenVersion + " 时返回 "
                    + (woven.Outcome == SimOutcome.ReturnedInt ? woven.IntValue.ToString() : "<void>")
                    + "（期望 " + WovenVersion + "）：guard 形态不符。");
            }

            // (2) 版本 != 1：必须**真的抛** System.InvalidOperationException
            int[] unwoven = new int[] { 0, 2, -1 };
            for (int i = 0; i < unwoven.Length; i++)
            {
                SimResult bad = SimulateBody(require, version, unwoven[i], what);
                if (bad.Outcome != SimOutcome.Threw)
                {
                    throw new WeaverException(
                        what + " 在版本为 " + unwoven[i] + " 时没有抛异常（返回 "
                        + (bad.Outcome == SimOutcome.ReturnedInt ? bad.IntValue.ToString() : "<void>")
                        + "）：这是「假 guard」——未编织程序集不会被拒。明确失败。");
                }

                if (!string.Equals(bad.ExceptionType, InvalidOperationExceptionFullName, StringComparison.Ordinal))
                {
                    throw new WeaverException(
                        what + " 在版本为 " + unwoven[i] + " 时抛的是 " + bad.ExceptionType
                        + "（期望 " + InvalidOperationExceptionFullName + "）：guard 形态不符。");
                }
            }
        }

        /// <summary>
        /// 未编织程序集的实例侧门：生成物在实例字段初始化里调用 Require
        /// （`private readonly int PMNet_rpcWeaveGate = PMNet_RequireRpcWeave();`），
        /// 字段初始化会被 Roslyn 发射进**每个非 `: this(...)` 链式构造函数**。
        /// 因此这里断言：gate 字段存在且形态正确，且每个非 this 链构造函数都调用了 Require。
        ///
        /// P3 反例：把字段初始化改成 `= 1;`（或删掉字段）后，`PMNet_BuildEntry` 仍然调 guard，
        /// 注册依旧被拒，但 `new` 会成功 —— 「正常 new 实例与注册均拒绝未编织程序集」缺了一半。
        /// </summary>
        private static void ValidateInstanceGate(ClassBucket bucket, MethodDefinition require)
        {
            TypeDefinition type = bucket.Type;

            FieldDefinition gate = null;
            for (int i = 0; i < type.Fields.Count; i++)
            {
                if (string.Equals(type.Fields[i].Name, GateFieldName, StringComparison.Ordinal))
                {
                    gate = type.Fields[i];
                    break;
                }
            }

            if (gate == null)
            {
                throw new WeaverException(
                    "类型 " + type.FullName + " 缺少实例 gate 字段 " + GateFieldName + "："
                    + "契约要求实例字段初始化调用 " + RequireMethodName + "()，否则未编织程序集 new 时不会被拒。"
                    + "修复：确认生成器已产出该字段并重新生成。");
            }

            if (gate.IsStatic || !gate.IsPrivate || !gate.IsInitOnly
                || gate.FieldType == null || gate.FieldType.MetadataType != MetadataType.Int32)
            {
                throw new WeaverException(
                    "类型 " + type.FullName + " 的实例 gate 字段 " + GateFieldName
                    + " 形态不符（应为 private readonly 实例 int）。");
            }

            int instanceCtors = 0;
            int gatedCtors = 0;
            List<string> ungated = new List<string>();
            for (int i = 0; i < type.Methods.Count; i++)
            {
                MethodDefinition ctor = type.Methods[i];
                if (!ctor.IsConstructor || ctor.IsStatic)
                {
                    continue;
                }

                instanceCtors++;
                if (CountCallsTo(ctor, require) > 0)
                {
                    gatedCtors++;
                    continue;
                }

                // `: this(...)` 链式构造：字段初始化只出现在链的根构造函数里，这里不算缺失。
                if (IsThisChainedConstructor(ctor, type))
                {
                    continue;
                }

                ungated.Add(ctor.Name);
            }

            if (instanceCtors == 0)
            {
                throw new WeaverException(
                    "类型 " + type.FullName + " 没有任何实例构造函数：无法保证未编织时 new 被拒，形态不符。");
            }

            if (gatedCtors == 0 || ungated.Count > 0)
            {
                throw new WeaverException(
                    "类型 " + type.FullName + " 的实例构造函数（"
                    + (ungated.Count == 0 ? "<全部>" : string.Join(", ", ungated.ToArray()))
                    + "）没有调用 " + RequireMethodName + "()：未编织程序集可以被直接 new（实例 gate 失效）。明确失败。");
            }
        }

        /// <summary>判断构造函数是否是 `: this(...)` 链式构造（字段初始化只在链的根构造函数里）。</summary>
        private static bool IsThisChainedConstructor(MethodDefinition ctor, TypeDefinition type)
        {
            if (!ctor.HasBody)
            {
                return false;
            }

            List<Instruction> instructions = new List<Instruction>(ctor.Body.Instructions);
            for (int i = 0; i < instructions.Count; i++)
            {
                Instruction ins = instructions[i];
                if (ins.OpCode.Code != Code.Call && ins.OpCode.Code != Code.Callvirt)
                {
                    continue;
                }

                MethodReference target = ins.Operand as MethodReference;
                if (target == null || !string.Equals(target.Name, ".ctor", StringComparison.Ordinal))
                {
                    continue;
                }

                TypeReference declaring = target.DeclaringType;
                bool sameType = declaring != null
                                && string.Equals(declaring.FullName, type.FullName, StringComparison.Ordinal);

                // C# 保证构造函数里第一条 ctor 调用要么是基类 .ctor、要么是 this(...)：
                // 是同类 ctor ⇒ this 链；是基类 ⇒ 不是。
                return sameType;
            }

            return false;
        }

        private static void RequireStaticInt32NoArg(MethodDefinition method, string what)
        {
            if (!method.IsStatic || method.Parameters.Count != 0
                || method.ReturnType == null || method.ReturnType.MetadataType != MetadataType.Int32)
            {
                throw new WeaverException(
                    what + " " + Loc(method) + " 签名不符（应为 static int " + method.Name + "()）。");
            }

            if (!method.HasBody)
            {
                throw new WeaverException(what + " " + Loc(method) + " 没有方法体。");
            }
        }

        private static int ReadWeaveVersion(TypeDefinition type)
        {
            MethodDefinition version = FindMethod(type, VersionMethodName);
            if (version == null || !version.HasBody)
            {
                throw new WeaverException("类型 " + type.FullName + " 的 " + VersionMethodName + " 不可读。");
            }

            // 版本方法的 IL 形态**不是**固定的：Debug 构建里 `return 0;` 会被编成
            //   nop; ldc.i4.0; stloc.0; br.s <end>; ldloc.0; ret
            // 而 Release 是 `ldc.i4.0; ret`。因此判据不能是固定指令序列，也不能只“数常量个数”：
            //   P3 已证伪「恰好 1 个 ret / 不含调用 / 恰好 1 个整型常量 ⇒ 返回的就是那个常量」——
            //   `int v = 1; return -v;`（ldc.i4.1; stloc.0; ldloc.0; neg; ret）满足那三条不变式，
            //   实际却返回 -1，会被读成 1（于是 `--check` 报“已编织”绿、运行期 new 必抛）。
            // 现在改为**有界抽象求值出真实返回值**（见 IlEval 段）：认识就精确算，
            // 不认识就以明确失败拒绝（绝不用“大概是这样”放行）。
            SimResult result = SimulateBody(
                version,
                null,
                0,
                "类型 " + type.FullName + " 的 " + VersionMethodName + "()");

            if (result.Outcome != SimOutcome.ReturnedInt)
            {
                throw new WeaverException(
                    "类型 " + type.FullName + " 的 " + VersionMethodName
                    + " 不以整型返回结束：版本必须是编译期常量返回，拒绝猜测版本。");
            }

            return result.IntValue;
        }

        private static void ValidateRpcSignature(ClassBucket bucket)
        {
            TypeDefinition type = bucket.Type;

            for (int k = 0; k < bucket.Methods.Count; k++)
            {
                MethodDefinition m = bucket.Methods[k];

                int sameName = 0;
                for (int i = 0; i < type.Methods.Count; i++)
                {
                    if (string.Equals(type.Methods[i].Name, m.Name, StringComparison.Ordinal))
                    {
                        sameName++;
                    }
                }

                if (sameName != 1)
                {
                    throw new WeaverException(
                        "RPC " + Loc(m) + " 存在同名重载（同名方法 " + sameName + " 个）：契约不支持 RPC 同名重载。");
                }

                if (m.IsConstructor)
                {
                    throw new WeaverException("RPC " + Loc(m) + " 是构造函数：不支持。");
                }

                if (m.IsStatic)
                {
                    throw new WeaverException("RPC " + Loc(m) + " 是 static：不支持（契约只支持实例方法）。");
                }

                if (m.IsVirtual)
                {
                    throw new WeaverException(
                        "RPC " + Loc(m) + " 是 virtual：契约初版不处理虚方法/网络继承，明确拒绝（不猜重写继承链）。");
                }

                if (m.IsAbstract)
                {
                    throw new WeaverException("RPC " + Loc(m) + " 是 abstract：不支持（必须有业务体）。");
                }

                if (m.IsPInvokeImpl
                    || (m.ImplAttributes & MethodImplAttributes.InternalCall) != 0
                    || (m.ImplAttributes & MethodImplAttributes.Native) != 0)
                {
                    throw new WeaverException("RPC " + Loc(m) + " 是 extern/native：不支持（必须有托管业务体）。");
                }

                if (m.HasGenericParameters)
                {
                    throw new WeaverException("RPC " + Loc(m) + " 是泛型方法：不支持。");
                }

                if (m.ReturnType == null || m.ReturnType.MetadataType != MetadataType.Void)
                {
                    throw new WeaverException("RPC " + Loc(m) + " 必须返回 void。");
                }

                if (!m.HasBody)
                {
                    throw new WeaverException("RPC " + Loc(m) + " 没有方法体：无法拆分（契约要求普通方法直接写业务体）。");
                }

                if (HasAttribute(m, "System.Runtime.CompilerServices.AsyncStateMachineAttribute"))
                {
                    throw new WeaverException("RPC " + Loc(m) + " 是 async：不支持。");
                }

                for (int i = 0; i < m.Parameters.Count; i++)
                {
                    ParameterDefinition p = m.Parameters[i];
                    if (p.ParameterType != null && p.ParameterType.IsByReference)
                    {
                        throw new WeaverException(
                            "RPC " + Loc(m) + " 的参数 " + p.Name + " 是 ref/out/in：不支持（契约禁止）。");
                    }

                    if (p.IsOut || p.IsIn)
                    {
                        throw new WeaverException(
                            "RPC " + Loc(m) + " 的参数 " + p.Name + " 带 In/Out 修饰：不支持（契约禁止）。");
                    }

                    if (p.IsOptional || p.HasDefault || p.HasConstant)
                    {
                        throw new WeaverException(
                            "RPC " + Loc(m) + " 的参数 " + p.Name + " 有默认值：不支持（契约禁止 default 参数）。");
                    }

                    if (HasAttribute(p, "System.ParamArrayAttribute"))
                    {
                        throw new WeaverException(
                            "RPC " + Loc(m) + " 的参数 " + p.Name + " 是 params：不支持（契约禁止）。");
                    }

                    if (p.ParameterType != null && p.ParameterType.IsPointer)
                    {
                        throw new WeaverException(
                            "RPC " + Loc(m) + " 的参数 " + p.Name + " 是指针类型：不支持。");
                    }
                }
            }
        }

        private static void ValidateUnwovenClass(ClassBucket bucket)
        {
            TypeDefinition type = bucket.Type;

            for (int k = 0; k < bucket.Methods.Count; k++)
            {
                MethodDefinition m = bucket.Methods[k];

                if (FindMethod(type, BodyPrefix + m.Name) != null)
                {
                    throw new WeaverException(
                        "类型 " + type.FullName + " 的 " + VersionMethodName + " 返回 0（视为未编织），"
                        + "但已存在私有业务体 " + BodyPrefix + m.Name
                        + "：结构自相矛盾（stamp 丢失或半编织），明确失败，不重复拆。");
                }

                MethodDefinition send = FindMethod(type, SendPrefix + m.Name);
                if (send == null)
                {
                    throw new WeaverException(
                        "类型 " + type.FullName + " 的 RPC " + m.Name + " 缺少生成物发送 helper "
                        + SendPrefix + m.Name + "：有 RPC 无生成物 ⇒ 明确失败。"
                        + "修复：运行 PMNetGen 的 --decl-gen 重新生成。");
                }

                if (send.IsStatic)
                {
                    throw new WeaverException("发送 helper " + Loc(send) + " 是 static：冻结格式要求实例方法。");
                }

                if (send.ReturnType == null || send.ReturnType.MetadataType != MetadataType.Void)
                {
                    throw new WeaverException("发送 helper " + Loc(send) + " 必须返回 void。");
                }

                if (send.IsPublic)
                {
                    throw new WeaverException(
                        "发送 helper " + Loc(send) + " 是 public：冻结格式 v1 要求它必须是 private"
                        + "（只有编织后的同类入口 " + m.Name + " 才能用它，业务不得直接调用）。"
                        + "这属于生成器侧改动（P2）：把发射器的 `public void " + SendPrefix + "<M>` 改成 private。");
                }

                if (!send.HasBody)
                {
                    throw new WeaverException("发送 helper " + Loc(send) + " 没有方法体。");
                }

                RequireSameParameterTypes(m, send, "发送 helper");

                int sendCalls = CountCallsTo(send, m);
                if (sendCalls != 1)
                {
                    throw new WeaverException(
                        "发送 helper " + Loc(send) + " 必须**恰好一次**调用 " + m.Name
                        + "（实际 " + sendCalls + "）：契约要求编织前两个 helper 各恰一次调用原始普通方法。");
                }

                MethodDefinition invoke = FindMethod(type, InvokePrefix + m.Name);
                if (invoke == null)
                {
                    throw new WeaverException(
                        "类型 " + type.FullName + " 的 RPC " + m.Name + " 缺少生成物收包 helper "
                        + InvokePrefix + m.Name + "：有 RPC 无生成物 ⇒ 明确失败。");
                }

                if (!invoke.IsStatic)
                {
                    throw new WeaverException("收包 helper " + Loc(invoke) + " 必须是 static。");
                }

                if (invoke.ReturnType == null || invoke.ReturnType.MetadataType != MetadataType.Void)
                {
                    throw new WeaverException("收包 helper " + Loc(invoke) + " 必须返回 void。");
                }

                if (invoke.Parameters.Count != 2
                    || invoke.Parameters[0].ParameterType == null
                    || invoke.Parameters[0].ParameterType.FullName != ObjectTypeFullName
                    || invoke.Parameters[1].ParameterType == null
                    || invoke.Parameters[1].ParameterType.FullName != ReaderTypeFullName)
                {
                    throw new WeaverException(
                        "收包 helper " + Loc(invoke) + " 签名不符（应为 static void "
                        + InvokePrefix + "<M>(" + ObjectTypeFullName + ", " + ReaderTypeFullName + ")）。");
                }

                if (!invoke.HasBody)
                {
                    throw new WeaverException("收包 helper " + Loc(invoke) + " 没有方法体。");
                }

                int invokeCalls = CountCallsTo(invoke, m);
                if (invokeCalls != 1)
                {
                    throw new WeaverException(
                        "收包 helper " + Loc(invoke) + " 必须**恰好一次**调用 " + m.Name
                        + "（实际 " + invokeCalls + "）：契约要求编织前两个 helper 各恰一次调用原始普通方法。");
                }
            }
        }

        private static void ValidateWovenClass(ClassBucket bucket)
        {
            TypeDefinition type = bucket.Type;

            for (int k = 0; k < bucket.Methods.Count; k++)
            {
                MethodDefinition m = bucket.Methods[k];
                MethodDefinition send = FindMethod(type, SendPrefix + m.Name);
                MethodDefinition invoke = FindMethod(type, InvokePrefix + m.Name);
                MethodDefinition body = FindMethod(type, BodyPrefix + m.Name);

                if (body == null)
                {
                    throw new WeaverException(
                        "类型 " + type.FullName + " 的 " + VersionMethodName + " 返回 1（声称已编织），"
                        + "但缺少私有业务体 " + BodyPrefix + m.Name + "：损坏/半编织，明确失败。");
                }

                if (body.IsStatic || body.IsPublic)
                {
                    throw new WeaverException("私有业务体 " + Loc(body) + " 必须是 private 实例方法。");
                }

                if (body.ReturnType == null || body.ReturnType.MetadataType != MetadataType.Void)
                {
                    throw new WeaverException("私有业务体 " + Loc(body) + " 必须返回 void。");
                }

                RequireSameParameterTypes(m, body, "私有业务体");
                if (body.ImplAttributes != m.ImplAttributes)
                {
                    throw new WeaverException("私有业务体实现标志与入口不一致（Synchronized等语义可能丢失）："
                        + Loc(body) + "；请重新编译再编织。");
                }

                if (FindRpcMarkers(body).Count != 0)
                {
                    throw new WeaverException(
                        "私有业务体 " + Loc(body) + " 仍带 RPC Attribute：契约要求业务体无 RPC Attribute。");
                }

                if (send == null || invoke == null || !send.HasBody || !invoke.HasBody)
                {
                    throw new WeaverException("类型 " + type.FullName + " 的 RPC " + m.Name + " 缺少 helper。");
                }

                // 1) wrapper 结构：ldarg.0 + 各参数 → call 发送 helper → ret
                List<Instruction> wrapper = new List<Instruction>();
                for (int i = 0; i < m.Body.Instructions.Count; i++)
                {
                    Instruction ins = m.Body.Instructions[i];
                    if (ins.OpCode.Code != Code.Nop)
                    {
                        wrapper.Add(ins);
                    }
                }

                int expected = m.Parameters.Count + 3;
                if (wrapper.Count != expected)
                {
                    throw new WeaverException(
                        "RPC 入口 " + Loc(m) + " 的 IL 不是「this+参数 → call 发送 helper → ret」（指令数 "
                        + wrapper.Count + "，期望 " + expected + "）：stamp=1 但 wrapper 未改写（不能只有 stamp 就算绿）。");
                }

                if (m.Body.HasExceptionHandlers || m.Body.HasVariables)
                {
                    throw new WeaverException("RPC 入口 " + Loc(m) + " 不应有异常处理/局部变量（wrapper 形态）。");
                }

                for (int i = 0; i <= m.Parameters.Count; i++)
                {
                    int loaded;
                    if (!TryGetLoadedArgumentIndex(wrapper[i], m, out loaded) || loaded != i)
                    {
                        throw new WeaverException(
                            "RPC 入口 " + Loc(m) + " 的第 " + i + " 条指令不是加载第 " + i + " 个参数：wrapper 形态不符。");
                    }
                }

                Instruction call = wrapper[wrapper.Count - 2];
                if (call.OpCode.Code != Code.Call || !Targets(call.Operand as MethodReference, send))
                {
                    throw new WeaverException(
                        "RPC 入口 " + Loc(m) + " 没有 call 发送 helper " + SendPrefix + m.Name + "：wrapper 形态不符。");
                }

                if (wrapper[wrapper.Count - 1].OpCode.Code != Code.Ret)
                {
                    throw new WeaverException("RPC 入口 " + Loc(m) + " 的最后一条指令不是 ret。");
                }

                // 2) 两个 helper 的唯一业务调用都指向私有业务体，且都不再调用 M（否则递归）
                if (CountCallsTo(send, body) != 1)
                {
                    throw new WeaverException(
                        "发送 helper " + Loc(send) + " 没有**恰好一次**调用私有业务体 " + BodyPrefix + m.Name
                        + "（实际 " + CountCallsTo(send, body) + "）：损坏 helper。");
                }

                if (CountCallsTo(send, m) != 0)
                {
                    throw new WeaverException(
                        "发送 helper " + Loc(send) + " 仍在调用 " + m.Name + "：会与原入口互相递归。");
                }

                if (CountCallsTo(invoke, body) != 1)
                {
                    throw new WeaverException(
                        "收包 helper " + Loc(invoke) + " 没有**恰好一次**调用私有业务体 " + BodyPrefix + m.Name
                        + "（实际 " + CountCallsTo(invoke, body) + "）：损坏 helper。");
                }

                if (CountCallsTo(invoke, m) != 0)
                {
                    throw new WeaverException(
                        "收包 helper " + Loc(invoke) + " 仍在调用 " + m.Name + "：收包路径会递归回网络入口。");
                }
            }
        }

        // =================================================================================
        //  编织
        // =================================================================================

        private static void WeaveMethod(ClassBucket bucket, MethodDefinition m, List<PdbProbe> expectations)
        {
            TypeDefinition type = bucket.Type;
            MethodDefinition send = FindMethod(type, SendPrefix + m.Name);
            MethodDefinition invoke = FindMethod(type, InvokePrefix + m.Name);

            if (send == null || invoke == null)
            {
                throw new WeaverException("内部一致性错误：编织 " + Loc(m) + " 时找不到 helper。");
            }

            // 克隆前先记下源方法的 sequence point 条数：落盘后用它做 PDB 完整性断言。
            MethodDebugInformation sourceDebug = m.DebugInformation;
            int sourceSequencePoints = sourceDebug == null ? 0 : sourceDebug.SequencePoints.Count;

            // 1) 完整克隆业务体到私有 PMNet_RpcBody_<M>
            MethodDefinition body = ILBodyCloner.CloneBody(m, BodyPrefix + m.Name);
            type.Methods.Add(body);

            PdbProbe expectation = new PdbProbe();
            expectation.TypeFullName = type.FullName;
            expectation.MethodName = body.Name;
            expectation.ParameterCount = body.Parameters.Count;
            expectation.ExpectedSequencePoints = sourceSequencePoints;
            expectations.Add(expectation);

            // 2) 两个 helper 的唯一业务调用改指私有业务体（保留其它业务里对 M 的普通调用）
            RetargetSingleCall(send, m, body);
            RetargetSingleCall(invoke, m, body);

            // 3) 原 M 的 IL 改为发包入口：this + 各参数 → call PMNet_<M> → ret
            BuildNetworkEntry(m, send);
        }

        private static void BuildNetworkEntry(MethodDefinition m, MethodDefinition send)
        {
            MethodBody body = m.Body;
            body.Instructions.Clear();
            body.ExceptionHandlers.Clear();
            body.Variables.Clear();
            body.InitLocals = false;
            body.MaxStackSize = m.Parameters.Count + 1;

            body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            for (int i = 0; i < m.Parameters.Count; i++)
            {
                body.Instructions.Add(Instruction.Create(OpCodes.Ldarg, m.Parameters[i]));
            }

            body.Instructions.Add(Instruction.Create(OpCodes.Call, send));
            body.Instructions.Add(Instruction.Create(OpCodes.Ret));

            // 调试信息：**清空而不新增**。
            //
            // 为什么不是“给 wrapper 加一条指向原首行的 sequence point”：Mono.Cecil 0.11.6 的
            // portable-PDB 写入器在“把某个方法的 sequence point 集合换成另一组非空集合”时
            // 会写出损坏的 SP blob（独立读取器 System.Reflection.Metadata 报
            // BadImageFormatException: Invalid compressed integer；已用最小实验隔离：
            // Clear→0 条有效、原地改值且条数不变有效、Clear 后 Add 新集合损坏）。
            // 因此 wrapper 一律写 0 条（有效且诚实：它是一个两三条指令的合成转发桩），
            // 真正的行号信息完整保留在 PMNet_RpcBody_<M> 上。
            // 落盘前还会用独立读取器逐个方法校验 PDB（见 VerifyPdbIntegrity），
            // 任何 PDB 损坏都会变成明确失败，不会静默写出去。
            MethodDebugInformation debug = m.DebugInformation;
            if (debug != null)
            {
                debug.SequencePoints.Clear();
                debug.Scope = null;
            }
        }

        private static void SetWeaveVersion(TypeDefinition type, int version)
        {
            MethodDefinition method = FindMethod(type, VersionMethodName);
            if (method == null || !method.HasBody)
            {
                throw new WeaverException("内部一致性错误：无法写版本到 " + type.FullName + "。");
            }

            MethodBody body = method.Body;
            body.Instructions.Clear();
            body.ExceptionHandlers.Clear();
            body.Variables.Clear();
            body.InitLocals = false;
            body.MaxStackSize = 1;
            body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, version));
            body.Instructions.Add(Instruction.Create(OpCodes.Ret));

            MethodDebugInformation debug = method.DebugInformation;
            if (debug != null)
            {
                debug.SequencePoints.Clear();
                debug.Scope = null;
            }
        }

        private static void RetargetSingleCall(MethodDefinition helper, MethodDefinition from, MethodDefinition to)
        {
            if (helper == null || !helper.HasBody)
            {
                throw new WeaverException("内部一致性错误：helper 不可用。");
            }

            int changed = 0;
            for (int i = 0; i < helper.Body.Instructions.Count; i++)
            {
                Instruction ins = helper.Body.Instructions[i];
                if (ins.OpCode.Code != Code.Call && ins.OpCode.Code != Code.Callvirt)
                {
                    continue;
                }

                MethodReference target = ins.Operand as MethodReference;
                if (target == null || !Targets(target, from))
                {
                    continue;
                }

                ins.Operand = to;
                changed++;
            }

            if (changed != 1)
            {
                throw new WeaverException(
                    "内部一致性错误：helper " + Loc(helper) + " 对 " + from.Name
                    + " 的调用数 " + changed + "（期望 1）。");
            }
        }

        // =================================================================================
        //  原子落盘
        // =================================================================================

        private static void WriteAtomically(
            AssemblyDefinition assembly,
            string fullPath,
            bool pdbOnDisk,
            WeaveReport report,
            List<PdbProbe> expectations)
        {
            bool writeSymbols = assembly.MainModule.HasSymbols;
            if (pdbOnDisk && !writeSymbols)
            {
                throw new WeaverException("输入有 PDB 但内存中没有符号：拒绝写出（不得静默丢调试信息）。");
            }

            string pdbPath = Path.ChangeExtension(fullPath, ".pdb");
            string stamp = ".pmweave-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string tmpDll = fullPath + stamp + ".tmp";
            string tmpPdb = Path.ChangeExtension(tmpDll, ".pdb");

            List<string> staged = new List<string>();

            try
            {
                WriterParameters writerParameters = new WriterParameters();
                writerParameters.WriteSymbols = writeSymbols;
                if (writeSymbols)
                {
                    // 外部 PDB 与 embedded PDB 分别用对应的 writer provider（不偷换格式）。
                    if (pdbOnDisk)
                    {
                        writerParameters.SymbolWriterProvider = new PortablePdbWriterProvider();
                    }
                    else
                    {
                        writerParameters.SymbolWriterProvider = new EmbeddedPortablePdbWriterProvider();
                    }
                }

                assembly.Write(tmpDll, writerParameters);
                staged.Add(tmpDll);

                // ★ 先把暂存 PDB 也登记进清理列表，再做校验：
                //   旧实现在 VerifyPdbIntegrity 抛异常时还没登记 tmpPdb，于是失败路径会在目标目录里
                //   留下 `*.pmweave-*.pdb` 残留（本仓库 Tools/PMNetE2E/obj/Release/net8.0/
                //   PMNetE2E.dll.pmweave-b79a9e4c.pdb 就是这条留下的真实证据）。
                if (File.Exists(tmpPdb))
                {
                    staged.Add(tmpPdb);
                }

                if (!File.Exists(tmpDll))
                {
                    throw new WeaverException("写出失败：暂存文件不存在（" + tmpDll + "）。");
                }

                if (pdbOnDisk && !File.Exists(tmpPdb))
                {
                    throw new WeaverException("写出失败：暂存 PDB 不存在（" + tmpPdb + "）：不得静默丢调试信息。");
                }

                // 独立读取器校验：PDB 必须能被第三方解析，且克隆出来的业务体
                // 保留与源方法同样多的 sequence point；任何异常/不符 ⇒ 明确失败，不落盘。
                if (writeSymbols)
                {
                    VerifyPdb(tmpDll, expectations, "写出后");
                    report.Note("PDB 独立校验通过（方法数、sequence point 与局部作用域均可解析，共核对 "
                                + expectations.Count + " 个克隆业务体）。");
                }

                // 两个文件都先落到暂存，再替换；替换失败则从备份回滚。
                List<string> targets = new List<string>();
                List<string> sources = new List<string>();
                List<long> expectedLengths = new List<long>();
                targets.Add(fullPath);
                sources.Add(tmpDll);
                expectedLengths.Add(new FileInfo(tmpDll).Length);
                if (File.Exists(tmpPdb) && pdbOnDisk)
                {
                    targets.Add(pdbPath);
                    sources.Add(tmpPdb);
                    expectedLengths.Add(new FileInfo(tmpPdb).Length);
                }

                CommitWithRollback(targets, sources, expectedLengths, stamp);

                report.SymbolsOut = writeSymbols;
                report.Note("已原子替换（长度已核对）：" + string.Join(", ", targets.ToArray()));
            }
            finally
            {
                for (int i = 0; i < staged.Count; i++)
                {
                    TryDelete(staged[i]);
                }
            }
        }

        /// <summary>
        /// 提交替换：先为每个目标做一份**显式备份**，再用 `File.Move(overwrite: true)` 把暂存文件换上去。
        ///
        /// 为什么不用 `File.Replace`（它内部是 Windows 的 `ReplaceFile`）：
        /// `ReplaceFile` 会让目标路径**保留原来的文件身份**（file id）。实测在本机上，这会让
        /// “先加载过旧 DLL、再替换路径、再从一个新 AssemblyLoadContext 按同一路径加载”
        /// 仍拿到**旧内容的程序集**（已用最小实验复现：同一份新文件换个路径就能正确加载）。
        /// 对 Unity 编辑器这类“可能已经加载过该程序集”的宿主，这是一个真实陷阱：
        /// 域名重载后可能跑旧代码，而磁盘上的文件看上去已经换新了。
        /// `File.Move(..., overwrite: true)` 走的是重命名语义（目标获得源文件的身份），
        /// 没有这个陷阱；代价是需要自己做备份/回滚，这里显式做了。
        /// </summary>
        private static void CommitWithRollback(
            List<string> targets,
            List<string> sources,
            List<long> expectedLengths,
            string stamp)
        {
            List<string> backups = new List<string>();
            List<string> doneTargets = new List<string>();
            HashSet<string> keepBackups = new HashSet<string>(StringComparer.Ordinal);
            List<string> problems = new List<string>();
            int committed = 0;

            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    string dest = targets[i];
                    string src = sources[i];

                    // ★ 备份名带本次调用的唯一 stamp：旧实现写死 `<目标>.pmweave.bak`，
                    //   两个并发 weave 会互相覆盖/删除对方的备份（并把对方回滚所需的原始内容删掉）。
                    string backup = dest + stamp + ".bak";

                    TryDelete(backup);

                    bool hadOriginal = File.Exists(dest);
                    if (hadOriginal)
                    {
                        File.Copy(dest, backup, true);
                    }

                    // ★ 先登记再 Move：旧实现在 Move 之后才 Add，若“复制备份成功、Move 失败”，
                    //   那个备份不会被清理（失败路径留下 .bak 残留）。
                    backups.Add(hadOriginal ? backup : null);
                    doneTargets.Add(dest);

                    File.Move(src, dest, true);
                    committed++;
                }

                // 提交后长度核对：在删除备份**之前**做，不通过还能回滚。
                for (int i = 0; i < targets.Count; i++)
                {
                    if (!File.Exists(targets[i]))
                    {
                        throw new IOException("替换后目标不存在：" + targets[i]);
                    }

                    long actual = new FileInfo(targets[i]).Length;
                    if (actual != expectedLengths[i])
                    {
                        throw new IOException(
                            "替换后目标长度与暂存内容不符：" + targets[i] + "（" + actual
                            + " vs " + expectedLengths[i] + "）");
                    }
                }
            }
            catch (Exception ex)
            {
                // 回滚已替换的目标，保证不留下“半程序集/半 PDB”。
                for (int i = committed - 1; i >= 0; i--)
                {
                    string dest = doneTargets[i];
                    string backup = backups[i];
                    try
                    {
                        if (backup != null)
                        {
                            File.Copy(backup, dest, true);
                            long restored = new FileInfo(dest).Length;
                            long original = new FileInfo(backup).Length;
                            if (restored != original)
                            {
                                // 回滚“成功”但长度不对：不能当作已恢复；保留备份供人工恢复。
                                keepBackups.Add(backup);
                                problems.Add(dest + "（回滚后长度 " + restored + " 与备份 " + original
                                             + " 不符；原始内容备份保留在 " + backup + "）");
                            }
                        }
                        else if (File.Exists(dest))
                        {
                            // 替换前目标不存在 ⇒ 回滚必须删掉它，否则会留下一个新文件。
                            File.Delete(dest);
                        }
                    }
                    catch (Exception rollbackEx)
                    {
                        // ★ 旧实现这里 `catch {}` 吞一切，调用者只看到“已尝试回滚”，
                        //   不知道目标其实还是新内容。现在把具体文件报出来，并保留备份。
                        if (backup != null)
                        {
                            keepBackups.Add(backup);
                        }

                        problems.Add(dest + "（回滚失败：" + rollbackEx.GetType().Name + "：" + rollbackEx.Message
                                     + (backup == null ? string.Empty : "；原始内容备份保留在 " + backup) + "）");
                    }
                }

                string verdict = problems.Count == 0
                    ? "已从备份回滚全部已替换文件（目标为替换前内容）。"
                    : "★ 回滚未完成，以下目标可能仍是新内容：" + string.Join("；", problems.ToArray());

                throw new WeaverException("原子替换失败：" + ex.Message + "。" + verdict);
            }
            finally
            {
                for (int i = 0; i < backups.Count; i++)
                {
                    // 回滚失败 / 回滚长度不符的备份要保留（problems 里已写出路径）。
                    if (backups[i] != null && !keepBackups.Contains(backups[i]))
                    {
                        DeleteBackup(backups[i]);
                    }
                }
            }
        }

        /// <summary>删掉临时备份文件；短暂重试（杀软 / 索引器可能瞬时占用句柄）。</summary>
        private static void DeleteBackup(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        return;
                    }

                    File.Delete(path);
                    return;
                }
                catch
                {
                    System.Threading.Thread.Sleep(20);
                }
            }

            // 删不掉也只是留下一个 `.bak`，不影响 DLL/PDB 的正确性；但要让调用者知道。
            Console.Error.WriteLine("[PMNetWeaver] 警告：未能删除临时备份 " + path + "（不影响已提交的 DLL/PDB）。");
        }

        private static void TryDelete(string path)        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 清理失败不影响结论；残留文件不影响正确性（下次替换前都会被删）。
            }
        }

        // =================================================================================
        //  有界 IL 抽象求值器
        // =================================================================================
        //
        // 用途：把两个只能靠**行为**回答的问题变成可证明的结论：
        //   1. 版本方法 PMNet_GetRpcWeaveVersion() 到底返回几；
        //   2. guard PMNet_RequireRpcWeave() 在版本 != 1 时到底抛不抛（抛的是不是契约指定的异常）。
        //
        // 为什么必须有它（P3 用真实变体证伪的两个假绿）：
        //   · 「恰好 1 个 ret / 不含调用 / 恰好 1 个整型常量」并不等于「返回的就是那个常量」：
        //     `int v = 1; return -v;`（ldc.i4.1; stloc.0; ldloc.0; neg; ret）满足那三条不变式，
        //     实际返回 -1，会被读成 1（`--check` 报绿，而运行期 new 必抛）。
        //   · 「guard 调用了版本方法」并不等于「guard 真会抛」：
        //     `PMNet_GetRpcWeaveVersion(); return 1;` 恒不抛（假 guard）。
        //
        // 口径：**只认白名单指令**，认识就精确求值，不认识就抛 WeaverException（拒绝猜测）。
        // 白名单覆盖冻结格式在 Debug / Release 下的全部真实形态（已用真实产物逐条核对）：
        //   版本 Debug:   nop; ldc.i4.0; stloc.0; br.s <end>; ldloc.0; ret
        //   版本 Release: ldc.i4.0; ret
        //   guard Debug:  nop; call version; ldc.i4.1; ceq; ldc.i4.0; ceq; stloc.0; ldloc.0;
        //                 brfalse.s <ok>; nop; ldstr …; newobj IOE(string); throw;
        //                 <ok> ldc.i4.1; stloc.1; br.s <end>; <end> ldloc.1; ret
        //   guard Release: call version; ldc.i4.1; beq.s <ok>; ldstr …; newobj IOE(string); throw;
        //                  <ok> ldc.i4.1; ret

        private enum SimOutcome
        {
            ReturnedInt,
            ReturnedVoid,
            Threw,
        }

        private sealed class SimResult
        {
            public SimOutcome Outcome;
            public int IntValue;
            public string ExceptionType;
        }

        private enum SimKind
        {
            Null,
            Int,
            Str,
            Exception,
        }

        private struct SimValue
        {
            public SimKind Kind;
            public int Int;
            public string Text;
        }

        private static SimValue SimInt(int value)
        {
            SimValue v = new SimValue();
            v.Kind = SimKind.Int;
            v.Int = value;
            return v;
        }

        private static SimValue SimStr(string text)
        {
            SimValue v = new SimValue();
            v.Kind = SimKind.Str;
            v.Text = text;
            return v;
        }

        private static SimValue SimException(string typeFullName)
        {
            SimValue v = new SimValue();
            v.Kind = SimKind.Exception;
            v.Text = typeFullName;
            return v;
        }

        private static SimValue SimPop(List<SimValue> stack, string what, Instruction ins)
        {
            if (stack.Count == 0)
            {
                throw new WeaverException(
                    what + "：IL_" + ins.Offset.ToString("x4") + " 处求值时栈下溢，形态无法识别，拒绝猜测。");
            }

            SimValue v = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            return v;
        }

        private static int SimPopInt(List<SimValue> stack, string what, Instruction ins)
        {
            SimValue v = SimPop(stack, what, ins);
            if (v.Kind != SimKind.Int)
            {
                throw new WeaverException(
                    what + "：IL_" + ins.Offset.ToString("x4") + " 处期望整型操作数（实际 " + v.Kind
                    + "），形态无法识别，拒绝猜测。");
            }

            return v.Int;
        }

        private static void SimStoreLocal(
            SimValue[] locals,
            bool[] set,
            int index,
            SimValue value,
            string what,
            Instruction ins)
        {
            if (index < 0 || index >= locals.Length)
            {
                throw new WeaverException(
                    what + "：IL_" + ins.Offset.ToString("x4") + " 写入了越界局部变量 " + index + "，拒绝猜测。");
            }

            locals[index] = value;
            set[index] = true;
        }

        private static SimValue SimLoadLocal(
            SimValue[] locals,
            bool[] set,
            int index,
            string what,
            Instruction ins)
        {
            if (index < 0 || index >= locals.Length || !set[index])
            {
                throw new WeaverException(
                    what + "：IL_" + ins.Offset.ToString("x4") + " 读取了未初始化的局部变量 " + index + "，拒绝猜测。");
            }

            return locals[index];
        }

        private static int SimLocalIndex(Instruction ins)
        {
            switch (ins.OpCode.Code)
            {
                case Code.Ldloc_0:
                case Code.Stloc_0:
                    return 0;
                case Code.Ldloc_1:
                case Code.Stloc_1:
                    return 1;
                case Code.Ldloc_2:
                case Code.Stloc_2:
                    return 2;
                case Code.Ldloc_3:
                case Code.Stloc_3:
                    return 3;
                default:
                {
                    VariableDefinition local = ins.Operand as VariableDefinition;
                    return local == null ? -1 : local.Index;
                }
            }
        }

        private static int SimTargetIndex(
            Instruction target,
            Dictionary<Instruction, int> indexOf,
            string what,
            Instruction ins)
        {
            int index;
            if (target == null || !indexOf.TryGetValue(target, out index))
            {
                throw new WeaverException(
                    what + "：IL_" + ins.Offset.ToString("x4") + " 的分支目标不在方法体内，拒绝猜测。");
            }

            return index;
        }

        /// <summary>
        /// 对 <paramref name="method"/> 做一次有界抽象求值。
        /// <paramref name="allowedCall"/> 不为 null 时，方法里唯一允许的调用是它（返回值为 allowedCallValue）；
        /// 传 null 表示不允许任何调用（版本方法必须是编译期常量）。
        /// </summary>
        private static SimResult SimulateBody(
            MethodDefinition method,
            MethodDefinition allowedCall,
            int allowedCallValue,
            string what)
        {
            if (method == null || !method.HasBody)
            {
                throw new WeaverException(what + "：方法没有方法体，无法求值。");
            }

            MethodBody body = method.Body;
            List<Instruction> ins = new List<Instruction>(body.Instructions);
            Dictionary<Instruction, int> indexOf = new Dictionary<Instruction, int>();
            for (int i = 0; i < ins.Count; i++)
            {
                indexOf[ins[i]] = i;
            }

            List<SimValue> stack = new List<SimValue>();
            SimValue[] locals = new SimValue[body.Variables.Count];
            bool[] localSet = new bool[body.Variables.Count];

            int pc = 0;
            int steps = 0;
            int maxSteps = (ins.Count * 8) + 64;

            while (true)
            {
                if (++steps > maxSteps)
                {
                    throw new WeaverException(what + "：控制流无法在有限步内求值（疑似循环），拒绝猜测。");
                }

                if (pc < 0 || pc >= ins.Count)
                {
                    throw new WeaverException(what + "：控制流跳到了方法体之外，拒绝猜测。");
                }

                Instruction ci = ins[pc];
                Code code = ci.OpCode.Code;
                int next = pc + 1;

                switch (code)
                {
                    case Code.Nop:
                        break;

                    case Code.Ldc_I4_M1: stack.Add(SimInt(-1)); break;
                    case Code.Ldc_I4_0: stack.Add(SimInt(0)); break;
                    case Code.Ldc_I4_1: stack.Add(SimInt(1)); break;
                    case Code.Ldc_I4_2: stack.Add(SimInt(2)); break;
                    case Code.Ldc_I4_3: stack.Add(SimInt(3)); break;
                    case Code.Ldc_I4_4: stack.Add(SimInt(4)); break;
                    case Code.Ldc_I4_5: stack.Add(SimInt(5)); break;
                    case Code.Ldc_I4_6: stack.Add(SimInt(6)); break;
                    case Code.Ldc_I4_7: stack.Add(SimInt(7)); break;
                    case Code.Ldc_I4_8: stack.Add(SimInt(8)); break;
                    case Code.Ldc_I4_S: stack.Add(SimInt(Convert.ToInt32((sbyte)ci.Operand))); break;
                    case Code.Ldc_I4: stack.Add(SimInt(Convert.ToInt32((int)ci.Operand))); break;

                    case Code.Ldnull: stack.Add(new SimValue()); break;
                    case Code.Ldstr: stack.Add(SimStr((string)ci.Operand)); break;

                    case Code.Dup:
                    {
                        SimValue v = SimPop(stack, what, ci);
                        stack.Add(v);
                        stack.Add(v);
                        break;
                    }

                    case Code.Pop:
                        SimPop(stack, what, ci);
                        break;

                    case Code.Ldloc_0:
                    case Code.Ldloc_1:
                    case Code.Ldloc_2:
                    case Code.Ldloc_3:
                    case Code.Ldloc_S:
                    case Code.Ldloc:
                        stack.Add(SimLoadLocal(locals, localSet, SimLocalIndex(ci), what, ci));
                        break;

                    case Code.Stloc_0:
                    case Code.Stloc_1:
                    case Code.Stloc_2:
                    case Code.Stloc_3:
                    case Code.Stloc_S:
                    case Code.Stloc:
                    {
                        SimValue v = SimPop(stack, what, ci);
                        SimStoreLocal(locals, localSet, SimLocalIndex(ci), v, what, ci);
                        break;
                    }

                    case Code.Br:
                    case Code.Br_S:
                        next = SimTargetIndex(ci.Operand as Instruction, indexOf, what, ci);
                        break;

                    case Code.Brtrue:
                    case Code.Brtrue_S:
                        if (SimPopInt(stack, what, ci) != 0)
                        {
                            next = SimTargetIndex(ci.Operand as Instruction, indexOf, what, ci);
                        }

                        break;

                    case Code.Brfalse:
                    case Code.Brfalse_S:
                        if (SimPopInt(stack, what, ci) == 0)
                        {
                            next = SimTargetIndex(ci.Operand as Instruction, indexOf, what, ci);
                        }

                        break;

                    case Code.Beq:
                    case Code.Beq_S:
                    {
                        int b = SimPopInt(stack, what, ci);
                        int a = SimPopInt(stack, what, ci);
                        if (a == b)
                        {
                            next = SimTargetIndex(ci.Operand as Instruction, indexOf, what, ci);
                        }

                        break;
                    }

                    case Code.Bne_Un:
                    case Code.Bne_Un_S:
                    {
                        int b = SimPopInt(stack, what, ci);
                        int a = SimPopInt(stack, what, ci);
                        if (a != b)
                        {
                            next = SimTargetIndex(ci.Operand as Instruction, indexOf, what, ci);
                        }

                        break;
                    }

                    case Code.Ceq:
                    {
                        int b = SimPopInt(stack, what, ci);
                        int a = SimPopInt(stack, what, ci);
                        stack.Add(SimInt(a == b ? 1 : 0));
                        break;
                    }

                    case Code.Cgt:
                    case Code.Cgt_Un:
                    {
                        int b = SimPopInt(stack, what, ci);
                        int a = SimPopInt(stack, what, ci);
                        bool greater = code == Code.Cgt ? a > b : (uint)a > (uint)b;
                        stack.Add(SimInt(greater ? 1 : 0));
                        break;
                    }

                    case Code.Clt:
                    case Code.Clt_Un:
                    {
                        int b = SimPopInt(stack, what, ci);
                        int a = SimPopInt(stack, what, ci);
                        bool less = code == Code.Clt ? a < b : (uint)a < (uint)b;
                        stack.Add(SimInt(less ? 1 : 0));
                        break;
                    }

                    case Code.Neg:
                        stack.Add(SimInt(-SimPopInt(stack, what, ci)));
                        break;

                    case Code.Not:
                        stack.Add(SimInt(~SimPopInt(stack, what, ci)));
                        break;

                    case Code.Add:
                    case Code.Sub:
                    case Code.Mul:
                    case Code.And:
                    case Code.Or:
                    case Code.Xor:
                    {
                        int b = SimPopInt(stack, what, ci);
                        int a = SimPopInt(stack, what, ci);
                        int r;
                        switch (code)
                        {
                            case Code.Add: r = a + b; break;
                            case Code.Sub: r = a - b; break;
                            case Code.Mul: r = a * b; break;
                            case Code.And: r = a & b; break;
                            case Code.Or: r = a | b; break;
                            default: r = a ^ b; break;
                        }

                        stack.Add(SimInt(r));
                        break;
                    }

                    case Code.Call:
                    case Code.Callvirt:
                    {
                        MethodReference reference = ci.Operand as MethodReference;
                        if (allowedCall == null)
                        {
                            throw new WeaverException(
                                what + " 含调用指令（" + DescribeCall(reference) + "，IL_"
                                + ci.Offset.ToString("x4")
                                + "）：版本必须是编译期常量，拒绝猜测（未知版本必须明确失败）。");
                        }

                        if (!Targets(reference, allowedCall))
                        {
                            throw new WeaverException(
                                what + " 调用了非版本方法（" + DescribeCall(reference) + "，IL_"
                                + ci.Offset.ToString("x4") + "）：guard 形态不符，拒绝猜测。");
                        }

                        int argc = reference == null ? 0 : reference.Parameters.Count;
                        for (int i = 0; i < argc; i++)
                        {
                            SimPop(stack, what, ci);
                        }

                        stack.Add(SimInt(allowedCallValue));
                        break;
                    }

                    case Code.Newobj:
                    {
                        MethodReference reference = ci.Operand as MethodReference;
                        string typeName = reference == null || reference.DeclaringType == null
                            ? "<未知>"
                            : reference.DeclaringType.FullName;

                        if (!string.Equals(typeName, InvalidOperationExceptionFullName, StringComparison.Ordinal))
                        {
                            throw new WeaverException(
                                what + " 构造了 " + typeName + "（IL_" + ci.Offset.ToString("x4")
                                + "）：guard 必须抛 " + InvalidOperationExceptionFullName + "，拒绝猜测。");
                        }

                        int argc = reference.Parameters.Count;
                        for (int i = 0; i < argc; i++)
                        {
                            SimPop(stack, what, ci);
                        }

                        stack.Add(SimException(InvalidOperationExceptionFullName));
                        break;
                    }

                    case Code.Throw:
                    {
                        SimValue v = SimPop(stack, what, ci);
                        if (v.Kind != SimKind.Exception)
                        {
                            throw new WeaverException(
                                what + "：IL_" + ci.Offset.ToString("x4") + " 抛出的不是新建的异常对象（实际 "
                                + v.Kind + "），形态无法识别，拒绝猜测。");
                        }

                        SimResult thrown = new SimResult();
                        thrown.Outcome = SimOutcome.Threw;
                        thrown.ExceptionType = v.Text;
                        return thrown;
                    }

                    case Code.Ret:
                    {
                        SimResult done = new SimResult();
                        if (stack.Count == 0)
                        {
                            done.Outcome = SimOutcome.ReturnedVoid;
                            return done;
                        }

                        if (stack.Count != 1 || stack[0].Kind != SimKind.Int)
                        {
                            throw new WeaverException(
                                what + "：ret 时栈上不是一个整型值（深度 " + stack.Count + "），形态无法识别，拒绝猜测。");
                        }

                        done.Outcome = SimOutcome.ReturnedInt;
                        done.IntValue = stack[0].Int;
                        return done;
                    }

                    default:
                        throw new WeaverException(
                            what + " 含求值器不支持的指令 " + ci.OpCode + "（IL_" + ci.Offset.ToString("x4")
                            + "）：拒绝猜测（只支持冻结格式实际产出的指令形态）。");
                }

                pc = next;
            }
        }

        private static string DescribeCall(MethodReference reference)
        {
            if (reference == null)
            {
                return "<null>";
            }

            string owner = reference.DeclaringType == null ? "<未知类型>" : reference.DeclaringType.FullName;
            return owner + "::" + reference.Name + "，参数 " + reference.Parameters.Count + " 个";
        }

        // =================================================================================
        //  通用工具
        // =================================================================================

        private static string Loc(MethodDefinition method)
        {
            return method.DeclaringType.FullName + "." + method.Name;
        }

        private static MethodDefinition FindMethod(TypeDefinition type, string name)
        {
            for (int i = 0; i < type.Methods.Count; i++)
            {
                if (string.Equals(type.Methods[i].Name, name, StringComparison.Ordinal))
                {
                    return type.Methods[i];
                }
            }

            return null;
        }

        private static List<string> FindRpcMarkers(MethodDefinition method)
        {
            List<string> hits = new List<string>();
            if (!method.HasCustomAttributes)
            {
                return hits;
            }

            for (int i = 0; i < method.CustomAttributes.Count; i++)
            {
                TypeReference attributeType = method.CustomAttributes[i].AttributeType;
                if (attributeType == null)
                {
                    continue;
                }

                string fullName = attributeType.FullName;
                for (int k = 0; k < MarkerAttributeFullNames.Length; k++)
                {
                    if (string.Equals(fullName, MarkerAttributeFullNames[k], StringComparison.Ordinal))
                    {
                        hits.Add(fullName);
                        break;
                    }
                }
            }

            return hits;
        }

        private static bool HasAttribute(ICustomAttributeProvider provider, string fullName)
        {
            if (provider == null || !provider.HasCustomAttributes)
            {
                return false;
            }

            for (int i = 0; i < provider.CustomAttributes.Count; i++)
            {
                TypeReference attributeType = provider.CustomAttributes[i].AttributeType;
                if (attributeType != null && string.Equals(attributeType.FullName, fullName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>参数类型逐一比对（全名 + 个数），用于 helper / 业务体签名一致性。</summary>
        private static void RequireSameParameterTypes(MethodDefinition reference, MethodDefinition candidate, string what)
        {
            if (candidate.Parameters.Count != reference.Parameters.Count)
            {
                throw new WeaverException(
                    what + " " + Loc(candidate) + " 的参数个数与 " + Loc(reference) + " 不一致（"
                    + candidate.Parameters.Count + " vs " + reference.Parameters.Count + "）。");
            }

            for (int i = 0; i < reference.Parameters.Count; i++)
            {
                string a = reference.Parameters[i].ParameterType == null
                    ? null
                    : reference.Parameters[i].ParameterType.FullName;
                string b = candidate.Parameters[i].ParameterType == null
                    ? null
                    : candidate.Parameters[i].ParameterType.FullName;
                if (!string.Equals(a, b, StringComparison.Ordinal))
                {
                    throw new WeaverException(
                        what + " " + Loc(candidate) + " 的第 " + i + " 个参数类型（" + b
                        + "）与 " + Loc(reference) + " 的（" + a + "）不一致。");
                }
            }
        }

        /// <summary>
        /// 统计某个方法体里直接 call/callvirt 目标方法的次数。
        /// 刻意**不使用** Resolve()：同一模块内的引用按「名字 + 声明类型 + 参数类型」比对即可，
        /// 避免解析器异常变成"没找到=0 次"的静默结论。
        /// </summary>
        private static int CountCallsTo(MethodDefinition owner, MethodDefinition target)
        {
            if (owner == null || !owner.HasBody || target == null)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < owner.Body.Instructions.Count; i++)
            {
                Instruction ins = owner.Body.Instructions[i];
                if (ins.OpCode.Code != Code.Call && ins.OpCode.Code != Code.Callvirt)
                {
                    continue;
                }

                if (Targets(ins.Operand as MethodReference, target))
                {
                    count++;
                }
            }

            return count;
        }

        private static bool Targets(MethodReference reference, MethodDefinition target)
        {
            if (reference == null || target == null)
            {
                return false;
            }

            if (!string.Equals(reference.Name, target.Name, StringComparison.Ordinal))
            {
                return false;
            }

            TypeReference declaring = reference.DeclaringType;
            if (declaring == null || !string.Equals(declaring.FullName, target.DeclaringType.FullName, StringComparison.Ordinal))
            {
                return false;
            }

            if (reference.Parameters.Count != target.Parameters.Count)
            {
                return false;
            }

            for (int i = 0; i < target.Parameters.Count; i++)
            {
                string a = target.Parameters[i].ParameterType == null ? null : target.Parameters[i].ParameterType.FullName;
                string b = reference.Parameters[i].ParameterType == null ? null : reference.Parameters[i].ParameterType.FullName;
                if (!string.Equals(a, b, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 读出一条「加载第 N 个 IL 参数」的指令（0 = this）。
        /// wrapper 结构校验用：只看形态，不看调试信息。
        ///
        /// 注意 Cecil 的 `ParameterDefinition.Index` 是「参数集合内下标（不含 this）」：
        /// 实例方法的第 0 个参数 Index=0，而它的 **IL 参数号是 1**；隐式 this 的 Index=-1。
        /// </summary>
        private static bool TryGetLoadedArgumentIndex(Instruction instruction, MethodDefinition owner, out int index)
        {
            switch (instruction.OpCode.Code)
            {
                case Code.Ldarg_0:
                    index = 0;
                    return true;
                case Code.Ldarg_1:
                    index = 1;
                    return true;
                case Code.Ldarg_2:
                    index = 2;
                    return true;
                case Code.Ldarg_3:
                    index = 3;
                    return true;
                case Code.Ldarg_S:
                case Code.Ldarg:
                {
                    ParameterDefinition parameter = instruction.Operand as ParameterDefinition;
                    if (parameter == null)
                    {
                        index = -1;
                        return false;
                    }

                    // Index < 0 表示隐式 this。
                    if (parameter.Index < 0)
                    {
                        index = 0;
                        return true;
                    }

                    index = parameter.Index + (owner.HasThis ? 1 : 0);
                    return true;
                }
                default:
                    index = -1;
                    return false;
            }
        }

        /// <summary>
        /// 用**独立读取器**（System.Reflection.Metadata，net8.0 内置）校验刚写出的 PDB：
        ///
        /// 1. 每个方法的 sequence point / 局部作用域记录都必须能解析；
        /// 2. PDB 不得出现 DLL 里不存在的方法行（否则两者不对应）；
        /// 3. 每个探针方法都要在**写出的 DLL 元数据里唯一定位出精确 RID**
        ///    （同名同参数个数 ⇒ 不唯一就明确失败，不靠「名字后缀」猜），
        ///    再按该 RID 取 sequence point 条数并与期望比较；
        /// 4. --check 模式额外核对：wrapper 无残留行号、克隆业务体在 PDB 里有对应行。
        ///
        /// 与 P1/P2 实现的差别（P2 已报的 BLOCKER）：旧实现把「声明类型名.方法名」当字典键，
        /// 同类型里的**合法重载**（两个 .ctor、同名不同签名的方法）键相同、SP 条数不同，
        /// 于是被误判成「PDB 损坏」。PMNet 运行时与业务编进同一程序集（Unity/Server 真实形态）时
        /// 天然有 20 处这种重载 ⇒ 所有参与工程都编不了。现在按 RID 核对，重载互不影响。
        ///
        /// 为什么必须用独立读取器：Mono.Cecil 0.11.6 的 portable-PDB 写入器在
        /// “把某方法的 sequence point 集合换成另一组非空集合”时会写出损坏的 SP blob
        /// （最小实验已隔离：Clear→0 条有效、原地改值且条数不变有效、Clear 后换新集合损坏）。
        /// 本工具因此只做“清空/原地搬移”，并用独立读取器把任何残留损坏变成**写入前的明确失败**。
        /// </summary>
        private static void VerifyPdb(string dllPath, List<PdbProbe> probes, string what)
        {
            string pdbPath = Path.ChangeExtension(dllPath, ".pdb");
            if (!File.Exists(pdbPath))
            {
                throw new WeaverException(what + "：找不到 PDB（" + pdbPath + "）：拒绝（不得静默丢调试信息）。");
            }

            List<DllMethodRow> methods = ReadDllMethodRows(dllPath);
            Dictionary<int, int> counts = ReadPdbSequencePointCounts(pdbPath, methods);

            for (int i = 0; i < probes.Count; i++)
            {
                PdbProbe probe = probes[i];

                // ── 在写出的 DLL 里唯一定位目标方法的 RID（不按名字后缀猜）──
                int foundRid = -1;
                int foundCount = 0;
                for (int m = 0; m < methods.Count; m++)
                {
                    DllMethodRow row = methods[m];
                    if (!string.Equals(row.MethodName, probe.MethodName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (row.ParameterCount != probe.ParameterCount)
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(probe.TypeFullName)
                        && !string.Equals(row.TypeFullName, probe.TypeFullName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foundRid = row.Rid;
                    foundCount++;
                }

                if (foundCount == 0)
                {
                    throw new WeaverException(
                        what + "：写出的 DLL 里找不到方法 " + probe.MethodName + "（参数 " + probe.ParameterCount
                        + " 个）：无法完成 PDB 核对。");
                }

                if (foundCount > 1)
                {
                    throw new WeaverException(
                        what + "：写出的 DLL 里有 " + foundCount + " 个同名同参数个数的方法 "
                        + probe.MethodName + "：无法唯一定位 RID，拒绝猜测。");
                }

                int spCount;
                if (!counts.TryGetValue(foundRid, out spCount))
                {
                    throw new WeaverException(
                        what + "：写出的 PDB 里没有方法 " + probe.MethodName + "（RID " + foundRid
                        + "）的调试信息行：DLL 与 PDB 不对应，拒绝提交。");
                }

                if (probe.ExpectedSequencePoints >= 0 && spCount != probe.ExpectedSequencePoints)
                {
                    throw new WeaverException(
                        what + "：方法 " + probe.MethodName + " 的 sequence point 条数为 " + spCount
                        + "（期望 " + probe.ExpectedSequencePoints
                        + "）：行号映射未完整搬移 / DLL 与 PDB 不对应，拒绝提交。");
                }
            }
        }

        /// <summary>
        /// --check 的符号核对（只读）：旧实现的 --check 完全不看符号，
        /// 于是「已编织但 PDB 是旧的/损坏的」也能报绿。这里断言：
        ///   · PDB 能独立解析，且与 DLL 的方法行对应（见 VerifyPdb）；
        ///   · wrapper M 在 PDB 里**没有**残留行号（编织器把合成转发桩的 SP 清空；
        ///     若是旧 PDB，wrapper 上会留着编织前的行号 ⇒ 立即暴露「DLL 已织、PDB 还是旧的」）；
        ///   · 每个克隆业务体 PMNet_RpcBody_&lt;M&gt; 在 PDB 里都有对应的调试信息行。
        /// 没有 PDB（无符号构建）时按契约允许，跳过并明确记录。
        /// </summary>
        private static void VerifyWovenSymbols(string fullPath, bool pdbOnDisk, List<ClassBucket> buckets, WeaveReport report)
        {
            if (!pdbOnDisk)
            {
                report.Note("--check：输入没有 PDB（无符号可工作），跳过符号核对。");
                return;
            }

            List<PdbProbe> probes = new List<PdbProbe>();
            for (int i = 0; i < buckets.Count; i++)
            {
                ClassBucket bucket = buckets[i];
                for (int k = 0; k < bucket.Methods.Count; k++)
                {
                    MethodDefinition m = bucket.Methods[k];

                    PdbProbe wrapper = new PdbProbe();
                    wrapper.TypeFullName = bucket.Type.FullName;
                    wrapper.MethodName = m.Name;
                    wrapper.ParameterCount = m.Parameters.Count;
                    wrapper.ExpectedSequencePoints = 0;
                    probes.Add(wrapper);

                    PdbProbe body = new PdbProbe();
                    body.TypeFullName = bucket.Type.FullName;
                    body.MethodName = BodyPrefix + m.Name;
                    body.ParameterCount = m.Parameters.Count;
                    body.ExpectedSequencePoints = -1;
                    probes.Add(body);
                }

                // ★ auto-property：被改写的 setter 与 RawSet 都必须没有残留行号（旧 PDB 会立刻暴露）。
                if (bucket.PropertyPlans != null && bucket.PropertyPlans.Length > 0)
                {
                    PropertyWeaver.AddCheckProbes(bucket.PropertyPlans, probes);
                }
            }

            VerifyPdb(fullPath, probes, "--check：PDB 与 DLL 必须逐方法对应");
            report.Note("--check：符号核对通过（每对 wrapper/业务体与属性 setter/RawSet 都能按 RID 在 PDB 里找到，"
                        + "被改写的方法无残留行号，共核对 " + probes.Count + " 个方法）。");
        }

        private static string ReadTypeFullName(SrmMetadataReader reader, System.Reflection.Metadata.TypeDefinitionHandle handle)
        {
            List<string> names = new List<string>();
            HashSet<int> seen = new HashSet<int>();
            while (!handle.IsNil)
            {
                if (!seen.Add(SrmMetadataTokens.GetRowNumber(handle))) throw new WeaverException("类型嵌套存在环");
                System.Reflection.Metadata.TypeDefinition type = reader.GetTypeDefinition(handle);
                names.Insert(0, reader.GetString(type.Name));
                System.Reflection.Metadata.TypeDefinitionHandle parent = type.GetDeclaringType();
                if (parent.IsNil)
                {
                    string ns = reader.GetString(type.Namespace);
                    return (string.IsNullOrEmpty(ns) ? "" : ns + ".") + string.Join("/", names);
                }
                handle = parent;
            }
            throw new WeaverException("方法缺少声明类型");
        }

        private static List<DllMethodRow> ReadDllMethodRows(string dllPath)
        {
            List<DllMethodRow> rows = new List<DllMethodRow>();
            using (FileStream peStream = File.OpenRead(dllPath))
            {
                using (SrmPEReader pe = new SrmPEReader(peStream))
                {
                    SrmMetadataReader md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
                    foreach (SrmMethodDefinitionHandle handle in md.MethodDefinitions)
                    {
                        System.Reflection.Metadata.MethodDefinition definition = md.GetMethodDefinition(handle);
                        DllMethodRow row = new DllMethodRow();
                        row.Rid = SrmMetadataTokens.GetRowNumber(handle);
                        row.MethodName = md.GetString(definition.Name);
                        row.ParameterCount = 0;
                        foreach (System.Reflection.Metadata.ParameterHandle parameter in definition.GetParameters())
                            if (md.GetParameter(parameter).SequenceNumber != 0) row.ParameterCount++;
                        row.TypeSimpleName = "?";
                        row.TypeFullName = "?";

                        System.Reflection.Metadata.TypeDefinitionHandle typeHandle = definition.GetDeclaringType();
                        if (!typeHandle.IsNil)
                        {
                            System.Reflection.Metadata.TypeDefinition typeDefinition = md.GetTypeDefinition(typeHandle);
                            string ns = md.GetString(typeDefinition.Namespace);
                            string name = md.GetString(typeDefinition.Name);
                            row.TypeSimpleName = name;
                            row.TypeFullName = ReadTypeFullName(md, typeHandle);
                        }

                        rows.Add(row);
                    }
                }
            }

            return rows;
        }

        private static Dictionary<int, int> ReadPdbSequencePointCounts(string pdbPath, List<DllMethodRow> methods)
        {
            Dictionary<int, string> labels = new Dictionary<int, string>();
            for (int i = 0; i < methods.Count; i++)
            {
                labels[methods[i].Rid] = methods[i].TypeFullName + "." + methods[i].MethodName;
            }

            Dictionary<int, int> counts = new Dictionary<int, int>();
            using (FileStream pdbStream = File.OpenRead(pdbPath))
            {
                using (SrmMetadataReaderProvider provider = SrmMetadataReaderProvider.FromPortablePdbStream(pdbStream))
                {
                    SrmMetadataReader pdb = provider.GetMetadataReader();
                    foreach (SrmMethodDebugInformationHandle handle in pdb.MethodDebugInformation)
                    {
                        int rid = SrmMetadataTokens.GetRowNumber(handle);
                        string name;
                        if (!labels.TryGetValue(rid, out name))
                        {
                            throw new WeaverException(
                                "PDB 里存在 DLL 中不存在的调试信息行（RID " + rid
                                + "）：DLL 与 PDB 不对应，无法完成完整性核对，拒绝提交。");
                        }

                        System.Reflection.Metadata.MethodDebugInformation info = pdb.GetMethodDebugInformation(handle);

                        int count = 0;
                        try
                        {
                            foreach (System.Reflection.Metadata.SequencePoint point in info.GetSequencePoints())
                            {
                                count++;
                            }
                        }
                        catch (Exception ex)
                        {
                            throw new WeaverException(
                                "PDB 在方法 " + name + " 上的 sequence point 记录无法解析（" + ex.GetType().Name
                                + "：" + ex.Message + "）：拒绝提交损坏的 PDB。这是 Mono.Cecil 符号写入路径的缺陷，"
                                + "必须报出来而不是静默写出去。");
                        }

                        try
                        {
                            foreach (System.Reflection.Metadata.LocalScopeHandle scopeHandle in pdb.GetLocalScopes(handle))
                            {
                                System.Reflection.Metadata.LocalScope scope = pdb.GetLocalScope(scopeHandle);
                                foreach (System.Reflection.Metadata.LocalVariableHandle variableHandle in scope.GetLocalVariables())
                                {
                                    System.Reflection.Metadata.LocalVariable variable = pdb.GetLocalVariable(variableHandle);
                                    string unusedName = pdb.GetString(variable.Name);
                                    if (unusedName == null)
                                    {
                                        throw new WeaverException("局部变量名不可读。");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            throw new WeaverException(
                                "PDB 在方法 " + name + " 上的局部作用域/局部变量无法解析（" + ex.GetType().Name
                                + "：" + ex.Message + "）：拒绝提交损坏的 PDB。");
                        }

                        counts[rid] = count;
                    }
                }
            }

            return counts;
        }

        /// <summary>
        /// PDB 核对探针。
        ///
        /// 为什么不是「声明类型名.方法名」字典键：同名重载会撞键（P2 的 BLOCKER）。
        /// 这里用（方法名 + 参数个数 [+ 声明类型简单名]）在**写出的 DLL 元数据里唯一定位 RID**，
        /// 再按 RID 取该方法的 sequence point 条数 —— 每条 RID 恰好一行，重载互不干扰。
        /// </summary>
        internal sealed class PdbProbe
        {
            /// <summary>完整类型名（含命名空间和嵌套类型）；避免不同命名空间同名类互撞。</summary>
            public string TypeFullName;

            /// <summary>方法名（wrapper M 或克隆业务体 PMNet_RpcBody_&lt;M&gt;）。</summary>
            public string MethodName;

            /// <summary>参数个数（与名字一起唯一定位 RID）。</summary>
            public int ParameterCount;

            /// <summary>&gt;= 0：精确核对 sequence point 条数；-1：只要求 PDB 里有该方法的行。</summary>
            public int ExpectedSequencePoints;
        }

        private sealed class DllMethodRow
        {
            public int Rid;
            public string TypeSimpleName;
            public string TypeFullName;
            public string MethodName;
            public int ParameterCount;
        }

        private sealed class ClassBucket
        {
            public TypeDefinition Type;
            public List<MethodDefinition> Methods;
            public int Version;

            /// <summary>本类的 [PMReplicated] auto-property（字段旧模式不在此列）。</summary>
            public List<PropertyDefinition> Properties;

            /// <summary>本类 auto-property 的预检结果（含成员定位 / 槽位 / Push 语义）。</summary>
            public PropertyPlan[] PropertyPlans;
        }
    }
}
