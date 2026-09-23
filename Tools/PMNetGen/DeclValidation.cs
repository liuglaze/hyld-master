using System;
using System.Collections.Generic;
using System.Reflection;
using PMNet;
using PMNet.Codegen;

namespace PMNetGen
{
    // =========================================================================================
    //  R2 声明校验（契约 §5 的 13 条规则 + §6 的类型集 + 自动属性形态规则 14）
    //
    //  设计约束：
    //    1. **所有**规则都在这一层，扫描器不做规则判定（见 DeclScanner.cs 头注释）；
    //    2. 错误一律带 `[规则 N]` 前缀——门禁（Tools/PMDeclCheck）靠这个前缀统计
    //       "每条规则是否真的被触发过"，避免"跑绿了但一条规则都没生效"；
    //    3. 报告要能定位到源码：消息里带类/成员名与源文件行号。
    //
    //  规则 14（自动属性形态）不是 R2 契约 §5 的条款，而是自动属性复制契约
    //  （Docs/plans/net-property-authoring-contract.md §1）新增的：它会把
    //  "自定义访问器 / 只读 / indexer / virtual-override-abstract / ref-return /
    //   显式接口实现 / 标记在不支持的成员种类上"变成**生成前的硬错误**，
    //  而不是让它们到编织期（甚至运行期）才暴露，更不能让它们静默漏扫。
    //
    //  关于"生成期反射"：本文件用反射枚举 PMNet 运行时里**已存在的** PMNetObject 派生类型，
    //  目的是让规则 1 能正确接受 `: PMNetPawnObject` 这类运行时基类。运行期禁反射
    //  （D-R0-48 / T40）说的是**发货产物**不许扫反射，生成期工具不受此约束；
    //  生成出来的代码本身仍然零反射（PMDeclCheck 会断言产物里没有反射调用）。
    // =========================================================================================

    /// <summary>契约 §6 的标量类型分类（复制成员与 RPC 参数共用）。</summary>
    public enum PMWireKind
    {
        /// <summary>不在支持的类型集内。</summary>
        Unsupported = 0,

        /// <summary>`byte`。</summary>
        Byte,

        /// <summary>`sbyte`。</summary>
        SByte,

        /// <summary>`short`。</summary>
        Short,

        /// <summary>`ushort`。</summary>
        UShort,

        /// <summary>`int`。</summary>
        Int,

        /// <summary>`uint`。</summary>
        UInt,

        /// <summary>`long`。</summary>
        Long,

        /// <summary>`ulong`。</summary>
        ULong,

        /// <summary>`bool`。</summary>
        Bool,

        /// <summary>`float`。</summary>
        Float,

        /// <summary>`double`。</summary>
        Double,

        /// <summary>`string`。</summary>
        String,

        /// <summary>底层为整型的 enum。</summary>
        Enum,

        /// <summary>`T[]`（T 为上表之一）。</summary>
        Array,
    }

    /// <summary>契约 §6 的首版类型集。</summary>
    public static class PMTypeSet
    {
        /// <summary>把书写类型名分类。数组会通过 <paramref name="elementTypeName"/> 返回元素类型。</summary>
        public static PMWireKind Classify(string typeName, PMDeclRawFacts facts, out string elementTypeName)
        {
            elementTypeName = null;
            return Classify(typeName, facts, out elementTypeName, 0);
        }

        /// <summary>给出"为什么不在类型集内"的可读原因；受支持时返回 null。</summary>
        public static string DescribeUnsupported(string typeName, PMDeclRawFacts facts)
        {
            string element;
            PMWireKind kind = Classify(typeName, facts, out element, 0);
            if (kind != PMWireKind.Unsupported)
            {
                return null;
            }

            string name = Normalize(typeName);

            if (facts != null && facts.NetworkStructTypeNames.Contains(name))
            {
                return "类型 " + name + " 被 [PMNetworkStruct] 标记；结构体复制属 R2 明确不实现的范围（契约 §6）";
            }

            if (name.EndsWith("[]", StringComparison.Ordinal))
            {
                string elem = name.Substring(0, name.Length - 2);
                if (elem.EndsWith("[]", StringComparison.Ordinal)
                    || elem.EndsWith("[,]", StringComparison.Ordinal))
                {
                    return "多维/交错数组不在类型集内（契约 §6 只支持一维 T[]）：" + name;
                }

                return "数组元素类型不在类型集内（契约 §6）：" + elem;
            }

            if (name.EndsWith("?", StringComparison.Ordinal))
            {
                return "可空类型不在类型集内（契约 §6）：" + name;
            }

            if (name.IndexOf('<') >= 0)
            {
                return "泛型类型不在类型集内（契约 §6）：" + name;
            }

            return "类型 " + name + " 不在首版类型集内（契约 §6：整数/布尔/浮点/字符串/枚举/一维数组）";
        }

        private static PMWireKind Classify(string typeName, PMDeclRawFacts facts, out string elementTypeName, int depth)
        {
            elementTypeName = null;
            string name = Normalize(typeName);
            if (name.Length == 0)
            {
                return PMWireKind.Unsupported;
            }

            if (name.EndsWith("[]", StringComparison.Ordinal))
            {
                if (depth > 0)
                {
                    return PMWireKind.Unsupported; // 交错数组不支持
                }

                string elem = name.Substring(0, name.Length - 2);
                string ignored;
                PMWireKind elemKind = Classify(elem, facts, out ignored, depth + 1);
                if (elemKind == PMWireKind.Unsupported || elemKind == PMWireKind.Array)
                {
                    return PMWireKind.Unsupported;
                }

                elementTypeName = elem;
                return PMWireKind.Array;
            }

            if (facts != null && facts.NetworkStructTypeNames.Contains(name))
            {
                return PMWireKind.Unsupported;
            }

            switch (name)
            {
                case "byte":
                case "Byte":
                case "System.Byte":
                    return PMWireKind.Byte;

                case "sbyte":
                case "SByte":
                case "System.SByte":
                    return PMWireKind.SByte;

                case "short":
                case "Int16":
                case "System.Int16":
                    return PMWireKind.Short;

                case "ushort":
                case "UInt16":
                case "System.UInt16":
                    return PMWireKind.UShort;

                case "int":
                case "Int32":
                case "System.Int32":
                    return PMWireKind.Int;

                case "uint":
                case "UInt32":
                case "System.UInt32":
                    return PMWireKind.UInt;

                case "long":
                case "Int64":
                case "System.Int64":
                    return PMWireKind.Long;

                case "ulong":
                case "UInt64":
                case "System.UInt64":
                    return PMWireKind.ULong;

                case "bool":
                case "Boolean":
                case "System.Boolean":
                    return PMWireKind.Bool;

                case "float":
                case "Single":
                case "System.Single":
                    return PMWireKind.Float;

                case "double":
                case "Double":
                case "System.Double":
                    return PMWireKind.Double;

                case "string":
                case "String":
                case "System.String":
                    return PMWireKind.String;
            }

            if (facts != null && facts.EnumTypeNames.Contains(name))
            {
                return PMWireKind.Enum;
            }

            return PMWireKind.Unsupported;
        }

