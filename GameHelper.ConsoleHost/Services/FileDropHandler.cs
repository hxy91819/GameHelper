using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using GameHelper.ConsoleHost.Models;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
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
            var gameCatalogService = new GameCatalogService(provider);

            var steamResolver = services.GetService<ISteamGameResolver>();

            var added = 0;
            var updated = 0;
            var skipped = 0;
            var duplicateBefore = CountExistingDuplicates(provider);

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

                    var result = gameCatalogService.Import(new GameEntryImportRequest
                    {
                        ExecutableName = executableName,
                        ExecutablePath = exePath,
                        DisplayName = displayName,
                        BaseDataKey = baseDataKey,
                        IsEnabled = true
                    });
                    var entry = result.Entry;

                    if (result.WasAdded)
                    {
                        added++;
                        Console.WriteLine($"Added:   {entry.ExecutableName}  DataKey={entry.DataKey}  Path={entry.ExecutablePath}  DisplayName={entry.DisplayName}  Enabled={entry.IsEnabled}  HDR={entry.HdrEnabled}");
                    }
                    else
                    {
                        updated++;
                        var oldPath = result.PreviousExecutablePath;
                        if (!string.IsNullOrWhiteSpace(oldPath) &&
                            !string.Equals(ConfigEntryMatcher.NormalizePath(oldPath), normalizedExePath, StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"Updated: {entry.ExecutableName}  DataKey={entry.DataKey}  Path={entry.ExecutablePath} (changed from {oldPath})  DisplayName={entry.DisplayName}  Enabled={entry.IsEnabled}  HDR={entry.HdrEnabled}");
                        }
                        else if (string.IsNullOrWhiteSpace(oldPath))
                        {
                            Console.WriteLine($"Updated: {entry.ExecutableName}  DataKey={entry.DataKey}  Path={entry.ExecutablePath} (added)  DisplayName={entry.DisplayName}  Enabled={entry.IsEnabled}  HDR={entry.HdrEnabled}");
                        }
                        else
                        {
                            Console.WriteLine($"Updated: {entry.ExecutableName}  DataKey={entry.DataKey}  Path={entry.ExecutablePath}  DisplayName={entry.DisplayName}  Enabled={entry.IsEnabled}  HDR={entry.HdrEnabled}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Skip: {path} -> {ex.Message}");
                    skipped++;
                }
            }

            gameCatalogService.RepairStorage();
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

        private static int CountExistingDuplicates(YamlConfigProvider provider)
        {
            try
            {
                var validation = YamlConfigValidator.Validate(provider.ConfigPath);
                return Math.Max(0, validation.DuplicateCount);
            }
            catch
            {
                return 0;
            }

        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);
    }
}
