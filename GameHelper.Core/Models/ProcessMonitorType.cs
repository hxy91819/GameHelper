namespace GameHelper.Core.Models
{
    /// <summary>
    /// Specifies the type of process monitor to use for detecting process lifecycle events.
    /// </summary>
    public enum ProcessMonitorType
    {
        /// <summary>
        /// Use Windows Management Instrumentation (WMI) for process monitoring.
        /// Compatible with all Windows versions but has higher latency.
        /// </summary>
        WMI,

        /// <summary>
        /// Use Event Tracing for Windows (ETW) for process monitoring.
        /// Provides lower latency but requires administrator privileges.
        /// </summary>
        ETW
    }
}