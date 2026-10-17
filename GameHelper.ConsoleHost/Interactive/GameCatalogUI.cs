using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Spectre.Console;
using ConsoleValidationResult = Spectre.Console.ValidationResult;

namespace GameHelper.ConsoleHost.Interactive
{
    /// <summary>
    /// Handles game catalog management interactions (view, add, edit, remove).
    /// </summary>
    public sealed class GameCatalogUI
    {
        private enum ConfigAction
        {
            View,
            Add,
            ImportSteam,
            Edit,
            Remove,
            Back
        }

        private readonly IAnsiConsole _console;
        private readonly PromptUI _promptUI;
        private readonly IGameConfiguration _gameConfiguration;
        private readonly IAutoStartManager _autoStartManager;
        private readonly IGameCatalogService _gameCatalogService;
        private readonly ISteamGameResolver? _steamGameResolver;

        public GameCatalogUI(
            IAnsiConsole console,
            PromptUI promptUI,
            IGameConfiguration gameConfiguration,
            IAutoStartManager autoStartManager,
            IGameCatalogService gameCatalogService,
            ISteamGameResolver? steamGameResolver = null)
        {
            _console = console ?? throw new ArgumentNullException(nameof(console));
            _promptUI = promptUI ?? throw new ArgumentNullException(nameof(promptUI));
            _gameConfiguration = gameConfiguration ?? throw new ArgumentNullException(nameof(gameConfiguration));
            _autoStartManager = autoStartManager ?? throw new ArgumentNullException(nameof(autoStartManager));
            _gameCatalogService = gameCatalogService ?? throw new ArgumentNullException(nameof(gameCatalogService));
            _steamGameResolver = steamGameResolver;
        }

        public async Task HandleConfigurationAsync()
        {
            while (true)
            {
                var title = "[bold green]Config management[/]";
                var choices = Enum.GetValues<ConfigAction>();
                var prompt = new SelectionPrompt<ConfigAction>
                {
                    PageSize = 5
                };
                prompt.Title(title);
                prompt.AddChoices(choices);

                var selection = _promptUI.PromptSelection(
                    prompt,
                    choices,
                    action => action switch
                    {
                        ConfigAction.View => "📋  View current config",
                        ConfigAction.Add => "➕  Add new game",
                        ConfigAction.ImportSteam => "🎮  Import Steam games",
                        ConfigAction.Edit => "✏️  Edit existing game",
                        ConfigAction.Remove => "🗑  Remove game",
                        ConfigAction.Back => "⬅️  Back",
                        _ => action.ToString()
                    },
                    title);
                switch (selection)
                {
                    case ConfigAction.View:
                        RenderConfigTable();
                        break;
                    case ConfigAction.Add:
                        await AddGameAsync().ConfigureAwait(false);
                        break;
                    case ConfigAction.ImportSteam:
                        await ImportSteamGamesAsync().ConfigureAwait(false);
                        break;
                    case ConfigAction.Edit:
                        await EditGameAsync().ConfigureAwait(false);
                        break;
                    case ConfigAction.Remove:
                        await RemoveGameAsync().ConfigureAwait(false);
                        break;
                    case ConfigAction.Back:
                        return;
                }
            }
        }

