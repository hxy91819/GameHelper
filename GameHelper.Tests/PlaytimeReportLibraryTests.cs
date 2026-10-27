using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using Moq;
using GameHelper.Infrastructure.Ipc;

namespace GameHelper.Tests;

public sealed class PlaytimeReportLibraryTests
{
    [Fact]
    public async Task Report_ReadsOneSnapshotAndReturnsAggregatesWithoutRawSessions()
    {
        var history = new Mock<IPlaytimeSnapshotProvider>();
        history.Setup(source => source.GetSnapshot()).Returns(new PlaytimeSnapshot([
            new() { GameName = "key", Sessions = [new("key", new(2026, 1, 1), new(2026, 1, 1, 1, 0, 0), TimeSpan.FromHours(1), 60)] }
        ], null));
        var configuration = new Mock<IGameConfiguration>();
        configuration.Setup(source => source.Read()).Returns(new AppConfig { Games = [new() { DataKey = "key", DisplayName = "Display" }] });
        var response = await Library(configuration.Object, history.Object).ExecuteAsync(new() { Operation = GameLibraryOperation.Report, ReportPeriod = new(2026, 1) });
        Assert.True(response.Success); Assert.Equal(60, response.Report!.Minutes);
        Assert.Equal("Display", Assert.Single(response.Report.TopGames).Name);
        Assert.Null(response.Statistics);
        history.Verify(source => source.GetSnapshot(), Times.Once);
        configuration.Verify(source => source.Change(It.IsAny<Action<AppConfig>>()), Times.Never);
    }

    [Fact]
    public async Task Report_ReadFailure_ReturnsFailureWithoutFabricatedReport()
    {
        var history = new Mock<IPlaytimeSnapshotProvider>();
        history.Setup(source => source.GetSnapshot()).Returns(new PlaytimeSnapshot([], "source.csv", ReadFailed: true));
        var response = await Library(Mock.Of<IGameConfiguration>(), history.Object).ExecuteAsync(new() { Operation = GameLibraryOperation.Report, ReportPeriod = new(2026) });
        Assert.False(response.Success); Assert.Null(response.Report); Assert.Contains("历史", response.Error);
    }

    [Fact]
    public async Task Report_PipeSerialization_RoundTripsPeriodAndReportWithoutRecursivePrevious()
    {
        using var stream = new MemoryStream();
        await GameLibraryPipe.WriteAsync(stream, new GameLibraryRequest { Operation = GameLibraryOperation.Report, ReportPeriod = new(2026, 1) }, CancellationToken.None);
        stream.Position = 0;
        var request = await GameLibraryPipe.ReadAsync<GameLibraryRequest>(stream, CancellationToken.None);
        Assert.Equal(new ReportPeriod(2026, 1), request.ReportPeriod);
        stream.SetLength(0); stream.Position = 0;
        await GameLibraryPipe.WriteAsync(stream, new GameLibraryResponse { Success = true,
            Report = new() { Period = request.ReportPeriod!, Minutes = 42 } }, CancellationToken.None);
        stream.Position = 0;
        var response = await GameLibraryPipe.ReadAsync<GameLibraryResponse>(stream, CancellationToken.None);
        Assert.Equal(42, response.Report!.Minutes); Assert.Equal(new ReportPeriod(2026, 1), response.Report.Period);
    }

    private static GameLibrary Library(IGameConfiguration config, IPlaytimeSnapshotProvider history) => new(config,
        Mock.Of<IGameCatalogService>(), Mock.Of<IGameAutomationService>(), Mock.Of<IMonitorControlService>(),
        Mock.Of<IGameLaunchCoordinator>(), Mock.Of<ISteamGameResolver>(), history, new Clock());
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
