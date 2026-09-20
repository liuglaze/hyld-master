using System;
using System.Collections.Generic;
using System.Text;
using Google.Protobuf;
using PMNet.Generated;
using SocketProto;

namespace PMNetVerify
{
    /// <summary>
    /// 计划 T2：验证 PMNet 生成的序列化器与 Google.Protobuf 逐字节等价，且双向可交叉解析。
    ///
    /// 为什么这是 P0 的判定性实验：
    /// 决策 D3 要求保留 protobuf 作为线格式，因此生成代码必须与 protoc 产物在线格式上完全一致，
    /// 否则新旧两端无法互通、历史回放包无法解析。本程序用真实 proto 构造多组场景做对照。
    ///
    /// 每组场景做三项检查：
    ///   1. Google.Protobuf 与 PMNet 序列化同一组取值，字节必须完全相同；
    ///   2. Google.Protobuf 能解析 PMNet 写出的字节，且语义等价；
    ///   3. PMNet 能解析 Google.Protobuf 写出的字节，重序列化后仍逐字节相同。
    ///
    /// 退出码：0 全部通过；1 存在失败。
    /// </summary>
    internal static class Program
    {
        private static int _checks;
        private static int _failures;

        private static int Main()
        {
            Console.WriteLine("== PMNet T2 逐字节等价校验 ==");
            Console.WriteLine("对照基准: Google.Protobuf " + typeof(Google.Protobuf.IMessage).Assembly.GetName().Version);
            Console.WriteLine();

            ScenarioEmpty();
            ScenarioLobby();
            ScenarioBattleHotPath();
            ScenarioScalarEdges();
            ScenarioDefaultOmission();
            ScenarioRepeatedScaling();

            Console.WriteLine();
            Console.WriteLine("== 汇总: " + _checks + " 项检查, " + _failures + " 项失败 ==");
            return _failures == 0 ? 0 : 1;
        }

        // ---------------- 场景 1：全默认值 ----------------

        private static void ScenarioEmpty()
        {
            Section("场景 1：全默认值（proto3 应产出 0 字节）");

            Verify<MainPack, PMMainPack>(
                "空 MainPack",
                new MainPack(),
                new PMMainPack(),
                delegate (MainPack m) { return m.ToByteArray(); },
                delegate (PMMainPack m) { return PMMainPackSerializer.ToByteArray(m); },
                delegate (byte[] b) { return MainPack.Parser.ParseFrom(b); },
                delegate (byte[] b) { return PMMainPackSerializer.ParseFrom(b); });

            Verify<AuthoritativePlayerState, PMAuthoritativePlayerState>(
                "空 AuthoritativePlayerState",
                new AuthoritativePlayerState(),
                new PMAuthoritativePlayerState(),
                delegate (AuthoritativePlayerState m) { return m.ToByteArray(); },
                delegate (PMAuthoritativePlayerState m) { return PMAuthoritativePlayerStateSerializer.ToByteArray(m); },
                delegate (byte[] b) { return AuthoritativePlayerState.Parser.ParseFrom(b); },
                delegate (byte[] b) { return PMAuthoritativePlayerStateSerializer.ParseFrom(b); });
        }

        // ---------------- 场景 2：大厅（枚举 / 字符串 / repeated message）----------------

