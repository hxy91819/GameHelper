using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace GameHelper.ConsoleHost.Utilities
{
    /// <summary>
    /// 交互窗口的宿主守卫：当程序没有运行在 Windows Terminal 里时，用 wt.exe 把自己重新拉起，
    /// 使用户看到的窗口始终是 Windows Terminal（字体、配色、边框一致），而不是回落到经典 conhost。
    /// 任意一个前置条件不满足时保持当前宿主，启动流程不受影响。
    /// </summary>
    internal static class WindowsTerminalHost
    {
        /// <summary>重新拉起时附加的标记参数，保证同一条启动链最多转移一次宿主。</summary>
        internal const string RelaunchedArgument = "--in-windows-terminal";

        /// <summary>后台启动参数：启动后收进任务栏，并在窗口还原时才接管宿主。</summary>
        internal const string MinimizedArgument = "--minimized";

        /// <summary>设置该环境变量（1/true）可关闭自动转移宿主。</summary>
        internal const string DisableEnvironmentVariable = "GAMEHELPER_DISABLE_TERMINAL_RELAUNCH";

        private const string SandboxEnvironmentVariable = "GAMEHELPER_DATA_DIR";
        private const string SingleInstanceOverrideEnvironmentVariable = "GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE";
        private const string WindowsTerminalProcessName = "WindowsTerminal";
        private const string WindowsTerminalAliasPath = @"Microsoft\WindowsApps\wt.exe";
        private const uint GaRootOwner = 3;
        private static readonly TimeSpan RelaunchConfirmationTimeout = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan RelaunchConfirmationPollInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan RestorePollInterval = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan StartupGracePeriod = TimeSpan.FromSeconds(30);
        private const int RestoreConfirmedPolls = 5;

        /// <summary>
        /// 检测当前控制台宿主，必要时用 Windows Terminal 重新拉起自身。
        /// 返回 true 表示已经拉起新实例，调用方应立即结束当前进程。
        /// </summary>
        public static bool TryRelaunch(string[] args, bool isFileDropRequest)
        {
            try
            {
                var executablePath = ResolveSelfExecutablePath();
                var terminalPath = FindWindowsTerminalExecutable();
                var facts = CollectFacts(args, isFileDropRequest, executablePath, terminalPath);
                if (!Evaluate(facts).Relocate)
                {
                    return false;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = terminalPath!,
                    Arguments = BuildTerminalCommandLine(executablePath!, args),
                    UseShellExecute = true,
                    WorkingDirectory = AppContext.BaseDirectory,
                    WindowStyle = ProcessWindowStyle.Normal,
                };

                var launchedAt = DateTime.UtcNow.AddSeconds(-1);
                using var relaunched = Process.Start(startInfo);
                if (relaunched is null)
                {
                    return false;
                }

                // 新窗口没起来时不能把当前窗口交出去，否则用户会什么都看不到。
                if (WaitForSuccessor(executablePath!, launchedAt))
                {
                    return true;
                }

                Console.WriteLine("Windows Terminal did not start a new instance; continuing in the current console.");
                return false;
            }
            catch (Exception ex)
            {
                // 转移宿主失败不是致命错误：继续在当前控制台里跑。
                Console.WriteLine($"Failed to relaunch inside Windows Terminal: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 后台启动（--minimized）专用：先留在当前宿主里最小化，等用户真的把窗口还原时
        /// 再用 Windows Terminal 接管。这样开机不会抢焦点，用户打开时又是终端外观。
        /// 返回 true 表示监视线程已经启动。
        /// </summary>
        public static bool TryDeferRelaunchUntilWindowRestored(string[] args, bool isFileDropRequest)
        {
            try
            {
                var executablePath = ResolveSelfExecutablePath();
                var terminalPath = FindWindowsTerminalExecutable();
                var facts = CollectFacts(args, isFileDropRequest, executablePath, terminalPath);
                if (!Evaluate(facts).Relocate)
                {
                    return false;
                }

                var watcher = new Thread(() => WatchForRestoreAndRelaunch(args, isFileDropRequest))
                {
                    Name = "WindowsTerminalHandoff",
                    IsBackground = true,
                };
                watcher.Start();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to schedule Windows Terminal handoff: {ex.Message}");
                return false;
            }
        }

        private static void WatchForRestoreAndRelaunch(string[] args, bool isFileDropRequest)
        {
            // 只有确认过窗口确实最小化过，才可能在它恢复时接管；否则（例如隐藏启动）什么都不做。
            if (!ConsoleWindowMinimizer.WaitUntilMinimized(TimeSpan.FromSeconds(10)))
            {
                return;
            }

            // 启动阶段窗口会被重建、部署探针附加控制台也可能把它短暂显示出来，
            // 先跳过一段稳定期，并要求"已还原"连续成立，避免把噪声当成用户操作。
            Thread.Sleep(StartupGracePeriod);

            var restoredPolls = 0;
            while (restoredPolls < RestoreConfirmedPolls)
            {
                Thread.Sleep(RestorePollInterval);
                restoredPolls = ConsoleWindowMinimizer.IsCurrentWindowRestored() ? restoredPolls + 1 : 0;
            }

            var relaunchArgs = Array.FindAll(
                args, argument => !string.Equals(argument, MinimizedArgument, StringComparison.OrdinalIgnoreCase));

            ProcessInstanceGuard.ReleaseForHandoff();
            if (TryRelaunch(relaunchArgs, isFileDropRequest))
            {
                Environment.Exit(0);
            }

            // 接管失败就继续在当前宿主里跑，并把单实例占用拿回来。
            _ = ProcessInstanceGuard.TryClaim();
        }

        /// <summary>纯策略判断，便于在无法创建真实终端的测试环境里覆盖全部分支。</summary>
        internal static TerminalRelaunchDecision Evaluate(TerminalRelaunchFacts facts)
        {
            if (!facts.IsWindows) return TerminalRelaunchDecision.Keep("not running on Windows");
            if (facts.IsFileDropRequest) return TerminalRelaunchDecision.Keep("file-drop launch keeps the current host");
            if (facts.IsSandboxed) return TerminalRelaunchDecision.Keep("sandboxed instance never changes its host");
            if (facts.DisabledByEnvironment) return TerminalRelaunchDecision.Keep("disabled by " + DisableEnvironmentVariable);
            if (facts.AlreadyRelaunched) return TerminalRelaunchDecision.Keep("already relaunched once");
            if (facts.IsDebuggerAttached) return TerminalRelaunchDecision.Keep("debugger attached");
            if (!facts.HasInteractiveConsole) return TerminalRelaunchDecision.Keep("console streams are redirected");
            if (facts.IsHostedByWindowsTerminal) return TerminalRelaunchDecision.Keep("already hosted by Windows Terminal");
            if (!facts.WindowsTerminalAvailable) return TerminalRelaunchDecision.Keep("wt.exe is not available");
            if (facts.AnotherInstanceRunning) return TerminalRelaunchDecision.Keep("another instance is already running");

            return TerminalRelaunchDecision.Transfer("console host is not Windows Terminal");
        }

        /// <summary>判断当前进程的控制台窗口是否由 Windows Terminal 拥有。</summary>
        internal static bool IsHostedByWindowsTerminal()
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                var consoleWindow = GetConsoleWindow();
                if (consoleWindow == IntPtr.Zero)
                {
                    return false;
                }

                // Windows Terminal 拥有 ConPTY 的隐藏兼容窗口，真正的显示窗口是它的根所有者。
                var rootOwner = GetAncestor(consoleWindow, GaRootOwner);
                var displayWindow = rootOwner != IntPtr.Zero ? rootOwner : consoleWindow;
                _ = GetWindowThreadProcessId(displayWindow, out var processId);
                if (processId == 0)
                {
                    return false;
                }

                using var owner = Process.GetProcessById((int)processId);
                return string.Equals(owner.ProcessName, WindowsTerminalProcessName, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        internal static string? FindWindowsTerminalExecutable()
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                return null;
            }

            var aliasPath = Path.Combine(localAppData, WindowsTerminalAliasPath);
            return File.Exists(aliasPath) ? aliasPath : null;
        }

        /// <summary>构建 wt.exe 的命令行：新窗口 + 指定工作目录 + 原始参数 + 防重入标记。</summary>
        internal static string BuildTerminalCommandLine(string executablePath, string[] args)
        {
            var builder = new StringBuilder("-w -1");

            var workingDirectory = Path.GetDirectoryName(executablePath);
            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                builder.Append(" -d ").Append(QuoteArgument(workingDirectory));
            }

            builder.Append(" -- ").Append(QuoteArgument(executablePath));
            foreach (var argument in args)
            {
                builder.Append(' ').Append(QuoteArgument(argument));
            }

            return builder.Append(' ').Append(RelaunchedArgument).ToString();
        }

        private static TerminalRelaunchFacts CollectFacts(
            string[] args, bool isFileDropRequest, string? executablePath, string? terminalPath)
        {
            return new TerminalRelaunchFacts
            {
                IsWindows = OperatingSystem.IsWindows(),
                IsFileDropRequest = isFileDropRequest,
                IsSandboxed = HasEnvironmentValue(SandboxEnvironmentVariable)
                    || HasEnvironmentValue(SingleInstanceOverrideEnvironmentVariable),
                DisabledByEnvironment = HasEnvironmentValue(DisableEnvironmentVariable),
                AlreadyRelaunched = Array.Exists(
                    args, argument => string.Equals(argument, RelaunchedArgument, StringComparison.OrdinalIgnoreCase)),
                IsDebuggerAttached = Debugger.IsAttached,
                HasInteractiveConsole = !Console.IsInputRedirected && !Console.IsOutputRedirected,
                IsHostedByWindowsTerminal = IsHostedByWindowsTerminal(),
                WindowsTerminalAvailable = !string.IsNullOrWhiteSpace(terminalPath)
                    && !string.IsNullOrWhiteSpace(executablePath),
                AnotherInstanceRunning = ProcessInstanceGuard.IsAnotherInstanceRunning(),
            };
        }

        /// <summary>
        /// 解析"自己"的可执行文件路径。框架依赖方式（dotnet run）下 ProcessPath 是 dotnet.exe，
        /// 直接用它重新拉起会跑错程序，因此返回 null 放弃转移。
        /// </summary>
        private static string? ResolveSelfExecutablePath()
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                return null;
            }

            var fileName = Path.GetFileNameWithoutExtension(processPath);
            return string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase) ? null : processPath;
        }

        /// <summary>等待 Windows Terminal 拉起的新实例出现，确认转移真的成功。</summary>
        private static bool WaitForSuccessor(string executablePath, DateTime launchedAt)
        {
            var processName = Path.GetFileNameWithoutExtension(executablePath);
            var deadline = DateTime.UtcNow.Add(RelaunchConfirmationTimeout);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(RelaunchConfirmationPollInterval);
                foreach (var candidate in Process.GetProcessesByName(processName))
                {
                    using (candidate)
                    {
                        try
                        {
                            if (candidate.Id == Environment.ProcessId
                                || candidate.StartTime.ToUniversalTime() < launchedAt)
                            {
                                continue;
                            }

                            if (string.Equals(candidate.MainModule?.FileName, executablePath, StringComparison.OrdinalIgnoreCase))
                            {
                                return true;
                            }
                        }
                        catch
                        {
                            // 进程已退出或路径不可读时继续等待下一个候选。
                        }
                    }
                }
            }

            return false;
        }

        private static bool HasEnvironmentValue(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static string QuoteArgument(string value)
        {
            if (value.Length == 0)
            {
                return "\"\"";
            }

            if (value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            {
                return value;
            }

            var builder = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    builder.Append('\\', (backslashes * 2) + 1).Append('"');
                    backslashes = 0;
                    continue;
                }

                builder.Append('\\', backslashes).Append(character);
                backslashes = 0;
            }

            return builder.Append('\\', backslashes * 2).Append('"').ToString();
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }

    /// <summary>宿主转移的判定输入。</summary>
    internal sealed record TerminalRelaunchFacts
    {
        public bool IsWindows { get; init; } = true;
        public bool IsFileDropRequest { get; init; }
        public bool IsSandboxed { get; init; }
        public bool DisabledByEnvironment { get; init; }
        public bool AlreadyRelaunched { get; init; }
        public bool IsDebuggerAttached { get; init; }
        public bool HasInteractiveConsole { get; init; } = true;
        public bool IsHostedByWindowsTerminal { get; init; }
        public bool WindowsTerminalAvailable { get; init; } = true;
        public bool AnotherInstanceRunning { get; init; }
    }

    /// <summary>宿主转移的判定结果。</summary>
    internal readonly record struct TerminalRelaunchDecision(bool Relocate, string Reason)
    {
        public static TerminalRelaunchDecision Keep(string reason) => new(false, reason);

        public static TerminalRelaunchDecision Transfer(string reason) => new(true, reason);
    }
}
