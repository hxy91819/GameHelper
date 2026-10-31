using System.Windows;
using System.Windows.Threading;
namespace GameHelper.Desktop.Input;

/// <summary>Polls the global mode chord; ordinary navigation stays limited to the foreground window.</summary>
internal sealed class WindowControllerInput : IDisposable
{
    private static WindowControllerInput? owner;
    private readonly Window window;
    private readonly Action<IReadOnlyList<ControllerAction>> receive;
    private readonly Action<string> error;
    private readonly bool modals;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private IControllerSource? source;
    private ControllerNavigation? navigation;
    private bool disposed;
    public ControllerKind Kind => navigation?.DeviceKind ?? ControllerKind.Unknown;
    public WindowControllerInput(Window window, Action<IReadOnlyList<ControllerAction>> receive, Action<string> error, bool modals = false)
    {
        this.window = window; this.receive = receive; this.error = error; this.modals = modals;
        window.Activated += Changed; window.Deactivated += Changed; window.StateChanged += Changed;
        window.IsVisibleChanged += VisibilityChanged;
        Application.Current.Activated += Changed; Application.Current.Deactivated += Changed;
        timer.Tick += Tick; Update();
    }
    private bool Foreground => window.WindowState != WindowState.Minimized &&
        ((window.IsActive && window.IsEnabled) || (modals && window.OwnedWindows.Cast<Window>().Any(w => w.IsActive)));
    private void Changed(object? sender, EventArgs e) { Update(); window.Dispatcher.BeginInvoke(new Action(Update)); }
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Update();
    private void Update()
    {
        if (disposed) return;
        // The hidden desktop yields to the console window. Minimized visible windows
        // retain the global chord listener, including while a game owns the foreground.
        if (!window.IsVisible) { Stop(); return; }
        if (source is not null) return;
        try
        {
            owner?.Stop(); owner = this;
            source = new SdlControllerSource(); navigation = new(source, TimeProvider.System); timer.Start();
        }
        catch (Exception ex) { Stop(); error("手柄输入不可用：" + ex.Message); }
    }
    private void Tick(object? sender, EventArgs e)
    {
        Update(); if (navigation is null) return;
        try { receive(navigation.Poll(Foreground, globalModeChord: true)); }
        catch (Exception ex) { Stop(); error("手柄输入已停止：" + ex.Message); }
    }
    private void Stop()
    {
        try { timer.Stop(); } catch { }
        var old = source; source = null; navigation = null;
        if (owner == this) owner = null;
        try { old?.Dispose(); } catch { }
    }
    public void Dispose()
    {
        disposed = true; Stop(); timer.Tick -= Tick;
        window.Activated -= Changed; window.Deactivated -= Changed; window.StateChanged -= Changed;
        window.IsVisibleChanged -= VisibilityChanged;
        Application.Current.Activated -= Changed; Application.Current.Deactivated -= Changed;
    }
}