        private static void ScenarioLobby()
        {
            Section("场景 2：大厅登录与房间列表（枚举 + 字符串 + repeated 子消息）");

            MainPack g = new MainPack();
            g.Requestcode = RequestCode.User;
            g.Actioncode = ActionCode.Login;
            g.Returncode = ReturnCode.Succeed;
            g.Loginpack = new LoginPack { Username = "player_one", Password = "p@ssw0rd" };
            g.Str = "入场消息";
            g.Timestamp = 1700000000123L;
            g.RequestId = 20260918;

            g.Roompack.Add(new RoomPack { Roomid = "room-1", Maxnum = 3, Curnum = 2, State = RoomState.RoomNormal });
            g.Roompack.Add(new RoomPack { Roomid = "room-2", Maxnum = 6, Curnum = 6, State = RoomState.RoomFull });

            g.Playerspack.Add(MakePlayerPack("u1", "阿一", 1, PlayerState.PlayerOnline, Hero.XueLi, FightPattern.BaoShiZhengBa));
            g.Playerspack.Add(MakePlayerPack("u2", "阿二", 2, PlayerState.PlayerGame, Hero.KeErTe, FightPattern.Sheji));
            g.Playerspack.Add(MakePlayerPack("u3", "阿三", 3, PlayerState.PlayerOnRoom, Hero.PeiPei, FightPattern.BaoShiZhengBa));

            g.Friendroompack.Add(new FriendRoomPack { Roomid = "fr-1", Maxnum = 3, Curnum = 1, State = RoomState.RoomNormal });
            g.Friendroompack.Add(new FriendRoomPack { Roomid = "fr-2", Maxnum = 6, Curnum = 3, State = RoomState.RoomGame });

            g.Chatpack = new ChatPack { Playername = "阿一", Message = "hello 世界", State = 7 };
            g.UserInfopack = MakePlayerPack("u9", "自己", 9, PlayerState.PlayerOnline, Hero.BeiYa, FightPattern.Sheji);

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

            g.BattleNetSimConfig = new BattleNetSimConfig
            {
                BattlePlayerId = 3,
                DropRate = 0.1f,
                DelayMinMs = 30,
                DelayMaxMs = 60,
            };

            PMMainPack p = new PMMainPack();
            p.Requestcode = PMRequestCode.User;
            p.Actioncode = PMActionCode.Login;
            p.Returncode = PMReturnCode.Succeed;
            p.Loginpack = new PMLoginPack { Username = "player_one", Password = "p@ssw0rd" };
            p.Str = "入场消息";
            p.Timestamp = 1700000000123L;
            p.RequestId = 20260918;

            p.Roompack.Add(new PMRoomPack { Roomid = "room-1", Maxnum = 3, Curnum = 2, State = PMRoomState.RoomNormal });
            p.Roompack.Add(new PMRoomPack { Roomid = "room-2", Maxnum = 6, Curnum = 6, State = PMRoomState.RoomFull });

            p.Playerspack.Add(MakePmPlayerPack("u1", "阿一", 1, PMPlayerState.PlayerOnline, PMHero.XueLi, PMFightPattern.BaoShiZhengBa));
            p.Playerspack.Add(MakePmPlayerPack("u2", "阿二", 2, PMPlayerState.PlayerGame, PMHero.KeErTe, PMFightPattern.Sheji));
            p.Playerspack.Add(MakePmPlayerPack("u3", "阿三", 3, PMPlayerState.PlayerOnRoom, PMHero.PeiPei, PMFightPattern.BaoShiZhengBa));

            p.Friendroompack.Add(new PMFriendRoomPack { Roomid = "fr-1", Maxnum = 3, Curnum = 1, State = PMRoomState.RoomNormal });
            p.Friendroompack.Add(new PMFriendRoomPack { Roomid = "fr-2", Maxnum = 6, Curnum = 3, State = PMRoomState.RoomGame });

            p.Chatpack = new PMChatPack { Playername = "阿一", Message = "hello 世界", State = 7 };
            p.UserInfopack = MakePmPlayerPack("u9", "自己", 9, PMPlayerState.PlayerOnline, PMHero.BeiYa, PMFightPattern.Sheji);

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

            p.BattleNetSimConfig = new PMBattleNetSimConfig
            {
                BattlePlayerId = 3,
                DropRate = 0.1f,
                DelayMinMs = 30,
                DelayMaxMs = 60,
            };

            Verify<MainPack, PMMainPack>(
                "大厅 MainPack",
                g,
                p,
                delegate (MainPack m) { return m.ToByteArray(); },
                delegate (PMMainPack m) { return PMMainPackSerializer.ToByteArray(m); },
                delegate (byte[] b) { return MainPack.Parser.ParseFrom(b); },
                delegate (byte[] b) { return PMMainPackSerializer.ParseFrom(b); });
        }

        // ---------------- 场景 3：战斗热路径（多层嵌套）----------------

