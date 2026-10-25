using GameHelper.Core.Models;

namespace GameHelper.Core.Services;

/// <summary>Groups persisted minutes by local session end date, matching the existing history preview.</summary>
public static class PlaytimeDashboardBuilder
{
    public static PlaytimeDashboard Build(IReadOnlyList<GamePlaytimeRecord> records, IEnumerable<GameConfig> configs,
        PlaytimeDashboardQuery query, TimeProvider? timeProvider = null)
    {
        if (query.Days is not (0 or 7 or 30 or 90)) throw new ArgumentException("统计范围应为 7、30、90 天或全部。");
        var clock = timeProvider ?? TimeProvider.System;
        var now = clock.GetLocalNow().DateTime;
        var today = DateOnly.FromDateTime(now);
        DateTime Local(DateTime value) => value.Kind == DateTimeKind.Utc ? TimeZoneInfo.ConvertTimeFromUtc(value, clock.LocalTimeZone) : value;
        var configIndex = StatisticsConfigIndex.Build(configs.GroupBy(game => game.DataKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase));
        var history = records.SelectMany(record => record.Sessions.Select(session => new DashboardSession(
            record.GameName, configIndex.ResolveDisplayName(record.GameName), Local(session.StartTime), Local(session.EndTime), session.DurationMinutes)))
            .Where(session => session.Minutes >= 0 && session.End >= session.Start && session.End <= now).ToList();
        var start = query.Days == 0 ? history.Select(session => DateOnly.FromDateTime(session.End)).DefaultIfEmpty(today).Min() : today.AddDays(1 - query.Days);
        var period = history.Where(session => DateOnly.FromDateTime(session.End) >= start).ToList();
        var previousStart = query.Days == 0 ? start : start.AddDays(-query.Days);
        var previous = query.Days == 0 ? 0 : history.Where(session => DateOnly.FromDateTime(session.End) >= previousStart
            && DateOnly.FromDateTime(session.End) < start).Sum(session => session.Minutes);
        var daily = history.GroupBy(session => DateOnly.FromDateTime(session.End))
            .ToDictionary(group => group.Key, group => new DashboardDay(group.Key, group.Sum(session => session.Minutes), group.Count()));
        var chart = new List<DashboardBucket>();
        if (query.Days == 0)
        {
            var month = new DateOnly(today.Year, today.Month, 1).AddMonths(-11);
            for (var index = 0; index < 12; index++)
            {
                var from = month.AddMonths(index);
                var to = from.AddMonths(1).AddDays(-1);
                chart.Add(new DashboardBucket(from.ToString("yy/MM"), from, to, daily.Where(pair => pair.Key >= from && pair.Key <= to).Sum(pair => pair.Value.Minutes)));
            }
        }
        else
            for (var date = start; date <= today; date = date.AddDays(1))
                chart.Add(new DashboardBucket(date.ToString("MM/dd"), date, date, daily.GetValueOrDefault(date)?.Minutes ?? 0));
        var calendarStart = today.AddDays(-364);
        calendarStart = calendarStart.AddDays(-(((int)calendarStart.DayOfWeek + 6) % 7));
        var calendar = new List<DashboardDay>();
        for (var date = calendarStart; date <= today; date = date.AddDays(1))
            calendar.Add(daily.GetValueOrDefault(date) ?? new DashboardDay(date, 0, 0));
        var games = period.GroupBy(session => session.DataKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DashboardGame(group.Key, group.First().Name, group.Sum(session => session.Minutes), group.Count(), group.Max(session => session.End)))
            .OrderByDescending(game => game.Minutes).ThenBy(game => game.Name, StringComparer.OrdinalIgnoreCase).ToList();
        // A calendar date is global and may precede the selected summary period.
        var focus = (query.FocusDate is null ? period : history).Where(session => query.SessionGameKey is null || session.DataKey.Equals(query.SessionGameKey, StringComparison.OrdinalIgnoreCase))
            .Where(session => query.FocusDate is null || DateOnly.FromDateTime(session.End) == query.FocusDate).ToList();
        var weekdays = new long[7];
        foreach (var session in focus) weekdays[((int)session.End.DayOfWeek + 6) % 7] += session.Minutes;
        var rhythm = new DashboardRhythm(focus.Sum(session => session.Minutes), focus.Count,
            focus.Select(session => session.End.Date).Distinct().Count(), focus.Select(session => session.Minutes).DefaultIfEmpty(0).Max(), weekdays);
        var recent = query.IncludeRecent ? focus.OrderByDescending(session => session.End).ThenByDescending(session => session.Start)
            .ThenBy(session => session.DataKey, StringComparer.OrdinalIgnoreCase).ToList() : [];
        const int pageSize = 30;
        var offset = recent.Count == 0 ? 0 : Math.Clamp(query.Offset, 0, ((recent.Count - 1) / pageSize) * pageSize);
        return new PlaytimeDashboard
        {
            Rhythm = rhythm, Today = today, Start = start, Days = query.Days, Minutes = period.Sum(session => session.Minutes), PreviousMinutes = previous,
            Sessions = period.Count, ActiveDays = period.Select(session => session.End.Date).Distinct().Count(), GamesPlayed = games.Count,
            LongestSessionMinutes = period.Select(session => session.Minutes).DefaultIfEmpty(0).Max(),
            Trend = chart, Calendar = calendar, Games = games.Take(1000).ToList(),
            Recent = recent.Skip(offset).Take(pageSize).ToList(), RecentTotal = recent.Count, Offset = offset, PageSize = pageSize
        };
    }
}
