using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Infrastructure.Providers
{
    /// <summary>
    /// File-backed play time tracker compatible with GamePlayTimeManager's playtime.json format.
    /// Writes to %AppData%/GameHelper/playtime.json by default.
    /// </summary>
    public sealed class FileBackedPlayTimeService : IPlayTimeService
    {
        private readonly object _gate = new();
        private readonly string _filePath;

        private readonly Dictionary<string, GamePlayTimeRecord> _playTimes;
        private readonly Dictionary<string, DateTime> _starts;

        public FileBackedPlayTimeService()
            : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                 "GameHelper"))
        {
        }

        // For tests: pass a directory
        public FileBackedPlayTimeService(string configDirectory)
        {
            if (string.IsNullOrWhiteSpace(configDirectory))
                throw new ArgumentException("configDirectory required", nameof(configDirectory));

            Directory.CreateDirectory(configDirectory);
            _filePath = Path.Combine(configDirectory, "playtime.json");

            _starts = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            _playTimes = new Dictionary<string, GamePlayTimeRecord>(StringComparer.OrdinalIgnoreCase);

            Load();
        }

        public void StartTracking(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName)) return;
            lock (_gate)
            {
                if (_starts.ContainsKey(gameName)) return; // already tracking
                _starts[gameName] = DateTime.Now;
                if (!_playTimes.ContainsKey(gameName))
                {
                    _playTimes[gameName] = new GamePlayTimeRecord { GameName = gameName };
                }
            }
        }

        public PlaySession? StopTracking(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName)) return null;

            lock (_gate)
            {
                if (!_starts.TryGetValue(gameName, out var start)) return null; // not tracking

                var end = DateTime.Now;
                var duration = end - start;
                if (duration < TimeSpan.Zero)
                {
                    duration = TimeSpan.Zero;
                }

                var minutes = (long)duration.TotalMinutes;
                if (!_playTimes.TryGetValue(gameName, out var rec))
                {
                    rec = new GamePlayTimeRecord { GameName = gameName };
                    _playTimes[gameName] = rec;
                }

                var minutesClamped = minutes < 0 ? 0 : minutes;
                rec.Sessions.Add(new GameSessionRecord
                {
                    StartTime = start,
                    EndTime = end,
                    DurationMinutes = minutesClamped
                });
                _starts.Remove(gameName);
                Save();

                return new PlaySession(gameName, start, end, duration, minutesClamped);
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_filePath)) return;
                var json = File.ReadAllText(_filePath);
                var root = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                if (root != null && root.TryGetValue("games", out var node) && node != null)
                {
                    var items = JsonSerializer.Deserialize<GamePlayTimeRecord[]>(node.ToString() ?? string.Empty);
                    if (items != null)
                    {
                        foreach (var it in items)
                        {
                            if (!string.IsNullOrWhiteSpace(it.GameName))
                            {
                                // Ensure non-null list
                                it.Sessions ??= new List<GameSessionRecord>();
                                _playTimes[it.GameName] = it;
                            }
                        }
                    }
                }
            }
            catch
            {
                // ignore malformed file; start fresh in memory
            }
        }

        private void Save()
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var payload = new Dictionary<string, object>
            {
                ["games"] = _playTimes.Values
                    .OrderBy(v => v.GameName, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }

        // Internal DTOs matching UI JSON format
        private sealed class GamePlayTimeRecord
        {
            public string GameName { get; set; } = string.Empty;
            public List<GameSessionRecord> Sessions { get; set; } = new();
        }

        private sealed class GameSessionRecord
        {
            public DateTime StartTime { get; set; }
            public DateTime EndTime { get; set; }
            public long DurationMinutes { get; set; }
        }
    }
}
