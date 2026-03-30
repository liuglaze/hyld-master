using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Logging
{
    public static class Debug
    {
        public static string prefix = "";
        public static string TraceSavePath;
        public static int traceDumpLen = 128 * 1024;
        static StringBuilder _traceSb = new StringBuilder();
        private static Stream _stream;

        public static void Trace(string msg, bool isNeedLogTrace = false, bool isNewLine = true)
        {
            if (isNewLine)
                _traceSb.AppendLine(msg);
            else
                _traceSb.Append(msg);

            if (isNeedLogTrace)
            {
                StackTrace st = new StackTrace(true);
                StackFrame[] sf = st.GetFrames();
                for (int i = 0; i < sf.Length; ++i)
                {
                    var frame = sf[i];
                    var typeName = frame.GetMethod()?.DeclaringType?.FullName ?? "null";
                    var methodName = frame.GetMethod()?.Name ?? "null";
                    var fileName = frame.GetFileName() ?? "unknown";
                    var lineNum = frame.GetFileLineNumber();
                    _traceSb.AppendLine($"at {typeName}  ::  {methodName}   in   {fileName}   :line   {lineNum}");
                }
            }
            if (_traceSb.Length > traceDumpLen)
                FlushTrace();
        }

        public static void FlushTrace()
        {
            if (string.IsNullOrEmpty(TraceSavePath))
                return;
            if (_stream == null)
            {
                var dir = Path.GetDirectoryName(TraceSavePath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                _stream = File.Open(TraceSavePath, FileMode.OpenOrCreate, FileAccess.Write);
            }
            var bytes = UTF8Encoding.Default.GetBytes(_traceSb.ToString());
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
            _traceSb.Clear();
        }
        [Flags]
        public enum LogSeverity
        {
            Exception = 1,
            Error = 2,
            Warn = 4,
            Info = 8,
        }

        public class LogEventArgs : EventArgs
        {
            public LogSeverity LogSeverity { get; }
            public string Message { get; }
            public LogEventArgs(LogSeverity logSeverity, string message)
            {
                LogSeverity = logSeverity;
                Message = message;
            }
        }

        public static LogSeverity LogSeverityLevel =
            LogSeverity.Info | LogSeverity.Warn | LogSeverity.Error | LogSeverity.Exception;

        public static event EventHandler<LogEventArgs> OnMessage = DefaultServerLogHandler;

        // 统一主接口
        public static void Log(object message, LogSeverity severity = LogSeverity.Info, params object[] args)
        {
            string formatted = message == null ? "" : message.ToString();
            if (args != null && args.Length > 0)
            {
                object[] formattedArgs = new object[args.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    var arg = args[i];
                    // 为每个参数添加类名，以提高可读性
                    formattedArgs[i] = arg != null ? $"{arg.GetType().Name}: {arg}" : "null";
                }
                formatted = string.Format(formatted, formattedArgs);
            }
            string fullMsg = prefix + formatted;
            // 同时写入文件（复用 Trace 的文件缓冲机制）
            string timestampedMsg = $"[{DateTime.Now:HH:mm:ss.fff}] {fullMsg}";
            Trace(timestampedMsg);
            if (OnMessage != null && (LogSeverityLevel & severity) != 0)
                OnMessage.Invoke(null, new LogEventArgs(severity, fullMsg));
        }


        // 默认日志输出（带堆栈）
        public static void DefaultServerLogHandler(object sender, LogEventArgs logArgs)
        {
            if ((LogSeverity.Error & logArgs.LogSeverity) != 0
                || (LogSeverity.Exception & logArgs.LogSeverity) != 0)
            {
                StackTrace st = new StackTrace(true);
                StackFrame[] sf = st.GetFrames();
                StringBuilder sb = new StringBuilder();
                for (int i = 4; i < sf.Length; ++i)
                {
                    var frame = sf[i];
                    var typeName = frame.GetMethod()?.DeclaringType?.FullName ?? "null";
                    var methodName = frame.GetMethod()?.Name ?? "null";
                    var fileName = frame.GetFileName() ?? "unknown";
                    var lineNum = frame.GetFileLineNumber();
                    sb.AppendLine($"{typeName}::{methodName} Line={lineNum} File={fileName}");
                }
                Console.WriteLine(sb.ToString());
            }
            Console.WriteLine(logArgs.Message);
        }

        
    }
}
