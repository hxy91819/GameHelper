using GameHelper.ConsoleHost.Commands;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Tests;

public sealed class StatsCommandTests
{
    [Fact]
    public void Run_WhenStatisticsServiceThrows_ShouldPrintFriendlyError()
    {
        var statisticsService = new ThrowingStatisticsService();

        var writer = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(writer);
            StatsCommand.Run(statisticsService, Array.Empty<string>());
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = writer.ToString();
        Assert.Contains("Failed to read stats:", output);
    }

    [Fact]
    public void Run_WhenNoStats_ShouldPrintNoDataMessage()
    {
        var statisticsService = new EmptyStatisticsService();

        var writer = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(writer);
            StatsCommand.Run(statisticsService, Array.Empty<string>());
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Contains("No playtime data yet.", writer.ToString());
    }

    private sealed class ThrowingStatisticsService : IStatisticsService
    {
        public IReadOnlyList<GameStatsSummary> GetOverview()
        {
            throw new InvalidOperationException("broken file");
        }

        public GameStatsSummary? GetDetails(string dataKeyOrGameName)
        {
            throw new InvalidOperationException("broken file");
        }

        public SessionActivitySnapshot GetSessionActivitySnapshot()
        {
            throw new InvalidOperationException("broken file");
        }
    }

    private sealed class EmptyStatisticsService : IStatisticsService
    {
        public IReadOnlyList<GameStatsSummary> GetOverview() => Array.Empty<GameStatsSummary>();

        public GameStatsSummary? GetDetails(string dataKeyOrGameName) => null;

        public SessionActivitySnapshot GetSessionActivitySnapshot() => new(
            new HashSet<SessionActivityKey>(),
            Array.Empty<SessionActivityRecord>(),
            string.Empty);
    }
}
