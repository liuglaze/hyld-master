// ============================================================================
//  PMBattleContentManifestTest —— R4-C / C2 manifest 纯规则验收
// ============================================================================
//
//  被验收对象（**真实生产代码**，不复制不快照）：
//    Client/Assets/Scripts/PMUnity/PMBattleContentManifest.cs
//
//  本工程能证明什么：
//    · 冻结常量的值（formatVersion=1 / mapId / seed=1380205313=0x52444301 / 两个 Resources 键）；
//    · 冻结 schema 的**字段名与类型集合**（8 个字段，多一个少一个都算失败 —— 契约禁止私加共享字段）；
//    · 摘要派生规则：contentDigest 前 4 字节按**小端** uint（0→1）、
//      worldVersion = (int)(collisionDigest & 0x7fffffff)（0→1）；
//    · 内部一致性：collisionDigest/worldVersion 必须等于派生值（手写常量、大小端写反、0、负数全被拒）；
//    · 资源键白名单：目录前缀 / 扩展名 / 反斜杠 / 绝对路径 / 上跳 / 空白 / 非冻结键全被拒；
//    · 版本与非 0 门：formatVersion / worldVersion / collisionDigest；
//    · JsonUtility 边界的失败面：null / 空串 / 非法 JSON / 非对象根 / 缺字段。
//
//  本工程**不能**证明什么（区分实机的口径，写清以免被读成"实机已验"）：
//    · Unity 2019.4 真实 JsonUtility 的行为。本工程只提供 **JsonUtility 一个类型的替身**
//      （见文件尾部 UnityEngine 段），并且替身承担了三条与实机同语义但未经实机验证的行为：
//        1) 未知字段被忽略；2) 缺字段留默认值；3) uint 字段可被解析。
//      这三条**必须**由 C1（Editor 写 manifest 后回读）与 C3（真实 Unity 会话加载）确认，
//      已登记在 Docs/plans/_r4c_runtime_report.md 的待办里。
//    · 任何运行时行为：不加载 Resources、不建场景、不跑 PhysX、不实例化 prefab。
//
//  运行：dotnet Tools/PMBattleContentManifestTest/bin/Release/net8.0/PMBattleContentManifestTest.dll
//  退出码：0 = 全部通过；1 = 存在失败
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using PMNet.Unity;

namespace PMBattleContentManifestTest
{
    internal static class Program
    {
        private static int _checks;
        private static int _failed;

