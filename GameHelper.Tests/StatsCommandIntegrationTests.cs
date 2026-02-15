using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using GameHelper.ConsoleHost.Services;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Providers;
using Xunit;

namespace GameHelper.Tests
{
    public class StatsCommandIntegrationTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _csvFile;
        private readonly string _jsonFile;
        private readonly string _configFile;

        public StatsCommandIntegrationTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "GameHelperTests_StatsIntegration", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _csvFile = Path.Combine(_dir, "playtime.csv");
            _jsonFile = Path.Combine(_dir, "playtime.json");
            _configFile = Path.Combine(_dir, "config.yml");
        }

        [Fact]
        public void CsvBackedService_CreatesValidCsvForStatsCommand()
        {
            var svc = new CsvBackedPlayTimeService(_dir);
            
            // Create some sessions
            svc.StartTracking("witcher3.exe");
            System.Threading.Thread.Sleep(100); // Small delay to ensure different timestamps
            svc.StopTracking("witcher3.exe");
            
            svc.StartTracking("rdr2.exe");
            System.Threading.Thread.Sleep(100);
            svc.StopTracking("rdr2.exe");

            // Verify CSV file was created correctly
            Assert.True(File.Exists(_csvFile));
            var lines = File.ReadAllLines(_csvFile);
            
            Assert.Equal("game,start_time,end_time,duration_minutes", lines[0]);
            Assert.True(lines.Length >= 3); // header + 2 sessions
            
            // Verify CSV format is parseable
            for (int i = 1; i < lines.Length; i++)
            {
                var parts = lines[i].Split(',');
                Assert.True(parts.Length >= 4, $"Line {i} should have at least 4 parts: {lines[i]}");
                
                // Verify game name
                Assert.True(parts[0] == "witcher3.exe" || parts[0] == "rdr2.exe");
                
                // Verify timestamps are parseable
                Assert.True(DateTime.TryParse(parts[1], out _), $"Start time should be parseable: {parts[1]}");
                Assert.True(DateTime.TryParse(parts[2], out _), $"End time should be parseable: {parts[2]}");
                
                // Verify duration is numeric
                Assert.True(long.TryParse(parts[3], out var duration), $"Duration should be numeric: {parts[3]}");
                Assert.True(duration >= 0, "Duration should be non-negative");
            }
        }

        [Fact]
        public void MigrationFromJson_CreatesCorrectCsvFormat()
        {
            // Create a JSON file with known data
            var jsonData = new
            {
                games = new[]
                {
                    new
                    {
                        GameName = "witcher3.exe",
                        Sessions = new[]
                        {
                            new
                            {
                                StartTime = new DateTime(2025, 8, 16, 10, 0, 0),
                                EndTime = new DateTime(2025, 8, 16, 11, 40, 0),
                                DurationMinutes = 100L
                            }
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(jsonData, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_jsonFile, json);

            // Create service - should trigger migration
            var svc = new CsvBackedPlayTimeService(_dir);

            // Verify migration created correct CSV
            Assert.True(File.Exists(_csvFile));
            var lines = File.ReadAllLines(_csvFile);
            
            Assert.Equal("game,start_time,end_time,duration_minutes", lines[0]);
            Assert.Equal(2, lines.Length); // header + 1 session
            
            var sessionLine = lines[1];
            Assert.Equal("witcher3.exe,2025-08-16T10:00:00,2025-08-16T11:40:00,100", sessionLine);
        }

        // Story 1.4: Tests for DisplayName priority and DataKey grouping
        [Fact]
        public void ReadFromCsv_ShouldGroupByDataKey()
        {
            // Given: CSV 包含多条 "elden_ring" 的记录
            var csvContent = new StringBuilder();
            csvContent.AppendLine("game,start_time,end_time,duration_minutes");
            csvContent.AppendLine("elden_ring,2025-11-01T10:00:00,2025-11-01T12:00:00,120");
            csvContent.AppendLine("elden_ring,2025-11-02T14:00:00,2025-11-02T15:30:00,90");
            csvContent.AppendLine("dark_souls_3,2025-11-03T20:00:00,2025-11-03T22:00:00,120");
            
            File.WriteAllText(_csvFile, csvContent.ToString());
            
            // When: 读取 CSV
            var games = PlaytimeDataReader.ReadFromCsv(_csvFile);
            
            // Then: 应按 DataKey 分组
            Assert.Equal(2, games.Count);
            
            var eldenRing = games.First(g => g.GameName.Equals("elden_ring", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(2, eldenRing.Sessions.Count);
            Assert.Equal(210, eldenRing.Sessions.Sum(s => s.DurationMinutes)); // 120 + 90
            
            var darkSouls = games.First(g => g.GameName.Equals("dark_souls_3", StringComparison.OrdinalIgnoreCase));
            Assert.Single(darkSouls.Sessions);
            Assert.Equal(120, darkSouls.Sessions.Sum(s => s.DurationMinutes));
        }

        [Fact]
        public void ReadFromCsv_WithConfig_ShouldUseDisplayName()
        {
            // Given: 配置文件包含 DataKey 和 DisplayName
            var appConfig = new AppConfig
            {
                Games = new List<GameConfig>
                {
                    new() { DataKey = "elden_ring", ExecutableName = "eldenring.exe", DisplayName = "Elden Ring", IsEnabled = true },
                    new() { DataKey = "dark_souls_3", ExecutableName = "darksouls3.exe", DisplayName = "Dark Souls III", IsEnabled = true }
                }
            };
            
            var provider = new YamlConfigProvider(_configFile);
            provider.SaveAppConfig(appConfig);
            
            // CSV 包含 DataKey
            var csvContent = new StringBuilder();
            csvContent.AppendLine("game,start_time,end_time,duration_minutes");
            csvContent.AppendLine("elden_ring,2025-11-01T10:00:00,2025-11-01T12:00:00,120");
            csvContent.AppendLine("dark_souls_3,2025-11-02T14:00:00,2025-11-02T15:30:00,90");
            
            File.WriteAllText(_csvFile, csvContent.ToString());
            
            // When: 读取配置和 CSV
            var configs = provider.Load();
            var games = PlaytimeDataReader.ReadFromCsv(_csvFile);
            
            // Then:
            // YamlConfigProvider returns dictionary keyed by EntryId.
            // We validate through entry values (DataKey / DisplayName / ExecutableName).
            Assert.Equal(2, configs.Count);
            
            // Verify configs can be found and have correct DisplayName
            var eldenConfig = configs.Values.First(c => c.DataKey == "elden_ring");
            Assert.Equal("Elden Ring", eldenConfig.DisplayName);
            Assert.Equal("eldenring.exe", eldenConfig.ExecutableName);
            
            var darkSoulsConfig = configs.Values.First(c => c.DataKey == "dark_souls_3");
            Assert.Equal("Dark Souls III", darkSoulsConfig.DisplayName);
            Assert.Equal("darksouls3.exe", darkSoulsConfig.ExecutableName);
            
            // CSV 读取应按 DataKey 正确分组
            Assert.Equal(2, games.Count);
            Assert.Contains(games, g => g.GameName == "elden_ring");
            Assert.Contains(games, g => g.GameName == "dark_souls_3");
        }

        [Fact]
        public void ReadFromCsv_WithConfig_FallbackToDataKey()
        {
            // Given: 配置文件包含 DataKey 但没有 DisplayName
            var appConfig = new AppConfig
            {
                Games = new List<GameConfig>
                {
                    new() { DataKey = "elden_ring", ExecutableName = "eldenring.exe", IsEnabled = true }
                }
            };
            
            var provider = new YamlConfigProvider(_configFile);
            provider.SaveAppConfig(appConfig);
            
            var csvContent = new StringBuilder();
            csvContent.AppendLine("game,start_time,end_time,duration_minutes");
            csvContent.AppendLine("elden_ring,2025-11-01T10:00:00,2025-11-01T12:00:00,120");
            
            File.WriteAllText(_csvFile, csvContent.ToString());
            
            // When: 读取配置
            var configs = provider.Load();
            
            // Then: DisplayName 应为 null，需要回退到 DataKey
            Assert.Single(configs);
            var eldenConfig = configs.Values.First();
            Assert.Null(eldenConfig.DisplayName);
            Assert.Equal("elden_ring", eldenConfig.DataKey);
            Assert.Equal("eldenring.exe", eldenConfig.ExecutableName);
        }

        [Fact]
        public void ReadFromCsv_OrphanedRecords_ShouldShowOriginalValue()
        {
            // Given: CSV 包含配置中不存在的游戏（孤立记录）
            var appConfig = new AppConfig
            {
                Games = new List<GameConfig>
                {
                    new() { DataKey = "elden_ring", ExecutableName = "eldenring.exe", DisplayName = "Elden Ring", IsEnabled = true }
                }
            };
            
            var provider = new YamlConfigProvider(_configFile);
            provider.SaveAppConfig(appConfig);
            
            var csvContent = new StringBuilder();
            csvContent.AppendLine("game,start_time,end_time,duration_minutes");
            csvContent.AppendLine("elden_ring,2025-11-01T10:00:00,2025-11-01T12:00:00,120");
            csvContent.AppendLine("unknown_game,2025-11-02T14:00:00,2025-11-02T15:30:00,90");
            
            File.WriteAllText(_csvFile, csvContent.ToString());
            
            // When: 读取 CSV
            var games = PlaytimeDataReader.ReadFromCsv(_csvFile);
            var configs = provider.Load();
            
            // Then: 
            // - CSV 应包含两个游戏
            Assert.Equal(2, games.Count);
            
            // - "unknown_game" 不在配置中（配置只有一个条目）
            Assert.Single(configs);
            Assert.DoesNotContain(configs.Values, c => c.DataKey == "unknown_game");
            
            // - CSV 读取器应正常读取，使用原始值
            var orphan = games.First(g => g.GameName == "unknown_game");
            Assert.Equal("unknown_game", orphan.GameName);
            Assert.Single(orphan.Sessions);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dir))
                {
                    Directory.Delete(_dir, true);
                }
            }
            catch
            {
                // ignore
            }
        }
    }
}
