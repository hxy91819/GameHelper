using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GameHelper.Infrastructure.Notifications;

/// <summary>Hidden top-level window owned and disposed on its message-pump thread.</summary>
internal sealed class NativeMessageWindow : IDisposable
{
    private readonly string _className;
    private readonly IntPtr _instance;
    private readonly WindowProcedure _procedure;
    private readonly Action<Exception> _cleanupFailed;
    private ushort _windowClass;
    internal IntPtr Handle { get; private set; }

    internal NativeMessageWindow(string className, string title, WindowProcedure procedure, Action<Exception> cleanupFailed)
    {
        _className = className;
        _procedure = procedure; // Root the unmanaged callback until window/class cleanup finishes.
        _cleanupFailed = cleanupFailed;
        _instance = GetModuleHandle(null);
        try
        {
            var registration = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = _procedure,
                Instance = _instance, ClassName = _className
            };
            _windowClass = RegisterClassEx(ref registration);
            if (_windowClass == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            // Keep a top-level parent: the tray needs Explorer's TaskbarCreated broadcast.
            Handle = CreateWindowEx(0, _className, title, 0,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
            if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { Dispose(); throw; }
    }

    internal static void PumpMessages()
    {
        int result;
        while ((result = GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
        if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        SafeCleanup(() => { if (Handle != IntPtr.Zero) DestroyWindow(Handle); });
        Handle = IntPtr.Zero;
        SafeCleanup(() => { if (_windowClass != 0) UnregisterClass(_className, _instance); });
        _windowClass = 0;
        GC.KeepAlive(_procedure);
    }

    private void SafeCleanup(Action cleanup)
    {
        try { cleanup(); }
        catch (Exception ex) { _cleanupFailed(ex); }
    }

    public delegate IntPtr WindowProcedure(IntPtr window, uint message, nuint wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public WindowProcedure Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public nuint WParam;
        public IntPtr LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? module);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass registration);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint extended, string className, string name, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] public static extern IntPtr DefWindowProc(IntPtr window, uint message, nuint wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
}
