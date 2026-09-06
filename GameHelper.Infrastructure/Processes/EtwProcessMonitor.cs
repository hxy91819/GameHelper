using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

using GameHelper.Infrastructure.Exceptions;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;

namespace GameHelper.Infrastructure.Processes
{
    /// <summary>
    /// ETW-based process monitor that provides low-latency process lifecycle notifications.
    /// Requires administrator privileges to access kernel ETW providers.
    /// </summary>
    public sealed class EtwProcessMonitor : IProcessMonitor
    {
        private const string SessionNamePrefix = "GameHelper-ETW-";

        /// <summary>会话丢失后的自动恢复尝试次数。</summary>
        public const int SessionRecoveryAttempts = 3;

        private TraceEventSession? _session;
        private Thread? _processingThread;
        private volatile bool _isRunning;
        private volatile bool _disposed;
        private volatile bool _stopRequested;
        private ProcessObservationPolicy _policy = ProcessObservationPolicy.ObserveAll();
        private readonly ILogger<EtwProcessMonitor>? _logger;
        private string _sessionName;

        /// <summary>0 = 空闲；1 = 恢复流程进行中（Interlocked）。</summary>
        private int _recovering;
        private Timer? _healthCheckTimer;
        private readonly object _lifecycleLock = new();

        private readonly ConcurrentDictionary<int, string> _startPathCache = new();

        /// <inheritdoc />
        public event Action<ProcessEventInfo>? ProcessStarted;
        /// <inheritdoc />
        public event Action<ProcessEventInfo>? ProcessStopped;

        /// <summary>
        /// Creates a new ETW process monitor with optional process filtering.
        /// </summary>
        /// <param name="allowedProcessNames">Optional whitelist of process names to monitor. If null, monitors all processes.</param>
        /// <param name="logger">Optional logger for diagnostic information.</param>
        public EtwProcessMonitor(IEnumerable<string>? allowedProcessNames = null, ILogger<EtwProcessMonitor>? logger = null)
        {
            _logger = logger;
            _sessionName = NewSessionName();
            
            if (allowedProcessNames != null)
            {
                Configure(new ProcessObservationPolicy(allowedProcessNames));
            }

            _logger?.LogDebug(
                "EtwProcessMonitor created with {Count} candidate processes",
                CurrentPolicy.CandidateProcessNames.Count);
        }

        /// <inheritdoc />
        public void Start()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(EtwProcessMonitor));