        private void RenderConfigTable()
        {
            var configs = _gameCatalogService.List()
                .ToList();
            AppConfig? appConfig = null;
            try
            {
                appConfig = _gameConfiguration.Read();
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[red]Failed to load global config: {Markup.Escape(ex.Message)}[/]");
            }
            var configRule = new Rule("[yellow]Current config[/]")
            {
                Style = new Style(Color.Grey),
                Justification = Justify.Left
            };
            _console.Write(configRule);

            if (configs.Count == 0)
            {
                _console.MarkupLine("[italic grey]No games configured yet. Add one now![/]");
                return;
            }

            var table = new Table { Border = TableBorder.Rounded };
            table.AddColumn("DataKey");
            table.AddColumn("Executable");
            table.AddColumn("Display name");
            table.AddColumn("Path");
            table.AddColumn("Automation");
            table.AddColumn("HDR");

            foreach (var cfg in configs.OrderBy(e => e.DataKey, StringComparer.OrdinalIgnoreCase))
            {
                var pathDisplay = "-";
                if (!string.IsNullOrWhiteSpace(cfg.ExecutablePath))
                {
                    pathDisplay = cfg.ExecutablePath.Length > 30
                        ? "..." + cfg.ExecutablePath.Substring(cfg.ExecutablePath.Length - 27)
                        : cfg.ExecutablePath;
                }

                table.AddRow(
                    Markup.Escape(cfg.DataKey),
                    Markup.Escape(cfg.ExecutableName ?? "-"),
                    string.IsNullOrWhiteSpace(cfg.DisplayName) ? "-" : Markup.Escape(cfg.DisplayName!),
                    Markup.Escape(pathDisplay),
                    cfg.IsEnabled ? "[green]Enabled[/]" : "[red]Disabled[/]",
                    cfg.HdrEnabled ? "[green]Auto enable[/]" : "[yellow]Do not auto enable[/]");
            }

            _console.Write(table);

            if (appConfig != null)
            {
                var autoStartState = appConfig.AutoStartInteractiveMonitor
                    ? "[green]Enter live monitor automatically on startup[/]"
                    : "[yellow]Manually select monitor after startup[/]";
                _console.WriteLine();
                _console.MarkupLine($"Auto monitor: {autoStartState}");

                var launchSegments = new List<string>
                {
                    appConfig.LaunchOnSystemStartup
                        ? "[green]Auto-start GameHelper at login[/]"
                        : "[yellow]Do not auto-start at login[/]"
                };

                if (!_autoStartManager.IsSupported)
                {
                    launchSegments.Add("(not supported in this environment)");
                }
                else
                {
                    var systemState = TryReadSystemAutoStartState();
                    if (systemState.HasValue)
                    {
                        var systemSegment = systemState.Value
                            ? "[green]system state: enabled[/]"
                            : "[yellow]system state: not enabled[/]";

                        if (systemState.Value != appConfig.LaunchOnSystemStartup)
                        {
                            systemSegment += "[red](mismatch with config)[/]";
                        }

                        launchSegments.Add($"({systemSegment})");
                    }
                }

                var launchState = string.Concat(launchSegments);
                _console.MarkupLine($"Auto-start at login: {launchState}");
            }
        }

        private bool? TryReadSystemAutoStartState()
        {
            try
            {
                return _autoStartManager.IsEnabled();
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[yellow]Failed to read system auto-start state: {Markup.Escape(ex.Message)}[/]");
                return null;
            }
        }

        private Task AddGameAsync()
        {
            // Prompt for input - can be file path or executable name
            var inputPrompt = new TextPrompt<string>(
                "Enter the game's executable name, or drag & drop an EXE/LNK file\n" +
                "(e.g. [green]game.exe[/] or full path [green]C:\\Games\\game.exe[/])")
                .Validate(input => string.IsNullOrWhiteSpace(input)
                    ? ConsoleValidationResult.Error("Input cannot be empty.")
                    : ConsoleValidationResult.Success());

            var input = _promptUI.Prompt(inputPrompt);
            input = input.Trim().Trim('"'); // Remove quotes if dragged

            string? exePath = null;
            string executableName;
            string? productName = null;

            // Check if input is a file path
            if (File.Exists(input))
            {
                var ext = Path.GetExtension(input).ToLowerInvariant();

                if (ext == ".lnk")
                {
                    // Resolve shortcut
                    exePath = ExecutableResolver.TryResolveFromInput(input);
                    if (string.IsNullOrWhiteSpace(exePath))
                    {
                        _console.MarkupLine("[red]Failed to resolve shortcut target.[/]");
                        return Task.CompletedTask;
                    }
                    _console.MarkupLine($"[grey]Resolved shortcut target: {Markup.Escape(exePath)}[/]");
                }
                else if (ext == ".exe")
                {
                    exePath = input;
                }
                else
                {
                    _console.MarkupLine("[yellow]Unsupported file type. Provide a .exe or .lnk file.[/]");
                    return Task.CompletedTask;
                }

                // Extract metadata
                (productName, _) = GameMetadataExtractor.ExtractMetadata(exePath);
                executableName = Path.GetFileName(exePath);

                // Display extracted information
                _console.MarkupLine("[green]Game file detected:[/]");
                _console.MarkupLine($"  Path: {Markup.Escape(exePath)}");
                _console.MarkupLine($"  Executable: {Markup.Escape(executableName)}");
                if (!string.IsNullOrWhiteSpace(productName))
                {
                    _console.MarkupLine($"  Product name: {Markup.Escape(productName)}");
                }
                _console.WriteLine();
            }
            else
            {
                // Treat as executable name only
                executableName = input;
            }

            var executableIdentity = ExecutableIdentity.Parse(exePath ?? executableName);
            var preview = _gameCatalogService.PreviewIntake(new GameCatalogIntakeRequest
            {
                Executable = executableIdentity,
                ProductName = productName
            });
            var existingConfig = preview.ExistingEntry;
            var suggestedDataKey = preview.SuggestedDataKey;

            // Prompt for DataKey
            var dataKeyPrompt = new TextPrompt<string>(
                $"Enter a DataKey (unique identifier used to link data)\n" +
                $"Suggested value: [green]{Markup.Escape(suggestedDataKey)}[/]")
                .AllowEmpty()
                .DefaultValue(suggestedDataKey)
                .Validate(key =>
                {
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        return ConsoleValidationResult.Error("DataKey cannot be empty.");
                    }
                    var availability = _gameCatalogService.PreviewIntake(new GameCatalogIntakeRequest
                    {
                        Executable = executableIdentity,
                        DataKey = key,
                        ProductName = productName
                    });
                    if (!availability.IsRequestedDataKeyAvailable)
                    {
                        return ConsoleValidationResult.Error($"DataKey '{key}' is already used by another game.");
                    }
                    return ConsoleValidationResult.Success();
                });

