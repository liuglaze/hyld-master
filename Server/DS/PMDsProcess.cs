using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace PMNet.Control
{
    /// <summary>
    /// R3-A1：DS 进程启动/观测接口 + **真实固定 exe 适配器**。
    ///
    /// 事实来源：`Docs/plans/net-r3-control-contract.md` §2 末段与 §3。
    ///
    /// 三条硬约束（都是「不做就等于是漏洞」的约束）：
    /// 1. **不经过 shell**：真实适配器用 <see cref="ProcessStartInfo.ArgumentList"/> 逐个传参，
    ///    `UseShellExecute=false`。**不存在**把参数拼成一个命令串再交给 `cmd /c` 的路径，
    ///    因此「Lobby 被诱导执行任意命令」这条链在结构上不存在。
    /// 2. **固定 exe + 白名单**：可执行文件路径由调用方（R3-B）从配置里给出，
    ///    并可用 <see cref="PMDsProcessWhitelist"/> 收紧；已知 shell 一律拒绝
    ///    （<see cref="PMDsProcessSafety.IsKnownShell"/>），杜绝「配置被写成 cmd.exe 就变成任意命令执行」。
    /// 3. **stdout/stderr 有界且不参与就绪判定**：输出只保留尾部 N 字节（环形缓冲），
    ///    只作诊断；**进程是否就绪只由控制帧 Ready 决定**，任何日志字符串都不构成就绪证据。
    ///
    /// A1 的范围：本文件只提供接口与真实适配器。测试用替身实现 <see cref="IPMDsProcess"/>/<see cref="IPMDsProcessLauncher"/>
    /// 做故障注入，**不启动真实 HyldDS**。R3-B 才负责把白名单、端口范围、引导文件路径接进真实宿主。
    /// </summary>
    public enum PMDsProcessFault : byte
    {
        None = 0,

        /// <summary>启动请求为 null。</summary>
        NullRequest = 1,

        /// <summary>可执行文件路径为空。</summary>
        MissingExecutable = 2,

        /// <summary>可执行文件路径不是绝对路径。</summary>
        ExecutableNotRooted = 3,

        /// <summary>可执行文件在路径上找不到。</summary>
        ExecutableNotFound = 4,

        /// <summary>可执行文件是已知的 shell 解释器（拒绝）。</summary>
        ShellRejected = 5,

        /// <summary>可执行文件不在配置白名单内。</summary>
        NotWhitelisted = 6,

        /// <summary>工作目录为空。</summary>
        MissingWorkingDirectory = 7,

        /// <summary>工作目录不存在。</summary>
        WorkingDirectoryNotFound = 8,

        /// <summary>某个参数含非法字符（NUL / 换行）。</summary>
        InvalidArgument = 9,

        /// <summary>真实启动失败（权限/路径/资源等）。</summary>
        LaunchFailed = 10,
    }

    /// <summary>
    /// DS 启动请求。**不可变约定**：构造后由调用方持有，协调器只做深拷贝与透传，
    /// 从不修改、从不把参数拼成字符串。
    /// </summary>
    public sealed class PMDsProcessLaunchRequest
    {
        /// <summary>可执行文件绝对路径（由配置给出，不从客户端输入取）。</summary>
        public string ExecutablePath = string.Empty;

        /// <summary>工作目录绝对路径。</summary>
        public string WorkingDirectory = string.Empty;

        /// <summary>参数数组（逐个传递，不拼接；**不得包含密钥**，引导内容一律走文件路径参数）。</summary>
        public string[] Arguments = new string[0];

        /// <summary>每个输出流保留的尾部字节上限（stdout / stderr 各自）。</summary>
        public int MaxCapturedOutputBytes = DefaultMaxCapturedOutputBytes;

        /// <summary>默认的输出保留上限：64KiB / 流。</summary>
        public const int DefaultMaxCapturedOutputBytes = 64 * 1024;

        public PMDsProcessLaunchRequest()
        {
        }

        public PMDsProcessLaunchRequest(string executablePath, string workingDirectory, params string[] arguments)
        {
            ExecutablePath = executablePath ?? string.Empty;
            WorkingDirectory = workingDirectory ?? string.Empty;
            Arguments = arguments ?? new string[0];
        }

        /// <summary>深拷贝（协调器持有自己的副本，避免调用方事后改数组）。</summary>
        public PMDsProcessLaunchRequest Clone()
        {
            PMDsProcessLaunchRequest copy = new PMDsProcessLaunchRequest();
            copy.ExecutablePath = ExecutablePath;
            copy.WorkingDirectory = WorkingDirectory;
            copy.MaxCapturedOutputBytes = MaxCapturedOutputBytes;
            string[] args = Arguments ?? new string[0];
            copy.Arguments = new string[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                copy.Arguments[i] = args[i];
            }

            return copy;
        }

        /// <summary>诊断描述：**只给 exe 路径与参数个数**，不展开参数（参数里可能有引导文件路径）。</summary>
        public override string ToString()
        {
            int count = Arguments == null ? 0 : Arguments.Length;
            return "launch(exe=" + ExecutablePath + " args=" + count + " wd=" + WorkingDirectory + ")";
        }
    }

    /// <summary>DS 进程替身/真实进程的统一视图。协调器只通过这个接口与进程交互。</summary>
    public interface IPMDsProcess : IDisposable
    {
        /// <summary>进程 ID（替身可返回 0）。</summary>
        int ProcessId { get; }

        /// <summary>是否仍在运行（协调器在 <c>Tick</c> 里轮询它，避免跨线程改状态）。</summary>
        bool IsRunning { get; }

        /// <summary>取退出码；未退出时返回 false。</summary>
        bool TryGetExitCode(out int exitCode);

        /// <summary>
        /// 有界等待进程退出，返回「是否已退出」。
        ///
        /// 为什么需要它：<see cref="Kill"/> 的语义只是「请求终止」——进程可能仍在运行并仍持有监听端口。
        /// 协调器必须能问一句「你真的走了吗」，才敢把端口还给共享端口池。
        /// 替身/同步实现可以退化为一次状态查询（不需要真的 sleep）。
        /// </summary>
        bool WaitForExit(int timeoutMilliseconds);

        /// <summary>stdout 累计字节数（**有界保留的是尾部**，这里是总量）。</summary>
        long StdOutTotalBytes { get; }

        /// <summary>stderr 累计字节数。</summary>
        long StdErrTotalBytes { get; }

        /// <summary>stdout 尾部文本（诊断用，**不构成就绪证据**）。</summary>
        string StdOutTail { get; }

        /// <summary>stderr 尾部文本（诊断用）。</summary>
        string StdErrTail { get; }

        /// <summary>
        /// 请求优雅退出。对无头 DS 而言**没有窗口可关**，真实优雅路径是控制通道的 Shutdown 消息；
        /// 这个方法是「尽力而为」的钩子（例如给标准输入写一行），允许为空实现。
        /// </summary>
        void RequestGracefulExit();

        /// <summary>强杀（含子进程树）。</summary>
        void Kill();

        /// <summary>进程退出通知（至多触发一次）。协调器不依赖它推进状态，只作为加速观测。</summary>
        event Action<int> Exited;
    }

    /// <summary>DS 进程启动器。</summary>
    public interface IPMDsProcessLauncher
    {
        /// <summary>尝试启动。失败时给出分类与**不含秘密**的原因描述。</summary>
        bool TryStart(PMDsProcessLaunchRequest request, out IPMDsProcess process,
            out PMDsProcessFault fault, out string detail);
    }

    /// <summary>可执行文件白名单（由 R3-B 从配置装载；大小写不敏感、按绝对路径规范化比较）。</summary>
    public sealed class PMDsProcessWhitelist
    {
        private readonly HashSet<string> _paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>加入一个绝对路径（重复加入幂等）。</summary>
        public void Add(string executablePath)
        {
            string normalized = Normalize(executablePath);
            if (normalized == null)
            {
                throw new ArgumentException("白名单路径必须为绝对路径：" + executablePath, "executablePath");
            }

            _paths.Add(normalized);
        }

        /// <summary>白名单条目数。</summary>
        public int Count { get { return _paths.Count; } }

        /// <summary>是否包含该路径。</summary>
        public bool Contains(string executablePath)
        {
            string normalized = Normalize(executablePath);
            return normalized != null && _paths.Contains(normalized);
        }

        /// <summary>规范化：展开环境变量 + 全路径 + 去掉尾部斜杠。</summary>
        public static string Normalize(string executablePath)
        {
            if (string.IsNullOrEmpty(executablePath))
            {
                return null;
            }

            string expanded = Environment.ExpandEnvironmentVariables(executablePath);
            try
            {
                string full = Path.GetFullPath(expanded);
                return full.TrimEnd('\\', '/');
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>进程启动请求的结构校验与安全判定（真实启动器与协调器共用同一口径）。</summary>
    public static class PMDsProcessSafety
    {
        private static readonly string[] KnownShells = new string[]
        {
            "cmd", "cmd.exe",
            "powershell", "powershell.exe",
            "pwsh", "pwsh.exe",
            "wscript", "wscript.exe",
            "cscript", "cscript.exe",
            "sh", "bash", "sh.exe", "bash.exe",
        };

        /// <summary>仅结构校验（不碰文件系统）——协调器用它做「分配前」检查。</summary>
        public static bool ValidateStructure(PMDsProcessLaunchRequest request,
            out PMDsProcessFault fault, out string detail)
        {
            fault = PMDsProcessFault.None;
            detail = null;

            if (request == null)
            {
                fault = PMDsProcessFault.NullRequest;
                detail = "启动请求为 null";
                return false;
            }

            if (string.IsNullOrEmpty(request.ExecutablePath))
            {
                fault = PMDsProcessFault.MissingExecutable;
                detail = "缺少可执行文件路径";
                return false;
            }

            if (!IsRooted(request.ExecutablePath))
            {
                fault = PMDsProcessFault.ExecutableNotRooted;
                detail = "可执行文件必须是绝对路径：" + request.ExecutablePath;
                return false;
            }

            if (IsKnownShell(request.ExecutablePath))
            {
                fault = PMDsProcessFault.ShellRejected;
                detail = "拒绝通过 shell 解释器启动 DS：" + Path.GetFileName(request.ExecutablePath);
                return false;
            }

            if (string.IsNullOrEmpty(request.WorkingDirectory))
            {
                fault = PMDsProcessFault.MissingWorkingDirectory;
                detail = "缺少工作目录";
                return false;
            }

            string[] args = request.Arguments ?? new string[0];
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == null)
                {
                    fault = PMDsProcessFault.InvalidArgument;
                    detail = "第 " + i + " 个参数为 null";
                    return false;
                }

                if (args[i].IndexOf('\0') >= 0 || args[i].IndexOf('\r') >= 0 || args[i].IndexOf('\n') >= 0)
                {
                    fault = PMDsProcessFault.InvalidArgument;
                    detail = "第 " + i + " 个参数含 NUL/换行（会破坏「一个参数一个 argv 项」的不变量）";
                    return false;
                }
            }

            return true;
        }

        /// <summary>启动前的完整校验：结构 + 文件存在 + 白名单。</summary>
        public static bool ValidateForLaunch(PMDsProcessLaunchRequest request, PMDsProcessWhitelist whitelist,
            out PMDsProcessFault fault, out string detail)
        {
            if (!ValidateStructure(request, out fault, out detail))
            {
                return false;
            }

            if (whitelist != null && whitelist.Count > 0 && !whitelist.Contains(request.ExecutablePath))
            {
                fault = PMDsProcessFault.NotWhitelisted;
                detail = "可执行文件不在白名单内：" + request.ExecutablePath;
                return false;
            }

            string fullExe = ResolveFullPath(request.ExecutablePath);
            if (fullExe == null || !File.Exists(fullExe))
            {
                fault = PMDsProcessFault.ExecutableNotFound;
                detail = "可执行文件不存在：" + request.ExecutablePath;
                return false;
            }

            string fullWorkingDirectory = ResolveFullPath(request.WorkingDirectory);
            if (fullWorkingDirectory == null || !Directory.Exists(fullWorkingDirectory))
            {
                fault = PMDsProcessFault.WorkingDirectoryNotFound;
                detail = "工作目录不存在：" + request.WorkingDirectory;
                return false;
            }

            return true;
        }

        /// <summary>路径是否为绝对路径（同时接受 Windows 盘符路径与 UNC）。</summary>
        public static bool IsRooted(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            string expanded = Environment.ExpandEnvironmentVariables(path);
            return Path.IsPathRooted(expanded);
        }

        /// <summary>是否为已知 shell 解释器（只看文件名）。</summary>
        public static bool IsKnownShell(string executablePath)
        {
            if (string.IsNullOrEmpty(executablePath))
            {
                return false;
            }

            string name;
            try
            {
                name = Path.GetFileName(executablePath);
            }
            catch (Exception)
            {
                name = executablePath;
            }

            for (int i = 0; i < KnownShells.Length; i++)
            {
                if (string.Equals(name, KnownShells[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        internal static string ResolveFullPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 有界的输出环形缓冲：只保留**尾部** <c>maxBytes</c> 字节，另外单独记录累计总量。
    ///
    /// 为什么必须这样：无头 DS 会持续打日志，若把 stdout/stderr 全收进内存，
    /// 一局跑久了就是内存泄漏；而「只保留尾部」既够诊断（崩溃现场就在尾部），又天然有界。
    /// </summary>
    public sealed class PMDsBoundedTextLog
    {
        private readonly byte[] _ring;
        private readonly int _capacity;
        private int _start;
        private int _count;

        public PMDsBoundedTextLog(int maxBytes)
        {
            if (maxBytes <= 0)
            {
                throw new ArgumentOutOfRangeException("maxBytes", "输出保留上限必须为正");
            }

            _capacity = maxBytes;
            _ring = new byte[maxBytes];
        }

        /// <summary>保留上限。</summary>
        public int Capacity { get { return _capacity; } }

        /// <summary>当前保留的字节数（≤ <see cref="Capacity"/>）。</summary>
        public int RetainedBytes { get { return _count; } }

        /// <summary>累计写入的字节数（**不受上限限制**，这是「流过多少」的真相）。</summary>
        public long TotalBytes { get; private set; }

        /// <summary>追加字节（超出容量时覆盖最旧的部分）。</summary>
        public void Append(byte[] data, int offset, int count)
        {
            if (data == null || count <= 0)
            {
                return;
            }

            if (offset < 0 || offset + count > data.Length)
            {
                throw new ArgumentOutOfRangeException("count", "追加范围越界");
            }

            TotalBytes += count;

            if (count >= _capacity)
            {
                // 一次写入就超过容量：只留最后 capacity 字节。
                Buffer.BlockCopy(data, offset + count - _capacity, _ring, 0, _capacity);
                _start = 0;
                _count = _capacity;
                return;
            }

            for (int i = 0; i < count; i++)
            {
                int writeAt = _start + _count;
                if (writeAt >= _capacity)
                {
                    writeAt -= _capacity;
                }

                _ring[writeAt] = data[offset + i];
                if (_count < _capacity)
                {
                    _count++;
                }
                else
                {
                    _start++;
                    if (_start >= _capacity)
                    {
                        _start = 0;
                    }
                }
            }
        }

        /// <summary>取尾部文本（按 UTF-8 解码；截断处在多字节序列中间时用替换字符，这是可接受的诊断误差）。</summary>
        public string GetTail()
        {
            if (_count == 0)
            {
                return string.Empty;
            }

            byte[] ordered = new byte[_count];
            int first = Math.Min(_count, _capacity - _start);
            Buffer.BlockCopy(_ring, _start, ordered, 0, first);
            if (first < _count)
            {
                Buffer.BlockCopy(_ring, 0, ordered, first, _count - first);
            }

            return Encoding.UTF8.GetString(ordered, 0, ordered.Length);
        }
    }

    /// <summary>
    /// 真实进程适配器：**固定 exe + 逐个参数 + 无 shell + 有界输出**。
    ///
    /// 这个类刻意不提供「命令字符串」入口：可执行文件与参数在类型层面就是分开的。
    /// </summary>
    public sealed class PMDsSystemProcessLauncher : IPMDsProcessLauncher
    {
        private readonly PMDsProcessWhitelist _whitelist;

        /// <summary>不启用白名单（只做结构校验 + 存在性校验）。</summary>
        public PMDsSystemProcessLauncher()
        {
        }

        /// <summary>启用白名单（R3-B 应从配置装载）。</summary>
        public PMDsSystemProcessLauncher(PMDsProcessWhitelist whitelist)
        {
            _whitelist = whitelist;
        }

        /// <summary>当前白名单（可能为 null）。</summary>
        public PMDsProcessWhitelist Whitelist { get { return _whitelist; } }

        public bool TryStart(PMDsProcessLaunchRequest request, out IPMDsProcess process,
            out PMDsProcessFault fault, out string detail)
        {
            process = null;
            if (!PMDsProcessSafety.ValidateForLaunch(request, _whitelist, out fault, out detail))
            {
                return false;
            }

            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = PMDsProcessSafety.ResolveFullPath(request.ExecutablePath);
                startInfo.WorkingDirectory = PMDsProcessSafety.ResolveFullPath(request.WorkingDirectory);

                // 关键：不做 shell 解析、不做命令串拼接、不弹窗口。
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.RedirectStandardInput = true;

                string[] args = request.Arguments ?? new string[0];
                for (int i = 0; i < args.Length; i++)
                {
                    // ArgumentList 让 .NET 负责转义：每个元素严格对应一个 argv 项。
                    startInfo.ArgumentList.Add(args[i]);
                }

                Process started = new Process();
                started.StartInfo = startInfo;
                if (!started.Start())
                {
                    started.Dispose();
                    fault = PMDsProcessFault.LaunchFailed;
                    detail = "Process.Start() 返回 false";
                    return false;
                }

                PMDsSystemProcess wrapper = new PMDsSystemProcess(started, request.MaxCapturedOutputBytes);
                process = wrapper;
                fault = PMDsProcessFault.None;
                detail = null;
                return true;
            }
            catch (Exception ex)
            {
                fault = PMDsProcessFault.LaunchFailed;
                // 只回传异常类型与消息；这里不会有密钥（参数从不进诊断串）。
                detail = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }
    }

    /// <summary>真实进程包装：读输出线程 + 有界尾部缓冲 + 幂等退出观测。</summary>
    public sealed class PMDsSystemProcess : IPMDsProcess
    {
        private readonly Process _process;
        private readonly PMDsBoundedTextLog _stdout;
        private readonly PMDsBoundedTextLog _stderr;
        private readonly object _gate = new object();

        private bool _exitedRaised;
        private bool _disposed;

        public PMDsSystemProcess(Process process, int maxCapturedOutputBytes)
        {
            if (process == null)
            {
                throw new ArgumentNullException("process");
            }

            if (maxCapturedOutputBytes <= 0)
            {
                maxCapturedOutputBytes = PMDsProcessLaunchRequest.DefaultMaxCapturedOutputBytes;
            }

            _process = process;
            _stdout = new PMDsBoundedTextLog(maxCapturedOutputBytes);
            _stderr = new PMDsBoundedTextLog(maxCapturedOutputBytes);

            StartPump(_process.StandardOutput, _stdout);
            StartPump(_process.StandardError, _stderr);

            try
            {
                _process.EnableRaisingEvents = true;
                _process.Exited += OnProcessExited;
            }
            catch (Exception)
            {
                // 事件挂不上不影响轮询路径（协调器本来就在 Tick 里轮询）。
            }
        }

        public int ProcessId
        {
            get
            {
                try
                {
                    return _process.Id;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        public bool IsRunning
        {
            get
            {
                try
                {
                    return !_process.HasExited;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public bool TryGetExitCode(out int exitCode)
        {
            exitCode = 0;
            try
            {
                if (!_process.HasExited)
                {
                    return false;
                }

                exitCode = _process.ExitCode;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary><see cref="Kill"/> 之后的一次有界等待上限（毫秒）。</summary>
        public const int KillWaitMilliseconds = 2000;

        /// <summary>有界等待退出；超时/不支持时返回 false（调用方据此**不释放**端口）。</summary>
        public bool WaitForExit(int timeoutMilliseconds)
        {
            try
            {
                if (_process.HasExited)
                {
                    return true;
                }

                if (timeoutMilliseconds <= 0)
                {
                    return false;
                }

                return _process.WaitForExit(timeoutMilliseconds);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public long StdOutTotalBytes { get { return _stdout.TotalBytes; } }

        public long StdErrTotalBytes { get { return _stderr.TotalBytes; } }

        public string StdOutTail { get { return _stdout.GetTail(); } }

        public string StdErrTail { get { return _stderr.GetTail(); } }

        public event Action<int> Exited;

        public void RequestGracefulExit()
        {
            // 无头 DS 没有窗口；真实优雅路径是控制通道的 Shutdown 消息。
            // 这里只给标准输入写一行作「尽力而为」的信号，失败即忽略。
            try
            {
                if (!_process.HasExited && _process.StartInfo.RedirectStandardInput)
                {
                    _process.StandardInput.WriteLine("shutdown");
                    _process.StandardInput.Flush();
                }
            }
            catch (Exception)
            {
            }
        }

        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(true);
                }
            }
            catch (Exception)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill();
                    }
                }
                catch (Exception)
                {
                }
            }

            // ★ 关键：<c>Kill</c> 只把「终止请求」发出去，进程可能还在跑、还占着监听端口。
            // 这里做一次**有界等待**，让「Kill 返回 ⇒ 绝大多数情况下端口已真正释放」成立；
            // 超时也不抛也不假装已退出：协调器会在 Tick 里继续确认，确认不到就不归还端口。
            WaitForExit(KillWaitMilliseconds);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            try
            {
                _process.Exited -= OnProcessExited;
            }
            catch (Exception)
            {
            }

            try
            {
                _process.Dispose();
            }
            catch (Exception)
            {
            }
        }

        private void OnProcessExited(object sender, EventArgs args)
        {
            int exitCode;
            if (!TryGetExitCode(out exitCode))
            {
                exitCode = 0;
            }

            RaiseExited(exitCode);
        }

        private void RaiseExited(int exitCode)
        {
            Action<int> handler;
            lock (_gate)
            {
                if (_exitedRaised || _disposed)
                {
                    return;
                }

                _exitedRaised = true;
                handler = Exited;
            }

            if (handler != null)
            {
                try
                {
                    handler(exitCode);
                }
                catch (Exception)
                {
                }
            }
        }

        private static void StartPump(StreamReader reader, PMDsBoundedTextLog log)
        {
            Thread thread = new Thread(delegate()
            {
                try
                {
                    char[] chunk = new char[2048];
                    while (true)
                    {
                        int read = reader.Read(chunk, 0, chunk.Length);
                        if (read <= 0)
                        {
                            break;
                        }

                        byte[] bytes = Encoding.UTF8.GetBytes(chunk, 0, read);
                        log.Append(bytes, 0, bytes.Length);
                    }
                }
                catch (Exception)
                {
                    // 进程被杀时读线程抛异常是正常的；有界缓冲里已经留下了能看到的部分。
                }
            });
            thread.IsBackground = true;
            thread.Name = "PMDsProcessPump";
            thread.Start();
        }
    }
}
