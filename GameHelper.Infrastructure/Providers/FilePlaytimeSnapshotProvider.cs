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
                var remaining = line.AsSpan();

                // 1. Game Name
                var gameNameSpan = SplitNextField(ref remaining);
                if (gameNameSpan.IsEmpty && remaining.IsEmpty) continue;

                // 2. Start Time
                var startTimeSpan = SplitNextField(ref remaining);
                if (startTimeSpan.IsEmpty) continue;

                // 3. End Time
                var endTimeSpan = SplitNextField(ref remaining);
                if (endTimeSpan.IsEmpty) continue;

                // 4. Duration
                var durationSpan = SplitNextField(ref remaining);
                if (durationSpan.IsEmpty) continue;

                var gameName = Unescape(gameNameSpan);
                var startTime = DateTime.Parse(startTimeSpan);
                var endTime = DateTime.Parse(endTimeSpan);
                var durationMinutes = long.Parse(durationSpan);

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

    /// <summary>
    /// Parses the next CSV field from the remaining line span.
    /// Handles quoted fields (including escaped quotes via double-quote) and standard fields.
    /// Updates the remaining span to point after the current field and delimiter.
    /// </summary>
    private static ReadOnlySpan<char> SplitNextField(ref ReadOnlySpan<char> remaining)
    {
        if (remaining.IsEmpty) return ReadOnlySpan<char>.Empty;

        // Check if the field starts with a quote
        if (remaining[0] == '"')
        {
            // Quoted field
            // We need to find the closing quote that is NOT escaped.
            // An escaped quote is represented as two double quotes ("").

            int i = 1;
            while (i < remaining.Length)
            {
                if (remaining[i] == '"')
                {
                    // Check if this is an escaped quote ("")
                    if (i + 1 < remaining.Length && remaining[i + 1] == '"')
                    {
                        // Skip the escaped quote
                        i += 2;
                        continue;
                    }

                    // Found the closing quote
                    var field = remaining.Slice(1, i - 1); // Extract content inside quotes

                    // Move past the closing quote
                    int advance = i + 1;

                    // Move past the comma if it exists
                    if (advance < remaining.Length && remaining[advance] == ',')
                    {
                        advance++;
                    }

                    remaining = remaining.Slice(advance);
                    return field;
                }
                i++;
            }

            // Malformed quoted field (no closing quote), consume the rest as the field
            var rest = remaining.Slice(1);
            remaining = ReadOnlySpan<char>.Empty;
            return rest;
        }
        else
        {
            // Standard field
            int commaIndex = remaining.IndexOf(',');
            if (commaIndex >= 0)
            {
                var field = remaining.Slice(0, commaIndex);
                remaining = remaining.Slice(commaIndex + 1);
                return field;
            }
            else
            {
                var field = remaining;
                remaining = ReadOnlySpan<char>.Empty;
                return field;
            }
        }
    }

    private static string Unescape(ReadOnlySpan<char> field)
    {
        // Optimization: check if we need to replace anything
        bool needsUnescape = false;
        foreach (char c in field)
        {
            if (c == '"')
            {
                needsUnescape = true;
                break;
            }
        }

        if (needsUnescape)
        {
            return field.ToString().Replace("\"\"", "\"");
        }

        return field.ToString();
    }
}
