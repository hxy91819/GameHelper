using System;
using System.IO;
using System.Runtime.InteropServices;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameHelper.Infrastructure.Controllers;

public sealed class WindowsSpeedEngine : ISpeedEngine
{
    private readonly ILogger<WindowsSpeedEngine> _logger;
    private readonly string _libraryPath;

    private nint _libraryHandle;
    private AttachDelegate? _attach;
    private SetSpeedDelegate? _setSpeed;
    private DetachDelegate? _detach;

    public WindowsSpeedEngine(ILogger<WindowsSpeedEngine> logger)
    {
        _logger = logger;
        _libraryPath = Path.Combine(AppContext.BaseDirectory, "native", "win-x64", "GameHelper.Speedhack.dll");
        EnsureLoaded();
    }

    public bool IsSupported { get; private set; }

    public string UnavailableReason { get; private set; } = "Windows 倍速引擎未初始化。";

    public SpeedOperationResult ApplySpeed(int processId, double multiplier)
    {
        if (!EnsureLoaded())
        {
            return new SpeedOperationResult { Success = false, Message = UnavailableReason };
        }

        try
        {
            var attachCode = _attach!(processId);
            if (attachCode != 0)
            {
                return new SpeedOperationResult
                {
                    Success = false,
                    Message = $"附加进程失败，错误码 {attachCode}。"
                };
            }

            var setCode = _setSpeed!(processId, multiplier);
            return new SpeedOperationResult
            {
                Success = setCode == 0,
                Message = setCode == 0
                    ? $"已设置为 {multiplier:0.##}x"
                    : $"设置倍率失败，错误码 {setCode}。"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "设置倍速失败：PID={ProcessId}", processId);
            return new SpeedOperationResult
            {
                Success = false,
                Message = ex.Message
            };
        }
    }

    public SpeedOperationResult ResetSpeed(int processId)
    {
        if (!EnsureLoaded())
        {
            return new SpeedOperationResult { Success = false, Message = UnavailableReason };
        }

        try
        {
            var setCode = _setSpeed!(processId, 1.0d);
            if (setCode != 0)
            {
                return new SpeedOperationResult
                {
                    Success = false,
                    Message = $"恢复倍率失败，错误码 {setCode}。"
                };
            }

            var detachCode = _detach!(processId);
            return new SpeedOperationResult
            {
                Success = detachCode == 0,
                Message = detachCode == 0
                    ? "已恢复为 1.0x"
                    : $"恢复后分离失败，错误码 {detachCode}。"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "恢复倍速失败：PID={ProcessId}", processId);
            return new SpeedOperationResult
            {
                Success = false,
                Message = ex.Message
            };
        }
    }

    public void Dispose()
    {
        if (_libraryHandle != nint.Zero)
        {
            NativeLibrary.Free(_libraryHandle);
            _libraryHandle = nint.Zero;
        }
    }

    private bool EnsureLoaded()
    {
        if (IsSupported)
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            UnavailableReason = "当前平台不支持 Windows 倍速引擎。";
            return false;
        }

        if (!File.Exists(_libraryPath))
        {
            UnavailableReason = $"未找到原生倍速组件：{_libraryPath}";
            return false;
        }

        try
        {
            _libraryHandle = NativeLibrary.Load(_libraryPath);
            _attach = LoadDelegate<AttachDelegate>("gh_speedhack_attach");
            _setSpeed = LoadDelegate<SetSpeedDelegate>("gh_speedhack_set_speed");
            _detach = LoadDelegate<DetachDelegate>("gh_speedhack_detach");
            IsSupported = true;
            UnavailableReason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载原生倍速组件失败。");
            UnavailableReason = $"加载原生倍速组件失败：{ex.Message}";
            return false;
        }
    }

    private T LoadDelegate<T>(string exportName) where T : Delegate
    {
        var export = NativeLibrary.GetExport(_libraryHandle, exportName);
        return Marshal.GetDelegateForFunctionPointer<T>(export);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AttachDelegate(int processId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetSpeedDelegate(int processId, double multiplier);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DetachDelegate(int processId);
}
