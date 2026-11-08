using GameHelper.Core.Models;

namespace GameHelper.Desktop;

/// <summary>Private journal metadata keyed by the stable catalog/history identity.</summary>
/// <param name="Status">One <see cref="JournalStatus"/> tag or a canonical comma-joined subset; 未开始 means no tag.</param>
public sealed record JournalRecord(string Status = JournalStatus.NotStarted, DateOnly? CompletedOn = null, string Note = "");

internal sealed class LibraryJournalStore
{
    internal static readonly string[] Statuses = [.. JournalStatus.All];
    internal const int NoteLimit = 500;
    private readonly string? _path;
    private Dictionary<string, JournalRecord> _records;
    private readonly DesktopDocument<Dictionary<string, JournalRecord>> _document;

    internal LibraryJournalStore(string? configPath, IDesktopDocumentReader? reader = null)
    {
        _path = configPath is null ? null : configPath + ".desktop-journal.json";
        _document = new(_path, () => new(), reader: reader);
        _records = new(StringComparer.OrdinalIgnoreCase);
        Reload();
    }

    internal void Reload()
    {
        ApplyRecords(_document.Read());
    }
    internal async Task ReloadAsync() => ApplyRecords(await Task.Run(_document.Read));
    private void ApplyRecords(Dictionary<string, JournalRecord> saved)
    {
        var next = new Dictionary<string, JournalRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in saved)
            if (!string.IsNullOrWhiteSpace(key) && value is not null && Valid(value, out var record))
                next[key] = record;
        _records = next;
    }

    internal JournalRecord Read(string key) => _records.GetValueOrDefault(key) ?? new();
    internal IEnumerable<KeyValuePair<string, JournalRecord>> Entries => _records;
    internal event EventHandler? Saved;
    internal void Save(string key, JournalRecord record)
    {
        if (_path is null) throw new InvalidOperationException("只读界面不能保存手帐。");
        var next = new Dictionary<string, JournalRecord>(_records, StringComparer.OrdinalIgnoreCase) { [key] = Validate(key, record) };
        _document.Write(next);
        _records = next;
        Saved?.Invoke(this, EventArgs.Empty);
    }

    internal async Task SaveAsync(string key, JournalRecord record)
    {
        if (_path is null) throw new InvalidOperationException("只读界面不能保存手帐。");
        var next = new Dictionary<string, JournalRecord>(_records, StringComparer.OrdinalIgnoreCase) { [key] = Validate(key, record) };
        await Task.Run(() => _document.Write(next));
        _records = next;
        Saved?.Invoke(this, EventArgs.Empty);
    }

    internal bool Matches(string key, string status) => string.IsNullOrEmpty(status) || JournalStatus.Has(Read(key).Status, status);

    private static JournalRecord Validate(string key, JournalRecord record)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("缺少游戏身份。");
        if (!JournalStatus.TryNormalize(record.Status, out var status)) throw new ArgumentException("请选择有效的归档状态。");
        if (record.Note is null || record.Note.Length > NoteLimit || record.Note.Contains('\r') || record.Note.Contains('\n'))
            throw new ArgumentException($"短评限单行 {NoteLimit} 字。");
        return record with { Status = status };
    }

    /// <summary>Accepts only stored shapes, returning the canonical status for round-tripping.</summary>
    private static bool Valid(JournalRecord? record, out JournalRecord normalized)
    {
        normalized = new();
        if (record is null || record.Note is null || record.Note.Length > NoteLimit
            || record.Note.Contains('\r') || record.Note.Contains('\n')) return false;
        if (!JournalStatus.TryNormalize(record.Status, out var status)) return false;
        normalized = record with { Status = status };
        return true;
    }
}
