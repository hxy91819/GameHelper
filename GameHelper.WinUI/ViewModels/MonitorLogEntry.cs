namespace GameHelper.WinUI.ViewModels;

public sealed class MonitorLogEntry
{
    public MonitorLogEntry(DateTimeOffset timestamp, string level, string message)
    {
        Timestamp = timestamp;
        Level = level;
        Message = message;
    }

    public DateTimeOffset Timestamp { get; }

    public string Level { get; }

    public string Message { get; }

    public string TimestampText => Timestamp.ToString("HH:mm:ss");
}
