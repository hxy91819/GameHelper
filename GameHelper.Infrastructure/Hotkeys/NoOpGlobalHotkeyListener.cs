using System;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Infrastructure.Hotkeys;

public sealed class NoOpGlobalHotkeyListener : IGlobalHotkeyListener
{
    public bool IsListening => false;

    public HotkeyRegistrationResult Start(HotkeyBinding binding, Action onTriggered)
    {
        return new HotkeyRegistrationResult
        {
            Success = false,
            Message = "当前平台不支持全局热键监听。"
        };
    }

    public void Stop()
    {
    }
}
