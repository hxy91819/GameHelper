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
    public async Task ControllerCompanion_PublishedLaunchStartsHelperFirstAndReusesItAcrossGames()
    {
        if (!TryGuard(out var exePath)) return;
        using var sandbox = CreateSandbox();
        var compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
        Assert.True(File.Exists(compiler), "Windows .NET Framework compiler required for an isolated GUI probe.");
        var code = Path.Combine(sandbox.RootDir, "probe.cs");
        var companion = Path.Combine(sandbox.RootDir, "gh-controller-" + Guid.NewGuid().ToString("N") + ".exe");
        var game = Path.Combine(sandbox.RootDir, "gh-controlled-game.exe");
        var marker = Path.Combine(sandbox.RootDir, "markers"); Directory.CreateDirectory(marker);
        await File.WriteAllTextAsync(code, """
            using System;
            using System.Diagnostics;
            using System.IO;
            using System.Windows.Forms;
            class QuietForm : Form { protected override bool ShowWithoutActivation { get { return true; } } }
            class Probe {
              [STAThread] static void Main() {
                var marker = Environment.GetEnvironmentVariable("GH_TEST_COMPANION_MARKER");
                var name = Path.GetFileNameWithoutExtension(Application.ExecutablePath);
                if (name == "gh-controlled-game" && Directory.GetFiles(marker, "gh-controller-*.txt").Length != 1) return;
                File.WriteAllText(Path.Combine(marker, name + ".txt"), Process.GetCurrentProcess().Id.ToString());
                Application.Run(new QuietForm { Opacity = 0, ShowInTaskbar = false, WindowState = FormWindowState.Minimized });
              }
            }
            """);
        var compile = new ProcessStartInfo(compiler) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "/nologo", "/target:winexe", "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll", "/out:" + companion, code }) compile.ArgumentList.Add(arg);
        using (var compilerProcess = Process.Start(compile)!) { await compilerProcess.WaitForExitAsync(); Assert.Equal(0, compilerProcess.ExitCode); }
        File.Copy(companion, game);
        var source = new YamlConfigProvider(sandbox.ConfigPath);
        source.Change(config => config.Games = [new() { DataKey = "controller-game", Executable = game, DisplayName = "PS Controller Game", IsEnabled = false,
            Launch = new() { Target = game, Ds4WindowsPath = companion } }]);
        using var app = StartApp(exePath!, sandbox, new Dictionary<string, string> { ["GH_TEST_COMPANION_MARKER"] = marker });
        try
        {
            await WaitUntilOutputAsync(app, sandbox, text => text.Contains("GameAutomationService started"), ReadyTimeout, "controller runtime ready");
            var client = new GameLibraryPipe(sandbox.ConfigPath);
            var request = new GameLibraryRequest { Operation = GameLibraryOperation.Launch, DataKey = "controller-game" };
            var first = await client.ExecuteAsync(request); Assert.True(first.Success, first.Error);
            var gameMarker = Path.Combine(marker, "gh-controlled-game.txt");
            await WaitUntilAsync(() => int.TryParse(ReadTextFile(gameMarker), out _), ReadyTimeout, "companion started before game");
            var companionMarker = Directory.GetFiles(marker, "gh-controller-*.txt").Single();
            var companionId = int.Parse(File.ReadAllText(companionMarker), CultureInfo.InvariantCulture);
            using (var child = Process.GetProcessById(int.Parse(File.ReadAllText(gameMarker), CultureInfo.InvariantCulture)))
            { child.Kill(); await child.WaitForExitAsync(); }
            File.Delete(gameMarker);
            var second = await client.ExecuteAsync(request); Assert.True(second.Success, second.Error);
            await WaitUntilAsync(() => int.TryParse(ReadTextFile(gameMarker), out _), ReadyTimeout, "second game launch");
            Assert.Equal(companionId.ToString(CultureInfo.InvariantCulture), File.ReadAllText(companionMarker));
            using var running = Process.GetProcessById(companionId); Assert.False(running.HasExited);
            var screenshots = Environment.GetEnvironmentVariable("GAMEHELPER_TEST_CONTROLLER_SCREENSHOTS") ?? Path.Combine(sandbox.RootDir, "controller-ui");
            var start = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(exePath)!, "desktop", "GameHelper.Desktop.exe")) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "--config", sandbox.ConfigPath, "--snapshot", screenshots }) start.ArgumentList.Add(arg);
            start.Environment["GAMEHELPER_DATA_DIR"] = sandbox.RootDir;
            using var ui = Process.Start(start)!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            try { await ui.WaitForExitAsync(deadline.Token); } finally { if (!ui.HasExited) ui.Kill(); }
            Assert.Equal(0, ui.ExitCode); Assert.True(File.Exists(Path.Combine(screenshots, "launch-options.png")));
            output.WriteLine("Isolated GUI companion started before the game, reused across launches, and remains running; native option screenshot: " + screenshots);
        }
        finally
        {
            StopAppAndSweepSessions(app);
            foreach (var file in Directory.GetFiles(marker, "*.txt"))
            {
                try
                {
                    using var owned = Process.GetProcessById(int.Parse(File.ReadAllText(file), CultureInfo.InvariantCulture));
                    if (owned.MainModule?.FileName is { } path && (path.Equals(game, StringComparison.OrdinalIgnoreCase) || path.Equals(companion, StringComparison.OrdinalIgnoreCase)))
                    { owned.Kill(); await owned.WaitForExitAsync(); }
                }
                catch (ArgumentException) { /* Owned probe already exited. */ }
            }
        }
    }

    [WindowsOnlyFact]
    public async Task Statistics_PublishedRuntimeRendersDashboardAndPaginatesWithoutChangingHistory()
    {
        if (!TryGuard(out var exePath)) return;
        using var sandbox = CreateSandbox();
        var source = new YamlConfigProvider(sandbox.ConfigPath);
        var names = new[] { "Hades II", "Stardew Valley", "Baldur's Gate 3", "Elden Ring" };
        source.Change(config => config.Games = names.Select((name, index) => new GameConfig
        { DataKey = "stats-" + index, Executable = "gh-stats-never-running-" + index + ".exe", DisplayName = name, IsEnabled = false }).ToList());
        var anchor = TimeProvider.System.GetLocalNow().DateTime.AddHours(-2);
        var csv = new StringBuilder("game,start_time,end_time,duration_minutes\n");
        long totalMinutes = 0;
        for (var index = 0; index < 46; index++)
        {
            var minutes = (index % 5 + 1) * 25;
            var end = index == 45 ? anchor.AddDays(-500) : anchor.AddDays(-index);
            var key = index == 45 ? "removed-game" : "stats-" + index % 4;
            csv.AppendLine(FormattableString.Invariant($"{key},{end.AddMinutes(-minutes):O},{end:O},{minutes}"));
            totalMinutes += minutes;
        }
        File.WriteAllText(sandbox.CsvPath, csv.ToString());
        var original = File.ReadAllBytes(sandbox.CsvPath);
        using var app = StartApp(exePath!, sandbox);
        try
        {
            await WaitUntilOutputAsync(app, sandbox, text => text.Contains("GameAutomationService started"), ReadyTimeout, "statistics runtime ready");
            var client = new GameLibraryPipe(sandbox.ConfigPath);
            var all = await client.ExecuteAsync(new GameLibraryRequest { Operation = GameLibraryOperation.Statistics, Statistics = new(0) });
            Assert.True(all.Success, all.Error);
            var data = Assert.IsType<PlaytimeDashboard>(all.Statistics);
            Assert.Equal(totalMinutes, data.Minutes);
            Assert.Equal(46, data.Sessions);
            Assert.Equal(5, data.GamesPlayed);
            Assert.Equal(30, data.Recent.Count);
            Assert.Contains(data.Games, game => game.Name == "Hades II");
            Assert.Contains(data.Games, game => game.DataKey == "removed-game");
            var next = await client.ExecuteAsync(new GameLibraryRequest { Operation = GameLibraryOperation.Statistics, Statistics = new(0, Offset: 30) });
            Assert.Equal(16, next.Statistics!.Recent.Count);
            Assert.Equal(totalMinutes, next.Statistics.Minutes);
            var desktop = Path.Combine(Path.GetDirectoryName(exePath)!, "desktop", "GameHelper.Desktop.exe");
            var screenshots = Environment.GetEnvironmentVariable("GAMEHELPER_TEST_STATISTICS_SCREENSHOTS") ?? Path.Combine(sandbox.RootDir, "statistics-ui");
            var start = new ProcessStartInfo(desktop) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--config"); start.ArgumentList.Add(sandbox.ConfigPath);
            start.ArgumentList.Add("--snapshot"); start.ArgumentList.Add(screenshots);
            start.Environment["GAMEHELPER_DATA_DIR"] = sandbox.RootDir;
            start.Environment["GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE"] = "1";
            using var ui = Process.Start(start)!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            try { await ui.WaitForExitAsync(deadline.Token); }
            finally { if (!ui.HasExited) ui.Kill(); }
            Assert.Equal(0, ui.ExitCode);
            Assert.True(File.Exists(Path.Combine(screenshots, "statistics-all.png")));
            Assert.True(File.Exists(Path.Combine(screenshots, "statistics-calendar.png")));
            Assert.True(File.Exists(Path.Combine(screenshots, "statistics-small.png")));
            Assert.Equal(original, await File.ReadAllBytesAsync(sandbox.CsvPath));
            output.WriteLine("Read-only statistics IPC, persisted totals, unknown-game retention, pagination and native WPF dashboard verified: " + screenshots);
        }
        finally { StopAppAndSweepSessions(app); }
    }

    [SteamPosterLiveFact]
    public async Task PosterNameSearch_QueriesSteamByTitleAndDownloadsUniqueExactMatch()
    {
        using var sandbox = CreateSandbox();
        using var http = new System.Net.Http.HttpClient();
        var client = new GameHelper.Infrastructure.Artwork.SteamArtworkClient(http, Path.Combine(sandbox.RootDir, "posters"),
            GameHelper.Desktop.LocalGameArtwork.IsValidPoster);
        var results = await client.SearchAsync("Portal 2", CancellationToken.None);
        var exact = GameHelper.Infrastructure.Artwork.SteamArtworkNameMatcher.ExactMatch("Portal 2™", results);
        Assert.NotNull(exact); Assert.Equal(620u, exact.AppId);
        var image = await client.GetPosterAsync(exact.AppId, CancellationToken.None);
        Assert.NotNull(image); Assert.True(File.Exists(image));
        var astral = await client.SearchAsync("Astral Ascent", CancellationToken.None);
        var best = GameHelper.Infrastructure.Artwork.SteamArtworkNameMatcher.BestMatch("Astral Ascent", astral);
        Assert.NotNull(best); Assert.Equal(1280930u, best.AppId); Assert.True(best.IsGame);
        Assert.NotNull(await client.GetPosterAsync(best.AppId, CancellationToken.None));
    }

    [WindowsOnlyFact]
    public async Task DirectoryDiscovery_PublishedRuntimeScansImportsAndSeparatesMissingGames()
    {
        if (!TryGuard(out var exePath)) return;
        using var sandbox = CreateSandbox();
        var missing = Path.Combine(sandbox.RootDir, "removed-game.exe");
        var source = new YamlConfigProvider(sandbox.ConfigPath);
        source.Change(config => config.Games =
        [new() { DataKey = "missing", Executable = missing, DisplayName = "No longer installed", IsEnabled = false },
         new() { DataKey = "unknown", Executable = "gh-discovery-unlocated.exe", DisplayName = "Legacy game", IsEnabled = false }]);
        var libraryRoot = Path.Combine(sandbox.RootDir, "Library");
        var gameRoot = Path.Combine(libraryRoot, "Found Adventure"); Directory.CreateDirectory(gameRoot);
        var executable = Path.Combine(gameRoot, "gh-found-adventure.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable);
        File.WriteAllBytes(Path.Combine(gameRoot, "uninstall.exe"), [1]);
        using var app = StartApp(exePath!, sandbox);
        try
        {
            await WaitUntilOutputAsync(app, sandbox, text => text.Contains("GameAutomationService started"), ReadyTimeout, "discovery runtime ready");
            var client = new GameLibraryPipe(sandbox.ConfigPath);
            var original = File.ReadAllText(sandbox.ConfigPath);
            var scan = await client.ExecuteAsync(new GameLibraryRequest { Operation = GameLibraryOperation.DiscoverDirectory, Directory = libraryRoot });
            Assert.True(scan.Success, scan.Error); var candidate = Assert.Single(scan.LocalScan!.Candidates);
            Assert.Equal(executable, candidate.ExecutablePath); Assert.False(candidate.AlreadyAdded);
            Assert.Equal(original, File.ReadAllText(sandbox.ConfigPath));
            var imported = await client.ExecuteAsync(new GameLibraryRequest { Operation = GameLibraryOperation.ImportDirectory, LocalGames = [candidate] });
            Assert.True(imported.Success, imported.Error);
            var found = imported.Games.Single(item => item.Config.Executable == executable);
            Assert.Equal(GameInstallationState.Installed, found.Installation.State);
            Assert.False(found.Config.HdrEnabled); Assert.Null(found.Config.SpeedMultiplier);
            Assert.NotNull(found.Config.AddedAt);
            Assert.Equal(found.Config.AddedAt, new YamlConfigProvider(sandbox.ConfigPath).Read().Games.Single(item => item.DataKey == found.Config.DataKey).AddedAt);
            Assert.Equal(GameInstallationState.Missing, imported.Games.Single(item => item.Config.DataKey == "missing").Installation.State);
            Assert.Equal(GameInstallationState.Unknown, imported.Games.Single(item => item.Config.DataKey == "unknown").Installation.State);
            var afterImport = File.ReadAllText(sandbox.ConfigPath);
            var duplicate = await client.ExecuteAsync(new GameLibraryRequest { Operation = GameLibraryOperation.ImportDirectory, LocalGames = [candidate] });
            Assert.Equal(3, duplicate.Games.Count); Assert.Equal(afterImport, File.ReadAllText(sandbox.ConfigPath));
            var screenshots = Environment.GetEnvironmentVariable("GAMEHELPER_TEST_DISCOVERY_SCREENSHOTS") ?? Path.Combine(sandbox.RootDir, "discovery-ui");
            var start = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(exePath)!, "desktop", "GameHelper.Desktop.exe")) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "--config", sandbox.ConfigPath, "--snapshot", screenshots }) start.ArgumentList.Add(argument);
            start.Environment["GAMEHELPER_DATA_DIR"] = sandbox.RootDir;
            start.Environment["GAMEHELPER_TEST_SCAN_DIRECTORY"] = libraryRoot;
            using var ui = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            try { await ui.WaitForExitAsync(timeout.Token); } finally { if (!ui.HasExited) ui.Kill(); }
            Assert.Equal(0, ui.ExitCode);
            Assert.True(File.Exists(Path.Combine(screenshots, "directory-scan.png")));
            Assert.True(File.Exists(Path.Combine(screenshots, "installed-default.png")));
            Assert.Equal(afterImport, File.ReadAllText(sandbox.ConfigPath));
            File.Delete(executable);
            var gone = await client.ExecuteAsync(new GameLibraryRequest { Operation = GameLibraryOperation.List });
            Assert.Equal(GameInstallationState.Missing, gone.Games.Single(item => item.Config.Executable == executable).Installation.State);
            output.WriteLine("Published directory scan/import/defaults/idempotency/installation grouping and native picker verified: " + screenshots);
        }
        finally { StopAppAndSweepSessions(app); }
    }

    [SteamPosterLiveFact]
    public async Task SteamPoster_PublishedDesktopDownloadsCachesAndRendersWithoutChangingGameSettings()
    {
        if (!TryGuard(out var exePath)) return;
        using var sandbox = CreateSandbox();
        var source = new YamlConfigProvider(sandbox.ConfigPath);
        source.Change(config =>
        {
            var game = config.Games.Single();
            game.DisplayName = "Portal 2";
            game.Launch = new GameLaunchOptions { Target = "steam://rungameid/620" };
        });
        var original = File.ReadAllText(sandbox.ConfigPath);
        using var app = StartApp(exePath!, sandbox);
        var desktop = Path.Combine(Path.GetDirectoryName(exePath)!, "desktop", "GameHelper.Desktop.exe");
        ProcessStartInfo StartDesktop(params string[] arguments)
        {
            var start = new ProcessStartInfo(desktop) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--config"); start.ArgumentList.Add(sandbox.ConfigPath);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["GAMEHELPER_TEST_OFFSCREEN"] = "1";
            start.Environment["GAMEHELPER_DATA_DIR"] = sandbox.RootDir;
            start.Environment["GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE"] = "1";
            start.Environment["GAMEHELPER_ALLOW_MULTIPLE_INSTANCES"] = "1";
            return start;
        }
        Process? ui = null;
        try
        {
            await WaitUntilOutputAsync(app, sandbox, text => text.Contains("GameAutomationService started"), ReadyTimeout, "poster runtime ready");
            ui = Process.Start(StartDesktop("--restore"))!;
            var cached = Path.Combine(sandbox.GameHelperDir, "cache", "steam-posters", "620.jpg");
            await WaitUntilAsync(() => File.Exists(cached), TimeSpan.FromSeconds(45), "official Steam poster downloaded to isolated cache");
            ui.Refresh();
            Assert.False(ui.HasExited);
            var screenshots = Environment.GetEnvironmentVariable("GAMEHELPER_TEST_POSTER_SCREENSHOTS") ?? Path.Combine(sandbox.RootDir, "posters-ui");
            using var snapshot = Process.Start(StartDesktop("--snapshot", screenshots))!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await snapshot.WaitForExitAsync(deadline.Token); }
            finally { if (!snapshot.HasExited) snapshot.Kill(); }
            Assert.Equal(0, snapshot.ExitCode);
            Assert.Contains("Posters: 1", await File.ReadAllTextAsync(Path.Combine(screenshots, "verification.txt")));
            Assert.Equal(original, await File.ReadAllTextAsync(sandbox.ConfigPath));
            output.WriteLine("Official Steam poster download, isolated persistent cache and WPF grid/list rendering passed: " + screenshots);
        }
        finally
        {
            if (ui is not null) { if (!ui.HasExited) { ui.Kill(); ui.WaitForExit(5000); } ui.Dispose(); }
            StopAppAndSweepSessions(app);
        }
    }

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
            using var probe = StartProbe(sandbox, "3");
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
            Target = sandbox.ProbeExePath, Arguments = "4"
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

            var ready = await new GameLibraryPipe(sandbox.ConfigPath).ExecuteAsync(new GameLibraryRequest { Operation = GameLibraryOperation.List });
            var visibility = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../..", "scripts/probe-console-window.ps1")),
                "-TargetProcessId", ready.RuntimeProcessId.ToString(CultureInfo.InvariantCulture), "-RequireNoConsole" }) visibility.ArgumentList.Add(argument);
            using (var probe = Process.Start(visibility)!)
            {
                var stdout = probe.StandardOutput.ReadToEndAsync(); var stderr = probe.StandardError.ReadToEndAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await probe.WaitForExitAsync(deadline.Token); } finally { if (!probe.HasExited) probe.Kill(entireProcessTree: true); }
                Assert.True(probe.ExitCode == 0, await stdout + await stderr);
            }

            using var child = StartProbe(sandbox, "3");
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

            child = StartProbe(sandbox, "29");
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
            child = StartProbe(sandbox, "600");
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
                // StartApp clears the background log so prior readiness cannot satisfy this launch.
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
            child = StartProbe(sandbox, "600");
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
        // A console-subsystem fake game would open a window through the real game launcher.
        var source = Path.Combine(sandbox.RootDir, "windowless-probe.cs");
        File.WriteAllText(source, "using System; using System.Threading; class Probe { static void Main(string[] args) { Thread.Sleep(int.Parse(args[0]) * 1000); } }");
        var compile = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "/nologo", "/target:winexe", "/out:" + sandbox.ProbeExePath, source }) compile.ArgumentList.Add(argument);
        using var compiler = Process.Start(compile)!;
        if (!compiler.WaitForExit(15000)) { compiler.Kill(entireProcessTree: true); throw new TimeoutException("Windowless probe compilation timed out."); }
        Assert.Equal(0, compiler.ExitCode);
        return sandbox;
    }

    private static AppHandle StartApp(string exePath, Sandbox sandbox, IReadOnlyDictionary<string, string>? environment = null)
    {
        File.Delete(sandbox.ConfigPath + ".runtime.log");
        var startInfo = GameHelper.Infrastructure.Startup.BackgroundRuntime.CreateStartInfo(exePath, sandbox.ConfigPath);
        startInfo.EnvironmentVariables["GAMEHELPER_DATA_DIR"] = sandbox.RootDir;
        startInfo.EnvironmentVariables["GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE"] = "1";
        startInfo.EnvironmentVariables["GAMEHELPER_ALLOW_MULTIPLE_INSTANCES"] = "1";
        if (environment is not null) foreach (var pair in environment) startInfo.EnvironmentVariables[pair.Key] = pair.Value;

        var process = Process.Start(startInfo)!;
        process.StandardInput.Close(); process.StandardOutput.Close(); process.StandardError.Close();
        return new AppHandle(process);
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
    /// 强杀测试实例（后台进程树，专属沙盒实例，绝非用户实例），随后只清理属主已死亡的
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

    private static string AppOutput(Sandbox sandbox) => ReadTextFile(sandbox.ConfigPath + ".runtime.log");

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
        }

        public string RootDir { get; }
        public string GameHelperDir { get; }
        public string ConfigPath { get; }
        public string CsvPath { get; }
        public string ProbeExePath { get; }

        public void Dispose()
        {
            try { Directory.Delete(RootDir, recursive: true); }
            catch { /* 沙盒清理失败不应掩盖测试结果。 */ }
        }
    }
}

public sealed class SteamPosterLiveFactAttribute : FactAttribute
{
    public SteamPosterLiveFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GAMEHELPER_TEST_STEAM_POSTER") != "1")
            Skip = "Live Steam poster verification requires GAMEHELPER_TEST_STEAM_POSTER=1.";
    }
}
