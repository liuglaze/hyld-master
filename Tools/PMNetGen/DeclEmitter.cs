using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PMNet;
using PMNet.Codegen;

namespace PMNetGen
{
    // =========================================================================================
    //  R2 代码发射器（契约 §4 的生成物 API 面）
    //
    //  硬约束（每一条都有对应的门禁断言）：
    //    1. 每类一个 `PMNet.<命名空间>.<类型名>.g.cs`；命名空间与该类相同；
    //    2. 每程序集一个 `PMNetGeneratedRegistry.g.cs`；
    //    3. 产物必须能在 **C# 7.3 + netstandard2.0** 下编译（Unity 2019.4，D-R0-48）——
    //       不用插值字符串、不用 `nameof` 之外的现代语法糖、不用 Span/ValueTuple 等新 API；
    //    4. `RegisterAll()` **显式列出每个类**，禁止反射扫描（D-R0-48 / T40 的断言）；
    //    5. **确定性**：同样输入两次生成逐字节相同；源文件重排后逐字节相同（契约 §2.3）。
    //       因此这里不写时间戳、不写绝对路径、不依赖任何字典的枚举顺序。
    //
    //  PMNet 运行时类型一律写**全限定名**（`PMNet.PMNetObject` 等），这样生成的 partial
    //  即使源文件没有 `using PMNet;` 也能编译；同时仍把源文件的 using 复制过来，
    //  以便成员类型里出现的自定义 enum / 外部命名空间类型能解析。
    // =========================================================================================

    /// <summary>一个待写盘的生成产物。</summary>
    public sealed class PMGeneratedFile
    {
        /// <summary>文件名（不含目录）。</summary>
        public string FileName;

        /// <summary>文本内容（LF 行尾；写盘时统一转 UTF-8 BOM + CRLF）。</summary>
        public string Content;
    }

    /// <summary>把 <see cref="PMDeclModel"/> 发射成生成物。</summary>
    public static class PMDeclEmitter
    {
        /// <summary>注册表文件名（每程序集一个）。</summary>
        public const string RegistryFileName = "PMNetGeneratedRegistry.g.cs";

        /// <summary>复制数组的长度上限（契约 §8 把它列为"待收敛参数"，R2 取保守值）。</summary>
        public const int MaxArrayLength = 4096;

        private const string Lf = "\n";

        /// <summary>按契约 §4 的命名规则给出类文件名。</summary>
        public static string FileNameFor(PMDeclClass cls)
        {
            string ns = string.IsNullOrEmpty(cls.Namespace) ? string.Empty : cls.Namespace + ".";
            return "PMNet." + ns + cls.TypeName + ".g.cs";
        }

        /// <summary>发射全部产物（按 ClassId 升序 + 注册表在最后）。</summary>
        public static List<PMGeneratedFile> EmitAll(PMDeclModel model, PMDeclRawFacts facts)
        {
            if (model == null)
            {
                throw new ArgumentNullException("model");
            }

            if (facts == null)
            {
                facts = new PMDeclRawFacts();
            }

            List<PMGeneratedFile> files = new List<PMGeneratedFile>();
            List<PMDeclClass> classes = new List<PMDeclClass>(model.Classes);
            classes.Sort(delegate (PMDeclClass a, PMDeclClass b)
            {
                int byId = a.ClassId.CompareTo(b.ClassId);
                return byId != 0 ? byId : string.CompareOrdinal(a.QualifiedName, b.QualifiedName);
            });

            for (int i = 0; i < classes.Count; i++)
            {
                PMGeneratedFile f = new PMGeneratedFile();
                f.FileName = FileNameFor(classes[i]);
                f.Content = EmitClass(classes[i], facts);
                files.Add(f);
            }

            PMGeneratedFile registry = new PMGeneratedFile();
            registry.FileName = RegistryFileName;
            registry.Content = EmitRegistry(model);
            files.Add(registry);

            return files;
        }

