using GameHelper.Core.Models;
namespace GameHelper.Core.Abstractions;

/// <summary>Completed-session reads and optimistic, atomic rating edits; never changes playtime.</summary>
public interface ISessionRatingStore
{
    RatedSession Get(Guid sessionId);
    IReadOnlyList<SessionRatingPoint> Timeline(string dataKey);
    SessionReviewPage Read(SessionReviewQuery query);
    RatedSession Save(SaveSessionRating request);
}

/// <summary>Best-effort invitation, with only a completed session identity as payload.</summary>
public interface ISessionRatingNotifier { void Invite(Guid sessionId); }
