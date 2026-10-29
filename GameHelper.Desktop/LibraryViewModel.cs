using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Desktop;

/// <summary>Desktop state; all persisted changes and launch ownership remain in the running host.</summary>
public sealed class LibraryViewModel : ObservableState, IDisposable
{
    private readonly IGameLibrary _library;
    internal IGameLibrary Library => _library;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<AsyncCommand> _commands = [];
    private readonly ObservableCollection<LibraryGameItem> _games = [];
    private readonly Dictionary<string, LibraryGameItem> _gameIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly LibraryDraftStore? _draftStore;
    private readonly SteamPosterLibrary _posters;
    private readonly LibraryGenreStore _genreStore;
    private readonly LibraryJournalStore _journalStore;
    private readonly LibraryScanStore _scanStore;
    private readonly GameHelper.Infrastructure.Artwork.SteamGenreClient _genreClient;
    private static readonly System.Net.Http.HttpClient GenreHttp = new(new System.Net.Http.SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
    private readonly bool _allowGenreNetwork;
    private readonly GameHelper.Infrastructure.Storage.Sqlite.SqliteControllerSupportStore _controllerStore;
    private readonly GameHelper.Infrastructure.Artwork.SteamControllerSupportClient _controllerClient;
    private readonly string? _controllerConfig;
    private string? _genre;
    public ObservableCollection<GenreGroup> GenreGroups { get; } = [];
    public LibraryGameItem? ContinueGame => _genre is null ? _games.Where(game => game.InstallationState == GameInstallationState.Installed && (game.IsRunning || game.LastPlayedAt is not null)).OrderByDescending(game => game.IsRunning).ThenByDescending(game => game.LastPlayedAt).FirstOrDefault() : null;
    public bool HasContinueGame => ContinueGame is not null;
    public IReadOnlyList<DashboardBucket> WeeklyTrend { get; private set; } = [];
    private string _weeklyHours = "0.0";
    public string WeeklyHours { get => _weeklyHours; private set => Set(ref _weeklyHours, value); }
    public string CatalogCount => _games.Count.ToString("00");
    public string CollectionTitle => _genre ?? "我的游戏";
    public string CollectionCaption => _genre is null ? "每一段冒险，都值得珍藏。" : "按游戏类型浏览 · 一款游戏可以属于多个类型";
    private Dictionary<string, LibraryDraft> _restoredDrafts = new(StringComparer.OrdinalIgnoreCase);
    private LibraryGameItem? _selected;
    private bool _busy;
    private bool _refreshing;
    private long _generation;
    private bool _connected;
    private string _actionError = string.Empty;
    private string _status = "正在连接游戏监控…";
    private string _search = string.Empty;
    private string _filter = "all";
    private string _group = "installed";
    private string _configPath = string.Empty;
    private readonly DispatcherTimer? _elapsedTimer;
    private readonly TimeProvider _timeProvider;

    public LibraryViewModel(IGameLibrary library, string? configPath = null, string? metadataConfigPath = null) : this(library, configPath, null, metadataConfigPath: metadataConfigPath) { }

    internal LibraryViewModel(IGameLibrary library, string? configPath, SteamPosterLibrary? posters, TimeProvider? timeProvider = null, string? metadataConfigPath = null, GameHelper.Infrastructure.Artwork.SteamGenreClient? genreClient = null, DesktopSideData? sideData = null)
    {
        _library = library;
        _controllerConfig = configPath ?? metadataConfigPath;
        _controllerStore = new(new(), timeProvider ?? TimeProvider.System);
        _controllerClient = new(GenreHttp, _controllerStore, timeProvider ?? TimeProvider.System);
        _genreStore = sideData?.Genres ?? new LibraryGenreStore(configPath ?? metadataConfigPath);
        _scanStore = sideData?.Scan ?? new LibraryScanStore(configPath);
        _allowGenreNetwork = configPath is not null && (posters?.AllowNetwork ?? true);
        _genreClient = genreClient ?? new(GenreHttp, Path.Combine(GameHelper.Core.Utilities.AppDataPath.GetGameHelperDirectory(), "cache", "steam-genres"));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _journalStore = sideData?.Journal ?? new LibraryJournalStore(configPath ?? metadataConfigPath);
        _journalStore.Saved += OnJournalSaved;
        Statistics = new StatisticsViewModel(library, FindArtwork, _journalStore, _timeProvider);
        _posters = posters ?? sideData?.Posters ?? new SteamPosterLibrary(configPath ?? metadataConfigPath, allowNetwork: configPath is not null);
        if (configPath is not null)
        {
            _draftStore = sideData?.DraftStore ?? new LibraryDraftStore(configPath);
            try { _restoredDrafts = (sideData?.Drafts ?? _draftStore.Read()).ToDictionary(draft => draft.Baseline.Config.DataKey, StringComparer.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { _status = "上次未保存的修改未能读取，已忽略。"; }
            if (sideData?.DraftError is { } draftError) _status = draftError;
        }
        Games = CollectionViewSource.GetDefaultView(_games);
        Games.Filter = Matches;
        RefreshCommand = Command(() => RefreshAsync(), () => !IsBusy);
        ReloadSelectedCommand = Command(() => RunAsync(ReloadSelectedAsync), () => Ready && Selected is not null);
        SaveCommand = Command(() => RunAsync(SaveSelectedAsync), () => Ready && Selected?.IsDirty == true);
        LaunchCommand = Command(() => RunAsync(async () =>
        {
            var targetKey = Selected?.DataKey;
            if (targetKey is null) return;
            if (Selected?.IsDirty == true) await SaveSelectedAsync();
            LaunchWindowHandoff.AllowForeground();
            await SendAsync(new GameLibraryRequest { Operation = GameLibraryOperation.Launch, DataKey = targetKey });
            LaunchWindowHandoff.Minimize();
        }), () => Ready && Selected is not null && !string.IsNullOrWhiteSpace(Selected.Target) && !Selected.IsRunning && Selected.State != "启动中");
        // 秒级计时时钟只在 UI 线程泵下跳；测试线程可直接调 OnClockTick 驱动。
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => OnClockTick();
        _elapsedTimer.Start();
    }

    public ICollectionView Games { get; }
    public StatisticsViewModel Statistics { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ReloadSelectedCommand { get; }
    public string ActionError { get => _actionError; private set { if (Set(ref _actionError, value)) Changed(nameof(HasActionError)); } }
    public bool HasActionError => !string.IsNullOrEmpty(ActionError);
    public ICommand LaunchCommand { get; }
    /// <summary>Window hand-off used around a launch; attached by the window and replaceable in tests.</summary>
    internal ILaunchWindowHandoff LaunchWindowHandoff { get; set; } = NoopLaunchWindowHandoff.Instance;
    /// <summary>Double-clicking a poster uses the launch button's command, including all disabled cases.</summary>
    internal void LaunchFromPoster()
    {
        if (LaunchCommand.CanExecute(null)) LaunchCommand.Execute(null);
    }
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) { Changed(nameof(Ready)); RefreshCommands(); } } }
    public bool Connected { get => _connected; private set { if (Set(ref _connected, value)) { Changed(nameof(Ready)); RefreshCommands(); } } }
    public bool Ready => Connected && !IsBusy;
    public string Status { get => _status; set => Set(ref _status, value); }
    public string ConfigPath { get => _configPath; private set => Set(ref _configPath, value); }
    public string Count => $"{Games.Cast<object>().Count()} 款收藏";
    public string InstallationSummary => $"本机 {_games.Count(game => game.InstallationState == GameInstallationState.Installed)} · 未找到 {_games.Count(game => game.InstallationState == GameInstallationState.Missing)} · 待确认 {_games.Count(game => game.InstallationState == GameInstallationState.Unknown)}";
    public string InstallationGroup => _group;
    public string Search { get => _search; set { if (Set(ref _search, value)) Filter(); } }
    public LibraryGameItem? Selected { get => _selected; set { if (Set(ref _selected, value)) { ActionError = string.Empty; Changed(nameof(SelectedJournalSummary)); } RefreshCommands(); } }
    public string SelectedJournalSummary => Selected is null ? string.Empty : JournalSummary(_journalStore.Read(Selected.DataKey));
    internal Task<JournalEditorViewModel?> EditSelectedJournalAsync()
    {
        var selected = Selected;
        return selected is null ? Task.FromResult<JournalEditorViewModel?>(null) : LoadJournalAsync(selected);
    }
    private async Task<JournalEditorViewModel?> LoadJournalAsync(LibraryGameItem selected) =>
        await JournalEditorViewModel.LoadAsync(_journalStore, selected.DataKey, selected.Name, _timeProvider);
    internal JournalEditorViewModel? EditSelectedJournal() => Selected is null ? null : new(_journalStore, Selected.DataKey, Selected.Name, _timeProvider);
    private void OnJournalSaved(object? sender, EventArgs e) => Changed(nameof(SelectedJournalSummary));
    internal static string JournalSummary(JournalRecord record) => record.Status
        + (record.CompletedOn is { } date ? $" · 通关 {date:yyyy/MM/dd}" : string.Empty)
        + (record.Note.Length > 0 ? "\n" + record.Note : "\n还没有留下短评");
    public bool HasUnsavedChanges => _games.Any(game => game.IsDirty);

    private AsyncCommand Command(Func<Task> action, Func<bool> canExecute)
    {
        var command = new AsyncCommand(action, canExecute, Report);
        _commands.Add(command);
        return command;
    }

    private void RefreshCommands() { foreach (var command in _commands) command.Refresh(); }
    private void Report(Exception ex)
    {
        Status = ex is IOException or TimeoutException or OperationCanceledException
            ? "未能完成与监控进程的通信。请确认监控在运行后重试。"
            : ex.Message;
    }

    public void SetGenre(string? genre) { _genre = genre; Changed(nameof(CollectionTitle)); Changed(nameof(CollectionCaption)); Changed(nameof(ContinueGame)); Changed(nameof(HasContinueGame)); Filter(); }
    private static int GenreOrder(string genre) => genre switch
    { "动作" => 0, "冒险" => 1, "角色扮演" => 2, "策略" => 3, "模拟" => 4, "独立" => 5, "未分类" => int.MaxValue, _ => 6 };
    private void RebuildGenres()
    {
        var groups = _games.SelectMany(game => game.Genres.Count == 0 ? new[] { "未分类" } : game.Genres)
            .Append("未分类").GroupBy(genre => genre, StringComparer.OrdinalIgnoreCase).OrderBy(group => GenreOrder(group.Key)).ThenBy(group => group.Key)
            .Select(group => new GenreGroup(group.Key, group.Count() - (group.Key == "未分类" ? 1 : 0))).ToList();
        if (!GenreGroups.SequenceEqual(groups)) { GenreGroups.Clear(); foreach (var group in groups) GenreGroups.Add(group); }
    }
    internal async Task LoadGenresAsync(LibraryGameItem game)
    {
        var version = ++game.GenreVersion;
        game.GenreIdentity = (game.Executable, game.Target);
        if (_genreStore.TryRead(game.DataKey, out var manual)) { game.SetGenres(manual); return; }
        try
        {
            var key = game.DataKey; var executable = game.Executable; var target = game.Target;
            var linked = _posters.AssociatedAppId(key);
            var id = linked ?? await Task.Run(() => GameHelper.Infrastructure.Artwork.SteamArtworkIdentity.Resolve(executable, target), _lifetime.Token);
            var genres = id is { } appId ? await _genreClient.GetAsync(appId, _allowGenreNetwork, _lifetime.Token) : [];
            if (_lifetime.IsCancellationRequested || version != game.GenreVersion) return;
            game.SetGenres(_genreStore.TryRead(key, out manual) ? manual : genres);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status = "类型暂不可用，将继续使用本地分类。"; }
    }
    private async Task CarryGenresAsync(LibraryGameItem previous, LibraryGameItem current)
    {
        var version = current.GenreVersion;
        await previous.GenresLoading;
        if (!_lifetime.IsCancellationRequested && current.GenreVersion == version) current.SetGenres(previous.Genres);
    }
    internal async Task SaveGenresAsync(LibraryGameItem game, string[]? genres)
    {
        await Task.Run(() => _genreStore.Save(game.DataKey, genres));
        // Reset is immediate; network work belongs to the window lifetime, not the editor dialog.
        if (genres is null) game.SetGenres([]);
        game.GenresLoading = LoadGenresAsync(game);
        _ = LoadControllerAsync(game);
    }
    internal async Task LoadControllerAsync(LibraryGameItem game)
    {
        var version = ++game.ControllerVersion;
        var target = game.Target;
        var linked = _posters.AssociatedAppId(game.DataKey);
        uint? id = Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme == "steam" && uri.Host == "rungameid"
            && uint.TryParse(uri.AbsolutePath.Trim('/'), out var parsed) && parsed > 0 ? parsed : linked;
        try
        {
            var initial = await Task.Run(() =>
                (id is { } cachedId ? _controllerStore.Read(cachedId)?.Value : null) ?? new ControllerSupport(), _lifetime.Token);
            var manual = await Task.Run(() => _controllerConfig is null ? null : _controllerStore.Manual(_controllerConfig, game.DataKey), _lifetime.Token);
            if (game.ControllerVersion != version || _lifetime.IsCancellationRequested) return;
            game.SetController(initial with { Manual = manual }); Filter();
            var result = await Task.Run(async () =>
            {
                var value = id is { } app ? await _controllerClient.GetAsync(app, _allowGenreNetwork, _lifetime.Token) : new ControllerSupport();
                return value with { Manual = _controllerConfig is null ? null : _controllerStore.Manual(_controllerConfig, game.DataKey) };
            }, _lifetime.Token);
            if (!_lifetime.IsCancellationRequested && game.ControllerVersion == version) { game.SetController(result); Filter(); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        { Status = "手柄支持信息暂不可用：" + ex.Message; }
    }
    internal async Task SetControllerManualAsync(bool? value)
    {
        if (Selected is not { } game || _controllerConfig is null) return;
        try
        {
            await Task.Run(() => _controllerStore.SaveManual(_controllerConfig, game.DataKey, value));
            ++game.ControllerVersion;
            game.SetController(game.Controller with { Manual = value }); Filter();
        }
        catch (Exception ex) { Status = "手柄支持设置未保存：" + ex.Message; }
    }
    public void SetFilter(string filter) { _filter = filter; Filter(); }
    public void SetInstallationGroup(string group) { _group = group; Changed(nameof(InstallationGroup)); Filter(); }

    /// <summary>推进所有运行中卡片的本局计时；测试可在无消息泵的线程直接调用来模拟秒级时钟。</summary>
    internal void OnClockTick()
    {
        var now = _timeProvider.GetLocalNow().DateTime;
        foreach (var game in _games) game.OnSecondTick(now);
    }
    private bool Matches(object value)
    {
        var game = (LibraryGameItem)value;
        return (_group == "all" || (_group == "installed" && game.InstallationState == GameInstallationState.Installed)
                || (_group == "missing" && game.InstallationState == GameInstallationState.Missing) || (_group == "unknown" && game.InstallationState == GameInstallationState.Unknown))
            && (_genre is null || (_genre == "未分类" ? game.Genres.Count == 0 : game.Genres.Contains(_genre, StringComparer.OrdinalIgnoreCase)))
            && (_filter != "unsupported" || game.Controller.Unsupported)
            && (_filter != "hdr" || game.Hdr) && (_filter != "speed" || game.UseSpeed)
            && (game.Name.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase) || game.Executable.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase));
    }
    private void Filter()
    {
        Games.Refresh();
        var visible = Games.Cast<LibraryGameItem>().ToList();
        if (Selected is null || !visible.Contains(Selected)) Selected = visible.FirstOrDefault();
        Changed(nameof(Count));
        Changed(nameof(InstallationSummary));
    }

    public async Task RefreshAsync(bool discardDrafts = false)
    {
        if (IsBusy) return;
        if (discardDrafts)
        {
            await RunAsync(async () =>
            {
                await _draftWrite;
                var response = await _library.ExecuteAsync(new GameLibraryRequest { Operation = GameLibraryOperation.List }, _lifetime.Token);
                if (!response.Success) throw new InvalidOperationException(response.Error);
                var discardCurrent = HasUnsavedChanges;
                var latest = await Task.Run(() =>
                {
                    // Delete only with the editor's original expectation; never refresh the version before deleting.
                    if (discardCurrent) _draftStore?.Write([]);
                    return _draftStore?.Read() ?? [];
                });
                _restoredDrafts = latest.ToDictionary(d => d.Baseline.Config.DataKey, StringComparer.OrdinalIgnoreCase);
                _draftFailure = null; // These contents are the newly accepted durable snapshot, not a pending failed write.
                Apply(response, true);
            });
            return;
        }
        if (_refreshing || _lifetime.IsCancellationRequested) return;
        _refreshing = true; var generation = _generation;
        try
        {
            var response = await _library.ExecuteAsync(new GameLibraryRequest { Operation = GameLibraryOperation.List }, _lifetime.Token);
            if (!_lifetime.IsCancellationRequested && generation == _generation && !IsBusy) Apply(response);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (generation == _generation && !_lifetime.IsCancellationRequested) Report(ex); }
        finally { _refreshing = false; }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        var actionKey = Selected?.DataKey;
        _generation++;
        ActionError = string.Empty;
        IsBusy = true;
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Report(ex);
            if (string.Equals(actionKey, Selected?.DataKey, StringComparison.OrdinalIgnoreCase)) ActionError = Status;
        }
        finally { IsBusy = false; }
    }

