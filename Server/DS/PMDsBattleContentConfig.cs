// R4-C / C3：Lobby（Server 侧，net8.0）的「正式内容 manifest」只读配置入口。
//
// 为什么单独一个文件：
//   · 它只依赖 BCL + System.Text.Json（net8.0 框架内自带），**不引用 UnityEngine**，
//     也不引用 PMR3Runtime —— Server 侧因此不需要把 Unity 语言面拖进来；
//   · 校验口径必须与 C2 的 Client/Assets/Scripts/PMUnity/PMBattleContentManifest.cs
//     **逐条一致**（schema / 固定值 / 资源白名单 / hash 派生），否则两端会对同一份
//     manifest 得出不同摘要。两侧一致由 Tools/PMBattleContentSessionTest 的对照门禁守住。
//
// 契约（Docs/plans/net-r4c-content-contract.md 的 C3 段）：
//   · 显式会话模式：CollisionDigest == 0 禁止；0x52334201 保留给诊断场景（PMR3TestScene）；
//     正式内容的 digest 不得等于保留值，也不得为 0；
//   · HYLD_PMNET_DS=1 启动时，从仓库内 Client/Assets/Resources/PMNet/BattleContentV1.json
//     读取并校验正式 manifest；缺失/非法 => **拒绝新链启动**，绝不默认回落到诊断摘要；
//   · 允许 HYLD_PMNET_CONTENT_MANIFEST 显式**绝对路径**部署（未设置时从仓库根推导）；
//   · 本文件只读配置，不打印票据/秘密；失败原因里带完整路径，便于部署定位。
//
// 语言面：net8.0（Server.csproj 的默认 glob 覆盖 Server/DS/*.cs）。
// 本文件写成 C# 7.3 兼容的语法，以便任何链入它的工具（net8 或更严格语言面）都能编译。