        /// <summary>发射注册表文件（空模型也要产出——它承载 RemoteSender 接缝与封板逻辑）。</summary>
        public static string EmitRegistry(PMDeclModel model)
        {
            List<PMDeclClass> classes = new List<PMDeclClass>(model.Classes);
            classes.Sort(delegate (PMDeclClass a, PMDeclClass b)
            {
                int byId = a.ClassId.CompareTo(b.ClassId);
                return byId != 0 ? byId : string.CompareOrdinal(a.QualifiedName, b.QualifiedName);
            });

            StringBuilder sb = new StringBuilder(8192);
            Header(sb, "程序集注册表",
                "类数：" + classes.Count.ToString(CultureInfo.InvariantCulture),
                "协议摘要（生成期）：0x" + model.GlobalProtocolHash.ToString("X8", CultureInfo.InvariantCulture));

            sb.Append("using System;").Append(Lf);
            sb.Append("using System.Collections.Generic;").Append(Lf);
            sb.Append(Lf);
            sb.Append("namespace PMNet.Generated").Append(Lf);
            sb.Append("{").Append(Lf);
            sb.Append("    /// <summary>一条已被判定为「需要发往远端」的 RPC 调用（参数编码委托 + 目标对象）。</summary>").Append(Lf);
            sb.Append("    public struct PMNetPendingRpc").Append(Lf);
            sb.Append("    {").Append(Lf);
            sb.Append("        /// <summary>调用方对象。</summary>").Append(Lf);
            sb.Append("        public PMNet.PMNetObject Target;").Append(Lf);
            sb.Append(Lf);
            sb.Append("        /// <summary>RPC ID。</summary>").Append(Lf);
            sb.Append("        public ushort RpcId;").Append(Lf);
            sb.Append(Lf);
            sb.Append("        /// <summary>参数编码委托；**必须在调用它的同步栈上完成编码**（实参帧不做长期保存）。</summary>").Append(Lf);
            sb.Append("        public PMNet.PMRpcWriter Write;").Append(Lf);
            sb.Append("    }").Append(Lf);
            sb.Append(Lf);
            sb.Append("    /// <summary>").Append(Lf);
            sb.Append("    /// 本程序集生成产物的注册表。").Append(Lf);
            sb.Append("    ///").Append(Lf);
            sb.Append("    /// **RegisterAll 显式列出每个类，不做任何反射扫描**（D-R0-48 / T40）：").Append(Lf);
            sb.Append("    /// Unity 2019.4 的 Mono/IL2CPP 下反射扫描既慢又会被裁剪，").Append(Lf);
            sb.Append("    /// 而「哪些类型参与网络」必须是编译期可知的。").Append(Lf);
            sb.Append("    /// </summary>").Append(Lf);
            sb.Append("    public static class PMNetGeneratedRegistry").Append(Lf);
            sb.Append("    {").Append(Lf);
            sb.Append("        /// <summary>本程序集显式注册的网络类数量。</summary>").Append(Lf);
            sb.Append("        public const int GeneratedClassCount = ").Append(classes.Count).Append(";").Append(Lf);
            sb.Append(Lf);
            sb.Append("        /// <summary>").Append(Lf);
            sb.Append("        /// 远端发送接缝：M05/R2-B 的原生 RPC 发送路径接线点。").Append(Lf);
            sb.Append("        /// 为 null 时，`EnqueueRemote` 把调用放进本类的待发队列（供端到端测试断言）。").Append(Lf);
            sb.Append("        /// 非 null 时必须**同步**完成编码（实参由闭包持有，编码后即释放）。").Append(Lf);
            sb.Append("        /// </summary>").Append(Lf);
            sb.Append("        public static Action<PMNet.PMNetObject, ushort, PMNet.PMRpcWriter> RemoteSender;").Append(Lf);
            sb.Append(Lf);
            sb.Append("        private static readonly Queue<PMNetPendingRpc> _pendingRpcs = new Queue<PMNetPendingRpc>(64);").Append(Lf);
            sb.Append(Lf);
            sb.Append("        /// <summary>待发队列长度（RemoteSender 未接线时的诊断口径）。</summary>").Append(Lf);
            sb.Append("        public static int PendingRpcCount { get { return _pendingRpcs.Count; } }").Append(Lf);
            sb.Append(Lf);
            sb.Append("        /// <summary>").Append(Lf);
            sb.Append("        /// 待发队列上限（D-R0-18：复制队列与内存必须有界）。").Append(Lf);
            sb.Append("        ///").Append(Lf);
            sb.Append("        /// 这个队列**只在 RemoteSender 未接线的开发/测试形态下才有内容**；").Append(Lf);
            sb.Append("        /// 生产接线后它恒空，因此上限是防跑飞的安全网，而不是正常工作流的一部分。").Append(Lf);
            sb.Append("        /// 超限时**显式失败、不接受新调用**，绝不淘汰已接受的调用：").Append(Lf);
            sb.Append("        /// 可靠 RPC 的语义是「保序且要求送达」（D-R0-06），可靠缓冲溢出等价于断连（D-R0-07），").Append(Lf);
            sb.Append("        /// 因此不能沿用投射物式的「容量满丢最旧」——那等于静默丢一条已承诺送达的调用。").Append(Lf);
            sb.Append("        /// 宿主应把 RejectedPendingRpcs 当告警消费：非 0 说明发送路径没接线。").Append(Lf);
            sb.Append("        /// </summary>").Append(Lf);
            sb.Append("        public const int MaxPendingRpcs = 1024;").Append(Lf);
            sb.Append(Lf);
            sb.Append("        /// <summary>因队列已满而被**拒绝**的调用数（非 0 ⇒ 发送路径没接线，属配置问题）。</summary>").Append(Lf);
            sb.Append("        public static long RejectedPendingRpcs;").Append(Lf);
            sb.Append(Lf);
            sb.Append("        /// <summary>记录一条需要发往远端的 RPC（由生成桩调用）。</summary>").Append(Lf);
            sb.Append("        public static void EnqueueRemote(PMNet.PMNetObject target, ushort rpcId, PMNet.PMRpcWriter write)").Append(Lf);
            sb.Append("        {").Append(Lf);
            sb.Append("            if (target == null || write == null)").Append(Lf);
            sb.Append("            {").Append(Lf);
            sb.Append("                return;").Append(Lf);
            sb.Append("            }").Append(Lf);
            sb.Append(Lf);
            sb.Append("            if (RemoteSender != null)").Append(Lf);
            sb.Append("            {").Append(Lf);
            sb.Append("                RemoteSender(target, rpcId, write);").Append(Lf);
            sb.Append("                return;").Append(Lf);
            sb.Append("            }").Append(Lf);
            sb.Append(Lf);
            sb.Append("            if (_pendingRpcs.Count >= MaxPendingRpcs)").Append(Lf);
            sb.Append("            {").Append(Lf);
            sb.Append("                RejectedPendingRpcs++;").Append(Lf);
            sb.Append(Lf);
            sb.Append("                // 不接受新调用：队列内容与顺序原样保留（已接受的调用一条都不能被淘汰）。").Append(Lf);
            sb.Append("                throw new InvalidOperationException(").Append(Lf);
            sb.Append("                    \"PMNet RPC 待发队列已满（上限 \" + MaxPendingRpcs").Append(Lf);
            sb.Append("                    + \"）：本次调用被拒绝，队列里已接受的调用保持不变。\"").Append(Lf);
            sb.Append("                    + \"可靠 RPC 不允许淘汰已接受的调用（D-R0-06 / D-R0-07）；\"").Append(Lf);
            sb.Append("                    + \"出现这一条说明发送路径（RemoteSender）没有接线。\");").Append(Lf);
            sb.Append("            }").Append(Lf);
            sb.Append(Lf);
            sb.Append("            PMNetPendingRpc pending = new PMNetPendingRpc();").Append(Lf);
            sb.Append("            pending.Target = target;").Append(Lf);
            sb.Append("            pending.RpcId = rpcId;").Append(Lf);
            sb.Append("            pending.Write = write;").Append(Lf);
            sb.Append("            _pendingRpcs.Enqueue(pending);").Append(Lf);
            sb.Append("        }").Append(Lf);
            sb.Append(Lf);
            sb.Append("        /// <summary>取出一条待发 RPC（先进先出）。</summary>").Append(Lf);
            sb.Append("        public static bool TryDequeueRpc(out PMNetPendingRpc pending)").Append(Lf);
            sb.Append("        {").Append(Lf);
            sb.Append("            if (_pendingRpcs.Count == 0)").Append(Lf);
            sb.Append("            {").Append(Lf);
            sb.Append("                pending = default(PMNetPendingRpc);").Append(Lf);
            sb.Append("                return false;").Append(Lf);
            sb.Append("            }").Append(Lf);
            sb.Append(Lf);
            sb.Append("            pending = _pendingRpcs.Dequeue();").Append(Lf);
            sb.Append("            return true;").Append(Lf);
            sb.Append("        }").Append(Lf);
            sb.Append(Lf);
            sb.Append("        /// <summary>清空待发队列（测试与关服清理用）。</summary>").Append(Lf);
            sb.Append("        /// 刻意**不复位** RejectedPendingRpcs：它是累计诊断量，").Append(Lf);
            sb.Append("        /// 复位会让「发送路径没接线」这条告警被清理动作掩盖掉。").Append(Lf);
            sb.Append("        public static void ClearPendingRpcs()").Append(Lf);
            sb.Append("        {").Append(Lf);
            sb.Append("            _pendingRpcs.Clear();").Append(Lf);
            sb.Append("        }").Append(Lf);
            sb.Append(Lf);
            sb.Append("        /// <summary>本程序集的整体协议摘要（D-R0-46），由 RegisterAll 计算后存入 PMNetRegistry。</summary>").Append(Lf);
            sb.Append("        public static uint ProtocolHash { get { return PMNet.PMNetRegistry.ProtocolHash; } }").Append(Lf);
            sb.Append(Lf);
            sb.Append("        /// <summary>").Append(Lf);
            sb.Append("        /// 注册本程序集的全部生成产物。").Append(Lf);
            sb.Append("        /// 幂等：封板后重复调用直接返回（PMNetRegistry 也不允许封板后再注册）。").Append(Lf);
            sb.Append("        /// </summary>").Append(Lf);
            sb.Append("        public static void RegisterAll()").Append(Lf);
            sb.Append("        {").Append(Lf);
            sb.Append("            if (PMNet.PMNetRegistry.IsSealed)").Append(Lf);
            sb.Append("            {").Append(Lf);
            sb.Append("                return;").Append(Lf);
            sb.Append("            }").Append(Lf);
            sb.Append(Lf);
            sb.Append("            // ★ 显式列出每个类（禁止反射扫描）。").Append(Lf);

            if (classes.Count == 0)
            {
                sb.Append("            PMNet.PMNetClassEntry[] entries = new PMNet.PMNetClassEntry[0];").Append(Lf);
            }
            else
            {
                sb.Append("            PMNet.PMNetClassEntry[] entries = new PMNet.PMNetClassEntry[")
                    .Append(classes.Count).Append("];").Append(Lf);
                for (int i = 0; i < classes.Count; i++)
                {
                    sb.Append("            entries[").Append(i).Append("] = global::")
                        .Append(classes[i].QualifiedName).Append(".PMNet_BuildEntry();")
                        .Append(" // ").Append(classes[i].TypeName)
                        .Append("（ClassId=").Append(classes[i].ClassId).Append("）").Append(Lf);
                }
            }

            sb.Append(Lf);
            sb.Append("            for (int i = 0; i < entries.Length; i++)").Append(Lf);
            sb.Append("            {").Append(Lf);
            sb.Append("                PMNet.PMNetRegistry.RegisterClass(entries[i]);").Append(Lf);
            sb.Append("            }").Append(Lf);
            sb.Append(Lf);
            sb.Append("            // 全局摘要用**唯一的** PMStableHash 实现现算，不写死字面量：").Append(Lf);
            sb.Append("            // 生成期与运行期只要喂进同一份条目，结果必然一致。").Append(Lf);
            sb.Append("            PMNet.PMNetRegistry.Seal(PMNet.PMStableHash.GlobalProtocolHash(entries));").Append(Lf);
            sb.Append("        }").Append(Lf);
            sb.Append("    }").Append(Lf);
            sb.Append("}").Append(Lf);
            return sb.ToString();
        }

