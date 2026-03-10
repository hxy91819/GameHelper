using System;

namespace GameHelper.Infrastructure.Hotkeys;

public interface IWindowsHotkeyPlatform
{
    uint GetCurrentThreadId();

    void EnsureMessageQueue();

    bool RegisterHotKey(nint handle, int id, uint modifiers, uint virtualKey);

    bool UnregisterHotKey(nint handle, int id);

    int GetMessage(out NativeMessage message);

    bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);
}
