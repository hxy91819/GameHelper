using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Processes;
using GameHelper.Infrastructure.Controllers;
using GameHelper.Core.Services;
using GameHelper.Infrastructure.Ipc;
using GameHelper.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using GameHelper.Tests;
using Xunit;
using Xunit.Abstractions;

namespace GameHelper.Tests.EndToEnd;

/// <summary>
/// 已发布 exe 的 monitor 命令产品级端到端验证：真实 ETW 内核会话 + 真实子进程 + 沙盒数据目录，
/// 覆盖宿主装配（Worker/GameAutomationService/CSV 持久化）到落盘的完整链路，以及 ETW 会话被外部
/// 杀死后（对应线上"游戏停止未检出"故障）的恢复与漏退出补偿。库级同构场景见
/// EtwSessionLossIntegrationTests；本文件补齐真实产物与宿主装配这层。
/// 需要管理员权限与已发布产物，任一不满足时跳过（与既有 ETW 集成测试同一约定）。
/// 启动方式：经 cmd /c 以文件重定向拉起（无控制台 + 管道 stdout 时应用启动会卡住，实测文件重定向正常），
/// 断言只匹配 ASCII 子串以兼容 GBK/UTF-8 输出。
/// </summary>
[Collection("ETW")]
public sealed class PublishedMonitorEndToEndTests(ITestOutputHelper output)
{
    private const string ProbeDataKey = "gh_e2e_probe";
    private const string ProbeExeName = "gh-e2e-probe.exe";
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PersistTimeout = TimeSpan.FromSeconds(75);

