using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using GameHelper.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace GameHelper.Infrastructure.Providers
{
    /// <summary>
    /// CSV-backed play time tracker that appends sessions to playtime.csv.
    /// Automatically migrates from existing playtime.json on first run.
    /// </summary>
    public sealed class CsvBackedPlayTimeService : IPlayTimeService
    {
        private readonly object _gate = new();
        private readonly string _csvFilePath;
        private readonly string _jsonFilePath;
        private readonly ILogger<CsvBackedPlayTimeService>? _logger;

        private readonly Dictionary<string, DateTime> _activeSessions;

        public CsvBackedPlayTimeService(ILogger<CsvBackedPlayTimeService>? logger = null)
            : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                 "GameHelper"), logger)
        {
        }

        // For tests: pass a directory
        public CsvBackedPlayTimeService(string configDirectory, ILogger<CsvBackedPlayTimeService>? logger = null)
        {
            if (string.IsNullOrWhiteSpace(configDirectory))
                throw new ArgumentException("configDirectory required", nameof(configDirectory));

            try
            {
                Directory.CreateDirectory(configDirectory);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to create config directory: {configDirectory}", ex);
            }

            _csvFilePath = Path.Combine(configDirectory, "playtime.csv");
            _jsonFilePath = Path.Combine(configDirectory, "playtime.json");
            _logger = logger;

            _activeSessions = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            // Perform one-time migration from JSON to CSV if needed
            try
            {
                MigrateFromJsonIfNeeded();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed during JSON to CSV migration, continuing with empty CSV");
                // Don't throw - we can continue with an empty CSV
            }
        }

        public void StartTracking(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName)) return;
            
            lock (_gate)
            {
                if (_activeSessions.ContainsKey(gameName)) return; // already tracking
                _activeSessions[gameName] = DateTime.Now;
            }
        }

        public void StopTracking(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName)) return;
            
            lock (_gate)
            {
                if (!_activeSessions.TryGetValue(gameName, out var startTime)) return; // not tracking
                
                var endTime = DateTime.Now;
                var durationMinutes = (long)(endTime - startTime).TotalMinutes;
                if (durationMinutes < 0) durationMinutes = 0;

                _activeSessions.Remove(gameName);
                
                try
                {
                    AppendSessionToCsv(gameName, startTime, endTime, durationMinutes);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to write session to CSV for game {GameName}", gameName);
                    // Continue execution - we'll retry on next StopTracking
                }
            }
        }

        private void AppendSessionToCsv(string gameName, DateTime startTime, DateTime endTime, long durationMinutes)
        {
            const int maxRetries = 3;
            const int retryDelayMs = 100;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    // Ensure CSV file exists with header
                    if (!File.Exists(_csvFilePath))
                    {
                        var dir = Path.GetDirectoryName(_csvFilePath);
                        if (!string.IsNullOrEmpty(dir)) 
                        {
                            Directory.CreateDirectory(dir);
                        }
                        
                        // Use atomic write for header creation
                        var tempFile = _csvFilePath + ".tmp";
                        File.WriteAllText(tempFile, "game,start_time,end_time,duration_minutes\n", Encoding.UTF8);
                        File.Move(tempFile, _csvFilePath);
                    }

                    // Format the CSV line
                    var startTimeStr = startTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                    var endTimeStr = endTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                    var csvLine = $"{EscapeCsvField(gameName)},{startTimeStr},{endTimeStr},{durationMinutes}\n";

                    // Append to file with proper file sharing and ensure data is flushed
                    using var stream = new FileStream(_csvFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                    using var writer = new StreamWriter(stream, Encoding.UTF8);
                    writer.Write(csvLine);
                    writer.Flush(); // Ensure data is written to disk immediately
                    stream.Flush(); // Ensure OS buffers are flushed
                    
                    // Success - exit retry loop
                    return;
                }
                catch (Exception ex) when (attempt < maxRetries - 1)
                {
                    _logger?.LogWarning(ex, "Failed to write CSV (attempt {Attempt}/{MaxRetries}), retrying...", attempt + 1, maxRetries);
                    System.Threading.Thread.Sleep(retryDelayMs);
                }
            }
            
            // If we get here, all retries failed - this will be caught by the caller
            throw new InvalidOperationException($"Failed to write session to CSV after {maxRetries} attempts");
        }

        private static string EscapeCsvField(string field)
        {
            if (string.IsNullOrEmpty(field)) return field;
            
            // If field contains comma, quote, or newline, wrap in quotes and escape internal quotes
            if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
            {
                return $"\"{field.Replace("\"", "\"\"")}\"";
            }
            
            return field;
        }

        private void MigrateFromJsonIfNeeded()
        {
            // Only migrate if CSV doesn't exist but JSON does
            if (File.Exists(_csvFilePath) || !File.Exists(_jsonFilePath))
            {
                return;
            }

            try
            {
                _logger?.LogInformation("Migrating playtime data from JSON to CSV format");
                
                var json = File.ReadAllText(_jsonFilePath);
                var root = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                
                if (root != null && root.TryGetValue("games", out var node) && node != null)
                {
                    var games = JsonSerializer.Deserialize<JsonGameRecord[]>(node.ToString() ?? string.Empty);
                    if (games != null)
                    {
                        // Create CSV with header
                        var dir = Path.GetDirectoryName(_csvFilePath);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        
                        using var stream = new FileStream(_csvFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
                        using var writer = new StreamWriter(stream, Encoding.UTF8);
                        
                        writer.WriteLine("game,start_time,end_time,duration_minutes");
                        
                        foreach (var game in games)
                        {
                            if (game.Sessions != null)
                            {
                                foreach (var session in game.Sessions)
                                {
                                    var startTimeStr = session.StartTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                                    var endTimeStr = session.EndTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                                    var csvLine = $"{EscapeCsvField(game.GameName)},{startTimeStr},{endTimeStr},{session.DurationMinutes}";
                                    writer.WriteLine(csvLine);
                                }
                            }
                        }
                        
                        _logger?.LogInformation("Successfully migrated {GameCount} games with sessions to CSV", games.Length);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to migrate from JSON to CSV, will continue with empty CSV");
                // If migration fails, we'll start with an empty CSV
            }
        }

        // DTOs for JSON migration
        private sealed class JsonGameRecord
        {
            public string GameName { get; set; } = string.Empty;
            public List<JsonSessionRecord>? Sessions { get; set; }
        }

        private sealed class JsonSessionRecord
        {
            public DateTime StartTime { get; set; }
            public DateTime EndTime { get; set; }
            public long DurationMinutes { get; set; }
        }
    }
}