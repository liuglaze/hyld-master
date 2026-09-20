// R4-C / C3 门禁：正式内容 manifest 的 schema / 派生 / 路径口径对照测试（纯 C#，不虚构 Unity 运行）。
//
// 目的（契约 Docs/plans/net-r4c-content-contract.md 的 C3 段）：
//   1) 证明 C3 依赖的两份实现**口径一致**
//        · Server/DS/PMDsBattleContentConfig.cs          —— net8（System.Text.Json），Lobby 侧；
//        · Client/Assets/Scripts/PMUnity/PMBattleContentManifest.cs —— C2 的 Unity 侧（JsonUtility）。
//      两者对**同一批正反 JSON** 必须给出同样的结论（接受/拒绝、摘要、世界版本）。
//   2) 固化 C3 独有规则：CollisionDigest = 0 禁止；保留诊断值 0x52334201 不得被正式 manifest 撞上。
//   3) 固化路径口径：默认从仓库根推导；HYLD_PMNET_CONTENT_MANIFEST 必须是绝对路径；
//      缺失/非法一律**明确失败**（不回退诊断内容），错误里带完整路径。
//
// 边界（诚实口径）：
//   · 本工程不编、也不加载任何 UnityEngine；C2 类唯一的 Unity 依赖 JsonUtility 由文件末尾的**替身**提供，
//     替身只做「JSON → public 字段」的解析边界（与 PMBattleContentManifestTest 同一做法），规则一律真实。
//   · 不启动 Unity、不加载 Resources、不建场景 —— 这里证明的是规则与路径，不是"实机加载成功"。
//
// 运行：dotnet Tools\PMBattleContentSessionTest\bin\Release\net8.0\PMBattleContentSessionTest.dll
//      （从仓库内运行时会额外覆盖"默认路径推导 + 产物存在性"；从仓库外运行时该节按事实跳过/走失败分支）

using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using PMNet.Control;
using PMNet.Unity;

internal static class Program
{
    private static int _checks;
    private static int _failed;
    private static int _skipped;

    // ── 冻结期望值（**独立**于被测实现，取自契约/C2 报告，避免同义反复） ──
    private const uint ExpectedReservedDigest = 0x52334201u;
    private const int ExpectedReservedWorldVersion = 1379090945;   // (int)(0x52334201 & 0x7fffffff)
    private const uint ExpectedValidDigest = 1279077919u;      // "1f2e3d4c…" 前 4 字节小端
    private const int ExpectedValidWorldVersion = 1279077919;
    private const int ExpectedSeed = 1380205313;

    private static readonly string DigestValid = "1f2e3d4c" + new string('0', 56);
    private static readonly string DigestOne = "01" + new string('0', 62);
    private static readonly string DigestZero = new string('0', 64);
    private static readonly string DigestReserved = "01423352" + new string('0', 56);   // 小端 = 0x52334201

    private static int Main()
    {
        Console.WriteLine("=== PMBattleContentSessionTest：C3 manifest 口径对照门禁（net8 配置 vs C2 Unity 类）===");

        SectionA();
        SectionB();
        SectionC();
        SectionD();
        SectionE();

        Console.WriteLine();
        Console.WriteLine("PMBattleContentSessionTest: 共 " + _checks.ToString(CultureInfo.InvariantCulture)
                          + " 项，失败 " + _failed.ToString(CultureInfo.InvariantCulture)
                          + " 项，跳过 " + _skipped.ToString(CultureInfo.InvariantCulture) + " 项。");
        return _failed == 0 ? 0 : 1;
    }

