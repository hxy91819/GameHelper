using System;
using System.Threading;

namespace GameHelper.ConsoleHost.Utilities
{
    internal static class ProcessInstanceGuard
    {
        private static Mutex? _mutex;

        public static bool TryClaim()
        {
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

                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    _mutex?.ReleaseMutex();
                    _mutex?.Dispose();
                    _mutex = null;
                };

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
    }
}
