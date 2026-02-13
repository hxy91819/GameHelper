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
                var span = line.AsSpan();

                // Parse 4 fields: GameName, StartTime, EndTime, DurationMinutes

                // 1. GameName
                SplitNextField(span, out var gameNamePart, out var rest1);
                if (gameNamePart.IsEmpty && rest1.IsEmpty) continue;

                var gameName = ParseString(gameNamePart);

                // 2. StartTime
                SplitNextField(rest1, out var startTimePart, out var rest2);
                var startTime = ParseDateTime(startTimePart);

                // 3. EndTime
                SplitNextField(rest2, out var endTimePart, out var rest3);
                var endTime = ParseDateTime(endTimePart);

                // 4. DurationMinutes
                SplitNextField(rest3, out var durationPart, out _);
                var durationMinutes = ParseLong(durationPart);

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

    // Optimized CSV splitting logic using Span to avoid string allocations
    private static void SplitNextField(ReadOnlySpan<char> input, out ReadOnlySpan<char> field, out ReadOnlySpan<char> rest)
    {
        if (input.IsEmpty)
        {
            field = ReadOnlySpan<char>.Empty;
            rest = ReadOnlySpan<char>.Empty;
            return;
        }

        if (input[0] == '"')
        {
            // Quoted field
            for (int i = 1; i < input.Length; i++)
            {
                if (input[i] == '"')
                {
                    // Check for escaped quote ""
                    if (i + 1 < input.Length && input[i + 1] == '"')
                    {
                        i++; // skip next quote
                    }
                    else
                    {
                        // End of quoted field
                        int endOfField = i + 1;
                        // Expect comma or end
                        if (endOfField < input.Length && input[endOfField] == ',')
                        {
                            field = input.Slice(0, endOfField);
                            rest = input.Slice(endOfField + 1);
                            return;
                        }
                        field = input.Slice(0, endOfField);
                        rest = input.Slice(endOfField);
                        return;
                    }
                }
            }
            // Mismatched quotes or end of line inside quotes - treat as whole field
            field = input;
            rest = ReadOnlySpan<char>.Empty;
        }
        else
        {
            // Simple field
            int commaIndex = input.IndexOf(',');
            if (commaIndex >= 0)
            {
                field = input.Slice(0, commaIndex);
                rest = input.Slice(commaIndex + 1);
                return;
            }
            field = input;
            rest = ReadOnlySpan<char>.Empty;
        }
    }

    private static string ParseString(ReadOnlySpan<char> field)
    {
        if (field.IsEmpty) return string.Empty;

        // Check if quoted
        if (field.Length >= 2 && field[0] == '"' && field[field.Length - 1] == '"')
        {
            var content = field.Slice(1, field.Length - 2);
            // Check if needs unescaping
            if (content.IndexOf('"') >= 0)
            {
                return content.ToString().Replace("\"\"", "\"");
            }
            return content.ToString();
        }
        return field.ToString();
    }

    private static DateTime ParseDateTime(ReadOnlySpan<char> field)
    {
        if (field.Length >= 2 && field[0] == '"' && field[field.Length - 1] == '"')
        {
             field = field.Slice(1, field.Length - 2);
        }
        return DateTime.Parse(field);
    }

     private static long ParseLong(ReadOnlySpan<char> field)
    {
        if (field.Length >= 2 && field[0] == '"' && field[field.Length - 1] == '"')
        {
             field = field.Slice(1, field.Length - 2);
        }
        return long.Parse(field);
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
}
