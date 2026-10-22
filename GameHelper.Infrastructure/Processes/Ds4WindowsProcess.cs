using System.ComponentModel;
using System.Diagnostics;

namespace GameHelper.Infrastructure.Processes;

/// <summary>Starts the configured controller companion once, reusing only an exact executable-path match.</summary>
internal static class Ds4WindowsProcess
{
    private static readonly object Gate = new();

    internal static void EnsureRunning(string executable)
    {
        lock (Gate)
        {
            if (IsRunning(executable)) return;
            using var process = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)!,
                WindowStyle = ProcessWindowStyle.Minimized
            }) ?? throw new InvalidOperationException("未能启动 DS4Windows。");
            try
            {
                var idle = process.WaitForInputIdle(5000);
                if (process.HasExited)
                {
                    if (IsRunning(executable)) return;
                    throw new InvalidOperationException("DS4Windows 启动后已退出，请确认状态后重试。");
                }
                if (!idle) throw new InvalidOperationException("DS4Windows 尚未完成启动，请确认状态后重试。");
            }
            catch (InvalidOperationException) when (process.HasExited && IsRunning(executable)) { }
        }
    }

    private static bool IsRunning(string executable)
    {
        var candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable));
        try
        {
            foreach (var process in candidates)
            {
                try
                {
                    if (!process.HasExited && process.MainModule?.FileName is { } path
                        && Path.GetFullPath(path).Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (InvalidOperationException) { /* Process exited while being inspected. */ }
                catch (Win32Exception ex)
                { throw new InvalidOperationException("无法确认已运行的 DS4Windows 路径，请检查进程权限后重试。", ex); }
            }
            return false;
        }
        finally { foreach (var process in candidates) process.Dispose(); }
    }
}
