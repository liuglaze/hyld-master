using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Google.Protobuf;
using PMNet.Server;
using SocketProto;

namespace PMUdpRouterTest
{
    /// <summary>
    /// P3'-1 功能测试：战斗 UDP 路由层（PMUdpRouter / PMSingleBattleRegistry）。
    ///
    /// 用**真实 UDP 回环**跑完整链路，不依赖 Unity、不依赖服务端进程。
    ///
    /// 关键点：测试自己扮演「主线程」——收到数据报后不立即处理，
    /// 而是显式调用 DrainAndDispatch 来分发，这与 PMDsHost.Update 的真实调用方式一致；
    /// 其中「线程纪律」用例专门断言 OnDatagramReceived 不会执行 handler。
    /// </summary>
    internal static class Program
    {
        private static int _checks;
        private static int _failures;
        private static readonly List<string> _failLines = new List<string>();

        private static int Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("===== PMUdpRouter 功能测试（P3'-1）=====");
            Console.WriteLine();

            try
            {
                RunAll();
            }
            catch (Exception e)
            {
                Fail("测试进程未预期地抛出异常", e.ToString());
            }

            Console.WriteLine();
            Console.WriteLine("== 汇总: " + _checks + " 项检查, " + _failures + " 项失败 ==");
            if (_failures > 0)
            {
                Console.WriteLine();
                Console.WriteLine("失败明细：");
                foreach (string line in _failLines)
                {
                    Console.WriteLine("  - " + line);
                }
            }

            return _failures == 0 ? 0 : 1;
        }

        // ==================== 用例 ====================

