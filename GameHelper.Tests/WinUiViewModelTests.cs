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

        public IReadOnlyList<GameEntry> List() => _store.Values.ToList();

        public GameCatalogIntakePreview PreviewIntake(GameCatalogIntakeRequest request) => new()
        {
            Executable = request.Executable,
            SuggestedDataKey = request.DataKey ?? request.Executable.Name,
            IsRequestedDataKeyAvailable = true
        };

        public GameCatalogIntakeResult Intake(GameCatalogIntakeRequest request)
        {
            var key = request.DataKey ?? request.Executable.Name;
            var entry = new GameEntry
            {
                DataKey = key,
                Executable = request.Executable,
                DisplayName = request.DisplayName ?? key,
                IsEnabled = request.IsEnabled,
                HdrEnabled = request.HdrEnabled ?? false
            };

            _store[key] = entry;
            return new GameCatalogIntakeResult { Entry = entry, WasAdded = true };
        }

        public IReadOnlyList<GameCatalogIntakeResult> BatchIntake(IEnumerable<GameCatalogIntakeRequest> requests) =>
            requests.Select(Intake).ToList();

        public GameEntry Update(string dataKey, GameCatalogUpdateRequest request)
        {
            if (!_store.TryGetValue(dataKey, out var entry))
            {
                throw new KeyNotFoundException();
            }

            var updated = entry with
            {
                Executable = request.Executable ?? entry.Executable,
                DisplayName = request.ClearDisplayName ? null : request.DisplayName ?? entry.DisplayName,
                IsEnabled = request.IsEnabled ?? entry.IsEnabled,
                HdrEnabled = request.HdrEnabled ?? entry.HdrEnabled
            };
            _store[dataKey] = updated;
            return updated;
        }

        public bool Remove(string dataKey) => _store.Remove(dataKey);
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

        public SessionActivitySnapshot GetSessionActivitySnapshot() => new(
            new HashSet<SessionActivityKey>(),
            Array.Empty<SessionActivityRecord>(),
            string.Empty);
    }

    private sealed class FakeMonitorControlService : IMonitorControlService
    {
        public bool IsRunning { get; private set; }

        public void Start() => IsRunning = true;

        public void Stop() => IsRunning = false;
    }
}
