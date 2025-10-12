using System;

namespace GameHelper.Core.Models
{
    /// <summary>
    /// Represents a single gameplay session tracked by <see cref="IPlayTimeService"/> implementations.
    /// </summary>
    public sealed record PlaySession(
        string GameName,
        DateTime StartTime,
        DateTime EndTime,
        TimeSpan Duration,
        long DurationMinutes);
}