    private async Task ReloadSelectedAsync()
    {
        if (Selected is null) return;
        var key = Selected.DataKey;
        var response = await _library.ExecuteAsync(new GameLibraryRequest { Operation = GameLibraryOperation.List }, _lifetime.Token);
        if (!response.Success) throw new InvalidOperationException(response.Error);
        _restoredDrafts.Remove(key);
        // Reload only the selected entry; never discard another game's pending settings.
        Apply(response, false, key);
    }

    private async Task SaveSelectedAsync()
    {
        if (Selected is null) return;
        var key = Selected.DataKey;
        await FlushDraftsAsync();
        var response = await _library.ExecuteAsync(new GameLibraryRequest
        {
            Operation = GameLibraryOperation.Save, DataKey = key,
            ExpectedRevision = Selected.Revision, Game = Selected.ToConfig()
        }, _lifetime.Token);
        if (!response.Success) throw new InvalidOperationException(response.Error);
        // Replace only the saved draft; other games may have unsaved changes.
        Apply(response, false, key);
    }

    private async Task SendAsync(GameLibraryRequest request)
    {
        var response = await _library.ExecuteAsync(request, _lifetime.Token);
        Apply(response);
    }

    private void Apply(GameLibraryResponse response, bool discardDrafts = false, string? selectedKey = null)
    {
        if (_lifetime.IsCancellationRequested) return;
        if (!response.Success) throw new InvalidOperationException(response.Error ?? "游戏库操作失败。");
        var key = selectedKey ?? Selected?.DataKey;
        var old = _games.ToDictionary(game => game.DataKey, StringComparer.OrdinalIgnoreCase);
        var updated = new List<LibraryGameItem>();
        foreach (var row in response.Games.Select((game, index) => (Game: game, Index: index))
            .OrderByDescending(item => LibraryGameItem.IsRunningState(item.Game.State))
            .ThenByDescending(item => Activity(item.Game))
            .ThenByDescending(item => item.Game.LastPlayedAt)
            .ThenByDescending(item => item.Index).Select(item => item.Game))
        {
            LibraryGameItem item;
            if (_restoredDrafts.Remove(row.Config.DataKey, out var restored))
            {
                item = LibraryGameItem.RestoreDraft(restored);
                item.RefreshRuntime(row);
                item.PropertyChanged += OnItemChanged;
                item.ArtworkLoading = item.LoadArtworkAsync(_posters, _lifetime.Token);
            }
            else if (!discardDrafts && !string.Equals(selectedKey, row.Config.DataKey, StringComparison.OrdinalIgnoreCase) && old.TryGetValue(row.Config.DataKey, out var draft) && (draft.IsDirty || draft.Revision == row.Revision))
            {
                item = draft;
                item.RefreshRuntime(row);
            }
            else
            {
                item = new LibraryGameItem(row);
                if (old.TryGetValue(row.Config.DataKey, out var previous) && previous.GenreIdentity == (item.Executable, item.Target))
                { item.SetGenres(previous.Genres); item.GenreIdentity = previous.GenreIdentity; }
                item.PropertyChanged += OnItemChanged;
                item.ArtworkLoading = item.LoadArtworkAsync(_posters, _lifetime.Token);
            }
            if (discardDrafts || !old.TryGetValue(row.Config.DataKey, out var existing) || existing.GenreIdentity != (item.Executable, item.Target))
                item.GenresLoading = LoadGenresAsync(item);
            else if (!ReferenceEquals(existing, item)) item.GenresLoading = CarryGenresAsync(existing, item);
            _ = LoadControllerAsync(item);
            updated.Add(item);
        }
        for (var index = 0; index < updated.Count; index++)
        {
            if (index < _games.Count && ReferenceEquals(_games[index], updated[index])) continue;
            var oldIndex = _games.IndexOf(updated[index]);
            if (oldIndex >= 0) _games.Move(oldIndex, index); else _games.Insert(index, updated[index]);
        }
        while (_games.Count > updated.Count) _games.RemoveAt(_games.Count - 1);
        _gameIndex.Clear();
        foreach (var game in _games) _gameIndex[game.DataKey] = game;
        if (!Games.Cast<LibraryGameItem>().SequenceEqual(_games.Where(item => Matches(item)))) Games.Refresh();
        RebuildGenres(); Changed(nameof(CatalogCount));
        Changed(nameof(ContinueGame)); Changed(nameof(HasContinueGame));
        Changed(nameof(Count)); Changed(nameof(InstallationSummary));
        Selected = Games.Cast<LibraryGameItem>().FirstOrDefault(game => game.DataKey == key) ?? Games.Cast<LibraryGameItem>().FirstOrDefault();
        Connected = true;
        ConfigPath = response.ConfigPath;
        Status = response.Message ?? (response.MonitorRunning ? "监控已就绪 · 关闭游戏库后继续监控" : "监控未运行 · 启动游戏时会先开启监控");
        if (!discardDrafts) PersistDrafts();
    }

