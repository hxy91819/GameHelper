using GameHelper.Core.Models;

namespace GameHelper.Desktop;

/// <summary>Joins desktop-only journal completions into the report view; journal data never enters configuration or IPC.</summary>
internal static class ReportJournalMerge
{
    internal static bool IsCompleted(string status) => status is "已通关" or "全成就";

    internal static PlaytimeReport Apply(PlaytimeReport report, IEnumerable<KeyValuePair<string, JournalRecord>> journal,
        Func<string, string?>? displayName = null)
    {
        var start = report.Period.Start;
        var end = report.Period.EndExclusive;
        var previous = report.Period.Previous;
        var completed = new List<ReportClearedGame>();
        foreach (var (key, record) in journal)
            if (IsCompleted(record.Status) && record.CompletedOn is { } completedOn)
                completed.Add(new(key, Name(key, displayName), completedOn, record.Status, record.Note));
        return report with
        {
            ClearedGames = completed.Where(game => game.CompletedOn >= start && game.CompletedOn < end)
                .OrderBy(game => game.CompletedOn)
                .ThenBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(game => game.DataKey, StringComparer.OrdinalIgnoreCase).ToList(),
            PreviousClearedGames = completed.Count(game =>
                game.CompletedOn >= previous.Start && game.CompletedOn < previous.EndExclusive)
        };
    }

    private static string Name(string dataKey, Func<string, string?>? displayName)
    {
        var name = displayName?.Invoke(dataKey);
        return string.IsNullOrWhiteSpace(name) ? dataKey : name.Trim();
    }
}
