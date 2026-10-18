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

    /// <summary>
    /// 冷却窗口：构造期探测失败后（真实宿主里出现过一次枚举拿零句柄、
    /// 而普通探针上下文一切正常的环境差异），IsAvailable 按此间隔重试而不是永久判死。
    /// </summary>
    private const int AvailabilityReProbeCooldownMs = 10_000;

    private const uint CoInitApartmentThreaded = 0x2;
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    private const int PhysicalMonitorDescriptionLength = 128;

    private readonly ILogger<DdcMonitorModeController> _logger;
    private readonly object _busLock = new();
    private readonly object _availabilityLock = new();
    private bool _detectedAvailable;
    private int _lastAvailabilityProbeTick;

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

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr rect, MonitorEnumProc callback, IntPtr data);

    [DllImport("dxva2.dll")]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint count);

    [DllImport("dxva2.dll")]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, IntPtr monitorsBuffer);

    [DllImport("dxva2.dll")]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte vcpCode, ref uint sourceType, out uint currentValue, out uint maximumValue);

    [DllImport("dxva2.dll")]
    private static extern bool SetVCPFeature(IntPtr hMonitor, byte vcpCode, uint newValue);

    [DllImport("dxva2.dll")]
    private static extern bool DestroyPhysicalMonitors(uint count, IntPtr monitorsBuffer);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    /// <summary>
    /// dxva2 的监视器配置服务依赖线程 COM 上下文；宿主线程的（未初始化或其他 Apartment）
    /// COM 状态会让 GetPhysicalMonitorsFromHMONITOR 成功返回却写零句柄，因此每个 DDC/CI
    /// 操作都在专属 STA + 显式 CoInitializeEx 的线程上执行——等价于实测始终可用的
    /// PowerShell STA 环境，同时把总线延迟从宿主线程解耦。
    /// </summary>
    private T RunOnSta<T>(Func<T> operation)
    {
        T result = default!;
        Exception? error = null;
        var completed = new ManualResetEventSlim(false);
        var worker = new Thread(() =>
        {
            try
            {
                var initResult = CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded);
                try
                {
                    result = operation();
                }
                finally
                {
                    if (initResult is 0 or 1) // S_OK or S_FALSE: we own the init.
                    {
                        CoUninitialize();
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                completed.Set();
            }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.IsBackground = true;
        worker.Name = "GameHelper.DdcStaOp";
        worker.Start();
        completed.Wait();
        if (error is not null)
        {
            throw error;
        }

        return result!;
    }

    /// <summary>
    /// Live physical-monitor handle session. Handles are captured from a flat
    /// native-layout buffer (HANDLE + WCHAR[128] description per slot) rather than a
    /// marshalled struct array: on .NET 8 the struct-array copy-back zeroed the handle
    /// field while the description came back correct, which made every probe look like
    /// "no controllable monitor". Disposing releases the handles via DestroyPhysicalMonitors.
    /// </summary>
    private sealed class PhysicalMonitorSession : IDisposable
    {
        private IntPtr[] _handles;
        private readonly uint _count;
        private readonly int _slotSize;
        private bool _disposed;

        public PhysicalMonitorSession(IntPtr[] handles, string description, uint count, int slotSize)
        {
            _handles = handles;
            Description = description;
            _count = count;
            _slotSize = slotSize;
        }

        public IntPtr Handle => _handles.Length > 0 ? _handles[0] : IntPtr.Zero;

        public string Description { get; }

        public bool IsValid => !_disposed && _handles.Length > 0 && _handles[0] != IntPtr.Zero;

        /// <summary>Session with no handles; used on enumeration failure paths.</summary>
        public static PhysicalMonitorSession Invalid() => new(Array.Empty<IntPtr>(), string.Empty, 0, IntPtr.Size + PhysicalMonitorDescriptionLength * 2);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var handles = Interlocked.Exchange(ref _handles, Array.Empty<IntPtr>());
            if (handles is null || handles.Length == 0)
            {
                return;
            }

            var bufferSize = (int)((uint)handles.Length * (uint)_slotSize);
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                Marshal.Copy(new byte[bufferSize], 0, buffer, bufferSize);
                for (var index = 0; index < handles.Length; index++)
                {
                    Marshal.WriteIntPtr(buffer, index * _slotSize, handles[index]);
                }

                // Best-effort cleanup: a single failure here does not block the rest of cleanup.
                DestroyPhysicalMonitors((uint)handles.Length, buffer);
            }
            catch (Exception)
            {
                // Handles are also reclaimed when the process exits.
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    public DdcMonitorModeController(ILogger<DdcMonitorModeController> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _detectedAvailable = false;

        if (!OperatingSystem.IsWindows())
        {
            _lastAvailabilityProbeTick = Environment.TickCount;
            _logger.LogInformation("DDC/CI monitor preset automation requires Windows; controller disabled.");
            return;
        }

        var probe = RunOnSta(ProbeControllableMonitor);
        _detectedAvailable = probe.Available;
        _lastAvailabilityProbeTick = Environment.TickCount;
        if (_detectedAvailable)
        {
            _logger.LogInformation("DDC/CI monitor preset automation controller ready ({Monitor}).", probe.Description);
        }
        else
        {
            _logger.LogInformation(
                "No DDC/CI controllable monitor detected yet; retrying every {CooldownSeconds}s from caller threads.",
                AvailabilityReProbeCooldownMs / 1000);
        }
    }

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            lock (_availabilityLock)
            {
                // 构造期探测失败时按冷却间隔重试：宿主环境下首次枚举拿零句柄的原因
                // 与普通进程上下文不同，但后续线程上重试可以成功。
                if (Environment.TickCount - _lastAvailabilityProbeTick < AvailabilityReProbeCooldownMs)
                {
                    return _detectedAvailable;
                }

                var probe = RunOnSta(ProbeControllableMonitor);
                _detectedAvailable = probe.Available;
                _lastAvailabilityProbeTick = Environment.TickCount;
                if (_detectedAvailable)
                {
                    _logger.LogInformation("DDC/CI monitor became available ({Monitor}); preset automation enabled.", probe.Description);
                }
                else
                {
                    _logger.LogDebug("DDC/CI monitor still not detectable; will retry after cooldown.");
                }

                return _detectedAvailable;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<byte, uint>? TryReadState(IReadOnlyList<MonitorModeWrite> probeCodes)
    {
        if (!OperatingSystem.IsWindows() || probeCodes is null || probeCodes.Count == 0)
        {
            return null;
        }

        try
        {
            return RunOnSta(() =>
            {
                lock (_busLock)
                {
                    return TryReadStateCore(probeCodes);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DDC/CI monitor state read failed; treated as unreadable.");
            return null;
        }
    }

    private IReadOnlyDictionary<byte, uint>? TryReadStateCore(IReadOnlyList<MonitorModeWrite> probeCodes)
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

    /// <inheritdoc />
    public void Apply(IReadOnlyList<MonitorModeWrite> writes)
    {
        if (!OperatingSystem.IsWindows() || writes is null || writes.Count == 0)
        {
            return;
        }

        try
        {
            RunOnSta(() =>
            {
                lock (_busLock)
                {
                    ApplyCore(writes);
                }

                return true;
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DDC/CI preset apply failed on the STA worker.");
        }
    }

    private void ApplyCore(IReadOnlyList<MonitorModeWrite> writes)
    {
        using var session = AcquireFirstPhysicalMonitor();
        if (!session.IsValid)
        {
            _logger.LogWarning("No controllable monitor available; preset recipe skipped.");
            return;
        }

        var allWritten = true;
        for (var index = 0; index < writes.Count; index++)
        {
            var write = writes[index];
            if (!SetVCPFeature(session.Handle, write.Code, write.Value))
            {
                allWritten = false;
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
        if (allWritten) _logger.LogInformation(
            "DDC/CI preset recipe applied to {Monitor}: {Writes}.",
            session.Description,
            string.Join(", ", writes));
    }

    /// <summary>Probes for a controllable monitor; runs on the STA worker via <see cref="RunOnSta{T}"/>.</summary>
    private (bool Available, string Description) ProbeControllableMonitor()
    {
        using var session = AcquireFirstPhysicalMonitor();
        return (session.IsValid, session.Description);
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
            _logger.LogWarning("EnumDisplayMonitors failed (win32 error {Error}); no controllable monitor this round.", Marshal.GetLastWin32Error());
            return PhysicalMonitorSession.Invalid();
        }

        // Native PHYSICAL_MONITOR slot layout: HANDLE followed by WCHAR[128] description.
        var slotSize = IntPtr.Size + PhysicalMonitorDescriptionLength * 2;

        foreach (var hMonitor in handles)
        {
            var count = 0u;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, ref count) || count == 0)
            {
                continue;
            }

            var bufferSize = checked((int)(count * (uint)slotSize));
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, buffer))
                {
                    _logger.LogWarning(
                        "GetPhysicalMonitorsFromHMONITOR failed (win32 error {Error}); DDC/CI operation skipped.",
                        Marshal.GetLastWin32Error());
                    continue;
                }

                var monitorHandles = new IntPtr[count];
                for (var index = 0; index < count; index++)
                {
                    monitorHandles[index] = Marshal.ReadIntPtr(buffer, index * slotSize);
                }

                var slotChars = new char[PhysicalMonitorDescriptionLength];
                for (var charIndex = 0; charIndex < slotChars.Length; charIndex++)
                {
                    slotChars[charIndex] = (char)Marshal.ReadInt16(buffer, IntPtr.Size + charIndex * 2);
                }

                var description = new string(slotChars);
                var terminator = description.IndexOf('\0');
                description = (terminator >= 0 ? description[..terminator] : description).Trim();

                if (monitorHandles[0] == IntPtr.Zero)
                {
                    // Defensive: the native layer reported success but handed back no handle.
                    var dump = new byte[Math.Min(32, bufferSize)];
                    Marshal.Copy(buffer, dump, 0, dump.Length);
                    _logger.LogWarning(
                        "Physical monitor enumeration returned a null handle for slot 0 (hMonitor=0x{H:X}, count={Count}, bytes={Bytes}); DDC/CI operation skipped.",
                        hMonitor,
                        count,
                        BitConverter.ToString(dump));
                    continue;
                }

                return new PhysicalMonitorSession(monitorHandles, description, count, slotSize);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        _logger.LogWarning("No physical monitor handle could be acquired; DDC/CI operation skipped.");
        return PhysicalMonitorSession.Invalid();
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
