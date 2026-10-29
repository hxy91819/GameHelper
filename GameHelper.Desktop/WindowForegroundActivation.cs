using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace GameHelper.Desktop;

/// <summary>Restores a launch destination and its keyboard focus without changing system foreground policy.</summary>
internal static class WindowForegroundActivation
{
    internal static bool Restore(Window window, IInputElement? focusTarget = null)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var foreground = TryForeground(handle);
        if (foreground)
        {
            window.Focus();
            if (focusTarget is UIElement element && (!element.IsVisible || !element.IsEnabled || !element.Focusable))
                focusTarget = null;
            Keyboard.Focus(focusTarget ?? window);
        }
        return foreground;
    }

    private static bool TryForeground(nint handle)
    {
        if (GetForegroundWindow() == handle) return true;
        var currentThread = GetCurrentThreadId();
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var attached = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
            SetActiveWindow(handle);
            SetFocus(handle);
            return GetForegroundWindow() == handle;
        }
        finally { if (attached) AttachThreadInput(currentThread, foregroundThread, false); }
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachThreadInput(uint first, uint second, bool attach);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BringWindowToTop(nint window);
    [DllImport("user32.dll")] private static extern nint SetActiveWindow(nint window);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
}
