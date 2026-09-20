using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PMNet;
using PMNet.Codegen;

namespace PMNetGen
{
    // =========================================================================================
    //  R2 声明扫描器（契约：Docs/plans/net-r2-codegen-contract.md）
    //
    //  职责边界（刻意划得很窄）：
    //    本文件**只负责"发现"**：把源码里的 [PMNetworkObject] / [PMReplicated] / [PMRepNotify]
    //    / [PMQuantized] / [PMServerRpc] / [PMClientRpc] / [PMNetMulticast] 声明读成
    //    PMDeclModel，并完成**与声明顺序无关**的 ID / 掩码区间 / 协议摘要赋值。
    //    契约 §5 的 12 条报错规则**全部**在 DeclValidation.cs 里，这里一条都不做——
    //    否则"某条规则在哪一层生效"会退化成两个文件里各有一半的模糊状态。
    //
    //  为什么用 Roslyn 而不是正则：
    //    属性可以写在基类列表前后、可以有命名参数、条件可以写成 `(PMCond)1`；
    //    更关键的是"重排不变"要求拿到**结构**而不是文本位置。
    //  为什么只做语法级（不建 Compilation、不引业务程序集）：
    //    生成期只需要"谁被标记了、成员叫什么、类型怎么写"。建 Compilation 会把整个业务的
    //    编译依赖拖进生成器，而生成器必须在业务编译成功之前就能跑。
    // =========================================================================================

    /// <summary>一次声明扫描的结果：冻结 IR + IR 之外的补充事实。</summary>
    public sealed class PMDeclScanResult
    {
        /// <summary>冻结 IR（<see cref="PMDeclModel"/>）。ID、掩码区间、协议摘要均已填充。</summary>
        public PMDeclModel Model;

        /// <summary>
        /// 补充事实。
        ///
        /// **为什么不能只靠 IR**：
        /// <list type="bullet">
        ///   <item>规则 9（`WithValidation` 与 `Validator=ForceValidate` 不得同时出现）判的是
        ///         "源码里显式写了什么"，而 IR 只保留"生效后的档位"；</item>
        ///   <item>规则 1（能否推到 PMNetObject）需要"全部类型（含**未被标记**的中间基类）"的继承图，
        ///         而 IR 只收录被标记的类；</item>
        ///   <item>IR 里没有"是否泛型""是否有可用无参构造""源文件的 using 列表"这类生成期需要的信息。</item>
        /// </list>
        /// 因此把只在语法层可见的事实单独带出来，而不是去改**已冻结**的 IR（见 R2 契约 §1）。
        /// </summary>
        public PMDeclRawFacts Facts;
    }

    /// <summary>IR 之外的语法层事实（详见 <see cref="PMDeclScanResult.Facts"/> 的注释）。</summary>
    public sealed class PMDeclRawFacts
    {
        /// <summary>全部顶层类型（含未标记的）限定名 → 直接基类型书写名（无基类 = 空串）。</summary>
        public readonly Dictionary<string, string> DirectBaseTypes =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>简单类型名 → 全部同名限定名（用于把 `Foo` 解析成 `Ns.Foo`）。</summary>
        public readonly Dictionary<string, List<string>> TypeSimpleNames =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        /// <summary>全部 enum 的限定名与简单名（类型集判定用，见契约 §6）。</summary>
        public readonly HashSet<string> EnumTypeNames = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>全部被 [PMNetworkStruct] 标记的类型的限定名与简单名。R2 不实现，声明即报错。</summary>
        public readonly HashSet<string> NetworkStructTypeNames = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>泛型顶层类型的限定名（规则 2）。</summary>
        public readonly HashSet<string> GenericTypes = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>声明了构造函数但没有可用无参构造的**网络类**（生成构造工厂会编译不过）。</summary>
        public readonly HashSet<string> TypesWithoutUsableCtor = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>抽象的**网络类**（无法 `new`）。</summary>
        public readonly HashSet<string> AbstractTypes = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>已自行覆写 CollectLifetimeReplicatedProps 的类型（生成代码会与之冲突）。</summary>
        public readonly HashSet<string> TypesWithOwnRepListOverride = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>把 [PMNetworkObject] 放到 class 之外（struct / interface / record）的类型（规则 1）。</summary>
        public readonly List<string> NonClassNetworkObjectHosts = new List<string>();

        /// <summary>类型的修饰符前缀（如 `public sealed `），用于生成 partial 声明时保持可访问性一致。</summary>
        public readonly Dictionary<string, string> TypeModifierPrefix =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>类型所在源文件的 using 列表（生成文件必须复制，否则类型名解析不到）。</summary>
        public readonly Dictionary<string, List<string>> UsingsByType =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        /// <summary>[PMRepNotify] 的声明事实（规则 5 / 6）。</summary>
        public readonly List<PMDeclRepNotifyFact> RepNotifies = new List<PMDeclRepNotifyFact>();

        /// <summary>RPC 的声明事实（规则 8 / 9）。</summary>
        public readonly List<PMDeclRpcFact> Rpcs = new List<PMDeclRpcFact>();

        /// <summary>
        /// 复制成员的**条件源码文本**（键 = `类限定名.成员名`）。
        /// 缺键 = 未写条件（等价 PMCond.None）；有键但解析不出来 = 规则 11 报错。
        /// </summary>
        public readonly Dictionary<string, string> ConditionSources =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>复制属性没有 set 访问器（生成写入器会编译不过）——生成期告警，不阻断。</summary>
        public readonly List<string> ReplicatedWithoutSetter = new List<string>();

        /// <summary>声明了 RPC 属性但所在类不是 [PMNetworkObject]（规则 7 的附带检查）。</summary>
        public readonly List<string> OrphanRpcDeclarations = new List<string>();

        /// <summary>声明了 [PMReplicated] 但所在类不是 [PMNetworkObject]（规则 4 的附带检查）。</summary>
        public readonly List<string> OrphanReplicatedDeclarations = new List<string>();

        /// <summary>
        /// 类限定名 → 复制属性序号基址（继承链上所有已声明祖先的复制属性总数）。
        ///
        /// 为什么要它：`PMRepList.Add(propertyIndex, ...)` 的 index 是**每对象连续**的，
        /// `MarkPropertyDirty(propertyIndex)` 也用它。派生类若从 0 开始编号，
        /// `PMRepList.TryGet` 会命中基类的同号条目——那是"派生类属性写到了基类槽位"的静默错位。
        /// </summary>
        public readonly Dictionary<string, int> PropertyIndexBase =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>实际解析的 .cs 文件数。</summary>
        public int ParsedFileCount;

        /// <summary>Roslyn 报出的语法错误条数（只统计，不阻断；声明扫描不需要源码能编译）。</summary>
        public int SyntaxErrorCount;
    }

    /// <summary>一条 [PMRepNotify] 声明（规则 5 / 6 的判据）。</summary>
    public sealed class PMDeclRepNotifyFact
    {
        /// <summary>所在类限定名。</summary>
        public string ClassQualifiedName;

        /// <summary>被标记的方法名。</summary>
        public string MethodName;

        /// <summary>ForMember 实参（字符串字面量原值）。</summary>
        public string ForMember;

        /// <summary>形参个数。</summary>
        public int ParameterCount;

        /// <summary>返回类型是否为 void。</summary>
        public bool ReturnsVoid;

        /// <summary>声明所在行（诊断用）。</summary>
        public int Line;
    }

    /// <summary>一条 RPC 声明（规则 8 / 9 的判据）。</summary>
    public sealed class PMDeclRpcFact
    {
        /// <summary>所在类限定名。</summary>
        public string ClassQualifiedName;

        /// <summary>方法名。</summary>
        public string MethodName;

        /// <summary>是否显式写了 `WithValidation`（只有 PMServerRpc 有这个字段）。</summary>
        public bool WithValidationExplicit;

        /// <summary>显式写下的 Validator 档位（未写 = None）。</summary>
        public PMRpcValidator ValidatorExplicit;

