using System;
using System.Collections.Generic;

namespace GameHelper.Core.Models;

/// <summary>
/// One in-flight play session snapshot row: the tracked process identity plus the
/// original session start time. Used to carry live game sessions across a GameHelper
/// restart (deploy, crash recovery) without interrupting the recorded playtime.
/// </summary>
public sealed class InFlightSession
{
    /// <summary>Stable game identity this process instance belongs to.</summary>
    public string DataKey { get; set; } = string.Empty;

    /// <summary>Observed process id at snapshot time; null when unavailable.</summary>
    public int? ProcessId { get; set; }

    /// <summary>Normalized executable name (guaranteed .exe suffix).</summary>
    public string? NormalizedName { get; set; }

    /// <summary>Normalized executable path when resolved at start; null for name-only matches.</summary>
    public string? NormalizedPath { get; set; }

    /// <summary>Original session start time (local).</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>UTC process creation identity; absent in legacy snapshots.</summary>
    public DateTime? ProcessStartedAtUtc { get; set; }

    /// <summary>
    /// 库内启动标记：true=由启动按钮（Launch）发起的会话；false=外部启动的会话；
    /// null=升级前旧版快照，续算时按库内启动对待以保持行为连续（仅影响自动化效果，不影响时长）。
    /// </summary>
    public bool? LaunchedByHelper { get; set; }

    /// <summary>Fixed end time for a completed session awaiting a successful history write.</summary>
    public DateTime? RecoveryEndAt { get; set; }
}

/// <summary>
/// Snapshot of all in-flight sessions plus the moment it was taken. The snapshot time
/// doubles as the last-known-alive timestamp for closing sessions whose game exited
/// during a restart window.
/// </summary>
public sealed class InFlightSnapshot
{
    /// <summary>Whether this instance changed an originally disabled HDR state.</summary>
    public bool HdrEnabledByGameHelper { get; set; }

    /// <summary>Launch deadlines awaiting real process observation; never replayed as launch commands.</summary>
    public Dictionary<string, DateTimeOffset> PendingLaunches { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Local time when the snapshot was persisted.</summary>
    public DateTime SnapshotAt { get; set; }

    /// <summary>In-flight session rows; empty means nothing is being tracked.</summary>
    public List<InFlightSession> Sessions { get; set; } = new();
}