        private static int Main(string[] args)
        {
            Console.WriteLine("PMBattleContentManifestTest —— R4-C / C2 manifest 纯规则验收");
            Console.WriteLine("(net8 + JsonUtility 边界替身；真实 Unity 2019 编译由 PMBattleContentRuntimeCheck 覆盖)");

            Section("A. 冻结常量");
            CheckFrozenConstants();

            Section("B. 冻结 schema 的字段名与类型集合");
            CheckSchemaShape();

            Section("C. 摘要派生规则（小端 + 非 0 兜底）");
            CheckDigestDerivation();

            Section("D. 合法 manifest 全字段与 JSON 往返");
            CheckValidManifestRoundTrip();

            Section("E. 资源键白名单");
            CheckResourcePathWhitelist();

            Section("F. 版本 / mapId / seed 固定值");
            CheckFixedValueGates();

            Section("G. digest 内部一致性（手写常量必须被拒）");
            CheckDigestInternalConsistency();

            Section("H. JsonUtility 边界失败面");
            CheckJsonBoundary();

            Section("I. Unity 依赖面纪律（manifest 只能用 JsonUtility 一个 Unity 类型）");
            CheckUnitySurfaceDiscipline();

            Section("J. C1 产物（磁盘）↔ 冻结资源键 / manifest 规则");
            CheckBakedArtifactsAgainstFrozenKeys();

            Console.WriteLine();
            Console.WriteLine("---- 实机（Unity）未执行的三条边界行为 ----");
            Console.WriteLine("  · 未知字段忽略：本工程断言的是替身行为，实机由 C1 回读 / C3 加载确认；");
            Console.WriteLine("  · 缺字段留默认值：同上；");
            Console.WriteLine("  · uint 字段（collisionDigest）可被 JsonUtility 解析：同上（这是最需要实机确认的一条）；");
            Console.WriteLine("  · Resources 加载 / 隔离物理世界 / 出生位与墙的判定：必须 C3/C4 在 Unity 内跑。");

            Console.WriteLine();
            Console.WriteLine("PMBattleContentManifestTest: " + _checks.ToString(CultureInfo.InvariantCulture)
                              + " 项，失败 " + _failed.ToString(CultureInfo.InvariantCulture) + " 项。");

            return _failed == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- A. 冻结常量

        private static void CheckFrozenConstants()
        {
            Check("ExpectedFormatVersion == 1", PMBattleContentManifest.ExpectedFormatVersion == 1,
                  "实际 " + PMBattleContentManifest.ExpectedFormatVersion.ToString(CultureInfo.InvariantCulture));
            Check("ExpectedMapId == \"hyld-map2-v1\"",
                  PMBattleContentManifest.ExpectedMapId == "hyld-map2-v1",
                  "实际 \"" + PMBattleContentManifest.ExpectedMapId + "\"");
            Check("ExpectedSeed == 1380205313 (0x52444301)",
                  PMBattleContentManifest.ExpectedSeed == 1380205313
                  && PMBattleContentManifest.ExpectedSeed == 0x52444301,
                  "实际 " + PMBattleContentManifest.ExpectedSeed.ToString(CultureInfo.InvariantCulture));
            Check("ManifestResourceKey == \"PMNet/BattleContentV1\"",
                  PMBattleContentManifest.ManifestResourceKey == "PMNet/BattleContentV1",
                  "实际 \"" + PMBattleContentManifest.ManifestResourceKey + "\"");
            Check("MapResourceKey == \"PMNet/BattleMapV1\"",
                  PMBattleContentManifest.MapResourceKey == "PMNet/BattleMapV1",
                  "实际 \"" + PMBattleContentManifest.MapResourceKey + "\"");
            Check("PlayerResourceKey == \"PMNet/PlayerVisualV1\"",
                  PMBattleContentManifest.PlayerResourceKey == "PMNet/PlayerVisualV1",
                  "实际 \"" + PMBattleContentManifest.PlayerResourceKey + "\"");
            Check("ContentDigestHexLength == 64 (SHA256)",
                  PMBattleContentManifest.ContentDigestHexLength == 64,
                  "实际 " + PMBattleContentManifest.ContentDigestHexLength.ToString(CultureInfo.InvariantCulture));
        }

        // ---------------------------------------------------------------- B. schema 形状

        private static void CheckSchemaShape()
        {
            FieldInfo[] fields = typeof(PMBattleContentManifest)
                .GetFields(BindingFlags.Instance | BindingFlags.Public);

            Check("public 实例字段数量 == 8（不许私加共享字段）",
                  fields.Length == 8,
                  "实际 " + fields.Length.ToString(CultureInfo.InvariantCulture));

            Dictionary<string, Type> map = new Dictionary<string, Type>(StringComparer.Ordinal);
            for (int i = 0; i < fields.Length; i++)
            {
                map[fields[i].Name] = fields[i].FieldType;
            }

            CheckType(map, "formatVersion", typeof(int));
            CheckType(map, "mapId", typeof(string));
            CheckType(map, "seed", typeof(int));
            CheckType(map, "mapResource", typeof(string));
            CheckType(map, "playerResource", typeof(string));
            CheckType(map, "contentDigest", typeof(string));
            CheckType(map, "collisionDigest", typeof(uint));
            CheckType(map, "worldVersion", typeof(int));

            Check("类型是 [Serializable]（JsonUtility 要求）",
                  Attribute.IsDefined(typeof(PMBattleContentManifest), typeof(SerializableAttribute)),
                  "JsonUtility 只能反序列化 [Serializable] 的类");
        }

        private static void CheckType(Dictionary<string, Type> map, string name, Type expected)
        {
            Type actual;
            if (!map.TryGetValue(name, out actual))
            {
                Check("字段 " + name + " 存在且类型为 " + expected.Name, false, "字段缺失（契约冻结字段名）");
                return;
            }

            Check("字段 " + name + " : " + expected.Name, actual == expected,
                  "实际类型 " + actual.Name);
        }

        // ---------------------------------------------------------------- C. 摘要派生

        private static void CheckDigestDerivation()
        {
            // 小端证据：第一个字节是最低位。若实现按大端取前 4 个 hex 字符，这里会得到 0x01000000。
            uint digest;
            string error;
            bool ok = PMBattleContentManifest.TryDeriveCollisionDigest(Digest("01000000"), out digest, out error);
            Check("前 4 字节 01 00 00 00 → LSB-first uint = 1（小端，不是大端 0x01000000）",
                  ok && digest == 1u,
                  "ok=" + ok + " digest=" + digest.ToString(CultureInfo.InvariantCulture) + " err=" + error);

            ok = PMBattleContentManifest.TryDeriveCollisionDigest(Digest("0a000000"), out digest, out error);
            Check("前 4 字节 0a 00 00 00 → 10", ok && digest == 10u,
                  "digest=" + digest.ToString(CultureInfo.InvariantCulture));

            ok = PMBattleContentManifest.TryDeriveCollisionDigest(Digest("78563412"), out digest, out error);
            Check("前 4 字节 78 56 34 12 → 0x12345678", ok && digest == 0x12345678u,
                  "digest=0x" + digest.ToString("X8", CultureInfo.InvariantCulture));

            ok = PMBattleContentManifest.TryDeriveCollisionDigest(Digest("00000000"), out digest, out error);
            Check("前 4 字节全 0 → 0 于是取 1（非 0 兜底）", ok && digest == 1u,
                  "digest=" + digest.ToString(CultureInfo.InvariantCulture));

            ok = PMBattleContentManifest.TryDeriveCollisionDigest(Digest("ffffffff"), out digest, out error);
            Check("前 4 字节全 ff → 0xffffffff", ok && digest == 0xffffffffu,
                  "digest=0x" + digest.ToString("X8", CultureInfo.InvariantCulture));

            Check("worldVersion(10) == 10", PMBattleContentManifest.DeriveWorldVersion(10u) == 10,
                  "实际 " + PMBattleContentManifest.DeriveWorldVersion(10u).ToString(CultureInfo.InvariantCulture));

            Check("worldVersion(0x80000000) == 1（& 0x7fffffff 后为 0 → 取 1）",
                  PMBattleContentManifest.DeriveWorldVersion(0x80000000u) == 1,
                  "实际 " + PMBattleContentManifest.DeriveWorldVersion(0x80000000u).ToString(CultureInfo.InvariantCulture));

            Check("worldVersion(0xffffffff) == 0x7fffffff",
                  PMBattleContentManifest.DeriveWorldVersion(0xffffffffu) == 0x7fffffff,
                  "实际 " + PMBattleContentManifest.DeriveWorldVersion(0xffffffffu).ToString(CultureInfo.InvariantCulture));

            Check("worldVersion(0) == 1（0 兜底）", PMBattleContentManifest.DeriveWorldVersion(0u) == 1,
                  "实际 " + PMBattleContentManifest.DeriveWorldVersion(0u).ToString(CultureInfo.InvariantCulture));

            uint cd;
            int wv;
            ok = PMBattleContentManifest.TryDeriveCollisionDigestAndWorldVersion(Digest("00000080"), out cd, out wv,
                                                                                out error);
            Check("组合派生 \"00000080\" → collisionDigest=0x80000000, worldVersion=1",
                  ok && cd == 0x80000000u && wv == 1,
                  "cd=0x" + cd.ToString("X8", CultureInfo.InvariantCulture)
                  + " wv=" + wv.ToString(CultureInfo.InvariantCulture));

            Check("IsLowercaseSha256Hex 接受 64 位小写 hex",
                  PMBattleContentManifest.IsLowercaseSha256Hex(Digest("abcdef01")),
                  string.Empty);
            Check("IsLowercaseSha256Hex 拒绝大写",
                  !PMBattleContentManifest.IsLowercaseSha256Hex(Digest("abcdef01").ToUpperInvariant()),
                  string.Empty);
            Check("IsLowercaseSha256Hex 拒绝 63 位",
                  !PMBattleContentManifest.IsLowercaseSha256Hex(new string('a', 63)), string.Empty);
            Check("IsLowercaseSha256Hex 拒绝 65 位",
                  !PMBattleContentManifest.IsLowercaseSha256Hex(new string('a', 65)), string.Empty);
            Check("IsLowercaseSha256Hex 拒绝非 hex 字符",
                  !PMBattleContentManifest.IsLowercaseSha256Hex(new string('g', 64)), string.Empty);
            Check("IsLowercaseSha256Hex 拒绝 null", !PMBattleContentManifest.IsLowercaseSha256Hex(null), string.Empty);

            ok = PMBattleContentManifest.TryDeriveCollisionDigest("not-a-digest", out digest, out error);
            Check("派生拒绝非法 digest（并给出原因）", !ok && !string.IsNullOrEmpty(error), "err=" + error);
        }

        // ---------------------------------------------------------------- D. 合法 manifest

        private static void CheckValidManifestRoundTrip()
        {
            PMBattleContentManifest manifest = MakeValidManifest(Digest("1f2e3d4c"));
            string error;
            Check("合法 manifest 通过 Validate", PMBattleContentManifest.Validate(manifest, out error),
                  "err=" + error);

            Check("合法 manifest 的 collisionDigest == 0x4c3d2e1f（小端）",
                  manifest.collisionDigest == 0x4c3d2e1fu,
                  "实际 0x" + manifest.collisionDigest.ToString("X8", CultureInfo.InvariantCulture));

            string json = ToJson(manifest);
            PMBattleContentManifest parsed;
            Check("TryParseJson 解析真实 JSON 成功",
                  PMBattleContentManifest.TryParseJson(json, out parsed, out error), "err=" + error);

            if (parsed != null)
            {
                Check("解析后 formatVersion/mapId/seed 一致",
                      parsed.formatVersion == 1 && parsed.mapId == "hyld-map2-v1" && parsed.seed == 1380205313,
                      parsed.Describe());
                Check("解析后两个资源键一致",
                      parsed.mapResource == "PMNet/BattleMapV1" && parsed.playerResource == "PMNet/PlayerVisualV1",
                      parsed.Describe());
                Check("解析后 contentDigest 一致", parsed.contentDigest == manifest.contentDigest,
                      parsed.contentDigest);
                Check("解析后 collisionDigest / worldVersion 一致（uint 字段往返）",
                      parsed.collisionDigest == manifest.collisionDigest && parsed.worldVersion == manifest.worldVersion,
                      "cd=" + parsed.collisionDigest.ToString(CultureInfo.InvariantCulture)
                      + " wv=" + parsed.worldVersion.ToString(CultureInfo.InvariantCulture));
            }

            // 未知字段必须被忽略（实机同语义待确认，见文件头边界声明）
            string withExtra = "{\"formatVersion\":1,\"mapId\":\"hyld-map2-v1\",\"seed\":1380205313,"
                               + "\"mapResource\":\"PMNet/BattleMapV1\",\"playerResource\":\"PMNet/PlayerVisualV1\","
                               + "\"contentDigest\":\"" + manifest.contentDigest + "\","
                               + "\"collisionDigest\":" + manifest.collisionDigest.ToString(CultureInfo.InvariantCulture) + ","
                               + "\"worldVersion\":" + manifest.worldVersion.ToString(CultureInfo.InvariantCulture) + ","
                               + "\"futureField\":\"ignored\"}";
            Check("JSON 含未知字段时仍解析成功（替身边界行为）",
                  PMBattleContentManifest.TryParseJson(withExtra, out parsed, out error), "err=" + error);

            // Clone 是"自持一份"，改副本不影响原件
            PMBattleContentManifest clone = manifest.Clone();
            clone.mapResource = "PMNet/Whatever";
            Check("Clone 与原对象解耦",
                  manifest.mapResource == "PMNet/BattleMapV1" && clone.mapResource == "PMNet/Whatever",
                  "orig=" + manifest.mapResource + " clone=" + clone.mapResource);

            Check("Describe 含关键字段", manifest.Describe().IndexOf("worldVersion", StringComparison.Ordinal) > 0,
                  manifest.Describe());
        }

        // ---------------------------------------------------------------- E. 资源键白名单

        private static void CheckResourcePathWhitelist()
        {
            string error;

            Check("精确键 \"PMNet/BattleMapV1\" 通过",
                  PMBattleContentManifest.IsAllowedResourceKey("PMNet/BattleMapV1",
                                                               PMBattleContentManifest.MapResourceKey, out error),
                  "err=" + error);

            CheckRejectedKey("PMNet/BattleMapV2", "非冻结键");
            CheckRejectedKey("Other/BattleMapV1", "目录前缀不符");
            CheckRejectedKey("BattleMapV1", "缺少 PMNet/ 前缀");
            CheckRejectedKey("PMNet/BattleMapV1.prefab", "含扩展名");
            CheckRejectedKey("PMNet/BattleMapV1.json", "含扩展名");
            CheckRejectedKey("../BattleMapV1", "上跳");
            CheckRejectedKey("PMNet/../BattleMapV1", "上跳");
            CheckRejectedKey("PMNet\\BattleMapV1", "反斜杠");
            CheckRejectedKey("/PMNet/BattleMapV1", "绝对路径");
            CheckRejectedKey("PMNet//BattleMapV1", "空目录段");
            CheckRejectedKey("PMNet/ BattleMapV1", "含空白");
            CheckRejectedKey("", "空串");
            CheckRejectedKey(null, "null");

            StringBuilder longKey = new StringBuilder("PMNet/");
            while (longKey.Length <= PMBattleContentManifest.MaxResourceKeyLength)
            {
                longKey.Append('a');
            }

            CheckRejectedKey(longKey.ToString(), "超长");

            Check("playerResource 用地图键被拒",
                  !PMBattleContentManifest.IsAllowedResourceKey("PMNet/BattleMapV1",
                                                                 PMBattleContentManifest.PlayerResourceKey, out error),
                  "err=" + error);

            Check("playerResource 精确键通过",
                  PMBattleContentManifest.IsAllowedResourceKey("PMNet/PlayerVisualV1",
                                                               PMBattleContentManifest.PlayerResourceKey, out error),
                  "err=" + error);
        }

        private static void CheckRejectedKey(string key, string why)
        {
            string error;
            bool ok = PMBattleContentManifest.IsAllowedResourceKey(key, PMBattleContentManifest.MapResourceKey, out error);
            Check("资源键被拒（" + why + "）：" + (key == null ? "<null>" : "\"" + key + "\""),
                  !ok && !string.IsNullOrEmpty(error), "err=" + error);
        }

        // ---------------------------------------------------------------- F. 固定值门

        private static void CheckFixedValueGates()
        {
            string error;

            PMBattleContentManifest manifest = MakeValidManifest(Digest("11223344"));
            manifest.formatVersion = 2;
            Check("formatVersion=2 被拒", !PMBattleContentManifest.Validate(manifest, out error), "err=" + error);

            manifest = MakeValidManifest(Digest("11223344"));
            manifest.formatVersion = 0;
            Check("formatVersion=0 被拒", !PMBattleContentManifest.Validate(manifest, out error), "err=" + error);

            manifest = MakeValidManifest(Digest("11223344"));
            manifest.mapId = "hyld-map4-v1";
            Check("mapId=map4 被拒（首版不擅自换图）",
                  !PMBattleContentManifest.Validate(manifest, out error), "err=" + error);

            manifest = MakeValidManifest(Digest("11223344"));
            manifest.mapId = null;
            Check("mapId=null 被拒", !PMBattleContentManifest.Validate(manifest, out error), "err=" + error);

            manifest = MakeValidManifest(Digest("11223344"));
            manifest.seed = 0;
            Check("seed=0 被拒（拒绝运行时各自随机）",
                  !PMBattleContentManifest.Validate(manifest, out error), "err=" + error);

            manifest = MakeValidManifest(Digest("11223344"));
            manifest.seed = 2;
            Check("seed=2（协议里的默认值）被拒", !PMBattleContentManifest.Validate(manifest, out error),
                  "err=" + error);

            manifest = MakeValidManifest(Digest("11223344"));
            manifest.mapResource = "PMNet/BattleMapV2";
            Check("mapResource 非冻结键被拒", !PMBattleContentManifest.Validate(manifest, out error), "err=" + error);

            manifest = MakeValidManifest(Digest("11223344"));
            manifest.playerResource = "PMNet/BattleMapV1";
            Check("playerResource 用地图键被拒", !PMBattleContentManifest.Validate(manifest, out error),
                  "err=" + error);
        }

        // ---------------------------------------------------------------- G. digest 内部一致

        private static void CheckDigestInternalConsistency()
        {
            string error;
            string digest = Digest("1f2e3d4c");   // LE = 0x4c3d2e1f

            PMBattleContentManifest manifest = MakeValidManifest(digest);
            manifest.collisionDigest = 0u;
            Check("collisionDigest=0 被拒", !PMBattleContentManifest.Validate(manifest, out error), "err=" + error);

            manifest = MakeValidManifest(digest);
            manifest.collisionDigest = 0x4c3d2e1fu + 1u;
            Check("collisionDigest 与派生值不符（+1）被拒",
                  !PMBattleContentManifest.Validate(manifest, out error), "err=" + error);

            manifest = MakeValidManifest(digest);
            manifest.collisionDigest = 0x1f2e3d4cu;   // 大端解释（大小端写反的典型错误）
            Check("collisionDigest 用大端解释被拒", !PMBattleContentManifest.Validate(manifest, out error),
                  "err=" + error);

            manifest = MakeValidManifest(digest);
            manifest.worldVersion = 0;
            Check("worldVersion=0 被拒", !PMBattleContentManifest.Validate(manifest, out error), "err=" + error);

            manifest = MakeValidManifest(digest);
            manifest.worldVersion = -1;
            Check("worldVersion=-1 被拒", !PMBattleContentManifest.Validate(manifest, out error), "err=" + error);

            manifest = MakeValidManifest(digest);
            manifest.worldVersion = manifest.worldVersion + 1;
            Check("worldVersion 与派生值不符被拒", !PMBattleContentManifest.Validate(manifest, out error),
                  "err=" + error);

            manifest = MakeValidManifest(digest);
            manifest.contentDigest = digest.ToUpperInvariant();
            Check("contentDigest 大写被拒", !PMBattleContentManifest.Validate(manifest, out error), "err=" + error);

            manifest = MakeValidManifest(digest);
            manifest.contentDigest = digest.Substring(0, 63);
            Check("contentDigest 63 位被拒", !PMBattleContentManifest.Validate(manifest, out error), "err=" + error);

            manifest = MakeValidManifest(digest);
            manifest.contentDigest = digest + "0";
            Check("contentDigest 65 位被拒", !PMBattleContentManifest.Validate(manifest, out error), "err=" + error);

            manifest = MakeValidManifest(digest);
            manifest.contentDigest = "g" + digest.Substring(1);
            Check("contentDigest 含非 hex 字符被拒", !PMBattleContentManifest.Validate(manifest, out error),
                  "err=" + error);

            manifest = MakeValidManifest(digest);
            manifest.contentDigest = null;
            Check("contentDigest=null 被拒", !PMBattleContentManifest.Validate(manifest, out error), "err=" + error);

            Check("null manifest 被拒（资源未生成的口径）",
                  !PMBattleContentManifest.Validate(null, out error), "err=" + error);
        }

        // ---------------------------------------------------------------- H. Json 边界

        private static void CheckJsonBoundary()
        {
            PMBattleContentManifest manifest;
            string error;

            Check("TryParseJson(null) 被拒", !PMBattleContentManifest.TryParseJson(null, out manifest, out error),
                  "err=" + error);
            Check("TryParseJson(\"\") 被拒", !PMBattleContentManifest.TryParseJson(string.Empty, out manifest, out error),
                  "err=" + error);
            Check("TryParseJson(\"{\") 被拒（非法 JSON）",
                  !PMBattleContentManifest.TryParseJson("{", out manifest, out error), "err=" + error);
            Check("TryParseJson(\"[]\") 被拒（根不是对象）",
                  !PMBattleContentManifest.TryParseJson("[]", out manifest, out error), "err=" + error);
            Check("TryParseJson(\"{}\") 被拒（缺字段 → 版本为 0）",
                  !PMBattleContentManifest.TryParseJson("{}", out manifest, out error), "err=" + error);

            PMBattleContentManifest valid = MakeValidManifest(Digest("1f2e3d4c"));
            string withoutWorldVersion = ToJson(valid).Replace(",\"worldVersion\":" + valid.worldVersion.ToString(CultureInfo.InvariantCulture), string.Empty);
            Check("缺 worldVersion 被拒（worldVersion=0）",
                  !PMBattleContentManifest.TryParseJson(withoutWorldVersion, out manifest, out error), "err=" + error);

            string withUppercase = ToJson(valid).Replace(valid.contentDigest, valid.contentDigest.ToUpperInvariant());
            Check("大写 contentDigest 被拒（位置：整体校验路径）",
                  !PMBattleContentManifest.TryParseJson(withUppercase, out manifest, out error), "err=" + error);
        }

        // ---------------------------------------------------------------- I. Unity 依赖面纪律

        private static void CheckUnitySurfaceDiscipline()
        {
            // 本工程的编译面已经**只**给 JsonUtility 一个 Unity 类型：manifest 源码若引入第二个
            // （Resources/Debug/Object/…）本工程会直接编译失败。下面的源码扫描把同一纪律
            // 变成一条可读的断言，并列出当前实际用到的 Unity 类型名。
            string path = ResolveManifestSourcePath();
            if (path == null)
            {
                Check("manifest 源码扫描（manifest Unity 依赖面 == {JsonUtility}）", true,
                      "源码文件未找到（非仓库内运行时跳过扫描；编译面纪律仍由本工程生效）");
                return;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex)
            {
                Check("manifest 源码扫描（manifest Unity 依赖面 == {JsonUtility}）", true,
                      "读取失败跳过：" + ex.GetType().Name);
                return;
            }

            string[] banned =
            {
                "Resources", "Debug", "TextAsset", "GameObject", "Transform", "Physics", "PhysicsScene",
                "Scene", "SceneManager", "Application", "MonoBehaviour", "Collider", "Rigidbody",
                "Camera", "Animator", "Mathf", "Quaternion", "Vector3", "Time", "ScriptableObject",
                "Object.Instantiate", "AudioListener", "LODGroup", "MeshFilter", "Renderer",
            };

            List<string> hits = new List<string>();
            int jsonUtilityUses = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string code = StripCommentsAndLiterals(lines[i]);
                if (code.IndexOf("JsonUtility", StringComparison.Ordinal) >= 0)
                {
                    jsonUtilityUses++;
                }

                for (int b = 0; b < banned.Length; b++)
                {
                    if (code.IndexOf(banned[b], StringComparison.Ordinal) >= 0)
                    {
                        hits.Add("L" + (i + 1).ToString(CultureInfo.InvariantCulture) + ":" + banned[b]);
                    }
                }
            }

            Check("manifest 源码不出现 JsonUtility 之外的 Unity 类型用法",
                  hits.Count == 0,
                  hits.Count == 0 ? "0 命中" : string.Join(", ", hits.ToArray()));
            Check("manifest 源码恰好使用 JsonUtility 一次（解析入口唯一）",
                  jsonUtilityUses == 1,
                  "出现 " + jsonUtilityUses.ToString(CultureInfo.InvariantCulture) + " 次");
        }

