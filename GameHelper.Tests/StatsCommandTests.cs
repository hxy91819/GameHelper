using GameHelper.ConsoleHost.Commands;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace GameHelper.Tests;

public sealed class StatsCommandTests
{
    [Fact]
    public void Run_WhenStatisticsServiceThrows_ShouldPrintFriendlyError()
    {
        var services = new ServiceCollection()
            .AddSingleton<IStatisticsService, ThrowingStatisticsService>()
            .BuildServiceProvider();

        var writer = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(writer);
            StatsCommand.Run(services, Array.Empty<string>());
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
        var services = new ServiceCollection()
            .AddSingleton<IStatisticsService>(new EmptyStatisticsService())
            .BuildServiceProvider();

        var writer = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(writer);
            StatsCommand.Run(services, Array.Empty<string>());
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
    }

    private sealed class EmptyStatisticsService : IStatisticsService
    {
        public IReadOnlyList<GameStatsSummary> GetOverview() => Array.Empty<GameStatsSummary>();

        public GameStatsSummary? GetDetails(string dataKeyOrGameName) => null;
    }
}
