using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.ConsoleHost;
using GameHelper.Infrastructure.Controllers;
using GameHelper.Infrastructure.Processes;
using GameHelper.Infrastructure.Providers;
using GameHelper.Infrastructure.Validators;
using GameHelper.Infrastructure.Resolvers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

static void ConvertConfigCommand()
{
    try
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dir = Path.Combine(appData, "GameHelper");
        string jsonPath = Path.Combine(dir, "config.json");
        string ymlPath = Path.Combine(dir, "config.yml");

        if (!File.Exists(jsonPath))
        {
            Console.WriteLine($"No JSON config found at {jsonPath}");
            return;
        }

        // Load from JSON
        var jsonProvider = new JsonConfigProvider(jsonPath);
        var data = jsonProvider.Load();
        Console.WriteLine($"Loaded {data.Count} entries from JSON.");

        // Save to YAML (overwrites if exists)
        var yamlProvider = new YamlConfigProvider(ymlPath);
        yamlProvider.Save(data);
        Console.WriteLine($"Written YAML to {ymlPath}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to convert: {ex.Message}");
    }
}

static void RunValidateConfigCommand()
{
    try
    {
        var provider = new YamlConfigProvider();
        string path = provider.ConfigPath;
        var result = YamlConfigValidator.Validate(path);

        Console.WriteLine($"Validating: {path}");
        Console.WriteLine($"Games: {result.GameCount}, Duplicates: {result.DuplicateCount}");
        if (result.Warnings.Count > 0)
        {
            Console.WriteLine("Warnings:");
            foreach (var w in result.Warnings) Console.WriteLine("  - " + w);
        }
        if (result.Errors.Count > 0)
        {
            Console.WriteLine("Errors:");
            foreach (var e in result.Errors) Console.WriteLine("  - " + e);
        }

        Console.WriteLine(result.IsValid ? "Config is valid." : "Config has errors.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to validate: {ex.Message}");
    }
}

// Extract optional global flags from args and build an effective argument list for commands
// Supported: --config <path> | -c <path>, and --debug | -v | --verbose
string? configOverride = null;
bool enableDebug = false;
string[] effectiveArgs = args;
try
{
    if (args != null && args.Length > 0)
    {
        var list = new List<string>(args);
        int idx = list.FindIndex(s => string.Equals(s, "--config", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "-c", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            if (idx + 1 < list.Count)
            {
                configOverride = list[idx + 1];
                list.RemoveAt(idx + 1);
                list.RemoveAt(idx);
            }
            else
            {
                Console.WriteLine("Missing path after --config/-c. Ignoring.");
                list.RemoveAt(idx);
            }
        }
        // handle --debug/-v/--verbose (boolean flag)
        int didx = list.FindIndex(s =>
            string.Equals(s, "--debug", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s, "-v", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s, "--verbose", StringComparison.OrdinalIgnoreCase));
        if (didx >= 0)
        {
            enableDebug = true;
            list.RemoveAt(didx);
        }
        effectiveArgs = list.ToArray();
    }
}
catch { }

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        logging.SetMinimumLevel(enableDebug ? LogLevel.Debug : LogLevel.Information);
    })
    .ConfigureServices(services =>
    {
        // Register core abstractions with infrastructure implementations
        // Config provider first, as the process monitor will read enabled game names to build a whitelist
        services.AddSingleton<IConfigProvider>(sp =>
        {
            if (!string.IsNullOrWhiteSpace(configOverride)) return new YamlConfigProvider(configOverride!);
            return new YamlConfigProvider();
        });
        services.AddSingleton<IProcessMonitor>(sp =>
        {
            var cfg = sp.GetRequiredService<IConfigProvider>();
            var map = cfg.Load();
            // Use enabled entries as whitelist (keys are normalized executable names like "game.exe")
            var allowed = map is null
                ? Array.Empty<string>()
                : System.Linq.Enumerable.Select(
                    System.Linq.Enumerable.Where(map, kv => kv.Value?.IsEnabled == true),
                    kv => kv.Key);
            return new WmiProcessMonitor(allowed);
        });
        services.AddSingleton<IHdrController, NoOpHdrController>();
        services.AddSingleton<IPlayTimeService, FileBackedPlayTimeService>();
        services.AddSingleton<IGameAutomationService, GameAutomationService>();
        // Steam URL resolver for optional .url drag&drop support
        services.AddSingleton<ISteamGameResolver, SteamGameResolver>();
        services.AddHostedService<Worker>();
    })
    .Build();