        private static void ScenarioBattleHotPath()
        {
            Section("场景 3：战斗权威帧（4 层嵌套 + repeated + 枚举 + 负数 + bool）");

            MainPack g = new MainPack();
            g.Requestcode = RequestCode.Battle;
            g.Actioncode = ActionCode.BattlePushDowmAllFrameOpeartions;

            g.BattleInfo = new BattleInfo { ServerFrame = 1234, RandSeed = 987654321 };

            for (int i = 0; i < 6; i++)
            {
                g.BattleInfo.BattleUsers.Add(new BattlePlayerPack
                {
                    Id = 200 + i,
                    Teamid = i % 2,
                    Roomid = 5,
                    Playername = "战斗玩家" + i,
                    Hero = (Hero)(i % 20),
                    Battleid = i,
                });
            }

            BattleClientInput input = new BattleClientInput
            {
                BattlePlayerId = 0,
                ClientTick = 4321,
                AckedServerFrame = 1230,
                RttMs = 117,
            };
            input.Moves.Add(new ClientMove
            {
                MoveFrame = 4320,
                MoveX = 0.5f,
                MoveY = -0.5f,
                PredictedPosX = 1.25f,
                PredictedPosY = 1f,
                PredictedPosZ = -3.75f,
                MoveType = MoveType.NewMove,
                PredictedVelX = 0.1f,
                PredictedVelY = 0f,
                PredictedVelZ = -0.2f,
            });
            input.Moves.Add(new ClientMove
            {
                MoveFrame = 4321,
                MoveX = 1f,
                MoveY = 0f,
                PredictedPosX = 2.25f,
                PredictedPosY = 1f,
                PredictedPosZ = -3.85f,
                MoveType = MoveType.OldMove,
                PredictedVelX = 0.2f,
                PredictedVelY = 0f,
                PredictedVelZ = -0.1f,
            });
            input.Attacks.Add(new ClientAttack
            {
                AttackId = 777,
                AttackMoveFrame = 4320,
                TowardX = 0.707f,
                TowardY = 0.707f,
                AttackType = AttackType.Super,
            });
            g.BattleInfo.ClientInput = input;

            BattleServerUpdate update = new BattleServerUpdate { StateBaseFrame = 1225 };
            BattleFrame frame = new BattleFrame { ServerFrame = 1234 };

            for (int i = 0; i < 3; i++)
            {
                // 注意：i=0 时 -0.25f * 0 得到 -0.0f，两侧都会把它当默认值省略，
                // 解析回来变成 +0.0f。这是 protobuf 的固有行为，此处刻意保留作为回归点。
                PlayerFrameInput fi = new PlayerFrameInput
                {
                    BattlePlayerId = i,
                    MoveX = 0.25f * i,
                    MoveY = -0.25f * i,
                };
                if (i == 0)
                {
                    fi.Attacks.Add(new ServerAttack
                    {
                        AttackId = 777,
                        AttackerBattlePlayerId = 0,
                        AttackMoveFrame = 4320,
                        SpawnServerFrame = 1234,
                        TowardX = 0.707f,
                        TowardY = 0.707f,
                        SpawnPosX = -12.5f,
                        SpawnPosY = 1f,
                        SpawnPosZ = 4.25f,
                        AttackType = AttackType.Super,
                    });
                }

                frame.PlayerInputs.Add(fi);
            }

            for (int i = 0; i < 6; i++)
            {
                frame.PlayerStates.Add(new AuthoritativePlayerState
                {
                    BattleId = i,
                    PosX = -12.5f + i,
                    PosY = 1f,
                    PosZ = 4.25f - i,
                    Hp = 200 - i * 13,
                    IsDead = i == 5,
                    StateMask = 31u,
                    Mana = 90 - i * 5,
                    SuperEnergy = i * 40,
                });
            }

            update.Frames.Add(frame);
            update.MoveAck = new MoveAckResult
            {
                BattleId = 0,
                AckedMoveFrame = 4321,
                AckGoodMove = false,
                CorrectPosX = 2.5f,
                CorrectPosY = 1f,
                CorrectPosZ = -4f,
                FrameDiscrepancy = -5,
                ResolvingFrameDiscrepancy = true,
                CorrectVelX = 0.25f,
                CorrectVelY = 0f,
                CorrectVelZ = -0.125f,
            };
            update.HitEvents.Add(new HitEvent
            {
                AttackId = 777,
                AttackerBattleId = 0,
                VictimBattleId = 5,
                Damage = 37,
                HitFrameId = 1234,
                HitPosX = -10.5f,
                HitPosY = 1f,
                HitPosZ = 2.25f,
                IsKill = false,
            });
            update.HitEvents.Add(new HitEvent
            {
                AttackId = 778,
                AttackerBattleId = 0,
                VictimBattleId = 5,
                Damage = 200,
                HitFrameId = 1234,
                IsKill = true,
            });
            update.AttackAcks.Add(new AttackAck
            {
                BattlePlayerId = 0,
                AttackId = 777,
                Accepted = false,
                ManaAfter = 30,
                RejectReason = "mana",
                SuperEnergyAfter = 0,
            });
            g.BattleInfo.ServerUpdate = update;

            // ---- PMNet 侧同构构造 ----
            PMMainPack p = new PMMainPack();
            p.Requestcode = PMRequestCode.Battle;
            p.Actioncode = PMActionCode.BattlePushDowmAllFrameOpeartions;

            p.BattleInfo = new PMBattleInfo { ServerFrame = 1234, RandSeed = 987654321 };

            for (int i = 0; i < 6; i++)
            {
                p.BattleInfo.BattleUsers.Add(new PMBattlePlayerPack
                {
                    Id = 200 + i,
                    Teamid = i % 2,
                    Roomid = 5,
                    Playername = "战斗玩家" + i,
                    Hero = (PMHero)(i % 20),
                    Battleid = i,
                });
            }

            PMBattleClientInput pinput = new PMBattleClientInput
            {
                BattlePlayerId = 0,
                ClientTick = 4321,
                AckedServerFrame = 1230,
                RttMs = 117,
            };
            pinput.Moves.Add(new PMClientMove
            {
                MoveFrame = 4320,
                MoveX = 0.5f,
                MoveY = -0.5f,
                PredictedPosX = 1.25f,
                PredictedPosY = 1f,
                PredictedPosZ = -3.75f,
                MoveType = PMMoveType.NewMove,
                PredictedVelX = 0.1f,
                PredictedVelY = 0f,
                PredictedVelZ = -0.2f,
            });
            pinput.Moves.Add(new PMClientMove
            {
                MoveFrame = 4321,
                MoveX = 1f,
                MoveY = 0f,
                PredictedPosX = 2.25f,
                PredictedPosY = 1f,
                PredictedPosZ = -3.85f,
                MoveType = PMMoveType.OldMove,
                PredictedVelX = 0.2f,
                PredictedVelY = 0f,
                PredictedVelZ = -0.1f,
            });
            pinput.Attacks.Add(new PMClientAttack
            {
                AttackId = 777,
                AttackMoveFrame = 4320,
                TowardX = 0.707f,
                TowardY = 0.707f,
                AttackType = PMAttackType.Super,
            });
            p.BattleInfo.ClientInput = pinput;

            PMBattleServerUpdate pupdate = new PMBattleServerUpdate { StateBaseFrame = 1225 };
            PMBattleFrame pframe = new PMBattleFrame { ServerFrame = 1234 };

            for (int i = 0; i < 3; i++)
            {
                // 与 Google 侧保持同构，含 i=0 的 -0.0f 回归点（见上方注释）。
                PMPlayerFrameInput pfi = new PMPlayerFrameInput
                {
                    BattlePlayerId = i,
                    MoveX = 0.25f * i,
                    MoveY = -0.25f * i,
                };
                if (i == 0)
                {
                    pfi.Attacks.Add(new PMServerAttack
                    {
                        AttackId = 777,
                        AttackerBattlePlayerId = 0,
                        AttackMoveFrame = 4320,
                        SpawnServerFrame = 1234,
                        TowardX = 0.707f,
                        TowardY = 0.707f,
                        SpawnPosX = -12.5f,
                        SpawnPosY = 1f,
                        SpawnPosZ = 4.25f,
                        AttackType = PMAttackType.Super,
                    });
                }

                pframe.PlayerInputs.Add(pfi);
            }

            for (int i = 0; i < 6; i++)
            {
                pframe.PlayerStates.Add(new PMAuthoritativePlayerState
                {
                    BattleId = i,
                    PosX = -12.5f + i,
                    PosY = 1f,
                    PosZ = 4.25f - i,
                    Hp = 200 - i * 13,
                    IsDead = i == 5,
                    StateMask = 31u,
                    Mana = 90 - i * 5,
                    SuperEnergy = i * 40,
                });
            }

            pupdate.Frames.Add(pframe);
            pupdate.MoveAck = new PMMoveAckResult
            {
                BattleId = 0,
                AckedMoveFrame = 4321,
                AckGoodMove = false,
                CorrectPosX = 2.5f,
                CorrectPosY = 1f,
                CorrectPosZ = -4f,
                FrameDiscrepancy = -5,
                ResolvingFrameDiscrepancy = true,
                CorrectVelX = 0.25f,
                CorrectVelY = 0f,
                CorrectVelZ = -0.125f,
            };
            pupdate.HitEvents.Add(new PMHitEvent
            {
                AttackId = 777,
                AttackerBattleId = 0,
                VictimBattleId = 5,
                Damage = 37,
                HitFrameId = 1234,
                HitPosX = -10.5f,
                HitPosY = 1f,
                HitPosZ = 2.25f,
                IsKill = false,
            });
            pupdate.HitEvents.Add(new PMHitEvent
            {
                AttackId = 778,
                AttackerBattleId = 0,
                VictimBattleId = 5,
                Damage = 200,
                HitFrameId = 1234,
                IsKill = true,
            });
            pupdate.AttackAcks.Add(new PMAttackAck
            {
                BattlePlayerId = 0,
                AttackId = 777,
                Accepted = false,
                ManaAfter = 30,
                RejectReason = "mana",
                SuperEnergyAfter = 0,
            });
            p.BattleInfo.ServerUpdate = pupdate;

            Verify<MainPack, PMMainPack>(
                "战斗权威帧 MainPack",
                g,
                p,
                delegate (MainPack m) { return m.ToByteArray(); },
                delegate (PMMainPack m) { return PMMainPackSerializer.ToByteArray(m); },
                delegate (byte[] b) { return MainPack.Parser.ParseFrom(b); },
                delegate (byte[] b) { return PMMainPackSerializer.ParseFrom(b); });
        }

