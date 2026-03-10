using System.Collections.Generic;
using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

public interface IGameProcessMatcher
{
    GameProcessMatchSnapshot CreateSnapshot(IReadOnlyDictionary<string, GameConfig> configs);

    GameProcessMatchResult? Match(ProcessEventInfo processInfo, GameProcessMatchSnapshot snapshot);

    string? NormalizeName(string? executableName);

    string? NormalizePath(string? executablePath);
}
