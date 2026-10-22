using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Desktop;

public sealed record StatisticsGameRow(int Rank, string DataKey, string Name, long Minutes, int Sessions, DateTime LastPlayed, double Share)
{
    public string RankLabel => Rank.ToString("00", CultureInfo.InvariantCulture);
    public string Initial => string.IsNullOrEmpty(Name) ? "G" : StringInfo.GetNextTextElement(Name);
    public string Time => StatisticsViewModel.Duration(Minutes);
    public string ShareLabel => Share.ToString("P0", CultureInfo.CurrentCulture);
    public double BarWidth => Math.Clamp(Share * 180, 2, 180);
    public string LastLabel => LastPlayed.ToString("yy/MM/dd");
}

public sealed record StatisticsSessionRow(string Name, DateTime Start, DateTime End, long Minutes)
{
    public string DateLabel => End.ToString("MM/dd");
    public string StartLabel => Start.ToString("MM/dd HH:mm");
    public string Time => StatisticsViewModel.Duration(Minutes);
    public string Detail => $"{Start:yyyy/MM/dd HH:mm} → {End:yyyy/MM/dd HH:mm}";
}

/// <summary>Independent read-only dashboard state; changing pages never touches game drafts or monitoring.</summary>
public sealed class StatisticsViewModel : ObservableState, IDisposable
{
    private const string PolicyNote = "仅统计已结束的会话 · 含已移出游戏库的历史";
    private readonly IGameLibrary _library;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _pending;
    private readonly List<AsyncCommand> _commands = [];
    private readonly ObservableCollection<StatisticsGameRow> _games = [];
    private PlaytimeDashboard _data = new();
    private int _days = 30;
    private string? _sessionKey;
    private string? _sessionName;
    private int _offset;
    private string _search = string.Empty;
    private string _status = PolicyNote;
    private bool _busy;
    private bool _loaded;
    private bool _disposed;
    private PlaytimeDashboardQuery _appliedQuery = new();

