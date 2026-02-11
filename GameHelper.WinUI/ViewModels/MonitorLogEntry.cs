using Microsoft.Extensions.Logging;

namespace GameHelper.WinUI.ViewModels;

public sealed class MonitorLogEntry
{
    public MonitorLogEntry(DateTimeOffset timestamp, LogLevel level, string category, string message)
    {
        Timestamp = timestamp;
        Level = level;
        Category = category;
        Message = message;
    }

    public DateTimeOffset Timestamp { get; }

    public LogLevel Level { get; }

    public string Category { get; }

    public string Message { get; }

    public string TimestampText => Timestamp.ToString("HH:mm:ss");

    public string LevelText => Level.ToString().ToUpperInvariant();
}