        // ---------------- 场景 4：标量边界 ----------------

        private static void ScenarioScalarEdges()
        {
            Section("场景 4：标量边界（负 int32 的 10 字节 varint / 负 float / NaN / -0.0f / 大 int64）");

            MainPack g = new MainPack();
            g.Timestamp = -1L;                    // 负 int64 → 10 字节
            g.RequestId = -7;                      // 负 int32 → 10 字节 varint
            g.Str = "";                            // 空串应省略
            g.BattleInfo = new BattleInfo
            {
                ServerFrame = -12345,              // 负 int32 → 10 字节 varint
                RandSeed = int.MinValue,           // 极值
            };
            g.BattleInfo.ClientInput = new BattleClientInput
            {
                BattlePlayerId = -1,
                ClientTick = int.MaxValue,
                AckedServerFrame = -2147483647,
                RttMs = 0,
            };
            g.BattleInfo.ServerUpdate = new BattleServerUpdate
            {
                MoveAck = new MoveAckResult
                {
                    BattleId = -1,
                    CorrectPosX = -1.5f,
                    CorrectPosY = float.NaN,        // NaN 必须写出且位模式一致
                    CorrectPosZ = -0.0f,            // -0.0f == 0f，两侧都应省略
                    FrameDiscrepancy = -5,
                    CorrectVelX = float.Epsilon,
                    CorrectVelY = float.MaxValue,
                    CorrectVelZ = float.MinValue,
                },
            };
            g.BattleInfo.ServerUpdate.Frames.Add(new BattleFrame
            {
                ServerFrame = 7,
                PlayerStates =
                {
                    new AuthoritativePlayerState
                    {
                        BattleId = 42,
                        PosX = float.NegativeInfinity,
                        PosY = float.PositiveInfinity,
                        StateMask = uint.MaxValue,
                        Hp = -1,
                    },
                },
            });

            PMMainPack p = new PMMainPack();
            p.Timestamp = -1L;
            p.RequestId = -7;
            p.Str = "";
            p.BattleInfo = new PMBattleInfo
            {
                ServerFrame = -12345,
                RandSeed = int.MinValue,
            };
            p.BattleInfo.ClientInput = new PMBattleClientInput
            {
                BattlePlayerId = -1,
                ClientTick = int.MaxValue,
                AckedServerFrame = -2147483647,
                RttMs = 0,
            };
            p.BattleInfo.ServerUpdate = new PMBattleServerUpdate
            {
                MoveAck = new PMMoveAckResult
                {
                    BattleId = -1,
                    CorrectPosX = -1.5f,
                    CorrectPosY = float.NaN,
                    CorrectPosZ = -0.0f,
                    FrameDiscrepancy = -5,
                    CorrectVelX = float.Epsilon,
                    CorrectVelY = float.MaxValue,
                    CorrectVelZ = float.MinValue,
                },
            };
            p.BattleInfo.ServerUpdate.Frames.Add(new PMBattleFrame
            {
                ServerFrame = 7,
            });
            p.BattleInfo.ServerUpdate.Frames[0].PlayerStates.Add(new PMAuthoritativePlayerState
            {
                BattleId = 42,
                PosX = float.NegativeInfinity,
                PosY = float.PositiveInfinity,
                StateMask = uint.MaxValue,
                Hp = -1,
            });

            Verify<MainPack, PMMainPack>(
                "标量边界 MainPack",
                g,
                p,
                delegate (MainPack m) { return m.ToByteArray(); },
                delegate (PMMainPack m) { return PMMainPackSerializer.ToByteArray(m); },
                delegate (byte[] b) { return MainPack.Parser.ParseFrom(b); },
                delegate (byte[] b) { return PMMainPackSerializer.ParseFrom(b); });
        }

