using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using Moq;

namespace GameHelper.Tests;

public sealed class GameLibraryHistoryTests
{
    private readonly Clock _clock = new();
    private readonly History _history = new();
    private GameLibrary Create() => new(Mock.Of<IGameConfiguration>(c => c.Read() == new AppConfig
        { Games = new() { new() { DataKey = "game", Executable = "game.exe" } } }),
        Mock.Of<IGameCatalogService>(), Mock.Of<IGameAutomationService>(), Mock.Of<IMonitorControlService>(),
        Mock.Of<IGameLaunchCoordinator>(l => l.GetGameStates() == new Dictionary<string, string>()),
        Mock.Of<ISteamGameResolver>(), _history, _clock);

    [Fact]
    public async Task StableHistoryVersionReadsHistoryOnceWhileMetadataStaysFresh()
    {
        var library = Create();
        for (var index = 0; index < 100; index++) Assert.True((await List(library)).Success);
        Assert.Equal(1, _history.Reads);
        Assert.Equal(100, _history.VersionReads);
        _history.Version = new("restored", 0);
        await List(library);
        Assert.Equal(2, _history.Reads);
    }

    [Fact]
    public async Task CacheUsesActualSnapshotVersionAcrossConcurrentCommit()
    {
        var library = Create();
        _history.SnapshotVersion = new("database", 2);
        await List(library);
        _history.Version = _history.SnapshotVersion;
        await List(library);
        Assert.Equal(1, _history.Reads);
    }

    [Fact]
    public async Task ConcurrentListsDoNotDuplicateHistoryMaterialization()
    {
        var library = Create();
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => List(library))));
        Assert.All(results, result => Assert.True(result.Success));
        Assert.Equal(1, _history.Reads);
    }

    [Fact]
    public async Task FailedReadsInvalidateCacheAndNeverReturnSuccessfulEmptyOrStaleHistory()
    {
        var library = Create();
        await List(library);
        _history.MetadataFailure = true;
        Assert.False((await List(library)).Success);
        _history.MetadataFailure = false;
        _history.ReadFailed = true;
        Assert.False((await List(library)).Success);
        _history.ReadFailed = false;
        Assert.True((await List(library)).Success);
        Assert.Equal(3, _history.Reads);
    }

    [Fact]
    public async Task LegacyNullVersionAlwaysReadsAgain()
    {
        _history.Version = null;
        var library = Create();
        await List(library); await List(library); await List(library);
        Assert.Equal(3, _history.Reads);
    }

    [Fact]
    public async Task FutureRecencyBecomesVisibleAndClockRollbackReevaluatesWithoutHistoryCommit()
    {
        var end = _clock.Now.AddMinutes(10).UtcDateTime;
        _history.Records = [new() { GameName = "game", Sessions = [new("game", end.AddMinutes(-20), end, TimeSpan.FromMinutes(20), 20)] },
            new() { GameName = "GAME", Sessions = [new("GAME", end.AddMinutes(1), end, TimeSpan.Zero, 5)] }];
        var library = Create();
        var game = (await List(library)).Games.Single();
        Assert.Null(game.LastPlayedAt); Assert.Equal(25, game.TotalMinutes);
        _clock.Now = _clock.Now.AddMinutes(10);
        Assert.NotNull((await List(library)).Games.Single().LastPlayedAt);
        _clock.Now = _clock.Now.AddMinutes(-10);
        Assert.Null((await List(library)).Games.Single().LastPlayedAt);
        Assert.Equal(3, _history.Reads);
    }

    private static Task<GameLibraryResponse> List(GameLibrary library) => library.ExecuteAsync(new() { Operation = GameLibraryOperation.List });
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2030, 1, 2, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
    private sealed class History : IPlaytimeSnapshotProvider
    {
        public int Reads, VersionReads;
        public bool ReadFailed, MetadataFailure;
        public PlaytimeHistoryVersion? Version = new("database", 1), SnapshotVersion;
        public IReadOnlyList<GamePlaytimeRecord> Records = [];
        public IReadOnlyList<GamePlaytimeRecord> GetPlaytimeRecords() { Reads++; return Records; }
        public PlaytimeSnapshot GetSnapshot() { Reads++; return new(Records, null, ReadFailed, SnapshotVersion ?? Version); }
        public PlaytimeHistoryVersion? GetHistoryVersion()
        { VersionReads++; if (MetadataFailure) throw new IOException("metadata unavailable"); return Version; }
    }
}