        private static void RunAll()
        {
            // ---- 场景 1：解析失败不崩溃 ----
            using (Harness h = new Harness())
            {
                h.SendRaw(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
                h.Pump();
                Check("垃圾字节 → 计为解析错误", h.Router.ParseErrors() >= 1,
                    "parseErr=" + h.Router.ParseErrors());
                Check("垃圾字节 → 不产生 handler 调用", h.HandlerInvocations == 0,
                    "invocations=" + h.HandlerInvocations);
            }
            // ---- 场景 2：线程纪律（本设计的核心性质）----
            using (Harness h = new Harness())
            {
                h.Send(MakeBattleReady(uid: 101, battlePlayerId: 1));
                h.WaitAllProcessed();

                Check("收到数据报后 handler 仍未被调用（后台只入队）",
                    h.HandlerInvocations == 0, "invocations=" + h.HandlerInvocations);
                Check("包仍在队列里等待主线程消费", h.Router.PendingInboundCount >= 1,
                    "pending=" + h.Router.PendingInboundCount);

                h.Router.DrainAndDispatch(64);
                Check("DrainAndDispatch 之后队列已空", h.Router.PendingInboundCount == 0,
                    "pending=" + h.Router.PendingInboundCount);
                Check("此时 handler 才被执行（分发发生在主线程调用点）",
                    h.HandlerInvocations == 1, "invocations=" + h.HandlerInvocations);
            }

            // ---- 场景 3：BattleReady 建链 + 端点覆写 ----
            using (Harness h = new Harness())
            {
                h.Send(MakeBattleReady(uid: 101, battlePlayerId: 1));
                h.Pump();
                Check("BattleReady 之后端点路由数 = 1", h.Router.EndpointRouteCount() == 1,
                    "routes=" + h.Router.EndpointRouteCount());
                Check("日志里出现建链记录（证明日志走主线程路径）",
                    h.ContainsLog("BattleReady 建链"), string.Join(" | ", h.Logs));
                Check("BattleReady 也交给战斗层（战斗层需要它登记就绪，与旧实现一致）",
                    h.HandlerInvocations == 1 && h.LastActionCode == ActionCode.BattleReady,
                    "invocations=" + h.HandlerInvocations + " action=" + h.LastActionCode);
            }

            // ---- 场景 4：未建链端点发业务包 → 不可路由 ----
            using (Harness h = new Harness())
            {
                h.Send(MakePlayerOperations(battlePlayerId: 1));
                h.Pump();
                Check("未建链端点的业务包 → 不可路由计数 +1", h.Router.UnroutablePackets() >= 1,
                    "unroutable=" + h.Router.UnroutablePackets());
                Check("未建链端点的业务包 → 不触发 handler", h.HandlerInvocations == 0,
                    "invocations=" + h.HandlerInvocations);
            }

            // ---- 场景 5：建链后业务包正常分发 ----
            using (Harness h = new Harness())
            {
                h.Send(MakeBattleReady(uid: 101, battlePlayerId: 1));
                h.Pump();
                h.Send(MakePlayerOperations(battlePlayerId: 1));
                h.Pump();

                Check("BattleReady + 业务包共分发 2 个", h.HandlerInvocations == 2,
                    "invocations=" + h.HandlerInvocations);
                Check("最后一次分发的 ActionCode 是业务包",
                    h.LastActionCode == ActionCode.BattlePushDowmPlayerOpeartions,
                    "action=" + h.LastActionCode);
                Check("分发计数 = 2", h.Router.DispatchedPackets() == 2,
                    "dispatched=" + h.Router.DispatchedPackets());
            }

            // ---- 场景 6：battlePlayerId 不匹配被拒 ----
            using (Harness h = new Harness())
            {
                h.Registry.RegisterPlayer(101, 2);   // 注册表说 uid=101 是本局 2 号
                h.Send(MakeBattleReady(uid: 101, battlePlayerId: 2));
                h.Pump();
                Check("BattleReady 用注册表分配的编号建链成功", h.Router.EndpointRouteCount() == 1,
                    "routes=" + h.Router.EndpointRouteCount());

                h.Send(MakePlayerOperations(battlePlayerId: 9));   // 冒充 9 号
                h.Pump();
                Check("业务包编号不匹配 → 被拒绝（只分发过 BattleReady）", h.HandlerInvocations == 1,
                    "invocations=" + h.HandlerInvocations);
                Check("业务包编号不匹配 → 计为不可路由", h.Router.UnroutablePackets() >= 1,
                    "unroutable=" + h.Router.UnroutablePackets());
            }

            // ---- 场景 7：BattleReady 编号与注册表不符 → 建链被拒 ----
            using (Harness h = new Harness())
            {
                h.Registry.RegisterPlayer(101, 2);
                h.Send(MakeBattleReady(uid: 101, battlePlayerId: 7));   // 谎称 7 号
                h.Pump();
                Check("BattleReady 编号与注册表不符 → 建链被拒", h.Router.EndpointRouteCount() == 0,
                    "routes=" + h.Router.EndpointRouteCount());
            }

            // ---- 场景 8：Ping/Pong 语义 ----
            using (Harness h = new Harness())
            {
                // 8a：未建链的端点 Ping → 不应答（避免成为开放反射器）
                h.Send(MakePing(timestamp: 12345));
                h.Pump();
                Check("未建链端点的 Ping → 不回 Pong", !h.Client.TryReceivePack(out _),
                    "收到了不应有的回包");
                Check("未建链端点的 Ping → 被计数（不再静默丢弃）", h.Router.UnroutedPings() >= 1,
                    "unroutedPing=" + h.Router.UnroutedPings());

                // 8b：建链后 Ping → 回 Pong 且 Timestamp 原样回带
                h.Send(MakeBattleReady(uid: 101, battlePlayerId: 1));
                h.Pump();
                h.Send(MakePing(timestamp: 999));
                h.Pump();

                MainPack pong;
                bool got = h.Client.TryReceivePack(out pong);
                Check("建链后 Ping → 收到回包", got, "未收到回包");
                if (got)
                {
                    Check("回包是 Pong", pong.Actioncode == ActionCode.Pong, "action=" + pong.Actioncode);
                    Check("Pong 原样回带 Timestamp", pong.Timestamp == 999, "timestamp=" + pong.Timestamp);
                }

                Check("Ping 不触发战斗 handler（Ping 在路由层就地应答）", h.HandlerInvocations == 1,
                    "invocations=" + h.HandlerInvocations + "（应为 1，即仅 BattleReady）");
            }

            // ---- 场景 9：handler 异常被隔离 ----
            using (Harness h = new Harness())
            {
                h.ThrowFromHandler = true;
                h.Send(MakeBattleReady(uid: 101, battlePlayerId: 1));
                h.Pump();
                h.Send(MakePlayerOperations(battlePlayerId: 1));

                bool threw = false;
                try
                {
                    h.Pump();
                }
                catch (Exception)
                {
                    threw = true;
                }

                Check("handler 抛异常不冒泡到主线程调用点", !threw, "异常冒泡了");
                Check("handler 异常被计数", h.Router.HandlerErrors() >= 1,
                    "handlerErr=" + h.Router.HandlerErrors());
            }

            // ---- 场景 10：重复注册被拒（旧实现是静默覆盖）----
            using (Harness h = new Harness())
            {
                bool again = h.Router.RegisterBattle(h.Registry.BattleId, h.OnBattlePacketPublic);
                Check("同一 battleId 重复注册 → 返回 false（不静默覆盖）", !again,
                    "第二次注册返回了 true");
                Check("注册数仍为 1", h.Router.RegisteredBattleCount == 1,
                    "battles=" + h.Router.RegisteredBattleCount);
            }

            // ---- 场景 11：UnregisterBattle 后不再分发 ----
            using (Harness h = new Harness())
            {
                h.Send(MakeBattleReady(uid: 101, battlePlayerId: 1));
                h.Pump();
                h.Router.UnregisterBattle(h.Registry.BattleId);
                Check("注销后端点路由被清空", h.Router.EndpointRouteCount() == 0,
                    "routes=" + h.Router.EndpointRouteCount());

                h.Send(MakePlayerOperations(battlePlayerId: 1));
                h.Pump();
                Check("注销后业务包不再分发（仍为注销前那 1 次 BattleReady）",
                    h.HandlerInvocations == 1, "invocations=" + h.HandlerInvocations);
                // 注销会同时清掉端点路由，所以这个包在**路由阶段**就被拒了（早于「有无 handler」）。
                // 这里要断言的是本质：包被计入某个可观测计数器，而不是静默消失
                // （旧实现的对应分支只打一行日志、无任何计数器，见 S6-D11）。
                Check("注销后的包被计入可观测计数器，不是静默丢弃",
                    h.Router.UnroutablePackets() >= 1 || h.Router.NoHandlerPackets() >= 1,
                    "unroutable=" + h.Router.UnroutablePackets()
                    + " noHandler=" + h.Router.NoHandlerPackets());
            }

            // ---- 场景 12：Send 永不抛出 ----
            using (Harness h = new Harness())
            {
                MainPack p = MakePing(1);
                Check("Send 到非法端点 → 返回 false 而非抛出", !h.Router.Send(p, "这不是端点"),
                    "返回了 true");

                h.Router.Send(p, "127.0.0.1:notaport");
                Check("Send 非法端口 → 返回 false 而非抛出", true, "");

                h.Router.Send(p, null);
                Check("Send null 端点 → 返回 false 而非抛出", true, "");
            }

            // ---- 场景 13：队列有界（洪泛不会撑爆内存）----
            using (Harness h = new Harness())
            {
                // 只灌不 drain：队列触顶后应开始丢弃
                for (int i = 0; i < 6000; i++)
                {
                    h.Send(MakePlayerOperations(battlePlayerId: 1));
                }

                h.WaitAllProcessed();
                Check("入站队列不会超过上限 4096", h.Router.PendingInboundCount <= 4096,
                    "pending=" + h.Router.PendingInboundCount);
                Check("队列触顶后发生丢弃（qFull > 0）", h.Router.QueueFullDrops() > 0,
                    "qFull=" + h.Router.QueueFullDrops());

                int drained = h.Router.DrainAndDispatch(100000);
                Check("排空后队列为空", h.Router.PendingInboundCount == 0,
                    "pending=" + h.Router.PendingInboundCount);
                Check("排空返回的分发数 > 0", drained > 0, "drained=" + drained);
            }

            // ---- 场景 14：未分配编号（DS 单局模式）仍可建链 ----
            using (Harness h = new Harness())
            {
                // 注册表未登记任何玩家：
                // 默认 AllowUnassignedPlayers=true，TryGetBattlePlayerId 返回 0（未分配）
                h.Send(MakeBattleReady(uid: 55, battlePlayerId: 3));
                h.Pump();
                Check("未登记玩家且允许未分配 → 建链成功（跳过编号校验）",
                    h.Router.EndpointRouteCount() == 1, "routes=" + h.Router.EndpointRouteCount());

                // 关闭宽容后应拒绝新 uid
                h.Registry.AllowUnassignedPlayers = false;
                using (Harness h2 = new Harness())
                {
                    h2.Registry.AllowUnassignedPlayers = false;
                    h2.Send(MakeBattleReady(uid: 66, battlePlayerId: 1));
                    h2.Pump();
                    Check("禁止未分配后 → 建链被拒", h2.Router.EndpointRouteCount() == 0,
                        "routes=" + h2.Router.EndpointRouteCount());
                }
            }

            // ---- 场景 15：注册表按 uid 正确分配编号 ----
            {
                PMSingleBattleRegistry reg = new PMSingleBattleRegistry();
                reg.RegisterPlayer(11, 1);
                reg.RegisterPlayer(22, 2);

                int id;
                Check("注册表 uid=22 → battlePlayerId=2", reg.TryGetBattlePlayerId(22, out id) && id == 2,
                    "id=" + id);

                reg.AllowUnassignedPlayers = false;
                Check("注册表未知 uid 且禁止未分配 → 返回 false",
                    !reg.TryGetBattlePlayerId(33, out id), "意外返回 true");

                int bid;
                Check("注册表 uid→battleId 返回本进程战斗编号",
                    reg.TryGetBattleIdByUid(11, out bid) && bid == reg.BattleId, "battleId=" + bid);
                Check("注册表 uid<=0 → 返回 false", !reg.TryGetBattleIdByUid(0, out bid), "意外返回 true");
            }
        }

        // ==================== 构造包 ====================

        private static MainPack MakeBattleReady(int uid, int battlePlayerId)
        {
            MainPack pack = new MainPack();
            pack.Actioncode = ActionCode.BattleReady;

            BattlePlayerPack player = new BattlePlayerPack();
            player.Id = uid;                 // 账号级 uid
            player.Battleid = battlePlayerId; // 本局内编号
            pack.Battleplayerpack.Add(player);
            return pack;
        }

        private static MainPack MakePlayerOperations(int battlePlayerId)
        {
            MainPack pack = new MainPack();
            pack.Actioncode = ActionCode.BattlePushDowmPlayerOpeartions;

            BattleInfo info = new BattleInfo();
            BattleClientInput input = new BattleClientInput();
            input.BattlePlayerId = battlePlayerId;
            info.ClientInput = input;
            pack.BattleInfo = info;
            return pack;
        }

        private static MainPack MakePing(long timestamp)
        {
            MainPack pack = new MainPack();
            pack.Actioncode = ActionCode.Ping;
            pack.Timestamp = timestamp;
            return pack;
        }

        // ==================== 测试夹具 ====================

        /// <summary>
        /// 把「一个 DS 进程 + 一个客户端」的最小形态搭起来：
        /// 服务端 socket 绑回环随机端口，客户端 socket 与之对话。
        /// 测试自己扮演主线程，显式 Pump 驱动「收包 → drain → dispatch」。
        /// </summary>
        private sealed class Harness : IDisposable
        {
            private readonly Socket _serverSocket;
            private readonly List<string> _logs = new List<string>();
            private readonly object _logLock = new object();

            public readonly Socket Client;
            public readonly PMSingleBattleRegistry Registry;
            public readonly PMUdpRouter Router;

            public int HandlerInvocations;
            public ActionCode LastActionCode;
            public bool ThrowFromHandler;

            public Harness()
            {
                _serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _serverSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                int port = ((IPEndPoint)_serverSocket.LocalEndPoint).Port;

                Client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                Client.Connect(new IPEndPoint(IPAddress.Loopback, port));   // 便于用 Send/Receive

                Registry = new PMSingleBattleRegistry();
                Router = new PMUdpRouter(_serverSocket, Registry, Capture, Capture, Capture);
                Router.RegisterBattle(Registry.BattleId, OnBattlePacket);

                BeginReceive();
            }

            private void Capture(string message)
            {
                lock (_logLock)
                {
                    _logs.Add(message);
                }
            }

            public List<string> Logs
            {
                get
                {
                    lock (_logLock)
                    {
                        return new List<string>(_logs);
                    }
                }
            }

            // ---- 接收泵：模拟 PMDsHost.OnReceive（后台线程只把字节交给路由层）----

            private byte[] _buffer = new byte[64 * 1024];
            private EndPoint _from = new IPEndPoint(IPAddress.Any, 0);

            /// <summary>已发出的包数（测试线程写）。</summary>
            private int _sent;

            /// <summary>
            /// 已交给路由层处理的包数。**必须在 OnDatagramReceived 返回之后**才递增：
            /// 若与接收同一个时序点递增，等待方会在「字节已收到但还没解析入队」的窗口里提前返回，
            /// 于是断言看到的是一个半成品状态（这正是本测试第一版误报 9 项的原因）。
            /// </summary>
            private int _processed;

            private void BeginReceive()
            {
                _serverSocket.BeginReceiveFrom(_buffer, 0, _buffer.Length, SocketFlags.None, ref _from, OnReceive, null);
            }

            private void OnReceive(IAsyncResult ar)
            {
                try
                {
                    EndPoint from = _from;
                    int n = _serverSocket.EndReceiveFrom(ar, ref from);
                    if (n > 0)
                    {
                        Router.OnDatagramReceived(_buffer, n, from);
                        Interlocked.Increment(ref _processed);
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    // 忽略
                }
                finally
                {
                    try
                    {
                        BeginReceive();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }

            /// <summary>发送一个 protobuf 包（测试线程）。</summary>
            public void Send(MainPack pack)
            {
                Interlocked.Increment(ref _sent);
                Client.Send(pack.ToByteArray());
            }

            /// <summary>发送原始字节（测试线程）。</summary>
            public void SendRaw(byte[] bytes)
            {
                Interlocked.Increment(ref _sent);
                Client.Send(bytes);
            }

            /// <summary>等待所有已发出的包都已被路由层处理过（解析+入队或计数）。</summary>
            public bool WaitAllProcessed()
            {
                int target = Interlocked.CompareExchange(ref _sent, 0, 0);
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline)
                {
                    if (Interlocked.CompareExchange(ref _processed, 0, 0) >= target)
                    {
                        return true;
                    }

                    Thread.Sleep(2);
                }

                return Interlocked.CompareExchange(ref _processed, 0, 0) >= target;
            }

            /// <summary>
            /// 一次主线程迭代：等全部已发包抵达并被路由层入队，然后分发。
            /// 对应真实 DS 里的 PMDsHost.Update（等包不需要，但分发时机一致）。
            /// </summary>
            public void Pump()
            {
                WaitAllProcessed();
                Router.DrainAndDispatch(1000000);
                Thread.Sleep(5);
            }

            private void OnBattlePacket(MainPack pack)
            {
                HandlerInvocations++;
                LastActionCode = pack != null ? pack.Actioncode : ActionCode.ActionNone;
                if (ThrowFromHandler)
                {
                    throw new InvalidOperationException("测试故意让 handler 抛异常");
                }
            }

            /// <summary>供「重复注册」用例复用的同一委托实例。</summary>
            public PMUdpRouter.PacketHandler OnBattlePacketPublic
            {
                get { return OnBattlePacket; }
            }

            /// <summary>日志（注入回调的实现）里是否包含某段文本。</summary>
            public bool ContainsLog(string fragment)
            {
                lock (_logLock)
                {
                    for (int i = 0; i < _logs.Count; i++)
                    {
                        if (_logs[i] != null && _logs[i].IndexOf(fragment, StringComparison.Ordinal) >= 0)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            public void Dispose()
            {
                try
                {
                    Client.Close();
                }
                catch (Exception)
                {
                }

                try
                {
                    _serverSocket.Close();
                }
                catch (Exception)
                {
                }
            }
        }

        // ==================== 客户端辅助 ====================

        private static void SendPack(this Socket socket, MainPack pack)
        {
            socket.Send(pack.ToByteArray());
        }

        private static bool TryReceivePack(this Socket socket, out MainPack pack)
        {
            pack = null;
            if (!socket.Poll(300000, SelectMode.SelectRead))   // 300ms
            {
                return false;
            }

            byte[] buf = new byte[64 * 1024];
            int n = socket.Receive(buf);
            if (n <= 0)
            {
                return false;
            }

            pack = MainPack.Parser.ParseFrom(buf, 0, n);
            return true;
        }

        // ==================== 断言 ====================

        private static void Check(string name, bool ok, string detail)
        {
            _checks++;
            if (ok)
            {
                Console.WriteLine("  [OK]   " + name);
            }
            else
            {
                _failures++;
                _failLines.Add(name + "  → " + detail);
                Console.WriteLine("  [FAIL] " + name + "  → " + detail);
            }
        }

        private static void Fail(string name, string detail)
        {
            Check(name, false, detail);
        }
    }
}
