using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Infrastructure.Providers;
using GameHelper.Core.Abstractions;
using Moq;

namespace GameHelper.Tests;

public sealed class LibraryRecencyTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("test+8", TimeSpan.FromHours(8), "test", "test");
    }

    [Fact]
    public void RecentHistory_UsesLocalOffsetAndIgnoresInvalidOrFutureSessions()
    {
        var clock = new Clock();
        var end = DateTime.SpecifyKind(clock.Now.UtcDateTime.AddHours(7), DateTimeKind.Unspecified);
        PlaySession Session(DateTime finish, long minutes = 1) => new("a", finish.AddMinutes(-1), finish, TimeSpan.FromMinutes(minutes), minutes);
        var history = new Mock<IPlaytimeSnapshotProvider>();
        history.Setup(item => item.GetSnapshot()).Returns(new PlaytimeSnapshot([
            new() { GameName = "A", Sessions = [Session(end), Session(end.AddDays(2)), Session(end.AddMinutes(1), -1)] },
            new() { GameName = "a", Sessions = [Session(end.AddMinutes(-5))] }
        ], null));
        var values = new LibraryHistorySummary(history.Object, clock).Read()!;
        Assert.Single(values);
        Assert.Equal(clock.Now.AddHours(-1), values["a"].LastPlayedAt);
    }

    [Fact]
    public void CatalogIntake_PersistsAddedTimeWithoutBackfillingLegacyOrResettingOnEdit()
    {
        var root = Path.Combine(Path.GetTempPath(), "gh-added-at", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "config.yml");
            var source = new YamlConfigProvider(path);
            source.Change(config => config.Games = [new() { DataKey = "legacy", Executable = "legacy.exe" }]);
            var clock = new Clock(); var catalog = new GameCatalogService(source, clock);
            catalog.Intake(new() { Executable = ExecutableIdentity.Parse("new.exe"), DataKey = "new" });
            var expected = clock.Now; clock.Now = clock.Now.AddDays(1);
            catalog.Update("new", new() { DisplayName = "Renamed" });
            catalog.Intake(new() { Executable = ExecutableIdentity.Parse("new.exe") });
            catalog.Update("legacy", new() { DisplayName = "Old" });
            var reopened = new YamlConfigProvider(path).Read();
            Assert.Equal(expected, reopened.Games.Single(game => game.DataKey == "new").AddedAt);
            Assert.Null(reopened.Games.Single(game => game.DataKey == "legacy").AddedAt);
        }
        finally { Directory.Delete(root, true); }
    }
}
