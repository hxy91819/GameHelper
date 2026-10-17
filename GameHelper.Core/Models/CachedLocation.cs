using System;

namespace GameHelper.Core.Models;

/// <summary>
/// A previously auto-detected coordinate, cached in the data directory so daylight
/// computation works across restarts and refresh failures.
/// </summary>
public sealed class CachedLocation
{
    /// <summary>Latitude in degrees (north positive).</summary>
    public double Latitude { get; set; }

    /// <summary>Longitude in degrees (east positive).</summary>
    public double Longitude { get; set; }

    /// <summary>Local time when this coordinate was last detected.</summary>
    public DateTime UpdatedAt { get; set; }
}