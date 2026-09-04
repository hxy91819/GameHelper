using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using Microsoft.Extensions.Logging;
using System.Text;

namespace GameHelper.Infrastructure.Providers
{
    /// <summary>
    /// CSV-backed play time tracker that appends sessions to playtime.csv.
    /// Automatically migrates from existing playtime.json on first run.
    /// </summary>
    public sealed class CsvBackedPlayTimeService : IPlayTimeService
    {
        private readonly object _gate = new();
        private readonly object _csvWriteGate = new();
        private readonly string _csvFilePath;
        private readonly string _jsonFilePath;
        private readonly ILogger<CsvBackedPlayTimeService>? _logger;

        private readonly Dictionary<string, DateTime> _activeSessions;

        public CsvBackedPlayTimeService(ILogger<CsvBackedPlayTimeService>? logger = null)
            : this(AppDataPath.GetGameHelperDirectory(), logger)
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

        public PlaySession? StopTracking(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName)) return null;

            PlaySession session;
            lock (_gate)
            {
                if (!_activeSessions.TryGetValue(gameName, out var startTime)) return null; // not tracking

                var endTime = DateTime.Now;
                var duration = endTime - startTime;
                if (duration < TimeSpan.Zero)
                {
                    duration = TimeSpan.Zero;
                }

                var durationMinutes = (long)duration.TotalMinutes;
                if (durationMinutes < 0) durationMinutes = 0;

                _activeSessions.Remove(gameName);
                session = new PlaySession(gameName, startTime, endTime, duration, durationMinutes);
            }

            try
            {
                AppendSessionToCsv(session.GameName, session.StartTime, session.EndTime, session.DurationMinutes);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to write session to CSV for game {GameName}", gameName);
                // Continue execution - the in-memory tracking state has already ended.
            }

            return session;
        }

        private void AppendSessionToCsv(string gameName, DateTime startTime, DateTime endTime, long durationMinutes)
        {
            const int maxRetries = 3;
            const int retryDelayMs = 100;

            for (var attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    lock (_csvWriteGate)
                    {
                        PlaytimeCsvCodec.Append(
                            _csvFilePath,
                            new PlaytimeCsvRow(gameName, startTime, endTime, durationMinutes));
                    }

                    return;
                }
                catch (Exception ex) when (attempt < maxRetries - 1)
                {
                    _logger?.LogWarning(ex, "Failed to write CSV (attempt {Attempt}/{MaxRetries}), retrying...", attempt + 1, maxRetries);
                    System.Threading.Thread.Sleep(retryDelayMs);
                }
            }

            throw new InvalidOperationException($"Failed to write session to CSV after {maxRetries} attempts");
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

                var json = File.ReadAllText(_jsonFilePath, Encoding.UTF8);
                var root = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

                if (root != null && root.TryGetValue("games", out var node) && node != null)
                {
                    var games = JsonSerializer.Deserialize<JsonGameRecord[]>(node.ToString() ?? string.Empty);
                    if (games != null)
                    {
                        var rows = games.SelectMany(game =>
                            (game.Sessions ?? Enumerable.Empty<JsonSessionRecord>())
                                .Select(session => new PlaytimeCsvRow(
                                    game.GameName,
                                    session.StartTime,
                                    session.EndTime,
                                    session.DurationMinutes)));
                        PlaytimeCsvCodec.WriteAll(_csvFilePath, rows);

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
