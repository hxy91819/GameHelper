using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using GameHelper.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace GameHelper.Infrastructure.Controllers
{
    /// <summary>
    /// Windows-specific HDR controller that detects monitor support and toggles HDR via the Win+Alt+B shortcut.
    /// </summary>
    public sealed class WindowsHdrController : IHdrController
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

        private readonly ILogger<WindowsHdrController> _logger;
        private readonly object _sync = new();

        private bool _isHdrSupported;
        private bool _isHdrEnabled;
        private bool _loggedUnsupported;

        public WindowsHdrController(ILogger<WindowsHdrController> logger)
        {
            _logger = logger;

            if (!OperatingSystem.IsWindows())
            {
                _logger.LogInformation("HDR controller disabled: non-Windows platform detected.");
                return;
            }

            RefreshState();
        }

        /// <inheritdoc />
        public bool IsEnabled => _isHdrSupported && _isHdrEnabled;

        /// <inheritdoc />
        public void Enable() => Toggle(desiredState: true, reason: "HDR requested by active game(s)");

        /// <inheritdoc />
        public void Disable() => Toggle(desiredState: false, reason: "No HDR-enabled games active");

        private void Toggle(bool desiredState, string reason)
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            lock (_sync)
            {
                RefreshState();

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

        private void LogUnsupportedOnce()
        {
            if (_loggedUnsupported)
            {
                return;
            }

            _loggedUnsupported = true;
            _logger.LogInformation("System HDR is not supported; skipping HDR automation.");
        }

        private bool WaitForDesiredState(bool desiredState)
        {
            for (var attempt = 0; attempt < MaxStatePollingAttempts; attempt++)
            {
                Thread.Sleep(StatePollingDelayMs);
                RefreshState();

                if (_isHdrSupported && _isHdrEnabled == desiredState)
                {
                    _logger.LogInformation("HDR state confirmed {State}.", desiredState ? "enabled" : "disabled");
                    return true;
                }
            }

            // Final refresh to capture the latest state even on failure.
            RefreshState();
            return _isHdrSupported && _isHdrEnabled == desiredState;
        }

        private void RefreshState()
        {
            if (!OperatingSystem.IsWindows())
            {
                _isHdrSupported = false;
                _isHdrEnabled = false;
                return;
            }

            try
            {
                var displays = EnumerateDisplays();
                var supported = false;
                var enabled = false;

                foreach (var info in displays)
                {
                    if (!info.Supported)
                    {
                        continue;
                    }

                    supported = true;
                    if (info.Enabled)
                    {
                        enabled = true;
                        break;
                    }
                }

                _isHdrSupported = supported;
                _isHdrEnabled = enabled;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query HDR state via DisplayConfig APIs.");
                _isHdrSupported = false;
                _isHdrEnabled = false;
            }
        }

        private IReadOnlyList<DisplayAdvancedColorInfo> EnumerateDisplays()
        {
            var result = new List<DisplayAdvancedColorInfo>();

            var status = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount);
            if (status != ErrorSuccess || pathCount == 0)
            {
                return result;
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = modeCount > 0 ? new DISPLAYCONFIG_MODE_INFO[modeCount] : Array.Empty<DISPLAYCONFIG_MODE_INFO>();

            status = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (status != ErrorSuccess)
            {
                return result;
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
                    continue;
                }

                result.Add(new DisplayAdvancedColorInfo(info.AdvancedColorSupported, info.AdvancedColorEnabled));
            }

            return result;
        }

        private static bool TrySendToggleHotkey()
        {
            var inputs = new[]
            {
                CreateKeyInput(VkLwin, true, extended: true),
                CreateKeyInput(VkMenu, true, extended: true),
                CreateKeyInput(VkB, true),
                CreateKeyInput(VkB, false),
                CreateKeyInput(VkMenu, false, extended: true),
                CreateKeyInput(VkLwin, false, extended: true)
            };

            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            return sent == (uint)inputs.Length;
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

        #region Native interop

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray, ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

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

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
            public uint modeInfo1;
            public uint modeInfo2;
            public uint modeInfo3;
            public uint modeInfo4;
            public uint modeInfo5;
            public uint modeInfo6;
            public uint modeInfo7;
            public uint modeInfo8;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public DISPLAYCONFIG_DEVICE_INFO_TYPE type;
            public uint size;
            public LUID adapterId;
            public uint id;
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

        private readonly record struct DisplayAdvancedColorInfo(bool Supported, bool Enabled);

        #endregion
    }
}
