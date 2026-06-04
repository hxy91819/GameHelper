using System;
using System.Collections.Generic;
using System.IO;
using GameHelper.ConsoleHost.Services;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Providers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GameHelper.Tests;

public sealed class FileDropHandlerTests
{
    [Fact]
    public void LooksLikeFilePaths_ReturnsTrueForExistingExeFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var exePath = Path.Combine(tempDir, "SampleGame.exe");
            File.WriteAllText(exePath, string.Empty);

            var result = FileDropHandler.LooksLikeFilePaths(new[] { exePath });

            Assert.True(result);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ProcessFilePaths_AddsNewExecutableWithDefaults()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.yml");
        var exePath = Path.Combine(tempDir, "NewAdventure.exe");
        File.WriteAllText(exePath, string.Empty);

        using var services = new ServiceCollection().BuildServiceProvider();

        try
        {
            var summary = FileDropHandler.ProcessFilePaths(new[] { exePath }, configPath, services);

            Assert.Equal(1, summary.Added);
            Assert.Equal(0, summary.Updated);
            Assert.Equal(0, summary.Skipped);

            var provider = new YamlConfigProvider(configPath);
            var map = provider.Load();

            var config = Assert.Single(map.Values);
            Assert.False(string.IsNullOrWhiteSpace(config.EntryId));
            Assert.Equal("newadventure", config.DataKey); // DataKey is normalized: lowercase, no .exe
            Assert.Equal("NewAdventure.exe", config.ExecutableName);
            Assert.Equal("NewAdventure", config.DisplayName);
            Assert.True(config.IsEnabled);
            Assert.False(config.HDREnabled);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ProcessFilePaths_UpdatesExistingGameAndPreservesHdrChoice()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.yml");
        var exePath = Path.Combine(tempDir, "LegacyClassic.exe");
        File.WriteAllText(exePath, string.Empty);

        var seed = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["legacy-entry"] = new GameConfig
            {
                EntryId = "legacy-entry",
                DataKey = "LegacyClassic", // Existing DataKey without .exe
                ExecutableName = "LegacyClassic.exe",
                DisplayName = "旧版经典",
                IsEnabled = false,
                HDREnabled = false
            }
        };
        var seedProvider = new YamlConfigProvider(configPath);
        seedProvider.Save(seed);

        using var services = new ServiceCollection().BuildServiceProvider();

