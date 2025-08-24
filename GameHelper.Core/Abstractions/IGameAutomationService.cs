namespace GameHelper.Core.Abstractions
{
    /// <summary>
    /// Orchestrates game-related automations: subscribes to process monitor events,
    /// tracks playtime sessions, and toggles HDR when the first/last enabled game starts/stops.
    /// </summary>
    public interface IGameAutomationService
    {
        /// <summary>
        /// Initializes the automation pipeline (load config, subscribe to monitor events).
        /// Call once during application startup.
        /// </summary>
        void Start();

        /// <summary>
        /// Shuts down the automation pipeline (unsubscribe). Ensures any active
        /// playtime sessions are flushed/persisted to storage.
        /// </summary>
        void Stop();
    }
}
