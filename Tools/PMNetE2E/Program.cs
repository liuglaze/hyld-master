// R2-C 端到端总门禁（T40 + T41 收口）。
//
// 事实来源：
//   - Docs/plans/net-r2-codegen-contract.md（§3 协议摘要、§4 生成物 API 面、§4.3.1 实参闭包、
//     §4.3.2 校验三态路由、§5 规则集、§6 类型集、§7-C 集成与总门禁）
//   - Docs/plans/net-r0-contract.md §2.4（D-R0-12..18）、§5（复制与生命周期契约）
//   - Docs/plans/net-architecture-migration.md §5 的 R2 行、§5.4/§5.5、§6.1 的 T40/T41
//
// 结构（每段都自己数一遍"实际跑到了多少"，见任务书 §4「门禁自身」）：
//   [0] 生成物与当前声明一致（PMNetGen decl-check，只校验不写盘，逐字节）
//   [1] 声明 → 注册表：零反射注册后类/属性/RPC 计数与生成物一致
//   [2] 复制收敛（T41）：不同基线最终一致 / 旧 ACK 不清新脏位 / 值未变不发 / 条件跃迁补发 / OnRep 真被调用
//   [2b] Create 初值（D-R0-16）：首次 Tick 之前构包，创建回调里就读到初值；
//        非拥有者拿不到 OwnerOnly 成员
//   [3] RPC 双向（T40）：调用桩入队 / 实参不被覆盖 / 接收侧三态 / 归属校验
//       （数组调用点快照、入队前长度门、队列超限显式失败）
//       （带真实连接与世界查找的接收入口：非 Owner / Owner / 已销毁 /
//         方向错 / IgnoreRpcs / 原生 Validate false,true / ForceValidate 三态 + 非法值 / 畸形包）
//   [3b] RPC 注册表按 (ClassId, RpcId) 复合键：两个类可以共用同一个 RpcId，
//        投递按目标类分派；裸 RpcId 歧义时查询返回 false；类内重复仍硬失败；
//        注册失败不留下半个类
//   [4] 语言面：生成物 + 夹具 + PMNet 运行时在 C# 7.3 + netstandard2.0 下真编译
//   [5] 负向验证：五个缺陷各注入一次，断言门禁**确实抓到**（含注入前后失败数）
//
// 退出码：0 = 干净实现全部通过且五个缺陷全部被抓到；1 = 否则。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PMNet;

namespace PMNetE2E
{
    internal static class Program
    {
        // ------------------------------------------------------------------ 断言框架

        private static int _passed;
        private static readonly List<string> _failures = new List<string>();

        /// <summary>「实际跑到的数量」计数：防止出现「0 个用例也通过」。</summary>
        private static int _nClasses;
        private static int _nProperties;
        private static int _nRpcs;
        private static int _nRepPayloads;
        private static int _nRepRecords;
        private static int _nOnRep;
        private static int _nRpcQueued;
        private static int _nRpcInvoked;
        private static int _nValidationSink;
        private static int _nRpcReceiveAccepted;
        private static int _nRpcReceiveRejected;
        private static int _nDisconnectRequested;
        private static int _nArraySnapshot;
        private static int _nCreateCallbacks;

        private static int Main(string[] args)
        {
            Console.WriteLine("=== R2-C 端到端总门禁（T40 + T41 收口） ===");
            Console.WriteLine("链条：声明（E2eFixtures.cs）→ PMNetGen 生成 → 零反射注册表 → 复制收敛 → RPC 双向");
            Console.WriteLine();

            string root = FindRepoRoot();
            Console.WriteLine("仓库根目录：" + root);
            Console.WriteLine();

            Section("0. 生成物与当前声明逐字节一致（PMNetGen 的声明校验模式，只读）", delegate () { CheckGeneratedFresh(root); });
            Section("1. 声明 → 注册表：零反射注册后计数与生成物一致", CheckRegistry);
            Section("2. 复制收敛（T41）", TestReplication);
            Section("2b. Create 初值（D-R0-16）：首次 Tick 前构包，创建回调里就读到初值", TestCreateInitialState);
            Section("3. RPC 双向（T40）", TestRpc);
            Section("3b. RPC 注册表按 (ClassId, RpcId) 分桶（复合键投递 / 裸 RpcId 歧义 / 错误类拒绝 / 注册原子性）",
                TestRpcRegistryCompositeKey);
            Section("4. 语言面：生成物 + 夹具 + PMNet 运行时在 C# 7.3 + netstandard2.0 下真编译",
                delegate () { CheckLanguageSurface(root); });
            Section("5. 负向验证：注入五个缺陷，断言门禁确实抓到", TestFaultInjection);

            Section("[6] 门禁自身：实际跑到的数量必须全部 > 0（不得「0 个用例也通过」）", CheckReachCounts);

            Console.WriteLine("=== 汇总 ===");
            Console.WriteLine("通过 " + _passed + " 项，失败 " + _failures.Count + " 项");
            for (int i = 0; i < _failures.Count; i++)
            {
                Console.WriteLine("  - " + _failures[i]);
            }

            if (_passed > 0 && _failures.Count == 0)
            {
                Console.WriteLine("结果：PASS");
                return 0;
            }

            Console.WriteLine("结果：FAIL");
            return 1;
        }

        // ------------------------------------------------------------------ 通用工具

