using System.Globalization;
using System.Windows.Media;
using GameHelper.Core.Models;

namespace GameHelper.Desktop;

public sealed class LibraryGameItem : ObservableState
{
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
    private ImageSource? _artwork;
    private readonly LibraryGame _baseline;

    public LibraryGameItem(LibraryGame row)
    {
        _baseline = row;
        DataKey = row.Config.DataKey;
        Revision = row.Revision;
        _name = row.Config.DisplayName ?? DataKey;
        _executable = row.Config.Executable ?? string.Empty;
        _target = row.Config.Launch?.Target ?? row.Config.ExecutablePath ?? string.Empty;
        _arguments = row.Config.Launch?.Arguments ?? string.Empty;
        _directory = row.Config.Launch?.WorkingDirectory ?? string.Empty;
        _hdr = row.Config.HdrEnabled;
        _enabled = row.Config.IsEnabled;
        _useSpeed = row.Config.SpeedMultiplier is not null;
        _speed = (row.Config.SpeedMultiplier ?? 1.5).ToString("0.##", CultureInfo.CurrentCulture);
        _state = row.State;
        _minutes = row.TotalMinutes;
    }

    public string DataKey { get; }
    public string Revision { get; }
    public string Name { get => _name; set { if (Set(ref _name, value)) Edited(); } }
    public string Executable { get => _executable; set { if (Set(ref _executable, value)) Edited(); } }
    public string Target { get => _target; set { if (Set(ref _target, value)) Edited(); } }
    public string Arguments { get => _arguments; set { if (Set(ref _arguments, value)) Edited(); } }
    public string Directory { get => _directory; set { if (Set(ref _directory, value)) Edited(); } }
    public string Speed { get => _speed; set { if (Set(ref _speed, value)) Edited(); } }
    public bool Hdr { get => _hdr; set { if (Set(ref _hdr, value)) Edited(); } }
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) Edited(); } }
    public bool UseSpeed { get => _useSpeed; set { if (Set(ref _useSpeed, value)) Edited(); } }
    public bool IsDirty { get => _dirty; private set => Set(ref _dirty, value); }
    public string State { get => _state; private set => Set(ref _state, value); }
    public ImageSource? Artwork { get => _artwork; private set { if (Set(ref _artwork, value)) Changed(nameof(ArtworkIsCover)); } }
    public bool ArtworkIsCover => Artwork is System.Windows.Media.Imaging.BitmapSource { PixelHeight: > 128 };
    public string Playtime => $"历史时长 {_minutes / 60d:0.#} 小时";
    public string Summary => !Enabled ? "自动化已暂停" : string.Join(" · ", new[] { Hdr ? "HDR" : "SDR", UseSpeed ? $"{Speed}×" : null }.Where(value => value is not null));
    public string Initial => string.IsNullOrEmpty(Name) ? "G" : StringInfo.GetNextTextElement(Name);

    private void Edited()
    {
        IsDirty = true;
        Changed(nameof(Summary));
        Changed(nameof(Initial));
    }

    public void RefreshRuntime(LibraryGame row)
    {
        State = row.State;
        _minutes = row.TotalMinutes;
        Changed(nameof(Playtime));
    }

    public async Task LoadArtworkAsync()
    {
        Artwork = await Task.Run(() => LocalGameArtwork.Load(Executable, Target));
    }

    internal LibraryDraft ExportDraft() => new(_baseline, Name, Executable, Target, Arguments, Directory, Speed, Hdr, Enabled, UseSpeed);
    internal static LibraryGameItem RestoreDraft(LibraryDraft draft)
    {
        return new LibraryGameItem(draft.Baseline)
        {
            _name = draft.Name, _executable = draft.Executable, _target = draft.Target,
            _arguments = draft.Arguments, _directory = draft.Directory, _speed = draft.Speed,
            _hdr = draft.Hdr, _enabled = draft.Enabled, _useSpeed = draft.UseSpeed, _dirty = true
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
        return new GameConfig
        {
            DataKey = DataKey, DisplayName = Name.Trim(), Executable = Executable.Trim(),
            IsEnabled = Enabled, HdrEnabled = Hdr, SpeedMultiplier = speed,
            Launch = string.IsNullOrWhiteSpace(Target) ? null : new GameLaunchOptions
            {
                Target = Target.Trim(), Arguments = NullIfEmpty(Arguments), WorkingDirectory = NullIfEmpty(Directory)
            }
        };
    }

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
