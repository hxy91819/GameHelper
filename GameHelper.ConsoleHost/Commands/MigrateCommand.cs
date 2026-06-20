using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using GameHelper.ConsoleHost.Models;
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
                AnsiConsole.MarkupLine("[yellow]预览模式：不会修改任何文件[/]");
            }

            AnsiConsole.MarkupLine($"[blue]配置文件: {configPath}[/]");
            AnsiConsole.MarkupLine($"[blue]CSV 文件: {csvPath}[/]");
            AnsiConsole.WriteLine();

            // Step 1: Load current configuration
            IReadOnlyDictionary<string, GameConfig>? migratedConfig = null;
            bool configMigrated = false;

            if (File.Exists(configPath))
            {
                migratedConfig = MigrateConfiguration(configPath, out configMigrated);
                if (migratedConfig == null)
                {
                    AnsiConsole.MarkupLine("[red]✗ 配置读取失败[/]");
                    return;
                }
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]⚠ 配置文件不存在: {configPath}[/]");
                AnsiConsole.MarkupLine("[yellow]跳过配置读取[/]");
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
        /// Loads the current compact YAML configuration for CSV migration.
        /// </summary>
        private static IReadOnlyDictionary<string, GameConfig>? MigrateConfiguration(
            string configPath,
            out bool migrated)
        {
            migrated = false;

            AnsiConsole.MarkupLine("[cyan]═══ 配置文件检查 ═══[/]");

            try
            {
                var provider = new YamlConfigProvider(configPath);
                var configs = provider.Load();
                AnsiConsole.MarkupLine($"[green]✓ 已读取 {configs.Count} 个游戏配置[/]");
                return configs;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ 配置读取失败: {ex.Message}[/]");
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

        private static string FormatMigrationMethod(PlaytimeMigrationMatch match)
        {
            return match.Kind switch
            {
                PlaytimeMigrationMatchKind.ExactExecutableName => "精确匹配",
                PlaytimeMigrationMatchKind.FuzzyExecutableName => $"模糊匹配 ({match.Score}%)",
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
