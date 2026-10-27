using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace GameHelper.Core.Services;

/// <summary>Accumulates foreground play across games and resets only after a continuous break.</summary>
public sealed class BreakReminderService : IBreakReminderService
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
    private readonly object _gate = new();
    private readonly IForegroundActivityProvider _activity;
    private readonly IBreakReminderNotifier _notifier;
    private readonly IProcessPathResolver _pathResolver;
    private readonly TimeProvider _clock;
    private readonly ILogger<BreakReminderService> _logger;
    private ITimer? _timer;
    private BreakReminderSettings _settings = new();
    private GameProcessMatcher? _matcher;
    private (ProcessEventInfo Process, long Started)? _cachedIdentity;
    private bool _cachedIsGame;
    private bool _monitoring;
    private bool _disposed;
    private bool _previousIsGame;
    private bool _previousIsRest;
    private long _lastSample;
    private long _generation;
    private TimeSpan _playtime;
    private TimeSpan _rest;
    private TimeSpan _nextReminder;
    private DateTimeOffset? _pausedUntil;

    public BreakReminderService(IForegroundActivityProvider activity, IBreakReminderNotifier notifier,
        IProcessPathResolver pathResolver, TimeProvider clock, ILogger<BreakReminderService> logger)
    {
        _activity = activity;
        _notifier = notifier;
        _pathResolver = pathResolver;
        _clock = clock;
        _logger = logger;
    }

    public bool IsRunning { get { lock (_gate) return _timer is not null; } }

    public DateTimeOffset? PausedUntil
    {
        get { lock (_gate) return _pausedUntil > _clock.GetUtcNow() ? _pausedUntil : null; }
    }

    public void PauseUntil(DateTimeOffset until)
    {
        lock (_gate)
        {
            if (_disposed || _timer is null) return;
            _pausedUntil = until > _clock.GetUtcNow() ? until : null;
            ClearNotifications();
        }
    }

    public void Resume()
    {
        lock (_gate) _pausedUntil = null;
    }

    public void Start(AppConfig configuration)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_monitoring) return;
            _monitoring = true;
            Configure(configuration);
        }
    }

    public void ReloadConfig(AppConfig configuration)
    {
        lock (_gate)
        {
            if (_disposed) return;
            Configure(configuration);
        }
    }

    public void Snooze(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        lock (_gate)
        {
            if (_disposed || _timer is null) return;
            var target = _playtime + duration;
            if (target > _nextReminder) _nextReminder = target;
            ClearNotifications();
        }
    }

    public void ResetCycle()
    {
        lock (_gate)
        {
            if (_disposed || _timer is null) return;
            Reset();
            _lastSample = _clock.GetTimestamp();
            Sample(_generation);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _monitoring = false;
            _pausedUntil = null;
            StopTimer();
            Reset();
            _matcher = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
        }
    }

    private void Configure(AppConfig configuration)
    {
        var settings = configuration.BreakReminder ?? new BreakReminderSettings();
        var changed = settings != _settings;
        _cachedIdentity = null;
        try
        {
            settings.Validate();
            var games = configuration.Games.Where(game => !string.IsNullOrWhiteSpace(game.DataKey))
                .ToDictionary(game => game.DataKey, StringComparer.OrdinalIgnoreCase);
            _matcher = new GameProcessMatcher(AutomationConfigIndex.Build(games, _logger), _pathResolver, _logger);
            if (changed || !settings.Enabled) { StopTimer(); Reset(); }
            if (!settings.Enabled) _pausedUntil = null;
            _settings = settings;
            if (!_monitoring || !settings.Enabled || _timer is not null) return;
            _nextReminder = TimeSpan.FromMinutes(settings.PlayMinutes);
            _lastSample = _clock.GetTimestamp();
            var generation = ++_generation;
            Sample(generation);
            _timer = _clock.CreateTimer(_ => Sample(generation), null, SampleInterval, SampleInterval);
        }
        catch (Exception ex)
        {
            StopTimer();
            Reset();
            _logger.LogWarning(ex, "Break reminders disabled after configuration/start failure");
        }
    }

    private void Sample(long generation)
    {
        lock (_gate)
        {
            if (!_monitoring || generation != _generation || _disposed) return;
            var now = _clock.GetTimestamp();
            var elapsed = _clock.GetElapsedTime(_lastSample, now);
            _lastSample = now;
            try
            {
                var activity = _activity.Read();
                if (activity.SuspendedFor > TimeSpan.Zero)
                {
                    AddRest(activity.SuspendedFor);
                    elapsed = TimeSpan.Zero;
                }
                if (!activity.IsAvailable)
                {
                    _previousIsGame = false;
                    _previousIsRest = false;
                    _rest = TimeSpan.Zero;
                    _cachedIdentity = null;
                    return;
                }

                var isGame = !activity.IsAway && IsGame(activity);
                // Credit only observed intervals. Long scheduling gaps cannot prove foreground play.
                if (_previousIsGame && elapsed <= SampleInterval * 2) _playtime += elapsed;
                if (_previousIsRest && elapsed <= SampleInterval * 2) AddRest(elapsed);
                if (isGame)
                {
                    _rest = TimeSpan.Zero;
                    if (_playtime >= _nextReminder && !(_pausedUntil > _clock.GetUtcNow()))
                    {
                        _nextReminder = _playtime + TimeSpan.FromMinutes(_settings.RepeatMinutes);
                        try
                        {
                            _notifier.Show(_playtime, _settings.BreakMinutes, _settings.Sound);
                            _logger.LogInformation("Break reminder submitted after {Minutes} foreground-game minutes; suggested break {BreakMinutes} minutes",
                                (long)_playtime.TotalMinutes, _settings.BreakMinutes);
                        }
                        catch (Exception ex) { _logger.LogWarning(ex, "Break notification failed; retrying at next reminder interval"); }
                    }
                }
                _previousIsGame = isGame;
                _previousIsRest = !isGame;
            }
            catch (Exception ex)
            {
                _previousIsGame = false;
                _previousIsRest = false;
                _rest = TimeSpan.Zero;
                _cachedIdentity = null;
                _logger.LogDebug(ex, "Foreground activity unavailable; reminder timing paused");
            }
        }
    }

    private bool IsGame(ForegroundActivity activity)
    {
        if (activity.Process is not { } process) { _cachedIdentity = null; return false; }
        var identity = activity.ProcessStartTicks is { } started ? (process, started) : ((ProcessEventInfo, long)?)null;
        if (identity is not null && identity == _cachedIdentity) return _cachedIsGame;
        _cachedIdentity = identity;
        _cachedIsGame = _matcher?.Match(process, PathNormalizer.NormalizeName(process.ExecutableName), out _, out _) is not null;
        return _cachedIsGame;
    }

    private void AddRest(TimeSpan elapsed)
    {
        var wasComplete = _rest >= TimeSpan.FromMinutes(_settings.BreakMinutes);
        _rest += elapsed;
        if (wasComplete || _rest < TimeSpan.FromMinutes(_settings.BreakMinutes)) return;
        _playtime = TimeSpan.Zero;
        _nextReminder = TimeSpan.FromMinutes(_settings.PlayMinutes);
        ClearNotifications();
    }

    private void Reset()
    {
        _playtime = TimeSpan.Zero;
        _rest = TimeSpan.Zero;
        _previousIsGame = false;
        _previousIsRest = false;
        _cachedIdentity = null;
        _nextReminder = TimeSpan.FromMinutes(_settings.PlayMinutes);
        ClearNotifications();
    }

    private void StopTimer()
    {
        ++_generation;
        try { _timer?.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose reminder timer"); }
        finally { _timer = null; }
    }

    private void ClearNotifications()
    {
        try { _notifier.Clear(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to clear break notifications"); }
    }
}
