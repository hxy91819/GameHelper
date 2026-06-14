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
    public void GameCatalogService_Update_ShouldClearOptionalFields()
    {
        var provider = new FakeConfigProvider();
        var service = new GameCatalogService(provider);

        _ = service.Add(new GameEntryUpsertRequest
        {
            ExecutableName = "clear.exe",
            ExecutablePath = @"C:\Games\clear.exe",
            DisplayName = "Clear Me",
            IsEnabled = true
        });

        var updated = service.Update("clear.exe", new GameEntryUpsertRequest
        {
            ExecutableName = "clear.exe",
            ClearExecutablePath = true,
            ClearDisplayName = true,
            IsEnabled = true
        });

        Assert.Null(updated.ExecutablePath);
        Assert.Null(updated.DisplayName);
    }

    [Fact]
    public void GameCatalogService_Save_ShouldCreateEntryWithRequestedDataKey()
    {
        var provider = new FakeConfigProvider();
        var service = new GameCatalogService(provider);

        var saved = service.Save(new GameEntryUpsertRequest
        {
            DataKey = "custom-key",
            ExecutableName = "custom.exe",
            DisplayName = "Custom",
            IsEnabled = true,
            HdrEnabled = true
        });

        Assert.Equal("custom-key", saved.DataKey);
        Assert.Equal("custom.exe", saved.ExecutableName);
        Assert.Equal("Custom", saved.DisplayName);
        Assert.True(saved.HdrEnabled);
    }

    [Fact]
    public void GameCatalogService_Save_ShouldUpdateExistingMatchingEntry()
    {
        var provider = new FakeConfigProvider();
        var service = new GameCatalogService(provider);
        provider.Save(new Dictionary<string, GameConfig>
        {
            ["entry1"] = new()
            {
                EntryId = "entry1",
                DataKey = "old-key",
                ExecutableName = "same.exe",
                DisplayName = "Old",
                IsEnabled = false,
                HDREnabled = false
            }
        });

        var saved = service.Save(new GameEntryUpsertRequest
        {
            DataKey = "new-key",
            ExecutableName = "same.exe",
            DisplayName = "New",
            IsEnabled = true,
            HdrEnabled = true
        });

        Assert.Equal("new-key", saved.DataKey);
        Assert.Equal("New", saved.DisplayName);
        Assert.True(saved.IsEnabled);
        Assert.True(saved.HdrEnabled);
        Assert.Single(provider.Load());
    }

    [Fact]
    public void GameCatalogService_Save_ShouldRepairMissingEntryIdWhenUpdating()
    {
        var provider = new FakeConfigProvider();
        var service = new GameCatalogService(provider);
        provider.Save(new Dictionary<string, GameConfig>
        {
            ["legacy-key"] = new()
            {
                DataKey = "legacy",
                ExecutableName = "legacy.exe",
                DisplayName = "Legacy",
                IsEnabled = false
            }
        });

        var saved = service.Save(new GameEntryUpsertRequest
        {
            DataKey = "legacy",
            ExecutableName = "legacy.exe",
            DisplayName = "Updated",
            IsEnabled = true
        });

        var config = Assert.Single(provider.Load().Values);
        Assert.False(string.IsNullOrWhiteSpace(config.EntryId));
        Assert.Equal(saved.DataKey, config.DataKey);
        Assert.Equal("Updated", config.DisplayName);
    }

    [Fact]
    public void GameCatalogService_QueryHelpers_ShouldUseCatalogStoragePolicy()
    {
        var provider = new FakeConfigProvider();
        var service = new GameCatalogService(provider);
        provider.Save(new Dictionary<string, GameConfig>
        {
            ["entry1"] = new()
            {
                EntryId = "entry1",
                DataKey = "same",
                ExecutableName = "same.exe",
                ExecutablePath = @"C:\Games\same.exe",
                DisplayName = "Same"
            }
        });

        var match = service.FindExistingForAdd("same.exe", @"C:\Games\same.exe");
        var suggested = service.SuggestDataKey(@"C:\Games\same.exe");

        Assert.NotNull(match);
        Assert.Equal("same", match.DataKey);
        Assert.Equal("same2", suggested);
        Assert.False(service.IsDataKeyAvailable("same"));
        Assert.True(service.IsDataKeyAvailable("same", currentDataKey: "same"));
    }

    [Fact]
    public void GameCatalogService_Import_ShouldAddUsingBaseDataKey()
    {
        var provider = new FakeConfigProvider();
        var service = new GameCatalogService(provider);

        var result = service.Import(new GameEntryImportRequest
        {
            ExecutableName = "Launcher.exe",
            ExecutablePath = @"C:\Games\Launcher.exe",
            DisplayName = "Friendly Game",
            BaseDataKey = "friendly-game",
            IsEnabled = true
        });

        Assert.True(result.WasAdded);
        Assert.Equal("friendly-game", result.Entry.DataKey);
        Assert.Equal("Launcher.exe", result.Entry.ExecutableName);
        Assert.Equal(@"C:\Games\Launcher.exe", result.Entry.ExecutablePath);
        Assert.Equal("Friendly Game", result.Entry.DisplayName);
        Assert.True(result.Entry.IsEnabled);
        Assert.False(result.Entry.HdrEnabled);
    }

    [Fact]
    public void GameCatalogService_Import_ShouldUpdateExistingAndPreserveHdrChoice()
    {
        var provider = new FakeConfigProvider();
        var service = new GameCatalogService(provider);
        provider.Save(new Dictionary<string, GameConfig>
        {
            ["legacy-entry"] = new()
            {
                EntryId = "legacy-entry",
                DataKey = "legacy",
                ExecutableName = "Legacy.exe",
                ExecutablePath = @"C:\Games\Legacy.exe",
                DisplayName = "Old Name",
                IsEnabled = false,
                HDREnabled = true
            }
        });

        var result = service.Import(new GameEntryImportRequest
        {
            ExecutableName = "Legacy.exe",
            ExecutablePath = @"C:\Games\Legacy.exe",
            DisplayName = "New Name",
            BaseDataKey = "new-key",
            IsEnabled = true
        });

        Assert.False(result.WasAdded);
        Assert.Equal(@"C:\Games\Legacy.exe", result.PreviousExecutablePath);
        Assert.Equal("legacy", result.Entry.DataKey);
        Assert.Equal("New Name", result.Entry.DisplayName);
        Assert.True(result.Entry.IsEnabled);
        Assert.True(result.Entry.HdrEnabled);
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
    public void StatisticsService_GetSessionActivitySnapshot_ShouldResolveDisplayNames()
    {
        var provider = new FakeConfigProvider();
        provider.Save(new Dictionary<string, GameConfig>
        {
            ["game.exe"] = new()
            {
                DataKey = "game-key",
                ExecutableName = "game.exe",
                DisplayName = "Game Display"
            }
        });

        var started = new DateTime(2024, 1, 1, 20, 0, 0, DateTimeKind.Unspecified);
        var snapshot = new FakePlaytimeSnapshotProvider
        {
            Snapshot = new PlaytimeSnapshot(
                new List<GamePlaytimeRecord>
                {
                    new()
                    {
                        GameName = "game-key",
                        Sessions =
                        {
                            new PlaySession("game-key", started, started.AddMinutes(30), TimeSpan.FromMinutes(30), 30)
                        }
                    }
                },
                @"C:\GameHelper\playtime.csv")
        };

        var service = new StatisticsService(snapshot, provider);
        var activity = service.GetSessionActivitySnapshot();

        var record = Assert.Single(activity.Records);
        Assert.Equal("Game Display", record.DisplayName);
        Assert.Equal(@"C:\GameHelper\playtime.csv", activity.Source);
        Assert.Contains(record.Key, activity.Keys);
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

        public PlaytimeSnapshot? Snapshot { get; set; }

        public IReadOnlyList<GamePlaytimeRecord> GetPlaytimeRecords() => Records;

        public PlaytimeSnapshot GetSnapshot() => Snapshot ?? new PlaytimeSnapshot(Records, null);
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
