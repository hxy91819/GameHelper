using GameHelper.ConsoleHost.Commands;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;

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

        var output = CaptureOutput(() => StatsCommand.Run(statisticsService, Array.Empty<string>()));

        Assert.Contains("No playtime data yet.", output);
    }

    [Fact]
    public void Run_WithGameOptionIgnoringCase_ShouldPrintMatchingDetails()
    {
        var statisticsService = new RecordingStatisticsService(new GameStatsSummary
        {
            GameName = "sample",
            DisplayName = "Sample Game",
            TotalMinutes = 90,
            RecentMinutes = 30,
            SessionCount = 3
        });

        var output = CaptureOutput(() => StatsCommand.Run(statisticsService, new[] { "--GAME", "sample" }));

        Assert.Equal("sample", statisticsService.LastDetailsArgument);
        Assert.Equal(0, statisticsService.OverviewCalls);
        Assert.Contains("Sample Game", output);
        Assert.DoesNotContain("TOTAL", output);
    }

    [Fact]
    public void Run_WithMissingGameOptionValue_ShouldPrintErrorWithoutReadingStats()
    {
        var statisticsService = new RecordingStatisticsService(null);

        var output = CaptureOutput(() => StatsCommand.Run(statisticsService, new[] { "--game" }));

        Assert.Contains("Missing value after --game.", output);
        Assert.Equal(0, statisticsService.OverviewCalls);
        Assert.Null(statisticsService.LastDetailsArgument);
    }

    private static string CaptureOutput(Action action)
    {
        var writer = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(writer);
            action();
            return writer.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
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

        public SessionActivityPreview GetSessionActivityPreview()
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

        public SessionActivityPreview GetSessionActivityPreview() => new(
            Array.Empty<SessionGameSummary>(),
            Array.Empty<DailyPlaytimeSummary>(),
            0,
            StatisticsService.PreviewWindowDays,
            string.Empty);
    }

    private sealed class RecordingStatisticsService : IStatisticsService
    {
        private readonly GameStatsSummary? _details;

        public RecordingStatisticsService(GameStatsSummary? details)
        {
            _details = details;
        }

        public int OverviewCalls { get; private set; }

        public string? LastDetailsArgument { get; private set; }

        public IReadOnlyList<GameStatsSummary> GetOverview()
        {
            OverviewCalls++;
            return Array.Empty<GameStatsSummary>();
        }

        public GameStatsSummary? GetDetails(string dataKeyOrGameName)
        {
            LastDetailsArgument = dataKeyOrGameName;
            return _details;
        }

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
