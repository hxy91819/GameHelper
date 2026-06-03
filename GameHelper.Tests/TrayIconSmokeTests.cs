using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GameHelper.Tests
{
    /// <summary>
    /// End-to-end verification that GameHelper.ConsoleHost puts a real icon
    /// in the system tray. The test spawns the compiled console host with
    /// <c>--tray-smoke-test</c>; the host creates a NotifyIcon on an STA
    /// thread, lets the shell register it, and prints a single line
    /// <c>TRAY_OK</c> to stdout iff the icon is really there. Anything else
    /// (<c>TRAY_FAIL: ...</c>, no output, exit code != 0) is a failure.
    ///
    /// The test requires:
    ///   - Windows (NotifyIcon / Shell_NotifyIcon are Win32-only).
    ///   - A real desktop session, i.e. <see cref="Environment.UserInteractive"/>
    ///     is true.
    ///   - The env var <c>DOTNET_RUN_TRAY_TESTS=1</c> to be set, so a normal
    ///     <c>dotnet test</c> run does not try to bring up a tray icon.
    /// </summary>
    public class TrayIconSmokeTests
    {
        private const string EnableEnvVar = "DOTNET_RUN_TRAY_TESTS";
        private const string OkMarker = "TRAY_OK";
        private static readonly TimeSpan SmokeTimeout = TimeSpan.FromSeconds(30);

        [SkippableFact]
        public void TrayIconSmokeTest_ReportsTrayOk()
        {
            Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
                "Tray icon only works on Windows.");
            Skip.IfNot(Environment.UserInteractive,
                "Tray icon requires an interactive desktop session (skipped in headless CI).");
            Skip.IfNot(string.Equals(
                    Environment.GetEnvironmentVariable(EnableEnvVar),
                    "1",
                    StringComparison.Ordinal),
                $"Set {EnableEnvVar}=1 to opt in to the tray smoke test.");

            var (exitCode, stdout, _) = ConsoleHostSmokeRunner.RunAsync(
                new[] { "--tray-smoke-test" },
                SmokeTimeout,
                OkMarker).GetAwaiter().GetResult();

            Assert.Contains(OkMarker, stdout);
            Assert.Equal(0, exitCode);
        }
    }

    /// <summary>
    /// End-to-end verification that the console window really gets hidden
    /// from the taskbar after startup. Without this test, the only way to
    /// know the hide actually worked was to look at the taskbar by hand —
    /// which is exactly the manual-verification step the user wanted to
    /// eliminate.
    ///
    /// The test spawns the compiled console host with <c>--hide-smoke-test</c>;
    /// the host allocates a console, calls Hide(), then asserts
    /// <c>IsWindowVisible(console_hwnd) == false</c> and that
    /// <c>WS_EX_APPWINDOW</c> is not set. It prints a single line
    /// <c>HIDE_OK</c> on success.
    /// </summary>
    public class HideWindowSmokeTests
    {
        private const string OkMarker = "HIDE_OK";
        private static readonly TimeSpan SmokeTimeout = TimeSpan.FromSeconds(30);

        [SkippableFact]
        public void HideSmokeTest_ConsoleWindowIsHiddenAfterStartup()
        {
            Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
                "Hide window test only runs on Windows.");
            Skip.IfNot(Environment.UserInteractive,
                "Hide window test requires an interactive desktop session.");

            var (exitCode, stdout, stderr) = ConsoleHostSmokeRunner.RunAsync(
                new[] { "--hide-smoke-test" },
                SmokeTimeout,
                OkMarker).GetAwaiter().GetResult();

            Assert.True(
                stdout.Contains(OkMarker),
                $"Expected '{OkMarker}' on stdout. Exit code: {exitCode}.\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
        }
    }

    /// <summary>
    /// Shared helper for spawning the compiled GameHelper.ConsoleHost.exe
    /// with a smoke-test flag and capturing its stdout / stderr. We invoke
    /// the .exe directly (not <c>dotnet run</c>) because <c>dotnet run</c>
    /// routes the child's stdout through a host process that swallows the
    /// marker line when OutputType=WinExe.
    /// </summary>
    internal static class ConsoleHostSmokeRunner
    {
        public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
            string[] args,
            TimeSpan timeout,
            string expectedMarker)
        {
            var exePath = LocateConsoleHostExe();

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var proc = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderrBuilder.AppendLine(e.Data); };

            if (!proc.Start())
            {
                throw new InvalidOperationException($"Failed to start {exePath}");
            }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException(
                    $"Smoke test did not exit within {timeout.TotalSeconds:F0}s.\n" +
                    $"--- stdout ---\n{stdoutBuilder}\n--- stderr ---\n{stderrBuilder}");
            }

            return (proc.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
        }

        private static string LocateConsoleHostExe()
        {
            var repoRoot = LocateRepoRoot();
            var consoleHostDir = Path.Combine(repoRoot, "GameHelper.ConsoleHost", "bin");
            var tfm = "net8.0-windows";

            var candidates = new[]
            {
                Path.Combine(consoleHostDir, "Debug", tfm, "GameHelper.ConsoleHost.exe"),
                Path.Combine(consoleHostDir, "Release", tfm, "GameHelper.ConsoleHost.exe"),
            };
            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            throw new FileNotFoundException(
                $"Could not locate GameHelper.ConsoleHost.exe. Looked in:\n  " +
                string.Join("\n  ", candidates) +
                $"\nDid you run `dotnet build` first?");
        }

        private static string LocateRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "GameHelper.sln")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            throw new InvalidOperationException(
                $"Could not locate GameHelper.sln starting from {AppContext.BaseDirectory}");
        }
    }
}
