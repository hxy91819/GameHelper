using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using GameHelper.Infrastructure.Providers;
using Xunit;

namespace GameHelper.Tests
{
    public class CsvBackedPlayTimeServiceTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _csvFile;
        private readonly string _jsonFile;

        public CsvBackedPlayTimeServiceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "GameHelperTests_CsvPlaytime", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _csvFile = Path.Combine(_dir, "playtime.csv");
            _jsonFile = Path.Combine(_dir, "playtime.json");
        }

        [Fact]
        public void StartStop_CreatesFile_WithHeaderAndSingleSession()
        {
            var svc = new CsvBackedPlayTimeService(_dir);
            var name = "witcher3.exe";

            svc.StartTracking(name);
            var session = svc.StopTracking(name);

            Assert.NotNull(session);
            Assert.Equal(name, session!.GameName, StringComparer.OrdinalIgnoreCase);
            Assert.True(session.Duration >= TimeSpan.Zero);

            Assert.True(File.Exists(_csvFile));
            var lines = File.ReadAllLines(_csvFile);
            
            Assert.True(lines.Length >= 2); // header + at least one session
            Assert.Equal("game,start_time,end_time,duration_minutes", lines[0]);
            Assert.StartsWith("witcher3.exe,", lines[1]);
        }

        [Fact]
        public void CaseInsensitive_GameName_CreatesMultipleSessions()
        {
            var svc = new CsvBackedPlayTimeService(_dir);
            
            svc.StartTracking("RDR2.EXE");
            svc.StopTracking("RDR2.EXE");

            svc.StartTracking("rdr2.exe");
            svc.StopTracking("rdr2.exe");

            var lines = File.ReadAllLines(_csvFile);
            var sessionLines = lines.Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            
            Assert.Equal(2, sessionLines.Length);
            Assert.All(sessionLines, line => Assert.Contains("rdr2.exe", line, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void MultipleStartStop_AppendsToExistingFile()
        {
            var svc = new CsvBackedPlayTimeService(_dir);
            
            svc.StartTracking("game1.exe");
            svc.StopTracking("game1.exe");
            
            svc.StartTracking("game2.exe");
            svc.StopTracking("game2.exe");
            
            svc.StartTracking("game1.exe");
            svc.StopTracking("game1.exe");

            var lines = File.ReadAllLines(_csvFile);
            var sessionLines = lines.Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            
            Assert.Equal(3, sessionLines.Length);
            Assert.Equal(2, sessionLines.Count(l => l.Contains("game1.exe")));
            Assert.Equal(1, sessionLines.Count(l => l.Contains("game2.exe")));
        }

        [Fact]
        public void GameNameWithComma_IsProperlyEscaped()
        {
            var svc = new CsvBackedPlayTimeService(_dir);
            var gameName = "My Game, Version 2.exe";
            
            svc.StartTracking(gameName);
            svc.StopTracking(gameName);

            var lines = File.ReadAllLines(_csvFile);
            var sessionLine = lines[1];
            
            Assert.StartsWith("\"My Game, Version 2.exe\",", sessionLine);
        }

        [Fact]
        public void GameNameWithQuotes_IsProperlyEscaped()
        {
            var svc = new CsvBackedPlayTimeService(_dir);
            var gameName = "Game \"Ultimate\" Edition.exe";
            
            svc.StartTracking(gameName);
            svc.StopTracking(gameName);

            var lines = File.ReadAllLines(_csvFile);
            var sessionLine = lines[1];
            
            Assert.StartsWith("\"Game \"\"Ultimate\"\" Edition.exe\",", sessionLine);
        }

        [Fact]
        public void JsonExists_CsvDoesNot_PerformsMigration()
        {
            // Create a JSON file with sample data
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
                            },
                            new
                            {
                                StartTime = new DateTime(2025, 8, 17, 14, 0, 0),
                                EndTime = new DateTime(2025, 8, 17, 15, 30, 0),
                                DurationMinutes = 90L
                            }
                        }
                    },
                    new
                    {
                        GameName = "rdr2.exe",
                        Sessions = new[]
                        {
                            new
                            {
                                StartTime = new DateTime(2025, 8, 18, 20, 0, 0),
                                EndTime = new DateTime(2025, 8, 18, 22, 15, 0),
                                DurationMinutes = 135L
                            }
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(jsonData, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_jsonFile, json);

            // Create service - should trigger migration
            var svc = new CsvBackedPlayTimeService(_dir);

            Assert.True(File.Exists(_csvFile));
            var lines = File.ReadAllLines(_csvFile);
            
            Assert.Equal("game,start_time,end_time,duration_minutes", lines[0]);
            
            var sessionLines = lines.Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            Assert.Equal(3, sessionLines.Length); // 2 witcher3 + 1 rdr2
            
            Assert.Equal(2, sessionLines.Count(l => l.Contains("witcher3.exe")));
            Assert.Equal(1, sessionLines.Count(l => l.Contains("rdr2.exe")));
            
            // Verify specific session data
            var witcher3Line1 = sessionLines.First(l => l.Contains("witcher3.exe") && l.Contains("2025-08-16T10:00:00"));
            Assert.Contains("2025-08-16T11:40:00,100", witcher3Line1);
        }

        [Fact]
        public void CsvExists_JsonExists_DoesNotMigrate()
        {
            // Create both files
            File.WriteAllText(_csvFile, "game,start_time,end_time,duration_minutes\ntest.exe,2025-08-16T10:00:00,2025-08-16T11:00:00,60\n");
            File.WriteAllText(_jsonFile, "{\"games\":[]}");

            var originalCsvContent = File.ReadAllText(_csvFile);
            
            // Create service - should NOT trigger migration
            var svc = new CsvBackedPlayTimeService(_dir);

            var currentCsvContent = File.ReadAllText(_csvFile);
            Assert.Equal(originalCsvContent, currentCsvContent);
        }

        [Fact]
        public void MalformedJson_DoesNotThrow_ContinuesWithEmptyCsv()
        {
            File.WriteAllText(_jsonFile, "not-json");
            
            var svc = new CsvBackedPlayTimeService(_dir);
            
            svc.StartTracking("test.exe");
            svc.StopTracking("test.exe");

            Assert.True(File.Exists(_csvFile));
            var lines = File.ReadAllLines(_csvFile);
            Assert.Equal("game,start_time,end_time,duration_minutes", lines[0]);
            Assert.StartsWith("test.exe,", lines[1]);
        }

        [Fact]
        public void RepeatedStartTracking_IsIdempotent()
        {
            var svc = new CsvBackedPlayTimeService(_dir);
            
            svc.StartTracking("game.exe");
            svc.StartTracking("game.exe"); // Should be ignored
            svc.StartTracking("game.exe"); // Should be ignored
            svc.StopTracking("game.exe");

            var lines = File.ReadAllLines(_csvFile);
            var sessionLines = lines.Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            
            Assert.Equal(1, sessionLines.Length); // Only one session should be recorded
        }

        [Fact]
        public void StopTrackingWithoutStart_DoesNothing()
        {
            var svc = new CsvBackedPlayTimeService(_dir);
            
            var session = svc.StopTracking("nonexistent.exe");

            Assert.Null(session);

            Assert.False(File.Exists(_csvFile)); // No file should be created
        }

        // Story 1.4: Tests for DataKey usage
        [Fact]
        public void StopTracking_ShouldWriteDataKeyToCsv()
        {
            // Given: 使用 DataKey "elden_ring" 开始跟踪
            var svc = new CsvBackedPlayTimeService(_dir);
            var dataKey = "elden_ring";
            
            svc.StartTracking(dataKey);
            
            // When: 停止跟踪
            var session = svc.StopTracking(dataKey);
            
            // Then: 
            // - Session 应该返回并包含正确的 DataKey
            Assert.NotNull(session);
            Assert.Equal(dataKey, session!.GameName);
            
            // - CSV 文件应包含 "elden_ring" 作为 game 列的值
            Assert.True(File.Exists(_csvFile));
            var csvContent = File.ReadAllText(_csvFile);
            Assert.Contains("elden_ring", csvContent);
            
            var lines = File.ReadAllLines(_csvFile);
            Assert.True(lines.Length >= 2);
            Assert.StartsWith("elden_ring,", lines[1]);
        }

        [Fact]
        public void StopTracking_ShouldEscapeSpecialCharactersInDataKey()
        {
            // Given: DataKey 包含逗号和引号
            var dataKey = "Game, The: \"Special Edition\"";
            var svc = new CsvBackedPlayTimeService(_dir);
            
            svc.StartTracking(dataKey);
            
            // When: 停止跟踪
            var session = svc.StopTracking(dataKey);
            
            // Then: CSV 应正确转义逗号和引号
            Assert.NotNull(session);
            
            var lines = File.ReadAllLines(_csvFile);
            var sessionLine = lines[1];
            
            // Should be wrapped in quotes and internal quotes doubled
            Assert.StartsWith("\"Game, The: \"\"Special Edition\"\"\",", sessionLine);
        }

        [Fact]
        public void MultipleGamesWithDataKeys_ShouldWriteCorrectly()
        {
            // Given: 多个游戏使用 DataKey 格式
            var svc = new CsvBackedPlayTimeService(_dir);
            
            // When: 记录多个游戏会话
            svc.StartTracking("elden_ring");
            svc.StopTracking("elden_ring");
            
            svc.StartTracking("dark_souls_3");
            svc.StopTracking("dark_souls_3");
            
            svc.StartTracking("elden_ring");
            svc.StopTracking("elden_ring");
            
            // Then: CSV 应包含正确的 DataKey 记录
            var lines = File.ReadAllLines(_csvFile);
            var sessionLines = lines.Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            
            Assert.Equal(3, sessionLines.Length);
            Assert.Equal(2, sessionLines.Count(l => l.StartsWith("elden_ring,")));
            Assert.Equal(1, sessionLines.Count(l => l.StartsWith("dark_souls_3,")));
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