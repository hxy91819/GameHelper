using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Core.Services;

/// <summary>Instance-owned, revision-bound history overview; live library state never enters this cache.</summary>
internal sealed class LibraryHistorySummary(IPlaytimeSnapshotProvider history, TimeProvider clock)
{
    internal sealed record Item(long TotalMinutes, DateTimeOffset? LastPlayedAt);
    private readonly object _gate = new();
    private PlaytimeHistoryVersion? _version;
    private IReadOnlyDictionary<string, Item>? _cached;
    private DateTimeOffset _readAt, _visibleAt;

    internal IReadOnlyDictionary<string, Item>? Read()
    {
        lock (_gate)
        {
            try
            {
                var version = history.GetHistoryVersion();
                var now = clock.GetUtcNow();
                if (version is not null && version == _version && _cached is not null && now >= _readAt && now < _visibleAt)
                    return _cached;
                var snapshot = history.GetSnapshot();
                if (snapshot.ReadFailed) { Invalidate(); return null; }
                var result = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
                var visibleAt = DateTimeOffset.MaxValue;
                foreach (var record in snapshot.Records)
                {
                    var item = result.GetValueOrDefault(record.GameName) ?? new Item(0, null);
                    var total = item.TotalMinutes;
                    var last = item.LastPlayedAt;
                    foreach (var session in record.Sessions)
                    {
                        total = checked(total + session.DurationMinutes);
                        // Preserve raw ordering/validity checks and the existing timezone interpretation.
                        if (session.EndTime < session.StartTime || session.DurationMinutes < 0) continue;
                        var end = Offset(session.EndTime, clock.LocalTimeZone);
                        if (end > now) { if (end < visibleAt) visibleAt = end; }
                        else if (last is null || end > last) last = end;
                    }
                    result[record.GameName] = new(total, last);
                }
                // Metadata may have advanced between its read and the history transaction.
                // Cache only the identity actually materialized by that transaction.
                _version = version is null ? null : snapshot.HistoryVersion;
                _cached = result;
                _readAt = now;
                _visibleAt = visibleAt;
                return result;
            }
            catch
            {
                Invalidate();
                return null; // An unreadable history is neither a valid empty result nor a usable old overview.
            }
        }
    }

    private void Invalidate() { _version = null; _cached = null; }

    private static DateTimeOffset Offset(DateTime value, TimeZoneInfo zone) => value.Kind == DateTimeKind.Utc
        ? new DateTimeOffset(value) : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), zone.GetUtcOffset(value));
}
