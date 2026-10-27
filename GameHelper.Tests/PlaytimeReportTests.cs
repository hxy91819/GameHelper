using GameHelper.Core.Models;
using GameHelper.Core.Services;

namespace GameHelper.Tests;

public sealed class PlaytimeReportTests
{
    private static readonly TimeProvider Clock = new FixedClock(new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero));
    private static PlaySession Session(string end, long minutes = 60) => new("a", DateTime.Parse(end).AddHours(-2),
        DateTime.Parse(end), TimeSpan.FromMinutes(minutes), minutes);
    private static PlaytimeReport Build(ReportPeriod period, params PlaySession[] sessions) =>
        PlaytimeReportBuilder.Build([new() { GameName = "a", Sessions = sessions.ToList() }], [], period, Clock);

    [Fact]
    public void Build_YearBoundary_AssignsWholeCrossMidnightSessionToEndDate()
    {
        var report = Build(new(2026), Session("2025-12-31T23:59:59", 20), Session("2026-01-01T00:00:00", 40),
            Session("2026-12-31T23:59:59", 70), Session("2027-01-01T00:00:00", 90));
        Assert.Equal(110, report.Minutes);
        Assert.Equal(2, report.ActiveDays);
        Assert.Equal(20, report.PreviousMinutes);
        Assert.Equal(90, report.MinutesChange);
        Assert.Equal(450d, report.MinutesChangePercent);
        Assert.Equal(70, report.LongestSession!.Minutes);
        Assert.Equal(12, report.Months.Count);
        Assert.Equal(report.Minutes, report.Months.Sum(bucket => bucket.Minutes));
        Assert.Equal(report.Minutes, report.Weeks.Sum(bucket => bucket.Minutes));
        Assert.False(report.IsPartial);
    }

    [Theory]
    [InlineData(1, "2026-01-01", "2026-03-31T23:59:59", "2026-04-01")]
    [InlineData(2, "2026-04-01", "2026-06-30T23:59:59", "2026-07-01")]
    [InlineData(3, "2026-07-01", "2026-09-30T23:59:59", "2026-10-01")]
    [InlineData(4, "2026-10-01", "2026-12-31T23:59:59", "2027-01-01")]
    public void Build_Quarter_IncludesFirstAndLastDayExcludesNextQuarter(int quarter, string first, string last, string next)
    {
        var report = Build(new(2026, quarter), Session(first), Session(last), Session(next));
        Assert.Equal(120, report.Minutes);
        Assert.Equal(3, report.Months.Count);
        Assert.Equal(report.Minutes, report.Weeks.Sum(bucket => bucket.Minutes));
        Assert.All(report.Weeks.Skip(1), week => Assert.Equal(DayOfWeek.Monday, week.Start.DayOfWeek));
    }

    [Fact]
    public void Build_FirstQuarter_ComparesPreviousYearFourthQuarter()
    {
        var report = Build(new(2026, 1), Session("2025-10-01", 20), Session("2025-12-31", 30), Session("2026-01-01", 100));
        Assert.Equal(new ReportPeriod(2025, 4), report.Period.Previous);
        Assert.Equal(50, report.PreviousMinutes);
        Assert.Equal(100d, report.MinutesChangePercent);
        Assert.Equal(2, report.PreviousActiveDays);
    }

    [Fact]
    public void Build_Empty_HasZeroStatisticsAndNoPercentage()
    {
        var report = Build(new(2026));
        Assert.Equal(0, report.Minutes); Assert.Equal(0, report.ActiveDays); Assert.Equal(0, report.GamesPlayed);
        Assert.Empty(report.TopGames); Assert.Null(report.LongestSession); Assert.Null(report.MostPlayedHour);
        Assert.Null(report.MinutesChangePercent); Assert.All(report.HourMinutes, value => Assert.Equal(0, value));
        Assert.Contains("本期没有", PlaytimeReportMarkdown.Render(report));
    }

    [Fact]
    public void Build_UtcSessions_UsesInjectedLocalTimezone()
    {
        var clock = new FixedClock(new(2027, 1, 15, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(8), "test", "test"));
        var end = new DateTime(2025, 12, 31, 16, 0, 0, DateTimeKind.Utc);
        var report = PlaytimeReportBuilder.Build([new() { GameName = "a", Sessions = [new("a", end.AddHours(-1), end, TimeSpan.FromHours(1), 60)] }],
            [new() { DataKey = "a", AddedAt = new(2025, 12, 31, 16, 0, 0, TimeSpan.Zero) }], new(2026, 1), clock);
        Assert.Equal(60, report.Minutes); Assert.Equal(23, report.MostPlayedHour); Assert.Equal(["a"], report.NewGameKeys);
    }

    [Fact]
    public void Build_RankAndNewGames_UsesMetadataAndExcludesInvalidAndFuture()
    {
        var records = Enumerable.Range(1, 7).Select(index => new GamePlaytimeRecord { GameName = "g" + index,
            Sessions = [Session("2026-02-01", index * 10)] }).ToList();
        records.Add(new() { GameName = "bad", Sessions = [Session("2026-02-01", -1), Session("2028-01-01")] });
        var configs = new[] { new GameConfig { DataKey = "g1", DisplayName = "Friendly", AddedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) },
            new GameConfig { DataKey = "old" }, new GameConfig { DataKey = "next", AddedAt = new(2028, 1, 1, 0, 0, 0, TimeSpan.Zero) } };
        var report = PlaytimeReportBuilder.Build(records, configs, new(2026), Clock);
        Assert.Equal(7, report.GamesPlayed); Assert.Equal(5, report.TopGames.Count);
        Assert.Equal("g7", report.TopGames[0].DataKey); Assert.Equal(["g1"], report.NewGameKeys);
        Assert.Equal(280, report.Minutes);
    }

    [Fact]
    public void Build_PartialPeriod_ExcludesFutureAndInvalidSessions()
    {
        var report = Build(new(2027), Session("2027-01-10"), Session("2027-01-16"),
            new("a", new(2027, 1, 11), new(2027, 1, 10), TimeSpan.Zero, 10));
        Assert.Equal(60, report.Minutes); Assert.True(report.IsPartial);
        Assert.Contains("本期尚未结束", PlaytimeReportMarkdown.Render(report));
    }

    [Fact]
    public void Render_Markdown_EscapesGameNamesAndIncludesCommentary()
    {
        var report = new PlaytimeReport { TopGames = [new("key", "[link](x)\n<script>", 30, 1, default)] };
        var markdown = PlaytimeReportMarkdown.Render(report, "点评内容");
        Assert.DoesNotContain("<script>", markdown); Assert.Contains("\\[link\\]", markdown); Assert.Contains("点评内容", markdown);
    }

    private sealed class FixedClock(DateTimeOffset now, TimeZoneInfo? zone = null) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => zone ?? TimeZoneInfo.Utc;
    }
}
