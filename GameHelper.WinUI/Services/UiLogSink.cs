using Microsoft.Extensions.Logging;

namespace GameHelper.WinUI.Services;

public sealed class UiLogSink
{
    public static UiLogSink Instance { get; } = new();

    public event Action<UiLogEntry>? LogReceived;

    public void Write(LogLevel level, string category, string message, Exception? exception)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (exception != null)
        {
            message = $"{message} | {exception.GetType().Name}: {exception.Message}";
        }

        LogReceived?.Invoke(new UiLogEntry(DateTimeOffset.Now, level, category, message));
    }
}

public sealed record UiLogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message);
