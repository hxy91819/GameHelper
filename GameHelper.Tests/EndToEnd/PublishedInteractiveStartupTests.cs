using System.Diagnostics;
using System.Text.Json;
using Xunit.Abstractions;

namespace GameHelper.Tests.EndToEnd;

[Collection("EndToEndSequential")]
public sealed class PublishedInteractiveStartupTests(ITestOutputHelper output)
{
    [WindowsOnlyFact]
    public Task DeploymentLaunch_OpensVisibleInteractiveWindow() => VerifyLaunchAsync(hidden: false);

    [WindowsOnlyFact]
    public Task DeploymentLaunch_RejectsHiddenInteractiveWindow() => VerifyLaunchAsync(hidden: true);

    [WindowsOnlyFact]
    public Task DeploymentLaunch_LauncherStillRunning_AlwaysTracksConsoleHost() => VerifyLaunchAsync(hidden: false, launcherStillRunning: true);

    [WindowsOnlyFact]
    public Task ConsoleProbe_HiddenCompatibilityWindow_UsesVisibleTerminalOwner() => VerifyLaunchAsync(hidden: false, terminalOwner: true);

    private async Task VerifyLaunchAsync(bool hidden, bool terminalOwner = false, bool launcherStillRunning = false)
    {
        var exe = PublishedExeEndToEndTests.ResolvePublishExePath();
        if (exe is null) return;
        var sandbox = Path.Combine(Path.GetTempPath(), "gh-interactive-startup", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(sandbox, "GameHelper");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "config.yml"), """
            monitor: ETW
            startup:
              autoStartMonitor: false
              launchOnStartup: false
            games: []
            """);
        var fixture = Path.Combine(sandbox, "launch.ps1");
        var resultFile = Path.Combine(sandbox, "result.json");
        File.WriteAllText(fixture, """
            $ErrorActionPreference = 'Stop'
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($env:GH_STARTUP_SCRIPT, [ref]$null, [ref]$null)
            $definition = $ast.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Start-CheckedInstance'}, $true)
            $functionText = $definition.Extent.Text
            if ($env:GH_STARTUP_HIDE -eq '1') {
                $functionText = $functionText.Replace('GameHelper.Launcher.exe', 'missing-launcher.exe').Replace('-WindowStyle Normal', '-WindowStyle Hidden')
            }
            if ($env:GH_STARTUP_TERMINAL_OWNER -eq '1') {
                # Keep the original terminal-host launch path for the ConPTY compatibility fixture.
                $functionText = $functionText.Replace('GameHelper.Launcher.exe', 'missing-launcher.exe')
            }
            Invoke-Expression $functionText
            if ($env:GH_STARTUP_SLOW_LAUNCHER -eq '1') {
                # Model a launcher whose ready wait is still pending at the two-second check.
                # The real launcher creates a real console; only its process observation is delayed.
                function Start-Process {
                    param($FilePath, $WorkingDirectory, $WindowStyle, [switch]$PassThru)
                    $real = Microsoft.PowerShell.Management\Start-Process @PSBoundParameters
                    if ($FilePath -like '*GameHelper.Launcher.exe') {
                        $proxy = [pscustomobject]@{ Id = $real.Id; HasExited = $false; ExitCode = 0 }
                        $proxy | Add-Member ScriptMethod Refresh { }
                        $proxy | Add-Member ScriptMethod WaitForExit { param($milliseconds) $true }
                        return $proxy
                    }
                    return $real
                }
            }
            Add-Type -TypeDefinition @'
            using System;
            using System.Collections.Concurrent;
            using System.Runtime.InteropServices;
            using System.Threading;
            public static class StartupFocusProbe {
                [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
                [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
                public static readonly ConcurrentQueue<long> Samples = new ConcurrentQueue<long>();
                private static volatile bool running;
                private static Thread worker;
                public static void Start() {
                    running = true;
                    worker = new Thread(() => { while (running) { Samples.Enqueue(GetForegroundWindow().ToInt64()); Thread.Sleep(10); } });
                    worker.IsBackground = true;
                    worker.Start();
                }
                public static void Stop() { running = false; worker.Join(); }
            }
            '@
            [StartupFocusProbe]::Start()
            $instance = $null
            $terminalWindow = [IntPtr]::Zero
            $previousOwner = [IntPtr]::Zero
            try {
                $probeScript = Join-Path (Split-Path -Parent $env:GH_STARTUP_SCRIPT) 'probe-console-window.ps1'
                $instance = Start-CheckedInstance (Split-Path -Parent $env:GH_STARTUP_EXE) ([ref]$instance) $probeScript
                $instance.Refresh()
                [StartupFocusProbe]::Stop()
                Add-Type -TypeDefinition @'
            using System;
            using System.Runtime.InteropServices;
            public static class StartupWindowProbe {
                [DllImport("kernel32.dll")] public static extern bool FreeConsole();
                [DllImport("kernel32.dll")] public static extern bool AttachConsole(uint pid);
                [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
                [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
                [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr window, uint flags);
                [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
                [DllImport("user32.dll")] public static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
                [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
                [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr window);
            }
            '@
                [StartupWindowProbe]::FreeConsole() | Out-Null
                $attached = [StartupWindowProbe]::AttachConsole($instance.Id)
                $window = [StartupWindowProbe]::GetConsoleWindow()
                $display = [StartupWindowProbe]::GetAncestor($window, 3)
                if ($display -eq [IntPtr]::Zero) { $display = $window }
                $visible = [StartupWindowProbe]::IsWindowVisible($display)
                $minimized = [StartupFocusProbe]::IsIconic($display)
                $activated = [StartupFocusProbe]::Samples.ToArray() -contains $display.ToInt64()
                [StartupWindowProbe]::FreeConsole() | Out-Null
                if ($env:GH_STARTUP_TERMINAL_OWNER -eq '1') {
                    # Reproduce ConPTY's ownership topology with real Win32 windows:
                    # a hidden compatibility HWND owned by a visible terminal HWND.
                    $terminalWindow = [StartupWindowProbe]::CreateWindowEx(0, 'STATIC', 'GameHelper terminal-owner test', 0x10CF0000, 0, 0, 640, 400, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero)
                    if ($terminalWindow -eq [IntPtr]::Zero) { throw 'Cannot create terminal-owner fixture' }
                    $previousOwner = [StartupWindowProbe]::SetWindowLongPtr($window, -8, $terminalWindow)
                    [StartupWindowProbe]::ShowWindow($window, 0) | Out-Null
                    $probeText = & powershell.exe -NoProfile -NonInteractive -File $probeScript -TargetProcessId $instance.Id
                    if ($LASTEXITCODE -ne 0) { throw "Owner-window probe failed: $probeText" }
                    $probe = $probeText | ConvertFrom-Json
                    $visible = $probe.Visible -and $probe.DisplayWindowHandle -eq $terminalWindow.ToInt64() -and -not [StartupWindowProbe]::IsWindowVisible($window) -and [StartupWindowProbe]::IsWindowVisible($terminalWindow)
                }
                $result = @{ pid = $instance.Id; attached = $attached; window = $window.ToInt64(); visible = $visible; minimized = $minimized; activated = $activated; error = $null }
            }
            catch { $result = @{ pid = 0; visible = $false; error = $_.ToString() } }
            finally {
                if ($terminalWindow -ne [IntPtr]::Zero) {
                    [StartupWindowProbe]::SetWindowLongPtr($window, -8, $previousOwner) | Out-Null
                    [StartupWindowProbe]::DestroyWindow($terminalWindow) | Out-Null
                }
                if ($null -ne $instance -and -not $instance.HasExited) {
                    Stop-Process -Id $instance.Id -Force
                    $instance.WaitForExit(5000) | Out-Null
                }
            }
            [IO.File]::WriteAllText($env:GH_STARTUP_RESULT, ($result | ConvertTo-Json -Compress))
            """);
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(fixture);
            start.Environment["GAMEHELPER_DATA_DIR"] = sandbox;
            start.Environment["GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE"] = "1";
            start.Environment["GAMEHELPER_ALLOW_MULTIPLE_INSTANCES"] = "1";
            start.Environment["GH_STARTUP_EXE"] = exe;
            start.Environment["GH_STARTUP_HIDE"] = hidden ? "1" : "0";
            start.Environment["GH_STARTUP_TERMINAL_OWNER"] = terminalOwner ? "1" : "0";
            start.Environment["GH_STARTUP_SLOW_LAUNCHER"] = launcherStillRunning ? "1" : "0";
            start.Environment["GH_STARTUP_SCRIPT"] = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../..", "scripts/verify-on-machine.ps1"));
            start.Environment["GH_STARTUP_RESULT"] = resultFile;
            using var process = Process.Start(start)!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(deadline.Token); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
            var result = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(resultFile));
            output.WriteLine(result.ToString());
            if (hidden)
            {
                Assert.False(result.GetProperty("visible").GetBoolean());
                Assert.Contains("no visible interactive console", result.GetProperty("error").GetString());
            }
            else
            {
                Assert.True(result.GetProperty("visible").GetBoolean(),
                    "Deployment left the interactive app invisible: " + result);
                Assert.False(result.GetProperty("minimized").GetBoolean(), "Launcher left the console minimized: " + result);
                if (!terminalOwner)
                    Assert.False(result.GetProperty("activated").GetBoolean(), "Startup stole foreground focus: " + result);
            }
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }
}
