namespace GameHelper.Core.Models;

/// <summary>Current local assessment; revision zero means no previous edit.</summary>
public sealed record SessionRating(int Stars, string? Thought, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, long Revision);

/// <summary>A completed identity with its current rating and deletion-aware edit revision.</summary>
public sealed record RatedSession(Guid SessionId, string DataKey, DateTime Start, DateTime End, long Minutes,
    SessionRating? Rating, long Revision);

/// <summary>Bounded, newest-first session page; BeforeSequence is an exclusive stable cursor.</summary>
public sealed record SessionReviewQuery(string? DataKey = null, long? BeforeSequence = null, int Limit = 30, bool RatedOnly = false);
public sealed record SessionReviewPage(IReadOnlyList<RatedSession> Sessions, long? NextSequence);
public sealed record SaveSessionRating(Guid SessionId, int? Stars, string? Thought, long ExpectedRevision);
public sealed record RatingSummary(double? Average, int Samples, RatedSession? Highest);

/// <summary>Small local chart payload; private thought text is fetched only in bounded session pages.</summary>
public sealed record SessionRatingPoint(Guid SessionId, DateTime End, int Stars);
