using System;
using System.Collections.Generic;
using System.Linq;

namespace GameHelper.Core.Utilities
{
    /// <summary>
    /// Shared identity utilities for configuration entry keys.
    /// </summary>
    public static class ConfigIdentity
    {
        public static string EnsureUniqueDataKey(string? dataKey, ISet<string> usedDataKeys, string fallbackBase = "game")
        {
            var baseKey = string.IsNullOrWhiteSpace(dataKey) ? fallbackBase : dataKey.Trim();
            if (baseKey.Length == 0)
            {
                baseKey = fallbackBase;
            }

            if (usedDataKeys.Add(baseKey))
            {
                return baseKey;
            }

            var suffix = 2;
            while (true)
            {
                var candidate = $"{baseKey}{suffix}";
                if (usedDataKeys.Add(candidate))
                {
                    return candidate;
                }

                suffix++;
            }
        }

        public static string EnsureUniqueDataKey(string? dataKey, IEnumerable<string?> existingDataKeys, string fallbackBase = "game")
        {
            var used = new HashSet<string>(
                existingDataKeys
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v!.Trim()),
                StringComparer.OrdinalIgnoreCase);

            return EnsureUniqueDataKey(dataKey, used, fallbackBase);
        }
    }
}
