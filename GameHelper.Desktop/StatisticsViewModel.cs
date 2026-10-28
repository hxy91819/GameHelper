using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Desktop;

public sealed class StatisticsGameRow : ObservableState
{
    private ImageSource? _artwork;

    public StatisticsGameRow(int rank, string dataKey, string name, long minutes, int sessions, DateTime lastPlayed, double share, ImageSource? artwork)
    {
        Rank = rank;
        DataKey = dataKey;
        Name = name;
        Minutes = minutes;
        Sessions = sessions;
        LastPlayed = lastPlayed;
        Share = share;
        _artwork = artwork;
        Initial = string.IsNullOrEmpty(name) ? "G" : StringInfo.GetNextTextElement(name);
    }

    public int Rank { get; }
    public string DataKey { get; }
    public string Name { get; }
    public long Minutes { get; }
    public int Sessions { get; }
    public DateTime LastPlayed { get; }
    public double Share { get; }
    public string Initial { get; }
    public ImageSource? Artwork { get => _artwork; set => Set(ref _artwork, value); }
    private string _journalSummary = string.Empty;
    private string _note = string.Empty;
    public string JournalSummary { get => _journalSummary; internal set => Set(ref _journalSummary, value); }
    public string Note { get => _note; internal set => Set(ref _note, value); }
    public string RankLabel => Rank.ToString("00", CultureInfo.InvariantCulture);
    public string Time => StatisticsViewModel.Duration(Minutes);
    public string ShareLabel => Share.ToString("P0", CultureInfo.CurrentCulture);
    public double BarWidth => Math.Clamp(Share * 180, 2, 180);
    public string LastLabel => LastPlayed.ToString("yy/MM/dd");
}

public sealed class StatisticsSessionRow : ObservableState
{
    private ImageSource? _artwork;

    public StatisticsSessionRow(string dataKey, string name, DateTime start, DateTime end, long minutes, ImageSource? artwork)
    {
        DataKey = dataKey;
        Name = name;
        Start = start;
        End = end;
        Minutes = minutes;
        _artwork = artwork;
        Initial = string.IsNullOrEmpty(name) ? "G" : StringInfo.GetNextTextElement(name);
    }

    public string DataKey { get; }
    public string Name { get; }
    public DateTime Start { get; }
    public DateTime End { get; }
    public long Minutes { get; }
    public string Initial { get; }
    public ImageSource? Artwork { get => _artwork; set => Set(ref _artwork, value); }
    public string DateLabel => End.ToString("MM/dd");
    public string StartLabel => Start.ToString("MM/dd HH:mm");
    public string Time => StatisticsViewModel.Duration(Minutes);
    public string Detail => $"{Start:yyyy/MM/dd HH:mm} → {End:yyyy/MM/dd HH:mm}";
}

/// <summary>Dashboard presentation with private desktop journal metadata; changing pages never touches game drafts or monitoring.</summary>
public sealed class StatisticsViewModel : ObservableState, IDisposable
{
    private const string PolicyNote = "仅统计已结束的会话 · 含已移出游戏库的历史";
    private readonly IGameLibrary _library;
    private readonly LibraryJournalStore _journal;
    private readonly TimeProvider _clock;
    private string _journalStatus = string.Empty;
    private readonly Func<string, ImageSource?>? _artwork;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _pending;
    private readonly List<AsyncCommand> _commands = [];
    private readonly ObservableCollection<StatisticsGameRow> _games = [];
    private PlaytimeDashboard _data = new();
    private IReadOnlyList<StatisticsSessionRow> _recent = [];
    private ImageSource? _favouriteArtwork;
    private int _days = 30;
    private string? _sessionKey;
    private string? _sessionName;
    private int _offset;
    private DateOnly? _focusDate;
    private string _search = string.Empty;
    private string _status = PolicyNote;
    private bool _busy;
    private bool _loaded;
    private bool _disposed;
    private PlaytimeDashboardQuery _appliedQuery = new();

    public StatisticsViewModel(IGameLibrary library, Func<string, ImageSource?>? artwork = null)
        : this(library, artwork, new LibraryJournalStore(null), TimeProvider.System) { }