        /// <summary>去掉空白与 `global::` 前缀（与 PMStableHash 的归一化口径一致）。</summary>
        public static string Normalize(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return string.Empty;
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder(typeName.Length);
            for (int i = 0; i < typeName.Length; i++)
            {
                char c = typeName[i];
                if (!char.IsWhiteSpace(c))
                {
                    sb.Append(c);
                }
            }

            return sb.ToString().Replace("global::", string.Empty);
        }
    }

    /// <summary>声明校验器：把契约 §5 / §6 的判定结果写进 `PMDeclModel.Errors` / `Warnings`。</summary>
    public static class PMDeclValidation
    {
        /// <summary>
        /// 契约 §5 的规则条数 + 自动属性形态规则（规则 14）。
        ///
        /// 规则 13 是 R2 返工时**补上**的：原契约只要求"Server RPC 必须声明校验"（规则 8），
        /// 但那只管"有没有声明"。实测发现「声明了 ForceValidate 档位、却没写同伴方法」
        /// 会让发射器走投无路 —— 要么调用不存在的方法，要么退化成
        /// 「声称有校验、实际没有」。后者是本项目最不能接受的失败形态，因此单独立规则。
        ///
        /// 规则 14 是自动属性复制开工时**补上**的：用户批准自然 C# 自动属性赋值后，
        /// 业务可以写 `[PMReplicated] public int Hp { get; private set; }`。
        /// 但"属性"这个语法形式能写出的形态远多于可编织的一种，而编织器（另一组并行工作）
        /// 只处理"普通实例 auto-property"。若不在这里拦下，不支持形态会以
        /// 「声明通过 → 生成物看似正常 → 编织期才报错（或更糟：静默不生效）」的路径漏出去。
        /// </summary>
        public const int RuleCount = 14;

        /// <summary>规则标题（索引 = 规则号 - 1），供门禁打印"每条规则的实际命中情况"。</summary>
        public static readonly string[] RuleTitles =
        {
            "[PMNetworkObject] 的类必须是 partial 且能推到 PMNetObject",
            "[PMNetworkObject] 的类不得是泛型类",
            "[PMReplicated] 成员不得是 static / const / readonly",
            "[PMReplicated] 成员与 RPC 参数的类型必须落在支持的类型集内",
            "[PMRepNotify] 的 ForMember 必须对应一个已声明的 [PMReplicated] 成员",
            "[PMRepNotify] 目标方法必须无参、返回 void",
            "[PMRpc] 方法必须是可编织的普通实例方法：返回 void、不得 static/virtual/abstract/extern/async/泛型/无体、不得同名重载，形参不得 ref/out/in/params/default",
            "[PMServerRpc] 必须声明校验（Validator != None 或存在 _ForceValidate 同伴）",
            "WithValidation = true 与 Validator = ForceValidate 不得同时出现",
            "同一类内 PropertyId / RpcId 不得重复（含跨程序集）",
            "声明的条件必须是 D-R0-14 的 8 项之一",
            "同一个类的 MaskOffset 区间不得重叠，且总数 == ChangeMaskBitCount",
            "声明的校验档位必须有可调用的同伴方法（ForceValidate ⇒ _ForceValidate；Validate ⇒ _Validate）",
            "[PMReplicated] 属性必须是普通实例自动属性（get;set;）：不得自定义访问器 / 只读 / indexer / virtual-override-abstract / ref-return / 显式接口实现，也不得把标记放在不支持的成员种类上",
        };

        /// <summary>PMNet 运行时里全部 PMNetObject 派生类型的名字（用于规则 1 的继承链判定）。</summary>
        private static readonly HashSet<string> KnownNetBaseNames = BuildKnownNetBaseNames();

        /// <summary>
        /// 执行全部 13 条规则、自动属性形态规则 14 与 §6 类型集检查。
        /// 结果直接写进 <paramref name="model"/> 的 Errors / Warnings，不做返回值。
        /// </summary>
        public static void Validate(PMDeclModel model, PMDeclRawFacts facts)
        {
            if (model == null)
            {
                throw new ArgumentNullException("model");
            }

            if (facts == null)
            {
                facts = new PMDeclRawFacts();
            }

            Dictionary<string, PMDeclClass> declared = new Dictionary<string, PMDeclClass>(StringComparer.Ordinal);
            for (int i = 0; i < model.Classes.Count; i++)
            {
                PMDeclClass cls = model.Classes[i];
                string key = PMDeclScanner.KeyOf(cls);
                if (!declared.ContainsKey(key))
                {
                    declared.Add(key, cls);
                }
            }

            Rule1_NetworkObjectHost(model, facts, declared);
            Rule2_NoGeneric(model, facts);
            Rule3_ReplicatedModifiers(model, declared);
            Rule4_TypeSet(model, facts, declared);
            Rule5_RepNotifyForMember(model, facts, declared);
            Rule6_RepNotifySignature(model, facts, declared);
            Rule7_RpcSignature(model, facts, declared);
            Rule8_ServerRpcValidation(model, facts, declared);
            Rule9_ValidatorConflict(model, facts, declared);
            Rule10_IdUniqueness(model);
            Rule11_ConditionSet(model, facts);
            Rule12_MaskLayout(model);
            Rule13_ValidatorCompanion(model, facts, declared);
            Rule14_ReplicatedPropertyShape(model, facts);

            ScanIntegrityGate(model, facts);

            EmitWarnings(model, facts, declared);
        }

        // ------------------------------------------------------------------ 扫描完整性

