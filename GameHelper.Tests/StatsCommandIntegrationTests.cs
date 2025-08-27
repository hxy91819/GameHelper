using System;
using System.IO;
using System.Text.Json;
using GameHelper.Infrastructure.Providers;
using Xunit;

namespace GameHelper.Tests
{
    public class StatsCommandIntegrationTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _csvFile;
        private readonly string _jsonFile;

        public StatsCommandIntegrationTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "GameHelperTests_StatsIntegration", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _csvFile = Path.Combine(_dir, "playtime.csv");
            _jsonFile = Path.Combine(_dir, "playtime.json");
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