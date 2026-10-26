using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using GameHelper.Core.Models;

namespace GameHelper.Desktop;

internal sealed class DirectoryImportWindow : Window
{
    private readonly DirectoryImportSelection _selection;
    public IReadOnlyList<LocalGameCandidate> SelectedGames => _selection.SelectedGames;
    internal void ShowAuxiliaryCandidates() => _selection.ShowAuxiliary = true;

    public DirectoryImportWindow(LocalGameScan scan)
    {
        Title = "从目录添加游戏";
        Width = 1100; Height = 700; MinWidth = 800; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeBrushes.Get("Sidebar");
        Foreground = ThemeBrushes.Get("TextPrimary");
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 12;
        _selection = new DirectoryImportSelection(scan.Candidates);
        var view = CollectionViewSource.GetDefaultView(_selection.Choices);
        view.GroupDescriptions.Add(new PropertyGroupDescription("Candidate.Folder"));
        view.Filter = value => _selection.Matches((DirectoryGameChoice)value);
        var root = new DockPanel();
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = $"发现 {scan.Candidates.Select(item => item.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count()} 个目录 · {scan.Candidates.Count} 个候选程序", FontSize = 25, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = scan.Root, Foreground = ThemeBrushes.Get("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = scan.Root, Margin = new Thickness(0, 8, 0, 10) });
        header.Children.Add(new TextBlock { Text = "默认不勾选。每个游戏目录只选择一个程序；推荐仅供参考，请确认名称、依据和完整路径后添加。已有条目保持原设置。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        if (scan.Truncated || scan.SkippedDirectories > 0)
            header.Children.Add(new TextBlock { Text = $"{(scan.Truncated ? "扫描已到达数量、深度或时间上限，请缩小目录重扫。" : "") } 跳过 {scan.SkippedDirectories} 个无法读取的目录。", Foreground = ThemeBrushes.Get("Warning"), TextWrapping = TextWrapping.Wrap });
        var search = new TextBox { ToolTip = "按游戏名或路径筛选", Margin = new Thickness(0, 6, 0, 12) };
        header.Children.Add(new TextBlock { Text = "筛选名称或程序路径", FontSize = 12 }); header.Children.Add(search);
        var auxiliary = new CheckBox { Content = $"显示辅助程序（{scan.Candidates.Count(item => item.IsAuxiliary)}）", Margin = new Thickness(0, 0, 0, 10) };
        auxiliary.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(DirectoryImportSelection.ShowAuxiliary))
        { Source = _selection, Mode = BindingMode.TwoWay });
        header.Children.Add(auxiliary);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        var add = new Button { Content = "添加选中（0）", IsDefault = true, IsEnabled = false };
        add.Click += (_, _) => { if (SelectedGames.Count > 0) DialogResult = true; };
        DockPanel.SetDock(add, Dock.Right); footer.Children.Add(add);
        var clear = new Button { Content = "全部取消", Margin = new Thickness(0, 0, 10, 0) };
        clear.Click += (_, _) => _selection.Clear();
        DockPanel.SetDock(clear, Dock.Left); footer.Children.Add(clear);
        var recommended = new Button { Content = "只选推荐程序", HorizontalAlignment = HorizontalAlignment.Left };
        recommended.Click += (_, _) => _selection.SelectRecommended();
        footer.Children.Add(recommended);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        search.TextChanged += (_, _) => _selection.Search = search.Text;
        _selection.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(DirectoryImportSelection.Search) or nameof(DirectoryImportSelection.ShowAuxiliary)) view.Refresh();
            add.Content = $"添加选中（{_selection.SelectedCount}）";
            add.IsEnabled = _selection.SelectedCount > 0;
        };
        var grid = new DataGrid { ItemsSource = view, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
            Background = Brushes.Transparent, Foreground = ThemeBrushes.Get("TextPrimary"), RowBackground = ThemeBrushes.Get("Surface"),
            AlternatingRowBackground = ThemeBrushes.Get("TableAlternate"), GridLinesVisibility = DataGridGridLinesVisibility.None,
            HeadersVisibility = DataGridHeadersVisibility.Column, RowHeight = 58, BorderThickness = new Thickness(0), SelectionMode = DataGridSelectionMode.Single, CanUserSortColumns = false };
        VirtualizingPanel.SetIsVirtualizingWhenGrouping(grid, true);
        var groupBorder = new FrameworkElementFactory(typeof(Border));
        groupBorder.SetValue(Border.BackgroundProperty, ThemeBrushes.Get("Sidebar"));
        groupBorder.SetValue(Border.PaddingProperty, new Thickness(8, 12, 8, 8));
        var groupTitle = new FrameworkElementFactory(typeof(TextBlock));
        groupTitle.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        groupTitle.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        groupTitle.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        groupTitle.SetBinding(ToolTipProperty, new Binding("Name"));
        groupBorder.AppendChild(groupTitle);
        grid.GroupStyle.Add(new GroupStyle { HeaderTemplate = new DataTemplate { VisualTree = groupBorder } });
        var headerStyle = new Style(typeof(DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, ThemeBrushes.Get("Sidebar")));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, ThemeBrushes.Get("Muted")));
        headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 12, 8, 12)));
        headerStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
        grid.ColumnHeaderStyle = headerStyle;
        var rowText = new Style(typeof(TextBlock));
        rowText.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
        rowText.Setters.Add(new Setter(MarginProperty, new Thickness(8, 0, 8, 0)));
        rowText.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        rowText.Setters.Add(new Setter(ToolTipProperty, new Binding("Candidate.Name")));
        var checkbox = new FrameworkElementFactory(typeof(CheckBox));
        checkbox.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(DirectoryGameChoice.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        checkbox.SetBinding(IsEnabledProperty, new Binding(nameof(DirectoryGameChoice.CanSelect)));
        checkbox.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        grid.Columns.Add(new DataGridTemplateColumn { Header = "选择", Width = 60, CellTemplate = new DataTemplate { VisualTree = checkbox } });
        grid.Columns.Add(new DataGridTextColumn { Header = "游戏 / 程序", Binding = new Binding("Candidate.Name"), Width = 160, IsReadOnly = true, ElementStyle = rowText });
        grid.Columns.Add(new DataGridTextColumn { Header = "识别", Binding = new Binding(nameof(DirectoryGameChoice.Note)), Width = 80, IsReadOnly = true, ElementStyle = rowText });
        var evidenceText = new Style(typeof(TextBlock), rowText);
        evidenceText.Setters.Add(new Setter(TextBlock.FontSizeProperty, 11d));
        evidenceText.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        evidenceText.Setters.Add(new Setter(MaxHeightProperty, 48d));
        evidenceText.Setters.Add(new Setter(ToolTipProperty, new Binding("Candidate.Evidence")));
        grid.Columns.Add(new DataGridTextColumn { Header = "推荐 / 判断依据", Binding = new Binding("Candidate.Evidence"),
            Width = 230, IsReadOnly = true, ElementStyle = evidenceText });
        var pathColumn = new DataGridTextColumn { Header = "程序路径", Binding = new Binding("Candidate.ExecutablePath"), Width = new DataGridLength(1.8, DataGridLengthUnitType.Star), IsReadOnly = true };
        var textStyle = new Style(typeof(TextBlock), rowText);
        textStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding("Candidate.ExecutablePath")));
        pathColumn.ElementStyle = textStyle; grid.Columns.Add(pathColumn);
        root.Children.Add(grid); Content = new Border { Background = ThemeBrushes.Get("Sidebar"), Padding = new Thickness(24), Child = root };
    }

}
