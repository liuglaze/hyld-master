using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PMNet.Codegen
{
    /// <summary>
    /// 稳定 ID 的分配与锁文件（D-R0-49）。
    ///
    /// ## 为什么单一哈希不够
    ///
    /// 只用 `hash(稳定键)` 会有一个致命问题：**16 位空间下碰撞是必然的**。
    /// 属性/RPC 的 ID 是 16 位（65536），一个中等规模项目几百个成员时，
    /// 生日悖论下碰撞概率已经不可忽略。一旦碰撞：
    /// <list type="bullet">
    ///   <item>后声明的那个会覆盖先声明的（生成器报错），或者</item>
    ///   <item>若用"探测下一个空位"解决，那么**新增一个声明可能让已有声明的 ID 改变** ——
    ///         这等于悄悄改了线协议，而编译期毫无提示。</item>
    /// </list>
    ///
    /// ## 锁文件怎么解决
    ///
    /// 生成器在 `Docs/plans/pmnet-ids.json` 里记录「稳定键 → 已分配的 ID」：
    /// <list type="number">
    ///   <item>**已有键** ⇒ 一律沿用锁文件里的 ID（锁文件是权威，不是哈希）。</item>
    ///   <item>**新键** ⇒ 先试哈希值；若被占用，按**字典序确定性探测**下一个空位。</item>
    ///   <item>**新键的探测若会撞到已有键的 ID** ⇒ 跳过（已有键优先），继续探测。</item>
    ///   <item>**已删除的键** ⇒ 只告警，**ID 永不回收**（回收会让旧客户端把新对象
    ///         错认成老对象，正是 D-R0-02「NetId 不复用」的同一条道理）。</item>
    /// </list>
    ///
    /// 这样：
    /// <list type="bullet">
    ///   <item>重排源文件 ⇒ 稳定键不变 ⇒ ID 不变 ⇒ 线协议不变（可被门禁验证）；</item>
    ///   <item>新增声明 ⇒ 只影响新键，已有键的 ID 被锁文件钉死；</item>
    ///   <item>改名 ⇒ 稳定键变 ⇒ 新 ID，等价于"新成员 + 老成员退役"，这是**显式**的，
    ///         想保持兼容就在 `[PMNetworkObject("显式稳定键")]` 里钉住。</item>
    /// </list>
    ///
    /// ## 命名空间隔离
    ///
    /// 类 ID（32 位）、属性 ID（16 位）、RPC ID（16 位）**各有独立的编号空间**。
    /// 属性 ID 只需在**类内**唯一（UE 的 `FLifetimeProperty` 也是这个口径），
    /// 因此属性表按 `类稳定键` 分桶。
    /// </summary>
    public sealed class PMIdLock
    {
        /// <summary>锁文件格式版本。不匹配 ⇒ 拒绝加载（避免用错误结构解释旧文件）。</summary>
        public const int FormatVersion = 1;

        /// <summary>属性 / RPC 的 ID 空间上限（16 位）。</summary>
        public const int MemberIdSpace = 65536;

        /// <summary>类 ID 空间（32 位）。</summary>
        public const long ClassIdSpace = 4294967296L;

        private readonly Dictionary<string, uint> _classIds = new Dictionary<string, uint>(StringComparer.Ordinal);

        /// <summary>类稳定键 → (成员稳定键 → 成员 ID)。属性与 RPC 共用成员编号空间。</summary>
        private readonly Dictionary<string, Dictionary<string, ushort>> _memberIds =
            new Dictionary<string, Dictionary<string, ushort>>(StringComparer.Ordinal);

        /// <summary>本次分配过程中发现的碰撞（诊断用）。</summary>
        public readonly List<string> Collisions = new List<string>();

        /// <summary>本次分配新增的键（用于报告"新增了哪些 ID"）。</summary>
        public readonly List<string> Added = new List<string>();

        /// <summary>锁文件里有、但本次声明中不存在的键（只告警，ID 不回收）。</summary>
        public readonly List<string> Retired = new List<string>();

        /// <summary>已记录的类 ID 数量。</summary>
        public int ClassCount { get { return _classIds.Count; } }

        /// <summary>已记录的成员 ID 总数。</summary>
        public int MemberCount
        {
            get
            {
                int n = 0;
                foreach (KeyValuePair<string, Dictionary<string, ushort>> kv in _memberIds)
                {
                    n += kv.Value.Count;
                }

                return n;
            }
        }

        /// <summary>
        /// 分配类 ID。已存在则沿用锁文件里的值（权威）。
        /// </summary>
        public uint AllocateClass(string stableKey)
        {
            uint existing;
            if (_classIds.TryGetValue(stableKey, out existing))
            {
                return existing;
            }

            uint candidate = PMStableHash.ClassId(stableKey);

            // 32 位空间碰撞概率极低，但仍要处理：不能因为"几乎不会发生"就静默覆盖。
            int probe = 0;
            while (IsClassIdTaken(candidate))
            {
                probe++;
                if (probe > 100000)
                {
                    throw new InvalidOperationException("类 ID 分配探测超过上限，锁文件可能已损坏：" + stableKey);
                }

                candidate = unchecked(PMStableHash.ClassId(stableKey) + (uint)probe);
                if (candidate == 0u) { candidate = 1u; }
            }

            if (probe > 0)
            {
                Collisions.Add("类 " + stableKey + " 哈希碰撞，探测 " + probe + " 次后落在 " + candidate);
            }

            _classIds.Add(stableKey, candidate);
            Added.Add("CLASS " + stableKey + " = " + candidate);
            return candidate;
        }

        private bool IsClassIdTaken(uint id)
        {
            foreach (KeyValuePair<string, uint> kv in _classIds)
            {
                if (kv.Value == id) { return true; }
            }

            return false;
        }

        /// <summary>
        /// 分配成员 ID（属性或 RPC 共用同一编号空间，按所属类分桶）。
        ///
        /// 注意：属性与 RPC **必须分桶到不同的编号空间**，否则"属性 5"和"RPC 5"
        /// 会互相挤压、并让锁文件无法表达。这里用 `ownerStableKey` 分桶，
        /// 而 `memberKind` 参与稳定键（`PROP:` / `RPC:` 前缀已区分），
        /// 因此同一个类下属性与 RPC 天然不冲突。
        /// </summary>
        public ushort AllocateMember(string ownerStableKey, string memberStableKey)
        {
            Dictionary<string, ushort> bucket;
            if (!_memberIds.TryGetValue(ownerStableKey, out bucket))
            {
                bucket = new Dictionary<string, ushort>(StringComparer.Ordinal);
                _memberIds.Add(ownerStableKey, bucket);
            }

            ushort existing;
            if (bucket.TryGetValue(memberStableKey, out existing))
            {
                return existing;
            }

            ushort candidate = PMStableHash.MemberId(memberStableKey);

            int probe = 0;
            while (IsMemberIdTaken(bucket, candidate))
            {
                probe++;
                if (probe >= MemberIdSpace)
                {
                    throw new InvalidOperationException(
                        "成员 ID 空间已满（" + MemberIdSpace + "），无法为 " + memberStableKey + " 分配 ID");
                }

                candidate = (ushort)((PMStableHash.MemberId(memberStableKey) + probe) & 0xFFFF);
                if (candidate == 0) { candidate = 1; }
            }

            if (probe > 0)
            {
                Collisions.Add("成员 " + memberStableKey + "（属 " + ownerStableKey + "）哈希碰撞，探测 "
                               + probe + " 次后落在 " + candidate);
            }

            bucket.Add(memberStableKey, candidate);
            Added.Add("MEMBER " + ownerStableKey + " / " + memberStableKey + " = " + candidate);
            return candidate;
        }

        private static bool IsMemberIdTaken(Dictionary<string, ushort> bucket, ushort id)
        {
            foreach (KeyValuePair<string, ushort> kv in bucket)
            {
                if (kv.Value == id) { return true; }
            }

            return false;
        }

        /// <summary>
        /// 从已加载的锁文件里取出某个键的 ID（不分配）。
        /// 用于"变更检测"：生成器在分配**之前**先记下旧值，分配之后再比对，
        /// 若某个已存在键的 ID 变了，就是硬错误（等于改了线协议）。
        /// </summary>
        public bool TryGetClassId(string stableKey, out uint id)
        {
            return _classIds.TryGetValue(stableKey, out id);
        }

        /// <summary>从已加载的锁文件里取出某个成员的 ID（不分配）。</summary>
        public bool TryGetMemberId(string ownerStableKey, string memberStableKey, out ushort id)
        {
            id = 0;
            Dictionary<string, ushort> bucket;
            if (!_memberIds.TryGetValue(ownerStableKey, out bucket))
            {
                return false;
            }

            return bucket.TryGetValue(memberStableKey, out id);
        }

        /// <summary>标记某个键已退役（本次声明里不再出现）。</summary>
        public void MarkRetired(string key)
        {
            Retired.Add(key);
        }

        // -------------------------------------------------------------------- 枚举（只读）

        /// <summary>
        /// 枚举锁文件里的全部类稳定键（字典序）。
        ///
        /// 补这个 API 的原因：「键退役只告警、ID 永不回收」这条规则需要知道
        /// **锁文件里有、而本次声明里没有**的键。没有枚举入口时只能去反解
        /// `Serialize()` 的输出形状 —— 那是拿"输出格式"当"读取接口"用，
        /// 一旦序列化格式变化就会静默失准。读取接口应当显式存在。
        /// </summary>
        public List<string> EnumerateClassKeys()
        {
            List<string> keys = new List<string>(_classIds.Keys);
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        /// <summary>枚举某个类下的全部成员稳定键（字典序）。类不存在则返回空表。</summary>
        public List<string> EnumerateMemberKeys(string ownerStableKey)
        {
            List<string> keys = new List<string>();
            Dictionary<string, ushort> bucket;
            if (ownerStableKey != null && _memberIds.TryGetValue(ownerStableKey, out bucket))
            {
                keys.AddRange(bucket.Keys);
                keys.Sort(StringComparer.Ordinal);
            }

            return keys;
        }

        /// <summary>枚举全部「类稳定键 → 成员稳定键」对（用于整体比对退役键）。</summary>
        public List<KeyValuePair<string, string>> EnumerateAllMemberKeys()
        {
            List<KeyValuePair<string, string>> all = new List<KeyValuePair<string, string>>();
            List<string> owners = EnumerateClassKeys();
            for (int i = 0; i < owners.Count; i++)
            {
                Dictionary<string, ushort> bucket;
                if (!_memberIds.TryGetValue(owners[i], out bucket)) { continue; }

                List<string> members = new List<string>(bucket.Keys);
                members.Sort(StringComparer.Ordinal);
                for (int j = 0; j < members.Count; j++)
                {
                    all.Add(new KeyValuePair<string, string>(owners[i], members[j]));
                }
            }

            return all;
        }

        // -------------------------------------------------------------------- 序列化

        /// <summary>
        /// 序列化为锁文件文本（JSON 形状，手写以避免引入序列化依赖）。
        ///
        /// 键按字典序排序输出 —— 这样"同样的声明集合"必然产出**逐字节相同**的文件，
        /// 二次生成的 diff 为空（T40 的断言之一）。
        /// </summary>
        public string Serialize()
        {
            List<string> classKeys = new List<string>(_classIds.Keys);
            classKeys.Sort(StringComparer.Ordinal);

            StringBuilder sb = new StringBuilder(4096);
            sb.Append("{\n");
            sb.Append("  \"formatVersion\": ").Append(FormatVersion).Append(",\n");
            sb.Append("  \"_comment\": \"本文件是 PMNet 稳定 ID 的权威来源（D-R0-49）。请勿手工编辑；改 ID 等于改线协议。\",\n");

            sb.Append("  \"classes\": {\n");
            for (int i = 0; i < classKeys.Count; i++)
            {
                sb.Append("    ").Append(Quote(classKeys[i])).Append(": ").Append(_classIds[classKeys[i]]);
                sb.Append(i + 1 < classKeys.Count ? ",\n" : "\n");
            }

            sb.Append("  },\n");

            sb.Append("  \"members\": {\n");
            for (int i = 0; i < classKeys.Count; i++)
            {
                string owner = classKeys[i];
                Dictionary<string, ushort> bucket;
                if (!_memberIds.TryGetValue(owner, out bucket) || bucket.Count == 0)
                {
                    continue;
                }

                List<string> memberKeys = new List<string>(bucket.Keys);
                memberKeys.Sort(StringComparer.Ordinal);

                sb.Append("    ").Append(Quote(owner)).Append(": {\n");
                for (int j = 0; j < memberKeys.Count; j++)
                {
                    sb.Append("      ").Append(Quote(memberKeys[j])).Append(": ").Append(bucket[memberKeys[j]]);
                    sb.Append(j + 1 < memberKeys.Count ? ",\n" : "\n");
                }

                sb.Append("    }");
                sb.Append(i + 1 < classKeys.Count ? ",\n" : "\n");
            }

            sb.Append("  }\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>
        /// 从锁文件文本加载。返回 null 表示文件为空/不存在（此时按"全新分配"处理）。
        /// 结构不合法则抛异常 —— 宁可停下来，也不要用半个锁文件去分配 ID。
        /// </summary>
        public static PMIdLock Deserialize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            PMIdLock result = new PMIdLock();
            PMJsonReader reader = new PMJsonReader(text);

            if (!reader.EnterObject())
            {
                throw new FormatException("锁文件不是 JSON 对象");
            }

            bool sawVersion = false;

            while (true)
            {
                string key;
                if (!reader.TryReadKey(out key))
                {
                    break;
                }

                if (key == "formatVersion")
                {
                    int version = reader.ReadInt();
                    if (version != FormatVersion)
                    {
                        throw new FormatException("锁文件格式版本不兼容：文件=" + version + "，本工具=" + FormatVersion);
                    }

                    sawVersion = true;
                }
                else if (key == "_comment")
                {
                    reader.SkipValue();
                }
                else if (key == "classes")
                {
                    if (!reader.EnterObject()) { throw new FormatException("classes 不是对象"); }

                    while (true)
                    {
                        string classKey;
                        if (!reader.TryReadKey(out classKey)) { break; }
                        uint id = (uint)reader.ReadLong();
                        if (id == 0u) { throw new FormatException("类 " + classKey + " 的 ID 为 0（保留值）"); }
                        result._classIds[classKey] = id;
                    }

                    reader.ExitObject();
                }
                else if (key == "members")
                {
                    if (!reader.EnterObject()) { throw new FormatException("members 不是对象"); }

                    while (true)
                    {
                        string owner;
                        if (!reader.TryReadKey(out owner)) { break; }
                        if (!reader.EnterObject()) { throw new FormatException("members." + owner + " 不是对象"); }

                        Dictionary<string, ushort> bucket = new Dictionary<string, ushort>(StringComparer.Ordinal);
                        while (true)
                        {
                            string memberKey;
                            if (!reader.TryReadKey(out memberKey)) { break; }
                            long id = reader.ReadLong();
                            if (id <= 0 || id >= MemberIdSpace)
                            {
                                throw new FormatException("成员 " + memberKey + " 的 ID " + id + " 越界");
                            }

                            bucket[memberKey] = (ushort)id;
                        }

                        reader.ExitObject();
                        result._memberIds[owner] = bucket;
                    }

                    reader.ExitObject();
                }
                else
                {
                    reader.SkipValue();
                }
            }

            reader.ExitObject();

            if (!sawVersion)
            {
                throw new FormatException("锁文件缺少 formatVersion 字段");
            }

            return result;
        }

        private static string Quote(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' || c == '\\')
                {
                    sb.Append('\\').Append(c);
                }
                else if (c < ' ')
                {
                    sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append(c);
                }
            }

            sb.Append('"');
            return sb.ToString();
        }
    }

    /// <summary>
    /// 极简 JSON 读取器（只为锁文件服务）。
    ///
    /// 不引入 Newtonsoft/System.Text.Json 的理由与项目其它工具一致：零 NuGet 依赖，
    /// 且我们只需要"读一个结构固定的小文件"，通用 JSON 库是过度设计。
    /// 写得严谨一些：非法输入一律抛 `FormatException`，不猜。
    /// </summary>
    internal sealed class PMJsonReader
    {
        private readonly string _s;
        private int _i;

        public PMJsonReader(string text)
        {
            _s = text;
            _i = 0;
        }

        /// <summary>吃掉 `{`。</summary>
        public bool EnterObject()
        {
            SkipWhitespace();
            if (_i < _s.Length && _s[_i] == '{')
            {
                _i++;
                return true;
            }

            return false;
        }

        /// <summary>吃掉 `}`。</summary>
        public void ExitObject()
        {
            SkipWhitespace();
            if (_i >= _s.Length || _s[_i] != '}')
            {
                throw new FormatException("期望 '}'，位置 " + _i);
            }

            _i++;
        }

        /// <summary>
        /// 读下一个 `"key":`；返回 false 表示对象已结束。
        /// 会自动吃掉键值之间的逗号。
        /// </summary>
        public bool TryReadKey(out string key)
        {
            key = null;
            SkipWhitespace();

            if (_i >= _s.Length) { return false; }

            if (_s[_i] == '}')
            {
                return false;
            }

            if (_s[_i] == ',')
            {
                _i++;
                SkipWhitespace();
                if (_i < _s.Length && _s[_i] == '}') { return false; }
            }

            if (_i >= _s.Length || _s[_i] != '"')
            {
                throw new FormatException("期望键字符串，位置 " + _i);
            }

            key = ReadString();
            SkipWhitespace();

            if (_i >= _s.Length || _s[_i] != ':')
            {
                throw new FormatException("期望 ':'，位置 " + _i);
            }

            _i++;
            return true;
        }

        public int ReadInt()
        {
            return (int)ReadLong();
        }

        public long ReadLong()
        {
            SkipWhitespace();
            int start = _i;

            if (_i < _s.Length && (_s[_i] == '-' || _s[_i] == '+'))
            {
                _i++;
            }

            while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9')
            {
                _i++;
            }

            if (_i == start)
            {
                throw new FormatException("期望数字，位置 " + start);
            }

            long value;
            if (!long.TryParse(_s.Substring(start, _i - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                throw new FormatException("数字解析失败：" + _s.Substring(start, _i - start));
            }

            return value;
        }

        /// <summary>跳过任意一个值（字符串 / 数字 / 对象 / 数组 / 字面量）。</summary>
        public void SkipValue()
        {
            SkipWhitespace();
            if (_i >= _s.Length) { return; }

            char c = _s[_i];
            if (c == '"')
            {
                ReadString();
                return;
            }

            if (c == '{' || c == '[')
            {
                char open = c;
                char close = c == '{' ? '}' : ']';
                int depth = 0;
                while (_i < _s.Length)
                {
                    char d = _s[_i];
                    if (d == '"') { ReadString(); continue; }
                    if (d == open) { depth++; }
                    else if (d == close)
                    {
                        depth--;
                        if (depth == 0) { _i++; return; }
                    }

                    _i++;
                }

                throw new FormatException("未闭合的 " + open + " 值");
            }

            // 字面量：true / false / null / 裸数字
            while (_i < _s.Length && _s[_i] != ',' && _s[_i] != '}' && _s[_i] != ']')
            {
                _i++;
            }
        }

        private string ReadString()
        {
            if (_s[_i] != '"') { throw new FormatException("期望 '\"'，位置 " + _i); }
            _i++;

            StringBuilder sb = new StringBuilder(32);
            while (_i < _s.Length)
            {
                char c = _s[_i++];
                if (c == '"') { return sb.ToString(); }

                if (c == '\\')
                {
                    if (_i >= _s.Length) { break; }
                    char esc = _s[_i++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 > _s.Length) { throw new FormatException("\\u 转义不完整"); }
                            sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            _i += 4;
                            break;
                        default:
                            throw new FormatException("未知转义 \\" + esc);
                    }

                    continue;
                }

                sb.Append(c);
            }

            throw new FormatException("字符串未闭合");
        }

        private void SkipWhitespace()
        {
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { _i++; }
                else { break; }
            }
        }
    }
}
