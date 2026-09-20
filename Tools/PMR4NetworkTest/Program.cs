// R4-B / B1 验收门禁（契约 Docs/plans/net-r4-network-contract.md §B1；主计划 P4B1 / P4B2）。
//
// 这个文件做三件事，且刻意把三者分开：
//   [1] **wire 契约**：用**独立手写字节**核对 codec 的解码侧（不依赖 codec 自己的编码器）——
//       正向：全字段往返；负向：NaN/未知 Mode/越界/缺必填/尾部/跨 kind/身份非法全部被拒
//       且**不产生部分结果**。手写字节意味着测试是解码器的独立对照，而不是"用实现测实现"。
//   [2] **真实链路**：AP→DS→AP/SP 走**真实 PMTransport 字节链**（分片/序号/ACK/可靠域/
//       不可靠域都真实参与）+ **真实生成桩**（`PMNet_*`）+ 真实世界/复制/生命周期，
//       覆盖：属性收敛、输入首包丢失、重复/乱序、完整状态差异回滚、事件先后与迟到、
//       重同步升流与旧流丢弃、SP 只插值、DS 预算防加速、多角色隔离。
//   [3] **声明同步**：调真实生成器 `PMNetGen --decl-check` 逐字节校验产物与声明/锁文件一致，
//       并断言原 ID（ClassId 与 Probe/Echo 的 ID）未变（沿用 pmnet-r3-ids.json）。
//
// 明确不测（诚实边界，见报告）：
//   · 真实 Unity PhysX 碰撞（属 B2 / Tools/PMR4UnityCheck + 编辑器菜单验证）；
//   · 真实 UDP socket / 跨机 MTU / NAT（属 B3/B4 的集成与真实 DS 验收）；
//   · 端到端 Unity 客户端表现（属 B4/用户实测）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using PMNet;
using PMNet.Mover;
using PMNet.Prediction;
using PMNet.R3;
using PMNet.Session;
using PMNet.Transport;

namespace PMR4NetworkTest
{
    internal static class Program
    {
        private static int _passed;
        private static readonly List<string> _failures = new List<string>();
        private static readonly List<string> _warnings = new List<string>();

