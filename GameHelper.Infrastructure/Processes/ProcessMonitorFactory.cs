using System;
using System.Collections.Generic;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Exceptions;
using Microsoft.Extensions.Logging;

namespace GameHelper.Infrastructure.Processes
{
    /// <summary>
    /// Factory for creating process monitor instances based on configuration.
    /// </summary>
    public static class ProcessMonitorFactory
    {
        /// <summary>
        /// Creates a process monitor of the specified type.
        /// </summary>
        /// <param name="type">The type of process monitor to create.</param>
        /// <param name="allowedProcessNames">Optional whitelist of process names to monitor.</param>
        /// <param name="logger">Optional logger for diagnostic information.</param>
        /// <returns>A configured process monitor instance.</returns>
        /// <exception cref="ArgumentException">Thrown when an unsupported monitor type is specified.</exception>
        /// <exception cref="InsufficientPrivilegesException">Thrown when ETW monitor requires administrator privileges.</exception>
        public static IProcessMonitor Create(
            ProcessMonitorType type,
            IEnumerable<string>? allowedProcessNames = null,
            ILogger? logger = null)
        {
            return type switch
            {
                ProcessMonitorType.ETW => new EtwProcessMonitor(allowedProcessNames, logger as ILogger<EtwProcessMonitor>),
                ProcessMonitorType.WMI => new WmiProcessMonitor(allowedProcessNames),
                _ => throw new ArgumentException($"Unsupported monitor type: {type}", nameof(type))
            };
        }

        /// <summary>
        /// Creates a process monitor with automatic fallback from ETW to WMI if ETW fails.
        /// </summary>
        /// <param name="preferredType">The preferred monitor type to try first.</param>
        /// <param name="allowedProcessNames">Optional whitelist of process names to monitor.</param>
        /// <param name="logger">Optional logger for diagnostic information.</param>
        /// <returns>A configured process monitor instance, potentially with fallback applied.</returns>
        public static IProcessMonitor CreateWithFallback(
            ProcessMonitorType preferredType,
            IEnumerable<string>? allowedProcessNames = null,
            ILogger? logger = null)
        {
            // If WMI is preferred, just create it directly
            if (preferredType == ProcessMonitorType.WMI)
            {
                logger?.LogInformation("Creating WMI process monitor (preferred)");
                return Create(ProcessMonitorType.WMI, allowedProcessNames, logger);
            }

            // Try ETW first, fall back to WMI if it fails
            try
            {
                logger?.LogInformation("Attempting to create ETW process monitor");
                var etwMonitor = Create(ProcessMonitorType.ETW, allowedProcessNames, logger);
                
                // Test if we can actually start the ETW monitor
                etwMonitor.Start();
                etwMonitor.Stop();
                
                logger?.LogInformation("ETW process monitor created successfully");
                return etwMonitor;
            }
            catch (InsufficientPrivilegesException ex)
            {
                logger?.LogWarning(ex, "ETW monitor requires administrator privileges, falling back to WMI");
                return Create(ProcessMonitorType.WMI, allowedProcessNames, logger);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to create ETW monitor, falling back to WMI");
                return Create(ProcessMonitorType.WMI, allowedProcessNames, logger);
            }
        }

        /// <summary>
        /// Creates a no-op process monitor for testing or disabled scenarios.
        /// </summary>
        /// <returns>A no-op process monitor instance.</returns>
        public static IProcessMonitor CreateNoOp()
        {
            return new NoOpProcessMonitor();
        }
    }
}