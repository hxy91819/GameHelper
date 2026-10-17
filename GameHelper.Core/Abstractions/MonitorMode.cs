namespace GameHelper.Core.Abstractions;

/// <summary>
/// The two monitor OSD presets GameHelper switches between via DDC/CI.
/// </summary>
public enum MonitorMode
{
    /// <summary>The "game" preset (e.g. Dell Game mode).</summary>
    Game = 0,

    /// <summary>The low-blue-light "comfort" preset (e.g. Dell ComfortView).</summary>
    Comfort = 1
}