            var dataKey = _promptUI.Prompt(dataKeyPrompt);
            if (string.IsNullOrWhiteSpace(dataKey))
            {
                dataKey = suggestedDataKey;
            }

            // Prompt for DisplayName
            var defaultDisplayName = existingConfig != null && !string.IsNullOrWhiteSpace(existingConfig.DisplayName)
                ? existingConfig.DisplayName!
                : productName ?? string.Empty;

            var displayNamePrompt = new TextPrompt<string>("Enter display name (optional, press Enter to skip)")
                .AllowEmpty()
                .DefaultValue(defaultDisplayName);
            var displayName = _promptUI.Prompt(displayNamePrompt);

            // Prompt for automation enable
            var enableTitle = "Enable automation?";
            var enableChoices = existingConfig?.IsEnabled == false
                ? new[] { "Disabled", "Enabled" }
                : new[] { "Enabled", "Disabled" };
            var enablePrompt = new SelectionPrompt<string>();
            enablePrompt.Title(enableTitle);
            enablePrompt.AddChoices(enableChoices);
            var enable = _promptUI.PromptSelection(enablePrompt, enableChoices, value => Markup.Escape(value), enableTitle);

            // Prompt for HDR
            var hdrTitle = "How to control HDR while a game is running?";
            var defaultHdrEnabled = existingConfig?.HdrEnabled ?? false;
            var hdrChoices = defaultHdrEnabled
                ? new[] { "Auto enable HDR", "Do not auto enable" }
                : new[] { "Do not auto enable", "Auto enable HDR" };
            var hdrPrompt = new SelectionPrompt<string>();
            hdrPrompt.Title(hdrTitle);
            hdrPrompt.AddChoices(hdrChoices);
            var hdr = _promptUI.PromptSelection(hdrPrompt, hdrChoices, value => Markup.Escape(value), hdrTitle);

            var saved = _gameCatalogService.Intake(new GameCatalogIntakeRequest
            {
                DataKey = dataKey,
                Executable = executableIdentity,
                ProductName = productName,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
                IsEnabled = string.Equals(enable, "Enabled", StringComparison.Ordinal),
                HdrEnabled = string.Equals(hdr, "Auto enable HDR", StringComparison.Ordinal)
            }).Entry;

            _console.MarkupLine($"[green]Saved[/]: {Markup.Escape(saved.ExecutableName)} (DataKey: {Markup.Escape(saved.DataKey)})");
            return Task.CompletedTask;
        }

