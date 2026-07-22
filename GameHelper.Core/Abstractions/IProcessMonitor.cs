using System;
using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions
{
    /// <summary>
    /// Provides process lifecycle notifications. Implementations emit <see cref="ProcessStarted"/> and
    /// <see cref="ProcessStopped"/> events with the process executable name (e.g., "game.exe").
    /// Must be started and stopped explicitly.
    /// </summary>
    public interface IProcessMonitor : IDisposable
    {
        /// <summary>
        /// Raised when a process starts. Payload contains both executable name and optional full path information.
        /// </summary>
        event Action<ProcessEventInfo>? ProcessStarted;

        /// <summary>
        /// Raised when a process stops. Payload contains both executable name and optional full path information.
        /// </summary>
        event Action<ProcessEventInfo>? ProcessStopped;

        /// <summary>
        /// Atomically replaces the process-name gate and stop-event policy. Implementations must accept
        /// configuration both before and while monitoring.
        /// </summary>
        void Configure(ProcessObservationPolicy policy);

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
