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
    public string When { get => _when; set => Set(ref _when, value); }
    public string Type { get => _type; set { if (Set(ref _type, value)) Changed(nameof(IsRun)); } }
    public string Path { get => _path; set => Set(ref _path, value); }
    public string Arguments { get => _arguments; set => Set(ref _arguments, value); }
    public bool Wait { get => _wait; set => Set(ref _wait, value); }
    public bool IsRun => Type == "run";
    public GameAction ToAction() => new()
    {
        When = When, Type = Type, Path = Path.Trim(),
        Arguments = string.IsNullOrWhiteSpace(Arguments) ? null : Arguments,
        Wait = IsRun && Wait ? true : null
    };
}
