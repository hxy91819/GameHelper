using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using GameHelper.Desktop.Input;

namespace GameHelper.Desktop;

internal sealed partial class ConsoleModeWindow
{
    // Console mode always uses a dark surface, even when the desktop library is light.
    internal static void ConfigureTheme(ResourceDictionary resources)
    {
        resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/GameHelper.Desktop;component/Themes/Dark.xaml", UriKind.Relative)));
        resources[typeof(Button)] = ConsoleModeVisuals.ButtonStyle();
        resources["TextPrimary"] = ConsoleModeVisuals.Text;
        resources["Muted"] = ConsoleModeVisuals.Secondary;
    }

    private readonly List<Button> tabButtons = [];
    private readonly TextBlock selectionSummary = ConsoleModeVisuals.Label("", 17, ConsoleModeVisuals.Secondary);
    private readonly TextBlock detailTitle = ConsoleModeVisuals.Label("", 34);
    private readonly TextBlock detailSummary = ConsoleModeVisuals.Label("", 18, ConsoleModeVisuals.Secondary);
    private readonly TextBlock detailController = ConsoleModeVisuals.Label("", 18);
    private readonly TextBlock detailSettings = ConsoleModeVisuals.Label("", 17, ConsoleModeVisuals.Secondary);
    private readonly TextBlock catalogState = ConsoleModeVisuals.Label("", 15, ConsoleModeVisuals.Secondary);
    private readonly TextBlock emptyState = ConsoleModeVisuals.Label("", 24);
    private readonly Image backdrop = new() { Stretch = Stretch.UniformToFill, Opacity = .13, IsHitTestVisible = false };
    private readonly Grid presentation = new();
    private readonly Grid contentBody = new();
    private readonly Grid detailGrid = new();
    private readonly StackPanel detailBody = new();

