using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.WinUI.ViewModels;

namespace GameHelper.Tests;

public sealed class WinUiViewModelTests
{
    [Fact]
    public void SettingsViewModel_Save_ShouldPersistUpdatedSettings()
    {
        var service = new FakeSettingsService();
        var viewModel = new SettingsViewModel(service)
        {
            SelectedMonitorType = ProcessMonitorType.WMI.ToString(),
            AutoStartInteractiveMonitor = true,
            LaunchOnSystemStartup = true
        };

        viewModel.SaveCommand.Execute(null);

        Assert.NotNull(service.LastUpdateRequest);
        Assert.Equal(ProcessMonitorType.WMI, service.LastUpdateRequest!.ProcessMonitorType);
        Assert.True(service.LastUpdateRequest.AutoStartInteractiveMonitor);
        Assert.True(service.LastUpdateRequest.LaunchOnSystemStartup);
    }

    [Fact]
    public void GamesViewModel_AddAndDelete_ShouldUpdateCollection()
    {
        var service = new FakeGameCatalogService();
        var viewModel = new GamesViewModel(service);

        var before = viewModel.Games.Count;
        viewModel.AddCommand.Execute(null);
        Assert.True(viewModel.Games.Count >= before + 1);

        viewModel.SelectedGame = viewModel.Games.First();
        viewModel.DeleteCommand.Execute(null);
        Assert.True(viewModel.Games.Count <= before);
    }

    [Fact]
    public void StatsViewModel_Refresh_ShouldLoadItems()
    {
        var service = new FakeStatisticsService
        {
            Overview = new List<GameStatsSummary>
            {
                new() { GameName = "game-a", DisplayName = "Game A", TotalMinutes = 120, RecentMinutes = 60, SessionCount = 2 }
            }
        };

        var viewModel = new StatsViewModel(service);
        viewModel.RefreshCommand.Execute(null);

        Assert.Single(viewModel.Stats);
        Assert.Equal("game-a", viewModel.Stats[0].GameName);
    }

    [Fact]
    public void ShellViewModel_ToggleMonitor_ShouldSwitchText()
    {
        var service = new FakeMonitorControlService();
        var viewModel = new ShellViewModel(service);

        Assert.Equal("Start Monitor", viewModel.MonitorButtonText);
        viewModel.ToggleMonitorCommand.Execute(null);
        Assert.Equal("Stop Monitor", viewModel.MonitorButtonText);
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        private AppSettingsSnapshot _current = new();

        public UpdateAppSettingsRequest? LastUpdateRequest { get; private set; }

        public AppSettingsSnapshot Get() => _current;

        public AppSettingsSnapshot Update(UpdateAppSettingsRequest request)
        {
            LastUpdateRequest = request;
            _current = new AppSettingsSnapshot
            {
                ProcessMonitorType = request.ProcessMonitorType ?? ProcessMonitorType.ETW,
                AutoStartInteractiveMonitor = request.AutoStartInteractiveMonitor,
                LaunchOnSystemStartup = request.LaunchOnSystemStartup
            };

            return _current;
        }
    }

    private sealed class FakeGameCatalogService : IGameCatalogService
    {
        private readonly Dictionary<string, GameEntry> _store = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<GameEntry> GetAll() => _store.Values.ToList();

        public GameEntry Add(GameEntryUpsertRequest request)
        {
            var key = request.ExecutableName ?? $"game-{_store.Count + 1}.exe";
            var entry = new GameEntry
            {
                DataKey = key,
                ExecutableName = key,
                DisplayName = request.DisplayName ?? key,
                IsEnabled = request.IsEnabled,
                HdrEnabled = request.HdrEnabled
            };

            _store[key] = entry;
            return entry;
        }

        public GameEntry Update(string dataKey, GameEntryUpsertRequest request)
        {
            if (!_store.TryGetValue(dataKey, out var entry))
            {
                throw new KeyNotFoundException();
            }

            entry.DisplayName = request.DisplayName ?? entry.DisplayName;
            entry.IsEnabled = request.IsEnabled;
            entry.HdrEnabled = request.HdrEnabled;
            return entry;
        }

        public bool Delete(string dataKey) => _store.Remove(dataKey);
    }

    private sealed class FakeStatisticsService : IStatisticsService
    {
        public IReadOnlyList<GameStatsSummary> Overview { get; set; } = Array.Empty<GameStatsSummary>();

        public IReadOnlyList<GameStatsSummary> GetOverview() => Overview;

        public GameStatsSummary? GetDetails(string dataKeyOrGameName)
        {
            return Overview.FirstOrDefault(item =>
                string.Equals(item.GameName, dataKeyOrGameName, StringComparison.OrdinalIgnoreCase));
        }
    }

    private sealed class FakeMonitorControlService : IMonitorControlService
    {
        public bool IsRunning { get; private set; }

        public void Start() => IsRunning = true;

        public void Stop() => IsRunning = false;
    }
}
