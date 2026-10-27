using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameHelper.Infrastructure.Controllers;

/// <summary>
/// Windows HDR control: selected DisplayConfig targets when configured, otherwise the Win+Alt+B shortcut.
/// </summary>
public sealed class WindowsHdrController : IHdrController, IPrimaryDisplayHdrController
{
    private const uint QdcOnlyActivePaths = 0x00000002;
    private const uint ErrorSuccess = 0;
    private const int MaxStatePollingAttempts = 6;
    private const int StatePollingDelayMs = 500;

    private const ushort VkLwin = 0x5B;
    private const ushort VkMenu = 0x12;
    private const ushort VkB = 0x42;
    private const uint InputKeyboard = 1;
    private const uint KeyeventfExtendedkey = 0x0001;
    private const uint KeyeventfKeyup = 0x0002;

    private const uint KeyeventfKeybdEventKeyup = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private readonly ILogger<WindowsHdrController> _logger;
    private readonly object _sync = new();
    private readonly Func<IReadOnlyList<DisplayAdvancedColorInfo>> _queryDisplays;

    private readonly bool _useNativeQueries;
    private readonly Action<DisplayAdvancedColorInfo, bool> _setDisplayState;
    private readonly Action _pollDelay;
    private HdrSettings _settings = new();
    private IReadOnlyList<DisplayAdvancedColorInfo> _displays = Array.Empty<DisplayAdvancedColorInfo>();
    private readonly HashSet<string> _enabledDisplayPaths = new(StringComparer.OrdinalIgnoreCase);

    private bool _isHdrSupported;
    private bool _isHdrEnabled;
    private bool _isHdrForceDisabled;
    private bool _loggedUnsupported;
    private bool _loggedForceDisabled;
    private bool? _lastLoggedSupported;
    private bool? _lastLoggedEnabled;
    private bool? _lastLoggedForceDisabled;

    public WindowsHdrController(ILogger<WindowsHdrController> logger)
        : this(logger, null)
    {
    }

    internal WindowsHdrController(ILogger<WindowsHdrController> logger,
        Func<IReadOnlyList<DisplayAdvancedColorInfo>>? queryDisplays,
        Action<DisplayAdvancedColorInfo, bool>? setDisplayState = null, Action? pollDelay = null)
    {
        _logger = logger;
        _useNativeQueries = queryDisplays is null;
        _queryDisplays = queryDisplays ?? EnumerateDisplays;
        _setDisplayState = setDisplayState ?? SetDisplayState;
        _pollDelay = pollDelay ?? (() => Thread.Sleep(StatePollingDelayMs));

        if (_useNativeQueries && !OperatingSystem.IsWindows())
        {
            _logger.LogInformation("HDR controller disabled: non-Windows platform detected.");
            return;
        }

        RefreshState();
    }

    /// <inheritdoc />
    public bool IsEnabled { get { lock (_sync) return _isHdrEnabled; } }

    /// <inheritdoc />
    public bool TryGetEnabled(out bool enabled)
    {
        lock (_sync)
        {
            var known = RefreshState();
            enabled = _isHdrEnabled;
            return known;
        }
    }

    /// <inheritdoc />
    public void Configure(HdrSettings? settings)
    {
        settings?.Validate();
        lock (_sync)
        {
            _settings = new HdrSettings
            {
                AllowedDisplays = settings?.AllowedDisplays.ToList() ?? new(),
                DisabledDisplays = settings?.DisabledDisplays.ToList() ?? new()
            };
        }
    }

    /// <inheritdoc />
    public HdrAutomationAvailability GetAutomationAvailability()
    {
        lock (_sync)
        {
            if (!RefreshState()) return HdrAutomationAvailability.Unknown;
            if (_displays.Any(d => d.Supported && !d.ForceDisabled && Allows(d)))
                return HdrAutomationAvailability.Available;
            // Only a fully excluded online display set is a deliberate policy skip. An allowed
            // display that is online but hardware-unsupported/force-disabled is Unavailable,
            // even when an excluded HDR display is connected at the same time.
            if (_settings.HasDisplayRules() && _displays.Count > 0 && _displays.All(d => !Allows(d)))
                return HdrAutomationAvailability.ExcludedByPolicy;
            return HdrAutomationAvailability.Unavailable;
        }
    }

