using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using PMNet;
using PMNet.Generated;
using SocketProto;

namespace PMNetVerify
{
    /// <summary>
    /// 计划 E1（旧战斗链退役）之后的协议门禁与逐字节等价校验。
    ///
    /// 为什么重写而不是删掉：
    /// 旧版程序以「旧战斗热路径」（BattleInfo / ClientMove / MoveAckResult / HitEvent …）为主要对照场景；
    /// 这些消息已随 E1 从权威 proto 删除，继续断言它们等于保留死代码，也等于对已经不存在的覆盖说谎。
    /// 本版把对照对象换成**协议里真实剩下的那 7 个大厅 DTO**，并且消息/枚举集合、
    /// 每个 message 的字段号与 repeated 标记、每个 enum 的成员数值，**全部在运行时从权威 proto 读出来**，
    /// 不写死数量。任何「悄悄加回一个旧类型」「重排字段号」「挪动枚举数值」都会在这里失败。
    ///
    /// 三重判定（Google.Protobuf 永远作为独立 oracle，不与 PMNet 自比较）：
    ///   1. 同一组取值，protoc 生成的 <c>ToByteArray()</c> 与 <c>PMNetWriter</c> 产物必须逐字节相同；
    ///   2. 用 protoc 的 Parser 解析 PMNet 写出的字节，重编码后仍逐字节相同；
    ///   3. 用 PMNet 的 ParseFrom 解析 protoc 写出的字节，重编码后仍逐字节相同。
    ///
    /// 覆盖边界（诚实口径）：
    /// 退役的旧战斗/浮点场景（ClientMove 的 float 三元组、MoveAckResult 的修正位置/速度、
    /// HitEvent 的命中坐标等）已随 E1 删除，本程序**不再断言它们**，也不以「仍被覆盖」表述。
    /// 剩余 7 个大厅 DTO 全部不含浮点字段，因此本轮没有 float 覆盖面，这是删除的直接后果，不是遗漏。
    ///
    /// 退出码：0 全部通过；1 存在失败；2 找不到权威 proto（环境不完整）。
    /// </summary>
    internal static class Program
    {
        private const string ProtoRelativePath = "ProtobufAndNotepad/Protobuf/SocketProto.proto";
        private const string PmNetGeneratedRelativePath = "Client/Assets/Scripts/PMNet/Generated/SocketProto.PMNet.g.cs";

        private static int _checks;
        private static int _failures;
        private static readonly HashSet<string> _exercisedMessages = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> _exercisedEnums = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<Type, MethodInfo> _pmWriteCache = new Dictionary<Type, MethodInfo>();
        private static readonly Dictionary<Type, MethodInfo> _pmParseCache = new Dictionary<Type, MethodInfo>();

        private static int Main()
        {
            Console.WriteLine("== PMNet E1 退役后：剩余大厅 DTO 的独立 protoc 字节 oracle ==");
            Console.WriteLine("对照基准: Google.Protobuf " + typeof(IMessage).Assembly.GetName().Version);
            Console.WriteLine("被校验对象: PMNet 生成序列化器（Tools/PMNetGen 产物，Unity 2019.4 语言面）");
            Console.WriteLine();

            string repoRoot;
            string protoPath;
            if (!TryResolveProtoPath(out repoRoot, out protoPath))
            {
                Console.WriteLine("[FATAL] 未找到权威 proto: " + ProtoRelativePath);
                Console.WriteLine("[FATAL] 查找起点: " + AppContext.BaseDirectory + "（向上逐级）");
                return 2;
            }

            Console.WriteLine("仓库根    : " + repoRoot);
            Console.WriteLine("权威 proto: " + protoPath);
            Console.WriteLine();

            ProtoFileSchema schema = ProtoFileSchema.Parse(File.ReadAllText(protoPath, new UTF8Encoding(false)));

            PrintInventory(schema);

            SchemaGate(schema);
            EmptyAllMessagesScenario(schema);
            MainPackFullScenario();
            DirectSubMessageScenarios();
            StringEdgeScenarios();
            ScalarEdgeScenarios();
            RepeatedScalingScenarios();
            UnknownFieldSkipScenario();
            TruncationParityScenario();
            ArtifactGate(repoRoot);
            CoverageGate(schema);

            Console.WriteLine();
            Console.WriteLine("== 汇总: " + _checks + " 项检查, " + _failures + " 项失败 ==");
            return _failures == 0 ? 0 : 1;
        }

        // ================= A. 权威 proto 结构门禁 =================

        private static readonly string[] RetiredTypeNames = new string[]
        {
            // 旧战斗消息
            "BattleInfo", "BattleNetSimConfig", "BattleClientInput", "BattleServerUpdate",
            "BattleFrame", "PlayerFrameInput", "ClientAttack", "ServerAttack", "ClientMove",
            "MoveAckResult", "HitEvent", "AttackAck", "AuthoritativePlayerState",
            // 旧枚举
            "MoveType", "AttackType",
            // 孤立房间包（活源码零引用）
            "BattleRoomPack",
        };

        private static readonly string[] RetiredActionNames = new string[]
        {
            "BattleReady", "BattleStart", "BattlePushDowmAllFrameOpeartions", "BattlePushDowmPlayerOpeartions",
            "ClientSendClearSenceReady", "AllClearSenceReady", "ClientSendGameOver", "BattlePushDowmGameOver",
            "BattleReview", "BattlePushDownHitEvents", "BattleSetNetSimConfig",
        };

        private static void SchemaGate(ProtoFileSchema schema)
        {
            Section("A. 权威 proto 结构门禁（退役类型缺席 / 号码冻结 / 幸存字段号稳定）");

            HashSet<string> messageNames = schema.MessageNames();
            HashSet<string> enumNames = schema.EnumNames();

            for (int i = 0; i < RetiredTypeNames.Length; i++)
            {
                string retired = RetiredTypeNames[i];
                CheckCondition("proto 已不含退役类型 " + retired,
                    !messageNames.Contains(retired) && !enumNames.Contains(retired),
                    "仍存在同名声明");
            }

            ProtoEnumType requestCode = schema.FindEnum("RequestCode");
            ProtoEnumType actionCode = schema.FindEnum("ActionCode");
            ProtoMessage mainPack = schema.FindMessage("MainPack");

            CheckCondition("RequestCode 声明存在", requestCode != null, "缺 RequestCode");
            CheckCondition("ActionCode 声明存在", actionCode != null, "缺 ActionCode");
            CheckCondition("MainPack 声明存在", mainPack != null, "缺 MainPack");
            if (requestCode == null || actionCode == null || mainPack == null)
            {
                return;
            }

            CheckCondition("RequestCode 冻结号码 7", ReservedHasNumber(requestCode.Reserved, 7), ReservedText(requestCode.Reserved));
            CheckCondition("RequestCode 冻结号码 8", ReservedHasNumber(requestCode.Reserved, 8), ReservedText(requestCode.Reserved));
            CheckCondition("RequestCode 冻结名 Battle", ReservedHasName(requestCode.Reserved, "Battle"), ReservedText(requestCode.Reserved));
            CheckCondition("RequestCode 冻结名 ClearSence", ReservedHasName(requestCode.Reserved, "ClearSence"), ReservedText(requestCode.Reserved));

            bool allActionsReserved = true;
            for (int v = 31; v <= 41; v++)
            {
                if (!ReservedHasNumber(actionCode.Reserved, v))
                {
                    allActionsReserved = false;
                    break;
                }
            }

            CheckCondition("ActionCode 冻结号码 31..41", allActionsReserved, ReservedText(actionCode.Reserved));
            for (int i = 0; i < RetiredActionNames.Length; i++)
            {
                string name = RetiredActionNames[i];
                CheckCondition("ActionCode 冻结名 " + name, ReservedHasName(actionCode.Reserved, name), ReservedText(actionCode.Reserved));
                CheckCondition("ActionCode 已无成员 " + name, !actionCode.HasValueName(name), "仍存在成员");
            }

            CheckCondition("MainPack 冻结号码 13", ReservedHasNumber(mainPack.Reserved, 13), ReservedText(mainPack.Reserved));
            CheckCondition("MainPack 冻结号码 15", ReservedHasNumber(mainPack.Reserved, 15), ReservedText(mainPack.Reserved));
            CheckCondition("MainPack 冻结名 battleInfo", ReservedHasName(mainPack.Reserved, "battleInfo"), ReservedText(mainPack.Reserved));
            CheckCondition("MainPack 冻结名 battle_net_sim_config", ReservedHasName(mainPack.Reserved, "battle_net_sim_config"), ReservedText(mainPack.Reserved));

            // 幸存枚举：值域连续且上界正确（RequestCode 上界 6、ActionCode 上界 30）
            CheckCondition("RequestCode 幸存成员恰为 0..6",
                requestCode.MaxValue() == 6 && requestCode.ValueNumbers.Count == 7,
                "max=" + requestCode.MaxValue() + " count=" + requestCode.ValueNumbers.Count);
            CheckCondition("ActionCode 幸存成员恰为 0..30",
                actionCode.MaxValue() == 30 && actionCode.ValueNumbers.Count == 31,
                "max=" + actionCode.MaxValue() + " count=" + actionCode.ValueNumbers.Count);
            CheckCondition("ActionCode.StartEnterBattle = 30", actionCode.ValueOf("StartEnterBattle") == 30, "值漂移");
            CheckCondition("ActionCode 未冻结幸存号码 30", !ReservedHasNumber(actionCode.Reserved, 30), ReservedText(actionCode.Reserved));

            GeneratedGate(schema);
        }

