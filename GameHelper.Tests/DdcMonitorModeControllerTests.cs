using System.Collections.Concurrent;
using GameHelper.Infrastructure.Controllers;
using Microsoft.Extensions.Logging;

namespace GameHelper.Tests;

public sealed class DdcMonitorModeControllerTests
{
    [WindowsOnlyFact]
    public void IsAvailable_StableOnlineMonitor_DoesNotRepeatRecoveryLog()
    {
        var logger = new RecordingLogger();
        var tick = 0;
        var probes = 0;
        var controller = new DdcMonitorModeController(logger,
            () => { probes++; return (true, "test monitor"); }, () => tick);

        for (var poll = 0; poll < 4; poll++)
        {
            tick += 10_000;
            Assert.True(controller.IsAvailable);
            Assert.True(controller.IsAvailable); // A second caller shares the cooldown.
        }

        Assert.Equal(5, probes); // Probing must continue even while the monitor stays online.
        Assert.Single(logger.Messages.Where(m => m.Text.Contains("controller ready")));
        Assert.DoesNotContain(logger.Messages, m => m.Text.Contains("became available"));
    }

    [WindowsOnlyFact]
    public void IsAvailable_InitiallyOffline_LogsOnlyActualRecovery()
    {
        var logger = new RecordingLogger();
        var tick = 0;
        var online = false;
        var controller = new DdcMonitorModeController(logger, () => (online, "test monitor"), () => tick);

        tick += 10_000;
        Assert.False(controller.IsAvailable);
        online = true;
        tick += 10_000;
        Assert.True(controller.IsAvailable);
        tick += 10_000;
        Assert.True(controller.IsAvailable);

        Assert.Single(logger.Messages.Where(m => m.Text.Contains("became available")));
        Assert.DoesNotContain(logger.Messages, m => m.Text.Contains("became unavailable"));
    }

    [WindowsOnlyFact]
    public void IsAvailable_DisconnectAndReconnect_LogsEachTransitionOnce()
    {
        var logger = new RecordingLogger();
        var tick = 0;
        var online = true;
        var controller = new DdcMonitorModeController(logger, () => (online, "test monitor"), () => tick);

        online = false;
        tick += 10_000;
        Assert.False(controller.IsAvailable);
        tick += 10_000;
        Assert.False(controller.IsAvailable);
        online = true;
        tick += 10_000;
        Assert.True(controller.IsAvailable);
        tick += 10_000;
        Assert.True(controller.IsAvailable);

        Assert.Single(logger.Messages.Where(m => m.Level == LogLevel.Warning && m.Text.Contains("became unavailable")));
        Assert.Single(logger.Messages.Where(m => m.Level == LogLevel.Information && m.Text.Contains("became available")));
    }

    private sealed class RecordingLogger : ILogger<DdcMonitorModeController>
    {
        public ConcurrentQueue<(LogLevel Level, string Text)> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue((logLevel, formatter(state, exception)));
    }
}