    /// <inheritdoc />
    public bool TryGetDisplays(out IReadOnlyList<HdrDisplayInfo> displays)
    {
        lock (_sync)
        {
            if (!RefreshState()) { displays = Array.Empty<HdrDisplayInfo>(); return false; }
            displays = _displays.Select(d => new HdrDisplayInfo(d.Name, d.DevicePath,
                d.Supported, d.Enabled, d.ForceDisabled, Allows(d))).ToArray();
            return true;
        }
    }

    public TrayHdrState ReadPrimary()
    {
        lock (_sync)
        {
            if (!RefreshState()) return new(null, false, "无法读取显示器");
            var primary = _displays.Where(d => d.Primary).ToArray();
            if (primary.Length != 1) return new(null, false, "无法唯一识别主显示器");
            var display = primary[0];
            return new(display.Enabled, display.Supported && !display.ForceDisabled,
                display.Supported && !display.ForceDisabled ? null : "主显示器不支持 HDR");
        }
    }

    public void TogglePrimary()
    {
        lock (_sync)
        {
            var state = ReadPrimary();
            if (!state.CanToggle) throw new InvalidOperationException(state.Reason);
            var display = _displays.Single(d => d.Primary);
            var desired = !state.Enabled!.Value;
            // Manual changes never enter the automation rollback target set.
            _setDisplayState(display, desired);
            for (var attempt = 0; attempt < MaxStatePollingAttempts; attempt++)
            {
                _pollDelay();
                var updated = ReadPrimary();
                if (updated.Enabled == desired && updated.CanToggle) return;
            }
            throw new InvalidOperationException("未能确认主显示器的 HDR 状态。");
        }
    }

    private bool Allows(DisplayAdvancedColorInfo display) => _settings.Allows(display.Name, display.DevicePath);

    /// <inheritdoc />
    public void Enable() => Toggle(desiredState: true, reason: "HDR requested by active game(s)");

    /// <inheritdoc />
    public void Disable() => Toggle(desiredState: false, reason: "No HDR-enabled games active");

    private void Toggle(bool desiredState, string reason)
    {
        if (_useNativeQueries && !OperatingSystem.IsWindows())
        {
            return;
        }

        lock (_sync)
        {
            if (!RefreshState()) return;

            if (_settings.HasDisplayRules() || _enabledDisplayPaths.Count > 0)
            {
                SetSelectedDisplays(desiredState);
                return;
            }

            if (_isHdrForceDisabled)
            {
                LogForceDisabledOnce();
                return;
            }

            if (!_isHdrSupported)
            {
                LogUnsupportedOnce();
                return;
            }

            if (_isHdrEnabled == desiredState)
            {
                _logger.LogDebug("HDR already {State}; no toggle required ({Reason}).", desiredState ? "enabled" : "disabled", reason);
                return;
            }

            _logger.LogInformation("{Action} HDR via Win+Alt+B ({Reason}).", desiredState ? "Enabling" : "Disabling", reason);

            if (!TrySendToggleHotkey())
            {
                _logger.LogWarning("Failed to issue HDR toggle hotkey.");
                return;
            }

            if (!WaitForDesiredState(desiredState))
            {
                _logger.LogWarning("HDR state did not transition to {DesiredState} after shortcut invocation.", desiredState ? "enabled" : "disabled");
            }
        }
    }

