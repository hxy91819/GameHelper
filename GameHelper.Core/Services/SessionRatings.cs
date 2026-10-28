using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
namespace GameHelper.Core.Services;

/// <summary>Rating rules shared by IPC and local callers.</summary>
public sealed class SessionRatings(ISessionRatingStore store, TimeProvider? timeProvider = null)
{
    public const int ThoughtLimit = 2000;
    public IReadOnlyList<SessionRatingPoint> Timeline(string dataKey)
    {
        if (string.IsNullOrWhiteSpace(dataKey)) throw new ArgumentException("缺少游戏身份。");
        var clock = timeProvider ?? TimeProvider.System;
        return store.Timeline(dataKey).OrderBy(s => s.End.Kind == DateTimeKind.Utc ? TimeZoneInfo.ConvertTimeFromUtc(s.End, clock.LocalTimeZone) : s.End)
            .ThenBy(s => s.SessionId).ToList();
    }
    public RatedSession Get(Guid id) => store.Get(id);
    public SessionReviewPage Read(SessionReviewQuery query)
    {
        if (query.Limit is < 1 or > 100 || query.BeforeSequence is <= 0) throw new ArgumentException("会话分页参数无效。");
        return store.Read(query);
    }
    public RatedSession Save(SaveSessionRating request)
    {
        if (request.SessionId == Guid.Empty || request.ExpectedRevision < 0) throw new ArgumentException("评分身份或版本无效。");
        if (request.Stars is < 1 or > 5) throw new ArgumentException("请选择 1–5 星。");
        if (request.Thought?.Length > ThoughtLimit) throw new ArgumentException($"想法最多 {ThoughtLimit} 个字符。");
        return store.Save(request with { Thought = request.Stars is null || string.IsNullOrWhiteSpace(request.Thought) ? null : request.Thought });
    }
    public static RatingSummary Summarize(IEnumerable<RatedSession> sessions, ReportPeriod period, TimeProvider clock)
    {
        DateTime Local(DateTime date) => date.Kind == DateTimeKind.Utc ? TimeZoneInfo.ConvertTimeFromUtc(date, clock.LocalTimeZone) : date;
        var rated = sessions.Where(s => s.Rating is not null && Local(s.End) >= Local(s.Start) && Local(s.End) <= clock.GetLocalNow().DateTime
            && DateOnly.FromDateTime(Local(s.End)) >= period.Start && DateOnly.FromDateTime(Local(s.End)) < period.EndExclusive).ToList();
        return new(rated.Count == 0 ? null : rated.Average(s => s.Rating!.Stars), rated.Count,
            rated.OrderByDescending(s => s.Rating!.Stars).ThenBy(s => Local(s.End)).ThenBy(s => s.SessionId).FirstOrDefault());
    }
    public static bool ShouldInvite(PlaySession session) => session.DurationMinutes >= 10;
}
