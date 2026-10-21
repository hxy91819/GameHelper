using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Providers;

namespace GameHelper.WpfPrototype;

// Throwaway experiment: compare three native WPF library layouts with the same read-only catalog.
// No monitor, launcher, HDR controller, CE, config writer, or startup registration is constructed here.
public partial class MainWindow : Window
{
    private readonly List<GameEntry> _games;
    private readonly System.ComponentModel.ICollectionView _view;
    private readonly string? _snapshotDirectory;
    private GameEntry? _selected;
    private string _filter = "all";
    private int _variant;

    public MainWindow(string[] args)
    {
        var configPath = Option(args, "--config") ?? Path.Combine(
            Environment.GetEnvironmentVariable("GAMEHELPER_DATA_DIR") ??
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHelper", "config.yml");
        var demo = args.Contains("--demo", StringComparer.Ordinal);
        _snapshotDirectory = Option(args, "--snapshot");
        _games = (demo ? DemoGames() : new YamlConfigProvider(configPath).Read().Games)
            .OrderBy(game => game.DisplayName ?? game.DataKey, StringComparer.CurrentCultureIgnoreCase)
            .Select((game, index) => new GameEntry(game, index)).ToList();
        _view = CollectionViewSource.GetDefaultView(_games);
        InitializeComponent();
        CatalogSummary.Text = demo ? "演示目录 · 不连接运行中的游戏" : $"已登记 {_games.Count} 款游戏 · 按名称排序";
        _view.Filter = Matches;
        GamesList.ItemsSource = _view;
        ApplyVariant(0);
        Refresh();
        Status.Text = "布局 1 / 3 · Ctrl+F 搜索 · Alt+1 / 2 / 3 切换布局 · 所有修改仅在内存中";
        PreviewKeyDown += OnKeyboard;
        if (_snapshotDirectory is not null)
        {
            ContentRendered += CapturePreview;
        }
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"{name} 缺少参数。");
        return args[index + 1];
    }

    private bool Matches(object item)
    {
        var game = (GameEntry)item;
        var query = SearchBox.Text.Trim();
        return (_filter != "hdr" || game.Hdr) && (_filter != "speed" || game.Speed is not null) &&
            (game.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || game.Executable.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void Refresh()
    {
        if (!IsInitialized) return;
        _view.Refresh();
        var visible = _view.Cast<GameEntry>().ToList();
        ResultCount.Text = $"{visible.Count} 款游戏 / 共 {_games.Count} 款";
        EmptyState.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_selected is null || !visible.Contains(_selected)) GamesList.SelectedItem = visible.FirstOrDefault();
        else GamesList.SelectedItem = _selected;
        AllFilter.Background = Brush(_filter == "all" ? "#365080" : "#242D3D");
        HdrFilter.Background = Brush(_filter == "hdr" ? "#365080" : "#242D3D");
        SpeedFilter.Background = Brush(_filter == "speed" ? "#365080" : "#242D3D");
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void OnFilter(object sender, RoutedEventArgs e)
    {
        _filter = (string)((Button)sender).Tag;
        Refresh();
        Status.Text = $"布局 {_variant + 1} / 3 · 筛选：{((Button)sender).Content} · 已载入 {_games.Count} 款游戏 · 修改不保存";
    }

    private void OnGameSelected(object sender, SelectionChangedEventArgs e)
    {
        RememberPreferences();
        _selected = GamesList.SelectedItem as GameEntry;
        Details.DataContext = _selected;
        HdrCheck.IsChecked = _selected?.PreviewHdr ?? false;
        SpeedCheck.IsChecked = _selected?.PreviewSpeed ?? false;
        SpeedLabel.Text = _selected?.Speed is double speed ? $"已配置倍率：{speed:0.##}×" : "未配置倍率 · 开启时预览 1.5×";
        SelectedName.Text = _selected?.Name ?? "请选择一款游戏";
        Details.IsEnabled = _selected is not null;
        SelectionBar.IsEnabled = _selected is not null;
    }

    private void RememberPreferences()
    {
        if (_selected is null) return;
        _selected.PreviewHdr = HdrCheck.IsChecked == true;
        _selected.PreviewSpeed = SpeedCheck.IsChecked == true;
    }

    private void OnVariant(object sender, RoutedEventArgs e) => ApplyVariant(int.Parse((string)((Button)sender).Tag));

    private void ApplyVariant(int variant)
    {
        _variant = variant;
        GamesList.ItemTemplate = (DataTemplate)Resources[variant == 0 ? "CardTemplate" : "RowTemplate"];
        GamesList.ItemsPanel = (ItemsPanelTemplate)Resources[variant == 0 ? "CardsPanel" : "RowsPanel"];
        LibraryColumn.Width = variant == 2 ? new GridLength(300) : new GridLength(1, GridUnitType.Star);
        DetailColumn.Width = variant switch { 0 => new GridLength(0), 1 => new GridLength(330), _ => new GridLength(1, GridUnitType.Star) };
        Details.Visibility = variant == 0 ? Visibility.Collapsed : Visibility.Visible;
        SelectionBar.Visibility = variant == 0 ? Visibility.Visible : Visibility.Collapsed;
        Hero.Height = variant == 2 ? 210 : 110;
        CardsButton.Background = Brush(variant == 0 ? "#4265A1" : "#242D3D");
        RowsButton.Background = Brush(variant == 1 ? "#4265A1" : "#242D3D");
        FocusButton.Background = Brush(variant == 2 ? "#4265A1" : "#242D3D");
        Status.Text = $"布局 {variant + 1} / 3 · 当前筛选 {_filter} · 载入 {_games.Count} 款游戏 · 所有修改仅在内存中";
    }

    private void OnInspectSelection(object sender, RoutedEventArgs e) => ApplyVariant(2);

    private void OnPreviewLaunch(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        RememberPreferences();
        var steps = new List<string>();
        if (_selected.PreviewHdr) steps.Add("开启 HDR 并确认状态");
        steps.Add("从已配置入口启动游戏");
        steps.Add("确认游戏进程");
        if (_selected.PreviewSpeed) steps.Add($"按策略应用 {_selected.Speed ?? 1.5:0.##}× 倍速");
        steps.Add("退出后清理并恢复本工具修改的状态");
        Status.Text = "流程预览（未执行）：" + string.Join(" → ", steps);
    }

    private void OnKeyboard(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is >= Key.D1 and <= Key.D3)
        {
            ApplyVariant(e.SystemKey - Key.D1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocused) SearchBox.Clear();
    }

    private async void CapturePreview(object? sender, EventArgs e)
    {
        ContentRendered -= CapturePreview;
        try
        {
            Directory.CreateDirectory(_snapshotDirectory!);
            for (var variant = 0; variant < 3; variant++)
            {
                ApplyVariant(variant);
                await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                // Allow a rendered frame after swapping ItemsPanel; layout alone can retain the previous visual cache.
                await System.Threading.Tasks.Task.Delay(150);
                Capture($"layout-{variant + 1}.png");
            }
            SearchBox.Text = "__no_game_matches_this_query__";
            if (_view.Cast<GameEntry>().Any() || EmptyState.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Empty search state did not update.");
            await System.Threading.Tasks.Task.Delay(150);
            Capture("empty.png");
            SearchBox.Clear();
            if (_view.Cast<GameEntry>().Count() != _games.Count)
                throw new InvalidOperationException("Clearing search did not restore the catalog.");
            if (_games.Count > 0)
            {
                GamesList.SelectedIndex = 0;
                HdrCheck.IsChecked = true;
                OnPreviewLaunch(this, new RoutedEventArgs());
                if (!Status.Text.Contains("开启 HDR", StringComparison.Ordinal))
                    throw new InvalidOperationException("Launch preview did not use the changed preference.");
            }
            await File.WriteAllTextAsync(Path.Combine(_snapshotDirectory!, "verification.txt"),
                $"PASS: three layouts, empty search, clear search, in-memory HDR launch preview. Catalog count: {_games.Count}. No game launched or configuration written.");
            Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(_snapshotDirectory!, "failure.txt"), ex.ToString());
            Application.Current.Shutdown(1);
        }
    }

    private void Capture(string name)
    {
        UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(_snapshotDirectory!, name));
        encoder.Save(file);
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));

