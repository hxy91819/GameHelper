using System;
using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

public interface IGlobalHotkeyListener
{
    bool IsListening { get; }

    HotkeyRegistrationResult Start(HotkeyBinding binding, Action onTriggered);

    void Stop();
}
