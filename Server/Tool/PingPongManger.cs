using System;
using System.Collections.Generic;
using System.Text;

namespace Server.Tool
{
    public class PingPongTool
    {
        public static bool isUserPing = true;
        public static long pingInterval =180 ;
        public static long GetTimeStamp()
        {
            TimeSpan ts = DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, 0, 0);
            return Convert.ToInt64(ts.TotalSeconds);
        }
    }
}
