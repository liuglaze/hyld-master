// ============================================================================
//  PMBattleContentManifest —— R4-C / C2 正式内容 manifest 的解析与强校验
// ============================================================================
//
//  契约来源：Docs/plans/net-r4c-content-contract.md（冻结 schema + 冻结摘要派生规则）
//
//  字段（**一个字都不许改**，也不许私加共享字段）：
//    formatVersion / mapId / seed / mapResource / playerResource /
//    contentDigest / collisionDigest / worldVersion
//
//  固定值（契约原文）：
//    formatVersion = 1
//    mapId         = "hyld-map2-v1"
//    seed          = 1380205313  (= 0x52444301，构建期固定种子；运行时各自随机一律拒绝)
//    mapResource   = "PMNet/BattleMapV1"    (Resources 键)
//    playerResource= "PMNet/PlayerVisualV1" (Resources 键)
//
//  摘要派生（**冻结**，本类把它实现成"必须相等"，手写常量一律拒绝）：
//    contentDigest    = 小写 SHA256 十六进制串（64 字符）
//    collisionDigest  = contentDigest 的**前 4 个摘要字节按小端**解释成 uint（为 0 则取 1）
//    worldVersion     = (int)(collisionDigest & 0x7fffffff)（为 0 则取 1）
//  ⇒ C1（Editor 烘焙）在写 manifest 时应直接调用本类的
//    TryDeriveCollisionDigestAndWorldVersion，避免两端各写一遍派生规则而漂移。
//
//  ---------------------------------------------------------------------------
//  Unity 依赖面：**只有** UnityEngine.JsonUtility 这一个 Unity API。
//  ---------------------------------------------------------------------------
//    · 资源读取（Resources.Load<TextAsset>）与 prefab 实例化都在 PMUnityBattleMap.cs，
//      不在本文件。这样"纯规则测试"（Tools/PMBattleContentManifestTest）只需要桩
//      JsonUtility 一个类型，就能编译并跑到本文件的**真实规则代码**；
//    · 本文件不打日志、不读文件、不碰 Transform/物理/场景：失败只回错误字符串。
//
//  诚实边界（不许被读成"资源防篡改"承诺）：
//    · 本类**不重算** C1 的编辑器侧内容指纹（模板源指纹 / mesh 引用 / 源 Player 指纹
//      在运行期都不可得），因此只校验 manifest 的**内部一致性**（版本、固定值、
//      路径白名单、摘要格式、派生值相等）；
//    · 「两端同一份资源」由 C3 的显式会话契约（同构建 + digest 握手）守护，
//      本类不声称客户端资源防篡改；
//    · JsonUtility 是**宽容**解析器（未知字段忽略、缺字段留默认值、部分类型不符可能静默），
//      所以强校验必须在解析之后由 Validate 补上，绝不能依赖解析器报错。
// ============================================================================

using System;
using System.Globalization;
using UnityEngine;

namespace PMNet.Unity
{
    /// <summary>
    /// 正式内容 manifest（R4-C 冻结 schema）。
    ///
    /// 字段名/类型即协议：JsonUtility 按**字段名**反序列化，改名或改类型等于改协议。
    /// 本类只承载"这份内容是什么"，不承载任何运动/网络/场景状态。
    /// </summary>
    [Serializable]
    public class PMBattleContentManifest
    {
        // ---------------------------------------------------------------- 冻结常量

        /// <summary>schema 版本（契约冻结 = 1）。</summary>
        public const int ExpectedFormatVersion = 1;

        /// <summary>首图 id（契约冻结：沿用当前真实联机入口 HYLDGame + map2）。</summary>
        public const string ExpectedMapId = "hyld-map2-v1";

        /// <summary>构建期固定种子（0x52444301）。双方加载同一份烘焙产物，不运行时各自随机。</summary>
        public const int ExpectedSeed = 1380205313;

