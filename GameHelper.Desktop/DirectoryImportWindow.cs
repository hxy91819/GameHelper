using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using GameHelper.Core.Models;

namespace GameHelper.Desktop;

internal sealed class DirectoryImportWindow : Window
{
    private readonly List<Choice> _choices;
    public IReadOnlyList<LocalGameCandidate> SelectedGames => _choices.Where(item => item.Selected && !item.Candidate.AlreadyAdded).Select(item => item.Candidate).ToList();

    public DirectoryImportWindow(LocalGameScan scan)
    {
        Title = "从目录添加游戏";
        Width = 950; Height = 680; MinWidth = 700; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(20, 26, 24));
        Foreground = new SolidColorBrush(Color.FromRgb(240, 242, 235));
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 14;
        _choices = scan.Candidates.Select(item => new Choice(item)).ToList();
        var root = new DockPanel { Margin = new Thickness(24) };
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = $"发现 {scan.Candidates.Count} 个候选程序", FontSize = 25, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = scan.Root, Foreground = Brushes.Honeydew, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = scan.Root, Margin = new Thickness(0, 8, 0, 10) });
        header.Children.Add(new TextBlock { Text = "已预选每个目录的推荐程序，请确认游戏名称和路径。新增游戏的 HDR / 倍速默认关闭，已有条目保持原设置。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        if (scan.Truncated || scan.SkippedDirectories > 0)
            header.Children.Add(new TextBlock { Text = $"{(scan.Truncated ? "扫描已到达数量、深度或时间上限，请缩小目录重扫。" : "") } 跳过 {scan.SkippedDirectories} 个无法读取的目录。", Foreground = Brushes.Khaki, TextWrapping = TextWrapping.Wrap });
        var search = new TextBox { ToolTip = "按游戏名或路径筛选", Margin = new Thickness(0, 6, 0, 12) };
        header.Children.Add(new TextBlock { Text = "筛选名称或程序路径", FontSize = 12 }); header.Children.Add(search);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        var add = new Button { Content = "添加选中游戏", IsDefault = true };
        add.Click += (_, _) => { if (SelectedGames.Count > 0) DialogResult = true; };
        DockPanel.SetDock(add, Dock.Right); footer.Children.Add(add);
        var clear = new Button { Content = "全部取消", Margin = new Thickness(0, 0, 10, 0) };
        clear.Click += (_, _) => { foreach (var choice in _choices) choice.Selected = false; };
        DockPanel.SetDock(clear, Dock.Left); footer.Children.Add(clear);
        var recommended = new Button { Content = "只选推荐程序", HorizontalAlignment = HorizontalAlignment.Left };
        recommended.Click += (_, _) => { foreach (var choice in _choices) choice.Selected = choice.Candidate.Recommended && !choice.Candidate.AlreadyAdded; };
        footer.Children.Add(recommended);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var view = CollectionViewSource.GetDefaultView(_choices);
        view.Filter = value => ((Choice)value).Candidate.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase) || ((Choice)value).Candidate.ExecutablePath.Contains(search.Text, StringComparison.OrdinalIgnoreCase);
        search.TextChanged += (_, _) => view.Refresh();
        var grid = new DataGrid { ItemsSource = view, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
            Background = Brushes.Transparent, Foreground = Brushes.White, RowBackground = new SolidColorBrush(Color.FromRgb(25, 33, 30)),
            AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(29, 40, 33)), GridLinesVisibility = DataGridGridLinesVisibility.None,
            HeadersVisibility = DataGridHeadersVisibility.Column, RowHeight = 58, BorderThickness = new Thickness(0), SelectionMode = DataGridSelectionMode.Single };
        var headerStyle = new Style(typeof(DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(20, 26, 24))));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Honeydew));
        headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 12, 8, 12)));
        headerStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
        grid.ColumnHeaderStyle = headerStyle;
        var rowText = new Style(typeof(TextBlock));
        rowText.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
        rowText.Setters.Add(new Setter(MarginProperty, new Thickness(8, 0, 8, 0)));
        rowText.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        rowText.Setters.Add(new Setter(ToolTipProperty, new Binding("Candidate.Name")));
        var checkbox = new FrameworkElementFactory(typeof(CheckBox));
        checkbox.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(Choice.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        checkbox.SetBinding(IsEnabledProperty, new Binding(nameof(Choice.CanSelect)));
        checkbox.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        grid.Columns.Add(new DataGridTemplateColumn { Header = "选择", Width = 60, CellTemplate = new DataTemplate { VisualTree = checkbox } });
        grid.Columns.Add(new DataGridTextColumn { Header = "游戏", Binding = new Binding("Candidate.Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true, ElementStyle = rowText });
        grid.Columns.Add(new DataGridTextColumn { Header = "识别", Binding = new Binding(nameof(Choice.Note)), Width = 95, IsReadOnly = true, ElementStyle = rowText });
        var pathColumn = new DataGridTextColumn { Header = "程序路径", Binding = new Binding("Candidate.ExecutablePath"), Width = new DataGridLength(1.8, DataGridLengthUnitType.Star), IsReadOnly = true };
        var textStyle = new Style(typeof(TextBlock), rowText);
        textStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding("Candidate.ExecutablePath")));
        pathColumn.ElementStyle = textStyle; grid.Columns.Add(pathColumn);
        root.Children.Add(grid); Content = root;
    }

    private sealed class Choice(LocalGameCandidate candidate) : ObservableState
    {
        private bool _selected = candidate.Recommended && !candidate.AlreadyAdded;
        public LocalGameCandidate Candidate => candidate;
        public bool CanSelect => !candidate.AlreadyAdded;
        public string Note => candidate.AlreadyAdded ? "已在游戏库" : candidate.Recommended ? "推荐" : "备选程序";
        public bool Selected { get => _selected; set => Set(ref _selected, value && CanSelect); }
    }
}
