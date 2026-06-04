using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace GameHelper.ConsoleHost.Utilities
{
    /// <summary>
    /// Provides build-time metadata (version, build date, commit id) for the running assembly.
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
        /// Returns the commit SHA embedded in AssemblyInformationalVersionAttribute (after the '+').
        /// Falls back to "unknown".
        /// </summary>
        public static string GetCommitId()
        {
            try
            {
                var assembly = Assembly.GetEntryAssembly() ?? typeof(BuildInfoHelper).Assembly;
                var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (string.IsNullOrWhiteSpace(informational))
                    return "unknown";

                var plusIndex = informational.IndexOf('+');
                if (plusIndex < 0 || plusIndex + 1 >= informational.Length)
                    return "unknown";

                var commitId = informational[(plusIndex + 1)..];
                // Some build systems append metadata after the commit; take the first segment only.
                var firstSegment = commitId.Split(new[] { '-', '.' }, 2)[0];
                return string.IsNullOrWhiteSpace(firstSegment) ? "unknown" : firstSegment;
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
                // Use Process.MainModule instead of Assembly.Location because
                // Location returns empty string for single-file published apps.
                string? path = null;
                try
                {
                    path = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                }
                catch { }

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