        // ---------------- 场景 5：默认值省略 ----------------

        private static void ScenarioDefaultOmission()
        {
            Section("场景 5：默认值省略（显式写入零值应产出于全默认相同的字节）");

            MoveAckResult g = new MoveAckResult
            {
                BattleId = 0,
                AckedMoveFrame = 0,
                AckGoodMove = false,
                CorrectPosX = 0f,
                FrameDiscrepancy = 0,
                ResolvingFrameDiscrepancy = false,
                CorrectVelZ = 0f,
            };
            PMMoveAckResult p = new PMMoveAckResult
            {
                BattleId = 0,
                AckedMoveFrame = 0,
                AckGoodMove = false,
                CorrectPosX = 0f,
                FrameDiscrepancy = 0,
                ResolvingFrameDiscrepancy = false,
                CorrectVelZ = 0f,
            };

            Verify<MoveAckResult, PMMoveAckResult>(
                "全零 MoveAckResult",
                g,
                p,
                delegate (MoveAckResult m) { return m.ToByteArray(); },
                delegate (PMMoveAckResult m) { return PMMoveAckResultSerializer.ToByteArray(m); },
                delegate (byte[] b) { return MoveAckResult.Parser.ParseFrom(b); },
                delegate (byte[] b) { return PMMoveAckResultSerializer.ParseFrom(b); });
        }

