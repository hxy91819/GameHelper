using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GameHelper.Core.Abstractions;
using Microsoft.Win32;

namespace GameHelper.Infrastructure.Resolvers
{
    /// <summary>
    /// Resolve Steam URLs (steam://rungameid/{appid}) to local executable paths by reading Steam registry and app manifests.
    /// </summary>
    public sealed class SteamGameResolver : ISteamGameResolver
    {
        private static readonly Regex RunGameIdRegex = new Regex(
            @"^steam://rungameid/(?<id>\d+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public string? TryParseInternetShortcutUrl(string urlFilePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(urlFilePath) || !File.Exists(urlFilePath)) return null;
                // .url is an INI-like file. We just scan for first non-empty line starting with URL=
                foreach (var line in File.ReadAllLines(urlFilePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                    {
                        return trimmed.Substring(4).Trim();
                    }
                }
            }
            catch { }
            return null;
        }

        public string? TryParseRunGameId(string steamUrl)
        {
            if (string.IsNullOrWhiteSpace(steamUrl)) return null;
            var m = RunGameIdRegex.Match(steamUrl.Trim());
            if (!m.Success) return null;
            return m.Groups["id"].Value;
        }

        public string? TryResolveExeFromAppId(string appId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(appId)) return null;
                var steamRoot = GetSteamRootFromRegistry();
                if (steamRoot == null) return null;