        /// <summary>
        /// 反射对照：PMNet 生成产物必须与权威 proto 一一对应，且字段号/名字/repeated/线格式全部一致。
        /// 这是「不得重排幸存 field / enum 数字」的机器可判据 —— 不依赖任何人肉对比。
        /// </summary>
        private static void GeneratedGate(ProtoFileSchema schema)
        {
            Assembly assembly = typeof(PMMainPack).Assembly;
            const string ns = "PMNet.Generated";

            HashSet<string> generatedMessages = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> generatedEnums = new HashSet<string>(StringComparer.Ordinal);

            Type[] types = assembly.GetTypes();
            for (int i = 0; i < types.Length; i++)
            {
                Type t = types[i];
                if (t.Namespace != ns || t.IsNested)
                {
                    continue;
                }

                if (t.IsEnum && t.Name.StartsWith("PM", StringComparison.Ordinal))
                {
                    generatedEnums.Add(t.Name.Substring(2));
                    continue;
                }

                if (t.IsClass && t.Name.StartsWith("PM", StringComparison.Ordinal)
                    && t.Name.EndsWith("Serializer", StringComparison.Ordinal))
                {
                    generatedMessages.Add(t.Name.Substring(2, t.Name.Length - 2 - "Serializer".Length));
                }
            }

            CheckCondition("生成消息集合与 proto 完全一致",
                generatedMessages.SetEquals(schema.MessageNames()),
                "proto=[" + Join(schema.MessageNames()) + "] generated=[" + Join(generatedMessages) + "]");
            CheckCondition("生成枚举集合与 proto 完全一致",
                generatedEnums.SetEquals(schema.EnumNames()),
                "proto=[" + Join(schema.EnumNames()) + "] generated=[" + Join(generatedEnums) + "]");

            // ---- 枚举成员数值 ----
            for (int i = 0; i < schema.Enums.Count; i++)
            {
                ProtoEnumType pe = schema.Enums[i];
                Type et = assembly.GetType(ns + ".PM" + pe.Name, false);
                CheckCondition("生成枚举类型存在 PM" + pe.Name, et != null, "缺失");
                if (et == null)
                {
                    continue;
                }

                Dictionary<string, int> members = new Dictionary<string, int>(StringComparer.Ordinal);
                FieldInfo[] fields = et.GetFields(BindingFlags.Public | BindingFlags.Static);
                for (int f = 0; f < fields.Length; f++)
                {
                    members[fields[f].Name] = Convert.ToInt32(fields[f].GetValue(null));
                }

                bool sameCount = members.Count == pe.ValueNames.Count;
                bool sameContent = sameCount;
                if (sameCount)
                {
                    for (int v = 0; v < pe.ValueNames.Count; v++)
                    {
                        int number;
                        if (!members.TryGetValue(pe.ValueNames[v], out number) || number != pe.ValueNumbers[v])
                        {
                            sameContent = false;
                            break;
                        }
                    }
                }

                CheckCondition("枚举 " + pe.Name + " 成员名/数值与 proto 一致（" + pe.ValueNames.Count + " 项）",
                    sameContent, "proto=" + pe.Describe() + " generated=" + DescribeMap(members));
                _exercisedEnums.Add(pe.Name);
            }

            // ---- 消息字段号 / 名字 / repeated / 线格式 ----
            for (int i = 0; i < schema.Messages.Count; i++)
            {
                ProtoMessage pm = schema.Messages[i];
                Type serializer = assembly.GetType(ns + ".PM" + pm.Name + "Serializer", false);
                CheckCondition("生成序列化器存在 PM" + pm.Name + "Serializer", serializer != null, "缺失");
                if (serializer == null)
                {
                    continue;
                }

                FieldInfo descField = serializer.GetField("Fields", BindingFlags.Public | BindingFlags.Static);
                PMNetFieldDesc[] descs = descField == null ? null : descField.GetValue(null) as PMNetFieldDesc[];
                CheckCondition("描述符表存在 " + pm.Name + ".Fields", descs != null, "缺失");
                if (descs == null)
                {
                    continue;
                }

                CheckCondition("字段数量一致 " + pm.Name + "（" + pm.Fields.Count + "）",
                    descs.Length == pm.Fields.Count, "proto=" + pm.Fields.Count + " generated=" + descs.Length);

                Dictionary<int, PMNetFieldDesc> byNumber = new Dictionary<int, PMNetFieldDesc>();
                for (int d = 0; d < descs.Length; d++)
                {
                    byNumber[descs[d].Number] = descs[d];
                    CheckCondition(pm.Name + " 不含被冻结字段号 " + descs[d].Number,
                        !ReservedHasNumber(pm.Reserved, descs[d].Number), "reserved 号码被复用");
                }

                for (int f = 0; f < pm.Fields.Count; f++)
                {
                    ProtoField pf = pm.Fields[f];
                    PMNetFieldDesc desc;
                    bool exists = byNumber.TryGetValue(pf.Number, out desc);
                    CheckCondition(pm.Name + "." + pf.Name + " 字段号 " + pf.Number + " 存在且数值稳定",
                        exists, "生成描述符缺该号码");
                    if (!exists)
                    {
                        continue;
                    }

                    CheckCondition(pm.Name + " " + pf.Number + " 名字一致",
                        desc.Name == pf.Name, "proto=" + pf.Name + " generated=" + desc.Name);
                    CheckCondition(pm.Name + " " + pf.Name + " repeated 标记一致",
                        desc.Repeated == pf.Repeated, "proto=" + pf.Repeated + " generated=" + desc.Repeated);

                    if (pf.Repeated && schema.FindMessage(pf.TypeName) == null)
                    {
                        CheckCondition(pm.Name + "." + pf.Name + " 为 repeated message（本门禁只支持该形态）",
                            false, "出现 packed repeated 标量，需要扩展本门禁");
                        continue;
                    }

                    PMWireType expected = WireTypeOf(pf, schema);
                    CheckCondition(pm.Name + "." + pf.Name + " 线格式一致",
                        desc.WireType == expected, "proto=" + expected + " generated=" + desc.WireType);
                }
            }
        }

