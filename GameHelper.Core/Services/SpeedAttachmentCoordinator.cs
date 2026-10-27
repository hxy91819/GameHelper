using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace GameHelper.Core.Services;

/// <summary>
/// Owns speed attachment policy, deferred work and exit correlation. Configuration and
/// lifecycle calls remain serialized by GameAutomationService's state lock. Deferred
/// callbacks use the scheduler execution boundary and a short record lock; neither
/// acquires the service lock. Stop drains execution before clearing records. Exit report
/// lookup runs outside the service lock.
/// </summary>
internal sealed class SpeedAttachmentCoordinator
{
    private const int SpeedAttachRecordTtlMinutes = 60;

    /// <summary>
    /// Upper bound between a Cheat Engine attach and a game exit for the exit to count as a
    /// suspected injection-race crash (the BepInEx interop-generation race crashes within ~1 min).
    /// </summary>
    private static readonly TimeSpan SpeedCrashCorrelationWindow = TimeSpan.FromMinutes(2);

    private readonly IGameSpeedController? _speedController;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly SpeedAttachScheduler _speedAttachGate;
    private readonly ConcurrentDictionary<int, SpeedAttachRecord> _recentSpeedAttacks = new();
    private CheatEngineSettings? _cheatEngine;
    private readonly object _recordLock = new();
    private bool _accepting = true;
    private long _epoch;

    internal SpeedAttachmentCoordinator(
        IGameSpeedController? speedController,
        TimeProvider timeProvider,
        ILogger logger,
        IProcessIdentityProbe? processIdentityProbe,
        TimeSpan? pollInterval,
        TimeSpan? timeout)
    {
        _speedController = speedController;
        _timeProvider = timeProvider;
        _logger = logger;
        _speedAttachGate = new SpeedAttachScheduler(
            timeProvider, logger,
            (request, epoch) => ApplyAndRecordSpeed(request.DataKey, request.ProcessId, request.Speed, request.Hotkeys, epoch),
            processIdentityProbe, pollInterval, timeout);
    }

    internal void Configure(CheatEngineSettings? settings) => _cheatEngine = settings;

    // Called under the service state lock, before recovery may request an immediate attach.
    internal void Start()
    {
        lock (_recordLock) { _accepting = true; _epoch++; }
    }

    internal void CancelAll()
    {
        lock (_recordLock) { _accepting = false; _epoch++; }
        _speedAttachGate.CancelAllAndWait();
    }

    internal void ClearRecords()
    {
        lock (_recordLock) _recentSpeedAttacks.Clear();
    }

    // Capture under the service state lock; report only after releasing it (WER file I/O).
    internal SpeedAttachRecord? CaptureExit(int processId)
    {
        _speedAttachGate.CancelPending(processId);
        _recentSpeedAttacks.TryRemove(processId, out var record);
        return record;
    }

    internal void ApplyConfiguredSpeed(GameConfig config, int? processId, string? gamePath, DateTime? processStartedUtc)
    {
        if (!config.SpeedMultiplier.HasValue || _speedController is null)
        {
            return;
        }

        if (!processId.HasValue)
        {
            _logger.LogWarning(
                "Cannot apply configured speed for {DataKey}: process ID is unavailable",
                config.DataKey);
            return;
        }

        // Cheat Engine injects helper DLLs into the game on attach. When a mod loader (BepInEx /
        // MelonLoader) boots alongside, its sensitive startup phase races that injection and
        // crashes the game with zero logs. Without a resolvable game path the loader cannot be
        // detected, so the legacy immediate attach applies (same as speedAttach: immediate).
        long epoch;
        lock (_recordLock)
        {
            if (!_accepting) return;
            epoch = _epoch;
        }
        var hotkeys = CheatEngineSettings.Resolve(config.SpeedHotkeys, _cheatEngine);
        var loaderInfo = ModLoaderDetector.Detect(gamePath);
        if (IsImmediateSpeedAttach(config)
            || loaderInfo.Kind == ModLoaderKind.None
            || loaderInfo.LogPath is null
            || gamePath is null)
        {
            if (loaderInfo.Kind != ModLoaderKind.None)
            {
                _logger.LogDebug(
                    "speedAttach: immediate configured or unusable loader signal for {DataKey}; attaching speed immediately.",
                    config.DataKey);
            }

            ApplyAndRecordSpeed(config.DataKey, processId.Value, config.SpeedMultiplier.Value, hotkeys, epoch);
            return;
        }

        _speedAttachGate.ScheduleDeferred(new SpeedAttachRequest(
            config.DataKey,
            processId.Value,
            config.SpeedMultiplier.Value,
            loaderInfo.LogPath,
            processStartedUtc,
            gamePath,
            hotkeys?.ToArray()), epoch);
    }

