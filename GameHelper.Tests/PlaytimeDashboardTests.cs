using GameHelper.Core.Models;
using GameHelper.Core.Services;

namespace GameHelper.Tests;

public sealed class PlaytimeDashboardTests
{
    private static readonly DateTime Today = new(2026, 4, 21, 12, 0, 0, DateTimeKind.Unspecified);
    private static readonly TimeProvider Clock = new FixedClock(new DateTimeOffset(Today, TimeSpan.Zero), TimeZoneInfo.Utc);
    private static GameConfig[] Configs => [new() { DataKey = "a", Executable = "a.exe", DisplayName = "游戏 A" }];
    private static GamePlaytimeRecord Record(string key, params PlaySession[] sessions) => new() { GameName = key, Sessions = sessions.ToList() };
    private static PlaySession Session(string key, DateTime end, long minutes) => new(key, end.AddMinutes(-Math.Max(1, minutes)), end, TimeSpan.FromMinutes(minutes), minutes);

    [Fact]
    public void Range_UsesCompletedEndDayAndPersistedMinutesIncludingRemovedGames()
    {
        var firstDay = Today.Date.AddDays(-6);
        var crossMidnight = new PlaySession("a", firstDay.AddMinutes(-20), firstDay.AddMinutes(15), TimeSpan.FromMinutes(35), 34);
        var result = PlaytimeDashboardBuilder.Build([
            Record("a", crossMidnight, Session("a", Today.AddDays(-1), 60), Session("a", Today.AddDays(-8), 90)),
            Record("removed", Session("removed", Today.AddDays(-2), 20))], Configs, new(7), Clock);
        Assert.Equal(114, result.Minutes);
        Assert.Equal(90, result.PreviousMinutes);
        Assert.Equal(3, result.Sessions);
        Assert.Equal(3, result.ActiveDays);
        Assert.Equal(2, result.GamesPlayed);
        Assert.Equal(34, result.Trend[0].Minutes);
        Assert.Equal("游戏 A", result.Games[0].Name);
        Assert.Contains(result.Games, game => game.Name == "removed");
        Assert.Equal(7, result.Trend.Count);
    }

    [Fact]
    public void Pagination_FiltersOnlySessionTableAndClampsOffset()
    {
        var records = new[] { Record("a", Enumerable.Range(0, 75).Select(index => Session("a", Today.AddMinutes(-index - 1), 2)).ToArray()),
            Record("b", Session("b", Today.AddDays(-1), 50)) };
        var page = PlaytimeDashboardBuilder.Build(records, Configs, new(30, "A", 9999), Clock);
        Assert.Equal(200, page.Minutes);
        Assert.Equal(76, page.Sessions);
        Assert.Equal(75, page.RecentTotal);
        Assert.Equal(60, page.Offset);
        Assert.Equal(15, page.Recent.Count);
        Assert.All(page.Recent, session => Assert.Equal("a", session.DataKey));
        Assert.True(page.Recent.Zip(page.Recent.Skip(1), (first, second) => first.End >= second.End).All(value => value));
        var empty = PlaytimeDashboardBuilder.Build(records, Configs, new(30, "absent", -3), Clock);
        Assert.Empty(empty.Recent);
        Assert.Equal(0, empty.Offset);
        Assert.Equal(200, empty.Minutes);
    }

    [Fact]
    public void AllHistory_KeepsOldTotalsButBoundsCalendarTrendAndRankings()
    {
        var records = Enumerable.Range(0, 1001).Select(index => Record("game" + index, Session("game" + index, Today.AddDays(-1), 1))).ToList();
        records.Add(Record("old", Session("old", Today.AddDays(-900), 60)));
        var data = PlaytimeDashboardBuilder.Build(records, Configs, new(0), Clock);
        Assert.Equal(1061, data.Minutes);
        Assert.Equal(1002, data.GamesPlayed);
        Assert.Equal(1000, data.Games.Count);
        Assert.Equal(12, data.Trend.Count);
        Assert.Equal(1001, data.Trend.Sum(day => day.Minutes));
        Assert.InRange(data.Calendar.Count, 365, 371);
        Assert.Equal(DayOfWeek.Monday, data.Calendar[0].Date.DayOfWeek);
        Assert.Equal(DateOnly.FromDateTime(Today), data.Calendar[^1].Date);
        Assert.Equal(1001, data.Calendar.Sum(day => day.Minutes));
        Assert.Equal(30, data.Recent.Count);
    }

    [Fact]
    public void EmptyAndInvalidHistory_StillProducesBoundedZeroFilledCharts()
    {
        var records = new[] { Record("a", Session("a", Today.AddDays(1), 10), Session("a", Today.AddDays(-1), -4),
            new PlaySession("a", Today, Today.AddMinutes(-1), TimeSpan.Zero, 10)) };
        var empty = PlaytimeDashboardBuilder.Build(records, Configs, new(30), Clock);
        Assert.Equal(0, empty.Minutes);
        Assert.Equal(0, empty.Sessions);
        Assert.Empty(empty.Games);
        Assert.Equal(30, empty.Trend.Count);
        Assert.All(empty.Trend, day => Assert.Equal(0, day.Minutes));
        var shortSession = PlaytimeDashboardBuilder.Build([Record("a", Session("a", Today.AddMinutes(-1), 0))], Configs, new(30), Clock);
        Assert.Equal(1, shortSession.ActiveDays);
        Assert.Equal(1, shortSession.Calendar[^1].Sessions);
    }

    [Fact]
    public void UtcRecords_AreBucketedInClockLocalTimeZone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("dashboard-test", TimeSpan.FromHours(8), "test", "test");
        var clock = new FixedClock(new DateTimeOffset(2026, 4, 21, 4, 0, 0, TimeSpan.Zero), zone);
        var end = new DateTime(2026, 4, 20, 18, 0, 0, DateTimeKind.Utc);
        var data = PlaytimeDashboardBuilder.Build([Record("a", Session("a", end, 30))], Configs, new(7), clock);
        Assert.Equal(new DateOnly(2026, 4, 21), data.Today);
        Assert.Equal(30, data.Trend[^1].Minutes);
        Assert.Equal(2, data.Recent[0].End.Hour);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(14)]
    [InlineData(365)]
    public void UnsupportedRanges_AreRejected(int days) => Assert.Throws<ArgumentException>(() => PlaytimeDashboardBuilder.Build([], [], new(days), Clock));

    private sealed class FixedClock(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => zone;
    }
}
