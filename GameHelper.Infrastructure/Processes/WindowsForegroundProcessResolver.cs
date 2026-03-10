using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameHelper.Infrastructure.Processes;

public sealed class WindowsForegroundProcessResolver : IForegroundProcessResolver
{
    private readonly ILogger<WindowsForegroundProcessResolver> _logger;

    public WindowsForegroundProcessResolver(ILogger<WindowsForegroundProcessResolver> logger)
    {
        _logger = logger;
    }

    public ForegroundProcessInfo? GetForegroundProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var handle = GetForegroundWindow();
            if (handle == nint.Zero)
            {
                return null;
            }

            GetWindowThreadProcessId(handle, out var processId);
            if (processId == 0)
            {
                return null;
            }

            using var process = Process.GetProcessById((int)processId);
            string? path = null;
            string executableName;

            try
            {
                path = process.MainModule?.FileName;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "读取前台进程路径失败：PID={ProcessId}", processId);
            }

            executableName = !string.IsNullOrWhiteSpace(path)
                ? Path.GetFileName(path)
                : process.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? process.ProcessName
                    : process.ProcessName + ".exe";

            return new ForegroundProcessInfo((int)processId, executableName, path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "解析前台进程失败。");
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
}
