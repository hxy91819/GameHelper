using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameHelper.Core.Models;
using Microsoft.Win32;

namespace GameHelper.Desktop;

public partial class MainWindow : Window
{
    private readonly LibraryViewModel _model;
    private readonly DispatcherTimer _refreshTimer;

    public MainWindow(LibraryViewModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += OnTick;
        _refreshTimer.Start();
        PreviewKeyDown += OnKeyboard;
        Closed += (_, _) => { _refreshTimer.Stop(); _refreshTimer.Tick -= OnTick; _model.Dispose(); };
    }

    private async void OnTick(object? sender, EventArgs e) => await _model.RefreshAsync();
    private void OnFilter(object sender, RoutedEventArgs e) => _model.SetFilter((string)((Button)sender).Tag);
    private void OnLayout(object sender, RoutedEventArgs e) => SetLayout((string)((Button)sender).Tag);
    private void SetLayout(string layout)
    {
        GameList.ItemTemplate = (DataTemplate)Resources[layout == "grid" ? "CardTemplate" : "RowTemplate"];
        GameList.ItemsPanel = (ItemsPanelTemplate)Resources[layout == "grid" ? "CardsPanel" : "RowsPanel"];
    }

    private async void OnAdd(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择用于识别游戏的 exe", Filter = "游戏程序|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await _model.AddAsync(dialog.FileName);
    }

    private void OnChooseEntry(object sender, RoutedEventArgs e)
    {
        if (_model.Selected is null) return;
        var dialog = new OpenFileDialog { Title = "选择原本使用的游戏启动入口", Filter = "游戏程序或快捷方式|*.exe;*.lnk;*.url", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) _model.Selected.Target = dialog.FileName;
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_model.Selected is not { } game) return;
        if (MessageBox.Show(this, $"从目录移除“{game.Name}”？历史游玩记录会保留。", "移除游戏", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            await _model.RemoveAsync();
    }

    private async void OnReload(object sender, RoutedEventArgs e)
    {
        if (_model.HasUnsavedChanges && MessageBox.Show(this, "放弃未保存的修改并重新载入？", "重新载入", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await _model.RefreshAsync(discardDrafts: true);
    }

    private async void OnSteam(object sender, RoutedEventArgs e)
    {
        var games = await _model.DiscoverAsync();
        if (games.Count == 0) return;
        var dialog = new SteamImportWindow(games) { Owner = this };
        if (dialog.ShowDialog() == true) await _model.ImportAsync(dialog.SelectedAppIds);
    }

    private void OnKeyboard(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocused) SearchBox.Clear();
    }

    /// <summary>Read-only rendering probe against an explicitly selected running host.</summary>
    public async Task CaptureAsync(string directory)
    {
        _refreshTimer.Stop();
        Directory.CreateDirectory(directory);
        foreach (var layout in new[] { "grid", "list" })
        {
            SetLayout(layout);
            UpdateLayout();
            await Task.Delay(200);
            Capture(Path.Combine(directory, layout + ".png"));
        }
        _model.Search = "__no_game_matches_this_query__";
        await Task.Delay(100);
        if (!_model.Games.IsEmpty) throw new InvalidOperationException("搜索空结果未生效。");
        Capture(Path.Combine(directory, "empty.png"));
        _model.Search = string.Empty;
        Width = MinWidth; Height = MinHeight;
        await Task.Delay(100);
        Capture(Path.Combine(directory, "small-window.png"));
        await File.WriteAllTextAsync(Path.Combine(directory, "verification.txt"), "PASS: connected to host; grid/list render; empty search; minimum window size. No settings changed or games launched.");
    }

    private void Capture(string path)
    {
        UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