        /// <summary>manifest 自身的 Resources 键（唯一入口，宿主按它读 TextAsset）。</summary>
        public const string ManifestResourceKey = "PMNet/BattleContentV1";

        /// <summary>地图 prefab 的 Resources 键（契约冻结）。</summary>
        public const string MapResourceKey = "PMNet/BattleMapV1";

        /// <summary>角色表现 prefab 的 Resources 键（契约冻结）。</summary>
        public const string PlayerResourceKey = "PMNet/PlayerVisualV1";

        /// <summary>三个共享资源必须落在的 Resources 目录前缀。</summary>
        public const string ResourceDirectory = "PMNet/";

        /// <summary>contentDigest 的十六进制长度（SHA256 = 32 字节）。</summary>
        public const int ContentDigestHexLength = 64;

        /// <summary>资源键的长度上限（防御性；同时避免异常长的键进日志/比对）。</summary>
        public const int MaxResourceKeyLength = 128;

        // ---------------------------------------------------------------- 协议字段
        // 字段名与顺序即协议（顺序不影响 JsonUtility，但按契约顺序书写便于人工比对）。

        /// <summary>schema 版本，必须等于 <see cref="ExpectedFormatVersion"/>。</summary>
        public int formatVersion;

        /// <summary>地图 id，必须等于 <see cref="ExpectedMapId"/>。</summary>
        public string mapId;

        /// <summary>构建期固定种子，必须等于 <see cref="ExpectedSeed"/>。</summary>
        public int seed;

        /// <summary>地图 prefab 的 Resources 键，必须等于 <see cref="MapResourceKey"/>。</summary>
        public string mapResource;

        /// <summary>角色表现 prefab 的 Resources 键，必须等于 <see cref="PlayerResourceKey"/>。</summary>
        public string playerResource;

        /// <summary>内容摘要：64 字符**小写** SHA256 十六进制串。</summary>
        public string contentDigest;

        /// <summary>碰撞摘要：由 contentDigest 前 4 字节按小端派生（非 0）。</summary>
        public uint collisionDigest;

        /// <summary>碰撞世界版本：由 collisionDigest 派生（正数）。</summary>
        public int worldVersion;

        // ---------------------------------------------------------------- 校验

