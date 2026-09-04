using System.Globalization;
using System.Text;

namespace GameHelper.Infrastructure.Providers;

/// <summary>
/// Owns the on-disk playtime CSV schema and all serialization details.
/// </summary>
internal static class PlaytimeCsvCodec
{
    internal const string Header = "game,start_time,end_time,duration_minutes";

    private const int FieldCount = 4;
    private const string DateTimeFormat = "yyyy-MM-ddTHH:mm:ss";
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    internal static void EnsureFileExists(string path)
    {
        if (File.Exists(path))
        {
            return;
        }

        EnsureDirectory(path);

        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, Utf8WithoutBom);
            writer.WriteLine(Header);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another writer won the create race. Its schema header is authoritative.
        }
    }

    internal static void Append(string path, PlaytimeCsvRow row)
    {
        EnsureFileExists(path);

        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, Utf8WithoutBom);
        WriteRow(writer, row);
    }

    internal static void WriteAll(string path, IEnumerable<PlaytimeCsvRow> rows)
    {
        EnsureDirectory(path);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, Utf8WithoutBom))
            {
                writer.WriteLine(Header);
                foreach (var row in rows)
                {
                    WriteRow(writer, row);
                }
            }

            File.Move(tempPath, path, overwrite: false);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Cleanup must not hide the migration/write failure that caused it.
            }
        }
    }

    internal static void ReadRows(string path, Action<PlaytimeCsvRow> handleRow)
    {
        ArgumentNullException.ThrowIfNull(handleRow);

        using var reader = new StreamReader(path, Utf8WithoutBom, detectEncodingFromByteOrderMarks: true);
        using var records = ParseRecords(reader).GetEnumerator();

        if (!records.MoveNext() || !IsExpectedHeader(records.Current))
        {
            return;
        }

        while (records.MoveNext())
        {
            if (TryParseRow(records.Current, out var row))
            {
                handleRow(row);
            }
        }
    }

    private static bool IsExpectedHeader(IReadOnlyList<string> fields)
    {
        return fields.Count == FieldCount
            && string.Equals(fields[0], "game", StringComparison.Ordinal)
            && string.Equals(fields[1], "start_time", StringComparison.Ordinal)
            && string.Equals(fields[2], "end_time", StringComparison.Ordinal)
            && string.Equals(fields[3], "duration_minutes", StringComparison.Ordinal);
    }

    private static bool TryParseRow(IReadOnlyList<string> fields, out PlaytimeCsvRow row)
    {
        row = default;
        if (fields.Count != FieldCount || string.IsNullOrWhiteSpace(fields[0]))
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

    private static IEnumerable<IReadOnlyList<string>> ParseRecords(TextReader reader)
    {
        var fields = new List<string>(FieldCount);
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

        // An unterminated quoted field is malformed and intentionally discarded.
        if (hasRecordContent && !inQuotes)
        {
            fields.Add(field.ToString());
            yield return malformedRecord ? Array.Empty<string>() : fields.ToArray();
        }
    }

    private static void WriteRow(TextWriter writer, PlaytimeCsvRow row)
    {
        writer.Write(Escape(row.GameName));
        writer.Write(',');
        writer.Write(row.StartTime.ToString(DateTimeFormat, CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(row.EndTime.ToString(DateTimeFormat, CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.WriteLine(row.DurationMinutes.ToString(CultureInfo.InvariantCulture));
    }

    private static string Escape(string field)
    {
        if (field.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return field;
        }

        return $"\"{field.Replace("\"", "\"\"")}\"";
    }

    private static void EnsureDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}

internal readonly record struct PlaytimeCsvRow(
    string GameName,
    DateTime StartTime,
    DateTime EndTime,
    long DurationMinutes);