    internal StatisticsViewModel(IGameLibrary library, Func<string, ImageSource?>? artwork, LibraryJournalStore journal, TimeProvider clock)
    {
        _library = library;
        _journal = journal; _clock = clock;
        _journal.Saved += OnJournalSaved;
        _artwork = artwork;
        Games = CollectionViewSource.GetDefaultView(_games);
        Games.Filter = value => value is StatisticsGameRow row && _journal.Matches(row.DataKey, _journalStatus)
            && (row.Name.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase) || row.DataKey.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase));
        RefreshCommand = Command(RefreshAsync, () => !IsBusy);
        WeekCommand = Command(() => SetRangeAsync(7), () => true);
        MonthCommand = Command(() => SetRangeAsync(30), () => true);
        QuarterCommand = Command(() => SetRangeAsync(90), () => true);
        AllCommand = Command(() => SetRangeAsync(0), () => true);
        PreviousCommand = Command(() => PageAsync(-1), () => !IsBusy && _data.Offset > 0);
        NextCommand = Command(() => PageAsync(1), () => !IsBusy && _data.Offset + _data.PageSize < _data.RecentTotal);
        ClearGameCommand = Command(() => SelectGameAsync(null), () => !IsBusy && (_sessionKey is not null || _focusDate is not null));
    }

    public ICollectionView Games { get; }
    public ICommand RefreshCommand { get; }
    public ICommand WeekCommand { get; }
    public ICommand MonthCommand { get; }
    public ICommand QuarterCommand { get; }
    public ICommand AllCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand ClearGameCommand { get; }
    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); UpdateCommands(); } }
    public bool HasHistory => _data.Sessions > 0;
    internal bool HasLoaded => _loaded;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Search { get => _search; set { if (Set(ref _search, value)) RefreshFilter(); } }
    public IReadOnlyList<string> JournalStatuses { get; } = ["所有状态", .. LibraryJournalStore.Statuses];
    public string SelectedJournalStatus { get => string.IsNullOrEmpty(_journalStatus) ? "所有状态" : _journalStatus; set { if (Set(ref _journalStatus, value == "所有状态" ? string.Empty : value)) RefreshFilter(); } }
    public bool HasMatchingGames => !Games.IsEmpty;
    internal Task<JournalEditorViewModel> EditJournalAsync(StatisticsGameRow row) => JournalEditorViewModel.LoadAsync(_journal, row.DataKey, row.Name, _clock);
    internal JournalEditorViewModel EditJournal(StatisticsGameRow row) => new(_journal, row.DataKey, row.Name, _clock);
    private void RefreshFilter() { Games.Refresh(); Changed(nameof(HasMatchingGames)); }
    private void OnJournalSaved(object? sender, EventArgs e)
    {
        foreach (var row in _games) ApplyJournal(row);
        RefreshFilter();
    }
    private void ApplyJournal(StatisticsGameRow row)
    {
        var record = _journal.Read(row.DataKey);
        row.JournalSummary = record.Status + (record.CompletedOn is { } date ? $"\n{date:yyyy/MM/dd}" : string.Empty);
        row.Note = record.Note.Length == 0 ? "还没有留下短评" : record.Note;
    }
    public int Days => _days;
    public string TotalHours => (_data.Minutes / 60d).ToString("N1", CultureInfo.CurrentCulture);
    public string SessionCount => _data.Sessions.ToString("N0", CultureInfo.CurrentCulture);
    public string ActiveDays => _data.ActiveDays.ToString("N0", CultureInfo.CurrentCulture);
    public string GamesPlayed => _data.GamesPlayed.ToString("N0", CultureInfo.CurrentCulture);
    public string Average => _data.Sessions == 0 ? "暂无单次记录" : $"平均单次 {Duration(_data.Minutes / _data.Sessions)}";
    public string DateRange => _loaded ? $"{_data.Start:yyyy-MM-dd} — {_data.Today:yyyy-MM-dd} · {(_days == 0 ? "全部历史" : $"最近 {_days} 天")}" : "正在读取历史记录…";
    public string Change => _days == 0 ? string.Empty : _data.PreviousMinutes == 0
        ? (_data.Minutes > 0 ? "前一周期没有记录" : "本期暂无游玩记录")
        : $"较前 {_days} 天 {(_data.Minutes >= _data.PreviousMinutes ? "↑" : "↓")} {Math.Abs(_data.Minutes / (double)_data.PreviousMinutes - 1):P0}";
    public string TrendCaption => _days == 0 ? "最近 12 个月" : "每日时长 · 小时";
    public IReadOnlyList<DashboardBucket> Trend => _data.Trend;
    public IReadOnlyList<DashboardDay> Calendar => _data.Calendar;
    public double CalendarWidth => 34 + Math.Ceiling((Calendar.Count + (Calendar.Count == 0 ? 0 : ((int)Calendar[0].Date.DayOfWeek + 6) % 7)) / 7d) * 17;
    public double RankingHeight => Math.Clamp(_data.Games.Count * 57 + 34, 120, 650);
    public string FavouriteShareLabel => $"占本期 {FavouriteShare:P1}";
    public double FavouriteBarWidth => FavouriteShare * 180;
    public string FavouriteName => _data.Games.FirstOrDefault()?.Name ?? "暂无记录";
    public string FavouriteInitial => _data.Games.FirstOrDefault() is { } game && !string.IsNullOrEmpty(game.Name)
        ? StringInfo.GetNextTextElement(game.Name) : "?";
    public ImageSource? FavouriteArtwork => _favouriteArtwork;
    public bool HasFavourite => _data.Games.Count > 0;
    public string FavouriteTime => _data.Games.FirstOrDefault() is { } game ? Duration(game.Minutes) : string.Empty;
    public double FavouriteShare => _data.Minutes == 0 ? 0 : (_data.Games.FirstOrDefault()?.Minutes ?? 0) / (double)_data.Minutes;
    public string Longest => _data.Sessions == 0 ? "暂无记录" : Duration(_data.LongestSessionMinutes);
    public IReadOnlyList<StatisticsSessionRow> Recent => _recent;
    public string RecentTitle => _sessionName ?? "游玩节奏";
    public string FocusClearLabel => _sessionKey is null && _focusDate is null ? "全部游戏" : "清除筛选";
    public string RangeSummary => _days == 0 ? "全部历史" : $"统计范围 {_days} 天";
    public string FocusCaption => (_focusDate is { } date ? $"{date:yyyy/MM/dd} · 当日汇总" : "当前时段 · 汇总") + " · 按结束日";
    public string FocusHours => (_data.Rhythm.Minutes / 60d).ToString("N1", CultureInfo.CurrentCulture);
    public string FocusActiveDays => $"{_data.Rhythm.ActiveDays} 天";
    public string FocusAverage => _data.Rhythm.ActiveDays == 0 ? "暂无记录" : Duration(_data.Rhythm.Minutes / _data.Rhythm.ActiveDays);
    public string FocusSessions => $"{_data.Rhythm.Sessions} 次";
    public string FocusLongest => _data.Rhythm.Sessions == 0 ? "暂无记录" : Duration(_data.Rhythm.LongestSessionMinutes);
    public DateOnly? FocusDate => _focusDate;
    public IReadOnlyList<WeekdayRow> Weekdays => _data.Rhythm.WeekdayMinutes.Select((minutes, index) => new WeekdayRow(
        "周" + "一二三四五六日"[index], Duration(minutes), minutes / (double)Math.Max(1, _data.Rhythm.WeekdayMinutes.Max()) * 100)).ToList();
    public string PageLabel => _data.RecentTotal == 0 ? "暂无记录" : $"{_data.Offset + 1}–{_data.Offset + _data.Recent.Count} / {_data.RecentTotal}";
    public string RankingCaption
    {
        get
        {
            if (_data.Games.Count == 0) return string.Empty;
            return _data.GamesPlayed > _data.Games.Count ? $"显示前 {_data.Games.Count} 款 · 点击游戏查看汇总" : "点击游戏查看汇总";
        }
    }

    public Task EnsureLoadedAsync() => _loaded ? Task.CompletedTask : RefreshAsync();
    public Task SetRangeAsync(int days) { _days = days; _offset = 0; _focusDate = null; Changed(nameof(Days)); return RefreshAsync(); }
    public Task SelectGameAsync(StatisticsGameRow? game)
    {
        if (game is null) { _sessionKey = null; _sessionName = null; _focusDate = null; }
        else { _sessionKey = _sessionKey == game.DataKey ? null : game.DataKey; _sessionName = _sessionKey is null ? null : game.Name; }
        _offset = 0;
        return RefreshAsync();
    }
    public Task SelectDateAsync(DateOnly date)
    {
        _focusDate = _focusDate == date ? null : date;
        return RefreshAsync();
    }
    private Task PageAsync(int direction) { _offset = Math.Max(0, _data.Offset + direction * _data.PageSize); return RefreshAsync(); }

    /// <summary>Picks up posters the library loaded after the dashboard data arrived; rows update in place.</summary>
    public void RefreshArtwork()
    {
        if (_disposed) return;
        foreach (var row in _games) row.Artwork = ArtworkFor(row.DataKey);
        foreach (var row in _recent) row.Artwork = ArtworkFor(row.DataKey);
        var favourite = ArtworkFor(_data.Games.FirstOrDefault()?.DataKey);
        if (ReferenceEquals(favourite, _favouriteArtwork)) return;
        _favouriteArtwork = favourite;
        Changed(nameof(FavouriteArtwork));
    }

    private ImageSource? ArtworkFor(string? dataKey) => dataKey is null || _artwork is null ? null : _artwork(dataKey);

    public async Task RefreshAsync()
    {
        if (_disposed) return;
        _pending?.Cancel(); _pending?.Dispose();
        var pending = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _pending = pending;
        IsBusy = true;
        try
        {
            var response = await _library.ExecuteAsync(new GameLibraryRequest
            { Operation = GameLibraryOperation.Statistics, Statistics = new PlaytimeDashboardQuery(_days, _sessionKey, _offset, _focusDate, IncludeRecent: false) }, pending.Token);
            if (pending.IsCancellationRequested || _pending != pending || _disposed) return;
            if (!response.Success || response.Statistics is null) throw new InvalidOperationException(response.Error ?? "无法读取历史统计。");
            _data = response.Statistics;
            _appliedQuery = new PlaytimeDashboardQuery(_days, _sessionKey, _data.Offset, _focusDate, IncludeRecent: false);
            _offset = _data.Offset;
            _loaded = true;
            _games.Clear();
            foreach (var (game, index) in _data.Games.Select((game, index) => (game, index)))
                _games.Add(new StatisticsGameRow(index + 1, game.DataKey, game.Name, game.Minutes, game.Sessions, game.LastPlayed,
                    _data.Minutes == 0 ? 0 : game.Minutes / (double)_data.Minutes, ArtworkFor(game.DataKey)));
            _recent = _data.Recent.Select(row => new StatisticsSessionRow(row.DataKey, row.Name, row.Start, row.End, row.Minutes, ArtworkFor(row.DataKey))).ToList();
            foreach (var row in _games) ApplyJournal(row);
            RefreshFilter();
            _favouriteArtwork = ArtworkFor(_data.Games.FirstOrDefault()?.DataKey);
            Status = PolicyNote;
            foreach (var property in new[] { nameof(FocusClearLabel), nameof(RangeSummary), nameof(CalendarWidth), nameof(RankingHeight), nameof(FavouriteShareLabel), nameof(FavouriteBarWidth), nameof(HasHistory), nameof(TotalHours), nameof(SessionCount), nameof(ActiveDays), nameof(GamesPlayed), nameof(Average),
                nameof(DateRange), nameof(Change), nameof(TrendCaption), nameof(Trend), nameof(Calendar), nameof(FavouriteName), nameof(FavouriteInitial),
                nameof(FavouriteArtwork), nameof(HasFavourite), nameof(FavouriteTime), nameof(FavouriteShare), nameof(Longest), nameof(Recent), nameof(RecentTitle),
                nameof(PageLabel), nameof(RankingCaption), nameof(FocusCaption), nameof(FocusHours), nameof(FocusActiveDays), nameof(FocusAverage), nameof(FocusSessions), nameof(FocusLongest), nameof(Weekdays), nameof(FocusDate) }) Changed(property);
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested || _disposed) { }
        catch (Exception)
        {
            if (_pending == pending && !_disposed)
            {
                _days = _appliedQuery.Days;
                _sessionKey = _appliedQuery.SessionGameKey;
                _focusDate = _appliedQuery.FocusDate;
                _sessionName = _data.Games.FirstOrDefault(game => game.DataKey.Equals(_sessionKey, StringComparison.OrdinalIgnoreCase))?.Name;
                _offset = _data.Offset;
                Changed(nameof(Days));
                Status = "统计暂不可用，请刷新重试。";
            }
        }
        finally { if (_pending == pending && !_disposed) IsBusy = false; }
    }

    private AsyncCommand Command(Func<Task> action, Func<bool> canExecute)
    {
        var command = new AsyncCommand(action, () => !_disposed && canExecute(), ex => Status = ex.Message);
        _commands.Add(command); return command;
    }
    private void UpdateCommands() { foreach (var command in _commands) command.Refresh(); }
    public static string Duration(long minutes) => minutes == 0 ? "不足 1 分钟" : minutes < 60 ? $"{minutes} 分钟"
        : minutes % 60 == 0 ? $"{minutes / 60} 小时" : $"{minutes / 60} 小时 {minutes % 60} 分";
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _journal.Saved -= OnJournalSaved;
        _pending?.Cancel(); _pending?.Dispose(); _pending = null;
        _lifetime.Cancel(); _lifetime.Dispose();
    }
}

public sealed record WeekdayRow(string Day, string Time, double Width);