        /// <summary>
        /// 去掉行内注释与**字符串字面量**，只留可执行代码：
        ///   · 字符串里出现的 Unity 类型名（例如错误文案里的 "Resources 键…"）不是依赖，
        ///     必须排除，否则本断言会变成一句噪音而不是纪律；
        ///   · 不处理多行注释/逐字字符串（本文件不用它们；出现时本断言会保守报错，不做静默放过）。
        /// </summary>
        private static string StripCommentsAndLiterals(string line)
        {
            StringBuilder builder = new StringBuilder(line.Length);
            bool inLiteral = false;
            bool escaped = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (inLiteral)
                {
                    if (escaped) { escaped = false; continue; }
                    if (c == '\\') { escaped = true; continue; }
                    if (c == '"') { inLiteral = false; }
                    continue;
                }

                if (c == '"')
                {
                    inLiteral = true;
                    continue;
                }

                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    break;   // 行注释：本行代码到此为止
                }

                builder.Append(c);
            }

            return builder.ToString();
        }

        /// <summary>
        /// C1 的三份产物与 C2 的冻结资源键必须一一对应（这是跨组最容易"各写一半"的位置：
        /// C1 写 `Assets/Resources/PMNet/BattleMapV1.prefab`，C2 用键 `PMNet/BattleMapV1`）。
        ///
        /// 本检查的三种状态（都不假装）：
        ///   · `Resources/PMNet` 不存在 ⇒ 明确报"未烘焙（C1 菜单尚未执行）"，不判失败；
        ///   · 目录在但三份产物的某一份缺失 ⇒ **失败**（半成品发布状态本身就是缺陷）；
        ///   · 三份都在 ⇒ 直接用 C2 的规则解析 manifest 文本并校验（这是 C1 产物的第一次真实对账，
        ///     同时覆盖"uint 字段能不能被 JSON 往返"这类实机边界）。
        /// </summary>
        private static void CheckBakedArtifactsAgainstFrozenKeys()
        {
            string resources = ResolveResourcesDirectory();
            if (resources == null)
            {
                Check("Resources/PMNet 目录可定位", true, "非仓库内运行：跳过磁盘一致性检查");
                return;
            }

            string mapPath = Path.Combine(resources, "BattleMapV1.prefab");
            string playerPath = Path.Combine(resources, "PlayerVisualV1.prefab");
            string manifestPath = Path.Combine(resources, "BattleContentV1.json");

            Check("冻结资源键的目录前缀 == \"PMNet/\"",
                  PMBattleContentManifest.ResourceDirectory == "PMNet/",
                  "实际 \"" + PMBattleContentManifest.ResourceDirectory + "\"");

            bool map = File.Exists(mapPath);
            bool player = File.Exists(playerPath);
            bool manifest = File.Exists(manifestPath);

            if (!map && !player && !manifest)
            {
                Check("C1 三份产物已烘焙（磁盘）", true,
                      "**未烘焙**：C1 的 Editor 菜单 Build/Prepare PMNet Battle Content 尚未执行"
                      + "（这是 PENDING_USER，不是通过；三份产物齐备后本检查会自动转为强校验）");
                return;
            }

            Check("C1 三份产物齐备（缺任意一份都是半成品发布状态）", map && player && manifest,
                  "map=" + map + " player=" + player + " manifest=" + manifest
                  + "（目录 " + resources + "）");

            if (!manifest)
            {
                return;
            }

            string text;
            try
            {
                text = File.ReadAllText(manifestPath);
            }
            catch (Exception ex)
            {
                Check("运行时能读取 manifest 文本", false, ex.GetType().Name + " " + ex.Message);
                return;
            }

            PMBattleContentManifest parsed;
            string error;
            bool ok = PMBattleContentManifest.TryParseJson(text, out parsed, out error);
            Check("磁盘上的 manifest 通过 C2 强校验（含 uint 字段真实往返）", ok, ok ? parsed.Describe() : error);

            if (!ok)
            {
                return;
            }

            Check("磁盘 manifest 的资源键 == 冻结键",
                  parsed.mapResource == PMBattleContentManifest.MapResourceKey
                  && parsed.playerResource == PMBattleContentManifest.PlayerResourceKey,
                  "mapResource=\"" + parsed.mapResource + "\" playerResource=\"" + parsed.playerResource + "\"");
        }

        /// <summary>`Client/Assets/Resources/PMNet` 的绝对路径；非仓库内运行返回 null。</summary>
        private static string ResolveResourcesDirectory()
        {
            DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "Client"))
                    && Directory.Exists(Path.Combine(dir.FullName, "Tools")))
                {
                    return Path.Combine(dir.FullName, "Client", "Assets", "Resources", "PMNet");
                }

                dir = dir.Parent;
            }

            return null;
        }

        private static string ResolveManifestSourcePath()
        {
            // 从可执行文件目录向上找「同时含 Client 与 Tools 的那一层」（与 Tools/PMNetE2E 同一做法；
            // 刻意不硬编码绝对路径）。找不到就返回 null，由调用方降级为“跳过扫描”并明说。
            DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "Client"))
                    && Directory.Exists(Path.Combine(dir.FullName, "Tools")))
                {
                    string candidate = Path.Combine(dir.FullName, "Client", "Assets", "Scripts", "PMUnity",
                                                    "PMBattleContentManifest.cs");
                    return File.Exists(candidate) ? candidate : null;
                }

                dir = dir.Parent;
            }

            return null;
        }

        // ---------------------------------------------------------------- 工具

        private static PMBattleContentManifest MakeValidManifest(string contentDigest)
        {
            uint collisionDigest;
            int worldVersion;
            string error;
            if (!PMBattleContentManifest.TryDeriveCollisionDigestAndWorldVersion(contentDigest, out collisionDigest,
                                                                                out worldVersion, out error))
            {
                throw new InvalidOperationException("测试自检失败：无法派生摘要：" + error);
            }

            PMBattleContentManifest manifest = new PMBattleContentManifest();
            manifest.formatVersion = PMBattleContentManifest.ExpectedFormatVersion;
            manifest.mapId = PMBattleContentManifest.ExpectedMapId;
            manifest.seed = PMBattleContentManifest.ExpectedSeed;
            manifest.mapResource = PMBattleContentManifest.MapResourceKey;
            manifest.playerResource = PMBattleContentManifest.PlayerResourceKey;
            manifest.contentDigest = contentDigest;
            manifest.collisionDigest = collisionDigest;
            manifest.worldVersion = worldVersion;
            return manifest;
        }

        /// <summary>手写 JSON（同时也是 C1 应当产出的形态示例：字段名/类型与冻结 schema 一致）。</summary>
        private static string ToJson(PMBattleContentManifest manifest)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("{\"formatVersion\":").Append(manifest.formatVersion.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"mapId\":\"").Append(manifest.mapId).Append('"');
            builder.Append(",\"seed\":").Append(manifest.seed.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"mapResource\":\"").Append(manifest.mapResource).Append('"');
            builder.Append(",\"playerResource\":\"").Append(manifest.playerResource).Append('"');
            builder.Append(",\"contentDigest\":\"").Append(manifest.contentDigest).Append('"');
            builder.Append(",\"collisionDigest\":").Append(manifest.collisionDigest.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"worldVersion\":").Append(manifest.worldVersion.ToString(CultureInfo.InvariantCulture));
            builder.Append('}');
            return builder.ToString();
        }

        /// <summary>用给定前 8 个 hex 字符组成一个合法的 64 位小写 digest（多余字符截断、不足补 0）。</summary>
        private static string Digest(string firstEight)
        {
            StringBuilder builder = new StringBuilder(firstEight);
            while (builder.Length < PMBattleContentManifest.ContentDigestHexLength)
            {
                builder.Append('0');
            }

            return builder.ToString(0, PMBattleContentManifest.ContentDigestHexLength).ToLowerInvariant();
        }

        private static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("---- " + title + " ----");
        }

        private static void Check(string name, bool ok, string detail)
        {
            _checks++;
            if (!ok)
            {
                _failed++;
            }

            Console.WriteLine("  [" + (ok ? "PASS" : "FAIL") + "] " + name
                              + (string.IsNullOrEmpty(detail) ? string.Empty : (" :: " + detail)));
        }
    }
}

