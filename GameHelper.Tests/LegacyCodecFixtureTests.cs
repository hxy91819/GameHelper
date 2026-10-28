using System.Text;
using GameHelper.Core.Services;
using GameHelper.Infrastructure.Providers;

namespace GameHelper.Tests;

public sealed class LegacyCodecFixtureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GameHelper.LegacyCodec." + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Csv_OldCodec_StillMatchesFrozenBytesAndReports()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Path.Combine(_directory, "playtime.csv"), LegacyFixtures.Read("csv.input.bin"));
        var records = new FilePlaytimeSnapshotProvider(_directory).GetSnapshot().Records;
        Assert.Equal(6, records.Sum(game => game.Sessions.Count));
        var output = Path.Combine(_directory, "output.csv");
        PlaytimeCsvCodec.WriteAll(output, records.SelectMany(game => game.Sessions).Select(session =>
            new PlaytimeCsvRow(session.GameName, session.StartTime, session.EndTime, session.DurationMinutes)));
        Assert.Equal(LegacyFixtures.Platform("csv.expected"), File.ReadAllBytes(output));
        var report = new StatsReportBuilder().Build(records, [], new(2025, 4, 1, 12, 0, 0, DateTimeKind.Unspecified), true);
        foreach (var file in report.Files)
            Assert.Equal(LegacyFixtures.Platform(file.RelativePath.Replace('/', '_')), Encoding.UTF8.GetBytes(file.Content));
    }

    [Fact]
    public void LegacyJson_OldMigration_StillMatchesFrozenProjection()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Path.Combine(_directory, "playtime.json"), LegacyFixtures.Read("legacy.input.bin"));
        _ = new CsvBackedPlayTimeService(_directory);
        Assert.Equal(LegacyFixtures.Platform("legacy.expected"), File.ReadAllBytes(Path.Combine(_directory, "playtime.csv")));
    }

    [Fact]
    public void InFlight_OldStore_StillMatchesFrozenPlatformOutput()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "inflight-sessions.json");
        File.WriteAllBytes(path, LegacyFixtures.Read("inflight.input.bin"));
        var store = new JsonInFlightSessionStore(path);
        var snapshot = store.Load();
        Assert.NotNull(snapshot);
        store.Save(snapshot!);
        Assert.Equal(LegacyFixtures.Platform("inflight.expected"), File.ReadAllBytes(path));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}

internal static class LegacyFixtures
{
    internal static byte[] Platform(string prefix) => Read(prefix + (OperatingSystem.IsWindows() ? ".windows.bin" : ".unix.bin"));
    internal static byte[] Read(string name)
    {
        using var stream = typeof(LegacyFixtures).Assembly.GetManifestResourceStream("GameHelper.Tests.Resources.LegacyUserData." + name)!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}
