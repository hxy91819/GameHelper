using System.ComponentModel;
using System.Runtime.InteropServices;
using GameHelper.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace GameHelper.Infrastructure.Notifications;

/// <summary>Native system notifications with transient tray icons, usable without Windows App SDK registration.</summary>
public sealed class WindowsBreakReminderNotifier : IBreakReminderNotifier
{
    private const uint CallbackMessage = 0x8001;
    private const uint ShowMessage = 0x8002;
    private const uint ClearMessage = 0x8003;
    private readonly object _gate = new();
    private readonly ILogger<WindowsBreakReminderNotifier> _logger;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly NativeMethods.WindowProcedure _procedure;
    private readonly string _className = "GameHelper.BreakReminder." + Guid.NewGuid().ToString("N");
    private Thread? _thread;
    private IntPtr _window;
    private Exception? _startupError;
    private bool _disposed;
    // The following fields belong exclusively to the native window thread.
    private uint _iconId;
    private bool _hasIcon;

    public WindowsBreakReminderNotifier(ILogger<WindowsBreakReminderNotifier> logger)
    {
        _logger = logger;
        _procedure = HandleMessage;
    }

    internal bool HasWindow => Volatile.Read(ref _window) != IntPtr.Zero;

    public void Show(TimeSpan playtime, int breakMinutes, bool sound)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureWindow();
            var text = $"已连续游戏{(long)playtime.TotalMinutes}分钟，建议休息{breakMinutes}分钟。";
            var request = GCHandle.Alloc(text);
            try
            {
                if (NativeMethods.SendMessage(_window, ShowMessage, sound ? (nuint)1 : 0, GCHandle.ToIntPtr(request)) == IntPtr.Zero)
                    throw new InvalidOperationException("Windows could not submit the break notification.");
            }
            finally { request.Free(); }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_window != IntPtr.Zero) NativeMethods.SendMessage(_window, ClearMessage, 0, IntPtr.Zero);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_window != IntPtr.Zero) NativeMethods.SendMessage(_window, 0x0010, 0, IntPtr.Zero);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to close notification window"); }
            try
            {
                if (_thread is not null && !_thread.Join(TimeSpan.FromSeconds(5)))
                    _logger.LogWarning("Notification thread did not exit within five seconds");
            }
            finally
            {
                // Do not release the startup event while a late-starting thread could still signal it.
                if (_thread is null || !_thread.IsAlive) _ready.Dispose();
            }
        }
    }

    private void EnsureWindow()
    {
        if (_window != IntPtr.Zero) return;
        if (_thread is null || !_thread.IsAlive)
        {
            _ready.Reset();
            _startupError = null;
            _thread = new Thread(RunWindow) { IsBackground = true, Name = "GameHelper break notifications" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
        if (!_ready.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Notification window startup timed out.");
        if (_startupError is not null) throw new InvalidOperationException("Notification window startup failed.", _startupError);
        if (_window == IntPtr.Zero) throw new InvalidOperationException("Notification window is unavailable.");
    }

    private void RunWindow()
    {
        var instance = NativeMethods.GetModuleHandle(null);
        ushort windowClass = 0;
        IntPtr window = IntPtr.Zero;
        try
        {
            var registration = new NativeMethods.WindowClass
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.WindowClass>(),
                Procedure = _procedure,
                Instance = instance,
                ClassName = _className
            };
            windowClass = NativeMethods.RegisterClassEx(ref registration);
            if (windowClass == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            window = NativeMethods.CreateWindowEx(0, _className, "GameHelper break notifications", 0,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            Volatile.Write(ref _window, window);
            _ready.Set();
            int result;
            while ((result = NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0)
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }
            if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _logger.LogWarning(ex, "Native notification window failed");
        }
        finally
        {
            try { RemoveIcon(window); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to remove notification icon"); }
            try { if (window != IntPtr.Zero) NativeMethods.DestroyWindow(window); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to destroy notification window"); }
            Volatile.Write(ref _window, IntPtr.Zero);
            try { if (windowClass != 0) NativeMethods.UnregisterClass(_className, instance); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to unregister notification class"); }
            _ready.Set();
        }
    }

    private IntPtr HandleMessage(IntPtr window, uint message, nuint wParam, IntPtr lParam)
    {
        try
        {
            switch (message)
            {
                case ShowMessage:
                    RemoveIcon(window);
                    _iconId = _iconId % ushort.MaxValue + 1;
                    var notification = CreateIconData(window);
                    notification.Flags = 1 | 2 | 4;
                    notification.Callback = CallbackMessage;
                    notification.Icon = NativeMethods.LoadIcon(IntPtr.Zero, new IntPtr(32516));
                    if (!NativeMethods.ShellNotifyIcon(0, ref notification)) return IntPtr.Zero;
                    _hasIcon = true;
                    notification.Version = 4;
                    if (!NativeMethods.ShellNotifyIcon(4, ref notification)) { RemoveIcon(window); return IntPtr.Zero; }
                    notification.Flags = 0x10 | 0x40; // INFO + REALTIME: never queue a stale break reminder.
                    notification.Title = "GameHelper · 该休息了";
                    notification.Text = (string?)GCHandle.FromIntPtr(lParam).Target ?? string.Empty;
                    notification.InfoFlags = 1 | 0x80 | (wParam == 0 ? 0x10u : 0); // INFO, respect quiet time, optional no sound.
                    if (!NativeMethods.ShellNotifyIcon(1, ref notification)) { RemoveIcon(window); return IntPtr.Zero; }
                    NativeMethods.SetTimer(window, _iconId, 60000, IntPtr.Zero);
                    return new IntPtr(1);
                case ClearMessage:
                    RemoveIcon(window);
                    return IntPtr.Zero;
                case CallbackMessage:
                    var callback = unchecked((uint)lParam.ToInt64());
                    var eventId = callback & 0xffff;
                    if ((callback >> 16) == _iconId && eventId is 0x403 or 0x404 or 0x405)
                        RemoveIcon(window); // hidden, timeout, or clicked; clicking does not activate the app.
                    return IntPtr.Zero;
                case 0x0113: // WM_TIMER fallback for suppressed notifications.
                    if (wParam == _iconId) RemoveIcon(window);
                    return IntPtr.Zero;
                case 0x0010: // WM_CLOSE
                    RemoveIcon(window);
                    NativeMethods.DestroyWindow(window);
                    return IntPtr.Zero;
                case 0x0002: // WM_DESTROY
                    NativeMethods.PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Notification message failed"); return IntPtr.Zero; }
        return NativeMethods.DefWindowProc(window, message, wParam, lParam);
    }

    private NativeMethods.IconData CreateIconData(IntPtr window) => new()
    {
        Size = (uint)Marshal.SizeOf<NativeMethods.IconData>(), Window = window, Id = _iconId,
        Tip = "GameHelper · 休息提醒", Text = string.Empty, Title = string.Empty
    };

    private void RemoveIcon(IntPtr window)
    {
        if (!_hasIcon) return;
        NativeMethods.KillTimer(window, _iconId);
        var notification = CreateIconData(window);
        if (!NativeMethods.ShellNotifyIcon(2, ref notification))
            _logger.LogDebug("Notification icon already unavailable");
        _hasIcon = false;
    }

    private static class NativeMethods
    {
        public delegate IntPtr WindowProcedure(IntPtr window, uint message, nuint wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WindowClass
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
        public struct Message
        {
            public IntPtr Window;
            public uint Id;
            public nuint WParam;
            public IntPtr LParam;
            public uint Time;
            public int X, Y;
            public uint Private;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct IconData
        {
            public uint Size;
            public IntPtr Window;
            public uint Id, Flags, Callback;
            public IntPtr Icon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
            public uint State, StateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Text;
            public uint Version;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
            public uint InfoFlags;
            public Guid Guid;
            public IntPtr BalloonIcon;
        }

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? module);
        [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassEx(ref WindowClass registration);
        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(uint extended, string className, string name, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)] public static extern bool UnregisterClass(string className, IntPtr instance);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
        public static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] public static extern IntPtr DispatchMessage(ref Message message);
        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] public static extern IntPtr DefWindowProc(IntPtr window, uint message, nuint wParam, IntPtr lParam);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")] public static extern IntPtr SendMessage(IntPtr window, uint message, nuint wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
        [DllImport("user32.dll", EntryPoint = "LoadIconW")] public static extern IntPtr LoadIcon(IntPtr instance, IntPtr resource);
        [DllImport("user32.dll")] public static extern nuint SetTimer(IntPtr window, nuint id, uint milliseconds, IntPtr callback);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool KillTimer(IntPtr window, nuint id);
        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ShellNotifyIcon(uint operation, ref IconData notification);
    }
}