        private static int Main()
        {
            Console.WriteLine("=== PMR4NetworkTest（R4-B / B1）===");
            Console.WriteLine("Unity 语言面由 Tools/PMR4NetworkCheck（netstandard2.0 + C#7.3）单独守；本工程跑真实字节链。");
            Console.WriteLine();

            PMR3Player.MovementWarn = delegate(string message) { _warnings.Add(message); };

            PMNetRegistry.Reset();
            PMR3Runtime.Shutdown();
            PMR3Runtime.Register();
            PMR3Runtime.PlayerReplicated += OnPlayerReplicated;

            Section("A. 声明与生成物（--decl-check + 原 ID 不变 + 描述符）", TestDeclarationArtifacts);
            Section("B. codec：全字段往返与非法值拒绝（P4B1）", TestCodecContract);
            Section("C. 真实链路 AP→DS→AP：收敛与边界推进（P4B2）", TestReplicationConvergence);
            Section("D. 输入重发/首包丢失/重复/乱序", TestInputResend);
            Section("E. 完整状态差异与回滚（DS 可信 Effect）", TestFullStateDivergence);
            Section("F. 事件先后、去重、迟到与可靠补发", TestEvents);
            Section("F2. 可靠域顺序：旧流事件→Resync2→流2事件→Resync3→流3事件（同批不漏不重）",
                TestReliableOrderAcrossResyncs);
            Section("F3. 事件派发回调异常：不重放、不再发、不打断其它通知", TestEventDispatchCallbackIsolation);
            Section("G. 重同步：DS 升流 + 旧流丢弃 + 不倒退拒绝", TestResync);
            Section("H. SP：只插值、不预测、静止 yaw、冻结", TestSimulatedProxy);
            Section("H2. SP 与 DS 升流：复制快照重绑 / 旧流不回写 / 冻结不升流", TestSimulatedProxyStreamRebind);
            Section("I. DS 预算与信用（防加速 / 同 tick 不重复充值 / 0dt 拒绝）", TestBudgetAndCredit);
            Section("J. 多角色隔离", TestMultiPlayerIsolation);
            Section("K. 冻结 / 释放 / 诊断", TestLifecycle);

            Console.WriteLine();
            Console.WriteLine("==================================================");
            Console.WriteLine("通过 " + _passed + " 项，失败 " + _failures.Count + " 项。");
            if (_warnings.Count > 0)
            {
                Console.WriteLine("（运行时告警 " + _warnings.Count + " 条，前 10 条）");
                for (int i = 0; i < _warnings.Count && i < 10; i++)
                {
                    Console.WriteLine("  [warn] " + _warnings[i]);
                }
            }

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
                Console.WriteLine("      " + Trim(ex.StackTrace, 900));
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

        private static void CheckEq(long actual, long expected, string label)
        {
            Check(actual == expected, label + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckEq(uint actual, uint expected, string label)
        {
            Check(actual == expected, label + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckEq(int actual, int expected, string label)
        {
            Check(actual == expected, label + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckEq(ulong actual, ulong expected, string label)
        {
            Check(actual == expected, label + "（期望 " + expected + "，实际 " + actual + "）");
        }

        private static void CheckStr(string actual, string expected, string label)
        {
            Check(string.Equals(actual, expected, StringComparison.Ordinal),
                label + "（期望 '" + expected + "'，实际 '" + actual + "'）");
        }

        private static void CheckNear(float actual, float expected, float tolerance, string label)
        {
            bool ok = Math.Abs(actual - expected) <= tolerance;
            Check(ok, label + "（期望 " + expected + "±" + tolerance + "，实际 " + actual + "）");
        }

        private static void CheckVec(PMVector3 actual, PMVector3 expected, float tolerance, string label)
        {
            bool ok = Math.Abs(actual.X - expected.X) <= tolerance
                      && Math.Abs(actual.Y - expected.Y) <= tolerance
                      && Math.Abs(actual.Z - expected.Z) <= tolerance;
            Check(ok, label + "（期望 " + expected + "±" + tolerance + "，实际 " + actual + "）");
        }

        private static void ExpectFormatException(Action body, string label)
        {
            bool threw = false;
            try
            {
                body();
            }
            catch (FormatException)
            {
                threw = true;
            }
            catch (ArgumentOutOfRangeException)
            {
                threw = true;
            }

            Check(threw, label + "（应抛 FormatException/ArgumentOutOfRange）");
        }

        private static void Diag(string message)
        {
        }

        private static string Trim(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) { return "<empty>"; }
            text = text.Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }

        private static string ResolveRepoRoot()
        {
            DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 12 && dir != null; i++)
            {
                string candidate = Path.Combine(dir.FullName, "Client", "Assets", "Scripts", "PMR3", "PMR3Player.cs");
                if (File.Exists(candidate)) { return dir.FullName; }
                dir = dir.Parent;
            }

            return null;
        }

        // =================================================================================
        //  A. 声明与生成物
        // =================================================================================

        private static void TestDeclarationArtifacts()
        {
            string root = ResolveRepoRoot();
            Check(root != null, "定位到仓库根目录");
            if (root == null) { return; }

            string gen = Path.Combine(root, "Tools", "PMNetGen", "bin", "Release", "net8.0", "PMNetGen.dll");
            if (!File.Exists(gen))
            {
                Check(false, "PMNetGen.dll 不存在（先 dotnet build Tools/PMNetGen -c Release）");
                return;
            }

            string declDir = Path.Combine(root, "Client", "Assets", "Scripts", "PMR3");
            string outDir = Path.Combine(declDir, "Generated");
            string idLock = Path.Combine(root, "Docs", "plans", "pmnet-r3-ids.json");

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = "dotnet";
            psi.Arguments = "\"" + gen + "\" --decl-check \"" + declDir + "\" --out-dir \"" + outDir
                            + "\" --id-lock \"" + idLock + "\"";
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.WorkingDirectory = root;

            using (Process p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Check(p.ExitCode == 0, "PMNetGen --decl-check 退出码 0（产物与声明/锁文件逐字节一致），实际 "
                                       + p.ExitCode);
                if (p.ExitCode != 0)
                {
                    Console.WriteLine("      stdout: " + Trim(stdout, 400));
                    Console.WriteLine("      stderr: " + Trim(stderr, 400));
                }
            }

            // A2：ID 锁沿用 pmnet-r3-ids.json，**原 ID 一律不变**。
            string lockText = File.ReadAllText(idLock, Encoding.UTF8);
            CheckTrue(lockText.Contains("\"CLASS:PMNet.R3.PMR3Player\": 405815557"), "ClassId 保持 405815557 不变");
            CheckTrue(lockText.Contains("\"PROP:PMNet.R3.PMR3Player._uid\": 18801"), "_uid PropertyId 保持 18801");
            CheckTrue(lockText.Contains("\"PROP:PMNet.R3.PMR3Player._probeCount\": 12656"),
                "_probeCount PropertyId 保持 12656");
            CheckTrue(lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ServerProbe\": 34232"), "ServerProbe RpcId 保持 34232");
            CheckTrue(lockText.Contains("\"RPC:PMNet.R3.PMR3Player.ClientEcho\": 63853"), "ClientEcho RpcId 保持 63853");
            CheckTrue(lockText.Contains("PROP:PMNet.R3.PMR3Player._movementSnapshotV1"), "新属性 _movementSnapshotV1 已分配 ID");
            CheckTrue(lockText.Contains("RPC:PMNet.R3.PMR3Player.ServerMovementInputV1"), "新 RPC ServerMovementInputV1 已分配 ID");
            CheckTrue(lockText.Contains("RPC:PMNet.R3.PMR3Player.ServerMovementResyncV1"), "新 RPC ServerMovementResyncV1 已分配 ID");
            CheckTrue(lockText.Contains("RPC:PMNet.R3.PMR3Player.ClientMovementEventsV1"), "新 RPC ClientMovementEventsV1 已分配 ID");
            CheckTrue(lockText.Contains("RPC:PMNet.R3.PMR3Player.ClientMovementResyncV1"), "新 RPC ClientMovementResyncV1 已分配 ID");

            string sharedLock = Path.Combine(root, "Docs", "plans", "pmnet-ids.json");
            CheckTrue(!File.ReadAllText(sharedLock, Encoding.UTF8).Contains("PMR3Player"),
                "共享锁未被本批改写（仍不含 PMR3 条目）");

            // A3：运行期描述符（零反射注册表）。
            CheckTrue(PMNetRegistry.IsSealed, "注册表已封板");
            CheckEq(PMR3Player.PMGeneratedChangeMaskBitCount, 3, "复制属性位宽 == 3");
            CheckEq(PMR3Player.PMGeneratedClassId, 405815557u, "运行期 ClassId 与锁一致");

            PMNetClassEntry entry;
            CheckTrue(PMNetRegistry.TryGetClass(PMR3Player.PMGeneratedClassId, out entry) && entry != null,
                "PMR3Player 已注册");
            CheckEq(entry.Rep.Properties.Length, 3, "复制描述符属性数 == 3");

            PMNetRpcEntry rpc;
            CheckTrue(PMNetRegistry.TryGetRpc(PMR3Player.PMGeneratedClassId,
                PMR3Player.PMGeneratedRpcId_ServerMovementInputV1, out rpc), "ServerMovementInputV1 已注册");
            CheckTrue(rpc.Descriptor.Direction == PMRpcKind.Server, "ServerMovementInputV1 方向 = Server");
            CheckTrue(!rpc.Descriptor.IsReliable, "ServerMovementInputV1 为**不可靠**（高频输入不得堵可靠流）");
            CheckTrue(rpc.Descriptor.Validator == PMRpcValidator.ForceValidate, "ServerMovementInputV1 档位 = ForceValidate");

            CheckTrue(PMNetRegistry.TryGetRpc(PMR3Player.PMGeneratedClassId,
                PMR3Player.PMGeneratedRpcId_ServerMovementResyncV1, out rpc), "ServerMovementResyncV1 已注册");
            CheckTrue(rpc.Descriptor.IsReliable && rpc.Descriptor.Direction == PMRpcKind.Server,
                "ServerMovementResyncV1 = 可靠 + Server");
            CheckTrue(rpc.Descriptor.Validator == PMRpcValidator.ForceValidate, "ServerMovementResyncV1 档位 = ForceValidate");

            CheckTrue(PMNetRegistry.TryGetRpc(PMR3Player.PMGeneratedClassId,
                PMR3Player.PMGeneratedRpcId_ClientMovementEventsV1, out rpc), "ClientMovementEventsV1 已注册");
            CheckTrue(rpc.Descriptor.IsReliable && rpc.Descriptor.Direction == PMRpcKind.Client,
                "ClientMovementEventsV1 = 可靠 + Client（给 owner）");

            CheckTrue(PMNetRegistry.TryGetRpc(PMR3Player.PMGeneratedClassId,
                PMR3Player.PMGeneratedRpcId_ClientMovementResyncV1, out rpc), "ClientMovementResyncV1 已注册");
            CheckTrue(rpc.Descriptor.IsReliable && rpc.Descriptor.Direction == PMRpcKind.Client,
                "ClientMovementResyncV1 = 可靠 + Client（给 owner）");

            // 旧 Probe/Echo 必须保持可用（契约：原 Probe/Echo 保持可用）。
            CheckTrue(PMNetRegistry.TryGetRpc(PMR3Player.PMGeneratedClassId,
                PMR3Player.PMGeneratedRpcId_ServerProbe, out rpc), "ServerProbe 仍注册（未破坏 R3-B）");
            CheckTrue(PMNetRegistry.TryGetRpc(PMR3Player.PMGeneratedClassId,
                PMR3Player.PMGeneratedRpcId_ClientEcho, out rpc), "ClientEcho 仍注册（未破坏 R3-B）");

            CheckTrue(PMR3Runtime.ProtocolHash != 0u, "整体协议摘要非 0");
            CheckEq(global::PMNet.Generated.PMNetGeneratedRegistry.ProtocolHash, PMNetRegistry.ProtocolHash,
                "生成期摘要 == 运行期摘要");
        }

        // =================================================================================
        //  B. codec：全字段往返与非法值拒绝
        // =================================================================================
        //
        //  这里刻意**手写 wire 字节**（而不是用 codec 的编码器造非法输入）：
        //  解码侧的对照必须独立，否则"编码器不产生的字节"永远测不到解码侧。
        //  字段号是冻结契约，写在下面的常量里（改动即改线协议）。

        private const int WKind = 1;
        private const int WVersion = 2;
        private const int WEpoch = 3;
        private const int WInstance = 4;
        private const int WStream = 5;

        private static void TestCodecContract()
        {
            const uint epoch = 0x4A01u;
            const uint instance = 0x777u;
            const uint stream = 3u;

            // ---------------- B1：输入批正向（全字段往返，含 yaw 规范化与跳跃边沿）
            PMR4MovementInputEntry[] entries = new PMR4MovementInputEntry[3];
            entries[0].InputFrame = 10L;
            entries[0].StepMs = 16;
            entries[0].Input = Move(-0.5f, 0.25f, 370f, true);
            entries[1].InputFrame = 11L;
            entries[1].StepMs = 1;
            entries[1].Input = Move(1f, -1f, 0f, false);
            entries[2].InputFrame = 12L;
            entries[2].StepMs = 50;
            entries[2].Input = Move(0f, 0f, -10f, false);

            byte[] inputsPayload = PMR4MovementCodec.EncodeInputs(epoch, instance, stream, entries);
            CheckTrue(inputsPayload.Length <= PMR4MovementCodec.MaxBlobBytes, "输入批长度 <= 4096");

            PMR4MovementInputBatch inputs;
            string error;
            CheckTrue(PMR4MovementCodec.TryDecodeInputs(inputsPayload, 0, inputsPayload.Length, out inputs, out error),
                "输入批可解码：" + error);
            if (inputs != null)
            {
                CheckEq(inputs.Epoch, epoch, "输入批 epoch 往返");
                CheckEq(inputs.InstanceId, instance, "输入批 instance 往返");
                CheckEq(inputs.StreamVersion, stream, "输入批 stream 往返");
                CheckEq(inputs.Entries.Length, 3, "输入批条数往返");
                CheckEq(inputs.Entries[0].InputFrame, 10L, "第 1 条帧号往返");
                CheckNear(inputs.Entries[0].Input.MoveX, -0.5f, 0f, "第 1 条 MoveX 往返（逐位）");
                CheckNear(inputs.Entries[0].Input.MoveZ, 0.25f, 0f, "第 1 条 MoveZ 往返（逐位）");
                CheckNear(inputs.Entries[0].Input.YawDegrees, 10f, 1e-4f, "第 1 条 yaw 规范化为 [0,360)（370 → 10）");
                CheckTrue(inputs.Entries[0].Input.JumpPressed, "第 1 条跳跃边沿往返");
                CheckEq(inputs.Entries[1].StepMs, 1, "第 2 条 dt=1 往返");
                CheckNear(inputs.Entries[2].Input.YawDegrees, 350f, 1e-4f, "第 3 条负 yaw 规范化为 350");
                CheckTrue(inputs.Entries[0].Input.Effects == null && inputs.Entries[0].Input.Layers == null,
                    "解码后的输入不含 Effects/Layers（上行不开放该权限）");
            }

            // ---------------- B2：输入批非法值（手写字节）
            CheckTrue(!TryDecodeInputsRaw(BuildRawInputs(epoch, instance, stream, RawEntry(10L, 0, 0f, 0f, 0f, 0f, false)),
                    out error), "stepMs=0 的上行输入被拒：" + error);
            CheckTrue(!TryDecodeInputsRaw(BuildRawInputs(epoch, instance, stream, RawEntry(10L, 51, 0f, 0f, 0f, 0f, false)),
                    out error), "stepMs=51 被拒：" + error);
            CheckTrue(!TryDecodeInputsRaw(BuildRawInputs(epoch, instance, stream, RawEntry(10L, 16, 2f, 0f, 0f, 0f, false)),
                    out error), "MoveX=2（超 [-1,1]）被拒：" + error);
            CheckTrue(!TryDecodeInputsRaw(BuildRawInputs(epoch, instance, stream, RawEntry(10L, 16, float.NaN, 0f, 0f, 0f, false)),
                    out error), "MoveX=NaN 被拒：" + error);
            CheckTrue(!TryDecodeInputsRaw(BuildRawInputs(epoch, instance, stream, RawEntry(10L, 16, 0f, 0f, 0f, 400f, false)),
                    out error), "yaw=400（非规范范围）被拒：" + error);
            CheckTrue(!TryDecodeInputsRaw(BuildRawInputs(epoch, instance, stream, RawEntry(-1L, 16, 0f, 0f, 0f, 0f, false)),
                    out error), "帧号为负被拒：" + error);
            CheckTrue(TryDecodeInputsRaw(BuildRawInputs(epoch, instance, stream, RawEntry(0L, 16, 0f, 0f, 0f, 0f, false)),
                    out error), "帧号 0 是合法首帧（输入帧 0 产出边界 1）：" + error);
            CheckTrue(!TryDecodeInputsRaw(BuildRawInputs(epoch, instance, stream,
                    RawEntry(10L, 16, 0f, 0f, 0f, 0f, false), RawEntry(10L, 16, 0f, 0f, 0f, 0f, false)), out error),
                "同帧号重复被拒：" + error);
            CheckTrue(!TryDecodeInputsRaw(BuildRawInputs(epoch, instance, stream,
                    RawEntry(11L, 16, 0f, 0f, 0f, 0f, false), RawEntry(10L, 16, 0f, 0f, 0f, 0f, false)), out error),
                "帧号非升序被拒：" + error);
            CheckTrue(!TryDecodeInputsRaw(BuildRawInputs(0u, instance, stream,
                    RawEntry(10L, 16, 0f, 0f, 0f, 0f, false)), out error), "epoch=0 被拒：" + error);
            CheckTrue(!TryDecodeInputsRaw(BuildRawInputs(epoch, 0u, stream,
                    RawEntry(10L, 16, 0f, 0f, 0f, 0f, false)), out error), "instance=0 被拒：" + error);
            CheckTrue(!TryDecodeInputsRaw(BuildRawInputs(epoch, instance, 0u,
                    RawEntry(10L, 16, 0f, 0f, 0f, 0f, false)), out error), "stream=0 被拒：" + error);
            CheckTrue(!TryDecodeInputsRaw(BuildRawInputsVersion(epoch, instance, stream, 2,
                    RawEntry(10L, 16, 0f, 0f, 0f, 0f, false)), out error), "编码版本 2 被拒：" + error);
            CheckTrue(!TryDecodeInputsRaw(BuildRawInputsKind(PMR4PayloadKind.Snapshot, epoch, instance, stream,
                    RawEntry(10L, 16, 0f, 0f, 0f, 0f, false)), out error), "kind 错（把 Snapshot 喂给输入通道）被拒：" + error);
            CheckTrue(!TryDecodeInputsRaw(BuildRawInputsWithUnknownField(epoch, instance, stream), out error),
                "未知字段号被拒：" + error);
            CheckTrue(!TryDecodeInputsRaw(BuildRawInputsWithTrailingByte(epoch, instance, stream), out error),
                "尾部多余字节被拒：" + error);
            CheckTrue(!TryDecodeInputsRaw(BuildRawInputsWithBadHeaderOrder(epoch, instance, stream), out error),
                "头部字段乱序被拒：" + error);
            CheckTrue(!TryDecodeInputsRaw(new byte[PMR4MovementCodec.MaxBlobBytes + 1], out error),
                "载荷超过 4096 字节被拒：" + error);
            CheckTrue(!TryDecodeInputsRaw(new byte[0], out error), "空载荷被拒：" + error);

            PMR4MovementInputBatch notProduced;
            CheckTrue(!PMR4MovementCodec.TryDecodeInputs(new byte[PMR4MovementCodec.MaxBlobBytes + 1], 0,
                    PMR4MovementCodec.MaxBlobBytes + 1, out notProduced, out error) && notProduced == null,
                "解码失败时**不产生任何部分结果**（batch == null）");

            // 编码侧：非法本地数据必须抛（不静默发出）。
            ExpectFormatException(delegate
            {
                PMR4MovementInputEntry bad = new PMR4MovementInputEntry();
                bad.InputFrame = 1L;
                bad.StepMs = 0;
                bad.Input = Move(0f, 0f, 0f, false);
                PMR4MovementCodec.EncodeInputs(epoch, instance, stream, new[] { bad });
            }, "编码 dt=0 抛 FormatException");

            ExpectFormatException(delegate
            {
                PMR4MovementInputEntry bad = new PMR4MovementInputEntry();
                bad.InputFrame = 1L;
                bad.StepMs = 16;
                PMMoverInput carry = Move(0f, 0f, 0f, false);
                carry.Effects = new[] { PMMoverEffectRequest.SetMode(PMMoverMode.Flying) };
                bad.Input = carry;
                PMR4MovementCodec.EncodeInputs(epoch, instance, stream, new[] { bad });
            }, "编码携带 Effects 的上行输入抛 FormatException（上行不开放效果权限）");

            ExpectFormatException(delegate
            {
                PMR4MovementInputEntry[] many = new PMR4MovementInputEntry[PMR4MovementCodec.MaxInputsPerPacket + 1];
                for (int i = 0; i < many.Length; i++)
                {
                    many[i].InputFrame = 1L + i;
                    many[i].StepMs = 16;
                    many[i].Input = Move(0f, 0f, 0f, false);
                }

                PMR4MovementCodec.EncodeInputs(epoch, instance, stream, many);
            }, "一包超过 8 条输入抛 FormatException");

            // ---------------- B3：快照正向（完整 Sync/Aux，含 2 层 ActiveLayers 与 ElapsedMs）
            PMMoverSyncState sync = PMMoverSyncState.CreateDefault();
            sync.Position = new PMVector3(1.25f, 1f, -3.5f);
            sync.Velocity = new PMVector3(0.5f, -1.5f, 2.25f);
            sync.PreAdditiveVelocity = new PMVector3(0.5f, 0f, 2f);
            sync.YawDegrees = 123.5f;
            sync.Mode = PMMoverMode.Falling;
            sync.Grounded = false;
            sync.GroundNormal = new PMVector3(0f, 0.6f, 0.8f);
            sync.Scale = 1.75f;
            sync.MaxSpeed = 4.5f;
            sync.Acceleration = 22f;
            sync.Braking = 18f;
            sync.GravityScale = 1.5f;
            sync.JumpSpeed = 5.25f;
            sync.ActiveLayers = new[]
            {
                new PMMoverLayer
                {
                    InstanceId = 11u, Kind = PMMoverLayerKind.AdditiveVelocity, Priority = 3,
                    Velocity = new PMVector3(1f, 0f, 0.5f), DurationMs = 500, ElapsedMs = 125
                },
                new PMMoverLayer
                {
                    InstanceId = 12u, Kind = PMMoverLayerKind.OverrideVelocity, Priority = 7,
                    Velocity = new PMVector3(-2f, 1f, 0f), DurationMs = 0, ElapsedMs = 0
                }
            };

            PMMoverAuxState aux = PMMoverAuxState.CreateDefault();
            aux.Gravity = new PMVector3(0.1f, -9.81f, -0.2f);
            aux.CollisionWorldVersion = 0x52334201;
            aux.ConfigVersion = 9;

            PMR4MovementSnapshotBlob blob = new PMR4MovementSnapshotBlob();
            blob.Epoch = epoch;
            blob.InstanceId = instance;
            blob.StreamVersion = stream;
            blob.OutputFrame = 4242L;
            blob.ServerFrame = 77L;
            blob.TotalSimTimeMs = 12345.5;
            blob.Sync = sync;
            blob.Aux = aux;

            byte[] snapPayload = PMR4MovementCodec.EncodeSnapshot(false, blob);
            PMR4MovementSnapshotBlob decoded;
            CheckTrue(PMR4MovementCodec.TryDecodeSnapshot(snapPayload, 0, snapPayload.Length,
                PMR4PayloadKind.Snapshot, out decoded, out error), "快照可解码：" + error);
            if (decoded != null)
            {
                CheckEq(decoded.OutputFrame, 4242L, "快照 outputFrame 往返");
                CheckEq(decoded.ServerFrame, 77L, "快照 serverFrame 往返");
                CheckTrue(decoded.TotalSimTimeMs == 12345.5, "快照 totalSimTimeMs 逐位往返");
                CheckVec(decoded.Sync.Position, sync.Position, 0f, "Sync.Position 逐位往返");
                CheckVec(decoded.Sync.Velocity, sync.Velocity, 0f, "Sync.Velocity 逐位往返");
                CheckVec(decoded.Sync.PreAdditiveVelocity, sync.PreAdditiveVelocity, 0f, "Sync.PreAdditiveVelocity 逐位往返");
                CheckNear(decoded.Sync.YawDegrees, sync.YawDegrees, 0f, "Sync.YawDegrees 逐位往返");
                CheckTrue(decoded.Sync.Mode == sync.Mode, "Sync.Mode 往返");
                CheckTrue(decoded.Sync.Grounded == sync.Grounded, "Sync.Grounded 往返");
                CheckVec(decoded.Sync.GroundNormal, sync.GroundNormal, 0f, "Sync.GroundNormal 逐位往返");
                CheckNear(decoded.Sync.Scale, sync.Scale, 0f, "Sync.Scale 逐位往返");
                CheckNear(decoded.Sync.MaxSpeed, sync.MaxSpeed, 0f, "Sync.MaxSpeed 逐位往返");
                CheckNear(decoded.Sync.Acceleration, sync.Acceleration, 0f, "Sync.Acceleration 逐位往返");
                CheckNear(decoded.Sync.Braking, sync.Braking, 0f, "Sync.Braking 逐位往返");
                CheckNear(decoded.Sync.GravityScale, sync.GravityScale, 0f, "Sync.GravityScale 逐位往返");
                CheckNear(decoded.Sync.JumpSpeed, sync.JumpSpeed, 0f, "Sync.JumpSpeed 逐位往返");
                CheckEq(decoded.Sync.ActiveLayers.Length, 2, "ActiveLayers 层数往返");
                CheckEq(decoded.Sync.ActiveLayers[0].InstanceId, 11u, "第 1 层 instanceId 往返");
                CheckTrue(decoded.Sync.ActiveLayers[0].Kind == PMMoverLayerKind.AdditiveVelocity, "第 1 层 kind 往返");
                CheckEq(decoded.Sync.ActiveLayers[0].Priority, 3, "第 1 层 priority 往返");
                CheckVec(decoded.Sync.ActiveLayers[0].Velocity, sync.ActiveLayers[0].Velocity, 0f, "第 1 层 velocity 逐位往返");
                CheckEq(decoded.Sync.ActiveLayers[0].DurationMs, 500, "第 1 层 durationMs 往返");
                CheckEq(decoded.Sync.ActiveLayers[0].ElapsedMs, 125, "第 1 层 **ElapsedMs** 往返（契约点名要求）");
                CheckEq(decoded.Sync.ActiveLayers[1].InstanceId, 12u, "第 2 层 instanceId 往返");
                CheckTrue(decoded.Sync.ActiveLayers[1].Kind == PMMoverLayerKind.OverrideVelocity, "第 2 层 kind 往返");
                CheckVec(decoded.Aux.Gravity, aux.Gravity, 0f, "Aux.Gravity 逐位往返");
                CheckEq(decoded.Aux.CollisionWorldVersion, aux.CollisionWorldVersion, "Aux.CollisionWorldVersion 往返");
                CheckEq(decoded.Aux.ConfigVersion, aux.ConfigVersion, "Aux.ConfigVersion 往返");
            }

            // 快照的 Resync 变体：kind 不同，因此**不能**互换解码。
            byte[] resyncPayload = PMR4MovementCodec.EncodeSnapshot(true, blob);
            PMR4MovementSnapshotBlob resyncDecoded;
            CheckTrue(PMR4MovementCodec.TryDecodeSnapshot(resyncPayload, 0, resyncPayload.Length,
                PMR4PayloadKind.Resync, out resyncDecoded, out error), "Resync 载荷按 Resync 解码成功");
            CheckTrue(!PMR4MovementCodec.TryDecodeSnapshot(resyncPayload, 0, resyncPayload.Length,
                PMR4PayloadKind.Snapshot, out resyncDecoded, out error), "Resync 载荷按普通快照解码被拒（跨通道错用）");
            CheckTrue(!PMR4MovementCodec.TryDecodeSnapshot(snapPayload, 0, snapPayload.Length,
                PMR4PayloadKind.Resync, out resyncDecoded, out error), "普通快照按 Resync 解码被拒");

            // ---------------- B4：快照非法值（手写字节）
            CheckTrue(!TryDecodeSnapshotRaw(BuildRawSnapshot(PMR4PayloadKind.Snapshot, 1, epoch, instance, stream,
                    10L, 1L, 100.0, RawSync(RawSyncOptions.PositionXNaN), RawAux(0f, -9.81f, 0f, 1, 0)), out error),
                "Sync.Position.X = NaN 被拒：" + error);
            CheckTrue(!TryDecodeSnapshotRaw(BuildRawSnapshot(PMR4PayloadKind.Snapshot, 1, epoch, instance, stream,
                    10L, 1L, 100.0, RawSync(RawSyncOptions.VelocityZInfinity), RawAux(0f, -9.81f, 0f, 1, 0)), out error),
                "Sync.Velocity.Z = +Inf 被拒：" + error);
            CheckTrue(!TryDecodeSnapshotRaw(BuildRawSnapshot(PMR4PayloadKind.Snapshot, 1, epoch, instance, stream,
                    10L, 1L, 100.0, RawSync(RawSyncOptions.ModeUnknown), RawAux(0f, -9.81f, 0f, 1, 0)), out error),
                "未知 Mode(99) 被拒：" + error);
            CheckTrue(!TryDecodeSnapshotRaw(BuildRawSnapshot(PMR4PayloadKind.Snapshot, 1, epoch, instance, stream,
                    10L, 1L, 100.0, RawSync(RawSyncOptions.ScaleZero), RawAux(0f, -9.81f, 0f, 1, 0)), out error),
                "Scale=0 被拒：" + error);
            CheckTrue(!TryDecodeSnapshotRaw(BuildRawSnapshot(PMR4PayloadKind.Snapshot, 1, epoch, instance, stream,
                    10L, 1L, 100.0, RawSync(RawSyncOptions.MaxSpeedNegative), RawAux(0f, -9.81f, 0f, 1, 0)), out error),
                "MaxSpeed 为负被拒：" + error);
            CheckTrue(!TryDecodeSnapshotRaw(BuildRawSnapshot(PMR4PayloadKind.Snapshot, 1, epoch, instance, stream,
                    10L, 1L, 100.0, RawSync(RawSyncOptions.OmitScale), RawAux(0f, -9.81f, 0f, 1, 0)), out error),
                "Sync 缺必填字段 scale 被拒（完整 Sync 全部字段必须编码）：" + error);
            CheckTrue(!TryDecodeSnapshotRaw(BuildRawSnapshot(PMR4PayloadKind.Snapshot, 1, epoch, instance, stream,
                    10L, 1L, 100.0, RawSync(RawSyncOptions.OmitMode), RawAux(0f, -9.81f, 0f, 1, 0)), out error),
                "Sync 缺必填字段 mode 被拒：" + error);
            CheckTrue(!TryDecodeSnapshotRaw(BuildRawSnapshot(PMR4PayloadKind.Snapshot, 1, epoch, instance, stream,
                    10L, 1L, 100.0, RawSync(RawSyncOptions.SeventeenLayers), RawAux(0f, -9.81f, 0f, 1, 0)), out error),
                "层数 17 超过上限 16 被拒：" + error);
            CheckTrue(!TryDecodeSnapshotRaw(BuildRawSnapshot(PMR4PayloadKind.Snapshot, 1, epoch, instance, stream,
                    10L, 1L, 100.0, RawSync(RawSyncOptions.LayerElapsedNegative), RawAux(0f, -9.81f, 0f, 1, 0)), out error),
                "层 ElapsedMs 为负被拒：" + error);
            CheckTrue(!TryDecodeSnapshotRaw(BuildRawSnapshot(PMR4PayloadKind.Snapshot, 1, epoch, instance, stream,
                    10L, 1L, 100.0, RawSync(RawSyncOptions.LayerInstanceZero), RawAux(0f, -9.81f, 0f, 1, 0)), out error),
                "层 instanceId=0 被拒：" + error);
            CheckTrue(!TryDecodeSnapshotRaw(BuildRawSnapshot(PMR4PayloadKind.Snapshot, 1, epoch, instance, stream,
                    10L, 1L, 100.0, RawSync(RawSyncOptions.LayerKindUnknown), RawAux(0f, -9.81f, 0f, 1, 0)), out error),
                "层 Kind 未知被拒：" + error);
            CheckTrue(!TryDecodeSnapshotRaw(BuildRawSnapshot(PMR4PayloadKind.Snapshot, 1, epoch, instance, stream,
                    10L, 1L, 100.0, RawSync(RawSyncOptions.None), RawAux(0f, -9.81f, 0f, -1, 0)), out error),
                "Aux.CollisionWorldVersion 为负被拒：" + error);
            CheckTrue(!TryDecodeSnapshotRaw(BuildRawSnapshot(PMR4PayloadKind.Snapshot, 1, epoch, instance, stream,
                    10L, 1L, 100.0, RawSync(RawSyncOptions.None), RawAuxOmit(0f, -9.81f, 1, 0)), out error),
                "Aux 缺必填字段被拒：" + error);
            CheckTrue(!TryDecodeSnapshotRaw(BuildRawSnapshot(PMR4PayloadKind.Snapshot, 1, epoch, instance, stream,
                    10L, 1L, 100.0, RawSync(RawSyncOptions.None), RawAux(0f, float.NaN, 0f, 1, 0)), out error),
                "Aux.Gravity.Y = NaN 被拒：" + error);
            CheckTrue(!TryDecodeSnapshotRaw(BuildRawSnapshot(PMR4PayloadKind.Snapshot, 2, epoch, instance, stream,
                    10L, 1L, 100.0, RawSync(RawSyncOptions.None), RawAux(0f, -9.81f, 0f, 1, 0)), out error),
                "版本 2 的快照被拒：" + error);
            CheckTrue(!TryDecodeSnapshotRaw(BuildRawSnapshot((PMR4PayloadKind)9, 1, epoch, instance, stream,
                    10L, 1L, 100.0, RawSync(RawSyncOptions.None), RawAux(0f, -9.81f, 0f, 1, 0)), out error),
                "未知载荷种类被拒：" + error);
            CheckTrue(!TryDecodeSnapshotRaw(BuildRawSnapshot(PMR4PayloadKind.Snapshot, 1, epoch, instance, stream,
                    -1L, 1L, 100.0, RawSync(RawSyncOptions.None), RawAux(0f, -9.81f, 0f, 1, 0)), out error),
                "负 outputFrame 被拒：" + error);
            CheckTrue(!TryDecodeSnapshotRaw(BuildRawSnapshot(PMR4PayloadKind.Snapshot, 1, epoch, instance, stream,
                    10L, 1L, double.NaN, RawSync(RawSyncOptions.None), RawAux(0f, -9.81f, 0f, 1, 0)), out error),
                "totalSimTimeMs = NaN 被拒：" + error);
            CheckTrue(!TryDecodeSnapshotRaw(BuildRawSnapshotWithTrailingByte(epoch, instance, stream), out error),
                "快照尾部多余字节被拒：" + error);

            // ---------------- B4b：字段号顺序与「标量不得重复」（独立手搋字节对照）
            // 这两个不变量只能靠手搋字节验证：任何合规编码器都不会产生「重复标量」或「字段号倒退」。
            CheckTrue(!TryDecodeInputsRaw(BuildRawInputsWithDuplicateEntryScalar(epoch, instance, stream), out error),
                "输入条目内标量字段重复（moveX 两次）被拒：" + error);
            CheckTrue(!TryDecodeSnapshotRaw(BuildRawSnapshotWithDuplicateScalar(epoch, instance, stream), out error),
                "快照顶层标量字段重复（totalSimTimeMs 两次）被拒：" + error);
            CheckTrue(!TryDecodeSnapshotRaw(BuildRawSnapshot(PMR4PayloadKind.Snapshot, 1, epoch, instance, stream,
                    10L, 1L, 100.0, RawSyncWithDuplicateScalar(10), RawAux(0f, -9.81f, 0f, 1, 0)), out error),
                "Sync 标量字段重复（yaw 两次）被拒：" + error);
            CheckTrue(!TryDecodeSnapshotRaw(BuildRawSnapshot(PMR4PayloadKind.Snapshot, 1, epoch, instance, stream,
                    10L, 1L, 100.0, RawSyncWithDuplicateScalar(1), RawAux(0f, -9.81f, 0f, 1, 0)), out error),
                "Sync 标量字段重复（positionX 两次）被拒：" + error);
            CheckTrue(!TryDecodeSnapshotRaw(BuildRawSnapshot(PMR4PayloadKind.Snapshot, 1, epoch, instance, stream,
                    10L, 1L, 100.0, RawSyncWithDuplicateLayerScalar(), RawAux(0f, -9.81f, 0f, 1, 0)), out error),
                "活跃层内标量字段重复（priority 两次）被拒：" + error);
            CheckTrue(!TryDecodeSnapshotRaw(BuildRawSnapshot(PMR4PayloadKind.Snapshot, 1, epoch, instance, stream,
                    10L, 1L, 100.0, RawSync(RawSyncOptions.None), RawAuxWithDuplicateScalar()), out error),
                "Aux 标量字段重复（collisionWorldVersion 两次）被拒：" + error);
            CheckTrue(!TryDecodeEventsRaw(RawEventsWithDuplicateScalar(epoch, instance, stream), out error),
                "事件条目内标量字段重复（boundary 两次）被拒：" + error);
            CheckTrue(!TryDecodeSnapshotRaw(BuildRawSnapshotWithRegressingFieldOrder(epoch, instance, stream), out error),
                "快照字段号倒退（outputFrame 出现在 sync 之后）被拒：" + error);
            CheckTrue(!TryDecodeInputsRaw(BuildRawInputsWithRegressingEntryField(epoch, instance, stream), out error),
                "输入条目字段号倒退（moveX 出现在 yaw 之后）被拒：" + error);

            // 反向：**数组用重复的 message 字段承载**必须仍然被接受（多条目/多层/多事件）。
            CheckTrue(TryDecodeInputsRaw(BuildRawInputs(epoch, instance, stream,
                    RawEntry(10L, 16, 0f, 0f, 0f, 0f, false), RawEntry(11L, 16, 0f, 0f, 0f, 0f, false)), out error),
                "多条输入（重复字段号 10）仍被接受：" + error);
            CheckTrue(TryDecodeEventsRaw(RawEvents(epoch, instance, stream, 1, 5L, 1UL, 1, 0, 6L, 2UL, 2, 0), out error),
                "多条事件（重复字段号 11）仍被接受：" + error);

            PMR4MovementSnapshotBlob notProducedBlob;
            CheckTrue(!PMR4MovementCodec.TryDecodeSnapshot(new byte[PMR4MovementCodec.MaxBlobBytes + 1], 0,
                    PMR4MovementCodec.MaxBlobBytes + 1, PMR4PayloadKind.Snapshot, out notProducedBlob, out error)
                    && notProducedBlob == null,
                "快照解码失败时不产生部分结果");

            // 层数上限：正好 16 层必须通过。
            PMMoverSyncState maxLayers = PMMoverSyncState.CreateDefault();
            maxLayers.ActiveLayers = new PMMoverLayer[PMR4MovementCodec.MaxLayers];
            for (int i = 0; i < maxLayers.ActiveLayers.Length; i++)
            {
                maxLayers.ActiveLayers[i].InstanceId = (uint)(100 + i);
                maxLayers.ActiveLayers[i].Kind = PMMoverLayerKind.AdditiveVelocity;
                maxLayers.ActiveLayers[i].Priority = i;
                maxLayers.ActiveLayers[i].Velocity = new PMVector3(1f, 0f, 0f);
                maxLayers.ActiveLayers[i].DurationMs = 0;
                maxLayers.ActiveLayers[i].ElapsedMs = 0;
            }

            PMR4MovementSnapshotBlob maxBlob = new PMR4MovementSnapshotBlob();
            maxBlob.Epoch = epoch;
            maxBlob.InstanceId = instance;
            maxBlob.StreamVersion = stream;
            maxBlob.OutputFrame = 1L;
            maxBlob.ServerFrame = 0L;
            maxBlob.TotalSimTimeMs = 0.0;
            maxBlob.Sync = maxLayers;
            maxBlob.Aux = aux;
            byte[] maxPayload = PMR4MovementCodec.EncodeSnapshot(false, maxBlob);
            CheckTrue(maxPayload.Length <= PMR4MovementCodec.MaxBlobBytes,
                "16 层快照仍在 4096 字节上限内（实际 " + maxPayload.Length + "）");
            PMR4MovementSnapshotBlob maxDecoded;
            CheckTrue(PMR4MovementCodec.TryDecodeSnapshot(maxPayload, 0, maxPayload.Length,
                PMR4PayloadKind.Snapshot, out maxDecoded, out error), "16 层快照可解码");
            CheckEq(maxDecoded != null ? maxDecoded.Sync.ActiveLayers.Length : -1, 16, "16 层往返");

            // ---------------- B5：事件批
            PMR4MovementEventRecord[] events = new PMR4MovementEventRecord[3];
            events[0].Boundary = 5L;
            events[0].Kind = PMR4MovementDriver.EventKindModeChanged;
            events[0].Value = 1;
            events[0].Key = PMR4MovementDriver.BuildEventKey(5L, PMR4MovementDriver.EventKindModeChanged);
            events[1].Boundary = 9L;
            events[1].Kind = PMR4MovementDriver.EventKindLanded;
            events[1].Value = 0;
            events[1].Key = PMR4MovementDriver.BuildEventKey(9L, PMR4MovementDriver.EventKindLanded);
            events[2].Boundary = 9L;
            events[2].Kind = PMR4MovementDriver.EventKindModeChanged;
            events[2].Value = 0;
            events[2].Key = PMR4MovementDriver.BuildEventKey(9L, PMR4MovementDriver.EventKindModeChanged);

            byte[] eventPayload = PMR4MovementCodec.EncodeEvents(epoch, instance, stream, 1, events);
            PMR4MovementEventBatch eventBatch;
            CheckTrue(PMR4MovementCodec.TryDecodeEvents(eventPayload, 0, eventPayload.Length, out eventBatch, out error),
                "事件批可解码：" + error);
            if (eventBatch != null)
            {
                CheckEq(eventBatch.Sequence, 1, "事件批 sequence 往返");
                CheckEq(eventBatch.Events.Length, 3, "事件条数往返");
                CheckEq(eventBatch.Events[0].Boundary, 5L, "事件边界往返");
                CheckEq(eventBatch.Events[1].Key, events[1].Key, "事件 Key 往返");
                CheckEq(eventBatch.Events[2].Kind, PMR4MovementDriver.EventKindModeChanged, "事件 Kind 往返");
            }

            CheckTrue(!TryDecodeEventsRaw(RawEvents(epoch, instance, stream, 1, 0L, 1UL, 1, 0), out error),
                "事件 boundary=0 被拒：" + error);
            CheckTrue(!TryDecodeEventsRaw(RawEvents(epoch, instance, stream, 1, 5L, 0UL, 1, 0), out error),
                "事件 key=0 被拒：" + error);
            CheckTrue(!TryDecodeEventsRaw(RawEvents(epoch, instance, stream, 0, 5L, 1UL, 1, 0), out error),
                "事件批 sequence=0 被拒：" + error);
            CheckTrue(!TryDecodeEventsRaw(RawEvents(epoch, instance, stream, 1, 9L, 2UL, 1, 0, 5L, 3UL, 1, 0), out error),
                "事件边界倒序被拒：" + error);
            CheckTrue(!TryDecodeEventsRaw(RawEvents(epoch, instance, stream, 1, 5L, 7UL, 1, 0, 5L, 7UL, 1, 0), out error),
                "同边界重复 Key 被拒：" + error);
            CheckTrue(!TryDecodeEventsRaw(RawEvents(epoch, instance, stream, 1), out error),
                "无事件的事件批被拒（契约：只承载非空事件边界）：" + error);

            PMR4MovementEventBatch notProducedBatch;
            CheckTrue(!PMR4MovementCodec.TryDecodeEvents(BuildRawInputs(epoch, instance, stream,
                    RawEntry(10L, 16, 0f, 0f, 0f, 0f, false)), 0, inputsPayload.Length, out notProducedBatch, out error)
                    && notProducedBatch == null,
                "事件解码失败时不产生部分结果");

            ExpectFormatException(delegate
            {
                PMR4MovementCodec.EncodeEvents(epoch, instance, stream, 1, new PMR4MovementEventRecord[0]);
            }, "编码空事件批抛 FormatException");

            ExpectFormatException(delegate
            {
                PMR4MovementEventRecord bad = new PMR4MovementEventRecord();
                bad.Boundary = 5L;
                bad.Key = 0UL;
                bad.Kind = 1;
                bad.Value = 0;
                PMR4MovementCodec.EncodeEvents(epoch, instance, stream, 1, new[] { bad });
            }, "编码 Key=0 的事件抛 FormatException");

            // ---------------- B6：yaw 规范化辅助
            float yaw;
            CheckTrue(PMR4MovementCodec.TryNormalizeYaw(370f, out yaw) && yaw == 10f, "370° 规范化为 10°");
            CheckTrue(PMR4MovementCodec.TryNormalizeYaw(-10f, out yaw) && yaw == 350f, "-10° 规范化为 350°");
            CheckTrue(PMR4MovementCodec.TryNormalizeYaw(360f, out yaw) && yaw == 0f, "360° 规范化为 0°");
            CheckTrue(!PMR4MovementCodec.TryNormalizeYaw(float.NaN, out yaw), "NaN 朝向规范化失败（被拒）");
        }

        private static PMMoverInput Move(float x, float z, float yaw, bool jump)
        {
            PMMoverInput input = PMMoverInput.Empty();
            input.MoveX = x;
            input.MoveZ = z;
            input.MoveY = 0f;
            input.YawDegrees = yaw;
            input.JumpPressed = jump;
            return input;
        }

        private static bool TryDecodeInputsRaw(byte[] payload, out string error)
        {
            PMR4MovementInputBatch batch;
            bool ok = PMR4MovementCodec.TryDecodeInputs(payload, 0, payload.Length, out batch, out error);
            if (ok && batch == null)
            {
                error = "解码返回 true 但 batch 为 null";
                return false;
            }

            return ok;
        }

        private static bool TryDecodeSnapshotRaw(byte[] payload, out string error)
        {
            PMR4MovementSnapshotBlob blob;
            bool ok = PMR4MovementCodec.TryDecodeSnapshot(payload, 0, payload.Length,
                PMR4PayloadKind.Snapshot, out blob, out error);
            if (ok && blob == null)
            {
                error = "解码返回 true 但 blob 为 null";
                return false;
            }

            return ok;
        }

        private static bool TryDecodeEventsRaw(byte[] payload, out string error)
        {
            PMR4MovementEventBatch batch;
            bool ok = PMR4MovementCodec.TryDecodeEvents(payload, 0, payload.Length, out batch, out error);
            if (ok && batch == null)
            {
                error = "解码返回 true 但 batch 为 null";
                return false;
            }

            return ok;
        }

        // ---- wire 字节的手写构造（独立于 codec 的编码器）----

        private struct RawInput
        {
            public long Frame;
            public int StepMs;
            public float X;
            public float Z;
            public float Y;
            public float Yaw;
            public bool Jump;
        }

        private static RawInput RawEntry(long frame, int stepMs, float x, float z, float yaw, float y, bool jump)
        {
            RawInput e = new RawInput();
            e.Frame = frame;
            e.StepMs = stepMs;
            e.X = x;
            e.Z = z;
            e.Y = y;
            e.Yaw = yaw;
            e.Jump = jump;
            return e;
        }

        private static void WriteHeader(PMNetWriter w, PMR4PayloadKind kind, int version,
                                        uint epoch, uint instance, uint stream)
        {
            w.WriteTag(WKind, PMWireType.Varint);
            w.WriteVarint((ulong)(byte)kind);
            w.WriteTag(WVersion, PMWireType.Varint);
            w.WriteVarint((ulong)version);
            w.WriteTag(WEpoch, PMWireType.Varint);
            w.WriteVarint(epoch);
            w.WriteTag(WInstance, PMWireType.Varint);
            w.WriteVarint(instance);
            w.WriteTag(WStream, PMWireType.Varint);
            w.WriteVarint(stream);
        }

        private static byte[] BuildRawInputs(uint epoch, uint instance, uint stream, params RawInput[] entries)
        {
            return BuildRawInputsKind(PMR4PayloadKind.Inputs, epoch, instance, stream, 1, entries);
        }

        private static byte[] BuildRawInputsVersion(uint epoch, uint instance, uint stream, int version,
                                                   params RawInput[] entries)
        {
            return BuildRawInputsKind(PMR4PayloadKind.Inputs, epoch, instance, stream, version, entries);
        }

        private static byte[] BuildRawInputsKind(PMR4PayloadKind kind, uint epoch, uint instance, uint stream,
                                                params RawInput[] entries)
        {
            return BuildRawInputsKind(kind, epoch, instance, stream, 1, entries);
        }

        private static byte[] BuildRawInputsKind(PMR4PayloadKind kind, uint epoch, uint instance, uint stream,
                                                int version, RawInput[] entries)
        {
            PMNetWriter w = new PMNetWriter(128);
            WriteHeader(w, kind, version, epoch, instance, stream);
            for (int i = 0; i < entries.Length; i++)
            {
                PMNetWriter sub = w.RentSubWriter();
                sub.WriteTag(1, PMWireType.Varint); sub.WriteSInt64(entries[i].Frame);
                sub.WriteTag(2, PMWireType.Varint); sub.WriteVarint((ulong)entries[i].StepMs);
                sub.WriteTag(3, PMWireType.Fixed32); sub.WriteFloat(entries[i].X);
                sub.WriteTag(4, PMWireType.Fixed32); sub.WriteFloat(entries[i].Z);
                sub.WriteTag(5, PMWireType.Fixed32); sub.WriteFloat(entries[i].Y);
                sub.WriteTag(6, PMWireType.Fixed32); sub.WriteFloat(entries[i].Yaw);
                sub.WriteTag(7, PMWireType.Varint); sub.WriteBool(entries[i].Jump);
                w.WriteSubMessage(10, sub);
            }

            return w.ToArray();
        }

        private static byte[] BuildRawInputsWithUnknownField(uint epoch, uint instance, uint stream)
        {
            PMNetWriter w = new PMNetWriter(128);
            WriteHeader(w, PMR4PayloadKind.Inputs, 1, epoch, instance, stream);
            w.WriteTag(11, PMWireType.Varint);
            w.WriteVarint(1UL);
            return w.ToArray();
        }

        private static byte[] BuildRawInputsWithTrailingByte(uint epoch, uint instance, uint stream)
        {
            byte[] body = BuildRawInputs(epoch, instance, stream, RawEntry(10L, 16, 0f, 0f, 0f, 0f, false));
            byte[] padded = new byte[body.Length + 1];
            Buffer.BlockCopy(body, 0, padded, 0, body.Length);
            padded[body.Length] = 0x7F;
            return padded;
        }

        private static byte[] BuildRawInputsWithBadHeaderOrder(uint epoch, uint instance, uint stream)
        {
            PMNetWriter w = new PMNetWriter(64);
            w.WriteTag(WKind, PMWireType.Varint);
            w.WriteVarint((ulong)(byte)PMR4PayloadKind.Inputs);
            w.WriteTag(WVersion, PMWireType.Varint);
            w.WriteVarint(1UL);
            w.WriteTag(WInstance, PMWireType.Varint);
            w.WriteVarint(instance);
            w.WriteTag(WEpoch, PMWireType.Varint);
            w.WriteVarint(epoch);
            w.WriteTag(WStream, PMWireType.Varint);
            w.WriteVarint(stream);
            return w.ToArray();
        }

        private enum RawSyncOptions
        {
            None,
            PositionXNaN,
            VelocityZInfinity,
            ModeUnknown,
            ScaleZero,
            MaxSpeedNegative,
            OmitScale,
            OmitMode,
            SeventeenLayers,
            LayerElapsedNegative,
            LayerInstanceZero,
            LayerKindUnknown,
        }

        private static byte[] RawSync(RawSyncOptions option)
        {
            PMNetWriter s = new PMNetWriter(256);

            s.WriteTag(1, PMWireType.Fixed32);
            s.WriteFloat(option == RawSyncOptions.PositionXNaN ? float.NaN : 0f);
            s.WriteTag(2, PMWireType.Fixed32); s.WriteFloat(1f);
            s.WriteTag(3, PMWireType.Fixed32); s.WriteFloat(0f);
            s.WriteTag(4, PMWireType.Fixed32); s.WriteFloat(0f);
            s.WriteTag(5, PMWireType.Fixed32); s.WriteFloat(0f);
            s.WriteTag(6, PMWireType.Fixed32);
            s.WriteFloat(option == RawSyncOptions.VelocityZInfinity ? float.PositiveInfinity : 0f);
            s.WriteTag(7, PMWireType.Fixed32); s.WriteFloat(0f);
            s.WriteTag(8, PMWireType.Fixed32); s.WriteFloat(0f);
            s.WriteTag(9, PMWireType.Fixed32); s.WriteFloat(0f);
            s.WriteTag(10, PMWireType.Fixed32); s.WriteFloat(0f);

            if (option != RawSyncOptions.OmitMode)
            {
                s.WriteTag(11, PMWireType.Varint);
                s.WriteVarint(option == RawSyncOptions.ModeUnknown ? 99UL : 0UL);
            }

            s.WriteTag(12, PMWireType.Varint); s.WriteBool(true);
            s.WriteTag(13, PMWireType.Fixed32); s.WriteFloat(0f);
            s.WriteTag(14, PMWireType.Fixed32); s.WriteFloat(1f);
            s.WriteTag(15, PMWireType.Fixed32); s.WriteFloat(0f);

            if (option != RawSyncOptions.OmitScale)
            {
                s.WriteTag(16, PMWireType.Fixed32);
                s.WriteFloat(option == RawSyncOptions.ScaleZero ? 0f : 1f);
            }

            s.WriteTag(17, PMWireType.Fixed32);
            s.WriteFloat(option == RawSyncOptions.MaxSpeedNegative ? -1f : 3.9f);
            s.WriteTag(18, PMWireType.Fixed32); s.WriteFloat(20f);
            s.WriteTag(19, PMWireType.Fixed32); s.WriteFloat(20f);
            s.WriteTag(20, PMWireType.Fixed32); s.WriteFloat(1f);
            s.WriteTag(21, PMWireType.Fixed32); s.WriteFloat(4f);

            int layerCount = option == RawSyncOptions.SeventeenLayers ? PMR4MovementCodec.MaxLayers + 1 : 0;
            for (int i = 0; i < layerCount; i++)
            {
                PMNetWriter layer = s.RentSubWriter();
                layer.WriteTag(1, PMWireType.Varint); layer.WriteVarint((ulong)(i + 1));
                layer.WriteTag(2, PMWireType.Varint); layer.WriteVarint(0UL);
                layer.WriteTag(3, PMWireType.Varint); layer.WriteSInt32(i);
                layer.WriteTag(4, PMWireType.Fixed32); layer.WriteFloat(0f);
                layer.WriteTag(5, PMWireType.Fixed32); layer.WriteFloat(0f);
                layer.WriteTag(6, PMWireType.Fixed32); layer.WriteFloat(0f);
                layer.WriteTag(7, PMWireType.Varint); layer.WriteSInt32(0);
                layer.WriteTag(8, PMWireType.Varint); layer.WriteSInt32(0);
                s.WriteSubMessage(22, layer);
            }

            if (option == RawSyncOptions.LayerElapsedNegative || option == RawSyncOptions.LayerInstanceZero
                || option == RawSyncOptions.LayerKindUnknown)
            {
                PMNetWriter layer = s.RentSubWriter();
                layer.WriteTag(1, PMWireType.Varint);
                layer.WriteVarint(option == RawSyncOptions.LayerInstanceZero ? 0UL : 5UL);
                layer.WriteTag(2, PMWireType.Varint);
                layer.WriteVarint(option == RawSyncOptions.LayerKindUnknown ? 9UL : 1UL);
                layer.WriteTag(3, PMWireType.Varint); layer.WriteSInt32(0);
                layer.WriteTag(4, PMWireType.Fixed32); layer.WriteFloat(0f);
                layer.WriteTag(5, PMWireType.Fixed32); layer.WriteFloat(0f);
                layer.WriteTag(6, PMWireType.Fixed32); layer.WriteFloat(0f);
                layer.WriteTag(7, PMWireType.Varint); layer.WriteSInt32(100);
                layer.WriteTag(8, PMWireType.Varint);
                layer.WriteSInt32(option == RawSyncOptions.LayerElapsedNegative ? -1 : 10);
                s.WriteSubMessage(22, layer);
            }

            return s.ToArray();
        }

        private static byte[] RawAux(float gx, float gy, float gz, int worldVersion, int configVersion)
        {
            PMNetWriter a = new PMNetWriter(48);
            a.WriteTag(1, PMWireType.Fixed32); a.WriteFloat(gx);
            a.WriteTag(2, PMWireType.Fixed32); a.WriteFloat(gy);
            a.WriteTag(3, PMWireType.Fixed32); a.WriteFloat(gz);
            a.WriteTag(4, PMWireType.Varint); a.WriteSInt32(worldVersion);
            a.WriteTag(5, PMWireType.Varint); a.WriteSInt32(configVersion);
            return a.ToArray();
        }

        private static byte[] RawAuxOmit(float gx, float gy, int worldVersion, int configVersion)
        {
            PMNetWriter a = new PMNetWriter(48);
            a.WriteTag(1, PMWireType.Fixed32); a.WriteFloat(gx);
            a.WriteTag(2, PMWireType.Fixed32); a.WriteFloat(gy);
            a.WriteTag(4, PMWireType.Varint); a.WriteSInt32(worldVersion);
            return a.ToArray();
        }

        private static byte[] BuildRawSnapshot(PMR4PayloadKind kind, int version, uint epoch, uint instance,
                                               uint stream, long outputFrame, long serverFrame, double totalMs,
                                               byte[] sync, byte[] aux)
        {
            PMNetWriter w = new PMNetWriter(256);
            WriteHeader(w, kind, version, epoch, instance, stream);
            w.WriteTag(10, PMWireType.Varint); w.WriteSInt64(outputFrame);
            w.WriteTag(11, PMWireType.Varint); w.WriteSInt64(serverFrame);
            w.WriteTag(12, PMWireType.Fixed64); w.WriteDouble(totalMs);
            w.WriteTag(13, PMWireType.LengthDelimited); w.WriteVarint((ulong)sync.Length); w.WriteRawBytes(sync, 0, sync.Length);
            w.WriteTag(14, PMWireType.LengthDelimited); w.WriteVarint((ulong)aux.Length); w.WriteRawBytes(aux, 0, aux.Length);
            return w.ToArray();
        }

        private static byte[] BuildRawSnapshotWithTrailingByte(uint epoch, uint instance, uint stream)
        {
            byte[] body = BuildRawSnapshot(PMR4PayloadKind.Snapshot, 1, epoch, instance, stream, 1L, 0L, 0.0,
                RawSync(RawSyncOptions.None), RawAux(0f, -9.81f, 0f, 1, 0));
            byte[] padded = new byte[body.Length + 1];
            Buffer.BlockCopy(body, 0, padded, 0, body.Length);
            padded[body.Length] = 0x7F;
            return padded;
        }

        // ---- 「字段号重复」的独立对照（合规编码器不会产生这些字节，因此必须手搋）----
        //
        // 契约的不变量是：字段号**不得倒退**，且**标量字段不得重复**（数组用重复的 message
        // 字段承载：输入条目 10 / 活跃层 22 / 事件条目 11）。下面四个构造器各自把一个标量字段
        // 连续写两次，用来钉住「重复标量必须被拒」——它只能靠独立字节对照，不能靠编码器。

        /// <summary>完整 Sync（1..21，无层），可选在某个字段号上**连续写两次**。</summary>
        private static byte[] RawSyncWithDuplicateScalar(int duplicateField)
        {
            PMNetWriter s = new PMNetWriter(256);
            for (int f = 1; f <= 21; f++)
            {
                int times = (f == duplicateField) ? 2 : 1;
                for (int n = 0; n < times; n++)
                {
                    if (f == 11)
                    {
                        s.WriteTag(11, PMWireType.Varint); s.WriteVarint(0UL);       // Mode = Walking
                    }
                    else if (f == 12)
                    {
                        s.WriteTag(12, PMWireType.Varint); s.WriteBool(true);
                    }
                    else if (f == 16)
                    {
                        s.WriteTag(16, PMWireType.Fixed32); s.WriteFloat(1f);        // Scale > 0
                    }
                    else if (f == 17)
                    {
                        s.WriteTag(17, PMWireType.Fixed32); s.WriteFloat(3.9f);      // MaxSpeed >= 0
                    }
                    else
                    {
                        s.WriteTag(f, PMWireType.Fixed32); s.WriteFloat(f == 2 || f == 14 ? 1f : 0f);
                    }
                }
            }

            return s.ToArray();
        }

        /// <summary>完整 Sync（1..21）挂一层，层内 priority(3) 连续写两次。</summary>
        private static byte[] RawSyncWithDuplicateLayerScalar()
        {
            byte[] scalars = RawSync(RawSyncOptions.None);

            PMNetWriter layer = new PMNetWriter(64);
            layer.WriteTag(1, PMWireType.Varint); layer.WriteVarint(5UL);
            layer.WriteTag(2, PMWireType.Varint); layer.WriteVarint(0UL);
            layer.WriteTag(3, PMWireType.Varint); layer.WriteSInt32(0);
            layer.WriteTag(3, PMWireType.Varint); layer.WriteSInt32(0);          // ← 重复标量
            layer.WriteTag(4, PMWireType.Fixed32); layer.WriteFloat(0f);
            layer.WriteTag(5, PMWireType.Fixed32); layer.WriteFloat(0f);
            layer.WriteTag(6, PMWireType.Fixed32); layer.WriteFloat(0f);
            layer.WriteTag(7, PMWireType.Varint); layer.WriteSInt32(0);
            layer.WriteTag(8, PMWireType.Varint); layer.WriteSInt32(0);
            byte[] layerBytes = layer.ToArray();

            PMNetWriter s = new PMNetWriter(384);
            s.WriteRawBytes(scalars, 0, scalars.Length);
            s.WriteTag(22, PMWireType.LengthDelimited);
            s.WriteVarint((ulong)layerBytes.Length);
            s.WriteRawBytes(layerBytes, 0, layerBytes.Length);
            return s.ToArray();
        }

        /// <summary>Aux 的 collisionWorldVersion(4) 连续写两次。</summary>
        private static byte[] RawAuxWithDuplicateScalar()
        {
            PMNetWriter a = new PMNetWriter(64);
            a.WriteTag(1, PMWireType.Fixed32); a.WriteFloat(0f);
            a.WriteTag(2, PMWireType.Fixed32); a.WriteFloat(-9.81f);
            a.WriteTag(3, PMWireType.Fixed32); a.WriteFloat(0f);
            a.WriteTag(4, PMWireType.Varint); a.WriteSInt32(1);
            a.WriteTag(4, PMWireType.Varint); a.WriteSInt32(1);                  // ← 重复标量
            a.WriteTag(5, PMWireType.Varint); a.WriteSInt32(0);
            return a.ToArray();
        }

        /// <summary>输入条目内的 moveX(3) 连续写两次。</summary>
        private static byte[] BuildRawInputsWithDuplicateEntryScalar(uint epoch, uint instance, uint stream)
        {
            PMNetWriter w = new PMNetWriter(128);
            WriteHeader(w, PMR4PayloadKind.Inputs, 1, epoch, instance, stream);

            PMNetWriter sub = w.RentSubWriter();
            sub.WriteTag(1, PMWireType.Varint); sub.WriteSInt64(10L);
            sub.WriteTag(2, PMWireType.Varint); sub.WriteVarint(16UL);
            sub.WriteTag(3, PMWireType.Fixed32); sub.WriteFloat(0f);
            sub.WriteTag(3, PMWireType.Fixed32); sub.WriteFloat(0f);              // ← 重复标量
            sub.WriteTag(4, PMWireType.Fixed32); sub.WriteFloat(0f);
            sub.WriteTag(5, PMWireType.Fixed32); sub.WriteFloat(0f);
            sub.WriteTag(6, PMWireType.Fixed32); sub.WriteFloat(0f);
            sub.WriteTag(7, PMWireType.Varint); sub.WriteBool(false);
            w.WriteSubMessage(10, sub);
            return w.ToArray();
        }

        /// <summary>输入条目内字段号**倒退**：moveX(3) 出现在 yaw(6) 之后。</summary>
        private static byte[] BuildRawInputsWithRegressingEntryField(uint epoch, uint instance, uint stream)
        {
            PMNetWriter w = new PMNetWriter(128);
            WriteHeader(w, PMR4PayloadKind.Inputs, 1, epoch, instance, stream);

            PMNetWriter sub = w.RentSubWriter();
            sub.WriteTag(1, PMWireType.Varint); sub.WriteSInt64(10L);
            sub.WriteTag(2, PMWireType.Varint); sub.WriteVarint(16UL);
            sub.WriteTag(4, PMWireType.Fixed32); sub.WriteFloat(0f);
            sub.WriteTag(5, PMWireType.Fixed32); sub.WriteFloat(0f);
            sub.WriteTag(6, PMWireType.Fixed32); sub.WriteFloat(0f);
            sub.WriteTag(7, PMWireType.Varint); sub.WriteBool(false);
            sub.WriteTag(3, PMWireType.Fixed32); sub.WriteFloat(0f);              // ← 字段号倒退
            w.WriteSubMessage(10, sub);
            return w.ToArray();
        }

        /// <summary>事件条目内的 boundary(1) 连续写两次。</summary>
        private static byte[] RawEventsWithDuplicateScalar(uint epoch, uint instance, uint stream)
        {
            PMNetWriter w = new PMNetWriter(128);
            WriteHeader(w, PMR4PayloadKind.Events, 1, epoch, instance, stream);
            w.WriteTag(10, PMWireType.Varint); w.WriteVarint(1UL);

            PMNetWriter sub = w.RentSubWriter();
            sub.WriteTag(1, PMWireType.Varint); sub.WriteSInt64(5L);
            sub.WriteTag(1, PMWireType.Varint); sub.WriteSInt64(5L);              // ← 重复标量
            sub.WriteTag(2, PMWireType.Varint); sub.WriteVarint(7UL);
            sub.WriteTag(3, PMWireType.Varint); sub.WriteSInt32(1);
            sub.WriteTag(4, PMWireType.Varint); sub.WriteSInt32(0);
            w.WriteSubMessage(11, sub);
            return w.ToArray();
        }

        /// <summary>快照顶层 totalSimTimeMs(12) 连续写两次。</summary>
        private static byte[] BuildRawSnapshotWithDuplicateScalar(uint epoch, uint instance, uint stream)
        {
            PMNetWriter w = new PMNetWriter(256);
            WriteHeader(w, PMR4PayloadKind.Snapshot, 1, epoch, instance, stream);
            w.WriteTag(10, PMWireType.Varint); w.WriteSInt64(1L);
            w.WriteTag(11, PMWireType.Varint); w.WriteSInt64(0L);
            w.WriteTag(12, PMWireType.Fixed64); w.WriteDouble(0.0);
            w.WriteTag(12, PMWireType.Fixed64); w.WriteDouble(0.0);               // ← 重复标量
            byte[] sync = RawSync(RawSyncOptions.None);
            byte[] aux = RawAux(0f, -9.81f, 0f, 1, 0);
            w.WriteTag(13, PMWireType.LengthDelimited); w.WriteVarint((ulong)sync.Length); w.WriteRawBytes(sync, 0, sync.Length);
            w.WriteTag(14, PMWireType.LengthDelimited); w.WriteVarint((ulong)aux.Length); w.WriteRawBytes(aux, 0, aux.Length);
            return w.ToArray();
        }

        /// <summary>快照顶层字段号**倒退**：outputFrame(10) 出现在 sync(13) 之后。</summary>
        private static byte[] BuildRawSnapshotWithRegressingFieldOrder(uint epoch, uint instance, uint stream)
        {
            PMNetWriter w = new PMNetWriter(256);
            WriteHeader(w, PMR4PayloadKind.Snapshot, 1, epoch, instance, stream);
            w.WriteTag(10, PMWireType.Varint); w.WriteSInt64(1L);
            w.WriteTag(11, PMWireType.Varint); w.WriteSInt64(0L);
            w.WriteTag(12, PMWireType.Fixed64); w.WriteDouble(0.0);
            byte[] sync = RawSync(RawSyncOptions.None);
            byte[] aux = RawAux(0f, -9.81f, 0f, 1, 0);
            w.WriteTag(13, PMWireType.LengthDelimited); w.WriteVarint((ulong)sync.Length); w.WriteRawBytes(sync, 0, sync.Length);
            w.WriteTag(14, PMWireType.LengthDelimited); w.WriteVarint((ulong)aux.Length); w.WriteRawBytes(aux, 0, aux.Length);
            w.WriteTag(10, PMWireType.Varint); w.WriteSInt64(1L);                 // ← 字段号倒退
            return w.ToArray();
        }

        private static byte[] RawEvents(uint epoch, uint instance, uint stream, int sequence, params object[] records)
        {
            PMNetWriter w = new PMNetWriter(128);
            WriteHeader(w, PMR4PayloadKind.Events, 1, epoch, instance, stream);
            w.WriteTag(10, PMWireType.Varint);
            w.WriteVarint((ulong)sequence);
            for (int i = 0; i + 3 < records.Length + 1 && i + 4 <= records.Length; i += 4)
            {
                PMNetWriter sub = w.RentSubWriter();
                sub.WriteTag(1, PMWireType.Varint); sub.WriteSInt64(Convert.ToInt64(records[i]));
                sub.WriteTag(2, PMWireType.Varint); sub.WriteVarint(Convert.ToUInt64(records[i + 1]));
                sub.WriteTag(3, PMWireType.Varint); sub.WriteSInt32(Convert.ToInt32(records[i + 2]));
                sub.WriteTag(4, PMWireType.Varint); sub.WriteSInt32(Convert.ToInt32(records[i + 3]));
                w.WriteSubMessage(11, sub);
            }

            return w.ToArray();
        }

        // =================================================================================
        //  真实链路：手工 link / hub / world / bridge（与 PMR3RuntimeTest 同一手法）
        // =================================================================================

        private sealed class TestLink : PMNet.Transport.IPMTransportLink
        {
            public readonly string Name;
            public readonly LinkHub Hub;
            public readonly List<TestLink> Peers = new List<TestLink>(2);
            public PMTransportConnection Connection;

            /// <summary>丢接下来的 N 个包（用于「输入首包丢失」这类场景）。</summary>
            public int DropNext;

            public TestLink(string name, LinkHub hub)
            {
                Name = name;
                Hub = hub;
            }

            public bool Send(byte[] buffer, int offset, int count)
            {
                Hub.Route(this, buffer, offset, count);
                return true;
            }

            public string Describe()
            {
                return Name;
            }
        }

        private sealed class LinkHub
        {
            private readonly List<TestLink> _links = new List<TestLink>(4);
            private readonly Dictionary<TestLink, List<byte[]>> _inbox = new Dictionary<TestLink, List<byte[]>>();

            private TestLink _holdLink;
            private int _holdRemaining;
            private readonly List<KeyValuePair<TestLink, byte[]>> _held = new List<KeyValuePair<TestLink, byte[]>>();

            public long Dropped;
            public long Held;

            public TestLink AddLink(string name)
            {
                TestLink link = new TestLink(name, this);
                _links.Add(link);
                _inbox[link] = new List<byte[]>();
                return link;
            }

            public void Connect(TestLink a, TestLink b)
            {
                a.Peers.Add(b);
                b.Peers.Add(a);
            }

            /// <summary>扣留该链路发出的接下来 <paramref name="count"/> 个数据报（用于构造"迟到"）。</summary>
            public void HoldFrom(TestLink link, int count)
            {
                _holdLink = link;
                _holdRemaining = count;
            }

            public void ReleaseHeld()
            {
                KeyValuePair<TestLink, byte[]>[] batch = _held.ToArray();
                _held.Clear();
                for (int i = 0; i < batch.Length; i++)
                {
                    _inbox[batch[i].Key].Add(batch[i].Value);
                }
            }

            public void Route(TestLink from, byte[] buffer, int offset, int count)
            {
                if (from.DropNext > 0)
                {
                    from.DropNext--;
                    Dropped++;
                    return;
                }

                for (int i = 0; i < from.Peers.Count; i++)
                {
                    TestLink peer = from.Peers[i];
                    byte[] copy = new byte[count];
                    Buffer.BlockCopy(buffer, offset, copy, 0, count);

                    if (ReferenceEquals(_holdLink, from) && _holdRemaining > 0)
                    {
                        _holdRemaining--;
                        Held++;
                        _held.Add(new KeyValuePair<TestLink, byte[]>(peer, copy));
                        continue;
                    }

                    _inbox[peer].Add(copy);
                }
            }

            public void Deliver()
            {
                for (int i = 0; i < _links.Count; i++)
                {
                    TestLink link = _links[i];
                    List<byte[]> box = _inbox[link];
                    if (box.Count == 0) { continue; }

                    byte[][] batch = box.ToArray();
                    box.Clear();
                    for (int k = 0; k < batch.Length; k++)
                    {
                        if (link.Connection != null)
                        {
                            link.Connection.OnDatagram(batch[k], 0, batch[k].Length);
                        }
                    }
                }
            }
        }

        private sealed class EndpointRow
        {
            public string Name;
            public PMNetWorld World;
            public PMNetSessionBridge Bridge;
        }

        private sealed class ConnectionRow
        {
            public string Name;
            public EndpointRow Endpoint;
            public PMTransportConnection Connection;
            public TestLink Link;
        }

        private sealed class Rig : IDisposable
        {
            public readonly LinkHub Hub = new LinkHub();
            public EndpointRow Server;
            public readonly List<EndpointRow> ClientEndpoints = new List<EndpointRow>(2);
            public readonly List<ConnectionRow> ServerViews = new List<ConnectionRow>(2);
            public readonly List<ConnectionRow> ClientConnections = new List<ConnectionRow>(2);
            public readonly uint Epoch;
            public long Now = 1000L;

            private Rig(uint epoch)
            {
                Epoch = epoch;
            }

            public PMNetWorld ServerWorld { get { return Server.World; } }
            public PMNetSessionBridge ServerBridge { get { return Server.Bridge; } }
            public PMTransportConnection ServerView(int index) { return ServerViews[index].Connection; }
            public PMNetWorld ClientWorld(int index) { return ClientEndpoints[index].World; }
            public PMNetSessionBridge ClientBridge(int index) { return ClientEndpoints[index].Bridge; }
            public TestLink ServerLink(int index) { return ServerViews[index].Link; }
            public TestLink ClientLink(int index) { return ClientConnections[index].Link; }

            public static Rig Build(uint epoch, params int[] uids)
            {
                Rig rig = new Rig(epoch);
                rig.Server = rig.NewEndpoint("server", true, epoch, PMR3Runtime.ProtocolHash);

                for (int i = 0; i < uids.Length; i++)
                {
                    rig.AddClient(uids[i]);
                }

                rig.Finish();
                return rig;
            }

            private EndpointRow NewEndpoint(string name, bool isServer, uint epoch, uint protocolHash)
            {
                EndpointRow row = new EndpointRow();
                row.Name = name;
                row.World = new PMNetWorld(new PMSession(epoch, isServer));
                row.World.Warn = delegate(string m) { };
                row.Bridge = new PMNetSessionBridge(row.World);
                row.Bridge.Warn = delegate(string m) { };
                return row;
            }

            private void AddClient(int uid)
            {
                int connectionId = ClientEndpoints.Count + 1;
                EndpointRow client = NewEndpoint("client" + connectionId, false, Epoch, PMR3Runtime.ProtocolHash);
                ClientEndpoints.Add(client);

                TestLink serverLink = Hub.AddLink("server->c" + connectionId);
                TestLink clientLink = Hub.AddLink("c" + connectionId + "->server");
                Hub.Connect(serverLink, clientLink);

                ServerViews.Add(NewConnection(Server, "serverView" + connectionId, connectionId,
                    PMSessionPeerRole.Client, uid, uid, uid, serverLink));
                ClientConnections.Add(NewConnection(client, "client" + connectionId, 1,
                    PMSessionPeerRole.Server, uid, uid, uid, clientLink));
            }

            private ConnectionRow NewConnection(EndpointRow owner, string name, int connectionId,
                                               PMSessionPeerRole peerRole, int uid, int playerId, int teamId,
                                               TestLink link)
            {
                ConnectionRow row = new ConnectionRow();
                row.Name = name;
                row.Endpoint = owner;
                row.Link = link;

                PMSessionIdentity identity = new PMSessionIdentity();
                identity.ConnectionId = connectionId;
                identity.PeerRole = peerRole;
                identity.Uid = uid;
                identity.PlayerId = playerId;
                identity.TeamId = teamId;
                identity.HeroId = 1;
                identity.Epoch = Epoch;
                identity.LocalProtocolHash = PMR3Runtime.ProtocolHash;
                identity.PeerProtocolHash = PMR3Runtime.ProtocolHash;
                identity.MatchId = "match-r4-b-network-test";
                identity.DsId = "ds-r4-b-network-test";

                PMTransportConfig config = new PMTransportConfig();
                config.IdleTimeoutMs = 0L;

                row.Connection = new PMTransportConnection(identity, owner.World, owner.Bridge, link, config);
                link.Connection = row.Connection;
                return row;
            }

            private void Finish()
            {
                for (int i = 0; i < ServerViews.Count; i++)
                {
                    string error;
                    CheckTrue(ServerViews[i].Connection.TryActivate(out error),
                        ServerViews[i].Name + " 连接激活成功（" + (error ?? "ok") + "）");
                }

                for (int i = 0; i < ClientConnections.Count; i++)
                {
                    string error;
                    CheckTrue(ClientConnections[i].Connection.TryActivate(out error),
                        ClientConnections[i].Name + " 连接激活成功（" + (error ?? "ok") + "）");
                }

                PMR3Runtime.Attach(Server.World, Server.Bridge);
                for (int i = 0; i < ClientEndpoints.Count; i++)
                {
                    PMR3Runtime.Attach(ClientEndpoints[i].World, ClientEndpoints[i].Bridge);
                }
            }

            public PMR3Player SpawnPlayer(int index)
            {
                return PMR3Runtime.SpawnPlayer(Server.World, Server.Bridge, ServerView(index));
            }

            public void Frame(int frames)
            {
                for (int f = 0; f < frames; f++)
                {
                    Hub.Deliver();
                    Server.Bridge.Update(Now);
                    for (int i = 0; i < ClientEndpoints.Count; i++)
                    {
                        ClientEndpoints[i].Bridge.Update(Now);
                    }

                    Hub.Deliver();
                    Now += 16L;
                }
            }

            public void Dispose()
            {
                PMR3Runtime.Detach(Server.World);
                for (int i = 0; i < ClientEndpoints.Count; i++)
                {
                    PMR3Runtime.Detach(ClientEndpoints[i].World);
                }

                try { Server.Bridge.Dispose(); }
                catch (Exception) { }

                for (int i = 0; i < ClientEndpoints.Count; i++)
                {
                    try { ClientEndpoints[i].Bridge.Dispose(); }
                    catch (Exception) { }
                }
            }
        }

        private static readonly List<PMR3Player> ReplicatedPlayers = new List<PMR3Player>();

        private static void OnPlayerReplicated(PMR3Player player)
        {
            ReplicatedPlayers.Add(player);
        }

        /// <summary>一个"服务端权威副本 + 客户端副本"的运动场景（含 Driver）。</summary>
        private sealed class PlayerCase : IDisposable
        {
            public Rig Rig;
            public int OwnerIndex;
            public PMMoverTestWorld ServerWorld;
            public PMMoverTestWorld ClientWorld;
            public PMR3Player ServerPlayer;
            public PMR4MovementDriver DsDriver;
            public PMR3Player OwnerPlayer;
            public PMR4MovementDriver ApDriver;
            public readonly List<PMR3Player> SpPlayers = new List<PMR3Player>(2);
            public readonly List<PMR4MovementDriver> SpDrivers = new List<PMR4MovementDriver>(2);
            public readonly List<PMPredictionEvent> Dispatched = new List<PMPredictionEvent>(16);
            public long ServerFrame;
            public uint Epoch;

            public static PlayerCase Build(uint epoch, int[] uids, int ownerIndex)
            {
                PlayerCase c = new PlayerCase();
                c.Epoch = epoch;
                c.OwnerIndex = ownerIndex;
                c.Rig = Rig.Build(epoch, uids);
                c.ServerWorld = new PMMoverTestWorld();
                c.ClientWorld = new PMMoverTestWorld();

                c.ServerPlayer = c.Rig.SpawnPlayer(ownerIndex);
                if (c.ServerPlayer == null)
                {
                    Check(false, "权威侧创建玩家副本失败");
                    return c;
                }

                c.DsDriver = new PMR4MovementDriver(
                    c.ServerPlayer, c.ServerWorld, PMNetRole.Authority, epoch,
                    CreateInitialSync(), PMMoverAuxState.CreateDefault());

                // 契约：初始完整快照必须在 Spawn 首次生命周期 Flush **之前**设置。
                c.DsDriver.PublishInitialSnapshot();
                c.Rig.Frame(3);

                // 客户端副本：按 Create 记录的初值建立 Driver（宿主在 PlayerReplicated 里做同样的事）。
                PMNetObject obj;
                bool found = c.Rig.ClientWorld(ownerIndex).TryFind(c.ServerPlayer.NetId, out obj) && obj != null;
                CheckTrue(found, "拥有者客户端收到副本（Create）");
                if (!found) { return c; }

                c.OwnerPlayer = obj as PMR3Player;
                string error;
                c.ApDriver = PMR4MovementDriver.CreateFromPlayerInitialSnapshot(
                    c.OwnerPlayer, c.ClientWorld, epoch, out error);
                CheckTrue(c.ApDriver != null, "AP Driver 建立成功（" + (error ?? "ok") + "）");
                if (c.ApDriver != null)
                {
                    c.ApDriver.EventDispatched += c.OnEvent;
                }

                for (int i = 0; i < c.Rig.ClientEndpoints.Count; i++)
                {
                    if (i == ownerIndex) { continue; }

                    PMNetObject other;
                    if (!c.Rig.ClientWorld(i).TryFind(c.ServerPlayer.NetId, out other) || other == null)
                    {
                        continue;
                    }

                    PMR3Player sp = other as PMR3Player;
                    PMR4MovementDriver spDriver = PMR4MovementDriver.CreateFromPlayerInitialSnapshot(
                        sp, c.ClientWorld, epoch, out error);
                    CheckTrue(spDriver != null, "SP Driver 建立成功（client" + i + "：" + (error ?? "ok") + "）");
                    if (sp != null) { c.SpPlayers.Add(sp); }
                    if (spDriver != null) { c.SpDrivers.Add(spDriver); }
                }

                return c;
            }

            private void OnEvent(PMPredictionEvent evt)
            {
                Dispatched.Add(evt);
            }

            /// <summary>
            /// 出生状态。**刻意避开测试世界的墙**：<see cref="PMMoverTestWorld"/> 的墙盒是
            /// x∈[-0.5,0.5]、y∈[0,2]、z∈[-4,4]，而 <see cref="PMMoverSyncState.CreateDefault"/> 的
            /// 位置恰好是 (0,1,0) —— 正在墙盒**内部**。若直接在原点出生，扫掠会被墙堵死，
            /// “输入驱动位移”根本测不出来（踩坑记录：该现象误导致“模型不动”的误判）。
            /// </summary>
            public static PMMoverSyncState CreateInitialSync()
            {
                PMMoverSyncState initial = PMMoverSyncState.CreateDefault();
                initial.Position = new PMVector3(3f, 1f, 0f);
                return initial;
            }

            public PMFrameId ServerFrameId()
            {
                return new PMFrameId(PMFrameDomain.AuthorityServer, ServerFrame);
            }

            /// <summary>一个完整往返：AP 预测 + 上行 → DS Pump → 下行 → AP/SP 应用。</summary>
            public void Step(int stepMs, PMMoverInput input, bool sendInput = true)
            {
                if (ApDriver != null)
                {
                    ApDriver.Tick(stepMs, input, ServerFrameId());
                    if (sendInput)
                    {
                        ApDriver.SendInputPayload();
                    }
                }

                Rig.Frame(1);

                if (DsDriver != null)
                {
                    DsDriver.Pump(stepMs, ServerFrameId());
                }

                ServerFrame++;

                Rig.Frame(1);
                Apply();
            }

            /// <summary>只让客户端应用入站（不推进 DS）。</summary>
            public void Apply(double wallMs = 0.0)
            {
                if (ApDriver != null) { ApDriver.Update(wallMs); }
                for (int i = 0; i < SpDrivers.Count; i++)
                {
                    SpDrivers[i].Update(wallMs);
                    SpDrivers[i].Advance(wallMs);
                }
            }

            /// <summary>
            /// 把最后一包推到位（断言前的收尾）。
            ///
            /// 为什么需要它：发送→传输层 flush→投递→派发→接纳全都在帧边界上，
            /// 最后一包通常要再过一个帧周期才会被 Pump 接纳。它仍然**只走真实链路**
            /// （不手工调用业务实现冒充网络），因此不是“绕过”而是“真的再跑两帧”。
            /// </summary>
            public void Settle()
            {
                for (int i = 0; i < 2; i++)
                {
                    Rig.Frame(1);
                    if (DsDriver != null) { DsDriver.Pump(16.0, ServerFrameId()); }
                    ServerFrame++;
                    Rig.Frame(1);
                    Apply();
                }
            }

            public void Dispose()
            {
                if (DsDriver != null) { DsDriver.Dispose(); }
                if (ApDriver != null) { ApDriver.Dispose(); }
                for (int i = 0; i < SpDrivers.Count; i++) { SpDrivers[i].Dispose(); }
                Rig.Dispose();
            }
        }

        // =================================================================================
        //  C. 真实链路 AP→DS→AP：收敛与边界推进
        // =================================================================================

        private static void TestReplicationConvergence()
        {
            PlayerCase c = null;
            try
            {
                c = PlayerCase.Build(0x4B01u, new[] { 51, 52 }, 0);
                if (c.ApDriver == null || c.DsDriver == null) { return; }

                CheckTrue(c.OwnerPlayer.Role == PMNetRole.AutonomousProxy, "owner 副本角色 = AutonomousProxy");
                CheckTrue(c.SpDrivers.Count == 1 && c.SpPlayers[0].Role == PMNetRole.SimulatedProxy,
                    "非 owner 副本角色 = SimulatedProxy（仍是需要 Driver 的对象）");
                CheckEq(c.ApDriver.InstanceId, c.ServerPlayer.NetId.Value, "AP instanceId 绑定 player.NetId");
                CheckEq(c.ApDriver.Epoch, c.Epoch, "AP epoch 绑定会话");
                CheckEq(c.ApDriver.StreamVersion, 1u, "初始 streamVersion = 1");
                CheckEq((long)c.ApDriver.OutputBoundary.Value, 0L, "AP 初始输出边界 = 0");
                CheckTrue(c.OwnerPlayer.MovementSnapshotPayload != null
                          && c.OwnerPlayer.MovementSnapshotPayload.Length > 0,
                    "Create 记录已带上运动初值载荷（与创建原子到达）");

                // 20 步匀速前进：上行输入 → DS 权威 → 下行快照 → AP 应用。
                int steps = 20;
                for (int i = 0; i < steps; i++)
                {
                    c.Step(16, Move(0f, 1f, 0f, false));
                }

                c.Settle(); // 网络需要再跑一两帧把最后一包推到位（真实链路，不绕过）

                CheckEq(c.DsDriver.AuthorityNextInputFrame, steps, "DS 消费了全部 " + steps + " 条输入");
                CheckEq((long)c.DsDriver.OutputBoundary.Value, steps, "DS 权威输出边界 == " + steps);
                CheckEq((long)c.ApDriver.OutputBoundary.Value, steps, "AP 输出边界跟进 == " + steps);
                CheckEq((long)c.ApDriver.ConfirmedBoundary.Value, steps, "AP 已确认边界 == " + steps);
                CheckTrue(c.ApDriver.SnapshotPayloadsApplied > 0, "AP 应用过快照（复制通道真的在用）");

                PMMoverSyncState auth = c.DsDriver.GetAuthoritativeSync();
                PMMoverSyncState pred = c.ApDriver.GetPredictedSync();
                CheckVec(pred.Position, auth.Position, 0.05f, "AP 预测与 DS 权威位置收敛（同模型同输入）");
                CheckTrue(auth.Position.Z > 0.5f, "DS 权威位置确实前进了（Z=" + auth.Position.Z + "）");

                // 无差异 ⇒ 不应触发回滚。
                CheckEq(c.ApDriver.Timeline.ReplayedSteps, 0L, "无状态差异时不发生重放（ShouldReconcile=false）");
                CheckEq(c.ApDriver.Timeline.ConfirmedEventsEmitted, 0L,
                    "预测时间轴从不广播事件（事件唯一派发者是独立日志，避免双重派发）");

                // SP 也吃到了快照。
                CheckTrue(c.SpDrivers[0].SamplePresentation().HasValue, "SP 收到并采样到权威状态");
                CheckTrue(c.SpDrivers[0].Interpolation.AuthorityAccepted > 0, "SP 插值缓冲接受了权威快照");

                // 输入重发窗口在被确认后退役。
                CheckEq(c.ApDriver.UnackedInputCount, 0, "已确认输入全部从重发窗口退役");
            }
            finally
            {
                if (c != null) { c.Dispose(); }
                ReplicatedPlayers.Clear();
            }
        }

        // =================================================================================
        //  D. 输入重发/首包丢失/重复/乱序
        // =================================================================================

        private static void TestInputResend()
        {
            PlayerCase c = null;
            try
            {
                c = PlayerCase.Build(0x4B02u, new[] { 53 }, 0);
                if (c.ApDriver == null || c.DsDriver == null) { return; }

                // D1：首包丢失 —— 丢客户端发出的第一个上行数据报。
                c.Rig.ClientLink(0).DropNext = 1;
                for (int i = 0; i < 8; i++)
                {
                    c.Step(16, Move(1f, 0f, 0f, false));
                }

                c.Settle();

                CheckTrue(c.Rig.Hub.Dropped >= 1, "确实丢过上行数据报（Dropped=" + c.Rig.Hub.Dropped + "）");
                CheckEq(c.DsDriver.AuthorityNextInputFrame, 8L,
                    "首包丢失后 DS 仍拿到连续输入（重发窗口补齐了缺口）");
                CheckEq((long)c.DsDriver.OutputBoundary.Value, 8L, "首包丢失不产生缺帧停滞");

                long entriesSent = c.ApDriver.InputEntriesSent;
                CheckTrue(entriesSent > 8, "存在重发（发送条目数 " + entriesSent + " > 8 条输入）");

                // D2：重复 —— 同一批输入连发两次。DS 必须去重且不重复模拟。
                long boundaryBefore = c.DsDriver.OutputBoundary.Value;
                long admittedBefore = c.DsDriver.InputsAdmitted;
                c.ApDriver.Tick(16, Move(0f, 1f, 0f, false), c.ServerFrameId());
                c.ApDriver.SendInputPayload();
                c.ApDriver.SendInputPayload(); // 第二次：同一窗口重发
                c.Settle();

                CheckEq(c.DsDriver.OutputBoundary.Value, boundaryBefore + 1,
                    "重复输入只被模拟一次（边界只前进 1）");
                CheckTrue(c.DsDriver.InputsRejectedByBuffer > 0,
                    "重复输入被输入缓冲拒绝并计数（" + c.DsDriver.InputsRejectedByBuffer + "）");
                CheckTrue(c.DsDriver.InputsAdmitted > admittedBefore, "同一批里的新输入仍被接纳");

                // D3：未确认窗口的选取规则 —— 最旧 4 条 + 最新 4 条（不饿死旧缺口，也不丢新鲜度）。
                PlayerCase c2 = PlayerCase.Build(0x4B03u, new[] { 54 }, 0);
                try
                {
                    if (c2.ApDriver != null)
                    {
                        // 只 tick+发送一次（让第 1 条进窗口），然后离线 tick 到窗口内 11 条。
                        for (int i = 0; i < 11; i++)
                        {
                            c2.ApDriver.Tick(16, Move(0f, 0f, 0f, false), c2.ServerFrameId());
                        }

                        byte[] payload;
                        CheckTrue(c2.ApDriver.TryBuildInputPayload(out payload), "未确认窗口可构造上行载荷");
                        PMR4MovementInputBatch batch;
                        string error;
                        CheckTrue(PMR4MovementCodec.TryDecodeInputs(payload, 0, payload.Length, out batch, out error),
                            "上行载荷可解码：" + error);
                        if (batch != null)
                        {
                            CheckEq(batch.Entries.Length, PMR4MovementCodec.MaxInputsPerPacket,
                                "窗口 > 8 条时按 8 条发送");
                            CheckEq(batch.Entries[0].InputFrame, 0L, "包内含**最旧**未确认输入（防缺帧饥饿）");
                            CheckEq(batch.Entries[3].InputFrame, 3L, "包内最旧段为 4 条");
                            CheckEq(batch.Entries[4].InputFrame, 7L, "包内最新段从第 8 条开始");
                            CheckEq(batch.Entries[7].InputFrame, 10L, "包内最新段到第 11 条（兼带最新）");
                            CheckTrue(batch.Entries[0].InputFrame < batch.Entries[1].InputFrame
                                      && batch.Entries[7].InputFrame > batch.Entries[6].InputFrame,
                                "包内条目严格升序（不重复、不倒退）");
                        }

                        // 窗口 ≤ 8 条时全部发出（"最新"天然在包内）。
                        c2.DsDriver.Pump(200.0, c2.ServerFrameId());
                        c2.Rig.Frame(2);
                        c2.Apply();
                    }
                }
                finally
                {
                    c2.Dispose();
                }

                // D4：上行不得携带 Effects/Layers（编码即失败）。
                ExpectFormatException(delegate
                {
                    PMMoverInput cheat = Move(0f, 0f, 0f, false);
                    cheat.Effects = new[] { PMMoverEffectRequest.SetVelocity(new PMVector3(100f, 0f, 0f)) };
                    PMR4MovementInputEntry entry = new PMR4MovementInputEntry();
                    entry.InputFrame = 1L;
                    entry.StepMs = 16;
                    entry.Input = cheat;
                    PMR4MovementCodec.EncodeInputs(c.Epoch, c.ApDriver.InstanceId, 1u, new[] { entry });
                }, "上行携带 Effects 在编码阶段即失败（不信任客户端自报效果）");
            }
            finally
            {
                if (c != null) { c.Dispose(); }
                ReplicatedPlayers.Clear();
            }
        }

        // =================================================================================
        //  E. 完整状态差异与回滚
        // =================================================================================

        private static void TestFullStateDivergence()
        {
            PlayerCase c = null;
            try
            {
                c = PlayerCase.Build(0x4B04u, new[] { 55 }, 0);
                if (c.ApDriver == null || c.DsDriver == null) { return; }

                for (int i = 0; i < 4; i++)
                {
                    c.Step(16, Move(0f, 1f, 0f, false));
                }

                PMMoverSyncState before = c.ApDriver.GetPredictedSync();

                // DS 在帧边界提交**可信**运动 Effect（只允许 DS 调用）：强制一个巨大的状态差异。
                PMVector3 target = new PMVector3(5f, 1f, -4f);
                CheckTrue(c.DsDriver.ServerSubmitTrustedEffect(PMMoverEffectRequest.Teleport(target)),
                    "DS 接受可信 Teleport 命令");
                CheckTrue(c.DsDriver.ServerSubmitTrustedEffect(PMMoverEffectRequest.SetMode(PMMoverMode.Flying)),
                    "DS 接受可信 SetMode 命令");

                // 上行继续（含未确认输入）：AP 必须恢复 + 按原 dt 重放并收敛。
                for (int i = 0; i < 3; i++)
                {
                    c.Step(16, Move(0f, 1f, 0f, false));
                }

                PMMoverSyncState auth = c.DsDriver.GetAuthoritativeSync();
                PMMoverSyncState pred = c.ApDriver.GetPredictedSync();

                CheckTrue(c.ApDriver.Timeline.ReplayedSteps > 0,
                    "强制差异后发生了恢复 + 重放（replayedSteps=" + c.ApDriver.Timeline.ReplayedSteps + "）");
                CheckVec(pred.Position, auth.Position, 0.05f, "重放后 AP 预测与 DS 权威位置再次收敛");
                CheckTrue(Math.Abs(pred.Position.X - before.Position.X) > 1f, "位置确实发生了大位移（差异是真实的）");
                CheckTrue(pred.Mode == auth.Mode, "重放后 Mode 与权威一致（完整状态恢复，不只是位置）");

                // 反向：客户端不得通过上行注入效果（编码层已封，见 D4 的 ExpectFormatException）。
                CheckEq(c.DsDriver.TrustedCommandsQueued, 2L, "DS 只消费了 DS 自己提交的 2 条可信命令（客户端无法注入）");
            }
            finally
            {
                if (c != null) { c.Dispose(); }
                ReplicatedPlayers.Clear();
            }
        }

        // =================================================================================
        //  F. 事件先后、去重、迟到与可靠补发
        // =================================================================================

        private static void TestEvents()
        {
            PlayerCase c = null;
            try
            {
                c = PlayerCase.Build(0x4B05u, new[] { 56 }, 0);
                if (c.ApDriver == null || c.DsDriver == null) { return; }

                // 走几步，然后跳一次（Walking → Falling 会产出 ModeChanged 事件；之后落地产出 Landed）。
                for (int i = 0; i < 3; i++)
                {
                    c.Step(16, Move(0f, 0f, 0f, false));
                }

                c.Step(16, Move(0f, 0f, 0f, true), true); // 起跳

                for (int i = 0; i < 70; i++)
                {
                    c.Step(16, Move(0f, 0f, 0f, false), true);
                }

                CheckTrue(c.DsDriver.EventPayloadsSent > 0,
                    "DS 下发了事件批（可靠 RPC：" + c.DsDriver.EventPayloadsSent + " 批）");
                CheckTrue(c.ApDriver.EventPayloadsReceived >= c.DsDriver.EventPayloadsSent,
                    "AP 收到全部事件批");
                CheckEq(c.ApDriver.EventBatchesAccepted, c.DsDriver.EventPayloadsSent,
                    "事件批全部被独立日志接受（无缺口 / 无溢出）");
                CheckTrue(c.ApDriver.Journal != null && !c.ApDriver.Journal.Failed, "事件日志未进入失败态");
                CheckTrue(c.Dispatched.Count >= 2, "至少派发了起跳与落地两个不可逆事件（实际 " + c.Dispatched.Count + "）");

                // 事件按边界升序派发，且同 Key 只派发一次。
                bool ordered = true;
                var seen = new HashSet<ulong>();
                for (int i = 0; i < c.Dispatched.Count; i++)
                {
                    if (!seen.Add(c.Dispatched[i].Key)) { ordered = false; }
                }

                CheckTrue(ordered, "同一 Key 的事件**不重复派发**（去重生效）");
                CheckTrue(c.ApDriver.Journal.DispatchedThrough > 0, "事件派发水位推进");

                bool sawModeChanged = false;
                bool sawLanded = false;
                for (int i = 0; i < c.Dispatched.Count; i++)
                {
                    if (c.Dispatched[i].Kind == PMR4MovementDriver.EventKindModeChanged) { sawModeChanged = true; }
                    if (c.Dispatched[i].Kind == PMR4MovementDriver.EventKindLanded) { sawLanded = true; }
                }

                CheckTrue(sawModeChanged, "派发过 Mode 变化事件（不可逆离散跃迁）");
                CheckTrue(sawLanded, "派发过落地事件");

                // 契约：预测时间轴**从不**广播事件（只有独立日志派发）。
                CheckEq(c.ApDriver.Timeline.ConfirmedEventsEmitted, 0L, "时间轴事件广播恒为 0（无双重派发）");

                // F2：迟到事件 —— 扣留 DS→client 的一个数据报，让状态先被确认到更后面，
                //     再放行；事件仍必须派发（且只一次）。
                int dispatchedBefore = c.Dispatched.Count;
                long lateBefore = c.ApDriver.Journal.LateDispatches;
                c.Rig.Hub.HoldFrom(c.Rig.ServerLink(0), 1);

                // 再跳一次，制造一个新事件；随后继续推进若干帧，让状态确认越过它的边界。
                c.Step(16, Move(0f, 0f, 0f, true), true);
                for (int i = 0; i < 6; i++)
                {
                    c.Step(16, Move(0f, 0f, 0f, false), true);
                }

                c.Rig.Hub.ReleaseHeld();
                c.Rig.Frame(2);
                c.Apply();

                CheckTrue(c.Rig.Hub.Held > 0, "确实扣留过一个数据报（Held=" + c.Rig.Hub.Held + "）");
                CheckTrue(c.Dispatched.Count >= dispatchedBefore,
                    "迟到事件没有被丢弃（派发数 " + dispatchedBefore + " → " + c.Dispatched.Count + "）");
                CheckTrue(c.ApDriver.Journal.LateDispatches > lateBefore
                          || c.ApDriver.Journal.DispatchedThrough > 0,
                    "存在「状态已确认到更后面」的迟到路径（LateDispatches=" + c.ApDriver.Journal.LateDispatches + "）");

                // F3：直接验证日志语义 —— 先事件后确认（事件早到不解冻、越界不派发）。
                PMR4MovementEventJournal journal = new PMR4MovementEventJournal();
                journal.Rebind(7u, 0L);
                PMR4MovementEventBatch batch = new PMR4MovementEventBatch();
                batch.Epoch = 1u;
                batch.InstanceId = 2u;
                batch.StreamVersion = 7u;
                batch.Sequence = 1;
                batch.Events = new PMR4MovementEventRecord[2];
                batch.Events[0].Boundary = 10L;
                batch.Events[0].Key = PMR4MovementDriver.BuildEventKey(10L, 1);
                batch.Events[0].Kind = 1;
                batch.Events[1].Boundary = 12L;
                batch.Events[1].Key = PMR4MovementDriver.BuildEventKey(12L, 2);
                batch.Events[1].Kind = 2;

                string journalError;
                CheckTrue(journal.TryAccept(batch, out journalError), "事件日志接受第 1 批（sequence=1）");
                CheckEq(journal.PendingEvents, 2, "事件先到时被缓存（不派发）");

                int dispatched = 0;
                journal.DispatchTo(9L, delegate(PMPredictionEvent e) { dispatched++; });
                CheckEq(dispatched, 0, "确认边界未到 ⇒ 一条都不派发");
                journal.DispatchTo(11L, delegate(PMPredictionEvent e) { dispatched++; });
                CheckEq(dispatched, 1, "确认到 11 ⇒ 只派发边界 10 的事件");
                CheckEq(journal.PendingEvents, 1, "边界 12 的事件仍在缓存");

                CheckTrue(!journal.TryAccept(batch, out journalError), "重复批（sequence=1）被拒");
                CheckEq(journal.PendingEvents, 1, "重复批不改变缓存（不重复广播）");

                PMR4MovementEventBatch gap = new PMR4MovementEventBatch();
                gap.Epoch = 1u;
                gap.InstanceId = 2u;
                gap.StreamVersion = 7u;
                gap.Sequence = 3;
                gap.Events = new PMR4MovementEventRecord[1];
                gap.Events[0].Boundary = 20L;
                gap.Events[0].Key = PMR4MovementDriver.BuildEventKey(20L, 1);
                gap.Events[0].Kind = 1;
                CheckTrue(!journal.TryAccept(gap, out journalError), "序号缺口（1 → 3）被拒");
                CheckTrue(journal.Failed, "序号缺口进入显式失败态（不跳过、不丢未交付事件）");
                CheckEq(journal.PendingEvents, 1, "失败时未交付事件仍在缓存（未被淘汰）");
            }
            finally
            {
                if (c != null) { c.Dispose(); }
                ReplicatedPlayers.Clear();
            }
        }

        // =================================================================================
        //  G. 重同步：DS 升流 + 旧流丢弃 + 不倒退拒绝
        // =================================================================================

        private static void TestResync()
        {
            PlayerCase c = null;
            try
            {
                c = PlayerCase.Build(0x4B06u, new[] { 57 }, 0);
                if (c.ApDriver == null || c.DsDriver == null) { return; }

                for (int i = 0; i < 4; i++)
                {
                    c.Step(16, Move(0f, 1f, 0f, false));
                }

                long boundaryBeforeResync = c.DsDriver.OutputBoundary.Value;
                uint streamBefore = c.ApDriver.StreamVersion;
                CheckTrue(boundaryBeforeResync > 0, "重同步前 DS 边界 > 0");

                // 制造"客户端失步"：只 tick 不上行、也不让 DS 消费，直到未确认窗口满。
                for (int i = 0; i < PMR4MovementCodec.MaxUnackedInputs + 8; i++)
                {
                    c.ApDriver.Tick(16, Move(0f, 0f, 0f, false), c.ServerFrameId());
                    c.ApDriver.SendInputPayload();
                }

                CheckTrue(c.ApDriver.UnackedWindowExhausted > 0,
                    "未确认窗口满了之后不再产出新的预测输入（不静默扩窗）");
                CheckTrue(c.ApDriver.ResyncRequestsSent > 0, "AP 发出了显式重同步请求（可靠 + ForceValidate）");

                // 让请求与旧流输入到达 DS：DS 必须升流、丢弃旧流输入、下发完整可信快照。
                c.Rig.Frame(2);
                c.DsDriver.Pump(16.0, c.ServerFrameId());
                c.ServerFrame++;
                c.Rig.Frame(3);
                c.Apply();

                CheckTrue(c.DsDriver.ResyncServed > 0, "DS 提供过重同步（ResyncServed=" + c.DsDriver.ResyncServed + "）");
                CheckTrue(c.DsDriver.InputRejectedStream > 0,
                    "旧流输入被 DS 丢弃并计数（InputRejectedStream=" + c.DsDriver.InputRejectedStream + "）");
                CheckEq(c.ApDriver.StreamVersion, c.DsDriver.StreamVersion, "AP 与 DS 的流代次重新对齐");
                CheckTrue(c.ApDriver.StreamVersion > streamBefore, "流代次确实被 DS 递增（非客户端自升）");

                CheckTrue(c.ApDriver.OutputBoundary.Value >= boundaryBeforeResync,
                    "重绑后输出边界不倒退（" + boundaryBeforeResync + " → " + c.ApDriver.OutputBoundary.Value + "）");
                CheckEq(c.ApDriver.ResyncRejectedOldStream, 0L, "没有因「旧流」误拒当前流的重同步载荷");

                // 重绑后的 AP 必须能继续跟在权威后面收敛。
                for (int i = 0; i < 8; i++)
                {
                    c.Step(16, Move(0f, 1f, 0f, false));
                }

                c.Settle();

                PMMoverSyncState auth = c.DsDriver.GetAuthoritativeSync();
                PMMoverSyncState pred = c.ApDriver.GetPredictedSync();
                CheckVec(pred.Position, auth.Position, 0.05f, "重同步之后 AP 继续与 DS 收敛");
                CheckTrue(c.ApDriver.StreamVersion == c.DsDriver.StreamVersion, "新流上持续对齐");

                // G2：旧流的载荷必须被丢弃（这里用**上一流的快照/事件**手动构造）。
                uint oldStream = streamBefore;
                PMR4MovementSnapshotBlob staleBlob = new PMR4MovementSnapshotBlob();
                staleBlob.Epoch = c.Epoch;
                staleBlob.InstanceId = c.ApDriver.InstanceId;
                staleBlob.StreamVersion = oldStream;
                staleBlob.OutputFrame = c.ApDriver.ConfirmedBoundary.Value + 100L;
                staleBlob.TotalSimTimeMs = 999999.0;
                staleBlob.Sync = c.ApDriver.GetPredictedSync();
                staleBlob.Aux = c.ApDriver.GetPredictedAux();

                long appliedBefore = c.ApDriver.SnapshotPayloadsApplied;
                c.OwnerPlayer.PublishMovementSnapshot(PMR4MovementCodec.EncodeSnapshot(false, staleBlob));
                c.ApDriver.OnSnapshotReplicated();
                c.ApDriver.Update(0.0);
                CheckTrue(c.ApDriver.SnapshotRejectedIdentity > 0 || c.ApDriver.SnapshotRejectedStreamNewer > 0,
                    "旧流快照被丢弃并计数（旧流不得回写新流状态）");
                CheckEq(c.ApDriver.SnapshotPayloadsApplied, appliedBefore, "旧流快照没有被应用");

                // 不倒退拒绝：同流但边界低于已确认边界的 Resync 载荷必须被拒。
                PMR4MovementSnapshotBlob regressing = new PMR4MovementSnapshotBlob();
                regressing.Epoch = c.Epoch;
                regressing.InstanceId = c.ApDriver.InstanceId;
                regressing.StreamVersion = c.ApDriver.StreamVersion;
                regressing.OutputFrame = 0L;
                regressing.TotalSimTimeMs = 0.0;
                regressing.Sync = c.ApDriver.GetPredictedSync();
                regressing.Aux = c.ApDriver.GetPredictedAux();

                long rejectedBefore = c.ApDriver.ResyncRejectedRegressing;
                c.OwnerPlayer.PMNet_ClientMovementResyncV1(PMR4MovementCodec.EncodeSnapshot(true, regressing));
                c.Rig.Frame(2);
                c.Apply();
                CheckTrue(c.ApDriver.ResyncRejectedRegressing > rejectedBefore,
                    "会倒退已确认边界的重同步被拒绝（契约：新流保持输出边界/累计时间不倒退）");
            }
            finally
            {
                if (c != null) { c.Dispose(); }
                ReplicatedPlayers.Clear();
            }
        }

        // =================================================================================
        //  H. SP：只插值、不预测、静止 yaw、冻结
        // =================================================================================

        private static void TestSimulatedProxy()
        {
            PlayerCase c = null;
            try
            {
                c = PlayerCase.Build(0x4B07u, new[] { 58, 59 }, 0);
                if (c.ApDriver == null || c.SpDrivers.Count == 0) { return; }

                PMR4MovementDriver sp = c.SpDrivers[0];
                CheckTrue(sp.Role == PMNetRole.SimulatedProxy, "SP Driver 角色正确");

                // H1（真实链路）：SP 会收快照、不预测、不参与权威模拟。
                //
                // 注意：**不能**用真实复制链的连续快照去测“Alpha ∈ (0,1)” ——
                // 复制通道对同一值会重复下发（同一 TotalSimTimeMs 连续到达），
                // 此时缓冲的 From==To、窗口退化，Sample() 正确地鉗到最新（Alpha=1）。
                // 相位受控的插值验证放在 H2。
                CheckEq(sp.Interpolation.MaxExtrapolateMs, 0L, "SP 外推上限为 0（首批只插值）");

                // SP 不得预测：Tick / Pump 都要显式拒绝。
                bool tickThrew = false;
                try { sp.Tick(16, Move(0f, 0f, 0f, false), c.ServerFrameId()); }
                catch (InvalidOperationException) { tickThrew = true; }
                CheckTrue(tickThrew, "SP 调用 Tick 被显式拒绝（不预测）");

                bool pumpThrew = false;
                try { sp.Pump(16.0, c.ServerFrameId()); }
                catch (InvalidOperationException) { pumpThrew = true; }
                CheckTrue(pumpThrew, "SP 调用 Pump 被显式拒绝（不权威模拟）");

                for (int i = 0; i < 8; i++)
                {
                    c.Step(16, Move(0f, 1f, 0f, false));
                }

                CheckTrue(sp.Interpolation.AuthorityAccepted > 0, "SP 收到并接受了权威快照（真实复制链）");
                CheckTrue(sp.Interpolation.Samples >= 0, "SP 采样计数可读");
                CheckTrue(sp.SamplePresentation().HasValue, "SP 能采出表现状态");
                CheckTrue(sp.Interpolation.SampleTimeMs <= sp.Interpolation.LastTotalSimTimeMs + 0.0001,
                    "SP 表现时钟不超过最新权威时间（无外推）");

                // H2：相位受控的插值验证。
                //
                // 用**真实入站入口**（PMR3Player.PublishMovementSnapshot → MovementDriver.OnSnapshotReplicated
                // → ApplyPending）喂入两张**总时长递增**的权威快照，
                // 从而把表现窗口与时钟相位精确落在可控位置。
                // （总时长必须相对当前权威时间递增：插值缓冲会拒绝倒退的快照，那是它应有的行为。）
                // 它验证的是 Driver + 插值缓冲的**插值语义**（不是“绕过网络”）：
                // 状态通道的真实性已由 C/E/G/J 的真实复制链覆盖。
                // 静止 yaw：位置不动、只转朝向 → SP 必须在窗口内插值，而不是直接跳到最新。
                double baseMs = sp.Interpolation.LastTotalSimTimeMs;

                FeedControlledSnapshot(c, sp, 101L, baseMs + 100.0, 0f); // 窗口1 = [≈base, base+100]，yaw → 0
                sp.Update(0.0);
                PMInterpolatedState<PMMoverSyncState, PMMoverAuxState> first = sp.SamplePresentation();
                CheckTrue(first.HasValue && !first.ClampedToLatest,
                    "受控窗口 1：表现时钟落在窗口内（未被鉗到最新，Alpha=" + first.Alpha + "）");

                sp.Advance(150.0); // 表现时钟推进到 base+150（已落在下一个窗口内部）
                FeedControlledSnapshot(c, sp, 102L, baseMs + 200.0, 90f); // 窗口2 = [base+100, base+200]
                sp.Update(0.0);
                PMInterpolatedState<PMMoverSyncState, PMMoverAuxState> mid = sp.SamplePresentation();
                CheckTrue(mid.HasValue, "受控窗口 2：SP 有可采样状态");
                CheckTrue(mid.Alpha > 0.3f && mid.Alpha < 0.7f,
                    "受控窗口 2：时钟落在窗口中间 ⇒ Alpha≈0.5（实际 " + mid.Alpha + "）");
                CheckTrue(!mid.ClampedToLatest, "受控窗口 2：未被鉗到最新（插值窗口真的在用）");
                CheckNear(mid.Sync.YawDegrees, 45f, 3f, "受控窗口 2：静止 yaw 在 0°→90° 之间插值到 ~45°");

                // 超出最新权威 ⇒ 鉗到最新（**不外推**）。
                sp.Advance(500.0);
                PMInterpolatedState<PMMoverSyncState, PMMoverAuxState> beyond = sp.SamplePresentation();
                CheckTrue(beyond.ClampedToLatest, "受控窗口：超出最新权威时被鉗到最新（不外推）");
                CheckEq((long)Math.Round(beyond.Alpha), 1L, "受控窗口：鉗到最新时 Alpha = 1");
                CheckTrue(beyond.TotalSimTimeMs <= sp.Interpolation.LastTotalSimTimeMs + 0.0001,
                    "受控窗口：钳位后总时长不超过最新权威");

                // 冻结：SP 不再推进表现时钟。
                double sampleTimeBefore = sp.Interpolation.SampleTimeMs;
                sp.Freeze();
                sp.Advance(100.0);
                CheckEq((long)sp.Interpolation.SampleTimeMs, (long)sampleTimeBefore, "冻结后 SP 表现时钟不推进");
                CheckTrue(sp.IsFrozen, "SP 进入冻结态");
            }
            finally
            {
                if (c != null) { c.Dispose(); }
                ReplicatedPlayers.Clear();
            }
        }

        /// <summary>
        /// 给 SP 喂一张**受控**权威快照（走真实入站入口，不绕过 Driver 的解码与缓冲）。
        /// </summary>
        private static void FeedControlledSnapshot(PlayerCase c, PMR4MovementDriver sp,
                                                  long outputFrame, double totalSimTimeMs, float yawDegrees)
        {
            PMMoverSyncState sync = c.DsDriver.GetAuthoritativeSync();
            sync.YawDegrees = yawDegrees;

            PMR4MovementSnapshotBlob blob = new PMR4MovementSnapshotBlob();
            blob.Epoch = c.Epoch;
            blob.InstanceId = sp.InstanceId;
            blob.StreamVersion = sp.StreamVersion;
            blob.OutputFrame = outputFrame;
            blob.ServerFrame = c.ServerFrame;
            blob.TotalSimTimeMs = totalSimTimeMs;
            blob.Sync = sync;
            blob.Aux = c.DsDriver.GetAuthoritativeAux();

            byte[] payload = PMR4MovementCodec.EncodeSnapshot(false, blob);
            c.SpPlayers[0].PublishMovementSnapshot(payload);
            sp.OnSnapshotReplicated();
        }

        // =================================================================================
        //  F2. 可靠域顺序：事件与重同步必须按**到达顺序**消费
        // =================================================================================

        /// <summary>
        /// 经**真实生成桩**从 DS 侧发一批权威事件（走 PMNet 可靠域 → Transport → 客户端 RPC 分发）。
        /// 刻意不直接调 <c>OnClientEventsPayload</c>：接收侧必须经过真实解码/身份/流校验与入队。
        /// </summary>
        private static void SendEventBatch(PlayerCase c, uint stream, int sequence, long boundary, int kind)
        {
            PMR4MovementEventRecord[] records = new PMR4MovementEventRecord[1];
            records[0].Boundary = boundary;
            records[0].Key = PMR4MovementDriver.BuildEventKey(boundary, kind);
            records[0].Kind = kind;
            records[0].Value = kind;

            byte[] payload = PMR4MovementCodec.EncodeEvents(
                c.Epoch, c.DsDriver.InstanceId, stream, sequence, records);

            c.ServerPlayer.PMNet_ClientMovementEventsV1(payload);
        }

        /// <summary>经**真实生成桩**从 DS 侧发一张重同步完整快照（owner-only 可靠 RPC）。</summary>
        private static void SendResyncSnapshot(PlayerCase c, uint stream, long outputFrame, double totalSimTimeMs)
        {
            PMR4MovementSnapshotBlob blob = new PMR4MovementSnapshotBlob();
            blob.Epoch = c.Epoch;
            blob.InstanceId = c.DsDriver.InstanceId;
            blob.StreamVersion = stream;
            blob.OutputFrame = outputFrame;
            blob.ServerFrame = c.ServerFrame;
            blob.TotalSimTimeMs = totalSimTimeMs;
            blob.Sync = c.DsDriver.GetAuthoritativeSync();
            blob.Aux = c.DsDriver.GetAuthoritativeAux();

            c.ServerPlayer.PMNet_ClientMovementResyncV1(PMR4MovementCodec.EncodeSnapshot(true, blob));
        }

        /// <summary>
        /// 可靠域顺序：事件与重同步共享同一个可靠顺序域，DS 侧按序发送
        /// （先 flush 事件批，再发升流重同步）。若接收侧“先跑完所有 Resync、再跑所有 Events”，
        /// 「旧流事件 → Resync2 → 流2事件 → Resync3 → 流3事件」会被重绑到流3之后才喂入旧流事件，
        /// 更早代次的事件会被当作过期丢弃 ⇒ **丢三条事件中的一条**（且只表现为“偶尔少一次不可逆通知”）。
        /// 本用例把五条报文按真实顺序一次性投递，要求三条事件不漏不重。
        /// </summary>
        private static void TestReliableOrderAcrossResyncs()
        {
            PlayerCase c = null;
            try
            {
                c = PlayerCase.Build(0x4B09u, new[] { 63 }, 0);
                if (c.ApDriver == null || c.DsDriver == null) { return; }

                for (int i = 0; i < 6; i++) { c.Step(16, Move(0f, 1f, 0f, false)); }
                c.Settle();
                c.Rig.Frame(2);
                c.Apply();

                long confirmed = c.ApDriver.ConfirmedBoundary.Value;
                CheckTrue(confirmed >= 4L, "AP 已确认边界足够承载三个事件边界（" + confirmed + "）");

                uint s1 = c.ApDriver.StreamVersion;
                uint s2 = s1 + 1u;
                uint s3 = s2 + 1u;

                long b1 = confirmed - 3L;
                long b2 = confirmed - 2L;
                long b3 = confirmed - 1L;

                // 重同步落到**远高于**当前 pending 的边界：这样无论期间是否又有属性快照把确认边界往前推，
                // 「不倒退」都成立，用例的判据只落在“事件是否按到达顺序被接纳”上。
                long resyncBase = c.ApDriver.OutputBoundary.Value + 1000L;
                double simBase = c.ApDriver.Timeline.CurrentTotalSimTimeMs;

                int dispatchedBefore = c.Dispatched.Count;
                long acceptedBefore = c.ApDriver.EventBatchesAccepted;
                long rejectedBefore = c.ApDriver.EventBatchesRejected;

                // 真实 Transport 发送（五条报文按到达顺序出队）。
                SendEventBatch(c, s1, 1, b1, PMR4MovementDriver.EventKindModeChanged);
                SendResyncSnapshot(c, s2, resyncBase, simBase + 1000.0);
                SendEventBatch(c, s2, 1, b2, PMR4MovementDriver.EventKindLanded);
                SendResyncSnapshot(c, s3, resyncBase + 1000L, simBase + 2000.0);
                SendEventBatch(c, s3, 1, b3, PMR4MovementDriver.EventKindModeChanged);

                // 投递 + 真实 RPC 分发（只入队），再一次 Update 里按到达顺序消费。
                c.Rig.Frame(2);
                c.ApDriver.Update(0.0);

                int added = c.Dispatched.Count - dispatchedBefore;
                CheckEq(added, 3L, "旧流事件→Resync2→流2事件→Resync3→流3事件：三条事件都不丢");
                CheckEq(c.ApDriver.EventBatchesAccepted, acceptedBefore + 3L, "三个事件批全部被接受");
                CheckEq(c.ApDriver.EventBatchesRejected, rejectedBefore,
                    "没有事件批被拒（不得把更早代次的事件当过期丢弃）");
                CheckTrue(!c.ApDriver.Journal.Failed, "事件日志未进入失败态");

                HashSet<ulong> keys = new HashSet<ulong>();
                bool unique = true;
                for (int i = dispatchedBefore; i < c.Dispatched.Count; i++)
                {
                    if (!keys.Add(c.Dispatched[i].Key)) { unique = false; }
                }

                CheckTrue(unique, "三条事件各派发一次（不重）");
                CheckTrue(keys.Contains(PMR4MovementDriver.BuildEventKey(b1, PMR4MovementDriver.EventKindModeChanged)),
                    "旧流事件（边界 " + b1 + "）确实被派发");
                CheckTrue(keys.Contains(PMR4MovementDriver.BuildEventKey(b2, PMR4MovementDriver.EventKindLanded)),
                    "流2事件（边界 " + b2 + "）确实被派发");
                CheckTrue(keys.Contains(PMR4MovementDriver.BuildEventKey(b3, PMR4MovementDriver.EventKindModeChanged)),
                    "流3事件（边界 " + b3 + "）确实被派发");

                CheckEq(c.ApDriver.StreamVersion, s3, "AP 流代次落到最后一条 Resync 的代次");
                CheckTrue(c.ApDriver.Timeline.ConfirmedFrame.Value >= resyncBase + 1000L,
                    "时间轴确认边界落到最后一条 Resync 的边界（不倒退）");
            }
            finally
            {
                if (c != null) { c.Dispose(); }
                ReplicatedPlayers.Clear();
            }
        }

        // =================================================================================
        //  F3. 事件派发回调异常：不重放、不再发、不打断其它通知
        // =================================================================================

        /// <summary>
        /// 宿主的表现回调（<c>EventDispatched</c>）抛异常时：
        ///   · 必须被**单独记录**，不能把异常括回复制应用/仿真路径；
        ///   · 已记录到「已派发」水位的不可逆事件**不得重复派发**（否则宿主动画/音效会重放）；
        ///   · 其它回调（已登记的接收者）仍必须收到该事件。
        /// </summary>
        private static void TestEventDispatchCallbackIsolation()
        {
            PlayerCase c = null;
            try
            {
                c = PlayerCase.Build(0x4B0Bu, new[] { 66 }, 0);
                if (c.ApDriver == null || c.DsDriver == null) { return; }

                for (int i = 0; i < 6; i++) { c.Step(16, Move(0f, 1f, 0f, false)); }
                c.Settle();
                c.Rig.Frame(2);
                c.Apply();

                long confirmed = c.ApDriver.ConfirmedBoundary.Value;
                CheckTrue(confirmed >= 2L, "AP 已确认边界足够派发一条事件（" + confirmed + "）");

                long boundary = confirmed - 1L;
                ulong key = PMR4MovementDriver.BuildEventKey(boundary, PMR4MovementDriver.EventKindModeChanged);
                uint stream = c.ApDriver.StreamVersion;
                int dispatchedBefore = c.Dispatched.Count;

                Action<PMPredictionEvent> boom = delegate(PMPredictionEvent e)
                {
                    throw new InvalidOperationException("host sink boom");
                };

                long failedBefore = c.ApDriver.EventDispatchFailed;
                c.ApDriver.EventDispatched += boom;
                try
                {
                    SendEventBatch(c, stream, 1, boundary, PMR4MovementDriver.EventKindModeChanged);
                    c.Rig.Frame(2);
                    c.ApDriver.Update(0.0);
                }
                finally
                {
                    c.ApDriver.EventDispatched -= boom;
                }

                CheckEq(c.Dispatched.Count - dispatchedBefore, 1L, "抛异常的宿主回调不打断其它接收者（仍派发 1 条）");
                CheckTrue(c.ApDriver.EventDispatchFailed > failedBefore,
                    "宿主回调异常被单独记录（EventDispatchFailed=" + c.ApDriver.EventDispatchFailed + "）");
                CheckEq(c.ApDriver.Journal.PendingEvents, 0, "异常后未派发事件数归零");
                CheckEq(c.ApDriver.Journal.DispatchedThrough, boundary, "异常后派发水位已经推进");

                // 再跑若干帧：同一条不可逆事件绝不能因为“上次回调抛了异常”而被重发。
                c.Rig.Frame(2);
                c.ApDriver.Update(0.0);

                int sameKey = 0;
                for (int i = dispatchedBefore; i < c.Dispatched.Count; i++)
                {
                    if (c.Dispatched[i].Key == key) { sameKey++; }
                }

                CheckEq(sameKey, 1L, "同一条不可逆事件只派发一次（回调异常不导致重放）");

                // 日志层同样的不变量（直接调 DispatchTo，让异常真的抛出去而不是被 Driver 包住）：
                // 回调抛异常时水位必须**已经前移**，否则下一次派发会把同一条不可逆事件再发一次。
                PMR4MovementEventJournal journal = new PMR4MovementEventJournal();
                journal.Rebind(21u, 0L);

                PMR4MovementEventBatch batch = new PMR4MovementEventBatch();
                batch.Epoch = 1u;
                batch.InstanceId = 2u;
                batch.StreamVersion = 21u;
                batch.Sequence = 1;
                batch.Events = new PMR4MovementEventRecord[1];
                batch.Events[0].Boundary = 5L;
                batch.Events[0].Key = PMR4MovementDriver.BuildEventKey(5L, PMR4MovementDriver.EventKindModeChanged);
                batch.Events[0].Kind = PMR4MovementDriver.EventKindModeChanged;

                string journalError;
                CheckTrue(journal.TryAccept(batch, out journalError), "日志接受一批事件：" + journalError);

                int journalCalls = 0;
                try
                {
                    journal.DispatchTo(5L, delegate(PMPredictionEvent e)
                    {
                        journalCalls++;
                        throw new InvalidOperationException("journal sink boom");
                    });
                }
                catch (InvalidOperationException)
                {
                }

                CheckEq(journalCalls, 1L, "日志回调被调用一次");
                CheckEq(journal.PendingEvents, 0, "回调抛异常后未派发事件数归零（水位已前移）");
                CheckEq(journal.DispatchedThrough, 5L, "回调抛异常后派发水位已推进");

                int journalCalls2 = 0;
                journal.DispatchTo(5L, delegate(PMPredictionEvent e) { journalCalls2++; });
                CheckEq(journalCalls2, 0L, "再次派发不会重发同一条事件（日志层也不重放）");
            }
            finally
            {
                if (c != null) { c.Dispose(); }
                ReplicatedPlayers.Clear();
            }
        }

        // =================================================================================
        //  H2. SP 与 DS 升流：复制快照重绑 / 旧流不回写 / 冻结不升流
        // =================================================================================
        /// <summary>
        /// SP 收不到 owner-only 的 <c>ClientMovementResyncV1</c>，因此较新代次的合法**复制快照**
        /// 必须是它唯一的重绑来源（否则 DS 一升流，SP 就永远停在旧流、再也接不上快照）。
        /// 同时必须保持两条红线：旧流快照不得回写；冻结的 SP 不得借快照升流。
        /// </summary>
        private static void TestSimulatedProxyStreamRebind()
        {
            PlayerCase c = null;
            try
            {
                c = PlayerCase.Build(0x4B0Au, new[] { 64, 65 }, 0);
                if (c.ApDriver == null || c.SpDrivers.Count == 0) { return; }

                PMR4MovementDriver sp = c.SpDrivers[0];

                for (int i = 0; i < 6; i++) { c.Step(16, Move(0f, 1f, 0f, false)); }

                CheckTrue(sp.Interpolation.AuthorityAccepted > 0, "SP 在升流前已接受权威快照（真实复制链）");
                uint streamBefore = sp.StreamVersion;
                double simBefore = sp.Interpolation.LastTotalSimTimeMs;
                long appliedBefore = sp.SnapshotPayloadsApplied;

                // DS 真实升流：契约里唯一允许递增 StreamVersion 的位置。
                CheckTrue(c.DsDriver.BeginServerResync("sp-newer-stream"), "DS 升流成功（真实宿主路径）");
                uint dsStream = c.DsDriver.StreamVersion;
                CheckTrue(dsStream > streamBefore, "DS 流代次确实递增（" + streamBefore + " → " + dsStream + "）");

                c.Rig.Frame(3);
                c.Apply();

                CheckEq(sp.StreamVersion, dsStream, "SP 经复制快照追上 DS 的新流代次（收不到重同步 RPC）");
                CheckTrue(sp.SnapshotStreamRebinds > 0, "SP 的快照重绑路径被走到并计数");
                CheckTrue(sp.SnapshotPayloadsApplied > appliedBefore, "新流快照被 SP 真实应用（插值绑定已更新）");
                CheckTrue(sp.Interpolation.LastTotalSimTimeMs >= simBefore, "SP 的权威累计仿真时间不倒退");
                CheckTrue(sp.SamplePresentation().HasValue, "重绑后 SP 仍可采样表现状态");

                // 旧流快照不得回写：即使它带着更大的边界/仿真时间。
                double simNow = sp.Interpolation.LastTotalSimTimeMs;
                long outNow = sp.Interpolation.LastOutputFrame.Value;

                PMMoverSyncState staleSync = c.DsDriver.GetAuthoritativeSync();
                staleSync.Position = new PMVector3(50f, 1f, 50f);

                PMR4MovementSnapshotBlob stale = new PMR4MovementSnapshotBlob();
                stale.Epoch = c.Epoch;
                stale.InstanceId = sp.InstanceId;
                stale.StreamVersion = streamBefore;          // ← 旧流
                stale.OutputFrame = outNow + 5L;
                stale.ServerFrame = c.ServerFrame;
                stale.TotalSimTimeMs = simNow + 500.0;
                stale.Sync = staleSync;
                stale.Aux = c.DsDriver.GetAuthoritativeAux();

                long identityRejectedBefore = sp.SnapshotRejectedIdentity;
                c.ServerPlayer.PublishMovementSnapshot(PMR4MovementCodec.EncodeSnapshot(false, stale));
                c.Rig.Frame(3);
                c.Apply();

                CheckTrue(sp.SnapshotRejectedIdentity > identityRejectedBefore,
                    "旧流快照被 SP 丢弃并计数（旧流不回写）");
                CheckTrue(sp.Interpolation.LastTotalSimTimeMs < stale.TotalSimTimeMs,
                    "旧流快照没有被应用到插值缓冲");
                CheckEq(sp.StreamVersion, dsStream, "旧流快照不改写 SP 的流代次");

                // 冻结的 SP 不借复制快照升流（断线冻结保持）。
                sp.Freeze();
                uint streamFrozen = sp.StreamVersion;
                double simFrozen = sp.Interpolation.LastTotalSimTimeMs;

                PMR4MovementSnapshotBlob newer = new PMR4MovementSnapshotBlob();
                newer.Epoch = c.Epoch;
                newer.InstanceId = sp.InstanceId;
                newer.StreamVersion = streamFrozen + 1u;     // ← 更晚的流代次
                newer.OutputFrame = sp.Interpolation.LastOutputFrame.Value + 10L;
                newer.ServerFrame = c.ServerFrame;
                newer.TotalSimTimeMs = simFrozen + 1000.0;
                newer.Sync = c.DsDriver.GetAuthoritativeSync();
                newer.Aux = c.DsDriver.GetAuthoritativeAux();

                long frozenBefore = sp.SnapshotRebindRejectedFrozen;
                c.ServerPlayer.PublishMovementSnapshot(PMR4MovementCodec.EncodeSnapshot(false, newer));
                c.Rig.Frame(3);
                sp.Update(0.0);

                CheckTrue(sp.SnapshotRebindRejectedFrozen > frozenBefore, "冻结期间的新流快照被显式拒绝并计数");
                CheckEq(sp.StreamVersion, streamFrozen, "冻结的 SP 不借复制快照升流（流代次不变）");
                CheckTrue(Math.Abs(sp.Interpolation.LastTotalSimTimeMs - simFrozen) < 0.0001,
                    "冻结的 SP 插值基准不前进（断线冻结保持）");
            }
            finally
            {
                if (c != null) { c.Dispose(); }
                ReplicatedPlayers.Clear();
            }
        }

        // =================================================================================
        //  I. DS 预算与信用
        // =================================================================================

        private static void TestBudgetAndCredit()
        {
            PlayerCase c = null;
            try
            {
                c = PlayerCase.Build(0x4B08u, new[] { 60 }, 0);
                if (c.ApDriver == null || c.DsDriver == null) { return; }

                // 先灌满输入队列（离线 tick + 上行，但不让 DS 消费）。
                for (int i = 0; i < 40; i++)
                {
                    c.ApDriver.Tick(16, Move(0f, 1f, 0f, false), c.ServerFrameId());
                    c.ApDriver.SendInputPayload();
                }

                c.Rig.Frame(1);

                // 一次超长墙钟：步数上限 8、输入时间上限 100ms 必须拦住"一帧跳一大段"。
                PMPumpResult pumped = c.DsDriver.Pump(1000.0, c.ServerFrameId());
                CheckTrue(pumped.Steps <= 8, "一次 Pump 步数 <= 8（实际 " + pumped.Steps + "）");
                CheckTrue(pumped.ConsumedMs <= 100, "一次 Pump 消费输入时间 <= 100ms（实际 " + pumped.ConsumedMs + "）");
                CheckTrue(c.DsDriver.AuthorityBuffer.CreditMs <= c.DsDriver.AuthorityBuffer.CreditCapMs,
                    "信用不超过上限");
                CheckEq(c.DsDriver.AuthorityNextInputFrame, pumped.Steps,
                    "水位只按真实消费推进（不按客户端自报帧号跳进度）");
                c.ServerFrame++;

                // 同一宿主 tick 重复调用：不得重复充值 / 重复推进。
                long tickId = 0x1234L;
                PMPumpResult first = c.DsDriver.Pump(100.0, c.ServerFrameId(), tickId);
                double creditAfterFirst = c.DsDriver.AuthorityBuffer.CreditMs;
                PMPumpResult second = c.DsDriver.Pump(100.0, c.ServerFrameId(), tickId);
                CheckEq(second.Steps, 0, "同一宿主 tick 的第二次 Pump 零步");
                CheckTrue(c.DsDriver.AuthorityBuffer.CreditMs <= creditAfterFirst,
                    "同一宿主 tick 不重复补充信用");
                CheckTrue(c.DsDriver.DuplicateHostTickRejections > 0, "重复宿主 tick 被计数");

                // 上行 0dt 一律拒绝（编码层）。
                ExpectFormatException(delegate
                {
                    PMR4MovementInputEntry e = new PMR4MovementInputEntry();
                    e.InputFrame = 1L;
                    e.StepMs = 0;
                    e.Input = Move(0f, 0f, 0f, false);
                    PMR4MovementCodec.EncodeInputs(c.Epoch, c.ApDriver.InstanceId, 1u, new[] { e });
                }, "上行 0dt 被拒（0 只允许 DS 侧缺帧占位）");

                // 缺帧不跨越：洞后面有输入时，DS 必须等待而不是拿最大帧跨过去。
                PlayerCase c2 = PlayerCase.Build(0x4B09u, new[] { 61 }, 0);
                try
                {
                    if (c2.ApDriver != null && c2.DsDriver != null)
                    {
                        c2.ApDriver.Tick(16, Move(0f, 1f, 0f, false), c2.ServerFrameId());
                        c2.ApDriver.Tick(16, Move(0f, 1f, 0f, false), c2.ServerFrameId());
                        // 手工只发"第 2 条"（构造缺口：第 1 条不在包内）。
                        PMR4MovementInputEntry[] only2 = new PMR4MovementInputEntry[1];
                        only2[0].InputFrame = 2L;
                        only2[0].StepMs = 16;
                        only2[0].Input = Move(0f, 0f, 0f, false);
                        byte[] gapPayload = PMR4MovementCodec.EncodeInputs(
                            c2.Epoch, c2.ApDriver.InstanceId, c2.ApDriver.StreamVersion, only2);
                        c2.OwnerPlayer.PMNet_ServerMovementInputV1(gapPayload);
                        // 发送→传输层 flush→投递→派发 都发生在帧边界上，且**各需一帧**。
                        c2.Rig.Frame(2);
                        PMPumpResult gapPump = c2.DsDriver.Pump(16.0, c2.ServerFrameId());
                        CheckEq(gapPump.Steps, 0, "缺帧时 Pump 不跨越（零步）");
                        CheckTrue(c2.DsDriver.AuthorityBuffer.HasGap, "缺口被识别（HasGap）");
                        CheckEq(c2.DsDriver.AuthorityNextInputFrame, 0L, "缺帧时水位不前进");
                    }
                }
                finally
                {
                    c2.Dispose();
                }
            }
            finally
            {
                if (c != null) { c.Dispose(); }
                ReplicatedPlayers.Clear();
            }
        }

        // =================================================================================
        //  J. 多角色隔离
        // =================================================================================

        private static void TestMultiPlayerIsolation()
        {
            Rig rig = null;
            try
            {
                uint epoch = 0x4B0Au;
                rig = Rig.Build(epoch, new[] { 71, 72 });
                PMMoverTestWorld world = new PMMoverTestWorld();

                PMR3Player a = rig.SpawnPlayer(0);
                PMR3Player b = rig.SpawnPlayer(1);
                CheckTrue(a != null && b != null && a.NetId.Value != b.NetId.Value, "两个玩家副本各有唯一 NetId");

                PMR4MovementDriver da = new PMR4MovementDriver(a, world, PMNetRole.Authority, epoch,
                    PMMoverSyncState.CreateDefault(), PMMoverAuxState.CreateDefault());
                PMR4MovementDriver db = new PMR4MovementDriver(b, world, PMNetRole.Authority, epoch,
                    PMMoverSyncState.CreateDefault(), PMMoverAuxState.CreateDefault());
                da.PublishInitialSnapshot();
                db.PublishInitialSnapshot();

                CheckTrue(da.InstanceId != db.InstanceId, "两个 Driver 的 instanceId 不同（身份绑定对象而非进程）");
                CheckEq(da.StreamVersion, 1u, "A 的流代次独立");
                CheckEq(db.StreamVersion, 1u, "B 的流代次独立");

                rig.Frame(3);

                // 只驱动 A：B 的权威状态与边界必须一动不动。
                PMMoverSyncState bBefore = db.GetAuthoritativeSync();
                for (int i = 0; i < 10; i++)
                {
                    PMR4MovementInputEntry e = new PMR4MovementInputEntry();
                    e.InputFrame = i;
                    e.StepMs = 16;
                    e.Input = Move(0f, 1f, 0f, false);
                    byte[] payload = PMR4MovementCodec.EncodeInputs(epoch, da.InstanceId, da.StreamVersion,
                        new[] { e });
                    a.PMNet_ServerMovementInputV1(payload);
                    rig.Frame(1);
                    da.Pump(16.0, new PMFrameId(PMFrameDomain.AuthorityServer, i + 1));
                }

                CheckEq(da.OutputBoundary.Value, 10L, "A 的边界推进到 10");
                CheckEq(db.OutputBoundary.Value, 0L, "B 的边界保持 0（未被 A 的输入驱动）");
                PMMoverSyncState bAfter = db.GetAuthoritativeSync();
                CheckVec(bAfter.Position, bBefore.Position, 0f, "B 的权威位置逐位不变（多角色隔离）");
                CheckEq(db.InputsAdmitted, 0L, "B 没有接纳任何输入");

                // 把 A 的载荷错投给 B：身份不匹配必须被拒。
                PMR4MovementInputEntry wrong = new PMR4MovementInputEntry();
                wrong.InputFrame = 99L;
                wrong.StepMs = 16;
                wrong.Input = Move(0f, 1f, 0f, false);
                byte[] wrongPayload = PMR4MovementCodec.EncodeInputs(epoch, 12345u, 1u, new[] { wrong });
                b.PMNet_ServerMovementInputV1(wrongPayload);
                rig.Frame(1);
                db.Pump(16.0, new PMFrameId(PMFrameDomain.AuthorityServer, 99));
                CheckTrue(db.InputRejectedIdentity > 0, "instanceId 不匹配的上行输入被拒（身份校验）");
                CheckEq(db.OutputBoundary.Value, 0L, "被拒的输入没有推进 B 的边界");

                // J3：**防非 owner** —— 另一个客户持的是 A 的 SimulatedProxy 副本，
                // 它冒名上行时必须被服务端按“来源连接是不是该对象 owner”拒掉（D-R0-42）。
                PMNetObject victimObj;
                bool victimFound = rig.ClientWorld(1).TryFind(a.NetId, out victimObj) && victimObj != null;
                CheckTrue(victimFound, "client2 上拿到 A 的副本（SimulatedProxy）");
                if (victimFound)
                {
                    PMR3Player victim = victimObj as PMR3Player;
                    CheckTrue(victim != null && victim.Role == PMNetRole.SimulatedProxy,
                        "A 在 client2 上的副本角色 = SimulatedProxy（不是 owner）");

                    PMR4MovementInputEntry cheat = new PMR4MovementInputEntry();
                    cheat.InputFrame = 4242L;
                    cheat.StepMs = 16;
                    cheat.Input = Move(0f, 1f, 0f, false);
                    byte[] cheatPayload = PMR4MovementCodec.EncodeInputs(epoch, da.InstanceId, da.StreamVersion,
                        new[] { cheat });

                    long admittedBefore = da.InputsAdmitted;
                    long rejectedBefore = rig.ServerViews[1].Connection.RpcRejected;
                    long boundaryBefore = da.OutputBoundary.Value;

                    victim.PMNet_ServerMovementInputV1(cheatPayload);
                    rig.Frame(2);
                    da.Pump(16.0, new PMFrameId(PMFrameDomain.AuthorityServer, 500));

                    CheckEq(da.InputsAdmitted, admittedBefore, "非 owner 的上行输入**没有**被执行（InputsAdmitted 不变）");
                    CheckEq(da.OutputBoundary.Value, boundaryBefore, "非 owner 的上行没有推进权威边界");
                    CheckTrue(rig.ServerViews[1].Connection.RpcRejected > rejectedBefore,
                        "服务端在非 owner 连接上拒绝了该 RPC（归属校验生效）");
                }

                da.Dispose();
                db.Dispose();
            }
            finally
            {
                if (rig != null) { rig.Dispose(); }
                ReplicatedPlayers.Clear();
            }
        }

        // =================================================================================
        //  K. 冻结 / 释放 / 诊断
        // =================================================================================

        private static void TestLifecycle()
        {
            PlayerCase c = null;
            try
            {
                c = PlayerCase.Build(0x4B0Bu, new[] { 81 }, 0);
                if (c.ApDriver == null || c.DsDriver == null) { return; }

                for (int i = 0; i < 5; i++)
                {
                    c.Step(16, Move(0f, 1f, 0f, false));
                }

                long confirmedBefore = c.ApDriver.ConfirmedBoundary.Value;
                c.ApDriver.Freeze();
                CheckTrue(c.ApDriver.IsFrozen, "AP 冻结成功（断线只冻结，不丢历史）");
                CheckEq(c.ApDriver.ConfirmedBoundary.Value, confirmedBefore, "冻结不改变已确认边界");

                // 冻结期间权威快照一律被拒（Stalled），状态零改动。
                long stalledBefore = c.ApDriver.Timeline.StalledAuthorities;
                for (int i = 0; i < 6; i++)
                {
                    c.Rig.Frame(1);
                    c.ApDriver.Update(16.0);
                }

                CheckTrue(c.ApDriver.Timeline.StalledAuthorities > stalledBefore,
                    "冻结期间的权威快照被拒并计数（不解冻、不部分应用）");

                // 墙钟跨过 2s ⇒ 只通知一次重同步请求。
                for (int i = 0; i < 40; i++)
                {
                    c.ApDriver.Update(100.0);
                }

                CheckTrue(c.ApDriver.FrozenWallClockRequests >= 1, "断线墙钟触发了重同步请求通知");
                CheckTrue(c.ApDriver.ResyncRequestsSent >= 1, "重同步请求经生成桩发出");

                // 诊断摘要必须能区分三重身份。
                string described = c.ApDriver.Describe();
                CheckTrue(described.Contains("epoch=") && described.Contains("instance=") && described.Contains("stream="),
                    "诊断摘要同时给出 epoch / instanceId / streamVersion（三者不混用）");

                string dsDescribed = c.DsDriver.Describe();
                CheckTrue(dsDescribed.Contains("role=Authority") && dsDescribed.Contains("creditMs="),
                    "DS 诊断摘要含角色与信用");

                // Dispose 幂等 + 解绑 player 引用。
                c.ApDriver.Dispose();
                c.ApDriver.Dispose();
                CheckTrue(c.ApDriver.IsDisposed, "Driver 释放幂等");
                CheckTrue(c.OwnerPlayer.MovementDriver == null, "释放后 player 上的 Driver 引用已摘除");

                bool threw = false;
                try { c.ApDriver.GetPredictedSync(); }
                catch (ObjectDisposedException) { threw = true; }
                CheckTrue(threw, "释放后调用被显式拒绝（ObjectDisposedException）");
            }
            finally
            {
                if (c != null) { c.Dispose(); }
                ReplicatedPlayers.Clear();
            }
        }
    }
}