        private Task ImportSteamGamesAsync()
        {
            if (_steamGameResolver is null)
            {
                _console.MarkupLine("[yellow]Steam scan service is not configured in this environment.[/]");
                return Task.CompletedTask;
            }

            var games = _steamGameResolver.TryEnumerateInstalledGames();
            if (games.Count == 0)
            {
                _console.MarkupLine("[yellow]No installed Steam games with executables found.[/]");
                return Task.CompletedTask;
            }

            var table = new Table { Border = TableBorder.Rounded };
            table.AddColumn("Steam AppId");
            table.AddColumn("Game");
            table.AddColumn("Executable");
            foreach (var game in games)
            {
                table.AddRow(
                    Markup.Escape(game.AppId),
                    Markup.Escape(game.Name),
                    Markup.Escape(Path.GetFileName(game.ExecutablePath)));
            }

            _console.Write(table);
            if (!_promptUI.PromptConfirm($"Import the {games.Count} Steam games above? Entries with the same executable will be updated."))
            {
                _console.MarkupLine("[grey]Import cancelled.[/]");
                return Task.CompletedTask;
            }

            var results = _gameCatalogService.BatchIntake(games.Select(game => new GameCatalogIntakeRequest
            {
                Executable = ExecutableIdentity.Parse(game.ExecutablePath),
                ProductName = game.Name,
                DisplayName = game.Name,
                IsEnabled = true
            }));
            var added = results.Count(result => result.WasAdded);
            _console.MarkupLine($"[green]Steam import completed[/]: added {added}, updated {results.Count - added}.");
            return Task.CompletedTask;
        }