        private static PMWireType WireTypeOf(ProtoField field, ProtoFileSchema schema)
        {
            if (field.Repeated)
            {
                return PMWireType.LengthDelimited;
            }

            string t = field.TypeName;
            if (t == "string" || t == "bytes")
            {
                return PMWireType.LengthDelimited;
            }

            if (t == "float")
            {
                return PMWireType.Fixed32;
            }

            if (t == "double")
            {
                return PMWireType.Fixed64;
            }

            if (schema.FindMessage(t) != null)
            {
                return PMWireType.LengthDelimited;
            }

            return PMWireType.Varint;
        }

        // ================= B. 空消息（集合由 proto 实时枚举） =================

        private static void EmptyAllMessagesScenario(ProtoFileSchema schema)
        {
            Section("B. 空消息：proto3 全默认必须产出 0 字节（消息集合实时来自 proto，不预设数量）");

            string googleNs = typeof(MainPack).Namespace;
            Assembly assembly = typeof(PMMainPack).Assembly;

            for (int i = 0; i < schema.Messages.Count; i++)
            {
                ProtoMessage pm = schema.Messages[i];
                Type googleType = assembly.GetType(googleNs + "." + pm.Name, false);
                Type pmType = assembly.GetType("PMNet.Generated.PM" + pm.Name, false);

                CheckCondition("protoc 侧类型存在 " + pm.Name, googleType != null, "缺失");
                CheckCondition("PMNet 侧类型存在 PM" + pm.Name, pmType != null, "缺失");
                if (googleType == null || pmType == null)
                {
                    continue;
                }

                object google = Activator.CreateInstance(googleType);
                object pmNet = Activator.CreateInstance(pmType);
                VerifyReflective("空 " + pm.Name, pm.Name, googleType, google, pmType, pmNet);
            }
        }

        // ================= C. MainPack 全字段嵌套 =================

        private static void MainPackFullScenario()
        {
            Section("C. MainPack 全字段嵌套（全部幸存字段 + 全部子消息）");

            MainPack g = new MainPack();
            g.Requestcode = RequestCode.Matching;
            g.Actioncode = ActionCode.StartEnterBattle;
            g.Returncode = ReturnCode.Succeed;
            g.Loginpack = new LoginPack { Username = "player_one", Password = "p@ssw0rd" };
            g.Str = "进入大厅：等待匹配";
            g.Roompack.Add(new RoomPack { Roomid = "room-1", Maxnum = 3, Curnum = 2, State = RoomState.RoomNormal });
            g.Roompack.Add(new RoomPack { Roomid = "room-2", Maxnum = 6, Curnum = 6, State = RoomState.RoomFull });
            g.Friendspack.Add(MakePlayerPack("f1", "好友甲", 11, PlayerState.PlayerOnline, Hero.XueLi, FightPattern.BaoShiZhengBa));
            g.UserInfopack = MakePlayerPack("me", "自己", 9, PlayerState.PlayerOnRoom, Hero.BeiYa, FightPattern.Sheji);
            g.Friendroompack.Add(new FriendRoomPack { Roomid = "fr-1", Maxnum = 3, Curnum = 1, State = RoomState.RoomNormal });
            g.Friendroompack.Add(new FriendRoomPack { Roomid = "fr-2", Maxnum = 6, Curnum = 3, State = RoomState.RoomGame });
            g.Playerspack.Add(MakePlayerPack("u1", "阿一", 1, PlayerState.PlayerOnline, Hero.XueLi, FightPattern.BaoShiZhengBa));
            g.Playerspack.Add(MakePlayerPack("u2", "阿二", 2, PlayerState.PlayerGame, Hero.KeErTe, FightPattern.Sheji));
            g.Playerspack.Add(MakePlayerPack("u3", "阿三", 3, PlayerState.PlayerOnRoom, Hero.PeiPei, FightPattern.BaoShiZhengBa));
            g.Chatpack = new ChatPack { Playername = "阿一", Message = "hello 世界", State = 7 };
            for (int i = 0; i < 6; i++)
            {
                g.Battleplayerpack.Add(new BattlePlayerPack
                {
                    Id = 100 + i,
                    Teamid = i % 2,
                    Roomid = 1,
                    Playername = "玩家" + i,
                    Hero = (Hero)(i % 20),
                    Battleid = i,
                });
            }

            g.Timestamp = 1700000000123L;
            g.RequestId = 20260918;

            PMMainPack p = new PMMainPack();
            p.Requestcode = PMRequestCode.Matching;
            p.Actioncode = PMActionCode.StartEnterBattle;
            p.Returncode = PMReturnCode.Succeed;
            p.Loginpack = new PMLoginPack { Username = "player_one", Password = "p@ssw0rd" };
            p.Str = "进入大厅：等待匹配";
            p.Roompack.Add(new PMRoomPack { Roomid = "room-1", Maxnum = 3, Curnum = 2, State = PMRoomState.RoomNormal });
            p.Roompack.Add(new PMRoomPack { Roomid = "room-2", Maxnum = 6, Curnum = 6, State = PMRoomState.RoomFull });
            p.Friendspack.Add(MakePmPlayerPack("f1", "好友甲", 11, PMPlayerState.PlayerOnline, PMHero.XueLi, PMFightPattern.BaoShiZhengBa));
            p.UserInfopack = MakePmPlayerPack("me", "自己", 9, PMPlayerState.PlayerOnRoom, PMHero.BeiYa, PMFightPattern.Sheji);
            p.Friendroompack.Add(new PMFriendRoomPack { Roomid = "fr-1", Maxnum = 3, Curnum = 1, State = PMRoomState.RoomNormal });
            p.Friendroompack.Add(new PMFriendRoomPack { Roomid = "fr-2", Maxnum = 6, Curnum = 3, State = PMRoomState.RoomGame });
            p.Playerspack.Add(MakePmPlayerPack("u1", "阿一", 1, PMPlayerState.PlayerOnline, PMHero.XueLi, PMFightPattern.BaoShiZhengBa));
            p.Playerspack.Add(MakePmPlayerPack("u2", "阿二", 2, PMPlayerState.PlayerGame, PMHero.KeErTe, PMFightPattern.Sheji));
            p.Playerspack.Add(MakePmPlayerPack("u3", "阿三", 3, PMPlayerState.PlayerOnRoom, PMHero.PeiPei, PMFightPattern.BaoShiZhengBa));
            p.Chatpack = new PMChatPack { Playername = "阿一", Message = "hello 世界", State = 7 };
            for (int i = 0; i < 6; i++)
            {
                p.Battleplayerpack.Add(new PMBattlePlayerPack
                {
                    Id = 100 + i,
                    Teamid = i % 2,
                    Roomid = 1,
                    Playername = "玩家" + i,
                    Hero = (PMHero)(i % 20),
                    Battleid = i,
                });
            }

            p.Timestamp = 1700000000123L;
            p.RequestId = 20260918;

            VerifyPair("大厅 MainPack 全字段嵌套", "MainPack", g, p);
        }

        // ================= D. 子消息直测 =================

