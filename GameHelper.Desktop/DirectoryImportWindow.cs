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

    public DirectoryImportWindow(LocalGameScan scan, IEnumerable<string>? ignoredPaths = null, Action<IReadOnlyList<string>, bool>? ignoredChanged = null)
    {
        Title = "从目录添加游戏";
        Width = 1100; Height = 700; MinWidth = 800; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeBrushes.Get("Sidebar");
        Foreground = ThemeBrushes.Get("TextPrimary");
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 12;
        _selection = new DirectoryImportSelection(scan.Candidates, ignoredPaths);
        if (ignoredChanged is not null) _selection.IgnoredChanged += ignoredChanged;
        var view = CollectionViewSource.GetDefaultView(_selection.Choices);
        view.GroupDescriptions.Add(new PropertyGroupDescription("Candidate.Folder"));
        view.Filter = value => _selection.Matches((DirectoryGameChoice)value);
        var root = new DockPanel();
        var header = new StackPanel();
        var title = new TextBlock { FontSize = 25, FontWeight = FontWeights.SemiBold };
        header.Children.Add(title);
        header.Children.Add(new TextBlock { Text = scan.Root, Foreground = ThemeBrushes.Get("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = scan.Root, Margin = new Thickness(0, 8, 0, 10) });
        header.Children.Add(new TextBlock { Text = "默认不勾选。每个游戏目录只选择一个程序；推荐仅供参考，请确认名称、依据和完整路径后添加。已在游戏库和已忽略的目录默认收起，不是游戏的目录可点右侧“忽略此目录”。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        if (scan.Truncated || scan.SkippedDirectories > 0)
            header.Children.Add(new TextBlock { Text = $"{(scan.Truncated ? "扫描已到达数量、深度或时间上限，请缩小目录重扫。" : "") } 跳过 {scan.SkippedDirectories} 个无法读取的目录。", Foreground = ThemeBrushes.Get("Warning"), TextWrapping = TextWrapping.Wrap });
        var search = new TextBox { ToolTip = "按游戏名或路径筛选", Margin = new Thickness(0, 6, 0, 12) };
        header.Children.Add(new TextBlock { Text = "筛选名称或程序路径", FontSize = 12 }); header.Children.Add(search);
        var auxiliary = new CheckBox { Content = $"显示辅助程序（{scan.Candidates.Count(item => item.IsAuxiliary)}）", Margin = new Thickness(0, 0, 0, 10) };
        auxiliary.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(DirectoryImportSelection.ShowAuxiliary))
        { Source = _selection, Mode = BindingMode.TwoWay });
        header.Children.Add(auxiliary);
        var existing = new CheckBox { Margin = new Thickness(0, 0, 0, 10) };
        existing.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(DirectoryImportSelection.ShowExisting)) { Source = _selection, Mode = BindingMode.TwoWay });
        header.Children.Add(existing);
        var ignored = new CheckBox { Margin = new Thickness(0, 0, 0, 10) };
        ignored.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(DirectoryImportSelection.ShowIgnored)) { Source = _selection, Mode = BindingMode.TwoWay });
        header.Children.Add(ignored);
        void UpdateCounts()
        {
            title.Text = _selection.NewFolderCount == 0 ? "没有发现新游戏目录" : $"发现 {_selection.NewFolderCount} 个新游戏目录";
            existing.Content = $"显示已在游戏库的目录（{_selection.ExistingFolderCount}）";
            ignored.Content = $"显示已忽略的目录（{_selection.IgnoredFolderCount}）";
        }
        UpdateCounts();
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
            if (args.PropertyName is nameof(DirectoryImportSelection.Search) or nameof(DirectoryImportSelection.ShowAuxiliary)
                or nameof(DirectoryImportSelection.ShowExisting) or nameof(DirectoryImportSelection.ShowIgnored)
                or nameof(DirectoryImportSelection.IgnoredFolderCount)) view.Refresh();
            UpdateCounts();
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
        var groupRow = new FrameworkElementFactory(typeof(DockPanel));
        var ignore = new FrameworkElementFactory(typeof(Button));
        ignore.SetValue(DockPanel.DockProperty, Dock.Right);
        ignore.SetValue(Button.PaddingProperty, new Thickness(10, 2, 10, 2));
        ignore.SetBinding(ContentControl.ContentProperty, new Binding("Items[0]." + nameof(DirectoryGameChoice.IgnoreLabel)));
        ignore.SetBinding(VisibilityProperty, new Binding("Items[0]." + nameof(DirectoryGameChoice.CanIgnore)) { Converter = new BooleanToVisibilityConverter() });
        ignore.SetValue(ToolTipProperty, "忽略后，重新扫描时这个目录默认不再出现；可勾选“显示已忽略的目录”恢复。");
        ignore.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((sender, _) =>
        {
            if ((sender as FrameworkElement)?.DataContext is CollectionViewGroup { Items: [DirectoryGameChoice choice, ..] })
                _selection.SetFolderIgnored(choice.Candidate.Folder, !choice.FolderIgnored);
        }));
        groupRow.AppendChild(ignore);
        groupRow.AppendChild(groupTitle);
        groupBorder.AppendChild(groupRow);
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