    // =================================================================================
    //  A：冻结常量与摘要派生（与 C2 报告 §5 的独立期望值对齐）
    // =================================================================================
    private static void SectionA()
    {
        Section("A 冻结常量与摘要派生");

        CheckEq(ExpectedReservedDigest, PMDsBattleContentConfig.ReservedDiagnosticCollisionDigest,
                "A1 保留诊断摘要 = 0x52334201（与 PMR3Runtime.CollisionDigest 同值，见 §E 源码交叉校验）");
        CheckEq(1, PMDsBattleContentConfig.ExpectedFormatVersion, "A2 ExpectedFormatVersion");
        CheckEq(ExpectedSeed, PMDsBattleContentConfig.ExpectedSeed, "A3 ExpectedSeed = 0x52444301");
        CheckEq("hyld-map2-v1", PMDsBattleContentConfig.ExpectedMapId, "A4 ExpectedMapId");
        CheckEq("PMNet/BattleMapV1", PMDsBattleContentConfig.ExpectedMapResource, "A5 ExpectedMapResource");
        CheckEq("PMNet/PlayerVisualV1", PMDsBattleContentConfig.ExpectedPlayerResource, "A6 ExpectedPlayerResource");
        CheckEq(64, PMDsBattleContentConfig.ContentDigestHexLength, "A7 ContentDigestHexLength");

        uint derived;
        string error;

        Check(PMDsBattleContentConfig.TryDeriveCollisionDigest(DigestValid, out derived, out error),
              "A8 合法 contentDigest 可派生");
        CheckEq(ExpectedValidDigest, derived, "A9 派生 = 前 4 字节**小端** uint");

        CheckEq(ExpectedValidWorldVersion, PMDsBattleContentConfig.DeriveWorldVersion(derived), "A10 worldVersion 派生");

        Check(PMDsBattleContentConfig.TryDeriveCollisionDigest(DigestOne, out derived, out error),
              "A11 '01…' 可派生");
        CheckEq(1u, derived, "A12 '01…' → 1（小端；若按大端会得到 0x01000000）");

        Check(PMDsBattleContentConfig.TryDeriveCollisionDigest(DigestZero, out derived, out error),
              "A13 全 0 摘要可派生");
        CheckEq(1u, derived, "A14 派生的 0 → 1（不许出现 0 摘要）");

        CheckEq(1, PMDsBattleContentConfig.DeriveWorldVersion(0u), "A15 worldVersion(0) → 1");
        CheckEq(1, PMDsBattleContentConfig.DeriveWorldVersion(0x80000000u), "A16 worldVersion(0x80000000) → 1（掩码后为 0）");
        CheckEq(0x7fffffff, PMDsBattleContentConfig.DeriveWorldVersion(0xffffffffu), "A17 worldVersion(0xffffffff)");

        Check(PMDsBattleContentConfig.IsLowercaseSha256Hex(DigestValid), "A18 小写 hex64 接受");
        Check(!PMDsBattleContentConfig.IsLowercaseSha256Hex(DigestValid.ToUpperInvariant()), "A19 大写被拒");
        Check(!PMDsBattleContentConfig.IsLowercaseSha256Hex(DigestValid.Substring(0, 63)), "A20 63 位被拒");
        Check(!PMDsBattleContentConfig.IsLowercaseSha256Hex(DigestValid + "0"), "A21 65 位被拒");
        Check(!PMDsBattleContentConfig.IsLowercaseSha256Hex("zz" + new string('0', 62)), "A22 非 hex 被拒");
        Check(!PMDsBattleContentConfig.IsLowercaseSha256Hex(null), "A23 null 被拒");
        Check(!PMDsBattleContentConfig.IsLowercaseSha256Hex(string.Empty), "A24 空串被拒");
        Check(!PMDsBattleContentConfig.TryDeriveCollisionDigest("XYZ", out derived, out error), "A25 非法摘要派生失败");
    }

    // =================================================================================
    //  B：有效 manifest（net8 侧）
    // =================================================================================
    private static void SectionB()
    {
        Section("B 有效 manifest（net8 / Lobby 侧）");

        string json = ValidManifest();
        PMDsBattleContentInfo info;
        string error;
        bool ok = PMDsBattleContentConfig.TryParse(json, "<test>", out info, out error);

        Check(ok, "B1 有效 manifest 被接受" + (ok ? "" : "（" + Shorten(error) + "）"));
        if (!ok) { return; }

        CheckEq(DigestValid, info.ContentDigest, "B2 contentDigest 回读");
        CheckEq(ExpectedValidDigest, info.CollisionDigest, "B3 collisionDigest 回读");
        CheckEq(ExpectedValidWorldVersion, info.WorldVersion, "B4 worldVersion 回读");
        CheckEq("PMNet/BattleMapV1", info.MapResource, "B5 mapResource 回读");
        CheckEq("PMNet/PlayerVisualV1", info.PlayerResource, "B6 playerResource 回读");
        CheckEq("<test>", info.ManifestPath, "B7 路径回读");

        // 未知字段被忽略（与 JsonUtility 口径一致）
        string withExtra = json.Substring(0, json.Length - 1) + ",\"futureField\":42}";
        PMDsBattleContentInfo extra;
        string extraError;
        Check(PMDsBattleContentConfig.TryParse(withExtra, "<test>", out extra, out extraError),
              "B8 未知字段被忽略（与 C2/JsonUtility 同口径）");
    }

