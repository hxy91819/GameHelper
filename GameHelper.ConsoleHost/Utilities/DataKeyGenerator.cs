using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GameHelper.Core.Abstractions;

namespace GameHelper.ConsoleHost.Utilities
{
    /// <summary>
    /// Centralized DataKey generation logic to ensure consistency across the application.
    /// </summary>
    public static class DataKeyGenerator
    {
        /// <summary>
        /// Generates a unique DataKey from executable path and optional product name.
        /// Ensures uniqueness by appending a suffix if the key already exists.
        /// </summary>
        /// <param name="exePath">Full path to the executable file.</param>
        /// <param name="productName">Optional product name from metadata.</param>
        /// <param name="configProvider">Config provider to check for existing keys.</param>
        /// <returns>A unique DataKey string.</returns>
        public static string GenerateUniqueDataKey(
            string exePath, 
            string? productName, 
            IConfigProvider configProvider)
        {
            // Generate base key
            var baseKey = GenerateBaseDataKey(exePath, productName);
            
            // Check for uniqueness
            var existingConfigs = configProvider.Load();
            var existingKeys = existingConfigs.Values
                .Select(c => c.DataKey)
                .Where(k => !string.IsNullOrEmpty(k))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            
            // If unique, return as-is
            if (!existingKeys.Contains(baseKey))
            {
                return baseKey;
            }
            
            // Otherwise, append a numeric suffix
            int suffix = 2;
            string uniqueKey;
            do
            {
                uniqueKey = $"{baseKey}{suffix}";
                suffix++;
            } while (existingKeys.Contains(uniqueKey));
            
            return uniqueKey;
        }
        
        /// <summary>
        /// Generates a base DataKey without uniqueness check.
        /// Used by migration tool where uniqueness is guaranteed by source data.
        /// </summary>
        /// <param name="exePath">Full path to the executable file.</param>
        /// <param name="productName">Optional product name from metadata.</param>
        /// <returns>A normalized DataKey string.</returns>
        public static string GenerateBaseDataKey(string exePath, string? productName = null)
        {
            string baseKey;
            
            // Prefer ProductName if available and suitable
            if (!string.IsNullOrWhiteSpace(productName) && IsSuitableProductName(productName))
            {
                baseKey = NormalizeDataKey(productName);
            }
            else
            {
                // Fallback to filename
                var fileName = GetFileNameWithoutExtensionCrossPlatform(exePath);
                baseKey = NormalizeDataKey(fileName);
            }
            
            return baseKey;
        }

        private static string GetFileNameWithoutExtensionCrossPlatform(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            // Normalize Windows separators so macOS/Linux can also parse Windows-style paths.
            var normalizedPath = path.Replace('\\', '/');
            return Path.GetFileNameWithoutExtension(normalizedPath);
        }
        
        /// <summary>
        /// Normalizes a string to be used as a DataKey.
        /// Rules: lowercase, no spaces, no special characters except underscore and hyphen.
        /// </summary>
        private static string NormalizeDataKey(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }
            
            // Convert to lowercase
            string normalized = input.ToLowerInvariant();
            
            // Remove or replace special characters
            // Keep: letters, numbers, underscore, hyphen
            // Replace spaces with empty string
            normalized = Regex.Replace(normalized, @"[^\w\-]", "");
            
            // Remove leading/trailing hyphens or underscores
            normalized = normalized.Trim('-', '_');
            
            return normalized;
        }
        
        /// <summary>
        /// Checks if a ProductName is suitable to use as a DataKey base.
        /// Rejects names that are too generic or contain too many special characters.
        /// </summary>
        private static bool IsSuitableProductName(string productName)
        {
            if (string.IsNullOrWhiteSpace(productName))
            {
                return false;
            }
            
            // Reject if too short (likely generic)
            if (productName.Length < 3)
            {
                return false;
            }
            
            // Reject if mostly non-alphanumeric (after normalization would be too short)
            var alphanumericCount = productName.Count(char.IsLetterOrDigit);
            if (alphanumericCount < 3)
            {
                return false;
            }
            
            // Reject common generic names
            var genericNames = new[] { "game", "application", "app", "program", "launcher" };
            if (genericNames.Contains(productName.ToLowerInvariant()))
            {
                return false;
            }
            
            return true;
        }
    }
}
