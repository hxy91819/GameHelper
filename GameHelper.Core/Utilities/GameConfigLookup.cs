using System;
using System.Collections.Generic;
using System.Linq;
using GameHelper.Core.Models;

namespace GameHelper.Core.Utilities
{
    /// <summary>
    /// Read-only lookup indexes for resolving configs by DataKey / ExecutableName / map key.
    /// </summary>
    public sealed class GameConfigLookup
    {
        private GameConfigLookup(
            IReadOnlyDictionary<string, GameConfig> byMapKey,
            IReadOnlyDictionary<string, GameConfig> byDataKey,
            IReadOnlyDictionary<string, IReadOnlyList<GameConfig>> byExecutableName)
        {
            ByMapKey = byMapKey;
            ByDataKey = byDataKey;
            ByExecutableName = byExecutableName;
        }

        public IReadOnlyDictionary<string, GameConfig> ByMapKey { get; }

        public IReadOnlyDictionary<string, GameConfig> ByDataKey { get; }

        public IReadOnlyDictionary<string, IReadOnlyList<GameConfig>> ByExecutableName { get; }

        public static GameConfigLookup Build(IReadOnlyDictionary<string, GameConfig> configs)
        {
            var byDataKey = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var config in configs.Values.Where(c => c is not null && !string.IsNullOrWhiteSpace(c.DataKey)))
            {
                byDataKey.TryAdd(config.DataKey!, config);
            }

            var byExecutableName = configs.Values
                .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.ExecutableName))
                .GroupBy(c => c.ExecutableName!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<GameConfig>)g.ToList(), StringComparer.OrdinalIgnoreCase);

            return new GameConfigLookup(configs, byDataKey, byExecutableName);
        }

        public GameConfig? Resolve(string key)
        {
            if (ByDataKey.TryGetValue(key, out var byDataKeyConfig))
            {
                return byDataKeyConfig;
            }

            if (ByExecutableName.TryGetValue(key, out var byExecutableNameConfigs) &&
                byExecutableNameConfigs.Count == 1)
            {
                return byExecutableNameConfigs[0];
            }

            return ByMapKey.TryGetValue(key, out var byMapKeyConfig) ? byMapKeyConfig : null;
        }
    }
}