    private void SetSelectedDisplays(bool desiredState)
    {
        // After a runtime restart only the aggregate scheduler lease survives. Restore
        // enabled targets in that case, preserving the existing system-wide lease semantics.
        var targets = _displays.Where(d => d.Supported && !d.ForceDisabled &&
            (desiredState ? Allows(d) : ShouldRestore(d))).ToArray();
        foreach (var display in targets.Where(d => d.Enabled != desiredState))
        {
            // Record before writing so a partial failure remains eligible for rollback,
            // even if a subsequent config reload excludes this target.
            if (desiredState) _enabledDisplayPaths.Add(display.DevicePath);
            _logger.LogInformation("Setting HDR on display '{Display}' to {Enabled}.", display.Name, desiredState);
            _setDisplayState(display, desiredState);
        }
        if (targets.Length == 0) return;
        for (var attempt = 0; attempt < MaxStatePollingAttempts; attempt++)
        {
            _pollDelay();
            if (!RefreshState()) continue;
            if (targets.All(target => _displays.Any(d => d.DevicePath == target.DevicePath && d.Enabled == desiredState)))
            {
                if (!desiredState) _enabledDisplayPaths.Clear();
                return;
            }
        }
        throw new InvalidOperationException("未能确认所选显示器的 HDR 状态。");
    }

    private bool ShouldRestore(DisplayAdvancedColorInfo display) => _enabledDisplayPaths.Count > 0
        ? _enabledDisplayPaths.Contains(display.DevicePath) : display.Enabled;