    // =================================================================================
    //  C：与 C2（Unity 侧）的对照门禁
    // =================================================================================
    private static void SectionC()
    {
        Section("C 对照门禁：同一 JSON，两侧同判");

        // C1 有效：两侧都通过且摘要/版本一致
        string valid = ValidManifest();
        PMDsBattleContentInfo netInfo;
        string netError;
        PMBattleContentManifest unityInfo;
        string unityError;

        bool netOk = PMDsBattleContentConfig.TryParse(valid, "<test>", out netInfo, out netError);
        bool unityOk = PMBattleContentManifest.TryParseJson(valid, out unityInfo, out unityError);
        Check(netOk, "C1 net8 接受有效 manifest" + (netOk ? "" : "（" + Shorten(netError) + "）"));
        Check(unityOk, "C2 C2/Unity 接受有效 manifest" + (unityOk ? "" : "（" + Shorten(unityError) + "）"));
        if (netOk && unityOk)
        {
            CheckEq(unityInfo.collisionDigest, netInfo.CollisionDigest, "C3 两侧 collisionDigest 一致");
            CheckEq(unityInfo.worldVersion, netInfo.WorldVersion, "C4 两侧 worldVersion 一致");
            CheckEq(unityInfo.contentDigest, netInfo.ContentDigest, "C5 两侧 contentDigest 一致");
        }

        // C6 零 digest：两侧都拒
        CheckBothReject(ManifestJson(DigestValid, "0", "1", "1", "hyld-map2-v1", "1380205313",
                                     "PMNet/BattleMapV1", "PMNet/PlayerVisualV1"),
                        "C6 collisionDigest=0");

        // C7 保留诊断值：net8 必须拒（C3 规则）；C2 不含该规则（其口径是"与派生一致即可"）——
        //    这正是"正式内容不得撞保留值"由 C3 层守护的证据，刻意分两侧断言。
        string reserved = ManifestJson(DigestReserved, ExpectedReservedDigest.ToString(CultureInfo.InvariantCulture),
                                       ExpectedReservedWorldVersion.ToString(CultureInfo.InvariantCulture),
                                       "1", "hyld-map2-v1", "1380205313",
                                       "PMNet/BattleMapV1", "PMNet/PlayerVisualV1");
        PMDsBattleContentInfo reservedNet;
        string reservedNetError;
        PMBattleContentManifest reservedUnity;
        string reservedUnityError;
        Check(!PMDsBattleContentConfig.TryParse(reserved, "<test>", out reservedNet, out reservedNetError),
              "C7 net8 拒绝撞保留值 0x52334201（" + Shorten(reservedNetError) + "）");
        Check(PMBattleContentManifest.TryParseJson(reserved, out reservedUnity, out reservedUnityError),
              "C8 C2/Unity 接受该 JSON（它只校验派生一致；保留值规则属 C3）");

        // C9 手写常量（与派生不一致）：两侧都拒（"旧 digest 不一致"）
        CheckBothReject(ManifestJson(DigestValid, "12345", ExpectedValidWorldVersion.ToString(CultureInfo.InvariantCulture),
                                     "1", "hyld-map2-v1", "1380205313", "PMNet/BattleMapV1", "PMNet/PlayerVisualV1"),
                        "C9 collisionDigest 与 contentDigest 派生不一致（旧/手写 digest）");

        // C10 worldVersion 与派生不一致：两侧都拒
        CheckBothReject(ManifestJson(DigestValid, ExpectedValidDigest.ToString(CultureInfo.InvariantCulture), "7",
                                     "1", "hyld-map2-v1", "1380205313", "PMNet/BattleMapV1", "PMNet/PlayerVisualV1"),
                        "C10 worldVersion 与派生不一致");

        // C11 formatVersion 错：两侧都拒
        CheckBothReject(ManifestJson(DigestValid, ExpectedValidDigest.ToString(CultureInfo.InvariantCulture),
                                     ExpectedValidWorldVersion.ToString(CultureInfo.InvariantCulture),
                                     "2", "hyld-map2-v1", "1380205313", "PMNet/BattleMapV1", "PMNet/PlayerVisualV1"),
                        "C11 formatVersion=2");

        // C12 mapId 错：两侧都拒
        CheckBothReject(ManifestJson(DigestValid, ExpectedValidDigest.ToString(CultureInfo.InvariantCulture),
                                     ExpectedValidWorldVersion.ToString(CultureInfo.InvariantCulture),
                                     "1", "map4", "1380205313", "PMNet/BattleMapV1", "PMNet/PlayerVisualV1"),
                        "C12 mapId 非冻结值");

        // C13 seed 错（0 / 2）：两侧都拒
        CheckBothReject(ManifestJson(DigestValid, ExpectedValidDigest.ToString(CultureInfo.InvariantCulture),
                                     ExpectedValidWorldVersion.ToString(CultureInfo.InvariantCulture),
                                     "1", "hyld-map2-v1", "0", "PMNet/BattleMapV1", "PMNet/PlayerVisualV1"),
                        "C13 seed=0");
        CheckBothReject(ManifestJson(DigestValid, ExpectedValidDigest.ToString(CultureInfo.InvariantCulture),
                                     ExpectedValidWorldVersion.ToString(CultureInfo.InvariantCulture),
                                     "1", "hyld-map2-v1", "2", "PMNet/BattleMapV1", "PMNet/PlayerVisualV1"),
                        "C14 seed=2");

        // C15 资源键互换：两侧都拒
        CheckBothReject(ManifestJson(DigestValid, ExpectedValidDigest.ToString(CultureInfo.InvariantCulture),
                                     ExpectedValidWorldVersion.ToString(CultureInfo.InvariantCulture),
                                     "1", "hyld-map2-v1", "1380205313", "PMNet/PlayerVisualV1", "PMNet/BattleMapV1"),
                        "C15 资源键互换");

        // C16 contentDigest 大写：两侧都拒
        CheckBothReject(ManifestJson(DigestValid.ToUpperInvariant(), ExpectedValidDigest.ToString(CultureInfo.InvariantCulture),
                                     ExpectedValidWorldVersion.ToString(CultureInfo.InvariantCulture),
                                     "1", "hyld-map2-v1", "1380205313", "PMNet/BattleMapV1", "PMNet/PlayerVisualV1"),
                        "C16 contentDigest 大写");

        // C17 contentDigest 长度错：两侧都拒
        CheckBothReject(ManifestJson(DigestValid.Substring(0, 63), ExpectedValidDigest.ToString(CultureInfo.InvariantCulture),
                                     ExpectedValidWorldVersion.ToString(CultureInfo.InvariantCulture),
                                     "1", "hyld-map2-v1", "1380205313", "PMNet/BattleMapV1", "PMNet/PlayerVisualV1"),
                        "C17 contentDigest 63 位");

        // C18 缺字段（worldVersion 缺失）：两侧都拒
        CheckBothReject(ManifestJson(DigestValid, ExpectedValidDigest.ToString(CultureInfo.InvariantCulture), null,
                                     "1", "hyld-map2-v1", "1380205313", "PMNet/BattleMapV1", "PMNet/PlayerVisualV1"),
                        "C18 缺 worldVersion 字段");

        // C19 非法 schema/JSON：两侧都拒
        CheckBothReject("[]", "C19 根为数组");
        CheckBothReject("{}", "C20 空对象");
        CheckBothReject("{", "C21 非法 JSON");
        CheckBothReject(string.Empty, "C22 空串");
    }