        /// <summary>
        /// 扫描完整性门（不是契约 §5 的规则，而是「不许用不完整的声明集覆盖现有产物」的安全网）。
        ///
        /// 为什么需要它：读取失败 / 语法错误都会让扫描结果**看起来正常但少东西**。
        /// `--decl-gen` 写出的是**整套**产物（包括注册表），一旦用这种残缺结果生成，
        /// 就会拿一张假空/少类的注册表覆盖现有产物 ---- 那是比“报错停下”严重得多的失败形态。
        /// 这两个条件以前只是告警（读不到就跳过、语法错误只计数），
        /// 而告警不会阻断生成，因此这里升级为错误（`Program.RunDecl` 在 Errors 非空时直接拒给生成）。
        /// </summary>
        private static void ScanIntegrityGate(PMDeclModel model, PMDeclRawFacts facts)
        {
            for (int i = 0; i < facts.ReadFailures.Count; i++)
            {
                model.Errors.Add("[扫描完整性] 读取源文件失败：" + facts.ReadFailures[i]
                    + " —— 声明集不完整，拒绝生成/校验（禁止用残缺结果覆盖现有产物）。");
            }

            if (facts.SyntaxErrorCount > 0)
            {
                string detail = facts.SyntaxErrorFiles.Count > 0
                    ? "；首例：" + facts.SyntaxErrorFiles[0]
                    : string.Empty;
                model.Errors.Add("[扫描完整性] 扫描期检测到 " + facts.SyntaxErrorCount + " 处 C# 语法错误" + detail
                    + " —— 语法错误会让声明集不完整，拒绝生成/校验（禁止产出假空注册表覆盖现有产物）。");
            }
        }

        // ------------------------------------------------------------------ 规则 1

        private static void Rule1_NetworkObjectHost(
            PMDeclModel model,
            PMDeclRawFacts facts,
            Dictionary<string, PMDeclClass> declared)
        {
            for (int i = 0; i < facts.NonClassNetworkObjectHosts.Count; i++)
            {
                Err(model, 1, "[PMNetworkObject] 只能标注在 class 上：" + facts.NonClassNetworkObjectHosts[i]
                    + "（struct / interface / record 无法继承 PMNetObject，也不支持生成 partial 成员）");
            }

            // 嵌套类型上的 [PMNetworkObject]：扫描器只处理顶层类型，这个标记不会生效。
            // 不报就是 “写了声明、但完全不参与复制” 的静默漏扫。
            for (int i = 0; i < facts.NestedNetworkObjectTypes.Count; i++)
            {
                Err(model, 1, "[PMNetworkObject] 不得标注在**嵌套类型**上（扫描器只处理顶层类型，"
                    + "生成物是「顶层 partial class <TypeName>」，无法与嵌套类型合并）："
                    + facts.NestedNetworkObjectTypes[i]
                    + " —— 该声明不会生效，请把它提到命名空间层级");
            }

            for (int i = 0; i < model.Classes.Count; i++)
            {
                PMDeclClass cls = model.Classes[i];
                string where = Where(cls);

                if (!cls.IsPartial)
                {
                    Err(model, 1, "[PMNetworkObject] 的类必须是 partial（生成器要往同一个类里补成员）："
                        + cls.QualifiedName + where);
                }

                int derived = DerivesFromNetObject(cls, facts);
                if (derived == 0)
                {
                    Err(model, 1, "[PMNetworkObject] 的类推不到 PMNetObject："
                        + cls.QualifiedName + "（直接基类 "
                        + (string.IsNullOrEmpty(cls.BaseTypeName) ? "<无>" : cls.BaseTypeName)
                        + "）" + where);
                }
                else if (derived == 2)
                {
                    Warn(model, "无法在声明期证明 " + cls.QualifiedName + " 继承自 PMNetObject"
                        + "（基类 " + cls.BaseTypeName + " 不在本次扫描范围内，可能是其它程序集的中间基类）。"
                        + "已放行；若该基类最终不派生自 PMNetObject，会在运行时表现为类型注册失败。");
                }
            }
        }

        private static int DerivesFromNetObject(PMDeclClass cls, PMDeclRawFacts facts)
        {
            // 0 = 确定不是；1 = 确定是；2 = 无法证明
            string ns = cls.Namespace;
            string cur = cls.BaseTypeName;
            List<string> guard = new List<string>();

            while (!string.IsNullOrEmpty(cur))
            {
                string simple = SimpleName(cur);
                if (KnownNetBaseNames.Contains(simple) || KnownNetBaseNames.Contains(cur))
                {
                    return 1;
                }

                string resolved = PMDeclScanner.ResolveTypeName(facts, ns, cur);
                if (resolved == null || guard.Contains(resolved))
                {
                    return 2;
                }

                guard.Add(resolved);

                string baseText;
                if (!facts.DirectBaseTypes.TryGetValue(resolved, out baseText))
                {
                    return 2;
                }

                ns = NamespaceOf(resolved);
                cur = baseText;
            }

            return 0;
        }

        private static HashSet<string> BuildKnownNetBaseNames()
        {
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            Type root = typeof(PMNetObject);
            names.Add(root.Name);
            names.Add(root.FullName);

            Type[] types;
            try
            {
                types = root.Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types;
            }

            if (types == null)
            {
                return names;
            }

            for (int i = 0; i < types.Length; i++)
            {
                Type t = types[i];
                if (t == null || t.IsGenericTypeDefinition)
                {
                    continue;
                }

                if (!root.IsAssignableFrom(t))
                {
                    continue;
                }

                names.Add(t.Name);
                if (t.FullName != null)
                {
                    names.Add(t.FullName);
                }
            }

            return names;
        }

        // ------------------------------------------------------------------ 规则 2

        private static void Rule2_NoGeneric(PMDeclModel model, PMDeclRawFacts facts)
        {
            for (int i = 0; i < model.Classes.Count; i++)
            {
                PMDeclClass cls = model.Classes[i];
                if (facts.GenericTypes.Contains(PMDeclScanner.KeyOf(cls)))
                {
                    Err(model, 2, "[PMNetworkObject] 的类不得是泛型类（生成的静态表无法表达开放泛型）："
                        + cls.QualifiedName + Where(cls));
                }
            }
        }

        // ------------------------------------------------------------------ 规则 3

        private static void Rule3_ReplicatedModifiers(PMDeclModel model, Dictionary<string, PMDeclClass> declared)
        {
            for (int i = 0; i < model.Classes.Count; i++)
            {
                PMDeclClass cls = model.Classes[i];
                for (int k = 0; k < cls.Properties.Count; k++)
                {
                    PMDeclProperty p = cls.Properties[k];
                    if (!p.IsStatic && !p.IsReadOnly)
                    {
                        continue;
                    }

                    string why = p.IsStatic && p.IsReadOnly ? "static/const/readonly"
                        : (p.IsStatic ? "static" : "readonly");

                    Err(model, 3, "[PMReplicated] 成员不得是 " + why + "（复制层必须能写入）："
                        + cls.QualifiedName + "." + p.MemberName + Where(cls, p.Line));
                }
            }
        }

