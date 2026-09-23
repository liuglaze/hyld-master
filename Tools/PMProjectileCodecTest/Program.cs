// R5-B1 / T5B1 验收门禁（契约 Docs/plans/net-r5-projectile-contract.md §B1；冻结段「A3/B1 集成冻结」）。
//
// 这个文件只做一件事：把 PMProjectileCodec 的**线协议不变量**钉成可执行断言，并且刻意把
// 「正向」与「负向」分开：
//   [1] 正向：四类载荷（SpawnIntent / HitBatch / Snapshot / Decision）的**全字段非默认值**往返，
//       外加 `encode(decode(encode(x))) == encode(x)` 的字节级往返（证明「正反字段一致」。
//   [2] 负向：**测试侧独立手写字段级字节**（自己写 header/tag/varint/zigzag/float），
//       覆盖 required 缺失、字段乱序、标量重复、未知字段、错 wire type、尾部字节、
//       每个合法载荷的**逐字节截断**、长度前缀超限、NaN/Inf、Key/Origin/身份/帧域非法、
//       上行注入权威字段（spec/AuthorityNetId）、数组超 100、快照权威 ID=0、Decision Pending 伪终态。
//       手写字节意味着解码器有一个**独立对照**，而不是「拿 codec 自己的编码器造一个坏载荷」。
//   [3] 边界与隔离：0 候选 / 100 候选 / 100+100 快照目标；数组克隆隔离；Snapshot.Clone 深拷贝；
//       跨 kind 错用；载荷超 16384；并把「100 候选 > 生成桩 4096」这个**登记项**测出来。
//
// 诚实边界（不在本工程内）：
//   · 真实 PMR3 RPC / 生成桩 byte[] 传输 / Unity 宿主 / 双端实机 —— 属 B2/C 与 T45（PENDING_USER）；
//   · L0–L4 几何与预算（segment ≤ 20m、visualOffset ≤ 20m、空命中批、白名单、飞行预算）——
//     属 A2 的 PMProjectileValidator；本工程只测 codec 的「可表示 / 有限 / 有界 / 身份自洽」。
//
// 运行：dotnet Tools/PMProjectileCodecTest/bin/Release/net8.0/PMProjectileCodecTest.dll
// 退出码：0 = 全部通过；1 = 存在失败

using System;
using System.Collections.Generic;
using System.Text;
using PMNet;
using PMNet.Mover;
using PMNet.Projectile;

namespace PMProjectileCodecTest
{
    internal static class Program
    {
        private const uint Epoch = 0x4A01u;
        private const uint Owner = 0x777u;
        private const uint Proj = 5u;

        private static int _passed;
        private static readonly List<string> _failures = new List<string>();

        private static int Main()
        {
            Console.WriteLine("=== PMProjectileCodecTest（R5-B1 / T5B1）===");
            Console.WriteLine("Unity 语言面由 Tools/PMProjectileCodecCheck（netstandard2.0 + C#7.3）单独守；本工程跑真实字节。");
            Console.WriteLine();

            Section("A. SpawnIntent：全字段往返 + 边界 + 字节级往返", TestSpawnRoundTrip);
            Section("B. HitBatch：全字段往返 + 0/100 候选 + 字节级往返", TestHitBatchRoundTrip);
            Section("C. Snapshot：State+Spec 全字段往返 + 数组边界 + 字节级往返", TestSnapshotRoundTrip);
            Section("D. Decision：终态往返 + Pending/未知必拒", TestDecisionRoundTrip);
            Section("E. 必填字段逐个缺失必拒", TestMissingRequired);
            Section("F. 乱序 / 重复 / 未知字段 / 错 wire / 尾部字节", TestOrderDuplicateUnknownWire);
            Section("G. 逐字节截断：必须 false 且不抛异常", TestTruncation);
            Section("H. 长度前缀超限：分配前拒绝", TestLengthPrefix);
            Section("I. NaN/Inf 与非规范值（yaw/predictionMs/rewind）", TestNonFiniteAndRanges);
            Section("J. Key / Origin / 身份 / 帧域非法", TestIdentityAndOrigin);
            Section("K. 上行注入权威字段必拒", TestUplinkInjection);
            Section("L. 数组克隆隔离 / Snapshot.Clone / 跨 kind 错用", TestArrayIsolation);
            Section("M. 载荷上限与「生成桩 4096」登记项", TestSizeLimits);
            Section("N. 独立字面 golden bytes / 裁决激活 ID 随 origin / 读端边界", TestGoldenBytesAndReaderEdges);

            Console.WriteLine();
            Console.WriteLine("==================================================");
            Console.WriteLine("通过 " + _passed + " 项，失败 " + _failures.Count + " 项。");
            if (_failures.Count > 0)
            {
                Console.WriteLine("失败明细：");
                for (int i = 0; i < _failures.Count; i++)
                {
                    Console.WriteLine("  [FAIL] " + _failures[i]);
                }

                Console.WriteLine("结果：FAILED");
                return 1;
            }

            Console.WriteLine("结果：PASS");
            return 0;
        }

        // =================================================================================
        //  断言
        // =================================================================================

        private static void Section(string name, Action body)
        {
            Console.WriteLine("── " + name);
            int before = _failures.Count;
            try
            {
                body();
            }
            catch (Exception ex)
            {
                _failures.Add(name + "：抛出 " + ex.GetType().Name + "：" + ex.Message);
                Console.WriteLine("      " + ex.GetType().Name + "：" + ex.Message);
                Console.WriteLine("      " + ex.StackTrace);
            }

            Console.WriteLine("   [" + (before == _failures.Count ? "OK" : "FAIL") + "] " + name);
        }

        private static void Check(bool ok, string label)
        {
            if (ok)
            {
                _passed++;
            }
            else
            {
                _failures.Add(label);
                Console.WriteLine("      FAIL " + label);
            }
        }

        private static void CheckTrue(bool value, string label)
        {
            Check(value, label);
        }