    // =================================================================================
    //  D：路径口径（默认从仓库根推导）
    // =================================================================================
    private static void SectionD()
    {
        Section("D 路径口径（默认推导 / 环境变量优先级）");

        Environment.SetEnvironmentVariable(PMDsBattleContentConfig.ManifestEnvironmentVariable, null);

        string root = FindRepositoryRoot();
        string path;
        string error;

        if (root == null)
        {
            bool ok = PMDsBattleContentConfig.TryResolveManifestPath(out path, out error);
            Check(!ok && Contains(error, "无法从可执行文件目录向上定位仓库根"),
                  "D1 未设 env 且定位不到仓库根：显式失败并说明原因（" + Shorten(error) + "）");
            Note("（本次运行不在仓库树内：D2/D3 按事实走失败分支；在仓库内运行时为成功分支）");
            return;
        }

        bool resolved = PMDsBattleContentConfig.TryResolveManifestPath(out path, out error);
        Check(resolved, "D2 未设 env：从仓库根推导默认路径" + (resolved ? "" : "（" + Shorten(error) + "）"));
        if (!resolved) { return; }

        string expected = Path.GetFullPath(Path.Combine(root, PMDsBattleContentConfig.DefaultRelativeManifestPath));
        CheckEq(expected, Path.GetFullPath(path), "D3 默认路径 = 仓库根 + " + PMDsBattleContentConfig.DefaultRelativeManifestPath);

        if (File.Exists(expected))
        {
            PMDsBattleContentInfo info;
            string loadError;
            bool loaded = PMDsBattleContentConfig.TryLoad(out info, out loadError);
            Check(loaded, "D4 产物已存在：TryLoad 成功" + (loaded ? "" : "（" + Shorten(loadError) + "）"));
            if (loaded)
            {
                CheckEq(ExpectedValidDigest, info.CollisionDigest, "D5 产物 collisionDigest（真实烘焙值）");
            }
        }
        else
        {
            PMDsBattleContentInfo info;
            string loadError;
            bool loaded = PMDsBattleContentConfig.TryLoad(out info, out loadError);
            Check(!loaded, "D4 产物缺失：TryLoad 明确失败（不默认诊断内容）");
            Check(Contains(loadError, "不存在") && Contains(loadError, expected),
                  "D5 失败原因含完整路径（" + Shorten(loadError) + "）");
            Check(Contains(loadError, "不回退诊断内容"), "D6 失败原因声明不回退诊断内容");
        }
    }

