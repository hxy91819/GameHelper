using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;

namespace GameHelper.Tests;

public sealed class CoreApplicationServicesTests
{
    [Fact]
    public void SettingsService_Update_ShouldPersistSnapshot()
    {
        var provider = new FakeGameConfiguration();
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
    public void GameCatalogService_IntakeUpdateRemove_CommitOncePerOperation()
    {
        var configuration = new FakeGameConfiguration();
        var service = new GameCatalogService(configuration);

        var created = service.Intake(new GameCatalogIntakeRequest
        {
            Executable = ExecutableIdentity.Parse("test.exe"),
            DisplayName = "Test"
        });
        var updated = service.Update(created.Entry.DataKey, new GameCatalogUpdateRequest
        {
            DisplayName = "Test Updated",
            IsEnabled = false,
            HdrEnabled = true
        });
        var removed = service.Remove(created.Entry.DataKey);

        Assert.Equal("test", created.Entry.DataKey);
        Assert.Equal("Test Updated", updated.DisplayName);
        Assert.False(updated.IsEnabled);
        Assert.True(updated.HdrEnabled);
        Assert.True(removed);
        Assert.Equal(3, configuration.ChangeCount);
    }

    [Fact]
    public void GameCatalogService_Update_ReplacesExecutableIdentityAndClearsDisplayName()
    {
        var configuration = new FakeGameConfiguration();
        var service = new GameCatalogService(configuration);
        var created = service.Intake(new GameCatalogIntakeRequest
        {
            Executable = ExecutableIdentity.Parse(@"C:\Games\clear.exe"),
            DisplayName = "Clear Me"
        });

        var updated = service.Update(created.Entry.DataKey, new GameCatalogUpdateRequest
        {
            Executable = ExecutableIdentity.Parse("clear.exe"),
            ClearDisplayName = true
        });

        Assert.Equal("clear.exe", updated.ExecutableName);
        Assert.Null(updated.ExecutablePath);
        Assert.Null(updated.DisplayName);
    }

    [Fact]
    public void GameCatalogService_PreviewIntake_ConcentratesDuplicateAndDataKeyPolicy()
    {
        var configuration = new FakeGameConfiguration(new AppConfig
        {
            Games =
            [
                new GameConfig
                {
                    DataKey = "same",
                    Executable = @"C:\Games\same.exe",
                    DisplayName = "Same"
                }
            ]
        });
        var service = new GameCatalogService(configuration);

        var preview = service.PreviewIntake(new GameCatalogIntakeRequest
        {
            Executable = ExecutableIdentity.Parse(@"C:\Games\same.exe"),
            DataKey = "same"
        });

        Assert.Equal("same", preview.ExistingEntry?.DataKey);
        Assert.Equal("same", preview.SuggestedDataKey);
        Assert.True(preview.IsRequestedDataKeyAvailable);
        Assert.Equal(0, configuration.ChangeCount);
    }

    [Fact]
    public void GameCatalogService_Intake_UpdatesExistingAndPreservesHdrChoice()
    {
        var configuration = new FakeGameConfiguration(new AppConfig
        {
            Games =
            [
                new GameConfig
                {
                    DataKey = "legacy",
                    Executable = @"C:\Games\Legacy.exe",
                    DisplayName = "Old Name",
                    IsEnabled = false,
                    HdrEnabled = true
                }
            ]
        });
        var service = new GameCatalogService(configuration);

        var result = service.Intake(new GameCatalogIntakeRequest
        {
            Executable = ExecutableIdentity.Parse(@"C:\Games\Legacy.exe"),
            DataKey = "new-key",
            DisplayName = "New Name"
        });

        Assert.False(result.WasAdded);
        Assert.Equal(@"C:\Games\Legacy.exe", result.PreviousExecutablePath);
        Assert.Equal("new-key", result.Entry.DataKey);
        Assert.True(result.Entry.HdrEnabled);
        Assert.Single(configuration.Read().Games!);
    }

    [Fact]
    public void GameCatalogService_BatchIntake_UsesOneAtomicChange()
    {
        var configuration = new FakeGameConfiguration(new AppConfig
        {
            ProcessMonitorType = ProcessMonitorType.WMI,
            AutoStartInteractiveMonitor = true
        });
        var service = new GameCatalogService(configuration);

        var results = service.BatchIntake(
        [
            new GameCatalogIntakeRequest { Executable = ExecutableIdentity.Parse("a.exe") },
            new GameCatalogIntakeRequest { Executable = ExecutableIdentity.Parse("b.exe") }
        ]);

        Assert.Equal(2, results.Count);
        Assert.Equal(1, configuration.ChangeCount);
        Assert.Equal(ProcessMonitorType.WMI, configuration.Read().ProcessMonitorType);
        Assert.True(configuration.Read().AutoStartInteractiveMonitor);
    }

    [Fact]
    public void StatisticsService_ShouldAggregateSessions()
    {
        var provider = new FakeGameConfiguration(new AppConfig
        {
            Games = [new()
            {
                DataKey = "game.exe",
                Executable = "game.exe",
                DisplayName = "Game Display"
            }]
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
        var provider = new FakeGameConfiguration(new AppConfig
        {
            Games = [new()
            {
                DataKey = "wh40krt",
                Executable = "wh40krt.exe",
                DisplayName = "Warhammer 40,000: Rogue Trader"
            }]
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
        var provider = new FakeGameConfiguration(new AppConfig
        {
            Games = [new()
            {
                DataKey = "game-key",
                Executable = "game.exe",
                DisplayName = "Game Display"
            }]
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
    public void StatisticsService_GetDetails_ShouldResolveDisplayName()
    {
        var provider = new FakeGameConfiguration(new AppConfig
        {
            Games = [new()
            {
                DataKey = "game-key",
                Executable = "game.exe",
                DisplayName = "Game Display"
            }]
        });

        var snapshot = new FakePlaytimeSnapshotProvider
        {
            Records = new List<GamePlaytimeRecord>
            {
                new()
                {
                    GameName = "game-key",
                    Sessions =
                    {
                        new PlaySession("game-key", DateTime.Now.AddMinutes(-30), DateTime.Now, TimeSpan.FromMinutes(30), 30)
                    }
                }
            }
        };

        var service = new StatisticsService(snapshot, provider);
        var details = service.GetDetails("game-key");

        Assert.NotNull(details);
        Assert.Equal("Game Display", details.DisplayName);
    }

    [Fact]
    public void StatisticsService_WhenRecentMinutesTie_ShouldSortByTotalMinutesDescending()
    {
        var provider = new FakeGameConfiguration(new AppConfig
        {
            Games =
            [
                new() { DataKey = "a", Executable = "a.exe", DisplayName = "A" },
                new() { DataKey = "b", Executable = "b.exe", DisplayName = "B" }
            ]
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

    private sealed class FakeGameConfiguration : IGameConfiguration
    {
        private AppConfig _config;

        public FakeGameConfiguration(AppConfig? config = null)
        {
            _config = Clone(config ?? new AppConfig());
        }

        public int ChangeCount { get; private set; }

        public AppConfig Read() => Clone(_config);

        public AppConfig Change(Action<AppConfig> change)
        {
            var working = Clone(_config);
            change(working);
            _config = Clone(working);
            ChangeCount++;
            return Clone(_config);
        }

        private static AppConfig Clone(AppConfig source)
        {
            return new AppConfig
            {
                ProcessMonitorType = source.ProcessMonitorType,
                AutoStartInteractiveMonitor = source.AutoStartInteractiveMonitor,
                LaunchOnSystemStartup = source.LaunchOnSystemStartup,
                Games = source.Games?.Select(config => new GameConfig
                {
                    DataKey = config.DataKey,
                    Executable = config.Executable,
                    DisplayName = config.DisplayName,
                    IsEnabled = config.IsEnabled,
                    HdrEnabled = config.HdrEnabled
                }).ToList()
            };
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

        public void Configure(ProcessObservationPolicy policy)
        {
        }

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
