namespace GameHelper.Core.Models;

public sealed class SessionActivityRecord
{
    public SessionActivityRecord(string gameName, string displayName, DateTime start, DateTime end, long minutes)
    {
        Key = new SessionActivityKey(gameName, start, end, minutes);
        DisplayName = displayName;
    }

    public SessionActivityKey Key { get; }

    public string DisplayName { get; }

    public DateTime Start => Key.Start;

    public DateTime End => Key.End;

    public long DurationMinutes => Key.DurationMinutes;
}
