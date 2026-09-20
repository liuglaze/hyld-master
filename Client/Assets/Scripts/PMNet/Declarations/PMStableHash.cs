using System;
using System.Text;

namespace PMNet
{
    /// <summary>
    /// 稳定 ID 与协议摘要的**唯一实现**。
    ///
    /// **为什么放在运行时侧而不是生成器侧**：生成器（外部 Roslyn 工具）与运行期
    /// 都要算出同一个哈希。若两边各写一份，「看起来一样」的两段代码迟早会因为
    /// 一个 `unchecked`、一处编码差异而分叉——而分叉的表现是"线协议静默不兼容"，
    /// 排查代价极高。因此只留一份实现，生成器通过 `<Compile Include>` 直接引它。
    ///
    /// ## 稳定 ID 的分配规则（D-R0-49）
    ///
    /// ID 由**稳定键**的哈希决定，**与声明顺序无关**。
    /// 稳定键的构造：
    /// <code>
    /// 类：   "CLASS:" + 命名空间 + "." + 类型名
    /// 属性： "PROP:"  + 命名空间 + "." + 类型名 + "." + 成员名
    /// RPC：  "RPC:"   + 命名空间 + "." + 类型名 + "." + 方法名
    /// </code>
    ///
    /// 「与声明顺序无关」是硬要求：否则重排源文件、或换一个文件放同一个类，
    /// 线协议就变了，而编译期毫无提示。
    ///
    /// 哈希碰撞由**锁文件**兜底（见生成器侧的 `PMIdLock`）：生成器在分配时检测碰撞，
    /// 并按字典序确定性探测下一个空闲 ID，把结果写进锁文件；此后锁文件是权威。
    /// 若某次生成会导致**已存在键的 ID 改变**，生成器直接报错——那等于悄悄改了线协议。
    /// </summary>
    public static class PMStableHash
    {
        /// <summary>
        /// FNV-1a 32 位。
        ///
        /// 选它而不是 SHA/MD5 的理由：① 生成器与运行期都要用，实现越短越不容易分叉；
        /// ② 我们只需要"稳定且分布均匀"，不需要密码学强度；③ 无外部依赖（Unity 侧可用）。
        /// </summary>
        public static uint Fnv1a32(string text)
        {
            if (text == null) { text = string.Empty; }

            unchecked
            {
                const uint offsetBasis = 2166136261u;
                const uint prime = 16777619u;

                uint hash = offsetBasis;
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    // 逐字节喂入（UTF-16 低字节再高字节），避免 Encoding 调用带来的分配。
                    hash = (hash ^ (byte)(c & 0xFF)) * prime;
                    hash = (hash ^ (byte)((c >> 8) & 0xFF)) * prime;
                }

                return hash;
            }
        }

        /// <summary>FNV-1a 32 位，喂入原始字节。</summary>
        public static uint Fnv1a32(byte[] data, int offset, int count)
        {
            if (data == null) { return Fnv1a32(string.Empty); }

            unchecked
            {
                uint hash = 2166136261u;
                const uint prime = 16777619u;

                for (int i = 0; i < count; i++)
                {
                    hash = (hash ^ data[offset + i]) * prime;
                }

                return hash;
            }
        }

        /// <summary>打散后再取低 16 位，降低"低位聚集"导致的碰撞集中。</summary>
        public static ushort ToUInt16(uint hash)
        {
            // 32→16 直接截断会让高位信息丢失，用一次乘加把高位混入低位。
            unchecked
            {
                uint mixed = hash ^ (hash >> 16);
                mixed *= 2246822519u;
                mixed ^= mixed >> 13;
                return (ushort)(mixed & 0xFFFFu);
            }
        }

        /// <summary>稳定键：类。</summary>
        public static string ClassKey(string ns, string typeName)
        {
            return "CLASS:" + Qualify(ns, typeName);
        }