        /// <summary>
        /// 从可执行文件目录向上找「同时含 Client 与 Tools 的那一层」。
        /// 刻意不硬编码绝对路径：主计划 §9.5 教训 16 是「路径硬编码 + 失败不报错」。
        /// </summary>
        private static string FindRepoRoot()
        {
            DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "Client"))
                    && Directory.Exists(Path.Combine(dir.FullName, "Tools")))
                {
                    return dir.FullName.Replace('\\', '/');
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException("找不到同时含 Client 与 Tools 的仓库根目录，起点：" + AppContext.BaseDirectory);
        }

        private static void Section(string name, Action body)
        {
            Console.WriteLine("── " + name);
            try
            {
                body();
            }
            catch (Exception ex)
            {
                _failures.Add("[" + name + "] 测试体抛异常：" + ex.GetType().Name + " " + ex.Message);
                Console.WriteLine("    FAIL 测试体抛异常：" + ex.GetType().Name + " " + ex.Message);
            }

            Console.WriteLine();
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
            }

            Console.WriteLine((ok ? "    OK   " : "    FAIL ") + label);
        }

        /// <summary>
        /// 「0 个用例也通过」是门禁最危险的失败形态：断言全绿只是因为什么都没跑到。
        /// 因此把每类实际触达的东西都数一遍，并逐条要求 > 0。
        /// </summary>
        private static void CheckReachCounts()
        {
            ReportCounters();
            Console.WriteLine();

            Check(_nClasses > 0, "实际注册的网络类数 > 0（" + _nClasses + "）");
            Check(_nProperties > 0, "实际跑到的复制属性数 > 0（" + _nProperties + "）");
            Check(_nRpcs > 0, "实际跑到的 RPC 数 > 0（" + _nRpcs + "）");
            Check(_nRepPayloads > 0, "实际发出的复制载荷数 > 0（" + _nRepPayloads + "）");
            Check(_nRepRecords > 0, "实际投递并应用的复制记录数 > 0（" + _nRepRecords + "）");
            Check(_nOnRep > 0, "实际触发的 OnRep 分发数 > 0（" + _nOnRep + "）");
            Check(_nRpcQueued > 0, "实际进入待发队列的 RPC 数 > 0（" + _nRpcQueued + "）");
            Check(_nRpcInvoked > 0, "实际执行的 RPC 实现数 > 0（" + _nRpcInvoked + "）");
            Check(_nValidationSink > 0, "实际产生的校验上报数 > 0（" + _nValidationSink + "）");
            Check(_nRpcReceiveAccepted > 0, "通过接收入口交给执行器的 RPC 数 > 0（" + _nRpcReceiveAccepted + "）");
            Check(_nRpcReceiveRejected > 0, "被接收入口闸门拒绝的 RPC 数 > 0（" + _nRpcReceiveRejected + "）");
            Check(_nDisconnectRequested > 0, "接收入口实际向来源连接发出的断连请求数 > 0（" + _nDisconnectRequested + "）");
            Check(_nArraySnapshot > 0, "数组实参调用点快照实际生效的次数 > 0（" + _nArraySnapshot + "）");
            Check(_nCreateCallbacks > 0, "实际触发的接收侧创建回调数 > 0（" + _nCreateCallbacks + "）");
        }

        private static void ReportCounters()
        {
            Console.WriteLine("    类数           = " + _nClasses);
            Console.WriteLine("    复制属性数     = " + _nProperties);
            Console.WriteLine("    RPC 数         = " + _nRpcs);
            Console.WriteLine("    复制载荷数     = " + _nRepPayloads);
            Console.WriteLine("    复制记录数     = " + _nRepRecords);
            Console.WriteLine("    OnRep 分发数   = " + _nOnRep);
            Console.WriteLine("    RPC 入队数     = " + _nRpcQueued);
            Console.WriteLine("    RPC 执行数     = " + _nRpcInvoked);
            Console.WriteLine("    校验上报数     = " + _nValidationSink);
            Console.WriteLine("    接收入口放行数 = " + _nRpcReceiveAccepted);
            Console.WriteLine("    接收入口拒绝数 = " + _nRpcReceiveRejected);
            Console.WriteLine("    实际断连请求数 = " + _nDisconnectRequested);
            Console.WriteLine("    数组快照次数   = " + _nArraySnapshot);
            Console.WriteLine("    创建回调次数   = " + _nCreateCallbacks);
        }

        private static string Trim(string s, int max)
        {
            if (s == null)
            {
                return string.Empty;
            }

            s = s.Replace("\r", string.Empty).Replace("\n", " | ");
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        // ------------------------------------------------------------------ [0] 生成物新鲜度

        private static void CheckGeneratedFresh(string root)
        {
            string genDll = root + "/Tools/PMNetGen/bin/Release/net8.0/PMNetGen.dll";
            string fixtures = root + "/Tools/PMNetE2E/E2eFixtures.cs";
            string outDir = root + "/Tools/PMNetE2E/Generated";
            string idLock = root + "/Tools/PMNetE2E/e2e-ids.json";

            Check(File.Exists(genDll), "PMNetGen.dll 存在：" + genDll);
            Check(File.Exists(fixtures), "夹具文件存在：" + fixtures);
            Check(File.Exists(idLock), "ID 锁文件存在：" + idLock);
            if (!File.Exists(genDll) || !File.Exists(fixtures))
            {
                return;
            }

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = "dotnet";
            psi.Arguments = "\"" + genDll + "\" --decl-check \"" + fixtures + "\" --out-dir \""
                            + outDir + "\" --id-lock \"" + idLock + "\"";
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.WorkingDirectory = root;

            using (Process p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();

                Check(p.ExitCode == 0,
                    "声明校验模式退出码 0（产物与锁文件都与当前声明逐字节一致），实际 " + p.ExitCode);
                if (p.ExitCode != 0)
                {
                    Console.WriteLine("      stdout: " + Trim(stdout, 600));
                    Console.WriteLine("      stderr: " + Trim(stderr, 600));
                }
            }
        }

        // ------------------------------------------------------------------ [1] 注册表

        private static void CheckRegistry()
        {
            PMNetRegistry.Reset();
            PMNet.Generated.PMNetGeneratedRegistry.RegisterAll();

            Check(PMNetRegistry.IsSealed, "RegisterAll 之后注册表已封板");
            Check(PMNetRegistry.ClassCount == PMNet.Generated.PMNetGeneratedRegistry.GeneratedClassCount,
                "注册类数 == 生成期常量 GeneratedClassCount（" + PMNetRegistry.ClassCount + " == "
                + PMNet.Generated.PMNetGeneratedRegistry.GeneratedClassCount + "）—— 这一条同时防住"
                + "「新增 .g.cs 没被编进来」这类静默缺口（主计划 §9.5 教训 7）");

            PMNetClassEntry rep = null;
            PMNetRegistry.TryGetClass(E2eReplicated.PMGeneratedClassId, out rep);
            PMNetClassEntry board = null;
            PMNetRegistry.TryGetClass(E2eScoreboard.PMGeneratedClassId, out board);

            int props = (rep != null && rep.Rep != null && rep.Rep.Properties != null ? rep.Rep.Properties.Length : 0)
                        + (board != null && board.Rep != null && board.Rep.Properties != null ? board.Rep.Properties.Length : 0);

            _nClasses += PMNetRegistry.ClassCount;
            _nProperties += props;
            _nRpcs += PMNetRegistry.RpcCount;

            Check(props == 5, "生成描述符里的复制属性总数 == 5（主类 4 + 第二类 1），实际 " + props);
            Check(rep != null && rep.Rep.ChangeMaskBitCount == 4,
                "主夹具 ChangeMaskBitCount == 4，实际 "
                + (rep != null && rep.Rep != null ? rep.Rep.ChangeMaskBitCount : -1));

            // 协议摘要必须两端一致：生成期写进注册表的值 == 运行期算出来的值。
            Check(PMNet.Generated.PMNetGeneratedRegistry.ProtocolHash == PMNetRegistry.ProtocolHash,
                "运行期 ProtocolHash 与生成期一致：0x" + PMNetRegistry.ProtocolHash.ToString("X8"));

            PMNetRpcEntry fire;
            bool hasFire = PMNetRegistry.TryGetRpc(E2eReplicated.PMGeneratedRpcId_Fire, out fire);
            Check(hasFire && fire.Descriptor.Direction == PMRpcKind.Server,
                "Fire 注册为 Server 方向");
            Check(hasFire && fire.Descriptor.Validator == PMRpcValidator.ForceValidate,
                "Fire 的 Validator 档位来自声明（ForceValidate）");
            Check(hasFire && fire.Descriptor.IsReliable, "Fire 为可靠 RPC");

            PMNetRpcEntry notify;
            bool hasNotify = PMNetRegistry.TryGetRpc(E2eReplicated.PMGeneratedRpcId_Notify, out notify);
            Check(hasNotify && notify.Descriptor.Direction == PMRpcKind.Client,
                "Notify 注册为 Client 方向");

            // RV5/RV6 新增夹具的档位与方向必须真的落进描述符（否则后面的喂包级测试
            // 会变成"测了一个根本不存在的分支"）。
            PMNetRpcEntry push;
            bool hasPush = PMNetRegistry.TryGetRpc(E2eReplicated.PMGeneratedRpcId_Push, out push);
            Check(hasPush && push.Descriptor.Direction == PMRpcKind.Multicast,
                "Push 注册为 Multicast 方向（RV5 数组快照夹具）");

            PMNetRpcEntry checkedRpc;
            bool hasChecked = PMNetRegistry.TryGetRpc(E2eReplicated.PMGeneratedRpcId_Checked, out checkedRpc);
            Check(hasChecked && checkedRpc.Descriptor.Validator == PMRpcValidator.Validate,
                "Checked 的 Validator 档位为原生 Validate（RV6 断连夹具，此前零覆盖）");

            PMNetRpcEntry say;
            bool hasSay = PMNetRegistry.TryGetRpc(E2eReplicated.PMGeneratedRpcId_Say, out say);
            Check(hasSay && say.Descriptor.Direction == PMRpcKind.Client,
                "Say 注册为 Client 方向（RV5 字符串长度门夹具）");

            PMNetRpcEntry warp;
            bool hasWarp = PMNetRegistry.TryGetRpc(E2eReplicated.PMGeneratedRpcId_Warp, out warp);
            Check(hasWarp && warp.Descriptor.Validator == PMRpcValidator.ForceValidate,
                "Warp 的 Validator 档位为 ForceValidate（RV6 三态与非法值夹具）");
        }

        // ------------------------------------------------------------------ 复制替身

        /// <summary>
        /// 简易连接替身：把发出的载荷收集起来，便于"手动投递"（与 B 块门禁同构）。
        ///
        /// 它**同时实现** `IPMNetRpcDisconnectTarget`，因为 RV6 的断连路径需要一条
        /// "真的能把断连请求落到连接上"的宿主连接；R3 的真实实现会把这个接口方法
        /// 直接委托给既有的 `PMNet.Transport.PMTransport.Disconnect(PMDisconnectReason)`。
        /// </summary>
        private sealed class TestConn : PMNetConnection, IPMNetRpcDisconnectTarget
        {
            public readonly List<byte[]> Sent = new List<byte[]>();

            /// <summary>收到的断连请求（RPC 方法名）。</summary>
            public readonly List<string> DisconnectRequests = new List<string>();

            public TestConn(int id, bool serverSide)
                : base(id, serverSide)
            {
            }

            public override bool IsReady { get { return true; } }

            public override void Send(byte[] payload, PMRpcReliability reliability)
            {
                Sent.Add(payload);
            }

            public void RequestRpcDisconnect(PMNetObject target, ushort rpcId, string methodName)
            {
                DisconnectRequests.Add(methodName);
            }
        }

        /// <summary>
        /// 一条**没实现**断连接缝的连接。
        ///
        /// 存在的理由：RV6 要求"校验失败必须真的发出断连请求"；如果连接没有实现接缝，
        /// 入口**不能静默当没事**（H3 的风险：未接线 ⇒ 既不断连也无痕迹）。
        /// 它用来断言不可路由的断连请求会被计数且告警。
        /// </summary>
        private sealed class PlainConn : PMNetConnection
        {
            public PlainConn(int id, bool serverSide)
                : base(id, serverSide)
            {
            }

            public override bool IsReady { get { return true; } }

            public override void Send(byte[] payload, PMRpcReliability reliability)
            {
            }
        }

        /// <summary>带连接的载体：让 `E2eReplicated.GetNetConnection()` 能推导出连接（RPC 归属用）。</summary>
        private sealed class ConnCarrier : PMNetObject
        {
            public PMNetConnection Conn;

            public override PMNetConnection GetNetConnection()
            {
                return Conn;
            }
        }

        private sealed class RigClient
        {
            public PMNetWorld World;
            public TestConn Conn;
            public E2eReplicated Obj;
            public PMReplicationChannel Receiver;
        }

        private sealed class Rig
        {
            public uint ClassId;
            public PMNetWorld ServerWorld;
            public E2eReplicated ServerObj;
            public PMReplicationChannel Sender;
            public readonly List<RigClient> Clients = new List<RigClient>();
        }

        private static Func<PMRepOptions, PMReplicationChannel> _senderFactory;

        /// <summary>
        /// 建一条完整的「服务端权威 + N 条客户端副本」链路。
        /// 关键点：客户端副本**由生命周期消息创建**（而不是手工 new）——
        /// 这样 Create 与初始状态的原子性（D-R0-16）也在链路上。
        /// </summary>
        private static Rig NewRig(int clientCount)
        {
            Rig rig = new Rig();
            rig.ClassId = E2eReplicated.PMGeneratedClassId;
            rig.ServerWorld = new PMNetWorld(new PMSession(0x2Au, true));
            rig.ServerWorld.RegisterClass(rig.ClassId, delegate () { return new E2eReplicated(); });

            rig.ServerObj = new E2eReplicated();
            if (!rig.ServerWorld.Spawn(rig.ServerObj, rig.ClassId))
            {
                throw new InvalidOperationException("服务端 Spawn 失败");
            }

            PMRepOptions opts = new PMRepOptions();
            rig.Sender = _senderFactory == null ? new PMReplicationChannel(opts) : _senderFactory(opts);
            rig.Sender.World = rig.ServerWorld;
            rig.Sender.RegisterObject(rig.ServerObj);
            rig.Sender.RegisterOnRepDispatcher(rig.ClassId, E2eReplicated.E2eDispatchOnRep);

            for (int i = 0; i < clientCount; i++)
            {
                AddClient(rig);
            }

            return rig;
        }

        private static RigClient AddClient(Rig rig)
        {
            RigClient c = new RigClient();
            c.World = new PMNetWorld(new PMSession(0x2Au, false));
            c.World.RegisterClass(rig.ClassId, delegate () { return new E2eReplicated(); });
            c.Conn = new TestConn(700 + rig.Clients.Count, true);

            rig.ServerWorld.AddConnection(c.Conn);
            rig.Sender.AddConnection(c.Conn);

            byte[] lifecycle = rig.ServerWorld.BuildLifecycleBatch(c.Conn);
            if (lifecycle != null)
            {
                c.World.OnLifecycleMessage(lifecycle, 0, lifecycle.Length);
            }

            PMNetObject o;
            if (!c.World.TryFind(rig.ServerObj.NetId, out o) || o == null)
            {
                throw new InvalidOperationException("客户端世界没有创建出副本对象（生命周期路径有问题）");
            }

            c.Obj = (E2eReplicated)o;
            c.Receiver = new PMReplicationChannel();
            c.Receiver.World = c.World;
            c.Receiver.RegisterOnRepDispatcher(rig.ClassId, E2eReplicated.E2eDispatchOnRep);

            rig.Clients.Add(c);
            return c;
        }

        /// <summary>服务端组装一轮并"发出"（填入各连接的待投递队列，尚未被客户端应用）。</summary>
        private static void SendAll(Rig rig)
        {
            _nRepPayloads += rig.Sender.Tick();
        }

        /// <summary>
        /// 把某条连接"待发"的载荷投递给它的客户端副本，并把客户端产生的确认回灌给发送侧。
        ///
        /// 投递时传的连接对象是**服务端视角的那一条** ——
        /// 真实形态下客户端的上行确认同样落在服务端那条连接上被处理。
        /// </summary>
        private static int Deliver(Rig rig, RigClient c)
        {
            int applied = 0;
            List<byte[]> sent = c.Conn.Sent;
            for (int i = 0; i < sent.Count; i++)
            {
                applied += c.Receiver.OnMessage(c.Conn, sent[i], 0, sent[i].Length);
                _nRepRecords++;
            }

            sent.Clear();

            byte[] ack = c.Receiver.BuildAckMessage(c.Conn);
            if (ack != null)
            {
                rig.Sender.OnMessage(c.Conn, ack, 0, ack.Length);
            }

            return applied;
        }

        private static void SendAndDeliverAll(Rig rig)
        {
            SendAll(rig);
            for (int i = 0; i < rig.Clients.Count; i++)
            {
                Deliver(rig, rig.Clients[i]);
            }
        }

        private static PMRepMessage Decode(byte[] payload, string context)
        {
            PMRepMessage msg = new PMRepMessage();
            string error;
            if (!PMReplicationReader.TryRead(new PMNetReader(payload), msg, out error))
            {
                _failures.Add(context + "：解码载荷失败 " + error);
                return null;
            }

            return msg;
        }

        /// <summary>某连接的待发载荷里出现过的全部槽位（去重、升序）。</summary>
        private static List<int> PendingSlots(TestConn conn, string context)
        {
            List<int> all = new List<int>();
            for (int i = 0; i < conn.Sent.Count; i++)
            {
                PMRepMessage msg = Decode(conn.Sent[i], context);
                if (msg == null)
                {
                    continue;
                }

                for (int u = 0; u < msg.Updates.Count; u++)
                {
                    int[] slots = msg.Updates[u].Slots;
                    if (slots == null)
                    {
                        continue;
                    }

                    for (int s = 0; s < slots.Length; s++)
                    {
                        if (!all.Contains(slots[s]))
                        {
                            all.Add(slots[s]);
                        }
                    }
                }
            }

            all.Sort();
            return all;
        }

        private static string SlotsToText(List<int> slots)
        {
            if (slots == null || slots.Count == 0)
            {
                return "[]";
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder("[");
            for (int i = 0; i < slots.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append(slots[i]);
            }

            return sb.Append(']').ToString();
        }

        // ------------------------------------------------------------------ [2] 复制收敛（T41）

        /// <summary>槽位下标由生成描述符决定：0=_title，1=_speed，2=_ammo(OwnerOnly)，3=_health。</summary>
        private const int SlotTitle = 0;
        private const int SlotSpeed = 1;
        private const int SlotAmmo = 2;
        private const int SlotHealth = 3;

        private static void TestReplication()
        {
            _senderFactory = null;
            TestDifferentBaselinesConverge();
            TestStaleAckKeepsNewDirty();
            TestUnchangedSuppressed();
            TestConditionTransition();
            TestOnRepReallyDispatched();
        }

        /// <summary>T41 主用例：两条连接基线不同，最终都收敛到权威值。</summary>
        private static void TestDifferentBaselinesConverge()
        {
            Rig rig = NewRig(2);
            RigClient a = rig.Clients[0];
            RigClient b = rig.Clients[1];

            // 只在一条连接上推进五轮：另一条的基线落后多版。
            for (int i = 1; i <= 5; i++)
            {
                rig.ServerObj.PMNet_Set_health(i * 10);
                rig.ServerObj.PMNet_Set_speed(i * 1.5f);
                rig.ServerObj.PMNet_Set_title("v" + i);
                SendAll(rig);
                Deliver(rig, a);
            }

            Check(a.Obj.ReadHealth() == 50 && a.Obj.ReadTitle() == "v5", "J1 快连接已追到最新（health=50/title=v5）");
            Check(b.Obj.ReadHealth() != a.Obj.ReadHealth(),
                "J2 慢连接确实落后（health=" + b.Obj.ReadHealth() + " vs " + a.Obj.ReadHealth() + "）"
                + " —— 这条断言保证「不同基线」这一前提真的成立，而不是两条连接恰好同值");
            Check(b.Conn.Sent.Count > 0, "J3 慢连接的落后更新仍在待投递队列里（" + b.Conn.Sent.Count + " 个载荷）");

            Deliver(rig, b);

            Check(b.Obj.ReadHealth() == 50 && b.Obj.ReadTitle() == "v5",
                "J4 慢连接最终收敛（health=" + b.Obj.ReadHealth() + "/title=" + b.Obj.ReadTitle() + "）");
            Check(a.Obj.ReadHealth() == b.Obj.ReadHealth()
                  && Math.Abs(a.Obj.ReadSpeed() - b.Obj.ReadSpeed()) < 1e-6f
                  && a.Obj.ReadTitle() == b.Obj.ReadTitle(),
                "J5 两条连接最终一致（int/float/string 三类都一致）");
            Check(a.Obj.ReadHealth() == rig.ServerObj.ReadHealth()
                  && a.Obj.ReadTitle() == rig.ServerObj.ReadTitle(),
                "J6 与权威值一致");
        }

        /// <summary>R0 §5：ACK 只能前进，旧 ACK 不得清除新脏位。</summary>
        private static void TestStaleAckKeepsNewDirty()
        {
            Rig rig = NewRig(1);
            RigClient c = rig.Clients[0];
            uint netId = rig.ServerObj.NetId.Value;

            // 先把基线钉住（v0）。
            rig.ServerObj.PMNet_Set_health(1);
            SendAndDeliverAll(rig);
            Check(c.Obj.ReadHealth() == 1, "K1 基线已建立（health=1）");

            // v1：值 100，发出但**不投递**（保持"在途"）。
            rig.ServerObj.PMNet_Set_health(100);
            SendAll(rig);
            long[] inflight1 = rig.Sender.GetInflightVersions(c.Conn, netId);
            Check(inflight1.Length >= 1, "K2 v1 仍在途（" + inflight1.Length + " 条）");
            long v1 = inflight1[inflight1.Length - 1];

            // v2：值 200，覆盖同一条在途链。
            rig.ServerObj.PMNet_Set_health(200);
            SendAll(rig);

            // ★ 旧 ACK（v1）到达：它只能确认 v1 那一版的值，不能碰 v2 的脏位。
            rig.Sender.OnAck(c.Conn, netId, v1);

            long acked;
            bool hasAcked = rig.Sender.TryGetAckedVersion(c.Conn, netId, out acked);
            Check(hasAcked && acked == v1, "K3 ACK 推进到 v1=" + v1 + "（实际 " + (hasAcked ? acked.ToString() : "<无>") + "）");

            byte[] baseline;
            bool hasBaseline = rig.Sender.TryGetBaselineValue(c.Conn, netId, SlotHealth, out baseline);
            int baselineHealth = hasBaseline ? DecodeInt32(baseline) : -1;
            Check(hasBaseline && baselineHealth == 100,
                "K4 基线被推进到 v1 的**值**（100），而不是当前值（200），实际 " + baselineHealth);

            Check(rig.ServerObj.Dirty.IsDirty(SlotHealth),
                "K5 旧 ACK 之后新脏位仍在（health 槽位仍为脏）—— R0 §5「旧 ACK 不得清除新脏位」");

            // 旧 ACK 不能把新的值当成已同步：下一轮必须还带得上 health。
            c.Conn.Sent.Clear();
            SendAll(rig);
            List<int> slots = PendingSlots(c.Conn, "K6");
            Check(slots.Contains(SlotHealth),
                "K6 旧 ACK 之后下一轮仍补发 health，实际槽位 " + SlotsToText(slots));

            Deliver(rig, c);
            Check(c.Obj.ReadHealth() == 200, "K7 客户端最终拿到 200（实际 " + c.Obj.ReadHealth() + "）");

            long staleBefore = rig.Sender.Stats.StaleAckIgnored;
            rig.Sender.OnAck(c.Conn, netId, v1);
            Check(rig.Sender.Stats.StaleAckIgnored > staleBefore,
                "K8 重复的旧 ACK 被判为过期（StaleAckIgnored " + staleBefore + " → "
                + rig.Sender.Stats.StaleAckIgnored + "）");
            rig.Sender.TryGetAckedVersion(c.Conn, netId, out acked);
            Check(acked == rig.Sender.Stats.AcksReceived || acked > v1,
                "K9 过期 ACK 没有把版本号回退（当前 " + acked + "）");
        }

        private static int DecodeInt32(byte[] value)
        {
            if (value == null)
            {
                return int.MinValue;
            }

            return new PMNetReader(value).ReadInt32();
        }

        /// <summary>D-R0-13：标脏只说明"可能变了"，值未变则掩码为空、不发数据。</summary>
        private static void TestUnchangedSuppressed()
        {
            Rig rig = NewRig(1);
            RigClient c = rig.Clients[0];

            rig.ServerObj.PMNet_Set_health(42);
            SendAndDeliverAll(rig);
            Check(c.Obj.ReadHealth() == 42, "L1 基线已建立（health=42）");

            // 情形一：只是标脏，值根本没动。
            rig.ServerObj.MarkPropertyDirty(SlotHealth);
            c.Conn.Sent.Clear();
            long before = rig.Sender.Stats.SuppressedUnchanged;
            SendAll(rig);
            Check(c.Conn.Sent.Count == 0,
                "L2 值未变（仅标脏）⇒ 无载荷，实际发了 " + c.Conn.Sent.Count + " 个");
            Check(rig.Sender.Stats.SuppressedUnchanged > before,
                "L3 被抑制的计数确实增长（SuppressedUnchanged " + before + " → "
                + rig.Sender.Stats.SuppressedUnchanged + "）");

            // 情形二：写回同一个值（走生成物的 PMNet_Set_*，因此一定标了脏）。
            rig.ServerObj.PMNet_Set_health(42);
            c.Conn.Sent.Clear();
            SendAll(rig);
            Check(c.Conn.Sent.Count == 0,
                "L4 写回同值（走生成物 Set_health）⇒ 仍无载荷，实际发了 " + c.Conn.Sent.Count + " 个");

            // 反向对照：真的改了值就一定发得出去（否则 L2/L4 可能只是"整体不发"）。
            c.Conn.Sent.Clear();
            rig.ServerObj.PMNet_Set_health(43);
            SendAll(rig);
            Check(c.Conn.Sent.Count > 0, "L5 真的改值 ⇒ 一定有载荷（对照，实际 "
                  + c.Conn.Sent.Count + " 个）");
            Deliver(rig, c);
            Check(c.Obj.ReadHealth() == 43, "L6 客户端收到新值 43");
        }

        /// <summary>D-R0-15：条件由"不满足 → 满足"时必须补发当前值。</summary>
        private static void TestConditionTransition()
        {
            Rig rig = NewRig(1);
            RigClient c = rig.Clients[0];

            rig.ServerObj.PMNet_Set_title("ready");
            SendAndDeliverAll(rig);

            // 非 Owner 连接：OwnerOnly 的 _ammo 不参与复制。
            rig.ServerObj.PMNet_Set_ammo(5);
            c.Conn.Sent.Clear();
            SendAll(rig);
            List<int> slots = PendingSlots(c.Conn, "M1");
            Check(!slots.Contains(SlotAmmo),
                "M1 非 Owner 连接上 OwnerOnly 属性被条件过滤，实际槽位 " + SlotsToText(slots));
            Deliver(rig, c);
            Check(c.Obj.ReadAmmo() == 0, "M2 客户端没拿到 _ammo（实际 " + c.Obj.ReadAmmo() + "）");

            // 条件由不满足变满足：这一版连接成为 Owner。
            rig.ServerObj.OwnerConnection = c.Conn;
            c.Conn.Sent.Clear();
            long before = rig.Sender.Stats.TransitionForceSends;
            SendAll(rig);
            List<int> after = PendingSlots(c.Conn, "M3");
            Check(after.Contains(SlotAmmo),
                "M3 条件跃迁后补发 _ammo（值未发生新变化也补发），实际槽位 " + SlotsToText(after));
            Check(rig.Sender.Stats.TransitionForceSends > before,
                "M4 跃迁补发计数增长（" + before + " → " + rig.Sender.Stats.TransitionForceSends + "）");
            Deliver(rig, c);
            Check(c.Obj.ReadAmmo() == 5, "M5 客户端拿到补发的当前值 5（实际 " + c.Obj.ReadAmmo() + "）");
        }

        /// <summary>OnRep 必须真的通过**生成物的分发表**被调用（而不是只写进描述符）。</summary>
        private static void TestOnRepReallyDispatched()
        {
            Rig rig = NewRig(1);
            RigClient c = rig.Clients[0];

            rig.ServerObj.PMNet_Set_health(1);
            SendAndDeliverAll(rig);

            int notifyBefore = c.Obj.HealthNotifyCount;
            long dispatchedBefore = c.Receiver.Stats.OnRepDispatched;

            rig.ServerObj.PMNet_Set_health(888);
            SendAndDeliverAll(rig);

            Check(c.Obj.HealthNotifyCount == notifyBefore + 1,
                "N1 属性变化后 RepNotify 正好被调用一次（" + notifyBefore + " → " + c.Obj.HealthNotifyCount + "）");
            Check(c.Receiver.Stats.OnRepDispatched > dispatchedBefore,
                "N2 复制层的 OnRep 分发计数增长（" + dispatchedBefore + " → "
                + c.Receiver.Stats.OnRepDispatched + "）");
            Check(c.Obj.ReadHealth() == 888, "N3 客户端值已更新（" + c.Obj.ReadHealth() + "）");
            _nOnRep += c.Obj.HealthNotifyCount;
        }

        // ------------------------------------------------------------------ [2b] Create 初值（D-R0-16）

        /// <summary>
        /// 真实生成物 + 真实世界路径下的 Create 验收（D-R0-16：创建与初始状态同包到达）。
        ///
        /// 三条判据缺一条这条验收就不成立：
        /// 1. 服务端先把四个成员赋成**非默认值**（其中 `_ammo` 是 OwnerOnly），
        ///    然后在**首次 Tick 之前**用 `BuildLifecycleBatch` 构包 ——
        ///    因此接收侧读到的值只可能来自 Create 记录里的声明式初值；
        /// 2. 客户端在 `OnReplicatedCreate` 里读值（不是等更新过来再读），
        ///    读到的必须已经是初值；
        /// 3. 非拥有者连接的 Create 记录里**不得**出现 OwnerOnly 的 `_ammo` 槽位
        ///    （初值的条件过滤是权限边界，不是带宽优化）。
        /// </summary>
        private static void TestCreateInitialState()
        {
            uint classId = E2eReplicated.PMGeneratedClassId;

            PMNetWorld server = new PMNetWorld(new PMSession(0x2Du, true));
            server.RegisterClass(classId, delegate () { return new E2eReplicated(); });

            E2eReplicated serverObj = new E2eReplicated();
            Check(server.Spawn(serverObj, classId), "Y0 服务端 Spawn 成功（Create 记录的来源就绪）");
            if (serverObj.State != PMNetObjectState.Active)
            {
                return;
            }

            // 非默认初值：三个无条件成员 + 一个 OwnerOnly 成员。
            serverObj.PMNet_Set_health(1234);
            serverObj.PMNet_Set_speed(7.25f);
            serverObj.PMNet_Set_title("create-title");
            serverObj.PMNet_Set_ammo(88);

            PMReplicationChannel sender = new PMReplicationChannel(new PMRepOptions());
            sender.World = server;
            sender.RegisterObject(serverObj);
            sender.RegisterOnRepDispatcher(classId, E2eReplicated.E2eDispatchOnRep);

            TestConn ownerConn = new TestConn(940, true);
            TestConn otherConn = new TestConn(941, true);
            server.AddConnection(ownerConn);
            server.AddConnection(otherConn);
            sender.AddConnection(ownerConn);
            sender.AddConnection(otherConn);

            // 只有 ownerConn 拥有该对象 —— OwnerOnly 条件的唯一输入。
            serverObj.OwnerConnection = ownerConn;

            // ★ 首次 Tick 之前构包：Create 必须自带初值。
            byte[] ownerBatch = server.BuildLifecycleBatch(ownerConn);
            byte[] otherBatch = server.BuildLifecycleBatch(otherConn);

            Check(ownerBatch != null && ownerBatch.Length > 0,
                "Y1 拥有者连接的 Create 批量消息已产生（" + (ownerBatch == null ? 0 : ownerBatch.Length) + " 字节）");
            Check(otherBatch != null && otherBatch.Length > 0,
                "Y2 非拥有者连接的 Create 批量消息已产生（" + (otherBatch == null ? 0 : otherBatch.Length) + " 字节）");
            Check(ownerConn.Sent.Count == 0 && otherConn.Sent.Count == 0,
                "Y3 ★本段全程没有调用复制层 Tick()：初值不可能来自后续 Update（两条连接的发送列表都为空）");

            string err;
            List<PMNetLifecycleRecord> ownerRecs = new List<PMNetLifecycleRecord>();
            Check(PMLifecycleCodec.TryRead(ownerBatch, 0, ownerBatch.Length, ownerRecs, out err) && ownerRecs.Count == 1,
                "Y4 拥有者的 Create 批量消息可解析且恰好一条记录" + (err == null ? "" : "（" + err + "）"));

            if (ownerRecs.Count != 1)
            {
                return;
            }

            PMNetLifecycleRecord ownerRec = ownerRecs[0];
            Check(ownerRec.Kind == PMObjectEventKind.Create
                  && ownerRec.RepInitialState != null && ownerRec.RepInitialState.Length > 0,
                "Y5 ★Create 记录自带声明式初值（"
                + (ownerRec.RepInitialState == null ? 0 : ownerRec.RepInitialState.Length) + " 字节）—— 不靠后续 Update 补齐");

            string slotErr;
            List<int> ownerSlots = DecodeInitialStateSlots(ownerRec.RepInitialState, out slotErr);
            Check(slotErr == null, "Y6 拥有者的声明式初值可解出槽位表" + (slotErr == null ? "" : "：" + slotErr));
            Check(ownerSlots.Count == 4 && ownerSlots.Contains(SlotTitle) && ownerSlots.Contains(SlotSpeed)
                  && ownerSlots.Contains(SlotAmmo) && ownerSlots.Contains(SlotHealth),
                "Y7 ★拥有者拿到全部四个槽位（含 OwnerOnly 的 _ammo）：" + SlotsToText(ownerSlots));

            List<PMNetLifecycleRecord> otherRecs = new List<PMNetLifecycleRecord>();
            Check(PMLifecycleCodec.TryRead(otherBatch, 0, otherBatch.Length, otherRecs, out err) && otherRecs.Count == 1,
                "Y8 非拥有者的 Create 批量消息同样可解析");

            if (otherRecs.Count != 1)
            {
                return;
            }

            List<int> otherSlots = DecodeInitialStateSlots(otherRecs[0].RepInitialState, out slotErr);
            Check(!otherSlots.Contains(SlotAmmo),
                "Y9 ★非拥有者的 Create 初值里没有 OwnerOnly 的 _ammo 槽位（" + SlotsToText(otherSlots) + "）");
            Check(otherSlots.Count == 3 && otherSlots.Contains(SlotTitle) && otherSlots.Contains(SlotSpeed)
                  && otherSlots.Contains(SlotHealth),
                "Y10 非拥有者仍然拿到三个无条件槽位（排除是定向的，不是整体漏发）");

            // ── 接收侧：创建回调里读值 ────────────────────────────────────────────
            PMNetWorld ownerWorld = new PMNetWorld(new PMSession(0x2Du, false));
            ownerWorld.RegisterClass(classId, delegate () { return new E2eReplicated(); });
            ownerWorld.OnLifecycleMessage(ownerBatch, 0, ownerBatch.Length);

            PMNetObject found;
            Check(ownerWorld.TryFind(serverObj.NetId, out found) && found != null,
                "Y11 拥有者客户端世界真的创建出了副本对象");
            E2eReplicated ownerCopy = found as E2eReplicated;
            Check(ownerCopy != null, "Y12 副本是夹具声明的类型 E2eReplicated");
            if (ownerCopy == null)
            {
                return;
            }

            Check(ownerCopy.CreateCallbackCount == 1,
                "Y13 ★创建回调恰好发生一次（实际 " + ownerCopy.CreateCallbackCount + "）");
            Check(ownerCopy.CreateHealthSeen == 1234 && Math.Abs(ownerCopy.CreateSpeedSeen - 7.25f) < 1e-6f
                  && ownerCopy.CreateTitleSeen == "create-title" && ownerCopy.CreateAmmoSeen == 88,
                "Y14 ★创建回调里读到的四个成员全部是初值：health=" + ownerCopy.CreateHealthSeen
                + " speed=" + ownerCopy.CreateSpeedSeen + " title=" + (ownerCopy.CreateTitleSeen ?? "<null>")
                + " ammo=" + ownerCopy.CreateAmmoSeen);
            Check(ownerCopy.HealthNotifyCount == 0,
                "Y15 初值不走增量路径（本次创建没有派生 OnRep 分发，OnRep 计数仍为 "
                + ownerCopy.HealthNotifyCount + "）");
            _nCreateCallbacks += ownerCopy.CreateCallbackCount;

            PMNetWorld otherWorld = new PMNetWorld(new PMSession(0x2Du, false));
            otherWorld.RegisterClass(classId, delegate () { return new E2eReplicated(); });
            otherWorld.OnLifecycleMessage(otherBatch, 0, otherBatch.Length);

            PMNetObject otherFound;
            otherWorld.TryFind(serverObj.NetId, out otherFound);
            E2eReplicated otherCopy = otherFound as E2eReplicated;
            Check(otherCopy != null && otherCopy.CreateCallbackCount == 1,
                "Y16 非拥有者客户端同样收到 Create 并触发创建回调");
            if (otherCopy == null)
            {
                return;
            }

            Check(otherCopy.CreateHealthSeen == 1234 && Math.Abs(otherCopy.CreateSpeedSeen - 7.25f) < 1e-6f
                  && otherCopy.CreateTitleSeen == "create-title",
                "Y17 非拥有者的三个无条件初值与拥有者一致（health=" + otherCopy.CreateHealthSeen
                + " speed=" + otherCopy.CreateSpeedSeen + " title=" + (otherCopy.CreateTitleSeen ?? "<null>") + "）");
            Check(otherCopy.CreateAmmoSeen == 0,
                "Y18 ★非拥有者的创建回调里 _ammo 仍是默认值 0（服务端设的是 88 ⇒ 该值确实被条件过滤掉，而不是漏发整体）");
            _nCreateCallbacks += otherCopy.CreateCallbackCount;
        }

        /// <summary>
        /// 解出声明式初值里的槽位列表。线格式：`( varint(slot) | varint(propertyId) | varint(valueLen) | value )*`。
        /// 门禁用它证明"某个槽位真的不在初值里"（而不是只断言业务侧读到默认值）。
        /// </summary>
        private static List<int> DecodeInitialStateSlots(byte[] repState, out string error)
        {
            List<int> slots = new List<int>();
            error = null;

            if (repState == null || repState.Length == 0)
            {
                return slots;
            }

            try
            {
                PMNetReader r = new PMNetReader(repState);
                while (!r.IsAtEnd)
                {
                    int slot = checked((int)r.ReadVarint());
                    int propertyId = checked((int)r.ReadVarint());
                    int len = checked((int)r.ReadVarint());
                    r.ReadRawBytesCopy(len);

                    if (propertyId == 0)
                    {
                        error = "槽位 " + slot + " 的属性 ID 为 0（无效）";
                        slots.Clear();
                        return slots;
                    }

                    slots.Add(slot);
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + " " + ex.Message;
                slots.Clear();
            }

            return slots;
        }

        // ------------------------------------------------------------------ [3] RPC 双向（T40）

        private static void TestRpc()
        {
            TestCallStubEnqueuesWhenRemote();
            TestArgClosureNotOverwritten();
            TestArrayArgCallTimeSnapshot();
            TestPreflightLimitsBeforeEnqueue();
            TestReceiveSideValidationRouting();
            TestOwnershipCheck();
            TestReceiveEntryPermissions();
            TestReceiveEntryValidationRouting();
            TestReceiveEntryMalformedPayload();
        }

        /// <summary>造一个「客户端视角、有连接」的对象，使 Server RPC 的 callspace 判定为 Remote。</summary>
        private static E2eReplicated MakeRemoteSideObject(out TestConn conn)
        {
            conn = new TestConn(900, false);
            ConnCarrier carrier = new ConnCarrier();
            carrier.Conn = conn;

            E2eReplicated obj = new E2eReplicated();
            obj.Owner = carrier;
            obj.NetMode = PMNetMode.Client;
            obj.Role = PMNetRole.AutonomousProxy;
            return obj;
        }

        private static void TestCallStubEnqueuesWhenRemote()
        {
            TestConn conn;
            E2eReplicated obj = MakeRemoteSideObject(out conn);

            PMFunctionCallspace cs = PMRpcDispatch.EvaluateCallspace(
                obj.NetMode, obj.Role, PMRpcKind.Server, obj.GetNetConnection() != null, false);
            Check(PMRpcDispatch.ShouldSendRemote(cs),
                "O1 客户端侧对象调 Server RPC 的 callspace 判定为 Remote（" + cs + "）");

            PMNet.Generated.PMNetGeneratedRegistry.ClearPendingRpcs();
            obj.Fire(3, 1.25f);
            Check(PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount == 1,
                "O2 调用桩把该次调用放进了待发队列（PendingRpcCount="
                + PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount + "）");
            _nRpcQueued += 1;

            // O2b 待发队列必须有界（D-R0-18），但**不得丢最旧**（RV5）。
            //
            // 判据来自 D-R0-06/D-R0-07：可靠 RPC 是「保序且要求送达」，可靠缓冲溢出等价于断连。
            // 因此这条队列（RemoteSender 未接线时的开发/测试形态）在超限时必须
            // **显式失败、不接受新调用**，并原样保留已接受的调用与它们的顺序。
            {
                PMNet.Generated.PMNetGeneratedRegistry.ClearPendingRpcs();
                long rejectedBefore = PMNet.Generated.PMNetGeneratedRegistry.RejectedPendingRpcs;
                int max = PMNet.Generated.PMNetGeneratedRegistry.MaxPendingRpcs;

                for (int t = 0; t < max; t++)
                {
                    obj.Fire(t, 0.5f);
                }

                Check(PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount == max,
                    "O2b 待发队列有界：压入 " + max + " 条后长度为 " + max);

                // 第 max+1 条必须**显式失败**（抛异常），而不是悄悄挤掉第 0 条。
                bool overflowThrew = false;
                try
                {
                    obj.Fire(999999, 0.5f);
                }
                catch (InvalidOperationException)
                {
                    overflowThrew = true;
                }

                Check(overflowThrew, "O2b 超限时显式失败（抛 InvalidOperationException，不接受新调用）");
                Check(PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount == max,
                    "O2b 被拒的那次调用没有改变队列长度（仍为 " + max + "）");

                long rejected = PMNet.Generated.PMNetGeneratedRegistry.RejectedPendingRpcs - rejectedBefore;
                Check(rejected == 1, "O2b 被拒的调用被计数而非静默丢弃（实际计数 " + rejected + "）");

                // 顺序对照：队首必须仍是**第一条被接受的调用**（实参 0）—— 即"保留原顺序、不淘汰"。
                PMNet.Generated.PMNetPendingRpc head;
                Check(PMNet.Generated.PMNetGeneratedRegistry.TryDequeueRpc(out head),
                    "O2b 有界之后仍可正常出队");

                PMNetRpcEntry headEntry;
                if (PMNetRegistry.TryGetRpc(head.RpcId, out headEntry) && headEntry != null)
                {
                    PMNetWriter headWriter = new PMNetWriter(32);
                    head.Write(head.Target, headWriter);
                    E2eReplicated headReceiver = new E2eReplicated();
                    headEntry.Invoke(headReceiver, new PMNetReader(headWriter.ToArray()));
                    Check(headReceiver.LastFireTargetId == 0,
                        "O2b 队首仍是最早被接受的调用（实参 0，实际 " + headReceiver.LastFireTargetId
                        + "）—— 没有丢最旧");
                    _nRpcInvoked += 1;
                }
                else
                {
                    Check(false, "O2b 取不到 Fire 的接收分发");
                }

                PMNet.Generated.PMNetGeneratedRegistry.ClearPendingRpcs();
                _nRpcQueued += max + 1;
            }

            // 反向对照：服务端侧（Authority）调 Server RPC 是本地执行、不入队。
            PMNet.Generated.PMNetGeneratedRegistry.ClearPendingRpcs();
            E2eReplicated serverSide = new E2eReplicated();
            serverSide.NetMode = PMNetMode.DedicatedServer;
            serverSide.Role = PMNetRole.Authority;
            serverSide.Fire(4, 2.5f);
            Check(PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount == 0,
                "O3 服务端侧调 Server RPC 不入队（本地执行），实际 "
                + PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount);
            Check(serverSide.FireCount == 1, "O4 服务端侧调用确实本地执行了实现（FireCount="
                  + serverSide.FireCount + "）");
            _nRpcInvoked += serverSide.FireCount;

            // 方向契约 ①：服务端侧**无主**对象（无连接、无 owning player）调 Client RPC
            // ⇒ 本地执行、不外发。这是 UE 分支 #14-① 的合法路径（AI 拥有的对象调 Client RPC），不是缺陷。
            PMNet.Generated.PMNetGeneratedRegistry.ClearPendingRpcs();
            serverSide.Notify(9);
            Check(PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount == 0,
                "O5 服务端侧无主对象调 Client RPC 本地执行、不外发（无收件人），实际入队 "
                + PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount);
            Check(serverSide.NotifyCount == 1, "O6 上述调用确实本地执行了实现（NotifyCount="
                  + serverSide.NotifyCount + "）");
            _nRpcInvoked += serverSide.NotifyCount;

            // 方向契约 ②：服务端侧**有**连接的对象调 Client RPC ⇒ Remote（发给拥有者）。
            TestConn carrierConn = new TestConn(901, true);
            ConnCarrier carrier = new ConnCarrier();
            carrier.Conn = carrierConn;
            serverSide.Owner = carrier;
            PMNet.Generated.PMNetGeneratedRegistry.ClearPendingRpcs();
            serverSide.Notify(10);
            Check(PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount == 1,
                "O7 服务端侧有连接的对象调 Client RPC 进待发队列（方向位生效），实际 "
                + PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount);
            _nRpcQueued += 1;
            PMNet.Generated.PMNetGeneratedRegistry.ClearPendingRpcs();
        }

        /// <summary>
        /// 契约 §4.3.1 的核心不变性：**实参随调用走**。
        ///
        /// 取出待发项**之后**再调一次同样的 RPC（不同实参），然后才执行第一次的编码 ——
        /// 第一次的实参不得被覆盖。这正是"对象上的实参帧"返工要解决的问题（§5.5 缺陷 1）。
        /// </summary>
        private static void TestArgClosureNotOverwritten()
        {
            PMNet.Generated.PMNetGeneratedRegistry.ClearPendingRpcs();

            TestConn conn;
            E2eReplicated sender = MakeRemoteSideObject(out conn);

            // 第一次调用（实参 11 / 1.5），随后把它从队列里取出来"挂住"。
            sender.Fire(11, 1.5f);
            PMNet.Generated.PMNetPendingRpc pending1;
            bool taken = PMNet.Generated.PMNetGeneratedRegistry.TryDequeueRpc(out pending1);
            Check(taken, "P1 第一次调用可从待发队列取出");
            _nRpcQueued += 1;

            // 第二次调用（实参 99 / 9.5）—— 在第一次的编码**之前**发生。
            sender.Fire(99, 9.5f);
            _nRpcQueued += 1;

            // 现在才执行第一次的编码。
            PMNetWriter w = new PMNetWriter(32);
            pending1.Write(pending1.Target, w);
            byte[] bytes = w.ToArray();
            Check(bytes.Length > 0, "P2 第一次调用编码出非空字节（" + bytes.Length + " 字节）");

            // 喂给接收侧（走生成物的接收分发），看它读回来的实参是哪一次。
            E2eReplicated receiver = new E2eReplicated();
            PMNetRpcEntry entry;
            Check(PMNetRegistry.TryGetRpc(pending1.RpcId, out entry), "P3 按 RpcId 取到生成物注册的接收分发");

            _nRpcInvoked++;
            entry.Invoke(receiver, new PMNetReader(bytes));

            Check(receiver.LastFireTargetId == 11 && Math.Abs(receiver.LastFireAngle - 1.5f) < 1e-6f,
                "P4 ★核心不变性：第一次的实参未被后续调用覆盖（收到 "
                + receiver.LastFireTargetId + " / " + receiver.LastFireAngle + "，期望 11 / 1.5）");

            // 第二条待发项应当仍然是第二次的实参（各自独立）。
            PMNet.Generated.PMNetPendingRpc pending2;
            bool taken2 = PMNet.Generated.PMNetGeneratedRegistry.TryDequeueRpc(out pending2);
            Check(taken2, "P5 第二次调用仍在队列里");
            if (taken2)
            {
                PMNetWriter w2 = new PMNetWriter(32);
                pending2.Write(pending2.Target, w2);
                E2eReplicated receiver2 = new E2eReplicated();
                _nRpcInvoked++;
                entry.Invoke(receiver2, new PMNetReader(w2.ToArray()));
                Check(receiver2.LastFireTargetId == 99 && Math.Abs(receiver2.LastFireAngle - 9.5f) < 1e-6f,
                    "P6 第二次调用的实参也正确（收到 " + receiver2.LastFireTargetId + " / "
                    + receiver2.LastFireAngle + "）");
            }

            PMNet.Generated.PMNetGeneratedRegistry.ClearPendingRpcs();
        }

        /// <summary>契约 §4.3.2：三态路由的动作差异是契约（Reject 跳过实现、Report 上报后仍执行）。</summary>
        private static void TestReceiveSideValidationRouting()
        {
            List<string> reported = new List<string>();
            List<string> validateFailed = new List<string>();

            Action<PMNetObject, ushort, PMRpcValidation, string> onReported =
                delegate (PMNetObject t, ushort id, PMRpcValidation verdict, string name)
                {
                    reported.Add(name + ":" + verdict);
                };
            Action<PMNetObject, ushort, string> onValidateFailed =
                delegate (PMNetObject t, ushort id, string name) { validateFailed.Add(name); };

            PMRpcValidationSink.OnReported = onReported;
            PMRpcValidationSink.OnValidateFailed = onValidateFailed;

            try
            {
                E2eReplicated r = new E2eReplicated();

                // --- Accept：静默放行 ---
                reported.Clear();
                validateFailed.Clear();
                FireInto(r, 3, 1.0f);
                Check(r.FireCount == 1, "Q1 Accept ⇒ 实现被执行一次（FireCount=" + r.FireCount + "）");
                Check(reported.Count == 0, "Q2 Accept ⇒ 不产生任何上报（实际 " + reported.Count + "）");
                _nRpcInvoked += r.FireCount;

                // --- Reject：上报后跳过实现、**不断连** ---
                reported.Clear();
                validateFailed.Clear();
                FireInto(r, -1, 1.0f);
                Check(r.FireCount == 1, "Q3 Reject ⇒ 实现**未**再执行（FireCount 仍为 " + r.FireCount + "）");
                Check(reported.Count == 1 && reported[0] == "Fire:Reject",
                    "Q4 Reject ⇒ 上报 Reject（实际 " + (reported.Count > 0 ? reported[0] : "<无>") + "）");
                Check(validateFailed.Count == 0,
                    "Q5 Reject 走的是三态上报入口，没有请求断连（OnValidateFailed 未被调用）");
                _nValidationSink += reported.Count + validateFailed.Count;

                // --- Report：上报**且**实现仍执行 ---
                reported.Clear();
                validateFailed.Clear();
                int before = r.FireCount;
                FireInto(r, 7, 2.0f);
                Check(r.FireCount == before + 1,
                    "Q6 Report ⇒ 实现仍然执行（FireCount " + before + " → " + r.FireCount + "）");
                Check(reported.Count == 1 && reported[0] == "Fire:Report",
                    "Q7 Report ⇒ 上报 Report（实际 " + (reported.Count > 0 ? reported[0] : "<无>") + "）");
                Check(validateFailed.Count == 0, "Q8 Report 也不请求断连");
                _nRpcInvoked += 1;
                _nValidationSink += reported.Count + validateFailed.Count;

                // 参数正确性顺带钉一下：上报路径不会把实参读歪。
                Check(r.LastFireTargetId == 7 && Math.Abs(r.LastFireAngle - 2.0f) < 1e-6f,
                    "Q9 Report 路径上实参读取正确（" + r.LastFireTargetId + " / " + r.LastFireAngle + "）");
            }
            finally
            {
                PMRpcValidationSink.OnReported = null;
                PMRpcValidationSink.OnValidateFailed = null;
            }
        }

        private static void FireInto(E2eReplicated target, int targetId, float angle)
        {
            PMNetRpcEntry entry;
            if (!PMNetRegistry.TryGetRpc(E2eReplicated.PMGeneratedRpcId_Fire, out entry))
            {
                throw new InvalidOperationException("Fire 未注册");
            }

            PMNetWriter w = new PMNetWriter(32);
            w.WriteInt32(targetId);
            w.WriteFloat(angle);
            entry.Invoke(target, new PMNetReader(w.ToArray()));
        }

        /// <summary>D-R0-42：服务端只接受来自"该对象拥有者连接"的调用。</summary>
        private static void TestOwnershipCheck()
        {
            Check(!PMRpcDispatch.ShouldCallRemoteFunction(true, false, false),
                "R1 非 Owner 连接发来的 Server RPC 被拒（服务端 + 不是对象拥有者）");
            Check(PMRpcDispatch.ShouldCallRemoteFunction(true, true, false),
                "R2 Owner 连接发来的 Server RPC 被接受");
            Check(!PMRpcDispatch.ShouldCallRemoteFunction(true, true, true),
                "R3 IgnoreRpcs 状态下即使 Owner 也被拒");
            Check(PMRpcDispatch.ShouldCallRemoteFunction(false, false, false),
                "R4 客户端侧不额外判归属（能收到说明服务端已放行）");
        }

        // =================================================================================
        //  RV5：数组实参快照 / 入队前长度门
        // =================================================================================

        /// <summary>
        /// 造一个「服务端视角 + 有连接」的对象。
        /// 用途：让 **Client** 方向的 RPC 判定为 Remote（服务端有连接的对象调 Client RPC
        /// 才是"发给拥有者"这条真实路径，见 #14/#16 分支）。
        /// </summary>
        private static E2eReplicated MakeServerSideConnectedObject(out TestConn conn)
        {
            conn = new TestConn(910, true);
            ConnCarrier carrier = new ConnCarrier();
            carrier.Conn = conn;

            E2eReplicated obj = new E2eReplicated();
            obj.Owner = carrier;
            obj.NetMode = PMNetMode.DedicatedServer;
            obj.Role = PMNetRole.Authority;
            return obj;
        }

        /// <summary>
        /// RV5 核心：数组实参必须在**调用点**快照，且快照要发生在**本地执行之前**。
        ///
        /// 用 Multicast 是因为它在服务端同时命中 Local 与 Remote：**先本地执行、再外发**。
        /// 夹具的 `Push` 实现在本地执行时就地改写 `values[0]`，因此
        /// "远端看到的值"与"本地数组现在的值"必须是两个不同的数：
        ///   远端 = 调用时的值（快照）；本地 = 999（实现改写后）。
        /// 只捕获引用（或旧版的"对象上的实参帧"）在这一条上会发出**错误的参数**。
        /// </summary>
        private static void TestArrayArgCallTimeSnapshot()
        {
            E2eReplicated sender = new E2eReplicated();
            sender.NetMode = PMNetMode.DedicatedServer;
            sender.Role = PMNetRole.Authority;

            PMFunctionCallspace cs = PMRpcDispatch.EvaluateCallspace(
                sender.NetMode, sender.Role, PMRpcKind.Multicast, false, false);
            Check(PMRpcDispatch.ShouldExecuteLocal(cs) && PMRpcDispatch.ShouldSendRemote(cs),
                "M1 服务端多播的 callspace 同时含 Local 与 Remote（" + cs + "）");

            PMNet.Generated.PMNetGeneratedRegistry.ClearPendingRpcs();
            float[] buffer = new float[] { 1.5f, 2.5f };
            sender.Push(buffer);

            Check(sender.PushCount == 1 && Math.Abs(sender.LastPushFirst - 1.5f) < 1e-6f,
                "M2 本地执行先发生、看到调用时的值（" + sender.LastPushFirst + "）");
            Check(Math.Abs(buffer[0] - 999f) < 1e-6f,
                "M3 本地实现确实就地改写了实参数组（" + buffer[0] + "）—— 这正是快照存在的理由");
            Check(PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount == 1,
                "M4 多播也把这次调用排进了远端发送队列（实际 "
                + PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount + "）");

            PMNet.Generated.PMNetPendingRpc pending;
            Check(PMNet.Generated.PMNetGeneratedRegistry.TryDequeueRpc(out pending),
                "M5 取出待发项");
            PMNetWriter w = new PMNetWriter(32);
            pending.Write(pending.Target, w);

            E2eReplicated receiver = new E2eReplicated();
            PMNetRpcEntry entry;
            Check(PMNetRegistry.TryGetRpc(pending.RpcId, out entry), "M6 取到 Push 的接收分发");
            _nRpcInvoked++;
            entry.Invoke(receiver, new PMNetReader(w.ToArray()));

            Check(receiver.PushCount == 1 && Math.Abs(receiver.LastPushFirst - 1.5f) < 1e-6f,
                "M7 ★核心：远端看到的是**调用时**的值 1.5（实际 " + receiver.LastPushFirst
                + "）—— 本地的 999 没有泄漏到线上");
            Check(receiver.LastPushLength == 2, "M8 数组长度也随快照传递（" + receiver.LastPushLength + "）");
            _nArraySnapshot += 1;

            PMNet.Generated.PMNetGeneratedRegistry.ClearPendingRpcs();
        }

        /// <summary>
        /// RV5：长度门必须在**入队前**执行（数组与字符串各一条）。
        ///
        /// 只靠编码期的检查不够：编码发生在待发队列被排空时，那时这条调用**已经被接受**，
        /// 失败会落在与调用点无关的栈上。因此判据是"失败发生在入队之前、且队列没有变化"。
        /// </summary>
        private static void TestPreflightLimitsBeforeEnqueue()
        {
            TestConn conn;
            E2eReplicated server = MakeServerSideConnectedObject(out conn);

            PMNet.Generated.PMNetGeneratedRegistry.ClearPendingRpcs();

            bool arrayThrew = false;
            try
            {
                server.Push(new float[8192]);
            }
            catch (FormatException)
            {
                arrayThrew = true;
            }

            Check(arrayThrew, "M9 数组实参超长在**调用点**即抛 FormatException");
            Check(PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount == 0,
                "M10 超长数组没有进入待发队列（长度门在入队前执行）");

            bool stringThrew = false;
            try
            {
                server.Say(new string('x', PMNetString.MaxBytes + 1));
            }
            catch (FormatException)
            {
                stringThrew = true;
            }

            Check(stringThrew, "M11 字符串实参超长在**调用点**即抛 FormatException");
            Check(PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount == 0,
                "M12 超长字符串没有进入待发队列（string 不可变 ⇒ 只做长度门，不做拷贝）");

            // 对照：恰好等于上限的字符串必须能正常入队，
            // 否则上面两条可能只是"这条路什么都发不出去"。
            server.Say(new string('x', PMNetString.MaxBytes));
            Check(PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount == 1,
                "M13 恰好等于上限的字符串正常入队（实际 "
                + PMNet.Generated.PMNetGeneratedRegistry.PendingRpcCount + "）");
            _nRpcQueued += 1;

            // 顺便把这条入队项编码回来，证明"上限内的字符串真的能走完全程"。
            PMNet.Generated.PMNetPendingRpc pending;
            if (PMNet.Generated.PMNetGeneratedRegistry.TryDequeueRpc(out pending))
            {
                PMNetWriter w = new PMNetWriter(PMNetString.MaxBytes + 32);
                pending.Write(pending.Target, w);
                E2eReplicated receiver = new E2eReplicated();
                PMNetRpcEntry entry;
                if (PMNetRegistry.TryGetRpc(pending.RpcId, out entry) && entry != null)
                {
                    entry.Invoke(receiver, new PMNetReader(w.ToArray()));
                    Check(receiver.SayCount == 1 && receiver.LastSay != null
                          && receiver.LastSay.Length == PMNetString.MaxBytes,
                        "M14 上限内的字符串经接收侧正确还原（长度 "
                        + (receiver.LastSay == null ? -1 : receiver.LastSay.Length) + "）");
                    _nRpcInvoked += 1;
                }
            }

            PMNet.Generated.PMNetGeneratedRegistry.ClearPendingRpcs();
        }

        // =================================================================================
        //  RV6：带真实连接与世界查找的接收入口
        // =================================================================================

        /// <summary>
        /// 造一个「服务端世界 + 已登记对象 + 拥有者连接」的最小宿主。
        /// 走 `PMNetWorld.Spawn` 而不是手工 new：NetId / State / 世界索引都得是真链路。
        /// </summary>
        private static PMNetWorld NewReceiveWorld(out E2eReplicated obj, out TestConn ownerConn)
        {
            PMNetWorld world = new PMNetWorld(new PMSession(0x2Cu, true));
            world.RegisterClass(E2eReplicated.PMGeneratedClassId, delegate () { return new E2eReplicated(); });

            obj = new E2eReplicated();
            if (!world.Spawn(obj, E2eReplicated.PMGeneratedClassId))
            {
                throw new InvalidOperationException("RV6 夹具对象 Spawn 失败");
            }

            ownerConn = new TestConn(920, true);
            world.AddConnection(ownerConn);
            obj.OwnerConnection = ownerConn;
            return world;
        }

        private static PMNetRpcReceiveResult DeliverRpc(
            PMNetWorld world, PMNetConnection source, ushort rpcId, PMNetObject target, byte[] payload)
        {
            return PMNetRpcReceive.Deliver(world, source, rpcId, target.NetId, payload, 0, payload.Length);
        }

        private static byte[] EncodeNotify(int code)
        {
            PMNetWriter w = new PMNetWriter(16);
            w.WriteInt32(code);
            return w.ToArray();
        }

        private static byte[] EncodeWarp(int verdict)
        {
            PMNetWriter w = new PMNetWriter(16);
            w.WriteInt32(verdict);
            return w.ToArray();
        }

        private static byte[] EncodeChecked(int token)
        {
            PMNetWriter w = new PMNetWriter(16);
            w.WriteInt32(token);
            return w.ToArray();
        }

        /// <summary>
        /// RV6：权限闸门（世界查找 / 存活 / 方向 / 归属 / IgnoreRpcs）必须**在解码之前**生效，
        /// 且它们是真的把包投递进接收入口得到的结论 —— 不是对谓词的纯函数断言。
        /// </summary>
        private static void TestReceiveEntryPermissions()
        {
            PMNetRpcReceive.ResetStats();

            E2eReplicated obj;
            TestConn ownerConn;
            PMNetWorld world = NewReceiveWorld(out obj, out ownerConn);

            TestConn stranger = new TestConn(921, true);
            world.AddConnection(stranger);

            byte[] fire = EncodeFire(5, 1.0f);

            // (1) Owner 连接 ⇒ 放行
            int before = obj.FireCount;
            PMNetRpcReceiveResult owned = DeliverRpc(world, ownerConn, E2eReplicated.PMGeneratedRpcId_Fire, obj, fire);
            Check(owned.Applied, "R5 Owner 连接投递 Server RPC ⇒ 放行（实际 " + owned.Status + "）");
            Check(obj.FireCount == before + 1,
                "R6 Owner 连接的调用真的执行了实现（FireCount " + before + " → " + obj.FireCount + "）");
            _nRpcReceiveAccepted += 1;
            _nRpcInvoked += 1;

            // (2) 非 Owner 连接 ⇒ 拒绝且不执行
            before = obj.FireCount;
            PMNetRpcReceiveResult strangerResult =
                DeliverRpc(world, stranger, E2eReplicated.PMGeneratedRpcId_Fire, obj, fire);
            Check(strangerResult.Status == PMRpcReceiveStatus.NotOwner,
                "R7 非 Owner 连接投递 Server RPC ⇒ NotOwner（实际 " + strangerResult.Status + "）");
            Check(obj.FireCount == before,
                "R8 非 Owner 的调用**没有**执行实现（FireCount 仍为 " + obj.FireCount + "）");
            Check(!strangerResult.DisconnectRequested,
                "R9 归属拒绝**不**请求断连（与原生校验失败刻意不同）");
            _nRpcReceiveRejected += 1;

            // (3) IgnoreRpcs ⇒ 即使 Owner 也拒
            ownerConn.IgnoreRpcs = true;
            before = obj.FireCount;
            PMNetRpcReceiveResult ignored =
                DeliverRpc(world, ownerConn, E2eReplicated.PMGeneratedRpcId_Fire, obj, fire);
            Check(ignored.Status == PMRpcReceiveStatus.IgnoreRpcs,
                "R10 IgnoreRpcs 状态下即使 Owner 也被拒（实际 " + ignored.Status + "）");
            Check(obj.FireCount == before, "R11 IgnoreRpcs 的调用没有执行实现");
            ownerConn.IgnoreRpcs = false;
            _nRpcReceiveRejected += 1;

            // (4) 方向错：服务端收到 Client 方向
            before = obj.NotifyCount;
            PMNetRpcReceiveResult wrongDirection = DeliverRpc(
                world, ownerConn, E2eReplicated.PMGeneratedRpcId_Notify, obj, EncodeNotify(3));
            Check(wrongDirection.Status == PMRpcReceiveStatus.WrongDirection,
                "R12 服务端收到 Client 方向的 RPC ⇒ WrongDirection（实际 " + wrongDirection.Status + "）");
            Check(obj.NotifyCount == before, "R13 方向非法的调用没有执行实现");
            _nRpcReceiveRejected += 1;

            // (5) 已销毁 ⇒ 世界查不到（销毁即摘出索引，D-R0-04）
            uint destroyedId = obj.NetId.Value;
            Check(world.DestroyObject(obj), "R14 夹具对象可被销毁（服务端权威路径）");
            PMNetRpcReceiveResult destroyed = PMNetRpcReceive.Deliver(
                world, ownerConn, E2eReplicated.PMGeneratedRpcId_Fire,
                new PMNetId(destroyedId, false), fire, 0, fire.Length);
            Check(destroyed.Status == PMRpcReceiveStatus.UnknownTarget,
                "R15 已销毁对象 ⇒ UnknownTarget（实际 " + destroyed.Status + "）");
            _nRpcReceiveRejected += 1;

            // (6) 未注册的 RpcId
            // 注意：目标对象必须**存活**，否则先撞到的是 UnknownTarget —— 这条断言
            // 就变成在测另一件事（R14 已经把 obj 销毁了，所以这里另起一个活对象）。
            E2eReplicated liveTarget = new E2eReplicated();
            world.Spawn(liveTarget, E2eReplicated.PMGeneratedClassId);
            liveTarget.OwnerConnection = ownerConn;
            PMNetRpcReceiveResult unknown = DeliverRpc(world, ownerConn, 65000, liveTarget, fire);
            Check(unknown.Status == PMRpcReceiveStatus.UnknownRpc,
                "R16 未注册的 RpcId ⇒ UnknownRpc（实际 " + unknown.Status + "）");
            Check(unknown.Detail != null && unknown.Detail.Contains("65000"),
                "R16b 拒绝细节点出未注册的 RpcId（可观测，实际：" + unknown.Detail + "）");
            _nRpcReceiveRejected += 1;

            // (7) 客户端侧：Server 方向非法；Client 方向放行
            Rig rig = NewRig(1);
            RigClient client = rig.Clients[0];

            PMNetRpcReceiveResult clientServer = DeliverRpc(client.World, client.Conn,
                E2eReplicated.PMGeneratedRpcId_Fire, client.Obj, EncodeFire(1, 0f));
            Check(clientServer.Status == PMRpcReceiveStatus.WrongDirection,
                "R17 客户端侧收到 Server 方向 RPC ⇒ WrongDirection（实际 " + clientServer.Status + "）");
            _nRpcReceiveRejected += 1;

            int notifyBefore = client.Obj.NotifyCount;
            PMNetRpcReceiveResult clientOk = DeliverRpc(client.World, client.Conn,
                E2eReplicated.PMGeneratedRpcId_Notify, client.Obj, EncodeNotify(42));
            Check(clientOk.Applied,
                "R18 客户端侧收到 Client 方向 RPC ⇒ 放行（实际 " + clientOk.Status + "）");
            Check(client.Obj.NotifyCount == notifyBefore + 1 && client.Obj.LastNotifyCode == 42,
                "R19 客户端侧 Client RPC 真的执行了实现（code=" + client.Obj.LastNotifyCode + "）");
            _nRpcReceiveAccepted += 1;
            _nRpcInvoked += 1;

            Check(PMNetRpcReceive.Rejected >= 6,
                "R20 拒绝计数可观测（实际 " + PMNetRpcReceive.Rejected + "）");
            Check(PMNetRpcReceive.Delivered == 2,
                "R21 放行计数只统计真的交给执行器的两次（实际 " + PMNetRpcReceive.Delivered + "）");

            PMNetRpcReceive.ResetStats();
        }

        /// <summary>
        /// RV6：校验路由经**真实入口**投递（而不是只调 `entry.Invoke`）。
        /// 三态 + 非法值 + 原生 Validate 的 false/true 必须全部走到，且
        /// "Validate=false 真的向来源连接发断连请求"而 Reject 不发。
        /// </summary>
        private static void TestReceiveEntryValidationRouting()
        {
            List<string> reported = new List<string>();
            List<string> validateFailed = new List<string>();

            PMRpcValidationSink.OnReported = delegate (PMNetObject t, ushort id, PMRpcValidation v, string n)
            {
                reported.Add(n + ":" + v);
            };
            PMRpcValidationSink.OnValidateFailed = delegate (PMNetObject t, ushort id, string n)
            {
                validateFailed.Add(n);
            };

            PMNetRpcReceive.ResetStats();

            try
            {
                E2eReplicated obj;
                TestConn ownerConn;
                PMNetWorld world = NewReceiveWorld(out obj, out ownerConn);

                // --- ForceValidate：Accept / Report / Reject / 非法值 ---
                reported.Clear();
                int warpBefore = obj.WarpCount;
                PMNetRpcReceiveResult accept = DeliverRpc(
                    world, ownerConn, E2eReplicated.PMGeneratedRpcId_Warp, obj, EncodeWarp(0));
                Check(accept.Applied && obj.WarpCount == warpBefore + 1 && reported.Count == 0,
                    "V1 ForceValidate=Accept ⇒ 静默放行并执行实现（上报 " + reported.Count + " 条）");
                _nRpcReceiveAccepted += 1;

                reported.Clear();
                int afterAccept = obj.WarpCount;
                PMNetRpcReceiveResult report = DeliverRpc(
                    world, ownerConn, E2eReplicated.PMGeneratedRpcId_Warp, obj, EncodeWarp(1));
                Check(report.Applied && obj.WarpCount == afterAccept + 1,
                    "V2 ForceValidate=Report ⇒ 仍然执行实现");
                Check(reported.Count == 1 && reported[0] == "Warp:Report",
                    "V3 ForceValidate=Report ⇒ 上报 Report（实际 "
                    + (reported.Count > 0 ? reported[0] : "<无>") + "）");
                _nRpcReceiveAccepted += 1;
                _nValidationSink += reported.Count;

                reported.Clear();
                int afterReport = obj.WarpCount;
                PMNetRpcReceiveResult reject = DeliverRpc(
                    world, ownerConn, E2eReplicated.PMGeneratedRpcId_Warp, obj, EncodeWarp(2));
                Check(reject.Applied && obj.WarpCount == afterReport,
                    "V4 ForceValidate=Reject ⇒ 跳过实现（WarpCount 仍为 " + obj.WarpCount + "）");
                Check(reported.Count == 1 && reported[0] == "Warp:Reject",
                    "V5 ForceValidate=Reject ⇒ 上报 Reject（实际 "
                    + (reported.Count > 0 ? reported[0] : "<无>") + "）");
                Check(!reject.DisconnectRequested && ownerConn.DisconnectRequests.Count == 0,
                    "V6 ForceValidate 的 Reject **不**请求断连（D-R0-45）");
                _nRpcReceiveAccepted += 1;
                _nValidationSink += reported.Count;

                reported.Clear();
                int afterReject = obj.WarpCount;
                PMNetRpcReceiveResult invalid = DeliverRpc(
                    world, ownerConn, E2eReplicated.PMGeneratedRpcId_Warp, obj, EncodeWarp(99));
                Check(invalid.Applied && obj.WarpCount == afterReject,
                    "V7 ★非法 ForceValidate 结果（99）失败关闭：跳过实现（WarpCount 仍为 "
                    + obj.WarpCount + "）");
                Check(reported.Count == 1 && reported[0] == "Warp:Reject",
                    "V8 非法结果被归并成 Reject 上报（实际 "
                    + (reported.Count > 0 ? reported[0] : "<无>") + "）");
                _nRpcReceiveAccepted += 1;
                _nValidationSink += reported.Count;

                // --- 原生 Validate：false ⇒ 真的向来源连接发断连请求 ---
                validateFailed.Clear();
                int checkedBefore = obj.CheckedCount;
                PMNetRpcReceiveResult bad = DeliverRpc(
                    world, ownerConn, E2eReplicated.PMGeneratedRpcId_Checked, obj, EncodeChecked(-1));
                Check(bad.Status == PMRpcReceiveStatus.ValidationFailed,
                    "V9 原生 Validate=false ⇒ ValidationFailed（实际 " + bad.Status + "）");
                Check(obj.CheckedCount == checkedBefore, "V10 Validate=false 时实现没有执行");
                Check(bad.DisconnectRequested && ownerConn.DisconnectRequests.Count == 1
                      && ownerConn.DisconnectRequests[0] == "Checked",
                    "V11 ★Validate=false 真的向来源连接发出了断连请求（实际 "
                    + ownerConn.DisconnectRequests.Count + " 次）");
                Check(validateFailed.Count == 1 && validateFailed[0] == "Checked",
                    "V12 同时经 PMRpcValidationSink 留痕（可观测性）");
                _nDisconnectRequested += ownerConn.DisconnectRequests.Count;
                _nValidationSink += validateFailed.Count;

                PMNetRpcReceiveResult good = DeliverRpc(
                    world, ownerConn, E2eReplicated.PMGeneratedRpcId_Checked, obj, EncodeChecked(7));
                Check(good.Applied && obj.CheckedCount == checkedBefore + 1 && obj.LastCheckedToken == 7,
                    "V13 原生 Validate=true ⇒ 正常执行实现（token=" + obj.LastCheckedToken + "）");
                Check(ownerConn.DisconnectRequests.Count == 1,
                    "V14 Validate 通过不会产生额外的断连请求");
                _nRpcReceiveAccepted += 1;
                _nRpcInvoked += 1;

                // --- 断连接缝未实现时必须可观测，而不是静默（H3）---
                PlainConn plain = new PlainConn(922, true);
                world.AddConnection(plain);
                obj.OwnerConnection = plain;

                List<string> warnings = new List<string>();
                PMNetRpcReceive.Warn = delegate (string m) { warnings.Add(m); };
                long unroutableBefore = PMNetRpcReceive.UnroutableDisconnectRequests;
                PMNetRpcReceiveResult unroutable = DeliverRpc(
                    world, plain, E2eReplicated.PMGeneratedRpcId_Checked, obj, EncodeChecked(-1));
                Check(unroutable.Status == PMRpcReceiveStatus.ValidationFailed
                      && !unroutable.DisconnectRequested,
                    "V15 连接未实现断连接缝：仍拒绝实现，但不谎报断连成功");
                Check(PMNetRpcReceive.UnroutableDisconnectRequests == unroutableBefore + 1
                      && warnings.Count == 1,
                    "V16 不可路由的断连请求被计数并告警（不静默）");
                PMNetRpcReceive.Warn = null;
            }
            finally
            {
                PMRpcValidationSink.OnReported = null;
                PMRpcValidationSink.OnValidateFailed = null;
                PMNetRpcReceive.Warn = null;
                PMNetRpcReceive.ResetStats();
            }
        }

        /// <summary>
        /// RV6：畸形载荷必须被接收入口拒绝，且**业务实现不得执行**。
        /// 三类：截断（读越界）、尾随字节（布局漂移）、长度前缀越界（分配前拒绝）。
        /// </summary>
        private static void TestReceiveEntryMalformedPayload()
        {
            PMNetRpcReceive.ResetStats();

            E2eReplicated obj;
            TestConn ownerConn;
            PMNetWorld world = NewReceiveWorld(out obj, out ownerConn);

            // (1) 截断：Fire 需要 int + float，只给 int
            PMNetWriter truncated = new PMNetWriter(16);
            truncated.WriteInt32(5);
            int before = obj.FireCount;
            PMNetRpcReceiveResult t = DeliverRpc(
                world, ownerConn, E2eReplicated.PMGeneratedRpcId_Fire, obj, truncated.ToArray());
            Check(t.Status == PMRpcReceiveStatus.Malformed,
                "X1 截断载荷 ⇒ Malformed（实际 " + t.Status + "）");
            Check(obj.FireCount == before, "X2 截断载荷没有执行实现（解码期抛异常）");
            _nRpcReceiveRejected += 1;

            // (2) 尾随字节
            PMNetWriter trailing = new PMNetWriter(32);
            trailing.WriteInt32(5);
            trailing.WriteFloat(1f);
            trailing.WriteInt32(777);
            before = obj.FireCount;
            PMNetRpcReceiveResult tr = DeliverRpc(
                world, ownerConn, E2eReplicated.PMGeneratedRpcId_Fire, obj, trailing.ToArray());
            Check(tr.Status == PMRpcReceiveStatus.Malformed,
                "X3 尾随字节 ⇒ Malformed（实际 " + tr.Status + "）");
            Check(obj.FireCount == before,
                "X4 带尾随字节的载荷在**业务实现之前**被拒（FireCount 仍为 " + obj.FireCount + "）");
            _nRpcReceiveRejected += 1;

            // (3) 数组长度前缀越界：必须在分配之前拒绝（客户端侧的 Multicast 接收路径）
            Rig rig = NewRig(1);
            RigClient client = rig.Clients[0];

            PMNetWriter huge = new PMNetWriter(16);
            huge.WriteSInt32(5000);
            int pushBefore = client.Obj.PushCount;
            PMNetRpcReceiveResult h = DeliverRpc(client.World, client.Conn,
                E2eReplicated.PMGeneratedRpcId_Push, client.Obj, huge.ToArray());
            Check(h.Status == PMRpcReceiveStatus.Malformed,
                "X5 数组长度前缀越界 ⇒ Malformed（实际 " + h.Status + "）");
            Check(client.Obj.PushCount == pushBefore,
                "X6 越界数组没有执行实现（读侧在分配之前用上限拒绝）");
            _nRpcReceiveRejected += 1;

            // (4) 字符串超限：同样在分配之前拒绝
            PMNetWriter longString = new PMNetWriter(PMNetString.MaxBytes + 32);
            longString.WriteStringValue(new string('x', PMNetString.MaxBytes + 1));
            int sayBefore = client.Obj.SayCount;
            PMNetRpcReceiveResult s = DeliverRpc(client.World, client.Conn,
                E2eReplicated.PMGeneratedRpcId_Say, client.Obj, longString.ToArray());
            Check(s.Status == PMRpcReceiveStatus.Malformed,
                "X7 超长字符串 ⇒ Malformed（实际 " + s.Status + "）");
            Check(client.Obj.SayCount == sayBefore, "X8 超长字符串没有执行实现");
            _nRpcReceiveRejected += 1;

            Check(PMNetRpcReceive.MalformedCount == 4,
                "X9 畸形计数可观测（实际 " + PMNetRpcReceive.MalformedCount + "）");

            PMNetRpcReceive.ResetStats();
        }

        // =================================================================================
        //  RPC 注册表复合键：(ClassId, RpcId)
        // =================================================================================

        // 手工构造的三个类：A 与 B **共用**同一个 RpcId（契约 §2.2：RpcId 只在类内唯一），
        // C 只用另一个号 —— 它用来做"错误类拒绝"的对照（C 类上不存在共享号）。
        private const uint CompositeClassA = 0x0A0A0A0Au;
        private const uint CompositeClassB = 0x0B0B0B0Bu;
        private const uint CompositeClassC = 0x0C0C0C0Cu;
        private const ushort CompositeSharedRpcId = 777;

        private static int _compositeMarkerA;
        private static int _compositeMarkerB;
        private static int _compositeMarkerC;
        private static int _compositeArgA;
        private static int _compositeArgB;

        private static void CompositeInvokerA(PMNetObject t, PMNetReader r)
        {
            _compositeMarkerA++;
            _compositeArgA = r.ReadInt32();
        }

        private static void CompositeInvokerB(PMNetObject t, PMNetReader r)
        {
            _compositeMarkerB++;
            _compositeArgB = r.ReadInt32();
        }

        private static void CompositeInvokerC(PMNetObject t, PMNetReader r)
        {
            _compositeMarkerC++;
        }

        private static PMNetRpcEntry BuildCompositeRpc(uint classId, ushort rpcId, string methodName, PMRpcInvoker invoke)
        {
            PMRpcDescriptor d = new PMRpcDescriptor();
            d.RpcId = rpcId;
            d.Direction = PMRpcKind.Server;      // 服务端接收方向（本测试的接收侧是服务端世界）
            d.IsReliable = true;
            d.Validator = PMRpcValidator.None;
            d.ParamLayoutId = 1;
            d.MethodName = methodName;

            PMNetRpcEntry rpc = new PMNetRpcEntry();
            rpc.Descriptor = d;
            rpc.OwningClassId = classId;
            rpc.Invoke = invoke;
            return rpc;
        }

        private static PMNetClassEntry BuildCompositeClass(uint classId, string typeName, PMNetRpcEntry[] rpcs)
        {
            PMNetClassEntry entry = new PMNetClassEntry();
            entry.ClassId = classId;
            entry.TypeName = typeName;
            entry.Rep = null;
            entry.Factory = delegate () { return new E2eReplicated(); };
            entry.Rpcs = rpcs;
            return entry;
        }

        /// <summary>造一个「服务端世界 + 已 Spawn 的指定 ClassId 对象 + 拥有者连接」的最小宿主。</summary>
        private static PMNetWorld NewCompositeWorld(uint classId, out E2eReplicated obj, out TestConn conn)
        {
            PMNetWorld world = new PMNetWorld(new PMSession(0x2Eu, true));
            world.RegisterClass(classId, delegate () { return new E2eReplicated(); });

            obj = new E2eReplicated();
            if (!world.Spawn(obj, classId))
            {
                throw new InvalidOperationException("复合键夹具对象 Spawn 失败（ClassId=" + classId + "）");
            }

            conn = new TestConn(950 + (int)(classId & 0xF), true);
            world.AddConnection(conn);
            obj.OwnerConnection = conn;    // 归属闸门要能放行，才能测到查表那一步
            return world;
        }

        private static byte[] EncodeInt(int value)
        {
            PMNetWriter w = new PMNetWriter(8);
            w.WriteInt32(value);
            return w.ToArray();
        }

        /// <summary>
        /// 注册表的键必须是 **(ClassId, RpcId)**（契约 §2.2：RPC ID 在类内唯一）。
        ///
        /// 两种缺陷形态都做成可判定的断言：
        /// 1. 两个类共用同一个 RpcId ⇒ 必须都能登记（只按 RpcId 建表的形态在这里直接硬失败）；
        /// 2. 投递必须按**目标对象的 ClassId** 分派 ⇒ 两个类各命中自己的实现；
        /// 3. RpcId 属于别的类 ⇒ 拒绝（且拒绝原因里点名它属于哪个类），
        ///    而不是靠对象类型转换失败去兜；
        /// 4. 裸 RpcId 歧义时兼容查询返回 false（不得任意挑一个）；
        /// 5. 注册失败不得留下半登记的类（旧形态是先登记类、再逐条登记 RPC）。
        /// </summary>
        private static void TestRpcRegistryCompositeKey()
        {
            int classesBefore = PMNetRegistry.ClassCount;
            int rpcsBefore = PMNetRegistry.RpcCount;

            PMNetRegistry.Reset();

            PMNetClassEntry entryA = BuildCompositeClass(CompositeClassA, "CompositeA",
                new PMNetRpcEntry[]
                {
                    BuildCompositeRpc(CompositeClassA, CompositeSharedRpcId, "SharedA", new PMRpcInvoker(CompositeInvokerA)),
                });
            PMNetClassEntry entryB = BuildCompositeClass(CompositeClassB, "CompositeB",
                new PMNetRpcEntry[]
                {
                    BuildCompositeRpc(CompositeClassB, CompositeSharedRpcId, "SharedB", new PMRpcInvoker(CompositeInvokerB)),
                });
            PMNetClassEntry entryC = BuildCompositeClass(CompositeClassC, "CompositeC",
                new PMNetRpcEntry[]
                {
                    BuildCompositeRpc(CompositeClassC, (ushort)(CompositeSharedRpcId + 1), "OtherC", new PMRpcInvoker(CompositeInvokerC)),
                });

            bool registered = true;
            string registrationError = null;
            try
            {
                PMNetRegistry.RegisterClass(entryA);
                PMNetRegistry.RegisterClass(entryB);   // ★ 旧形态（只按 RpcId 建表）在这里会报「RpcId 重复注册」
                PMNetRegistry.RegisterClass(entryC);
            }
            catch (Exception ex)
            {
                registered = false;
                registrationError = ex.GetType().Name + " " + ex.Message;
            }

            Check(registered, "Ka 两个不同类可以登记同一个 RpcId（契约 §2.2 按类分桶）"
                + (registrationError == null ? "" : "，实际抛出 " + registrationError));
            Check(PMNetRegistry.RpcCount == 3,
                "Kb 三条 RPC 按 (ClassId, RpcId) 计为 3 条（实际 " + PMNetRegistry.RpcCount + "）");

            PMNetRpcEntry gotA;
            PMNetRpcEntry gotB;
            Check(PMNetRegistry.TryGetRpc(CompositeClassA, CompositeSharedRpcId, out gotA) && gotA != null
                  && gotA.Descriptor.MethodName == "SharedA",
                "Kc (A 类, 共享 RpcId) 查到 A 类自己的执行器");
            Check(PMNetRegistry.TryGetRpc(CompositeClassB, CompositeSharedRpcId, out gotB) && gotB != null
                  && gotB.Descriptor.MethodName == "SharedB",
                "Kd (B 类, 共享 RpcId) 查到 B 类自己的执行器");
            Check(!ReferenceEquals(gotA, gotB), "Ke 两个类拿到的是不同执行器（复合键没有串线）");

            PMNetRpcEntry bare;
            Check(!PMNetRegistry.TryGetRpc(CompositeSharedRpcId, out bare),
                "Kf ★共享 RpcId 的裸查询返回 false（歧义不得任意挑一个）");
            PMNetRpcEntry bareUnique;
            Check(PMNetRegistry.TryGetRpc((ushort)(CompositeSharedRpcId + 1), out bareUnique) && bareUnique != null,
                "Kg 唯一 RpcId 的裸查询仍返回 true（歧义只影响发生冲突的那个 ID）");

            // ── 真实投递：按目标对象的 ClassId 分派 ────────────────────────────
            _compositeMarkerA = 0;
            _compositeMarkerB = 0;
            _compositeMarkerC = 0;
            _compositeArgA = 0;
            _compositeArgB = 0;
            PMNetRpcReceive.ResetStats();

            E2eReplicated objA;
            TestConn connA;
            PMNetWorld worldA = NewCompositeWorld(CompositeClassA, out objA, out connA);

            E2eReplicated objB;
            TestConn connB;
            PMNetWorld worldB = NewCompositeWorld(CompositeClassB, out objB, out connB);

            E2eReplicated objC;
            TestConn connC;
            PMNetWorld worldC = NewCompositeWorld(CompositeClassC, out objC, out connC);

            PMNetRpcReceiveResult resA = DeliverRpc(worldA, connA, CompositeSharedRpcId, objA, EncodeInt(11));
            Check(resA.Applied && _compositeMarkerA == 1 && _compositeMarkerB == 0,
                "Kh ★投给 A 类目标执行了 A 类实现（A=" + _compositeMarkerA + " B=" + _compositeMarkerB
                + "，状态 " + resA.Status + "）");
            Check(_compositeArgA == 11, "Ki A 类执行器读到了本次载荷的实参（" + _compositeArgA + "）");
            _nRpcReceiveAccepted += 1;
            _nRpcInvoked += 1;

            PMNetRpcReceiveResult resB = DeliverRpc(worldB, connB, CompositeSharedRpcId, objB, EncodeInt(22));
            Check(resB.Applied && _compositeMarkerB == 1 && _compositeMarkerA == 1,
                "Kj ★投给 B 类目标执行了 B 类实现（A=" + _compositeMarkerA + " B=" + _compositeMarkerB + "）");
            Check(_compositeArgB == 22, "Kk B 类执行器读到了本次载荷的实参（" + _compositeArgB + "）");
            _nRpcReceiveAccepted += 1;
            _nRpcInvoked += 1;

            // ── 错误类拒绝：共享号属于 A/B，投给 C 类目标必须拒 ────────────────
            int markerCBefore = _compositeMarkerC;
            PMNetRpcReceiveResult wrongClass = DeliverRpc(worldC, connC, CompositeSharedRpcId, objC, EncodeInt(33));
            Check(wrongClass.Status == PMRpcReceiveStatus.UnknownRpc,
                "Kl ★RpcId 属于别的类 ⇒ UnknownRpc（不接受投到目标类之外的 RPC，实际 " + wrongClass.Status + "）");
            Check(_compositeMarkerC == markerCBefore,
                "Km 错误类的投递没有执行任何实现（C 计数仍为 " + _compositeMarkerC + "）");
            Check(wrongClass.Detail != null && wrongClass.Detail.Contains("没有注册 RpcId " + CompositeSharedRpcId),
                "Kn 拒绝细节点名目标类与该 RpcId（可观测，实际：" + wrongClass.Detail + "）");
            _nRpcReceiveRejected += 1;

            // ── 错误类拒绝（无歧义版）：某个类独有的 RpcId 投给另一个类的目标 ──────
            // 与上面那条的区别：这次裸 RpcId 唯一，因此拒绝原因还能点名它属于哪个类 ——
            // 这是"投错类"可观测的必要条件（否则排查时只能看到一句未注册）。
            int markerABeforeUnique = _compositeMarkerA;
            PMNetRpcReceiveResult wrongClassUnique = DeliverRpc(
                worldA, connA, (ushort)(CompositeSharedRpcId + 1), objA, EncodeInt(55));
            Check(wrongClassUnique.Status == PMRpcReceiveStatus.UnknownRpc && _compositeMarkerA == markerABeforeUnique,
                "Kn2 ★某类独有的 RpcId 投给另一个类的目标 ⇒ UnknownRpc 且不执行任何实现（实际 "
                + wrongClassUnique.Status + "，A 计数 " + _compositeMarkerA + "）");
            Check(wrongClassUnique.Detail != null && wrongClassUnique.Detail.Contains("不接受投到别的类"),
                "Kn3 该拒绝的原因里点出这个 RpcId 属于哪个类（实际：" + wrongClassUnique.Detail + "）");
            _nRpcReceiveRejected += 1;

            PMNetRpcReceiveResult ownC = DeliverRpc(worldC, connC, (ushort)(CompositeSharedRpcId + 1), objC, EncodeInt(44));
            Check(ownC.Applied && _compositeMarkerC == markerCBefore + 1,
                "Ko 对照：C 类自己的 RpcId 投给 C 类目标正常放行（证明拒绝是定向的）");
            _nRpcReceiveAccepted += 1;
            _nRpcInvoked += 1;

            // ── 注册原子性：类内重复 ⇒ 整类不登记（不留半登记的类）────────────────
            PMNetClassEntry partial = BuildCompositeClass(0x0D0D0D0Du, "CompositePartial",
                new PMNetRpcEntry[]
                {
                    BuildCompositeRpc(0x0D0D0D0Du, 999, "PartialFirst", new PMRpcInvoker(CompositeInvokerC)),
                    BuildCompositeRpc(0x0D0D0D0Du, 999, "PartialDup", new PMRpcInvoker(CompositeInvokerC)),
                });

            int rpcsBeforeAtomic = PMNetRegistry.RpcCount;
            bool atomicThrew = false;
            try
            {
                PMNetRegistry.RegisterClass(partial);
            }
            catch (InvalidOperationException)
            {
                atomicThrew = true;
            }

            Check(atomicThrew, "Kp 类内重复 RpcId 仍然硬失败（抛异常）");

            PMNetClassEntry notRegistered;
            Check(!PMNetRegistry.TryGetClass(0x0D0D0D0Du, out notRegistered),
                "Kq 失败的注册没有留下「半个类」（ClassId 未登记）");
            Check(PMNetRegistry.RpcCount == rpcsBeforeAtomic,
                "Kr 失败的注册没有留下任何 RPC 条目（" + PMNetRegistry.RpcCount + " == " + rpcsBeforeAtomic + "）");

            // ── 复原注册表，不影响既有计数与后续断言 ──────────────────────────
            PMNetRegistry.Reset();
            PMNet.Generated.PMNetGeneratedRegistry.RegisterAll();

            Check(PMNetRegistry.ClassCount == classesBefore && PMNetRegistry.RpcCount == rpcsBefore,
                "Ks 注册表已复原到生成物状态（" + PMNetRegistry.ClassCount + " 类 / "
                + PMNetRegistry.RpcCount + " 条 RPC，期望 " + classesBefore + " / " + rpcsBefore + "）");

            PMNetRpcEntry fireAfterBare;
            Check(PMNetRegistry.TryGetRpc(E2eReplicated.PMGeneratedRpcId_Fire, out fireAfterBare) && fireAfterBare != null,
                "Kt 复原后裸 RpcId 查询再次可用（Fire 在生成物里唯一）");
            PMNetRpcEntry fireAfterKey;
            Check(PMNetRegistry.TryGetRpc(E2eReplicated.PMGeneratedClassId, E2eReplicated.PMGeneratedRpcId_Fire, out fireAfterKey)
                  && fireAfterKey != null,
                "Ku 复原后复合键查询也可用（(E2eReplicated, Fire)）");

            PMNetRpcReceive.ResetStats();
        }

        // ------------------------------------------------------------------ [4] 语言面

        private static void CheckLanguageSurface(string root)
        {
            List<SyntaxTree> trees = new List<SyntaxTree>();
            CSharpParseOptions options = new CSharpParseOptions(LanguageVersion.CSharp7_3);

            // 夹具
            string fixtures = root + "/Tools/PMNetE2E/E2eFixtures.cs";
            trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(fixtures), options, "E2eFixtures.cs"));

            // 生成产物
            string genDir = root + "/Tools/PMNetE2E/Generated";
            string[] genFiles = Directory.GetFiles(genDir, "*.cs", SearchOption.AllDirectories);
            Array.Sort(genFiles, StringComparer.Ordinal);
            for (int i = 0; i < genFiles.Length; i++)
            {
                trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(genFiles[i]), options, Path.GetFileName(genFiles[i])));
            }

            // PMNet 手写核心（与 PMNetLangCheck / PMDeclCheck 用同一份源码，不复制）
            string pmnetDir = root + "/Client/Assets/Scripts/PMNet";
            string[] runtimeFiles = Directory.GetFiles(pmnetDir, "*.cs", SearchOption.AllDirectories);
            int kept = 0;
            for (int i = 0; i < runtimeFiles.Length; i++)
            {
                if (runtimeFiles[i].Replace('\\', '/').Contains("/PMNet/Generated/"))
                {
                    continue;
                }

                trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(runtimeFiles[i]), options, Path.GetFileName(runtimeFiles[i])));
                kept++;
            }

            string refMode;
            List<MetadataReference> references = BuildReferences(out refMode);
            Check(references != null && references.Count > 0, "编译引用集可用：" + refMode);
            if (references == null || references.Count == 0)
            {
                return;
            }

            CSharpCompilation compilation = CSharpCompilation.Create(
                "PMNetE2EGenerated",
                trees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            List<string> errors = new List<string>();
            foreach (Diagnostic d in compilation.GetDiagnostics())
            {
                if (d.Severity == DiagnosticSeverity.Error)
                {
                    errors.Add(d.Id + " " + d.GetMessage() + " @ " + d.Location.GetLineSpan().Path + ":"
                        + (d.Location.GetLineSpan().StartLinePosition.Line + 1));
                }
            }

            Console.WriteLine("      编译输入：生成物 " + genFiles.Length + " 个 + 夹具 1 个 + PMNet 运行时 "
                + kept + " 个；引用集：" + refMode);
            for (int i = 0; i < errors.Count && i < 20; i++)
            {
                Console.WriteLine("      " + errors[i]);
            }

            Check(errors.Count == 0, "生成物 + 夹具 + 运行时在 C# 7.3 下零编译错误（实际 "
                  + errors.Count + " 个）");
        }

        private static List<MetadataReference> BuildReferences(out string mode)
        {
            List<MetadataReference> refs = new List<MetadataReference>();

            string refDir = FindNetStandard20RefDirectory();
            if (refDir != null)
            {
                string[] dlls = Directory.GetFiles(refDir, "*.dll");
                Array.Sort(dlls, StringComparer.Ordinal);
                for (int i = 0; i < dlls.Length; i++)
                {
                    try
                    {
                        refs.Add(MetadataReference.CreateFromFile(dlls[i]));
                    }
                    catch (Exception)
                    {
                        // 个别 facade 无法作为元数据读取时跳过；netstandard.dll 一定读得到。
                    }
                }

                mode = "netstandard2.0 引用程序集（" + refs.Count + " 个）";
                if (refs.Count > 0)
                {
                    return refs;
                }
            }

            string tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
            if (!string.IsNullOrEmpty(tpa))
            {
                string[] parts = tpa.Split(Path.PathSeparator);
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i].Length == 0)
                    {
                        continue;
                    }

                    try
                    {
                        refs.Add(MetadataReference.CreateFromFile(parts[i]));
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            mode = "降级：运行时 TPA（" + refs.Count + " 个）—— 未找到 netstandard2.0 引用程序集，"
                   + "本段只强制语言面 C# 7.3，API 面由 Tools/PMNetLangCheck 单独覆盖";
            return refs;
        }

        private static string FindNetStandard20RefDirectory()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string pkg = Path.Combine(home, ".nuget", "packages", "netstandard.library");
            if (!Directory.Exists(pkg))
            {
                return null;
            }

            string[] versions = Directory.GetDirectories(pkg);
            Array.Sort(versions, StringComparer.OrdinalIgnoreCase);
            for (int i = versions.Length - 1; i >= 0; i--)
            {
                string candidate = Path.Combine(versions[i], "build", "netstandard2.0", "ref");
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "netstandard.dll")))
                {
                    return candidate;
                }
            }

            return null;
        }

        // ------------------------------------------------------------------ [5] 负向验证

        /// <summary>一条可独立判定真假的断言探针。</summary>
        private sealed class Probe
        {
            public string Id;
            public string What;
            public Func<string> Run;      // 返回 null = 通过；返回字符串 = 失败原因

            public Probe(string id, string what, Func<string> run)
            {
                Id = id;
                What = what;
                Run = run;
            }
        }

        private static List<string> RunProbes(List<Probe> probes)
        {
            List<string> failures = new List<string>();
            for (int i = 0; i < probes.Count; i++)
            {
                string detail;
                try
                {
                    detail = probes[i].Run();
                }
                catch (Exception ex)
                {
                    detail = "抛出 " + ex.GetType().Name + "：" + ex.Message;
                }

                if (detail != null)
                {
                    failures.Add(probes[i].Id + " " + probes[i].What + " → " + detail);
                }
            }

            return failures;
        }

        // ---- 注入 ①：实参被覆盖（复刻返工前的「对象上的实参帧」形态）----

        /// <summary>
        /// 复刻返工前的生成物形态：实参暂存在**对象字段**上，编码推迟到"发送时"才从字段里读。
        ///
        /// 写入边界不允许改生成器，因此这里用**同一形状的替身**做负向验证 ——
        /// 目的不是证明生成器有 bug（§5.5 已记录它被修掉了），而是证明
        /// 「P4 实参未被覆盖」这条断言对**那种形态**真的会失败，即断言不是空的。
        /// </summary>
        private sealed class FaultyArgFrameSender
        {
            public int Arg0;
            public float Arg1;
            public readonly List<int> Pending = new List<int>();

            public void Call(int targetId, float angle)
            {
                Arg0 = targetId;
                Arg1 = angle;
                Pending.Add(0);
            }

            public bool Take(out object token)
            {
                if (Pending.Count == 0)
                {
                    token = null;
                    return false;
                }

                Pending.RemoveAt(0);
                token = this;
                return true;
            }

            public byte[] Encode(object token)
            {
                PMNetWriter w = new PMNetWriter(32);
                w.WriteInt32(Arg0);
                w.WriteFloat(Arg1);
                return w.ToArray();
            }
        }

        /// <summary>可注入的接收侧：`validating=false` 时复刻返工前「只记档位、不发射校验调用」的形态。</summary>
        private static void InvokeFire(E2eReplicated target, byte[] payload, bool validating)
        {
            if (validating)
            {
                PMNetRpcEntry entry;
                if (!PMNetRegistry.TryGetRpc(E2eReplicated.PMGeneratedRpcId_Fire, out entry))
                {
                    throw new InvalidOperationException("Fire 未注册");
                }

                entry.Invoke(target, new PMNetReader(payload));
                return;
            }

            // 仅故障注入：普通名现在是网络入口，必须真正绕过验证调用私有业务体才构成反例。
            PMNetReader r = new PMNetReader(payload);
            int p0 = r.ReadInt32();
            float p1 = r.ReadFloat();
            System.Reflection.MethodInfo body = typeof(E2eReplicated).GetMethod("PMNet_RpcBody_Fire",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (body == null) throw new InvalidOperationException("缺陷注入缺少编织业务体，不能空跑");
            body.Invoke(target, new object[] { p0, p1 });
        }

        /// <summary>可注入的复制层：`comparing=false` 时复刻「标脏即发、不做值比较」。</summary>
        private sealed class NoCompareChannel : PMReplicationChannel
        {
            public NoCompareChannel(PMRepOptions options)
                : base(options)
            {
            }

            protected override bool HasValueChangedSinceBaseline(
                PMNetObject obj, PMReplicationDescriptor descriptor, int slot, byte[] current, byte[] baseline)
            {
                return true;
            }
        }

        private static byte[] EncodeFire(int targetId, float angle)
        {
            PMNetWriter w = new PMNetWriter(32);
            w.WriteInt32(targetId);
            w.WriteFloat(angle);
            return w.ToArray();
        }

        private static void TestFaultInjection()
        {
            // 五个缺陷各配一对探针：
            //   prod*  = 【生产形态】（生成物 / 运行时真实现）—— 注入前后都应通过；
            //   faulty* = 【缺陷形态】（复刻返工前或历史上的错误实现）—— 只在注入后应失败。
            // 写入边界不允许改生成器与运行时，因此缺陷形态用**同形状的替身**实现；
            // 目的不是证明它们有 bug（§5.5 已记录被修掉），而是证明断言对那种形态真的会失败。
            TestConn genConn;
            E2eReplicated genSender = MakeRemoteSideObject(out genConn);
            FaultyArgFrameSender faulty = new FaultyArgFrameSender();

            Probe prodArg = new Probe("F1", "实参闭包（生成物实际形态）",
                delegate () { return ProbeArgClosure(
                    delegate () { PMNet.Generated.PMNetGeneratedRegistry.ClearPendingRpcs(); },
                    delegate (int t, float a) { genSender.Fire(t, a); },
                    delegate () {
                        PMNet.Generated.PMNetPendingRpc p;
                        if (!PMNet.Generated.PMNetGeneratedRegistry.TryDequeueRpc(out p)) { return null; }
                        return p;
                    },
                    delegate (object tok) {
                        PMNet.Generated.PMNetPendingRpc p = (PMNet.Generated.PMNetPendingRpc)tok;
                        PMNetWriter w = new PMNetWriter(32);
                        p.Write(p.Target, w);
                        return w.ToArray();
                    },
                    true); });

            Probe faultyArg = new Probe("F2", "实参帧（返工前形态）",
                delegate () { return ProbeArgClosure(
                    delegate () { faulty.Pending.Clear(); },
                    delegate (int t, float a) { faulty.Call(t, a); },
                    delegate () { object tok; return faulty.Take(out tok) ? tok : null; },
                    delegate (object tok) { return faulty.Encode(tok); },
                    true); });

            Probe prodReject = new Probe("F3", "Reject 后实现未执行（生成物形态）",
                delegate () { return ProbeRejectSkips(true); });

            Probe faultyReject = new Probe("F4", "Reject 后实现未执行（只记档位、不发射校验的形态）",
                delegate () { return ProbeRejectSkips(false); });

            Probe prodUnchanged = new Probe("F5", "值未变不发（正常复制层）",
                delegate () { return ProbeUnchangedSuppressed(null); });

            Probe faultyUnchanged = new Probe("F6", "值未变不发（不做值比较的复制层）",
                delegate () { return ProbeUnchangedSuppressed(typeof(NoCompareChannel)); });

            // ④ 接收入口不做归属校验（复刻 R2 首版：接收点根本没有这个落点）
            Probe prodOwnership = new Probe("F7", "非 Owner 投递被接收入口拒绝（生产形态）",
                delegate () { return ProbeOwnershipEnforced(true); });

            Probe faultyOwnership = new Probe("F8", "非 Owner 投递被执行（接收点无归属校验的形态）",
                delegate () { return ProbeOwnershipEnforced(false); });

            // ⑤ 只按裸 RpcId 定位 RPC（复刻旧形态：查目标之前先按 RpcId 全局查表）
            Probe prodComposite = new Probe("F9", "按 (ClassId, RpcId) 分派（生产形态）",
                delegate () { return ProbeCompositeKey(true); });

            Probe faultyComposite = new Probe("F10", "两个类共用同一 RpcId 时仍能定位执行器（只按裸 RpcId 查表的形态）",
                delegate () { return ProbeCompositeKey(false); });

            _senderFactory = null;

            List<string> cleanFailures = RunProbes(
                new List<Probe> { prodArg, prodReject, prodUnchanged, prodOwnership, prodComposite });
            Console.WriteLine("    [注入前] 五条生产形态探针（F1/F3/F5/F7/F9），失败 " + cleanFailures.Count + " 项");
            for (int i = 0; i < cleanFailures.Count; i++)
            {
                Console.WriteLine("      - " + cleanFailures[i]);
            }

            Check(cleanFailures.Count == 0,
                "S1 注入前四条生产形态探针全部通过（失败 " + cleanFailures.Count + " 项）");

            CheckInjection("① 实参被覆盖",
                "把实参暂存在对象字段上、编码推迟到发送时从字段读出（复刻返工前的生成物形态）",
                prodArg, faultyArg);

            CheckInjection("② Reject 后仍执行了实现",
                "接收侧只把档位写进描述符、不发射校验调用，直接调实现",
                prodReject, faultyReject);

            CheckInjection("③ 值未变却发了载荷",
                "复制层跳过值比较，标脏即视为已变化",
                prodUnchanged, faultyUnchanged);

            CheckInjection("④ 接收入口不做归属校验",
                "接收侧只读参数、直接调实现，不看连接/方向/归属（复刻 R2 首版的接收形态）",
                prodOwnership, faultyOwnership);

            CheckInjection("⑤ 只按裸 RpcId 定位 RPC",
                "查目标对象之前先按 RpcId 全局查表（两个类共用同一个 RpcId 时无法区分）",
                prodComposite, faultyComposite);

            _senderFactory = null;
        }

        /// <summary>
        /// 一条注入的完整对照：先只跑生产形态探针（= 注入前），再跑「生产 + 缺陷形态」（= 注入后）。
        /// 打印两次的失败数，并断言「缺陷形态探针失败、生产形态探针仍通过」。
        /// </summary>
        private static void CheckInjection(string name, string how, Probe clean, Probe faulty)
        {
            List<string> before = RunProbes(new List<Probe> { clean });
            List<string> after = RunProbes(new List<Probe> { clean, faulty });

            Console.WriteLine("    [注入 " + name + "] " + how);
            Console.WriteLine("      注入前失败 " + before.Count + " 项 → 注入后失败 " + after.Count + " 项");
            for (int i = 0; i < after.Count; i++)
            {
                Console.WriteLine("        - " + after[i]);
            }

            Check(before.Count == 0, name + " → 注入前生产形态探针 " + clean.Id + " 通过");

            bool hit = false;
            bool cleanStillPasses = true;
            for (int i = 0; i < after.Count; i++)
            {
                if (after[i].StartsWith(faulty.Id + " ", StringComparison.Ordinal))
                {
                    hit = true;
                }

                if (after[i].StartsWith(clean.Id + " ", StringComparison.Ordinal))
                {
                    cleanStillPasses = false;
                }
            }

            Check(hit, name + " → 命中：门禁抓住了该缺陷（探针 " + faulty.Id + " 失败）");
            if (!hit)
            {
                Console.WriteLine("      结论：**未命中** —— 该缺陷没有被门禁抓住，断言需要加强");
            }

            Check(cleanStillPasses, name + " → 对照探针 " + clean.Id
                + " 在注入下仍通过（说明失败确实来自注入，而不是整体崩掉）");
        }

        /// <summary>核心不变性的探针实现：返回 null 表示通过。</summary>
        private static string ProbeArgClosure(
            Action reset,
            Action<int, float> call,
            Func<object> takeFirst,
            Func<object, byte[]> encodeFirst,
            bool validating)
        {
            // 每次探针都从干净状态开始：待发队列里不得有上一条探针留下的残留，
            // 否则 FIFO 会让 takeFirst 取到错的项，断言就变成噪声。
            reset();

            // 第一次调用（实参 A = 11 / 1.5）。
            call(11, 1.5f);

            object token = takeFirst();
            if (token == null)
            {
                return "没有取出第一次的待发项";
            }

            // ★ 关键顺序：先取出待发项，再调第二次（不同实参），最后才编码第一次。
            call(99, 9.5f);

            byte[] bytes = encodeFirst(token);
            if (bytes == null || bytes.Length == 0)
            {
                return "第一次的编码没有产出字节";
            }

            E2eReplicated receiver = new E2eReplicated();
            InvokeFire(receiver, bytes, validating);

            if (receiver.LastFireTargetId != 11 || Math.Abs(receiver.LastFireAngle - 1.5f) > 1e-6f)
            {
                return "第一次的实参被覆盖：收到 " + receiver.LastFireTargetId + " / "
                       + receiver.LastFireAngle + "，期望 11 / 1.5";
            }

            return null;
        }

        /// <summary>
        /// 可注入的接收入口替身：**不做任何权限校验**，只读参数、直接调实现。
        ///
        /// 它复刻的是 R2 首版的真实形态（C2：RPC 接收入口不存在 ⇒ D-R0-42 在接收点没有落点），
        /// 目的不是证明生产实现有 bug，而是证明"非 Owner 不得执行"这条断言
        /// 对那种形态真的会失败（断言不是空的）。
        /// </summary>
        private static void NoGateDeliver(PMNetObject target, ushort rpcId, byte[] payload)
        {
            PMNetRpcEntry entry;
            if (!PMNetRegistry.TryGetRpc(rpcId, out entry) || entry == null)
            {
                return;
            }

            entry.Invoke(target, new PMNetReader(payload));
        }

        /// <summary>
        /// 可注入的 RPC 定位形态：
        /// - `composite = true`：走生产接收入口（按 **(目标对象的 ClassId, RpcId)** 定位）；
        /// - `composite = false`：复刻旧形态 —— 在查目标对象**之前**先按裸 RpcId 全局查表。
        ///
        /// 关键差别在"两个类共用同一个 RpcId"这个场景上：契约 §2.2 规定 RpcId 只在类内唯一，
        /// 因此旧形态要么在注册期直接失败、要么只能挑到其中一个类的实现（串线）。
        /// 返回 null = 通过。
        /// </summary>
        private static string ProbeCompositeKey(bool composite)
        {
            try
            {
                PMNetRegistry.Reset();
                PMNetRegistry.RegisterClass(BuildCompositeClass(CompositeClassA, "CompositeA",
                    new PMNetRpcEntry[]
                    {
                        BuildCompositeRpc(CompositeClassA, CompositeSharedRpcId, "SharedA", new PMRpcInvoker(CompositeInvokerA)),
                    }));
                PMNetRegistry.RegisterClass(BuildCompositeClass(CompositeClassB, "CompositeB",
                    new PMNetRpcEntry[]
                    {
                        BuildCompositeRpc(CompositeClassB, CompositeSharedRpcId, "SharedB", new PMRpcInvoker(CompositeInvokerB)),
                    }));

                _compositeMarkerA = 0;
                _compositeMarkerB = 0;

                E2eReplicated objA;
                TestConn connA;
                PMNetWorld worldA = NewCompositeWorld(CompositeClassA, out objA, out connA);

                if (composite)
                {
                    PMNetRpcReceiveResult r = DeliverRpc(
                        worldA, connA, CompositeSharedRpcId, objA, EncodeInt(11));

                    if (!r.Applied || _compositeMarkerA != 1 || _compositeMarkerB != 0)
                    {
                        return "按 (ClassId, RpcId) 投递后 A 类实现没有被（且仅被）执行一次：状态=" + r.Status
                               + " A=" + _compositeMarkerA + " B=" + _compositeMarkerB;
                    }

                    return null;
                }

                // 缺陷形态：不先看目标对象的 ClassId，直接按裸 RpcId 全局查表。
                PMNetRpcEntry legacy;
                if (!PMNetRegistry.TryGetRpc(CompositeSharedRpcId, out legacy) || legacy == null)
                {
                    return "只按裸 RpcId 全局查表：A/B 共用同一 ID ⇒ 查不到任何执行器，"
                           + "一条合法的 A 类调用会在这条路径上失败";
                }

                legacy.Invoke(objA, new PMNetReader(EncodeInt(11)));
                if (_compositeMarkerA != 1)
                {
                    return "只按裸 RpcId 全局查表：调给 A 类目标却执行了别的类实现（A="
                           + _compositeMarkerA + " B=" + _compositeMarkerB + "）";
                }

                return null;
            }
            finally
            {
                // 探针必须自净：注册表状态是全局共享的，不复原会把后面的断言变成噪声。
                PMNetRegistry.Reset();
                PMNet.Generated.PMNetGeneratedRegistry.RegisterAll();
            }
        }

        /// <summary>探针：非 Owner 连接投递 Server RPC 时，实现是否被执行。返回 null = 通过。</summary>
        private static string ProbeOwnershipEnforced(bool useRealEntry)
        {
            E2eReplicated obj;
            TestConn ownerConn;
            PMNetWorld world = NewReceiveWorld(out obj, out ownerConn);

            TestConn stranger = new TestConn(931, true);
            world.AddConnection(stranger);

            byte[] fire = EncodeFire(5, 1.0f);
            int before = obj.FireCount;

            if (useRealEntry)
            {
                PMNetRpcReceive.Deliver(
                    world, stranger, E2eReplicated.PMGeneratedRpcId_Fire, obj.NetId, fire, 0, fire.Length);
            }
            else
            {
                NoGateDeliver(obj, E2eReplicated.PMGeneratedRpcId_Fire, fire);
            }

            if (obj.FireCount != before)
            {
                return "非 Owner 连接投递 Server RPC 却执行了实现（FireCount "
                       + before + " → " + obj.FireCount + "）";
            }

            return null;
        }

        private static string ProbeRejectSkips(bool validating)
        {
            List<string> reported = new List<string>();
            Action<PMNetObject, ushort, PMRpcValidation, string> hook =
                delegate (PMNetObject t, ushort id, PMRpcValidation v, string n) { reported.Add(n + ":" + v); };

            PMRpcValidationSink.OnReported = hook;
            try
            {
                E2eReplicated r = new E2eReplicated();
                InvokeFire(r, EncodeFire(-1, 1.0f), validating);

                if (r.FireCount != 0)
                {
                    return "Reject 之后实现仍然被执行（FireCount=" + r.FireCount + "）";
                }

                if (validating && reported.Count != 1)
                {
                    return "Reject 没有上报（上报数 " + reported.Count + "）";
                }

                return null;
            }
            finally
            {
                PMRpcValidationSink.OnReported = null;
            }
        }

        private static string ProbeUnchangedSuppressed(Type channelType)
        {
            Func<PMRepOptions, PMReplicationChannel> saved = _senderFactory;
            try
            {
                if (channelType == null)
                {
                    _senderFactory = null;
                }
                else
                {
                    _senderFactory = delegate (PMRepOptions o)
                    {
                        return (PMReplicationChannel)Activator.CreateInstance(channelType, new object[] { o });
                    };
                }

                Rig rig = NewRig(1);
                RigClient c = rig.Clients[0];

                rig.ServerObj.PMNet_Set_health(42);
                SendAndDeliverAll(rig);

                rig.ServerObj.MarkPropertyDirty(SlotHealth);
                c.Conn.Sent.Clear();
                SendAll(rig);

                if (c.Conn.Sent.Count != 0)
                {
                    return "值未变却发了 " + c.Conn.Sent.Count + " 个载荷";
                }

                return null;
            }
            finally
            {
                _senderFactory = saved;
            }
        }
    }
}
