namespace GameHelper.Desktop;

/// <summary>Explicit-save journal draft; the clock supplies a local completion date.</summary>
public sealed class JournalEditorViewModel : ObservableState
{
    private readonly LibraryJournalStore _store;
    private readonly string _key;
    private readonly TimeProvider _clock;
    private string _status;
    private DateTime? _completedOn;
    private string _note;
    private string _error = string.Empty;
    internal JournalEditorViewModel(LibraryJournalStore store, string key, string name, TimeProvider clock)
    {
        _store = store; _key = key; _clock = clock; Name = name;
        var record = store.Read(key);
        _status = record.Status;
        _completedOn = record.CompletedOn?.ToDateTime(TimeOnly.MinValue);
        _note = record.Note;
    }
    public string Name { get; }
    public IReadOnlyList<string> Statuses => LibraryJournalStore.Statuses;
    public string Status
    {
        get => _status;
        set
        {
            if (!Set(ref _status, value)) return;
            if (IsCompleted && CompletedOn is null) CompletedOn = _clock.GetLocalNow().Date;
            Changed(nameof(IsCompleted));
        }
    }
    public bool IsCompleted => Status is "已通关" or "全成就";
    public DateTime? CompletedOn { get => _completedOn; set => Set(ref _completedOn, value); }
    public string Note { get => _note; set { if (Set(ref _note, value)) Changed(nameof(NoteCount)); } }
    public string NoteCount => $"{Note.Length} / {LibraryJournalStore.NoteLimit}";
    public string Error { get => _error; private set => Set(ref _error, value); }
    public bool Save()
    {
        try
        {
            _store.Save(_key, new(Status, IsCompleted && CompletedOn is { } date ? DateOnly.FromDateTime(date) : null, Note));
            Error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { Error = "手帐未保存：" + ex.Message; return false; }
    }
}
