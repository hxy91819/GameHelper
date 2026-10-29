namespace GameHelper.Desktop.Input;

public enum ControllerKind { Unknown, Xbox, PlayStation }
[Flags]
public enum ControllerAction { None = 0, Up = 1, Down = 2, Left = 4, Right = 8, Confirm = 16, Back = 32, Menu = 64, PreviousPage = 128, NextPage = 256 }
public sealed record ControllerState(uint Id, ControllerKind Kind, ControllerAction Buttons, short X = 0, short Y = 0);
public interface IControllerSource : IDisposable { IReadOnlyList<ControllerState> Read(); }

/// <summary>Single-controller arbitration prevents physical/virtual duplicates, including delayed reports.</summary>
public sealed class ControllerNavigation(IControllerSource source, TimeProvider clock)
{
    private uint? _owner;
    private ControllerAction _previous;
    private long _next;
    private bool _armed;
    public ControllerKind DeviceKind { get; private set; }
    public IReadOnlyList<ControllerAction> Poll(bool foreground)
    {
        var devices = source.Read();
        // Prefer a native Sony controller over a virtual Xbox, then keep ownership until disconnect.
        var device = devices.FirstOrDefault(d => d.Id == _owner) ?? devices.OrderByDescending(d => d.Kind == ControllerKind.PlayStation).ThenBy(d => d.Id).FirstOrDefault();
        if (device?.Id != _owner) { _owner = device?.Id; _armed = false; _previous = 0; }
        DeviceKind = device?.Kind ?? ControllerKind.Unknown;
        var buttons = device is null ? ControllerAction.None : Map(device);
        if (!foreground) { _armed = false; _previous = buttons; return []; }
        if (!_armed) { _armed = buttons == 0; _previous = buttons; return []; }
        var now = clock.GetTimestamp();
        var pressed = buttons & ~_previous;
        var direction = buttons & (ControllerAction.Up | ControllerAction.Down | ControllerAction.Left | ControllerAction.Right);
        if (buttons != _previous) _next = now + (long)(clock.TimestampFrequency * .4);
        else if (direction != 0 && now >= _next) { pressed |= direction; _next = now + (long)(clock.TimestampFrequency * .09); }
        _previous = buttons;
        return Enum.GetValues<ControllerAction>().Where(a => a != 0 && (pressed & a) != 0).ToArray();
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