        private static void DirectSubMessageScenarios()
        {
            Section("D. 子消息直测（protoc 侧与 PMNet 侧同构取值）");

            VerifyPair("ChatPack", "ChatPack",
                new ChatPack { Playername = "阿一", Message = "上号吗", State = 3 },
                new PMChatPack { Playername = "阿一", Message = "上号吗", State = 3 });

            VerifyPair("LoginPack", "LoginPack",
                new LoginPack { Username = "player_one", Password = "p@ssw0rd" },
                new PMLoginPack { Username = "player_one", Password = "p@ssw0rd" });

            VerifyPair("RoomPack", "RoomPack",
                new RoomPack { Roomid = "room-42", Maxnum = 6, Curnum = 4, State = RoomState.RoomGame },
                new PMRoomPack { Roomid = "room-42", Maxnum = 6, Curnum = 4, State = PMRoomState.RoomGame });

            VerifyPair("BattlePlayerPack", "BattlePlayerPack",
                new BattlePlayerPack { Id = 314, Teamid = 1, Roomid = 7, Playername = "战斗名册甲", Hero = Hero.TaLa, Battleid = 21 },
                new PMBattlePlayerPack { Id = 314, Teamid = 1, Roomid = 7, Playername = "战斗名册甲", Hero = PMHero.TaLa, Battleid = 21 });

            VerifyPair("FriendRoomPack", "FriendRoomPack",
                new FriendRoomPack { Roomid = "fr-9", Maxnum = 3, Curnum = 3, State = RoomState.RoomFull },
                new PMFriendRoomPack { Roomid = "fr-9", Maxnum = 3, Curnum = 3, State = PMRoomState.RoomFull });

            VerifyPair("PlayerPack", "PlayerPack",
                new PlayerPack { Username = "u7", Playername = "阿七", Id = 7, State = PlayerState.PlayerOnInvated, Hero = Hero.HeiYa, Fightpattern = FightPattern.Sheji },
                new PMPlayerPack { Username = "u7", Playername = "阿七", Id = 7, State = PMPlayerState.PlayerOnInvated, Hero = PMHero.HeiYa, Fightpattern = PMFightPattern.Sheji });
        }

        // ================= E. 字符串边界 =================

        private static void StringEdgeScenarios()
        {
            Section("E. 字符串边界（UTF8 中文 / 空串 / 非 BMP / 内嵌 NUL / 长度前缀跨字节）");

            string nonBmp = char.ConvertFromUtf32(0x1F600);              // U+1F600（4 字节 UTF-8，代理对）
            string mixed = "中" + nonBmp + "a\u0000b";                    // 混合 + 内嵌 NUL
            string longAscii = new string('x', 300);                      // 长度前缀需 2 字节
            string longCjk = Repeat("汉字", 64);                          // 128 个中文 → 384 字节 UTF-8

            string[] cases = new string[] { "中文测试", "", nonBmp, "a\u0000b", "\u0000", mixed, longAscii, longCjk };

            for (int i = 0; i < cases.Length; i++)
            {
                string value = cases[i];
                string label = "字符串 #" + i + " len=" + value.Length + " bytes=" + Encoding.UTF8.GetByteCount(value);

                MainPack g = new MainPack();
                g.Requestcode = RequestCode.User;
                g.Actioncode = ActionCode.Login;
                g.Str = value;
                g.Loginpack = new LoginPack { Username = value, Password = value };
                g.Chatpack = new ChatPack { Playername = value, Message = value, State = 1 };
                g.Roompack.Add(new RoomPack { Roomid = value, Maxnum = 2, Curnum = 1, State = RoomState.RoomNormal });
                g.Playerspack.Add(MakePlayerPack(value, value, 5, PlayerState.PlayerOnline, Hero.BaLi, FightPattern.Sheji));

                PMMainPack p = new PMMainPack();
                p.Requestcode = PMRequestCode.User;
                p.Actioncode = PMActionCode.Login;
                p.Str = value;
                p.Loginpack = new PMLoginPack { Username = value, Password = value };
                p.Chatpack = new PMChatPack { Playername = value, Message = value, State = 1 };
                p.Roompack.Add(new PMRoomPack { Roomid = value, Maxnum = 2, Curnum = 1, State = PMRoomState.RoomNormal });
                p.Playerspack.Add(MakePmPlayerPack(value, value, 5, PMPlayerState.PlayerOnline, PMHero.BaLi, PMFightPattern.Sheji));

                VerifyPair(label, "MainPack", g, p);
            }
        }

        // ================= F. 标量边界 =================

        private static void ScalarEdgeScenarios()
        {
            Section("F. 标量边界（负 int32 的 10 字节 varint / int64 64 位边界 / request_id 边界 / 未知枚举值）");

            MainPack g = new MainPack();
            g.Requestcode = (RequestCode)1000;            // proto3 允许未知枚举值
            g.Actioncode = (ActionCode)(-5);              // 负枚举 → 10 字节 varint
            g.Returncode = (ReturnCode)(-1);
            g.RequestId = int.MinValue;
            g.Timestamp = long.MinValue;
            g.Str = "";
            g.Chatpack = new ChatPack { Playername = "负值", Message = "", State = -7 };
            g.UserInfopack = new PlayerPack
            {
                Username = "neg",
                Playername = "负边界",
                Id = int.MinValue,
                State = (PlayerState)(-3),
                Hero = (Hero)(-1),
                Fightpattern = (FightPattern)(-2),
            };
            g.Roompack.Add(new RoomPack { Roomid = "neg", Maxnum = int.MinValue, Curnum = int.MaxValue, State = (RoomState)(-1) });
            g.Battleplayerpack.Add(new BattlePlayerPack
            {
                Id = int.MaxValue,
                Teamid = int.MinValue,
                Roomid = -1,
                Playername = "",
                Hero = (Hero)int.MaxValue,
                Battleid = int.MinValue,
            });

            PMMainPack p = new PMMainPack();
            p.Requestcode = (PMRequestCode)1000;
            p.Actioncode = (PMActionCode)(-5);
            p.Returncode = (PMReturnCode)(-1);
            p.RequestId = int.MinValue;
            p.Timestamp = long.MinValue;
            p.Str = "";
            p.Chatpack = new PMChatPack { Playername = "负值", Message = "", State = -7 };
            p.UserInfopack = new PMPlayerPack
            {
                Username = "neg",
                Playername = "负边界",
                Id = int.MinValue,
                State = (PMPlayerState)(-3),
                Hero = (PMHero)(-1),
                Fightpattern = (PMFightPattern)(-2),
            };
            p.Roompack.Add(new PMRoomPack { Roomid = "neg", Maxnum = int.MinValue, Curnum = int.MaxValue, State = (PMRoomState)(-1) });
            p.Battleplayerpack.Add(new PMBattlePlayerPack
            {
                Id = int.MaxValue,
                Teamid = int.MinValue,
                Roomid = -1,
                Playername = "",
                Hero = (PMHero)int.MaxValue,
                Battleid = int.MinValue,
            });

            VerifyPair("负值/极值 MainPack", "MainPack", g, p);

            // 单独覆盖 int64 上界与 int32 上界（避免全部挤在同一个 message 里）
            MainPack g2 = new MainPack();
            g2.Timestamp = long.MaxValue;
            g2.RequestId = int.MaxValue;
            g2.Chatpack = new ChatPack { State = int.MaxValue };

            PMMainPack p2 = new PMMainPack();
            p2.Timestamp = long.MaxValue;
            p2.RequestId = int.MaxValue;
            p2.Chatpack = new PMChatPack { State = int.MaxValue };

            VerifyPair("正上界 MainPack（int64.MaxValue / int32.MaxValue）", "MainPack", g2, p2);

            // -1 与 request_id 的「非请求 = 0 / 任意非 0」语义边界
            MainPack g3 = new MainPack();
            g3.Timestamp = -1L;
            g3.RequestId = -1;
            g3.Str = "req";

            PMMainPack p3 = new PMMainPack();
            p3.Timestamp = -1L;
            p3.RequestId = -1;
            p3.Str = "req";

            VerifyPair("-1 边界 MainPack", "MainPack", g3, p3);
        }

        // ================= G. repeated 规模 =================

