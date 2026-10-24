using System.Globalization;
using System.IO;
using System.Windows.Media;
using GameHelper.Core.Models;

namespace GameHelper.Desktop;

public sealed class LibraryGameItem : ObservableState
{
    internal const string DefaultDs4WindowsPath = @"D:\Program Files\DS4Windows\DS4Windows\DS4Windows.exe";
    private bool _useDs4Windows;
    private string _ds4WindowsPath;
    private string _name;
    private string _executable;
    private string _target;
    private string _arguments;
    private string _directory;
    private string _speed;
    private bool _hdr;
    private bool _enabled;
    private bool _useSpeed;
    private bool _dirty;
    private string _state;
    private long _minutes;
    private DateTime? _runningStartedAt;
    private string? _runningElapsed;
    private ImageSource? _artwork;
    private bool _artworkIsCover;
    private string _posterStatus = "正在读取海报…";
    private int _artworkVersion;
    private readonly LibraryGame _baseline;
    private GameInstallation _installation;
    internal Task ArtworkLoading { get; set; } = Task.CompletedTask;

    public LibraryGameItem(LibraryGame row)
    {
        _baseline = row;
        LastPlayedAt = row.LastPlayedAt;
        _installation = row.Installation;
        DataKey = row.Config.DataKey;
        Revision = row.Revision;
        _name = row.Config.DisplayName ?? DataKey;
        _executable = row.Config.Executable ?? string.Empty;
        _target = row.Config.Launch?.Target ?? row.Config.ExecutablePath ?? string.Empty;
        _useDs4Windows = row.Config.Launch?.Ds4WindowsPath is not null;
        _ds4WindowsPath = row.Config.Launch?.Ds4WindowsPath ?? DefaultDs4WindowsPath;
        _arguments = row.Config.Launch?.Arguments ?? string.Empty;
        _directory = row.Config.Launch?.WorkingDirectory ?? string.Empty;
        _hdr = row.Config.HdrEnabled;
        _enabled = row.Config.IsEnabled;
        _useSpeed = row.Config.SpeedMultiplier is not null;
        _speed = (row.Config.SpeedMultiplier ?? 1.5).ToString("0.##", CultureInfo.CurrentCulture);
        _state = row.State;
        _minutes = row.TotalMinutes;
        _runningStartedAt = row.RunningStartedAt;
    }