        /// <summary>
        /// 稳定键：属性。
        ///
        /// **入参是「所属类的稳定键」，不是命名空间+类型名** —— 这一点是刻意的：
        /// 成员键必须以类键为前缀，否则 `[PMNetworkObject("显式键")]` 那条"改名但保持
        /// 线协议兼容"的承诺就是假的。
        ///
        /// 早期版本用 `命名空间.类型名` 拼成员键，结果是：钉住类键后改类名，
        /// **类 ID 不变、全部成员 ID 都变**——等于只保住了半个协议。
        /// 对**未显式指定** StableKey 的类，`ClassKeyBody` 恰好等于 `命名空间.类型名`，
        /// 因此这个修正不改变任何既有 ID（无兼容代价）。
        /// </summary>
        public static string PropertyKey(string classStableKey, string memberName)
        {
            return "PROP:" + ClassKeyBody(classStableKey) + "." + memberName;
        }

        /// <summary>稳定键：RPC。入参同样是「所属类的稳定键」，理由见 <see cref="PropertyKey"/>。</summary>
        public static string RpcKey(string classStableKey, string methodName)
        {
            return "RPC:" + ClassKeyBody(classStableKey) + "." + methodName;
        }

        /// <summary>
        /// 取出类稳定键的主体（去掉 `CLASS:` 前缀）。
        ///
        /// 未显式指定 StableKey 时，类键是 `CLASS:命名空间.类型名`，
        /// 因此主体就是 `命名空间.类型名`，与修正前拼出的成员键逐字一致。
        /// </summary>
        public static string ClassKeyBody(string classStableKey)
        {
            if (string.IsNullOrEmpty(classStableKey))
            {
                return string.Empty;
            }

            const string prefix = "CLASS:";
            if (classStableKey.StartsWith(prefix, StringComparison.Ordinal))
            {
                return classStableKey.Substring(prefix.Length);
            }

            return classStableKey;
        }

        /// <summary>拼限定名；命名空间为空时不产生前导点。</summary>
        public static string Qualify(string ns, string typeName)
        {
            if (string.IsNullOrEmpty(ns))
            {
                return typeName;
            }

            return ns + "." + typeName;
        }

        /// <summary>类 ID（32 位）。0 被保留为无效，命中 0 时改为 1。</summary>
        public static uint ClassId(string stableKey)
        {
            uint h = Fnv1a32(stableKey);
            return h == 0u ? 1u : h;
        }

        /// <summary>成员 ID（属性 / RPC，16 位）。0 被保留为无效，命中 0 时改为 1。</summary>
        public static ushort MemberId(string stableKey)
        {
            ushort v = ToUInt16(Fnv1a32(stableKey));
            return v == 0 ? (ushort)1 : v;
        }

        /// <summary>
        /// 参数布局哈希（D-R0-46）。
        ///
        /// 只取决于**参数类型的有序列表**，与参数名无关——改参数名不该破坏协议。
        /// 两端不一致即协议不兼容，必须走断连/标记流程，而不是忽略该 RPC。
        /// </summary>
        public static ushort ParamLayoutId(string[] paramTypeNames)
        {
            StringBuilder sb = new StringBuilder(64);
            sb.Append("SIG(");
            if (paramTypeNames != null)
            {
                for (int i = 0; i < paramTypeNames.Length; i++)
                {
                    if (i > 0) { sb.Append(','); }
                    sb.Append(NormalizeTypeName(paramTypeNames[i]));
                }
            }

            sb.Append(')');
            return MemberId("SIG:" + sb);
        }

