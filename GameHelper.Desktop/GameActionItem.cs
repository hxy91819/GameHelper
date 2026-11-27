using GameHelper.Core.Models;

namespace GameHelper.Desktop;

/// <summary>One editable executable action.</summary>
public sealed class GameActionItem(GameAction action) : ObservableState
{
    private string _when = action.When;
    private string _type = action.Type;
    private string _path = action.Path;
    private string _arguments = action.Arguments ?? string.Empty;
    private bool _wait = action.Wait == true;
    private bool _singleInstance = action.SingleInstance == true;
    private bool _stopOnGameExit = action.StopOnGameExit == true;
    public string When { get => _when; set { if (Set(ref _when, value)) Changed(nameof(CanManage)); } }
    public string Type { get => _type; set { if (Set(ref _type, value)) { Changed(nameof(IsRun)); Changed(nameof(CanManage)); } } }
    public string Path { get => _path; set => Set(ref _path, value); }
    public string Arguments { get => _arguments; set => Set(ref _arguments, value); }
    public bool Wait { get => _wait; set { if (Set(ref _wait, value)) Changed(nameof(CanManage)); } }
    public bool SingleInstance { get => _singleInstance; set => Set(ref _singleInstance, value); }
    public bool StopOnGameExit { get => _stopOnGameExit; set => Set(ref _stopOnGameExit, value); }
    public bool CanManage => IsRun && When == "beforeLaunch" && !Wait;
    public bool IsRun => Type == "run";
    public GameAction ToAction() => new()
    {
        When = When, Type = Type, Path = Path.Trim(),
        Arguments = string.IsNullOrWhiteSpace(Arguments) ? null : Arguments,
        Wait = IsRun && Wait ? true : null,
        SingleInstance = CanManage && SingleInstance ? true : null,
        StopOnGameExit = CanManage && StopOnGameExit ? true : null
    };
}