        private static void RepeatedScalingScenarios()
        {
            Section("G. repeated 规模（0 / 1 / " + 64 + "）：RoomPack / BattlePlayerPack / FriendRoomPack / PlayerPack / Friendspack");

            int[] counts = new int[] { 0, 1, 64 };

            for (int c = 0; c < counts.Length; c++)
            {
                int count = counts[c];

                for (int kind = 0; kind < 5; kind++)
                {
                    MainPack g = new MainPack();
                    g.Requestcode = RequestCode.Room;
                    g.Actioncode = ActionCode.PlayerList;

                    PMMainPack p = new PMMainPack();
                    p.Requestcode = PMRequestCode.Room;
                    p.Actioncode = PMActionCode.PlayerList;

                    string label;
                    switch (kind)
                    {
                        case 0:
                            label = "roompack";
                            for (int i = 0; i < count; i++)
                            {
                                g.Roompack.Add(new RoomPack { Roomid = "r" + i, Maxnum = 6, Curnum = i % 7, State = RoomState.RoomNormal });
                                p.Roompack.Add(new PMRoomPack { Roomid = "r" + i, Maxnum = 6, Curnum = i % 7, State = PMRoomState.RoomNormal });
                            }
                            break;
                        case 1:
                            label = "battleplayerpack";
                            for (int i = 0; i < count; i++)
                            {
                                g.Battleplayerpack.Add(new BattlePlayerPack { Id = i, Teamid = i % 2, Roomid = 3, Playername = "名册" + i, Hero = (Hero)(i % 20), Battleid = i });
                                p.Battleplayerpack.Add(new PMBattlePlayerPack { Id = i, Teamid = i % 2, Roomid = 3, Playername = "名册" + i, Hero = (PMHero)(i % 20), Battleid = i });
                            }
                            break;
                        case 2:
                            label = "friendroompack";
                            for (int i = 0; i < count; i++)
                            {
                                g.Friendroompack.Add(new FriendRoomPack { Roomid = "fr" + i, Maxnum = 3, Curnum = i % 4, State = RoomState.RoomGame });
                                p.Friendroompack.Add(new PMFriendRoomPack { Roomid = "fr" + i, Maxnum = 3, Curnum = i % 4, State = PMRoomState.RoomGame });
                            }
                            break;
                        case 3:
                            label = "playerspack";
                            for (int i = 0; i < count; i++)
                            {
                                g.Playerspack.Add(MakePlayerPack("u" + i, "玩家" + i, i, PlayerState.PlayerOnline, (Hero)(i % 20), FightPattern.Sheji));
                                p.Playerspack.Add(MakePmPlayerPack("u" + i, "玩家" + i, i, PMPlayerState.PlayerOnline, (PMHero)(i % 20), PMFightPattern.Sheji));
                            }
                            break;
                        default:
                            label = "friendspack";
                            for (int i = 0; i < count; i++)
                            {
                                g.Friendspack.Add(MakePlayerPack("f" + i, "好友" + i, 100 + i, PlayerState.PlayerOutline, (Hero)(i % 20), FightPattern.BaoShiZhengBa));
                                p.Friendspack.Add(MakePmPlayerPack("f" + i, "好友" + i, 100 + i, PMPlayerState.PlayerOutline, (PMHero)(i % 20), PMFightPattern.BaoShiZhengBa));
                            }
                            break;
                    }

                    VerifyPair("MainPack." + label + " × " + count, "MainPack", g, p);
                }
            }
        }

        // ================= H. 未知字段跳过 =================

