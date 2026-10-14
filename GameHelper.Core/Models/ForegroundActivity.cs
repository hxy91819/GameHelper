namespace GameHelper.Core.Models;

/// <summary>A desktop observation. Unavailable observations cannot establish a completed break.</summary>
public sealed record ForegroundActivity(
    bool IsAvailable,
    ProcessEventInfo? Process = null,
    long? ProcessStartTicks = null,
    bool IsAway = false,
    TimeSpan SuspendedFor = default)
{
    public static ForegroundActivity Unavailable { get; } = new(false);
    public static ForegroundActivity Away { get; } = new(true, IsAway: true);
}
