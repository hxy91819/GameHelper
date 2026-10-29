using System.Windows;
using GameHelper.Infrastructure.Processes;
using GameHelper.Core.Models;

namespace GameHelper.Desktop;

/// <summary>Desktop window actions around a library launch; tests replace this to avoid Win32 and a real window.</summary>
internal interface ILaunchWindowHandoff
{
    /// <summary>Let the background host and the game it starts take the foreground.</summary>
    void AllowForeground();

    /// <summary>Minimize the library once the host accepted the launch request.</summary>
    void Minimize();

    /// <summary>Track only a successful launch owned by this window; console mode owns its own tracking.</summary>
    void Track(string key, string name, Guid requestId) { }

    /// <summary>Observe fresh runtime snapshots even when the library is minimized.</summary>
    void Observe(IReadOnlyList<LibraryGame> games) { }
}

/// <summary>Hands off a launch and returns the desktop window after its own confirmed session ends.</summary>
internal sealed class MainWindowLaunchWindowHandoff : ILaunchWindowHandoff
{
    private readonly Action minimize;
    private readonly Action restore;
    private readonly Action allowForeground;
    private readonly TimeProvider clock;
    private ConsoleLaunchState state;
    internal bool Tracking => state.Tracking;
    internal MainWindowLaunchWindowHandoff(Window window, System.Windows.IInputElement? focusTarget = null)
        : this(() => window.WindowState = WindowState.Minimized,
            () => WindowForegroundActivation.Restore(window, focusTarget), TimeProvider.System,
            () => WindowsForegroundPermission.AllowAnyProcess()) { }
    internal MainWindowLaunchWindowHandoff(Action minimize, Action restore, TimeProvider clock, Action? allowForeground = null)
    {
        this.minimize = minimize; this.restore = restore; this.clock = clock;
        this.allowForeground = allowForeground ?? (() => { });
        state = new(clock);
    }
    public void AllowForeground() => allowForeground();
    public void Minimize() => minimize();
    public void Track(string key, string name, Guid requestId)
    {
        // A later explicit library launch becomes this window's return destination.
        state = new(clock);
        state.Begin(key, name, requestId);
    }
    public void Observe(IReadOnlyList<LibraryGame> games)
    {
        if (!Tracking) return;
        var game = games.FirstOrDefault(game => string.Equals(game.Config.DataKey, state.Key, StringComparison.OrdinalIgnoreCase));
        if (state.Observe(game is not null && LibraryGameItem.IsRunningState(game.State), game?.LaunchRequestId, game?.LaunchProcessId)
            == ConsoleLaunchState.Effect.Restore) restore();
    }
}

/// <summary>Leaves the window untouched until the library window attaches its real hand-off.</summary>
internal sealed class NoopLaunchWindowHandoff : ILaunchWindowHandoff
{
    public static NoopLaunchWindowHandoff Instance { get; } = new();
    public void AllowForeground() { }
    public void Minimize() { }
}