// Print effective config file path if available
try
{
    var cfgProvider = host.Services.GetService<IConfigProvider>();
    if (cfgProvider is IConfigPathProvider pathProvider)
    {
        Console.WriteLine($"Using config: {pathProvider.ConfigPath}");
    }
    // Print build info (last write time of entry assembly or process exe) and current log level
    try { PrintBuildInfo(enableDebug); } catch { }

    // Plan B: If launched with file arguments (drag & drop .lnk/.exe onto EXE/shortcut), auto-add to config and exit
    try
    {
        if (LooksLikeFilePaths(effectiveArgs))
        {
            var summary = RunAddFilesFromArgs(effectiveArgs, configOverride, host.Services);
            var text = $"已完成添加/更新\nAdded={summary.Added}, Updated={summary.Updated}, Skipped={summary.Skipped}\n重复清理: {summary.DuplicatesRemoved}\n配置: {summary.ConfigPath}";
            TryShowMessageBox(text, "GameHelper");
            Environment.Exit(0);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Auto-add failed: {ex.Message}");
        Environment.Exit(1);
    }
}
catch
{
    // ignore printing errors
}

var command = effectiveArgs.Length > 0 ? effectiveArgs[0].ToLowerInvariant() : "monitor";
switch (command)
{
    case "monitor":
        await host.RunAsync();
        break;

    case "config":
        RunConfigCommand(host.Services, effectiveArgs.Skip(1).ToArray());
        break;

    case "stats":
        RunStatsCommand(effectiveArgs.Skip(1).ToArray());
        break;

    case "convert-config":
        ConvertConfigCommand();
        break;

    case "validate-config":
        RunValidateConfigCommand();
        break;

    default:
        PrintUsage();
        break;
}

static void PrintUsage()
{
    Console.WriteLine("GameHelper Console");
    Console.WriteLine("Usage:");
    Console.WriteLine("  monitor [--config <path>] [--debug]");
    Console.WriteLine("  config list [--config <path>] [--debug]");
    Console.WriteLine("  config add <exe> [--config <path>] [--debug]");
    Console.WriteLine("  config remove <exe> [--config <path>] [--debug]");
    Console.WriteLine("  stats [--game <name>] [--config <path>] [--debug]");
    Console.WriteLine("  convert-config");
    Console.WriteLine("  validate-config");
    Console.WriteLine();
    Console.WriteLine("Global options:");
    Console.WriteLine("  --config, -c    Override path to config.yml");
    Console.WriteLine("  --debug, -v     Enable verbose debug logging");
}

static void PrintBuildInfo(bool debug)
{
    try
    {
        string? path = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            try { path = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName; } catch { }
        }
        DateTime? ts = null;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            ts = File.GetLastWriteTime(path);
        }
        if (ts.HasValue)
        {
            Console.WriteLine($"Build time: {ts.Value:yyyy-MM-dd HH:mm:ss}");
        }
        Console.WriteLine($"Log level: {(debug ? "Debug" : "Information")}");
    }
    catch { }
}

// Detect if args look like a list of existing .lnk/.exe/.url file paths
static bool LooksLikeFilePaths(string[] args)
{
    if (args is null || args.Length == 0) return false;
    foreach (var a in args)
    {
        if (string.IsNullOrWhiteSpace(a)) return false;
        if (!File.Exists(a)) return false;
        var ext = Path.GetExtension(a);
        if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
            return false;
    }
    return true;
}

// Add/update config entries for provided file paths (supports .lnk resolution and .url via ISteamGameResolver)
static AddSummary RunAddFilesFromArgs(string[] paths, string? configOverride, IServiceProvider services)
{
    var provider = !string.IsNullOrWhiteSpace(configOverride) ? new YamlConfigProvider(configOverride!) : new YamlConfigProvider();
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
            exe ??= TryResolveExecutableFromInput(p);
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
                if (dragExt.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || dragExt.Equals(".url", StringComparison.OrdinalIgnoreCase))
                {
                    var candidate = Path.GetFileNameWithoutExtension(p);
                    if (!string.IsNullOrWhiteSpace(candidate)) alias = candidate;
                }
            }
            catch { }

            if (map.TryGetValue(key, out var existing))
            {
                // Overwrite alias with latest dragged name per user requirement; ensure enabled flags are true by default
                existing.Alias = alias;
                existing.IsEnabled = true;
                if (!existing.HDREnabled) existing.HDREnabled = true;
                updated++;
                Console.WriteLine($"Updated: {key}  Alias={existing.Alias}  Enabled={existing.IsEnabled}  HDR={existing.HDREnabled}");
            }
            else
            {
                map[key] = new GameConfig { Name = key, Alias = alias, IsEnabled = true, HDREnabled = true };
                added++;
                Console.WriteLine($"Added:   {key}  Alias={alias}  Enabled=true  HDR=true");
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
    return new AddSummary { Added = added, Updated = updated, Skipped = skipped, DuplicatesRemoved = dupBefore, ConfigPath = provider.ConfigPath };
}

// Resolve input file to an executable path, supporting .lnk shortcuts
static string? TryResolveExecutableFromInput(string input)
{
    if (string.IsNullOrWhiteSpace(input)) return null;
    var ext = Path.GetExtension(input);
    if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)) return input;
    if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
    {
        var target = ResolveShortcutTargetViaWsh(input);
        return target;
    }
    return null;
}