    private void BuildPresentation()
    {
        Background = ConsoleModeVisuals.Surface; Foreground = ConsoleModeVisuals.Text;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); UseLayoutRounding = true;
        var scene = new Grid { ClipToBounds = true }; Content = scene;
        backdrop.OpacityMask = new LinearGradientBrush(Colors.Transparent, Colors.Black, new Point(0, 1), new Point(1, 0));
        scene.Children.Add(backdrop);
        presentation.Margin = new Thickness(44, 26, 44, 20); scene.Children.Add(presentation);
        presentation.RowDefinitions.Add(new() { Height = GridLength.Auto });
        presentation.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        presentation.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel(); presentation.Children.Add(header);
        var brandRow = new Grid { Margin = new Thickness(0, 0, 0, 20) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal };
        var logoResources = (ResourceDictionary)Application.LoadComponent(new Uri("/GameHelper.Desktop;component/Assets/Brand.xaml", UriKind.Relative));
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new System.Windows.Rect(0, 0, 64, 64))));
        drawing.Children.Add(new GeometryDrawing(ConsoleModeVisuals.Text, null, (Geometry)logoResources["BrandGlyph"]));
        drawing.Children.Add(new GeometryDrawing(ConsoleModeVisuals.Accent, null, (Geometry)logoResources["BrandPlay"]));
        brand.Children.Add(new Image { Source = new DrawingImage(drawing), Width = 31, Height = 31, Margin = new Thickness(0, 0, 10, 0) });
        brand.Children.Add(new TextBlock { Text = "GAMEHELPER", FontSize = 17, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        brand.Children.Add(new TextBlock { Text = "  /  主机模式", FontSize = 15, Foreground = ConsoleModeVisuals.Secondary, VerticalAlignment = VerticalAlignment.Center });
        brandRow.Children.Add(brand);
        catalogState.HorizontalAlignment = HorizontalAlignment.Right; catalogState.VerticalAlignment = VerticalAlignment.Center; brandRow.Children.Add(catalogState);
        header.Children.Add(brandRow);
        var titles = new Grid();
        heading.FontSize = 34; heading.FontWeight = FontWeights.SemiBold; heading.TextTrimming = TextTrimming.CharacterEllipsis;
        titles.Children.Add(heading); header.Children.Add(titles);
        selectionSummary.Margin = new Thickness(0, 6, 0, 16); selectionSummary.TextWrapping = TextWrapping.NoWrap; selectionSummary.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(selectionSummary);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) }; header.Children.Add(tabs);
        string[] names = ["全部游戏", "最近游玩", "未通关", "支持手柄"];
        for (int i = 0; i < names.Length; i++)
        {
            int index = i;
            var button = new Button { Content = names[i], FontSize = 16, Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 0, 8, 0) };
            button.Click += (_, _) => { tab = index; Reload(); }; tabButtons.Add(button); tabs.Children.Add(button);
        }
        var search = new Button { Content = "搜索  /  屏幕键盘", FontSize = 16, Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(12, 0, 0, 0), Background = Brushes.Transparent };
        search.Click += (_, _) => OpenKeyboard(false); tabs.Children.Add(search);
        Grid.SetRow(contentBody, 1); presentation.Children.Add(contentBody);
        contentBody.Children.Add(shelf); contentBody.Children.Add(details);
        emptyState.HorizontalAlignment = HorizontalAlignment.Center; emptyState.VerticalAlignment = VerticalAlignment.Center; emptyState.TextAlignment = TextAlignment.Center; contentBody.Children.Add(emptyState);
        ConfigureShelf(shelf); shelf.SelectionChanged += (_, _) => UpdatePresentation();
        shelf.MouseDoubleClick += (_, _) => Handle(ControllerAction.Confirm);
        details.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        details.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var detailScroll = new ScrollViewer { Content = detailGrid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        details.Children.Add(detailScroll);
        detailGrid.ColumnDefinitions.Add(new() { Width = new GridLength(244) });
        detailGrid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        detailPoster.Width = 220; detailPoster.Height = 330; detailPoster.VerticalAlignment = VerticalAlignment.Top;
        var posterFrame = ConsoleModeVisuals.PanelFrame(detailPoster, 10); posterFrame.HorizontalAlignment = HorizontalAlignment.Left; posterFrame.VerticalAlignment = VerticalAlignment.Top; detailGrid.Children.Add(posterFrame);
        Grid.SetColumn(detailBody, 1); detailBody.Margin = new Thickness(28, 0, 0, 12); detailGrid.Children.Add(detailBody);
        detailTitle.FontWeight = FontWeights.SemiBold; detailBody.Children.Add(detailTitle);
        detailSummary.Margin = new Thickness(0, 10, 0, 18); detailBody.Children.Add(detailSummary);
        var controllerPanel = new StackPanel(); controllerPanel.Children.Add(ConsoleModeVisuals.Label("手柄体验", 13, ConsoleModeVisuals.Accent));
        detailController.Margin = new Thickness(0, 8, 0, 0); controllerPanel.Children.Add(detailController);
        detailBody.Children.Add(ConsoleModeVisuals.PanelFrame(controllerPanel, 16));
        var settingsPanel = new StackPanel(); settingsPanel.Children.Add(ConsoleModeVisuals.Label("启动配置", 13, ConsoleModeVisuals.Accent));
        detailSettings.Margin = new Thickness(0, 8, 0, 0); settingsPanel.Children.Add(detailSettings);
        var settingsFrame = ConsoleModeVisuals.PanelFrame(settingsPanel, 16); settingsFrame.Margin = new Thickness(0, 12, 0, 0); detailBody.Children.Add(settingsFrame);
        info.FontSize = 16; info.Foreground = ConsoleModeVisuals.Secondary; info.Margin = new Thickness(0, 16, 0, 14); detailBody.Children.Add(info);
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) }; Grid.SetRow(actions, 1); details.Children.Add(actions);
        detailLaunchButton.FontSize = 19; detailLaunchButton.FontWeight = FontWeights.SemiBold; detailLaunchButton.Background = ConsoleModeVisuals.Accent; detailLaunchButton.Foreground = ConsoleModeVisuals.Surface;
        detailLaunchButton.Margin = new Thickness(0, 0, 10, 10); detailLaunchButton.Click += (_, _) => Handle(ControllerAction.Confirm); actions.Children.Add(detailLaunchButton);
        var settingsButton = new Button { Content = "游戏设置  □ / X", Margin = new Thickness(0, 0, 10, 10) };
        settingsButton.Click += (_, _) => Handle(ControllerAction.Auxiliary); actions.Children.Add(settingsButton);
        stopButton.FontSize = 17; stopButton.Margin = new Thickness(0, 0, 10, 10); stopButton.Click += (_, _) => ConfirmStop(); actions.Children.Add(stopButton);
        var back = new Button { Content = "返回", Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 10) }; back.Click += (_, _) => Handle(ControllerAction.Back); actions.Children.Add(back);
        var footer = new Border { BorderBrush = ConsoleModeVisuals.Stroke, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0), Margin = new Thickness(0, 12, 0, 0) };
        var footerBody = new StackPanel(); footer.Child = footerBody; status.FontSize = 13; status.Foreground = ConsoleModeVisuals.Secondary;
        help.FontSize = 15; help.Foreground = ConsoleModeVisuals.Text; help.Margin = new Thickness(0, 5, 0, 0); footerBody.Children.Add(status); footerBody.Children.Add(help);
        Grid.SetRow(footer, 2); presentation.Children.Add(footer);
        scene.Children.Add(launchOverlay); scene.Children.Add(actionOverlay);
        launchOverlay.Background = ConsoleModeVisuals.Brush("#F5101116");
        SizeChanged += (_, _) => UpdatePresentationGeometry();
        contentBody.SizeChanged += (_, _) => UpdatePresentationGeometry();
        shelf.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) => { if (e.ViewportHeightChange != 0) UpdatePresentationGeometry(); }));
    }

    private void UpdatePresentationGeometry()
    {
        if (presentation.Children.Count == 0) return;
        var compact = ActualWidth < 1100 || ActualHeight < 760;
        presentation.Margin = compact ? new Thickness(28, 18, 28, 14) : new Thickness(44, 26, 44, 20);
        heading.FontSize = compact ? 28 : 36;
        help.FontSize = compact ? 15 : 18;
        var shelfViewport = FindVisual<ScrollContentPresenter>(shelf)?.ActualHeight;
        var posterHeight = ShelfPosterHeight(shelfViewport is > 0 ? shelfViewport.Value : contentBody.ActualHeight - 30);
        if (!Equals(shelf.Tag, posterHeight)) shelf.Tag = posterHeight;
        detailGrid.ColumnDefinitions[0].Width = new GridLength(compact ? 204 : 244);
        detailPoster.Width = compact ? 180 : 220; detailPoster.Height = compact ? 270 : 330;
        detailBody.Margin = new Thickness(compact ? 18 : 28, 0, 0, 12); detailTitle.FontSize = compact ? 27 : 34;
    }

    private void UpdatePresentation()
    {
        catalogState.Text = $"●  {(model.Connected ? "已连接" : "正在连接")}  ·  {shelf.Items.Count} 款游戏";
        heading.Text = detail ? "游戏详情" : Selected?.Name ?? "今晚，继续冒险。";
        selectionSummary.Text = Selected is { } game ? $"{game.TotalHours}  ·  {game.Controller.Label}  ·  {game.LastPlayedLabel}" : query.Length > 0 ? $"没有找到“{query}” · 试试其他关键词" : "让每一段冒险，都值得珍藏。";
        var artwork = Selected?.BannerArtwork ?? Selected?.DetailArtwork;
        if (!ReferenceEquals(backdrop.Source, artwork)) backdrop.Source = artwork;
        heading.ToolTip = Selected?.Name;
        selectionSummary.ToolTip = selectionSummary.Text;
        for (var i = 0; i < tabButtons.Count; i++)
        {
            tabButtons[i].Background = i == tab ? ConsoleModeVisuals.Brush("#30273F") : Brushes.Transparent;
            tabButtons[i].BorderBrush = i == tab ? ConsoleModeVisuals.Accent : ConsoleModeVisuals.Stroke;
            tabButtons[i].Foreground = i == tab ? ConsoleModeVisuals.Text : ConsoleModeVisuals.Secondary;
        }
        emptyState.Visibility = !detail && shelf.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        emptyState.Text = query.Length > 0 ? "没有找到匹配的游戏\n按搜索键修改关键词"
            : !model.ConsoleGames.Any() ? "游戏库还没有内容\n返回桌面游戏库，添加你的第一款游戏"
            : !model.ConsoleShelfGames.Any() ? "本地未找到可显示的游戏\n返回桌面游戏库，检查安装位置"
            : "这个分类暂时没有游戏\n切换分类试试";
        UpdatePresentationGeometry();
    }

    internal static double ShelfPosterHeight(double viewportHeight) => Math.Clamp(viewportHeight - 120, 130, 310);

    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindVisual<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private sealed class PosterWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is double height ? height * 2 / 3 : 220d;
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
    }

    internal static void ConfigureShelf(ListBox shelf)
    {
        shelf.Tag = 310d;
        shelf.Padding = new Thickness(0, 8, 0, 0);
        shelf.Background = Brushes.Transparent; shelf.BorderThickness = new Thickness(0); shelf.Foreground = Brushes.White;
        var chrome = new FrameworkElementFactory(typeof(Border));
        chrome.Name = "CardChrome";
        chrome.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        chrome.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        chrome.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        chrome.AppendChild(presenter);
        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = chrome };
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(34, 35, 48)), "CardChrome"));
        selected.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(188, 163, 229)), "CardChrome"));
        selected.Setters.Add(new Setter(UIElement.RenderTransformProperty, new TranslateTransform(0, -4), "CardChrome"));
        var shadow = new DropShadowEffect { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 4, Opacity = .35 }; shadow.Freeze();
        selected.Setters.Add(new Setter(UIElement.EffectProperty, shadow, "CardChrome"));
        template.Triggers.Add(selected);
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        itemStyle.Setters.Add(new Setter(Control.TemplateProperty, template));
        shelf.ItemContainerStyle = itemStyle;
        shelf.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        shelf.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        var panel = new FrameworkElementFactory(typeof(StackPanel)); panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal); shelf.ItemsPanel = new ItemsPanelTemplate(panel);
        var card = new FrameworkElementFactory(typeof(StackPanel)); card.SetValue(FrameworkElement.WidthProperty, 220d); card.SetValue(FrameworkElement.MarginProperty, new Thickness(10));
        var image = new FrameworkElementFactory(typeof(GamePosterTile)); image.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); image.SetBinding(FrameworkElement.WidthProperty, new Binding("Tag") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBox), 1), Converter = new PosterWidthConverter(), FallbackValue = 206.6666666667d }); image.SetBinding(FrameworkElement.HeightProperty, new Binding("Tag") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBox), 1), FallbackValue = 310d }); image.SetBinding(GamePosterTile.ArtworkProperty, new Binding("CoverArtwork")); image.SetBinding(GamePosterTile.LabelProperty, new Binding("Initial")); card.AppendChild(image);
        foreach (var prop in new[] { "Name", "TotalHours", "Controller.Label" }) { var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); text.SetValue(TextBlock.FontSizeProperty, prop == "Name" ? 18d : 15d); text.SetValue(FrameworkElement.MarginProperty, new Thickness(0, prop == "Name" ? 10 : 4, 0, 0)); text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); text.SetValue(FrameworkElement.HeightProperty, prop == "Name" ? 25d : 21d); text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap); text.SetBinding(TextBlock.TextProperty, new Binding(prop));
            if (prop == "Name") text.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Name"));
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, prop == "Name" ? ConsoleModeVisuals.Text : ConsoleModeVisuals.Secondary));
            if (prop == "Controller.Label")
            {
                var red = new DataTrigger { Binding = new Binding("Controller.Unsupported"), Value = true };
                red.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.Salmon)); style.Triggers.Add(red);
            }
            text.SetValue(FrameworkElement.StyleProperty, style);
            card.AppendChild(text); }
        shelf.ItemTemplate = new DataTemplate { VisualTree = card };
    }
}
