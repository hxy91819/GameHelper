using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace GameHelper.ConsoleHost.Utilities
{
    /// <summary>
    /// Extracts metadata from executable files using FileVersionInfo.
    /// </summary>
    public static class GameMetadataExtractor
    {
        /// <summary>
        /// Attempts to extract ProductName and CompanyName from an executable file.
        /// </summary>
        /// <param name="exePath">Path to the executable file.</param>
        /// <param name="logger">Optional logger for diagnostic messages.</param>
        /// <returns>A tuple containing ProductName and CompanyName, or null if extraction fails.</returns>
        public static (string? ProductName, string? CompanyName) ExtractMetadata(string exePath, ILogger? logger = null)
        {
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return (null, null);
            }

            if (!File.Exists(exePath))
            {
                logger?.LogDebug("File does not exist: {Path}", exePath);
                return (null, null);
            }

            try
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(exePath);
                var productName = string.IsNullOrWhiteSpace(versionInfo.ProductName) 
                    ? null 
                    : versionInfo.ProductName.Trim();
                var companyName = string.IsNullOrWhiteSpace(versionInfo.CompanyName) 
                    ? null 
                    : versionInfo.CompanyName.Trim();

                logger?.LogDebug("Extracted metadata from {Path}: ProductName={ProductName}, CompanyName={CompanyName}", 
                    exePath, productName ?? "(null)", companyName ?? "(null)");

                return (productName, companyName);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Failed to extract metadata from {Path}", exePath);
                return (null, null);
            }
        }

        /// <summary>
        /// Generates a suggested DataKey from metadata or file name.
        /// This method is deprecated. Use DataKeyGenerator.GenerateBaseDataKey() instead.
        /// </summary>
        /// <param name="exePath">Path to the executable file.</param>
        /// <param name="productName">Optional product name from metadata.</param>
        /// <returns>A suggested DataKey string.</returns>
        [Obsolete("Use DataKeyGenerator.GenerateBaseDataKey() for consistent DataKey generation")]
        public static string GenerateSuggestedDataKey(string exePath, string? productName)
        {
            return DataKeyGenerator.GenerateBaseDataKey(exePath, productName);
        }
    }
}