        /// <summary>
        /// 强校验（契约里 C2 的"manifest 强校验 schema1 / 固定 Resources 路径 / seed /
        /// digest 内部一致"）。失败时 <paramref name="error"/> 是**可归因**的中文原因，
        /// 调用方必须据此 fail closed（拒绝加载资源、拒绝上报就绪），不得回退临时内容。
        ///
        /// 规则顺序（先证"是什么"，再证"派生是否自洽"，便于日志定位）：
        ///   1) 非 null；
        ///   2) formatVersion == 1；
        ///   3) mapId == "hyld-map2-v1"；
        ///   4) seed == 1380205313；
        ///   5) mapResource / playerResource 过资源键白名单（目录前缀 + 结构 + 精确键）；
        ///   6) contentDigest 是 64 位小写 hex；
        ///   7) collisionDigest != 0 且 == 由 contentDigest 派生的值；
        ///   8) worldVersion > 0 且 == 由 collisionDigest 派生的值。
        /// </summary>
        public static bool Validate(PMBattleContentManifest manifest, out string error)
        {
            error = null;

            if (manifest == null)
            {
                error = "manifest 为 null：内容资源未生成或解析失败"
                        + "（先执行 Editor 菜单 Build/Prepare PMNet Battle Content，即 C1 的构建期烘焙）。";
                return false;
            }

            if (manifest.formatVersion != ExpectedFormatVersion)
            {
                error = "manifest.formatVersion=" + manifest.formatVersion.ToString(CultureInfo.InvariantCulture)
                        + "，期望 " + ExpectedFormatVersion.ToString(CultureInfo.InvariantCulture)
                        + "：schema 版本不匹配（不做跨版本兼容，拒绝继续）。";
                return false;
            }

            if (!string.Equals(manifest.mapId, ExpectedMapId, StringComparison.Ordinal))
            {
                error = "manifest.mapId=\"" + Quote(manifest.mapId) + "\"，期望 \"" + ExpectedMapId
                        + "\"：首图冻结为 map2（不擅自把 map4/其它模板纳入，也不做多图选择）。";
                return false;
            }

            if (manifest.seed != ExpectedSeed)
            {
                error = "manifest.seed=" + manifest.seed.ToString(CultureInfo.InvariantCulture)
                        + "，期望 " + ExpectedSeed.ToString(CultureInfo.InvariantCulture)
                        + "（0x52444301）：构建期固定种子，运行时各自随机一律拒绝。";
                return false;
            }

            string pathError;
            if (!IsAllowedResourceKey(manifest.mapResource, MapResourceKey, out pathError))
            {
                error = "manifest.mapResource 不合法：" + pathError;
                return false;
            }

            if (!IsAllowedResourceKey(manifest.playerResource, PlayerResourceKey, out pathError))
            {
                error = "manifest.playerResource 不合法：" + pathError;
                return false;
            }

            if (!IsLowercaseSha256Hex(manifest.contentDigest))
            {
                error = "manifest.contentDigest=\"" + Quote(manifest.contentDigest)
                        + "\" 不是 " + ContentDigestHexLength.ToString(CultureInfo.InvariantCulture)
                        + " 字符小写 SHA256 十六进制串。";
                return false;
            }

            uint derivedCollisionDigest;
            if (!TryDeriveCollisionDigest(manifest.contentDigest, out derivedCollisionDigest, out error))
            {
                return false;
            }

            if (manifest.collisionDigest == 0u)
            {
                error = "manifest.collisionDigest=0：契约要求非 0（派生规则里 0 必须取 1）。";
                return false;
            }

            if (manifest.collisionDigest != derivedCollisionDigest)
            {
                error = "manifest.collisionDigest=" + manifest.collisionDigest.ToString(CultureInfo.InvariantCulture)
                        + "，但由 contentDigest 前 4 字节按小端派生得到 "
                        + derivedCollisionDigest.ToString(CultureInfo.InvariantCulture)
                        + "：摘要字段必须由 digest 派生，不许各写一个常量。";
                return false;
            }

            if (manifest.worldVersion <= 0)
            {
                error = "manifest.worldVersion=" + manifest.worldVersion.ToString(CultureInfo.InvariantCulture)
                        + "：契约要求正数（派生规则里 0 必须取 1）。";
                return false;
            }

            int derivedWorldVersion = DeriveWorldVersion(manifest.collisionDigest);
            if (manifest.worldVersion != derivedWorldVersion)
            {
                error = "manifest.worldVersion=" + manifest.worldVersion.ToString(CultureInfo.InvariantCulture)
                        + "，但由 collisionDigest 派生得到 "
                        + derivedWorldVersion.ToString(CultureInfo.InvariantCulture)
                        + "：worldVersion 必须由 collisionDigest 确定派生。";
                return false;
            }

            return true;
        }

        /// <summary>
        /// 用 Unity 的 JsonUtility 解析 manifest 文本，并**紧接着做强校验**
        /// （解析成功但校验失败同样返回 false —— 一个未通过校验的 manifest 不可用，
        /// 让调用方拿到"半成品 manifest"只会把失败推迟到更难定位的地方）。
        ///
        /// 解析异常一律转成错误字符串：manifest 文件损坏/不是 JSON/是空文件都必须显式失败。
        /// </summary>
        public static bool TryParseJson(string json, out PMBattleContentManifest manifest, out string error)
        {
            manifest = null;
            error = null;

            if (json == null)
            {
                error = "manifest JSON 文本为 null：资源未生成（先执行 C1 的 Editor 菜单烘焙）。";
                return false;
            }

            if (json.Length == 0)
            {
                error = "manifest JSON 文本为空（0 字节）：拒绝把空文件当有效 manifest。";
                return false;
            }

            PMBattleContentManifest parsed;
            try
            {
                parsed = JsonUtility.FromJson<PMBattleContentManifest>(json);
            }
            catch (Exception ex)
            {
                error = "manifest JSON 解析失败：" + ex.GetType().Name + " " + ex.Message;
                return false;
            }

            if (parsed == null)
            {
                error = "manifest JSON 解析结果为 null（内容不是对象或不是有效 JSON）。";
                return false;
            }

            if (!Validate(parsed, out error))
            {
                return false;
            }

            manifest = parsed;
            return true;
        }

