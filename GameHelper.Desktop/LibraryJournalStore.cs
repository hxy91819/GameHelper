namespace GameHelper.Desktop;

/// <summary>Private journal metadata keyed by the stable catalog/history identity.</summary>
public sealed record JournalRecord(string Status = "未开始", DateOnly? CompletedOn = null, string Note = "");

internal sealed class LibraryJournalStore
{
    internal static readonly string[] Statuses = ["未开始", "在玩", "已通关", "搁置"];
    internal const int NoteLimit = 80;
    private readonly string? _path;
    private Dictionary<string, JournalRecord> _records;

    internal LibraryJournalStore(string? configPath)
    {
        _path = configPath is null ? null : configPath + ".desktop-journal.json";
        var saved = DesktopDocumentStore.Read(_path, () => new Dictionary<string, JournalRecord>());
        _records = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in saved)
            if (!string.IsNullOrWhiteSpace(key) && value is not null && Statuses.Contains(value.Status)
                && value.Note is not null && value.Note.Length <= NoteLimit && !value.Note.Contains('\r') && !value.Note.Contains('\n'))
                _records[key] = value;
    }

    internal JournalRecord Read(string key) => _records.GetValueOrDefault(key) ?? new();
    internal event EventHandler? Saved;
    internal void Save(string key, JournalRecord record)
    {
        if (_path is null) throw new InvalidOperationException("只读界面不能保存手帐。");
        if (string.IsNullOrWhiteSpace(key) || !Statuses.Contains(record.Status)) throw new ArgumentException("请选择归档状态。");
        if (record.Note.Length > NoteLimit || record.Note.Contains('\r') || record.Note.Contains('\n'))
            throw new ArgumentException("短评限单行 80 字。");
        var next = new Dictionary<string, JournalRecord>(_records, StringComparer.OrdinalIgnoreCase) { [key] = record };
        DesktopDocumentStore.Write(_path, next);
        _records = next;
        Saved?.Invoke(this, EventArgs.Empty);
    }

    internal bool Matches(string key, string status) => string.IsNullOrEmpty(status) || Read(key).Status == status;
}
