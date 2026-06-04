using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using GameHelper.ConsoleHost.Models;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using GameHelper.Infrastructure.Providers;
using GameHelper.Infrastructure.Validators;
using Microsoft.Extensions.DependencyInjection;

namespace GameHelper.ConsoleHost.Services
{
    public static class FileDropHandler
    {
        // Detect if args look like a list of existing .lnk/.exe/.url file paths
        public static bool LooksLikeFilePaths(string[] args)
        {
            if (args is null || args.Length == 0)
            {
                return false;
            }

            foreach (var item in args)
            {
                if (string.IsNullOrWhiteSpace(item) || !File.Exists(item))
                {
                    return false;
                }

                var ext = Path.GetExtension(item);
                if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        // Add/update config entries for provided file paths (supports .lnk resolution and .url via ISteamGameResolver)
        public static AddSummary ProcessFilePaths(string[] paths, string? configOverride, IServiceProvider services)
        {
            var provider = !string.IsNullOrWhiteSpace(configOverride)
                ? new YamlConfigProvider(configOverride!)
                : new YamlConfigProvider();

            // Always rebuild the working map by EntryId to avoid key-shape drift from older callers/tests.
            var map = provider.Load().Values.ToDictionary(
                cfg => ConfigIdentity.EnsureEntryId(cfg.EntryId),
                cfg => cfg,
                StringComparer.OrdinalIgnoreCase);

            var steamResolver = services.GetService<ISteamGameResolver>();

            var added = 0;
            var updated = 0;
            var skipped = 0;

            foreach (var path in paths)
            {
                try
                {
                    var exePath = ResolveExecutablePath(path, steamResolver);
                    if (string.IsNullOrWhiteSpace(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"Skip: not an exe or cannot resolve target -> {path}");
                        skipped++;
                        continue;
                    }

                    var executableName = Path.GetFileName(exePath);
                    var normalizedExePath = ConfigEntryMatcher.NormalizePath(exePath);

                    var (productName, _) = GameMetadataExtractor.ExtractMetadata(exePath);
                    var baseDataKey = DataKeyGenerator.GenerateBaseDataKey(exePath, productName);
                    var displayName = ResolveDisplayName(path, exePath);

                    var existing = ConfigEntryMatcher.FindExistingForAdd(map.Values, executableName, exePath);
                    if (existing is not null)
                    {
                        var oldPath = existing.ExecutablePath;
                        existing.EntryId = ConfigIdentity.EnsureEntryId(existing.EntryId);
                        existing.DataKey = EnsureExistingDataKey(existing, map.Values, baseDataKey);
                        existing.ExecutablePath = exePath;
                        existing.ExecutableName = executableName;
                        existing.DisplayName = displayName;
                        existing.IsEnabled = true;

                        map[existing.EntryId] = existing;
                        updated++;

                        if (!string.IsNullOrWhiteSpace(oldPath) &&
                            !string.Equals(ConfigEntryMatcher.NormalizePath(oldPath), normalizedExePath, StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"Updated: {executableName}  DataKey={existing.DataKey}  Path={exePath} (changed from {oldPath})  DisplayName={existing.DisplayName}  Enabled={existing.IsEnabled}  HDR={existing.HDREnabled}");
                        }
                        else if (string.IsNullOrWhiteSpace(oldPath))
                        {
                            Console.WriteLine($"Updated: {executableName}  DataKey={existing.DataKey}  Path={exePath} (added)  DisplayName={existing.DisplayName}  Enabled={existing.IsEnabled}  HDR={existing.HDREnabled}");
                        }
                        else
                        {
                            Console.WriteLine($"Updated: {executableName}  DataKey={existing.DataKey}  Path={exePath}  DisplayName={existing.DisplayName}  Enabled={existing.IsEnabled}  HDR={existing.HDREnabled}");
                        }
                    }
                    else
                    {
                        var dataKey = ConfigIdentity.EnsureUniqueDataKey(baseDataKey, map.Values.Select(c => c.DataKey));
                        var entryId = Guid.NewGuid().ToString("N");

                        var config = new GameConfig
                        {
                            EntryId = entryId,
                            DataKey = dataKey,
                            ExecutablePath = exePath,
                            ExecutableName = executableName,
                            DisplayName = displayName,
                            IsEnabled = true,
                            HDREnabled = false
                        };

                        map[entryId] = config;
                        added++;
                        Console.WriteLine($"Added:   {executableName}  DataKey={dataKey}  Path={exePath}  DisplayName={displayName}  Enabled=true  HDR=false");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Skip: {path} -> {ex.Message}");
                    skipped++;
                }
            }

            // Detect duplicates before save (based on current file) so we can report how many were auto-removed by rewriting
            var duplicateBefore = 0;
            try
            {
                var validation = YamlConfigValidator.Validate(provider.ConfigPath);
                duplicateBefore = Math.Max(0, validation.DuplicateCount);
            }
            catch
            {
                // best effort only
            }

            provider.Save(map);
            Console.WriteLine($"Done. Added={added}, Updated={updated}, Skipped={skipped}");

            return new AddSummary
            {
                Added = added,
                Updated = updated,
                Skipped = skipped,
                DuplicatesRemoved = duplicateBefore,
                ConfigPath = provider.ConfigPath
            };
        }

        // Show an informational dialog so users launching via Explorer/shortcut can see the result
        public static void TryShowMessageBox(string text, string caption)
        {
            try
            {
                // MB_OK | MB_ICONINFORMATION
                MessageBoxW(IntPtr.Zero, text, caption, 0x00000000u | 0x00000040u);
            }
            catch
            {
                // best effort only
            }
        }

        private static string? ResolveExecutablePath(string path, ISteamGameResolver? steamResolver)
        {
            string? exe = null;
            var ext = Path.GetExtension(path);

            if (ext.Equals(".url", StringComparison.OrdinalIgnoreCase) && steamResolver != null)
            {
                try
                {
                    var url = steamResolver.TryParseInternetShortcutUrl(path);
                    var appId = url != null ? steamResolver.TryParseRunGameId(url) : null;
                    exe = appId != null ? steamResolver.TryResolveExeFromAppId(appId) : null;
                }
                catch
                {
                    // fall through to default resolver
                }
            }

            exe ??= ExecutableResolver.TryResolveFromInput(path);
            return exe;
        }

        private static string ResolveDisplayName(string sourcePath, string exePath)
        {
            var displayName = Path.GetFileNameWithoutExtension(exePath);
            try
            {
                var ext = Path.GetExtension(sourcePath);
                if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
                {
                    var candidate = Path.GetFileNameWithoutExtension(sourcePath);
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        displayName = candidate;
                    }
                }
            }
            catch
            {
                // fallback to exe-derived display name
            }

            return displayName;
        }

        private static string EnsureExistingDataKey(GameConfig existing, IEnumerable<GameConfig> allConfigs, string baseDataKey)
        {
            if (!string.IsNullOrWhiteSpace(existing.DataKey))
            {
                return existing.DataKey;
            }

            var keys = allConfigs
                .Where(c => !string.Equals(c.EntryId, existing.EntryId, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.DataKey);

            return ConfigIdentity.EnsureUniqueDataKey(baseDataKey, keys);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);
    }
}
