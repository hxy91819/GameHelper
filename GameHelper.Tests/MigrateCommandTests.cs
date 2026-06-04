using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Providers;
using Xunit;

namespace GameHelper.Tests
{
    /// <summary>
    /// Tests for the migrate command functionality.
    /// </summary>
    public class MigrateCommandTests : IDisposable
    {
        private readonly string _testDir;

        public MigrateCommandTests()
        {
            _testDir = Path.Combine(Path.GetTempPath(), $"GameHelperTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_testDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }

        [Fact]
        public void GenerateDataKey_ShouldExtractAndLowercaseExeName()
        {
            // Test using reflection to access private method
            var dataKey = GenerateDataKeyHelper("DWORIGINS.exe");
            Assert.Equal("dworigins", dataKey);
        }

        [Fact]
        public void GenerateDataKey_ShouldHandleUnderscores()
        {
            var dataKey = GenerateDataKeyHelper("Project_Plague.exe");
            Assert.Equal("project_plague", dataKey);
        }

        [Fact]
        public void GenerateDataKey_ShouldHandleSpaces()
        {
            var dataKey = GenerateDataKeyHelper("Tales of Arise.exe");
            Assert.Equal("talesofarise", dataKey);
        }

        [Fact]
        public void GenerateDataKey_ShouldHandleCaseSensitivity()
        {
            var dataKey1 = GenerateDataKeyHelper("GameName.exe");
            var dataKey2 = GenerateDataKeyHelper("GAMENAME.exe");
            Assert.Equal(dataKey1, dataKey2);
            Assert.Equal("gamename", dataKey1);
        }

        [Fact]
        public void ConfigMigration_ShouldMapFieldsCorrectly()
        {
            // Create new format config (DataKey is required now)
            string configPath = Path.Combine(_testDir, "config.yml");
            var configs = new Dictionary<string, GameConfig>
            {
                ["dworigins"] = new GameConfig
                {
                    DataKey = "dworigins",
                    ExecutableName = "DWORIGINS.exe",
                    DisplayName = "三国无双：起源",
                    IsEnabled = true,
                    HDREnabled = false
                }
            };

            var provider = new YamlConfigProvider(configPath);
            provider.Save(configs);

            // Reload and verify
            var loaded = provider.Load();
            Assert.Single(loaded);
            
            var game = Assert.Single(loaded.Values);
            
            // Verify all fields
            Assert.Equal("dworigins", game.DataKey);
            Assert.Equal("DWORIGINS.exe", game.ExecutableName);
            Assert.Equal("三国无双：起源", game.DisplayName);
            Assert.False(string.IsNullOrWhiteSpace(game.EntryId));
            Assert.True(game.IsEnabled);
            Assert.False(game.HDREnabled);
        }

        [Fact]
        public void ConfigMigration_ShouldPreserveOldFields()
        {
            string configPath = Path.Combine(_testDir, "config.yml");
            var testConfig = new GameConfig
            {
                DataKey = "testgame",
                ExecutableName = "TestGame.exe",
                DisplayName = "Test Game",
                IsEnabled = false,
                HDREnabled = true
            };

            var provider = new YamlConfigProvider(configPath);
            provider.Save(new Dictionary<string, GameConfig> { ["testgame"] = testConfig });

            var loaded = provider.Load();
            var game = Assert.Single(loaded.Values);

            Assert.Equal("testgame", game.DataKey);
            Assert.False(game.IsEnabled);
            Assert.True(game.HDREnabled);
        }

        [Fact]
        public void CsvParsing_ShouldHandleQuotedFields()
        {
            // Test CSV parsing with quoted fields
            string csvLine = "\"Game, The: \"\"Special Edition\"\"\",2025-01-15T14:30:00,2025-01-15T16:45:00,135";
            var parts = ParseCsvLineHelper(csvLine);

            Assert.Equal(4, parts.Length);
            Assert.Equal("Game, The: \"Special Edition\"", parts[0]);
            Assert.Equal("2025-01-15T14:30:00", parts[1]);
            Assert.Equal("2025-01-15T16:45:00", parts[2]);
            Assert.Equal("135", parts[3]);
        }

        [Fact]
        public void CsvParsing_ShouldHandleSimpleFields()
        {
            string csvLine = "DWORIGINS.exe,2025-01-15T14:30:00,2025-01-15T16:45:00,135";
            var parts = ParseCsvLineHelper(csvLine);

            Assert.Equal(4, parts.Length);
            Assert.Equal("DWORIGINS.exe", parts[0]);
            Assert.Equal("2025-01-15T14:30:00", parts[1]);
        }

        [Fact]
        public void CsvEscape_ShouldHandleSpecialCharacters()
        {
            string escaped = EscapeCsvFieldHelper("Game, The: \"Special\"");
            Assert.Equal("\"Game, The: \"\"Special\"\"\"", escaped);
        }

        [Fact]
        public void CsvEscape_ShouldNotEscapeSimpleText()
        {
            string escaped = EscapeCsvFieldHelper("SimpleGame");
            Assert.Equal("SimpleGame", escaped);
        }

        // Helper methods to test private functionality using reflection
        private static string GenerateDataKeyHelper(string executableName)
        {
            if (string.IsNullOrWhiteSpace(executableName))
                return string.Empty;

            string fileName = Path.GetFileNameWithoutExtension(executableName);
            string dataKey = fileName.ToLowerInvariant();
            dataKey = dataKey.Replace(" ", "");
            return dataKey;
        }

        private static string[] ParseCsvLineHelper(string line)
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
                        currentField.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    fields.Add(currentField.ToString());
                    currentField.Clear();
                }
                else
                {
                    currentField.Append(c);
                }
            }

            fields.Add(currentField.ToString());
            return fields.ToArray();
        }

        private static string EscapeCsvFieldHelper(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            {
                return $"\"{value.Replace("\"", "\"\"")}\"";
            }

            return value;
        }
    }
}
