namespace GameHelper.Desktop.Input;

public enum ControllerKind { Unknown, Xbox, PlayStation }
[Flags]
public enum ControllerAction { None = 0, Up = 1, Down = 2, Left = 4, Right = 8, Confirm = 16, Back = 32, Menu = 64, PreviousPage = 128, NextPage = 256, Auxiliary = 512, SearchFinish = 1024, ModeModifier = 2048, ToggleConsoleMode = 4096, PreviousShelfPage = 8192, NextShelfPage = 16384 }
public sealed record ControllerState(uint Id, ControllerKind Kind, ControllerAction Buttons, short X = 0, short Y = 0, short LeftTrigger = 0, short RightTrigger = 0);
public interface IControllerSource : IDisposable { IReadOnlyList<ControllerState> Read(); }

/// <summary>Single-controller arbitration prevents physical/virtual duplicates, including delayed reports.</summary>
public sealed class ControllerNavigation(IControllerSource source, TimeProvider clock)
{
    private uint? _owner;
    private ControllerAction _previous;
    private long _next;
    private bool _armed;
    private bool _menuPending, _modeChordConsumed;
    private long? _idleSince;
    private Dictionary<uint, ControllerAction> _lastDevices = new();
    private readonly Dictionary<uint, ControllerAction> _triggerStates = new();
    public ControllerKind DeviceKind { get; private set; }
    public IReadOnlyList<ControllerAction> Poll(bool foreground)
    {
        var devices = source.Read();
        var now = clock.GetTimestamp();
        foreach (var id in _triggerStates.Keys.Except(devices.Select(device => device.Id)).ToArray()) _triggerStates.Remove(id);
        var states = devices.ToDictionary(d => d.Id, MapWithTriggers);
        var device = devices.FirstOrDefault(d => d.Id == _owner);
        var takeover = false;
        if (device is not null)
        {
            if (states[device.Id] != 0) _idleSince = null;
            else _idleSince ??= now;
            if (foreground && _armed && _idleSince is { } idle && clock.GetElapsedTime(idle, now) >= TimeSpan.FromMilliseconds(500))
            {
                var candidate = devices.OrderByDescending(d => d.Kind == ControllerKind.PlayStation).ThenBy(d => d.Id)
                    .FirstOrDefault(d => d.Id != _owner && _lastDevices.TryGetValue(d.Id, out var previous) && (states[d.Id] & ~previous) != 0);
                if (candidate is not null) { device = candidate; takeover = true; }
            }
        }
        else device = devices.OrderByDescending(d => d.Kind == ControllerKind.PlayStation).ThenBy(d => d.Id).FirstOrDefault();
        if (device?.Id != _owner)
        {
            // A takeover accepts only edges newly pressed on that device, never its held buttons.
            _previous = takeover && device is not null ? _lastDevices[device.Id] : 0;
            _owner = device?.Id; _armed = takeover;
            _menuPending = _modeChordConsumed = false;
            _idleSince = device is not null && states[device.Id] == 0 ? now : null;
        }
        _lastDevices = states;
        DeviceKind = device?.Kind ?? ControllerKind.Unknown;
        var buttons = device is null ? ControllerAction.None : states[device.Id];
        if (!foreground) { _armed = false; _menuPending = _modeChordConsumed = false; _previous = buttons; return []; }
        if (!_armed) { _armed = buttons == 0; _previous = buttons; return []; }
        var pressed = buttons & ~_previous;
        const ControllerAction chord = ControllerAction.ModeModifier | ControllerAction.Menu;
        if (_modeChordConsumed)
        {
            // Both chord keys must be released. Neither release can open a menu.
            _modeChordConsumed = (buttons & chord) != 0;
            _previous = buttons;
            return [];
        }
        if ((pressed & ControllerAction.Menu) != 0) _menuPending = true;
        if ((buttons & chord) == chord && (pressed & chord) != 0)
        {
            _modeChordConsumed = true; _menuPending = false; _previous = buttons;
            return [ControllerAction.ToggleConsoleMode];
        }
        // Delay the standalone Menu until release so either chord key can come first.
        pressed &= ~(ControllerAction.Menu | ControllerAction.ModeModifier | ControllerAction.ToggleConsoleMode);
        if (_menuPending && (buttons & ControllerAction.Menu) == 0)
        {
            pressed |= ControllerAction.Menu; _menuPending = false;
        }
        var direction = buttons & (ControllerAction.Up | ControllerAction.Down | ControllerAction.Left | ControllerAction.Right);
        if (buttons != _previous) _next = now + (long)(clock.TimestampFrequency * .4);
        else if (direction != 0 && now >= _next) { pressed |= direction; _next = now + (long)(clock.TimestampFrequency * .09); }
        _previous = buttons;
        return Enum.GetValues<ControllerAction>().Where(a => a != 0 && (pressed & a) != 0).ToArray();
    }
    private ControllerAction MapWithTriggers(ControllerState state)
    {
        var held = _triggerStates.GetValueOrDefault(state.Id);
        // SDL trigger axes run from 0 to 32767. Hysteresis rejects light touches and threshold jitter.
        static bool Pressed(short value, bool wasHeld) => value >= (wasHeld ? 8000 : 16000);
        var triggers = ControllerAction.None;
        if (Pressed(state.LeftTrigger, (held & ControllerAction.PreviousShelfPage) != 0)) triggers |= ControllerAction.PreviousShelfPage;
        if (Pressed(state.RightTrigger, (held & ControllerAction.NextShelfPage) != 0)) triggers |= ControllerAction.NextShelfPage;
        _triggerStates[state.Id] = triggers;
        return Map(state) | triggers;
    }
    public static ControllerAction Map(ControllerState state)
    {
        var buttons = state.Buttons;
        // Dominant axis avoids diagonal double movement. D-pad wins over stick.
        if ((buttons & (ControllerAction.Up | ControllerAction.Down | ControllerAction.Left | ControllerAction.Right)) != 0) return buttons;
        if (Math.Max(Math.Abs((int)state.X), Math.Abs((int)state.Y)) < 16000) return buttons;
        return buttons | (Math.Abs((int)state.X) > Math.Abs((int)state.Y)
            ? state.X < 0 ? ControllerAction.Left : ControllerAction.Right
            : state.Y < 0 ? ControllerAction.Up : ControllerAction.Down);
    }
}
