using Microsoft.Extensions.Logging;

namespace GameHelper.WinUI.Services;

public sealed class UiLoggerProvider : ILoggerProvider
{
    private readonly UiLogSink _sink;

    public UiLoggerProvider(UiLogSink sink)
    {
        _sink = sink;
    }

    public ILogger CreateLogger(string categoryName) => new UiLogger(_sink, categoryName);

    public void Dispose()
    {
    }

    private sealed class UiLogger : ILogger
    {
        private readonly UiLogSink _sink;
        private readonly string _category;

        public UiLogger(UiLogSink sink, string category)
        {
            _sink = sink;
            _category = category;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            _sink.Write(logLevel, _category, message, exception);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