        // ------------------------------------------------------------------ 规则 4（含 §6 类型集）

        private static void Rule4_TypeSet(
            PMDeclModel model,
            PMDeclRawFacts facts,
            Dictionary<string, PMDeclClass> declared)
        {
            // 4a：复制成员类型
            for (int i = 0; i < model.Classes.Count; i++)
            {
                PMDeclClass cls = model.Classes[i];
                for (int k = 0; k < cls.Properties.Count; k++)
                {
                    PMDeclProperty p = cls.Properties[k];
                    string reason = PMTypeSet.DescribeUnsupported(p.TypeName, facts);
                    if (reason != null)
                    {
                        Err(model, 4, "[PMReplicated] 成员类型不在支持的类型集内："
                            + cls.QualifiedName + "." + p.MemberName + "（" + p.TypeName + "）—— " + reason
                            + Where(cls, p.Line));
                    }
                }
            }

            // 4b：RPC 参数类型（契约 §6：类型集"复制与 RPC 参数共用"）
            for (int i = 0; i < model.Classes.Count; i++)
            {
                PMDeclClass cls = model.Classes[i];
                for (int k = 0; k < cls.Rpcs.Count; k++)
                {
                    PMDeclRpc rpc = cls.Rpcs[k];
                    for (int pi = 0; pi < rpc.Parameters.Count; pi++)
                    {
                        PMDeclParam param = rpc.Parameters[pi];
                        string reason = PMTypeSet.DescribeUnsupported(param.TypeName, facts);
                        if (reason != null)
                        {
                            Err(model, 4, "RPC 参数类型不在支持的类型集内："
                                + cls.QualifiedName + "." + rpc.MethodName + " 的参数 " + param.Name
                                + "（" + param.TypeName + "）—— " + reason + Where(cls, rpc.Line));
                        }
                    }
                }
            }

            // 4c：标记在非 [PMNetworkObject] 类上的 [PMReplicated]（静默忽略会变成"声明了但不同步"）
            for (int i = 0; i < facts.OrphanReplicatedDeclarations.Count; i++)
            {
                Err(model, 4, "[PMReplicated] 所在类不是 [PMNetworkObject]，该声明会被完全忽略："
                    + facts.OrphanReplicatedDeclarations[i]);
            }
        }

        // ------------------------------------------------------------------ 规则 5 / 6

        private static void Rule5_RepNotifyForMember(
            PMDeclModel model,
            PMDeclRawFacts facts,
            Dictionary<string, PMDeclClass> declared)
        {
            HashSet<string> seenTargets = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < facts.RepNotifies.Count; i++)
            {
                PMDeclRepNotifyFact fact = facts.RepNotifies[i];
                PMDeclClass cls;
                if (!declared.TryGetValue(fact.ClassQualifiedName, out cls))
                {
                    Err(model, 5, "[PMRepNotify] 所在类不是 [PMNetworkObject]：" + fact.ClassQualifiedName
                        + "." + fact.MethodName + "（行 " + fact.Line + "）");
                    continue;
                }

                bool found = false;
                for (int k = 0; k < cls.Properties.Count; k++)
                {
                    if (string.Equals(cls.Properties[k].MemberName, fact.ForMember, StringComparison.Ordinal))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    Err(model, 5, "[PMRepNotify] 的 ForMember 找不到对应的 [PMReplicated] 成员："
                        + cls.QualifiedName + "." + fact.MethodName + " → ForMember=\"" + fact.ForMember + "\""
                        + Where(cls, fact.Line));
                    continue;
                }

                string target = cls.QualifiedName + "." + fact.ForMember;
                if (!seenTargets.Add(target))
                {
                    Warn(model, "同一个复制成员被多个 [PMRepNotify] 监听（只有先声明的那个会生效）：" + target);
                }
            }

            // 反向检查：IR 里的 OnRepMethodName 必须真实存在（防止扫描器与 IR 分叉）
            for (int i = 0; i < model.Classes.Count; i++)
            {
                PMDeclClass cls = model.Classes[i];
                for (int k = 0; k < cls.Properties.Count; k++)
                {
                    string onRep = cls.Properties[k].OnRepMethodName;
                    if (string.IsNullOrEmpty(onRep))
                    {
                        continue;
                    }

                    bool exists = false;
                    for (int f = 0; f < facts.RepNotifies.Count; f++)
                    {
                        if (string.Equals(facts.RepNotifies[f].ClassQualifiedName, cls.QualifiedName, StringComparison.Ordinal)
                            && string.Equals(facts.RepNotifies[f].MethodName, onRep, StringComparison.Ordinal))
                        {
                            exists = true;
                            break;
                        }
                    }

                    if (!exists)
                    {
                        Err(model, 5, "[PMRepNotify] 绑定不一致：" + cls.QualifiedName + "." + cls.Properties[k].MemberName
                            + " 指向的 " + onRep + " 不是一条 [PMRepNotify] 声明");
                    }
                }
            }
        }

        private static void Rule6_RepNotifySignature(
            PMDeclModel model,
            PMDeclRawFacts facts,
            Dictionary<string, PMDeclClass> declared)
        {
            for (int i = 0; i < facts.RepNotifies.Count; i++)
            {
                PMDeclRepNotifyFact fact = facts.RepNotifies[i];
                if (!declared.ContainsKey(fact.ClassQualifiedName))
                {
                    continue; // 已由规则 5 报过"所在类不是网络类"
                }

                if (fact.ParameterCount != 0)
                {
                    Err(model, 6, "[PMRepNotify] 目标方法必须无参：" + fact.ClassQualifiedName + "." + fact.MethodName
                        + " 有 " + fact.ParameterCount + " 个形参（行 " + fact.Line + "）");
                }

                if (!fact.ReturnsVoid)
                {
                    Err(model, 6, "[PMRepNotify] 目标方法必须返回 void：" + fact.ClassQualifiedName + "." + fact.MethodName
                        + "（行 " + fact.Line + "）");
                }
            }
        }

        // ------------------------------------------------------------------ 规则 7