                foreach (var lib in EnumerateSteamLibraries(steamRoot))
                {
                    var manifest = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
                    if (!File.Exists(manifest)) continue;

                    if (!TryReadAppManifest(manifest, out var installdir, out var appName)) continue;

                    var installDirPath = Path.Combine(lib, "steamapps", "common", installdir);
                    if (!Directory.Exists(installDirPath)) continue;

                    var best = PickBestExeCandidate(new DirectoryInfo(installDirPath), installdir, appName);
                    if (!string.IsNullOrWhiteSpace(best) && File.Exists(best))
                    {
                        return best;
                    }
                }
            }
            catch { }
            return null;
        }

        public IReadOnlyList<string> TryEnumerateExeCandidates(string appId)
        {
            var result = new List<string>();
            try
            {
                var steamRoot = GetSteamRootFromRegistry();
                if (steamRoot == null) return result;
                foreach (var lib in EnumerateSteamLibraries(steamRoot))
                {
                    var manifest = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
                    if (!File.Exists(manifest)) continue;
                    if (!TryReadAppManifest(manifest, out var installdir, out var appName)) continue;
                    var installDirPath = Path.Combine(lib, "steamapps", "common", installdir);
                    if (!Directory.Exists(installDirPath)) continue;
                    result.AddRange(EnumerateExeCandidates(new DirectoryInfo(installDirPath)));
                }
            }
            catch { }
            return result;
        }

        private static string? GetSteamRootFromRegistry()
        {
            try
            {
                // Allow test/override via environment variable first
                var env = Environment.GetEnvironmentVariable("GAMEHELPER_STEAM_ROOT");
                if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
                {
                    return NormalizePath(env);
                }

                using var key = Registry.CurrentUser.OpenSubKey(@"Software\\Valve\\Steam");
                var path = key?.GetValue("SteamPath") as string;
                if (!string.IsNullOrWhiteSpace(path)) return NormalizePath(path);
                var exe = key?.GetValue("SteamExe") as string;
                if (!string.IsNullOrWhiteSpace(exe)) return NormalizePath(Path.GetDirectoryName(exe)!);
            }
            catch { }
            return null;
        }

        private static IEnumerable<string> EnumerateSteamLibraries(string steamRoot)
        {
            // Always include main steam root as a library
            yield return steamRoot;

            var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) yield break;

            // The file is a Valve KeyValue text; we do a simple heuristic parse to find all paths
            foreach (var path in ParseLibraryPathsFromVdf(vdf))
            {
                var p = NormalizePath(path);
                if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                {
                    yield return p;
                }
            }
        }

        private static IEnumerable<string> ParseLibraryPathsFromVdf(string vdfPath)
        {
            // We try both new and old formats: lines like "\"path\"\t\"D:\\SteamLibrary\"" or indexed blocks "\"1\"\n{\n \"path\" \"D:\\...\" }"
            var content = SafeReadAllText(vdfPath);
            if (content == null) yield break;

            // 1) Find path entries
            var rxPathKV = new Regex(@"""path""\s*""(?<p>[^""]+)""", RegexOptions.IgnoreCase);
            foreach (Match m in rxPathKV.Matches(content))
            {
                var p = m.Groups["p"].Value;
                if (!string.IsNullOrWhiteSpace(p)) yield return p;
            }
        }

        private static bool TryReadAppManifest(string manifestPath, out string installdir, out string? name)
        {
            installdir = string.Empty;
            name = null;
            try
            {
                var text = SafeReadAllText(manifestPath);
                if (text == null) return false;
                // Very simple extraction from ACF text
                var rxInstallDir = new Regex(@"""installdir""\s*""(?<v>[^""]+)""", RegexOptions.IgnoreCase);
                var rxName = new Regex(@"""name""\s*""(?<v>[^""]+)""", RegexOptions.IgnoreCase);
                var m1 = rxInstallDir.Match(text);
                if (m1.Success) installdir = m1.Groups["v"].Value;
                var m2 = rxName.Match(text);
                if (m2.Success) name = m2.Groups["v"].Value;
                return !string.IsNullOrWhiteSpace(installdir);
            }
            catch { return false; }
        }

        private static string? PickBestExeCandidate(DirectoryInfo installDir, string? installDirName, string? appName)
        {
            var candidates = EnumerateExeCandidates(installDir).ToList();
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0];

            string scoreBase = (installDirName ?? string.Empty).ToLowerInvariant();
            string scoreName = (appName ?? string.Empty).ToLowerInvariant();

            string? best = null;
            int bestScore = int.MinValue;
            long bestSize = -1;

            foreach (var path in candidates)
            {
                var file = Path.GetFileName(path).ToLowerInvariant();
                int score = 0;
                if (!string.IsNullOrEmpty(scoreBase) && file.Contains(scoreBase)) score += 5;
                if (!string.IsNullOrEmpty(scoreName) && file.Contains(scoreName)) score += 3;
                if (!file.Contains("setup") && !file.Contains("unins") && !file.Contains("vcredist") && !file.Contains("dxsetup")) score += 2;
                try
                {
                    var len = new FileInfo(path).Length;
                    // Prefer larger exe as a heuristic for main binary
                    if (len > bestSize && score >= bestScore)
                    {
                        best = path;
                        bestScore = score;
                        bestSize = len;
                    }
                }
                catch
                {
                    if (score > bestScore)
                    {
                        best = path;
                        bestScore = score;
                        bestSize = -1;
                    }
                }
            }
            return best ?? candidates[0];
        }

        private static IEnumerable<string> EnumerateExeCandidates(DirectoryInfo root)
        {
            // search depth 2 to avoid falling into deep engine/tools folders
            var queue = new Queue<(DirectoryInfo dir, int depth)>();
            queue.Enqueue((root, 0));
            while (queue.Count > 0)
            {
                var (dir, depth) = queue.Dequeue();
                FileInfo[] files;
                try { files = dir.GetFiles("*.exe", SearchOption.TopDirectoryOnly); }
                catch { files = Array.Empty<FileInfo>(); }

                foreach (var f in files)
                {
                    var name = f.Name.ToLowerInvariant();
                    if (name.Contains("vcredist") || name.Contains("dxsetup") || name.StartsWith("unins") || name.Contains("setup"))
                        continue;
                    yield return f.FullName;
                }

                if (depth < 1) // allow one subdirectory level
                {
                    DirectoryInfo[] subs;
                    try { subs = dir.GetDirectories(); } catch { subs = Array.Empty<DirectoryInfo>(); }
                    foreach (var s in subs)
                    {
                        // skip typical redist/tools folders
                        var n = s.Name.ToLowerInvariant();
                        if (n.Contains("redist") || n.Contains("vc") || n.Contains("_commonredist") || n.Contains("tools")) continue;
                        queue.Enqueue((s, depth + 1));
                    }
                }
            }
        }

        private static string? SafeReadAllText(string path)
        {
            try { return File.ReadAllText(path, Encoding.UTF8); } catch { }
            try { return File.ReadAllText(path, Encoding.Default); } catch { }
            return null;
        }

        private static string NormalizePath(string p)
        {
            return p.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
        }
    }
}
