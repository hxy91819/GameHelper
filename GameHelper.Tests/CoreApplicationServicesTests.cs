using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;

namespace GameHelper.Tests;

public sealed class CoreApplicationServicesTests
{
    [Fact]
    public void SettingsService_Update_ShouldPersistSnapshot()
    {
        var provider = new FakeConfigProvider();
        var service = new SettingsService(provider);

        var updated = service.Update(new UpdateAppSettingsRequest
        {
            ProcessMonitorType = ProcessMonitorType.WMI,
            AutoStartInteractiveMonitor = true,
            LaunchOnSystemStartup = true
        });

        Assert.Equal(ProcessMonitorType.WMI, updated.ProcessMonitorType);
        Assert.True(updated.AutoStartInteractiveMonitor);
        Assert.True(updated.LaunchOnSystemStartup);
    }

    [Fact]
    public void GameCatalogService_AddUpdateDelete_ShouldWork()
    {
        var provider = new FakeConfigProvider();
        var service = new GameCatalogService(provider);

        var created = service.Add(new GameEntryUpsertRequest
        {
            ExecutableName = "test.exe",
            DisplayName = "Test",
            IsEnabled = true
        });

        Assert.Equal("test.exe", created.DataKey);

        var updated = service.Update("test.exe", new GameEntryUpsertRequest
        {
            ExecutableName = "test.exe",
            DisplayName = "Test Updated",
            IsEnabled = false,
            HdrEnabled = true
        });

        Assert.Equal("Test Updated", updated.DisplayName);
        Assert.False(updated.IsEnabled);
        Assert.True(updated.HdrEnabled);

        Assert.True(service.Delete("test.exe"));
    }

    [Fact]
    public void StatisticsService_ShouldAggregateSessions()
    {
        var provider = new FakeConfigProvider();
        provider.Save(new Dictionary<string, GameConfig>
        {
            ["game.exe"] = new()
            {
                DataKey = "game.exe",
                ExecutableName = "game.exe",
                DisplayName = "Game Display"
            }
        });

        var now = DateTime.Now;
        var snapshot = new FakePlaytimeSnapshotProvider
        {
            Records = new List<GamePlaytimeRecord>
            {
                new()
                {
                    GameName = "game.exe",
                    Sessions =
                    {
                        new PlaySession("game.exe", now.AddHours(-3), now.AddHours(-2), TimeSpan.FromHours(1), 60),
                        new PlaySession("game.exe", now.AddDays(-20), now.AddDays(-20).AddMinutes(30), TimeSpan.FromMinutes(30), 30)
                    }
                }
            }
        };

        var service = new StatisticsService(snapshot, provider);
        var overview = service.GetOverview();

        Assert.Single(overview);
        Assert.Equal("Game Display", overview[0].DisplayName);
        Assert.Equal(90, overview[0].TotalMinutes);
        Assert.Equal(60, overview[0].RecentMinutes);
    }

    [Fact]
    public void StatisticsService_ShouldPreferDisplayName_WhenRecordUsesDataKey()
    {
        var provider = new FakeConfigProvider();
        provider.Save(new Dictionary<string, GameConfig>
        {
            // Simulate provider keyed by executable name while data is tracked by DataKey.
            ["wh40krt.exe"] = new()
            {
                DataKey = "wh40krt",
                ExecutableName = "wh40krt.exe",
                DisplayName = "Warhammer 40,000: Rogue Trader"
            }
        });

        var now = DateTime.Now;
        var snapshot = new FakePlaytimeSnapshotProvider
        {
            Records = new List<GamePlaytimeRecord>
            {
                new()
                {
                    GameName = "wh40krt",
                    Sessions =
                    {
                        new PlaySession("wh40krt", now.AddHours(-1), now, TimeSpan.FromHours(1), 60)
                    }
                }
            }
        };

        var service = new StatisticsService(snapshot, provider);
        var overview = service.GetOverview();

        Assert.Single(overview);
        Assert.Equal("wh40krt", overview[0].GameName);
        Assert.Equal("Warhammer 40,000: Rogue Trader", overview[0].DisplayName);
    }