        private static void Rule7_RpcSignature(
            PMDeclModel model,
            PMDeclRawFacts facts,
            Dictionary<string, PMDeclClass> declared)
        {
            // 7a：IR 级检查（static / 返回值）。
            //     IR 里的 IsStatic 与下面的语法事实同源，因此 static 只在这里报一次，
            //     语法级循环不再重复报（避免同一问题两条错误）。
            for (int i = 0; i < model.Classes.Count; i++)
            {
                PMDeclClass cls = model.Classes[i];
                for (int k = 0; k < cls.Rpcs.Count; k++)
                {
                    PMDeclRpc rpc = cls.Rpcs[k];
                    if (rpc.IsStatic)
                    {
                        Err(model, 7, "RPC 方法不得是 static（没有实例就没有 callspace 与归属判定）："
                            + cls.QualifiedName + "." + rpc.MethodName + Where(cls, rpc.Line));
                    }

                    if (!rpc.ReturnsVoid)
                    {
                        Err(model, 7, "RPC 方法必须返回 void（无返回值语义）："
                            + cls.QualifiedName + "." + rpc.MethodName + Where(cls, rpc.Line));
                    }
                }
            }

            // 7b：编织器（Tools/PMNetWeaver）明确拒绝的方法形态。
            //
            // 这些形态在**编译后的编织阶段**必然失败（net-rpc-weaving-contract.md §3），
            // 但那时生成物已经写出去、业务也已经照着它编译过了 ---- 失败点离声明点太远。
            // 因此在声明层直接报错，让“不支持”变成生成前的硬错误。
            for (int i = 0; i < facts.Rpcs.Count; i++)
            {
                PMDeclRpcFact fact = facts.Rpcs[i];

                PMDeclClass cls;
                if (!declared.TryGetValue(fact.ClassQualifiedName, out cls))
                {
                    continue; // 非网络类上的声明由 7c 报
                }

                string head = cls.QualifiedName + "." + fact.MethodName;
                string where = Where(cls, fact.Line);

                if (fact.HasOverload)
                {
                    Err(model, 7, "RPC 方法不得有同名重载（编织器按方法名定位，不猜重载/重写链）："
                        + head + where);
                }

                if (fact.MultipleRpcAttributes)
                {
                    Err(model, 7, "同一个方法不得带多个 RPC 标记（会生成多个同名 helper ⇒ 直接编译失败）："
                        + head + where);
                }

                if (fact.IsVirtual)
                {
                    Err(model, 7, "RPC 方法不得是 virtual（编织器不处理虚方法/网络继承，不猜重写继承链）："
                        + head + where);
                }

                if (fact.IsAbstract)
                {
                    Err(model, 7, "RPC 方法不得是 abstract（必须直接写业务体，没有可拆的实现）："
                        + head + where);
                }

                if (fact.IsExtern)
                {
                    Err(model, 7, "RPC 方法不得是 extern/native（必须有托管业务体才能拆）："
                        + head + where);
                }

                if (fact.IsAsync)
                {
                    Err(model, 7, "RPC 方法不得是 async（状态机会改写方法体，编织器明确拒绝）："
                        + head + where);
                }

                if (fact.HasTypeParameters)
                {
                    Err(model, 7, "RPC 方法不得是泛型方法：" + head + where);
                }

                if (!fact.HasBody && !fact.IsAbstract && !fact.IsExtern)
                {
                    // abstract / extern 本来就没有托管业务体，已由上面两条报出；
                    // 这里只报“本当有体却没写”（`void M();`）的情形，避免同一问题两条错误。
                    Err(model, 7, "RPC 方法必须有方法体（块体或表达式体）：" + head
                        + " —— 契约要求直接在普通方法里写业务体" + where);
                }

                for (int k = 0; k < fact.UnsupportedParamModifiers.Count; k++)
                {
                    Err(model, 7, "RPC 形参不支持 " + fact.UnsupportedParamModifiers[k]
                        + "（ref/out/in 按引用、params 变长、default 默认值都无法表达线上参数布局）："
                        + head + where);
                }
            }

            // 7c：声明在非 [PMNetworkObject] 类上的 RPC（完全忽略 = 静默缺陷）
            for (int i = 0; i < facts.OrphanRpcDeclarations.Count; i++)
            {
                Err(model, 7, "RPC 声明所在类不是 [PMNetworkObject]，该声明会被完全忽略："
                    + facts.OrphanRpcDeclarations[i]);
            }
        }

        // ------------------------------------------------------------------ 规则 8 / 9

        private static void Rule8_ServerRpcValidation(
            PMDeclModel model,
            PMDeclRawFacts facts,
            Dictionary<string, PMDeclClass> declared)
        {
            for (int i = 0; i < model.Classes.Count; i++)
            {
                PMDeclClass cls = model.Classes[i];
                for (int k = 0; k < cls.Rpcs.Count; k++)
                {
                    PMDeclRpc rpc = cls.Rpcs[k];
                    if (rpc.Kind != PMRpcKind.Server)
                    {
                        continue;
                    }

                    if (rpc.Validator != PMRpcValidator.None)
                    {
                        continue;
                    }

                    Err(model, 8, "[PMServerRpc] 必须声明校验（项目反外挂红线，对齐 UHT 对 Server RPC 的强制要求）："
                        + cls.QualifiedName + "." + rpc.MethodName
                        + " —— 请显式写 `Validator = PMRpcValidator.ForceValidate`（三态，Reject 不断连）、"
                        + "`Validator = PMRpcValidator.Validate` 或 `WithValidation = true`（失败即断连），"
                        + "或提供同名 `" + rpc.MethodName + "_ForceValidate` 同伴方法。"
                        + Where(cls, rpc.Line));
                }
            }
        }

        private static void Rule9_ValidatorConflict(
            PMDeclModel model,
            PMDeclRawFacts facts,
            Dictionary<string, PMDeclClass> declared)
        {
            for (int i = 0; i < facts.Rpcs.Count; i++)
            {
                PMDeclRpcFact fact = facts.Rpcs[i];
                if (!fact.WithValidationExplicit || fact.ValidatorExplicit != PMRpcValidator.ForceValidate)
                {
                    continue;
                }

                Err(model, 9, "`WithValidation = true` 与 `Validator = PMRpcValidator.ForceValidate` 不得同时出现"
                    + "（D-R0-45：前者失败即断连、后者 Reject 只跳过实现，同时写会让「校验失败后断不断连」不确定）："
                    + fact.ClassQualifiedName + "." + fact.MethodName + "（行 " + fact.Line + "）");
            }
        }

        // ------------------------------------------------------------------ 规则 10

