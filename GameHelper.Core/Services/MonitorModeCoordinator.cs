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
    private MonitorModeSettings? _monitorModeSettings;
    private UserCoordinate? _monitorModeCoordinates;
    private DateTime? _monitorModeCoordinatesAt;
    private int _locationBootstrapInFlight;
    private long? _locationBootstrapKickTimestamp;
    private Entry[] _entries = [];
    private sealed class Entry(MonitorDeviceIdentity device, MonitorModePlan plan, IMonitorModeController controller)
    {
        internal readonly MonitorDeviceIdentity Device = device;
        internal readonly MonitorModePlan Plan = plan;
        internal readonly IMonitorModeController Controller = controller;
        internal (bool Game, bool Daylight, bool Available, string Path)? Last;
        internal string? Pause;
    }

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

    internal sealed record Snapshot(bool AnyGameActive, bool Started);

    // Called with the service state lock held; these methods never enter UpdateLock.
    internal void Configure(MonitorModeSettings? settings)
    {
        _monitorModeSettings = settings;
        var entries = new List<Entry>();
        if (settings?.Enabled == true)
        {
            try
            {
                settings.Validate();
                foreach (var profile in settings.Displays.Where(profile => profile.Enabled))
                {
                    var plan = MonitorModePlan.TryCreate(settings.ForDisplay(profile), _logger, ResolveMonitorModeCoordinates);
                    var controller = _monitorModeController?.ForDevice(profile.Device);
                    if (plan is not null && controller is not null) entries.Add(new(profile.Device, plan, controller));
                }
                if (settings.Displays.Count == 0) _logger.LogInformation("显示器预设待绑定：旧配方已保留，未绑定设备时不执行自动切换。");
            }
            catch (ArgumentException ex) { entries.Clear(); _logger.LogWarning(ex, "Invalid bound monitor profiles; preset automation disabled."); }
        }
        _entries = entries.ToArray();
    }

    internal void Reset()
    {
        foreach (var entry in _entries) { entry.Last = null; entry.Pause = null; }
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

            Entry[] entries;
            var anyGameActive = false;
            lock (_stateLock)
            {
                var snapshot = _captureSnapshot();
                entries = _entries;
                if (!snapshot.Started || entries.Length == 0) return;
                anyGameActive = snapshot.AnyGameActive;
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

            // HDR and availability are evaluated for the bound display, not another screen.
            IReadOnlyList<HdrDisplayInfo> displays = [];
            bool known;
            try { known = _hdr.TryGetDisplays(out displays); }
            catch (Exception) { known = false; }
            foreach (var entry in entries)
            {
                try
                {
                    lock (_stateLock) if (!_captureSnapshot().Started || !ReferenceEquals(entries, _entries)) return;
                    var display = known ? entry.Device.Resolve(displays) : null;
                    var pause = !known ? "无法确认显示设备状态" : display is null ? "设备离线或身份不唯一" : display.Enabled ? "该设备 HDR 已开启" : null;
                    if (pause is null && entries.Count(other => string.Equals(other.Device.Resolve(displays)?.DevicePath, display!.DevicePath, StringComparison.OrdinalIgnoreCase)) > 1)
                        pause = "多份配方指向同一设备";
                    lock (_stateLock)
                    {
                        if (pause is not null)
                        {
                            entry.Last = null;
                            if (entry.Pause != pause) _logger.LogInformation("显示器 {Device} 预设暂停：{Reason}。", entry.Device.Name, pause);
                            entry.Pause = pause;
                            continue;
                        }
                        if (entry.Pause is not null) _logger.LogInformation("显示器 {Device} 预设恢复。", entry.Device.Name);
                        entry.Pause = null;
                    }
                    var available = entry.Controller.IsAvailable;
                    var daylight = entry.Plan.Daylight.IsDaylight(_timeProvider.GetLocalNow());
                    var state = (anyGameActive, daylight, available, display!.DevicePath);
                    lock (_stateLock)
                    {
                        if (!_captureSnapshot().Started || !ReferenceEquals(entries, _entries)) return;
                        if (!forced && entry.Last == state) continue;
                        entry.Last = state;
                    }
                    if (!_monitorModeScheduler.Update(entry.Plan, anyGameActive, entry.Controller, _logger, _timeProvider.GetLocalNow()))
                        lock (_stateLock) entry.Last = null;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Monitor mode update failed for {Device}; the next tick will retry.", entry.Device.Name);
                    lock (_stateLock) entry.Last = null;
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
            if (_locationCacheStore is null || _entries.Length == 0) return null;
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
