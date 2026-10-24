using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameHelper.Infrastructure.Artwork;

namespace GameHelper.Desktop;

internal sealed class SteamPosterWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly LibraryViewModel _model;
    private readonly TextBox _query;
    private readonly TextBox _id;
    private readonly TextBlock _status;
    private readonly ListBox _results;
    private readonly Button _search;
    private readonly Button _select;
    public uint? SelectedAppId { get; private set; }

    public SteamPosterWindow(LibraryViewModel model, string name)
    {
        _model = model;
        Title = "匹配 Steam 海报";
        Width = 680; Height = 620; MinWidth = 520; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(20, 26, 24));
        Foreground = new SolidColorBrush(Color.FromRgb(240, 242, 235));
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 14;
        var root = new DockPanel { Margin = new Thickness(24) };
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "选择海报对应的 Steam 游戏", FontSize = 23, Margin = new Thickness(0, 0, 0, 12) });
        header.Children.Add(new TextBlock { Text = "仅关联海报，不改变游戏的启动方式。", Margin = new Thickness(0, 0, 0, 10) });
        var queryRow = new DockPanel();
        _search = new Button { Content = "搜索", Margin = new Thickness(12, 5, 0, 10), IsDefault = true };
        _search.Click += async (_, _) => await SearchAsync();
        DockPanel.SetDock(_search, Dock.Right); queryRow.Children.Add(_search);
        _query = new TextBox { Text = name, MaxLength = 120 }; queryRow.Children.Add(_query); header.Children.Add(queryRow);
        header.Children.Add(new TextBlock { Text = "或粘贴 Steam 商店链接 / App ID", FontSize = 12 });
        _id = new TextBox { MaxLength = 300 }; header.Children.Add(_id);
        _status = new TextBlock { Text = "正在搜索…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 14) }; header.Children.Add(_status);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var automatic = new Button { Content = "恢复自动识别", Margin = new Thickness(0, 0, 10, 0) };
        automatic.Click += (_, _) => { SelectedAppId = null; DialogResult = true; };
        _select = new Button { Content = "使用此游戏的海报", IsEnabled = false };
        _select.Click += (_, _) =>
        {
            SelectedAppId = SteamArtworkIdentity.Parse(_id.Text) ?? (_results.SelectedItem as SteamArtworkGame)?.AppId;
            if (SelectedAppId is > 0) DialogResult = true;
        };
        actions.Children.Add(automatic); actions.Children.Add(_select);
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        _results = new DarkListBox { DisplayMemberPath = nameof(SteamArtworkGame.Name), Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0) };
        _results.SelectionChanged += (_, _) => RefreshChoice();
        _id.TextChanged += (_, _) => RefreshChoice();
        root.Children.Add(_results); Content = root;
        Loaded += async (_, _) => await SearchAsync();
        Closed += (_, _) => { _lifetime.Cancel(); _lifetime.Dispose(); };
    }

    private void RefreshChoice() => _select.IsEnabled = SteamArtworkIdentity.Parse(_id.Text) is > 0 || _results.SelectedItem is SteamArtworkGame;

    private async Task SearchAsync()
    {
        if (!_search.IsEnabled || _lifetime.IsCancellationRequested) return;
        _search.IsEnabled = false;
        try
        {
            var results = await _model.SearchPostersAsync(_query.Text, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            _results.ItemsSource = results;
            _status.Text = results.Count == 0 ? "没有找到结果，可改用英文名称或粘贴 App ID。" : $"找到 {results.Count} 个结果，请确认游戏与版本。";
        }
        catch (OperationCanceledException) { if (!_lifetime.IsCancellationRequested) _status.Text = "Steam 搜索超时，可重试或填写 App ID。"; }
        catch (Exception ex) { if (!_lifetime.IsCancellationRequested) _status.Text = "Steam 搜索暂不可用：" + ex.Message; }
        finally { if (!_lifetime.IsCancellationRequested) _search.IsEnabled = true; }
    }
}
