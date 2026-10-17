using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameHelper.Infrastructure.Controllers;

/// <summary>
/// Real monitor preset controller that talks to the monitor over the DDC/CI channel
/// (the I2C side channel carried by HDMI/DP/USB-C) using the Windows dxva2 (MCCS) APIs.
/// </summary>
/// <remarks>
/// All bus operations are serialized inside this class because the physical DDC wire is
/// shared. Every operation acquires fresh physical-monitor handles and releases them in
/// a best-effort <c>finally</c>; a handle is only valid between enumeration and destroy.
/// </remarks>
public sealed class DdcMonitorModeController : IMonitorModeController
{
    private const int ReadAttempts = 2;
    private const int ReadRetryDelayMs = 50;
    private const int WriteSettleDelayMs = 400;

    private const int PhysicalMonitorDescriptionLength = 128;

    private readonly ILogger<DdcMonitorModeController> _logger;
    private readonly object _busLock = new();

    /// <summary>Native monitor-enum callback.</summary>
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = PhysicalMonitorDescriptionLength)]
        public string Description;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr rect, MonitorEnumProc callback, IntPtr data);

    [DllImport("dxva2.dll")]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint count);

    [DllImport("dxva2.dll")]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll")]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte vcpCode, ref uint sourceType, out uint currentValue, out uint maximumValue);

    [DllImport("dxva2.dll")]
    private static extern bool SetVCPFeature(IntPtr hMonitor, byte vcpCode, uint newValue);

    [DllImport("dxva2.dll")]
    private static extern bool DestroyPhysicalMonitors(uint count, [In] PHYSICAL_MONITOR[] monitors);

    /// <summary>
    /// A live physical-monitor handle session. Disposing releases the native handles;
    /// <see cref="IsValid"/> is false when enumeration failed this round.
    /// </summary>
    private sealed class PhysicalMonitorSession : IDisposable
    {
        private PHYSICAL_MONITOR[]? _monitors;
        private readonly uint _count;

        public PhysicalMonitorSession(PHYSICAL_MONITOR[] monitors, uint count)
        {
            _monitors = monitors;
            _count = count;
            Handle = monitors[0].hPhysicalMonitor;
            Description = (monitors[0].Description ?? string.Empty).Trim();
        }

        public static PhysicalMonitorSession Empty { get; } = new(Array.Empty<PHYSICAL_MONITOR>(), 0);

        public IntPtr Handle { get; }

        public string Description { get; }

        public bool IsValid => Handle != IntPtr.Zero && _monitors is not null;

        public void Dispose()
        {
            var monitors = Interlocked.Exchange(ref _monitors, null);
            if (monitors is null)
            {
                return;
            }

            try
            {
                // Best-effort cleanup: a single failure here does not block the rest of cleanup.
                DestroyPhysicalMonitors(_count, monitors);
            }
            catch (Exception)
            {
                // Handles are also reclaimed when the process exits.
            }
        }
    }

    public DdcMonitorModeController(ILogger<DdcMonitorModeController> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        IsAvailable = false;

        if (!OperatingSystem.IsWindows())
        {
            _logger.LogInformation("DDC/CI monitor preset automation requires Windows; controller disabled.");
            return;
        }

        using var session = AcquireFirstPhysicalMonitor();
        IsAvailable = session.IsValid;
        if (IsAvailable)
        {
            _logger.LogInformation("DDC/CI monitor preset automation controller ready ({Monitor}).", session.Description);
        }
        else
        {
            _logger.LogInformation("No DDC/CI controllable monitor detected; monitor preset automation will be skipped.");
        }
    }

    /// <inheritdoc />
    public bool IsAvailable { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<byte, uint>? TryReadState(IReadOnlyList<MonitorModeWrite> probeCodes)
    {
        if (!OperatingSystem.IsWindows() || probeCodes is null || probeCodes.Count == 0)
        {
            return null;
        }

        lock (_busLock)
        {
            using var session = AcquireFirstPhysicalMonitor();
            if (!session.IsValid)
            {
                return null;
            }

            var state = new Dictionary<byte, uint>(probeCodes.Count);
            foreach (var probe in probeCodes)
            {
                if (!TryReadCode(session.Handle, probe.Code, out var value))
                {
                    _logger.LogWarning(
                        "Failed to read VCP 0x{Code:X2} from {Monitor}; monitor state detection aborted.",
                        probe.Code,
                        session.Description);
                    return null;
                }

                state[probe.Code] = value;
            }

            return state;
        }
    }

    /// <inheritdoc />
    public void Apply(IReadOnlyList<MonitorModeWrite> writes)
    {
        if (!OperatingSystem.IsWindows() || writes is null || writes.Count == 0)
        {
            return;
        }

        lock (_busLock)
        {
            using var session = AcquireFirstPhysicalMonitor();
            if (!session.IsValid)
            {
                _logger.LogWarning("No controllable monitor available; preset recipe skipped.");
                return;
            }

            for (var index = 0; index < writes.Count; index++)
            {
                var write = writes[index];
                if (!SetVCPFeature(session.Handle, write.Code, write.Value))
                {
                    _logger.LogWarning(
                        "Failed to write VCP 0x{Code:X2}=0x{Value:X2} to {Monitor} ({Index}/{Total}).",
                        write.Code,
                        write.Value,
                        session.Description,
                        index + 1,
                        writes.Count);
                }

                if (index < writes.Count - 1)
                {
                    // Some monitors only accept the next preset value after settling the previous one.
                    Thread.Sleep(WriteSettleDelayMs);
                }
            }

            Thread.Sleep(WriteSettleDelayMs);
            _logger.LogInformation(
                "DDC/CI preset recipe applied to {Monitor}: {Writes}.",
                session.Description,
                string.Join(", ", writes));
        }
    }

    private PhysicalMonitorSession AcquireFirstPhysicalMonitor()
    {
        var handles = new List<IntPtr>();
        var enumProc = new MonitorEnumProc((IntPtr hMonitor, IntPtr hdcMonitor, ref RECT rect, IntPtr data) =>
        {
            handles.Add(hMonitor);
            return true;
        });

        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, enumProc, IntPtr.Zero))
        {
            _logger.LogWarning("EnumDisplayMonitors failed; no controllable monitor this round.");
            return PhysicalMonitorSession.Empty;
        }

        foreach (var hMonitor in handles)
        {
            var count = 0u;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, ref count) || count == 0)
            {
                continue;
            }

            var monitors = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
            {
                continue;
            }

            return new PhysicalMonitorSession(monitors, count);
        }

        _logger.LogWarning("No physical monitor handle could be acquired; DDC/CI operation skipped.");
        return PhysicalMonitorSession.Empty;
    }

    private static bool TryReadCode(IntPtr handle, byte code, out uint value)
    {
        value = 0;
        for (var attempt = 0; attempt < ReadAttempts; attempt++)
        {
            var sourceType = 0u;
            if (GetVCPFeatureAndVCPFeatureReply(handle, code, ref sourceType, out value, out _))
            {
                return true;
            }

            Thread.Sleep(ReadRetryDelayMs);
        }

        return false;
    }
}