        private static void Rule10_IdUniqueness(PMDeclModel model)
        {
            Dictionary<uint, string> classIds = new Dictionary<uint, string>();

            for (int i = 0; i < model.Classes.Count; i++)
            {
                PMDeclClass cls = model.Classes[i];

                string owner;
                if (classIds.TryGetValue(cls.ClassId, out owner))
                {
                    Err(model, 10, "ClassId 重复（含跨程序集）：" + cls.ClassId
                        + " 同时被 " + owner + " 与 " + cls.QualifiedName + " 使用");
                }
                else
                {
                    classIds.Add(cls.ClassId, cls.QualifiedName);
                }

                Dictionary<ushort, string> propIds = new Dictionary<ushort, string>();
                Dictionary<string, ushort> propNames = new Dictionary<string, ushort>(StringComparer.Ordinal);

                for (int k = 0; k < cls.Properties.Count; k++)
                {
                    PMDeclProperty p = cls.Properties[k];

                    string prev;
                    if (propIds.TryGetValue(p.PropertyId, out prev))
                    {
                        Err(model, 10, "同一类内 PropertyId 重复：" + cls.QualifiedName + " 的 "
                            + prev + " 与 " + p.MemberName + " 都用 " + p.PropertyId
                            + "（线协议歧义）" + Where(cls, p.Line));
                    }
                    else
                    {
                        propIds.Add(p.PropertyId, p.MemberName);
                    }

                    ushort sameNameId;
                    if (propNames.TryGetValue(p.MemberName, out sameNameId))
                    {
                        Err(model, 10, "同一类内成员重复声明：" + cls.QualifiedName + "." + p.MemberName
                            + "（例如拆在多个 partial 部分里各写了一次），稳定键相同 ⇒ PropertyId 相同 = "
                            + p.PropertyId + Where(cls, p.Line));
                    }
                    else
                    {
                        propNames.Add(p.MemberName, p.PropertyId);
                    }
                }

                Dictionary<ushort, string> rpcIds = new Dictionary<ushort, string>();
                for (int k = 0; k < cls.Rpcs.Count; k++)
                {
                    PMDeclRpc r = cls.Rpcs[k];
                    string prev;
                    if (rpcIds.TryGetValue(r.RpcId, out prev))
                    {
                        Err(model, 10, "同一类内 RpcId 重复：" + cls.QualifiedName + " 的 "
                            + prev + " 与 " + r.MethodName + " 都用 " + r.RpcId + Where(cls, r.Line));
                    }
                    else
                    {
                        rpcIds.Add(r.RpcId, r.MethodName);
                    }
                }

                // 生成的访问器名冲突：`_hp` 与 `hp` 稳定键不同（所以 ID 不重复），
                // 但都会生成 `PMNet_Set_hp` ⇒ 重复方法、编译失败。这类冲突必须在声明期拦下。
                Dictionary<string, string> setters = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int k = 0; k < cls.Properties.Count; k++)
                {
                    PMDeclProperty p = cls.Properties[k];
                    string accessor = "PMNet_Set" + p.MemberName;
                    string key = accessor.TrimStart('_');
                    string prev;
                    if (setters.TryGetValue(key, out prev))
                    {
                        Err(model, 10, "生成的访问器名冲突：" + cls.QualifiedName + " 的 " + prev
                            + " 与 " + p.MemberName + " 都会生成 " + accessor
                            + "（同名成员，或成员名只差前导下划线时，会撞成同一个方法名 ⇒ 直接编译失败）"
                            + Where(cls, p.Line));
                    }
                    else
                    {
                        setters.Add(key, p.MemberName);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ 规则 11

        private static void Rule11_ConditionSet(PMDeclModel model, PMDeclRawFacts facts)
        {
            string[] allowed = PMCondNames.ImplementedNames();

            for (int i = 0; i < model.Classes.Count; i++)
            {
                PMDeclClass cls = model.Classes[i];
                for (int k = 0; k < cls.Properties.Count; k++)
                {
                    PMDeclProperty p = cls.Properties[k];
                    string source;
                    if (!facts.ConditionSources.TryGetValue(cls.QualifiedName + "." + p.MemberName, out source))
                    {
                        continue; // 未写条件 = PMCond.None，合法
                    }

                    PMCond resolved;
                    if (PMCondNames.TryResolve(source, out resolved))
                    {
                        continue;
                    }

                    int unimplemented;
                    string hint = PMCondNames.IsUnimplementedName(source, out unimplemented)
                        ? "该条件属于 D-R0-14 明确不做的 9 项（UE 原值 " + unimplemented + "），不做静默降级"
                        : "无法解析该条件表达式";

                    Err(model, 11, "复制条件不在首版允许的 8 项内：" + cls.QualifiedName + "." + p.MemberName
                        + " 写了 `" + source + "` —— " + hint + "。允许值："
                        + string.Join(" / ", allowed) + Where(cls, p.Line));
                }
            }
        }

        // ------------------------------------------------------------------ 规则 13

        /// <summary>
        /// 规则 13：声明的校验档位必须有可调用的同伴方法。
        ///
        /// 发射器为 `ForceValidate` 生成的是对 `<M>_ForceValidate` 的调用、为 `Validate`
        /// 生成的是对 `<M>_Validate` 的调用。同伴不存在时若只是"不生成调用"，
        /// 就会留下**声称有校验、实际没有**的路径 —— 那正是缺陷 2 在同一层级的重演，
        /// 因此这里直接报错（D-R0-50：声明错误编译期失败）。
        ///
        /// 同伴的返回类型也是契约的一部分：
        /// - `<M>_ForceValidate` 必须返回 `PMRpcValidation`（三态）；
        /// - `<M>_Validate` 必须返回 `bool`（false ⇒ 请求断连）。
        /// 返回类型不符时不报硬错（语法层拿不到可靠类型信息），改为告警 + 由编译期兜底。
        /// </summary>
        private static void Rule13_ValidatorCompanion(
            PMDeclModel model,
            PMDeclRawFacts facts,
            Dictionary<string, PMDeclClass> declared)
        {
            for (int i = 0; i < facts.Rpcs.Count; i++)
            {
                PMDeclRpcFact fact = facts.Rpcs[i];

                PMDeclClass cls;
                if (!declared.TryGetValue(fact.ClassQualifiedName, out cls))
                {
                    continue;
                }

                PMDeclRpc rpc = FindRpc(cls, fact.MethodName);
                if (rpc == null)
                {
                    continue;
                }

                if (rpc.Validator == PMRpcValidator.ForceValidate && !fact.HasForceValidateCompanion)
                {
                    Err(model, 13,
                        "校验档位声明为 ForceValidate，但没有同伴方法 `" + fact.MethodName + "_ForceValidate`："
                        + cls.QualifiedName + "." + fact.MethodName
                        + " —— 发射器会为它生成对该同伴的调用；同伴不存在就只剩两条路："
                        + "调用不存在的方法（编译失败），或退化成「声称有校验、实际没有」。"
                        + "请补上 `PMRpcValidation " + fact.MethodName + "_ForceValidate(...)`，"
                        + "或把档位改成 None（仅 Client/Multicast 允许）。"
                        + Where(cls, fact.Line));
                    continue;
                }

                if (rpc.Validator == PMRpcValidator.Validate && !fact.HasValidateCompanion)
                {
                    Err(model, 13,
                        "校验档位声明为 Validate（含 WithValidation = true），但没有同伴方法 `"
                        + fact.MethodName + "_Validate`：" + cls.QualifiedName + "." + fact.MethodName
                        + " —— 请补上 `bool " + fact.MethodName + "_Validate(...)`（返回 false ⇒ 请求断连），"
                        + "或改用 `Validator = PMRpcValidator.ForceValidate` + `_ForceValidate` 同伴。"
                        + Where(cls, fact.Line));
                }
            }
        }

        /// <summary>按方法名在类里找 IR 里的 RPC。</summary>
        private static PMDeclRpc FindRpc(PMDeclClass cls, string methodName)
        {
            for (int i = 0; i < cls.Rpcs.Count; i++)
            {
                if (cls.Rpcs[i].MethodName == methodName)
                {
                    return cls.Rpcs[i];
                }
            }

            return null;
        }

        // ------------------------------------------------------------------ 规则 14

        /// <summary>
        /// 规则 14：`[PMReplicated]` 的属性必须是**普通实例自动属性**（get;set;，
        /// 允许访问性不同与初始化器），且标记不得放在不支持的成员种类上。
        ///
        /// 为什么必须在声明期硬失败：
        /// <list type="bullet">
        ///   <item>不支持的属性形态（自定义 getter/setter、只读、indexer、virtual/override/
        ///         abstract、ref-return、显式接口实现）在编织期**必然失败或产生静默错语义**：
        ///         例如只读属性没有 setter 就没有可改写点，自定义 setter 改写后会丢掉业务副作用；</item>
        ///   <item>标记放到 event / delegate 这类成员上时，声明根本进不了 IR，
        ///         后期没有任何一层会看到它 —— 那就是「写了标记、但完全不参与复制」的静默缺陷。</item>
        /// </list>
        ///
        /// 字段（旧模式）不参与本规则：字段没有“自定义访问器”一说，它的
        /// static / const / readonly 已由规则 3 报出，类型由规则 4 报出。
        /// </summary>
        private static void Rule14_ReplicatedPropertyShape(PMDeclModel model, PMDeclRawFacts facts)
        {
            // 14a：IR 里的每个复制成员都必须有对应的声明形态事实。
            //
            // 这是“扫描器与 IR 分叉”的反向核对：IR 里有、facts 里没有，意味着
            // 扫描器把某个成员收进了 IR 却没记下它的形态（发射器与校验器会因此盲判）。
            // 只在**真的扫过源码**时才要求（ParsedFileCount > 0）：门禁与其它工具会
            // 手工构造模型（没有源码，也就没有形态事实）。
            if (facts.ParsedFileCount > 0)
            {
                for (int i = 0; i < model.Classes.Count; i++)
                {
                    PMDeclClass cls = model.Classes[i];
                    for (int k = 0; k < cls.Properties.Count; k++)
                    {
                        PMDeclProperty p = cls.Properties[k];
                        if (!facts.PropertyFacts.ContainsKey(cls.QualifiedName + "." + p.MemberName))
                        {
                            Err(model, 14, "[PMReplicated] 成员缺少声明形态事实（扫描器与 IR 分叉）："
                                + cls.QualifiedName + "." + p.MemberName
                                + " —— 形态不可证 ⇒ 无法保证生成物与声明一致" + Where(cls, p.Line));
                        }
                    }
                }
            }

            // 14b：不支持形态的**属性**（含 indexer，它不进 IR，因此遍历 facts 而不是遍历 IR）。
            //      static 已在规则 3 报出，这里跳过以免同一问题两条错误。
            List<string> keys = new List<string>(facts.PropertyFacts.Keys);
            keys.Sort(StringComparer.Ordinal);
            for (int i = 0; i < keys.Count; i++)
            {
                PMDeclPropertyFact fact = facts.PropertyFacts[keys[i]];
                if (fact.IsField || fact.IsStatic)
                {
                    continue;
                }

                List<string> reasons = fact.DescribeUnsupportedShapes();
                for (int r = 0; r < reasons.Count; r++)
                {
                    Err(model, 14, "[PMReplicated] 属性 " + fact.ClassQualifiedName + "." + fact.MemberName
                        + " 不是受支持的普通实例自动属性（get;set;）：" + reasons[r]
                        + "（契约 Docs/plans/net-property-authoring-contract.md §1）"
                        + "（行 " + fact.Line + "）");
                }
            }

            // 14c：标记放在不支持的成员种类上（event / delegate / 等）。
            //      这些成员根本不会被收进 IR —— 不在这里报，就永远没人报。
            for (int i = 0; i < facts.ReplicatedOnUnsupportedMembers.Count; i++)
            {
                Err(model, 14, "[PMReplicated] 声明位置不支持：" + facts.ReplicatedOnUnsupportedMembers[i]
                    + "（契约 Docs/plans/net-property-authoring-contract.md §1：只支持普通实例字段与"
                    + "普通实例 auto-property）");
            }

            // 14d：**嵌套类型**成员上的 [PMReplicated] / [PMQuantized]。
            //      与 14c 同理：扫描器只处理顶层类型，这类成员根本不会被收进 IR。
            for (int i = 0; i < facts.NestedReplicatedMembers.Count; i++)
            {
                Err(model, 14, "[PMReplicated] 声明在**嵌套类型**里（扫描器只处理顶层类型，该声明不会生效）："
                    + facts.NestedReplicatedMembers[i]
                    + " —— 请把声明所在的类提到命名空间层级");
            }
        }

        // ------------------------------------------------------------------ 规则 12

        private static void Rule12_MaskLayout(PMDeclModel model)
        {
            for (int i = 0; i < model.Classes.Count; i++)
            {
                PMDeclClass cls = model.Classes[i];

                if (cls.ChangeMaskBitCount < 0 || cls.ChangeMaskBitCount > (1 << 20))
                {
                    Err(model, 12, "变更掩码位数异常：" + cls.QualifiedName + " 的 ChangeMaskBitCount = "
                        + cls.ChangeMaskBitCount);
                    continue;
                }

                bool[] used = new bool[cls.ChangeMaskBitCount];
                int total = 0;

                List<PMDeclProperty> sorted = new List<PMDeclProperty>(cls.Properties);
                sorted.Sort(delegate (PMDeclProperty a, PMDeclProperty b)
                {
                    int byOffset = a.MaskOffset.CompareTo(b.MaskOffset);
                    return byOffset != 0 ? byOffset : string.CompareOrdinal(a.MemberName, b.MemberName);
                });

                for (int k = 0; k < sorted.Count; k++)
                {
                    PMDeclProperty p = sorted[k];
                    if (p.MaskBitCount == 0)
                    {
                        Err(model, 12, "变更掩码位数为 0：" + cls.QualifiedName + "." + p.MemberName);
                        continue;
                    }

                    int end = p.MaskOffset + p.MaskBitCount;
                    if (end > cls.ChangeMaskBitCount)
                    {
                        Err(model, 12, "变更掩码区间越界：" + cls.QualifiedName + "." + p.MemberName
                            + " 占用位 [" + p.MaskOffset + ", " + end + ")，但 ChangeMaskBitCount = "
                            + cls.ChangeMaskBitCount);
                        continue;
                    }

                    for (int b = p.MaskOffset; b < end; b++)
                    {
                        if (used[b])
                        {
                            Err(model, 12, "变更掩码区间重叠：" + cls.QualifiedName + " 的位 " + b
                                + " 被多个属性占用（" + p.MemberName + "）");
                        }

                        used[b] = true;
                    }

                    total += p.MaskBitCount;
                }

                if (total != cls.ChangeMaskBitCount)
                {
                    Err(model, 12, "变更掩码位数与属性占用不一致：" + cls.QualifiedName
                        + " 的属性合计 " + total + " 位，但 ChangeMaskBitCount = " + cls.ChangeMaskBitCount);
                }
            }
        }

        // ------------------------------------------------------------------ 告警

        private static void EmitWarnings(
            PMDeclModel model,
            PMDeclRawFacts facts,
            Dictionary<string, PMDeclClass> declared)
        {
            // 注：旧版这里对「[PMReplicated] 属性没有 set 访问器」发告警。
            // 自动属性复制开工后它升级为**硬错误**（规则 14：只读属性没有可改写的 setter），
            // 因此不再重复告警 —— 告警不阻断，而这一条必须阻断。
            for (int i = 0; i < facts.QuantizedWithoutReplicated.Count; i++)
            {
                Warn(model, "[PMQuantized] 没有配套的 [PMReplicated]，量化器不会生效（量化只对已复制成员有意义）："
                    + facts.QuantizedWithoutReplicated[i]);
            }

            for (int i = 0; i < model.Classes.Count; i++)
            {
                PMDeclClass cls = model.Classes[i];
                string key = PMDeclScanner.KeyOf(cls);

                if (facts.AbstractTypes.Contains(key) || facts.TypesWithoutUsableCtor.Contains(key))
                {
                    Warn(model, "无法为 " + cls.QualifiedName + " 生成构造工厂（"
                        + (facts.AbstractTypes.Contains(key) ? "类型是 abstract" : "没有可用的无参构造")
                        + "）：生成物里 Factory 置 null，需要注册方自行提供实例化方式");
                }

                if (facts.TypesWithOwnRepListOverride.Contains(key))
                {
                    Warn(model, "类已自行覆写 CollectLifetimeReplicatedProps：" + cls.QualifiedName
                        + " —— 生成代码会再覆写一次，产生 CS0111（重复成员）。请把它从业务代码里去掉，改由生成物负责注册");
                }
            }

            for (int i = 0; i < facts.Rpcs.Count; i++)
            {
                PMDeclRpcFact fact = facts.Rpcs[i];
                if (fact.HasValidateCompanion && fact.ValidatorExplicit == PMRpcValidator.None
                    && !fact.WithValidationExplicit && !fact.HasForceValidateCompanion)
                {
                    Warn(model, fact.ClassQualifiedName + "." + fact.MethodName + " 只有 `_Validate` 同伴，"
                        + "但规则 8 只承认 `_ForceValidate` 同伴或显式 Validator 档位；"
                        + "若本意是 UE 原生校验请显式写 `WithValidation = true`");
                }

                if (fact.MethodName.EndsWith("_Implementation", StringComparison.Ordinal))
                {
                    Warn(model, "RPC 方法名以 `_Implementation` 结尾：" + fact.ClassQualifiedName + "." + fact.MethodName
                        + " —— R2 契约 §4.3 的约定是把属性直接写在业务方法上、由生成器另出 `PMNet_<方法名>` 调用桩；"
                        + "当前按字面方法名生成，请确认这是有意的（迁移计划 §3.9.5 的示例用的是另一种写法）");
                }
            }

            if (facts.SyntaxErrorCount > 0)
            {
                // 语法错误已在 ScanIntegrityGate 升级为**错误**（拒绝生成），
                // 这里不再重复告警：告警不会阻断生成，而这条必须阻断。
                Warn(model, "扫描期检测到 " + facts.SyntaxErrorCount + " 处 C# 语法错误（已由扫描完整性门拒绝生成）");
            }

            if (facts.ParsedFileCount == 0)
            {
                Warn(model, "没有扫描到任何 .cs 文件（ParsedFileCount = 0）——门禁会因此判定失败，"
                    + "不允许把「0 个文件被扫描」当作通过");
            }
        }

        // ------------------------------------------------------------------ 工具

        private static void Err(PMDeclModel model, int rule, string message)
        {
            model.Errors.Add("[规则 " + rule + "] " + message);
        }

        private static void Warn(PMDeclModel model, string message)
        {
            model.Warnings.Add("[告警] " + message);
        }

        private static string Where(PMDeclClass cls)
        {
            return Where(cls, 0);
        }

        private static string Where(PMDeclClass cls, int line)
        {
            string path = string.IsNullOrEmpty(cls.FilePath) ? "<未知源文件>" : cls.FilePath;
            int effective = line > 0 ? line : cls.Line;
            return "（" + path + ":" + effective + "）";
        }

        private static string SimpleName(string text)
        {
            string t = PMTypeSet.Normalize(text);
            int dot = t.LastIndexOf('.');
            return dot >= 0 ? t.Substring(dot + 1) : t;
        }

        private static string NamespaceOf(string qualifiedName)
        {
            int dot = qualifiedName.LastIndexOf('.');
            return dot >= 0 ? qualifiedName.Substring(0, dot) : string.Empty;
        }
    }
}
