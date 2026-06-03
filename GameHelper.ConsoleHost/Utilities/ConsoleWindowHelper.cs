using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace GameHelper.ConsoleHost.Utilities
{
    internal static class ConsoleWindowHelper
    {
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int SW_SHOWMINNOACTIVE = 7;

        private const int CTRL_CLOSE_EVENT = 2;
        private const int CTRL_LOGOFF_EVENT = 5;
        private const int CTRL_SHUTDOWN_EVENT = 6;

        // [DECISION] WS_EX_APPWINDOW is the extended style bit that tells
        // the taskbar to show a button for this window, even when the
        // window is hidden via ShowWindow(SW_HIDE). A bare ShowWindow(SW_HIDE)
        // is therefore not enough to remove the taskbar entry — we must also
        // clear WS_EX_APPWINDOW in the same step. WS_EX_TOOLWINDOW is the
        // counterpart style that hides a window from the taskbar / Alt-Tab
        // even when visible, and is what we set on Hide.
        private const int GWL_EXSTYLE = -20;
        private const uint WS_EX_APPWINDOW = 0x00040000;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate? handler, bool add);

        [DllImport("user32.dll", EntryPoint = "IsWindowVisible", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisibleNative(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private delegate bool ConsoleCtrlDelegate(int ctrlType);

        private static ConsoleCtrlDelegate? _ctrlHandler;
        private static volatile Func<bool>? _onCloseRequested;

        public static void CreateConsole()
        {
            if (!OperatingSystem.IsWindows()) return;

            AllocConsole();
        }

        public static IntPtr GetHandle()
        {
            return GetConsoleWindow();
        }

        public static bool IsWindowVisible(IntPtr hWnd)
        {
            if (!OperatingSystem.IsWindows()) return false;
            return IsWindowVisibleNative(hWnd);
        }

        public static void Show()
        {
            var hwnd = GetHandle();
            if (hwnd == IntPtr.Zero) return;

            // Restore the "real application window" extended style so the
            // taskbar entry comes back when the user un-hides us.
            ToggleTaskbarPresence(hwnd, showInTaskbar: true);
            ShowWindow(hwnd, SW_SHOW);
        }

        public static void Hide()
        {
            var hwnd = GetHandle();
            if (hwnd == IntPtr.Zero) return;

            // Step 1: drop WS_EX_APPWINDOW and add WS_EX_TOOLWINDOW so the
            // taskbar removes the button. ShowWindow(SW_HIDE) alone does
            // not accomplish this — the shell keeps the taskbar entry as
            // long as WS_EX_APPWINDOW is set.
            ToggleTaskbarPresence(hwnd, showInTaskbar: false);
            // Step 2: hide the window from the screen.
            ShowWindow(hwnd, SW_HIDE);
        }

        public static void Minimize()
        {
            var hwnd = GetHandle();
            if (hwnd == IntPtr.Zero) return;

            ShowWindow(hwnd, SW_SHOWMINNOACTIVE);
        }

        // [DECISION] A previous attempt to subclass the conhost window's
        // WndProc (so title-bar minimize routed to Hide) was removed:
        // the conhost window lives in conhost.exe, a different process,
        // and SetWindowLongPtr(GWL_WNDPROC) on a foreign HWND returns
        // 0 unconditionally. There is no in-process hook available for
        // the conhost window.
        //
        // The user's "minimize → hide to tray" requirement is still
        // satisfied because the tray-mode flow in Program.cs calls
        // Hide() immediately after the tray icon is created, so the
        // conhost window is never shown to the user. There is no
        // visible title bar to click "minimize" on. If a future change
        // surfaces the conhost (e.g. a "Show console" menu item),
        // re-introduce the hook with a SetWindowsHookEx(WH_CALLWNDPROC)
        // + injected DLL approach, or replace the conhost with a
        // WinForms Form whose WndProc we can subclass.

        private static void ToggleTaskbarPresence(IntPtr hwnd, bool showInTaskbar)
        {
            var current = GetWindowLongWrapper(hwnd, GWL_EXSTYLE).ToInt64() & 0xFFFFFFFFL;
            uint newStyle = showInTaskbar
                ? ((uint)current | WS_EX_APPWINDOW) & ~WS_EX_TOOLWINDOW
                : ((uint)current & ~WS_EX_APPWINDOW) | WS_EX_TOOLWINDOW;
            SetWindowLongWrapper(hwnd, GWL_EXSTYLE, (IntPtr)(uint)newStyle);
        }

        private static IntPtr GetWindowLongWrapper(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(hWnd, nIndex)
                : GetWindowLongPtr32(hWnd, nIndex);
        }

        private static IntPtr SetWindowLongWrapper(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
                : SetWindowLongPtr32(hWnd, nIndex, dwNewLong);
        }

        public static bool InstallCloseHandler(Func<bool> onCloseRequested)
        {
            RemoveCloseHandler();
            _onCloseRequested = onCloseRequested;
            var handler = new ConsoleCtrlDelegate(ConsoleCtrlHandler);
            if (SetConsoleCtrlHandler(handler, true))
            {
                _ctrlHandler = handler;
                return true;
            }
            _onCloseRequested = null;
            return false;
        }

        public static void RemoveCloseHandler()
        {
            var old = Interlocked.Exchange(ref _ctrlHandler, null);
            if (old != null)
            {
                SetConsoleCtrlHandler(old, false);
            }
            _onCloseRequested = null;
        }

        private static bool ConsoleCtrlHandler(int ctrlType)
        {
            if (ctrlType == CTRL_CLOSE_EVENT || ctrlType == CTRL_LOGOFF_EVENT || ctrlType == CTRL_SHUTDOWN_EVENT)
            {
                _onCloseRequested?.Invoke();
                return true;
            }
            return false;
        }
    }
}