    private IReadOnlyList<string> _genres = [];
    public IReadOnlyList<string> Genres => _genres;
    public string GenreLabel => _genres.Count == 0 ? "未分类" : string.Join(" · ", _genres);
    internal Task GenresLoading { get; set; } = Task.CompletedTask;
    internal int GenreVersion { get; set; }
    internal (string Executable, string Target)? GenreIdentity { get; set; }
    internal void SetGenres(IReadOnlyList<string> genres)
    {
        if (_genres.SequenceEqual(genres, StringComparer.OrdinalIgnoreCase)) return;
        _genres = genres.ToArray();
        Changed(nameof(Genres)); Changed(nameof(GenreLabel));
    }
    public string DataKey { get; }
    public string Revision { get; }
    public DateTimeOffset? LastPlayedAt { get; private set; }
    public string LastPlayedLabel => LastPlayedAt is { } date ? $"最近游玩 {date.LocalDateTime:MM/dd HH:mm}" : "尚无已完成记录";
    public string Name { get => _name; set { if (Set(ref _name, value)) Edited(); } }
    public string Executable { get => _executable; set { if (Set(ref _executable, value)) Edited(); } }
    public string Target { get => _target; set { if (Set(ref _target, value)) Edited(); } }
    public string Arguments { get => _arguments; set { if (Set(ref _arguments, value)) Edited(); } }
    public string Directory { get => _directory; set { if (Set(ref _directory, value)) Edited(); } }
    public string Speed { get => _speed; set { if (Set(ref _speed, value)) Edited(); } }
    public bool Hdr { get => _hdr; set { if (Set(ref _hdr, value)) Edited(); } }
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) Edited(); } }
    public bool UseSpeed { get => _useSpeed; set { if (Set(ref _useSpeed, value)) Edited(); } }
    public bool UseDs4Windows { get => _useDs4Windows; set { if (Set(ref _useDs4Windows, value)) Edited(); } }
    public string Ds4WindowsPath { get => _ds4WindowsPath; set { if (Set(ref _ds4WindowsPath, value)) Edited(); } }
    public bool IsDirty { get => _dirty; private set => Set(ref _dirty, value); }
    public string State { get => _state; private set => Set(ref _state, value); }
    public bool IsRunning => IsRunningState(_state);
    internal static bool IsRunningState(string state) => state is "运行中" or "运行中·外部";
    /// <summary>状态徽章文本：运行中卡片附加本局实时计时的 mm:ss，其他状态原样。</summary>
    public string StateDisplay => IsRunning && _runningElapsed is not null ? $"{_state} · {_runningElapsed}" : _state;
    /// <summary>运行中卡片的会话计时行；停止运行后为 null。</summary>
    public string? RunningSessionLine => IsRunning && _runningElapsed is not null ? $"本次已玩 {_runningElapsed}" : null;
    public ImageSource? Artwork { get => _artwork; private set => Set(ref _artwork, value); }
    public bool ArtworkIsCover { get => _artworkIsCover; private set { if (Set(ref _artworkIsCover, value)) Changed(nameof(ArtworkIsIcon)); } }
    public bool ArtworkIsIcon => !ArtworkIsCover;
    public string PosterStatus { get => _posterStatus; private set => Set(ref _posterStatus, value); }
    public string Playtime => $"历史时长 {_minutes / 60d:0.#} 小时";
    public string Summary => !Enabled ? "自动化已暂停" : string.Join(" · ", new[] { Hdr ? "HDR" : "SDR", UseSpeed ? $"{Speed}×" : null }.Where(value => value is not null));
    public string Initial => string.IsNullOrEmpty(Name) ? "G" : StringInfo.GetNextTextElement(Name);
    public GameInstallationState InstallationState => _installation.State;
    public string InstallationLabel => _installation.State switch { GameInstallationState.Installed => "本机已安装", GameInstallationState.Missing => "本地未找到", _ => "安装位置待确认" };
    public string InstallationDetail => _installation.Description;

    private void Edited()
    {
        IsDirty = true;
        Changed(nameof(Summary));
        Changed(nameof(Initial));
    }

    public void RefreshRuntime(LibraryGame row)
    {
        LastPlayedAt = row.LastPlayedAt; Changed(nameof(LastPlayedLabel));
        var startChanged = _runningStartedAt != row.RunningStartedAt;
        var stateChanged = _state != row.State;
        State = row.State;
        if (stateChanged) Changed(nameof(IsRunning));
        _runningStartedAt = row.RunningStartedAt;
        if (startChanged || !IsRunning) _runningElapsed = null;
        if (startChanged || stateChanged)
        {
            Changed(nameof(StateDisplay));
            Changed(nameof(RunningSessionLine));
        }
        if (_installation != row.Installation)
        {
            _installation = row.Installation;
            Changed(nameof(InstallationState)); Changed(nameof(InstallationLabel)); Changed(nameof(InstallationDetail));
        }
        if (_minutes != row.TotalMinutes) { _minutes = row.TotalMinutes; Changed(nameof(Playtime)); }
    }

    /// <summary>每秒推进运行中游戏的"本局已玩"计时（由 <see cref="LibraryViewModel"/> 的时钟驱动）。</summary>
    internal void OnSecondTick(DateTime now)
    {
        if (!IsRunning)
        {
            if (_runningElapsed is not null)
            {
                _runningElapsed = null;
                Changed(nameof(StateDisplay));
                Changed(nameof(RunningSessionLine));
            }
            return;
        }

        if (_runningStartedAt is not { } start) return;
        var elapsed = now - start;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var text = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        if (text != _runningElapsed)
        {
            _runningElapsed = text;
            Changed(nameof(StateDisplay));
            Changed(nameof(RunningSessionLine));
        }
    }

    internal async Task LoadArtworkAsync(SteamPosterLibrary posters, CancellationToken cancellationToken, bool refresh = false)
    {
        var version = ++_artworkVersion;
        try
        {
            var artwork = await posters.LoadAsync(DataKey, Executable, Target, cancellationToken, refresh);
            if (cancellationToken.IsCancellationRequested || version != _artworkVersion) return;
            Artwork = artwork.Image;
            ArtworkIsCover = artwork.IsCover;
            PosterStatus = artwork.Description;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Text.Json.JsonException)
        { if (version == _artworkVersion) PosterStatus = "海报暂不可用，可重新匹配后重试"; }
    }

    internal LibraryDraft ExportDraft() => new(_baseline, Name, Executable, Target, Arguments, Directory, Speed, Hdr, Enabled, UseSpeed, UseDs4Windows, Ds4WindowsPath);
    internal static LibraryGameItem RestoreDraft(LibraryDraft draft)
    {
        return new LibraryGameItem(draft.Baseline)
        {
            _name = draft.Name, _executable = draft.Executable, _target = draft.Target,
            _arguments = draft.Arguments, _directory = draft.Directory, _speed = draft.Speed,
            _hdr = draft.Hdr, _enabled = draft.Enabled, _useSpeed = draft.UseSpeed, _dirty = true,
            _useDs4Windows = draft.UseDs4Windows, _ds4WindowsPath = draft.Ds4WindowsPath ?? DefaultDs4WindowsPath
        };
    }

    public GameConfig ToConfig()
    {
        double? speed = null;
        if (UseSpeed)
        {
            if (!double.TryParse(Speed, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed)
                || !double.IsFinite(parsed) || parsed <= 0) throw new ArgumentException("倍速必须是大于 0 的数字。");
            speed = parsed;
        }
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("请输入游戏名称。");
        if (UseDs4Windows && string.IsNullOrWhiteSpace(Target)) throw new ArgumentException("启用 DS4Windows 前，请先配置游戏启动入口。");
        return new GameConfig
        {
            DataKey = DataKey, DisplayName = Name.Trim(), Executable = Executable.Trim(),
            IsEnabled = Enabled, HdrEnabled = Hdr, SpeedMultiplier = speed,
            Launch = string.IsNullOrWhiteSpace(Target) ? null : new GameLaunchOptions
            {
                Target = Target.Trim(), Arguments = NullIfEmpty(Arguments), WorkingDirectory = NullIfEmpty(Directory),
                Ds4WindowsPath = UseDs4Windows ? Ds4WindowsPath.Trim() : null
            }
        };
    }

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
