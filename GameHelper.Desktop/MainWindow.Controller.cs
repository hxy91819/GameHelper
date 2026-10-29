using System.Windows;
using GameHelper.Desktop.Input;
namespace GameHelper.Desktop;
public partial class MainWindow
{
    private WindowControllerInput? _controllerInput;
    public ControllerKind ControllerKind => _controllerInput?.Kind ?? ControllerKind.Unknown;
    private void ConnectControllerLifecycle() => _controllerInput = new(this, HandleController, message => _model.Status = message, true);
    private void DisconnectControllerLifecycle() => _controllerInput?.Dispose();
    private void HandleController(IReadOnlyList<ControllerAction> actions)
    {
        var dialog = OwnedWindows.Cast<Window>().FirstOrDefault(w => w.IsActive);
        foreach (var action in actions)
        {
            if (dialog is not null) { if (action == ControllerAction.Back) dialog.Close(); continue; }
            if (action == ControllerAction.ToggleConsoleMode) { OpenConsoleMode(); return; }
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
