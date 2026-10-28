using System.Text;
using System.Text.Json;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Providers;

namespace GameHelper.Infrastructure.Storage.Sqlite.Legacy;

internal sealed record LegacyItem(long Ordinal, string Key, object? Value, string Reason = "")
{
    internal bool Accepted => Reason.Length == 0;
}

internal static class LegacyDecoder
{
    internal static readonly string[] SideKinds = ["journal", "genres", "artwork", "drafts", "scan", "theme"];
    internal static List<LegacyItem> Decode(ImportSource source, string backupDirectory, bool csvExists)
    {
        if (!source.Exists || (source.Kind == "json" && csvExists)) return [];
        var path = Path.Combine(backupDirectory, source.BackupName);
        if (LegacySql.Hash(File.ReadAllBytes(path)) != source.Sha256) throw new InvalidDataException("Source backup hash mismatch: " + source.Path);
        var text = File.ReadAllText(path, Encoding.UTF8);
        if (source.Kind == "csv") return Csv(text);
        return DecodeJson(source.Kind, text);
    }

    internal static List<LegacyItem> DecodeJson(string kind, string text)
    {
        using var document = JsonDocument.Parse(text); // Never use the stores' read-failure-to-empty fallback.
        var root = document.RootElement;
        var items = new List<LegacyItem>();
        void Add(string key, object? value, string reason = "") => items.Add(new(items.Count + 1, key, value, reason));
        switch (kind)
        {
            case "json":
                if (root.ValueKind == JsonValueKind.Null) break;
                if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Legacy JSON requires an object.");
                if (!root.TryGetProperty("games", out var games) || games.ValueKind == JsonValueKind.Null) break;
                var oldGames = games.Deserialize<LegacyGame[]>() ?? [];
                foreach (var game in oldGames)
                    foreach (var session in game.Sessions ?? [])
                    {
                        // The old JSON migration writes CSV first: it loses subsecond precision and Kind.
                        var row = new PlaytimeCsvRow(game.GameName, Project(session.StartTime), Project(session.EndTime), session.DurationMinutes);
                        Add(game.GameName, row, string.IsNullOrWhiteSpace(game.GameName) || session.DurationMinutes < 0 ? "legacy CSV rejects key or minutes" : "");
                    }
                break;
            case "inflight":
                var snapshot = root.Deserialize<InFlightSnapshot>();
                if (snapshot is null) break; // Old Load returns null for literal null.
                snapshot.Sessions ??= [];
                foreach (var session in snapshot.Sessions)
                    if (session is null) throw new InvalidDataException("Null in-flight process.");
                var rejected = snapshot.Sessions.Select((session, index) => (session, index))
                    .Where(pair => string.IsNullOrWhiteSpace(pair.session.DataKey)).ToList();
                snapshot.Sessions.RemoveAll(session => string.IsNullOrWhiteSpace(session.DataKey));
                Add("", snapshot);
                foreach (var (_, index) in rejected) Add("", null, "legacy in-flight rejects blank DataKey at process " + (index + 1));
                break;
            case "journal":
                foreach (var (key, value) in root.Deserialize<Dictionary<string, LegacyJournal?>>() ?? [])
                    Add(key, value, string.IsNullOrWhiteSpace(key) || value is null ||
                        !new[] { "未开始", "在玩", "已通关", "全成就", "搁置" }.Contains(value.Status) ||
                        value.Note is null || value.Note.Length > 80 || value.Note.Contains('\r') || value.Note.Contains('\n')
                        ? "legacy journal validation rejected item" : "");
                break;
            case "genres":
                var genres = new Dictionary<string, string[]>(root.Deserialize<Dictionary<string, string[]>>() ?? [], StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in genres)
                {
                    if (value is null || value.Any(item => item is null) || value.Distinct(StringComparer.Ordinal).Count() != value.Length)
                        throw new InvalidDataException("Genre array cannot be represented losslessly: " + key);
                    Add(key, value);
                }
                break;
            case "artwork":
                var artwork = root.Deserialize<Dictionary<string, uint>>() ?? [];
                if (artwork.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != artwork.Count || artwork.Values.Any(id => id == 0))
                    throw new InvalidDataException("Artwork dictionary cannot be represented losslessly.");
                foreach (var (key, value) in artwork) Add(key, value);
                break;
            case "drafts":
                foreach (var draft in root.Deserialize<List<LegacyDraft>>() ?? [])
                {
                    if (draft?.Baseline?.Config is null) throw new InvalidDataException("Draft has no Baseline.Config.");
                    Add(draft.Baseline.Config.DataKey, draft);
                }
                break;
            case "scan":
                var scan = root.Deserialize<LegacyScan>() ?? new();
                if (scan.Directories is null || scan.Ignored is null || scan.Directories.Count > 6 ||
                    scan.Directories.Any(item => item is null) || scan.Ignored.Any(item => item is null) ||
                    scan.Directories.Distinct(StringComparer.OrdinalIgnoreCase).Count() != scan.Directories.Count ||
                    scan.Ignored.Distinct(StringComparer.OrdinalIgnoreCase).Count() != scan.Ignored.Count)
                    throw new InvalidDataException("Scan document cannot be represented losslessly.");
                Add("", scan);
                break;
            case "theme": Add("", new LegacyTheme(root.Deserialize<LegacyTheme>()?.Theme == "light" ? "light" : "dark")); break;
            default: throw new InvalidDataException("Unknown legacy codec: " + kind);
        }
        return items;
    }

    internal static List<LegacyItem> Csv(string text)
    {
        using var reader = new StringReader(text);
        using var records = LegacyCsvReader.ParseRecords(reader).GetEnumerator();
        if (!records.MoveNext()) return [];
        if (!LegacyCsvReader.IsExpectedHeader(records.Current)) throw new InvalidDataException("Nonempty CSV has an invalid header.");
        var items = new List<LegacyItem>();
        while (records.MoveNext())
        {
            var accepted = LegacyCsvReader.TryParseRow(records.Current, out var row);
            items.Add(new(items.Count + 1, row.GameName ?? "", accepted ? row : null,
                accepted ? "" : "legacy CSV rejects syntax, field count, key, date or minutes: " + JsonSerializer.Serialize(records.Current)));
        }
        return items;
    }

    private static DateTime Project(DateTime value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Unspecified);
}