        // ---------------- 场景 6：repeated 规模 ----------------

        private static void ScenarioRepeatedScaling()
        {
            Section("场景 6：repeated 元素数量（0 / 1 / 64，覆盖长度前缀与多次 tag）");

            int[] counts = new int[] { 0, 1, 64 };
            for (int c = 0; c < counts.Length; c++)
            {
                int count = counts[c];

                HitEvent[] gEvents = new HitEvent[count];
                PMHitEvent[] pEvents = new PMHitEvent[count];
                for (int i = 0; i < count; i++)
                {
                    gEvents[i] = new HitEvent
                    {
                        AttackId = i + 1,
                        AttackerBattleId = 0,
                        VictimBattleId = 1,
                        Damage = i,
                        HitFrameId = 100 + i,
                    };
                    pEvents[i] = new PMHitEvent
                    {
                        AttackId = i + 1,
                        AttackerBattleId = 0,
                        VictimBattleId = 1,
                        Damage = i,
                        HitFrameId = 100 + i,
                    };
                }

                MainPack g = new MainPack();
                g.Requestcode = RequestCode.Battle;
                g.BattleInfo = new BattleInfo();
                g.BattleInfo.ServerUpdate = new BattleServerUpdate();
                for (int i = 0; i < count; i++)
                {
                    g.BattleInfo.ServerUpdate.HitEvents.Add(gEvents[i]);
                }

                PMMainPack p = new PMMainPack();
                p.Requestcode = PMRequestCode.Battle;
                p.BattleInfo = new PMBattleInfo();
                p.BattleInfo.ServerUpdate = new PMBattleServerUpdate();
                for (int i = 0; i < count; i++)
                {
                    p.BattleInfo.ServerUpdate.HitEvents.Add(pEvents[i]);
                }

                Verify<MainPack, PMMainPack>(
                    "repeated HitEvent × " + count,
                    g,
                    p,
                    delegate (MainPack m) { return m.ToByteArray(); },
                    delegate (PMMainPack m) { return PMMainPackSerializer.ToByteArray(m); },
                    delegate (byte[] b) { return MainPack.Parser.ParseFrom(b); },
                    delegate (byte[] b) { return PMMainPackSerializer.ParseFrom(b); });
            }
        }

