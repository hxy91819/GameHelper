using System;
using System.Collections.Generic;
using System.Linq;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameHelper.Core.Services
{
    public sealed class GameAutomationService : IGameAutomationService
    {
        private readonly IProcessMonitor _monitor;
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
            _configProvider = configProvider;
            _hdr = hdr;
            _playTime = playTime;
            _logger = logger;
        }

        public void Start()
        {
            _configs = _configProvider.Load();
            _monitor.ProcessStarted += OnProcessStarted;
            _monitor.ProcessStopped += OnProcessStopped;
            _logger.LogInformation("GameAutomationService started with {Count} configs", _configs.Count);
        }

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

        private void OnProcessStarted(string processName)
        {
            if (!IsEnabled(processName))
            {
                _logger.LogDebug("Ignoring start for not-enabled process: {Process}", processName);
                return;
            }

            var wasEmpty = _active.Count == 0;
            _active.Add(processName);

            _logger.LogInformation("Process started: {Process}", processName);
            _playTime.StartTracking(processName);

            if (wasEmpty && _active.Count == 1)
            {
                _logger.LogInformation("First active game detected, enabling HDR");
                _hdr.Enable();
            }
        }

        private void OnProcessStopped(string processName)
        {
            if (!IsEnabled(processName))
            {
                _logger.LogDebug("Ignoring stop for not-enabled process: {Process}", processName);
                return;
            }

            var removed = _active.Remove(processName);
            if (!removed) return;

            _logger.LogInformation("Process stopped: {Process}", processName);
            _playTime.StopTracking(processName);

            if (_active.Count == 0)
            {
                _logger.LogInformation("Last active game exited, disabling HDR");
                _hdr.Disable();
            }
        }

        private bool IsEnabled(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) return false;
            if (_configs.TryGetValue(processName, out var cfg))
            {
                return cfg.IsEnabled;
            }
            return false;
        }
    }
}
