using System;
using System.Runtime.InteropServices;

namespace GameHelper.Infrastructure.Hotkeys;

public sealed class NativeWindowsHotkeyPlatform : IWindowsHotkeyPlatform
{
    private const uint PmNoremove = 0x0000;

    public uint GetCurrentThreadId() => GetCurrentThreadIdNative();

    public void EnsureMessageQueue()
    {
        PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoremove);
    }

    public bool RegisterHotKey(nint handle, int id, uint modifiers, uint virtualKey)
    {
        return RegisterHotKeyNative(handle, id, modifiers, virtualKey);
    }

    public bool UnregisterHotKey(nint handle, int id)
    {
        return UnregisterHotKeyNative(handle, id);
    }

    public int GetMessage(out NativeMessage message)
    {
        var native = new MSG();
        var result = GetMessageNative(ref native, IntPtr.Zero, 0, 0);
        message = new NativeMessage
        {
            Handle = native.hwnd,
            Message = native.message,
            WParam = native.wParam,
            LParam = native.lParam,
            Time = native.time
        };
        return result;
    }

    public bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam)
    {
        return PostThreadMessageNative(threadId, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKeyNative(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKeyNative(nint hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessageNative(ref MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PeekMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessageNative(uint idThread, uint msg, nuint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadIdNative();
}
