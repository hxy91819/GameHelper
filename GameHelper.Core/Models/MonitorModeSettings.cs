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
/// Global monitor preset-mode automation settings. Missing configuration disables the feature.
/// Default presets fit a Dell U2720QM (Game mode / ComfortView over DDC/CI); other monitors
/// can override the recipes below.
/// </summary>
public sealed class MonitorModeSettings
{
    /// <summary>Master switch for automatic preset switching.</summary>
    public bool Enabled { get; set; }

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

    /// <summary>DDC/CI recipe applied when the game preset is wanted. Order matters.</summary>
    public List<MonitorModePresetData> GamePresets { get; set; } = U2720QMGamePresets();

    /// <summary>DDC/CI recipe applied when the comfort preset is wanted. Order matters.</summary>
    public List<MonitorModePresetData> ComfortPresets { get; set; } = U2720QMComfortPresets();

    /// <summary>Rejects invalid configuration without silently rewriting user values.</summary>
    public void Validate()
    {
        ValidatePresets(GamePresets, nameof(GamePresets));
        ValidatePresets(ComfortPresets, nameof(ComfortPresets));

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
        new MonitorModePresetData { Code = "DC", Value = "0x00" },
        new MonitorModePresetData { Code = "E2", Value = "0x1D" },
        new MonitorModePresetData { Code = "F0", Value = "0x0C" }
    };

    private static void ValidatePresets(List<MonitorModePresetData> presets, string fieldName)
    {
        if (presets.Count == 0)
        {
            throw new ArgumentException($"monitorMode.{fieldName} must contain at least one preset.");
        }

        foreach (var preset in presets)
        {
            if (!MonitorModeWrite.TryParse(preset.Code, preset.Value, out var write))
            {
                throw new ArgumentException(
                    $"monitorMode.{fieldName} contains an invalid code/value pair: '{preset.Code}' = '{preset.Value}'.");
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