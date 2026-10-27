using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameHelper.Core.Services;

/// <summary>
/// Owns preset evaluation and location caching. Updates and Stop acquire UpdateLock before
/// the service state lock. Configure and Reset borrow only the state lock. Hardware and
/// location-cache I/O run outside the state lock; the service supplies a consistent snapshot.
/// </summary>
internal sealed class MonitorModeCoordinator
{
    private readonly IMonitorModeController? _monitorModeController;
    private readonly IUserLocationSource? _locationSource;
    private readonly ILocationCacheStore? _locationCacheStore;
    private readonly IHdrController _hdr;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly object _stateLock;
    private readonly Func<Snapshot> _captureSnapshot;
    private readonly MonitorModeScheduler _monitorModeScheduler = new();
    private readonly object _monitorModeUpdateLock = new();
    private MonitorModePlan? _monitorModePlan;
    private MonitorModeSettings? _monitorModeSettings;
    private UserCoordinate? _monitorModeCoordinates;
    private DateTime? _monitorModeCoordinatesAt;
    private bool? _lastMonitorModeControllerAvailable;
    private int _locationBootstrapInFlight;
    private long? _locationBootstrapKickTimestamp;
    private bool? _lastMonitorModeAnyGameActive;
    private bool? _lastMonitorModeDaylight;
    private bool _lastMonitorModeHdrPause;

    internal MonitorModeCoordinator(IMonitorModeController? controller, IHdrController hdr,
        IUserLocationSource? locationSource, ILocationCacheStore? locationCacheStore,
        TimeProvider timeProvider, ILogger logger, object stateLock, Func<Snapshot> captureSnapshot)
    {
        _monitorModeController = controller;
        _hdr = hdr;
        _locationSource = locationSource;
        _locationCacheStore = locationCacheStore;
        _timeProvider = timeProvider;
        _logger = logger;
        _stateLock = stateLock;
        _captureSnapshot = captureSnapshot;
    }

    internal bool HasController => _monitorModeController is not null;

    internal object UpdateLock => _monitorModeUpdateLock;

    internal sealed record Snapshot(bool AnyGameActive, IReadOnlyList<string> HdrGameKeys, bool OwnsHdr, bool Started);

    // Called with the service state lock held; these methods never enter UpdateLock.
    internal void Configure(MonitorModeSettings? settings)
    {
        _monitorModeSettings = settings;
        _monitorModePlan = MonitorModePlan.TryCreate(settings, _logger, ResolveMonitorModeCoordinates);
    }

    internal void Reset()
    {
        _lastMonitorModeAnyGameActive = null;
        _lastMonitorModeDaylight = null;
        _lastMonitorModeControllerAvailable = null;
        _lastMonitorModeHdrPause = false;
    }