    private static bool IsImmediateSpeedAttach(GameConfig config) =>
        string.Equals(config.SpeedAttach?.Trim(), GameConfig.SpeedAttachPolicyImmediate, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Performs the actual attach and records it for exit-correlation when the controller really
    /// launched Cheat Engine. Runs both on the immediate path and from the deferred gate callback.
    /// </summary>
    private void ApplyAndRecordSpeed(string dataKey, int processId, double speed, IReadOnlyList<SpeedHotkey>? hotkeys, long epoch)
    {
        lock (_recordLock)
        {
            if (!_accepting || epoch != _epoch) return;
        }
        try
        {
            var attached = hotkeys is { Count: > 0 }
                ? _speedController!.ApplySpeed(processId, speed, hotkeys)
                : _speedController!.ApplySpeed(processId, speed);
            if (attached)
            {
                lock (_recordLock)
                {
                    // A timed-out controller may finish later, even after a restart. Never republish
                    // its old record into the stopped service or a new lifecycle.
                    if (!_accepting || epoch != _epoch) return;
                    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
                    _recentSpeedAttacks[processId] = new SpeedAttachRecord(dataKey, nowUtc);
                    PruneSpeedAttachRecords(nowUtc);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply configured speed for {DataKey}", dataKey);
        }
    }

    private void PruneSpeedAttachRecords(DateTime nowUtc)
    {
        foreach (var pair in _recentSpeedAttacks)
        {
            if (nowUtc - pair.Value.AttachedUtc > TimeSpan.FromMinutes(SpeedAttachRecordTtlMinutes))
            {
                _recentSpeedAttacks.TryRemove(pair);
            }
        }
    }

    internal sealed record SpeedAttachRecord(string DataKey, DateTime AttachedUtc);

    /// <summary>
    /// Crash correlation for a game that exited shortly after the speed controller attached Cheat
    /// Engine into it. The BepInEx injection race leaves no game-side logs, so this keeps the
    /// association (PID, attach time, WER report) in GameHelper's own log for diagnosis.
    /// </summary>
    internal void ReportSpeedAttachExitCorrelation(
        SpeedAttachRecord record,
        string dataKey,
        string? executableName,
        DateTime stoppedUtc)
    {
        var elapsed = stoppedUtc - record.AttachedUtc;
        if (elapsed > SpeedCrashCorrelationWindow || elapsed < TimeSpan.Zero)
        {
            return;
        }

        var report = WerCrashReportFinder.FindLatest(
            Environment.GetEnvironmentVariable("ProgramData"),
            executableName,
            record.AttachedUtc);
        if (report is not null)
        {
            _logger.LogWarning(
                "Game {DataKey} exited {Elapsed} after the speed controller attached Cheat Engine (attached {Attached}) and Windows archived a crash report: {Report}. " +
                "If this exit was an unexpected startup crash, suspect the BepInEx/Cheat Engine attach race: launch the game once with its speed setting removed so BepInEx rebuilds its interop cache, then re-enable speed.",
                dataKey,
                elapsed,
                record.AttachedUtc,
                report);
        }
        else
        {
            _logger.LogInformation(
                "Game {DataKey} exited {Elapsed} after the speed controller attached Cheat Engine (attached {Attached}); no WER crash report found for {Executable}, so this exit does not look like an injection crash.",
                dataKey,
                elapsed,
                record.AttachedUtc,
                executableName ?? "n/a");
        }
    }

}
