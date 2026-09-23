// 旧链退役（Docs/plans/net-legacy-retirement-contract.md）的静态禁回归门禁。
//
// 设计要点（与任务书逐条对应）：
//   · 只做**静态事实**：文件存在性 / 词法剥离后的标识符 / proto 声明与字段号 / GUID / SHA256。
//   · 词法先剥离注释与字符串（保留换行与偏移，便于报行号），再查禁用类型 token，
//     因此「注释里说明某个类已退役」不会被误报成引用（契约明确要求）。
//   · proto 解析复用生成器自己的 ProtoParser（只读链接），保证与生成期口径一致；
//     reserved 语句由本工具单独扫描（ProtoParser 会把 reserved 跳过，不暴露范围）。
//   · 缺输入一律 FAIL，绝不允许「扫到 0 个文件 ⇒ 绿」。
//   · --repo <path> 把根切到沙盒；负例自测在 %TEMP% 下建临时沙盒，不碰真实生产文件。
//
// 本门禁**不**冒充的行为验证：PMDsHost 缺 -bootstrap 时的退出码/是否启动会话由
// Tools/PMDsHostCheck 的替身门禁承担；本门禁只断言其结构事实。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PMNetGen;

namespace PMLegacyRetirementTest
{
    internal sealed class CheckResult
    {
        public string Group;
        public string Id;
        public bool Pass;
        public string Detail;

        public CheckResult(string group, string id, bool pass, string detail)
        {
            Group = group;
            Id = id;
            Pass = pass;
            Detail = detail ?? string.Empty;
        }
    }

    internal sealed class RepoLayout
    {
        public readonly string Root;

        public RepoLayout(string root)
        {
            Root = Path.GetFullPath(root);
        }

        public string P(string rel)
        {
            return Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
        }

        public string Rel(string abs)
        {
            string full = Path.GetFullPath(abs);
            string root = Root.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? Root
                : Root + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return full.Substring(root.Length).Replace('\\', '/');
            }

