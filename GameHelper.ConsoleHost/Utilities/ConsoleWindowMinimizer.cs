using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace GameHelper.ConsoleHost.Utilities
{
    /// <summary>
    /// 把当前控制台窗口收进任务栏而不是抢到前台：用于自启动这类"后台拉起"的场景。
    /// 控制台宿主会在首屏输出后重新显示窗口，因此这里在限定时间内反复纠正，
    /// 而不是只调用一次 ShowWindow。
    /// </summary>
    internal static class ConsoleWindowMinimizer
    {
        private const uint GaRootOwner = 3;
        private const int SwShowMinNoActive = 7;
        private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(150);

        public static bool TryMinimizeCurrentWindow(TimeSpan duration)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            var deadline = DateTime.UtcNow + duration;
            var minimized = false;
            do
            {
                try
                {
                    var window = ResolveWindow();
                    // 只处理已经显示的窗口：隐藏启动（例如部署校验的负向用例）不能被重新显示出来。
                    if (window != IntPtr.Zero && IsWindowVisible(window))
                    {
                        if (!IsIconic(window))
                        {
                            _ = ShowWindow(window, SwShowMinNoActive);
                        }

                        minimized |= IsIconic(window);
                    }
                }
                catch
                {
                    // 窗口尚未就绪或宿主拒绝该操作时继续纠正，超时后放弃。
                }

                if (DateTime.UtcNow >= deadline)
                {
                    break;
                }

                Thread.Sleep(RetryInterval);
            }
            while (true);

            return minimized;
        }

        private static IntPtr ResolveWindow()
        {
            var window = GetConsoleWindow();
            if (window == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var rootWindow = GetAncestor(window, GaRootOwner);
            return rootWindow != IntPtr.Zero ? rootWindow : window;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);
    }
}