    private static DateTimeOffset Activity(LibraryGame game)
    {
        var played = game.LastPlayedAt ?? DateTimeOffset.MinValue;
        return game.Config.AddedAt is { } added && added > played ? added : played;
    }

    internal async Task LoadWeeklyAsync()
    {
        try
        {
            var response = await _library.ExecuteAsync(new GameLibraryRequest { Operation = GameLibraryOperation.Statistics,
                Statistics = new(7, IncludeRecent: false) }, _lifetime.Token);
            if (!_lifetime.IsCancellationRequested && response.Success && response.Statistics is { } data)
                {
                    var monday = data.Today.AddDays(-(((int)data.Today.DayOfWeek + 6) % 7));
                    WeeklyTrend = Enumerable.Range(0, 7).Select(index =>
                    {
                        var date = monday.AddDays(index);
                        return new DashboardBucket(date.ToString("MM/dd"), date, date, data.Trend.FirstOrDefault(bucket => bucket.Start == date)?.Minutes ?? 0);
                    }).ToList();
                    WeeklyHours = (WeeklyTrend.Sum(bucket => bucket.Minutes) / 60d).ToString("0.0"); Changed(nameof(WeeklyTrend));
                }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException) { }
    }

    public async Task AddAsync(string executable)
    {
        await RunAsync(() => SendAsync(new GameLibraryRequest
        {
            Operation = GameLibraryOperation.Add,
            Game = new GameConfig { Executable = executable, DisplayName = Path.GetFileNameWithoutExtension(executable), Launch = new GameLaunchOptions { Target = executable } }
        }));
    }