        private static void UnknownFieldSkipScenario()
        {
            Section("H. 未知字段：读取必须跳过而不是误解析（含被冻结的 MainPack 13/15 槽位）");

            MainPack g = new MainPack();
            g.Requestcode = RequestCode.Friend;
            g.Actioncode = ActionCode.FindFriendsInfo;
            g.Str = "未知字段用例";
            g.Loginpack = new LoginPack { Username = "u", Password = "p" };
            g.Roompack.Add(new RoomPack { Roomid = "r0", Maxnum = 2, Curnum = 1, State = RoomState.RoomNormal });
            g.Timestamp = 42L;
            g.RequestId = 7;

            byte[] known = g.ToByteArray();

            List<byte> payload = new List<byte>(known);
            // 普通未知字段：varint / fixed32 / fixed64 / length-delimited
            payload.AddRange(EncodeTag(97, 0));
            payload.AddRange(EncodeVarint(300UL));
            payload.AddRange(EncodeTag(98, 5));
            payload.AddRange(new byte[] { 0x01, 0x02, 0x03, 0x04 });
            payload.AddRange(EncodeTag(99, 1));
            payload.AddRange(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            payload.AddRange(EncodeTag(100, 2));
            payload.AddRange(EncodeBytes(Encoding.UTF8.GetBytes("abc")));
            // 被冻结的旧槽位 13 / 15：内容刻意做成「像旧战斗包」的字节，仍必须被跳过
            payload.AddRange(EncodeTag(13, 2));
            payload.AddRange(EncodeBytes(new byte[] { 0x08, 0x01, 0x10, 0x02 }));
            payload.AddRange(EncodeTag(15, 2));
            payload.AddRange(EncodeBytes(new byte[] { 0x08, 0x03 }));

            byte[] withUnknown = payload.ToArray();

            // ---- protoc 侧：必须能解析；已知字段投影必须与参考一致 ----
            object googleParsed = GoogleParse(typeof(MainPack), withUnknown);
            byte[] googleReencoded = ((IMessage)googleParsed).ToByteArray();
            bool googlePreserved = BytesEqual(googleReencoded, withUnknown);
            bool googleDropped = BytesEqual(googleReencoded, known);
            CheckCondition("protoc 侧解析含未知字段的包不抛异常且已知字段投影不变",
                ((IMessage)googleParsed).ToString() == g.ToString(),
                "已知字段投影不一致");
            CheckCondition("protoc 侧重编码结果要么保留未知字段、要么丢弃（无第三种结果）",
                googlePreserved || googleDropped,
                "长度 " + googleReencoded.Length + " 既非 " + withUnknown.Length + " 也非 " + known.Length);
            Console.WriteLine("  [INFO] protoc 侧未知字段处理: " + (googlePreserved ? "保留并原序写回" : "丢弃"));

            // ---- PMNet 侧：跳过未知字段，重编码必须精确回到已知字段字节 ----
            object pmParsed = PmParse(typeof(PMMainPack), withUnknown);
            byte[] pmReencoded = PmWrite(typeof(PMMainPack), pmParsed);
            CheckBytes("PMNet 侧解析含未知字段的包后重编码 == 已知字段字节", known, pmReencoded);

            // 反向：protoc 写出的已知字段字节，PMNet 解析后重编码一致（同上但显式再断言一次）
            CheckBytes("PMNet 解析 protoc 已知字节重编码一致", known,
                PmWrite(typeof(PMMainPack), PmParse(typeof(PMMainPack), known)));

            _exercisedMessages.Add("MainPack");
        }

        // ================= I. 截断拒绝 =================

        private static void TruncationParityScenario()
        {
            Section("I. 截断输入：protoc 拒绝的，PMNet 必须同样拒绝；两者都接受时字节必须一致");

            MainPack g = new MainPack();
            g.Requestcode = RequestCode.User;
            g.Actioncode = ActionCode.Login;
            g.Str = "截断用例";
            g.Loginpack = new LoginPack { Username = "player", Password = "secret" };
            g.Roompack.Add(new RoomPack { Roomid = "room", Maxnum = 3, Curnum = 1, State = RoomState.RoomNormal });
            g.Timestamp = 1700000000123L;
            g.RequestId = 99;

            byte[] full = g.ToByteArray();

            int acceptedBoth = 0;
            int rejectedBoth = 0;
            int mismatch = 0;
            int firstMismatchAt = -1;

            for (int length = 0; length <= full.Length; length++)
            {
                byte[] prefix = new byte[length];
                Buffer.BlockCopy(full, 0, prefix, 0, length);

                byte[] googleBytes = null;
                byte[] pmBytes = null;
                bool googleOk = TryGoogleSerialize(prefix, out googleBytes);
                bool pmOk = TryPmSerialize(prefix, out pmBytes);

                if (googleOk != pmOk)
                {
                    mismatch++;
                    if (firstMismatchAt < 0)
                    {
                        firstMismatchAt = length;
                    }

                    continue;
                }

                if (!googleOk)
                {
                    rejectedBoth++;
                    continue;
                }

                acceptedBoth++;
                if (!BytesEqual(googleBytes, pmBytes))
                {
                    mismatch++;
                    if (firstMismatchAt < 0)
                    {
                        firstMismatchAt = length;
                    }
                }
            }

            CheckCondition("截断前缀的接受/拒绝判定与 protoc 完全一致（" + (full.Length + 1) + " 个前缀）",
                mismatch == 0,
                "首个不一致前缀长度 = " + firstMismatchAt + "（" + mismatch + " 处）");
            CheckCondition("截断样本里两种结果都出现过（断言非空洞）",
                rejectedBoth > 0 && acceptedBoth > 0,
                "rejected=" + rejectedBoth + " accepted=" + acceptedBoth);
            Console.WriteLine("  [INFO] 前缀统计: 双方接受 " + acceptedBoth + " 个 / 双方拒绝 " + rejectedBoth + " 个 / 总计 " + (full.Length + 1));

            // 显式挑一个「字段中途被截断」的样本，要求双方都拒绝
            byte[] midField = new byte[full.Length - 1];
            Buffer.BlockCopy(full, 0, midField, 0, full.Length - 1);
            byte[] ignoredA;
            byte[] ignoredB;
            bool googleMid = TryGoogleSerialize(midField, out ignoredA);
            bool pmMid = TryPmSerialize(midField, out ignoredB);
            CheckCondition("字段中途截断（末尾少 1 字节）双方都拒绝",
                !googleMid && !pmMid, "protoc=" + (googleMid ? "接受" : "拒绝") + " PMNet=" + (pmMid ? "接受" : "拒绝"));

            _exercisedMessages.Add("MainPack");
        }

        // ================= J. 生成产物门禁 =================

        private static void ArtifactGate(string repoRoot)
        {
            Section("J. 生成产物门禁（4 份产物存在 / 不含退役类型标识符 / PMNet proto 哈希自洽）");

            string[] artifacts = new string[]
            {
                "ProtobufAndNotepad/Protobuf/CSharp/SocketProto.cs",
                "Client/Assets/Scripts/Server/SocketProto.cs",
                "Server/Server/SocketProto.cs",
                PmNetGeneratedRelativePath,
            };

            for (int a = 0; a < artifacts.Length; a++)
            {
                string rel = artifacts[a];
                string full = Path.Combine(repoRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                bool exists = File.Exists(full);
                CheckCondition("产物存在 " + rel, exists, "缺失: " + full);
                if (!exists)
                {
                    continue;
                }

                string text = File.ReadAllText(full, new UTF8Encoding(false));
                for (int i = 0; i < RetiredTypeNames.Length; i++)
                {
                    string retired = RetiredTypeNames[i];
                    CheckCondition("产物 " + rel + " 不含退役标识符 " + retired,
                        !ContainsIdentifier(text, retired), "仍出现该标识符");
                }
            }

            string generatedPath = Path.Combine(repoRoot, PmNetGeneratedRelativePath.Replace('/', Path.DirectorySeparatorChar));
            string protoPath = Path.Combine(repoRoot, ProtoRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(generatedPath) && File.Exists(protoPath))
            {
                string generated = File.ReadAllText(generatedPath, new UTF8Encoding(false));
                string proto = File.ReadAllText(protoPath, new UTF8Encoding(false));

                string declared = ExtractProtoHashConstant(generated);
                string actual = Sha256Hex(proto.Replace("\r\n", "\n"));

                CheckCondition("PMNet 生成物 ProtoHash 与权威 proto 内容哈希一致",
                    declared != null && declared == actual,
                    "declared=" + (declared == null ? "<缺>" : declared) + " actual=" + actual);
            }
        }

        // ================= K. 覆盖门禁 =================

        private static void CoverageGate(ProtoFileSchema schema)
        {
            Section("K. 覆盖门禁（proto 里每个 message / enum 都至少被本程序断言过一次）");

            HashSet<string> messages = schema.MessageNames();
            HashSet<string> enums = schema.EnumNames();

            List<string> uncoveredMessages = new List<string>();
            foreach (string name in messages)
            {
                if (!_exercisedMessages.Contains(name))
                {
                    uncoveredMessages.Add(name);
                }
            }

            List<string> uncoveredEnums = new List<string>();
            foreach (string name in enums)
            {
                if (!_exercisedEnums.Contains(name))
                {
                    uncoveredEnums.Add(name);
                }
            }

            CheckCondition("proto 全部 " + messages.Count + " 个 message 均被断言",
                uncoveredMessages.Count == 0, "未覆盖: " + Join(uncoveredMessages));
            CheckCondition("proto 全部 " + enums.Count + " 个 enum 均被断言",
                uncoveredEnums.Count == 0, "未覆盖: " + Join(uncoveredEnums));

            Console.WriteLine("  [INFO] 覆盖边界：旧战斗/浮点路径（ClientMove 三元组 / MoveAckResult 修正量 / HitEvent 命中坐标）");
            Console.WriteLine("         已随 E1 从权威 proto 删除，本程序不再对其断言；剩余 DTO 无 float/double 字段。");
        }

        // ================= 对照驱动（Google.Protobuf 为唯一 oracle） =================

        private static bool VerifyPair<TG, TP>(string label, string protoMessageName, TG google, TP pmNet)
            where TG : class, IMessage
            where TP : class
        {
            return VerifyReflective(label, protoMessageName, typeof(TG), google, typeof(TP), pmNet);
        }

        private static bool VerifyReflective(
            string label, string protoMessageName, Type googleType, object googleMessage, Type pmType, object pmMessage)
        {
            byte[] googleBytes = ((IMessage)googleMessage).ToByteArray();
            byte[] pmBytes = PmWrite(pmType, pmMessage);

            _exercisedMessages.Add(protoMessageName);

            bool match = CheckBytes(label + " 字节一致（protoc vs PMNetWriter）", googleBytes, pmBytes);
            if (!match)
            {
                Dump(label, googleBytes, pmBytes);
                return false;
            }

            // protoc 解析 PMNet 产物后重编码：必须仍逐字节相同
            object googleFromPm = GoogleParse(googleType, pmBytes);
            CheckBytes(label + " protoc 解析 PMNet 字节后重编码一致", pmBytes, ((IMessage)googleFromPm).ToByteArray());

            // PMNet 解析 protoc 产物后重编码：必须仍逐字节相同
            object pmFromGoogle = PmParse(pmType, googleBytes);
            CheckBytes(label + " PMNet 解析 protoc 字节后重编码一致", googleBytes, PmWrite(pmType, pmFromGoogle));
            return true;
        }

        private static object GoogleParse(Type googleType, byte[] data)
        {
            PropertyInfo parserProperty = googleType.GetProperty("Parser", BindingFlags.Public | BindingFlags.Static);
            object parser = parserProperty.GetValue(null, null);
            MethodInfo parse = parser.GetType().GetMethod("ParseFrom", new Type[] { typeof(byte[]) });
            return parse.Invoke(parser, new object[] { data });
        }

        private static bool TryGoogleSerialize(byte[] data, out byte[] result)
        {
            try
            {
                result = ((IMessage)GoogleParse(typeof(MainPack), data)).ToByteArray();
                return true;
            }
            catch (Exception)
            {
                result = null;
                return false;
            }
        }

        private static byte[] PmWrite(Type pmType, object message)
        {
            MethodInfo method;
            if (!_pmWriteCache.TryGetValue(pmType, out method))
            {
                Type serializer = pmType.Assembly.GetType(pmType.Namespace + "." + pmType.Name + "Serializer", true);
                method = serializer.GetMethod("ToByteArray", BindingFlags.Public | BindingFlags.Static, null, new Type[] { pmType }, null);
                _pmWriteCache[pmType] = method;
            }

            return (byte[])method.Invoke(null, new object[] { message });
        }

        private static object PmParse(Type pmType, byte[] data)
        {
            MethodInfo method;
            if (!_pmParseCache.TryGetValue(pmType, out method))
            {
                Type serializer = pmType.Assembly.GetType(pmType.Namespace + "." + pmType.Name + "Serializer", true);
                method = serializer.GetMethod("ParseFrom", BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(byte[]) }, null);
                _pmParseCache[pmType] = method;
            }

            return method.Invoke(null, new object[] { data });
        }

        private static bool TryPmSerialize(byte[] data, out byte[] result)
        {
            try
            {
                result = PmWrite(typeof(PMMainPack), PmParse(typeof(PMMainPack), data));
                return true;
            }
            catch (Exception)
            {
                result = null;
                return false;
            }
        }

        // ================= 编码辅助（构造未知字段） =================

        private static byte[] EncodeTag(int fieldNumber, int wireType)
        {
            return EncodeVarint(((ulong)(uint)fieldNumber << 3) | (ulong)(uint)wireType);
        }

        private static byte[] EncodeVarint(ulong value)
        {
            List<byte> bytes = new List<byte>(10);
            ulong current = value;
            do
            {
                byte b = (byte)(current & 0x7FUL);
                current >>= 7;
                if (current != 0UL)
                {
                    b |= 0x80;
                }

                bytes.Add(b);
            }
            while (current != 0UL);

            return bytes.ToArray();
        }

        private static byte[] EncodeBytes(byte[] payload)
        {
            List<byte> bytes = new List<byte>(payload.Length + 4);
            bytes.AddRange(EncodeVarint((ulong)payload.Length));
            bytes.AddRange(payload);
            return bytes.ToArray();
        }

        private static string ExtractProtoHashConstant(string generatedText)
        {
            const string marker = "ProtoHash = \"";
            int start = generatedText.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            start += marker.Length;
            int end = generatedText.IndexOf('"', start);
            if (end < 0)
            {
                return null;
            }

            return generatedText.Substring(start, end - start);
        }

        private static string Sha256Hex(string text)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(new UTF8Encoding(false).GetBytes(text));
                StringBuilder sb = new StringBuilder(digest.Length * 2);
                for (int i = 0; i < digest.Length; i++)
                {
                    sb.Append(digest[i].ToString("x2"));
                }

                return sb.ToString();
            }
        }

