using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Core.Services;

/// <summary>
/// 把游玩时长快照聚合成可推送的报告（Markdown 总览 + 按日×按游戏 CSV）。
/// 只输出聚合数据，不含精确时间戳（原始明细需显式开启且仅建议私有仓库）。
/// 报告内容不嵌生成时刻，保证内容指纹稳定（否则"内容未变化跳过上传"失效）；更新时间见提交历史。
/// </summary>
public sealed class StatsReportBuilder
{
    private const string ReportFileName = "README.md";
    private const string DailyCsvFileName = "daily.csv";
    private const string RawCsvFileName = "raw/playtime.csv";

    /// <summary>
    /// 构建推送内容。
    /// </summary>
    /// <param name="records">全部游玩记录。</param>
    /// <param name="games">当前游戏配置，用于把 DataKey 解析为显示名。</param>
    /// <param name="generatedAtLocal">报告生成本地时间（用于“最近 7 天”窗口）。</param>
    /// <param name="includeRawCsv">是否附带原始会话明细。</param>
    public StatsReport Build(
        IReadOnlyList<GamePlaytimeRecord> records,
        IReadOnlyList<GameConfig> games,
        DateTime generatedAtLocal,
        bool includeRawCsv)
    {
        var displayNames = BuildDisplayNames(games);
        var sessions = FlattenSessions(records, displayNames);

        var files = new List<StatsUploadFile>
        {
            new(ReportFileName, BuildReportMarkdown(sessions, generatedAtLocal)),
            new(DailyCsvFileName, BuildDailyCsv(sessions))
        };

        if (includeRawCsv)
        {
            files.Add(new StatsUploadFile(RawCsvFileName, BuildRawCsv(sessions)));
        }

        var totalMinutes = sessions.Sum(session => session.DurationMinutes);
        return new StatsReport(
            files,
            sessions.Count,
            sessions.Select(session => session.DataKey).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            totalMinutes,
            ComputeContentHash(files));
    }

