using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using FuzzySharp;
using GameHelper.ConsoleHost.Models;
using GameHelper.ConsoleHost.Services;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Providers;
using Spectre.Console;

namespace GameHelper.ConsoleHost.Commands
{
    /// <summary>
    /// Provides migration functionality for legacy configuration and CSV data formats.
    /// Migrates from name/alias format to dataKey/executableName/displayName format.
    /// </summary>
    public static class MigrateCommand
    {
        /// <summary>
        /// Configuration format detection result.
        /// </summary>
        private enum ConfigFormat
        {
            /// <summary>Uses name/alias fields, missing dataKey.</summary>
            OldFormat,
            /// <summary>Uses dataKey field with executableName/displayName.</summary>
            NewFormat,
            /// <summary>Mix of old and new formats.</summary>
            Mixed
        }

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
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string gameHelperDir = Path.Combine(appData, "GameHelper");
            
            if (string.IsNullOrEmpty(configPath))
            {
                configPath = Path.Combine(gameHelperDir, "config.yml");
            }
            
            if (string.IsNullOrEmpty(csvPath))
            {
                csvPath = Path.Combine(gameHelperDir, "playtime.csv");
            }

            if (dryRun)
            {
                AnsiConsole.MarkupLine("[yellow]预览模式：不会修改任何文件[/]");
            }

            AnsiConsole.MarkupLine($"[blue]配置文件: {configPath}[/]");
            AnsiConsole.MarkupLine($"[blue]CSV 文件: {csvPath}[/]");
            AnsiConsole.WriteLine();

            // Step 1: Migrate configuration
            IReadOnlyDictionary<string, GameConfig>? migratedConfig = null;
            bool configMigrated = false;

            if (File.Exists(configPath))
            {
                migratedConfig = MigrateConfiguration(configPath, dryRun, force, out configMigrated);
                if (migratedConfig == null)
                {
                    AnsiConsole.MarkupLine("[red]✗ 配置迁移失败[/]");
                    return;
                }
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]⚠ 配置文件不存在: {configPath}[/]");
                AnsiConsole.MarkupLine("[yellow]跳过配置迁移[/]");
            }

