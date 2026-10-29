using SDL3;

namespace GameHelper.Desktop.Input;

/// <summary>Owned and pumped exclusively on the WPF dispatcher thread.</summary>
internal sealed class SdlControllerSource : IControllerSource
{
    private readonly Dictionary<uint, IntPtr> _handles = new();
    private bool _initialized;
    public SdlControllerSource()
    {
        if (!SDL.SetHintWithPriority("SDL_JOYSTICK_ENHANCED_REPORTS", "0", SDL.HintPriority.Override))
            throw new InvalidOperationException("无法禁用 SDL 手柄增强报告，已取消初始化：" + SDL.GetError());
        // WPF owns foreground gating; SDL has no window of its own.
        SDL.SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
        _initialized = SDL.InitSubSystem(SDL.InitFlags.Gamepad);
        if (!_initialized) throw new InvalidOperationException(SDL.GetError());
    }
    public IReadOnlyList<ControllerState> Read()
    {
        SDL.PumpEvents();
        while (SDL.PollEvent(out _)) { }
        var ids = SDL.GetGamepads(out _) ?? [];
        foreach (var id in _handles.Keys.Except(ids).ToArray()) { SDL.CloseGamepad(_handles[id]); _handles.Remove(id); }
        foreach (var id in ids)
            if (!_handles.ContainsKey(id)) { var handle = SDL.OpenGamepad(id); if (handle != IntPtr.Zero) _handles.Add(id, handle); }
        return _handles.Select(pair =>
        {
            var h = pair.Value;
            var kind = SDL.GetGamepadType(h) switch
            {
                SDL.GamepadType.PS3 or SDL.GamepadType.PS4 or SDL.GamepadType.PS5 => ControllerKind.PlayStation,
                SDL.GamepadType.Xbox360 or SDL.GamepadType.XboxOne => ControllerKind.Xbox,
                _ => ControllerKind.Unknown
            };
            var buttons = ControllerAction.None;
            foreach (var (button, action) in Mapping) if (SDL.GetGamepadButton(h, button)) buttons |= action;
            return new ControllerState(pair.Key, kind, buttons, SDL.GetGamepadAxis(h, SDL.GamepadAxis.LeftX), SDL.GetGamepadAxis(h, SDL.GamepadAxis.LeftY));
        }).ToArray();
    }
    private static readonly (SDL.GamepadButton, ControllerAction)[] Mapping =
    [
        (SDL.GamepadButton.DPadUp, ControllerAction.Up), (SDL.GamepadButton.DPadDown, ControllerAction.Down),
        (SDL.GamepadButton.DPadLeft, ControllerAction.Left), (SDL.GamepadButton.DPadRight, ControllerAction.Right),
        (SDL.GamepadButton.South, ControllerAction.Confirm), (SDL.GamepadButton.East, ControllerAction.Back),
        (SDL.GamepadButton.West, ControllerAction.Auxiliary), (SDL.GamepadButton.North, ControllerAction.SearchFinish),
        (SDL.GamepadButton.Start, ControllerAction.Menu), (SDL.GamepadButton.LeftShoulder, ControllerAction.PreviousPage),
        (SDL.GamepadButton.RightShoulder, ControllerAction.NextPage)
    ];
    public void Dispose()
    {
        foreach (var h in _handles.Values) { try { SDL.CloseGamepad(h); } catch { /* Continue releasing remaining devices. */ } }
        _handles.Clear();
        try { if (_initialized) SDL.QuitSubSystem(SDL.InitFlags.Gamepad); }
        catch { /* All native cleanup steps are best effort. */ }
        finally { _initialized = false; }
    }
}
