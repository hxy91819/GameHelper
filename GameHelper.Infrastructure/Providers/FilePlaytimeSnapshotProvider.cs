using System.Runtime.InteropServices;
using System.Text.Json;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using System.Text;

namespace GameHelper.Infrastructure.Providers;

public sealed class FilePlaytimeSnapshotProvider : IPlaytimeSnapshotProvider
{
    private readonly string? _playtimeDirectory;

    public FilePlaytimeSnapshotProvider()
    {
    }

    public FilePlaytimeSnapshotProvider(string playtimeDirectory)
    {
        _playtimeDirectory = playtimeDirectory ?? throw new ArgumentNullException(nameof(playtimeDirectory));
    }

    public IReadOnlyList<GamePlaytimeRecord> GetPlaytimeRecords()
    {
        return GetSnapshot().Records;
    }

    public IReadOnlyList<GamePlaytimeOverviewRecord> GetPlaytimeOverview(DateTime recentCutoff)
    {
        var csvFile = GetPlaytimeCsvPath();
        var jsonFile = GetPlaytimeJsonPath();

        try
        {
            if (File.Exists(csvFile))
            {
                return ReadOverviewFromCsv(csvFile, recentCutoff);
            }

            if (File.Exists(jsonFile))
            {
                return AggregateRecords(ReadFromJson(jsonFile), recentCutoff);
            }
        }
        catch
        {
            // Keep shell flows resilient to corrupt or unreadable history files.
        }

        return Array.Empty<GamePlaytimeOverviewRecord>();
    }

    public PlaytimeSnapshot GetSnapshot()
    {
        var csvFile = GetPlaytimeCsvPath();
        var jsonFile = GetPlaytimeJsonPath();

        try
        {
            if (File.Exists(csvFile))
            {
                return new PlaytimeSnapshot(ReadFromCsv(csvFile), csvFile);
            }

            if (File.Exists(jsonFile))
            {
                return new PlaytimeSnapshot(ReadFromJson(jsonFile), jsonFile);
            }
        }
        catch
        {
            // Keep shell flows resilient to corrupt or unreadable history files.
        }

        return new PlaytimeSnapshot(Array.Empty<GamePlaytimeRecord>(), null);
    }

    private string GetPlaytimeCsvPath()
    {
        return _playtimeDirectory is null
            ? AppDataPath.GetPlaytimeCsvPath()
            : Path.Combine(_playtimeDirectory, "playtime.csv");
    }

    private string GetPlaytimeJsonPath()
    {
        return _playtimeDirectory is null
            ? AppDataPath.GetPlaytimeJsonPath()
            : Path.Combine(_playtimeDirectory, "playtime.json");
    }

    private static IReadOnlyList<GamePlaytimeRecord> ReadFromCsv(string path)
    {
        var map = new Dictionary<string, GamePlaytimeRecord>(StringComparer.OrdinalIgnoreCase);
        PlaytimeCsvCodec.ReadRows(path, row =>
        {
            if (!map.TryGetValue(row.GameName, out var record))
            {
                record = new GamePlaytimeRecord { GameName = row.GameName };
                map[row.GameName] = record;
            }

            record.Sessions.Add(new PlaySession(
                row.GameName,
                row.StartTime,
                row.EndTime,
                row.EndTime - row.StartTime,
                row.DurationMinutes));
        });

        return map.Values.ToList();
    }

    private static IReadOnlyList<GamePlaytimeOverviewRecord> ReadOverviewFromCsv(string path, DateTime recentCutoff)
    {
        var map = new Dictionary<string, PlaytimeOverviewAccumulator>(StringComparer.OrdinalIgnoreCase);
        PlaytimeCsvCodec.ReadRows(path, row =>
        {
            ref var accumulator = ref CollectionsMarshal.GetValueRefOrAddDefault(map, row.GameName, out var exists);
            if (!exists)
            {
                accumulator = new PlaytimeOverviewAccumulator(row.GameName);
            }

            accumulator.Add(row.EndTime, row.DurationMinutes, recentCutoff);
        });

        return map.Values
            .Select(accumulator => accumulator.ToRecord())
            .ToList();
    }

    private static IReadOnlyList<GamePlaytimeOverviewRecord> AggregateRecords(
        IReadOnlyList<GamePlaytimeRecord> records,
        DateTime recentCutoff)
    {
        if (records.Count == 0)
        {
            return Array.Empty<GamePlaytimeOverviewRecord>();
        }

        return records
            .Select(record => new GamePlaytimeOverviewRecord(
                record.GameName,
                record.Sessions.Sum(session => session.DurationMinutes),
                record.Sessions.Where(session => session.EndTime >= recentCutoff).Sum(session => session.DurationMinutes),
                record.Sessions.Count))
            .ToList();
    }

    private static IReadOnlyList<GamePlaytimeRecord> ReadFromJson(string path)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var json = File.ReadAllText(path, Encoding.UTF8);
        var document = JsonSerializer.Deserialize<JsonElement>(json, options);
        if (document.ValueKind != JsonValueKind.Object || !document.TryGetProperty("games", out var gamesNode))
        {
            return Array.Empty<GamePlaytimeRecord>();
        }

        var records = JsonSerializer.Deserialize<List<GamePlaytimeRecord>>(gamesNode.GetRawText(), options);
        return records ?? new List<GamePlaytimeRecord>();
    }

    private struct PlaytimeOverviewAccumulator
    {
        private readonly string _gameName;

        public PlaytimeOverviewAccumulator(string gameName)
        {
            _gameName = gameName;
        }

        public long TotalMinutes { get; private set; }

        public long RecentMinutes { get; private set; }

        public int SessionCount { get; private set; }

        public void Add(DateTime endTime, long durationMinutes, DateTime recentCutoff)
        {
            TotalMinutes += durationMinutes;
            if (endTime >= recentCutoff)
            {
                RecentMinutes += durationMinutes;
            }

            SessionCount++;
        }

        public readonly GamePlaytimeOverviewRecord ToRecord()
        {
            return new GamePlaytimeOverviewRecord(_gameName, TotalMinutes, RecentMinutes, SessionCount);
        }
    }
}
