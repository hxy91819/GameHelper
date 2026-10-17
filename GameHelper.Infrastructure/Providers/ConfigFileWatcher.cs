using System;
using System.IO;
using System.Threading;
using GameHelper.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameHelper.Infrastructure.Providers;

/// <summary>
/// Watches the YAML config file on disk and raises a callback once edits settle,
/// so externally edited configuration takes effect without restarting GameHelper.
/// <see cref="Start"/> is idempotent and best-effort: if the platform watcher cannot
/// be created, hot reload degrades to a no-op and monitoring still works as before.
/// </summary>
public sealed class ConfigFileWatcher : IConfigWatcher
{
    /// <summary>Debounce window that collapses editor write bursts into a single reload.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly Action _onChanged;
    private readonly TimeSpan _debounce;
    private readonly ILogger _logger;

    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private bool _retryScheduled;
    private bool _disposed;

    public ConfigFileWatcher(string filePath, Action onChanged)
        : this(filePath, onChanged, NullLogger.Instance, DefaultDebounce)
    {
    }

    public ConfigFileWatcher(string filePath, Action onChanged, ILogger logger)
        : this(filePath, onChanged, logger, DefaultDebounce)
    {
    }

    internal ConfigFileWatcher(string filePath, Action onChanged, ILogger logger, TimeSpan debounce)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("filePath required", nameof(filePath));
        }

        ArgumentNullException.ThrowIfNull(onChanged);
        if (debounce <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(debounce));
        }

        _filePath = Path.GetFullPath(filePath);
        _onChanged = onChanged;
        _logger = logger ?? NullLogger.Instance;
        _debounce = debounce;
    }

    /// <inheritdoc />
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_watcher is not null)
            {
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                var fileName = Path.GetFileName(_filePath);
                if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
                {
                    throw new InvalidOperationException($"Config path {_filePath} is not a valid file path.");
                }

                Directory.CreateDirectory(directory);

                var watcher = new FileSystemWatcher(directory, fileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };
                watcher.Changed += OnWatchedFileEvent;
                watcher.Created += OnWatchedFileEvent;
                watcher.Renamed += OnWatchedFileEvent;
                watcher.Error += OnWatchedError;
                _watcher = watcher;
                _logger.LogInformation("Watching config file for changes: {ConfigPath}", _filePath);
            }
            catch (Exception ex)
            {
                // Hot reload is auxiliary: a failed watcher must not break monitoring startup.
                // Leaving _watcher null keeps Start() retryable (e.g. the directory appears later).
                _logger.LogWarning(ex, "Config hot reload disabled: failed to watch {ConfigPath}", _filePath);
                _watcher = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _retryScheduled = false;
        }

        // SafeCleanup: isolate every step so a single failure cannot block the rest.
        if (_watcher is not null)
        {
            try
            {
                _watcher.Changed -= OnWatchedFileEvent;
                _watcher.Created -= OnWatchedFileEvent;
                _watcher.Renamed -= OnWatchedFileEvent;
                _watcher.Error -= OnWatchedError;
            }
            catch
            {
                // Unsubscription is best-effort; Dispose below closes the handle anyway.
            }

            try
            {
                _watcher.Dispose();
            }
            catch
            {
                // Best-effort cleanup.
            }

            _watcher = null;
        }

        if (_debounceTimer is not null)
        {
            try
            {
                _debounceTimer.Dispose();
            }
            catch
            {
                // Best-effort cleanup; in-flight callbacks check _disposed under lock.
            }

            _debounceTimer = null;
        }
    }

    private void OnWatchedFileEvent(object sender, FileSystemEventArgs e)
    {
        ScheduleReload();
    }

    private void OnWatchedError(object sender, ErrorEventArgs e)
    {
        _logger.LogWarning(e.GetException(), "Config file watcher error for {ConfigPath}", _filePath);
    }

    private void ScheduleReload()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var timer = _debounceTimer;
            if (timer is null)
            {
                _debounceTimer = new Timer(OnDebounceElapsed, null, _debounce, Timeout.InfiniteTimeSpan);
            }
            else
            {
                try
                {
                    timer.Change(_debounce, Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException)
                {
                    // Raced with Dispose; ignore.
                }
            }
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        bool isRetry;
        Action callback;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            isRetry = _retryScheduled;
            _retryScheduled = false;
            if (!File.Exists(_filePath))
            {
                // Editors briefly delete/replace the file while saving. Never reload an
                // absent file - that would rebuild indexes as empty and stall monitoring
                // until the next change arrives.
                _logger.LogDebug("Config file {ConfigPath} is temporarily absent; reload skipped", _filePath);
                return;
            }

            callback = _onChanged;
        }

        try
        {
            callback();
        }
        catch (Exception ex)
        {
            // The file can still be mid-write when the callback reads it. Retry once so a
            // burst that ends in the final event still lands; bound the retry so a
            // permanently broken file cannot spin the reload loop forever.
            _logger.LogWarning(ex, "Config reload failed after change to {ConfigPath}", _filePath);
            if (!isRetry)
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _retryScheduled = true;
                    try
                    {
                        _debounceTimer?.Change(_debounce, Timeout.InfiniteTimeSpan);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Raced with Dispose; ignore.
                    }
                }
            }
        }
    }
}