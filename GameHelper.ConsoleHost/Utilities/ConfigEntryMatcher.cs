using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameHelper.Core.Models;

namespace GameHelper.ConsoleHost.Utilities
{
    /// <summary>
    /// Shared matching policy for add/import flows.
    /// Path-exact match wins; name-only fallback is allowed only for a single candidate without path.
    /// </summary>
    public static class ConfigEntryMatcher
    {
        public static GameConfig? FindExistingForAdd(
            IEnumerable<GameConfig> configs,
            string executableName,
            string? executablePath)
        {
            var candidates = configs?.Where(c => c is not null).ToList() ?? new List<GameConfig>();
            var normalizedPath = NormalizePath(executablePath);

            if (!string.IsNullOrWhiteSpace(normalizedPath))
            {
                var byPath = candidates.FirstOrDefault(cfg =>
                    !string.IsNullOrWhiteSpace(cfg.ExecutablePath) &&
                    string.Equals(NormalizePath(cfg.ExecutablePath), normalizedPath, StringComparison.OrdinalIgnoreCase));
                if (byPath is not null)
                {
                    return byPath;
                }
            }

            var sameNameCandidates = candidates
                .Where(cfg => string.Equals(cfg.ExecutableName, executableName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (sameNameCandidates.Count == 1 && string.IsNullOrWhiteSpace(sameNameCandidates[0].ExecutablePath))
            {
                return sameNameCandidates[0];
            }

            return null;
        }

        public static string NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.Trim().TrimEnd('\\', '/');
            }
        }
    }
}