            lock (_lifecycleLock)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(EtwProcessMonitor));

                if (_isRunning)
                {
                    _logger?.LogDebug("ETW monitor already running");
                    return;
                }

                if (!IsRunningAsAdministrator())
                {
                    throw new InsufficientPrivilegesException();
                }

                _stopRequested = false;
                try
                {
                    InitializeEtwSession();
                    PrefillRunningProcesses();
                    _isRunning = true;
                    StartHealthCheckTimer();
                    _logger?.LogInformation("ETW process monitor started successfully");
                }
                catch (Exception ex) when (IsResourceExhausted(ex))
                {
                    _logger?.LogWarning(ex, "ETW session resource exhausted. Cleaning up stale sessions and retrying.");
                    SafeCleanup();
                    CleanupStaleSessions();
                    try
                    {
                        InitializeEtwSession();
                        PrefillRunningProcesses();
                        _isRunning = true;
                        StartHealthCheckTimer();
                        _logger?.LogInformation("ETW process monitor started successfully after cleanup");
                    }
                    catch (Exception retryEx)
                    {
                        _logger?.LogError(retryEx, "Failed to start ETW monitor even after cleanup");
                        SafeCleanup();
                        throw new EtwMonitorException("Failed to initialize ETW session after cleanup", retryEx);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to start ETW process monitor");
                    SafeCleanup();
                    throw new EtwMonitorException("Failed to initialize ETW session", ex);
                }
            }
        }

        /// <inheritdoc />
        public void Stop()
        {
            if (_disposed || !_isRunning)
                return;

            _logger?.LogDebug("Stopping ETW process monitor");
            lock (_lifecycleLock)
            {
                // 正常停止：处理线程退出后不得触发恢复。
                _stopRequested = true;
                StopHealthCheckTimer();
                SafeCleanup();
            }
            _logger?.LogInformation("ETW process monitor stopped");
        }

        /// <inheritdoc />
        public void Configure(ProcessObservationPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(policy);
            Volatile.Write(ref _policy, policy);

            _logger?.LogDebug(
                "ETW observation policy updated: {Count} candidates, all={ObserveAll}, stop={ObserveStopEvents}",
                policy.CandidateProcessNames.Count,
                policy.ObservesAllProcessNames,
                policy.ObserveStopEvents);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            lock (_lifecycleLock)
            {
                StopHealthCheckTimer();
                SafeCleanup();
            }
            _logger?.LogDebug("EtwProcessMonitor disposed");
        }

        /// <summary>会话名带进程 PID，供陈旧会话清理时区分"死者残留"与"活跃实例"。</summary>
        private static string NewSessionName() =>
            $"{SessionNamePrefix}{Environment.ProcessId}-{Guid.NewGuid():N}";

        private void InitializeEtwSession()
        {
            // 每次初始化都生成新名字：恢复场景下旧名字可能已被外部停止但尚未完全释放。
            _sessionName = NewSessionName();
            _session = new TraceEventSession(_sessionName);

            // Enable kernel process events
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process);

            // Set up event handlers
            _session.Source.Kernel.ProcessStart += OnProcessStart;
            _session.Source.Kernel.ProcessStop += OnProcessStop;

            // Start processing events in a background thread
            _processingThread = new Thread(ProcessEvents)
            {
                Name = "ETW-ProcessMonitor",
                IsBackground = true
            };
            _processingThread.Start();
        }

        private void ProcessEvents()
        {
            try
            {
                _logger?.LogDebug("ETW event processing thread started");
                _session?.Source.Process();
            }
            catch (Exception ex)
            {
                if (!_disposed && _isRunning)
                {
                    _logger?.LogError(ex, "ETW event processing thread encountered an error");
                }
            }
            finally
            {
                _logger?.LogDebug("ETW event processing thread ended");
                // 非 Stop/Dispose 主动退出（_isRunning 仍为 true）意味着事件流意外中断：
                // 若不恢复，实例会变成"僵尸监控"——进程照常运行但 start/stop 全部失聪，
                // 活跃会话的 stop 事件永远收不到，游玩时长静默丢失。
                if (Volatile.Read(ref _isRunning) && !_disposed && !_stopRequested
                    && Interlocked.CompareExchange(ref _recovering, 0, 0) == 0)
                {
                    _logger?.LogError("ETW event stream ended unexpectedly; scheduling session recovery");
                    ScheduleRecovery();
                }
            }
        }

        /// <summary>周期性自检：活跃会话列表里找不到自己的会话即触发恢复。</summary>
        private void StartHealthCheckTimer()
        {
            StopHealthCheckTimer();
            // 检查间隔远小于一次游玩会话的量级，且单次检查只是读取会话名列表，开销可忽略。
            _healthCheckTimer = new Timer(
                _ => CheckSessionHealth(),
                null,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30));
        }

        private void StopHealthCheckTimer()
        {
            _healthCheckTimer?.Dispose();
            _healthCheckTimer = null;
        }

        private void CheckSessionHealth()
        {
            if (!_isRunning || _disposed || _stopRequested)
            {
                return;
            }

            try
            {
                var sessionName = _sessionName;
                var active = TraceEventSession.GetActiveSessionNames();
                if (!active.Contains(sessionName, StringComparer.OrdinalIgnoreCase))
                {
                    _logger?.LogError(
                        "ETW session {SessionName} disappeared from active sessions; scheduling session recovery",
                        sessionName);
                    ScheduleRecovery();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "ETW session health check failed");
            }
        }

        /// <summary>调度一次恢复（幂等：恢复进行中时忽略后续触发）。</summary>
        private void ScheduleRecovery()
        {
            if (Interlocked.CompareExchange(ref _recovering, 1, 0) != 0)
            {
                return;
            }

            _ = Task.Run(RunSessionRecovery);
        }

        private void RunSessionRecovery()
        {
            try
            {
                for (var attempt = 1; attempt <= SessionRecoveryAttempts; attempt++)
                {
                    lock (_lifecycleLock)
                    {
                        if (_disposed || _stopRequested)
                        {
                            return;
                        }

                        // 保留 PID→路径缓存：活跃游戏进程并未退出，恢复后它的 stop 事件
                        // 需要缓存命中来通过名字门控，会话时长才能追回。
                        SafeCleanup(preservePathCache: true);
                    }

                    // 退避放在锁外，避免阻塞并发的 Stop/Dispose。
                    Thread.Sleep(TimeSpan.FromSeconds(attempt));

                    lock (_lifecycleLock)
                    {
                        if (_disposed || _stopRequested)
                        {
                            return;
                        }

                        try
                        {
                            InitializeEtwSession();
                            PrefillRunningProcesses();
                            _isRunning = true;
                            _logger?.LogInformation(
                                "ETW session recovered on attempt {Attempt}; process monitoring resumed",
                                attempt);
                            return;
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(
                                ex,
                                "ETW session recovery attempt {Attempt}/{Attempts} failed",
                                attempt,
                                SessionRecoveryAttempts);
                        }
                    }
                }

                _logger?.LogError(
                    "ETW session recovery failed after {Attempts} attempts; process monitoring is DOWN. "
                    + "Game sessions will not be tracked until GameHelper is restarted.",
                    SessionRecoveryAttempts);
            }
            finally
            {
                Interlocked.Exchange(ref _recovering, 0);
            }
        }

        private void OnProcessStart(TraceEvent data)
        {
            try
            {
                var processName = GetProcessName(data);
                if (string.IsNullOrWhiteSpace(processName))
                    return;

                var imageFileName = data.PayloadByName("ImageFileName") as string;
                if (IsAllowedProcessStart(processName, imageFileName))
                {
                    var pathHint = imageFileName;
                    var cached = false;
                    if (!string.IsNullOrWhiteSpace(pathHint))
                    {
                        _startPathCache[data.ProcessID] = pathHint;
                        cached = true;
                    }

                    _logger?.LogDebug(
                        "Process started: {ProcessName} (PID: {ProcessId}, ImageFileName={ImageFileName}, Cached={Cached})",
                        processName, data.ProcessID, imageFileName, cached);

                    var info = new ProcessEventInfo(processName, pathHint, data.ProcessID);
                    ProcessStarted?.Invoke(info);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error processing ProcessStart event");
            }
        }

        private void OnProcessStop(TraceEvent data)
        {
            // Remove the cached path for this PID regardless of stop-events being enabled,
            // so stale entries don't accumulate while stop events are disabled.
            bool hadCache = _startPathCache.TryRemove(data.ProcessID, out var cachedPath);

            try
            {
                var processName = GetProcessName(data);

                // If the ETW stop event doesn't carry a clear name but we had cached
                // this PID from a prior start, derive the name from the cached path.
                if (string.IsNullOrWhiteSpace(processName) && hadCache && !string.IsNullOrWhiteSpace(cachedPath))
                {
                    processName = Path.GetFileName(cachedPath);
                }

                // If this PID was previously cached (meaning it passed the start filter),
                // always allow the stop event regardless of whether the stop payload's
                // name matches the current filter. The process may have changed its
                // displayed name between start and stop.
                if (!IsAllowedProcessStop(processName, hadCache))
                {
                    return;
                }

                var fallbackImageFileName = data.PayloadByName("ImageFileName") as string;
                var realPath = hadCache ? cachedPath : fallbackImageFileName;
                var executableName = processName!;

                _logger?.LogDebug(
                    "Process stopped: {ProcessName} (PID: {ProcessId}, CacheHit={CacheHit}, CachedPath={CachedPath}, Fallback={Fallback})",
                    executableName, data.ProcessID, hadCache, realPath, fallbackImageFileName);

                var info = new ProcessEventInfo(executableName, realPath, data.ProcessID);
                ProcessStopped?.Invoke(info);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error processing ProcessStop event");
            }
        }

        private static string? GetProcessName(TraceEvent data)
        {
            // Try to get the image file name from the event
            if (data.PayloadByName("ImageFileName") is string imageFileName && !string.IsNullOrWhiteSpace(imageFileName))
            {
                try
                {
                    return Path.GetFileName(imageFileName);
                }
                catch
                {
                    // Fall back to process name if path parsing fails
                }
            }

            // Fall back to process name
            if (data.PayloadByName("ProcessName") is string processName && !string.IsNullOrWhiteSpace(processName))
            {
                return processName;
            }

            return null;
        }

        internal bool IsAllowedProcessStart(string processName, string? pathHint)
        {
            var policy = CurrentPolicy;
            if (policy.Includes(processName))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(pathHint))
            {
                return false;
            }

            try
            {
                // The ETW callback must stay cheap: do not open every non-candidate
                // process here just to recover rare bad name/path payloads.
                var hintName = Path.GetFileName(pathHint);
                return !string.IsNullOrWhiteSpace(hintName) && policy.Includes(hintName);
            }
            catch
            {
                return false;
            }
        }

        internal bool IsAllowedProcessStop(string? processName, bool hadCachedStart)
        {
            var policy = CurrentPolicy;
            return policy.ObserveStopEvents &&
                !string.IsNullOrWhiteSpace(processName) &&
                (hadCachedStart || policy.Includes(processName));
        }

        private ProcessObservationPolicy CurrentPolicy => Volatile.Read(ref _policy);

        /// <summary>
        /// Checks whether an exception indicates ETW session resource exhaustion (0x800705AA).
        /// </summary>
        private static bool IsResourceExhausted(Exception ex)
        {
            // 0x800705AA = ERROR_NO_SYSTEM_RESOURCES
            return ex is COMException comEx
                && unchecked((uint)comEx.HResult) == 0x800705AA;
        }

        /// <summary>
        /// Finds and stops stale GameHelper ETW sessions left behind by crashed or
        /// killed processes. Sessions owned by a live process are left alone — the
        /// session name embeds the owning PID, so another instance starting up can
        /// no longer accidentally kill a running instance's active session.
        /// </summary>
        private void CleanupStaleSessions()
        {
            try
            {
                var staleSessions = TraceEventSession.GetActiveSessionNames()
                    .Where(name => ShouldCleanupSession(name, IsProcessAlive))
                    .ToList();

                if (staleSessions.Count == 0)
                {
                    _logger?.LogDebug("No stale GameHelper ETW sessions found");
                    return;
                }

                _logger?.LogWarning("Found {Count} stale GameHelper ETW sessions, cleaning up", staleSessions.Count);

                foreach (var name in staleSessions)
                {
                    try
                    {
                        using var session = new TraceEventSession(name);
                        session.Stop();
                        _logger?.LogDebug("Stopped stale ETW session: {SessionName}", name);
                    }
                    catch (Exception stopEx)
                    {
                        _logger?.LogDebug(stopEx, "Failed to stop stale ETW session: {SessionName}", name);
                    }
                }
            }
            catch (Exception cleanupEx)
            {
                _logger?.LogDebug(cleanupEx, "Error during stale ETW session cleanup");
            }
        }

        /// <summary>
        /// 决定一个 GameHelper ETW 会话是否可以清理。纯函数便于单元测试。
        /// 新格式（含 PID）且属主进程仍存活 → 保留；属主已死或旧格式（无法判定）→ 清理。
        /// </summary>
        internal static bool ShouldCleanupSession(string sessionName, Func<int, bool> isProcessAlive)
        {
            ArgumentNullException.ThrowIfNull(isProcessAlive);
            if (!sessionName.StartsWith(SessionNamePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var remainder = sessionName[SessionNamePrefix.Length..];
            var separator = remainder.IndexOf('-');
            if (separator <= 0
                || !int.TryParse(remainder[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ownerPid))
            {
                // 旧格式（GameHelper-ETW-{guid}）：无法判定属主，保持既有清理行为。
                return true;
            }

            return !isProcessAlive(ownerPid);
        }

        private static bool IsProcessAlive(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch
            {
                // 进程已退出时 GetProcessById 抛异常。
                return false;
            }
        }

        /// <summary>
        /// Gets the real executable path for a process, resolving inaccurate paths from shortcut launches.
        /// </summary>
        /// <param name="processId">Process ID</param>
        /// <returns>The real executable path, or null if retrieval fails.</returns>
        private string? GetRealProcessPath(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return QueryFullProcessImageName(process.Handle);
            }
            catch (Exception ex)
            {
                // Process may have already exited, or insufficient privileges.
                _logger?.LogDebug(ex, "Unable to get real path for process {ProcessId}", processId);
                return null;
            }
        }

        /// <summary>
        /// Uses Win32 API QueryFullProcessImageName to obtain the full image path for a process.
        /// This method is more reliable than ETW's ImageFileName for correctly handling shortcut-launched processes.
        /// </summary>
        /// <param name="processHandle">Process handle</param>
        /// <returns>The full executable path, or null if retrieval fails.</returns>
        private static string? QueryFullProcessImageName(IntPtr processHandle)
        {
            const uint maxPath = 1024;
            uint size = maxPath;
            var sb = new StringBuilder((int)size);

            if (NativeMethods.QueryFullProcessImageNameNative(processHandle, 0, sb, ref size))
            {
                return sb.ToString();
            }

            return null;
        }

        /// <summary>
        /// Win32 API declarations
        /// </summary>
        private static class NativeMethods
        {
            /// <summary>
            /// Retrieves the full image file name for a process.
            /// See: https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamea
            /// </summary>
            [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, CharSet = CharSet.Unicode)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool QueryFullProcessImageNameNative(
                IntPtr hProcess,
                uint dwFlags,
                StringBuilder lpExeName,
                ref uint lpdwSize);
        }

        private static bool IsRunningAsAdministrator()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private void PrefillRunningProcesses()
        {
            try
            {
                var policy = CurrentPolicy;
                if (policy.ObservesAllProcessNames || policy.CandidateProcessNames.Count == 0)
                {
                    _logger?.LogDebug("No process-name candidates configured, skipping pre-fill scan");
                    return;
                }

                var processes = new List<RunningProcessInfo>();
                foreach (var process in Process.GetProcesses())
                {
                    using (process)
                    {
                        try
                        {
                            processes.Add(new RunningProcessInfo(
                                process.Id,
                                process.ProcessName,
                                GetRealProcessPath(process.Id)));
                        }
                        catch
                        {
                            // Process may exit while being enumerated; skip it.
                        }
                    }
                }

                int prefillCount = 0;
                foreach (var proc in processes)
                {
                    if (!policy.Includes(proc.ProcessName))
                    {
                        continue;
                    }

                    var fullPath = proc.Path;
                    if (string.IsNullOrWhiteSpace(fullPath))
                    {
                        // Even if we can't get the live path, prefill with the process name
                        // so that we have at least some record. ETW stop will still fall back.
                        _logger?.LogWarning(
                            "Pre-fill unable to resolve full path for running process {ProcessName} (PID: {ProcessId})",
                            proc.ProcessName, proc.Id);
                    }
                    else
                    {
                        _startPathCache[proc.Id] = fullPath;
                        prefillCount++;
                    }
                }

                if (prefillCount > 0)
                {
                    _logger?.LogInformation(
                        "Pre-filled {Count} running process entries into ETW path cache",
                        prefillCount);
                }
                else
                {
                    _logger?.LogDebug("No matching running processes found to pre-fill");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to pre-fill running process path cache");
            }
        }

        private readonly record struct RunningProcessInfo(int Id, string ProcessName, string? Path);

        private void SafeCleanup(bool preservePathCache = false)
        {
            _isRunning = false;
            if (!preservePathCache)
            {
                _startPathCache.Clear();
            }

            // 1) Break the processing loop so the thread exits Process() first
            if (_session != null)
            {
                try
                {
                    _session.Source.StopProcessing();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error stopping ETW source processing");
                }
            }

            // 2) Unsubscribe from events
            if (_session != null)
            {
                try
                {
                    _session.Source.Kernel.ProcessStart -= OnProcessStart;
                    _session.Source.Kernel.ProcessStop -= OnProcessStop;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error unsubscribing from ETW events");
                }
            }

            // 3) Stop and dispose the session
            if (_session != null)
            {
                try
                {
                    _session.Stop();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error stopping ETW session");
                }

                try
                {
                    _session.Dispose();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error disposing ETW session");
                }

                _session = null;
            }

            // 4) Wait for the processing thread to finish
            if (_processingThread != null)
            {
                try
                {
                    if (_processingThread.IsAlive)
                    {
                        _processingThread.Join(TimeSpan.FromSeconds(5));
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error waiting for ETW processing thread to complete");
                }

                _processingThread = null;
            }
        }
    }
}
