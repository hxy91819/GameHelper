using System.Text.Json.Serialization;

namespace GameHelper.Core.Models;

/// <summary>A per-game executable action, ordered within its trigger.</summary>
public sealed record GameAction
{
    public string When { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string? Arguments { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Wait { get; init; }

    /// <summary>Reuses only an identical action owned by this game, never arbitrary user processes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SingleInstance { get; init; }
    /// <summary>Terminates this action's owned process tree when the last game process exits.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? StopOnGameExit { get; init; }

    /// <summary>Checks stored Windows executable shape without probing installation.</summary>
    public void Validate()
    {
        if (When is not ("beforeLaunch" or "afterExit")) throw new ArgumentException("when must be beforeLaunch or afterExit.");
        if (Type is not ("run" or "close")) throw new ArgumentException("type must be run or close.");
        if (string.IsNullOrWhiteSpace(Path) ||
            !((Path.Length > 3 && char.IsLetter(Path[0]) && Path[1] == ':' && Path[2] is '\\' or '/') ||
              (Path.StartsWith(@"\\") && Path.Trim('\\').Split('\\').Length >= 3)) ||
            !Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("path must be an absolute Windows .exe path.");
        if ((SingleInstance == true || StopOnGameExit == true) && (Type != "run" || When != "beforeLaunch" || Wait == true))
            throw new ArgumentException("进程管理选项仅支持不等待退出的启动前运行动作。");
        if (Type == "close" && Wait is not null) throw new ArgumentException("wait is only supported for run actions.");
    }
}
