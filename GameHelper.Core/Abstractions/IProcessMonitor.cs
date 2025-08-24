using System;

namespace GameHelper.Core.Abstractions
{
    /// <summary>
    /// Provides process lifecycle notifications. Implementations emit <see cref="ProcessStarted"/> and
    /// <see cref="ProcessStopped"/> events with the process executable name (e.g., "game.exe").
    /// Must be started and stopped explicitly.
    /// </summary>
    public interface IProcessMonitor
    {
        /// <summary>
        /// Raised when a process starts. Payload is the process executable name (case-insensitive).
        /// </summary>
        event Action<string>? ProcessStarted;

        /// <summary>
        /// Raised when a process stops. Payload is the process executable name (case-insensitive).
        /// </summary>
        event Action<string>? ProcessStopped;

        /// <summary>
        /// Starts monitoring and begins raising events.
        /// </summary>
        void Start();

        /// <summary>
        /// Stops monitoring and unsubscribes internal handlers.
        /// </summary>
        void Stop();
    }
}
