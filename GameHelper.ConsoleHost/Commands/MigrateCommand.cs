using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using GameHelper.ConsoleHost.Services;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Core.Utilities;
using GameHelper.Infrastructure.Providers;
using Spectre.Console;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GameHelper.ConsoleHost.Commands
{
    /// <summary>
    /// Provides migration functionality for CSV playtime data.
    /// Migrates playtime records to stable dataKey values using the current compact YAML configuration.
    /// </summary>
    public static class MigrateCommand
    {
        /// <summary>
        /// Executes the migration command.
        /// </summary>
        public static void Run(string[] args)
        {
            // Parse arguments
            string? configPath = null;
            string? csvPath = null;
            bool dryRun = false;
            bool force = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--config":
                        if (i + 1 < args.Length) configPath = args[++i];
                        break;
                    case "--csv":
                        if (i + 1 < args.Length) csvPath = args[++i];
                        break;
                    case "--dry-run":
                    case "--preview":
                        dryRun = true;
                        break;
                    case "--force":
                        force = true;
                        break;
                }
            }

            // Default paths if not specified
            if (string.IsNullOrEmpty(configPath))
            {
                configPath = AppDataPath.GetConfigPath();
            }
            
            if (string.IsNullOrEmpty(csvPath))
            {
                csvPath = AppDataPath.GetPlaytimeCsvPath();
            }

            if (dryRun)
            {
                AnsiConsole.MarkupLine("[yellow]Preview mode: no files will be modified[/]");
            }

            AnsiConsole.MarkupLine($"[blue]Config file: {configPath}[/]");
            AnsiConsole.MarkupLine($"[blue]CSV file: {csvPath}[/]");
            AnsiConsole.WriteLine();

            // Step 1: Load current configuration
            IReadOnlyDictionary<string, GameConfig>? migratedConfig = null;
            bool configMigrated = false;

            if (File.Exists(configPath))
            {
                migratedConfig = MigrateConfiguration(configPath, out configMigrated);
                if (migratedConfig == null)
                {
                    AnsiConsole.MarkupLine("[red]✗ Failed to read config[/]");
                    return;
                }
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]⚠ Config file not found: {configPath}[/]");
                AnsiConsole.MarkupLine("[yellow]Skipping config read[/]");
            }

            // Step 2: Ask if user wants to migrate CSV
            if (File.Exists(csvPath))
            {
                if (!force && !dryRun && configMigrated)
                {
                    AnsiConsole.WriteLine();
                    if (!AnsiConsole.Confirm("Continue migrating CSV data?", true))
                    {
                        AnsiConsole.MarkupLine("[yellow]CSV migration cancelled[/]");
                        return;
                    }
                }

                if (migratedConfig != null)
                {
                    MigrateCsvData(csvPath, migratedConfig, dryRun, force);
                }
                else
                {
                    AnsiConsole.MarkupLine("[yellow]⚠ No valid config; skipping CSV migration[/]");
                }
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]⚠ CSV file not found: {csvPath}[/]");
            }

            if (dryRun)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[yellow]Preview mode completed - no files were modified[/]");
            }
        }

        /// <summary>
        /// Loads the current compact YAML configuration for CSV migration.
        /// </summary>
        private static IReadOnlyDictionary<string, GameConfig>? MigrateConfiguration(
            string configPath,
            out bool migrated)
        {
            migrated = false;

            AnsiConsole.MarkupLine("[cyan]═══ Config file check ═══[/]");

            try
            {
                var provider = new YamlConfigProvider(configPath);
                var configs = provider.Read().Games.ToDictionary(
                    config => config.DataKey,
                    StringComparer.OrdinalIgnoreCase);
                AnsiConsole.MarkupLine($"[green]✓ Read {configs.Count} game configs[/]");
                return configs;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ Failed to read config: {ex.Message}[/]");
                return null;
            }
        }

        /// <summary>
        /// Migrates CSV playtime data from executable names to DataKeys.
        /// </summary>
        private static void MigrateCsvData(
            string csvPath,
            IReadOnlyDictionary<string, GameConfig> configs,
            bool dryRun,
            bool force)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[cyan]═══ CSV data migration ═══[/]");

            try
            {
                if (!File.Exists(csvPath))
                {
                    AnsiConsole.MarkupLine($"[yellow]⚠ CSV file not found: {csvPath}[/]");
                    return;
                }

                // Read CSV records
                var lines = File.ReadAllLines(csvPath, Encoding.UTF8);
                if (lines.Length == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]⚠ CSV file is empty[/]");
                    return;
                }

                // Parse CSV
                string header = lines[0];
                var records = new List<(string originalGame, string game, string startTime, string endTime, string duration)>();

                for (int i = 1; i < lines.Length; i++)
                {
                    var parts = ParseCsvLine(lines[i]);
                    if (parts.Length >= 4)
                    {
                        string game = parts[0];
                        string originalGame = game;
                        
                        records.Add((originalGame, game, parts[1], parts[2], parts[3]));
                    }
                }

                // Migration statistics
                int totalRecords = records.Count;
                int skippedCount = 0;
                int migratedCount = 0;
                var orphanedRecords = new List<string>();
                var migrationDetails = new List<(string old, string newKey, string method)>();
                var migrationMatcher = new PlaytimeMigrationMatcher();

                // Process each record
                for (int i = 0; i < records.Count; i++)
                {
                    var record = records[i];

                    var match = migrationMatcher.Match(record.game, configs.Values);
                    if (match.Kind == PlaytimeMigrationMatchKind.AlreadyDataKey)
                    {
                        skippedCount++;
                        continue;
                    }

                    if (match.ShouldRewrite && !string.IsNullOrWhiteSpace(match.DataKey))
                    {
                        records[i] = (record.originalGame, match.DataKey, record.startTime, record.endTime, record.duration);
                        migratedCount++;
                        migrationDetails.Add((record.originalGame, match.DataKey, FormatMigrationMethod(match)));
                        continue;
                    }

                    if (match.Kind == PlaytimeMigrationMatchKind.Ambiguous)
                    {
                        orphanedRecords.Add($"{record.game} (ambiguous)");
                        continue;
                    }

                    // No match found
                    orphanedRecords.Add(record.game);
                }

                // Show migration preview
                AnsiConsole.MarkupLine($"Total records scanned:      [cyan]{totalRecords}[/]");
                AnsiConsole.MarkupLine($"Successfully migrated:      [green]{migratedCount}[/]");
                AnsiConsole.MarkupLine($"Already in new format:      [blue]{skippedCount}[/]");
                AnsiConsole.MarkupLine($"Unmatched records:          [yellow]{orphanedRecords.Count}[/]");
                AnsiConsole.WriteLine();

                // Show migration details (first 10)
                if (migrationDetails.Any())
                {
                    var detailTable = new Table();
                    detailTable.AddColumn("Original game");
                    detailTable.AddColumn("New DataKey");
                    detailTable.AddColumn("Match method");

                    foreach (var detail in migrationDetails.Take(10))
                    {
                        detailTable.AddRow(detail.old, $"[green]{detail.newKey}[/]", detail.method);
                    }

                    if (migrationDetails.Count > 10)
                    {
                        detailTable.AddRow("[dim]...[/]", $"[dim]({migrationDetails.Count - 10} more)[/]", "[dim]...[/]");
                    }

                    AnsiConsole.Write(detailTable);
                    AnsiConsole.WriteLine();
                }

                // Show orphaned records
                if (orphanedRecords.Any())
                {
                    AnsiConsole.MarkupLine("[yellow]Unmatched records:[/]");
                    var orphanTable = new Table();
                    orphanTable.AddColumn("Game name");
                    orphanTable.AddColumn("Suggested action");

                    foreach (var orphan in orphanedRecords.Take(10))
                    {
                        orphanTable.AddRow(orphan, "Add the config manually or edit the CSV");
                    }

                    if (orphanedRecords.Count > 10)
                    {
                        orphanTable.AddRow($"[dim]...({orphanedRecords.Count - 10} more)[/]", "[dim]...[/]");
                    }

                    AnsiConsole.Write(orphanTable);
                    AnsiConsole.WriteLine();
                }

                if (dryRun)
                {
                    AnsiConsole.MarkupLine("[yellow]Preview mode: the CSV file will not be modified[/]");
                    return;
                }

                if (migratedCount == 0)
                {
                    AnsiConsole.MarkupLine("[green]✓ All records are already in the new format or unmatched; no CSV changes needed[/]");
                    return;
                }

                // Confirm migration
                if (!force)
                {
                    if (!AnsiConsole.Confirm($"Migrate {migratedCount} record(s)?", true))
                    {
                        AnsiConsole.MarkupLine("[yellow]CSV migration cancelled[/]");
                        return;
                    }
                }

                // Backup original CSV
                string timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                string backupPath = $"{csvPath}.backup.{timestamp}";
                File.Copy(csvPath, backupPath);
                AnsiConsole.MarkupLine($"[green]✓ Backed up CSV file: {Path.GetFileName(backupPath)}[/]");

                // Write migrated CSV
                using (var writer = new StreamWriter(csvPath, false, Encoding.UTF8))
                {
                    writer.WriteLine(header);
                    foreach (var record in records)
                    {
                        writer.WriteLine($"{EscapeCsvField(record.game)},{record.startTime},{record.endTime},{record.duration}");
                    }
                }

                AnsiConsole.MarkupLine($"[green]✓ Successfully migrated {migratedCount} CSV record(s)[/]");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[dim]Backup file: {backupPath}[/]");
                AnsiConsole.MarkupLine($"[dim]To roll back, run: copy \"{backupPath}\" \"{csvPath}\"[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ CSV migration failed: {ex.Message}[/]");
            }
        }

        private static string FormatMigrationMethod(PlaytimeMigrationMatch match)
        {
            return match.Kind switch
            {
                PlaytimeMigrationMatchKind.ExactExecutableName => "Exact match",
                PlaytimeMigrationMatchKind.FuzzyExecutableName => $"Fuzzy match ({match.Score}%)",
                _ => match.Kind.ToString()
            };
        }

        /// <summary>
        /// Parses a CSV line handling quoted fields.
        /// </summary>
        private static string[] ParseCsvLine(string line)
        {
            var fields = new List<string>();
            var currentField = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        // Escaped quote
                        currentField.Append('"');
                        i++;
                    }
                    else
                    {
                        // Toggle quote mode
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    // Field separator
                    fields.Add(currentField.ToString());
                    currentField.Clear();
                }
                else
                {
                    currentField.Append(c);
                }
            }

            // Add last field
            fields.Add(currentField.ToString());

            return fields.ToArray();
        }

        /// <summary>
        /// Escapes a CSV field value.
        /// </summary>
        private static string EscapeCsvField(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            {
                return $"\"{value.Replace("\"", "\"\"")}\"";
            }

            return value;
        }
    }
}
