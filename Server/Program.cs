using Logging;
using System;

namespace Server
{
    class Program
    {
        static void Main()
        {
			// 服务端日志写本地文件（每次启动新文件，方便直接读取分析）
			string _logSessionTime = DateTime.Now.ToString("yyyy-MM-dd_HH时mm分ss秒");
			string _traceLogPath = $"D:/unity/hyld-master/hyld-master/Server/log/{_logSessionTime}/server.log";
			Debug.TraceSavePath = _traceLogPath;
            Logging.Debug.Log("start");

            ServerConfig.MaxRoom3_3Number = 1;//一场战斗多少人才开始游戏
			ServerConfig.MaxTeam3_3Number = 1;//一个战斗多少个队伍
			//数据库设置
			ServerConfig.DOMConectStr = "database=hyld;data source=localhost;user=root;password=123456;pooling=false;CharSet=utf8mb4;port=3306;SslMode=None;AllowPublicKeyRetrieval=True";

			new Server(ServerConfig.TCPservePort);
			Debug.FlushTrace();
			Console.Read();
		}
    }
}