    // =================================================================================
    //  E：显式部署路径（HYLD_PMNET_CONTENT_MANIFEST）
    // =================================================================================
    private static void SectionE()
    {
        Section("E 显式部署路径（env 覆盖）");

        string tempDir = Path.Combine(Path.GetTempPath(), "pmr4c-session-test");
        Directory.CreateDirectory(tempDir);
        string tempManifest = Path.Combine(tempDir, "BattleContentV1.json");

        try
        {
            File.WriteAllText(tempManifest, ValidManifest(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Skip("E1 无法写临时 manifest（" + ex.GetType().Name + "）");
            return;
        }

        // E1~E7：绝对路径覆盖 → 解析 + 装载成功
        Environment.SetEnvironmentVariable(PMDsBattleContentConfig.ManifestEnvironmentVariable, tempManifest);
        string resolved;
        string resolveError;
        Check(PMDsBattleContentConfig.TryResolveManifestPath(out resolved, out resolveError),
              "E1 绝对路径覆盖被接受" + (resolveError == null ? "" : "（" + Shorten(resolveError) + "）"));
        CheckEq(tempManifest, resolved, "E2 覆盖路径原样返回");

        PMDsBattleContentInfo info;
        string loadError;
        bool loaded = PMDsBattleContentConfig.TryLoad(out info, out loadError);
        Check(loaded, "E3 TryLoad 从部署路径读到有效 manifest" + (loaded ? "" : "（" + Shorten(loadError) + "）"));
        if (loaded)
        {
            CheckEq(DigestValid, info.ContentDigest, "E4 contentDigest");
            CheckEq(ExpectedValidDigest, info.CollisionDigest, "E5 collisionDigest");
            CheckEq(ExpectedValidWorldVersion, info.WorldVersion, "E6 worldVersion");
            CheckEq(tempManifest, info.ManifestPath, "E7 路径回读");
        }

        // E8：相对路径被拒
        Environment.SetEnvironmentVariable(PMDsBattleContentConfig.ManifestEnvironmentVariable, "relative/BattleContentV1.json");
        Check(!PMDsBattleContentConfig.TryResolveManifestPath(out resolved, out resolveError)
              && Contains(resolveError, "必须是绝对路径"),
              "E8 相对路径被拒绝（" + Shorten(resolveError) + "）");

        // E9：绝对路径但文件不存在 → TryLoad 明确失败且不回退
        string missing = Path.Combine(tempDir, "NotThere.json");
        Environment.SetEnvironmentVariable(PMDsBattleContentConfig.ManifestEnvironmentVariable, missing);
        Check(!PMDsBattleContentConfig.TryLoad(out info, out loadError),
              "E9 文件不存在：TryLoad 失败");
        Check(Contains(loadError, missing) && Contains(loadError, "不回退诊断内容"),
              "E10 失败原因含完整部署路径与「不回退诊断内容」声明（" + Shorten(loadError) + "）");

        // E11：内容非法（残留的旧 digest）→ TryLoad 失败
        string stale = Path.Combine(tempDir, "StaleContent.json");
        File.WriteAllText(stale, ManifestJson(DigestValid, "12345", "12345", "1", "hyld-map2-v1", "1380205313",
                                              "PMNet/BattleMapV1", "PMNet/PlayerVisualV1"), new UTF8Encoding(false));
        Environment.SetEnvironmentVariable(PMDsBattleContentConfig.ManifestEnvironmentVariable, stale);
        Check(!PMDsBattleContentConfig.TryLoad(out info, out loadError),
              "E11 旧/手写 digest 的 manifest 被拒（" + Shorten(loadError) + "）");

        Environment.SetEnvironmentVariable(PMDsBattleContentConfig.ManifestEnvironmentVariable, null);
    }

    // =================================================================================
    //  工具
    // =================================================================================

    private static string ValidManifest()
    {
        return ManifestJson(DigestValid, ExpectedValidDigest.ToString(CultureInfo.InvariantCulture),
                            ExpectedValidWorldVersion.ToString(CultureInfo.InvariantCulture),
                            "1", "hyld-map2-v1", "1380205313", "PMNet/BattleMapV1", "PMNet/PlayerVisualV1");
    }

    /// <summary>构造 manifest JSON；任一参数为 null 即省略该字段（用于缺字段负例）。</summary>
    private static string ManifestJson(string contentDigest, string collisionDigest, string worldVersion,
                                       string formatVersion, string mapId, string seed,
                                       string mapResource, string playerResource)
    {
        StringBuilder builder = new StringBuilder();
        builder.Append('{');
        bool first = true;

        AppendField(builder, ref first, "formatVersion", formatVersion, false);
        AppendField(builder, ref first, "mapId", mapId, true);
        AppendField(builder, ref first, "seed", seed, false);
        AppendField(builder, ref first, "mapResource", mapResource, true);
        AppendField(builder, ref first, "playerResource", playerResource, true);
        AppendField(builder, ref first, "contentDigest", contentDigest, true);
        AppendField(builder, ref first, "collisionDigest", collisionDigest, false);
        AppendField(builder, ref first, "worldVersion", worldVersion, false);

        builder.Append('}');
        return builder.ToString();
    }

    private static void AppendField(StringBuilder builder, ref bool first, string name, string value, bool quoted)
    {
        if (value == null) { return; }

        if (!first) { builder.Append(','); }
        first = false;

        builder.Append('"').Append(name).Append("\":");
        if (quoted) { builder.Append('"').Append(value).Append('"'); }
        else { builder.Append(value); }
    }

    /// <summary>同一份 JSON：net8 侧与 Unity 侧**都必须拒绝**。</summary>
    private static void CheckBothReject(string json, string what)
    {
        PMDsBattleContentInfo info;
        string netError;
        bool netOk = PMDsBattleContentConfig.TryParse(json, "<test>", out info, out netError);

        PMBattleContentManifest manifest;
        string unityError;
        bool unityOk = PMBattleContentManifest.TryParseJson(json, out manifest, out unityError);

        Check(!netOk, what + "：net8 侧拒绝（" + Shorten(netError) + "）");
        Check(!unityOk, what + "：C2/Unity 侧拒绝（" + Shorten(unityError) + "）");
    }

    private static string FindRepositoryRoot()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
        {
            if (Directory.Exists(Path.Combine(dir, "Server")) && Directory.Exists(Path.Combine(dir, "Client")))
            {
                return dir;
            }

            DirectoryInfo parent = Directory.GetParent(dir);
            dir = parent == null ? null : parent.FullName;
        }

        return null;
    }

    private static bool Contains(string text, string needle)
    {
        return text != null && text.IndexOf(needle, StringComparison.Ordinal) >= 0;
    }

    private static string Shorten(string text)
    {
        if (string.IsNullOrEmpty(text)) { return "<null>"; }

        string oneLine = text.Replace("\r", " ").Replace("\n", " ");
        return oneLine.Length <= 120 ? oneLine : oneLine.Substring(0, 120) + "…";
    }

    private static void Section(string name)
    {
        Console.WriteLine();
        Console.WriteLine("[" + name + "]");
    }

    private static void Check(bool condition, string what)
    {
        _checks++;
        if (condition)
        {
            Console.WriteLine("  OK   " + what);
        }
        else
        {
            _failed++;
            Console.WriteLine("  FAIL " + what);
        }
    }

    private static void CheckEq(object expected, object actual, string what)
    {
        bool equal = expected == null ? actual == null : expected.Equals(actual);
        Check(equal, what + "（期望 " + Format(expected) + "，实际 " + Format(actual) + "）");
    }

    private static string Format(object value)
    {
        return value == null ? "<null>" : value.ToString();
    }

    private static void Note(string text)
    {
        Console.WriteLine("  --   " + text);
    }

    private static void Skip(string what)
    {
        _skipped++;
        Console.WriteLine("  SKIP " + what);
    }
}

// ============================================================================================
//  测试替身：**只**替 UnityEngine.JsonUtility 的解析边界
//
//  为什么可以替、替到什么程度：
//    · C2 的 PMBattleContentManifest 只用了一个 Unity API（JsonUtility），源码扫描门禁已把它钉死；
//    · 这里用 System.Text.Json + 反射按 public 字段名赋值，只模拟「JSON → 对象」这一层的
//      真实语义：未知字段忽略、缺字段留默认、非法 JSON 抛异常、根必须是对象；
//    · **规则（schema/固定值/白名单/派生）一行都不替** —— 它们跑的是 C2 的真实源码。
// ============================================================================================
namespace UnityEngine
{
    internal static class JsonUtility
    {
        public static T FromJson<T>(string json)
        {
            if (json == null) { throw new ArgumentNullException("json"); }

            using (JsonDocument document = JsonDocument.Parse(json))
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new ArgumentException("JsonUtility：根必须是 JSON 对象（模拟 Unity 行为）");
                }

