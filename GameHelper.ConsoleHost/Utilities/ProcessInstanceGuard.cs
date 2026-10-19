using System;
using System.Threading;

namespace GameHelper.ConsoleHost.Utilities
{
    internal static class ProcessInstanceGuard
    {
        private const string DisableSingleInstanceEnvironmentVariable = "GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE";

        private static Mutex? _mutex;

        public static bool TryClaim()
        {
            if (IsSingleInstanceDisabledByEnvironment())
            {
                return true;
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

        internal static bool IsSingleInstanceDisabledByEnvironment()
        {
            var value = Environment.GetEnvironmentVariable(DisableSingleInstanceEnvironmentVariable);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static void Release()
        {
            var mutex = Interlocked.Exchange(ref _mutex, null);
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
}