    [WindowsOnlyFact]
    public async Task BackgroundEntry_DeploymentStartsOneWindowlessRuntimeAndTracksGame()
    {
        if (!TryGuard(out var exePath)) return;
        using var sandbox = CreateSandbox();
        var script = Path.Combine(sandbox.RootDir, "background-start.ps1");
        var result = Path.Combine(sandbox.RootDir, "runtime-pid.txt");
        File.WriteAllText(script, """
            $ErrorActionPreference = 'Stop'
            . $env:GH_START_SCRIPT
            $instance = $null
            try {
                $instance = Start-CheckedInstance $env:GH_PUBLISH_DIR ([ref]$instance)
                [IO.File]::WriteAllText($env:GH_RESULT, [string]$instance.Id)
            } catch {
                [IO.File]::WriteAllText($env:GH_RESULT + '.error', $_.ToString())
                exit 1
            }
            """);
        async Task<int> StartAsync()
        {
            var start = new ProcessStartInfo("powershell.exe")
            { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-ExecutionPolicy"); start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File"); start.ArgumentList.Add(script);
            start.Environment["GAMEHELPER_DATA_DIR"] = sandbox.RootDir;
            start.Environment["GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE"] = "1";
            start.Environment["GAMEHELPER_ALLOW_MULTIPLE_INSTANCES"] = "1";
            start.Environment["GH_START_SCRIPT"] = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../..", "scripts/start-checked-instance.ps1"));
            start.Environment["GH_PUBLISH_DIR"] = Path.GetDirectoryName(exePath)!;
            start.Environment["GH_RESULT"] = result;
            using var broker = Process.Start(start)!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await broker.WaitForExitAsync(deadline.Token); }
            finally { if (!broker.HasExited) broker.Kill(entireProcessTree: true); }
            Assert.True(broker.ExitCode == 0, File.Exists(result + ".error") ? File.ReadAllText(result + ".error") : "Background startup failed.");
            return int.Parse(File.ReadAllText(result), CultureInfo.InvariantCulture);
        }
        using var app = new AppHandle(Process.GetProcessById(await StartAsync()));
        try
        {
            var client = new GameLibraryPipe(sandbox.ConfigPath);
            var ready = await client.ExecuteAsync(new GameLibraryRequest { Operation = GameLibraryOperation.List });
            Assert.True(ready.Success, ready.Error);
            Assert.True(ready.MonitorRunning);
            Assert.Equal(ready.RuntimeProcessId, await StartAsync());
            Assert.Contains(new EtwRuntime(null).GetActiveSessionNames(), name => name.StartsWith($"GameHelper-ETW-{ready.RuntimeProcessId}-", StringComparison.Ordinal));
            using var probe = StartProbe(sandbox, "/c ping 127.0.0.1 -n 4 > NUL");
            await WaitUntilAsync(() => ReadProbeRows(sandbox.CsvPath).Count > 0, PersistTimeout, "background runtime tracked game without a console");
            Assert.True(File.Exists(sandbox.ConfigPath + ".runtime.log"));
        }
        finally { StopAppAndSweepSessions(app); }
    }

    [WindowsOnlyFact]
    public async Task GameLibrary_PublishedRuntime_SavesLaunchesTracksAndRendersDesktop()
    {
        if (!TryGuard(out var exePath)) return;
        using var sandbox = CreateSandbox();
        var source = new YamlConfigProvider(sandbox.ConfigPath);
        source.Change(config => config.Games.Single().Launch = new GameLaunchOptions
        {
            Target = sandbox.ProbeExePath, Arguments = "/c ping 127.0.0.1 -n 5 > NUL"
        });
        using var app = StartApp(exePath!, sandbox);
        try
        {
            await WaitUntilOutputAsync(app, sandbox, text => text.Contains("GameAutomationService started"), ReadyTimeout, "library runtime ready");
            var client = new GameLibraryPipe(sandbox.ConfigPath);
            var listed = await client.ExecuteAsync(new GameLibraryRequest { Operation = GameLibraryOperation.List });
            Assert.True(listed.Success, listed.Error);
            Assert.True(listed.MonitorRunning);
            var original = Assert.Single(listed.Games);
            original.Config.DisplayName = "Library Sandbox Probe";
            var saved = await client.ExecuteAsync(new GameLibraryRequest
            {
                Operation = GameLibraryOperation.Save, DataKey = ProbeDataKey, ExpectedRevision = original.Revision, Game = original.Config
            });
            Assert.True(saved.Success, saved.Error);
            var stale = await client.ExecuteAsync(new GameLibraryRequest
            {
                Operation = GameLibraryOperation.Save, DataKey = ProbeDataKey, ExpectedRevision = original.Revision, Game = original.Config
            });
            Assert.False(stale.Success);
            Assert.Equal("Library Sandbox Probe", source.Read().Games.Single().DisplayName);
            var launched = await client.ExecuteAsync(new GameLibraryRequest { Operation = GameLibraryOperation.Launch, DataKey = ProbeDataKey });
            Assert.True(launched.Success, launched.Error);
            await WaitUntilOutputAsync(app, sandbox, text => text.Contains("Process start") && text.Contains(ProbeDataKey), ReadyTimeout, "library launch observed");
            await WaitUntilAsync(() => ReadProbeRows(sandbox.CsvPath).Count > 0, PersistTimeout, "library launch persisted playtime");

            var desktop = Path.Combine(Path.GetDirectoryName(exePath)!, "desktop", "GameHelper.Desktop.exe");
            Assert.True(File.Exists(desktop), "ConsoleHost publish must include the WPF desktop.");
            var screenshots = Environment.GetEnvironmentVariable("GAMEHELPER_TEST_LIBRARY_SCREENSHOTS") ?? Path.Combine(sandbox.RootDir, "ui");
            var start = new ProcessStartInfo(desktop) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--config"); start.ArgumentList.Add(sandbox.ConfigPath);
            start.ArgumentList.Add("--snapshot"); start.ArgumentList.Add(screenshots);
            start.Environment["GAMEHELPER_DATA_DIR"] = sandbox.RootDir;
            start.Environment["GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE"] = "1";
            using var ui = Process.Start(start)!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try { await ui.WaitForExitAsync(deadline.Token); }
            finally { if (!ui.HasExited) ui.Kill(entireProcessTree: true); }
            Assert.Equal(0, ui.ExitCode);
            Assert.True(File.Exists(Path.Combine(screenshots, "verification.txt")), "The WPF rendering probe did not finish.");
            Assert.Equal("Library Sandbox Probe", source.Read().Games.Single().DisplayName);
            output.WriteLine("Library IPC, optimistic save, native launch/ETW/CSV and desktop rendering passed: " + screenshots);
        }
        finally { StopAppAndSweepSessions(app); }
    }

    [WindowsOnlyFact]
    public async Task Monitor_WhenElevatedWithPublishedExe_CapturesSessionIntoCsv()
    {
        if (!TryGuard(out var exePath)) return;
        using var sandbox = CreateSandbox();
        // 启动前快照：区分测试实例与用户实例/其他实例的 ETW 会话，清理时绝不触碰属主存活的会话。
        var preLaunchSessions = new EtwRuntime(null).GetActiveSessionNames();
        using var app = StartApp(exePath, sandbox);
        try
        {
            await WaitUntilOutputAsync(app, sandbox, text => text.Contains("GameAutomationService started"),
                ReadyTimeout, "monitor host readiness (GameAutomationService started)");

            using var child = StartProbe(sandbox, "/c ping 127.0.0.1 -n 4 > NUL");
            await WaitUntilOutputAsync(app, sandbox,
                text => text.Contains("Process start") && text.Contains(ProbeDataKey),
                ReadyTimeout, "probe process start event");

            await WaitUntilAsync(() => ReadProbeRows(sandbox.CsvPath).Count > 0,
                PersistTimeout, "persisted playtime.csv row");

            var row = ReadProbeRows(sandbox.CsvPath)[0].Split(',');
            Assert.Equal(4, row.Length);
            Assert.False(string.IsNullOrWhiteSpace(row[1]), "start_time should be recorded");
            Assert.False(string.IsNullOrWhiteSpace(row[2]), "end_time should be recorded after probe exit");
            output.WriteLine($"Persisted row: {string.Join(',', row)}");
        }
        finally
        {
            StopAppAndSweepSessions(app);
        }
    }

    [WindowsOnlyFact]
    public async Task Monitor_WhenElevatedWithPublishedExe_ReconcilesGameExitDuringEtwSessionLoss()
    {
        if (!TryGuard(out var exePath)) return;
        using var sandbox = CreateSandbox();
        var preLaunchSessions = new EtwRuntime(null).GetActiveSessionNames();
        using var app = StartApp(exePath, sandbox);
        Process? child = null;
        try
        {
            await WaitUntilOutputAsync(app, sandbox, text => text.Contains("GameAutomationService started"),
                ReadyTimeout, "monitor host readiness (GameAutomationService started)");

            child = StartProbe(sandbox, "/c ping 127.0.0.1 -n 30 > NUL");
            await WaitUntilOutputAsync(app, sandbox,
                text => text.Contains("Process start") && text.Contains(ProbeDataKey),
                ReadyTimeout, "probe process start event");

            // 外部杀死应用的真实 ETW 内核会话（复现线上故障），随后立即退出游戏：
            // 恢复流程的重建延迟 >= 1 秒，退出事件必然落在断流窗口内，只能靠对账补偿。
            var runtime = new EtwRuntime(null);
            runtime.StopSession(ResolveNewAppSessionName(runtime, preLaunchSessions));
            child.Kill(entireProcessTree: true);

            await WaitUntilAsync(() => ReadProbeRows(sandbox.CsvPath).Count > 0,
                PersistTimeout, "reconciled playtime.csv row");
            await WaitUntilOutputAsync(app, sandbox, text => text.Contains("Reconciling missing process stop for PID"),
                TimeSpan.FromSeconds(10), "reconciliation warning log");
            await WaitUntilOutputAsync(app, sandbox, text => text.Contains("ETW session recovered on attempt"),
                TimeSpan.FromSeconds(10), "session recovery log");

            var row = ReadProbeRows(sandbox.CsvPath)[0].Split(',');
            Assert.Equal(4, row.Length);
            Assert.False(string.IsNullOrWhiteSpace(row[2]), "reconciled session must be completed with an end_time");
            output.WriteLine($"Reconciled row: {string.Join(',', row)}");
        }
        finally
        {
            if (child is not null && !child.HasExited) child.Kill(entireProcessTree: true);
            child?.Dispose();
            StopAppAndSweepSessions(app);
        }
    }

    [WindowsOnlyFact]
    public async Task Monitor_WhenElevatedWithPublishedExe_ResumesAcrossTwoCrashesAndPersistsOneContinuousSession()
    {
        if (!TryGuard(out var exePath)) return;
        using var sandbox = CreateSandbox();
        AppHandle? app = null;
        Process? child = null;
        try
        {
            app = StartApp(exePath, sandbox);
            await WaitUntilOutputAsync(app, sandbox, text => text.Contains("GameAutomationService started"),
                ReadyTimeout, "initial monitor readiness");
            child = StartProbe(sandbox, "/c ping 127.0.0.1 -n 600 > NUL");
            await WaitUntilAsync(() => ReadInFlightSnapshot(sandbox)?.Sessions.Any(
                    session => session.DataKey == ProbeDataKey && session.ProcessId == child.Id) == true,
                ReadyTimeout, "initial probe snapshot");

            var initialSnapshot = ReadInFlightSnapshot(sandbox)!;
            var originalStart = Assert.Single(initialSnapshot.Sessions).StartedAt;
            var previousSnapshotAt = initialSnapshot.SnapshotAt;
            // Separate the original start and first restart even at CSV's one-second precision.
            await Task.Delay(TimeSpan.FromSeconds(2));

            for (var restart = 1; restart <= 2; restart++)
            {
                StopAppAndSweepSessions(app);
                app.Dispose();
                app = null;
                Assert.False(child.HasExited, "the game probe must survive each host crash");
                Assert.Empty(ReadProbeRows(sandbox.CsvPath));
                // Each launch truncates its redirected logs. Remove old contents before polling
                // so a prior launch's readiness/recovery line cannot satisfy this launch.
                File.Delete(sandbox.OutFile);
                File.Delete(sandbox.ErrFile);
                app = StartApp(exePath, sandbox);
                await WaitUntilOutputAsync(app, sandbox,
                    text => text.Contains("Resumed in-flight session")
                        && text.Contains("In-flight snapshot rewritten after recovery"),
                    ReadyTimeout, $"recovery and rewritten snapshot after crash {restart}");
                await WaitUntilAsync(() => ReadInFlightSnapshot(sandbox) is { } snapshot
                        && snapshot.SnapshotAt > previousSnapshotAt
                        && snapshot.Sessions.Any(session => session.DataKey == ProbeDataKey
                            && session.ProcessId == child.Id && session.StartedAt == originalStart),
                    ReadyTimeout, $"preserved original start and refreshed snapshot after crash {restart}");
                previousSnapshotAt = ReadInFlightSnapshot(sandbox)!.SnapshotAt;
                Assert.Empty(ReadProbeRows(sandbox.CsvPath));
            }

            var stoppedAt = DateTime.Now;
            child.Kill(entireProcessTree: true);
            await WaitUntilAsync(() => ReadProbeRows(sandbox.CsvPath).Count > 0,
                PersistTimeout, "completed session after adopted game process exits");
            var row = Assert.Single(ReadProbeRows(sandbox.CsvPath)).Split(',');
            Assert.Equal(4, row.Length);
            var recordedStart = DateTime.Parse(row[1], CultureInfo.InvariantCulture);
            var recordedEnd = DateTime.Parse(row[2], CultureInfo.InvariantCulture);
            Assert.InRange((recordedStart - originalStart).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(1));
            Assert.True(recordedEnd >= stoppedAt.AddSeconds(-1), "end must include both host restarts");
            Assert.Equal((long)(recordedEnd - recordedStart).TotalMinutes,
                long.Parse(row[3], CultureInfo.InvariantCulture));
            await WaitUntilAsync(() => ReadInFlightSnapshot(sandbox)?.Sessions.Count == 0,
                ReadyTimeout, "empty in-flight snapshot after game completion");
            output.WriteLine($"Two-crash continuous session: {string.Join(',', row)}; original start {originalStart:O}");
        }
        finally
        {
            if (child is not null && !child.HasExited) child.Kill(entireProcessTree: true);
            child?.Dispose();
            if (app is not null)
            {
                StopAppAndSweepSessions(app);
                app.Dispose();
            }
        }
    }

    private static InFlightSnapshot? ReadInFlightSnapshot(Sandbox sandbox)
    {
        try
        {
            var path = Path.Combine(sandbox.GameHelperDir, "inflight-sessions.json");
            return File.Exists(path)
                ? JsonSerializer.Deserialize<InFlightSnapshot>(File.ReadAllText(path, Encoding.UTF8))
                : null;
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    [WindowsOnlyFact]
    public async Task MonitorMode_WhenHardwareVerificationEnabled_VerifiesRealRegistersAcrossGameAndConfigChanges()
    {
        // Opt in only after disabling monitorMode in other running instances: DDC is machine-wide.
        if (Environment.GetEnvironmentVariable("GAMEHELPER_TEST_DDC") != "1")
        {
            output.WriteLine("Skipped physical DDC writes: set GAMEHELPER_TEST_DDC=1 with other preset writers disabled.");
            return;
        }
        if (!TryGuard(out var exePath)) return;
        var hdr = new WindowsHdrController(NullLogger<WindowsHdrController>.Instance);
        Assert.True(hdr.TryGetEnabled(out var hdrEnabled), "HDR state must be known before physical preset verification.");
        Assert.False(hdrEnabled, "Physical preset verification requires HDR off; this test never toggles HDR.");
        using var sandbox = CreateSandbox();
        var controller = new DdcMonitorModeController(NullLogger<DdcMonitorModeController>.Instance);
        Assert.True(controller.IsAvailable, "Physical DDC/CI monitor must be online for this verification.");
        var plan = new MonitorModePlan(new GameHelper.Core.Models.MonitorModeSettings { Enabled = true });
        File.AppendAllText(sandbox.ConfigPath, "\nmonitorMode:\n  enabled: true\n  daylightStart: '00:00'\n  daylightEnd: '23:59'\n");
        using var app = StartApp(exePath!, sandbox);
        Process? child = null;
        try
        {
            await WaitUntilOutputAsync(app, sandbox, text => text.Contains("GameAutomationService started"), ReadyTimeout, "DDC host ready");
            await VerifyRegistersAsync(GameHelper.Core.Abstractions.MonitorMode.Comfort, "startup without game");
            child = StartProbe(sandbox, "/c ping 127.0.0.1 -n 600 > NUL");
            await VerifyRegistersAsync(GameHelper.Core.Abstractions.MonitorMode.Game, "daylight game start");
            var dayConfig = File.ReadAllText(sandbox.ConfigPath);
            File.WriteAllText(sandbox.ConfigPath, dayConfig.Replace("daylightEnd: '23:59'", "daylightEnd: '00:00'"));
            await VerifyRegistersAsync(GameHelper.Core.Abstractions.MonitorMode.Comfort, "hot reload to night while game stays alive");
            File.WriteAllText(sandbox.ConfigPath, dayConfig);
            await VerifyRegistersAsync(GameHelper.Core.Abstractions.MonitorMode.Game, "hot reload to day while game stays alive");
            child.Kill(entireProcessTree: true);
            await VerifyRegistersAsync(GameHelper.Core.Abstractions.MonitorMode.Comfort, "game exit");
        }
        finally
        {
            if (child is not null && !child.HasExited) child.Kill(entireProcessTree: true);
            child?.Dispose();
            StopAppAndSweepSessions(app);
            controller.Apply(plan.ComfortWrites);
        }

        async Task VerifyRegistersAsync(GameHelper.Core.Abstractions.MonitorMode mode, string phase)
        {
            await WaitUntilAsync(() => controller.TryReadState(plan.ProbeWrites) is { } state && plan.MatchesTarget(state, mode),
                TimeSpan.FromSeconds(20), $"physical {mode} registers at {phase}");
            // Catch firmware reverting an incomplete recipe after apparently successful writes.
            await Task.Delay(TimeSpan.FromSeconds(3));
            var stable = controller.TryReadState(plan.ProbeWrites);
            Assert.NotNull(stable);
            Assert.True(plan.MatchesTarget(stable, mode), $"firmware reverted the {mode} recipe at {phase}");
            if (mode == GameHelper.Core.Abstractions.MonitorMode.Comfort)
            {
                // The single F0 write must make the firmware update the other two registers too.
                Assert.Equal(0u, stable[0xDC]);
                Assert.Equal(0x1Du, stable[0xE2]);
                Assert.Equal(0x0Cu, stable[0xF0]);
            }
            output.WriteLine($"Physical DDC {phase}: {string.Join(", ", stable.Select(p => $"{p.Key:X2}={p.Value:X2}"))}");
        }
    }

    private bool TryGuard(out string? exePath)
    {
        exePath = PublishedExeEndToEndTests.ResolvePublishExePath();
        if (exePath is null)
        {
            output.WriteLine("Skipped: published exe not found (run publish first per verify-and-deploy workflow).");
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            output.WriteLine("Skipped real ETW monitor verification: administrator required.");
            return false;
        }

        return true;
    }

    private static Sandbox CreateSandbox()
    {
        var sandbox = new Sandbox();
        File.WriteAllText(sandbox.ConfigPath, $"""
            monitor: ETW
            startup:
              autoStartMonitor: false
              launchOnStartup: false
            sync:
              enabled: false
            games:
              - dataKey: {ProbeDataKey}
                executable: {ProbeExeName}
                displayName: GH E2E Probe
                enabled: true
                hdr: false
            """);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), sandbox.ProbeExePath);
        return sandbox;
    }

    private static AppHandle StartApp(string exePath, Sandbox sandbox)
    {
        // 经 cmd /c 文件重定向拉起，复现已验证可用的无阻塞启动环境；stdin 预置一个换行。
        var arguments = $"/d /c \"\"{exePath}\" monitor < \"{sandbox.StdInFile}\"" +
                        $" > \"{sandbox.OutFile}\" 2> \"{sandbox.ErrFile}\"\"";
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.EnvironmentVariables["GAMEHELPER_DATA_DIR"] = sandbox.RootDir;
        startInfo.EnvironmentVariables["GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE"] = "1";
        startInfo.EnvironmentVariables["GAMEHELPER_ALLOW_MULTIPLE_INSTANCES"] = "1";

        return new AppHandle(Process.Start(startInfo)!);
    }

    private static Process StartProbe(Sandbox sandbox, string arguments) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = sandbox.ProbeExePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

    private static string ResolveNewAppSessionName(EtwRuntime runtime, IReadOnlyCollection<string> preLaunchSessions)
    {
        var candidates = runtime.GetActiveSessionNames()
            .Except(preLaunchSessions, StringComparer.Ordinal)
            .Where(name => name.StartsWith("GameHelper-ETW-", StringComparison.Ordinal))
            .Where(name => !runtime.InspectProcess(ParseOwnerPid(name)).HasExited)
            .ToList();
        Assert.True(candidates.Count == 1,
            $"Expected exactly one new ETW session owned by the test app, found: [{string.Join(", ", candidates)}]");
        return candidates[0];
    }

    private static int ParseOwnerPid(string sessionName)
    {
        var remainder = sessionName["GameHelper-ETW-".Length..];
        return int.Parse(remainder[..remainder.IndexOf('-')]);
    }

    /// <summary>
    /// 强杀测试实例（cmd 进程树，专属沙盒实例，绝非用户实例），随后只清理属主已死亡的
    /// GameHelper ETW 会话；属主存活的会话可能是用户实例，绝不动。
    /// </summary>
    private static void StopAppAndSweepSessions(AppHandle app)
    {
        app.Kill();
        Thread.Sleep(TimeSpan.FromSeconds(2));
        var runtime = new EtwRuntime(null);
        foreach (var name in runtime.GetActiveSessionNames())
        {
            if (!name.StartsWith("GameHelper-ETW-", StringComparison.Ordinal)) continue;
            var ownerPid = ParseOwnerPid(name);
            // 属主已死才清理；PID 复用导致的误判只是留下孤儿会话，方向是安全的。
            if (!runtime.InspectProcess(ownerPid).HasExited) continue;
            try { runtime.StopSession(name); } catch { /* 测试清理尽力而为 */ }
        }
    }

    /// <summary>以共享读写方式读取重定向文件；GBK/UTF-8 差异不影响 ASCII 断言。</summary>
    private static string ReadTextFile(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.Latin1);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static List<string> ReadProbeRows(string csvPath) =>
        ReadTextFile(csvPath)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith(ProbeDataKey + ",", StringComparison.Ordinal))
            .ToList();

    private async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string description)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < timeout) await Task.Delay(100);
        Assert.True(condition(), $"Expected {description} within {timeout.TotalSeconds:F0} seconds.");
    }

    private async Task WaitUntilOutputAsync(AppHandle app, Sandbox sandbox, Func<string, bool> predicate,
        TimeSpan timeout, string description)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate(AppOutput(sandbox)) && timer.Elapsed < timeout) await Task.Delay(100);
        if (!predicate(AppOutput(sandbox)))
        {
            output.WriteLine($"[dump] app output while waiting for '{description}':");
            foreach (var line in AppOutput(sandbox).Split('\n').TakeLast(40)) output.WriteLine("[dump] " + line.TrimEnd('\r'));
        }

        Assert.True(predicate(AppOutput(sandbox)), $"Expected {description} within {timeout.TotalSeconds:F0} seconds.");
    }

    private static string AppOutput(Sandbox sandbox) => ReadTextFile(sandbox.OutFile) + ReadTextFile(sandbox.ErrFile);

    private sealed class AppHandle(Process process) : IDisposable
    {
        public void Kill()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* 进程可能已退出 */ }
            try { process.WaitForExit(5_000); } catch { /* 尽力等待 */ }
        }

        public void Dispose() => Kill();
    }

    /// <summary>独立的数据沙盒：目录隔离，测试后自动清理，绝不触碰真实 AppData。</summary>
    private sealed class Sandbox : IDisposable
    {
        public Sandbox()
        {
            RootDir = Path.Combine(Path.GetTempPath(), "gh-e2e-monitor", Guid.NewGuid().ToString("N"));
            GameHelperDir = Path.Combine(RootDir, "GameHelper");
            Directory.CreateDirectory(GameHelperDir);
            ConfigPath = Path.Combine(GameHelperDir, "config.yml");
            CsvPath = Path.Combine(GameHelperDir, "playtime.csv");
            ProbeExePath = Path.Combine(RootDir, ProbeExeName);
            StdInFile = Path.Combine(RootDir, "stdin.txt");
            OutFile = Path.Combine(RootDir, "app-out.log");
            ErrFile = Path.Combine(RootDir, "app-err.log");
            File.WriteAllText(StdInFile, "\n");
        }

        public string RootDir { get; }
        public string GameHelperDir { get; }
        public string ConfigPath { get; }
        public string CsvPath { get; }
        public string ProbeExePath { get; }
        public string StdInFile { get; }
        public string OutFile { get; }
        public string ErrFile { get; }

        public void Dispose()
        {
            try { Directory.Delete(RootDir, recursive: true); }
            catch { /* 沙盒清理失败不应掩盖测试结果。 */ }
        }
    }
}
