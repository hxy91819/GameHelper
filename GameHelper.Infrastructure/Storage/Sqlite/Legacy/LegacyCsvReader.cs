using System.Globalization;
using System.Text;
using GameHelper.Infrastructure.Providers;

namespace GameHelper.Infrastructure.Storage.Sqlite.Legacy;

// Frozen legacy parser; unlike the runtime reader, import also accounts for rejected logical records.
internal static class LegacyCsvReader
{
    internal static bool IsExpectedHeader(IReadOnlyList<string> fields)
    {
        return fields.Count == 4
            && string.Equals(fields[0], "game", StringComparison.Ordinal)
            && string.Equals(fields[1], "start_time", StringComparison.Ordinal)
            && string.Equals(fields[2], "end_time", StringComparison.Ordinal)
            && string.Equals(fields[3], "duration_minutes", StringComparison.Ordinal);
    }

    internal static bool TryParseRow(IReadOnlyList<string> fields, out PlaytimeCsvRow row)
    {
        row = default;
        if (fields.Count != 4 || string.IsNullOrWhiteSpace(fields[0]))
        {
            return false;
        }

        if (!DateTime.TryParse(fields[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var startTime)
            || !DateTime.TryParse(fields[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var endTime)
            || !long.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var durationMinutes)
            || durationMinutes < 0)
        {
            return false;
        }

        row = new PlaytimeCsvRow(fields[0], startTime, endTime, durationMinutes);
        return true;
    }

    internal static IEnumerable<IReadOnlyList<string>> ParseRecords(TextReader reader)
    {
        var fields = new List<string>(4);
        var field = new StringBuilder();
        var inQuotes = false;
        var afterClosingQuote = false;
        var malformedRecord = false;
        var hasRecordContent = false;

        while (reader.Read() is var value && value >= 0)
        {
            var current = (char)value;
            hasRecordContent = true;

            if (inQuotes)
            {
                if (current != '"')
                {
                    field.Append(current);
                    continue;
                }

                if (reader.Peek() == '"')
                {
                    _ = reader.Read();
                    field.Append('"');
                    continue;
                }

                inQuotes = false;
                afterClosingQuote = true;
                continue;
            }

            if (afterClosingQuote && current is not (',' or '\r' or '\n'))
            {
                malformedRecord = true;
                continue;
            }

            if (current == '"' && field.Length == 0)
            {
                inQuotes = true;
                continue;
            }

            if (current == '"')
            {
                malformedRecord = true;
                continue;
            }

            if (current == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
                afterClosingQuote = false;
                continue;
            }

            if (current is '\r' or '\n')
            {
                if (current == '\r' && reader.Peek() == '\n')
                {
                    _ = reader.Read();
                }

                fields.Add(field.ToString());
                field.Clear();
                yield return malformedRecord ? Array.Empty<string>() : fields.ToArray();
                fields.Clear();
                hasRecordContent = false;
                afterClosingQuote = false;
                malformedRecord = false;
                continue;
            }

            field.Append(current);
        }

        // The legacy runtime discards this tail; import records its rejected logical ordinal.
        if (hasRecordContent)
        {
            fields.Add(field.ToString());
            yield return malformedRecord || inQuotes ? Array.Empty<string>() : fields.ToArray();
        }
    }

}
