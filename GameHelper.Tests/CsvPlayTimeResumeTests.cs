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
        var laterStart = new DateTime(2026, 10, 17, 23, 0, 0);

        _service.ResumeTracking("witcher3", laterStart);
        var stopped = _service.StopTracking("witcher3");

        Assert.NotNull(stopped);
        Assert.True(stopped!.StartTime < laterStart);
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
    public void WriteRecoveredSession_ClampsNegativeDurationToZero()
    {
        _service.WriteRecoveredSession("witcher3", new DateTime(2026, 10, 17, 10, 0, 0), new DateTime(2026, 10, 17, 9, 0, 0));

        Assert.Equal(1, DataRowCount);
    }
}