        try
        {
            var summary = FileDropHandler.ProcessFilePaths(new[] { exePath }, configPath, services);

            Assert.Equal(0, summary.Added);
            Assert.Equal(1, summary.Updated);
            Assert.Equal(0, summary.Skipped);

            var provider = new YamlConfigProvider(configPath);
            var map = provider.Load();

            var config = Assert.Single(map.Values);
            Assert.Equal("LegacyClassic", config.DataKey); // DataKey should be preserved
            Assert.Equal("LegacyClassic.exe", config.ExecutableName);
            Assert.Equal("LegacyClassic", config.DisplayName);
            Assert.True(config.IsEnabled); // Updated to enabled
            Assert.False(config.HDREnabled); // HDR preserved
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ProcessFilePaths_UsesSteamResolverForUrlShortcuts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.yml");
        var urlPath = Path.Combine(tempDir, "SteamShortcut.url");
        var resolvedExe = Path.Combine(tempDir, "SteamAdventure.exe");
        File.WriteAllText(urlPath, "URL=steam://rungameid/4242");
        File.WriteAllText(resolvedExe, string.Empty);

        var resolver = new StubSteamResolver
        {
            UrlToReturn = "steam://rungameid/4242",
            AppIdToReturn = "4242",
            ExeToReturn = resolvedExe
        };

        using var services = new ServiceCollection()
            .AddSingleton<ISteamGameResolver>(resolver)
            .BuildServiceProvider();

        try
        {
            var summary = FileDropHandler.ProcessFilePaths(new[] { urlPath }, configPath, services);

            Assert.Equal(1, summary.Added);
            Assert.Equal(0, summary.Updated);
            Assert.Equal(0, summary.Skipped);

            Assert.Contains(urlPath, resolver.ShortcutPaths);
            Assert.Contains(resolver.UrlToReturn!, resolver.Urls);
            Assert.Contains(resolver.AppIdToReturn!, resolver.AppIds);

            var provider = new YamlConfigProvider(configPath);
            var map = provider.Load();

            var config = Assert.Single(map.Values);
            Assert.Equal("steamadventure", config.DataKey); // DataKey is normalized: lowercase, no .exe
            Assert.Equal("SteamAdventure.exe", config.ExecutableName);
            Assert.Equal("SteamShortcut", config.DisplayName); // DisplayName from .url filename
            Assert.True(config.IsEnabled);
            Assert.False(config.HDREnabled);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ProcessFilePaths_SameExecutableNameDifferentPath_AddsNewEntry()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var oldDir = Path.Combine(tempDir, "old");
        var newDir = Path.Combine(tempDir, "new");
        Directory.CreateDirectory(oldDir);
        Directory.CreateDirectory(newDir);

        var configPath = Path.Combine(tempDir, "config.yml");
        var oldExePath = Path.Combine(oldDir, "Game.exe");
        var newExePath = Path.Combine(newDir, "Game.exe");
        File.WriteAllText(oldExePath, string.Empty);
        File.WriteAllText(newExePath, string.Empty);

        var seed = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["entry1"] = new GameConfig
            {
                EntryId = "entry1",
                DataKey = "game",
                ExecutableName = "Game.exe",
                ExecutablePath = oldExePath,
                DisplayName = "Old Game",
                IsEnabled = true,
                HDREnabled = false
            }
        };
        new YamlConfigProvider(configPath).Save(seed);

        using var services = new ServiceCollection().BuildServiceProvider();

        try
        {
            var summary = FileDropHandler.ProcessFilePaths(new[] { newExePath }, configPath, services);

            Assert.Equal(1, summary.Added);
            Assert.Equal(0, summary.Updated);

            var loaded = new YamlConfigProvider(configPath).Load().Values.ToList();
            Assert.Equal(2, loaded.Count);
            Assert.Contains(loaded, c => string.Equals(c.ExecutablePath, oldExePath, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(loaded, c => string.Equals(c.ExecutablePath, newExePath, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(loaded, c => c.DataKey == "game");
            Assert.Contains(loaded, c => c.DataKey == "game2");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ProcessFilePaths_SamePath_ReusesEntryAndUpdates()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.yml");
        var exePath = Path.Combine(tempDir, "Game.exe");
        File.WriteAllText(exePath, string.Empty);

        var seed = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["entry1"] = new GameConfig
            {
                EntryId = "entry1",
                DataKey = "game",
                ExecutableName = "Game.exe",
                ExecutablePath = exePath,
                DisplayName = "Old Name",
                IsEnabled = false,
                HDREnabled = true
            }
        };
        new YamlConfigProvider(configPath).Save(seed);

        using var services = new ServiceCollection().BuildServiceProvider();

        try
        {
            var summary = FileDropHandler.ProcessFilePaths(new[] { exePath }, configPath, services);

            Assert.Equal(0, summary.Added);
            Assert.Equal(1, summary.Updated);

            var config = Assert.Single(new YamlConfigProvider(configPath).Load().Values);
            Assert.Equal("entry1", config.EntryId);
            Assert.True(config.IsEnabled);
            Assert.True(config.HDREnabled); // preserve previous HDR preference
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private sealed class StubSteamResolver : ISteamGameResolver
    {
        public string? UrlToReturn { get; set; }
        public string? AppIdToReturn { get; set; }
        public string? ExeToReturn { get; set; }
        public List<string> ShortcutPaths { get; } = new();
        public List<string> Urls { get; } = new();
        public List<string> AppIds { get; } = new();

        public string? TryParseInternetShortcutUrl(string urlFilePath)
        {
            ShortcutPaths.Add(urlFilePath);
            return UrlToReturn;
        }

        public string? TryParseRunGameId(string steamUrl)
        {
            Urls.Add(steamUrl);
            return AppIdToReturn;
        }

        public string? TryResolveExeFromAppId(string appId)
        {
            AppIds.Add(appId);
            return ExeToReturn;
        }

        public IReadOnlyList<string> TryEnumerateExeCandidates(string appId) => Array.Empty<string>();
    }
}
