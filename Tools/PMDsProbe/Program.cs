using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Google.Protobuf;
using SocketProto;

namespace PMDsProbe
{
    /// <summary>
    /// DS 协议探针：向一个运行中的 HyldDS 发送真实 protobuf 包，并报告它是否被正确处理。
    ///
    /// 用法：
    ///   PMDsProbe &lt;host&gt; &lt;port&gt; [uid] [battlePlayerId]
    ///   例：PMDsProbe 127.0.0.1 7801 101 1
    ///
    /// 它会按真实客户端的行为顺序发四步，并在每一步之间留出时间让 DS 的主线程消费：
    ///   1. Ping（未建链）→ 期望**没有**回包（路由层只在已建链端点上回 Pong，避免成为开放反射器）
    ///   2. BattleReady     → 期望建链成功（DS 日志出现「BattleReady 建链」）
    ///   3. Ping（已建链）  → 期望收到 Pong，且 Timestamp 原样回带
    ///   4. 业务包（移动上行）→ 期望被分发（DS 日志出现「战斗包已分发」）
    ///
    /// 本工具**只看网络侧可观测的结果**；「建链」「分发」这类服务端内部状态需要配合 DS 日志核对，
    /// 所以每一步都打印对应的日志关键字，便于直接到 ds_*.log 里 grep。
    /// </summary>
    internal static class Program
    {
        private static int _step;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            if (args.Length < 2)
            {
                Console.WriteLine("用法: PMDsProbe <host> <port> [uid] [battlePlayerId]");
                Console.WriteLine("  例: PMDsProbe 127.0.0.1 7801 101 1");
                return 2;
            }

            string host = args[0];
            int port = int.Parse(args[1]);
            int uid = args.Length > 2 ? int.Parse(args[2]) : 101;
            int battlePlayerId = args.Length > 3 ? int.Parse(args[3]) : 1;

            Console.WriteLine("===== PMDsProbe：向 " + host + ":" + port + " 探测 =====");
            Console.WriteLine("  uid=" + uid + " battlePlayerId=" + battlePlayerId);
            Console.WriteLine();

            using (Socket sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                sock.Bind(new IPEndPoint(IPAddress.Any, 0));
                sock.Connect(new IPEndPoint(IPAddress.Parse(host), port));

                // ---- 步骤 1：未建链的 Ping 不应有回包 ----
                Step("1. 未建链的 Ping（期望：无回包 —— 路由层只在已建链端点上回 Pong）");
                Send(sock, MakePing(111));
                MainPack reply;
                bool got = TryReceive(sock, out reply, 600);
                Report(!got,
                    got ? "收到了不应有的回包（action=" + reply.Actioncode + "）" : "无回包，符合预期",
                    "DS 日志应出现: 无法路由 UDP 包: ActionCode=Ping");

                // ---- 步骤 2：BattleReady 建链 ----
                Step("2. BattleReady 建链（期望：DS 建立端点→战斗路由）");
                Send(sock, MakeBattleReady(uid, battlePlayerId));
                Thread.Sleep(500);
                Report(true, "已发送（网络侧无回包，需看 DS 日志确认）",
                    "DS 日志应出现: [PMUdpRouter] BattleReady 建链: uid=" + uid);

                // ---- 步骤 3：已建链的 Ping 应回 Pong ----
                Step("3. 已建链的 Ping（期望：收到 Pong，Timestamp 原样回带）");
                Send(sock, MakePing(222));
                got = TryReceive(sock, out reply, 1500);
                if (!got)
                {
                    Report(false, "未收到 Pong", "确认 DS 是否在运行、端口是否正确");
                }
                else
                {
                    bool isPong = reply.Actioncode == ActionCode.Pong;
                    bool tsOk = reply.Timestamp == 222;
                    Report(isPong && tsOk,
                        "ActionCode=" + reply.Actioncode + " Timestamp=" + reply.Timestamp,
                        "Pong 的 Timestamp 必须与发送时一致");
                }

                // ---- 步骤 4：业务包应被分发 ----
                Step("4. 业务包（移动上行，期望：被分发到战斗 handler）");
                Send(sock, MakePlayerOperations(battlePlayerId));
                Thread.Sleep(500);
                Report(true, "已发送（网络侧无回包，需看 DS 日志确认）",
                    "DS 日志应出现: [PMDsHost] 战斗包已分发 N 个，最近 ActionCode=BattlePushDowmPlayerOpeartions");

                Console.WriteLine();
                Console.WriteLine("提示：DS 内部状态的唯一观测点是它自己的日志；本工具只验证网络侧行为。");
                Console.WriteLine("在 DS 心跳行（每 5 秒一行）里可以看到：");
                Console.WriteLine("  [PMDsHost] router recv=N queued=N dispatched=N pending=N unroutable=N ...");
            }

            Console.WriteLine();
            Console.WriteLine("===== 探测结束 =====");
            return 0;
        }

        // ==================== 构造包 ====================

        private static MainPack MakePing(long timestamp)
        {
            MainPack pack = new MainPack();
            pack.Actioncode = ActionCode.Ping;
            pack.Timestamp = timestamp;
            return pack;
        }

        private static MainPack MakeBattleReady(int uid, int battlePlayerId)
        {
            MainPack pack = new MainPack();
            pack.Actioncode = ActionCode.BattleReady;

            BattlePlayerPack player = new BattlePlayerPack();
            player.Id = uid;                  // 账号级 uid
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

        // ==================== 收发与输出 ====================

        private static void Send(Socket sock, MainPack pack)
        {
            byte[] bytes = pack.ToByteArray();
            sock.Send(bytes);
            Console.WriteLine("     → 已发送 " + bytes.Length + " 字节, ActionCode=" + pack.Actioncode);
        }

        private static bool TryReceive(Socket sock, out MainPack pack, int timeoutMs)
        {
            pack = null;
            if (!sock.Poll(timeoutMs * 1000, SelectMode.SelectRead))
            {
                return false;
            }

            byte[] buf = new byte[64 * 1024];
            int n = sock.Receive(buf);
            if (n <= 0)
            {
                return false;
            }

            pack = MainPack.Parser.ParseFrom(buf, 0, n);
            return true;
        }

        private static void Step(string title)
        {
            _step++;
            Console.WriteLine("[" + _step + "] " + title);
        }

        private static void Report(bool ok, string detail, string hint)
        {
            Console.WriteLine("     " + (ok ? "[OK]  " : "[FAIL]") + " " + detail);
            if (!ok && !string.IsNullOrEmpty(hint))
            {
                Console.WriteLine("           排查提示: " + hint);
            }
            else if (ok && !string.IsNullOrEmpty(hint))
            {
                Console.WriteLine("           核对: " + hint);
            }
        }
    }
}
