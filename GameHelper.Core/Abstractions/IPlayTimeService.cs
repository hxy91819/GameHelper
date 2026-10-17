namespace GameHelper.Core.Abstractions
{
    /// <summary>
    /// Tracks gameplay sessions per game. Implementations are responsible for persisting
    /// sessions (e.g., to a file) so that statistics can be computed later.
    /// </summary>
    public interface IPlayTimeService
    {
    /// <summary>
    /// Starts (or continues) tracking a session for the given game name.
    /// Idempotent if called repeatedly for the same active session.
    /// </summary>
    void StartTracking(string gameName);

    /// <summary>
    /// Starts tracking the game with the original session start time carried over from a
    /// previous GameHelper run (in-flight session recovery). The continued session is
    /// flushed as one continuous record when it eventually stops.
    /// </summary>
    void ResumeTracking(string gameName, DateTime startTime);

    /// <summary>
    /// Stops the active session for the given game name and persists it.
    /// Safe to call even if no session is active.
    /// </summary>
    /// <returns>The completed <see cref="Models.PlaySession"/> when a session was active; otherwise, null.</returns>
    Models.PlaySession? StopTracking(string gameName);

    /// <summary>
    /// Persists a completed session row recovered from a prior run's in-flight snapshot
    /// (the game exited while GameHelper was down). Does not touch live tracking state.
    /// </summary>
    void WriteRecoveredSession(string gameName, DateTime startTime, DateTime endTime);
    }
}
