using System;
using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

/// <summary>
/// Persists coordinates from the last successful auto-detection so the monitor-mode
/// daylight window keeps working across restarts and when the location service is
/// temporarily unavailable. Implementations must write atomically.
/// </summary>
public interface ILocationCacheStore
{
    /// <summary>
    /// Reads the cached coordinate. Returns null when the file is absent, corrupt, or
    /// semantically invalid; never throws for benign IO problems.
    /// </summary>
    CachedLocation? Load();

    /// <summary>Saves the coordinate atomically, stamping the update time.</summary>
    void Save(UserCoordinate coordinate);
}