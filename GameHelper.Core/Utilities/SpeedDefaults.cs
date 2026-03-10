using System;

namespace GameHelper.Core.Utilities;

public static class SpeedDefaults
{
    public const double DefaultSpeedMultiplier = 2.0d;
    public const string DefaultHotkey = "Ctrl+Alt+F10";
    public const double MinimumSpeedMultiplier = 1.01d;

    public static double NormalizeMultiplier(double? value)
    {
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value.Value < MinimumSpeedMultiplier)
        {
            return DefaultSpeedMultiplier;
        }

        return Math.Round(value.Value, 2, MidpointRounding.AwayFromZero);
    }

    public static string NormalizeHotkey(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? DefaultHotkey : value.Trim();
    }
}
