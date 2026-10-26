namespace GameHelper.Core.Models;

/// <summary>Global display selection for automatic HDR; empty lists preserve system-wide behavior.</summary>
public sealed class HdrSettings
{
    public List<string> AllowedDisplays { get; set; } = new();
    public List<string> DisabledDisplays { get; set; } = new();

    public bool HasDisplayRules() => AllowedDisplays.Count > 0 || DisabledDisplays.Count > 0;

    /// <summary>Exact, case-insensitive name or device-path match; exclusion wins.</summary>
    public bool Allows(string name, string devicePath) =>
        !DisabledDisplays.Any(value => Matches(value, name, devicePath)) &&
        (AllowedDisplays.Count == 0 || AllowedDisplays.Any(value => Matches(value, name, devicePath)));

    public void Validate()
    {
        if (AllowedDisplays is null || DisabledDisplays is null ||
            AllowedDisplays.Any(string.IsNullOrWhiteSpace) || DisabledDisplays.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("hdr display lists must contain non-empty display names or device paths.");
    }

    private static bool Matches(string value, string name, string path) =>
        string.Equals(value.Trim(), name, StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrEmpty(path) && string.Equals(value.Trim(), path, StringComparison.OrdinalIgnoreCase));
}
