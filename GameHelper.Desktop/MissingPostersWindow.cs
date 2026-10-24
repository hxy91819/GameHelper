using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using GameHelper.Infrastructure.Artwork;

namespace GameHelper.Desktop;

/// <summary>Explicit, cancellable name matching; completed associations are persisted separately from game settings.</summary>
internal sealed class MissingPostersWindow : Window
{
    private readonly LibraryViewModel _model;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private readonly ListBox _games = new DarkListBox();
    private readonly ListBox _results = new DarkListBox();
    private readonly TextBox _query = new() { MaxLength = 120 };
    private readonly Image _preview = new() { Width = 150, Height = 225, Stretch = Stretch.UniformToFill };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12), Foreground = Brushes.Honeydew };
    private readonly TextBlock _identity = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _include = new() { Content = "包括本地未找到 / 待确认游戏", Margin = new Thickness(0, 10, 0, 10) };
    private readonly Button _search = new() { Content = "按名称搜索", Margin = new Thickness(10, 0, 0, 0) };
    private readonly Button _automatic = new() { Content = "批量采用最佳匹配", Margin = new Thickness(0, 0, 12, 0) };
    private readonly CheckBox _autoApply = new() { Content = "搜索后自动采用最佳匹配", IsChecked = true, Margin = new Thickness(0, 0, 0, 8) };
    private readonly Button _apply = new() { Content = "使用选中的海报", IsEnabled = false };
    private bool _working;
    private bool _closed;
    private int _request;

    public MissingPostersWindow(LibraryViewModel model)
    {
        _model = model;
        _token = _lifetime.Token;
        Title = "海报补全 · 名称定向搜索";
        Width = 1000; Height = 720; MinWidth = 800; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(20, 26, 24));
        Foreground = new SolidColorBrush(Color.FromRgb(240, 242, 235));
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 14;
        var root = new DockPanel { Margin = new Thickness(24) };
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "让游戏库有封面", FontSize = 26, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "中英文名称综合匹配，优先原版游戏；已有海报关联不会被自动覆盖。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        heading.Children.Add(_include); heading.Children.Add(_autoApply); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var footer = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        DockPanel.SetDock(_apply, Dock.Right); footer.Children.Add(_apply);
        DockPanel.SetDock(_automatic, Dock.Left); footer.Children.Add(_automatic);
        footer.Children.Add(new TextBlock { Text = "关闭窗口可取消；已完成的关联会保留。", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var columns = new Grid(); columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) }); columns.ColumnDefinitions.Add(new ColumnDefinition());
        _games.Background = Brushes.Transparent; _games.Foreground = Brushes.White; _games.BorderThickness = new Thickness(0);
        _games.ItemTemplate = TitleTemplate(); ScrollViewer.SetHorizontalScrollBarVisibility(_games, ScrollBarVisibility.Disabled);
        columns.Children.Add(_games);
        var detail = new DockPanel { Margin = new Thickness(22, 0, 0, 0) }; Grid.SetColumn(detail, 1); columns.Children.Add(detail);
        var query = new DockPanel(); DockPanel.SetDock(_search, Dock.Right); query.Children.Add(_search); query.Children.Add(_query);
        DockPanel.SetDock(query, Dock.Top); detail.Children.Add(query);
        DockPanel.SetDock(_status, Dock.Top); detail.Children.Add(_status);
        var preview = new StackPanel { Width = 165, Margin = new Thickness(14, 0, 0, 0) };
        preview.Children.Add(_preview); preview.Children.Add(_identity); DockPanel.SetDock(preview, Dock.Right); detail.Children.Add(preview);
        _results.Background = Brushes.Transparent; _results.Foreground = Brushes.White; _results.BorderThickness = new Thickness(0); detail.Children.Add(_results);
        _results.ItemTemplate = TitleTemplate(); ScrollViewer.SetHorizontalScrollBarVisibility(_results, ScrollBarVisibility.Disabled);
        root.Children.Add(columns); Content = root;
        _search.Click += async (_, _) => await SearchAsync();
        _query.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; await SearchAsync(); } };
        _games.SelectionChanged += async (_, _) =>
        {
            if (_games.SelectedItem is LibraryGameItem game && !_working)
            { _query.Text = game.Name.Length > 120 ? game.Name[..120] : game.Name; await SearchAsync(); }
        };
        _results.SelectionChanged += async (_, _) => await PreviewAsync();
        _automatic.Click += async (_, _) => await MatchAsync();
        _apply.Click += async (_, _) => await ApplyAsync();
        _include.Checked += (_, _) => Populate(); _include.Unchecked += (_, _) => Populate();
        Loaded += (_, _) => Populate();
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _lifetime.Dispose(); };
    }

    private static DataTemplate TitleTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        text.SetBinding(ToolTipProperty, new Binding("Name")); text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(MarginProperty, new Thickness(10, 9, 10, 9)); return new DataTemplate { VisualTree = text };
    }

    private void Populate()
    {
        if (_closed || _working) return;
        _games.ItemsSource = _model.MissingPosters(_include.IsChecked == true);
        _games.SelectedIndex = _games.Items.Count > 0 ? 0 : -1;
        if (_games.Items.Count == 0) _status.Text = "当前范围没有缺少海报的游戏，可勾选包括其他分组。";
    }

    private void Busy(bool busy)
    {
        _working = busy;
        _search.IsEnabled = !busy; _automatic.IsEnabled = !busy; _include.IsEnabled = !busy; _games.IsEnabled = !busy;
        _autoApply.IsEnabled = !busy;
        _apply.IsEnabled = !busy && _results.SelectedItem is SteamArtworkGame && _games.SelectedItem is LibraryGameItem;
    }

    private async Task SearchAsync()
    {
        if (_working || _closed) return;
        var request = ++_request;
        Busy(true); _preview.Source = null; _identity.Text = ""; _results.ItemsSource = null;
        _status.Text = "正在按名称搜索 Steam…";
        try
        {
            var found = await _model.SearchPostersAsync(_query.Text, _token);
            if (_closed || request != _request) return;
            _results.ItemsSource = found;
            _status.Text = found.Count == 0 ? "没有找到结果，可尝试原文名称或调整关键词。单款匹配入口也支持商店链接 / App ID。" : $"找到 {found.Count} 个结果。选择后可预览，确认名称和版本再应用。";
            if (_games.SelectedItem is LibraryGameItem game)
            {
                var best = SteamArtworkNameMatcher.BestMatch(game.Name, found);
                _results.SelectedItem = best ?? found.FirstOrDefault(item => item.IsGame == true);
                if (best is not null && _autoApply.IsChecked == true && !_model.HasPosterAssociation(game))
                {
                    _token.ThrowIfCancellationRequested();
                    await _model.AssociatePosterAsync(game, best.AppId, refresh: false);
                    if (!_closed) _status.Text = $"已自动采用最佳匹配：{best.Name} · Steam {best.AppId}。可手动选择其他结果替换。";
                }
                else if (best is null && found.Count > 0) _status.Text = "候选结果接近或匹配度不足，请预览后选择；不会把续作、DLC 或画集自动用作原版海报。";
            }
        }
        catch (Exception ex) { if (!_closed) _status.Text = "搜索暂不可用，可重试：" + ex.Message; }
        finally { if (!_closed) Busy(false); }
    }

    private async Task PreviewAsync()
    {
        _apply.IsEnabled = !_working && _results.SelectedItem is SteamArtworkGame && _games.SelectedItem is LibraryGameItem;
        if (_results.SelectedItem is not SteamArtworkGame candidate) { _preview.Source = null; _identity.Text = ""; return; }
        var request = _request;
        _identity.Text = $"{candidate.Name}\nSteam {candidate.AppId}"; _preview.Source = null;
        try
        {
            var image = await _model.PreviewPosterAsync(candidate.AppId, _token);
            if (!_closed && request == _request && _results.SelectedItem == candidate) _preview.Source = image;
        }
        catch (Exception ex) { if (!_closed && _results.SelectedItem == candidate) _identity.Text += "\n海报预览暂不可用：" + ex.Message; }
    }

    private async Task ApplyAsync()
    {
        if (_working || _closed || _games.SelectedItem is not LibraryGameItem game || _results.SelectedItem is not SteamArtworkGame candidate) return;
        Busy(true);
        try
        {
            await _model.AssociatePosterAsync(game, candidate.AppId);
            if (!_closed) _status.Text = game.ArtworkIsCover ? $"已为“{game.Name}”保存海报。" : "已保存关联，图片暂不可用，可稍后重试。";
        }
        catch (Exception ex) { if (!_closed) _status.Text = "关联未完成：" + ex.Message; }
        finally { if (!_closed) Busy(false); }
    }

    private async Task MatchAsync()
    {
        if (_working || _closed) return;
        var games = _model.MissingPosters(_include.IsChecked == true).Where(game => !_model.HasPosterAssociation(game)).ToList();
        Busy(true); var matched = 0; var failed = 0;
        try
        {
            foreach (var (game, index) in games.Select((game, index) => (game, index)))
            {
                _token.ThrowIfCancellationRequested();
                _status.Text = $"{index + 1} / {games.Count} · 正在搜索 {game.Name}";
                try
                {
                    if (await _model.AutoMatchPosterAsync(game, _token)) matched++;
                }
                catch (OperationCanceledException) when (_closed) { return; }
                catch (Exception) { failed++; }
            }
            if (!_closed) _status.Text = $"已为 {matched} 款游戏采用最佳匹配；其余可逐款调整关键词确认。{(failed > 0 ? $" {failed} 项网络或保存失败，可重试。" : "")}";
        }
        catch (OperationCanceledException) when (_closed) { }
        finally { if (!_closed) Busy(false); }
    }
}