    private static List<GameConfig> DemoGames() => new()
    {
        new() { DataKey = "crawlers", DisplayName = "吸血鬼爬行者", Executable = "VampireCrawlers.exe", SpeedMultiplier = 1.5 },
        new() { DataKey = "elden-ring", DisplayName = "艾尔登法环", Executable = "eldenring.exe", HdrEnabled = true },
        new() { DataKey = "cyberpunk", DisplayName = "赛博朋克 2077", Executable = "Cyberpunk2077.exe", HdrEnabled = true },
        new() { DataKey = "hades", DisplayName = "Hades II", Executable = "Hades2.exe" },
        new() { DataKey = "hollow-knight", DisplayName = "Hollow Knight", Executable = "hollow_knight.exe" },
        new() { DataKey = "balatro", DisplayName = "Balatro", Executable = "Balatro.exe", SpeedMultiplier = 2 },
        new() { DataKey = "long-title", DisplayName = "一个名称特别长的游戏：用于检查中文换行、搜索和小窗口下的阅读体验", Executable = "LongTitle.exe" },
        new() { DataKey = "disabled", DisplayName = "暂停自动化的游戏", Executable = "Example.exe", IsEnabled = false }
    };

    private sealed class GameEntry
    {
        public GameEntry(GameConfig game, int index)
        {
            Name = game.DisplayName ?? game.DataKey;
            Executable = game.Executable ?? string.Empty;
            Hdr = game.HdrEnabled;
            Speed = game.SpeedMultiplier;
            PreviewHdr = Hdr;
            PreviewSpeed = Speed is not null;
            Summary = !game.IsEnabled ? "自动化已暂停" : string.Join("  ·  ", new[] { Hdr ? "HDR" : null, Speed is double value ? $"{value:0.##}× 倍速" : null }.Where(value => value is not null));
            if (Summary.Length == 0) Summary = "标准启动";
            var palette = new[] { "#304E79", "#644C75", "#2F6964", "#79533E", "#475D8B", "#664A55" };
            Artwork = new LinearGradientBrush(Brush(palette[index % palette.Length]).Color, Brush("#1A2231").Color, 65);
        }

        public string Name { get; }
        public string Executable { get; }
        public string Initial => System.Globalization.StringInfo.GetNextTextElement(Name);
        public bool Hdr { get; }
        public double? Speed { get; }
        public string Summary { get; }
        public bool PreviewHdr { get; set; }
        public bool PreviewSpeed { get; set; }
        public Brush Artwork { get; }
    }
}