    [Fact]
    public void StatisticsService_WhenRecentMinutesTie_ShouldSortByTotalMinutesDescending()
    {
        var provider = new FakeConfigProvider();
        provider.Save(new Dictionary<string, GameConfig>
        {
            ["a.exe"] = new() { DataKey = "a", ExecutableName = "a.exe", DisplayName = "A" },
            ["b.exe"] = new() { DataKey = "b", ExecutableName = "b.exe", DisplayName = "B" }
        });

        var now = DateTime.Now;
        var snapshot = new FakePlaytimeSnapshotProvider
        {
            Records = new List<GamePlaytimeRecord>
            {
                new()
                {
                    GameName = "a",
                    Sessions =
                    {
                        // recent = 60, total = 120
                        new PlaySession("a", now.AddHours(-1), now, TimeSpan.FromMinutes(60), 60),
                        new PlaySession("a", now.AddDays(-30), now.AddDays(-30).AddMinutes(60), TimeSpan.FromMinutes(60), 60)
                    }
                },
                new()
                {
                    GameName = "b",
                    Sessions =
                    {
                        // recent = 60, total = 90
                        new PlaySession("b", now.AddHours(-2), now.AddHours(-1), TimeSpan.FromMinutes(60), 60),
                        new PlaySession("b", now.AddDays(-30), now.AddDays(-30).AddMinutes(30), TimeSpan.FromMinutes(30), 30)
                    }
                }
            }
        };

        var service = new StatisticsService(snapshot, provider);
        var overview = service.GetOverview();

        Assert.Equal(2, overview.Count);
        Assert.Equal("a", overview[0].GameName);
        Assert.Equal("b", overview[1].GameName);
    }

    [Fact]
    public void MonitorControlService_ShouldStartAndStop()
    {
        var processMonitor = new FakeProcessMonitor();
        var automationService = new FakeGameAutomationService();
        var service = new MonitorControlService(processMonitor, automationService);

        service.Start();
        Assert.True(service.IsRunning);
        Assert.True(processMonitor.StartCalled);
        Assert.True(automationService.StartCalled);

        service.Stop();
        Assert.False(service.IsRunning);
        Assert.True(processMonitor.StopCalled);
        Assert.True(automationService.StopCalled);
    }

    private sealed class FakeConfigProvider : IConfigProvider, IAppConfigProvider
    {
        private Dictionary<string, GameConfig> _configs = new(StringComparer.OrdinalIgnoreCase);
        private AppConfig _appConfig = new();

        public IReadOnlyDictionary<string, GameConfig> Load() => _configs;

        public void Save(IReadOnlyDictionary<string, GameConfig> configs)
        {
            _configs = new Dictionary<string, GameConfig>(configs, StringComparer.OrdinalIgnoreCase);
        }

        public AppConfig LoadAppConfig() => _appConfig;

        public void SaveAppConfig(AppConfig appConfig)
        {
            _appConfig = appConfig;
        }
    }

    private sealed class FakePlaytimeSnapshotProvider : IPlaytimeSnapshotProvider
    {
        public IReadOnlyList<GamePlaytimeRecord> Records { get; set; } = Array.Empty<GamePlaytimeRecord>();

        public IReadOnlyList<GamePlaytimeRecord> GetPlaytimeRecords() => Records;
    }

    private sealed class FakeProcessMonitor : IProcessMonitor
    {
        public event Action<ProcessEventInfo>? ProcessStarted;
        public event Action<ProcessEventInfo>? ProcessStopped;

        public bool StartCalled { get; private set; }
        public bool StopCalled { get; private set; }

        public void Dispose()
        {
        }

        public void Start()
        {
            StartCalled = true;
        }

        public void Stop()
        {
            StopCalled = true;
        }
    }

    private sealed class FakeGameAutomationService : IGameAutomationService
    {
        public bool StartCalled { get; private set; }
        public bool StopCalled { get; private set; }

        public void Start()
        {
            StartCalled = true;
        }

        public void ReloadConfig()
        {
        }

        public void Stop()
        {
            StopCalled = true;
        }
    }
}
