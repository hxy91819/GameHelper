using System.Text.Json;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Infrastructure.Providers;
using Microsoft.Data.Sqlite;
using static GameHelper.Infrastructure.Storage.Sqlite.Legacy.LegacySql;

namespace GameHelper.Infrastructure.Storage.Sqlite.Legacy;

internal static class LegacyImportValidation
{
    internal static IReadOnlyList<GamePlaytimeRecord> ExpectedHistory(IEnumerable<LegacyItem> items)
    {
        var map = new Dictionary<string, GamePlaytimeRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in items.Where(item => item.Accepted).Select(item => item.Value).OfType<PlaytimeCsvRow>())
        {
            if (!map.TryGetValue(row.GameName, out var game)) map[row.GameName] = game = new() { GameName = row.GameName };
            game.Sessions.Add(new(row.GameName, row.StartTime, row.EndTime, row.EndTime - row.StartTime, row.DurationMinutes));
        }
        return map.Values.ToList();
    }

    internal static byte[] ExpectedSide(string kind, List<LegacyItem> items)
    {
        var accepted = items.Where(item => item.Accepted).ToList();
        object value;
        switch (kind)
        {
            case "journal":
                var journals = new Dictionary<string, LegacyJournal>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in accepted) journals[item.Key] = (LegacyJournal)item.Value!;
                value = journals; break;
            case "genres": value = accepted.ToDictionary(item => item.Key, item => (string[])item.Value!); break;
            case "artwork": value = accepted.ToDictionary(item => item.Key, item => (uint)item.Value!); break;
            case "drafts": value = accepted.Select(item => (LegacyDraft)item.Value!).ToList(); break;
            case "scan": value = accepted.Select(item => item.Value).FirstOrDefault() ?? new LegacyScan(); break;
            case "theme": value = accepted.Select(item => item.Value).FirstOrDefault() ?? new LegacyTheme("dark"); break;
            default: throw new InvalidDataException("Unknown side codec.");
        }
        return JsonSerializer.SerializeToUtf8Bytes(value, value.GetType());
    }

    internal static void Validate(SqliteConnection c, ImportManifest manifest, string backupDirectory,
        IReadOnlyDictionary<string, List<LegacyItem>> decoded, TimeProvider clock, IReadOnlyList<GameConfig> configs)
    {
        var expected = ReadOldHistory(manifest, backupDirectory);
        Equal(expected, ExpectedHistory(manifest.Sources.Where(s => s.Kind is "csv" or "json").SelectMany(s => decoded[s.SourceId])), "old reader versus import decoding");
        using var tx = c.BeginTransaction(deferred: true);
        var actual = LegacyDatabaseReader.History(c, tx);
        CompareHistory(expected, actual, clock, configs);
        foreach (var source in manifest.Sources)
        {
            if (manifest.SessionsOnly && source.ConfigPath is not null) continue;
            var items = decoded[source.SourceId];
            var accepted = items.Count(item => item.Accepted);
            var skipped = items.Count - accepted;
            if (Convert.ToInt64(Scalar(c, tx, "SELECT accepted_count FROM import_sources WHERE source_id=$0;", source.SourceId)) != accepted ||
                Convert.ToInt64(Scalar(c, tx, "SELECT skipped_count FROM import_sources WHERE source_id=$0;", source.SourceId)) != skipped ||
                Convert.ToInt64(Scalar(c, tx, "SELECT count(*) FROM import_records WHERE source_id=$0;", source.SourceId)) != items.Count)
                throw new InvalidDataException("Import accounting differs: " + source.Path);
            if (source.ConfigPath is { } config && source.Exists)
            {
                var scope = Convert.ToInt64(Scalar(c, tx, "SELECT scope_id FROM config_scopes WHERE config_path=$0;", config));
                EqualBytes(ExpectedSide(source.Kind, items), LegacyDatabaseReader.Side(c, tx, scope, source.Kind), source.Path);
            }
            if (source.Kind == "inflight")
            {
                var snapshot = items.Select(item => item.Value).OfType<InFlightSnapshot>().FirstOrDefault();
                if (snapshot is not null)
                {
                    snapshot.Sessions.RemoveAll(session => string.IsNullOrWhiteSpace(session.DataKey));
                    EqualBytes(LegacyDatabaseReader.SnapshotBytes(snapshot), LegacyDatabaseReader.SnapshotBytes(LegacyDatabaseReader.Snapshot(c, tx)!), source.Path);
                }
            }
        }
        SqliteDatabaseHealth.Check(c, full: true, tx);
        tx.Commit();
    }

    private static IReadOnlyList<GamePlaytimeRecord> ReadOldHistory(ImportManifest manifest, string backupDirectory)
    {
        var temporary = Path.Combine(backupDirectory, ".legacy-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (var source in manifest.Sources.Where(s => s.Exists && s.Kind is "csv" or "json"))
                File.Copy(Path.Combine(backupDirectory, source.BackupName), Path.Combine(temporary, source.Kind == "csv" ? "playtime.csv" : "playtime.json"));
            // Exercise the actual old JSON -> CSV path only against an isolated scratch copy.
            _ = new CsvBackedPlayTimeService(temporary);
            var old = new FilePlaytimeSnapshotProvider(temporary).GetSnapshot();
            if (old.ReadFailed) throw new InvalidDataException("Old history read failed during compatibility validation.");
            return old.Records;
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    internal static void CompareHistory(IReadOnlyList<GamePlaytimeRecord> expected, IReadOnlyList<GamePlaytimeRecord> actual,
        TimeProvider clock, IReadOnlyList<GameConfig> configs)
    {
        // Exact ordered records include Kind, duration ticks, saved minutes, casing and duplicates.
        Equal(expected, actual, "ordered history");
        foreach (var days in new[] { 0, 7, 30, 90 })
            Equal(PlaytimeDashboardBuilder.Build(expected, configs, new(days), clock),
                PlaytimeDashboardBuilder.Build(actual, configs, new(days), clock), "dashboard " + days);
        var years = expected.SelectMany(game => game.Sessions).Select(session => session.EndTime.Year)
            .Append(clock.GetLocalNow().Year).Where(year => year is >= 2 and <= 9998).Distinct();
        foreach (var year in years)
            foreach (int? quarter in new int?[] { null, 1, 2, 3, 4 })
            {
                var period = new ReportPeriod(year, quarter);
                Equal(PlaytimeReportBuilder.Build(expected, configs, period, clock), PlaytimeReportBuilder.Build(actual, configs, period, clock), "period " + period);
            }
        var builder = new StatsReportBuilder();
        Equal(builder.Build(expected, configs, clock.GetLocalNow().DateTime, true), builder.Build(actual, configs, clock.GetLocalNow().DateTime, true), "sync bytes and fingerprint");
    }

    internal static void Equal<T>(T expected, T actual, string label) =>
        EqualBytes(JsonSerializer.SerializeToUtf8Bytes(expected), JsonSerializer.SerializeToUtf8Bytes(actual), label);
    internal static void EqualBytes(byte[] expected, byte[] actual, string label)
    {
        if (!expected.AsSpan().SequenceEqual(actual)) throw new InvalidDataException("Legacy compatibility mismatch: " + label);
    }
}
