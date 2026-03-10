using System;

namespace GameHelper.Core.Models;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
    NoRepeat = 0x4000
}

public sealed record HotkeyBinding(string DisplayText, HotkeyModifiers Modifiers, uint VirtualKey);
