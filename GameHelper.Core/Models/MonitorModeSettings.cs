using System;
using System.Collections.Generic;

namespace GameHelper.Core.Models;

/// <summary>One code/value pair as written in the YAML <c>monitorMode</c> section.</summary>
public sealed class MonitorModePresetData
{
    /// <summary>VCP code as "DC" or "0xDC".</summary>
    public string? Code { get; set; }

    /// <summary>VCP value as "0x1D" (hex) or "29" (decimal).</summary>
    public string? Value { get; set; }
}

/// <summary>
/// Shared daylight policy and explicitly bound per-display preset automation.
/// Legacy top-level recipes remain as explicit binding templates for Dell U2720QM;
/// automation evaluates only the recipes stored under bound display profiles.
/// </summary>
public sealed class MonitorModeSettings
{
    /// <summary>Master switch for automatic preset switching.</summary>
    public bool Enabled { get; set; }

    /// <summary>Only these bound devices can receive automatic writes. Empty means no writes.</summary>
    public List<MonitorModeDisplayProfile> Displays { get; set; } = [];

    /// <summary>
    /// Coordinate source: "off" (default; use the fixed daylight window only) or "auto"
    /// (detect once via the Windows location service and cache locally). Manual
    /// latitude/longitude always win over auto detection.
    /// </summary>
    public string? Location { get; set; } = "off";

    /// <summary>Geographic latitude in degrees for solar sunrise/sunset computation. Optional.</summary>
    public double? Latitude { get; set; }

    /// <summary>Geographic longitude in degrees (east positive) for solar computation. Optional.</summary>
    public double? Longitude { get; set; }

    /// <summary>
    /// Fallback daylight start ("HH:mm", local time) when no coordinates are configured.
    /// </summary>
    public string? DaylightStart { get; set; } = "07:00";

    /// <summary>
    /// Fallback daylight end ("HH:mm", local time) when no coordinates are configured.
    /// </summary>
    public string? DaylightEnd { get; set; } = "18:30";

    /// <summary>Legacy game recipe template. Inert until explicitly copied into a device profile.</summary>
    public List<MonitorModePresetData> GamePresets { get; set; } = U2720QMGamePresets();

    /// <summary>Legacy comfort recipe template. Inert until explicitly copied into a device profile.</summary>
    public List<MonitorModePresetData> ComfortPresets { get; set; } = U2720QMComfortPresets();

    /// <summary>Rejects invalid configuration without silently rewriting user values.</summary>
    public void Validate()
    {
        ValidatePresets(GamePresets, nameof(GamePresets));
        ValidatePresets(ComfortPresets, nameof(ComfortPresets));
        if (Displays is null) throw new ArgumentException("monitorMode.displays cannot be null.");
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in Displays)
        {
            if (profile?.Device is null) throw new ArgumentException("显示器预设缺少设备绑定。");
            profile.Device.Validate();
            if (!identities.Add(profile.Device.Key())) throw new ArgumentException("同一显示设备只能保存一套预设配置。");
            ValidatePresets(profile.GamePresets, "displays.gamePresets");
            ValidatePresets(profile.ComfortPresets, "displays.comfortPresets");
        }

        ParseTime(DaylightStart, "monitorMode.daylightStart");
        ParseTime(DaylightEnd, "monitorMode.daylightEnd");

        if ((Latitude.HasValue || Longitude.HasValue) && (!Latitude.HasValue || !Longitude.HasValue))
        {
            throw new ArgumentException("monitorMode latitude and longitude must be configured together.");
        }

        if (!string.IsNullOrWhiteSpace(Location)
            && !string.Equals(Location, "off", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Location, "auto", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("monitorMode.location must be 'off' or 'auto'.");
        }

        if (Latitude is < -90 or > 90)
        {
            throw new ArgumentException("monitorMode latitude must be within [-90, 90].");
        }

        if (Longitude is < -180 or > 180)
        {
            throw new ArgumentException("monitorMode longitude must be within [-180, 180].");
        }
    }

    internal static List<MonitorModePresetData> U2720QMGamePresets() => new()
    {
        new MonitorModePresetData { Code = "DC", Value = "0x05" },
        new MonitorModePresetData { Code = "E2", Value = "0x04" },
        new MonitorModePresetData { Code = "F0", Value = "0x00" }
    };

    internal static List<MonitorModePresetData> U2720QMComfortPresets() => new()
    {
        // U2720QM firmware applies DC=00 and E2=1D together when F0 selects ComfortView.
        new MonitorModePresetData { Code = "F0", Value = "0x0C" }
    };

    /// <summary>Builds a detached recipe/daylight input for one configured device.</summary>
    public MonitorModeSettings ForDisplay(MonitorModeDisplayProfile profile) => new()
    {
        Enabled = Enabled && profile.Enabled, Location = Location, Latitude = Latitude, Longitude = Longitude,
        DaylightStart = DaylightStart, DaylightEnd = DaylightEnd,
        GamePresets = profile.GamePresets, ComfortPresets = profile.ComfortPresets
    };

    private static void ValidatePresets(List<MonitorModePresetData> presets, string fieldName)
    {
        if (presets is null || presets.Count == 0)
        {
            throw new ArgumentException($"monitorMode.{fieldName} must contain at least one preset.");
        }

        foreach (var preset in presets)
        {
            if (preset is null || !MonitorModeWrite.TryParse(preset.Code, preset.Value, out _))
            {
                throw new ArgumentException(
                    $"monitorMode.{fieldName} contains an invalid code/value pair: '{preset?.Code}' = '{preset?.Value}'.");
            }
        }
    }

    private static void ParseTime(string? text, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(text) || !TimeOnly.TryParse(text, out _))
        {
            throw new ArgumentException($"monitorMode.{fieldName} must be a valid HH:mm time.");
        }
    }
}
