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
    private void StartController()
    {
        try
        {
            _controllerSource = new SdlControllerSource();
            _controllerNavigation = new ControllerNavigation(_controllerSource, TimeProvider.System);
            _controllerTimer.Tick += OnControllerTick;
            _controllerTimer.Start();
        }
        catch (Exception ex) { _controllerSource?.Dispose(); _controllerSource = null; _model.Status = "手柄输入不可用：" + ex.Message; }
    }
    private void StopController()
    {
        _controllerTimer.Stop(); _controllerTimer.Tick -= OnControllerTick;
        _controllerSource?.Dispose(); _controllerSource = null;
    }
    private void OnControllerTick(object? sender, EventArgs e)
    {
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