        /// <summary>
        /// 类型名归一化：去掉空白，让 `int`、` int `、`System.Int32` 至少在**书写一致**时
        /// 得到同一个签名。
        ///
        /// 注意这里刻意**不做** C# 别名到 CLR 全名的展开（`int` → `System.Int32`）：
        /// 那需要语义模型，而参数签名是以**源码书写**为基准稳定的。
        /// 生成器会在声明阶段统一规范化书写，运行期不需要猜。
        /// </summary>
        public static string NormalizeTypeName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return "?";
            }

            StringBuilder sb = new StringBuilder(typeName.Length);
            for (int i = 0; i < typeName.Length; i++)
            {
                char c = typeName[i];
                if (!char.IsWhiteSpace(c))
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// 类协议摘要：对「属性 ID + 条件 + 量化器 + 成员类型签名」的有序列表取哈希。
        /// 属性顺序按 PropertyId 升序，因此与声明顺序无关。
        /// </summary>
        public static uint ClassProtocolHash(uint classId, PMPropertyDescriptor[] props)
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append("CLASSHASH(");
            sb.Append(classId.ToString("X8"));

            if (props != null)
            {
                for (int i = 0; i < props.Length; i++)
                {
                    sb.Append('|');
                    sb.Append(props[i].PropertyId);
                    sb.Append(':');
                    sb.Append((byte)props[i].Condition);
                    sb.Append(':');
                    sb.Append(props[i].QuantizerId);
                    sb.Append(':');
                    sb.Append(props[i].MaskOffset);
                    sb.Append(':');
                    sb.Append(props[i].MaskBitCount);
                    sb.Append(':');
                    sb.Append(props[i].MemberName);
                }
            }

            sb.Append(')');
            return Fnv1a32(sb.ToString());
        }

        /// <summary>
        /// 整体协议摘要：把每个类的协议摘要与每条 RPC 的（ID、方向、可靠性、参数布局）
        /// 按 **ID 升序**混入，得到一个全局值，用于握手比对（D-R0-46）。
        ///
        /// 之所以按 ID 升序而不是注册顺序：注册顺序取决于生成代码的书写顺序，
        /// 那正是 D-R0-49 要消除的东西。
        /// </summary>
        public static uint GlobalProtocolHash(PMNetClassEntry[] classes)
        {
            if (classes == null || classes.Length == 0)
            {
                return Fnv1a32("GLOBALHASH()");
            }

            // 按 ClassId 升序，与声明顺序解耦
            PMNetClassEntry[] sorted = new PMNetClassEntry[classes.Length];
            Array.Copy(classes, sorted, classes.Length);
            Array.Sort(sorted, delegate (PMNetClassEntry a, PMNetClassEntry b)
            {
                return a.ClassId.CompareTo(b.ClassId);
            });

            StringBuilder sb = new StringBuilder(1024);
            sb.Append("GLOBALHASH(");

            for (int i = 0; i < sorted.Length; i++)
            {
                PMNetClassEntry c = sorted[i];
                if (c == null) { continue; }

                sb.Append('|');
                sb.Append(c.ClassId.ToString("X8"));
                sb.Append(':');
                sb.Append(c.Rep != null ? c.Rep.ProtocolHash : 0u);

                if (c.Rpcs == null || c.Rpcs.Length == 0)
                {
                    continue;
                }

                // 同一类内的 RPC 也按 RpcId 升序
                ushort[] ids = new ushort[c.Rpcs.Length];
                for (int j = 0; j < c.Rpcs.Length; j++)
                {
                    ids[j] = c.Rpcs[j].Descriptor.RpcId;
                }

                Array.Sort(ids);

                for (int j = 0; j < ids.Length; j++)
                {
                    PMNetRpcEntry e = null;
                    for (int k = 0; k < c.Rpcs.Length; k++)
                    {
                        if (c.Rpcs[k].Descriptor.RpcId == ids[j]) { e = c.Rpcs[k]; break; }
                    }

                    if (e == null) { continue; }

                    sb.Append(";R");
                    sb.Append(e.Descriptor.RpcId);
                    sb.Append(':');
                    sb.Append((byte)e.Descriptor.Direction);
                    sb.Append(':');
                    sb.Append(e.Descriptor.IsReliable ? 1 : 0);
                    sb.Append(':');
                    sb.Append(e.Descriptor.ParamLayoutId);
                }
            }

            sb.Append(')');
            return Fnv1a32(sb.ToString());
        }
    }
}