                object instance = Activator.CreateInstance(typeof(T));
                FieldInfo[] fields = typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance);

                foreach (JsonProperty property in root.EnumerateObject())
                {
                    for (int i = 0; i < fields.Length; i++)
                    {
                        FieldInfo field = fields[i];
                        if (!string.Equals(field.Name, property.Name, StringComparison.Ordinal)) { continue; }

                        field.SetValue(instance, ReadValue(property.Value, field.FieldType));
                        break;
                    }
                }

                return (T)instance;
            }
        }

        public static string ToJson(object obj)
        {
            return ToJson(obj, false);
        }

        public static string ToJson(object obj, bool prettyPrint)
        {
            if (obj == null) { return "{}"; }

            StringBuilder builder = new StringBuilder();
            builder.Append('{');
            FieldInfo[] fields = obj.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
            bool first = true;

            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                object value = field.GetValue(obj);
                if (!first) { builder.Append(','); }
                first = false;

                builder.Append('"').Append(field.Name).Append("\":");
                if (field.FieldType == typeof(string))
                {
                    builder.Append('"').Append(value == null ? string.Empty : value.ToString()).Append('"');
                }
                else
                {
                    builder.Append(System.Convert.ToString(value, CultureInfo.InvariantCulture));
                }
            }

            builder.Append('}');
            return builder.ToString();
        }

        private static object ReadValue(JsonElement element, Type type)
        {
            if (type == typeof(string))
            {
                return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
            }

            if (type == typeof(int))
            {
                long raw;
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out raw)) { return (int)raw; }
                return 0;
            }

            if (type == typeof(uint))
            {
                long raw;
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out raw) && raw >= 0L && raw <= (long)uint.MaxValue)
                {
                    return (uint)raw;
                }

                return 0u;
            }

            if (type == typeof(bool))
            {
                return element.ValueKind == JsonValueKind.True;
            }

            return null;
        }
    }
}
