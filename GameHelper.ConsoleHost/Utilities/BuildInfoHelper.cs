using System;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace GameHelper.ConsoleHost.Utilities
{
    /// <summary>
    /// Provides build-time metadata (version, build date) for the running assembly.
    /// </summary>
    public static class BuildInfoHelper
    {
        /// <summary>
        /// Returns the application version, preferring AssemblyInformationalVersionAttribute.
        /// Falls back to AssemblyName.Version, then "unknown".
        /// </summary>
        public static string GetVersionDescription()
        {
            try
            {
                var assembly = Assembly.GetEntryAssembly() ?? typeof(BuildInfoHelper).Assembly;
                var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(informational))
                {
                    var plusIndex = informational.IndexOf('+');
                    return plusIndex > 0 ? informational[..plusIndex] : informational;
                }

                return assembly.GetName().Version?.ToString() ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        /// <summary>
        /// Returns the build timestamp derived from the entry assembly's last-write time.
        /// Falls back to the current process main module, then "unknown".
        /// </summary>
        public static string GetBuildTimeDescription()
        {
            try
            {
                string? path = Assembly.GetEntryAssembly()?.Location;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    try
                    {
                        path = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    }
                    catch
                    {
                        path = null;
                    }
                }

                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return "unknown";
                }

                return File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch
            {
                return "unknown";
            }
        }
    }
}
