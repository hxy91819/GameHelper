using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameHelper.Core.Models;

namespace GameHelper.Desktop;

internal sealed class SteamImportWindow : Window
{
    private readonly List<(SteamInstalledGame Game, CheckBox Choice)> _choices = [];
    public string[] SelectedAppIds => _choices.Where(item => item.Choice.IsChecked == true).Select(item => item.Game.AppId).ToArray();

    public SteamImportWindow(IReadOnlyList<SteamInstalledGame> games)
    {
        Title = "导入 Steam 游戏";
        Width = 640; Height = 620; MinWidth = 500; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(20, 26, 24));
        Foreground = new SolidColorBrush(Color.FromRgb(240, 242, 235));
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 14;
        var root = new DockPanel { Margin = new Thickness(24) };
        var header = new TextBlock { Text = $"发现 {games.Count} 款已安装游戏", FontSize = 23, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var note = new TextBlock { Text = "选择需要导入的游戏。已有条目保持原设置。", Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(note, Dock.Top); root.Children.Add(note);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var none = new Button { Content = "取消全选", Margin = new Thickness(0, 0, 10, 0) };
        none.Click += (_, _) => { foreach (var item in _choices) item.Choice.IsChecked = false; };
        var import = new Button { Content = "导入选中游戏", IsDefault = true };
        import.Click += (_, _) => { if (SelectedAppIds.Length > 0) DialogResult = true; };
        actions.Children.Add(none); actions.Children.Add(import);
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var rows = new StackPanel();
        foreach (var game in games.OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var choice = new CheckBox { Content = game.Name, IsChecked = true, ToolTip = game.ExecutablePath, Margin = new Thickness(4, 10, 4, 10) };
            rows.Children.Add(choice); _choices.Add((game, choice));
        }
        root.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }
}
