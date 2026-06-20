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

            var name = OperatingSystem.IsWindows()
                ? @"Global\GameHelper.ConsoleHost"
                : "GameHelper.ConsoleHost";

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
