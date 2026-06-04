using System;
using System.Collections.Generic;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Models;
using Xunit;

namespace GameHelper.Tests
{
    public class GameDisplayNameResolverTests
    {
        [Fact]
        public void ResolveDisplayName_ByDataKey_FindsConfigAndReturnsDisplayName()
        {
            // Arrange: config dictionary keyed by ExecutableName, but CSV stores DataKey
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

            // Act: lookup by DataKey (what is stored in CSV)
            var name = GameDisplayNameResolver.ResolveDisplayName(cfg, "deathstranding2onthebeach");

            // Assert: should resolve to DisplayName
            Assert.Equal("DEATH STRANDING 2", name);
        }

        [Fact]
        public void ResolveDisplayName_ByExecutableName_FindsConfigAndReturnsDisplayName()
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

            var name = GameDisplayNameResolver.ResolveDisplayName(cfg, "DS2.exe");
            Assert.Equal("DEATH STRANDING 2", name);
        }

        [Fact]
        public void ResolveDisplayName_NoDisplayName_FallsBackToExecutableName()
        {
            var cfg = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["DS2.exe"] = new()
                {
                    DataKey = "deathstranding2onthebeach",
                    ExecutableName = "DS2.exe",
                    DisplayName = null,
                    IsEnabled = true
                }
            };

            var name = GameDisplayNameResolver.ResolveDisplayName(cfg, "deathstranding2onthebeach");
            Assert.Equal("DS2.exe", name);
        }

        [Fact]
        public void ResolveDisplayName_NoConfig_ReturnsRawGameName()
        {
            var cfg = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);

            var name = GameDisplayNameResolver.ResolveDisplayName(cfg, "unknown_game");
            Assert.Equal("unknown_game", name);
        }

        [Fact]
        public void FindConfigByGameName_Priority_ExecutableNameKeyFirst()
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

            // Direct dictionary key lookup
            var byExe = GameDisplayNameResolver.FindConfigByGameName(cfg, "DS2.exe");
            Assert.NotNull(byExe);
            Assert.Equal("DS2.exe", byExe.ExecutableName);

            // DataKey fallback scan
            var byDataKey = GameDisplayNameResolver.FindConfigByGameName(cfg, "deathstranding2onthebeach");
            Assert.NotNull(byDataKey);
            Assert.Equal("deathstranding2onthebeach", byDataKey.DataKey);
        }
    }
}