            // Step 2: Ask if user wants to migrate CSV
            if (File.Exists(csvPath))
            {
                if (!force && !dryRun && configMigrated)
                {
                    AnsiConsole.WriteLine();
                    if (!AnsiConsole.Confirm("是否继续迁移 CSV 数据?", true))
                    {
                        AnsiConsole.MarkupLine("[yellow]CSV 迁移已取消[/]");
                        return;
                    }
                }

                if (migratedConfig != null)
                {
                    MigrateCsvData(csvPath, migratedConfig, dryRun, force);
                }
                else
                {
                    AnsiConsole.MarkupLine("[yellow]⚠ 没有有效的配置，跳过 CSV 迁移[/]");
                }
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]⚠ CSV 文件不存在: {csvPath}[/]");
            }

            if (dryRun)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[yellow]预览模式完成 - 未修改任何文件[/]");
            }
        }

        /// <summary>
        /// Detects the configuration format.
        /// </summary>
        private static ConfigFormat DetectConfigFormat(IReadOnlyDictionary<string, GameConfig> configs)
        {
            if (configs.Count == 0)
            {
                return ConfigFormat.NewFormat; // Empty is considered new format
            }

            bool hasOldFormat = configs.Values.Any(g =>
                (!string.IsNullOrEmpty(g.Name) || !string.IsNullOrEmpty(g.Alias)) &&
                string.IsNullOrEmpty(g.DataKey));

            bool hasNewFormat = configs.Values.Any(g =>
                !string.IsNullOrEmpty(g.DataKey));

            if (hasNewFormat && !hasOldFormat)
                return ConfigFormat.NewFormat;

            if (hasOldFormat && !hasNewFormat)
                return ConfigFormat.OldFormat;

            return ConfigFormat.Mixed;
        }

        /// <summary>
        /// Generates a DataKey from an executable name.
        /// </summary>
        private static string GenerateDataKey(string executableName)
        {
            if (string.IsNullOrWhiteSpace(executableName))
            {
                return string.Empty;
            }

            // Extract filename without path
            string fileName = Path.GetFileNameWithoutExtension(executableName);
            
            // Convert to lowercase
            string dataKey = fileName.ToLowerInvariant();
            
            // Remove spaces
            dataKey = dataKey.Replace(" ", "");
            
            return dataKey;
        }

        /// <summary>
        /// Migrates configuration from old format to new format.
        /// </summary>
        private static IReadOnlyDictionary<string, GameConfig>? MigrateConfiguration(
            string configPath,
            bool dryRun,
            bool force,
            out bool migrated)
        {
            migrated = false;

            AnsiConsole.MarkupLine("[cyan]═══ 配置文件迁移 ═══[/]");

            try
            {
                // Load existing configuration
                var provider = new YamlConfigProvider(configPath);
                var existingConfigs = provider.Load();

                // Detect format
                var format = DetectConfigFormat(existingConfigs);

                if (format == ConfigFormat.NewFormat)
                {
                    AnsiConsole.MarkupLine("[green]✓ 配置已是新格式，无需迁移[/]");
                    return existingConfigs;
                }

                // Determine which games need migration
                var gamesToMigrate = format == ConfigFormat.Mixed
                    ? existingConfigs.Where(kv => string.IsNullOrEmpty(kv.Value.DataKey)).ToList()
                    : existingConfigs.ToList();

                if (gamesToMigrate.Count == 0)
                {
                    AnsiConsole.MarkupLine("[green]✓ 所有游戏配置已包含 DataKey[/]");
                    return existingConfigs;
                }

                AnsiConsole.MarkupLine($"[yellow]检测到 {gamesToMigrate.Count} 个游戏需要迁移[/]");

                // Show preview table
                var table = new Table();
                table.AddColumn("游戏");
                table.AddColumn("旧字段 (name)");
                table.AddColumn("旧字段 (alias)");
                table.AddColumn("新字段 (dataKey)");
                table.AddColumn("新字段 (executableName)");
                table.AddColumn("新字段 (displayName)");

                var migratedConfigs = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);

                foreach (var kv in existingConfigs)
                {
                    GameConfig config;
                    
                    if (string.IsNullOrEmpty(kv.Value.DataKey))
                    {
                        // Need migration
                        string dataKey = GenerateDataKey(kv.Value.Name ?? string.Empty);
                        
                        config = new GameConfig
                        {
                            DataKey = dataKey,
                            ExecutableName = kv.Value.Name,
                            DisplayName = kv.Value.Alias,
                            ExecutablePath = kv.Value.ExecutablePath ?? string.Empty,
                            IsEnabled = kv.Value.IsEnabled,
                            HDREnabled = kv.Value.HDREnabled
                        };

                        table.AddRow(
                            $"[yellow]#{migratedConfigs.Count + 1}[/]",
                            kv.Value.Name ?? "[dim]N/A[/]",
                            kv.Value.Alias ?? "[dim]N/A[/]",
                            $"[green]{dataKey}[/]",
                            config.ExecutableName ?? "[dim]N/A[/]",
                            config.DisplayName ?? "[dim]N/A[/]"
                        );
                    }
                    else
                    {
                        // Already new format, keep as is
                        config = kv.Value;
                    }

                    migratedConfigs[config.DataKey] = config;
                }

                AnsiConsole.Write(table);

                if (dryRun)
                {
                    AnsiConsole.MarkupLine("[yellow]预览模式：配置文件不会被修改[/]");
                    return migratedConfigs;
                }

                // Confirm migration
                if (!force)
                {
                    if (!AnsiConsole.Confirm($"确认迁移 {gamesToMigrate.Count} 个游戏配置?", true))
                    {
                        AnsiConsole.MarkupLine("[yellow]配置迁移已取消[/]");
                        return null;
                    }
                }

                // Backup original file
                string timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                string backupPath = $"{configPath}.backup.{timestamp}";
                File.Copy(configPath, backupPath);
                AnsiConsole.MarkupLine($"[green]✓ 已备份配置文件: {Path.GetFileName(backupPath)}[/]");

                // Save migrated configuration
                provider.Save(migratedConfigs);
                AnsiConsole.MarkupLine($"[green]✓ 成功迁移 {gamesToMigrate.Count} 个游戏配置[/]");

                migrated = true;
                return migratedConfigs;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ 配置迁移失败: {ex.Message}[/]");
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
            AnsiConsole.MarkupLine("[cyan]═══ CSV 数据迁移 ═══[/]");

            try
            {
                if (!File.Exists(csvPath))
                {
                    AnsiConsole.MarkupLine($"[yellow]⚠ CSV 文件不存在: {csvPath}[/]");
                    return;
                }

                // Read CSV records
                var lines = File.ReadAllLines(csvPath, Encoding.UTF8);
                if (lines.Length == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]⚠ CSV 文件为空[/]");
                    return;
                }

                // Parse CSV
                string header = lines[0];
                var records = new List<(string originalGame, string game, string startTime, string endTime, string duration, bool isNewFormat)>();

                for (int i = 1; i < lines.Length; i++)
                {
                    var parts = ParseCsvLine(lines[i]);
                    if (parts.Length >= 4)
                    {
                        string game = parts[0];
                        string originalGame = game;
                        
                        // Check if already new format (matches any DataKey)
                        bool isNewFormat = configs.Any(kv =>
                            kv.Value.DataKey.Equals(game, StringComparison.OrdinalIgnoreCase));

                        records.Add((originalGame, game, parts[1], parts[2], parts[3], isNewFormat));
                    }
                }

                // Migration statistics
                int totalRecords = records.Count;
                int skippedCount = 0;
                int migratedCount = 0;
                var orphanedRecords = new List<string>();
                var migrationDetails = new List<(string old, string newKey, string method)>();

                // Process each record
                for (int i = 0; i < records.Count; i++)
                {
                    var record = records[i];

                    if (record.isNewFormat)
                    {
                        skippedCount++;
                        continue;
                    }

                    // Try exact match on ExecutableName
                    var exactMatch = configs.FirstOrDefault(kv =>
                        !string.IsNullOrEmpty(kv.Value.ExecutableName) &&
                        kv.Value.ExecutableName.Equals(record.game, StringComparison.OrdinalIgnoreCase));

                    if (exactMatch.Value != null)
                    {
                        records[i] = (record.originalGame, exactMatch.Value.DataKey, record.startTime, record.endTime, record.duration, false);
                        migratedCount++;
                        migrationDetails.Add((record.originalGame, exactMatch.Value.DataKey, "精确匹配"));
                        continue;
                    }

                    // Try fuzzy match
                    var fuzzyMatches = configs
                        .Where(kv => !string.IsNullOrEmpty(kv.Value.ExecutableName))
                        .Select(kv => new
                        {
                            Config = kv.Value,
                            Score = Fuzz.Ratio(record.game, kv.Value.ExecutableName!)
                        })
                        .Where(x => x.Score > 80)
                        .OrderByDescending(x => x.Score)
                        .ToList();

                    if (fuzzyMatches.Any())
                    {
                        var bestMatch = fuzzyMatches.First();
                        records[i] = (record.originalGame, bestMatch.Config.DataKey, record.startTime, record.endTime, record.duration, false);
                        migratedCount++;
                        migrationDetails.Add((record.originalGame, bestMatch.Config.DataKey, $"模糊匹配 ({bestMatch.Score}%)"));
                        continue;
                    }

                    // No match found
                    orphanedRecords.Add(record.game);
                }

                // Show migration preview
                AnsiConsole.MarkupLine($"扫描的总记录数:     [cyan]{totalRecords}[/]");
                AnsiConsole.MarkupLine($"成功迁移的记录数:   [green]{migratedCount}[/]");
                AnsiConsole.MarkupLine($"已是新格式（跳过）: [blue]{skippedCount}[/]");
                AnsiConsole.MarkupLine($"无法匹配的记录数:   [yellow]{orphanedRecords.Count}[/]");
                AnsiConsole.WriteLine();

                // Show migration details (first 10)
                if (migrationDetails.Any())
                {
                    var detailTable = new Table();
                    detailTable.AddColumn("原游戏名称");
                    detailTable.AddColumn("新 DataKey");
                    detailTable.AddColumn("匹配方式");

                    foreach (var detail in migrationDetails.Take(10))
                    {
                        detailTable.AddRow(detail.old, $"[green]{detail.newKey}[/]", detail.method);
                    }

                    if (migrationDetails.Count > 10)
                    {
                        detailTable.AddRow("[dim]...[/]", $"[dim]（还有 {migrationDetails.Count - 10} 条）[/]", "[dim]...[/]");
                    }

                    AnsiConsole.Write(detailTable);
                    AnsiConsole.WriteLine();
                }

                // Show orphaned records
                if (orphanedRecords.Any())
                {
                    AnsiConsole.MarkupLine("[yellow]无法匹配的记录：[/]");
                    var orphanTable = new Table();
                    orphanTable.AddColumn("游戏名称");
                    orphanTable.AddColumn("建议操作");

                    foreach (var orphan in orphanedRecords.Take(10))
                    {
                        orphanTable.AddRow(orphan, "手动添加配置或编辑CSV");
                    }

                    if (orphanedRecords.Count > 10)
                    {
                        orphanTable.AddRow($"[dim]...（还有 {orphanedRecords.Count - 10} 条）[/]", "[dim]...[/]");
                    }

                    AnsiConsole.Write(orphanTable);
                    AnsiConsole.WriteLine();
                }

                if (dryRun)
                {
                    AnsiConsole.MarkupLine("[yellow]预览模式：CSV 文件不会被修改[/]");
                    return;
                }

                if (migratedCount == 0)
                {
                    AnsiConsole.MarkupLine("[green]✓ 所有记录已是新格式或无法匹配，无需修改 CSV[/]");
                    return;
                }

                // Confirm migration
                if (!force)
                {
                    if (!AnsiConsole.Confirm($"确认迁移 {migratedCount} 条记录?", true))
                    {
                        AnsiConsole.MarkupLine("[yellow]CSV 迁移已取消[/]");
                        return;
                    }
                }

                // Backup original CSV
                string timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                string backupPath = $"{csvPath}.backup.{timestamp}";
                File.Copy(csvPath, backupPath);
                AnsiConsole.MarkupLine($"[green]✓ 已备份 CSV 文件: {Path.GetFileName(backupPath)}[/]");

                // Write migrated CSV
                using (var writer = new StreamWriter(csvPath, false, Encoding.UTF8))
                {
                    writer.WriteLine(header);
                    foreach (var record in records)
                    {
                        writer.WriteLine($"{EscapeCsvField(record.game)},{record.startTime},{record.endTime},{record.duration}");
                    }
                }

                AnsiConsole.MarkupLine($"[green]✓ 成功迁移 {migratedCount} 条 CSV 记录[/]");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[dim]备份文件: {backupPath}[/]");
                AnsiConsole.MarkupLine($"[dim]如需回滚，请运行: copy \"{backupPath}\" \"{csvPath}\"[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ CSV 迁移失败: {ex.Message}[/]");
            }
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
