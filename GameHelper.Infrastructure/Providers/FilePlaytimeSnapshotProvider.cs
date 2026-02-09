using System.Text.Json;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;

namespace GameHelper.Infrastructure.Providers;

public sealed class FilePlaytimeSnapshotProvider : IPlaytimeSnapshotProvider
{
    public IReadOnlyList<GamePlaytimeRecord> GetPlaytimeRecords()
    {
        var csvFile = AppDataPath.GetPlaytimeCsvPath();
        var jsonFile = AppDataPath.GetPlaytimeJsonPath();

        try
        {
            if (File.Exists(csvFile))
            {
                return ReadFromCsv(csvFile);
            }

            if (File.Exists(jsonFile))
            {
                return ReadFromJson(jsonFile);
            }
        }
        catch
        {
            // Keep shell flows resilient to corrupt or unreadable history files.
        }

        return Array.Empty<GamePlaytimeRecord>();
    }

    private static IReadOnlyList<GamePlaytimeRecord> ReadFromCsv(string path)
    {
        var map = new Dictionary<string, GamePlaytimeRecord>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StreamReader(path);
        _ = reader.ReadLine(); // skip header

        while (reader.ReadLine() is { } line)
        {
            try
            {
                var parts = ParseCsvLine(line);
                if (parts.Length < 4)
                {
                    continue;
                }

                var gameName = parts[0];
                var startTime = DateTime.Parse(parts[1]);
                var endTime = DateTime.Parse(parts[2]);
                var durationMinutes = long.Parse(parts[3]);

                if (!map.TryGetValue(gameName, out var record))
                {
                    record = new GamePlaytimeRecord { GameName = gameName };
                    map[gameName] = record;
                }

                record.Sessions.Add(new PlaySession(
                    gameName,
                    startTime,
                    endTime,
                    endTime - startTime,
                    durationMinutes));
            }
            catch
            {
                // Skip malformed rows.
            }
        }

        return map.Values.ToList();
    }

    private static IReadOnlyList<GamePlaytimeRecord> ReadFromJson(string path)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var json = File.ReadAllText(path);
        var document = JsonSerializer.Deserialize<JsonElement>(json, options);
        if (document.ValueKind != JsonValueKind.Object || !document.TryGetProperty("games", out var gamesNode))
        {
            return Array.Empty<GamePlaytimeRecord>();
        }

        var records = JsonSerializer.Deserialize<List<GamePlaytimeRecord>>(gamesNode.GetRawText(), options);
        return records ?? new List<GamePlaytimeRecord>();
    }

    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        result.Add(current.ToString());
        return result.ToArray();
    }
}
