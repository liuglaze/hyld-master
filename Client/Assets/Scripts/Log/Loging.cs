using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Logging
{
    public class HYLDDebug
    {
        // ==== 配置字段 ====
        public static string prefix = "";
        public static string TraceSavePath;
        public static string FrameTraceSavePath;
        public static int traceDumpLen = 128 * 1024;
        public static int frameTraceDumpLen = 256 * 1024;

        private static StringBuilder _traceSb = new StringBuilder();
        private static StringBuilder _frameTraceSb = new StringBuilder();
        private static Stream _stream;
        private static Stream _frameTraceStream;
        private static readonly object _traceLock = new object();
        private static readonly object _frameTraceLock = new object();

        // ==== 分文件帧日志 ====
        /// <summary>日志目录，由 HYLDManger.Awake 设置（和 FrameTraceSavePath 同目录）</summary>
        public static string FrameLogDirectory;

        // 各分类的 StringBuilder + Stream
        private static StringBuilder _summaryFrameSb = new StringBuilder();     // 每帧一行摘要
        private static StringBuilder _reconcileFrameSb = new StringBuilder();   // 和解详情（仅异常帧）
        private static StringBuilder _authorityFrameSb = new StringBuilder();   // 权威帧对比
        private static StringBuilder _playerStateFrameSb = new StringBuilder(); // 玩家坐标/状态

        private static Stream _summaryStream;
        private static Stream _reconcileStream;
        private static Stream _authorityStream;
        private static Stream _playerStateStream;

        private static readonly object _frameLogLock = new object();

        // 当前帧累积状态
        private static int _currentFrameId = -1;
        private static bool _frameHasMismatch;
        private static bool _frameHasReplay;
        private static bool _frameHasMissing;
        private static int _frameEntryCount;
        // 当前帧的详细日志（只在异常时写入分类文件，正常帧丢弃）
        private static StringBuilder _frameDetailBuffer = new StringBuilder();

        // ==== 日志等级相关 ====
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

        private static LogSeverity LogSeverityLevel =
            LogSeverity.Info | LogSeverity.Warn | LogSeverity.Error | LogSeverity.Exception;

        private static event EventHandler<LogEventArgs> OnMessage = DefaultServerLogHandler;

        public static void SetLogAllSeverities()
        {
            LogSeverityLevel = LogSeverity.Info | LogSeverity.Warn | LogSeverity.Error | LogSeverity.Exception;
        }

        // ==== 公共主接口 ====
        public static void Log(object obj, params object[] args)
        {
            LogMessage(null, LogSeverity.Info, prefix + obj?.ToString(), args);
        }
        public static void Log(string format, params object[] args)
        {
            LogMessage(null, LogSeverity.Info, prefix + format, args);
        }
        public static void LogFormat(string format, params object[] args)
        {
            LogMessage(null, LogSeverity.Info, prefix + format, args);
        }
        public static void LogWarning(object format, params object[] args)
        {
            LogMessage(null, LogSeverity.Warn, prefix + format?.ToString(), args);
        }
        public static void LogWarning(string format, params object[] args)
        {
            LogMessage(null, LogSeverity.Warn, prefix + format, args);
        }
        public static void LogError(object format, params object[] args)
        {
            LogMessage(null, LogSeverity.Error, prefix + format?.ToString(), args);
        }
        public static void LogError(string format, params object[] args)
        {
            LogMessage(null, LogSeverity.Error, prefix + format, args);
        }
        public static void LogError(Exception e)
        {
            LogMessage(null, LogSeverity.Error, prefix + e?.ToString());
        }
        public static void LogErrorFormat(string format, params object[] args)
        {
            LogMessage(null, LogSeverity.Error, prefix + format, args);
        }

        [Conditional("DEBUG")]
        public static void Assert(bool val, string msg = "")
        {
            if (!val)
                LogMessage(null, LogSeverity.Error, prefix + "AssertFailed!!! " + msg);
        }

        // ==== 主日志实现 ====
        public static void LogMessage(object sender, LogSeverity sev, string format, params object[] args)
        {
            if ((LogSeverityLevel & sev) != 0 && OnMessage != null)
            {
                var message = (args != null && args.Length > 0) ? string.Format(format, args) : format;
                OnMessage.Invoke(sender, new LogEventArgs(sev, message));
            }
        }

        private static void DefaultServerLogHandler(object sender, LogEventArgs logArgs)
        {
            if ((logArgs.LogSeverity & (LogSeverity.Error | LogSeverity.Exception)) != 0)
                UnityEngine.Debug.LogError(logArgs.Message);
            else if ((logArgs.LogSeverity & LogSeverity.Warn) != 0)
                UnityEngine.Debug.LogWarning(logArgs.Message);
            else
                UnityEngine.Debug.Log(logArgs.Message);
        }

        // ==== Trace 相关逻辑 ====
        public static void Trace(string msg, bool isNeedLogTrace = false, bool isNewLine = true)
        {
            lock (_traceLock)
            {
                if (isNewLine) _traceSb.AppendLine(msg);
                else _traceSb.Append(msg);

                if (isNeedLogTrace)
                {
                    StackTrace st = new StackTrace(true);
                    foreach (var frame in st.GetFrames())
                    {
                        var typeName = frame.GetMethod()?.DeclaringType?.FullName ?? "null";
                        var methodName = frame.GetMethod()?.Name ?? "null";
                        var fileName = frame.GetFileName() ?? "unknown";
                        var lineNum = frame.GetFileLineNumber();
                        _traceSb.AppendLine($"at {typeName} :: {methodName}  in  {fileName}  :line  {lineNum}");
                    }
                }
                if (_traceSb.Length > traceDumpLen)
                    FlushTraceInternal();
            }
        }

        /// <summary>
        /// 旧接口保留兼容。新代码应逐步迁移到 FrameLog_xxx 系列。
        /// </summary>
        public static void FrameTrace(string msg, bool isNeedLogTrace = false, bool isNewLine = true)
        {
            lock (_frameTraceLock)
            {
                if (isNewLine) _frameTraceSb.AppendLine(msg);
                else _frameTraceSb.Append(msg);

                if (isNeedLogTrace)
                {
                    StackTrace st = new StackTrace(true);
                    foreach (var frame in st.GetFrames())
                    {
                        var typeName = frame.GetMethod()?.DeclaringType?.FullName ?? "null";
                        var methodName = frame.GetMethod()?.Name ?? "null";
                        var fileName = frame.GetFileName() ?? "unknown";
                        var lineNum = frame.GetFileLineNumber();
                        _frameTraceSb.AppendLine($"at {typeName} :: {methodName}  in  {fileName}  :line  {lineNum}");
                    }
                }
                if (_frameTraceSb.Length > frameTraceDumpLen)
                    FlushFrameTraceInternal();
            }
        }

        // ================================================================
        //  新帧日志系统：分文件 + 正常帧压缩
        // ================================================================
        //
        //  磁盘文件结构（以 20260315_143022 为例）：
        //
        //  HYLDLogs/
        //  ├── 20260315_143022_runtime.log        ← 原有：TCP/通用日志
        //  ├── 20260315_143022_framesync.log       ← 原有：旧格式全量帧日志（兼容）
        //  ├── 20260315_143022_summary.log         ← ★ 新：每帧一行摘要
        //  ├── 20260315_143022_reconcile.log       ← ★ 新：和解详情（仅异常帧）
        //  ├── 20260315_143022_authority.log        ← ★ 新：权威帧对比（仅不匹配时）
        //  └── 20260315_143022_playerstate.log     ← ★ 新：玩家坐标/状态
        //
        //  summary.log 示例（你 99% 的时间只看这个文件）：
        //    [F54]  sync=54 predict=54 matched ops=2 history=0
        //    [F55]  sync=55 predict=55 matched ops=2 history=0
        //    [F56]  sync=56 predict=56 matched ops=2 history=0
        //    [F59]  ⚠MISSING ↻REPLAY sync=59 predict=61 ops=1 history=0 replay=2
        //    [F60]  ↻REPLAY sync=60 predict=62 ops=2 history=1 replay=1
        //    [F61]  sync=61 predict=62 matched ops=2 history=1
        //
        //  出问题时打开 reconcile.log，直接搜 F59 就能看到完整和解过程。
        // ================================================================

        /// <summary>初始化分文件日志。每个对局一个文件夹，文件名直接用功能名。</summary>
        public static void InitFrameLogFiles(string directory)
        {
            lock (_frameLogLock)
            {
                FrameLogDirectory = directory;
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                _summaryStream = OpenLogFile(directory, "summary.log");
                _reconcileStream = OpenLogFile(directory, "reconcile.log");
                _authorityStream = OpenLogFile(directory, "authority.log");
                _playerStateStream = OpenLogFile(directory, "playerstate.log");
            }
        }

        private static Stream OpenLogFile(string dir, string fileName)
        {
            string path = Path.Combine(dir, fileName);
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            return stream;
        }

        /// <summary>开始新的一帧。在 BattleTick / ConsumeAuthoritativeFrameBatch 入口调用。</summary>
        public static void FrameLog_BeginFrame(int frameId)
        {
            lock (_frameLogLock)
            {
                // 如果上一帧没 End，自动结束
                if (_currentFrameId >= 0)
                    FrameLog_EndFrame_Internal();

                _currentFrameId = frameId;
                _frameHasMismatch = false;
                _frameHasReplay = false;
                _frameHasMissing = false;
                _frameEntryCount = 0;
                _frameDetailBuffer.Clear();
            }
        }

        /// <summary>记录和解相关日志（仅异常帧会写入 reconcile.log）</summary>
        public static void FrameLog_Reconcile(string msg)
        {
            lock (_frameLogLock)
            {
                _frameEntryCount++;
                _frameDetailBuffer.AppendLine($"  [Reconcile] {msg}");
            }
        }

        /// <summary>记录权威帧对比日志</summary>
        public static void FrameLog_Authority(string msg, bool isMismatch = false)
        {
            lock (_frameLogLock)
            {
                _frameEntryCount++;
                if (isMismatch)
                    _frameHasMismatch = true;
                _frameDetailBuffer.AppendLine($"  [Authority] {msg}");
            }
        }

        /// <summary>记录玩家状态</summary>
        public static void FrameLog_PlayerState(string msg)
        {
            lock (_frameLogLock)
            {
                _frameEntryCount++;
                // 玩家状态始终写入 playerstate.log（体积可控，每帧就几行）
                _playerStateFrameSb.AppendLine($"[F{_currentFrameId}] {msg}");
                if (_playerStateFrameSb.Length > frameTraceDumpLen)
                    FlushToStream(_playerStateFrameSb, _playerStateStream);
            }
        }

        /// <summary>标记当前帧有重放</summary>
        public static void FrameLog_MarkReplay()
        {
            lock (_frameLogLock)
            {
                _frameHasReplay = true;
            }
        }

        /// <summary>标记当前帧有操作缺失</summary>
        public static void FrameLog_MarkMissing()
        {
            lock (_frameLogLock)
            {
                _frameHasMissing = true;
            }
        }

        /// <summary>
        /// 结束当前帧。写入 summary.log 一行摘要；异常帧同时写入 reconcile.log / authority.log 详情。
        /// </summary>
        public static void FrameLog_EndFrame(int syncFrame, int predictFrame, int opCount, int historyCount, int replayCount = 0, bool inputMatched = true)
        {
            lock (_frameLogLock)
            {
                if (_currentFrameId < 0)
                    return;

                // 构建 summary 行
                var sb = new StringBuilder();
                sb.Append($"[F{_currentFrameId}]");

                if (_frameHasMismatch) sb.Append(" ⚠MISMATCH");
                if (_frameHasMissing) sb.Append(" ✕MISSING");
                if (_frameHasReplay) sb.Append(" ↻REPLAY");

                sb.Append($"  sync={syncFrame} predict={predictFrame}");
                if (inputMatched && !_frameHasMismatch)
                    sb.Append(" matched");
                sb.Append($" ops={opCount} history={historyCount}");
                if (replayCount > 0)
                    sb.Append($" replay={replayCount}");

                _summaryFrameSb.AppendLine(sb.ToString());

                // 异常帧：详情写入 reconcile.log 和 authority.log
                if (_frameHasMismatch || _frameHasReplay || _frameHasMissing)
                {
                    string header = $"===== F{_currentFrameId} =====";
                    _reconcileFrameSb.AppendLine(header);
                    _reconcileFrameSb.Append(_frameDetailBuffer);
                    _reconcileFrameSb.AppendLine();

                    _authorityFrameSb.AppendLine(header);
                    _authorityFrameSb.Append(_frameDetailBuffer);
                    _authorityFrameSb.AppendLine();
                }

                // 检查是否需要 flush
                if (_summaryFrameSb.Length > frameTraceDumpLen)
                    FlushToStream(_summaryFrameSb, _summaryStream);
                if (_reconcileFrameSb.Length > frameTraceDumpLen)
                    FlushToStream(_reconcileFrameSb, _reconcileStream);
                if (_authorityFrameSb.Length > frameTraceDumpLen)
                    FlushToStream(_authorityFrameSb, _authorityStream);

                _currentFrameId = -1;
                _frameDetailBuffer.Clear();
            }
        }

        /// <summary>内部调用：上一帧没 End 时自动结束（降级摘要）</summary>
        private static void FrameLog_EndFrame_Internal()
        {
            _summaryFrameSb.AppendLine($"[F{_currentFrameId}]  (auto-end) entries={_frameEntryCount}");
            _currentFrameId = -1;
            _frameDetailBuffer.Clear();
        }

        private static void FlushToStream(StringBuilder sb, Stream stream)
        {
            if (sb.Length <= 0 || stream == null)
                return;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
                sb.Clear();
            }
            catch (Exception e)
            {
                Console.WriteLine("FlushToStream Error: " + e);
            }
        }

        public static void FlushTrace()
        {
            lock (_traceLock)
            {
                FlushTraceInternal();
            }
        }

        private static void FlushTraceInternal()
        {
            if (_traceSb.Length <= 0 || string.IsNullOrEmpty(TraceSavePath))
                return;

            try
            {
                if (_stream == null)
                {
                    var dir = Path.GetDirectoryName(TraceSavePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    _stream = new FileStream(TraceSavePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
                    _stream.Seek(0, SeekOrigin.End);
                }

                var bytes = Encoding.UTF8.GetBytes(_traceSb.ToString());
                _stream.Write(bytes, 0, bytes.Length);
                _stream.Flush();
                _traceSb.Clear();
            }
            catch (Exception e)
            {
                Console.WriteLine("FlushTrace Error: " + e);
            }
        }

        public static void FlushFrameTrace()
        {
            lock (_frameTraceLock)
            {
                FlushFrameTraceInternal();
            }
            // 同时 flush 新的分文件日志
            lock (_frameLogLock)
            {
                FlushToStream(_summaryFrameSb, _summaryStream);
                FlushToStream(_reconcileFrameSb, _reconcileStream);
                FlushToStream(_authorityFrameSb, _authorityStream);
                FlushToStream(_playerStateFrameSb, _playerStateStream);
            }
        }

        private static void FlushFrameTraceInternal()
        {
            if (_frameTraceSb.Length <= 0 || string.IsNullOrEmpty(FrameTraceSavePath))
                return;

            try
            {
                if (_frameTraceStream == null)
                {
                    var dir = Path.GetDirectoryName(FrameTraceSavePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    _frameTraceStream = new FileStream(FrameTraceSavePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
                    _frameTraceStream.Seek(0, SeekOrigin.End);
                }

                var bytes = Encoding.UTF8.GetBytes(_frameTraceSb.ToString());
                _frameTraceStream.Write(bytes, 0, bytes.Length);
                _frameTraceStream.Flush();
                _frameTraceSb.Clear();
            }
            catch (Exception e)
            {
                Console.WriteLine("FlushFrameTrace Error: " + e);
            }
        }

        private static void CloseTraceStreamInternal()
        {
            if (_stream == null)
                return;

            try
            {
                _stream.Flush();
                _stream.Dispose();
            }
            catch (Exception e)
            {
                Console.WriteLine("CloseTraceStream Error: " + e);
            }
            finally
            {
                _stream = null;
            }
        }

        private static void CloseFrameTraceStreamInternal()
        {
            if (_frameTraceStream == null)
                return;

            try
            {
                _frameTraceStream.Flush();
                _frameTraceStream.Dispose();
            }
            catch (Exception e)
            {
                Console.WriteLine("CloseFrameTraceStream Error: " + e);
            }
            finally
            {
                _frameTraceStream = null;
            }
        }

        private static void CloseFrameLogStreams()
        {
            Stream[] streams = { _summaryStream, _reconcileStream, _authorityStream, _playerStateStream };
            StringBuilder[] sbs = { _summaryFrameSb, _reconcileFrameSb, _authorityFrameSb, _playerStateFrameSb };

            for (int i = 0; i < streams.Length; i++)
            {
                if (streams[i] != null)
                {
                    try
                    {
                        if (sbs[i].Length > 0)
                        {
                            var bytes = Encoding.UTF8.GetBytes(sbs[i].ToString());
                            streams[i].Write(bytes, 0, bytes.Length);
                            sbs[i].Clear();
                        }
                        streams[i].Flush();
                        streams[i].Dispose();
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("CloseFrameLogStream Error: " + e);
                    }
                }
            }
            _summaryStream = null;
            _reconcileStream = null;
            _authorityStream = null;
            _playerStateStream = null;
        }

        public static void Shutdown()
        {
            lock (_traceLock)
            {
                FlushTraceInternal();
                CloseTraceStreamInternal();
            }

            lock (_frameTraceLock)
            {
                FlushFrameTraceInternal();
                CloseFrameTraceStreamInternal();
            }

            lock (_frameLogLock)
            {
                CloseFrameLogStreams();
            }
        }
    }
}