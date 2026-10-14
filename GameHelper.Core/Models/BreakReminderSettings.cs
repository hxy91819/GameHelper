namespace GameHelper.Core.Models;

/// <summary>Global foreground-game break reminder settings. Missing configuration is disabled.</summary>
public sealed record BreakReminderSettings
{
    public bool Enabled { get; init; }
    public int PlayMinutes { get; init; } = 40;
    public int BreakMinutes { get; init; } = 5;
    public int RepeatMinutes { get; init; } = 10;
    public bool Sound { get; init; } = true;

    /// <summary>Rejects invalid durations without silently replacing user configuration.</summary>
    public void Validate()
    {
        if (PlayMinutes <= 0 || BreakMinutes <= 0 || RepeatMinutes <= 0)
        {
            throw new ArgumentException("breakReminder playMinutes, breakMinutes and repeatMinutes must be positive integers.");
        }
    }
}
