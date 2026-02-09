using GameHelper.Core.Models;
using GameHelper.Infrastructure.Providers;

namespace GameHelper.Tests;

public sealed class FilePlaytimeSnapshotProviderTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _originalAppData;
    private readonly string? _originalXdg;

    public FilePlaytimeSnapshotProviderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "GameHelperTests_PlaytimeSnapshot", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        _originalAppData = Environment.GetEnvironmentVariable("APPDATA");
        _originalXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("APPDATA", _tempRoot);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);
    }

    [Fact]
    public void GetPlaytimeRecords_WithCsv_ShouldReturnRecords()
    {
        var gameDir = Path.Combine(_tempRoot, "GameHelper");
        Directory.CreateDirectory(gameDir);
        var csvPath = Path.Combine(gameDir, "playtime.csv");
        File.WriteAllText(
            csvPath,
            "game,start_time,end_time,duration_minutes\n" +
            "game.exe,2026-01-01T10:00:00,2026-01-01T10:30:00,30\n");

        var provider = new FilePlaytimeSnapshotProvider();
        var records = provider.GetPlaytimeRecords();

        Assert.Single(records);
        Assert.Equal("game.exe", records[0].GameName);
        Assert.Single(records[0].Sessions);
        Assert.Equal(30, records[0].Sessions[0].DurationMinutes);
    }

    [Fact]
    public void GetPlaytimeRecords_WithJsonFallback_ShouldReturnRecords()
    {
        var gameDir = Path.Combine(_tempRoot, "GameHelper");
        Directory.CreateDirectory(gameDir);
        var jsonPath = Path.Combine(gameDir, "playtime.json");
        File.WriteAllText(
            jsonPath,
            """
            {
              "games": [
                {
                  "gameName": "json-game.exe",
                  "sessions": [
                    {
                      "gameName": "json-game.exe",
                      "startTime": "2026-01-01T08:00:00",
                      "endTime": "2026-01-01T08:45:00",
                      "duration": "00:45:00",
                      "durationMinutes": 45
                    }
                  ]
                }
              ]
            }
            """);

        var provider = new FilePlaytimeSnapshotProvider();
        var records = provider.GetPlaytimeRecords();

        Assert.Single(records);
        Assert.Equal("json-game.exe", records[0].GameName);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("APPDATA", _originalAppData);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalXdg);

        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, true);
        }
    }
}
