using GameHelper.Core.Models;
using GameHelper.Core.Services;

namespace GameHelper.Tests.Sync;

public class StatsReportBuilderTests
{
    private static readonly DateTime GeneratedAt = new(2026, 9, 5, 22, 0, 0);

    private readonly StatsReportBuilder _builder = new();

    [Fact]
    public void Build_WithSessions_ProducesAggregatesAndDailyCsv()
    {
        var records = new List<GamePlaytimeRecord>
        {
            new()
            {
                GameName = "elden_ring",
                Sessions =
                {
                    Session("elden_ring", new DateTime(2026, 9, 4, 20, 0, 0), new DateTime(2026, 9, 4, 21, 0, 0), 60),
                    Session("elden_ring", new DateTime(2026, 9, 3, 19, 30, 0), new DateTime(2026, 9, 3, 20, 0, 0), 30)
                }
            },
            new()
            {
                GameName = "hades",
                Sessions =
                {
                    Session("hades", new DateTime(2026, 9, 5, 21, 0, 0), new DateTime(2026, 9, 5, 21, 45, 0), 45)
                }
            }
        };
        var games = new List<GameConfig>
        {
            new() { DataKey = "elden_ring", DisplayName = "艾尔登法环" }
        };

        var report = _builder.Build(records, games, GeneratedAt, includeRawCsv: false);

        Assert.Equal(3, report.SessionCount);
        Assert.Equal(2, report.GameCount);
        Assert.Equal(135, report.TotalMinutes);
        Assert.Equal(2, report.Files.Count);
        Assert.Equal("README.md", report.Files[0].RelativePath);
        Assert.Equal("daily.csv", report.Files[1].RelativePath);

        var dailyCsv = report.Files[1].Content;
        Assert.StartsWith("date,game,minutes", dailyCsv);
        // 按日期排序；无显示名的游戏回退到 DataKey，有显示名的仍用稳定 DataKey 作 CSV 主键。
        Assert.Contains("2026-09-03,elden_ring,30", dailyCsv);
        Assert.Contains("2026-09-04,elden_ring,60", dailyCsv);
        Assert.Contains("2026-09-05,hades,45", dailyCsv);
        Assert.True(dailyCsv.IndexOf("2026-09-03", StringComparison.Ordinal) < dailyCsv.IndexOf("2026-09-05", StringComparison.Ordinal));

        var readme = report.Files[0].Content;
        Assert.Contains("艾尔登法环", readme);
        Assert.Contains("2026-09-05", readme);
        // 报告不得包含生成时刻，否则内容指纹永远变化，"内容未变化跳过上传"失效。
        Assert.DoesNotContain("2026-09-05 22:00", readme);
    }

    [Fact]
    public void Build_WithUtcKindEnd_AttributesToLocalDay()
    {
        var utcEnd = DateTime.UtcNow;
        var expectedDay = utcEnd.ToLocalTime().Date;
        var records = new List<GamePlaytimeRecord>
        {
            new()
            {
                GameName = "tz_game",
                Sessions =
                {
                    new PlaySession(
                        "tz_game",
                        utcEnd.AddMinutes(-30),
                        DateTime.SpecifyKind(utcEnd, DateTimeKind.Utc),
                        TimeSpan.FromMinutes(30),
                        30)
                }
            }
        };

        var report = _builder.Build(records, new List<GameConfig>(), GeneratedAt, includeRawCsv: false);

        Assert.Contains(
            expectedDay.ToString("yyyy-MM-dd") + ",tz_game,30",
            report.Files[1].Content);
    }

    [Fact]
    public void Build_EmptyData_ProducesEmptyReport()
    {
        var report = _builder.Build(
            new List<GamePlaytimeRecord>(),
            new List<GameConfig>(),
            GeneratedAt,
            includeRawCsv: false);

        Assert.Equal(0, report.SessionCount);
        Assert.Contains("暂无游玩数据", report.Files[0].Content);
        Assert.DoesNotContain(",,", report.Files[1].Content);
    }

    [Fact]
    public void Build_IncludeRawCsv_AppendsRawDetailFile()
    {
        var records = new List<GamePlaytimeRecord>
        {
            new()
            {
                GameName = "comma_game",
                Sessions =
                {
                    Session("comma_game", new DateTime(2026, 9, 4, 20, 0, 0), new DateTime(2026, 9, 4, 21, 0, 0), 60)
                }
            }
        };

        var report = _builder.Build(records, new List<GameConfig>(), GeneratedAt, includeRawCsv: true);

        Assert.Equal(3, report.Files.Count);
        Assert.Equal("raw/playtime.csv", report.Files[2].RelativePath);
        Assert.StartsWith("game,start_time,end_time,duration_minutes", report.Files[2].Content);
        Assert.Contains("comma_game,2026-09-04T20:00:00,2026-09-04T21:00:00,60", report.Files[2].Content);
    }

    [Fact]
    public void Build_SameInput_ProducesStableHash()
    {
        var records = SingleSessionRecords();
        var games = new List<GameConfig>();

        var first = _builder.Build(records, games, GeneratedAt, includeRawCsv: false);
        var second = _builder.Build(records, games, GeneratedAt, includeRawCsv: false);

        Assert.Equal(first.ContentHash, second.ContentHash);
    }

    [Fact]
    public void Build_ChangedData_ChangesHash()
    {
        var games = new List<GameConfig>();
        var before = _builder.Build(SingleSessionRecords(), games, GeneratedAt, includeRawCsv: false);
        var after = _builder.Build(SingleSessionRecords(45), games, GeneratedAt, includeRawCsv: false);

        Assert.NotEqual(before.ContentHash, after.ContentHash);
    }

    private static List<GamePlaytimeRecord> SingleSessionRecords(int minutes = 60)
    {
        var start = new DateTime(2026, 9, 4, 20, 0, 0);
        var end = start.AddMinutes(minutes);
        return new List<GamePlaytimeRecord>
        {
            new()
            {
                GameName = "game_a",
                Sessions =
                {
                    Session("game_a", start, end, minutes)
                }
            }
        };
    }

    private static PlaySession Session(string game, DateTime start, DateTime end, long minutes) =>
        new(game, start, end, end - start, minutes);
}