        /// <summary>发射单个类的 partial 产物（契约 §4.1）。</summary>
        public static string EmitClass(PMDeclClass cls, PMDeclRawFacts facts)
        {
            string key = PMDeclScanner.KeyOf(cls);
            int indexBase = 0;
            facts.PropertyIndexBase.TryGetValue(key, out indexBase);

            bool hasArray = false;
            for (int i = 0; i < cls.Properties.Count; i++)
            {
                string element;
                if (PMTypeSet.Classify(cls.Properties[i].TypeName, facts, out element) == PMWireKind.Array)
                {
                    hasArray = true;
                }
            }

            for (int i = 0; i < cls.Rpcs.Count; i++)
            {
                for (int k = 0; k < cls.Rpcs[i].Parameters.Count; k++)
                {
                    string element;
                    if (PMTypeSet.Classify(cls.Rpcs[i].Parameters[k].TypeName, facts, out element) == PMWireKind.Array)
                    {
                        hasArray = true;
                    }
                }
            }

            StringBuilder sb = new StringBuilder(16384);
            Header(sb, "网络对象 partial 产物",
                "类：" + cls.QualifiedName,
                "稳定键：" + cls.StableKey,
                "类型 ID：" + cls.ClassId + "（0x" + cls.ClassId.ToString("X8", CultureInfo.InvariantCulture) + "）",
                "类协议摘要：0x" + cls.ClassProtocolHash.ToString("X8", CultureInfo.InvariantCulture),
                "复制属性：" + cls.Properties.Count + " 个 / RPC：" + cls.Rpcs.Count + " 条");

            EmitUsings(sb, cls, facts);

            if (!string.IsNullOrEmpty(cls.Namespace))
            {
                sb.Append("namespace ").Append(cls.Namespace).Append(Lf);
                sb.Append("{").Append(Lf);
            }

            string indent = string.IsNullOrEmpty(cls.Namespace) ? "    " : "        ";
            string baseIndent = string.IsNullOrEmpty(cls.Namespace) ? string.Empty : "    ";

            string modifiers = facts.TypeModifierPrefix.ContainsKey(key) ? facts.TypeModifierPrefix[key] : string.Empty;
            sb.Append(baseIndent).Append("/// <summary>[PMNetworkObject] 的生成产物（与业务 partial 合成同一个类）。</summary>").Append(Lf);
            sb.Append(baseIndent).Append(modifiers).Append("partial class ").Append(cls.TypeName).Append(Lf);
            sb.Append(baseIndent).Append("{").Append(Lf);

            // ---------------- 常量 ----------------
            sb.Append(indent).Append("/// <summary>类型 ID（32 位），与 PMNetRegistry 里的 ClassId 同源（D-R0-49）。</summary>").Append(Lf);
            sb.Append(indent).Append("public const uint PMGeneratedClassId = ").Append(cls.ClassId).Append("u;").Append(Lf);
            sb.Append(Lf);
            sb.Append(indent).Append("/// <summary>对象变更掩码总位数（= 本类复制属性数）。</summary>").Append(Lf);
            sb.Append(indent).Append("public const ushort PMGeneratedChangeMaskBitCount = ")
                .Append(cls.ChangeMaskBitCount).Append(";").Append(Lf);
            sb.Append(Lf);
            sb.Append(indent).Append("/// <summary>").Append(Lf);
            sb.Append(indent).Append("/// 本类复制属性序号在**整个继承链**上的基址（= 已声明祖先的复制属性总数）。").Append(Lf);
            sb.Append(indent).Append("/// PMRepList 的 index 与 MarkPropertyDirty 的 index 都是每对象连续的，").Append(Lf);
            sb.Append(indent).Append("/// 派生类若从 0 开始编号会与基类同号条目撞车。").Append(Lf);
            sb.Append(indent).Append("/// </summary>").Append(Lf);
            sb.Append(indent).Append("public const int PMGeneratedPropertyIndexBase = ").Append(indexBase).Append(";").Append(Lf);

            if (hasArray)
            {
                sb.Append(Lf);
                sb.Append(indent).Append("/// <summary>复制数组的长度上限（防御越界分配；契约 §8 列为待收敛参数，R2 取 ")
                    .Append(MaxArrayLength).Append("）。</summary>").Append(Lf);
                sb.Append(indent).Append("private const int PMGeneratedMaxArrayLength = ").Append(MaxArrayLength).Append(";").Append(Lf);
            }

            EmitRepList(sb, cls, facts, indent, indexBase);
            EmitSetters(sb, cls, facts, indent, indexBase);
            EmitPropertyIO(sb, cls, facts, indent);
            EmitOnRepDispatch(sb, cls, indent);
            EmitRpcs(sb, cls, facts, indent);
            EmitBuildEntry(sb, cls, facts, indent);

            sb.Append(baseIndent).Append("}").Append(Lf);

            if (!string.IsNullOrEmpty(cls.Namespace))
            {
                sb.Append("}").Append(Lf);
            }

            return sb.ToString();
        }

        // ------------------------------------------------------------------ 各段

        private static void EmitUsings(StringBuilder sb, PMDeclClass cls, PMDeclRawFacts facts)
        {
            List<string> usings = new List<string>();
            if (facts.UsingsByType.ContainsKey(PMDeclScanner.KeyOf(cls)))
            {
                usings.AddRange(facts.UsingsByType[PMDeclScanner.KeyOf(cls)]);
            }

            // 稳定排序 + 去重：产物必须与源文件顺序无关。
            usings.Sort(StringComparer.Ordinal);
            string prev = null;
            for (int i = 0; i < usings.Count; i++)
            {
                if (string.Equals(prev, usings[i], StringComparison.Ordinal))
                {
                    continue;
                }

                prev = usings[i];
                sb.Append(usings[i]).Append(Lf);
            }

            if (usings.Count > 0)
            {
                sb.Append(Lf);
            }
        }

        private static void EmitRepList(StringBuilder sb, PMDeclClass cls, PMDeclRawFacts facts, string indent, int indexBase)
        {
            sb.Append(Lf);
            sb.Append(indent).Append("// ---------------- 复制属性注册 ----------------").Append(Lf);
            sb.Append(Lf);
            sb.Append(indent).Append("/// <summary>注册本类的复制属性与条件（对应 UE 的 DOREPLIFETIME_WITH_PARAMS_FAST 位置）。</summary>").Append(Lf);
            sb.Append(indent).Append("protected override void CollectLifetimeReplicatedProps(PMNet.PMRepList outProps)").Append(Lf);
            sb.Append(indent).Append("{").Append(Lf);
            sb.Append(indent).Append("    base.CollectLifetimeReplicatedProps(outProps);").Append(Lf);

            for (int i = 0; i < cls.Properties.Count; i++)
            {
                PMDeclProperty p = cls.Properties[i];
                sb.Append(indent).Append("    outProps.Add(").Append(indexBase + i).Append(", PMNet.PMCond.")
                    .Append(p.Condition.ToString()).Append(", ").Append(p.PushBased ? "true" : "false")
                    .Append("); // ").Append(p.MemberName)
                    .Append("（PropertyId=").Append(p.PropertyId).Append("）").Append(Lf);
            }

            sb.Append(indent).Append("}").Append(Lf);
        }

        private static void EmitSetters(StringBuilder sb, PMDeclClass cls, PMDeclRawFacts facts, string indent, int indexBase)
        {
            if (cls.Properties.Count == 0)
            {
                return;
            }

            sb.Append(Lf);
            sb.Append(indent).Append("// ---------------- 赋值即标脏的访问器 ----------------").Append(Lf);

            for (int i = 0; i < cls.Properties.Count; i++)
            {
                PMDeclProperty p = cls.Properties[i];
                sb.Append(Lf);
                sb.Append(indent).Append("/// <summary>").Append(Lf);
                sb.Append(indent).Append("/// 赋值并标脏（Push 模型，契约 D-R0-13）。").Append(Lf);
                sb.Append(indent).Append("/// 业务必须在**真实赋值处**改调本访问器：漏标会让该属性静默不同步。").Append(Lf);
                sb.Append(indent).Append("/// </summary>").Append(Lf);
                sb.Append(indent).Append("public void PMNet_Set").Append(p.MemberName).Append("(")
                    .Append(p.TypeName).Append(" value)").Append(Lf);
                sb.Append(indent).Append("{").Append(Lf);
                sb.Append(indent).Append("    ").Append(p.MemberName).Append(" = value;").Append(Lf);
                sb.Append(indent).Append("    MarkPropertyDirty(").Append(indexBase + i).Append(");").Append(Lf);
                sb.Append(indent).Append("}").Append(Lf);
            }
        }

