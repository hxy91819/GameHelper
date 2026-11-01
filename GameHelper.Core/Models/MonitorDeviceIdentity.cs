using GameHelper.Core.Abstractions;

namespace GameHelper.Core.Models;

/// <summary>A saved physical display binding. Names are descriptive, never matching rules.</summary>
public sealed class MonitorDeviceIdentity
{
    public string Name { get; set; } = "";
    public string DevicePath { get; set; } = "";
    /// <summary>Manufacturer/product/serial identity when a reliable EDID serial is available.</summary>
    public string? HardwareKey { get; set; }

    public string Key() => !string.IsNullOrWhiteSpace(HardwareKey) ? "hardware:" + HardwareKey.Trim().ToUpperInvariant()
        : "path:" + DevicePath.Trim().ToUpperInvariant();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DevicePath)) throw new ArgumentException("显示器预设必须绑定具体设备，不能只填写名称。");
    }

    /// <summary>Ambiguous/missing identities fail closed; strong identities never fall back to a port.</summary>
    public HdrDisplayInfo? Resolve(IReadOnlyList<HdrDisplayInfo> displays)
    {
        if (string.IsNullOrWhiteSpace(DevicePath)) return null;
        var matches = displays.Where(display => !string.IsNullOrWhiteSpace(HardwareKey)
            ? string.Equals(HardwareKey, display.Identity?.HardwareKey, StringComparison.OrdinalIgnoreCase)
            : string.Equals(DevicePath, display.DevicePath, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}

/// <summary>Device-specific DDC recipes; an unconfigured display has no automatic write policy.</summary>
public sealed class MonitorModeDisplayProfile
{
    public MonitorDeviceIdentity Device { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public List<MonitorModePresetData> GamePresets { get; set; } = [];
    public List<MonitorModePresetData> ComfortPresets { get; set; } = [];
}