    private static Dictionary<string, string> BuildDisplayNames(IReadOnlyList<GameConfig> games)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in games)
        {
            if (string.IsNullOrWhiteSpace(game.DataKey))
            {
                continue;
            }

            if (!map.ContainsKey(game.DataKey) && !string.IsNullOrWhiteSpace(game.DisplayName))
            {
                map[game.DataKey] = game.DisplayName!;
            }
        }

        return map;
    }

    private static List<ReportSession> FlattenSessions(
        IReadOnlyList<GamePlaytimeRecord> records,
        Dictionary<string, string> displayNames)
    {
        var sessions = new List<ReportSession>();
        foreach (var record in records)
        {
            foreach (var session in record.Sessions)
            {
                sessions.Add(new ReportSession(
                    record.GameName,
                    displayNames.TryGetValue(record.GameName, out var displayName) ? displayName : record.GameName,
                    ToLocalTime(session.StartTime),
                    ToLocalTime(session.EndTime),
                    session.DurationMinutes));
            }
        }

        return sessions;
    }

    private static string BuildReportMarkdown(List<ReportSession> sessions, DateTime generatedAtLocal)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 游戏游玩统计");
        builder.AppendLine();

        if (sessions.Count == 0)
        {
            builder.AppendLine("_暂无游玩数据。_");
            return builder.ToString();
        }

        var firstDay = sessions.Min(session => session.StartTime.Date);
        var lastDay = sessions.Max(session => session.EndTime.Date);
        var today = generatedAtLocal.Date;
        var windowStart = today.AddDays(-6);
        // 本周按周一为一周起点；周日的偏移量为 6 天。
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));

        var totalMinutes = sessions.Sum(session => session.DurationMinutes);
        var weekMinutes = sessions
            .Where(session => session.EndTime.Date >= windowStart && session.EndTime.Date <= today)
            .Sum(session => session.DurationMinutes);
        var thisWeekMinutes = sessions
            .Where(session => session.EndTime.Date >= weekStart && session.EndTime.Date <= today)
            .Sum(session => session.DurationMinutes);
        var monthMinutes = sessions
            .Where(session => session.EndTime.Year == today.Year && session.EndTime.Month == today.Month)
            .Sum(session => session.DurationMinutes);

        builder.AppendLine($"> 由 GameHelper 自动生成 · 数据范围 {firstDay:yyyy-MM-dd} 至 {lastDay:yyyy-MM-dd} · 共 {sessions.Count} 次会话");
        builder.AppendLine();
        builder.AppendLine("## 总览");
        builder.AppendLine();
        builder.AppendLine("| 指标 | 数值 |");
        builder.AppendLine("| --- | ---: |");
        builder.AppendLine($"| 游戏数量 | {CountGames(sessions)} |");
        builder.AppendLine($"| 会话总数 | {sessions.Count} |");
        builder.AppendLine($"| 累计时长 | {FormatDuration(totalMinutes)} |");
        builder.AppendLine($"| 最近 7 天 | {FormatDuration(weekMinutes)} |");
        builder.AppendLine($"| 本周（周一起） | {FormatDuration(thisWeekMinutes)} |");
        builder.AppendLine($"| 本月（{today:yyyy-MM}） | {FormatDuration(monthMinutes)} |");

        AppendGameTable(builder, sessions);
        AppendDailyTrendTable(builder, sessions, today, windowStart);

        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine("_本文件由 GameHelper 自动生成，请勿手动编辑。_");
        return builder.ToString();
    }

    private static void AppendGameTable(StringBuilder builder, List<ReportSession> sessions)
    {
        builder.AppendLine();
        builder.AppendLine("## 各游戏时长");
        builder.AppendLine();
        builder.AppendLine("| 游戏 | 总时长 | 会话数 | 最近游玩 |");
        builder.AppendLine("| --- | ---: | ---: | --- |");

        var games = sessions
            .GroupBy(session => session.DataKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Name = group.First().DisplayName,
                TotalMinutes = group.Sum(session => session.DurationMinutes),
                Sessions = group.Count(),
                LastPlayed = group.Max(session => session.EndTime)
            })
            .OrderByDescending(game => game.TotalMinutes)
            .ThenBy(game => game.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var game in games)
        {
            builder.AppendLine(
                $"| {EscapeMarkdown(game.Name)} | {FormatDuration(game.TotalMinutes)} | {game.Sessions} | {game.LastPlayed:yyyy-MM-dd} |");
        }
    }

    private static void AppendDailyTrendTable(
        StringBuilder builder,
        List<ReportSession> sessions,
        DateTime today,
        DateTime windowStart)
    {
        builder.AppendLine();
        builder.AppendLine("## 最近 7 天");
        builder.AppendLine();
        builder.AppendLine("| 日期 | 星期 | 时长 |");
        builder.AppendLine("| --- | --- | ---: |");

        for (var day = windowStart; day <= today; day = day.AddDays(1))
        {
            var current = day;
            var minutes = sessions
                .Where(session => session.EndTime.Date == current)
                .Sum(session => session.DurationMinutes);
            builder.AppendLine($"| {day:yyyy-MM-dd} | {FormatWeekday(day)} | {FormatDuration(minutes)} |");
        }
    }

    private static string BuildDailyCsv(List<ReportSession> sessions)
    {
        var builder = new StringBuilder();
        builder.AppendLine("date,game,minutes");

        var rows = sessions
            .GroupBy(session => session.EndTime.Date)
            .OrderBy(group => group.Key)
            .SelectMany(day => day
                .GroupBy(session => session.DataKey, StringComparer.OrdinalIgnoreCase)
                .Select(byGame => new
                {
                    Date = day.Key,
                    Game = byGame.Key,
                    Minutes = byGame.Sum(session => session.DurationMinutes)
                }))
            .OrderBy(row => row.Date)
            .ThenBy(row => row.Game, StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            builder.Append(row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(EscapeCsv(row.Game));
            builder.Append(',');
            builder.AppendLine(row.Minutes.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string BuildRawCsv(List<ReportSession> sessions)
    {
        var builder = new StringBuilder();
        builder.AppendLine("game,start_time,end_time,duration_minutes");

        foreach (var session in sessions.OrderBy(item => item.StartTime))
        {
            builder.Append(EscapeCsv(session.DataKey));
            builder.Append(',');
            builder.Append(session.StartTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(session.EndTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.AppendLine(session.DurationMinutes.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string ComputeContentHash(IReadOnlyList<StatsUploadFile> files)
    {
        using var sha = SHA256.Create();
        foreach (var file in files.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var pathBytes = Encoding.UTF8.GetBytes(file.RelativePath + "\n");
            sha.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);
            var contentBytes = Encoding.UTF8.GetBytes(file.Content);
            sha.TransformBlock(contentBytes, 0, contentBytes.Length, null, 0);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
    }

    private static int CountGames(List<ReportSession> sessions) =>
        sessions.Select(session => session.DataKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    private static string FormatDuration(long minutes)
    {
        if (minutes < 60)
        {
            return $"{minutes} 分钟";
        }

        return $"{(minutes / 60.0).ToString("0.#", CultureInfo.InvariantCulture)} 小时";
    }

    private static string FormatWeekday(DateTime date) => $"周{"日一二三四五六"[(int)date.DayOfWeek]}";

    private static string EscapeMarkdown(string value) => value?.Replace("|", "\\|") ?? string.Empty;

    private static string EscapeCsv(string field)
    {
        if (string.IsNullOrEmpty(field) || field.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return field;
        }

        return $"\"{field.Replace("\"", "\"\"")}\"";
    }

    private static DateTime ToLocalTime(DateTime timestamp) =>
        timestamp.Kind == DateTimeKind.Utc ? timestamp.ToLocalTime() : timestamp;

    private sealed record ReportSession(
        string DataKey,
        string DisplayName,
        DateTime StartTime,
        DateTime EndTime,
        long DurationMinutes);
}