        private Task EditGameAsync()
        {
            var configs = _gameCatalogService.List();
            if (configs.Count == 0)
            {
                _console.MarkupLine("[italic grey]No games to edit.[/]");
                return Task.CompletedTask;
            }

            var title = "Select a game to edit";
            var choices = configs
                .Select(c => c.DataKey)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var prompt = new SelectionPrompt<string>
            {
                PageSize = 10
            };
            prompt.Title(title);
            prompt.AddChoices(choices);

            var selectedDataKey = _promptUI.PromptSelection(prompt, choices, value => Markup.Escape(value), title);
            var cfg = configs.FirstOrDefault(entry => string.Equals(entry.DataKey, selectedDataKey, StringComparison.OrdinalIgnoreCase));
            if (cfg is null)
            {
                _console.MarkupLine("[red]Matching config not found.[/]");
                return Task.CompletedTask;
            }

            // Display current configuration
            _console.MarkupLine("[yellow]Current config:[/]");
            _console.MarkupLine($"  DataKey: {Markup.Escape(cfg.DataKey)}");
            _console.MarkupLine($"  Executable: {Markup.Escape(cfg.ExecutableName ?? "-")}");
            if (!string.IsNullOrWhiteSpace(cfg.ExecutablePath))
            {
                _console.MarkupLine($"  Full path: {Markup.Escape(cfg.ExecutablePath)}");
            }
            if (!string.IsNullOrWhiteSpace(cfg.DisplayName))
            {
                _console.MarkupLine($"  Display name: {Markup.Escape(cfg.DisplayName)}");
            }
            _console.WriteLine();

            // Prompt for ExecutablePath update
            var pathPrompt = new TextPrompt<string>(
                "Update executable path (optional, press Enter to keep unchanged)\n" +
                "You can drag & drop an EXE or LNK file")
                .AllowEmpty()
                .DefaultValue(cfg.ExecutablePath ?? string.Empty);
            var pathInput = _promptUI.Prompt(pathPrompt);
            pathInput = pathInput.Trim().Trim('"');

            string? newExecutablePath = cfg.ExecutablePath;
            var clearExecutablePath = false;
            if (!string.IsNullOrWhiteSpace(pathInput) && pathInput != cfg.ExecutablePath)
            {
                if (File.Exists(pathInput))
                {
                    var ext = Path.GetExtension(pathInput).ToLowerInvariant();
                    if (ext == ".lnk")
                    {
                        newExecutablePath = ExecutableResolver.TryResolveFromInput(pathInput);
                        if (string.IsNullOrWhiteSpace(newExecutablePath))
                        {
                            _console.MarkupLine("[red]Failed to resolve shortcut target; keeping the original path.[/]");
                            newExecutablePath = cfg.ExecutablePath;
                        }
                        else
                        {
                            _console.MarkupLine($"[grey]Resolved shortcut target: {Markup.Escape(newExecutablePath)}[/]");
                        }
                    }
                    else if (ext == ".exe")
                    {
                        newExecutablePath = pathInput;
                    }
                    else
                    {
                        _console.MarkupLine("[yellow]Unsupported file type; keeping the original path.[/]");
                    }
                }
                else if (string.Equals(pathInput, "clear", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(pathInput, "remove", StringComparison.OrdinalIgnoreCase))
                {
                    newExecutablePath = null;
                    clearExecutablePath = true;
                    _console.MarkupLine("[grey]Executable path cleared.[/]");
                }
                else
                {
                    _console.MarkupLine("[yellow]File not found; keeping the original path. Type 'clear' to clear the path.[/]");
                }
            }

            // Prompt for DisplayName update
            var displayNamePrompt = new TextPrompt<string>("Update display name (may be left empty)")
                .AllowEmpty()
                .DefaultValue(cfg.DisplayName ?? string.Empty);
            var displayName = _promptUI.Prompt(displayNamePrompt);

            // Prompt for automation enable
            var enableTitle = "Enable automation?";
            var enableChoices = cfg.IsEnabled
                ? new[] { "Enabled", "Disabled" }
                : new[] { "Disabled", "Enabled" };
            var enablePrompt = new SelectionPrompt<string>();
            enablePrompt.Title(enableTitle);
            enablePrompt.AddChoices(enableChoices);
            var enable = _promptUI.PromptSelection(enablePrompt, enableChoices, value => Markup.Escape(value), enableTitle);

            // Prompt for HDR
            var hdrTitle = "How to control HDR while a game is running?";
            var hdrChoices = cfg.HdrEnabled
                ? new[] { "Auto enable HDR", "Do not auto enable" }
                : new[] { "Do not auto enable", "Auto enable HDR" };
            var hdrPrompt = new SelectionPrompt<string>();
            hdrPrompt.Title(hdrTitle);
            hdrPrompt.AddChoices(hdrChoices);
            var hdr = _promptUI.PromptSelection(hdrPrompt, hdrChoices, value => Markup.Escape(value), hdrTitle);

            ExecutableIdentity? updatedIdentity = null;
            if (clearExecutablePath)
            {
                updatedIdentity = ExecutableIdentity.Parse(cfg.ExecutableName!);
            }
            else if (!string.Equals(newExecutablePath, cfg.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(newExecutablePath))
            {
                updatedIdentity = ExecutableIdentity.Parse(newExecutablePath!);
            }

            var updated = _gameCatalogService.Update(cfg.DataKey, new GameCatalogUpdateRequest
            {
                Executable = updatedIdentity,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
                ClearDisplayName = string.IsNullOrWhiteSpace(displayName),
                IsEnabled = string.Equals(enable, "Enabled", StringComparison.Ordinal),
                HdrEnabled = string.Equals(hdr, "Auto enable HDR", StringComparison.Ordinal)
            });
            _console.MarkupLine("[green]Config updated.[/]");

            // Display updated configuration
            _console.WriteLine();
            _console.MarkupLine("[yellow]Updated config:[/]");
            _console.MarkupLine($"  DataKey: {Markup.Escape(updated.DataKey)}");
            if (!string.IsNullOrWhiteSpace(updated.ExecutablePath))
            {
                _console.MarkupLine($"  Full path: {Markup.Escape(updated.ExecutablePath)}");
            }
            if (!string.IsNullOrWhiteSpace(updated.DisplayName))
            {
                _console.MarkupLine($"  Display name: {Markup.Escape(updated.DisplayName)}");
            }

            return Task.CompletedTask;
        }

        private async Task RemoveGameAsync()
        {
            await Task.CompletedTask.ConfigureAwait(false);

            var configs = _gameCatalogService.List();
            if (configs.Count == 0)
            {
                _console.MarkupLine("[italic grey]No games to remove.[/]");
                return;
            }

            var title = "Select a game to remove";
            var choices = configs
                .Select(c => c.DataKey)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var prompt = new SelectionPrompt<string>
            {
                PageSize = 10
            };
            prompt.Title(title);
            prompt.AddChoices(choices);

            var selectedDataKey = _promptUI.PromptSelection(prompt, choices, value => Markup.Escape(value), title);
            if (!configs.Any(entry => string.Equals(entry.DataKey, selectedDataKey, StringComparison.OrdinalIgnoreCase)))
            {
                _console.MarkupLine("[red]Matching config not found.[/]");
                return;
            }

            var confirm = _promptUI.PromptConfirm($"Remove [bold]{Markup.Escape(selectedDataKey)}[/]?");
            if (!confirm)
            {
                return;
            }

            _gameCatalogService.Remove(selectedDataKey);
            _console.MarkupLine("[yellow]Game removed.[/]");
        }

    }
}