        private static void EmitPropertyIO(StringBuilder sb, PMDeclClass cls, PMDeclRawFacts facts, string indent)
        {
            if (cls.Properties.Count == 0)
            {
                return;
            }

            sb.Append(Lf);
            sb.Append(indent).Append("// ---------------- 属性读写器（供 PMPropertyDescriptor 持委托）----------------").Append(Lf);

            for (int i = 0; i < cls.Properties.Count; i++)
            {
                PMDeclProperty p = cls.Properties[i];
                string element;
                PMWireKind kind = PMTypeSet.Classify(p.TypeName, facts, out element);

                sb.Append(Lf);
                sb.Append(indent).Append("/// <summary>写出 ").Append(p.MemberName).Append(" 的当前值。</summary>").Append(Lf);
                sb.Append(indent).Append("private static void PMNet_Write_").Append(p.MemberName)
                    .Append("(PMNet.PMNetObject t, PMNet.PMNetWriter w)").Append(Lf);
                sb.Append(indent).Append("{").Append(Lf);
                sb.Append(indent).Append("    ").Append(cls.TypeName).Append(" self = (").Append(cls.TypeName).Append(")t;").Append(Lf);
                EmitWriteBody(sb, kind, p.TypeName, element, facts, "self." + p.MemberName, indent + "    ");
                sb.Append(indent).Append("}").Append(Lf);

                sb.Append(Lf);
                sb.Append(indent).Append("/// <summary>读入并赋值 ").Append(p.MemberName).Append("。注意：这是一个接收侧写入口，位于本 partial 内所以能访问私有成员。</summary>").Append(Lf);
                sb.Append(indent).Append("private static void PMNet_Read_").Append(p.MemberName)
                    .Append("(PMNet.PMNetObject t, PMNet.PMNetReader r)").Append(Lf);
                sb.Append(indent).Append("{").Append(Lf);
                sb.Append(indent).Append("    ").Append(cls.TypeName).Append(" self = (").Append(cls.TypeName).Append(")t;").Append(Lf);
                EmitReadBody(sb, kind, p.TypeName, element, facts, "self." + p.MemberName, indent + "    ", false);
                sb.Append(indent).Append("}").Append(Lf);
            }
        }

        private static void EmitOnRepDispatch(StringBuilder sb, PMDeclClass cls, string indent)
        {
            sb.Append(Lf);
            sb.Append(indent).Append("// ---------------- RepNotify 分发（0 = 无）----------------").Append(Lf);
            sb.Append(Lf);
            sb.Append(indent).Append("/// <summary>").Append(Lf);
            sb.Append(indent).Append("/// 按 PMPropertyDescriptor.OnRepMethodId 分发到业务的表现回调。").Append(Lf);
            sb.Append(indent).Append("/// 槽位按 PropertyId 升序、只对「有 OnRep」的属性从 1 起算").Append(Lf);
            sb.Append(indent).Append("/// （与生成期算出的 OnRepMethodId 同源）。").Append(Lf);
            sb.Append(indent).Append("/// </summary>").Append(Lf);
            sb.Append(indent).Append("private static void PMNet_OnRepDispatch(PMNet.PMNetObject t, ushort onRepMethodId)").Append(Lf);
            sb.Append(indent).Append("{").Append(Lf);

            bool any = false;
            for (int i = 0; i < cls.Properties.Count; i++)
            {
                if (!string.IsNullOrEmpty(cls.Properties[i].OnRepMethodName))
                {
                    any = true;
                    break;
                }
            }

            if (!any)
            {
                sb.Append(indent).Append("    // 本类没有任何 [PMRepNotify]。").Append(Lf);
                sb.Append(indent).Append("}").Append(Lf);
                return;
            }

            sb.Append(indent).Append("    ").Append(cls.TypeName).Append(" self = (").Append(cls.TypeName).Append(")t;").Append(Lf);
            sb.Append(Lf);
            sb.Append(indent).Append("    switch (onRepMethodId)").Append(Lf);
            sb.Append(indent).Append("    {").Append(Lf);

            for (int i = 0; i < cls.Properties.Count; i++)
            {
                PMDeclProperty p = cls.Properties[i];
                if (string.IsNullOrEmpty(p.OnRepMethodName))
                {
                    continue;
                }

                ushort slot = PMDeclScanner.RepNotifySlotOf(cls, i);
                sb.Append(indent).Append("        case ").Append(slot).Append(":").Append(Lf);
                sb.Append(indent).Append("            self.").Append(p.OnRepMethodName).Append("();").Append(Lf);
                sb.Append(indent).Append("            return;").Append(Lf);
            }

            sb.Append(indent).Append("        default:").Append(Lf);
            sb.Append(indent).Append("            return;").Append(Lf);
            sb.Append(indent).Append("    }").Append(Lf);
            sb.Append(indent).Append("}").Append(Lf);
        }

