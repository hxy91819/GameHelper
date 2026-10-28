using System.Globalization;
using System.Text;
using GameHelper.Core.Models;

namespace GameHelper.Core.Services;

/// <summary>Shared desktop preview and Markdown export of deterministic report statistics.</summary>
public static class PlaytimeReportMarkdown
{
    public static string Render(PlaytimeReport report, string? commentary = null)
    {
        static string Escape(string value) => value.Replace("\\", "\\\\").Replace("|", "\\|")
            .Replace("\r", " ").Replace("\n", " ").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("[", "\\[").Replace("]", "\\]").Replace("*", "\\*").Replace("_", "\\_").Replace("`", "\\`");
        static string Hours(long minutes) => (minutes / 60d).ToString("0.0", CultureInfo.InvariantCulture) + " 小时";
        static string Signed(int value) => value > 0 ? "+" + value.ToString(CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);
        var text = new StringBuilder();
        text.AppendLine($"# {report.Period}游玩报告").AppendLine();
        text.AppendLine($"{report.Period.Start:yyyy-MM-dd} 至 {report.Period.EndExclusive.AddDays(-1):yyyy-MM-dd}" +
            (report.IsPartial ? "（本期尚未结束，截至当前已结束会话）" : "")).AppendLine();
        text.AppendLine($"总时长：{Hours(report.Minutes)} · 游玩天数：{report.ActiveDays} · 游戏：{report.GamesPlayed} · 会话：{report.Sessions}").AppendLine();
        text.AppendLine($"上一期（{report.Period.Previous}）：{Hours(report.PreviousMinutes)} · {report.PreviousActiveDays} 天 · {report.PreviousGamesPlayed} 款");
        text.AppendLine($"时长变化：{report.MinutesChange:+0;-0;0} 分钟（" +
            (report.MinutesChangePercent is { } percent ? percent.ToString("+0.0;-0.0;0", CultureInfo.InvariantCulture) + "%" : "上期为零，无百分比") + "）").AppendLine();
        text.AppendLine(report.AverageRating is { } average ? $"本期平均分：{average.ToString("0.00", CultureInfo.InvariantCulture)} / 5 · {report.RatingSamples} 局" : "本期平均分：未评分（0 局）").AppendLine();
        if (report.HighestRatedSession is { } highest)
            text.AppendLine($"最高分的一局：{Escape(highest.DataKey)} · {highest.Rating!.Stars} 星 · {highest.End:yyyy-MM-dd HH:mm}").AppendLine();
        if (report.ClearedGames.Count > 0)
        {
            text.AppendLine("## 本期通关").AppendLine();
            text.AppendLine($"本期通关 {report.ClearedGames.Count} 款 · 上一期 {report.PreviousClearedGames} 款（变化 {Signed(report.ClearedChange)} 款）").AppendLine();
            foreach (var game in report.ClearedGames)
                text.AppendLine($"- {game.CompletedOn:yyyy-MM-dd} · {Escape(game.Name)} · {Escape(game.Status)}" +
                    (string.IsNullOrWhiteSpace(game.Note) ? string.Empty : $" · {Escape(game.Note)}"));
            text.AppendLine();
        }
        text.AppendLine("## Top 5").AppendLine();
        foreach (var game in report.TopGames) text.AppendLine($"- {Escape(game.Name)}：{Hours(game.Minutes)} · {game.Sessions} 次");
        if (report.TopGames.Count == 0) text.AppendLine("本期没有已结束的游玩记录。");
        text.AppendLine().AppendLine(report.LongestSession is { } longest
            ? $"最长单局：{Escape(longest.Name)} · {longest.Minutes} 分钟" : "最长单局：暂无");
        text.AppendLine(report.MostPlayedHour is { } hour ? $"最常游玩时段：{hour:00}:00–{hour + 1:00}:00（按开始小时归入整局分钟）" : "最常游玩时段：暂无");
        text.AppendLine().AppendLine("## 月分布").AppendLine();
        foreach (var bucket in report.Months) text.AppendLine($"- {bucket.Label}：{Hours(bucket.Minutes)}");
        text.AppendLine().AppendLine("## 周分布（周一开始，首末周按报告边界截断）").AppendLine();
        foreach (var bucket in report.Weeks) text.AppendLine($"- {bucket.Start:MM-dd}–{bucket.End:MM-dd}：{Hours(bucket.Minutes)}");
        text.AppendLine().AppendLine("## 新加入游戏（首次登记时间）").AppendLine();
        foreach (var key in report.NewGameKeys) text.AppendLine("- " + Escape(key));
        if (report.NewGameKeys.Count == 0) text.AppendLine("暂无；旧条目未记录首次登记日期时不作推断。");
        text.AppendLine().AppendLine("口径：本地结束日归档整局；采用保存分钟数；不含进行中会话；多游戏可能重叠。上一期为完整自然年／季度，未结束期间不作同比预测。");
        if (!string.IsNullOrWhiteSpace(commentary))
            text.AppendLine().AppendLine("## 大模型点评（仅供参考）").AppendLine().AppendLine(commentary);
        return text.ToString();
    }
}
