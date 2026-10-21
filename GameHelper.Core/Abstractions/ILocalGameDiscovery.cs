using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

/// <summary>Owns bounded local discovery and evidence-based installation status; never executes candidates.</summary>
public interface ILocalGameDiscovery
{
    Task<LocalGameScan> ScanAsync(string directory, IReadOnlyList<GameConfig> existing, CancellationToken cancellationToken);
    IReadOnlyDictionary<string, GameInstallation> Assess(IReadOnlyList<GameConfig> games);
}
