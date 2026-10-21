using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Desktop;

/// <summary>Desktop state; all persisted changes and launch ownership remain in the running host.</summary>
public sealed class LibraryViewModel : ObservableState, IDisposable
{
    private readonly IGameLibrary _library;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<AsyncCommand> _commands = [];
    private readonly ObservableCollection<LibraryGameItem> _games = [];
    private readonly LibraryDraftStore? _draftStore;
    private readonly SteamPosterLibrary _posters;
    private Dictionary<string, LibraryDraft> _restoredDrafts = new(StringComparer.OrdinalIgnoreCase);
    private LibraryGameItem? _selected;
    private bool _busy;
    private bool _connected;
    private string _status = "正在连接游戏监控…";
    private string _search = string.Empty;
    private string _filter = "all";
    private string _configPath = string.Empty;

    public LibraryViewModel(IGameLibrary library, string? configPath = null)
    {
        _library = library;
        _posters = new SteamPosterLibrary(configPath, allowNetwork: configPath is not null);
        if (configPath is not null)
        {
            _draftStore = new LibraryDraftStore(configPath);
            try { _restoredDrafts = _draftStore.Read().ToDictionary(draft => draft.Baseline.Config.DataKey, StringComparer.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { _status = "未能读取上次的界面草稿：" + ex.Message; }
        }
        Games = CollectionViewSource.GetDefaultView(_games);
        Games.Filter = Matches;
        RefreshCommand = Command(() => RefreshAsync(), () => !IsBusy);
        SaveCommand = Command(() => RunAsync(SaveSelectedAsync), () => Ready && Selected?.IsDirty == true);
        LaunchCommand = Command(() => RunAsync(async () =>
        {
            if (Selected?.IsDirty == true) await SaveSelectedAsync();
            if (Selected is not null) await SendAsync(new GameLibraryRequest { Operation = GameLibraryOperation.Launch, DataKey = Selected.DataKey });
        }), () => Ready && Selected is not null && !string.IsNullOrWhiteSpace(Selected.Target) && Selected.State is not ("运行中" or "启动中"));
    }

    public ICollectionView Games { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand LaunchCommand { get; }
    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); Changed(nameof(Ready)); RefreshCommands(); } }
    public bool Connected { get => _connected; private set { Set(ref _connected, value); Changed(nameof(Ready)); } }
    public bool Ready => Connected && !IsBusy;
    public string Status { get => _status; set => Set(ref _status, value); }
    public string ConfigPath { get => _configPath; private set => Set(ref _configPath, value); }
    public string Count => $"{Games.Cast<object>().Count()} / {_games.Count} 款游戏";
    public string Search { get => _search; set { if (Set(ref _search, value)) Filter(); } }
    public LibraryGameItem? Selected { get => _selected; set { Set(ref _selected, value); RefreshCommands(); } }
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
            ? "未能完成与监控进程的通信。请刷新确认状态；启动或保存操作不会自动重试。"
            : ex.Message;
    }

    public void SetFilter(string filter) { _filter = filter; Filter(); }
    private bool Matches(object value)
    {
        var game = (LibraryGameItem)value;
        return (_filter != "hdr" || game.Hdr) && (_filter != "speed" || game.UseSpeed)
            && (game.Name.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase) || game.Executable.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase));
    }
    private void Filter()
    {
        Games.Refresh();
        var visible = Games.Cast<LibraryGameItem>().ToList();
        if (Selected is null || !visible.Contains(Selected)) Selected = visible.FirstOrDefault();
        Changed(nameof(Count));
    }

    public async Task RefreshAsync(bool discardDrafts = false)
    {
        if (IsBusy) return;
        await RunAsync(async () =>
        {
            var response = await _library.ExecuteAsync(new GameLibraryRequest { Operation = GameLibraryOperation.List }, _lifetime.Token);
            Apply(response, discardDrafts);
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Report(ex); }
        finally { IsBusy = false; }
    }

    private async Task SaveSelectedAsync()
    {
        if (Selected is null) return;
        var key = Selected.DataKey;
        var response = await _library.ExecuteAsync(new GameLibraryRequest
        {
            Operation = GameLibraryOperation.Save, DataKey = key,
            ExpectedRevision = Selected.Revision, Game = Selected.ToConfig()
        }, _lifetime.Token);
        if (!response.Success) throw new InvalidOperationException(response.Error);
        // Replace only the saved draft; other games may have unsaved changes.
        var old = _games.FirstOrDefault(game => game.DataKey == key);
        if (old is not null) _games.Remove(old);
        Apply(response, false, key);
    }

    private async Task SendAsync(GameLibraryRequest request)
    {
        var response = await _library.ExecuteAsync(request, _lifetime.Token);
        Apply(response);
    }

    private void Apply(GameLibraryResponse response, bool discardDrafts = false, string? selectedKey = null)
    {
        if (!response.Success) throw new InvalidOperationException(response.Error ?? "游戏库操作失败。");
        var key = selectedKey ?? Selected?.DataKey;
        var old = _games.ToDictionary(game => game.DataKey, StringComparer.OrdinalIgnoreCase);
        var updated = new List<LibraryGameItem>();
        foreach (var row in response.Games.OrderBy(row => row.Config.DisplayName ?? row.Config.DataKey, StringComparer.CurrentCultureIgnoreCase))
        {
            LibraryGameItem item;
            if (!discardDrafts && _restoredDrafts.Remove(row.Config.DataKey, out var restored))
            {
                item = LibraryGameItem.RestoreDraft(restored);
                item.RefreshRuntime(row);
                item.PropertyChanged += OnItemChanged;
                item.ArtworkLoading = item.LoadArtworkAsync(_posters, _lifetime.Token);
            }
            else if (!discardDrafts && old.TryGetValue(row.Config.DataKey, out var draft) && (draft.IsDirty || draft.Revision == row.Revision))
            {
                item = draft;
                item.RefreshRuntime(row);
            }
            else
            {
                item = new LibraryGameItem(row);
                item.PropertyChanged += OnItemChanged;
                item.ArtworkLoading = item.LoadArtworkAsync(_posters, _lifetime.Token);
            }
            updated.Add(item);
        }
        if (!_games.SequenceEqual(updated))
        {
            _games.Clear();
            foreach (var item in updated) _games.Add(item);
        }
        Filter();
        Selected = _games.FirstOrDefault(game => game.DataKey == key) ?? _games.FirstOrDefault();
        if (discardDrafts) _restoredDrafts.Clear();
        Connected = true;
        ConfigPath = response.ConfigPath;
        Status = response.Message ?? (response.MonitorRunning ? "监控已就绪 · 关闭游戏库后继续监控" : "监控未运行 · 启动游戏时会先开启监控");
        PersistDrafts();
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
    internal Task<IReadOnlyList<GameHelper.Infrastructure.Artwork.SteamArtworkGame>> SearchPostersAsync(string query, CancellationToken cancellationToken) => _posters.SearchAsync(query, cancellationToken);
    internal Task WaitForArtworkAsync() => Task.WhenAll(_games.Select(game => game.ArtworkLoading));
    internal int PosterCount => _games.Count(game => game.ArtworkIsCover);
    internal async Task AssociatePosterAsync(LibraryGameItem game, uint? appId)
    {
        _posters.Associate(game.DataKey, appId);
        await game.LoadArtworkAsync(_posters, _lifetime.Token, refresh: true);
    }
    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshCommands();
        if (e.PropertyName is nameof(LibraryGameItem.Name) or nameof(LibraryGameItem.Executable) or nameof(LibraryGameItem.Target)
            or nameof(LibraryGameItem.Arguments) or nameof(LibraryGameItem.Directory) or nameof(LibraryGameItem.Speed)
            or nameof(LibraryGameItem.Hdr) or nameof(LibraryGameItem.Enabled) or nameof(LibraryGameItem.UseSpeed) or nameof(LibraryGameItem.IsDirty)) PersistDrafts();
    }
    private void PersistDrafts()
    {
        try { _draftStore?.Write(_games.Where(game => game.IsDirty).Select(game => game.ExportDraft())); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Status = "草稿未能写入磁盘，更新或关闭前请保存设置：" + ex.Message; }
    }
    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); }
}
