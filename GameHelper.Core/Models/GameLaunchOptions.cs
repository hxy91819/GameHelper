namespace GameHelper.Core.Models;

/// <summary>A launch entry, independent of the executable identity used for process observation.</summary>
public sealed record GameLaunchOptions
{
    public string Target { get; init; } = string.Empty;
    public string? Arguments { get; init; }
    public string? WorkingDirectory { get; init; }
    /// <summary>Optional DS4Windows executable to ensure running before starting the game; null disables it.</summary>
    // Preserve pre-existing revision tokens and saved UI drafts when this optional feature is disabled.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Ds4WindowsPath { get; init; }

    /// <summary>Checks stored format without requiring the game to remain installed.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Target)) throw new ArgumentException("请配置游戏启动入口。");
        if (Ds4WindowsPath is not null && (string.IsNullOrWhiteSpace(Ds4WindowsPath) || !Path.IsPathFullyQualified(Ds4WindowsPath)
            || !Path.GetExtension(Ds4WindowsPath).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("DS4Windows 路径应为 exe 完整路径。");
        if (Target.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(Target, @"\Asteam://rungameid/[0-9]+/?\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                || !string.IsNullOrWhiteSpace(Arguments) || !string.IsNullOrWhiteSpace(WorkingDirectory))
                throw new ArgumentException("Steam 入口应为 steam://rungameid/数字，不附带参数或工作目录。");
            return;
        }
        if (!Path.IsPathFullyQualified(Target) || !new[] { ".exe", ".lnk", ".url" }.Contains(Path.GetExtension(Target), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("启动入口应为 exe / 快捷方式完整路径，或 Steam 启动入口。");
        if (!string.IsNullOrWhiteSpace(WorkingDirectory) && !Path.IsPathFullyQualified(WorkingDirectory))
            throw new ArgumentException("工作目录应为完整路径。");
    }
}
