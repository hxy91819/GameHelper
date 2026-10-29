using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GameHelper.Desktop.Input;

namespace GameHelper.Desktop;

public partial class MainWindow
{
    private IControllerSource? _controllerSource;
    private ControllerNavigation? _controllerNavigation;
    private readonly DispatcherTimer _controllerTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    public ControllerKind ControllerKind => _controllerNavigation?.DeviceKind ?? ControllerKind.Unknown;
    private bool _controllerClosed;
    private void ConnectControllerLifecycle()
    {
        Application.Current.Activated += OnControllerActivated;
        Application.Current.Deactivated += OnControllerDeactivated;
        Activated += OnControllerActivated;
        Deactivated += OnControllerDeactivated;
        StateChanged += OnControllerActivated;
        UpdateControllerActivation();
    }
    private void OnControllerActivated(object? sender, EventArgs e) => UpdateControllerActivation();
    private void OnControllerDeactivated(object? sender, EventArgs e)
    {
        StopController();
        // Owned modal windows become active after the main window deactivation event.
        Dispatcher.BeginInvoke(new Action(UpdateControllerActivation), DispatcherPriority.Background);
    }
    private void UpdateControllerActivation()
    {
        if (_controllerClosed) return;
        if (WindowState != WindowState.Minimized && (IsActive || OwnedWindows.Cast<Window>().Any(w => w.IsActive))) StartController();
        else StopController();
    }
    private void DisconnectControllerLifecycle()
    {
        _controllerClosed = true;
        Application.Current.Activated -= OnControllerActivated;
        Application.Current.Deactivated -= OnControllerDeactivated;
        Activated -= OnControllerActivated;
        Deactivated -= OnControllerDeactivated;
        StateChanged -= OnControllerActivated;
        StopController();
    }
    private void StartController()
    {
        if (_controllerSource is not null) return;
        try
        {
            _controllerSource = new SdlControllerSource();
            _controllerNavigation = new ControllerNavigation(_controllerSource, TimeProvider.System);
            _controllerTimer.Tick += OnControllerTick;
            _controllerTimer.Start();
        }
        catch (Exception ex) { StopController(); _model.Status = "手柄输入不可用：" + ex.Message; }
    }
    private void StopController()
    {
        try { _controllerTimer.Stop(); } catch { /* Continue cleanup. */ }
        try { _controllerTimer.Tick -= OnControllerTick; } catch { /* Continue cleanup. */ }
        var source = _controllerSource;
        _controllerSource = null;
        _controllerNavigation = null;
        try { source?.Dispose(); } catch { /* State remains stopped even if native cleanup fails. */ }
    }
    private void OnControllerTick(object? sender, EventArgs e)
    {
        UpdateControllerActivation();
        if (_controllerNavigation is null) return;
        var dialog = OwnedWindows.Cast<Window>().FirstOrDefault(w => w.IsActive);
        var foreground = IsActive && IsEnabled;
        IReadOnlyList<ControllerAction> actions;
        try { actions = _controllerNavigation.Poll(foreground || dialog is not null); }
        catch (Exception ex) { StopController(); _model.Status = "手柄输入已停止：" + ex.Message; return; }
        foreach (var action in actions)
        {
            if (dialog is not null) { if (action == ControllerAction.Back) dialog.Close(); continue; }
            if (action == ControllerAction.Back)
            {
                if (SettingsPanel.Visibility == Visibility.Visible) SettingsPanel.Visibility = Visibility.Collapsed;
                else if (_showStatistics || _showGlobalSettings) Navigate(false);
                else SearchBox.Clear();
                continue;
            }
            if (_showStatistics || _showGlobalSettings || SettingsPanel.Visibility == Visibility.Visible) continue;
            if (action == ControllerAction.Confirm) { _model.LaunchFromPoster(); break; }
            if (action == ControllerAction.Menu) { OnSettings(this, new RoutedEventArgs()); continue; }
            var offset = action switch
            {
                ControllerAction.Left => -1, ControllerAction.Right => 1,
                ControllerAction.Up => _layout == "grid" ? -CardColumns : -1,
                ControllerAction.Down => _layout == "grid" ? CardColumns : 1,
                ControllerAction.PreviousPage => -CardColumns * 3,
                ControllerAction.NextPage => CardColumns * 3, _ => 0
            };
            if (offset == 0 || GameList.Items.Count == 0) continue;
            GameList.SelectedIndex = GameList.SelectedIndex < 0 ? 0 : Math.Clamp(GameList.SelectedIndex + offset, 0, GameList.Items.Count - 1);
            if (GameList.SelectedItem is { } item) GameList.ScrollIntoView(item);
        }
    }
}
