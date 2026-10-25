using System.ComponentModel;
using System.Diagnostics;

namespace GameHelper.Infrastructure.Processes;

/// <summary>Applies the controller companion state, reusing exact executable paths and stopping DS4Windows when disabled.</summary>
internal static class Ds4WindowsProcess
{
    private static readonly object Gate = new();

    internal static void EnsureRunning(string executable)
    {
        lock (Gate)
        {
            if (IsRunning(executable)) return;
            using var process = Process.Start(CreateStartInfo(executable)) ?? throw new InvalidOperationException("未能启动 DS4Windows。");
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

    internal static void StopRunning()
    {
        lock (Gate)
        {
            using var current = Process.GetCurrentProcess();
            var sessionId = current.SessionId;
            var candidates = Process.GetProcessesByName("DS4Windows");
            var failures = new List<Exception>();
            try
            {
                foreach (var process in candidates)
                {
                    try
                    {
                        // Elevated monitoring must not stop another Windows login session's companion.
                        if (process.HasExited || process.SessionId != sessionId) continue;
                        var sandbox = Environment.GetEnvironmentVariable(GameHelper.Core.Utilities.AppDataPath.DataDirectoryEnvironmentVariable);
                        if (!string.IsNullOrWhiteSpace(sandbox))
                        {
                            // A sandbox must never close the user's real controller companion.
                            string? path;
                            try { path = process.MainModule?.FileName; }
                            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { continue; }
                            if (!IsInsideSandbox(path, sandbox)) continue;
                        }
                        StopProcess(process);
                    }
                    catch (InvalidOperationException) when (process.HasExited) { }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or TimeoutException)
                    { failures.Add(ex); }
                }
            }
            finally { foreach (var process in candidates) process.Dispose(); }
            if (failures.Count > 0) throw new InvalidOperationException(
                "未能关闭 DS4Windows，请检查进程权限或手动退出后重试。", new AggregateException(failures));
        }
    }

    internal static bool IsInsideSandbox(string? path, string sandbox) => path is not null
        && Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(sandbox))
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static void StopProcess(Process process)
    {
        try
        {
            if (process.HasExited) return;
            if (process.CloseMainWindow() && process.WaitForExit(1500)) return;
            if (process.HasExited) return;
            process.Kill();
            if (!process.WaitForExit(3000)) throw new TimeoutException("DS4Windows 未能及时退出。");
        }
        catch (InvalidOperationException) when (process.HasExited) { }
        catch (Win32Exception) when (process.HasExited) { }
    }

    internal static ProcessStartInfo CreateStartInfo(string executable) => new(executable)
    {
        UseShellExecute = false, CreateNoWindow = true,
        WorkingDirectory = Path.GetDirectoryName(executable)!, WindowStyle = ProcessWindowStyle.Hidden
    };

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