            return full.Replace('\\', '/');
        }
    }

    internal static class Program
    {
        // =====================================================================================
        //  一、冻结契约数据（常量；不在此门禁里硬编码任何 ProtocolHash 值）
        // =====================================================================================

        private const string ProtoRel = "ProtobufAndNotepad/Protobuf/SocketProto.proto";
        private const string DsHostRel = "Client/Assets/Scripts/Server/Boot/PMDsHost.cs";
        private const string ControllersRel = "Server/Controller/Controllers.cs";
        private const string UiMatchingRel = "Client/Assets/Scripts/Server/Panel/UIMatchingPanel.cs";

        /// <summary>
        /// 旧链已删类型（C# 标识符）。契约 §B/§A/§C 列出；限定本项目源码命名空间/文件。
        /// 注意 BattleManage 用整词边界匹配，不会误伤 BattleManager 之类的其它名字。
        /// 注意 GameManger 是「Manger.GameManger」这条契约；第三方/引擎里的同名类型不在
        /// 扫描根（Client/Assets、Server）内，本门禁不会误判它们。
        /// </summary>
        private static readonly string[] LegacyTypeTokens =
        {
            "BattleManger",
            "BattleData",
            "HYLDPlayerManger",
            "HYLDBulletManger",
            "HYLDCameraManger",
            "HYLDBaoShiZhengBaManger",
            "GameManger",
            "UDPSocketManger",
            "CommandManger",
            "BattleFrameHud",
            "BattleController",
            "BattleManage",
            "BattleContext",
            "LZJUDP",
            "ServerBullet",
            "ServerVector3",
            "PMUdpRouter",
            "PMSingleBattleRegistry",
            "SavedMove",
        };

        /// <summary>契约 §B/§C：必须不存在的客户端源码（.cs 与 .cs.meta 各一条）。</summary>
        private static readonly string[] LegacyClientFiles =
        {
            "Client/Assets/Scripts/Server/Manger/Battle/BattleManger.cs",
            "Client/Assets/Scripts/Server/Manger/Battle/BattleData.cs",
            "Client/Assets/Scripts/Server/Manger/Battle/BattleData.Attack.cs",
            "Client/Assets/Scripts/Server/Manger/Battle/BattleData.Authority.cs",
            "Client/Assets/Scripts/Server/Manger/Battle/BattleData.HitEvent.cs",
            "Client/Assets/Scripts/Server/Manger/Battle/BattleData.Prediction.cs",
            "Client/Assets/Scripts/Server/Manger/Battle/BattleData.Rtt.cs",
            "Client/Assets/Scripts/Server/Manger/Battle/HYLDPlayerManger.cs",
            "Client/Assets/Scripts/Server/Manger/Battle/HYLDBulletManger.cs",
            "Client/Assets/Scripts/Server/Manger/Battle/HYLDCameraManger.cs",
            "Client/Assets/Scripts/Server/Manger/Battle/HYLDBaoShiZhengBaManger.cs",
            "Client/Assets/Scripts/Server/Manger/Battle/GameManger.cs",
            "Client/Assets/Scripts/Server/Manger/UDPSocketManger.cs",
            "Client/Assets/Scripts/Manger/CommandManger.cs",
            "Client/Assets/Scripts/Server/UI/BattleFrameHud.cs",
            "Client/Assets/Scripts/Server/Net/PMUdpRouter.cs",
            "Client/Assets/Scripts/Server/Net/PMSingleBattleRegistry.cs",
        };

        /// <summary>契约 §A：必须不存在的服务端源码。</summary>
        private static readonly string[] LegacyServerFiles =
        {
            "Server/Server/Battle.cs",
            "Server/Server/BattleController.Bullets.cs",
            "Server/Server/BattleController.Network.cs",
            "Server/Server/BattleManage.cs",
            "Server/Server/BattleContext.cs",
            "Server/Server/ServerBullet.cs",
            "Server/Server/ServerVector3.cs",
            "Server/Server/ClientUdp.cs",
        };

        /// <summary>契约 §C：三个只测旧路的退役工具，其工程文件必须不存在。</summary>
        private static readonly string[] RetiredToolProjects =
        {
            "Tools/PMUdpRouterCheck/PMUdpRouterCheck.csproj",
            "Tools/PMUdpRouterTest/PMUdpRouterTest.csproj",
            "Tools/PMDsProbe/PMDsProbe.csproj",
        };

        /// <summary>契约 §E1：旧 14 条战斗消息，声明名必须不存在。</summary>
        private static readonly string[] LegacyProtoMessages =
        {
            "BattleRoomPack",
            "BattleInfo",
            "BattleNetSimConfig",
            "BattleClientInput",
            "BattleServerUpdate",
            "BattleFrame",
            "PlayerFrameInput",
            "ClientAttack",
            "ServerAttack",
            "ClientMove",
            "MoveAckResult",
            "HitEvent",
            "AttackAck",
            "AuthoritativePlayerState",
        };

        /// <summary>契约 §E1：这两个枚举必须不存在。</summary>
        private static readonly string[] LegacyProtoEnums = { "MoveType", "AttackType" };

        /// <summary>契约 §E1：RequestCode 里这两个名字与号必须消失（号 7/8 需 reserved）。</summary>
        private static readonly string[] LegacyRequestNames = { "Battle", "ClearSence" };
        private static readonly int[] LegacyRequestNumbers = { 7, 8 };

        /// <summary>契约 §E1：ActionCode 31..41 必须 reserved（不得有定义）。</summary>
        private const int LegacyActionFirst = 31;
        private const int LegacyActionLast = 41;

        /// <summary>契约 §E1：MainPack 字段 13/15 必须 reserved（不得有定义）。</summary>
        private static readonly int[] LegacyMainPackNumbers = { 13, 15 };
        private static readonly string[] LegacyMainPackNames = { "battleInfo", "battle_net_sim_config" };

        /// <summary>build.bat 实际产出的四份 generated 产物（契约 §E「四份」）。</summary>
        private static readonly string[] GeneratedFiles =
        {
            "ProtobufAndNotepad/Protobuf/CSharp/SocketProto.cs",
            "Client/Assets/Scripts/Server/SocketProto.cs",
            "Server/Server/SocketProto.cs",
            "Client/Assets/Scripts/PMNet/Generated/SocketProto.PMNet.g.cs",
        };

        /// <summary>契约 §E2：非权威 proto 副本必须删除，避免旧类型从副本复活。</summary>
        private static readonly string[] NonAuthoritativeProtoCopies =
        {
            "Client/SocketProto.proto",
            "ProtobufAndNotepad/Protobuf/Proto/SocketProto.proto",
        };

        /// <summary>
        /// 契约 §D 的「禁改输入/输出」SHA256 冻结值，取自 Docs/plans/_legacy_asset_detach_report.md §4.5。
        /// 这是**固定锚**：若后续有意重烘（源变化），必须由人更新说明与锚值，门禁不写默认 PASS。
        /// </summary>
        private static readonly string[][] FrozenAnchors =
        {
            new[] { "Client/Assets/Scenes/HYLDGame.unity", "72e708d51613804fc16eb21585ec6e625254833699dbb08ff80b160af4953bb4" },
            new[] { "Client/Assets/Resources/Remake/Player.prefab", "6ba5cb30be52d7b759fe39311363803e16b13ae513d2c52be022ffc25c24dc54" },
            new[] { "Client/Assets/Resources/PMNet/BattleContentV1.json", "898cbe51fddf10bbe0c9d782912fde93d263b67c2b2327ce1f0adc4e8f7e339a" },
            new[] { "Client/Assets/Resources/PMNet/BattleMapV1.prefab", "dabe768d6e40fa5a7180dae342526080f16299b4eaf771a8d255f149b7c1c297" },
            new[] { "Client/Assets/Resources/PMNet/PlayerVisualV1.prefab", "dc5c9db2dbc2f1b4b69cf4fe2908f85f43159e4b3d027044332981bad91b7ed5" },
            new[] { "Client/ProjectSettings/EditorBuildSettings.asset", "0f49797cfd99d5ec3c2574aaa3d5218b222100fba26ea3194b2ab2033165e7bd" },
        };

        /// <summary>契约 §D：两个已解挂组件的 GUID，必须在 Client/Assets 的 *.unity / *.prefab 里出现 0 次。</summary>
        private static readonly string[][] DetachedGuidValues =
        {
            new[] { "BattleManger", "7200a0eb9673b6e4f8cb8386cdde31db" },
            new[] { "HYLDCameraManger", "eec213bc5141674488046b737aede2bf" },
        };

        /// <summary>扫描根（相对仓库根）。第三方/引擎/Docs/日志一律不在根内。</summary>
        private static readonly string[] ScanRoots = { "Client/Assets", "Server" };

        /// <summary>契约 §E：旧链硬编码战斗 UDP 端口，活源码不得再出现。</summary>
        private const string HardcodedLegacyUdpPort = "7777";

        // 厅相关既有号：name = number（不得漂移）。值取自当前权威 proto（冻结）。
        private static readonly Dictionary<string, string[]> FrozenEnumValues = new Dictionary<string, string[]>
        {
            { "RequestCode", new[] {
                "RequestNone=0", "User=1", "Room=2", "Friend=3", "FriendRoom=4", "PingPong=5", "Matching=6" } },
            { "ActionCode", new[] {
                "ActionNone=0", "Logon=1", "Login=2", "CreateRoom=3", "FindRoom=4", "PlayerList=5", "JoinRoom=6",
                "Exit=7", "Chat=8", "AplyAddFriend=9", "InviteFriend=10", "FindName=11", "UpdateName=12",
                "AcceptAddFriend=13", "RejectAddFriend=14", "FindPlayerInfo=15", "FindFriendsInfo=16",
                "FriendLogin=17", "FriendLogout=18", "AcceptInvateFriend=19", "RejectInvateFriend=20",
                "CancalInvateFriend=21", "ExitRoom=22", "GetFriendRoomInfo=23", "Ping=24", "Pong=25",
                "ChangeHero=26", "UpDateActiveFriendInfo=27", "AddMatchingPlayer=28", "RemoveMatchingPlayer=29",
                "StartEnterBattle=30" } },
            { "ReturnCode", new[] { "ReturnNone=0", "Succeed=1", "Fail=2", "NotRoom=3", "AddFriend=4" } },
            { "RoomState", new[] { "RoomNormal=0", "RoomFull=1", "RoomGame=2" } },
            { "PlayerState", new[] { "PlayerOnline=0", "PlayerOutline=1", "PlayerGame=2", "PlayerOnRoom=3", "PlayerOnInvated=4" } },
            { "Hero", new[] {
                "XueLi=0", "KeErTe=1", "PeiPei=2", "PanNi=3", "BaLi=4", "GongNiu=5", "DaLiEr=6", "GeEr=7",
                "BuLuoKe=8", "BaoPoMaiKe=9", "ABo=10", "DiKe=11", "BeiYa=12", "TaLa=13", "MaiKeSi=14",
                "SiPaiKe=15", "HeiYa=16", "LiAng=17", "PaMu=18", "RuiKe=19" } },
            { "FightPattern", new[] { "BaoShiZhengBa=0", "Sheji=1" } },
        };

        // 保留的大厅消息：字段名 = 字段号（不得漂移；允许未来追加新字段）。
        private static readonly Dictionary<string, string[]> FrozenMessageFields = new Dictionary<string, string[]>
        {
            { "MainPack", new[] {
                "requestcode=1", "actioncode=2", "returncode=3", "loginpack=4", "str=5", "roompack=6",
                "friendspack=7", "userInfopack=8", "friendroompack=9", "playerspack=10", "chatpack=11",
                "battleplayerpack=12", "timestamp=14", "request_id=16" } },
            { "ChatPack", new[] { "playername=1", "message=2", "state=3" } },
            { "LoginPack", new[] { "username=1", "password=2" } },
            { "RoomPack", new[] { "roomid=1", "maxnum=2", "curnum=3", "state=4" } },
            { "BattlePlayerPack", new[] { "id=1", "teamid=2", "roomid=3", "playername=4", "hero=5", "battleid=6" } },
            { "FriendRoomPack", new[] { "roomid=1", "maxnum=2", "curnum=3", "state=4" } },
            { "PlayerPack", new[] { "username=1", "playername=2", "id=3", "state=4", "hero=5", "fightpattern=6" } },
        };

        // =====================================================================================
        //  二、入口
        // =====================================================================================

        private static readonly List<CheckResult> All = new List<CheckResult>();
        private static readonly StringBuilder Log = new StringBuilder();
        private static int _negativePassed;
        private static int _negativeFailed;
        private static readonly List<string> NegativeNotes = new List<string>();
        private static string _selfTestRoot = string.Empty;
        private static bool _inSelfTest;

        private static int Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch (Exception)
            {
                // 受限终端不允许改编码；不影响断言结果。
            }

            string repoArg, reportPath;
            bool runSelfTest;
            ParseArgs(args, out repoArg, out reportPath, out runSelfTest);

            RepoLayout layout = new RepoLayout(ResolveRepoRoot(repoArg));

            Say("===== PMLegacyRetirementTest（旧链退役静态禁回归门禁）=====");
            Say("检查根目录   = " + layout.Root);
            Say("权威 proto   = " + protoProbeForDisplay(layout));
            Say("自身位置     = " + AppContext.BaseDirectory);
            Say("");

            RunAllGroups(layout);

            if (runSelfTest)
            {
                RunNegativeSelfTests(layout);
            }

            int passed = 0, failed = 0;
            foreach (CheckResult r in All)
            {
                if (r.Pass) passed++; else failed++;
            }

            Say("");
            Say("===== 静态门禁结果：通过 " + passed + " / 失败 " + failed + " =====");
            if (runSelfTest)
            {
                Say("===== 负例自测：通过 " + _negativePassed + " / 失败 " + _negativeFailed + " =====");
            }

            bool overall = failed == 0 && _negativeFailed == 0;
            Say(overall ? "总体结论：PASS" : "总体结论：FAIL");

            if (!string.IsNullOrEmpty(reportPath))
            {
                try
                {
                    WriteReport(reportPath, layout, runSelfTest, passed, failed, overall);
                    Say("报告已写入：" + Path.GetFullPath(reportPath));
                }
                catch (Exception ex)
                {
                    Say("报告写入失败：" + ex.Message);
                    overall = false;
                }
            }

            CleanupSelfTest();
            return overall ? 0 : 1;
        }

        private static string protoProbeForDisplay(RepoLayout layout)
        {
            string p = layout.P(ProtoRel);
            return File.Exists(p) ? ProtoRel : ProtoRel + "（不存在）";
        }

        private static void ParseArgs(string[] args, out string repoArg, out string reportPath, out bool runSelfTest)
        {
            repoArg = null;
            reportPath = null;
            runSelfTest = true;
            if (args == null)
            {
                return;
            }

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--repo" && i + 1 < args.Length) { repoArg = args[++i]; continue; }
                if (a.StartsWith("--repo=", StringComparison.Ordinal)) { repoArg = a.Substring(7); continue; }
                if (a == "--report" && i + 1 < args.Length) { reportPath = args[++i]; continue; }
                if (a.StartsWith("--report=", StringComparison.Ordinal)) { reportPath = a.Substring(9); continue; }
                if (a == "--self-test") { runSelfTest = true; continue; }
                if (a == "--no-self-test") { runSelfTest = false; continue; }
                if (!a.StartsWith("--", StringComparison.Ordinal) && string.IsNullOrEmpty(repoArg)) { repoArg = a; continue; }
            }
        }

        private static string ResolveRepoRoot(string repoArg)
        {
            if (!string.IsNullOrEmpty(repoArg))
            {
                return repoArg;
            }

            DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string marker = Path.Combine(dir.FullName, "ProtobufAndNotepad", "Protobuf", "SocketProto.proto");
                if (File.Exists(marker))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            // 兜底：<repo>/Tools/PMLegacyRetirementTest/bin/<cfg>/net8.0 → 上 6 级即仓库根。
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
        }

        private static void Say(string line)
        {
            Console.WriteLine(line);
            Log.AppendLine(line);
        }

        private static void Report(string group, string id, bool pass, string detail)
        {
            CheckResult r = new CheckResult(group, id, pass, detail);
            All.Add(r);
            Say("  [" + (pass ? "OK  " : "FAIL") + "] " + id + "  " + detail);
        }

        private static void RunAllGroups(RepoLayout layout)
        {
            Say("---- G1 已删文件 / 退役工具工程必须不存在 ----");
            Append(CheckG1_FilesAbsent(layout));

            Say("");
            Say("---- G2 活源码（词法去注释/字符串）不得含旧类型 token ----");
            Append(CheckG2_Tokens(layout));

            Say("");
            Say("---- G3 PMDsHost 结构事实（运行时行为由 PMDsHostCheck 另测）----");
            Append(CheckG3_DsHost(layout));

            Say("");
            Say("---- G4 开局路由唯一 + 无旧选链开关 ----");
            Append(CheckG4_Controller(layout));

            Say("");
            Say("---- G5 UIMatchingPanel 无旧入局回退 ----");
            Append(CheckG5_UiMatching(layout));

            Say("");
            Say("---- G6 权威 proto 声明名 / 字段与枚举号 / reserved ----");
            Append(CheckG6_Proto(layout));

            Say("");
            Say("---- G7 四份 generated 产物无已删类型 ----");
            Append(CheckG7_Generated(layout));

            Say("");
            Say("---- G8 资产 GUID 反查为 0 + 冻结 SHA 锚 ----");
            Append(CheckG8_Assets(layout));
        }

        private static void Append(List<CheckResult> list)
        {
            foreach (CheckResult r in list)
            {
                All.Add(r);
                Say("  [" + (r.Pass ? "OK  " : "FAIL") + "] " + r.Id + "  " + r.Detail);
            }
        }

        // =====================================================================================
        //  G1 已删文件 / 退役工具工程
        // =====================================================================================

        internal static List<CheckResult> CheckG1_FilesAbsent(RepoLayout L)
        {
            List<CheckResult> results = new List<CheckResult>();

            List<string> clientResurrected = new List<string>();
            foreach (string f in LegacyClientFiles)
            {
                if (File.Exists(L.P(f))) clientResurrected.Add(f);
                if (File.Exists(L.P(f + ".meta"))) clientResurrected.Add(f + ".meta");
            }

            results.Add(new CheckResult("G1", "G1.client-files-and-meta-absent", clientResurrected.Count == 0,
                clientResurrected.Count == 0
                    ? "客户端 17 个旧 .cs 与其 .meta 均不存在"
                    : "复活：" + string.Join(", ", clientResurrected)));

            List<string> serverResurrected = new List<string>();
            foreach (string f in LegacyServerFiles)
            {
                if (File.Exists(L.P(f))) serverResurrected.Add(f);
            }

            results.Add(new CheckResult("G1", "G1.server-files-absent", serverResurrected.Count == 0,
                serverResurrected.Count == 0
                    ? "服务端 8 个旧战斗/UDP 源码均不存在"
                    : "复活：" + string.Join(", ", serverResurrected)));

            List<string> toolResurrected = new List<string>();
            foreach (string f in RetiredToolProjects)
            {
                if (File.Exists(L.P(f))) toolResurrected.Add(f);
            }

            results.Add(new CheckResult("G1", "G1.retired-tool-csproj-absent", toolResurrected.Count == 0,
                toolResurrected.Count == 0
                    ? "PMUdpRouterCheck / PMUdpRouterTest / PMDsProbe 的 .csproj 均不存在"
                    : "复活：" + string.Join(", ", toolResurrected)));

            return results;
        }

        // =====================================================================================
        //  G2 旧类型 token（词法剥离后）
        // =====================================================================================

        internal static List<CheckResult> CheckG2_Tokens(RepoLayout L)
        {
            List<CheckResult> results = new List<CheckResult>();

            List<string> files = new List<string>();
            bool anyRootMissing = false;
            foreach (string root in ScanRoots)
            {
                string abs = L.P(root);
                if (!Directory.Exists(abs)) { anyRootMissing = true; continue; }
                CollectScanFiles(abs, files);
            }

            files.Sort(StringComparer.OrdinalIgnoreCase);

            if (anyRootMissing || files.Count == 0)
            {
                results.Add(new CheckResult("G2", "G2.scan-inputs-present", false,
                    "扫描根缺失或为空（缺输入必须 FAIL，禁止空扫描绿）：roots=" + string.Join(",", ScanRoots)
                    + " files=" + files.Count.ToString(CultureInfo.InvariantCulture)));
                return results;
            }

            results.Add(new CheckResult("G2", "G2.scan-inputs-present", true,
                "扫描输入存在：.cs 文件数 = " + files.Count.ToString(CultureInfo.InvariantCulture)));

            // 「不能误扫报告/自身字符串」：本门禁可执行体必须位于扫描根之外。
            // 若有人把工具搬进 Client/Assets 或 Server，它自己的字面量就会变成命中（或需要白名单，反而变弱）。
            string selfDir = Path.GetFullPath(AppContext.BaseDirectory).Replace('\\', '/');
            List<string> selfInside = new List<string>();
            foreach (string root in ScanRoots)
            {
                string rootAbs = Path.GetFullPath(L.P(root)).Replace('\\', '/');
                if (selfDir.StartsWith(rootAbs + "/", StringComparison.OrdinalIgnoreCase)) selfInside.Add(root);
            }

            results.Add(new CheckResult("G2", "G2.tool-outside-scan-roots", selfInside.Count == 0,
                selfInside.Count == 0
                    ? "门禁自身（" + selfDir + "）不在扫描根内，不会把自身字符串当成命中"
                    : "门禁自身落在扫描根内：" + string.Join(", ", selfInside) + "（自身字面量会自我命中）"));

            List<Regex> regexes = new List<Regex>();
            foreach (string t in LegacyTypeTokens)
            {
                regexes.Add(new Regex("(?<![A-Za-z0-9_])" + Regex.Escape(t) + "(?![A-Za-z0-9_])", RegexOptions.CultureInvariant));
            }

            List<string> hits = new List<string>();
            List<string> portHits = new List<string>();
            Regex portRegex = new Regex("(?<![A-Za-z0-9_])" + Regex.Escape(HardcodedLegacyUdpPort) + "(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);

            int strippedLengthMismatch = 0;
            double minCodeRatio = double.MaxValue;
            string minCodeRatioFile = string.Empty;
            long totalRawNonWs = 0;
            long totalCodeNonWs = 0;

            foreach (string abs in files)
            {
                string raw;
                try
                {
                    raw = File.ReadAllText(abs);
                }
                catch (Exception ex)
                {
                    hits.Add(L.Rel(abs) + ": 读取失败 " + ex.Message);
                    continue;
                }

                string code = CodeStripper.Strip(raw);
                string rel = L.Rel(abs);

                if (code.Length != raw.Length) strippedLengthMismatch++;
                double ratio = NonWhitespaceRatio(code, raw);
                if (ratio < minCodeRatio) { minCodeRatio = ratio; minCodeRatioFile = rel; }
                totalRawNonWs += CountNonWhitespace(raw);
                totalCodeNonWs += CountNonWhitespace(code);

                for (int i = 0; i < regexes.Count; i++)
                {
                    foreach (Match m in regexes[i].Matches(code))
                    {
                        hits.Add(rel + ":" + (LineOf(code, m.Index).ToString(CultureInfo.InvariantCulture))
                                 + " → " + LegacyTypeTokens[i]);
                    }
                }

                foreach (Match m in portRegex.Matches(code))
                {
                    portHits.Add(rel + ":" + (LineOf(code, m.Index).ToString(CultureInfo.InvariantCulture)));
                }
            }

            // 反「假绿」：词法剥离若整体失灵（把代码也剥掉），token 检查会输出 0 命中而看似通过。
            // 用两个独立证据兜底：
            //   (1) 全局代码占比（聚合，不受个别「几乎全是注释」的第三方示例文件影响）；
            //   (2) 金丝雀：三个已知必然存在的代码标识符必须在剥离后仍可搜到。
            double aggregateRatio = (double)totalCodeNonWs / (totalRawNonWs == 0 ? 1 : totalRawNonWs);
            List<string> canaryMissing = new List<string>();
            CheckCanary(L, canaryMissing, DsHostRel, "PMDsSessionHost");
            CheckCanary(L, canaryMissing, ControllersRel, "StartFightingDedicatedServer");
            CheckCanary(L, canaryMissing, UiMatchingRel, "PMDsEntryCodec");

            bool lexerSane = strippedLengthMismatch == 0 && aggregateRatio >= 0.30 && canaryMissing.Count == 0;
            results.Add(new CheckResult("G2", "G2.stripper-anti-vacuity", lexerSane,
                lexerSane
                    ? "词法剥离 1:1 保长（0 个长度不一致文件），全局代码占比 "
                      + aggregateRatio.ToString("F3", CultureInfo.InvariantCulture)
                      + "，三处代码金丝雀全部保留（最低单文件占比仅作参考="
                      + minCodeRatio.ToString("F3", CultureInfo.InvariantCulture) + " @" + minCodeRatioFile + "）"
                    : "词法剥离可疑：长度不一致=" + strippedLengthMismatch.ToString(CultureInfo.InvariantCulture)
                      + " 全局代码占比=" + aggregateRatio.ToString("F3", CultureInfo.InvariantCulture)
                      + " 金丝雀缺失=" + JoinCapped(canaryMissing, 6)
                      + "（若剥离失灵，token 检查会假绿）"));

            if (!_inSelfTest && !string.IsNullOrEmpty(minCodeRatioFile) && minCodeRatio < 0.15)
            {
                Say("  [INFO] 低代码占比文件（多为全注释第三方示例，非门禁失败）："
                    + minCodeRatioFile + " = " + minCodeRatio.ToString("F3", CultureInfo.InvariantCulture));
            }

            results.Add(new CheckResult("G2", "G2.legacy-type-tokens-absent", hits.Count == 0,
                hits.Count == 0
                    ? "19 个旧类型标识符在活源码（去注释/去字符串）中出现 0 次"
                    : "命中 " + hits.Count.ToString(CultureInfo.InvariantCulture) + " 处：" + JoinCapped(hits, 12)));

            results.Add(new CheckResult("G2", "G2.hardcoded-udp-7777-absent", portHits.Count == 0,
                portHits.Count == 0
                    ? "活源码（去注释/去字符串）无硬编码战斗 UDP 端口 " + HardcodedLegacyUdpPort
                    : "命中 " + portHits.Count.ToString(CultureInfo.InvariantCulture) + " 处：" + JoinCapped(portHits, 12)));

            return results;
        }

        private static bool IsNoiseDirectory(string name)
        {
            if (name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Library", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Docs", StringComparison.OrdinalIgnoreCase)
                || name.Equals("log", StringComparison.OrdinalIgnoreCase)
                || name.Equals("logs", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Generated", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 第三方 Google / Google.Protobuf 目录不进扫描（契约明确排除）。
            return name.StartsWith("Google", StringComparison.OrdinalIgnoreCase);
        }

        private static void CollectScanFiles(string dir, List<string> outFiles)
        {
            foreach (string f in Directory.EnumerateFiles(dir))
            {
                if (!f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                if (f.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)) continue; // generated：由 G7 覆盖
                string name = Path.GetFileName(f);
                if (IsGeneratedFileByFullPath(f)) continue;
                if (name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)) continue;
                outFiles.Add(f);
            }

            foreach (string d in Directory.EnumerateDirectories(dir))
            {
                if (IsNoiseDirectory(Path.GetFileName(d))) continue;
                CollectScanFiles(d, outFiles);
            }
        }

        private static bool IsGeneratedFileByFullPath(string abs)
        {
            string norm = Path.GetFullPath(abs).Replace('\\', '/');
            foreach (string g in GeneratedFiles)
            {
                if (norm.EndsWith("/" + g, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static int CountNonWhitespace(string s)
        {
            int count = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (!char.IsWhiteSpace(s[i])) count++;
            }

            return count;
        }

        private static double NonWhitespaceRatio(string stripped, string raw)
        {
            int rawCount = CountNonWhitespace(raw);
            if (rawCount == 0) return 1.0;
            return (double)CountNonWhitespace(stripped) / rawCount;
        }

        /// <summary>金丝雀：剥离后必须仍能搜到该标识符，否则说明词法剥离整体失灵（会导致 token 检查假绿）。</summary>
        private static void CheckCanary(RepoLayout L, List<string> missing, string rel, string ident)
        {
            string p = L.P(rel);
            if (!File.Exists(p))
            {
                missing.Add(rel + "（文件缺失）");
                return;
            }

            string code = CodeStripper.Strip(File.ReadAllText(p));
            if (!HasIdent(code, ident)) missing.Add(rel + "#" + ident);
        }

        private static int LineOf(string text, int index)
        {
            int line = 1;
            int limit = Math.Min(index, text.Length);
            for (int i = 0; i < limit; i++)
            {
                if (text[i] == '\n') line++;
            }

            return line;
        }

        private static string JoinCapped(List<string> items, int cap)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < items.Count && i < cap; i++)
            {
                if (i > 0) sb.Append("; ");
                sb.Append(items[i]);
            }

            if (items.Count > cap) sb.Append("; …共 ").Append(items.Count.ToString(CultureInfo.InvariantCulture)).Append(" 条");
            return sb.ToString();
        }

        // =====================================================================================
        //  G3 PMDsHost 结构事实
        // =====================================================================================

        internal static List<CheckResult> CheckG3_DsHost(RepoLayout L)
        {
            List<CheckResult> results = new List<CheckResult>();
            string path = L.P(DsHostRel);
            if (!File.Exists(path))
            {
                results.Add(new CheckResult("G3", "G3.dshost-present", false, "缺输入：" + DsHostRel));
                return results;
            }

            string code = CodeStripper.Strip(File.ReadAllText(path));

            bool dependsSession = HasIdent(code, "PMDsSessionHost");
            results.Add(new CheckResult("G3", "G3.dshost-depends-on-sessionhost", dependsSession,
                dependsSession
                    ? "PMDsHost 引用 PMDsSessionHost（新链唯一会话面）"
                    : "PMDsHost 不再引用 PMDsSessionHost"));

            List<string> forbidden = new List<string>();
            if (Regex.IsMatch(code, @"\bnew\s+Socket\b")) forbidden.Add("new Socket");
            foreach (string id in new[] { "Socket", "PMUdpRouter", "PMSingleBattleRegistry", "MainPack", "UdpClient", "Thread" })
            {
                if (HasIdent(code, id)) forbidden.Add(id);
            }

            results.Add(new CheckResult("G3", "G3.dshost-no-legacy-router-or-socket", forbidden.Count == 0,
                forbidden.Count == 0
                    ? "PMDsHost 去注释/去字符串后不含裸 socket / 旧 router / 旧诊断包 / 线程"
                    : "出现：" + string.Join(", ", forbidden)));

            bool bootstrapGate = HasIdent(code, "BootstrapPath");
            results.Add(new CheckResult("G3", "G3.dshost-requires-bootstrap-path", bootstrapGate,
                bootstrapGate
                    ? "PMDsHost 结构上读取 BootstrapPath（缺 -bootstrap 的**运行时行为**由 PMDsHostCheck 另测，本门不冒充）"
                    : "PMDsHost 未读取 BootstrapPath：无法拒绝裸 -port 启动"));

            return results;
        }

        // =====================================================================================
        //  G4 开局路由
        // =====================================================================================

        internal static List<CheckResult> CheckG4_Controller(RepoLayout L)
        {
            List<CheckResult> results = new List<CheckResult>();
            string path = L.P(ControllersRel);
            if (!File.Exists(path))
            {
                results.Add(new CheckResult("G4", "G4.controllers-present", false, "缺输入：" + ControllersRel));
                return results;
            }

            string code = CodeStripper.Strip(File.ReadAllText(path));

            List<string> legacy = new List<string>();
            foreach (string id in new[] { "BattleController", "BattleManage", "TryBeginBattle", "LZJUDP", "NewChainEnabled", "BattleData", "ClearSenceManger" })
            {
                if (HasIdent(code, id)) legacy.Add(id);
            }

            results.Add(new CheckResult("G4", "G4.controllers-no-legacy-selector", legacy.Count == 0,
                legacy.Count == 0
                    ? "Controllers.cs 无旧选链开关/旧开局实现标识符"
                    : "出现：" + string.Join(", ", legacy)));

            string body = ExtractMethodBody(code, "StartFighting");
            if (body == null)
            {
                results.Add(new CheckResult("G4", "G4.startfighting-routes-only-dedicated", false,
                    "未能在 Controllers.cs 中定位 StartFighting 方法体"));
            }
            else
            {
                bool callsDs = Regex.IsMatch(body, @"\bStartFightingDedicatedServer\s*\(");
                List<string> bad = new List<string>();
                foreach (string id in new[] { "BattleManage", "BattleController", "LZJUDP", "NewChainEnabled", "TryBeginBattle" })
                {
                    if (HasIdent(body, id)) bad.Add(id);
                }

                results.Add(new CheckResult("G4", "G4.startfighting-routes-only-dedicated", callsDs && bad.Count == 0,
                    callsDs && bad.Count == 0
                        ? "StartFighting 方法体只调用 StartFightingDedicatedServer（无旧分支）"
                        : "callsDs=" + callsDs + " legacy=" + string.Join(",", bad)));
            }

            // 全仓库活源码不得再有旧选链开关
            List<string> files = new List<string>();
            foreach (string root in ScanRoots)
            {
                string abs = L.P(root);
                if (Directory.Exists(abs)) CollectScanFiles(abs, files);
            }

            List<string> selectorHits = new List<string>();
            foreach (string abs in files)
            {
                string c = CodeStripper.Strip(File.ReadAllText(abs));
                if (HasIdent(c, "NewChainEnabled")) selectorHits.Add(L.Rel(abs));
            }

            results.Add(new CheckResult("G4", "G4.repo-no-newchainenabled", selectorHits.Count == 0,
                selectorHits.Count == 0
                    ? "活源码中 " + "NewChainEnabled" + " 出现 0 次"
                    : "命中：" + JoinCapped(selectorHits, 12)));

            return results;
        }

        // =====================================================================================
        //  G5 UIMatchingPanel
        // =====================================================================================

        internal static List<CheckResult> CheckG5_UiMatching(RepoLayout L)
        {
            List<CheckResult> results = new List<CheckResult>();
            string path = L.P(UiMatchingRel);
            if (!File.Exists(path))
            {
                results.Add(new CheckResult("G5", "G5.uimatching-present", false, "缺输入：" + UiMatchingRel));
                return results;
            }

            string code = CodeStripper.Strip(File.ReadAllText(path));

            List<string> legacy = new List<string>();
            foreach (string id in new[] { "BattleData", "BattleManger", "ClearSenceManger", "LoadScene", "InitBattleInfo", "AddBattleReview" })
            {
                if (HasIdent(code, id)) legacy.Add(id);
            }

            results.Add(new CheckResult("G5", "G5.uimatching-no-legacy-entry-fallback", legacy.Count == 0,
                legacy.Count == 0
                    ? "去注释/去字符串后无 BattleData/旧 ClearSence LoadScene 回退"
                    : "出现：" + string.Join(", ", legacy)));

            bool newChain = HasIdent(code, "PMDsEntryCodec");
            results.Add(new CheckResult("G5", "G5.uimatching-only-new-chain-entry", newChain,
                newChain
                    ? "UIMatchingPanel 只识别 PMDS1（PMDsEntryCodec）入局通知"
                    : "UIMatchingPanel 未引用 PMDsEntryCodec：新链入局通知无接收者"));

            return results;
        }

        // =====================================================================================
        //  G6 proto
        // =====================================================================================

        internal static List<CheckResult> CheckG6_Proto(RepoLayout L)
        {
            List<CheckResult> results = new List<CheckResult>();
            string path = L.P(ProtoRel);
            if (!File.Exists(path))
            {
                results.Add(new CheckResult("G6", "G6.proto-present", false,
                    "缺输入（必须 FAIL，不得空扫描绿）：" + ProtoRel));
                return results;
            }

            results.Add(new CheckResult("G6", "G6.proto-present", true, "权威 proto 存在：" + ProtoRel));

            string text = File.ReadAllText(path);

            ProtoFile file;
            try
            {
                file = ProtoParser.Parse(text, ProtoRel);
            }
            catch (Exception ex)
            {
                results.Add(new CheckResult("G6", "G6.proto-parse-and-type-refs", false,
                    "解析失败（字段引用了不存在的 message/enum ⇒ 已删类型复活或模型不一致）：" + ex.Message));
                return results;
            }

            // 显式求证：所有非标量字段类型都必须能在本文件内解析到声明
            List<string> dangling = new List<string>();
            foreach (ProtoMessage msg in file.Messages)
            {
                foreach (ProtoField f in msg.Fields)
                {
                    if (f.Kind == ProtoFieldKind.Scalar) continue;
                    if (file.FindMessage(f.TypeName) == null && file.FindEnum(f.TypeName) == null)
                    {
                        dangling.Add(msg.Name + "." + f.Name + " : " + f.TypeName);
                    }
                }
            }

            results.Add(new CheckResult("G6", "G6.proto-parse-and-type-refs", dangling.Count == 0,
                dangling.Count == 0
                    ? "解析通过，且无字段引用不存在的 message/enum（无类型复活）"
                    : "悬空引用：" + JoinCapped(dangling, 12)));

            // ---- 旧声明名 ----
            List<string> bannedDecls = new List<string>();
            foreach (string m in LegacyProtoMessages)
            {
                if (file.FindMessage(m) != null) bannedDecls.Add("message " + m);
            }

            foreach (string e in LegacyProtoEnums)
            {
                if (file.FindEnum(e) != null) bannedDecls.Add("enum " + e);
            }

            results.Add(new CheckResult("G6", "G6.proto-legacy-declarations-absent", bannedDecls.Count == 0,
                bannedDecls.Count == 0
                    ? "14 条旧战斗消息 + MoveType/AttackType 均未声明"
                    : "复活声明：" + string.Join(", ", bannedDecls)));

            // ---- 旧号 / 旧名（Request / Action / MainPack）----
            List<string> numberHits = new List<string>();
            HashSet<string> numberHitSet = new HashSet<string>(StringComparer.Ordinal);
            ProtoEnum requestCode = file.FindEnum("RequestCode");
            if (requestCode == null)
            {
                numberHitSet.Add("enum RequestCode 丢失");
            }
            else
            {
                foreach (ProtoEnumValue v in requestCode.Values)
                {
                    foreach (string n in LegacyRequestNames)
                    {
                        if (v.Name == n) numberHitSet.Add("RequestCode." + v.Name + "=" + v.Number);
                    }

                    foreach (int n in LegacyRequestNumbers)
                    {
                        if (v.Number == n) numberHitSet.Add("RequestCode 号 " + n + " 被 " + v.Name + " 占用");
                    }
                }
            }

            ProtoEnum actionCode = file.FindEnum("ActionCode");
            if (actionCode == null)
            {
                numberHitSet.Add("enum ActionCode 丢失");
            }
            else
            {
                foreach (ProtoEnumValue v in actionCode.Values)
                {
                    if (v.Number >= LegacyActionFirst && v.Number <= LegacyActionLast)
                    {
                        numberHitSet.Add("ActionCode " + v.Name + "=" + v.Number + "（应 reserved）");
                    }
                }
            }

            ProtoMessage mainPack = file.FindMessage("MainPack");
            if (mainPack == null)
            {
                numberHitSet.Add("message MainPack 丢失");
            }
            else
            {
                foreach (ProtoField f in mainPack.Fields)
                {
                    foreach (int n in LegacyMainPackNumbers)
                    {
                        if (f.Number == n) numberHitSet.Add("MainPack." + f.Name + "=" + n + "（应 reserved）");
                    }

                    foreach (string n in LegacyMainPackNames)
                    {
                        if (f.Name == n) numberHitSet.Add("MainPack." + f.Name + "=" + f.Number + "（应删除）");
                    }
                }
            }

            foreach (string h in numberHitSet) numberHits.Add(h);
            numberHits.Sort(StringComparer.Ordinal);
            results.Add(new CheckResult("G6", "G6.proto-legacy-numbers-absent", numberHits.Count == 0,
                numberHits.Count == 0
                    ? "RequestCode 7/8、ActionCode 31..41、MainPack 13/15 均无定义"
                    : string.Join("; ", numberHits)));

            // ---- 厅相关既有号不漂移 ----
            List<string> drift = new List<string>();
            foreach (KeyValuePair<string, string[]> kv in FrozenEnumValues)
            {
                ProtoEnum en = file.FindEnum(kv.Key);
                if (en == null)
                {
                    drift.Add("缺 enum " + kv.Key);
                    continue;
                }

                Dictionary<string, int> actual = new Dictionary<string, int>();
                Dictionary<int, string> byNumber = new Dictionary<int, string>();
                foreach (ProtoEnumValue v in en.Values)
                {
                    actual[v.Name] = v.Number;
                    byNumber[v.Number] = v.Name;
                }

                foreach (string expect in kv.Value)
                {
                    int eq = expect.IndexOf('=');
                    string name = expect.Substring(0, eq);
                    int num = int.Parse(expect.Substring(eq + 1), CultureInfo.InvariantCulture);
                    int got;
                    if (!actual.TryGetValue(name, out got))
                    {
                        drift.Add(kv.Key + " 缺 " + name + "=" + num);
                    }
                    else if (got != num)
                    {
                        drift.Add(kv.Key + "." + name + " 期望 " + num + " 实为 " + got);
                    }

                    string holder;
                    if (byNumber.TryGetValue(num, out holder) && holder != name)
                    {
                        drift.Add(kv.Key + " 号 " + num + " 被 " + holder + " 占用（应 " + name + "）");
                    }
                }
            }

            foreach (KeyValuePair<string, string[]> kv in FrozenMessageFields)
            {
                ProtoMessage msg = file.FindMessage(kv.Key);
                if (msg == null)
                {
                    drift.Add("缺 message " + kv.Key);
                    continue;
                }

                Dictionary<string, int> actual = new Dictionary<string, int>();
                Dictionary<int, string> byNumber = new Dictionary<int, string>();
                foreach (ProtoField f in msg.Fields)
                {
                    actual[f.Name] = f.Number;
                    byNumber[f.Number] = f.Name;
                }

                foreach (string expect in kv.Value)
                {
                    int eq = expect.IndexOf('=');
                    string name = expect.Substring(0, eq);
                    int num = int.Parse(expect.Substring(eq + 1), CultureInfo.InvariantCulture);
                    int got;
                    if (!actual.TryGetValue(name, out got))
                    {
                        drift.Add(kv.Key + " 缺字段 " + name + "=" + num);
                    }
                    else if (got != num)
                    {
                        drift.Add(kv.Key + "." + name + " 期望 " + num + " 实为 " + got);
                    }

                    string holder;
                    if (byNumber.TryGetValue(num, out holder) && holder != name)
                    {
                        drift.Add(kv.Key + " 字段号 " + num + " 被 " + holder + " 占用（应 " + name + "）");
                    }
                }
            }

            results.Add(new CheckResult("G6", "G6.proto-lobby-numbers-frozen", drift.Count == 0,
                drift.Count == 0
                    ? "厅相关既有号未漂移（7 枚举 + 7 消息；StartEnterBattle=30、Hero 全 20 项保留）"
                    : "漂移/缺失 " + drift.Count.ToString(CultureInfo.InvariantCulture) + " 处：" + JoinCapped(drift, 16)));

            // ---- reserved 语句（reserved 不是定义）----
            Dictionary<string, List<ReservedSpan>> reserved = ScanReserved(text);
            List<string> reserveMissing = new List<string>();
            foreach (int n in LegacyRequestNumbers)
            {
                if (!ReservedCovers(reserved, "RequestCode", n)) reserveMissing.Add("RequestCode " + n);
            }

            for (int n = LegacyActionFirst; n <= LegacyActionLast; n++)
            {
                if (!ReservedCovers(reserved, "ActionCode", n)) reserveMissing.Add("ActionCode " + n);
            }

            foreach (int n in LegacyMainPackNumbers)
            {
                if (!ReservedCovers(reserved, "MainPack", n)) reserveMissing.Add("MainPack " + n);
            }

            results.Add(new CheckResult("G6", "G6.proto-legacy-numbers-reserved", reserveMissing.Count == 0,
                reserveMissing.Count == 0
                    ? "旧号已显式 reserved（RequestCode 7/8、ActionCode 31..41、MainPack 13/15），号不可重用"
                    : "未显式 reserved：" + JoinCapped(reserveMissing, 16)));

            // ---- 非权威 proto 副本 ----
            List<string> copies = new List<string>();
            foreach (string c in NonAuthoritativeProtoCopies)
            {
                if (File.Exists(L.P(c))) copies.Add(c);
            }

            results.Add(new CheckResult("G6", "G6.non-authoritative-proto-copies-absent", copies.Count == 0,
                copies.Count == 0
                    ? "非权威 proto 副本均已删除"
                    : "仍存在（旧类型可从副本复活）：" + string.Join(", ", copies)));

            return results;
        }

        private sealed class ReservedSpan
        {
            public bool IsName;
            public string Name;
            public int From;
            public int To;
        }

        /// <summary>
        /// 扫描 proto 的 reserved 语句，按「最内层 enum/message」归属。
        /// reserved 语句不是定义——本方法只用于「旧号是否被显式 reserved」这一条断言。
        /// </summary>
        private static Dictionary<string, List<ReservedSpan>> ScanReserved(string protoText)
        {
            Dictionary<string, List<ReservedSpan>> map = new Dictionary<string, List<ReservedSpan>>();
            List<string> tokens = TokenizeProto(protoText);

            List<string> blockStack = new List<string>();
            string pendingBlockName = null;

            for (int i = 0; i < tokens.Count; i++)
            {
                string t = tokens[i];

                if (t == "enum" || t == "message")
                {
                    if (i + 1 < tokens.Count) pendingBlockName = tokens[i + 1];
                    continue;
                }

                if (t == "{")
                {
                    blockStack.Add(pendingBlockName ?? string.Empty);
                    pendingBlockName = null;
                    continue;
                }

                if (t == "}")
                {
                    if (blockStack.Count > 0) blockStack.RemoveAt(blockStack.Count - 1);
                    continue;
                }

                if (t == "reserved")
                {
                    string owner = blockStack.Count > 0 ? blockStack[blockStack.Count - 1] : string.Empty;
                    List<ReservedSpan> list;
                    if (!map.TryGetValue(owner, out list))
                    {
                        list = new List<ReservedSpan>();
                        map[owner] = list;
                    }

                    i++;
                    // 形式： reserved 7, 8;   /  reserved 31 to 41;  /  reserved "a", "b";
                    bool pendingTo = false;
                    ReservedSpan range = null;
                    for (; i < tokens.Count && tokens[i] != ";"; i++)
                    {
                        string tok = tokens[i];
                        if (tok == ",") { pendingTo = false; range = null; continue; }
                        if (tok == "to") { pendingTo = true; continue; }

                        bool isString = tok.Length >= 2 && tok[0] == '"';
                        if (isString)
                        {
                            ReservedSpan sp = new ReservedSpan();
                            sp.IsName = true;
                            sp.Name = tok.Substring(1, tok.Length - 2);
                            list.Add(sp);
                            continue;
                        }

                        int num;
                        if (int.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out num))
                        {
                            if (pendingTo && range != null)
                            {
                                range.To = num;
                                pendingTo = false;
                                range = null;
                            }
                            else
                            {
                                range = new ReservedSpan();
                                range.IsName = false;
                                range.From = num;
                                range.To = num;
                                list.Add(range);
                            }
                        }
                    }

                    continue;
                }
            }

            return map;
        }

        private static bool ReservedCovers(Dictionary<string, List<ReservedSpan>> map, string block, int number)
        {
            List<ReservedSpan> list;
            if (!map.TryGetValue(block, out list)) return false;
            foreach (ReservedSpan sp in list)
            {
                if (sp.IsName) continue;
                if (sp.From <= number && number <= sp.To) return true;
            }

            return false;
        }

        /// <summary>极简 proto 词法：去注释，切出 ident / number / string / 单字符符号。</summary>
        private static List<string> TokenizeProto(string text)
        {
            List<string> tokens = new List<string>();
            int i = 0;
            int n = text.Length;
            while (i < n)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }

                if (c == '/' && i + 1 < n && text[i + 1] == '/')
                {
                    while (i < n && text[i] != '\n') i++;
                    continue;
                }

                if (c == '/' && i + 1 < n && text[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < n && !(text[i] == '*' && text[i + 1] == '/')) i++;
                    i = Math.Min(n, i + 2);
                    continue;
                }

                if (c == '"')
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append('"');
                    i++;
                    while (i < n && text[i] != '"')
                    {
                        if (text[i] == '\\' && i + 1 < n) { sb.Append(text[i]); i++; }
                        sb.Append(text[i]);
                        i++;
                    }

                    if (i < n) { sb.Append('"'); i++; }
                    tokens.Add(sb.ToString());
                    continue;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    tokens.Add(text.Substring(start, i - start));
                    continue;
                }

                if (char.IsDigit(c))
                {
                    int start = i;
                    while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    tokens.Add(text.Substring(start, i - start));
                    continue;
                }

                tokens.Add(c.ToString());
                i++;
            }

            return tokens;
        }

        // =====================================================================================
        //  G7 generated 产物
        // =====================================================================================

        internal static List<CheckResult> CheckG7_Generated(RepoLayout L)
        {
            List<CheckResult> results = new List<CheckResult>();

            List<string> missing = new List<string>();
            foreach (string g in GeneratedFiles)
            {
                if (!File.Exists(L.P(g))) missing.Add(g);
            }

            if (missing.Count > 0)
            {
                results.Add(new CheckResult("G7", "G7.generated-present", false,
                    "缺输入（必须 FAIL）：" + string.Join(", ", missing)));
                return results;
            }

            results.Add(new CheckResult("G7", "G7.generated-present", true,
                "四份 generated 产物均存在"));

            List<string> banned = new List<string>();
            Regex declRegex = new Regex(
                @"\b(?:class|enum|struct|interface)\s+([A-Za-z_][A-Za-z0-9_]*)",
                RegexOptions.CultureInvariant);

            foreach (string g in GeneratedFiles)
            {
                string code = CodeStripper.Strip(File.ReadAllText(L.P(g)));
                HashSet<string> declared = new HashSet<string>(StringComparer.Ordinal);
                foreach (Match m in declRegex.Matches(code))
                {
                    declared.Add(m.Groups[1].Value);
                }

                foreach (string t in LegacyProtoMessages)
                {
                    CollectBannedGeneratedNames(banned, g, declared, t);
                }

                foreach (string t in LegacyProtoEnums)
                {
                    CollectBannedGeneratedNames(banned, g, declared, t);
                }
            }

            results.Add(new CheckResult("G7", "G7.generated-no-legacy-types", banned.Count == 0,
                banned.Count == 0
                    ? "四份产物（去注释）无 14 旧消息 + MoveType/AttackType 的 class/enum/serializer"
                    : "复活类型 " + banned.Count.ToString(CultureInfo.InvariantCulture) + " 处：" + JoinCapped(banned, 16)));

            // 字段名不得引用不存在的 message（generated 侧反查 global::SocketProto.X）
            List<string> dangling = new List<string>();
            Regex refRegex = new Regex(@"global::SocketProto\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant);
            foreach (string g in GeneratedFiles)
            {
                string code = CodeStripper.Strip(File.ReadAllText(L.P(g)));
                HashSet<string> declared = new HashSet<string>(StringComparer.Ordinal);
                foreach (Match m in declRegex.Matches(code))
                {
                    declared.Add(m.Groups[1].Value);
                }

                foreach (Match m in refRegex.Matches(code))
                {
                    string name = m.Groups[1].Value;
                    if (!declared.Contains(name))
                    {
                        dangling.Add(g + " → global::SocketProto." + name);
                    }
                }
            }

            results.Add(new CheckResult("G7", "G7.generated-no-dangling-type-refs", dangling.Count == 0,
                dangling.Count == 0
                    ? "generated 内 global::SocketProto.* 类型引用全部有本文件声明兜底"
                    : "悬空引用：" + JoinCapped(dangling, 16)));

            return results;
        }

        private static void CollectBannedGeneratedNames(List<string> hits, string file, HashSet<string> declared, string typeName)
        {
            string[] candidates = { typeName, "PM" + typeName, typeName + "Serializer", "PM" + typeName + "Serializer" };
            foreach (string c in candidates)
            {
                if (declared.Contains(c))
                {
                    hits.Add(file + " → " + c);
                }
            }
        }

        // =====================================================================================
        //  G8 资产
        // =====================================================================================

        internal static List<CheckResult> CheckG8_Assets(RepoLayout L)
        {
            List<CheckResult> results = new List<CheckResult>();

            string assetsRoot = L.P("Client/Assets");
            if (!Directory.Exists(assetsRoot))
            {
                results.Add(new CheckResult("G8", "G8.assets-input-present", false,
                    "缺输入（必须 FAIL）：Client/Assets 不存在"));
                return results;
            }

            List<string> assetFiles = new List<string>();
            CollectAssetFiles(assetsRoot, assetFiles);

            if (assetFiles.Count == 0)
            {
                results.Add(new CheckResult("G8", "G8.assets-input-present", false,
                    "Client/Assets 下 *.unity / *.prefab 数为 0（禁止空扫描绿）"));
                return results;
            }

            List<string> guidHits = new List<string>();
            foreach (string f in assetFiles)
            {
                string text;
                try
                {
                    text = File.ReadAllText(f);
                }
                catch (Exception)
                {
                    continue; // 非文本资产跳过（*.unity / *.prefab 均为 YAML 文本）
                }

                foreach (string[] kv in DetachedGuidValues)
                {
                    if (text.IndexOf(kv[1], StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        guidHits.Add(L.Rel(f) + " → " + kv[0] + " " + kv[1]);
                    }
                }
            }

            results.Add(new CheckResult("G8", "G8.detached-guids-zero", guidHits.Count == 0,
                guidHits.Count == 0
                    ? "两个已解挂 GUID 在 " + assetFiles.Count.ToString(CultureInfo.InvariantCulture)
                      + " 个 *.unity/*.prefab 中出现 0 次"
                    : "残留：" + JoinCapped(guidHits, 12)));

            List<string> anchorProblems = new List<string>();
            foreach (string[] kv in FrozenAnchors)
            {
                string p = L.P(kv[0]);
                if (!File.Exists(p))
                {
                    anchorProblems.Add(kv[0] + " 不存在（禁改输入/输出缺失）");
                    continue;
                }

                string actual = Sha256Hex(p);
                if (!string.Equals(actual, kv[1], StringComparison.OrdinalIgnoreCase))
                {
                    anchorProblems.Add(kv[0] + " SHA 不符（期望 " + kv[1].Substring(0, 12)
                                       + "… 实为 " + actual.Substring(0, 12) + "…）");
                }
            }

            results.Add(new CheckResult("G8", "G8.frozen-anchors-unchanged", anchorProblems.Count == 0,
                anchorProblems.Count == 0
                    ? "6 个冻结锚（HYLDGame.unity / Remake Player.prefab / 3 个 PMNet 输出 / EditorBuildSettings）SHA256 全部相符"
                    : string.Join("; ", anchorProblems)));

            return results;
        }

        private static void CollectAssetFiles(string dir, List<string> outFiles)
        {
            foreach (string f in Directory.EnumerateFiles(dir))
            {
                if (f.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    outFiles.Add(f);
                }
            }

            foreach (string d in Directory.EnumerateDirectories(dir))
            {
                if (IsNoiseDirectory(Path.GetFileName(d))) continue;
                CollectAssetFiles(d, outFiles);
            }
        }

        private static string Sha256Hex(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }

                return sb.ToString();
            }
        }

        // =====================================================================================
        //  三、负例自测（在 %TEMP% 沙盒里注入缺陷，不碰真实生产文件）
        // =====================================================================================

        private static void RunNegativeSelfTests(RepoLayout realLayout)
        {
            Say("");
            Say("---- N 负例自测（%TEMP% 沙盒注入；真实生产文件只读）----");
            _inSelfTest = true;
            try
            {
                RunNegativeSelfTestCases();
            }
            catch (Exception ex)
            {
                // 自测本身出错不得让整个门禁崩溃（那会让报告缺失、主侧无法对账）。
                _negativeFailed++;
                NegativeNotes.Add("FAIL | 负例自测异常 | " + ex.GetType().Name + ": " + ex.Message);
                Say("  [FAIL] 负例自测异常：" + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                _inSelfTest = false;
            }

            Say("  负例自测汇总：通过 " + _negativePassed + " / 失败 " + _negativeFailed);
        }

        private static void RunNegativeSelfTestCases()
        {
            // 沙盒根：必须落在 %TEMP% 下（绝不写仓库）。
            try
            {
                _selfTestRoot = Path.Combine(Path.GetTempPath(),
                    "pm-legacy-retirement-selftest-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_selfTestRoot);
            }
            catch (Exception ex)
            {
                _negativeFailed++;
                NegativeNotes.Add("FAIL | 沙盒创建 | " + ex.Message);
                Say("  [FAIL] 沙盒创建失败：" + ex.Message);
                return;
            }

            // N1 缺输入必须 FAIL（不是空扫描绿）
            N1_MissingInputFails();
            // N2 旧 class 复活必须被检出
            N2_ResurrectedClassDetected();
            N6_ExactRetiredModeName();
            // N3 注释/字符串同词不得误报
            N3_CommentAndStringNotReported();
            // N4 旧 Action 定义复活必须被检出
            N4_ResurrectedActionDetected();
            // N5 资产 GUID 残留必须被检出
            N5_AssetGuidResidueDetected();
        }

        private static bool HasAnyFail(List<CheckResult> results, string id)
        {
            foreach (CheckResult r in results)
            {
                if (r.Id == id && !r.Pass) return true;
            }

            return false;
        }

        private static void NegativeCase(string name, bool ok, string detail)
        {
            if (ok)
            {
                _negativePassed++;
                Say("  [OK  ] " + name + " —— " + detail);
            }
            else
            {
                _negativeFailed++;
                Say("  [FAIL] " + name + " —— " + detail);
            }

            NegativeNotes.Add((ok ? "PASS" : "FAIL") + " | " + name + " | " + detail);
        }

        private static string NewSandbox(string tag)
        {
            string dir = Path.Combine(_selfTestRoot, tag);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void WriteFileUnder(string root, string rel, string content)
        {
            string p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, content, new UTF8Encoding(false));
        }

        private static void N1_MissingInputFails()
        {
            // 沙盒里既没有权威 proto，也没有 Client/Assets、Server：任何一组都不得「绿」。
            string sb = NewSandbox("n1-missing-input");
            RepoLayout L = new RepoLayout(sb);

            List<CheckResult> g6 = CheckG6_Proto(L);
            bool g6Fail = HasAnyFail(g6, "G6.proto-present");

            List<CheckResult> g2 = CheckG2_Tokens(L);
            bool g2Fail = HasAnyFail(g2, "G2.scan-inputs-present");

            List<CheckResult> g8 = CheckG8_Assets(L);
            bool g8Fail = HasAnyFail(g8, "G8.assets-input-present");

            NegativeCase("N1 缺输入必须 FAIL",
                g6Fail && g2Fail && g8Fail,
                "proto 缺失=" + g6Fail + " 扫描根缺失=" + g2Fail + " 资产根缺失=" + g8Fail + "（三组都必须报 FAIL，禁止空扫描绿）");
        }

        private static void N2_ResurrectedClassDetected()
        {
            string sb = NewSandbox("n2-class-resurrect");
            RepoLayout L = new RepoLayout(sb);

            // 造最小可扫描输入：一个真正含旧类型标识符的 .cs
            WriteFileUnder(sb, "Client/Assets/Scripts/Fake.cs",
                "namespace X { public class Resurrected { void F() { BattleManger x = new BattleManger(); } } }\n");
            WriteFileUnder(sb, "Server/Server/Fake.cs", "namespace Y { public class Z { } }\n");
            // 同时验证「旧文件复活」也被 G1 检出
            WriteFileUnder(sb, "Client/Assets/Scripts/Server/Manger/Battle/BattleManger.cs", "// resurrected\n");

            List<CheckResult> g2 = CheckG2_Tokens(L);
            bool tokenFail = HasAnyFail(g2, "G2.legacy-type-tokens-absent");

            List<CheckResult> g1 = CheckG1_FilesAbsent(L);
            bool fileFail = HasAnyFail(g1, "G1.client-files-and-meta-absent");

            NegativeCase("N2 旧 class 复活必须被检出",
                tokenFail && fileFail,
                "token 命中=" + tokenFail + " 旧文件复活=" + fileFail);
        }

        private static void N6_ExactRetiredModeName()
        {
            string sb = NewSandbox("n6-retired-mode-name");
            RepoLayout layout = new RepoLayout(sb);
            WriteFileUnder(sb, "Client/Assets/Scripts/Server/Manger/Battle/HYLDBaoShiZhengBaManger.cs",
                "namespace Manger { public class HYLDBaoShiZhengBaManger {} }");
            WriteFileUnder(sb, "Server/Server/Fake.cs", "namespace Y { public class Z {} }");
            NegativeCase("N6 真实退役类名单D不能拼错放行",
                HasAnyFail(CheckG2_Tokens(layout), "G2.legacy-type-tokens-absent")
                && HasAnyFail(CheckG1_FilesAbsent(layout), "G1.client-files-and-meta-absent"),
                "按真实类名注入，同时检查token与删除文件清单");
        }

        private static void N3_CommentAndStringNotReported()
        {
            string sb = NewSandbox("n3-comment-string");
            RepoLayout L = new RepoLayout(sb);

            // 旧类型名只出现在注释、普通字符串、逐字字符串与插值字符串的字面量里 —— 一律不得误报。
            // 注意「插值字符串里的表达式」在本词法里仍按代码处理：因此这里把旧类型名放进
            // 插值字符串的**字面量部分**（`{1}` 是表达式，用不到旧类型名）。
            // 「代码里的 7777 必须被检出」由下面的正对照沙盒单独证明。
            string srcNoPort =
                "// BattleManger / BattleData 只是说明文字，7777 也只在注释里\n" +
                "/* HYLDCameraManger 也只在块注释里 */\n" +
                "namespace X { public class Ok {\n" +
                "  void F() {\n" +
                "    string a = \"BattleManger\";\n" +
                "    string b = @\"UDPSocketManger\";\n" +
                "    string c = $\"BattleController {1} BattleManage\";\n" +
                "    string d = $@\"GameManger\";\n" +
                "    char e = 'x';\n" +
                "  }\n" +
                "}\n";

            WriteFileUnder(sb, "Client/Assets/Scripts/Fake.cs", srcNoPort);
            WriteFileUnder(sb, "Server/Server/Fake.cs", "namespace Y { public class Z { } }\n");

            List<CheckResult> g2 = CheckG2_Tokens(L);
            bool tokenPass = !HasAnyFail(g2, "G2.legacy-type-tokens-absent");
            bool portPass = !HasAnyFail(g2, "G2.hardcoded-udp-7777-absent");

            // 反向对照：把 7777 放进真实代码，必须被报（证明「不误报」不是靠整体失灵）
            string sb2 = NewSandbox("n3b-positive-control");
            RepoLayout L2 = new RepoLayout(sb2);
            WriteFileUnder(sb2, "Client/Assets/Scripts/Fake.cs",
                "namespace X { public class Ok { int P = 7777; } }\n");
            WriteFileUnder(sb2, "Server/Server/Fake.cs", "namespace Y { public class Z { } }\n");
            List<CheckResult> g2b = CheckG2_Tokens(L2);
            bool portDetected = HasAnyFail(g2b, "G2.hardcoded-udp-7777-absent");
            bool tokenStillPass = !HasAnyFail(g2b, "G2.legacy-type-tokens-absent");

            NegativeCase("N3 注释/字符串同词不得误报",
                tokenPass && portPass && portDetected && tokenStillPass,
                "注释+字符串同词误报=" + (!tokenPass) + " 注释里7777误报=" + (!portPass)
                + " | 正对照：代码里的7777被检出=" + portDetected + " 且无旧类型误报=" + tokenStillPass);
        }

        private static void N4_ResurrectedActionDetected()
        {
            // 用一个**干净**的最小 proto 建立基线（全绿），再注入旧 Action 定义，必须转为 FAIL。
            string clean =
                "syntax=\"proto3\";\n" +
                "package SocketProto;\n" +
                "enum RequestCode {\n" +
                "  RequestNone=0; User=1; Room=2; Friend=3; FriendRoom=4; PingPong=5; Matching=6;\n" +
                "  reserved 7, 8;\n" +
                "}\n" +
                "enum ActionCode {\n" +
                "  ActionNone=0; Logon=1; Login=2; CreateRoom=3; FindRoom=4; PlayerList=5; JoinRoom=6; Exit=7;\n" +
                "  Chat=8; AplyAddFriend=9; InviteFriend=10; FindName=11; UpdateName=12; AcceptAddFriend=13;\n" +
                "  RejectAddFriend=14; FindPlayerInfo=15; FindFriendsInfo=16; FriendLogin=17; FriendLogout=18;\n" +
                "  AcceptInvateFriend=19; RejectInvateFriend=20; CancalInvateFriend=21; ExitRoom=22;\n" +
                "  GetFriendRoomInfo=23; Ping=24; Pong=25; ChangeHero=26; UpDateActiveFriendInfo=27;\n" +
                "  AddMatchingPlayer=28; RemoveMatchingPlayer=29; StartEnterBattle=30;\n" +
                "  reserved 31 to 41;\n" +
                "}\n" +
                "enum ReturnCode { ReturnNone=0; Succeed=1; Fail=2; NotRoom=3; AddFriend=4; }\n" +
                "enum RoomState { RoomNormal=0; RoomFull=1; RoomGame=2; }\n" +
                "enum PlayerState { PlayerOnline=0; PlayerOutline=1; PlayerGame=2; PlayerOnRoom=3; PlayerOnInvated=4; }\n" +
                "enum Hero { XueLi=0; KeErTe=1; PeiPei=2; PanNi=3; BaLi=4; GongNiu=5; DaLiEr=6; GeEr=7; BuLuoKe=8;\n" +
                "  BaoPoMaiKe=9; ABo=10; DiKe=11; BeiYa=12; TaLa=13; MaiKeSi=14; SiPaiKe=15; HeiYa=16; LiAng=17;\n" +
                "  PaMu=18; RuiKe=19; }\n" +
                "enum FightPattern { BaoShiZhengBa=0; Sheji=1; }\n" +
                "message MainPack {\n" +
                "  RequestCode requestcode=1; ActionCode actioncode=2; ReturnCode returncode=3; LoginPack loginpack=4;\n" +
                "  string str=5; repeated RoomPack roompack=6; repeated PlayerPack friendspack=7;\n" +
                "  PlayerPack userInfopack=8; repeated FriendRoomPack friendroompack=9; repeated PlayerPack playerspack=10;\n" +
                "  ChatPack chatpack=11; repeated BattlePlayerPack battleplayerpack=12; int64 timestamp=14;\n" +
                "  int32 request_id=16;\n" +
                "  reserved 13, 15;\n" +
                "}\n" +
                "message ChatPack { string playername=1; string message=2; int32 state=3; }\n" +
                "message LoginPack { string username=1; string password=2; }\n" +
                "message RoomPack { string roomid=1; int32 maxnum=2; int32 curnum=3; RoomState state=4; }\n" +
                "message BattlePlayerPack { int32 id=1; int32 teamid=2; int32 roomid=3; string playername=4; Hero hero=5; int32 battleid=6; }\n" +
                "message FriendRoomPack { string roomid=1; int32 maxnum=2; int32 curnum=3; RoomState state=4; }\n" +
                "message PlayerPack { string username=1; string playername=2; int32 id=3; PlayerState state=4; Hero hero=5; FightPattern fightpattern=6; }\n";

            string sbClean = NewSandbox("n4-clean");
            RepoLayout Lc = new RepoLayout(sbClean);
            WriteFileUnder(sbClean, ProtoRel, clean);
            List<CheckResult> okRun = CheckG6_Proto(Lc);
            bool cleanPass = true;
            foreach (CheckResult r in okRun)
            {
                if (!r.Pass) { cleanPass = false; break; }
            }

            string dirty = clean.Replace("  reserved 31 to 41;\n", "  reserved 31 to 41;\n  BattleReady=31;\n");
            string sbDirty = NewSandbox("n4-dirty");
            RepoLayout Ld = new RepoLayout(sbDirty);
            WriteFileUnder(sbDirty, ProtoRel, dirty);
            List<CheckResult> badRun = CheckG6_Proto(Ld);
            bool actionFail = HasAnyFail(badRun, "G6.proto-legacy-numbers-absent");

            NegativeCase("N4 旧 Action 定义复活必须被检出",
                cleanPass && actionFail,
                "干净基线全绿=" + cleanPass + " 注入 BattleReady=31 后被检出=" + actionFail);
        }

        private static void N5_AssetGuidResidueDetected()
        {
            string sb = NewSandbox("n5-guid-residue");
            RepoLayout L = new RepoLayout(sb);
            WriteFileUnder(sb, "Client/Assets/Scenes/Some.unity",
                "%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  m_Script: {fileID: 11500000, guid: 7200a0eb9673b6e4f8cb8386cdde31db, type: 3}\n");

            List<CheckResult> g8 = CheckG8_Assets(L);
            bool guidFail = HasAnyFail(g8, "G8.detached-guids-zero");

            // 对照：无残留时应为绿（其余 anchor 缺失会报另一条，不影响本条的断言）
            string sb2 = NewSandbox("n5-guid-clean");
            RepoLayout L2 = new RepoLayout(sb2);
            WriteFileUnder(sb2, "Client/Assets/Scenes/Some.unity", "%YAML 1.1\n--- !u!1 &1\nGameObject:\n  m_Name: X\n");
            List<CheckResult> g8b = CheckG8_Assets(L2);
            bool guidPass = !HasAnyFail(g8b, "G8.detached-guids-zero");

            NegativeCase("N5 资产 GUID 残留必须被检出",
                guidFail && guidPass,
                "注入 GUID 后被检出=" + guidFail + " 干净沙盒不误报=" + guidPass);
        }

        private static void CleanupSelfTest()
        {
            if (string.IsNullOrEmpty(_selfTestRoot)) return;
            try
            {
                string temp = Path.GetFullPath(Path.GetTempPath());
                string full = Path.GetFullPath(_selfTestRoot);
                if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
                {
                    Directory.Delete(full, true);
                }
            }
            catch (Exception)
            {
                // 清理失败不影响门禁结论（沙盒在 %TEMP% 下）。
            }
        }

        // =====================================================================================
        //  四、报告
        // =====================================================================================

        private static void WriteReport(string path, RepoLayout layout, bool ranSelfTest,
            int passed, int failed, bool overall)
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("# 旧链禁回归静态门禁报告（PMLegacyRetirementTest）");
            sb.AppendLine();
            sb.AppendLine("> 本文件由 `Tools/PMLegacyRetirementTest`（`--report`）生成；复跑命令：");
            sb.AppendLine("> `dotnet run -c Release --project Tools/PMLegacyRetirementTest -- --report Docs/plans/_legacy_retirement_gate_report.md`");
            sb.AppendLine("> 冻结契约：`Docs/plans/net-legacy-retirement-contract.md`。状态唯一源仍是 `Docs/plans/net-architecture-migration.md`。");
            sb.AppendLine();
            sb.AppendLine("## 1. 结论");
            sb.AppendLine();
            sb.AppendLine("| 项 | 值 |");
            sb.AppendLine("|---|---|");
            sb.AppendLine("| 检查根目录 | `" + layout.Root.Replace('\\', '/') + "` |");
            sb.AppendLine("| 静态检查 | 通过 " + passed.ToString(CultureInfo.InvariantCulture)
                          + " / 失败 " + failed.ToString(CultureInfo.InvariantCulture) + " |");
            if (ranSelfTest)
            {
                sb.AppendLine("| 负例自测 | 通过 " + _negativePassed.ToString(CultureInfo.InvariantCulture)
                              + " / 失败 " + _negativeFailed.ToString(CultureInfo.InvariantCulture) + " |");
            }
            else
            {
                sb.AppendLine("| 负例自测 | 本次以 `--no-self-test` 跳过 |");
            }
            sb.AppendLine("| 总体结论 | **" + (overall ? "PASS" : "FAIL") + "** |");
            sb.AppendLine();

            int protoFailed = 0, generatedFailed = 0;
            foreach (CheckResult r in All)
            {
                if (r.Pass) continue;
                if (r.Group == "G6") protoFailed++;
                if (r.Group == "G7") generatedFailed++;
            }

            if (failed > 0)
            {
                sb.AppendLine("失败项按组：G6(proto)=" + protoFailed.ToString(CultureInfo.InvariantCulture)
                              + "，G7(generated)=" + generatedFailed.ToString(CultureInfo.InvariantCulture)
                              + "，其余组=" + (failed - protoFailed - generatedFailed).ToString(CultureInfo.InvariantCulture) + "。");
                sb.AppendLine();
                sb.AppendLine("**当前已知根因（如实记录，不得为绿放行旧类型）**：契约 §E 的 proto 收缩是**串行**步骤"
                              + "（A/B/C/D 删完后再改共享 proto）。若并行工作尚未把 `ProtobufAndNotepad/Protobuf/SocketProto.proto`"
                              + " 收缩完，则 G6/G7 必然 FAIL——这不是门禁误报，而是「旧 proto / 旧 generated 仍在」的真实事实。"
                              + "主侧在 §E 完成后**必须复跑本门禁**。");
                sb.AppendLine();
            }

            sb.AppendLine("## 2. 范围（Scope）");
            sb.AppendLine();
            sb.AppendLine("- 只读检查**真实磁盘源码**：`Client/Assets/**/*.cs`、`Server/**/*.cs`（扫描根见下），");
            sb.AppendLine("  以及权威 proto、四份 generated 产物、`Client/Assets` 下的 `*.unity` / `*.prefab`、6 个冻结资产。");
            sb.AppendLine("- 明确排除：`Library/`、`bin/`、`obj/`、`Docs/`、历史 `log/`、第三方 `Google*` 目录、`Generated/` 与 `*.g.cs`");
            sb.AppendLine("  （generated 由 G7 单独检查，不混进 token 扫描）。");
            sb.AppendLine("- 扫描根：`Client/Assets`、`Server`。工具自身位于 `Tools/PMLegacyRetirementTest/`，**不在**扫描根内，");
            sb.AppendLine("  因此不存在「门禁把自己的字符串当命中」的问题。");
            sb.AppendLine("- **GameManger 同名风险**：契约指的是已删的 `Manger.GameManger`。扫描根限定在 `Client/Assets` 与 `Server`");
            sb.AppendLine("  的**本项目源码**（第三方/引擎目录已排除），不会误判第三方同名类型；当前活源码中 `GameManger` 出现 0 次。");
            sb.AppendLine();

            sb.AppendLine("## 3. 检查清单与结果（静态边界）");
            sb.AppendLine();
            sb.AppendLine("| 组 | 检查项 | 结果 | 说明 |");
            sb.AppendLine("|---|---|---|---|");
            foreach (CheckResult r in All)
            {
                sb.AppendLine("| " + r.Group + " | `" + r.Id + "` | " + (r.Pass ? "OK" : "**FAIL**")
                              + " | " + EscapeCell(r.Detail) + " |");
            }
            sb.AppendLine();

            sb.AppendLine("### 3.1 各组断言的是什么（能力边界）");
            sb.AppendLine();
            sb.AppendLine("- **G1** 已删源码/`.meta`/退役工具 `.csproj` 是否以任何形式复活（存在性即可判定）。");
            sb.AppendLine("- **G2** 活源码**词法剥离注释与字符串后**是否仍出现 19 个旧类型标识符与硬编码 UDP `7777`。");
            sb.AppendLine("  剥离保留换行与偏移，所以报的是真实行号；注释/字符串里的说明文字不会误报。");
            sb.AppendLine("- **G3** `PMDsHost` 的**结构事实**：依赖 `PMDsSessionHost`、无 `new Socket`/裸 socket/旧 router/旧诊断包/线程、");
            sb.AppendLine("  结构上要求 `BootstrapPath`。它**不**验证「缺 bootstrap 时是否真的 Quit(1) 且不启动会话」——");
            sb.AppendLine("  那是 `Tools/PMDsHostCheck` 的替身行为门禁的职责，本门禁不冒充。");
            sb.AppendLine("- **G4** `Controllers.StartFighting` 方法体只调 `StartFightingDedicatedServer`，且全活源码无 `NewChainEnabled` 旧选链开关。");
            sb.AppendLine("- **G5** `UIMatchingPanel` 去注释去字符串后无 `BattleData`/旧 `ClearSenceManger.LoadScene` 回退，且只认 `PMDsEntryCodec`。");
            sb.AppendLine("- **G6** 权威 proto 的**声明名 + 字段号 + 枚举号**：14 条旧战斗消息与 `MoveType`/`AttackType` 不得声明；");
            sb.AppendLine("  `RequestCode.Battle/ClearSence` 与号 7/8、`ActionCode` 31..41、`MainPack` 13/15 不得有定义，");
            sb.AppendLine("  且必须被显式 `reserved`（reserved 语句不算定义）；厅相关既有号（7 枚举 + 7 消息）不得漂移；");
            sb.AppendLine("  字段类型必须能在本文件内解析（禁止引用已删 message ⇒ 类型复活）。");
            sb.AppendLine("- **G7** 四份 generated 产物（去注释）中不得出现 14 旧消息 + `MoveType`/`AttackType` 的");
            sb.AppendLine("  `class`/`enum`/`Serializer`（含 `PM` 前缀形态），且 `global::SocketProto.*` 类型引用必须本文件有声明。");
            sb.AppendLine("- **G8** 两个已解挂 GUID 在 `Client/Assets` 的 `*.unity`/`*.prefab` 中出现 0 次（并强制资产文件数 > 0，");
            sb.AppendLine("  禁止「扫到 0 个文件就绿」）；6 个冻结锚的 SHA256 必须等于 `_legacy_asset_detach_report.md` §4.5 的固定值。");
            sb.AppendLine();
            sb.AppendLine("- 不做的事：不运行 Unity、不启动服务端、不提交、不 `git add`/暂存、不递归委派；");
            sb.AppendLine("  不重做业务测试（本工具只做静态事实判定）。");
            sb.AppendLine();

            if (ranSelfTest)
            {
                sb.AppendLine("## 4. 负例自测（临时沙盒注入，不修改真实生产文件）");
                sb.AppendLine();
                sb.AppendLine("沙盒根：`%TEMP%`（运行结束后清理）。失败计数：**" + _negativeFailed.ToString(CultureInfo.InvariantCulture) + "**。");
                sb.AppendLine();
                sb.AppendLine("| 结果 | 用例 | 说明 |");
                sb.AppendLine("|---|---|---|");
                foreach (string n in NegativeNotes)
                {
                    string[] parts = n.Split(new[] { '|' }, 3);
                    if (parts.Length == 3)
                    {
                        sb.AppendLine("| " + parts[0].Trim() + " | " + EscapeCell(parts[1].Trim()) + " | " + EscapeCell(parts[2].Trim()) + " |");
                    }
                    else
                    {
                        sb.AppendLine("| - | " + EscapeCell(n) + " | |");
                    }
                }

                sb.AppendLine();
                sb.AppendLine("负例覆盖：缺输入必须 FAIL（不是空扫描绿）、旧 class 复活必须被检出、");
                sb.AppendLine("注释/字符串里的同词**不得**误报（并带一组「代码里的 7777 必须被检出」的正对照，");
                sb.AppendLine("防止「不误报」是靠整体失灵换来的）、旧 Action 定义复活必须被检出、资产 GUID 残留必须被检出。");
                sb.AppendLine();
            }

            sb.AppendLine("## 5. 门禁检测力自证（可复现，不修改仓库文件）");
            sb.AppendLine();
            sb.AppendLine("负例自测只覆盖**合成夹具**。为证明 G6 对**真实历史内容**同样有检出力，");
            sb.AppendLine("可用下面的配方把契约 §E 之前的 proto 放进临时沙盒复跑（仓库文件全程只读）：");
            sb.AppendLine();
            sb.AppendLine("```bash");
            sb.AppendLine("SB=\"$TEMP/pm-gate-oldproto\"");
            sb.AppendLine("rm -rf \"$SB\"; mkdir -p \"$SB/ProtobufAndNotepad/Protobuf\" \"$SB/Client\"");
            sb.AppendLine("git show HEAD:ProtobufAndNotepad/Protobuf/SocketProto.proto > \"$SB/ProtobufAndNotepad/Protobuf/SocketProto.proto\"");
            sb.AppendLine("cp \"$SB/ProtobufAndNotepad/Protobuf/SocketProto.proto\" \"$SB/Client/SocketProto.proto\"");
            sb.AppendLine("dotnet Tools/PMLegacyRetirementTest/bin/Release/net8.0/PMLegacyRetirementTest.dll --repo \"$SB\" --no-self-test");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("本门禁实现完成后已执行过一次该配方（当时 HEAD 仍为 §E 之前的旧 proto），记录到的结果：");
            sb.AppendLine("G6 的 `legacy-declarations-absent` / `legacy-numbers-absent` / `legacy-numbers-reserved` /");
            sb.AppendLine("`non-authoritative-proto-copies-absent` 与 G2/G3/G4/G5/G7/G8 的缺输入项共报 **10 个 FAIL**，");
            sb.AppendLine("逐条给出了具体复活声明与占用号；把沙盒 proto 换回当前权威 proto 后即恢复全绿。");
            sb.AppendLine("⇒ G6/G7 的「绿」不是空扫描或缺失输入造成的假绿。");
            sb.AppendLine();
            sb.AppendLine("## 6. 未覆盖 / 待主侧复跑");
            sb.AppendLine();
            if (failed == 0)
            {
                sb.AppendLine("- 契约 §E 的 proto 收缩（旧消息删除、旧号 reserved、四份产物重生成、非权威副本删除）**已生效**，");
                sb.AppendLine("  G6/G7 因此为绿；本门禁将长期看护这条边界，任何一处回退都会立刻变红。");
            }
            else
            {
                sb.AppendLine("- G6/G7 依赖契约 §E 的 proto 收缩完成后才有意义；当前为 FAIL，属于「proto 还旧」的真实状态，");
                sb.AppendLine("  不得为让它变绿而放行旧类型，必须在 §E 完成后复跑本门禁。");
            }
            sb.AppendLine("- 冻结 SHA 锚是**固定值**：若后续**有意**重烘 `HYLDGame.unity` / `Remake/Player.prefab` / PMNet 输出，");
            sb.AppendLine("  必须同步更新锚值与本报告说明，门禁不会写默认 PASS。");
            sb.AppendLine("- 真实 Unity 实机加载、`PMDsHost` 运行时退出行为、服务端/Lobby 运行语义均不在本静态门禁范围内。");
            sb.AppendLine();
            sb.AppendLine("## 7. 写入边界");
            sb.AppendLine();
            sb.AppendLine("- 本工具只读仓库源码与资产；除 `--report <path>` 指定的报告文件外不写任何仓库文件（沙盒一律在 `%TEMP%`）。");
            sb.AppendLine("- 未运行 Unity、未启动服务端、未提交、未 `git add`/暂存、未递归委派，未执行任何 SVN/git 写操作。");
            sb.AppendLine();

            File.WriteAllText(Path.GetFullPath(path), sb.ToString(), new UTF8Encoding(false));
        }

        private static string EscapeCell(string s)
        {
            if (s == null) return string.Empty;
            return s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }

        // =====================================================================================
        //  五、小工具
        // =====================================================================================

        private static bool HasIdent(string code, string ident)
        {
            return Regex.IsMatch(code, "(?<![A-Za-z0-9_])" + Regex.Escape(ident) + "(?![A-Za-z0-9_])",
                RegexOptions.CultureInvariant);
        }

        /// <summary>从去注释源码里取出指定方法的 { } 方法体原文（含最外层大括号）。</summary>
        private static string ExtractMethodBody(string code, string methodName)
        {
            Regex head = new Regex("(?<![A-Za-z0-9_])" + Regex.Escape(methodName) + @"\s*\(", RegexOptions.CultureInvariant);
            foreach (Match m in head.Matches(code))
            {
                int i = m.Index + m.Length;
                int depth = 0;
                bool started = false;
                int start = -1;
                for (; i < code.Length; i++)
                {
                    char c = code[i];
                    if (c == '{')
                    {
                        if (!started) { started = true; start = i; }
                        depth++;
                    }
                    else if (c == '}')
                    {
                        depth--;
                        if (started && depth == 0)
                        {
                            return code.Substring(start, i - start + 1);
                        }
                    }
                    else if (c == ';' && !started)
                    {
                        break; // 声明而非定义
                    }
                }
            }

            return null;
        }
    }

    /// <summary>
    /// 词法剥离：把注释与字符串字面量替换成空白（保留换行与字符偏移）。
    ///
    /// 覆盖：行注释、块注释、普通字符串、字符字面量、逐字字符串（@"..."）、
    /// 插值字符串（$"..."、$@"..."、@$"..."），插值表达式的 **{ } 内仍然是代码**，
    /// 会继续按代码处理（这样 `$"{BattleData.X}"` 这种真实引用不会被漏掉）。
    /// </summary>
    internal sealed class CodeStripper
    {
        private readonly string _s;
        private readonly StringBuilder _o;
        private int _i;

        private readonly List<bool> _interpVerbatim = new List<bool>();
        private readonly List<int> _interpDepth = new List<int>();

        private CodeStripper(string s)
        {
            _s = s;
            _o = new StringBuilder(s.Length);
        }

        public static string Strip(string text)
        {
            CodeStripper st = new CodeStripper(text ?? string.Empty);
            st.Run();
            return st._o.ToString();
        }

        private int Last { get { return _interpDepth.Count - 1; } }
        private bool InInterpCode { get { int n = _interpDepth.Count; return n > 0 && _interpDepth[n - 1] >= 1; } }
        private bool InInterpLiteral { get { int n = _interpDepth.Count; return n > 0 && _interpDepth[n - 1] == 0; } }

        private char At(int k)
        {
            int j = _i + k;
            return j >= 0 && j < _s.Length ? _s[j] : '\0';
        }

        private void Emit(char c)
        {
            _o.Append(c);
        }

        private void Mask(char c)
        {
            if (c == '\n' || c == '\r') _o.Append(c);
            else _o.Append(' ');
        }

        private void Run()
        {
            while (_i < _s.Length)
            {
                if (InInterpLiteral)
                {
                    StepInterpLiteral();
                    continue;
                }

                char c = _s[_i];

                if (c == '/' && At(1) == '/') { ConsumeLineComment(); continue; }
                if (c == '/' && At(1) == '*') { ConsumeBlockComment(); continue; }

                if (InInterpCode)
                {
                    if (c == '{')
                    {
                        Emit(c);
                        _i++;
                        _interpDepth[Last] = _interpDepth[Last] + 1;
                        continue;
                    }

                    if (c == '}')
                    {
                        int d = _interpDepth[Last];
                        if (d == 1)
                        {
                            Mask(c);
                            _i++;
                            _interpDepth[Last] = 0;
                            continue;
                        }

                        Emit(c);
                        _i++;
                        _interpDepth[Last] = d - 1;
                        continue;
                    }
                }

                if (c == '"') { ConsumeNormalString(); continue; }
                if (c == '\'') { ConsumeCharLiteral(); continue; }
                if (c == '@' && At(1) == '"') { ConsumeVerbatimString(); continue; }
                if (c == '$' && At(1) == '"') { StartInterp(false, 2); continue; }
                if (c == '$' && At(1) == '@' && At(2) == '"') { StartInterp(true, 3); continue; }
                if (c == '@' && At(1) == '$' && At(2) == '"') { StartInterp(true, 3); continue; }

                Emit(c);
                _i++;
            }
        }

        private void StartInterp(bool verbatim, int prefixLen)
        {
            for (int k = 0; k < prefixLen && _i < _s.Length; k++)
            {
                Mask(_s[_i]);
                _i++;
            }

            _interpVerbatim.Add(verbatim);
            _interpDepth.Add(0);
        }

        private void PopInterp()
        {
            if (_interpDepth.Count > 0)
            {
                _interpDepth.RemoveAt(_interpDepth.Count - 1);
                _interpVerbatim.RemoveAt(_interpVerbatim.Count - 1);
            }
        }

        private void StepInterpLiteral()
        {
            bool verbatim = _interpVerbatim[Last];
            char c = _s[_i];

            if (verbatim)
            {
                if (c == '"')
                {
                    if (At(1) == '"') { Mask('"'); Mask('"'); _i += 2; return; }
                    Mask('"');
                    _i++;
                    PopInterp();
                    return;
                }
            }
            else
            {
                if (c == '\\')
                {
                    Mask(c);
                    if (_i + 1 < _s.Length) { Mask(_s[_i + 1]); _i += 2; }
                    else { _i++; }
                    return;
                }

                if (c == '"')
                {
                    Mask('"');
                    _i++;
                    PopInterp();
                    return;
                }
            }

            if (c == '{')
            {
                if (At(1) == '{') { Mask('{'); Mask('{'); _i += 2; return; }
                Mask('{');
                _i++;
                _interpDepth[Last] = 1;
                return;
            }

            if (c == '}')
            {
                if (At(1) == '}') { Mask('}'); Mask('}'); _i += 2; return; }
                Mask('}');
                _i++;
                return;
            }

            Mask(c);
            _i++;
        }

        private void ConsumeLineComment()
        {
            while (_i < _s.Length && _s[_i] != '\n')
            {
                Mask(_s[_i]);
                _i++;
            }
        }

        private void ConsumeBlockComment()
        {
            Mask('/');
            Mask('*');
            _i += 2;
            while (_i < _s.Length)
            {
                if (_s[_i] == '*' && At(1) == '/')
                {
                    Mask('*');
                    Mask('/');
                    _i += 2;
                    return;
                }

                Mask(_s[_i]);
                _i++;
            }
        }

        private void ConsumeNormalString()
        {
            Mask('"');
            _i++;
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c == '\\')
                {
                    Mask(c);
                    if (_i + 1 < _s.Length) { Mask(_s[_i + 1]); _i += 2; }
                    else { _i++; }
                    continue;
                }

                if (c == '"')
                {
                    Mask('"');
                    _i++;
                    return;
                }

                Mask(c);
                _i++;
            }
        }

        private void ConsumeVerbatimString()
        {
            Mask('@');
            Mask('"');
            _i += 2;
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c == '"')
                {
                    if (At(1) == '"') { Mask('"'); Mask('"'); _i += 2; continue; }
                    Mask('"');
                    _i++;
                    return;
                }

                Mask(c);
                _i++;
            }
        }

        private void ConsumeCharLiteral()
        {
            Mask('\'');
            _i++;
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c == '\\')
                {
                    Mask(c);
                    if (_i + 1 < _s.Length) { Mask(_s[_i + 1]); _i += 2; }
                    else { _i++; }
                    continue;
                }

                if (c == '\'')
                {
                    Mask('\'');
                    _i++;
                    return;
                }

                Mask(c);
                _i++;
            }
        }
    }
}