        private static void EmitRpcs(StringBuilder sb, PMDeclClass cls, PMDeclRawFacts facts, string indent)
        {
            if (cls.Rpcs.Count == 0)
            {
                return;
            }

            sb.Append(Lf);
            sb.Append(indent).Append("// ---------------- RPC ----------------").Append(Lf);
            sb.Append(Lf);
            sb.Append(indent).Append("// 实参传递：调用桩用**闭包捕获**本次调用的实参（契约 §4.3）。").Append(Lf);
            sb.Append(indent).Append("// 刻意不使用「对象上的实参帧」：发送可能被推迟（未接线 / 排队），而编码发生在").Append(Lf);
            sb.Append(indent).Append("// 推迟之后 —— 届时实参帧可能已被后续同 RPC 调用覆写，于是发出**错误的参数**。").Append(Lf);
            sb.Append(Lf);
            sb.Append(indent).Append("// 但闭包捕获本身只对**值类型与不可变引用**（string）成立：").Append(Lf);
            sb.Append(indent).Append("// 数组是可变引用类型，只捕获引用的话，编码时读到的是那个数组「当下」的元素，").Append(Lf);
            sb.Append(indent).Append("// 而不是调用时的值。尤其 Multicast：服务端会先本地执行、再外发，").Append(Lf);
            sb.Append(indent).Append("// 业务实现可能就地改写数组参数 —— 远端必须看到**调用时**的值。").Append(Lf);
            sb.Append(indent).Append("// 因此数组参数在调用点（且在本地执行之前）做一次浅拷贝快照；").Append(Lf);
            sb.Append(indent).Append("// string 不可变，不需要重复拷贝；长度门则在**入队前**执行。").Append(Lf);

            for (int i = 0; i < cls.Rpcs.Count; i++)
            {
                PMDeclRpc rpc = cls.Rpcs[i];
                PMDeclRpcFact fact = FindRpcFact(facts, cls, rpc.MethodName);

                sb.Append(Lf);
                sb.Append(indent).Append("/// <summary>RPC ").Append(rpc.MethodName)
                    .Append(" 的稳定 ID（16 位，D-R0-49）。</summary>").Append(Lf);
                sb.Append(indent).Append("public const ushort PMGeneratedRpcId_").Append(rpc.MethodName)
                    .Append(" = ").Append(rpc.RpcId).Append(";").Append(Lf);

                // ---- 接收侧分发（读参 → 过校验 → 调实现）----
                sb.Append(Lf);
                sb.Append(indent).Append("/// <summary>读出参数、过校验、调用业务实现（接收侧分发）。</summary>").Append(Lf);
                sb.Append(indent).Append("private static void PMNet_RpcInvoke_").Append(rpc.MethodName)
                    .Append("(PMNet.PMNetObject t, PMNet.PMNetReader r)").Append(Lf);
                sb.Append(indent).Append("{").Append(Lf);
                sb.Append(indent).Append("    ").Append(cls.TypeName).Append(" self = (").Append(cls.TypeName).Append(")t;").Append(Lf);
                for (int k = 0; k < rpc.Parameters.Count; k++)
                {
                    string element;
                    PMWireKind kind = PMTypeSet.Classify(rpc.Parameters[k].TypeName, facts, out element);
                    EmitReadBody(sb, kind, rpc.Parameters[k].TypeName, element, facts, "p" + k, indent + "    ", true);
                }

                // 尾随字节：契约只允许「参数正好占满载荷」。
                // 静默忽略会让「布局漂移」变成无声的兼容，而 D-R0-46 要求那是协议不兼容。
                // 位置很关键：**在业务实现之前**，也在校验同伴之前 ——
                // 否则一条带尾随字节的畸形包会先跑一遍校验同伴（甚至被报成"校验失败"）。
                sb.Append(Lf);
                sb.Append(indent).Append("    if (!r.IsAtEnd)").Append(Lf);
                sb.Append(indent).Append("    {").Append(Lf);
                sb.Append(indent).Append("        throw new System.FormatException(")
                    .Append(Quote("RPC " + rpc.MethodName + " 载荷存在尾随字节：参数只占 "))
                    .Append(" + r.Consumed + ")
                    .Append(Quote(" 字节，载荷更长"))
                    .Append(");").Append(Lf);
                sb.Append(indent).Append("    }").Append(Lf);

                EmitValidationRouting(sb, cls, rpc, fact, indent + "    ");

                sb.Append(indent).Append("    self.").Append(rpc.MethodName).Append("(");
                AppendArgs(sb, rpc);
                sb.Append(");").Append(Lf);
                sb.Append(indent).Append("}").Append(Lf);

                // ---- 业务可见调用桩 ----
                sb.Append(Lf);
                sb.Append(indent).Append("/// <summary>").Append(Lf);
                sb.Append(indent).Append("/// 业务可见的调用桩（契约 §4.3）：先走 GetFunctionCallspace 判定，").Append(Lf);
                sb.Append(indent).Append("/// 再按结果本地执行 / 发往远端 / 静默吞掉。").Append(Lf);
                sb.Append(indent).Append("/// **业务请调用本方法**，不要直接调用 ").Append(rpc.MethodName)
                    .Append("（直接调用只本地执行、不过网）。").Append(Lf);
                sb.Append(indent).Append("/// </summary>").Append(Lf);
                sb.Append(indent).Append("public void PMNet_").Append(rpc.MethodName).Append("(");
                for (int k = 0; k < rpc.Parameters.Count; k++)
                {
                    if (k > 0)
                    {
                        sb.Append(", ");
                    }

                    sb.Append(rpc.Parameters[k].TypeName).Append(" p").Append(k);
                }

                sb.Append(")").Append(Lf);
                sb.Append(indent).Append("{").Append(Lf);
                sb.Append(indent).Append("    PMNet.PMFunctionCallspace callspace = PMNet.PMRpcDispatch.EvaluateCallspace(").Append(Lf);
                sb.Append(indent).Append("        NetMode, Role, PMNet.PMRpcKind.").Append(rpc.Kind.ToString())
                    .Append(", GetNetConnection() != null, false);").Append(Lf);
                sb.Append(Lf);

                // 数组实参的快照变量必须在方法作用域声明（下面的两个 if 块都要用）。
                for (int k = 0; k < rpc.Parameters.Count; k++)
                {
                    string element;
                    if (PMTypeSet.Classify(rpc.Parameters[k].TypeName, facts, out element) == PMWireKind.Array)
                    {
                        sb.Append(indent).Append("    ").Append(rpc.Parameters[k].TypeName)
                            .Append(" p").Append(k).Append("Snapshot = null;").Append(Lf);
                    }
                }

                sb.Append(indent).Append("    // 实参预处理（快照 / 长度门）必须在**本地执行之前**：").Append(Lf);
                sb.Append(indent).Append("    // Multicast 在服务端会先本地执行、再外发，业务实现可以就地改写数组实参。").Append(Lf);
                sb.Append(indent).Append("    if (PMNet.PMRpcDispatch.ShouldSendRemote(callspace))").Append(Lf);
                sb.Append(indent).Append("    {").Append(Lf);
                EmitRemoteArgPreflight(sb, rpc, facts, indent + "        ");
                sb.Append(indent).Append("    }").Append(Lf);
                sb.Append(Lf);
                sb.Append(indent).Append("    if (PMNet.PMRpcDispatch.ShouldExecuteLocal(callspace))").Append(Lf);
                sb.Append(indent).Append("    {").Append(Lf);
                sb.Append(indent).Append("        ").Append(rpc.MethodName).Append("(");
                AppendArgs(sb, rpc);
                sb.Append(");").Append(Lf);
                sb.Append(indent).Append("    }").Append(Lf);
                sb.Append(Lf);
                sb.Append(indent).Append("    if (PMNet.PMRpcDispatch.ShouldSendRemote(callspace))").Append(Lf);
                sb.Append(indent).Append("    {").Append(Lf);
                sb.Append(indent).Append("        // 闭包捕获实参；数组用快照（值类型/string 不必）：编码推迟到真正发送时也不会读到被覆盖的值。").Append(Lf);
                sb.Append(indent).Append("        PMNet.Generated.PMNetGeneratedRegistry.EnqueueRemote(").Append(Lf);
                sb.Append(indent).Append("            this, PMGeneratedRpcId_").Append(rpc.MethodName).Append(",").Append(Lf);
                sb.Append(indent).Append("            delegate(PMNet.PMNetObject t, PMNet.PMNetWriter w)").Append(Lf);
                sb.Append(indent).Append("            {").Append(Lf);
                for (int k = 0; k < rpc.Parameters.Count; k++)
                {
                    string element;
                    PMWireKind kind = PMTypeSet.Classify(rpc.Parameters[k].TypeName, facts, out element);
                    string writeExpr = kind == PMWireKind.Array ? "p" + k + "Snapshot" : "p" + k;
                    EmitWriteBody(sb, kind, rpc.Parameters[k].TypeName, element, facts, writeExpr, indent + "                ");
                }

                sb.Append(indent).Append("            });").Append(Lf);
                sb.Append(indent).Append("    }").Append(Lf);
                sb.Append(indent).Append("}").Append(Lf);
            }
        }

        /// <summary>按名字取本类的 RPC 语法事实（校验同伴存在性等）。取不到时返回保守空事实。</summary>
        private static PMDeclRpcFact FindRpcFact(PMDeclRawFacts facts, PMDeclClass cls, string methodName)
        {
            if (facts != null && facts.Rpcs != null)
            {
                string owner = cls.QualifiedName;
                for (int i = 0; i < facts.Rpcs.Count; i++)
                {
                    PMDeclRpcFact f = facts.Rpcs[i];
                    if (f.MethodName == methodName && f.ClassQualifiedName == owner)
                    {
                        return f;
                    }
                }
            }

            return new PMDeclRpcFact();
        }

        /// <summary>拼实参列表 p0, p1, ...</summary>
        private static void AppendArgs(StringBuilder sb, PMDeclRpc rpc)
        {
            for (int k = 0; k < rpc.Parameters.Count; k++)
            {
                if (k > 0)
                {
                    sb.Append(", ");
                }

                sb.Append("p").Append(k);
            }
        }

        /// <summary>
        /// 发射「发往远端之前」的实参预处理：数组做调用点快照 + 长度门，字符串做长度门。
        ///
        /// 两件事都必须发生在**本地执行之前**：
        /// Multicast 在服务端会先本地执行、再按相关连接外发，而业务实现完全可能就地改写
        /// 数组参数（`values[0] = ...`）。只捕获引用的话，编码时读到的是那个数组「当下」的元素，
        /// 而不是调用时的值 —— 而调用点语义是"远端看到本次调用的值"。
        ///
        /// 长度门放在这里而不是只靠编码期（`WriteBounded` / 数组写体）：编码发生在
        /// 待发队列被排空的时候，那时这条调用**已经被接受**，失败会落在与调用点无关的栈上。
        /// 放在调用点才能做到"入队前失败"。
        ///
        /// `string` 不可变（每次"修改"都是新实例），因此不再拷贝，只做长度门。
        /// </summary>
        private static void EmitRemoteArgPreflight(
            StringBuilder sb, PMDeclRpc rpc, PMDeclRawFacts facts, string indent)
        {
            for (int k = 0; k < rpc.Parameters.Count; k++)
            {
                PMDeclParam p = rpc.Parameters[k];
                string element;
                PMWireKind kind = PMTypeSet.Classify(p.TypeName, facts, out element);

                if (kind == PMWireKind.Array)
                {
                    sb.Append(indent).Append("if (p").Append(k).Append(" != null)").Append(Lf);
                    sb.Append(indent).Append("{").Append(Lf);
                    sb.Append(indent).Append("    if (p").Append(k)
                        .Append(".Length > PMGeneratedMaxArrayLength)").Append(Lf);
                    sb.Append(indent).Append("    {").Append(Lf);
                    sb.Append(indent).Append("        throw new System.FormatException(")
                        .Append(Quote("RPC " + rpc.MethodName + " 的数组实参 p" + k
                                      + " 超长（上限 " + MaxArrayLength + "）："))
                        .Append(" + p").Append(k).Append(".Length);").Append(Lf);
                    sb.Append(indent).Append("    }").Append(Lf);
                    sb.Append(Lf);
                    sb.Append(indent).Append("    // 浅拷贝即足够：支持的元素集是整型/浮点/布尔/枚举/string，均为值类型或不可变引用。").Append(Lf);
                    sb.Append(indent).Append("    p").Append(k).Append("Snapshot = (").Append(p.TypeName)
                        .Append(")p").Append(k).Append(".Clone();").Append(Lf);
                    sb.Append(indent).Append("}").Append(Lf);
                    sb.Append(Lf);
                }
                else if (kind == PMWireKind.String)
                {
                    sb.Append(indent).Append("PMNet.PMNetString.EnsureWithinLimit(p").Append(k)
                        .Append(", ").Append(Quote("RPC " + rpc.MethodName + " 的字符串实参 p" + k))
                        .Append(");").Append(Lf);
                }
            }
        }

