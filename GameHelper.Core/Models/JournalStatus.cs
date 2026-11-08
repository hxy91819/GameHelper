namespace GameHelper.Core.Models;

/// <summary>
/// Private-journal tags. A record stores <see cref="NotStarted"/> or a canonical comma-joined
/// subset of <see cref="Tags"/>; the separator and order match the SQLite CHECK in migration 005.
/// </summary>
public static class JournalStatus
{
    public const string NotStarted = "未开始";
    public const string Playing = "在玩";
    public const string Completed = "已通关";
    public const string AllAchievements = "全成就";
    public const string Shelved = "搁置";
    public const char Separator = ',';

    /// <summary>Selectable tags in canonical order; <see cref="NotStarted"/> is the "no tag" default.</summary>
    public static readonly IReadOnlyList<string> Tags = [Playing, Completed, AllAchievements, Shelved];
    public static readonly IReadOnlyList<string> All = [NotStarted, .. Tags];

    /// <summary>Validates a stored value and rewrites it in canonical order; unknown or mixed "未开始" values fail.</summary>
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = NotStarted;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value == NotStarted) return true;
        var tokens = value.Split(Separator, StringSplitOptions.TrimEntries);
        if (tokens.Any(token => token == NotStarted || !Tags.Contains(token, StringComparer.Ordinal))) return false;
        normalized = string.Join(Separator, Tags.Where(tag => tokens.Contains(tag, StringComparer.Ordinal)));
        return true;
    }

    public static string Join(IEnumerable<string>? tags)
    {
        var selected = Tags.Where(tag => tags?.Contains(tag, StringComparer.Ordinal) == true).ToArray();
        return selected.Length == 0 ? NotStarted : string.Join(Separator, selected);
    }

    public static bool Has(string? status, string tag) =>
        status is not null && (status == tag || status.Split(Separator).Contains(tag, StringComparer.Ordinal));

    public static bool IsCompleted(string? status) => Has(status, Completed) || Has(status, AllAchievements);

    /// <summary>Display form of a stored status; unknown values stay visible instead of being hidden.</summary>
    public static string Describe(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return NotStarted;
        if (!TryNormalize(status, out var normalized)) return status;
        return normalized == NotStarted ? NotStarted : string.Join(" · ", normalized.Split(Separator));
    }
}