        private static void CheckEq(uint actual, uint expected, string label)
        {
            Check(actual == expected, label + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckEq(int actual, int expected, string label)
        {
            Check(actual == expected, label + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckEq(long actual, long expected, string label)
        {
            Check(actual == expected, label + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckClose(float actual, float expected, string label)
        {
            Check(actual == expected, label + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckClose(double actual, double expected, string label)
        {
            Check(actual == expected, label + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckVec(PMVector3 actual, PMVector3 expected, string label)
        {
            Check(actual.X == expected.X && actual.Y == expected.Y && actual.Z == expected.Z,
                label + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckKey(PMProjectileKey actual, PMProjectileKey expected, string label)
        {
            Check(actual == expected, label + "（期望 " + Key(expected) + "，实际 " + Key(actual) + "）");
        }

        private static void CheckContains(string actual, string needle, string label)
        {
            bool ok = actual != null && needle != null && actual.IndexOf(needle, StringComparison.Ordinal) >= 0;
            Check(ok, label + "（实际错误：'" + actual + "'）");
        }

        private static string Key(PMProjectileKey key)
        {
            return "(" + key.Epoch + "/" + key.OwnerNetId + "/" + key.ProjectileId + "/" + key.Origin + ")";
        }

        private static void CheckBytesEqual(byte[] actual, byte[] expected, string label)
        {
            bool ok = actual != null && expected != null && actual.Length == expected.Length;
            if (ok)
            {
                for (int i = 0; i < actual.Length; i++)
                {
                    if (actual[i] != expected[i])
                    {
                        ok = false;
                        break;
                    }
                }
            }

            int len = actual == null ? -1 : actual.Length;
            int exp = expected == null ? -1 : expected.Length;
            Check(ok, label + "（期望 " + exp + " 字节，实际 " + len + " 字节）");
        }

        // =================================================================================
        //  测试侧独立手写的字段级字节（不经过 PMProjectileCodec）
        // =================================================================================

        private static byte[] Header(int kind, uint epoch, uint owner, uint proj, int origin)
        {
            PMNetWriter w = new PMNetWriter(32);
            w.WriteTag(1, PMWireType.Varint);
            w.WriteVarint((ulong)kind);
            w.WriteTag(2, PMWireType.Varint);
            w.WriteVarint(1UL);
            w.WriteTag(3, PMWireType.Varint);
            w.WriteVarint(epoch);
            w.WriteTag(4, PMWireType.Varint);
            w.WriteVarint(owner);
            w.WriteTag(5, PMWireType.Varint);
            w.WriteVarint(proj);
            w.WriteTag(6, PMWireType.Varint);
            w.WriteVarint((ulong)origin);
            return w.ToArray();
        }

        private static byte[] FVarint(int field, ulong value)
        {
            PMNetWriter w = new PMNetWriter(16);
            w.WriteTag(field, PMWireType.Varint);
            w.WriteVarint(value);
            return w.ToArray();
        }

        private static byte[] FSInt64(int field, long value)
        {
            PMNetWriter w = new PMNetWriter(16);
            w.WriteTag(field, PMWireType.Varint);
            w.WriteSInt64(value);
            return w.ToArray();
        }

        private static byte[] FFloat(int field, float value)
        {
            PMNetWriter w = new PMNetWriter(8);
            w.WriteTag(field, PMWireType.Fixed32);
            w.WriteFloat(value);
            return w.ToArray();
        }

        private static byte[] FDouble(int field, double value)
        {
            PMNetWriter w = new PMNetWriter(12);
            w.WriteTag(field, PMWireType.Fixed64);
            w.WriteDouble(value);
            return w.ToArray();
        }

        private static byte[] FBool(int field, bool value)
        {
            return FVarint(field, value ? 1UL : 0UL);
        }

        private static byte[] FBytes(int field, byte[] body)
        {
            PMNetWriter w = new PMNetWriter(body == null ? 8 : body.Length + 8);
            w.WriteTag(field, PMWireType.LengthDelimited);
            w.WriteBytesValue(body);
            return w.ToArray();
        }

        private static byte[] FString(int field, string value)
        {
            PMNetWriter w = new PMNetWriter(32);
            w.WriteTag(field, PMWireType.LengthDelimited);
            w.WriteStringValue(value);
            return w.ToArray();
        }

        private static byte[] FRawTag(int field, PMWireType wire)
        {
            PMNetWriter w = new PMNetWriter(8);
            w.WriteTag(field, wire);
            return w.ToArray();
        }

        private static byte[] Concat(params byte[][] parts)
        {
            int total = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] != null)
                {
                    total += parts[i].Length;
                }
            }

            byte[] result = new byte[total];
            int at = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == null)
                {
                    continue;
                }

                Buffer.BlockCopy(parts[i], 0, result, at, parts[i].Length);
                at += parts[i].Length;
            }

            return result;
        }

        private static byte[] SubPayload(List<byte[]> fields)
        {
            byte[][] parts = new byte[fields.Count][];
            for (int i = 0; i < fields.Count; i++)
            {
                parts[i] = fields[i];
            }

            return Concat(parts);
        }

        private static byte[] Payload(int kind, params byte[][] fields)
        {
            return Payload(kind, (int)PMProjectileOrigin.ClientPredicted, fields);
        }

        private static byte[] Payload(int kind, int origin, params byte[][] fields)
        {
            return Payload(kind, Epoch, Owner, Proj, origin, fields);
        }

        private static byte[] Payload(int kind, uint epoch, uint owner, uint proj, int origin, params byte[][] fields)
        {
            byte[][] parts = new byte[fields.Length + 1][];
            parts[0] = Header(kind, epoch, owner, proj, origin);
            for (int i = 0; i < fields.Length; i++)
            {
                parts[i + 1] = fields[i];
            }

            return Concat(parts);
        }

        // ---- SpawnIntent（Kind=1）字段集合 ----

        private static List<byte[]> SpawnFields()
        {
            List<byte[]> f = new List<byte[]>(9);
            f.Add(FVarint(10, 7u));        // activationId
            f.Add(FFloat(11, 1.5f));       // position.x
            f.Add(FFloat(12, -2.25f));     // position.y
            f.Add(FFloat(13, 3.75f));      // position.z
            f.Add(FFloat(14, 0f));         // direction.x
            f.Add(FFloat(15, 0.5f));       // direction.y
            f.Add(FFloat(16, 1f));         // direction.z
            f.Add(FFloat(17, 123.5f));     // yaw
            f.Add(FVarint(18, 250u));      // predictionMs
            return f;
        }

        private static readonly int[] SpawnFieldNumbers = { 10, 11, 12, 13, 14, 15, 16, 17, 18 };

        // ---- HitBatch（Kind=2）字段集合 ----

        private static byte[] CandidateBytes(uint netId, uint stream, int domain, long frame,
                                             float ix, float iy, float iz, float ox, float oy, float oz)
        {
            List<byte[]> f = new List<byte[]>(10);
            f.Add(FVarint(1, netId));
            f.Add(FVarint(2, stream));
            f.Add(FVarint(3, (ulong)domain));
            f.Add(FSInt64(4, frame));
            f.Add(FFloat(5, ix));
            f.Add(FFloat(6, iy));
            f.Add(FFloat(7, iz));
            f.Add(FFloat(8, ox));
            f.Add(FFloat(9, oy));
            f.Add(FFloat(10, oz));
            return SubPayload(f);
        }

        private static List<byte[]> HitFields(int candidateCount, int rewindMs)
        {
            List<byte[]> f = new List<byte[]>(7 + candidateCount);
            f.Add(FFloat(10, -1f));        // previousPosition.x
            f.Add(FFloat(11, -2f));        // previousPosition.y
            f.Add(FFloat(12, -3f));        // previousPosition.z
            f.Add(FFloat(13, 4f));         // hitPosition.x
            f.Add(FFloat(14, 5f));         // hitPosition.y
            f.Add(FFloat(15, 6f));         // hitPosition.z
            f.Add(FVarint(16, (ulong)rewindMs));
            for (int i = 0; i < candidateCount; i++)
            {
                f.Add(FBytes(17, CandidateBytes(100u + (uint)i, 3u, 2, 500L + i,
                    1f + i, 2f, 3f, 0.01f, 0.02f, 0.03f)));
            }

            return f;
        }

        private static readonly int[] HitFieldNumbers = { 10, 11, 12, 13, 14, 15, 16 };

        // ---- Snapshot（Kind=3）字段集合 ----

        private static byte[] SpecBytes(float speed, float radius, int lifetime, int delay,
                                        bool stopOnHit, bool hideOnStop, bool skipTrajectory)
        {
            List<byte[]> f = new List<byte[]>(7);
            f.Add(FFloat(1, speed));
            f.Add(FFloat(2, radius));
            f.Add(FVarint(3, (ulong)lifetime));
            f.Add(FVarint(4, (ulong)delay));
            f.Add(FBool(5, stopOnHit));
            f.Add(FBool(6, hideOnStop));
            f.Add(FBool(7, skipTrajectory));
            return SubPayload(f);
        }

        private static List<byte[]> SnapshotFields(uint[] hits, uint[] allowed)
        {
            List<byte[]> f = new List<byte[]>(26);
            f.Add(FVarint(10, 42u));            // authorityNetId
            f.Add(FVarint(11, 0u));             // activationId（ServerDirect 允许 0）
            f.Add(FFloat(12, 1f));              // spawnPosition.x
            f.Add(FFloat(13, 2f));              // spawnPosition.y
            f.Add(FFloat(14, 3f));              // spawnPosition.z
            f.Add(FFloat(15, -1f));             // previousPosition.x
            f.Add(FFloat(16, -2f));             // previousPosition.y
            f.Add(FFloat(17, -3f));             // previousPosition.z
            f.Add(FFloat(18, 4f));              // position.x
            f.Add(FFloat(19, 5f));              // position.y
            f.Add(FFloat(20, 6f));              // position.z
            f.Add(FFloat(21, 0.5f));            // velocity.x
            f.Add(FFloat(22, -0.25f));          // velocity.y
            f.Add(FFloat(23, 7f));              // velocity.z
            f.Add(FFloat(24, 44.5f));           // yaw
            f.Add(FDouble(25, 1234.5));         // moveTimeMs
            f.Add(FBool(26, true));             // stopped
            f.Add(FBool(27, false));            // hidden
            f.Add(FBool(28, true));             // takenOver
            f.Add(FDouble(29, 9000.5));         // stopWallTimeMs
            f.Add(FDouble(30, 33.5));           // timeAfterStoppedMs
            f.Add(FDouble(31, 10000.5));        // tombstoneUntilMs

            if (hits != null)
            {
                for (int i = 0; i < hits.Length; i++)
                {
                    f.Add(FVarint(32, hits[i]));
                }
            }

            if (allowed != null)
            {
                for (int i = 0; i < allowed.Length; i++)
                {
                    f.Add(FVarint(33, allowed[i]));
                }
            }

            f.Add(FBytes(34, SpecBytes(12.5f, 0.35f, 2800, 120, true, true, false)));
            return f;
        }

        private static readonly int[] SnapshotFieldNumbers =
        {
            10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 34,
        };

        /// <summary>
        /// 快照载荷（头部 origin 固定 ServerDirect：权威快照总是由 DS 发出，
        /// 且这里的状态带 AuthorityNetId/ActivationId=0，与「服务端直创」语义一致）。
        /// </summary>
        private static byte[] SnapshotPayload(uint[] hits, uint[] allowed)
        {
            return Payload(3, Epoch, Owner, Proj, (int)PMProjectileOrigin.ServerDirect,
                SnapshotFields(hits, allowed).ToArray());
        }

        // ---- Decision（Kind=4）字段集合 ----

        private static List<byte[]> DecisionFields(uint activation, int result, string reason)
        {
            List<byte[]> f = new List<byte[]>(3);
            f.Add(FVarint(10, activation));
            f.Add(FVarint(11, (ulong)result));
            f.Add(FString(12, reason));
            return f;
        }

        private static readonly int[] DecisionFieldNumbers = { 10, 11, 12 };

        // =================================================================================
        //  A. SpawnIntent
        // =================================================================================

        private static void TestSpawnRoundTrip()
        {
            PMProjectileSpawnIntent intent = new PMProjectileSpawnIntent();
            intent.Key = new PMProjectileKey(Epoch, Owner, Proj, PMProjectileOrigin.ClientPredicted);
            intent.ActivationId = 7u;
            intent.Position = new PMVector3(1.5f, -2.25f, 3.75f);
            intent.Direction = new PMVector3(0f, 0.5f, 1f);
            intent.Yaw = 123.5f;
            intent.PredictionMs = 250;

            byte[] payload;
            string error;
            CheckTrue(PMProjectileCodec.TryEncodeSpawnIntent(intent, out payload, out error),
                "SpawnIntent 全字段非默认值可编码：" + error);
            if (payload == null)
            {
                return;
            }

            Console.WriteLine("      spawn 长度 = " + payload.Length + " 字节");
            CheckTrue(payload.Length <= PMProjectileCodec.MaxPayloadBytes, "SpawnIntent 长度 <= 16384");

            PMProjectileSpawnIntent back;
            CheckTrue(PMProjectileCodec.TryDecodeSpawnIntent(payload, 0, payload.Length, out back, out error),
                "SpawnIntent 可解码：" + error);
            if (back == null)
            {
                return;
            }

            CheckKey(back.Key, intent.Key, "SpawnIntent Key 往返");
            CheckEq(back.ActivationId, 7u, "SpawnIntent ActivationId 往返");
            CheckVec(back.Position, intent.Position, "SpawnIntent Position 往返");
            CheckVec(back.Direction, intent.Direction, "SpawnIntent Direction 往返");
            CheckClose(back.Yaw, 123.5f, "SpawnIntent Yaw 往返");
            CheckEq(back.PredictionMs, 250, "SpawnIntent PredictionMs 往返");

            byte[] again;
            CheckTrue(PMProjectileCodec.TryEncodeSpawnIntent(back, out again, out error),
                "SpawnIntent 再编码：" + error);
            CheckBytesEqual(again, payload, "SpawnIntent encode(decode(encode(x))) 字节一致");

            // 手写字节与 codec 编码必须一致（线格式的独立对照）。
            CheckBytesEqual(Payload(1, SpawnFields().ToArray()), payload, "SpawnIntent 手写字节 == codec 字节");

            // 边界：predictionMs = 0 / 500，yaw = 0 / 359.999
            CheckTrue(TrySpawn(0, 0f), "SpawnIntent predictionMs=0 且 yaw=0 可往返");
            CheckTrue(TrySpawn(500, 359.999f), "SpawnIntent predictionMs=500 且 yaw=359.999 可往返");
            CheckTrue(TrySpawn(0, 0.5f), "SpawnIntent yaw=0.5 可往返");

            // 非规范 yaw / 越界 predictionMs 在编解码两侧都必须拒。
            PMProjectileSpawnIntent bad = intent.Clone();
            bad.Yaw = 360f;
            byte[] ignored;
            CheckTrue(!PMProjectileCodec.TryEncodeSpawnIntent(bad, out ignored, out error),
                "SpawnIntent encode 拒绝 yaw=360（非规范）");
            CheckTrue(!TrySpawn(0, 360f), "SpawnIntent decode 拒绝 yaw=360");
            CheckTrue(!TrySpawn(0, -1f), "SpawnIntent decode 拒绝 yaw=-1");

            bad = intent.Clone();
            bad.PredictionMs = 501;
            CheckTrue(!PMProjectileCodec.TryEncodeSpawnIntent(bad, out ignored, out error),
                "SpawnIntent encode 拒绝 predictionMs=501");
            CheckTrue(!TrySpawn(501, 0f), "SpawnIntent decode 拒绝 predictionMs=501");

            bad = intent.Clone();
            bad.Direction = PMVector3.Zero;
            CheckTrue(!PMProjectileCodec.TryEncodeSpawnIntent(bad, out ignored, out error),
                "SpawnIntent encode 拒绝零方向");
            CheckSpawnDirectionRejected(0f, 0f, 0f, "方向为零向量");

            bad = intent.Clone();
            bad.ActivationId = 0u;
            CheckTrue(!PMProjectileCodec.TryEncodeSpawnIntent(bad, out ignored, out error),
                "SpawnIntent encode 拒绝 ActivationId=0（0 只允许可信 ServerDirect）");
        }

        private static bool TrySpawn(int predictionMs, float yaw)
        {
            List<byte[]> f = SpawnFields();
            f[7] = FFloat(17, yaw);
            f[8] = FVarint(18, (ulong)predictionMs);
            byte[] payload = Payload(1, f.ToArray());
            PMProjectileSpawnIntent intent;
            string error;
            return PMProjectileCodec.TryDecodeSpawnIntent(payload, 0, payload.Length, out intent, out error);
        }

        private static void CheckSpawnDirectionRejected(float x, float y, float z, string label)
        {
            List<byte[]> f = SpawnFields();
            f[4] = FFloat(14, x);
            f[5] = FFloat(15, y);
            f[6] = FFloat(16, z);
            byte[] payload = Payload(1, f.ToArray());
            PMProjectileSpawnIntent doc;
            string error;
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(payload, 0, payload.Length, out doc, out error),
                "SpawnIntent decode 拒绝 " + label);
        }

        // =================================================================================
        //  B. HitBatch
        // =================================================================================

        private static void TestHitBatchRoundTrip()
        {
            byte[] payload = Payload(2, HitFields(3, 350).ToArray());
            PMProjectileHitBatch batch;
            string error;
            CheckTrue(PMProjectileCodec.TryDecodeHitBatch(payload, 0, payload.Length, out batch, out error),
                "HitBatch（3 候选）可解码：" + error);
            if (batch == null)
            {
                return;
            }

            CheckKey(batch.Key, new PMProjectileKey(Epoch, Owner, Proj, PMProjectileOrigin.ClientPredicted),
                "HitBatch Key 往返");
            CheckVec(batch.PreviousPosition, new PMVector3(-1f, -2f, -3f), "HitBatch PreviousPosition 往返");
            CheckVec(batch.HitPosition, new PMVector3(4f, 5f, 6f), "HitBatch HitPosition 往返");
            CheckEq(batch.RewindMs, 350, "HitBatch RewindMs 往返");
            CheckEq(batch.Targets.Length, 3, "HitBatch 候选数往返");
            CheckEq(batch.Targets[1].TargetNetId, 101u, "HitBatch[1].TargetNetId 往返");
            CheckEq(batch.Targets[1].TargetStreamVersion, 3u, "HitBatch[1].TargetStreamVersion 往返");
            CheckEq((int)batch.Targets[1].TargetServerFrame.Domain, 2, "HitBatch[1].TargetServerFrame 域 = AuthorityServer");
            CheckEq(batch.Targets[1].TargetServerFrame.Value, 501L, "HitBatch[1].TargetServerFrame 值往返");
            CheckVec(batch.Targets[1].ImpactPoint, new PMVector3(2f, 2f, 3f), "HitBatch[1].ImpactPoint 往返");
            CheckVec(batch.Targets[1].VisualOffset, new PMVector3(0.01f, 0.02f, 0.03f), "HitBatch[1].VisualOffset 往返");

            // codec 编码与手写字节一致，再做字节级往返。
            PMProjectileHitBatch clone = batch.Clone();
            byte[] encoded;
            CheckTrue(PMProjectileCodec.TryEncodeHitBatch(clone, out encoded, out error),
                "HitBatch 可编码：" + error);
            CheckBytesEqual(encoded, payload, "HitBatch codec 字节 == 手写字节");
            CheckBytesEqual(EncodeBatch(encoded), encoded, "HitBatch encode(decode(encode(x))) 字节一致");

            // 0 候选：codec 层合法（空批由 L0 以 NoTargets 拒绝），必须返回非 null 空数组。
            byte[] empty = Payload(2, HitFields(0, 0).ToArray());
            PMProjectileHitBatch emptyBatch;
            CheckTrue(PMProjectileCodec.TryDecodeHitBatch(empty, 0, empty.Length, out emptyBatch, out error),
                "HitBatch 0 候选可解码（空批由 L0 判定）：" + error);
            if (emptyBatch != null)
            {
                CheckTrue(emptyBatch.Targets != null && emptyBatch.Targets.Length == 0,
                    "HitBatch 0 候选返回非 null 空数组");
            }

            CheckTrue(!TryEncodeHit(-1), "HitBatch encode 拒绝 rewind = -1");
            CheckTrue(!TryDecodeHitRewind(-1), "HitBatch decode 拒绝 rewind = -1");
            CheckTrue(TryDecodeHitRewind(0), "HitBatch rewind = 0 可解码");
            CheckTrue(TryDecodeHitRewind(500), "HitBatch rewind = 500 可解码");
            CheckTrue(TryDecodeHitRewind(1001), "HitBatch rewind = 1001 仍可解码（阈值只供 Report，不截断）");

            // 100 候选上限。
            byte[] hundred = Payload(2, HitFields(100, 100).ToArray());
            PMProjectileHitBatch big;
            CheckTrue(PMProjectileCodec.TryDecodeHitBatch(hundred, 0, hundred.Length, out big, out error),
                "HitBatch 100 候选可解码：" + error);
            if (big != null)
            {
                CheckEq(big.Targets.Length, 100, "HitBatch 100 候选计数");
            }

            Console.WriteLine("      hit 100 候选长度 = " + hundred.Length + " 字节");

            byte[] overflow = Payload(2, HitFields(101, 100).ToArray());
            PMProjectileHitBatch overflowBatch;
            CheckTrue(!PMProjectileCodec.TryDecodeHitBatch(overflow, 0, overflow.Length, out overflowBatch, out error),
                "HitBatch 101 候选必拒");
            CheckTrue(overflowBatch == null, "HitBatch 101 候选不返回部分对象");
            CheckContains(error, "超过上限", "HitBatch 101 候选由「上限」门拒绝");

            PMProjectileHitBatch tooMany = new PMProjectileHitBatch();
            tooMany.Key = new PMProjectileKey(Epoch, Owner, Proj, PMProjectileOrigin.ClientPredicted);
            tooMany.Targets = new PMProjectileHitCandidate[101];
            byte[] ignored;
            CheckTrue(!PMProjectileCodec.TryEncodeHitBatch(tooMany, out ignored, out error),
                "HitBatch encode 拒绝 101 候选");
        }

        private static byte[] EncodeBatch(byte[] payload)
        {
            PMProjectileHitBatch batch;
            string error;
            byte[] encoded;
            PMProjectileCodec.TryDecodeHitBatch(payload, 0, payload.Length, out batch, out error);
            PMProjectileCodec.TryEncodeHitBatch(batch, out encoded, out error);
            return encoded;
        }

        private static bool TryEncodeHit(int rewindMs)
        {
            PMProjectileHitBatch batch = new PMProjectileHitBatch();
            batch.Key = new PMProjectileKey(Epoch, Owner, Proj, PMProjectileOrigin.ClientPredicted);
            batch.RewindMs = rewindMs;
            batch.Targets = new PMProjectileHitCandidate[1];
            batch.Targets[0].TargetNetId = 5u;
            batch.Targets[0].TargetStreamVersion = 1u;
            batch.Targets[0].TargetServerFrame = new PMFrameId(PMFrameDomain.AuthorityServer, 100L);
            byte[] ignored;
            string error;
            return PMProjectileCodec.TryEncodeHitBatch(batch, out ignored, out error);
        }

        private static bool TryDecodeHitRewind(int rewindMs)
        {
            List<byte[]> f = HitFields(1, rewindMs);
            byte[] payload = Payload(2, f.ToArray());
            PMProjectileHitBatch batch;
            string error;
            return PMProjectileCodec.TryDecodeHitBatch(payload, 0, payload.Length, out batch, out error);
        }

        // =================================================================================
        //  C. Snapshot
        // =================================================================================

        private static void TestSnapshotRoundTrip()
        {
            uint[] hits = new uint[] { 11u, 22u };
            uint[] allowed = new uint[] { 33u, 44u, 55u };
            byte[] payload = SnapshotPayload(hits, allowed);

            PMProjectileSnapshot snapshot;
            string error;
            CheckTrue(PMProjectileCodec.TryDecodeSnapshot(payload, 0, payload.Length, out snapshot, out error),
                "Snapshot 全字段可解码：" + error);
            if (snapshot == null)
            {
                return;
            }

            Console.WriteLine("      snapshot 长度 = " + payload.Length + " 字节");

            CheckKey(snapshot.State.Key, new PMProjectileKey(Epoch, Owner, Proj, PMProjectileOrigin.ServerDirect),
                "Snapshot Key 往返（ServerDirect 头部）");
            CheckEq(snapshot.State.AuthorityNetId, 42u, "Snapshot AuthorityNetId 往返");
            CheckEq(snapshot.State.ActivationId, 0u, "Snapshot ActivationId=0 往返（ServerDirect 允许）");
            CheckVec(snapshot.State.SpawnPosition, new PMVector3(1f, 2f, 3f), "Snapshot SpawnPosition 往返");
            CheckVec(snapshot.State.PreviousPosition, new PMVector3(-1f, -2f, -3f), "Snapshot PreviousPosition 往返");
            CheckVec(snapshot.State.Position, new PMVector3(4f, 5f, 6f), "Snapshot Position 往返");
            CheckVec(snapshot.State.Velocity, new PMVector3(0.5f, -0.25f, 7f), "Snapshot Velocity 往返");
            CheckClose(snapshot.State.Yaw, 44.5f, "Snapshot Yaw 往返");
            CheckClose(snapshot.State.MoveTimeMs, 1234.5, "Snapshot MoveTimeMs 往返");
            CheckTrue(snapshot.State.Stopped, "Snapshot Stopped 往返");
            CheckTrue(!snapshot.State.Hidden, "Snapshot Hidden 往返");
            CheckTrue(snapshot.State.TakenOver, "Snapshot TakenOver 往返");
            CheckClose(snapshot.State.StopWallTimeMs, 9000.5, "Snapshot StopWallTimeMs 往返");
            CheckClose(snapshot.State.TimeAfterStoppedMs, 33.5, "Snapshot TimeAfterStoppedMs 往返");
            CheckClose(snapshot.State.TombstoneUntilMs, 10000.5, "Snapshot TombstoneUntilMs 往返");
            CheckEq(snapshot.State.HitTargets.Length, 2, "Snapshot HitTargets 长度往返");
            CheckEq(snapshot.State.HitTargets[1], 22u, "Snapshot HitTargets[1] 往返");
            CheckEq(snapshot.State.AllowedTargets.Length, 3, "Snapshot AllowedTargets 长度往返");
            CheckEq(snapshot.State.AllowedTargets[2], 55u, "Snapshot AllowedTargets[2] 往返");
            CheckClose(snapshot.Spec.SpeedMps, 12.5f, "Snapshot Spec.SpeedMps 往返");
            CheckClose(snapshot.Spec.RadiusM, 0.35f, "Snapshot Spec.RadiusM 往返");
            CheckEq(snapshot.Spec.LifetimeMs, 2800, "Snapshot Spec.LifetimeMs 往返");
            CheckEq(snapshot.Spec.DelayDestroyMs, 120, "Snapshot Spec.DelayDestroyMs 往返");
            CheckTrue(snapshot.Spec.StopOnHit, "Snapshot Spec.StopOnHit 往返");
            CheckTrue(snapshot.Spec.HideOnStop, "Snapshot Spec.HideOnStop 往返");
            CheckTrue(!snapshot.Spec.SkipFlyingTrajectoryValidation, "Snapshot Spec.SkipFlyingTrajectoryValidation 往返");

            // codec 编码 == 手写字节；字节级往返。
            byte[] encoded;
            CheckTrue(PMProjectileCodec.TryEncodeSnapshot(snapshot, out encoded, out error),
                "Snapshot 可编码：" + error);
            CheckBytesEqual(encoded, payload, "Snapshot codec 字节 == 手写字节");
            byte[] again;
            PMProjectileSnapshot reDecoded;
            PMProjectileCodec.TryDecodeSnapshot(encoded, 0, encoded.Length, out reDecoded, out error);
            PMProjectileCodec.TryEncodeSnapshot(reDecoded, out again, out error);
            CheckBytesEqual(again, encoded, "Snapshot encode(decode(encode(x))) 字节一致");

            // 数组边界：空 / 100 / 100。
            uint[] hundred = new uint[100];
            for (int i = 0; i < 100; i++)
            {
                hundred[i] = (uint)(1000 + i);
            }

            byte[] big = SnapshotPayload(hundred, hundred);
            PMProjectileSnapshot bigSnapshot;
            CheckTrue(PMProjectileCodec.TryDecodeSnapshot(big, 0, big.Length, out bigSnapshot, out error),
                "Snapshot 100+100 目标可解码：" + error);
            if (bigSnapshot != null)
            {
                CheckEq(bigSnapshot.State.HitTargets.Length, 100, "Snapshot HitTargets 100");
                CheckEq(bigSnapshot.State.AllowedTargets.Length, 100, "Snapshot AllowedTargets 100");
            }

            Console.WriteLine("      snapshot 100+100 目标长度 = " + big.Length + " 字节");

            uint[] oneOhOne = new uint[101];
            for (int i = 0; i < 101; i++)
            {
                oneOhOne[i] = (uint)(2000 + i);
            }

            byte[] tooWide = SnapshotPayload(oneOhOne, null);
            PMProjectileSnapshot rejected;
            CheckTrue(!PMProjectileCodec.TryDecodeSnapshot(tooWide, 0, tooWide.Length, out rejected, out error),
                "Snapshot HitTargets 101 必拒");
            CheckTrue(rejected == null, "Snapshot 101 目标不返回部分对象");
            CheckContains(error, "超过上限", "Snapshot 101 目标由「上限」门拒绝");

            tooWide = SnapshotPayload(null, oneOhOne);
            CheckTrue(!PMProjectileCodec.TryDecodeSnapshot(tooWide, 0, tooWide.Length, out rejected, out error),
                "Snapshot AllowedTargets 101 必拒");

            // 空数组往返。
            byte[] emptyArrays = SnapshotPayload(null, null);
            PMProjectileSnapshot emptySnapshot;
            CheckTrue(PMProjectileCodec.TryDecodeSnapshot(emptyArrays, 0, emptyArrays.Length, out emptySnapshot, out error),
                "Snapshot 空目标数组可解码：" + error);
            if (emptySnapshot != null)
            {
                CheckEq(emptySnapshot.State.HitTargets.Length, 0, "Snapshot 空 HitTargets");
                CheckEq(emptySnapshot.State.AllowedTargets.Length, 0, "Snapshot 空 AllowedTargets");
            }

            // encode 侧上限。
            PMProjectileState wide = new PMProjectileState();
            wide.Key = new PMProjectileKey(Epoch, Owner, Proj, PMProjectileOrigin.ServerDirect);
            wide.AuthorityNetId = 42u;
            wide.HitTargets = oneOhOne;
            PMProjectileSnapshot doc = new PMProjectileSnapshot();
            doc.State = wide;
            doc.Spec = new PMProjectileSpec();
            byte[] ignored;
            CheckTrue(!PMProjectileCodec.TryEncodeSnapshot(doc, out ignored, out error),
                "Snapshot encode 拒绝 101 HitTargets");

            // 权威 ID = 0 必须拒（双向）。
            wide.HitTargets = null;
            wide.AuthorityNetId = 0u;
            CheckTrue(!PMProjectileCodec.TryEncodeSnapshot(doc, out ignored, out error),
                "Snapshot encode 拒绝 AuthorityNetId=0");
            CheckContains(error, "AuthorityNetId", "Snapshot encode 的 AuthorityNetId=0 由身份门拒绝");

            List<byte[]> zeroAuth = SnapshotFields(null, null);
            zeroAuth[0] = FVarint(10, 0u);
            byte[] zeroAuthPayload = Payload(3, zeroAuth.ToArray());
            CheckTrue(!PMProjectileCodec.TryDecodeSnapshot(zeroAuthPayload, 0, zeroAuthPayload.Length, out rejected, out error),
                "Snapshot decode 拒绝 AuthorityNetId=0");
            CheckContains(error, "AuthorityNetId", "Snapshot decode 的 AuthorityNetId=0 由身份门拒绝");
        }

        // =================================================================================
        //  D. Decision
        // =================================================================================

        private static void TestDecisionRoundTrip()
        {
            byte[] rejected = Payload(4, DecisionFields(9u, 2, "geometry-out-of-budget").ToArray());
            PMProjectileDecision decision;
            string error;
            CheckTrue(PMProjectileCodec.TryDecodeDecision(rejected, 0, rejected.Length, out decision, out error),
                "Decision（Rejected）可解码：" + error);
            if (decision != null)
            {
                CheckKey(decision.Key, new PMProjectileKey(Epoch, Owner, Proj, PMProjectileOrigin.ClientPredicted),
                    "Decision Key 往返");
                CheckEq(decision.ActivationId, 9u, "Decision ActivationId 往返");
                CheckTrue(decision.Result == PMActivationResult.Rejected, "Decision Result = Rejected");
                CheckTrue(decision.Reason == "geometry-out-of-budget", "Decision Reason 往返");
            }

            byte[] confirmed = Payload(4, DecisionFields(9u, 1, string.Empty).ToArray());
            CheckTrue(PMProjectileCodec.TryDecodeDecision(confirmed, 0, confirmed.Length, out decision, out error),
                "Decision（Confirmed，空 Reason）可解码：" + error);
            if (decision != null)
            {
                CheckTrue(decision.Result == PMActivationResult.Confirmed, "Decision Result = Confirmed");
                CheckTrue(decision.Reason == string.Empty, "Decision 空 Reason 往返");
            }

            // 字节级往返。
            byte[] encoded;
            CheckTrue(PMProjectileCodec.TryEncodeDecision(decision, out encoded, out error),
                "Decision 可编码：" + error);
            CheckBytesEqual(encoded, confirmed, "Decision codec 字节 == 手写字节");

            // Pending 伪终态：0 必须被编解码两侧拒绝。
            byte[] pending = Payload(4, DecisionFields(9u, 0, "still-pending").ToArray());
            PMProjectileDecision pendingDecision;
            CheckTrue(!PMProjectileCodec.TryDecodeDecision(pending, 0, pending.Length, out pendingDecision, out error),
                "Decision decode 拒绝 Pending（0）伪终态");
            CheckTrue(pendingDecision == null, "Decision Pending 不返回部分对象");

            // 未知 result 3 / 大值。
            byte[] unknown = Payload(4, DecisionFields(9u, 3, "x").ToArray());
            CheckTrue(!PMProjectileCodec.TryDecodeDecision(unknown, 0, unknown.Length, out pendingDecision, out error),
                "Decision decode 拒绝未知 result=3");

            PMProjectileDecision doc = new PMProjectileDecision();
            doc.Key = new PMProjectileKey(Epoch, Owner, Proj, PMProjectileOrigin.ClientPredicted);
            doc.ActivationId = 9u;
            doc.Result = PMActivationResult.Pending;
            byte[] ignored;
            CheckTrue(!PMProjectileCodec.TryEncodeDecision(doc, out ignored, out error),
                "Decision encode 拒绝 Pending");
            CheckContains(error, "Pending", "Decision encode 的 Pending 由终态门拒绝");

            doc.Result = PMActivationResult.Rejected;
            doc.ActivationId = 0u;
            CheckTrue(!PMProjectileCodec.TryEncodeDecision(doc, out ignored, out error),
                "Decision encode 拒绝 ActivationId=0");

            // Reason 上限：64 字节通过，65 字节拒绝；字符数 != 字节数（中文按字节计）。
            List<byte[]> fields = DecisionFields(9u, 2, new string('a', 64));
            byte[] maxReason = Payload(4, fields.ToArray());
            CheckTrue(PMProjectileCodec.TryDecodeDecision(maxReason, 0, maxReason.Length, out decision, out error),
                "Decision Reason = 64 字节可解码：" + error);

            fields = DecisionFields(9u, 2, new string('a', 65));
            byte[] overReason = Payload(4, fields.ToArray());
            CheckTrue(!PMProjectileCodec.TryDecodeDecision(overReason, 0, overReason.Length, out decision, out error),
                "Decision Reason = 65 字节必拒");
            CheckContains(error, "超过上限", "Decision Reason 65 字节由有界门拒绝");

            doc.Reason = new string('a', 65);
            CheckTrue(!PMProjectileCodec.TryEncodeDecision(doc, out ignored, out error),
                "Decision encode 拒绝 65 字节 Reason");

            doc.ActivationId = 9u;
            doc.Reason = new string('中', 30); // 90 字节 UTF-8
            CheckTrue(!PMProjectileCodec.TryEncodeDecision(doc, out ignored, out error),
                "Decision encode 按 UTF-8 字节数拒绝（30 个中文 = 90 字节）");

            doc.Reason = null;
            CheckTrue(PMProjectileCodec.TryEncodeDecision(doc, out ignored, out error),
                "Decision encode 把 null Reason 归一为空串（唯一的一处宽松归一）：" + error);
        }

        // =================================================================================
        //  E. 必填字段缺失
        // =================================================================================

        private static void TestMissingRequired()
        {
            for (int i = 0; i < SpawnFieldNumbers.Length; i++)
            {
                List<byte[]> f = SpawnFields();
                f.RemoveAt(i);
                byte[] payload = Payload(1, f.ToArray());
                PMProjectileSpawnIntent intent;
                string error;
                CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(payload, 0, payload.Length, out intent, out error),
                    "SpawnIntent 缺字段 " + SpawnFieldNumbers[i] + " 必拒");
                CheckTrue(intent == null, "SpawnIntent 缺字段 " + SpawnFieldNumbers[i] + " 不返回部分对象");
            }

            for (int i = 0; i < HitFieldNumbers.Length; i++)
            {
                List<byte[]> f = HitFields(1, 100);
                f.RemoveAt(i);
                byte[] payload = Payload(2, f.ToArray());
                PMProjectileHitBatch batch;
                string error;
                CheckTrue(!PMProjectileCodec.TryDecodeHitBatch(payload, 0, payload.Length, out batch, out error),
                    "HitBatch 缺字段 " + HitFieldNumbers[i] + " 必拒");
            }

            for (int i = 0; i < SnapshotFieldNumbers.Length; i++)
            {
                List<byte[]> f = SnapshotFields(null, null);
                int index = SnapshotIndex(f, SnapshotFieldNumbers[i]);
                f.RemoveAt(index);
                byte[] payload = Payload(3, f.ToArray());
                PMProjectileSnapshot snapshot;
                string error;
                CheckTrue(!PMProjectileCodec.TryDecodeSnapshot(payload, 0, payload.Length, out snapshot, out error),
                    "Snapshot 缺字段 " + SnapshotFieldNumbers[i] + " 必拒");
                CheckTrue(snapshot == null, "Snapshot 缺字段 " + SnapshotFieldNumbers[i] + " 不返回部分对象");
            }

            for (int i = 0; i < DecisionFieldNumbers.Length; i++)
            {
                List<byte[]> f = DecisionFields(9u, 2, "r");
                f.RemoveAt(i);
                byte[] payload = Payload(4, f.ToArray());
                PMProjectileDecision decision;
                string error;
                CheckTrue(!PMProjectileCodec.TryDecodeDecision(payload, 0, payload.Length, out decision, out error),
                    "Decision 缺字段 " + DecisionFieldNumbers[i] + " 必拒");
            }

            // 命中候选的 1..10 全部必填。
            for (int missing = 1; missing <= 10; missing++)
            {
                List<byte[]> cand = new List<byte[]>(10);
                for (int field = 1; field <= 10; field++)
                {
                    if (field == missing)
                    {
                        continue;
                    }

                    cand.Add(CandidateField(field));
                }

                List<byte[]> f = HitFields(0, 100);
                f.Add(FBytes(17, SubPayload(cand)));
                byte[] payload = Payload(2, f.ToArray());
                PMProjectileHitBatch batch;
                string error;
                CheckTrue(!PMProjectileCodec.TryDecodeHitBatch(payload, 0, payload.Length, out batch, out error),
                    "命中候选缺字段 " + missing + " 必拒");
            }

            // spec 的 1..7 全部必填。
            for (int missing = 1; missing <= 7; missing++)
            {
                List<byte[]> spec = new List<byte[]>(7);
                for (int field = 1; field <= 7; field++)
                {
                    if (field == missing)
                    {
                        continue;
                    }

                    spec.Add(SpecField(field));
                }

                List<byte[]> f = SnapshotFields(null, null);
                f[f.Count - 1] = FBytes(34, SubPayload(spec));
                byte[] payload = Payload(3, f.ToArray());
                PMProjectileSnapshot snapshot;
                string error;
                CheckTrue(!PMProjectileCodec.TryDecodeSnapshot(payload, 0, payload.Length, out snapshot, out error),
                    "spec 缺字段 " + missing + " 必拒");
            }
        }

        private static int SnapshotIndex(List<byte[]> fields, int fieldNumber)
        {
            // SnapshotFields() 的字段顺序固定，且唯一可省略的字段在后面；这里按「第几个标量」定位。
            int[] order = SnapshotFieldNumbers;
            for (int i = 0; i < order.Length; i++)
            {
                if (order[i] == fieldNumber)
                {
                    return i;
                }
            }

            return -1;
        }

        private static byte[] CandidateField(int field)
        {
            switch (field)
            {
                case 1: return FVarint(1, 100u);
                case 2: return FVarint(2, 3u);
                case 3: return FVarint(3, 2UL);
                case 4: return FSInt64(4, 500L);
                case 5: return FFloat(5, 1f);
                case 6: return FFloat(6, 2f);
                case 7: return FFloat(7, 3f);
                case 8: return FFloat(8, 0.01f);
                case 9: return FFloat(9, 0.02f);
                default: return FFloat(10, 0.03f);
            }
        }

        private static byte[] SpecField(int field)
        {
            switch (field)
            {
                case 1: return FFloat(1, 12.5f);
                case 2: return FFloat(2, 0.35f);
                case 3: return FVarint(3, 2800u);
                case 4: return FVarint(4, 120u);
                case 5: return FBool(5, true);
                case 6: return FBool(6, true);
                default: return FBool(7, false);
            }
        }

        // =================================================================================
        //  F. 乱序 / 重复 / 未知 / 错 wire / 尾部
        // =================================================================================

        private static void TestOrderDuplicateUnknownWire()
        {
            string error;

            // --- 乱序：字段号倒退 ---
            List<byte[]> f = new List<byte[]>();
            f.Add(FFloat(17, 10f));   // yaw
            f.Add(FFloat(11, 1f));    // position.x（倒退）
            byte[] payload = Payload(1, f.ToArray());
            PMProjectileSpawnIntent intent;
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(payload, 0, payload.Length, out intent, out error),
                "SpawnIntent 字段号倒退必拒");
            CheckContains(error, "倒退", "SpawnIntent 字段号倒退由顺序门拒绝");

            // --- 重复：标量字段出现两次 ---
            f = SpawnFields();
            f.Insert(1, FFloat(11, 9f));
            payload = Payload(1, f.ToArray());
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(payload, 0, payload.Length, out intent, out error),
                "SpawnIntent 标量字段重复必拒");
            CheckContains(error, "重复", "SpawnIntent 字段重复由顺序门拒绝");

            f = HitFields(1, 100);
            f.Insert(1, FFloat(10, 9f));
            payload = Payload(2, f.ToArray());
            PMProjectileHitBatch batch;
            CheckTrue(!PMProjectileCodec.TryDecodeHitBatch(payload, 0, payload.Length, out batch, out error),
                "HitBatch 标量字段重复必拒");

            f = DecisionFields(9u, 2, "r");
            f.Insert(1, FVarint(10, 9u));
            payload = Payload(4, f.ToArray());
            PMProjectileDecision decision;
            CheckTrue(!PMProjectileCodec.TryDecodeDecision(payload, 0, payload.Length, out decision, out error),
                "Decision 标量字段重复必拒");

            // 候选 / spec 内部重复。
            List<byte[]> cand = new List<byte[]>();
            cand.Add(FVarint(1, 100u));
            cand.Add(FVarint(1, 101u));
            f = HitFields(0, 100);
            f.Add(FBytes(17, SubPayload(cand)));
            payload = Payload(2, f.ToArray());
            CheckTrue(!PMProjectileCodec.TryDecodeHitBatch(payload, 0, payload.Length, out batch, out error),
                "命中候选字段重复必拒");

            List<byte[]> spec = new List<byte[]>();
            spec.Add(FFloat(1, 1f));
            spec.Add(FFloat(1, 2f));
            f = SnapshotFields(null, null);
            f[f.Count - 1] = FBytes(34, SubPayload(spec));
            payload = Payload(3, f.ToArray());
            PMProjectileSnapshot snapshot;
            CheckTrue(!PMProjectileCodec.TryDecodeSnapshot(payload, 0, payload.Length, out snapshot, out error),
                "spec 内部字段重复必拒");

            // --- 未知字段（含「上行注入 spec 位置」的 34 与越界 19/9）---
            f = SpawnFields();
            f.Add(FVarint(19, 1u));
            payload = Payload(1, f.ToArray());
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(payload, 0, payload.Length, out intent, out error),
                "SpawnIntent 未知字段 19 必拒");

            f = SpawnFields();
            f.Add(FVarint(9, 1u));
            payload = Payload(1, f.ToArray());
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(payload, 0, payload.Length, out intent, out error),
                "SpawnIntent 未知字段 9 必拒");

            f = HitFields(1, 100);
            f.Add(FVarint(18, 1u));
            payload = Payload(2, f.ToArray());
            CheckTrue(!PMProjectileCodec.TryDecodeHitBatch(payload, 0, payload.Length, out batch, out error),
                "HitBatch 未知字段 18 必拒");

            f = SnapshotFields(null, null);
            f.Add(FVarint(35, 1u));
            payload = Payload(3, f.ToArray());
            CheckTrue(!PMProjectileCodec.TryDecodeSnapshot(payload, 0, payload.Length, out snapshot, out error),
                "Snapshot 未知字段 35 必拒");

            f = DecisionFields(9u, 2, "r");
            f.Add(FVarint(13, 1u));
            payload = Payload(4, f.ToArray());
            CheckTrue(!PMProjectileCodec.TryDecodeDecision(payload, 0, payload.Length, out decision, out error),
                "Decision 未知字段 13 必拒");

            // 命中候选 / spec 内部未知字段。
            cand = new List<byte[]>();
            cand.Add(FVarint(11, 1u));
            f = HitFields(0, 100);
            f.Add(FBytes(17, SubPayload(cand)));
            payload = Payload(2, f.ToArray());
            CheckTrue(!PMProjectileCodec.TryDecodeHitBatch(payload, 0, payload.Length, out batch, out error),
                "命中候选未知字段 11 必拒");

            spec = new List<byte[]>();
            spec.Add(FVarint(8, 1u));
            f = SnapshotFields(null, null);
            f[f.Count - 1] = FBytes(34, SubPayload(spec));
            payload = Payload(3, f.ToArray());
            CheckTrue(!PMProjectileCodec.TryDecodeSnapshot(payload, 0, payload.Length, out snapshot, out error),
                "spec 未知字段 8 必拒");

            // --- 错 wire type ---
            CheckWrongWire(1, 11, PMWireType.Varint, "SpawnIntent positionX 用 varint");
            CheckWrongWire(1, 10, PMWireType.Fixed32, "SpawnIntent activationId 用 fixed32");
            CheckWrongWire(1, 17, PMWireType.Varint, "SpawnIntent yaw 用 varint");
            CheckWrongWire(1, 18, PMWireType.Fixed32, "SpawnIntent predictionMs 用 fixed32");
            CheckWrongWire(2, 10, PMWireType.Varint, "HitBatch previousPositionX 用 varint");
            CheckWrongWire(2, 16, PMWireType.Fixed32, "HitBatch rewindMs 用 fixed32");
            CheckWrongWire(3, 10, PMWireType.Fixed64, "Snapshot authorityNetId 用 fixed64");
            CheckWrongWire(3, 25, PMWireType.Fixed32, "Snapshot moveTimeMs 用 fixed32（应为 fixed64）");
            CheckWrongWire(3, 26, PMWireType.Fixed64, "Snapshot stopped 用 fixed64（应为 varint）");
            CheckWrongWire(3, 32, PMWireType.LengthDelimited, "Snapshot HitTargets 元素用 length-delimited");
            CheckWrongWire(4, 10, PMWireType.Fixed32, "Decision activationId 用 fixed32");

            // --- 尾部字节（头部之后多一个字节）---
            byte[] valid = Payload(1, SpawnFields().ToArray());
            byte[] trailing = Concat(valid, new byte[] { 0x00 });
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(trailing, 0, trailing.Length, out intent, out error),
                "SpawnIntent 尾部多余字节必拒");

            valid = SnapshotPayload(null, null);
            trailing = Concat(valid, new byte[] { 0x01 });
            CheckTrue(!PMProjectileCodec.TryDecodeSnapshot(trailing, 0, trailing.Length, out snapshot, out error),
                "Snapshot 尾部多余字节必拒");

            // --- 头部字段乱序 / 重复 / 缺 version ---
            byte[] headerSwapped = Concat(FVarint(2, 1u), FVarint(1, 1u));
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(headerSwapped, 0, headerSwapped.Length, out intent, out error),
                "头部字段乱序必拒");
            CheckContains(error, "kind", "头部乱序由 kind 门拒绝");