        // ---------------------------------------------------------------- 摘要派生（冻结规则）

        /// <summary>是否恰好 64 个 [0-9a-f] 字符（小写）。</summary>
        public static bool IsLowercaseSha256Hex(string value)
        {
            if (value == null || value.Length != ContentDigestHexLength)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!hex)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// collisionDigest = contentDigest 前 4 个**摘要字节**按小端解释的 uint；
        /// 结果为 0 时取 1（契约冻结）。
        ///
        /// 为什么不"抽 4 个 hex 字符当大端数字"：契约写的是"前 4 个摘要字节按 LE uint"，
        /// 字节序错了会得到一个同样非 0、但两端各自不同的值，握手时才暴露，代价很大。
        /// </summary>
        public static bool TryDeriveCollisionDigest(string contentDigest, out uint collisionDigest, out string error)
        {
            collisionDigest = 0u;
            error = null;

            if (!IsLowercaseSha256Hex(contentDigest))
            {
                error = "contentDigest 不是 64 字符小写 SHA256 十六进制串，无法派生 collisionDigest。";
                return false;
            }

            uint b0 = HexByte(contentDigest, 0);
            uint b1 = HexByte(contentDigest, 2);
            uint b2 = HexByte(contentDigest, 4);
            uint b3 = HexByte(contentDigest, 6);

            unchecked
            {
                collisionDigest = b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
            }

            if (collisionDigest == 0u)
            {
                collisionDigest = 1u;
            }

            return true;
        }

        /// <summary>worldVersion = (int)(collisionDigest &amp; 0x7fffffff)；为 0 时取 1（契约冻结）。</summary>
        public static int DeriveWorldVersion(uint collisionDigest)
        {
            unchecked
            {
                int version = (int)(collisionDigest & 0x7fffffffu);
                if (version == 0)
                {
                    version = 1;
                }

                return version;
            }
        }

        /// <summary>
        /// 一次拿到两个派生值的便捷入口（C1 写 manifest 时用它，保证与 C2 的校验口径逐位一致）。
        /// </summary>
        public static bool TryDeriveCollisionDigestAndWorldVersion(string contentDigest,
                                                                   out uint collisionDigest,
                                                                   out int worldVersion,
                                                                   out string error)
        {
            worldVersion = 0;
            if (!TryDeriveCollisionDigest(contentDigest, out collisionDigest, out error))
            {
                return false;
            }

            worldVersion = DeriveWorldVersion(collisionDigest);
            return true;
        }

        // ---------------------------------------------------------------- 资源键白名单

