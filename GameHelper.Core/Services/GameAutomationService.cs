using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameHelper.Core.Services
{
    /// <summary>
    /// Coordinates process monitoring, playtime tracking, and HDR toggling based on enabled games.
    /// Lifecycle: call <see cref="Start"/> to subscribe to monitor events and load config,
    /// and <see cref="Stop"/> to unsubscribe and flush any active sessions.
    /// </summary>
    public sealed class GameAutomationService : IGameAutomationService
    {
        private readonly IProcessMonitor _monitor;
        private readonly IStopEventsControl? _stopControl; // optional capability
        private readonly IConfigProvider _configProvider;
        private readonly IHdrController _hdr;
        private readonly IPlayTimeService _playTime;
        private readonly ILogger<GameAutomationService> _logger;

        private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyDictionary<string, GameConfig> _configs = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);

        public GameAutomationService(
            IProcessMonitor monitor,
            IConfigProvider configProvider,
            IHdrController hdr,
            IPlayTimeService playTime,
            ILogger<GameAutomationService> logger)
        {
            _monitor = monitor;
            _stopControl = monitor as IStopEventsControl;
            _configProvider = configProvider;
            _hdr = hdr;
            _playTime = playTime;
            _logger = logger;
        }

        /// <summary>
        /// Loads configuration and subscribes to process monitor events. Idempotent.
        /// </summary>
        public void Start()
        {
            _configs = _configProvider.Load();
            _monitor.ProcessStarted += OnProcessStarted;
            _monitor.ProcessStopped += OnProcessStopped;
            // Optimization: if supported, do not listen to Stop events until we have the first active game
            try { _stopControl?.SetStopEventsEnabled(false); _logger.LogDebug("Stop events listening disabled at startup"); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to disable stop events at startup"); }
            _logger.LogInformation("GameAutomationService started with {Count} configs", _configs.Count);
        }

        /// <summary>
        /// Unsubscribes from monitor events and flushes any active play sessions to storage
        /// to prevent data loss when the host shuts down.
        /// </summary>
        public void Stop()
        {
            _monitor.ProcessStarted -= OnProcessStarted;
            _monitor.ProcessStopped -= OnProcessStopped;
            // Flush any active sessions to ensure playtime is persisted when the host shuts down
            if (_active.Count > 0)
            {
                foreach (var name in _active.ToArray())
                {
                    try
                    {
                        _logger.LogInformation("Flushing active session on stop: {Process}", name);
                        _playTime.StopTracking(name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to flush session for {Process}", name);
                    }
                }
                _active.Clear();
            }
            _logger.LogInformation("GameAutomationService stopped");
        }

        /// <summary>
        /// Handler for process start events. Starts a playtime session and enables HDR if this is the first active game.
        /// </summary>
        private void OnProcessStarted(string processName)
        {
            var key = Normalize(processName);
            if (!IsEnabled(key))
            {
                // Try a config-driven fuzzy fallback: map incoming name by stem to a single enabled config key
                var stemKey = Stem(key);
                var candidates = _configs
                    .Where(kvp => kvp.Value.IsEnabled)
                    .Select(kvp => kvp.Key)
                    .Where(cfgKey => {
                        var s = Stem(cfgKey);
                        return s == stemKey || s.StartsWith(stemKey, StringComparison.Ordinal) || stemKey.StartsWith(s, StringComparison.Ordinal);
                    })
                    .ToList();

                if (candidates.Count == 1)
                {
                    var mapped = candidates[0];
                    _logger.LogInformation("Start fallback matched by fuzzy stem: {Stem} -> {Process}", stemKey, mapped);
                    key = mapped; // continue using canonical config key
                }
                else
                {
                    if (candidates.Count > 1)
                    {
                        _logger.LogInformation("Start fallback ambiguous, multiple matches for stem {Stem}: {Candidates}", stemKey, string.Join(", ", candidates));
                    }
                    else
                    {
                        _logger.LogDebug("Ignoring start for not-enabled process: {Process}", key);
                    }
                    return;
                }
            }

            var wasEmpty = _active.Count == 0;
            _active.Add(key);
            _logger.LogDebug("Active count after start: {Count}", _active.Count);

            _logger.LogInformation("Process started: {Process}", key);
            try
            {
                _playTime.StartTracking(key);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start tracking for {Process}", key);
            }

            if (wasEmpty && _active.Count == 1)
            {
                _logger.LogInformation("First active game detected, enabling HDR");
                _hdr.Enable();
                // Enable Stop events now that we have at least one active game
                try { _stopControl?.SetStopEventsEnabled(true); _logger.LogDebug("Stop events enabled (first active)"); }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to enable stop events"); }
            }
        }

        /// <summary>
        /// Handler for process stop events. Stops a playtime session and disables HDR if it was the last active game.
        /// Includes fallbacks to handle minor name variations from OS events.
        /// </summary>
        private void OnProcessStopped(string processName)
        {
            var key = Normalize(processName);
            // Log raw stop events only at Debug to reduce noise for unrelated processes
            _logger.LogDebug("Stop event received: {Process}", key);

            var removed = _active.Remove(key);
            if (!removed)
            {
                // Fallback: fuzzy stem match to handle minor truncations/variations
                var stemKey = Stem(key);
                var candidates = _active
                    .Where(a => {
                        var sa = Stem(a);
                        return sa == stemKey || sa.StartsWith(stemKey, StringComparison.Ordinal) || stemKey.StartsWith(sa, StringComparison.Ordinal);
                    })
                    .ToList();

                if (candidates.Count == 1)
                {
                    var candidate = candidates[0];
                    _logger.LogInformation("Stop fallback matched by fuzzy stem: {Stem} -> {Process}", stemKey, candidate);
                    _active.Remove(candidate);
                    key = candidate; // continue using the active canonical key
                }
                else
                {
                    if (candidates.Count > 1)
                    {
                        _logger.LogInformation("Stop fallback ambiguous, multiple matches for stem {Stem}: {Candidates}", stemKey, string.Join(", ", candidates));
                    }
                    return;
                }
            }

            _logger.LogInformation("Process stopped: {Process}", key);

            PlaySession? session = null;
            try
            {
                session = _playTime.StopTracking(key);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop tracking for {Process}", key);
            }

            if (session is not null)
            {
                var formatted = FormatDuration(session.Duration);
                _logger.LogInformation(
                    "本次游玩时长：{Duration}（开始 {StartTime:t}，结束 {EndTime:t}）",
                    formatted,
                    session.StartTime,
                    session.EndTime);
            }

            _logger.LogDebug("Active count after stop: {Count}", _active.Count);

            if (_active.Count == 0)
            {
                _logger.LogInformation("Last active game exited, disabling HDR");
                _hdr.Disable();
                // No active games -> disable Stop events to minimize idle overhead
                try { _stopControl?.SetStopEventsEnabled(false); _logger.LogDebug("Stop events disabled (no active games)"); }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to disable stop events"); }
            }
        }

        /// <summary>
        /// Returns whether a process (by executable name) is enabled in the loaded config.
        /// Normalization ensures consistent matching against keys.
        /// </summary>
        private bool IsEnabled(string processName)
        {
            var key = Normalize(processName);
            if (string.IsNullOrWhiteSpace(key)) return false;
            if (_configs.TryGetValue(key, out var cfg))
            {
                return cfg.IsEnabled;
            }
            return false;
        }

        /// <summary>
        /// Normalizes process names to a canonical key: keep only file name and ensure ".exe" suffix.
        /// This reduces mismatch between start/stop events and configuration keys.
        /// </summary>
        private static string Normalize(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) return processName;
            // keep filename only
            var name = Path.GetFileName(processName.Trim());
            // ensure .exe suffix for consistency across WMI Start/Stop differences
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name += ".exe";
            }
            return name;
        }

        /// <summary>
        /// Produces a fuzzy stem used for tolerant comparisons: alphanumeric-only, lower-cased,
        /// without extension and punctuation. Helps handle minor truncations/variations.
        /// </summary>
        private static string Stem(string processName)
        {
            var n = Normalize(processName);
            var stem = Path.GetFileNameWithoutExtension(n);
            // alphanumeric-only, lower invariant to make fuzzy comparisons more robust (ignore spaces, dots, hyphens)
            var filtered = new string(stem.Where(char.IsLetterOrDigit).ToArray());
            return filtered.ToLowerInvariant();
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
            {
                duration = TimeSpan.Zero;
            }

            if (duration == TimeSpan.Zero)
            {
                return "0秒";
            }

            var parts = new List<string>();
            if (duration.Days > 0)
            {
                parts.Add($"{duration.Days}天");
            }

            if (duration.Hours > 0)
            {
                parts.Add($"{duration.Hours}小时");
            }

            if (duration.Minutes > 0)
            {
                parts.Add($"{duration.Minutes}分钟");
            }

            var seconds = duration.Seconds;
            if (seconds > 0 || parts.Count == 0)
            {
                parts.Add($"{seconds}秒");
            }

            return string.Concat(parts);
        }
    }
}
