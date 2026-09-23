// PMNet 编织便利层 —— 外部工具进程执行器（纯 BCL，不依赖 Unity API）。
//
// 契约：Docs/plans/net-rpc-weaving-contract.md §4（Editor 与构建接口）。
//
// 为什么单独一个文件：
//   便利层要反复启动**外部**工具（`dotnet <PMNetWeaver.dll>` / `dotnet build`），
//   而"启动外部进程"这件事本身有四个必须一次性做对的点，散在业务里迟早漏一个：
//     1) **超时**：工具卡住不能让编辑器一起卡住；超时只能终止**本次由我们自己启动**的子进程树，
//        绝不按进程名批量杀（那会命中用户自己的 dotnet / Unity / MSBuild）。
//     2) **并发**：同一时刻只允许一个工具运行。两个进程同时改写同一个程序集是数据损坏，
//        宁可让第二次调用**显式拒绝**（fail closed），也不排队。
//     3) **有界读取 + 有界排空**：stdout/stderr 都必须**持续**排空，否则子进程写满管道缓冲区就会
//        永久阻塞（看起来像"工具卡死"）；同时输出要有上限，避免一次 MSBuild 冗长日志把编辑器
//        内存和 Console 打爆。**并且**：排空本身也必须有界 —— 进程退出不代表管道已关闭
//        （孙进程会继承句柄），"无参 WaitForExit / ReadToEnd"在那种情况下会永久挂住。
//     4) **不做 shell 拼接**：全部走 `UseShellExecute = false`，参数按标准 MSVCRT 引用规则拼成
//        命令行字符串交给 CreateProcess，不存在 cmd 元字符注入面。
//
// 关于"参数数组"的诚实口径：
//   .NET Framework / Mono 的 `ProcessStartInfo` 在 Windows 上接受的是**一个命令行字符串**
//   （`Arguments`），不存在真正的"数组直传"通道。因此本执行器收的是 `IList<string>` 参数，
//   自己按 MSVCRT 规则引用后拼成字符串再执行（`ProcessStartInfo.Arguments` 就是这个字符串）。
//   不经过任何 shell，但**不能**声称"绕过了命令行字符串"。
//
// 读取实现（为什么不用 OutputDataReceived / BeginOutputReadLine）：
//   `BeginOutputReadLine` 是**按行**投递的，且在没有换行符时会在内部缓存里无界增长 ——
//   "工具连续输出 1 MB 不带换行"这种输入会同时撑爆内存与 64 KiB 预算（预算在 Append 里才生效，
//   已经晚了）。因此这里改成两条后台任务直接 `StreamReader.Read(char[])` 分块读取，
//   预算在**每块**上执行；不使用任何事件回调，也就不存在"对象 Dispose 之后事件回调再触发"的
//   生命周期竞态。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace PMNet.Weaving.Editor
{
    /// <summary>一次工具调用的结果（含失败原因、退出码、有界输出）。</summary>
    internal sealed class PMNetToolResult
    {
        /// <summary>可执行文件名/路径（如 <c>dotnet</c>）。</summary>
        public string FileName = string.Empty;

        /// <summary>
        /// 按 MSVCRT 规则引用后的参数串。这**就是**交给 CreateProcess 的字符串
        /// （本执行器不经过 shell）；字段保留它主要是为了日志可复现。
        /// </summary>
        public string Arguments = string.Empty;

        /// <summary>工作目录。</summary>
        public string WorkingDirectory = string.Empty;

        /// <summary>退出码；未启动或超时记为 -1。</summary>
        public int ExitCode = -1;

        /// <summary>标准输出（有上限，超限带截断标记）。</summary>
        public string StdOut = string.Empty;

        /// <summary>标准错误（有上限，超限带截断标记）。</summary>
        public string StdErr = string.Empty;

        /// <summary>进程是否真的被启动起来。</summary>
        public bool Started;

        /// <summary>是否因超时被我们主动终止。</summary>
        public bool TimedOut;

        /// <summary>是否发生了输出截断。</summary>
        public bool OutputTruncated;

        /// <summary>启动失败/超时/排空失败/其它异常的原因（成功时为空串）。</summary>
        public string Failure = string.Empty;

        /// <summary>耗时（秒）。</summary>
        public double DurationSeconds;

        /// <summary>
        /// 本次调用是否成功：已启动、未超时、退出码 0，**且没有任何 Failure 记录**。
        ///
        /// 必须一并看 <see cref="Failure"/>：早期实现只判前三条，于是"进程退出 0 但输出流一直
        /// 不关闭（孙进程继承管道）"这类显式失败会被当成成功，调用方随即按"编织完成"继续往下走。
        /// </summary>
        public bool Success
        {
            get { return Started && !TimedOut && ExitCode == 0 && string.IsNullOrEmpty(Failure); }
        }

        /// <summary>给日志用的一行命令描述。</summary>
        public string CommandLine
        {
            get
            {
                if (string.IsNullOrEmpty(Arguments))
                {
                    return FileName;
                }

                return FileName + " " + Arguments;
            }
        }

        /// <summary>一句话概括失败（供门禁报错文案复用）。</summary>
        public string ShortReason()
        {
            if (!string.IsNullOrEmpty(Failure))
            {
                return Failure;
            }

            if (TimedOut)
            {
                return "超时";
            }

            if (Started)
            {
                return "退出码 " + ExitCode.ToString(CultureInfo.InvariantCulture);
            }

            return "未启动";
        }

        /// <summary>把 stderr 的尾部拼进原因里（诊断信息通常就在最后几行）。</summary>
        public string TailOfStdErr(int maxChars)
        {
            string text = StdErr == null ? string.Empty : StdErr.Trim();
            if (text.Length <= maxChars)
            {
                return text;
            }

            return "..." + text.Substring(text.Length - maxChars);
        }
    }

    /// <summary>外部工具进程执行器（见文件头注释的四条纪律）。</summary>
    internal static class PMNetToolProcess
    {
        /// <summary>每个输出流最多保留的字符数。</summary>
        public const int MaxCapturedCharsPerStream = 64 * 1024;

        /// <summary>单次读取的字符块大小（预算按块执行，不存在无界行缓冲）。</summary>
        private const int ReadChunkChars = 4096;

        /// <summary>进程退出后等待两个读取任务收尾的上限（孙进程继承管道时用它兜底）。</summary>
        private const int DrainWaitMs = 3000;

        /// <summary>放弃排空后，等待读取任务观察到 dispose 的上限。</summary>
        private const int AbandonWaitMs = 1000;

        /// <summary>taskkill 自身的退出等待上限（它同样不能无限等）。</summary>
        private const int KillerWaitMs = 5000;

        /// <summary>并发闸门：同一时刻只允许一次工具运行。</summary>
        private static readonly object RunGate = new object();

        private static bool _runInFlight;

        /// <summary>
        /// 同步执行一个外部工具并返回结果。**不抛异常**：所有失败都体现在返回值里，
        /// 由调用方决定是记日志、阻断 Play/Build 还是仅提示。
        /// </summary>
        /// <param name="fileName">可执行文件名或绝对路径（如 <c>dotnet</c>）。</param>
        /// <param name="arguments">参数列表（**不经 shell**，逐个按 MSVCRT 规则引用成命令行字符串）。</param>
        /// <param name="workingDirectory">工作目录。</param>
        /// <param name="timeoutMs">超时上限（毫秒）。</param>
        public static PMNetToolResult Run(string fileName, IList<string> arguments, string workingDirectory, int timeoutMs)
        {
            PMNetToolResult result = new PMNetToolResult();
            result.FileName = fileName == null ? string.Empty : fileName;
            result.Arguments = BuildArgumentString(arguments);
            result.WorkingDirectory = workingDirectory == null ? string.Empty : workingDirectory;

            lock (RunGate)
            {
                if (_runInFlight)
                {
                    // 拒绝并发而不是排队：排队会让"两个改写同一程序集"的窗口依旧存在，只是更隐蔽。
                    result.Failure = "已有一个工具进程在运行，拒绝并发启动（避免同时改写同一个程序集）。";
                    return result;
                }

                _runInFlight = true;
            }

            try
            {
                ExecuteInto(result, timeoutMs);
            }
            finally
            {
                lock (RunGate)
                {
                    _runInFlight = false;
                }
            }

            return result;
        }

        /// <summary>当前是否有工具在运行（供状态展示）。</summary>
        public static bool IsRunning
        {
            get
            {
                lock (RunGate)
                {
                    return _runInFlight;
                }
            }
        }

        /// <summary>按 MSVCRT 规则把参数列表拼成命令行字符串。</summary>
        public static string BuildArgumentString(IList<string> arguments)
        {
            if (arguments == null || arguments.Count == 0)
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < arguments.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(QuoteArgument(arguments[i]));
            }

            return sb.ToString();
        }

        /// <summary>
        /// 单个参数引用（MSVCRT / CreateProcess 规则）：
        /// 含空白或引号才加引号；引号前必须是奇数个反斜杠；反斜杠结尾要翻倍。
        /// </summary>
        public static string QuoteArgument(string value)
        {
            if (value == null)
            {
                return "\"\"";
            }

            if (value.Length > 0 && value.IndexOfAny(new char[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            {
                return value;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            int backslashes = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    backslashes = 0;
                    continue;
                }

                if (backslashes > 0)
                {
                    sb.Append('\\', backslashes);
                    backslashes = 0;
                }

                sb.Append(c);
            }

            if (backslashes > 0)
            {
                sb.Append('\\', backslashes * 2);
            }

            sb.Append('"');
            return sb.ToString();
        }

        private static void ExecuteInto(PMNetToolResult result, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();

            if (string.IsNullOrEmpty(result.FileName))
            {
                result.Failure = "未指定可执行文件。";
                watch.Stop();
                result.DurationSeconds = watch.Elapsed.TotalSeconds;
                return;
            }

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = result.FileName;
            psi.Arguments = result.Arguments;
            psi.WorkingDirectory = result.WorkingDirectory;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            BoundedTextSink stdout = new BoundedTextSink(MaxCapturedCharsPerStream);
            BoundedTextSink stderr = new BoundedTextSink(MaxCapturedCharsPerStream);
            Process process = new Process();
            StreamPump outPump = null;
            StreamPump errPump = null;

            try
            {
                process.StartInfo = psi;

                if (!process.Start())
                {
                    throw new InvalidOperationException("Process.Start 返回 false");
                }

                result.Started = true;

                // 先挂上读取任务再等退出：否则子进程可能在被读到之前就把管道缓冲区写满并阻塞。
                outPump = StreamPump.Start(process.StandardOutput, stdout);
                errPump = StreamPump.Start(process.StandardError, stderr);

                int timeout = timeoutMs > 0 ? timeoutMs : 0;
                bool exited = process.WaitForExit(timeout);
                if (!exited)
                {
                    result.TimedOut = true;
                    result.Failure = "工具超时（超过 "
                        + timeout.ToString(CultureInfo.InvariantCulture)
                        + " ms），已终止本次由编辑器启动的子进程树。";
                    KillOwnProcessTree(process);
                    exited = SafeHasExited(process);
                }

                if (exited)
                {
                    try
                    {
                        result.ExitCode = process.ExitCode;
                    }
                    catch (Exception)
                    {
                        result.ExitCode = -1;
                    }
                }
                else
                {
                    result.ExitCode = -1;
                }

                // 有界排空：进程退出 **不代表** 管道已关闭（孙进程可能继承了 stdout/stderr）。
                StreamPump[] pumps = new StreamPump[] { outPump, errPump };
                if (!StreamPump.WaitAll(pumps, DrainWaitMs))
                {
                    result.Failure = AppendFailure(result.Failure,
                        "进程已退出但输出流在 " + DrainWaitMs.ToString(CultureInfo.InvariantCulture)
                        + " ms 内没有关闭（可能有子进程继承了 stdout/stderr 管道），按失败处理。");

                    // 主动放弃：dispose 读取器让阻塞中的读取立刻返回，再收一次尾观察异常。
                    StreamPump.ForceClose(pumps);
                    StreamPump.WaitAll(pumps, AbandonWaitMs);
                }
            }
            catch (Exception ex)
            {
                // 启动失败（找不到 dotnet / 权限 / 路径错）在这里显式落地，不静默当成"没变化"。
                result.Failure = AppendFailure(result.Failure, ex.GetType().Name + ": " + ex.Message);
                result.ExitCode = -1;
                StreamPump.ForceClose(new StreamPump[] { outPump, errPump });
            }
            finally
            {
                StreamPump.ForceClose(new StreamPump[] { outPump, errPump });
                result.StdOut = stdout.Text();
                result.StdErr = stderr.Text();
                result.OutputTruncated = stdout.Truncated || stderr.Truncated;
                try
                {
                    process.Dispose();
                }
                catch (Exception)
                {
                }

                watch.Stop();
                result.DurationSeconds = watch.Elapsed.TotalSeconds;
            }
        }

        private static bool SafeHasExited(Process process)
        {
            try
            {
                return process.HasExited;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string AppendFailure(string existing, string addition)
        {
            if (string.IsNullOrEmpty(existing))
            {
                return addition;
            }

            return existing + " " + addition;
        }

        /// <summary>
        /// 只终止**我们自己刚启动的那个 PID**（及其子进程树）。
        ///
        /// 为什么要连树一起收：`dotnet build` 会再拉起 MSBuild / 编译器子进程，只杀父进程会留下孤儿进程
        /// 继续持有文件句柄，下一次工具运行就会被"文件被占用"顶回来。
        ///
        /// 为什么只用 /PID 不用 /IM：按镜像名杀会命中用户自己正在跑的 dotnet/Unity/MSBuild 进程，
        /// 这是本文件明确禁止的行为。
        /// </summary>
        private static void KillOwnProcessTree(Process process)
        {
            int pid = -1;
            try
            {
                pid = process.Id;
            }
            catch (Exception)
            {
            }

            if (pid > 0 && Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                TaskKillOwnTree(pid);
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception)
            {
            }

            try
            {
                process.WaitForExit(KillerWaitMs);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// `taskkill /PID &lt;pid&gt; /T /F`：**同样**是有界执行 —— 它的两个输出流用同一套分块排空，
        /// 不允许出现"顺序 ReadToEnd 没有超时"的写法（那正是本文件要消除的挂死形态）。
        /// </summary>
        private static void TaskKillOwnTree(int pid)
        {
            Process killer = null;
            StreamPump outPump = null;
            StreamPump errPump = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "taskkill";
                psi.Arguments = "/PID " + pid.ToString(CultureInfo.InvariantCulture) + " /T /F";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                killer = Process.Start(psi);
                if (killer == null)
                {
                    return;
                }

                outPump = StreamPump.Start(killer.StandardOutput, new BoundedTextSink(MaxCapturedCharsPerStream));
                errPump = StreamPump.Start(killer.StandardError, new BoundedTextSink(MaxCapturedCharsPerStream));

                killer.WaitForExit(KillerWaitMs);
                StreamPump[] pumps = new StreamPump[] { outPump, errPump };
                if (!StreamPump.WaitAll(pumps, AbandonWaitMs))
                {
                    StreamPump.ForceClose(pumps);
                }
            }
            catch (Exception)
            {
                // taskkill 不可用时还有下面的 process.Kill() 兜底；这里不静默改变语义。
            }
            finally
            {
                StreamPump.ForceClose(new StreamPump[] { outPump, errPump });
                try
                {
                    if (killer != null)
                    {
                        killer.Dispose();
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// 一路读取一个输出流的分块读取任务。**不使用任何事件回调**：
        /// 事件在对象 Dispose 之后仍可能被投递（生命周期竞态），而任务模型的结束点只有一个。
        /// </summary>
        private sealed class StreamPump
        {
            private readonly StreamReader _reader;
            private readonly BoundedTextSink _sink;
            private readonly Task _task;

            private StreamPump(StreamReader reader, BoundedTextSink sink)
            {
                _reader = reader;
                _sink = sink;
                _task = Task.Run(new Action(Pump));
            }

            public static StreamPump Start(StreamReader reader, BoundedTextSink sink)
            {
                if (reader == null || sink == null)
                {
                    return null;
                }

                return new StreamPump(reader, sink);
            }

            private void Pump()
            {
                char[] buffer = new char[ReadChunkChars];
                try
                {
                    while (true)
                    {
                        int read = _reader.Read(buffer, 0, buffer.Length);
                        if (read <= 0)
                        {
                            return;
                        }

                        _sink.Append(buffer, read);
                    }
                }
                catch (Exception)
                {
                    // 两条**预期**路径都会走到这里：
                    //   · 调用方判定排空超时后主动 Dispose 读取器（放弃等待）；
                    //   · 进程/句柄被系统回收。
                    // 因此这里必须吞掉（否则会变成未观察的任务异常，甚至把 Dispose 之后的对象带进崩溃）。
                }
            }

            /// <summary>有界等待所有读取任务结束；返回 false 表示到点仍有流没关闭。</summary>
            public static bool WaitAll(IList<StreamPump> pumps, int millisecondsTimeout)
            {
                List<Task> tasks = new List<Task>();
                if (pumps != null)
                {
                    for (int i = 0; i < pumps.Count; i++)
                    {
                        if (pumps[i] != null && pumps[i]._task != null)
                        {
                            tasks.Add(pumps[i]._task);
                        }
                    }
                }

                if (tasks.Count == 0)
                {
                    return true;
                }

                try
                {
                    return Task.WaitAll(tasks.ToArray(), millisecondsTimeout);
                }
                catch (AggregateException)
                {
                    // Pump 内部已吞掉所有异常，因此正常情况不会走到这里；
                    // 万一走到，也只能按"已收尾"处理（没有更多可等待的东西）。
                    return true;
                }
            }

            /// <summary>
            /// 主动放弃读取：Dispose 读取器迫使阻塞中的 Read 立即返回或抛错。
            ///
            /// Dispose 刻意放到**另一个线程**上做（fire-and-forget，异常在任务里吞掉）：
            /// 某些运行时的 FileStream.Dispose 会等待内部状态，而“读取线程正阻塞在同一个管道上”
            /// 时那个等待可能并不短。本方法的契约是"调用方**必须**在有界时间内返回"，
            /// 因此不能把 Dispose 挂在调用线程上。
            /// </summary>
            public static void ForceClose(IList<StreamPump> pumps)
            {
                if (pumps == null)
                {
                    return;
                }

                for (int i = 0; i < pumps.Count; i++)
                {
                    StreamPump pump = pumps[i];
                    if (pump == null)
                    {
                        continue;
                    }

                    if (pump._task != null && pump._task.IsCompleted)
                    {
                        continue;   // 读取已自然收尾，不需要打断
                    }

                    StreamReader reader = pump._reader;
                    Task.Run(delegate
                    {
                        try
                        {
                            reader.Dispose();
                        }
                        catch (Exception)
                        {
                        }
                    });
                }
            }
        }

        /// <summary>有上限的文本收集器（读取任务与主线程并发访问，内部自带锁）。</summary>
        private sealed class BoundedTextSink
        {
            private readonly StringBuilder _sb = new StringBuilder();
            private readonly int _max;
            private readonly object _sync = new object();

            public BoundedTextSink(int max)
            {
                _max = max;
            }

            public bool Truncated { get; private set; }

            /// <summary>
            /// 按块追加：**每块**都受预算约束，超出的字符直接丢弃并打截断标记。
            ///
            /// 按块而不是按行是刻意的：按行追加时，"一整块超长行"会先被完整构造出来再超限，
            /// 预算实际拦不住它（这也是旧实现对"无换行的 64 KiB 以上输出"失效的原因）。
            /// </summary>
            public void Append(char[] buffer, int count)
            {
                if (buffer == null || count <= 0)
                {
                    return;
                }

                lock (_sync)
                {
                    if (_sb.Length >= _max)
                    {
                        Truncated = true;
                        return;
                    }

                    int take = Math.Min(count, _max - _sb.Length);
                    _sb.Append(buffer, 0, take);
                    if (take < count)
                    {
                        Truncated = true;
                    }
                }
            }

            public string Text()
            {
                lock (_sync)
                {
                    string text = _sb.ToString();
                    if (Truncated)
                    {
                        text += "...(输出超过 " + _max.ToString(CultureInfo.InvariantCulture) + " 字符，已截断)\n";
                    }

                    return text;
                }
            }
        }
    }
}