        /// <summary>
        /// 发射校验路由（缺陷修复：原来只把档位写进描述符、**不生成调用**，
        /// 于是「声明了校验」不等于「运行时真的校验了」）。
        ///
        /// 三种档位对应三种调用形态，且**动作差异是契约**（D-R0-45）：
        /// ForceValidate（项目三态）：Reject ⇒ 跳过实现但不断连；Report ⇒ 上报后仍执行。
        /// Validate（UE 原生）：返回 false ⇒ 请求断连。
        /// None：不生成校验调用。
        /// 档位与同伴不匹配的情形已在声明期被规则 13 拦下；发射器**自身也 fail-closed**：
        /// 该组合直接抛异常拒绝发射，不退化成一个只写注释、照常执行的产物。
        /// （EmitAll 是公开 API，声明期门禁不是唯一入口 —— 注释不是断言。）
        /// </summary>
        private static void EmitValidationRouting(
            StringBuilder sb, PMDeclClass cls, PMDeclRpc rpc, PMDeclRpcFact fact, string indent)
        {
            bool hasForce = fact != null && fact.HasForceValidateCompanion;
            bool hasNative = fact != null && fact.HasValidateCompanion;

            if (rpc.Validator == PMRpcValidator.ForceValidate && hasForce)
            {
                sb.Append(Lf);
                sb.Append(indent).Append("// 校验（ForceValidate 三态）：Reject 只跳过实现、**不断连**；Report 上报后仍执行。").Append(Lf);
                sb.Append(indent).Append("// ★ 非法返回值按**失败关闭**处理：C# 的枚举允许任意整数，若写成").Append(Lf);
                sb.Append(indent).Append("//   「Reject / Report / 否则执行」，一个 (PMRpcValidation)99 就会变成**放行**。").Append(Lf);
                sb.Append(indent).Append("PMNet.PMRpcValidation pmVerdict = self.")
                    .Append(rpc.MethodName).Append("_ForceValidate(");
                AppendArgs(sb, rpc);
                sb.Append(");").Append(Lf);
                sb.Append(indent).Append("if (pmVerdict != PMNet.PMRpcValidation.Accept)").Append(Lf);
                sb.Append(indent).Append("{").Append(Lf);
                sb.Append(indent).Append("    PMNet.PMRpcValidation pmReported = pmVerdict == PMNet.PMRpcValidation.Report").Append(Lf);
                sb.Append(indent).Append("        ? PMNet.PMRpcValidation.Report").Append(Lf);
                sb.Append(indent).Append("        : PMNet.PMRpcValidation.Reject;").Append(Lf);
                sb.Append(indent).Append("    PMNet.PMRpcValidationSink.NotifyReported(").Append(Lf);
                sb.Append(indent).Append("        t, PMGeneratedRpcId_").Append(rpc.MethodName)
                    .Append(", pmReported, ").Append(Quote(rpc.MethodName)).Append(");").Append(Lf);
                sb.Append(Lf);
                sb.Append(indent).Append("    if (pmReported == PMNet.PMRpcValidation.Reject)").Append(Lf);
                sb.Append(indent).Append("    {").Append(Lf);
                sb.Append(indent).Append("        return;").Append(Lf);
                sb.Append(indent).Append("    }").Append(Lf);
                sb.Append(indent).Append("}").Append(Lf);
                return;
            }

            if (rpc.Validator == PMRpcValidator.Validate && hasNative)
            {
                sb.Append(Lf);
                sb.Append(indent).Append("// 校验（UE 原生 Validate）：返回 false ⇒ 请求断开连接。").Append(Lf);
                sb.Append(indent).Append("if (!self.").Append(rpc.MethodName).Append("_Validate(");
                AppendArgs(sb, rpc);
                sb.Append("))").Append(Lf);
                sb.Append(indent).Append("{").Append(Lf);
                sb.Append(indent).Append("    PMNet.PMRpcValidationSink.NotifyValidateFailed(").Append(Lf);
                sb.Append(indent).Append("        t, PMGeneratedRpcId_").Append(rpc.MethodName)
                    .Append(", ").Append(Quote(rpc.MethodName)).Append(");").Append(Lf);
                sb.Append(Lf);
                sb.Append(indent).Append("    // 校验发生在生成物内部，它拿不到连接；把结论抛给持有连接的那一层：").Append(Lf);
                sb.Append(indent).Append("    // PMNetRpcReceive.Deliver 会据此向来源连接发出断连请求（D-R0-45）。").Append(Lf);
                sb.Append(indent).Append("    throw new PMNet.PMNetRpcValidationFailedException(").Append(Lf);
                sb.Append(indent).Append("        PMGeneratedRpcId_").Append(rpc.MethodName)
                    .Append(", ").Append(Quote(rpc.MethodName)).Append(");").Append(Lf);
                sb.Append(indent).Append("}").Append(Lf);
                return;
            }

            if (rpc.Validator != PMRpcValidator.None)
            {
                // ★ fail-closed：这里曾经只发射一条注释，然后照常执行实现 ——
                //   也就是「声称有校验、实际没有」。注释不是断言，而 EmitAll 是公开 API，
                //   任何绕过声明期门禁的调用路径（增量生成、编辑器内生成、未来工具复用）
                //   都会静默 fail-open。因此改为**硬错误、拒绝发射**。
                throw new InvalidOperationException(
                    "RPC " + cls.QualifiedName + "." + rpc.MethodName + " 声明了校验档位 " + rpc.Validator
                    + "，但没有可调用的同伴方法：发射器 fail-closed（拒绝发射），"
                    + "不退化成「声称有校验、实际没有」。声明期规则 13 应已拦下这种情况。");
            }
        }
        private static void EmitBuildEntry(StringBuilder sb, PMDeclClass cls, PMDeclRawFacts facts, string indent)
        {
            string key = PMDeclScanner.KeyOf(cls);

            sb.Append(Lf);
            sb.Append(indent).Append("// ---------------- 静态注册表条目 ----------------").Append(Lf);
            sb.Append(Lf);
            sb.Append(indent).Append("/// <summary>").Append(Lf);
            sb.Append(indent).Append("/// 本类贡献给 PMNetRegistry 的条目。").Append(Lf);
            sb.Append(indent).Append("/// 协议摘要用 PMStableHash 现算（与生成期同一实现），不写死字面量。").Append(Lf);
            sb.Append(indent).Append("/// </summary>").Append(Lf);
            sb.Append(indent).Append("internal static PMNet.PMNetClassEntry PMNet_BuildEntry()").Append(Lf);
            sb.Append(indent).Append("{").Append(Lf);

            sb.Append(indent).Append("    PMNet.PMPropertyDescriptor[] props = new PMNet.PMPropertyDescriptor[")
                .Append(cls.Properties.Count).Append("];").Append(Lf);

            for (int i = 0; i < cls.Properties.Count; i++)
            {
                PMDeclProperty p = cls.Properties[i];
                sb.Append(indent).Append("    PMNet.PMPropertyDescriptor p").Append(i)
                    .Append(" = new PMNet.PMPropertyDescriptor();").Append(Lf);
                sb.Append(indent).Append("    p").Append(i).Append(".PropertyId = ").Append(p.PropertyId).Append(";").Append(Lf);
                sb.Append(indent).Append("    p").Append(i).Append(".Condition = PMNet.PMCond.")
                    .Append(p.Condition.ToString()).Append(";").Append(Lf);
                sb.Append(indent).Append("    p").Append(i).Append(".MaskOffset = ").Append(p.MaskOffset).Append(";").Append(Lf);
                sb.Append(indent).Append("    p").Append(i).Append(".MaskBitCount = ").Append(p.MaskBitCount).Append(";").Append(Lf);
                sb.Append(indent).Append("    p").Append(i).Append(".QuantizerId = ").Append(p.QuantizerId).Append(";").Append(Lf);
                sb.Append(indent).Append("    p").Append(i).Append(".OnRepMethodId = ")
                    .Append(PMDeclScanner.RepNotifySlotOf(cls, i)).Append(";").Append(Lf);
                sb.Append(indent).Append("    p").Append(i).Append(".PushBased = ")
                    .Append(p.PushBased ? "true" : "false").Append(";").Append(Lf);
                sb.Append(indent).Append("    p").Append(i).Append(".MemberName = ").Append(Quote(p.MemberName)).Append(";").Append(Lf);
                sb.Append(indent).Append("    p").Append(i).Append(".SetterName = ").Append(Quote("PMNet_Set" + p.MemberName)).Append(";").Append(Lf);
                sb.Append(indent).Append("    p").Append(i).Append(".Writer = PMNet_Write_").Append(p.MemberName).Append(";").Append(Lf);
                sb.Append(indent).Append("    p").Append(i).Append(".Reader = PMNet_Read_").Append(p.MemberName).Append(";").Append(Lf);
                sb.Append(indent).Append("    props[").Append(i).Append("] = p").Append(i).Append(";").Append(Lf);
            }

            sb.Append(Lf);
            sb.Append(indent).Append("    PMNet.PMReplicationDescriptor rep = new PMNet.PMReplicationDescriptor();").Append(Lf);
            sb.Append(indent).Append("    rep.ClassId = PMGeneratedClassId;").Append(Lf);
            sb.Append(indent).Append("    rep.Properties = props;").Append(Lf);
            sb.Append(indent).Append("    rep.ChangeMaskBitCount = PMGeneratedChangeMaskBitCount;").Append(Lf);
            sb.Append(indent).Append("    rep.HasConditionalMask = ").Append(HasConditional(cls) ? "true" : "false").Append(";").Append(Lf);
            sb.Append(indent).Append("    rep.ProtocolHash = PMNet.PMStableHash.ClassProtocolHash(PMGeneratedClassId, props);").Append(Lf);
            sb.Append(indent).Append("    rep.TypeName = ").Append(Quote(cls.QualifiedName)).Append(";").Append(Lf);
            sb.Append(Lf);
            sb.Append(indent).Append("    PMNet.PMNetRpcEntry[] rpcs = new PMNet.PMNetRpcEntry[")
                .Append(cls.Rpcs.Count).Append("];").Append(Lf);

            for (int i = 0; i < cls.Rpcs.Count; i++)
            {
                PMDeclRpc rpc = cls.Rpcs[i];
                sb.Append(indent).Append("    PMNet.PMRpcDescriptor d").Append(i)
                    .Append(" = new PMNet.PMRpcDescriptor();").Append(Lf);
                sb.Append(indent).Append("    d").Append(i).Append(".RpcId = PMGeneratedRpcId_")
                    .Append(rpc.MethodName).Append(";").Append(Lf);
                sb.Append(indent).Append("    d").Append(i).Append(".Direction = PMNet.PMRpcKind.")
                    .Append(rpc.Kind.ToString()).Append(";").Append(Lf);
                sb.Append(indent).Append("    d").Append(i).Append(".IsReliable = ")
                    .Append(rpc.Reliability == PMRpcReliability.Reliable ? "true" : "false").Append(";").Append(Lf);
                sb.Append(indent).Append("    d").Append(i).Append(".Validator = PMNet.PMRpcValidator.")
                    .Append(rpc.Validator.ToString()).Append(";").Append(Lf);
                sb.Append(indent).Append("    d").Append(i).Append(".ParamLayoutId = ").Append(rpc.ParamLayoutId).Append(";").Append(Lf);
                sb.Append(indent).Append("    d").Append(i).Append(".MethodName = ").Append(Quote(rpc.MethodName)).Append(";").Append(Lf);
                sb.Append(Lf);
                sb.Append(indent).Append("    PMNet.PMNetRpcEntry r").Append(i).Append(" = new PMNet.PMNetRpcEntry();").Append(Lf);
                sb.Append(indent).Append("    r").Append(i).Append(".Descriptor = d").Append(i).Append(";").Append(Lf);
                sb.Append(indent).Append("    r").Append(i).Append(".OwningClassId = PMGeneratedClassId;").Append(Lf);
                // 发送侧实参由调用桩的闭包捕获，注册项不再持有 Write 委托（缺陷 1 返工）。
                sb.Append(indent).Append("    r").Append(i).Append(".Invoke = PMNet_RpcInvoke_").Append(rpc.MethodName).Append(";").Append(Lf);
                sb.Append(indent).Append("    rpcs[").Append(i).Append("] = r").Append(i).Append(";").Append(Lf);
            }

            sb.Append(Lf);
            sb.Append(indent).Append("    PMNet.PMNetClassEntry entry = new PMNet.PMNetClassEntry();").Append(Lf);
            sb.Append(indent).Append("    entry.ClassId = PMGeneratedClassId;").Append(Lf);
            sb.Append(indent).Append("    entry.TypeName = ").Append(Quote(cls.QualifiedName)).Append(";").Append(Lf);
            sb.Append(indent).Append("    entry.Rep = rep;").Append(Lf);
            sb.Append(indent).Append("    entry.Rpcs = rpcs;").Append(Lf);

            bool instantiable = !facts.AbstractTypes.Contains(key) && !facts.TypesWithoutUsableCtor.Contains(key);
            if (instantiable)
            {
                sb.Append(indent).Append("    entry.Factory = PMNet_CreateInstance;").Append(Lf);
                sb.Append(indent).Append("    return entry;").Append(Lf);
                sb.Append(indent).Append("}").Append(Lf);
                sb.Append(Lf);
                sb.Append(indent).Append("/// <summary>构造工厂（对应 PMNetWorld.RegisterClass 的工厂参数）。</summary>").Append(Lf);
                sb.Append(indent).Append("private static PMNet.PMNetObject PMNet_CreateInstance()").Append(Lf);
                sb.Append(indent).Append("{").Append(Lf);
                sb.Append(indent).Append("    return new global::").Append(cls.QualifiedName).Append("();").Append(Lf);
                sb.Append(indent).Append("}").Append(Lf);
            }
            else
            {
                sb.Append(indent).Append("    // 类型是 abstract 或没有可用无参构造，无法生成工厂；").Append(Lf);
                sb.Append(indent).Append("    // 注册方（PMNetWorld）必须另行提供实例化方式。").Append(Lf);
                sb.Append(indent).Append("    entry.Factory = null;").Append(Lf);
                sb.Append(indent).Append("    return entry;").Append(Lf);
                sb.Append(indent).Append("}").Append(Lf);
            }
        }

