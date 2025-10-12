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
        /// Stops the active session for the given game name and persists it.
        /// Safe to call even if no session is active.
        /// </summary>
        /// <returns>The completed <see cref="Models.PlaySession"/> when a session was active; otherwise, null.</returns>
        Models.PlaySession? StopTracking(string gameName);
    }
}
