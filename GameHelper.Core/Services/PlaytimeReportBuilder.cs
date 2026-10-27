using GameHelper.Core.Models;

namespace GameHelper.Core.Services;

/// <summary>Pure aggregation of completed sessions, assigned wholly to their local end date.</summary>
public static class PlaytimeReportBuilder
{
    public static PlaytimeReport Build(IReadOnlyList<GamePlaytimeRecord> records, IEnumerable<GameConfig> configs,
        ReportPeriod period, TimeProvider timeProvider)
    {
        var start = period.Start;
        var end = period.EndExclusive;
        var previousStart = start.AddMonths(period.Quarter is null ? -12 : -3);
        var now = timeProvider.GetLocalNow().DateTime;
        DateTime Local(DateTime value) => value.Kind == DateTimeKind.Utc
            ? TimeZoneInfo.ConvertTimeFromUtc(value, timeProvider.LocalTimeZone) : value;
        var games = configs.GroupBy(game => game.DataKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var sessions = records.SelectMany(record => record.Sessions.Select(session => new DashboardSession(
                record.GameName, games.GetValueOrDefault(record.GameName)?.DisplayName ?? record.GameName,
                Local(session.StartTime), Local(session.EndTime), session.DurationMinutes)))
            .Where(session => session.Minutes >= 0 && session.End >= session.Start && session.End <= now).ToList();
        static DateOnly Day(DashboardSession session) => DateOnly.FromDateTime(session.End);
        var current = sessions.Where(session => Day(session) >= start && Day(session) < end).ToList();
        var previous = sessions.Where(session => Day(session) >= previousStart && Day(session) < start).ToList();
        var top = current.GroupBy(session => session.DataKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DashboardGame(group.Key, group.First().Name, group.Sum(session => session.Minutes),
                group.Count(), group.Max(session => session.End)))
            .OrderByDescending(game => game.Minutes).ThenBy(game => game.DataKey, StringComparer.OrdinalIgnoreCase).ToList();
        var hours = new long[24];
        foreach (var session in current) hours[session.Start.Hour] += session.Minutes;
        var longest = current.OrderByDescending(session => session.Minutes).ThenBy(session => session.End)
            .ThenBy(session => session.DataKey, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        var daily = current.GroupBy(Day).ToDictionary(group => group.Key, group => group.Sum(session => session.Minutes));
        var months = new List<DashboardBucket>();
        for (var date = start; date < end; date = date.AddMonths(1))
            months.Add(Bucket(date.ToString("yyyy-MM"), date, date.AddMonths(1).AddDays(-1)));
        var weeks = new List<DashboardBucket>();
        for (var date = start; date < end;)
        {
            var nextMonday = date.AddDays(7 - ((int)date.DayOfWeek + 6) % 7);
            var to = nextMonday < end ? nextMonday : end;
            weeks.Add(Bucket(date.ToString("MM-dd"), date, to.AddDays(-1)));
            date = to;
        }
        return new PlaytimeReport
        {
            Period = period, IsPartial = DateOnly.FromDateTime(now) < end,
            Minutes = current.Sum(session => session.Minutes), Sessions = current.Count,
            ActiveDays = daily.Count, GamesPlayed = top.Count,
            PreviousMinutes = previous.Sum(session => session.Minutes),
            PreviousActiveDays = previous.Select(Day).Distinct().Count(),
            PreviousGamesPlayed = previous.Select(session => session.DataKey).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            TopGames = top.Take(5).ToList(), Months = months, Weeks = weeks, HourMinutes = hours,
            MostPlayedHour = hours.Max() == 0 ? null : Array.IndexOf(hours, hours.Max()),
            LongestSession = longest is null ? null : new(longest.DataKey, longest.Name, longest.Minutes, 1, longest.End),
            NewGameKeys = games.Values.Where(game => game.AddedAt is { } added &&
                    DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(added, timeProvider.LocalTimeZone).DateTime) >= start &&
                    DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(added, timeProvider.LocalTimeZone).DateTime) < end &&
                    added <= timeProvider.GetUtcNow())
                .Select(game => game.DataKey).Order(StringComparer.OrdinalIgnoreCase).ToList()
        };
        DashboardBucket Bucket(string label, DateOnly from, DateOnly to) =>
            new(label, from, to, daily.Where(pair => pair.Key >= from && pair.Key <= to).Sum(pair => pair.Value));
    }
}
