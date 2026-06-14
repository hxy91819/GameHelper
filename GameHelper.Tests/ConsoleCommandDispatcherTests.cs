using GameHelper.ConsoleHost;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
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
                new GameEntry { DataKey = "sample", ExecutableName = "sample.exe", IsEnabled = true, HdrEnabled = true }
            }));
        });

        var output = await CaptureConsoleAsync(() => ConsoleCommandDispatcher.DispatchAsync(host, new ParsedArguments
        {
            EffectiveArgs = new[] { "config", "list" }
        }));

        Assert.Contains("sample  Enabled=True  HDR=True", output);
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

        public IReadOnlyList<GameEntry> GetAll() => _games;

        public GameEntry Add(GameEntryUpsertRequest request) => throw new NotSupportedException();

        public GameEntry Save(GameEntryUpsertRequest request) => throw new NotSupportedException();

        public GameEntryImportResult Import(GameEntryImportRequest request) => throw new NotSupportedException();

        public void RepairStorage() => throw new NotSupportedException();

        public GameEntry Update(string dataKey, GameEntryUpsertRequest request) => throw new NotSupportedException();

        public bool Delete(string dataKey) => throw new NotSupportedException();
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
    }
}
