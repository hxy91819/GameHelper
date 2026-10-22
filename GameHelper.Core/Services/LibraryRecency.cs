using GameHelper.Core.Models;

namespace GameHelper.Core.Services;

internal static class LibraryRecency
{
    internal static IReadOnlyDictionary<string, DateTimeOffset> LastPlayed(IReadOnlyList<GamePlaytimeRecord> records, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        return records.SelectMany(record => record.Sessions.Select(session => (Key: record.GameName, Session: session)))
            .Where(item => item.Session.EndTime >= item.Session.StartTime && item.Session.DurationMinutes >= 0)
            .Select(item => (item.Key, End: Offset(item.Session.EndTime, clock.LocalTimeZone)))
            .Where(item => item.End <= now)
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Max(item => item.End), StringComparer.OrdinalIgnoreCase);
    }

    private static DateTimeOffset Offset(DateTime value, TimeZoneInfo zone) => value.Kind == DateTimeKind.Utc
        ? new DateTimeOffset(value) : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), zone.GetUtcOffset(value));
}