        private static bool ContainsIdentifier(string text, string identifier)
        {
            int index = 0;
            while ((index = text.IndexOf(identifier, index, StringComparison.Ordinal)) >= 0)
            {
                int end = index + identifier.Length;
                bool leftOk = index == 0 || !IsIdentifierChar(text[index - 1]);
                bool rightOk = end >= text.Length || !IsIdentifierChar(text[end]);
                if (leftOk && rightOk)
                {
                    return true;
                }

                index = end;
            }

            return false;
        }

        private static bool IsIdentifierChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        // ================= 权威 proto 解析（运行时读取，不写死数量） =================

        private sealed class ProtoField
        {
            public string TypeName;
            public string Name;
            public int Number;
            public bool Repeated;
        }

        private sealed class ProtoMessage
        {
            public string Name;
            public readonly List<ProtoField> Fields = new List<ProtoField>();
            public readonly List<string> Reserved = new List<string>();
        }

        private sealed class ProtoEnumType
        {
            public string Name;
            public readonly List<string> ValueNames = new List<string>();
            public readonly List<int> ValueNumbers = new List<int>();
            public readonly List<string> Reserved = new List<string>();

            public bool HasValueName(string valueName)
            {
                return ValueNames.Contains(valueName);
            }

            public int ValueOf(string valueName)
            {
                for (int i = 0; i < ValueNames.Count; i++)
                {
                    if (ValueNames[i] == valueName)
                    {
                        return ValueNumbers[i];
                    }
                }

                return -1;
            }

            public int MaxValue()
            {
                int max = int.MinValue;
                for (int i = 0; i < ValueNumbers.Count; i++)
                {
                    if (ValueNumbers[i] > max)
                    {
                        max = ValueNumbers[i];
                    }
                }

                return max;
            }

            public string Describe()
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < ValueNames.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    sb.Append(ValueNames[i]).Append('=').Append(ValueNumbers[i]);
                }

