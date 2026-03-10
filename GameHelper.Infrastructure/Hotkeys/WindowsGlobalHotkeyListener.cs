using System;
using System.Runtime.InteropServices;
using System.Threading;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameHelper.Infrastructure.Hotkeys;

public sealed class WindowsGlobalHotkeyListener : IGlobalHotkeyListener
{
    private const uint WmHotkey = 0x0312;
    private const uint WmQuit = 0x0012;
    private const int HotkeyId = 0x4748;
    private const int StartupTimeoutMs = 5000;
    private const int ShutdownTimeoutMs = 5000;

    private readonly ILogger<WindowsGlobalHotkeyListener> _logger;
    private readonly IWindowsHotkeyPlatform _platform;
    private readonly object _sync = new();

    private Thread? _thread;
    private uint _threadId;
    private bool _registered;

    public WindowsGlobalHotkeyListener(ILogger<WindowsGlobalHotkeyListener> logger)
        : this(logger, new NativeWindowsHotkeyPlatform())
    {
    }

    public WindowsGlobalHotkeyListener(ILogger<WindowsGlobalHotkeyListener> logger, IWindowsHotkeyPlatform platform)
    {
        _logger = logger;
        _platform = platform;
    }

    public bool IsListening { get; private set; }

    public HotkeyRegistrationResult Start(HotkeyBinding binding, Action onTriggered)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(onTriggered);

        lock (_sync)
        {
            Stop();

            if (!OperatingSystem.IsWindows() && _platform is NativeWindowsHotkeyPlatform)
            {
                return new HotkeyRegistrationResult
                {
                    Success = false,
                    Message = "当前平台不支持全局热键监听。"
                };
            }

            var startupSignal = new ManualResetEventSlim(false);
            HotkeyRegistrationResult? result = null;
            _thread = new Thread(() =>
            {
                try
                {
                    _threadId = _platform.GetCurrentThreadId();
                    _platform.EnsureMessageQueue();

                    var modifiers = (uint)(binding.Modifiers | HotkeyModifiers.NoRepeat);
                    _registered = _platform.RegisterHotKey(nint.Zero, HotkeyId, modifiers, binding.VirtualKey);
                    if (!_registered)
                    {
                        var errorCode = Marshal.GetLastWin32Error();
                        result = new HotkeyRegistrationResult
                        {
                            Success = false,
                            Message = $"热键 '{binding.DisplayText}' 注册失败，可能已被占用。Win32Error={errorCode}"
                        };
                        startupSignal.Set();
                        return;
                    }

                    result = new HotkeyRegistrationResult
                    {
                        Success = true,
                        Message = $"热键已注册：{binding.DisplayText}"
                    };
                    startupSignal.Set();

                    while (true)
                    {
                        var messageResult = _platform.GetMessage(out var message);
                        if (messageResult <= 0)
                        {
                            break;
                        }

                        if (message.Message == WmHotkey && (int)message.WParam == HotkeyId)
                        {
                            onTriggered();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "全局热键监听线程异常退出。");
                    result ??= new HotkeyRegistrationResult
                    {
                        Success = false,
                        Message = $"热键监听异常退出：{ex.Message}"
                    };
                    startupSignal.Set();
                }
                finally
                {
                    if (_registered)
                    {
                        _platform.UnregisterHotKey(nint.Zero, HotkeyId);
                    }

                    _registered = false;
                    IsListening = false;
                }
            })
            {
                IsBackground = true,
                Name = "GameHelper.GlobalHotkey"
            };

            _thread.Start();
            if (!startupSignal.Wait(StartupTimeoutMs))
            {
                return new HotkeyRegistrationResult
                {
                    Success = false,
                    Message = $"热键 '{binding.DisplayText}' 注册超时。"
                };
            }

            result ??= new HotkeyRegistrationResult
            {
                Success = false,
                Message = "热键注册失败。"
            };
            IsListening = result.Success;
            return result;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_thread is null)
            {
                return;
            }

            if (_threadId != 0)
            {
                _platform.PostThreadMessage(_threadId, WmQuit, 0, 0);
            }

            if (!_thread.Join(ShutdownTimeoutMs))
            {
                _logger.LogWarning("全局热键监听线程未能在超时时间内退出。");
            }

            _thread = null;
            _threadId = 0;
            _registered = false;
            IsListening = false;
        }
    }
}
