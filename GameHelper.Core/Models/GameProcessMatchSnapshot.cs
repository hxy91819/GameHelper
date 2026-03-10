using System.Collections.Generic;

namespace GameHelper.Core.Models;

public sealed class GameProcessMatchSnapshot
{
    public IReadOnlyDictionary<string, GameConfig> Configs { get; init; } = new Dictionary<string, GameConfig>();

    public IReadOnlyDictionary<string, GameConfig> ConfigsByDataKey { get; init; } = new Dictionary<string, GameConfig>();

    public IReadOnlyDictionary<string, GameConfig> ConfigsByPath { get; init; } = new Dictionary<string, GameConfig>();

    public IReadOnlyList<GameProcessNameEntry> NameEntries { get; init; } = [];
}
