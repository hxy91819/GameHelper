using GameHelper.Core.Models;

namespace GameHelper.Desktop;

/// <summary>Explicit-save journal draft; statuses are independent tags and the clock supplies a local completion date.</summary>
public sealed class JournalEditorViewModel : ObservableState
{
    private readonly LibraryJournalStore _store;
    private readonly string _key;
    private readonly TimeProvider _clock;
    private readonly JournalStatusOption[] _options;
    private DateTime? _completedOn;
    private string _note;
    private string _error = string.Empty;
    private bool _applying;
    internal JournalEditorViewModel(LibraryJournalStore store, string key, string name, TimeProvider clock)
    {
        _store = store; _key = key; _clock = clock; Name = name;
        _options = JournalStatus.All.Select(item => new JournalStatusOption(item, OnStatusChanged)).ToArray();
        var record = store.Read(key);
        ApplyStatus(record.Status, fillDate: false);
        _completedOn = record.CompletedOn?.ToDateTime(TimeOnly.MinValue);
        _note = record.Note;
    }
    internal static async Task<JournalEditorViewModel> LoadAsync(LibraryJournalStore store, string key, string name, TimeProvider clock)
    {
        await store.ReloadAsync();
        return new(store, key, name, clock);
    }
    public string Name { get; }
    /// <summary>未开始 is exclusive; the four tags combine freely and none selected falls back to 未开始.</summary>
    public IReadOnlyList<JournalStatusOption> StatusOptions => _options;
    public int NoteLimit => LibraryJournalStore.NoteLimit;
    public string Status
    {
        get => JournalStatus.Join(_options.Where(option => option.IsSelected).Select(option => option.Name));
        set => ApplyStatus(value);
    }
    public bool IsCompleted => JournalStatus.IsCompleted(Status);
    public DateTime? CompletedOn { get => _completedOn; set => Set(ref _completedOn, value); }
    public string Note { get => _note; set { if (Set(ref _note, value)) Changed(nameof(NoteCount)); } }
    public string NoteCount => $"{Note.Length} / {NoteLimit}";
    public string Error { get => _error; private set => Set(ref _error, value); }
    public bool Save()
    {
        try
        {
            _store.Save(_key, Draft());
            Error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { Error = "手帐未保存：" + ex.Message; return false; }
    }
    public async Task<bool> SaveAsync()
    {
        try
        {
            await _store.SaveAsync(_key, Draft());
            Error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { Error = "手帐未保存：" + ex.Message; return false; }
    }

    private JournalRecord Draft() => new(Status, IsCompleted && CompletedOn is { } date ? DateOnly.FromDateTime(date) : null, Note);

    /// <summary>Applies a stored/programmatic status without echoing option callbacks.</summary>
    private void ApplyStatus(string? status, bool fillDate = true)
    {
        var normalized = JournalStatus.TryNormalize(status, out var value) ? value : JournalStatus.NotStarted;
        var selected = normalized.Split(JournalStatus.Separator);
        _applying = true;
        try { foreach (var option in _options) option.Select(selected.Contains(option.Name, StringComparer.Ordinal)); }
        finally { _applying = false; }
        if (fillDate) FillCompletionDate();
        Changed(nameof(Status)); Changed(nameof(IsCompleted));
    }

    /// <summary>Applies a click on one tag; the window uses this so the UI never writes sibling states mid-binding.</summary>
    internal void Toggle(string name, bool selected)
    {
        if (_options.FirstOrDefault(option => option.Name == name) is not { } option || option.IsSelected == selected) return;
        option.Select(selected);
        ApplyToggle(option);
    }

    private void OnStatusChanged(JournalStatusOption option)
    {
        if (_applying) return;
        ApplyToggle(option);
    }

    private void ApplyToggle(JournalStatusOption option)
    {
        _applying = true;
        try
        {
            if (option.IsSelected)
            {
                var exclusive = option.Name == JournalStatus.NotStarted;
                foreach (var other in _options.Where(item => item != option && (exclusive || item.Name == JournalStatus.NotStarted)))
                    other.Select(false);
            }
            else if (!_options.Any(item => item.IsSelected))
                _options.First(item => item.Name == JournalStatus.NotStarted).Select(true);
        }
        finally { _applying = false; }
        FillCompletionDate();
        Changed(nameof(Status)); Changed(nameof(IsCompleted));
    }

    private void FillCompletionDate()
    {
        if (!IsCompleted || CompletedOn is not null) return;
        _completedOn = _clock.GetLocalNow().Date;
        Changed(nameof(CompletedOn));
    }
}

/// <summary>One selectable journal tag; toggling notifies the owning editor so exclusivity stays coherent.</summary>
public sealed class JournalStatusOption : ObservableState
{
    private readonly Action<JournalStatusOption> _changed;
    private bool _selected;
    internal JournalStatusOption(string name, Action<JournalStatusOption> changed) { Name = name; _changed = changed; }
    public string Name { get; }
    public bool IsSelected
    {
        get => _selected;
        set { if (!Set(ref _selected, value)) return; _changed(this); }
    }
    internal void Select(bool value) => Set(ref _selected, value, nameof(IsSelected));
}