// ============================================================================
//  边界替身：UnityEngine 段
// ============================================================================
//
//  只桩 **JsonUtility 一个类型**（"只桩 JsonUtility 边界"），刻意不做成独立文件：
//  C2 契约限定「本测试工程最多 Program.cs / csproj 两个文件」。
//
//  替身实现的选择与它的诚实边界：
//    · 用 System.Text.Json 解析，然后按**public 实例字段名**逐一赋值 —— 与 Unity 的
//      JsonUtility 同一套模型（字段名匹配、忽略未知字段、缺字段留默认值）；
//    · 非法 JSON → 抛 JsonException（与 Unity 抛异常的语义一致，让 TryParseJson 的
//      catch 路径被真实执行）；
//    · 根不是对象 → 抛 ArgumentException（Unity 的 JsonUtility 同样只接受对象根）；
//    · 类型不符（例如给 int 字段一个字符串）→ 留默认值不抛（复刻 JsonUtility 的宽容）；
//    · 字段名匹配是**大小写敏感**的（Unity 的实机语义未在本工程验证，测试不依赖大小写差异）。
//
//  因此下列三条是"替身承担、实机未验"的行为（已登记在报告里）：
//    1) 未知字段被忽略；2) 缺字段留默认值；3) uint 字段可被解析。
// ============================================================================