    public StatisticsViewModel(IGameLibrary library)
    {
        _library = library;
        Games = CollectionViewSource.GetDefaultView(_games);
        Games.Filter = value => ((StatisticsGameRow)value).Name.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase)
            || ((StatisticsGameRow)value).DataKey.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase);
        RefreshCommand = Command(RefreshAsync, () => !IsBusy);
        WeekCommand = Command(() => SetRangeAsync(7), () => true);
        MonthCommand = Command(() => SetRangeAsync(30), () => true);
        QuarterCommand = Command(() => SetRangeAsync(90), () => true);
        AllCommand = Command(() => SetRangeAsync(0), () => true);
        PreviousCommand = Command(() => PageAsync(-1), () => !IsBusy && _data.Offset > 0);
        NextCommand = Command(() => PageAsync(1), () => !IsBusy && _data.Offset + _data.PageSize < _data.RecentTotal);
        ClearGameCommand = Command(() => SelectGameAsync(null), () => !IsBusy && _sessionKey is not null);
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
    public string Search { get => _search; set { if (Set(ref _search, value)) Games.Refresh(); } }
    public int Days => _days;
    public string TotalHours => (_data.Minutes / 60d).ToString("N1", CultureInfo.CurrentCulture);
    public string SessionCount => _data.Sessions.ToString("N0", CultureInfo.CurrentCulture);
    public string ActiveDays => _data.ActiveDays.ToString("N0", CultureInfo.CurrentCulture);
    public string GamesPlayed => _data.GamesPlayed.ToString("N0", CultureInfo.CurrentCulture);
    public string Average => _data.Sessions == 0 ? "暂无单次记录" : $"平均单次 {Duration(_data.Minutes / _data.Sessions)}";
    public string DateRange => _loaded ? $"{_data.Start:yyyy.MM.dd} — {_data.Today:yyyy.MM.dd}" : "正在读取历史记录…";
    public string Change => _days == 0 ? string.Empty : _data.PreviousMinutes == 0
        ? (_data.Minutes > 0 ? "前一周期没有记录" : "本期暂无游玩记录")
        : $"较前 {_days} 天 {(_data.Minutes >= _data.PreviousMinutes ? "↑" : "↓")} {Math.Abs(_data.Minutes / (double)_data.PreviousMinutes - 1):P0}";
    public string TrendCaption => _days == 0 ? "最近 12 个月" : string.Empty;
    public IReadOnlyList<DashboardBucket> Trend => _data.Trend;
    public IReadOnlyList<DashboardDay> Calendar => _data.Calendar;
    public string FavouriteName => _data.Games.FirstOrDefault()?.Name ?? "暂无记录";
    public string FavouriteTime => _data.Games.FirstOrDefault() is { } game ? Duration(game.Minutes) : string.Empty;
    public double FavouriteShare => _data.Minutes == 0 ? 0 : (_data.Games.FirstOrDefault()?.Minutes ?? 0) / (double)_data.Minutes;
    public string Longest => _data.Sessions == 0 ? "暂无记录" : Duration(_data.LongestSessionMinutes);
    public IReadOnlyList<StatisticsSessionRow> Recent => _data.Recent.Select(row => new StatisticsSessionRow(row.Name, row.Start, row.End, row.Minutes)).ToList();
    public string RecentTitle => _sessionName is null ? "逐次游玩记录" : $"记录 · {_sessionName}";
    public string PageLabel => _data.RecentTotal == 0 ? "暂无记录" : $"{_data.Offset + 1}–{_data.Offset + _data.Recent.Count} / {_data.RecentTotal}";
    public string RankingCaption
    {
        get
        {
            if (_data.Games.Count == 0) return string.Empty;
            return _data.GamesPlayed > _data.Games.Count ? $"显示前 {_data.Games.Count} 款 · 点击游戏查看记录" : "点击游戏查看记录";
        }
    }

    public Task EnsureLoadedAsync() => _loaded ? Task.CompletedTask : RefreshAsync();
    public Task SetRangeAsync(int days) { _days = days; _offset = 0; Changed(nameof(Days)); return RefreshAsync(); }
    public Task SelectGameAsync(StatisticsGameRow? game)
    {
        _sessionKey = game?.DataKey; _sessionName = game?.Name; _offset = 0;
        return RefreshAsync();
    }
    private Task PageAsync(int direction) { _offset = Math.Max(0, _data.Offset + direction * _data.PageSize); return RefreshAsync(); }

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
            { Operation = GameLibraryOperation.Statistics, Statistics = new PlaytimeDashboardQuery(_days, _sessionKey, _offset) }, pending.Token);
            if (pending.IsCancellationRequested || _pending != pending || _disposed) return;
            if (!response.Success || response.Statistics is null) throw new InvalidOperationException(response.Error ?? "无法读取历史统计。");
            _data = response.Statistics;
            _appliedQuery = new PlaytimeDashboardQuery(_days, _sessionKey, _data.Offset);
            _offset = _data.Offset;
            _loaded = true;
            _games.Clear();
            foreach (var (game, index) in _data.Games.Select((game, index) => (game, index)))
                _games.Add(new StatisticsGameRow(index + 1, game.DataKey, game.Name, game.Minutes, game.Sessions, game.LastPlayed,
                    _data.Minutes == 0 ? 0 : game.Minutes / (double)_data.Minutes));
            Status = PolicyNote;
            foreach (var property in new[] { nameof(HasHistory), nameof(TotalHours), nameof(SessionCount), nameof(ActiveDays), nameof(GamesPlayed), nameof(Average),
                nameof(DateRange), nameof(Change), nameof(TrendCaption), nameof(Trend), nameof(Calendar), nameof(FavouriteName),
                nameof(FavouriteTime), nameof(FavouriteShare), nameof(Longest), nameof(Recent), nameof(RecentTitle), nameof(PageLabel), nameof(RankingCaption) }) Changed(property);
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested || _disposed) { }
        catch (Exception ex)
        {
            if (_pending == pending && !_disposed)
            {
                _days = _appliedQuery.Days;
                _sessionKey = _appliedQuery.SessionGameKey;
                _sessionName = _data.Games.FirstOrDefault(game => game.DataKey.Equals(_sessionKey, StringComparison.OrdinalIgnoreCase))?.Name;
                _offset = _data.Offset;
                Changed(nameof(Days));
                Status = "统计暂不可用，请刷新重试：" + ex.Message;
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
    public static string Duration(long minutes) => minutes == 0 ? "不足 1 分钟" : minutes < 60 ? $"{minutes} 分钟" : $"{minutes / 60d:0.#} 小时";
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pending?.Cancel(); _pending?.Dispose(); _pending = null;
        _lifetime.Cancel(); _lifetime.Dispose();
    }
}