    private static void SetDisplayState(DisplayAdvancedColorInfo display, bool enabled)
    {
        var request = new DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE>(),
                adapterId = new LUID { LowPart = display.AdapterLow, HighPart = display.AdapterHigh },
                id = display.TargetId
            },
            value = enabled ? 1u : 0u
        };
        var status = DisplayConfigSetDeviceInfo(ref request);
        if (status != ErrorSuccess) throw new Win32Exception(status, "Cannot set HDR for the selected display.");
    }

    private void LogUnsupportedOnce()
    {
        if (_loggedUnsupported)
        {
            return;
        }

        _loggedUnsupported = true;
        _logger.LogInformation("System HDR is not supported; skipping HDR automation.");
    }

    private void LogForceDisabledOnce()
    {
        if (_loggedForceDisabled)
        {
            return;
        }

        _loggedForceDisabled = true;
        _logger.LogWarning("Windows reports HDR as force-disabled; skipping HDR automation.");
    }

    private bool WaitForDesiredState(bool desiredState)
    {
        for (var attempt = 0; attempt < MaxStatePollingAttempts; attempt++)
        {
            Thread.Sleep(StatePollingDelayMs);
            if (!RefreshState()) continue;

            if (_isHdrForceDisabled)
            {
                LogForceDisabledOnce();
                return false;
            }

            if (_isHdrSupported && _isHdrEnabled == desiredState)
            {
                _logger.LogInformation("HDR state confirmed {State}.", desiredState ? "enabled" : "disabled");
                return true;
            }
        }

        // Final refresh to capture the latest state even on failure.
        if (!RefreshState()) return false;

        if (_isHdrForceDisabled)
        {
            LogForceDisabledOnce();
            return false;
        }

        return _isHdrSupported && _isHdrEnabled == desiredState;
    }

    private bool RefreshState()
    {
        if (_useNativeQueries && !OperatingSystem.IsWindows())
        {
            _isHdrSupported = false;
            _isHdrEnabled = false;
            _isHdrForceDisabled = false;
            return true;
        }

        try
        {
            var displays = _queryDisplays();
            _displays = displays;
            _isHdrSupported = displays.Any(d => d.Supported && !d.ForceDisabled);
            // Observe ALL active displays, including excluded and force-disabled ones.
            _isHdrEnabled = displays.Any(d => d.Enabled);
            if (!_isHdrEnabled) _enabledDisplayPaths.Clear();
            _isHdrForceDisabled = displays.Any(d => d.ForceDisabled) && !_isHdrSupported;

            if (ShouldLogSummary())
            {
                LogDisplaySummary(displays);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query HDR state via DisplayConfig APIs.");
            // Unknown is not an observed transition to SDR. Retain last-known state for
            // HDR ownership/rollback; TryGetEnabled separately reports that this read failed.
            return false;
        }
    }

    private IReadOnlyList<DisplayAdvancedColorInfo> EnumerateDisplays()
    {
        var result = new List<DisplayAdvancedColorInfo>();

        var status = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount);
        if (status != ErrorSuccess)
        {
            throw new Win32Exception((int)status, "Cannot query active display buffer sizes.");
        }
        if (pathCount == 0)
        {
            return result;
        }

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = modeCount > 0 ? new DISPLAYCONFIG_MODE_INFO[modeCount] : Array.Empty<DISPLAYCONFIG_MODE_INFO>();

        status = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (status != ErrorSuccess)
        {
            throw new Win32Exception((int)status, "Cannot query active display paths.");
        }

        for (var i = 0; i < pathCount; i++)
        {
            var info = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(),
                    adapterId = paths[i].targetInfo.adapterId,
                    id = paths[i].targetInfo.id
                }
            };

            status = DisplayConfigGetDeviceInfo(ref info);
            if (status != ErrorSuccess)
            {
                throw new Win32Exception((int)status, "Cannot determine advanced color state for an active display.");
            }

            var targetName = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                    adapterId = paths[i].targetInfo.adapterId,
                    id = paths[i].targetInfo.id
                }
            };

            status = DisplayConfigGetDeviceInfo(ref targetName);
            if (status != ErrorSuccess && _settings.HasDisplayRules())
                throw new Win32Exception((int)status, "Cannot identify an active display for HDR selection.");
            var name = status == ErrorSuccess ? GetDisplayName(targetName) : $"Display {i + 1}";

            result.Add(new DisplayAdvancedColorInfo(
                name,
                info.AdvancedColorSupported,
                info.AdvancedColorEnabled,
                info.AdvancedColorForceDisabled,
                targetName.monitorDevicePath ?? string.Empty,
                paths[i].targetInfo.adapterId.LowPart,
                paths[i].targetInfo.adapterId.HighPart,
                paths[i].targetInfo.id,
                paths[i].sourceInfo.modeInfoIdx < modeCount &&
                modes[paths[i].sourceInfo.modeInfoIdx].infoType == 1 &&
                modes[paths[i].sourceInfo.modeInfoIdx].sourceX == 0 &&
                modes[paths[i].sourceInfo.modeInfoIdx].sourceY == 0));
        }

        return result;
    }

    private bool TrySendToggleHotkey()
    {
        if (TrySendInputHotkey())
        {
            return true;
        }

        _logger.LogDebug("SendInput API failed; falling back to keybd_event sequence.");

        return TrySendKeybdEventHotkey();
    }

    private bool TrySendInputHotkey()
    {
        var inputs = new[]
        {
            CreateKeyInput(VkLwin, true),
            CreateKeyInput(VkMenu, true),
            CreateKeyInput(VkB, true),
            CreateKeyInput(VkB, false),
            CreateKeyInput(VkMenu, false),
            CreateKeyInput(VkLwin, false)
        };

        var size = Marshal.SizeOf<INPUT>();
        var sent = SendInput((uint)inputs.Length, inputs, size);
        if (sent == (uint)inputs.Length)
        {
            return true;
        }

        var batchError = Marshal.GetLastWin32Error();
        _logger.LogDebug("SendInput batch submission failed with error {Error}; attempting sequential submission.", batchError);

        for (var index = 0; index < inputs.Length; index++)
        {
            var singleSent = SendInput(1, new[] { inputs[index] }, size);
            if (singleSent != 1)
            {
                var stepError = Marshal.GetLastWin32Error();
                _logger.LogDebug("SendInput sequential submission failed at step {Index} with error {Error}.", index, stepError);
                return false;
            }

            Thread.Sleep(30);
        }

        return true;
    }

    private bool TrySendKeybdEventHotkey()
    {
        try
        {
            keybd_event((byte)VkLwin, 0, 0, UIntPtr.Zero);
            keybd_event((byte)VkMenu, 0, 0, UIntPtr.Zero);
            keybd_event((byte)VkB, 0, 0, UIntPtr.Zero);

            Thread.Sleep(50);

            keybd_event((byte)VkB, 0, KeyeventfKeybdEventKeyup, UIntPtr.Zero);
            keybd_event((byte)VkMenu, 0, KeyeventfKeybdEventKeyup, UIntPtr.Zero);
            keybd_event((byte)VkLwin, 0, KeyeventfKeybdEventKeyup, UIntPtr.Zero);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "keybd_event fallback failed to issue HDR toggle.");
            return false;
        }
    }

    private static INPUT CreateKeyInput(ushort key, bool keyDown, bool extended = false)
    {
        var flags = extended ? KeyeventfExtendedkey : 0u;
        if (!keyDown)
        {
            flags |= KeyeventfKeyup;
        }

        return new INPUT
        {
            type = InputKeyboard,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = key,
                    wScan = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };
    }

    private static string GetDisplayName(in DISPLAYCONFIG_TARGET_DEVICE_NAME name)
    {
        if (!string.IsNullOrWhiteSpace(name.monitorFriendlyDeviceName))
        {
            return name.monitorFriendlyDeviceName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(name.monitorDevicePath))
        {
            return name.monitorDevicePath.Trim();
        }

        return "Unknown Display";
    }

    private bool ShouldLogSummary()
    {
        if (_lastLoggedSupported != _isHdrSupported ||
            _lastLoggedEnabled != _isHdrEnabled ||
            _lastLoggedForceDisabled != _isHdrForceDisabled)
        {
            _lastLoggedSupported = _isHdrSupported;
            _lastLoggedEnabled = _isHdrEnabled;
            _lastLoggedForceDisabled = _isHdrForceDisabled;
            return true;
        }

        return false;
    }

    private void LogDisplaySummary(IReadOnlyList<DisplayAdvancedColorInfo> displays)
    {
        if (displays.Count == 0)
        {
            _logger.LogInformation("No active display paths were reported; HDR will be treated as unsupported.");
            return;
        }

        foreach (var display in displays)
        {
            var status = display.Supported
                ? (display.Enabled ? "HDR supported and currently enabled" : "HDR supported but currently disabled")
                : "HDR not supported";

            if (display.ForceDisabled)
            {
                status += " (Windows reports the feature as force-disabled)";
            }

            _logger.LogInformation("Display '{Display}': {Status}.", display.Name, status);
        }

        _logger.LogInformation(
            "HDR detection summary: Supported={Supported}, Enabled={Enabled}, ForceDisabled={ForceDisabled}.",
            _isHdrSupported,
            _isHdrEnabled,
            _isHdrForceDisabled);
    }

    #region Native interop

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray, ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE requestPacket);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private enum DISPLAYCONFIG_DEVICE_INFO_TYPE : uint
    {
        DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1,
        DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2,
        DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_PREFERRED_MODE = 3,
        DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME = 4,
        DISPLAYCONFIG_DEVICE_INFO_SET_TARGET_PERSISTENCE = 5,
        DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_BASE_TYPE = 6,
        DISPLAYCONFIG_DEVICE_INFO_GET_SUPPORT_VIRTUAL_RESOLUTION = 7,
        DISPLAYCONFIG_DEVICE_INFO_SET_SUPPORT_VIRTUAL_RESOLUTION = 8,
        DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9,
        DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE = 10,
        DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL = 11
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    // The native union includes a 48-byte DISPLAYCONFIG_TARGET_MODE after the
    // 16-byte header. QueryDisplayConfig writes 64 bytes per array element.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)]
        public uint infoType;
        [FieldOffset(4)]
        public uint id;
        [FieldOffset(8)]
        public LUID adapterId;
        [FieldOffset(28)]
        public int sourceX;
        [FieldOffset(32)]
        public int sourceY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public DISPLAYCONFIG_DEVICE_INFO_TYPE type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        private readonly uint _value;
        public readonly uint colorEncoding;
        public readonly uint bitsPerColorChannel;

        public bool AdvancedColorSupported => (_value & 0x1) == 0x1;
        public bool AdvancedColorEnabled => (_value & 0x2) == 0x2;
        public bool WideColorEnforced => (_value & 0x4) == 0x4;
        public bool AdvancedColorForceDisabled => (_value & 0x8) == 0x8;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    internal readonly record struct DisplayAdvancedColorInfo(string Name, bool Supported, bool Enabled, bool ForceDisabled,
        string DevicePath = "", uint AdapterLow = 0, int AdapterHigh = 0, uint TargetId = 0, bool Primary = false);

    #endregion
}
