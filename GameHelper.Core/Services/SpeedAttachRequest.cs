using System;

namespace GameHelper.Core.Services;

/// <summary>Deferred speed attach request produced when a mod loader was detected at game start.</summary>
/// <param name="DataKey">Game config key, for logging only.</param>
/// <param name="ProcessId">Game process the speed applies to.</param>
/// <param name="Speed">Configured speed multiplier.</param>
/// <param name="ChainloaderLogPath">Loader log rewritten per launch; completion marker source. Null disables the marker check (timeout-only gating).</param>
/// <param name="ProcessStartedUtc">
/// Game process start time. Loader log writes older than this belong to a previous run, so the
/// completion marker already present after a fast boot still counts. Null falls back to the gate start.
/// </param>
/// <param name="Hotkeys">Hotkey snapshot from the same game activation as Speed.</param>
/// <param name="ExpectedProcessPath">Executable path the PID must still own at attach time; guards against PID reuse during the wait.</param>
public sealed record SpeedAttachRequest(
    string DataKey,
    int ProcessId,
    double Speed,
    string? ChainloaderLogPath,
    DateTime? ProcessStartedUtc,
    string? ExpectedProcessPath,
    IReadOnlyList<GameHelper.Core.Models.SpeedHotkey>? Hotkeys = null);
