using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Logging
{
    public static class Debug
    {
        public static string prefix = "";
        public static string TraceSavePath;
        public static int traceDumpLen = 128 * 1024;

        private static readonly ConcurrentQueue<string> _traceQueue = new ConcurrentQueue<string>();
        private static readonly AutoResetEvent _traceSignal = new AutoResetEvent(false);
        private static readonly ManualResetEventSlim _flushComplete = new ManualResetEventSlim(true);
        private static readonly object _writerLock = new object();
        private static readonly StringBuilder _traceSb = new StringBuilder();
        private static Stream _stream;
        private static Thread _writerThread;
        private static long _queuedCount;
        private static long _writtenCount;

        public static void Trace(string msg, bool isNeedLogTrace = false, bool isNewLine = true)
        {
            StringBuilder sb = new StringBuilder();
            if (isNewLine)
                sb.AppendLine(msg);
            else
                sb.Append(msg);

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
                    sb.AppendLine($"at {typeName}  ::  {methodName}   in   {fileName}   :line   {lineNum}");
                }
            }

            EnsureWriterThread();
            _flushComplete.Reset();
            Interlocked.Increment(ref _queuedCount);
            _traceQueue.Enqueue(sb.ToString());
            _traceSignal.Set();
        }

        public static void FlushTrace()
        {
            if (string.IsNullOrEmpty(TraceSavePath))
                return;

            EnsureWriterThread();
            _traceSignal.Set();
            while (Volatile.Read(ref _writtenCount) < Volatile.Read(ref _queuedCount))
            {
                _flushComplete.Wait(100);
            }

            lock (_writerLock)
            {
                FlushBufferToDisk();
                _stream?.Flush();
            }
        }

        private static void EnsureWriterThread()
        {
            if (_writerThread != null)
                return;

            lock (_writerLock)
            {
                if (_writerThread != null)
                    return;

                _writerThread = new Thread(LogWriterLoop)
                {
                    IsBackground = true,
                    Name = "ServerLogWriter",
                };
                _writerThread.Start();
            }
        }

        private static void LogWriterLoop()
        {
            while (true)
            {
                DrainTraceQueue();
                _traceSignal.WaitOne(100);
            }
        }

        private static void DrainTraceQueue()
        {
            lock (_writerLock)
            {
                int drained = 0;
                while (_traceQueue.TryDequeue(out string msg))
                {
                    _traceSb.Append(msg);
                    drained++;
                    Interlocked.Increment(ref _writtenCount);

                    if (_traceSb.Length > traceDumpLen)
                        FlushBufferToDisk();
                }

                if (drained > 0)
                    FlushBufferToDisk();

                if (Volatile.Read(ref _writtenCount) >= Volatile.Read(ref _queuedCount))
                    _flushComplete.Set();
            }
        }

        private static void FlushBufferToDisk()
        {
            if (string.IsNullOrEmpty(TraceSavePath) || _traceSb.Length == 0)
                return;

            if (_stream == null)
            {
                var dir = Path.GetDirectoryName(TraceSavePath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // FileShare.ReadWrite 是必需的，不是优化：
                // File.Open(path, FileMode.OpenOrCreate, FileAccess.Write) 的重载会用默认的
                // FileShare.None，把日志文件**独占锁定**——服务端在跑的时候，
                // cat/cp/tail/编辑器全都读不到（表现为「文件有大小但读到 0 字节」或 Device busy）。
                // 联调时无法查看运行中的日志是致命的运维缺陷，因此这里显式允许共享读。
                _stream = new FileStream(
                    TraceSavePath,
                    FileMode.OpenOrCreate,
                    FileAccess.Write,
                    FileShare.ReadWrite);
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

        public static void Log(object message, LogSeverity severity = LogSeverity.Info, params object[] args)
        {
            string formatted = message == null ? "" : message.ToString();
            if (args != null && args.Length > 0)
            {
                object[] formattedArgs = new object[args.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    var arg = args[i];
                    formattedArgs[i] = arg != null ? $"{arg.GetType().Name}: {arg}" : "null";
                }
                formatted = string.Format(formatted, formattedArgs);
            }

            string fullMsg = prefix + formatted;
            string timestampedMsg = $"[{DateTime.Now:HH:mm:ss.fff}] {fullMsg}";
            Trace(timestampedMsg);
            if (OnMessage != null && (LogSeverityLevel & severity) != 0)
                OnMessage.Invoke(null, new LogEventArgs(severity, fullMsg));
        }

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