        // ------------------------------------------------------------------ 编码原语

        private static void EmitWriteBody(
            StringBuilder sb,
            PMWireKind kind,
            string typeName,
            string elementType,
            PMDeclRawFacts facts,
            string valueExpr,
            string indent)
        {
            if (kind == PMWireKind.Array)
            {
                string elem;
                PMWireKind elemKind = PMTypeSet.Classify(elementType, facts, out elem);

                sb.Append(indent).Append(typeName).Append(" a = ").Append(valueExpr).Append(";").Append(Lf);
                sb.Append(indent).Append("if (a == null)").Append(Lf);
                sb.Append(indent).Append("{").Append(Lf);
                sb.Append(indent).Append("    // -1 = null（契约 §6 的“长度 + 元素”需要一个 null 哨兵，否则 null 与空数组不可区分）").Append(Lf);
                sb.Append(indent).Append("    w.WriteSInt32(-1);").Append(Lf);
                sb.Append(indent).Append("}").Append(Lf);
                sb.Append(indent).Append("else").Append(Lf);
                sb.Append(indent).Append("{").Append(Lf);
                sb.Append(indent).Append("    w.WriteSInt32(a.Length);").Append(Lf);
                sb.Append(indent).Append("    for (int i = 0; i < a.Length; i++)").Append(Lf);
                sb.Append(indent).Append("    {").Append(Lf);
                sb.Append(indent).Append("        ").Append(WriteStatement(elemKind, elementType, "a[i]")).Append(Lf);
                sb.Append(indent).Append("    }").Append(Lf);
                sb.Append(indent).Append("}").Append(Lf);
                return;
            }

            sb.Append(indent).Append(WriteStatement(kind, typeName, valueExpr)).Append(Lf);
        }