                return sb.ToString();
            }
        }

        private sealed class ProtoFileSchema
        {
            public readonly List<ProtoMessage> Messages = new List<ProtoMessage>();
            public readonly List<ProtoEnumType> Enums = new List<ProtoEnumType>();

            public ProtoMessage FindMessage(string name)
            {
                for (int i = 0; i < Messages.Count; i++)
                {
                    if (Messages[i].Name == name)
                    {
                        return Messages[i];
                    }
                }

                return null;
            }

            public ProtoEnumType FindEnum(string name)
            {
                for (int i = 0; i < Enums.Count; i++)
                {
                    if (Enums[i].Name == name)
                    {
                        return Enums[i];
                    }
                }

                return null;
            }

            public HashSet<string> MessageNames()
            {
                HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < Messages.Count; i++)
                {
                    names.Add(Messages[i].Name);
                }

                return names;
            }

            public HashSet<string> EnumNames()
            {
                HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < Enums.Count; i++)
                {
                    names.Add(Enums[i].Name);
                }

                return names;
            }

            public static ProtoFileSchema Parse(string text)
            {
                ProtoFileSchema schema = new ProtoFileSchema();
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                ProtoMessage currentMessage = null;
                ProtoEnumType currentEnum = null;
                string pendingReserved = null;

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = StripComment(lines[i]).Trim();
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    // reserved 允许跨行书写：累积到出现分号为止。
                    // 否则续行里的名字会漏记，门禁会在「名字确实被冻结」时误报失败。
                    if (pendingReserved != null)
                    {
                        pendingReserved = pendingReserved + " " + line;
                        if (line.EndsWith(";", StringComparison.Ordinal))
                        {
                            AddReserved(currentMessage, currentEnum,
                                pendingReserved.Substring(0, pendingReserved.Length - 1).Trim());
                            pendingReserved = null;
                        }

                        continue;
                    }

                    if (line.StartsWith("message ", StringComparison.Ordinal))
                    {
                        currentMessage = new ProtoMessage();
                        currentMessage.Name = TakeIdentifier(line.Substring("message ".Length));
                        schema.Messages.Add(currentMessage);
                        currentEnum = null;
                        continue;
                    }

                    if (line.StartsWith("enum ", StringComparison.Ordinal))
                    {
                        currentEnum = new ProtoEnumType();
                        currentEnum.Name = TakeIdentifier(line.Substring("enum ".Length));
                        schema.Enums.Add(currentEnum);
                        currentMessage = null;
                        continue;
                    }

                    if (line == "{" || line == "}")
                    {
                        continue;
                    }

                    if (line.StartsWith("reserved ", StringComparison.Ordinal))
                    {
                        string body = line.Substring("reserved ".Length).Trim();
                        if (body.EndsWith(";", StringComparison.Ordinal))
                        {
                            AddReserved(currentMessage, currentEnum, body.Substring(0, body.Length - 1).Trim());
                        }
                        else
                        {
                            pendingReserved = body;
                        }

                        continue;
                    }

                    if (currentEnum != null)
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0)
                        {
                            string valueName = line.Substring(0, eq).Trim();
                            string numberText = line.Substring(eq + 1).Trim();
                            if (numberText.EndsWith(";", StringComparison.Ordinal))
                            {
                                numberText = numberText.Substring(0, numberText.Length - 1).Trim();
                            }

                            int number;
                            if (int.TryParse(numberText, out number))
                            {
                                currentEnum.ValueNames.Add(valueName);
                                currentEnum.ValueNumbers.Add(number);
                            }
                        }

                        continue;
                    }

                    if (currentMessage != null)
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0)
                        {
                            string left = line.Substring(0, eq).Trim();
                            string numberText = line.Substring(eq + 1).Trim();
                            int bracket = numberText.IndexOf('[');
                            if (bracket >= 0)
                            {
                                numberText = numberText.Substring(0, bracket).Trim();
                            }

                            if (numberText.EndsWith(";", StringComparison.Ordinal))
                            {
                                numberText = numberText.Substring(0, numberText.Length - 1).Trim();
                            }

                            int number;
                            if (!int.TryParse(numberText, out number))
                            {
                                continue;
                            }

                            string[] parts = left.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length < 2)
                            {
                                continue;
                            }

                            ProtoField field = new ProtoField();
                            field.Repeated = parts.Length >= 3 && parts[0] == "repeated";
                            if (field.Repeated)
                            {
                                field.TypeName = parts[1];
                                field.Name = parts[2];
                            }
                            else
                            {
                                field.TypeName = parts[0];
                                field.Name = parts[1];
                            }

                            field.Number = number;
                            currentMessage.Fields.Add(field);
                        }

                        continue;
                    }
                }

                return schema;
            }

            private static void AddReserved(ProtoMessage message, ProtoEnumType enumType, string body)
            {
                if (message != null)
                {
                    message.Reserved.Add(body);
                }
                else if (enumType != null)
                {
                    enumType.Reserved.Add(body);
                }
            }

            private static string StripComment(string line)
            {
                int index = line.IndexOf("//", StringComparison.Ordinal);
                return index < 0 ? line : line.Substring(0, index);
            }

            private static string TakeIdentifier(string text)
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (char.IsLetterOrDigit(c) || c == '_')
                    {
                        sb.Append(c);
                    }
                    else
                    {
                        break;
                    }
                }

                return sb.ToString();
            }
        }

        // ================= 路径解析 =================

        private static bool TryResolveProtoPath(out string repoRoot, out string protoPath)
        {
            DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 12 && directory != null; i++)
            {
                string candidate = Path.Combine(directory.FullName, ProtoRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                {
                    repoRoot = directory.FullName;
                    protoPath = candidate;
                    return true;
                }

                directory = directory.Parent;
            }

            repoRoot = null;
            protoPath = null;
            return false;
        }

        // ================= 输出与断言 =================

        private static void PrintInventory(ProtoFileSchema schema)
        {
            Console.WriteLine("权威 proto 清单（运行时解析，行数与数量不作为常量写死）：");
            Console.WriteLine("  message 数: " + schema.Messages.Count);
            for (int i = 0; i < schema.Messages.Count; i++)
            {
                ProtoMessage pm = schema.Messages[i];
                StringBuilder sb = new StringBuilder();
                for (int f = 0; f < pm.Fields.Count; f++)
                {
                    if (f > 0)
                    {
                        sb.Append(", ");
                    }

                    sb.Append(pm.Fields[f].Name).Append('=').Append(pm.Fields[f].Number);
                }

                Console.WriteLine("    " + pm.Name + "（字段 " + pm.Fields.Count + "）: " + sb);
                if (pm.Reserved.Count > 0)
                {
                    Console.WriteLine("      reserved: " + ReservedText(pm.Reserved));
                }
            }

            Console.WriteLine("  enum 数: " + schema.Enums.Count);
            for (int i = 0; i < schema.Enums.Count; i++)
            {
                ProtoEnumType pe = schema.Enums[i];
                Console.WriteLine("    " + pe.Name + "（" + pe.ValueNames.Count + "）: " + pe.Describe());
                if (pe.Reserved.Count > 0)
                {
                    Console.WriteLine("      reserved: " + ReservedText(pe.Reserved));
                }
            }

            Console.WriteLine();
        }

        private static bool ReservedHasNumber(List<string> reserved, int number)
        {
            string joined = string.Join(" ", reserved);
            string[] tokens = joined.Split(new char[] { ',', ' ', '\t', '"' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];
                if (token == number.ToString())
                {
                    return true;
                }

                if (token == "to" && i > 0 && i + 1 < tokens.Length)
                {
                    int low;
                    int high;
                    if (int.TryParse(tokens[i - 1], out low) && int.TryParse(tokens[i + 1], out high)
                        && number >= low && number <= high)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool ReservedHasName(List<string> reserved, string name)
        {
            string joined = string.Join(" ", reserved);
            return joined.IndexOf("\"" + name + "\"", StringComparison.Ordinal) >= 0;
        }

        private static string ReservedText(List<string> reserved)
        {
            return reserved.Count == 0 ? "<无>" : string.Join(" | ", reserved);
        }

        private static string Join(IEnumerable<string> values)
        {
            return string.Join(", ", values);
        }

        private static string DescribeMap(Dictionary<string, int> map)
        {
            StringBuilder sb = new StringBuilder();
            bool first = true;
            foreach (KeyValuePair<string, int> pair in map)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                sb.Append(pair.Key).Append('=').Append(pair.Value);
            }

            return sb.ToString();
        }

        private static string Repeat(string value, int count)
        {
            StringBuilder sb = new StringBuilder(value.Length * count);
            for (int i = 0; i < count; i++)
            {
                sb.Append(value);
            }

            return sb.ToString();
        }

        private static PlayerPack MakePlayerPack(
            string username, string playername, int id, PlayerState state, Hero hero, FightPattern pattern)
        {
            return new PlayerPack
            {
                Username = username,
                Playername = playername,
                Id = id,
                State = state,
                Hero = hero,
                Fightpattern = pattern,
            };
        }

        private static PMPlayerPack MakePmPlayerPack(
            string username, string playername, int id, PMPlayerState state, PMHero hero, PMFightPattern pattern)
        {
            return new PMPlayerPack
            {
                Username = username,
                Playername = playername,
                Id = id,
                State = state,
                Hero = hero,
                Fightpattern = pattern,
            };
        }

        private static void Section(string title)
        {
            Console.WriteLine(title);
        }

        private static void CheckCondition(string name, bool ok, string failDetail)
        {
            _checks++;
            if (ok)
            {
                Console.WriteLine("  [PASS] " + name);
                return;
            }

            _failures++;
            Console.WriteLine("  [FAIL] " + name + "：" + failDetail);
        }

        private static bool CheckBytes(string name, byte[] expected, byte[] actual)
        {
            _checks++;
            if (BytesEqual(expected, actual))
            {
                Console.WriteLine("  [PASS] " + name + "（" + expected.Length + " B）");
                return true;
            }

            _failures++;
            Console.WriteLine("  [FAIL] " + name + "（期望 " + expected.Length + " B，实际 " + actual.Length + " B）");
            return false;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static void Dump(string name, byte[] expected, byte[] actual)
        {
            Console.WriteLine("    " + name + " protoc 侧: " + Hex(expected));
            Console.WriteLine("    " + name + " PMNet  侧: " + Hex(actual));

            int limit = Math.Min(expected.Length, actual.Length);
            for (int i = 0; i < limit; i++)
            {
                if (expected[i] != actual[i])
                {
                    Console.WriteLine("    首个差异位于字节偏移 " + i
                        + "：protoc 0x" + expected[i].ToString("X2") + "，PMNet 0x" + actual[i].ToString("X2"));
                    return;
                }
            }

            Console.WriteLine("    前缀相同，长度不同");
        }

        private static string Hex(byte[] data)
        {
            int limit = Math.Min(data.Length, 48);
            StringBuilder sb = new StringBuilder(limit * 3 + 8);
            for (int i = 0; i < limit; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(data[i].ToString("X2"));
            }

            if (data.Length > limit)
            {
                sb.Append(" ...(共 ").Append(data.Length).Append(" B)");
            }

            return sb.ToString();
        }
    }
}