using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace PMNet.Control
{
    /// <summary>
    /// 正式内容 manifest 的 net8 只读视图（字段与 C2 的 <c>PMBattleContentManifest</c> 同 schema）。
    ///
    /// 它**不是** C2 那个类的替身实现：这里只有「Lobby 需要知道的东西」
    /// （路径 + 摘要 + 世界版本 + 两个资源键），用于分配对局与回报 Ready 摘要。
    /// </summary>
    public struct PMDsBattleContentInfo
    {
        /// <summary>实际读取的 manifest 绝对路径（日志/部署对账用）。</summary>
        public string ManifestPath;

        /// <summary>内容指纹：64 位小写 SHA256 hex。</summary>
        public string ContentDigest;

        /// <summary>碰撞摘要（由 contentDigest 派生；非 0、且不等于保留诊断值）。</summary>
        public uint CollisionDigest;

        /// <summary>碰撞世界版本（由 collisionDigest 派生；正数）。</summary>
        public int WorldVersion;

        /// <summary>地图资源键（必须等于契约冻结值）。</summary>
        public string MapResource;

        /// <summary>角色表现资源键（必须等于契约冻结值）。</summary>
        public string PlayerResource;
    }

    /// <summary>
    /// 正式内容 manifest 的装载 + 强校验（net8）。失败一律返回带路径/字段名的可读原因，
    /// **不**返回一个"部分有效"的对象（与 C2 的 <c>TryParseJson</c> 同纪律）。
    /// </summary>
    public static class PMDsBattleContentConfig
    {
        /// <summary>显式绝对路径覆盖（部署用）。未设置时按仓库根 + 默认相对路径推导。</summary>
        public const string ManifestEnvironmentVariable = "HYLD_PMNET_CONTENT_MANIFEST";

        /// <summary>默认部署位置（相对仓库根；与 C1 的产物路径一致）。</summary>
        public const string DefaultRelativeManifestPath = "Client/Assets/Resources/PMNet/BattleContentV1.json";

        /// <summary>冻结 schema 版本。</summary>
        public const int ExpectedFormatVersion = 1;

        /// <summary>冻结地图标识。</summary>
        public const string ExpectedMapId = "hyld-map2-v1";

        /// <summary>冻结构建期种子（0x52444301）。</summary>
        public const int ExpectedSeed = 1380205313;

        /// <summary>冻结地图资源键。</summary>
        public const string ExpectedMapResource = "PMNet/BattleMapV1";

        /// <summary>冻结角色资源键。</summary>
        public const string ExpectedPlayerResource = "PMNet/PlayerVisualV1";

        /// <summary>内容指纹的十六进制长度（SHA256）。</summary>
        public const int ContentDigestHexLength = 64;

        /// <summary>
        /// 保留给诊断场景（PMR3TestScene）的碰撞摘要。
        ///
        /// 与 <c>PMR3Runtime.CollisionDigest</c> 同值；本文件刻意**不**引用 PMR3Runtime
        /// （避免 net8 配置把 Unity 侧声明链拖进来），两者的相等由
        /// <c>Tools/PMBattleContentSessionTest</c> 的对照断言守住。
        /// </summary>
        public const uint ReservedDiagnosticCollisionDigest = 0x52334201u;

        /// <summary>manifest 文件大小上限（只读配置，超出即拒绝，避免误读大文件）。</summary>
        public const int MaxManifestBytes = 64 * 1024;

        /// <summary>仓库根向上搜索的最大层数。</summary>
        private const int MaxRootSearchDepth = 12;

        /// <summary>
        /// 解析 manifest 路径：环境变量优先（必须是绝对路径），否则从仓库根推导默认路径。
        /// 只做路径推导**不**做存在性判断（存在性由 <see cref="TryLoad"/> 负责）。
        /// </summary>
        public static bool TryResolveManifestPath(out string path, out string error)
        {
            path = null;
            error = null;

            string fromEnvironment = Environment.GetEnvironmentVariable(ManifestEnvironmentVariable);
            if (!string.IsNullOrEmpty(fromEnvironment))
            {
                if (!Path.IsPathRooted(fromEnvironment))
                {
                    error = ManifestEnvironmentVariable + " 必须是绝对路径（实际 '" + fromEnvironment
                            + "'）：不支持相对路径，避免对局依赖进程工作目录";
                    return false;
                }

                path = fromEnvironment;
                return true;
            }

            string root = FindRepositoryRoot();
            if (string.IsNullOrEmpty(root))
            {
                error = "未设置 " + ManifestEnvironmentVariable + "，且无法从可执行文件目录向上定位仓库根"
                        + "（需要同时含 'Server' 与 'Client' 子目录的那一层）";
                return false;
            }

            path = Path.Combine(root, DefaultRelativeManifestPath);
            return true;
        }

        /// <summary>读取 + 强校验正式 manifest。任何失败都返回 false 并写可读原因。</summary>
        public static bool TryLoad(out PMDsBattleContentInfo info, out string error)
        {
            info = default(PMDsBattleContentInfo);
            error = null;

            string path;
            string pathError;
            if (!TryResolveManifestPath(out path, out pathError))
            {
                error = pathError;
                return false;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception ex)
            {
                error = "manifest 路径非法（'" + path + "'）：" + ex.GetType().Name + " " + ex.Message;
                return false;
            }

            if (!File.Exists(fullPath))
            {
                error = "正式内容 manifest 不存在（路径='" + fullPath
                        + "'）：请先产出该文件（或用 " + ManifestEnvironmentVariable + " 指向部署路径）；"
                        + "本链不回退诊断内容";
                return false;
            }

            long length;
            try
            {
                length = new FileInfo(fullPath).Length;
            }
            catch (Exception ex)
            {
                error = "读取 manifest 长度失败（路径='" + fullPath + "'）：" + ex.GetType().Name + " " + ex.Message;
                return false;
            }

            if (length <= 0L || length > (long)MaxManifestBytes)
            {
                error = "manifest 文件大小非法（路径='" + fullPath + "'，实际 " + length.ToString(CultureInfo.InvariantCulture)
                        + " 字节，允许 1.." + MaxManifestBytes.ToString(CultureInfo.InvariantCulture) + "）";
                return false;
            }

            string json;
            try
            {
                json = File.ReadAllText(fullPath);
            }
            catch (Exception ex)
            {
                error = "读取 manifest 失败（路径='" + fullPath + "'）：" + ex.GetType().Name + " " + ex.Message;
                return false;
            }

            return TryParse(json, fullPath, out info, out error);
        }

        /// <summary>
        /// 解析 + 强校验（纯函数，便于门禁用字符串直接跑正反例）。
        /// </summary>
        public static bool TryParse(string json, string manifestPath, out PMDsBattleContentInfo info, out string error)
        {
            info = default(PMDsBattleContentInfo);
            error = null;

            if (string.IsNullOrEmpty(json))
            {
                error = "manifest 内容为空（路径='" + (manifestPath ?? "<null>") + "'）";
                return false;
            }

            JsonDocumentOptions options = new JsonDocumentOptions();
            options.AllowTrailingCommas = false;
            options.CommentHandling = JsonCommentHandling.Disallow;
            options.MaxDepth = 16;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json, options);
            }
            catch (JsonException ex)
            {
                error = "manifest 不是合法 JSON（路径='" + (manifestPath ?? "<null>") + "'）：" + ex.Message;
                return false;
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    error = "manifest 根必须是 JSON 对象（实际 " + root.ValueKind.ToString() + "）";
                    return false;
                }

                int formatVersion;
                if (!TryReadInt32(root, "formatVersion", out formatVersion, out error)) { return false; }
                if (formatVersion != ExpectedFormatVersion)
                {
                    error = "formatVersion 必须为 " + ExpectedFormatVersion.ToString(CultureInfo.InvariantCulture)
                            + "（实际 " + formatVersion.ToString(CultureInfo.InvariantCulture) + "）";
                    return false;
                }

                string mapId;
                if (!TryReadString(root, "mapId", out mapId, out error)) { return false; }
                if (!string.Equals(mapId, ExpectedMapId, StringComparison.Ordinal))
                {
                    error = "mapId 必须为 '" + ExpectedMapId + "'（实际 '" + mapId + "'）";
                    return false;
                }

                int seed;
                if (!TryReadInt32(root, "seed", out seed, out error)) { return false; }
                if (seed != ExpectedSeed)
                {
                    error = "seed 必须为 " + ExpectedSeed.ToString(CultureInfo.InvariantCulture)
                            + "（实际 " + seed.ToString(CultureInfo.InvariantCulture) + "）";
                    return false;
                }

                string mapResource;
                if (!TryReadString(root, "mapResource", out mapResource, out error)) { return false; }
                if (!string.Equals(mapResource, ExpectedMapResource, StringComparison.Ordinal))
                {
                    error = "mapResource 必须为 '" + ExpectedMapResource + "'（实际 '" + mapResource + "'）";
                    return false;
                }

                string playerResource;
                if (!TryReadString(root, "playerResource", out playerResource, out error)) { return false; }
                if (!string.Equals(playerResource, ExpectedPlayerResource, StringComparison.Ordinal))
                {
                    error = "playerResource 必须为 '" + ExpectedPlayerResource + "'（实际 '" + playerResource + "'）";
                    return false;
                }

                string contentDigest;
                if (!TryReadString(root, "contentDigest", out contentDigest, out error)) { return false; }
                if (!IsLowercaseSha256Hex(contentDigest))
                {
                    error = "contentDigest 必须是 " + ContentDigestHexLength.ToString(CultureInfo.InvariantCulture)
                            + " 位小写十六进制（实际 '" + (contentDigest ?? "<null>") + "'）";
                    return false;
                }

                uint collisionDigest;
                if (!TryReadUInt32(root, "collisionDigest", out collisionDigest, out error)) { return false; }

                int worldVersion;
                if (!TryReadInt32(root, "worldVersion", out worldVersion, out error)) { return false; }

                uint derivedDigest;
                string deriveError;
                if (!TryDeriveCollisionDigest(contentDigest, out derivedDigest, out deriveError))
                {
                    error = deriveError;
                    return false;
                }

                if (collisionDigest == 0u)
                {
                    error = "collisionDigest 不得为 0（0 表示未选择内容）";
                    return false;
                }

                if (collisionDigest == ReservedDiagnosticCollisionDigest)
                {
                    error = "collisionDigest 等于保留诊断值 0x"
                            + ReservedDiagnosticCollisionDigest.ToString("X8", CultureInfo.InvariantCulture)
                            + "：正式内容不得撞保留值（诊断内容不得作为正式内容分配）";
                    return false;
                }

                if (collisionDigest != derivedDigest)
                {
                    error = "collisionDigest 与 contentDigest 派生值不一致：manifest=0x"
                            + collisionDigest.ToString("X8", CultureInfo.InvariantCulture)
                            + "，派生=0x" + derivedDigest.ToString("X8", CultureInfo.InvariantCulture)
                            + "（手写常量一律拒绝）";
                    return false;
                }

                int derivedWorldVersion = DeriveWorldVersion(collisionDigest);
                if (worldVersion != derivedWorldVersion)
                {
                    error = "worldVersion 与 collisionDigest 派生值不一致：manifest="
                            + worldVersion.ToString(CultureInfo.InvariantCulture)
                            + "，派生=" + derivedWorldVersion.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                if (worldVersion <= 0)
                {
                    error = "worldVersion 必须为正数（实际 " + worldVersion.ToString(CultureInfo.InvariantCulture) + "）";
                    return false;
                }

                PMDsBattleContentInfo parsed = default(PMDsBattleContentInfo);
                parsed.ManifestPath = manifestPath;
                parsed.ContentDigest = contentDigest;
                parsed.CollisionDigest = collisionDigest;
                parsed.WorldVersion = worldVersion;
                parsed.MapResource = mapResource;
                parsed.PlayerResource = playerResource;
                info = parsed;
                return true;
            }
        }

        /// <summary>严格小写 hex（长度 64）：大写/长度错/非 hex/null 一律 false。</summary>
        public static bool IsLowercaseSha256Hex(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != ContentDigestHexLength)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool digit = c >= '0' && c <= '9';
                bool lower = c >= 'a' && c <= 'f';
                if (!digit && !lower)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 摘要派生（与 C2 冻结口径逐字一致）：前 4 个摘要字节按**小端**组成 uint，0 → 1。
        /// </summary>
        public static bool TryDeriveCollisionDigest(string contentDigest, out uint collisionDigest, out string error)
        {
            collisionDigest = 0u;
            error = null;

            if (!IsLowercaseSha256Hex(contentDigest))
            {
                error = "contentDigest 必须是 " + ContentDigestHexLength.ToString(CultureInfo.InvariantCulture)
                        + " 位小写十六进制";
                return false;
            }

            uint b0 = ParseHexByte(contentDigest, 0);
            uint b1 = ParseHexByte(contentDigest, 2);
            uint b2 = ParseHexByte(contentDigest, 4);
            uint b3 = ParseHexByte(contentDigest, 6);

            uint value = b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
            if (value == 0u)
            {
                value = 1u;
            }

            collisionDigest = value;
            return true;
        }

        /// <summary>世界版本派生（与 C2 冻结口径逐字一致）：<c>collisionDigest &amp; 0x7fffffff</c>，0 → 1。</summary>
        public static int DeriveWorldVersion(uint collisionDigest)
        {
            int version = (int)(collisionDigest & 0x7fffffffu);
            if (version == 0)
            {
                version = 1;
            }

            return version;
        }

        // ────────────────────────────────────────────────────────────────
        //  内部
        // ────────────────────────────────────────────────────────────────

        private static uint ParseHexByte(string value, int offset)
        {
            int high = HexNibble(value[offset]);
            int low = HexNibble(value[offset + 1]);
            return (uint)((high << 4) | low);
        }

        private static int HexNibble(char c)
        {
            if (c >= '0' && c <= '9') { return c - '0'; }
            return c - 'a' + 10;
        }

        private static bool TryReadInt32(JsonElement root, string name, out int value, out string error)
        {
            value = 0;
            error = null;

            JsonElement element;
            if (!root.TryGetProperty(name, out element))
            {
                error = "manifest 缺少字段 '" + name + "'";
                return false;
            }

            if (element.ValueKind != JsonValueKind.Number)
            {
                error = "字段 '" + name + "' 必须是 JSON 数字（实际 " + element.ValueKind.ToString() + "）";
                return false;
            }

            long raw;
            if (!element.TryGetInt64(out raw) || raw < int.MinValue || raw > int.MaxValue)
            {
                error = "字段 '" + name + "' 不是 int32 范围内的整数";
                return false;
            }

            value = (int)raw;
            return true;
        }

        private static bool TryReadUInt32(JsonElement root, string name, out uint value, out string error)
        {
            value = 0u;
            error = null;

            JsonElement element;
            if (!root.TryGetProperty(name, out element))
            {
                error = "manifest 缺少字段 '" + name + "'";
                return false;
            }

            if (element.ValueKind != JsonValueKind.Number)
            {
                error = "字段 '" + name + "' 必须是 JSON 数字（实际 " + element.ValueKind.ToString() + "）";
                return false;
            }

            long raw;
            if (!element.TryGetInt64(out raw) || raw < 0L || raw > (long)uint.MaxValue)
            {
                error = "字段 '" + name + "' 不是 uint32 范围内的整数（实际 " + element.GetRawText() + "）";
                return false;
            }

            value = (uint)raw;
            return true;
        }

        private static bool TryReadString(JsonElement root, string name, out string value, out string error)
        {
            value = null;
            error = null;

            JsonElement element;
            if (!root.TryGetProperty(name, out element))
            {
                error = "manifest 缺少字段 '" + name + "'";
                return false;
            }

            if (element.ValueKind != JsonValueKind.String)
            {
                error = "字段 '" + name + "' 必须是 JSON 字符串（实际 " + element.ValueKind.ToString() + "）";
                return false;
            }

            value = element.GetString();
            return true;
        }

        /// <summary>
        /// 从可执行文件目录向上找「同时含 Server 与 Client 子目录」的那一层（= 工程根）。
        /// 与 <c>Program.cs</c> 的 <c>ResolveLogDirectory</c> 同一口径：**不猜**用户目录。
        /// </summary>
        private static string FindRepositoryRoot()
        {
            string dir;
            try
            {
                dir = AppContext.BaseDirectory;
            }
            catch (Exception)
            {
                return null;
            }

            for (int i = 0; i < MaxRootSearchDepth && !string.IsNullOrEmpty(dir); i++)
            {
                try
                {
                    if (Directory.Exists(Path.Combine(dir, "Server"))
                        && Directory.Exists(Path.Combine(dir, "Client")))
                    {
                        return dir;
                    }
                }
                catch (Exception)
                {
                    return null;
                }

                DirectoryInfo parent = Directory.GetParent(dir);
                dir = parent == null ? null : parent.FullName;
            }

            return null;
        }
    }
}
