using System;
using System.Collections.Generic;

namespace GameHelper.ConsoleHost.Interactive
{
    /// <summary>
    /// Represents a point-in-time snapshot of playtime session data.
    /// </summary>
    internal sealed record SessionSnapshot(HashSet<SessionKey> Keys, List<SessionRecord> Records, string Source);

    /// <summary>
    /// Represents a single play session (immutable key, mutable display name).
    /// </summary>
    internal sealed class SessionRecord
    {
        public SessionRecord(string gameName, string displayName, DateTime start, DateTime end, long minutes)
        {
            Key = new SessionKey(gameName, start, end, minutes);
            DisplayName = displayName;
        }

        public SessionKey Key { get; }

        public string DisplayName { get; }

        public DateTime Start => Key.Start;

        public DateTime End => Key.End;

        public long DurationMinutes => Key.DurationMinutes;
    }

    /// <summary>
    /// Uniquely identifies a play session by game name, start time, end time, and duration.
    /// </summary>
    internal readonly record struct SessionKey(string Game, DateTime Start, DateTime End, long DurationMinutes);
}
