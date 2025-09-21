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

            Assert.True(map.TryGetValue("NewAdventure.exe", out var config));
            Assert.Equal("NewAdventure", config.Alias);
            Assert.True(config.IsEnabled);
            Assert.True(config.HDREnabled);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ProcessFilePaths_UpdatesExistingGameAndReEnables()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.yml");
        var exePath = Path.Combine(tempDir, "LegacyClassic.exe");
        File.WriteAllText(exePath, string.Empty);

        var seed = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["LegacyClassic.exe"] = new GameConfig
            {
                Name = "LegacyClassic.exe",
                Alias = "旧版经典",
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

            Assert.True(map.TryGetValue("LegacyClassic.exe", out var config));
            Assert.Equal("LegacyClassic", config.Alias);
            Assert.True(config.IsEnabled);
            Assert.True(config.HDREnabled);
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

            Assert.True(map.TryGetValue("SteamAdventure.exe", out var config));
            Assert.Equal("SteamShortcut", config.Alias);
            Assert.True(config.IsEnabled);
            Assert.True(config.HDREnabled);
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
