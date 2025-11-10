using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using GameHelper.ConsoleHost.Models;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
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
            if (args is null || args.Length == 0) return false;
            foreach (var a in args)
            {
                if (string.IsNullOrWhiteSpace(a)) return false;
                if (!File.Exists(a)) return false;
                var ext = Path.GetExtension(a);
                if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) && 
                    !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) && 
                    !ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        // Add/update config entries for provided file paths (supports .lnk resolution and .url via ISteamGameResolver)
        public static AddSummary ProcessFilePaths(string[] paths, string? configOverride, IServiceProvider services)
        {
            var provider = !string.IsNullOrWhiteSpace(configOverride) 
                ? new YamlConfigProvider(configOverride!) 
                : new YamlConfigProvider();
            var map = new Dictionary<string, GameConfig>(provider.Load(), StringComparer.OrdinalIgnoreCase);
            var steamResolver = services.GetService<ISteamGameResolver>();

            int added = 0, updated = 0, skipped = 0;
            foreach (var p in paths)
            {
                try
                {
                    string? exe = null;
                    var ext = Path.GetExtension(p);
                    if (ext.Equals(".url", StringComparison.OrdinalIgnoreCase) && steamResolver != null)
                    {
                        try
                        {
                            var url = steamResolver.TryParseInternetShortcutUrl(p);
                            var appId = url != null ? steamResolver.TryParseRunGameId(url) : null;
                            exe = appId != null ? steamResolver.TryResolveExeFromAppId(appId) : null;
                        }
                        catch { /* fall back below */ }
                    }
                    exe ??= ExecutableResolver.TryResolveFromInput(p);
                    if (string.IsNullOrWhiteSpace(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"Skip: not an exe or cannot resolve target -> {p}");
                        skipped++;
                        continue;
                    }
                    var key = Path.GetFileName(exe);
                    // Prefer the dragged item's display name for alias when available (.lnk/.url), otherwise fallback to exe filename
                    string alias = Path.GetFileNameWithoutExtension(exe);
                    try
                    {
                        var dragExt = Path.GetExtension(p);
                        if (dragExt.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || 
                            dragExt.Equals(".url", StringComparison.OrdinalIgnoreCase))
                        {
                            var candidate = Path.GetFileNameWithoutExtension(p);
                            if (!string.IsNullOrWhiteSpace(candidate)) alias = candidate;
                        }
                    }
                    catch { }

                    if (map.TryGetValue(key, out var existing))
                    {
                        // Overwrite alias with latest dragged name per user requirement; ensure automation is enabled by default
                        existing.DataKey = existing.DataKey ?? key;
                        existing.ExecutablePath = exe; // Update with full path for L1 matching
                        existing.ExecutableName = key;
                        existing.DisplayName = alias;
                        existing.IsEnabled = true;
                        updated++;
                        Console.WriteLine($"Updated: {key}  Path={exe}  DisplayName={existing.DisplayName}  Enabled={existing.IsEnabled}  HDR={existing.HDREnabled}");
                    }
                    else
                    {
                        map[key] = new GameConfig
                        {
                            DataKey = key,
                            ExecutablePath = exe, // Store full path for L1 matching
                            ExecutableName = key,
                            DisplayName = alias,
                            IsEnabled = true,
                            HDREnabled = false
                        };
                        added++;
                        Console.WriteLine($"Added:   {key}  Path={exe}  DisplayName={alias}  Enabled=true  HDR=false");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Skip: {p} -> {ex.Message}");
                    skipped++;
                }
            }

            // Detect duplicates before save (based on current file) so we can report how many were auto-removed by rewriting
            int dupBefore = 0;
            try
            {
                var vr = YamlConfigValidator.Validate(provider.ConfigPath);
                dupBefore = Math.Max(0, vr.DuplicateCount);
            }
            catch { }

            provider.Save(map);
            Console.WriteLine($"Done. Added={added}, Updated={updated}, Skipped={skipped}");
            return new AddSummary 
            { 
                Added = added, 
                Updated = updated, 
                Skipped = skipped, 
                DuplicatesRemoved = dupBefore, 
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
            catch { }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);
    }
}