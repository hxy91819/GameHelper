using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Infrastructure.Controllers;
using GameHelper.Infrastructure.Ipc;
using GameHelper.Infrastructure.Providers;
using GameHelper.Infrastructure.Validators;
using Moq;

namespace GameHelper.Tests;

public sealed class GlobalSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GameHelper.GlobalSettings." + Guid.NewGuid().ToString("N"));
    private YamlConfigProvider Provider => new(Path.Combine(_root, "config.yml"));
    public GlobalSettingsTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Save_RoundTripsAllSettings_AppliesStartupAndReload_PreservesGames()
    {
        Provider.Change(config => config.Games.Add(new GameConfig
        {
            DataKey = "game", Executable = "game.exe", SpeedMultiplier = 2,
            SpeedHotkeys = [new() { Hotkey = "Ctrl+F1", Speed = 1 }, new() { Hotkey = "Ctrl+F2" }]
        }));
        var autoStart = new Mock<IAutoStartManager>(); var automation = new Mock<IGameAutomationService>();
        var hdr = new Mock<IHdrController>();
        IReadOnlyList<HdrDisplayInfo> displays = [new("TV", "tv-path", true, false, false, true)];
        hdr.Setup(controller => controller.TryGetDisplays(out displays)).Returns(true);
        var service = new SettingsService(Provider, autoStart.Object, automation.Object, hdr.Object);
        var request = new UpdateAppSettingsRequest
        {
            ProcessMonitorType = ProcessMonitorType.WMI, AutoStartInteractiveMonitor = true, LaunchOnSystemStartup = true,
            Hdr = new() { AllowedDisplays = ["tv-path"], DisabledDisplays = ["desk-path"] },
            BreakReminder = new() { Enabled = true, PlayMinutes = 30, BreakMinutes = 4, RepeatMinutes = 8, Sound = false },
            MonitorMode = new() { Enabled = true, DaylightStart = "08:00" },
            CheatEngine = new() { Executable = @"C:\Tools\cheatengine.exe" },
            SyncSettings = new() { Enabled = true, Repo = "owner/repo", Method = "api", Token = "test-token", IntervalMinutes = 60 }
        };
        var saved = service.Update(request);
        autoStart.Verify(manager => manager.SetEnabled(true), Times.Once);
        automation.Verify(owner => owner.ReloadConfig(), Times.Once);
        Assert.Equal(ProcessMonitorType.WMI, saved.ProcessMonitorType);
        Assert.Equal(request.BreakReminder, saved.BreakReminder);
        Assert.Equal("08:00", saved.MonitorMode!.DaylightStart);
        Assert.Equal(request.CheatEngine.Executable, saved.CheatEngine!.Executable);
        Assert.Equal("owner/repo", saved.SyncSettings!.Repo);
        Assert.Equal("test-token", saved.SyncSettings.Token);
        Assert.Equal("tv-path", Assert.Single(saved.Hdr!.AllowedDisplays));
        Assert.Equal("desk-path", Assert.Single(saved.Hdr.DisabledDisplays));
        Assert.True(saved.DisplaysAvailable); Assert.Single(saved.Displays);
        Assert.Contains(nameof(AppConfig.ProcessMonitorType), saved.RestartRequired);
        Assert.Contains(nameof(AppConfig.ProcessMonitorType), service.Get().RestartRequired);
        var game = Assert.Single(Provider.Read().Games);
        Assert.Equal("game", game.DataKey);
        Assert.Equal(2, game.SpeedMultiplier);
        Assert.Equal(new double?[] { 1, null }, game.SpeedHotkeys!.Select(binding => binding.Speed));
        Assert.Equal(new[] { "Ctrl+F1", "Ctrl+F2" }, game.SpeedHotkeys.Select(binding => binding.Hotkey));
        request.Hdr.AllowedDisplays.Clear(); saved.Hdr.AllowedDisplays.Clear();
        Assert.Single(service.Get().Hdr!.AllowedDisplays);
        Assert.True(YamlConfigValidator.Validate(Path.Combine(_root, "config.yml")).IsValid);
    }

    [Theory]
    [InlineData("monitor")] [InlineData("reminder")] [InlineData("ddc")] [InlineData("ce")] [InlineData("sync")] [InlineData("hdr")] [InlineData("ddc-null")]
    public void InvalidSettings_DoNotCommitOrApply(string field)
    {
        Provider.Change(config => config.AutoStartInteractiveMonitor = true);
        var before = File.ReadAllText(Path.Combine(_root, "config.yml"));
        var autoStart = new Mock<IAutoStartManager>(); var automation = new Mock<IGameAutomationService>();
        var service = new SettingsService(Provider, autoStart.Object, automation.Object);
        var request = new UpdateAppSettingsRequest { LaunchOnSystemStartup = true };
        switch (field)
        {
            case "monitor": request.ProcessMonitorType = (ProcessMonitorType)99; break;
            case "reminder": request.BreakReminder = new() { PlayMinutes = 0 }; break;
            case "ddc": request.MonitorMode = new() { GamePresets = [] }; break;
            case "ddc-null": request.MonitorMode = new() { GamePresets = null! }; break;
            case "ce": request.CheatEngine = new() { Executable = "relative.exe" }; break;
            case "sync": request.SyncSettings = new() { Enabled = true, Repo = "bad" }; break;
            case "hdr": request.Hdr = new() { AllowedDisplays = [""] }; break;
        }
        Assert.Throws<ArgumentException>(() => service.Update(request));
        Assert.Equal(before, File.ReadAllText(Path.Combine(_root, "config.yml")));
        autoStart.Verify(manager => manager.SetEnabled(It.IsAny<bool>()), Times.Never);
        automation.Verify(owner => owner.ReloadConfig(), Times.Never);
    }

    [Fact]
    public void ApplyFailure_ReturnsSavedSnapshotAndWarning_StillReloads()
    {
        var autoStart = new Mock<IAutoStartManager>(); var automation = new Mock<IGameAutomationService>();
        autoStart.Setup(manager => manager.SetEnabled(true)).Throws(new IOException("registry unavailable"));
        var saved = new SettingsService(Provider, autoStart.Object, automation.Object).Update(new() { LaunchOnSystemStartup = true });
        Assert.True(saved.LaunchOnSystemStartup); Assert.Contains("registry unavailable", saved.ApplyWarning);
        automation.Verify(owner => owner.ReloadConfig(), Times.Once);
    }

    [Theory]
    [InlineData("processMonitorType: Unknown")]
    [InlineData("launchOnSystemStartup: maybe")]
    [InlineData("autoStartInteractiveMonitor: [true]")]
    [InlineData("monitorMode:\n  daylightStart: invalid")]
    [InlineData("monitorMode:\n  gamePresets: []")]
    [InlineData("cheatEngine:\n  executable: relative.exe")]
    [InlineData("sync:\n  enabled: true\n  repo: bad")]
    [InlineData("sync:\n  method: unknown")]
    [InlineData("sync:\n  intervalMinutes: 1")]
    public void YamlValidator_RejectsInvalidGlobalSettingsWithoutGames(string yaml)
    {
        var path = Path.Combine(_root, "config.yml"); File.WriteAllText(path, yaml);
        Assert.False(YamlConfigValidator.Validate(path).IsValid);
    }

    [Fact]
    public void DisabledUnconfiguredSync_CanBeSavedWithoutARepository()
    {
        var saved = new SettingsService(Provider).Update(new() { SyncSettings = new() });
        Assert.False(saved.SyncSettings!.Enabled);
        Assert.Equal("", saved.SyncSettings.Repo);
        Assert.True(YamlConfigValidator.Validate(Path.Combine(_root, "config.yml")).IsValid);
    }

    [Fact]
    public void CandidateRead_SkipsFailuresAndKeepsDefaultCodeOrder()
    {
        var visited = new List<byte>();
        var values = DdcMonitorModeController.ReadCandidates(code =>
        {
            visited.Add(code);
            if (code == 0xE2) throw new IOException("unsupported");
            return code == 0xF0 ? null : 5u;
        });
        Assert.Equal(new byte[] { 0xDC, 0xE2, 0xF0, 0x14 }, visited);
        Assert.Equal(new byte[] { 0xDC, 0x14 }, values.Select(value => value.Code));
    }

    [Fact]
    public void Difference_OnlyIncludesCommonChangedCodes_AndCanRestoreBaseline()
    {
        var before = new MonitorModeSnapshot("monitor", [new(0xDC, 0), new(0xE2, 29), new(0xF0, 12)]);
        var after = new MonitorModeSnapshot("monitor", [new(0xDC, 5), new(0xE2, 29), new(0x14, 9)]);
        Assert.Equal(new MonitorModeWrite(0xDC, 5), Assert.Single(MonitorModeSnapshot.Difference(before, after)));
        Assert.Equal(new MonitorModeWrite(0xDC, 0), Assert.Single(MonitorModeSnapshot.Difference(after, before)));
        Assert.Empty(MonitorModeSnapshot.Difference(before, before));
        Assert.Throws<ArgumentException>(() => MonitorModeSnapshot.Difference(before, after with { TargetId = "other" }));
    }

    [Fact]
    public async Task Ipc_DispatchesSettingsSnapshotsAndSync_WithoutTouchingCatalog()
    {
        var settings = new Mock<ISettingsService>(); var sync = new Mock<ISyncService>();
        var monitorMode = new Mock<IMonitorModeController>(); var catalog = new Mock<IGameCatalogService>(MockBehavior.Strict);
        var snapshot = new AppSettingsSnapshot { LaunchOnSystemStartup = true };
        settings.Setup(service => service.Get()).Returns(snapshot);
        settings.Setup(service => service.Update(It.IsAny<UpdateAppSettingsRequest>())).Returns(snapshot);
        monitorMode.Setup(controller => controller.ReadSnapshot()).Returns(new MonitorModeSnapshot("test", [new(0xDC, 1)]));
        sync.Setup(service => service.GetStatus()).Returns(new SyncStatusInfo { Repo = "owner/repo" });
        sync.Setup(service => service.ValidateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new SyncOutcome(SyncOutcomeStatus.Validated));
        sync.Setup(service => service.SyncNowAsync(true, It.IsAny<CancellationToken>())).ReturnsAsync(SyncOutcome.UploadedCommit("commit"));
        var library = new GameLibrary(Provider, catalog.Object, Mock.Of<IGameAutomationService>(), Mock.Of<IMonitorControlService>(),
            Mock.Of<IGameLaunchCoordinator>(), Mock.Of<ISteamGameResolver>(), Mock.Of<IPlaytimeSnapshotProvider>(),
            settings: settings.Object, monitorMode: monitorMode.Object, sync: sync.Object);
        foreach (var operation in new[] { GameLibraryOperation.GetSettings, GameLibraryOperation.UpdateSettings,
                     GameLibraryOperation.DdcSnapshot, GameLibraryOperation.SyncStatus, GameLibraryOperation.SyncValidate, GameLibraryOperation.SyncPush })
        {
            var request = await PipeRoundTripAsync(new GameLibraryRequest
                { Operation = operation, Settings = new() { LaunchOnSystemStartup = true } });
            var response = await PipeRoundTripAsync(await library.ExecuteAsync(request));
            Assert.True(response.Success);
            switch (operation)
            {
                case GameLibraryOperation.GetSettings: case GameLibraryOperation.UpdateSettings: Assert.True(response.Settings!.LaunchOnSystemStartup); break;
                case GameLibraryOperation.DdcSnapshot: Assert.Single(response.DdcSnapshot!.Values); break;
                default: Assert.Equal("owner/repo", response.SyncStatus!.Repo); break;
            }
        }
        settings.Verify(service => service.Update(It.Is<UpdateAppSettingsRequest>(r => r.LaunchOnSystemStartup)), Times.Once);
        sync.Verify(service => service.ValidateAsync(It.IsAny<CancellationToken>()), Times.Once);
        sync.Verify(service => service.SyncNowAsync(true, It.IsAny<CancellationToken>()), Times.Once);
        Provider.Change(config => config.MonitorMode = new() { Enabled = true });
        await Assert.ThrowsAsync<InvalidOperationException>(() => library.ExecuteAsync(new() { Operation = GameLibraryOperation.DdcSnapshot }));
        Provider.Change(config => config.MonitorMode!.Enabled = false);
        monitorMode.Setup(controller => controller.ReadSnapshot()).Returns((MonitorModeSnapshot?)null);
        var unavailable = await library.ExecuteAsync(new() { Operation = GameLibraryOperation.DdcSnapshot });
        Assert.False(unavailable.Success); Assert.NotEmpty(unavailable.Error!);
        sync.Setup(service => service.ValidateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(SyncOutcome.FailedWithError("denied"));
        var failed = await library.ExecuteAsync(new() { Operation = GameLibraryOperation.SyncValidate });
        Assert.False(failed.Success); Assert.Equal("denied", failed.Error);
    }
    private static async Task<T> PipeRoundTripAsync<T>(T value)
    {
        using var stream = new MemoryStream();
        await GameLibraryPipe.WriteAsync(stream, value, CancellationToken.None);
        stream.Position = 0;
        return await GameLibraryPipe.ReadAsync<T>(stream, CancellationToken.None);
    }

}
