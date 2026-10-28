using System;

namespace GameHelper.Core.Models;

/// <summary>
/// Represents a single gameplay session tracked by <see cref="IPlayTimeService"/> implementations.
/// </summary>
public sealed record PlaySession(
    string GameName,
    DateTime StartTime,
    DateTime EndTime,
    TimeSpan Duration,
    long DurationMinutes)
{
    /// <summary>Present only for a newly committed, unrated observed completion; transient notification eligibility.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid? RatingInvitationId { get; init; }
}
