using System;
using System.Collections.Generic;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using Xunit;

namespace GameHelper.Tests
{
    public class GameConfigLookupTests
    {
        [Fact]
        public void Resolve_ByDataKey_ReturnsConfig()
        {
            var cfg = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["DS2.exe"] = new()
                {
                    DataKey = "deathstranding2onthebeach",
                    ExecutableName = "DS2.exe",
                    DisplayName = "DEATH STRANDING 2",
                    IsEnabled = true
                }
            };

            var lookup = GameConfigLookup.Build(cfg);
            var result = lookup.Resolve("deathstranding2onthebeach");

            Assert.NotNull(result);
            Assert.Equal("DEATH STRANDING 2", result.DisplayName);
        }

        [Fact]
        public void Resolve_ByExecutableName_ReturnsConfig()
        {
            var cfg = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["DS2.exe"] = new()
                {
                    DataKey = "deathstranding2onthebeach",
                    ExecutableName = "DS2.exe",
                    DisplayName = "DEATH STRANDING 2",
                    IsEnabled = true
                }
            };

            var lookup = GameConfigLookup.Build(cfg);
            var result = lookup.Resolve("DS2.exe");

            Assert.NotNull(result);
            Assert.Equal("DEATH STRANDING 2", result.DisplayName);
        }

        [Fact]
        public void Resolve_ByMapKey_ReturnsConfig()
        {
            var cfg = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["DS2.exe"] = new()
                {
                    DataKey = "deathstranding2onthebeach",
                    ExecutableName = "DS2.exe",
                    DisplayName = "DEATH STRANDING 2",
                    IsEnabled = true
                }
            };

            var lookup = GameConfigLookup.Build(cfg);
            var result = lookup.Resolve("DS2.exe");

            Assert.NotNull(result);
            Assert.Equal("DS2.exe", result.ExecutableName);
        }

        [Fact]
        public void Resolve_NoMatch_ReturnsNull()
        {
            var cfg = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
            var lookup = GameConfigLookup.Build(cfg);

            var result = lookup.Resolve("unknown_game");
            Assert.Null(result);
        }

        [Fact]
        public void Resolve_AmbiguousExecutableName_ReturnsNull()
        {
            var cfg = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["game1"] = new()
                {
                    DataKey = "game1",
                    ExecutableName = "shared.exe",
                    IsEnabled = true
                },
                ["game2"] = new()
                {
                    DataKey = "game2",
                    ExecutableName = "shared.exe",
                    IsEnabled = true
                }
            };

            var lookup = GameConfigLookup.Build(cfg);
            // Ambiguous ExecutableName should not resolve when there are multiple matches
            var result = lookup.Resolve("shared.exe");
            Assert.Null(result);
        }
    }
}
