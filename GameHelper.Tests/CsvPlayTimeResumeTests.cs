using System;
using System.IO;
using System.Linq;
using GameHelper.Infrastructure.Providers;
using Xunit;

namespace GameHelper.Tests;

public class CsvPlayTimeResumeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CsvBackedPlayTimeService _service;

    public CsvPlayTimeResumeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GameHelper.Tests." + Guid.NewGuid().ToString("N"));
        _service = new CsvBackedPlayTimeService(_tempDir);
    }

    public void Dispose()
    {
        Directory.Delete(_tempDir, recursive: true);
    }

    private string CsvPath => Path.Combine(_tempDir, "playtime.csv");

    private int DataRowCount =>
        File.Exists(CsvPath) ? File.ReadAllLines(CsvPath).Count(line => !string.IsNullOrWhiteSpace(line)) - 1 : 0;

    [Fact]
    public void ResumeTracking_FlushesOneContinuousRowFromOriginalStart()
    {
        var originalStart = new DateTime(2026, 10, 17, 9, 0, 0);

        _service.ResumeTracking("witcher3", originalStart);
        var stopped = _service.StopTracking("witcher3");

        Assert.NotNull(stopped);
        Assert.Equal(originalStart, stopped!.StartTime);
        Assert.Equal(1, DataRowCount);
    }

    [Fact]
    public void ResumeTracking_WhenAlreadyStarted_KeepsExistingStart()
    {
        _service.StartTracking("witcher3");
        var farFutureStart = DateTime.Now + TimeSpan.FromMinutes(2);

        _service.ResumeTracking("witcher3", farFutureStart);
        var stopped = _service.StopTracking("witcher3");

        Assert.NotNull(stopped);
        Assert.True(stopped!.StartTime < farFutureStart);
    }

    [Fact]
    public void WriteRecoveredSession_AppendsRowWithoutTouchingLiveState()
    {
        _service.WriteRecoveredSession("witcher3", new DateTime(2026, 10, 17, 9, 0, 0), new DateTime(2026, 10, 17, 10, 0, 0));

        Assert.Equal(1, DataRowCount);

        // Recovered rows must not create an active session for the same game.
        Assert.Null(_service.StopTracking("witcher3"));
    }

    [Fact]
    public void StopTracking_LockedHistory_ReportsFailureSoAutomationCanRetainSnapshot()
    {
        var start = DateTime.Now.AddMinutes(-2);
        _service.ResumeTracking("game", start);
        File.WriteAllText(CsvPath, "game,start_time,end_time,duration_minutes\n");
        using (var locked = new FileStream(CsvPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => _service.StopTracking("game"));
        _service.WriteRecoveredSession("game", start, DateTime.Now);
        Assert.Equal(1, DataRowCount);
    }

    [Fact]
    public void WriteRecoveredSession_ReplayedSnapshotAcrossServiceRestarts_DoesNotDuplicateRow()
    {
        var start = new DateTime(2026, 10, 17, 9, 0, 0).AddTicks(12345);
        var end = start.AddMinutes(20);
        _service.WriteRecoveredSession("witcher3", start, end);
        var restarted = new CsvBackedPlayTimeService(_tempDir);
        restarted.WriteRecoveredSession("WITCHER3", start, end);
        Assert.Equal(1, DataRowCount);
    }

    [Fact]
    public void WriteRecoveredSession_ClampsNegativeDurationToZero()
    {
        _service.WriteRecoveredSession("witcher3", new DateTime(2026, 10, 17, 10, 0, 0), new DateTime(2026, 10, 17, 9, 0, 0));

        Assert.Equal(1, DataRowCount);
    }
}