// Use WScript.Shell COM to resolve .lnk target without compile-time COM references
static string? ResolveShortcutTargetViaWsh(string lnkPath)
{
    try
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return null;
        var shell = Activator.CreateInstance(shellType);
        var shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
        var target = shortcut?.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;
        return target;
    }
    catch { return null; }
}

// Show an informational dialog so users launching via Explorer/shortcut can see the result
static void TryShowMessageBox(string text, string caption)
{
    try
    {
        // MB_OK | MB_ICONINFORMATION
        MessageBoxW(IntPtr.Zero, text, caption, 0x00000000u | 0x00000040u);
    }
    catch { }
}

[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

static void RunConfigCommand(IServiceProvider services, string[] args)
{
    if (args.Length == 0) { PrintUsage(); return; }
    using var scope = services.CreateScope();
    var provider = scope.ServiceProvider.GetRequiredService<IConfigProvider>();
    var sub = args[0].ToLowerInvariant();
    var map = new Dictionary<string, GameConfig>(provider.Load(), StringComparer.OrdinalIgnoreCase);

    switch (sub)
    {
        case "list":
            if (map.Count == 0)
            {
                Console.WriteLine("No games configured.");
                return;
            }
            foreach (var kv in map.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"{kv.Key}  Enabled={kv.Value.IsEnabled}  HDR={kv.Value.HDREnabled}");
            }
            return;

        case "add":
            if (args.Length < 2) { Console.WriteLine("Missing <exe>."); return; }
            var name = args[1];
            map[name] = new GameConfig { Name = name, IsEnabled = true, HDREnabled = true };
            provider.Save(map);
            Console.WriteLine($"Added {name}.");
            return;

        case "remove":
            if (args.Length < 2) { Console.WriteLine("Missing <exe>."); return; }
            var remove = args[1];
            if (map.Remove(remove))
            {
                provider.Save(map);
                Console.WriteLine($"Removed {remove}.");
            }
            else
            {
                Console.WriteLine($"Not found: {remove}");
            }
            return;
        default:
            PrintUsage();
            return;
    }
}

static void RunStatsCommand(string[] args)
{
    string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    string dir = Path.Combine(appData, "GameHelper");
    string file = Path.Combine(dir, "playtime.json");
    if (!File.Exists(file))
    {
        Console.WriteLine("No playtime data yet.");
        return;
    }

    try
    {
        var json = File.ReadAllText(file);
        var root = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
        if (root == null || !root.TryGetValue("games", out var node) || node == null)
        {
            Console.WriteLine("No playtime data.");
            return;
        }
        var items = JsonSerializer.Deserialize<List<GameItem>>(node.ToString() ?? string.Empty) ?? new();
        string? filterGame = null;
        if (args.Length >= 2 && args[0] == "--game") filterGame = args[1];

        var list = string.IsNullOrWhiteSpace(filterGame)
            ? items
            : items.Where(i => string.Equals(i.GameName, filterGame, StringComparison.OrdinalIgnoreCase)).ToList();

        if (list.Count == 0)
        {
            Console.WriteLine("No matching playtime.");
            return;
        }

        // load config to resolve Alias for display (YAML only)
        var cfgProvider2 = new YamlConfigProvider();
        var cfg = new Dictionary<string, GameConfig>(cfgProvider2.Load(), StringComparer.OrdinalIgnoreCase);

        long total = 0;
        foreach (var g in list.OrderBy(g => g.GameName, StringComparer.OrdinalIgnoreCase))
        {
            var minutes = g.Sessions?.Sum(s => s.DurationMinutes) ?? 0;
            total += minutes;
            var display = cfg.TryGetValue(g.GameName, out var gc) && !string.IsNullOrWhiteSpace(gc.Alias)
                ? gc.Alias!
                : g.GameName;
            var formatted = FormatDuration(minutes);
            Console.WriteLine($"{display}: {formatted}, sessions={g.Sessions?.Count ?? 0}");
        }
        if (string.IsNullOrWhiteSpace(filterGame))
        {
            Console.WriteLine($"TOTAL: {FormatDuration(total)}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to read stats: {ex.Message}");
    }
}

// Format minutes as "N min" when < 60, otherwise as hours (e.g., "2 h" or "2.5 h")
static string FormatDuration(long minutes)
{
    if (minutes < 60) return $"{minutes} min";
    if (minutes % 60 == 0) return $"{minutes / 60} h";
    return $"{(minutes / 60.0):0.0} h";
}

// DTOs for JSON mapping used by stats command
internal sealed class GameItem
{
    public string GameName { get; set; } = string.Empty;
    public List<GameSession> Sessions { get; set; } = new();
}

internal sealed class GameSession
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public long DurationMinutes { get; set; }
}

internal sealed class AddSummary
{
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int DuplicatesRemoved { get; set; }
    public string ConfigPath { get; set; } = string.Empty;
}
