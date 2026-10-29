using System.Text;
using System.Globalization;
using GameHelper.Core.Models;
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
        var utcSession = Assert.Single(records.SelectMany(game => game.Sessions).Where(session => session.StartTime.Kind == DateTimeKind.Utc));
        var report = new StatsReportBuilder().Build(records, [], new(2025, 4, 1, 12, 0, 0, DateTimeKind.Unspecified), true);
        var localStart = TimeZoneInfo.ConvertTimeFromUtc(utcSession.StartTime, TimeZoneInfo.Local);
        var localEnd = TimeZoneInfo.ConvertTimeFromUtc(utcSession.EndTime, TimeZoneInfo.Local);
        Assert.Contains(FormattableString.Invariant($"{utcSession.GameName},{localStart:yyyy-MM-ddTHH:mm:ss},{localEnd:yyyy-MM-ddTHH:mm:ss},{utcSession.DurationMinutes}"),
            Assert.Single(report.Files, file => file.RelativePath == "raw/playtime.csv").Content);

        // Frozen reports were captured in UTC. Verify actual local conversion above, then
        // compare their UTC wall-clock representation without changing the machine time zone.
        var frozenRecords = LegacyFixtures.ReportUtcWallClock(records);
        report = new StatsReportBuilder().Build(frozenRecords, [], new(2025, 4, 1, 12, 0, 0, DateTimeKind.Unspecified), true);
        foreach (var file in report.Files)
            Assert.Equal(LegacyFixtures.Platform(file.RelativePath.Replace('/', '_')), Encoding.UTF8.GetBytes(file.Content));
    }

    [Theory]
    [InlineData(0, "2025-03-30T00:30:00")]
    [InlineData(8, "2025-03-30T08:30:00")]
    public void FrozenReportUtcWallClockIsIndependentOfSourceOffset(int offsetHours, string localWallClock)
    {
        var local = DateTime.ParseExact(localWallClock, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        var utc = new DateTimeOffset(local, TimeSpan.FromHours(offsetHours)).UtcDateTime;
        var records = new[] { new GamePlaytimeRecord { GameName = "utc", Sessions = [new("utc", utc, utc.AddHours(1), TimeSpan.FromHours(1), 60)] } };
        var frozen = Assert.Single(Assert.Single(LegacyFixtures.ReportUtcWallClock(records)).Sessions);
        Assert.Equal(new DateTime(2025, 3, 30, 0, 30, 0, DateTimeKind.Unspecified), frozen.StartTime);
        Assert.Equal(DateTimeKind.Unspecified, frozen.StartTime.Kind);
        Assert.Equal(DateTimeKind.Utc, records[0].Sessions[0].StartTime.Kind);
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
    // The frozen sync reports use UTC wall-clock values. Keep their exact byte oracle
    // independent of the host zone while retaining original records for kind assertions.
    internal static IReadOnlyList<GamePlaytimeRecord> ReportUtcWallClock(IReadOnlyList<GamePlaytimeRecord> records) =>
        records.Select(game => new GamePlaytimeRecord
        {
            GameName = game.GameName,
            Sessions = game.Sessions.Select(session => session with
            {
                StartTime = UtcWallClock(session.StartTime), EndTime = UtcWallClock(session.EndTime)
            }).ToList()
        }).ToList();
    private static DateTime UtcWallClock(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? DateTime.SpecifyKind(value, DateTimeKind.Unspecified) : value;
    internal static byte[] Platform(string prefix) => Read(prefix + (OperatingSystem.IsWindows() ? ".windows.bin" : ".unix.bin"));
    internal static byte[] Read(string name)
    {
        using var stream = typeof(LegacyFixtures).Assembly.GetManifestResourceStream("GameHelper.Tests.Resources.LegacyUserData." + name)!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}
