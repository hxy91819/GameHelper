using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Tests.Api;

internal sealed class InMemoryConfigProvider : IConfigProvider, IAppConfigProvider
{
    private Dictionary<string, GameConfig> _configs = new(StringComparer.OrdinalIgnoreCase);
    private AppConfig _appConfig = new();

    public IReadOnlyDictionary<string, GameConfig> Load() => _configs;

    public void Save(IReadOnlyDictionary<string, GameConfig> configs)
    {
        _configs = new Dictionary<string, GameConfig>(configs, StringComparer.OrdinalIgnoreCase);
    }

    public AppConfig LoadAppConfig() => _appConfig;

    public void SaveAppConfig(AppConfig appConfig)
    {
        _appConfig = appConfig;
    }

    public void Seed(params GameConfig[] games)
    {
        foreach (var g in games)
            _configs[g.DataKey] = g;
    }
}