        // ---------------- 对照驱动 ----------------

        private static void Verify<TGoogle, TPm>(
            string name,
            TGoogle googleMessage,
            TPm pmMessage,
            Func<TGoogle, byte[]> googleWrite,
            Func<TPm, byte[]> pmWrite,
            Func<byte[], TGoogle> googleRead,
            Func<byte[], TPm> pmRead)
        {
            byte[] googleBytes = googleWrite(googleMessage);
            byte[] pmBytes = pmWrite(pmMessage);

            bool bytesMatch = CheckBytes(name + " 字节一致", googleBytes, pmBytes);
            if (!bytesMatch)
            {
                Dump(name, googleBytes, pmBytes);
                return;
            }

            // 检查 2：Google.Protobuf 解析 PMNet 产物后重序列化，应仍逐字节一致。
            //
            // 注意：这里不比较消息对象的 Equals，而比较重序列化字节。
            // 原因是一个已确认的 protobuf 固有性质：float 的负零在线上会丢失符号。
            // -0.0f == 0f 为真，因此两侧都把 -0.0f 当作默认值省略，解析回来得到 +0.0f；
            // 而 Google.Protobuf 的 Equals 对 float 用位比较（BitwiseSingleEqualityComparer），
            // 会把这种合法往返判为不等。字节级断言才是真正要保证的口径。
            TGoogle googleFromPm = googleRead(pmBytes);
            byte[] googleReround = googleWrite(googleFromPm);
            CheckBytes(name + " Google 重序列化一致", pmBytes, googleReround);

            // 检查 3：PMNet 解析 Google 产物并重序列化，应逐字节一致
            TPm pmFromGoogle = pmRead(googleBytes);
            byte[] pmRoundTrip = pmWrite(pmFromGoogle);
            CheckBytes(name + " PMNet 往返一致", googleBytes, pmRoundTrip);
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

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
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
            Console.WriteLine("    " + name + " 期望: " + Hex(expected));
            Console.WriteLine("    " + name + " 实际: " + Hex(actual));

            int limit = Math.Min(expected.Length, actual.Length);
            for (int i = 0; i < limit; i++)
            {
                if (expected[i] != actual[i])
                {
                    Console.WriteLine("    首个差异位于字节偏移 " + i
                        + "：期望 0x" + expected[i].ToString("X2") + "，实际 0x" + actual[i].ToString("X2"));
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

        // ---------------- 构造辅助 ----------------

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
    }
}