    /// <summary>
    /// Re-evaluates the monitor preset rule. Gating happens under the state lock, while
    /// the DDC/CI bus I/O inside <see cref="MonitorModeScheduler.Update"/> stays outside
    /// it so a slow monitor never stalls session bookkeeping.
    /// </summary>
    internal void Update(bool forced)
    {
        lock (_monitorModeUpdateLock)
        {
            var controller = _monitorModeController;
            if (controller is null)
            {
                return;
            }

            MonitorModePlan? plan;
            var anyGameActive = false;
            IReadOnlyList<string> hdrGameKeys;
            var ownsHdr = false;
            lock (_stateLock)
            {
                var snapshot = _captureSnapshot();
                ownsHdr = snapshot.OwnsHdr;
                plan = _monitorModePlan;
                if (!snapshot.Started || plan is null) return;
                anyGameActive = snapshot.AnyGameActive;
                hdrGameKeys = snapshot.HdrGameKeys;
                // 显示器从系统消失/回来会改变控制器可用性：这属于必须重新评估的变化
                // （显示器离线期间档位可能停在被强制写入过的状态，回来需要对齐）。
                // location=auto 且还没有坐标时，随 tick 自愈式重试坐标引导：
                // 用户事后在系统设置里打开“定位”无需重启即可拿到日出日落判定。
                if (NeedsAutoLocation(_monitorModeSettings)
                    && (!_monitorModeCoordinatesAt.HasValue || _timeProvider.GetLocalNow().DateTime - _monitorModeCoordinatesAt.Value > LocationCacheMaxAge)
                    && _locationCacheStore is not null
                    && _locationSource is not null)
                {
                    var nowTimestamp = _timeProvider.GetTimestamp();
                    if ((!_locationBootstrapKickTimestamp.HasValue
                         || _timeProvider.GetElapsedTime(_locationBootstrapKickTimestamp.Value, nowTimestamp).TotalMilliseconds >= LocationBootstrapKickCooldownMs)
                        && Interlocked.CompareExchange(ref _locationBootstrapInFlight, 1, 0) == 0)
                    {
                        _locationBootstrapKickTimestamp = nowTimestamp;
                        var settingsSnapshot = _monitorModeSettings!;
                        try
                        {
                            _ = Task.Run(() => BootstrapMonitorModeLocation(settingsSnapshot));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to kick off the monitor mode location bootstrap.");
                            Interlocked.Exchange(ref _locationBootstrapInFlight, 0);
                        }
                    }
                }

            }

            try
            {
                // 规则排除且无租约的库内游戏可降级为 SDR 预设；
                // 系统 HDR 已开启或无法确认时，始终不做 DDC/CI 探测与写档。
                // 暂停路径让变更门失效，HDR 退出后无需新事件即可重评档位。
                var hdrKnown = _hdr.TryGetEnabled(out var hdrEnabled);
                var excludedWithoutLease = hdrGameKeys.Count > 0 && !ownsHdr && hdrKnown && !hdrEnabled &&
                    _hdr.GetAutomationAvailability() == HdrAutomationAvailability.ExcludedByPolicy;
                var hdrPresetPause = !hdrKnown || hdrEnabled || (hdrGameKeys.Count > 0 && !excludedWithoutLease);
                if (hdrPresetPause)
                {
                    lock (_stateLock)
                    {
                        _lastMonitorModeDaylight = null;
                        _lastMonitorModeControllerAvailable = null;
                    }
                    if (!_lastMonitorModeHdrPause)
                    {
                        _lastMonitorModeHdrPause = true;
                        _logger.LogInformation(
                            "显示器预设自动化暂停：{Reason}。",
                            hdrGameKeys.Count > 0
                                ? $"HDR 自动化游戏活跃（{string.Join("、", hdrGameKeys)}）"
                                : "系统 HDR 已开启或状态未知");
                    }
                    return;
                }
                if (_lastMonitorModeHdrPause)
                {
                    _lastMonitorModeHdrPause = false;
                    _logger.LogInformation("显示器预设自动化恢复：系统确认 SDR，且没有需要让位的 HDR 自动化。");
                }
                // Availability probing can perform DDC I/O; never hold the session state lock.
                var controllerAvailable = controller.IsAvailable;
                var daylight = plan.Daylight.IsDaylight(_timeProvider.GetLocalNow());
                lock (_stateLock)
                {
                    if (!_captureSnapshot().Started) return;
                    if (!forced
                        && _lastMonitorModeAnyGameActive == anyGameActive
                        && _lastMonitorModeDaylight == daylight
                        && _lastMonitorModeControllerAvailable == controllerAvailable)
                    {
                        return;
                    }

                    _lastMonitorModeAnyGameActive = anyGameActive;
                    _lastMonitorModeDaylight = daylight;
                    _lastMonitorModeControllerAvailable = controllerAvailable;
                }

                if (!_monitorModeScheduler.Update(plan, anyGameActive, controller, _logger, _timeProvider.GetLocalNow()))
                {
                    lock (_stateLock) _lastMonitorModeDaylight = null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Monitor mode update failed; the next event or tick will retry.");
                lock (_stateLock)
                {
                    // Invalidate the change gate so subsequent ticks retry even without a state change.
                    _lastMonitorModeDaylight = null;
                    _lastMonitorModeControllerAvailable = null;
                }
            }
        }
    }

    private static bool NeedsAutoLocation(MonitorModeSettings? settings) =>
        settings is not null
        && string.Equals(settings.Location, "auto", StringComparison.OrdinalIgnoreCase)
        && !(settings.Latitude.HasValue && settings.Longitude.HasValue);

    /// <summary>Memoizes cached coordinates without holding the session state lock during file I/O.</summary>
    private UserCoordinate? ResolveMonitorModeCoordinates()
    {
        MonitorModeSettings? settings;
        lock (_stateLock)
        {
            settings = _monitorModeSettings;
            if (!NeedsAutoLocation(settings)) return null;
            if (_monitorModeCoordinates.HasValue) return _monitorModeCoordinates;
            if (_locationCacheStore is null || _monitorModePlan is null) return null;
        }

        var cached = _locationCacheStore.Load();
        lock (_stateLock)
        {
            if (!ReferenceEquals(settings, _monitorModeSettings) || !NeedsAutoLocation(_monitorModeSettings)) return null;
            if (cached is not null && !_monitorModeCoordinates.HasValue)
            {
                _monitorModeCoordinates = new UserCoordinate(cached.Latitude, cached.Longitude);
                _monitorModeCoordinatesAt = cached.UpdatedAt;
            }
            return _monitorModeCoordinates;
        }
    }

    /// <summary>
    /// Coordinate bootstrap for monitorMode location=auto: prefer a cache younger than a
    /// day, otherwise query the location source once and persist the result. On success
    /// the monitor-mode plan is rebuilt and evaluated immediately so the first rule
    /// application uses the solar window; on failure the fixed time window applies.
    /// </summary>
    private void BootstrapMonitorModeLocation(MonitorModeSettings settings)
    {
        var cache = _locationCacheStore;
        if (cache is null)
        {
            return;
        }

        try
        {
            var cached = cache.Load();
            var stale = cached is null || (_timeProvider.GetLocalNow().DateTime - cached.UpdatedAt) > LocationCacheMaxAge;

            UserCoordinate? live = null;
            if (stale && _locationSource is not null)
            {
                try
                {
                    live = _locationSource.TryGetLocation();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Automatic location failed during the monitor mode location bootstrap.");
                }
            }

            UserCoordinate publish;
            if (live is not null)
            {
                try
                {
                    cache.Save(live.Value);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to persist the detected location; it will be re-detected next time.");
                }

                publish = live.Value;
            }
            else if (cached is not null)
            {
                if (stale)
                {
                    _logger.LogInformation(
                        "monitorMode location=auto could not refresh coordinates (cache age {Age}); using the cached coordinate.",
                        _timeProvider.GetLocalNow().DateTime - cached.UpdatedAt);
                }

                publish = new UserCoordinate(cached.Latitude, cached.Longitude);
            }
            else
            {
                _logger.LogInformation(
                    "monitorMode location=auto obtained no coordinates (the system 'Location' switch may still be off or no fix yet); daylight falls back to fixed times. Retrying every 60 seconds until coordinates are cached or latitude/longitude is set manually.");
                return;
            }

            lock (_stateLock)
            {
                if (!_captureSnapshot().Started || !ReferenceEquals(settings, _monitorModeSettings) || !NeedsAutoLocation(settings)) return;
                _monitorModeCoordinates = publish;
                _monitorModeCoordinatesAt = live.HasValue ? _timeProvider.GetLocalNow().DateTime : cached!.UpdatedAt;
            }
            Update(forced: true);
        }
        finally
        {
            Interlocked.Exchange(ref _locationBootstrapInFlight, 0);
        }
    }

    private static readonly TimeSpan LocationCacheMaxAge = TimeSpan.FromHours(24);

    /// <summary>定位引导失败后的重试冷却（随监视模式 tick 自愈重试）。</summary>
    private const int LocationBootstrapKickCooldownMs = 60_000;

}
