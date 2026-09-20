using Logging;
using System;
using System.IO;
using System.Threading;

namespace Server
{
    class Program
    {
        static void Main()
        {
            // 服务端日志写本地文件（每次启动新文件，方便直接读取分析）
            string sessionName = DateTime.Now.ToString("yyyy-MM-dd_HH时mm分ss秒");
            string logDir = ResolveLogDirectory();
            string traceLogPath = Path.Combine(logDir, sessionName, "server.log");
            Debug.TraceSavePath = traceLogPath;

            Logging.Debug.Log("start");
            Logging.Debug.Log("[Program] 服务端日志文件 = " + traceLogPath);

            // 进程身份与退出路径日志。
            //
            // 背景：联调期间多次出现「服务端起来了、跑了六七分钟、然后端口静默消失」，
            // 而日志里没有任何异常。要在「自己退出」与「被外部结束」之间做出区分，
            // 就必须把能记录的退出路径都记下来：
            //   - 若日志以 [Program] Main 返回 结尾  → 是自己走的（保活失效，代码问题）
            //   - 若日志以 [Program] ProcessExit 结尾 → 是收到正常退出信号
            //   - 若日志什么都不打就断掉        → 是被 TerminateProcess 强杀（外部/作业对象）
            // 没有这一行的话，上面三种情况在事后完全无法区分。
            Logging.Debug.Log("[Program] pid=" + System.Diagnostics.Process.GetCurrentProcess().Id
                + " 启动于 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
            {
                Logging.Debug.Log("[Program] ProcessExit 触发（收到正常退出信号）");
                Debug.FlushTrace();
            };

            ServerConfig.MaxRoom3_3Number = 1;//一场战斗多少人才开始游戏
            ServerConfig.MaxTeam3_3Number = 1;//一个战斗多少个队伍

            // 说明：原先这里设置 ServerConfig.DOMConectStr（MySQL 连接串）。
            // 账号数据已改为进程内内存库（Server/DAO/UserStore.cs），不再需要任何数据库配置。
            Logging.Debug.Log($"[Program] 用户存储 = 内存实现（内存账号数={DAO.UserStore.UserCount}）");

            new Server(ServerConfig.TCPservePort);
            Debug.FlushTrace();

            // 保活方式。
            //
            // 原实现只用 Console.Read()：在交互式控制台里没问题，
            // 但当 stdin 被重定向（服务化、CI、后台拉起、`start /B` 重定向日志等）时，
            // Console.Read() 会立刻返回 -1，Main 随即返回，进程「启动成功但秒退」——
            // 日志里能看到「启动监听成功」，但端口马上消失，极难定位。
            //
            // 迁移计划里 Lobby/DS 都需要能被非交互地拉起，所以这里分开处理。
            if (Console.IsInputRedirected)
            {
                Logging.Debug.Log("[Program] stdin 被重定向（非交互运行），改用无限等待保持进程常驻；外部终止即可退出");
                Debug.FlushTrace();
                Thread.Sleep(Timeout.Infinite);
            }
            else
            {
                Console.Read();
            }

            Debug.FlushTrace();
            Logging.Debug.Log("[Program] Main 返回（保活结束）——若非外部终止，说明保活逻辑失效");
            Debug.FlushTrace();
        }

        /// <summary>
        /// 推导日志根目录。
        ///
        /// 为什么需要这段逻辑：
        ///   原实现把路径硬编码为 `D:/unity/hyld-master/hyld-master/Server/log/...`，
        ///   那是某台旧开发机的绝对路径。工程换位置后，日志会被写到一个与代码无关的目录树里，
        ///   排查时根本找不到（而且因为父目录会被自动创建，失败还不会报错）。
        ///
        /// 推导顺序：
        ///   1) 从可执行文件所在目录向上找「同时含 Server 与 Client 子目录」的那一层，即工程根；
        ///      找到就用 <工程根>/Server/log —— 与 Server/AGENTS.md 记录的位置约定一致。
        ///   2) 找不到（例如只拷了产物到别处运行）就退回 <可执行文件目录>/log。
        /// 两条路都会把最终路径打进日志，避免再次出现「日志不知道去哪了」。
        /// </summary>
        private static string ResolveLogDirectory()
        {
            try
            {
                DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    if (Directory.Exists(Path.Combine(dir.FullName, "Server")) &&
                        Directory.Exists(Path.Combine(dir.FullName, "Client")))
                    {
                        return Path.Combine(dir.FullName, "Server", "log");
                    }

                    dir = dir.Parent;
                }
            }
            catch (Exception ex)
            {
                Logging.Debug.Log("[Program] 推导日志目录失败，退回可执行文件目录：" + ex.Message);
            }

            return Path.Combine(AppContext.BaseDirectory, "log");
        }
    }
}