    public async Task RemoveAsync()
    {
        if (Selected is null) return;
        await RunAsync(() => SendAsync(new GameLibraryRequest { Operation = GameLibraryOperation.Remove, DataKey = Selected.DataKey }));
    }

    public async Task<IReadOnlyList<SteamInstalledGame>> DiscoverAsync()
    {
        IReadOnlyList<SteamInstalledGame> games = [];
        await RunAsync(async () =>
        {
            Status = "正在扫描本机 Steam 游戏库…";
            var response = await _library.ExecuteAsync(new GameLibraryRequest { Operation = GameLibraryOperation.DiscoverSteam }, _lifetime.Token);
            if (!response.Success) throw new InvalidOperationException(response.Error);
            games = response.SteamGames;
            Status = $"扫描完成：发现 {games.Count} 款已安装游戏。";
        });
        return games;
    }

    public Task ImportAsync(string[] ids) => RunAsync(() => SendAsync(new GameLibraryRequest { Operation = GameLibraryOperation.ImportSteam, SteamAppIds = ids }));
    public async Task<LocalGameScan?> DiscoverDirectoryAsync(string directory)
    {
        LocalGameScan? scan = null;
        await RunAsync(async () =>
        {
            Status = "正在读取目录，识别候选游戏程序…";
            var response = await _library.ExecuteAsync(new GameLibraryRequest { Operation = GameLibraryOperation.DiscoverDirectory, Directory = directory }, _lifetime.Token);
            if (!response.Success) throw new InvalidOperationException(response.Error);
            scan = response.LocalScan;
            Status = $"扫描完成：{scan?.Candidates.Count ?? 0} 个候选程序，请勾选需要加入的游戏。";
            if (scan is not null) await SaveScanMemoryAsync(() => _scanStore.Remember(scan.Root));
        });
        return scan;
    }
    internal IReadOnlyList<string> RecentScanDirectories => _scanStore.Directories;
    internal IReadOnlyCollection<string> IgnoredScanPrograms => _scanStore.Ignored;
    internal async void SetScanIgnored(IReadOnlyList<string> executables, bool ignored) => await SaveScanMemoryAsync(() => _scanStore.SetIgnored(executables, ignored));
    /// <summary>Rescan a remembered root; a root that no longer exists is dropped from the list.</summary>
    public async Task<LocalGameScan?> RescanDirectoryAsync(string directory)
    {
        if (Directory.Exists(directory)) return await DiscoverDirectoryAsync(directory);
        await SaveScanMemoryAsync(() => _scanStore.Forget(directory));
        Status = $"目录已不存在，已从最近扫描中移除：{directory}";
        return null;
    }
    // Scan memory is a convenience; failing to persist it must not fail the scan itself.
    private async Task SaveScanMemoryAsync(Action save)
    {
        try { await _sideWrites.WaitAsync(); try { await Task.Run(save); } finally { _sideWrites.Release(); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { Status += " 扫描记录未能保存：" + ex.Message; }
    }
    public Task ImportDirectoryAsync(IReadOnlyList<LocalGameCandidate> games) => RunAsync(() => SendAsync(new GameLibraryRequest { Operation = GameLibraryOperation.ImportDirectory, LocalGames = games }));
    internal Task<IReadOnlyList<GameHelper.Infrastructure.Artwork.SteamArtworkGame>> SearchPostersAsync(string query, CancellationToken cancellationToken) => _posters.SearchAsync(query, cancellationToken);
    internal IReadOnlyList<LibraryGameItem> MissingPosters(bool includeUnavailable) => _games.Where(game => !game.ArtworkIsCover
        && (includeUnavailable || game.InstallationState == GameInstallationState.Installed)).ToList();
    internal bool HasPosterAssociation(LibraryGameItem game) => _posters.HasAssociation(game.DataKey);
    internal async Task<bool> AutoMatchPosterAsync(LibraryGameItem game, CancellationToken cancellationToken)
    {
        if (!await _posters.MatchNameAsync(game.DataKey, game.Name, cancellationToken)) return false;
        // A short-lived dialog only cancels its wait. Shared downloads belong to this library window.
        game.ArtworkLoading = game.LoadArtworkAsync(_posters, _lifetime.Token);
        await game.ArtworkLoading.WaitAsync(cancellationToken);
        game.GenresLoading = LoadGenresAsync(game);
        _ = LoadControllerAsync(game);
        return true;
    }
    internal Task<System.Windows.Media.ImageSource?> PreviewPosterAsync(uint appId, CancellationToken cancellationToken)
        => _posters.PreviewAsync(appId, _lifetime.Token).WaitAsync(cancellationToken);
    internal Task WaitForArtworkAsync() => Task.WhenAll(_games.Select(game => game.ArtworkLoading));
    internal int PosterCount => _games.Count(game => game.ArtworkIsCover);
    internal async Task AssociatePosterAsync(LibraryGameItem game, uint? appId, bool refresh = true)
    {
        await Task.Run(() => _posters.Associate(game.DataKey, appId));
        await game.LoadArtworkAsync(_posters, _lifetime.Token, refresh);
        game.GenresLoading = LoadGenresAsync(game);
        _ = LoadControllerAsync(game);
    }
    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshCommands();
        if (e.PropertyName == nameof(LibraryGameItem.Genres)) { RebuildGenres(); Filter(); }
        if (e.PropertyName is nameof(LibraryGameItem.Artwork) or nameof(LibraryGameItem.ArtworkIsCover)) Statistics.RefreshArtwork();
        if (e.PropertyName is nameof(LibraryGameItem.Name) or nameof(LibraryGameItem.Executable) or nameof(LibraryGameItem.Target)
            or nameof(LibraryGameItem.Arguments) or nameof(LibraryGameItem.Directory) or nameof(LibraryGameItem.Speed)
            or nameof(LibraryGameItem.Hdr) or nameof(LibraryGameItem.Enabled) or nameof(LibraryGameItem.UseSpeed) or nameof(LibraryGameItem.IsDirty)
            or nameof(LibraryGameItem.UseDs4Windows) or nameof(LibraryGameItem.Ds4WindowsPath) or nameof(LibraryGameItem.ActionEditor) or nameof(LibraryGameItem.SpeedHotkeys)) PersistDrafts();
    }
    private ImageSource? FindArtwork(string dataKey)
        => _gameIndex.TryGetValue(dataKey, out var game) && game.ArtworkIsCover ? game.Artwork : null;
    internal PlaytimeReport ReportWithJournal(PlaytimeReport report) =>
        report.JournalIncluded ? report : ReportJournalMerge.Apply(report, _journalStore.Entries, FindDisplayName);
    private string? FindDisplayName(string dataKey) => _gameIndex.TryGetValue(dataKey, out var game) ? game.Name : null;
    private readonly SemaphoreSlim _sideWrites = new(1, 1);
    private Task _draftWrite = Task.CompletedTask;
    private Exception? _draftFailure;
    internal async Task FlushDraftsAsync()
    {
        await _draftWrite;
        await DesktopDocumentStore.DrainProjectionsAsync();
        if (_draftFailure is { } failure) throw new InvalidOperationException("草稿尚未保存，编辑已保留：" + failure.Message, failure);
    }
    private void PersistDrafts()
    {
        var drafts = _games.Where(game => game.IsDirty).Select(game => game.ExportDraft()).Concat(_restoredDrafts.Values).ToList();
        if (_draftStore?.UsesSqlite != true)
        {
            try { _draftStore?.Write(drafts); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status = "草稿未能写入磁盘：" + ex.Message; }
            return;
        }
        _draftWrite = WriteDraftsAsync(drafts);
    }
    private async Task WriteDraftsAsync(List<LibraryDraft> drafts)
    {
        try
        {
            await _sideWrites.WaitAsync();
            try { await Task.Run(() => _draftStore!.Write(drafts)); _draftFailure = null; }
            finally { _sideWrites.Release(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { _draftFailure = ex; Status = "草稿未能写入磁盘，更新或关闭前请保存设置：" + ex.Message; }
    }
    public void Dispose()
    {
        _elapsedTimer?.Stop();
        _journalStore.Saved -= OnJournalSaved;
        Statistics.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