        /// <summary>
        /// 资源键白名单：必须**精确等于**契约冻结的那个键，且结构上不许出现
        /// 绝对路径 / 反斜杠 / 上跳 / 扩展名 / 控制字符。
        ///
        /// 为什么要结构检查而不只做字符串相等：相等已经挡住了非法键，但一旦将来
        /// 需要放宽成"允许多个候选键"，结构规则是防线；同时结构错误能给出更有用的错误信息
        /// （"这是路径，不是 Resources 键"）。
        /// </summary>
        public static bool IsAllowedResourceKey(string key, string expectedKey, out string error)
        {
            error = null;

            if (key == null)
            {
                error = "资源键为 null。";
                return false;
            }

            if (key.Length == 0)
            {
                error = "资源键为空串。";
                return false;
            }

            if (key.Length > MaxResourceKeyLength)
            {
                error = "资源键长度 " + key.Length.ToString(CultureInfo.InvariantCulture)
                        + " 超过上限 " + MaxResourceKeyLength.ToString(CultureInfo.InvariantCulture) + "。";
                return false;
            }

            if (key.IndexOf('\\') >= 0)
            {
                error = "\"" + Quote(key) + "\" 含反斜杠：Resources 键必须用正斜杠。";
                return false;
            }

            if (key[0] == '/')
            {
                error = "\"" + Quote(key) + "\" 以 '/' 开头：Resources 键必须是相对 Assets/Resources 的路径。";
                return false;
            }

            if (key.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                error = "\"" + Quote(key) + "\" 含 '..'：拒绝任何上跳。";
                return false;
            }

            if (key.IndexOf(".", StringComparison.Ordinal) >= 0)
            {
                error = "\"" + Quote(key) + "\" 含 '.'：Resources 键不带扩展名（扩展名由 asset 类型决定）。";
                return false;
            }

            if (key.IndexOf("//", StringComparison.Ordinal) >= 0)
            {
                error = "\"" + Quote(key) + "\" 含空目录段 '//'。";
                return false;
            }

            for (int i = 0; i < key.Length; i++)
            {
                if (char.IsControl(key[i]) || char.IsWhiteSpace(key[i]))
                {
                    error = "\"" + Quote(key) + "\" 含控制字符或空白字符。";
                    return false;
                }
            }

            if (!key.StartsWith(ResourceDirectory, StringComparison.Ordinal))
            {
                error = "\"" + Quote(key) + "\" 不在固定目录 \"" + ResourceDirectory + "\" 下。";
                return false;
            }

            if (!string.Equals(key, expectedKey, StringComparison.Ordinal))
            {
                error = "\"" + Quote(key) + "\" 不是契约冻结的键 \"" + expectedKey
                        + "\"（首版资源路径固定，不做候选回退）。";
                return false;
            }

            return true;
        }

        // ---------------------------------------------------------------- 工具

        /// <summary>复制一份（本类字段是值/字符串，浅拷贝即可；避免把外部引用留在宿主里被改写）。</summary>
        public PMBattleContentManifest Clone()
        {
            PMBattleContentManifest copy = new PMBattleContentManifest();
            copy.formatVersion = formatVersion;
            copy.mapId = mapId;
            copy.seed = seed;
            copy.mapResource = mapResource;
            copy.playerResource = playerResource;
            copy.contentDigest = contentDigest;
            copy.collisionDigest = collisionDigest;
            copy.worldVersion = worldVersion;
            return copy;
        }

        /// <summary>一行摘要（进日志/报告用；不含任何需要保密的输入）。</summary>
        public string Describe()
        {
            return "PMBattleContentManifest(formatVersion=" + formatVersion.ToString(CultureInfo.InvariantCulture)
                   + ", mapId=\"" + Quote(mapId) + "\""
                   + ", seed=" + seed.ToString(CultureInfo.InvariantCulture)
                   + ", mapResource=\"" + Quote(mapResource) + "\""
                   + ", playerResource=\"" + Quote(playerResource) + "\""
                   + ", contentDigest=\"" + Quote(contentDigest) + "\""
                   + ", collisionDigest=" + collisionDigest.ToString(CultureInfo.InvariantCulture)
                   + ", worldVersion=" + worldVersion.ToString(CultureInfo.InvariantCulture) + ")";
        }

        private static uint HexByte(string hex, int index)
        {
            uint hi = (uint)HexNibble(hex[index]);
            uint lo = (uint)HexNibble(hex[index + 1]);
            return (hi << 4) | lo;
        }

        private static int HexNibble(char c)
        {
            if (c >= '0' && c <= '9') { return c - '0'; }
            return c - 'a' + 10;
        }

        private static string Quote(string value)
        {
            return value == null ? "<null>" : value;
        }
    }
}
