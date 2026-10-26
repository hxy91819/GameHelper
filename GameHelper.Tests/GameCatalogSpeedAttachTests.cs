using System.Collections.Generic;
using System.Linq;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using Xunit;

namespace GameHelper.Tests;

public sealed class GameCatalogSpeedAttachTests
{
    [Fact]
    public void Update_KeepsPerGameSpeedAttachPolicy()
    {
        var configuration = new InMemoryGameConfiguration(new AppConfig
        {
            Games = new List<GameConfig>
            {
                new()
                {
                    DataKey = "game",
                    Executable = @"D:\Games\Game\Game.exe",
                    SpeedAttach = GameConfig.SpeedAttachPolicyImmediate
                }
            }
        });
        var catalog = new GameCatalogService(configuration);

        catalog.Update("game", new GameCatalogUpdateRequest { DisplayName = "Renamed" });

        var saved = Assert.Single(configuration.Read().Games);
        Assert.Equal("Renamed", saved.DisplayName);
        Assert.Equal(GameConfig.SpeedAttachPolicyImmediate, saved.SpeedAttach);
    }

    private sealed class InMemoryGameConfiguration : IGameConfiguration
    {
        private AppConfig _config;

        public InMemoryGameConfiguration(AppConfig config)
        {
            _config = config;
        }

        public AppConfig Read() => _config;

        public AppConfig Change(System.Action<AppConfig> change)
        {
            var copy = new AppConfig { Games = _config.Games.Select(game => new GameConfig
            {
                DataKey = game.DataKey,
                Executable = game.Executable,
                DisplayName = game.DisplayName,
                IsEnabled = game.IsEnabled,
                HdrEnabled = game.HdrEnabled,
                SpeedMultiplier = game.SpeedMultiplier,
                SpeedHotkeys = game.SpeedHotkeys,
                SpeedAttach = game.SpeedAttach
            }).ToList() };
            change(copy);
            _config = copy;
            return copy;
        }
    }
}
