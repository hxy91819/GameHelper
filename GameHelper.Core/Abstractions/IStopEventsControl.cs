using System;

namespace GameHelper.Core.Abstractions
{
    /// <summary>
    /// Optional capability for a process monitor to enable/disable Stop events dynamically.
    /// Implementations should keep Start events always enabled and toggle only Stop events.
    /// </summary>
    public interface IStopEventsControl
    {
        /// <summary>
        /// Enables or disables emitting ProcessStopped events.
        /// </summary>
        /// <param name="enabled">True to enable stop events; false to disable.</param>
        void SetStopEventsEnabled(bool enabled);
    }
}
