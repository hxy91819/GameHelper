namespace GameHelper.Core.Models;

/// <summary>Steam declarations and a separate, configuration-scoped user override.</summary>
public sealed record ControllerSupport(string Status = "unknown", bool DualShock = false, bool DualSense = false, bool? Manual = null)
{
    public bool Unsupported => Manual == false || (Manual is null && Status == "unlisted");
    public string Label => Manual is { } value ? (value ? "支持手柄（手动）" : "不支持手柄（手动）") : Status switch
    { "full" => "完整支持手柄", "partial" => "部分支持手柄", "unlisted" => "不支持手柄", _ => "手柄支持未知" };
    public string Description => (Manual is not null ? "用户手动设置，优先于 Steam。" : Status == "unlisted" ? "Steam 未标注手柄支持" : Label)
        + (DualShock ? " · Steam 声明 DualShock 支持" : "") + (DualSense ? " · Steam 声明 DualSense 支持" : "");
}
