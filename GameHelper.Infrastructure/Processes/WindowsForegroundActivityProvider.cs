using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameHelper.Infrastructure.Processes;

/// <summary>Samples foreground identity without input hooks; secure desktops and suspend time are breaks.</summary>
public sealed class WindowsForegroundActivityProvider : IForegroundActivityProvider
{
    private readonly object _gate = new();
    private readonly IProcessPathResolver _paths;
    private readonly ILogger<WindowsForegroundActivityProvider> _logger;
    private (int Pid, long Started)? _cachedIdentity;
    private ProcessEventInfo _cachedProcess;
    private ulong? _lastUptime;
    private ulong _lastAwake;

    public WindowsForegroundActivityProvider(IProcessPathResolver paths, ILogger<WindowsForegroundActivityProvider> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public ForegroundActivity Read()
    {
        lock (_gate)
        {
            var suspended = ReadSuspendDuration();
            var desktop = NativeMethods.OpenInputDesktop(0, false, 1);
            if (desktop == IntPtr.Zero)
            {
                _cachedIdentity = null;
                // A locked/UAC desktop is inaccessible to an ordinary desktop process.
                return Marshal.GetLastWin32Error() == 5
                    ? ForegroundActivity.Away with { SuspendedFor = suspended }
                    : ForegroundActivity.Unavailable with { SuspendedFor = suspended };
            }
            try
            {
                var name = new StringBuilder(256);
                if (!NativeMethods.GetUserObjectInformation(desktop, 2, name, 512, out _))
                    return ForegroundActivity.Unavailable with { SuspendedFor = suspended };
                if (!string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase))
                {
                    _cachedIdentity = null;
                    return ForegroundActivity.Away with { SuspendedFor = suspended };
                }
            }
            finally { NativeMethods.CloseDesktop(desktop); }

            var window = NativeMethods.GetForegroundWindow();
            if (window == IntPtr.Zero || NativeMethods.GetWindowThreadProcessId(window, out var pid) == 0)
            {
                _cachedIdentity = null;
                return ForegroundActivity.Unavailable with { SuspendedFor = suspended };
            }
            try
            {
                using var process = Process.GetProcessById(checked((int)pid));
                var started = process.StartTime.ToUniversalTime().Ticks;
                var identity = (process.Id, started);
                if (_cachedIdentity != identity)
                {
                    _cachedIdentity = null;
                    var processName = process.ProcessName + ".exe";
                    _cachedProcess = new ProcessEventInfo(processName, _paths.TryResolveExecutablePath(process.Id), process.Id);
                    _cachedIdentity = identity;
                }
                if (NativeMethods.GetForegroundWindow() != window || process.HasExited)
                    return ForegroundActivity.Unavailable with { SuspendedFor = suspended };
                return new ForegroundActivity(true, _cachedProcess, started, SuspendedFor: suspended);
            }
            catch (Exception ex)
            {
                _cachedIdentity = null;
                _logger.LogDebug(ex, "Cannot read foreground process {ProcessId}", pid);
                return ForegroundActivity.Unavailable with { SuspendedFor = suspended };
            }
        }
    }

    private TimeSpan ReadSuspendDuration()
    {
        var uptime = NativeMethods.GetTickCount64();
        if (!NativeMethods.QueryUnbiasedInterruptTime(out var awake)) { _lastUptime = null; return TimeSpan.Zero; }
        long suspendedTicks = 0;
        if (_lastUptime is { } previous && uptime >= previous && awake >= _lastAwake)
            suspendedTicks = (long)((uptime - previous) * TimeSpan.TicksPerMillisecond) - (long)(awake - _lastAwake);
        _lastUptime = uptime;
        _lastAwake = awake;
        // Uptime has a coarser resolution than unbiased interrupt time.
        return suspendedTicks > TimeSpan.TicksPerMillisecond * 250 ? TimeSpan.FromTicks(suspendedTicks) : TimeSpan.Zero;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
        [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, uint length, out uint needed);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("kernel32.dll")]
        public static extern ulong GetTickCount64();
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryUnbiasedInterruptTime(out ulong time);
    }
}
