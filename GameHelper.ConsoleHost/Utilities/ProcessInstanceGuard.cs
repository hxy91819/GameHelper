using System;
using System.Threading;

namespace GameHelper.ConsoleHost.Utilities;

internal static class ProcessInstanceGuard
{
    private const string DisableSingleInstanceEnvironmentVariable = "GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE";
    private const string AllowMultipleInstancesEnvironmentVariable = "GAMEHELPER_ALLOW_MULTIPLE_INSTANCES";

    private static Mutex? _mutex;
    private static Mutex? _sandboxMutex;

    public static bool TryClaim()
    {
        if (IsSingleInstanceDisabledByEnvironment())
        {
            return TryClaimSandboxSlot();
        }

        if (_mutex != null)
        {
            return true;
        }

        var name = GetMutexName();

        try
        {
            _mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
            if (!createdNew)
            {
                _mutex.Dispose();
                _mutex = null;
                return false;
            }

            AppDomain.CurrentDomain.ProcessExit += (_, _) => Release();

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch
        {
            // Fail open on unexpected errors to avoid blocking startup completely.
            return true;
        }
    }

    /// <summary>
    /// 沙盒/测试实例仍然只允许一个在跑：跳过全局单实例互斥是为了和用户实例共存，
    /// 不是为了允许无限多开。需要并行跑多个测试实例时显式设置
    /// <c>GAMEHELPER_ALLOW_MULTIPLE_INSTANCES=1</c>。
    /// </summary>
    private static bool TryClaimSandboxSlot()
    {
        if (IsMultipleInstancesAllowedByEnvironment())
        {
            return true;
        }

        if (_sandboxMutex != null)
        {
            return true;
        }

        try
        {
            _sandboxMutex = new Mutex(initiallyOwned: true, GetSandboxMutexName(), out var createdNew);
            if (!createdNew)
            {
                _sandboxMutex.Dispose();
                _sandboxMutex = null;
                return false;
            }

            AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseSandboxSlot();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 只探测是否已有实例在运行，不占用互斥体。供启动早期的宿主转移判断使用。
    /// </summary>
    internal static bool IsAnotherInstanceRunning()
    {
        if (IsSingleInstanceDisabledByEnvironment())
        {
            return false;
        }

        try
        {
            if (!Mutex.TryOpenExisting(GetMutexName(), out var existing))
            {
                return false;
            }

            existing.Dispose();
            return true;
        }
        catch
        {
            // 无法判断时按"没有其他实例"处理，交给后续 TryClaim 决定。
            return false;
        }
    }

    private static string GetMutexName()
        => OperatingSystem.IsWindows() ? @"Global\GameHelper.ConsoleHost" : "GameHelper.ConsoleHost";

    private static string GetSandboxMutexName()
        => OperatingSystem.IsWindows() ? @"Global\GameHelper.ConsoleHost.Sandbox" : "GameHelper.ConsoleHost.Sandbox";

    internal static bool IsSingleInstanceDisabledByEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(DisableSingleInstanceEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsMultipleInstancesAllowedByEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(AllowMultipleInstancesEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static void Release()
    {
        ReleaseMutex(ref _mutex);
        ReleaseSandboxSlot();
    }

    private static void ReleaseSandboxSlot() => ReleaseMutex(ref _sandboxMutex);

    private static void ReleaseMutex(ref Mutex? target)
    {
        var mutex = Interlocked.Exchange(ref target, null);
        if (mutex == null)
        {
            return;
        }

        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // This can occur if the releasing thread does not own the mutex. Swallow to avoid crashing on exit.
        }
        finally
        {
            mutex.Dispose();
        }
    }
}