        /// <summary>是否存在 `&lt;方法名&gt;_ForceValidate` 同伴（规则 8 的第二个放行条件）。</summary>
        public bool HasForceValidateCompanion;

        /// <summary>是否存在 `&lt;方法名&gt;_Validate` 同伴（不构成规则 8 的放行条件，仅用于告警）。</summary>
        public bool HasValidateCompanion;

        /// <summary>声明所在行（诊断用）。</summary>
        public int Line;
    }

    /// <summary>
    /// `PMCond` 的书写名 ↔ 数值映射（契约 §5 规则 11；§2.2 要求"未实现的条件直接报错，不静默降级"）。
    ///
    /// 放在扫描器文件里是为了让扫描器（决定 IR 里的 Condition 取值）与校验器（报规则 11）
    /// 用的是**同一张表**——否则"扫描器认了、校验器不认"会变成一个自相矛盾的失败。
    /// </summary>
    public static class PMCondNames
    {
        private static readonly PMCond[] Implemented =
        {
            PMCond.None,
            PMCond.OwnerOnly,
            PMCond.SkipOwner,
            PMCond.SimulatedOnly,
            PMCond.AutonomousOnly,
            PMCond.Custom,
            PMCond.Dynamic,
            PMCond.Never,
        };

        /// <summary>明确不做、且必须在声明期报错的 9 项（UE 其余 ELifetimeCondition 值）。</summary>
        private static readonly Dictionary<string, int> Unimplemented = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "InitialOnly", 1 },
            { "SimulatedOrPhysics", 6 },
            { "InitialOrOwner", 7 },
            { "ReplayOrOwner", 9 },
            { "ReplayOnly", 10 },
            { "SimulatedOnlyNoReplay", 11 },
            { "SimulatedOrPhysicsNoReplay", 12 },
            { "SkipReplay", 13 },
            { "NetGroup", 16 },
        };

        /// <summary>首版实现的 8 项书写名（有序，便于打印）。</summary>
        public static string[] ImplementedNames()
        {
            string[] names = new string[Implemented.Length];
            for (int i = 0; i < Implemented.Length; i++)
            {
                names[i] = Implemented[i].ToString();
            }

            return names;
        }

        /// <summary>按数值解析（覆盖 `(PMCond)1` 这类写法）。</summary>
        public static bool TryResolveValue(int value, out PMCond cond)
        {
            for (int i = 0; i < Implemented.Length; i++)
            {
                if ((int)Implemented[i] == value)
                {
                    cond = Implemented[i];
                    return true;
                }
            }

            cond = PMCond.None;
            return false;
        }

        /// <summary>
        /// 解析源码里写的条件表达式文本。
        /// 接受：`PMCond.OwnerOnly` / `OwnerOnly` / `(PMCond)3` / `3`。
        /// <paramref name="sourceText"/> 为空/为 null 表示"未写"，等价于 <see cref="PMCond.None"/> 且**合法**。
        /// </summary>
        public static bool TryResolve(string sourceText, out PMCond cond)
        {
            cond = PMCond.None;
            if (string.IsNullOrEmpty(sourceText))
            {
                return true;
            }

            string text = Clean(sourceText);

            int numeric;
            if (int.TryParse(text, out numeric))
            {
                return TryResolveValue(numeric, out cond);
            }

            text = TrimName(text);
            for (int i = 0; i < Implemented.Length; i++)
            {
                if (string.Equals(Implemented[i].ToString(), text, StringComparison.Ordinal))
                {
                    cond = Implemented[i];
                    return true;
                }
            }

            return false;
        }

        /// <summary>该书写名是否属于"明确不做的 9 项"（用于给出更准确的报错文本）。</summary>
        public static bool IsUnimplementedName(string sourceText, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(sourceText))
            {
                return false;
            }

            string text = Clean(sourceText);
            if (int.TryParse(text, out value))
            {
                // 数值形式的未实现条件（如 `(PMCond)1`）也应按"未实现"报错。
                for (int i = 0; i < Implemented.Length; i++)
                {
                    if ((int)Implemented[i] == value)
                    {
                        return false;
                    }
                }

                return true;
            }

            return Unimplemented.TryGetValue(TrimName(text), out value);
        }

        /// <summary>去掉空白与 `(PMCond)` 这类强制转换前缀。</summary>
        private static string Clean(string text)
        {
            string t = Strip(text);
            if (t.StartsWith("(", StringComparison.Ordinal))
            {
                int close = t.IndexOf(')');
                if (close >= 0)
                {
                    t = t.Substring(close + 1);
                }
            }

            return t;
        }

        private static string TrimName(string text)
        {
            string t = text.Replace("(", string.Empty).Replace(")", string.Empty);
            int dot = t.LastIndexOf('.');
            if (dot >= 0)
            {
                t = t.Substring(dot + 1);
            }

            return t;
        }

        private static string Strip(string text)
        {
            StringBuilder sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (!char.IsWhiteSpace(text[i]))
                {
                    sb.Append(text[i]);
                }
            }

            return sb.ToString();
        }
    }

    /// <summary>锁文件里出现过的键（用于"键退役只告警、ID 永不回收"的检测）。</summary>
    public sealed class PMDeclLockKeys
    {
        /// <summary>类稳定键。</summary>
        public readonly List<string> ClassKeys = new List<string>();

        /// <summary>类稳定键 → 成员稳定键列表（不一定完整，只用于诊断打印）。</summary>
        public readonly Dictionary<string, List<string>> MemberKeys =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);
    }

    /// <summary>声明扫描器：源码路径 → <see cref="PMDeclModel"/>。</summary>
    public static class PMDeclScanner
    {
        private const string AttrNetworkObject = "PMNetworkObject";
        private const string AttrNetworkStruct = "PMNetworkStruct";
        private const string AttrReplicated = "PMReplicated";
        private const string AttrRepNotify = "PMRepNotify";
        private const string AttrQuantized = "PMQuantized";
        private const string AttrServerRpc = "PMServerRpc";
        private const string AttrClientRpc = "PMClientRpc";
        private const string AttrNetMulticast = "PMNetMulticast";

        // ------------------------------------------------------------------------------ 入口

        /// <summary>
        /// 扫描给定路径（目录递归取 `*.cs`；也可以直接给单个 `.cs` 文件）并产出 IR。
        /// </summary>
        /// <param name="paths">源目录或源文件。</param>
        /// <param name="idLock">ID 权威来源（会**就地分配**新键）；为 null 时用全新锁。</param>
        /// <param name="label">诊断用的输入描述（如命令行原文）。</param>
        public static PMDeclScanResult Scan(IList<string> paths, PMIdLock idLock, string label)
        {
            if (paths == null || paths.Count == 0)
            {
                throw new ArgumentException("必须至少给出一个源路径");
            }

            if (idLock == null)
            {
                idLock = new PMIdLock();
            }

            PMDeclScanResult result = new PMDeclScanResult();
            result.Model = new PMDeclModel();
            result.Facts = new PMDeclRawFacts();

            PMDeclRawFacts facts = result.Facts;
            PMDeclModel model = result.Model;

            List<string> files = CollectFiles(paths);
            facts.ParsedFileCount = files.Count;

            if (files.Count == 0)
            {
                model.Warnings.Add("[告警] 没有找到任何 .cs 源文件（输入："
                    + (label ?? string.Join(", ", ToArray(paths))) + "）");
            }

            CSharpParseOptions options = new CSharpParseOptions(LanguageVersion.Latest);
            Dictionary<string, ClassAccumulator> accs = new Dictionary<string, ClassAccumulator>(StringComparer.Ordinal);

            for (int fi = 0; fi < files.Count; fi++)
            {
                string path = files[fi];
                string text;
                try
                {
                    // 本仓库存在 GBK 源文件的历史遗留（Tools/fix_gbk_sources.py）。非 UTF-8 文件里的
                    // 非 ASCII 字符会被替换成 U+FFFD，但属性名/类型名/成员名都是 ASCII，
                    // 声明扫描不受影响。这一条记在报告里，不做静默假设。
                    text = File.ReadAllText(path);
                }
                catch (Exception ex)
                {
                    model.Warnings.Add("[告警] 读取源文件失败，已跳过：" + path + "（" + ex.Message + "）");
                    continue;
                }

                SyntaxTree tree = CSharpSyntaxTree.ParseText(text, options, path);
                SyntaxNode root = tree.GetRoot();

                foreach (Diagnostic d in tree.GetDiagnostics())
                {
                    if (d.Severity == DiagnosticSeverity.Error)
                    {
                        facts.SyntaxErrorCount++;
                    }
                }

                CollectTypes(root, path, CollectUsings(root), accs, facts);
            }

            // 直接基类以"累积器里各 partial 部分里唯一出现的那条基类列表"为准，
            // 避免"第一部分带基类、第二部分不带"时被后写的空串覆盖。
            foreach (KeyValuePair<string, ClassAccumulator> kv in accs)
            {
                facts.DirectBaseTypes[kv.Key] = kv.Value.BaseTypeName;
                AddSimpleName(facts, kv.Value.SimpleName, kv.Key);
            }

            // ---- 组装 IR：只收录 class 形态的 [PMNetworkObject]；稳定键冲突按规则 10 报错 ----
            List<ClassAccumulator> networkAccs = new List<ClassAccumulator>();
            foreach (KeyValuePair<string, ClassAccumulator> kv in accs)
            {
                ClassAccumulator acc = kv.Value;
                if (!acc.IsNetworkObject)
                {
                    if (acc.HasRpcAttributes)
                    {
                        for (int i = 0; i < acc.Rpcs.Count; i++)
                        {
                            facts.OrphanRpcDeclarations.Add(acc.QualifiedName + "." + acc.Rpcs[i].MethodName);
                        }
                    }

                    if (acc.HasReplicatedMembers)
                    {
                        for (int i = 0; i < acc.Properties.Count; i++)
                        {
                            if (acc.Properties[i].HasReplicated)
                            {
                                facts.OrphanReplicatedDeclarations.Add(acc.QualifiedName + "." + acc.Properties[i].MemberName);
                            }
                        }
                    }

                    continue;
                }

                if (!acc.IsClass)
                {
                    facts.NonClassNetworkObjectHosts.Add(acc.QualifiedName);
                    continue;
                }

                networkAccs.Add(acc);
            }

            networkAccs.Sort(delegate (ClassAccumulator a, ClassAccumulator b)
            {
                int byKey = string.CompareOrdinal(a.EffectiveStableKey, b.EffectiveStableKey);
                return byKey != 0 ? byKey : string.CompareOrdinal(a.QualifiedName, b.QualifiedName);
            });

            List<PMDeclClass> classes = new List<PMDeclClass>();
            Dictionary<string, string> keyOwner = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < networkAccs.Count; i++)
            {
                ClassAccumulator acc = networkAccs[i];
                string owner;
                if (keyOwner.TryGetValue(acc.EffectiveStableKey, out owner))
                {
                    model.Errors.Add("[规则 10] 稳定的类键重复：" + acc.EffectiveStableKey
                        + "（" + owner + " 与 " + acc.QualifiedName
                        + "）。显式 StableKey 必须全局唯一，否则两个类型会争同一个类型 ID。");
                    continue;
                }

                keyOwner.Add(acc.EffectiveStableKey, acc.QualifiedName);
                classes.Add(BuildClass(acc, facts));
            }

            // ---- ID 分配（锁文件是权威；只有新键才走哈希 + 确定性探测）----
            AssignIds(classes, idLock);

            // ---- 收尾：掩码区间、参数布局哈希、类/全局协议摘要 ----
            for (int i = 0; i < classes.Count; i++)
            {
                FinalizeClass(classes[i]);
            }

            ComputePropertyIndexBase(classes, facts);

            classes.Sort(delegate (PMDeclClass a, PMDeclClass b)
            {
                return a.ClassId.CompareTo(b.ClassId);
            });

            // 先把类表挂到 IR 上，再算协议摘要：
            // `BuildClassEntries(model)` 读的就是 `model.Classes`，
            // 顺序反了会算出一个“空类表”的 GlobalProtocolHash（与生成物里的值不一致）。
            model.Classes = classes;
            ComputeProtocolHashes(classes, model);

            return result;
        }

        // ------------------------------------------------------------------------------ 收集（第一遍）

        private sealed class ClassAccumulator
        {
            public string QualifiedName = string.Empty;
            public string Namespace = string.Empty;
            public string SimpleName = string.Empty;
            public string FilePath = string.Empty;
            public int Line;
            public bool IsPartial;
            public bool IsGeneric;
            public bool IsClass;
            public bool IsNetworkObject;
            public bool IsAbstract;
            public bool HasOwnRepListOverride;
            public bool HasExplicitCtor;
            public bool HasParameterlessCtor;
            public string BaseTypeName = string.Empty;
            public string StableKeyOverride;
            public string ModifierPrefix = string.Empty;

            public readonly List<string> Usings = new List<string>();
            public readonly List<PropRecord> Properties = new List<PropRecord>();
            public readonly List<RpcRecord> Rpcs = new List<RpcRecord>();
            public readonly List<PMDeclRepNotifyFact> RepNotifies = new List<PMDeclRepNotifyFact>();
            public readonly List<string> MethodNames = new List<string>();

            public bool HasRpcAttributes { get { return Rpcs.Count > 0; } }

            public bool HasReplicatedMembers
            {
                get
                {
                    for (int i = 0; i < Properties.Count; i++)
                    {
                        if (Properties[i].HasReplicated)
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }

            public string EffectiveStableKey
            {
                get
                {
                    return string.IsNullOrEmpty(StableKeyOverride)
                        ? PMStableHash.ClassKey(Namespace, SimpleName)
                        : StableKeyOverride;
                }
            }
        }

        private sealed class PropRecord
        {
            public string MemberName = string.Empty;
            public string TypeName = string.Empty;
            public bool IsField;
            public bool IsStatic;
            public bool IsReadOnly;
            public bool HasSetter = true;
            public bool HasReplicated;
            public string ConditionSource;
            public ushort QuantizerId;
            public int Line;
        }

        private sealed class RpcRecord
        {
            public string MethodName = string.Empty;
            public PMRpcKind Kind;
            public PMRpcReliability Reliability;
            public PMRpcValidator ValidatorExplicit;
            public bool WithValidationExplicit;
            public bool ReturnsVoid;
            public bool IsStatic;
            public int Line;
            public readonly List<PMDeclParam> Parameters = new List<PMDeclParam>();
        }

        private static void CollectTypes(
            SyntaxNode root,
            string path,
            List<string> usings,
            Dictionary<string, ClassAccumulator> accs,
            PMDeclRawFacts facts)
        {
            foreach (SyntaxNode node in root.DescendantNodes())
            {
                EnumDeclarationSyntax en = node as EnumDeclarationSyntax;
                if (en != null)
                {
                    // enum 只用于类型集判定（契约 §6），不要求限定在顶层。
                    facts.EnumTypeNames.Add(en.Identifier.Text);
                    facts.EnumTypeNames.Add(Qualified(GetNamespace(en), en.Identifier.Text));
                    continue;
                }

                TypeDeclarationSyntax btd = node as TypeDeclarationSyntax;
                if (btd == null || !IsTopLevel(btd))
                {
                    continue;
                }

                string typeNs = GetNamespace(btd);
                string typeSimple = btd.Identifier.Text;
                string qn = Qualified(typeNs, typeSimple);

                ClassAccumulator acc = GetOrAdd(accs, qn);
                acc.QualifiedName = qn;
                acc.Namespace = typeNs;
                acc.SimpleName = typeSimple;
                acc.IsClass |= btd is ClassDeclarationSyntax;
                if (string.IsNullOrEmpty(acc.FilePath))
                {
                    acc.FilePath = path;
                }

                acc.IsPartial |= HasModifier(btd.Modifiers, SyntaxKind.PartialKeyword);
                acc.IsGeneric |= btd.TypeParameterList != null;
                acc.IsAbstract |= HasModifier(btd.Modifiers, SyntaxKind.AbstractKeyword);

                string baseText = FirstBaseTypeText(btd);
                if (!string.IsNullOrEmpty(baseText))
                {
                    acc.BaseTypeName = baseText;
                }

                // 只要任意一个 partial 部分带可访问性修饰符就用它：生成代码复制同一前缀，
                // 避免"一部分 public、另一部分不写"时生成出默认 internal 的冲突（CS0262）。
                if (string.IsNullOrEmpty(acc.ModifierPrefix))
                {
                    acc.ModifierPrefix = ModifierPrefix(btd);
                }

                MergeUsings(acc.Usings, usings);

                bool isNetworkObject = false;
                foreach (AttributeListSyntax attrs in btd.AttributeLists)
                {
                    foreach (AttributeSyntax attr in attrs.Attributes)
                    {
                        string name = SimpleAttributeName(attr);
                        if (name == AttrNetworkObject)
                        {
                            isNetworkObject = true;
                            string key;
                            if (TryGetStringArgument(attr, "StableKey", 0, out key) && !string.IsNullOrEmpty(key))
                            {
                                acc.StableKeyOverride = key;
                            }
                        }
                        else if (name == AttrNetworkStruct)
                        {
                            // [PMNetworkStruct] 是"R2 不实现、声明即报错"的标记（契约 §6），不参与 IR。
                            facts.NetworkStructTypeNames.Add(typeSimple);
                            facts.NetworkStructTypeNames.Add(qn);
                        }
                    }
                }

                if (isNetworkObject)
                {
                    acc.IsNetworkObject = true;
                    if (acc.Line == 0)
                    {
                        acc.Line = LineOf(btd);
                        acc.FilePath = path;
                    }
                }

                CollectMembers(btd, acc, facts, qn);
            }
        }

        private static void CollectMembers(
            TypeDeclarationSyntax btd,
            ClassAccumulator acc,
            PMDeclRawFacts facts,
            string qn)
        {
            foreach (MemberDeclarationSyntax member in btd.Members)
            {
                FieldDeclarationSyntax field = member as FieldDeclarationSyntax;
                if (field != null)
                {
                    foreach (VariableDeclaratorSyntax v in field.Declaration.Variables)
                    {
                        PropRecord rec = CollectPropAttributes(field.AttributeLists, v.Identifier.Text,
                            TypeText(field.Declaration.Type), true, field.Modifiers, LineOf(v));
                        if (rec != null)
                        {
                            acc.Properties.Add(rec);
                        }
                    }

                    continue;
                }

                PropertyDeclarationSyntax prop = member as PropertyDeclarationSyntax;
                if (prop != null)
                {
                    PropRecord rec = CollectPropAttributes(prop.AttributeLists, prop.Identifier.Text,
                        TypeText(prop.Type), false, prop.Modifiers, LineOf(prop));
                    if (rec != null)
                    {
                        rec.HasSetter = HasSetter(prop);
                        if (!rec.HasSetter)
                        {
                            facts.ReplicatedWithoutSetter.Add(qn + "." + prop.Identifier.Text);
                        }

                        acc.Properties.Add(rec);
                    }

                    continue;
                }

                ConstructorDeclarationSyntax ctor = member as ConstructorDeclarationSyntax;
                if (ctor != null)
                {
                    acc.HasExplicitCtor = true;
                    if (ctor.ParameterList.Parameters.Count == 0)
                    {
                        acc.HasParameterlessCtor = true;
                    }

                    continue;
                }

                MethodDeclarationSyntax method = member as MethodDeclarationSyntax;
                if (method == null)
                {
                    continue;
                }

                string methodName = method.Identifier.Text;
                acc.MethodNames.Add(methodName);

                if (methodName == "CollectLifetimeReplicatedProps")
                {
                    acc.HasOwnRepListOverride = true;
                }

                foreach (AttributeListSyntax attrs in method.AttributeLists)
                {
                    foreach (AttributeSyntax attr in attrs.Attributes)
                    {
                        string name = SimpleAttributeName(attr);
                        if (name == AttrRepNotify)
                        {
                            string forMember;
                            TryGetStringArgument(attr, "ForMember", 0, out forMember);
                            PMDeclRepNotifyFact fact = new PMDeclRepNotifyFact();
                            fact.ClassQualifiedName = qn;
                            fact.MethodName = methodName;
                            fact.ForMember = forMember ?? string.Empty;
                            fact.ParameterCount = method.ParameterList.Parameters.Count;
                            fact.ReturnsVoid = TypeText(method.ReturnType) == "void";
                            fact.Line = LineOf(method);
                            acc.RepNotifies.Add(fact);
                            facts.RepNotifies.Add(fact);
                        }
                        else if (name == AttrServerRpc || name == AttrClientRpc || name == AttrNetMulticast)
                        {
                            RpcRecord rec = new RpcRecord();
                            rec.MethodName = methodName;
                            rec.Kind = name == AttrServerRpc ? PMRpcKind.Server
                                : (name == AttrClientRpc ? PMRpcKind.Client : PMRpcKind.Multicast);
                            rec.ReturnsVoid = TypeText(method.ReturnType) == "void";
                            rec.IsStatic = HasModifier(method.Modifiers, SyntaxKind.StaticKeyword);
                            rec.Line = LineOf(method);
                            rec.Reliability = PMRpcReliability.Unreliable;

                            string text;
                            PMRpcReliability rel;
                            if (TryGetNamedArgument(attr, "Reliability", out text)
                                && TryParseReliability(text, out rel))
                            {
                                rec.Reliability = rel;
                            }

                            PMRpcValidator validator;
                            if (TryGetNamedArgument(attr, "Validator", out text)
                                && TryParseValidator(text, out validator))
                            {
                                rec.ValidatorExplicit = validator;
                            }

                            bool withValidation;
                            if (TryGetNamedArgument(attr, "WithValidation", out text)
                                && bool.TryParse(TypeText(text), out withValidation))
                            {
                                rec.WithValidationExplicit = withValidation;
                            }

                            foreach (ParameterSyntax p in method.ParameterList.Parameters)
                            {
                                PMDeclParam param = new PMDeclParam();
                                param.Name = p.Identifier.Text;
                                param.TypeName = p.Type != null ? TypeText(p.Type) : string.Empty;
                                rec.Parameters.Add(param);
                            }

                            acc.Rpcs.Add(rec);
                        }
                    }
                }
            }
        }

        private static PropRecord CollectPropAttributes(
            SyntaxList<AttributeListSyntax> attrs,
            string memberName,
            string typeName,
            bool isField,
            SyntaxTokenList modifiers,
            int line)
        {
            bool replicated = false;
            bool quantized = false;
            string conditionSource = null;
            ushort quantizerId = 0;

            foreach (AttributeListSyntax list in attrs)
            {
                foreach (AttributeSyntax attr in list.Attributes)
                {
                    string name = SimpleAttributeName(attr);
                    if (name == AttrReplicated)
                    {
                        replicated = true;
                        string explicitCondition;
                        if (TryGetNamedArgument(attr, "Condition", out explicitCondition))
                        {
                            conditionSource = explicitCondition;
                        }
                        else if (attr.ArgumentList != null && attr.ArgumentList.Arguments.Count > 0)
                        {
                            conditionSource = attr.ArgumentList.Arguments[0].ToString();
                        }
                    }
                    else if (name == AttrQuantized)
                    {
                        quantized = true;
                        int value;
                        if (TryGetIntArgument(attr, "QuantizerId", 0, out value) && value > 0)
                        {
                            quantizerId = value > ushort.MaxValue ? ushort.MaxValue : (ushort)value;
                        }
                    }
                }
            }

            if (!replicated && !quantized)
            {
                return null;
            }

            PropRecord rec = new PropRecord();
            rec.MemberName = memberName;
            rec.TypeName = typeName;
            rec.IsField = isField;
            rec.IsStatic = HasModifier(modifiers, SyntaxKind.StaticKeyword)
                          || HasModifier(modifiers, SyntaxKind.ConstKeyword);
            rec.IsReadOnly = HasModifier(modifiers, SyntaxKind.ReadOnlyKeyword)
                             || HasModifier(modifiers, SyntaxKind.ConstKeyword);
            rec.HasReplicated = replicated;
            rec.ConditionSource = conditionSource;
            rec.QuantizerId = quantizerId;
            rec.Line = line;
            return rec;
        }

        // ------------------------------------------------------------------------------ 建 IR

        private static PMDeclClass BuildClass(ClassAccumulator acc, PMDeclRawFacts facts)
        {
            PMDeclClass cls = new PMDeclClass();
            cls.Namespace = acc.Namespace;
            cls.TypeName = acc.SimpleName;
            cls.StableKey = acc.EffectiveStableKey;
            cls.BaseTypeName = acc.BaseTypeName;
            cls.IsPartial = acc.IsPartial;
            cls.FilePath = acc.FilePath;
            cls.Line = acc.Line;

            if (acc.IsGeneric)
            {
                facts.GenericTypes.Add(acc.QualifiedName);
            }

            if (acc.IsAbstract)
            {
                facts.AbstractTypes.Add(acc.QualifiedName);
            }

            if (acc.HasExplicitCtor && !acc.HasParameterlessCtor)
            {
                facts.TypesWithoutUsableCtor.Add(acc.QualifiedName);
            }

            if (acc.HasOwnRepListOverride)
            {
                facts.TypesWithOwnRepListOverride.Add(acc.QualifiedName);
            }

            facts.TypeModifierPrefix[acc.QualifiedName] = acc.ModifierPrefix;
            facts.UsingsByType[acc.QualifiedName] = acc.Usings;

            for (int i = 0; i < acc.Properties.Count; i++)
            {
                PropRecord pr = acc.Properties[i];
                if (!pr.HasReplicated)
                {
                    continue;
                }

                PMDeclProperty p = new PMDeclProperty();
                p.MemberName = pr.MemberName;
                p.TypeName = pr.TypeName;
                p.IsField = pr.IsField;
                p.IsStatic = pr.IsStatic;
                p.IsReadOnly = pr.IsReadOnly;
                p.PushBased = true;
                p.QuantizerId = pr.QuantizerId;
                p.Line = pr.Line;

                PMCond cond;
                if (PMCondNames.TryResolve(pr.ConditionSource, out cond))
                {
                    p.Condition = cond;
                }
                else
                {
                    // 解析不出来：IR 里留 None，由 DeclValidation 的规则 11 报错（不在这里报）。
                    p.Condition = PMCond.None;
                }

                cls.Properties.Add(p);

                if (pr.ConditionSource != null)
                {
                    facts.ConditionSources[acc.QualifiedName + "." + pr.MemberName] = pr.ConditionSource;
                }
            }

            for (int i = 0; i < acc.Rpcs.Count; i++)
            {
                RpcRecord rr = acc.Rpcs[i];
                PMDeclRpc rpc = new PMDeclRpc();
                rpc.MethodName = rr.MethodName;
                rpc.Kind = rr.Kind;
                rpc.Reliability = rr.Reliability;
                rpc.IsStatic = rr.IsStatic;
                rpc.ReturnsVoid = rr.ReturnsVoid;
                rpc.Line = rr.Line;
                for (int k = 0; k < rr.Parameters.Count; k++)
                {
                    rpc.Parameters.Add(rr.Parameters[k]);
                }

                PMDeclRpcFact fact = new PMDeclRpcFact();
                fact.ClassQualifiedName = acc.QualifiedName;
                fact.MethodName = rr.MethodName;
                fact.WithValidationExplicit = rr.WithValidationExplicit;
                fact.ValidatorExplicit = rr.ValidatorExplicit;
                fact.HasForceValidateCompanion = acc.MethodNames.Contains(rr.MethodName + "_ForceValidate");
                fact.HasValidateCompanion = acc.MethodNames.Contains(rr.MethodName + "_Validate");
                fact.Line = rr.Line;
                facts.Rpcs.Add(fact);

                // 生效的校验档位：把"存在 _ForceValidate 同伴"折叠进 IR（契约 §5 规则 8 的第二个放行条件）。
                // 显式档位优先；其次 WithValidation=true（等价 Validate）；再次同伴。
                if (rr.ValidatorExplicit != PMRpcValidator.None)
                {
                    rpc.Validator = rr.ValidatorExplicit;
                }
                else if (rr.WithValidationExplicit)
                {
                    rpc.Validator = PMRpcValidator.Validate;
                }
                else if (fact.HasForceValidateCompanion)
                {
                    rpc.Validator = PMRpcValidator.ForceValidate;
                }

                cls.Rpcs.Add(rpc);
            }

            // RepNotify 绑定：把 OnRepMethodName 挂到被监听的属性上。
            // 找不到对应属性时**不在这里报错**——那是规则 5 的事。
            for (int i = 0; i < acc.RepNotifies.Count; i++)
            {
                PMDeclRepNotifyFact fact = acc.RepNotifies[i];
                for (int k = 0; k < cls.Properties.Count; k++)
                {
                    if (string.Equals(cls.Properties[k].MemberName, fact.ForMember, StringComparison.Ordinal)
                        && cls.Properties[k].OnRepMethodName == null)
                    {
                        cls.Properties[k].OnRepMethodName = fact.MethodName;
                    }
                }
            }

            return cls;
        }

        // ------------------------------------------------------------------------------ ID 分配

        private static void AssignIds(List<PMDeclClass> classes, PMIdLock idLock)
        {
            // 按稳定键升序分配：新键的"碰撞探测"结果因此与源文件顺序无关（契约 §2.2）。
            List<int> order = new List<int>();
            for (int i = 0; i < classes.Count; i++)
            {
                order.Add(i);
            }

            order.Sort(delegate (int a, int b)
            {
                return string.CompareOrdinal(classes[a].StableKey, classes[b].StableKey);
            });

            for (int oi = 0; oi < order.Count; oi++)
            {
                PMDeclClass cls = classes[order[oi]];

                uint existing;
                bool known = idLock.TryGetClassId(cls.StableKey, out existing);
                uint allocated = idLock.AllocateClass(cls.StableKey);
                if (known && allocated != existing)
                {
                    // 锁文件是权威，这条理论上不可达；留作"已存在键的 ID 被改"的硬防线
                    // （那等于悄悄改了线协议，必须整块失败而不是继续生成）。
                    throw new InvalidOperationException(
                        "类 " + cls.StableKey + " 的 ID 从 " + existing + " 变成了 " + allocated);
                }

                cls.ClassId = allocated;

                List<PMDeclProperty> props = cls.Properties;
                props.Sort(delegate (PMDeclProperty a, PMDeclProperty b)
                {
                    return string.CompareOrdinal(a.MemberName, b.MemberName);
                });

                for (int i = 0; i < props.Count; i++)
                {
                    props[i].PropertyId = idLock.AllocateMember(
                        cls.StableKey, PMStableHash.PropertyKey(cls.StableKey, props[i].MemberName));
                }

                List<PMDeclRpc> rpcs = cls.Rpcs;
                rpcs.Sort(delegate (PMDeclRpc a, PMDeclRpc b)
                {
                    return string.CompareOrdinal(a.MethodName, b.MethodName);
                });

                for (int i = 0; i < rpcs.Count; i++)
                {
                    rpcs[i].RpcId = idLock.AllocateMember(
                        cls.StableKey, PMStableHash.RpcKey(cls.StableKey, rpcs[i].MethodName));
                }
            }
        }

        // ------------------------------------------------------------------------------ 收尾

        private static void FinalizeClass(PMDeclClass cls)
        {
            // 掩码区间：按 PropertyId 升序紧凑排布，每属性 1 位（契约 §5 规则 12 的不变量）。
            // 属性 Id 与 RPC Id 共用同一个类内编号空间（见 PMIdLock.AllocateMember 的注释），
            // 因此这里只需要按 PropertyId 排一次。
            cls.Properties.Sort(delegate (PMDeclProperty a, PMDeclProperty b)
            {
                return a.PropertyId.CompareTo(b.PropertyId);
            });

            for (int i = 0; i < cls.Properties.Count; i++)
            {
                cls.Properties[i].MaskOffset = (ushort)i;
                cls.Properties[i].MaskBitCount = 1;
            }

            cls.ChangeMaskBitCount = cls.Properties.Count;

            cls.Rpcs.Sort(delegate (PMDeclRpc a, PMDeclRpc b)
            {
                return a.RpcId.CompareTo(b.RpcId);
            });

            // 参数布局哈希：只取决于参数类型的有序列表，与参数名无关（D-R0-46）。
            for (int i = 0; i < cls.Rpcs.Count; i++)
            {
                string[] types = new string[cls.Rpcs[i].Parameters.Count];
                for (int k = 0; k < types.Length; k++)
                {
                    types[k] = cls.Rpcs[i].Parameters[k].TypeName;
                }

                cls.Rpcs[i].ParamLayoutId = PMStableHash.ParamLayoutId(types);
            }
        }

        /// <summary>
        /// RepNotify 槽位（1 起算；0 = 无）。
        ///
        /// IR 里只存 `OnRepMethodName`（冻结字段），槽位由**按 PropertyId 升序的确定性顺序**推导，
        /// 因此生成物与生成期算出的 `OnRepMethodId` 必然一致。
        /// </summary>
        public static ushort RepNotifySlotOf(PMDeclClass cls, int propertyIndex)
        {
            ushort slot = 0;
            for (int i = 0; i < cls.Properties.Count; i++)
            {
                if (!string.IsNullOrEmpty(cls.Properties[i].OnRepMethodName))
                {
                    slot++;
                    if (i == propertyIndex)
                    {
                        return slot;
                    }
                }
            }

            return 0;
        }

        /// <summary>
        /// 计算继承链上的复制属性序号基址。
        /// 只沿**本次扫描到的**已声明类上溯；跨程序集基类无法在声明期得知，按 0 处理。
        /// </summary>
        private static void ComputePropertyIndexBase(List<PMDeclClass> classes, PMDeclRawFacts facts)
        {
            Dictionary<string, PMDeclClass> declared = new Dictionary<string, PMDeclClass>(StringComparer.Ordinal);
            for (int i = 0; i < classes.Count; i++)
            {
                declared[KeyOf(classes[i])] = classes[i];
            }

            for (int i = 0; i < classes.Count; i++)
            {
                PMDeclClass cls = classes[i];
                int total = 0;
                List<string> guard = new List<string>();
                string ns = cls.Namespace;
                string cur = cls.BaseTypeName;

                while (!string.IsNullOrEmpty(cur))
                {
                    if (LastSegment(cur) == "PMNetObject")
                    {
                        break;
                    }

                    string resolved = ResolveTypeName(facts, ns, cur);
                    if (resolved == null || guard.Contains(resolved))
                    {
                        break;
                    }

                    guard.Add(resolved);

                    PMDeclClass baseCls;
                    if (!declared.TryGetValue(resolved, out baseCls))
                    {
                        break;
                    }

                    total += baseCls.Properties.Count;
                    ns = baseCls.Namespace;
                    cur = baseCls.BaseTypeName;
                }

                facts.PropertyIndexBase[KeyOf(cls)] = total;
            }
        }

        /// <summary>类限定名（IR 里 IR 外一致使用的键）。</summary>
        public static string KeyOf(PMDeclClass cls)
        {
            return cls.Namespace + "." + cls.TypeName;
        }

        private static void ComputeProtocolHashes(List<PMDeclClass> classes, PMDeclModel model)
        {
            for (int i = 0; i < classes.Count; i++)
            {
                PMDeclClass cls = classes[i];
                cls.ClassProtocolHash = PMStableHash.ClassProtocolHash(cls.ClassId, BuildPropertyDescriptors(cls));
            }

            model.GlobalProtocolHash = PMStableHash.GlobalProtocolHash(BuildClassEntries(model));
        }

        /// <summary>
        /// 按生成物将要注册的形状构造类条目。
        ///
        /// **必须与 DeclEmitter 产出的 `PMNet_BuildEntry()` 完全一致**，
        /// 否则生成期算出的 GlobalProtocolHash 与运行期算出的值会不同——
        /// 那是"协议静默不兼容"最典型的来源。PMDeclCheck 会断言两者相等。
        /// </summary>
        public static PMNetClassEntry[] BuildClassEntries(PMDeclModel model)
        {
            List<PMDeclClass> classes = model.Classes;
            PMNetClassEntry[] entries = new PMNetClassEntry[classes.Count];

            for (int i = 0; i < classes.Count; i++)
            {
                PMDeclClass cls = classes[i];

                PMReplicationDescriptor rep = new PMReplicationDescriptor();
                rep.ClassId = cls.ClassId;
                rep.Properties = BuildPropertyDescriptors(cls);
                rep.ChangeMaskBitCount = cls.ChangeMaskBitCount;
                rep.HasConditionalMask = HasConditionalMask(cls);
                rep.ProtocolHash = cls.ClassProtocolHash;
                rep.TypeName = cls.QualifiedName;

                PMNetRpcEntry[] rpcs = new PMNetRpcEntry[cls.Rpcs.Count];
                for (int k = 0; k < rpcs.Length; k++)
                {
                    PMDeclRpc r = cls.Rpcs[k];
                    PMNetRpcEntry e = new PMNetRpcEntry();
                    e.Descriptor = new PMRpcDescriptor();
                    e.Descriptor.RpcId = r.RpcId;
                    e.Descriptor.Direction = r.Kind;
                    e.Descriptor.IsReliable = r.Reliability == PMRpcReliability.Reliable;
                    e.Descriptor.Validator = r.Validator;
                    e.Descriptor.ParamLayoutId = r.ParamLayoutId;
                    e.Descriptor.MethodName = r.MethodName;
                    e.OwningClassId = cls.ClassId;
                    rpcs[k] = e;
                }

                PMNetClassEntry entry = new PMNetClassEntry();
                entry.ClassId = cls.ClassId;
                entry.TypeName = cls.QualifiedName;
                entry.Rep = rep;
                entry.Rpcs = rpcs;
                entries[i] = entry;
            }

            return entries;
        }

        /// <summary>构造属性描述符数组（只填协议摘要与注册表关心的字段）。</summary>
        public static PMPropertyDescriptor[] BuildPropertyDescriptors(PMDeclClass cls)
        {
            PMPropertyDescriptor[] props = new PMPropertyDescriptor[cls.Properties.Count];
            for (int i = 0; i < props.Length; i++)
            {
                PMDeclProperty p = cls.Properties[i];
                PMPropertyDescriptor d = new PMPropertyDescriptor();
                d.PropertyId = p.PropertyId;
                d.Condition = p.Condition;
                d.MaskOffset = p.MaskOffset;
                d.MaskBitCount = p.MaskBitCount;
                d.QuantizerId = p.QuantizerId;
                d.OnRepMethodId = RepNotifySlotOf(cls, i);
                d.PushBased = p.PushBased;
                d.MemberName = p.MemberName;
                d.SetterName = "PMNet_Set" + p.MemberName;
                props[i] = d;
            }

            return props;
        }

        private static bool HasConditionalMask(PMDeclClass cls)
        {
            for (int i = 0; i < cls.Properties.Count; i++)
            {
                if (cls.Properties[i].Condition != PMCond.None)
                {
                    return true;
                }
            }

            return false;
        }

        // ------------------------------------------------------------------------------ 键退役

        /// <summary>
        /// 锁文件里已存在、但本次声明中不再出现的类键 ⇒ 只告警，**ID 永不回收**（契约 §2.2）。
        /// </summary>
        public static void ReportRetiredKeys(PMIdLock idLock, string lockBeforeText, PMDeclModel model)
        {
            if (idLock == null || model == null)
            {
                return;
            }

            PMDeclLockKeys before = ExtractLockKeys(lockBeforeText);
            HashSet<string> current = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < model.Classes.Count; i++)
            {
                current.Add(model.Classes[i].StableKey);
            }

            for (int i = 0; i < before.ClassKeys.Count; i++)
            {
                if (!current.Contains(before.ClassKeys[i]))
                {
                    idLock.MarkRetired(before.ClassKeys[i]);
                }
            }

            for (int i = 0; i < idLock.Retired.Count; i++)
            {
                model.Warnings.Add("[告警] 稳定键已退役（ID 不回收，线协议里该编号永久保留）：" + idLock.Retired[i]);
            }
        }

        /// <summary>
        /// 从锁文件文本里取出全部键。
        ///
        /// 为什么要这么绕：<see cref="PMIdLock"/> 只提供 `TryGet*`（按键查值），**没有枚举键的 API**，
        /// 而"退役检测"必须知道锁文件里有哪些键。这里复用 `PMIdLock.Serialize()` 的输出形状
        /// （由 `formatVersion` 保护）做一次只读的键扫描，不引入第二套 JSON 实现。
        /// </summary>
        public static PMDeclLockKeys ExtractLockKeys(string lockJsonText)
        {
            PMDeclLockKeys keys = new PMDeclLockKeys();
            if (string.IsNullOrEmpty(lockJsonText))
            {
                return keys;
            }

            string[] lines = lockJsonText.Replace("\r\n", "\n").Split('\n');
            int section = 0; // 1 = classes, 2 = members
            string currentOwner = null;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line == "}" || line == "},")
                {
                    continue;
                }

                if (line.StartsWith("\"classes\"", StringComparison.Ordinal))
                {
                    section = 1;
                    continue;
                }

                if (line.StartsWith("\"members\"", StringComparison.Ordinal))
                {
                    section = 2;
                    continue;
                }

                int colon = FindValueColon(line);
                if (colon <= 0)
                {
                    continue;
                }

                string keyPart = line.Substring(0, colon).Trim().TrimEnd(',').Trim();
                string valuePart = line.Substring(colon + 1).Trim().TrimEnd(',').Trim();
                if (keyPart.Length < 2 || keyPart[0] != '"')
                {
                    continue;
                }

                string key = Unquote(keyPart);
                if (section == 1)
                {
                    keys.ClassKeys.Add(key);
                }
                else if (section == 2)
                {
                    if (valuePart.StartsWith("{", StringComparison.Ordinal))
                    {
                        currentOwner = key;
                        if (!keys.MemberKeys.ContainsKey(key))
                        {
                            keys.MemberKeys[key] = new List<string>();
                        }
                    }
                    else if (currentOwner != null)
                    {
                        keys.MemberKeys[currentOwner].Add(key);
                    }
                }
            }

            keys.ClassKeys.Sort(StringComparer.Ordinal);
            return keys;
        }

        /// <summary>
        /// 找出「键」与「值」之间的那个冒号。
        ///
        /// 坑：稳定键本身就带冒号（`CLASS:Ns.Type` / `PROP:Ns.Type.Member`），
        /// 所以不能取行里**第一个**冒号——那会把 `"CLASS` 当成键、
        /// 把 `Ns.Type": 123` 当成值，后果是“退役检测”全部拿错。
        /// </summary>
        private static int FindValueColon(string line)
        {
            int start = 0;
            if (line.Length > 0 && line[0] == '"')
            {
                int closeQuote = line.IndexOf('"', 1);
                if (closeQuote < 0)
                {
                    return -1;
                }

                start = closeQuote + 1;
            }

            return line.IndexOf(':', start);
        }

        private static string Unquote(string quoted)
        {
            string t = quoted;
            if (t.Length >= 2 && t[0] == '"' && t[t.Length - 1] == '"')
            {
                t = t.Substring(1, t.Length - 2);
            }

            return t.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        // ------------------------------------------------------------------------------ 工具

        /// <summary>
        /// 取属性的"简单名"：去掉命名空间限定与 `Attribute` 后缀。
        ///
        /// 按**全名字符串**匹配即可（不引运行时程序集）：
        /// `PMNet.PMReplicated` / `PMReplicated` / `PMReplicatedAttribute` 都归一到 `PMReplicated`。
        /// </summary>
        public static string SimpleAttributeName(AttributeSyntax attr)
        {
            string name = attr.Name != null ? attr.Name.ToString() : string.Empty;
            int dot = name.LastIndexOf('.');
            if (dot >= 0)
            {
                name = name.Substring(dot + 1);
            }

            if (name.EndsWith("Attribute", StringComparison.Ordinal))
            {
                name = name.Substring(0, name.Length - "Attribute".Length);
            }

            return name;
        }

        private static List<string> CollectUsings(SyntaxNode root)
        {
            List<string> result = new List<string>();
            CompilationUnitSyntax cu = root as CompilationUnitSyntax;
            if (cu == null)
            {
                return result;
            }

            foreach (UsingDirectiveSyntax u in cu.Usings)
            {
                result.Add(u.ToString());
            }

            return result;
        }

        private static void MergeUsings(List<string> target, List<string> source)
        {
            for (int i = 0; i < source.Count; i++)
            {
                if (!target.Contains(source[i]))
                {
                    target.Add(source[i]);
                }
            }
        }

        private static void AddSimpleName(PMDeclRawFacts facts, string simple, string qualified)
        {
            List<string> list;
            if (!facts.TypeSimpleNames.TryGetValue(simple, out list))
            {
                list = new List<string>();
                facts.TypeSimpleNames[simple] = list;
            }

            if (!list.Contains(qualified))
            {
                list.Add(qualified);
            }
        }

        /// <summary>把书写形式（可能是简单名）解析成扫描到的限定名；解析不到返回 null。</summary>
        public static string ResolveTypeName(PMDeclRawFacts facts, string contextNamespace, string written)
        {
            if (string.IsNullOrEmpty(written))
            {
                return null;
            }

            string name = TypeText(written).Replace("global::", string.Empty);
            int lt = name.IndexOf('<');
            if (lt >= 0)
            {
                name = name.Substring(0, lt);
            }

            if (facts.DirectBaseTypes.ContainsKey(name))
            {
                return name;
            }

            if (!string.IsNullOrEmpty(contextNamespace))
            {
                string candidate = contextNamespace + "." + name;
                if (facts.DirectBaseTypes.ContainsKey(candidate))
                {
                    return candidate;
                }
            }

            List<string> list;
            if (facts.TypeSimpleNames.TryGetValue(name, out list) && list.Count == 1)
            {
                return list[0];
            }

            return null;
        }

        private static bool IsTopLevel(SyntaxNode node)
        {
            return node.Parent is BaseNamespaceDeclarationSyntax || node.Parent is CompilationUnitSyntax;
        }

        private static string GetNamespace(SyntaxNode node)
        {
            List<string> parts = new List<string>();
            for (SyntaxNode n = node.Parent; n != null; n = n.Parent)
            {
                BaseNamespaceDeclarationSyntax ns = n as BaseNamespaceDeclarationSyntax;
                if (ns != null)
                {
                    parts.Insert(0, ns.Name.ToString());
                }
            }

            return string.Join(".", parts.ToArray());
        }

        private static string Qualified(string ns, string simple)
        {
            return string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
        }

        private static string LastSegment(string text)
        {
            string t = TypeText(text);
            int dot = t.LastIndexOf('.');
            return dot >= 0 ? t.Substring(dot + 1) : t;
        }

        private static string FirstBaseTypeText(BaseTypeDeclarationSyntax btd)
        {
            if (btd.BaseList == null || btd.BaseList.Types.Count == 0)
            {
                return string.Empty;
            }

            // C# 只允许一个基类，且必须排在接口之前；取第一个就是基类（若写的是接口，
            // 规则 1 的继承链推导会得出"推不到 PMNetObject"并报错，不会静默放行）。
            return TypeText(btd.BaseList.Types[0].Type);
        }

        private static string ModifierPrefix(BaseTypeDeclarationSyntax btd)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < btd.Modifiers.Count; i++)
            {
                SyntaxToken m = btd.Modifiers[i];
                if (m.IsKind(SyntaxKind.PartialKeyword))
                {
                    continue; // partial 由生成代码统一补
                }

                sb.Append(m.Text).Append(' ');
            }

            return sb.ToString();
        }

        private static bool HasModifier(SyntaxTokenList modifiers, SyntaxKind kind)
        {
            for (int i = 0; i < modifiers.Count; i++)
            {
                if (modifiers[i].IsKind(kind))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasSetter(PropertyDeclarationSyntax prop)
        {
            if (prop.AccessorList == null)
            {
                return false;
            }

            foreach (AccessorDeclarationSyntax a in prop.AccessorList.Accessors)
            {
                if (a.IsKind(SyntaxKind.SetAccessorDeclaration)
                    || a.IsKind(SyntaxKind.InitAccessorDeclaration))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>类型语法节点的书写文本（去空白）。</summary>
        public static string TypeText(TypeSyntax type)
        {
            return type == null ? string.Empty : TypeText(type.ToString());
        }

        /// <summary>去掉全部空白（IR 里的类型名以"去空白"为准，与 PMStableHash 的归一化口径一致）。</summary>
        public static string TypeText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (!char.IsWhiteSpace(text[i]))
                {
                    sb.Append(text[i]);
                }
            }

            return sb.ToString();
        }

        private static int LineOf(SyntaxNode node)
        {
            return node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        }

        private static bool TryGetNamedArgument(AttributeSyntax attr, string name, out string value)
        {
            value = null;
            if (attr.ArgumentList == null)
            {
                return false;
            }

            foreach (AttributeArgumentSyntax arg in attr.ArgumentList.Arguments)
            {
                if (arg.NameEquals != null
                    && string.Equals(arg.NameEquals.Name.Identifier.Text, name, StringComparison.Ordinal))
                {
                    value = arg.Expression.ToString();
                    return true;
                }

                if (arg.NameColon != null
                    && string.Equals(arg.NameColon.Name.Identifier.Text, name, StringComparison.Ordinal))
                {
                    value = arg.Expression.ToString();
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetStringArgument(AttributeSyntax attr, string name, int position, out string value)
        {
            value = null;
            if (attr.ArgumentList == null)
            {
                return false;
            }

            string named;
            if (TryGetNamedArgument(attr, name, out named))
            {
                value = ResolveStringExpression(named);
                return true;
            }

            List<AttributeArgumentSyntax> positional = new List<AttributeArgumentSyntax>();
            foreach (AttributeArgumentSyntax arg in attr.ArgumentList.Arguments)
            {
                if (arg.NameEquals == null && arg.NameColon == null)
                {
                    positional.Add(arg);
                }
            }

            if (position < positional.Count)
            {
                value = ResolveStringExpression(positional[position].ToString());
                return true;
            }

            return false;
        }

        /// <summary>
        /// 把属性实参表达式还原成一个字符串值。
        ///
        /// 支持两种写法（两种都在真实业务里很常见）：
        /// <code>
        /// [PMRepNotify("OnRep_Hp")]       // 字符串字面量
        /// [PMRepNotify(nameof(OnRep_Hp))]  // nameof —— 这里按语法取最后一个标识符段
        /// </code>
        /// 其它表达式（如常量拼接）无法在**语法层**求值：原样返回，由规则 5 报"找不到对应成员"。
        /// 这样不会把"解析不出来"误判成"匹配成功"。
        /// </summary>
        private static string ResolveStringExpression(string expressionText)
        {
            string t = TypeText(expressionText);
            if (t.Length >= 2 && t[0] == '"' && t[t.Length - 1] == '"')
            {
                return Unquote(t);
            }

            const string nameofPrefix = "nameof(";
            if (t.StartsWith(nameofPrefix, StringComparison.Ordinal) && t.EndsWith(")", StringComparison.Ordinal))
            {
                string inner = t.Substring(nameofPrefix.Length, t.Length - nameofPrefix.Length - 1);
                int dot = inner.LastIndexOf('.');
                return dot >= 0 ? inner.Substring(dot + 1) : inner;
            }

            return t;
        }

        private static bool TryGetIntArgument(AttributeSyntax attr, string name, int position, out int value)
        {
            value = 0;
            string text = null;

            string named;
            if (TryGetNamedArgument(attr, name, out named))
            {
                text = named;
            }
            else if (attr.ArgumentList != null)
            {
                List<AttributeArgumentSyntax> positional = new List<AttributeArgumentSyntax>();
                foreach (AttributeArgumentSyntax arg in attr.ArgumentList.Arguments)
                {
                    if (arg.NameEquals == null && arg.NameColon == null)
                    {
                        positional.Add(arg);
                    }
                }

                if (position < positional.Count)
                {
                    text = positional[position].ToString();
                }
            }

            if (text == null)
            {
                return false;
            }

            return int.TryParse(TypeText(text), out value);
        }

        private static bool TryParseReliability(string text, out PMRpcReliability value)
        {
            value = PMRpcReliability.Unreliable;
            string name = LastSegment(text);
            if (name == "Reliable")
            {
                value = PMRpcReliability.Reliable;
                return true;
            }

            if (name == "Unreliable")
            {
                value = PMRpcReliability.Unreliable;
                return true;
            }

            return false;
        }

        private static bool TryParseValidator(string text, out PMRpcValidator value)
        {
            value = PMRpcValidator.None;
            string name = LastSegment(text);
            if (name == "None")
            {
                value = PMRpcValidator.None;
                return true;
            }

            if (name == "Validate")
            {
                value = PMRpcValidator.Validate;
                return true;
            }

            if (name == "ForceValidate")
            {
                value = PMRpcValidator.ForceValidate;
                return true;
            }

            return false;
        }

        private static ClassAccumulator GetOrAdd(Dictionary<string, ClassAccumulator> accs, string qualifiedName)
        {
            ClassAccumulator acc;
            if (!accs.TryGetValue(qualifiedName, out acc))
            {
                acc = new ClassAccumulator();
                acc.QualifiedName = qualifiedName;
                accs.Add(qualifiedName, acc);
            }

            return acc;
        }

        private static string[] ToArray(IList<string> paths)
        {
            string[] result = new string[paths.Count];
            for (int i = 0; i < paths.Count; i++)
            {
                result[i] = paths[i];
            }

            return result;
        }

        /// <summary>把输入路径展开成按序（Ordinal 排序）的 .cs 文件列表。</summary>
        public static List<string> CollectFiles(IList<string> paths)
        {
            List<string> files = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                if (File.Exists(path))
                {
                    string full = Path.GetFullPath(path);
                    if (IsScanCandidate(full) && seen.Add(full))
                    {
                        files.Add(full);
                    }

                    continue;
                }

                if (Directory.Exists(path))
                {
                    string[] all = Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories);
                    Array.Sort(all, StringComparer.Ordinal);
                    for (int k = 0; k < all.Length; k++)
                    {
                        string full = Path.GetFullPath(all[k]);
                        if (IsScanCandidate(full) && seen.Add(full))
                        {
                            files.Add(full);
                        }
                    }

                    continue;
                }

                throw new FileNotFoundException("源路径不存在：" + path);
            }

            files.Sort(StringComparer.Ordinal);
            return files;
        }

        private static bool IsScanCandidate(string fullPath)
        {
            string leaf = Path.GetFileName(fullPath);
            if (leaf.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            {
                return false; // 生成产物不回扫（否则二次生成会把自己的产物当输入）
            }

            string normalized = fullPath.Replace('\\', '/');
            if (normalized.IndexOf("/obj/", StringComparison.OrdinalIgnoreCase) >= 0
                || normalized.IndexOf("/bin/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            return true;
        }
    }
}
