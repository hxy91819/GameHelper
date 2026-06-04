using System;
using System.Collections.Generic;
using System.Linq;
using GameHelper.Core.Models;

namespace GameHelper.ConsoleHost.Utilities
{
    /// <summary>
    /// Resolves a human-readable display name for a game given its stored identifier (DataKey)
    /// and the loaded configuration map.
    /// </summary>
    public static class GameDisplayNameResolver
    {
        /// <summary>
        /// Priority: DisplayName/Alias > ExecutableName > raw gameName.
        /// The config dictionary is keyed by ExecutableName, so if a direct lookup fails
        /// we fall back to scanning by DataKey.
        /// </summary>
        public static string ResolveDisplayName(Dictionary<string, GameConfig> cfg, string gameName)
        {
            var gc = FindConfigByGameName(cfg, gameName);
            if (!string.IsNullOrWhiteSpace(gc?.DisplayName))
                return gc.DisplayName!;
            if (!string.IsNullOrWhiteSpace(gc?.ExecutableName))
                return gc.ExecutableName;
            return gameName;
        }

        /// <summary>
        /// Looks up a config by dictionary key (ExecutableName) first, then by DataKey.
        /// </summary>
        public static GameConfig? FindConfigByGameName(Dictionary<string, GameConfig> cfg, string gameName)
        {
            if (cfg.TryGetValue(gameName, out var result))
                return result;

            return cfg.Values.FirstOrDefault(c =>
                string.Equals(c.DataKey, gameName, StringComparison.OrdinalIgnoreCase));
        }
    }
}