            byte[] headerDup = Concat(FVarint(1, 1u), FVarint(1, 1u));
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(headerDup, 0, headerDup.Length, out intent, out error),
                "头部 kind 重复必拒");

            byte[] noVersion = Concat(FVarint(1, 1u), FVarint(3, Epoch));
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(noVersion, 0, noVersion.Length, out intent, out error),
                "头部缺 version 必拒");
        }

        private static void CheckWrongWire(int kind, int field, PMWireType wrongWire, string label)
        {
            // 用「正确的字段集合」但把指定字段换成错 wire：解码必须拒。
            List<byte[]> f;
            if (kind == 1)
            {
                f = SpawnFields();
                for (int i = 0; i < f.Count; i++)
                {
                    if (SpawnFieldNumbers[i] == field)
                    {
                        f[i] = FRawTag(field, wrongWire);
                    }
                }
            }
            else if (kind == 2)
            {
                f = HitFields(1, 100);
                for (int i = 0; i < HitFieldNumbers.Length; i++)
                {
                    if (HitFieldNumbers[i] == field)
                    {
                        f[i] = FRawTag(field, wrongWire);
                    }
                }
            }
            else if (kind == 3)
            {
                if (field == 32 || field == 33)
                {
                    // 32/33 是可选数组，不在 SnapshotFieldNumbers 里；这里显式造一个「带一个目标」
                    // 再把该元素换成错 wire，才能真的碰到数组元素的 wire 检查。
                    f = field == 32
                        ? SnapshotFields(new uint[] { 5u }, null)
                        : SnapshotFields(null, new uint[] { 5u });
                    f[22] = wrongWire == PMWireType.LengthDelimited
                        ? FBytes(field, new byte[] { 0x01 })
                        : FRawTag(field, wrongWire);
                }
                else
                {
                    f = SnapshotFields(null, null);
                    int index = SnapshotIndex(f, field);
                    if (index >= 0)
                    {
                        if (wrongWire == PMWireType.LengthDelimited)
                        {
                            f[index] = FBytes(field, new byte[] { 0x01 });
                        }
                        else
                        {
                            f[index] = FRawTag(field, wrongWire);
                        }
                    }
                }
            }
            else
            {
                f = DecisionFields(9u, 2, "r");
                for (int i = 0; i < DecisionFieldNumbers.Length; i++)
                {
                    if (DecisionFieldNumbers[i] == field)
                    {
                        f[i] = FRawTag(field, wrongWire);
                    }
                }
            }

            byte[] payload = Payload(kind, f.ToArray());
            string error;
            if (kind == 1)
            {
                PMProjectileSpawnIntent doc;
                CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(payload, 0, payload.Length, out doc, out error),
                    "错 wire 必拒：" + label);
            }
            else if (kind == 2)
            {
                PMProjectileHitBatch doc;
                CheckTrue(!PMProjectileCodec.TryDecodeHitBatch(payload, 0, payload.Length, out doc, out error),
                    "错 wire 必拒：" + label);
            }
            else if (kind == 3)
            {
                PMProjectileSnapshot doc;
                CheckTrue(!PMProjectileCodec.TryDecodeSnapshot(payload, 0, payload.Length, out doc, out error),
                    "错 wire 必拒：" + label);
            }
            else
            {
                PMProjectileDecision doc;
                CheckTrue(!PMProjectileCodec.TryDecodeDecision(payload, 0, payload.Length, out doc, out error),
                    "错 wire 必拒：" + label);
            }
        }

        // =================================================================================
        //  G. 逐字节截断
        // =================================================================================

        private static void TestTruncation()
        {
            // SpawnIntent / Snapshot / Decision：全部字段必填，因此**任何**截断都必须被拒。
            // （Snapshot 的 spec 是最后一个必填字段，所以截断到任何位置都会缺 spec。）
            CheckAllTruncationsRejected(1, Payload(1, SpawnFields().ToArray()), "SpawnIntent");
            CheckAllTruncationsRejected(3, SnapshotPayload(new uint[] { 1u }, new uint[] { 2u }), "Snapshot");
            CheckAllTruncationsRejected(4, Payload(4, DecisionFields(9u, 2, "r").ToArray()), "Decision");

            // HitBatch：候选是 repeated 字段且 0 候选在 codec 层合法，因此「恰好在字段边界截断」
            // 会得到一个**结构完整但更短**的合法载荷（protobuf 没有包内总长，这是格式的固有性质，
            // 不是半应用）。这里要求：这类边界截断只能产出候选数严格更少的完整对象，
            // 其余全部被拒，且整过程不得抛异常。
            byte[] hit = Payload(2, HitFields(2, 100).ToArray());
            int rejected = 0;
            int boundaryValid = 0;
            for (int cut = 0; cut < hit.Length; cut++)
            {
                PMProjectileHitBatch batch;
                string error;
                if (PMProjectileCodec.TryDecodeHitBatch(hit, 0, cut, out batch, out error))
                {
                    boundaryValid++;
                    CheckTrue(batch != null && batch.Targets != null && batch.Targets.Length < 2,
                        "HitBatch 截断产生的合法载荷候候选数严格少于原文（cut=" + cut + "）");
                }
                else
                {
                    rejected++;
                }
            }

            CheckEq(rejected, hit.Length - boundaryValid,
                "HitBatch 逐字节截断：除候选边界外全部被拒（0.." + (hit.Length - 1) + "）");
            Console.WriteLine("      hit 候选边界截断产生 " + boundaryValid + " 个结构合法的更短载荷（登记项）");
            CheckTrue(boundaryValid >= 1, "HitBatch 确实存在字段边界截断（否则说明用例没覆盖到该性质）");

            // 空载荷 / null / 越界区间。
            PMProjectileSpawnIntent doc;
            string error2;
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(null, out doc, out error2), "null 载荷必拒");
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(new byte[0], out doc, out error2), "空载荷必拒");
            byte[] small = Payload(1, SpawnFields().ToArray());
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(small, 0, small.Length + 1, out doc, out error2),
                "载荷区间越界必拒");
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(small, -1, small.Length, out doc, out error2),
                "负 offset 必拒");
        }

        private static void CheckAllTruncationsRejected(int kind, byte[] payload, string label)
        {
            int rejected = 0;
            for (int cut = 0; cut < payload.Length; cut++)
            {
                if (Decode(kind, payload, cut) == false)
                {
                    rejected++;
                }
            }

            CheckEq(rejected, payload.Length, label + " 逐字节截断全部被拒（0.." + (payload.Length - 1) + "）");
        }

        private static bool Decode(int kind, byte[] payload, int count)
        {
            string error;
            if (kind == 1)
            {
                PMProjectileSpawnIntent doc;
                return PMProjectileCodec.TryDecodeSpawnIntent(payload, 0, count, out doc, out error);
            }

            if (kind == 2)
            {
                PMProjectileHitBatch doc;
                return PMProjectileCodec.TryDecodeHitBatch(payload, 0, count, out doc, out error);
            }

            if (kind == 3)
            {
                PMProjectileSnapshot doc;
                return PMProjectileCodec.TryDecodeSnapshot(payload, 0, count, out doc, out error);
            }

            PMProjectileDecision decision;
            return PMProjectileCodec.TryDecodeDecision(payload, 0, count, out decision, out error);
        }

        // =================================================================================
        //  H. 长度前缀超限
        // =================================================================================

        private static void TestLengthPrefix()
        {
            // 声明 65 字节（超过 MaxReasonBytes = 64），同时真的给 65 字节。
            byte[] overCap = Concat(
                Header(4, Epoch, Owner, Proj, 1),
                FVarint(10, 9u),
                FVarint(11, 2u),
                FRawTag(12, PMWireType.LengthDelimited),
                new byte[] { 65 },
                new byte[65]);

            PMProjectileDecision decision;
            string error;
            CheckTrue(!PMProjectileCodec.TryDecodeDecision(overCap, 0, overCap.Length, out decision, out error),
                "Reason 声明 65 字节必拒（分配前上限）");
            CheckContains(error, "分配前拒绝", "Reason 65 字节由「分配前上限」门拒绝");

            // 声明 50 字节（未超上限）但只给 3 字节：必须被读越界拒绝（FormatException → false）。
            byte[] shortBody = Concat(
                Header(4, Epoch, Owner, Proj, 1),
                FVarint(10, 9u),
                FVarint(11, 2u),
                FRawTag(12, PMWireType.LengthDelimited),
                new byte[] { 50 },
                new byte[] { 1, 2, 3 });
            CheckTrue(!PMProjectileCodec.TryDecodeDecision(shortBody, 0, shortBody.Length, out decision, out error),
                "Reason 声明 50 字节但只有 3 字节必拒");

            // 长度前缀为 10 字节 varint（ulong 最大值）：必须被 checked 溢出拒绝，而不是当成负数。
            byte[] hugePrefix = Concat(
                Header(4, Epoch, Owner, Proj, 1),
                FVarint(10, 9u),
                FVarint(11, 2u),
                FRawTag(12, PMWireType.LengthDelimited),
                FVarintRaw(0xFFFFFFFFFFFFFFFFUL));
            CheckTrue(!PMProjectileCodec.TryDecodeDecision(hugePrefix, 0, hugePrefix.Length, out decision, out error),
                "Reason 长度前缀为 ulong 最大值必拒（溢出 → false 而非负数）");
            CheckTrue(decision == null, "长度前缀溢出时不返回部分对象");
        }

        private static byte[] FVarintRaw(ulong value)
        {
            PMNetWriter w = new PMNetWriter(16);
            w.WriteVarint(value);
            return w.ToArray();
        }

        // =================================================================================
        //  I. NaN/Inf 与范围
        // =================================================================================

        private static void TestNonFiniteAndRanges()
        {
            string error;

            // SpawnIntent：position / direction / yaw
            CheckSpawnRejected(11, FFloat(11, float.NaN), "positionX = NaN");
            CheckSpawnRejected(12, FFloat(12, float.PositiveInfinity), "positionY = +Inf");
            CheckSpawnRejected(13, FFloat(13, float.NegativeInfinity), "positionZ = -Inf");
            CheckSpawnRejected(14, FFloat(14, float.NaN), "directionX = NaN");
            CheckSpawnRejected(16, FFloat(16, float.PositiveInfinity), "directionZ = +Inf");
            CheckSpawnRejected(17, FFloat(17, float.NaN), "yaw = NaN");
            CheckSpawnRejected(17, FFloat(17, float.PositiveInfinity), "yaw = +Inf");

            // 方向分量大到长度平方溢出 → 也必须拒（否则权威归一化得到 NaN）。
            CheckSpawnDirectionRejected(1e30f, 1e30f, 1e30f, "方向分量 1e30（长度平方溢出为 +Inf）");
            CheckSpawnDirectionRejected(1e-30f, 1e-30f, 1e-30f, "方向分量 1e-30（长度平方下溢为 0）");

            // encode 侧同样拒 NaN / 溢出方向。
            PMProjectileSpawnIntent intent = new PMProjectileSpawnIntent();
            intent.Key = new PMProjectileKey(Epoch, Owner, Proj, PMProjectileOrigin.ClientPredicted);
            intent.ActivationId = 1u;
            intent.Direction = new PMVector3(0f, 0f, 1f);
            intent.Position = new PMVector3(float.NaN, 0f, 0f);
            byte[] ignored;
            CheckTrue(!PMProjectileCodec.TryEncodeSpawnIntent(intent, out ignored, out error),
                "SpawnIntent encode 拒绝 NaN Position");
            intent.Position = new PMVector3(0f, 0f, 0f);
            intent.Direction = new PMVector3(1e30f, 0f, 0f);
            CheckTrue(!PMProjectileCodec.TryEncodeSpawnIntent(intent, out ignored, out error),
                "SpawnIntent encode 拒绝长度平方溢出的 Direction");

            // HitBatch：位置 / 候选点位
            List<byte[]> f = HitFields(0, 100);
            f.Add(FBytes(17, CandidateBytes(100u, 3u, 2, 500L, float.NaN, 0f, 0f, 0f, 0f, 0f)));
            byte[] payload = Payload(2, f.ToArray());
            PMProjectileHitBatch batch;
            CheckTrue(!PMProjectileCodec.TryDecodeHitBatch(payload, 0, payload.Length, out batch, out error),
                "HitBatch decode 拒绝候选 ImpactPoint = NaN");

            f = HitFields(0, 100);
            f.Add(FBytes(17, CandidateBytes(100u, 3u, 2, 500L, 0f, 0f, 0f, float.PositiveInfinity, 0f, 0f)));
            payload = Payload(2, f.ToArray());
            CheckTrue(!PMProjectileCodec.TryDecodeHitBatch(payload, 0, payload.Length, out batch, out error),
                "HitBatch decode 拒绝候选 VisualOffset = +Inf");

            f = HitFields(1, 100);
            f[0] = FFloat(10, float.NaN);
            payload = Payload(2, f.ToArray());
            CheckTrue(!PMProjectileCodec.TryDecodeHitBatch(payload, 0, payload.Length, out batch, out error),
                "HitBatch decode 拒绝 PreviousPosition = NaN");

            // Snapshot：位置 / 时间 / spec
            CheckSnapshotRejected(18, FFloat(18, float.NaN), "positionX = NaN");
            CheckSnapshotRejected(21, FFloat(21, float.PositiveInfinity), "velocityX = +Inf");
            CheckSnapshotRejected(24, FFloat(24, float.NaN), "yaw = NaN");
            CheckSnapshotRejected(25, FDouble(25, double.NaN), "moveTimeMs = NaN");
            CheckSnapshotRejected(29, FDouble(29, double.PositiveInfinity), "stopWallTimeMs = +Inf");
            CheckSnapshotRejected(31, FDouble(31, double.NaN), "tombstoneUntilMs = NaN");
            CheckSnapshotRejected(25, FDouble(25, -1.0), "moveTimeMs = -1");
            CheckSnapshotRejected(30, FDouble(30, -0.5), "timeAfterStoppedMs = -0.5");
            CheckSnapshotRejected(24, FFloat(24, -1f), "yaw = -1（非规范）");
            CheckSnapshotRejected(24, FFloat(24, 360f), "yaw = 360（非规范）");

            List<byte[]> spec = new List<byte[]>();
            spec.Add(FFloat(1, float.NaN));
            spec.Add(FFloat(2, 0.1f));
            spec.Add(FVarint(3, 100u));
            spec.Add(FVarint(4, 0u));
            spec.Add(FBool(5, true));
            spec.Add(FBool(6, true));
            spec.Add(FBool(7, false));
            f = SnapshotFields(null, null);
            f[f.Count - 1] = FBytes(34, SubPayload(spec));
            payload = Payload(3, f.ToArray());
            PMProjectileSnapshot snapshot;
            CheckTrue(!PMProjectileCodec.TryDecodeSnapshot(payload, 0, payload.Length, out snapshot, out error),
                "Snapshot decode 拒绝 spec.SpeedMps = NaN");

            spec[0] = FFloat(1, -1f);
            f = SnapshotFields(null, null);
            f[f.Count - 1] = FBytes(34, SubPayload(spec));
            payload = Payload(3, f.ToArray());
            CheckTrue(!PMProjectileCodec.TryDecodeSnapshot(payload, 0, payload.Length, out snapshot, out error),
                "Snapshot decode 拒绝 spec.SpeedMps = -1");

            spec[0] = FFloat(1, 1f);
            spec[1] = FFloat(2, float.PositiveInfinity);
            f = SnapshotFields(null, null);
            f[f.Count - 1] = FBytes(34, SubPayload(spec));
            payload = Payload(3, f.ToArray());
            CheckTrue(!PMProjectileCodec.TryDecodeSnapshot(payload, 0, payload.Length, out snapshot, out error),
                "Snapshot decode 拒绝 spec.RadiusM = +Inf");

            spec[1] = FFloat(2, 0.1f);
            spec[2] = FVarintRaw(unchecked((ulong)(long)(-1)));
            f = SnapshotFields(null, null);
            f[f.Count - 1] = FBytes(34, SubPayload(spec));
            payload = Payload(3, f.ToArray());
            CheckTrue(!PMProjectileCodec.TryDecodeSnapshot(payload, 0, payload.Length, out snapshot, out error),
                "Snapshot decode 拒绝 spec.LifetimeMs 负值编码");

            // encode 侧 snapshot NaN。
            PMProjectileSnapshot doc = new PMProjectileSnapshot();
            PMProjectileState state = new PMProjectileState();
            state.Key = new PMProjectileKey(Epoch, Owner, Proj, PMProjectileOrigin.ServerDirect);
            state.AuthorityNetId = 42u;
            state.Position = new PMVector3(float.NaN, 0f, 0f);
            doc.State = state;
            doc.Spec = new PMProjectileSpec();
            byte[] ignoredBytes;
            CheckTrue(!PMProjectileCodec.TryEncodeSnapshot(doc, out ignoredBytes, out error),
                "Snapshot encode 拒绝 NaN Position");
            state.Position = new PMVector3(1f, 1f, 1f);
            doc.Spec = null;
            CheckTrue(!PMProjectileCodec.TryEncodeSnapshot(doc, out ignoredBytes, out error),
                "Snapshot encode 拒绝缺 Spec（权威配置不允许静默默认）");

            // predictionMs 的 10 字节负值编码。
            f = SpawnFields();
            f[8] = FVarintRaw(unchecked((ulong)(long)(-1)));
            payload = Payload(1, f.ToArray());
            PMProjectileSpawnIntent spawn;
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(payload, 0, payload.Length, out spawn, out error),
                "SpawnIntent decode 拒绝 predictionMs 负值编码");

            f = SpawnFields();
            f[8] = FVarint(18, 501u);
            payload = Payload(1, f.ToArray());
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(payload, 0, payload.Length, out spawn, out error),
                "SpawnIntent decode 拒绝 predictionMs = 501");
        }

        private static void CheckSpawnRejected(int fieldNumber, byte[] replacement, string label)
        {
            List<byte[]> f = SpawnFields();
            for (int i = 0; i < SpawnFieldNumbers.Length; i++)
            {
                if (SpawnFieldNumbers[i] == fieldNumber)
                {
                    f[i] = replacement;
                }
            }

            byte[] payload = Payload(1, f.ToArray());
            PMProjectileSpawnIntent doc;
            string error;
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(payload, 0, payload.Length, out doc, out error),
                "SpawnIntent decode 拒绝 " + label);
        }

        private static void CheckSnapshotRejected(int fieldNumber, byte[] replacement, string label)
        {
            List<byte[]> f = SnapshotFields(null, null);
            int index = SnapshotIndex(f, fieldNumber);
            f[index] = replacement;
            byte[] payload = Payload(3, f.ToArray());
            PMProjectileSnapshot doc;
            string error;
            CheckTrue(!PMProjectileCodec.TryDecodeSnapshot(payload, 0, payload.Length, out doc, out error),
                "Snapshot decode 拒绝 " + label);
        }

        // =================================================================================
        //  J. Key / Origin / 身份 / 帧域
        // =================================================================================

        private static void TestIdentityAndOrigin()
        {
            string error;

            // Key 三件套为 0。
            CheckRejectedOrigin(1, 0u, Owner, Proj, 1, "epoch=0");
            CheckRejectedOrigin(1, Epoch, 0u, Proj, 1, "ownerNetId=0");
            CheckRejectedOrigin(1, Epoch, Owner, 0u, 1, "projectileId=0");
            CheckRejectedOrigin(2, 0u, Owner, Proj, 1, "hit epoch=0");
            CheckRejectedOrigin(2, Epoch, 0u, Proj, 1, "hit ownerNetId=0");
            CheckRejectedOrigin(2, Epoch, Owner, 0u, 1, "hit projectileId=0");
            CheckRejectedOrigin(3, 0u, Owner, Proj, 2, "snapshot epoch=0");
            CheckRejectedOrigin(3, Epoch, 0u, Proj, 2, "snapshot ownerNetId=0");
            CheckRejectedOrigin(3, Epoch, Owner, 0u, 2, "snapshot projectileId=0");
            CheckRejectedOrigin(4, Epoch, Owner, 0u, 1, "decision projectileId=0");

            // 未知 origin。
            CheckRejectedOrigin(1, Epoch, Owner, Proj, 0, "origin=0（None）");
            CheckRejectedOrigin(1, Epoch, Owner, Proj, 3, "origin=3（未知）");
            CheckRejectedOrigin(2, Epoch, Owner, Proj, 3, "hit origin=3");
            CheckRejectedOrigin(3, Epoch, Owner, Proj, 3, "snapshot origin=3");
            CheckRejectedOrigin(4, Epoch, Owner, Proj, 3, "decision origin=3");

            // 上行 origin 只允许 ClientPredicted。
            CheckRejectedOrigin(1, Epoch, Owner, Proj, 2, "上行 SpawnIntent origin=ServerDirect");
            CheckRejectedOrigin(2, Epoch, Owner, Proj, 2, "上行 HitBatch origin=ServerDirect");

            // 下行两类允许两种 origin（不是伪造通道，只是不额外设限）。
            CheckAcceptedOrigin(3, Epoch, Owner, Proj, 1, "快照 origin=ClientPredicted 允许");
            CheckAcceptedOrigin(3, Epoch, Owner, Proj, 2, "快照 origin=ServerDirect 允许");
            CheckAcceptedOrigin(4, Epoch, Owner, Proj, 1, "裁决 origin=ClientPredicted 允许");
            CheckAcceptedOrigin(4, Epoch, Owner, Proj, 2, "裁决 origin=ServerDirect 允许");

            // encode 侧 origin 门。
            PMProjectileSpawnIntent intent = new PMProjectileSpawnIntent();
            intent.Key = new PMProjectileKey(Epoch, Owner, Proj, PMProjectileOrigin.ServerDirect);
            intent.ActivationId = 1u;
            intent.Direction = new PMVector3(0f, 0f, 1f);
            byte[] ignored;
            CheckTrue(!PMProjectileCodec.TryEncodeSpawnIntent(intent, out ignored, out error),
                "SpawnIntent encode 拒绝 origin=ServerDirect");

            PMProjectileHitBatch hit = new PMProjectileHitBatch();
            hit.Key = new PMProjectileKey(Epoch, Owner, Proj, PMProjectileOrigin.ServerDirect);
            CheckTrue(!PMProjectileCodec.TryEncodeHitBatch(hit, out ignored, out error),
                "HitBatch encode 拒绝 origin=ServerDirect");

            // 命中候选的帧域与身份。
            CheckCandidateRejected(0u, 3u, 2, 500L, "targetNetId=0");
            CheckCandidateRejected(100u, 0u, 2, 500L, "targetStreamVersion=0");
            CheckCandidateRejected(100u, 3u, 0, 500L, "帧域 None");
            CheckCandidateRejected(100u, 3u, 1, 500L, "帧域 Input（错域帧）");
            CheckCandidateRejected(100u, 3u, 3, 500L, "帧域 SimTime（错域帧）");
            CheckCandidateRejected(100u, 3u, 4, 500L, "帧域 Session（错域帧）");
            CheckCandidateRejected(100u, 3u, 2, 0L, "帧值为 0");
            CheckCandidateRejected(100u, 3u, 2, -5L, "帧值为负");

            // 版本不兼容。
            byte[] badVersion = Concat(
                FVarint(1, 1u),
                FVarint(2, 2u),
                FVarint(3, Epoch),
                FVarint(4, Owner),
                FVarint(5, Proj),
                FVarint(6, 1u));
            PMProjectileSpawnIntent doc;
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(badVersion, 0, badVersion.Length, out doc, out error),
                "version=2 不兼容必拒");
            CheckContains(error, "版本不兼容", "version 门拒绝 version=2");

            // 未知 kind。
            byte[] badKind = FVarint(1, 5u);
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(badKind, 0, badKind.Length, out doc, out error),
                "未知 kind=5 必拒");

            byte[] kindZero = FVarint(1, 0u);
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(kindZero, 0, kindZero.Length, out doc, out error),
                "kind=0（None）必拒");
        }

        private static void CheckRejectedOrigin(int kind, uint epoch, uint owner, uint proj, int origin, string label)
        {
            byte[] payload = BuildValid(kind, epoch, owner, proj, origin);
            string error;
            bool ok = DecodeWithError(kind, payload, out error);
            CheckTrue(!ok, "身份/origin 非法必拒：" + label);
            CheckTrue(error != null, "身份/origin 非法返回 error：" + label);
        }

        private static bool DecodeWithError(int kind, byte[] payload, out string error)
        {
            if (kind == 1)
            {
                PMProjectileSpawnIntent doc;
                return PMProjectileCodec.TryDecodeSpawnIntent(payload, 0, payload.Length, out doc, out error);
            }

            if (kind == 2)
            {
                PMProjectileHitBatch doc;
                return PMProjectileCodec.TryDecodeHitBatch(payload, 0, payload.Length, out doc, out error);
            }

            if (kind == 3)
            {
                PMProjectileSnapshot doc;
                return PMProjectileCodec.TryDecodeSnapshot(payload, 0, payload.Length, out doc, out error);
            }

            PMProjectileDecision decision;
            return PMProjectileCodec.TryDecodeDecision(payload, 0, payload.Length, out decision, out error);
        }

        private static void CheckAcceptedOrigin(int kind, uint epoch, uint owner, uint proj, int origin, string label)
        {
            byte[] payload = BuildValid(kind, epoch, owner, proj, origin);
            CheckTrue(Decode(kind, payload, payload.Length), "允许的 origin 可解码：" + label);
        }

        private static byte[] BuildValid(int kind, uint epoch, uint owner, uint proj, int origin)
        {
            if (kind == 1)
            {
                return Payload(1, epoch, owner, proj, origin, SpawnFields().ToArray());
            }

            if (kind == 2)
            {
                return Payload(2, epoch, owner, proj, origin, HitFields(1, 100).ToArray());
            }

            if (kind == 3)
            {
                return Payload(3, epoch, owner, proj, origin, SnapshotFields(null, null).ToArray());
            }

            return Payload(4, epoch, owner, proj, origin, DecisionFields(9u, 2, "r").ToArray());
        }

        private static void CheckCandidateRejected(uint netId, uint stream, int domain, long frame, string label)
        {
            List<byte[]> f = HitFields(0, 100);
            f.Add(FBytes(17, CandidateBytes(netId, stream, domain, frame, 1f, 2f, 3f, 0f, 0f, 0f)));
            byte[] payload = Payload(2, f.ToArray());
            PMProjectileHitBatch batch;
            string error;
            CheckTrue(!PMProjectileCodec.TryDecodeHitBatch(payload, 0, payload.Length, out batch, out error),
                "命中候选非法必拒：" + label);
        }

        // =================================================================================
        //  K. 上行注入权威字段
        // =================================================================================

        private static void TestUplinkInjection()
        {
            string error;

            // 把「快照 spec 子消息」塞进上行 spawn 载荷（field 34）→ 未知字段，必拒。
            byte[] specBody = SpecBytes(12.5f, 0.35f, 2800, 120, true, true, false);
            List<byte[]> f = SpawnFields();
            f.Add(FBytes(34, specBody));
            byte[] payload = Payload(1, f.ToArray());
            PMProjectileSpawnIntent intent;
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(payload, 0, payload.Length, out intent, out error),
                "上行注入 spec（field 34）必拒");
            CheckTrue(intent == null, "上行注入权威字段不返回部分对象");

            // 逐个注入「权威字段」的候选位置：AuthorityNetId / HitTargets / Stopped / Spec。
            int[] injectAt = { 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35 };
            for (int i = 0; i < injectAt.Length; i++)
            {
                f = SpawnFields();
                f.Add(FVarint(injectAt[i], 1u));
                payload = Payload(1, f.ToArray());
                CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(payload, 0, payload.Length, out intent, out error),
                    "上行注入字段 " + injectAt[i] + " 必拒");
            }

            // 上行命中批注入 spec / 权威 ID。
            f = HitFields(1, 100);
            f.Add(FBytes(34, specBody));
            payload = Payload(2, f.ToArray());
            PMProjectileHitBatch batch;
            CheckTrue(!PMProjectileCodec.TryDecodeHitBatch(payload, 0, payload.Length, out batch, out error),
                "上行命中批注入 spec（field 34）必拒");

            f = HitFields(1, 100);
            f.Insert(0, FVarint(9, 42u));
            payload = Payload(2, f.ToArray());
            CheckTrue(!PMProjectileCodec.TryDecodeHitBatch(payload, 0, payload.Length, out batch, out error),
                "上行命中批注入 authority 字段（field 9）必拒");

            // 客户端用「快照载荷」冒充上行 spawn：kind 门拒绝。
            byte[] snapshotPayload = SnapshotPayload(null, null);
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(snapshotPayload, 0, snapshotPayload.Length, out intent, out error),
                "跨通道：快照载荷喂给 spawn 解码器必拒");
            CheckContains(error, "载荷种类不符", "跨通道错用由 kind 门拒绝");

            byte[] hitPayload = Payload(2, HitFields(1, 100).ToArray());
            PMProjectileSnapshot snapshot;
            CheckTrue(!PMProjectileCodec.TryDecodeSnapshot(hitPayload, 0, hitPayload.Length, out snapshot, out error),
                "跨通道：命中批载荷喂给快照解码器必拒");

            byte[] decisionPayload = Payload(4, DecisionFields(9u, 2, "r").ToArray());
            PMProjectileHitBatch batch2;
            CheckTrue(!PMProjectileCodec.TryDecodeHitBatch(decisionPayload, 0, decisionPayload.Length, out batch2, out error),
                "跨通道：裁决载荷喂给命中批解码器必拒");

            PMProjectileDecision decision;
            CheckTrue(!PMProjectileCodec.TryDecodeDecision(snapshotPayload, 0, snapshotPayload.Length, out decision, out error),
                "跨通道：快照载荷喂给裁决解码器必拒");
        }

        // =================================================================================
        //  L. 数组隔离 / Clone / 跨 kind
        // =================================================================================

        private static void TestArrayIsolation()
        {
            string error;

            // 解码两次 → 两个独立对象与独立数组。
            byte[] payload = Payload(2, HitFields(2, 100).ToArray());
            PMProjectileHitBatch first;
            PMProjectileHitBatch second;
            PMProjectileCodec.TryDecodeHitBatch(payload, 0, payload.Length, out first, out error);
            PMProjectileCodec.TryDecodeHitBatch(payload, 0, payload.Length, out second, out error);
            CheckTrue(first != null && second != null, "HitBatch 两次解码都成功");
            if (first != null && second != null)
            {
                CheckTrue(!ReferenceEquals(first.Targets, second.Targets), "HitBatch 两次解码的 Targets 不是同一数组");
                first.Targets[0].TargetNetId = 9999u;
                CheckEq(second.Targets[0].TargetNetId, 100u, "HitBatch 修改一个解码结果的数组不影响另一个");
            }

            // 解码后修改原始载荷 → 已解码结果不受影响（说明读侧是复制而非别名）。
            byte[] snapshotPayload = SnapshotPayload(new uint[] { 11u, 22u }, new uint[] { 33u });
            PMProjectileSnapshot snapshot;
            PMProjectileCodec.TryDecodeSnapshot(snapshotPayload, 0, snapshotPayload.Length, out snapshot, out error);
            if (snapshot != null)
            {
                for (int i = 0; i < snapshotPayload.Length; i++)
                {
                    snapshotPayload[i] = 0x5A;
                }

                CheckEq(snapshot.State.HitTargets.Length, 2, "修改载荷后 HitTargets 长度不变");
                CheckEq(snapshot.State.HitTargets[1], 22u, "修改载荷后 HitTargets[1] 不变");
                CheckEq(snapshot.State.AllowedTargets[0], 33u, "修改载荷后 AllowedTargets[0] 不变");
            }

            // Snapshot.Clone 深拷贝：两个方向的隔离都要成立。
            uint[] hits = new uint[] { 1u, 2u };
            PMProjectileSnapshot source = new PMProjectileSnapshot();
            PMProjectileState sourceState = new PMProjectileState();
            sourceState.Key = new PMProjectileKey(Epoch, Owner, Proj, PMProjectileOrigin.ServerDirect);
            sourceState.AuthorityNetId = 7u;
            sourceState.HitTargets = hits;
            sourceState.AllowedTargets = new uint[] { 3u };
            source.Spec = new PMProjectileSpec();
            source.State = sourceState;

            PMProjectileSnapshot clone = source.Clone();
            CheckTrue(!ReferenceEquals(clone.State, source.State), "Clone 的 State 是新对象");
            CheckTrue(!ReferenceEquals(clone.State.HitTargets, source.State.HitTargets), "Clone 的 HitTargets 是新数组");
            CheckTrue(!ReferenceEquals(clone.Spec, source.Spec), "Clone 的 Spec 是新对象");
            hits[0] = 777u;
            CheckEq(clone.State.HitTargets[0], 1u, "修改源数组不影响 Clone");
            clone.State.AllowedTargets[0] = 888u;
            CheckEq(source.State.AllowedTargets[0], 3u, "修改 Clone 数组不影响源");
            clone.Spec.SpeedMps = 99f;
            CheckClose(source.Spec.SpeedMps, 10f, "修改 Clone 的 Spec 不影响源（默认值 10）");

            // HitBatch.Clone 同样隔离数组（共享契约里的实现，这里做一道确认）。
            PMProjectileHitBatch batch = new PMProjectileHitBatch();
            batch.Targets = new PMProjectileHitCandidate[1];
            PMProjectileHitBatch batchClone = batch.Clone();
            CheckTrue(!ReferenceEquals(batchClone.Targets, batch.Targets), "HitBatch.Clone 的 Targets 是新数组");

            // 空数组：不返回 null。
            byte[] emptySnapshot = SnapshotPayload(null, null);
            PMProjectileSnapshot empty;
            PMProjectileCodec.TryDecodeSnapshot(emptySnapshot, 0, emptySnapshot.Length, out empty, out error);
            if (empty != null)
            {
                CheckTrue(empty.State.HitTargets != null, "空 HitTargets 不是 null");
                CheckTrue(empty.State.AllowedTargets != null, "空 AllowedTargets 不是 null");
            }
        }

        // =================================================================================
        //  M. 载荷上限 / 生成桩 4096 登记
        // =================================================================================

        private static void TestSizeLimits()
        {
            // codec 上限常量。
            CheckEq(PMProjectileCodec.MaxPayloadBytes, 16384, "MaxPayloadBytes = 16384");
            CheckEq(PMProjectileCodec.MaxGeneratedRpcBytes, 4096, "MaxGeneratedRpcBytes = 4096（PMR3 生成桩上限）");
            CheckEq(PMProjectileCodec.MaxBatchTargets, 100, "MaxBatchTargets = 100");
            CheckEq(PMProjectileCodec.MaxSnapshotTargets, 100, "MaxSnapshotTargets = 100");
            CheckEq(PMProjectileCodec.MaxReasonBytes, 64, "MaxReasonBytes = 64");
            CheckEq(PMProjectileCodec.MaxPredictionMs, 500, "MaxPredictionMs = 500");
            CheckEq(PMProjectileCodec.RewindReportThresholdMs, 1000, "RewindReportThresholdMs = 1000");
            CheckEq(PMProjectileCodec.ProtocolVersion, 1, "ProtocolVersion = 1");

            // 超上限载荷：读侧在分配前拒绝。
            byte[] oversized = new byte[PMProjectileCodec.MaxPayloadBytes + 1];
            PMProjectileSpawnIntent doc;
            string error;
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(oversized, 0, oversized.Length, out doc, out error),
                "载荷 16385 字节必拒");
            CheckContains(error, "超过上限", "载荷超 16384 由长度门拒绝");

            // 合法输入能产生的最大载荷远小于 16384（登记：encode 的长度门是防御性的）。
            byte[] hit100 = Payload(2, HitFields(100, 1000).ToArray());
            uint[] hundred = new uint[100];
            for (int i = 0; i < 100; i++)
            {
                hundred[i] = (uint)(5000 + i);
            }

            byte[] snap100 = SnapshotPayload(hundred, hundred);
            Console.WriteLine("      实测：hit 100 候选 = " + hit100.Length + " 字节；snapshot 100+100 = "
                              + snap100.Length + " 字节");
            CheckTrue(hit100.Length <= PMProjectileCodec.MaxPayloadBytes, "100 候选仍在 codec 16384 上限内");
            CheckTrue(snap100.Length <= PMProjectileCodec.MaxPayloadBytes, "100+100 快照仍在 codec 16384 上限内");

            // 登记项：100 候选超过 PMR3 生成桩的 byte[] 上限 4096 → B2 必须切批或提升声明上限。
            CheckTrue(hit100.Length > PMProjectileCodec.MaxGeneratedRpcBytes,
                "登记：100 候选载荷（" + hit100.Length + " 字节）超过生成桩 byte[] 上限 4096，B2 需切批/限量");
        }

        // =================================================================================
        //  N. 独立字面 golden bytes / 裁决激活 ID 随 origin / 读端边界
        // =================================================================================

        /// <summary>
        /// **完全字面量**的 SpawnIntent（kind=1）wire 字节：不经过 PMNetWriter、不经过 codec 编码器，
        /// 逐字节按 protobuf 规范手写（tag = (field&lt;&lt;3)|wire、varint、fixed32 小端全部自己算）。
        /// 身份取 epoch=1 / owner=2 / projectileId=3 / origin=ClientPredicted(1)。
        ///
        /// 这条字面量与下面「codec 编码 == 该字面量」的断言合起来，作用域比 encode/decode 自比较大：
        /// 它同时钉住了 PMNetWriter 的 tag/varint/fixed32 是否与 protobuf 线格式逐字节一致，
        /// 以及字段顺序是否真的是 10→18 单调。
        ///
        /// 偏移对照（共 55 字节）：[0,12) 头部；[12,14) activationId；[14,29) position；
        /// [29,45) direction；[45,51) yaw（tag 2 字节 + fixed32）；[51,55) predictionMs。
        /// </summary>
        private static readonly byte[] GoldenSpawnIntentBytes = new byte[]
        {
            0x08, 0x01,                                     // field 1  kind = 1（SpawnIntent）
            0x10, 0x01,                                     // field 2  version = 1
            0x18, 0x01,                                     // field 3  epoch = 1
            0x20, 0x02,                                     // field 4  ownerNetId = 2
            0x28, 0x03,                                     // field 5  projectileId = 3
            0x30, 0x01,                                     // field 6  origin = 1（ClientPredicted）
            0x50, 0x07,                                     // field 10 activationId = 7
            0x5D, 0x00, 0x00, 0x80, 0x3F,                   // field 11 position.x = 1.0f
            0x65, 0x00, 0x00, 0x00, 0x3F,                   // field 12 position.y = 0.5f
            0x6D, 0x00, 0x00, 0x00, 0xBF,                   // field 13 position.z = -0.5f
            0x75, 0x00, 0x00, 0x80, 0x3F,                   // field 14 direction.x = 1.0f
            0x7D, 0x00, 0x00, 0x00, 0x00,                   // field 15 direction.y = 0
            0x85, 0x01, 0x00, 0x00, 0x00, 0x00,             // field 16 direction.z = 0（tag 两字节）
            0x8D, 0x01, 0x00, 0x00, 0xB4, 0x42,             // field 17 yaw = 90.0f
            0x90, 0x01, 0xC8, 0x01,                         // field 18 predictionMs = 200
        };

        /// <summary>
        /// **完全字面量**的 Decision（kind=4）：origin=ServerDirect(2) + activationId=0，
        /// Result=Confirmed，Reason 为空串（长度前缀 0x00，字段本身必须出现）。
        ///
        /// 这是「可信权威弹终态」在 wire 上唯一可能带 activationId=0 的形态
        /// （契约「激活ID=0仅可信ServerDirect可用」）。把 origin 字节换成 0x01（ClientPredicted）
        /// 就是必须被拒的伪终态，见 N3。
        /// </summary>
        private static readonly byte[] GoldenDecisionServerDirectZeroActivationBytes = new byte[]
        {
            0x08, 0x04,             // field 1  kind = 4（Decision）
            0x10, 0x01,             // field 2  version = 1
            0x18, 0x01,             // field 3  epoch = 1
            0x20, 0x02,             // field 4  ownerNetId = 2
            0x28, 0x03,             // field 5  projectileId = 3
            0x30, 0x02,             // field 6  origin = 2（ServerDirect）
            0x50, 0x00,             // field 10 activationId = 0
            0x58, 0x01,             // field 11 result = 1（Confirmed）
            0x62, 0x00,             // field 12 reason = ""（length 0）
        };

        private static void TestGoldenBytesAndReaderEdges()
        {
            string error;

            // ---- N1. SpawnIntent：字面 golden bytes，解码 + 编码双向对齐 ----
            CheckEq(GoldenSpawnIntentBytes.Length, 55, "N1 golden SpawnIntent 长度 = 55（自校验偏移注释）");
            PMProjectileSpawnIntent spawn;
            CheckTrue(PMProjectileCodec.TryDecodeSpawnIntent(GoldenSpawnIntentBytes, 0,
                    GoldenSpawnIntentBytes.Length, out spawn, out error),
                "N1 字面 golden SpawnIntent 可解码：" + error);
            if (spawn != null)
            {
                CheckKey(spawn.Key, new PMProjectileKey(1u, 2u, 3u, PMProjectileOrigin.ClientPredicted),
                    "N1 golden Key");
                CheckEq(spawn.ActivationId, 7u, "N1 golden ActivationId");
                CheckVec(spawn.Position, new PMVector3(1.0f, 0.5f, -0.5f), "N1 golden Position");
                CheckVec(spawn.Direction, new PMVector3(1.0f, 0f, 0f), "N1 golden Direction");
                CheckClose(spawn.Yaw, 90.0f, "N1 golden Yaw");
                CheckEq(spawn.PredictionMs, 200, "N1 golden PredictionMs");

                byte[] reencoded;
                CheckTrue(PMProjectileCodec.TryEncodeSpawnIntent(spawn, out reencoded, out error),
                    "N1 golden 可再编码：" + error);
                CheckBytesEqual(reencoded, GoldenSpawnIntentBytes,
                    "N1 codec 编码 == 字面 golden 字节（钉住 tag/varint/fixed32 与字段顺序）");
            }

            // ---- N2. 裁决：origin=ServerDirect + activationId=0 必须**可表达**（本轮修正的 bug）----
            PMProjectileDecision decision;
            CheckTrue(PMProjectileCodec.TryDecodeDecision(GoldenDecisionServerDirectZeroActivationBytes, 0,
                    GoldenDecisionServerDirectZeroActivationBytes.Length, out decision, out error),
                "N2 裁决 origin=ServerDirect + ActivationId=0 必须可解码（可信权威弹终态）：" + error);
            if (decision != null)
            {
                CheckKey(decision.Key, new PMProjectileKey(1u, 2u, 3u, PMProjectileOrigin.ServerDirect),
                    "N2 golden 裁决 Key");
                CheckEq(decision.ActivationId, 0u, "N2 golden 裁决 ActivationId=0");
                CheckTrue(decision.Result == PMActivationResult.Confirmed, "N2 golden 裁决 Result=Confirmed");
                CheckTrue(decision.Reason == string.Empty, "N2 golden 裁决 Reason 空串");
            }

            PMProjectileDecision serverDirect = new PMProjectileDecision();
            serverDirect.Key = new PMProjectileKey(1u, 2u, 3u, PMProjectileOrigin.ServerDirect);
            serverDirect.ActivationId = 0u;
            serverDirect.Result = PMActivationResult.Confirmed;
            serverDirect.Reason = string.Empty;
            byte[] encodedDecision;
            CheckTrue(PMProjectileCodec.TryEncodeDecision(serverDirect, out encodedDecision, out error),
                "N2 裁决 encode 允许 origin=ServerDirect + ActivationId=0：" + error);
            CheckBytesEqual(encodedDecision, GoldenDecisionServerDirectZeroActivationBytes,
                "N2 裁决 codec 编码 == 字面 golden 字节");

            // ---- N3. 裁决：origin=ClientPredicted + activationId=0 必须**被拒** ----
            byte[] clientZero = (byte[])GoldenDecisionServerDirectZeroActivationBytes.Clone();
            clientZero[11] = 0x01; // field 6 origin：2（ServerDirect）→ 1（ClientPredicted）
            PMProjectileDecision rejectedDecision;
            CheckTrue(!PMProjectileCodec.TryDecodeDecision(clientZero, 0, clientZero.Length, out rejectedDecision,
                    out error), "N3 裁决 origin=ClientPredicted + ActivationId=0 必拒");
            CheckTrue(rejectedDecision == null, "N3 拒绝时不返回部分对象");
            CheckContains(error, "ActivationId", "N3 由激活 ID 门拒绝");

            PMProjectileDecision clientPredictedZero = new PMProjectileDecision();
            clientPredictedZero.Key = new PMProjectileKey(1u, 2u, 3u, PMProjectileOrigin.ClientPredicted);
            clientPredictedZero.ActivationId = 0u;
            clientPredictedZero.Result = PMActivationResult.Rejected;
            byte[] ignored;
            CheckTrue(!PMProjectileCodec.TryEncodeDecision(clientPredictedZero, out ignored, out error),
                "N3 裁决 encode 拒绝 origin=ClientPredicted + ActivationId=0");
            CheckContains(error, "ActivationId", "N3 encode 由激活 ID 门拒绝");

            // 反例对照：同一个 ClientPredicted 裁决只要 activationId != 0 就必须通过。
            clientPredictedZero.ActivationId = 1u;
            CheckTrue(PMProjectileCodec.TryEncodeDecision(clientPredictedZero, out ignored, out error),
                "N3 对照：ClientPredicted + ActivationId=1 允许：" + error);
            serverDirect.ActivationId = 9u;
            CheckTrue(PMProjectileCodec.TryEncodeDecision(serverDirect, out ignored, out error),
                "N3 对照：ServerDirect + ActivationId=9 允许：" + error);

            // ---- N5. 非零 offset：offset/count 必须被尊重 ----
            byte[] boxed = new byte[GoldenSpawnIntentBytes.Length + 9];
            for (int i = 0; i < boxed.Length; i++)
            {
                boxed[i] = 0xEE;
            }

            Buffer.BlockCopy(GoldenSpawnIntentBytes, 0, boxed, 4, GoldenSpawnIntentBytes.Length);
            PMProjectileSpawnIntent boxedSpawn;
            CheckTrue(PMProjectileCodec.TryDecodeSpawnIntent(boxed, 4, GoldenSpawnIntentBytes.Length, out boxedSpawn,
                    out error), "N5 非零 offset + 前后垃圾字节内嵌解码：" + error);
            if (boxedSpawn != null)
            {
                CheckEq(boxedSpawn.ActivationId, 7u, "N5 内嵌解码字段正确");
            }

            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(boxed, 4, boxed.Length - 4, out boxedSpawn, out error),
                "N5 尾部垃圾被计入 count 时必拒（尾部字节门）");
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(boxed, 4, -1, out boxedSpawn, out error),
                "N5 负 count 必拒");
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(boxed, boxed.Length, 0, out boxedSpawn, out error),
                "N5 count=0 必拒（空载荷）");

            // ---- N6. 嵌套长度前缀溢出：0x7FFFFFFF 让读端 `_pos + length` 在 int 上回绕 ----
            // 要求 fail-closed 且**不得抛出异常**（异常会让本测试进程直接以非 0 退出）。
            List<byte[]> f = HitFields(0, 100);
            f.Add(FRawTag(17, PMWireType.LengthDelimited));
            f.Add(FVarintRaw(0x7FFFFFFFUL));
            byte[] overflowCandidate = Payload(2, f.ToArray());
            PMProjectileHitBatch overflowBatch;
            CheckTrue(!PMProjectileCodec.TryDecodeHitBatch(overflowCandidate, 0, overflowCandidate.Length,
                    out overflowBatch, out error), "N6 候选子消息声明长度 0x7FFFFFFF 必拒（不得越界/抛异常）");
            CheckTrue(overflowBatch == null, "N6 溢出拒绝时不返回部分对象");

            List<byte[]> specFields = SnapshotFields(null, null);
            specFields.RemoveAt(specFields.Count - 1);
            specFields.Add(FRawTag(34, PMWireType.LengthDelimited));
            specFields.Add(FVarintRaw(0x7FFFFFFFUL));
            byte[] overflowSpec = Payload(3, specFields.ToArray());
            PMProjectileSnapshot overflowSnapshot;
            CheckTrue(!PMProjectileCodec.TryDecodeSnapshot(overflowSpec, 0, overflowSpec.Length, out overflowSnapshot,
                    out error), "N6 spec 子消息声明长度 0x7FFFFFFF 必拒（不得越界/抛异常）");
            CheckTrue(overflowSnapshot == null, "N6 spec 溢出拒绝时不返回部分对象");

            // ---- N7. 超过 10 字节的 varint（long varint）→ FormatException → false ----
            byte[] longVarintBytes = Concat(
                FVarint(1, 1u),
                new byte[] { 0x10, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80 });
            PMProjectileSpawnIntent longDoc;
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(longVarintBytes, 0, longVarintBytes.Length, out longDoc,
                    out error), "N7 10 字节全是续位的 varint 必拒（long varint）");
            CheckContains(error, "varint", "N7 由 varint 长度门上抛并被转成 false");

            // ---- N8. 快照时间字段的负值（补齐 stopWall/timeAfterStopped/tombstone 的负向）----
            CheckSnapshotRejected(29, FDouble(29, -1.0), "stopWallTimeMs = -1");
            CheckSnapshotRejected(30, FDouble(30, -1.0), "timeAfterStoppedMs = -1");
            CheckSnapshotRejected(31, FDouble(31, -1.0), "tombstoneUntilMs = -1");

            // ---- N9. 快照 bool 的另一极性（true/false 双向）----
            List<byte[]> flip = SnapshotFields(null, null);
            flip[SnapshotIndex(flip, 26)] = FBool(26, false);
            flip[SnapshotIndex(flip, 27)] = FBool(27, true);
            flip[SnapshotIndex(flip, 28)] = FBool(28, false);
            byte[] flipPayload = Payload(3, flip.ToArray());
            PMProjectileSnapshot flipped;
            CheckTrue(PMProjectileCodec.TryDecodeSnapshot(flipPayload, 0, flipPayload.Length, out flipped, out error),
                "N9 快照 bool 反极性可解码：" + error);
            if (flipped != null)
            {
                CheckTrue(!flipped.State.Stopped, "N9 Stopped=false 往返");
                CheckTrue(flipped.State.Hidden, "N9 Hidden=true 往返");
                CheckTrue(!flipped.State.TakenOver, "N9 TakenOver=false 往返");
                byte[] flipBack;
                CheckTrue(PMProjectileCodec.TryEncodeSnapshot(flipped, out flipBack, out error),
                    "N9 反极性可再编码：" + error);
                CheckBytesEqual(flipBack, flipPayload, "N9 快照 bool 反极性字节级往返");
            }

            // ---- N10. HitBatch 的 repeated 尾部边界语义（与 _r5_codec_report §7.5 一致）----
            byte[] hitFull = Payload(2, HitFields(1, 100).ToArray());
            byte[] hitPartial = Payload(2, HitFields(0, 100).ToArray());
            bool prefix = hitPartial.Length < hitFull.Length;
            for (int i = 0; prefix && i < hitPartial.Length; i++)
            {
                if (hitPartial[i] != hitFull[i])
                {
                    prefix = false;
                }
            }

            CheckTrue(prefix, "N10 0 候选载荷是 1 候选载荷的严格字节前缀");

            PMProjectileHitBatch partialBatch;
            CheckTrue(PMProjectileCodec.TryDecodeHitBatch(hitPartial, 0, hitPartial.Length, out partialBatch, out error),
                "N10 0 候选在 codec 层合法（空批由 L0 NoTargets 拒，不是 codec 拒）：" + error);
            if (partialBatch != null)
            {
                CheckEq(partialBatch.Targets.Length, 0, "N10 边界截断解出 0 候选（结构完整但更短）");
                CheckEq(partialBatch.RewindMs, 100, "N10 边界截断保留 rewindMs");
                CheckClose(partialBatch.PreviousPosition.X, -1f, "N10 边界截断保留 previousPosition");
            }

            PMProjectileHitBatch fullBatch;
            CheckTrue(PMProjectileCodec.TryDecodeHitBatch(hitFull, 0, hitFull.Length, out fullBatch, out error),
                "N10 1 候选载荷可解码：" + error);
            if (fullBatch != null)
            {
                CheckEq(fullBatch.Targets.Length, 1, "N10 完整载荷解出 1 候选");
            }

            // ---- N11. 以字面 golden 为基准的负向：未知 / 重复 / 截断 / NaN ----
            byte[] withUnknown = Concat(GoldenSpawnIntentBytes, new byte[] { 0x98, 0x01, 0x01 }); // field 19
            PMProjectileSpawnIntent bad;
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(withUnknown, 0, withUnknown.Length, out bad, out error),
                "N11 字面 golden + 未知字段 19 必拒");
            CheckContains(error, "未知字段", "N11 由未知字段门拒绝");

            byte[] duplicated = Concat(GoldenSpawnIntentBytes, new byte[] { 0x90, 0x01, 0xC8, 0x01 }); // 重复 18
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(duplicated, 0, duplicated.Length, out bad, out error),
                "N11 字面 golden + 重复 predictionMs 必拒");
            CheckContains(error, "重复", "N11 由顺序门拒绝重复");

            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(GoldenSpawnIntentBytes, 0,
                    GoldenSpawnIntentBytes.Length - 1, out bad, out error),
                "N11 字面 golden 截断 1 字节（yaw 值中间）必拒");

            // yaw 的 fixed32 值在原 golden 里的偏移 = 长度 - predictionMs 整字段(4) - yaw 值(4)。
            int yawValueAt = GoldenSpawnIntentBytes.Length - 8;
            CheckTrue(GoldenSpawnIntentBytes[yawValueAt] == 0x00 && GoldenSpawnIntentBytes[yawValueAt + 1] == 0x00
                      && GoldenSpawnIntentBytes[yawValueAt + 2] == 0xB4 && GoldenSpawnIntentBytes[yawValueAt + 3] == 0x42,
                "N11 golden 的 yaw 偏移自校验（90.0f = 00 00 B4 42）");
            byte[] nanYaw = (byte[])GoldenSpawnIntentBytes.Clone();
            nanYaw[yawValueAt] = 0x00;
            nanYaw[yawValueAt + 1] = 0x00;
            nanYaw[yawValueAt + 2] = 0xC0;
            nanYaw[yawValueAt + 3] = 0x7F; // 0x7FC00000 = quiet NaN
            CheckTrue(!PMProjectileCodec.TryDecodeSpawnIntent(nanYaw, 0, nanYaw.Length, out bad, out error),
                "N11 字面 golden 的 yaw 换成 NaN 位型必拒");
        }
    }
}
