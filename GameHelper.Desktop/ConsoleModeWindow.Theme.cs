using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace GameHelper.Desktop;

internal sealed partial class ConsoleModeWindow
{
    // Console mode always uses a dark surface, even when the desktop library is light.
    internal static void ConfigureTheme(ResourceDictionary resources) => resources.MergedDictionaries.Add(
        (ResourceDictionary)Application.LoadComponent(new Uri("/GameHelper.Desktop;component/Themes/Dark.xaml", UriKind.Relative)));

    internal static void ConfigureShelf(ListBox shelf)
    {
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
        selected.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(50, 41, 63)), "CardChrome"));
        selected.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(212, 189, 240)), "CardChrome"));
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
        var image = new FrameworkElementFactory(typeof(GamePosterTile)); image.SetValue(FrameworkElement.HeightProperty, 310d); image.SetBinding(GamePosterTile.ArtworkProperty, new Binding("CoverArtwork")); image.SetBinding(GamePosterTile.LabelProperty, new Binding("Initial")); card.AppendChild(image);
        foreach (var prop in new[] { "Name", "TotalHours", "Controller.Label" }) { var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); text.SetValue(TextBlock.FontSizeProperty, 19d); text.SetBinding(TextBlock.TextProperty, new Binding(prop));
            if (prop == "Controller.Label")
            {
                var style = new Style(typeof(TextBlock));
                var red = new DataTrigger { Binding = new Binding("Controller.Unsupported"), Value = true };
                red.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.Salmon)); style.Triggers.Add(red);
                text.SetValue(FrameworkElement.StyleProperty, style);
            }
            card.AppendChild(text); }
        shelf.ItemTemplate = new DataTemplate { VisualTree = card };
    }
}
