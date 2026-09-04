using GameHelper.ConsoleHost;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GameHelper.Tests;

public sealed class ConsoleCommandDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_ConfigList_PrintsConfiguredGames()
    {
        using var host = CreateHost(services =>
        {
            services.AddSingleton<IGameCatalogService>(new FakeGameCatalogService(new[]
            {
                new GameEntry { DataKey = "sample", Executable = ExecutableIdentity.Parse("sample.exe"), DisplayName = "Sample Game", IsEnabled = true, HdrEnabled = true }
            }));
        });

        var output = await CaptureConsoleAsync(() => ConsoleCommandDispatcher.DispatchAsync(host, new ParsedArguments
        {
            EffectiveArgs = new[] { "config", "list" }
        }));

        Assert.Contains("sample  DisplayName=Sample Game  Enabled=True  HDR=True", output);
    }

    [Fact]
    public async Task DispatchAsync_Stats_PrintsStatisticsOverview()
    {
        using var host = CreateHost(services =>
        {
            services.AddSingleton<IStatisticsService>(new FakeStatisticsService(new[]
            {
                new GameStatsSummary
                {
                    GameName = "sample",
                    DisplayName = "Sample Game",
                    TotalMinutes = 125,
                    RecentMinutes = 65,
                    SessionCount = 2
                }
            }));
        });

        var output = await CaptureConsoleAsync(() => ConsoleCommandDispatcher.DispatchAsync(host, new ParsedArguments
        {
            EffectiveArgs = new[] { "stats" }
        }));

        Assert.Contains("Sample Game", output);
        Assert.Contains("2.1 h", output);
        Assert.Contains("1.1 h", output);
        Assert.Contains("TOTAL", output);
    }

    [Fact]
    public async Task DispatchAsync_UnknownCommand_PrintsUsage()
    {
        using var host = CreateHost(_ => { });

        var output = await CaptureConsoleAsync(() => ConsoleCommandDispatcher.DispatchAsync(host, new ParsedArguments
        {
            EffectiveArgs = new[] { "unknown-command" }
        }));

        Assert.Contains("GameHelper Console", output);
        Assert.Contains("Usage:", output);
    }

    [Fact]
    public async Task DispatchAsync_ValidateConfig_UsesConfigOverride()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "GameHelperTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.yml");
        try
        {
            await File.WriteAllTextAsync(configPath, """
monitor: ETW
startup:
  autoStartMonitor: false
  launchOnStartup: false
games:
  - dataKey: smoke_granblue
    executable: smoke.exe
    displayName: "Granblue Fantasy: Relink"
    enabled: true
    hdr: false
""");

            using var host = CreateHost(_ => { });

            var output = await CaptureConsoleAsync(() => ConsoleCommandDispatcher.DispatchAsync(host, new ParsedArguments
            {
                EffectiveArgs = new[] { "validate-config" },
                ConfigOverride = configPath
            }));

            Assert.Contains($"Validating: {configPath}", output);
            Assert.Contains("Config is valid.", output);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static IHost CreateHost(Action<IServiceCollection> configureServices)
    {
        return Host.CreateDefaultBuilder(Array.Empty<string>())
            .ConfigureServices(configureServices)
            .Build();
    }

    private static async Task<string> CaptureConsoleAsync(Func<Task> action)
    {
        var originalOut = Console.Out;
        await using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            await action().ConfigureAwait(false);
            return writer.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private sealed class FakeGameCatalogService : IGameCatalogService
    {
        private readonly IReadOnlyList<GameEntry> _games;

        public FakeGameCatalogService(IReadOnlyList<GameEntry> games)
        {
            _games = games;
        }

        public IReadOnlyList<GameEntry> List() => _games;

        public GameCatalogIntakePreview PreviewIntake(GameCatalogIntakeRequest request) => new()
        {
            Executable = request.Executable,
            SuggestedDataKey = request.DataKey ?? request.Executable.Name,
            IsRequestedDataKeyAvailable = true
        };

        public GameCatalogIntakeResult Intake(GameCatalogIntakeRequest request) => throw new NotSupportedException();

        public IReadOnlyList<GameCatalogIntakeResult> BatchIntake(IEnumerable<GameCatalogIntakeRequest> requests) =>
            throw new NotSupportedException();

        public GameEntry Update(string dataKey, GameCatalogUpdateRequest request) => throw new NotSupportedException();

        public bool Remove(string dataKey) => throw new NotSupportedException();
    }

    private sealed class FakeStatisticsService : IStatisticsService
    {
        private readonly IReadOnlyList<GameStatsSummary> _overview;

        public FakeStatisticsService(IReadOnlyList<GameStatsSummary> overview)
        {
            _overview = overview;
        }

        public IReadOnlyList<GameStatsSummary> GetOverview() => _overview;

        public GameStatsSummary? GetDetails(string dataKeyOrGameName) =>
            _overview.FirstOrDefault(item =>
                string.Equals(item.GameName, dataKeyOrGameName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.DisplayName, dataKeyOrGameName, StringComparison.OrdinalIgnoreCase));

        public SessionActivitySnapshot GetSessionActivitySnapshot() => new(
            new HashSet<SessionActivityKey>(),
            Array.Empty<SessionActivityRecord>(),
            string.Empty);

        public SessionActivityPreview GetSessionActivityPreview() => new(
            Array.Empty<SessionGameSummary>(),
            Array.Empty<DailyPlaytimeSummary>(),
            0,
            StatisticsService.PreviewWindowDays,
            string.Empty);
    }
}