        private static string WriteStatement(PMWireKind kind, string typeName, string valueExpr)
        {
            switch (kind)
            {
                case PMWireKind.Byte:
                    return "w.WriteVarint((ulong)" + valueExpr + ");";
                case PMWireKind.SByte:
                case PMWireKind.Short:
                    return "w.WriteSInt32(" + valueExpr + ");";
                case PMWireKind.UShort:
                    return "w.WriteVarint(" + valueExpr + ");";
                case PMWireKind.Int:
                    return "w.WriteInt32(" + valueExpr + ");";
                case PMWireKind.UInt:
                    return "w.WriteUInt32(" + valueExpr + ");";
                case PMWireKind.Long:
                    return "w.WriteInt64(" + valueExpr + ");";
                case PMWireKind.ULong:
                    return "w.WriteUInt64(" + valueExpr + ");";
                case PMWireKind.Bool:
                    return "w.WriteBool(" + valueExpr + ");";
                case PMWireKind.Float:
                    return "w.WriteFloat(" + valueExpr + ");";
                case PMWireKind.Double:
                    return "w.WriteDouble(" + valueExpr + ");";
                case PMWireKind.String:
                    // 有界写入（D-R0-18）：上限在运行时单点（PMNetString.MaxBytes）。
                    return "PMNet.PMNetString.WriteBounded(w, " + valueExpr + ");";
                case PMWireKind.Enum:
                    return "w.WriteEnum((int)" + valueExpr + ");";
                default:
                    return "// 不支持的类型（应由声明校验拦截）：" + typeName;
            }
        }

        /// <summary>
        /// 发射接收侧读入代码。
        /// </summary>
        /// <param name="targetExpr">赋值目标（属性写：“self.X”；RPC 参数写：“p0”）。</param>
        /// <param name="declareTypedLocal">
        /// true = 目标尚未声明，本段代码先声明再赋值（用于 RPC 参数局部变量）；
        /// false = 目标是已存在的成员（用于属性写入器）。
        /// </param>
        private static void EmitReadBody(
            StringBuilder sb,
            PMWireKind kind,
            string typeName,
            string elementType,
            PMDeclRawFacts facts,
            string targetExpr,
            string indent,
            bool declareTypedLocal)
        {
            if (kind == PMWireKind.Array)
            {
                string elem;
                PMWireKind elemKind = PMTypeSet.Classify(elementType, facts, out elem);

                if (declareTypedLocal)
                {
                    sb.Append(indent).Append(typeName).Append(" ").Append(targetExpr).Append(";").Append(Lf);
                }

                sb.Append(indent).Append("int n = r.ReadSInt32();").Append(Lf);
                sb.Append(indent).Append("if (n < 0)").Append(Lf);
                sb.Append(indent).Append("{").Append(Lf);
                sb.Append(indent).Append("    ").Append(targetExpr).Append(" = null;").Append(Lf);
                sb.Append(indent).Append("}").Append(Lf);
                sb.Append(indent).Append("else").Append(Lf);
                sb.Append(indent).Append("{").Append(Lf);
                sb.Append(indent).Append("    if (n > PMGeneratedMaxArrayLength)").Append(Lf);
                sb.Append(indent).Append("    {").Append(Lf);
                sb.Append(indent).Append("        // 越界长度 = 数据损坏，抛异常而不是静默截断（静默截断会掩盖协议错误）。").Append(Lf);
                sb.Append(indent).Append("        throw new System.FormatException(")
                    .Append(Quote("PMNet 复制数组长度越界：" + typeName)).Append(");").Append(Lf);
                sb.Append(indent).Append("    }").Append(Lf);
                sb.Append(Lf);
                sb.Append(indent).Append("    ").Append(elementType).Append("[] a = new ").Append(elementType)
                    .Append("[n];").Append(Lf);
                sb.Append(indent).Append("    for (int i = 0; i < n; i++)").Append(Lf);
                sb.Append(indent).Append("    {").Append(Lf);
                sb.Append(indent).Append("        a[i] = ").Append(ReadExpression(elemKind, elementType)).Append(";").Append(Lf);
                sb.Append(indent).Append("    }").Append(Lf);
                sb.Append(Lf);
                sb.Append(indent).Append("    ").Append(targetExpr).Append(" = a;").Append(Lf);
                sb.Append(indent).Append("}").Append(Lf);
                return;
            }

            string expr = ReadExpression(kind, typeName);
            if (declareTypedLocal)
            {
                sb.Append(indent).Append(typeName).Append(" ").Append(targetExpr).Append(" = ").Append(expr).Append(";").Append(Lf);
            }
            else
            {
                sb.Append(indent).Append(targetExpr).Append(" = ").Append(expr).Append(";").Append(Lf);
            }
        }

        private static string ReadExpression(PMWireKind kind, string typeName)
        {
            switch (kind)
            {
                case PMWireKind.Byte:
                    return "(byte)r.ReadVarint()";
                case PMWireKind.SByte:
                    return "(sbyte)r.ReadSInt32()";
                case PMWireKind.Short:
                    return "(short)r.ReadSInt32()";
                case PMWireKind.UShort:
                    return "(ushort)r.ReadVarint()";
                case PMWireKind.Int:
                    return "r.ReadInt32()";
                case PMWireKind.UInt:
                    return "r.ReadUInt32()";
                case PMWireKind.Long:
                    return "r.ReadInt64()";
                case PMWireKind.ULong:
                    return "r.ReadUInt64()";
                case PMWireKind.Bool:
                    return "r.ReadBool()";
                case PMWireKind.Float:
                    return "r.ReadFloat()";
                case PMWireKind.Double:
                    return "r.ReadDouble()";
                case PMWireKind.String:
                    // 有界读入（D-R0-18）：先按上限预检，再分配。
                    return "PMNet.PMNetString.ReadBounded(r)";
                case PMWireKind.Enum:
                    return "(" + typeName + ")r.ReadEnum()";
                default:
                    return "default(" + typeName + ") /* 不支持的类型（应由声明校验拦截） */";
            }
        }

        // ------------------------------------------------------------------ 工具

        private static void Header(StringBuilder sb, string kind, params string[] lines)
        {
            sb.Append("// <auto-generated>").Append(Lf);
            sb.Append("//     本文件由 Tools/PMNetGen 生成，请勿手动修改。").Append(Lf);
            sb.Append("//     ").Append(kind).Append(Lf);
            for (int i = 0; i < lines.Length; i++)
            {
                sb.Append("//     ").Append(lines[i]).Append(Lf);
            }

            sb.Append("// ").Append(Lf);
            sb.Append("//     生成契约：Docs/plans/net-r2-codegen-contract.md（§2 稳定 ID / §4 API 面）").Append(Lf);
            sb.Append("//     语言面：C# 7.3 + .NET Standard 2.0（Unity 2019.4 约束，D-R0-48）").Append(Lf);
            sb.Append("//     重新生成：dotnet Tools/PMNetGen/bin/Release/net8.0/PMNetGen.dll \\").Append(Lf);
            sb.Append("//                 --decl-gen <源目录> --out-dir <输出目录> --id-lock Docs/plans/pmnet-ids.json").Append(Lf);
            sb.Append("//     同步校验：build.bat 以 --decl-check 模式比对，不一致即失败").Append(Lf);
            sb.Append("// </auto-generated>").Append(Lf);
            sb.Append(Lf);
        }

        private static bool HasConditional(PMDeclClass cls)
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

        private static string DescribeValidator(PMDeclRpc rpc)
        {
            if (rpc.Kind != PMRpcKind.Server)
            {
                return "档位 " + rpc.Validator;
            }

            switch (rpc.Validator)
            {
                case PMRpcValidator.ForceValidate:
                    return "ForceValidate：三态 Accept/Report/Reject，Reject 只跳过实现、不断连";
                case PMRpcValidator.Validate:
                    return "Validate：UE 原生语义，校验失败即断连";
                default:
                    return "None（规则 8 会对 Server RPC 报错）";
            }
        }

        /// <summary>C# 字符串字面量转义（只处理确定性可复现的部分）。</summary>
        private static string Quote(string text)
        {
            StringBuilder sb = new StringBuilder(text.Length + 2);
            sb.Append('"');
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }

            sb.Append('"');
            return sb.ToString();
        }
    }
}
