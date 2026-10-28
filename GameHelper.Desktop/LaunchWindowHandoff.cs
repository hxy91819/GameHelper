using System.Windows;
using GameHelper.Infrastructure.Processes;

namespace GameHelper.Desktop;

/// <summary>Desktop window actions around a library launch; tests replace this to avoid Win32 and a real window.</summary>
internal interface ILaunchWindowHandoff
{
    /// <summary>Let the background host and the game it starts take the foreground.</summary>
    void AllowForeground();

    /// <summary>Minimize the library once the host accepted the launch request.</summary>
    void Minimize();
}

/// <summary>Grants the foreground hand-off and minimizes the actual library window.</summary>
internal sealed class MainWindowLaunchWindowHandoff(Window window) : ILaunchWindowHandoff
{
    public void AllowForeground() => WindowsForegroundPermission.AllowAnyProcess();
    public void Minimize() => window.WindowState = WindowState.Minimized;
}

/// <summary>Leaves the window untouched until the library window attaches its real hand-off.</summary>
internal sealed class NoopLaunchWindowHandoff : ILaunchWindowHandoff
{
    public static NoopLaunchWindowHandoff Instance { get; } = new();
    public void AllowForeground() { }
    public void Minimize() { }
}
