using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Desktop;

namespace GameHelper.Tests;

public sealed class ReportJournalMergeTests
{
    [Fact]
    public void Apply_YearBoundary_SortsByDateAndCountsOnlyCompletedStatuses()
    {
        var journal = new Dictionary<string, JournalRecord>(StringComparer.OrdinalIgnoreCase)
        {
            ["late"] = new("已通关", new DateOnly(2026, 12, 31), "年末"),
            ["first"] = new("全成就", new DateOnly(2026, 1, 1), ""),
            ["before"] = new("已通关", new DateOnly(2025, 12, 31), ""),
            ["after"] = new("已通关", new DateOnly(2027, 1, 1), ""),
            ["missing-date"] = new("已通关", null, "")
        };
        var report = ReportJournalMerge.Apply(new PlaytimeReport { Period = new(2026) }, journal, key => "名称 " + key);
        Assert.Equal(["first", "late"], report.ClearedGames.Select(game => game.DataKey));
        Assert.Equal("名称 first", report.ClearedGames[0].Name);
        Assert.Equal(1, report.PreviousClearedGames);
        Assert.Equal(1, report.ClearedChange);
    }

    [Fact]
    public void Apply_QuarterBoundary_IncludesFirstAndLastDayExcludesNextQuarter()
    {
        var journal = new Dictionary<string, JournalRecord>(StringComparer.OrdinalIgnoreCase)
        {
            ["previous"] = new("已通关", new DateOnly(2025, 12, 31), ""),
            ["first"] = new("已通关", new DateOnly(2026, 1, 1), ""),
            ["last"] = new("全成就", new DateOnly(2026, 3, 31), ""),
            ["next"] = new("已通关", new DateOnly(2026, 4, 1), "")
        };
        var report = ReportJournalMerge.Apply(new PlaytimeReport { Period = new(2026, 1) }, journal);
        Assert.Equal(["first", "last"], report.ClearedGames.Select(game => game.DataKey));
        Assert.Equal(1, report.PreviousClearedGames);
    }

    [Theory]
    [InlineData("未开始")]
    [InlineData("在玩")]
    [InlineData("搁置")]
    public void Apply_IgnoresJournalStatusesThatAreNotCompleted(string status)
    {
        var journal = new Dictionary<string, JournalRecord> { ["a"] = new(status, new DateOnly(2026, 2, 2), "短评") };
        var report = ReportJournalMerge.Apply(new PlaytimeReport { Period = new(2026) }, journal);
        Assert.Empty(report.ClearedGames);
        Assert.Equal(0, report.PreviousClearedGames);
    }

    [Fact]
    public void Render_OmitsSectionWhenPeriodHasNoCompletedGames()
    {
        var markdown = PlaytimeReportMarkdown.Render(new PlaytimeReport { Period = new(2026), PreviousClearedGames = 2 });
        Assert.DoesNotContain("本期通关", markdown);
    }

    [Fact]
    public void Render_ExportTextShowsGameDateStatusNoteAndPreviousComparison()
    {
        var report = new PlaytimeReport
        {
            Period = new(2026, 1),
            ClearedGames =
            [
                new("a", "游戏 A", new DateOnly(2026, 1, 1), "全成就"),
                new("b", "游戏 | B", new DateOnly(2026, 3, 31), "已通关", "旅程 [结束]")
            ],
            PreviousClearedGames = 1
        };
        var markdown = PlaytimeReportMarkdown.Render(report);
        Assert.Contains("## 本期通关", markdown);
        Assert.Contains("本期通关 2 款 · 上一期 1 款（变化 +1 款）", markdown);
        var first = markdown.IndexOf("2026-01-01 · 游戏 A · 全成就", StringComparison.Ordinal);
        var second = markdown.IndexOf("2026-03-31 · 游戏 \\| B · 已通关 · 旅程 \\[结束\\]", StringComparison.Ordinal);
        Assert.True(first >= 0);
        Assert.True(second > first);
    }
}