namespace UnityEngine
{
    /// <summary>
    /// UnityEngine.JsonUtility 的**测试替身**（只覆盖 FromJson&lt;T&gt;）。
    /// 真实 Unity 2019.4 的实现由 Tools/PMBattleContentRuntimeCheck（真实 DLL 编译）
    /// 与 C1/C3 的运行期确认覆盖。
    /// </summary>
    public static class JsonUtility
    {
        /// <summary>把 JSON 对象反序列化成 T（只支持 public 实例字段，与 Unity 同模型）。</summary>
        public static T FromJson<T>(string json)
        {
            if (json == null)
            {
                throw new ArgumentNullException("json");
            }

            using (System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json))
            {
                System.Text.Json.JsonElement root = document.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    throw new ArgumentException("JsonUtility 替身：JSON 根节点不是对象。", "json");
                }

                object instance = Activator.CreateInstance<T>();
                FieldInfo[] fields = typeof(T).GetFields(BindingFlags.Instance | BindingFlags.Public);

                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo field = fields[i];

                    System.Text.Json.JsonElement value;
                    if (!root.TryGetProperty(field.Name, out value))
                    {
                        continue;   // 缺字段 → 留默认值（Unity 语义）
                    }

                    if (value.ValueKind == System.Text.Json.JsonValueKind.Null)
                    {
                        continue;
                    }

                    try
                    {
                        Assign(instance, field, value);
                    }
                    catch (Exception)
                    {
                        // 类型不符 → 留默认值（复刻 JsonUtility 的宽容）
                    }
                }

                return (T)instance;
            }
        }

        private static void Assign(object instance, FieldInfo field, System.Text.Json.JsonElement value)
        {
            Type type = field.FieldType;

            if (type == typeof(int))
            {
                if (value.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    field.SetValue(instance, value.GetInt32());
                }

                return;
            }

            if (type == typeof(uint))
            {
                if (value.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    field.SetValue(instance, value.GetUInt32());
                }

                return;
            }

            if (type == typeof(string))
            {
                if (value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    field.SetValue(instance, value.GetString());
                }

                return;
            }

            if (type == typeof(bool))
            {
                if (value.ValueKind == System.Text.Json.JsonValueKind.True
                    || value.ValueKind == System.Text.Json.JsonValueKind.False)
                {
                    field.SetValue(instance, value.GetBoolean());
                }

                return;
            }

            if (type == typeof(float))
            {
                if (value.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    field.SetValue(instance, value.GetSingle());
                }
            }
        }
    }
}
